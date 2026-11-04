using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 構造化 warning の事実から、WARNING 列の短文と tooltip の詳細を読取り時に作ります。
/// 文言は常に現在の表示言語の資源から選び、警告自体は言語に依存しない種別と事実だけを保持します。
/// 翻訳結果は保持しないため、言語変更では表示値のキャッシュ失効だけで現在語に追従します。
/// </summary>
internal static class ChartWarningProjectionFormatter
{
    internal static string BuildDigestText(
        ChartFile chart,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection,
        bool hideResourceHealthDigestWhenInstallDestinationSet = true)
    {
        return BuildDigestText(
            EnumerateProjectedWarnings(chart?.Warnings, resourceHealthProjection, hasResourceHealthProjection),
            chart?.InstallDestination,
            hideResourceHealthDigestWhenInstallDestinationSet);
    }

    internal static string BuildDisplayText(
        ChartFile chart,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return BuildDisplayText(
            EnumerateProjectedWarnings(chart?.Warnings, resourceHealthProjection, hasResourceHealthProjection));
    }

    internal static string BuildTooltipText(
        ChartFile chart,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return BuildTooltipText(
            EnumerateProjectedWarnings(chart?.Warnings, resourceHealthProjection, hasResourceHealthProjection));
    }

    internal static bool HasHighlightedWarning(
        ChartFile chart,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return HasAnyHighlightedWarning(
            EnumerateProjectedWarnings(chart?.Warnings, resourceHealthProjection, hasResourceHealthProjection));
    }

    /// <summary>
    /// WARNING 列に表示する短文（件数と種類のラベル）を作成します。
    /// </summary>
    /// <param name="sourceWarnings">対象の warning。</param>
    /// <param name="installDestination">導入先。</param>
    /// <param name="hideResourceHealthDigestWhenInstallDestinationSet">導入先がある行で資源健康度の短文を隠すかどうか。保留項目では false です。</param>
    /// <returns>短文。表示対象がなければ空文字。</returns>
    internal static string BuildDigestText(
        IEnumerable<ChartWarning> sourceWarnings,
        string installDestination,
        bool hideResourceHealthDigestWhenInstallDestinationSet = true)
    {
        List<ChartWarning> warnings = [.. EnumerateEffectiveWarnings(sourceWarnings)];
        if (warnings.Count == 0)
        {
            return string.Empty;
        }

        string[] labels = [.. warnings
            .Where(warning => warning.ShowInDigest && ShouldShowInDigest(warning, installDestination, hideResourceHealthDigestWhenInstallDestinationSet))
            .OrderBy(warning => warning.Priority)
            .Select(GetDigestLabel)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)];
        if (labels.Length == 0)
        {
            return string.Empty;
        }
        return "[" + warnings.Select(warning => warning.Kind).Distinct().Count() + "] " + string.Join(", ", labels);
    }

    /// <summary>
    /// tooltip に表示する詳細本文を、優先度順に改行で連結して作成します。
    /// </summary>
    /// <param name="sourceWarnings">対象の warning。</param>
    /// <returns>詳細本文。</returns>
    internal static string BuildTooltipText(IEnumerable<ChartWarning> sourceWarnings)
    {
        return string.Join(
            Environment.NewLine,
            [.. EnumerateEffectiveWarnings(sourceWarnings)
                .Where(warning => warning.ShowInTooltip)
                .OrderBy(warning => warning.Priority)
                .Select(GetDetailText)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 互換表示用の WARNING 全文を作成します。詳細本文と同じです。
    /// </summary>
    /// <param name="sourceWarnings">対象の warning。</param>
    /// <returns>詳細表示文字列。</returns>
    internal static string BuildDisplayText(IEnumerable<ChartWarning> sourceWarnings)
    {
        return BuildTooltipText(sourceWarnings);
    }

    /// <summary>
    /// いずれかの warning が行ハイライトを要求しているかどうかを返します。
    /// </summary>
    /// <param name="sourceWarnings">対象の warning。</param>
    /// <returns>要求している場合は true。</returns>
    internal static bool HasAnyHighlightedWarning(IEnumerable<ChartWarning> sourceWarnings)
    {
        return EnumerateEffectiveWarnings(sourceWarnings).Any(warning => warning.HighlightRow);
    }

    /// <summary>
    /// 種別に対応する短いラベルを現在の表示言語で返します。
    /// </summary>
    /// <param name="warning">対象の warning。</param>
    /// <returns>短いラベル。</returns>
    internal static string GetDigestLabel(ChartWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        return warning.Kind switch
        {
            ChartWarningKind.NestedChartFileInPackage => Resources.WarningDigest_NestedChart,
            ChartWarningKind.ResourceBearingNestedCharts => Resources.WarningDigest_ResourceBearingNestedCharts,
            ChartWarningKind.ZeroNoteMismatch => Resources.WarningDigest_ZeroNoteMismatch,
            ChartWarningKind.ChartInfoParseFailure => Resources.WarningDigest_ChartInfoParseFailure,
            ChartWarningKind.Lr2PathEncodingUnsupported => Resources.WarningDigest_Lr2PathEncodingUnsupported,
            ChartWarningKind.Lr2PathTooLong => Resources.WarningDigest_Lr2PathTooLong,
            ChartWarningKind.Lr2ResourcePathUnsupported => Resources.WarningDigest_Lr2ResourcePathUnsupported,
            ChartWarningKind.Lr2ResourcePathTooLong => Resources.WarningDigest_Lr2ResourcePathTooLong,
            ChartWarningKind.DuplicateChart => Resources.WarningDigest_DuplicateChart,
            ChartWarningKind.InstallEstimationAmbiguous => Resources.WarningDigest_InstallEstimationAmbiguous,
            ChartWarningKind.InstallEstimationMetadataMismatch => Resources.WarningDigest_InstallEstimationMetadataMismatch,
            ChartWarningKind.InstallEstimationUnrelated => Resources.WarningDigest_InstallEstimationUnrelated,
            ChartWarningKind.InstallEstimationMetadataUnavailable => Resources.WarningDigest_InstallEstimationMetadataUnavailable,
            ChartWarningKind.InstallEstimationAudioMissing => Resources.WarningDigest_InstallEstimationAudioMissing,
            ChartWarningKind.InstallEstimationAudioUnverifiable => Resources.WarningDigest_InstallEstimationAudioUnverifiable,
            ChartWarningKind.InstallEstimationReinstallNotImproved => Resources.WarningDigest_InstallEstimationReinstallNotImproved,
            ChartWarningKind.InstalledDestinationAmbiguous => Resources.WarningDigest_InstalledDestinationAmbiguous,
            ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous => Resources.WarningDigest_InstalledDestinationAutoAppliedAmbiguous,
            ChartWarningKind.UnsupportedResourcePath => Resources.WarningDigest_UnsupportedResourcePath,
            ChartWarningKind.InstalledDestinationResolveFailed => Resources.WarningDigest_InstalledDestinationResolveFailed,
            ChartWarningKind.SourceSurfaceScanLimitExceeded => Resources.WarningDigest_SourceSurfaceScanLimitExceeded,
            ChartWarningKind.InstallEstimationLowConfidence => Resources.WarningDigest_InstallEstimationLowConfidence,
            ChartWarningKind.AlreadyInstalled => Resources.WarningDigest_AlreadyInstalled,
            ChartWarningKind.SingleBmsFile => Resources.WarningDigest_SingleBmsFile,
            ChartWarningKind.SingleBmsonFile => Resources.WarningDigest_SingleBmsonFile,
            ChartWarningKind.ResourceWavMissing or ChartWarningKind.ResourceBgaMissing or ChartWarningKind.ResourceMovieMissing => Resources.WarningDigest_ResourceMissing,
            ChartWarningKind.ResourceStagefileMissing or ChartWarningKind.ResourceBackbmpMissing or ChartWarningKind.ResourceBannerMissing => Resources.WarningDigest_ImageMissing,
            _ => throw new ArgumentOutOfRangeException(nameof(warning), warning.Kind, null),
        };
    }

    /// <summary>
    /// 種別と事実から、tooltip に出す詳細を現在の表示言語で作成します。
    /// 引数の書式は現在のカルチャで整形します。
    /// </summary>
    /// <param name="warning">対象の warning。</param>
    /// <returns>詳細本文。生成元のない種別では空文字。</returns>
    internal static string GetDetailText(ChartWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        return warning.Kind switch
        {
            ChartWarningKind.NestedChartFileInPackage => Resources.Warning_NestedChartFileInPackage,
            ChartWarningKind.ResourceBearingNestedCharts => Resources.Warning_ResourceBearingNestedCharts,
            ChartWarningKind.ZeroNoteMismatch => Resources.Warning_ZeroNoteMismatch,
            ChartWarningKind.ChartInfoParseFailure => BuildParseFailureDetail(warning),
            ChartWarningKind.Lr2PathEncodingUnsupported => Resources.Warning_Lr2PathEncodingUnsupported,
            ChartWarningKind.Lr2PathTooLong => Resources.Warning_Lr2PathTooLong,
            ChartWarningKind.Lr2ResourcePathUnsupported => Resources.Warning_Lr2ResourcePathUnsupported,
            ChartWarningKind.Lr2ResourcePathTooLong => Resources.Warning_Lr2ResourcePathTooLong,
            ChartWarningKind.DuplicateChart => Resources.Warning_DuplicateBmsFile,
            ChartWarningKind.InstallEstimationAmbiguous => string.Format(Resources.Warning_InstallEstimationAmbiguous, warning.Path, warning.SecondaryPath),
            ChartWarningKind.InstallEstimationMetadataMismatch => string.Format(Resources.Warning_InstallEstimationMetadataMismatch, warning.Path),
            ChartWarningKind.InstallEstimationUnrelated => string.Format(Resources.Warning_InstallEstimationUnrelated, warning.Path),
            ChartWarningKind.InstallEstimationMetadataUnavailable => string.Format(Resources.Warning_InstallEstimationMetadataUnavailable, warning.Path),
            ChartWarningKind.InstallEstimationAudioMissing => string.Format(Resources.Warning_InstallEstimationAudioMissing, warning.Path),
            ChartWarningKind.InstallEstimationAudioUnverifiable => string.Format(Resources.Warning_InstallEstimationAudioUnverifiable, warning.Path),
            ChartWarningKind.InstallEstimationReinstallNotImproved => string.Format(Resources.Warning_InstallEstimationReinstallNotImproved, warning.Path),
            ChartWarningKind.InstalledDestinationAmbiguous => string.Format(Resources.Warning_InstalledDestinationAmbiguous, BuildCandidateList(warning)),
            ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous => string.Format(Resources.Warning_InstalledDestinationAutoAppliedAmbiguous, BuildCandidateList(warning)),
            ChartWarningKind.UnsupportedResourcePath => Resources.Warning_UnsupportedResourcePath,
            ChartWarningKind.InstalledDestinationResolveFailed => Resources.Warning_InstalledDestinationResolveFailed,
            ChartWarningKind.SourceSurfaceScanLimitExceeded => string.Format(Resources.Warning_SourceSurfaceScanLimitExceeded, warning.Count),
            // 生成元のない種別です。従来と同じく詳細を持たせません。
            ChartWarningKind.InstallEstimationLowConfidence => string.Empty,
            ChartWarningKind.AlreadyInstalled => Resources.Warning_AlreadyInstalled,
            ChartWarningKind.SingleBmsFile => Resources.Warning_SingleBmsFile,
            ChartWarningKind.SingleBmsonFile => Resources.Warning_SingleBmsonFile,
            ChartWarningKind.ResourceWavMissing => string.Format(Resources.Warning_WavFilesNotFound, warning.Health, warning.ExistingCount, warning.DefinedCount),
            ChartWarningKind.ResourceBgaMissing => string.Format(Resources.Warning_BgaFilesNotFound, warning.Health, warning.ExistingCount, warning.DefinedCount),
            ChartWarningKind.ResourceMovieMissing => string.Format(Resources.Warning_MovieFilesNotFound, warning.Health, warning.ExistingCount, warning.DefinedCount),
            ChartWarningKind.ResourceStagefileMissing => Resources.Warning_StagefileNotFound,
            ChartWarningKind.ResourceBackbmpMissing => Resources.Warning_BackbmpNotFound,
            ChartWarningKind.ResourceBannerMissing => Resources.Warning_BannerNotFound,
            _ => throw new ArgumentOutOfRangeException(nameof(warning), warning.Kind, null),
        };
    }

    private static string BuildCandidateList(ChartWarning warning)
    {
        return string.Join(Environment.NewLine, warning.Candidates.Select(path => "- " + path));
    }

    // 記録された診断は原文のまま使い、欠落している場合だけ現在の表示言語の代替語を使います。
    private static string BuildParseFailureDetail(ChartWarning warning)
    {
        string reason = !string.IsNullOrWhiteSpace(warning.ExceptionType)
            ? warning.ExceptionType
            : (!string.IsNullOrWhiteSpace(warning.FailureKind) ? warning.FailureKind : Resources.WarningDigest_ChartInfoParseFailure);
        string message = !string.IsNullOrWhiteSpace(warning.DiagnosticMessage) ? warning.DiagnosticMessage : Resources.WarningDigest_ChartInfoParseFailure;
        return string.Format(CultureInfo.CurrentCulture, Resources.Warning_ChartInfoParseFailure, reason, message);
    }

    private static bool ShouldShowInDigest(ChartWarning warning, string installDestination, bool hideResourceHealthDigestWhenInstallDestinationSet)
    {
        if (hideResourceHealthDigestWhenInstallDestinationSet && warning.Category == ChartWarningCategory.ResourceHealth)
        {
            return string.IsNullOrWhiteSpace(installDestination);
        }
        return true;
    }

    private static IEnumerable<ChartWarning> EnumerateEffectiveWarnings(IEnumerable<ChartWarning> sourceWarnings)
    {
        Dictionary<ChartWarningKind, ChartWarning> effective = [];
        foreach (ChartWarning warning in sourceWarnings ?? [])
        {
            if (warning != null)
            {
                effective[warning.Kind] = warning;
            }
        }
        return effective.Values;
    }

    private static IEnumerable<ChartWarning> EnumerateProjectedWarnings(
        IEnumerable<ChartWarning> sourceWarnings,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        foreach (ChartWarning warning in (sourceWarnings ?? Enumerable.Empty<ChartWarning>()).Where(warning => warning != null && (!hasResourceHealthProjection || warning.Category != ChartWarningCategory.ResourceHealth)))
        {
            yield return warning;
        }

        if (!hasResourceHealthProjection)
        {
            yield break;
        }

        foreach (ChartWarning warning in resourceHealthProjection?.Warnings ?? Enumerable.Empty<ChartWarning>())
        {
            if (warning != null)
            {
                yield return warning;
            }
        }
    }
}
