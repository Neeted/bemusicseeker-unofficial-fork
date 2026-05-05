using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ResourceSurfaceMaterializer
{
    internal static DirectoryResourceLookupCache.Entry CreateSingleRootEntry(
        string rootDirectory,
        IEnumerable<string> audioFilePaths,
        IEnumerable<string> imageFilePaths,
        IEnumerable<string> movieFilePaths)
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

        AddResourceHashes(rootDirectory, audioFilePaths, allBaseNameHashes, audioBaseNameHashes, audioRelativePathHashes);
        AddResourceHashes(rootDirectory, imageFilePaths, allBaseNameHashes, imageBaseNameHashes, imageRelativePathHashes);
        AddResourceHashes(rootDirectory, movieFilePaths, allBaseNameHashes, movieBaseNameHashes, movieRelativePathHashes);

        return new DirectoryResourceLookupCache.Entry(
            allBaseNameHashes.OrderBy((uint hash) => hash).ToArray(),
            audioBaseNameHashes.OrderBy((uint hash) => hash).ToArray(),
            imageBaseNameHashes.OrderBy((uint hash) => hash).ToArray(),
            movieBaseNameHashes.OrderBy((uint hash) => hash).ToArray(),
            audioRelativePathHashes.OrderBy((uint hash) => hash).ToArray(),
            imageRelativePathHashes.OrderBy((uint hash) => hash).ToArray(),
            movieRelativePathHashes.OrderBy((uint hash) => hash).ToArray());
    }

    private static void AddResourceHashes(string rootDirectory, IEnumerable<string> absolutePaths, ISet<uint> allBaseNameHashes, ISet<uint> categoryBaseNameHashes, ISet<uint> categoryRelativePathHashes)
    {
        foreach (string absolutePath in (absolutePaths ?? Enumerable.Empty<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string baseName = ChartResourcePathNormalizer.NormalizeFileNameForLookup(Path.GetFileName(absolutePath));
            string relativePath = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(
                ChartResourcePathNormalizer.NormalizeRelativePathForLookup(rootDirectory, absolutePath));
            if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            uint relativePathHash = BMSDirectoryFileNameHash.GetLookupHash(relativePath);
            allBaseNameHashes.Add(relativePathHash);
            categoryBaseNameHashes.Add(relativePathHash);
            categoryRelativePathHashes.Add(relativePathHash);
        }
    }
}
