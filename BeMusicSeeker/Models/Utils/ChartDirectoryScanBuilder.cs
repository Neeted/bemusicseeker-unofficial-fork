using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models.Utils;

internal static class ChartDirectoryScanBuilder
{
    internal const string ChartGroupName = "chart";

    internal const string AudioGroupName = "audio";

    internal const string ImageGroupName = "image";

    internal const string MovieGroupName = "movie";

    internal static readonly string[] ChartExtensions = BMSFile.bmsExtensions.Concat(new[] { ".bmson" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static readonly string[] AudioExtensions = BMSFile.wavExtensions.Concat(new[] { ".flac" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static readonly string[] ImageExtensions = BMSFile.bgaImageExtensions.Concat(new[] { ".jpeg" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static readonly string[] MovieExtensions = BMSFile.bgaMovieExtensions.Concat(new[] { ".webm", ".mkv", ".m1v", ".m2v", ".3gp", ".flv", ".rm" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static readonly HashSet<string> chartExtensionsSet = new HashSet<string>(ChartExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> audioExtensionsSet = new HashSet<string>(AudioExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> imageExtensionsSet = new HashSet<string>(ImageExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> movieExtensionsSet = new HashSet<string>(MovieExtensions, StringComparer.OrdinalIgnoreCase);

    internal static BmsScanResult BuildFromRoots(IEnumerable<string> roots)
    {
        RootFileEnumerationResult enumerationResult = new FastRootFileEnumerator().EnumerateFiles(roots, CreateDefaultEnumerationGroups(), verboseLog: false);
        return BuildFromGroupedPaths(enumerationResult);
    }

    internal static BmsScanResult BuildFromChartDirectories(IEnumerable<string> chartDirectories)
    {
        return BuildFromRoots(chartDirectories);
    }

    internal static IReadOnlyList<RootFileEnumerationGroup> CreateDefaultEnumerationGroups(bool includeAllFiles = false)
    {
        List<RootFileEnumerationGroup> groups = new List<RootFileEnumerationGroup>
        {
            new RootFileEnumerationGroup(ChartGroupName, ChartExtensions),
            new RootFileEnumerationGroup(AudioGroupName, AudioExtensions),
            new RootFileEnumerationGroup(ImageGroupName, ImageExtensions),
            new RootFileEnumerationGroup(MovieGroupName, MovieExtensions)
        };
        if (includeAllFiles)
        {
            groups.Add(new RootFileEnumerationGroup(RootFileEnumerationService.AllFilesGroupName, Array.Empty<string>(), includeAllFiles: true));
        }
        return groups;
    }

    internal static BmsScanResult BuildFromGroupedPaths(RootFileEnumerationResult enumerationResult)
    {
        return BuildFromAbsolutePaths(
            enumerationResult?.GetPaths(ChartGroupName) ?? Array.Empty<string>(),
            enumerationResult?.GetPaths(AudioGroupName) ?? Array.Empty<string>(),
            enumerationResult?.GetPaths(ImageGroupName) ?? Array.Empty<string>(),
            enumerationResult?.GetPaths(MovieGroupName) ?? Array.Empty<string>());
    }

    internal static BmsScanResult BuildFromAbsolutePaths(
        IEnumerable<string> chartFilePaths,
        IEnumerable<string> audioFilePaths,
        IEnumerable<string> imageFilePaths,
        IEnumerable<string> movieFilePaths)
    {
        BmsScanResult result = new BmsScanResult();
        HashSet<string> normalizedChartPaths = new HashSet<string>(
            (chartFilePaths ?? Enumerable.Empty<string>())
                .Where((string path) => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        foreach (string chartPath in normalizedChartPaths.OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase))
        {
            result.ChartFilePaths.Add(chartPath);
            string chartDirectory = Path.GetDirectoryName(chartPath);
            if (!string.IsNullOrWhiteSpace(chartDirectory))
            {
                result.ChartDirectories.Add(chartDirectory);
            }
        }

        Dictionary<string, HashSet<uint>> allBaseNameHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> audioBaseNameHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> imageBaseNameHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> movieBaseNameHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> audioRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> imageRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> movieRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedAllBaseNameHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedAudioBaseNameHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedImageBaseNameHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedMovieBaseNameHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedAudioRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedImageRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedMovieRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);

        AssignResourceFiles(result.ChartDirectories, audioFilePaths, ChartResourceKind.Audio, allBaseNameHashes, audioBaseNameHashes, audioRelativePathHashes, selfOwnedAllBaseNameHashes, selfOwnedAudioBaseNameHashes, selfOwnedAudioRelativePathHashes);
        AssignResourceFiles(result.ChartDirectories, imageFilePaths, ChartResourceKind.Image, allBaseNameHashes, imageBaseNameHashes, imageRelativePathHashes, selfOwnedAllBaseNameHashes, selfOwnedImageBaseNameHashes, selfOwnedImageRelativePathHashes);
        AssignResourceFiles(result.ChartDirectories, movieFilePaths, ChartResourceKind.Movie, allBaseNameHashes, movieBaseNameHashes, movieRelativePathHashes, selfOwnedAllBaseNameHashes, selfOwnedMovieBaseNameHashes, selfOwnedMovieRelativePathHashes);

        SetDictionary(result.AllResourceBaseNameHashesByChartDirectory, allBaseNameHashes);
        SetDictionary(result.AudioBaseNameHashesByChartDirectory, audioBaseNameHashes);
        SetDictionary(result.ImageBaseNameHashesByChartDirectory, imageBaseNameHashes);
        SetDictionary(result.MovieBaseNameHashesByChartDirectory, movieBaseNameHashes);
        SetDictionary(result.AudioRelativePathHashesByChartDirectory, audioRelativePathHashes);
        SetDictionary(result.ImageRelativePathHashesByChartDirectory, imageRelativePathHashes);
        SetDictionary(result.MovieRelativePathHashesByChartDirectory, movieRelativePathHashes);
        SetDictionary(result.SelfOwnedAllResourceBaseNameHashesByChartDirectory, selfOwnedAllBaseNameHashes);
        SetDictionary(result.SelfOwnedAudioBaseNameHashesByChartDirectory, selfOwnedAudioBaseNameHashes);
        SetDictionary(result.SelfOwnedImageBaseNameHashesByChartDirectory, selfOwnedImageBaseNameHashes);
        SetDictionary(result.SelfOwnedMovieBaseNameHashesByChartDirectory, selfOwnedMovieBaseNameHashes);
        SetDictionary(result.SelfOwnedAudioRelativePathHashesByChartDirectory, selfOwnedAudioRelativePathHashes);
        SetDictionary(result.SelfOwnedImageRelativePathHashesByChartDirectory, selfOwnedImageRelativePathHashes);
        SetDictionary(result.SelfOwnedMovieRelativePathHashesByChartDirectory, selfOwnedMovieRelativePathHashes);
        return result;
    }

    private static Dictionary<string, HashSet<uint>> InitializeDirectoryHashSets(IEnumerable<string> chartDirectories)
    {
        Dictionary<string, HashSet<uint>> map = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (string chartDirectory in chartDirectories ?? Enumerable.Empty<string>())
        {
            map[chartDirectory] = new HashSet<uint>();
        }
        return map;
    }

    private static void AssignResourceFiles(
        IEnumerable<string> chartDirectories,
        IEnumerable<string> absolutePaths,
        ChartResourceKind kind,
        Dictionary<string, HashSet<uint>> allBaseNameHashes,
        Dictionary<string, HashSet<uint>> categoryBaseNameHashes,
        Dictionary<string, HashSet<uint>> categoryRelativePathHashes,
        Dictionary<string, HashSet<uint>> selfOwnedAllBaseNameHashes,
        Dictionary<string, HashSet<uint>> selfOwnedCategoryBaseNameHashes,
        Dictionary<string, HashSet<uint>> selfOwnedCategoryRelativePathHashes)
    {
        HashSet<string> directorySet = new HashSet<string>(chartDirectories ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (string absolutePath in (absolutePaths ?? Enumerable.Empty<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            List<string> ownerDirectories = FindOwningChartDirectories(directorySet, absolutePath);
            if (ownerDirectories.Count == 0)
            {
                continue;
            }
            string baseName = ChartResourcePathNormalizer.NormalizeFileNameForLookup(Path.GetFileName(absolutePath));
            if (string.IsNullOrWhiteSpace(baseName))
            {
                continue;
            }
            for (int i = 0; i < ownerDirectories.Count; i++)
            {
                string ownerDirectory = ownerDirectories[i];
                string relativePath = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(
                    ChartResourcePathNormalizer.NormalizeRelativePathForLookup(ownerDirectory, absolutePath));
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }
                uint relativePathHash = BMSDirectoryFileNameHash.GetLookupHash(relativePath);
                allBaseNameHashes[ownerDirectory].Add(relativePathHash);
                categoryBaseNameHashes[ownerDirectory].Add(relativePathHash);
                categoryRelativePathHashes[ownerDirectory].Add(relativePathHash);
                if (i == 0)
                {
                    selfOwnedAllBaseNameHashes[ownerDirectory].Add(relativePathHash);
                    selfOwnedCategoryBaseNameHashes[ownerDirectory].Add(relativePathHash);
                    selfOwnedCategoryRelativePathHashes[ownerDirectory].Add(relativePathHash);
                }
            }
        }
    }

    private static List<string> FindOwningChartDirectories(ISet<string> chartDirectories, string absolutePath)
    {
        List<string> owners = new List<string>();
        if (chartDirectories == null || chartDirectories.Count == 0 || string.IsNullOrWhiteSpace(absolutePath))
        {
            return owners;
        }
        string currentDirectory = Path.GetDirectoryName(absolutePath);
        while (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            if (chartDirectories.Contains(currentDirectory))
            {
                owners.Add(currentDirectory);
            }
            currentDirectory = Path.GetDirectoryName(currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        return owners;
    }

    private static void SetDictionary(Dictionary<string, uint[]> destination, Dictionary<string, HashSet<uint>> source)
    {
        destination.Clear();
        foreach (KeyValuePair<string, HashSet<uint>> entry in source)
        {
            destination[entry.Key] = entry.Value.OrderBy((uint hash) => hash).ToArray();
        }
    }

    internal static bool IsChartFile(string path)
    {
        return HasExtension(path, chartExtensionsSet);
    }

    internal static bool IsAudioFile(string path)
    {
        return HasExtension(path, audioExtensionsSet);
    }

    internal static bool IsImageFile(string path)
    {
        return HasExtension(path, imageExtensionsSet);
    }

    internal static bool IsMovieFile(string path)
    {
        return HasExtension(path, movieExtensionsSet);
    }

    private static bool HasExtension(string path, ISet<string> extensions)
    {
        string extension = Path.GetExtension(path ?? string.Empty);
        return !string.IsNullOrWhiteSpace(extension) && extensions.Contains(extension);
    }
}
