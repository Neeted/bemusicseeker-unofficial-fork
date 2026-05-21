using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartStorageTargetSet
{
    private ChartStorageTargetSet(List<BMSFile> bmsFiles, List<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        BmsFiles = bmsFiles ?? [];
        BmsonSongs = bmsonSongs ?? [];
    }

    internal List<BMSFile> BmsFiles { get; }

    internal List<LR2SongDBExtended.bmson_song> BmsonSongs { get; }

    internal static ChartStorageTargetSet FromCharts(IEnumerable<ChartFile> charts)
    {
        List<BMSFile> bmsFiles = [];
        var bmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
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
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
            if (bmsonSong != null && !string.IsNullOrWhiteSpace(bmsonSong.path))
            {
                bmsonSongsByPath[bmsonSong.path] = bmsonSong;
            }
        }

        return new ChartStorageTargetSet(bmsFiles, [.. bmsonSongsByPath.Values]);
    }
}
