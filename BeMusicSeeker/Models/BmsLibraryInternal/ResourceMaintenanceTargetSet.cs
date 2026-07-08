using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct ResourceMaintenanceTargetSet
{
    private readonly List<ChartFile> charts;

    private ResourceMaintenanceTargetSet(
        List<ChartFile> charts,
        bool isFullOwned,
        StorageRowsVersionSnapshot? storageRowsVersion,
        int? ownedCollectionVersion,
        int? resourceHealthInputVersion)
    {
        this.charts = charts ?? [];
        IsSpecified = true;
        IsFullOwned = isFullOwned;
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        ResourceHealthInputVersion = resourceHealthInputVersion;
    }

    internal List<ChartFile> Charts => charts ?? [];

    internal bool IsSpecified { get; }

    internal bool IsFullOwned { get; }

    internal StorageRowsVersionSnapshot? StorageRowsVersion { get; }

    internal int? OwnedCollectionVersion { get; }

    internal int? ResourceHealthInputVersion { get; }

    internal int Count => Charts.Count;

    internal static ResourceMaintenanceTargetSet ForSubset(List<ChartFile> charts)
    {
        return new ResourceMaintenanceTargetSet(charts, false, null, null, null);
    }

    internal static ResourceMaintenanceTargetSet ForFullOwned(
        List<ChartFile> charts,
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion,
        int resourceHealthInputVersion)
    {
        return new ResourceMaintenanceTargetSet(
            charts,
            true,
            storageRowsVersion,
            ownedCollectionVersion,
            resourceHealthInputVersion);
    }

    internal ResourceMaintenanceTargetSet WithCharts(List<ChartFile> charts)
    {
        return IsSpecified
            ? new ResourceMaintenanceTargetSet(
                charts,
                IsFullOwned,
                StorageRowsVersion,
                OwnedCollectionVersion,
                ResourceHealthInputVersion)
            : default;
    }

    internal ResourceMaintenanceTargetSet WithOwnedCollectionVersion(int ownedCollectionVersion)
    {
        return IsSpecified
            ? new ResourceMaintenanceTargetSet(
                Charts,
                IsFullOwned,
                StorageRowsVersion,
                ownedCollectionVersion,
                ResourceHealthInputVersion)
            : default;
    }

    internal ResourceMaintenanceTargetSet WithResourceHealthInputVersion(int resourceHealthInputVersion)
    {
        return IsSpecified
            ? new ResourceMaintenanceTargetSet(
                Charts,
                IsFullOwned,
                StorageRowsVersion,
                OwnedCollectionVersion,
                resourceHealthInputVersion)
            : default;
    }
}
