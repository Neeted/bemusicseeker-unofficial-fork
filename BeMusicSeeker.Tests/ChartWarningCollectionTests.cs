using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartWarningCollectionTests
{
    [TestMethod]
    public void WarningDigestText_OrdersByPriorityAndDeduplicatesLabelsWhileCountingKinds()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile file = new BMSFile();

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
        BMSFile file = new BMSFile();
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
        BMSFile file = new BMSFile();
        file.SetWarning(ChartWarningKind.ResourceWavMissing, string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2));
        List<string> changedProperties = new List<string>();
        file.PropertyChanged += delegate(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            changedProperties.Add(e.PropertyName);
        };

        file.instl_dst = "C:\\Installed";

        Assert.AreEqual(string.Empty, file.WarningDigestText);
        StringAssert.Contains(file.WarningTooltipText, "WAV");
        CollectionAssert.Contains(changedProperties, nameof(BMSFile.WarningDigestText));
    }

    [TestMethod]
    public void CompatibilityFlags_CreateVirtualWarningsForHighlightAndDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile file = new BMSFile
        {
            HasZeroNoteMismatchWarning = true,
            HasLowConfidenceInstallWarning = true,
            IsHashDuplicated = true
        };

        Assert.IsTrue(file.HasHighlightedWarning);
        Assert.AreEqual("[3] ゼロノート不整合, 重複譜面, 導入先推定", file.WarningDigestText);

        file.HasZeroNoteMismatchWarning = false;
        file.HasLowConfidenceInstallWarning = false;
        file.IsHashDuplicated = false;

        Assert.IsFalse(file.HasHighlightedWarning);
        Assert.AreEqual(string.Empty, file.WarningDigestText);
    }

    [TestMethod]
    public void ClearWarning_RemovesMatchingStructuredWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile file = new BMSFile();
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
        BMSFile source = new BMSFile();
        source.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);
        source.SetWarning(ChartWarningKind.ResourceWavMissing, "wav missing");
        BMSFile copy = new BMSFile();

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
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();

                AssertNoWarningColumn(songDb, "song");
                AssertNoWarningColumn(songDb, "install");
                AssertNoWarningColumn(songDb, "maintenance");
            }
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
        string[] columnNames = songDb.Query<ColumnNameRow>("PRAGMA table_info(" + tableName + ");")
            .Select((ColumnNameRow row) => row.name)
            .ToArray();
        CollectionAssert.DoesNotContain(columnNames, "warning");
    }

    private sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }
}
