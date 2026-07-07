using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PlaylistReferenceManager
{
    private readonly object syncRoot = new();
    private PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;

    internal PlaylistReferenceDisplay Find(string md5, string sha256)
    {
        lock (syncRoot)
        {
            return (index ?? PlaylistReferenceIndex.Empty).Find(md5, sha256);
        }
    }

    internal PlaylistReferenceDisplay Find(ChartFile chart)
    {
        lock (syncRoot)
        {
            return (index ?? PlaylistReferenceIndex.Empty).Find(chart);
        }
    }

    internal PlaylistReferenceDisplay Find(LibraryChartRef chart)
    {
        lock (syncRoot)
        {
            return (index ?? PlaylistReferenceIndex.Empty).Find(chart);
        }
    }

    internal void ReplaceTable(BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        if (table == null)
        {
            return;
        }
        List<BMSTableEntry> entrySnapshot = SnapshotEntries(table, entries);
        lock (syncRoot)
        {
            index ??= PlaylistReferenceIndex.Empty;
            index.ReplaceTable(table, entrySnapshot);
        }
    }

    internal void RemoveTable(BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        lock (syncRoot)
        {
            index?.RemoveTable(table);
        }
    }

    internal void RemoveTables(IEnumerable<BMSTable> tables)
    {
        if (tables == null)
        {
            return;
        }
        lock (syncRoot)
        {
            foreach (BMSTable table in tables.Where(table => table != null).Distinct())
            {
                index?.RemoveTable(table);
            }
        }
    }

    internal void Synchronize(IEnumerable<BMSTable> tables)
    {
        lock (syncRoot)
        {
            index = PlaylistReferenceIndex.FromTables(tables);
        }
    }

    internal static List<BMSTableEntry> SnapshotEntries(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        if (entries != null)
        {
            return [.. entries.Where(entry => entry?.is_removed == false)];
        }
        if (table == null)
        {
            return [];
        }
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return [.. table.entries.Where(entry => entry?.is_removed == false)];
        }
    }
}
