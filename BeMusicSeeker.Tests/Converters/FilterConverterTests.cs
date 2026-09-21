using System.Globalization;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FilterConverterTests
{
    [TestMethod]
    public void ModeFilterConverter_RoundTripsChartFilterBindingValue()
    {
        var converter = new modeFilterConverter();

        object selected = converter.Convert(
            ChartModeFilter._7KEYS,
            typeof(bool),
            "_7KEYS",
            CultureInfo.InvariantCulture);
        object updated = converter.ConvertBack(false, typeof(ChartModeFilter), "_7KEYS", CultureInfo.InvariantCulture);

        Assert.AreEqual(true, selected);
        Assert.AreEqual(ChartModeFilter.None, updated);
    }

    [TestMethod]
    public void PlaylistOwnedFilterConverter_RoundTripsWorkspaceBindingValue()
    {
        var converter = new playlistSummaryOwnedFilterConverter();

        object selected = converter.Convert(
            PlaylistOwnedFilter.OwnedComplete,
            typeof(bool),
            "OwnedComplete",
            CultureInfo.InvariantCulture);
        object updated = converter.ConvertBack(
            true,
            typeof(PlaylistOwnedFilter),
            "OwnedIncomplete",
            CultureInfo.InvariantCulture);

        Assert.AreEqual(true, selected);
        Assert.AreEqual(PlaylistOwnedFilter.OwnedIncomplete, updated);
    }
}
