using System.Globalization;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FilterConverterTests
{
    /// <summary>7/24/48KEYSのBindingを往復し、単独の解除と全選択から5だけを解除した残りを確認します。</summary>
    [TestMethod]
    [DataRow(ChartModeFilter._7KEYS, "_7KEYS", ChartModeFilter.None)]
    [DataRow(ChartModeFilter._24KEYS, "_24KEYS", ChartModeFilter.None)]
    [DataRow(ChartModeFilter._48KEYS, "_48KEYS", ChartModeFilter.None)]
    [DataRow(ChartModeFilter.All, "_5KEYS", ChartModeFilter._7KEYS | ChartModeFilter._9KEYS | ChartModeFilter._10KEYS | ChartModeFilter._14KEYS | ChartModeFilter._24KEYS | ChartModeFilter._48KEYS)]
    public void ModeFilterConverter_RoundTripsChartFilterBindingValue(ChartModeFilter initial, string parameter, ChartModeFilter expected)
    {
        var converter = new modeFilterConverter();

        object selected = converter.Convert(
            initial,
            typeof(bool),
            parameter,
            CultureInfo.InvariantCulture);
        object updated = converter.ConvertBack(false, typeof(ChartModeFilter), parameter, CultureInfo.InvariantCulture);

        Assert.AreEqual(true, selected);
        Assert.AreEqual(expected, updated);
    }

    /// <summary>所持完了の選択表示と未完了への入力を変換し、画面とworkspaceの条件を取り違えません。</summary>
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
