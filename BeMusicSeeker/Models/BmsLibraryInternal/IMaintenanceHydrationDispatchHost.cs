namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IMaintenanceHydrationDispatchHost
{
    long DispatchMaintenanceHydration(MaintenanceHydrationDispatchPlan plan);
}
