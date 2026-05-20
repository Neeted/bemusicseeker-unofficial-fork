using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartResourceSnapshot
{
    internal readonly struct ResourceReference(string normalizedPath, uint relativePathHash, bool isPathAware)
    {
        public string NormalizedPath { get; } = normalizedPath ?? string.Empty;

        public uint RelativePathHash { get; } = relativePathHash;

        public bool IsPathAware { get; } = isPathAware;
    }

    private readonly List<ResourceReference> audioReferences = [];

    private readonly List<ResourceReference> visualReferences = [];

    private readonly List<ResourceReference> movieReferences = [];

    private readonly List<ResourceReference> optionalImageReferences = [];

    public HashSet<string> AudioRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioRelativePathHashes { get; } = [];

    public HashSet<string> AudioPathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioPathAwareRelativePathHashes { get; } = [];

    public HashSet<string> VisualRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualRelativePathHashes { get; } = [];

    public HashSet<string> VisualPathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualPathAwareRelativePathHashes { get; } = [];

    public HashSet<string> MovieRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MovieRelativePathHashes { get; } = [];

    public HashSet<string> MoviePathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MoviePathAwareRelativePathHashes { get; } = [];

    public HashSet<string> OptionalImageRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImageRelativePathHashes { get; } = [];

    public HashSet<string> OptionalImagePathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImagePathAwareRelativePathHashes { get; } = [];

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

    public HashSet<uint> EnumerateAllRelativePathHashes()
    {
        return [.. AudioRelativePathHashes
                .Concat(VisualRelativePathHashes)
                .Concat(MovieRelativePathHashes)
                .Concat(OptionalImageRelativePathHashes)];
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
        var snapshot = new ChartResourceSnapshot();
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

    public static ChartResourceSnapshot Create(ChartFile chart)
    {
        if (chart == null)
        {
            throw new ArgumentNullException(nameof(chart));
        }
        if (chart.Kind == ChartFileKind.Bmson && chart.BmsonSong != null)
        {
            return Create(chart.BmsonSong);
        }
        if (chart.BmsFile != null)
        {
            return Create(chart.BmsFile);
        }
        return new ChartResourceSnapshot();
    }

    public static ChartResourceSnapshot Create(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            throw new ArgumentNullException(nameof(song));
        }
        var snapshot = new ChartResourceSnapshot();
        foreach (string audioPath in song.wav_files ?? Enumerable.Empty<string>())
        {
            snapshot.AddReference(ChartResourceKind.Audio, audioPath);
        }
        foreach (string visualPath in song.bga_files ?? Enumerable.Empty<string>())
        {
            snapshot.AddReference(ChartResourcePathNormalizer.ClassifyPath(visualPath), visualPath);
        }
        snapshot.AddOptionalImage(song.banner);
        snapshot.AddOptionalImage(song.backbmp);
        snapshot.AddOptionalImage(song.stagefile);
        snapshot.PathSegmentReferenceCount = snapshot.EnumerateAllRelativePaths().Count(ChartResourcePathNormalizer.HasDirectorySegments);
        return snapshot;
    }

    public static ChartResourceSnapshot CreateAggregate(IEnumerable<BMSFile> files)
    {
        var aggregate = new ChartResourceSnapshot();
        foreach (BMSFile file in (files ?? []).Where(item => item != null))
        {
            aggregate.Merge(Create(file));
        }
        aggregate.PathSegmentReferenceCount = aggregate.EnumerateAllRelativePaths().Count(ChartResourcePathNormalizer.HasDirectorySegments);
        return aggregate;
    }

    public static ChartResourceSnapshot CreateAggregate(IEnumerable<ChartFile> charts)
    {
        var aggregate = new ChartResourceSnapshot();
        foreach (ChartFile chart in (charts ?? []).Where(item => item != null))
        {
            aggregate.Merge(Create(chart));
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
            AddNormalized(AudioRelativePaths, AudioRelativePathHashes, AudioPathAwareRelativePaths, AudioPathAwareRelativePathHashes, audioReferences, reference.NormalizedPath);
        }
        foreach (ResourceReference reference in other.VisualReferences)
        {
            AddNormalized(VisualRelativePaths, VisualRelativePathHashes, VisualPathAwareRelativePaths, VisualPathAwareRelativePathHashes, visualReferences, reference.NormalizedPath);
        }
        foreach (ResourceReference reference in other.MovieReferences)
        {
            AddNormalized(MovieRelativePaths, MovieRelativePathHashes, MoviePathAwareRelativePaths, MoviePathAwareRelativePathHashes, movieReferences, reference.NormalizedPath);
        }
        foreach (ResourceReference reference in other.OptionalImageReferences)
        {
            AddNormalized(OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImagePathAwareRelativePaths, OptionalImagePathAwareRelativePathHashes, optionalImageReferences, reference.NormalizedPath);
        }
    }

    private static void EnsureComponentCollectionsLoaded(BMSFile file)
    {
        if (file.WAVfiles != null && file.BGAfiles != null)
        {
            return;
        }
        BMSFile.SetBMSComponentFilesFromBMSFile(file);
    }

    private void AddOptionalImage(string path)
    {
        AddNormalized(OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImagePathAwareRelativePaths, OptionalImagePathAwareRelativePathHashes, optionalImageReferences, path);
    }

    private void AddReference(ChartResourceKind kind, string path)
    {
        switch (kind)
        {
            case ChartResourceKind.Audio:
                AddNormalized(AudioRelativePaths, AudioRelativePathHashes, AudioPathAwareRelativePaths, AudioPathAwareRelativePathHashes, audioReferences, path);
                break;
            case ChartResourceKind.Image:
                AddNormalized(VisualRelativePaths, VisualRelativePathHashes, VisualPathAwareRelativePaths, VisualPathAwareRelativePathHashes, visualReferences, path);
                break;
            case ChartResourceKind.Movie:
                AddNormalized(MovieRelativePaths, MovieRelativePathHashes, MoviePathAwareRelativePaths, MoviePathAwareRelativePathHashes, movieReferences, path);
                break;
        }
    }

    private static void AddNormalized(ISet<string> relativePaths, ISet<uint> relativePathHashes, ISet<string> pathAwareRelativePaths, ISet<uint> pathAwareRelativePathHashes, ICollection<ResourceReference> references, string path)
    {
        string normalizedPath = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }
        if (!relativePaths.Add(normalizedPath))
        {
            return;
        }
        uint relativePathHash = ChartResourceKeyHash.GetLookupHash(normalizedPath);
        relativePathHashes.Add(relativePathHash);
        bool isPathAware = ChartResourcePathNormalizer.HasDirectorySegments(normalizedPath);
        if (isPathAware)
        {
            pathAwareRelativePaths.Add(normalizedPath);
            pathAwareRelativePathHashes.Add(relativePathHash);
        }
        references?.Add(new ResourceReference(normalizedPath, relativePathHash, isPathAware));
    }
}
