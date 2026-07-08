using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IMaintenanceHydrationApplyHost
{
    IDisposable EnterOwnedStorageWriteLock();

    OwnedChartStorageOwnerView CreateOwnedChartStorageOwnerView();

    IDisposable BeginResourceHealthInputMutation();

    ResourceMaintenanceTargetSet CreateFullOwnedResourceMaintenanceTargetSet(string reason);

    int DeleteStaleMaintenanceRows(IEnumerable<string> staleMaintenancePaths);

    void ForceInvalidateResourceHealthIndex(string reason);

    void DispatchMaintenanceHydrationResult(
        MaintenanceTableHydrationResult result,
        ResourceMaintenanceTargetSet resourceHealthTargets);
}
