using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableColumnSettingsTests
{
    [TestMethod]
    public void Constructor_PlaylistDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST),
            "Status",
            "Folder",
            "Title",
            "Artist",
            "Url1",
            "Url2",
            "Comment",
            "Clear",
            "Rank",
            "Rate",
            "Bp",
            "ChartJudge",
            "ChartJudgePercent",
            "Notes",
            "ChartLongNotes",
            "ChartScratchNotes",
            "ChartMainBpm",
            "ChartMinBpm",
            "ChartMaxBpm",
            "ChartSoflan",
            "ChartTotal",
            "ChartTotalPerNote",
            "ChartDuration",
            "ChartFeature",
            "ChartDensity",
            "ChartPeakDensity",
            "ChartEndDensity",
            "PlaylistSymbols");
    }

    [TestMethod]
    public void Constructor_StandardDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD),
            "Status",
            "Title",
            "Artist",
            "Genre",
            "Mode",
            "Folder",
            "Path",
            "Clear",
            "Rank",
            "Rate",
            "Bp",
            "Level",
            "ChartDifficulty",
            "ChartJudge",
            "ChartJudgePercent",
            "Notes",
            "ChartLongNotes",
            "ChartScratchNotes",
            "ChartMainBpm",
            "ChartMinBpm",
            "ChartMaxBpm",
            "ChartSoflan",
            "ChartTotal",
            "ChartTotalPerNote",
            "ChartDuration",
            "ChartFeature",
            "ChartDensity",
            "ChartPeakDensity",
            "ChartEndDensity",
            "PlaylistSymbols");
    }

    [TestMethod]
    public void Constructor_UnregisteredDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.UNREGISTERED),
            "Status",
            "Warning",
            "Title",
            "Artist",
            "Genre",
            "Mode",
            "Folder",
            "Path",
            "PlaylistSymbols");
    }

    [TestMethod]
    public void Constructor_AssignsSharedAdjustedDefaultWidths()
    {
        CustomTableColumnSettings settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        Assert.AreEqual(50, settings.Mode.Width);
        Assert.AreEqual(60, settings.Rate.Width);
        Assert.AreEqual(60, settings.ChartDuration.Width);
    }

    [TestMethod]
    public void Constructor_InstallAndFullScanDefaultsMatchInitialColumnOrder()
    {
        string[] expected =
        {
            "Status",
            "PlaylistSymbols",
            "WavHealth",
            "BgaHealth",
            "MovieHealth",
            "Warning",
            "InstallDst",
            "InstallDstTitle",
            "Title",
            "InstallDstArtist",
            "Artist",
            "Mode",
            "Folder",
            "Path",
            "Hash"
        };

        AssertVisibleColumnOrder(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL), expected);
        AssertVisibleColumnOrder(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.FULLSCAN), expected);
    }

    [TestMethod]
    public void Constructor_ZeroNoteDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.ZERO_NOTE),
            "Status",
            "Title",
            "Artist",
            "Mode",
            "Warning",
            "Notes",
            "PlaylistSymbols",
            "Folder",
            "Path",
            "Hash");
    }

    [TestMethod]
    public void Constructor_DuplicateDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.DUPLICATE),
            "Status",
            "PlaylistSymbols",
            "WavHealth",
            "BgaHealth",
            "MovieHealth",
            "Warning",
            "Hash",
            "Title",
            "Artist",
            "Mode",
            "Path",
            "Folder");
    }

    [TestMethod]
    public void Constructor_ChartInfoParseErrorDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.CHART_INFO_PARSE_ERROR),
            "Status",
            "PlaylistSymbols",
            "WavHealth",
            "BgaHealth",
            "MovieHealth",
            "Warning",
            "Title",
            "Artist",
            "Mode",
            "Folder",
            "Path",
            "Hash");
    }

    [TestMethod]
    public void Constructor_EncodingDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.ENCODING),
            "Status",
            "CharcterEncoding",
            "Title",
            "Artist",
            "Genre",
            "Mode",
            "Folder",
            "Path");
    }

    [TestMethod]
    public void Constructor_AssignsUniqueDisplayIndexToHiddenColumns()
    {
        foreach (CustomTableColumnSettings.ViewKind ViewKind in Enum.GetValues(typeof(CustomTableColumnSettings.ViewKind)))
        {
            CustomTableColumnSettings settings = new CustomTableColumnSettings(ViewKind);
            int[] displayIndexes = GetLayouts(settings).Select((item) => item.Layout.DisplayIndex).ToArray();

            Assert.AreEqual(displayIndexes.Length, displayIndexes.Distinct().Count(), ViewKind.ToString());
        }
    }

    [TestMethod]
    public void MaintenanceFilterType_ChartInfoParseErrorKeepsExplicitValue()
    {
        Assert.AreEqual(40, (int)MainWindowViewModel.MaintenanceFilterType.ChartInfoParseErrorFilter);
        Assert.AreEqual(255, (int)MainWindowViewModel.MaintenanceFilterType.FilterNone);
    }

    [TestMethod]
    public void EnsureChartInfoColumnDefaults_CompletesMissingLayoutsWithoutOverwritingExistingChoices()
    {
        CustomTableColumnSettings settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
        settings.EntryLevel.Visibility = Visibility.Visible;
        settings.EntryLevel.DisplayIndex = 77;
        settings.Level.Visibility = Visibility.Visible;
        settings.Level.DisplayIndex = 78;
        settings.ChartDifficulty = null;

        settings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.PLAYLIST);

        Assert.AreEqual(Visibility.Visible, settings.Status.Visibility);
        Assert.AreEqual(0, settings.Status.DisplayIndex);
        Assert.AreEqual(18, settings.Status.Width);
        Assert.IsNotNull(settings.ChartDifficulty);
        Assert.AreEqual(Visibility.Visible, settings.EntryLevel.Visibility);
        Assert.AreEqual(77, settings.EntryLevel.DisplayIndex);
        Assert.AreEqual(Visibility.Visible, settings.Level.Visibility);
        Assert.AreEqual(78, settings.Level.DisplayIndex);
    }

    [TestMethod]
    public void EnsureChartInfoColumnDefaults_RecreatesMissingStatusLayout()
    {
        CustomTableColumnSettings settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD)
        {
            Status = null
        };

        settings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.STANDARD);

        Assert.IsNotNull(settings.Status);
        Assert.AreEqual(Visibility.Visible, settings.Status.Visibility);
        Assert.AreEqual(0, settings.Status.DisplayIndex);
        Assert.AreEqual(18, settings.Status.Width);
    }

    private static void AssertVisibleColumnOrder(CustomTableColumnSettings settings, params string[] expectedNames)
    {
        string[] actualNames = GetLayouts(settings)
            .Where((item) => item.Layout.Visibility == Visibility.Visible)
            .OrderBy((item) => item.Layout.DisplayIndex)
            .Select((item) => item.Name)
            .ToArray();

        CollectionAssert.AreEqual(expectedNames, actualNames);
    }

    private static IReadOnlyList<(string Name, CustomTableColumnSettings.ColumnLayout Layout)> GetLayouts(CustomTableColumnSettings settings)
    {
        return typeof(CustomTableColumnSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where((property) => property.PropertyType == typeof(CustomTableColumnSettings.ColumnLayout))
            .Select((property) => (property.Name, Layout: (CustomTableColumnSettings.ColumnLayout)property.GetValue(settings)))
            .ToArray();
    }
}
