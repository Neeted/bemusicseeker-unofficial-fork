namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthIndexMutationDispatcher(IResourceHealthIndexMutationDispatchHost host)
{
    internal ResourceHealthIndexDispatchResult Dispatch(
        ResourceHealthIndexMutation mutation,
        string reason)
    {
        var result = new ResourceHealthIndexDispatchResult
        {
            Snapshot = host.GetPublishedResourceHealthIndexSnapshotOrEmpty()
        };
        if (mutation == null || !mutation.HasChanges)
        {
            return result;
        }
        if (mutation.Invalidate)
        {
            host.InvalidateResourceHealthIndex(reason);
            result.Snapshot = host.GetPublishedResourceHealthIndexSnapshotOrEmpty();
            return result;
        }
        if (mutation.Defer)
        {
            result.Deferred = true;
            result.Snapshot = host.GetPublishedResourceHealthIndexSnapshotOrEmpty();
            host.LogResourceHealthIndexDeferred(reason, result.Snapshot, mutation.UpdateTargetCount);
            return result;
        }
        if (!mutation.RebuildFull
            && mutation.HasDeltaTargets
            && host.TryApplyResourceHealthIndexDelta(
                reason,
                mutation.UpdatedTargets,
                mutation.RemovedTargets,
                mutation.DeltaBaseResourceHealthInputVersion,
                mutation.DeltaTargetResourceHealthInputVersion,
                out ResourceHealthIndexSnapshot deltaSnapshot))
        {
            result.Snapshot = deltaSnapshot;
            result.DeltaApplied = true;
            result.IndexMs = deltaSnapshot.BuildMs;
            return result;
        }
        if (!mutation.RebuildFull && mutation.HasDeltaTargets && mutation.InvalidateIfDeltaFails)
        {
            host.InvalidateResourceHealthIndex(reason);
            result.Snapshot = host.GetPublishedResourceHealthIndexSnapshotOrEmpty();
            return result;
        }
        if (mutation.RebuildFull || mutation.HasDeltaTargets)
        {
            result.Snapshot = host.RebuildResourceHealthIndexSnapshot(
                reason,
                mutation.FullOwnedTargetSet,
                out bool staleFullOwnedTarget);
            if (staleFullOwnedTarget)
            {
                return result;
            }
            result.FullRebuilt = true;
            result.IndexMs = result.Snapshot.BuildMs;
        }
        return result;
    }
}
