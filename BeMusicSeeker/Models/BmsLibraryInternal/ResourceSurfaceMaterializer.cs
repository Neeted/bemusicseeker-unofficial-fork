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

        HashSet<uint> audioRelativePathHashes = new HashSet<uint>();
        HashSet<uint> imageRelativePathHashes = new HashSet<uint>();
        HashSet<uint> movieRelativePathHashes = new HashSet<uint>();

        AddResourceHashes(rootDirectory, audioFilePaths, audioRelativePathHashes);
        AddResourceHashes(rootDirectory, imageFilePaths, imageRelativePathHashes);
        AddResourceHashes(rootDirectory, movieFilePaths, movieRelativePathHashes);

        return new DirectoryResourceLookupCache.Entry(
            audioRelativePathHashes.OrderBy((uint hash) => hash).ToArray(),
            imageRelativePathHashes.OrderBy((uint hash) => hash).ToArray(),
            movieRelativePathHashes.OrderBy((uint hash) => hash).ToArray());
    }

    private static void AddResourceHashes(string rootDirectory, IEnumerable<string> absolutePaths, ISet<uint> categoryRelativePathHashes)
    {
        foreach (string absolutePath in (absolutePaths ?? Enumerable.Empty<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string relativePath = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(
                ChartResourcePathNormalizer.NormalizeRelativePathForLookup(rootDirectory, absolutePath));
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            uint relativePathHash = ChartResourceKeyHash.GetLookupHash(relativePath);
            categoryRelativePathHashes.Add(relativePathHash);
        }
    }
}
