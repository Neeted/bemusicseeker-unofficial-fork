using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryPlaylistReferenceService(int playlistReferenceApplyChunkSize)
{
    private readonly int playlistReferenceApplyChunkSize = playlistReferenceApplyChunkSize;

    public PlaylistReferenceMaps BuildReferenceMaps(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        var md5Dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        var sha256Dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        AddEntriesToReferenceMaps(md5Dictionary, sha256Dictionary, table, entries);
        return new PlaylistReferenceMaps
        {
            Md5ToTablesMap = md5Dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            Sha256ToTablesMap = sha256Dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public PlaylistReferenceMaps BuildReferenceMaps(IEnumerable<BMSTable> tables)
    {
        var md5Dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        var sha256Dictionary = new Dictionary<string, HashSet<BMSTable>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in tables ?? [])
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
                entries = [.. table.entries];
            }
            AddEntriesToReferenceMaps(md5Dictionary, sha256Dictionary, table, entries);
        }
        return new PlaylistReferenceMaps
        {
            Md5ToTablesMap = md5Dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            Sha256ToTablesMap = sha256Dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public int ApplyReferenceMap(IEnumerable<LibraryChartRef> charts, PlaylistReferenceMaps referenceMaps, out int matchedCharts, out PlaylistReferenceApplyStats applyStats)
    {
        matchedCharts = 0;
        applyStats = default;
        if (charts == null || referenceMaps == null || ((referenceMaps.Md5ToTablesMap?.Count ?? 0) == 0 && (referenceMaps.Sha256ToTablesMap?.Count ?? 0) == 0))
        {
            return 0;
        }
        int processed = 0;
        var chunkStopwatch = Stopwatch.StartNew();
        foreach (LibraryChartRef chart in charts)
        {
            if (TryGetReferenceTables(chart, referenceMaps))
            {
                matchedCharts++;
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
        return matchedCharts;
    }

    public int ApplyReferenceMap(IEnumerable<PackageChartEntry> entries, PlaylistReferenceMaps referenceMaps, out int matchedCharts, out PlaylistReferenceApplyStats applyStats)
    {
        matchedCharts = 0;
        applyStats = default;
        if (entries == null || referenceMaps == null || ((referenceMaps.Md5ToTablesMap?.Count ?? 0) == 0 && (referenceMaps.Sha256ToTablesMap?.Count ?? 0) == 0))
        {
            return 0;
        }

        int processed = 0;
        var chunkStopwatch = Stopwatch.StartNew();
        foreach (PackageChartEntry entry in entries)
        {
            if (TryGetReferenceTables(entry?.Chart, referenceMaps))
            {
                matchedCharts++;
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
        return matchedCharts;
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
                    value = [];
                    md5Dictionary[entry.md5] = value;
                }
                value.Add(table);
            }
            if (!string.IsNullOrWhiteSpace(entry.sha256))
            {
                if (!sha256Dictionary.TryGetValue(entry.sha256, out HashSet<BMSTable> value))
                {
                    value = [];
                    sha256Dictionary[entry.sha256] = value;
                }
                value.Add(table);
            }
        }
    }

    private static bool TryGetReferenceTables(ChartFile chart, PlaylistReferenceMaps referenceMaps)
    {
        if (chart == null || referenceMaps == null)
        {
            return false;
        }

        BMSTable[] tables = null;
        bool matched = !string.IsNullOrWhiteSpace(chart.Md5) && referenceMaps.Md5ToTablesMap != null && referenceMaps.Md5ToTablesMap.TryGetValue(chart.Md5, out tables);
        if (!matched && !string.IsNullOrWhiteSpace(chart.Sha256) && referenceMaps.Sha256ToTablesMap != null)
        {
            matched = referenceMaps.Sha256ToTablesMap.TryGetValue(chart.Sha256, out tables);
        }
        return matched && tables != null;
    }

    private static bool TryGetReferenceTables(LibraryChartRef chart, PlaylistReferenceMaps referenceMaps)
    {
        if (chart == null || referenceMaps == null)
        {
            return false;
        }

        BMSTable[] tables = null;
        bool matched = !string.IsNullOrWhiteSpace(chart.Md5) && referenceMaps.Md5ToTablesMap != null && referenceMaps.Md5ToTablesMap.TryGetValue(chart.Md5, out tables);
        if (!matched && !string.IsNullOrWhiteSpace(chart.Sha256) && referenceMaps.Sha256ToTablesMap != null)
        {
            matched = referenceMaps.Sha256ToTablesMap.TryGetValue(chart.Sha256, out tables);
        }
        return matched && tables != null;
    }
}
