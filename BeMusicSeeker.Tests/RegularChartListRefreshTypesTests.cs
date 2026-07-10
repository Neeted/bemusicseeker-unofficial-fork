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
        ChartListSortSpecification sort = ChartListSortSpecification.Create(
            nameof(LibraryChartRow.rank),
            ListSortDirection.Descending,
            hasValue: true);

        var request = new RegularChartListRefreshRequest(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: "folder",
            MainViewUpdateMode.FolderFilterSelected,
            treeParameter: "tree-folder",
            includeBmsonRows: true,
            virtualSubsetRequiredFailure: false,
            keywordFilter: null,
            RegularChartModeFilter.SevenKeys,
            sort);

        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, request.Mode);
        Assert.AreEqual(MainViewUpdateMode.TreeViewFilterNotChanged, request.RequestedMode);
        Assert.AreEqual("folder", request.Parameter);
        Assert.AreEqual("tree-folder", request.TreeParameter);
        Assert.IsTrue(request.IncludeBmsonRows);
        Assert.IsFalse(request.VirtualSubsetRequiredFailure);
        Assert.AreEqual(string.Empty, request.KeywordFilter);
        Assert.AreEqual(RegularChartModeFilter.SevenKeys, request.ModeFilter);
        Assert.IsTrue(request.HasSortParameters);
        Assert.AreEqual(nameof(LibraryChartRow.rateDouble), request.SortColumnName);
        Assert.AreEqual(nameof(LibraryChartRow.rank), request.RequestedSortColumnName);
        Assert.AreEqual(ListSortDirection.Descending, request.SortDirection);
        Assert.AreEqual(nameof(LibraryChartRow.rank), request.Sort.RequestedColumnName);
        Assert.AreEqual(ListSortDirection.Descending, request.Sort.Direction);
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
    public void RefreshChartRowsView_DispatchesRegularProductionEntry()
    {
        string refreshChartRowsView = SourceTextTestHelper.ReadMainWindowViewModelMethodBody("private void RefreshChartRowsView(");
        string root = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(refreshChartRowsView, "ChartListRefreshCoordinator.ResolveRoute");
        StringAssert.Contains(refreshChartRowsView, "UpdateBmsFilesViewBindingMode(route.IsPlaylistTreeActive)");
        StringAssert.Contains(refreshChartRowsView, "RegisterPlaylistSourceBuildRequest(route.Mode, route.RequestedMode, parameter)");
        StringAssert.Contains(refreshChartRowsView, "regularChartListOwner.ApplyRegularView(");
        Assert.IsFalse(root.Contains("ApplyMainLibraryChartListView"));
        Assert.IsFalse(root.Contains("TryApplyVirtualDefaultNormalLibraryView"));
        Assert.IsFalse(root.Contains("TryApplyVirtualChartSubsetLibraryView"));
    }
}
