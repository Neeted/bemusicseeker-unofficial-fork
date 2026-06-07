using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FolderDirectoryEnumerationService
{
    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntries(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories)
    {
        List<string> roots = [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)
                && roots.Any(root => Lr2FolderPath.IsSameOrDescendant(path, root))), StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0 || targetSet.Count == 0)
        {
            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        return CreateEntriesFromTargetSet(targetSet);
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntriesFromTargets(
        IEnumerable<string> targetDirectories)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        return CreateEntriesFromTargetSet(targetSet);
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntriesFromSurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> sourceEntries,
        IEnumerable<string> targetDirectories)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (sourceEntries == null || sourceEntries.Count == 0 || targetSet.Count == 0)
        {
            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (RootFileEnumerationEntry sourceEntry in sourceEntries.Values)
        {
            string key = Lr2FolderPath.NormalizeDirectoryPath(sourceEntry?.Path);
            if (string.IsNullOrWhiteSpace(key) || !targetSet.Contains(key))
            {
                continue;
            }

            entries[key] = new RootFileEnumerationEntry(key, sourceEntry.LastWriteTimeUtc, sourceEntry.FileSize);
        }
        return entries;
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntriesFromGroupedEnumeration(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories)
    {
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (targetSet.Count == 0)
        {
            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        RootFileEnumerationResult result = RootFileEnumerationService.EnumerateFilesWithFallback(
            rootDirectories,
            [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)]);
        if (!result.Success)
        {
            if (RootFileEnumerationService.IsBridgeContractFailure(result.ErrorReason))
            {
                throw new InvalidOperationException("directory metadata grouped enumeration failed: " + result.ErrorReason);
            }

            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        return CreateEntriesFromSurface(result.EntriesByGroup.TryGetValue(RootFileEnumerationService.DirectoriesGroupName, out Dictionary<string, RootFileEnumerationEntry> entries)
            ? entries
            : new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), targetSet);
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntriesFromTargetSet(
        ISet<string> targetSet)
    {
        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string targetDirectory in targetSet)
        {
            AddEntryIfTarget(entries, targetSet, RootFileEnumerationEntry.FromDirectoryInfo(targetDirectory));
        }
        return entries;
    }

    private static void AddEntryIfTarget(
        IDictionary<string, RootFileEnumerationEntry> entries,
        ISet<string> targetSet,
        RootFileEnumerationEntry entry)
    {
        string key = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
        if (string.IsNullOrWhiteSpace(key) || targetSet?.Contains(key) != true)
        {
            return;
        }
        entries[key] = new RootFileEnumerationEntry(key, entry.LastWriteTimeUtc, entry.FileSize);
    }
}
