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
