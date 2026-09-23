using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns aggregate playlist-summary count reuse independently from the presentation workspace.
/// </summary>
internal sealed class PlaylistCatalogSummaryOwner
{
    private readonly object gate = new();

    private readonly ConditionalWeakTable<BMSTable, TableCountCacheEntry> tableCountCache = new();

    private long tableCountCacheGeneration;

    internal long TableCountCacheGeneration
    {
        get
        {
            lock (gate)
            {
                return tableCountCacheGeneration;
            }
        }
    }

    internal void InvalidateTableCounts()
    {
        lock (gate)
        {
            tableCountCacheGeneration++;
        }
    }

    internal bool TryGetTableCount(
        BMSTable table,
        int ownedSnapshotVersion,
        out PlaylistSummaryCountResult countResult)
    {
        countResult = default;
        if (table == null)
        {
            return false;
        }
        return TryGetTableCount(table, CreateTableCountCacheKey(table, ownedSnapshotVersion), out countResult);
    }

    internal bool TrySetTableCount(
        BMSTable table,
        int ownedSnapshotVersion,
        PlaylistSummaryCountResult countResult,
        long expectedGeneration)
    {
        if (table == null)
        {
            return false;
        }
        return TrySetTableCount(
            table,
            CreateTableCountCacheKey(table, ownedSnapshotVersion),
            countResult,
            expectedGeneration);
    }

    private bool TryGetTableCount(
        BMSTable table,
        string key,
        out PlaylistSummaryCountResult countResult)
    {
        lock (gate)
        {
            countResult = default;
            TableCountCacheEntry cacheEntry = tableCountCache.GetValue(table, _ => new TableCountCacheEntry());
            if (cacheEntry.Generation != tableCountCacheGeneration
                || !string.Equals(cacheEntry.Key, key, StringComparison.Ordinal)
                || !cacheEntry.HasValue)
            {
                return false;
            }
            countResult = cacheEntry.Result;
            return true;
        }
    }

    private bool TrySetTableCount(
        BMSTable table,
        string key,
        PlaylistSummaryCountResult countResult,
        long expectedGeneration)
    {
        lock (gate)
        {
            if (expectedGeneration != tableCountCacheGeneration)
            {
                return false;
            }
            TableCountCacheEntry cacheEntry = tableCountCache.GetValue(table, _ => new TableCountCacheEntry());
            cacheEntry.Key = key;
            cacheEntry.Result = countResult;
            cacheEntry.Generation = tableCountCacheGeneration;
            cacheEntry.HasValue = true;
            return true;
        }
    }

    internal PlaylistSummaryCountResult GetOrBuildTableCount(
        BMSTable table,
        OwnedChartHashIndexVersionedSnapshot ownedHashSnapshot,
        CancellationToken cancellationToken,
        out bool cacheHit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (table == null)
        {
            cacheHit = false;
            return default;
        }

        int ownedSnapshotVersion = ownedHashSnapshot?.Version ?? 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long expectedGeneration;
            string key;
            int entriesRevision;
            PlaylistEntriesLoadState entriesLoadState;
            IReadOnlyList<BMSTableEntry> entries;
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                key = CreateTableCountCacheKey(table, ownedSnapshotVersion);
                if (TryGetTableCount(table, key, out PlaylistSummaryCountResult cachedResult))
                {
                    cacheHit = true;
                    return cachedResult;
                }
                entriesRevision = table.PlaylistEntriesRevision;
                entriesLoadState = table.PlaylistEntriesLoadState;
                lock (gate)
                {
                    expectedGeneration = tableCountCacheGeneration;
                }
                entries = [.. table.GetEntriesExceptDummy()];
            }

            cancellationToken.ThrowIfCancellationRequested();
            PlaylistSummaryCountResult countResult = CalculateTableCount(entries, ownedHashSnapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                if (table.PlaylistEntriesRevision != entriesRevision
                    || table.PlaylistEntriesLoadState != entriesLoadState
                    || !string.Equals(
                        key,
                        CreateTableCountCacheKey(table, ownedSnapshotVersion),
                        StringComparison.Ordinal))
                {
                    continue;
                }
                TrySetTableCount(table, key, countResult, expectedGeneration);
            }
            cacheHit = false;
            return countResult;
        }
    }

    internal static PlaylistSummaryCountResult CalculateTableCount(
        IEnumerable<BMSTableEntry> entries,
        HashSet<string> ownedMd5Hashes,
        HashSet<string> ownedSha256Hashes)
    {
        HashSet<string> safeMd5Hashes = ownedMd5Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> safeSha256Hashes = ownedSha256Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return CalculateTableCount(
            entries,
            md5 => !string.IsNullOrWhiteSpace(md5) && safeMd5Hashes.Contains(md5),
            sha256 => !string.IsNullOrWhiteSpace(sha256) && safeSha256Hashes.Contains(sha256),
            CancellationToken.None);
    }

    internal static PlaylistSummaryCountResult CalculateTableCount(
        IEnumerable<BMSTableEntry> entries,
        OwnedChartHashIndexVersionedSnapshot ownedHashSnapshot)
    {
        return CalculateTableCount(entries, ownedHashSnapshot, CancellationToken.None);
    }

    private static PlaylistSummaryCountResult CalculateTableCount(
        IEnumerable<BMSTableEntry> entries,
        OwnedChartHashIndexVersionedSnapshot ownedHashSnapshot,
        CancellationToken cancellationToken)
    {
        return CalculateTableCount(
            entries,
            ownedHashSnapshot == null ? null : new Func<string, bool>(ownedHashSnapshot.ContainsMd5),
            ownedHashSnapshot == null ? null : new Func<string, bool>(ownedHashSnapshot.ContainsSha256),
            cancellationToken);
    }

    private static PlaylistSummaryCountResult CalculateTableCount(
        IEnumerable<BMSTableEntry> entries,
        Func<string, bool> containsMd5,
        Func<string, bool> containsSha256,
        CancellationToken cancellationToken)
    {
        PlaylistSummaryCountResult result = default;
        containsMd5 ??= _ => false;
        containsSha256 ??= _ => false;
        foreach (BMSTableEntry entry in entries ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.ScannedEntries++;
            if (entry == null || entry.is_removed)
            {
                continue;
            }
            bool hasMd5 = !string.IsNullOrWhiteSpace(entry.md5);
            bool hasSha256 = !string.IsNullOrWhiteSpace(entry.sha256);
            if (!hasMd5 && !hasSha256)
            {
                continue;
            }
            result.TotalCharts++;
            if ((hasMd5 && containsMd5(entry.md5)) || (!hasMd5 && containsSha256(entry.sha256)))
            {
                result.OwnedCharts++;
            }
        }
        return result;
    }

    private static string CreateTableCountCacheKey(BMSTable table, int ownedSnapshotVersion)
    {
        if (table == null)
        {
            return null;
        }
        string tableKey = table.playlist_id.HasValue
            ? "id:" + table.playlist_id.Value.ToString(CultureInfo.InvariantCulture)
            : "name:" + (table.name ?? string.Empty) + "|symbol:" + (table.symbol ?? string.Empty);
        return tableKey
            + "|entryRevision:" + table.PlaylistEntriesRevision.ToString(CultureInfo.InvariantCulture)
            + "|owned:" + ownedSnapshotVersion.ToString(CultureInfo.InvariantCulture)
            + "|state:" + table.PlaylistEntriesLoadState;
    }

    private sealed class TableCountCacheEntry
    {
        internal string Key { get; set; }

        internal PlaylistSummaryCountResult Result { get; set; }

        internal long Generation { get; set; }

        internal bool HasValue { get; set; }
    }
}
