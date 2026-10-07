using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public class ChartPackage : LR2SongDBExtended.install
{
    private List<PackageChartEntry> chartEntries;

    private bool hasExplicitChartFiles;

    private readonly object installEstimationSnapshotLock = new();

    private PackageChartDiscoverySnapshot packageChartDiscoverySnapshot;

    private PackageInstallSurfaceSnapshot packageInstallSurfaceSnapshot;

    internal PendingEstimateDeferredReason DeferredEstimateReason { get; set; }

    /// <summary>確定後、受付権とモデル排他を解放してからパスとヘッダーを公開します。</summary>
    internal void PublishPath()
    {
        RaisePropertyChanged(nameof(path));
        PublishDisplayTitle();
    }

    /// <summary>代表entryの確定後にヘッダーだけを公開します。所属集合通知は発行しません。</summary>
    internal void PublishDisplayTitle() => RaisePropertyChanged(nameof(DisplayTitle));

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

    /// <summary>具体化済みの所属列が変わった時に、派生参照索引へだけ通知します。</summary>
    internal event Action<ChartPackage> ChartEntriesChanged;

    internal List<PackageChartEntry> ChartEntries
    {
        get
        {
            if (hasExplicitChartFiles)
            {
                return [.. chartEntries ?? []];
            }
            PackageChartDiscoverySnapshot snapshot = GetOrBuildPackageChartDiscoverySnapshot(out bool cacheHit);
            if (!cacheHit)
            {
                ChartEntriesChanged?.Invoke(this);
            }
            return snapshot.ChartEntries;
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

    /// <summary>確定した所属列を保持します。残存entryを複製せず、以後のパス変更で再探索しません。</summary>
    internal void ReplaceChartEntries(IEnumerable<PackageChartEntry> nextEntries)
    {
        List<PackageChartEntry> normalizedEntries = [.. NormalizeChartEntries(nextEntries)];
        lock (installEstimationSnapshotLock)
        {
            chartEntries = normalizedEntries;
            hasExplicitChartFiles = true;
        }
        ChartEntriesChanged?.Invoke(this);
    }

    /// <summary>既に取得した所属列だけを捕捉します。未取得のファイル探索や解析を開始しません。</summary>
    internal IReadOnlyList<PackageChartEntry> CaptureMaterializedChartEntries()
    {
        lock (installEstimationSnapshotLock)
        {
            return hasExplicitChartFiles ? [.. chartEntries ?? []] : packageChartDiscoverySnapshot?.ChartEntries ?? [];
        }
    }

    /// <summary>導入済み所属への加入時に、取得済み列を確定列へ移します。パス変更後も再探索へ戻しません。</summary>
    internal void RetainMaterializedChartEntries()
    {
        lock (installEstimationSnapshotLock)
        {
            if (!hasExplicitChartFiles && packageChartDiscoverySnapshot != null)
            {
                chartEntries = packageChartDiscoverySnapshot.ChartEntries;
                hasExplicitChartFiles = true;
            }
        }
    }

    private static IEnumerable<PackageChartEntry> NormalizeChartEntries(IEnumerable<PackageChartEntry> entries)
    {
        foreach (PackageChartEntry entry in entries ?? [])
        {
            PackageChartEntry snapshot = entry;
            if (snapshot?.Chart == null)
            {
                continue;
            }
            yield return snapshot;
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

    internal PackageInstallEstimationSnapshot GetOrBuildInstallEstimationSnapshotFromEntries(IEnumerable<PackageChartEntry> targetEntries, ChartResourceSnapshot definedResources = null)
    {
        PackageInstallSurfaceSnapshot installSurfaceSnapshot = GetOrBuildInstallEstimationSurfaceSnapshot(out bool sourceSurfaceCacheHit);
        return PackageInstallEstimationSnapshotBuilder.Build(this, targetEntries, installSurfaceSnapshot, sourceSurfaceCacheHit, definedResources: definedResources);
    }

    internal PackageInstallEstimationSnapshot BuildInstallEstimationSnapshotFromEntries(IEnumerable<PackageChartEntry> targetEntries, PackageInstallSurfaceSnapshot installSurfaceSnapshot, bool sourceSurfaceCacheHit, bool sourceSurfaceBatchHit = false, ChartResourceSnapshot definedResources = null)
    {
        return PackageInstallEstimationSnapshotBuilder.Build(
            this,
            targetEntries,
            installSurfaceSnapshot,
            sourceSurfaceCacheHit,
            sourceSurfaceBatchHit,
            definedResources);
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
