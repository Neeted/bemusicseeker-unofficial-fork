using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using Livet.EventListeners;

namespace BeMusicSeeker.ViewModels;

internal sealed class RegularChartMutationRefreshSuppressionChangedEventArgs : EventArgs
{
    internal RegularChartMutationRefreshSuppressionChangedEventArgs(bool isSuppressed)
    {
        IsSuppressed = isSuppressed;
    }

    internal bool IsSuppressed { get; }
}

/// <summary>
/// 通常一覧の要求寿命、派生行、順序の再利用、予備構築と終端公開を所有します。
/// </summary>
internal sealed partial class RegularChartListOwner : IDisposable
{
    internal event EventHandler<NormalLibraryRefreshAppliedEventArgs> NormalLibraryRefreshApplied;

    internal event EventHandler<MainChartListSortRequestedEventArgs> SortChanged;

    internal event EventHandler<RegularChartSortRefreshRequestedEventArgs> SortRefreshRequested;

    internal event EventHandler<RegularChartTreeNavigationPresentationRequestedEventArgs> TreeNavigationPresentationRequested;

    internal event EventHandler<RegularChartMaintenanceNavigationPresentationRequestedEventArgs> MaintenanceNavigationPresentationRequested;

    internal event EventHandler<RegularChartInstallNavigationPresentationRequestedEventArgs> InstallNavigationPresentationRequested;

    internal event EventHandler<RegularChartMutationRefreshSuppressionChangedEventArgs> RefreshSuppressionChanged;

    private readonly object syncRoot = new();
    private readonly object normalLibraryRefreshApplyLock = new();
    private readonly MainChartListViewModel mainChartList;
    private readonly PlaylistWorkspaceViewModel playlistWorkspace;
    private readonly Action<string> log;
    private readonly Action<string> logWarning;
    private readonly Action<Action> dispatchToUi;
    private readonly IUiScheduler normalLibraryRefreshUiScheduler;
    private readonly PendingPackageWorkflowOwner pendingPackageWorkflow;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly IChartMutationPlaybackPort playback;
    private readonly Dictionary<NormalLibrarySortCacheKey, List<LibraryChartRow>> sortCache = [];
    private readonly Dictionary<NormalLibrarySortCacheKey, ChartListOrder> virtualOrderCache = [];
    private readonly Dictionary<VirtualChartSubsetSortCacheKey, ChartListOrder> virtualSubsetOrderCache = [];
    private readonly HashSet<Task> folderRenameTasks = [];
    private readonly HashSet<Task> viewPreparationTasks = [];
    private readonly NormalLibraryRowCache rowCache = new();
    private CancellationTokenSource currentCancellation;
    private long currentRequestId;
    private RegularChartNavigationRequest currentNavigation;
    private bool regularRequestActive;
    private IReadOnlyList<LibraryChartRow> folderRows;
    private IReadOnlyList<LibraryChartRow> keywordRows;
    private IReadOnlyList<LibraryChartRow> modeRows;
    private List<LibraryChartRow> folderSortSourceSnapshot;
    private List<LibraryChartRow> folderSortResultSnapshot;
    private string folderSortColumnName;
    private ListSortDirection? folderSortDirection;
    private RegularNormalLibraryTreeFilter treeFilter;
    private ChartListSortSpecification currentSort;
    private NormalLibrarySourceRows virtualSourceRows;
    private BMSLibrary virtualSourceRowsLibrary;
    private bool virtualSourceRowsLibraryReserved;
    private long virtualSourceRowsGeneration;
    private long sourceGeneration;
    private long sortKeyGeneration;
    private long sortRequestRevision;
    private RegularChartSortRefreshRequestedEventArgs pendingSortRefresh;
    private Task sortRefreshWorker;
    private long warningGeneration;
    private long installDestinationGeneration;
    private long maintenanceGeneration;
    private int handledCatalogRevision;
    private PropertyChangedEventListener normalLibraryRefreshListener;
    private BMSLibrary normalLibraryRefreshSource;
    private int normalLibraryRefreshHandledNotificationVersion;
    private int normalLibraryRefreshRequestedNotificationVersion;
    private string normalLibraryRefreshRequestedReason;
    private bool normalLibraryRefreshApplySuppressed;
    private bool normalLibraryRefreshDrainScheduled;
    private TaskCompletionSource<bool> normalLibraryRefreshDrainCompletionSource;
    private Task normalLibraryRefreshDrainCompletion = Task.CompletedTask;
    private (BMSLibrary Library, Task Work)? duplicateDataPreparation;
    private CancellationTokenSource virtualOrderPrewarmCancellation;

    private RegularChartListPrewarmLease virtualOrderPrewarmLease;
    private Task virtualOrderPrewarmCompletion = Task.CompletedTask;
    private Task shutdownCompletion = Task.CompletedTask;
    private Task folderRenameTail = Task.CompletedTask;
    private int virtualOrderPrewarmRunId;
    private bool disposed;

    private readonly IUiDialogService mutationDialogs;
    private IUiDialogService duplicateNavigationDialogs;

    /// <summary>重複解析を開始できない場合の案内を、画面の既存通知窓口へ接続します。</summary>
    internal void ConfigureDuplicateNavigationDialogs(IUiDialogService dialogs)
        => duplicateNavigationDialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));


    /// <summary>Composes list behavior with the terminal dialog dependency for manual folder mutations.</summary>
    internal RegularChartListOwner(
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        Action<string> log,
        Action<Action> dispatchToUi,
        Action<string> logWarning,
        PendingPackageWorkflowOwner pendingPackageWorkflow,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IChartMutationPlaybackPort playback,
        IUiScheduler normalLibraryRefreshUiScheduler,
        IUiDialogService mutationDialogs = null)
    {
        this.mainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.logWarning = logWarning ?? log;
        this.pendingPackageWorkflow = pendingPackageWorkflow ?? throw new ArgumentNullException(nameof(pendingPackageWorkflow));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.mutationDialogs = mutationDialogs;
        this.normalLibraryRefreshUiScheduler = normalLibraryRefreshUiScheduler
            ?? throw new ArgumentNullException(nameof(normalLibraryRefreshUiScheduler));
        this.mainChartList.AppliedColumnModeCommitted += MainChartListAppliedColumnModeCommitted;
    }

    /// <summary>現在の選択を保ってライブラリを接続・交換します。交換時は既存の構築番号と入力識別で旧一覧候補を失効させ、開始済みTaskは完了まで回収します。</summary>
    internal void AttachNormalLibraryRefreshSource(BMSLibrary library)
    {
        PropertyChangedEventListener previousListener = null;
        CancellationTokenSource previousRequest = null;
        // 起動時は既にファイル操作の権限を持つため、接続のために同じ権限を再取得しない。
        // 通知購読と一覧の参照元をロック内で交換し、旧処理の取消と購読解除はロック外で行う。
        {
            PropertyChangedEventListener nextListener = null;
            lock (normalLibraryRefreshApplyLock)
            {
                lock (syncRoot)
                {
                    if (ReferenceEquals(normalLibraryRefreshSource, library))
                    {
                        return;
                    }
                    // ライブラリ交換でも利用者の選択を保ち、実体化済みの行を含む旧候補だけを失効させる。
                    previousRequest = InvalidateCurrentRequestUnsafe();

                    previousListener = normalLibraryRefreshListener;
                    normalLibraryRefreshListener = null;
                    normalLibraryRefreshSource?.DisconnectDuplicateDataPreparation(PrepareAcceptedDuplicateDataAsync);
                    normalLibraryRefreshSource = library;
                    normalLibraryRefreshHandledNotificationVersion = 0;
                    normalLibraryRefreshRequestedNotificationVersion = 0;
                    normalLibraryRefreshRequestedReason = null;
                    if (!disposed && library != null)
                    {
                        library.ConnectDuplicateDataPreparation(PrepareAcceptedDuplicateDataAsync);
                        nextListener = new PropertyChangedEventListener(library);
                        normalLibraryRefreshListener = nextListener;
                    }
                }
            }

            if (nextListener != null)
            {
                BMSLibrary attachedLibrary = library;
                nextListener.RegisterHandler(
                    () => attachedLibrary.NormalLibraryRefreshNotificationVersion,
                    delegate
                    {
                        QueueLatestNormalLibraryRefreshNotification(
                            "normal_library_refresh",
                            attachedLibrary);
                    });
                QueueLatestNormalLibraryRefreshNotification(
                    "normal_library_refresh",
                    attachedLibrary);
            }
        }
        CancelAndDispose(previousRequest);
        previousListener?.Dispose();
    }

    internal void QueueLatestNormalLibraryRefreshNotification(
        string reason,
        BMSLibrary expectedLibrary = null)
    {
        _ = RequestLibraryChangeDelivery(reason, expectedLibrary, requireDelivery: false);
    }

    /// <summary>既存の単一drainを一回予約または共有し、同じbatchの全配送先とUI操作の終端を待ちます。呼出元の取消は共有配送を取消しません。</summary>
    internal Task DeliverPendingLibraryChangesAsync(BMSLibrary expectedLibrary, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) { return Task.FromCanceled(ct); }
        if (expectedLibrary == null) { return Task.FromException(new ArgumentNullException(nameof(expectedLibrary))); }
        Task delivery = RequestLibraryChangeDelivery("library_change_delivery", expectedLibrary, requireDelivery: true);
        return ct.CanBeCanceled ? delivery.WaitAsync(ct) : delivery;
    }

    private Task RequestLibraryChangeDelivery(string reason, BMSLibrary expectedLibrary, bool requireDelivery)
    {
        TaskCompletionSource<bool> completionSource;
        BMSLibrary library;
        int requestedVersion;
        lock (syncRoot)
        {
            library = normalLibraryRefreshSource;
            if (disposed
                || library == null
                || (expectedLibrary != null && !ReferenceEquals(library, expectedLibrary)))
            {
                return requireDelivery
                    ? Task.FromCanceled(new CancellationToken(canceled: true))
                    : Task.CompletedTask;
            }

            requestedVersion = library.NormalLibraryRefreshNotificationVersion;
            if (normalLibraryRefreshDrainScheduled)
            {
                normalLibraryRefreshRequestedNotificationVersion = Math.Max(normalLibraryRefreshRequestedNotificationVersion, requestedVersion);
                normalLibraryRefreshRequestedReason = reason;
                return normalLibraryRefreshDrainCompletion;
            }
            if (requestedVersion <= normalLibraryRefreshHandledNotificationVersion)
            {
                return Task.CompletedTask;
            }

            normalLibraryRefreshRequestedNotificationVersion = Math.Max(
                normalLibraryRefreshRequestedNotificationVersion,
                requestedVersion);
            normalLibraryRefreshRequestedReason = reason;
            if (normalLibraryRefreshApplySuppressed)
            {
                return requireDelivery
                    ? Task.FromException(new InvalidOperationException("Library change delivery is suppressed."))
                    : Task.CompletedTask;
            }

            normalLibraryRefreshDrainScheduled = true;
            completionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            normalLibraryRefreshDrainCompletionSource = completionSource;
            normalLibraryRefreshDrainCompletion = completionSource.Task;
        }

        // fanoutとschedulerのCompletionを別々に確認する。Scheduleが同期実行する実装にも対応する。
        var execution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // このdrainだけの実試行位置です。予約後に合流したbatchも、失敗時の同位置再予約から除きます。
        int attemptedVersion = requestedVersion;
        IUiScheduledOperation operation;
        try
        {
            operation = normalLibraryRefreshUiScheduler.Schedule(
                () => DrainNormalLibraryRefreshNotifications(library, execution, ref attemptedVersion),
                UiSchedulePriority.Normal);
        }
        catch (Exception exception)
        {
            CompleteNormalLibraryRefreshDrain(completionSource, library, attemptedVersion, exception);
            return completionSource.Task;
        }

        if (operation?.IsAccepted != true)
        {
            CompleteNormalLibraryRefreshDrain(completionSource, library, attemptedVersion,
                new InvalidOperationException(operation?.RejectionReason ?? "UI scheduler rejected the refresh drain."));
            return completionSource.Task;
        }
        _ = ObserveNormalLibraryRefreshUiOperationAsync(operation, execution, completionSource, library, () => attemptedVersion);
        return completionSource.Task;
    }

    private void DrainNormalLibraryRefreshNotifications(BMSLibrary expectedLibrary, TaskCompletionSource<bool> execution,
        ref int attemptedVersion)
    {
        try
        {
            while (TryCaptureNormalLibraryRefreshDrainRequest(
                out BMSLibrary library,
                out string reason,
                out int requestedVersion))
            {
                if (!ReferenceEquals(library, expectedLibrary)) { throw new OperationCanceledException("The library change source was replaced."); }
                bool applied = ApplyLatestNormalLibraryRefreshNotificationOnExecutionLane(reason, library, ref attemptedVersion);
                if (!applied)
                {
                    AdvanceMissingNormalLibraryRefreshNotification(library, requestedVersion);
                    throw new InvalidOperationException("The requested library change range was not available for delivery.");
                }
            }
            lock (syncRoot)
            {
                if (disposed || !ReferenceEquals(normalLibraryRefreshSource, expectedLibrary))
                { throw new OperationCanceledException("Library change delivery was stopped."); }
                if (normalLibraryRefreshRequestedNotificationVersion > normalLibraryRefreshHandledNotificationVersion)
                { throw new InvalidOperationException("The requested library change delivery was suppressed."); }
            }
            execution.TrySetResult(true);
        }
        catch (Exception exception)
        {
            execution.TrySetException(exception);
        }
    }

    private async Task ObserveNormalLibraryRefreshUiOperationAsync(
        IUiScheduledOperation operation,
        TaskCompletionSource<bool> execution,
        TaskCompletionSource<bool> completionSource,
        BMSLibrary library, Func<int> getAttemptedVersion)
    {
        Exception failure = null;
        try
        {
            await operation.Completion.ConfigureAwait(false);
            if (operation.IsAborted) { throw new OperationCanceledException("UI scheduler aborted the refresh drain."); }
            if (!execution.Task.IsCompleted)
            {
                throw new InvalidOperationException(operation.IsAborted
                        ? "UI scheduler aborted the refresh drain."
                        : "UI scheduler completed without running the refresh drain.");
            }
            await execution.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        if (execution.Task.IsFaulted) { _ = execution.Task.Exception; }
        CompleteNormalLibraryRefreshDrain(completionSource, library, getAttemptedVersion(), failure);
    }

    private bool TryCaptureNormalLibraryRefreshDrainRequest(
        out BMSLibrary library,
        out string reason,
        out int requestedVersion)
    {
        lock (syncRoot)
        {
            if (disposed
                || normalLibraryRefreshApplySuppressed
                || normalLibraryRefreshSource == null
                || normalLibraryRefreshRequestedNotificationVersion
                    <= normalLibraryRefreshHandledNotificationVersion)
            {
                library = null;
                reason = null;
                requestedVersion = 0;
                return false;
            }

            library = normalLibraryRefreshSource;
            reason = normalLibraryRefreshRequestedReason ?? "normal_library_refresh";
            requestedVersion = normalLibraryRefreshRequestedNotificationVersion;
            return true;
        }
    }

    private void AdvanceMissingNormalLibraryRefreshNotification(
        BMSLibrary library,
        int requestedVersion)
    {
        bool missingNotification = false;
        lock (syncRoot)
        {
            if (!disposed
                && !normalLibraryRefreshApplySuppressed
                && ReferenceEquals(normalLibraryRefreshSource, library)
                && normalLibraryRefreshHandledNotificationVersion < requestedVersion)
            {
                normalLibraryRefreshHandledNotificationVersion = requestedVersion;
                missingNotification = true;
            }
        }
        if (missingNotification)
        {
            logWarning(
                "normal_library_refresh notification_missing requestedVersion="
                + requestedVersion);
        }
    }

    private void CompleteNormalLibraryRefreshDrain(TaskCompletionSource<bool> completionSource,
        BMSLibrary library, int attemptedVersion, Exception failure)
    {
        BMSLibrary rescheduleLibrary = null;
        string reason = null;
        lock (syncRoot)
        {
            if (ReferenceEquals(normalLibraryRefreshDrainCompletionSource, completionSource))
            {
                if (failure == null && (cachedNormalChanges.HasPending
                    || mainChartList.Rows is ChartListVirtualView { HasPendingReferenceChanges: true }))
                { failure = new InvalidOperationException("Library change delivery did not cover the captured reference boundary."); }
                normalLibraryRefreshDrainScheduled = false;
                if (!disposed && !normalLibraryRefreshApplySuppressed && normalLibraryRefreshSource != null
                    && normalLibraryRefreshRequestedNotificationVersion > normalLibraryRefreshHandledNotificationVersion
                    && (failure == null || !ReferenceEquals(library, normalLibraryRefreshSource)
                        || normalLibraryRefreshRequestedNotificationVersion > attemptedVersion))
                {
                    rescheduleLibrary = normalLibraryRefreshSource;
                    reason = normalLibraryRefreshRequestedReason;
                }
            }
        }
        if (rescheduleLibrary != null) { QueueLatestNormalLibraryRefreshNotification(reason ?? "normal_library_refresh", rescheduleLibrary); }
        if (failure == null) { completionSource.TrySetResult(true); }
        else
        {
            if (failure is OperationCanceledException) { completionSource.TrySetCanceled(); }
            else
            {
                completionSource.TrySetException(failure);
                _ = completionSource.Task.Exception; // 通知だけの予約でもfaultを観測し、明示要求には同じ失敗Taskを返す。
            }
            logWarning("normal_library_refresh drain_failed exception=" + failure.GetType().Name + " message=" + failure.Message);
        }
    }

    private bool ApplyLatestNormalLibraryRefreshNotificationOnExecutionLane(
        string reason,
        BMSLibrary expectedLibrary, ref int attemptedVersion)
    {
        lock (normalLibraryRefreshApplyLock)
        {
            BMSLibrary library;
            lock (syncRoot)
            {
                if (normalLibraryRefreshApplySuppressed
                    && (expectedLibrary == null || ReferenceEquals(normalLibraryRefreshSource, expectedLibrary)))
                {
                    return false;
                }
                if (disposed
                    || (expectedLibrary != null && !ReferenceEquals(normalLibraryRefreshSource, expectedLibrary)))
                {
                    return false;
                }
                library = normalLibraryRefreshSource;
            }

            NormalLibraryRefreshNotificationBatch notificationBatch = ConsumeNormalLibraryRefreshNotification(library);
            if (!notificationBatch.HasRefreshNotification)
            {
                return false;
            }

            attemptedVersion = notificationBatch.LatestVersion;
            ApplyNormalLibraryRefreshNotificationBatch(library, notificationBatch, reason);
            lock (syncRoot)
            {
                if (!disposed && ReferenceEquals(normalLibraryRefreshSource, library))
                {
                    normalLibraryRefreshHandledNotificationVersion = Math.Max(
                        normalLibraryRefreshHandledNotificationVersion,
                        notificationBatch.LatestVersion);
                }
            }

            return true;
        }
    }

    private void MainChartListAppliedColumnModeCommitted(MainViewUpdateMode mode)
    {
        if (mode == MainViewUpdateMode.PlayHistorySelected)
        {
            ResetDerivedCaches();
        }
    }

    internal ChartListSortSpecification CaptureSort()
    {
        lock (syncRoot)
        {
            return currentSort;
        }
    }

    internal ChartListSortParameters CaptureSortParameters()
    {
        lock (syncRoot)
        {
            return currentSort.HasValue
                ? new ChartListSortParameters
                {
                    ColumnsName = currentSort.RequestedColumnName,
                    Direction = currentSort.Direction
                }
                : null;
        }
    }

    internal bool ResetSortParameters(ChartListSortSpecification expectedSort)
    {
        lock (syncRoot)
        {
            if (!currentSort.HasValue
                || !expectedSort.HasValue
                || !string.Equals(currentSort.RequestedColumnName, expectedSort.RequestedColumnName, StringComparison.Ordinal)
                || currentSort.Direction != expectedSort.Direction)
            {
                return false;
            }

            currentSort = ChartListSortSpecification.Create(
                columnName: null,
                ListSortDirection.Ascending,
                hasValue: false);
            return true;
        }
    }

    internal bool HasTreeFilter
    {
        get
        {
            lock (syncRoot)
            {
                return treeFilter != null;
            }
        }
    }

    /// <summary>有効な画面移動を同期的に受理し、次の受付か停止まで同じ要求参照を保ちます。一覧の参照元ライブラリは、画面選択とは別に検査します。</summary>
    internal RegularChartNavigationRequest AcceptNavigation(DuplicateViewContext duplicateContext = null)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pendingNormalPresentation.HasValue)
            { pendingNormalPresentation.Value.Presentation.Completion?.TrySetResult(RegularChartListTerminalResult.Stale()); }
            return currentNavigation = new RegularChartNavigationRequest(this, duplicateContext);
        }
    }

    /// <summary>重複データの完成通知を受けたUIが、現在の選択を解決するための受付参照を取得します。依存更新には適用済みの参照を使います。</summary>
    internal RegularChartNavigationRequest CaptureNavigation()
    {
        lock (syncRoot) { return currentNavigation; }
    }

    /// <summary>指定した画面受付が現在も有効かを確認します。初期値のnullも参照が一致する間は有効です。</summary>
    internal bool IsCurrentNavigation(RegularChartNavigationRequest request)
    {
        lock (syncRoot) { return IsCurrentNavigationUnsafe(request); }
    }

    private bool IsCurrentNavigationUnsafe(RegularChartNavigationRequest request)
        => !disposed && ReferenceEquals(currentNavigation, request);

    /// <summary>通常ツリーの有効入力を受理し、同じ受付のUI配送と一覧更新を開始します。</summary>
    internal bool NavigateTree(RegularChartFolderFilterKind? filterKind, string filterKey = null)
    {
        if (filterKind.HasValue
            && filterKind.Value != RegularChartFolderFilterKind.Directory
            && filterKind.Value != RegularChartFolderFilterKind.Artist)
        {
            return false;
        }
        EventHandler<RegularChartTreeNavigationPresentationRequestedEventArgs> handler = TreeNavigationPresentationRequested
            ?? throw new InvalidOperationException("Regular chart tree navigation presentation is not composed.");
        RegularNormalLibraryTreeFilter filter = filterKind.HasValue
            ? RegularNormalLibraryTreeFilter.Create(filterKind.Value, filterKey) : null;
        ObserveSynchronousNavigation(StartViewPreparation((navigation, _) => DispatchNavigationAsync(navigation, () =>
        {
            bool keywordRefresh = playlistWorkspace.RequestPlaylistSummaryMode(enabled: false);
            if (!IsCurrentNavigation(navigation)) { return Task.CompletedTask; }
            lock (syncRoot)
            {
                if (!IsCurrentNavigationUnsafe(navigation)) { return Task.CompletedTask; }
                treeFilter = filter;
            }
            var request = new RegularChartTreeNavigationPresentationRequestedEventArgs(keywordRefresh,
                MainViewUpdateMode.FolderFilterSelected)
            { Navigation = navigation };
            handler(this, request);
            return request.Presentation;
        }), runOnWorker: false, duplicateContext: null), "regularTreeNavigation");
        return true;
    }

    /// <summary>有効な保守画面の移動要求を処理開始前に受理します。重複ではデータ準備を、その他の保守画面では一覧表示までを回収します。</summary>
    internal Task<bool> NavigateMaintenanceAsync(MainViewUpdateMode mode, object parameter = null,
        string reason = "maintenance_navigation")
    {
        if (!IsMaintenanceNavigationMode(mode)) { return Task.FromResult(false); }
        object capturedParameter = CaptureMaintenanceParameter(mode, parameter);
        return StartMaintenanceNavigation(mode, capturedParameter, reason, out _, runOnWorker: true);
    }

    /// <summary>UIへ適用済みの画面受付を使って依存更新を反映します。停止後は準備や通知を開始せず、未受理の結果を返します。</summary>
    /// <param name="navigation">UIへ適用した参照です。初期に捕捉したnullを後発受付へ付け替えません。</param>
    /// <param name="refreshCurrentView">現在の画面対象を表示し、その完了Taskを返す処理です。</param>
    /// <returns>受理したUI配送と表示処理の完了を表すTaskです。停止時には開始済みTaskを回収します。</returns>
    internal Task RefreshCurrentViewAsync(RegularChartNavigationRequest navigation, Func<Task> refreshCurrentView)
    {
        ArgumentNullException.ThrowIfNull(refreshCurrentView);
        return StartViewPreparation((_, _) => DispatchNavigationAsync(navigation, refreshCurrentView),
            runOnWorker: false, acceptNavigation: false);
    }

    /// <summary>同期入口も同じ短命受付と表示判定を使います。boolは受理を表し、UIの非同期完了を待ちません。</summary>
    internal bool NavigateMaintenance(MainViewUpdateMode mode, object parameter = null,
        string reason = "maintenance_navigation")
    {
        if (!IsMaintenanceNavigationMode(mode)) { return false; }
        object capturedParameter = CaptureMaintenanceParameter(mode, parameter);
        Task<bool> execution = StartMaintenanceNavigation(mode, capturedParameter, reason, out bool accepted, runOnWorker: false);
        ObserveSynchronousNavigation(execution, "regularMaintenanceNavigation");
        return accepted;
    }

    /// <summary>新規解析だけを選択受理前に共通受付へ非待機で受け付け、Busyでは表示文脈を変更しません。</summary>
    private Task<bool> StartMaintenanceNavigation(MainViewUpdateMode mode, object parameter, string reason, out bool accepted, bool runOnWorker)
    {
        IDisposable operationLease = null;
        bool busy;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            BMSLibrary library = normalLibraryRefreshSource;
            busy = mode == MainViewUpdateMode.DuplicateFilterSelected && library != null
                && library.DuplicateChartGroups == null && !IsDuplicateDataPreparationActiveUnsafe(library)
                && !chartFileOperations.TryEnter(out operationLease);
        }
        accepted = !busy;
        if (busy) { return RejectAsync(); }
        try
        {
            return StartViewPreparation(async (navigation, library) =>
            {
                using (operationLease)
                using (LibraryFileMutationCapability capability = operationLease == null ? null : chartFileOperations.CreateMutationCapability(operationLease))
                { await ExecuteMaintenanceNavigation(navigation, library, mode, parameter, reason, capability).ConfigureAwait(false); }
            }, runOnWorker: runOnWorker && mode != MainViewUpdateMode.DuplicateFilterSelected,
                duplicateContext: mode == MainViewUpdateMode.DuplicateFilterSelected ? (DuplicateViewContext)parameter : null);
        }
        catch { operationLease?.Dispose(); throw; }

        async Task<bool> RejectAsync()
        {
            await StartViewPreparation((_, _) => NotifyLibraryOperationBusyAsync(duplicateNavigationDialogs), runOnWorker: false, acceptNavigation: false).ConfigureAwait(false);
            return false;
        }
    }

    private static object CaptureMaintenanceParameter(MainViewUpdateMode mode, object parameter)
        => mode == MainViewUpdateMode.DuplicateFilterSelected ? CaptureDuplicateContext(parameter) : parameter;

    /// <summary>重複ルート、グループ、フォルダの入力を共通の選択文脈へ変換し、それぞれの対象の意味を保ちます。</summary>
    internal static DuplicateViewContext CaptureDuplicateContext(object parameter)
        => parameter switch
        {
            null => DuplicateViewContext.ForRoot(),
            DuplicateViewContext context => context,
            DuplicateGroup group => DuplicateViewContext.CaptureGroup(group),
            string folder => DuplicateViewContext.ForFolder(folder),
            _ => DuplicateViewContext.ForGroup(null)
        };

    private async Task ExecuteMaintenanceNavigation(RegularChartNavigationRequest navigation, BMSLibrary library,
        MainViewUpdateMode mode, object parameter, string reason, LibraryFileMutationCapability capability)
    {
        await DispatchNavigationAsync(navigation, () =>
        {
            bool keywordRefresh = playlistWorkspace.RequestPlaylistSummaryMode(enabled: false);
            if (!IsCurrentNavigation(navigation)) { return Task.CompletedTask; }
            bool hasAttachedLibrary;
            lock (syncRoot) { hasAttachedLibrary = normalLibraryRefreshSource != null; }
            // 重複以外の画面は現在のライブラリから一覧を作る。ライブラリ未接続時はサマリー解除の通知だけを行う。
            if (!hasAttachedLibrary && !keywordRefresh) { return Task.CompletedTask; }
            var request = new RegularChartMaintenanceNavigationPresentationRequestedEventArgs(
                mode, parameter, keywordRefresh, refreshRequested: hasAttachedLibrary && mode != MainViewUpdateMode.DuplicateFilterSelected)
            { Navigation = navigation };
            (MaintenanceNavigationPresentationRequested
                ?? throw new InvalidOperationException("Regular chart maintenance navigation presentation is not composed."))(this, request);
            if (mode == MainViewUpdateMode.DuplicateFilterSelected)
            {
                RegularDuplicateViewInput input;
                lock (syncRoot)
                {
                    input = ReferenceEquals(normalLibraryRefreshSource, library) && !IsDuplicateDataPreparationActiveUnsafe(library)
                        ? RegularDuplicateViewInput.Capture(library) : null;
                }
                // 完成済み結果は表示だけを開始し、Lを所有する解析の準備Taskへ混ぜない。
                if (input != null) { DuplicateDataReady?.Invoke(this, new RegularDuplicateDataReadyEventArgs(library, input)); }
            }
            return request.Presentation;
        }).ConfigureAwait(false);
        if (mode == MainViewUpdateMode.DuplicateFilterSelected && library != null)
        {
            // 解析前の画面文脈は現在の選択だけへ反映し、開始した解析は後発選択でも完了させる。
            await StartDuplicateDataPreparation(library, reason, capability).ConfigureAwait(false);
        }
    }

    /// <summary>選択命令を持たないデータ完成通知です。VMが現在の選択をUIで解決して表示を開始します。</summary>
    internal event EventHandler<RegularDuplicateDataReadyEventArgs> DuplicateDataReady;

    /// <summary>接続中ライブラリで開始済みの準備Taskだけを参照します。未準備でも新しい解析は開始しません。</summary>
    internal Task PrepareDuplicateDataAsync(string reason = "duplicate_dependency")
    {
        lock (syncRoot)
        {
            return !disposed && IsDuplicateDataPreparationActiveUnsafe(normalLibraryRefreshSource)
                ? duplicateDataPreparation.Value.Work : Task.CompletedTask;
        }
    }

    /// <summary>受理済み変更の共通権限で、接続中ライブラリの現在の重複選択に必要な準備だけを行います。</summary>
    private Task PrepareAcceptedDuplicateDataAsync(BMSLibrary library, LibraryFileMutationCapability capability)
    {
        lock (syncRoot)
        {
            if (disposed || !ReferenceEquals(normalLibraryRefreshSource, library) || currentNavigation?.DuplicateContext == null)
            { return Task.CompletedTask; }
            if (IsDuplicateDataPreparationActiveUnsafe(library)) { return duplicateDataPreparation.Value.Work; }
            if (library.DuplicateChartGroups != null) { return Task.CompletedTask; }
        }
        return StartDuplicateDataPreparation(library, "accepted_library_change", capability);
    }

    private bool IsDuplicateDataPreparationActiveUnsafe(BMSLibrary library)
        => duplicateDataPreparation is { } active && ReferenceEquals(active.Library, library) && !active.Work.IsCompleted;

    /// <summary>共通Lを所有する実解析と完成通知だけを、単一の準備Taskとして開始前に登録します。準備済み表示は登録せず、進行中の実解析があれば同じTaskへ合流します。</summary>
    private Task StartDuplicateDataPreparation(BMSLibrary library, string reason, LibraryFileMutationCapability capability = null)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task execution;
        lock (syncRoot)
        {
            if (library == null || disposed || !ReferenceEquals(normalLibraryRefreshSource, library)) { return Task.CompletedTask; }
            if (IsDuplicateDataPreparationActiveUnsafe(library)) { return duplicateDataPreparation.Value.Work; }
            if (library.DuplicateChartGroups != null || capability == null) { return Task.CompletedTask; }
            using (chartFileOperations.Borrow(capability)) { }
            execution = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                if (library.DuplicateChartGroups == null)
                {
                    using LibraryFileMutationLease borrowed = chartFileOperations.Borrow(capability);
                    library.SearchDuplicateChartGroups();
                }
                await RequestLibraryChangeDelivery(reason, library, requireDelivery: false).ConfigureAwait(false);
                RegularDuplicateViewInput input = RegularDuplicateViewInput.Capture(library)
                    ?? throw new InvalidOperationException("Duplicate data preparation did not produce ready groups.");
                await normalLibraryRefreshUiScheduler.InvokeAsync(() =>
                {
                    lock (syncRoot) { if (disposed || !ReferenceEquals(normalLibraryRefreshSource, library)) { return Task.CompletedTask; } }
                    var ready = new RegularDuplicateDataReadyEventArgs(library, input);
                    DuplicateDataReady?.Invoke(this, ready);
                    // 通知受信中の同期例外はデータ準備の失敗として返す。一覧表示は別のTaskとして所有する。
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            });
            duplicateDataPreparation = (library, execution);
            viewPreparationTasks.Add(execution);
        }
        execution.ObserveFault("duplicateDataPreparation");
        _ = ReleaseViewPreparationAsync(execution);
        start.SetResult();
        return execution;
    }

    /// <summary>準備済み重複の表示開始と非同期終端を所有し、失敗を既存の操作失敗通知へ一度渡します。通知中も停止の回収対象です。</summary>
    /// <param name="presentation">現在選択を検査して表示を開始するUI側の処理です。同期開始例外も通知対象に含みます。</param>
    /// <param name="dialogs">既存の操作失敗通知を表示する窓口です。</param>
    internal void PresentDuplicateData(Func<Task> presentation, IUiDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(dialogs);
        Task execution = StartViewPreparation(async (_, _) =>
        {
            try { await normalLibraryRefreshUiScheduler.InvokeAsync(presentation).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                logWarning("duplicate presentation failed: " + exception);
                try
                {
                    UiDialogResult result = await dialogs.ShowMessageAsync(UiMessageRequest.CreateError(
                        BeMusicSeeker.Properties.Resources.Msg_error_unexpected,
                        BeMusicSeeker.Properties.Resources.Error)).ConfigureAwait(false);
                    if (result == null || result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
                    { FileDbMutationReport.LogNotificationFailure(result?.Exception ?? new InvalidOperationException("Duplicate presentation failure was not displayed.")); }
                }
                catch (Exception notificationFailure) { FileDbMutationReport.LogNotificationFailure(notificationFailure); }
            }
        }, runOnWorker: false, acceptNavigation: false);
        execution.ObserveFault("duplicatePresentation");
    }

    private async Task DispatchNavigationAsync(RegularChartNavigationRequest navigation, Func<Task> presentation)
    {
        if (!IsCurrentNavigation(navigation)) { return; }
        var delivery = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        // UI配送の拒否や中断もTaskへ返し、開始されない処理の完了を待ち続けない。
        await normalLibraryRefreshUiScheduler.InvokeAsync(() => dispatchToUi(() =>
        {
            try { delivery.TrySetResult(IsCurrentNavigation(navigation) ? presentation() : Task.CompletedTask); }
            catch (Exception exception) { delivery.TrySetException(exception); }
        })).ConfigureAwait(false);
        await (await delivery.Task.ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static void ObserveSynchronousNavigation(Task execution, string operation)
    {
        // 同期入口で既に発生した例外は従来どおり呼出元へ返し、未終端のUI処理を同期的には待ちません。
        if (execution.IsFaulted) { execution.GetAwaiter().GetResult(); }
        execution.ObserveFault(operation);
    }

    /// <summary>画面移動と依存更新のTaskを開始前に登録します。停止後は依存更新を未受理とし、明示した画面移動には破棄済み例外を返します。受理済みTaskはロック外で回収します。</summary>
    private Task<bool> StartViewPreparation(Func<RegularChartNavigationRequest, BMSLibrary, Task> execute,
        bool runOnWorker, bool acceptNavigation = true, DuplicateViewContext duplicateContext = null)
    {
        // 非同期入口では開始合図の継続を非同期実行にし、UIで解析の継続を実行しない。
        var start = new TaskCompletionSource<bool>(runOnWorker
            ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None);
        RegularChartNavigationRequest navigation;
        BMSLibrary library;
        Task<bool> execution;
        lock (syncRoot)
        {
            if (disposed && !acceptNavigation) { return Task.FromResult(false); }
            ObjectDisposedException.ThrowIf(disposed, this);
            navigation = acceptNavigation ? AcceptNavigation(duplicateContext) : currentNavigation;
            library = normalLibraryRefreshSource;
            execution = runOnWorker ? Task.Run(ExecuteAsync) : ExecuteAsync();
            viewPreparationTasks.Add(execution);
        }
        // 処理はTask集合へ登録してから開始し、登録のロック内ではUIを呼び出さない。
        start.SetResult(true);
        _ = ReleaseViewPreparationAsync(execution);
        return execution;

        async Task<bool> ExecuteAsync()
        {
            await start.Task.ConfigureAwait(false);
            await execute(navigation, library).ConfigureAwait(false);
            return true;
        }
    }

    internal static bool IsMaintenanceNavigationMode(MainViewUpdateMode mode)
    {
        return mode is MainViewUpdateMode.FullScanAllChartsFilterSelected
            or MainViewUpdateMode.FileMissingFilterSelected
            or MainViewUpdateMode.FileMissingIgnoredFilterSelected
            or MainViewUpdateMode.DuplicateFilterSelected
            or MainViewUpdateMode.GarbledFilterSelected
            or MainViewUpdateMode.GarbleFixedFilterSelected
            or MainViewUpdateMode.UnregisteredFilterSelected
            or MainViewUpdateMode.ZeroNoteFilterSelected
            or MainViewUpdateMode.ChartInfoParseErrorFilterSelected;
    }

    /// <summary>導入画面の受付をワーカー投入前に固定し、UI配送から表示終端までを返します。</summary>
    internal Task<bool> NavigateInstallAsync(MainViewUpdateMode mode, object parameter = null)
    {
        if (!IsInstallNavigationMode(mode)) { return Task.FromResult(false); }
        return StartViewPreparation(
            (navigation, _) => ExecuteInstallNavigation(navigation, mode, parameter), runOnWorker: true, duplicateContext: null);
    }

    /// <summary>導入画面の同期入口です。受理だけを返し、同じ受付の表示を非同期に回収します。</summary>
    internal bool NavigateInstall(MainViewUpdateMode mode, object parameter = null)
    {
        if (!IsInstallNavigationMode(mode)) { return false; }
        ObserveSynchronousNavigation(StartViewPreparation(
            (navigation, _) => ExecuteInstallNavigation(navigation, mode, parameter), runOnWorker: false, duplicateContext: null), "regularInstallNavigation");
        return true;
    }

    private static bool IsInstallNavigationMode(MainViewUpdateMode mode)
        => mode is MainViewUpdateMode.NewlyInstalledFolderSelected or MainViewUpdateMode.PendingInstallFolderSelected;

    private Task ExecuteInstallNavigation(RegularChartNavigationRequest navigation, MainViewUpdateMode mode, object parameter)
        => DispatchNavigationAsync(navigation, () =>
        {
            bool keywordRefresh = playlistWorkspace.RequestPlaylistSummaryMode(enabled: false);
            if (!IsCurrentNavigation(navigation)) { return Task.CompletedTask; }
            var request = new RegularChartInstallNavigationPresentationRequestedEventArgs(mode, parameter, keywordRefresh)
            { Navigation = navigation };
            (InstallNavigationPresentationRequested
                ?? throw new InvalidOperationException("Regular chart install navigation presentation is not composed."))(this, request);
            return request.Presentation;
        });

    internal RegularNormalLibraryTreeFilter CaptureTreeFilter(bool enabled)
    {
        lock (syncRoot)
        {
            return enabled ? treeFilter : null;
        }
    }

    internal RegularChartListRequestLease BeginRequest()
    {
        if (!TryBeginRequest(out RegularChartListRequestLease lease))
        {
            throw new ObjectDisposedException(nameof(RegularChartListOwner));
        }
        return lease;
    }

    internal bool TryBeginRequest(out RegularChartListRequestLease lease)
    {
        return TryBeginRequest(
            retireDetailSource: false,
            out lease,
            out _);
    }

    private bool TryBeginRequest(
        bool retireDetailSource,
        out RegularChartListRequestLease lease,
        out PlaylistSourceRetirementRequest detailSourceRetirement,
        RegularChartNavigationRequest navigation = null)
    {
        CancellationTokenSource previous;
        lock (syncRoot)
        {
            if (disposed || navigation != null && !IsCurrentNavigationUnsafe(navigation))
            {
                lease = null;
                detailSourceRetirement = null;
                return false;
            }
            detailSourceRetirement = retireDetailSource
                ? playlistWorkspace.PrepareDetailSourceRetirementWithoutPublishing()
                : null;
            previous = currentCancellation;
            currentCancellation = new CancellationTokenSource();
            currentRequestId = MainViewBuildRequestSequence.Next();
            regularRequestActive = true;
            normalBuildCommits = null;
            normalBuildScoreChanges = null;
            normalBuildReferenceCommits = null;
            normalBuildLibrary = null;
            normalBuildMd5Keys.Clear();
            normalBuildSha256Keys.Clear();
            pendingNormalPresentation = null;
            lease = new RegularChartListRequestLease(currentRequestId, currentCancellation.Token, currentNavigation);
        }
        if (detailSourceRetirement != null)
        {
            playlistWorkspace.PublishDetailSourceRetirement(detailSourceRetirement);
        }
        CancelAndDispose(previous);
        if (Net10PerformanceLog.IsEnabled)
        {
            var performanceInteraction =
                PerformanceInteraction.Existing("normal_library", lease.RequestId);
            Net10PerformanceLog.Write(
                performanceInteraction,
                "input_accepted");
            Net10PerformanceLog.Write(performanceInteraction, "owner_queued");
        }
        return true;
    }

    internal bool TryBeginVirtualRequest(BMSLibrary library, out RegularChartListRequestLease lease)
    {
        return TryBeginVirtualRequest(
            library,
            retireDetailSource: false,
            out lease,
            out _);
    }

    private bool TryBeginVirtualRequest(
        BMSLibrary library,
        bool retireDetailSource,
        out RegularChartListRequestLease lease,
        out PlaylistSourceRetirementRequest detailSourceRetirement,
        long? expectedRequestId = null,
        RegularChartNavigationRequest navigation = null,
        bool resetDerivedCaches = false)
    {
        CancellationTokenSource previousRequest;
        CancellationTokenSource prewarmCancellation = null;
        lock (syncRoot)
        {
            if (disposed || navigation != null && !IsCurrentNavigationUnsafe(navigation)
                || expectedRequestId.HasValue && currentRequestId != expectedRequestId.Value)
            {
                lease = null;
                detailSourceRetirement = null;
                return false;
            }
            // packageのUI入口予約より後に選択が変わっていない場合だけ、破棄とlease発行を一緒に確定する。
            if (expectedRequestId.HasValue || resetDerivedCaches) { ResetDerivedCachesUnsafe(); }
            if (virtualSourceRowsLibraryReserved
                && !ReferenceEquals(virtualSourceRowsLibrary, library))
            {
                sourceGeneration++;
                ClearAllSortCachesUnsafe(clearSourceRows: true);
                prewarmCancellation = GetActivePrewarmCancellationUnsafe();
            }
            virtualSourceRowsLibrary = library;
            virtualSourceRowsLibraryReserved = true;
            detailSourceRetirement = retireDetailSource
                ? playlistWorkspace.PrepareDetailSourceRetirementWithoutPublishing()
                : null;
            previousRequest = currentCancellation;
            currentCancellation = new CancellationTokenSource();
            currentRequestId = MainViewBuildRequestSequence.Next();
            regularRequestActive = true;
            normalBuildLibrary = library;
            normalBuildCommits = [];
            normalBuildScoreChanges = [];
            normalBuildReferenceCommits = [];
            pendingNormalDelivery = Task.CompletedTask;
            pendingNormalScoreRequiredVersion = -1;
            pendingNormalCatalogRequiredRevision = -1;
            pendingNormalReferenceRequiredRevision = -1;
            normalBuildMd5Keys.Clear();
            normalBuildSha256Keys.Clear();
            pendingNormalPresentation = null;
            lease = new RegularChartListRequestLease(currentRequestId, currentCancellation.Token, currentNavigation);
        }
        if (detailSourceRetirement != null)
        {
            playlistWorkspace.PublishDetailSourceRetirement(detailSourceRetirement);
        }
        CancelAndDispose(previousRequest);
        Cancel(prewarmCancellation);
        if (Net10PerformanceLog.IsEnabled)
        {
            var performanceInteraction =
                PerformanceInteraction.Existing("normal_library", lease.RequestId);
            Net10PerformanceLog.Write(
                performanceInteraction,
                "input_accepted",
                "presentation=virtual");
            Net10PerformanceLog.Write(
                performanceInteraction,
                "owner_queued",
                "presentation=virtual");
        }
        return true;
    }

    /// <summary>現在のleaseを取り消し、leaseを発行しない画面切替も古いUI入口予約から区別します。</summary>
    internal void InvalidatePendingRequest()
    {
        CancellationTokenSource previous;
        lock (syncRoot)
        {
            previous = currentCancellation;
            currentCancellation = null;
            // leaseがないplaylist等への切替も、予約済みの古いUI入口と区別する。
            currentRequestId = MainViewBuildRequestSequence.Next();
            regularRequestActive = false;
        }
        CancelAndDispose(previous);
    }

    /// <summary>適用済み画面の並べ替えを同期的に受理します。開始前に登録した処理が、UI配送から一覧表示の完了までを所有します。</summary>
    /// <param name="request">既存の列・方向・区分を持つ整列入力です。</param>
    /// <param name="navigation">UI適用済み参照です。初期nullを最新へ取り直さず、同じ現在参照の間だけ有効です。</param>
    internal void QueueSort(MainChartListSortRequestedEventArgs request, RegularChartNavigationRequest navigation = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target != MainChartListSortTarget.Regular)
        { throw new ArgumentException("A regular chart-list sort request is required.", nameof(request)); }
        MainChartListSortRequestedEventArgs ownedRequest;
        TaskCompletionSource start = null;
        Task execution = null;
        CancellationTokenSource previousRequest;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsCurrentNavigationUnsafe(navigation)) { return; }
            var next = ChartListSortSpecification.Create(request.ColumnName, request.Direction, hasValue: true);
            if (currentSort.HasValue
                && string.Equals(currentSort.RequestedColumnName, next.RequestedColumnName, StringComparison.Ordinal)
                && currentSort.Direction == next.Direction) { return; }
            currentSort = next;
            ownedRequest = new MainChartListSortRequestedEventArgs(request.ColumnName, request.Direction, request.Target, ++sortRequestRevision);
            previousRequest = InvalidateCurrentRequestUnsafe();
            pendingSortRefresh = new RegularChartSortRefreshRequestedEventArgs(ownedRequest, navigation);
            if (sortRefreshWorker == null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task startTask = start.Task;
                execution = Task.Run(async () =>
                {
                    await startTask.ConfigureAwait(false);
                    await ProcessSortRefreshQueueAsync().ConfigureAwait(false);
                });
                sortRefreshWorker = execution;
                viewPreparationTasks.Add(execution);
            }
        }
        try
        {
            CancelAndDispose(previousRequest);
            SortChanged?.Invoke(this, ownedRequest);
        }
        finally
        {
            // 通知が失敗しても受理済み要求を開始する。ロック内でUIやTaskの完了を待たない。
            if (start != null)
            {
                execution.ObserveFault("regularChartListSortRequested");
                _ = ReleaseViewPreparationAsync(execution);
                start.SetResult();
            }
        }
    }

    internal bool CanBeginCellEdit(MainChartListCellEditContext context)
    {
        if (context == null || string.IsNullOrWhiteSpace(context.PropertyName))
        {
            return false;
        }
        if (string.Equals(context.PropertyName, nameof(LibraryChartRow.Folder), StringComparison.Ordinal))
        {
            return GridRowResolver.TryGetFolderEditChartOperationTarget(context.Row, context.SourceScope, out _);
        }
        return string.Equals(context.PropertyName, "instl_dst", StringComparison.Ordinal)
            && IsInstallDestinationEditSection(context.OperationSection)
            && GridRowResolver.TryGetChartOperationTarget(context.Row, context.SourceScope, out ChartOperationTarget target)
            && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination);
    }

    /// <summary>通常・保留セルの実編集Taskを返し、必要表示と受付終端を端末の所有者へ接続します。取消・対象外は変更しません。</summary>
    internal Task CompleteCellEdit(MainChartListCellEditEndedEventArgs request)
    {
        if (request == null || !request.Commit)
        {
            return Task.CompletedTask;
        }
        MainChartListCellEditContext context = request.Context;
        if (string.Equals(context.PropertyName, nameof(LibraryChartRow.Folder), StringComparison.Ordinal)
            && GridRowResolver.TryGetFolderEditChartOperationTarget(context.Row, context.SourceScope, out ChartOperationTarget folderTarget)
            && RenameChartFolderRequest.TryCreate(folderTarget, out RenameChartFolderRequest renameRequest))
        {
            return RenameChartFolderAsync(renameRequest, request.Text);
        }
        if (string.Equals(context.PropertyName, "instl_dst", StringComparison.Ordinal)
            && IsInstallDestinationEditSection(context.OperationSection)
            && GridRowResolver.TryGetChartOperationTarget(context.Row, context.SourceScope, out ChartOperationTarget installTarget)
            && installTarget.HasCapability(ChartOperationCapabilities.UpdateInstallDestination)
            && PendingInstallDestinationEditRequest.TryCreate(installTarget, out PendingInstallDestinationEditRequest installRequest))
        {
            return pendingPackageWorkflow.SetPendingAsync(installRequest, request.Text);
        }
        return Task.CompletedTask;
    }

    /// <summary>改名を対象確定前にLへ非待機受付し、実変更・必要公開・通知・cleanupの終端まで保持します。Busyで予約しません。</summary>
    internal Task RenameChartFolderAsync(RenameChartFolderRequest request, string newFolder)
    {
        if (request?.HasTarget != true || string.IsNullOrWhiteSpace(newFolder)) { return Task.CompletedTask; }
        Task renameTask = null;
        lock (syncRoot)
        {
            if (disposed || normalLibraryRefreshSource == null) { return Task.CompletedTask; }
            if (chartFileOperations.TryEnter(out IDisposable accepted))
            {
                BMSLibrary library = normalLibraryRefreshSource;
                renameTask = Task.Run(async () =>
                {
                    using (accepted)
                    {
                        await ExecuteFolderRenameAsync(library, request, newFolder, accepted).ConfigureAwait(false);
                    }
                });
                renameTask.ObserveFault("regularChartListFolderEditRequested");
                folderRenameTasks.Add(renameTask);
            }
        }
        if (renameTask == null) { return NotifyLibraryOperationBusyAsync(); }
        _ = renameTask.ContinueWith(task =>
        {
            lock (syncRoot) { folderRenameTasks.Remove(task); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return renameTask;
    }

    private async Task NotifyLibraryOperationBusyAsync(IUiDialogService dialogs = null)
    {
        dialogs ??= mutationDialogs;
        if (dialogs != null)
        {
            UiDialogResult result = await dialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(
                BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, BeMusicSeeker.Properties.Resources.Warning)).ConfigureAwait(false);
            UiDialogRoute.ThrowIfNotShown(result, "Folder rename Busy notification");
        }
    }

    private async Task ExecuteFolderRenameAsync(BMSLibrary library, RenameChartFolderRequest request, string newFolder, IDisposable operationGate)
    {
        using LibraryFileMutationCapability libraryCapability = chartFileOperations.CreateMutationCapability(operationGate);
        string directoryName = DirectoryExt.GetDirectoryNameSimple(request.Chart.Path);
        FolderMoveBatch batch = library.PrepareChartFolderRename(directoryName, newFolder);
        LibraryFileMutationLease playlistLease = null;
        if (IsCurrentLibrary(library) && !library.TryEnterManagedOutputMutation(batch.Moves.SelectMany(plan => new[] { plan.SourceDirectory, plan.DestinationDirectory }), true, out playlistLease, libraryCapability))
        { throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy); }
        using LibraryFileMutationLease acceptedPlaylist = playlistLease;
        using LibraryFileMutationCapability playlistCapability = playlistLease?.CreateMutationCapability();
        using LibraryFileMutationCapability capability = libraryCapability.WithPlaylistCapability(playlistCapability);
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        bool normalRefreshApplySuppressed = false;
        bool operationAdmitted = false;
        LibraryMutationSessionReceipt mutationReceipt = null;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            operationAdmitted = true;
            if (IsCurrentLibrary(library))
            {
                activityLease = chartMutationActivity.Enter();
                if (playback != null && batch.Moves.Count > 0) { await playback.StopPlaybackForMutationAsync().ConfigureAwait(false); }
                dialogScope = library.BeginOperationDialogScope();
                suppressionStarted = true;
                PublishRefreshSuppressionChanged(isSuppressed: true);
                if (!string.IsNullOrWhiteSpace(directoryName)
                    && LongPathFileSystem.DirectoryExists(directoryName))
                {
                    lock (syncRoot)
                    {
                        normalLibraryRefreshApplySuppressed = true;
                        normalRefreshApplySuppressed = true;
                    }
                    mutationReceipt = library.RenameChartFolderWithReceipt(directoryName, newFolder, false,
                        reportAtTerminal: mutationDialogs != null, capability: capability, preparedBatch: batch);
                    lock (syncRoot)
                    {
                        normalLibraryRefreshApplySuppressed = false;
                        normalRefreshApplySuppressed = false;
                    }
                    Exception mutationFailure = mutationReceipt?.PhysicalFailure
                        ?? mutationReceipt?.ApplyFailure
                        ?? mutationReceipt?.FinalizationFailure;
                    if (mutationFailure != null)
                    {
                        failures.Add(ExceptionDispatchInfo.Capture(mutationFailure));
                    }
                    if (mutationReceipt?.DurableCommit != true || mutationFailure != null)
                    {
                        logWarning(
                            "regular_chart_folder_rename_not_committed durable="
                            + (mutationReceipt?.DurableCommit == true).ToString().ToLowerInvariant()
                            + " candidatePaths="
                            + string.Join("|", mutationReceipt?.CandidatePaths ?? []));
                    }
                    if (mutationReceipt?.DurableCommit == true)
                    {
                        InvalidatePathMutationCaches(library);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            // 論理Lは外側が通知・cleanup終端まで保持し、短いmodel/DB保護だけを解放して公開します。
            if (normalRefreshApplySuppressed)
            {
                lock (syncRoot)
                {
                    normalLibraryRefreshApplySuppressed = false;
                }
            }
            Task requiredDelivery = mutationReceipt?.DurableCommit == true && IsCurrentLibrary(library)
                ? DeliverPendingLibraryChangesAsync(library, CancellationToken.None) : Task.CompletedTask;
            if (suppressionStarted)
            {
                CaptureNotification(() => PublishRefreshSuppressionChanged(isSuppressed: false));
            }
            if (activityLease != null)
            {
                CaptureNotification(activityLease.Dispose);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                CaptureNotification(dialogScope.Flush);
            }
            // ThreadStaticの短いdialog scopeを外してから非同期待機し、元workerへscopeを残しません。
            try
            {
                await requiredDelivery.ConfigureAwait(false);
                await library.CompleteAcceptedDuplicateDataAsync(capability).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                if (!IsCurrentLibrary(library)) { /* 表示所有者の停止・交換後は旧表示を必要条件にしません。 */ }
                else if (failures.Count == 0) { failures.Add(ExceptionDispatchInfo.Capture(failure)); }
                else
                {
                    try { logWarning("regular_chart_folder_rename_projection_failed " + failure); }
                    catch { /* 診断先の故障で先行する物理・出力の原因を置換しません。 */ }
                }
            }
            if (operationAdmitted)
            {
                CaptureNotification(mainChartList.RequestDisplayRefresh);
            }
        }
        if (mutationDialogs != null)
        {
            if (mutationReceipt != null)
            {
                await FileDbMutationReport.ShowAsync(mutationDialogs, BeMusicSeeker.Properties.Resources.FileDbMutationReport_Rename,
                    mutationReceipt,
                    failures.Count == 0 ? null : new AggregateException(failures.Select(failure => failure.SourceException)))
                    .ConfigureAwait(false);
            }
            else
            {
                await NotifyFolderRenameFailureAsync(failures).ConfigureAwait(false);
            }
        }
        switch (failures.Count)
        {
            case 0:
                return;
            case 1:
                failures[0].Throw();
                return;
            default:
                throw new AggregateException(failures.Select(failure => failure.SourceException));
        }

        void CaptureNotification(Action notification)
        {
            if (mutationDialogs != null)
            {
                FileDbMutationReport.NotifyBestEffort(notification);
            }
            else
            {
                CaptureCleanupFailure(notification, failures);
            }
        }
    }

    private async Task NotifyFolderRenameFailureAsync(IReadOnlyCollection<ExceptionDispatchInfo> failures)
    {
        Exception[] operationFailures = [..
            failures
                .Select(failure => failure.SourceException)
                .Where(exception => exception is not OperationCanceledException)];
        if (operationFailures.Length == 0)
        {
            return;
        }

        string details = string.Join(
            Environment.NewLine,
            operationFailures.Select(exception => FileDbMutationReport.Limit(exception.Message, 400)));
        var request = UiMessageRequest.CreateError(
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + details,
            BeMusicSeeker.Properties.Resources.Error);
        try
        {
            UiDialogResult result = await mutationDialogs.ShowMessageAsync(request).ConfigureAwait(false);
            if (result == null || result.Status is not (UiDialogStatus.Accepted
                or UiDialogStatus.Rejected
                or UiDialogStatus.CancelledByUser
                or UiDialogStatus.ClosedByUser))
            {
                FileDbMutationReport.LogNotificationFailure(result?.Exception
                    ?? new InvalidOperationException("Folder rename failure was not displayed."));
            }
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }

    private void PublishRefreshSuppressionChanged(bool isSuppressed)
    {
        RefreshSuppressionChanged?.Invoke(
            this,
            new RegularChartMutationRefreshSuppressionChangedEventArgs(isSuppressed));
    }

    private static void CaptureCleanupFailure(Action cleanup, List<ExceptionDispatchInfo> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
    }

    private void InvalidatePathMutationCaches(BMSLibrary library)
    {
        lock (normalLibraryRefreshApplyLock)
        {
            lock (syncRoot)
            {
                if (disposed || !ReferenceEquals(normalLibraryRefreshSource, library))
                {
                    return;
                }
            }
            InvalidateIdentitySortKeys(clearSourceRows: false);
        }
    }

    private bool IsCurrentLibrary(BMSLibrary library)
    {
        lock (syncRoot)
        {
            return !disposed && ReferenceEquals(normalLibraryRefreshSource, library);
        }
    }

    private static bool IsInstallDestinationEditSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.InstallPending
            || section == MainViewOperationSection.FullScanCheck;
    }

    private async Task ProcessSortRefreshQueueAsync()
    {
        ExceptionDispatchInfo failure = null;
        while (true)
        {
            RegularChartSortRefreshRequestedEventArgs request;
            lock (syncRoot)
            {
                request = pendingSortRefresh;
                pendingSortRefresh = null;
                if (request == null || disposed)
                {
                    // 次要求の登録と同じロック内で追跡を外し、処理の所有が途切れる再起動を避ける。
                    sortRefreshWorker = null;
                    break;
                }
            }
            try
            {
                await normalLibraryRefreshUiScheduler.InvokeAsync(() =>
                {
                    if (IsCurrentSortRequest(request)
                        && mainChartList.CurrentOperationContext.OperationSection != MainViewOperationSection.PlayHistory
                        && !(playlistWorkspace.IsPlaylistDetailViewActive && !playlistWorkspace.IsPlaylistSummaryMode))
                    { SortRefreshRequested?.Invoke(this, request); }
                }).ConfigureAwait(false);
            }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
            try
            {
                // 後続購読先が失敗しても、先にVMが開始した同じ表示Taskの終端を所有する。
                await request.Presentation.ConfigureAwait(false);
            }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        failure?.Throw();
    }

    /// <summary>UI入力を読む前に、並べ替えを受理した時点の画面受付と整列版が現在も有効かを確認します。</summary>
    internal bool IsCurrentSortRequest(RegularChartSortRefreshRequestedEventArgs request)
    {
        lock (syncRoot)
        { return !disposed && IsCurrentNavigationUnsafe(request.Navigation) && IsCurrentSortRequest(request.SortRequest); }
    }

    internal bool IsCurrentSortRequest(MainChartListSortRequestedEventArgs request)
    {
        lock (syncRoot)
        {
            return request.OwnerRevision == sortRequestRevision
                && currentSort.HasValue
                && string.Equals(currentSort.RequestedColumnName, request.ColumnName, StringComparison.Ordinal)
                && currentSort.Direction == request.Direction;
        }
    }

    internal bool IsCurrentRegularRows(IList expectedRows)
    {
        lock (syncRoot)
        {
            return regularRequestActive && ReferenceEquals(mainChartList.Rows, expectedRows);
        }
    }

    internal bool HasFolderRows
    {
        get
        {
            lock (syncRoot)
            {
                return folderRows != null;
            }
        }
    }

    internal bool HasKeywordRows
    {
        get
        {
            lock (syncRoot)
            {
                return keywordRows != null;
            }
        }
    }

    internal bool HasModeRows
    {
        get
        {
            lock (syncRoot)
            {
                return modeRows != null;
            }
        }
    }

    internal IReadOnlyList<LibraryChartRow> SnapshotRows()
    {
        return rowCache.SnapshotRows();
    }

    private NormalLibraryRefreshNotificationBatch ConsumeNormalLibraryRefreshNotification(BMSLibrary library)
    {
        if (library == null)
        {
            return NormalLibraryRefreshNotificationBatch.Empty;
        }

        NormalLibraryRefreshNotificationBatch notificationBatch =
            library.GetNormalLibraryRefreshNotificationsAfter(normalLibraryRefreshHandledNotificationVersion);
        if (notificationBatch == null)
        {
            return NormalLibraryRefreshNotificationBatch.Empty;
        }

        lock (syncRoot)
        {
            if (disposed
                || !ReferenceEquals(normalLibraryRefreshSource, library)
                || notificationBatch.LatestVersion <= normalLibraryRefreshHandledNotificationVersion)
            {
                return NormalLibraryRefreshNotificationBatch.Empty;
            }
        }
        return notificationBatch;
    }

    private void ApplyNormalLibraryRefreshNotificationBatch(
        BMSLibrary library,
        NormalLibraryRefreshNotificationBatch notificationBatch,
        string reason)
    {
        if (library == null || notificationBatch?.HasRefreshNotification != true)
        {
            return;
        }

        IReadOnlyList<ChartFile> installDestinationChangedCharts = notificationBatch.InstallDestinationChangedCharts ?? [];
        bool installDestinationStateChanged = notificationBatch.ResetsPriorNotifications
            || notificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged);
        if (notificationBatch.ResetsPriorNotifications)
        {
            mainChartList.RowProjection.ClearTransientStates();
        }
        if (installDestinationChangedCharts.Count > 0)
        {
            mainChartList.RowProjection.UpdateTransientStates(
                installDestinationChangedCharts,
                forceInstallDestinationProjection: true);
            mainChartList.RowProjection.PruneTransientStatesToOwnedCharts(library);
        }
        else if (installDestinationStateChanged)
        {
            mainChartList.RowProjection.PruneTransientStatesToOwnedCharts(library);
        }

        if (mainChartList.Rows is ChartListVirtualView { NormalInput: NormalLibrarySourceRows normalInput }
            && ReferenceEquals(normalInput.Library, library)
            && (notificationBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged)
                || notificationBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged)))
        {
            // Deferで実際に無効化された専門索引は局所キー比較より前に準備する。
            // 通常viewが全体再構築を省略しても、警告提供元の必要な構築は省略しない。
            PrepareResourceHealthIndexForView(library, MainViewUpdateMode.FolderFilterSelected, "normal_library_batch_projection");
        }
        ApplyNormalLibraryChartChanges(library, notificationBatch);
        ApplyScoreChanges(library, notificationBatch.ScoreChanges);
        mainChartList.RowProjection.CaptureVersions(library);
        foreach (LibraryChartRow row in rowCache.GetRowsForScoreKeys(notificationBatch.ChangedDetailMd5s, notificationBatch.ChangedDetailSha256s))
        {
            row.RefreshDisplayForDataDependency(MainViewDataDependency.ChartInfo);
        }
        ApplyNormalLibraryRefreshNotificationEffects(notificationBatch, reason);
        if (notificationBatch.ResetsPriorNotifications)
        {
            ResetDerivedCaches();
        }
        NormalLibraryRefreshApplied?.Invoke(
            this,
            new NormalLibraryRefreshAppliedEventArgs(notificationBatch, reason));
    }

    /// <summary>現在の表示条件で全batch依存を一度判断します。非依存表示ではRowsと編集を保持します。</summary>
    /// <param name="library">現在のライブラリと既存の警告・保守索引の所有者。</param>
    /// <param name="batch">今回消費する全通知の変更事実。</param>
    /// <param name="mode">現在の表示モード。</param>
    /// <param name="selectedPackage">選択中の導入済み所属。全所属表示ではnull。</param>
    /// <param name="filters">現在の検索・モード条件。</param>
    /// <param name="playlistDetail">プレイリスト詳細を表示しているか。</param>
    /// <returns>表示更新はここで公開し、一覧更新は画面の既存接続へ返します。</returns>
    internal MainViewRefreshAction ApplyNotificationPresentation(
        BMSLibrary library, NormalLibraryRefreshNotificationBatch batch, MainViewUpdateMode mode,
        ChartPackage selectedPackage, ChartListFilterSnapshot filters, bool playlistDetail)
    {
        if (!playlistDetail && !batch.ResetsPriorNotifications
            && mainChartList.Rows is ChartListVirtualView duplicate && duplicate.UsesCatalogCurrentValues
            && ReferenceEquals(duplicate.ScoreLibrary, library) && library.DuplicateChartGroups != null
            && batch.ChangedDetailMd5s.Count == 0 && batch.ChangedDetailSha256s.Count == 0
            && batch.InstallDestinationChangedCharts.Count == 0)
        {
            // 共通値・検索・順序は配送済みの実差分で更新する。重複の所属と選択キーは検索時のまま保つ。
            mainChartList.UpdateSummaryText(duplicate);
            mainChartList.RequestDisplayRefresh();
            return MainViewRefreshAction.RefreshDisplay;
        }
        if (!playlistDetail && mainChartList.Rows is ChartListVirtualView normal && normal.NormalInput != null
            && ReferenceEquals(normal.NormalInput.Library, library) && !batch.ResetsPriorNotifications
            && (normal.HasPendingReferenceChanges || batch.CatalogCommits.All(commit => commit.Revision <= normal.NormalInput.Snapshot.CatalogRevision)))
        {
            // カタログ差分は配送時にすでに対象だけへ反映済みです。ここでは画面公開だけを接続します。
            mainChartList.UpdateSummaryText(normal);
            mainChartList.RequestDisplayRefresh();
            return MainViewRefreshAction.RefreshDisplay;
        }
        MainViewRefreshAction action = MainViewRefreshDecisionService.BuildNotificationBatch(
            batch, mode, selectedPackage, HasTreeFilter, filters.KeywordFilter, filters.ModeFilter,
            CaptureSortParameters()?.ColumnsName, playlistDetail);
        if (action == MainViewRefreshAction.Refresh)
        {
            ResetDerivedCaches();
            return action;
        }
        if (batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged)
            || batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged))
        {
            PrepareResourceHealthIndexForView(library, mode, "normal_library_batch_display");
        }
        if (mainChartList.Rows is ChartListVirtualView view)
        {
            // 専用対象集合は既存のpackage/警告投影を維持します。通常一覧の差分経路とは分けます。
            view.ForEachRealizedRow(row =>
            {
                if (batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged))
                { row.RefreshDisplayForDataDependency(MainViewDataDependency.Warning); }
                if (batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged))
                { row.RefreshDisplayForDataDependency(MainViewDataDependency.Maintenance); }
                if (batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged))
                { row.RefreshDisplayForDataDependency(MainViewDataDependency.InstallDestination); }
                if (batch.ChangedDetailMd5s.Count > 0 || batch.ChangedDetailSha256s.Count > 0)
                { row.RefreshDisplayForDataDependency(MainViewDataDependency.ChartInfo); }
            });
        }
        mainChartList.RequestDisplayRefresh();
        return action;
    }

    /// <summary>既存通知が捕捉した共通値と退役tokenだけを、通常一覧のcacheへ反映します。</summary>
    internal void ApplyNormalLibraryChartChanges(BMSLibrary library, NormalLibraryRefreshNotificationBatch notification)
    {
        if (notification.ResetsPriorNotifications)
        {
            rowCache.Clear();
            lock (syncRoot)
            {
                ClearVirtualSourceRowsUnsafe();
            }
            return;
        }
        ApplyIncrementalCatalogNotification(library, notification);
        if (notification.CatalogCommits.Count > 0) { return; }
        rowCache.ApplyChanges(notification.ChangedCharts, notification.DeletedTokens,
            (row, chart) =>
            {
                if (mainChartList.Rows is ChartListVirtualView view
                    && ReferenceEquals(view.NormalInput?.Library, library) && view.IsRealizedNormalRow(chart.Token, row))
                {
                    // 同じ確定差分で接続した捕捉入力を保持し、対象の再投影と最新lookupへの付替えを避けます。
                    return;
                }
                mainChartList.RowProjection.ConfigureLibraryRow(library, row);
            });
    }

    private void ApplyNormalLibraryRefreshNotificationEffects(
        NormalLibraryRefreshNotificationBatch notificationBatch,
        string reason)
    {
        if (!notificationBatch.ResetsPriorNotifications && notificationBatch.CatalogCommits.Count > 0)
        {
            // 正本版では失効させません。実キー比較と対象更新は上の差分適用が所有します。
            return;
        }
        int sourceCacheCount = 0;
        bool sourceGenerationChanged = notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged)
            && TryInvalidateSourceForCatalogRevision(
                notificationBatch.CatalogRevision,
                out sourceCacheCount,
                clearSourceRows: notificationBatch.ResetsPriorNotifications);
        if (notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged))
        {
            LogNormalLibrarySortCacheInvalidation("source", reason, sourceCacheCount);
        }

        bool installDestinationStateChanged = notificationBatch.HasEffect(
            LibraryChartRefreshEffects.InstallDestinationOverlayChanged);
        if (!sourceGenerationChanged && installDestinationStateChanged)
        {
            InvalidateNormalLibrarySortDependency(
                MainViewDataDependency.InstallDestination,
                "install_destination_changed");
        }
        if (notificationBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged))
        {
            InvalidateNormalLibrarySortDependency(MainViewDataDependency.Warning, "warning_changed");
        }
        if (notificationBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged))
        {
            InvalidateNormalLibrarySortDependency(MainViewDataDependency.Maintenance, "maintenance_changed");
        }
    }

    private void InvalidateNormalLibrarySortDependency(MainViewDataDependency dependency, string reason)
    {
        int removedCount = InvalidateSortCacheByDependency(dependency, out int cacheCount);
        LogNormalLibrarySortCacheInvalidation("dependency", reason, cacheCount, dependency, removedCount);
    }

    private void InvalidateNormalLibrarySortKeysForBmsonSync(
        BmsonLibraryRowCacheSyncResult result,
        string reason = null)
    {
        if (!result.SortKeyChanged)
        {
            return;
        }

        string invalidationReason = result.SourceIdentityChanged
            ? MainViewRefreshDecisionService.NormalLibraryBmsonSourceIdentityChangedReason
            : (string.IsNullOrWhiteSpace(reason)
                ? MainViewRefreshDecisionService.NormalLibraryBmsonSortKeyChangedReason
                : reason);
        bool clearSourceRows = MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange(invalidationReason);
        int cacheCount = InvalidateIdentitySortKeys(clearSourceRows);
        LogNormalLibrarySortCacheInvalidation("sort_key", invalidationReason, cacheCount);
    }

    private void LogNormalLibrarySortCacheInvalidation(string reason, string detail, int cacheCountBefore)
    {
        log("normal_library_sort_cache_invalidate reason=" + (reason ?? string.Empty)
            + " detail=" + (detail ?? string.Empty)
            + " cacheCountBefore=" + cacheCountBefore
            + " sourceGeneration=" + SourceGeneration
            + " sortKeyGeneration=" + SortKeyGeneration);
    }

    private void LogNormalLibrarySortCacheInvalidation(
        string reason,
        string detail,
        int cacheCountBefore,
        MainViewDataDependency dependency,
        int removedCount)
    {
        log("normal_library_sort_cache_invalidate reason=" + (reason ?? string.Empty)
            + " detail=" + (detail ?? string.Empty)
            + " dependency=" + dependency
            + " cacheCountBefore=" + cacheCountBefore
            + " removed=" + removedCount
            + " sourceGeneration=" + SourceGeneration
            + " sortKeyGeneration=" + SortKeyGeneration
            + " warningGeneration=" + WarningGeneration
            + " installDestinationGeneration=" + InstallDestinationGeneration
            + " maintenanceGeneration=" + MaintenanceGeneration
            + " referenceTablesGeneration=" + ReferenceTablesGeneration);
    }

    internal LibraryChartRow CreateVirtualRow(BMSLibrary library, ChartListSourceRow sourceRow)
    {
        if (sourceRow == null)
        {
            return null;
        }
        LibraryChartRow row = rowCache.GetOrCreate(sourceRow.Chart, null)
            ?? LibraryChartRow.FromChartFile(sourceRow.Chart);
        mainChartList.RowProjection.ConfigureLibraryRow(library, row, sourceRow);
        return row;
    }

    internal void ResetDerivedCaches()
    {
        InvalidatePendingRequest();
        lock (syncRoot) { ResetDerivedCachesUnsafe(); }
    }

    private void ResetDerivedCachesUnsafe()
    {
        folderRows = null;
        keywordRows = null;
        modeRows = null;
        folderSortSourceSnapshot = null;
        folderSortResultSnapshot = null;
        folderSortColumnName = null;
        folderSortDirection = null;
    }

    internal void ClearSortCache()
    {
        CancellationTokenSource previous;
        CancellationTokenSource prewarmCancellation;
        lock (syncRoot)
        {
            previous = InvalidateCurrentRequestUnsafe();
            sortKeyGeneration++;
            ClearAllSortCachesUnsafe(clearSourceRows: true);
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
    }

    internal bool IsVirtualOrderPrewarmRunning
    {
        get
        {
            lock (syncRoot)
            {
                return !virtualOrderPrewarmCompletion.IsCompleted;
            }
        }
    }

    internal bool TryBeginVirtualOrderPrewarm(BMSLibrary library, out RegularChartListPrewarmLease lease)
    {
        CancellationTokenSource previousRequest = null;
        CancellationTokenSource activePrewarmToCancel = null;
        CancellationTokenSource completedPrewarmToDispose = null;
        bool started = false;
        lock (syncRoot)
        {
            if (disposed)
            {
                lease = null;
                return false;
            }
            if (virtualSourceRowsLibraryReserved
                && !ReferenceEquals(virtualSourceRowsLibrary, library))
            {
                sourceGeneration++;
                previousRequest = InvalidateCurrentRequestUnsafe();
                ClearAllSortCachesUnsafe(clearSourceRows: true);
                activePrewarmToCancel = GetActivePrewarmCancellationUnsafe();
            }
            virtualSourceRowsLibrary = library;
            virtualSourceRowsLibraryReserved = true;
            if (!virtualOrderPrewarmCompletion.IsCompleted)
            {
                lease = null;
            }
            else
            {
                completedPrewarmToDispose = virtualOrderPrewarmCancellation;
                virtualOrderPrewarmCancellation = new CancellationTokenSource();
                lease = new RegularChartListPrewarmLease(
                    ++virtualOrderPrewarmRunId,
                    virtualOrderPrewarmCancellation.Token,
                    library);
                virtualOrderPrewarmLease = lease;
                virtualOrderPrewarmCompletion = lease.Completion;
                started = true;
            }
        }
        CancelAndDispose(previousRequest);
        Cancel(activePrewarmToCancel);
        CancelAndDispose(completedPrewarmToDispose);
        return started;
    }

    /// <summary>
    /// Cancels and completes the exact active virtual-order prewarm lease.
    /// Stale leases cannot cancel a newer run.
    /// </summary>
    /// <param name="lease">The active lease issued by this owner.</param>
    /// <returns><see langword="true"/> when the active lease was cancelled.</returns>
    internal bool CancelVirtualOrderPrewarm(RegularChartListPrewarmLease lease)
    {
        if (lease == null)
        {
            return false;
        }

        CancellationTokenSource cancellation = null;
        lock (syncRoot)
        {
            if (!ReferenceEquals(virtualOrderPrewarmLease, lease))
            {
                return false;
            }
            cancellation = virtualOrderPrewarmCancellation;
            virtualOrderPrewarmLease = null;
        }
        Cancel(cancellation);
        lease.Dispose();
        return true;
    }

    internal static int ResolveVirtualOrderPrewarmDegree(int descriptorCount)
    {
        if (descriptorCount <= 1)
        {
            return 1;
        }
        int processorDegree = Math.Max(1, Environment.ProcessorCount - 1);
        return Math.Max(1, Math.Min(Math.Min(processorDegree, 4), descriptorCount));
    }

    internal void RunVirtualOrderPrewarm(
        RegularChartListPrewarmLease lease,
        BMSLibrary library,
        bool includeBmsonRows,
        IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors,
        string reason)
    {
        if (lease == null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        var stopwatch = Stopwatch.StartNew();
        int descriptorCount = descriptors?.Count ?? 0;
        int degree = ResolveVirtualOrderPrewarmDegree(descriptorCount);
        int cacheHitCount = 0;
        int builtCount = 0;
        int staleSkippedCount = 0;
        int rowCount = 0;
        long sourceGenerationAtLookup = 0L;
        long sortKeyGenerationAtLookup = 0L;
        try
        {
            lease.Token.ThrowIfCancellationRequested();
            log("virtual_order_prewarm start reason=" + (reason ?? string.Empty)
                + " runId=" + lease.RunId
                + " descriptorCount=" + descriptorCount
                + " degree=" + degree);
            if (descriptorCount == 0)
            {
                log("virtual_order_prewarm skipped reason=" + (reason ?? string.Empty) + " descriptorCount=0");
                return;
            }
            IReadOnlyList<ChartListSourceRow> sourceRows = GetOrCreateVirtualNormalLibrarySourceRows(
                library,
                includeBmsonRows,
                out bool sourceRowsCacheHit,
                out sourceGenerationAtLookup,
                out sortKeyGenerationAtLookup);
            rowCount = sourceRows.Count;
            if (!IsCurrentVirtualGeneration(sourceGenerationAtLookup, sortKeyGenerationAtLookup))
            {
                staleSkippedCount = descriptorCount;
                return;
            }

            foreach (IGrouping<int, VirtualNormalLibrarySortDescriptor> stage in (descriptors ?? [])
                .GroupBy(descriptor => descriptor.PrewarmPriority)
                .OrderBy(group => group.Key))
            {
                VirtualNormalLibrarySortDescriptor[] stageDescriptors = [.. stage];
                int stageCacheHitCount = 0;
                int stageBuiltCount = 0;
                int stageStaleDetected = 0;
                int stageDegree = ResolveVirtualOrderPrewarmDegree(stageDescriptors.Length);
                if (!IsCurrentVirtualGeneration(sourceGenerationAtLookup, sortKeyGenerationAtLookup))
                {
                    staleSkippedCount += stageDescriptors.Length;
                    break;
                }

                Parallel.ForEach(
                    stageDescriptors,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = stageDegree,
                        CancellationToken = lease.Token
                    },
                    (descriptor, loopState) =>
                    {
                        if (!IsCurrentVirtualGeneration(sourceGenerationAtLookup, sortKeyGenerationAtLookup))
                        {
                            Interlocked.Exchange(ref stageStaleDetected, 1);
                            loopState.Stop();
                            return;
                        }
                        _ = GetOrCreateVirtualNormalLibraryOrder(
                            sourceRows,
                            library,
                            descriptor.ColumnName,
                            descriptor.Direction,
                            sourceGenerationAtLookup,
                            sortKeyGenerationAtLookup,
                            CaptureExternalVersions(library, default),
                            useCache: true,
                            out bool cacheHit,
                            out _,
                            out _,
                            out _);
                        if (cacheHit)
                        {
                            Interlocked.Increment(ref stageCacheHitCount);
                        }
                        else
                        {
                            Interlocked.Increment(ref stageBuiltCount);
                        }
                    });
                cacheHitCount += stageCacheHitCount;
                builtCount += stageBuiltCount;
                if (Volatile.Read(ref stageStaleDetected) != 0
                    || !IsCurrentVirtualGeneration(sourceGenerationAtLookup, sortKeyGenerationAtLookup))
                {
                    staleSkippedCount += Math.Max(0, stageDescriptors.Length - stageCacheHitCount - stageBuiltCount);
                    break;
                }
            }

            log("virtual_order_prewarm done reason=" + (reason ?? string.Empty)
                + " runId=" + lease.RunId
                + " descriptorCount=" + descriptorCount
                + " rowCount=" + rowCount
                + " sourceRowsReuse=" + sourceRowsCacheHit
                + " cacheHit=" + cacheHitCount
                + " built=" + builtCount
                + " staleSkipped=" + staleSkippedCount
                + " sourceGeneration=" + sourceGenerationAtLookup
                + " sortKeyGeneration=" + sortKeyGenerationAtLookup
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
            log("virtual_order_prewarm cancelled reason=" + (reason ?? string.Empty)
                + " runId=" + lease.RunId
                + " descriptorCount=" + descriptorCount
                + " rowCount=" + rowCount
                + " cacheHit=" + cacheHitCount
                + " built=" + builtCount
                + " staleSkipped=" + staleSkippedCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            log("virtual_order_prewarm failed reason=" + (reason ?? string.Empty)
                + " runId=" + lease.RunId
                + " descriptorCount=" + descriptorCount
                + " rowCount=" + rowCount
                + " cacheHit=" + cacheHitCount
                + " built=" + builtCount
                + " staleSkipped=" + staleSkippedCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name);
        }
    }

    /// <summary>指定した上限優先度までの通常一覧ソートを、既存の優先度内順序で昇降順とも選びます。0は準備しません。</summary>
    /// <exception cref="ArgumentOutOfRangeException">上限が0～3の範囲外です。</exception>
    internal static IReadOnlyList<VirtualNormalLibrarySortDescriptor> CreateVirtualOrderPrewarmDescriptors(int maxPrewarmPriority)
    {
        if (maxPrewarmPriority is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPrewarmPriority), maxPrewarmPriority, "Prewarm priority must be between 0 and 3.");
        }
        return [.. ChartListOrder.GetVirtualSortColumnMetadata()
            .Select((column, index) => new { Column = column, Index = index })
            .Where(item => item.Column.PrewarmPriority > 0)
            .Where(item => item.Column.PrewarmPriority <= maxPrewarmPriority)
            .OrderBy(item => item.Column.PrewarmPriority)
            .ThenBy(item => GetVirtualOrderPrewarmOrder(item.Column.NormalizedColumnName))
            .ThenBy(item => item.Index)
            .SelectMany(item => new[]
            {
                new VirtualNormalLibrarySortDescriptor(item.Column.NormalizedColumnName, ListSortDirection.Ascending, item.Column.PrewarmPriority),
                new VirtualNormalLibrarySortDescriptor(item.Column.NormalizedColumnName, ListSortDirection.Descending, item.Column.PrewarmPriority)
            })];
    }

    private static int GetVirtualOrderPrewarmOrder(string columnName)
    {
        return columnName switch
        {
            nameof(LibraryChartRow.Title) => 0,
            nameof(LibraryChartRow.Folder) => 1,
            nameof(LibraryChartRow.path) => 2,
            nameof(LibraryChartRow.Artist) => 3,
            nameof(LibraryChartRow.clear) => 0,
            nameof(LibraryChartRow.rateDouble) => 1,
            nameof(LibraryChartRow.minbp) => 2,
            nameof(LibraryChartRow.ChartJudgeSortKey) => 3,
            nameof(LibraryChartRow.ChartNotes) => 4,
            nameof(LibraryChartRow.ChartLongNotes) => 5,
            nameof(LibraryChartRow.ChartScratchNotes) => 6,
            nameof(LibraryChartRow.ChartMainBpmSortKey) => 7,
            nameof(LibraryChartRow.ChartMinBpmSortKey) => 8,
            nameof(LibraryChartRow.ChartMaxBpmSortKey) => 9,
            nameof(LibraryChartRow.ChartSoflanCount) => 10,
            nameof(LibraryChartRow.ChartTotalSortKey) => 11,
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey) => 12,
            nameof(LibraryChartRow.ChartDurationSortKey) => 13,
            nameof(LibraryChartRow.ChartDensitySortKey) => 14,
            nameof(LibraryChartRow.ChartPeakDensitySortKey) => 15,
            nameof(LibraryChartRow.ChartEndDensitySortKey) => 16,
            _ => int.MaxValue,
        };
    }

    internal int CacheCount
    {
        get
        {
            lock (syncRoot)
            {
                return GetCacheCountUnsafe();
            }
        }
    }

    internal long SourceGeneration
    {
        get
        {
            lock (syncRoot)
            {
                return sourceGeneration;
            }
        }
    }

    internal long SortKeyGeneration
    {
        get
        {
            lock (syncRoot)
            {
                return sortKeyGeneration;
            }
        }
    }

    internal long WarningGeneration => ReadGeneration(() => warningGeneration);

    internal long InstallDestinationGeneration => ReadGeneration(() => installDestinationGeneration);

    internal long MaintenanceGeneration => ReadGeneration(() => maintenanceGeneration);

    /// <summary>診断用の通常元集合の反映済み参照位置です。独立した手動世代は持ちません。</summary>
    internal long ReferenceTablesGeneration => ReadGeneration(() => virtualSourceRows?.References.Revision ?? 0);

    internal int InvalidateSource()
    {
        CancellationTokenSource previous;
        CancellationTokenSource prewarmCancellation;
        int cacheCount;
        lock (syncRoot)
        {
            cacheCount = GetCacheCountUnsafe();
            sourceGeneration++;
            previous = InvalidateCurrentRequestUnsafe();
            ClearAllSortCachesUnsafe(clearSourceRows: true);
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
        return cacheCount;
    }

    internal bool TryInvalidateSourceForCatalogRevision(int currentVersion, out int cacheCount, bool clearSourceRows = true)
    {
        CancellationTokenSource previous = null;
        CancellationTokenSource prewarmCancellation;
        lock (syncRoot)
        {
            if (currentVersion > 0 && currentVersion == handledCatalogRevision)
            {
                cacheCount = 0;
                return false;
            }
            handledCatalogRevision = currentVersion;
            cacheCount = GetCacheCountUnsafe();
            sourceGeneration++;
            previous = InvalidateCurrentRequestUnsafe();
            ClearAllSortCachesUnsafe(clearSourceRows);
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
        return true;
    }

    internal void InvalidateVirtualSourceRows()
    {
        _ = InvalidateSource();
    }

    internal int InvalidateIdentitySortKeys(bool clearSourceRows)
    {
        CancellationTokenSource previous;
        CancellationTokenSource prewarmCancellation;
        int cacheCount;
        lock (syncRoot)
        {
            cacheCount = GetCacheCountUnsafe();
            sortKeyGeneration++;
            previous = InvalidateCurrentRequestUnsafe();
            sortCache.Clear();
            virtualOrderCache.Clear();
            virtualSubsetOrderCache.Clear();
            if (clearSourceRows)
            {
                ClearVirtualSourceRowsUnsafe();
            }
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
        return cacheCount;
    }

    internal static bool IsSortCacheCandidate(string columnName)
    {
        return ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out _);
    }

    internal int InvalidateSortCacheByDependency(MainViewDataDependency dependency, out int cacheCount)
    {
        CancellationTokenSource previous;
        CancellationTokenSource prewarmCancellation;
        int removed;
        lock (syncRoot)
        {
            IncrementDependencyGenerationUnsafe(dependency);
            cacheCount = GetCacheCountUnsafe();
            previous = InvalidateCurrentRequestUnsafe();
            removed = PruneCacheUnsafe(sortCache, dependency)
                + PruneCacheUnsafe(virtualOrderCache, dependency)
                + PruneCacheUnsafe(virtualSubsetOrderCache, dependency);
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
        return removed;
    }

    private RegularVirtualSourceRowsLookup LookupVirtualSourceRows(BMSLibrary library, bool includeBmsonRows)
    {
        lock (syncRoot)
        {
            if (!virtualSourceRowsLibraryReserved)
            {
                virtualSourceRowsLibrary = library;
                virtualSourceRowsLibraryReserved = true;
            }
            bool identityMatches = ReferenceEquals(virtualSourceRowsLibrary, library);
            bool cacheHit = identityMatches
                && virtualSourceRows != null
                && virtualSourceRowsGeneration == sourceGeneration
                && virtualSourceRows.IncludeBmsonRows == includeBmsonRows;
            return new RegularVirtualSourceRowsLookup(
                library,
                includeBmsonRows,
                identityMatches ? sourceGeneration : -1L,
                sortKeyGeneration,
                cacheHit ? virtualSourceRows : null,
                cacheHit);
        }
    }

    private void TryPublishVirtualSourceRows(
        RegularVirtualSourceRowsLookup lookup,
        NormalLibrarySourceRows rows)
    {
        lock (syncRoot)
        {
            if (disposed
                || !rows.IsCurrent
                || !virtualSourceRowsLibraryReserved
                || !ReferenceEquals(virtualSourceRowsLibrary, lookup.Library)
                || sourceGeneration != lookup.SourceGeneration
                || sortKeyGeneration != lookup.SortKeyGeneration)
            {
                return;
            }
            virtualSourceRows = rows;
            virtualSourceRowsLibrary = lookup.Library;
            virtualSourceRowsGeneration = lookup.SourceGeneration;
        }
    }

    internal NormalLibrarySortCacheGenerationSnapshot CaptureSortGeneration(
        string columnName,
        RegularChartListExternalVersions externalVersions)
    {
        lock (syncRoot)
        {
            ResolveDependencyGenerationsUnsafe(
                columnName,
                externalVersions,
                out long score,
                out long chartInfo,
                out long maintenance,
                out long warning,
                out long installDestination,
                out long referenceTables);
            return new NormalLibrarySortCacheGenerationSnapshot(
                sourceGeneration,
                sortKeyGeneration,
                score,
                chartInfo,
                maintenance,
                warning,
                installDestination,
                referenceTables);
        }
    }

    /// <summary>整列に使った元集合の参照位置をcache識別へ含めます。最新索引位置へ読み替えません。</summary>
    internal NormalLibrarySortCacheKey CreateVirtualOrderKey(
        long expectedSourceGeneration,
        long expectedSortKeyGeneration,
        string columnName,
        ListSortDirection direction,
        int rowCount,
        RegularChartListExternalVersions externalVersions, long? referencePosition = null)
    {
        lock (syncRoot)
        {
            ResolveDependencyGenerationsUnsafe(
                columnName,
                externalVersions,
                out long score,
                out long chartInfo,
                out long maintenance,
                out long warning,
                out long installDestination,
                out long referenceTables);
            return new NormalLibrarySortCacheKey(
                expectedSourceGeneration,
                expectedSortKeyGeneration,
                score,
                chartInfo,
                maintenance,
                warning,
                installDestination,
                referencePosition.HasValue && MainViewRefreshDecisionService.GetSortColumnDependency(columnName) == MainViewDataDependency.ReferenceTables
                    ? referencePosition.Value : referenceTables,
                columnName,
                direction,
                rowCount);
        }
    }

    /// <summary>専用集合の同じ捕捉参照位置を整列cacheへ接続します。通常元集合の位置とは独立です。</summary>
    internal VirtualChartSubsetSortCacheKey CreateVirtualSubsetOrderKey(
        long expectedSourceGeneration,
        long expectedSortKeyGeneration,
        int treeMode,
        string subsetName,
        long sourceRowsSignature,
        string columnName,
        ListSortDirection direction,
        int rowCount,
        RegularChartListExternalVersions externalVersions, long? referencePosition = null)
    {
        lock (syncRoot)
        {
            ResolveDependencyGenerationsUnsafe(
                columnName,
                externalVersions,
                out long score,
                out long chartInfo,
                out long maintenance,
                out long warning,
                out long installDestination,
                out long referenceTables);
            return new VirtualChartSubsetSortCacheKey(
                expectedSourceGeneration,
                expectedSortKeyGeneration,
                score,
                chartInfo,
                maintenance,
                warning,
                installDestination,
                referencePosition.HasValue && MainViewRefreshDecisionService.GetSortColumnDependency(columnName) == MainViewDataDependency.ReferenceTables
                    ? referencePosition.Value : referenceTables,
                treeMode,
                subsetName,
                sourceRowsSignature,
                columnName,
                direction,
                rowCount);
        }
    }

    internal bool TryGetVirtualOrder(NormalLibrarySortCacheKey key, out ChartListOrder order)
    {
        lock (syncRoot)
        {
            return virtualOrderCache.TryGetValue(key, out order) && order != null;
        }
    }

    internal bool TryGetVirtualSubsetOrder(VirtualChartSubsetSortCacheKey key, out ChartListOrder order)
    {
        lock (syncRoot)
        {
            return virtualSubsetOrderCache.TryGetValue(key, out order) && order != null;
        }
    }

    /// <summary>世代と捕捉入力を同じcache登録区間で検査し、背景構築の旧順序を再登録しません。</summary>
    internal bool TryPublishVirtualOrder(
        NormalLibrarySortCacheKey key,
        ChartListOrder order,
        RegularChartListExternalVersions currentExternalVersions,
        NormalLibrarySourceRows input = null)
    {
        lock (syncRoot)
        {
            if (disposed || !IsCurrentUnsafe(key, currentExternalVersions) || input?.IsCurrent == false
                || input != null && input.References.Revision < CaptureRequiredReferenceRevision(input.Library))
            {
                return false;
            }
            virtualOrderCache[key] = order;
            return true;
        }
    }

    /// <summary>入力の参照差分が現在の必要位置を覆う場合だけ専用整列cacheを公開します。</summary>
    internal bool TryPublishVirtualSubsetOrder(
        VirtualChartSubsetSortCacheKey key,
        ChartListOrder order,
        RegularChartListExternalVersions currentExternalVersions, ChartListReferenceInput referenceInput = null, int? catalogRevision = null)
    {
        lock (syncRoot)
        {
            if (disposed || !IsCurrentUnsafe(key, currentExternalVersions, referenceInput?.Revision)
                || referenceInput != null && referenceInput.Revision < CaptureRequiredReferenceRevision(referenceInput.Library)
                || catalogRevision.HasValue && referenceInput?.Library != null && catalogRevision.Value != referenceInput.Library.CatalogRevision)
            {
                return false;
            }
            virtualSubsetOrderCache[key] = order;
            return true;
        }
    }

    internal bool IsCurrentVirtualGeneration(long expectedSourceGeneration, long expectedSortKeyGeneration)
    {
        lock (syncRoot)
        {
            return !disposed
                && sourceGeneration == expectedSourceGeneration
                && sortKeyGeneration == expectedSortKeyGeneration;
        }
    }

    internal RegularChartListBuildResult Build(
        RegularChartListRequestLease lease,
        RegularChartListRefreshRequest request,
        RegularChartListBuildInput input)
    {
        if (lease == null)
        {
            throw new ArgumentNullException(nameof(lease));
        }
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        lease.Token.ThrowIfCancellationRequested();
        Stopwatch stopwatch = input.Stopwatch ?? Stopwatch.StartNew();
        long stageStartMs = stopwatch.ElapsedMilliseconds;

        IReadOnlyList<LibraryChartRow> existingFolderRows;
        List<LibraryChartRow> existingFolderSortSource;
        List<LibraryChartRow> existingFolderSortResult;
        string existingFolderSortColumn;
        ListSortDirection? existingFolderSortDirection;
        lock (syncRoot)
        {
            existingFolderRows = folderRows;
            existingFolderSortSource = folderSortSourceSnapshot;
            existingFolderSortResult = folderSortResultSnapshot;
            existingFolderSortColumn = folderSortColumnName;
            existingFolderSortDirection = folderSortDirection;
        }

        IReadOnlyList<LibraryChartRow> nextFolderRows = input.HasFolderRowsOverride
            ? RegularChartListStageState.Materialize(input.FolderRowsOverride)
            : RegularChartListStageState.Materialize(existingFolderRows);
        long folderMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        lease.Token.ThrowIfCancellationRequested();
        stageStartMs = stopwatch.ElapsedMilliseconds;
        IReadOnlyList<LibraryChartRow> nextKeywordRows = !string.IsNullOrWhiteSpace(request.KeywordFilter)
            ? RegularChartListStageState.Materialize(RegularChartListFilterService.ApplyKeywordFilter(nextFolderRows, request.KeywordFilter))
            : nextFolderRows;
        long keywordMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        lease.Token.ThrowIfCancellationRequested();
        stageStartMs = stopwatch.ElapsedMilliseconds;
        IReadOnlyList<LibraryChartRow> nextModeRows = request.ModeFilter != ChartModeFilter.All
            ? RegularChartListStageState.Materialize(RegularChartListFilterService.ApplyModeFilter(nextKeywordRows, request.ModeFilter))
            : nextKeywordRows;
        long modeMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        lease.Token.ThrowIfCancellationRequested();

        var stage = new RegularChartListStageState
        {
            FolderRows = nextFolderRows,
            KeywordRows = nextKeywordRows,
            ModeRows = nextModeRows
        };
        stageStartMs = stopwatch.ElapsedMilliseconds;
        RegularChartListSortResult sort = ApplySort(
            lease,
            request,
            stage,
            input,
            existingFolderSortSource,
            existingFolderSortResult,
            existingFolderSortColumn,
            existingFolderSortDirection,
            out NormalLibrarySortCacheKey? pendingCacheKey,
            out List<LibraryChartRow> pendingCacheRows);
        long sortMs = stopwatch.ElapsedMilliseconds - stageStartMs;

        return new RegularChartListBuildResult(stage, sort, pendingCacheKey, pendingCacheRows, folderMs, keywordMs, modeMs, sortMs);
    }

    internal RegularMaterializedChartListApplyResult TryApplyMaterialized(
        RegularMaterializedChartListApplyRequest request)
    {
        if (request == null)
        {
            throw new ArgumentException("A complete materialized regular chart-list request is required.", nameof(request));
        }
        if (!TryBeginRequest(
            request.RetireDetailSource,
            out RegularChartListRequestLease lease,
            out PlaylistSourceRetirementRequest detailSourceRetirement, navigation: request.Navigation))
        {
            return default;
        }
        request.DetailSourceRetirement = detailSourceRetirement;

        RegularChartListBuildResult build = BuildMaterialized(request, lease, out RegularChartListPresentationResult presentation, HasTreeFilter);
        if (presentation == null) { return default; }
        RegularChartListTerminalResult terminal = TryCommit(lease, presentation);
        return terminal.WasCommitted ? new RegularMaterializedChartListApplyResult(terminal.RowsApply, build, request.Mode) : default;
    }

    private RegularChartListBuildResult BuildMaterialized(RegularMaterializedChartListApplyRequest request,
        RegularChartListRequestLease lease, out RegularChartListPresentationResult presentation, bool hasTreeFilter)
    {
        presentation = null;
        Stopwatch stopwatch = request.Stopwatch ?? Stopwatch.StartNew();
        RegularChartListBuildResult build;
        try
        {
            build = Build(
                lease,
                request.RefreshRequest,
                new RegularChartListBuildInput
                {
                    CurrentTreeMode = request.RefreshRequest.CurrentTreeMode,
                    HasVirtualNormalLibraryTreeFilter = hasTreeFilter,
                    IsPlaylistDetailView = false,
                    HasFolderRowsOverride = request.HasFolderRowsOverride,
                    FolderRowsOverride = request.FolderRowsOverride,
                    SortCacheGeneration = CaptureSortGeneration(
                        request.RefreshRequest.SortColumnName,
                        request.ExternalVersions),
                    Stopwatch = stopwatch
                });
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
            return default;
        }

        IList rowsView = build.Sort.RowsView;
        long terminalStageStartMs = stopwatch.ElapsedMilliseconds;
        presentation = RegularChartListPresentationResult.ForMaterialized(
                build,
                new MainChartListRowsApplyRequest
                {
                    Rows = rowsView,
                    ColumnsSettings = request.ColumnSelection.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                    Summary = request.PreserveSummary
                        ? MainChartListSummaryUpdate.Preserve()
                        : MainChartListSummaryUpdate.NormalRows(rowsView),
                    ColumnSettingReuse = request.ColumnSelection.Reused,
                    ColumnPreparationMs = request.ColumnSelection.ElapsedMs,
                    TerminalStageStartMs = terminalStageStartMs,
                    Stopwatch = stopwatch
                },
                request.ColumnSelection,
                request.Mode,
                stopwatch,
                request.DetailSourceRetirement);
        return build;
    }

    /// <summary>
    /// Cancels regular work before the shell mutates main-view selection or route state.
    /// </summary>
    internal void PrepareForMainViewRefresh()
    {
        InvalidatePendingRequest();
    }

    internal void InitializeColumnPresentation(MainViewUpdateMode currentTreeMode)
    {
        ApplyColumnPresentation(
            mainChartList.LoadColumnSetting(
                MainViewUpdateMode.TreeViewFilterNotChanged,
                currentTreeMode,
                isInit: true));
    }

    internal void ResetCurrentColumnPresentation()
    {
        MainViewUpdateMode currentMode = mainChartList.LastAppliedColumnMode
            ?? throw new InvalidOperationException("The main chart column presentation has not been initialized.");
        ApplyColumnPresentation(mainChartList.LoadColumnSetting(currentMode, currentMode, isInit: true));
    }

    private void ApplyColumnPresentation(MainChartListColumnSelection selection)
    {
        if (!selection.AppliedMode.HasValue)
        {
            throw new InvalidOperationException("The main chart column presentation did not resolve a view mode.");
        }

        mainChartList.ColumnsSettings = selection.ColumnsSettings;
        PlaylistColumnPresentationCommit commit = playlistWorkspace.CommitColumnPresentationWithoutNotification(
            selection.PlaylistColumnSettingsVisibility,
            selection.PlaylistSummaryColumnsSettings);
        mainChartList.CommitAppliedColumnMode(selection.AppliedMode);
        playlistWorkspace.PublishColumnPresentation(commit);
    }

    /// <summary>正規入口で捕捉した画面受付と表示入力を、通常一覧の各構築経路へ渡します。</summary>
    /// <param name="route">既存の一覧経路・現在のツリー区分です。</param>
    /// <param name="library">表示対象ライブラリです。</param>
    /// <param name="parameter">今回の入力対象です。</param>
    /// <param name="treeParameter">継続表示で維持するツリー対象です。</param>
    /// <param name="preserveSummary">サマリーを保持するかどうかです。</param>
    /// <param name="stopwatch">一覧更新全体の計測です。</param>
    /// <param name="filters">捕捉済みの絞込みです。</param>
    /// <param name="navigation">同じ短命な画面受付です。自動更新では現在の受付を引き継ぎ、独立した直接呼出しでは省略できます。</param>
    /// <returns>既存の利用権と画面受付が有効な場合だけ採用した結果です。</returns>
    internal RegularChartListEntryResult ApplyMainLibraryView(
        ChartListRefreshRoute route,
        BMSLibrary library,
        object parameter,
        object treeParameter,
        bool preserveSummary,
        Stopwatch stopwatch,
        ChartListFilterSnapshot filters = null,
        RegularChartNavigationRequest navigation = null)
    {
        if (route.Kind != ChartListRefreshRouteKind.ContinueMainLibrary)
        {
            throw new ArgumentException(
                "The regular chart-list owner requires a main-library route.",
                nameof(route));
        }

        return ApplyRegularView(
            new RegularChartListEntryRequest
            {
                Library = library,
                Mode = route.Mode,
                RequestedMode = route.RequestedMode,
                Parameter = parameter,
                CurrentTreeMode = route.CurrentTreeMode,
                TreeParameter = treeParameter,
                IncludeBmsonRows = route.IncludeBmsonRows,
                PreserveSummary = preserveSummary,
                Stopwatch = stopwatch,
                Filters = filters ?? ChartListFilterSnapshot.Default,
                Navigation = navigation
            });
    }

    /// <summary>直接呼出しもUI捕捉と共通構築処理を使います。本番の非同期入口は同じ候補をワーカーで構築します。</summary>
    internal RegularChartListEntryResult ApplyRegularView(RegularChartListEntryRequest request)
    {
        PreparedRegularView prepared = PrepareRegularView(request);
        if (prepared == null) { return default; }
        RegularChartListPresentationResult presentation = prepared.Build();
        if (presentation != null) { presentation.InputIsCurrent = prepared.InputIsCurrent; }
        RegularChartListTerminalResult terminal = presentation == null ? RegularChartListTerminalResult.Stale()
            : TryCommitCore(prepared.Lease, presentation);
        return prepared.Finish(terminal);
    }

    private PreparedRegularView PrepareRegularView(RegularChartListEntryRequest request)
    {
        if (request == null || request.Stopwatch == null)
        {
            throw new ArgumentException("A complete regular chart-list entry request is required.", nameof(request));
        }
        if (request.IncludeBmsonRows && request.Library == null)
        {
            throw new ArgumentException("A library is required when bmson rows are included.", nameof(request));
        }
        if (request.CurrentTreeMode is MainViewUpdateMode.NewlyInstalledFolderSelected or MainViewUpdateMode.PendingInstallFolderSelected)
        {
            throw new InvalidOperationException("Package views require ApplyMainLibraryViewAsync.");
        }
        if (request.Navigation != null && !IsCurrentNavigation(request.Navigation)) { return null; }
        ChartListSortSpecification sort = CaptureSort();
        ChartListFilterSnapshot filters = request.Filters ?? ChartListFilterSnapshot.Default;
        string keywordFilter = filters.KeywordFilter;
        ChartModeFilter modeFilter = filters.ModeFilter;
        RegularChartListExternalVersions externalVersions = CaptureExternalVersions(request.Library, default);
        MainViewUpdateMode resolvedMode = ChartListRefreshCoordinator.ResolveMainColumnSettingMode(request.Mode, request.CurrentTreeMode);
        MainViewUpdateMode resolvedTreeMode = ChartListRefreshCoordinator.ResolveMainColumnSettingMode(request.CurrentTreeMode, request.CurrentTreeMode);
        MainChartListColumnSelection columnSelection = mainChartList.ResolveColumnSettingForViewUpdate(resolvedMode, request.CurrentTreeMode);
        MainChartListColumnSelection treeColumnSelection = resolvedMode == resolvedTreeMode
            ? columnSelection
            : mainChartList.ResolveColumnSettingForViewUpdate(resolvedTreeMode, request.CurrentTreeMode);

        bool sortWasReset = !TryResolveVirtualSort(
            sort,
            out string virtualSortColumn,
            out ListSortDirection virtualSortDirection);
        if (sortWasReset)
        {
            virtualSortColumn = nameof(LibraryChartRow.Title);
            virtualSortDirection = ListSortDirection.Ascending;
        }

        if (IsDefaultVirtualRequest(request.Mode, request.CurrentTreeMode))
        {
            var input = new RegularVirtualNormalLibraryApplyRequest
            {
                Navigation = request.Navigation,
                Library = request.Library,
                IncludeBmsonRows = request.IncludeBmsonRows,
                TreeFilter = CaptureTreeFilter(ShouldApplyDefaultTreeFilter(request.CurrentTreeMode)),
                KeywordFilter = keywordFilter,
                ModeFilter = modeFilter,
                SortColumnName = virtualSortColumn,
                SortDirection = virtualSortDirection,
                ExternalVersions = externalVersions,
                ColumnSelection = columnSelection,
                PreserveSummary = request.PreserveSummary,
                Mode = request.Mode,
                Stopwatch = request.Stopwatch,
                RetireDetailSource = true,
                Reason = request.Mode.ToString()
            };
            if (!TryBeginVirtualRequest(request.Library, true, out RegularChartListRequestLease lease,
                    out PlaylistSourceRetirementRequest retirement, navigation: request.Navigation, resetDerivedCaches: true)) { return null; }
            input.DetailSourceRetirement = retirement;
            mainChartList.RowProjection.CaptureVersions(request.Library);
            RegularVirtualNormalLibraryApplyResult result = default;
            return new PreparedRegularView(lease, () =>
            {
                result = BuildVirtualNormalLibrary(input, lease, out RegularChartListPresentationResult presentation);
                return presentation;
            }, terminal =>
            {
                result = result.WithTerminal(terminal);
                LogDefaultVirtualEntry(request, sort, result, sortWasReset);
                if (terminal.WasCommitted && sortWasReset) { ResetSortIfCurrent(lease, sort); }
                return new RegularChartListEntryResult(terminal.WasCommitted, RegularChartListEntryRoute.DefaultVirtual, sortWasReset);
            });
        }

        bool duplicateView = request.CurrentTreeMode == MainViewUpdateMode.DuplicateFilterSelected;
        RegularDuplicateViewInput duplicateInput = duplicateView ? request.DuplicateInput ?? RegularDuplicateViewInput.Capture(request.Library) : null;
        if (duplicateView && duplicateInput == null && request.ConfirmedUnresolvedDuplicateSource == null) { return null; }
        object subsetParameter = request.TreeParameter ?? request.Parameter;
        bool canResolveSubset = !duplicateView || subsetParameter is DuplicateViewContext || request.ConfirmedUnresolvedDuplicateSource != null;
        if (IsSubsetVirtualRequest(request.Mode, request.CurrentTreeMode) && canResolveSubset)
        {
            var input = new RegularVirtualChartSubsetApplyRequest
            {
                Navigation = request.Navigation,
                Library = request.Library,
                TreeMode = request.CurrentTreeMode,
                SelectionContext = (request.TreeParameter ?? request.Parameter) is DuplicateViewContext duplicateSelection
                    ? (duplicateSelection.Kind, duplicateSelection.Kind == DuplicateViewContextKind.Root
                        ? string.Empty : duplicateSelection.Value?.ToUpperInvariant()) : null,
                KeywordFilter = keywordFilter,
                ModeFilter = modeFilter,
                SortColumnName = virtualSortColumn,
                SortDirection = virtualSortDirection,
                ExternalVersions = externalVersions,
                ColumnSelection = columnSelection,
                PreserveSummary = request.PreserveSummary,
                Mode = request.Mode,
                Stopwatch = request.Stopwatch,
                RetireDetailSource = true
            };
            if (!TryBeginVirtualRequest(request.Library, true, out RegularChartListRequestLease lease,
                    out PlaylistSourceRetirementRequest retirement, navigation: request.Navigation, resetDerivedCaches: true)) { return null; }
            input.DetailSourceRetirement = retirement;
            mainChartList.RowProjection.CaptureVersions(request.Library);
            RegularVirtualChartSubsetApplyResult result = default;
            return new PreparedRegularView(lease, () =>
            {
                RegularChartListSubsetSource subset;
                bool resolved;
                if (request.ConfirmedUnresolvedDuplicateSource != null) { subset = request.ConfirmedUnresolvedDuplicateSource; resolved = true; }
                else
                {
                    resolved = duplicateView ? TryResolveDuplicateSource(duplicateInput.Groups, subsetParameter, out subset)
                    : TryResolveSubsetSource(request, request.Library, out subset);
                }
                if (!resolved) { throw new InvalidOperationException("The captured regular subset did not resolve."); }
                input.SourceCharts = subset.SourceCharts;
                input.SourceProjectionMode = subset.SourceProjectionMode;
                input.ApplyResourceHealthProjection = subset.ApplyResourceHealthProjection;
                input.SubsetName = subset.Name;
                result = BuildVirtualChartSubset(input, lease, out RegularChartListPresentationResult presentation);
                return presentation;
            }, terminal =>
            {
                result = result.WithTerminal(terminal);
                LogSubsetVirtualEntry(request, sort, input.SubsetName, result, sortWasReset);
                if (terminal.WasCommitted && sortWasReset) { ResetSortIfCurrent(lease, sort); }
                return new RegularChartListEntryResult(terminal.WasCommitted, RegularChartListEntryRoute.SubsetVirtual, sortWasReset);
            })
            { InputIsCurrent = duplicateInput == null ? null : () => duplicateInput.IsCurrent };
        }

        bool virtualSubsetRequiredFailure = IsSubsetVirtualRequired(request.Mode, request.CurrentTreeMode);
        if (virtualSubsetRequiredFailure)
        {
            logWarning("main_view_virtual_required_failed scope=chart_subset"
                + " mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode
                + " treeMode=" + request.CurrentTreeMode);
        }
        bool hasFolderRowsOverride = virtualSubsetRequiredFailure;
        IEnumerable<LibraryChartRow> folderRowsOverride = virtualSubsetRequiredFailure ? [] : null;
        MainViewUpdateMode effectiveMode = request.Mode;
        object effectiveParameter = request.Parameter;
        if (!virtualSubsetRequiredFailure
            && MainViewRefreshDecisionService.ShouldRebuildRegularFolderStage(
                request.Mode,
                HasFolderRows,
                HasKeywordRows,
                HasModeRows,
                request.CurrentTreeMode))
        {
            effectiveMode = request.CurrentTreeMode;
            effectiveParameter = request.TreeParameter;
        }

        var refreshRequest = new RegularChartListRefreshRequest(
            effectiveMode,
            request.RequestedMode,
            effectiveParameter,
            request.CurrentTreeMode,
            request.TreeParameter,
            request.IncludeBmsonRows,
            virtualSubsetRequiredFailure,
            keywordFilter,
            modeFilter,
            sort);
        if (!refreshRequest.VirtualSubsetRequiredFailure
            && (refreshRequest.Mode == MainViewUpdateMode.FolderFilterSelected
                || refreshRequest.Mode == MainViewUpdateMode.FullScanAllChartsFilterSelected))
        {
            hasFolderRowsOverride = true;
            folderRowsOverride = [];
        }

        var materializedInput = new RegularMaterializedChartListApplyRequest
        {
            Navigation = request.Navigation,
            RefreshRequest = refreshRequest,
            HasFolderRowsOverride = hasFolderRowsOverride,
            FolderRowsOverride = folderRowsOverride,
            ExternalVersions = externalVersions,
            ColumnSelection = effectiveMode == request.Mode ? columnSelection : treeColumnSelection,
            PreserveSummary = request.PreserveSummary,
            Mode = effectiveMode,
            Stopwatch = request.Stopwatch,
            RetireDetailSource = true
        };
        if (!TryBeginRequest(true, out RegularChartListRequestLease materializedLease,
                out PlaylistSourceRetirementRequest materializedRetirement, request.Navigation)) { return null; }
        materializedInput.DetailSourceRetirement = materializedRetirement;
        bool hasTreeFilter = HasTreeFilter;
        RegularChartListBuildResult build = default;
        return new PreparedRegularView(materializedLease, () =>
        {
            if (!virtualSubsetRequiredFailure) { PrepareResourceHealthIndexForView(request.Library, request.CurrentTreeMode, "normal_library_view"); }
            build = BuildMaterialized(materializedInput, materializedLease, out RegularChartListPresentationResult presentation, hasTreeFilter);
            return presentation;
        }, terminal =>
        {
            RegularMaterializedChartListApplyResult result = terminal.WasCommitted
                ? new RegularMaterializedChartListApplyResult(terminal.RowsApply, build, effectiveMode) : default;
            LogMaterializedEntry(request, effectiveMode, refreshRequest, result);
            return new RegularChartListEntryResult(terminal.WasCommitted, RegularChartListEntryRoute.Materialized, sortWasReset: false);
        })
        { InputIsCurrent = duplicateInput == null ? null : () => duplicateInput.IsCurrent };
    }

    // 一回のUI入力と構築の利用権を、画面外での構築処理へ渡す。構築中に現在値を取り直さない。
    private sealed class PreparedRegularView(RegularChartListRequestLease lease,
        Func<RegularChartListPresentationResult> build, Func<RegularChartListTerminalResult, RegularChartListEntryResult> finish)
    {
        internal RegularChartListRequestLease Lease { get; } = lease;
        /// <summary>完成キャッシュの参照/失効版を同じ候補の最終UI採用へ渡します。</summary>
        internal Func<bool> InputIsCurrent { get; init; }
        internal Func<RegularChartListPresentationResult> Build { get; } = build;
        internal Func<RegularChartListTerminalResult, RegularChartListEntryResult> Finish { get; } = finish;
    }

    private static bool TryResolveVirtualSort(
        ChartListSortSpecification sort,
        out string columnName,
        out ListSortDirection direction)
    {
        direction = sort.HasValue ? sort.Direction : ListSortDirection.Ascending;
        if (!sort.HasValue)
        {
            columnName = nameof(LibraryChartRow.Title);
            return true;
        }
        return ChartListOrder.TryNormalizeVirtualSortColumn(sort.RequestedColumnName, out columnName);
    }

    internal static bool IsDefaultVirtualRequest(MainViewUpdateMode mode, MainViewUpdateMode treeMode)
    {
        return (treeMode == MainViewUpdateMode.FolderFilterSelected
                || treeMode == MainViewUpdateMode.FullScanAllChartsFilterSelected)
            && (mode == treeMode
                || mode == MainViewUpdateMode.TreeViewFilterNotChanged
                || mode == MainViewUpdateMode.KeywordFilterUpdated
                || mode == MainViewUpdateMode.ModeFilterUpdated
                || mode == MainViewUpdateMode.SortUpdated);
    }

    internal static bool IsSubsetVirtualRequest(MainViewUpdateMode mode, MainViewUpdateMode treeMode)
    {
        return IsSubsetVirtualTreeMode(treeMode)
            && (mode == treeMode
                || mode == MainViewUpdateMode.TreeViewFilterNotChanged
                || mode == MainViewUpdateMode.KeywordFilterUpdated
                || mode == MainViewUpdateMode.ModeFilterUpdated
                || mode == MainViewUpdateMode.SortUpdated);
    }

    internal static bool IsSubsetVirtualRequired(MainViewUpdateMode mode, MainViewUpdateMode treeMode)
    {
        return IsSubsetVirtualRequest(mode, treeMode) || IsSubsetVirtualTreeMode(mode);
    }

    internal static bool IsSubsetVirtualTreeMode(MainViewUpdateMode mode)
    {
        return mode == MainViewUpdateMode.FileMissingFilterSelected
            || mode == MainViewUpdateMode.FileMissingIgnoredFilterSelected
            || mode == MainViewUpdateMode.DuplicateFilterSelected
            || mode == MainViewUpdateMode.GarbledFilterSelected
            || mode == MainViewUpdateMode.GarbleFixedFilterSelected
            || mode == MainViewUpdateMode.UnregisteredFilterSelected
            || mode == MainViewUpdateMode.ZeroNoteFilterSelected
            || mode == MainViewUpdateMode.ChartInfoParseErrorFilterSelected
            || mode == MainViewUpdateMode.NewlyInstalledFolderSelected
            || mode == MainViewUpdateMode.PendingInstallFolderSelected;
    }

    internal static bool ShouldApplyDefaultTreeFilter(MainViewUpdateMode treeMode)
    {
        return treeMode != MainViewUpdateMode.FullScanAllChartsFilterSelected;
    }

    internal static bool ShouldApplyResourceHealthProjection(MainViewUpdateMode treeMode)
    {
        return treeMode == MainViewUpdateMode.FileMissingFilterSelected
            || treeMode == MainViewUpdateMode.FileMissingIgnoredFilterSelected
            || treeMode == MainViewUpdateMode.NewlyInstalledFolderSelected;
    }

    /// <summary>
    /// ResourceHealth を投影する一覧の入力境界で、現在の索引を準備します。
    /// 行の getter は構築を行わないため、行・並べ替え・cache の採用より前に
    /// 一覧の管理主体がこの境界を通します。
    /// </summary>
    /// <param name="library">表示対象のライブラリ。</param>
    /// <param name="treeMode">現在のツリー表示モード。</param>
    /// <param name="reason">索引準備の診断理由。</param>
    internal void PrepareResourceHealthIndexForView(
        BMSLibrary library,
        MainViewUpdateMode treeMode,
        string reason)
    {
        if (library == null
            || (treeMode != MainViewUpdateMode.FolderFilterSelected
                && treeMode != MainViewUpdateMode.FullScanAllChartsFilterSelected
                && !ShouldApplyResourceHealthProjection(treeMode)))
        {
            return;
        }

        library.GetResourceHealthIndexSnapshotForView(
            string.IsNullOrWhiteSpace(reason) ? "normal_library_view" : reason);
    }

    private static bool TryResolveSubsetSource(
        RegularChartListEntryRequest request,
        BMSLibrary library,
        out RegularChartListSubsetSource source)
    {
        object parameter = request.CurrentTreeMode == MainViewUpdateMode.DuplicateFilterSelected
            ? request.TreeParameter ?? request.Parameter
            : request.Parameter;
        switch (request.CurrentTreeMode)
        {
            case MainViewUpdateMode.FileMissingFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesNeedResourceFix, "file_missing", applyResourceHealthProjection: true);
                return true;
            case MainViewUpdateMode.FileMissingIgnoredFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesNeedResourceFixIgnored, "file_missing_ignored", applyResourceHealthProjection: true);
                return true;
            case MainViewUpdateMode.DuplicateFilterSelected:
                return TryResolveDuplicateSource(library?.DuplicateChartGroups, parameter, out source);
            case MainViewUpdateMode.GarbledFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesGarbled, "garbled", ChartListSourceProjectionMode.OwnerBacked);
                return true;
            case MainViewUpdateMode.GarbleFixedFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesGarbledFixed, "garble_fixed", ChartListSourceProjectionMode.OwnerBacked);
                return true;
            case MainViewUpdateMode.UnregisteredFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesUnregistered, "unregistered", ChartListSourceProjectionMode.OwnerBacked);
                return true;
            case MainViewUpdateMode.ZeroNoteFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesZeroNote, "zero_note", ChartListSourceProjectionMode.OwnerBacked);
                return true;
            case MainViewUpdateMode.ChartInfoParseErrorFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartInfoParseFailedChartFiles, "chart_info_parse_error");
                return true;
            default:
                source = null;
                return false;
        }
    }

    /// <summary>捕捉済みpackage所属を一巡し、各packageが返した固定entry列を一度だけ取得して連結します。</summary>
    internal static IReadOnlyList<PackageChartEntry> CreatePackageEntrySnapshot(IEnumerable<ChartPackage> packages)
    {
        // ChartEntries は未準備なら本来の発見処理も実行する。失敗を空の成功へ置き換えない。
        IReadOnlyList<PackageChartEntry>[] entries = (packages ?? [])
            .Where(package => package != null)
            .Select(package => (IReadOnlyList<PackageChartEntry>)package.ChartEntries)
            .ToArray();
        if (entries.Length == 0) { return []; }
        if (entries.Length == 1) { return entries[0]; }
        var flattened = new PackageChartEntry[entries.Sum(snapshot => snapshot.Count)];
        int position = 0;
        foreach (IReadOnlyList<PackageChartEntry> snapshot in entries)
        {
            foreach (PackageChartEntry entry in snapshot) { flattened[position++] = entry; }
        }
        return flattened;
    }

    /// <summary>明示root、現在結果内で一意なグループ所属、folder配下を分けて捕捉します。未解決を全件へ広げず、一覧構築時に共通現在値を投影します。</summary>
    internal static bool TryResolveDuplicateSource(
        IEnumerable<DuplicateGroup> duplicateGroups,
        object parameter,
        out RegularChartListSubsetSource source)
    {
        if (duplicateGroups == null)
        {
            source = null;
            return false;
        }
        List<DuplicateGroup> groups = [.. (duplicateGroups ?? []).Where(group => group != null)];
        if (parameter is DuplicateViewContext context)
        {
            if (context.Kind == DuplicateViewContextKind.Root)
            {
                source = RegularChartListSubsetSource.ForCharts(
                    [.. groups.SelectMany(group => group.ChartFiles)],
                    "duplicate_all", ChartListSourceProjectionMode.DuplicateCurrent, applyResourceHealthProjection: true);
                return true;
            }
            if (context.Kind == DuplicateViewContextKind.GroupFolder)
            {
                DuplicateGroup group = context.ResolveGroup(groups);
                source = RegularChartListSubsetSource.ForCharts(
                    [.. (group?.ChartFiles ?? [])], "duplicate_group", ChartListSourceProjectionMode.DuplicateCurrent,
                    applyResourceHealthProjection: true);
                return true;
            }
            string prefix = string.IsNullOrWhiteSpace(context.Value) ? null
                : context.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            source = RegularChartListSubsetSource.ForCharts(
                [.. groups.SelectMany(group => group.ChartFiles)
                    .Where(chart => prefix != null && !string.IsNullOrWhiteSpace(chart.Path)
                        && chart.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))],
                "duplicate_folder", ChartListSourceProjectionMode.DuplicateCurrent, applyResourceHealthProjection: true);
            return true;
        }
        source = null;
        return false;
    }

    private void LogDefaultVirtualEntry(
        RegularChartListEntryRequest request,
        ChartListSortSpecification sort,
        RegularVirtualNormalLibraryApplyResult result,
        bool sortWasReset)
    {
        if (sortWasReset)
        {
            logWarning("main_view_virtual_sort_reset scope=normal_library"
                + " mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode
                + " treeMode=" + request.CurrentTreeMode
                + " requestedSortColumn=" + (sort.RequestedColumnName ?? "(default_title)")
                + " appliedSortColumn=" + nameof(LibraryChartRow.Title)
                + " appliedSortDirection=" + ListSortDirection.Ascending);
        }
        if (result.WasCommitted)
        {
            LogMainSortDetail(ChartListRefreshCoordinator.CreateVirtualSortMetrics(
                result.Order,
                result.SortMs,
                result.SortCacheHit,
                GetSortCacheGenerationForLog(result.SortCacheKey),
                result.OrderCacheLookupMs,
                result.OrderBuildMs));
        }
        MainChartListRowsApplyResult rowsApply = result.Terminal.RowsApply;
        log("main_view_build route=default_virtual"
            + " mode=" + request.Mode
            + " requestedMode=" + request.RequestedMode
            + " committed=" + result.WasCommitted
            + " sortReset=" + sortWasReset
            + " folderMs=" + result.FolderMs
            + " keywordMs=" + result.KeywordMs
            + " modeMs=" + result.ModeMs
            + " sortMs=" + result.SortMs
            + " sortReuse=" + result.SortCacheHit
            + " sortProfile=" + (result.Order?.SortProfile ?? string.Empty)
            + " sortEngine=virtual virtual=True"
            + " sourceRowsMs=" + result.SourceRowsMs
            + " columnMs=" + rowsApply.ColumnStageMs
            + " prepareSwapMs=" + rowsApply.PrepareSwapMs
            + " columnSettingMs=" + rowsApply.ColumnSettingMs
            + " setViewMs=" + rowsApply.SetViewMs
            + " columnSettingReuse=" + rowsApply.ColumnSettingReuse
            + " folderCount=" + result.FolderCount
            + " keywordCount=" + result.KeywordCount
            + " modeCount=" + result.ModeCount
            + " viewCount=" + (result.RowsView?.Count ?? 0)
            + " sourceRows=" + result.SourceRowCount
            + " orderedRows=" + (result.Order?.Count ?? 0)
            + " viewRowsCreated=" + (result.RowsView?.RealizedRowCount ?? 0)
            + " distinctFolderCount=" + result.DistinctFolderCount
            + " summaryFolderCountReuse=" + result.SummaryCacheHit
            + " sourceRowsReuse=" + result.SourceRowsCacheHit
            + " orderCacheLookupMs=" + result.OrderCacheLookupMs
            + " orderBuildMs=" + result.OrderBuildMs
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
    }

    private void LogSubsetVirtualEntry(
        RegularChartListEntryRequest request,
        ChartListSortSpecification sort,
        string subsetName,
        RegularVirtualChartSubsetApplyResult result,
        bool sortWasReset)
    {
        if (sortWasReset)
        {
            logWarning("main_view_virtual_subset_sort_reset"
                + " mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode
                + " treeMode=" + request.CurrentTreeMode
                + " requestedSortColumn=" + (sort.RequestedColumnName ?? "(default_title)")
                + " appliedSortColumn=" + nameof(LibraryChartRow.Title)
                + " appliedSortDirection=" + ListSortDirection.Ascending);
        }
        if (result.WasCommitted)
        {
            LogMainSortDetail(ChartListRefreshCoordinator.CreateVirtualSortMetrics(
                result.Order,
                result.SortMs,
                result.SortCacheHit,
                GetSortCacheGenerationForLog(result.SortCacheKey),
                result.OrderCacheLookupMs,
                result.OrderBuildMs));
        }
        if (result.WasCommitted && ShouldApplyResourceHealthProjection(request.CurrentTreeMode))
        {
            LogResourceHealthProjection(request.Library, request.CurrentTreeMode, result.RowsView?.Count ?? 0);
        }
        MainChartListRowsApplyResult rowsApply = result.Terminal.RowsApply;
        log("main_view_build route=subset_virtual"
            + " mode=" + request.Mode
            + " requestedMode=" + request.RequestedMode
            + " treeMode=" + request.CurrentTreeMode
            + " subset=" + subsetName
            + " committed=" + result.WasCommitted
            + " sortReset=" + sortWasReset
            + " keywordMs=" + result.KeywordMs
            + " modeMs=" + result.ModeMs
            + " sortMs=" + result.SortMs
            + " sortReuse=" + result.SortCacheHit
            + " sortProfile=" + (result.Order?.SortProfile ?? string.Empty)
            + " sortEngine=virtual virtual=True"
            + " sourceRowsMs=" + result.SourceRowsMs
            + " columnMs=" + rowsApply.ColumnStageMs
            + " prepareSwapMs=" + rowsApply.PrepareSwapMs
            + " columnSettingMs=" + rowsApply.ColumnSettingMs
            + " setViewMs=" + rowsApply.SetViewMs
            + " columnSettingReuse=" + rowsApply.ColumnSettingReuse
            + " sourceCount=" + result.SourceRowCount
            + " keywordCount=" + result.KeywordCount
            + " modeCount=" + result.ModeCount
            + " viewCount=" + (result.RowsView?.Count ?? 0)
            + " orderedRows=" + (result.Order?.Count ?? 0)
            + " viewRowsCreated=" + (result.RowsView?.RealizedRowCount ?? 0)
            + " distinctFolderCount=" + result.DistinctFolderCount
            + " sourceRowsSignature=" + result.SourceRowsSignature
            + " orderCacheLookupMs=" + result.OrderCacheLookupMs
            + " orderBuildMs=" + result.OrderBuildMs
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
    }

    private void LogMaterializedEntry(
        RegularChartListEntryRequest request,
        MainViewUpdateMode effectiveMode,
        RegularChartListRefreshRequest refreshRequest,
        RegularMaterializedChartListApplyResult result)
    {
        MainChartListRowsApplyResult rowsApply = result.RowsApply;
        log("main_view_build route=materialized"
            + " mode=" + effectiveMode
            + " requestedMode=" + request.RequestedMode
            + " committed=" + result.WasCommitted
            + " folderMs=" + result.FolderMs
            + " keywordMs=" + result.KeywordMs
            + " modeMs=" + result.ModeMs
            + " sortMs=" + result.SortMs
            + " sortReuse=" + result.SortReuse
            + " sortProfile=" + result.SortProfile
            + " sortEngine=fast virtual=False"
            + " columnMs=" + rowsApply.ColumnStageMs
            + " prepareSwapMs=" + rowsApply.PrepareSwapMs
            + " columnSettingMs=" + rowsApply.ColumnSettingMs
            + " setViewMs=" + rowsApply.SetViewMs
            + " columnSettingReuse=" + rowsApply.ColumnSettingReuse
            + " folderCount=" + result.FolderCount
            + " keywordCount=" + result.KeywordCount
            + " modeCount=" + result.ModeCount
            + " viewCount=" + result.ViewCount
            + " sortColumn=" + (refreshRequest.HasSortParameters ? refreshRequest.RequestedSortColumnName : "(default_title)")
            + " sortDirection=" + refreshRequest.SortDirection
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
    }

    private void LogMainSortDetail(LibraryChartSortMetrics metrics)
    {
        log("main_sort_detail rowCount=" + metrics.RowCount
            + " columnName=" + (metrics.ColumnName ?? string.Empty)
            + " direction=" + metrics.Direction
            + " propertyType=" + (metrics.PropertyTypeName ?? "(null)")
            + " sortProfile=" + (metrics.SortProfile ?? string.Empty)
            + " stringSortKind=" + (metrics.StringSortKind ?? string.Empty)
            + " sortReuse=" + metrics.SortReuse
            + " sortCacheKey=" + (metrics.SortCacheKey ?? string.Empty)
            + " sortCacheGeneration=" + metrics.SortCacheGeneration
            + " sortCacheHit=" + metrics.SortCacheHit
            + " orderCacheLookupMs=" + metrics.OrderCacheLookupMs
            + " orderBuildMs=" + metrics.OrderBuildMs
            + " sortMs=" + metrics.SortMs);
    }

    private void LogResourceHealthProjection(BMSLibrary library, MainViewUpdateMode mode, int rowCount)
    {
        ResourceHealthIndexSnapshot snapshot = library?.TryGetCurrentResourceHealthIndexSnapshotForView();
        int overlayCount = snapshot == null
            ? 0
            : mode == MainViewUpdateMode.FileMissingFilterSelected
                ? snapshot.ActiveTargets.Count
                : mode == MainViewUpdateMode.FileMissingIgnoredFilterSelected
                    ? snapshot.IgnoredTargets.Count
                    : snapshot.NeedFixCount;
        log("resource_health_projection reason=" + mode
            + " rowCount=" + rowCount
            + " overlayCount=" + overlayCount
            + " ignored=" + (snapshot?.IgnoredCount ?? 0)
            + " version=" + (snapshot?.Version ?? 0));
    }

    private static long GetSortCacheGenerationForLog(NormalLibrarySortCacheKey key)
    {
        if (key.ScoreGeneration != 0)
        {
            return key.ScoreGeneration;
        }

        if (key.ChartInfoGeneration != 0)
        {
            return key.ChartInfoGeneration;
        }

        if (key.WarningGeneration != 0)
        {
            return key.WarningGeneration;
        }

        if (key.InstallDestinationGeneration != 0)
        {
            return key.InstallDestinationGeneration;
        }

        if (key.ReferenceTablesGeneration != 0)
        {
            return key.ReferenceTablesGeneration;
        }

        if (key.MaintenanceGeneration != 0)
        {
            return key.MaintenanceGeneration;
        }

        return key.SortKeyGeneration;
    }

    private static long GetSortCacheGenerationForLog(VirtualChartSubsetSortCacheKey key)
    {
        if (key.ScoreGeneration != 0)
        {
            return key.ScoreGeneration;
        }

        if (key.ChartInfoGeneration != 0)
        {
            return key.ChartInfoGeneration;
        }

        if (key.WarningGeneration != 0)
        {
            return key.WarningGeneration;
        }

        if (key.InstallDestinationGeneration != 0)
        {
            return key.InstallDestinationGeneration;
        }

        if (key.ReferenceTablesGeneration != 0)
        {
            return key.ReferenceTablesGeneration;
        }

        if (key.MaintenanceGeneration != 0)
        {
            return key.MaintenanceGeneration;
        }

        return key.SortKeyGeneration;
    }

    internal RegularChartListTerminalResult TryCommit(
        RegularChartListRequestLease lease,
        RegularChartListPresentationResult presentation)
    {
        if (lease == null || presentation == null || presentation.Kind != RegularChartListPresentationKind.Materialized)
        {
            throw new ArgumentException("A complete regular chart-list terminal request is required.");
        }

        return TryCommitCore(lease, presentation);
    }

    internal RegularChartListTerminalResult TryCommitVirtual(
        RegularChartListRequestLease lease,
        RegularChartListPresentationResult presentation)
    {
        if (lease == null || presentation == null || presentation.Kind != RegularChartListPresentationKind.Virtual)
        {
            throw new ArgumentException("A complete virtual regular chart-list terminal request is required.");
        }

        return TryCommitCore(lease, presentation);
    }

    internal RegularVirtualNormalLibraryApplyResult TryApplyVirtualNormalLibrary(
        RegularVirtualNormalLibraryApplyRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!TryBeginVirtualRequest(
            request.Library,
            request.RetireDetailSource,
            out RegularChartListRequestLease lease,
            out PlaylistSourceRetirementRequest detailSourceRetirement, navigation: request.Navigation, resetDerivedCaches: true))
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }
        request.DetailSourceRetirement = detailSourceRetirement;
        mainChartList.RowProjection.CaptureVersions(request.Library);
        RegularVirtualNormalLibraryApplyResult result = BuildVirtualNormalLibrary(request, lease, out RegularChartListPresentationResult presentation);
        if (presentation == null) { return default; }
        RegularChartListTerminalResult terminal = TryCommitVirtual(lease, presentation);
        return terminal.WasCommitted ? result.WithTerminal(terminal) : default;
    }

    private RegularVirtualNormalLibraryApplyResult BuildVirtualNormalLibrary(RegularVirtualNormalLibraryApplyRequest request,
        RegularChartListRequestLease lease, out RegularChartListPresentationResult presentation)
    {
        presentation = null;
        PrepareResourceHealthIndexForView(
            request.Library,
            MainViewUpdateMode.FolderFilterSelected,
            string.IsNullOrWhiteSpace(request.Reason)
                ? "normal_library"
                : request.Reason);

        Stopwatch stopwatch = request.Stopwatch ?? Stopwatch.StartNew();
        var performanceInteraction =
            PerformanceInteraction.Existing("normal_library", lease.RequestId);
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(performanceInteraction, "owner_started");
        }

        long stageStartMs = stopwatch.ElapsedMilliseconds;
        IReadOnlyList<ChartListSourceRow> sourceRows = GetOrCreateVirtualNormalLibrarySourceRows(
            request.Library,
            request.IncludeBmsonRows,
            out bool sourceRowsCacheHit,
            out long sourceRowsSourceGeneration,
            out long sourceRowsSortKeyGeneration);
        int scorePosition = (sourceRows as NormalLibrarySourceRows)?.ScoreInput?.Position ?? 0;
        if (lease.Token.IsCancellationRequested)
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }
        long sourceRowsMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                performanceInteraction,
                "snapshot_projection",
                "sourceRows=" + sourceRows.Count
                + " sourceRowsMs=" + sourceRowsMs
                + " cacheHit=" + sourceRowsCacheHit.ToString().ToLowerInvariant());
        }

        stageStartMs = stopwatch.ElapsedMilliseconds;
        ChartListOrder fullOrder = GetOrCreateVirtualNormalLibraryOrder(
            sourceRows,
            request.Library,
            request.SortColumnName,
            request.SortDirection,
            sourceRowsSourceGeneration,
            sourceRowsSortKeyGeneration,
            request.ExternalVersions,
            useCache: true,
            out bool sortCacheHit,
            out NormalLibrarySortCacheKey sortCacheKey,
            out long orderCacheLookupMs,
            out long orderBuildMs, scorePosition);
        if (lease.Token.IsCancellationRequested)
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }
        long sortStageMs = stopwatch.ElapsedMilliseconds - stageStartMs;

        int[] viewOrderedIndexes = ApplyVirtualNormalLibraryFilters(
            sourceRows,
            fullOrder.Indexes,
            request.TreeFilter,
            GridKeywordSearchQuery.Parse(request.KeywordFilter),
            request.ModeFilter,
            out int folderFilteredCount,
            out int keywordFilteredCount,
            out int modeFilteredCount,
            out long folderStageMs,
            out long keywordStageMs,
            out long modeStageMs);
        if (lease.Token.IsCancellationRequested)
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }

        ChartListOrder order = fullOrder.WithIndexes(viewOrderedIndexes);
        int distinctFolderCount = -1;
        bool summaryCacheHit = false;

        stageStartMs = stopwatch.ElapsedMilliseconds;
        ChartListVirtualView rowsView = null;
        rowsView = new ChartListVirtualView(
            sourceRows,
            order,
            row => ReferenceEquals(mainChartList.Rows, rowsView)
                ? CreateVirtualRow(request.Library, row)
                : mainChartList.RowProjection.CreateSubsetRow(request.Library, row, includeResourceHealth: true),
            distinctFolderCount,
            CreateNormalFilter(request), scorePosition: scorePosition,
            selectionScope: (request.Library, request.IncludeBmsonRows, request.TreeFilter?.Kind, request.TreeFilter?.Term));
        distinctFolderCount = rowsView.DistinctFolderCount;
        presentation = RegularChartListPresentationResult.ForVirtual(
                new MainChartListRowsApplyRequest
                {
                    Rows = rowsView,
                    ColumnsSettings = request.ColumnSelection.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                    Summary = request.PreserveSummary
                        ? MainChartListSummaryUpdate.Preserve()
                        : MainChartListSummaryUpdate.NormalCounts(rowsView.Count, distinctFolderCount),
                    ColumnSettingReuse = request.ColumnSelection.Reused,
                    ColumnPreparationMs = request.ColumnSelection.ElapsedMs,
                    TerminalStageStartMs = stageStartMs,
                    Stopwatch = stopwatch
                },
                request.ColumnSelection,
                request.Mode,
                stopwatch,
                request.DetailSourceRetirement);

        return new RegularVirtualNormalLibraryApplyResult(
            default,
            rowsView,
            order,
            sortCacheKey,
            sourceRows.Count,
            folderFilteredCount,
            keywordFilteredCount,
            modeFilteredCount,
            distinctFolderCount,
            sourceRowsCacheHit,
            sortCacheHit,
            summaryCacheHit,
            sourceRowsMs,
            folderStageMs,
            keywordStageMs,
            modeStageMs,
            sortStageMs,
            orderCacheLookupMs,
            orderBuildMs);
    }

    internal RegularVirtualChartSubsetApplyResult TryApplyVirtualChartSubset(
        RegularVirtualChartSubsetApplyRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if ((request.SourceCharts == null) == (request.SourceEntries == null))
        {
            throw new ArgumentException("Exactly one virtual subset source must be provided.", nameof(request));
        }

        if (!TryBeginVirtualRequest(
            request.Library,
            request.RetireDetailSource,
            out RegularChartListRequestLease lease,
            out PlaylistSourceRetirementRequest detailSourceRetirement, navigation: request.Navigation, resetDerivedCaches: true))
        {
            return default;
        }
        request.DetailSourceRetirement = detailSourceRetirement;
        mainChartList.RowProjection.CaptureVersions(request.Library);
        RegularVirtualChartSubsetApplyResult result = BuildVirtualChartSubset(request, lease, out RegularChartListPresentationResult presentation);
        if (presentation == null) { return default; }
        RegularChartListTerminalResult terminal = TryCommitVirtual(lease, presentation);
        return terminal.WasCommitted ? result.WithTerminal(terminal) : default;
    }

    private RegularVirtualChartSubsetApplyResult BuildVirtualChartSubset(
        RegularVirtualChartSubsetApplyRequest request,
        RegularChartListRequestLease lease,
        out RegularChartListPresentationResult presentation)
    {
        presentation = null;
        if (lease.Token.IsCancellationRequested) { return default; }
        if (request.ApplyResourceHealthProjection)
        {
            request.Library?.GetResourceHealthIndexSnapshotForView(
                string.IsNullOrWhiteSpace(request.SubsetName)
                    ? "normal_library_subset"
                    : "normal_library_subset_" + request.SubsetName);
        }

        Stopwatch stopwatch = request.Stopwatch ?? Stopwatch.StartNew();
        var scoreInput = new ChartListScoreInput(request.Library);
        int scorePosition = scoreInput.Position;
        var referenceInput = new ChartListReferenceInput(request.Library);
        int catalogRevision = request.Library?.CatalogRevision ?? 0;
        IEnumerable<ChartFile> sourceCharts = request.SourceCharts;
        if (request.SourceProjectionMode == ChartListSourceProjectionMode.DuplicateCurrent)
        {
            // 検索結果のChartFileは操作用snapshotのまま保ち、一覧では所属と固有警告だけを採用する。
            ChartFile[] memberships = [.. sourceCharts];
            CatalogReadSnapshot current = request.Library.CaptureCatalogReadSnapshot(memberships.Select(chart => LibraryChartRef.FromChartFile(chart)));
            catalogRevision = current.CatalogRevision;
            sourceCharts = memberships.Where(chart => chart.Token != null && current.Charts.ContainsKey(chart.Token))
                .Select(chart => DuplicateGroup.ProjectCurrentChart(chart, current.Charts[chart.Token]));
        }
        NormalLibrarySortCacheGenerationSnapshot generation = CaptureSortGeneration(
            request.SortColumnName,
            request.ExternalVersions);

        long stageStartMs = stopwatch.ElapsedMilliseconds;
        List<ChartListSourceRow> sourceRows = request.SourceEntries != null
            ? mainChartList.RowProjection.BuildPackageSourceRows(
                request.Library,
                request.SourceEntries,
                request.ApplyResourceHealthProjection, scoreInput, referenceInput)
            : mainChartList.RowProjection.BuildStandardSourceRows(
                request.Library,
                sourceCharts,
                request.SourceProjectionMode,
                request.ApplyResourceHealthProjection, scoreInput, referenceInput);
        if (lease.Token.IsCancellationRequested)
        {
            return default;
        }
        long sourceRowsMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        long sourceRowsSignature = ComputeVirtualChartSubsetSourceRowsSignature(sourceRows);

        stageStartMs = stopwatch.ElapsedMilliseconds;
        ChartListOrder fullOrder = GetOrCreateVirtualChartSubsetOrder(
            sourceRows,
            request.Library,
            request.SortColumnName,
            request.SortDirection,
            (int)request.TreeMode,
            request.SubsetName,
            sourceRowsSignature,
            generation.Source,
            generation.SortKey,
            request.ExternalVersions,
            out bool sortCacheHit,
            out VirtualChartSubsetSortCacheKey sortCacheKey,
            out long orderCacheLookupMs,
            out long orderBuildMs, scorePosition, referenceInput, catalogRevision);
        if (lease.Token.IsCancellationRequested)
        {
            return default;
        }
        long sortStageMs = stopwatch.ElapsedMilliseconds - stageStartMs;

        int[] viewOrderedIndexes = ApplyVirtualChartSubsetFilters(
            sourceRows,
            fullOrder.Indexes,
            GridKeywordSearchQuery.Parse(request.KeywordFilter),
            request.ModeFilter,
            out int keywordCount,
            out int modeCount,
            out long keywordStageMs,
            out long modeStageMs);
        if (lease.Token.IsCancellationRequested)
        {
            return default;
        }

        ChartListOrder order = fullOrder.WithIndexes(viewOrderedIndexes);
        int distinctFolderCount = CountDistinctFoldersByIndex(
            sourceRows,
            viewOrderedIndexes,
            () => lease.Token.IsCancellationRequested,
            out _,
            out _);
        if (lease.Token.IsCancellationRequested)
        {
            return default;
        }
        stageStartMs = stopwatch.ElapsedMilliseconds;
        var rowsView = new ChartListVirtualView(
            sourceRows,
            order,
            row => mainChartList.RowProjection.CreateSubsetRow(
                request.Library,
                row,
                request.ApplyResourceHealthProjection),
            distinctFolderCount, CreateSubsetFilter(request.KeywordFilter, request.ModeFilter), scoreInput, scorePosition,
            (kind, count) => mainChartList.RowProjection.NormalInputWorkObserver?.Invoke(kind, count), referenceInput, catalogRevision,
            selectionScope: (request.Library, request.TreeMode, request.SubsetName, request.SourceProjectionMode, request.SelectionContext));
        presentation = RegularChartListPresentationResult.ForVirtual(
                new MainChartListRowsApplyRequest
                {
                    Rows = rowsView,
                    ColumnsSettings = request.ColumnSelection.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                    Summary = request.PreserveSummary
                        ? MainChartListSummaryUpdate.Preserve()
                        : MainChartListSummaryUpdate.NormalCounts(rowsView.Count, distinctFolderCount),
                    ColumnSettingReuse = request.ColumnSelection.Reused,
                    ColumnPreparationMs = request.ColumnSelection.ElapsedMs,
                    TerminalStageStartMs = stageStartMs,
                    Stopwatch = stopwatch
                },
                request.ColumnSelection,
                request.Mode,
                stopwatch,
                request.DetailSourceRetirement);
        return new RegularVirtualChartSubsetApplyResult(
            default,
            rowsView,
            order,
            sortCacheKey,
            sourceRows.Count,
            keywordCount,
            modeCount,
            distinctFolderCount,
            sourceRowsSignature,
            sourceRowsMs,
            keywordStageMs,
            modeStageMs,
            sortStageMs,
            sortCacheHit,
            orderCacheLookupMs,
            orderBuildMs);
    }

    private ChartListOrder GetOrCreateVirtualChartSubsetOrder(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        BMSLibrary library,
        string columnName,
        ListSortDirection direction,
        int treeMode,
        string subsetName,
        long sourceRowsSignature,
        long expectedSourceGeneration,
        long expectedSortKeyGeneration,
        RegularChartListExternalVersions externalVersions,
        out bool cacheHit,
        out VirtualChartSubsetSortCacheKey cacheKey,
        out long orderCacheLookupMs,
        out long orderBuildMs, int? scorePosition = null, ChartListReferenceInput referenceInput = null, int? catalogRevision = null)
    {
        var lookupStopwatch = Stopwatch.StartNew();
        int rowCount = sourceRows?.Count ?? 0;
        if (!ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out string normalizedColumnName))
        {
            throw new ArgumentException("Unsupported virtual chart subset sort column.", nameof(columnName));
        }
        cacheKey = CreateVirtualSubsetOrderKey(
            expectedSourceGeneration,
            expectedSortKeyGeneration,
            treeMode,
            subsetName,
            sourceRowsSignature,
            normalizedColumnName,
            direction,
            rowCount,
            CaptureExternalVersions(library, externalVersions, scorePosition), referenceInput?.Revision);
        if ((!catalogRevision.HasValue || library == null || library.CatalogRevision == catalogRevision.Value)
            && TryGetVirtualSubsetOrder(cacheKey, out ChartListOrder cachedOrder)
            && (rowCount == 0 || !sourceRows[0].UsesCatalogCurrentValues || cachedOrder.IsSortedFor(sourceRows)))
        {
            lookupStopwatch.Stop();
            cacheHit = true;
            orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
            orderBuildMs = 0L;
            return cachedOrder;
        }

        lookupStopwatch.Stop();
        var buildStopwatch = Stopwatch.StartNew();
        if (!ChartListOrder.TryCreate(sourceRows, normalizedColumnName, direction, out ChartListOrder order))
        {
            throw new ArgumentException("Unsupported virtual chart subset sort column.", nameof(columnName));
        }
        buildStopwatch.Stop();
        TryPublishVirtualSubsetOrder(
            cacheKey,
            order,
            CaptureExternalVersions(library, externalVersions), referenceInput, catalogRevision);
        cacheHit = false;
        orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
        orderBuildMs = buildStopwatch.ElapsedMilliseconds;
        return order;
    }

    internal static int[] ApplyVirtualChartSubsetFilters(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        GridKeywordSearchQuery keywordQuery,
        ChartModeFilter modeFilter,
        out int keywordFilteredCount,
        out int modeFilteredCount,
        out long keywordStageMs,
        out long modeStageMs)
    {
        int[] indexes = [.. (orderedIndexes ?? []).Where(index => sourceRows != null && index >= 0 && index < sourceRows.Count)];
        var stageStopwatch = Stopwatch.StartNew();
        if (keywordQuery != null && keywordQuery.HasTokens)
        {
            indexes = indexes.AsParallel().AsOrdered()
                .Where(index => keywordQuery.MatchesChartListSourceRow(sourceRows[index]))
                .ToArray();
        }
        keywordFilteredCount = indexes.Length;
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;
        stageStopwatch.Restart();
        if (modeFilter != ChartModeFilter.All)
        {
            HashSet<int?> modeValues = RegularChartListFilterService.CreateModeFilterValueSet(modeFilter);
            indexes = [.. indexes.Where(index => modeValues.Contains(sourceRows[index]?.Mode))];
        }
        modeFilteredCount = indexes.Length;
        modeStageMs = stageStopwatch.ElapsedMilliseconds;
        return indexes;
    }

    internal static long ComputeVirtualChartSubsetSourceRowsSignature(
        IReadOnlyList<ChartListSourceRow> sourceRows)
    {
        unchecked
        {
            long hash = 17L;
            hash = (hash * 397L) ^ (sourceRows?.Count ?? 0);
            if (sourceRows == null)
            {
                return hash;
            }
            foreach (ChartListSourceRow row in sourceRows)
            {
                hash = (hash * 397L) ^ (row?.Kind == ChartFileKind.Bms ? 1 : 2);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Path ?? string.Empty);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Hash ?? string.Empty);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Sha256 ?? string.Empty);
                hash = (hash * 397L) ^ (row?.PackageEntry?.ProjectionVersion ?? 0);
            }
            return hash;
        }
    }

    private IReadOnlyList<ChartListSourceRow> GetOrCreateVirtualNormalLibrarySourceRows(
        BMSLibrary library,
        bool includeBmsonRows,
        out bool cacheHit,
        out long sourceGenerationAtLookup,
        out long sortKeyGenerationAtLookup)
    {
        CatalogChartCollectionSnapshot snapshot = library?.CaptureNormalLibraryChartSnapshot(includeBmsonRows);
        lock (syncRoot)
        {
            if (snapshot != null && virtualSourceRows is NormalLibrarySourceRows captured
                && ReferenceEquals(captured.Library, library)
                && captured.Snapshot.CatalogRevision != snapshot.CatalogRevision)
            {
                // 通知未消費でも明示要求は確定入力から作る。件数が同じ別所属の添字も捨てる。
                if (virtualSourceRowsGeneration == sourceGeneration)
                {
                    sourceGeneration++;
                }
                ClearAllSortCachesUnsafe(clearSourceRows: true);
            }
        }
        RegularVirtualSourceRowsLookup lookup = LookupVirtualSourceRows(library, includeBmsonRows);
        if (lookup.CacheHit)
        {
            cacheHit = true;
            sourceGenerationAtLookup = lookup.SourceGeneration;
            sortKeyGenerationAtLookup = lookup.SortKeyGeneration;
            return lookup.Rows;
        }

        (BMSLibrary Library, List<PlaylistReferenceCommitReceipt> Commits) referenceCapture = (Library: library, Commits: new List<PlaylistReferenceCommitReceipt>());
        lock (syncRoot) { normalReferenceCaptures.Add(referenceCapture); }
        try
        {
            NormalLibrarySourceRows sourceRows = mainChartList.RowProjection.BuildNormalSourceRows(library, snapshot, includeBmsonRows);
            // 元集合の初回投影/hash登録と同じ一巡で参照を捕捉し、同時に届いた実差分も公開前へ反映します。
            sourceRows.PrepareForOrder();
            lock (syncRoot)
            {
                foreach (PlaylistReferenceCommitReceipt commit in referenceCapture.Commits)
                { sourceRows = sourceRows.WithReferenceCommit(commit, out _); }
                TryPublishVirtualSourceRows(lookup, sourceRows);
            }
            cacheHit = false;
            sourceGenerationAtLookup = lookup.SourceGeneration;
            sortKeyGenerationAtLookup = lookup.SortKeyGeneration;
            return sourceRows;
        }
        finally { lock (syncRoot) { normalReferenceCaptures.Remove(referenceCapture); } }
    }

    private ChartListOrder GetOrCreateVirtualNormalLibraryOrder(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        BMSLibrary library,
        string columnName,
        ListSortDirection direction,
        long expectedSourceGeneration,
        long expectedSortKeyGeneration,
        RegularChartListExternalVersions externalVersions,
        bool useCache,
        out bool cacheHit,
        out NormalLibrarySortCacheKey cacheKey,
        out long orderCacheLookupMs,
        out long orderBuildMs, int? scorePosition = null)
    {
        var lookupStopwatch = Stopwatch.StartNew();
        int rowCount = sourceRows?.Count ?? 0;
        if (!ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out string normalizedColumnName))
        {
            throw new ArgumentException("Unsupported virtual normal library sort column.", nameof(columnName));
        }

        if (useCache)
        {
            cacheKey = CreateVirtualOrderKey(
                expectedSourceGeneration,
                expectedSortKeyGeneration,
                normalizedColumnName,
                direction,
                rowCount,
                CaptureExternalVersions(library, externalVersions, scorePosition ?? (sourceRows as NormalLibrarySourceRows)?.ScoreInput?.Position),
                (sourceRows as NormalLibrarySourceRows)?.References.Revision);
            if (TryGetVirtualOrder(cacheKey, out ChartListOrder cachedOrder))
            {
                lookupStopwatch.Stop();
                cacheHit = true;
                orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
                orderBuildMs = 0L;
                return cachedOrder;
            }
        }
        else
        {
            cacheKey = default;
        }

        lookupStopwatch.Stop();
        var buildStopwatch = Stopwatch.StartNew();
        if (!ChartListOrder.TryCreate(sourceRows, normalizedColumnName, direction, out ChartListOrder order))
        {
            throw new ArgumentException("Unsupported virtual normal library sort column.", nameof(columnName));
        }
        buildStopwatch.Stop();
        if (useCache)
        {
            TryPublishVirtualOrder(cacheKey, order, CaptureExternalVersions(library, externalVersions), sourceRows as NormalLibrarySourceRows);
        }
        cacheHit = false;
        orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
        orderBuildMs = buildStopwatch.ElapsedMilliseconds;
        return order;
    }

    private static RegularChartListExternalVersions CaptureExternalVersions(
        BMSLibrary requestLibrary,
        RegularChartListExternalVersions fallback, int? scorePosition = null)
    {
        return requestLibrary == null
            ? fallback
            : new RegularChartListExternalVersions(
                scorePosition ?? requestLibrary.ScoreSnapshotVersion,
                requestLibrary.ChartInfoIndexVersion,
                requestLibrary.MaintenanceHydrationCompletedVersion);
    }

    internal static int[] ApplyVirtualNormalLibraryFilters(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        RegularNormalLibraryTreeFilter treeFilter,
        GridKeywordSearchQuery keywordQuery,
        ChartModeFilter modeFilter,
        out int folderFilteredCount,
        out int keywordFilteredCount,
        out int modeFilteredCount,
        out long folderStageMs,
        out long keywordStageMs,
        out long modeStageMs)
    {
        int[] indexes = [.. (orderedIndexes ?? []).Where(index => sourceRows != null && index >= 0 && index < sourceRows.Count)];
        var stageStopwatch = Stopwatch.StartNew();
        if (treeFilter != null)
        {
            indexes = [.. indexes.Where(index => treeFilter.Matches(sourceRows[index]))];
        }
        folderFilteredCount = indexes.Length;
        folderStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        if (keywordQuery != null && keywordQuery.HasTokens)
        {
            indexes = indexes.AsParallel().AsOrdered()
                .Where(index => keywordQuery.MatchesChartListSourceRow(sourceRows[index]))
                .ToArray();
        }
        keywordFilteredCount = indexes.Length;
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        if (modeFilter != ChartModeFilter.All)
        {
            HashSet<int?> modeValues = RegularChartListFilterService.CreateModeFilterValueSet(modeFilter);
            indexes = [.. indexes.Where(index => modeValues.Contains(sourceRows[index]?.Mode))];
        }
        modeFilteredCount = indexes.Length;
        modeStageMs = stageStopwatch.ElapsedMilliseconds;
        return indexes;
    }

    internal static int CountDistinctFoldersByIndex(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        Func<bool> shouldStop,
        out int scannedIndexCount,
        out bool stopped)
    {
        scannedIndexCount = 0;
        stopped = false;
        if (sourceRows == null || orderedIndexes == null)
        {
            return -1;
        }

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int position = 0; position < orderedIndexes.Count; position++)
        {
            if (position > 0
                && (position & 1023) == 0
                && shouldStop?.Invoke() == true)
            {
                stopped = true;
                break;
            }
            int sourceIndex = orderedIndexes[position];
            scannedIndexCount++;
            if (sourceIndex < 0 || sourceIndex >= sourceRows.Count)
            {
                continue;
            }
            string folder = sourceRows[sourceIndex]?.Folder;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                folders.Add(folder);
            }
        }
        return folders.Count;
    }

    private RegularChartListTerminalResult TryCommitCore(
        RegularChartListRequestLease lease,
        RegularChartListPresentationResult presentation)
    {
        if (presentation.InputIsCurrent?.Invoke() == false)
        { presentation.Completion?.TrySetResult(RegularChartListTerminalResult.Stale()); return RegularChartListTerminalResult.Stale(); }
        bool prepared;
        lock (syncRoot) { prepared = IsCurrentUnsafe(lease) && PrepareNormalCandidateUnsafe(lease, presentation); }
        if (!prepared)
        {
            StartPendingLibraryChangeDelivery();
            return RegularChartListTerminalResult.Stale();
        }

        PlaylistMainTablePresentationCommit mainTablePresentationCommit = null;
        PlaylistSourceClearCommitResult detailSourceClearCommit = null;
        bool CommitRegularPresentation(Action commitRows)
        {
            lock (syncRoot)
            {
                if (!IsCurrentUnsafe(lease) || presentation.InputIsCurrent?.Invoke() == false
                    || !PrepareNormalCandidateUnsafe(lease, presentation))
                {
                    return false;
                }
                if (presentation.CreateRowsRequest().Rows is ChartListVirtualView normalView && normalView.NormalInput != null)
                {
                    rowCache.Prune(normalView.NormalInput.Snapshot);
                    virtualSourceRows = normalView.NormalInput;
                    virtualSourceRowsLibrary = normalView.NormalInput.Library;
                    virtualSourceRowsGeneration = sourceGeneration;
                }
                commitRows();
                normalBuildCommits = null;
                normalBuildScoreChanges = null;
                normalBuildReferenceCommits = null;
                normalBuildLibrary = null;
                normalBuildMd5Keys.Clear();
                normalBuildSha256Keys.Clear();
                pendingNormalPresentation = null;
                mainTablePresentationCommit = playlistWorkspace.CommitMainTablePresentationWithoutNotification(
                    presentation.ColumnSelection,
                    playlistDetailActive: false,
                    playlistSummaryActive: false);
                if (presentation.DetailSourceRetirement != null)
                {
                    detailSourceClearCommit =
                        playlistWorkspace.CommitDetailSourceClearForRegularView(
                            presentation.DetailSourceRetirement);
                }
                if (presentation.Build != null)
                {
                    folderRows = presentation.Build.Stage.FolderRows;
                    keywordRows = presentation.Build.Stage.KeywordRows;
                    modeRows = presentation.Build.Stage.ModeRows;
                    folderSortSourceSnapshot = presentation.Build.Sort.FolderSortSourceSnapshot;
                    folderSortResultSnapshot = presentation.Build.Sort.FolderSortResultSnapshot;
                    folderSortColumnName = presentation.Build.Sort.FolderSortColumnName;
                    folderSortDirection = presentation.Build.Sort.FolderSortDirection;
                    if (presentation.Build.PendingCacheKey.HasValue && presentation.Build.PendingCacheRows != null)
                    {
                        sortCache[presentation.Build.PendingCacheKey.Value] = presentation.Build.PendingCacheRows;
                    }
                }
                mainChartList.CommitAppliedColumnMode(presentation.ColumnSelection.AppliedMode);
                mainChartList.CommitCompletion(new MainChartListCompletion(
                    lease.RequestId,
                    Stopwatch.GetTimestamp(),
                    Thread.CurrentThread.ManagedThreadId,
                    presentation.Mode,
                    presentation.Stopwatch.ElapsedMilliseconds));
                return true;
            }
        }

        void PublishRelatedPresentation()
        {
            List<Exception> publishExceptions = [];
            try
            {
                playlistWorkspace.PublishMainTablePresentation(mainTablePresentationCommit);
            }
            catch (Exception ex)
            {
                publishExceptions.Add(ex);
            }
            try
            {
                playlistWorkspace.PublishDetailSourceClearForRegularView(detailSourceClearCommit);
            }
            catch (Exception ex)
            {
                publishExceptions.Add(ex);
            }
            if (publishExceptions.Count > 0)
            {
                throw new AggregateException(publishExceptions);
            }
        }

        MainChartListPresentationApplyResult applied;
        try
        {
            if (Net10PerformanceLog.IsEnabled)
            {
                Net10PerformanceLog.Write(
                    PerformanceInteraction.Existing("normal_library", lease.RequestId),
                    "ui_started");
            }
            applied = mainChartList.ApplyPresentation(
                presentation.CreateRowsRequest(),
                CommitRegularPresentation,
                PublishRelatedPresentation);
        }
        catch (MainChartListPresentationPublishException ex)
        {
            throw new RegularChartListTerminalPublishException(ex.InnerException ?? ex);
        }
        if (applied.WasApplied && Net10PerformanceLog.IsEnabled)
        {
            MainChartListRowsApplyRequest rowsRequest = presentation.CreateRowsRequest();
            Net10PerformanceLog.Write(
                PerformanceInteraction.Existing("normal_library", lease.RequestId),
                "ui_applied",
                "rows=" + (rowsRequest.Rows?.Count ?? 0)
                + " totalMs=" + presentation.Stopwatch.ElapsedMilliseconds);
        }
        if (applied.WasApplied && presentation.CreateRowsRequest().Rows is ChartListVirtualView normalRows
            && presentation.CreateRowsRequest().Summary.Kind != MainChartListSummaryKind.Preserve)
        {
            mainChartList.UpdateSummaryText(normalRows);
        }
        if (!applied.WasApplied) { StartPendingLibraryChangeDelivery(); }
        RegularChartListTerminalResult terminal = applied.WasApplied
            ? RegularChartListTerminalResult.Committed(applied.RowsApply)
            : RegularChartListTerminalResult.Stale();
        if (applied.WasApplied || presentation.InputIsCurrent?.Invoke() == false) { presentation.Completion?.TrySetResult(terminal); }
        return terminal;
    }

    internal Task StopAsync()
    {
        CancellationTokenSource requestCancellation;
        CancellationTokenSource prewarmCancellation;
        Task prewarmCompletion;
        Task normalLibraryRefreshCompletion;
        Task[] viewAndMutationTasksToDrain;
        PropertyChangedEventListener normalLibraryRefreshListenerToDispose;
        lock (normalLibraryRefreshApplyLock)
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return shutdownCompletion;
                }
                disposed = true;
                requestCancellation = currentCancellation;
                currentCancellation = null;
                currentRequestId = 0L;
                currentNavigation = null;
                regularRequestActive = false;
                normalLibraryRefreshListenerToDispose = normalLibraryRefreshListener;
                normalLibraryRefreshListener = null;
                normalLibraryRefreshSource?.DisconnectDuplicateDataPreparation(PrepareAcceptedDuplicateDataAsync);
                normalLibraryRefreshSource = null;
                normalBuildCommits = null;
                normalBuildScoreChanges = null;
                normalBuildReferenceCommits = null;
                normalBuildLibrary = null;
                normalBuildMd5Keys.Clear();
                normalBuildSha256Keys.Clear();
                pendingNormalPresentation = null;
                cachedNormalChanges.Clear();
                normalLibraryRefreshHandledNotificationVersion = 0;
                normalLibraryRefreshRequestedNotificationVersion = 0;
                normalLibraryRefreshRequestedReason = null;
                normalLibraryRefreshCompletion = Task.WhenAll(normalCandidateDeliveries.Append(normalLibraryRefreshDrainCompletion));
                prewarmCancellation = virtualOrderPrewarmCancellation;
                prewarmCompletion = virtualOrderPrewarmCompletion;
                viewAndMutationTasksToDrain = [.. folderRenameTasks, .. viewPreparationTasks];
                shutdownCompletion = DrainShutdownAsync(
                    prewarmCompletion,
                    prewarmCancellation,
                    normalLibraryRefreshCompletion,
                    viewAndMutationTasksToDrain);
            }
        }
        mainChartList.AppliedColumnModeCommitted -= MainChartListAppliedColumnModeCommitted;
        normalLibraryRefreshListenerToDispose?.Dispose();
        CancelAndDispose(requestCancellation);
        Cancel(prewarmCancellation);
        return shutdownCompletion;
    }

    public void Dispose()
    {
        mainChartList.AppliedColumnModeCommitted -= MainChartListAppliedColumnModeCommitted;
        _ = StopAsync();
    }

    private RegularChartListSortResult ApplySort(
        RegularChartListRequestLease lease,
        RegularChartListRefreshRequest request,
        RegularChartListStageState stage,
        RegularChartListBuildInput input,
        List<LibraryChartRow> existingFolderSortSource,
        List<LibraryChartRow> existingFolderSortResult,
        string existingFolderSortColumn,
        ListSortDirection? existingFolderSortDirection,
        out NormalLibrarySortCacheKey? pendingCacheKey,
        out List<LibraryChartRow> pendingCacheRows)
    {
        pendingCacheKey = null;
        pendingCacheRows = null;
        List<LibraryChartRow> rows = stage.ModeRows as List<LibraryChartRow> ?? [.. stage.ModeRows];
        if (request.Mode > MainViewUpdateMode.SortUpdated)
        {
            return RegularChartListSortResult.Bypass(new List<LibraryChartRow>(rows), existingFolderSortSource, existingFolderSortResult, existingFolderSortColumn, existingFolderSortDirection);
        }

        string columnName = request.SortColumnName;
        ListSortDirection direction = request.SortDirection;
        bool isTreeSelectionRequest = request.RequestedMode != MainViewUpdateMode.TreeViewFilterNotChanged
            && request.RequestedMode < MainViewUpdateMode.KeywordFilterUpdated;
        bool isFolderMode = request.Mode == MainViewUpdateMode.FolderFilterSelected;
        bool fullNormalResult = input.CurrentTreeMode == MainViewUpdateMode.FolderFilterSelected
            && !input.HasVirtualNormalLibraryTreeFilter
            && string.IsNullOrWhiteSpace(request.KeywordFilter)
            && request.ModeFilter == ChartModeFilter.All
            && !input.IsPlaylistDetailView
            && rows.Count == stage.FolderCount && rows.Count == stage.KeywordCount && rows.Count == stage.ModeCount;

        if (isFolderMode && isTreeSelectionRequest
            && existingFolderSortSource != null && existingFolderSortResult != null
            && string.Equals(existingFolderSortColumn, columnName, StringComparison.Ordinal)
            && existingFolderSortDirection == direction
            && IsSameReferenceSequence(rows, existingFolderSortSource))
        {
            return RegularChartListSortResult.ReuseFolderSnapshot(existingFolderSortResult, rows, existingFolderSortResult, columnName, direction);
        }

        NormalLibrarySortCacheKey cacheKey = default;
        if (fullNormalResult
            && ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out string normalizedColumn))
        {
            cacheKey = input.SortCacheGeneration.Create(normalizedColumn, direction, rows.Count);
            lock (syncRoot)
            {
                if (IsCurrentUnsafe(lease) && sortCache.TryGetValue(cacheKey, out List<LibraryChartRow> cachedRows))
                {
                    return RegularChartListSortResult.ReuseSortCache(cachedRows, isFolderMode ? rows : existingFolderSortSource, existingFolderSortResult, isFolderMode ? columnName : existingFolderSortColumn, isFolderMode ? direction : existingFolderSortDirection, CreateSortMetrics(cacheKey, 0L, cacheHit: true));
                }
            }
        }

        lease.Token.ThrowIfCancellationRequested();
        List<LibraryChartRow> sortedRows = LibraryChartRowSortEngine.SortForMainView(
            rows,
            request.Sort,
            input.IsPlaylistDetailView,
            out string sortProfile,
            out LibraryChartSortMetrics metrics);
        lease.Token.ThrowIfCancellationRequested();
        if (fullNormalResult && !string.IsNullOrWhiteSpace(cacheKey.ColumnName))
        {
            pendingCacheKey = cacheKey;
            pendingCacheRows = sortedRows;
            metrics = CreateSortMetrics(cacheKey, metrics.SortMs, cacheHit: false);
        }
        return RegularChartListSortResult.Sorted(sortedRows, sortProfile, isFolderMode ? rows : existingFolderSortSource, isFolderMode ? sortedRows : existingFolderSortResult, isFolderMode ? columnName : existingFolderSortColumn, isFolderMode ? direction : existingFolderSortDirection, metrics);
    }

    private bool IsCurrentUnsafe(RegularChartListRequestLease lease)
    {
        return regularRequestActive
            && IsCurrentNavigationUnsafe(lease.Navigation)
            && currentRequestId == lease.RequestId
            && !lease.Token.IsCancellationRequested;
    }

    private static LibraryChartSortMetrics CreateSortMetrics(NormalLibrarySortCacheKey key, long sortMs, bool cacheHit)
    {
        string profile = "library_chart_string_fast_ordinal_ignore_case";
        string stringKind = "ordinal_ignore_case";
        string propertyType = nameof(String);
        if (ChartListOrder.TryGetVirtualSortColumnMetadata(key.ColumnName, out ChartListOrderColumnMetadata metadata))
        {
            propertyType = metadata.PropertyTypeName;
            stringKind = metadata.StringSortKind;
            if (metadata.KeyKind == ChartListOrderKeyKind.Comparable)
            {
                profile = "library_chart_comparable_fast";
            }
        }
        return new LibraryChartSortMetrics(key.RowCount, key.ColumnName, key.Direction, propertyType, profile, stringKind, sortMs, cacheHit, key.ColumnName, key.SortKeyGeneration, cacheHit);
    }

    private static bool IsSameReferenceSequence<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) where T : class
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool DoesSortColumnDependOn(string columnName, MainViewDataDependency dependency)
    {
        if (!ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata))
        {
            return false;
        }
        return metadata.Dependency == dependency
            || (metadata.Dependency == MainViewDataDependency.Warning
                && dependency is MainViewDataDependency.InstallDestination or MainViewDataDependency.Maintenance);
    }

    private CancellationTokenSource InvalidateCurrentRequestUnsafe()
    {
        CancellationTokenSource previous = currentCancellation;
        currentCancellation = null;
        currentRequestId = MainViewBuildRequestSequence.Next();
        regularRequestActive = false;
        normalBuildCommits = null;
        normalBuildScoreChanges = null;
        normalBuildReferenceCommits = null;
        normalBuildLibrary = null;
        normalBuildMd5Keys.Clear();
        normalBuildSha256Keys.Clear();
        pendingNormalPresentation = null;
        return previous;
    }

    private CancellationTokenSource GetActivePrewarmCancellationUnsafe()
    {
        return virtualOrderPrewarmCompletion.IsCompleted
            ? null
            : virtualOrderPrewarmCancellation;
    }

    private long ReadGeneration(Func<long> accessor)
    {
        lock (syncRoot)
        {
            return accessor();
        }
    }

    private int GetCacheCountUnsafe()
    {
        return sortCache.Count
            + virtualOrderCache.Count
            + virtualSubsetOrderCache.Count
            + (virtualSourceRows != null ? 1 : 0);
    }

    private void ClearAllSortCachesUnsafe(bool clearSourceRows)
    {
        sortCache.Clear();
        virtualOrderCache.Clear();
        virtualSubsetOrderCache.Clear();
        if (clearSourceRows)
        {
            ClearVirtualSourceRowsUnsafe();
        }
    }

    private void ClearVirtualSourceRowsUnsafe()
    {
        cachedNormalChanges.Clear();
        virtualSourceRows = null;
    }

    private void IncrementDependencyGenerationUnsafe(MainViewDataDependency dependency)
    {
        switch (dependency)
        {
            case MainViewDataDependency.Warning:
                warningGeneration++;
                break;
            case MainViewDataDependency.InstallDestination:
                installDestinationGeneration++;
                break;
            case MainViewDataDependency.Maintenance:
                maintenanceGeneration++;
                break;
            case MainViewDataDependency.ReferenceTables:
                break;
        }
    }

    private static int PruneCacheUnsafe<TValue>(
        Dictionary<NormalLibrarySortCacheKey, TValue> cache,
        MainViewDataDependency dependency)
    {
        NormalLibrarySortCacheKey[] keys = [.. cache.Keys];
        int removed = 0;
        foreach (NormalLibrarySortCacheKey key in keys)
        {
            if (DoesSortColumnDependOn(key.ColumnName, dependency) && cache.Remove(key))
            {
                removed++;
            }
        }
        return removed;
    }

    private static int PruneCacheUnsafe(
        Dictionary<VirtualChartSubsetSortCacheKey, ChartListOrder> cache,
        MainViewDataDependency dependency)
    {
        VirtualChartSubsetSortCacheKey[] keys = [.. cache.Keys];
        int removed = 0;
        foreach (VirtualChartSubsetSortCacheKey key in keys)
        {
            if (DoesSortColumnDependOn(key.ColumnName, dependency) && cache.Remove(key))
            {
                removed++;
            }
        }
        return removed;
    }

    private void ResolveDependencyGenerationsUnsafe(
        string columnName,
        RegularChartListExternalVersions externalVersions,
        out long score,
        out long chartInfo,
        out long maintenance,
        out long warning,
        out long installDestination,
        out long referenceTables)
    {
        score = 0L;
        chartInfo = 0L;
        maintenance = 0L;
        warning = 0L;
        installDestination = 0L;
        referenceTables = 0L;
        if (!ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata))
        {
            return;
        }

        switch (metadata.Dependency)
        {
            case MainViewDataDependency.Score:
                score = externalVersions.Score;
                break;
            case MainViewDataDependency.ChartInfo:
                chartInfo = externalVersions.ChartInfo;
                break;
            case MainViewDataDependency.Maintenance:
                maintenance = GetMaintenanceGenerationUnsafe(externalVersions);
                break;
            case MainViewDataDependency.Warning:
                warning = warningGeneration;
                installDestination = installDestinationGeneration;
                maintenance = GetMaintenanceGenerationUnsafe(externalVersions);
                break;
            case MainViewDataDependency.InstallDestination:
                installDestination = installDestinationGeneration;
                break;
            case MainViewDataDependency.ReferenceTables:
                referenceTables = virtualSourceRows?.References.Revision ?? 0;
                break;
        }
    }

    private long GetMaintenanceGenerationUnsafe(RegularChartListExternalVersions externalVersions)
    {
        return (externalVersions.MaintenanceHydration << 32)
            ^ (maintenanceGeneration & 0xffffffffL);
    }

    private bool IsCurrentUnsafe(
        NormalLibrarySortCacheKey key,
        RegularChartListExternalVersions externalVersions)
    {
        NormalLibrarySortCacheGenerationSnapshot current = CaptureSortGenerationUnsafe(key.ColumnName, externalVersions);
        return sourceGeneration == key.SourceGeneration
            && sortKeyGeneration == key.SortKeyGeneration
            && current.Score == key.ScoreGeneration
            && current.ChartInfo == key.ChartInfoGeneration
            && current.Maintenance == key.MaintenanceGeneration
            && current.Warning == key.WarningGeneration
            && current.InstallDestination == key.InstallDestinationGeneration
            && current.ReferenceTables == key.ReferenceTablesGeneration;
    }

    private bool IsCurrentUnsafe(
        VirtualChartSubsetSortCacheKey key,
        RegularChartListExternalVersions externalVersions, long? referencePosition = null)
    {
        NormalLibrarySortCacheGenerationSnapshot current = CaptureSortGenerationUnsafe(key.ColumnName, externalVersions);
        return sourceGeneration == key.SourceGeneration
            && sortKeyGeneration == key.SortKeyGeneration
            && current.Score == key.ScoreGeneration
            && current.ChartInfo == key.ChartInfoGeneration
            && current.Maintenance == key.MaintenanceGeneration
            && current.Warning == key.WarningGeneration
            && current.InstallDestination == key.InstallDestinationGeneration
            && (referencePosition.HasValue && MainViewRefreshDecisionService.GetSortColumnDependency(key.ColumnName) == MainViewDataDependency.ReferenceTables
                ? referencePosition.Value : current.ReferenceTables) == key.ReferenceTablesGeneration;
    }

    private NormalLibrarySortCacheGenerationSnapshot CaptureSortGenerationUnsafe(
        string columnName,
        RegularChartListExternalVersions externalVersions)
    {
        ResolveDependencyGenerationsUnsafe(
            columnName,
            externalVersions,
            out long score,
            out long chartInfo,
            out long maintenance,
            out long warning,
            out long installDestination,
            out long referenceTables);
        return new NormalLibrarySortCacheGenerationSnapshot(
            sourceGeneration,
            sortKeyGeneration,
            score,
            chartInfo,
            maintenance,
            warning,
            installDestination,
            referenceTables);
    }

    private static void CancelAndDispose(CancellationTokenSource cancellation)
    {
        if (cancellation == null)
        {
            return;
        }

        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        if (cancellation == null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task DrainShutdownAsync(
        Task prewarmCompletion,
        CancellationTokenSource prewarmCancellation,
        Task normalLibraryRefreshCompletion,
        IReadOnlyList<Task> viewAndMutationTasks)
    {
        try
        {
            await (prewarmCompletion ?? Task.CompletedTask).ConfigureAwait(false);
            try { await (normalLibraryRefreshCompletion ?? Task.CompletedTask).ConfigureAwait(false); }
            catch
            {
                // 配送要求の失敗は元のTaskに保持し、停止はその終端を回収して残る資源の解放へ進む。
            }
            try
            {
                await Task.WhenAll(viewAndMutationTasks ?? []).ConfigureAwait(false);
            }
            catch
            {
                // rename と package 構築の fault は ObserveFault で既に観測済みのため、
                // shutdown は残りの resource の drain と解放を継続する。
            }
        }
        finally
        {
            prewarmCancellation?.Dispose();
        }
    }

}

internal sealed class NormalLibraryRefreshAppliedEventArgs : EventArgs
{
    internal NormalLibraryRefreshAppliedEventArgs(
        NormalLibraryRefreshNotificationBatch notificationBatch,
        string reason)
    {
        NotificationBatch = notificationBatch ?? NormalLibraryRefreshNotificationBatch.Empty;
        Reason = reason ?? string.Empty;
    }

    internal NormalLibraryRefreshNotificationBatch NotificationBatch { get; }

    internal string Reason { get; }
}

internal sealed class RegularChartListRequestLease
{
    /// <summary>構築取消に加え、元の正規画面受付を行採用まで保持します。</summary>
    internal RegularChartListRequestLease(long requestId, CancellationToken token, RegularChartNavigationRequest navigation = null)
    {
        RequestId = requestId;
        Token = token;
        Navigation = navigation;
    }
    /// <summary>この構築を開始した画面受付です。キャッシュ自己通知と独立して比較します。</summary>
    internal RegularChartNavigationRequest Navigation { get; }
    internal long RequestId { get; }
    internal CancellationToken Token { get; }
}

internal sealed class RegularChartListPrewarmLease : IDisposable
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int completed;

    /// <summary>Initializes an immutable lease for one library-bound prewarm run.</summary>
    internal RegularChartListPrewarmLease(int runId, CancellationToken token, BMSLibrary library)
    {
        RunId = runId;
        Token = token;
        Library = library;
    }

    internal int RunId { get; }

    internal CancellationToken Token { get; }

    /// <summary>Gets the exact library reserved when the lease was issued, or null for a no-library test boundary.</summary>
    internal BMSLibrary Library { get; }

    internal Task Completion => completion.Task;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
        {
            completion.TrySetResult(true);
        }
    }
}

internal sealed class RegularChartListBuildInput
{
    internal MainViewUpdateMode CurrentTreeMode { get; set; }
    internal bool HasVirtualNormalLibraryTreeFilter { get; set; }
    internal bool IsPlaylistDetailView { get; set; }
    internal bool HasFolderRowsOverride { get; set; }
    internal IEnumerable<LibraryChartRow> FolderRowsOverride { get; set; }
    internal NormalLibrarySortCacheGenerationSnapshot SortCacheGeneration { get; set; }
    internal Stopwatch Stopwatch { get; set; }
}

internal sealed class RegularChartListEntryRequest
{
    /// <summary>元の画面受付を利用権発行・採用まで渡します。独立した直接呼出しではnullです。</summary>
    internal RegularChartNavigationRequest Navigation { get; set; }

    internal BMSLibrary Library { get; set; }

    /// <summary>UIで捕捉した完成結果です。再失効を最終採用でも検査します。</summary>
    internal RegularDuplicateViewInput DuplicateInput { get; set; }

    /// <summary>保守後の選択解決で所属の消失を確定した空一覧です。データ未準備の代用には使いません。</summary>
    internal RegularChartListSubsetSource ConfirmedUnresolvedDuplicateSource { get; set; }

    internal ChartListFilterSnapshot Filters { get; set; }

    internal MainViewUpdateMode Mode { get; set; }

    internal MainViewUpdateMode RequestedMode { get; set; }

    internal object Parameter { get; set; }

    internal MainViewUpdateMode CurrentTreeMode { get; set; }

    internal object TreeParameter { get; set; }

    internal bool IncludeBmsonRows { get; set; }

    internal bool PreserveSummary { get; set; }

    internal Stopwatch Stopwatch { get; set; }
}

internal sealed class RegularChartListSubsetSource
{
    private RegularChartListSubsetSource()
    {
    }

    internal IEnumerable<ChartFile> SourceCharts { get; private set; }

    internal ChartListSourceProjectionMode SourceProjectionMode { get; private set; }

    internal bool ApplyResourceHealthProjection { get; private set; }

    internal string Name { get; private set; }

    internal static RegularChartListSubsetSource ForCharts(
        IEnumerable<ChartFile> charts,
        string name,
        ChartListSourceProjectionMode projectionMode = ChartListSourceProjectionMode.PreserveSourceProjection,
        bool applyResourceHealthProjection = false)
    {
        return new RegularChartListSubsetSource
        {
            SourceCharts = charts ?? [],
            SourceProjectionMode = projectionMode,
            ApplyResourceHealthProjection = applyResourceHealthProjection,
            Name = name ?? string.Empty
        };
    }


}

internal enum RegularChartListEntryRoute
{
    DefaultVirtual,
    SubsetVirtual,
    Materialized
}

internal readonly struct RegularChartListEntryResult
{
    internal RegularChartListEntryResult(bool wasCommitted, RegularChartListEntryRoute route, bool sortWasReset)
    {
        WasCommitted = wasCommitted;
        Route = route;
        SortWasReset = sortWasReset;
    }

    internal bool WasCommitted { get; }

    internal RegularChartListEntryRoute Route { get; }

    internal bool SortWasReset { get; }
}

internal sealed class RegularChartListBuildResult
{
    internal RegularChartListBuildResult(RegularChartListStageState stage, RegularChartListSortResult sort, NormalLibrarySortCacheKey? pendingCacheKey, List<LibraryChartRow> pendingCacheRows, long folderMs, long keywordMs, long modeMs, long sortMs)
    {
        Stage = stage;
        Sort = sort;
        PendingCacheKey = pendingCacheKey;
        PendingCacheRows = pendingCacheRows;
        FolderMs = folderMs;
        KeywordMs = keywordMs;
        ModeMs = modeMs;
        SortMs = sortMs;
    }
    internal RegularChartListStageState Stage { get; }
    internal RegularChartListSortResult Sort { get; }
    internal NormalLibrarySortCacheKey? PendingCacheKey { get; }
    internal List<LibraryChartRow> PendingCacheRows { get; }
    internal long FolderMs { get; }
    internal long KeywordMs { get; }
    internal long ModeMs { get; }
    internal long SortMs { get; }
}

internal enum RegularChartListPresentationKind
{
    Materialized,
    Virtual
}

internal sealed class RegularChartListPresentationResult
{
    /// <summary>捕捉したキャッシュ参照と失効版を、UIでの採用直前に照合します。未準備や再失効なら候補を採用しません。</summary>
    internal Func<bool> InputIsCurrent { get; set; }

    private RegularChartListPresentationResult(
        RegularChartListBuildResult build,
        MainChartListRowsApplyRequest rowsRequest,
        MainChartListColumnSelection columnSelection,
        MainViewUpdateMode mode,
        Stopwatch stopwatch,
        PlaylistSourceRetirementRequest detailSourceRetirement,
        RegularChartListPresentationKind kind)
    {
        Build = build;
        if (rowsRequest == null)
        {
            throw new ArgumentNullException(nameof(rowsRequest));
        }
        rows = rowsRequest.Rows;
        columnsSettings = rowsRequest.ColumnsSettings;
        selectionPolicy = rowsRequest.SelectionPolicy;
        summary = rowsRequest.Summary;
        columnSettingReuse = rowsRequest.ColumnSettingReuse;
        columnPreparationMs = rowsRequest.ColumnPreparationMs;
        terminalStageStartMs = rowsRequest.TerminalStageStartMs;
        rowsStopwatch = rowsRequest.Stopwatch;
        rowsAlreadyPrepared = rowsRequest.RowsAlreadyPrepared;
        ColumnSelection = columnSelection;
        Stopwatch = stopwatch ?? throw new ArgumentNullException(nameof(stopwatch));
        Mode = mode;
        DetailSourceRetirement = detailSourceRetirement;
        Kind = kind;
    }

    /// <summary>非同期package要求の採用終端です。配送待ちでも同じ候補と結果を呼出し元へ返します。</summary>
    internal TaskCompletionSource<RegularChartListTerminalResult> Completion { get; set; }

    internal RegularChartListBuildResult Build { get; }

    internal MainChartListColumnSelection ColumnSelection { get; }

    internal MainViewUpdateMode Mode { get; }

    internal Stopwatch Stopwatch { get; }

    internal PlaylistSourceRetirementRequest DetailSourceRetirement { get; }

    internal RegularChartListPresentationKind Kind { get; }

    private readonly IList rows;

    private readonly CustomTableColumnSettings columnsSettings;

    private readonly MainChartListSelectionPolicy selectionPolicy;

    private readonly MainChartListSummaryUpdate summary;

    private readonly bool columnSettingReuse;

    private readonly long columnPreparationMs;

    private readonly long terminalStageStartMs;

    private readonly Stopwatch rowsStopwatch;

    private readonly bool rowsAlreadyPrepared;

    internal MainChartListRowsApplyRequest CreateRowsRequest()
    {
        return new MainChartListRowsApplyRequest
        {
            Rows = rows,
            ColumnsSettings = columnsSettings,
            SelectionPolicy = selectionPolicy,
            Summary = rows is ChartListVirtualView normal
                && summary.Kind != MainChartListSummaryKind.Preserve
                ? MainChartListSummaryUpdate.NormalCounts(normal.Count, normal.DistinctFolderCount) : summary,
            ColumnSettingReuse = columnSettingReuse,
            ColumnPreparationMs = columnPreparationMs,
            TerminalStageStartMs = terminalStageStartMs,
            Stopwatch = rowsStopwatch,
            RowsAlreadyPrepared = rowsAlreadyPrepared,
            OperationContextMode = ColumnSelection.AppliedMode
        };
    }

    internal static RegularChartListPresentationResult ForMaterialized(
        RegularChartListBuildResult build,
        MainChartListRowsApplyRequest rowsRequest,
        MainChartListColumnSelection columnSelection,
        MainViewUpdateMode mode,
        Stopwatch stopwatch,
        PlaylistSourceRetirementRequest detailSourceRetirement = null)
    {
        if (build == null)
        {
            throw new ArgumentNullException(nameof(build));
        }
        if (rowsRequest == null)
        {
            throw new ArgumentNullException(nameof(rowsRequest));
        }
        if (!ReferenceEquals(rowsRequest.Rows, build.Sort.RowsView))
        {
            throw new ArgumentException("Materialized presentation rows must match the build result.", nameof(rowsRequest));
        }
        return new RegularChartListPresentationResult(
            build,
            rowsRequest,
            columnSelection,
            mode,
            stopwatch,
            detailSourceRetirement,
            RegularChartListPresentationKind.Materialized);
    }

    internal static RegularChartListPresentationResult ForVirtual(
        MainChartListRowsApplyRequest rowsRequest,
        MainChartListColumnSelection columnSelection,
        MainViewUpdateMode mode,
        Stopwatch stopwatch,
        PlaylistSourceRetirementRequest detailSourceRetirement = null)
    {
        return new RegularChartListPresentationResult(
            build: null,
            rowsRequest,
            columnSelection,
            mode,
            stopwatch,
            detailSourceRetirement,
            RegularChartListPresentationKind.Virtual);
    }
}

internal sealed class RegularMaterializedChartListApplyRequest
{
    /// <summary>元の画面受付を利用権発行・採用まで渡します。独立した直接呼出しではnullです。</summary>
    internal RegularChartNavigationRequest Navigation { get; set; }

    internal RegularChartListRefreshRequest RefreshRequest { get; set; }
    internal bool HasFolderRowsOverride { get; set; }
    internal IEnumerable<LibraryChartRow> FolderRowsOverride { get; set; }
    internal RegularChartListExternalVersions ExternalVersions { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal bool PreserveSummary { get; set; }
    internal MainViewUpdateMode Mode { get; set; }
    internal Stopwatch Stopwatch { get; set; }
    internal PlaylistSourceRetirementRequest DetailSourceRetirement { get; set; }
    internal bool RetireDetailSource { get; set; }
}

internal readonly struct RegularMaterializedChartListApplyResult
{
    internal RegularMaterializedChartListApplyResult(
        MainChartListRowsApplyResult rowsApply,
        RegularChartListBuildResult build,
        MainViewUpdateMode appliedMode)
    {
        WasCommitted = true;
        RowsApply = rowsApply;
        AppliedMode = appliedMode;
        FolderCount = build.Stage.FolderCount;
        KeywordCount = build.Stage.KeywordCount;
        ModeCount = build.Stage.ModeCount;
        ViewCount = build.Sort.RowsView?.Count ?? 0;
        FolderMs = build.FolderMs;
        KeywordMs = build.KeywordMs;
        ModeMs = build.ModeMs;
        SortMs = build.SortMs;
        SortReuse = build.Sort.SortReuse;
        SortProfile = build.Sort.SortProfile;
    }

    internal bool WasCommitted { get; }
    internal MainChartListRowsApplyResult RowsApply { get; }
    internal MainViewUpdateMode AppliedMode { get; }
    internal int FolderCount { get; }
    internal int KeywordCount { get; }
    internal int ModeCount { get; }
    internal int ViewCount { get; }
    internal long FolderMs { get; }
    internal long KeywordMs { get; }
    internal long ModeMs { get; }
    internal long SortMs { get; }
    internal bool SortReuse { get; }
    internal string SortProfile { get; }
}

internal sealed class RegularVirtualNormalLibraryApplyRequest
{
    /// <summary>元の画面受付を利用権発行・採用まで渡します。独立した直接呼出しではnullです。</summary>
    internal RegularChartNavigationRequest Navigation { get; set; }

    internal BMSLibrary Library { get; set; }
    internal bool IncludeBmsonRows { get; set; }
    internal RegularNormalLibraryTreeFilter TreeFilter { get; set; }
    internal string KeywordFilter { get; set; }
    internal ChartModeFilter ModeFilter { get; set; }
    internal string SortColumnName { get; set; }
    internal ListSortDirection SortDirection { get; set; }
    internal RegularChartListExternalVersions ExternalVersions { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal bool PreserveSummary { get; set; }
    internal MainViewUpdateMode Mode { get; set; }
    internal Stopwatch Stopwatch { get; set; }
    internal PlaylistSourceRetirementRequest DetailSourceRetirement { get; set; }
    internal bool RetireDetailSource { get; set; }
    internal string Reason { get; set; }
}

internal sealed class RegularVirtualChartSubsetApplyRequest
{
    /// <summary>元の画面受付を利用権発行・採用まで渡します。独立した直接呼出しではnullです。</summary>
    internal RegularChartNavigationRequest Navigation { get; set; }

    internal BMSLibrary Library { get; set; }
    internal IEnumerable<ChartFile> SourceCharts { get; set; }
    internal IEnumerable<PackageChartEntry> SourceEntries { get; set; }
    internal ChartListSourceProjectionMode SourceProjectionMode { get; set; }
    internal bool ApplyResourceHealthProjection { get; set; }
    internal MainViewUpdateMode TreeMode { get; set; }
    internal string SubsetName { get; set; }
    /// <summary>同じ区分内の重複群・folder・packageを区別する変更不能な元集合キーです。全体集合ではnullです。</summary>
    internal object SelectionContext { get; set; }
    internal string KeywordFilter { get; set; }
    internal ChartModeFilter ModeFilter { get; set; }
    internal string SortColumnName { get; set; }
    internal ListSortDirection SortDirection { get; set; }
    internal RegularChartListExternalVersions ExternalVersions { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal bool PreserveSummary { get; set; }
    internal MainViewUpdateMode Mode { get; set; }
    internal Stopwatch Stopwatch { get; set; }
    internal PlaylistSourceRetirementRequest DetailSourceRetirement { get; set; }
    internal bool RetireDetailSource { get; set; }
}

internal readonly struct RegularVirtualChartSubsetApplyResult
{
    internal RegularVirtualChartSubsetApplyResult(
        RegularChartListTerminalResult terminal,
        ChartListVirtualView rowsView,
        ChartListOrder order,
        VirtualChartSubsetSortCacheKey sortCacheKey,
        int sourceRowCount,
        int keywordCount,
        int modeCount,
        int distinctFolderCount,
        long sourceRowsSignature,
        long sourceRowsMs,
        long keywordMs,
        long modeMs,
        long sortMs,
        bool sortCacheHit,
        long orderCacheLookupMs,
        long orderBuildMs)
    {
        Terminal = terminal;
        RowsView = rowsView;
        Order = order;
        SortCacheKey = sortCacheKey;
        SourceRowCount = sourceRowCount;
        KeywordCount = keywordCount;
        ModeCount = modeCount;
        DistinctFolderCount = distinctFolderCount;
        SourceRowsSignature = sourceRowsSignature;
        SourceRowsMs = sourceRowsMs;
        KeywordMs = keywordMs;
        ModeMs = modeMs;
        SortMs = sortMs;
        SortCacheHit = sortCacheHit;
        OrderCacheLookupMs = orderCacheLookupMs;
        OrderBuildMs = orderBuildMs;
    }

    /// <summary>UIの同じ要求leaseで採用した終端を、構築済みの計測結果へ結び付けます。</summary>
    internal RegularVirtualChartSubsetApplyResult WithTerminal(RegularChartListTerminalResult terminal)
        => new(terminal, RowsView, Order, SortCacheKey, SourceRowCount, KeywordCount, ModeCount,
            DistinctFolderCount, SourceRowsSignature, SourceRowsMs, KeywordMs, ModeMs, SortMs,
            SortCacheHit, OrderCacheLookupMs, OrderBuildMs);

    internal bool WasCommitted => Terminal.WasCommitted;
    internal RegularChartListTerminalResult Terminal { get; }
    internal ChartListVirtualView RowsView { get; }
    internal ChartListOrder Order { get; }
    internal VirtualChartSubsetSortCacheKey SortCacheKey { get; }
    internal int SourceRowCount { get; }
    internal int KeywordCount { get; }
    internal int ModeCount { get; }
    internal int DistinctFolderCount { get; }
    internal long SourceRowsSignature { get; }
    internal long SourceRowsMs { get; }
    internal long KeywordMs { get; }
    internal long ModeMs { get; }
    internal long SortMs { get; }
    internal bool SortCacheHit { get; }
    internal long OrderCacheLookupMs { get; }
    internal long OrderBuildMs { get; }
}

internal readonly struct RegularVirtualNormalLibraryApplyResult
{
    internal RegularVirtualNormalLibraryApplyResult(
        RegularChartListTerminalResult terminal,
        ChartListVirtualView rowsView,
        ChartListOrder order,
        NormalLibrarySortCacheKey sortCacheKey,
        int sourceRowCount,
        int folderCount,
        int keywordCount,
        int modeCount,
        int distinctFolderCount,
        bool sourceRowsCacheHit,
        bool sortCacheHit,
        bool summaryCacheHit,
        long sourceRowsMs,
        long folderMs,
        long keywordMs,
        long modeMs,
        long sortMs,
        long orderCacheLookupMs,
        long orderBuildMs)
    {
        Terminal = terminal;
        RowsView = rowsView;
        Order = order;
        SortCacheKey = sortCacheKey;
        SourceRowCount = sourceRowCount;
        FolderCount = folderCount;
        KeywordCount = keywordCount;
        ModeCount = modeCount;
        DistinctFolderCount = distinctFolderCount;
        SourceRowsCacheHit = sourceRowsCacheHit;
        SortCacheHit = sortCacheHit;
        SummaryCacheHit = summaryCacheHit;
        SourceRowsMs = sourceRowsMs;
        FolderMs = folderMs;
        KeywordMs = keywordMs;
        ModeMs = modeMs;
        SortMs = sortMs;
        OrderCacheLookupMs = orderCacheLookupMs;
        OrderBuildMs = orderBuildMs;
    }

    internal bool WasCommitted => Terminal.WasCommitted;
    internal RegularChartListTerminalResult Terminal { get; }
    internal ChartListVirtualView RowsView { get; }
    internal ChartListOrder Order { get; }
    internal NormalLibrarySortCacheKey SortCacheKey { get; }
    internal int SourceRowCount { get; }
    internal int FolderCount { get; }
    internal int KeywordCount { get; }
    internal int ModeCount { get; }
    internal int DistinctFolderCount { get; }
    internal bool SourceRowsCacheHit { get; }
    internal bool SortCacheHit { get; }
    internal bool SummaryCacheHit { get; }
    internal long SourceRowsMs { get; }
    internal long FolderMs { get; }
    internal long KeywordMs { get; }
    internal long ModeMs { get; }
    internal long SortMs { get; }
    internal long OrderCacheLookupMs { get; }
    internal long OrderBuildMs { get; }

    /// <summary>ワーカーで構築した同じ候補へ、UI採用と必要配送後の終端を結びます。</summary>
    internal RegularVirtualNormalLibraryApplyResult WithTerminal(RegularChartListTerminalResult terminal)
        => new(terminal, RowsView, Order, SortCacheKey, SourceRowCount, FolderCount, KeywordCount, ModeCount,
            DistinctFolderCount, SourceRowsCacheHit, SortCacheHit, SummaryCacheHit, SourceRowsMs, FolderMs,
            KeywordMs, ModeMs, SortMs, OrderCacheLookupMs, OrderBuildMs);

    internal static RegularVirtualNormalLibraryApplyResult NotCommitted() => default;
}

internal readonly struct NormalLibrarySortCacheGenerationSnapshot
{
    internal NormalLibrarySortCacheGenerationSnapshot(long source, long sortKey, long score, long chartInfo, long maintenance, long warning, long installDestination, long referenceTables)
    {
        Source = source;
        SortKey = sortKey;
        Score = score;
        ChartInfo = chartInfo;
        Maintenance = maintenance;
        Warning = warning;
        InstallDestination = installDestination;
        ReferenceTables = referenceTables;
    }
    internal long Source { get; }
    internal long SortKey { get; }
    internal long Score { get; }
    internal long ChartInfo { get; }
    internal long Maintenance { get; }
    internal long Warning { get; }
    internal long InstallDestination { get; }
    internal long ReferenceTables { get; }
    internal NormalLibrarySortCacheKey Create(string columnName, ListSortDirection direction, int rowCount) => new(Source, SortKey, Score, ChartInfo, Maintenance, Warning, InstallDestination, ReferenceTables, columnName, direction, rowCount);
}

internal readonly struct RegularChartListTerminalResult
{
    private RegularChartListTerminalResult(bool wasCommitted, MainChartListRowsApplyResult rowsApply)
    {
        WasCommitted = wasCommitted;
        RowsApply = rowsApply;
    }
    internal bool WasCommitted { get; }
    internal MainChartListRowsApplyResult RowsApply { get; }
    internal static RegularChartListTerminalResult Stale() => new(false, default);
    internal static RegularChartListTerminalResult Committed(MainChartListRowsApplyResult rowsApply) => new(true, rowsApply);
}

internal sealed class RegularChartListTerminalPublishException : Exception
{
    internal RegularChartListTerminalPublishException()
    {
    }

    internal RegularChartListTerminalPublishException(string message)
        : base(message)
    {
    }

    internal RegularChartListTerminalPublishException(Exception innerException)
        : base("Regular chart-list terminal state was committed but publishing notifications failed.", innerException)
    {
    }

    internal RegularChartListTerminalPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    [System.Obsolete(DiagnosticId = "SYSLIB0051")]
    private RegularChartListTerminalPublishException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}

internal static class MainViewBuildRequestSequence
{
    private static long seed;

    internal static long Next() => Interlocked.Increment(ref seed);
}
