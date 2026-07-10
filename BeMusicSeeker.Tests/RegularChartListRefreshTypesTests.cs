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
    public void RefreshChartRowsView_DispatchesMainLibraryWorkflow()
    {
        string refreshChartRowsView = SourceTextTestHelper.ReadMainWindowViewModelMethodBody("private void RefreshChartRowsView(");

        StringAssert.Contains(refreshChartRowsView, "ChartListRefreshCoordinator.ResolveRoute");
        StringAssert.Contains(refreshChartRowsView, "UpdateBmsFilesViewBindingMode(route.IsPlaylistTreeActive)");
        StringAssert.Contains(refreshChartRowsView, "RegisterPlaylistSourceBuildRequest(route.Mode, route.RequestedMode, parameter)");
        StringAssert.Contains(refreshChartRowsView, "ApplyMainLibraryChartListView(route.Mode, route.RequestedMode, parameter, route.IncludeBmsonRows, viewBuildStopwatch)");
    }

    [TestMethod]
    public void ApplyMainLibraryChartListView_DelegatesMaterializedOwnership()
    {
        string mainLibraryWorkflow = SourceTextTestHelper.ReadMainWindowViewModelMethodBody("private void ApplyMainLibraryChartListView(");

        StringAssert.Contains(mainLibraryWorkflow, "new RegularChartListRefreshRequest(");
        StringAssert.Contains(mainLibraryWorkflow, "regularRequest.VirtualSubsetRequiredFailure");
        StringAssert.Contains(mainLibraryWorkflow, "regularChartListOwner.TryBeginRequest(");
        StringAssert.Contains(mainLibraryWorkflow, "regularChartListOwner.Build(");
        StringAssert.Contains(mainLibraryWorkflow, "regularChartListOwner.TryCommit(");
    }

    [TestMethod]
    public void VirtualDefaultRoute_DelegatesCompleteWorkflowWithoutRootCacheHelpers()
    {
        string route = SourceTextTestHelper.ReadMainWindowViewModelMethodBody(
            "private bool TryApplyVirtualDefaultNormalLibraryView(");
        string root = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");

        StringAssert.Contains(route, "regularChartListOwner.TryApplyVirtualNormalLibrary(");
        Assert.IsFalse(route.Contains("new ChartListVirtualView("));
        Assert.IsFalse(route.Contains("TryCommitVirtual("));
        Assert.IsFalse(root.Contains("private List<ChartListSourceRow> GetOrCreateVirtualNormalLibrarySourceRows("));
        Assert.IsFalse(root.Contains("private ChartListOrder GetOrCreateVirtualNormalLibraryOrder("));
        Assert.IsFalse(root.Contains("private static int[] ApplyVirtualNormalLibraryFilters("));
        Assert.IsFalse(root.Contains("private void RunVirtualNormalLibraryOrderPrewarm("));
    }
}
