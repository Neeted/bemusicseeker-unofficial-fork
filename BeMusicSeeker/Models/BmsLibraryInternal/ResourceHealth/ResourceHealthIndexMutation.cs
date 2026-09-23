using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthIndexMutation
{
    internal List<ChartFile> UpdatedTargets { get; } = [];

    internal List<ChartFile> RemovedTargets { get; } = [];

    internal ResourceMaintenanceTargetSet FullOwnedTargetSet { get; set; }

    internal int? DeltaBaseResourceHealthInputVersion { get; set; }

    internal int? DeltaTargetResourceHealthInputVersion { get; set; }

    internal bool Invalidate { get; set; }

    internal bool RebuildFull { get; set; }

    internal bool Defer { get; set; }

    internal bool InvalidateIfDeltaFails { get; set; }

    internal int UpdateTargetCount => UpdatedTargets.Count + RemovedTargets.Count;

    internal bool HasDeltaTargets => UpdatedTargets.Count > 0 || RemovedTargets.Count > 0;

    internal bool HasChanges => Invalidate || RebuildFull || Defer || HasDeltaTargets;

    internal ResourceHealthIndexMutationFacts ToFacts()
    {
        return ResourceHealthIndexMutationFacts.From(this);
    }
}

internal sealed class ResourceHealthIndexMutationFacts
{
    private ResourceHealthIndexMutationFacts(
        IReadOnlyList<ChartFile> updatedTargets,
        IReadOnlyList<ChartFile> removedTargets,
        ResourceMaintenanceTargetSet fullOwnedTargetSet,
        int? deltaBaseResourceHealthInputVersion,
        int? deltaTargetResourceHealthInputVersion,
        bool invalidate,
        bool rebuildFull,
        bool defer,
        bool invalidateIfDeltaFails)
    {
        UpdatedTargets = Array.AsReadOnly([.. (updatedTargets ?? []).Where(chart => chart != null)]);
        RemovedTargets = Array.AsReadOnly([.. (removedTargets ?? []).Where(chart => chart != null)]);
        FullOwnedTargetSet = fullOwnedTargetSet;
        DeltaBaseResourceHealthInputVersion = deltaBaseResourceHealthInputVersion;
        DeltaTargetResourceHealthInputVersion = deltaTargetResourceHealthInputVersion;
        Invalidate = invalidate;
        RebuildFull = rebuildFull;
        Defer = defer;
        InvalidateIfDeltaFails = invalidateIfDeltaFails;
    }

    internal IReadOnlyList<ChartFile> UpdatedTargets { get; }

    internal IReadOnlyList<ChartFile> RemovedTargets { get; }

    internal ResourceMaintenanceTargetSet FullOwnedTargetSet { get; }

    internal int? DeltaBaseResourceHealthInputVersion { get; }

    internal int? DeltaTargetResourceHealthInputVersion { get; }

    internal bool Invalidate { get; }

    internal bool RebuildFull { get; }

    internal bool Defer { get; }

    internal bool InvalidateIfDeltaFails { get; }

    internal int UpdateTargetCount => UpdatedTargets.Count + RemovedTargets.Count;

    internal bool HasDeltaTargets => UpdatedTargets.Count > 0 || RemovedTargets.Count > 0;

    internal bool HasChanges => Invalidate || RebuildFull || Defer || HasDeltaTargets;

    internal ResourceHealthIndexMutation ToMutation()
    {
        var mutation = new ResourceHealthIndexMutation();
        mutation.UpdatedTargets.AddRange(UpdatedTargets ?? []);
        mutation.RemovedTargets.AddRange(RemovedTargets ?? []);
        mutation.FullOwnedTargetSet = FullOwnedTargetSet;
        mutation.DeltaBaseResourceHealthInputVersion = DeltaBaseResourceHealthInputVersion;
        mutation.DeltaTargetResourceHealthInputVersion = DeltaTargetResourceHealthInputVersion;
        mutation.Invalidate = Invalidate;
        mutation.RebuildFull = RebuildFull;
        mutation.Defer = Defer;
        mutation.InvalidateIfDeltaFails = InvalidateIfDeltaFails;
        return mutation;
    }

    internal static ResourceHealthIndexMutationFacts From(ResourceHealthIndexMutation mutation)
    {
        if (mutation == null)
        {
            return new ResourceHealthIndexMutationFacts([], [], default, null, null, false, false, false, false);
        }
        return new ResourceHealthIndexMutationFacts(
            SnapshotCharts(mutation.UpdatedTargets),
            SnapshotCharts(mutation.RemovedTargets),
            SnapshotTargetSet(mutation.FullOwnedTargetSet),
            mutation.DeltaBaseResourceHealthInputVersion,
            mutation.DeltaTargetResourceHealthInputVersion,
            mutation.Invalidate,
            mutation.RebuildFull,
            mutation.Defer,
            mutation.InvalidateIfDeltaFails);
    }

    private static IReadOnlyList<ChartFile> SnapshotCharts(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? [])
            .Where(chart => chart != null)
            .Select(ChartFileProjection.ToImmutableSnapshot)
            .Where(chart => chart != null)];
    }

    /// <summary>
    /// full 入力の版を保持し、storage owner の後続変更を読まない不変入力へ切り離す。
    /// receipt 作成時と、実 full rebuild に必要な遅延取得時で同じ境界を使う。
    /// </summary>
    internal static ResourceMaintenanceTargetSet SnapshotTargetSet(ResourceMaintenanceTargetSet targetSet)
    {
        if (!targetSet.IsSpecified)
        {
            return default;
        }

        IReadOnlyList<ChartFile> charts = SnapshotCharts(targetSet.Charts);
        if (!targetSet.IsFullOwned || !targetSet.HasFullOwnedVersion)
        {
            return ResourceMaintenanceTargetSet.ForSubset(charts);
        }

        return ResourceMaintenanceTargetSet.ForFullOwned(
            charts,
            targetSet.StorageRowsVersion!.Value,
            targetSet.OwnedCollectionVersion!.Value,
            targetSet.ResourceHealthInputVersion!.Value);
    }
}
