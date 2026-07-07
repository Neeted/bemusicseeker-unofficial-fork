using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPlaylistReferenceApplyHost
{
    void ReplacePlaylistReferenceIndexTable(BMSTable table, IEnumerable<BMSTableEntry> entries = null);
    void RemovePlaylistReferenceIndexTable(BMSTable table);
    void RemovePlaylistReferenceIndexTables(IEnumerable<BMSTable> tables);
    void SynchronizePlaylistReferenceIndex(IEnumerable<BMSTable> tables);
    List<LibraryChartRef> SnapshotLibraryChartRefsForPlaylistReferenceApply(PlaylistReferenceMaps referenceMaps);
    List<LibraryChartRef> SnapshotLibraryChartRefsForPlaylistReferenceApply(ISet<string> md5Hashes, ISet<string> sha256Hashes);
    List<PackageChartEntry> SnapshotPendingChartEntriesForPlaylistReferenceApply(PlaylistReferenceMaps referenceMaps);
    List<PackageChartEntry> SnapshotPendingChartEntriesForPlaylistReferenceApply();
    List<PackageChartEntry> SnapshotPackageChartEntriesForPlaylistReferenceApply(IEnumerable<ChartPackage> packages, PlaylistReferenceMaps referenceMaps);
    void LogInstallPerformance(string message);
}

internal static class PlaylistReferenceApplyCoordinator
{
    internal static void AddReferenceBMSTables(BmsLibraryPlaylistReferenceService service, IPlaylistReferenceApplyHost host, BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        if (table == null)
        {
            return;
        }
        var stopwatchBuildMap = Stopwatch.StartNew();
        IEnumerable<BMSTableEntry> sourceEntries = entries;
        if (sourceEntries == null)
        {
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                sourceEntries = [.. table.entries];
            }
        }
        else
        {
            sourceEntries = [.. sourceEntries];
        }
        PlaylistReferenceMaps referenceMaps = service.BuildReferenceMaps(table, sourceEntries);
        host.ReplacePlaylistReferenceIndexTable(table, sourceEntries);
        stopwatchBuildMap.Stop();

        var stopwatchApplySong = Stopwatch.StartNew();
        ApplyToLibraryCharts(service, host, referenceMaps, out int matchedLibraryCharts, out int appliedLibraryCharts, out PlaylistReferenceApplyStats songApplyStats);
        stopwatchApplySong.Stop();

        var stopwatchApplyPending = Stopwatch.StartNew();
        ApplyToPendingCharts(service, host, referenceMaps, out int matchedPendingFiles, out int appliedPendingCharts, out PlaylistReferenceApplyStats pendingApplyStats);
        stopwatchApplyPending.Stop();

        const int tableCount = 1;
        host.LogInstallPerformance("playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + stopwatchApplySong.ElapsedMilliseconds + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + stopwatchApplyPending.ElapsedMilliseconds + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + tableCount + " matchedLibraryCharts=" + matchedLibraryCharts + " appliedLibraryCharts=" + appliedLibraryCharts + " matchedPendingFiles=" + matchedPendingFiles + " appliedPendingCharts=" + appliedPendingCharts);
    }

    internal static void AddReferenceBMSTables(BmsLibraryPlaylistReferenceService service, IPlaylistReferenceApplyHost host, IEnumerable<BMSTable> tables)
    {
        if (tables == null)
        {
            return;
        }
        List<BMSTable> list = [.. tables.Where(t => t != null)];
        if (list.Count == 0)
        {
            return;
        }
        var stopwatchBuildMap = Stopwatch.StartNew();
        PlaylistReferenceMaps referenceMaps = service.BuildReferenceMaps(list);
        host.SynchronizePlaylistReferenceIndex(list);
        stopwatchBuildMap.Stop();

        ApplyBatchReferenceMaps(service, host, referenceMaps, out long applySongMs, out long applyPendingMs, out int matchedLibraryCharts, out int appliedLibraryCharts, out int matchedPendingFiles, out int appliedPendingCharts, out PlaylistReferenceApplyStats songApplyStats, out PlaylistReferenceApplyStats pendingApplyStats);

        host.LogInstallPerformance("playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + applySongMs + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + applyPendingMs + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + list.Count + " matchedLibraryCharts=" + matchedLibraryCharts + " appliedLibraryCharts=" + appliedLibraryCharts + " matchedPendingFiles=" + matchedPendingFiles + " appliedPendingCharts=" + appliedPendingCharts);
    }

    internal static void AddReferenceBMSTablesIncremental(BmsLibraryPlaylistReferenceService service, IPlaylistReferenceApplyHost host, IEnumerable<BMSTable> tables)
    {
        if (tables == null)
        {
            return;
        }
        List<BMSTable> list = [.. tables.Where(table => table != null).Distinct()];
        if (list.Count == 0)
        {
            return;
        }
        var stopwatchBuildMap = Stopwatch.StartNew();
        PlaylistReferenceMaps referenceMaps = service.BuildReferenceMaps(list);
        foreach (BMSTable table in list)
        {
            host.ReplacePlaylistReferenceIndexTable(table);
        }
        stopwatchBuildMap.Stop();

        ApplyBatchReferenceMaps(service, host, referenceMaps, out long applySongMs, out long applyPendingMs, out int matchedLibraryCharts, out int appliedLibraryCharts, out int matchedPendingFiles, out int appliedPendingCharts, out PlaylistReferenceApplyStats songApplyStats, out PlaylistReferenceApplyStats pendingApplyStats);

        host.LogInstallPerformance("playlist_ref_incremental_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applySongMs=" + applySongMs + " applySongChunks=" + songApplyStats.Chunks + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs + " applySongYieldCount=" + songApplyStats.YieldCount + " applyPendingMs=" + applyPendingMs + " applyPendingChunks=" + pendingApplyStats.Chunks + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs + " applyPendingYieldCount=" + pendingApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + list.Count + " matchedLibraryCharts=" + matchedLibraryCharts + " appliedLibraryCharts=" + appliedLibraryCharts + " matchedPendingFiles=" + matchedPendingFiles + " appliedPendingCharts=" + appliedPendingCharts);
    }

    internal static void AddReferenceBMSTablesToPackageCharts(BmsLibraryPlaylistReferenceService service, IPlaylistReferenceApplyHost host, IEnumerable<BMSTable> tables, IEnumerable<ChartPackage> packages)
    {
        if (tables == null || packages == null)
        {
            return;
        }
        List<BMSTable> tableList = [.. tables.Where(table => table != null)];
        List<ChartPackage> packageList = [.. packages.Where(package => package != null)];
        if (tableList.Count == 0 || packageList.Count == 0)
        {
            return;
        }

        var stopwatchBuildMap = Stopwatch.StartNew();
        PlaylistReferenceMaps referenceMaps = service.BuildReferenceMaps(tableList);
        stopwatchBuildMap.Stop();

        int matchedPackageFiles = 0;
        int appliedPackageCharts = 0;
        PlaylistReferenceApplyStats packageApplyStats = default;
        long applyPackageMs = 0L;
        if (HasReferenceMaps(referenceMaps))
        {
            var stopwatchApplyPackage = Stopwatch.StartNew();
            List<PackageChartEntry> packageEntriesSnapshot = host.SnapshotPackageChartEntriesForPlaylistReferenceApply(packageList, referenceMaps);
            if (packageEntriesSnapshot.Count > 0)
            {
                appliedPackageCharts = service.ApplyReferenceMap(packageEntriesSnapshot, referenceMaps, out matchedPackageFiles, out packageApplyStats);
            }
            stopwatchApplyPackage.Stop();
            applyPackageMs = stopwatchApplyPackage.ElapsedMilliseconds;
        }
        host.LogInstallPerformance("playlist_ref_package_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds + " applyPackageMs=" + applyPackageMs + " applyPackageChunks=" + packageApplyStats.Chunks + " applyPackageChunkMaxMs=" + packageApplyStats.MaxChunkMs + " applyPackageYieldCount=" + packageApplyStats.YieldCount + " mapMd5Count=" + referenceMaps.Md5ToTablesMap.Count + " mapSha256Count=" + referenceMaps.Sha256ToTablesMap.Count + " tableCount=" + tableList.Count + " packageCount=" + packageList.Count + " matchedPackageFiles=" + matchedPackageFiles + " appliedPackageCharts=" + appliedPackageCharts);
    }

    internal static void ReplaceReferenceBMSTable(IPlaylistReferenceApplyHost host, BMSTable oldTable, BMSTable newTable, IEnumerable<BMSTableEntry> oldEntries = null, IEnumerable<BMSTableEntry> newEntries = null)
    {
        List<BMSTableEntry> oldEntriesSnapshot = PlaylistReferenceManager.SnapshotEntries(oldTable, oldEntries);
        List<BMSTableEntry> newEntriesSnapshot = PlaylistReferenceManager.SnapshotEntries(newTable, newEntries);
        BuildPlaylistReferenceHashSets(oldEntriesSnapshot, out HashSet<string> oldMd5Hashes, out HashSet<string> oldSha256Hashes);
        BuildPlaylistReferenceHashSets(newEntriesSnapshot, out HashSet<string> newMd5Hashes, out HashSet<string> newSha256Hashes);
        List<LibraryChartRef> libraryChartsSnapshot = host.SnapshotLibraryChartRefsForPlaylistReferenceApply(
            CreateCombinedPlaylistReferenceHashSet(oldMd5Hashes, newMd5Hashes),
            CreateCombinedPlaylistReferenceHashSet(oldSha256Hashes, newSha256Hashes));
        List<LibraryChartRef> oldLibraryCharts = FilterPlaylistReferenceTargets(libraryChartsSnapshot, oldMd5Hashes, oldSha256Hashes);
        List<PackageChartEntry> oldPendingEntries = FilterPendingPlaylistReferenceTargetEntries(host.SnapshotPendingChartEntriesForPlaylistReferenceApply(), oldMd5Hashes, oldSha256Hashes);
        List<LibraryChartRef> newLibraryCharts = FilterPlaylistReferenceTargets(libraryChartsSnapshot, newMd5Hashes, newSha256Hashes);
        List<PackageChartEntry> newPendingEntries = FilterPendingPlaylistReferenceTargetEntries(host.SnapshotPendingChartEntriesForPlaylistReferenceApply(), newMd5Hashes, newSha256Hashes);
        host.LogInstallPerformance("playlist_ref_replace targetsOldLibraryCharts=" + oldLibraryCharts.Count + " targetsOldPending=" + oldPendingEntries.Count + " targetsNewLibraryCharts=" + newLibraryCharts.Count + " targetsNewPending=" + newPendingEntries.Count + " oldEntryCount=" + oldEntriesSnapshot.Count + " newEntryCount=" + newEntriesSnapshot.Count);
        if (oldTable != null)
        {
            host.RemovePlaylistReferenceIndexTable(oldTable);
        }
        if (newTable != null)
        {
            host.ReplacePlaylistReferenceIndexTable(newTable, newEntriesSnapshot);
        }
    }

    internal static void AddReferenceBMSTablesToCharts(IPlaylistReferenceApplyHost host, BMSTable table, IEnumerable<ChartFile> charts)
    {
        if (table == null || charts == null)
        {
            return;
        }
        host.ReplacePlaylistReferenceIndexTable(table);
    }

    internal static void RefreshReferenceDisplayForTable(IPlaylistReferenceApplyHost host, BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        host.ReplacePlaylistReferenceIndexTable(table);
    }

    internal static void SynchronizeReferenceBMSTables(IPlaylistReferenceApplyHost host, IEnumerable<BMSTable> tables)
    {
        List<BMSTable> list = ((tables != null) ? [.. tables.Where(table => table != null).Distinct()] : new List<BMSTable>());
        host.SynchronizePlaylistReferenceIndex(list);
    }

    internal static void RemoveReferenceBMSTables(IPlaylistReferenceApplyHost host, BMSTable table, IEnumerable<BMSTableEntry> entries = null)
    {
        if (table == null)
        {
            return;
        }
        if (entries == null)
        {
            host.RemovePlaylistReferenceIndexTable(table);
            return;
        }
        host.ReplacePlaylistReferenceIndexTable(table);
    }

    internal static void RemoveReferenceBMSTables(IPlaylistReferenceApplyHost host, IEnumerable<BMSTable> tables)
    {
        List<BMSTable> list = tables?.Where(table => table != null).Distinct().ToList();
        if (list == null || list.Count == 0)
        {
            return;
        }
        host.RemovePlaylistReferenceIndexTables(list);
    }

    internal static List<PackageChartEntry> FilterPackagePlaylistReferenceTargets(IEnumerable<PackageChartEntry> entries, PlaylistReferenceMaps referenceMaps)
    {
        if (entries == null || !HasReferenceMaps(referenceMaps))
        {
            return [];
        }
        List<PackageChartEntry> matchedEntries = [];
        foreach (PackageChartEntry entry in entries)
        {
            ChartFile chart = entry?.Chart;
            if (chart == null || !HasPlaylistReferenceMapMatch(chart, referenceMaps))
            {
                continue;
            }
            matchedEntries.Add(entry);
        }
        return [.. matchedEntries.Distinct()];
    }

    internal static bool HasPlaylistReferenceMapMatch(ChartFile chart, PlaylistReferenceMaps referenceMaps)
    {
        return chart != null
            && referenceMaps != null
            && ((!string.IsNullOrWhiteSpace(chart.Md5) && referenceMaps.Md5ToTablesMap?.ContainsKey(chart.Md5) == true)
                || (!string.IsNullOrWhiteSpace(chart.Sha256) && referenceMaps.Sha256ToTablesMap?.ContainsKey(chart.Sha256) == true));
    }

    private static void ApplyBatchReferenceMaps(
        BmsLibraryPlaylistReferenceService service,
        IPlaylistReferenceApplyHost host,
        PlaylistReferenceMaps referenceMaps,
        out long applySongMs,
        out long applyPendingMs,
        out int matchedLibraryCharts,
        out int appliedLibraryCharts,
        out int matchedPendingFiles,
        out int appliedPendingCharts,
        out PlaylistReferenceApplyStats songApplyStats,
        out PlaylistReferenceApplyStats pendingApplyStats)
    {
        applySongMs = 0L;
        applyPendingMs = 0L;
        matchedLibraryCharts = 0;
        matchedPendingFiles = 0;
        appliedLibraryCharts = 0;
        appliedPendingCharts = 0;
        songApplyStats = default;
        pendingApplyStats = default;
        if (!HasReferenceMaps(referenceMaps))
        {
            return;
        }
        var stopwatchApplySong = Stopwatch.StartNew();
        ApplyToLibraryCharts(service, host, referenceMaps, out matchedLibraryCharts, out appliedLibraryCharts, out songApplyStats);
        stopwatchApplySong.Stop();
        applySongMs = stopwatchApplySong.ElapsedMilliseconds;
        var stopwatchApplyPending = Stopwatch.StartNew();
        ApplyToPendingCharts(service, host, referenceMaps, out matchedPendingFiles, out appliedPendingCharts, out pendingApplyStats);
        stopwatchApplyPending.Stop();
        applyPendingMs = stopwatchApplyPending.ElapsedMilliseconds;
    }

    private static void ApplyToLibraryCharts(
        BmsLibraryPlaylistReferenceService service,
        IPlaylistReferenceApplyHost host,
        PlaylistReferenceMaps referenceMaps,
        out int matchedLibraryCharts,
        out int appliedLibraryCharts,
        out PlaylistReferenceApplyStats songApplyStats)
    {
        matchedLibraryCharts = 0;
        appliedLibraryCharts = 0;
        songApplyStats = default;
        List<LibraryChartRef> libraryChartsSnapshot = null;
        if (HasReferenceMaps(referenceMaps))
        {
            libraryChartsSnapshot = host.SnapshotLibraryChartRefsForPlaylistReferenceApply(referenceMaps);
        }
        if (HasReferenceMaps(referenceMaps) && (libraryChartsSnapshot?.Count ?? 0) > 0)
        {
            appliedLibraryCharts = service.ApplyReferenceMap(libraryChartsSnapshot, referenceMaps, out matchedLibraryCharts, out songApplyStats);
        }
    }

    private static void ApplyToPendingCharts(
        BmsLibraryPlaylistReferenceService service,
        IPlaylistReferenceApplyHost host,
        PlaylistReferenceMaps referenceMaps,
        out int matchedPendingFiles,
        out int appliedPendingCharts,
        out PlaylistReferenceApplyStats pendingApplyStats)
    {
        matchedPendingFiles = 0;
        appliedPendingCharts = 0;
        pendingApplyStats = default;
        List<PackageChartEntry> pendingEntriesSnapshot = null;
        if (HasReferenceMaps(referenceMaps))
        {
            pendingEntriesSnapshot = host.SnapshotPendingChartEntriesForPlaylistReferenceApply(referenceMaps);
        }
        if (HasReferenceMaps(referenceMaps) && (pendingEntriesSnapshot?.Count ?? 0) > 0)
        {
            appliedPendingCharts = service.ApplyReferenceMap(pendingEntriesSnapshot, referenceMaps, out matchedPendingFiles, out pendingApplyStats);
        }
    }

    private static bool HasReferenceMaps(PlaylistReferenceMaps referenceMaps)
    {
        return (referenceMaps?.Md5ToTablesMap?.Count ?? 0) + (referenceMaps?.Sha256ToTablesMap?.Count ?? 0) > 0;
    }

    private static List<LibraryChartRef> FilterPlaylistReferenceTargets(IEnumerable<LibraryChartRef> charts, HashSet<string> md5Hashes, HashSet<string> sha256Hashes)
    {
        if (charts == null || ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0))
        {
            return [];
        }
        return [.. charts.Where(chart =>
        {
            if (chart == null)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(chart.Md5) && md5Hashes.Contains(chart.Md5))
            {
                return true;
            }
            return !string.IsNullOrWhiteSpace(chart.Sha256) && sha256Hashes.Contains(chart.Sha256);
        }).Distinct()];
    }

    private static List<PackageChartEntry> FilterPendingPlaylistReferenceTargetEntries(IEnumerable<PackageChartEntry> entries, HashSet<string> md5Hashes, HashSet<string> sha256Hashes)
    {
        if (entries == null || ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0))
        {
            return [];
        }
        List<PackageChartEntry> matchedEntries = [];
        foreach (PackageChartEntry entry in entries)
        {
            ChartFile chart = entry?.Chart;
            if (chart == null || !HasPlaylistReferenceHashMatch(chart, md5Hashes, sha256Hashes))
            {
                continue;
            }
            matchedEntries.Add(entry);
        }
        return [.. matchedEntries.Distinct()];
    }

    private static bool HasPlaylistReferenceHashMatch(ChartFile chart, HashSet<string> md5Hashes, HashSet<string> sha256Hashes)
    {
        return chart != null
            && ((!string.IsNullOrWhiteSpace(chart.Md5) && md5Hashes?.Contains(chart.Md5) == true)
                || (!string.IsNullOrWhiteSpace(chart.Sha256) && sha256Hashes?.Contains(chart.Sha256) == true));
    }

    private static HashSet<string> CreateCombinedPlaylistReferenceHashSet(params IEnumerable<string>[] hashSets)
    {
        var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IEnumerable<string> hashes in hashSets ?? [])
        {
            foreach (string hash in hashes ?? [])
            {
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    combined.Add(hash);
                }
            }
        }
        return combined;
    }

    private static void BuildPlaylistReferenceHashSets(IEnumerable<BMSTableEntry> entries, out HashSet<string> md5Hashes, out HashSet<string> sha256Hashes)
    {
        md5Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sha256Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTableEntry entry in entries ?? [])
        {
            PlaylistEntryLookupKey lookupKey = PlaylistEntryLookupKey.FromEntry(entry);
            if (!lookupKey.HasValue)
            {
                continue;
            }
            if (lookupKey.Kind == PlaylistEntryLookupKeyKind.Md5)
            {
                md5Hashes.Add(lookupKey.Hash);
            }
            else
            {
                sha256Hashes.Add(lookupKey.Hash);
            }
        }
    }
}
