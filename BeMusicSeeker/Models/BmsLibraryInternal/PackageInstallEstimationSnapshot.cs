using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageInstallSurfaceSnapshot
{
    public static PackageInstallSurfaceSnapshot Empty { get; } = new PackageInstallSurfaceSnapshot
    {
        SourcePath = string.Empty,
        SourceDirectory = string.Empty,
        BundledResources = new DirectoryResourceLookupCache.Entry(),
        SourceCandidateResources = new DirectoryResourceLookupCache.Entry(),
        ScanBackend = string.Empty
    };

    public string SourcePath { get; set; } = string.Empty;

    public string SourceDirectory { get; set; } = string.Empty;

    public DirectoryResourceLookupCache.Entry BundledResources { get; set; } = new DirectoryResourceLookupCache.Entry();

    public DirectoryResourceLookupCache.Entry SourceCandidateResources { get; set; } = new DirectoryResourceLookupCache.Entry();

    public long ScanMs { get; set; }

    public int ChartFileCount { get; set; }

    public int ResourceFileCount { get; set; }

    public int TrackedFileCount { get; set; }

    public long HashMaterializeMs { get; set; }

    public string ScanBackend { get; set; } = string.Empty;
}

internal sealed class PackageChartDiscoverySnapshot
{
    private List<PackageChartEntry> chartEntries = [];

    public string SourcePath { get; set; } = string.Empty;

    public List<PackageChartEntry> ChartEntries
    {
        get
        {
            return [.. chartEntries.Where(entry => entry?.Chart != null)];
        }
        set
        {
            chartEntries = NormalizeChartEntries(value);
        }
    }

    internal void ReplaceCompatibilityAdapters(IEnumerable<BMSFile> adapters)
    {
        chartEntries = NormalizeChartEntries((adapters ?? []).Select(PackageChartEntry.FromCompatibilityAdapter));
    }

    internal void ReplaceChartEntries(IEnumerable<PackageChartEntry> entries)
    {
        chartEntries = NormalizeChartEntries(entries);
    }

    internal void RemoveChartEntriesByPath(ISet<string> pathsToRemove)
    {
        if (pathsToRemove == null || pathsToRemove.Count == 0)
        {
            return;
        }
        chartEntries.RemoveAll(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.Path) && pathsToRemove.Contains(entry.Chart.Path));
    }

    private static List<PackageChartEntry> NormalizeChartEntries(IEnumerable<PackageChartEntry> entries)
    {
        var normalizedEntries = new List<PackageChartEntry>();
        foreach (PackageChartEntry entry in entries ?? [])
        {
            if (entry?.Chart == null)
            {
                continue;
            }
            normalizedEntries.Add(entry.CompatibilityAdapter != null
                ? PackageChartEntry.FromCompatibilityAdapter(entry.CompatibilityAdapter)
                : PackageChartEntry.FromChart(entry.Chart));
        }
        return normalizedEntries;
    }

}

internal sealed class PackageInstallEstimationSnapshot
{
    public ChartFile RepresentativeChart { get; set; }

    public ChartResourceSnapshot DefinedResources { get; set; } = new ChartResourceSnapshot();

    public InstallEstimationMetadataProfile TargetMetadataProfile { get; set; } = InstallEstimationMetadataProfile.Empty;

    public DirectoryResourceLookupCache.Entry BundledResources { get; set; } = new DirectoryResourceLookupCache.Entry();

    public DirectoryResourceLookupCache.Entry SourceCandidateResources { get; set; } = new DirectoryResourceLookupCache.Entry();

    public string SourceDirectory { get; set; } = string.Empty;

    public int ChartCount { get; set; }

    public int BundledAudioCount => BundledResources?.AudioRelativePathHashArray?.Length ?? 0;

    public int BundledImageCount => BundledResources?.ImageRelativePathHashArray?.Length ?? 0;

    public int BundledMovieCount => BundledResources?.MovieRelativePathHashArray?.Length ?? 0;

    public long SourceSurfaceScanMs { get; set; }

    public int SourceSurfaceChartFileCount { get; set; }

    public int SourceSurfaceResourceFileCount { get; set; }

    public int SourceSurfaceTrackedFileCount { get; set; }

    public long SourceSurfaceHashMaterializeMs { get; set; }

    public bool SourceSurfaceCacheHit { get; set; }

    public bool SourceSurfaceBatchHit { get; set; }

    public string SourceSurfaceScanBackend { get; set; } = string.Empty;
}

internal static class PackageInstallEstimationSnapshotBuilder
{
    internal static PackageInstallEstimationSnapshot Build(ChartPackage package, IEnumerable<PackageChartEntry> targetEntries, PackageInstallSurfaceSnapshot installSurfaceSnapshot, bool sourceSurfaceCacheHit, bool sourceSurfaceBatchHit = false)
    {
        List<PackageChartEntry> normalizedTargetEntries = NormalizeEntries(targetEntries);
        PackageChartEntry representativeEntry = SelectRepresentativeEntry(normalizedTargetEntries);
        return new PackageInstallEstimationSnapshot
        {
            RepresentativeChart = representativeEntry?.Chart,
            DefinedResources = ChartResourceSnapshot.CreateAggregate(normalizedTargetEntries.Select(entry => entry.Chart)),
            TargetMetadataProfile = BuildTargetMetadataProfile(normalizedTargetEntries),
            BundledResources = installSurfaceSnapshot?.BundledResources?.Clone() ?? new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = installSurfaceSnapshot?.SourceCandidateResources?.Clone() ?? new DirectoryResourceLookupCache.Entry(),
            SourceDirectory = installSurfaceSnapshot?.SourceDirectory ?? ResolveSourceDirectory(package?.path),
            ChartCount = normalizedTargetEntries.Count,
            SourceSurfaceScanMs = installSurfaceSnapshot?.ScanMs ?? 0L,
            SourceSurfaceChartFileCount = installSurfaceSnapshot?.ChartFileCount ?? 0,
            SourceSurfaceResourceFileCount = installSurfaceSnapshot?.ResourceFileCount ?? 0,
            SourceSurfaceTrackedFileCount = installSurfaceSnapshot?.TrackedFileCount ?? 0,
            SourceSurfaceHashMaterializeMs = installSurfaceSnapshot?.HashMaterializeMs ?? 0L,
            SourceSurfaceCacheHit = sourceSurfaceCacheHit,
            SourceSurfaceBatchHit = sourceSurfaceBatchHit,
            SourceSurfaceScanBackend = installSurfaceSnapshot?.ScanBackend ?? string.Empty
        };
    }

    internal static PackageInstallEstimationSnapshot BuildForLooseEntries(IEnumerable<PackageChartEntry> targetEntries)
    {
        List<PackageChartEntry> normalizedTargetEntries = NormalizeEntries(targetEntries);
        PackageChartEntry representativeEntry = SelectRepresentativeEntry(normalizedTargetEntries);
        PackageInstallSurfaceSnapshot sourceSurfaceSnapshot = BuildSourceCandidateResourcesForLooseFiles(representativeEntry?.Chart);
        return new PackageInstallEstimationSnapshot
        {
            RepresentativeChart = representativeEntry?.Chart,
            DefinedResources = ChartResourceSnapshot.CreateAggregate(normalizedTargetEntries.Select(entry => entry.Chart)),
            TargetMetadataProfile = BuildTargetMetadataProfile(normalizedTargetEntries),
            BundledResources = new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = sourceSurfaceSnapshot.SourceCandidateResources?.Clone() ?? new DirectoryResourceLookupCache.Entry(),
            SourceDirectory = sourceSurfaceSnapshot.SourceDirectory,
            ChartCount = normalizedTargetEntries.Count,
            SourceSurfaceScanMs = sourceSurfaceSnapshot.ScanMs,
            SourceSurfaceChartFileCount = sourceSurfaceSnapshot.ChartFileCount,
            SourceSurfaceResourceFileCount = sourceSurfaceSnapshot.ResourceFileCount,
            SourceSurfaceTrackedFileCount = sourceSurfaceSnapshot.TrackedFileCount,
            SourceSurfaceHashMaterializeMs = sourceSurfaceSnapshot.HashMaterializeMs,
            SourceSurfaceCacheHit = false,
            SourceSurfaceBatchHit = false,
            SourceSurfaceScanBackend = sourceSurfaceSnapshot.ScanBackend
        };
    }

    internal static PackageInstallSurfaceSnapshot BuildSharedInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, SourceSurfaceEntryView sourceSurface, bool includeBundledResources)
    {
        DirectoryResourceLookupCache.Entry resourceEntry = sourceSurface?.ResourceEntry?.Clone() ?? new DirectoryResourceLookupCache.Entry();
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath ?? string.Empty,
            SourceDirectory = sourceDirectory ?? string.Empty,
            BundledResources = includeBundledResources ? resourceEntry.Clone() : new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = resourceEntry,
            ScanMs = 0L,
            ChartFileCount = sourceSurface?.ChartFileCount ?? 0,
            ResourceFileCount = sourceSurface?.ResourceFileCount ?? 0,
            TrackedFileCount = sourceSurface?.TrackedFileCount ?? 0,
            HashMaterializeMs = 0L,
            ScanBackend = sourceSurface?.ScanBackend ?? string.Empty
        };
    }

    internal static PackageChartDiscoverySnapshot BuildPackageChartDiscoverySnapshot(string packagePath)
    {
        return BuildPackageChartDiscoverySnapshot(packagePath, Settings.Default.UseEverythingForPendingPackageSourceScan);
    }

    internal static PackageChartDiscoverySnapshot BuildPackageChartDiscoverySnapshot(string packagePath, bool useEverythingForPendingPackageSourceScan)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return new PackageChartDiscoverySnapshot();
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(packagePath);
        }
        catch
        {
            return new PackageChartDiscoverySnapshot();
        }

        if (Directory.Exists(normalizedPath))
        {
            RootFileEnumerationResult enumerationResult = useEverythingForPendingPackageSourceScan
                ? EnumerateChartsOnlyWithFallback(normalizedPath)
                : EnumerateChartsOnlyFastOnly(normalizedPath);
            return new PackageChartDiscoverySnapshot
            {
                SourcePath = normalizedPath,
                ChartEntries = CreatePendingChartEntriesFromPaths(enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ChartGroupName) ?? [])
            };
        }

        return new PackageChartDiscoverySnapshot
        {
            SourcePath = normalizedPath,
            ChartEntries = File.Exists(normalizedPath) && PendingChartEntry.IsSupportedChartFilePath(normalizedPath)
                ? [.. new List<PackageChartEntry> { PackageChartEntry.FromPath(normalizedPath) }.Where(entry => entry != null)]
                : []
        };
    }

    internal static PackageInstallSurfaceSnapshot BuildPackageInstallSurfaceSnapshot(string packagePath)
    {
        return BuildPackageInstallSurfaceSnapshot(packagePath, Settings.Default.UseEverythingForPendingPackageSourceScan);
    }

    internal static PackageInstallSurfaceSnapshot BuildPackageInstallSurfaceSnapshot(string packagePath, bool useEverythingForPendingPackageSourceScan)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return PackageInstallSurfaceSnapshot.Empty;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(packagePath);
        }
        catch
        {
            return PackageInstallSurfaceSnapshot.Empty;
        }

        if (Directory.Exists(normalizedPath))
        {
            return CreateDirectoryInstallSurfaceSnapshot(normalizedPath, normalizedPath, useEverythingForPendingPackageSourceScan);
        }

        string sourceDirectory = ResolveSourceDirectory(normalizedPath);
        return CreateFileInstallSurfaceSnapshot(normalizedPath, sourceDirectory, useEverythingForPendingPackageSourceScan);
    }

    private static PackageInstallSurfaceSnapshot BuildSourceCandidateResourcesForLooseFiles(ChartFile representativeChart)
    {
        string sourcePath = representativeChart?.Path ?? string.Empty;
        string sourceDirectory = ResolveSourceDirectory(sourcePath);
        return CreateFileInstallSurfaceSnapshot(sourcePath, sourceDirectory, Settings.Default.UseEverythingForPendingPackageSourceScan);
    }

    private static PackageInstallSurfaceSnapshot CreateDirectoryInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, bool useEverythingForPendingPackageSourceScan)
    {
        PackageInstallSurfaceSnapshot snapshot = useEverythingForPendingPackageSourceScan
            ? TryCreateSourceRootInstallSurfaceSnapshot(sourcePath, sourceDirectory, includeBundledResources: true)
            : null;
        if (snapshot != null)
        {
            return snapshot;
        }

        RootFileEnumerationResult enumerationResult = useEverythingForPendingPackageSourceScan
            ? EnumerateSourceSurfaceWithFallback(sourceDirectory)
            : EnumerateSourceSurfaceFastOnly(sourceDirectory);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        DirectoryResourceLookupCache.Entry entry = CreateResourceEntryFromEnumeration(sourceDirectory, enumerationResult);
        stopwatch.Stop();
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath,
            SourceDirectory = sourceDirectory,
            BundledResources = entry,
            SourceCandidateResources = entry.Clone(),
            ScanMs = enumerationResult?.EnumerationMs ?? 0L,
            ChartFileCount = (enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ChartGroupName) ?? []).Count,
            ResourceFileCount = CountDistinctPaths(
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.AudioGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ImageGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.MovieGroupName)),
            TrackedFileCount = CountDistinctPaths(
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ChartGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.AudioGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ImageGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.MovieGroupName)),
            HashMaterializeMs = stopwatch.ElapsedMilliseconds,
            ScanBackend = enumerationResult?.BackendName ?? "fast"
        };
    }

    private static PackageInstallSurfaceSnapshot CreateFileInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, bool useEverythingForPendingPackageSourceScan)
    {
        PackageInstallSurfaceSnapshot snapshot = useEverythingForPendingPackageSourceScan
            ? TryCreateSourceRootInstallSurfaceSnapshot(sourcePath, sourceDirectory, includeBundledResources: false)
            : null;
        if (snapshot != null)
        {
            return snapshot;
        }

        RootFileEnumerationResult enumerationResult = useEverythingForPendingPackageSourceScan
            ? EnumerateSourceSurfaceWithFallback(sourceDirectory)
            : EnumerateSourceSurfaceFastOnly(sourceDirectory);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        DirectoryResourceLookupCache.Entry sourceCandidateResources = CreateResourceEntryFromEnumeration(sourceDirectory, enumerationResult);
        stopwatch.Stop();
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath,
            SourceDirectory = sourceDirectory,
            BundledResources = new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = sourceCandidateResources,
            ScanMs = enumerationResult?.EnumerationMs ?? 0L,
            ChartFileCount = (enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ChartGroupName) ?? []).Count,
            ResourceFileCount = CountDistinctPaths(
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.AudioGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ImageGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.MovieGroupName)),
            TrackedFileCount = CountDistinctPaths(
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ChartGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.AudioGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ImageGroupName),
                enumerationResult?.GetPaths(ChartDirectoryScanBuilder.MovieGroupName)),
            HashMaterializeMs = stopwatch.ElapsedMilliseconds,
            ScanBackend = enumerationResult?.BackendName ?? "fast"
        };
    }

    private static PackageInstallSurfaceSnapshot TryCreateSourceRootInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, bool includeBundledResources)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return null;
        }

        if (!EverythingNative.TryScanSourceRoots([sourceDirectory], out EverythingNative.BridgeSourceRootScanResult scanResult, out _)
            || scanResult == null
            || !scanResult.TryGetEntry(sourceDirectory, out EverythingNative.BridgeSourceRootEntryResult entryResult)
            || entryResult == null
            || entryResult.TrackedFileCount <= 0)
        {
            return null;
        }

        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath,
            SourceDirectory = sourceDirectory,
            BundledResources = includeBundledResources ? entryResult.ResourceEntry.Clone() : new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = entryResult.ResourceEntry.Clone(),
            ScanMs = scanResult.TotalMs,
            ChartFileCount = entryResult.ChartFileCount,
            ResourceFileCount = entryResult.ResourceFileCount,
            TrackedFileCount = entryResult.TrackedFileCount,
            HashMaterializeMs = scanResult.ManagedMaterializeMs,
            ScanBackend = scanResult.BackendName
        };
    }

    private static DirectoryResourceLookupCache.Entry CreateResourceEntryFromEnumeration(string rootDirectory, RootFileEnumerationResult enumerationResult)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return new DirectoryResourceLookupCache.Entry();
        }

        return ResourceSurfaceMaterializer.CreateSingleRootEntry(
            rootDirectory,
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.AudioGroupName) ?? [],
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ImageGroupName) ?? [],
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.MovieGroupName) ?? []);
    }

    private static RootFileEnumerationResult EnumerateChartsOnlyWithFallback(string rootDirectory)
    {
        return RootFileEnumerationService.EnumerateFilesWithFallback(
            [rootDirectory],
            [
                new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, ChartDirectoryScanBuilder.ChartExtensions)
            ]);
    }

    private static RootFileEnumerationResult EnumerateChartsOnlyFastOnly(string rootDirectory)
    {
        return new FastRootFileEnumerator().EnumerateFiles(
            [rootDirectory],
            [
                new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, ChartDirectoryScanBuilder.ChartExtensions)
            ]);
    }

    private static RootFileEnumerationResult EnumerateSourceSurfaceWithFallback(string rootDirectory)
    {
        return RootFileEnumerationService.EnumerateFilesWithFallback(
            [rootDirectory],
            ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(includeAllFiles: false));
    }

    private static RootFileEnumerationResult EnumerateSourceSurfaceFastOnly(string rootDirectory)
    {
        return new FastRootFileEnumerator().EnumerateFiles(
            [rootDirectory],
            ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(includeAllFiles: false));
    }

    private static List<PackageChartEntry> CreatePendingChartEntriesFromPaths(IEnumerable<string> chartPaths)
    {
        return [.. (chartPaths ?? [])
            .Select(PackageChartEntry.FromPath)
            .Where(entry => entry != null)];
    }

    private static int CountDistinctPaths(params IReadOnlyCollection<string>[] groups)
    {
        return (groups ?? [])
            .Where(group => group != null)
            .SelectMany(group => group)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static List<PackageChartEntry> NormalizeEntries(IEnumerable<PackageChartEntry> targetEntries)
    {
        return [.. (targetEntries ?? []).Where(entry => entry?.Chart != null)];
    }

    private static PackageChartEntry SelectRepresentativeEntry(IReadOnlyCollection<PackageChartEntry> targetEntries)
    {
        return targetEntries?
            .Where(entry => entry?.Chart != null)
            .OrderByDescending(entry => entry.ResourceSnapshot.TotalReferenceCount)
            .FirstOrDefault();
    }

    private static string ResolveSourceDirectory(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return string.Empty;
        }

        try
        {
            string normalizedPath = Path.GetFullPath(packagePath);
            if (Directory.Exists(normalizedPath))
            {
                return normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return Path.GetDirectoryName(normalizedPath) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static InstallEstimationMetadataProfile BuildTargetMetadataProfile(IEnumerable<PackageChartEntry> targetEntries)
    {
        return InstallEstimationMetadataNormalizer.BuildProfile(
            (targetEntries ?? [])
                .Select(entry => entry?.Chart)
                .Where(chart => chart != null)
                .Select(chart => (chart.Title ?? string.Empty, chart.Artist ?? string.Empty, chart.Path ?? string.Empty)));
    }
}
