using BeMusicSeeker.ViewModels;
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
