using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

internal sealed class RootFileEnumerationEntry(string path, DateTime? lastWriteTimeUtc = null, long? fileSize = null)
{
    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public string Path { get; } = path ?? string.Empty;

    public DateTime? LastWriteTimeUtc { get; } = NormalizeUtc(lastWriteTimeUtc);

    public long? FileSize { get; } = fileSize >= 0 ? fileSize : null;

    public int? LastWriteTimeUnixSeconds => LastWriteTimeUtc.HasValue ? ToUnixSeconds(LastWriteTimeUtc.Value) : null;

    internal static RootFileEnumerationEntry FromFileData(FileData file)
    {
        return file == null
            ? null
            : new RootFileEnumerationEntry(file.Path, file.LastWriteTimeUtc, file.Size);
    }

    internal static RootFileEnumerationEntry FromDirectoryInfo(string path)
    {
        try
        {
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(path);
            return LongPathFileSystem.DirectoryExists(normalizedPath)
                ? new RootFileEnumerationEntry(normalizedPath, LongPathFileSystem.GetLastWriteTimeUtc(normalizedPath, isDirectory: true))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? NormalizeUtc(DateTime? timestamp)
    {
        if (!timestamp.HasValue || timestamp.Value <= DateTime.MinValue)
        {
            return null;
        }

        return timestamp.Value.Kind == DateTimeKind.Utc
            ? timestamp.Value
            : timestamp.Value.ToUniversalTime();
    }

    private static int ToUnixSeconds(DateTime timestampUtc)
    {
        DateTime normalized = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        long seconds = (long)Math.Floor((normalized - UnixEpochUtc).TotalSeconds);
        if (seconds < int.MinValue)
        {
            return int.MinValue;
        }
        if (seconds > int.MaxValue)
        {
            return int.MaxValue;
        }
        return (int)seconds;
    }
}

internal sealed class RootFileEnumerationGroup(
    string name,
    IEnumerable<string> extensions,
    bool includeAllFiles = false,
    bool includeDirectories = false,
    IEnumerable<string> excludedDirectories = null)
{
    public string Name { get; } = name ?? string.Empty;

    public string[] Extensions { get; } = [.. (extensions ?? [])
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith(".") ? extension : "." + extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    public bool IncludeAllFiles { get; } = includeAllFiles;

    public bool IncludeDirectories { get; } = includeDirectories;

    public string[] ExcludedDirectories { get; } = [.. (excludedDirectories ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory =>
            {
                try
                {
                    return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    return null;
                }
            })
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}

internal sealed class RootFileEnumerationResult
{
    public bool Success { get; set; }

    public string BackendName { get; set; } = string.Empty;

    public string ErrorReason { get; set; } = string.Empty;

    public long EnumerationMs { get; set; }

    public int TotalFileCount { get; set; }

    public Dictionary<string, HashSet<string>> PathsByGroup { get; } = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, RootFileEnumerationEntry>> EntriesByGroup { get; } = new Dictionary<string, Dictionary<string, RootFileEnumerationEntry>>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> QueryMsByGroup { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ulong> QueryHitCountByGroup { get; } = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

    public void InitializeGroup(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }

        PathsByGroup[groupName] = new HashSet<string>(StringComparer.Ordinal);
        EntriesByGroup[groupName] = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.Ordinal);
        QueryMsByGroup[groupName] = 0L;
        QueryHitCountByGroup[groupName] = 0UL;
    }

    public void AddEntry(string groupName, RootFileEnumerationEntry entry)
    {
        if (string.IsNullOrWhiteSpace(groupName) || entry == null || string.IsNullOrWhiteSpace(entry.Path))
        {
            return;
        }

        if (!PathsByGroup.TryGetValue(groupName, out HashSet<string> paths))
        {
            paths = new HashSet<string>(StringComparer.Ordinal);
            PathsByGroup[groupName] = paths;
        }

        if (!EntriesByGroup.TryGetValue(groupName, out Dictionary<string, RootFileEnumerationEntry> entries))
        {
            entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.Ordinal);
            EntriesByGroup[groupName] = entries;
        }

        paths.Add(entry.Path);
        entries[entry.Path] = entry;
    }

    public IReadOnlyCollection<string> GetPaths(string groupName)
    {
        if (!string.IsNullOrWhiteSpace(groupName) && PathsByGroup.TryGetValue(groupName, out HashSet<string> paths))
        {
            return paths;
        }
        return [];
    }

    public IReadOnlyCollection<RootFileEnumerationEntry> GetEntries(string groupName)
    {
        if (!string.IsNullOrWhiteSpace(groupName) && EntriesByGroup.TryGetValue(groupName, out Dictionary<string, RootFileEnumerationEntry> entries))
        {
            return entries.Values;
        }
        return [];
    }

    public RootFileEnumerationEntry GetEntry(string groupName, string path)
    {
        return !string.IsNullOrWhiteSpace(groupName)
            && !string.IsNullOrWhiteSpace(path)
            && EntriesByGroup.TryGetValue(groupName, out Dictionary<string, RootFileEnumerationEntry> entries)
            && entries.TryGetValue(path, out RootFileEnumerationEntry entry)
            ? entry
            : null;
    }

    public long GetQueryMs(string groupName)
    {
        return !string.IsNullOrWhiteSpace(groupName) && QueryMsByGroup.TryGetValue(groupName, out long ms) ? ms : 0L;
    }

    public ulong GetQueryHitCount(string groupName)
    {
        return !string.IsNullOrWhiteSpace(groupName) && QueryHitCountByGroup.TryGetValue(groupName, out ulong count) ? count : 0UL;
    }
}

internal interface IRootFileEnumerator
{
    RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false);
}

internal static class RootFileEnumerationService
{
    internal const string AllFilesGroupName = "__all__";

    internal const string DirectoriesGroupName = "__directories__";

    internal static List<string> NormalizeExecutionRoots(IEnumerable<string> rootDirectories)
    {
        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && LongPathFileSystem.DirectoryExists(path))
            .Select(LongPathFileSystem.NormalizePathForStorage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)];

        var result = new List<string>(roots.Count);
        foreach (string root in roots)
        {
            if (result.Any(existing => IsSameOrDescendant(root, existing)))
            {
                continue;
            }
            result.Add(root);
        }
        return result;
    }

    internal static RootFileEnumerationResult EnumerateFilesWithFallback(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
    {
        List<RootFileEnumerationGroup> groupList = [.. (groups ?? []).Where(group => group != null && !string.IsNullOrWhiteSpace(group.Name))];
        if (groupList.Count == 0)
        {
            return new RootFileEnumerationResult
            {
                Success = true,
                BackendName = "fast"
            };
        }

        RootFileEnumerationResult result = new EverythingRootFileEnumerator().EnumerateFiles(rootDirectories, groupList, verboseLog);
        if (result.Success)
        {
            return result;
        }
        if (IsBridgeContractFailure(result.ErrorReason))
        {
            return result;
        }

        RootFileEnumerationResult fallbackResult = new FastRootFileEnumerator().EnumerateFiles(rootDirectories, groupList, verboseLog);
        if (fallbackResult.Success && string.IsNullOrWhiteSpace(fallbackResult.ErrorReason))
        {
            fallbackResult.ErrorReason = result.ErrorReason ?? string.Empty;
        }
        return fallbackResult;
    }

    internal static bool IsBridgeContractFailure(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.StartsWith("bridge_contract_mismatch:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_header_size_mismatch:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_fixed_scan_export_missing", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_source_root_scan_export_missing", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_grouped_enumeration_export_missing", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_dll_not_found:", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("bridge_dll_load_failed:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string normalizedCandidate = NormalizeDirectoryForComparison(candidate);
        string normalizedRoot = NormalizeDirectoryForComparison(root);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryForComparison(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
