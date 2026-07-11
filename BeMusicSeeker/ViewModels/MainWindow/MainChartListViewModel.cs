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

    private MainChartListCellEditContext pendingCellEditContext;

    private MainChartListCellEditContext activeCellEditContext;

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

    internal event EventHandler<MainChartListCellEditBeginningEventArgs> CellEditBeginningRequested;

    internal event EventHandler<MainChartListCellEditContext> CellEditStarted;

    internal event EventHandler<MainChartListCellEditEndedEventArgs> CellEditEndedRequested;

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
    internal void RequestSort(string columnName, ListSortDirection direction)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return;
        }
        SortRequested?.Invoke(this, new MainChartListSortRequestedEventArgs(columnName, direction, sortTarget));
    }

    internal bool TryBeginCellEdit(MainChartListCellEditContext context)
    {
        if (context == null)
        {
            return false;
        }
        var request = new MainChartListCellEditBeginningEventArgs(context);
        CellEditBeginningRequested?.Invoke(this, request);
        pendingCellEditContext = request.Accepted ? context : null;
        return request.Accepted;
    }

    internal void NotifyCellEditStarted(object row, string propertyName)
    {
        if (IsSameCellEdit(pendingCellEditContext, row, propertyName))
        {
            activeCellEditContext = pendingCellEditContext;
            pendingCellEditContext = null;
            CellEditStarted?.Invoke(this, activeCellEditContext);
        }
    }

    internal void RequestCellEditEnded(object row, string propertyName, string text, bool commit)
    {
        if (IsSameCellEdit(activeCellEditContext, row, propertyName))
        {
            MainChartListCellEditContext context = activeCellEditContext;
            activeCellEditContext = null;
            var request = new MainChartListCellEditEndedEventArgs(context, text, commit);
            CellEditEndedRequested?.Invoke(this, request);
        }
    }

    private static bool IsSameCellEdit(
        MainChartListCellEditContext context,
        object row,
        string propertyName)
    {
        return context != null
            && ReferenceEquals(context.Row, row)
            && string.Equals(context.PropertyName, propertyName, StringComparison.Ordinal);
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

    internal MainChartListRowsTransition PrepareRowsTransition(MainChartListRowsApplyRequest request)
    {
        return new MainChartListRowsTransition(this, PrepareRowsApply(request));
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

    internal MainChartListRowsCommit CommitPreparedRowsWithoutDisposal(MainChartListPreparedRowsApply prepared)
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

    internal void DisposeCommittedRows(MainChartListRowsCommit commit)
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

    internal MainChartListRowsApplyResult PublishRowsCommit(MainChartListRowsCommit commit)
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

    internal void CancelPreparedRowsApply(MainChartListPreparedRowsApply prepared)
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

internal sealed class MainChartListCellEditContext : EventArgs
{
    internal MainChartListCellEditContext(
        object row,
        string propertyName,
        ChartOperationSourceScope sourceScope,
        MainViewOperationSection operationSection)
    {
        Row = row;
        PropertyName = propertyName ?? string.Empty;
        SourceScope = sourceScope;
        OperationSection = operationSection;
    }

    internal object Row { get; }

    internal string PropertyName { get; }

    internal ChartOperationSourceScope SourceScope { get; }

    internal MainViewOperationSection OperationSection { get; }
}

internal sealed class MainChartListCellEditBeginningEventArgs : EventArgs
{
    internal MainChartListCellEditBeginningEventArgs(MainChartListCellEditContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    internal MainChartListCellEditContext Context { get; }

    internal bool Accepted { get; set; }
}

internal sealed class MainChartListCellEditEndedEventArgs : EventArgs
{
    internal MainChartListCellEditEndedEventArgs(MainChartListCellEditContext context, string text, bool commit)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Text = text ?? string.Empty;
        Commit = commit;
    }

    internal MainChartListCellEditContext Context { get; }

    internal string Text { get; }

    internal bool Commit { get; }
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
        MainChartListSortTarget target,
        long ownerRevision = 0L)
    {
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        Target = target;
        OwnerRevision = ownerRevision;
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

    /// <summary>
    /// Gets the owning workflow revision, or zero before the request reaches its owner.
    /// </summary>
    internal long OwnerRevision { get; }
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

internal sealed class MainChartListRowsTransition
{
    private readonly MainChartListViewModel owner;
    private readonly MainChartListPreparedRowsApply prepared;
    private MainChartListRowsCommit commit;
    private bool canceled;
    private bool completed;

    internal MainChartListRowsTransition(
        MainChartListViewModel owner,
        MainChartListPreparedRowsApply prepared)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
    }

    internal bool OwnershipTransferred => commit != null;

    internal void CommitOwnership()
    {
        ThrowIfFinished();
        if (commit != null)
        {
            throw new InvalidOperationException("The main chart-list rows transition already transferred ownership.");
        }
        commit = owner.CommitPreparedRowsWithoutDisposal(prepared);
    }

    internal void Cancel()
    {
        ThrowIfFinished();
        if (commit != null)
        {
            throw new InvalidOperationException("A committed main chart-list rows transition cannot be canceled.");
        }
        owner.CancelPreparedRowsApply(prepared);
        canceled = true;
    }

    internal MainChartListRowsApplyResult Complete()
    {
        ThrowIfFinished();
        if (commit == null)
        {
            throw new InvalidOperationException("The main chart-list rows transition must transfer ownership before completion.");
        }

        completed = true;
        MainChartListRowsApplyResult result = default;
        var exceptions = new List<Exception>();
        TryComplete(() => owner.DisposeCommittedRows(commit), exceptions);
        TryComplete(() => result = owner.PublishRowsCommit(commit), exceptions);
        if (exceptions.Count > 0)
        {
            throw new AggregateException("The main chart-list rows transition committed ownership but completion failed.", exceptions);
        }
        return result;
    }

    private void ThrowIfFinished()
    {
        if (canceled)
        {
            throw new InvalidOperationException("The main chart-list rows transition was canceled.");
        }
        if (completed)
        {
            throw new InvalidOperationException("The main chart-list rows transition was completed.");
        }
    }

    private static void TryComplete(Action action, ICollection<Exception> exceptions)
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
