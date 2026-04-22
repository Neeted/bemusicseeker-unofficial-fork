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
        SourceCandidateResources = new DirectoryResourceLookupCache.Entry()
    };

    public string SourcePath { get; set; } = string.Empty;

    public string SourceDirectory { get; set; } = string.Empty;

    public DirectoryResourceLookupCache.Entry BundledResources { get; set; } = new DirectoryResourceLookupCache.Entry();

    public DirectoryResourceLookupCache.Entry SourceCandidateResources { get; set; } = new DirectoryResourceLookupCache.Entry();
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

    public int BundledAudioCount => BundledResources?.AudioFileNameHashCount ?? 0;

    public int BundledImageCount => BundledResources?.ImageFileNameHashCount ?? 0;

    public int BundledMovieCount => BundledResources?.MovieFileNameHashCount ?? 0;
}

internal static class PackageInstallEstimationSnapshotBuilder
{
    internal static PackageInstallEstimationSnapshot Build(BMSPackage package, IEnumerable<BMSFile> targetFiles, PackageInstallSurfaceSnapshot installSurfaceSnapshot)
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
            ChartCount = targetFileList.Count
        };
    }

    internal static PackageInstallEstimationSnapshot BuildForLooseFiles(IEnumerable<BMSFile> targetFiles)
    {
        List<BMSFile> targetFileList = (targetFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        BMSFile representativeFile = SelectRepresentativeFile(targetFileList);
        return new PackageInstallEstimationSnapshot
        {
            RepresentativeFile = representativeFile,
            DefinedResources = ChartResourceSnapshot.CreateAggregate(targetFileList),
            TargetMetadataProfile = BuildTargetMetadataProfile(targetFileList),
            BundledResources = new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = BuildSourceCandidateResourcesForLooseFiles(representativeFile),
            SourceDirectory = representativeFile == null ? string.Empty : ResolveSourceDirectory(representativeFile.path),
            ChartCount = targetFileList.Count
        };
    }

    internal static PackageInstallSurfaceSnapshot BuildInstallSurface(string packagePath)
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
            DirectoryResourceLookupCache.Entry entry = BuildResourceEntryFromRoot(normalizedPath);
            return new PackageInstallSurfaceSnapshot
            {
                SourcePath = normalizedPath,
                SourceDirectory = normalizedPath,
                BundledResources = entry,
                SourceCandidateResources = entry.Clone()
            };
        }

        string sourceDirectory = ResolveSourceDirectory(normalizedPath);
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = normalizedPath,
            SourceDirectory = sourceDirectory,
            BundledResources = new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = BuildResourceEntryFromRoot(sourceDirectory)
        };
    }

    private static DirectoryResourceLookupCache.Entry BuildSourceCandidateResourcesForLooseFiles(BMSFile representativeFile)
    {
        string sourceDirectory = representativeFile == null ? string.Empty : ResolveSourceDirectory(representativeFile.path);
        return BuildResourceEntryFromRoot(sourceDirectory);
    }

    private static DirectoryResourceLookupCache.Entry BuildResourceEntryFromRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return new DirectoryResourceLookupCache.Entry();
        }

        HashSet<uint> allBaseNameHashes = new HashSet<uint>();
        HashSet<uint> audioBaseNameHashes = new HashSet<uint>();
        HashSet<uint> imageBaseNameHashes = new HashSet<uint>();
        HashSet<uint> movieBaseNameHashes = new HashSet<uint>();
        HashSet<uint> audioRelativePathHashes = new HashSet<uint>();
        HashSet<uint> imageRelativePathHashes = new HashSet<uint>();
        HashSet<uint> movieRelativePathHashes = new HashSet<uint>();

        IEnumerable<string> allFiles;
        try
        {
            allFiles = FastDirectoryEnumerator.GetFilePathsAsParallel(rootDirectory, null, null, SearchOption.AllDirectories)
                .Where((string path) => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new DirectoryResourceLookupCache.Entry();
        }

        foreach (string absolutePath in allFiles)
        {
            if (ChartDirectoryScanBuilder.IsChartFile(absolutePath))
            {
                continue;
            }

            ChartResourceKind resourceKind = GetResourceKind(absolutePath);
            if (resourceKind == ChartResourceKind.Unknown)
            {
                continue;
            }

            string baseName = ChartResourcePathNormalizer.NormalizeFileNameForLookup(Path.GetFileName(absolutePath));
            string relativePath = ChartResourcePathNormalizer.NormalizeRelativePathForLookup(rootDirectory, absolutePath);
            if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            uint baseNameHash = BMSDirectoryFileNameHash.GetLookupHash(baseName);
            uint relativePathHash = BMSDirectoryFileNameHash.GetLookupHash(relativePath);
            allBaseNameHashes.Add(baseNameHash);
            switch (resourceKind)
            {
                case ChartResourceKind.Audio:
                    audioBaseNameHashes.Add(baseNameHash);
                    audioRelativePathHashes.Add(relativePathHash);
                    break;
                case ChartResourceKind.Image:
                    imageBaseNameHashes.Add(baseNameHash);
                    imageRelativePathHashes.Add(relativePathHash);
                    break;
                case ChartResourceKind.Movie:
                    movieBaseNameHashes.Add(baseNameHash);
                    movieRelativePathHashes.Add(relativePathHash);
                    break;
            }
        }

        return new DirectoryResourceLookupCache.Entry(
            allBaseNameHashes.OrderBy((uint hash) => hash).ToArray(),
            audioBaseNameHashes.OrderBy((uint hash) => hash).ToArray(),
            imageBaseNameHashes.OrderBy((uint hash) => hash).ToArray(),
            movieBaseNameHashes.OrderBy((uint hash) => hash).ToArray(),
            audioRelativePathHashes.OrderBy((uint hash) => hash).ToArray(),
            imageRelativePathHashes.OrderBy((uint hash) => hash).ToArray(),
            movieRelativePathHashes.OrderBy((uint hash) => hash).ToArray());
    }

    private static ChartResourceKind GetResourceKind(string path)
    {
        if (ChartDirectoryScanBuilder.IsAudioFile(path))
        {
            return ChartResourceKind.Audio;
        }
        if (ChartDirectoryScanBuilder.IsImageFile(path))
        {
            return ChartResourceKind.Image;
        }
        if (ChartDirectoryScanBuilder.IsMovieFile(path))
        {
            return ChartResourceKind.Movie;
        }
        return ChartResourceKind.Unknown;
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
