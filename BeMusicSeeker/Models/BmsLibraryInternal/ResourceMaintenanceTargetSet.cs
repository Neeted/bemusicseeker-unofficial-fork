using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct ResourceMaintenanceTargetSet
{
    private readonly IReadOnlyList<ChartFile> charts;

    private ResourceMaintenanceTargetSet(
        IEnumerable<ChartFile> charts,
        bool isFullOwned,
        StorageRowsVersionSnapshot? storageRowsVersion,
        int? ownedCollectionVersion,
        int? resourceHealthInputVersion)
    {
        this.charts = Array.AsReadOnly([.. (charts ?? []).Where(chart => chart != null)]);
        IsSpecified = true;
        IsFullOwned = isFullOwned;
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        ResourceHealthInputVersion = resourceHealthInputVersion;
    }

    internal IReadOnlyList<ChartFile> Charts => charts ?? [];

    internal bool IsSpecified { get; }

    internal bool IsFullOwned { get; }

    internal StorageRowsVersionSnapshot? StorageRowsVersion { get; }

    internal int? OwnedCollectionVersion { get; }

    internal int? ResourceHealthInputVersion { get; }

    internal int Count => Charts.Count;

    internal bool HasFullOwnedVersion => IsFullOwned
        && StorageRowsVersion.HasValue
        && OwnedCollectionVersion.HasValue
        && ResourceHealthInputVersion.HasValue;

    internal static ResourceMaintenanceTargetSet ForSubset(IEnumerable<ChartFile> charts)
    {
        return new ResourceMaintenanceTargetSet(charts, false, null, null, null);
    }

    internal static ResourceMaintenanceTargetSet ForFullOwned(
        IEnumerable<ChartFile> charts,
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

    internal ResourceMaintenanceTargetSet WithCharts(IEnumerable<ChartFile> charts)
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
