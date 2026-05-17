using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
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

        Assert.AreEqual("[3] サブフォルダ譜面, リソース不足", file.WarningDigestText);
        StringAssert.Contains(file.WarningTooltipText, Resources.Warning_NestedChartFileInPackage);
        StringAssert.Contains(file.WarningTooltipText, "wav missing");
        StringAssert.Contains(file.WarningTooltipText, "bga missing");
    }

    [TestMethod]
    public void ClearWarningsByCategory_RemovesOnlyMatchingStructuredWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();
        file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "structured estimate");
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);

        file.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);

        Assert.IsFalse(file.WarningTooltipText.Contains("structured estimate"));
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
        Assert.AreEqual("[1] サブフォルダ譜面", file.WarningDigestText);
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
    }

    [TestMethod]
    public void ResourceWarningDigest_IsHiddenWhenInstallDestinationIsSet()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();
        file.SetWarning(ChartWarningKind.ResourceWavMissing, string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2));
        List<string> changedProperties = [];
        file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            changedProperties.Add(e.PropertyName);
        };

        file.instl_dst = "C:\\Installed";

        Assert.AreEqual(string.Empty, file.WarningDigestText);
        StringAssert.Contains(file.WarningTooltipText, "WAV");
        CollectionAssert.Contains(changedProperties, nameof(BMSFile.WarningDigestText));
    }

    [TestMethod]
    public void StructuredWarnings_DriveCompatibilityAliasesHighlightAndDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
        file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous");
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);

        Assert.IsTrue(file.HasZeroNoteMismatchWarning);
        Assert.IsTrue(file.HasLowConfidenceInstallWarning);
        Assert.IsTrue(file.IsHashDuplicated);
        Assert.IsTrue(file.HasHighlightedWarning);
        Assert.AreEqual("[3] ゼロノート不整合, 重複譜面, 推定先複数", file.WarningDigestText);

        file.ClearWarning(ChartWarningKind.ZeroNoteMismatch);
        file.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        file.ClearWarning(ChartWarningKind.DuplicateChart);

        Assert.IsFalse(file.HasZeroNoteMismatchWarning);
        Assert.IsFalse(file.HasLowConfidenceInstallWarning);
        Assert.IsFalse(file.IsHashDuplicated);
        Assert.IsFalse(file.HasHighlightedWarning);
        Assert.AreEqual(string.Empty, file.WarningDigestText);
    }

    [TestMethod]
    public void CompatibilityAliasSetters_MutateStructuredWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile
        {
            HasZeroNoteMismatchWarning = true,
            HasLowConfidenceInstallWarning = true,
            IsHashDuplicated = true
        };

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.InstallEstimationLowConfidence));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.DuplicateChart));

        file.HasZeroNoteMismatchWarning = false;
        file.HasLowConfidenceInstallWarning = false;
        file.IsHashDuplicated = false;

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

        Assert.IsFalse(file.HasLowConfidenceInstallWarning);
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.InstalledDestinationResolveFailed));
    }

    [TestMethod]
    public void InstalledDestinationAmbiguous_IsLowConfidenceAlias()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.InstalledDestinationAmbiguous, Resources.Warning_InstalledDestinationAmbiguous);

        Assert.IsTrue(file.HasLowConfidenceInstallWarning);
        Assert.IsTrue(file.HasHighlightedWarning);
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.InstalledDestinationAmbiguous));
        StringAssert.Contains(file.WarningDigestText, Resources.WarningDigest_InstalledDestinationAmbiguous);
    }

    [TestMethod]
    public void ChartInfoParseFailure_HighlightsAndUsesDedicatedDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(
            ChartWarningKind.ChartInfoParseFailure,
            string.Format(Resources.Warning_ChartInfoParseFailure, "InvalidDataException", "開始BPM未定義"));

        Assert.IsTrue(file.HasChartInfoParseFailureWarning);
        Assert.IsTrue(file.HasHighlightedWarning);
        Assert.AreEqual("[1] メタデータ解析エラー", file.WarningDigestText);
        StringAssert.Contains(file.WarningTooltipText, "InvalidDataException");
        StringAssert.Contains(file.WarningTooltipText, "開始BPM未定義");
    }

    [TestMethod]
    public void Lr2PathEncodingUnsupported_HighlightsAndUsesDedicatedDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile();

        file.SetWarning(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported);

        Assert.IsTrue(file.HasHighlightedWarning);
        Assert.AreEqual("[1] LR2パス非対応", file.WarningDigestText);
        StringAssert.Contains(file.WarningTooltipText, "Shift_JIS");
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
        Assert.AreEqual("[1] サブフォルダ譜面", file.WarningDigestText);
    }

    [TestMethod]
    public void CopyStructuredWarningsFrom_CopiesStructuredWarningsToSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var source = new BMSFile();
        source.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);
        source.SetWarning(ChartWarningKind.ResourceWavMissing, "wav missing");
        var copy = new BMSFile();

        copy.CopyStructuredWarningsFrom(source);

        Assert.IsTrue(copy.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
        Assert.IsTrue(copy.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(source.WarningDigestText, copy.WarningDigestText);
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
