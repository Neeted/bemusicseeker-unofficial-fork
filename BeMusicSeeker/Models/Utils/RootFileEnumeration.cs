using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

internal sealed class RootFileEnumerationGroup
{
    public RootFileEnumerationGroup(string name, IEnumerable<string> extensions, bool includeAllFiles = false)
    {
        Name = name ?? string.Empty;
        Extensions = (extensions ?? Enumerable.Empty<string>())
            .Where((string extension) => !string.IsNullOrWhiteSpace(extension))
            .Select((string extension) => extension.StartsWith(".") ? extension : "." + extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IncludeAllFiles = includeAllFiles;
    }

    public string Name { get; }

    public string[] Extensions { get; }

    public bool IncludeAllFiles { get; }
}

internal sealed class RootFileEnumerationResult
{
    public bool Success { get; set; }

    public string BackendName { get; set; } = string.Empty;

    public string ErrorReason { get; set; } = string.Empty;

    public long EnumerationMs { get; set; }

    public int TotalFileCount { get; set; }

    public Dictionary<string, HashSet<string>> PathsByGroup { get; } = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> QueryMsByGroup { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ulong> QueryHitCountByGroup { get; } = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> GetPaths(string groupName)
    {
        if (!string.IsNullOrWhiteSpace(groupName) && PathsByGroup.TryGetValue(groupName, out HashSet<string> paths))
        {
            return paths;
        }
        return Array.Empty<string>();
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

    internal static RootFileEnumerationResult EnumerateFilesWithFallback(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
    {
        List<RootFileEnumerationGroup> groupList = (groups ?? Enumerable.Empty<RootFileEnumerationGroup>())
            .Where((RootFileEnumerationGroup group) => group != null && !string.IsNullOrWhiteSpace(group.Name))
            .ToList();
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

        RootFileEnumerationResult fallbackResult = new FastRootFileEnumerator().EnumerateFiles(rootDirectories, groupList, verboseLog);
        if (fallbackResult.Success && string.IsNullOrWhiteSpace(fallbackResult.ErrorReason))
        {
            fallbackResult.ErrorReason = result.ErrorReason ?? string.Empty;
        }
        return fallbackResult;
    }
}
