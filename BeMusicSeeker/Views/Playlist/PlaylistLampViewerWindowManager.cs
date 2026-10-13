using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.Views;

/// <summary>
/// 一つのメイン画面のモードレスランプビューアを所有します。初回確定結果まで表示せず、
/// 各窓の全session仕事とsource解放を独立した一つの回収Taskへ合流させます。
/// </summary>
internal sealed class PlaylistLampViewerWindowManager : IDisposable, IAsyncDisposable
{
    private sealed class WindowLifetime
    {
        internal WindowLifetime(
            PlaylistLampViewerWindow window,
            IPlaylistLampViewerDataSource source,
            PlaylistLampViewerViewModel viewModel,
            CancellationToken inheritedCancellation)
        {
            Window = window;
            Source = source;
            ViewModel = viewModel;
            OpenCancellation = new CancellationTokenSource();
            InheritedCancellationRegistration = inheritedCancellation.Register(() => RequestCancellation());
        }

        internal PlaylistLampViewerWindow Window { get; }

        internal IPlaylistLampViewerDataSource Source { get; }

        internal PlaylistLampViewerViewModel ViewModel { get; }

        internal CancellationTokenSource OpenCancellation { get; }

        internal CancellationTokenRegistration InheritedCancellationRegistration { get; }

        private readonly object cancellationGate = new();

        // 親tokenから直接CTSを同期取消すると、別Cancelとの競合でcallback終端を見失います。
        internal Task RequestCancellation()
        {
            TaskCompletionSource completion;
            lock (cancellationGate)
            {
                if (CancellationTask != null) { return CancellationTask; }
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationTask = completion.Task;
            }
            _ = CancelCoreAsync(completion);
            return completion.Task;
        }

        private async Task CancelCoreAsync(TaskCompletionSource completion)
        {
            try
            {
                await Task.Run(() => OpenCancellation.Cancel()).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }

        internal bool IsShown;

        internal int FailureHandled;

        internal int Cleaned;

        internal int OpenCompleted;

        internal int OpenCancellationDisposed;

        internal Task CleanupTask;

        internal Task CancellationTask;

        // 解放を試みた後も確認できない実結果です。通知例外や通常openの失敗は保持しません。
        internal Exception CleanupFailure;
    }

    private readonly MainWindow owner;

    private readonly PlaylistWorkspaceViewModel workspace;

    private readonly IUiDialogService dialogs;

    private readonly Func<PlaylistLampViewerOpenContext, IPlaylistLampViewerDataSource> dataSourceFactory;

    private readonly Func<PlaylistLampHistoricalScoreSourceContext> historicalSourceContextFactory;

    private readonly Action<PlaylistLampViewerWindow> prepareWindowForShow;

    private readonly Action<PlaylistLampViewerWindow> activateWindowForInitialPresentation;

    private readonly HashSet<WindowLifetime> lifetimes = [];

    private readonly CancellationTokenSource shutdownCancellation = new();

    private readonly HashSet<Task> activeOperations = [];

    private int disposed;

    // window lifetimeをまだ作れない初期化とmanager自身の取消資源の未確認解放だけを所有します。
    private Exception managerCleanupFailure;

    /// <summary>Creates the manager for one main-window owner.</summary>
    /// <param name="owner">Main-window shell that owns every child viewer.</param>
    /// <param name="workspace">Playlist owner used for open and navigation intents.</param>
    /// <param name="dialogs">Owner-scoped dialog service used for failed aggregation notifications.</param>
    /// <param name="dataSourceFactory">
    /// Optional owner-scoped factory for the immutable viewer source. Production composition
    /// uses the library-backed source; alternate hosts may provide another implementation of
    /// the same narrow source contract without changing window lifecycle ownership.
    /// </param>
    /// <param name="prepareWindowForShow">
    /// Optional owner-scoped presentation preparation invoked after a fresh viewer is created
    /// and before it is shown. Production leaves this unset; deterministic hosts may use it to
    /// attach their native-window observation policy without changing the manager lifecycle.
    /// </param>
    /// <param name="activateWindowForInitialPresentation">
    /// Optional owner-scoped activation attempt invoked once after the viewer is shown and before
    /// its temporary WPF owner is released. Production uses the viewer's regular activation
    /// behavior; deterministic hosts may observe this boundary without activating a native window.
    /// </param>
    internal PlaylistLampViewerWindowManager(
        MainWindow owner,
        PlaylistWorkspaceViewModel workspace,
        IUiDialogService dialogs = null,
        Func<PlaylistLampViewerOpenContext, IPlaylistLampViewerDataSource> dataSourceFactory = null,
        Action<PlaylistLampViewerWindow> prepareWindowForShow = null,
        Action<PlaylistLampViewerWindow> activateWindowForInitialPresentation = null,
        Func<PlaylistLampHistoricalScoreSourceContext> historicalSourceContextFactory = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.dialogs = dialogs ?? new UiDialogCoordinator();
        this.historicalSourceContextFactory = historicalSourceContextFactory;
        this.dataSourceFactory = dataSourceFactory
            ?? (context => new BmsLibraryPlaylistLampDataSource(
                context.Playlist,
                context.Library,
                this.historicalSourceContextFactory));
        this.prepareWindowForShow = prepareWindowForShow;
        this.activateWindowForInitialPresentation = activateWindowForInitialPresentation
            ?? ActivateWindowForInitialPresentation;
    }

    /// <summary>Gets the number of currently shown child viewer windows.</summary>
    internal int Count => lifetimes.Count(lifetime => lifetime.IsShown && Volatile.Read(ref lifetime.Cleaned) == 0);

    /// <summary>Gets a stable snapshot of currently shown child viewer windows.</summary>
    internal IReadOnlyList<PlaylistLampViewerWindow> Windows => lifetimes
        .Where(lifetime => lifetime.IsShown && Volatile.Read(ref lifetime.Cleaned) == 0)
        .Select(lifetime => lifetime.Window)
        .ToArray();

    /// <summary>
    /// Opens a fresh modeless viewer for a table captured by a playlist-tree context menu.
    /// </summary>
    /// <param name="table">Table captured from the context menu.</param>
    /// <returns>A task yielding the shown child window, or null for a stale/failed open.</returns>
    internal Task<PlaylistLampViewerWindow> TryOpenAsync(
        BMSTable table,
        CancellationToken cancellationToken = default)
        => TrackOpenAsync(workspace.CapturePlaylistLampViewerOpenContext(table), cancellationToken);

    /// <summary>
    /// Opens a fresh modeless viewer for the single playlist-summary row captured by a context menu.
    /// </summary>
    /// <param name="row">Summary row captured from the context menu.</param>
    /// <returns>A task yielding the shown child window, or null for a stale/failed open.</returns>
    internal Task<PlaylistLampViewerWindow> TryOpenAsync(
        PlaylistSummaryRow row,
        CancellationToken cancellationToken = default)
        => TrackOpenAsync(workspace.CapturePlaylistLampViewerOpenContext(row), cancellationToken);

    /// <summary>
    /// Opens a viewer from an already resolved owner context.
    /// This keeps the typed owner boundary reusable by menu and composition integrations
    /// while the default source factory remains the production library-backed source.
    /// </summary>
    /// <param name="context">Stable playlist/library context captured by the workspace owner.</param>
    /// <param name="cancellationToken">Cancellation requested by the invoking owner.</param>
    /// <returns>A task yielding the shown child window, or null for a stale/failed open.</returns>
    internal Task<PlaylistLampViewerWindow> TryOpenAsync(
        PlaylistLampViewerOpenContext context,
        CancellationToken cancellationToken = default)
        => TrackOpenAsync(context, cancellationToken);

    /// <summary>
    /// Compatibility fire-and-forget entry for synchronous menu callers.
    /// The actual presentation remains gated by <see cref="TryOpenAsync(BMSTable, CancellationToken)"/>.
    /// </summary>
    /// <param name="table">Table captured from the context menu.</param>
    /// <returns>Always null because the window is created after the asynchronous first-result gate.</returns>
    internal PlaylistLampViewerWindow TryOpen(BMSTable table)
    {
        _ = OpenAndNotifyAsync(() => TryOpenAsync(table));
        return null;
    }

    /// <summary>Compatibility fire-and-forget entry for synchronous summary-menu callers.</summary>
    /// <param name="row">Summary row captured from the context menu.</param>
    /// <returns>Always null because the window is created after the asynchronous first-result gate.</returns>
    internal PlaylistLampViewerWindow TryOpen(PlaylistSummaryRow row)
    {
        _ = OpenAndNotifyAsync(() => TryOpenAsync(row));
        return null;
    }

    /// <summary>各窓の一つの回収Taskへ合流し、native閉鎖を先に要求します。実終端は非同期で待ちます。</summary>
    internal void CloseAll()
    {
        var failures = new List<Exception>();
        foreach (WindowLifetime lifetime in lifetimes.ToArray())
        {
            try { Close(lifetime); }
            catch (Exception exception) { failures.Add(exception); }
        }
        ThrowCleanupFailures(failures);
    }

    /// <summary>新規受付を止めて所有窓へ終了を要求します。非同期処理の終端はDisposeAsyncまたはWaitForCompletionAsyncで待ちます。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        // shutdown由来のlinked取消も同期callbackを持ち得るため、実Taskを先に所有します。
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperations.Add(completion.Task);
        try { CloseAll(); }
        catch (Exception exception) { RetainManagerCleanupFailure(exception); }
        finally { _ = CancelShutdownAsync(completion); }
    }

    private async Task CancelShutdownAsync(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { await Task.Run(() => shutdownCancellation.Cancel()).ConfigureAwait(true); }
        catch (Exception exception) { failures.Add(exception); }
        try { shutdownCancellation.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count > 0) { RetainManagerCleanupFailure(CreateCleanupFailure(failures)); }
        completion.TrySetResult();
        activeOperations.Remove(completion.Task);
    }

    /// <summary>実処理・回収・通知を待ち、未確認の実解放は重複・後続呼出しにも失敗として返します。解放を再試行しません。</summary>
    public async ValueTask DisposeAsync()
    {
        Exception failure = null;
        try { Dispose(); }
        catch (Exception exception) { failure = exception; }
        try { await WaitForCompletionAsync().ConfigureAwait(true); }
        catch (Exception exception)
        {
            if (failure != null) { throw new AggregateException(failure, exception); }
            throw;
        }
        if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    /// <summary>全実Taskを待った後、未確認の実解放結果を返します。重複・後続の待機でも故障を失わず、解放を再試行しません。</summary>
    internal async Task WaitForCompletionAsync()
    {
        // open/通知の完了過程でも子cleanupが生まれるため、単一snapshotで終端にしません。
        while (activeOperations.Count > 0)
        {
            await Task.WhenAll(activeOperations.ToArray()).ConfigureAwait(true);
        }
        var failures = new List<Exception>();
        if (managerCleanupFailure != null) { failures.Add(managerCleanupFailure); }
        foreach (WindowLifetime lifetime in lifetimes)
        {
            if (lifetime.CleanupFailure != null) { failures.Add(lifetime.CleanupFailure); }
        }
        ThrowCleanupFailures(failures);
    }

    private async Task<PlaylistLampViewerWindow> TrackOpenAsync(
        PlaylistLampViewerOpenContext context,
        CancellationToken cancellationToken)
    {
        // openの普通の失敗は呼出元が所有します。終了待機は実処理の完了と解放結果を区別します。
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperations.Add(completion.Task);
        try { return await OpenAsync(context, cancellationToken).ConfigureAwait(true); }
        finally
        {
            completion.TrySetResult();
            activeOperations.Remove(completion.Task);
        }
    }

    private async Task OpenAndNotifyAsync(Func<Task<PlaylistLampViewerWindow>> open)
    {
        // 互換入口も実openだけでなく、その失敗終端の通知までowner終了が待ちます。
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperations.Add(completion.Task);
        try
        {
            try { await open().ConfigureAwait(true); }
            catch (Exception exception) { await ShowOperationFailureAsync(exception).ConfigureAwait(true); }
        }
        finally
        {
            completion.TrySetResult();
            activeOperations.Remove(completion.Task);
        }
    }

    private async Task<PlaylistLampViewerWindow> OpenAsync(
        PlaylistLampViewerOpenContext context,
        CancellationToken cancellationToken)
    {
        if (context == null || Volatile.Read(ref disposed) != 0)
        {
            return null;
        }

        IPlaylistLampViewerDataSource source = null;
        PlaylistLampViewerSession session = null;
        PlaylistLampViewerViewModel viewModel = null;
        WindowLifetime lifetime = null;
        using var openCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownCancellation.Token,
            cancellationToken);
        try
        {
            source = dataSourceFactory(context)
                ?? throw new InvalidOperationException("The playlist lamp viewer source factory returned null.");
            session = new PlaylistLampViewerSession(context.PlaylistId, source);
            viewModel = new PlaylistLampViewerViewModel(
                context.PlaylistId,
                context.PlaylistName,
                session,
                owner.Dispatcher,
                request => workspace.TryRequestPlaylistLampNavigation(request));
            PlaylistLampViewerWindow window = new(owner, viewModel);
            lifetime = new WindowLifetime(window, source, viewModel, openCancellation.Token);
            lifetimes.Add(lifetime);
            window.Tag = lifetime;
            window.Closed += WindowClosed;
            viewModel.TerminalResultChanged += ViewModelTerminalResultChanged;
            prepareWindowForShow?.Invoke(window);

            PlaylistLampAggregationResult firstResult = await viewModel
                .StartAndWaitForPresentableAsync(lifetime.OpenCancellation.Token)
                .ConfigureAwait(true);
            if (Volatile.Read(ref disposed) != 0
                || openCancellation.IsCancellationRequested
                || lifetime.OpenCancellation.IsCancellationRequested
                || Volatile.Read(ref lifetime.Cleaned) != 0)
            {
                await BeginCleanup(lifetime, closeWindow: true).ConfigureAwait(true);
                ThrowLifetimeCleanupFailure(lifetime);
                return null;
            }
            if (firstResult.State == PlaylistLampViewerState.Deleted)
            {
                await BeginCleanup(lifetime, closeWindow: true).ConfigureAwait(true);
                ThrowLifetimeCleanupFailure(lifetime);
                await ShowInitialDeletedAsync(openCancellation.Token).ConfigureAwait(true);
                return null;
            }
            if (firstResult.State == PlaylistLampViewerState.Failed)
            {
                await BeginCleanup(lifetime, closeWindow: true).ConfigureAwait(true);
                ThrowLifetimeCleanupFailure(lifetime);
                await ShowFailureAsync(firstResult, initial: true, openCancellation.Token).ConfigureAwait(true);
                return null;
            }

            lifetime.IsShown = true;
            window.Show();
            activateWindowForInitialPresentation(window);
            window.Owner = null;
            return window;
        }
        catch (Exception failure)
        {
            bool ownedCancellation = failure is OperationCanceledException
                && (openCancellation.IsCancellationRequested
                    || lifetime?.OpenCancellation.IsCancellationRequested == true
                    || lifetime?.CleanupTask != null
                    || Volatile.Read(ref disposed) != 0);
            var cleanupFailures = new List<Exception>();
            if (lifetime != null)
            {
                try
                {
                    await BeginCleanup(lifetime, closeWindow: true).ConfigureAwait(true);
                    ThrowLifetimeCleanupFailure(lifetime);
                }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            else
            {
                TryCleanup(() => viewModel?.Dispose(), cleanupFailures);
                TryCleanup(() => session?.Dispose(), cleanupFailures);
                if (session != null)
                {
                    try { await session.DisposeAsync().ConfigureAwait(true); }
                    catch (Exception exception) { cleanupFailures.Add(exception); }
                }
                try { await DisposeSourceAsync(source).ConfigureAwait(true); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
                if (cleanupFailures.Count > 0)
                {
                    RetainManagerCleanupFailure(CreateCleanupFailure(cleanupFailures));
                }
            }
            if (cleanupFailures.Count > 0)
            {
                if (!ownedCancellation) { cleanupFailures.Insert(0, failure); }
                ThrowCleanupFailures(cleanupFailures);
            }
            if (ownedCancellation) { return null; }
            throw;
        }
        finally
        {
            if (lifetime != null)
            {
                Volatile.Write(ref lifetime.OpenCompleted, 1);
                try
                {
                    if (lifetime.CleanupTask != null)
                    {
                        // cleanupはOpenAsyncを待ちません。逆方向にだけ合流し、初回tokenを早期解放しません。
                        await lifetime.CleanupTask.ConfigureAwait(true);
                        DisposeOpenCancellationIfSafe(lifetime);
                        if (lifetime.CleanupFailure == null) { lifetimes.Remove(lifetime); }
                    }
                }
                catch (Exception exception)
                {
                    RetainLifetimeCleanupFailure(lifetime, exception);
                    throw;
                }
            }
        }
    }

    private static void ActivateWindowForInitialPresentation(PlaylistLampViewerWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _ = window.Activate();
    }

    private async void ViewModelTerminalResultChanged(
        object sender,
        PlaylistLampAggregationResultChangedEventArgs e)
    {
        if (sender is not PlaylistLampViewerViewModel viewModel
            || e?.Result == null
            || FindLifetime(viewModel) is not { } lifetime
            || !lifetime.IsShown
            || Volatile.Read(ref lifetime.Cleaned) != 0)
        {
            return;
        }
        if (e.Result.State is not (PlaylistLampViewerState.Deleted or PlaylistLampViewerState.Failed)
            || Interlocked.Exchange(ref lifetime.FailureHandled, 1) != 0)
        {
            return;
        }
        // close/回収が失敗した場合の所有通知も含め、async voidの実終端まで待機対象へ残します。
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperations.Add(completion.Task);
        try { await HandleLiveTerminalAsync(lifetime, e.Result).ConfigureAwait(true); }
        catch (Exception exception)
        {
            // 表示だけの失敗はShowFailureAsyncが処理済みです。ここは実close/資源回収の失敗です。
            await ShowOperationFailureAsync(exception).ConfigureAwait(true);
        }
        finally
        {
            completion.TrySetResult();
            activeOperations.Remove(completion.Task);
        }
    }

    private async Task HandleLiveTerminalAsync(WindowLifetime lifetime, PlaylistLampAggregationResult result)
    {
        try
        {
            if (result.State == PlaylistLampViewerState.Failed)
            {
                await ShowFailureAsync(result, initial: false, lifetime.OpenCancellation.Token).ConfigureAwait(true);
            }
        }
        finally
        {
            await BeginCleanup(lifetime, closeWindow: true).ConfigureAwait(true);
            ThrowLifetimeCleanupFailure(lifetime);
        }
    }

    private async Task ShowFailureAsync(
        PlaylistLampAggregationResult result,
        bool initial,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        string message = initial
            ? Resources.PlaylistLampViewer_initial_failure
            : Resources.PlaylistLampViewer_live_failure;
        if (!string.IsNullOrWhiteSpace(result?.FailureMessage))
        {
            message += Environment.NewLine + result.FailureMessage;
        }
        try
        {
            UiDialogResult dialogResult = await dialogs.ShowMessageAsync(
                new UiMessageRequest(
                    message,
                    Resources.PlaylistLampViewer_failure_title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Hand,
                    MessageBoxResult.OK,
                    owner: owner),
                cancellationToken).ConfigureAwait(true);
            UiDialogRoute.ThrowIfNotShown(dialogResult, initial
                ? "playlist lamp viewer initial failure"
                : "playlist lamp viewer live failure");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // この窓またはownerの取消だけを静かな通知終端として扱います。
        }
        catch (Exception notificationFailure)
        {
            TryLogFailure(notificationFailure, "playlist lamp viewer notification");
        }
    }

    private async Task ShowInitialDeletedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            UiDialogResult dialogResult = await dialogs.ShowMessageAsync(
                new UiMessageRequest(
                    Resources.PlaylistLampViewer_initial_deleted,
                    Resources.PlaylistLampViewer_failure_title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    MessageBoxResult.OK,
                    owner: owner),
                cancellationToken).ConfigureAwait(true);
            UiDialogRoute.ThrowIfNotShown(dialogResult, "playlist lamp viewer initial deleted");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // この窓またはownerの取消だけを静かな通知終端として扱います。
        }
        catch (Exception notificationFailure)
        {
            TryLogFailure(notificationFailure, "playlist lamp viewer notification");
        }
    }

    private void WindowClosed(object sender, EventArgs e)
    {
        if (sender is PlaylistLampViewerWindow window && window.Tag is WindowLifetime lifetime)
        {
            // native Closedでは同期throwせず、予約済み回収の実結果をowner drainが観測します。
            _ = BeginCleanup(lifetime, closeWindow: false);
        }
    }

    private void Close(WindowLifetime lifetime)
    {
        if (lifetime != null) { _ = BeginCleanup(lifetime, closeWindow: true); }
    }

    private WindowLifetime FindLifetime(PlaylistLampViewerViewModel viewModel)
        => lifetimes.FirstOrDefault(lifetime => ReferenceEquals(lifetime.ViewModel, viewModel));

    private Task BeginCleanup(WindowLifetime lifetime, bool closeWindow)
    {
        if (lifetime.CleanupTask != null) { return lifetime.CleanupTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.CleanupTask = completion.Task;
        activeOperations.Add(completion.Task);
        Volatile.Write(ref lifetime.Cleaned, 1);
        var failures = new List<Exception>();
        // 再入するClosedや停止callbackより先に、唯一の回収とmanagerの待機対象を成立させます。
        if (closeWindow)
        {
            TryCleanup(() =>
            {
                lifetime.Window.Close();
            }, failures);
        }
        // 未表示WindowもApplication/Ownerの集合に所属するため、実Close後にだけ購読を外します。
        // Close中のClosed再入は、先に登録した唯一のCleanupTaskへ合流します。
        TryCleanup(() => lifetime.Window.Closed -= WindowClosed, failures);
        TryCleanup(() => lifetime.ViewModel.TerminalResultChanged -= ViewModelTerminalResultChanged, failures);
        TryCleanup(() => lifetime.Window.DataContext = null, failures);
        TryCleanup(lifetime.ViewModel.Dispose, failures);
        _ = lifetime.RequestCancellation();
        _ = CleanupCoreAsync(lifetime, failures, completion);
        return completion.Task;
    }

    private async Task CleanupCoreAsync(
        WindowLifetime lifetime,
        List<Exception> failures,
        TaskCompletionSource completion)
    {
        try { await lifetime.ViewModel.DisposeAsync().ConfigureAwait(true); }
        catch (Exception exception) { failures.Add(exception); }
        try { await lifetime.CancellationTask.ConfigureAwait(true); }
        catch (Exception exception) { failures.Add(exception); }
        // Capture/buildと両取消経路のcallbackが実終端になってからsourceを一度だけ解放します。
        try { await DisposeSourceAsync(lifetime.Source).ConfigureAwait(true); }
        catch (Exception exception) { failures.Add(exception); }
        TryCleanup(() => DisposeOpenCancellationIfSafe(lifetime), failures);
        TryCleanup(() => lifetime.Window.Tag = null, failures);
        if (failures.Count > 0) { RetainLifetimeCleanupFailure(lifetime, CreateCleanupFailure(failures)); }
        if (lifetime.CleanupFailure == null && Volatile.Read(ref lifetime.OpenCompleted) != 0)
        {
            lifetimes.Remove(lifetime);
        }
        // 失敗履歴はlifetimeへ保持し、managerの活動台帳は実終端だけを表します。
        completion.TrySetResult();
        activeOperations.Remove(completion.Task);
    }

    private static void ThrowLifetimeCleanupFailure(WindowLifetime lifetime)
    {
        if (lifetime.CleanupFailure != null) { ThrowCleanupFailures([lifetime.CleanupFailure]); }
    }

    private void RetainLifetimeCleanupFailure(WindowLifetime lifetime, Exception failure)
    {
        lifetime.CleanupFailure = lifetime.CleanupFailure == null
            ? failure
            : new AggregateException(lifetime.CleanupFailure, failure);
        lifetimes.Add(lifetime);
    }

    private void RetainManagerCleanupFailure(Exception failure)
    {
        managerCleanupFailure = managerCleanupFailure == null
            ? failure
            : new AggregateException(managerCleanupFailure, failure);
    }

    private static Exception CreateCleanupFailure(IReadOnlyList<Exception> failures)
        => failures.Count == 1 ? failures[0] : new AggregateException(failures);

    private static void TryCleanup(Action action, ICollection<Exception> failures)
    {
        try { action(); }
        catch (Exception exception) { failures.Add(exception); }
    }

    private static void ThrowCleanupFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 1)
        {
            // 実解放からのOCEは取消要求の成功ではなく、async drainにも故障として残します。
            if (failures[0] is OperationCanceledException) { throw new AggregateException(failures[0]); }
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1) { throw new AggregateException(failures); }
    }

    private async Task ShowOperationFailureAsync(Exception exception)
    {
        TryLogFailure(exception, "playlist lamp viewer operation");
        if (Volatile.Read(ref disposed) != 0) { return; }
        try
        {
            UiDialogResult notification = await dialogs.ShowMessageAsync(new UiMessageRequest(
                Resources.Msg_error_unexpected + Environment.NewLine + exception.Message,
                Resources.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK,
                owner: owner)).ConfigureAwait(true);
            UiDialogRoute.ThrowIfNotShown(notification, "playlist lamp viewer operation failure notification");
        }
        catch (Exception notificationFailure) { TryLogFailure(notificationFailure, "playlist lamp viewer operation failure notification"); }
    }

    private static void TryLogFailure(Exception exception, string routeName)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, routeName); }
        catch { /* 診断失敗を実close/資源回収の成否へ合成しません。 */ }
    }

    private static void DisposeOpenCancellationIfSafe(WindowLifetime lifetime)
    {
        if (lifetime != null
            && Volatile.Read(ref lifetime.OpenCompleted) != 0
            && Volatile.Read(ref lifetime.Cleaned) != 0
            && Interlocked.Exchange(ref lifetime.OpenCancellationDisposed, 1) == 0)
        {
            try { lifetime.InheritedCancellationRegistration.Dispose(); }
            finally { lifetime.OpenCancellation.Dispose(); }
        }
    }

    private static Task DisposeSourceAsync(IPlaylistLampViewerDataSource source)
    {
        return Task.Run(async () =>
        {
            if (source is IAsyncDisposable asyncSource)
            {
                await asyncSource.DisposeAsync().ConfigureAwait(false);
            }
            else { (source as IDisposable)?.Dispose(); }
        });
    }
}
