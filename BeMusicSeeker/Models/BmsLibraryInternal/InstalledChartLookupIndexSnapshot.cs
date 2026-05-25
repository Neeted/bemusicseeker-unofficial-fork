using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IInstalledChartLookupIndex : IPrimaryHashLookup
{
    int HashCount { get; }

    IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash);
}

internal interface IPrimaryHashLookup
{
    int DistinctPrimaryHashCount { get; }

    bool ContainsPrimaryHash(string lookupHash);

    int GetPrimaryHashCount(string lookupHash);
}

internal interface IMutablePrimaryHashLookup : IPrimaryHashLookup
{
    void AddPrimaryHash(string lookupHash);
}

internal sealed class InstalledChartLookupIndexSnapshot : IInstalledChartLookupIndex
{
    private readonly Dictionary<string, IReadOnlyList<string>> md5Directories;

    private readonly Dictionary<string, IReadOnlyList<string>> sha256Directories;

    private readonly HashSet<string> knownChartDirectories;

    private readonly Dictionary<string, int> primaryHashCounts;

    public InstalledChartLookupIndexSnapshot()
        : this(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
    {
    }

    private InstalledChartLookupIndexSnapshot(
        Dictionary<string, IReadOnlyList<string>> md5Directories,
        Dictionary<string, IReadOnlyList<string>> sha256Directories,
        HashSet<string> knownChartDirectories,
        Dictionary<string, int> primaryHashCounts)
    {
        this.md5Directories = md5Directories ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        this.sha256Directories = sha256Directories ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        this.knownChartDirectories = knownChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        this.primaryHashCounts = primaryHashCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    internal static InstalledChartLookupIndexSnapshot Create(
        Dictionary<string, HashSet<string>> md5DirectoryMap,
        Dictionary<string, HashSet<string>> sha256DirectoryMap,
        HashSet<string> knownChartDirectories,
        Dictionary<string, int> primaryHashCounts)
    {
        return new InstalledChartLookupIndexSnapshot(
            CopyDirectoryMap(md5DirectoryMap),
            CopyDirectoryMap(sha256DirectoryMap),
            CopyKnownDirectories(knownChartDirectories),
            CopyPrimaryHashCounts(primaryHashCounts));
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Md5Directories => md5Directories;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Sha256Directories => sha256Directories;

    public int HashCount => Md5Directories.Count + Sha256Directories.Count;

    public int DirectoryReferenceCount => Md5Directories.Sum(x => x.Value.Count)
        + Sha256Directories.Sum(x => x.Value.Count);

    public IReadOnlyCollection<string> KnownChartDirectories => knownChartDirectories;

    public IReadOnlyDictionary<string, int> PrimaryHashCounts => primaryHashCounts;

    public int DistinctPrimaryHashCount => primaryHashCounts.Count;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public bool ContainsPrimaryHashAfterExcluding(string lookupHash, IReadOnlyDictionary<string, int> excludedCounts)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return false;
        }
        int count = GetPrimaryHashCount(lookupHash);
        int excludedCount = excludedCounts != null && excludedCounts.TryGetValue(lookupHash, out int value) ? value : 0;
        return count - excludedCount > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && primaryHashCounts.TryGetValue(lookupHash, out int count)
            ? count
            : 0;
    }

    public IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }
        if (md5Directories.TryGetValue(lookupHash, out IReadOnlyList<string> md5DirectoryList) && md5DirectoryList != null)
        {
            return CreateDistinctDirectoryList(md5DirectoryList);
        }
        if (sha256Directories.TryGetValue(lookupHash, out IReadOnlyList<string> sha256DirectoryList) && sha256DirectoryList != null)
        {
            return CreateDistinctDirectoryList(sha256DirectoryList);
        }
        return [];
    }

    private static Dictionary<string, IReadOnlyList<string>> CopyDirectoryMap(Dictionary<string, HashSet<string>> source)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return result;
        }
        foreach (KeyValuePair<string, HashSet<string>> item in source)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
            {
                result[item.Key] = [.. (item.Value ?? [])
                    .Where(dir => !string.IsNullOrWhiteSpace(dir))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)];
            }
        }
        return result;
    }

    private static HashSet<string> CopyKnownDirectories(HashSet<string> source)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return result;
        }
        foreach (string directory in source)
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                result.Add(directory);
            }
        }
        return result;
    }

    private static Dictionary<string, int> CopyPrimaryHashCounts(Dictionary<string, int> source)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return result;
        }
        foreach (KeyValuePair<string, int> item in source)
        {
            if (!string.IsNullOrWhiteSpace(item.Key) && item.Value > 0)
            {
                result[item.Key] = item.Value;
            }
        }
        return result;
    }

    private static IReadOnlyList<string> CreateDistinctDirectoryList(IEnumerable<string> directories)
    {
        return [.. (directories ?? [])
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal IPrimaryHashLookup CreateExcludingLookup(IReadOnlyDictionary<string, int> excludedCounts)
    {
        return excludedCounts == null || excludedCounts.Count == 0
            ? this
            : new ExcludingPrimaryHashLookup(this, excludedCounts);
    }
}

internal sealed class InstalledChartLookupIndexState : IPrimaryHashLookup
{
    private readonly Dictionary<string, Dictionary<string, int>> md5DirectoryCounts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, int>> sha256DirectoryCounts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> knownChartDirectoryCounts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> primaryHashCounts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, int>> primaryHashPathCounts = new(StringComparer.OrdinalIgnoreCase);

    private InstalledChartLookupIndexSnapshot snapshot;

    private bool snapshotDirty = true;

    public int DistinctPrimaryHashCount => primaryHashCounts.Count;

    internal int HashCount => md5DirectoryCounts.Count + sha256DirectoryCounts.Count;

    internal int DirectoryReferenceCount => md5DirectoryCounts.Sum(item => item.Value.Count)
        + sha256DirectoryCounts.Sum(item => item.Value.Count);

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && primaryHashCounts.TryGetValue(lookupHash, out int count)
            ? count
            : 0;
    }

    internal IReadOnlyList<string> GetPathsByPrimaryHash(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash)
            && primaryHashPathCounts.TryGetValue(lookupHash, out Dictionary<string, int> pathCounts)
            ? [.. pathCounts.Keys
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]
            : [];
    }

    internal IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }
        if (md5DirectoryCounts.TryGetValue(lookupHash, out Dictionary<string, int> md5Directories) && md5Directories != null)
        {
            return CreateDirectoryList(md5Directories);
        }
        if (sha256DirectoryCounts.TryGetValue(lookupHash, out Dictionary<string, int> sha256Directories) && sha256Directories != null)
        {
            return CreateDirectoryList(sha256Directories);
        }
        return [];
    }

    internal IReadOnlyCollection<string> CreateKnownChartDirectorySnapshot()
    {
        return [.. knownChartDirectoryCounts.Keys
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)];
    }

    internal void AddChart(string path, string md5, string sha256)
    {
        string directory = GetDirectory(path);
        string primaryHash = GetPrimaryHash(md5, sha256);
        AddKnownDirectory(directory);
        AddDirectoryHash(md5DirectoryCounts, md5, directory);
        AddDirectoryHash(sha256DirectoryCounts, sha256, directory);
        AddPrimaryHash(primaryHash);
        AddPrimaryHashPath(primaryHash, path);
    }

    internal void RemoveChart(string path, string md5, string sha256)
    {
        string directory = GetDirectory(path);
        string primaryHash = GetPrimaryHash(md5, sha256);
        RemoveKnownDirectory(directory);
        RemoveDirectoryHash(md5DirectoryCounts, md5, directory);
        RemoveDirectoryHash(sha256DirectoryCounts, sha256, directory);
        RemovePrimaryHash(primaryHash);
        RemovePrimaryHashPath(primaryHash, path);
    }

    internal void MoveChart(string oldPath, string newPath, string md5, string sha256)
    {
        RemoveChart(oldPath, md5, sha256);
        AddChart(newPath, md5, sha256);
    }

    internal IPrimaryHashLookup CreateExcludingLookup(IReadOnlyDictionary<string, int> excludedCounts)
    {
        InstalledChartLookupIndexSnapshot baseline = CreateSnapshot();
        return excludedCounts == null || excludedCounts.Count == 0
            ? baseline
            : new ExcludingPrimaryHashLookup(baseline, excludedCounts);
    }

    internal InstalledChartLookupIndexSnapshot CreateSnapshot()
    {
        if (!snapshotDirty && snapshot != null)
        {
            return snapshot;
        }
        snapshot = InstalledChartLookupIndexSnapshot.Create(
            ToDirectorySetMap(md5DirectoryCounts),
            ToDirectorySetMap(sha256DirectoryCounts),
            new HashSet<string>(knownChartDirectoryCounts.Keys, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(primaryHashCounts, StringComparer.OrdinalIgnoreCase));
        snapshotDirty = false;
        return snapshot;
    }

    private static Dictionary<string, HashSet<string>> ToDirectorySetMap(Dictionary<string, Dictionary<string, int>> source)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Dictionary<string, int>> item in source)
        {
            result[item.Key] = new HashSet<string>(item.Value.Keys, StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    private static IReadOnlyList<string> CreateDirectoryList(Dictionary<string, int> directoryCounts)
    {
        IEnumerable<string> directories = directoryCounts == null
            ? []
            : directoryCounts.Keys;
        return [.. directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)];
    }

    private void AddKnownDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Increment(knownChartDirectoryCounts, directory);
            MarkDirty();
        }
    }

    private void RemoveKnownDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Decrement(knownChartDirectoryCounts, directory))
        {
            MarkDirty();
        }
    }

    private void AddDirectoryHash(Dictionary<string, Dictionary<string, int>> directoryCountsByHash, string hash, string directory)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        if (!directoryCountsByHash.TryGetValue(hash, out Dictionary<string, int> directoryCounts))
        {
            directoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            directoryCountsByHash[hash] = directoryCounts;
        }
        Increment(directoryCounts, directory);
        MarkDirty();
    }

    private void RemoveDirectoryHash(Dictionary<string, Dictionary<string, int>> directoryCountsByHash, string hash, string directory)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        if (directoryCountsByHash.TryGetValue(hash, out Dictionary<string, int> directoryCounts)
            && Decrement(directoryCounts, directory))
        {
            if (directoryCounts.Count == 0)
            {
                directoryCountsByHash.Remove(hash);
            }
            MarkDirty();
        }
    }

    private void AddPrimaryHash(string lookupHash)
    {
        if (!string.IsNullOrWhiteSpace(lookupHash))
        {
            Increment(primaryHashCounts, lookupHash);
            MarkDirty();
        }
    }

    private void RemovePrimaryHash(string lookupHash)
    {
        if (!string.IsNullOrWhiteSpace(lookupHash) && Decrement(primaryHashCounts, lookupHash))
        {
            MarkDirty();
        }
    }

    private void AddPrimaryHashPath(string lookupHash, string path)
    {
        if (string.IsNullOrWhiteSpace(lookupHash) || string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        if (!primaryHashPathCounts.TryGetValue(lookupHash, out Dictionary<string, int> pathCounts))
        {
            pathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            primaryHashPathCounts[lookupHash] = pathCounts;
        }
        Increment(pathCounts, path);
    }

    private void RemovePrimaryHashPath(string lookupHash, string path)
    {
        if (string.IsNullOrWhiteSpace(lookupHash) || string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        if (primaryHashPathCounts.TryGetValue(lookupHash, out Dictionary<string, int> pathCounts)
            && Decrement(pathCounts, path)
            && pathCounts.Count == 0)
        {
            primaryHashPathCounts.Remove(lookupHash);
        }
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    private static bool Decrement(Dictionary<string, int> counts, string key)
    {
        if (!counts.TryGetValue(key, out int count))
        {
            return false;
        }
        if (count <= 1)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = count - 1;
        }
        return true;
    }

    private static string GetPrimaryHash(string md5, string sha256)
    {
        return !string.IsNullOrWhiteSpace(md5)
            ? md5
            : string.IsNullOrWhiteSpace(sha256) ? null : sha256;
    }

    private static string GetDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return DirectoryExt.GetDirectoryNameSimple(path);
        }
        catch
        {
            return null;
        }
    }

    private void MarkDirty()
    {
        snapshotDirty = true;
    }
}

internal sealed class PrimaryHashSetLookup : IMutablePrimaryHashLookup
{
    private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public PrimaryHashSetLookup()
    {
    }

    public PrimaryHashSetLookup(IEnumerable<string> hashes)
    {
        foreach (string hash in hashes ?? [])
        {
            AddPrimaryHash(hash);
        }
    }

    public int DistinctPrimaryHashCount => counts.Count;

    internal IEnumerable<string> PrimaryHashes => counts.Keys;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return !string.IsNullOrWhiteSpace(lookupHash) && counts.TryGetValue(lookupHash, out int count)
            ? count
            : 0;
    }

    public void AddPrimaryHash(string lookupHash)
    {
        if (!string.IsNullOrWhiteSpace(lookupHash))
        {
            counts[lookupHash] = counts.TryGetValue(lookupHash, out int count) ? count + 1 : 1;
        }
    }
}

internal sealed class PrimaryHashGuardLookup : IMutablePrimaryHashLookup
{
    private readonly IPrimaryHashLookup baseline;

    private readonly PrimaryHashSetLookup additions = new();

    public PrimaryHashGuardLookup(IPrimaryHashLookup baseline)
    {
        this.baseline = baseline ?? EmptyPrimaryHashLookup.Instance;
    }

    public int DistinctPrimaryHashCount => baseline.DistinctPrimaryHashCount
        + additions.PrimaryHashes.Count(hash => baseline.GetPrimaryHashCount(hash) == 0);

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return baseline.ContainsPrimaryHash(lookupHash) || additions.ContainsPrimaryHash(lookupHash);
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return baseline.GetPrimaryHashCount(lookupHash) + additions.GetPrimaryHashCount(lookupHash);
    }

    public void AddPrimaryHash(string lookupHash)
    {
        additions.AddPrimaryHash(lookupHash);
    }
}

internal sealed class ExcludingPrimaryHashLookup : IPrimaryHashLookup
{
    private readonly IPrimaryHashLookup source;

    private readonly IReadOnlyDictionary<string, int> excludedCounts;

    private readonly int distinctPrimaryHashCount;

    public ExcludingPrimaryHashLookup(IPrimaryHashLookup source, IReadOnlyDictionary<string, int> excludedCounts)
    {
        this.source = source ?? EmptyPrimaryHashLookup.Instance;
        this.excludedCounts = excludedCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        distinctPrimaryHashCount = this.source.DistinctPrimaryHashCount
            - this.excludedCounts.Count(delegate (KeyValuePair<string, int> excluded)
            {
                int sourceCount = this.source.GetPrimaryHashCount(excluded.Key);
                return sourceCount > 0 && sourceCount - excluded.Value <= 0;
            });
    }

    public int DistinctPrimaryHashCount => Math.Max(0, distinctPrimaryHashCount);

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return GetPrimaryHashCount(lookupHash) > 0;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return 0;
        }
        int excludedCount = excludedCounts.TryGetValue(lookupHash, out int value) ? value : 0;
        return Math.Max(0, source.GetPrimaryHashCount(lookupHash) - excludedCount);
    }
}

internal sealed class EmptyPrimaryHashLookup : IPrimaryHashLookup
{
    public static EmptyPrimaryHashLookup Instance { get; } = new();

    private EmptyPrimaryHashLookup()
    {
    }

    public int DistinctPrimaryHashCount => 0;

    public bool ContainsPrimaryHash(string lookupHash)
    {
        return false;
    }

    public int GetPrimaryHashCount(string lookupHash)
    {
        return 0;
    }
}
