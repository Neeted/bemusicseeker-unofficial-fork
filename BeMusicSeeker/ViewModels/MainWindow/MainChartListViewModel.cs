using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns main chart-list binding state while the shell keeps refresh orchestration during the transition.
/// </summary>
public sealed class MainChartListViewModel : ViewModel
{
    private IList rows = new List<object>();

    private int selectedIndex;

    private CustomTableColumnSettings columnsSettings;

    private string summaryText = string.Empty;

    /// <summary>
    /// Raised immediately before a different row collection replaces the active main-table rows.
    /// </summary>
    internal event EventHandler RowsReplacing;

    internal void PrepareRowsReplacement()
    {
        RowsReplacing?.Invoke(this, EventArgs.Empty);
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
        if (rowsChanged && !request.RowsAlreadyPrepared)
        {
            long prepareStartMs = request.Stopwatch.ElapsedMilliseconds;
            PrepareRowsReplacement();
            prepareSwapMs = request.Stopwatch.ElapsedMilliseconds - prepareStartMs;
        }

        long columnSettingStartMs = request.Stopwatch.ElapsedMilliseconds;
        long columnSettingMs = request.ColumnPreparationMs
            + request.Stopwatch.ElapsedMilliseconds
            - columnSettingStartMs;

        long setViewStartMs = request.Stopwatch.ElapsedMilliseconds;
        if (rowsChanged)
        {
            DisposeRows(rows);
            rows = request.Rows;
        }
        columnsSettings = request.ColumnsSettings;
        selectedIndex = nextSelectedIndex;
        summaryText = nextSummaryText;

        long columnNotificationMs = 0L;
        if (columnsChanged)
        {
            long columnNotificationStartMs = request.Stopwatch.ElapsedMilliseconds;
            RaisePropertyChanged(nameof(ColumnsSettings));
            RaisePropertyChanged(nameof(RowDragKind));
            columnNotificationMs = request.Stopwatch.ElapsedMilliseconds - columnNotificationStartMs;
            columnSettingMs += columnNotificationMs;
        }
        if (rowsChanged)
        {
            RaisePropertyChanged(nameof(Rows));
        }
        if (summaryChanged)
        {
            RaisePropertyChanged(nameof(SummaryText));
        }
        if (selectionChanged)
        {
            RaisePropertyChanged(nameof(SelectedIndex));
        }

        long setViewMs = request.Stopwatch.ElapsedMilliseconds - setViewStartMs - columnNotificationMs;
        long columnStageMs = request.Stopwatch.ElapsedMilliseconds - request.TerminalStageStartMs;
        return new MainChartListRowsApplyResult(
            prepareSwapMs,
            columnSettingMs,
            setViewMs,
            columnStageMs,
            request.ColumnSettingReuse);
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

    /// <summary>
    /// Resolves row drag behavior for tests that still verify the legacy root property.
    /// </summary>
    /// <param name="settings">Column settings that influence row behavior.</param>
    /// <returns>Row drag behavior used by the main chart table.</returns>
    internal static CustomTableRowDragKind ResolveRowDragKindForTest(CustomTableColumnSettings settings)
    {
        return ResolveRowDragKind(settings);
    }

    /// <summary>
    /// Formats a normal chart-list summary for tests that still verify the legacy root helper.
    /// </summary>
    /// <param name="rowCount">Visible chart count.</param>
    /// <param name="distinctFolderCount">Visible distinct-folder count.</param>
    /// <returns>Formatted summary text.</returns>
    internal static string FormatSummaryTextForTest(int rowCount, int distinctFolderCount)
    {
        return FormatSummaryText(rowCount, distinctFolderCount);
    }

    private static CustomTableRowDragKind ResolveRowDragKind(CustomTableColumnSettings settings)
    {
        return CustomTableRowDragKind.PlaylistDropCandidateRows;
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
