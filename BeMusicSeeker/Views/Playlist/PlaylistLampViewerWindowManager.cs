using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

/// <summary>
/// Owns all modeless playlist lamp viewer windows opened by one main-window shell.
/// It gates first presentation on a terminal non-loading result and keeps each window's
/// source/session lifetime independent from every other instance.
/// </summary>
internal sealed class PlaylistLampViewerWindowManager : IDisposable
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
            OpenCancellation = CancellationTokenSource.CreateLinkedTokenSource(inheritedCancellation);
        }

        internal PlaylistLampViewerWindow Window { get; }

        internal IPlaylistLampViewerDataSource Source { get; }

        internal PlaylistLampViewerViewModel ViewModel { get; }

        internal CancellationTokenSource OpenCancellation { get; }

        internal bool IsShown;

        internal int FailureHandled;

        internal int Cleaned;

        internal int OpenCompleted;
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

    private int disposed;

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
        => OpenAsync(workspace.CapturePlaylistLampViewerOpenContext(table), cancellationToken);

    /// <summary>
    /// Opens a fresh modeless viewer for the single playlist-summary row captured by a context menu.
    /// </summary>
    /// <param name="row">Summary row captured from the context menu.</param>
    /// <returns>A task yielding the shown child window, or null for a stale/failed open.</returns>
    internal Task<PlaylistLampViewerWindow> TryOpenAsync(
        PlaylistSummaryRow row,
        CancellationToken cancellationToken = default)
        => OpenAsync(workspace.CapturePlaylistLampViewerOpenContext(row), cancellationToken);

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
        => OpenAsync(context, cancellationToken);

    /// <summary>
    /// Compatibility fire-and-forget entry for synchronous menu callers.
    /// The actual presentation remains gated by <see cref="TryOpenAsync(BMSTable, CancellationToken)"/>.
    /// </summary>
    /// <param name="table">Table captured from the context menu.</param>
    /// <returns>Always null because the window is created after the asynchronous first-result gate.</returns>
    internal PlaylistLampViewerWindow TryOpen(BMSTable table)
    {
        _ = TryOpenAsync(table);
        return null;
    }

    /// <summary>Compatibility fire-and-forget entry for synchronous summary-menu callers.</summary>
    /// <param name="row">Summary row captured from the context menu.</param>
    /// <returns>Always null because the window is created after the asynchronous first-result gate.</returns>
    internal PlaylistLampViewerWindow TryOpen(PlaylistSummaryRow row)
    {
        _ = TryOpenAsync(row);
        return null;
    }

    /// <summary>Closes every tracked viewer and releases pending source/session lifetimes.</summary>
    internal void CloseAll()
    {
        foreach (WindowLifetime lifetime in lifetimes.ToArray())
        {
            lifetime.OpenCancellation.Cancel();
            if (lifetime.IsShown && lifetime.Window.IsVisible)
            {
                try
                {
                    lifetime.Window.Close();
                }
                catch (InvalidOperationException)
                {
                    Cleanup(lifetime);
                }
            }
            else
            {
                Cleanup(lifetime);
            }
        }
    }

    /// <summary>Closes all child windows and makes future opens no-op.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        shutdownCancellation.Cancel();
        CloseAll();
        shutdownCancellation.Dispose();
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
            prepareWindowForShow?.Invoke(window);
            lifetime = new WindowLifetime(window, source, viewModel, openCancellation.Token);
            lifetimes.Add(lifetime);
            window.Tag = lifetime;
            window.Closed += WindowClosed;
            viewModel.TerminalResultChanged += ViewModelTerminalResultChanged;

            PlaylistLampAggregationResult firstResult = await viewModel
                .StartAndWaitForPresentableAsync(lifetime.OpenCancellation.Token)
                .ConfigureAwait(true);
            if (Volatile.Read(ref disposed) != 0
                || openCancellation.IsCancellationRequested
                || lifetime.OpenCancellation.IsCancellationRequested)
            {
                Cleanup(lifetime);
                return null;
            }
            if (firstResult.State == PlaylistLampViewerState.Deleted)
            {
                Cleanup(lifetime);
                await ShowInitialDeletedAsync(openCancellation.Token).ConfigureAwait(true);
                return null;
            }
            if (firstResult.State == PlaylistLampViewerState.Failed)
            {
                Cleanup(lifetime);
                await ShowFailureAsync(firstResult, initial: true, openCancellation.Token).ConfigureAwait(true);
                return null;
            }

            lifetime.IsShown = true;
            window.Show();
            activateWindowForInitialPresentation(window);
            window.Owner = null;
            return window;
        }
        catch (OperationCanceledException) when (
            openCancellation.IsCancellationRequested
            || lifetime?.OpenCancellation.IsCancellationRequested == true
            || Volatile.Read(ref disposed) != 0)
        {
            if (lifetime != null)
            {
                Cleanup(lifetime);
            }
            else
            {
                viewModel?.Dispose();
                session?.Dispose();
                DisposeSource(source);
            }
            return null;
        }
        catch
        {
            if (lifetime != null)
            {
                Cleanup(lifetime);
            }
            else
            {
                viewModel?.Dispose();
                session?.Dispose();
                DisposeSource(source);
            }
            throw;
        }
        finally
        {
            if (lifetime != null)
            {
                Volatile.Write(ref lifetime.OpenCompleted, 1);
                DisposeOpenCancellationIfSafe(lifetime);
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

        if (e.Result.State == PlaylistLampViewerState.Deleted)
        {
            Close(lifetime);
            return;
        }
        if (e.Result.State != PlaylistLampViewerState.Failed
            || Interlocked.Exchange(ref lifetime.FailureHandled, 1) != 0)
        {
            return;
        }

        CancellationToken notificationCancellation = lifetime.OpenCancellation.Token;
        try
        {
            await ShowFailureAsync(e.Result, initial: false, notificationCancellation)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested
            || Volatile.Read(ref disposed) != 0)
        {
            // Owner shutdown or manual close cancels an in-flight notification. The child
            // still reaches the same exact-once cleanup path in finally.
        }
        finally
        {
            Close(lifetime);
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

    private async Task ShowInitialDeletedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }
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

    private void WindowClosed(object sender, EventArgs e)
    {
        if (sender is PlaylistLampViewerWindow window)
        {
            Cleanup(window);
        }
    }

    private void Close(WindowLifetime lifetime)
    {
        if (lifetime == null || Volatile.Read(ref lifetime.Cleaned) != 0)
        {
            return;
        }
        lifetime.OpenCancellation.Cancel();
        if (lifetime.IsShown && lifetime.Window.IsVisible)
        {
            try
            {
                lifetime.Window.Close();
            }
            catch (InvalidOperationException)
            {
                Cleanup(lifetime);
            }
        }
        else
        {
            Cleanup(lifetime);
        }
    }

    private WindowLifetime FindLifetime(PlaylistLampViewerViewModel viewModel)
        => lifetimes.FirstOrDefault(lifetime => ReferenceEquals(lifetime.ViewModel, viewModel));

    private void Cleanup(PlaylistLampViewerWindow window)
    {
        if (window == null)
        {
            return;
        }
        Cleanup(window.Tag as WindowLifetime);
    }

    private void Cleanup(WindowLifetime lifetime)
    {
        if (lifetime == null || Interlocked.Exchange(ref lifetime.Cleaned, 1) != 0)
        {
            return;
        }
        lifetime.Window.Closed -= WindowClosed;
        lifetime.ViewModel.TerminalResultChanged -= ViewModelTerminalResultChanged;
        lifetime.Window.DataContext = null;
        lifetime.ViewModel.Dispose();
        DisposeSource(lifetime.Source);
        DisposeOpenCancellationIfSafe(lifetime);
        lifetimes.Remove(lifetime);
        lifetime.Window.Tag = null;
    }

    private static void DisposeOpenCancellationIfSafe(WindowLifetime lifetime)
    {
        if (lifetime != null
            && Volatile.Read(ref lifetime.OpenCompleted) != 0
            && Volatile.Read(ref lifetime.Cleaned) != 0)
        {
            lifetime.OpenCancellation.Dispose();
        }
    }

    private static void DisposeSource(IPlaylistLampViewerDataSource source)
    {
        (source as IDisposable)?.Dispose();
    }
}
