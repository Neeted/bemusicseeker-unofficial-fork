using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FolderDirectoryEnumerationService
{
    internal static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntries(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories)
    {
        HashSet<string> targetSet = [.. (targetDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))];
        if (targetSet.Count == 0)
        {
            return new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (roots.Count > 0)
        {
            RootFileEnumerationResult result = RootFileEnumerationService.EnumerateFilesWithFallback(
                roots,
                [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)]);
            if (result.Success)
            {
                foreach (RootFileEnumerationEntry entry in result.GetEntries(RootFileEnumerationService.DirectoriesGroupName))
                {
                    AddEntryIfTarget(entries, targetSet, entry);
                }
            }
        }

        foreach (string targetDirectory in targetSet)
        {
            if (!entries.ContainsKey(targetDirectory))
            {
                AddEntryIfTarget(entries, targetSet, RootFileEnumerationEntry.FromDirectoryInfo(targetDirectory));
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
