using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartStorageTargetSet
{
    private ChartStorageTargetSet(
        List<BMSFile> bmsFiles,
        List<LR2SongDBExtended.bmson_song> bmsonSongs,
        List<ChartFile> charts)
    {
        BmsFiles = bmsFiles ?? [];
        BmsonSongs = bmsonSongs ?? [];
        Charts = charts ?? [];
    }

    internal List<BMSFile> BmsFiles { get; }

    internal List<LR2SongDBExtended.bmson_song> BmsonSongs { get; }

    internal List<ChartFile> Charts { get; }

    internal static ChartStorageTargetSet FromCharts(IEnumerable<ChartFile> charts)
    {
        List<BMSFile> bmsFiles = [];
        List<ChartFile> bmsCharts = [];
        var bmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        var bmsonChartsByPath = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null)
            {
                continue;
            }

            BMSFile bmsFile = chart.GetBmsStorageOwner();
            if (ChartFileKindResolver.IsBmsChartFile(bmsFile))
            {
                bmsFiles.Add(bmsFile);
                bmsCharts.Add(ChartFileProjection.FromBmsFile(
                    bmsFile,
                    includeWarningSnapshot: false,
                    includeResourceReferences: true,
                    includeScoreSnapshot: false));
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
            if (bmsonSong != null && !string.IsNullOrWhiteSpace(bmsonSong.path))
            {
                bmsonSongsByPath[bmsonSong.path] = bmsonSong;
                bmsonChartsByPath[bmsonSong.path] = ChartFileProjection.FromBmsonSong(
                    bmsonSong,
                    includeWarningSnapshot: false,
                    includeResourceReferences: true);
            }
        }

        return new ChartStorageTargetSet(
            bmsFiles,
            [.. bmsonSongsByPath.Values],
            [.. bmsCharts.Concat(bmsonChartsByPath.Values)]);
    }

    internal static ChartStorageTargetSet FromRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => ChartFileKindResolver.IsBmsChartFile(file))];
        var bmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.bmson_song bmsonSong in bmsonSongs ?? [])
        {
            if (bmsonSong != null && !string.IsNullOrWhiteSpace(bmsonSong.path))
            {
                bmsonSongsByPath[bmsonSong.path] = bmsonSong;
            }
        }
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. bmsonSongsByPath.Values];
        return new ChartStorageTargetSet(
            bmsFileList,
            bmsonSongList,
            ChartFileProjection.FromStorageRows(
                bmsFileList,
                bmsonSongList,
                includeWarningSnapshot: false,
                requireBmsonPath: true,
                includeResourceReferences: true,
                includeScoreSnapshot: false));
    }
}
