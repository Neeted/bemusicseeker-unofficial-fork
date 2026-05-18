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

    public List<PendingChartEntry> PendingCharts => [.. ChartEntries.Select(entry => entry.GetOrCreateCompatibilityAdapter()).OfType<PendingChartEntry>()];

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

    private List<BMSFile> ChartFiles
    {
        get
        {
            if (hasExplicitChartFiles)
            {
                return [.. (chartEntries ?? []).Select(entry => entry?.GetOrCreateCompatibilityAdapter()).Where(file => file != null)];
            }
            return GetOrBuildPackageChartDiscoverySnapshot(out _).ChartFiles;
        }
    }

    internal bool ContainsChartAdapter(BMSFile chartFile)
    {
        return (ChartFiles ?? []).Any(file => IsSameChartAdapter(file, chartFile));
    }

    internal List<BMSFile> GetChartAdapters()
    {
        return [.. (ChartFiles ?? []).Where(file => file != null)];
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
        ReplaceChartAdapters(GetChartAdapters().Where(file => string.IsNullOrWhiteSpace(file.path) || !pathsToRemove.Contains(file.path)));
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
        foreach (BMSFile chartFile in GetChartAdapters())
        {
            chartFile.path = Path.Combine(destinationDirectory, Path.GetFileName(chartFile.path));
            ClearInstalledChartAdapterMetadata(chartFile);
        }
    }

    internal void ApplyDirectoryInstallDestination(string sourcePath, string destinationDirectory)
    {
        foreach (BMSFile chartFile in GetChartAdapters())
        {
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
