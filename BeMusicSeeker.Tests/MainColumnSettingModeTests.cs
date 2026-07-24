using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainColumnSettingModeTests
{
    [TestMethod]
    public void ResolveMainColumnSettingMode_UsesCurrentTreeModeForIncrementalUpdates()
    {
        int currentTreeMode = (int)MainViewUpdateMode.PendingInstallFolderSelected;

        Assert.AreEqual((MainViewUpdateMode)currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.TreeViewFilterNotChanged, (MainViewUpdateMode)currentTreeMode));
        Assert.AreEqual((MainViewUpdateMode)currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.KeywordFilterUpdated, (MainViewUpdateMode)currentTreeMode));
        Assert.AreEqual((MainViewUpdateMode)currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.ModeFilterUpdated, (MainViewUpdateMode)currentTreeMode));
        Assert.AreEqual((MainViewUpdateMode)currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.SortUpdated, (MainViewUpdateMode)currentTreeMode));
    }

    [TestMethod]
    public void ResolveMainColumnSettingMode_UsesPlayHistoryForIncrementalUpdates()
    {
        int currentTreeMode = (int)MainViewUpdateMode.PlayHistorySelected;

        Assert.AreEqual((MainViewUpdateMode)currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.TreeViewFilterNotChanged, (MainViewUpdateMode)currentTreeMode));
        Assert.AreEqual((MainViewUpdateMode)currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.KeywordFilterUpdated, (MainViewUpdateMode)currentTreeMode));
        Assert.AreEqual((MainViewUpdateMode)currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.ModeFilterUpdated, (MainViewUpdateMode)currentTreeMode));
        Assert.AreEqual((MainViewUpdateMode)currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.SortUpdated, (MainViewUpdateMode)currentTreeMode));
    }

    [TestMethod]
    public void ResolveMainColumnSettingMode_KeepsExplicitTreeSelection()
    {
        int explicitMode = (int)MainViewUpdateMode.FolderFilterSelected;
        int currentTreeMode = (int)MainViewUpdateMode.PendingInstallFolderSelected;

        Assert.AreEqual((MainViewUpdateMode)explicitMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode((MainViewUpdateMode)explicitMode, (MainViewUpdateMode)currentTreeMode));
    }

    [TestMethod]
    public void MainChartListOperationContext_MapsPlayHistoryToDedicatedSection()
    {
        var mainChartList = new MainChartListViewModel();
        mainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);

        Assert.AreEqual(
            MainViewOperationSection.PlayHistory,
            mainChartList.CurrentOperationContext.OperationSection);
    }

    [TestMethod]
    public void IsPlayHistoryMainViewMode_TreatsIncrementalUpdatesAsPlayHistoryWhenSelected()
    {
        Assert.IsTrue(ChartListRefreshCoordinator.IsPlayHistoryMainViewMode(
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.FolderFilterSelected));
        Assert.IsTrue(ChartListRefreshCoordinator.IsPlayHistoryMainViewMode(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.PlayHistorySelected));
        Assert.IsFalse(ChartListRefreshCoordinator.IsPlayHistoryMainViewMode(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.PlayHistorySelected));
        Assert.IsFalse(ChartListRefreshCoordinator.IsPlayHistoryMainViewMode(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.FolderFilterSelected));
    }

    [TestMethod]
    public void ResolveMainColumnHeaderContextMenuResourceKey_UsesPlayHistoryMenuForPlayHistorySettings()
    {
        Assert.AreEqual(
            "playHistoryColumnHeaderContextMenu",
            MainWindow.ResolveMainColumnHeaderContextMenuResourceKey(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY)));
        Assert.AreEqual(
            "tableColumnHeaderContextMenu",
            MainWindow.ResolveMainColumnHeaderContextMenuResourceKey(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD)));
    }

    [TestMethod]
    public void MainChartListViewModel_ResolveRowDragKind_UsesPlaylistDropCandidateRowsForPlayHistorySettings()
    {
        var mainChartList = new MainChartListViewModel();
        mainChartList.ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        Assert.AreEqual(
            CustomTableRowDragKind.PlaylistDropCandidateRows,
            mainChartList.RowDragKind);
        mainChartList.ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        Assert.AreEqual(
            CustomTableRowDragKind.PlaylistDropCandidateRows,
            mainChartList.RowDragKind);
        mainChartList.ColumnsSettings = null;
        Assert.AreEqual(
            CustomTableRowDragKind.PlaylistDropCandidateRows,
            mainChartList.RowDragKind);
    }

}
