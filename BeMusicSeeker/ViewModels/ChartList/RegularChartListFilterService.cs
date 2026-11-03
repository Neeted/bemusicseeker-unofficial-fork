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
    /// 通常行へ共通のモード判定を適用し、入力順を保ちます。Allでは未列挙値も通します。
    /// </summary>
    /// <param name="sourceRows">キーワード絞込み後の行。</param>
    /// <param name="modeFilter">選択した一覧モード。</param>
    /// <returns>選択したモードとnullの行。Allでは元の行集合。</returns>
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

        return sourceRows.Where(row => modeFilter.Matches(row.mode));
    }

    /// <summary>通常一覧の差分判定用に、選択した既知モードとnullの集合を作ります。Allの全通過は呼出し元で判定します。</summary>
    /// <param name="modeFilter">選択した一覧モード。</param>
    /// <returns>共通判定に一致する既知の一覧モードとnull。</returns>
    internal static HashSet<int?> CreateModeFilterValueSet(ChartModeFilter modeFilter)
    {
        HashSet<int?> modeValues = [null];
        foreach (int mode in new[] { 5, 7, 9, 10, 14, 24, 48 })
        {
            if (modeFilter.Matches(mode)) { modeValues.Add(mode); }
        }
        return modeValues;
    }
}
