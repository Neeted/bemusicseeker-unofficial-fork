using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartResourceSnapshot
{
    internal readonly struct ResourceReference(string normalizedPath, uint relativePathHash, bool isPathAware, string rawPath = null)
    {
        public string NormalizedPath { get; } = normalizedPath ?? string.Empty;

        public uint RelativePathHash { get; } = relativePathHash;

        public bool IsPathAware { get; } = isPathAware;

        public string RawPath { get; } = rawPath ?? string.Empty;
    }

    private readonly List<ResourceReference> audioReferences = [];

    private readonly List<ResourceReference> visualReferences = [];

    private readonly List<ResourceReference> movieReferences = [];

    private readonly List<ResourceReference> optionalImageReferences = [];

    private readonly List<UnsupportedChartResourceReference> unsupportedResourceReferences = [];

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

    public int UnsupportedResourceReferenceCount => unsupportedResourceReferences.Count;

    public bool HasUnsupportedParentTraversalReference => unsupportedResourceReferences.Any(reference => reference.Reason == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported);

    public int PathSegmentReferenceCount { get; private set; }

    public int AudioPathAwareReferenceCount => AudioPathAwareRelativePaths.Count;

    public int VisualPathAwareReferenceCount => VisualPathAwareRelativePaths.Count;

    public int MoviePathAwareReferenceCount => MoviePathAwareRelativePaths.Count;

    public int OptionalImagePathAwareReferenceCount => OptionalImagePathAwareRelativePaths.Count;

    public IReadOnlyList<ResourceReference> AudioReferences => audioReferences;

    public IReadOnlyList<ResourceReference> VisualReferences => visualReferences;

    public IReadOnlyList<ResourceReference> MovieReferences => movieReferences;

    public IReadOnlyList<ResourceReference> OptionalImageReferences => optionalImageReferences;

    public IReadOnlyList<UnsupportedChartResourceReference> UnsupportedResourceReferences => unsupportedResourceReferences;

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

    private static ChartResourceSnapshot Create(BMSFile file)
    {
        if (file == null)
        {
            throw new ArgumentNullException(nameof(file));
        }
        EnsureComponentCollectionsLoaded(file);
        var snapshot = new ChartResourceSnapshot();
        if ((file.ResourceReferences?.Count ?? 0) > 0)
        {
            foreach (ChartResourceReference reference in file.ResourceReferences)
            {
                snapshot.AddReference(reference);
            }
        }
        else
        {
            foreach (string audioPath in file.WAVfiles ?? Enumerable.Empty<string>())
            {
                snapshot.AddReference(ChartResourceKind.Audio, audioPath);
            }
            foreach (string visualPath in file.BGAfiles ?? Enumerable.Empty<string>())
            {
                snapshot.AddReference(ChartResourcePathNormalizer.ClassifyPath(visualPath), visualPath);
            }
        }
        snapshot.AddOptionalImage(file.banner);
        snapshot.AddOptionalImage(file.backbmp);
        snapshot.AddOptionalImage(file.stagefile);
        snapshot.AddUnsupportedReferences(file.UnsupportedResourceReferences);
        snapshot.PathSegmentReferenceCount = snapshot.EnumerateAllRelativePaths().Count(ChartResourcePathNormalizer.HasDirectorySegments);
        return snapshot;
    }

    public static ChartResourceSnapshot Create(ChartFile chart)
    {
        if (chart == null)
        {
            throw new ArgumentNullException(nameof(chart));
        }
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null && !HasProjectedResourceLists(chart))
        {
            return Create(bmsFile);
        }
        ChartResourceSnapshot snapshot = CreateFromChartFields(chart, bmsFile?.ResourceReferences);
        if (bmsFile != null)
        {
            snapshot.AddUnsupportedReferences(bmsFile.UnsupportedResourceReferences);
        }
        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null && !HasProjectedResourceLists(chart))
        {
            return Create(bmsonSong);
        }
        if (bmsonSong != null)
        {
            snapshot.AddUnsupportedReferences(bmsonSong.UnsupportedResourceReferences);
        }
        return snapshot;
    }

    private static bool HasProjectedResourceLists(ChartFile chart)
    {
        return (chart?.AudioResourcePaths?.Count ?? 0) > 0
            || (chart?.VisualResourcePaths?.Count ?? 0) > 0;
    }

    private static ChartResourceSnapshot CreateFromChartFields(
        ChartFile chart,
        IEnumerable<ChartResourceReference> rawResourceReferences = null)
    {
        var snapshot = new ChartResourceSnapshot();
        Dictionary<ResourceReferenceLookupKey, ChartResourceReference> rawResourceLookup = BuildRawResourceReferenceLookup(rawResourceReferences);
        foreach (string audioPath in chart.AudioResourcePaths ?? Enumerable.Empty<string>())
        {
            snapshot.AddReference(ResolveRawReference(rawResourceLookup, ChartResourceKind.Audio, audioPath) ?? new ChartResourceReference(ChartResourceKind.Audio, audioPath, audioPath));
        }
        foreach (string visualPath in chart.VisualResourcePaths ?? Enumerable.Empty<string>())
        {
            ChartResourceKind kind = ChartResourcePathNormalizer.ClassifyPath(visualPath);
            snapshot.AddReference(ResolveRawReference(rawResourceLookup, kind, visualPath) ?? new ChartResourceReference(kind, visualPath, visualPath));
        }
        snapshot.AddOptionalImage(chart.Banner);
        snapshot.AddOptionalImage(chart.Backbmp);
        snapshot.AddOptionalImage(chart.Stagefile);
        snapshot.PathSegmentReferenceCount = snapshot.EnumerateAllRelativePaths().Count(ChartResourcePathNormalizer.HasDirectorySegments);
        return snapshot;
    }

    private static Dictionary<ResourceReferenceLookupKey, ChartResourceReference> BuildRawResourceReferenceLookup(
        IEnumerable<ChartResourceReference> references)
    {
        var lookup = new Dictionary<ResourceReferenceLookupKey, ChartResourceReference>();
        foreach (ChartResourceReference reference in references ?? [])
        {
            if (string.IsNullOrWhiteSpace(reference.NormalizedPath))
            {
                continue;
            }
            var key = new ResourceReferenceLookupKey(reference.Kind, reference.NormalizedPath);
            if (!lookup.ContainsKey(key))
            {
                lookup.Add(key, reference);
            }
        }
        return lookup;
    }

    private static ChartResourceReference? ResolveRawReference(
        Dictionary<ResourceReferenceLookupKey, ChartResourceReference> lookup,
        ChartResourceKind kind,
        string normalizedPath)
    {
        if (lookup == null || lookup.Count == 0 || string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }
        return lookup.TryGetValue(new ResourceReferenceLookupKey(kind, normalizedPath), out ChartResourceReference reference)
            ? reference
            : null;
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
        snapshot.AddUnsupportedReferences(song.UnsupportedResourceReferences);
        snapshot.PathSegmentReferenceCount = snapshot.EnumerateAllRelativePaths().Count(ChartResourcePathNormalizer.HasDirectorySegments);
        return snapshot;
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
            AddResourceKey(AudioRelativePaths, AudioRelativePathHashes, AudioPathAwareRelativePaths, AudioPathAwareRelativePathHashes, audioReferences, reference);
        }
        foreach (ResourceReference reference in other.VisualReferences)
        {
            AddResourceKey(VisualRelativePaths, VisualRelativePathHashes, VisualPathAwareRelativePaths, VisualPathAwareRelativePathHashes, visualReferences, reference);
        }
        foreach (ResourceReference reference in other.MovieReferences)
        {
            AddResourceKey(MovieRelativePaths, MovieRelativePathHashes, MoviePathAwareRelativePaths, MoviePathAwareRelativePathHashes, movieReferences, reference);
        }
        foreach (ResourceReference reference in other.OptionalImageReferences)
        {
            AddResourceKey(OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImagePathAwareRelativePaths, OptionalImagePathAwareRelativePathHashes, optionalImageReferences, reference);
        }
        AddUnsupportedReferences(other.UnsupportedResourceReferences);
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
        AddNormalized(ChartResourceKind.Image, OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImagePathAwareRelativePaths, OptionalImagePathAwareRelativePathHashes, optionalImageReferences, path);
    }

    private void AddReference(ChartResourceKind kind, string path)
    {
        AddReference(kind, path, path);
    }

    private void AddReference(ChartResourceReference reference)
    {
        AddReference(reference.Kind, reference.NormalizedPath, reference.RawPath);
    }

    private void AddReference(ChartResourceKind kind, string path, string rawPath)
    {
        switch (kind)
        {
            case ChartResourceKind.Audio:
                AddNormalized(kind, AudioRelativePaths, AudioRelativePathHashes, AudioPathAwareRelativePaths, AudioPathAwareRelativePathHashes, audioReferences, path, rawPath);
                break;
            case ChartResourceKind.Image:
                AddNormalized(kind, VisualRelativePaths, VisualRelativePathHashes, VisualPathAwareRelativePaths, VisualPathAwareRelativePathHashes, visualReferences, path, rawPath);
                break;
            case ChartResourceKind.Movie:
                AddNormalized(kind, MovieRelativePaths, MovieRelativePathHashes, MoviePathAwareRelativePaths, MoviePathAwareRelativePathHashes, movieReferences, path, rawPath);
                break;
            default:
                AddUnsupportedReferenceIfNeeded(kind, rawPath);
                break;
        }
    }

    private void AddNormalized(ChartResourceKind kind, ISet<string> relativePaths, ISet<uint> relativePathHashes, ISet<string> pathAwareRelativePaths, ISet<uint> pathAwareRelativePathHashes, ICollection<ResourceReference> references, string path, string rawPath = null)
    {
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(path);
        if (!result.IsValid)
        {
            AddUnsupportedReferenceIfNeeded(kind, rawPath ?? path, result.Status);
            return;
        }
        string normalizedPath = StripLookupExtension(result.NormalizedPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }
        AddResourceKey(
            relativePaths,
            relativePathHashes,
            pathAwareRelativePaths,
            pathAwareRelativePathHashes,
            references,
            new ResourceReference(
                normalizedPath,
                ChartResourceKeyHash.GetLookupHash(normalizedPath),
                IsNormalizedPathAware(normalizedPath),
                rawPath ?? path));
    }

    private void AddUnsupportedReferenceIfNeeded(
        ChartResourceKind kind,
        string path,
        ChartResourcePathNormalizationStatus status = ChartResourcePathNormalizationStatus.ParentTraversalUnsupported)
    {
        if (status != ChartResourcePathNormalizationStatus.ParentTraversalUnsupported)
        {
            return;
        }
        unsupportedResourceReferences.Add(new UnsupportedChartResourceReference(
            kind == ChartResourceKind.Unknown ? ChartResourcePathNormalizer.ClassifyReferencePathExtension(path) : kind,
            path,
            status));
    }

    private void AddUnsupportedReferences(IEnumerable<UnsupportedChartResourceReference> references)
    {
        foreach (UnsupportedChartResourceReference reference in references ?? [])
        {
            if (reference.Reason == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported)
            {
                unsupportedResourceReferences.Add(reference);
            }
        }
    }

    private static void AddResourceKey(ISet<string> relativePaths, ISet<uint> relativePathHashes, ISet<string> pathAwareRelativePaths, ISet<uint> pathAwareRelativePathHashes, ICollection<ResourceReference> references, ResourceReference reference)
    {
        string normalizedPath = reference.NormalizedPath;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }
        if (!relativePaths.Add(normalizedPath))
        {
            return;
        }
        uint relativePathHash = reference.RelativePathHash == 0
            ? ChartResourceKeyHash.GetLookupHash(normalizedPath)
            : reference.RelativePathHash;
        relativePathHashes.Add(relativePathHash);
        bool isPathAware = reference.IsPathAware || IsNormalizedPathAware(normalizedPath);
        if (isPathAware)
        {
            pathAwareRelativePaths.Add(normalizedPath);
            pathAwareRelativePathHashes.Add(relativePathHash);
        }
        references?.Add(new ResourceReference(normalizedPath, relativePathHash, isPathAware, reference.RawPath));
    }

    private static bool IsNormalizedPathAware(string normalizedPath)
    {
        return !string.IsNullOrWhiteSpace(normalizedPath)
            && (normalizedPath.IndexOf(Path.DirectorySeparatorChar) >= 0 || normalizedPath.IndexOf(Path.AltDirectorySeparatorChar) >= 0);
    }

    private static string StripLookupExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string extension = Path.GetExtension(value);
        return string.IsNullOrWhiteSpace(extension) ? value : Path.ChangeExtension(value, null);
    }

    private readonly struct ResourceReferenceLookupKey(ChartResourceKind kind, string normalizedPath) : IEquatable<ResourceReferenceLookupKey>
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        private readonly ChartResourceKind kind = kind;

        private readonly string normalizedPath = normalizedPath ?? string.Empty;

        public bool Equals(ResourceReferenceLookupKey other)
        {
            return kind == other.kind
                && PathComparer.Equals(normalizedPath, other.normalizedPath);
        }

        public override bool Equals(object obj)
        {
            return obj is ResourceReferenceLookupKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)kind * 397) ^ PathComparer.GetHashCode(normalizedPath);
            }
        }
    }
}
