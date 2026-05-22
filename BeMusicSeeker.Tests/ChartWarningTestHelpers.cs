using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

internal static class ChartWarningTestHelpers
{
    internal static bool ContainsLowConfidenceInstallEstimationWarning(BMSFile file)
    {
        return file?.Warnings.ToStructuredList().Any(IsLowConfidenceInstallEstimationWarning) == true;
    }

    internal static bool ContainsLowConfidenceInstallEstimationWarning(ChartFile? chart)
    {
        return chart?.Warnings.Any(IsLowConfidenceInstallEstimationWarning) == true;
    }

    internal static bool ContainsLowConfidenceInstallEstimationWarning(PackageChartEntry? entry)
    {
        return ContainsLowConfidenceInstallEstimationWarning(entry?.Chart);
    }

    internal static string BuildDigestText(ChartFile? chart)
    {
        return ChartWarningCollection.BuildDigestText(chart?.Warnings ?? [], chart?.InstallDestination);
    }

    internal static string BuildDigestText(PackageChartEntry? entry)
    {
        return BuildDigestText(entry?.Chart);
    }

    internal static string BuildTooltipText(ChartFile? chart)
    {
        return ChartWarningCollection.BuildTooltipText(chart?.Warnings ?? []);
    }

    internal static string BuildTooltipText(PackageChartEntry? entry)
    {
        return BuildTooltipText(entry?.Chart);
    }

    private static bool IsLowConfidenceInstallEstimationWarning(ChartWarning warning)
    {
        return warning?.Kind == ChartWarningKind.InstallEstimationAmbiguous
            || warning?.Kind == ChartWarningKind.InstallEstimationMetadataMismatch
            || warning?.Kind == ChartWarningKind.InstallEstimationReinstallNotImproved
            || warning?.Kind == ChartWarningKind.InstalledDestinationAmbiguous
            || warning?.Kind == ChartWarningKind.InstallEstimationLowConfidence;
    }
}
