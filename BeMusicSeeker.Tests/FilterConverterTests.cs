using System.Globalization;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FilterConverterTests
{
    [TestMethod]
    public void ModeFilterConverter_RoundTripsLegacyBindingValue()
    {
        var converter = new modeFilterConverter();

        object selected = converter.Convert(
            MainWindowViewModel.ModeFilterType._7KEYS,
            typeof(bool),
            "_7KEYS",
            CultureInfo.InvariantCulture);
        object updated = converter.ConvertBack(false, typeof(MainWindowViewModel.ModeFilterType), "_7KEYS", CultureInfo.InvariantCulture);

        Assert.AreEqual(true, selected);
        Assert.AreEqual(MainWindowViewModel.ModeFilterType.None, updated);
    }

    [TestMethod]
    public void PlaylistOwnedFilterConverter_RoundTripsLegacyBindingValue()
    {
        var converter = new playlistSummaryOwnedFilterConverter();

        object selected = converter.Convert(
            MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedComplete,
            typeof(bool),
            "OwnedComplete",
            CultureInfo.InvariantCulture);
        object updated = converter.ConvertBack(
            true,
            typeof(MainWindowViewModel.PlaylistSummaryOwnedFilterType),
            "OwnedIncomplete",
            CultureInfo.InvariantCulture);

        Assert.AreEqual(true, selected);
        Assert.AreEqual(MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedIncomplete, updated);
    }
}
