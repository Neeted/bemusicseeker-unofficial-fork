using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
            "PlaylistSymbols",
            "CharcterEncoding");
    }

    [TestMethod]
    public void Constructor_AssignsSharedAdjustedDefaultWidths()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        Assert.AreEqual(50, settings.Mode.Width);
        Assert.AreEqual(60, settings.Rate.Width);
        Assert.AreEqual(60, settings.ChartDuration.Width);
    }

    [TestMethod]
    public void Constructor_InstallAndFullScanDefaultsMatchInitialColumnOrder()
    {
        string[] expected =
        [
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
        ];

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
    public void Constructor_PlayHistoryDefaultsMatchInitialColumnOrder()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);

        AssertVisibleColumnOrder(
            settings,
            "PlayHistoryPlayedAt",
            "PlayHistoryFolderLabels",
            "Title",
            "PlayHistoryBestClear",
            "PlayHistoryBestDjLevel",
            "PlayHistoryBestRate",
            "PlayHistoryBestExscore",
            "PlayHistoryBestBp",
            "PlayHistoryBestCombo",
            "PlayHistoryKind",
            "PlayHistoryOption",
            "PlayHistoryOpHistory",
            "PlayHistoryPlayExscore",
            "PlayHistoryJudges");
        Assert.AreEqual(90, settings.PlayHistoryBestDjLevel.Width);
        Assert.AreEqual(90, settings.PlayHistoryBestRate.Width);
        Assert.AreEqual(90, settings.PlayHistoryBestExscore.Width);
        Assert.AreEqual(90, settings.PlayHistoryBestBp.Width);
        Assert.AreEqual(90, settings.PlayHistoryBestCombo.Width);
    }

    [TestMethod]
    public void Constructor_PlayHistoryHidesMainOnlyLayouts()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);

        Assert.AreEqual(Visibility.Hidden, settings.Status.Visibility);
        Assert.AreEqual(Visibility.Hidden, settings.Folder.Visibility);
        Assert.AreEqual(Visibility.Hidden, settings.Path.Visibility);
        Assert.AreEqual(Visibility.Hidden, settings.Clear.Visibility);
        Assert.AreEqual(Visibility.Hidden, settings.Rank.Visibility);
        Assert.AreEqual(Visibility.Hidden, settings.Rate.Visibility);
        Assert.AreEqual(Visibility.Hidden, settings.Bp.Visibility);
    }

    [TestMethod]
    public void Constructor_AssignsUniqueDisplayIndexToHiddenColumns()
    {
        foreach (CustomTableColumnSettings.ViewKind ViewKind in Enum.GetValues(typeof(CustomTableColumnSettings.ViewKind)))
        {
            var settings = new CustomTableColumnSettings(ViewKind);
            int[] displayIndexes = [.. GetActiveLayouts(settings).Select((item) => item.Layout.DisplayIndex)];

            Assert.AreEqual(displayIndexes.Length, displayIndexes.Distinct().Count(), ViewKind.ToString());
        }
    }

    [TestMethod]
    public void MainViewUpdateMode_MaintenanceValuesRemainStable()
    {
        Assert.AreEqual(40, (int)MainViewUpdateMode.ChartInfoParseErrorFilterSelected);
        Assert.AreEqual(255, (int)MainViewUpdateMode.UpdatedNone);
    }

    [TestMethod]
    public void EnsureChartInfoColumnDefaults_CompletesMissingLayoutsWithoutOverwritingExistingChoices()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
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
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD)
        {
            Status = null
        };

        settings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.STANDARD);

        Assert.IsNotNull(settings.Status);
        Assert.AreEqual(Visibility.Visible, settings.Status.Visibility);
        Assert.AreEqual(0, settings.Status.DisplayIndex);
        Assert.AreEqual(18, settings.Status.Width);
    }

    [TestMethod]
    public void EnsurePlayHistoryColumnDefaults_CompletesMissingLayoutsWithoutOverwritingExistingChoices()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        settings.PlayHistoryBestExscore.Visibility = Visibility.Visible;
        settings.PlayHistoryBestExscore.DisplayIndex = 77;
        settings.PlayHistoryJudges = null;

        settings.EnsurePlayHistoryColumnDefaults();

        Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, settings.Kind);
        Assert.IsNotNull(settings.PlayHistoryJudges);
        Assert.AreEqual(Visibility.Visible, settings.PlayHistoryBestExscore.Visibility);
        Assert.AreEqual(77, settings.PlayHistoryBestExscore.DisplayIndex);
        Assert.IsTrue(GetActiveLayouts(settings).All(item => item.Layout.DisplayIndex >= 0), "missing display index remains");
    }

    [TestMethod]
    public void EnsureColumnSettingDefaults_RecreatesAndCompletesPlayHistorySettings()
    {
        var settings = new Settings();
        var existing = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        existing.PlayHistoryBestExscore.Visibility = Visibility.Visible;
        existing.PlayHistoryBestExscore.DisplayIndex = 77;
        existing.PlayHistoryJudges = null;
        settings.PlayHistoryCustomTableColumnSettings = existing;

        Settings.EnsureColumnSettingDefaults(settings);

        Assert.AreSame(existing, settings.PlayHistoryCustomTableColumnSettings);
        Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, settings.PlayHistoryCustomTableColumnSettings.Kind);
        Assert.IsNotNull(settings.PlayHistoryCustomTableColumnSettings.PlayHistoryJudges);
        Assert.AreEqual(Visibility.Visible, settings.PlayHistoryCustomTableColumnSettings.PlayHistoryBestExscore.Visibility);
        Assert.AreEqual(77, settings.PlayHistoryCustomTableColumnSettings.PlayHistoryBestExscore.DisplayIndex);

        settings.PlayHistoryCustomTableColumnSettings = null;
        Settings.EnsureColumnSettingDefaults(settings);

        Assert.IsNotNull(settings.PlayHistoryCustomTableColumnSettings);
        Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, settings.PlayHistoryCustomTableColumnSettings.Kind);
        AssertVisibleColumnOrder(
            settings.PlayHistoryCustomTableColumnSettings,
            "PlayHistoryPlayedAt",
            "PlayHistoryFolderLabels",
            "Title",
            "PlayHistoryBestClear",
            "PlayHistoryBestDjLevel",
            "PlayHistoryBestRate",
            "PlayHistoryBestExscore",
            "PlayHistoryBestBp",
            "PlayHistoryBestCombo",
            "PlayHistoryKind",
            "PlayHistoryOption",
            "PlayHistoryOpHistory",
            "PlayHistoryPlayExscore",
            "PlayHistoryJudges");
    }

    private static void AssertVisibleColumnOrder(CustomTableColumnSettings settings, params string[] expectedNames)
    {
        string[] actualNames = [.. GetActiveLayouts(settings)
            .Where((item) => item.Layout.Visibility == Visibility.Visible)
            .OrderBy((item) => item.Layout.DisplayIndex)
            .Select((item) => item.Name)];

        CollectionAssert.AreEqual(expectedNames, actualNames);
    }

    private static IReadOnlyList<(string Name, CustomTableColumnSettings.ColumnLayout Layout)> GetLayouts(CustomTableColumnSettings settings)
    {
        return [.. typeof(CustomTableColumnSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where((property) => property.PropertyType == typeof(CustomTableColumnSettings.ColumnLayout))
            .Select((property) => (property.Name, Layout: (CustomTableColumnSettings.ColumnLayout)property.GetValue(settings)))];
    }

    private static IReadOnlyList<(string Name, CustomTableColumnSettings.ColumnLayout Layout)> GetActiveLayouts(CustomTableColumnSettings settings)
    {
        HashSet<CustomTableColumnSettings.ColumnLayout> activeLayouts = [.. CustomTableColumnFactory.EnumerateMainColumnLayouts(settings)];
        return [.. GetLayouts(settings).Where((item) => item.Layout != null && activeLayouts.Contains(item.Layout))];
    }
}
