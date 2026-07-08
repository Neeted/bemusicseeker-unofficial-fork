using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ResourceHealthIndexMutationPlanner
{
    internal static ResourceHealthIndexMutation BuildMaintenanceMutation(
        ResourceMaintenanceTargetSet maintenanceTargets,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        bool resourceHealthIndexCurrent,
        bool workflowHasUpdates,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null)
    {
        var mutation = new ResourceHealthIndexMutation();
        List<ChartFile> maintenanceTargetCharts = maintenanceTargets.Charts;
        bool forceResourceHealthDelta = resourceHealthIndexUpdateMode == ResourceHealthIndexUpdateMode.DeltaOnUpdates && resourceHealthIndexCurrent;
        bool shouldUpdateIndex = workflowHasUpdates || !resourceHealthIndexCurrent || forceResourceHealthDelta;
        if (!shouldUpdateIndex)
        {
            return mutation;
        }
        if (resourceHealthIndexUpdateMode == ResourceHealthIndexUpdateMode.DeferOnUpdates)
        {
            mutation.Defer = true;
            mutation.UpdatedTargets.AddRange(maintenanceTargetCharts ?? []);
            return mutation;
        }
        if (resourceHealthIndexUpdateMode == ResourceHealthIndexUpdateMode.DeltaOnUpdates)
        {
            if (resourceHealthIndexCurrent)
            {
                mutation.UpdatedTargets.AddRange(maintenanceTargetCharts ?? []);
                mutation.DeltaBaseResourceHealthInputVersion = deltaBaseResourceHealthInputVersion;
                mutation.DeltaTargetResourceHealthInputVersion = deltaTargetResourceHealthInputVersion;
                mutation.InvalidateIfDeltaFails = true;
            }
            else
            {
                mutation.Invalidate = true;
            }
            return mutation;
        }
        mutation.RebuildFull = true;
        if (maintenanceTargets.HasFullOwnedVersion)
        {
            mutation.FullOwnedTargetSet = maintenanceTargets;
        }
        return mutation;
    }

    internal static ResourceHealthIndexMutation BuildMaintenanceHydrationFullRebuildMutation(
        ResourceMaintenanceTargetSet fullOwnedTargets)
    {
        return new ResourceHealthIndexMutation
        {
            RebuildFull = true,
            FullOwnedTargetSet = fullOwnedTargets
        };
    }
}
