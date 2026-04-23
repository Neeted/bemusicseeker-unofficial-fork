using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

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

    public int FileCount { get; set; }

    public long HashMaterializeMs { get; set; }

    public string ScanBackend { get; set; } = string.Empty;
}

internal sealed class PackageSourceScanSnapshot
{
    public string SourcePath { get; set; } = string.Empty;

    public List<BMSFile> BmsFiles { get; set; } = new List<BMSFile>();

    public PackageInstallSurfaceSnapshot InstallSurfaceSnapshot { get; set; } = PackageInstallSurfaceSnapshot.Empty;
}

internal sealed class PackageInstallEstimationSnapshot
{
    public BMSFile RepresentativeFile { get; set; }

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

    public int SourceSurfaceFileCount { get; set; }

    public long SourceSurfaceHashMaterializeMs { get; set; }

    public bool SourceSurfaceCacheHit { get; set; }

    public string SourceSurfaceScanBackend { get; set; } = string.Empty;
}

internal static class PackageInstallEstimationSnapshotBuilder
{
    internal static PackageInstallEstimationSnapshot Build(BMSPackage package, IEnumerable<BMSFile> targetFiles, PackageInstallSurfaceSnapshot installSurfaceSnapshot, bool sourceSurfaceCacheHit)
    {
        List<BMSFile> targetFileList = (targetFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        return new PackageInstallEstimationSnapshot
        {
            RepresentativeFile = SelectRepresentativeFile(targetFileList),
            DefinedResources = ChartResourceSnapshot.CreateAggregate(targetFileList),
            TargetMetadataProfile = BuildTargetMetadataProfile(targetFileList),
            BundledResources = installSurfaceSnapshot?.BundledResources?.Clone() ?? new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = installSurfaceSnapshot?.SourceCandidateResources?.Clone() ?? new DirectoryResourceLookupCache.Entry(),
            SourceDirectory = installSurfaceSnapshot?.SourceDirectory ?? ResolveSourceDirectory(package?.path),
            ChartCount = targetFileList.Count,
            SourceSurfaceScanMs = installSurfaceSnapshot?.ScanMs ?? 0L,
            SourceSurfaceFileCount = installSurfaceSnapshot?.FileCount ?? 0,
            SourceSurfaceHashMaterializeMs = installSurfaceSnapshot?.HashMaterializeMs ?? 0L,
            SourceSurfaceCacheHit = sourceSurfaceCacheHit,
            SourceSurfaceScanBackend = installSurfaceSnapshot?.ScanBackend ?? string.Empty
        };
    }

    internal static PackageInstallEstimationSnapshot BuildForLooseFiles(IEnumerable<BMSFile> targetFiles)
    {
        List<BMSFile> targetFileList = (targetFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        BMSFile representativeFile = SelectRepresentativeFile(targetFileList);
        PackageInstallSurfaceSnapshot sourceSurfaceSnapshot = BuildSourceCandidateResourcesForLooseFiles(representativeFile);
        return new PackageInstallEstimationSnapshot
        {
            RepresentativeFile = representativeFile,
            DefinedResources = ChartResourceSnapshot.CreateAggregate(targetFileList),
            TargetMetadataProfile = BuildTargetMetadataProfile(targetFileList),
            BundledResources = new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = sourceSurfaceSnapshot.SourceCandidateResources?.Clone() ?? new DirectoryResourceLookupCache.Entry(),
            SourceDirectory = sourceSurfaceSnapshot.SourceDirectory,
            ChartCount = targetFileList.Count,
            SourceSurfaceScanMs = sourceSurfaceSnapshot.ScanMs,
            SourceSurfaceFileCount = sourceSurfaceSnapshot.FileCount,
            SourceSurfaceHashMaterializeMs = sourceSurfaceSnapshot.HashMaterializeMs,
            SourceSurfaceCacheHit = false,
            SourceSurfaceScanBackend = sourceSurfaceSnapshot.ScanBackend
        };
    }

    internal static PackageSourceScanSnapshot BuildPackageSourceScanSnapshot(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return new PackageSourceScanSnapshot();
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(packagePath);
        }
        catch
        {
            return new PackageSourceScanSnapshot();
        }

        if (Directory.Exists(normalizedPath))
        {
            RootFileEnumerationResult enumerationResult = RootFileEnumerationService.EnumerateFilesWithFallback(
                new[] { normalizedPath },
                ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(includeAllFiles: true));
            PackageInstallSurfaceSnapshot installSurfaceSnapshot = CreateDirectoryInstallSurfaceSnapshot(normalizedPath, normalizedPath, enumerationResult);
            return new PackageSourceScanSnapshot
            {
                SourcePath = normalizedPath,
                BmsFiles = (enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ChartGroupName) ?? Array.Empty<string>())
                    .Select(CreatePendingChartFromPath)
                    .Where((BMSFile file) => file != null)
                    .ToList(),
                InstallSurfaceSnapshot = installSurfaceSnapshot
            };
        }

        string sourceDirectory = ResolveSourceDirectory(normalizedPath);
        RootFileEnumerationResult filePackageEnumerationResult = RootFileEnumerationService.EnumerateFilesWithFallback(
            new[] { sourceDirectory },
            ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(includeAllFiles: true));
        return new PackageSourceScanSnapshot
        {
            SourcePath = normalizedPath,
            BmsFiles = File.Exists(normalizedPath) && PendingChartEntry.IsSupportedChartFilePath(normalizedPath)
                ? new List<BMSFile> { CreatePendingChartFromPath(normalizedPath) }.Where((BMSFile file) => file != null).ToList()
                : new List<BMSFile>(),
            InstallSurfaceSnapshot = CreateFileInstallSurfaceSnapshot(normalizedPath, sourceDirectory, filePackageEnumerationResult)
        };
    }

    private static PackageInstallSurfaceSnapshot BuildSourceCandidateResourcesForLooseFiles(BMSFile representativeFile)
    {
        string sourceDirectory = representativeFile == null ? string.Empty : ResolveSourceDirectory(representativeFile.path);
        RootFileEnumerationResult enumerationResult = RootFileEnumerationService.EnumerateFilesWithFallback(
            new[] { sourceDirectory },
            ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(includeAllFiles: true));
        return CreateFileInstallSurfaceSnapshot(representativeFile?.path ?? string.Empty, sourceDirectory, enumerationResult);
    }

    private static PackageInstallSurfaceSnapshot CreateDirectoryInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, RootFileEnumerationResult enumerationResult)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        DirectoryResourceLookupCache.Entry entry = CreateResourceEntryFromEnumeration(sourceDirectory, enumerationResult);
        stopwatch.Stop();
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath,
            SourceDirectory = sourceDirectory,
            BundledResources = entry,
            SourceCandidateResources = entry.Clone(),
            ScanMs = enumerationResult?.EnumerationMs ?? 0L,
            FileCount = enumerationResult?.TotalFileCount ?? 0,
            HashMaterializeMs = stopwatch.ElapsedMilliseconds,
            ScanBackend = enumerationResult?.BackendName ?? "fast"
        };
    }

    private static PackageInstallSurfaceSnapshot CreateFileInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, RootFileEnumerationResult enumerationResult)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        DirectoryResourceLookupCache.Entry sourceCandidateResources = CreateResourceEntryFromEnumeration(sourceDirectory, enumerationResult);
        stopwatch.Stop();
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath,
            SourceDirectory = sourceDirectory,
            BundledResources = new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = sourceCandidateResources,
            ScanMs = enumerationResult?.EnumerationMs ?? 0L,
            FileCount = enumerationResult?.TotalFileCount ?? 0,
            HashMaterializeMs = stopwatch.ElapsedMilliseconds,
            ScanBackend = enumerationResult?.BackendName ?? "fast"
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
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.AudioGroupName) ?? Array.Empty<string>(),
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ImageGroupName) ?? Array.Empty<string>(),
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.MovieGroupName) ?? Array.Empty<string>());
    }

    private static BMSFile CreatePendingChartFromPath(string filePath)
    {
        try
        {
            return PendingChartEntry.CreateFromFilePath(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static BMSFile SelectRepresentativeFile(IReadOnlyCollection<BMSFile> targetFiles)
    {
        return targetFiles?
            .Where((BMSFile file) => file != null)
            .OrderByDescending((BMSFile file) => ChartResourceSnapshot.Create(file).TotalReferenceCount)
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

    private static InstallEstimationMetadataProfile BuildTargetMetadataProfile(IEnumerable<BMSFile> targetFiles)
    {
        return InstallEstimationMetadataNormalizer.BuildProfile(
            (targetFiles ?? Enumerable.Empty<BMSFile>())
                .Where((BMSFile file) => file != null)
                .Select((BMSFile file) => (file.Title ?? string.Empty, file.Artist ?? string.Empty, file.path ?? string.Empty)));
    }
}
