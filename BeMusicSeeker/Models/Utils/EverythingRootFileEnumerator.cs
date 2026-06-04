using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

internal sealed class EverythingRootFileEnumerator : IRootFileEnumerator
{
    private static readonly Dictionary<string, uint> StableGroupIds = new(StringComparer.OrdinalIgnoreCase)
    {
        { ChartDirectoryScanBuilder.ChartGroupName, 1u },
        { ChartDirectoryScanBuilder.AudioGroupName, 2u },
        { ChartDirectoryScanBuilder.ImageGroupName, 3u },
        { ChartDirectoryScanBuilder.MovieGroupName, 4u },
        { ChartDirectoryScanBuilder.TextGroupName, 5u },
        { RootFileEnumerationService.AllFilesGroupName, 6u }
    };

    public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
    {
        var result = new RootFileEnumerationResult
        {
            BackendName = EverythingNative.GroupedEnumerationBackendName
        };

        List<string> roots = NormalizeRoots(rootDirectories);
        List<RootFileEnumerationGroup> groupList = [.. (groups ?? []).Where(group => group != null && !string.IsNullOrWhiteSpace(group.Name))];
        foreach (RootFileEnumerationGroup group in groupList)
        {
            result.PathsByGroup[group.Name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            result.QueryMsByGroup[group.Name] = 0L;
            result.QueryHitCountByGroup[group.Name] = 0UL;
        }

        if (roots.Count == 0 || groupList.Count == 0)
        {
            result.Success = true;
            return result;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var groupedQueries = new List<EverythingNative.BridgeGroupedQuery>(groupList.Count);
            Dictionary<uint, string> groupNamesById = [];
            foreach (RootFileEnumerationGroup group in groupList)
            {
                uint groupId = GetStableGroupId(group.Name);
                groupNamesById[groupId] = group.Name;
                string query = group.IncludeAllFiles
                    ? EverythingNative.BuildAllFilesQuery([.. roots])
                    : EverythingNative.BuildFilesQuery([.. roots], Array.ConvertAll(group.Extensions, extension => extension.TrimStart('.')));
                groupedQueries.Add(new EverythingNative.BridgeGroupedQuery(groupId, query));
            }

            if (!EverythingNative.TryEnumerateGroupedFiles(groupedQueries, out EverythingNative.BridgeGroupedEnumerationResult groupedResult, out string reason))
            {
                result.Success = false;
                result.ErrorReason = reason ?? "bridge_grouped_query_failed";
                return result;
            }

            foreach (KeyValuePair<uint, EverythingNative.BridgeGroupedEnumerationGroupResult> entry in groupedResult.Groups)
            {
                if (!groupNamesById.TryGetValue(entry.Key, out string groupName))
                {
                    continue;
                }

                EverythingNative.BridgeGroupedEnumerationGroupResult groupResult = entry.Value;
                result.PathsByGroup[groupName] = groupResult.Paths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result.QueryMsByGroup[groupName] = groupResult.QueryMs;
                result.QueryHitCountByGroup[groupName] = groupResult.HitCount;
            }

            if (result.PathsByGroup.TryGetValue(RootFileEnumerationService.AllFilesGroupName, out HashSet<string> allFiles))
            {
                result.TotalFileCount = allFiles.Count;
            }
            else
            {
                result.TotalFileCount = groupedResult?.TotalFileCount ?? result.PathsByGroup.Values.SelectMany(paths => paths).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            }

            if (result.TotalFileCount == 0)
            {
                result.Success = false;
                result.ErrorReason = "empty_results_with_roots";
                return result;
            }

            result.Success = true;
            return result;
        }
        finally
        {
            stopwatch.Stop();
            result.EnumerationMs = stopwatch.ElapsedMilliseconds;
        }
    }

    private static List<string> NormalizeRoots(IEnumerable<string> rootDirectories)
    {
        return [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static uint GetStableGroupId(string groupName)
    {
        if (!string.IsNullOrWhiteSpace(groupName) && StableGroupIds.TryGetValue(groupName, out uint knownId))
        {
            return knownId;
        }

        unchecked
        {
            uint hash = 2166136261u;
            foreach (char ch in (groupName ?? string.Empty).ToUpperInvariant())
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return hash == 0u ? 0xFFFFFFFFu : hash;
        }
    }
}
