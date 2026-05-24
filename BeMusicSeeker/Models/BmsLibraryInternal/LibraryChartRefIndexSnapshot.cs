using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryChartRefIndexSnapshot
{
    private static readonly LibraryChartRefIndexSnapshot empty = new(
        new Dictionary<BMSFile, LibraryChartRef>(),
        new Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef>(),
        new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, List<LibraryChartRef>>(StringComparer.OrdinalIgnoreCase),
        [],
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<BMSFile, LibraryChartRef> bmsByReference;
    private readonly Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef> bmsonByReference;
    private readonly Dictionary<string, LibraryChartRef> byKindAndPath;
    private readonly HashSet<string> ambiguousKindAndPathKeys;
    private readonly Dictionary<string, List<LibraryChartRef>> directRefsByDirectory;
    private readonly List<string> sortedDirectDirectories;
    private readonly Dictionary<string, int> subtreeCountsByDirectory;
    private readonly Dictionary<string, int> pathCounts;

    private LibraryChartRefIndexSnapshot(
        Dictionary<BMSFile, LibraryChartRef> bmsByReference,
        Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef> bmsonByReference,
        Dictionary<string, LibraryChartRef> byKindAndPath,
        HashSet<string> ambiguousKindAndPathKeys,
        Dictionary<string, List<LibraryChartRef>> directRefsByDirectory,
        List<string> sortedDirectDirectories,
        Dictionary<string, int> subtreeCountsByDirectory,
        Dictionary<string, int> pathCounts)
    {
        this.bmsByReference = bmsByReference;
        this.bmsonByReference = bmsonByReference;
        this.byKindAndPath = byKindAndPath;
        this.ambiguousKindAndPathKeys = ambiguousKindAndPathKeys;
        this.directRefsByDirectory = directRefsByDirectory;
        this.sortedDirectDirectories = sortedDirectDirectories;
        this.subtreeCountsByDirectory = subtreeCountsByDirectory;
        this.pathCounts = pathCounts;
    }

    internal static LibraryChartRefIndexSnapshot Empty => empty;

    internal static LibraryChartRefIndexSnapshot FromStorageOwnerCharts(IEnumerable<ChartFile> charts)
    {
        return FromLibraryChartRefs((charts ?? [])
            .Select(CreateStorageOwnerRef)
            .Where(chart => chart != null));
    }

    internal static LibraryChartRefIndexSnapshot FromLibraryChartRefs(IEnumerable<LibraryChartRef> charts)
    {
        var bmsByReference = new Dictionary<BMSFile, LibraryChartRef>();
        var bmsonByReference = new Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef>();
        var byKindAndPath = new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
        var ambiguousKindAndPathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directRefsByDirectory = new Dictionary<string, List<LibraryChartRef>>(StringComparer.OrdinalIgnoreCase);
        var subtreeCountsByDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (LibraryChartRef chart in (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path)))
        {
            BMSFile bmsFile = chart.GetBmsStorageOwner();
            if (bmsFile != null && !bmsByReference.ContainsKey(bmsFile))
            {
                bmsByReference[bmsFile] = chart;
            }

            LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
            if (bmsonSong != null && !bmsonByReference.ContainsKey(bmsonSong))
            {
                bmsonByReference[bmsonSong] = chart;
            }

            string pathKey = CreatePathKey(chart.Path);
            if (!string.IsNullOrWhiteSpace(pathKey))
            {
                AddPathLookup(chart, pathKey, byKindAndPath, ambiguousKindAndPathKeys);
                Increment(pathCounts, pathKey);
            }

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
            }
        }

        List<string> sortedDirectDirectories = [.. directRefsByDirectory.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)];
        return new LibraryChartRefIndexSnapshot(
            bmsByReference,
            bmsonByReference,
            byKindAndPath,
            ambiguousKindAndPathKeys,
            directRefsByDirectory,
            sortedDirectDirectories,
            subtreeCountsByDirectory,
            pathCounts);
    }

    internal CanonicalChartResolveResult ResolveCanonicalCharts(IEnumerable<LibraryChartRef> inputCharts)
    {
        var result = new CanonicalChartResolveResult();
        var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            string canonicalKey = CreateKindAndPathKey(canonicalChart.Kind, canonicalChart.Path);
            if (addedKeys.Add(canonicalKey))
            {
                result.CanonicalCharts.Add(canonicalChart);
            }
        }
        return result;
    }

    internal List<LibraryChartRef> GetChartRefsUnderRealPath(string folderPath)
    {
        string folderKey = CreateDirectoryKey(folderPath);
        if (string.IsNullOrWhiteSpace(folderKey))
        {
            return [];
        }

        List<LibraryChartRef> refs = [];
        if (directRefsByDirectory.TryGetValue(folderKey, out List<LibraryChartRef> directRefs))
        {
            refs.AddRange(directRefs);
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

            refs.AddRange(directRefsByDirectory[directoryKey]);
        }
        return refs;
    }

    internal int CountChartRefsUnderRealPath(string folderPath, ISet<string> excludedPaths)
    {
        string folderKey = CreateDirectoryKey(folderPath);
        if (string.IsNullOrWhiteSpace(folderKey)
            || !subtreeCountsByDirectory.TryGetValue(folderKey, out int count))
        {
            return 0;
        }

        if (excludedPaths?.Count > 0 == true)
        {
            var excludedPathKeys = new HashSet<string>(
                excludedPaths
                    .Select(CreatePathKey)
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
            foreach (string pathKey in excludedPathKeys)
            {
                if (IsPathUnderDirectory(pathKey, folderKey)
                    && pathCounts.TryGetValue(pathKey, out int pathCount))
                {
                    count -= pathCount;
                }
            }
        }

        return Math.Max(0, count);
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
            return bmsChart;
        }

        LR2SongDBExtended.bmson_song bmsonSong = inputChart.GetBmsonStorageOwner();
        if (bmsonSong != null && bmsonByReference.TryGetValue(bmsonSong, out LibraryChartRef bmsonChart))
        {
            return bmsonChart;
        }

        string pathKey = CreateKindAndPathKey(inputChart.Kind, inputChart.Path);
        if (string.IsNullOrWhiteSpace(pathKey) || ambiguousKindAndPathKeys.Contains(pathKey))
        {
            return null;
        }

        return byKindAndPath.TryGetValue(pathKey, out LibraryChartRef pathChart)
            ? pathChart
            : null;
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

    private static LibraryChartRef CreateStorageOwnerRef(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return LibraryChartRef.FromBmsFile(bmsOwner);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        return bmsonOwner != null
            ? LibraryChartRef.FromBmsonSong(bmsonOwner)
            : LibraryChartRef.FromChartFile(chart);
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

    private static bool IsPathUnderDirectory(string pathKey, string folderKey)
    {
        return !string.IsNullOrWhiteSpace(pathKey)
            && !string.IsNullOrWhiteSpace(folderKey)
            && pathKey.StartsWith(AppendDirectorySeparator(folderKey), StringComparison.OrdinalIgnoreCase);
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string CreateDirectoryKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string pathKey = CreatePathKey(path);
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
}
