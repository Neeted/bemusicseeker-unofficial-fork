using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Classifies the package catalog section selected for removal from the chart table.
/// </summary>
internal enum PackageCatalogSection
{
    Pending,
    Installed
}

/// <summary>
/// Describes a chart-table request to remove pending or newly installed package catalog entries.
/// </summary>
internal sealed class PackageCatalogRemovalRequest
{
    private PackageCatalogRemovalRequest(PackageCatalogSection section, IReadOnlyList<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one chart operation target is required.", nameof(targets));
        }

        Section = section;
        Targets = targets;
    }

    internal PackageCatalogSection Section { get; }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal bool IsPending => Section == PackageCatalogSection.Pending;

    internal static PackageCatalogRemovalRequest CreatePending(IEnumerable<ChartOperationTarget> targets)
    {
        return new PackageCatalogRemovalRequest(PackageCatalogSection.Pending, MaterializeTargets(targets));
    }

    internal static PackageCatalogRemovalRequest CreateInstalled(IEnumerable<ChartOperationTarget> targets)
    {
        return new PackageCatalogRemovalRequest(PackageCatalogSection.Installed, MaterializeTargets(targets));
    }

    private static IReadOnlyList<ChartOperationTarget> MaterializeTargets(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        var materializedTargets = new List<ChartOperationTarget>();
        foreach (ChartOperationTarget target in targets)
        {
            if (target == null)
            {
                throw new ArgumentException("Selected target collection must not contain null targets.", nameof(targets));
            }

            materializedTargets.Add(target);
        }

        return materializedTargets;
    }
}
