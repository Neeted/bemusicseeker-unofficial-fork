using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

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

internal sealed class InstalledChartLookupIndexSnapshot : IPrimaryHashLookup
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

    internal IPrimaryHashLookup CreateExcludingLookup(IReadOnlyDictionary<string, int> excludedCounts)
    {
        return excludedCounts == null || excludedCounts.Count == 0
            ? this
            : new ExcludingPrimaryHashLookup(this, excludedCounts);
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
