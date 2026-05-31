using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

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

    internal List<string> GetDistinctChartDirectories()
    {
        return [.. Charts
            .Select(chart => chart?.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(DirectoryExt.GetDirectoryNameSimple)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

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
                ThrowIfPathless(bmsFile.path);
                bmsFiles.Add(bmsFile);
                bmsCharts.Add(ChartFileProjection.FromBmsFile(
                    bmsFile,
                    includeWarningSnapshot: false,
                    includeResourceReferences: true,
                    includeScoreSnapshot: false));
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
            if (bmsonSong != null)
            {
                ThrowIfPathless(bmsonSong.path);
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

    private static void ThrowIfPathless(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Owned chart storage rows must have a non-empty path.");
        }
    }

    internal static ChartStorageTargetSet FromRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? [])
            .Where(file => ChartFileKindResolver.IsBmsChartFile(file) && !string.IsNullOrWhiteSpace(file.path))];
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
                requirePath: true,
                includeResourceReferences: true,
                includeScoreSnapshot: false));
    }
}
