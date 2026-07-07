using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class RepairInstalledLocationSearchRequest
{
    private readonly Lazy<IReadOnlyList<PackageChartEntry>> repairEntries;

    private RepairInstalledLocationSearchRequest(IReadOnlyList<ChartOperationTarget> targets)
    {
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        repairEntries = new Lazy<IReadOnlyList<PackageChartEntry>>(
            () => [.. Targets
                .Select(target => target.ToPackageChartEntry())
                .Where(entry => entry?.Chart != null)]);
    }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal IReadOnlyList<PackageChartEntry> RepairEntries => repairEntries.Value;

    internal bool HasTargets => Targets.Count > 0;

    internal static bool TryCreate(IEnumerable<ChartOperationTarget> targets, out RepairInstalledLocationSearchRequest request)
    {
        request = null;
        if (targets == null)
        {
            return false;
        }

        List<ChartOperationTarget> validTargets = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation))];
        if (validTargets.Count == 0)
        {
            return false;
        }

        request = new RepairInstalledLocationSearchRequest(validTargets);
        return true;
    }

    internal void MaterializeRepairEntries()
    {
        _ = RepairEntries.Count;
    }
}
