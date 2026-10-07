using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallDestinationOverlayChartRefSnapshot
{
    private static readonly InstallDestinationOverlayChartRefSnapshot empty = new(
        new Dictionary<string, List<ChartFile>>(StringComparer.OrdinalIgnoreCase),
        []);

    private readonly Dictionary<string, List<ChartFile>> refsByInstallDestinationDirectory;
    private readonly List<string> sortedInstallDestinationDirectories;

    private InstallDestinationOverlayChartRefSnapshot(
        Dictionary<string, List<ChartFile>> refsByInstallDestinationDirectory,
        List<string> sortedInstallDestinationDirectories)
    {
        this.refsByInstallDestinationDirectory = refsByInstallDestinationDirectory ?? new Dictionary<string, List<ChartFile>>(StringComparer.OrdinalIgnoreCase);
        this.sortedInstallDestinationDirectories = sortedInstallDestinationDirectories ?? [];
    }

    internal static InstallDestinationOverlayChartRefSnapshot Empty => empty;

    /// <summary>
    /// overlay snapshot に含まれる chart ref 件数を返します。
    /// duplicate merge prepare の cold path 計測で、snapshot の規模をログへ残すために公開しています。
    /// </summary>
    internal int ChartCount => refsByInstallDestinationDirectory.Values.Sum(refs => refs?.Count ?? 0);

    internal int DirectoryCount => refsByInstallDestinationDirectory.Count;

    internal static InstallDestinationOverlayChartRefSnapshot FromCharts(IEnumerable<ChartFile> charts)
    {
        return FromLibraryChartRefs((charts ?? [])
            .Where(chart => chart != null));
    }

    internal static InstallDestinationOverlayChartRefSnapshot FromLibraryChartRefs(IEnumerable<ChartFile> charts)
    {
        List<ChartFile> refs = [.. (charts ?? [])
            .Where(chart => chart != null)
            .GroupBy(CreateRuntimeKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group.First())];
        if (refs.Count == 0)
        {
            return Empty;
        }

        var refsByDirectory = new Dictionary<string, List<ChartFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in refs)
        {
            string directoryKey = CreateDirectoryKey(chart.InstallDestination);
            if (string.IsNullOrWhiteSpace(directoryKey))
            {
                continue;
            }

            if (!refsByDirectory.TryGetValue(directoryKey, out List<ChartFile> directoryRefs))
            {
                directoryRefs = [];
                refsByDirectory[directoryKey] = directoryRefs;
            }
            directoryRefs.Add(chart);
        }

        if (refsByDirectory.Count == 0)
        {
            return Empty;
        }

        return new InstallDestinationOverlayChartRefSnapshot(
            refsByDirectory,
            [.. refsByDirectory.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]);
    }

    internal List<ChartFile> GetChartsUnderInstallDestination(string folderPath)
    {
        string folderKey = CreateDirectoryKey(folderPath);
        if (string.IsNullOrWhiteSpace(folderKey))
        {
            return [];
        }

        List<ChartFile> refs = [];
        if (refsByInstallDestinationDirectory.TryGetValue(folderKey, out List<ChartFile> directRefs))
        {
            refs.AddRange(directRefs);
        }

        string descendantPrefix = AppendDirectorySeparator(folderKey);
        int index = LowerBound(sortedInstallDestinationDirectories, descendantPrefix);
        for (; index < sortedInstallDestinationDirectories.Count; index++)
        {
            string directoryKey = sortedInstallDestinationDirectories[index];
            if (!directoryKey.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.Equals(directoryKey, folderKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            refs.AddRange(refsByInstallDestinationDirectory[directoryKey]);
        }
        return refs;
    }

    /// <summary>導入先投影から要求に必要な不変識別だけを返します。</summary>
    internal List<LibraryChartRef> GetChartRefsUnderInstallDestination(string folderPath)
        => [.. GetChartsUnderInstallDestination(folderPath).Select(chart => LibraryChartRef.FromChartFile(chart))];
    private static string CreateRuntimeKey(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        ChartFileKind chartKind = chart.Kind;
        string primaryKey = ChartFileRuntimeStateKey.Create(chartKind, chart.Path, chart.Md5, chart.Sha256);
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            return primaryKey;
        }

        return ChartFileRuntimeStateKey.CreatePathKey(chartKind, chart.Path);
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

    private static string CreateDirectoryKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            string pathKey = Path.GetFullPath(path.Trim());
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
        catch
        {
            return path.Trim();
        }
    }
}
