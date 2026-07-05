using System;
using System.Collections;
using System.Collections.Generic;
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
