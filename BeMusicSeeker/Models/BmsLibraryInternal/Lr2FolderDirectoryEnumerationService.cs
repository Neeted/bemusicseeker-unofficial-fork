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
        HashSet<string> targetSet = new((targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (targetSet.Count == 0)
        {
            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        RootFileEnumerationResult enumeration = RootFileEnumerationService.EnumerateFilesWithFallback(
            rootDirectories,
            [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)]);
        if (enumeration?.Success == true)
        {
            foreach (RootFileEnumerationEntry entry in enumeration.GetEntries(RootFileEnumerationService.DirectoriesGroupName))
            {
                AddEntryIfTarget(entries, targetSet, entry);
            }
        }
        else
        {
            foreach (string targetDirectory in targetSet)
            {
                AddEntryIfTarget(entries, targetSet, RootFileEnumerationEntry.FromDirectoryInfo(targetDirectory));
            }
        }

        foreach (string root in (rootDirectories ?? []).Select(Lr2FolderPath.NormalizeDirectoryPath))
        {
            if (!string.IsNullOrWhiteSpace(root) && targetSet.Contains(root) && !entries.ContainsKey(root))
            {
                AddEntryIfTarget(entries, targetSet, RootFileEnumerationEntry.FromDirectoryInfo(root));
            }
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
