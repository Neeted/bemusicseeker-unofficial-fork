using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

internal sealed class EverythingRootFileEnumerator : IRootFileEnumerator
{
    private const uint RootVisibilityProbeGroupId = 0xFFFFFFFEu;

    private static readonly Dictionary<string, uint> StableGroupIds = new(StringComparer.OrdinalIgnoreCase)
    {
        { ChartDirectoryScanBuilder.ChartGroupName, 1u },
        { ChartDirectoryScanBuilder.AudioGroupName, 2u },
        { ChartDirectoryScanBuilder.ImageGroupName, 3u },
        { ChartDirectoryScanBuilder.MovieGroupName, 4u },
        { ChartDirectoryScanBuilder.TextGroupName, 5u },
        { RootFileEnumerationService.AllFilesGroupName, 6u },
        { RootFileEnumerationService.DirectoriesGroupName, 7u }
    };

    private readonly EverythingNative everythingNative;

    internal EverythingRootFileEnumerator(EverythingNative everythingNative)
    {
        this.everythingNative = everythingNative
            ?? throw new ArgumentNullException(nameof(everythingNative));
    }

    public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
    {
        var result = new RootFileEnumerationResult
        {
            BackendName = EverythingNative.GroupedEnumerationBackendName
        };

        List<string> roots = NormalizeRoots(rootDirectories, result);
        List<RootFileEnumerationGroup> groupList = [.. (groups ?? []).Where(group => group != null && !string.IsNullOrWhiteSpace(group.Name))];
        foreach (RootFileEnumerationGroup group in groupList)
        {
            result.InitializeGroup(group.Name);
        }

        if (roots.Count == 0 || groupList.Count == 0)
        {
            result.Success = result.IsComplete;
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
                string query = group.IncludeDirectories
                    ? EverythingNative.BuildDirectoriesQuery([.. roots], group.ExcludedDirectories)
                    : group.IncludeAllFiles
                    ? EverythingNative.BuildAllFilesQuery([.. roots], group.ExcludedDirectories)
                    : EverythingNative.BuildFilesQuery([.. roots], Array.ConvertAll(group.Extensions, extension => extension.TrimStart('.')), group.ExcludedDirectories);
                groupedQueries.Add(new EverythingNative.BridgeGroupedQuery(groupId, query));
            }

            if (!everythingNative.TryEnumerateGroupedFiles(groupedQueries, out EverythingNative.BridgeGroupedEnumerationResult groupedResult, out string reason))
            {
                result.MarkFailed(reason ?? "bridge_grouped_query_failed");
                return result;
            }

            foreach (KeyValuePair<uint, EverythingNative.BridgeGroupedEnumerationGroupResult> entry in groupedResult.Groups)
            {
                if (!groupNamesById.TryGetValue(entry.Key, out string groupName))
                {
                    continue;
                }

                EverythingNative.BridgeGroupedEnumerationGroupResult groupResult = entry.Value;
                result.PathsByGroup[groupName] = groupResult.Paths ?? new HashSet<string>(StringComparer.Ordinal);
                result.EntriesByGroup[groupName] = groupResult.Entries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.Ordinal);
                result.QueryMsByGroup[groupName] = groupResult.QueryMs;
                result.QueryHitCountByGroup[groupName] = groupResult.HitCount;
            }

            if (result.PathsByGroup.TryGetValue(RootFileEnumerationService.AllFilesGroupName, out HashSet<string> allFiles))
            {
                result.TotalFileCount = allFiles.Count;
            }
            else
            {
                result.TotalFileCount = groupedResult?.TotalFileCount ?? 0;
                if (result.TotalFileCount == 0)
                {
                    result.TotalFileCount = result.PathsByGroup.Values.SelectMany(paths => paths).Distinct(StringComparer.Ordinal).Count();
                }
            }

            if (result.TotalFileCount == 0)
            {
                if (TryProbeRootVisibility(roots, out string probeReason))
                {
                    result.Success = result.IsComplete;
                    result.ErrorReason = string.Empty;
                    return result;
                }
                result.MarkFailed(string.IsNullOrWhiteSpace(probeReason)
                    ? "empty_results_with_roots"
                    : "empty_results_with_roots:" + probeReason);
                return result;
            }

            result.Success = result.IsComplete;
            return result;
        }
        finally
        {
            stopwatch.Stop();
            result.EnumerationMs = stopwatch.ElapsedMilliseconds;
        }
    }

    private bool TryProbeRootVisibility(IReadOnlyCollection<string> roots, out string reason)
    {
        reason = null;
        if (roots == null || roots.Count == 0)
        {
            return true;
        }

        if (!everythingNative.TryEnumerateGroupedFiles(
            [new EverythingNative.BridgeGroupedQuery(RootVisibilityProbeGroupId, EverythingNative.BuildDirectoriesQuery([.. roots]))],
            out EverythingNative.BridgeGroupedEnumerationResult result,
            out reason))
        {
            return false;
        }

        return result.Groups.TryGetValue(RootVisibilityProbeGroupId, out EverythingNative.BridgeGroupedEnumerationGroupResult group)
            && group.HitCount > 0;
    }

    private static List<string> NormalizeRoots(IEnumerable<string> rootDirectories, RootFileEnumerationResult result)
    {
        return RootFileEnumerationService.NormalizeExecutionRoots(rootDirectories, result);
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
