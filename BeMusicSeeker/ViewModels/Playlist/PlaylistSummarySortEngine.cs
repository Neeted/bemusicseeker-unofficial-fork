using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// PlaylistSummary 一覧のソート処理を集約します。
/// </summary>
internal static class PlaylistSummarySortEngine
{
    /// <summary>
    /// 指定条件で PlaylistSummary 行をソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。</param>
    /// <param name="sortProfile">適用したソートプロファイル。</param>
    /// <returns>ソート済みリスト。</returns>
    internal static List<PlaylistSummaryRow> Sort(IEnumerable<PlaylistSummaryRow> source, ChartListSortParameters sortParameters, out string sortProfile)
    {
        IEnumerable<PlaylistSummaryRow> safeSource = source ?? [];
        string columnName = sortParameters?.ColumnsName;
        ListSortDirection direction = sortParameters?.Direction ?? ListSortDirection.Ascending;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = nameof(PlaylistSummaryRow.Name);
        }

        switch (columnName)
        {
            case nameof(PlaylistSummaryRow.PlaylistId):
                sortProfile = "playlist_summary_numeric_int32";
                return SortByTypedKey(safeSource, row => row?.PlaylistId, direction);
            case nameof(PlaylistSummaryRow.Name):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return SortByString(safeSource, row => row?.Name ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.FolderName):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return SortByString(safeSource, row => row?.FolderName ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.OutputBaseDisplayName):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return SortByString(safeSource, row => row?.OutputBaseDisplayName ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.CompatPrefix):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return SortByString(safeSource, row => row?.CompatPrefix ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.Symbol):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return SortByString(safeSource, row => row?.Symbol ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.HeaderUriText):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return SortByString(safeSource, row => row?.HeaderUriText ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.DataUriText):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return SortByString(safeSource, row => row?.DataUriText ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.LastUpdate):
                sortProfile = "playlist_summary_date";
                return SortByTypedKey(safeSource, row => row?.LastUpdate ?? DateTime.MinValue, direction);
            case nameof(PlaylistSummaryRow.TotalCharts):
                sortProfile = "playlist_summary_numeric_int32";
                return SortByTypedKey(safeSource, row => row?.TotalCharts ?? 0, direction);
            case nameof(PlaylistSummaryRow.OwnedCharts):
                sortProfile = "playlist_summary_numeric_int32";
                return SortByTypedKey(safeSource, row => row?.OwnedCharts ?? 0, direction);
            case nameof(PlaylistSummaryRow.MissingCharts):
                sortProfile = "playlist_summary_numeric_int32";
                return SortByTypedKey(safeSource, row => row?.MissingCharts ?? 0, direction);
            case nameof(PlaylistSummaryRow.OwnedRatio):
                sortProfile = "playlist_summary_numeric_double";
                return SortByTypedKey(safeSource, row => row?.OwnedRatio ?? 0.0, direction);
            case nameof(PlaylistSummaryRow.IsExternalSync):
                sortProfile = "playlist_summary_numeric_bool";
                return SortByTypedKey(safeSource, row => row?.IsExternalSync ?? false, direction);
            case nameof(PlaylistSummaryRow.StatusSortOrder):
                sortProfile = "playlist_summary_numeric_int32";
                return SortByTypedKey(safeSource, row => row?.StatusSortOrder ?? int.MaxValue, direction);
            case nameof(PlaylistSummaryRow.IsRootFolder):
                sortProfile = "playlist_summary_numeric_bool";
                return SortByTypedKey(safeSource, row => row?.IsRootFolder ?? false, direction);
            case nameof(PlaylistSummaryRow.BmtSort):
                sortProfile = "playlist_summary_numeric_int32";
                return SortByTypedKey(safeSource, row => row?.BmtSort ?? int.MaxValue, direction);
            case nameof(PlaylistSummaryRow.IsBmtOutput):
                sortProfile = "playlist_summary_numeric_bool";
                return SortByTypedKey(safeSource, row => row?.IsBmtOutput ?? false, direction);
            default:
                sortProfile = "playlist_summary_string_fast_fallback";
                return SortByString(safeSource, row => row?.Name ?? string.Empty, direction);
        }
    }

    /// <summary>
    /// 文字列キーでソートします。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="keySelector">主キー取得関数。</param>
    /// <param name="direction">ソート方向。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<PlaylistSummaryRow> SortByString(IEnumerable<PlaylistSummaryRow> source, Func<PlaylistSummaryRow, string> keySelector, ListSortDirection direction)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(keySelector, comparer).ThenBy(GetTieBreakName, comparer).ThenBy(GetTieBreakPlaylistId)];
        }
        return [.. source.OrderByDescending(keySelector, comparer).ThenBy(GetTieBreakName, comparer).ThenBy(GetTieBreakPlaylistId)];
    }

    /// <summary>
    /// 型付きキーでソートします。
    /// </summary>
    /// <typeparam name="TKey">比較キー型。</typeparam>
    /// <param name="source">ソート対象。</param>
    /// <param name="keySelector">主キー取得関数。</param>
    /// <param name="direction">ソート方向。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<PlaylistSummaryRow> SortByTypedKey<TKey>(IEnumerable<PlaylistSummaryRow> source, Func<PlaylistSummaryRow, TKey> keySelector, ListSortDirection direction)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        if (direction == ListSortDirection.Ascending)
        {
            return [.. source.OrderBy(keySelector).ThenBy(GetTieBreakName, comparer).ThenBy(GetTieBreakPlaylistId)];
        }
        return [.. source.OrderByDescending(keySelector).ThenBy(GetTieBreakName, comparer).ThenBy(GetTieBreakPlaylistId)];
    }

    /// <summary>
    /// 文字列タイブレーク用の NAME キーを返します。
    /// </summary>
    /// <param name="row">対象行。</param>
    /// <returns>NAME キー。</returns>
    private static string GetTieBreakName(PlaylistSummaryRow row)
    {
        return row?.Name ?? string.Empty;
    }

    /// <summary>
    /// 最終タイブレーク用の PlaylistId キーを返します。
    /// </summary>
    /// <param name="row">対象行。</param>
    /// <returns>PlaylistId キー。</returns>
    private static int GetTieBreakPlaylistId(PlaylistSummaryRow row)
    {
        return row?.PlaylistId ?? int.MinValue;
    }
}
