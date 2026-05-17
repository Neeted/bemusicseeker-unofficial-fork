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

    internal static readonly string[] ChartExtensions = [.. BMSFile.bmsExtensions.Concat([".bmson"]).Distinct(StringComparer.OrdinalIgnoreCase)];

    internal static readonly string[] AudioExtensions = [.. BMSFile.wavExtensions.Concat([".flac"]).Distinct(StringComparer.OrdinalIgnoreCase)];

    internal static readonly string[] ImageExtensions = [.. BMSFile.bgaImageExtensions.Concat([".jpeg"]).Distinct(StringComparer.OrdinalIgnoreCase)];

    internal static readonly string[] MovieExtensions = [.. BMSFile.bgaMovieExtensions.Concat([".webm", ".mkv", ".m1v", ".m2v", ".3gp", ".flv", ".rm"]).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static readonly HashSet<string> chartExtensionsSet = new(ChartExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> audioExtensionsSet = new(AudioExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> imageExtensionsSet = new(ImageExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> movieExtensionsSet = new(MovieExtensions, StringComparer.OrdinalIgnoreCase);

    internal static ChartScanResult BuildFromRoots(IEnumerable<string> roots)
    {
        RootFileEnumerationResult enumerationResult = new FastRootFileEnumerator().EnumerateFiles(roots, CreateDefaultEnumerationGroups(), verboseLog: false);
        return BuildFromGroupedPaths(enumerationResult);
    }

    internal static ChartScanResult BuildFromChartDirectories(IEnumerable<string> chartDirectories)
    {
        return BuildFromRoots(chartDirectories);
    }

    internal static IReadOnlyList<RootFileEnumerationGroup> CreateDefaultEnumerationGroups(bool includeAllFiles = false)
    {
        return CreateEnumerationGroups(ChartExtensions, includeAllFiles);
    }

    internal static IReadOnlyList<RootFileEnumerationGroup> CreateEnumerationGroups(IEnumerable<string> chartExtensions, bool includeAllFiles = false)
    {
        List<RootFileEnumerationGroup> groups =
        [
            new RootFileEnumerationGroup(ChartGroupName, ResolveChartExtensions(chartExtensions)),
            new RootFileEnumerationGroup(AudioGroupName, AudioExtensions),
            new RootFileEnumerationGroup(ImageGroupName, ImageExtensions),
            new RootFileEnumerationGroup(MovieGroupName, MovieExtensions)
        ];
        if (includeAllFiles)
        {
            groups.Add(new RootFileEnumerationGroup(RootFileEnumerationService.AllFilesGroupName, [], includeAllFiles: true));
        }
        return groups;
    }

    internal static string[] ResolveChartExtensions(IEnumerable<string> chartExtensions)
    {
        string[] extensions = [.. (chartExtensions ?? [])
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim())
            .Select(extension => extension.StartsWith(".") ? extension : "." + extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        return extensions.Length == 0 ? ChartExtensions : extensions;
    }

    internal static ChartScanResult BuildFromGroupedPaths(RootFileEnumerationResult enumerationResult)
    {
        return BuildFromAbsolutePaths(
            enumerationResult?.GetPaths(ChartGroupName) ?? [],
            enumerationResult?.GetPaths(AudioGroupName) ?? [],
            enumerationResult?.GetPaths(ImageGroupName) ?? [],
            enumerationResult?.GetPaths(MovieGroupName) ?? []);
    }

    internal static ChartScanResult BuildFromAbsolutePaths(
        IEnumerable<string> chartFilePaths,
        IEnumerable<string> audioFilePaths,
        IEnumerable<string> imageFilePaths,
        IEnumerable<string> movieFilePaths)
    {
        var result = new ChartScanResult();
        var normalizedChartPaths = new HashSet<string>(
            (chartFilePaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        foreach (string chartPath in normalizedChartPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            result.ChartFilePaths.Add(chartPath);
            string chartDirectory = Path.GetDirectoryName(chartPath);
            if (!string.IsNullOrWhiteSpace(chartDirectory))
            {
                result.ChartDirectories.Add(chartDirectory);
            }
        }

        Dictionary<string, HashSet<uint>> audioRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> imageRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> movieRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedAudioRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedImageRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);
        Dictionary<string, HashSet<uint>> selfOwnedMovieRelativePathHashes = InitializeDirectoryHashSets(result.ChartDirectories);

        AssignResourceFiles(result.ChartDirectories, audioFilePaths, ChartResourceKind.Audio, audioRelativePathHashes, selfOwnedAudioRelativePathHashes);
        AssignResourceFiles(result.ChartDirectories, imageFilePaths, ChartResourceKind.Image, imageRelativePathHashes, selfOwnedImageRelativePathHashes);
        AssignResourceFiles(result.ChartDirectories, movieFilePaths, ChartResourceKind.Movie, movieRelativePathHashes, selfOwnedMovieRelativePathHashes);

        SetDictionary(result.AudioRelativePathHashesByChartDirectory, audioRelativePathHashes);
        SetDictionary(result.ImageRelativePathHashesByChartDirectory, imageRelativePathHashes);
        SetDictionary(result.MovieRelativePathHashesByChartDirectory, movieRelativePathHashes);
        SetDictionary(result.SelfOwnedAudioRelativePathHashesByChartDirectory, selfOwnedAudioRelativePathHashes);
        SetDictionary(result.SelfOwnedImageRelativePathHashesByChartDirectory, selfOwnedImageRelativePathHashes);
        SetDictionary(result.SelfOwnedMovieRelativePathHashesByChartDirectory, selfOwnedMovieRelativePathHashes);
        return result;
    }

    private static Dictionary<string, HashSet<uint>> InitializeDirectoryHashSets(IEnumerable<string> chartDirectories)
    {
        var map = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (string chartDirectory in chartDirectories ?? [])
        {
            map[chartDirectory] = [];
        }
        return map;
    }

    private static void AssignResourceFiles(
        IEnumerable<string> chartDirectories,
        IEnumerable<string> absolutePaths,
        ChartResourceKind kind,
        Dictionary<string, HashSet<uint>> categoryRelativePathHashes,
        Dictionary<string, HashSet<uint>> selfOwnedCategoryRelativePathHashes)
    {
        var directorySet = new HashSet<string>(chartDirectories ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (string absolutePath in (absolutePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            List<string> ownerDirectories = FindOwningChartDirectories(directorySet, absolutePath);
            if (ownerDirectories.Count == 0)
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
                uint relativePathHash = ChartResourceKeyHash.GetLookupHash(relativePath);
                categoryRelativePathHashes[ownerDirectory].Add(relativePathHash);
                if (i == 0)
                {
                    selfOwnedCategoryRelativePathHashes[ownerDirectory].Add(relativePathHash);
                }
            }
        }
    }

    private static List<string> FindOwningChartDirectories(ISet<string> chartDirectories, string absolutePath)
    {
        List<string> owners = [];
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
            destination[entry.Key] = [.. entry.Value.OrderBy(hash => hash)];
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
