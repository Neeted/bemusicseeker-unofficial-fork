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
using BeMusicSeeker.Views;
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
            DetailBuildState.PendingRequest?.Complete();
            DetailBuildState.PendingRequest = null;
            activeBuildCancellation = DetailBuildState.CurrentBuildCancellation;
            DetailBuildState.CurrentBuildRequest = null;
            requestCompletion = DetailBuildState.AdvanceCompletedRequestVersionUnsafe(
                DetailBuildState.RequestVersion);
            lock (DetailViewState.SyncRoot)
            {
                DetailViewState.Source.CurrentIdentity = null;
                DetailViewState.Source.CurrentSelectionScope = PlaylistDetailSelectionScope.OrdinaryRoot;
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

    /// <summary>
    /// Creates playlist workspace state with explicit search persistence boundaries.
    /// </summary>
    /// <param name="playlistUrlInstallSink">所有入力付き要求の導入受付。受理時に待ち行列へ寿命を移し、未受理は false を返します。</param>
    /// <param name="keywordSearchFavoritesSettingsStore">The explicit normal/summary Favorites settings boundary.</param>
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
        Func<DroppedInstallBatchRequest, bool> playlistUrlInstallSink,
        Action<Uri> playlistUrlBrowserOpenSink,
        Action<Exception, string> externalPlaylistImportWarningLog,
        Action<string> externalPlaylistImportInfoLog,
        Action<Exception, string> beatorajaTableUrlImportWarningLog,
        Action<string> beatorajaTableUrlImportInfoLog,
        IMainChartColumnSettingsStore playlistSummaryColumnSettingsStore,
        PlaylistSummaryBmtSortCoordinator playlistSummaryBmtSort,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
        IKeywordSearchFavoritesSettingsStore keywordSearchFavoritesSettingsStore,
        Func<BMSPlaylist> playlistStoreProvider,
        PlaylistPropertySaveService propertySaveService,
        Func<BMSLibrary> playlistLibraryProvider,
        Func<Lr2ConfigOwner> lr2ConfigProvider,
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
        IUiDialogService playlistWorkspaceDialogService = null,
        ChartFileOperationSynchronizer playlistOperationAdmission = null,
        Func<Task> playlistUrlInstallCompletionProvider = null)
    {
        this.playlistOperationAdmission = playlistOperationAdmission;
        this.playlistUrlInstallCompletionProvider = playlistUrlInstallCompletionProvider ?? (() => Task.CompletedTask);
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
        IKeywordSearchHistorySettingsStore historySettingsStore = keywordSearchHistorySettingsStore
            ?? throw new ArgumentNullException(nameof(keywordSearchHistorySettingsStore));
        playlistSummaryKeywordSearchSavedQueryOwner = new KeywordSearchSavedQueryOwner(
            KeywordSearchSavedQueryScope.PlaylistSummary,
            historySettingsStore,
            keywordSearchFavoritesSettingsStore);
        playlistSummaryKeywordSearchAssistanceOwner = new KeywordSearchAssistanceOwner(
            playlistSummaryKeywordSearchSavedQueryOwner,
            GridKeywordSearchContext.PlaylistSummary);
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
        playlistUrlAcquisitionPresentationScheduler = playlistRestoreUiApplyScheduler;
        PlaylistReferenceApplyWorkflow = new PlaylistReferenceApplyWorkflowOwner(
            playlistReferenceApplyScheduler,
            playlistReloadCleanupShutdownRequestedProvider,
            playlistReloadLog);
        this.playlistWorkspaceDialogService = playlistWorkspaceDialogService ?? new UiDialogCoordinator();
        PlaylistRemovalWorkflow = new PlaylistRemovalWorkflowOwner(
            this.playlistWorkspaceDialogService,
            getPlaylistStore,
            getPlaylistLibrary,
            getLr2Config,
            customFolderOutputSettingsProvider,
            (store, kind) => TryBeginPlaylistMutationForOwner(
                store,
                kind));
        PlaylistTableLevelOverwriteWorkflow = new PlaylistTableLevelOverwriteWorkflowOwner(
            this.playlistWorkspaceDialogService,
            getPlaylistLibrary);
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

    async Task<BeatorajaBmtPreparation> ISettingsDialogWorkspacePort.PrepareBeatorajaBmtSettingsAsync(BeatorajaBmtOptionsSnapshot previous, BeatorajaBmtOptionsSnapshot target, LibraryFileMutationCapability capability)
        => getPlaylistStore() is { } playlist ? await playlist.BmtOutput.PrepareSettingsChangeAsync(previous, target, capability) : new(previous, target, [], []);

    Task ISettingsDialogWorkspacePort.CompleteBeatorajaBmtSettingsAsync(BeatorajaBmtPreparation preparation, LibraryFileMutationCapability capability)
        => getPlaylistStore()?.BmtOutput.CompleteSettingsChangeAsync(preparation, capability) ?? Task.CompletedTask;

    Task ISettingsDialogWorkspacePort.RunWithPlaylistOperationNotificationsAsync(
        Func<Task> operation,
        string operationName)
        => RunWithPlaylistOperationNotificationsAsync(operation, operationName);

    event EventHandler<PlaylistCatalogChangedEventArgs> ISettingsDialogWorkspacePort.PlaylistCatalogChanged
    {
        add => PlaylistCatalogChanged += value;
        remove => PlaylistCatalogChanged -= value;
    }

    private readonly ChartFileOperationSynchronizer playlistOperationAdmission;

    private readonly Func<Task> playlistUrlInstallCompletionProvider;

    bool ISettingsDialogCustomFolderOutputPort.CanBeginOutputOperation
        => playlistOperationAdmission?.CanEnter ?? getPlaylistStore()?.CanEnterPlaylistMutation == true;

    event EventHandler ISettingsDialogCustomFolderOutputPort.OutputOperationAvailabilityChanged
    {
        add { if (playlistOperationAdmission != null) { playlistOperationAdmission.AvailabilityChanged += value; } }
        remove { if (playlistOperationAdmission != null) { playlistOperationAdmission.AvailabilityChanged -= value; } }
    }

    LibraryFileMutationLease ISettingsDialogCustomFolderOutputPort.TryBeginOutputOperation()
    {
        if (playlistOperationAdmission != null)
        {
            return playlistOperationAdmission.TryEnter(out IDisposable accepted) ? (LibraryFileMutationLease)accepted : null;
        }
        return getPlaylistStore()?.TryEnterPlaylistMutation(out IDisposable lease) == true ? (LibraryFileMutationLease)lease : null;
    }

    CustomFolderOutputSettingsSnapshot ISettingsDialogCustomFolderOutputPort.CustomFolderOutputSettings
        => customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");

    void ISettingsDialogCustomFolderOutputPort.ValidateCustomFolderOutputRegistration(
        CustomFolderOutputSettingsSnapshot settings, IReadOnlyDictionary<string, string> renames)
        => getPlaylistStore()?.ValidateCustomFolderOutputRegistration(settings, renames);

    IReadOnlyList<string> ISettingsDialogCustomFolderOutputPort.PrepareCustomFolderOutputSearchRoots(
        string previousRootOutputBaseDirectory, CustomFolderOutputSettingsSnapshot settings, IReadOnlyList<string> currentDirectories,
        IReadOnlyDictionary<string, string> renames)
        => getPlaylistStore()?.PrepareCustomFolderOutputSearchRoots(previousRootOutputBaseDirectory, currentDirectories, settings, renames)
            ?? currentDirectories;

    IReadOnlyList<PlaylistCustomFolderOutputMaintenanceOwner.PreviousOutputLocation> ISettingsDialogCustomFolderOutputPort.CommitCustomFolderOutputSettings(
        CustomFolderOutputSettingsSnapshot previous, CustomFolderOutputSettingsSnapshot target,
        IReadOnlyDictionary<string, string> renames, LibraryFileMutationCapability capability)
        => getPlaylistStore()?.CommitCustomFolderOutputSettings(previous, target, renames, capability) ?? [];

    void ISettingsDialogCustomFolderOutputPort.RegenerateCustomFolderOutputs(
        IReadOnlyList<PlaylistCustomFolderOutputMaintenanceOwner.PreviousOutputLocation> previousLocations,
        CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability)
        => getPlaylistStore()?.RegenerateCustomFolderOutputs(previousLocations, settings, capability);

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
        return ExecutePlaylistMutationAsync(PlaylistWorkspaceMutationKind.SummarySort, capability => ApplyCurrentVisibleBmtOrderCore(visibleRowsSnapshot, capability));
    }

    private async Task ApplyCurrentVisibleBmtOrderCore(IReadOnlyList<PlaylistSummaryRow> visibleRows, LibraryFileMutationCapability capability)
    {
        if (GetSummaryBmtSort().ApplyCurrentVisibleOrder(visibleRows, capability))
        {
            await GetPlaylistStore().BmtOutput.SyncUrlsAsync("playlist_summary_apply_current_order_to_bmt_sort", capability).ConfigureAwait(false);
            RequestPlaylistSummaryBmtSortRefresh("playlist_summary_apply_current_order_to_bmt_sort");
        }
    }

    internal Task MoveSummaryRowsToBmtTopAsync(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<PlaylistSummaryRow> rowsSnapshot = [.. (rows ?? [])];
        return ExecutePlaylistMutationAsync(PlaylistWorkspaceMutationKind.SummarySort, capability => MoveSummaryRowsToBmtTopCore(rowsSnapshot, capability));
    }

    private async Task MoveSummaryRowsToBmtTopCore(IReadOnlyList<PlaylistSummaryRow> rows, LibraryFileMutationCapability capability)
    {
        if (GetSummaryBmtSort().MoveRowsToTop(rows, capability))
        {
            await GetPlaylistStore().BmtOutput.SyncUrlsAsync("playlist_summary_move_to_bmt_sort_top", capability).ConfigureAwait(false);
            RequestPlaylistSummaryBmtSortRefresh("playlist_summary_move_to_bmt_sort_top");
        }
    }

    internal Task MoveSummaryRowsToBmtBottomAsync(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<PlaylistSummaryRow> rowsSnapshot = [.. (rows ?? [])];
        return ExecutePlaylistMutationAsync(PlaylistWorkspaceMutationKind.SummarySort, capability => MoveSummaryRowsToBmtBottomCore(rowsSnapshot, capability));
    }

    private async Task MoveSummaryRowsToBmtBottomCore(IReadOnlyList<PlaylistSummaryRow> rows, LibraryFileMutationCapability capability)
    {
        if (GetSummaryBmtSort().MoveRowsToBottom(rows, capability))
        {
            await GetPlaylistStore().BmtOutput.SyncUrlsAsync("playlist_summary_move_to_bmt_sort_bottom", capability).ConfigureAwait(false);
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
        return ExecutePlaylistMutationAsync(PlaylistWorkspaceMutationKind.SummarySort, async capability =>
        {
            await DropSummaryRowsInBmtOrderCore(
                visibleRowsSnapshot,
                draggedRowsSnapshot,
                visibleInsertIndex,
                currentPlaylistId, capability).ConfigureAwait(false);
        });
    }

    private async Task<long> DropSummaryRowsInBmtOrderCore(
        IReadOnlyList<PlaylistSummaryRow> visibleRows,
        IReadOnlyList<PlaylistSummaryRow> draggedRowsSnapshot,
        int visibleInsertIndex,
        int? currentPlaylistId, LibraryFileMutationCapability capability)
    {
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
                    selectionRestoreRequest = QueuePlaylistSummarySelectionRestore(appliedDraggedRows, appliedCurrentPlaylistId);
                }, capability);
            if (!changed)
            {
                return 0L;
            }
            await GetPlaylistStore().BmtOutput.SyncUrlsAsync("playlist_summary_bmt_sort_drag_drop", capability).ConfigureAwait(false);
            dataRebuildGeneration = RequestPlaylistSummaryBmtSortRefresh(
                "playlist_summary_bmt_sort_drag_drop",
                selectionRestoreRequest != null
                    ? generation => SetPlaylistSummarySelectionRestoreMinimumGenerationUnsafe(
                        selectionRestoreRequest,
                        generation)
                    : null);
            if (selectionRestoreRequest != null && dataRebuildGeneration <= 0L)
            {
                ClearPlaylistSummarySelectionRestore(selectionRestoreRequest);
            }
            return dataRebuildGeneration;
        }
        catch
        {
            ClearPlaylistSummarySelectionRestore(selectionRestoreRequest);
            throw;
        }
    }

    private PlaylistSummarySelectionRestoreRequest QueuePlaylistSummarySelectionRestore(
        IEnumerable<PlaylistSummaryRow> draggedRows, int? currentPlaylistId)
    {
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
            return null;
        }
        int? selectedCurrentPlaylistId = currentPlaylistId.HasValue && playlistIds.Contains(currentPlaylistId.Value)
            ? currentPlaylistId
            : playlistIds.FirstOrDefault();
        var selectionRestoreRequest = new PlaylistSummarySelectionRestoreRequest(playlistIds, selectedCurrentPlaylistId);
        lock (playlistSummaryTransitionLock)
        {
            pendingPlaylistSummarySelectionRestore = selectionRestoreRequest;
            pendingPlaylistSummarySelectionMinDataGeneration = long.MaxValue;
            return selectionRestoreRequest;
        }
    }

    private void SetPlaylistSummarySelectionRestoreMinimumGenerationUnsafe(
        PlaylistSummarySelectionRestoreRequest selectionRestoreRequest,
        long dataRebuildGeneration)
    {
        // 開始順ではなくデータ世代がこの意図を消費できる表示を特定します。
        // 先に開始した操作でも、後から割り当てられた新世代の意図で置き換えられます。
        if (selectionRestoreRequest == null
            || dataRebuildGeneration <= 0L
            || lastPlaylistSummaryAppliedDataGeneration > dataRebuildGeneration
            || (pendingPlaylistSummarySelectionRestore != null
                && pendingPlaylistSummarySelectionMinDataGeneration > 0L
                && pendingPlaylistSummarySelectionMinDataGeneration != long.MaxValue
                && dataRebuildGeneration < pendingPlaylistSummarySelectionMinDataGeneration))
        {
            return;
        }
        pendingPlaylistSummarySelectionRestore = selectionRestoreRequest;
        pendingPlaylistSummarySelectionMinDataGeneration = dataRebuildGeneration;
    }

    private bool TryTakePlaylistSummarySelectionRestore(out PlaylistSummarySelectionRestoreRequest request)
    {
        lock (playlistSummaryTransitionLock)
        {
            if (playlistSummaryDataBuildStopped
                || pendingPlaylistSummarySelectionRestore == null
                || pendingPlaylistSummarySelectionRestore.PlaylistIds.Count == 0
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
            request = pendingPlaylistSummarySelectionRestore;
            ClearPlaylistSummarySelectionRestoreUnsafe();
            return true;
        }
    }

    private void ClearPlaylistSummarySelectionRestore(PlaylistSummarySelectionRestoreRequest request)
    {
        if (request == null)
        {
            return;
        }
        lock (playlistSummaryTransitionLock)
        {
            if (ReferenceEquals(pendingPlaylistSummarySelectionRestore, request))
            {
                ClearPlaylistSummarySelectionRestoreUnsafe();
            }
        }
    }

    private void ClearPlaylistSummarySelectionRestoreUnsafe()
    {
        pendingPlaylistSummarySelectionRestore = null;
        pendingPlaylistSummarySelectionMinDataGeneration = 0L;
    }

    internal async Task ApplyImportedTablesToBmtFrontAsync(IReadOnlyList<BMSTable> importedTables, LibraryFileMutationCapability capability)
    {
        if (GetSummaryBmtSort().ApplyImportedTablesToFront(importedTables, capability))
        {
            await GetPlaylistStore().BmtOutput.SyncUrlsAsync("beatoraja_table_url_import", capability);
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

    private PlaylistOwnedFilter playlistSummaryOwnedFilter = PlaylistOwnedFilter.All;

    private long lastPlaylistSummaryBuildCompletedTimestamp;

    private long lastPlaylistSummaryBuildElapsedMs;

    private readonly object playlistSummaryTransitionLock = new();

    private long playlistSummaryPresentationGeneration;

    private PerformanceInteraction appliedPlaylistSummaryPerformanceInteraction;

    private long playlistSummaryDataRebuildGeneration;

    private long lastPlaylistSummaryAppliedDataGeneration;

    private PlaylistSummarySelectionRestoreRequest pendingPlaylistSummarySelectionRestore;

    private long pendingPlaylistSummarySelectionMinDataGeneration;

    private long playlistSummaryRowsCacheGeneration;

    private readonly PlaylistSummaryVersionedCollection playlistSummaryRowsCache = new();

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
        if (pendingPlaylistSummarySelectionRestore != null
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
        TaskCompletionSource<bool> completion = CreatePendingLifecycleCompletion();
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

    /// <summary>UI公開時に現行raw行と表参照索引を揃えます。未読込み表を含むrawも保持し、再利用不可なら次の明示要求では再構築します。</summary>
    internal bool TrySetPlaylistSummaryRowsCache(IEnumerable<PlaylistSummaryRow> rows, long dataRebuildGeneration, bool reusable = true)
    {
        lock (playlistSummaryTransitionLock)
        {
            if (dataRebuildGeneration != playlistSummaryDataRebuildGeneration)
            {
                return false;
            }
            playlistSummaryRowsCache.ReplaceItemsWithoutNotification(rows as IReadOnlyList<PlaylistSummaryRow> ?? [.. (rows ?? [])], dataRebuildGeneration);
            playlistSummaryRowsCacheValid = reusable;
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

    /// <summary>現行要求の行と通知を一括反映し、反映直前の選択または対応するBMT選択意図を終端で復元します。</summary>
    internal bool TryApplyPlaylistSummary(PlaylistSummaryApplyRequest request)
    {
        if (request?.Rows == null)
        {
            throw new ArgumentException("Playlist summary rows are required.", nameof(request));
        }

        var publishExceptions = new List<Exception>();
        bool applied = false;
        bool rowsChanged = false;
        try
        {
            EventArgs startingArgs = EventArgs.Empty;
            lock (playlistSummaryTransitionLock)
            {
                if (IsPlaylistSummaryApplyCurrentUnsafe(request)
                    && !Equals(appliedPlaylistSummaryIdentity, GetPlaylistSummaryApplyIdentity(request)))
                { startingArgs = new CustomTableSelectionResetStartingEventArgs(); }
            }
            applied = playlistSummaryView.PreserveSelection(() => TryApplyPlaylistSummaryCore(request, publishExceptions, out rowsChanged), () =>
            {
                TryPublish(TrySchedulePlaylistReloadCleanup, publishExceptions);
                EventArgs args = rowsChanged && TryTakePlaylistSummarySelectionRestore(out PlaylistSummarySelectionRestoreRequest restore)
                    ? new CustomTableSelectionUpdateEventArgs(restore.PlaylistIds.Cast<object>(), restore.CurrentPlaylistId, restore.CurrentPlaylistId)
                    : EventArgs.Empty;
                TryPublish(() => playlistSummaryView.PublishSelectionUpdateCompleted(args), publishExceptions);
            }, startingArgs);
        }
        catch (Exception ex) { publishExceptions.Add(ex); }
        if (publishExceptions.Count > 0) { throw new PlaylistSummaryPublishException(new AggregateException(publishExceptions)); }
        return applied;
    }

    /// <summary>同じtransition lock内で予備判定と実採用を照合し、通知中の要求失効も実採用前に拒否します。</summary>
    private bool IsPlaylistSummaryApplyCurrentUnsafe(PlaylistSummaryApplyRequest request)
        => isPlaylistSummaryModeRequested && !playlistSummaryDataBuildStopped
            && request.PresentationGeneration == playlistSummaryPresentationGeneration
            && (!request.DataRebuildGeneration.HasValue || request.DataRebuildGeneration.Value == playlistSummaryDataRebuildGeneration)
            && (!request.CacheGeneration.HasValue || request.CacheGeneration.Value == playlistSummaryRowsCacheGeneration);

    /// <summary>未指定時も既存の表示識別規則を使い、Resetの予備判定と採用を揃えます。</summary>
    private static PlaylistSummaryPresentationIdentity GetPlaylistSummaryApplyIdentity(PlaylistSummaryApplyRequest request)
        => request.Identity ?? new PlaylistSummaryPresentationIdentity(
            request.CacheGeneration ?? request.DataRebuildGeneration ?? request.PresentationGeneration,
            string.Empty, PlaylistOwnedFilter.All, nameof(PlaylistSummaryRow.Name), ListSortDirection.Ascending);

    private bool TryApplyPlaylistSummaryCore(PlaylistSummaryApplyRequest request, List<Exception> publishExceptions, out bool rowsChanged)
    {
        string nextSummaryText;
        PlaylistSummaryPresentationIdentity identity = GetPlaylistSummaryApplyIdentity(request);
        rowsChanged = false;
        bool textChanged;
        bool operationContextChanged;
        PlaylistPresentationModeCommit modeCommit;
        PlaylistSourceRetirementRequest detailSourceRetirement;
        lock (playlistSummaryTransitionLock)
        {
            if (!IsPlaylistSummaryApplyCurrentUnsafe(request))
            {
                return false;
            }

            rowsChanged = !Equals(appliedPlaylistSummaryIdentity, identity);
            if (rowsChanged)
            {
                playlistSummaryView.ReplaceItemsWithoutNotification(
                    request.Rows,
                    identity.SourceVersion);
                appliedPlaylistSummaryIdentity = identity;
                Interlocked.Exchange(ref lastPlaylistSummaryBuildCompletedTimestamp, Stopwatch.GetTimestamp());
            }
            nextSummaryText = request.SummaryText ?? FormatPlaylistSummaryText(playlistSummaryView.TotalCharts, playlistSummaryView.Count);
            textChanged = playlistSummaryText != nextSummaryText;
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

        if (rowsChanged)
        {
            TryPublish(playlistSummaryView.PublishReset, publishExceptions);
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryView)), publishExceptions);
        }
        if (textChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryText)), publishExceptions);
        }
        TryPublish(
            () => detailMainChartList.PublishOperationContextChanged(operationContextChanged),
            publishExceptions);
        TryPublish(() => PublishPresentationMode(modeCommit), publishExceptions);
        TryPublish(
            () => PublishDetailSourceClearForRegularView(detailSourceClearCommit),
            publishExceptions);
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

    /// <summary>明示文言。省略時は採用collectionの件数・譜面数から同じ公開処理で生成します。</summary>
    internal string SummaryText { get; set; }

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

    /// <summary>現在viewへ採用済みの条件。セルの局所再評価は未採用要求の条件を使いません。</summary>
    internal string KeywordFilter { get; }

    /// <summary>現在viewへ採用済みの条件。セルの局所再評価は未採用要求の条件を使いません。</summary>
    internal PlaylistOwnedFilter OwnedFilter { get; }

    /// <summary>現在viewへ採用済みの条件。セルの局所再評価は未採用要求の条件を使いません。</summary>
    internal string SortColumn { get; }

    /// <summary>現在viewへ採用済みの条件。セルの局所再評価は未採用要求の条件を使いません。</summary>
    internal ListSortDirection SortDirection { get; }

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

/// <summary>採用済み行・表参照/位置索引・件数を同じ寿命で保持し、一括採用と局所編集を接続します。</summary>
internal sealed class PlaylistSummaryVersionedCollection : ObservableCollection<PlaylistSummaryRow>, ICustomTableSelectionIdentitySource
{
    private readonly Dictionary<BMSTable, (PlaylistSummaryRow Row, int Index)> rowsByTable = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<int, int> indicesByPlaylistId = [];
    /// <summary>行集合の変更前に、更新直前の選択識別を捕捉します。</summary>
    public event EventHandler SelectionUpdateStarting;
    /// <summary>公開通知後に、一般選択または操作固有の選択意図を復元します。</summary>
    public event EventHandler SelectionUpdateCompleted;

    /// <summary>Items更新前から公開通知終端まで選択を一回捕捉・復元します。実全体Resetだけ編集値の先引渡しを開始通知で指定します。</summary>
    internal T PreserveSelection<T>(Func<T> update, Action completed = null, EventArgs startingArgs = null)
        => CustomTableSelectionUpdate.Run(() => SelectionUpdateStarting?.Invoke(this, startingArgs ?? EventArgs.Empty), update,
            completed ?? (() => PublishSelectionUpdateCompleted(EventArgs.Empty)));

    /// <summary>操作固有の選択意図または一般復元を、更新終端で表へ渡します。</summary>
    internal void PublishSelectionUpdateCompleted(EventArgs args) => SelectionUpdateCompleted?.Invoke(this, args);

    /// <summary>表示位置を表IDへ変換し、行投影の交換後も同じ表を照合します。</summary>
    public object GetSelectionIdentity(int index) => index >= 0 && index < Count ? Items[index]?.PlaylistId : null;

    /// <summary>一括採用・局所更新と一緒に維持するID索引から表示位置を返します。</summary>
    public int IndexOfSelectionIdentity(object identity)
        => identity is int id && indicesByPlaylistId.TryGetValue(id, out int index) ? index : -1;

    /// <summary>同じsummary表示の更新だけを同一文脈とします。</summary>
    public bool SharesSelectionIdentityScope(ICustomTableSelectionIdentitySource other) => ReferenceEquals(this, other);

    internal long Version { get; private set; }

    /// <summary>採用行の譜面数合計。一括採用時に集計し、局所加入・離脱で差し引きます。</summary>
    internal int TotalCharts { get; private set; }

    /// <summary>現在の表参照に対応する行を、一覧の列挙・実体化をせず取得します。旧表と同IDの別参照は一致させません。</summary>
    internal bool TryGetTableRow(BMSTable table, out PlaylistSummaryRow row)
    {
        row = null;
        if (table == null || !rowsByTable.TryGetValue(table, out (PlaylistSummaryRow Row, int Index) entry)) { return false; }
        row = entry.Row;
        return true;
    }

    /// <summary>本番の一括置換境界で行・版・表参照索引を揃え、公開通知は呼出元へ委ねます。</summary>
    internal void ReplaceItemsWithoutNotification(
        IReadOnlyList<PlaylistSummaryRow> rows,
        long version)
    {
        Items.Clear();
        rowsByTable.Clear();
        indicesByPlaylistId.Clear();
        TotalCharts = 0;
        for (int index = 0; index < rows.Count; index++)
        {
            Items.Add(rows[index]);
            if (rows[index]?.PlaylistId is int id) { indicesByPlaylistId.TryAdd(id, index); }
            TotalCharts = checked(TotalCharts + (rows[index]?.TotalCharts ?? 0));
            if (rows[index]?.TableRef is BMSTable table) { rowsByTable.TryAdd(table, (rows[index], index)); }
        }
        Version = version;
    }

    /// <summary>cacheの失効・停止で行を解放するとき、同じ寿命の表参照索引も解放します。</summary>
    protected override void ClearItems()
    {
        rowsByTable.Clear();
        indicesByPlaylistId.Clear();
        TotalCharts = 0;
        base.ClearItems();
    }

    /// <summary>現在対象の旧位置を確保してから属性を反映し、対象だけのfilterと二分比較で集合・位置・件数を更新します。</summary>
    internal System.Collections.Specialized.NotifyCollectionChangedEventArgs ApplySavedProperties(
        BMSTable table, PlaylistSummaryRow raw, IComparer<PlaylistSummaryRow> comparer, Func<PlaylistSummaryRow, bool> matches, out int projectedRows)
    {
        TryGetTableRow(table, out PlaylistSummaryRow current);
        int oldIndex = current == null ? -1 : rowsByTable[table].Index;
        // raw/currentが同じ参照でも、旧位置の検索より先にsortキーを書き換えません。
        projectedRows = 0;
        if (raw != null) { raw.ApplySavedProperties(table, notify: false); projectedRows++; }
        if (current != null && !ReferenceEquals(current, raw)) { current.ApplySavedProperties(table, notify: false); projectedRows++; }
        PlaylistSummaryRow target = current ?? raw;
        if (target == null) { return null; }
        bool included = matches(target);
        if (current != null && !included)
        {
            Items.RemoveAt(oldIndex); rowsByTable.Remove(table); TotalCharts -= current.TotalCharts;
            if (current.PlaylistId is int id) { indicesByPlaylistId.Remove(id); }
            ReindexShiftedReferences(oldIndex, Items.Count - 1);
            return new(System.Collections.Specialized.NotifyCollectionChangedAction.Remove, current, oldIndex);
        }
        if (!included) { return null; }
        if (current != null) { Items.RemoveAt(oldIndex); }
        int low = 0, high = Items.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (comparer.Compare(Items[middle], target) < 0) { low = middle + 1; } else { high = middle; }
        }
        // 同値域の上端も二分探索し、全体の安定sortと同じ相対順を保ちます。
        int nextIndex = low;
        if (oldIndex >= low)
        {
            high = Items.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (comparer.Compare(Items[middle], target) <= 0) { low = middle + 1; } else { high = middle; }
            }
            nextIndex = Math.Min(oldIndex, low);
        }
        Items.Insert(nextIndex, target);
        ReindexShiftedReferences(current == null ? nextIndex : Math.Min(oldIndex, nextIndex),
            current == null ? Items.Count - 1 : Math.Max(oldIndex, nextIndex));
        if (current == null)
        {
            TotalCharts = checked(TotalCharts + target.TotalCharts);
            return new(System.Collections.Specialized.NotifyCollectionChangedAction.Add, target, nextIndex);
        }
        return nextIndex == oldIndex ? null : new(System.Collections.Specialized.NotifyCollectionChangedAction.Move, target, nextIndex, oldIndex);
    }

    // 値・filter・比較の全件評価はしません。Listの参照移動区間だけ、同じ索引の位置を追従させます。
    private void ReindexShiftedReferences(int first, int last)
    {
        for (int index = first; index <= last; index++)
        {
            if (Items[index].PlaylistId is int id) { indicesByPlaylistId[id] = index; }
            if (Items[index].TableRef is BMSTable table) { rowsByTable[table] = (Items[index], index); }
        }
    }

    /// <summary>局所更新済みcollectionの変更だけをBindingへ公開します。</summary>
    internal void PublishLocalChange(System.Collections.Specialized.NotifyCollectionChangedEventArgs change)
    {
        if (change == null) { return; }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(change);
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

/// <summary>BMT操作が実際に適用した表IDと主行を、対応データ世代の反映まで保持する不変の意図です。</summary>
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

    [System.Obsolete(DiagnosticId = "SYSLIB0051")]
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
