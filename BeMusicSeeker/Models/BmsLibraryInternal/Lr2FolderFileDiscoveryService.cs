using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderFileCandidateSnapshot(
    IReadOnlyList<string> paths,
    IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath,
    bool discoveryComplete)
{
    public IReadOnlyList<string> Paths { get; } = paths ?? [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> EntriesByPath { get; } =
        entriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public bool DiscoveryComplete { get; } = discoveryComplete;
}

internal static class Lr2FolderFileDiscoveryService
{
    private const string Lr2FolderFileEnumerationGroupName = "lr2folder";

    internal static List<string> CreateDiscoveryDirectories(
        IEnumerable<string> rootDirectories,
        string normalCustomFolderOutputBaseDir,
        string rootCustomFolderOutputBaseDir,
        IEnumerable<string> builtinSourceDirectories)
    {
        return CreateDiscoveryDirectories(
            rootDirectories,
            new[] { normalCustomFolderOutputBaseDir },
            rootCustomFolderOutputBaseDir,
            builtinSourceDirectories);
    }

    internal static List<string> CreateDiscoveryDirectories(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> normalCustomFolderOutputBaseDirs,
        string rootCustomFolderOutputBaseDir,
        IEnumerable<string> builtinSourceDirectories)
    {
        var candidates = new List<string>();
        candidates.AddRange(rootDirectories ?? []);
        candidates.AddRange(normalCustomFolderOutputBaseDirs ?? []);
        candidates.Add(rootCustomFolderOutputBaseDir);
        candidates.AddRange(builtinSourceDirectories ?? []);

        return [.. candidates
            .Where(path => !string.IsNullOrWhiteSpace(path) && LongPathFileSystem.DirectoryExists(path))
            .Select(LongPathFileSystem.NormalizePathForStorage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    internal static Lr2FolderFileCandidateSnapshot CreateFileCandidates(
        IEnumerable<string> rootDirectories,
        string lr2RootPath,
        Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings,
        Action<string> logScan,
        EverythingNative everythingNative,
        IEnumerable<string> excludedDirectories = null)
    {
        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && LongPathFileSystem.DirectoryExists(path))
            .Select(LongPathFileSystem.NormalizePathForStorage)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (roots.Count == 0)
        {
            logScan?.Invoke("lr2folder_scan skipped reason=no_roots roots=0");
            return new Lr2FolderFileCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true);
        }

        IReadOnlyList<string> excludedDirectoryList = [.. (excludedDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(SafeFullPathOrOriginal)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        RootFileEnumerationGroup[] groups = [new RootFileEnumerationGroup(Lr2FolderFileEnumerationGroupName, [".lr2folder"], excludedDirectories: excludedDirectoryList)];
        RootFileEnumerationResult result = RootFileEnumerationService.EnumerateFilesWithFallback(
            roots,
            groups,
            everythingNative,
            retryEmptyEverythingResultWithFastEnumerator: true);
        if (!result.Success)
        {
            logScan?.Invoke("lr2folder_scan failed"
                + " roots=" + roots.Count
                + " backend=" + (result.BackendName ?? string.Empty)
                + " enumerationMs=" + result.EnumerationMs
                + " reason=" + (result.ErrorReason ?? "unknown"));
            return new Lr2FolderFileCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: false);
        }
        List<RootFileEnumerationEntry> rawEntries = [.. result.GetEntries(Lr2FolderFileEnumerationGroupName)
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))];
        List<RootFileEnumerationEntry> includedEntries = [.. rawEntries
            .Where(entry => builtinCustomFolderSettings?.ShouldIncludeCustomFolderFile(entry.Path, lr2RootPath) != false)];
        Dictionary<string, RootFileEnumerationEntry> entriesByPath = includedEntries
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(entry => entry.Path, entry => entry, StringComparer.OrdinalIgnoreCase);
        logScan?.Invoke("lr2folder_scan success"
            + " roots=" + roots.Count
            + " backend=" + (result.BackendName ?? string.Empty)
            + " enumerationMs=" + result.EnumerationMs
            + " queryHits=" + result.GetQueryHitCount(Lr2FolderFileEnumerationGroupName)
            + " queryMs=" + result.GetQueryMs(Lr2FolderFileEnumerationGroupName)
            + " rawEntries=" + rawEntries.Count
            + " includedEntries=" + includedEntries.Count
            + " dedupedEntries=" + entriesByPath.Count
            + " excludedDirs=" + excludedDirectoryList.Count
            + " filteredEntries=" + (rawEntries.Count - includedEntries.Count));
        return new Lr2FolderFileCandidateSnapshot([.. entriesByPath.Keys
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)], entriesByPath, discoveryComplete: true);
    }

    internal static Lr2FolderFileCandidateSnapshot MergeCandidateSurface(
        Lr2FolderFileCandidateSnapshot baseCandidates,
        Lr2SongDbSyncPreparedDataSurface preparedSurface)
    {
        if (preparedSurface?.HasLr2FolderSurface != true)
        {
            return baseCandidates ?? new Lr2FolderFileCandidateSnapshot(
                [],
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: true);
        }

        Lr2DirectoryScopeMatcher scopeMatcher = Lr2DirectoryScopeMatcher.Create(preparedSurface.Lr2FolderScopeDirectories);
        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in baseCandidates?.Paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || scopeMatcher.ContainsFilePath(path))
            {
                continue;
            }

            entriesByPath[path] = baseCandidates.EntriesByPath != null
                && baseCandidates.EntriesByPath.TryGetValue(path, out RootFileEnumerationEntry entry)
                    ? entry
                    : new RootFileEnumerationEntry(path);
        }

        foreach (string path in preparedSurface.Lr2FolderFilePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            entriesByPath[path] = preparedSurface.Lr2FolderFileEntries != null
                && preparedSurface.Lr2FolderFileEntries.TryGetValue(path, out RootFileEnumerationEntry entry)
                    ? entry
                    : new RootFileEnumerationEntry(path);
        }

        return new Lr2FolderFileCandidateSnapshot(
            [.. entriesByPath.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            entriesByPath,
            (baseCandidates?.DiscoveryComplete ?? false) && preparedSurface.Lr2FolderFileDiscoveryComplete);
    }

    internal static Lr2FolderFileCandidateSnapshot ExcludeAppManagedOutputCandidates(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath,
        IEnumerable<string> appManagedOutputFilePaths,
        bool discoveryComplete,
        out int excludedCount,
        IEnumerable<string> appManagedOutputDirectories = null)
    {
        excludedCount = 0;
        HashSet<string> excludedFilePaths = CreateNormalizedPathSet(appManagedOutputFilePaths);
        Lr2DirectoryScopeMatcher excludedDirectoryMatcher = Lr2DirectoryScopeMatcher.Create(appManagedOutputDirectories);
        if (excludedFilePaths.Count == 0 && excludedDirectoryMatcher.IsEmpty)
        {
            Dictionary<string, RootFileEnumerationEntry> unchangedEntries = CreateEntrySurface(paths, entriesByPath);
            return new Lr2FolderFileCandidateSnapshot(
                [.. unchangedEntries.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
                unchangedEntries,
                discoveryComplete);
        }

        var resultEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths ?? [])
        {
            RootFileEnumerationEntry entry = entriesByPath != null
                && entriesByPath.TryGetValue(path, out RootFileEnumerationEntry rawEntry)
                    ? rawEntry
                    : null;
            AddFilteredEntry(
                resultEntries,
                seenPaths,
                path,
                entry,
                excludedFilePaths,
                excludedDirectoryMatcher,
                ref excludedCount);
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            AddFilteredEntry(
                resultEntries,
                seenPaths,
                pair.Key,
                pair.Value,
                excludedFilePaths,
                excludedDirectoryMatcher,
                ref excludedCount);
        }


        return new Lr2FolderFileCandidateSnapshot(
            [.. resultEntries.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            resultEntries,
            discoveryComplete);
    }

    private static HashSet<string> CreateNormalizedPathSet(IEnumerable<string> paths)
    {
        return new HashSet<string>((paths ?? [])
            .Select(SafeFullPathOrOriginal)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> CreateDiscoveryDirectoriesForEnumeration(
        IEnumerable<string> discoveryDirectories,
        Lr2SongDbSyncPreparedDataSurface preparedSurface)
    {
        if (preparedSurface?.HasLr2FolderSurface != true)
        {
            return [.. (discoveryDirectories ?? [])];
        }

        Lr2DirectoryScopeMatcher scopeMatcher = Lr2DirectoryScopeMatcher.Create(preparedSurface.Lr2FolderScopeDirectories);
        return [.. (discoveryDirectories ?? [])
            .Where(directory => !scopeMatcher.ContainsDirectory(directory))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, RootFileEnumerationEntry> CreateEntrySurface(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalizedPath = SafeFullPathOrOriginal(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            RootFileEnumerationEntry entry = entriesByPath != null
                && entriesByPath.TryGetValue(path, out RootFileEnumerationEntry rawEntry)
                    ? rawEntry
                    : null;
            result[normalizedPath] = entry == null
                ? new RootFileEnumerationEntry(normalizedPath)
                : new RootFileEnumerationEntry(normalizedPath, entry.LastWriteTimeUtc, entry.FileSize);
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            string path = SafeFullPathOrOriginal(!string.IsNullOrWhiteSpace(pair.Value?.Path) ? pair.Value.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path) || result.ContainsKey(path))
            {
                continue;
            }

            RootFileEnumerationEntry entry = pair.Value;
            result[path] = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
        }

        return result;
    }

    private static void AddFilteredEntry(
        IDictionary<string, RootFileEnumerationEntry> result,
        ISet<string> seenPaths,
        string path,
        RootFileEnumerationEntry entry,
        ISet<string> excludedFilePaths,
        Lr2DirectoryScopeMatcher excludedDirectoryMatcher,
        ref int excludedCount)
    {
        if (result == null || seenPaths == null)
        {
            return;
        }

        string normalizedPath = SafeFullPathOrOriginal(!string.IsNullOrWhiteSpace(entry?.Path) ? entry.Path : path);
        if (string.IsNullOrWhiteSpace(normalizedPath) || !seenPaths.Add(normalizedPath))
        {
            return;
        }

        if (excludedFilePaths?.Contains(normalizedPath) == true
            || excludedDirectoryMatcher?.ContainsFilePath(normalizedPath) == true)
        {
            excludedCount++;
            return;
        }

        result[normalizedPath] = entry == null
            ? new RootFileEnumerationEntry(normalizedPath)
            : new RootFileEnumerationEntry(normalizedPath, entry.LastWriteTimeUtc, entry.FileSize);
    }

    private static string SafeFullPathOrOriginal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return LongPathFileSystem.NormalizePathForStorage(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return path;
        }
    }

    internal static List<string> CreatePruneDirectories(
        IEnumerable<string> rootDirectories,
        string normalCustomFolderOutputBaseDir,
        string rootCustomFolderOutputBaseDir,
        IEnumerable<string> builtinSourceDirectories,
        bool includeAppManagedOutputDirectories = true)
    {
        return CreatePruneDirectories(
            rootDirectories,
            new[] { normalCustomFolderOutputBaseDir },
            rootCustomFolderOutputBaseDir,
            builtinSourceDirectories,
            includeAppManagedOutputDirectories);
    }

    internal static List<string> CreatePruneDirectories(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> normalCustomFolderOutputBaseDirs,
        string rootCustomFolderOutputBaseDir,
        IEnumerable<string> builtinSourceDirectories,
        bool includeAppManagedOutputDirectories = true)
    {
        var candidates = new List<string>();
        candidates.AddRange(rootDirectories ?? []);
        if (includeAppManagedOutputDirectories)
        {
            candidates.AddRange(normalCustomFolderOutputBaseDirs ?? []);
            candidates.Add(rootCustomFolderOutputBaseDir);
        }
        candidates.AddRange(builtinSourceDirectories ?? []);
        if ((builtinSourceDirectories ?? []).Any())
        {
            candidates.Add(@"LR2files\CustomFolder");
        }

        return [.. candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    internal static List<string> CreateBuiltinCustomFolderPruneDirectories()
    {
        return [@"LR2files\CustomFolder"];
    }

    internal static List<string> CreateBuiltinFolderSourceDirectories(string lr2RootPath)
    {
        if (string.IsNullOrWhiteSpace(lr2RootPath))
        {
            return [];
        }

        return [.. new[]
            {
                Path.Combine(lr2RootPath, "LR2files", "CustomFolder")
            }
            .Where(path => !string.IsNullOrWhiteSpace(path) && LongPathFileSystem.DirectoryExists(path))
            .Select(LongPathFileSystem.NormalizePathForStorage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

}
