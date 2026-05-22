using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;

internal static class ChartWarningTestHelpers
{
    internal static bool ContainsLowConfidenceInstallEstimationWarning(BMSFile file)
    {
        return file?.Warnings.ToStructuredList().Any(IsLowConfidenceInstallEstimationWarning) == true;
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
