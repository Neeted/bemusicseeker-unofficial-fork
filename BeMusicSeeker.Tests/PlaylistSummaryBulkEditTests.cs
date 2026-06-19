using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSummaryBulkEditTests
{
    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_ReturnsValueWhenAllTablesMatch()
    {
        BMSTable first = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
        BMSTable second = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder);

        bool? state = MainWindowViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
            [first, second],
            LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        Assert.AreEqual(true, state);
    }

    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_ReturnsNullWhenTablesAreMixed()
    {
        BMSTable enabled = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        BMSTable disabled = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        bool? state = MainWindowViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
            [enabled, disabled],
            LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        Assert.IsNull(state);
    }

    [TestMethod]
    public void ApplyPlaylistSummaryCustomFolderOutputPatchToMask_KeepsIndeterminateValues()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.UserFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
        var patch = new MainWindowViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            UserFolder = null,
            ClearFolder = true
        };

        LR2SongDBExtended.playlist.CustomFolderType next = MainWindowViewModel.ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);

        Assert.IsFalse(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.UserFolder));
        Assert.IsTrue(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.ClearFolder));
    }

    [TestMethod]
    public void ApplyPlaylistSummaryCustomFolderOutputPatchToMask_DisablesLevelFolderForFolderEntryUnit()
    {
        BMSTable table = CreateTable(
            LR2SongDBExtended.playlist.CustomFolderType.None,
            LR2SongDBExtended.playlist.EntryUnitType.Folder);
        var patch = new MainWindowViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            LevelFolder = true
        };

        LR2SongDBExtended.playlist.CustomFolderType next = MainWindowViewModel.ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);

        Assert.IsFalse(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.LevelFolder));
    }

    [TestMethod]
    public void CanUseLastPlaySortFolder_ReturnsTrueOnlyWhenSchemaInstalled()
    {
        Assert.IsFalse(MainWindowViewModel.CanUseLastPlaySortFolder(null));
        foreach (Lr2PlayHistorySchemaStatus status in Enum.GetValues(typeof(Lr2PlayHistorySchemaStatus)))
        {
            Assert.AreEqual(
                status == Lr2PlayHistorySchemaStatus.Installed,
                MainWindowViewModel.CanUseLastPlaySortFolder(new Lr2PlayHistorySchemaCheckResult { Status = status }),
                status.ToString());
        }
    }

    [TestMethod]
    public void SuppressUnavailableLastPlaySortFolder_ClearsOnlyLastPlaySortFolder()
    {
        var patch = new MainWindowViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            UserFolder = true,
            LastPlaySortFolder = true
        };

        MainWindowViewModel.PlaylistSummaryCustomFolderOutputPatch next =
            MainWindowViewModel.SuppressUnavailableLastPlaySortFolder(patch, canUseLastPlaySortFolder: false);

        Assert.AreSame(patch, next);
        Assert.AreEqual(true, next.UserFolder);
        Assert.IsNull(next.LastPlaySortFolder);
    }

    [TestMethod]
    public void SuppressUnavailableLastPlaySortFolder_KeepsLastPlaySortFolderWhenAvailable()
    {
        var patch = new MainWindowViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            LastPlaySortFolder = true
        };

        MainWindowViewModel.PlaylistSummaryCustomFolderOutputPatch next =
            MainWindowViewModel.SuppressUnavailableLastPlaySortFolder(patch, canUseLastPlaySortFolder: true);

        Assert.AreSame(patch, next);
        Assert.AreEqual(true, next.LastPlaySortFolder);
    }

    [TestMethod]
    public void PlaylistSummaryBulkEditDialogViewModel_IgnoresLastPlaySortFolderWhenUnavailable()
    {
        var owner = new MainWindowViewModel();
        owner.settingDialog.ApplyLr2PlayHistorySchemaCheckResult(new Lr2PlayHistorySchemaCheckResult
        {
            Status = Lr2PlayHistorySchemaStatus.SkippedProfile
        });
        var dialog = new MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel(owner, []);

        dialog.OutputLastPlaySortFolder = true;

        Assert.IsFalse(dialog.CanUseLastPlaySortFolder);
        Assert.IsFalse(dialog.CanApplyCustomFolderOutput);

        dialog.OutputUserFolder = true;

        Assert.IsTrue(dialog.CanApplyCustomFolderOutput);
    }

    [TestMethod]
    public void PlaylistPropertyDialog_BindsLastPlaySortFolderEnabledStateToSchemaStatus()
    {
        string xaml = ReadWorkspaceText("BeMusicSeeker", "Views", "PlaylistPropertyDialog.xaml");

        StringAssert.Contains(xaml, "PlaylistProp_ftype_last_play_sort");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding settingDialog.CanUseLastPlaySortFolder, Mode=OneWay}\"");
    }

    [TestMethod]
    public void PlaylistSummaryBulkEditDialog_BindsLastPlaySortFolderEnabledStateToSchemaStatus()
    {
        string xaml = ReadWorkspaceText("BeMusicSeeker", "Views", "PlaylistSummaryBulkEditDialog.xaml");

        StringAssert.Contains(xaml, "PlaylistProp_ftype_last_play_sort");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding playlistSummaryBulkEditDialog.CanUseLastPlaySortFolder, Mode=OneWay}\"");
    }

    [TestMethod]
    public void MainWindowRefreshLastPlaySortSchemaStatus_DropsStalePlaylistResult()
    {
        string source = ReadWorkspaceText("BeMusicSeeker", "Views", "MainWindow.cs");

        StringAssert.Contains(source, "BMSPlaylist expectedPlaylist = viewModel?.GetActiveBMSPlaylistForCustomFolderOutput();");
        StringAssert.Contains(source, "ReferenceEquals(expectedPlaylist, viewModel.GetActiveBMSPlaylistForCustomFolderOutput())");
    }

    [TestMethod]
    public void MainWindowCreateNewPlaylist_RefreshesLastPlaySortSchemaBeforeCreate()
    {
        string source = ReadWorkspaceText("BeMusicSeeker", "Views", "MainWindow.cs");
        int handlerIndex = source.IndexOf("private async void treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick", StringComparison.Ordinal);
        int refreshIndex = source.IndexOf("await RefreshLr2PlayHistorySchemaStatusForCustomFolderUiAsync(viewModel)", handlerIndex, StringComparison.Ordinal);
        int createIndex = source.IndexOf("viewModel.CreateBMSTable()", handlerIndex, StringComparison.Ordinal);

        Assert.IsTrue(handlerIndex >= 0);
        Assert.IsTrue(refreshIndex > handlerIndex);
        Assert.IsTrue(createIndex > refreshIndex);
    }

    [TestMethod]
    public void ApplyPlaylistSummaryExternalSyncFlagForTable_KeepsInvalidUrlTableOffWhenEnabling()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);

        bool changed = MainWindowViewModel.ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync: true);

        Assert.IsFalse(changed);
        Assert.IsFalse(table.is_external_sync);
    }

    [TestMethod]
    public void ApplyPlaylistSummaryExternalSyncFlagForTable_EnablesValidUrlTable()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        table.Header_url = new Uri("https://example.invalid/header.json", UriKind.Absolute);
        table.Data_url = new Uri("data.json", UriKind.Relative);

        bool changed = MainWindowViewModel.ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync: true);

        Assert.IsTrue(changed);
        Assert.IsTrue(table.is_external_sync);
    }

    private static BMSTable CreateTable(
        LR2SongDBExtended.playlist.CustomFolderType ignoreFolderOutput,
        LR2SongDBExtended.playlist.EntryUnitType entryUnitType = LR2SongDBExtended.playlist.EntryUnitType.File)
    {
        return new BMSTable
        {
            ignore_folder_output = ignoreFolderOutput,
            entry_type = entryUnitType
        };
    }

    private static string ReadWorkspaceText(params string[] pathParts)
    {
        DirectoryInfo directory = new(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null
            && !File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker-decomp.sln")))
        {
            directory = directory.Parent;
        }
        if (directory == null)
        {
            Assert.Fail("workspace root was not found");
            throw new InvalidOperationException("workspace root was not found");
        }
        string path = directory.FullName;
        foreach (string part in pathParts)
        {
            path = Path.Combine(path, part);
        }
        return File.ReadAllText(path);
    }
}
