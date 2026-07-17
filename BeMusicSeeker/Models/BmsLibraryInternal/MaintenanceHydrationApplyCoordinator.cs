using System;
using System.Diagnostics;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class MaintenanceHydrationApplyCoordinator
{
    private readonly IMaintenanceHydrationApplyHost host;

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    internal MaintenanceHydrationApplyCoordinator(
        IMaintenanceHydrationApplyHost host,
        ResourceHealthIndexOwner resourceHealthOwner)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.resourceHealthOwner = resourceHealthOwner ?? throw new ArgumentNullException(nameof(resourceHealthOwner));
    }

    internal void Apply(MaintenanceTableHydrationResult result)
    {
        if (result == null)
        {
            return;
        }

        var applyStopwatch = Stopwatch.StartNew();
        ResourceMaintenanceTargetSet resourceHealthTargets = default;
        using (host.EnterOwnedStorageWriteLock())
        {
            OwnedChartStorageOwnerView ownerView = host.CreateOwnedChartStorageOwnerView();
            var attachStopwatch = Stopwatch.StartNew();
            using (resourceHealthOwner.BeginInputMutation())
            {
                MaintenanceHydrationOwnerAttachService.AttachMaintenanceSnapshots(ownerView, result);
            }
            attachStopwatch.Stop();
            result.MaintenanceAttachMs = attachStopwatch.ElapsedMilliseconds;
            resourceHealthTargets = host.CreateFullOwnedResourceMaintenanceTargetSet("maintenance_hydration");
            MaintenanceHydrationOwnerAttachService.CaptureOwnerPathAndStaleMaintenancePaths(ownerView, result);
            applyStopwatch.Stop();
            result.MaintenanceApplyMs = applyStopwatch.ElapsedMilliseconds;
            var cleanupStopwatch = Stopwatch.StartNew();
            if (result.StaleMaintenancePaths.Count > 0)
            {
                try
                {
                    result.CleanupDeletedCount = host.DeleteStaleMaintenanceRows(result.StaleMaintenancePaths);
                }
                catch
                {
                    resourceHealthOwner.ForceInvalidate("maintenance_hydration_cleanup_failed");
                    throw;
                }
            }
            cleanupStopwatch.Stop();
            result.CleanupMs = cleanupStopwatch.ElapsedMilliseconds;
        }

        result.ViewRefreshQueued = true;
        host.DispatchMaintenanceHydrationResult(result, resourceHealthTargets);
    }
}
