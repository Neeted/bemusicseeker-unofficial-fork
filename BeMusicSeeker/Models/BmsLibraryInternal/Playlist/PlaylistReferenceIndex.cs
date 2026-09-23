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

    private readonly Dictionary<BMSTable, PlaylistReferenceTableDisplaySnapshot> tableDisplaySnapshots = new(BmsTableReferenceComparer.Instance);

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

    internal void ReplaceSnapshotTable(PlaylistReferenceTableSnapshot snapshot)
    {
        BMSTable table = snapshot?.Table;
        if (table == null)
        {
            return;
        }
        RemoveTable(table);
        var keys = new PlaylistReferenceTableKeys();
        AddSnapshotEntries(table, snapshot.Entries, keys);
        tableDisplaySnapshots[table] = new PlaylistReferenceTableDisplaySnapshot(snapshot.Symbol, snapshot.Name);
        if (keys.HasAny)
        {
            tableKeys[table] = keys;
        }
        RebuildDisplayMaps();
    }

    internal void RemoveTable(BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        if (!tableKeys.TryGetValue(table, out PlaylistReferenceTableKeys keys))
        {
            tableDisplaySnapshots.Remove(table);
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
        tableDisplaySnapshots.Remove(table);
    }

    internal PlaylistReferenceLookupKeys CreateLookupKeysSnapshot()
    {
        return new PlaylistReferenceLookupKeys(md5ToTablesMap.Keys, sha256ToTablesMap.Keys);
    }

    internal static PlaylistReferenceIndex FromSnapshots(IEnumerable<PlaylistReferenceTableSnapshot> snapshots)
    {
        var index = new PlaylistReferenceIndex();
        foreach (PlaylistReferenceTableSnapshot snapshot in snapshots ?? [])
        {
            if (snapshot?.Table == null)
            {
                continue;
            }
            var keys = new PlaylistReferenceTableKeys();
            index.AddSnapshotEntries(snapshot.Table, snapshot.Entries, keys);
            index.tableDisplaySnapshots[snapshot.Table] = new PlaylistReferenceTableDisplaySnapshot(snapshot.Symbol, snapshot.Name);
            if (keys.HasAny)
            {
                index.tableKeys[snapshot.Table] = keys;
            }
        }
        index.RebuildDisplayMaps();
        return index;
    }

    private void AddSnapshotEntries(BMSTable table, IEnumerable<PlaylistReferenceEntrySnapshot> entries, PlaylistReferenceTableKeys keys)
    {
        if (table == null || entries == null)
        {
            return;
        }
        foreach (PlaylistReferenceEntrySnapshot entry in entries)
        {
            PlaylistEntryLookupKey lookupKey = entry?.LookupKey ?? default;
            if (!lookupKey.HasValue)
            {
                continue;
            }
            if (lookupKey.Kind == PlaylistEntryLookupKeyKind.Md5)
            {
                AddEntryKey(md5ToTablesMap, keys.Md5, lookupKey.Hash, table);
            }
            else
            {
                AddEntryKey(sha256ToTablesMap, keys.Sha256, lookupKey.Hash, table);
            }
        }
    }

    private void AddEntryKey(
        Dictionary<string, List<BMSTable>> map,
        HashSet<string> keys,
        string hash,
        BMSTable table)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }
        string key = hash.Trim();
        AddTableToKey(map, key, table);
        keys.Add(key);
    }

    private void RebuildDisplayMaps()
    {
        md5ToDisplayMap.Clear();
        foreach (KeyValuePair<string, List<BMSTable>> item in md5ToTablesMap)
        {
            md5ToDisplayMap[item.Key] = CreateDisplay(item.Value);
        }

        sha256ToDisplayMap.Clear();
        foreach (KeyValuePair<string, List<BMSTable>> item in sha256ToTablesMap)
        {
            sha256ToDisplayMap[item.Key] = CreateDisplay(item.Value);
        }
    }

    private static void AddTableToKey(
        Dictionary<string, List<BMSTable>> map,
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
        }
    }

    private PlaylistReferenceDisplay CreateDisplay(IEnumerable<BMSTable> tables)
    {
        return new PlaylistReferenceDisplay([.. (tables ?? [])
            .Where(table => table != null)
            .Where(table => tableDisplaySnapshots.ContainsKey(table))
            .Select(table => tableDisplaySnapshots[table])]);
    }

    private void RemoveTableFromKey(
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
            displayMap[key] = CreateDisplay(tables);
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
