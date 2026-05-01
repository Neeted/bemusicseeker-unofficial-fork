using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using System.Collections.Generic;
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
    public void ClearWarningsByCategory_RemovesOnlyMatchingStructuredAndLegacyWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile file = new BMSFile();
        file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "structured estimate");
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);
        file.warning = Resources.Warning_InstallEstimationAmbiguous
            .Replace("{0}", "C:\\A")
            .Replace("{1}", "C:\\B")
            + "\r\n"
            + Resources.Warning_NestedChartFileInPackage;

        file.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);

        Assert.IsFalse(file.WarningTooltipText.Contains("structured estimate"));
        Assert.IsFalse(file.warning.Contains(Resources.Warning_InstallEstimationAmbiguousPrefix));
        Assert.AreEqual("[1] サブフォルダ譜面", file.WarningDigestText);
        Assert.AreEqual(Resources.Warning_NestedChartFileInPackage, file.warning);
    }

    [TestMethod]
    public void LegacyWarningText_IsClassifiedForDigestAndTooltip()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile file = new BMSFile();
        file.warning = Resources.Warning_NestedChartFileInPackage
            + "\r\n"
            + string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2);

        Assert.AreEqual("[2] サブフォルダ譜面, リソース不足", file.WarningDigestText);
        StringAssert.Contains(file.WarningTooltipText, Resources.Warning_NestedChartFileInPackage);
        StringAssert.Contains(file.WarningTooltipText, "WAV");
    }

    [TestMethod]
    public void ResourceWarningDigest_IsHiddenWhenInstallDestinationIsSet()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile file = new BMSFile
        {
            warning = string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2)
        };
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
    public void ClearWarning_RemovesMatchingStructuredAndLegacyWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile file = new BMSFile();
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);
        file.warning = Resources.Warning_DuplicateBmsFile + "\r\n" + Resources.Warning_NestedChartFileInPackage;

        file.ClearWarning(ChartWarningKind.DuplicateChart);

        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
        Assert.AreEqual(Resources.Warning_NestedChartFileInPackage, file.warning);
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
}
