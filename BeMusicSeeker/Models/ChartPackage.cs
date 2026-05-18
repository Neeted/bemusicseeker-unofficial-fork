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
    private readonly List<BMSFile> chartFiles;

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
            List<BMSFile> chartAdapters = GetChartAdapters();
            if (chartAdapters.Count == 0)
            {
                return path ?? string.Empty;
            }

            BMSFile representativeChart = chartAdapters[0];
            return chartAdapters.Count > 1 ? representativeChart.title : representativeChart.Title;
        }
    }

    internal List<PackageChartEntry> ChartEntries
    {
        get
        {
            if (hasExplicitChartFiles)
            {
                return [.. (chartFiles ?? []).Select(PackageChartEntry.FromCompatibilityAdapter).Where(entry => entry != null)];
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
                return chartFiles ?? [];
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
            chartFiles.Clear();
            chartFiles.AddRange(nextAdapters);
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
        chartFiles = [chartFile];
        hasExplicitChartFiles = true;
    }

    public ChartPackage(IEnumerable<BMSFile> chartFiles)
    {
        this.chartFiles = [.. chartFiles];
        hasExplicitChartFiles = true;
    }

    internal PackageInstallEstimationSnapshot GetOrBuildInstallEstimationSnapshot(IEnumerable<BMSFile> targetFiles)
    {
        List<PackageChartEntry> targetEntries = ResolveTargetEntries(targetFiles);
        PackageInstallSurfaceSnapshot installSurfaceSnapshot = GetOrBuildInstallEstimationSurfaceSnapshot(out bool sourceSurfaceCacheHit);
        return PackageInstallEstimationSnapshotBuilder.Build(this, targetEntries, installSurfaceSnapshot, sourceSurfaceCacheHit);
    }

    internal PackageInstallEstimationSnapshot BuildInstallEstimationSnapshot(IEnumerable<BMSFile> targetFiles, PackageInstallSurfaceSnapshot installSurfaceSnapshot, bool sourceSurfaceCacheHit, bool sourceSurfaceBatchHit = false)
    {
        return PackageInstallEstimationSnapshotBuilder.Build(
            this,
            ResolveTargetEntries(targetFiles),
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

    private List<PackageChartEntry> ResolveTargetEntries(IEnumerable<BMSFile> targetFiles)
    {
        List<BMSFile> targetFileList = [.. (targetFiles ?? []).Where(file => file != null)];
        if (targetFileList.Count == 0)
        {
            return ChartEntries ?? [];
        }

        List<PackageChartEntry> packageEntries = ChartEntries ?? [];
        var targetEntries = new List<PackageChartEntry>(targetFileList.Count);
        foreach (BMSFile targetFile in targetFileList)
        {
            PackageChartEntry packageEntry = packageEntries.FirstOrDefault(entry => IsSameChartTarget(entry, targetFile));
            targetEntries.Add(packageEntry ?? PackageChartEntry.FromCompatibilityAdapter(targetFile));
        }
        return [.. targetEntries.Where(entry => entry?.Chart != null)];
    }

    private static bool IsSameChartTarget(PackageChartEntry entry, BMSFile targetFile)
    {
        if (entry?.Chart == null || targetFile == null)
        {
            return false;
        }
        if (IsSameChartAdapter(entry.CompatibilityAdapter, targetFile))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(entry.Chart.Path)
            && !string.IsNullOrWhiteSpace(targetFile.path)
            && entry.Chart.Path.Equals(targetFile.path, StringComparison.OrdinalIgnoreCase);
    }
}
