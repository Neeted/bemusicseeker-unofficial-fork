using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>重複検索の共通値とハッシュ別の派生索引を不変に捕捉します。</summary>
internal sealed class OwnedDuplicateChartRowSnapshot
{
    private readonly ImmutableDictionary<string, ImmutableDictionary<OwnedChartToken, OrderedRow>> buckets;
    private IReadOnlyList<DuplicateHashBucket> duplicateBuckets;

    internal OwnedDuplicateChartRowSnapshot(IReadOnlyList<DuplicateChartRow> rows,
        IReadOnlyList<ChartFile> bmsCharts, Func<ChartFile, OwnedChartCanonicalOrderKey> order = null)
    {
        Rows = rows as DuplicateChartRow[] ?? [.. rows ?? []];
        BmsCharts = bmsCharts;
        var root = ImmutableDictionary.Create<string, ImmutableDictionary<OwnedChartToken, OrderedRow>>(StringComparer.OrdinalIgnoreCase);
        long ordinal = 0;
        foreach (DuplicateChartRow row in rows ?? [])
        {
            if (row == null || !row.HasChartSource || string.IsNullOrWhiteSpace(row.LookupHash))
            {
                continue;
            }
            OwnedChartToken token = row.Chart.Token ?? new OwnedChartToken();
            root.TryGetValue(row.LookupHash, out ImmutableDictionary<OwnedChartToken, OrderedRow> bucket);
            bucket ??= ImmutableDictionary<OwnedChartToken, OrderedRow>.Empty;
            root = root.SetItem(row.LookupHash, bucket.SetItem(token, new OrderedRow(
                order?.Invoke(row.Chart) ?? new OwnedChartCanonicalOrderKey(row.ChartKind, row.Path, ordinal++, false), row)));
        }
        buckets = root;
    }

    private OwnedDuplicateChartRowSnapshot(
        ImmutableDictionary<string, ImmutableDictionary<OwnedChartToken, OrderedRow>> buckets,
        CatalogStorageIndexedSequence<ChartFile> sequence, int bmsCount, IReadOnlyList<ChartFile> existingBmsCharts = null)
    {
        this.buckets = buckets;
        Rows = new DuplicateRowsView(new CatalogStorageReadOnlyView<ChartFile>(sequence));
        BmsCharts = existingBmsCharts ?? new CatalogStorageReadOnlyView<ChartFile>(sequence, 0, bmsCount);
    }

    internal IReadOnlyList<DuplicateChartRow> Rows { get; }
    internal IReadOnlyList<ChartFile> BmsCharts { get; }

    internal IReadOnlyList<DuplicateHashBucket> DuplicateHashBuckets => duplicateBuckets ??=
        [.. buckets.Where(pair => pair.Value.Count > 1)
            .Select(pair => new { pair.Key, Rows = pair.Value.Values.OrderBy(value => value.Order).ToArray() })
            .OrderBy(pair => pair.Rows[0].Order)
            .Select(pair => new DuplicateHashBucket(pair.Key, [.. pair.Rows.Select(value => value.Row)]))];

    internal int DuplicateHashCount => DuplicateHashBuckets.Count;
    internal int DuplicateHashRowCount => DuplicateHashBuckets.Sum(bucket => bucket.Rows.Count);

    /// <summary>影響したハッシュの要素だけを置換し、既存のsequenceを共有して捕捉します。</summary>
    internal OwnedDuplicateChartRowSnapshot Change(ChartFile chart, bool add,
        OwnedChartCanonicalOrderKey order, CatalogStorageIndexedSequence<ChartFile> sequence, int bmsCount)
    {
        string hash = chart.Md5;
        ImmutableDictionary<string, ImmutableDictionary<OwnedChartToken, OrderedRow>> root = buckets;
        if (!string.IsNullOrWhiteSpace(hash))
        {
            root.TryGetValue(hash, out ImmutableDictionary<OwnedChartToken, OrderedRow> bucket);
            bucket ??= ImmutableDictionary<OwnedChartToken, OrderedRow>.Empty;
            bucket = add
                ? bucket.SetItem(chart.Token, new OrderedRow(order, DuplicateChartRow.CreateFromChart(chart)))
                : bucket.Remove(chart.Token);
            root = bucket.Count == 0 ? root.Remove(hash) : root.SetItem(hash, bucket);
        }
        return new OwnedDuplicateChartRowSnapshot(root, sequence, bmsCount, chart.Kind == ChartFileKind.Bmson ? BmsCharts : null);
    }

    private readonly record struct OrderedRow(OwnedChartCanonicalOrderKey Order, DuplicateChartRow Row);

    private sealed class DuplicateRowsView(IReadOnlyList<ChartFile> charts) : IReadOnlyList<DuplicateChartRow>
    {
        public int Count => charts.Count;
        public DuplicateChartRow this[int index] => DuplicateChartRow.CreateFromChart(charts[index]);
        public IEnumerator<DuplicateChartRow> GetEnumerator()
        {
            foreach (ChartFile chart in charts)
            {
                yield return DuplicateChartRow.CreateFromChart(chart);
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

/// <summary>所持集合の共通現在値を捕捉します。保存行への参照は公開しません。</summary>
internal sealed class OwnedChartCollectionView
{
    private readonly IReadOnlyDictionary<ChartFileKind, ImmutableDictionary<string, OwnedChartToken>> paths;

    internal OwnedChartCollectionView(IReadOnlyList<ChartFile> bmsCharts,
        IReadOnlyList<ChartFile> bmsonCharts, IReadOnlyDictionary<ChartFileKind, ImmutableDictionary<string, OwnedChartToken>> paths)
    {
        BmsCharts = bmsCharts;
        BmsonCharts = bmsonCharts;
        this.paths = paths;
    }

    internal IReadOnlyList<ChartFile> BmsCharts { get; }
    internal IReadOnlyList<ChartFile> BmsonCharts { get; }
    internal int OwnerPathCount => paths.Values.Sum(index => index.Count);
    internal int Count => BmsCharts.Count + BmsonCharts.Count;

    /// <summary>DBの識別に使う、未加工のexact pathだけを照合します。</summary>
    internal bool ContainsOwnerPath(string path) => !string.IsNullOrWhiteSpace(path) && paths.Values.Any(index => index.ContainsKey(path));
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
    private OwnedChartCollectionView collectionView;
    private readonly Dictionary<OwnedChartToken, CatalogStorageSequenceEntry<ChartFile>> chartEntriesByChart = [];
    private ImmutableDictionary<ChartFileKind, ImmutableDictionary<string, OwnedChartToken>> chartsByPath =
        ImmutableDictionary<ChartFileKind, ImmutableDictionary<string, OwnedChartToken>>.Empty
            .Add(ChartFileKind.Bms, ImmutableDictionary.Create<string, OwnedChartToken>(StringComparer.Ordinal))
            .Add(ChartFileKind.Bmson, ImmutableDictionary.Create<string, OwnedChartToken>(StringComparer.Ordinal));
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

            entries.Add(CreateCanonicalEntry(chart.Token != null ? chart : chart with { Token = new OwnedChartToken() }));
        }

        chartSequence = CatalogStorageIndexedSequence<ChartFile>.FromEntries(
            entries,
            CompareRawCanonicalEntries,
            sequenceWorkObserver);
        chartEntriesByChart.Clear();
        foreach (CatalogStorageSequenceEntry<ChartFile> entry in entries)
        {
            chartEntriesByChart[entry.Value.Token] = entry;
        }
        bmsonNeedsCanonicalNormalization = entries.Any(entry => entry.Value?.Kind == ChartFileKind.Bmson);
        bmsonChartCount = entries.Count(entry => entry.Value?.Kind == ChartFileKind.Bmson);
        sequenceUsesCanonicalComparer = false;
        RefreshCanonicalSequenceView();
    }

    private void RefreshCanonicalSequenceView()
    {
        charts = new CatalogStorageReadOnlyView<ChartFile>(chartSequence);
        collectionView = null;
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

    /// <summary>共通基本値から所持集合を作り、従来の読込み除外件数を同時に返します。</summary>
    internal static OwnedChartCollectionState FromCharts(IEnumerable<ChartFile> charts,
        out OwnedChartStorageRowFilterSummary filterSummary)
    {
        OwnedChartCollectionState state = FromCharts(charts);
        filterSummary = state.FilterSummary;
        return state;
    }

    /// <summary>共通基本値から所持集合を作り、明示的な継承がない項目には新しい短命な識別を発行します。</summary>
    internal static OwnedChartCollectionState FromCharts(IEnumerable<ChartFile> charts,
        CancellationToken cancellationToken = default, ICatalogStorageSequenceWorkObserver sequenceWorkObserver = null)
    {
        List<ChartFile> input = [.. (charts ?? []).Where(chart => chart != null)];
        int[] skipped = new int[6];
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<ChartFile>(input.Count);
        foreach (ChartFile chart in input.Where(chart => chart.Kind == ChartFileKind.Bms)
            .Concat(input.Where(chart => chart.Kind == ChartFileKind.Bmson)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int kind = chart.Kind == ChartFileKind.Bms ? 0 : 1;
            if (string.IsNullOrWhiteSpace(chart.Path))
            {
                skipped[kind]++;
            }
            else if (string.IsNullOrWhiteSpace(chart.Md5))
            {
                skipped[2 + kind]++;
            }
            else if (!paths.Add(chart.Path))
            {
                skipped[4 + kind]++;
            }
            else
            {
                accepted.Add(chart);
            }
        }
        return new OwnedChartCollectionState(accepted, cancellationToken, sequenceWorkObserver)
        { FilterSummary = new(skipped[0], skipped[1], skipped[2], skipped[3], skipped[4], skipped[5]) };
    }

    /// <summary>読込み時の従来の除外条件と件数です。受理範囲を変更しません。</summary>
    internal OwnedChartStorageRowFilterSummary FilterSummary { get; private set; }

    /// <summary>所持集合へ新規または再解析の置換値を追加し、各項目の識別を新しくします。</summary>
    internal bool UpsertCharts(IEnumerable<ChartFile> values)
    {
        List<ChartFile> added = [.. (values ?? []).Where(chart => chart != null)];
        ValidateCharts(added);
        if (added.Count == 0)
        {
            return false;
        }
        List<string> normalizedPaths = [];
        bool normalized = EnsureCanonicalBmsonOrder(out normalizedPaths);
        if (normalized)
        {
            // 初回のBMSON順序確定だけは既存の全体正規化境界で派生順序も破棄します。
            duplicateChartRowSnapshot = null;
        }
        foreach (ChartFile value in added)
        {
            if (TryGetCurrentChartByExactPath(value.Path, out ChartFile previous))
            {
                libraryChartRefIndexSnapshot?.RemoveCharts([previous]);
                RemoveCanonicalChart(previous);
                UnregisterCurrentChartIndex(previous);
            }
            ChartFile next = value with { Token = new OwnedChartToken() };
            InsertCanonicalChart(next);
            RegisterCurrentChartIndex(next);
            libraryChartRefIndexSnapshot?.AddCharts([next]);
        }
        libraryChartRefIndexSnapshot?.ReorderAffectedPathsByStorageOrder(
            normalizedPaths.Concat(added.Select(chart => chart.Path)), CompareCanonicalChartRefs);
        return normalized;
    }

    /// <summary>一つの形式の明示再読込みだけを置換し、他形式の現在値・所属索引・順序列を共有します。</summary>
    internal void ReplaceKindCharts(ChartFileKind kind, IEnumerable<ChartFile> values)
    {
        OwnedChartCollectionState replacement = FromCharts((values ?? []).Where(chart => chart?.Kind == kind));
        OwnedChartCollectionView old = CreateCollectionView();
        ChartFile[] removed = [.. kind == ChartFileKind.Bms ? old.BmsCharts : old.BmsonCharts];
        foreach (ChartFile chart in removed)
        {
            RemoveCanonicalChart(chart);
            UnregisterCurrentChartIndex(chart);
        }
        if (kind == ChartFileKind.Bmson)
        {
            chartSequence = chartSequence.WithComparison(CompareRawCanonicalEntries);
            sequenceUsesCanonicalComparer = false;
            bmsonNeedsCanonicalNormalization = replacement.bmsonChartCount > 0;
        }
        int duplicateBmsonPaths = 0;
        foreach (ChartFile chart in replacement.charts)
        {
            if (TryGetCurrentChartByExactPath(chart.Path, out ChartFile existing))
            {
                // 読込み時の既存優先順はBMSが先であり、形式別置換でも同じ除外条件を保つ。
                duplicateBmsonPaths++;
                if (kind == ChartFileKind.Bmson)
                {
                    continue;
                }
                RemoveCanonicalChart(existing);
                UnregisterCurrentChartIndex(existing);
            }
            InsertCanonicalChart(chart);
            RegisterCurrentChartIndex(chart);
        }
        libraryChartRefIndexSnapshot = null;
        duplicateChartRowSnapshot = null;
        OwnedChartStorageRowFilterSummary summary = replacement.FilterSummary;
        FilterSummary = new(summary.PathlessBmsCount, summary.PathlessBmsonCount,
            summary.Md5lessBmsCount, summary.Md5lessBmsonCount,
            summary.DuplicatePathBmsCount, summary.DuplicatePathBmsonCount + duplicateBmsonPaths);
        RefreshCanonicalSequenceView();
    }

    /// <summary>現在の集合に適用する共通値の保存識別と形式の衝突を、DB確定前に検査します。</summary>
    internal void ValidateCharts(IEnumerable<ChartFile> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChartFile chart in values ?? [])
        {
            if (chart == null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(chart.Path) || string.IsNullOrWhiteSpace(chart.Md5) || !seen.Add(chart.Path))
            {
                throw new InvalidOperationException("Owned chart replacement requires unique exact paths and MD5 values.");
            }
            if (TryGetCurrentChartByExactPath(chart.Path, out ChartFile previous) && previous.Kind != chart.Kind)
            {
                throw new InvalidOperationException("Owned chart replacement cannot change the format at an existing exact path.");
            }
        }
    }

    /// <summary>現在の基本情報を投影し、入力譜面の取得済みリソース結果を共有します。</summary>
    internal List<ChartFile> CreateSnapshot(
        bool includeWarningSnapshot = false,
        bool includeScoreSnapshot = false)
    {
        return [.. charts
            .Where(HasCurrentPath)
            .Select(chart => CreateReadSnapshot(chart, includeWarningSnapshot, true, includeScoreSnapshot))
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
            .Select(chart => CreateCurrentValueSnapshot(
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
                includeScoreSnapshot: includeScoreSnapshot);
        }

        List<LibraryChartRef> refs = CreateLibraryChartRefIndexSnapshot().GetChartRefsUnderRealPath(directoryPath);
        return [.. refs
            .Select(chart => CreateCurrentValueSnapshot(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    /// <summary>物理フォルダ配下の現在値を、保存対象として捕捉します。</summary>
    internal ChartStorageTargetSet CreateStorageTargetsForSubtreeDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return ChartStorageTargetSet.FromCharts(charts);
        }
        List<LibraryChartRef> refs = CreateLibraryChartRefIndexSnapshot().GetChartRefsUnderRealPath(directoryPath);
        return ChartStorageTargetSet.FromCharts(refs.Select(ResolveCurrentChart));
    }

    /// <summary>指定MD5の基本情報を投影し、入力譜面の取得済みリソース結果を共有します。</summary>
    internal List<ChartFile> CreateSnapshotForMd5Hashes(
        ISet<string> md5Hashes,
        bool includeWarningSnapshot = false,
        bool includeScoreSnapshot = false)
    {
        if (md5Hashes == null || md5Hashes.Count == 0)
        {
            return [];
        }

        return [.. charts
            .Where(chart => HasCurrentOwnedIdentity(chart) && md5Hashes.Contains(GetCurrentMd5(chart)))
            .Select(chart => CreateReadSnapshot(chart, includeWarningSnapshot, true, includeScoreSnapshot))
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
            .Select(chart => CreateCurrentValueSnapshot(
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
            .Select(chart => ChartFileProjection.CaptureBasicSnapshot(
                chart,
                includeWarningSnapshot: false,
                includeScoreSnapshot: false))
            .Where(chart => chart != null)];
    }

    /// <summary>BMSの基本情報を投影し、入力譜面の取得済みリソース結果を共有します。</summary>
    internal List<ChartFile> CreateBmsSnapshot(
        bool includeWarningSnapshot = false,
        bool includeScoreSnapshot = false)
    {
        return [.. charts
            .Where(HasCurrentPath)
            .Where(chart => chart?.Kind == ChartFileKind.Bms)
            .Select(chart => CreateReadSnapshot(chart, includeWarningSnapshot, true, includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal LibraryChartRefIndexSnapshot CreateLibraryChartRefIndexSnapshot(Action cancellationCheck = null)
    {
        return libraryChartRefIndexSnapshot ??= LibraryChartRefIndexSnapshot.FromCharts(charts, cancellationCheck);
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
            if (!TryGetCurrentChartByExactPath(path, out ChartFile chart))
            {
                continue;
            }

            ChartFileKind kind = chart.Kind == ChartFileKind.Bmson
                ? ChartFileKind.Bmson
                : ChartFileKind.Bms;
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
    /// <param name="currentChartRef">現在の所持tokenと捕捉事実を持つ参照。</param>
    /// <param name="stableOrder">canonical sequence内の安定順。</param>
    /// <returns>一致する有効なchartが存在する場合はtrue。</returns>
    internal bool TryGetCanonicalChartRefForExactPath(
        ChartFileKind kind,
        string exactPath,
        out LibraryChartRef currentChartRef,
        out OwnedChartCanonicalOrderKey stableOrder)
    {
        currentChartRef = null;
        stableOrder = OwnedChartCanonicalOrderKey.Missing;
        if (string.IsNullOrWhiteSpace(exactPath)
            || !TryGetCurrentChartByExactPath(exactPath, out ChartFile chart)
            || (chart.Kind == ChartFileKind.Bmson) != (kind == ChartFileKind.Bmson))
        {
            return false;
        }

        return TryGetCanonicalChartRef(chart, out currentChartRef, out stableOrder);
    }

    /// <summary>
    /// ownerまたはexact pathを使って、現在のrefとcanonical sequenceの安定順を取得します。
    /// </summary>
    /// <param name="inputChartRef">owner refまたはpath ref。</param>
    /// <param name="currentChartRef">現在の所持tokenと捕捉事実を持つ参照。</param>
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
        if (inputChartRef?.Token != null)
        {
            if (chartEntriesByChart.TryGetValue(inputChartRef.Token, out CatalogStorageSequenceEntry<ChartFile> currentEntry))
            {
                chart = currentEntry.Value;
            }
            return chart != null;
        }
        if (chart == null
            && !string.IsNullOrWhiteSpace(inputChartRef?.Path)
            && TryGetCurrentChartByExactPath(inputChartRef.Path, out ChartFile pathChart)
            && (pathChart.Kind == ChartFileKind.Bmson) == (inputChartRef.Kind == ChartFileKind.Bmson))
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
        if (chart?.Token == null || !chartEntriesByChart.TryGetValue(chart.Token, out CatalogStorageSequenceEntry<ChartFile> entry))
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
            && chartEntriesByChart.TryGetValue(leftChart.Token, out CatalogStorageSequenceEntry<ChartFile> leftEntry)
            && chartEntriesByChart.TryGetValue(rightChart.Token, out CatalogStorageSequenceEntry<ChartFile> rightEntry))
        {
            return sequenceUsesCanonicalComparer
                ? CompareCanonicalEntries(leftEntry, rightEntry)
                : CompareRawCanonicalEntries(leftEntry, rightEntry);
        }
        return StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    }

    internal bool ContainsKnownChart(ChartFile chart)
    {
        var inputRef = LibraryChartRef.FromChartFile(chart);
        if (inputRef == null)
        {
            return false;
        }

        LibraryChartRefIndexSnapshot index = CreateLibraryChartRefIndexSnapshot();
        if (index.ResolveCanonicalCharts([inputRef]).CanonicalCharts.Count > 0)
        {
            return true;
        }

        return inputRef.Token == null && !string.IsNullOrWhiteSpace(inputRef.Path)
            && index.GetChartRefsByPaths([inputRef.Path]).Any(candidate => candidate?.Kind == inputRef.Kind);
    }


    /// <summary>
    /// playlist detail の hash 解決に使う chart fact snapshot を作成します。
    /// ref と canonical 順序を同じ走査で捕捉し、後続の差分更新へ可変な管理主体を渡しません。
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

            var fact = PlaylistLibraryResolveChartFact.FromChart(
                ChartFileProjection.CaptureBasicSnapshot(chart) ?? chart,
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
        foreach (ChartFile chart in charts)
        {
            if (HasCurrentOwnedIdentity(chart))
            {
                DuplicateChartRow row = CreateDuplicateChartRow(chart);
                if (row != null)
                {
                    rows.Add(row);
                }
            }
        }
        return new OwnedDuplicateChartRowSnapshot(rows, [.. rows.Where(row => row.ChartKind == ChartFileKind.Bms).Select(row => row.Chart)], GetCurrentDuplicateOrder);
    }

    private OwnedChartCanonicalOrderKey GetCurrentDuplicateOrder(ChartFile chart)
    {
        CatalogStorageSequenceEntry<ChartFile> entry = chartEntriesByChart[chart.Token];
        return new OwnedChartCanonicalOrderKey(chart.Kind, entry.SortKey, entry.Ordinal, sequenceUsesCanonicalComparer);
    }

    private void RebuildCurrentIndexes()
    {
        RebuildCurrentIndexes(CancellationToken.None);
    }

    private void RebuildCurrentIndexes(CancellationToken cancellationToken)
    {
        chartsByPath = chartsByPath.SetItem(ChartFileKind.Bms, chartsByPath[ChartFileKind.Bms].Clear())
            .SetItem(ChartFileKind.Bmson, chartsByPath[ChartFileKind.Bmson].Clear());
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

        string pathKey = GetCurrentPath(chart);
        if (string.IsNullOrWhiteSpace(pathKey))
        {
            return;
        }
        if (TryGetPathToken(pathKey, out OwnedChartToken existingToken) && existingToken != chart.Token)
        {
            throw new InvalidOperationException("Owned chart current paths must be unique.");
        }
        chartsByPath = chartsByPath.SetItem(chart.Kind, chartsByPath[chart.Kind].SetItem(pathKey, chart.Token));
        if (duplicateChartRowSnapshot != null)
        {
            duplicateChartRowSnapshot = duplicateChartRowSnapshot.Change(chart, true,
                GetCurrentDuplicateOrder(chart), chartSequence, chartSequence.Count - bmsonChartCount);
        }
    }

    private void UnregisterCurrentChartIndex(ChartFile chart)
    {
        if (chart == null)
        {
            return;
        }

        string pathKey = chart.Path;
        if (!string.IsNullOrWhiteSpace(pathKey))
        {
            if (chartsByPath[chart.Kind].TryGetValue(pathKey, out OwnedChartToken indexedToken) && indexedToken == chart.Token)
            {
                chartsByPath = chartsByPath.SetItem(chart.Kind, chartsByPath[chart.Kind].Remove(pathKey));
                if (duplicateChartRowSnapshot != null)
                {
                    duplicateChartRowSnapshot = duplicateChartRowSnapshot.Change(chart, false,
                        OwnedChartCanonicalOrderKey.Missing, chartSequence, chartSequence.Count - bmsonChartCount);
                }
            }
        }
    }

    /// <summary>LR2対象のBMS所属とDB exact pathの派生索引を全件複製せず捕捉します。同じ項目の値更新だけでは索引を差し替えません。</summary>
    internal IReadOnlyDictionary<string, OwnedChartToken> CapturePathMembershipIndex() => chartsByPath[ChartFileKind.Bms];

    private bool TryGetPathToken(string path, out OwnedChartToken token)
        => chartsByPath[ChartFileKind.Bms].TryGetValue(path, out token)
            || chartsByPath[ChartFileKind.Bmson].TryGetValue(path, out token);

    private bool TryGetCurrentChartByExactPath(string path, out ChartFile chart)
    {
        chart = null;
        if (!TryGetPathToken(path, out OwnedChartToken token)
            || !chartEntriesByChart.TryGetValue(token, out CatalogStorageSequenceEntry<ChartFile> entry))
        {
            return false;
        }
        chart = entry.Value;
        return true;
    }

    private bool TryResolveCurrentChartByOwner(ChartFile inputChart, out ChartFile currentChart)
    {
        currentChart = null;
        if (inputChart?.Token != null && chartEntriesByChart.TryGetValue(inputChart.Token, out CatalogStorageSequenceEntry<ChartFile> currentEntry))
        {
            currentChart = currentEntry.Value;
            return true;
        }
        return false;
    }

    private bool TryResolveRemoveRequest(OwnedChartRemoveRequest request, out ChartFile currentChart)
    {
        currentChart = null;
        if (request == null)
        {
            return false;
        }
        if (request.Mode == OwnedChartRemoveMode.Item)
        {
            if (request.Token != null && chartEntriesByChart.TryGetValue(request.Token,
                out CatalogStorageSequenceEntry<ChartFile> entry) && entry.Value.Kind == request.Kind)
            {
                currentChart = entry.Value;
                return true;
            }
            return false;
        }

        string pathKey = request.Path;
        if (string.IsNullOrWhiteSpace(pathKey) || !TryGetCurrentChartByExactPath(pathKey, out currentChart))
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

    /// <summary>現在の所持項目を形式ごとに捕捉します。</summary>
    internal OwnedChartCollectionView CreateCollectionView() => CreateCollectionView(sortBmsonByPath: false);

    /// <summary>通常一覧の初期構築に使う共通現在値を捕捉します。</summary>
    internal OwnedChartCollectionView CreateNormalLibrarySourceChartView() => CreateCollectionView(sortBmsonByPath: true);

    private OwnedChartCollectionView CreateCollectionView(bool sortBmsonByPath)
    {
        if (collectionView == null)
        {
            int bmsCount = chartSequence.Count - bmsonChartCount;
            collectionView = new OwnedChartCollectionView(
                new CatalogStorageReadOnlyView<ChartFile>(chartSequence, 0, bmsCount),
                new CatalogStorageReadOnlyView<ChartFile>(chartSequence, bmsCount, bmsonChartCount), chartsByPath);
        }
        return sortBmsonByPath
            ? new OwnedChartCollectionView(collectionView.BmsCharts,
                [.. collectionView.BmsonCharts.OrderBy(chart => chart.Path, StringComparer.OrdinalIgnoreCase)], chartsByPath)
            : collectionView;
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

    /// <summary>
    /// 現在のcanonical collectionからinstalled chart lookupの初期stateを構築します。
    /// </summary>
    /// <param name="bmsCount">取り込んだBMS chart数。</param>
    /// <param name="bmsonCount">取り込んだBMSON chart数。</param>
    /// <param name="storeWorkObserver">実際のchart格納単位を観測する内部observer。指定しない場合は観測しません。</param>
    /// <returns>現在のcollectionを反映したinstalled lookup state。</returns>
    internal InstalledChartLookupIndexState CreateInstalledChartLookupIndexState(
        out int bmsCount,
        out int bmsonCount,
        Action<string> storeWorkObserver = null)
    {
        var state = new InstalledChartLookupIndexState
        {
            StoreWorkObserver = storeWorkObserver
        };
        bmsCount = 0;
        bmsonCount = 0;
        foreach (ChartFile chart in charts.Where(chart => chart != null))
        {
            if (!HasCurrentOwnedIdentity(chart))
            {
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

    private static DuplicateChartRow CreateDuplicateChartRow(ChartFile chart) => DuplicateChartRow.CreateFromChart(chart);

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
            .GroupBy(change => (change.Chart.Token, change.NewPath))
            .Select(group => group.First())];
        if (currentPathChanges.Any(change => string.IsNullOrWhiteSpace(change.NewPath)))
        {
            throw new InvalidOperationException("Owned chart path changes must keep a non-empty path.");
        }
        var changingCharts = new HashSet<OwnedChartToken>();
        var newPathKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (LibraryChartPathChange pathChange in currentPathChanges)
        {
            if (!TryResolveCurrentChartByOwner(pathChange.Chart, out ChartFile currentChart))
            {
                continue;
            }
            changingCharts.Add(currentChart.Token);
            string newPathKey = pathChange.NewPath;
            if (string.IsNullOrWhiteSpace(newPathKey))
            {
                throw new InvalidOperationException("Owned chart path changes must keep a non-empty path.");
            }
            if (!newPathKeys.Add(newPathKey))
            {
                throw new InvalidOperationException("Owned chart path changes must keep unique target paths.");
            }
            if (TryGetCurrentChartByExactPath(newPathKey, out ChartFile existingChart)
                && existingChart.Token != currentChart.Token
                && !changingCharts.Contains(existingChart.Token)
                && !currentPathChanges.Any(change => TryResolveCurrentChartByOwner(change.Chart, out ChartFile changingChart) && changingChart.Token == existingChart.Token))
            {
                throw new InvalidOperationException("Owned chart path change would collide with another owned chart.");
            }
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
        foreach (LibraryChartDigestChange change in digestChanges ?? [])
        {
            if (change == null || !change.HasDigestChange
                || !TryGetCurrentChartByExactPath(change.Path, out ChartFile current)
                || (current.Kind == ChartFileKind.Bmson) != (change.Kind == ChartFileKind.Bmson))
            {
                continue;
            }
            if (!string.Equals(current.Md5, change.NewMd5, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.Sha256, change.NewSha256, StringComparison.OrdinalIgnoreCase))
            {
                ApplyCurrentChartValue(current with { Md5 = change.NewMd5, Sha256 = change.NewSha256 });
            }
        }
    }

    private void ApplyCurrentPathIndexChanges(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        var replacements = new List<(ChartFile Current, ChartFile Next)>();
        foreach (LibraryChartPathChange change in pathChanges ?? [])
        {
            if (TryResolveCurrentChartByOwner(change?.Chart, out ChartFile current))
            {
                ChartFile next = string.Equals(change.Chart.Path, change.NewPath, StringComparison.Ordinal)
                    ? change.Chart : ChartFileProjection.WithPath(current, change.NewPath);
                replacements.Add((current, next));
            }
        }
        foreach ((ChartFile Current, ChartFile Next) replacement in replacements)
        {
            UnregisterCurrentChartIndex(replacement.Current);
        }
        foreach ((ChartFile Current, ChartFile Next) replacement in replacements)
        {
            CatalogStorageSequenceEntry<ChartFile> previous = chartEntriesByChart[replacement.Current.Token];
            int position = chartSequence.FindIndex(previous);
            // 並び順は既存の位置を継承し、DB照合用exact pathだけを更新します。
            var nextEntry = new CatalogStorageSequenceEntry<ChartFile>(replacement.Next,
                replacement.Next.Path, previous.SortKey, previous.Ordinal);
            chartSequence = chartSequence.ReplaceAt(position, nextEntry);
            chartEntriesByChart[replacement.Current.Token] = nextEntry;
        }
        foreach ((ChartFile Current, ChartFile Next) replacement in replacements)
        {
            RegisterCurrentChartIndex(replacement.Next);
        }
        if (replacements.Any(replacement => replacement.Current.Kind == ChartFileKind.Bmson))
        {
            // 移転直後の位置は保持し、次の upsert で現在パスの canonical 順を確定します。
            bmsonNeedsCanonicalNormalization = true;
        }
        RefreshCanonicalSequenceView();
    }

    /// <summary>前回重複警告を付けた識別にだけ警告解除を適用します。識別から内容を返す窓口は作りません。</summary>
    internal void ClearDuplicateWarnings(IEnumerable<OwnedChartToken> tokens)
    {
        foreach (OwnedChartToken token in tokens ?? [])
        {
            if (token != null && chartEntriesByChart.TryGetValue(token, out CatalogStorageSequenceEntry<ChartFile> entry))
            {
                ApplyCurrentChartValue(entry.Value with { Warnings = [.. entry.Value.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart)] });
            }
        }
    }

    /// <summary>同じ所持識別へ通常の現在値を適用します。捕捉済み読取り値と順序位置は変更しません。</summary>
    internal bool ApplyCurrentChartValue(ChartFile value)
    {
        if (value?.Token == null || !chartEntriesByChart.TryGetValue(value.Token, out CatalogStorageSequenceEntry<ChartFile> entry))
        {
            return false;
        }
        ChartFile current = entry.Value;
        if (current.Kind != value.Kind || !string.Equals(current.Path, value.Path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A current chart value must keep its kind and exact path.");
        }
        bool digestChanged = !string.Equals(current.Md5, value.Md5, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Sha256, value.Sha256, StringComparison.OrdinalIgnoreCase);
        ChartFile next = digestChanged ? value with { Score = ChartScoreSnapshot.NoScore(value.Path) } : value;
        if (digestChanged && next.ChartInfo != null
            && (!string.Equals(next.ChartInfo.md5, next.Md5, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(next.ChartInfo.sha256, next.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            next = ChartFileProjection.WithChartInfo(next, null);
        }
        if (current == next)
        {
            return true;
        }
        if (digestChanged)
        {
            libraryChartRefIndexSnapshot?.RemoveCharts([current]);
        }
        ReplaceCurrentChartValue(next);
        if (digestChanged)
        {
            libraryChartRefIndexSnapshot?.AddCharts([next]);
        }
        return true;
    }

    private void ReplaceCurrentChartValue(ChartFile next)
    {
        CatalogStorageSequenceEntry<ChartFile> previous = chartEntriesByChart[next.Token];
        int position = chartSequence.FindIndex(previous);
        var replacement = new CatalogStorageSequenceEntry<ChartFile>(next, previous.ExactPath, previous.SortKey, previous.Ordinal);
        if (duplicateChartRowSnapshot != null)
        {
            duplicateChartRowSnapshot = duplicateChartRowSnapshot.Change(previous.Value, false,
                OwnedChartCanonicalOrderKey.Missing, chartSequence, chartSequence.Count - bmsonChartCount);
        }
        chartSequence = chartSequence.ReplaceAt(position, replacement);
        chartEntriesByChart[next.Token] = replacement;
        RegisterCurrentChartIndex(next);
        RefreshCanonicalSequenceView();
    }
    /// <summary>現在のowner参照または指定された旧exact keyだけを正本と索引から取り除きます。</summary>
    internal int RemoveChartRequests(IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        List<ChartFile> actualRemovedCharts = [];
        var actualRemovedSet = new HashSet<OwnedChartToken>();
        foreach (OwnedChartRemoveRequest request in removeRequests ?? [])
        {
            if (TryResolveRemoveRequest(request, out ChartFile currentChart) && actualRemovedSet.Add(currentChart.Token))
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
            libraryChartRefIndexSnapshot?.RemoveCharts(removedCharts);
        }
        return removed;
    }

    /// <summary>
    /// 削除要求を現在のcanonical chartへ解決し、反映用の不変identity factsを確定します。
    /// </summary>
    /// <param name="removeRequests">owner参照またはDB exact pathを指定する削除要求。</param>
    /// <returns>現在のcollectionで解決できたowner要求と、解決できないDB-only path cleanup。</returns>
    internal List<OwnedChartRemoveRequest> ResolveCurrentRemoveRequests(IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        var resolvedRequests = new List<OwnedChartRemoveRequest>();
        var resolvedCharts = new HashSet<OwnedChartToken>();
        foreach (OwnedChartRemoveRequest request in removeRequests ?? [])
        {
            if (request?.Mode == OwnedChartRemoveMode.PathCleanup)
            {
                if (TryResolveRemoveRequest(request, out ChartFile resolvedPathChart))
                {
                    var resolvedRequest =
                        OwnedChartRemoveRequest.FromResolvedPathCleanup(resolvedPathChart);
                    if (resolvedRequest != null && resolvedCharts.Add(resolvedPathChart.Token))
                    {
                        resolvedRequests.Add(resolvedRequest);
                    }
                }
                else
                {
                    // DB-only cleanup keeps its original exact path when the
                    // current owned collection cannot resolve a chart.
                    resolvedRequests.Add(request);
                }
                continue;
            }
            if (!TryResolveRemoveRequest(request, out ChartFile currentChart) || !resolvedCharts.Add(currentChart.Token))
            {
                continue;
            }

            resolvedRequests.Add(request);
        }
        return resolvedRequests;
    }

    /// <summary>
    /// 既存BMSON項目の順序を初回だけ正規化し、その際に順序を確認したパスを返します。
    /// </summary>
    /// <param name="normalizedPaths">初回正規化の対象となったBMSON項目のパス。正規化を省略した場合は空です。</param>
    /// <returns>既存BMSON suffixの初回canonical正規化を実施した場合は<see langword="true"/>。</returns>
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
            chartEntriesByChart[entry.Value.Token] = entry;
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
        // BMS は新規の順序番号で BMS 区間の末尾へ入るため、既知の区間境界をそのまま使います。
        int insertionIndex = chart.Kind == ChartFileKind.Bms
            ? chartSequence.Count - bmsonChartCount
            : chartSequence.FindInsertionIndex(entry);
        chartSequence = chartSequence.InsertAt(insertionIndex, entry);
        chartEntriesByChart[chart.Token] = entry;
        if (chart.Kind == ChartFileKind.Bmson)
        {
            bmsonChartCount++;
        }
        RefreshCanonicalSequenceView();
    }

    private bool RemoveCanonicalChart(ChartFile chart)
    {
        if (chart?.Token == null || !chartEntriesByChart.TryGetValue(chart.Token, out CatalogStorageSequenceEntry<ChartFile> entry))
        {
            return false;
        }

        int index = chartSequence.FindIndex(entry);
        if (index < 0)
        {
            throw new InvalidOperationException("Owned chart canonical sequence entry is missing.");
        }
        chartSequence = chartSequence.RemoveAt(index);
        chartEntriesByChart.Remove(chart.Token);
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

    private static string GetCurrentDirectory(ChartFile chart)
    {
        string path = GetCurrentPath(chart);
        return string.IsNullOrWhiteSpace(path) ? null : DirectoryExt.GetDirectoryNameSimple(path);
    }

    private static string GetCurrentPath(ChartFile chart) => chart?.Path;

    private static bool HasCurrentPath(ChartFile chart)
        => !string.IsNullOrWhiteSpace(GetCurrentPath(chart));

    private static bool HasCurrentOwnedIdentity(ChartFile chart)
        => HasCurrentPath(chart) && !string.IsNullOrWhiteSpace(GetCurrentMd5(chart));

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
            if (TryResolveCurrentChartByOwner(pathChange?.Chart, out ChartFile current))
            {
                yield return new LibraryChartPathChange
                {
                    Chart = pathChange.Chart,
                    OldPath = pathChange.OldPath,
                    NewPath = pathChange.NewPath
                };
            }
        }
    }

    private static LibraryChartRef CreateCurrentLibraryChartRef(ChartFile chart)
        => chart == null ? null : LibraryChartRef.FromChartFile(chart with
        {
            Path = GetCurrentPath(chart),
            Md5 = GetCurrentMd5(chart),
            Sha256 = GetCurrentSha256(chart)
        });
    private static string GetCurrentMd5(ChartFile chart) => chart?.Md5;

    private static string GetCurrentSha256(ChartFile chart) => chart?.Sha256;

    /// <summary>指定した所持識別だけで現在値を解決し、識別がない要求だけkindとDBのexact pathを使います。</summary>
    internal ChartFile ResolveCurrentChart(LibraryChartRef chart)
    {
        TryResolveCanonicalChartRef(chart, out ChartFile current);
        return current;
    }

    private ChartFile CreateCurrentValueSnapshot(LibraryChartRef chart,
        bool includeWarningSnapshot, bool includeResourceReferences, bool includeScoreSnapshot)
        => CreateReadSnapshot(ResolveCurrentChart(chart), includeWarningSnapshot, includeResourceReferences, includeScoreSnapshot);

    private static ChartFile CreateReadSnapshot(ChartFile current,
        bool includeWarningSnapshot, bool includeResourceReferences, bool includeScoreSnapshot)
        => current == null ? null : current with
        {
            Warnings = includeWarningSnapshot ? current.Warnings : [],
            Resources = includeResourceReferences ? current.Resources : null,
            Score = includeScoreSnapshot ? current.Score : ChartScoreSnapshot.NoScore(current.Path)
        };

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
        AddRuntimeStateKeys(keys, chart.Kind, chart.Path, chart.Md5, chart.Sha256);
    }

    private static string CreateCurrentRuntimeStatePrimaryKey(ChartFile chart) => ChartFileRuntimeStateKey.Create(chart);

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
        private readonly HashSet<OwnedChartToken> tokens = [];
        private readonly HashSet<string> bmsPaths = new(System.StringComparer.Ordinal);
        private readonly HashSet<string> bmsonPaths = new(System.StringComparer.Ordinal);

        private RemovedChartKeySet()
        {
        }

        public bool IsEmpty => tokens.Count == 0
            && bmsPaths.Count == 0
            && bmsonPaths.Count == 0;

        public bool HasBmsKeys => tokens.Count > 0 || bmsPaths.Count > 0;

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
            if (row.Chart?.Token != null && tokens.Contains(row.Chart.Token))
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

        private void AddChart(ChartFile chart, bool includePath)
        {
            if (chart == null)
            {
                return;
            }
            if (chart.Token != null)
            {
                tokens.Add(chart.Token);
            }
            if (includePath)
            {
                AddPath(chart.Kind, GetCurrentPath(chart));
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
