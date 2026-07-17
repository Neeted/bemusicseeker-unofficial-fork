using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IMaintenanceHydrationApplyHost
{
    IDisposable EnterOwnedStorageWriteLock();

    OwnedChartStorageOwnerView CreateOwnedChartStorageOwnerView();

    ResourceMaintenanceTargetSet CreateFullOwnedResourceMaintenanceTargetSet(string reason);

    int DeleteStaleMaintenanceRows(IEnumerable<string> staleMaintenancePaths);

    void DispatchMaintenanceHydrationResult(
        MaintenanceTableHydrationResult result,
        ResourceMaintenanceTargetSet resourceHealthTargets);
}
