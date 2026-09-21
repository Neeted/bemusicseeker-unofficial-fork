using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class CustomFolderOutputPhysicalSurface
{
    public static CustomFolderOutputPhysicalSurface Empty { get; } = new(
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
        discoveryComplete: true);

    public CustomFolderOutputPhysicalSurface(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> fileEntries,
        bool discoveryComplete)
    {
        FileEntries = NormalizeEntries(fileEntries);
        DiscoveryComplete = discoveryComplete;
    }

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> FileEntries { get; }

    public bool DiscoveryComplete { get; }

    public bool HasEntries => FileEntries.Count > 0;

    public RootFileEnumerationEntry Resolve(string path)
    {
        string normalizedPath = NormalizeFilePath(path);
        return !string.IsNullOrWhiteSpace(normalizedPath)
            && FileEntries.TryGetValue(normalizedPath, out RootFileEnumerationEntry entry)
                ? entry
                : null;
    }

    public static CustomFolderOutputPhysicalSurface FromEntries(
        IEnumerable<RootFileEnumerationEntry> entries,
        bool discoveryComplete)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (RootFileEnumerationEntry entry in entries ?? [])
        {
            AddEntry(result, entry);
        }
        return new CustomFolderOutputPhysicalSurface(result, discoveryComplete);
    }

    public static CustomFolderOutputPhysicalSurface FromDictionary(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries,
        bool discoveryComplete)
    {
        return new CustomFolderOutputPhysicalSurface(entries, discoveryComplete);
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> NormalizeEntries(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            RootFileEnumerationEntry entry = pair.Value;
            string path = NormalizeFilePath(!string.IsNullOrWhiteSpace(entry?.Path) ? entry.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            result[path] = entry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
        }
        return result;
    }

    private static void AddEntry(IDictionary<string, RootFileEnumerationEntry> result, RootFileEnumerationEntry entry)
    {
        string path = NormalizeFilePath(entry?.Path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        result[path] = new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
    }

    internal static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return path;
        }
    }
}
