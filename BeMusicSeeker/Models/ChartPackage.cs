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

    internal bool ContainsChartAdapter(BMSFile chartFile)
    {
        if (chartFile == null)
        {
            return false;
        }
        return (ChartEntries ?? []).Any(entry => IsSameChartTarget(entry, chartFile));
    }

    internal List<BMSFile> GetChartAdapters()
    {
        return [.. (ChartEntries ?? [])
            .Select(entry => entry?.GetOrCreateCompatibilityAdapter())
            .Where(file => file != null)];
    }

    internal int GetChartAdapterCount()
    {
        return ChartEntries?.Count ?? 0;
    }

    internal void ClearChartAdapterInstallDestinations()
    {
        foreach (BMSFile chartFile in GetChartAdapters())
        {
            chartFile.instl_dst = null;
        }
    }

    internal void RemoveChartAdaptersByPath(ISet<string> pathsToRemove)
    {
        if (pathsToRemove == null || pathsToRemove.Count == 0)
        {
            return;
        }
        if (hasExplicitChartFiles)
        {
            chartEntries.RemoveAll(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.Path) && pathsToRemove.Contains(entry.Chart.Path));
            return;
        }
        GetOrBuildPackageChartDiscoverySnapshot(out _).RemoveChartEntriesByPath(pathsToRemove);
    }

    internal bool RemoveChartAdapters(Func<BMSFile, bool> predicate)
    {
        if (predicate == null)
        {
            return false;
        }
        List<BMSFile> currentAdapters = GetChartAdapters();
        List<BMSFile> nextAdapters = [.. currentAdapters.Where(file => !predicate(file))];
        if (nextAdapters.Count == currentAdapters.Count)
        {
            return false;
        }
        ReplaceChartAdapters(nextAdapters);
        return true;
    }

    internal bool IsChartAdapterEmpty()
    {
        return (ChartEntries?.Count ?? 0) == 0;
    }

    internal void ApplySingleFileInstallDestination(string destinationDirectory)
    {
        ApplySingleFileInstallDestination(destinationDirectory, GetChartAdapters());
    }

    internal void ApplySingleFileInstallDestination(string destinationDirectory, IEnumerable<BMSFile> chartFiles)
    {
        foreach (BMSFile chartFile in chartFiles ?? [])
        {
            if (chartFile == null)
            {
                continue;
            }
            chartFile.path = Path.Combine(destinationDirectory, Path.GetFileName(chartFile.path));
            ClearInstalledChartAdapterMetadata(chartFile);
        }
    }

    internal void ApplyDirectoryInstallDestination(string sourcePath, string destinationDirectory)
    {
        ApplyDirectoryInstallDestination(sourcePath, destinationDirectory, GetChartAdapters());
    }

    internal void ApplyDirectoryInstallDestination(string sourcePath, string destinationDirectory, IEnumerable<BMSFile> chartFiles)
    {
        foreach (BMSFile chartFile in chartFiles ?? [])
        {
            if (chartFile == null)
            {
                continue;
            }
            chartFile.path = chartFile.path.ReplaceFromStart(sourcePath + Path.DirectorySeparatorChar, destinationDirectory + Path.DirectorySeparatorChar, isIgnoreCase: true);
            ClearInstalledChartAdapterMetadata(chartFile);
        }
    }

    internal void ReplaceChartAdapters(IEnumerable<BMSFile> nextChartFiles)
    {
        List<BMSFile> nextAdapters = [.. (nextChartFiles ?? []).Where(file => file != null)];
        if (hasExplicitChartFiles)
        {
            chartEntries.Clear();
            chartEntries.AddRange(nextAdapters.Select(PackageChartEntry.FromCompatibilityAdapter).Where(entry => entry != null));
            return;
        }
        GetOrBuildPackageChartDiscoverySnapshot(out _).ReplaceCompatibilityAdapters(nextAdapters);
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

    private static void ClearInstalledChartAdapterMetadata(BMSFile chartFile)
    {
        chartFile.parent = null;
        chartFile.folder = null;
        chartFile.adddate = null;
        chartFile.date = null;
    }

    private static bool IsSameChartAdapter(BMSFile left, BMSFile right)
    {
        return left != null
            && right != null
            && (ReferenceEquals(left, right)
                || (!string.IsNullOrWhiteSpace(left.path) && !string.IsNullOrWhiteSpace(right.path) && left.path.Equals(right.path, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsSameChartTarget(PackageChartEntry entry, BMSFile chartFile)
    {
        if (entry == null || chartFile == null)
        {
            return false;
        }
        if (IsSameChartAdapter(entry.CompatibilityAdapter, chartFile))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(entry.Chart?.Path)
            && !string.IsNullOrWhiteSpace(chartFile.path)
            && entry.Chart.Path.Equals(chartFile.path, StringComparison.OrdinalIgnoreCase);
    }

    public ChartPackage()
    {
    }

    public ChartPackage(BMSFile chartFile)
    {
        path = chartFile.path;
        chartEntries = [PackageChartEntry.FromCompatibilityAdapter(chartFile)];
        hasExplicitChartFiles = true;
    }

    public ChartPackage(IEnumerable<BMSFile> chartFiles)
    {
        chartEntries = [.. (chartFiles ?? []).Select(PackageChartEntry.FromCompatibilityAdapter).Where(entry => entry != null)];
        hasExplicitChartFiles = true;
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
