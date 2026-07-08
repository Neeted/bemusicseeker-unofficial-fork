using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class MaintenanceHydrationOwnerAttachService
{
    internal static void AttachMaintenanceSnapshots(
        OwnedChartStorageOwnerView ownerView,
        MaintenanceTableHydrationResult result)
    {
        if (ownerView == null || result == null)
        {
            return;
        }

        foreach (BMSFile item in ownerView.BmsFiles)
        {
            if (item == null)
            {
                continue;
            }
            BMSFileMaintenanceInfo nextInfo = null;
            if (!string.IsNullOrWhiteSpace(item.path)
                && result.MaintenanceMap.TryGetValue(item.path, out BMSFileMaintenanceInfo value)
                && (item.HasMaintenanceInfoHash(value.hash) || string.Equals(value.hash, item.hash, System.StringComparison.OrdinalIgnoreCase)))
            {
                nextInfo = value;
                result.AppliedBmsCount++;
                item.SetMaintenanceInfo(nextInfo, suppressPropertyChanged: true, MaintenanceInfoOrigin.DbHydrated);
                result.ValidSnapshotCount++;
            }
            else
            {
                result.DefaultBmsCount++;
                if (item.HasValidMaintenanceInfoSnapshot)
                {
                    result.ValidSnapshotCount++;
                }
                else
                {
                    nextInfo = item.TryGetMaintenanceInfoWithoutCreating() ?? new BMSFileMaintenanceInfo(item);
                    item.SetMaintenanceInfo(nextInfo, suppressPropertyChanged: true, MaintenanceInfoOrigin.Placeholder);
                    result.PlaceholderCount++;
                }
            }
        }

        foreach (LR2SongDBExtended.bmson_song item in ownerView.BmsonSongs)
        {
            if (item == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(item.path)
                && result.MaintenanceMap.TryGetValue(item.path, out BMSFileMaintenanceInfo value)
                && string.Equals(value.hash, item.md5, System.StringComparison.OrdinalIgnoreCase))
            {
                value.NormalizeForBmson(item.path, item.md5);
                item.MaintenanceInfo = value;
                result.AppliedBmsonCount++;
                result.ValidSnapshotCount++;
            }
            else
            {
                result.DefaultBmsonCount++;
                if (item.MaintenanceInfo != null)
                {
                    result.ValidSnapshotCount++;
                }
                else
                {
                    result.PlaceholderCount++;
                }
            }
        }
    }

    internal static void CaptureOwnerPathAndStaleMaintenancePaths(
        OwnedChartStorageOwnerView ownerView,
        MaintenanceTableHydrationResult result)
    {
        if (ownerView == null || result == null)
        {
            return;
        }

        result.OwnerPathCount = ownerView.OwnerPathCount;
        foreach (string maintenancePath in result.MaintenanceMap.Keys)
        {
            if (!ownerView.ContainsOwnerPath(maintenancePath))
            {
                result.StaleMaintenancePaths.Add(maintenancePath);
            }
        }
        result.StalePathCount = result.StaleMaintenancePaths.Count;
    }
}
