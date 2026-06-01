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
        List<DuplicateChartRow> rows,
        List<BMSFile> bmsStorageRows)
    {
        MutableRows = rows ?? [];
        MutableBmsStorageRows = bmsStorageRows ?? [];
    }

    internal IReadOnlyList<DuplicateChartRow> Rows => MutableRows;

    internal IReadOnlyList<BMSFile> BmsStorageRows => MutableBmsStorageRows;

    internal List<DuplicateChartRow> MutableRows { get; }

    internal List<BMSFile> MutableBmsStorageRows { get; }

    internal OwnedDuplicateChartRowSnapshot CreateSnapshotCopy()
        => new([.. MutableRows], [.. MutableBmsStorageRows]);
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
        => !string.IsNullOrWhiteSpace(path) && ownerPaths.Contains(OwnedChartCollectionState.CreateOwnedPathKey(path));
}

internal readonly struct OwnedChartStorageRowFilterSummary(
    int pathlessBmsCount,
    int pathlessBmsonCount,
    int md5lessBmsCount,
    int md5lessBmsonCount,
    int duplicatePathBmsCount,
    int duplicatePathBmsonCount)
{
    internal int PathlessBmsCount { get; } = pathlessBmsCount;

    internal int PathlessBmsonCount { get; } = pathlessBmsonCount;

    internal int Md5lessBmsCount { get; } = md5lessBmsCount;

    internal int Md5lessBmsonCount { get; } = md5lessBmsonCount;

    internal int DuplicatePathBmsCount { get; } = duplicatePathBmsCount;

    internal int DuplicatePathBmsonCount { get; } = duplicatePathBmsonCount;

    internal bool HasSkippedRows => PathlessBmsCount > 0
        || PathlessBmsonCount > 0
        || Md5lessBmsCount > 0
        || Md5lessBmsonCount > 0
        || DuplicatePathBmsCount > 0
        || DuplicatePathBmsonCount > 0;
}

internal sealed class OwnedChartCollectionState
{
    private readonly List<ChartFile> charts;
    private LibraryChartRefIndexSnapshot libraryChartRefIndexSnapshot;
    private OwnedDuplicateChartRowSnapshot duplicateChartRowSnapshot;

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
        return FromStorageRows(bmsFiles, bmsonSongs, out _);
    }

    internal static OwnedChartCollectionState FromStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        out OwnedChartStorageRowFilterSummary filterSummary)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. (bmsonSongs ?? []).Where(song => song != null)];
        List<BMSFile> ownedBmsFiles = [];
        List<LR2SongDBExtended.bmson_song> ownedBmsonSongs = [];
        var ownedPathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pathlessBmsCount = 0;
        int pathlessBmsonCount = 0;
        int md5lessBmsCount = 0;
        int md5lessBmsonCount = 0;
        int duplicatePathBmsCount = 0;
        int duplicatePathBmsonCount = 0;
        foreach (BMSFile file in bmsFileList)
        {
            if (!HasPath(file))
            {
                pathlessBmsCount++;
                continue;
            }
            if (!HasMd5(file))
            {
                md5lessBmsCount++;
                continue;
            }
            if (!ownedPathKeys.Add(CreateOwnedPathKey(file.path)))
            {
                duplicatePathBmsCount++;
                continue;
            }
            ownedBmsFiles.Add(file);
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSongList)
        {
            if (!HasPath(song))
            {
                pathlessBmsonCount++;
                continue;
            }
            if (!HasMd5(song))
            {
                md5lessBmsonCount++;
                continue;
            }
            if (!ownedPathKeys.Add(CreateOwnedPathKey(song.path)))
            {
                duplicatePathBmsonCount++;
                continue;
            }
            ownedBmsonSongs.Add(song);
        }
        filterSummary = new OwnedChartStorageRowFilterSummary(
            pathlessBmsCount,
            pathlessBmsonCount,
            md5lessBmsCount,
            md5lessBmsonCount,
            duplicatePathBmsCount,
            duplicatePathBmsonCount);
        List<ChartFile> charts = ChartFileProjection.FromBmsStorageOwnerIdentities(ownedBmsFiles);
        charts.AddRange(ChartFileProjection.FromBmsonStorageOwnerIdentities(ownedBmsonSongs));
        return new OwnedChartCollectionState(charts);
    }

    internal List<ChartFile> CreateSnapshot(
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        return [.. charts
            .Where(HasCurrentPath)
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
            .Where(chart => HasCurrentOwnedIdentity(chart) && md5Hashes.Contains(GetCurrentMd5(chart)))
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
            .Where(HasCurrentOwnedIdentity)
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
            .Where(HasCurrentPath)
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

    /// <summary>
    /// real path / canonical lookup 用 index がすでに構築済みかを返します。
    /// </summary>
    internal bool IsLibraryChartRefIndexSnapshotInitialized => libraryChartRefIndexSnapshot != null;

    internal ILibraryChartCanonicalLookup CreateCanonicalChartLookupSnapshot()
    {
        return CreateLibraryChartRefIndexSnapshot();
    }

    internal List<LibraryChartRef> CreateLibraryChartRefsUnderRealPath(string directoryPath)
    {
        return CreateLibraryChartRefIndexSnapshot().GetChartRefsUnderRealPath(directoryPath);
    }

    internal int CountLibraryChartRefsUnderRealPath(string directoryPath)
    {
        return CreateLibraryChartRefIndexSnapshot().CountChartRefsUnderRealPath(directoryPath, null);
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

    /// <summary>
    /// library chart ref index を構築せず、現在の owned chart list を直接 scan して path 一致 chart を返します。
    /// primary hash lookup だけが温まっている upsert mutation では、既存 path の置換検出に full path index を作らないために使います。
    /// </summary>
    /// <param name="paths">検索対象 path。</param>
    /// <returns>現在の owned chart に含まれる path 一致 chart refs。</returns>
    internal List<LibraryChartRef> CreateLibraryChartRefsForPathsByScan(IEnumerable<string> paths)
    {
        var pathSet = new HashSet<string>(
            (paths ?? []).Select(CreateOwnedPathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return [];
        }

        return [.. charts
            .Where(chart => chart != null)
            .Where(chart => pathSet.Contains(CreateOwnedPathKey(GetCurrentPath(chart))))
            .Select(CreateCurrentLibraryChartRef)
            .Where(chart => chart != null)];
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
            .Where(chart => HasCurrentOwnedIdentity(chart) && HasHashMatch(chart, md5Hashes, sha256Hashes))
            .Select(CreateCurrentLibraryChartRef)
            .Where(chart => chart != null)];
    }

    internal OwnedDuplicateChartRowSnapshot CreateDuplicateChartRowSnapshot()
    {
        duplicateChartRowSnapshot ??= BuildDuplicateChartRowSnapshot();
        return duplicateChartRowSnapshot.CreateSnapshotCopy();
    }

    internal bool IsDuplicateChartRowSnapshotInitialized => duplicateChartRowSnapshot != null;

    private OwnedDuplicateChartRowSnapshot BuildDuplicateChartRowSnapshot()
    {
        var rows = new List<DuplicateChartRow>();
        var bmsStorageRows = new List<BMSFile>();
        foreach (ChartFile chart in charts)
        {
            if (!HasCurrentOwnedIdentity(chart))
            {
                continue;
            }

            DuplicateChartRow row = CreateDuplicateChartRow(chart);
            if (row != null)
            {
                rows.Add(row);
            }

            BMSFile bmsOwner = chart?.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                bmsStorageRows.Add(bmsOwner);
            }
        }

        return new OwnedDuplicateChartRowSnapshot(rows, bmsStorageRows);
    }

    internal OwnedChartStorageOwnerView CreateStorageOwnerView()
    {
        var bmsFiles = new List<BMSFile>();
        var bmsonSongs = new List<LR2SongDBExtended.bmson_song>();
        var ownerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts)
        {
            BMSFile bmsOwner = chart?.GetBmsStorageOwner();
            if (bmsOwner != null && HasOwnedStorageIdentity(bmsOwner))
            {
                bmsFiles.Add(bmsOwner);
                AddOwnerPath(ownerPaths, bmsOwner.path);
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
            if (bmsonOwner != null && HasOwnedStorageIdentity(bmsonOwner))
            {
                bmsonSongs.Add(bmsonOwner);
                AddOwnerPath(ownerPaths, bmsonOwner.path);
            }
        }
        return new OwnedChartStorageOwnerView(bmsFiles, bmsonSongs, ownerPaths);
    }

    internal OwnedChartStorageOwnerView CreateNormalLibrarySourceStorageOwnerView()
    {
        var bmsFiles = new List<BMSFile>();
        var bmsonSongs = new List<LR2SongDBExtended.bmson_song>();
        var ownerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts)
        {
            BMSFile bmsOwner = chart?.GetBmsStorageOwner();
            if (bmsOwner != null && HasOwnedStorageIdentity(bmsOwner))
            {
                bmsFiles.Add(bmsOwner);
                AddOwnerPath(ownerPaths, bmsOwner.path);
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
            if (bmsonOwner != null && HasOwnedStorageIdentity(bmsonOwner))
            {
                AddOwnerPath(ownerPaths, bmsonOwner.path);
                bmsonSongs.Add(bmsonOwner);
            }
        }
        return new OwnedChartStorageOwnerView(
            bmsFiles,
            [.. bmsonSongs.OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)],
            ownerPaths);
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
            if (!HasCurrentOwnedIdentity(chart))
            {
                continue;
            }

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

    /// <summary>
    /// owned chart の primary md5 count だけを持つ軽量 lookup state を作成します。
    /// duplicate merge など、directory lookup が不要な既所持判定で full installed lookup の構築を避けるために使います。
    /// </summary>
    /// <param name="bmsCount">BMS owner の件数。</param>
    /// <param name="bmsonCount">bmson owner の件数。</param>
    /// <returns>primary md5 lookup state。</returns>
    internal PrimaryHashLookupState CreatePrimaryHashLookupState(out int bmsCount, out int bmsonCount)
    {
        var state = new PrimaryHashLookupState();
        bmsCount = 0;
        bmsonCount = 0;
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            if (!HasCurrentOwnedIdentity(chart))
            {
                continue;
            }

            BMSFile bmsOwner = chart.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                state.AddPrimaryHash(bmsOwner.hash);
                bmsCount++;
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
            if (bmsonOwner != null)
            {
                state.AddPrimaryHash(bmsonOwner.md5);
                bmsonCount++;
                continue;
            }

            state.AddPrimaryHash(chart.Md5);
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

    internal InstalledChartLookupIndexState CreateInstalledChartLookupIndexState(out int bmsCount, out int bmsonCount)
    {
        var state = new InstalledChartLookupIndexState();
        bmsCount = 0;
        bmsonCount = 0;
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            if (!HasCurrentOwnedIdentity(chart))
            {
                continue;
            }

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

    private void ApplyDuplicateRowPathChanges(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            for (int i = 0; i < duplicateChartRowSnapshot.MutableRows.Count; i++)
            {
                DuplicateChartRow row = duplicateChartRowSnapshot.MutableRows[i];
                if (!IsDuplicateRowForChart(row, pathChange?.Chart))
                {
                    continue;
                }

                duplicateChartRowSnapshot.MutableRows[i] = row.WithPath(pathChange.NewPath);
            }
        }
    }

    private void RemoveDuplicateRows(IEnumerable<ChartFile> removedCharts)
    {
        List<ChartFile> removedChartList = [.. (removedCharts ?? []).Where(chart => chart != null)];
        if (duplicateChartRowSnapshot == null || removedChartList.Count == 0)
        {
            return;
        }

        duplicateChartRowSnapshot.MutableRows.RemoveAll(row => removedChartList.Any(chart => IsDuplicateRowForChart(row, chart)));
        var removedBmsOwners = new HashSet<BMSFile>(
            removedChartList.Select(chart => chart.GetBmsStorageOwner()).Where(file => file != null));
        if (removedBmsOwners.Count > 0)
        {
            duplicateChartRowSnapshot.MutableBmsStorageRows.RemoveAll(removedBmsOwners.Contains);
        }
    }

    private void AddDuplicateRows(IEnumerable<ChartFile> addedBmsCharts, IEnumerable<ChartFile> addedBmsonCharts)
    {
        List<DuplicateChartRow> bmsRows = [.. (addedBmsCharts ?? []).Select(CreateDuplicateChartRow).Where(row => row != null)];
        List<DuplicateChartRow> bmsonRows = [.. (addedBmsonCharts ?? []).Select(CreateDuplicateChartRow).Where(row => row != null)];
        if (bmsRows.Count > 0)
        {
            int firstBmsonIndex = duplicateChartRowSnapshot.MutableRows.FindIndex(row => row?.ChartKind == ChartFileKind.Bmson);
            if (firstBmsonIndex < 0)
            {
                duplicateChartRowSnapshot.MutableRows.AddRange(bmsRows);
            }
            else
            {
                duplicateChartRowSnapshot.MutableRows.InsertRange(firstBmsonIndex, bmsRows);
            }
            duplicateChartRowSnapshot.MutableBmsStorageRows.AddRange(bmsRows.Select(row => row.BmsFile).Where(file => file != null));
        }
        if (bmsonRows.Count > 0)
        {
            duplicateChartRowSnapshot.MutableRows.AddRange(bmsonRows);
            SortDuplicateBmsonRowsByPath();
        }
    }

    private void SortDuplicateBmsonRowsByPath()
    {
        int firstBmsonIndex = duplicateChartRowSnapshot.MutableRows.FindIndex(row => row?.ChartKind == ChartFileKind.Bmson);
        if (firstBmsonIndex < 0)
        {
            return;
        }

        List<DuplicateChartRow> sortedBmsonRows = [.. duplicateChartRowSnapshot.MutableRows
            .Skip(firstBmsonIndex)
            .Where(row => row != null)
            .OrderBy(row => row.Path, System.StringComparer.OrdinalIgnoreCase)];
        duplicateChartRowSnapshot.MutableRows.RemoveRange(
            firstBmsonIndex,
            duplicateChartRowSnapshot.MutableRows.Count - firstBmsonIndex);
        duplicateChartRowSnapshot.MutableRows.AddRange(sortedBmsonRows);
    }

    private static bool IsDuplicateRowForChart(DuplicateChartRow row, ChartFile chart)
    {
        if (row == null || chart == null)
        {
            return false;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return ReferenceEquals(row.BmsFile, bmsOwner)
                || (row.ChartKind == ChartFileKind.Bms && string.Equals(row.Path, bmsOwner.path, System.StringComparison.OrdinalIgnoreCase));
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            return ReferenceEquals(row.BmsonSong, bmsonOwner)
                || (row.ChartKind == ChartFileKind.Bmson && string.Equals(row.Path, bmsonOwner.path, System.StringComparison.OrdinalIgnoreCase));
        }

        return row.ChartKind == chart.Kind
            && string.Equals(row.Path, chart.Path, System.StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameDuplicateRowKind(DuplicateChartRow row, LibraryChartKind kind)
    {
        return (row.ChartKind == ChartFileKind.Bms && kind == LibraryChartKind.Bms)
            || (row.ChartKind == ChartFileKind.Bmson && kind == LibraryChartKind.Bmson);
    }

    private static string NormalizeMd5(string md5)
        => string.IsNullOrWhiteSpace(md5) ? null : md5.Trim();

    private static HashSet<string> CreatePathSet(IEnumerable<string> paths)
    {
        return new HashSet<string>(
            (paths ?? []).Select(CreateOwnedPathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddOwnerPath(ISet<string> ownerPaths, string path)
    {
        string pathKey = CreateOwnedPathKey(path);
        if (!string.IsNullOrWhiteSpace(pathKey))
        {
            ownerPaths.Add(pathKey);
        }
    }

    internal ChartInfoHydrationOwnerSummary CreateChartInfoHydrationOwnerSummary(
        ISet<string> currentChartInfoSha256s,
        ISet<string> currentParseFailureMd5s)
    {
        var summary = new ChartInfoHydrationOwnerSummary();
        foreach (ChartFile chart in charts.Where(HasCurrentOwnedIdentity))
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
            if (!HasCurrentOwnedIdentity(chart))
            {
                continue;
            }

            AddCurrentRuntimeStateKeys(keys, chart);
        }
        return keys;
    }

    internal HashSet<string> CreateChartRuntimeStatePrimaryKeySnapshot()
    {
        var keys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            if (!HasCurrentOwnedIdentity(chart))
            {
                continue;
            }

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
        List<LibraryChartPathChange> currentPathChanges = [.. GetPathChangesForCurrentCharts(pathChanges)];
        if (currentPathChanges.Any(change => string.IsNullOrWhiteSpace(change.NewPath)))
        {
            throw new InvalidOperationException("Owned chart path changes must keep a non-empty path.");
        }
        ThrowIfCurrentPathCollision();
        if (duplicateChartRowSnapshot != null)
        {
            ApplyDuplicateRowPathChanges(currentPathChanges);
        }
        if (libraryChartRefIndexSnapshot == null)
        {
            return;
        }

        libraryChartRefIndexSnapshot.MoveCharts(currentPathChanges);
        libraryChartRefIndexSnapshot.ReorderAffectedPathsByStorageOrder(
            charts,
            currentPathChanges.Select(change => change.OldPath).Concat(currentPathChanges.Select(change => change.NewPath)));
    }

    internal void ApplyDigestChanges(IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        if (duplicateChartRowSnapshot == null)
        {
            return;
        }

        foreach (LibraryChartDigestChange change in (digestChanges ?? []).Where(change => change?.Md5Changed == true))
        {
            string oldMd5 = NormalizeMd5(change.OldMd5);
            List<int> matchingIndexes = [];
            for (int i = 0; i < duplicateChartRowSnapshot.MutableRows.Count; i++)
            {
                DuplicateChartRow row = duplicateChartRowSnapshot.MutableRows[i];
                if (row == null
                    || !IsSameDuplicateRowKind(row, change.Kind)
                    || !string.Equals(row.Path, change.Path, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!string.Equals(row.LookupHash, oldMd5, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchingIndexes.Add(i);
            }
            if (matchingIndexes.Count == 1)
            {
                int index = matchingIndexes[0];
                duplicateChartRowSnapshot.MutableRows[index] = duplicateChartRowSnapshot.MutableRows[index].WithMd5(change.NewMd5);
            }
            else if (matchingIndexes.Count > 1)
            {
                throw new InvalidOperationException("Owned duplicate row digest update matched multiple charts for the same path.");
            }
        }
    }

    internal int RemoveCharts(IEnumerable<ChartFile> removedCharts)
    {
        List<ChartFile> removedChartList = [.. (removedCharts ?? []).Where(HasCurrentOwnedIdentity)];
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
                .Select(chart => GetCurrentPath(chart))
                .Select(CreateOwnedPathKey)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        var bmsonPaths = new HashSet<string>(
            removedChartList
                .Where(chart => chart.Kind == ChartFileKind.Bmson)
                .Select(chart => GetCurrentPath(chart))
                .Select(CreateOwnedPathKey)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        List<ChartFile> actualRemovedCharts = [.. charts.Where(chart => IsRemovedChart(chart, bmsOwners, bmsonOwners, bmsPaths, bmsonPaths))];
        int removed = charts.RemoveAll(actualRemovedCharts.Contains);
        if (removed > 0)
        {
            RemoveDuplicateRows(actualRemovedCharts);
            libraryChartRefIndexSnapshot?.RemoveCharts(actualRemovedCharts);
        }
        return removed;
    }

    internal void UpsertStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. (bmsonSongs ?? []).Where(song => song != null)];
        ThrowIfInvalidStorageRows(bmsFileList, bmsonSongList);
        ThrowIfDuplicateStorageRowPaths(bmsFileList, bmsonSongList);
        ThrowIfCrossKindUpsertPathCollision(bmsFileList, bmsonSongList);
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
        if (duplicateChartRowSnapshot != null)
        {
            RemoveDuplicateRows(removedCharts);
            AddDuplicateRows(addedBmsCharts, addedBmsonCharts);
        }
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
            bmsFiles.Select(file => CreateOwnedPathKey(file?.path)).Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        var bmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>(bmsonSongs.Where(song => song != null));
        var bmsonPaths = new HashSet<string>(
            bmsonSongs.Select(song => CreateOwnedPathKey(song?.path)).Where(path => !string.IsNullOrWhiteSpace(path)),
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

    private static bool HasCurrentPath(ChartFile chart)
        => !string.IsNullOrWhiteSpace(GetCurrentPath(chart));

    private static bool HasCurrentOwnedIdentity(ChartFile chart)
        => HasCurrentPath(chart) && !string.IsNullOrWhiteSpace(GetCurrentMd5(chart));

    private static bool HasPath(BMSFile file)
        => !string.IsNullOrWhiteSpace(file?.path);

    private static bool HasPath(LR2SongDBExtended.bmson_song song)
        => !string.IsNullOrWhiteSpace(song?.path);

    private static bool HasMd5(BMSFile file)
        => !string.IsNullOrWhiteSpace(file?.hash);

    private static bool HasMd5(LR2SongDBExtended.bmson_song song)
        => !string.IsNullOrWhiteSpace(song?.md5);

    private static bool HasOwnedStorageIdentity(BMSFile file)
        => HasPath(file) && HasMd5(file);

    private static bool HasOwnedStorageIdentity(LR2SongDBExtended.bmson_song song)
        => HasPath(song) && HasMd5(song);

    private static void ThrowIfInvalidStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        if ((bmsFiles ?? []).Any(file => file != null && !HasOwnedStorageIdentity(file))
            || (bmsonSongs ?? []).Any(song => song != null && !HasOwnedStorageIdentity(song)))
        {
            throw new InvalidOperationException("Owned chart storage rows must have non-empty path and md5.");
        }
    }

    internal static string CreateOwnedPathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return System.IO.Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static void ThrowIfDuplicateStorageRowPaths(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        var pathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile file in bmsFiles ?? [])
        {
            string pathKey = CreateOwnedPathKey(file?.path);
            if (!string.IsNullOrWhiteSpace(pathKey) && !pathKeys.Add(pathKey))
            {
                throw new InvalidOperationException("Owned chart storage row upserts must not contain duplicate paths.");
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSongs ?? [])
        {
            string pathKey = CreateOwnedPathKey(song?.path);
            if (!string.IsNullOrWhiteSpace(pathKey) && !pathKeys.Add(pathKey))
            {
                throw new InvalidOperationException("Owned chart storage row upserts must not contain duplicate paths.");
            }
        }
    }

    private void ThrowIfCrossKindUpsertPathCollision(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        var bmsPathKeys = new HashSet<string>(
            (bmsFiles ?? [])
                .Select(file => CreateOwnedPathKey(file?.path))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var bmsonPathKeys = new HashSet<string>(
            (bmsonSongs ?? [])
                .Select(song => CreateOwnedPathKey(song?.path))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            string pathKey = CreateOwnedPathKey(GetCurrentPath(chart));
            if (string.IsNullOrWhiteSpace(pathKey))
            {
                continue;
            }
            if (chart.Kind == ChartFileKind.Bms && bmsonPathKeys.Contains(pathKey))
            {
                throw new InvalidOperationException("Owned chart storage row upsert would replace a BMS row with a bmson row at the same path.");
            }
            if (chart.Kind == ChartFileKind.Bmson && bmsPathKeys.Contains(pathKey))
            {
                throw new InvalidOperationException("Owned chart storage row upsert would replace a bmson row with a BMS row at the same path.");
            }
        }
    }

    private void ThrowIfCurrentPathCollision()
    {
        var ownerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            string pathKey = CreateOwnedPathKey(GetCurrentPath(chart));
            if (!string.IsNullOrWhiteSpace(pathKey) && !ownerPaths.Add(pathKey))
            {
                throw new InvalidOperationException("Owned chart current paths must be unique.");
            }
        }
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
        string pathKey = CreateOwnedPathKey(GetCurrentPath(chart));
        if (string.IsNullOrWhiteSpace(pathKey))
        {
            return false;
        }
        return chart.Kind switch
        {
            ChartFileKind.Bms => bmsPaths.Contains(pathKey),
            ChartFileKind.Bmson => bmsonPaths.Contains(pathKey),
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
