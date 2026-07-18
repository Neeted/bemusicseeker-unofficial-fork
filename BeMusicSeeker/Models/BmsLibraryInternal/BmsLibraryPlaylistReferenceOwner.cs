using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PlaylistReferenceTableSnapshot
{
    internal PlaylistReferenceTableSnapshot(
        BMSTable table,
        string symbol,
        string name,
        IEnumerable<BMSTableEntry> entries)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        Symbol = symbol ?? string.Empty;
        Name = name ?? string.Empty;
        Entries = Array.AsReadOnly([.. (entries ?? [])
            .Where(entry => entry?.is_removed == false)
            .Select(entry => new PlaylistReferenceEntrySnapshot(entry))]);
    }

    internal BMSTable Table { get; }

    internal string Symbol { get; }

    internal string Name { get; }

    internal IReadOnlyList<PlaylistReferenceEntrySnapshot> Entries { get; }
}

internal sealed class PlaylistReferenceTableDisplaySnapshot
{
    internal PlaylistReferenceTableDisplaySnapshot(string symbol, string name)
    {
        Symbol = symbol ?? string.Empty;
        Name = name ?? string.Empty;
    }

    internal string Symbol { get; }

    internal string Name { get; }
}

internal sealed class PlaylistReferenceEntrySnapshot
{
    internal PlaylistReferenceEntrySnapshot(BMSTableEntry entry)
    {
        LookupKey = PlaylistEntryLookupKey.FromEntry(entry);
    }

    internal PlaylistEntryLookupKey LookupKey { get; }
}

internal sealed class PlaylistReferenceChartSnapshot
{
    internal PlaylistReferenceChartSnapshot(string md5, string sha256)
    {
        Md5 = NormalizeHash(md5);
        Sha256 = NormalizeHash(sha256);
    }

    internal string Md5 { get; }

    internal string Sha256 { get; }

    internal static PlaylistReferenceChartSnapshot FromLibraryChartRef(LibraryChartRef chart)
    {
        return chart == null ? null : new PlaylistReferenceChartSnapshot(chart.Md5, chart.Sha256);
    }

    internal static PlaylistReferenceChartSnapshot FromPackageChartEntry(PackageChartEntry entry)
    {
        return entry?.Chart == null ? null : new PlaylistReferenceChartSnapshot(entry.Chart.Md5, entry.Chart.Sha256);
    }

    private static string NormalizeHash(string hash)
    {
        return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();
    }
}

internal sealed class PlaylistReferenceCatalogApplyResult
{
    internal static PlaylistReferenceCatalogApplyResult Empty { get; } = new(0, 0, 0);

    internal PlaylistReferenceCatalogApplyResult(int catalogVersion, int affectedChartCount, int matchedChartCount)
    {
        CatalogVersion = catalogVersion;
        AffectedChartCount = affectedChartCount;
        MatchedChartCount = matchedChartCount;
    }

    internal int CatalogVersion { get; }

    internal int AffectedChartCount { get; }

    internal int MatchedChartCount { get; }
}

/// <summary>
/// Owns playlist-reference index state and the reference-apply behavior.
/// Source owners hand this object immutable snapshots; it does not own catalog,
/// package, playlist-persistence, or LR2 state.
/// </summary>
internal sealed class BmsLibraryPlaylistReferenceOwner
{
    private readonly object syncRoot = new();

    private readonly BmsLibraryPlaylistReferenceService service;

    private PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;

    internal BmsLibraryPlaylistReferenceOwner(int playlistReferenceApplyChunkSize)
    {
        service = new BmsLibraryPlaylistReferenceService(playlistReferenceApplyChunkSize);
    }

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

    /// <summary>
    /// Applies a catalog mutation receipt to the newly visible owned-chart
    /// projection. The reference index is table-owned, so the receipt does not
    /// mutate catalog state; it scopes a post-commit immutable projection pass.
    /// </summary>
    internal PlaylistReferenceCatalogApplyResult ApplyCatalogMutationReceipt(
        CatalogMutationReceipt receipt,
        IEnumerable<PlaylistReferenceChartSnapshot> affectedCharts)
    {
        if (receipt?.Applied != true || affectedCharts == null)
        {
            return PlaylistReferenceCatalogApplyResult.Empty;
        }
        PlaylistReferenceLookupKeys lookupKeys;
        lock (syncRoot)
        {
            lookupKeys = (index ?? PlaylistReferenceIndex.Empty).CreateLookupKeysSnapshot();
        }
        List<PlaylistReferenceChartSnapshot> chartSnapshots = [.. affectedCharts.Where(chart => chart != null)];
        int affectedChartCount = chartSnapshots.Count;
        int matchedChartCount;
        ApplyReferenceMap(
            chartSnapshots,
            lookupKeys,
            out matchedChartCount,
            out _,
            out _);
        return new PlaylistReferenceCatalogApplyResult(
            receipt.OwnedCollectionVersion,
            affectedChartCount,
            matchedChartCount);
    }

    internal PlaylistReferenceLookupKeys BuildReferenceLookupKeys(PlaylistReferenceTableSnapshot table)
    {
        return BuildReferenceLookupKeys(table == null ? [] : [table]);
    }

    internal PlaylistReferenceLookupKeys BuildReferenceLookupKeys(IEnumerable<PlaylistReferenceTableSnapshot> tables)
    {
        var md5Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sha256Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlaylistReferenceTableSnapshot snapshot in tables ?? [])
        {
            AddEntriesToLookupKeys(md5Hashes, sha256Hashes, snapshot);
        }
        return new PlaylistReferenceLookupKeys(md5Hashes, sha256Hashes);
    }

    internal void AddReferenceBMSTable(
        PlaylistReferenceTableSnapshot tableSnapshot,
        PlaylistReferenceLookupKeys lookupKeys,
        IEnumerable<PlaylistReferenceChartSnapshot> libraryCharts,
        IEnumerable<PlaylistReferenceChartSnapshot> pendingEntries,
        Action<string> logInstallPerformance)
    {
        if (tableSnapshot?.Table == null)
        {
            return;
        }

        BMSTable table = tableSnapshot.Table;
        lookupKeys ??= BuildReferenceLookupKeys(tableSnapshot);
        var stopwatchBuildMap = Stopwatch.StartNew();
        ReplaceTable(tableSnapshot);
        stopwatchBuildMap.Stop();

        var stopwatchApplySong = Stopwatch.StartNew();
        ApplyReferenceMap(libraryCharts, lookupKeys, out int matchedLibraryCharts, out int appliedLibraryCharts, out PlaylistReferenceApplyStats songApplyStats);
        stopwatchApplySong.Stop();
        var stopwatchApplyPending = Stopwatch.StartNew();
        ApplyReferenceMap(pendingEntries, lookupKeys, out int matchedPendingFiles, out int appliedPendingCharts, out PlaylistReferenceApplyStats pendingApplyStats);
        stopwatchApplyPending.Stop();

        logInstallPerformance?.Invoke(
            "playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds
            + " applySongMs=" + stopwatchApplySong.ElapsedMilliseconds
            + " applySongChunks=" + songApplyStats.Chunks
            + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs
            + " applySongYieldCount=" + songApplyStats.YieldCount
            + " applyPendingMs=" + stopwatchApplyPending.ElapsedMilliseconds
            + " applyPendingChunks=" + pendingApplyStats.Chunks
            + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs
            + " applyPendingYieldCount=" + pendingApplyStats.YieldCount
            + " mapMd5Count=" + (lookupKeys?.Md5Hashes?.Count ?? 0)
            + " mapSha256Count=" + (lookupKeys?.Sha256Hashes?.Count ?? 0)
            + " tableCount=1"
            + " matchedLibraryCharts=" + matchedLibraryCharts
            + " appliedLibraryCharts=" + appliedLibraryCharts
            + " matchedPendingFiles=" + matchedPendingFiles
            + " appliedPendingCharts=" + appliedPendingCharts);
    }

    internal void AddReferenceBMSTables(
        IEnumerable<PlaylistReferenceTableSnapshot> tables,
        PlaylistReferenceLookupKeys lookupKeys,
        IEnumerable<PlaylistReferenceChartSnapshot> libraryCharts,
        IEnumerable<PlaylistReferenceChartSnapshot> pendingEntries,
        Action<string> logInstallPerformance)
    {
        List<PlaylistReferenceTableSnapshot> tableList = [.. (tables ?? []).Where(snapshot => snapshot?.Table != null)];
        if (tableList.Count == 0)
        {
            return;
        }

        lookupKeys ??= BuildReferenceLookupKeys(tableList);
        var stopwatchBuildMap = Stopwatch.StartNew();
        Synchronize(tableList);
        stopwatchBuildMap.Stop();
        ApplyBatchReferenceMaps(
            lookupKeys,
            libraryCharts,
            pendingEntries,
            out long applySongMs,
            out long applyPendingMs,
            out int matchedLibraryCharts,
            out int appliedLibraryCharts,
            out int matchedPendingFiles,
            out int appliedPendingCharts,
            out PlaylistReferenceApplyStats songApplyStats,
            out PlaylistReferenceApplyStats pendingApplyStats);

        logInstallPerformance?.Invoke(
            "playlist_ref_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds
            + " applySongMs=" + applySongMs
            + " applySongChunks=" + songApplyStats.Chunks
            + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs
            + " applySongYieldCount=" + songApplyStats.YieldCount
            + " applyPendingMs=" + applyPendingMs
            + " applyPendingChunks=" + pendingApplyStats.Chunks
            + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs
            + " applyPendingYieldCount=" + pendingApplyStats.YieldCount
            + " mapMd5Count=" + (lookupKeys?.Md5Hashes?.Count ?? 0)
            + " mapSha256Count=" + (lookupKeys?.Sha256Hashes?.Count ?? 0)
            + " tableCount=" + tableList.Count
            + " matchedLibraryCharts=" + matchedLibraryCharts
            + " appliedLibraryCharts=" + appliedLibraryCharts
            + " matchedPendingFiles=" + matchedPendingFiles
            + " appliedPendingCharts=" + appliedPendingCharts);
    }

    internal void AddReferenceBMSTablesIncremental(
        IEnumerable<PlaylistReferenceTableSnapshot> tables,
        PlaylistReferenceLookupKeys lookupKeys,
        IEnumerable<PlaylistReferenceChartSnapshot> libraryCharts,
        IEnumerable<PlaylistReferenceChartSnapshot> pendingEntries,
        Action<string> logInstallPerformance)
    {
        List<PlaylistReferenceTableSnapshot> tableList = [.. (tables ?? []).Where(snapshot => snapshot?.Table != null).GroupBy(snapshot => snapshot.Table).Select(group => group.First())];
        if (tableList.Count == 0)
        {
            return;
        }

        lookupKeys ??= BuildReferenceLookupKeys(tableList);
        var stopwatchBuildMap = Stopwatch.StartNew();
        foreach (PlaylistReferenceTableSnapshot tableSnapshot in tableList)
        {
            ReplaceTable(tableSnapshot);
        }
        stopwatchBuildMap.Stop();
        ApplyBatchReferenceMaps(
            lookupKeys,
            libraryCharts,
            pendingEntries,
            out long applySongMs,
            out long applyPendingMs,
            out int matchedLibraryCharts,
            out int appliedLibraryCharts,
            out int matchedPendingFiles,
            out int appliedPendingCharts,
            out PlaylistReferenceApplyStats songApplyStats,
            out PlaylistReferenceApplyStats pendingApplyStats);

        logInstallPerformance?.Invoke(
            "playlist_ref_incremental_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds
            + " applySongMs=" + applySongMs
            + " applySongChunks=" + songApplyStats.Chunks
            + " applySongChunkMaxMs=" + songApplyStats.MaxChunkMs
            + " applySongYieldCount=" + songApplyStats.YieldCount
            + " applyPendingMs=" + applyPendingMs
            + " applyPendingChunks=" + pendingApplyStats.Chunks
            + " applyPendingChunkMaxMs=" + pendingApplyStats.MaxChunkMs
            + " applyPendingYieldCount=" + pendingApplyStats.YieldCount
            + " mapMd5Count=" + (lookupKeys?.Md5Hashes?.Count ?? 0)
            + " mapSha256Count=" + (lookupKeys?.Sha256Hashes?.Count ?? 0)
            + " tableCount=" + tableList.Count
            + " matchedLibraryCharts=" + matchedLibraryCharts
            + " appliedLibraryCharts=" + appliedLibraryCharts
            + " matchedPendingFiles=" + matchedPendingFiles
            + " appliedPendingCharts=" + appliedPendingCharts);
    }

    internal void AddReferenceBMSTablesToPackageCharts(
        IEnumerable<PlaylistReferenceTableSnapshot> tables,
        PlaylistReferenceLookupKeys lookupKeys,
        IEnumerable<PlaylistReferenceChartSnapshot> packageEntries,
        int packageCount,
        Action<string> logInstallPerformance)
    {
        List<PlaylistReferenceTableSnapshot> tableList = [.. (tables ?? []).Where(snapshot => snapshot?.Table != null)];
        if (tableList.Count == 0)
        {
            return;
        }

        lookupKeys ??= BuildReferenceLookupKeys(tableList);
        var stopwatchBuildMap = Stopwatch.StartNew();
        stopwatchBuildMap.Stop();
        int matchedPackageFiles = 0;
        int appliedPackageCharts = 0;
        PlaylistReferenceApplyStats packageApplyStats = default;
        long applyPackageMs = 0L;
        if (HasReferenceKeys(lookupKeys))
        {
            var stopwatchApplyPackage = Stopwatch.StartNew();
            List<PlaylistReferenceChartSnapshot> packageEntriesSnapshot = [.. (packageEntries ?? []).Where(entry => entry != null).Distinct()];
            if (packageEntriesSnapshot.Count > 0)
            {
                appliedPackageCharts = service.ApplyReferenceMap(packageEntriesSnapshot, lookupKeys, out matchedPackageFiles, out packageApplyStats);
            }
            stopwatchApplyPackage.Stop();
            applyPackageMs = stopwatchApplyPackage.ElapsedMilliseconds;
        }
        logInstallPerformance?.Invoke(
            "playlist_ref_package_batch buildMapMs=" + stopwatchBuildMap.ElapsedMilliseconds
            + " applyPackageMs=" + applyPackageMs
            + " applyPackageChunks=" + packageApplyStats.Chunks
            + " applyPackageChunkMaxMs=" + packageApplyStats.MaxChunkMs
            + " applyPackageYieldCount=" + packageApplyStats.YieldCount
            + " mapMd5Count=" + (lookupKeys?.Md5Hashes?.Count ?? 0)
            + " mapSha256Count=" + (lookupKeys?.Sha256Hashes?.Count ?? 0)
            + " tableCount=" + tableList.Count
            + " packageCount=" + packageCount
            + " matchedPackageFiles=" + matchedPackageFiles
            + " appliedPackageCharts=" + appliedPackageCharts);
    }

    internal void ReplaceReferenceBMSTable(
        PlaylistReferenceTableSnapshot oldTableSnapshot,
        PlaylistReferenceTableSnapshot newTableSnapshot,
        IEnumerable<PlaylistReferenceChartSnapshot> libraryCharts,
        IEnumerable<PlaylistReferenceChartSnapshot> pendingEntries,
        Action<string> logInstallPerformance)
    {
        BMSTable oldTable = oldTableSnapshot?.Table;
        BMSTable newTable = newTableSnapshot?.Table;
        IReadOnlyList<PlaylistReferenceEntrySnapshot> oldEntriesSnapshot = oldTableSnapshot?.Entries ?? [];
        IReadOnlyList<PlaylistReferenceEntrySnapshot> newEntriesSnapshot = newTableSnapshot?.Entries ?? [];
        BuildPlaylistReferenceHashSets(oldEntriesSnapshot, out HashSet<string> oldMd5Hashes, out HashSet<string> oldSha256Hashes);
        BuildPlaylistReferenceHashSets(newEntriesSnapshot, out HashSet<string> newMd5Hashes, out HashSet<string> newSha256Hashes);
        List<PlaylistReferenceChartSnapshot> libraryChartsSnapshot = [.. (libraryCharts ?? []).Where(chart => chart != null)];
        List<PlaylistReferenceChartSnapshot> pendingEntriesSnapshot = [.. (pendingEntries ?? []).Where(entry => entry != null)];
        List<PlaylistReferenceChartSnapshot> oldLibraryCharts = FilterPlaylistReferenceTargets(libraryChartsSnapshot, oldMd5Hashes, oldSha256Hashes);
        List<PlaylistReferenceChartSnapshot> oldPendingEntries = FilterPendingPlaylistReferenceTargetEntries(pendingEntriesSnapshot, oldMd5Hashes, oldSha256Hashes);
        List<PlaylistReferenceChartSnapshot> newLibraryCharts = FilterPlaylistReferenceTargets(libraryChartsSnapshot, newMd5Hashes, newSha256Hashes);
        List<PlaylistReferenceChartSnapshot> newPendingEntries = FilterPendingPlaylistReferenceTargetEntries(pendingEntriesSnapshot, newMd5Hashes, newSha256Hashes);
        logInstallPerformance?.Invoke(
            "playlist_ref_replace targetsOldLibraryCharts=" + oldLibraryCharts.Count
            + " targetsOldPending=" + oldPendingEntries.Count
            + " targetsNewLibraryCharts=" + newLibraryCharts.Count
            + " targetsNewPending=" + newPendingEntries.Count
            + " oldEntryCount=" + oldEntriesSnapshot.Count
            + " newEntryCount=" + newEntriesSnapshot.Count);
        if (newTable != null)
        {
            ReplaceTables(oldTable, newTableSnapshot);
        }
        else if (oldTable != null)
        {
            RemoveTable(oldTable);
        }
    }

    internal void AddReferenceBMSTablesToCharts(PlaylistReferenceTableSnapshot tableSnapshot)
    {
        ReplaceTable(tableSnapshot);
    }

    internal void RefreshReferenceDisplayForTable(PlaylistReferenceTableSnapshot tableSnapshot)
    {
        ReplaceTable(tableSnapshot);
    }

    internal void SynchronizeReferenceBMSTables(IEnumerable<PlaylistReferenceTableSnapshot> tables)
    {
        Synchronize([.. (tables ?? []).Where(snapshot => snapshot?.Table != null).GroupBy(snapshot => snapshot.Table).Select(group => group.First())]);
    }

    internal void RemoveReferenceBMSTables(PlaylistReferenceTableSnapshot tableSnapshot, bool removeTable)
    {
        BMSTable table = tableSnapshot?.Table;
        if (table == null)
        {
            return;
        }
        if (removeTable)
        {
            RemoveTable(table);
            return;
        }
        ReplaceTable(tableSnapshot);
    }

    internal void RemoveReferenceBMSTables(IEnumerable<PlaylistReferenceTableSnapshot> tables)
    {
        List<BMSTable> tableList = [.. (tables ?? [])
            .Where(snapshot => snapshot?.Table != null)
            .Select(snapshot => snapshot.Table)
            .Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }
        lock (syncRoot)
        {
            foreach (BMSTable table in tableList)
            {
                index?.RemoveTable(table);
            }
        }
    }

    private void ReplaceTable(PlaylistReferenceTableSnapshot snapshot)
    {
        if (snapshot?.Table == null)
        {
            return;
        }
        lock (syncRoot)
        {
            index ??= PlaylistReferenceIndex.Empty;
            index.ReplaceSnapshotTable(snapshot);
        }
    }

    private void ReplaceTables(BMSTable oldTable, PlaylistReferenceTableSnapshot newSnapshot)
    {
        lock (syncRoot)
        {
            index ??= PlaylistReferenceIndex.Empty;
            index.RemoveTable(oldTable);
            index.ReplaceSnapshotTable(newSnapshot);
        }
    }

    private void RemoveTable(BMSTable table)
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

    private void Synchronize(IEnumerable<PlaylistReferenceTableSnapshot> tables)
    {
        lock (syncRoot)
        {
            PlaylistReferenceIndex nextIndex = PlaylistReferenceIndex.FromSnapshots(tables);
            index = nextIndex;
        }
    }

    internal static bool HasPlaylistReferenceMatch(ChartFile chart, PlaylistReferenceLookupKeys lookupKeys)
    {
        return chart != null
            && lookupKeys != null
            && (lookupKeys.ContainsMd5(chart.Md5) || lookupKeys.ContainsSha256(chart.Sha256));
    }

    internal static bool HasPlaylistReferenceMatch(PlaylistReferenceChartSnapshot chart, PlaylistReferenceLookupKeys lookupKeys)
    {
        return chart != null
            && lookupKeys != null
            && (lookupKeys.ContainsMd5(chart.Md5) || lookupKeys.ContainsSha256(chart.Sha256));
    }

    internal static List<PlaylistReferenceChartSnapshot> FilterPackagePlaylistReferenceTargets(IEnumerable<PlaylistReferenceChartSnapshot> entries, PlaylistReferenceLookupKeys lookupKeys)
    {
        if (entries == null || !HasReferenceKeys(lookupKeys))
        {
            return [];
        }
        return [.. entries.Where(entry => entry != null && HasPlaylistReferenceMatch(entry, lookupKeys)).Distinct()];
    }

    internal static void BuildPlaylistReferenceHashSets(
        IEnumerable<PlaylistReferenceEntrySnapshot> entries,
        out HashSet<string> md5Hashes,
        out HashSet<string> sha256Hashes)
    {
        md5Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sha256Hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlaylistReferenceEntrySnapshot entry in entries ?? [])
        {
            PlaylistEntryLookupKey lookupKey = entry?.LookupKey ?? default;
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

    private static void AddEntriesToLookupKeys(
        HashSet<string> md5Hashes,
        HashSet<string> sha256Hashes,
        PlaylistReferenceTableSnapshot snapshot)
    {
        BMSTable table = snapshot?.Table;
        if (table == null || snapshot?.Entries == null)
        {
            return;
        }
        foreach (PlaylistReferenceEntrySnapshot entry in snapshot.Entries)
        {
            PlaylistEntryLookupKey lookupKey = entry?.LookupKey ?? default;
            if (!lookupKey.HasValue)
            {
                continue;
            }
            HashSet<string> target = lookupKey.Kind == PlaylistEntryLookupKeyKind.Md5
                ? md5Hashes
                : sha256Hashes;
            if (target != null)
            {
                target.Add(lookupKey.Hash);
            }
        }
    }

    private void ApplyBatchReferenceMaps(
        PlaylistReferenceLookupKeys lookupKeys,
        IEnumerable<PlaylistReferenceChartSnapshot> libraryCharts,
        IEnumerable<PlaylistReferenceChartSnapshot> pendingEntries,
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
        if (!HasReferenceKeys(lookupKeys))
        {
            return;
        }
        var stopwatchApplySong = Stopwatch.StartNew();
        ApplyReferenceMap(libraryCharts, lookupKeys, out matchedLibraryCharts, out appliedLibraryCharts, out songApplyStats);
        stopwatchApplySong.Stop();
        applySongMs = stopwatchApplySong.ElapsedMilliseconds;
        var stopwatchApplyPending = Stopwatch.StartNew();
        ApplyReferenceMap(pendingEntries, lookupKeys, out matchedPendingFiles, out appliedPendingCharts, out pendingApplyStats);
        stopwatchApplyPending.Stop();
        applyPendingMs = stopwatchApplyPending.ElapsedMilliseconds;
    }

    private void ApplyReferenceMap(
        IEnumerable<PlaylistReferenceChartSnapshot> charts,
        PlaylistReferenceLookupKeys lookupKeys,
        out int matchedCharts,
        out int appliedCharts,
        out PlaylistReferenceApplyStats applyStats)
    {
        appliedCharts = 0;
        applyStats = default;
        if (charts != null && HasReferenceKeys(lookupKeys))
        {
            appliedCharts = service.ApplyReferenceMap(charts, lookupKeys, out matchedCharts, out applyStats);
            return;
        }
        matchedCharts = 0;
    }

    private static bool HasReferenceKeys(PlaylistReferenceLookupKeys lookupKeys)
    {
        return lookupKeys?.HasAny == true;
    }

    private static List<PlaylistReferenceChartSnapshot> FilterPlaylistReferenceTargets(
        IEnumerable<PlaylistReferenceChartSnapshot> charts,
        HashSet<string> md5Hashes,
        HashSet<string> sha256Hashes)
    {
        if (charts == null || ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0))
        {
            return [];
        }
        return [.. charts.Where(chart => chart != null
            && ((!string.IsNullOrWhiteSpace(chart.Md5) && md5Hashes.Contains(chart.Md5))
                || (!string.IsNullOrWhiteSpace(chart.Sha256) && sha256Hashes.Contains(chart.Sha256)))).Distinct()];
    }

    private static List<PlaylistReferenceChartSnapshot> FilterPendingPlaylistReferenceTargetEntries(
        IEnumerable<PlaylistReferenceChartSnapshot> entries,
        HashSet<string> md5Hashes,
        HashSet<string> sha256Hashes)
    {
        if (entries == null || ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0))
        {
            return [];
        }
        return [.. entries.Where(entry => entry != null
            && ((!string.IsNullOrWhiteSpace(entry.Md5) && md5Hashes.Contains(entry.Md5))
                || (!string.IsNullOrWhiteSpace(entry.Sha256) && sha256Hashes.Contains(entry.Sha256)))).Distinct()];
    }
}
