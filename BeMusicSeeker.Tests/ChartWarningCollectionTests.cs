using BeMusicSeeker.Models;
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
            instl_dst = "C:\\Installed",
            warning = string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2)
        };

        Assert.AreEqual(string.Empty, file.WarningDigestText);
        StringAssert.Contains(file.WarningTooltipText, "WAV");
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
}
