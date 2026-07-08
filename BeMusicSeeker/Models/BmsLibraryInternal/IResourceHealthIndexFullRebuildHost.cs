using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IResourceHealthIndexFullRebuildHost
{
    ResourceMaintenanceTargetSet CreateFullOwnedResourceMaintenanceTargetSet(string reason);

    ResourceHealthIndexSnapshot BuildResourceHealthIndexSnapshot(IEnumerable<ChartFile> targets);

    ResourceHealthIndexFullOwnedPublishResult PublishFullOwnedSnapshot(
        string reason,
        ResourceMaintenanceTargetSet targetSet,
        ResourceHealthIndexSnapshot snapshot,
        int targetCount);

    void PublishSnapshot(ResourceHealthIndexSnapshot snapshot);

    void LogResourceHealthIndexBuild(string reason, ResourceHealthIndexSnapshot snapshot);
}
