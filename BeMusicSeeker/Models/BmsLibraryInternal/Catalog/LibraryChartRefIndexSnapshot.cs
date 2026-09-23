using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILibraryChartCanonicalLookup
{
    CanonicalChartResolveResult ResolveCanonicalCharts(IEnumerable<LibraryChartRef> inputCharts);

    int CountChartRefsUnderRealPath(string folderPath, ISet<string> excludedPaths);
}

/// <summary>
/// BMS subtree count / range query が実際に訪問した範囲を集約する instance-local 診断です。
/// </summary>
/// <param name="subtreeCountQueryCount">subtree count query の呼出し回数。</param>
/// <param name="rangeQueryCount">range query の呼出し回数。</param>
/// <param name="rangeVisitedReferenceCount">range query が実際に訪問した chart ref 数。</param>
/// <param name="rangeReturnedPathCount">range query が返した exact path 数。</param>
internal sealed class LibraryChartRefIndexBmsQueryDiagnostics(
    int subtreeCountQueryCount,
    int rangeQueryCount,
    int rangeVisitedReferenceCount,
    int rangeReturnedPathCount)
{
    internal static LibraryChartRefIndexBmsQueryDiagnostics Empty { get; } = new(0, 0, 0, 0);

    internal int SubtreeCountQueryCount { get; } = subtreeCountQueryCount;

    internal int RangeQueryCount { get; } = rangeQueryCount;

    internal int RangeVisitedReferenceCount { get; } = rangeVisitedReferenceCount;

    internal int RangeReturnedPathCount { get; } = rangeReturnedPathCount;
}

internal sealed class LibraryChartRefIndexSnapshot : ILibraryChartCanonicalLookup
{
    private readonly Dictionary<BMSFile, LibraryChartRef> bmsByReference;
    private readonly Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef> bmsonByReference;
    private readonly Dictionary<string, LibraryChartRef> byKindAndPath;
    private readonly HashSet<string> ambiguousKindAndPathKeys;
    private readonly Dictionary<string, List<LibraryChartRef>> refsByPath;
    private readonly Dictionary<string, List<LibraryChartRef>> directRefsByDirectory;
    private readonly List<string> sortedDirectDirectories;
    private readonly Dictionary<string, int> subtreeCountsByDirectory;
    private readonly Dictionary<string, int> bmsSubtreeCountsByDirectory;
    private int bmsSubtreeCountQueryCount;
    private int bmsRangeQueryCount;
    private int bmsRangeVisitedReferenceCount;
    private int bmsRangeReturnedPathCount;

    private LibraryChartRefIndexSnapshot(
        Dictionary<BMSFile, LibraryChartRef> bmsByReference,
        Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef> bmsonByReference,
        Dictionary<string, LibraryChartRef> byKindAndPath,
        HashSet<string> ambiguousKindAndPathKeys,
        Dictionary<string, List<LibraryChartRef>> refsByPath,
        Dictionary<string, List<LibraryChartRef>> directRefsByDirectory,
        List<string> sortedDirectDirectories,
        Dictionary<string, int> subtreeCountsByDirectory,
        Dictionary<string, int> bmsSubtreeCountsByDirectory)
    {
        this.bmsByReference = bmsByReference;
        this.bmsonByReference = bmsonByReference;
        this.byKindAndPath = byKindAndPath;
        this.ambiguousKindAndPathKeys = ambiguousKindAndPathKeys;
        this.refsByPath = refsByPath;
        this.directRefsByDirectory = directRefsByDirectory;
        this.sortedDirectDirectories = sortedDirectDirectories;
        this.subtreeCountsByDirectory = subtreeCountsByDirectory;
        this.bmsSubtreeCountsByDirectory = bmsSubtreeCountsByDirectory;
    }

    internal static LibraryChartRefIndexSnapshot Empty => FromLibraryChartRefs([]);

    /// <summary>
    /// index に登録された path-aware chart ref 件数を返します。
    /// </summary>
    internal int ChartRefCount => refsByPath.Values.Sum(refs => refs?.Count ?? 0);

    /// <summary>
    /// 直接 chart refs を持つ real path directory bucket 数を返します。
    /// </summary>
    internal int DirectDirectoryCount => directRefsByDirectory.Count;

    /// <summary>
    /// subtree count を持つ real path directory bucket 数を返します。
    /// </summary>
    internal int SubtreeDirectoryCount => subtreeCountsByDirectory.Count;

    internal static LibraryChartRefIndexSnapshot FromStorageOwnerCharts(
        IEnumerable<ChartFile> charts,
        Action cancellationCheck = null)
    {
        return FromLibraryChartRefs((charts ?? [])
            .Select(CreateStorageOwnerRef)
            .Where(chart => chart != null),
            cancellationCheck);
    }

    internal static LibraryChartRefIndexSnapshot FromLibraryChartRefs(
        IEnumerable<LibraryChartRef> charts,
        Action cancellationCheck = null)
    {
        var bmsByReference = new Dictionary<BMSFile, LibraryChartRef>();
        var bmsonByReference = new Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef>();
        var byKindAndPath = new Dictionary<string, LibraryChartRef>(StringComparer.Ordinal);
        var ambiguousKindAndPathKeys = new HashSet<string>(StringComparer.Ordinal);
        var refsByPath = new Dictionary<string, List<LibraryChartRef>>(StringComparer.Ordinal);
        var directRefsByDirectory = new Dictionary<string, List<LibraryChartRef>>(StringComparer.OrdinalIgnoreCase);
        var subtreeCountsByDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bmsSubtreeCountsByDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (LibraryChartRef chart in (charts ?? []).Where(chart => chart != null))
        {
            cancellationCheck?.Invoke();

            string pathKey = CreatePathKey(chart.Path);
            if (string.IsNullOrWhiteSpace(pathKey))
            {
                continue;
            }

            AddOwnerReference(chart, bmsByReference, bmsonByReference, overwrite: false);
            AddPathLookup(chart, pathKey, byKindAndPath, ambiguousKindAndPathKeys);
            AddPathRefLookup(chart, pathKey, refsByPath);

            string directoryKey = CreateDirectoryKeyForFilePath(chart.Path);
            if (string.IsNullOrWhiteSpace(directoryKey))
            {
                continue;
            }

            if (!directRefsByDirectory.TryGetValue(directoryKey, out List<LibraryChartRef> directRefs))
            {
                directRefs = [];
                directRefsByDirectory[directoryKey] = directRefs;
            }
            directRefs.Add(chart);

            foreach (string ancestor in EnumerateDirectoryAndAncestors(directoryKey))
            {
                Increment(subtreeCountsByDirectory, ancestor);
                if (IsBmsChartRef(chart))
                {
                    Increment(bmsSubtreeCountsByDirectory, ancestor);
                }
            }
        }

        List<string> sortedDirectDirectories = [.. directRefsByDirectory.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)];
        return new LibraryChartRefIndexSnapshot(
            bmsByReference,
            bmsonByReference,
            byKindAndPath,
            ambiguousKindAndPathKeys,
            refsByPath,
            directRefsByDirectory,
            sortedDirectDirectories,
            subtreeCountsByDirectory,
            bmsSubtreeCountsByDirectory);
    }

    internal void RemoveCharts(IEnumerable<ChartFile> charts)
    {
        foreach (ChartFile chart in charts ?? [])
        {
            RemoveChart(chart);
        }
    }

    internal void AddCharts(IEnumerable<ChartFile> charts)
    {
        foreach (ChartFile chart in charts ?? [])
        {
            AddChart(chart);
        }
    }

    internal void MoveCharts(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            if (pathChange?.Chart == null)
            {
                continue;
            }

            string indexedOldPath = ResolveIndexedPath(pathChange.Chart, pathChange.OldPath);
            bool hasOldPath = !string.IsNullOrWhiteSpace(indexedOldPath);
            bool hasNewPath = !string.IsNullOrWhiteSpace(pathChange.NewPath);
            bool removed = hasOldPath && RemoveChart(pathChange.Chart, indexedOldPath);
            if (hasNewPath && (!hasOldPath || removed))
            {
                AddChart(pathChange.Chart, pathChange.NewPath);
            }
            else if (!hasNewPath && removed)
            {
                AddChart(pathChange.Chart, pathChange.NewPath);
            }
        }
    }

    /// <summary>
    /// 変更されたpathとそのdirectory bucketだけを、canonical sequenceの安定順で並べ直します。
    /// </summary>
    /// <param name="affectedPaths">変更されたexact path。</param>
    /// <param name="storageOrderComparison">canonical sequenceと同じ順序でrefを比較する関数。</param>
    internal void ReorderAffectedPathsByStorageOrder(
        IEnumerable<string> affectedPaths,
        Comparison<LibraryChartRef> storageOrderComparison)
    {
        var affectedPathKeys = new HashSet<string>(
            (affectedPaths ?? []).Select(CreatePathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        if (affectedPathKeys.Count == 0)
        {
            return;
        }

        var affectedDirectoryKeys = new HashSet<string>(
            affectedPathKeys.Select(CreateDirectoryKeyForFilePath).Where(directory => !string.IsNullOrWhiteSpace(directory)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string pathKey in affectedPathKeys)
        {
            if (refsByPath.TryGetValue(pathKey, out List<LibraryChartRef> refs))
            {
                SortRefsByStorageOrder(refs, storageOrderComparison);
            }
        }
        foreach (string directoryKey in affectedDirectoryKeys)
        {
            if (directRefsByDirectory.TryGetValue(directoryKey, out List<LibraryChartRef> refs))
            {
                SortRefsByStorageOrder(refs, storageOrderComparison);
            }
        }
    }

    internal CanonicalChartResolveResult ResolveCanonicalCharts(IEnumerable<LibraryChartRef> inputCharts)
    {
        var result = new CanonicalChartResolveResult();
        var addedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (LibraryChartRef inputChart in inputCharts ?? [])
        {
            result.InputCount++;
            if (inputChart?.GetBmsStorageOwner() == null && inputChart?.GetBmsonStorageOwner() == null)
            {
                result.PathOnlyInputCount++;
            }

            LibraryChartRef canonicalChart = ResolveCanonicalChart(inputChart);
            if (canonicalChart == null)
            {
                if (inputChart != null)
                {
                    result.UnresolvedCharts.Add(inputChart);
                }
                continue;
            }

            string canonicalKey = CreateCanonicalResultKey(canonicalChart);
            if (addedKeys.Add(canonicalKey))
            {
                result.CanonicalCharts.Add(canonicalChart);
            }
        }
        return result;
    }

    CanonicalChartResolveResult ILibraryChartCanonicalLookup.ResolveCanonicalCharts(IEnumerable<LibraryChartRef> inputCharts)
    {
        return ResolveCanonicalCharts(inputCharts);
    }

    internal List<LibraryChartRef> GetChartRefsUnderRealPath(string folderPath)
    {
        string folderKey = CreateDirectoryKey(folderPath);
        if (string.IsNullOrWhiteSpace(folderKey))
        {
            return [];
        }

        return [.. EnumerateChartRefsUnderDirectoryKey(folderKey).Where(HasCurrentPath)];
    }

    internal List<string> GetBmsChartPathsUnderRealPath(string folderPath)
    {
        string folderKey = CreateDirectoryKey(folderPath);
        if (string.IsNullOrWhiteSpace(folderKey))
        {
            return [];
        }

        Interlocked.Increment(ref bmsRangeQueryCount);
        if (!bmsSubtreeCountsByDirectory.ContainsKey(folderKey))
        {
            return [];
        }

        var result = new List<string>();
        var addedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (LibraryChartRef chart in EnumerateChartRefsUnderDirectoryKey(folderKey))
        {
            Interlocked.Increment(ref bmsRangeVisitedReferenceCount);
            if (!IsBmsChartRef(chart) || !HasCurrentPath(chart))
            {
                continue;
            }

            string path = chart.Path;
            if (!string.IsNullOrWhiteSpace(path) && addedPaths.Add(path))
            {
                result.Add(path);
                Interlocked.Increment(ref bmsRangeReturnedPathCount);
            }
        }
        return result;
    }

    /// <summary>
    /// この index instance の BMS 範囲 query 診断を返します。
    /// </summary>
    internal LibraryChartRefIndexBmsQueryDiagnostics CaptureBmsQueryDiagnostics()
    {
        return new LibraryChartRefIndexBmsQueryDiagnostics(
            Volatile.Read(ref bmsSubtreeCountQueryCount),
            Volatile.Read(ref bmsRangeQueryCount),
            Volatile.Read(ref bmsRangeVisitedReferenceCount),
            Volatile.Read(ref bmsRangeReturnedPathCount));
    }

    private IEnumerable<LibraryChartRef> EnumerateChartRefsUnderDirectoryKey(string folderKey)
    {
        if (directRefsByDirectory.TryGetValue(folderKey, out List<LibraryChartRef> directRefs))
        {
            foreach (LibraryChartRef chart in directRefs)
            {
                yield return chart;
            }
        }

        string descendantPrefix = AppendDirectorySeparator(folderKey);
        int index = LowerBound(sortedDirectDirectories, descendantPrefix);
        for (; index < sortedDirectDirectories.Count; index++)
        {
            string directoryKey = sortedDirectDirectories[index];
            if (!directoryKey.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.Equals(directoryKey, folderKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (LibraryChartRef chart in directRefsByDirectory[directoryKey])
            {
                yield return chart;
            }
        }
    }

    internal List<LibraryChartRef> GetDirectChartRefsInRealPaths(IEnumerable<string> folderPaths)
    {
        var refs = new List<LibraryChartRef>();
        var addedRefs = new HashSet<LibraryChartRef>();
        foreach (string folderPath in folderPaths ?? [])
        {
            string folderKey = CreateDirectoryKey(folderPath);
            if (string.IsNullOrWhiteSpace(folderKey)
                || !directRefsByDirectory.TryGetValue(folderKey, out List<LibraryChartRef> directRefs))
            {
                continue;
            }

            foreach (LibraryChartRef chart in directRefs)
            {
                if (HasCurrentPath(chart) && addedRefs.Add(chart))
                {
                    refs.Add(chart);
                }
            }
        }
        return refs;
    }

    internal List<LibraryChartRef> GetChartRefsByPaths(IEnumerable<string> paths)
    {
        var refs = new List<LibraryChartRef>();
        var addedRefs = new HashSet<LibraryChartRef>();
        foreach (string path in paths ?? [])
        {
            string pathKey = CreatePathKey(path);
            if (string.IsNullOrWhiteSpace(pathKey)
                || !refsByPath.TryGetValue(pathKey, out List<LibraryChartRef> pathRefs))
            {
                continue;
            }

            foreach (LibraryChartRef chart in pathRefs)
            {
                if (HasCurrentPath(chart) && addedRefs.Add(chart))
                {
                    refs.Add(chart);
                }
            }
        }
        return refs;
    }

    internal int CountChartRefsUnderRealPath(string folderPath, ISet<string> excludedPaths)
    {
        string folderKey = CreateDirectoryKey(folderPath);
        if (string.IsNullOrWhiteSpace(folderKey)
            || !subtreeCountsByDirectory.ContainsKey(folderKey))
        {
            return 0;
        }

        HashSet<string> excludedPathKeys = null;
        if (excludedPaths?.Count > 0 == true)
        {
            excludedPathKeys = new HashSet<string>(
                excludedPaths
                    .Select(CreatePathKey)
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.Ordinal);
        }

        int count = 0;
        foreach (LibraryChartRef chart in EnumerateChartRefsUnderDirectoryKey(folderKey))
        {
            if (!HasCurrentPath(chart))
            {
                continue;
            }

            string pathKey = CreatePathKey(chart.Path);
            if (excludedPathKeys?.Contains(pathKey) == true)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    internal int CountBmsChartRefsUnderRealPath(string folderPath)
    {
        Interlocked.Increment(ref bmsSubtreeCountQueryCount);
        string folderKey = CreateDirectoryKey(folderPath);
        return !string.IsNullOrWhiteSpace(folderKey)
            && bmsSubtreeCountsByDirectory.TryGetValue(folderKey, out int count)
                ? count
                : 0;
    }

    int ILibraryChartCanonicalLookup.CountChartRefsUnderRealPath(string folderPath, ISet<string> excludedPaths)
    {
        return CountChartRefsUnderRealPath(folderPath, excludedPaths);
    }

    private void AddChart(ChartFile chart, string pathOverride = null)
    {
        var chartRef = LibraryChartRef.FromStorageOwnerChartFile(chart, pathOverride);
        AddChartRef(chartRef);
    }

    private void AddChartRef(LibraryChartRef chart)
    {
        if (chart == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(chart.Path))
        {
            return;
        }

        AddOwnerReference(chart, bmsByReference, bmsonByReference, overwrite: true);
        string pathKey = CreatePathKey(chart.Path);
        if (!string.IsNullOrWhiteSpace(pathKey))
        {
            AddPathRefLookup(chart, pathKey, refsByPath);
            RebuildPathLookup(pathKey);
        }

        string directoryKey = CreateDirectoryKeyForFilePath(chart.Path);
        if (string.IsNullOrWhiteSpace(directoryKey))
        {
            return;
        }

        if (!directRefsByDirectory.TryGetValue(directoryKey, out List<LibraryChartRef> directRefs))
        {
            directRefs = [];
            directRefsByDirectory[directoryKey] = directRefs;
            InsertSortedDirectoryKey(directoryKey);
        }
        directRefs.Add(chart);

        foreach (string ancestor in EnumerateDirectoryAndAncestors(directoryKey))
        {
            Increment(subtreeCountsByDirectory, ancestor);
            if (IsBmsChartRef(chart))
            {
                Increment(bmsSubtreeCountsByDirectory, ancestor);
            }
        }
    }

    private bool RemoveChart(ChartFile chart, string pathOverride = null)
    {
        if (chart == null)
        {
            return false;
        }

        string pathKey = CreatePathKey(ResolveIndexedPath(chart, pathOverride));
        if (string.IsNullOrWhiteSpace(pathKey))
        {
            return RemoveOwnerReference(chart);
        }

        int removedFromPath = RemoveMatchingPathRefs(pathKey, chart);
        if (removedFromPath > 0)
        {
            RemoveOwnerReference(chart);

            RebuildPathLookup(pathKey);
        }

        string directoryKey = CreateDirectoryKeyForFilePath(pathKey);
        if (string.IsNullOrWhiteSpace(directoryKey))
        {
            return removedFromPath > 0;
        }

        int removedFromDirectory = RemoveMatchingDirectRefs(directoryKey, chart);
        if (removedFromDirectory <= 0)
        {
            return removedFromPath > 0;
        }

        foreach (string ancestor in EnumerateDirectoryAndAncestors(directoryKey))
        {
            Decrement(subtreeCountsByDirectory, ancestor, removedFromDirectory);
            if (chart.GetBmsStorageOwner() != null || chart.Kind == ChartFileKind.Bms)
            {
                Decrement(bmsSubtreeCountsByDirectory, ancestor, removedFromDirectory);
            }
        }
        return true;
    }

    private int RemoveMatchingPathRefs(string pathKey, ChartFile chart)
    {
        if (!refsByPath.TryGetValue(pathKey, out List<LibraryChartRef> refs))
        {
            return 0;
        }

        int removed = refs.RemoveAll(reference => IsSameChart(reference, chart));
        if (refs.Count == 0)
        {
            refsByPath.Remove(pathKey);
        }
        return removed;
    }

    private int RemoveMatchingDirectRefs(string directoryKey, ChartFile chart)
    {
        if (!directRefsByDirectory.TryGetValue(directoryKey, out List<LibraryChartRef> refs))
        {
            return 0;
        }

        int removed = refs.RemoveAll(reference => IsSameChart(reference, chart));
        if (refs.Count == 0)
        {
            directRefsByDirectory.Remove(directoryKey);
            RemoveSortedDirectoryKey(directoryKey);
        }
        return removed;
    }

    private void RebuildPathLookup(string pathKey)
    {
        RemoveKindPathLookup(LibraryChartKind.Bms, pathKey);
        RemoveKindPathLookup(LibraryChartKind.Bmson, pathKey);
        if (!refsByPath.TryGetValue(pathKey, out List<LibraryChartRef> refs))
        {
            return;
        }

        foreach (LibraryChartRef chart in refs)
        {
            AddPathLookup(chart, pathKey, byKindAndPath, ambiguousKindAndPathKeys);
        }
    }

    private void RemoveKindPathLookup(LibraryChartKind kind, string pathKey)
    {
        string key = CreateKindAndPathKey(kind, pathKey, pathIsCanonical: true);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        byKindAndPath.Remove(key);
        ambiguousKindAndPathKeys.Remove(key);
    }

    private void InsertSortedDirectoryKey(string directoryKey)
    {
        int index = LowerBound(sortedDirectDirectories, directoryKey);
        if (index >= sortedDirectDirectories.Count
            || !string.Equals(sortedDirectDirectories[index], directoryKey, StringComparison.OrdinalIgnoreCase))
        {
            sortedDirectDirectories.Insert(index, directoryKey);
        }
    }

    private void RemoveSortedDirectoryKey(string directoryKey)
    {
        int index = LowerBound(sortedDirectDirectories, directoryKey);
        if (index < sortedDirectDirectories.Count
            && string.Equals(sortedDirectDirectories[index], directoryKey, StringComparison.OrdinalIgnoreCase))
        {
            sortedDirectDirectories.RemoveAt(index);
        }
    }

    private string ResolveIndexedPath(ChartFile chart, string explicitOldPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitOldPath))
        {
            return explicitOldPath;
        }

        BMSFile bmsFile = chart?.GetBmsStorageOwner();
        if (bmsFile != null && bmsByReference.TryGetValue(bmsFile, out LibraryChartRef bmsRef))
        {
            return bmsRef.Path;
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart?.GetBmsonStorageOwner();
        if (bmsonSong != null && bmsonByReference.TryGetValue(bmsonSong, out LibraryChartRef bmsonRef))
        {
            return bmsonRef.Path;
        }

        return null;
    }

    private static void SortRefsByStorageOrder(
        List<LibraryChartRef> refs,
        Comparison<LibraryChartRef> storageOrderComparison)
    {
        refs?.Sort((left, right) => CompareByStorageOrder(left, right, storageOrderComparison));
    }

    private static int CompareByStorageOrder(
        LibraryChartRef left,
        LibraryChartRef right,
        Comparison<LibraryChartRef> storageOrderComparison)
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

        int orderCompare = storageOrderComparison?.Invoke(left, right) ?? 0;
        return orderCompare != 0
            ? orderCompare
            : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    }

    private static string CreateStorageIdentityKey(LibraryChartRef chart)
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

        return (chart.Kind == LibraryChartKind.Bmson ? "bmson-path:" : "bms-path:") + chart.Path;
    }

    private LibraryChartRef ResolveCanonicalChart(LibraryChartRef inputChart)
    {
        if (inputChart == null)
        {
            return null;
        }

        BMSFile bmsFile = inputChart.GetBmsStorageOwner();
        if (bmsFile != null && bmsByReference.TryGetValue(bmsFile, out LibraryChartRef bmsChart))
        {
            return HasCurrentPath(bmsChart) ? bmsChart : null;
        }

        LR2SongDBExtended.bmson_song bmsonSong = inputChart.GetBmsonStorageOwner();
        if (bmsonSong != null && bmsonByReference.TryGetValue(bmsonSong, out LibraryChartRef bmsonChart))
        {
            return HasCurrentPath(bmsonChart) ? bmsonChart : null;
        }

        string pathKey = CreateKindAndPathKey(inputChart.Kind, inputChart.Path);
        if (string.IsNullOrWhiteSpace(pathKey) || ambiguousKindAndPathKeys.Contains(pathKey))
        {
            return null;
        }

        return byKindAndPath.TryGetValue(pathKey, out LibraryChartRef pathChart) && HasCurrentPath(pathChart)
            ? pathChart
            : null;
    }

    private static bool HasCurrentPath(LibraryChartRef chart)
    {
        if (chart == null)
        {
            return false;
        }

        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return !string.IsNullOrWhiteSpace(bmsFile.path);
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        return bmsonSong != null
            ? !string.IsNullOrWhiteSpace(bmsonSong.path)
            : !string.IsNullOrWhiteSpace(chart.Path);
    }

    private static bool IsBmsChartRef(LibraryChartRef chart)
        => chart?.Kind == LibraryChartKind.Bms;

    private static void AddOwnerReference(
        LibraryChartRef chart,
        Dictionary<BMSFile, LibraryChartRef> bmsByReference,
        Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef> bmsonByReference,
        bool overwrite)
    {
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null && (overwrite || !bmsByReference.ContainsKey(bmsFile)))
        {
            bmsByReference[bmsFile] = chart;
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null && (overwrite || !bmsonByReference.ContainsKey(bmsonSong)))
        {
            bmsonByReference[bmsonSong] = chart;
        }
    }

    private bool RemoveOwnerReference(ChartFile chart)
    {
        bool removed = false;
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            removed |= bmsByReference.Remove(bmsFile);
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            removed |= bmsonByReference.Remove(bmsonSong);
        }
        return removed;
    }

    private static string CreateCanonicalResultKey(LibraryChartRef chart)
    {
        string kindAndPathKey = CreateKindAndPathKey(chart.Kind, chart.Path);
        if (!string.IsNullOrWhiteSpace(kindAndPathKey))
        {
            return kindAndPathKey;
        }

        string storageKey = CreateStorageIdentityKey(chart);
        return !string.IsNullOrWhiteSpace(storageKey)
            ? storageKey
            : "ref:" + RuntimeHelpers.GetHashCode(chart);
    }

    private static void AddPathLookup(
        LibraryChartRef chart,
        string pathKey,
        Dictionary<string, LibraryChartRef> byKindAndPath,
        HashSet<string> ambiguousKindAndPathKeys)
    {
        string key = CreateKindAndPathKey(chart.Kind, pathKey, pathIsCanonical: true);
        if (string.IsNullOrWhiteSpace(key) || ambiguousKindAndPathKeys.Contains(key))
        {
            return;
        }

        if (byKindAndPath.TryGetValue(key, out LibraryChartRef existing)
            && (!ReferenceEquals(existing.GetBmsStorageOwner(), chart.GetBmsStorageOwner())
                || !ReferenceEquals(existing.GetBmsonStorageOwner(), chart.GetBmsonStorageOwner())))
        {
            byKindAndPath.Remove(key);
            ambiguousKindAndPathKeys.Add(key);
            return;
        }

        byKindAndPath[key] = chart;
    }

    private static void AddPathRefLookup(
        LibraryChartRef chart,
        string pathKey,
        Dictionary<string, List<LibraryChartRef>> refsByPath)
    {
        if (chart == null || string.IsNullOrWhiteSpace(pathKey))
        {
            return;
        }
        if (!refsByPath.TryGetValue(pathKey, out List<LibraryChartRef> refs))
        {
            refs = [];
            refsByPath[pathKey] = refs;
        }
        refs.Add(chart);
    }

    private static LibraryChartRef CreateStorageOwnerRef(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return string.IsNullOrWhiteSpace(bmsOwner.path) ? null : LibraryChartRef.FromBmsFile(bmsOwner);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            return string.IsNullOrWhiteSpace(bmsonOwner.path) ? null : LibraryChartRef.FromBmsonSong(bmsonOwner);
        }
        return string.IsNullOrWhiteSpace(chart.Path) ? null : LibraryChartRef.FromChartFile(chart);
    }

    private static IEnumerable<string> EnumerateDirectoryAndAncestors(string directoryKey)
    {
        string current = directoryKey;
        while (!string.IsNullOrWhiteSpace(current))
        {
            yield return current;
            string parent = GetParentDirectoryKey(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }
            current = parent;
        }
    }

    private static string GetParentDirectoryKey(string directoryKey)
    {
        try
        {
            string parent = Path.GetDirectoryName(directoryKey);
            return CreateDirectoryKey(parent);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string AppendDirectorySeparator(string path)
    {
        return string.IsNullOrWhiteSpace(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static int LowerBound(List<string> values, string value)
    {
        int low = 0;
        int high = values?.Count ?? 0;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (StringComparer.OrdinalIgnoreCase.Compare(values[mid], value) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }

    private static string CreateKindAndPathKey(LibraryChartKind kind, string path)
    {
        return CreateKindAndPathKey(kind, CreatePathKey(path), pathIsCanonical: true);
    }

    private static string CreateKindAndPathKey(LibraryChartKind kind, string path, bool pathIsCanonical)
    {
        string pathKey = pathIsCanonical ? path : CreatePathKey(path);
        return string.IsNullOrWhiteSpace(pathKey) ? null : kind + "|" + pathKey;
    }

    private static string CreatePathKey(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string CreateDirectoryKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        // directory探索の正規化は維持し、行lookupのexact keyとは分ける。
        string pathKey = OwnedChartCollectionState.CreateOwnedPathKey(path);
        if (string.IsNullOrWhiteSpace(pathKey))
        {
            return null;
        }

        string root = Path.GetPathRoot(pathKey);
        string trimmed = pathKey.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return pathKey;
        }

        string rootTrimmed = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(rootTrimmed) && string.Equals(trimmed, rootTrimmed, StringComparison.OrdinalIgnoreCase)
            ? root
            : trimmed;
    }

    private static string CreateDirectoryKeyForFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return CreateDirectoryKey(Path.GetDirectoryName(path));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out int count);
        counts[key] = count + 1;
    }

    private static void Decrement(Dictionary<string, int> counts, string key, int value)
    {
        if (value <= 0 || !counts.TryGetValue(key, out int count))
        {
            return;
        }

        count -= value;
        if (count <= 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = count;
        }
    }

    private static bool IsSameChart(LibraryChartRef chartRef, ChartFile chart)
    {
        if (chartRef == null || chart == null)
        {
            return false;
        }

        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null || chartRef.GetBmsStorageOwner() != null)
        {
            return ReferenceEquals(chartRef.GetBmsStorageOwner(), bmsFile);
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null || chartRef.GetBmsonStorageOwner() != null)
        {
            return ReferenceEquals(chartRef.GetBmsonStorageOwner(), bmsonSong);
        }

        LibraryChartKind kind = chart.Kind == ChartFileKind.Bmson ? LibraryChartKind.Bmson : LibraryChartKind.Bms;
        return chartRef.Kind == kind
            && string.Equals(CreatePathKey(chartRef.Path), CreatePathKey(chart.Path), StringComparison.Ordinal);
    }
}
