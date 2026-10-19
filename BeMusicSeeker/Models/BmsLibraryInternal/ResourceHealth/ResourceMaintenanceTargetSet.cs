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
        int? resourceHealthInputVersion)
    {
        this.charts = Array.AsReadOnly([.. (charts ?? []).Where(chart => chart != null)]);
        IsSpecified = true;
        IsFullOwned = isFullOwned;
        ResourceHealthInputVersion = resourceHealthInputVersion;
    }

    internal IReadOnlyList<ChartFile> Charts => charts ?? [];

    internal bool IsSpecified { get; }

    internal bool IsFullOwned { get; }

    internal int? ResourceHealthInputVersion { get; }

    internal int Count => Charts.Count;

    internal bool HasFullOwnedVersion => IsFullOwned
        && ResourceHealthInputVersion.HasValue;

    internal static ResourceMaintenanceTargetSet ForSubset(IEnumerable<ChartFile> charts)
    {
        return new ResourceMaintenanceTargetSet(charts, false, null);
    }

    internal static ResourceMaintenanceTargetSet ForFullOwned(
        IEnumerable<ChartFile> charts,
        int resourceHealthInputVersion)
    {
        return new ResourceMaintenanceTargetSet(
            charts,
            true,
            resourceHealthInputVersion);
    }

    internal ResourceMaintenanceTargetSet WithCharts(IEnumerable<ChartFile> charts)
    {
        return IsSpecified
            ? new ResourceMaintenanceTargetSet(
                charts,
                IsFullOwned,
                ResourceHealthInputVersion)
            : default;
    }

    internal ResourceMaintenanceTargetSet WithResourceHealthInputVersion(int resourceHealthInputVersion)
    {
        return IsSpecified
            ? new ResourceMaintenanceTargetSet(
                Charts,
                IsFullOwned,
                resourceHealthInputVersion)
            : default;
    }
}
