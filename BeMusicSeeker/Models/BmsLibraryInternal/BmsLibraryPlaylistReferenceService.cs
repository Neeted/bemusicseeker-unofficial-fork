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

    public Dictionary<string, BMSTable[]> BuildMd5ToTablesMap(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        Dictionary<string, HashSet<BMSTable>> dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        AddEntriesToMd5ToTablesMap(dictionary, table, entries);
        return dictionary.ToDictionary((KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, BMSTable[]> BuildMd5ToTablesMap(IEnumerable<BMSTable> tables)
    {
        Dictionary<string, HashSet<BMSTable>> dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in tables ?? Enumerable.Empty<BMSTable>())
        {
            if (table == null)
            {
                continue;
            }
            List<BMSTableEntry> entries = null;
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                entries = table.entries.ToList();
            }
            AddEntriesToMd5ToTablesMap(dictionary, table, entries);
        }
        return dictionary.ToDictionary((KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Key, (KeyValuePair<string, HashSet<BMSTable>> kvp) => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public int ApplyReferenceMap(IEnumerable<BMSFile> files, Dictionary<string, BMSTable[]> md5ToTablesMap, out int matchedFiles, out PlaylistReferenceApplyStats applyStats, bool suppressFilePropertyChanged = false)
    {
        matchedFiles = 0;
        applyStats = default(PlaylistReferenceApplyStats);
        if (files == null || md5ToTablesMap == null || md5ToTablesMap.Count == 0)
        {
            return 0;
        }
        int addCalls = 0;
        int processed = 0;
        Stopwatch chunkStopwatch = Stopwatch.StartNew();
        foreach (BMSFile file in files)
        {
            if (file != null && !string.IsNullOrWhiteSpace(file.hash) && md5ToTablesMap.TryGetValue(file.hash, out BMSTable[] value))
            {
                matchedFiles++;
                addCalls += file.AddRefTables(value, suppressFilePropertyChanged);
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

    private static void AddEntriesToMd5ToTablesMap(Dictionary<string, HashSet<BMSTable>> dictionary, BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        if (table == null || entries == null)
        {
            return;
        }
        foreach (BMSTableEntry entry in entries)
        {
            if (entry != null && !entry.is_removed && !string.IsNullOrWhiteSpace(entry.md5))
            {
                if (!dictionary.TryGetValue(entry.md5, out HashSet<BMSTable> value))
                {
                    value = new HashSet<BMSTable>();
                    dictionary[entry.md5] = value;
                }
                value.Add(table);
            }
        }
    }
}
