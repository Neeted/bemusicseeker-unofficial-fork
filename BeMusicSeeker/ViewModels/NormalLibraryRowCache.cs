using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private readonly Dictionary<BMSFile, LibraryChartRow> rowsByFile = new Dictionary<BMSFile, LibraryChartRow>(BmsFileReferenceComparer.Instance);
    private readonly Action<string> sortKeyChanged;

    internal NormalLibraryRowCache(Action<string> sortKeyChanged)
    {
        this.sortKeyChanged = sortKeyChanged;
    }

    internal int Count => rowsByFile.Count;

    internal LibraryChartRow GetOrCreate(BMSFile file, LibraryRowCacheBuildStats stats)
    {
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
        PropertyChangedEventManager.AddHandler(file, OnSourcePropertyChanged, string.Empty);
        if (stats != null)
        {
            stats.MissCount++;
        }
        return row;
    }

    internal int Prune(IEnumerable<BMSFile> currentFiles)
    {
        HashSet<BMSFile> current = new HashSet<BMSFile>(
            (currentFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null),
            BmsFileReferenceComparer.Instance);
        List<BMSFile> removed = rowsByFile.Keys.Where((BMSFile file) => !current.Contains(file)).ToList();
        foreach (BMSFile file in removed)
        {
            PropertyChangedEventManager.RemoveHandler(file, OnSourcePropertyChanged, string.Empty);
            rowsByFile.Remove(file);
        }
        return removed.Count;
    }

    internal void Clear()
    {
        foreach (BMSFile file in rowsByFile.Keys.ToList())
        {
            PropertyChangedEventManager.RemoveHandler(file, OnSourcePropertyChanged, string.Empty);
        }
        rowsByFile.Clear();
    }

    private void OnSourcePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        string propertyName = e?.PropertyName;
        if (string.IsNullOrEmpty(propertyName)
            || string.Equals(propertyName, nameof(BMSFile.Title), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(BMSFile.path), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(BMSFile.Folder), StringComparison.Ordinal))
        {
            sortKeyChanged?.Invoke(propertyName ?? string.Empty);
        }
    }

    private sealed class BmsFileReferenceComparer : IEqualityComparer<BMSFile>
    {
        internal static readonly BmsFileReferenceComparer Instance = new BmsFileReferenceComparer();

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
