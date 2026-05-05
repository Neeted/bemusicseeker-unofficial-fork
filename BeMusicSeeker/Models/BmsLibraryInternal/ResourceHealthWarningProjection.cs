using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthWarningProjection
{
    internal static readonly ResourceHealthWarningProjection Empty = new ResourceHealthWarningProjection(0, Array.Empty<ChartWarning>(), false);

    internal ResourceHealthWarningProjection(int version, IReadOnlyList<ChartWarning> warnings, bool isIgnored)
    {
        Version = version;
        Warnings = warnings ?? Array.Empty<ChartWarning>();
        IsIgnored = isIgnored;
    }

    internal int Version { get; }

    internal IReadOnlyList<ChartWarning> Warnings { get; }

    internal bool HasIssues => Warnings.Count > 0;

    internal bool IsIgnored { get; }
}

internal sealed class ResourceHealthIndexSnapshot
{
    internal static readonly ResourceHealthIndexSnapshot Empty = new ResourceHealthIndexSnapshot(
        0,
        Array.Empty<BMSFile>(),
        Array.Empty<BMSFile>(),
        new Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection>(),
        0,
        0);

    private readonly Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection> projectionsByKey;

    private ResourceHealthIndexSnapshot(
        int version,
        IReadOnlyList<BMSFile> activeFiles,
        IReadOnlyList<BMSFile> ignoredFiles,
        Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection> projectionsByKey,
        int targetCount,
        long buildMs)
    {
        Version = version;
        ActiveFiles = activeFiles ?? Array.Empty<BMSFile>();
        IgnoredFiles = ignoredFiles ?? Array.Empty<BMSFile>();
        this.projectionsByKey = projectionsByKey ?? new Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection>();
        TargetCount = targetCount;
        BuildMs = buildMs;
    }

    internal int Version { get; }

    internal IReadOnlyList<BMSFile> ActiveFiles { get; }

    internal IReadOnlyList<BMSFile> IgnoredFiles { get; }

    internal int TargetCount { get; }

    internal int NeedFixCount => ActiveFiles.Count + IgnoredFiles.Count;

    internal int IgnoredCount => IgnoredFiles.Count;

    internal long BuildMs { get; }

    internal static ResourceHealthIndexSnapshot Build(
        IEnumerable<BMSFile> targets,
        BmsLibraryMaintenanceService maintenanceService,
        int version)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<BMSFile> activeFiles = new List<BMSFile>();
        List<BMSFile> ignoredFiles = new List<BMSFile>();
        Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection> projections = new Dictionary<ResourceHealthChartKey, ResourceHealthWarningProjection>();
        int targetCount = 0;
        foreach (BMSFile target in targets ?? Enumerable.Empty<BMSFile>())
        {
            if (target == null)
            {
                continue;
            }
            targetCount++;
            ResourceHealthChartKey key = ResourceHealthChartKey.FromBmsFile(target);
            if (!key.IsValid)
            {
                continue;
            }
            IReadOnlyList<ChartWarning> warnings = maintenanceService?.BuildResourceHealthWarnings(target) ?? Array.Empty<ChartWarning>();
            if (warnings.Count == 0)
            {
                continue;
            }
            BMSFileMaintenanceInfo maintenanceInfo = target.HasValidMaintenanceInfoSnapshot
                ? target.TryGetMaintenanceInfoWithoutCreating()
                : null;
            bool isIgnored = maintenanceInfo?.is_files_warning_ignored == true;
            ResourceHealthWarningProjection projection = new ResourceHealthWarningProjection(version, warnings, isIgnored);
            projections[key] = projection;
            if (isIgnored)
            {
                ignoredFiles.Add(target);
            }
            else
            {
                activeFiles.Add(target);
            }
        }
        stopwatch.Stop();
        return new ResourceHealthIndexSnapshot(version, activeFiles, ignoredFiles, projections, targetCount, stopwatch.ElapsedMilliseconds);
    }

    internal ResourceHealthWarningProjection GetProjection(BMSFile file)
    {
        ResourceHealthChartKey key = ResourceHealthChartKey.FromBmsFile(file);
        return GetProjection(key);
    }

    internal ResourceHealthWarningProjection GetProjection(LR2SongDBExtended.bmson_song song)
    {
        ResourceHealthChartKey key = ResourceHealthChartKey.FromBmsonSong(song);
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

        internal static ResourceHealthChartKey FromBmsFile(BMSFile file)
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
