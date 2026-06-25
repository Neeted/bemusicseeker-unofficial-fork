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

    internal const string TextGroupName = "text";

    internal static readonly string[] ChartExtensions = [.. ChartFileKindResolver.ChartExtensions];

    internal static readonly string[] AudioExtensions = [.. ChartResourceExtensions.AudioExtensions];

    internal static readonly string[] ImageExtensions = [.. ChartResourceExtensions.ImageExtensions];

    internal static readonly string[] MovieExtensions = [.. ChartResourceExtensions.MovieExtensions];

    internal static readonly string[] TextExtensions = [".txt"];

    private static readonly HashSet<string> chartExtensionsSet = new(ChartExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> audioExtensionsSet = new(AudioExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> imageExtensionsSet = new(ImageExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> movieExtensionsSet = new(MovieExtensions, StringComparer.OrdinalIgnoreCase);

    internal static ChartScanResult BuildFromRoots(IEnumerable<string> roots)
    {
        return TryBuildFromRoots(roots, out ChartScanResult result, out _)
            ? result
            : new ChartScanResult();
    }

    internal static bool TryBuildFromRoots(IEnumerable<string> roots, out ChartScanResult result, out string failureReason)
    {
        RootFileEnumerationResult enumerationResult = new FastRootFileEnumerator().EnumerateFiles(roots, CreateDefaultEnumerationGroups(), verboseLog: false);
        if (!RootFileEnumerationService.IsAuthoritativeComplete(enumerationResult))
        {
            result = null;
            failureReason = RootFileEnumerationService.GetNonAuthoritativeReason(enumerationResult);
            return false;
        }
        result = BuildFromGroupedPaths(enumerationResult);
        failureReason = string.Empty;
        return true;
    }

    internal static ChartScanResult BuildFromChartDirectories(IEnumerable<string> chartDirectories)
    {
        return BuildFromRoots(chartDirectories);
    }

    internal static IReadOnlyList<RootFileEnumerationGroup> CreateDefaultEnumerationGroups(bool includeAllFiles = false, bool includeTextFiles = true, bool includeDirectoryMetadata = false)
    {
        return CreateEnumerationGroups(ChartExtensions, includeAllFiles, includeTextFiles, includeDirectoryMetadata);
    }

    internal static IReadOnlyList<RootFileEnumerationGroup> CreateEnumerationGroups(
        IEnumerable<string> chartExtensions,
        bool includeAllFiles = false,
        bool includeTextFiles = true,
        bool includeDirectoryMetadata = false)
    {
        List<RootFileEnumerationGroup> groups =
        [
            new RootFileEnumerationGroup(ChartGroupName, ResolveChartExtensions(chartExtensions)),
            new RootFileEnumerationGroup(AudioGroupName, AudioExtensions),
            new RootFileEnumerationGroup(ImageGroupName, ImageExtensions),
            new RootFileEnumerationGroup(MovieGroupName, MovieExtensions)
        ];
        if (includeTextFiles)
        {
            groups.Add(new RootFileEnumerationGroup(TextGroupName, TextExtensions));
        }
        if (includeDirectoryMetadata)
        {
            groups.Add(new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true));
        }
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
            enumerationResult?.GetEntries(ChartGroupName) ?? [],
            enumerationResult?.GetPaths(AudioGroupName) ?? [],
            enumerationResult?.GetPaths(ImageGroupName) ?? [],
            enumerationResult?.GetPaths(MovieGroupName) ?? [],
            enumerationResult?.GetEntries(TextGroupName) ?? [],
            enumerationResult?.GetEntries(RootFileEnumerationService.DirectoriesGroupName) ?? []);
    }

    internal static ChartScanResult BuildFromAbsolutePaths(
        IEnumerable<string> chartFilePaths,
        IEnumerable<string> audioFilePaths,
        IEnumerable<string> imageFilePaths,
        IEnumerable<string> movieFilePaths,
        IEnumerable<string> textFilePaths = null)
    {
        return BuildFromAbsolutePaths(
            chartFilePaths,
            audioFilePaths,
            imageFilePaths,
            movieFilePaths,
            NormalizeTextFilePaths(textFilePaths).Select(path => new RootFileEnumerationEntry(path)));
    }

    internal static ChartScanResult BuildFromAbsolutePaths(
        IEnumerable<string> chartFilePaths,
        IEnumerable<string> audioFilePaths,
        IEnumerable<string> imageFilePaths,
        IEnumerable<string> movieFilePaths,
        IEnumerable<RootFileEnumerationEntry> textFileEntries)
    {
        return BuildFromAbsolutePaths(
            NormalizeChartFilePaths(chartFilePaths).Select(path => new RootFileEnumerationEntry(path)),
            audioFilePaths,
            imageFilePaths,
            movieFilePaths,
            textFileEntries);
    }

    internal static ChartScanResult BuildFromAbsolutePaths(
        IEnumerable<RootFileEnumerationEntry> chartFileEntries,
        IEnumerable<string> audioFilePaths,
        IEnumerable<string> imageFilePaths,
        IEnumerable<string> movieFilePaths,
        IEnumerable<RootFileEnumerationEntry> textFileEntries)
    {
        return BuildFromAbsolutePaths(
            chartFileEntries,
            audioFilePaths,
            imageFilePaths,
            movieFilePaths,
            textFileEntries,
            null);
    }

    internal static ChartScanResult BuildFromAbsolutePaths(
        IEnumerable<RootFileEnumerationEntry> chartFileEntries,
        IEnumerable<string> audioFilePaths,
        IEnumerable<string> imageFilePaths,
        IEnumerable<string> movieFilePaths,
        IEnumerable<RootFileEnumerationEntry> textFileEntries,
        IEnumerable<RootFileEnumerationEntry> directoryEntries)
    {
        var result = new ChartScanResult();
        RootFileEnumerationEntry[] normalizedChartEntries = NormalizeChartFileEntries(chartFileEntries);
        foreach (RootFileEnumerationEntry chartEntry in normalizedChartEntries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            string chartPath = chartEntry.Path;
            result.ChartFilePaths.Add(chartPath);
            result.ChartFileEntriesByPath[chartPath] = chartEntry;
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
        RootFileEnumerationEntry[] normalizedTextFileEntries = NormalizeTextFileEntries(textFileEntries);
        AddTextFileEntries(result, normalizedTextFileEntries);
        AddDirectoryEntries(result, NormalizeDirectoryEntries(directoryEntries));

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
            .Select(LongPathFileSystem.NormalizePathForStorage)
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

    private static void AssignTextFiles(
        IEnumerable<string> chartDirectories,
        IEnumerable<string> absolutePaths,
        ISet<string> chartDirectoriesWithTextFiles)
    {
        var directorySet = new HashSet<string>(chartDirectories ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (string absolutePath in (absolutePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            .Select(LongPathFileSystem.NormalizePathForStorage)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrWhiteSpace(directory) && directorySet.Contains(directory))
            {
                chartDirectoriesWithTextFiles.Add(directory);
            }
        }
    }

    internal static void AddDirectTextFileDirectories(ChartScanResult result, IEnumerable<string> textFilePaths)
    {
        AddDirectTextFileEntries(result, NormalizeTextFilePaths(textFilePaths).Select(path => new RootFileEnumerationEntry(path)));
    }

    internal static void AddDirectTextFileEntries(ChartScanResult result, IEnumerable<RootFileEnumerationEntry> textFileEntries)
    {
        if (result == null)
        {
            return;
        }
        AddTextFileEntries(result, NormalizeTextFileEntries(textFileEntries));
    }

    internal static void AddDirectDirectoryEntries(ChartScanResult result, IEnumerable<RootFileEnumerationEntry> directoryEntries)
    {
        if (result == null)
        {
            return;
        }
        AddDirectoryEntries(result, NormalizeDirectoryEntries(directoryEntries));
    }

    private static void AddTextFileEntries(ChartScanResult result, IEnumerable<RootFileEnumerationEntry> textFileEntries)
    {
        if (result == null)
        {
            return;
        }

        RootFileEnumerationEntry[] entries = [.. textFileEntries ?? []];
        AssignTextFiles(result.ChartDirectories, entries.Select(entry => entry.Path), result.ChartDirectoriesWithTextFiles);
        foreach (RootFileEnumerationEntry entry in entries)
        {
            string absolutePath = entry?.Path;
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                continue;
            }
            result.TextFileEntriesByPath[absolutePath] = entry;
            if (IsFolderInfoFilePath(absolutePath))
            {
                result.FolderInfoFilePaths.Add(absolutePath);
                result.FolderInfoFileEntriesByPath[absolutePath] = entry;
            }
        }
    }

    private static bool IsFolderInfoFilePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetFileName(path), "folderinfo.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDirectoryEntries(ChartScanResult result, IEnumerable<RootFileEnumerationEntry> directoryEntries)
    {
        if (result == null)
        {
            return;
        }

        foreach (RootFileEnumerationEntry entry in directoryEntries ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            result.DirectoryEntriesByPath[normalized] = string.Equals(normalized, entry.Path, StringComparison.OrdinalIgnoreCase)
                ? entry
                : new RootFileEnumerationEntry(normalized, entry.LastWriteTimeUtc, entry.FileSize);
        }
    }

    private static string[] NormalizeTextFilePaths(IEnumerable<string> textFilePaths)
    {
        return [.. (textFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            .Select(LongPathFileSystem.NormalizePathForStorage)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string[] NormalizeChartFilePaths(IEnumerable<string> chartFilePaths)
    {
        return [.. (chartFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(LongPathFileSystem.NormalizePathForStorage)
            .Distinct(StringComparer.Ordinal)];
    }

    private static RootFileEnumerationEntry[] NormalizeChartFileEntries(IEnumerable<RootFileEnumerationEntry> chartFileEntries)
    {
        return [.. (chartFileEntries ?? [])
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            .GroupBy(entry => LongPathFileSystem.NormalizePathForStorage(entry.Path), StringComparer.Ordinal)
            .Select(group =>
            {
                RootFileEnumerationEntry entry = group.First();
                string fullPath = LongPathFileSystem.NormalizePathForStorage(entry.Path);
                return string.Equals(fullPath, entry.Path, StringComparison.OrdinalIgnoreCase)
                    ? entry
                    : new RootFileEnumerationEntry(fullPath, entry.LastWriteTimeUtc, entry.FileSize);
            })];
    }

    private static RootFileEnumerationEntry[] NormalizeTextFileEntries(IEnumerable<RootFileEnumerationEntry> textFileEntries)
    {
        return [.. (textFileEntries ?? [])
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            .Where(entry => string.Equals(Path.GetExtension(entry.Path), ".txt", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => LongPathFileSystem.NormalizePathForStorage(entry.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                RootFileEnumerationEntry entry = group.First();
                string fullPath = LongPathFileSystem.NormalizePathForStorage(entry.Path);
                return string.Equals(fullPath, entry.Path, StringComparison.OrdinalIgnoreCase)
                    ? entry
                    : new RootFileEnumerationEntry(fullPath, entry.LastWriteTimeUtc, entry.FileSize);
            })];
    }

    private static RootFileEnumerationEntry[] NormalizeDirectoryEntries(IEnumerable<RootFileEnumerationEntry> directoryEntries)
    {
        return [.. (directoryEntries ?? [])
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            .Select(entry =>
            {
                string normalized = Lr2FolderPath.NormalizeDirectoryPath(entry.Path);
                return string.IsNullOrWhiteSpace(normalized)
                    ? null
                    : new RootFileEnumerationEntry(normalized, entry.LastWriteTimeUtc, entry.FileSize);
            })
            .Where(entry => entry != null)
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
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
