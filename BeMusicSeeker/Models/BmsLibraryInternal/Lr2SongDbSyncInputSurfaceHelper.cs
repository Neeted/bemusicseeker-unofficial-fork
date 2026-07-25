using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2SongDbSyncInputSurfaceHelper
{
    internal static Lr2TextMetadataCandidateSnapshot CreateLr2SongDbSyncTextMetadataCandidates(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        EverythingNative everythingNative)
    {
        return Lr2FolderInfoCandidateEnumerationService.CreateTextMetadataSnapshot(
            rootDirectories,
            targetDirectories,
            everythingNative);
    }

    internal static IReadOnlyList<string> NormalizeLr2DirectoryMetadataTargets(IEnumerable<string> targetDirectories)
    {
        return [.. (targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyList<string> CreateLr2TextMetadataSourceDirectoriesOutsideRoots(
        IEnumerable<string> sourceDirectories,
        IEnumerable<string> coveredRoots)
    {
        List<string> roots = [.. NormalizeLr2DirectoryMetadataTargets(coveredRoots)];
        return [.. NormalizeLr2DirectoryMetadataTargets(sourceDirectories)
            .Where(source => !roots.Any(root => Lr2FolderPath.IsSameOrDescendant(source, root)
                || Lr2FolderPath.IsSameOrDescendant(root, source)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyCollection<string> MergeLr2DirectoryMetadataTargets(params IEnumerable<string>[] targetSets)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IEnumerable<string> targetSet in targetSets ?? [])
        {
            foreach (string target in NormalizeLr2DirectoryMetadataTargets(targetSet))
            {
                result.Add(target);
            }
        }
        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateLr2DirectoryEntriesFromSurfaceOrGroupedScan(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> sourceEntries,
        IEnumerable<string> groupedSourceDirectories,
        IEnumerable<string> targetDirectories,
        EverythingNative everythingNative)
    {
        IReadOnlyCollection<string> targets = NormalizeLr2DirectoryMetadataTargets(targetDirectories);
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(sourceEntries, targets);
        List<string> missingTargets = [.. targets
            .Where(target => !entries.TryGetValue(target, out RootFileEnumerationEntry entry)
                || entry.LastWriteTimeUtc == null)];
        if (missingTargets.Count == 0)
        {
            return entries;
        }

        IReadOnlyCollection<string> groupedEntriesSourceDirectories = [.. (groupedSourceDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (groupedEntriesSourceDirectories.Count == 0)
        {
            return entries;
        }
        IReadOnlyDictionary<string, RootFileEnumerationEntry> missingEntries =
            CreateLr2SongDbSyncDirectoryEntriesFromGroupedScan(groupedEntriesSourceDirectories, missingTargets, everythingNative);
        return MergeLr2DirectoryEntrySurfaces(entries, missingEntries);
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> MergeLr2DirectoryEntrySurfaces(
        params IReadOnlyDictionary<string, RootFileEnumerationEntry>[] entrySets)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, RootFileEnumerationEntry> entries in entrySets ?? [])
        {
            foreach (RootFileEnumerationEntry entry in entries?.Values ?? [])
            {
                string key = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }
                if (!result.TryGetValue(key, out RootFileEnumerationEntry existing)
                    || (existing.LastWriteTimeUtc == null && entry.LastWriteTimeUtc != null))
                {
                    result[key] = new RootFileEnumerationEntry(key, entry.LastWriteTimeUtc, entry.FileSize);
                }
            }
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateLr2SongDbSyncDirectoryEntriesFromGroupedScan(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        EverythingNative everythingNative)
    {
        return Lr2FolderDirectoryEnumerationService.CreateEntriesFromGroupedEnumeration(rootDirectories, targetDirectories, everythingNative);
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> OverlayLr2DirectoryEntrySurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> baseEntries,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> overlayEntries)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        AddDirectoryEntries(result, baseEntries, overwriteExisting: false);
        AddDirectoryEntries(result, overlayEntries, overwriteExisting: true);
        return result;
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> MergeMissingLr2DirectoryEntrySurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> baseEntries,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> additions)
    {
        if (additions == null || additions.Count == 0)
        {
            return baseEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, RootFileEnumerationEntry> result = null;
        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in additions)
        {
            RootFileEnumerationEntry entry = pair.Value;
            string path = Lr2FolderPath.NormalizeDirectoryPath(!string.IsNullOrWhiteSpace(entry?.Path) ? entry.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            RootFileEnumerationEntry next = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
            bool shouldApply = baseEntries == null
                || !baseEntries.TryGetValue(path, out RootFileEnumerationEntry existing)
                || (existing.LastWriteTimeUtc == null && next.LastWriteTimeUtc != null);
            if (!shouldApply)
            {
                continue;
            }

            result ??= CopyLr2DirectoryEntrySurface(baseEntries);
            result[path] = next;
        }

        return result ?? baseEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> MergePreparedFileSurface(
        IEnumerable<string> basePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> baseEntries,
        IEnumerable<string> preparedPaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> preparedEntries,
        IEnumerable<string> preparedScopeDirectories,
        out IReadOnlyDictionary<string, RootFileEnumerationEntry> mergedEntries)
    {
        Lr2DirectoryScopeMatcher scopeMatcher = Lr2DirectoryScopeMatcher.Create(preparedScopeDirectories);
        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (RootFileEnumerationEntry entry in CreateNormalizedFileEntries(basePaths, baseEntries))
        {
            if (scopeMatcher.ContainsFilePath(entry.Path))
            {
                continue;
            }
            entriesByPath[entry.Path] = entry;
        }
        foreach (RootFileEnumerationEntry entry in CreateNormalizedFileEntries(preparedPaths, preparedEntries))
        {
            entriesByPath[entry.Path] = entry;
        }

        mergedEntries = entriesByPath;
        return [.. entriesByPath.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyList<string> MergePreparedDirectoryList(
        IEnumerable<string> baseDirectories,
        IEnumerable<string> preparedDirectories,
        IEnumerable<string> preparedScopeDirectories)
    {
        IReadOnlyList<string> preparedDirectoryTargets = NormalizeLr2DirectoryMetadataTargets(preparedDirectories);
        Lr2DirectoryScopeMatcher scopeMatcher = Lr2DirectoryScopeMatcher.Create(preparedScopeDirectories);
        if (scopeMatcher.IsEmpty && preparedDirectoryTargets.Count == 0)
        {
            return TryUseNormalizedDirectoryList(baseDirectories, out IReadOnlyList<string> normalizedBaseDirectories)
                ? normalizedBaseDirectories
                : NormalizeLr2DirectoryMetadataTargets(baseDirectories);
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in baseDirectories ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (string.IsNullOrWhiteSpace(normalized)
                || scopeMatcher.ContainsNormalizedDirectory(normalized)
                || !seen.Add(normalized))
            {
                continue;
            }

            result.Add(normalized);
        }
        foreach (string directory in preparedDirectoryTargets)
        {
            if (seen.Add(directory))
            {
                result.Add(directory);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static Dictionary<string, RootFileEnumerationEntry> CopyLr2DirectoryEntrySurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        AddDirectoryEntries(result, entries, overwriteExisting: false);
        return result;
    }

    private static bool TryUseNormalizedDirectoryList(
        IEnumerable<string> directories,
        out IReadOnlyList<string> normalizedDirectories)
    {
        normalizedDirectories = directories as IReadOnlyList<string>;
        if (normalizedDirectories == null)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in normalizedDirectories)
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (string.IsNullOrWhiteSpace(normalized)
                || !string.Equals(normalized, directory, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(normalized))
            {
                normalizedDirectories = null;
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<RootFileEnumerationEntry> CreateNormalizedFileEntries(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawPath in paths ?? [])
        {
            string path = SafeFullPathOrOriginal(rawPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            RootFileEnumerationEntry entry = entriesByPath != null
                && entriesByPath.TryGetValue(rawPath, out RootFileEnumerationEntry rawEntry)
                    ? rawEntry
                    : null;
            entries[path] = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
        }
        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            string path = SafeFullPathOrOriginal(!string.IsNullOrWhiteSpace(pair.Value?.Path) ? pair.Value.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path) || entries.ContainsKey(path))
            {
                continue;
            }
            RootFileEnumerationEntry entry = pair.Value;
            entries[path] = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
        }
        return entries.Values;
    }

    private static void AddDirectoryEntries(
        IDictionary<string, RootFileEnumerationEntry> result,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries,
        bool overwriteExisting)
    {
        if (result == null)
        {
            return;
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            RootFileEnumerationEntry entry = pair.Value;
            string path = Lr2FolderPath.NormalizeDirectoryPath(!string.IsNullOrWhiteSpace(entry?.Path) ? entry.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (!overwriteExisting && result.ContainsKey(path))
            {
                continue;
            }
            result[path] = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
        }
    }

    private static string SafeFullPathOrOriginal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
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
}
