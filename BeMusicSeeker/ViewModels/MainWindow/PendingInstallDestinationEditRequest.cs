using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PendingInstallDestinationEditRequest
{
    private readonly Lazy<PackageChartEntry> chartEntry;

    private PendingInstallDestinationEditRequest(
        PackageChartEntry packageEntry,
        ChartFile chartFile,
        Func<PackageChartEntry> chartEntryFactory)
    {
        PackageEntry = packageEntry;
        ChartFile = chartFile;
        chartEntry = new Lazy<PackageChartEntry>(() => chartEntryFactory?.Invoke());
    }

    internal PackageChartEntry PackageEntry { get; }

    internal ChartFile ChartFile { get; }

    internal bool HasTarget => PackageEntry != null || ChartFile != null;

    internal static bool TryCreate(ChartOperationTarget target, out PendingInstallDestinationEditRequest request)
    {
        request = null;
        if (target?.HasCapability(ChartOperationCapabilities.UpdateInstallDestination) != true)
        {
            return false;
        }
        if (target.PackageEntry != null)
        {
            request = new PendingInstallDestinationEditRequest(target.PackageEntry, null, null);
            return true;
        }

        request = new PendingInstallDestinationEditRequest(null, target.Chart, target.ToPackageChartEntry);
        return request.HasTarget;
    }

    internal PackageChartEntry GetOrCreateChartEntry()
    {
        return PackageEntry ?? chartEntry.Value;
    }
}
