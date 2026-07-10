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

        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainViewUpdateMode.TreeViewFilterNotChanged, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainViewUpdateMode.KeywordFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainViewUpdateMode.ModeFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainViewUpdateMode.SortUpdated, currentTreeMode));
    }

    [TestMethod]
    public void ResolveMainColumnSettingMode_UsesPlayHistoryForIncrementalUpdates()
    {
        int currentTreeMode = (int)MainViewUpdateMode.PlayHistorySelected;

        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainViewUpdateMode.TreeViewFilterNotChanged, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainViewUpdateMode.KeywordFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainViewUpdateMode.ModeFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainViewUpdateMode.SortUpdated, currentTreeMode));
    }

    [TestMethod]
    public void ResolveMainColumnSettingMode_KeepsExplicitTreeSelection()
    {
        int explicitMode = (int)MainViewUpdateMode.FolderFilterSelected;
        int currentTreeMode = (int)MainViewUpdateMode.PendingInstallFolderSelected;

        Assert.AreEqual(explicitMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest(explicitMode, currentTreeMode));
    }

    [TestMethod]
    public void ShouldReuseMainColumnSetting_ReusesSameResolvedModeWhenSettingsAreReady()
    {
        int resolvedMode = (int)MainViewUpdateMode.FolderFilterSelected;

        Assert.IsTrue(MainWindowViewModel.ShouldReuseMainColumnSettingForTest(
            resolvedMode,
            resolvedMode,
            targetSettingsReady: true,
            playlistSummarySettingsReady: true,
            isInit: false));
    }

    [TestMethod]
    public void ResolveMainViewOperationSection_MapsPlayHistoryToDedicatedSection()
    {
        Assert.AreEqual(
            MainWindowViewModel.MainViewOperationSection.PlayHistory,
            MainWindowViewModel.ResolveMainViewOperationSection(MainViewUpdateMode.PlayHistorySelected));
    }

    [TestMethod]
    public void IsPlayHistoryMainViewMode_TreatsIncrementalUpdatesAsPlayHistoryWhenSelected()
    {
        Assert.IsTrue(MainWindowViewModel.IsPlayHistoryMainViewModeForTest(
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.FolderFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsPlayHistoryMainViewModeForTest(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.PlayHistorySelected));
        Assert.IsFalse(MainWindowViewModel.IsPlayHistoryMainViewModeForTest(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.PlayHistorySelected));
        Assert.IsFalse(MainWindowViewModel.IsPlayHistoryMainViewModeForTest(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.FolderFilterSelected));
    }

    [TestMethod]
    public void IsMainColumnSettingTargetReady_UsesPlayHistorySettingsForPlayHistoryMode()
    {
        var settings = new Settings
        {
            PlayHistoryCustomTableColumnSettings = null
        };

        Assert.IsFalse(MainWindowViewModel.IsMainColumnSettingTargetReadyForTest(
            MainViewUpdateMode.PlayHistorySelected,
            settings));

        settings.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);

        Assert.IsTrue(MainWindowViewModel.IsMainColumnSettingTargetReadyForTest(
            MainViewUpdateMode.PlayHistorySelected,
            settings));
    }

    [TestMethod]
    public void ResolveMainColumnHeaderContextMenuResourceKey_UsesPlayHistoryMenuForPlayHistorySettings()
    {
        Assert.AreEqual(
            "playHistoryColumnHeaderContextMenu",
            MainWindow.ResolveMainColumnHeaderContextMenuResourceKeyForTest(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY)));
        Assert.AreEqual(
            "tableColumnHeaderContextMenu",
            MainWindow.ResolveMainColumnHeaderContextMenuResourceKeyForTest(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD)));
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

    [TestMethod]
    public void ShouldReuseMainColumnSetting_DoesNotReuseWhenModeChangesOrSettingsAreMissing()
    {
        int resolvedMode = (int)MainViewUpdateMode.FolderFilterSelected;
        int otherMode = (int)MainViewUpdateMode.PendingInstallFolderSelected;

        Assert.IsFalse(MainWindowViewModel.ShouldReuseMainColumnSettingForTest(
            resolvedMode,
            otherMode,
            targetSettingsReady: true,
            playlistSummarySettingsReady: true,
            isInit: false));
        Assert.IsFalse(MainWindowViewModel.ShouldReuseMainColumnSettingForTest(
            resolvedMode,
            resolvedMode,
            targetSettingsReady: false,
            playlistSummarySettingsReady: true,
            isInit: false));
        Assert.IsFalse(MainWindowViewModel.ShouldReuseMainColumnSettingForTest(
            resolvedMode,
            resolvedMode,
            targetSettingsReady: true,
            playlistSummarySettingsReady: false,
            isInit: false));
        Assert.IsFalse(MainWindowViewModel.ShouldReuseMainColumnSettingForTest(
            resolvedMode,
            resolvedMode,
            targetSettingsReady: true,
            playlistSummarySettingsReady: true,
            isInit: true));
    }
}
