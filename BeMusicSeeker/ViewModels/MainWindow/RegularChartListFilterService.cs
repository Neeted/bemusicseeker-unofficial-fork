using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Applies pure keyword and play-mode filters for the materialized regular chart-list pipeline.
/// </summary>
internal static class RegularChartListFilterService
{
    /// <summary>
    /// Applies the main chart-list keyword filter to regular chart rows.
    /// </summary>
    /// <param name="sourceRows">Rows produced by the folder stage.</param>
    /// <param name="keywordFilter">Keyword filter text.</param>
    /// <returns>Rows that match the keyword query, or the original rows when the filter is empty.</returns>
    internal static IEnumerable<LibraryChartRow> ApplyKeywordFilter(IEnumerable<LibraryChartRow> sourceRows, string keywordFilter)
    {
        if (sourceRows == null)
        {
            throw new ArgumentNullException(nameof(sourceRows));
        }
        if (string.IsNullOrWhiteSpace(keywordFilter))
        {
            return sourceRows;
        }

        var query = GridKeywordSearchQuery.Parse(keywordFilter);
        return from row in sourceRows.AsParallel()
               where query.MatchesLibraryChartRow(row)
               select row;
    }

    /// <summary>
    /// Applies the selected play-mode filter to regular chart rows.
    /// </summary>
    /// <param name="sourceRows">Rows produced by the keyword stage.</param>
    /// <param name="modeFilter">Selected play-mode filter.</param>
    /// <returns>Rows that match the selected play modes, or the original rows when all modes are allowed.</returns>
    internal static IEnumerable<LibraryChartRow> ApplyModeFilter(IEnumerable<LibraryChartRow> sourceRows, ChartModeFilter modeFilter)
    {
        if (sourceRows == null)
        {
            throw new ArgumentNullException(nameof(sourceRows));
        }
        if (modeFilter == ChartModeFilter.All)
        {
            return sourceRows;
        }

        HashSet<int?> modeValues = CreateModeFilterValueSet(modeFilter);
        return sourceRows.Where(row => modeValues.Contains(row.mode));
    }

    /// <summary>
    /// Creates the set of row mode values accepted by a mode filter.
    /// </summary>
    /// <param name="modeFilter">Selected play-mode filter.</param>
    /// <returns>Accepted mode values. Null is always included to preserve legacy unknown-mode behavior.</returns>
    internal static HashSet<int?> CreateModeFilterValueSet(ChartModeFilter modeFilter)
    {
        HashSet<int?> modeValues = [null];
        if ((modeFilter & ChartModeFilter._5KEYS) == ChartModeFilter._5KEYS)
        {
            modeValues.Add(5);
        }
        if ((modeFilter & ChartModeFilter._7KEYS) == ChartModeFilter._7KEYS)
        {
            modeValues.Add(7);
        }
        if ((modeFilter & ChartModeFilter._9KEYS) == ChartModeFilter._9KEYS)
        {
            modeValues.Add(9);
        }
        if ((modeFilter & ChartModeFilter._10KEYS) == ChartModeFilter._10KEYS)
        {
            modeValues.Add(10);
        }
        if ((modeFilter & ChartModeFilter._14KEYS) == ChartModeFilter._14KEYS)
        {
            modeValues.Add(14);
        }
        return modeValues;
    }
}
