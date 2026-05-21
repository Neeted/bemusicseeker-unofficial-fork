using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class LibraryRowCacheBuildStats
{
    internal int HitCount { get; set; }

    internal int MissCount { get; set; }

    internal int PrunedCount { get; set; }
}

internal sealed class NormalLibraryRowCache
{
    private readonly Dictionary<BMSFile, LibraryChartRow> rowsByFile = new(BmsFileReferenceComparer.Instance);

    internal NormalLibraryRowCache()
    {
    }

    internal int Count => rowsByFile.Count;

    internal LibraryChartRow GetOrCreate(ChartFile chart, LibraryRowCacheBuildStats stats)
    {
        BMSFile file = chart?.GetBmsStorageOwner();
        if (file == null)
        {
            return null;
        }
        if (rowsByFile.TryGetValue(file, out LibraryChartRow row))
        {
            if (stats != null)
            {
                stats.HitCount++;
            }
            return row;
        }
        row = LibraryChartRow.FromBmsFile(file);
        if (row == null)
        {
            return null;
        }
        rowsByFile[file] = row;
        if (stats != null)
        {
            stats.MissCount++;
        }
        return row;
    }

    internal int Prune(IEnumerable<ChartFile> currentCharts)
    {
        var current = new HashSet<BMSFile>(
            GetBmsStorageOwners(currentCharts),
            BmsFileReferenceComparer.Instance);
        List<BMSFile> removed = [.. rowsByFile.Keys.Where(file => !current.Contains(file))];
        foreach (BMSFile file in removed)
        {
            rowsByFile.Remove(file);
        }
        return removed.Count;
    }

    internal void Clear()
    {
        rowsByFile.Clear();
    }

    private static IEnumerable<BMSFile> GetBmsStorageOwners(IEnumerable<ChartFile> charts)
    {
        return (charts ?? [])
            .Select(chart => chart?.GetBmsStorageOwner())
            .Where(file => file != null);
    }

    private sealed class BmsFileReferenceComparer : IEqualityComparer<BMSFile>
    {
        internal static readonly BmsFileReferenceComparer Instance = new();

        public bool Equals(BMSFile x, BMSFile y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(BMSFile obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
