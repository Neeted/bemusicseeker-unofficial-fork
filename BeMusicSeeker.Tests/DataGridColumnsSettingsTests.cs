using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DataGridColumnsSettingsTests
{
    [TestMethod]
    public void Constructor_PlaylistDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new dataGridColumnsSettings(dataGridColumnsSettings.viewType.PLAYLIST),
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
            new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD),
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
    public void Constructor_InstallAndFullScanDefaultsMatchInitialColumnOrder()
    {
        string[] expected =
        {
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

        AssertVisibleColumnOrder(new dataGridColumnsSettings(dataGridColumnsSettings.viewType.INSTALL), expected);
        AssertVisibleColumnOrder(new dataGridColumnsSettings(dataGridColumnsSettings.viewType.FULLSCAN), expected);
    }

    [TestMethod]
    public void Constructor_ZeroNoteDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new dataGridColumnsSettings(dataGridColumnsSettings.viewType.ZERO_NOTE),
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
            new dataGridColumnsSettings(dataGridColumnsSettings.viewType.DUPLICATE),
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
    public void Constructor_EncodingDefaultsMatchInitialColumnOrder()
    {
        AssertVisibleColumnOrder(
            new dataGridColumnsSettings(dataGridColumnsSettings.viewType.ENCODING),
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
        foreach (dataGridColumnsSettings.viewType viewType in Enum.GetValues(typeof(dataGridColumnsSettings.viewType)))
        {
            dataGridColumnsSettings settings = new dataGridColumnsSettings(viewType);
            int[] displayIndexes = GetLayouts(settings).Select((item) => item.Layout.DisplayIndex).ToArray();

            Assert.AreEqual(displayIndexes.Length, displayIndexes.Distinct().Count(), viewType.ToString());
        }
    }

    [TestMethod]
    public void EnsureChartInfoColumnDefaults_CompletesMissingLayoutsWithoutOverwritingExistingChoices()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.PLAYLIST);
        settings.EntryLevel.Visibility = Visibility.Visible;
        settings.EntryLevel.DisplayIndex = 77;
        settings.Level.Visibility = Visibility.Visible;
        settings.Level.DisplayIndex = 78;
        settings.ChartDifficulty = null;

        settings.EnsureChartInfoColumnDefaults(dataGridColumnsSettings.viewType.PLAYLIST);

        Assert.IsNotNull(settings.ChartDifficulty);
        Assert.AreEqual(Visibility.Visible, settings.EntryLevel.Visibility);
        Assert.AreEqual(77, settings.EntryLevel.DisplayIndex);
        Assert.AreEqual(Visibility.Visible, settings.Level.Visibility);
        Assert.AreEqual(78, settings.Level.DisplayIndex);
    }

    private static void AssertVisibleColumnOrder(dataGridColumnsSettings settings, params string[] expectedNames)
    {
        string[] actualNames = GetLayouts(settings)
            .Where((item) => item.Layout.Visibility == Visibility.Visible)
            .OrderBy((item) => item.Layout.DisplayIndex)
            .Select((item) => item.Name)
            .ToArray();

        CollectionAssert.AreEqual(expectedNames, actualNames);
    }

    private static IReadOnlyList<(string Name, dataGridColumnsSettings.dataGridColumnlayouts Layout)> GetLayouts(dataGridColumnsSettings settings)
    {
        return typeof(dataGridColumnsSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where((property) => property.PropertyType == typeof(dataGridColumnsSettings.dataGridColumnlayouts))
            .Select((property) => (property.Name, Layout: (dataGridColumnsSettings.dataGridColumnlayouts)property.GetValue(settings)))
            .ToArray();
    }
}
