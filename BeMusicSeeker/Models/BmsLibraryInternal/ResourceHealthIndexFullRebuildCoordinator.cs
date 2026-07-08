using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthIndexFullRebuildCoordinator(IResourceHealthIndexFullRebuildHost host)
{
    internal ResourceHealthIndexFullRebuildResult Rebuild(
        string reason,
        ResourceMaintenanceTargetSet fullOwnedTargetSet)
    {
        ResourceMaintenanceTargetSet targetSet = ResolveTargetSet(reason, fullOwnedTargetSet);
        List<ChartFile> targets = targetSet.Charts;
        ResourceHealthIndexSnapshot snapshot = host.BuildResourceHealthIndexSnapshot(targets);
        if (targetSet.HasFullOwnedVersion)
        {
            ResourceHealthIndexFullOwnedPublishResult publishResult = host.PublishFullOwnedSnapshot(
                reason,
                targetSet,
                snapshot,
                targets.Count);
            if (publishResult.StaleFullOwnedTarget)
            {
                return new ResourceHealthIndexFullRebuildResult(publishResult.Snapshot, staleFullOwnedTarget: true);
            }
        }
        else
        {
            host.PublishSnapshot(snapshot);
        }
        host.LogResourceHealthIndexBuild(reason, snapshot);
        return new ResourceHealthIndexFullRebuildResult(snapshot, staleFullOwnedTarget: false);
    }

    private ResourceMaintenanceTargetSet ResolveTargetSet(
        string reason,
        ResourceMaintenanceTargetSet requestedTargetSet)
    {
        ResourceMaintenanceTargetSet targetSet = requestedTargetSet;
        if (targetSet.IsSpecified && !targetSet.HasFullOwnedVersion)
        {
            targetSet = default;
        }
        return targetSet.IsSpecified
            ? targetSet
            : host.CreateFullOwnedResourceMaintenanceTargetSet(reason);
    }
}
