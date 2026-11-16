using System.Globalization;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 警告の短文(要約)と詳細が、種別と事実から読取り時に現在の資源で作られること、
/// および要約の順序・重複抑止・種類数・強調の仕様を確認します。期待値は資源と入力した事実から作ります。
/// </summary>
[TestClass]
public sealed class ChartWarningProjectionFormatterTests
{
    /// <summary>
    /// 全種別で、整形処理が例外なく空でないラベルを返し、種別ごとの要約・詳細の資源と引数の順序が正しいことを確認します。
    /// 種別と資源の取り違えや、資源健康度・候補一覧・件数の引数順の誤りを検出します。
    /// </summary>
    [TestMethod]
    public void EachKind_UsesItsDigestAndDetailResourcesWithFacts()
    {
        using IDisposable culture = TestCultureScope.UseJapaneseCulture();
        string candidateList = string.Join(Environment.NewLine, new[] { "- C:\\Dest\\A", "- C:\\Dest\\B", "- C:\\Dest\\C" });
        (ChartWarning Warning, string Label, string Detail, bool InDigest)[] cases =
        [
            (ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage), Resources.WarningDigest_NestedChart, Resources.Warning_NestedChartFileInPackage, true),
            (ChartWarning.Create(ChartWarningKind.ResourceBearingNestedCharts), Resources.WarningDigest_ResourceBearingNestedCharts, Resources.Warning_ResourceBearingNestedCharts, true),
            (ChartWarning.Create(ChartWarningKind.AlreadyInstalled), Resources.WarningDigest_AlreadyInstalled, Resources.Warning_AlreadyInstalled, true),
            (ChartWarning.Create(ChartWarningKind.SingleBmsFile), Resources.WarningDigest_SingleBmsFile, Resources.Warning_SingleBmsFile, true),
            (ChartWarning.Create(ChartWarningKind.SingleBmsonFile), Resources.WarningDigest_SingleBmsonFile, Resources.Warning_SingleBmsonFile, true),
            (ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 1, 4), Resources.WarningDigest_ResourceMissing, string.Format(Resources.Warning_WavFilesNotFound, 25, 1, 4), true),
            (ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceBgaMissing, 50, 2, 4), Resources.WarningDigest_ResourceMissing, string.Format(Resources.Warning_BgaFilesNotFound, 50, 2, 4), true),
            (ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceMovieMissing, 0, 0, 3), Resources.WarningDigest_ResourceMissing, string.Format(Resources.Warning_MovieFilesNotFound, 0, 0, 3), true),
            (ChartWarning.Create(ChartWarningKind.ResourceStagefileMissing), Resources.WarningDigest_ImageMissing, Resources.Warning_StagefileNotFound, false),
            (ChartWarning.Create(ChartWarningKind.ResourceBackbmpMissing), Resources.WarningDigest_ImageMissing, Resources.Warning_BackbmpNotFound, false),
            (ChartWarning.Create(ChartWarningKind.ResourceBannerMissing), Resources.WarningDigest_ImageMissing, Resources.Warning_BannerNotFound, false),
            (ChartWarning.CreateWithPathPair(ChartWarningKind.InstallEstimationAmbiguous, "p1", "p2"), Resources.WarningDigest_InstallEstimationAmbiguous, string.Format(Resources.Warning_InstallEstimationAmbiguous, "p1", "p2"), true),
            (ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationMetadataMismatch, "p1"), Resources.WarningDigest_InstallEstimationMetadataMismatch, string.Format(Resources.Warning_InstallEstimationMetadataMismatch, "p1"), true),
            (ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationUnrelated, "p1"), Resources.WarningDigest_InstallEstimationUnrelated, string.Format(Resources.Warning_InstallEstimationUnrelated, "p1"), true),
            (ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationMetadataUnavailable, "p1"), Resources.WarningDigest_InstallEstimationMetadataUnavailable, string.Format(Resources.Warning_InstallEstimationMetadataUnavailable, "p1"), true),
            (ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationReinstallNotImproved, "p1"), Resources.WarningDigest_InstallEstimationReinstallNotImproved, string.Format(Resources.Warning_InstallEstimationReinstallNotImproved, "p1"), true),
            (ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationAudioMissing, "p1"), Resources.WarningDigest_InstallEstimationAudioMissing, string.Format(Resources.Warning_InstallEstimationAudioMissing, "p1"), true),
            (ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationAudioUnverifiable, "p1"), Resources.WarningDigest_InstallEstimationAudioUnverifiable, string.Format(Resources.Warning_InstallEstimationAudioUnverifiable, "p1"), true),
            (ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A", @"C:\Dest\B", @"C:\Dest\C"]), Resources.WarningDigest_InstalledDestinationAmbiguous, string.Format(Resources.Warning_InstalledDestinationAmbiguous, candidateList), true),
            (ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, [@"C:\Dest\A", @"C:\Dest\B", @"C:\Dest\C"]), Resources.WarningDigest_InstalledDestinationAutoAppliedAmbiguous, string.Format(Resources.Warning_InstalledDestinationAutoAppliedAmbiguous, candidateList), true),
            (ChartWarning.Create(ChartWarningKind.UnsupportedResourcePath), Resources.WarningDigest_UnsupportedResourcePath, Resources.Warning_UnsupportedResourcePath, true),
            (ChartWarning.Create(ChartWarningKind.InstalledDestinationResolveFailed), Resources.WarningDigest_InstalledDestinationResolveFailed, Resources.Warning_InstalledDestinationResolveFailed, true),
            (ChartWarning.CreateSourceSurfaceScanLimitExceeded(50000), Resources.WarningDigest_SourceSurfaceScanLimitExceeded, string.Format(Resources.Warning_SourceSurfaceScanLimitExceeded, 50000), true),
            (ChartWarning.Create(ChartWarningKind.DuplicateChart), Resources.WarningDigest_DuplicateChart, Resources.Warning_DuplicateBmsFile, true),
            (ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch), Resources.WarningDigest_ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch, true),
            (ChartWarning.CreateChartInfoParseFailure("InvalidDataException", "parse_failed", "開始BPM未定義"), Resources.WarningDigest_ChartInfoParseFailure,
                string.Format(CultureInfo.CurrentCulture, Resources.Warning_ChartInfoParseFailure, "InvalidDataException", "開始BPM未定義"), true),
            (ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported), Resources.WarningDigest_Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported, true),
            (ChartWarning.Create(ChartWarningKind.Lr2PathTooLong), Resources.WarningDigest_Lr2PathTooLong, Resources.Warning_Lr2PathTooLong, true),
            (ChartWarning.Create(ChartWarningKind.Lr2ResourcePathUnsupported), Resources.WarningDigest_Lr2ResourcePathUnsupported, Resources.Warning_Lr2ResourcePathUnsupported, true),
            (ChartWarning.Create(ChartWarningKind.Lr2ResourcePathTooLong), Resources.WarningDigest_Lr2ResourcePathTooLong, Resources.Warning_Lr2ResourcePathTooLong, true),
        ];
        foreach ((ChartWarning warning, string label, string detail, bool inDigest) in cases)
        {
            Assert.AreEqual(label, ChartWarningProjectionFormatter.GetDigestLabel(warning), warning.Kind + " のラベル");
            Assert.AreEqual(detail, ChartWarningProjectionFormatter.GetDetailText(warning), warning.Kind + " の詳細");
            Assert.AreEqual(inDigest ? "[1] " + label : string.Empty, ChartWarningProjectionFormatter.BuildDigestText([warning], null), warning.Kind + " の要約");
            Assert.AreEqual(detail, ChartWarningProjectionFormatter.BuildTooltipText([warning]), warning.Kind + " の詳細全文");
        }

        // 生成元のない低信頼の種別は、例外なく空でないラベルを返します(詳細文は仕様対象外)。
        var lowConfidence = ChartWarning.Create(ChartWarningKind.InstallEstimationLowConfidence);
        Assert.IsFalse(string.IsNullOrWhiteSpace(ChartWarningProjectionFormatter.GetDigestLabel(lowConfidence)));
        Assert.AreEqual(Resources.WarningDigest_InstallEstimationLowConfidence, ChartWarningProjectionFormatter.GetDigestLabel(lowConfidence));
        _ = ChartWarningProjectionFormatter.GetDetailText(lowConfidence);

        ChartWarningKind[] covered = [.. cases.Select(row => row.Warning.Kind), ChartWarningKind.InstallEstimationLowConfidence];
        CollectionAssert.AreEquivalent(Enum.GetValues<ChartWarningKind>(), covered, "全種別を確認します。");
    }

    /// <summary>
    /// 要約が優先順位順で同じラベルを重複させず、種類数は有効な警告の種類を数え、詳細も優先順位順に並ぶことを確認します。
    /// 強調の有無(UnsupportedResourcePathは強調しない)も確認し、順序・重複・件数・強調の誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Digest_OrdersByPriorityDeduplicatesLabelsAndCountsKinds()
    {
        using IDisposable culture = TestCultureScope.UseJapaneseCulture();

        // WAV+BGAは同じラベルを重複せず、種類数は3(入れ子+WAV+BGA)になります。
        ChartWarning[] resources =
        [
            ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 1, 4),
            ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceBgaMissing, 50, 2, 4),
            ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage),
        ];
        Assert.AreEqual("[3] " + Resources.WarningDigest_NestedChart + ", " + Resources.WarningDigest_ResourceMissing,
            ChartWarningProjectionFormatter.BuildDigestText(resources, null));
        Assert.AreEqual(
            string.Join(Environment.NewLine, Resources.Warning_NestedChartFileInPackage,
                string.Format(Resources.Warning_WavFilesNotFound, 25, 1, 4), string.Format(Resources.Warning_BgaFilesNotFound, 50, 2, 4)),
            ChartWarningProjectionFormatter.BuildTooltipText(resources));

        // ゼロノート(20)・重複(30)・推定先複数(40)は優先順位順です。
        ChartWarning[] mixed =
        [
            ChartWarning.CreateWithPathPair(ChartWarningKind.InstallEstimationAmbiguous, "p1", "p2"),
            ChartWarning.Create(ChartWarningKind.DuplicateChart),
            ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch),
        ];
        Assert.AreEqual(
            "[3] " + string.Join(", ", Resources.WarningDigest_ZeroNoteMismatch, Resources.WarningDigest_DuplicateChart, Resources.WarningDigest_InstallEstimationAmbiguous),
            ChartWarningProjectionFormatter.BuildDigestText(mixed, null));

        // 同じ種類は後勝ちの一件として数えます。
        Assert.AreEqual("[1] " + Resources.WarningDigest_DuplicateChart, ChartWarningProjectionFormatter.BuildDigestText(
            [ChartWarning.Create(ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart)], null));

        // 音源理由はTITLE/ARTIST未確認の後ろに並び、保留(非表示にしない)では導入先があっても不足の要約を表示します。
        var audio = ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationAudioMissing, "p1");
        ChartWarning[] audioCase = [audio, ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationMetadataMismatch, "p1"),
            ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 1, 4)];
        string digest = ChartWarningProjectionFormatter.BuildDigestText(audioCase, @"C:\Destination", hideResourceHealthDigestWhenInstallDestinationSet: false);
        Assert.IsTrue(digest.IndexOf(Resources.WarningDigest_InstallEstimationMetadataMismatch, StringComparison.Ordinal)
            < digest.IndexOf(Resources.WarningDigest_InstallEstimationAudioMissing, StringComparison.Ordinal));
        StringAssert.Contains(digest, Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(ChartWarningProjectionFormatter.BuildTooltipText(audioCase), string.Format(Resources.Warning_InstallEstimationAudioMissing, "p1"));

        // 強調: 行強調を要求する種類があれば強調し、UnsupportedResourcePathなどは強調しない。
        Assert.IsTrue(ChartWarningProjectionFormatter.HasAnyHighlightedWarning([audio]));
        Assert.IsFalse(ChartWarningProjectionFormatter.HasAnyHighlightedWarning([ChartWarning.Create(ChartWarningKind.UnsupportedResourcePath)]));
        Assert.IsFalse(ChartWarningProjectionFormatter.HasAnyHighlightedWarning(resources));
        Assert.IsFalse(ChartWarningProjectionFormatter.HasAnyHighlightedWarning([]));
        Assert.AreEqual(string.Empty, ChartWarningProjectionFormatter.BuildDigestText([], null));
    }

    /// <summary>
    /// 導入先ありの通常行ではリソース不足の要約を隠して詳細を残し、保留(非表示にしない指定)では要約も表示することを確認します。
    /// </summary>
    [TestMethod]
    public void ResourceHealthDigest_HiddenForOwnedRowWithInstallDestination()
    {
        using IDisposable culture = TestCultureScope.UseJapaneseCulture();
        var warning = ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 50, 1, 2);
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartTestValues.Empty(),
            "C:\\Installed",
            string.Empty,
            string.Empty,
            [],
            [warning]);

        Assert.AreEqual(string.Empty, ChartWarningProjectionFormatter.BuildDigestText(chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
        Assert.AreEqual(string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2),
            ChartWarningProjectionFormatter.BuildTooltipText(chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
        Assert.AreEqual("[1] " + Resources.WarningDigest_ResourceMissing, ChartWarningProjectionFormatter.BuildDigestText(
            chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false, hideResourceHealthDigestWhenInstallDestinationSet: false));
    }

    /// <summary>
    /// 解析失敗の例外型・失敗分類・本文は記録された原文のまま表示し、欠落した場合だけ読取り時の現在の言語の代替語を使うことを確認します。
    /// </summary>
    [TestMethod]
    public void ParseFailure_KeepsDiagnosticsVerbatimAndResolvesFallbacksAtRead()
    {
        using IDisposable culture = TestCultureScope.UseJapaneseCulture();
        string label = Resources.WarningDigest_ChartInfoParseFailure;

        var verbatim = ChartWarning.CreateChartInfoParseFailure("InvalidDataException", "parse_failed", "開始BPM未定義");
        Assert.AreEqual("InvalidDataException", verbatim.ExceptionType);
        Assert.AreEqual("parse_failed", verbatim.FailureKind);
        Assert.AreEqual("開始BPM未定義", verbatim.DiagnosticMessage);
        Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.Warning_ChartInfoParseFailure, "InvalidDataException", "開始BPM未定義"),
            ChartWarningProjectionFormatter.GetDetailText(verbatim));

        // exception_typeと本文が空なら、原因はfailure_kind、本文は現在のラベルになります。
        var blank = ChartWarning.CreateChartInfoParseFailure(" ", "parse_failed", string.Empty);
        Assert.AreEqual(" ", blank.ExceptionType);
        Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.Warning_ChartInfoParseFailure, "parse_failed", label),
            ChartWarningProjectionFormatter.GetDetailText(blank));

        // 診断がすべて欠落していれば、原因も本文も現在のラベルになります。
        var missing = ChartWarning.CreateChartInfoParseFailure(null!, null!, null!);
        Assert.AreEqual(string.Empty, missing.ExceptionType);
        Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.Warning_ChartInfoParseFailure, label, label),
            ChartWarningProjectionFormatter.GetDetailText(missing));
        Assert.AreEqual("[1] " + label, ChartWarningProjectionFormatter.BuildDigestText([missing], null));
    }
}
