using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>譜面と導入entryの警告観測を同じ判定・表示処理へ接続します。</summary>
internal static class ChartWarningTestHelpers
{
    /// <summary>推定の低信頼に属する警告があるかを、元の警告集合から観測します。</summary>
    internal static bool ContainsLowConfidenceInstallEstimationWarning(ChartFile? chart)
    {
        return chart?.Warnings.Any(IsLowConfidenceInstallEstimationWarning) == true;
    }

    /// <summary>推定の低信頼に属する警告があるかを、元の警告集合から観測します。</summary>
    internal static bool ContainsLowConfidenceInstallEstimationWarning(PackageChartEntry? entry)
    {
        return ContainsLowConfidenceInstallEstimationWarning(entry?.Chart);
    }

    /// <summary>元の警告と導入先から同じ要約文言を取得します。</summary>
    internal static string BuildDigestText(ChartFile? chart)
    {
        return ChartWarningCollection.BuildDigestText(chart?.Warnings ?? [], chart?.InstallDestination);
    }

    /// <summary>元の警告と導入先から同じ要約文言を取得します。</summary>
    internal static string BuildDigestText(PackageChartEntry? entry)
    {
        return BuildDigestText(entry?.Chart);
    }

    /// <summary>元の警告集合から同じtooltip文言を取得します。</summary>
    internal static string BuildTooltipText(ChartFile? chart)
    {
        return ChartWarningCollection.BuildTooltipText(chart?.Warnings ?? []);
    }

    /// <summary>元の警告集合から同じtooltip文言を取得します。</summary>
    internal static string BuildTooltipText(PackageChartEntry? entry)
    {
        return BuildTooltipText(entry?.Chart);
    }

    private static bool IsLowConfidenceInstallEstimationWarning(ChartWarning warning)
    {
        return warning?.Kind == ChartWarningKind.InstallEstimationAmbiguous
            || warning?.Kind == ChartWarningKind.InstallEstimationMetadataMismatch
            || warning?.Kind == ChartWarningKind.InstallEstimationUnrelated
            || warning?.Kind == ChartWarningKind.InstallEstimationMetadataUnavailable
            || warning?.Kind == ChartWarningKind.InstallEstimationReinstallNotImproved
            || warning?.Kind == ChartWarningKind.InstalledDestinationAmbiguous
            || warning?.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous
            || warning?.Kind == ChartWarningKind.InstallEstimationLowConfidence;
    }
}
