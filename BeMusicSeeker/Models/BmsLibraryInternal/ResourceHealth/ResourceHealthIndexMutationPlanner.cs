using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ResourceHealthIndexMutationPlanner
{
    /// <summary>保守の保存値・警告投影に変更がある対象を反映し、現在索引がなければ既存の復元方針を適用します。</summary>
    internal static ResourceHealthIndexMutation BuildMaintenanceMutation(
        ResourceMaintenanceTargetSet maintenanceTargets,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        bool resourceHealthIndexCurrent,
        bool workflowHasUpdates,
        int? deltaBaseResourceHealthInputVersion = null,
        int? deltaTargetResourceHealthInputVersion = null)
    {
        var mutation = new ResourceHealthIndexMutation();
        IReadOnlyList<ChartFile> maintenanceTargetCharts = maintenanceTargets.Charts;
        // 警告詳細だけの差も保守の更新へ含めるため、同値の再検査で索引と表示通知を再発させません。
        bool shouldUpdateIndex = workflowHasUpdates || !resourceHealthIndexCurrent;
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
