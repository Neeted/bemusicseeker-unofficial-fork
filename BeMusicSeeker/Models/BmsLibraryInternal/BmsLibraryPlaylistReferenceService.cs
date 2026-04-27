using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryPlaylistReferenceService
{
    private readonly int playlistReferenceApplyChunkSize;

    public BmsLibraryPlaylistReferenceService(int playlistReferenceApplyChunkSize)
    {
        this.playlistReferenceApplyChunkSize = playlistReferenceApplyChunkSize;
    }

    public PlaylistReferenceMaps BuildReferenceMaps(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        Dictionary<string, HashSet<BMSTable>> md5Dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<BMSTable>> sha256Dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        AddEntriesToReferenceMaps(md5Dictionary, sha256Dictionary, table, entries);
        return new PlaylistReferenceMaps
        {
            Md5ToTablesMap = md5Dictionary.ToDictionary((KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            Sha256ToTablesMap = sha256Dictionary.ToDictionary((KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public PlaylistReferenceMaps BuildReferenceMaps(IEnumerable<BMSTable> tables)
    {
        Dictionary<string, HashSet<BMSTable>> md5Dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<BMSTable>> sha256Dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in tables ?? Enumerable.Empty<BMSTable>())
        {
            if (table == null)
            {
                continue;
            }
            List<BMSTableEntry> entries = null;
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                if (!table.ArePlaylistEntriesLoaded)
                {
                    throw new InvalidOperationException("Playlist entries are not loaded. table=" + (table.name ?? string.Empty));
                }
                entries = table.entries.ToList();
            }
            AddEntriesToReferenceMaps(md5Dictionary, sha256Dictionary, table, entries);
        }
        return new PlaylistReferenceMaps
        {
            Md5ToTablesMap = md5Dictionary.ToDictionary((KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            Sha256ToTablesMap = sha256Dictionary.ToDictionary((KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public int ApplyReferenceMap(IEnumerable<BMSFile> files, PlaylistReferenceMaps referenceMaps, out int matchedFiles, out PlaylistReferenceApplyStats applyStats, bool suppressFilePropertyChanged = false)
    {
        matchedFiles = 0;
        applyStats = default(PlaylistReferenceApplyStats);
        if (files == null || referenceMaps == null || ((referenceMaps.Md5ToTablesMap?.Count ?? 0) == 0 && (referenceMaps.Sha256ToTablesMap?.Count ?? 0) == 0))
        {
            return 0;
        }
        int addCalls = 0;
        int processed = 0;
        Stopwatch chunkStopwatch = Stopwatch.StartNew();
        foreach (BMSFile file in files)
        {
            BMSTable[] value = null;
            if (file != null)
            {
                bool matched = !string.IsNullOrWhiteSpace(file.hash) && referenceMaps.Md5ToTablesMap != null && referenceMaps.Md5ToTablesMap.TryGetValue(file.hash, out value);
                if (!matched && !string.IsNullOrWhiteSpace(file.sha256) && referenceMaps.Sha256ToTablesMap != null)
                {
                    matched = referenceMaps.Sha256ToTablesMap.TryGetValue(file.sha256, out value);
                }
                if (matched && value != null)
                {
                    matchedFiles++;
                    addCalls += file.AddRefTables(value, suppressFilePropertyChanged);
                }
            }
            processed++;
            if (processed % playlistReferenceApplyChunkSize == 0)
            {
                chunkStopwatch.Stop();
                applyStats.Chunks++;
                if (chunkStopwatch.ElapsedMilliseconds > applyStats.MaxChunkMs)
                {
                    applyStats.MaxChunkMs = chunkStopwatch.ElapsedMilliseconds;
                }
                Thread.Sleep(0);
                applyStats.YieldCount++;
                chunkStopwatch.Restart();
            }
        }
        chunkStopwatch.Stop();
        if (processed % playlistReferenceApplyChunkSize != 0 || processed == 0)
        {
            applyStats.Chunks++;
            if (chunkStopwatch.ElapsedMilliseconds > applyStats.MaxChunkMs)
            {
                applyStats.MaxChunkMs = chunkStopwatch.ElapsedMilliseconds;
            }
        }
        return addCalls;
    }

    private static void AddEntriesToReferenceMaps(Dictionary<string, HashSet<BMSTable>> md5Dictionary, Dictionary<string, HashSet<BMSTable>> sha256Dictionary, BMSTable table, IEnumerable<BMSTableEntry> entries)
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
            if (!string.IsNullOrWhiteSpace(entry.md5))
            {
                if (!md5Dictionary.TryGetValue(entry.md5, out HashSet<BMSTable> value))
                {
                    value = new HashSet<BMSTable>();
                    md5Dictionary[entry.md5] = value;
                }
                value.Add(table);
            }
            if (!string.IsNullOrWhiteSpace(entry.sha256))
            {
                if (!sha256Dictionary.TryGetValue(entry.sha256, out HashSet<BMSTable> value))
                {
                    value = new HashSet<BMSTable>();
                    sha256Dictionary[entry.sha256] = value;
                }
                value.Add(table);
            }
        }
    }
}
