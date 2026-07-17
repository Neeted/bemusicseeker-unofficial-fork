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
        UpdatedTargets = updatedTargets ?? [];
        RemovedTargets = removedTargets ?? [];
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

    internal static ResourceHealthIndexMutationFacts From(ResourceHealthIndexMutation mutation)
    {
        if (mutation == null)
        {
            return new ResourceHealthIndexMutationFacts([], [], default, null, null, false, false, false, false);
        }
        return new ResourceHealthIndexMutationFacts(
            [.. (mutation.UpdatedTargets ?? []).Where(chart => chart != null)],
            [.. (mutation.RemovedTargets ?? []).Where(chart => chart != null)],
            mutation.FullOwnedTargetSet,
            mutation.DeltaBaseResourceHealthInputVersion,
            mutation.DeltaTargetResourceHealthInputVersion,
            mutation.Invalidate,
            mutation.RebuildFull,
            mutation.Defer,
            mutation.InvalidateIfDeltaFails);
    }
}
