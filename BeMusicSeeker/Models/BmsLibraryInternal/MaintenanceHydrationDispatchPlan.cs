namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class MaintenanceHydrationDispatchPlan
{
    internal MaintenanceHydrationDispatchPlan(
        bool warningPresentationChanged,
        bool maintenancePresentationChanged,
        ResourceHealthIndexMutation resourceHealthMutation)
    {
        WarningPresentationChanged = warningPresentationChanged;
        MaintenancePresentationChanged = maintenancePresentationChanged;
        ResourceHealthMutation = resourceHealthMutation ?? throw new System.ArgumentNullException(nameof(resourceHealthMutation));
    }

    internal bool WarningPresentationChanged { get; }

    internal bool MaintenancePresentationChanged { get; }

    internal ResourceHealthIndexMutation ResourceHealthMutation { get; }
}
