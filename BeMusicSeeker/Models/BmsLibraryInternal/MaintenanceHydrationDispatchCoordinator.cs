namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class MaintenanceHydrationDispatchCoordinator
{
    private readonly IMaintenanceHydrationDispatchHost host;

    internal MaintenanceHydrationDispatchCoordinator(IMaintenanceHydrationDispatchHost host)
    {
        this.host = host ?? throw new System.ArgumentNullException(nameof(host));
    }

    internal void Dispatch(
        MaintenanceTableHydrationResult hydrationResult,
        ResourceMaintenanceTargetSet fullOwnedTargets)
    {
        MaintenanceHydrationDispatchPlan plan = ResourceHealthIndexMutationPlanner.BuildMaintenanceHydrationDispatchPlan(fullOwnedTargets);
        long resourceHealthIndexMs = host.DispatchMaintenanceHydration(plan);
        if (hydrationResult != null)
        {
            hydrationResult.ResourceHealthIndexMs = resourceHealthIndexMs;
        }
    }
}
