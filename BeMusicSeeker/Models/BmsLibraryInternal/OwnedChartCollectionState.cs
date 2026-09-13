using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedDuplicateChartRowSnapshot
{
    internal OwnedDuplicateChartRowSnapshot(
        IReadOnlyList<DuplicateChartRow> rows,
        IReadOnlyList<BMSFile> bmsStorageRows)
    {
        Rows = rows as DuplicateChartRow[] ?? [.. rows ?? []];
        BmsStorageRows = bmsStorageRows as BMSFile[] ?? [.. bmsStorageRows ?? []];
        DuplicateHashBuckets = BuildDuplicateHashBuckets(Rows, out int duplicateHashRowCount);
        DuplicateHashRowCount = duplicateHashRowCount;
    }

    internal IReadOnlyList<DuplicateChartRow> Rows { get; }

    internal IReadOnlyList<BMSFile> BmsStorageRows { get; }

    internal IReadOnlyList<DuplicateHashBucket> DuplicateHashBuckets { get; }

    internal int DuplicateHashCount => DuplicateHashBuckets.Count;

    internal int DuplicateHashRowCount { get; }

    private static IReadOnlyList<DuplicateHashBucket> BuildDuplicateHashBuckets(
        IReadOnlyList<DuplicateChartRow> rows,
        out int duplicateHashRowCount)
    {
        duplicateHashRowCount = 0;
        if (rows == null || rows.Count == 0)
        {
            return [];
        }

        var firstRowsByHash = new Dictionary<string, DuplicateChartRow>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<DuplicateChartRow>> duplicateRowsByHash = null;
        List<string> hashOrder = [];
        foreach (DuplicateChartRow row in rows)
        {
            if (row == null || !row.HasChartSource || string.IsNullOrWhiteSpace(row.LookupHash))
            {
                continue;
            }

            if (firstRowsByHash.TryGetValue(row.LookupHash, out DuplicateChartRow firstRow))
            {
                duplicateRowsByHash ??= new Dictionary<string, List<DuplicateChartRow>>(StringComparer.OrdinalIgnoreCase);
                if (!duplicateRowsByHash.TryGetValue(row.LookupHash, out List<DuplicateChartRow> duplicateHashRows))
                {
                    duplicateHashRows = [firstRow];
                    duplicateRowsByHash[row.LookupHash] = duplicateHashRows;
                }
                duplicateHashRows.Add(row);
            }
            else
            {
                firstRowsByHash[row.LookupHash] = row;
                hashOrder.Add(row.LookupHash);
            }
        }

        if (duplicateRowsByHash == null)
        {
            return [];
        }

        List<DuplicateHashBucket> buckets = [];
        foreach (string hash in hashOrder)
        {
            if (!duplicateRowsByHash.TryGetValue(hash, out List<DuplicateChartRow> duplicateHashRows))
            {
                continue;
            }

            duplicateHashRowCount += duplicateHashRows.Count;
            buckets.Add(new DuplicateHashBucket(hash, duplicateHashRows));
        }
        return buckets;
    }
}

internal sealed class DuplicateHashBucket
{
    internal DuplicateHashBucket(string lookupHash, IReadOnlyList<DuplicateChartRow> rows)
    {
        LookupHash = lookupHash;
        Rows = rows as DuplicateChartRow[] ?? [.. rows ?? []];
    }

    internal string LookupHash { get; }

    internal IReadOnlyList<DuplicateChartRow> Rows { get; }
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
        this.ownerPaths = ownerPaths ?? new HashSet<string>(StringComparer.Ordinal);
    }

    internal IReadOnlyList<BMSFile> BmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs { get; }

    internal int OwnerPathCount => ownerPaths.Count;

    internal int Count => BmsFiles.Count + BmsonSongs.Count;

    /// <summary>読込み済みの行が持つ未加工のexact pathだけを照合します。</summary>
    internal bool ContainsOwnerPath(string path)
        => !string.IsNullOrWhiteSpace(path) && ownerPaths.Contains(path);
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

/// <summary>
/// canonical sequenceでrefを比較する不変の順序keyです。
/// </summary>
internal readonly struct OwnedChartCanonicalOrderKey : IComparable<OwnedChartCanonicalOrderKey>
{
    /// <summary>query対象が存在しないことを表すkey。</summary>
    internal static OwnedChartCanonicalOrderKey Missing { get; } = new(ChartFileKind.Bmson, null, long.MaxValue, false);

    /// <summary>canonical sequenceの比較に必要な値を保持します。</summary>
    /// <param name="kind">chart kind。</param>
    /// <param name="capturedPath">entry作成時のpath。</param>
    /// <param name="ordinal">同一pathの安定tie順。</param>
    /// <param name="usesCapturedPathOrder">BMSONのcaptured path順を使うか。</param>
    internal OwnedChartCanonicalOrderKey(
        ChartFileKind kind,
        string capturedPath,
        long ordinal,
        bool usesCapturedPathOrder = true)
    {
        Kind = kind;
        CapturedPath = capturedPath;
        Ordinal = ordinal;
        UsesCapturedPathOrder = usesCapturedPathOrder;
    }

    /// <summary>chart kind。</summary>
    internal ChartFileKind Kind { get; }

    /// <summary>entry作成時に捕捉したpath。</summary>
    internal string CapturedPath { get; }

    /// <summary>同一kind/pathの安定tie順。</summary>
    internal long Ordinal { get; }

    /// <summary>BMSONのcaptured path順を使用するか。</summary>
    internal bool UsesCapturedPathOrder { get; }

    /// <summary>canonical sequenceの順序でこのkeyと比較します。</summary>
    public int CompareTo(OwnedChartCanonicalOrderKey other)
    {
        int kindCompare = (Kind == ChartFileKind.Bmson ? 1 : 0)
            .CompareTo(other.Kind == ChartFileKind.Bmson ? 1 : 0);
        if (kindCompare != 0)
        {
            return kindCompare;
        }
        if (UsesCapturedPathOrder && Kind == ChartFileKind.Bmson)
        {
            int pathCompare = StringComparer.OrdinalIgnoreCase.Compare(CapturedPath, other.CapturedPath);
            if (pathCompare != 0)
            {
                return pathCompare;
            }
        }
        return Ordinal.CompareTo(other.Ordinal);
    }
}

internal sealed class OwnedChartCollectionState
{
    private CatalogStorageIndexedSequence<ChartFile> chartSequence;
    private CatalogStorageReadOnlyView<ChartFile> charts;
    private readonly Dictionary<ChartFile, CatalogStorageSequenceEntry<ChartFile>> chartEntriesByChart = [];
    private readonly Dictionary<BMSFile, ChartFile> bmsChartsByOwner = [];
    private readonly Dictionary<LR2SongDBExtended.bmson_song, ChartFile> bmsonChartsByOwner = [];
    private readonly Dictionary<string, ChartFile> chartsByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<ChartFile, string> pathKeyByChart = [];
    private readonly ICatalogStorageSequenceWorkObserver sequenceWorkObserver;
    private long nextCanonicalOrdinal;
    private int bmsonChartCount;
    private bool bmsonNeedsCanonicalNormalization;
    private bool sequenceUsesCanonicalComparer;
    private LibraryChartRefIndexSnapshot libraryChartRefIndexSnapshot;
    private OwnedDuplicateChartRowSnapshot duplicateChartRowSnapshot;

    internal OwnedChartCollectionState()
        : this(new List<ChartFile>())
    {
    }

    private OwnedChartCollectionState(List<ChartFile> charts)
        : this(charts, CancellationToken.None)
    {
    }

    private OwnedChartCollectionState(List<ChartFile> charts, CancellationToken cancellationToken)
        : this(charts, cancellationToken, null)
    {
    }

    private OwnedChartCollectionState(
        List<ChartFile> charts,
        CancellationToken cancellationToken,
        ICatalogStorageSequenceWorkObserver sequenceWorkObserver)
    {
        this.sequenceWorkObserver = sequenceWorkObserver;
        InitializeCanonicalSequence(charts, cancellationToken);
        RebuildCurrentIndexes(cancellationToken);
    }

    private void InitializeCanonicalSequence(
        IEnumerable<ChartFile> initialCharts,
        CancellationToken cancellationToken)
    {
        var entries = new List<CatalogStorageSequenceEntry<ChartFile>>();
        foreach (ChartFile chart in initialCharts ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chart == null)
            {
                continue;
            }

            entries.Add(CreateCanonicalEntry(chart));
        }

        chartSequence = CatalogStorageIndexedSequence<ChartFile>.FromEntries(
            entries,
            CompareRawCanonicalEntries,
            sequenceWorkObserver);
        chartEntriesByChart.Clear();
        foreach (CatalogStorageSequenceEntry<ChartFile> entry in entries)
        {
            chartEntriesByChart[entry.Value] = entry;
        }
        bmsonNeedsCanonicalNormalization = entries.Any(entry => entry.Value?.Kind == ChartFileKind.Bmson);
        bmsonChartCount = entries.Count(entry => entry.Value?.Kind == ChartFileKind.Bmson);
        sequenceUsesCanonicalComparer = false;
        RefreshCanonicalSequenceView();
    }

    private void RefreshCanonicalSequenceView()
    {
        charts = new CatalogStorageReadOnlyView<ChartFile>(chartSequence);
    }

    private CatalogStorageSequenceEntry<ChartFile> CreateCanonicalEntry(ChartFile chart)
    {
        return new CatalogStorageSequenceEntry<ChartFile>(
            chart,
            chart?.Path,
            chart?.Path,
            nextCanonicalOrdinal++);
    }

    private static int CompareCanonicalEntries(
        CatalogStorageSequenceEntry<ChartFile> left,
        CatalogStorageSequenceEntry<ChartFile> right)
    {
        return CreateCanonicalOrderKey(left).CompareTo(CreateCanonicalOrderKey(right));
    }

    private static int CompareRawCanonicalEntries(
        CatalogStorageSequenceEntry<ChartFile> left,
        CatalogStorageSequenceEntry<ChartFile> right)
    {
        int kindCompare = GetCanonicalKindOrder(left?.Value).CompareTo(GetCanonicalKindOrder(right?.Value));
        return kindCompare != 0 ? kindCompare : left.Ordinal.CompareTo(right.Ordinal);
    }

    private static int GetCanonicalKindOrder(ChartFile chart)
        => chart?.Kind == ChartFileKind.Bmson ? 1 : 0;

    private static OwnedChartCanonicalOrderKey CreateCanonicalOrderKey(
        CatalogStorageSequenceEntry<ChartFile> entry)
    {
        return new OwnedChartCanonicalOrderKey(
            entry?.Value?.Kind ?? ChartFileKind.Bms,
            entry?.SortKey,
            entry?.Ordinal ?? long.MaxValue);
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
        return FromStorageRows(bmsFiles, bmsonSongs, CancellationToken.None, out filterSummary);
    }

    /// <summary>有効なstorage行をexact path単位で取り込み、除外した不正identityと重複行を報告します。</summary>
    internal static OwnedChartCollectionState FromStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        CancellationToken cancellationToken,
        out OwnedChartStorageRowFilterSummary filterSummary)
    {
        return FromStorageRows(
            bmsFiles,
            bmsonSongs,
            cancellationToken,
            null,
            out filterSummary);
    }

    /// <summary>
    /// 有効なstorage行を取り込み、sequenceの実アクセスを任意の内部observerへ伝えます。
    /// </summary>
    /// <param name="bmsFiles">入力順を保持するBMS storage行。</param>
    /// <param name="bmsonSongs">入力順を保持するBMSON storage行。</param>
    /// <param name="cancellationToken">取り込みを中断するtoken。</param>
    /// <param name="sequenceWorkObserver">sequenceの実処理を観測する内部observer。</param>
    /// <param name="filterSummary">除外したrowの集計。</param>
    internal static OwnedChartCollectionState FromStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        CancellationToken cancellationToken,
        ICatalogStorageSequenceWorkObserver sequenceWorkObserver,
        out OwnedChartStorageRowFilterSummary filterSummary)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. (bmsonSongs ?? []).Where(song => song != null)];
        List<BMSFile> ownedBmsFiles = [];
        List<LR2SongDBExtended.bmson_song> ownedBmsonSongs = [];
        var ownedPathKeys = new HashSet<string>(StringComparer.Ordinal);
        int pathlessBmsCount = 0;
        int pathlessBmsonCount = 0;
        int md5lessBmsCount = 0;
        int md5lessBmsonCount = 0;
        int duplicatePathBmsCount = 0;
        int duplicatePathBmsonCount = 0;
        foreach (BMSFile file in bmsFileList)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            if (!ownedPathKeys.Add(file.path))
            {
                duplicatePathBmsCount++;
                continue;
            }
            ownedBmsFiles.Add(file);
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSongList)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            if (!ownedPathKeys.Add(song.path))
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
        cancellationToken.ThrowIfCancellationRequested();
        List<ChartFile> charts = ChartFileProjection.FromBmsStorageOwnerIdentities(ownedBmsFiles, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        charts.AddRange(ChartFileProjection.FromBmsonStorageOwnerIdentities(ownedBmsonSongs, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return new OwnedChartCollectionState(charts, cancellationToken, sequenceWorkObserver);
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

    internal int CountBmsChartRefsUnderRealPath(string directoryPath)
    {
        return CreateLibraryChartRefIndexSnapshot().CountBmsChartRefsUnderRealPath(directoryPath);
    }

    internal List<string> CreateBmsChartPathsUnderRealPath(string directoryPath)
    {
        return CreateLibraryChartRefIndexSnapshot().GetBmsChartPathsUnderRealPath(directoryPath);
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
    /// canonical ref indexを構築せず、exact path索引から既存chartを取得してcanonical順に返します。
    /// </summary>
    /// <param name="paths">加工しない検索対象exact path。</param>
    /// <returns>現在の owned chart に含まれる path 一致 chart refs。</returns>
    internal List<LibraryChartRef> CreateLibraryChartRefsForCanonicalPaths(IEnumerable<string> paths)
    {
        HashSet<string> pathSet = CreatePathSet(paths);
        if (pathSet.Count == 0)
        {
            return [];
        }

        var refs = new List<LibraryChartRef>(pathSet.Count);
        foreach (string path in pathSet)
        {
            if (!chartsByPath.TryGetValue(path, out ChartFile chart))
            {
                continue;
            }

            LibraryChartKind kind = chart.Kind == ChartFileKind.Bmson
                ? LibraryChartKind.Bmson
                : LibraryChartKind.Bms;
            if (TryGetCanonicalChartRefForExactPath(kind, path, out LibraryChartRef chartRef, out _))
            {
                refs.Add(chartRef);
            }
        }
        refs.Sort(CompareCanonicalChartRefs);
        return refs;
    }

    /// <summary>
    /// kindとexact pathから、現在のowner refとcanonical sequenceの安定順を取得します。
    /// </summary>
    /// <param name="kind">照合するchart kind。</param>
    /// <param name="exactPath">加工しないexact path。</param>
    /// <param name="currentChartRef">現在のlive ownerを持つref。</param>
    /// <param name="stableOrder">canonical sequence内の安定順。</param>
    /// <returns>一致する有効なchartが存在する場合はtrue。</returns>
    internal bool TryGetCanonicalChartRefForExactPath(
        LibraryChartKind kind,
        string exactPath,
        out LibraryChartRef currentChartRef,
        out OwnedChartCanonicalOrderKey stableOrder)
    {
        currentChartRef = null;
        stableOrder = OwnedChartCanonicalOrderKey.Missing;
        if (string.IsNullOrWhiteSpace(exactPath)
            || !chartsByPath.TryGetValue(exactPath, out ChartFile chart)
            || (chart.Kind == ChartFileKind.Bmson) != (kind == LibraryChartKind.Bmson))
        {
            return false;
        }

        return TryGetCanonicalChartRef(chart, out currentChartRef, out stableOrder);
    }

    /// <summary>
    /// ownerまたはexact pathを使って、現在のrefとcanonical sequenceの安定順を取得します。
    /// </summary>
    /// <param name="inputChartRef">owner refまたはpath ref。</param>
    /// <param name="currentChartRef">現在のlive ownerを持つref。</param>
    /// <param name="stableOrder">canonical sequence内の安定順。</param>
    /// <returns>一致する有効なchartが存在する場合はtrue。</returns>
    internal bool TryGetCanonicalChartRef(
        LibraryChartRef inputChartRef,
        out LibraryChartRef currentChartRef,
        out OwnedChartCanonicalOrderKey stableOrder)
    {
        currentChartRef = null;
        stableOrder = OwnedChartCanonicalOrderKey.Missing;
        TryResolveCanonicalChartRef(inputChartRef, out ChartFile chart);
        return chart != null && TryGetCanonicalChartRef(chart, out currentChartRef, out stableOrder);
    }

    private bool TryResolveCanonicalChartRef(LibraryChartRef inputChartRef, out ChartFile chart)
    {
        chart = null;
        BMSFile bmsOwner = inputChartRef?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            bmsChartsByOwner.TryGetValue(bmsOwner, out chart);
        }
        else
        {
            LR2SongDBExtended.bmson_song bmsonOwner = inputChartRef?.GetBmsonStorageOwner();
            if (bmsonOwner != null)
            {
                bmsonChartsByOwner.TryGetValue(bmsonOwner, out chart);
            }
        }

        if (chart == null
            && !string.IsNullOrWhiteSpace(inputChartRef?.Path)
            && chartsByPath.TryGetValue(inputChartRef.Path, out ChartFile pathChart)
            && (pathChart.Kind == ChartFileKind.Bmson) == (inputChartRef.Kind == LibraryChartKind.Bmson))
        {
            chart = pathChart;
        }
        return chart != null;
    }

    private bool TryGetCanonicalChartRef(
        ChartFile chart,
        out LibraryChartRef currentChartRef,
        out OwnedChartCanonicalOrderKey stableOrder)
    {
        currentChartRef = null;
        stableOrder = OwnedChartCanonicalOrderKey.Missing;
        if (chart == null || !chartEntriesByChart.TryGetValue(chart, out CatalogStorageSequenceEntry<ChartFile> entry))
        {
            return false;
        }

        currentChartRef = CreateCurrentLibraryChartRef(chart);
        if (currentChartRef == null)
        {
            return false;
        }
        stableOrder = CreateCurrentOrderKey(entry);
        return true;
    }

    private OwnedChartCanonicalOrderKey CreateCurrentOrderKey(
        CatalogStorageSequenceEntry<ChartFile> entry)
    {
        return new OwnedChartCanonicalOrderKey(
            entry?.Value?.Kind ?? ChartFileKind.Bms,
            entry?.SortKey,
            entry?.Ordinal ?? long.MaxValue,
            sequenceUsesCanonicalComparer);
    }

    private int CompareCanonicalChartRefs(LibraryChartRef left, LibraryChartRef right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left == null)
        {
            return 1;
        }
        if (right == null)
        {
            return -1;
        }

        if (TryResolveCanonicalChartRef(left, out ChartFile leftChart)
            && TryResolveCanonicalChartRef(right, out ChartFile rightChart)
            && chartEntriesByChart.TryGetValue(leftChart, out CatalogStorageSequenceEntry<ChartFile> leftEntry)
            && chartEntriesByChart.TryGetValue(rightChart, out CatalogStorageSequenceEntry<ChartFile> rightEntry))
        {
            return sequenceUsesCanonicalComparer
                ? CompareCanonicalEntries(leftEntry, rightEntry)
                : CompareRawCanonicalEntries(leftEntry, rightEntry);
        }
        return StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
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
    /// playlist detail の hash 解決に使う chart fact snapshot を作成します。
    /// ref と canonical 順序を同じ走査で捕捉し、後続の差分更新へ live owner を渡しません。
    /// </summary>
    /// <param name="cancellationCheck">構築中に呼び出す cancellation callback。</param>
    /// <param name="storeWorkObserver">実格納の列挙とentry訪問を記録する任意の内部observer。</param>
    /// <returns>playlist detail 用 resolve fact snapshot。</returns>
    internal List<PlaylistLibraryResolveChartFact> CreatePlaylistLibraryResolveChartFactSnapshot(
        Action cancellationCheck = null,
        Action<string> storeWorkObserver = null)
    {
        var facts = new List<PlaylistLibraryResolveChartFact>(charts.Count);
        storeWorkObserver?.Invoke("playlist_resolve_source_enumeration");
        foreach (ChartFile chart in charts)
        {
            cancellationCheck?.Invoke();
            storeWorkObserver?.Invoke("playlist_resolve_source_entry_visited");
            if (!HasCurrentOwnedIdentity(chart)
                || !TryGetCanonicalChartRef(
                    chart,
                    out LibraryChartRef chartRef,
                    out OwnedChartCanonicalOrderKey stableOrder))
            {
                continue;
            }

            PlaylistLibraryResolveChartFact fact = PlaylistLibraryResolveChartFact.FromChart(
                chartRef,
                stableOrder);
            if (fact != null)
            {
                facts.Add(fact);
            }
        }
        return facts;
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
        return duplicateChartRowSnapshot;
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

    private void RebuildCurrentIndexes()
    {
        RebuildCurrentIndexes(CancellationToken.None);
    }

    private void RebuildCurrentIndexes(CancellationToken cancellationToken)
    {
        bmsChartsByOwner.Clear();
        bmsonChartsByOwner.Clear();
        chartsByPath.Clear();
        pathKeyByChart.Clear();
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegisterCurrentChartIndex(chart);
        }
    }

    private void RegisterCurrentChartIndex(ChartFile chart)
    {
        if (chart == null)
        {
            return;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            bmsChartsByOwner[bmsOwner] = chart;
        }
        else
        {
            LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
            if (bmsonOwner != null)
            {
                bmsonChartsByOwner[bmsonOwner] = chart;
            }
        }

        string pathKey = GetCurrentPath(chart);
        if (string.IsNullOrWhiteSpace(pathKey))
        {
            return;
        }
        if (chartsByPath.TryGetValue(pathKey, out ChartFile existingChart) && !ReferenceEquals(existingChart, chart))
        {
            throw new InvalidOperationException("Owned chart current paths must be unique.");
        }
        chartsByPath[pathKey] = chart;
        pathKeyByChart[chart] = pathKey;
    }

    private void UnregisterCurrentChartIndex(ChartFile chart)
    {
        if (chart == null)
        {
            return;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null && bmsChartsByOwner.TryGetValue(bmsOwner, out ChartFile indexedBmsChart) && ReferenceEquals(indexedBmsChart, chart))
        {
            bmsChartsByOwner.Remove(bmsOwner);
        }
        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null && bmsonChartsByOwner.TryGetValue(bmsonOwner, out ChartFile indexedBmsonChart) && ReferenceEquals(indexedBmsonChart, chart))
        {
            bmsonChartsByOwner.Remove(bmsonOwner);
        }
        if (pathKeyByChart.TryGetValue(chart, out string pathKey))
        {
            pathKeyByChart.Remove(chart);
            if (chartsByPath.TryGetValue(pathKey, out ChartFile indexedPathChart) && ReferenceEquals(indexedPathChart, chart))
            {
                chartsByPath.Remove(pathKey);
            }
        }
    }

    private bool TryResolveCurrentChartByOwner(ChartFile inputChart, out ChartFile currentChart)
    {
        currentChart = null;
        BMSFile bmsOwner = inputChart?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return bmsChartsByOwner.TryGetValue(bmsOwner, out currentChart);
        }
        LR2SongDBExtended.bmson_song bmsonOwner = inputChart?.GetBmsonStorageOwner();
        return bmsonOwner != null && bmsonChartsByOwner.TryGetValue(bmsonOwner, out currentChart);
    }

    private bool TryResolveRemoveRequest(OwnedChartRemoveRequest request, out ChartFile currentChart)
    {
        currentChart = null;
        if (request == null)
        {
            return false;
        }
        if (request.Mode == OwnedChartRemoveMode.OwnerReference)
        {
            if (request.BmsOwner != null)
            {
                return bmsChartsByOwner.TryGetValue(request.BmsOwner, out currentChart);
            }
            return request.BmsonOwner != null && bmsonChartsByOwner.TryGetValue(request.BmsonOwner, out currentChart);
        }

        string pathKey = request.Path;
        if (string.IsNullOrWhiteSpace(pathKey) || !chartsByPath.TryGetValue(pathKey, out currentChart))
        {
            currentChart = null;
            return false;
        }
        if (currentChart.Kind != request.Kind)
        {
            currentChart = null;
            return false;
        }
        return true;
    }

    internal OwnedChartStorageOwnerView CreateStorageOwnerView()
    {
        var bmsFiles = new List<BMSFile>();
        var bmsonSongs = new List<LR2SongDBExtended.bmson_song>();
        var ownerPaths = new HashSet<string>(StringComparer.Ordinal);
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
        var ownerPaths = new HashSet<string>(StringComparer.Ordinal);
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
        return CreateOwnedHashIndexSnapshot(CancellationToken.None);
    }

    internal OwnedChartHashIndexSnapshot CreateOwnedHashIndexSnapshot(CancellationToken cancellationToken)
    {
        return CreateOwnedHashIndexSnapshot(cancellationToken, null);
    }

    /// <summary>
    /// 現在の owned chart を一度だけ走査して hash build projection を作成します。
    /// </summary>
    /// <param name="cancellationToken">構築を中断する token。</param>
    /// <param name="storeWorkObserver">実際の source 列挙を記録する任意の内部 observer。</param>
    /// <returns>MD5/SHA-256 owner count を含む build projection。</returns>
    internal OwnedChartHashIndexSnapshot CreateOwnedHashIndexSnapshot(
        CancellationToken cancellationToken,
        Action<string> storeWorkObserver)
    {
        var snapshot = new OwnedChartHashIndexSnapshot();
        storeWorkObserver?.Invoke("owned_hash_source_enumeration");
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            storeWorkObserver?.Invoke("owned_hash_source_entry_visited");
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
        if (duplicateChartRowSnapshot == null)
        {
            return;
        }

        DuplicateChartRow[] rows = [.. duplicateChartRowSnapshot.Rows];
        bool changed = false;
        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            for (int i = 0; i < rows.Length; i++)
            {
                DuplicateChartRow row = rows[i];
                if (!IsDuplicateRowForChart(row, pathChange?.Chart))
                {
                    continue;
                }

                rows[i] = row.WithPath(pathChange.NewPath);
                changed = true;
            }
        }
        if (changed)
        {
            duplicateChartRowSnapshot = new OwnedDuplicateChartRowSnapshot(rows, duplicateChartRowSnapshot.BmsStorageRows);
        }
    }

    private void RemoveDuplicateRows(RemovedChartKeySet removedKeys)
    {
        if (duplicateChartRowSnapshot == null || removedKeys?.IsEmpty != false)
        {
            return;
        }

        List<DuplicateChartRow> rows = new(duplicateChartRowSnapshot.Rows.Count);
        bool changed = false;
        foreach (DuplicateChartRow row in duplicateChartRowSnapshot.Rows)
        {
            if (removedKeys.ContainsDuplicateRow(row))
            {
                changed = true;
                continue;
            }
            rows.Add(row);
        }
        if (!changed)
        {
            return;
        }

        IReadOnlyList<BMSFile> bmsStorageRows = duplicateChartRowSnapshot.BmsStorageRows;
        if (removedKeys.HasBmsKeys)
        {
            bmsStorageRows = [.. bmsStorageRows.Where(file => !removedKeys.ContainsBmsFile(file))];
        }
        duplicateChartRowSnapshot = new OwnedDuplicateChartRowSnapshot(rows, bmsStorageRows);
    }

    private void AddDuplicateRows(IEnumerable<ChartFile> addedBmsCharts, IEnumerable<ChartFile> addedBmsonCharts)
    {
        List<DuplicateChartRow> bmsRows = [.. (addedBmsCharts ?? []).Select(CreateDuplicateChartRow).Where(row => row != null)];
        List<DuplicateChartRow> bmsonRows = [.. (addedBmsonCharts ?? []).Select(CreateDuplicateChartRow).Where(row => row != null)];
        if (bmsRows.Count == 0 && bmsonRows.Count == 0)
        {
            return;
        }

        List<DuplicateChartRow> rows = [.. duplicateChartRowSnapshot.Rows];
        List<BMSFile> bmsStorageRows = [.. duplicateChartRowSnapshot.BmsStorageRows];
        if (bmsRows.Count > 0)
        {
            int firstBmsonIndex = rows.FindIndex(row => row?.ChartKind == ChartFileKind.Bmson);
            if (firstBmsonIndex < 0)
            {
                rows.AddRange(bmsRows);
            }
            else
            {
                rows.InsertRange(firstBmsonIndex, bmsRows);
            }
            bmsStorageRows.AddRange(bmsRows.Select(row => row.BmsFile).Where(file => file != null));
        }
        if (bmsonRows.Count > 0)
        {
            rows.AddRange(bmsonRows);
            SortDuplicateBmsonRowsByPath(rows);
        }

        duplicateChartRowSnapshot = new OwnedDuplicateChartRowSnapshot(rows, bmsStorageRows);
    }

    private static void SortDuplicateBmsonRowsByPath(List<DuplicateChartRow> rows)
    {
        int firstBmsonIndex = rows.FindIndex(row => row?.ChartKind == ChartFileKind.Bmson);
        if (firstBmsonIndex < 0)
        {
            return;
        }

        List<DuplicateChartRow> sortedBmsonRows = [.. rows
            .Skip(firstBmsonIndex)
            .Where(row => row != null)
            .OrderBy(row => row.Path, System.StringComparer.OrdinalIgnoreCase)];
        rows.RemoveRange(
            firstBmsonIndex,
            rows.Count - firstBmsonIndex);
        rows.AddRange(sortedBmsonRows);
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
                || (row.ChartKind == ChartFileKind.Bms && string.Equals(row.Path, bmsOwner.path, System.StringComparison.Ordinal));
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            return ReferenceEquals(row.BmsonSong, bmsonOwner)
                || (row.ChartKind == ChartFileKind.Bmson && string.Equals(row.Path, bmsonOwner.path, System.StringComparison.Ordinal));
        }

        return row.ChartKind == chart.Kind
            && string.Equals(row.Path, chart.Path, System.StringComparison.Ordinal);
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
            (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
    }

    private static void AddOwnerPath(ISet<string> ownerPaths, string path)
    {
        string pathKey = path;
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
        var keys = new HashSet<string>(System.StringComparer.Ordinal);
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
        var keys = new HashSet<string>(System.StringComparer.Ordinal);
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

    /// <summary>現在のownerへ確定した新exact pathを反映し、同じkeyの衝突を拒否します。</summary>
    internal void ApplyPathChanges(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        List<LibraryChartPathChange> currentPathChanges = [.. GetPathChangesForCurrentCharts(pathChanges)];
        currentPathChanges = [.. currentPathChanges
            .GroupBy(
                change => (CreateStorageIdentityKey(change.Chart) ?? string.Empty) + "|" + (change.NewPath ?? string.Empty),
                StringComparer.Ordinal)
            .Select(group => group.First())];
        if (currentPathChanges.Any(change => string.IsNullOrWhiteSpace(change.NewPath)))
        {
            throw new InvalidOperationException("Owned chart path changes must keep a non-empty path.");
        }
        var changingCharts = new HashSet<ChartFile>();
        var newPathKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (LibraryChartPathChange pathChange in currentPathChanges)
        {
            if (!TryResolveCurrentChartByOwner(pathChange.Chart, out ChartFile currentChart))
            {
                continue;
            }
            changingCharts.Add(currentChart);
            string newPathKey = pathChange.NewPath;
            if (string.IsNullOrWhiteSpace(newPathKey))
            {
                throw new InvalidOperationException("Owned chart path changes must keep a non-empty path.");
            }
            if (!newPathKeys.Add(newPathKey))
            {
                throw new InvalidOperationException("Owned chart path changes must keep unique target paths.");
            }
            if (chartsByPath.TryGetValue(newPathKey, out ChartFile existingChart)
                && !ReferenceEquals(existingChart, currentChart)
                && !changingCharts.Contains(existingChart)
                && !currentPathChanges.Any(change => TryResolveCurrentChartByOwner(change.Chart, out ChartFile changingChart) && ReferenceEquals(changingChart, existingChart)))
            {
                throw new InvalidOperationException("Owned chart path change would collide with another owned chart.");
            }
        }
        if (duplicateChartRowSnapshot != null)
        {
            ApplyDuplicateRowPathChanges(currentPathChanges);
        }
        if (libraryChartRefIndexSnapshot == null)
        {
            ApplyCurrentPathIndexChanges(currentPathChanges);
            return;
        }

        libraryChartRefIndexSnapshot.MoveCharts(currentPathChanges);
        libraryChartRefIndexSnapshot.ReorderAffectedPathsByStorageOrder(
            currentPathChanges.Select(change => change.OldPath).Concat(currentPathChanges.Select(change => change.NewPath)),
            CompareCanonicalChartRefs);
        ApplyCurrentPathIndexChanges(currentPathChanges);
    }

    internal void ApplyDigestChanges(IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        if (duplicateChartRowSnapshot == null)
        {
            return;
        }

        DuplicateChartRow[] rows = [.. duplicateChartRowSnapshot.Rows];
        bool changed = false;
        foreach (LibraryChartDigestChange change in (digestChanges ?? []).Where(change => change?.Md5Changed == true))
        {
            string oldMd5 = NormalizeMd5(change.OldMd5);
            List<int> matchingIndexes = [];
            for (int i = 0; i < rows.Length; i++)
            {
                DuplicateChartRow row = rows[i];
                if (row == null
                    || !IsSameDuplicateRowKind(row, change.Kind)
                    || !string.Equals(row.Path, change.Path, System.StringComparison.Ordinal))
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
                rows[index] = rows[index].WithMd5(change.NewMd5);
                changed = true;
            }
            else if (matchingIndexes.Count > 1)
            {
                throw new InvalidOperationException("Owned duplicate row digest update matched multiple charts for the same path.");
            }
        }
        if (changed)
        {
            duplicateChartRowSnapshot = new OwnedDuplicateChartRowSnapshot(rows, duplicateChartRowSnapshot.BmsStorageRows);
        }
    }

    private void ApplyCurrentPathIndexChanges(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            if (!TryResolveCurrentChartByOwner(pathChange?.Chart, out ChartFile currentChart))
            {
                continue;
            }

            if (pathKeyByChart.TryGetValue(currentChart, out string oldPathKey)
                && chartsByPath.TryGetValue(oldPathKey, out ChartFile indexedChart)
                && ReferenceEquals(indexedChart, currentChart))
            {
                chartsByPath.Remove(oldPathKey);
            }

            string newPathKey = pathChange.NewPath;
            if (string.IsNullOrWhiteSpace(newPathKey))
            {
                pathKeyByChart.Remove(currentChart);
                continue;
            }
            chartsByPath[newPathKey] = currentChart;
            pathKeyByChart[currentChart] = newPathKey;
        }
    }

    /// <summary>現在のowner参照または指定された旧exact keyだけを正本と索引から取り除きます。</summary>
    internal int RemoveChartRequests(IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        List<ChartFile> actualRemovedCharts = [];
        var actualRemovedSet = new HashSet<ChartFile>();
        foreach (OwnedChartRemoveRequest request in removeRequests ?? [])
        {
            if (TryResolveRemoveRequest(request, out ChartFile currentChart) && actualRemovedSet.Add(currentChart))
            {
                actualRemovedCharts.Add(currentChart);
            }
        }
        if (actualRemovedCharts.Count == 0)
        {
            return 0;
        }

        var removedCharts = new List<ChartFile>(actualRemovedCharts.Count);
        foreach (ChartFile chart in actualRemovedCharts)
        {
            if (!RemoveCanonicalChart(chart))
            {
                continue;
            }
            UnregisterCurrentChartIndex(chart);
            removedCharts.Add(chart);
        }
        int removed = removedCharts.Count;
        if (removed > 0)
        {
            RemovedChartKeySet actualRemovedKeys = RemovedChartKeySet.FromCharts(removedCharts, includePaths: false);
            RemoveDuplicateRows(actualRemovedKeys);
            libraryChartRefIndexSnapshot?.RemoveCharts(removedCharts);
        }
        return removed;
    }

    internal List<OwnedChartRemoveRequest> ResolveCurrentRemoveRequests(IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        var resolvedRequests = new List<OwnedChartRemoveRequest>();
        var resolvedCharts = new HashSet<ChartFile>();
        foreach (OwnedChartRemoveRequest request in removeRequests ?? [])
        {
            if (request?.Mode == OwnedChartRemoveMode.PathCleanup)
            {
                resolvedRequests.Add(request);
                continue;
            }
            if (!TryResolveRemoveRequest(request, out ChartFile currentChart) || !resolvedCharts.Add(currentChart))
            {
                continue;
            }

            BMSFile bmsOwner = currentChart.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                resolvedRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsOwner));
                continue;
            }

            LR2SongDBExtended.bmson_song bmsonOwner = currentChart.GetBmsonStorageOwner();
            if (bmsonOwner != null)
            {
                resolvedRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsonOwner));
            }
        }
        return resolvedRequests;
    }

    /// <summary>
    /// storage rowを局所的に置換し、今回の操作で初回BMSON canonical順序正規化が起きたかを返します。
    /// </summary>
    /// <param name="bmsFiles">追加または置換するBMS storage row。</param>
    /// <param name="bmsonSongs">追加または置換するBMSON storage row。</param>
    /// <returns>既存BMSON suffixの初回canonical正規化を実施した場合は<see langword="true"/>。</returns>
    internal bool UpsertStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. (bmsonSongs ?? []).Where(song => song != null)];
        ValidateStorageRows(bmsFileList, bmsonSongList);
        if (bmsFileList.Count == 0 && bmsonSongList.Count == 0)
        {
            return false;
        }

        bool bmsonCanonicalOrderNormalized = false;
        List<string> normalizedPaths = [];
        if (bmsonSongList.Count > 0 || bmsonNeedsCanonicalNormalization)
        {
            bmsonCanonicalOrderNormalized = EnsureCanonicalBmsonOrder(out normalizedPaths);
        }
        List<string> affectedPaths = [.. normalizedPaths
            .Concat(bmsFileList.Select(file => file.path))
            .Concat(bmsonSongList.Select(song => song.path))];
        List<ChartFile> removedCharts = RemoveMatchingStorageRows(bmsFileList, bmsonSongList);
        List<ChartFile> addedBmsCharts = ChartFileProjection.FromBmsStorageOwnerIdentities(bmsFileList);
        List<ChartFile> addedBmsonCharts = ChartFileProjection.FromBmsonStorageOwnerIdentities(bmsonSongList);
        foreach (ChartFile chart in addedBmsCharts.Concat(addedBmsonCharts))
        {
            InsertCanonicalChart(chart);
            RegisterCurrentChartIndex(chart);
        }
        if (duplicateChartRowSnapshot != null)
        {
            RemoveDuplicateRows(RemovedChartKeySet.FromCharts(removedCharts, includePaths: false));
            AddDuplicateRows(addedBmsCharts, addedBmsonCharts);
        }
        if (libraryChartRefIndexSnapshot != null)
        {
            libraryChartRefIndexSnapshot.RemoveCharts(removedCharts);
            libraryChartRefIndexSnapshot.AddCharts(addedBmsCharts);
            libraryChartRefIndexSnapshot.AddCharts(addedBmsonCharts);
            libraryChartRefIndexSnapshot.ReorderAffectedPathsByStorageOrder(
                affectedPaths,
                CompareCanonicalChartRefs);
        }
        return bmsonCanonicalOrderNormalized;
    }

    internal void ValidateStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. (bmsonSongs ?? []).Where(song => song != null)];
        ThrowIfInvalidStorageRows(bmsFileList, bmsonSongList);
        ThrowIfDuplicateStorageRowPaths(bmsFileList, bmsonSongList);
        ThrowIfCrossKindUpsertPathCollision(bmsFileList, bmsonSongList);
    }

    private bool EnsureCanonicalBmsonOrder(out List<string> normalizedPaths)
    {
        normalizedPaths = [];
        if (sequenceUsesCanonicalComparer && !bmsonNeedsCanonicalNormalization)
        {
            return false;
        }

        if (bmsonChartCount == 0)
        {
            chartSequence = chartSequence.WithComparison(CompareCanonicalEntries);
            sequenceUsesCanonicalComparer = true;
            bmsonNeedsCanonicalNormalization = false;
            RefreshCanonicalSequenceView();
            return false;
        }

        int bmsonStartIndex = chartSequence.Count - bmsonChartCount;
        IReadOnlyList<CatalogStorageSequenceEntry<ChartFile>> currentBmsonEntries =
            chartSequence.CaptureRange(bmsonStartIndex, bmsonChartCount);
        var normalizedBmsonEntries = new List<CatalogStorageSequenceEntry<ChartFile>>(bmsonChartCount);
        normalizedPaths = new List<string>(bmsonChartCount);
        foreach (CatalogStorageSequenceEntry<ChartFile> entry in currentBmsonEntries)
        {
            if (entry?.Value == null)
            {
                continue;
            }

            normalizedBmsonEntries.Add(CreateCanonicalEntry(entry.Value));
            string currentPath = GetCurrentPath(entry.Value);
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                normalizedPaths.Add(currentPath);
            }
        }
        normalizedBmsonEntries.Sort(CompareCanonicalEntries);
        chartSequence = chartSequence
            .ReplaceRange(bmsonStartIndex, bmsonChartCount, normalizedBmsonEntries)
            .WithComparison(CompareCanonicalEntries);
        foreach (CatalogStorageSequenceEntry<ChartFile> entry in normalizedBmsonEntries)
        {
            chartEntriesByChart[entry.Value] = entry;
        }
        bmsonNeedsCanonicalNormalization = false;
        sequenceUsesCanonicalComparer = true;
        RefreshCanonicalSequenceView();
        return true;
    }

    private void InsertCanonicalChart(ChartFile chart)
    {
        if (chart == null)
        {
            return;
        }

        CatalogStorageSequenceEntry<ChartFile> entry = CreateCanonicalEntry(chart);
        int insertionIndex = chartSequence.FindInsertionIndex(entry);
        chartSequence = chartSequence.InsertAt(insertionIndex, entry);
        chartEntriesByChart[chart] = entry;
        if (chart.Kind == ChartFileKind.Bmson)
        {
            bmsonChartCount++;
        }
        RefreshCanonicalSequenceView();
    }

    private bool RemoveCanonicalChart(ChartFile chart)
    {
        if (chart == null || !chartEntriesByChart.TryGetValue(chart, out CatalogStorageSequenceEntry<ChartFile> entry))
        {
            return false;
        }

        int index = chartSequence.FindIndex(entry);
        if (index < 0)
        {
            throw new InvalidOperationException("Owned chart canonical sequence entry is missing.");
        }
        chartSequence = chartSequence.RemoveAt(index);
        chartEntriesByChart.Remove(chart);
        if (chart.Kind == ChartFileKind.Bmson)
        {
            bmsonChartCount--;
            if (bmsonChartCount == 0)
            {
                bmsonNeedsCanonicalNormalization = false;
            }
        }
        RefreshCanonicalSequenceView();
        return true;
    }

    internal static void ValidateStorageRowsWithoutExistingCollection(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> bmsonSongList = [.. (bmsonSongs ?? []).Where(song => song != null)];
        ThrowIfInvalidStorageRows(bmsFileList, bmsonSongList);
        ThrowIfDuplicateStorageRowPaths(bmsFileList, bmsonSongList);
    }

    private List<ChartFile> RemoveMatchingStorageRows(
        IReadOnlyCollection<BMSFile> bmsFiles,
        IReadOnlyCollection<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<ChartFile> removedCharts = [];
        var removedSet = new HashSet<ChartFile>();
        foreach (BMSFile file in bmsFiles ?? [])
        {
            string pathKey = file?.path;
            if (string.IsNullOrWhiteSpace(pathKey)
                || !chartsByPath.TryGetValue(pathKey, out ChartFile chart)
                || chart.Kind != ChartFileKind.Bms)
            {
                continue;
            }
            if (removedSet.Add(chart))
            {
                removedCharts.Add(chart);
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSongs ?? [])
        {
            string pathKey = song?.path;
            if (string.IsNullOrWhiteSpace(pathKey)
                || !chartsByPath.TryGetValue(pathKey, out ChartFile chart)
                || chart.Kind != ChartFileKind.Bmson)
            {
                continue;
            }
            if (removedSet.Add(chart))
            {
                removedCharts.Add(chart);
            }
        }
        if (removedSet.Count > 0)
        {
            foreach (ChartFile chart in removedCharts)
            {
                if (RemoveCanonicalChart(chart))
                {
                    UnregisterCurrentChartIndex(chart);
                }
            }
        }
        return removedCharts;
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

    /// <summary>
    /// filesystem側の既存比較用にpathを正規化します。DB行・owned行のexact identityには使いません。
    /// </summary>
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
        var pathKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (BMSFile file in bmsFiles ?? [])
        {
            string pathKey = file?.path;
            if (!string.IsNullOrWhiteSpace(pathKey) && !pathKeys.Add(pathKey))
            {
                throw new InvalidOperationException("Owned chart storage row upserts must not contain duplicate paths.");
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSongs ?? [])
        {
            string pathKey = song?.path;
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
        foreach (BMSFile file in bmsFiles ?? [])
        {
            string pathKey = file?.path;
            if (!string.IsNullOrWhiteSpace(pathKey)
                && chartsByPath.TryGetValue(pathKey, out ChartFile existingChart)
                && existingChart.Kind != ChartFileKind.Bms)
            {
                throw new InvalidOperationException("Owned chart storage row upsert would replace a bmson row with a BMS row at the same path.");
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSongs ?? [])
        {
            string pathKey = song?.path;
            if (!string.IsNullOrWhiteSpace(pathKey)
                && chartsByPath.TryGetValue(pathKey, out ChartFile existingChart)
                && existingChart.Kind != ChartFileKind.Bmson)
            {
                throw new InvalidOperationException("Owned chart storage row upsert would replace a BMS row with a bmson row at the same path.");
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
        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            if (TryResolveCurrentChartByOwner(pathChange?.Chart, out _))
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
        if (!string.IsNullOrWhiteSpace(pathKey) && !string.Equals(pathKey, primaryKey, System.StringComparison.Ordinal))
        {
            keys.Add(pathKey);
        }
    }

    private sealed class RemovedChartKeySet
    {
        private readonly HashSet<BMSFile> bmsOwners = [];
        private readonly HashSet<LR2SongDBExtended.bmson_song> bmsonOwners = [];
        private readonly HashSet<string> bmsPaths = new(System.StringComparer.Ordinal);
        private readonly HashSet<string> bmsonPaths = new(System.StringComparer.Ordinal);

        private RemovedChartKeySet()
        {
        }

        public bool IsEmpty => bmsOwners.Count == 0
            && bmsonOwners.Count == 0
            && bmsPaths.Count == 0
            && bmsonPaths.Count == 0;

        public bool HasBmsKeys => bmsOwners.Count > 0 || bmsPaths.Count > 0;

        public static RemovedChartKeySet FromCharts(IEnumerable<ChartFile> charts, bool includePaths = true)
        {
            var keys = new RemovedChartKeySet();
            foreach (ChartFile chart in charts ?? [])
            {
                keys.AddChart(chart, includePaths);
            }
            return keys;
        }

        public bool ContainsDuplicateRow(DuplicateChartRow row)
        {
            if (row == null)
            {
                return false;
            }
            if (row.BmsFile != null && bmsOwners.Contains(row.BmsFile))
            {
                return true;
            }
            if (row.BmsonSong != null && bmsonOwners.Contains(row.BmsonSong))
            {
                return true;
            }
            if (bmsPaths.Count == 0 && bmsonPaths.Count == 0)
            {
                return false;
            }
            string pathKey = row.Path;
            if (string.IsNullOrWhiteSpace(pathKey))
            {
                return false;
            }
            return row.ChartKind switch
            {
                ChartFileKind.Bms => bmsPaths.Contains(pathKey),
                ChartFileKind.Bmson => bmsonPaths.Contains(pathKey),
                _ => false
            };
        }

        public bool ContainsBmsFile(BMSFile file)
        {
            if (file == null)
            {
                return false;
            }
            if (bmsOwners.Contains(file))
            {
                return true;
            }
            if (bmsPaths.Count == 0)
            {
                return false;
            }
            string pathKey = file.path;
            return !string.IsNullOrWhiteSpace(pathKey) && bmsPaths.Contains(pathKey);
        }

        private void AddChart(ChartFile chart, bool includePath)
        {
            if (chart == null)
            {
                return;
            }
            BMSFile bmsOwner = chart.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                AddBmsFile(bmsOwner, includePath);
                return;
            }
            LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
            if (bmsonOwner != null)
            {
                AddBmsonSong(bmsonOwner, includePath);
                return;
            }

            if (includePath)
            {
                AddPath(chart.Kind, GetCurrentPath(chart));
            }
        }

        private void AddBmsFile(BMSFile file, bool includePath = true)
        {
            if (file == null)
            {
                return;
            }
            bmsOwners.Add(file);
            if (includePath)
            {
                AddPath(ChartFileKind.Bms, file.path);
            }
        }

        private void AddBmsonSong(LR2SongDBExtended.bmson_song song, bool includePath = true)
        {
            if (song == null)
            {
                return;
            }
            bmsonOwners.Add(song);
            if (includePath)
            {
                AddPath(ChartFileKind.Bmson, song.path);
            }
        }

        private void AddPath(ChartFileKind kind, string path)
        {
            string pathKey = path;
            if (string.IsNullOrWhiteSpace(pathKey))
            {
                return;
            }
            if (kind == ChartFileKind.Bms)
            {
                bmsPaths.Add(pathKey);
            }
            else if (kind == ChartFileKind.Bmson)
            {
                bmsonPaths.Add(pathKey);
            }
        }
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
            snapshot.AddMd5(md5);
        }
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            snapshot.AddSha256(sha256);
        }
    }
}
