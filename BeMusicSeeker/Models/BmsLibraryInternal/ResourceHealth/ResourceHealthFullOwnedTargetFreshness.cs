namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ResourceHealthFullOwnedTargetFreshness
{
    internal static bool IsCurrent(
        ResourceMaintenanceTargetSet targetSet,
        OwnedChartCollectionVersionSnapshot currentStorageRowsVersion,
        int currentOwnedCollectionVersion,
        int currentResourceHealthInputVersion,
        bool currentResourceHealthInputVersionStable)
    {
        if (!targetSet.HasFullOwnedVersion || !currentResourceHealthInputVersionStable)
        {
            return false;
        }

        return targetSet.StorageRowsVersion.Value.OwnedCollectionVersion == currentStorageRowsVersion.OwnedCollectionVersion
            && targetSet.OwnedCollectionVersion.Value == currentOwnedCollectionVersion
            && targetSet.ResourceHealthInputVersion.Value == currentResourceHealthInputVersion;
    }
}
