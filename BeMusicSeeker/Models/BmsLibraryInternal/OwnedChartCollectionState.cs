using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedDuplicateChartRowSnapshot
{
    internal OwnedDuplicateChartRowSnapshot(
        IReadOnlyList<DuplicateChartRow> rows,
        IReadOnlyList<BMSFile> bmsStorageRows)
    {
        Rows = rows ?? [];
        BmsStorageRows = bmsStorageRows ?? [];
    }

    internal IReadOnlyList<DuplicateChartRow> Rows { get; }

    internal IReadOnlyList<BMSFile> BmsStorageRows { get; }
}

internal sealed class OwnedChartStorageOwnerView
{
    private readonly HashSet<string> ownerPaths;

    internal OwnedChartStorageOwnerView(
        IReadOnlyList<BMSFile> bmsFiles,
        IReadOnlyList<LR2SongDBExtended.bmson_song> bmsonSongs,
        HashSet<string> ownerPaths)
    {
        BmsFiles = bmsFiles ?? [];
        BmsonSongs = bmsonSongs ?? [];
        this.ownerPaths = ownerPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal IReadOnlyList<BMSFile> BmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs { get; }

    internal int OwnerPathCount => ownerPaths.Count;

    internal int Count => BmsFiles.Count + BmsonSongs.Count;

    internal bool ContainsOwnerPath(string path)
        => !string.IsNullOrWhiteSpace(path) && ownerPaths.Contains(path);
}

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
        List<LibraryChartRef> refs = CreateLibraryChartRefIndexSnapshot().GetDirectChartRefsInRealPaths(directoryPaths);
        if (refs.Count == 0)
        {
            return [];
        }

        return [.. refs
            .Select(chart => CreateStorageOwnerSnapshot(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal List<ChartFile> CreateSnapshotForSubtreeDirectory(
        string directoryPath,
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return CreateSnapshot(
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot);
        }

        List<LibraryChartRef> refs = CreateLibraryChartRefIndexSnapshot().GetChartRefsUnderRealPath(directoryPath);
        return [.. refs
            .Select(chart => CreateStorageOwnerSnapshot(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal ChartStorageTargetSet CreateStorageTargetsForSubtreeDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return ChartStorageTargetSet.FromRows(
                charts.Select(chart => chart?.GetBmsStorageOwner()),
                charts.Select(chart => chart?.GetBmsonStorageOwner()));
        }

        List<LibraryChartRef> refs = CreateLibraryChartRefIndexSnapshot().GetChartRefsUnderRealPath(directoryPath);
        return ChartStorageTargetSet.FromRows(
            refs.Select(chart => chart?.GetBmsStorageOwner()),
            refs.Select(chart => chart?.GetBmsonStorageOwner()));
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

    internal List<ChartFile> CreateSnapshotForPaths(
        IEnumerable<string> paths,
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        List<LibraryChartRef> refs = CreateLibraryChartRefIndexSnapshot().GetChartRefsByPaths(paths);
        if (refs.Count == 0)
        {
            return [];
        }

        return [.. refs
            .Select(chart => CreateStorageOwnerSnapshot(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal List<ChartFile> CreateFullResourceMaintenanceTargetSnapshot()
    {
        return [.. charts
            .Where(IsResourceMaintenanceTarget)
            .Select(chart => ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false))
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

    internal LibraryChartRefIndexSnapshot CreateLibraryChartRefIndexSnapshot(Action cancellationCheck = null)
    {
        return libraryChartRefIndexSnapshot ??= LibraryChartRefIndexSnapshot.FromStorageOwnerCharts(charts, cancellationCheck);
    }

    internal ILibraryChartCanonicalLookup CreateCanonicalChartLookupSnapshot()
    {
        return CreateLibraryChartRefIndexSnapshot();
    }

    internal List<LibraryChartRef> CreateLibraryChartRefsUnderRealPath(string directoryPath)
    {
        return CreateLibraryChartRefIndexSnapshot().GetChartRefsUnderRealPath(directoryPath);
    }

    internal List<string> CreateChartDirectoriesUnderRealPath(string directoryPath)
    {
        IEnumerable<LibraryChartRef> refs = string.IsNullOrWhiteSpace(directoryPath)
            ? charts.Select(CreateCurrentLibraryChartRef)
            : CreateLibraryChartRefIndexSnapshot().GetChartRefsUnderRealPath(directoryPath);
        return [.. refs
            .Select(chart => chart?.Directory)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(directory => directory.Length)
            .ThenBy(directory => directory, StringComparer.OrdinalIgnoreCase)];
    }

    internal List<LibraryChartRef> CreateLibraryChartRefsForPaths(IEnumerable<string> paths)
    {
        return CreateLibraryChartRefIndexSnapshot().GetChartRefsByPaths(paths);
    }

    internal bool ContainsKnownChart(ChartFile chart)
    {
        LibraryChartRef inputRef = LibraryChartRef.FromChartFile(chart);
        if (inputRef == null)
        {
            return false;
        }

        LibraryChartRefIndexSnapshot index = CreateLibraryChartRefIndexSnapshot();
        if (index.ResolveCanonicalCharts([inputRef]).CanonicalCharts.Count > 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(inputRef.Path)
            && index.GetChartRefsByPaths([inputRef.Path]).Any(candidate => candidate?.Kind == inputRef.Kind);
    }


    /// <summary>
    /// playlist detail の entry hash 解決に使う owned 隣接 index を作成します。
    /// </summary>
    /// <param name="cancellationCheck">構築中に呼び出す cancellation callback。</param>
    /// <returns>playlist detail 用 resolve index。</returns>
    internal PlaylistLibraryResolveIndexSnapshot CreatePlaylistLibraryResolveIndexSnapshot(Action cancellationCheck = null)
    {
        return PlaylistLibraryResolveIndexSnapshot.FromStorageOwnerCharts(charts, cancellationCheck);
    }

    internal List<LibraryChartRef> CreateLibraryChartRefsForHashes(
        ISet<string> md5Hashes,
        ISet<string> sha256Hashes)
    {
        bool hasMd5Hashes = md5Hashes?.Count > 0;
        bool hasSha256Hashes = sha256Hashes?.Count > 0;
        if (!hasMd5Hashes && !hasSha256Hashes)
        {
            return [];
        }

        return [.. charts
            .Where(chart => HasHashMatch(chart, md5Hashes, sha256Hashes))
            .Select(CreateCurrentLibraryChartRef)
            .Where(chart => chart != null)];
    }

    internal OwnedDuplicateChartRowSnapshot CreateDuplicateChartRowSnapshot()
    {
        return new OwnedDuplicateChartRowSnapshot(
            [.. charts.Select(CreateDuplicateChartRow).Where(row => row != null)],
            [.. charts.Select(chart => chart?.GetBmsStorageOwner()).Where(file => file != null)]);
    }

    internal OwnedChartStorageOwnerView CreateStorageOwnerView()
    {
        var bmsFiles = new List<BMSFile>();
        var bmsonSongs = new List<LR2SongDBExtended.bmson_song>();
        var ownerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts)
        {
            BMSFile bmsOwner = chart?.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                bmsFiles.Add(bmsOwner);
                AddOwnerPath(ownerPaths, bmsOwner.path);
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
            if (bmsonOwner != null)
            {
                bmsonSongs.Add(bmsonOwner);
                AddOwnerPath(ownerPaths, bmsonOwner.path);
            }
        }
        return new OwnedChartStorageOwnerView(bmsFiles, bmsonSongs, ownerPaths);
    }

    internal List<ChartFile> CreateFileScanRemovedStorageOwnerIdentityCharts(
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IReadOnlyList<BMSFile> nextBmsFiles,
        IReadOnlyList<LR2SongDBExtended.bmson_song> nextBmsonSongs)
    {
        var removedBmsFiles = new HashSet<BMSFile>();
        var removedBmsonSongs = new HashSet<LR2SongDBExtended.bmson_song>();
        var deletedBmsPathSet = CreatePathSet(deletedBmsPaths);
        var deletedBmsonPathSet = CreatePathSet(deletedBmsonPaths);
        var nextBmsFileSet = new HashSet<BMSFile>((nextBmsFiles ?? []).Where(file => file != null));
        var nextBmsonSongSet = new HashSet<LR2SongDBExtended.bmson_song>((nextBmsonSongs ?? []).Where(song => song != null));

        foreach (ChartFile chart in charts)
        {
            BMSFile bmsOwner = chart?.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                if (deletedBmsPathSet.Contains(bmsOwner.path) || !nextBmsFileSet.Contains(bmsOwner))
                {
                    removedBmsFiles.Add(bmsOwner);
                }
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
            if (bmsonOwner != null
                && (deletedBmsonPathSet.Contains(bmsonOwner.path) || !nextBmsonSongSet.Contains(bmsonOwner)))
            {
                removedBmsonSongs.Add(bmsonOwner);
            }
        }

        var removedCharts = ChartFileProjection.FromBmsStorageOwnerIdentities(removedBmsFiles);
        removedCharts.AddRange(ChartFileProjection.FromBmsonStorageOwnerIdentities(removedBmsonSongs));
        return removedCharts;
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

    private static DuplicateChartRow CreateDuplicateChartRow(ChartFile chart)
    {
        BMSFile bmsOwner = chart?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return DuplicateChartRow.CreateFromBmsFile(bmsOwner);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
        return bmsonOwner != null
            ? DuplicateChartRow.CreateFromBmsonSong(bmsonOwner)
            : DuplicateChartRow.CreateFromChart(chart);
    }

    private static HashSet<string> CreatePathSet(IEnumerable<string> paths)
    {
        return new HashSet<string>(
            (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddOwnerPath(ISet<string> ownerPaths, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            ownerPaths.Add(path);
        }
    }

    internal ChartInfoHydrationOwnerSummary CreateChartInfoHydrationOwnerSummary(
        ISet<string> currentChartInfoSha256s,
        ISet<string> currentParseFailureMd5s)
    {
        var summary = new ChartInfoHydrationOwnerSummary();
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            ClassifyChartInfoHydrationOwner(summary, GetCurrentSha256(chart), GetCurrentMd5(chart), currentChartInfoSha256s, currentParseFailureMd5s);
        }
        return summary;
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

    internal HashSet<string> CreateChartRuntimeStatePrimaryKeySnapshot()
    {
        var keys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            string key = CreateCurrentRuntimeStatePrimaryKey(chart);
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    internal List<string> CreatePathSnapshot()
    {
        return [.. charts
            .Select(GetCurrentPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))];
    }

    internal void ApplyPathChanges(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        if (libraryChartRefIndexSnapshot == null)
        {
            return;
        }

        List<LibraryChartPathChange> currentPathChanges = [.. GetPathChangesForCurrentCharts(pathChanges)];
        libraryChartRefIndexSnapshot.MoveCharts(currentPathChanges);
        libraryChartRefIndexSnapshot.ReorderAffectedPathsByStorageOrder(
            charts,
            currentPathChanges.Select(change => change.OldPath).Concat(currentPathChanges.Select(change => change.NewPath)));
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
        List<ChartFile> actualRemovedCharts = [.. charts.Where(chart => IsRemovedChart(chart, bmsOwners, bmsonOwners, bmsPaths, bmsonPaths))];
        int removed = charts.RemoveAll(actualRemovedCharts.Contains);
        if (removed > 0)
        {
            libraryChartRefIndexSnapshot?.RemoveCharts(actualRemovedCharts);
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

        List<ChartFile> removedCharts = RemoveMatchingStorageRows(bmsFileList, bmsonSongList);
        List<ChartFile> addedBmsCharts = ChartFileProjection.FromBmsStorageOwnerIdentities(bmsFileList);
        List<ChartFile> addedBmsonCharts = ChartFileProjection.FromBmsonStorageOwnerIdentities(bmsonSongList);
        InsertBmsChartsBeforeBmson(addedBmsCharts);
        charts.AddRange(addedBmsonCharts);
        SortBmsonChartsByPath();
        if (libraryChartRefIndexSnapshot != null)
        {
            libraryChartRefIndexSnapshot.RemoveCharts(removedCharts);
            libraryChartRefIndexSnapshot.AddCharts(addedBmsCharts);
            libraryChartRefIndexSnapshot.AddCharts(addedBmsonCharts);
            libraryChartRefIndexSnapshot.ReorderAffectedPathsByStorageOrder(
                charts,
                removedCharts.Select(GetCurrentPath)
                    .Concat(addedBmsCharts.Select(GetCurrentPath))
                    .Concat(addedBmsonCharts.Select(GetCurrentPath)));
        }
    }

    private List<ChartFile> RemoveMatchingStorageRows(
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

        List<ChartFile> removedCharts = [.. charts.Where(chart => IsRemovedChart(chart, bmsOwners, bmsonOwners, bmsPaths, bmsonPaths))];
        charts.RemoveAll(removedCharts.Contains);
        return removedCharts;
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

    private static bool IsResourceMaintenanceTarget(ChartFile chart)
    {
        return chart != null
            && (chart.Kind != ChartFileKind.Bmson || !string.IsNullOrWhiteSpace(GetCurrentPath(chart)));
    }

    private static bool HasHashMatch(ChartFile chart, ISet<string> md5Hashes, ISet<string> sha256Hashes)
    {
        if (chart == null)
        {
            return false;
        }

        string md5 = GetCurrentMd5(chart);
        if (!string.IsNullOrWhiteSpace(md5) && md5Hashes?.Contains(md5) == true)
        {
            return true;
        }

        string sha256 = GetCurrentSha256(chart);
        return !string.IsNullOrWhiteSpace(sha256) && sha256Hashes?.Contains(sha256) == true;
    }

    private IEnumerable<LibraryChartPathChange> GetPathChangesForCurrentCharts(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        var currentKeys = new HashSet<string>(
            charts.Select(CreateStorageIdentityKey).Where(key => !string.IsNullOrWhiteSpace(key)),
            System.StringComparer.OrdinalIgnoreCase);
        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            string key = CreateStorageIdentityKey(pathChange?.Chart);
            if (!string.IsNullOrWhiteSpace(key) && currentKeys.Contains(key))
            {
                yield return pathChange;
            }
        }
    }

    private static LibraryChartRef CreateCurrentLibraryChartRef(ChartFile chart)
    {
        BMSFile bmsOwner = chart?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return LibraryChartRef.FromBmsFile(bmsOwner);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
        return bmsonOwner != null ? LibraryChartRef.FromBmsonSong(bmsonOwner) : LibraryChartRef.FromChartFile(chart);
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

    private static string GetCurrentSha256(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return bmsOwner.sha256;
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        return bmsonOwner != null ? bmsonOwner.sha256 : chart.Sha256;
    }

    private static ChartFile CreateStorageOwnerSnapshot(
        LibraryChartRef chart,
        bool includeWarningSnapshot,
        bool includeResourceReferences,
        bool includeScoreSnapshot)
    {
        BMSFile bmsOwner = chart?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return ChartFileProjection.FromBmsFile(
                bmsOwner,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            return ChartFileProjection.FromBmsonSong(
                bmsonOwner,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences);
        }

        ChartFile chartSnapshot = chart?.GetChartSnapshot();
        return ChartFileProjection.FromStorageOwner(
            chartSnapshot,
            includeWarningSnapshot: includeWarningSnapshot,
            includeResourceReferences: includeResourceReferences,
            includeScoreSnapshot: includeScoreSnapshot) ?? chartSnapshot;
    }

    private static void ClassifyChartInfoHydrationOwner(
        ChartInfoHydrationOwnerSummary summary,
        string sha256,
        string md5,
        ISet<string> currentChartInfoSha256s,
        ISet<string> currentParseFailureMd5s)
    {
        summary.OwnerCount++;
        if (!string.IsNullOrWhiteSpace(sha256)
            && currentChartInfoSha256s != null
            && currentChartInfoSha256s.Contains(sha256))
        {
            summary.CurrentChartInfoOwnerCount++;
            summary.OwnerApplySkippedCount++;
            return;
        }
        if (!string.IsNullOrWhiteSpace(md5)
            && currentParseFailureMd5s != null
            && currentParseFailureMd5s.Contains(md5))
        {
            summary.CurrentParseFailureOwnerCount++;
            return;
        }
        summary.BackfillCandidateOwnerCount++;
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

    private static string CreateCurrentRuntimeStatePrimaryKey(ChartFile chart)
    {
        BMSFile bmsOwner = chart?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return ChartFileRuntimeStateKey.Create(bmsOwner);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
        return bmsonOwner != null
            ? ChartFileRuntimeStateKey.Create(bmsonOwner)
            : ChartFileRuntimeStateKey.Create(chart);
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

    private static string CreateStorageIdentityKey(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return "bms-owner:" + RuntimeHelpers.GetHashCode(bmsOwner);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            return "bmson-owner:" + RuntimeHelpers.GetHashCode(bmsonOwner);
        }

        return (chart.Kind == ChartFileKind.Bmson ? "bmson-path:" : "bms-path:") + chart.Path;
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
