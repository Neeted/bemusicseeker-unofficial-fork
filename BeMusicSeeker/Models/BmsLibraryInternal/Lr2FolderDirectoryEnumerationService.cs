using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FolderDirectoryEnumerationService
{
    private const int DirectLookupTargetThreshold = 512;

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

        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        if (targetSet.Count <= DirectLookupTargetThreshold)
        {
            foreach (string targetDirectory in targetSet)
            {
                AddEntryIfTarget(entries, targetSet, RootFileEnumerationEntry.FromDirectoryInfo(targetDirectory));
            }
            return entries;
        }

        RootFileEnumerationResult enumeration = RootFileEnumerationService.EnumerateFilesWithFallback(
            roots,
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

        foreach (string root in roots)
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
