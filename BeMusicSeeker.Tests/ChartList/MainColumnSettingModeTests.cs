using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainColumnSettingModeTests
{
    /// <summary>保留一覧の絞込み・sortなど部分更新では現在のtree区分を列設定へ引き継ぎ、通常一覧の列へ誤って切り替えません。</summary>
    [TestMethod]
    public void ResolveMainColumnSettingMode_UsesCurrentTreeModeForIncrementalUpdates()
    {
        MainViewUpdateMode currentTreeMode = MainViewUpdateMode.PendingInstallFolderSelected;

        Assert.AreEqual(currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.TreeViewFilterNotChanged, currentTreeMode));
        Assert.AreEqual(currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.KeywordFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.ModeFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.SortUpdated, currentTreeMode));
    }

    /// <summary>履歴一覧の絞込み・sortなど部分更新でも履歴列を維持し、通常列設定へ切り替えません。</summary>
    [TestMethod]
    public void ResolveMainColumnSettingMode_UsesPlayHistoryForIncrementalUpdates()
    {
        MainViewUpdateMode currentTreeMode = MainViewUpdateMode.PlayHistorySelected;

        Assert.AreEqual(currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.TreeViewFilterNotChanged, currentTreeMode));
        Assert.AreEqual(currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.KeywordFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.ModeFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(MainViewUpdateMode.SortUpdated, currentTreeMode));
    }

    /// <summary>保留一覧から明示的に通常folderを選ぶと、古い保留列設定を引き継がず選択先の区分を使います。</summary>
    [TestMethod]
    public void ResolveMainColumnSettingMode_KeepsExplicitTreeSelection()
    {
        MainViewUpdateMode explicitMode = MainViewUpdateMode.FolderFilterSelected;
        MainViewUpdateMode currentTreeMode = MainViewUpdateMode.PendingInstallFolderSelected;

        Assert.AreEqual(explicitMode, ChartListRefreshCoordinator.ResolveMainColumnSettingMode(explicitMode, currentTreeMode));
    }

    /// <summary>履歴選択は専用操作sectionへ分類し、通常ライブラリの操作受付へ配送しません。</summary>
    [TestMethod]
    public void MainChartListOperationContext_MapsPlayHistoryToDedicatedSection()
    {
        var mainChartList = new MainChartListViewModel();
        mainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);

        Assert.AreEqual(
            MainViewOperationSection.PlayHistory,
            mainChartList.CurrentOperationContext.OperationSection);
    }

    /// <summary>明示選択先を優先し、部分更新時だけ現在の履歴選択を引き継ぐことで、履歴判定の持越しと欠落を防ぎます。</summary>
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

    /// <summary>履歴列と通常列から対応するcompiledヘッダーメニューを選び、異なる操作を表示する配送誤りを検出します。</summary>
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

    /// <summary>履歴・通常・未設定の一覧で譜面をplaylist取り込み候補としてドラッグできる種類を返し、列設定の違いで候補配送を失いません。</summary>
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
