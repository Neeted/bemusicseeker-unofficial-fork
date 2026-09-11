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

    /// <summary>確定した譜面のstorage ownerを集め、BMSONは同じexact path内だけで集約します。</summary>
    internal static ChartStorageTargetSet FromCharts(IEnumerable<ChartFile> charts)
    {
        List<BMSFile> bmsFiles = [];
        List<ChartFile> bmsCharts = [];
        var bmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.Ordinal);
        var bmsonChartsByPath = new Dictionary<string, ChartFile>(StringComparer.Ordinal);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null)
            {
                continue;
            }

            BMSFile bmsFile = chart.GetBmsStorageOwner();
            if (ChartFileKindResolver.IsBmsChartFile(bmsFile))
            {
                ThrowIfInvalidStorageIdentity(bmsFile.path, bmsFile.hash);
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
                ThrowIfInvalidStorageIdentity(bmsonSong.path, bmsonSong.md5);
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

    private static void ThrowIfInvalidStorageIdentity(string path, string md5)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(md5))
        {
            throw new InvalidOperationException("Owned chart storage rows must have non-empty path and md5.");
        }
    }

    /// <summary>指定されたexact pathごとのstorage行を、別keyを畳まず反映対象にします。</summary>
    internal static ChartStorageTargetSet FromRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [];
        foreach (BMSFile file in bmsFiles ?? [])
        {
            if (!ChartFileKindResolver.IsBmsChartFile(file))
            {
                continue;
            }
            ThrowIfInvalidStorageIdentity(file.path, file.hash);
            bmsFileList.Add(file);
        }
        var bmsonSongsByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.Ordinal);
        foreach (LR2SongDBExtended.bmson_song bmsonSong in bmsonSongs ?? [])
        {
            if (bmsonSong != null)
            {
                ThrowIfInvalidStorageIdentity(bmsonSong.path, bmsonSong.md5);
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
