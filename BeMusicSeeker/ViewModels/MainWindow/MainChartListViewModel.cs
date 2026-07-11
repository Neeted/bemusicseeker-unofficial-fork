using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns main chart-list binding state while the shell keeps refresh orchestration during the transition.
/// </summary>
public sealed class MainChartListViewModel : ViewModel
{
    private readonly Action<Action> dispatchPresentationAction;

    internal MainChartRowProjectionOwner RowProjection { get; } = new();

    private IList rows = new List<object>();

    private int selectedIndex;

    private CustomTableColumnSettings columnsSettings;

    private string summaryText = string.Empty;

    private MainChartListSortPresentation sortParameters;

    private MainChartListSortTarget sortTarget;

    private long rowsCommitGeneration;

    internal MainChartListViewModel()
        : this(action => action())
    {
    }

    internal MainChartListViewModel(Action<Action> dispatchPresentationAction)
    {
        this.dispatchPresentationAction = dispatchPresentationAction
            ?? throw new ArgumentNullException(nameof(dispatchPresentationAction));
    }

    /// <summary>
    /// Raised immediately before a different row collection replaces the active main-table rows.
    /// </summary>
    internal event EventHandler RowsReplacing;

    internal event EventHandler RowsReplacementCanceled;

    internal event EventHandler RowsReplacementPublishFailed;

    /// <summary>
    /// Raised when the table asks the shell workflow to rebuild rows using a new sort.
    /// </summary>
    internal event EventHandler<MainChartListSortRequestedEventArgs> SortRequested;

    /// <summary>
    /// Raised on the UI thread when provider-backed visible cells need repainting without replacing rows.
    /// </summary>
    internal event EventHandler DisplayRefreshRequested;

    internal void PrepareRowsReplacement()
    {
        RowsReplacing?.Invoke(this, EventArgs.Empty);
    }

    internal void CancelRowsReplacement()
    {
        RowsReplacementCanceled?.Invoke(this, EventArgs.Empty);
    }

    internal void FailRowsReplacementPublish()
    {
        RowsReplacementPublishFailed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets the rows currently displayed by the main chart table.
    /// </summary>
    public IList Rows
    {
        get => rows;
        set
        {
            if (!ReferenceEquals(rows, value))
            {
                DisposeRows(rows);
                rows = value ?? new List<object>();
                RaisePropertyChanged(nameof(Rows));
            }
        }
    }

    /// <summary>
    /// Gets or sets the selected row index in the main chart table.
    /// </summary>
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (selectedIndex != value)
            {
                selectedIndex = value;
                RaisePropertyChanged(nameof(SelectedIndex));
            }
        }
    }

    /// <summary>
    /// Gets or sets the column settings used by the main chart table and its header menus.
    /// </summary>
    public CustomTableColumnSettings ColumnsSettings
    {
        get => columnsSettings;
        set
        {
            if (ReferenceEquals(columnsSettings, value))
            {
                return;
            }

            columnsSettings = value;
            RaisePropertyChanged(nameof(ColumnsSettings));
            RaisePropertyChanged(nameof(RowDragKind));
        }
    }

    /// <summary>
    /// Gets the drag behavior derived from the active main chart-table column settings.
    /// </summary>
    public CustomTableRowDragKind RowDragKind => ResolveRowDragKind(ColumnsSettings);

    /// <summary>
    /// Gets or sets the summary text displayed below the main chart table.
    /// </summary>
    public string SummaryText
    {
        get => summaryText;
        set
        {
            string next = value ?? string.Empty;
            if (summaryText != next)
            {
                summaryText = next;
                RaisePropertyChanged(nameof(SummaryText));
            }
        }
    }

    /// <summary>
    /// Gets the sort currently presented by the main chart table.
    /// </summary>
    public MainChartListSortPresentation SortParameters => sortParameters;

    /// <summary>
    /// Updates the active sort presentation when the shell changes between regular and play-history views.
    /// </summary>
    /// <param name="value">The active sort parameters.</param>
    /// <param name="target">The workflow that owns the active sort.</param>
    internal void SetSortPresentation(MainChartListSortPresentation value, MainChartListSortTarget target)
    {
        dispatchPresentationAction(() => SetSortPresentationCore(value, target));
    }

    private void SetSortPresentationCore(MainChartListSortPresentation value, MainChartListSortTarget target)
    {
        bool changed = sortTarget != target
            || !AreSameSortParameters(sortParameters, value);
        sortTarget = target;
        sortParameters = value;
        if (changed)
        {
            RaisePropertyChanged(nameof(SortParameters));
        }
    }

    /// <summary>
    /// Captures a table sort interaction together with the workflow currently presented by the child owner.
    /// </summary>
    /// <param name="columnName">The requested sort member.</param>
    /// <param name="direction">The requested direction.</param>
    /// <returns>The immutable request, or <see langword="null"/> when no column was supplied.</returns>
    internal MainChartListSortRequestedEventArgs CaptureSortRequest(string columnName, ListSortDirection direction)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }
        return new MainChartListSortRequestedEventArgs(columnName, direction, sortTarget);
    }

    /// <summary>
    /// Publishes a previously captured sort request so view changes cannot redirect delayed work.
    /// </summary>
    /// <param name="request">The immutable request captured at interaction time.</param>
    internal void RequestSort(MainChartListSortRequestedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        SortRequested?.Invoke(this, request);
    }

    /// <summary>
    /// Requests repaint of the existing main-table rows while preserving the ItemsSource and selection.
    /// </summary>
    internal void RequestDisplayRefresh()
    {
        EventHandler handler = DisplayRefreshRequested;
        if (handler == null)
        {
            return;
        }

        dispatchPresentationAction(() => handler(this, EventArgs.Empty));
    }

    /// <summary>
    /// Replaces main chart-table rows and optionally refreshes the normal row-count summary.
    /// </summary>
    /// <param name="nextRows">Rows that should be shown by the main chart table.</param>
    /// <param name="updateSummary">true to update the normal chart-list summary.</param>
    public void SetRows(IList nextRows, bool updateSummary)
    {
        Rows = nextRows;
        if (updateSummary)
        {
            UpdateSummaryText(nextRows);
        }
    }

    /// <summary>
    /// Replaces main chart-table rows using a known distinct-folder count for summary generation.
    /// </summary>
    /// <param name="nextRows">Rows that should be shown by the main chart table.</param>
    /// <param name="distinctFolderCount">Distinct folder count for the visible row set.</param>
    /// <param name="updateSummary">true to update the normal chart-list summary.</param>
    public void SetRows(IList nextRows, int distinctFolderCount, bool updateSummary)
    {
        Rows = nextRows;
        if (updateSummary)
        {
            UpdateSummaryText(nextRows?.Count ?? 0, distinctFolderCount);
        }
    }

    /// <summary>
    /// Updates the normal chart-list summary from the supplied row collection.
    /// </summary>
    /// <param name="rowsForSummary">Rows used to calculate visible chart and folder counts.</param>
    public void UpdateSummaryText(IList rowsForSummary)
    {
        if (rowsForSummary == null)
        {
            SummaryText = string.Empty;
            return;
        }

        if (rowsForSummary is IChartListViewMetadata metadata)
        {
            UpdateSummaryText(metadata.RowCount, metadata.DistinctFolderCount);
            return;
        }

        UpdateSummaryText(rowsForSummary.Count, CountDistinctFoldersForRows(rowsForSummary));
    }

    /// <summary>
    /// Updates the normal chart-list summary from known chart and folder counts.
    /// </summary>
    /// <param name="rowCount">Visible chart count.</param>
    /// <param name="distinctFolderCount">Visible distinct-folder count.</param>
    public void UpdateSummaryText(int rowCount, int distinctFolderCount)
    {
        SummaryText = FormatSummaryText(rowCount, distinctFolderCount);
    }

    /// <summary>
    /// Applies one main chart-list terminal state transition without shell callbacks.
    /// </summary>
    internal MainChartListRowsApplyResult ApplyRows(MainChartListRowsApplyRequest request)
    {
        MainChartListPreparedRowsApply prepared = PrepareRowsApply(request);
        MainChartListRowsCommit commit;
        try
        {
            commit = CommitPreparedRows(prepared);
        }
        catch
        {
            CancelPreparedRowsApply(prepared);
            throw;
        }

        return PublishRowsCommit(commit);
    }

    internal MainChartListCoordinatedRowsApplyResult ApplyCoordinatedRows(
        MainChartListRowsApplyRequest request,
        Func<Action, bool> tryCommitFeature,
        Action publishFeature)
    {
        if (tryCommitFeature == null)
        {
            throw new ArgumentNullException(nameof(tryCommitFeature));
        }
        if (publishFeature == null)
        {
            throw new ArgumentNullException(nameof(publishFeature));
        }

        MainChartListPreparedRowsApply prepared = PrepareRowsApply(request);
        MainChartListRowsCommit rowsCommit = null;
        Exception commitException = null;
        bool applied;
        try
        {
            applied = tryCommitFeature(() => rowsCommit = CommitPreparedRowsWithoutDisposal(prepared));
        }
        catch (Exception ex)
        {
            if (rowsCommit == null)
            {
                CancelPreparedRowsApply(prepared);
                throw;
            }
            applied = true;
            commitException = ex;
        }

        if (!applied)
        {
            if (rowsCommit == null)
            {
                CancelPreparedRowsApply(prepared);
                return default;
            }
            applied = true;
            commitException = new InvalidOperationException("A coordinated feature reported stale state after transferring row ownership.");
        }
        if (rowsCommit == null)
        {
            CancelPreparedRowsApply(prepared);
            throw new InvalidOperationException("A coordinated chart-list feature commit did not transfer row ownership.");
        }

        List<Exception> publishExceptions = [];
        if (commitException != null)
        {
            publishExceptions.Add(commitException);
        }
        MainChartListRowsApplyResult rowsApply = default;
        TryCoordinatedAction(() => DisposeCommittedRows(rowsCommit), publishExceptions);
        TryCoordinatedAction(() => rowsApply = PublishRowsCommit(rowsCommit), publishExceptions);
        TryCoordinatedAction(publishFeature, publishExceptions);
        if (publishExceptions.Count > 0)
        {
            throw new MainChartListCoordinatedPublishException(new AggregateException(publishExceptions));
        }
        return new MainChartListCoordinatedRowsApplyResult(applied: true, rowsApply);
    }

    internal PlayHistoryTerminalCommitResult ApplyPlayHistoryTerminal(
        PlayHistoryTerminalRequest request,
        PlayHistoryPresentationState state,
        PlaylistWorkspaceViewModel playlistWorkspace,
        RegularChartListOwner regularChartListOwner,
        PlaylistDetailBuildState playlistDetailBuildState,
        PlaylistDetailViewState playlistDetailViewState)
    {
        if (request?.ViewState == null || request.MainRowsRequest?.Rows == null)
        {
            throw new ArgumentException("A complete play-history terminal request is required.", nameof(request));
        }
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (playlistWorkspace == null) throw new ArgumentNullException(nameof(playlistWorkspace));
        if (regularChartListOwner == null) throw new ArgumentNullException(nameof(regularChartListOwner));
        if (playlistDetailBuildState == null) throw new ArgumentNullException(nameof(playlistDetailBuildState));
        if (playlistDetailViewState == null) throw new ArgumentNullException(nameof(playlistDetailViewState));

        var result = new PlayHistoryTerminalCommitResult();
        try
        {
            MainChartListCoordinatedRowsApplyResult coordinated = ApplyCoordinatedRows(
                request.MainRowsRequest,
                commitRows => state.TryCommitTerminal(
                    request,
                    result,
                    commitRows,
                    () =>
                    {
                        result.PlaylistSourceClear = playlistDetailBuildState.CommitSourceClear(playlistDetailViewState);
                        result.BindingMode = playlistWorkspace.CommitBindingModeWithoutNotification(playlistDetailActive: false);
                        result.ColumnPresentation = playlistWorkspace.CommitColumnPresentationWithoutNotification(
                            request.ColumnSelection.PlaylistColumnSettingsVisibility,
                            request.ColumnSelection.PlaylistSummaryColumnsSettings);
                        if (request.ColumnSelection.AppliedMode.HasValue)
                        {
                            regularChartListOwner.CommitExternalColumnMode(request.ColumnSelection.AppliedMode);
                        }
                        regularChartListOwner.ResetDerivedCaches();
                    }),
                () =>
                {
                    List<Exception> featurePublishExceptions = [];
                    if (result.ColumnPresentation != null)
                    {
                        TryCoordinatedAction(() => playlistWorkspace.PublishColumnPresentation(result.ColumnPresentation), featurePublishExceptions);
                    }
                    if (result.BindingMode != null)
                    {
                        TryCoordinatedAction(() => playlistWorkspace.PublishBindingMode(result.BindingMode), featurePublishExceptions);
                    }
                    if (featurePublishExceptions.Count > 0)
                    {
                        throw new AggregateException(featurePublishExceptions);
                    }
                });
            result.MainRowsApply = coordinated.RowsApply;
            return result;
        }
        catch (MainChartListCoordinatedPublishException ex)
        {
            throw new PlayHistoryTerminalPublishException(ex, ownershipTransferred: true, result);
        }
    }

    private static void TryCoordinatedAction(Action action, ICollection<Exception> exceptions)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
    }

    private MainChartListPreparedRowsApply PrepareRowsApply(MainChartListRowsApplyRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (request.Rows == null)
        {
            throw new ArgumentNullException(nameof(request.Rows));
        }
        if (request.Stopwatch == null)
        {
            throw new ArgumentNullException(nameof(request.Stopwatch));
        }
        if (request.ColumnsSettings == null)
        {
            throw new ArgumentNullException(nameof(request.ColumnsSettings));
        }

        string nextSummaryText = ResolveSummaryText(request.Summary);
        int nextSelectedIndex = request.SelectionPolicy == MainChartListSelectionPolicy.Reset
            ? -1
            : selectedIndex;
        bool rowsChanged = !ReferenceEquals(rows, request.Rows);
        bool columnsChanged = !ReferenceEquals(columnsSettings, request.ColumnsSettings);
        bool summaryChanged = summaryText != nextSummaryText;
        bool selectionChanged = selectedIndex != nextSelectedIndex;

        long prepareSwapMs = 0L;
        bool rowsReplacementPrepared = rowsChanged && request.RowsAlreadyPrepared;
        if (rowsChanged && !request.RowsAlreadyPrepared)
        {
            long prepareStartMs = request.Stopwatch.ElapsedMilliseconds;
            try
            {
                PrepareRowsReplacement();
            }
            catch (Exception prepareException)
            {
                try
                {
                    CancelRowsReplacement();
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(prepareException, cleanupException);
                }
                throw;
            }
            prepareSwapMs = request.Stopwatch.ElapsedMilliseconds - prepareStartMs;
            rowsReplacementPrepared = true;
        }

        return new MainChartListPreparedRowsApply(
            request,
            nextSummaryText,
            nextSelectedIndex,
            rowsChanged,
            columnsChanged,
            summaryChanged,
            selectionChanged,
            rowsReplacementPrepared,
            prepareSwapMs);
    }

    private MainChartListRowsCommit CommitPreparedRows(MainChartListPreparedRowsApply prepared)
    {
        return CommitPreparedRowsCore(prepared, disposePreviousRows: true);
    }

    private MainChartListRowsCommit CommitPreparedRowsWithoutDisposal(MainChartListPreparedRowsApply prepared)
    {
        return CommitPreparedRowsCore(prepared, disposePreviousRows: false);
    }

    private MainChartListRowsCommit CommitPreparedRowsCore(
        MainChartListPreparedRowsApply prepared,
        bool disposePreviousRows)
    {
        if (prepared == null)
        {
            throw new ArgumentNullException(nameof(prepared));
        }
        if (prepared.Committed)
        {
            throw new InvalidOperationException("The prepared main chart-list rows were already committed.");
        }

        MainChartListRowsApplyRequest request = prepared.Request;
        long columnSettingStartMs = request.Stopwatch.ElapsedMilliseconds;
        long columnSettingMs = request.ColumnPreparationMs
            + request.Stopwatch.ElapsedMilliseconds
            - columnSettingStartMs;

        long setViewStartMs = request.Stopwatch.ElapsedMilliseconds;
        IList previousRows = null;
        if (prepared.RowsChanged)
        {
            previousRows = rows;
            if (disposePreviousRows)
            {
                DisposeRows(previousRows);
                previousRows = null;
            }
            rows = request.Rows;
        }
        columnsSettings = request.ColumnsSettings;
        selectedIndex = prepared.NextSelectedIndex;
        summaryText = prepared.NextSummaryText;
        prepared.Committed = true;
        long commitGeneration = Interlocked.Increment(ref rowsCommitGeneration);

        return new MainChartListRowsCommit(
            prepared,
            columnSettingMs,
            request.Stopwatch.ElapsedMilliseconds - setViewStartMs,
            previousRows,
            commitGeneration);
    }

    private void DisposeCommittedRows(MainChartListRowsCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        if (commit.PreviousRowsPendingDisposal == null)
        {
            return;
        }

        IList previousRows = commit.PreviousRowsPendingDisposal;
        commit.PreviousRowsPendingDisposal = null;
        var disposeExceptions = new List<Exception>();
        if (previousRows is IChartListViewMetadata metadata)
        {
            try
            {
                metadata.DisposeRealizedRows();
            }
            catch (Exception ex)
            {
                disposeExceptions.Add(ex);
            }
        }
        else
        {
            foreach (object row in previousRows)
            {
                if (row is not IDisposable disposable)
                {
                    continue;
                }
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    disposeExceptions.Add(ex);
                }
            }
        }
        if (disposeExceptions.Count > 0)
        {
            throw new AggregateException("One or more previous main chart rows failed to dispose.", disposeExceptions);
        }
    }

    private MainChartListRowsApplyResult PublishRowsCommit(MainChartListRowsCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        if (commit.Published)
        {
            throw new InvalidOperationException("The main chart-list rows commit was already published.");
        }

        MainChartListPreparedRowsApply prepared = commit.Prepared;
        MainChartListRowsApplyRequest request = prepared.Request;
        long columnSettingMs = commit.ColumnSettingMs;
        long setViewNotificationStartMs = request.Stopwatch.ElapsedMilliseconds;
        long columnNotificationMs = 0L;
        try
        {
            if (prepared.ColumnsChanged && IsCurrentRowsCommit(commit))
            {
                long columnNotificationStartMs = request.Stopwatch.ElapsedMilliseconds;
                RaisePropertyChanged(nameof(ColumnsSettings));
                if (IsCurrentRowsCommit(commit))
                {
                    RaisePropertyChanged(nameof(RowDragKind));
                }
                columnNotificationMs = request.Stopwatch.ElapsedMilliseconds - columnNotificationStartMs;
                columnSettingMs += columnNotificationMs;
            }
            if (prepared.RowsChanged && IsCurrentRowsCommit(commit))
            {
                RaisePropertyChanged(nameof(Rows));
            }
            if (prepared.SummaryChanged && IsCurrentRowsCommit(commit))
            {
                RaisePropertyChanged(nameof(SummaryText));
            }
            if (prepared.SelectionChanged && IsCurrentRowsCommit(commit))
            {
                RaisePropertyChanged(nameof(SelectedIndex));
            }
        }
        catch
        {
            if (prepared.RowsReplacementPrepared)
            {
                FailRowsReplacementPublish();
            }
            throw;
        }

        commit.Published = true;
        long setViewMs = commit.CommitSetViewMs
            + request.Stopwatch.ElapsedMilliseconds
            - setViewNotificationStartMs
            - columnNotificationMs;
        long columnStageMs = request.Stopwatch.ElapsedMilliseconds - request.TerminalStageStartMs;
        return new MainChartListRowsApplyResult(
            prepared.PrepareSwapMs,
            columnSettingMs,
            setViewMs,
            columnStageMs,
            request.ColumnSettingReuse);
    }

    private bool IsCurrentRowsCommit(MainChartListRowsCommit commit)
    {
        return commit.Generation == Interlocked.Read(ref rowsCommitGeneration);
    }

    private void CancelPreparedRowsApply(MainChartListPreparedRowsApply prepared)
    {
        if (prepared?.RowsReplacementPrepared == true && !prepared.Committed)
        {
            CancelRowsReplacement();
        }
    }

    internal bool TryUpdateNormalSummary(IList expectedRows, int rowCount, int distinctFolderCount)
    {
        if (!ReferenceEquals(rows, expectedRows))
        {
            return false;
        }
        UpdateSummaryText(rowCount, distinctFolderCount);
        return true;
    }

    private static CustomTableRowDragKind ResolveRowDragKind(CustomTableColumnSettings settings)
    {
        return CustomTableRowDragKind.PlaylistDropCandidateRows;
    }

    private static bool AreSameSortParameters(
        MainChartListSortPresentation left,
        MainChartListSortPresentation right)
    {
        return left == null
            ? right == null
            : right != null
                && left.ColumnsName == right.ColumnsName
                && left.Direction == right.Direction;
    }

    private string ResolveSummaryText(MainChartListSummaryUpdate summary)
    {
        return summary.Kind switch
        {
            MainChartListSummaryKind.Preserve => summaryText,
            MainChartListSummaryKind.NormalRows => FormatSummaryText(
                summary.Rows.Count,
                summary.Rows is IChartListViewMetadata metadata
                    ? metadata.DistinctFolderCount
                    : CountDistinctFoldersForRows(summary.Rows)),
            MainChartListSummaryKind.NormalCounts => FormatSummaryText(summary.RowCount, summary.DistinctFolderCount),
            MainChartListSummaryKind.Explicit => summary.Text,
            _ => throw new ArgumentOutOfRangeException(nameof(summary)),
        };
    }

    private static string FormatSummaryText(int rowCount, int distinctFolderCount)
    {
        string text = "[" + rowCount + BeMusicSeeker.Properties.Resources.Num_songs;
        if (distinctFolderCount > 1)
        {
            return text + " / " + distinctFolderCount + BeMusicSeeker.Properties.Resources.Num_folders + "]";
        }
        return text + "]";
    }

    private static int CountDistinctFoldersForRows(IEnumerable rowsToCount)
    {
        if (rowsToCount == null)
        {
            return -1;
        }

        return rowsToCount.Cast<object>()
            .Select(GetSummaryFolderName)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static string GetSummaryFolderName(object row)
    {
        if (row is BMSFile bmsFile)
        {
            return bmsFile.Folder;
        }
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            return playlistDetailRow.Folder;
        }
        if (row is LibraryChartRow libraryChartRow)
        {
            return libraryChartRow.Folder;
        }
        return string.Empty;
    }

    /// <summary>
    /// Disposes realized or materialized row objects using the same contract as the legacy root chart-list setter.
    /// </summary>
    /// <param name="rowsToDispose">Rows that are about to leave the main chart table.</param>
    internal static void DisposeRows(IEnumerable rowsToDispose)
    {
        if (rowsToDispose == null)
        {
            return;
        }

        if (rowsToDispose is IChartListViewMetadata metadata)
        {
            metadata.DisposeRealizedRows();
            return;
        }

        foreach (object row in rowsToDispose)
        {
            if (row is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

/// <summary>
/// Identifies which root workflow owns a sort interaction shown by the shared main table.
/// </summary>
internal enum MainChartListSortTarget
{
    /// <summary>
    /// The normal library or playlist-detail workflow.
    /// </summary>
    Regular,

    /// <summary>
    /// The play-history workflow.
    /// </summary>
    PlayHistory
}

/// <summary>
/// Carries one main-table sort interaction from the child owner to the existing build orchestration.
/// </summary>
internal sealed class MainChartListSortRequestedEventArgs : EventArgs
{
    internal MainChartListSortRequestedEventArgs(
        string columnName,
        ListSortDirection direction,
        MainChartListSortTarget target)
    {
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        Target = target;
    }

    /// <summary>
    /// Gets the requested sort member.
    /// </summary>
    internal string ColumnName { get; }

    /// <summary>
    /// Gets the requested direction.
    /// </summary>
    internal ListSortDirection Direction { get; }

    /// <summary>
    /// Gets the workflow that was active when the interaction occurred.
    /// </summary>
    internal MainChartListSortTarget Target { get; }
}

internal enum MainChartListSelectionPolicy
{
    Preserve,
    Reset
}

internal enum MainChartListSummaryKind
{
    Preserve,
    NormalRows,
    NormalCounts,
    Explicit
}

internal readonly struct MainChartListSummaryUpdate
{
    private MainChartListSummaryUpdate(
        MainChartListSummaryKind kind,
        IList rows,
        int rowCount,
        int distinctFolderCount,
        string text)
    {
        Kind = kind;
        Rows = rows;
        RowCount = rowCount;
        DistinctFolderCount = distinctFolderCount;
        Text = text ?? string.Empty;
    }

    internal MainChartListSummaryKind Kind { get; }

    internal IList Rows { get; }

    internal int RowCount { get; }

    internal int DistinctFolderCount { get; }

    internal string Text { get; }

    internal static MainChartListSummaryUpdate Preserve() => new(MainChartListSummaryKind.Preserve, null, 0, 0, string.Empty);

    internal static MainChartListSummaryUpdate NormalRows(IList rows) => new(
        MainChartListSummaryKind.NormalRows,
        rows ?? throw new ArgumentNullException(nameof(rows)),
        0,
        0,
        string.Empty);

    internal static MainChartListSummaryUpdate NormalCounts(int rowCount, int distinctFolderCount) => new(
        MainChartListSummaryKind.NormalCounts,
        null,
        rowCount,
        distinctFolderCount,
        string.Empty);

    internal static MainChartListSummaryUpdate Explicit(string text) => new(
        MainChartListSummaryKind.Explicit,
        null,
        0,
        0,
        text);
}

internal sealed class MainChartListRowsApplyRequest
{
    internal IList Rows { get; set; }

    internal CustomTableColumnSettings ColumnsSettings { get; set; }

    internal MainChartListSelectionPolicy SelectionPolicy { get; set; }

    internal MainChartListSummaryUpdate Summary { get; set; }

    internal bool ColumnSettingReuse { get; set; }

    internal long ColumnPreparationMs { get; set; }

    internal long TerminalStageStartMs { get; set; }

    internal Stopwatch Stopwatch { get; set; }

    internal bool RowsAlreadyPrepared { get; set; }
}

internal sealed class MainChartListPreparedRowsApply
{
    internal MainChartListPreparedRowsApply(
        MainChartListRowsApplyRequest request,
        string nextSummaryText,
        int nextSelectedIndex,
        bool rowsChanged,
        bool columnsChanged,
        bool summaryChanged,
        bool selectionChanged,
        bool rowsReplacementPrepared,
        long prepareSwapMs)
    {
        Request = request;
        NextSummaryText = nextSummaryText;
        NextSelectedIndex = nextSelectedIndex;
        RowsChanged = rowsChanged;
        ColumnsChanged = columnsChanged;
        SummaryChanged = summaryChanged;
        SelectionChanged = selectionChanged;
        RowsReplacementPrepared = rowsReplacementPrepared;
        PrepareSwapMs = prepareSwapMs;
    }

    internal MainChartListRowsApplyRequest Request { get; }

    internal string NextSummaryText { get; }

    internal int NextSelectedIndex { get; }

    internal bool RowsChanged { get; }

    internal bool ColumnsChanged { get; }

    internal bool SummaryChanged { get; }

    internal bool SelectionChanged { get; }

    internal bool RowsReplacementPrepared { get; }

    internal long PrepareSwapMs { get; }

    internal bool Committed { get; set; }
}

internal sealed class MainChartListRowsCommit
{
    internal MainChartListRowsCommit(
        MainChartListPreparedRowsApply prepared,
        long columnSettingMs,
        long commitSetViewMs,
        IList previousRowsPendingDisposal,
        long generation)
    {
        Prepared = prepared;
        ColumnSettingMs = columnSettingMs;
        CommitSetViewMs = commitSetViewMs;
        PreviousRowsPendingDisposal = previousRowsPendingDisposal;
        Generation = generation;
    }

    internal MainChartListPreparedRowsApply Prepared { get; }

    internal long ColumnSettingMs { get; }

    internal long CommitSetViewMs { get; }

    internal long Generation { get; }

    internal IList PreviousRowsPendingDisposal { get; set; }

    internal bool Published { get; set; }
}

internal readonly struct MainChartListRowsApplyResult
{
    internal MainChartListRowsApplyResult(
        long prepareSwapMs,
        long columnSettingMs,
        long setViewMs,
        long columnStageMs,
        bool columnSettingReuse)
    {
        PrepareSwapMs = prepareSwapMs;
        ColumnSettingMs = columnSettingMs;
        SetViewMs = setViewMs;
        ColumnStageMs = columnStageMs;
        ColumnSettingReuse = columnSettingReuse;
    }

    internal long PrepareSwapMs { get; }

    internal long ColumnSettingMs { get; }

    internal long SetViewMs { get; }

    internal long ColumnStageMs { get; }

    internal bool ColumnSettingReuse { get; }
}

internal readonly struct MainChartListCoordinatedRowsApplyResult
{
    internal MainChartListCoordinatedRowsApplyResult(bool applied, MainChartListRowsApplyResult rowsApply)
    {
        Applied = applied;
        RowsApply = rowsApply;
    }

    internal bool Applied { get; }
    internal MainChartListRowsApplyResult RowsApply { get; }
}

[Serializable]
internal sealed class MainChartListCoordinatedPublishException : Exception
{
    internal MainChartListCoordinatedPublishException()
    {
    }

    internal MainChartListCoordinatedPublishException(string message)
        : base(message)
    {
    }

    internal MainChartListCoordinatedPublishException(Exception innerException)
        : base("The coordinated main chart-list state was committed but publishing failed.", innerException)
    {
    }

    internal MainChartListCoordinatedPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private MainChartListCoordinatedPublishException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
