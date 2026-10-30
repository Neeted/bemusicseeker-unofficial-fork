using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一操作で生成・検証した物理面と、管理分だけの不変な型定義を保持します。各層で別の定義snapshotを作りません。</summary>
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

    /// <summary>物理面を捕捉し、管理定義の未提供と完全0件を区別して保持します。型定義の辞書はここで一回だけ変更不能にし、後段へ同じ値を渡します。</summary>
    public Lr2SongDbSyncPreparedDataSurface(
        IEnumerable<string> lr2FolderScopeDirectories,
        IEnumerable<string> lr2FolderFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries,
        IEnumerable<string> folderInfoFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries,
        IEnumerable<string> textFileDirectories,
        bool discoveryComplete,
        IReadOnlyDictionary<string, Lr2FolderFileDefinition> managedFolderDefinitions = null)
    {
        ManagedFolderDefinitions = managedFolderDefinitions == null ? null
            : new System.Collections.ObjectModel.ReadOnlyDictionary<string, Lr2FolderFileDefinition>(
                managedFolderDefinitions.ToDictionary(pair => NormalizeFilePath(pair.Key), pair => pair.Value ?? throw new InvalidOperationException("Managed folder definition is missing."), StringComparer.OrdinalIgnoreCase));
        Lr2FolderScopeDirectories = NormalizeDirectories(lr2FolderScopeDirectories);
        Lr2FolderFileEntries = NormalizeEntries(lr2FolderFilePaths, lr2FolderFileEntries);
        Lr2FolderFilePaths = [.. Lr2FolderFileEntries.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        DirectoryEntries = NormalizeDirectoryEntries(directoryEntries);
        FolderInfoFileEntries = NormalizeEntries(folderInfoFilePaths, folderInfoFileEntries);
        FolderInfoFilePaths = [.. FolderInfoFileEntries.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        TextFileDirectories = NormalizeDirectories(textFileDirectories);
        Lr2FolderFileDiscoveryComplete = discoveryComplete;
    }

    /// <summary>管理定義の完全な面。nullは未提供、空辞書は管理出力0件です。組込みの物理メタデータとは区別します。</summary>
    public IReadOnlyDictionary<string, Lr2FolderFileDefinition> ManagedFolderDefinitions { get; }

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

    /// <summary>管理出力の確定項目から物理時刻と型定義を一緒に渡します。相対組込み項目を管理定義へ読み替えません。</summary>
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
        var definitions = new Dictionary<string, Lr2FolderFileDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (Lr2FolderFileSyncItem item in items ?? [])
        {
            string path = NormalizeFilePath(item?.FilePath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            entriesByPath[path] = new RootFileEnumerationEntry(path, item.LastWriteTimeUtc);
            if (Lr2FolderFileProjection.NormalizeKnownRelativeLr2FolderPath(item.DatabasePath) == null)
            {
                definitions[path] = item.Definition ?? throw new InvalidOperationException("Managed folder definition is missing.");
            }
        }

        return new Lr2SongDbSyncPreparedDataSurface(
            lr2FolderScopeDirectories,
            entriesByPath.Keys,
            entriesByPath,
            directoryEntries,
            folderInfoFilePaths,
            folderInfoFileEntries,
            textFileDirectories,
            discoveryComplete,
            definitions);
    }

    /// <summary>準備面を合流し、管理定義の未提供/完全0件と組込みの物理metadataを保ちます。</summary>
    public static Lr2SongDbSyncPreparedDataSurface Merge(params Lr2SongDbSyncPreparedDataSurface[] surfaces)
    {
        var scopeDirectories = new List<string>();
        var entriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var directoryEntriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var folderInfoEntriesByPath = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        var textFileDirectories = new List<string>();
        bool discoveryComplete = true;
        Dictionary<string, Lr2FolderFileDefinition> definitions = null;
        foreach (Lr2SongDbSyncPreparedDataSurface surface in surfaces ?? [])
        {
            if (surface == null)
            {
                continue;
            }

            if (surface.ManagedFolderDefinitions != null)
            {
                definitions ??= new(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Lr2FolderFileDefinition> pair in surface.ManagedFolderDefinitions)
                {
                    definitions[pair.Key] = pair.Value;
                }
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
            discoveryComplete,
            definitions);
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
