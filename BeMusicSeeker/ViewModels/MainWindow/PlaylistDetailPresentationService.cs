using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Builds playlist detail presentation rows from source snapshots without depending on the root shell ViewModel.
/// </summary>
internal static class PlaylistDetailPresentationService
{
    internal static List<PlaylistDetailSourceRow> ApplySourceRows(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        string keywordFilter,
        MainWindowViewModel.ModeFilterType modeFilter,
        MainWindowViewModel.cSortParameters sortParameters,
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
        if (modeFilter != MainWindowViewModel.ModeFilterType.All)
        {
            List<int?> modeFlag = [null];
            if ((modeFilter & MainWindowViewModel.ModeFilterType._5KEYS) == MainWindowViewModel.ModeFilterType._5KEYS)
            {
                modeFlag.Add(5);
            }
            if ((modeFilter & MainWindowViewModel.ModeFilterType._7KEYS) == MainWindowViewModel.ModeFilterType._7KEYS)
            {
                modeFlag.Add(7);
            }
            if ((modeFilter & MainWindowViewModel.ModeFilterType._9KEYS) == MainWindowViewModel.ModeFilterType._9KEYS)
            {
                modeFlag.Add(9);
            }
            if ((modeFilter & MainWindowViewModel.ModeFilterType._10KEYS) == MainWindowViewModel.ModeFilterType._10KEYS)
            {
                modeFlag.Add(10);
            }
            if ((modeFilter & MainWindowViewModel.ModeFilterType._14KEYS) == MainWindowViewModel.ModeFilterType._14KEYS)
            {
                modeFlag.Add(14);
            }
            modeRows = [.. keywordRows.Where(file => modeFlag.Contains(file.mode))];
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
        MainWindowViewModel.ModeFilterType modeFilter,
        MainWindowViewModel.cSortParameters sortParameters,
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

    internal static PlaylistDetailVirtualView ApplyVirtualViewFromSource(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        string keywordFilter,
        MainWindowViewModel.ModeFilterType modeFilter,
        MainWindowViewModel.cSortParameters sortParameters,
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
        var viewRows = new PlaylistDetailVirtualView(
            sortedSourceRows,
            CountDistinctFolders(sortedSourceRows));
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
