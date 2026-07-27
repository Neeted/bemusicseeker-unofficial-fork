using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FolderDirectoryEnumerationService
{
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
        var missingTargets = new HashSet<string>(targetSet, StringComparer.OrdinalIgnoreCase);
        foreach (string target in targetSet)
        {
            if (!sourceEntries.TryGetValue(target, out RootFileEnumerationEntry sourceEntry))
            {
                continue;
            }

            string key = Lr2FolderPath.NormalizeDirectoryPath(sourceEntry?.Path);
            if (!string.Equals(key, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries[target] = new RootFileEnumerationEntry(target, sourceEntry.LastWriteTimeUtc, sourceEntry.FileSize);
            missingTargets.Remove(target);
        }
        if (missingTargets.Count == 0)
        {
            return entries;
        }

        foreach (RootFileEnumerationEntry sourceEntry in sourceEntries.Values)
        {
            string key = Lr2FolderPath.NormalizeDirectoryPath(sourceEntry?.Path);
            if (string.IsNullOrWhiteSpace(key) || !missingTargets.Contains(key))
            {
                continue;
            }

            entries[key] = new RootFileEnumerationEntry(key, sourceEntry.LastWriteTimeUtc, sourceEntry.FileSize);
            missingTargets.Remove(key);
            if (missingTargets.Count == 0)
            {
                break;
            }
        }
        return entries;
    }

    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntriesFromGroupedEnumeration(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories,
        EverythingNative everythingNative)
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
            [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)],
            everythingNative,
            retryEmptyEverythingResultWithFastEnumerator: true);
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

}
