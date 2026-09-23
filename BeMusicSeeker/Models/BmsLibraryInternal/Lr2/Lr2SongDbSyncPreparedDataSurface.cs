using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncPreparedDataSurface
{
    public static Lr2SongDbSyncPreparedDataSurface Empty { get; } = new(
        [],
        [],
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
        [],
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
        [],
        discoveryComplete: true);

    public Lr2SongDbSyncPreparedDataSurface(
        IEnumerable<string> lr2FolderScopeDirectories,
        IEnumerable<string> lr2FolderFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries,
        bool discoveryComplete)
        : this(
            lr2FolderScopeDirectories,
            lr2FolderFilePaths,
            lr2FolderFileEntries,
            null,
            null,
            null,
            null,
            discoveryComplete)
    {
    }

    public Lr2SongDbSyncPreparedDataSurface(
        IEnumerable<string> lr2FolderScopeDirectories,
        IEnumerable<string> lr2FolderFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries,
        IEnumerable<string> folderInfoFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries,
        IEnumerable<string> textFileDirectories,
        bool discoveryComplete)
    {
        Lr2FolderScopeDirectories = NormalizeDirectories(lr2FolderScopeDirectories);
        Lr2FolderFileEntries = NormalizeEntries(lr2FolderFilePaths, lr2FolderFileEntries);
        Lr2FolderFilePaths = [.. Lr2FolderFileEntries.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        DirectoryEntries = NormalizeDirectoryEntries(directoryEntries);
        FolderInfoFileEntries = NormalizeEntries(folderInfoFilePaths, folderInfoFileEntries);
        FolderInfoFilePaths = [.. FolderInfoFileEntries.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        TextFileDirectories = NormalizeDirectories(textFileDirectories);
        Lr2FolderFileDiscoveryComplete = discoveryComplete;
    }

    public IReadOnlyList<string> Lr2FolderScopeDirectories { get; }

    public IReadOnlyList<string> Lr2FolderFilePaths { get; }

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2FolderFileEntries { get; }

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; }

    public IReadOnlyList<string> FolderInfoFilePaths { get; }

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; }

    public IReadOnlyList<string> TextFileDirectories { get; }

    public bool Lr2FolderFileDiscoveryComplete { get; }

    public bool HasLr2FolderSurface =>
        Lr2FolderScopeDirectories.Count > 0
        || Lr2FolderFilePaths.Count > 0
        || !Lr2FolderFileDiscoveryComplete;

    public bool HasPreparedDataSurface =>
        HasLr2FolderSurface
        || DirectoryEntries.Count > 0
        || FolderInfoFilePaths.Count > 0
        || TextFileDirectories.Count > 0;

    public static Lr2SongDbSyncPreparedDataSurface FromSyncItems(
        IEnumerable<string> lr2FolderScopeDirectories,
        IEnumerable<Lr2FolderFileSyncItem> items,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries = null,
        IEnumerable<string> folderInfoFilePaths = null,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries = null,
        IEnumerable<string> textFileDirectories = null,
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

        return new Lr2SongDbSyncPreparedDataSurface(
            lr2FolderScopeDirectories,
            entriesByPath.Keys,
            entriesByPath,
            directoryEntries,
            folderInfoFilePaths,
            folderInfoFileEntries,
            textFileDirectories,
            discoveryComplete);
    }

    public static Lr2SongDbSyncPreparedDataSurface Merge(params Lr2SongDbSyncPreparedDataSurface[] surfaces)
    {
        var scopeDirectories = new List<string>();
        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var directoryEntriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var folderInfoEntriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var textFileDirectories = new List<string>();
        bool discoveryComplete = true;
        foreach (Lr2SongDbSyncPreparedDataSurface surface in surfaces ?? [])
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

            MergeEntries(directoryEntriesByPath, surface.DirectoryEntries);
            MergeEntries(folderInfoEntriesByPath, surface.FolderInfoFileEntries);
            textFileDirectories.AddRange(surface.TextFileDirectories);
        }

        return new Lr2SongDbSyncPreparedDataSurface(
            scopeDirectories,
            entriesByPath.Keys,
            entriesByPath,
            directoryEntriesByPath,
            folderInfoEntriesByPath.Keys,
            folderInfoEntriesByPath,
            textFileDirectories,
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

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> NormalizeDirectoryEntries(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            RootFileEnumerationEntry entry = pair.Value;
            string path = NormalizeDirectoryPath(!string.IsNullOrWhiteSpace(entry?.Path) ? entry.Path : pair.Key);
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

    private static void MergeEntries(
        IDictionary<string, RootFileEnumerationEntry> result,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries)
    {
        if (result == null)
        {
            return;
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            string path = NormalizeFilePath(!string.IsNullOrWhiteSpace(pair.Value?.Path) ? pair.Value.Path : pair.Key);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            RootFileEnumerationEntry entry = pair.Value;
            if (!result.TryGetValue(path, out RootFileEnumerationEntry existing)
                || existing.LastWriteTimeUtc == null && entry?.LastWriteTimeUtc != null)
            {
                result[path] = entry == null
                    ? new RootFileEnumerationEntry(path)
                    : new RootFileEnumerationEntry(path, entry.LastWriteTimeUtc, entry.FileSize);
            }
        }
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
