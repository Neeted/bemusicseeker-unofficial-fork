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
        IReadOnlyList<BMSFile> activeTargets,
        IReadOnlyList<BMSFile> ignoredTargets,
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

    internal IReadOnlyList<BMSFile> ActiveTargets { get; }

    internal IReadOnlyList<BMSFile> IgnoredTargets { get; }

    internal int TargetCount { get; }

    internal int NeedFixCount => ActiveTargets.Count + IgnoredTargets.Count;

    internal int IgnoredCount => IgnoredTargets.Count;

    internal long BuildMs { get; }

    internal static ResourceHealthIndexSnapshot Build(
        IEnumerable<BMSFile> targets,
        BmsLibraryMaintenanceService maintenanceService,
        int version)
    {
        var stopwatch = Stopwatch.StartNew();
        List<BMSFile> activeTargets = [];
        List<BMSFile> ignoredTargets = [];
        Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection> projections = [];
        HashSet<ResourceHealthChartKey> targetKeys = [];
        int targetCount = 0;
        foreach (BMSFile target in targets ?? [])
        {
            if (target == null)
            {
                continue;
            }
            targetCount++;
            var key = ResourceHealthChartKey.FromCompatibilityBmsFile(target);
            if (!key.IsValid)
            {
                continue;
            }
            targetKeys.Add(key);
            IReadOnlyList<ChartWarning> warnings = maintenanceService?.BuildResourceHealthWarnings(target) ?? [];
            if (warnings.Count == 0)
            {
                continue;
            }
            BMSFileMaintenanceInfo maintenanceInfo = target.HasValidMaintenanceInfoSnapshot
                ? target.TryGetMaintenanceInfoWithoutCreating()
                : null;
            bool isIgnored = maintenanceInfo?.is_files_warning_ignored == true;
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
        return new ResourceHealthIndexSnapshot(version, activeTargets, ignoredTargets, projections, targetKeys, targetCount, stopwatch.ElapsedMilliseconds);
    }

    internal ResourceHealthIndexSnapshot ApplyDelta(
        IEnumerable<BMSFile> updatedTargets,
        IEnumerable<BMSFile> removedTargets,
        BmsLibraryMaintenanceService maintenanceService,
        int version)
    {
        var stopwatch = Stopwatch.StartNew();
        var nextProjections = new Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection>(projectionsByKey);
        var nextTargetKeys = new HashSet<ResourceHealthChartKey>(targetKeys);
        HashSet<ResourceHealthChartKey> changedKeys = [];
        foreach (BMSFile removedTarget in removedTargets ?? [])
        {
            var key = ResourceHealthChartKey.FromCompatibilityBmsFile(removedTarget);
            if (!key.IsValid)
            {
                continue;
            }
            nextTargetKeys.Remove(key);
            nextProjections.Remove(key);
            changedKeys.Add(key);
        }
        foreach (BMSFile updatedTarget in updatedTargets ?? [])
        {
            var key = ResourceHealthChartKey.FromCompatibilityBmsFile(updatedTarget);
            if (!key.IsValid)
            {
                continue;
            }
            nextTargetKeys.Add(key);
            nextProjections.Remove(key);
            changedKeys.Add(key);
            IReadOnlyList<ChartWarning> warnings = maintenanceService?.BuildResourceHealthWarnings(updatedTarget) ?? [];
            if (warnings.Count == 0)
            {
                continue;
            }
            BMSFileMaintenanceInfo maintenanceInfo = updatedTarget.HasValidMaintenanceInfoSnapshot
                ? updatedTarget.TryGetMaintenanceInfoWithoutCreating()
                : null;
            bool isIgnored = maintenanceInfo?.is_files_warning_ignored == true;
            nextProjections[key] = new ResourceHealthWarningProjection(version, warnings, isIgnored);
        }
        List<BMSFile> activeTargets = [.. ActiveTargets.Where(file => !changedKeys.Contains(ResourceHealthChartKey.FromCompatibilityBmsFile(file)))];
        List<BMSFile> ignoredTargets = [.. IgnoredTargets.Where(file => !changedKeys.Contains(ResourceHealthChartKey.FromCompatibilityBmsFile(file)))];
        foreach (BMSFile updatedTarget in updatedTargets ?? [])
        {
            var key = ResourceHealthChartKey.FromCompatibilityBmsFile(updatedTarget);
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

    internal ResourceHealthWarningProjection GetProjection(BMSFile file)
    {
        var key = ResourceHealthChartKey.FromCompatibilityBmsFile(file);
        return GetProjection(key);
    }

    internal ResourceHealthWarningProjection GetProjection(LR2SongDBExtended.bmson_song song)
    {
        var key = ResourceHealthChartKey.FromBmsonSong(song);
        return GetProjection(key);
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

        internal static ResourceHealthChartKey FromCompatibilityBmsFile(BMSFile file)
        {
            if (file == null)
            {
                return default;
            }
            bool isBmson = PendingChartEntry.IsBmsonChartFile(file);
            return new ResourceHealthChartKey(isBmson ? "bmson" : "bms", file.path, file.hash);
        }

        internal static ResourceHealthChartKey FromBmsonSong(LR2SongDBExtended.bmson_song song)
        {
            if (song == null)
            {
                return default;
            }
            return new ResourceHealthChartKey("bmson", song.path, song.md5);
        }

        public bool Equals(ResourceHealthChartKey other)
        {
            return string.Equals(kind, other.kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, other.path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(hash, other.hash, StringComparison.OrdinalIgnoreCase);
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
                hashCode = (hashCode * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(path ?? string.Empty);
                hashCode = (hashCode * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(hash ?? string.Empty);
                return hashCode;
            }
        }
    }
}
