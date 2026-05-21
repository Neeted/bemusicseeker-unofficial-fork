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
        BMSFile file = chart?.BmsFile;
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

    internal int Prune(IEnumerable<BMSFile> currentFiles)
    {
        var current = new HashSet<BMSFile>(
            (currentFiles ?? []).Where(file => file != null),
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
