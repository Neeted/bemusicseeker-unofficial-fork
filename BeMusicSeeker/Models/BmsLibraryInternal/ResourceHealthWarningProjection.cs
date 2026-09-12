using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthWarningProjection
{
    internal static readonly ResourceHealthWarningProjection Empty = new(0, [], false);

    internal ResourceHealthWarningProjection(int version, IReadOnlyList<ChartWarning> warnings, bool isIgnored)
    {
        Version = version;
        Warnings = warnings ?? [];
        IsIgnored = isIgnored;
    }

    internal int Version { get; }

    internal IReadOnlyList<ChartWarning> Warnings { get; }

    internal bool HasIssues => Warnings.Count > 0;

    internal bool IsIgnored { get; }
}

internal sealed class ResourceHealthIndexSnapshot
{
    internal static readonly ResourceHealthIndexSnapshot Empty = new(
        0,
        [],
        [],
        [],
        [],
        0,
        0);

    private readonly Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection> projectionsByKey;

    private readonly HashSet<ResourceHealthChartKey> targetKeys;

    private ResourceHealthIndexSnapshot(
        int version,
        IReadOnlyList<ChartFile> activeTargets,
        IReadOnlyList<ChartFile> ignoredTargets,
        Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection> projectionsByKey,
        HashSet<ResourceHealthChartKey> targetKeys,
        int targetCount,
        long buildMs)
    {
        Version = version;
        ActiveTargets = activeTargets ?? [];
        IgnoredTargets = ignoredTargets ?? [];
        this.projectionsByKey = projectionsByKey ?? [];
        this.targetKeys = targetKeys ?? [];
        TargetCount = targetCount;
        BuildMs = buildMs;
    }

    internal int Version { get; }

    internal IReadOnlyList<ChartFile> ActiveTargets { get; }

    internal IReadOnlyList<ChartFile> IgnoredTargets { get; }

    internal int TargetCount { get; }

    internal int NeedFixCount => ActiveTargets.Count + IgnoredTargets.Count;

    internal int IgnoredCount => IgnoredTargets.Count;

    internal long BuildMs { get; }

    internal static ResourceHealthIndexSnapshot Build(
        IEnumerable<ChartFile> targets,
        BmsLibraryMaintenanceService maintenanceService,
        int version)
    {
        var stopwatch = Stopwatch.StartNew();
        int capacity = targets switch
        {
            IReadOnlyCollection<ChartFile> readOnlyCollection => readOnlyCollection.Count,
            ICollection<ChartFile> collection => collection.Count,
            _ => 0
        };
        List<ChartFile> activeTargets = [];
        List<ChartFile> ignoredTargets = [];
        Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection> projections = [];
        HashSet<ResourceHealthChartKey> targetKeys =
            capacity > 0 ? new HashSet<ResourceHealthChartKey>(capacity) : [];
        foreach (ChartFile target in targets ?? [])
        {
            var key = ResourceHealthChartKey.FromChartFile(target);
            if (!key.IsValid || !targetKeys.Add(key))
            {
                continue;
            }
            IReadOnlyList<ChartWarning> warnings = maintenanceService?.BuildResourceHealthWarnings(target) ?? [];
            if (warnings.Count == 0)
            {
                continue;
            }
            bool isIgnored = BmsLibraryMaintenanceService.AreResourceHealthWarningsIgnored(target);
            var projection = new ResourceHealthWarningProjection(version, warnings, isIgnored);
            projections[key] = projection;
            if (isIgnored)
            {
                ignoredTargets.Add(target);
            }
            else
            {
                activeTargets.Add(target);
            }
        }
        stopwatch.Stop();
        return new ResourceHealthIndexSnapshot(version, activeTargets, ignoredTargets, projections, targetKeys, targetKeys.Count, stopwatch.ElapsedMilliseconds);
    }

    internal ResourceHealthIndexSnapshot ApplyDelta(
        IEnumerable<ChartFile> updatedTargets,
        IEnumerable<ChartFile> removedTargets,
        BmsLibraryMaintenanceService maintenanceService,
        int version)
    {
        var stopwatch = Stopwatch.StartNew();
        var nextProjections = new Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection>(projectionsByKey);
        var nextTargetKeys = new HashSet<ResourceHealthChartKey>(targetKeys);
        HashSet<ResourceHealthChartKey> changedKeys = [];
        List<ChartFile> uniqueRemovedTargets = DistinctValidTargets(removedTargets);
        List<ChartFile> uniqueUpdatedTargets = DistinctValidTargets(updatedTargets);
        HashSet<ResourceHealthChartKey> removedKeys = [.. uniqueRemovedTargets.Select(ResourceHealthChartKey.FromChartFile)];
        uniqueUpdatedTargets = [.. uniqueUpdatedTargets.Where(target => !removedKeys.Any(removedKey => removedKey.HasSameChartIdentity(ResourceHealthChartKey.FromChartFile(target))))];
        foreach (ChartFile removedTarget in uniqueRemovedTargets)
        {
            var key = ResourceHealthChartKey.FromChartFile(removedTarget);
            RemoveMatchingChartIdentity(key, nextTargetKeys, nextProjections, changedKeys);
        }
        foreach (ChartFile updatedTarget in uniqueUpdatedTargets)
        {
            var key = ResourceHealthChartKey.FromChartFile(updatedTarget);
            RemoveMatchingChartIdentity(key, nextTargetKeys, nextProjections, changedKeys);
            nextTargetKeys.Add(key);
            IReadOnlyList<ChartWarning> warnings = maintenanceService?.BuildResourceHealthWarnings(updatedTarget) ?? [];
            if (warnings.Count == 0)
            {
                continue;
            }
            bool isIgnored = BmsLibraryMaintenanceService.AreResourceHealthWarningsIgnored(updatedTarget);
            nextProjections[key] = new ResourceHealthWarningProjection(version, warnings, isIgnored);
        }
        List<ChartFile> activeTargets = [.. ActiveTargets.Where(file => !changedKeys.Contains(ResourceHealthChartKey.FromChartFile(file)))];
        List<ChartFile> ignoredTargets = [.. IgnoredTargets.Where(file => !changedKeys.Contains(ResourceHealthChartKey.FromChartFile(file)))];
        foreach (ChartFile updatedTarget in uniqueUpdatedTargets)
        {
            var key = ResourceHealthChartKey.FromChartFile(updatedTarget);
            if (!key.IsValid || !nextProjections.TryGetValue(key, out ResourceHealthWarningProjection projection))
            {
                continue;
            }
            if (projection.IsIgnored)
            {
                ignoredTargets.Add(updatedTarget);
            }
            else
            {
                activeTargets.Add(updatedTarget);
            }
        }
        stopwatch.Stop();
        return new ResourceHealthIndexSnapshot(version, activeTargets, ignoredTargets, nextProjections, nextTargetKeys, nextTargetKeys.Count, stopwatch.ElapsedMilliseconds);
    }

    private static List<ChartFile> DistinctValidTargets(IEnumerable<ChartFile> targets)
    {
        List<ChartFile> result = [];
        HashSet<ResourceHealthChartKey> keys = [];
        foreach (ChartFile target in targets ?? [])
        {
            var key = ResourceHealthChartKey.FromChartFile(target);
            if (key.IsValid && keys.Add(key))
            {
                result.Add(target);
            }
        }
        return result;
    }

    private static void RemoveMatchingChartIdentity(
        ResourceHealthChartKey key,
        HashSet<ResourceHealthChartKey> nextTargetKeys,
        Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection> nextProjections,
        HashSet<ResourceHealthChartKey> changedKeys)
    {
        if (!key.IsValid)
        {
            return;
        }
        foreach (ResourceHealthChartKey matchingKey in nextTargetKeys.Where(existingKey => existingKey.HasSameChartIdentity(key)).ToList())
        {
            nextTargetKeys.Remove(matchingKey);
            nextProjections.Remove(matchingKey);
            changedKeys.Add(matchingKey);
        }
        changedKeys.Add(key);
    }

    internal ResourceHealthWarningProjection GetProjection(ChartFile chart)
    {
        var key = ResourceHealthChartKey.FromChartFile(chart);
        return GetProjection(key);
    }

    internal ResourceHealthWarningProjection GetProjection(ChartFileKind kind, string path, string md5)
    {
        return GetProjection(ResourceHealthChartKey.FromChartIdentity(kind, path, md5));
    }

    private ResourceHealthWarningProjection GetProjection(ResourceHealthChartKey key)
    {
        if (!key.IsValid || !projectionsByKey.TryGetValue(key, out ResourceHealthWarningProjection projection))
        {
            return ResourceHealthWarningProjection.Empty;
        }
        return projection;
    }

    private readonly struct ResourceHealthChartKey : IEquatable<ResourceHealthChartKey>
    {
        private readonly string kind;
        private readonly string path;
        private readonly string hash;

        private ResourceHealthChartKey(string kind, string path, string hash)
        {
            this.kind = kind ?? string.Empty;
            this.path = path ?? string.Empty;
            this.hash = hash ?? string.Empty;
        }

        internal bool IsValid => !string.IsNullOrWhiteSpace(kind) && !string.IsNullOrWhiteSpace(path);

        internal static ResourceHealthChartKey FromChartFile(ChartFile chart)
        {
            if (chart == null)
            {
                return default;
            }
            return FromChartIdentity(chart.Kind, chart.Path, chart.Md5);
        }

        internal static ResourceHealthChartKey FromChartIdentity(ChartFileKind kind, string path, string md5)
        {
            return new ResourceHealthChartKey(
                kind == ChartFileKind.Bmson ? "bmson" : "bms",
                path,
                md5);
        }

        public bool Equals(ResourceHealthChartKey other)
        {
            return string.Equals(kind, other.kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, other.path, StringComparison.Ordinal)
                && string.Equals(hash, other.hash, StringComparison.OrdinalIgnoreCase);
        }

        internal bool HasSameChartIdentity(ResourceHealthChartKey other)
        {
            return string.Equals(kind, other.kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, other.path, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ResourceHealthChartKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = StringComparer.OrdinalIgnoreCase.GetHashCode(kind ?? string.Empty);
                // Catalog chart paths are exact row identities even on case-insensitive filesystems.
                hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(path ?? string.Empty);
                hashCode = (hashCode * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(hash ?? string.Empty);
                return hashCode;
            }
        }
    }
}
