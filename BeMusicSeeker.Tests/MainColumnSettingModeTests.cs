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
        int currentTreeMode = (int)MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected;

        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainWindowViewModel.viewUpdateMode.TreeViewFilterNotChanged, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainWindowViewModel.viewUpdateMode.KeywordFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainWindowViewModel.viewUpdateMode.ModeFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainWindowViewModel.viewUpdateMode.SortUpdated, currentTreeMode));
    }

    [TestMethod]
    public void ResolveMainColumnSettingMode_UsesPlayHistoryForIncrementalUpdates()
    {
        int currentTreeMode = (int)MainWindowViewModel.viewUpdateMode.PlayHistorySelected;

        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainWindowViewModel.viewUpdateMode.TreeViewFilterNotChanged, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainWindowViewModel.viewUpdateMode.KeywordFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainWindowViewModel.viewUpdateMode.ModeFilterUpdated, currentTreeMode));
        Assert.AreEqual(currentTreeMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest((int)MainWindowViewModel.viewUpdateMode.SortUpdated, currentTreeMode));
    }

    [TestMethod]
    public void ResolveMainColumnSettingMode_KeepsExplicitTreeSelection()
    {
        int explicitMode = (int)MainWindowViewModel.viewUpdateMode.FolderFilterSelected;
        int currentTreeMode = (int)MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected;

        Assert.AreEqual(explicitMode, MainWindowViewModel.ResolveMainColumnSettingModeForTest(explicitMode, currentTreeMode));
    }

    [TestMethod]
    public void ShouldReuseMainColumnSetting_ReusesSameResolvedModeWhenSettingsAreReady()
    {
        int resolvedMode = (int)MainWindowViewModel.viewUpdateMode.FolderFilterSelected;

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
            MainWindowViewModel.ResolveMainViewOperationSection(MainWindowViewModel.viewUpdateMode.PlayHistorySelected));
    }

    [TestMethod]
    public void IsPlayHistoryMainViewMode_TreatsIncrementalUpdatesAsPlayHistoryWhenSelected()
    {
        Assert.IsTrue(MainWindowViewModel.IsPlayHistoryMainViewModeForTest(
            MainWindowViewModel.viewUpdateMode.PlayHistorySelected,
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected));
        Assert.IsTrue(MainWindowViewModel.IsPlayHistoryMainViewModeForTest(
            MainWindowViewModel.viewUpdateMode.SortUpdated,
            MainWindowViewModel.viewUpdateMode.PlayHistorySelected));
        Assert.IsFalse(MainWindowViewModel.IsPlayHistoryMainViewModeForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            MainWindowViewModel.viewUpdateMode.PlayHistorySelected));
        Assert.IsFalse(MainWindowViewModel.IsPlayHistoryMainViewModeForTest(
            MainWindowViewModel.viewUpdateMode.SortUpdated,
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected));
    }

    [TestMethod]
    public void IsMainColumnSettingTargetReady_UsesPlayHistorySettingsForPlayHistoryMode()
    {
        var settings = new Settings
        {
            PlayHistoryCustomTableColumnSettings = null
        };

        Assert.IsFalse(MainWindowViewModel.IsMainColumnSettingTargetReadyForTest(
            MainWindowViewModel.viewUpdateMode.PlayHistorySelected,
            settings));

        settings.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);

        Assert.IsTrue(MainWindowViewModel.IsMainColumnSettingTargetReadyForTest(
            MainWindowViewModel.viewUpdateMode.PlayHistorySelected,
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
    public void ShouldReuseMainColumnSetting_DoesNotReuseWhenModeChangesOrSettingsAreMissing()
    {
        int resolvedMode = (int)MainWindowViewModel.viewUpdateMode.FolderFilterSelected;
        int otherMode = (int)MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected;

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
