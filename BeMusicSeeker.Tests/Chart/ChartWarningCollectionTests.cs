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
    [TestMethod]
    public void WarningDigestText_OrdersByPriorityAndDeduplicatesLabelsWhileCountingKinds()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceWavMissing), ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "wav missing")] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceBgaMissing), ChartWarning.Create(ChartWarningKind.ResourceBgaMissing, "bga missing")] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.NestedChartFileInPackage), ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage)] };

        Assert.AreEqual("[3] サブフォルダ譜面, リソース不足", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), Resources.Warning_NestedChartFileInPackage);
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "wav missing");
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "bga missing");
    }

    [TestMethod]
    public void ClearWarningsByCategory_RemovesOnlyMatchingStructuredWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstallEstimationAmbiguous), ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "structured estimate")] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.NestedChartFileInPackage), ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage)] };

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Category != ChartWarningCategory.InstallEstimation)] };

        Assert.IsFalse(ChartWarningCollection.BuildTooltipText(file.Warnings).Contains("structured estimate"));
        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        Assert.AreEqual("[1] サブフォルダ譜面", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
    }

    [TestMethod]
    public void ResourceWarningDigest_IsHiddenWhenChartInstallDestinationIsSet()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

    [TestMethod]
    public void WarningCollection_UsesCallbackAndInstallDestinationWithoutBmsFileOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

    [TestMethod]
    public void StructuredWarnings_DriveHighlightAndDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

    [TestMethod]
    public void StructuredWarningMutators_UpdateStructuredWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstallEstimationLowConfidence), ChartWarning.Create(ChartWarningKind.InstallEstimationLowConfidence, Resources.WarningDigest_InstallEstimationLowConfidence)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile)] };

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationLowConfidence));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Category != ChartWarningCategory.InstallEstimation)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart)] };

        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationLowConfidence));
        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
    }

    [TestMethod]
    public void InstalledDestinationResolveFailed_IsNotLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstalledDestinationResolveFailed), ChartWarning.Create(ChartWarningKind.InstalledDestinationResolveFailed, Resources.Warning_InstalledDestinationResolveFailed)] };

        Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
    }

    [TestMethod]
    public void UnsupportedResourcePath_IsNotLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.UnsupportedResourcePath), ChartWarning.Create(ChartWarningKind.UnsupportedResourcePath, Resources.Warning_UnsupportedResourcePath)] };

        Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsFalse(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
        Assert.AreEqual("[1] リソースパス非対応", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), Resources.Warning_UnsupportedResourcePath);
    }

    [TestMethod]
    public void InstalledDestinationAmbiguous_IsLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstalledDestinationAmbiguous), ChartWarning.Create(ChartWarningKind.InstalledDestinationAmbiguous, Resources.Warning_InstalledDestinationAmbiguous)] };

        Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
        StringAssert.Contains(ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination), Resources.WarningDigest_InstalledDestinationAmbiguous);
    }

    [TestMethod]
    public void InstalledDestinationAutoAppliedAmbiguous_IsLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous), ChartWarning.Create(ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, Resources.Warning_InstalledDestinationAutoAppliedAmbiguous)] };

        Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
        StringAssert.Contains(ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination), Resources.WarningDigest_InstalledDestinationAutoAppliedAmbiguous);
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), Resources.Warning_InstalledDestinationAutoAppliedAmbiguous.Split('\n')[0]);
    }

    [TestMethod]
    public void ChartInfoParseFailure_HighlightsAndUsesDedicatedDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ChartInfoParseFailure), ChartWarning.Create(ChartWarningKind.ChartInfoParseFailure, string.Format(Resources.Warning_ChartInfoParseFailure, "InvalidDataException", "開始BPM未定義"))] };

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual("[1] メタデータ解析エラー", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "InvalidDataException");
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "開始BPM未定義");
    }

    [TestMethod]
    public void Lr2PathEncodingUnsupported_HighlightsAndUsesDedicatedDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathEncodingUnsupported), ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported)] };

        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual("[1] LR2パス非対応", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "Shift_JIS");
    }

    [TestMethod]
    public void Lr2CompatibilityWarnings_HighlightAndUseDedicatedDigests()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathTooLong), ChartWarning.Create(ChartWarningKind.Lr2PathTooLong, Resources.Warning_Lr2PathTooLong)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2ResourcePathUnsupported), ChartWarning.Create(ChartWarningKind.Lr2ResourcePathUnsupported, Resources.Warning_Lr2ResourcePathUnsupported)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2ResourcePathTooLong), ChartWarning.Create(ChartWarningKind.Lr2ResourcePathTooLong, Resources.Warning_Lr2ResourcePathTooLong)] };

        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual("[3] LR2パス長超過, LR2リソース非対応, LR2リソースパス長超過", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "パス長制限");
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(file.Warnings), "リソースパス");
    }

    [TestMethod]
    public void ClearWarning_RemovesMatchingStructuredWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty();
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.NestedChartFileInPackage), ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage)] };

        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart)] };

        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
        Assert.AreEqual("[1] サブフォルダ譜面", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    [TestMethod]
    public void ReplaceAll_CopiesStructuredWarningsToSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var source = new ChartWarningCollection(() => { }, () => string.Empty);
        source.Set(ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage));
        source.Set(ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "wav missing"));
        var copy = new ChartWarningCollection(() => { }, () => string.Empty);

        copy.ReplaceAll(source.ToStructuredList());

        Assert.IsTrue(copy.Contains(ChartWarningKind.NestedChartFileInPackage));
        Assert.IsTrue(copy.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(source.BuildDigestText(), copy.BuildDigestText());
    }

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
