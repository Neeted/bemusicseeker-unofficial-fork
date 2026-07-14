using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns playlist detail state and the complete playlist-summary build and presentation workflow.
/// </summary>
public sealed partial class PlaylistWorkspaceViewModel : ViewModel
{
    private readonly Action<Action> dispatchPresentation;

    private readonly MainChartListViewModel detailMainChartList;

    private readonly Action<string> detailRetentionLog;

    private readonly Action<string> detailViewLog;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private IMainChartColumnSettingsStore playlistSummaryColumnSettingsStore;

    private readonly SemaphoreSlim manualReloadSemaphore = new(1, 1);

    private PlaylistSummaryBmtSortCoordinator playlistSummaryBmtSort;

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
            lock (DetailViewState.SyncRoot)
            {
                DetailViewState.Source.CurrentIdentity = null;
                DetailViewState.View.CurrentIdentity = null;
            }
            Volatile.Write(ref detailDataSource, dataSource);
        }
        ResetPlaylistLibraryIndexPrewarmForDataSourceChange();
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
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider)
    {
        this.dispatchPresentation = dispatchPresentation ?? throw new ArgumentNullException(nameof(dispatchPresentation));
        detailMainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        DetailBuildState = buildState ?? throw new ArgumentNullException(nameof(buildState));
        DetailViewState = viewState ?? throw new ArgumentNullException(nameof(viewState));
        this.detailViewLog = detailViewLog ?? throw new ArgumentNullException(nameof(detailViewLog));
        this.detailRetentionLog = detailRetentionLog ?? throw new ArgumentNullException(nameof(detailRetentionLog));
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider
            ?? throw new ArgumentNullException(nameof(customFolderOutputSettingsProvider));
    }

    internal void ConfigureSummaryBmtSort(PlaylistSummaryBmtSortCoordinator coordinator)
    {
        if (coordinator == null)
        {
            throw new ArgumentNullException(nameof(coordinator));
        }
        if (playlistSummaryBmtSort != null)
        {
            throw new InvalidOperationException("Playlist summary BMT sort is already configured.");
        }
        playlistSummaryBmtSort = coordinator;
    }

    internal void ConfigurePlaylistSummaryColumnSettingsStore(IMainChartColumnSettingsStore store)
    {
        if (store == null)
        {
            throw new ArgumentNullException(nameof(store));
        }
        if (playlistSummaryColumnSettingsStore != null)
        {
            throw new InvalidOperationException("Playlist summary column settings store is already configured.");
        }
        playlistSummaryColumnSettingsStore = store;
    }

    internal void ResetPlaylistSummaryColumnsToDefault()
    {
        IMainChartColumnSettingsStore store = playlistSummaryColumnSettingsStore
            ?? throw new InvalidOperationException("Playlist summary column settings store is not configured.");
        PlaylistSummaryColumnsSettings = store.ResetPlaylistSummary();
    }

    internal void ApplyCurrentVisibleBmtOrder(IEnumerable<PlaylistSummaryRow> visibleRows)
    {
        if (GetSummaryBmtSort().ApplyCurrentVisibleOrder(visibleRows))
        {
            RequestPlaylistSummaryBmtSortRefresh("playlist_summary_apply_current_order_to_bmt_sort");
        }
    }

    internal void MoveSummaryRowsToBmtTop(IEnumerable<PlaylistSummaryRow> rows)
    {
        if (GetSummaryBmtSort().MoveRowsToTop(rows))
        {
            RequestPlaylistSummaryBmtSortRefresh("playlist_summary_move_to_bmt_sort_top");
        }
    }

    internal void MoveSummaryRowsToBmtBottom(IEnumerable<PlaylistSummaryRow> rows)
    {
        if (GetSummaryBmtSort().MoveRowsToBottom(rows))
        {
            RequestPlaylistSummaryBmtSortRefresh("playlist_summary_move_to_bmt_sort_bottom");
        }
    }

    internal long DropSummaryRowsInBmtOrder(
        IEnumerable<PlaylistSummaryRow> visibleRows,
        IEnumerable<PlaylistSummaryRow> draggedRows,
        int visibleInsertIndex)
    {
        if (!GetSummaryBmtSort().DropRows(visibleRows, draggedRows, visibleInsertIndex))
        {
            return 0L;
        }
        return RequestPlaylistSummaryBmtSortRefresh("playlist_summary_bmt_sort_drag_drop");
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
        return playlistSummaryBmtSort
            ?? throw new InvalidOperationException("Playlist summary BMT sort is not configured.");
    }

    private long RequestPlaylistSummaryBmtSortRefresh(string reason)
    {
        PlaylistSummaryDataRefreshRequestResult request = RequestPlaylistSummaryDataRefresh(invalidateTableCountCache: false);
        if (!request.Queued)
        {
            return request.NextBuildGeneration;
        }
        PlaylistSummaryDataRefreshRequested?.Invoke(
            this,
            new PlaylistSummaryDataRefreshRequestedEventArgs(
                reason,
                invalidateTableCountCache: false,
                rebuildAsync: false,
                requestAlreadyQueued: true,
                nextBuildGeneration: request.NextBuildGeneration));
        return request.NextBuildGeneration;
    }

    private ChartListSortParameters playlistSummarySortParameters;

    private PlaylistSummaryColumnSettings playlistSummaryColumnsSettings;

    private Visibility columnSettingsVisibilityForPlaylist = Visibility.Collapsed;

    private long columnPresentationGeneration;

    private ObservableCollection<PlaylistSummaryRow> playlistSummaryView = [];

    private string playlistSummaryText = string.Empty;

    private WeakReference<ObservableCollection<PlaylistSummaryRow>> previousPlaylistSummaryViewWeakReference;

    private bool isPlaylistSummaryMode;

    private bool isPlaylistDetailViewActive;

    private bool useAsyncChartRowsViewBinding = true;

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

    private long playlistSummaryDataRebuildGeneration;

    private long playlistSummaryRowsCacheGeneration;

    private List<PlaylistSummaryRow> playlistSummaryRowsCache = [];

    private bool playlistSummaryRowsCacheValid;

    private long playlistSummaryRowsCacheDataRebuildGeneration;

    private readonly Dictionary<string, PlaylistSummaryCountResult> playlistSummaryTableCountCache = new(StringComparer.OrdinalIgnoreCase);

    private long playlistSummaryTableCountCacheGeneration;

    private CancellationTokenSource playlistSummaryDataBuildCancellation;

    private int playlistSummaryDataBuildActiveCount;

    private bool playlistSummaryDataBuildStopped;

    private bool deferredPlaylistSummaryDataRefreshRequested;

    private bool deferredPlaylistSummaryPresentationRefreshRequested;

    /// <summary>
    /// Raised after the summary view is replaced and code-behind selection restoration can run.
    /// </summary>
    internal event EventHandler<PlaylistSummaryViewAppliedEventArgs> PlaylistSummaryViewApplied;

    /// <summary>
    /// Raised after the child owner accepts a different playlist-summary sort.
    /// </summary>
    internal event EventHandler PlaylistSummarySortRequested;

    /// <summary>
    /// Raised after a playlist-summary filter input changes and the shell must refresh the visible summary.
    /// </summary>
    internal event EventHandler PlaylistSummaryFilterChanged;

    /// <summary>
    /// Raised after a playlist-summary data mutation requires the shell to schedule a fresh summary build.
    /// </summary>
    internal event EventHandler<PlaylistSummaryDataRefreshRequestedEventArgs> PlaylistSummaryDataRefreshRequested;

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
        PlaylistSummarySortRequested?.Invoke(this, EventArgs.Empty);
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
    /// <param name="commitBindingModeFirst">Preserves a caller's legacy binding-before-column commit order.</param>
    /// </summary>
    internal PlaylistMainTablePresentationCommit CommitMainTablePresentationWithoutNotification(
        MainChartListColumnSelection selection,
        bool playlistDetailActive,
        bool commitBindingModeFirst = false)
    {
        // Play-history historically commits binding mode before column presentation;
        // regular chart transitions retain their existing column-first order.
        PlaylistColumnPresentationCommit columnPresentation;
        PlaylistBindingModeCommit bindingMode;
        if (commitBindingModeFirst)
        {
            bindingMode = CommitBindingModeWithoutNotification(playlistDetailActive);
            columnPresentation = CommitColumnPresentationWithoutNotification(
                selection.PlaylistColumnSettingsVisibility,
                selection.PlaylistSummaryColumnsSettings);
        }
        else
        {
            columnPresentation = CommitColumnPresentationWithoutNotification(
                selection.PlaylistColumnSettingsVisibility,
                selection.PlaylistSummaryColumnsSettings);
            bindingMode = CommitBindingModeWithoutNotification(playlistDetailActive);
        }
        return new PlaylistMainTablePresentationCommit(columnPresentation, bindingMode);
    }

    internal void CommitMainTableColumnSetting(
        MainChartListViewModel mainChartList,
        MainChartListColumnSelection selection)
    {
        if (mainChartList == null)
        {
            throw new ArgumentNullException(nameof(mainChartList));
        }

        mainChartList.ColumnsSettings = selection.ColumnsSettings;
        PlaylistColumnPresentationCommit commit = CommitColumnPresentationWithoutNotification(
            selection.PlaylistColumnSettingsVisibility,
            selection.PlaylistSummaryColumnsSettings);
        mainChartList.CommitAppliedColumnMode(selection.AppliedMode);
        PublishColumnPresentation(commit);
    }

    internal void PublishColumnPresentation(PlaylistColumnPresentationCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        if (commit.VisibilityChanged && IsCurrentColumnPresentationCommit(commit))
        {
            RaisePropertyChanged(nameof(ColumnSettingsVisibilityForPlaylist));
        }
        if (commit.SummaryColumnsChanged && IsCurrentColumnPresentationCommit(commit))
        {
            RaisePropertyChanged(nameof(PlaylistSummaryColumnsSettings));
        }
    }

    private bool IsCurrentColumnPresentationCommit(PlaylistColumnPresentationCommit commit)
    {
        return commit.Generation == Interlocked.Read(ref columnPresentationGeneration);
    }

    internal PlaylistBindingModeCommit CommitBindingModeWithoutNotification(bool playlistDetailActive)
    {
        bool useAsyncBinding = !playlistDetailActive;
        bool detailActiveChanged = isPlaylistDetailViewActive != playlistDetailActive;
        bool asyncBindingChanged = useAsyncChartRowsViewBinding != useAsyncBinding;
        isPlaylistDetailViewActive = playlistDetailActive;
        useAsyncChartRowsViewBinding = useAsyncBinding;
        return new PlaylistBindingModeCommit(detailActiveChanged, asyncBindingChanged);
    }

    internal void PublishBindingMode(PlaylistBindingModeCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        if (commit.DetailActiveChanged)
        {
            RaisePropertyChanged(nameof(IsPlaylistDetailViewActive));
        }
        if (commit.AsyncBindingChanged)
        {
            RaisePropertyChanged(nameof(UseAsyncChartRowsViewBinding));
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
            PublishBindingMode(commit.BindingMode);
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
            bool changed;
            CancellationTokenSource cancellation = null;
            lock (playlistSummaryTransitionLock)
            {
                changed = isPlaylistSummaryMode != value;
                isPlaylistSummaryMode = value;
                if (changed && !value)
                {
                    cancellation = playlistSummaryDataBuildCancellation;
                    playlistSummaryDataBuildCancellation = null;
                    playlistSummaryDataRebuildGeneration++;
                    playlistSummaryPresentationGeneration++;
                    deferredPlaylistSummaryDataRefreshRequested = false;
                    deferredPlaylistSummaryPresentationRefreshRequested = false;
                }
            }
            CancelDataBuild(cancellation);
            if (changed)
            {
                RaisePropertyChanged(nameof(IsPlaylistSummaryMode));
            }
        }
    }

    /// <summary>
    /// Changes the playlist summary presentation mode and applies its associated header state.
    /// </summary>
    /// <returns><see langword="true"/> when the mode itself changed.</returns>
    internal bool SetPlaylistSummaryMode(bool enabled)
    {
        bool changed = IsPlaylistSummaryMode != enabled;
        IsPlaylistSummaryMode = enabled;
        if (enabled)
        {
            GridHeaderText = BeMusicSeeker.Properties.Resources.Playlist_summary_header;
        }
        else
        {
            GridHeaderText = string.Empty;
            PlaylistSummaryText = string.Empty;
        }
        return changed;
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
    /// Gets whether the main table should use async ItemsSource binding outside playlist detail view.
    /// </summary>
    public bool UseAsyncChartRowsViewBinding
    {
        get => useAsyncChartRowsViewBinding;
        internal set
        {
            if (useAsyncChartRowsViewBinding != value)
            {
                useAsyncChartRowsViewBinding = value;
                RaisePropertyChanged(nameof(UseAsyncChartRowsViewBinding));
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
    /// Gets or sets playlist summary column settings.
    /// </summary>
    public PlaylistSummaryColumnSettings PlaylistSummaryColumnsSettings
    {
        get => playlistSummaryColumnsSettings;
        internal set
        {
            if (ReferenceEquals(playlistSummaryColumnsSettings, value))
            {
                return;
            }

            playlistSummaryColumnsSettings = value;
            RaisePropertyChanged(nameof(PlaylistSummaryColumnsSettings));
        }
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
                PlaylistSummaryFilterChanged?.Invoke(this, EventArgs.Empty);
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
                PlaylistSummaryFilterChanged?.Invoke(this, EventArgs.Empty);
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
        }
        CancelDataBuild(cancellation);
        return generation;
    }

    internal PlaylistSummaryDataRefreshRequestResult RequestPlaylistSummaryDataRefresh(bool invalidateTableCountCache)
    {
        CancellationTokenSource cancellation;
        PlaylistSummaryDataRefreshRequestResult result;
        lock (playlistSummaryTransitionLock)
        {
            if (playlistSummaryDataBuildStopped)
            {
                return default;
            }
            cancellation = InvalidatePlaylistSummaryDataUnsafe(invalidateTableCountCache);
            bool queued = isPlaylistSummaryMode;
            if (queued)
            {
                deferredPlaylistSummaryDataRefreshRequested = true;
                deferredPlaylistSummaryPresentationRefreshRequested = false;
            }
            result = new PlaylistSummaryDataRefreshRequestResult
            {
                NextBuildGeneration = isPlaylistSummaryMode
                    ? playlistSummaryDataRebuildGeneration + 1L
                    : 0L,
                Queued = queued
            };
        }
        CancelDataBuild(cancellation);
        return result;
    }

    internal void RequestDeferredPlaylistSummaryPresentationRefresh()
    {
        lock (playlistSummaryTransitionLock)
        {
            if (isPlaylistSummaryMode
                && !playlistSummaryDataBuildStopped
                && !deferredPlaylistSummaryDataRefreshRequested)
            {
                deferredPlaylistSummaryPresentationRefreshRequested = true;
            }
        }
    }

    internal PlaylistSummaryDeferredRefreshKind TakeDeferredPlaylistSummaryRefresh(bool dataRefreshRequired)
    {
        lock (playlistSummaryTransitionLock)
        {
            bool applyDataRefresh = dataRefreshRequired || deferredPlaylistSummaryDataRefreshRequested;
            bool applyPresentationRefresh = deferredPlaylistSummaryPresentationRefreshRequested;
            deferredPlaylistSummaryDataRefreshRequested = false;
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

    private CancellationTokenSource InvalidatePlaylistSummaryDataUnsafe(bool invalidateTableCountCache)
    {
        CancellationTokenSource cancellation = playlistSummaryDataBuildCancellation;
        playlistSummaryDataBuildCancellation = null;
        playlistSummaryDataRebuildGeneration++;
        playlistSummaryPresentationGeneration++;
        playlistSummaryRowsCache.Clear();
        if (invalidateTableCountCache)
        {
            playlistSummaryTableCountCache.Clear();
            playlistSummaryTableCountCacheGeneration++;
        }
        playlistSummaryRowsCacheValid = false;
        playlistSummaryRowsCacheDataRebuildGeneration = 0L;
        playlistSummaryRowsCacheGeneration++;
        return cancellation;
    }

    internal bool TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest request)
    {
        CancellationTokenSource previousCancellation;
        lock (playlistSummaryTransitionLock)
        {
            if (!isPlaylistSummaryMode || playlistSummaryDataBuildStopped)
            {
                request = null;
                return false;
            }
            previousCancellation = playlistSummaryDataBuildCancellation;
            playlistSummaryDataBuildCancellation = new CancellationTokenSource();
            request = new PlaylistSummaryDataBuildRequest(
                ++playlistSummaryDataRebuildGeneration,
                playlistSummaryRowsCacheGeneration,
                playlistSummaryTableCountCacheGeneration,
                playlistSummaryDataBuildCancellation);
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
        lock (playlistSummaryTransitionLock)
        {
            if (ReferenceEquals(playlistSummaryDataBuildCancellation, request.CancellationSource))
            {
                playlistSummaryDataBuildCancellation = null;
            }
            playlistSummaryDataBuildActiveCount--;
        }
        request.CancellationSource.Dispose();
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

    internal void InvalidatePlaylistSummaryCache(bool invalidateTableCountCache)
    {
        lock (playlistSummaryTransitionLock)
        {
            playlistSummaryRowsCache.Clear();
            if (invalidateTableCountCache)
            {
                playlistSummaryTableCountCache.Clear();
                playlistSummaryTableCountCacheGeneration++;
            }
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
                || dataRebuildGeneration <= 0L
                || dataRebuildGeneration != playlistSummaryDataRebuildGeneration)
            {
                return null;
            }
            return [.. playlistSummaryRowsCache];
        }
    }

    internal bool TryGetPlaylistSummaryTableCount(string key, out PlaylistSummaryCountResult countResult)
    {
        lock (playlistSummaryTransitionLock)
        {
            return playlistSummaryTableCountCache.TryGetValue(key ?? string.Empty, out countResult);
        }
    }

    internal bool TrySetPlaylistSummaryTableCount(
        string key,
        PlaylistSummaryCountResult countResult,
        long expectedTableCountCacheGeneration)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        lock (playlistSummaryTransitionLock)
        {
            if (expectedTableCountCacheGeneration != playlistSummaryTableCountCacheGeneration)
            {
                return false;
            }
            playlistSummaryTableCountCache[key] = countResult;
            if (playlistSummaryTableCountCache.Count > 10000)
            {
                playlistSummaryTableCountCache.Clear();
                playlistSummaryTableCountCacheGeneration++;
            }
            return true;
        }
    }

    internal bool TryApplyPlaylistSummary(PlaylistSummaryApplyRequest request)
    {
        if (request?.Rows == null)
        {
            throw new ArgumentException("Playlist summary rows are required.", nameof(request));
        }

        string nextSummaryText = request.SummaryText ?? string.Empty;
        bool rowsChanged;
        bool textChanged;
        lock (playlistSummaryTransitionLock)
        {
            if (!isPlaylistSummaryMode
                || playlistSummaryDataBuildStopped
                || request.PresentationGeneration != playlistSummaryPresentationGeneration
                || (request.DataRebuildGeneration.HasValue && request.DataRebuildGeneration.Value != playlistSummaryDataRebuildGeneration)
                || (request.CacheGeneration.HasValue && request.CacheGeneration.Value != playlistSummaryRowsCacheGeneration))
            {
                return false;
            }

            rowsChanged = !ReferenceEquals(playlistSummaryView, request.Rows);
            textChanged = playlistSummaryText != nextSummaryText;
            if (rowsChanged)
            {
                ObservableCollection<PlaylistSummaryRow> previousView = playlistSummaryView;
                playlistSummaryView = request.Rows;
                if (previousView != null)
                {
                    previousPlaylistSummaryViewWeakReference = new WeakReference<ObservableCollection<PlaylistSummaryRow>>(previousView);
                }
                Interlocked.Exchange(ref lastPlaylistSummaryBuildCompletedTimestamp, Stopwatch.GetTimestamp());
            }
            playlistSummaryText = nextSummaryText;
        }

        var publishExceptions = new List<Exception>();
        if (rowsChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryView)), publishExceptions);
        }
        if (textChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryText)), publishExceptions);
        }
        PublishPlaylistSummaryViewApplied(
            new PlaylistSummaryViewAppliedEventArgs(request.DataRebuildGeneration ?? 0L),
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

    private void PublishPlaylistSummaryViewApplied(
        PlaylistSummaryViewAppliedEventArgs eventArgs,
        List<Exception> exceptions)
    {
        TrySchedulePlaylistReloadCleanup();
        Delegate[] subscribers = PlaylistSummaryViewApplied?.GetInvocationList() ?? [];
        foreach (Delegate subscriber in subscribers)
        {
            EventHandler<PlaylistSummaryViewAppliedEventArgs> handler =
                (EventHandler<PlaylistSummaryViewAppliedEventArgs>)subscriber;
            TryPublish(() => handler(this, eventArgs), exceptions);
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
    internal ObservableCollection<PlaylistSummaryRow> Rows { get; set; }

    internal string SummaryText { get; set; } = string.Empty;

    internal long PresentationGeneration { get; set; }

    internal long? DataRebuildGeneration { get; set; }

    internal long? CacheGeneration { get; set; }
}

internal sealed class PlaylistSummaryDataBuildRequest
{
    private int completed;

    internal PlaylistSummaryDataBuildRequest(
        long generation,
        long cacheGeneration,
        long tableCountCacheGeneration,
        CancellationTokenSource cancellationSource)
    {
        Generation = generation;
        CacheGeneration = cacheGeneration;
        TableCountCacheGeneration = tableCountCacheGeneration;
        CancellationSource = cancellationSource ?? throw new ArgumentNullException(nameof(cancellationSource));
    }

    internal long Generation { get; }

    internal long CacheGeneration { get; }

    internal long TableCountCacheGeneration { get; }

    internal CancellationTokenSource CancellationSource { get; }

    internal CancellationToken CancellationToken => CancellationSource.Token;

    internal bool TryComplete()
    {
        return Interlocked.Exchange(ref completed, 1) == 0;
    }
}

internal struct PlaylistSummaryCountResult
{
    internal int ScannedEntries;

    internal int TotalCharts;

    internal int OwnedCharts;
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

internal sealed class PlaylistSummaryViewAppliedEventArgs : EventArgs
{
    internal PlaylistSummaryViewAppliedEventArgs(long dataRebuildGeneration)
    {
        DataRebuildGeneration = dataRebuildGeneration;
    }

    internal long DataRebuildGeneration { get; }
}

internal sealed class PlaylistSummaryDataRefreshRequestedEventArgs : EventArgs
{
    internal PlaylistSummaryDataRefreshRequestedEventArgs(
        string reason,
        bool invalidateTableCountCache,
        bool rebuildAsync = true,
        bool requestAlreadyQueued = false,
        long nextBuildGeneration = 0L)
    {
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        InvalidateTableCountCache = invalidateTableCountCache;
        RebuildAsync = rebuildAsync;
        RequestAlreadyQueued = requestAlreadyQueued;
        NextBuildGeneration = nextBuildGeneration;
    }

    internal string Reason { get; }

    internal bool InvalidateTableCountCache { get; }

    internal bool RebuildAsync { get; }

    internal bool RequestAlreadyQueued { get; }

    internal long NextBuildGeneration { get; }
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

internal sealed class PlaylistBindingModeCommit
{
    internal PlaylistBindingModeCommit(bool detailActiveChanged, bool asyncBindingChanged)
    {
        DetailActiveChanged = detailActiveChanged;
        AsyncBindingChanged = asyncBindingChanged;
    }

    internal bool DetailActiveChanged { get; }

    internal bool AsyncBindingChanged { get; }
}

internal sealed class PlaylistMainTablePresentationCommit
{
    internal PlaylistMainTablePresentationCommit(
        PlaylistColumnPresentationCommit columnPresentation,
        PlaylistBindingModeCommit bindingMode)
    {
        ColumnPresentation = columnPresentation ?? throw new ArgumentNullException(nameof(columnPresentation));
        BindingMode = bindingMode ?? throw new ArgumentNullException(nameof(bindingMode));
    }

    internal PlaylistColumnPresentationCommit ColumnPresentation { get; }

    internal PlaylistBindingModeCommit BindingMode { get; }
}
