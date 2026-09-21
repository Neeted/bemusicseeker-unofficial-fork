using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PendingInstallDestinationClearRequest
{
    private readonly Lazy<IReadOnlyList<PackageChartEntry>> looseEntries;

    private PendingInstallDestinationClearRequest(
        IReadOnlyList<ChartOperationTarget> packageTargets,
        IReadOnlyList<ChartOperationTarget> looseTargets)
    {
        PackageTargets = packageTargets ?? throw new ArgumentNullException(nameof(packageTargets));
        LooseTargets = looseTargets ?? throw new ArgumentNullException(nameof(looseTargets));
        looseEntries = new Lazy<IReadOnlyList<PackageChartEntry>>(
            () => [.. LooseTargets
                .Select(target => target.ToPackageChartEntry())
                .Where(entry => entry?.Chart != null)]);
    }

    internal IReadOnlyList<ChartOperationTarget> PackageTargets { get; }

    internal IReadOnlyList<ChartOperationTarget> LooseTargets { get; }

    internal IReadOnlyList<PackageChartEntry> LooseEntries => looseEntries.Value;

    internal bool HasTargets => PackageTargets.Count > 0 || LooseTargets.Count > 0;

    internal void MaterializeLooseEntries()
    {
        _ = LooseEntries.Count;
    }

    internal static bool TryCreate(IEnumerable<ChartOperationTarget> targets, out PendingInstallDestinationClearRequest request)
    {
        request = null;
        if (targets == null)
        {
            return false;
        }

        List<ChartOperationTarget> validTargets = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))];
        List<ChartOperationTarget> packageTargets = [.. validTargets.Where(target => target.PackageEntry != null)];
        List<ChartOperationTarget> looseTargets = [.. validTargets.Where(target => target.PackageEntry == null)];

        PendingInstallDestinationClearRequest candidate = new(packageTargets, looseTargets);
        if (!candidate.HasTargets)
        {
            return false;
        }

        request = candidate;
        return true;
    }
}
