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

        bool? state = PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
            [first, second],
            LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        Assert.AreEqual(true, state);
    }

    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_ReturnsNullWhenTablesAreMixed()
    {
        BMSTable enabled = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        BMSTable disabled = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        bool? state = PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
            [enabled, disabled],
            LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        Assert.IsNull(state);
    }

    [TestMethod]
    public void ApplyPlaylistSummaryCustomFolderOutputPatchToMask_KeepsIndeterminateValues()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.UserFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
        var patch = new PlaylistWorkspaceViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            UserFolder = null,
            ClearFolder = true
        };

        LR2SongDBExtended.playlist.CustomFolderType next = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);

        Assert.IsFalse(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.UserFolder));
        Assert.IsTrue(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.ClearFolder));
    }

    [TestMethod]
    public void ApplyPlaylistSummaryCustomFolderOutputPatchToMask_DisablesLevelFolderForFolderEntryUnit()
    {
        BMSTable table = CreateTable(
            LR2SongDBExtended.playlist.CustomFolderType.None,
            LR2SongDBExtended.playlist.EntryUnitType.Folder);
        var patch = new PlaylistWorkspaceViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            LevelFolder = true
        };

        LR2SongDBExtended.playlist.CustomFolderType next = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);

        Assert.IsFalse(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.LevelFolder));
    }

    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_TreatsOldAllFoldersMaskAsNewTypesEnabled()
    {
        BMSTable table = CreateTable((LR2SongDBExtended.playlist.CustomFolderType)0x7F);

        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder));
        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder));
        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder));
    }

    [TestMethod]
    public void PlaylistSummaryBulkEditDialogViewModel_AllowsLastPlaySortFolderRegardlessOfSchemaStatus()
    {
        var owner = MainWindowViewModelTestFactory.Create();
        var dialog = new PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel(
            owner.PlaylistWorkspace,
            []);

        dialog.OutputLastPlaySortFolder = true;

        Assert.IsTrue(dialog.CanApplyCustomFolderOutput);
    }

    [TestMethod]
    public void PlaylistPropertyDialog_DoesNotBindLastPlaySortFolderEnabledStateToSchemaStatus()
    {
        string xaml = ReadWorkspaceText("BeMusicSeeker", "Views", "PlaylistPropertyDialog.xaml");

        StringAssert.Contains(xaml, "PlaylistProp_ftype_all_songs");
        StringAssert.Contains(xaml, "PlaylistProp_ftype_last_play_sort");
        Assert.IsFalse(xaml.Contains("CanUseLastPlaySortFolder"));
    }

    [TestMethod]
    public void PlaylistSummaryBulkEditDialog_DoesNotBindLastPlaySortFolderEnabledStateToSchemaStatus()
    {
        string xaml = ReadWorkspaceText("BeMusicSeeker", "Views", "PlaylistSummaryBulkEditDialog.xaml");

        StringAssert.Contains(xaml, "PlaylistProp_ftype_all_songs");
        StringAssert.Contains(xaml, "PlaylistProp_ftype_last_play_sort");
        Assert.IsFalse(xaml.Contains("CanUseLastPlaySortFolder"));
    }

    [TestMethod]
    public void SettingDialog_CustomFolderOutputDefaultsExposeAllFolderTypeOptions()
    {
        string xaml = ReadWorkspaceText("BeMusicSeeker", "Views", "SettingDialog.xaml");

        StringAssert.Contains(xaml, "Playlist_output_default_folder_types");
        StringAssert.Contains(xaml, "DefaultOutputAllSongsFolder");
        StringAssert.Contains(xaml, "DefaultOutputUserFolder");
        StringAssert.Contains(xaml, "DefaultOutputLevelFolder");
        StringAssert.Contains(xaml, "DefaultOutputAlphabetFolder");
        StringAssert.Contains(xaml, "DefaultOutputClearFolder");
        StringAssert.Contains(xaml, "DefaultOutputDJLevelFolder");
        StringAssert.Contains(xaml, "DefaultOutputCategoryAllFolder");
        StringAssert.Contains(xaml, "DefaultOutputOtherFolder");
        StringAssert.Contains(xaml, "DefaultOutputRandomFolder");
        StringAssert.Contains(xaml, "DefaultOutputBpmSortFolder");
        StringAssert.Contains(xaml, "DefaultOutputBpSortFolder");
        StringAssert.Contains(xaml, "DefaultOutputPlayCountSortFolder");
        StringAssert.Contains(xaml, "DefaultOutputLastPlaySortFolder");
    }

    [TestMethod]
    public void MainWindowPlaylistPropertyDialogs_DoNotRefreshLastPlaySortSchemaStatus()
    {
        string source = SourceTextTestHelper.ReadMainWindowSourceText();

        Assert.IsFalse(source.Contains("RefreshLr2PlayHistorySchemaStatusForCustomFolderUiAsync"));
        Assert.IsFalse(source.Contains("CheckLastPlaySortCustomFolderAvailability"));
    }

    [TestMethod]
    public void ApplyPlaylistSummaryExternalSyncFlagForTable_KeepsInvalidUrlTableOffWhenEnabling()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);

        bool changed = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync: true);

        Assert.IsFalse(changed);
        Assert.IsFalse(table.is_external_sync);
    }

    [TestMethod]
    public void ApplyPlaylistSummaryExternalSyncFlagForTable_EnablesValidUrlTable()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        table.Header_url = new Uri("https://example.invalid/header.json", UriKind.Absolute);
        table.Data_url = new Uri("data.json", UriKind.Relative);

        bool changed = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync: true);

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
            && !File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
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
