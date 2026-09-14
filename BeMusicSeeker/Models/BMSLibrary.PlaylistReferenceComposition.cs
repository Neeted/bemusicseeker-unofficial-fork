using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    private PlaylistReferenceTableSnapshot SnapshotPlaylistReferenceTable(
        BMSTable table,
        IEnumerable<BMSTableEntry> entries = null,
        bool requireLoaded = false)
    {
        if (table == null)
        {
            return null;
        }
        if (entries == null)
        {
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                if (!table.ArePlaylistEntriesLoaded)
                {
                    if (requireLoaded)
                    {
                        throw new System.InvalidOperationException("Playlist entries are not loaded. table=" + (table.name ?? string.Empty));
                    }
                    return new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, []);
                }
                return new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries.ToArray());
            }
        }
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, entries.ToArray());
        }
    }

    private List<PlaylistReferenceTableSnapshot> SnapshotPlaylistReferenceTables(
        IEnumerable<BMSTable> tables,
        bool requireLoaded = true)
    {
        return [.. (tables ?? [])
            .Where(table => table != null)
            .Select(table => SnapshotPlaylistReferenceTable(table, requireLoaded: requireLoaded))];
    }

    private List<PlaylistReferenceChartSnapshot> SnapshotLibraryChartRefsForPlaylistReferenceApply(PlaylistReferenceLookupKeys lookupKeys)
    {
        return SnapshotLibraryChartRefsForPlaylistReferenceApply(
            CreatePlaylistReferenceHashSet(lookupKeys?.Md5Hashes),
            CreatePlaylistReferenceHashSet(lookupKeys?.Sha256Hashes));
    }

    private List<PlaylistReferenceChartSnapshot> SnapshotLibraryChartRefsForPlaylistReferenceApply(
        ISet<string> md5Hashes,
        ISet<string> sha256Hashes)
    {
        if ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0)
        {
            return [];
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            lock (lockOwnedChartCollection)
            {
                return [.. catalogOwnedCollectionOwner.Collection
                    .CreateLibraryChartRefsForHashes(md5Hashes, sha256Hashes)
                    .Select(PlaylistReferenceChartSnapshot.FromLibraryChartRef)
                    .Where(snapshot => snapshot != null)];
            }
        }
    }

    private List<PlaylistReferenceChartSnapshot> SnapshotPendingChartEntriesForPlaylistReferenceApply(PlaylistReferenceLookupKeys lookupKeys)
    {
        if (ChartPackagesPending == null || ChartPackagesPending.Count == 0
            || lookupKeys?.HasAny != true)
        {
            return [];
        }
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            return [.. ChartPackagesPending
                .Where(pkg => pkg != null)
                .SelectMany(pkg => pkg.ChartEntries)
                .Where(entry => entry?.Chart != null && BmsLibraryPlaylistReferenceOwner.HasPlaylistReferenceMatch(entry.Chart, lookupKeys))
                .Select(PlaylistReferenceChartSnapshot.FromPackageChartEntry)
                .Where(snapshot => snapshot != null)
                .Distinct()];
        }
    }

    private List<PlaylistReferenceChartSnapshot> SnapshotPendingChartEntriesForPlaylistReferenceApply()
    {
        if (ChartPackagesPending == null || ChartPackagesPending.Count == 0)
        {
            return [];
        }
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            return [.. ChartPackagesPending
                .Where(pkg => pkg != null)
                .SelectMany(pkg => pkg.ChartEntries)
                .Where(entry => entry?.Chart != null)
                .Select(PlaylistReferenceChartSnapshot.FromPackageChartEntry)
                .Where(snapshot => snapshot != null)];
        }
    }

    private List<PlaylistReferenceChartSnapshot> SnapshotPackageChartEntriesForPlaylistReferenceApply(
        IEnumerable<ChartPackage> packages,
        PlaylistReferenceLookupKeys lookupKeys)
    {
        using (rwlockPendingInstallCharts.GetReaderGuard())
        using (rwlockBMSFiles.GetReaderGuard())
        {
            return BmsLibraryPlaylistReferenceOwner.FilterPackagePlaylistReferenceTargets(
                SnapshotPackageChartEntriesForPlaylistReferenceApply(packages),
                lookupKeys);
        }
    }

    private static List<PlaylistReferenceChartSnapshot> SnapshotPackageChartEntriesForPlaylistReferenceApply(IEnumerable<ChartPackage> packages)
    {
        return [.. (packages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Where(entry => entry?.Chart != null)
            .Select(PlaylistReferenceChartSnapshot.FromPackageChartEntry)
            .Where(snapshot => snapshot != null)];
    }

    private static HashSet<string> CreatePlaylistReferenceHashSet(IEnumerable<string> hashes)
    {
        return new HashSet<string>(
            (hashes ?? []).Where(hash => !string.IsNullOrWhiteSpace(hash)),
            System.StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> CreateCombinedPlaylistReferenceHashSet(params IEnumerable<string>[] hashSets)
    {
        var combined = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
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
}
