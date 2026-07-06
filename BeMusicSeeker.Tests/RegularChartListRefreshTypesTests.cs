using System.Collections.Generic;
using System.ComponentModel;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartListRefreshTypesTests
{
    [TestMethod]
    public void RegularChartListRefreshRequest_NormalizesSortInputs()
    {
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.rank),
            Direction = ListSortDirection.Descending
        };

        var request = new RegularChartListRefreshRequest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            MainWindowViewModel.viewUpdateMode.TreeViewFilterNotChanged,
            parameter: "folder",
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            treeParameter: "tree-folder",
            includeBmsonRows: true,
            virtualSubsetRequiredFailure: false,
            keywordFilter: null,
            MainWindowViewModel.ModeFilterType._7KEYS,
            sortParameters);

        Assert.AreEqual(MainWindowViewModel.viewUpdateMode.FolderFilterSelected, request.Mode);
        Assert.AreEqual(MainWindowViewModel.viewUpdateMode.TreeViewFilterNotChanged, request.RequestedMode);
        Assert.AreEqual("folder", request.Parameter);
        Assert.AreEqual("tree-folder", request.TreeParameter);
        Assert.IsTrue(request.IncludeBmsonRows);
        Assert.IsFalse(request.VirtualSubsetRequiredFailure);
        Assert.AreEqual(string.Empty, request.KeywordFilter);
        Assert.AreEqual(MainWindowViewModel.ModeFilterType._7KEYS, request.ModeFilter);
        Assert.IsTrue(request.HasSortParameters);
        Assert.AreEqual(nameof(LibraryChartRow.rateDouble), request.SortColumnName);
        Assert.AreEqual(nameof(LibraryChartRow.rank), request.RequestedSortColumnName);
        Assert.AreEqual(ListSortDirection.Descending, request.SortDirection);
        Assert.AreNotSame(sortParameters, request.SortParameters);
        Assert.AreEqual(nameof(LibraryChartRow.rank), request.SortParameters.ColumnsName);
        Assert.AreEqual(ListSortDirection.Descending, request.SortParameters.Direction);
    }

    [TestMethod]
    public void RegularChartListStageState_MaterializesNullAndPreservesListInstances()
    {
        List<LibraryChartRow> rows = [];

        List<LibraryChartRow> fromNull = RegularChartListStageState.Materialize(null);
        List<LibraryChartRow> fromList = RegularChartListStageState.Materialize(rows);
        RegularChartListStageState state = RegularChartListStageState.FromCaches(rows, null, fromList);

        Assert.AreEqual(0, fromNull.Count);
        Assert.AreSame(rows, fromList);
        Assert.AreEqual(0, state.FolderCount);
        Assert.AreEqual(0, state.KeywordCount);
        Assert.AreEqual(0, state.ModeCount);
        Assert.AreSame(rows, state.FolderRows);
        Assert.AreSame(fromList, state.ModeRows);
    }

    [TestMethod]
    public void RefreshChartRowsView_ConnectsRegularRequestAndStageState()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(viewModelCode, "new RegularChartListRefreshRequest(");
        StringAssert.Contains(viewModelCode, "regularRequest.VirtualSubsetRequiredFailure");
        StringAssert.Contains(viewModelCode, "RegularChartListStageState.Materialize(ChartRowsFolderView)");
        StringAssert.Contains(viewModelCode, "new RegularChartListSortContext");
        StringAssert.Contains(viewModelCode, "TryGetNormalLibrarySortCache = TryGetNormalLibrarySortCacheForCoordinator");
        StringAssert.Contains(viewModelCode, "RegularChartListSortCoordinator.ApplySort(regularRequest, regularStage, sortContext)");
    }
}
