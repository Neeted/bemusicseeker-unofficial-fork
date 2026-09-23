namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ResourceHealthFullOwnedTargetFreshness
{
    internal static bool IsCurrent(
        ResourceMaintenanceTargetSet targetSet,
        StorageRowsVersionSnapshot currentStorageRowsVersion,
        int currentOwnedCollectionVersion,
        int currentResourceHealthInputVersion,
        bool currentResourceHealthInputVersionStable)
    {
        if (!targetSet.HasFullOwnedVersion || !currentResourceHealthInputVersionStable)
        {
            return false;
        }

        return targetSet.StorageRowsVersion.Value.BmsRowsVersion == currentStorageRowsVersion.BmsRowsVersion
            && targetSet.StorageRowsVersion.Value.BmsonRowsVersion == currentStorageRowsVersion.BmsonRowsVersion
            && targetSet.OwnedCollectionVersion.Value == currentOwnedCollectionVersion
            && targetSet.ResourceHealthInputVersion.Value == currentResourceHealthInputVersion;
    }
}
