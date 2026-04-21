using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartResourceSnapshot
{
    public HashSet<string> AudioRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<string> AudioBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> VisualRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<string> VisualBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> MovieRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MovieRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<string> MovieBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MovieBaseNameHashes { get; } = new HashSet<uint>();

    public HashSet<string> OptionalImageRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImageRelativePathHashes { get; } = new HashSet<uint>();

    public HashSet<string> OptionalImageBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImageBaseNameHashes { get; } = new HashSet<uint>();

    public int AudioReferenceCount => AudioRelativePaths.Count;

    public int VisualReferenceCount => VisualRelativePaths.Count;

    public int MovieReferenceCount => MovieRelativePaths.Count;

    public int OptionalImageReferenceCount => OptionalImageRelativePaths.Count;

    public int TotalReferenceCount => AudioReferenceCount + VisualReferenceCount + MovieReferenceCount + OptionalImageReferenceCount;

    public int PathSegmentReferenceCount { get; private set; }

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
        AudioRelativePaths.UnionWith(other.AudioRelativePaths);
        AudioRelativePathHashes.UnionWith(other.AudioRelativePathHashes);
        AudioBaseNames.UnionWith(other.AudioBaseNames);
        AudioBaseNameHashes.UnionWith(other.AudioBaseNameHashes);
        VisualRelativePaths.UnionWith(other.VisualRelativePaths);
        VisualRelativePathHashes.UnionWith(other.VisualRelativePathHashes);
        VisualBaseNames.UnionWith(other.VisualBaseNames);
        VisualBaseNameHashes.UnionWith(other.VisualBaseNameHashes);
        MovieRelativePaths.UnionWith(other.MovieRelativePaths);
        MovieRelativePathHashes.UnionWith(other.MovieRelativePathHashes);
        MovieBaseNames.UnionWith(other.MovieBaseNames);
        MovieBaseNameHashes.UnionWith(other.MovieBaseNameHashes);
        OptionalImageRelativePaths.UnionWith(other.OptionalImageRelativePaths);
        OptionalImageRelativePathHashes.UnionWith(other.OptionalImageRelativePathHashes);
        OptionalImageBaseNames.UnionWith(other.OptionalImageBaseNames);
        OptionalImageBaseNameHashes.UnionWith(other.OptionalImageBaseNameHashes);
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
        AddNormalized(OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImageBaseNames, OptionalImageBaseNameHashes, path);
    }

    private void AddReference(ChartResourceKind kind, string path)
    {
        switch (kind)
        {
            case ChartResourceKind.Audio:
                AddNormalized(AudioRelativePaths, AudioRelativePathHashes, AudioBaseNames, AudioBaseNameHashes, path);
                break;
            case ChartResourceKind.Image:
                AddNormalized(VisualRelativePaths, VisualRelativePathHashes, VisualBaseNames, VisualBaseNameHashes, path);
                break;
            case ChartResourceKind.Movie:
                AddNormalized(MovieRelativePaths, MovieRelativePathHashes, MovieBaseNames, MovieBaseNameHashes, path);
                break;
        }
    }

    private static void AddNormalized(ISet<string> relativePaths, ISet<uint> relativePathHashes, ISet<string> baseNames, ISet<uint> baseNameHashes, string path)
    {
        string normalizedPath = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(path);
        string baseName = ChartResourcePathNormalizer.GetLookupFileName(path);
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(baseName))
        {
            return;
        }
        relativePaths.Add(normalizedPath);
        relativePathHashes.Add(BMSDirectoryFileNameHash.GetLookupHash(normalizedPath));
        baseNames.Add(baseName);
        baseNameHashes.Add(BMSDirectoryFileNameHash.GetLookupHash(baseName));
    }
}
