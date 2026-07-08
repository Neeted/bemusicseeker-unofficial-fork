using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IResourceHealthIndexMutationDispatchHost
{
    ResourceHealthIndexSnapshot GetPublishedResourceHealthIndexSnapshotOrEmpty();

    void InvalidateResourceHealthIndex(string reason);

    void LogResourceHealthIndexDeferred(
        string reason,
        ResourceHealthIndexSnapshot snapshot,
        int updateTargetCount);

    bool TryApplyResourceHealthIndexDelta(
        string reason,
        IEnumerable<ChartFile> updatedTargets,
        IEnumerable<ChartFile> removedTargets,
        int? deltaBaseResourceHealthInputVersion,
        int? deltaTargetResourceHealthInputVersion,
        out ResourceHealthIndexSnapshot snapshot);

    ResourceHealthIndexSnapshot RebuildResourceHealthIndexSnapshot(
        string reason,
        ResourceMaintenanceTargetSet fullOwnedTargetSet,
        out bool staleFullOwnedTarget);
}
