using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PendingInstallDestinationSearchRequest
{
    private PendingInstallDestinationSearchRequest(
        IReadOnlyList<ChartOperationTarget> packageTargets,
        IReadOnlyList<PackageChartEntry> looseEntries)
    {
        PackageTargets = packageTargets ?? throw new ArgumentNullException(nameof(packageTargets));
        LooseEntries = looseEntries ?? throw new ArgumentNullException(nameof(looseEntries));
        if (!HasTargets)
        {
            throw new ArgumentException("At least one pending install destination target is required.", nameof(packageTargets));
        }
    }

    internal IReadOnlyList<ChartOperationTarget> PackageTargets { get; }

    internal IReadOnlyList<PackageChartEntry> LooseEntries { get; }

    internal int SelectedRowCount => PackageTargets.Count + LooseEntries.Count;

    internal bool HasTargets => PackageTargets.Count > 0 || LooseEntries.Count > 0;

    internal static PendingInstallDestinationSearchRequest CreateInstallDestinationSearch(IEnumerable<ChartOperationTarget> targets)
    {
        return Create(targets);
    }

    private static PendingInstallDestinationSearchRequest Create(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        List<ChartOperationTarget> validTargets = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))];
        List<ChartOperationTarget> packageTargets = [.. validTargets.Where(target => target.PackageEntry != null)];
        List<PackageChartEntry> looseEntries = [.. validTargets
            .Where(target => target.PackageEntry == null)
            .Select(target => target.ToPackageChartEntry())
            .Where(entry => entry?.Chart != null)];

        return new PendingInstallDestinationSearchRequest(packageTargets, looseEntries);
    }
}
