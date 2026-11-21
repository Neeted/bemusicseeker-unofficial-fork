using System.Collections.Generic;

namespace BeMusicSeeker.Models;

/// <summary>
/// Exposes the existing maintenance notifications through the maintenance tree contract.
/// </summary>
public partial class BMSLibrary : IMaintenanceTreePresentationState
{
    int IMaintenanceTreePresentationState.DuplicateChartGroupsInvalidationVersion
        => DuplicateChartGroupsInvalidationVersion;

    List<DuplicateGroup> IMaintenanceTreePresentationState.SplitPlacementGroups
        => SplitPlacementGroups;
}
