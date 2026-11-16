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
        IComparer<PlaylistSummaryRow> comparer = CreateComparer(sortParameters, out sortProfile);
        return [.. (source ?? []).OrderBy(row => row, comparer)];
    }

    /// <summary>全体整列と局所位置調整で同じ主キー方向・昇順Name/IDの比較を使います。</summary>
    internal static IComparer<PlaylistSummaryRow> CreateComparer(ChartListSortParameters sortParameters, out string sortProfile)
    {
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
                return CompareByKey(row => row?.PlaylistId, direction);
            case nameof(PlaylistSummaryRow.Name):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return CompareByString(row => row?.Name ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.FolderName):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return CompareByString(row => row?.FolderName ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.OutputBaseDisplayName):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return CompareByString(row => row?.OutputBaseDisplayName ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.CompatPrefix):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return CompareByString(row => row?.CompatPrefix ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.Symbol):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return CompareByString(row => row?.Symbol ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.HeaderUriText):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return CompareByString(row => row?.HeaderUriText ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.DataUriText):
                sortProfile = "playlist_summary_string_fast_ordinal_ignore_case";
                return CompareByString(row => row?.DataUriText ?? string.Empty, direction);
            case nameof(PlaylistSummaryRow.LastUpdate):
                sortProfile = "playlist_summary_date";
                return CompareByKey(row => row?.LastUpdate ?? DateTime.MinValue, direction);
            case nameof(PlaylistSummaryRow.TotalCharts):
                sortProfile = "playlist_summary_numeric_int32";
                return CompareByKey(row => row?.TotalCharts ?? 0, direction);
            case nameof(PlaylistSummaryRow.OwnedCharts):
                sortProfile = "playlist_summary_numeric_int32";
                return CompareByKey(row => row?.OwnedCharts ?? 0, direction);
            case nameof(PlaylistSummaryRow.MissingCharts):
                sortProfile = "playlist_summary_numeric_int32";
                return CompareByKey(row => row?.MissingCharts ?? 0, direction);
            case nameof(PlaylistSummaryRow.OwnedRatio):
                sortProfile = "playlist_summary_numeric_double";
                return CompareByKey(row => row?.OwnedRatio ?? 0.0, direction);
            case nameof(PlaylistSummaryRow.IsExternalSync):
                sortProfile = "playlist_summary_numeric_bool";
                return CompareByKey(row => row?.IsExternalSync ?? false, direction);
            case nameof(PlaylistSummaryRow.StatusSortOrder):
                sortProfile = "playlist_summary_numeric_int32";
                return CompareByKey(row => row?.StatusSortOrder ?? int.MaxValue, direction);
            case nameof(PlaylistSummaryRow.IsRootFolder):
                sortProfile = "playlist_summary_numeric_bool";
                return CompareByKey(row => row?.IsRootFolder ?? false, direction);
            case nameof(PlaylistSummaryRow.BmtSort):
                sortProfile = "playlist_summary_numeric_int32";
                return CompareByKey(row => row?.BmtSort ?? int.MaxValue, direction);
            case nameof(PlaylistSummaryRow.IsBmtOutput):
                sortProfile = "playlist_summary_numeric_bool";
                return CompareByKey(row => row?.IsBmtOutput ?? false, direction);
            default:
                sortProfile = "playlist_summary_string_fast_fallback";
                return CompareByString(row => row?.Name ?? string.Empty, direction);
        }
    }

    private static IComparer<PlaylistSummaryRow> CompareByString(Func<PlaylistSummaryRow, string> selector, ListSortDirection direction)
        => CompareByKey(selector, direction, StringComparer.OrdinalIgnoreCase);

    private static IComparer<PlaylistSummaryRow> CompareByKey<TKey>(Func<PlaylistSummaryRow, TKey> selector,
        ListSortDirection direction, IComparer<TKey> comparer = null)
    {
        comparer ??= Comparer<TKey>.Default;
        return Comparer<PlaylistSummaryRow>.Create((left, right) =>
        {
            int primary = direction == ListSortDirection.Ascending
                ? comparer.Compare(selector(left), selector(right))
                : comparer.Compare(selector(right), selector(left));
            if (primary != 0) { return primary; }
            int name = StringComparer.OrdinalIgnoreCase.Compare(left?.Name ?? string.Empty, right?.Name ?? string.Empty);
            return name != 0 ? name : (left?.PlaylistId ?? int.MinValue).CompareTo(right?.PlaylistId ?? int.MinValue);
        });
    }
}
