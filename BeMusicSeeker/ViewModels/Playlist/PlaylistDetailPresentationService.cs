using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Builds playlist detail presentation rows from source snapshots without depending on the root shell ViewModel.
/// </summary>
internal static class PlaylistDetailPresentationService
{
    /// <summary>元行へ検索・共通モード判定・整列を適用し、各段階の件数と所要時間を返します。</summary>
    internal static List<PlaylistDetailSourceRow> ApplySourceRows(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        string keywordFilter,
        ChartModeFilter modeFilter,
        ChartListSortParameters sortParameters,
        out string sortProfile,
        out int keywordCount,
        out int modeCount,
        out long keywordStageMs,
        out long modeStageMs,
        out long sortStageMs)
    {
        var stageStopwatch = Stopwatch.StartNew();
        IReadOnlyList<PlaylistDetailSourceRow> effectiveSourceRows = sourceRows ?? [];
        List<PlaylistDetailSourceRow> keywordRows;
        if (!string.IsNullOrWhiteSpace(keywordFilter))
        {
            var query = GridKeywordSearchQuery.Parse(keywordFilter);
            keywordRows = effectiveSourceRows.AsParallel().Where(row => query.MatchesPlaylistDetail(row)).ToList();
        }
        else
        {
            keywordRows = (effectiveSourceRows as List<PlaylistDetailSourceRow>) ?? [.. effectiveSourceRows];
        }
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;
        keywordCount = keywordRows.Count;

        stageStopwatch.Restart();
        List<PlaylistDetailSourceRow> modeRows;
        if (modeFilter != ChartModeFilter.All)
        {
            modeRows = [.. keywordRows.Where(row => modeFilter.Matches(row.mode))];
        }
        else
        {
            modeRows = keywordRows;
        }
        modeStageMs = stageStopwatch.ElapsedMilliseconds;
        modeCount = modeRows.Count;

        stageStopwatch.Restart();
        List<PlaylistDetailSourceRow> sortedSourceRows = PlaylistDetailSortEngine.Sort(modeRows, sortParameters, out sortProfile);
        sortStageMs = stageStopwatch.ElapsedMilliseconds;
        return sortedSourceRows;
    }

    internal static List<PlaylistDetailRow> ApplyViewFromSource(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        string keywordFilter,
        ChartModeFilter modeFilter,
        ChartListSortParameters sortParameters,
        out string sortProfile,
        out int keywordCount,
        out int modeCount,
        out long keywordStageMs,
        out long modeStageMs,
        out long sortStageMs,
        out long viewMaterializeMs)
    {
        List<PlaylistDetailSourceRow> sortedSourceRows = ApplySourceRows(
            sourceRows,
            keywordFilter,
            modeFilter,
            sortParameters,
            out sortProfile,
            out keywordCount,
            out modeCount,
            out keywordStageMs,
            out modeStageMs,
            out sortStageMs);
        var stageStopwatch = Stopwatch.StartNew();
        List<PlaylistDetailRow> viewRows = CreateViewRowsFromSource(sortedSourceRows);
        viewMaterializeMs = stageStopwatch.ElapsedMilliseconds;
        return viewRows;
    }

    /// <summary>初回または明示条件変更の全範囲を評価し、確定identityと局所順序観測先を仮想表示へ接続します。</summary>
    internal static PlaylistDetailVirtualView ApplyVirtualViewFromSource(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        string keywordFilter,
        ChartModeFilter modeFilter,
        ChartListSortParameters sortParameters,
        out string sortProfile,
        out int keywordCount,
        out int modeCount,
        out long keywordStageMs,
        out long modeStageMs,
        out long sortStageMs,
        out long viewMaterializeMs,
        bool onlyNotOwned = false, System.Action<string> observeWork = null, PlaylistRequestIdentity? identity = null)
    {
        IReadOnlyList<PlaylistDetailSourceRow> visibleInput = onlyNotOwned ? [.. sourceRows.Where(row => !row.IsOwned)] : sourceRows;
        List<PlaylistDetailSourceRow> sortedSourceRows = ApplySourceRows(
            visibleInput,
            keywordFilter,
            modeFilter,
            sortParameters,
            out sortProfile,
            out keywordCount,
            out modeCount,
            out keywordStageMs,
            out modeStageMs,
            out sortStageMs);
        var stageStopwatch = Stopwatch.StartNew();
        var viewRows = new PlaylistDetailVirtualView(
            sortedSourceRows,
            CountDistinctFolders(sortedSourceRows), observeWork, identity);
        viewMaterializeMs = stageStopwatch.ElapsedMilliseconds;
        return viewRows;
    }

    private static List<PlaylistDetailRow> CreateViewRowsFromSource(IEnumerable<PlaylistDetailSourceRow> rows)
    {
        if (rows == null)
        {
            return [];
        }
        List<PlaylistDetailRow> clones = [];
        foreach (PlaylistDetailSourceRow row in rows)
        {
            PlaylistDetailRow playlistRow = row?.CreateViewRow();
            if (playlistRow != null)
            {
                clones.Add(playlistRow);
            }
        }
        return clones;
    }

    private static int CountDistinctFolders(IEnumerable<PlaylistDetailSourceRow> rows)
    {
        if (rows == null)
        {
            return -1;
        }
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row?.Folder))
            .Select(row => row.Folder)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .Count();
    }
}
