using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal static class PlayHistorySortEngine
{
    private static readonly Dictionary<string, string> ColumnAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DATE"] = nameof(PlayHistoryRow.PlayedAt),
        ["PlayedAt"] = nameof(PlayHistoryRow.PlayedAt),
        ["FolderLabels"] = nameof(PlayHistoryRow.FolderLabels),
        ["FOLDER"] = nameof(PlayHistoryRow.FolderLabels),
        ["Title"] = nameof(PlayHistoryRow.Title),
        ["TITLE"] = nameof(PlayHistoryRow.Title),
        ["Artist"] = nameof(PlayHistoryRow.Artist),
        ["ARTIST"] = nameof(PlayHistoryRow.Artist),
        ["BestClear"] = nameof(PlayHistoryRow.NewBestClear),
        ["CLEAR"] = nameof(PlayHistoryRow.NewBestClear),
        ["BestDjLevel"] = nameof(PlayHistoryRow.BestRate),
        ["BEST DJ"] = nameof(PlayHistoryRow.BestRate),
        ["BestRate"] = nameof(PlayHistoryRow.BestRate),
        ["BEST RATE"] = nameof(PlayHistoryRow.BestRate),
        ["BestBp"] = nameof(PlayHistoryRow.NewBestBp),
        ["BP"] = nameof(PlayHistoryRow.NewBestBp),
        ["BestCombo"] = nameof(PlayHistoryRow.NewBestCombo),
        ["COMBO"] = nameof(PlayHistoryRow.NewBestCombo),
        ["Kind"] = nameof(PlayHistoryRow.Kind),
        ["TYPE"] = nameof(PlayHistoryRow.Kind),
        ["OpHistory"] = nameof(PlayHistoryRow.OpHistoryNewBits),
        ["OP HISTORY"] = nameof(PlayHistoryRow.OpHistoryNewBits),
        ["BestExscore"] = nameof(PlayHistoryRow.NewBestExscore),
        ["BEST EXSCORE"] = nameof(PlayHistoryRow.NewBestExscore),
        ["PlayExscore"] = nameof(PlayHistoryRow.PlayExscore),
        ["PLAY EXSCORE"] = nameof(PlayHistoryRow.PlayExscore),
        ["JudgeTotal"] = nameof(PlayHistoryRow.JudgeTotal),
        ["JUDGES"] = nameof(PlayHistoryRow.JudgeTotal),
        ["Option"] = nameof(PlayHistoryRow.Option),
        ["OPTION"] = nameof(PlayHistoryRow.Option),
        ["Sha256"] = nameof(PlayHistoryRow.Sha256),
        ["SHA256"] = nameof(PlayHistoryRow.Sha256),
        ["Provider"] = nameof(PlayHistoryRow.Provider),
        ["PROVIDER"] = nameof(PlayHistoryRow.Provider),
        ["Source"] = nameof(PlayHistoryRow.Source),
        ["SOURCE"] = nameof(PlayHistoryRow.Source),
        ["RawHash"] = nameof(PlayHistoryRow.RawHash),
        ["RAW HASH"] = nameof(PlayHistoryRow.RawHash),
        ["Finalized"] = nameof(PlayHistoryRow.Finalized),
        ["FINALIZED"] = nameof(PlayHistoryRow.Finalized)
    };

    internal static bool TryNormalizeSortColumn(string columnName, out string normalizedColumnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            normalizedColumnName = nameof(PlayHistoryRow.PlayedAt);
            return true;
        }
        return ColumnAliases.TryGetValue(columnName.Trim(), out normalizedColumnName);
    }

    internal static bool TrySort(
        IEnumerable<PlayHistoryRow> source,
        ChartListSortParameters sortParameters,
        out List<PlayHistoryRow> sortedRows,
        out string sortProfile)
    {
        List<PlayHistoryRow> safeSource = [.. source ?? []];
        sortedRows = safeSource;
        sortProfile = string.Empty;
        if (!TryNormalizeSortColumn(sortParameters?.ColumnsName, out string columnName))
        {
            sortProfile = "play_history_unknown_column";
            return false;
        }

        ListSortDirection direction = sortParameters?.Direction
            ?? (columnName == nameof(PlayHistoryRow.PlayedAt) ? ListSortDirection.Descending : ListSortDirection.Ascending);
        sortedRows = SortByColumn(safeSource, columnName, direction);
        sortProfile = "play_history_" + columnName + "_" + (direction == ListSortDirection.Ascending ? "asc" : "desc");
        return true;
    }

    private static List<PlayHistoryRow> SortByColumn(List<PlayHistoryRow> source, string columnName, ListSortDirection direction)
    {
        return columnName switch
        {
            nameof(PlayHistoryRow.PlayedAt) => SortByPlayedAt(source, direction),
            nameof(PlayHistoryRow.FolderLabels) => SortString(source, row => row?.FolderLabels, direction),
            nameof(PlayHistoryRow.Title) => SortString(source, row => row?.Title, direction),
            nameof(PlayHistoryRow.Artist) => SortString(source, row => row?.Artist, direction),
            nameof(PlayHistoryRow.NewBestClear) => Sort(source, row => row?.NewBestClear.HasValue == true ? (int?)row.NewBestClear.Value : null, direction),
            nameof(PlayHistoryRow.BestRate) => Sort(source, row => row?.BestRate, direction),
            nameof(PlayHistoryRow.NewBestBp) => Sort(source, row => row?.NewBestBp, direction),
            nameof(PlayHistoryRow.NewBestCombo) => Sort(source, row => row?.NewBestCombo, direction),
            nameof(PlayHistoryRow.Kind) => SortString(source, row => row?.Kind, direction),
            nameof(PlayHistoryRow.OpHistoryNewBits) => Sort(source, row => row?.OpHistoryNewBits, direction),
            nameof(PlayHistoryRow.NewBestExscore) => Sort(source, row => row?.BestScoreUpdated == true ? row.NewBestExscore : null, direction),
            nameof(PlayHistoryRow.PlayExscore) => Sort(source, row => row?.PlayExscore, direction),
            nameof(PlayHistoryRow.JudgeTotal) => Sort(source, row => row?.JudgeTotal, direction),
            nameof(PlayHistoryRow.Option) => SortString(source, row => row?.Option, direction),
            nameof(PlayHistoryRow.Sha256) => SortString(source, row => row?.Sha256, direction),
            nameof(PlayHistoryRow.Provider) => Sort(source, row => row?.Provider, direction),
            nameof(PlayHistoryRow.Source) => SortString(source, row => row?.Source, direction),
            nameof(PlayHistoryRow.RawHash) => SortString(source, row => row?.RawHash, direction),
            nameof(PlayHistoryRow.Finalized) => Sort(source, row => row?.Finalized, direction),
            _ => source
        };
    }

    private static List<PlayHistoryRow> Sort<TKey>(IEnumerable<PlayHistoryRow> source, Func<PlayHistoryRow, TKey> keySelector, ListSortDirection direction)
    {
        StringComparer titleComparer = StringComparer.OrdinalIgnoreCase;
        return direction == ListSortDirection.Ascending
            ? [.. source.OrderBy(keySelector, Comparer<TKey>.Default).ThenBy(row => row?.Title ?? string.Empty, titleComparer)]
            : [.. source.OrderByDescending(keySelector, Comparer<TKey>.Default).ThenBy(row => row?.Title ?? string.Empty, titleComparer)];
    }

    private static List<PlayHistoryRow> SortByPlayedAt(IEnumerable<PlayHistoryRow> source, ListSortDirection direction)
    {
        return direction == ListSortDirection.Ascending
            ? [.. source.OrderBy(row => row?.PlayedAtUnix ?? 0L).ThenBy(row => row?.HistoryId ?? 0L)]
            : [.. source.OrderByDescending(row => row?.PlayedAtUnix ?? 0L).ThenByDescending(row => row?.HistoryId ?? 0L)];
    }

    private static List<PlayHistoryRow> SortString(IEnumerable<PlayHistoryRow> source, Func<PlayHistoryRow, string> keySelector, ListSortDirection direction)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        return direction == ListSortDirection.Ascending
            ? [.. source.OrderBy(row => keySelector(row) ?? string.Empty, comparer).ThenBy(row => row?.PlayedAtUnix ?? 0L)]
            : [.. source.OrderByDescending(row => keySelector(row) ?? string.Empty, comparer).ThenByDescending(row => row?.PlayedAtUnix ?? 0L)];
    }
}
