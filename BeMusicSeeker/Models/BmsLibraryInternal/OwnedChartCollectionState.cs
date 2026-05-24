using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedChartCollectionState
{
    private readonly List<ChartFile> charts;
    private LibraryChartRefIndexSnapshot libraryChartRefIndexSnapshot;

    internal OwnedChartCollectionState()
        : this(new List<ChartFile>())
    {
    }

    private OwnedChartCollectionState(List<ChartFile> charts)
    {
        this.charts = charts ?? [];
    }

    internal static OwnedChartCollectionState FromStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<ChartFile> charts = ChartFileProjection.FromBmsStorageOwnerIdentities(bmsFiles);
        charts.AddRange(ChartFileProjection.FromBmsonStorageOwnerIdentities(bmsonSongs));
        return new OwnedChartCollectionState(charts);
    }

    internal List<ChartFile> CreateSnapshot(
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        return [.. charts
            .Select(chart => ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal List<ChartFile> CreateSnapshotForDirectChildDirectories(
        IEnumerable<string> directoryPaths,
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        var directories = new HashSet<string>(
            (directoryPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        if (directories.Count == 0)
        {
            return [];
        }

        return [.. charts
            .Where(chart => directories.Contains(GetCurrentDirectory(chart)))
            .Select(chart => ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal List<ChartFile> CreateSnapshotForMd5Hashes(
        ISet<string> md5Hashes,
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        if (md5Hashes == null || md5Hashes.Count == 0)
        {
            return [];
        }

        return [.. charts
            .Where(chart => md5Hashes.Contains(GetCurrentMd5(chart)))
            .Select(chart => ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal List<ChartFile> CreateBmsSnapshot(
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        return [.. charts
            .Where(chart => chart?.Kind == ChartFileKind.Bms)
            .Select(chart => ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal LibraryChartRefIndexSnapshot CreateLibraryChartRefIndexSnapshot()
    {
        return libraryChartRefIndexSnapshot ??= LibraryChartRefIndexSnapshot.FromStorageOwnerCharts(charts);
    }

    internal OwnedChartHashIndexSnapshot CreateOwnedHashIndexSnapshot()
    {
        var snapshot = new OwnedChartHashIndexSnapshot();
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            BMSFile bmsOwner = chart.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                AddHashes(snapshot, bmsOwner.hash, bmsOwner.sha256);
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
            if (bmsonOwner != null)
            {
                AddHashes(snapshot, bmsonOwner.md5, bmsonOwner.sha256);
                continue;
            }

            AddHashes(snapshot, chart.Md5, chart.Sha256);
        }
        return snapshot;
    }

    internal InstalledChartLookupIndexState CreateInstalledChartLookupIndexState(out int bmsCount, out int bmsonCount)
    {
        var state = new InstalledChartLookupIndexState();
        bmsCount = 0;
        bmsonCount = 0;
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            BMSFile bmsOwner = chart.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                state.AddChart(bmsOwner.path, bmsOwner.hash, bmsOwner.sha256);
                bmsCount++;
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
            if (bmsonOwner != null)
            {
                state.AddChart(bmsonOwner.path, bmsonOwner.md5, bmsonOwner.sha256);
                bmsonCount++;
                continue;
            }

            state.AddChart(chart.Path, chart.Md5, chart.Sha256);
            if (chart.Kind == ChartFileKind.Bmson)
            {
                bmsonCount++;
            }
            else
            {
                bmsCount++;
            }
        }
        return state;
    }

    internal HashSet<string> CreateInstallDestinationRuntimeStateKeySnapshot()
    {
        var keys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            AddCurrentRuntimeStateKeys(keys, chart);
        }
        return keys;
    }

    internal List<string> CreatePathSnapshot()
    {
        return [.. charts
            .Select(GetCurrentPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))];
    }

    internal void InvalidateIndexes()
    {
        libraryChartRefIndexSnapshot = null;
    }

    internal int RemoveCharts(IEnumerable<ChartFile> removedCharts)
    {
        List<ChartFile> removedChartList = [.. (removedCharts ?? []).Where(chart => chart != null)];
        if (removedChartList.Count == 0)
        {
            return 0;
        }

        var bmsOwners = new HashSet<BMSFile>(removedChartList
            .Select(chart => chart.GetBmsStorageOwner())
            .Where(file => file != null));
        var bmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>(removedChartList
            .Select(chart => chart.GetBmsonStorageOwner())
            .Where(song => song != null));
        var bmsPaths = new HashSet<string>(
            removedChartList
                .Where(chart => chart.Kind == ChartFileKind.Bms)
                .Select(chart => chart.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        var bmsonPaths = new HashSet<string>(
            removedChartList
                .Where(chart => chart.Kind == ChartFileKind.Bmson)
                .Select(chart => chart.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        int removed = charts.RemoveAll(chart => IsRemovedChart(chart, bmsOwners, bmsonOwners, bmsPaths, bmsonPaths));
        if (removed > 0)
        {
            InvalidateIndexes();
        }
        return removed;
    }

    internal void UpsertStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. (bmsonSongs ?? [])
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        if (bmsFileList.Count == 0 && bmsonSongList.Count == 0)
        {
            return;
        }

        RemoveMatchingStorageRows(bmsFileList, bmsonSongList);
        InsertBmsChartsBeforeBmson(ChartFileProjection.FromBmsStorageOwnerIdentities(bmsFileList));
        charts.AddRange(ChartFileProjection.FromBmsonStorageOwnerIdentities(bmsonSongList));
        SortBmsonChartsByPath();
        InvalidateIndexes();
    }

    internal bool MatchesStorageRows(
        IReadOnlyList<BMSFile> bmsFiles,
        IReadOnlyList<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        int chartIndex = 0;
        if (bmsFiles != null)
        {
            for (int i = 0; i < bmsFiles.Count; i++)
            {
                BMSFile bmsFile = bmsFiles[i];
                if (bmsFile == null)
                {
                    continue;
                }
                if (chartIndex >= charts.Count
                    || charts[chartIndex]?.Kind != ChartFileKind.Bms
                    || !ReferenceEquals(charts[chartIndex].GetBmsStorageOwner(), bmsFile))
                {
                    return false;
                }
                chartIndex++;
            }
        }
        if (bmsonSongs != null)
        {
            for (int i = 0; i < bmsonSongs.Count; i++)
            {
                LR2SongDBExtended.bmson_song bmsonSong = bmsonSongs[i];
                if (bmsonSong == null)
                {
                    continue;
                }
                if (chartIndex >= charts.Count
                    || charts[chartIndex]?.Kind != ChartFileKind.Bmson
                    || !ReferenceEquals(charts[chartIndex].GetBmsonStorageOwner(), bmsonSong))
                {
                    return false;
                }
                chartIndex++;
            }
        }
        return chartIndex == charts.Count;
    }

    private void RemoveMatchingStorageRows(
        IReadOnlyCollection<BMSFile> bmsFiles,
        IReadOnlyCollection<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        var bmsOwners = new HashSet<BMSFile>(bmsFiles.Where(file => file != null));
        var bmsPaths = new HashSet<string>(
            bmsFiles.Select(file => file?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        var bmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>(bmsonSongs.Where(song => song != null));
        var bmsonPaths = new HashSet<string>(
            bmsonSongs.Select(song => song?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);

        if (charts.RemoveAll(chart => IsRemovedChart(chart, bmsOwners, bmsonOwners, bmsPaths, bmsonPaths)) > 0)
        {
            InvalidateIndexes();
        }
    }

    private void InsertBmsChartsBeforeBmson(IEnumerable<ChartFile> bmsCharts)
    {
        List<ChartFile> bmsChartList = [.. (bmsCharts ?? []).Where(chart => chart != null)];
        if (bmsChartList.Count == 0)
        {
            return;
        }

        int firstBmsonIndex = charts.FindIndex(chart => chart?.Kind == ChartFileKind.Bmson);
        if (firstBmsonIndex >= 0)
        {
            charts.InsertRange(firstBmsonIndex, bmsChartList);
        }
        else
        {
            charts.AddRange(bmsChartList);
        }
    }

    private void SortBmsonChartsByPath()
    {
        int firstBmsonIndex = charts.FindIndex(chart => chart?.Kind == ChartFileKind.Bmson);
        if (firstBmsonIndex < 0)
        {
            return;
        }

        List<ChartFile> sortedBmsonCharts = [.. charts
            .Skip(firstBmsonIndex)
            .Where(chart => chart != null)
            .OrderBy(chart => chart.Path, System.StringComparer.OrdinalIgnoreCase)];
        charts.RemoveRange(firstBmsonIndex, charts.Count - firstBmsonIndex);
        charts.AddRange(sortedBmsonCharts);
    }

    private static string GetCurrentDirectory(ChartFile chart)
    {
        string path = GetCurrentPath(chart);
        return string.IsNullOrWhiteSpace(path) ? null : DirectoryExt.GetDirectoryNameSimple(path);
    }

    private static string GetCurrentPath(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return bmsOwner.path;
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        return bmsonOwner != null ? bmsonOwner.path : chart.Path;
    }

    private static string GetCurrentMd5(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return bmsOwner.hash;
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        return bmsonOwner != null ? bmsonOwner.md5 : chart.Md5;
    }

    private static void AddCurrentRuntimeStateKeys(ISet<string> keys, ChartFile chart)
    {
        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            AddRuntimeStateKeys(keys, ChartFileKind.Bms, bmsOwner.path, bmsOwner.hash, bmsOwner.sha256);
            return;
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            AddRuntimeStateKeys(keys, ChartFileKind.Bmson, bmsonOwner.path, bmsonOwner.md5, bmsonOwner.sha256);
            return;
        }

        AddRuntimeStateKeys(keys, chart.Kind, chart.Path, chart.Md5, chart.Sha256);
    }

    private static void AddRuntimeStateKeys(ISet<string> keys, ChartFileKind kind, string path, string md5, string sha256)
    {
        string primaryKey = ChartFileRuntimeStateKey.Create(kind, path, md5, sha256);
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            keys.Add(primaryKey);
        }

        string pathKey = ChartFileRuntimeStateKey.CreatePathKey(kind, path);
        if (!string.IsNullOrWhiteSpace(pathKey) && !string.Equals(pathKey, primaryKey, System.StringComparison.OrdinalIgnoreCase))
        {
            keys.Add(pathKey);
        }
    }

    private static bool IsRemovedChart(
        ChartFile chart,
        ISet<BMSFile> bmsOwners,
        ISet<LR2SongDBExtended.bmson_song> bmsonOwners,
        ISet<string> bmsPaths,
        ISet<string> bmsonPaths)
    {
        if (chart == null)
        {
            return false;
        }
        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null && bmsOwners.Contains(bmsOwner))
        {
            return true;
        }
        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null && bmsonOwners.Contains(bmsonOwner))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(chart.Path))
        {
            return false;
        }
        return chart.Kind switch
        {
            ChartFileKind.Bms => bmsPaths.Contains(chart.Path),
            ChartFileKind.Bmson => bmsonPaths.Contains(chart.Path),
            _ => false
        };
    }

    private static void AddHashes(OwnedChartHashIndexSnapshot snapshot, string md5, string sha256)
    {
        if (!string.IsNullOrWhiteSpace(md5))
        {
            snapshot.Md5Hashes.Add(md5);
        }
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            snapshot.Sha256Hashes.Add(sha256);
        }
    }
}
