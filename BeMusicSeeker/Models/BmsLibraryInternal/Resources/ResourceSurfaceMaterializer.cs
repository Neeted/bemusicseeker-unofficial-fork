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

        HashSet<uint> audioRelativePathHashes = [];
        HashSet<uint> imageRelativePathHashes = [];
        HashSet<uint> movieRelativePathHashes = [];

        AddResourceHashes(rootDirectory, audioFilePaths, audioRelativePathHashes);
        AddResourceHashes(rootDirectory, imageFilePaths, imageRelativePathHashes);
        AddResourceHashes(rootDirectory, movieFilePaths, movieRelativePathHashes);

        return new DirectoryResourceLookupCache.Entry(
            [.. audioRelativePathHashes.OrderBy(hash => hash)],
            [.. imageRelativePathHashes.OrderBy(hash => hash)],
            [.. movieRelativePathHashes.OrderBy(hash => hash)]);
    }

    private static void AddResourceHashes(string rootDirectory, IEnumerable<string> absolutePaths, ISet<uint> categoryRelativePathHashes)
    {
        foreach (string absolutePath in (absolutePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
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
