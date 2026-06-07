using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FullGenerationPreparedDataSurface
{
    public static Lr2FullGenerationPreparedDataSurface Empty { get; } = new(
        [],
        [],
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
        discoveryComplete: true);

    public Lr2FullGenerationPreparedDataSurface(
        IEnumerable<string> lr2FolderScopeDirectories,
        IEnumerable<string> lr2FolderFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries,
        bool discoveryComplete)
    {
        Lr2FolderScopeDirectories = NormalizeDirectories(lr2FolderScopeDirectories);
        Lr2FolderFileEntries = NormalizeEntries(lr2FolderFilePaths, lr2FolderFileEntries);
        Lr2FolderFilePaths = [.. Lr2FolderFileEntries.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        Lr2FolderFileDiscoveryComplete = discoveryComplete;
    }

    public IReadOnlyList<string> Lr2FolderScopeDirectories { get; }

    public IReadOnlyList<string> Lr2FolderFilePaths { get; }

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2FolderFileEntries { get; }

    public bool Lr2FolderFileDiscoveryComplete { get; }

    public bool HasLr2FolderSurface =>
        Lr2FolderScopeDirectories.Count > 0
        || Lr2FolderFilePaths.Count > 0
        || !Lr2FolderFileDiscoveryComplete;

    public static Lr2FullGenerationPreparedDataSurface FromSyncItems(
        IEnumerable<string> lr2FolderScopeDirectories,
        IEnumerable<Lr2FolderFileSyncItem> items,
        bool discoveryComplete = true)
    {
        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (Lr2FolderFileSyncItem item in items ?? [])
        {
            string path = NormalizeFilePath(item?.FilePath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            entriesByPath[path] = new RootFileEnumerationEntry(path, item.LastWriteTimeUtc);
        }

        return new Lr2FullGenerationPreparedDataSurface(
            lr2FolderScopeDirectories,
            entriesByPath.Keys,
            entriesByPath,
            discoveryComplete);
    }

    public static Lr2FullGenerationPreparedDataSurface Merge(params Lr2FullGenerationPreparedDataSurface[] surfaces)
    {
        var scopeDirectories = new List<string>();
        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        bool discoveryComplete = true;
        foreach (Lr2FullGenerationPreparedDataSurface surface in surfaces ?? [])
        {
            if (surface == null)
            {
                continue;
            }

            scopeDirectories.AddRange(surface.Lr2FolderScopeDirectories);
            discoveryComplete = discoveryComplete && surface.Lr2FolderFileDiscoveryComplete;
            foreach (string path in surface.Lr2FolderFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                entriesByPath[path] = surface.Lr2FolderFileEntries != null
                    && surface.Lr2FolderFileEntries.TryGetValue(path, out RootFileEnumerationEntry entry)
                        ? entry
                        : new RootFileEnumerationEntry(path);
            }
        }

        return new Lr2FullGenerationPreparedDataSurface(
            scopeDirectories,
            entriesByPath.Keys,
            entriesByPath,
            discoveryComplete);
    }

    private static IReadOnlyList<string> NormalizeDirectories(IEnumerable<string> directories)
    {
        return [.. (directories ?? [])
            .Select(NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> NormalizeEntries(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawPath in paths ?? [])
        {
            string path = NormalizeFilePath(rawPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            RootFileEnumerationEntry sourceEntry = entriesByPath != null
                && entriesByPath.TryGetValue(rawPath, out RootFileEnumerationEntry rawEntry)
                    ? rawEntry
                    : null;
            result[path] = sourceEntry == null
                ? new RootFileEnumerationEntry(path)
                : new RootFileEnumerationEntry(path, sourceEntry.LastWriteTimeUtc, sourceEntry.FileSize);
        }

        if (entriesByPath == null)
        {
            return result;
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entriesByPath)
        {
            string path = NormalizeFilePath(pair.Key);
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

    private static string NormalizeFilePath(string path)
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

    private static string NormalizeDirectoryPath(string path)
    {
        string normalized = NormalizeFilePath(path);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
