using System;
using System.Collections.Generic;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartListFilterViewModelTests
{
    [TestMethod]
    public void ModeFilter_RejectsNoneButRefreshesBindingState()
    {
        var filters = new ChartListFilterViewModel();
        int changeCount = 0;
        List<string> propertyNames = [];
        filters.ModeFilterChanged += (_, _) => changeCount++;
        filters.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        filters.ModeFilter = ChartModeFilter._7KEYS;
        filters.ModeFilter = ChartModeFilter.None;

        Assert.AreEqual(ChartModeFilter._7KEYS, filters.ModeFilter);
        Assert.AreEqual(1, changeCount);
        Assert.AreEqual(2, propertyNames.Count);
        Assert.AreEqual(nameof(ChartListFilterViewModel.ModeFilter), propertyNames[0]);
        Assert.AreEqual(nameof(ChartListFilterViewModel.ModeFilter), propertyNames[1]);
    }

    [TestMethod]
    public void CaptureSnapshot_PreservesRawKeywordAndCanonicalModeTogether()
    {
        var filters = new ChartListFilterViewModel();
        int changeCount = 0;
        filters.KeywordFilterChanged += (_, _) => changeCount++;

        filters.KeywordFilter = "  title:Alpha  ";
        filters.ModeFilter = ChartModeFilter._5KEYS | ChartModeFilter._14KEYS;
        ChartListFilterSnapshot snapshot = filters.CaptureSnapshot();

        Assert.AreEqual(1, changeCount);
        Assert.AreEqual("  title:Alpha  ", snapshot.KeywordFilter);
        Assert.AreEqual(ChartModeFilter._5KEYS | ChartModeFilter._14KEYS, snapshot.ModeFilter);
    }

    [TestMethod]
    public void MainWindowLegacyFilterProperties_ForwardToChartFilterOwner()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.ModeFilter = MainWindowViewModel.ModeFilterType._7KEYS;
        viewModel.KeywordFilter = " title:alpha ";

        Assert.AreEqual(ChartModeFilter._7KEYS, viewModel.ChartFilters.ModeFilter);
        Assert.AreEqual(" title:alpha ", viewModel.ChartFilters.KeywordFilter);
        Assert.AreEqual(MainWindowViewModel.ModeFilterType._7KEYS, viewModel.ModeFilter);
        Assert.AreEqual(" title:alpha ", viewModel.KeywordFilter);

        viewModel.ModeFilter = MainWindowViewModel.ModeFilterType.None;

        Assert.AreEqual(ChartModeFilter._7KEYS, viewModel.ChartFilters.ModeFilter);
    }
}
