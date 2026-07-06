using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Captures the normalized inputs for the materialized regular chart-list pipeline.
/// </summary>
internal readonly struct RegularChartListRefreshRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegularChartListRefreshRequest"/> struct.
    /// </summary>
    /// <param name="mode">Mode currently used by the regular pipeline.</param>
    /// <param name="requestedMode">Mode originally requested by the caller.</param>
    /// <param name="parameter">Parameter associated with the current mode.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <param name="treeParameter">Parameter associated with the current tree selection.</param>
    /// <param name="includeBmsonRows">Whether normal library rows include bmson-backed charts.</param>
    /// <param name="virtualSubsetRequiredFailure">Whether a virtual subset request could not be fulfilled.</param>
    /// <param name="keywordFilter">Keyword filter text captured for the request.</param>
    /// <param name="modeFilter">Mode filter captured for the request.</param>
    /// <param name="sortParameters">Sort parameters captured for the request.</param>
    internal RegularChartListRefreshRequest(
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        object parameter,
        MainViewUpdateMode currentTreeMode,
        object treeParameter,
        bool includeBmsonRows,
        bool virtualSubsetRequiredFailure,
        string keywordFilter,
        MainWindowViewModel.ModeFilterType modeFilter,
        MainWindowViewModel.cSortParameters sortParameters)
    {
        Mode = mode;
        RequestedMode = requestedMode;
        Parameter = parameter;
        CurrentTreeMode = currentTreeMode;
        TreeParameter = treeParameter;
        IncludeBmsonRows = includeBmsonRows;
        VirtualSubsetRequiredFailure = virtualSubsetRequiredFailure;
        KeywordFilter = keywordFilter ?? string.Empty;
        ModeFilter = modeFilter;
        SortColumnName = NormalizeSortColumnName(sortParameters);
        RequestedSortColumnName = sortParameters?.ColumnsName;
        SortDirection = sortParameters?.Direction ?? ListSortDirection.Ascending;
        HasSortParameters = sortParameters != null;
        SortParameters = sortParameters == null
            ? null
            : new MainWindowViewModel.cSortParameters
            {
                ColumnsName = sortParameters.ColumnsName,
                Direction = sortParameters.Direction
            };
    }

    /// <summary>
    /// Gets the mode currently used by the regular pipeline.
    /// </summary>
    internal MainViewUpdateMode Mode { get; }

    /// <summary>
    /// Gets the mode originally requested by the caller.
    /// </summary>
    internal MainViewUpdateMode RequestedMode { get; }

    /// <summary>
    /// Gets the parameter associated with <see cref="Mode"/>.
    /// </summary>
    internal object Parameter { get; }

    /// <summary>
    /// Gets the current tree selection mode.
    /// </summary>
    internal MainViewUpdateMode CurrentTreeMode { get; }

    /// <summary>
    /// Gets the parameter associated with the current tree selection.
    /// </summary>
    internal object TreeParameter { get; }

    /// <summary>
    /// Gets a value indicating whether bmson-backed rows are included.
    /// </summary>
    internal bool IncludeBmsonRows { get; }

    /// <summary>
    /// Gets a value indicating whether a required virtual subset request failed.
    /// </summary>
    internal bool VirtualSubsetRequiredFailure { get; }

    /// <summary>
    /// Gets the keyword filter captured for the request.
    /// </summary>
    internal string KeywordFilter { get; }

    /// <summary>
    /// Gets the mode filter captured for the request.
    /// </summary>
    internal MainWindowViewModel.ModeFilterType ModeFilter { get; }

    /// <summary>
    /// Gets a value indicating whether explicit sort parameters were supplied.
    /// </summary>
    internal bool HasSortParameters { get; }

    /// <summary>
    /// Gets the normalized sort column name captured for diagnostics and later coordinator extraction.
    /// </summary>
    internal string SortColumnName { get; }

    /// <summary>
    /// Gets the raw requested sort column name for diagnostics that must preserve the caller-facing value.
    /// </summary>
    internal string RequestedSortColumnName { get; }

    /// <summary>
    /// Gets the requested sort direction.
    /// </summary>
    internal ListSortDirection SortDirection { get; }

    /// <summary>
    /// Gets a cloned sort parameter snapshot for the regular pipeline.
    /// </summary>
    internal MainWindowViewModel.cSortParameters SortParameters { get; }

    private static string NormalizeSortColumnName(MainWindowViewModel.cSortParameters sortParameters)
    {
        string columnName = sortParameters?.ColumnsName;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return nameof(LibraryChartRow.Title);
        }
        if (string.Equals(columnName, nameof(LibraryChartRow.rank), StringComparison.Ordinal))
        {
            return nameof(LibraryChartRow.rateDouble);
        }
        return columnName;
    }
}

/// <summary>
/// Carries the mutable row stages produced by the materialized regular chart-list pipeline.
/// </summary>
internal sealed class RegularChartListStageState
{
    /// <summary>
    /// Gets or sets the rows after the folder/tree stage.
    /// </summary>
    internal IReadOnlyList<LibraryChartRow> FolderRows { get; set; } = Array.Empty<LibraryChartRow>();

    /// <summary>
    /// Gets or sets the rows after the keyword stage.
    /// </summary>
    internal IReadOnlyList<LibraryChartRow> KeywordRows { get; set; } = Array.Empty<LibraryChartRow>();

    /// <summary>
    /// Gets or sets the rows after the mode filter stage.
    /// </summary>
    internal IReadOnlyList<LibraryChartRow> ModeRows { get; set; } = Array.Empty<LibraryChartRow>();

    /// <summary>
    /// Gets the number of rows after the folder/tree stage.
    /// </summary>
    internal int FolderCount => FolderRows?.Count ?? 0;

    /// <summary>
    /// Gets the number of rows after the keyword stage.
    /// </summary>
    internal int KeywordCount => KeywordRows?.Count ?? 0;

    /// <summary>
    /// Gets the number of rows after the mode filter stage.
    /// </summary>
    internal int ModeCount => ModeRows?.Count ?? 0;

    /// <summary>
    /// Creates a stage-state snapshot from the legacy root-owned row caches.
    /// </summary>
    /// <param name="folderRows">Rows after the folder/tree stage.</param>
    /// <param name="keywordRows">Rows after the keyword stage.</param>
    /// <param name="modeRows">Rows after the mode filter stage.</param>
    /// <returns>A stage-state object that exposes materialized rows and counts.</returns>
    internal static RegularChartListStageState FromCaches(
        IEnumerable<LibraryChartRow> folderRows,
        IEnumerable<LibraryChartRow> keywordRows,
        IEnumerable<LibraryChartRow> modeRows)
    {
        return new RegularChartListStageState
        {
            FolderRows = Materialize(folderRows),
            KeywordRows = Materialize(keywordRows),
            ModeRows = Materialize(modeRows),
        };
    }

    /// <summary>
    /// Converts a row stage to a stable list while preserving existing list instances when possible.
    /// </summary>
    /// <param name="rows">Rows produced by a regular chart-list stage.</param>
    /// <returns>A stable read-only list for downstream stages.</returns>
    internal static List<LibraryChartRow> Materialize(IEnumerable<LibraryChartRow> rows)
    {
        if (rows == null)
        {
            return new List<LibraryChartRow>();
        }
        if (rows is List<LibraryChartRow> list)
        {
            return list;
        }
        return rows.ToList();
    }
}
