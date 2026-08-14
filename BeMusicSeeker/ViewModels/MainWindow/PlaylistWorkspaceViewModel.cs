using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Views.Dialogs;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns playlist detail state and the complete playlist-summary build and presentation workflow.
/// </summary>
public sealed partial class PlaylistWorkspaceViewModel : ViewModel, ISettingsDialogWorkspacePort, ISettingsDialogCustomFolderOutputPort
{
    private readonly Action<Action> dispatchPresentation;

    private Action<Action> queueCatalogNotification;

    private readonly Func<Action, Task> playlistRestoreUiApplyScheduler;

    private readonly Func<bool> playlistRestoreUiThreadCheck;

    private readonly MainChartListViewModel detailMainChartList;

    private readonly Action<string> detailRetentionLog;

    private readonly Action<string> detailViewLog;

    private readonly Action<Exception, string> externalPlaylistImportWarningLog;

    private readonly Action<string> externalPlaylistImportInfoLog;

    private readonly Action<Exception, string> beatorajaTableUrlImportWarningLog;

    private readonly Action<string> beatorajaTableUrlImportInfoLog;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly Func<PlaylistUrlAcquisitionOptionsSnapshot> playlistUrlAcquisitionOptionsProvider;

    private readonly PlaylistUrlAcquisitionWorkflow playlistUrlAcquisitionWorkflow;

    private readonly PlaylistExternalPackageLookupService playlistExternalPackageLookupService;

    private readonly IMainChartColumnSettingsStore playlistSummaryColumnSettingsStore;

    private readonly IUiDialogService playlistWorkspaceDialogService;

    private readonly SemaphoreSlim manualReloadSemaphore = new(1, 1);

    private readonly PlaylistSummaryBmtSortCoordinator playlistSummaryBmtSort;

    private readonly PlaylistCatalogSummaryOwner playlistCatalogSummaryOwner = new();

    internal PlaylistCatalogSummaryOwner CatalogSummaryOwner => playlistCatalogSummaryOwner;

    internal PlaylistReferenceApplyWorkflowOwner PlaylistReferenceApplyWorkflow { get; }

    internal PlaylistRemovalWorkflowOwner PlaylistRemovalWorkflow { get; }

    internal PlaylistTableLevelOverwriteWorkflowOwner PlaylistTableLevelOverwriteWorkflow { get; }

    private IPlaylistDetailDataSource detailDataSource;

    internal PlaylistDetailBuildState DetailBuildState { get; }

    internal PlaylistDetailViewState DetailViewState { get; }

    internal void SetDetailDataSource(IPlaylistDetailDataSource dataSource)
    {
        if (dataSource == null)
        {
            throw new ArgumentNullException(nameof(dataSource));
        }
        CancellationTokenSource activeBuildCancellation;
        TaskCompletionSource<bool> requestCompletion;
        lock (DetailBuildState.SyncRoot)
        {
            if (ReferenceEquals(Volatile.Read(ref detailDataSource), dataSource))
            {
                return;
            }
            DetailBuildState.RequestVersion++;
            DetailBuildState.PendingRequest = null;
            activeBuildCancellation = DetailBuildState.CurrentBuildCancellation;
            DetailBuildState.CurrentBuildRequest = null;
            requestCompletion = DetailBuildState.AdvanceCompletedRequestVersionUnsafe(
                DetailBuildState.RequestVersion);
            lock (DetailViewState.SyncRoot)
            {
                DetailViewState.Source.CurrentIdentity = null;
                DetailViewState.View.CurrentIdentity = null;
            }
            Volatile.Write(ref detailDataSource, dataSource);
        }
        ResetPlaylistLibraryIndexPrewarmForDataSourceChange();
        requestCompletion?.TrySetResult(true);
        try
        {
            activeBuildCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal PlaylistWorkspaceViewModel(
        Action<Action> dispatchPresentation,
        MainChartListViewModel mainChartList,
        PlaylistDetailBuildState buildState,
        PlaylistDetailViewState viewState,
        Action<string> detailViewLog,
        Action<string> detailRetentionLog,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider,
        PlaylistUrlAcquisitionWorkflow playlistUrlAcquisitionWorkflow,
        PlaylistExternalPackageLookupService playlistExternalPackageLookupService,
        Func<PlaylistUrlAcquisitionOptionsSnapshot> playlistUrlAcquisitionOptionsProvider,
        Func<bool> playlistUrlInstallQueueActiveProvider,
        Action<IReadOnlyList<string>> playlistUrlInstallSink,
        Action<Uri> playlistUrlBrowserOpenSink,
        Action<Exception, string> externalPlaylistImportWarningLog,
        Action<string> externalPlaylistImportInfoLog,
        Action<Exception, string> beatorajaTableUrlImportWarningLog,
        Action<string> beatorajaTableUrlImportInfoLog,
        IMainChartColumnSettingsStore playlistSummaryColumnSettingsStore,
        PlaylistSummaryBmtSortCoordinator playlistSummaryBmtSort,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
        Func<BMSPlaylist> playlistStoreProvider,
        PlaylistPropertySaveService propertySaveService,
        Func<BMSLibrary> playlistLibraryProvider,
        Func<LR2Config> lr2ConfigProvider,
        Action<string> summaryBulkWarningLog,
        ObservableCollection<BMSTable> emptyPlaylistTreeSource,
        Func<string, Func<Task>, bool> playlistLibraryIndexPrewarmScheduler,
        Func<bool> playlistReloadCleanupStartupOperableProvider,
        Func<MainViewUpdateMode> playlistReloadCleanupCurrentTreeModeProvider,
        Func<Task> playlistReloadCleanupDispatcherIdleWaiter,
        Func<bool> playlistReloadCleanupShutdownRequestedProvider,
        Action playlistReloadCleanupGarbageCollector,
        Action<string> playlistReloadLog,
        Action<Exception, string> playlistSyncFailureLog,
        Func<string, Func<Task>, bool> playlistExternalSyncScheduler,
        Func<string, Func<Task>, bool> playlistReferenceApplyScheduler,
        Func<Action, Task> playlistRestoreUiApplyScheduler,
        Func<bool> playlistRestoreUiThreadCheck,
        IUiDialogService playlistWorkspaceDialogService = null)
    {
        this.dispatchPresentation = dispatchPresentation ?? throw new ArgumentNullException(nameof(dispatchPresentation));
        detailMainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        DetailBuildState = buildState ?? throw new ArgumentNullException(nameof(buildState));
        DetailViewState = viewState ?? throw new ArgumentNullException(nameof(viewState));
        this.detailViewLog = detailViewLog ?? throw new ArgumentNullException(nameof(detailViewLog));
        this.detailRetentionLog = detailRetentionLog ?? throw new ArgumentNullException(nameof(detailRetentionLog));
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider
            ?? throw new ArgumentNullException(nameof(customFolderOutputSettingsProvider));
        this.playlistUrlAcquisitionWorkflow = playlistUrlAcquisitionWorkflow
            ?? throw new ArgumentNullException(nameof(playlistUrlAcquisitionWorkflow));
        this.playlistExternalPackageLookupService = playlistExternalPackageLookupService
            ?? throw new ArgumentNullException(nameof(playlistExternalPackageLookupService));
        this.playlistUrlAcquisitionOptionsProvider = playlistUrlAcquisitionOptionsProvider
            ?? throw new ArgumentNullException(nameof(playlistUrlAcquisitionOptionsProvider));
        this.playlistUrlInstallQueueActiveProvider = playlistUrlInstallQueueActiveProvider
            ?? throw new ArgumentNullException(nameof(playlistUrlInstallQueueActiveProvider));
        this.playlistUrlInstallSink = playlistUrlInstallSink
            ?? throw new ArgumentNullException(nameof(playlistUrlInstallSink));
        this.playlistUrlBrowserOpenSink = playlistUrlBrowserOpenSink
            ?? throw new ArgumentNullException(nameof(playlistUrlBrowserOpenSink));
        this.externalPlaylistImportWarningLog = externalPlaylistImportWarningLog
            ?? throw new ArgumentNullException(nameof(externalPlaylistImportWarningLog));
        this.externalPlaylistImportInfoLog = externalPlaylistImportInfoLog
            ?? throw new ArgumentNullException(nameof(externalPlaylistImportInfoLog));
        this.beatorajaTableUrlImportWarningLog = beatorajaTableUrlImportWarningLog
            ?? throw new ArgumentNullException(nameof(beatorajaTableUrlImportWarningLog));
        this.beatorajaTableUrlImportInfoLog = beatorajaTableUrlImportInfoLog
            ?? throw new ArgumentNullException(nameof(beatorajaTableUrlImportInfoLog));
        this.playlistSummaryColumnSettingsStore = playlistSummaryColumnSettingsStore
            ?? throw new ArgumentNullException(nameof(playlistSummaryColumnSettingsStore));
        this.playlistSummaryBmtSort = playlistSummaryBmtSort
            ?? throw new ArgumentNullException(nameof(playlistSummaryBmtSort));
        playlistSummaryKeywordSearchHistorySettingsStore = keywordSearchHistorySettingsStore
            ?? throw new ArgumentNullException(nameof(keywordSearchHistorySettingsStore));
        playlistSummaryKeywordSearchHistory.AddRange(
            KeywordSearchHistoryStore.Deserialize(
                keywordSearchHistorySettingsStore.PlaylistSummaryKeywordSearchHistory));
        getPlaylistStore = playlistStoreProvider
            ?? throw new ArgumentNullException(nameof(playlistStoreProvider));
        this.propertySaveService = propertySaveService
            ?? throw new ArgumentNullException(nameof(propertySaveService));
        getPlaylistLibrary = playlistLibraryProvider
            ?? throw new ArgumentNullException(nameof(playlistLibraryProvider));
        getLr2Config = lr2ConfigProvider
            ?? throw new ArgumentNullException(nameof(lr2ConfigProvider));
        this.summaryBulkWarningLog = summaryBulkWarningLog
            ?? throw new ArgumentNullException(nameof(summaryBulkWarningLog));
        emptyPlaylistTreeTables = emptyPlaylistTreeSource
            ?? throw new ArgumentNullException(nameof(emptyPlaylistTreeSource));
        playlistTreeTables = emptyPlaylistTreeTables;
        this.playlistLibraryIndexPrewarmScheduler = playlistLibraryIndexPrewarmScheduler
            ?? throw new ArgumentNullException(nameof(playlistLibraryIndexPrewarmScheduler));
        this.playlistReloadCleanupStartupOperableProvider = playlistReloadCleanupStartupOperableProvider
            ?? throw new ArgumentNullException(nameof(playlistReloadCleanupStartupOperableProvider));
        this.playlistReloadCleanupCurrentTreeModeProvider = playlistReloadCleanupCurrentTreeModeProvider
            ?? throw new ArgumentNullException(nameof(playlistReloadCleanupCurrentTreeModeProvider));
        this.playlistReloadCleanupDispatcherIdleWaiter = playlistReloadCleanupDispatcherIdleWaiter
            ?? throw new ArgumentNullException(nameof(playlistReloadCleanupDispatcherIdleWaiter));
        this.playlistReloadCleanupShutdownRequestedProvider = playlistReloadCleanupShutdownRequestedProvider
            ?? throw new ArgumentNullException(nameof(playlistReloadCleanupShutdownRequestedProvider));
        this.playlistReloadCleanupGarbageCollector = playlistReloadCleanupGarbageCollector
            ?? throw new ArgumentNullException(nameof(playlistReloadCleanupGarbageCollector));
        this.playlistReloadLog = playlistReloadLog
            ?? throw new ArgumentNullException(nameof(playlistReloadLog));
        this.playlistSyncFailureLog = playlistSyncFailureLog
            ?? throw new ArgumentNullException(nameof(playlistSyncFailureLog));
        this.playlistExternalSyncScheduler = playlistExternalSyncScheduler
            ?? throw new ArgumentNullException(nameof(playlistExternalSyncScheduler));
        this.playlistRestoreUiApplyScheduler = playlistRestoreUiApplyScheduler
            ?? throw new ArgumentNullException(nameof(playlistRestoreUiApplyScheduler));
        this.playlistRestoreUiThreadCheck = playlistRestoreUiThreadCheck
            ?? throw new ArgumentNullException(nameof(playlistRestoreUiThreadCheck));
        this.playlistUrlAcquisitionPresentationScheduler = playlistRestoreUiApplyScheduler;
        PlaylistReferenceApplyWorkflow = new PlaylistReferenceApplyWorkflowOwner(
            playlistReferenceApplyScheduler,
            dispatchPresentation,
            playlistReloadCleanupShutdownRequestedProvider,
            playlistReloadLog);
        this.playlistWorkspaceDialogService = playlistWorkspaceDialogService ?? new UiDialogCoordinator();
        PlaylistRemovalWorkflow = new PlaylistRemovalWorkflowOwner(
            this.playlistWorkspaceDialogService,
            getPlaylistStore,
            getPlaylistLibrary,
            getLr2Config,
            customFolderOutputSettingsProvider);
        PlaylistTableLevelOverwriteWorkflow = new PlaylistTableLevelOverwriteWorkflowOwner(
            this.playlistWorkspaceDialogService,
            getPlaylistLibrary);
        PlaylistRemovalWorkflow.ReferenceSortInvalidationRequested +=
            (_, _) => RequestPlaylistReferenceSortInvalidation();
        PlaylistRemovalWorkflow.SummaryRefreshRequested +=
            (_, _) => RequestPlaylistSummaryRefresh("playlist_table_removed", rebuildAsync: false);
        PlaylistRemovalWorkflow.OperationNotificationPresentationRequested +=
            (_, request) => RaiseRequiredEvent(
                PlaylistOperationNotificationPresentationRequested,
                request,
                nameof(PlaylistOperationNotificationPresentationRequested));
        PlaylistRemovalWorkflow.MutationRejected +=
            (_, request) => RaiseRequiredEvent(
                MutationRejected,
                request,
                nameof(MutationRejected));
        PlaylistRemovalWorkflow.FolderRemovalApplied +=
            (_, request) =>
            {
                RemapCurrentPlaylistDetailFolderSelection(
                    request.Table,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [request.FolderName] = string.Empty
                    });
                PublishEntriesChanged(request.Table);
            };
        propertySaveService.ValidationError += ForwardPlaylistPropertyValidationError;
        propertySaveService.ExternalSyncConfirmationRequested += ForwardPlaylistPropertyExternalSyncConfirmationRequested;
        propertySaveService.InvalidOutputDirectoryRequested += ForwardPlaylistPropertyInvalidOutputDirectoryRequested;
        propertySaveService.PlaylistPropertySyncStarted += ForwardPlaylistPropertySyncStarted;
        propertySaveService.PlaylistPropertySyncProgressChanged += ForwardPlaylistSyncProgressChanged;
        propertySaveService.PlaylistPropertySyncFinished += ForwardPlaylistPropertySyncFinished;
        propertySaveService.PlaylistPropertyReferenceTableReplaced += ForwardPlaylistPropertyReferenceTableReplaced;
        propertySaveService.PlaylistPropertyFolderSelectionRemapped += ForwardPlaylistPropertyFolderSelectionRemapped;
        propertySaveService.PlaylistPropertyReferenceSortInvalidationRequested += ForwardPlaylistPropertyReferenceSortInvalidationRequested;
        propertySaveService.PlaylistPropertySyncResultReported += ForwardPlaylistSyncResultReported;
        propertySaveService.PlaylistPropertyExternalSyncFailed += ForwardPlaylistPropertyExternalSyncFailed;
        propertySaveService.PlaylistPropertySummaryDataRefreshRequested += ForwardPlaylistSummaryDataRefreshRequested;
        propertySaveService.PlaylistPropertyEntriesChanged += ForwardPlaylistEntriesChanged;
        propertySaveService.PlaylistOperationNotificationPresentationRequested += ForwardPlaylistOperationNotificationPresentationRequested;
    }

    internal void ConfigureCatalogNotificationQueue(Action<Action> queueAction)
    {
        Volatile.Write(
            ref queueCatalogNotification,
            queueAction ?? throw new ArgumentNullException(nameof(queueAction)));
    }

    bool ISettingsDialogWorkspacePort.HasPlaylistTables
        => getPlaylistStore()?.BMSTables != null;

    long ISettingsDialogWorkspacePort.PlaylistCatalogVersion
        => Interlocked.Read(ref playlistCatalogVersion);

    IReadOnlyList<PlaylistTablePresentationSnapshot> ISettingsDialogWorkspacePort.CapturePlaylistPresentationSnapshots()
    {
        BMSPlaylist playlist = getPlaylistStore();
        if (playlist == null)
        {
            return [];
        }

        playlist.AcquireReaderLockBMSTables();
        try
        {
            return [.. (playlist.BMSTables ?? Enumerable.Empty<BMSTable>())
                .Where(table => table != null)
                .Select(PlaylistTablePresentationSnapshot.From)];
        }
        finally
        {
            playlist.FreeReaderLockBMSTables();
        }
    }

    bool ISettingsDialogWorkspacePort.HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath)
        => HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(beatorajaRootPath);

    void ISettingsDialogWorkspacePort.SchedulePlaylistUrlCompletionRefresh(string reason)
        => getPlaylistStore()?.SchedulePlaylistUrlCompletionRefresh(reason);

    void ISettingsDialogWorkspacePort.QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath)
        => getPlaylistStore()?.BmtOutput.QueueBeatorajaBmtExportAll(reason, cleanupTablePath);

    Task ISettingsDialogWorkspacePort.RunWithPlaylistOperationNotificationsAsync(
        Func<Task> operation,
        string operationName)
        => RunWithPlaylistOperationNotificationsAsync(operation, operationName);

    event EventHandler<PlaylistCatalogChangedEventArgs> ISettingsDialogWorkspacePort.PlaylistCatalogChanged
    {
        add => PlaylistCatalogChanged += value;
        remove => PlaylistCatalogChanged -= value;
    }

    CustomFolderOutputSettingsSnapshot ISettingsDialogCustomFolderOutputPort.CustomFolderOutputSettings
        => customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");

    void ISettingsDialogCustomFolderOutputPort.ChangeCustomFolderBaseDirectoryWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        string additionalOutputBaseDirsBefore,
        string additionalOutputBaseDirsAfter,
        CustomFolderOutputSettingsSnapshot settings)
        => getPlaylistStore()?.ChangeCustomFolderBaseDirectoryWithSettings(
            outputDirBaseBefore,
            outputDirBaseAfter,
            additionalOutputBaseDirsBefore,
            additionalOutputBaseDirsAfter,
            settings);

    void ISettingsDialogCustomFolderOutputPort.ChangeCustomFolderBaseDirectoryRootWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        CustomFolderOutputSettingsSnapshot settings)
        => getPlaylistStore()?.ChangeCustomFolderBaseDirectoryRootWithSettings(
            outputDirBaseBefore,
            outputDirBaseAfter,
            settings);

    bool ISettingsDialogCustomFolderOutputPort.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
        string previousRootOutputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings)
        => getPlaylistStore()?.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
            previousRootOutputBaseDirectory,
            configOverride: getLr2Config(),
            settings: settings) == true;

    int ISettingsDialogCustomFolderOutputPort.ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
        string previousAdditionalOutputBaseDirectories,
        IReadOnlyDictionary<string, string> pendingRenames,
        CustomFolderOutputSettingsSnapshot settings)
        => getPlaylistStore()?.ApplyCustomFolderAdditionalOutputBaseRegistrationChangesWithSettings(
            previousAdditionalOutputBaseDirectories,
            pendingRenames,
            settings) ?? 0;

    internal bool RepairRootCustomFolderOutputSearchRootsAfterStartup(
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        if (!settings.OperationModeLR2DB)
        {
            return false;
        }

        BMSPlaylist playlist = getPlaylistStore();
        LR2Config config = getLr2Config();
        if (playlist?.BMSTables == null || config == null)
        {
            return false;
        }

        return playlist.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
            settings.LR2CustomFolderOutputBaseDirRootType,
            configOverride: config,
            settings: settings);
    }

    internal event Action<PlaylistSummarySelectionRestoreRequest> PlaylistSummarySelectionRestoreRequested;

    internal event EventHandler<PlaylistPresentationRefreshRequestedEventArgs> PlaylistPresentationRefreshRequested;

    internal async Task ResetPlaylistSummaryColumnsToDefaultAsync()
    {
        UiDialogResult result = await playlistWorkspaceDialogService.ConfirmAsync(
                new UiConfirmationRequest(
                    BeMusicSeeker.Properties.Resources.Msg_init_column_settings,
                    BeMusicSeeker.Properties.Resources.Confirm))
            .ConfigureAwait(true);
        if (result == null)
        {
            throw new InvalidOperationException(
                "Playlist summary column reset confirmation returned no dialog result.");
        }
        if (!result.IsAccepted)
        {
            if (result.Status is UiDialogStatus.Rejected
                or UiDialogStatus.CancelledByUser
                or UiDialogStatus.ClosedByUser)
            {
                return;
            }

            throw result.Exception
                ?? new InvalidOperationException(
                    "Playlist summary column reset confirmation could not be displayed ("
                    + result.Status
                    + ").");
        }

        IMainChartColumnSettingsStore store = playlistSummaryColumnSettingsStore
            ?? throw new InvalidOperationException("Playlist summary column settings store is not configured.");
        PlaylistSummaryColumnSettings settings = store.ResetPlaylistSummary();
        PlaylistColumnPresentationCommit commit = CommitColumnPresentationWithoutNotification(
            ColumnSettingsVisibilityForPlaylist,
            settings);
        PublishColumnPresentation(commit);
    }

    internal Task ApplyCurrentVisibleBmtOrderAsync(IEnumerable<PlaylistSummaryRow> visibleRows)
    {
        List<PlaylistSummaryRow> visibleRowsSnapshot = [.. (visibleRows ?? [])];
        return Task.Run(() => ApplyCurrentVisibleBmtOrderCore(visibleRowsSnapshot));
    }

    private void ApplyCurrentVisibleBmtOrderCore(IReadOnlyList<PlaylistSummaryRow> visibleRows)
    {
        if (GetSummaryBmtSort().ApplyCurrentVisibleOrder(visibleRows))
        {
            RequestPlaylistSummaryBmtSortRefresh("playlist_summary_apply_current_order_to_bmt_sort");
        }
    }

    internal Task MoveSummaryRowsToBmtTopAsync(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<PlaylistSummaryRow> rowsSnapshot = [.. (rows ?? [])];
        return Task.Run(() => MoveSummaryRowsToBmtTopCore(rowsSnapshot));
    }

    private void MoveSummaryRowsToBmtTopCore(IReadOnlyList<PlaylistSummaryRow> rows)
    {
        if (GetSummaryBmtSort().MoveRowsToTop(rows))
        {
            RequestPlaylistSummaryBmtSortRefresh("playlist_summary_move_to_bmt_sort_top");
        }
    }

    internal Task MoveSummaryRowsToBmtBottomAsync(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<PlaylistSummaryRow> rowsSnapshot = [.. (rows ?? [])];
        return Task.Run(() => MoveSummaryRowsToBmtBottomCore(rowsSnapshot));
    }

    private void MoveSummaryRowsToBmtBottomCore(IReadOnlyList<PlaylistSummaryRow> rows)
    {
        if (GetSummaryBmtSort().MoveRowsToBottom(rows))
        {
            RequestPlaylistSummaryBmtSortRefresh("playlist_summary_move_to_bmt_sort_bottom");
        }
    }

    internal Task DropSummaryRowsInBmtOrderAsync(
        IEnumerable<PlaylistSummaryRow> visibleRows,
        IEnumerable<PlaylistSummaryRow> draggedRows,
        int visibleInsertIndex,
        int? currentPlaylistId = null)
    {
        List<PlaylistSummaryRow> visibleRowsSnapshot = [.. (visibleRows ?? [])];
        List<PlaylistSummaryRow> draggedRowsSnapshot = [.. (draggedRows ?? [])];
        return Task.Run(() =>
        {
            DropSummaryRowsInBmtOrderCore(
                visibleRowsSnapshot,
                draggedRowsSnapshot,
                visibleInsertIndex,
                currentPlaylistId);
        });
    }

    private long DropSummaryRowsInBmtOrderCore(
        IReadOnlyList<PlaylistSummaryRow> visibleRows,
        IReadOnlyList<PlaylistSummaryRow> draggedRowsSnapshot,
        int visibleInsertIndex,
        int? currentPlaylistId)
    {
        long selectionRestoreOperationId = 0L;
        PlaylistSummarySelectionRestoreRequest selectionRestoreRequest = null;
        long dataRebuildGeneration = 0L;
        try
        {
            bool changed = GetSummaryBmtSort().DropRows(
                visibleRows,
                draggedRowsSnapshot,
                visibleInsertIndex,
                appliedDraggedTables =>
                {
                    var activeDraggedTableSet = new HashSet<BMSTable>(appliedDraggedTables);
                    List<PlaylistSummaryRow> appliedDraggedRows = [.. draggedRowsSnapshot.Where(
                        row => row?.TableRef != null && activeDraggedTableSet.Contains(row.TableRef))];
                    int? appliedCurrentPlaylistId = currentPlaylistId.HasValue
                        && appliedDraggedRows.Any(row => row.PlaylistId == currentPlaylistId)
                        ? currentPlaylistId
                        : null;
                    selectionRestoreOperationId = QueuePlaylistSummarySelectionRestore(
                        appliedDraggedRows,
                        appliedCurrentPlaylistId,
                        out selectionRestoreRequest);
                    dataRebuildGeneration = RequestPlaylistSummaryBmtSortRefresh(
                        "playlist_summary_bmt_sort_drag_drop",
                        selectionRestoreOperationId > 0L
                            ? generation => SetPlaylistSummarySelectionRestoreMinimumGenerationUnsafe(
                                selectionRestoreOperationId,
                                selectionRestoreRequest,
                                generation)
                            : null);
                    if (selectionRestoreOperationId > 0L && dataRebuildGeneration <= 0L)
                    {
                        ClearPlaylistSummarySelectionRestore(selectionRestoreOperationId);
                    }
                });
            if (!changed)
            {
                return 0L;
            }
            return dataRebuildGeneration;
        }
        catch
        {
            ClearPlaylistSummarySelectionRestore(selectionRestoreOperationId);
            throw;
        }
    }

    private long QueuePlaylistSummarySelectionRestore(
        IEnumerable<PlaylistSummaryRow> draggedRows,
        int? currentPlaylistId,
        out PlaylistSummarySelectionRestoreRequest selectionRestoreRequest)
    {
        selectionRestoreRequest = null;
        var playlistIds = new HashSet<int>();
        foreach (PlaylistSummaryRow row in draggedRows ?? [])
        {
            if (row?.PlaylistId is int playlistId)
            {
                playlistIds.Add(playlistId);
            }
        }
        if (playlistIds.Count == 0)
        {
            return 0L;
        }
        int? selectedCurrentPlaylistId = currentPlaylistId.HasValue && playlistIds.Contains(currentPlaylistId.Value)
            ? currentPlaylistId
            : playlistIds.FirstOrDefault();
        selectionRestoreRequest = new PlaylistSummarySelectionRestoreRequest(playlistIds, selectedCurrentPlaylistId);
        lock (playlistSummaryTransitionLock)
        {
            long operationId = ++playlistSummarySelectionRestoreOperationId;
            if (operationId <= 0L)
            {
                operationId = playlistSummarySelectionRestoreOperationId = 1L;
            }
            pendingPlaylistSummarySelectionPlaylistIds = playlistIds;
            pendingPlaylistSummaryCurrentPlaylistId = selectedCurrentPlaylistId;
            pendingPlaylistSummarySelectionMinDataGeneration = long.MaxValue;
            pendingPlaylistSummarySelectionOperationId = operationId;
            return operationId;
        }
    }

    private void SetPlaylistSummarySelectionRestoreMinimumGenerationUnsafe(
        long operationId,
        PlaylistSummarySelectionRestoreRequest selectionRestoreRequest,
        long dataRebuildGeneration)
    {
        // The data generation, rather than drop completion order, identifies the view that can consume this intent.
        // A later callback may therefore replace an earlier pending intent even when its operation started first.
        if (selectionRestoreRequest == null
            || dataRebuildGeneration <= 0L
            || lastPlaylistSummaryAppliedDataGeneration > dataRebuildGeneration
            || (pendingPlaylistSummarySelectionPlaylistIds != null
                && pendingPlaylistSummarySelectionMinDataGeneration > 0L
                && pendingPlaylistSummarySelectionMinDataGeneration != long.MaxValue
                && dataRebuildGeneration < pendingPlaylistSummarySelectionMinDataGeneration))
        {
            return;
        }
        pendingPlaylistSummarySelectionPlaylistIds = new HashSet<int>(selectionRestoreRequest.PlaylistIds);
        pendingPlaylistSummaryCurrentPlaylistId = selectionRestoreRequest.CurrentPlaylistId;
        pendingPlaylistSummarySelectionMinDataGeneration = dataRebuildGeneration;
        pendingPlaylistSummarySelectionOperationId = operationId;
    }

    private bool TryTakePlaylistSummarySelectionRestore(out PlaylistSummarySelectionRestoreRequest request)
    {
        lock (playlistSummaryTransitionLock)
        {
            if (playlistSummaryDataBuildStopped
                || pendingPlaylistSummarySelectionPlaylistIds == null
                || pendingPlaylistSummarySelectionPlaylistIds.Count == 0
                || pendingPlaylistSummarySelectionMinDataGeneration <= 0L
                || pendingPlaylistSummarySelectionMinDataGeneration == long.MaxValue
                || lastPlaylistSummaryAppliedDataGeneration < pendingPlaylistSummarySelectionMinDataGeneration)
            {
                request = null;
                return false;
            }
            if (lastPlaylistSummaryAppliedDataGeneration > pendingPlaylistSummarySelectionMinDataGeneration)
            {
                ClearPlaylistSummarySelectionRestoreUnsafe();
                request = null;
                return false;
            }
            request = new PlaylistSummarySelectionRestoreRequest(
                pendingPlaylistSummarySelectionPlaylistIds,
                pendingPlaylistSummaryCurrentPlaylistId);
            ClearPlaylistSummarySelectionRestoreUnsafe();
            return true;
        }
    }

    private void ClearPlaylistSummarySelectionRestore(long operationId)
    {
        if (operationId <= 0L)
        {
            return;
        }
        lock (playlistSummaryTransitionLock)
        {
            if (pendingPlaylistSummarySelectionOperationId == operationId)
            {
                ClearPlaylistSummarySelectionRestoreUnsafe();
            }
        }
    }

    private void ClearPlaylistSummarySelectionRestoreUnsafe()
    {
        pendingPlaylistSummarySelectionPlaylistIds = null;
        pendingPlaylistSummaryCurrentPlaylistId = null;
        pendingPlaylistSummarySelectionMinDataGeneration = 0L;
        pendingPlaylistSummarySelectionOperationId = 0L;
    }

    internal void ApplyImportedTablesToBmtFront(IReadOnlyList<BMSTable> importedTables)
    {
        if (GetSummaryBmtSort().ApplyImportedTablesToFront(importedTables))
        {
            RequestPlaylistSummaryBmtSortRefresh("beatoraja_table_url_import");
        }
    }

    private PlaylistSummaryBmtSortCoordinator GetSummaryBmtSort()
    {
        return playlistSummaryBmtSort;
    }

    private long RequestPlaylistSummaryBmtSortRefresh(
        string reason,
        Action<long> beforeRefreshRequested = null)
    {
        return RequestPlaylistSummaryDataRefresh(
            reason,
            rebuildAsync: false,
            beforeRefreshRequested);
    }

    private ChartListSortParameters playlistSummarySortParameters;

    private PlaylistSummaryColumnSettings playlistSummaryColumnsSettings;

    private Visibility columnSettingsVisibilityForPlaylist = Visibility.Collapsed;

    private long columnPresentationGeneration;

    private readonly PlaylistSummaryVersionedCollection playlistSummaryView = [];

    private string playlistSummaryText = string.Empty;

    private PlaylistSummaryPresentationIdentity appliedPlaylistSummaryIdentity;

    private bool isPlaylistSummaryMode;

    private bool isPlaylistSummaryModeRequested;

    private PlaylistSourceRetirementRequest pendingPlaylistSummaryDetailSourceRetirement;

    private bool isPlaylistDetailViewActive;

    private string gridHeaderText = string.Empty;

    private string playlistSummaryKeywordFilter = string.Empty;

    private string playlistSummaryKeywordSearchWarningText = string.Empty;

    private bool isPlaylistSummaryKeywordSearchHelpOpen;

    private readonly ObservableCollection<KeywordSearchSuggestionItem> playlistSummaryKeywordSearchSuggestions = [];

    private bool isPlaylistSummaryKeywordSearchSuggestionPopupOpen;

    private string playlistSummaryKeywordSearchSuggestionHeaderText = string.Empty;

    private PlaylistOwnedFilter playlistSummaryOwnedFilter = PlaylistOwnedFilter.All;

    private long lastPlaylistSummaryBuildCompletedTimestamp;

    private long lastPlaylistSummaryBuildElapsedMs;

    private readonly object playlistSummaryTransitionLock = new();

    private long playlistSummaryPresentationGeneration;

    private PerformanceInteraction appliedPlaylistSummaryPerformanceInteraction;

    private long playlistSummaryDataRebuildGeneration;

    private long lastPlaylistSummaryAppliedDataGeneration;

    private long playlistSummarySelectionRestoreOperationId;

    private HashSet<int> pendingPlaylistSummarySelectionPlaylistIds;

    private int? pendingPlaylistSummaryCurrentPlaylistId;

    private long pendingPlaylistSummarySelectionMinDataGeneration;

    private long pendingPlaylistSummarySelectionOperationId;

    private long playlistSummaryRowsCacheGeneration;

    private List<PlaylistSummaryRow> playlistSummaryRowsCache = [];

    private bool playlistSummaryRowsCacheValid;

    private long playlistSummaryRowsCacheDataRebuildGeneration;

    private CancellationTokenSource playlistSummaryDataBuildCancellation;

    private int playlistSummaryDataBuildActiveCount;

    private TaskCompletionSource<bool> playlistSummaryDataBuildIdleCompletion =
        CreateCompletedLifecycleCompletion();

    private bool playlistSummaryDataBuildStopped;

    private bool deferredPlaylistSummaryDataRefreshRequested;

    private bool deferredPlaylistSummaryDataRebuildAsync = true;

    private bool deferredPlaylistSummaryPresentationRefreshRequested;

    /// <summary>
    /// Gets or sets the current playlist summary sort parameters.
    /// </summary>
    public ChartListSortParameters PlaylistSummarySortParameters
    {
        get => playlistSummarySortParameters;
        internal set
        {
            if (value == null
                || playlistSummarySortParameters == null
                || playlistSummarySortParameters.ColumnsName != value.ColumnsName
                || playlistSummarySortParameters.Direction != value.Direction)
            {
                playlistSummarySortParameters = value;
                RaisePropertyChanged(nameof(PlaylistSummarySortParameters));
            }
        }
    }

    /// <summary>
    /// Accepts a summary-table sort interaction and updates the child-owned presentation state.
    /// </summary>
    /// <param name="columnName">The requested summary-row sort member.</param>
    /// <param name="direction">The requested direction.</param>
    internal void RequestPlaylistSummarySort(string columnName, ListSortDirection direction)
    {
        string normalizedColumnName = string.IsNullOrWhiteSpace(columnName)
            ? nameof(PlaylistSummaryRow.Name)
            : columnName;
        if (playlistSummarySortParameters != null
            && playlistSummarySortParameters.ColumnsName == normalizedColumnName
            && playlistSummarySortParameters.Direction == direction)
        {
            return;
        }

        PlaylistSummarySortParameters = new ChartListSortParameters
        {
            ColumnsName = normalizedColumnName,
            Direction = direction
        };
        RequestPlaylistSummaryPresentationRefresh();
    }

    /// <summary>
    /// Gets or sets the playlist detail/summary column menu visibility state.
    /// </summary>
    public Visibility ColumnSettingsVisibilityForPlaylist
    {
        get => columnSettingsVisibilityForPlaylist;
        internal set
        {
            if (columnSettingsVisibilityForPlaylist != value)
            {
                columnSettingsVisibilityForPlaylist = value;
                RaisePropertyChanged(nameof(ColumnSettingsVisibilityForPlaylist));
            }
        }
    }

    internal PlaylistColumnPresentationCommit CommitColumnPresentationWithoutNotification(
        Visibility visibility,
        PlaylistSummaryColumnSettings summaryColumnsSettings)
    {
        bool visibilityChanged = columnSettingsVisibilityForPlaylist != visibility;
        bool summaryColumnsChanged = !ReferenceEquals(playlistSummaryColumnsSettings, summaryColumnsSettings);
        columnSettingsVisibilityForPlaylist = visibility;
        playlistSummaryColumnsSettings = summaryColumnsSettings;
        long generation = visibilityChanged || summaryColumnsChanged
            ? Interlocked.Increment(ref columnPresentationGeneration)
            : Interlocked.Read(ref columnPresentationGeneration);
        return new PlaylistColumnPresentationCommit(
            visibilityChanged,
            summaryColumnsChanged,
            generation);
    }

    /// <summary>
    /// Commits the playlist-owned state that accompanies a main chart-table terminal apply.
    /// The state is intentionally committed without notifications; the table transaction
    /// publishes all related notifications after row ownership has transferred.
    /// <param name="selection">The column and summary presentation selected for the table.</param>
    /// <param name="playlistDetailActive">Whether the playlist detail binding remains active.</param>
    /// </summary>
    internal PlaylistMainTablePresentationCommit CommitMainTablePresentationWithoutNotification(
        MainChartListColumnSelection selection,
        bool playlistDetailActive,
        bool playlistSummaryActive)
    {
        PlaylistColumnPresentationCommit columnPresentation =
            CommitColumnPresentationWithoutNotification(
                selection.PlaylistColumnSettingsVisibility,
                selection.PlaylistSummaryColumnsSettings);
        PlaylistPresentationModeCommit modePresentation =
            CommitPresentationModeWithoutNotification(
                playlistSummaryActive,
                playlistDetailActive);
        return new PlaylistMainTablePresentationCommit(
            columnPresentation,
            modePresentation);
    }

    internal void PublishColumnPresentation(PlaylistColumnPresentationCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        var publishExceptions = new List<Exception>();
        if (commit.VisibilityChanged && IsCurrentColumnPresentationCommit(commit))
        {
            TryPublish(
                () => RaisePropertyChanged(nameof(ColumnSettingsVisibilityForPlaylist)),
                publishExceptions);
        }
        if (commit.SummaryColumnsChanged && IsCurrentColumnPresentationCommit(commit))
        {
            TryPublish(
                () => RaisePropertyChanged(nameof(PlaylistSummaryColumnsSettings)),
                publishExceptions);
        }
        if (publishExceptions.Count > 0)
        {
            throw new AggregateException(publishExceptions);
        }
    }

    private bool IsCurrentColumnPresentationCommit(PlaylistColumnPresentationCommit commit)
    {
        return commit.Generation == Interlocked.Read(ref columnPresentationGeneration);
    }

    internal bool CommitPlaylistDetailActivationWithoutNotification(bool playlistDetailActive)
    {
        bool detailActiveChanged = isPlaylistDetailViewActive != playlistDetailActive;
        isPlaylistDetailViewActive = playlistDetailActive;
        return detailActiveChanged;
    }

    internal void PublishPlaylistDetailActivation(bool changed)
    {
        if (changed)
        {
            RaisePropertyChanged(nameof(IsPlaylistDetailViewActive));
        }
    }

    /// <summary>
    /// Publishes the playlist-owned notifications for a completed main chart-table terminal apply.
    /// Each notification group is attempted independently so one subscriber cannot suppress the other.
    /// </summary>
    internal void PublishMainTablePresentation(PlaylistMainTablePresentationCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }

        List<Exception> publishExceptions = [];
        try
        {
            PublishColumnPresentation(commit.ColumnPresentation);
        }
        catch (Exception ex)
        {
            publishExceptions.Add(ex);
        }
        try
        {
            PublishPresentationMode(commit.ModePresentation);
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

    /// <summary>
    /// Gets or sets the rows currently displayed by the playlist summary table.
    /// </summary>
    public ObservableCollection<PlaylistSummaryRow> PlaylistSummaryView
    {
        get => playlistSummaryView;
    }

    public string PlaylistSummaryText
    {
        get => playlistSummaryText;
        internal set
        {
            string next = value ?? string.Empty;
            bool changed;
            lock (playlistSummaryTransitionLock)
            {
                changed = playlistSummaryText != next;
                playlistSummaryText = next;
            }
            if (changed)
            {
                RaisePropertyChanged(nameof(PlaylistSummaryText));
            }
        }
    }

    /// <summary>
    /// Gets whether the playlist summary table is active.
    /// </summary>
    public bool IsPlaylistSummaryMode
    {
        get => isPlaylistSummaryMode;
        internal set
        {
            RequestPlaylistSummaryMode(value);
            PublishPresentationMode(
                CommitPresentationModeWithoutNotification(
                    summaryActive: value,
                    detailActive: value ? false : isPlaylistDetailViewActive));
        }
    }

    internal bool IsPlaylistSummaryModeRequested
    {
        get
        {
            lock (playlistSummaryTransitionLock)
            {
                return isPlaylistSummaryModeRequested;
            }
        }
    }

    internal bool RequestPlaylistSummaryMode(bool enabled)
    {
        CancellationTokenSource cancellation = null;
        bool visibleModeWillChange;
        lock (playlistSummaryTransitionLock)
        {
            visibleModeWillChange = isPlaylistSummaryMode != enabled;
            if (isPlaylistSummaryModeRequested == enabled)
            {
                return visibleModeWillChange;
            }
            isPlaylistSummaryModeRequested = enabled;
            if (!enabled)
            {
                cancellation = playlistSummaryDataBuildCancellation;
                playlistSummaryDataBuildCancellation = null;
                playlistSummaryDataRebuildGeneration++;
                playlistSummaryPresentationGeneration++;
                deferredPlaylistSummaryDataRefreshRequested = false;
                deferredPlaylistSummaryDataRebuildAsync = true;
                deferredPlaylistSummaryPresentationRefreshRequested = false;
                ClearPlaylistSummarySelectionRestoreUnsafe();
            }
        }
        CancelDataBuild(cancellation);
        return visibleModeWillChange;
    }

    /// <summary>
    /// Changes the playlist summary presentation mode and applies its associated header state.
    /// </summary>
    /// <returns><see langword="true"/> when the mode itself changed.</returns>
    internal bool SetPlaylistSummaryMode(bool enabled)
    {
        bool changed = RequestPlaylistSummaryMode(enabled);
        PublishPresentationMode(
            CommitPresentationModeWithoutNotification(
                summaryActive: enabled,
                detailActive: enabled ? false : isPlaylistDetailViewActive));
        return changed;
    }

    private PlaylistPresentationModeCommit CommitPresentationModeWithoutNotification(
        bool summaryActive,
        bool detailActive)
    {
        lock (playlistSummaryTransitionLock)
        {
            bool summaryChanged = isPlaylistSummaryMode != summaryActive;
            bool detailChanged = isPlaylistDetailViewActive != detailActive;
            string nextHeaderText = summaryActive
                ? BeMusicSeeker.Properties.Resources.Playlist_summary_header
                : string.Empty;
            string nextSummaryText = summaryActive
                ? playlistSummaryText
                : string.Empty;
            bool headerChanged = gridHeaderText != nextHeaderText;
            bool summaryTextChanged = playlistSummaryText != nextSummaryText;
            isPlaylistSummaryMode = summaryActive;
            isPlaylistDetailViewActive = detailActive;
            gridHeaderText = nextHeaderText;
            playlistSummaryText = nextSummaryText;
            return new PlaylistPresentationModeCommit(
                summaryChanged,
                detailChanged,
                headerChanged,
                summaryTextChanged);
        }
    }

    private void PublishPresentationMode(PlaylistPresentationModeCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        var publishExceptions = new List<Exception>();
        if (commit.SummaryChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(IsPlaylistSummaryMode)), publishExceptions);
        }
        if (commit.DetailChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(IsPlaylistDetailViewActive)), publishExceptions);
        }
        if (commit.HeaderChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(GridHeaderText)), publishExceptions);
        }
        if (commit.SummaryTextChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryText)), publishExceptions);
        }
        if (publishExceptions.Count > 0)
        {
            throw new AggregateException(publishExceptions);
        }
    }

    /// <summary>
    /// Gets whether the shared main table is showing playlist detail rows.
    /// </summary>
    public bool IsPlaylistDetailViewActive
    {
        get => isPlaylistDetailViewActive;
        internal set
        {
            if (isPlaylistDetailViewActive != value)
            {
                isPlaylistDetailViewActive = value;
                RaisePropertyChanged(nameof(IsPlaylistDetailViewActive));
            }
        }
    }

    /// <summary>
    /// Gets or sets the playlist summary header text displayed above the table.
    /// </summary>
    public string GridHeaderText
    {
        get => gridHeaderText;
        internal set
        {
            string next = value ?? string.Empty;
            if (gridHeaderText != next)
            {
                gridHeaderText = next;
                RaisePropertyChanged(nameof(GridHeaderText));
            }
        }
    }

    /// <summary>
    /// Gets the playlist summary column settings.
    /// </summary>
    public PlaylistSummaryColumnSettings PlaylistSummaryColumnsSettings
    {
        get => playlistSummaryColumnsSettings;
    }

    /// <summary>
    /// Gets whether playlist summary rows are sorted by ascending BMT sort.
    /// </summary>
    public bool IsPlaylistSummarySortedByBmtSortAscending => PlaylistSummaryBmtSortOrderPlanner.IsSortedByBmtSortAscending(PlaylistSummarySortParameters);

    /// <summary>
    /// Gets or sets the playlist summary keyword filter text.
    /// </summary>
    public string PlaylistSummaryKeywordFilter
    {
        get => playlistSummaryKeywordFilter;
        set
        {
            string next = value ?? string.Empty;
            if (playlistSummaryKeywordFilter != next)
            {
                playlistSummaryKeywordFilter = next;
                RaisePropertyChanged(nameof(PlaylistSummaryKeywordFilter));
                UpdatePlaylistSummaryKeywordSearchPresentation();
                RequestVisiblePlaylistSummaryPresentationRefresh();
            }
        }
    }

    /// <summary>
    /// Gets playlist summary keyword warning text.
    /// </summary>
    public string PlaylistSummaryKeywordSearchWarningText => playlistSummaryKeywordSearchWarningText;

    /// <summary>
    /// Gets whether playlist summary keyword warning text is visible.
    /// </summary>
    public bool HasPlaylistSummaryKeywordSearchWarning => !string.IsNullOrWhiteSpace(playlistSummaryKeywordSearchWarningText);

    /// <summary>
    /// Gets or sets whether playlist summary keyword help is open.
    /// </summary>
    public bool IsPlaylistSummaryKeywordSearchHelpOpen
    {
        get => isPlaylistSummaryKeywordSearchHelpOpen;
        set
        {
            if (isPlaylistSummaryKeywordSearchHelpOpen != value)
            {
                isPlaylistSummaryKeywordSearchHelpOpen = value;
                RaisePropertyChanged(nameof(IsPlaylistSummaryKeywordSearchHelpOpen));
            }
        }
    }

    /// <summary>
    /// Gets playlist summary keyword help text.
    /// </summary>
    public string PlaylistSummaryKeywordSearchHelpText => KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.PlaylistSummary);

    /// <summary>
    /// Gets playlist summary keyword suggestions.
    /// </summary>
    public ObservableCollection<KeywordSearchSuggestionItem> PlaylistSummaryKeywordSearchSuggestions => playlistSummaryKeywordSearchSuggestions;

    /// <summary>
    /// Gets or sets whether playlist summary keyword suggestion popup is open.
    /// </summary>
    public bool IsPlaylistSummaryKeywordSearchSuggestionPopupOpen
    {
        get => isPlaylistSummaryKeywordSearchSuggestionPopupOpen;
        set
        {
            if (isPlaylistSummaryKeywordSearchSuggestionPopupOpen != value)
            {
                isPlaylistSummaryKeywordSearchSuggestionPopupOpen = value;
                RaisePropertyChanged(nameof(IsPlaylistSummaryKeywordSearchSuggestionPopupOpen));
            }
        }
    }

    /// <summary>
    /// Gets the playlist summary keyword suggestion popup header text.
    /// </summary>
    public string PlaylistSummaryKeywordSearchSuggestionHeaderText => playlistSummaryKeywordSearchSuggestionHeaderText;

    /// <summary>
    /// Gets or sets the playlist summary owned filter.
    /// </summary>
    public PlaylistOwnedFilter PlaylistSummaryOwnedFilter
    {
        get => playlistSummaryOwnedFilter;
        set
        {
            if (playlistSummaryOwnedFilter != value)
            {
                playlistSummaryOwnedFilter = value;
                RaisePropertyChanged(nameof(PlaylistSummaryOwnedFilter));
                RequestVisiblePlaylistSummaryPresentationRefresh();
            }
        }
    }

    internal long LastPlaylistSummaryBuildCompletedTimestamp => Interlocked.Read(ref lastPlaylistSummaryBuildCompletedTimestamp);

    /// <summary>
    /// Gets the last completed summary presentation duration for reload diagnostics.
    /// </summary>
    internal long LastPlaylistSummaryBuildElapsedMs => Interlocked.Read(ref lastPlaylistSummaryBuildElapsedMs);

    internal long BeginPlaylistSummaryPresentationGeneration()
    {
        lock (playlistSummaryTransitionLock)
        {
            return ++playlistSummaryPresentationGeneration;
        }
    }

    internal long BeginPlaylistSummaryDataRebuildGeneration()
    {
        CancellationTokenSource cancellation;
        long generation;
        lock (playlistSummaryTransitionLock)
        {
            cancellation = playlistSummaryDataBuildCancellation;
            playlistSummaryDataBuildCancellation = null;
            generation = ++playlistSummaryDataRebuildGeneration;
            ClearPlaylistSummarySelectionRestoreIfSupersededUnsafe(generation);
        }
        CancelDataBuild(cancellation);
        return generation;
    }

    internal PlaylistSummaryDataRefreshRequestResult RequestPlaylistSummaryDataRefresh(
        Action<long> beforeRefreshRequested = null,
        bool rebuildAsync = true)
    {
        CancellationTokenSource cancellation;
        PlaylistSummaryDataRefreshRequestResult result;
        lock (playlistSummaryTransitionLock)
        {
            if (playlistSummaryDataBuildStopped)
            {
                return default;
            }
            cancellation = InvalidatePlaylistSummaryDataUnsafe();
            bool queued = isPlaylistSummaryModeRequested;
            if (queued)
            {
                deferredPlaylistSummaryDataRefreshRequested = true;
                deferredPlaylistSummaryDataRebuildAsync &= rebuildAsync;
                deferredPlaylistSummaryPresentationRefreshRequested = false;
            }
            result = new PlaylistSummaryDataRefreshRequestResult
            {
                NextBuildGeneration = isPlaylistSummaryModeRequested
                    ? playlistSummaryDataRebuildGeneration + 1L
                    : 0L,
                Queued = queued
            };
            ClearPlaylistSummarySelectionRestoreIfSupersededUnsafe(result.NextBuildGeneration);
            if (queued)
            {
                beforeRefreshRequested?.Invoke(result.NextBuildGeneration);
            }
        }
        CancelDataBuild(cancellation);
        return result;
    }

    /// <summary>
    /// Requests a summary data refresh and drains it when the shell permits presentation work.
    /// </summary>
    /// <param name="reason">The boundary that invalidated the summary data.</param>
    /// <param name="rebuildAsync">Whether the data build should run asynchronously.</param>
    /// <param name="beforeRefreshRequested">Optional state publication performed before the request is drained.</param>
    /// <returns>The next data-build generation, or the accepted drained generation.</returns>
    internal long RequestPlaylistSummaryDataRefresh(
        string reason,
        bool rebuildAsync = true,
        Action<long> beforeRefreshRequested = null)
    {
        if (reason == null)
        {
            throw new ArgumentNullException(nameof(reason));
        }
        PlaylistSummaryDataRefreshRequestResult request = RequestPlaylistSummaryDataRefresh(
            beforeRefreshRequested,
            rebuildAsync);
        if (request.Queued)
        {
            RaiseRequiredEvent(
                PlaylistPresentationRefreshRequested,
                new PlaylistPresentationRefreshRequestedEventArgs(
                    PlaylistPresentationRefreshKind.SummaryData,
                    reason,
                    rebuildAsync),
                nameof(PlaylistPresentationRefreshRequested));
            if (!HasDeferredPlaylistSummaryRefresh())
            {
                return CurrentPlaylistSummaryDataRebuildGeneration;
            }
        }
        return request.NextBuildGeneration;
    }

    internal void RequestDeferredPlaylistSummaryPresentationRefresh()
    {
        lock (playlistSummaryTransitionLock)
        {
            if (isPlaylistSummaryModeRequested
                && !playlistSummaryDataBuildStopped
                && !deferredPlaylistSummaryDataRefreshRequested)
            {
                deferredPlaylistSummaryPresentationRefreshRequested = true;
            }
        }
    }

    internal bool HasDeferredPlaylistSummaryPresentationRefresh()
    {
        lock (playlistSummaryTransitionLock)
        {
            return deferredPlaylistSummaryPresentationRefreshRequested;
        }
    }

    internal bool HasDeferredPlaylistSummaryRefresh()
    {
        lock (playlistSummaryTransitionLock)
        {
            return deferredPlaylistSummaryDataRefreshRequested
                || deferredPlaylistSummaryPresentationRefreshRequested;
        }
    }

    /// <summary>
    /// Requests and drains a presentation-only refresh for the current playlist summary state.
    /// Hidden mode and an active data refresh remain governed by the existing deferred refresh state.
    /// </summary>
    internal void RequestPlaylistSummaryPresentationRefresh()
    {
        RaiseRequiredEvent(
            PlaylistPresentationRefreshRequested,
            new PlaylistPresentationRefreshRequestedEventArgs(
                PlaylistPresentationRefreshKind.SummaryPresentation,
                "playlist_summary_presentation"),
            nameof(PlaylistPresentationRefreshRequested));
    }

    internal void ApplyPlaylistSummaryDataRefresh(bool deferred, bool rebuildAsync)
    {
        if (!deferred)
        {
            DrainDeferredPlaylistSummaryRefresh(
                dataRefreshRequired: false,
                rebuildAsync);
        }
    }

    internal void ApplyPlaylistSummaryPresentationRefresh(bool deferred)
    {
        RequestDeferredPlaylistSummaryPresentationRefresh();
        if (!deferred)
        {
            DrainDeferredPlaylistSummaryRefresh(
                dataRefreshRequired: false,
                rebuildAsync: true);
        }
    }

    /// <summary>
    /// Requests a filter-driven presentation refresh only while summary mode is visible.
    /// Hidden-mode filter changes leave pending data refresh state untouched.
    /// </summary>
    private void RequestVisiblePlaylistSummaryPresentationRefresh()
    {
        if (IsPlaylistSummaryMode)
        {
            RequestPlaylistSummaryPresentationRefresh();
        }
    }

    internal PlaylistSummaryDeferredRefreshKind TakeDeferredPlaylistSummaryRefresh(bool dataRefreshRequired)
    {
        return TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired, out _);
    }

    internal PlaylistSummaryDeferredRefreshKind TakeDeferredPlaylistSummaryRefresh(
        bool dataRefreshRequired,
        out bool rebuildAsync)
    {
        lock (playlistSummaryTransitionLock)
        {
            bool applyDataRefresh = dataRefreshRequired || deferredPlaylistSummaryDataRefreshRequested;
            bool applyPresentationRefresh = deferredPlaylistSummaryPresentationRefreshRequested;
            rebuildAsync = deferredPlaylistSummaryDataRebuildAsync;
            deferredPlaylistSummaryDataRefreshRequested = false;
            deferredPlaylistSummaryDataRebuildAsync = true;
            deferredPlaylistSummaryPresentationRefreshRequested = false;
            if (playlistSummaryDataBuildStopped)
            {
                return PlaylistSummaryDeferredRefreshKind.None;
            }
            if (applyDataRefresh)
            {
                return PlaylistSummaryDeferredRefreshKind.Data;
            }
            return applyPresentationRefresh
                ? PlaylistSummaryDeferredRefreshKind.Presentation
                : PlaylistSummaryDeferredRefreshKind.None;
        }
    }

    private CancellationTokenSource InvalidatePlaylistSummaryDataUnsafe()
    {
        CancellationTokenSource cancellation = playlistSummaryDataBuildCancellation;
        playlistSummaryDataBuildCancellation = null;
        playlistSummaryDataRebuildGeneration++;
        playlistSummaryPresentationGeneration++;
        playlistSummaryRowsCache.Clear();
        playlistSummaryRowsCacheValid = false;
        playlistSummaryRowsCacheDataRebuildGeneration = 0L;
        playlistSummaryRowsCacheGeneration++;
        return cancellation;
    }

    private void ClearPlaylistSummarySelectionRestoreIfSupersededUnsafe(long nextDataRebuildGeneration)
    {
        if (pendingPlaylistSummarySelectionPlaylistIds != null
            && pendingPlaylistSummarySelectionMinDataGeneration > 0L
            && pendingPlaylistSummarySelectionMinDataGeneration != long.MaxValue
            && nextDataRebuildGeneration > pendingPlaylistSummarySelectionMinDataGeneration)
        {
            ClearPlaylistSummarySelectionRestoreUnsafe();
        }
    }

    internal bool TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest request)
    {
        CancellationTokenSource previousCancellation;
        lock (playlistSummaryTransitionLock)
        {
            if (!isPlaylistSummaryModeRequested || playlistSummaryDataBuildStopped)
            {
                request = null;
                return false;
            }
            previousCancellation = playlistSummaryDataBuildCancellation;
            if (playlistSummaryDataBuildActiveCount == 0)
            {
                playlistSummaryDataBuildIdleCompletion = CreatePendingLifecycleCompletion();
            }
            playlistSummaryDataBuildCancellation = new CancellationTokenSource();
            request = new PlaylistSummaryDataBuildRequest(
                ++playlistSummaryDataRebuildGeneration,
                playlistSummaryRowsCacheGeneration,
                playlistSummaryDataBuildCancellation);
            ClearPlaylistSummarySelectionRestoreIfSupersededUnsafe(request.Generation);
            playlistSummaryDataBuildActiveCount++;
        }
        CancelDataBuild(previousCancellation);
        return true;
    }

    internal void CompletePlaylistSummaryDataBuild(PlaylistSummaryDataBuildRequest request)
    {
        if (request?.TryComplete() != true)
        {
            return;
        }
        TaskCompletionSource<bool> idleCompletion = null;
        lock (playlistSummaryTransitionLock)
        {
            if (ReferenceEquals(playlistSummaryDataBuildCancellation, request.CancellationSource))
            {
                playlistSummaryDataBuildCancellation = null;
            }
            playlistSummaryDataBuildActiveCount--;
            if (playlistSummaryDataBuildActiveCount == 0)
            {
                idleCompletion = playlistSummaryDataBuildIdleCompletion;
            }
        }
        idleCompletion?.TrySetResult(true);
        request.CancellationSource.Dispose();
    }

    /// <summary>
    /// Returns a task that completes when every playlist-summary data build active at the capture point,
    /// including overlapping replacements, has left the build lifecycle.
    /// </summary>
    internal Task WaitForPlaylistSummaryDataBuildIdleAsync()
    {
        lock (playlistSummaryTransitionLock)
        {
            return playlistSummaryDataBuildActiveCount == 0
                ? Task.CompletedTask
                : playlistSummaryDataBuildIdleCompletion.Task;
        }
    }

    internal void StopPlaylistSummaryDataBuild()
    {
        CancellationTokenSource cancellation;
        lock (playlistSummaryTransitionLock)
        {
            playlistSummaryDataBuildStopped = true;
            cancellation = playlistSummaryDataBuildCancellation;
            playlistSummaryDataBuildCancellation = null;
            playlistSummaryDataRebuildGeneration++;
            playlistSummaryPresentationGeneration++;
            ClearPlaylistSummarySelectionRestoreUnsafe();
        }
        CancelDataBuild(cancellation);
    }

    internal bool IsPlaylistSummaryDataBuildIdle
    {
        get
        {
            lock (playlistSummaryTransitionLock)
            {
                return playlistSummaryDataBuildActiveCount == 0;
            }
        }
    }

    private static TaskCompletionSource<bool> CreatePendingLifecycleCompletion()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<bool> CreateCompletedLifecycleCompletion()
    {
        var completion = CreatePendingLifecycleCompletion();
        completion.SetResult(true);
        return completion;
    }

    internal long CurrentPlaylistSummaryPresentationGeneration
    {
        get
        {
            lock (playlistSummaryTransitionLock)
            {
                return playlistSummaryPresentationGeneration;
            }
        }
    }

    internal bool TryGetAppliedPlaylistSummaryPerformanceInteraction(
        out PerformanceInteraction interaction)
    {
        lock (playlistSummaryTransitionLock)
        {
            interaction = appliedPlaylistSummaryPerformanceInteraction;
            return interaction.InteractionId > 0L;
        }
    }

    internal long CurrentPlaylistSummaryDataRebuildGeneration
    {
        get
        {
            lock (playlistSummaryTransitionLock)
            {
                return playlistSummaryDataRebuildGeneration;
            }
        }
    }

    internal long CurrentPlaylistSummaryRowsCacheGeneration
    {
        get
        {
            lock (playlistSummaryTransitionLock)
            {
                return playlistSummaryRowsCacheGeneration;
            }
        }
    }

    internal bool IsCurrentPlaylistSummaryDataRebuildGeneration(long generation)
    {
        lock (playlistSummaryTransitionLock)
        {
            return generation == playlistSummaryDataRebuildGeneration;
        }
    }

    internal void InvalidatePlaylistSummaryCache()
    {
        lock (playlistSummaryTransitionLock)
        {
            playlistSummaryRowsCache.Clear();
            playlistSummaryRowsCacheValid = false;
            playlistSummaryRowsCacheDataRebuildGeneration = 0L;
            playlistSummaryRowsCacheGeneration++;
        }
    }

    internal bool TrySetPlaylistSummaryRowsCache(IEnumerable<PlaylistSummaryRow> rows, long dataRebuildGeneration)
    {
        lock (playlistSummaryTransitionLock)
        {
            if (dataRebuildGeneration != playlistSummaryDataRebuildGeneration)
            {
                return false;
            }
            playlistSummaryRowsCache = [.. (rows ?? [])];
            playlistSummaryRowsCacheValid = true;
            playlistSummaryRowsCacheDataRebuildGeneration = dataRebuildGeneration;
            playlistSummaryRowsCacheGeneration++;
            return true;
        }
    }

    internal List<PlaylistSummaryRow> GetPlaylistSummaryRowsCacheSnapshot(out long cacheGeneration, out long dataRebuildGeneration)
    {
        lock (playlistSummaryTransitionLock)
        {
            cacheGeneration = playlistSummaryRowsCacheGeneration;
            dataRebuildGeneration = playlistSummaryRowsCacheDataRebuildGeneration;
            if (!playlistSummaryRowsCacheValid
                || dataRebuildGeneration <= 0L)
            {
                return null;
            }
            return [.. playlistSummaryRowsCache];
        }
    }

    internal bool TryApplyPlaylistSummary(PlaylistSummaryApplyRequest request)
    {
        if (request?.Rows == null)
        {
            throw new ArgumentException("Playlist summary rows are required.", nameof(request));
        }

        string nextSummaryText = request.SummaryText ?? string.Empty;
        PlaylistSummaryPresentationIdentity identity = request.Identity
            ?? new PlaylistSummaryPresentationIdentity(
                request.CacheGeneration
                    ?? request.DataRebuildGeneration
                    ?? request.PresentationGeneration,
                string.Empty,
                PlaylistOwnedFilter.All,
                nameof(PlaylistSummaryRow.Name),
                ListSortDirection.Ascending);
        bool rowsChanged;
        bool textChanged;
        bool operationContextChanged;
        PlaylistPresentationModeCommit modeCommit;
        PlaylistSourceRetirementRequest detailSourceRetirement;
        lock (playlistSummaryTransitionLock)
        {
            if (!isPlaylistSummaryModeRequested
                || playlistSummaryDataBuildStopped
                || request.PresentationGeneration != playlistSummaryPresentationGeneration
                || (request.DataRebuildGeneration.HasValue && request.DataRebuildGeneration.Value != playlistSummaryDataRebuildGeneration)
                || (request.CacheGeneration.HasValue && request.CacheGeneration.Value != playlistSummaryRowsCacheGeneration))
            {
                return false;
            }

            rowsChanged = !Equals(appliedPlaylistSummaryIdentity, identity);
            textChanged = playlistSummaryText != nextSummaryText;
            if (rowsChanged)
            {
                playlistSummaryView.ReplaceItemsWithoutNotification(
                    request.Rows,
                    identity.SourceVersion);
                appliedPlaylistSummaryIdentity = identity;
                Interlocked.Exchange(ref lastPlaylistSummaryBuildCompletedTimestamp, Stopwatch.GetTimestamp());
            }
            playlistSummaryText = nextSummaryText;
            appliedPlaylistSummaryPerformanceInteraction = PerformanceInteraction.Existing(
                "playlist_summary",
                request.DataRebuildGeneration ?? request.PresentationGeneration,
                request.PresentationGeneration);
            if (request.DataRebuildGeneration is long appliedDataGeneration)
            {
                lastPlaylistSummaryAppliedDataGeneration = Math.Max(
                    lastPlaylistSummaryAppliedDataGeneration,
                    appliedDataGeneration);
            }
            modeCommit = CommitPresentationModeWithoutNotification(
                summaryActive: true,
                detailActive: false);
            operationContextChanged =
                detailMainChartList.CommitOperationContextWithoutNotification(
                    MainViewUpdateMode.PlaylistFilterSelected);
            detailSourceRetirement = pendingPlaylistSummaryDetailSourceRetirement;
            pendingPlaylistSummaryDetailSourceRetirement = null;
        }
        PlaylistSourceClearCommitResult detailSourceClearCommit =
            detailSourceRetirement == null
                ? null
                : CommitDetailSourceClearForRegularView(detailSourceRetirement);

        var publishExceptions = new List<Exception>();
        if (rowsChanged)
        {
            TryPublish(playlistSummaryView.PublishReset, publishExceptions);
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryView)), publishExceptions);
        }
        if (textChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryText)), publishExceptions);
        }
        if (rowsChanged)
        {
            PublishPlaylistSummarySelectionRestore(publishExceptions);
        }
        else
        {
            TrySchedulePlaylistReloadCleanup();
        }
        TryPublish(
            () => detailMainChartList.PublishOperationContextChanged(operationContextChanged),
            publishExceptions);
        TryPublish(() => PublishPresentationMode(modeCommit), publishExceptions);
        TryPublish(
            () => PublishDetailSourceClearForRegularView(detailSourceClearCommit),
            publishExceptions);
        if (publishExceptions.Count > 0)
        {
            throw new PlaylistSummaryPublishException(new AggregateException(publishExceptions));
        }
        return true;
    }

    private static void TryPublish(Action publish, List<Exception> exceptions)
    {
        try
        {
            publish();
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
    }

    private void PublishPlaylistSummarySelectionRestore(List<Exception> exceptions)
    {
        TrySchedulePlaylistReloadCleanup();
        if (!TryTakePlaylistSummarySelectionRestore(out PlaylistSummarySelectionRestoreRequest request))
        {
            return;
        }

        if (PlaylistSummarySelectionRestoreRequested == null)
        {
            TryPublish(
                () => throw new InvalidOperationException(
                    nameof(PlaylistSummarySelectionRestoreRequested) + " is not subscribed."),
                exceptions);
            return;
        }

        TryPublish(
            () => dispatchPresentation(() => PublishPlaylistSummarySelectionRestoreOnPresentationThread(request)),
            exceptions);
    }

    private void PublishPlaylistSummarySelectionRestoreOnPresentationThread(
        PlaylistSummarySelectionRestoreRequest request)
    {
        Action<PlaylistSummarySelectionRestoreRequest>[] subscribers =
            PlaylistSummarySelectionRestoreRequested?.GetInvocationList()
                .Cast<Action<PlaylistSummarySelectionRestoreRequest>>()
                .ToArray();
        if (subscribers == null)
        {
            return;
        }

        var subscriberExceptions = new List<Exception>();
        foreach (Action<PlaylistSummarySelectionRestoreRequest> subscriber in subscribers)
        {
            try
            {
                subscriber(request);
            }
            catch (Exception ex)
            {
                subscriberExceptions.Add(ex);
            }
        }
        if (subscriberExceptions.Count > 0)
        {
            throw new AggregateException(subscriberExceptions);
        }
    }

    private static void CancelDataBuild(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

}

internal sealed class PlaylistSummaryApplyRequest
{
    internal IReadOnlyList<PlaylistSummaryRow> Rows { get; set; }

    internal string SummaryText { get; set; } = string.Empty;

    internal PlaylistSummaryPresentationIdentity Identity { get; set; }

    internal long PresentationGeneration { get; set; }

    internal long? DataRebuildGeneration { get; set; }

    internal long? CacheGeneration { get; set; }
}

internal sealed class PlaylistSummaryPresentationIdentity : IEquatable<PlaylistSummaryPresentationIdentity>
{
    internal PlaylistSummaryPresentationIdentity(
        long sourceVersion,
        string keywordFilter,
        PlaylistOwnedFilter ownedFilter,
        string sortColumn,
        ListSortDirection sortDirection)
    {
        SourceVersion = sourceVersion;
        KeywordFilter = (keywordFilter ?? string.Empty).Trim();
        OwnedFilter = ownedFilter;
        SortColumn = sortColumn ?? nameof(PlaylistSummaryRow.Name);
        SortDirection = sortDirection;
    }

    internal long SourceVersion { get; }

    private string KeywordFilter { get; }

    private PlaylistOwnedFilter OwnedFilter { get; }

    private string SortColumn { get; }

    private ListSortDirection SortDirection { get; }

    public bool Equals(PlaylistSummaryPresentationIdentity other)
    {
        return other != null
            && SourceVersion == other.SourceVersion
            && string.Equals(KeywordFilter, other.KeywordFilter, StringComparison.Ordinal)
            && OwnedFilter == other.OwnedFilter
            && string.Equals(SortColumn, other.SortColumn, StringComparison.Ordinal)
            && SortDirection == other.SortDirection;
    }

    public override bool Equals(object obj)
    {
        return Equals(obj as PlaylistSummaryPresentationIdentity);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            SourceVersion,
            KeywordFilter,
            OwnedFilter,
            SortColumn,
            SortDirection);
    }
}

internal sealed class PlaylistSummaryVersionedCollection : ObservableCollection<PlaylistSummaryRow>
{
    internal long Version { get; private set; }

    internal void ReplaceItemsWithoutNotification(
        IReadOnlyList<PlaylistSummaryRow> rows,
        long version)
    {
        Items.Clear();
        for (int index = 0; index < rows.Count; index++)
        {
            Items.Add(rows[index]);
        }
        Version = version;
    }

    internal void PublishReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(
            new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

internal sealed class PlaylistSummaryDataBuildRequest
{
    private int completed;

    internal PlaylistSummaryDataBuildRequest(
        long generation,
        long cacheGeneration,
        CancellationTokenSource cancellationSource)
    {
        Generation = generation;
        CacheGeneration = cacheGeneration;
        CancellationSource = cancellationSource ?? throw new ArgumentNullException(nameof(cancellationSource));
    }

    internal long Generation { get; }

    internal long CacheGeneration { get; }

    internal CancellationTokenSource CancellationSource { get; }

    internal CancellationToken CancellationToken => CancellationSource.Token;

    internal bool TryComplete()
    {
        return Interlocked.Exchange(ref completed, 1) == 0;
    }
}

internal enum PlaylistSummaryDeferredRefreshKind
{
    None,
    Presentation,
    Data
}

internal struct PlaylistSummaryDataRefreshRequestResult
{
    internal long NextBuildGeneration;

    internal bool Queued;
}

internal sealed class PlaylistSummarySelectionRestoreRequest
{
    internal PlaylistSummarySelectionRestoreRequest(
        IReadOnlyCollection<int> playlistIds,
        int? currentPlaylistId)
    {
        PlaylistIds = Array.AsReadOnly(
            (playlistIds ?? throw new ArgumentNullException(nameof(playlistIds))).ToArray());
        CurrentPlaylistId = currentPlaylistId;
    }

    internal IReadOnlyCollection<int> PlaylistIds { get; }

    internal int? CurrentPlaylistId { get; }
}

internal sealed class PlaylistSummaryDataRefreshRequestedEventArgs : EventArgs
{
    internal PlaylistSummaryDataRefreshRequestedEventArgs(
        string reason,
        bool rebuildAsync = true)
    {
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        RebuildAsync = rebuildAsync;
    }

    internal string Reason { get; }

    internal bool RebuildAsync { get; }
}

[Serializable]
internal sealed class PlaylistSummaryPublishException : Exception
{
    internal PlaylistSummaryPublishException()
    {
    }

    internal PlaylistSummaryPublishException(string message)
        : base(message)
    {
    }

    internal PlaylistSummaryPublishException(Exception innerException)
        : base("Playlist summary state was committed but publishing notifications failed.", innerException)
    {
    }

    internal PlaylistSummaryPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private PlaylistSummaryPublishException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}

internal sealed class PlaylistColumnPresentationCommit
{
    internal PlaylistColumnPresentationCommit(bool visibilityChanged, bool summaryColumnsChanged, long generation)
    {
        VisibilityChanged = visibilityChanged;
        SummaryColumnsChanged = summaryColumnsChanged;
        Generation = generation;
    }

    internal bool VisibilityChanged { get; }

    internal bool SummaryColumnsChanged { get; }

    internal long Generation { get; }
}

internal sealed class PlaylistMainTablePresentationCommit
{
    internal PlaylistMainTablePresentationCommit(
        PlaylistColumnPresentationCommit columnPresentation,
        PlaylistPresentationModeCommit modePresentation)
    {
        ColumnPresentation = columnPresentation ?? throw new ArgumentNullException(nameof(columnPresentation));
        ModePresentation = modePresentation ?? throw new ArgumentNullException(nameof(modePresentation));
    }

    internal PlaylistColumnPresentationCommit ColumnPresentation { get; }

    internal PlaylistPresentationModeCommit ModePresentation { get; }
}

internal sealed class PlaylistPresentationModeCommit
{
    internal PlaylistPresentationModeCommit(
        bool summaryChanged,
        bool detailChanged,
        bool headerChanged,
        bool summaryTextChanged)
    {
        SummaryChanged = summaryChanged;
        DetailChanged = detailChanged;
        HeaderChanged = headerChanged;
        SummaryTextChanged = summaryTextChanged;
    }

    internal bool SummaryChanged { get; }

    internal bool DetailChanged { get; }

    internal bool HeaderChanged { get; }

    internal bool SummaryTextChanged { get; }
}
