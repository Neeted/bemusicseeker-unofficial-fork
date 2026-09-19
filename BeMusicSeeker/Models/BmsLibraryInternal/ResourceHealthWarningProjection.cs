using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

/// <summary>
/// resource-healthの対象 membershipとwarning projectionを世代単位で保持します。
/// 差分更新ではidentity mapと順序付きwarning sequenceの変更箇所だけを共有します。
/// </summary>
internal sealed class ResourceHealthIndexSnapshot
{
    internal static readonly ResourceHealthIndexSnapshot Empty = new(
        0,
        ResourceHealthWarningSequence.Empty,
        ResourceHealthWarningSequence.Empty,
        ImmutableDictionary<ResourceHealthChartIdentity, ResourceHealthTargetEntry>.Empty,
        0);

    private readonly ImmutableDictionary<ResourceHealthChartIdentity, ResourceHealthTargetEntry> entriesByIdentity;

    private readonly ResourceHealthWarningSequence activeTargetSequence;

    private readonly ResourceHealthWarningSequence ignoredTargetSequence;

    private readonly ResourceHealthWarningSequenceView activeTargetsView;

    private readonly ResourceHealthWarningSequenceView ignoredTargetsView;

    private ResourceHealthIndexSnapshot(
        int version,
        ResourceHealthWarningSequence activeTargetSequence,
        ResourceHealthWarningSequence ignoredTargetSequence,
        ImmutableDictionary<ResourceHealthChartIdentity, ResourceHealthTargetEntry> entriesByIdentity,
        long buildMs,
        Action<string> storeWorkObserver = null)
    {
        Version = version;
        this.activeTargetSequence = activeTargetSequence ?? throw new ArgumentNullException(nameof(activeTargetSequence));
        this.ignoredTargetSequence = ignoredTargetSequence ?? throw new ArgumentNullException(nameof(ignoredTargetSequence));
        this.entriesByIdentity = entriesByIdentity ?? throw new ArgumentNullException(nameof(entriesByIdentity));
        BuildMs = buildMs;
        StoreWorkObserver = storeWorkObserver;
        activeTargetsView = new ResourceHealthWarningSequenceView(activeTargetSequence, () => StoreWorkObserver);
        ignoredTargetsView = new ResourceHealthWarningSequenceView(ignoredTargetSequence, () => StoreWorkObserver);
    }

    /// <summary>このsnapshotを公開したmaintenance versionを返します。</summary>
    internal int Version { get; }

    /// <summary>active warning対象を既存順で返すread-only viewを返します。</summary>
    internal IReadOnlyList<ChartFile> ActiveTargets => activeTargetsView;

    /// <summary>ignored warning対象を既存順で返すread-only viewを返します。</summary>
    internal IReadOnlyList<ChartFile> IgnoredTargets => ignoredTargetsView;

    /// <summary>warningの有無にかかわらず保持するexact identityの対象数を返します。</summary>
    internal int TargetCount => entriesByIdentity.Count;

    /// <summary>activeとignoredを合わせたwarning対象数を返します。</summary>
    internal int NeedFixCount => activeTargetSequence.Count + ignoredTargetSequence.Count;

    /// <summary>ignored warning対象数を返します。</summary>
    internal int IgnoredCount => ignoredTargetSequence.Count;

    /// <summary>cold buildに要した計測値を返します。</summary>
    internal long BuildMs { get; }

    /// <summary>
    /// 実際に行った immutable store の lookup、ordinal 比較、列挙を、instance 単位で観測します。
    /// production では未設定のままにし、性能契約の検証だけで使用します。
    /// callback は snapshot へ再入せず、例外を投げないものとします。
    /// </summary>
    internal Action<string> StoreWorkObserver { get; set; }

    /// <summary>
    /// 全対象から初回resource-health snapshotを構築します。これはdeltaで代替できないcold full routeです。
    /// </summary>
    /// <param name="targets">membershipへ登録するchart対象列挙。</param>
    /// <param name="maintenanceService">warning factsを構築するmaintenance service。</param>
    /// <param name="version">構築したsnapshotへ付与するversion。</param>
    /// <returns>入力identity順を保持した新しいsnapshot。</returns>
    internal static ResourceHealthIndexSnapshot Build(
        IEnumerable<ChartFile> targets,
        BmsLibraryMaintenanceService maintenanceService,
        int version)
    {
        var stopwatch = Stopwatch.StartNew();
        List<ResourceHealthTargetEntry> activeTargets = [];
        List<ResourceHealthTargetEntry> ignoredTargets = [];
        ImmutableDictionary<ResourceHealthChartIdentity, ResourceHealthTargetEntry>.Builder entries =
            ImmutableDictionary.CreateBuilder<ResourceHealthChartIdentity, ResourceHealthTargetEntry>();
        long activeOrdinal = 0;
        long ignoredOrdinal = 0;
        foreach (ChartFile target in targets ?? [])
        {
            var identity = ResourceHealthChartIdentity.FromChartFile(target);
            if (!identity.IsValid || entries.ContainsKey(identity))
            {
                continue;
            }
            IReadOnlyList<ChartWarning> warnings = maintenanceService?.BuildResourceHealthWarnings(target) ?? [];
            bool isIgnored = warnings.Count > 0
                && BmsLibraryMaintenanceService.AreResourceHealthWarningsIgnored(target);
            ResourceHealthWarningProjection projection = warnings.Count == 0
                ? null
                : new ResourceHealthWarningProjection(version, warnings, isIgnored);
            long? warningOrdinal = projection == null
                ? null
                : isIgnored
                    ? ignoredOrdinal++
                    : activeOrdinal++;
            ResourceHealthTargetEntry entry = new(target, projection, warningOrdinal);
            entries.Add(identity, entry);
            if (projection != null && isIgnored)
            {
                ignoredTargets.Add(entry);
            }
            else if (projection != null)
            {
                activeTargets.Add(entry);
            }
        }
        stopwatch.Stop();
        return new ResourceHealthIndexSnapshot(
            version,
            ResourceHealthWarningSequence.Create(activeTargets, activeOrdinal),
            ResourceHealthWarningSequence.Create(ignoredTargets, ignoredOrdinal),
            entries.ToImmutable(),
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// 更新・削除対象だけをidentity mapとwarning sequenceへ反映した新しいsnapshotを返します。
    /// 削除を先に適用し、旧snapshotのimmutable stateを変更しません。
    /// </summary>
    /// <param name="updatedTargets">再評価するchart対象列挙。</param>
    /// <param name="removedTargets">membershipから削除するchart対象列挙。</param>
    /// <param name="maintenanceService">warning factsを構築するmaintenance service。</param>
    /// <param name="version">更新後snapshotへ付与するversion。</param>
    /// <returns>変更箇所だけを共有した新しいsnapshot。</returns>
    internal ResourceHealthIndexSnapshot ApplyDelta(
        IEnumerable<ChartFile> updatedTargets,
        IEnumerable<ChartFile> removedTargets,
        BmsLibraryMaintenanceService maintenanceService,
        int version)
    {
        var stopwatch = Stopwatch.StartNew();
        List<ChartFile> uniqueRemovedTargets = DistinctValidTargets(removedTargets);
        List<ChartFile> uniqueUpdatedTargets = DistinctValidTargets(updatedTargets);
        HashSet<ResourceHealthChartIdentity> removedIdentities =
            [.. uniqueRemovedTargets.Select(ResourceHealthChartIdentity.FromChartFile)];
        uniqueUpdatedTargets =
            [.. uniqueUpdatedTargets.Where(target =>
            {
                var identity = ResourceHealthChartIdentity.FromChartFile(target);
                return !removedIdentities.Contains(identity);
            })];

        ImmutableDictionary<ResourceHealthChartIdentity, ResourceHealthTargetEntry> nextEntries = entriesByIdentity;
        ResourceHealthWarningSequence nextActiveTargets = activeTargetSequence;
        ResourceHealthWarningSequence nextIgnoredTargets = ignoredTargetSequence;
        foreach (ChartFile removedTarget in uniqueRemovedTargets)
        {
            var identity = ResourceHealthChartIdentity.FromChartFile(removedTarget);
            if (!TryGetEntry(nextEntries, identity, out ResourceHealthTargetEntry existingEntry))
            {
                continue;
            }
            nextEntries = nextEntries.Remove(identity);
            RemoveFromWarningSequence(
                existingEntry,
                ref nextActiveTargets,
                ref nextIgnoredTargets,
                StoreWorkObserver);
        }
        foreach (ChartFile updatedTarget in uniqueUpdatedTargets)
        {
            var identity = ResourceHealthChartIdentity.FromChartFile(updatedTarget);
            if (TryGetEntry(nextEntries, identity, out ResourceHealthTargetEntry existingEntry))
            {
                RemoveFromWarningSequence(
                    existingEntry,
                    ref nextActiveTargets,
                    ref nextIgnoredTargets,
                    StoreWorkObserver);
            }
            IReadOnlyList<ChartWarning> warnings = maintenanceService?.BuildResourceHealthWarnings(updatedTarget) ?? [];
            bool isIgnored = warnings.Count > 0
                && BmsLibraryMaintenanceService.AreResourceHealthWarningsIgnored(updatedTarget);
            ResourceHealthWarningProjection projection = warnings.Count == 0
                ? null
                : new ResourceHealthWarningProjection(version, warnings, isIgnored);
            long? warningOrdinal = projection == null
                ? null
                : projection.IsIgnored
                    ? nextIgnoredTargets.NextOrdinal
                    : nextActiveTargets.NextOrdinal;
            ResourceHealthTargetEntry nextEntry = new(updatedTarget, projection, warningOrdinal);
            nextEntries = nextEntries.SetItem(identity, nextEntry);
            if (projection != null && projection.IsIgnored)
            {
                nextIgnoredTargets = nextIgnoredTargets.Append(nextEntry, StoreWorkObserver);
            }
            else if (projection != null)
            {
                nextActiveTargets = nextActiveTargets.Append(nextEntry, StoreWorkObserver);
            }
        }
        stopwatch.Stop();
        return new ResourceHealthIndexSnapshot(
            version,
            nextActiveTargets,
            nextIgnoredTargets,
            nextEntries,
            stopwatch.ElapsedMilliseconds,
            StoreWorkObserver);
    }

    private static List<ChartFile> DistinctValidTargets(IEnumerable<ChartFile> targets)
    {
        List<ChartFile> result = [];
        HashSet<ResourceHealthChartIdentity> identities = [];
        foreach (ChartFile target in targets ?? [])
        {
            var identity = ResourceHealthChartIdentity.FromChartFile(target);
            if (identity.IsValid && identities.Add(identity))
            {
                result.Add(target);
            }
        }
        return result;
    }

    private static void RemoveFromWarningSequence(
        ResourceHealthTargetEntry entry,
        ref ResourceHealthWarningSequence activeTargets,
        ref ResourceHealthWarningSequence ignoredTargets,
        Action<string> storeWorkObserver)
    {
        if (entry?.Projection == null || !entry.WarningOrdinal.HasValue)
        {
            return;
        }
        if (entry.Projection.IsIgnored)
        {
            ignoredTargets = ignoredTargets.Remove(entry.WarningOrdinal.Value, storeWorkObserver);
        }
        else
        {
            activeTargets = activeTargets.Remove(entry.WarningOrdinal.Value, storeWorkObserver);
        }
    }

    private bool TryGetEntry(
        ImmutableDictionary<ResourceHealthChartIdentity, ResourceHealthTargetEntry> entries,
        ResourceHealthChartIdentity identity,
        out ResourceHealthTargetEntry entry)
    {
        StoreWorkObserver?.Invoke("entry_lookup");
        return entries.TryGetValue(identity, out entry);
    }

    private sealed class ResourceHealthWarningSequence
    {
        internal static readonly ResourceHealthWarningSequence Empty = new(
            ImmutableList<ResourceHealthTargetEntry>.Empty,
            0);

        private readonly ImmutableList<ResourceHealthTargetEntry> entries;

        private readonly long nextOrdinal;

        private ResourceHealthWarningSequence(
            ImmutableList<ResourceHealthTargetEntry> entries,
            long nextOrdinal)
        {
            this.entries = entries ?? throw new ArgumentNullException(nameof(entries));
            this.nextOrdinal = nextOrdinal;
        }

        internal int Count => entries.Count;

        internal long NextOrdinal => nextOrdinal;

        internal static ResourceHealthWarningSequence Create(
            IReadOnlyList<ResourceHealthTargetEntry> source,
            long nextOrdinal)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            return new ResourceHealthWarningSequence(
                ImmutableList.CreateRange(source),
                nextOrdinal);
        }

        internal ResourceHealthTargetEntry EntryAt(int index)
        {
            return entries[index];
        }

        internal ResourceHealthWarningSequence Append(
            ResourceHealthTargetEntry entry,
            Action<string> storeWorkObserver)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            if (entry.WarningOrdinal != nextOrdinal)
            {
                throw new InvalidOperationException("Resource-health warning sequence ordinal is inconsistent.");
            }
            storeWorkObserver?.Invoke("warning_sequence_append");
            return new ResourceHealthWarningSequence(
                entries.Add(entry),
                checked(nextOrdinal + 1));
        }

        internal ResourceHealthWarningSequence Remove(
            long ordinal,
            Action<string> storeWorkObserver)
        {
            int position = FindOrdinalPosition(ordinal, storeWorkObserver);
            if (position < 0)
            {
                throw new InvalidOperationException("Resource-health warning sequence index is inconsistent.");
            }
            storeWorkObserver?.Invoke("warning_sequence_remove");
            return new ResourceHealthWarningSequence(
                entries.RemoveAt(position),
                nextOrdinal);
        }

        internal IEnumerable<ResourceHealthTargetEntry> Enumerate(Action<string> storeWorkObserver)
        {
            storeWorkObserver?.Invoke("warning_sequence_enumeration");
            foreach (ResourceHealthTargetEntry entry in entries)
            {
                storeWorkObserver?.Invoke("warning_sequence_entry_visited");
                yield return entry;
            }
        }

        private int FindOrdinalPosition(long ordinal, Action<string> storeWorkObserver)
        {
            int low = 0;
            int high = entries.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                long middleOrdinal = entries[middle].WarningOrdinal.Value;
                storeWorkObserver?.Invoke("warning_sequence_index_comparison");
                if (middleOrdinal == ordinal)
                {
                    return middle;
                }
                if (middleOrdinal < ordinal)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return -1;
        }
    }

    private sealed class ResourceHealthWarningSequenceView : IReadOnlyList<ChartFile>
    {
        private readonly ResourceHealthWarningSequence sequence;

        private readonly Func<Action<string>> observerProvider;

        internal ResourceHealthWarningSequenceView(
            ResourceHealthWarningSequence sequence,
            Func<Action<string>> observerProvider)
        {
            this.sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
            this.observerProvider = observerProvider;
        }

        public int Count => sequence.Count;

        public ChartFile this[int index] => sequence.EntryAt(index).Target;

        public IEnumerator<ChartFile> GetEnumerator()
        {
            Action<string> storeWorkObserver = observerProvider?.Invoke();
            foreach (ResourceHealthTargetEntry entry in sequence.Enumerate(storeWorkObserver))
            {
                yield return entry.Target;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    /// <summary>chartのexact identityとhashから保持中のwarning projectionを返します。</summary>
    /// <param name="chart">検索対象chart。</param>
    /// <returns>一致するprojection。対象なし、healthy、hash不一致は空projection。</returns>
    internal ResourceHealthWarningProjection GetProjection(ChartFile chart)
    {
        var key = ResourceHealthChartKey.FromChartFile(chart);
        return GetProjection(key);
    }

    /// <summary>kindとexact pathでentryをlookupし、hashをcase-insensitiveに照合してprojectionを返します。</summary>
    /// <param name="kind">chart kind。</param>
    /// <param name="path">大文字小文字を保持して照合するexact path。</param>
    /// <param name="md5">照合するhash。</param>
    /// <returns>一致するprojection。対象なし、healthy、hash不一致は空projection。</returns>
    internal ResourceHealthWarningProjection GetProjection(ChartFileKind kind, string path, string md5)
    {
        return GetProjection(ResourceHealthChartKey.FromChartIdentity(kind, path, md5));
    }

    private ResourceHealthWarningProjection GetProjection(ResourceHealthChartKey key)
    {
        if (!key.IsValid)
        {
            return ResourceHealthWarningProjection.Empty;
        }
        if (!TryGetEntry(entriesByIdentity, key.Identity, out ResourceHealthTargetEntry entry)
            || !string.Equals(entry.Hash, key.Hash, StringComparison.OrdinalIgnoreCase))
        {
            return ResourceHealthWarningProjection.Empty;
        }
        return entry.Projection ?? ResourceHealthWarningProjection.Empty;
    }

    private sealed class ResourceHealthTargetEntry
    {
        internal ResourceHealthTargetEntry(
            ChartFile target,
            ResourceHealthWarningProjection projection,
            long? warningOrdinal)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Hash = target.Md5 ?? string.Empty;
            Projection = projection;
            WarningOrdinal = warningOrdinal;
        }

        internal ChartFile Target { get; }

        internal string Hash { get; }

        internal ResourceHealthWarningProjection Projection { get; }

        internal long? WarningOrdinal { get; }
    }

    private readonly struct ResourceHealthChartIdentity : IEquatable<ResourceHealthChartIdentity>
    {
        private readonly ChartFileKind kind;
        private readonly string path;

        internal ResourceHealthChartIdentity(ChartFileKind kind, string path)
        {
            this.kind = kind;
            this.path = path ?? string.Empty;
        }

        internal bool IsValid => !string.IsNullOrWhiteSpace(path);

        internal static ResourceHealthChartIdentity FromChartFile(ChartFile chart)
        {
            if (chart == null)
            {
                return default;
            }
            return new ResourceHealthChartIdentity(chart.Kind, chart.Path);
        }

        public bool Equals(ResourceHealthChartIdentity other)
        {
            return kind == other.kind
                && string.Equals(path, other.path, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ResourceHealthChartIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (int)kind * 397 ^ StringComparer.Ordinal.GetHashCode(path ?? string.Empty);
            }
        }
    }

    private readonly struct ResourceHealthChartKey
    {
        private ResourceHealthChartKey(ResourceHealthChartIdentity identity, string hash)
        {
            Identity = identity;
            Hash = hash ?? string.Empty;
        }

        internal ResourceHealthChartIdentity Identity { get; }

        internal string Hash { get; }

        internal bool IsValid => Identity.IsValid;

        internal static ResourceHealthChartKey FromChartFile(ChartFile chart)
        {
            return chart == null
                ? default
                : new ResourceHealthChartKey(
                    ResourceHealthChartIdentity.FromChartFile(chart),
                    chart.Md5);
        }

        internal static ResourceHealthChartKey FromChartIdentity(ChartFileKind kind, string path, string md5)
        {
            return new ResourceHealthChartKey(
                new ResourceHealthChartIdentity(kind, path),
                md5);
        }

    }
}
