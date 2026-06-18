using System;
using System.Collections.Generic;
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
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.ResourceWavMissing, "wav missing");
        file.SetWarning(ChartWarningKind.ResourceBgaMissing, "bga missing");
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);

        Assert.AreEqual("[3] サブフォルダ譜面, リソース不足", file.Warnings.BuildDigestText());
        StringAssert.Contains(file.Warnings.BuildTooltipText(), Resources.Warning_NestedChartFileInPackage);
        StringAssert.Contains(file.Warnings.BuildTooltipText(), "wav missing");
        StringAssert.Contains(file.Warnings.BuildTooltipText(), "bga missing");
    }

    [TestMethod]
    public void ClearWarningsByCategory_RemovesOnlyMatchingStructuredWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();
        file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "structured estimate");
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);

        file.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);

        Assert.IsFalse(file.Warnings.BuildTooltipText().Contains("structured estimate"));
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
        Assert.AreEqual("[1] サブフォルダ譜面", file.Warnings.BuildDigestText());
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
    }

    [TestMethod]
    public void ResourceWarningDigest_IsHiddenWhenChartInstallDestinationIsSet()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();
        file.SetWarning(ChartWarningKind.ResourceWavMissing, string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2));
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: true),
            "C:\\Installed",
            string.Empty,
            string.Empty,
            [],
            file.Warnings.ToStructuredList());

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
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
        file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous");
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
        Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.Warnings.HasHighlightedWarning);
        Assert.AreEqual("[3] ゼロノート不整合, 重複譜面, 推定先複数", file.Warnings.BuildDigestText());

        file.ClearWarning(ChartWarningKind.ZeroNoteMismatch);
        file.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        file.ClearWarning(ChartWarningKind.DuplicateChart);

        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
        Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsFalse(file.Warnings.HasHighlightedWarning);
        Assert.AreEqual(string.Empty, file.Warnings.BuildDigestText());
    }

    [TestMethod]
    public void StructuredWarningMutators_UpdateStructuredWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
        file.SetWarning(ChartWarningKind.InstallEstimationLowConfidence, Resources.WarningDigest_InstallEstimationLowConfidence);
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.InstallEstimationLowConfidence));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.DuplicateChart));

        file.ClearWarning(ChartWarningKind.ZeroNoteMismatch);
        file.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        file.ClearWarning(ChartWarningKind.DuplicateChart);

        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.InstallEstimationLowConfidence));
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
    }

    [TestMethod]
    public void InstalledDestinationResolveFailed_IsNotLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, Resources.Warning_InstalledDestinationResolveFailed);

        Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.InstalledDestinationResolveFailed));
    }

    [TestMethod]
    public void UnsupportedResourcePath_IsNotLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.UnsupportedResourcePath, Resources.Warning_UnsupportedResourcePath);

        Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsFalse(file.Warnings.HasHighlightedWarning);
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.UnsupportedResourcePath));
        Assert.AreEqual("[1] リソースパス非対応", file.Warnings.BuildDigestText());
        StringAssert.Contains(file.Warnings.BuildTooltipText(), Resources.Warning_UnsupportedResourcePath);
    }

    [TestMethod]
    public void InstalledDestinationAmbiguous_IsLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.InstalledDestinationAmbiguous, Resources.Warning_InstalledDestinationAmbiguous);

        Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(file.Warnings.HasHighlightedWarning);
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.InstalledDestinationAmbiguous));
        StringAssert.Contains(file.Warnings.BuildDigestText(), Resources.WarningDigest_InstalledDestinationAmbiguous);
    }

    [TestMethod]
    public void InstalledDestinationAutoAppliedAmbiguous_IsLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, Resources.Warning_InstalledDestinationAutoAppliedAmbiguous);

        Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(file));
        Assert.IsTrue(file.Warnings.HasHighlightedWarning);
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
        StringAssert.Contains(file.Warnings.BuildDigestText(), Resources.WarningDigest_InstalledDestinationAutoAppliedAmbiguous);
        StringAssert.Contains(file.Warnings.BuildTooltipText(), Resources.Warning_InstalledDestinationAutoAppliedAmbiguous.Split('\n')[0]);
    }

    [TestMethod]
    public void ChartInfoParseFailure_HighlightsAndUsesDedicatedDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(
            ChartWarningKind.ChartInfoParseFailure,
            string.Format(Resources.Warning_ChartInfoParseFailure, "InvalidDataException", "開始BPM未定義"));

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ChartInfoParseFailure));
        Assert.IsTrue(file.Warnings.HasHighlightedWarning);
        Assert.AreEqual("[1] メタデータ解析エラー", file.Warnings.BuildDigestText());
        StringAssert.Contains(file.Warnings.BuildTooltipText(), "InvalidDataException");
        StringAssert.Contains(file.Warnings.BuildTooltipText(), "開始BPM未定義");
    }

    [TestMethod]
    public void Lr2PathEncodingUnsupported_HighlightsAndUsesDedicatedDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported);

        Assert.IsTrue(file.Warnings.HasHighlightedWarning);
        Assert.AreEqual("[1] LR2パス非対応", file.Warnings.BuildDigestText());
        StringAssert.Contains(file.Warnings.BuildTooltipText(), "Shift_JIS");
    }

    [TestMethod]
    public void Lr2CompatibilityWarnings_HighlightAndUseDedicatedDigests()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.Lr2PathTooLong, Resources.Warning_Lr2PathTooLong);
        file.SetWarning(ChartWarningKind.Lr2ResourcePathUnsupported, Resources.Warning_Lr2ResourcePathUnsupported);
        file.SetWarning(ChartWarningKind.Lr2ResourcePathTooLong, Resources.Warning_Lr2ResourcePathTooLong);

        Assert.IsTrue(file.Warnings.HasHighlightedWarning);
        Assert.AreEqual("[3] LR2パス長超過, LR2リソース非対応, LR2リソースパス長超過", file.Warnings.BuildDigestText());
        StringAssert.Contains(file.Warnings.BuildTooltipText(), "パス長制限");
        StringAssert.Contains(file.Warnings.BuildTooltipText(), "リソースパス");
    }

    [TestMethod]
    public void ClearWarning_RemovesMatchingStructuredWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);

        file.ClearWarning(ChartWarningKind.DuplicateChart);

        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
        Assert.AreEqual("[1] サブフォルダ譜面", file.Warnings.BuildDigestText());
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
