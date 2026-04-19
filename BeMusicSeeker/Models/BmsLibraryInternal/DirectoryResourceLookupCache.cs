using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DirectoryResourceLookupCache
{
    internal sealed class Entry
    {
        public HashSet<string> NormalizedBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<uint> FileNameHashes { get; } = new HashSet<uint>();

        public int FileNameHashCount => FileNameHashes.Count;
    }

    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<uint, HashSet<string>> directoriesByHash = new Dictionary<uint, HashSet<string>>();

    public IEnumerable<string> Keys => entries.Keys;

    public int Count => entries.Count;

    public void AddDir(string directoryPath, IEnumerable<string> fileNames)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }
        Entry entry = GetEntryOrNull(directoryPath) ?? new Entry();
        foreach (string fileName in fileNames ?? Enumerable.Empty<string>())
        {
            string normalized = ChartResourcePathNormalizer.NormalizeFileNameForLookup(fileName);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                entry.NormalizedBaseNames.Add(normalized);
                entry.FileNameHashes.Add(BMSDirectoryFileNameHash.GetFileNameHash(normalized));
            }
        }
        SetEntry(directoryPath, entry);
    }

    public void AddDirHashed(string directoryPath, IEnumerable<uint> fileNameHashes)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }
        Entry entry = GetEntryOrNull(directoryPath) ?? new Entry();
        foreach (uint fileNameHash in fileNameHashes ?? Enumerable.Empty<uint>())
        {
            entry.FileNameHashes.Add(fileNameHash);
        }
        SetEntry(directoryPath, entry);
    }

    public bool RemoveDir(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !entries.TryGetValue(directoryPath, out Entry entry))
        {
            return false;
        }
        UnregisterReverseLookup(directoryPath, entry);
        return entries.Remove(directoryPath);
    }

    public bool ReplaceDir(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
        {
            return false;
        }
        if (!entries.TryGetValue(oldPath, out Entry entry))
        {
            return false;
        }
        UnregisterReverseLookup(oldPath, entry);
        entries.Remove(oldPath);
        entries[newPath] = entry;
        RegisterReverseLookup(newPath, entry);
        return true;
    }

    public IReadOnlyCollection<string> GetDirectoriesByHash(uint fileNameHash)
    {
        if (directoriesByHash.TryGetValue(fileNameHash, out HashSet<string> directories))
        {
            return directories;
        }
        return Array.Empty<string>();
    }

    public Entry GetEntryOrNull(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }
        entries.TryGetValue(directoryPath, out Entry entry);
        return entry;
    }

    private void SetEntry(string directoryPath, Entry entry)
    {
        if (entries.TryGetValue(directoryPath, out Entry existing))
        {
            UnregisterReverseLookup(directoryPath, existing);
        }
        entries[directoryPath] = entry;
        RegisterReverseLookup(directoryPath, entry);
    }

    private void RegisterReverseLookup(string directoryPath, Entry entry)
    {
        foreach (uint fileNameHash in entry?.FileNameHashes ?? Enumerable.Empty<uint>())
        {
            if (!directoriesByHash.TryGetValue(fileNameHash, out HashSet<string> directories))
            {
                directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                directoriesByHash[fileNameHash] = directories;
            }
            directories.Add(directoryPath);
        }
    }

    private void UnregisterReverseLookup(string directoryPath, Entry entry)
    {
        foreach (uint fileNameHash in entry?.FileNameHashes ?? Enumerable.Empty<uint>())
        {
            if (!directoriesByHash.TryGetValue(fileNameHash, out HashSet<string> directories))
            {
                continue;
            }
            directories.Remove(directoryPath);
            if (directories.Count == 0)
            {
                directoriesByHash.Remove(fileNameHash);
            }
        }
    }
}
