using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public class ChartPackage : LR2SongDBExtended.install
{
    private readonly List<PackageChartEntry> chartEntries;

    private readonly bool hasExplicitChartFiles;

    private readonly object installEstimationSnapshotLock = new();

    private PackageChartDiscoverySnapshot packageChartDiscoverySnapshot;

    private PackageInstallSurfaceSnapshot packageInstallSurfaceSnapshot;

    internal PendingEstimateDeferredReason DeferredEstimateReason { get; set; }

    public string DisplayTitle
    {
        get
        {
            List<PackageChartEntry> entries = ChartEntries;
            if (entries.Count == 0)
            {
                return path ?? string.Empty;
            }

            ChartFile representativeChart = entries[0].Chart;
            return entries.Count > 1 ? representativeChart?.RawTitle ?? path ?? string.Empty : representativeChart?.Title ?? path ?? string.Empty;
        }
    }

    internal List<PackageChartEntry> ChartEntries
    {
        get
        {
            if (hasExplicitChartFiles)
            {
                return [.. chartEntries ?? []];
            }
            return GetOrBuildPackageChartDiscoverySnapshot(out _).ChartEntries;
        }
    }

    internal bool RemoveChartEntries(Func<PackageChartEntry, bool> predicate)
    {
        if (predicate == null)
        {
            return false;
        }
        List<PackageChartEntry> currentEntries = ChartEntries;
        List<PackageChartEntry> nextEntries = [.. currentEntries.Where(entry => !predicate(entry))];
        if (nextEntries.Count == currentEntries.Count)
        {
            return false;
        }
        ReplaceChartEntries(nextEntries);
        return true;
    }

    internal void ApplySingleFileInstallDestination(string destinationDirectory, IEnumerable<PackageChartEntry> entries)
    {
        foreach (PackageChartEntry entry in entries ?? [])
        {
            string chartPath = entry?.Chart?.Path;
            if (string.IsNullOrWhiteSpace(chartPath))
            {
                continue;
            }
            entry.ApplyInstalledPath(Path.Combine(destinationDirectory, Path.GetFileName(chartPath)));
        }
    }

    internal void ApplyDirectoryInstallDestination(string sourcePath, string destinationDirectory, IEnumerable<PackageChartEntry> entries)
    {
        foreach (PackageChartEntry entry in entries ?? [])
        {
            string chartPath = entry?.Chart?.Path;
            if (string.IsNullOrWhiteSpace(chartPath))
            {
                continue;
            }
            entry.ApplyInstalledPath(chartPath.ReplaceFromStart(sourcePath + Path.DirectorySeparatorChar, destinationDirectory + Path.DirectorySeparatorChar, isIgnoreCase: true));
        }
    }

    internal void ReplaceChartEntries(IEnumerable<PackageChartEntry> nextEntries)
    {
        List<PackageChartEntry> normalizedEntries = [.. NormalizeChartEntries(nextEntries)];
        if (hasExplicitChartFiles)
        {
            chartEntries.Clear();
            chartEntries.AddRange(normalizedEntries);
            return;
        }
        GetOrBuildPackageChartDiscoverySnapshot(out _).ReplaceChartEntries(normalizedEntries);
    }

    private static IEnumerable<PackageChartEntry> NormalizeChartEntries(IEnumerable<PackageChartEntry> entries)
    {
        foreach (PackageChartEntry entry in entries ?? [])
        {
            if (entry?.Chart == null)
            {
                continue;
            }
            yield return entry.CompatibilityAdapter != null
                ? PackageChartEntry.FromCompatibilityAdapter(entry.CompatibilityAdapter)
                : PackageChartEntry.FromChart(entry.Chart);
        }
    }

    public ChartPackage()
    {
    }

    private ChartPackage(IEnumerable<PackageChartEntry> entries, bool hasExplicitChartEntries)
    {
        chartEntries = [.. (entries ?? []).Where(entry => entry?.Chart != null)];
        hasExplicitChartFiles = hasExplicitChartEntries;
    }

    internal static ChartPackage FromChartEntries(IEnumerable<PackageChartEntry> entries)
    {
        return new ChartPackage(entries, hasExplicitChartEntries: true);
    }

    internal PackageInstallEstimationSnapshot GetOrBuildInstallEstimationSnapshotFromEntries(IEnumerable<PackageChartEntry> targetEntries)
    {
        PackageInstallSurfaceSnapshot installSurfaceSnapshot = GetOrBuildInstallEstimationSurfaceSnapshot(out bool sourceSurfaceCacheHit);
        return PackageInstallEstimationSnapshotBuilder.Build(this, targetEntries, installSurfaceSnapshot, sourceSurfaceCacheHit);
    }

    internal PackageInstallEstimationSnapshot BuildInstallEstimationSnapshotFromEntries(IEnumerable<PackageChartEntry> targetEntries, PackageInstallSurfaceSnapshot installSurfaceSnapshot, bool sourceSurfaceCacheHit, bool sourceSurfaceBatchHit = false)
    {
        return PackageInstallEstimationSnapshotBuilder.Build(
            this,
            targetEntries,
            installSurfaceSnapshot,
            sourceSurfaceCacheHit,
            sourceSurfaceBatchHit);
    }

    internal void InvalidateInstallEstimationSnapshot()
    {
        lock (installEstimationSnapshotLock)
        {
            packageChartDiscoverySnapshot = null;
            packageInstallSurfaceSnapshot = null;
        }
    }

    private PackageInstallSurfaceSnapshot GetOrBuildInstallEstimationSurfaceSnapshot(out bool cacheHit)
    {
        lock (installEstimationSnapshotLock)
        {
            if (packageInstallSurfaceSnapshot == null
                || !string.Equals(packageInstallSurfaceSnapshot.SourcePath, path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                packageInstallSurfaceSnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(path);
                cacheHit = false;
                return packageInstallSurfaceSnapshot ?? PackageInstallSurfaceSnapshot.Empty;
            }
            cacheHit = true;
            return packageInstallSurfaceSnapshot;
        }
    }

    private PackageChartDiscoverySnapshot GetOrBuildPackageChartDiscoverySnapshot(out bool cacheHit)
    {
        lock (installEstimationSnapshotLock)
        {
            if (packageChartDiscoverySnapshot == null
                || !string.Equals(packageChartDiscoverySnapshot.SourcePath, path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                packageChartDiscoverySnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageChartDiscoverySnapshot(path);
                cacheHit = false;
                return packageChartDiscoverySnapshot ?? new PackageChartDiscoverySnapshot();
            }
            cacheHit = true;
            return packageChartDiscoverySnapshot;
        }
    }

}
