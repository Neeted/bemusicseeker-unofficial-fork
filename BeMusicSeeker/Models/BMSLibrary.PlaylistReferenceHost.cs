using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IPlaylistReferenceApplyHost
{
    void IPlaylistReferenceApplyHost.ReplacePlaylistReferenceIndexTable(BMSTable table, IEnumerable<BMSTableEntry> entries)
    {
        playlistReferenceManager.ReplaceTable(table, entries);
    }

    void IPlaylistReferenceApplyHost.RemovePlaylistReferenceIndexTable(BMSTable table)
    {
        playlistReferenceManager.RemoveTable(table);
    }

    void IPlaylistReferenceApplyHost.RemovePlaylistReferenceIndexTables(IEnumerable<BMSTable> tables)
    {
        playlistReferenceManager.RemoveTables(tables);
    }

    void IPlaylistReferenceApplyHost.SynchronizePlaylistReferenceIndex(IEnumerable<BMSTable> tables)
    {
        playlistReferenceManager.Synchronize(tables);
    }

    List<LibraryChartRef> IPlaylistReferenceApplyHost.SnapshotLibraryChartRefsForPlaylistReferenceApply(PlaylistReferenceMaps referenceMaps)
    {
        return SnapshotLibraryChartRefsForPlaylistReferenceApplyCore(
            CreatePlaylistReferenceHashSet(referenceMaps?.Md5ToTablesMap?.Keys),
            CreatePlaylistReferenceHashSet(referenceMaps?.Sha256ToTablesMap?.Keys));
    }

    List<LibraryChartRef> IPlaylistReferenceApplyHost.SnapshotLibraryChartRefsForPlaylistReferenceApply(ISet<string> md5Hashes, ISet<string> sha256Hashes)
    {
        return SnapshotLibraryChartRefsForPlaylistReferenceApplyCore(md5Hashes, sha256Hashes);
    }

    private List<LibraryChartRef> SnapshotLibraryChartRefsForPlaylistReferenceApplyCore(ISet<string> md5Hashes, ISet<string> sha256Hashes)
    {
        if ((md5Hashes?.Count ?? 0) == 0 && (sha256Hashes?.Count ?? 0) == 0)
        {
            return null;
        }
        using (rwlockBMSFiles.GetReaderGuard())
        {
            EnsureOwnedChartCollectionBuiltUnsafe();
            List<LibraryChartRef> charts;
            lock (lockOwnedChartCollection)
            {
                charts = catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefsForHashes(md5Hashes, sha256Hashes);
            }
            return charts.Count == 0 ? null : charts;
        }
    }

    List<PackageChartEntry> IPlaylistReferenceApplyHost.SnapshotPendingChartEntriesForPlaylistReferenceApply(PlaylistReferenceMaps referenceMaps)
    {
        if (ChartPackagesPending == null || ChartPackagesPending.Count == 0)
        {
            return null;
        }
        if ((referenceMaps?.Md5ToTablesMap?.Count ?? 0) == 0 && (referenceMaps?.Sha256ToTablesMap?.Count ?? 0) == 0)
        {
            return null;
        }
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            List<PackageChartEntry> matchedEntries = [];
            foreach (PackageChartEntry entry in ChartPackagesPending.Where(pkg => pkg != null).SelectMany(pkg => pkg.ChartEntries))
            {
                ChartFile chart = entry?.Chart;
                if (chart == null || !PlaylistReferenceApplyCoordinator.HasPlaylistReferenceMapMatch(chart, referenceMaps))
                {
                    continue;
                }
                matchedEntries.Add(entry);
            }
            return [.. matchedEntries.Distinct()];
        }
    }

    List<PackageChartEntry> IPlaylistReferenceApplyHost.SnapshotPendingChartEntriesForPlaylistReferenceApply()
    {
        if (ChartPackagesPending == null || ChartPackagesPending.Count == 0)
        {
            return null;
        }
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            return [.. ChartPackagesPending.Where(pkg => pkg != null).SelectMany(pkg => pkg.ChartEntries).Where(entry => entry?.Chart != null)];
        }
    }

    List<PackageChartEntry> IPlaylistReferenceApplyHost.SnapshotPackageChartEntriesForPlaylistReferenceApply(IEnumerable<ChartPackage> packages, PlaylistReferenceMaps referenceMaps)
    {
        using (rwlockPendingInstallCharts.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetReaderGuard())
            {
                return PlaylistReferenceApplyCoordinator.FilterPackagePlaylistReferenceTargets(SnapshotPackageChartEntriesForPlaylistReferenceApply(packages), referenceMaps);
            }
        }
    }

    void IPlaylistReferenceApplyHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }

    private static List<PackageChartEntry> SnapshotPackageChartEntriesForPlaylistReferenceApply(IEnumerable<ChartPackage> packages)
    {
        return [.. (packages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Where(entry => entry?.Chart != null)];
    }

    private static HashSet<string> CreatePlaylistReferenceHashSet(IEnumerable<string> hashes)
    {
        return new HashSet<string>(
            (hashes ?? []).Where(hash => !string.IsNullOrWhiteSpace(hash)),
            StringComparer.OrdinalIgnoreCase);
    }
}
