using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PlaylistReferenceIndex
{
    internal static PlaylistReferenceIndex Empty => new();

    private readonly Dictionary<string, List<BMSTable>> md5ToTablesMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<BMSTable>> sha256ToTablesMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PlaylistReferenceDisplay> md5ToDisplayMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PlaylistReferenceDisplay> sha256ToDisplayMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<BMSTable, PlaylistReferenceTableKeys> tableKeys = new(BmsTableReferenceComparer.Instance);

    internal int Md5Count => md5ToTablesMap.Count;

    internal int Sha256Count => sha256ToTablesMap.Count;

    internal PlaylistReferenceDisplay Find(string md5, string sha256)
    {
        if (!string.IsNullOrWhiteSpace(md5) && md5ToDisplayMap.TryGetValue(md5.Trim(), out PlaylistReferenceDisplay md5Display))
        {
            return md5Display;
        }
        if (!string.IsNullOrWhiteSpace(sha256) && sha256ToDisplayMap.TryGetValue(sha256.Trim(), out PlaylistReferenceDisplay sha256Display))
        {
            return sha256Display;
        }
        return PlaylistReferenceDisplay.Empty;
    }

    internal PlaylistReferenceDisplay Find(ChartFile chart)
    {
        return chart == null
            ? PlaylistReferenceDisplay.Empty
            : Find(chart.Md5, chart.Sha256);
    }

    internal PlaylistReferenceDisplay Find(LibraryChartRef chart)
    {
        return chart == null
            ? PlaylistReferenceDisplay.Empty
            : Find(chart.Md5, chart.Sha256);
    }

    internal void ReplaceTable(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        if (table == null)
        {
            return;
        }
        RemoveTable(table);
        var keys = new PlaylistReferenceTableKeys();
        AddEntries(table, entries, keys, updateDisplay: true);
        if (keys.HasAny)
        {
            tableKeys[table] = keys;
        }
    }

    internal void RemoveTable(BMSTable table)
    {
        if (table == null || !tableKeys.TryGetValue(table, out PlaylistReferenceTableKeys keys))
        {
            return;
        }
        foreach (string key in keys.Md5)
        {
            RemoveTableFromKey(md5ToTablesMap, md5ToDisplayMap, key, table);
        }
        foreach (string key in keys.Sha256)
        {
            RemoveTableFromKey(sha256ToTablesMap, sha256ToDisplayMap, key, table);
        }
        tableKeys.Remove(table);
    }

    internal static PlaylistReferenceIndex FromReferenceMaps(PlaylistReferenceMaps referenceMaps)
    {
        var index = new PlaylistReferenceIndex();
        if (referenceMaps == null)
        {
            return index;
        }
        index.AddReferenceMap(referenceMaps.Md5ToTablesMap, index.md5ToTablesMap, index.md5ToDisplayMap, isMd5: true);
        index.AddReferenceMap(referenceMaps.Sha256ToTablesMap, index.sha256ToTablesMap, index.sha256ToDisplayMap, isMd5: false);
        return index;
    }

    internal static PlaylistReferenceIndex FromTables(IEnumerable<BMSTable> tables)
    {
        var index = new PlaylistReferenceIndex();
        foreach (BMSTable table in tables ?? [])
        {
            if (table == null)
            {
                continue;
            }
            List<BMSTableEntry> entries;
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                if (!table.ArePlaylistEntriesLoaded)
                {
                    throw new InvalidOperationException("Playlist entries are not loaded. table=" + (table.name ?? string.Empty));
                }
                entries = [.. table.entries];
            }
            var keys = new PlaylistReferenceTableKeys();
            index.AddEntries(table, entries, keys, updateDisplay: false);
            if (keys.HasAny)
            {
                index.tableKeys[table] = keys;
            }
        }
        index.RebuildDisplayMaps();
        return index;
    }

    private void AddReferenceMap(
        IDictionary<string, BMSTable[]> source,
        Dictionary<string, List<BMSTable>> target,
        Dictionary<string, PlaylistReferenceDisplay> displayMap,
        bool isMd5)
    {
        foreach (KeyValuePair<string, BMSTable[]> item in source ?? Enumerable.Empty<KeyValuePair<string, BMSTable[]>>())
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                continue;
            }
            string key = item.Key.Trim();
            foreach (BMSTable table in (item.Value ?? []).Where(table => table != null).Distinct())
            {
                AddTableToKey(target, displayMap, key, table);
                AddTableKey(table, key, isMd5);
            }
        }
    }

    private void AddEntries(BMSTable table, IEnumerable<BMSTableEntry> entries, PlaylistReferenceTableKeys keys, bool updateDisplay)
    {
        if (table == null || entries == null)
        {
            return;
        }
        foreach (BMSTableEntry entry in entries)
        {
            if (entry == null || entry.is_removed)
            {
                continue;
            }
            AddEntryKey(md5ToTablesMap, md5ToDisplayMap, keys.Md5, entry.md5, table, updateDisplay);
            AddEntryKey(sha256ToTablesMap, sha256ToDisplayMap, keys.Sha256, entry.sha256, table, updateDisplay);
        }
    }

    private void AddEntryKey(
        Dictionary<string, List<BMSTable>> map,
        Dictionary<string, PlaylistReferenceDisplay> displayMap,
        HashSet<string> keys,
        string hash,
        BMSTable table,
        bool updateDisplay)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }
        string key = hash.Trim();
        AddTableToKey(map, updateDisplay ? displayMap : null, key, table);
        keys.Add(key);
    }

    private void AddTableKey(BMSTable table, string key, bool isMd5)
    {
        if (!tableKeys.TryGetValue(table, out PlaylistReferenceTableKeys keys))
        {
            keys = new PlaylistReferenceTableKeys();
            tableKeys[table] = keys;
        }
        if (isMd5)
        {
            keys.Md5.Add(key);
        }
        else
        {
            keys.Sha256.Add(key);
        }
    }

    private void RebuildDisplayMaps()
    {
        md5ToDisplayMap.Clear();
        foreach (KeyValuePair<string, List<BMSTable>> item in md5ToTablesMap)
        {
            md5ToDisplayMap[item.Key] = new PlaylistReferenceDisplay([.. item.Value]);
        }

        sha256ToDisplayMap.Clear();
        foreach (KeyValuePair<string, List<BMSTable>> item in sha256ToTablesMap)
        {
            sha256ToDisplayMap[item.Key] = new PlaylistReferenceDisplay([.. item.Value]);
        }
    }

    private static void AddTableToKey(
        Dictionary<string, List<BMSTable>> map,
        Dictionary<string, PlaylistReferenceDisplay> displayMap,
        string key,
        BMSTable table)
    {
        if (string.IsNullOrWhiteSpace(key) || table == null)
        {
            return;
        }
        if (!map.TryGetValue(key, out List<BMSTable> tables))
        {
            tables = [];
            map[key] = tables;
        }
        if (!tables.Any(candidate => ReferenceEquals(candidate, table)))
        {
            tables.Add(table);
            if (displayMap != null)
            {
                displayMap[key] = new PlaylistReferenceDisplay([.. tables]);
            }
        }
    }

    private static void RemoveTableFromKey(
        Dictionary<string, List<BMSTable>> map,
        Dictionary<string, PlaylistReferenceDisplay> displayMap,
        string key,
        BMSTable table)
    {
        if (string.IsNullOrWhiteSpace(key) || table == null || !map.TryGetValue(key, out List<BMSTable> tables))
        {
            return;
        }
        if (tables.RemoveAll(candidate => ReferenceEquals(candidate, table)) == 0)
        {
            return;
        }
        if (tables.Count == 0)
        {
            map.Remove(key);
            displayMap.Remove(key);
        }
        else
        {
            displayMap[key] = new PlaylistReferenceDisplay([.. tables]);
        }
    }

    private sealed class PlaylistReferenceTableKeys
    {
        internal HashSet<string> Md5 { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal HashSet<string> Sha256 { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool HasAny => Md5.Count > 0 || Sha256.Count > 0;
    }

    private sealed class BmsTableReferenceComparer : IEqualityComparer<BMSTable>
    {
        internal static readonly BmsTableReferenceComparer Instance = new();

        public bool Equals(BMSTable x, BMSTable y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(BMSTable obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
