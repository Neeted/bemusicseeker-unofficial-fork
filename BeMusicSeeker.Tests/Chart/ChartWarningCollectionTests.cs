using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartWarningCollectionTests
{
    /// <summary>
    /// 複数種類の不足警告は件数を保って同じラベルを重複表示せず、詳細には各本文を残して件数や診断の欠落を検出する。
    /// </summary>
    [TestMethod]
    public void WarningDigestText_OrdersByPriorityAndDeduplicatesLabelsWhileCountingKinds()
    {

        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceWavMissing), ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "wav missing")] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceBgaMissing), ChartWarning.Create(ChartWarningKind.ResourceBgaMissing, "bga missing")] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.NestedChartFileInPackage), ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage)] };

        Assert.AreEqual("[3] サブフォルダ譜面, リソース不足", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), Resources.Warning_NestedChartFileInPackage);
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "wav missing");
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "bga missing");
    }

    /// <summary>
    /// 導入先ありの譜面ではリソース不足の一覧要約を隠して詳細本文を残し、通常所持扱いの警告表示と混同する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ResourceWarningDigest_IsHiddenWhenChartInstallDestinationIsSet()
    {

        ChartFile file = ChartTestValues.Empty();
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceWavMissing), ChartWarning.Create(ChartWarningKind.ResourceWavMissing, string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2))] };
        ChartFile chart = ChartFileProjection.WithPackageState(
            (file),
            "C:\\Installed",
            string.Empty,
            string.Empty,
            [],
            file.Warnings);

        Assert.AreEqual(string.Empty, ChartWarningProjectionFormatter.BuildDigestText(chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
        StringAssert.Contains(ChartWarningProjectionFormatter.BuildTooltipText(chart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), "WAV");
    }

    /// <summary>
    /// 警告の追加で一回通知し、導入先の変化を要約へ反映して詳細は残すことで古い状態の表示固定を検出する。
    /// </summary>
    [TestMethod]
    public void WarningCollection_UsesCallbackAndInstallDestinationWithoutBmsFileOwner()
    {

        int changedCount = 0;
        string installDestination = string.Empty;
        var warnings = new ChartWarningCollection(
            () => changedCount++,
            () => installDestination);

        warnings.Set(ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "WAV"));

        Assert.AreEqual(1, changedCount);
        Assert.AreEqual("[1] リソース不足", warnings.BuildDigestText());

        installDestination = "C:\\Installed";

        Assert.AreEqual(string.Empty, warnings.BuildDigestText());
        StringAssert.Contains(warnings.BuildTooltipText(), "WAV");
    }

    /// <summary>
    /// ゼロノート・重複・導入先曖昧の警告を強調と要約へ反映し、全解除後に表示が残留する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void StructuredWarnings_DriveHighlightAndDigest()
    {

        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstallEstimationAmbiguous), ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous")] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile)] };

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
        Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual("[3] ゼロノート不整合, 重複譜面, 推定先複数", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Category != ChartWarningCategory.InstallEstimation)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart)] };

        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
        Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        Assert.IsFalse(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual(string.Empty, ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    /// <summary>
    /// 非対応リソースパスは詳細と専用要約を表示して行強調しないことで、不足や導入先の低信頼警告との混同を検出する。
    /// </summary>
    [TestMethod]
    public void UnsupportedResourcePath_IsNotLowConfidenceAlias()
    {

        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.UnsupportedResourcePath), ChartWarning.Create(ChartWarningKind.UnsupportedResourcePath, Resources.Warning_UnsupportedResourcePath)] };

        Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsFalse(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
        Assert.AreEqual("[1] リソースパス非対応", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), Resources.Warning_UnsupportedResourcePath);
    }

    /// <summary>
    /// 導入済み候補が曖昧な警告を行強調と専用要約へ反映し、曖昧さを通常状態として隠す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void InstalledDestinationAmbiguous_IsLowConfidenceAlias()
    {

        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstalledDestinationAmbiguous), ChartWarning.Create(ChartWarningKind.InstalledDestinationAmbiguous, Resources.Warning_InstalledDestinationAmbiguous)] };

        Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
        StringAssert.Contains(ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination), Resources.WarningDigest_InstalledDestinationAmbiguous);
    }

    /// <summary>
    /// 自動適用した曖昧導入先にも行強調・専用要約・詳細本文を残し、適用成功で曖昧さを消す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void InstalledDestinationAutoAppliedAmbiguous_IsLowConfidenceAlias()
    {

        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous), ChartWarning.Create(ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, Resources.Warning_InstalledDestinationAutoAppliedAmbiguous)] };

        Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
        StringAssert.Contains(ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination), Resources.WarningDigest_InstalledDestinationAutoAppliedAmbiguous);
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), Resources.Warning_InstalledDestinationAutoAppliedAmbiguous.Split('\n')[0]);
    }

    /// <summary>
    /// 譜面情報の解析失敗を強調と専用要約に示し、詳細には例外名と元の理由を残して失敗の分類や原因を失う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ChartInfoParseFailure_HighlightsAndUsesDedicatedDigest()
    {

        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ChartInfoParseFailure), ChartWarning.Create(ChartWarningKind.ChartInfoParseFailure, string.Format(Resources.Warning_ChartInfoParseFailure, "InvalidDataException", "開始BPM未定義"))] };

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual("[1] メタデータ解析エラー", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "InvalidDataException");
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "開始BPM未定義");
    }

    /// <summary>
    /// LR2非対応の文字コードを強調と専用要約へ反映し、詳細のShift_JIS制約が欠落する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void Lr2PathEncodingUnsupported_HighlightsAndUsesDedicatedDigest()
    {

        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathEncodingUnsupported), ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported)] };

        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual("[1] LR2パス非対応", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "Shift_JIS");
    }

    /// <summary>
    /// LR2の譜面パス長・リソース非対応・リソースパス長を別警告として表示し、種類の欠落や詳細の混同を検出する。
    /// </summary>
    [TestMethod]
    public void Lr2CompatibilityWarnings_HighlightAndUseDedicatedDigests()
    {

        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathTooLong), ChartWarning.Create(ChartWarningKind.Lr2PathTooLong, Resources.Warning_Lr2PathTooLong)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2ResourcePathUnsupported), ChartWarning.Create(ChartWarningKind.Lr2ResourcePathUnsupported, Resources.Warning_Lr2ResourcePathUnsupported)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2ResourcePathTooLong), ChartWarning.Create(ChartWarningKind.Lr2ResourcePathTooLong, Resources.Warning_Lr2ResourcePathTooLong)] };

        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual("[3] LR2パス長超過, LR2リソース非対応, LR2リソースパス長超過", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "パス長制限");
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "リソースパス");
    }

    /// <summary>
    /// 別の警告集合から置換した種類と要約が一致し、複数種類の取りこぼしを検出する。
    /// </summary>
    [TestMethod]
    public void ReplaceAll_CopiesStructuredWarningsToSnapshot()
    {

        var source = new ChartWarningCollection(() => { }, () => string.Empty);
        source.Set(ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage));
        source.Set(ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "wav missing"));
        var copy = new ChartWarningCollection(() => { }, () => string.Empty);

        copy.ReplaceAll(source.ToStructuredList());

        Assert.IsTrue(copy.Contains(ChartWarningKind.NestedChartFileInPackage));
        Assert.IsTrue(copy.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(source.BuildDigestText(), copy.BuildDigestText());
    }

    /// <summary>
    /// 実DBのsong・install・maintenanceに一時警告の列を作らず、投影状態が永続スキーマへ混入する誤りを検出する。
    /// </summary>
    /// <remarks>保存スキーマへの混入を検出するため、実SQLiteテーブルの列を観測する。</remarks>
    [TestMethod]
    public void Warning_IsNotPersistedInSongInstallOrMaintenanceTables()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_WarningSchema_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.install>();
            songDb.CreateTable<LR2SongDBExtended.maintenance>();

            AssertNoWarningColumn(songDb, "song");
            AssertNoWarningColumn(songDb, "install");
            AssertNoWarningColumn(songDb, "maintenance");
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    private static void AssertNoWarningColumn(LR2SongDBExtended songDb, string tableName)
    {
        string[] columnNames = [.. songDb.Query<ColumnNameRow>("PRAGMA table_info(" + tableName + ");").Select(row => row.name)];
        CollectionAssert.DoesNotContain(columnNames, "warning");
    }

    private sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }
}
