using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartResourceSnapshot
{
    internal readonly struct ResourceReference
    {
        public ResourceReference(string normalizedPath, uint baseNameHash, uint relativePathHash, bool isPathAware)
        {
            NormalizedPath = normalizedPath ?? string.Empty;
            BaseNameHash = baseNameHash;
            RelativePathHash = relativePathHash;
            IsPathAware = isPathAware;
        }

        public string NormalizedPath { get; }

        public uint BaseNameHash { get; }

        public uint RelativePathHash { get; }

        public bool IsPathAware { get; }
    }

    private readonly List<ResourceReference> audioReferences = new List<ResourceReference>();

    private readonly List<ResourceReference> visualReferences = new List<ResourceReference>();

    private readonly List<ResourceReference> movieReferences = new List<ResourceReference>();

    private readonly List<ResourceReference> optionalImageReferences = new List<ResourceReference>();

    public HashSet<string> AudioRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<string> AudioBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> AudioPathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioPathAwareRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<uint> AudioBasenameOnlyBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> VisualRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<string> VisualBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> VisualPathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualPathAwareRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<uint> VisualBasenameOnlyBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> MovieRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MovieRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<string> MovieBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MovieBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> MoviePathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MoviePathAwareRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<uint> MovieBasenameOnlyBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> OptionalImageRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImageRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<string> OptionalImageBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImageBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> OptionalImagePathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImagePathAwareRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<uint> OptionalImageBasenameOnlyBaseNameHashes { get; } = new HashSet<uint>();

    public int AudioReferenceCount => AudioRelativePaths.Count;

    public int VisualReferenceCount => VisualRelativePaths.Count;

    public int MovieReferenceCount => MovieRelativePaths.Count;

    public int OptionalImageReferenceCount => OptionalImageRelativePaths.Count;

    public int TotalReferenceCount => AudioReferenceCount + VisualReferenceCount + MovieReferenceCount + OptionalImageReferenceCount;

    public int PathSegmentReferenceCount { get; private set; }

    public int AudioPathAwareReferenceCount => AudioPathAwareRelativePaths.Count;

    public int VisualPathAwareReferenceCount => VisualPathAwareRelativePaths.Count;

    public int MoviePathAwareReferenceCount => MoviePathAwareRelativePaths.Count;

    public int OptionalImagePathAwareReferenceCount => OptionalImagePathAwareRelativePaths.Count;

    public IReadOnlyList<ResourceReference> AudioReferences => audioReferences;

    public IReadOnlyList<ResourceReference> VisualReferences => visualReferences;

    public IReadOnlyList<ResourceReference> MovieReferences => movieReferences;

    public IReadOnlyList<ResourceReference> OptionalImageReferences => optionalImageReferences;

    public IEnumerable<string> EnumerateAllBaseNames()
    {
        return AudioBaseNames
            .Concat(VisualBaseNames)
            .Concat(MovieBaseNames)
            .Concat(OptionalImageBaseNames)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<uint> EnumerateAllBaseNameHashes()
    {
        return new HashSet<uint>(
            AudioBaseNameHashes
                .Concat(VisualBaseNameHashes)
                .Concat(MovieBaseNameHashes)
                .Concat(OptionalImageBaseNameHashes));
    }

    public HashSet<uint> EnumerateBroadFilterBaseNameHashes()
    {
        return new HashSet<uint>(
            AudioBasenameOnlyBaseNameHashes
                .Concat(VisualBasenameOnlyBaseNameHashes)
                .Concat(MovieBasenameOnlyBaseNameHashes)
                .Concat(OptionalImageBasenameOnlyBaseNameHashes));
    }

    public IEnumerable<string> EnumerateAllRelativePaths()
    {
        return AudioRelativePaths
            .Concat(VisualRelativePaths)
            .Concat(MovieRelativePaths)
            .Concat(OptionalImageRelativePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static ChartResourceSnapshot Create(BMSFile file)
    {
        if (file == null)
        {
            throw new ArgumentNullException(nameof(file));
        }
        EnsureComponentCollectionsLoaded(file);
        ChartResourceSnapshot snapshot = new ChartResourceSnapshot();
        foreach (string audioPath in file.WAVfiles ?? Enumerable.Empty<string>())
        {
            snapshot.AddReference(ChartResourceKind.Audio, audioPath);
        }
        foreach (string visualPath in file.BGAfiles ?? Enumerable.Empty<string>())
        {
            snapshot.AddReference(ChartResourcePathNormalizer.ClassifyPath(visualPath), visualPath);
        }
        snapshot.AddOptionalImage(file.banner);
        snapshot.AddOptionalImage(file.backbmp);
        snapshot.AddOptionalImage(file.stagefile);
        snapshot.PathSegmentReferenceCount = snapshot.EnumerateAllRelativePaths().Count(ChartResourcePathNormalizer.HasDirectorySegments);
        return snapshot;
    }

    public static ChartResourceSnapshot CreateAggregate(IEnumerable<BMSFile> files)
    {
        ChartResourceSnapshot aggregate = new ChartResourceSnapshot();
        foreach (BMSFile file in (files ?? Enumerable.Empty<BMSFile>()).Where((BMSFile item) => item != null))
        {
            aggregate.Merge(Create(file));
        }
        aggregate.PathSegmentReferenceCount = aggregate.EnumerateAllRelativePaths().Count(ChartResourcePathNormalizer.HasDirectorySegments);
        return aggregate;
    }

    private void Merge(ChartResourceSnapshot other)
    {
        if (other == null)
        {
            return;
        }
        foreach (ResourceReference reference in other.AudioReferences)
        {
            AddNormalized(AudioRelativePaths, AudioRelativePathHashes, AudioBaseNames, AudioBaseNameHashes, AudioPathAwareRelativePaths, AudioPathAwareRelativePathHashes, AudioBasenameOnlyBaseNameHashes, audioReferences, reference.NormalizedPath);
        }
        foreach (ResourceReference reference in other.VisualReferences)
        {
            AddNormalized(VisualRelativePaths, VisualRelativePathHashes, VisualBaseNames, VisualBaseNameHashes, VisualPathAwareRelativePaths, VisualPathAwareRelativePathHashes, VisualBasenameOnlyBaseNameHashes, visualReferences, reference.NormalizedPath);
        }
        foreach (ResourceReference reference in other.MovieReferences)
        {
            AddNormalized(MovieRelativePaths, MovieRelativePathHashes, MovieBaseNames, MovieBaseNameHashes, MoviePathAwareRelativePaths, MoviePathAwareRelativePathHashes, MovieBasenameOnlyBaseNameHashes, movieReferences, reference.NormalizedPath);
        }
        foreach (ResourceReference reference in other.OptionalImageReferences)
        {
            AddNormalized(OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImageBaseNames, OptionalImageBaseNameHashes, OptionalImagePathAwareRelativePaths, OptionalImagePathAwareRelativePathHashes, OptionalImageBasenameOnlyBaseNameHashes, optionalImageReferences, reference.NormalizedPath);
        }
    }

    private static void EnsureComponentCollectionsLoaded(BMSFile file)
    {
        if (file.WAVfiles != null && file.BGAfiles != null)
        {
            return;
        }
        if (PendingChartEntry.IsBmsonChartFile(file))
        {
            return;
        }
        BMSFile.SetBMSComponentFilesFromBMSFile(file);
    }

    private void AddOptionalImage(string path)
    {
        AddNormalized(OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImageBaseNames, OptionalImageBaseNameHashes, OptionalImagePathAwareRelativePaths, OptionalImagePathAwareRelativePathHashes, OptionalImageBasenameOnlyBaseNameHashes, optionalImageReferences, path);
    }

    private void AddReference(ChartResourceKind kind, string path)
    {
        switch (kind)
        {
            case ChartResourceKind.Audio:
                AddNormalized(AudioRelativePaths, AudioRelativePathHashes, AudioBaseNames, AudioBaseNameHashes, AudioPathAwareRelativePaths, AudioPathAwareRelativePathHashes, AudioBasenameOnlyBaseNameHashes, audioReferences, path);
                break;
            case ChartResourceKind.Image:
                AddNormalized(VisualRelativePaths, VisualRelativePathHashes, VisualBaseNames, VisualBaseNameHashes, VisualPathAwareRelativePaths, VisualPathAwareRelativePathHashes, VisualBasenameOnlyBaseNameHashes, visualReferences, path);
                break;
            case ChartResourceKind.Movie:
                AddNormalized(MovieRelativePaths, MovieRelativePathHashes, MovieBaseNames, MovieBaseNameHashes, MoviePathAwareRelativePaths, MoviePathAwareRelativePathHashes, MovieBasenameOnlyBaseNameHashes, movieReferences, path);
                break;
        }
    }

    private static void AddNormalized(ISet<string> relativePaths, ISet<uint> relativePathHashes, ISet<string> baseNames, ISet<uint> baseNameHashes, ISet<string> pathAwareRelativePaths, ISet<uint> pathAwareRelativePathHashes, ISet<uint> basenameOnlyBaseNameHashes, ICollection<ResourceReference> references, string path)
    {
        string normalizedPath = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(path);
        string baseName = ChartResourcePathNormalizer.GetLookupFileName(path);
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(baseName))
        {
            return;
        }
        if (!relativePaths.Add(normalizedPath))
        {
            return;
        }
        uint relativePathHash = BMSDirectoryFileNameHash.GetLookupHash(normalizedPath);
        relativePathHashes.Add(relativePathHash);
        baseNames.Add(baseName);
        uint baseNameHash = BMSDirectoryFileNameHash.GetLookupHash(baseName);
        baseNameHashes.Add(baseNameHash);
        bool isPathAware = ChartResourcePathNormalizer.HasDirectorySegments(normalizedPath);
        if (isPathAware)
        {
            pathAwareRelativePaths.Add(normalizedPath);
            pathAwareRelativePathHashes.Add(relativePathHash);
        }
        else
        {
            basenameOnlyBaseNameHashes.Add(baseNameHash);
        }
        references?.Add(new ResourceReference(normalizedPath, baseNameHash, relativePathHash, isPathAware));
    }
}
