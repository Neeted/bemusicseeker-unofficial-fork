using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingDialogCustomFolderOutputBaseTests
{
    [TestMethod]
    public void HasPendingSettingChanges_UsesSnapshotDiffsAndReset()
    {
        bool previousShowRecommUpdatedMsg = Settings.Default.ShowRecommUpdatedMsg;
        bool previousShowDuplicateFileCheckConfirmMsg = Settings.Default.ShowDuplicateFileCheckConfirmMsg;
        try
        {
            var viewModel = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsFalse(dialog.HasPendingSettingChanges());

            Settings.Default.ShowRecommUpdatedMsg = !previousShowRecommUpdatedMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            Settings.Default.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            Settings.Default.ShowDuplicateFileCheckConfirmMsg = !previousShowDuplicateFileCheckConfirmMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            Settings.Default.ShowDuplicateFileCheckConfirmMsg = previousShowDuplicateFileCheckConfirmMsg;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            SetDialogField(dialog, "operationModeLR2DB", !GetDialogField<bool>(dialog, "tempOperationModeLR2DB"));

            Assert.IsTrue(dialog.HasPendingSettingChanges());

            SetDialogField(dialog, "operationModeLR2DB", GetDialogField<bool>(dialog, "tempOperationModeLR2DB"));
            typeof(SettingsDialogViewModel)
                .GetMethod("backupSavedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, null);

            Assert.AreEqual(previousShowRecommUpdatedMsg, Settings.Default.ShowRecommUpdatedMsg);
            Assert.AreEqual(previousShowDuplicateFileCheckConfirmMsg, Settings.Default.ShowDuplicateFileCheckConfirmMsg);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            Settings.Default.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
            Settings.Default.ShowDuplicateFileCheckConfirmMsg = previousShowDuplicateFileCheckConfirmMsg;
        }
    }

    [TestMethod]
    public void PendingSettingsRemainIndependentOfActiveLibraryProfile()
    {
        bool previousShowRecommUpdatedMsg = Settings.Default.ShowRecommUpdatedMsg;
        try
        {
            var viewModel = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            SetViewModelField(viewModel, "hasActiveLibraryProfile", true);

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            Settings.Default.ShowRecommUpdatedMsg = !Settings.Default.ShowRecommUpdatedMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
        }
        finally
        {
            Settings.Default.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
        }
    }

    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditor_DraftsSaveValidationAndIdMatching()
    {
        string previousJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (new LR2SongDBExtended(songDbPath))
            {
            }
            Settings.Default.PlayHistoryDisplayTargetSetsJson = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            BMSTable tableA = CreatePresetTable(101, "Satellite", "SAT");
            BMSTable tableB = CreatePresetTable(202, "Satellite", "SAT");
            tableA.symbol = string.Empty;
            SetViewModelTables(viewModel, songDbPath, [tableA, tableB]);

            dialog.AddPlayHistoryFolderDisplayPreset();

            PlayHistoryFolderDisplayPresetEditor preset = dialog.PlayHistoryFolderDisplayPresets.Single();
            Assert.AreSame(preset, dialog.SelectedPlayHistoryFolderDisplayPreset);
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.AreEqual(string.Empty, Settings.Default.PlayHistoryDisplayTargetSetsJson);
            PlayHistoryDisplayTargetSet draft = preset.ToTargetSet();
            Assert.AreEqual(Resources.Play_history_folder_display_preset_default_name, draft.Name);
            Assert.AreEqual(1, draft.Targets.Count);
            Assert.AreEqual(101, draft.Targets[0].PlaylistId);
            PlayHistoryFolderPresetPlaylistOption tableAOption = new(
                PlaylistTablePresentationSnapshot.From(tableA),
                isSelected: true,
                selectionChanged: null);
            StringAssert.Contains(tableAOption.DisplayName, "[SAT]");
            Assert.IsTrue(PlayHistoryFolderPresetPlaylistOption.Matches(
                PlaylistTablePresentationSnapshot.From(tableA),
                draft.Targets[0]));
            Assert.IsFalse(PlayHistoryFolderPresetPlaylistOption.Matches(
                PlaylistTablePresentationSnapshot.From(tableB),
                draft.Targets[0]));

            preset.Name = string.Empty;
            Assert.IsFalse(InvokeValidatePlayHistoryFolderDisplayPresets(dialog, out string emptyNameError));
            StringAssert.Contains(emptyNameError, Resources.Error_PlayHistoryFolderPresetNameEmpty);

            preset.Name = Resources.Play_history_folder_display_preset_default_name;
            dialog.AddPlayHistoryFolderDisplayPreset();
            PlayHistoryFolderDisplayPresetEditSession duplicateSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(dialog.SelectedPlayHistoryFolderDisplayPreset);
            PlayHistoryFolderDisplayPresetEditor secondPreset = dialog.SelectedPlayHistoryFolderDisplayPreset;
            string secondPresetNameBeforeDuplicateValidation = secondPreset.Name;
            PlayHistoryDisplayTargetSet secondPresetTargetSetBeforeDuplicateValidation = secondPreset.ToTargetSet();
            duplicateSession.Name = preset.Name;
            Assert.IsFalse(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(duplicateSession, out string duplicateNameError));
            StringAssert.Contains(duplicateNameError, Resources.Error_PlayHistoryFolderPresetDuplicateName.Split(':')[0]);
            Assert.AreEqual(secondPresetNameBeforeDuplicateValidation, secondPreset.Name);
            AssertTargetSetEquals(secondPresetTargetSetBeforeDuplicateValidation, secondPreset.ToTargetSet());

            PlayHistoryFolderDisplayPresetEditSession editSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(dialog.SelectedPlayHistoryFolderDisplayPreset);
            editSession.Name = "Second";
            editSession.PlaylistOptions.ToList().ForEach(option => option.IsSelected = false);
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            SetViewModelField(viewModel, "hasActiveLibraryProfile", true);
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsFalse(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(editSession, out string noPlaylistError));
            StringAssert.Contains(noPlaylistError, Resources.Error_PlayHistoryFolderPresetNoPlaylist.Split(':')[0]);
            Assert.AreEqual(secondPresetNameBeforeDuplicateValidation, secondPreset.Name);
            AssertTargetSetEquals(secondPresetTargetSetBeforeDuplicateValidation, secondPreset.ToTargetSet());
            editSession.PlaylistOptions.Last().IsSelected = true;
            Assert.IsTrue(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(editSession, out string applyPresetError), applyPresetError);
            Assert.IsTrue(InvokeValidatePlayHistoryFolderDisplayPresets(dialog, out string validPresetError), validPresetError);
            secondPreset.Targets.Add(new PlayHistoryDisplayTargetReference { PlaylistId = 999 });

            PlayHistoryFolderDisplayPresetEditSession canceledEditSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(secondPreset);
            PlayHistoryDisplayTargetSet appliedTargetSetBeforeCancel = secondPreset.ToTargetSet();
            canceledEditSession.Name = "Canceled";
            canceledEditSession.PlaylistOptions.ToList().ForEach(option => option.IsSelected = !option.IsSelected);
            Assert.AreEqual("Second", secondPreset.Name);
            AssertTargetSetEquals(appliedTargetSetBeforeCancel, secondPreset.ToTargetSet());

            Assert.IsTrue(dialog.PersistPlayHistoryFolderDisplayPresetsIfChanged());
            Assert.IsFalse(string.IsNullOrWhiteSpace(Settings.Default.PlayHistoryDisplayTargetSetsJson));
            StringAssert.Contains(Settings.Default.PlayHistoryDisplayTargetSetsJson, "\"PlaylistId\"");
            Assert.IsFalse(Settings.Default.PlayHistoryDisplayTargetSetsJson.Contains("PlaylistName"));
            Assert.IsFalse(Settings.Default.PlayHistoryDisplayTargetSetsJson.Contains("PlaylistSymbol"));
            Assert.IsFalse(Settings.Default.PlayHistoryDisplayTargetSetsJson.Contains("FolderLabel"));
            Assert.IsFalse(Settings.Default.PlayHistoryDisplayTargetSetsJson.Contains("999"));

            Assert.AreEqual(7, viewModel.PlayHistory.DisplayTargets.Count);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.All, viewModel.PlayHistory.DisplayTargets[0].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistory.DisplayTargets[1].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistory.DisplayTargets[2].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetMode.FilterAndProject, viewModel.PlayHistory.DisplayTargets[1].Mode);
            Assert.AreEqual(PlayHistoryDisplayTargetMode.FilterAndProject, viewModel.PlayHistory.DisplayTargets[2].Mode);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistory.DisplayTargets[3].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistory.DisplayTargets[4].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetMode.ProjectOnly, viewModel.PlayHistory.DisplayTargets[3].Mode);
            Assert.AreEqual(PlayHistoryDisplayTargetMode.ProjectOnly, viewModel.PlayHistory.DisplayTargets[4].Mode);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.Playlist, viewModel.PlayHistory.DisplayTargets[5].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.Playlist, viewModel.PlayHistory.DisplayTargets[6].Kind);
        }
        finally
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = previousJson;
            TryDeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditor_ResetSettingsRestoresSavedDraft()
    {
        string previousJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
        try
        {
            string savedJson = PlayHistoryDisplayTargetSetStore.Serialize(
            [
                new PlayHistoryDisplayTargetSet
                {
                    Name = "Saved",
                    Targets =
                    [
                        new PlayHistoryDisplayTargetReference
                        {
                            PlaylistId = 101
                        }
                    ]
                }
            ]);
            var viewModel = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.PlayHistoryDisplayTargetSetsJson = savedJson;
            InvokeBackupSavedSettings(dialog);

            dialog.AddPlayHistoryFolderDisplayPreset();
            dialog.SelectedPlayHistoryFolderDisplayPreset.Name = "Unsaved";

            Assert.IsTrue(dialog.HasPendingSettingChanges());

            dialog.ResetPlayHistoryFolderDisplayPresetsForCancel();
            InvokeBackupSavedSettings(dialog);

            Assert.AreEqual(savedJson, Settings.Default.PlayHistoryDisplayTargetSetsJson);
            Assert.AreEqual(1, dialog.PlayHistoryFolderDisplayPresets.Count);
            Assert.AreEqual("Saved", dialog.SelectedPlayHistoryFolderDisplayPreset.Name);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = previousJson;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_PersistsSelectedIdentity()
    {
        string previousJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = string.Empty;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create();

            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved")
            ]);
            PlayHistoryDisplayTargetItem target = viewModel.PlayHistory.DisplayTargets.Single(item =>
                item.Kind == PlayHistoryDisplayTargetKind.TargetSet
                && item.Mode == PlayHistoryDisplayTargetMode.ProjectOnly
                && item.TargetSet.Name == "Saved");

            viewModel.PlayHistory.SelectedDisplayTargetIdentity = target.Identity;

            Assert.AreEqual(target.Identity, Settings.Default.PlayHistorySelectedDisplayTargetIdentity);
        }
        finally
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = previousJson;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_RestoresWhenSavedTargetAppearsLater()
    {
        string previousJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = string.Empty;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = "set-folder:SAVED";
            var viewModel = MainWindowViewModelTestFactory.Create();

            Assert.AreEqual(PlayHistoryDisplayTargetKind.All, viewModel.PlayHistory.SelectedDisplayTarget.Kind);

            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved")
            ]);

            Assert.AreEqual("set-folder:SAVED", viewModel.PlayHistory.SelectedDisplayTarget.Identity);
        }
        finally
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = previousJson;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_IgnoresTransientEmptyIdentity()
    {
        string previousJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = string.Empty;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create();

            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved")
            ]);
            PlayHistoryDisplayTargetItem target = viewModel.PlayHistory.DisplayTargets.Single(item =>
                item.Kind == PlayHistoryDisplayTargetKind.TargetSet
                && item.Mode == PlayHistoryDisplayTargetMode.ProjectOnly
                && item.TargetSet.Name == "Saved");
            viewModel.PlayHistory.SelectedDisplayTargetIdentity = target.Identity;

            viewModel.PlayHistory.SelectedDisplayTargetIdentity = null;
            viewModel.PlayHistory.SelectedDisplayTargetIdentity = string.Empty;

            Assert.AreEqual(target.Identity, viewModel.PlayHistory.SelectedDisplayTarget.Identity);
            Assert.AreEqual(target.Identity, Settings.Default.PlayHistorySelectedDisplayTargetIdentity);
        }
        finally
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = previousJson;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_PersistsExplicitAllIdentity()
    {
        string previousJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = string.Empty;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create();

            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved")
            ]);
            PlayHistoryDisplayTargetItem target = viewModel.PlayHistory.DisplayTargets.Single(item =>
                item.Kind == PlayHistoryDisplayTargetKind.TargetSet
                && item.Mode == PlayHistoryDisplayTargetMode.ProjectOnly
                && item.TargetSet.Name == "Saved");
            viewModel.PlayHistory.SelectedDisplayTargetIdentity = target.Identity;

            viewModel.PlayHistory.SelectedDisplayTargetIdentity = PlayHistoryDisplayTargetItem.All.Identity;

            Assert.AreEqual(PlayHistoryDisplayTargetKind.All, viewModel.PlayHistory.SelectedDisplayTarget.Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetItem.All.Identity, Settings.Default.PlayHistorySelectedDisplayTargetIdentity);
        }
        finally
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = previousJson;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_BeginPlayHistoryRequestReappliesSavedIdentity()
    {
        string previousJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = string.Empty;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = "set-folder:SAVED";
            var viewModel = MainWindowViewModelTestFactory.Create();
            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved")
            ]);
            typeof(PlayHistoryWorkflowOwner)
                .GetField("selectedDisplayTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel.PlayHistory, PlayHistoryDisplayTargetItem.All);

            viewModel.PlayHistory.ActivatePeriod(
                PlayHistoryPeriodRequest.All(),
                viewModel.ChartFilters.KeywordFilter);

            Assert.AreEqual("set-folder:SAVED", viewModel.PlayHistory.SelectedDisplayTarget.Identity);
        }
        finally
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = previousJson;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_RequeuesWhenSameIdentityTargetSetChanges()
    {
        string previousJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = string.Empty;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create();
            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved", playlistId: 101)
            ]);
            PlayHistoryDisplayTargetItem target = viewModel.PlayHistory.DisplayTargets.Single(item =>
                item.Kind == PlayHistoryDisplayTargetKind.TargetSet
                && item.Mode == PlayHistoryDisplayTargetMode.ProjectOnly
                && item.TargetSet.Name == "Saved");
            viewModel.PlayHistory.SelectedDisplayTargetIdentity = target.Identity;
            PlayHistoryWorkflowOwner owner = viewModel.PlayHistory;
            long revisionBeforeChange = owner.DisplayTargetRevision;
            Settings.Default.PlayHistoryDisplayTargetSetsJson = PlayHistoryDisplayTargetSetStore.Serialize(
            [
                CreateTargetSet("Saved", playlistId: 202)
            ]);

            InvokeRefreshPlayHistoryDisplayTargetSetsFromSettings(viewModel, queueRefreshWhenSelectionChanges: true);

            Assert.AreEqual(target.Identity, viewModel.PlayHistory.SelectedDisplayTarget.Identity);
            Assert.AreEqual(202, viewModel.PlayHistory.SelectedDisplayTarget.TargetSet.Targets.Single().PlaylistId);
            Assert.IsTrue(
                owner.DisplayTargetRevision > revisionBeforeChange,
                "The active play-history filter must be re-applied when the selected target set keeps the same identity but changes content.");
        }
        finally
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = previousJson;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void Lr2BmsDirectoryChoices_ExcludeCustomFolderOutputBases()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string additionalOutputBase = Path.Combine(tempRootPath, "AdditionalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([manualBmsRoot, normalOutputBase, additionalOutputBase, rootOutputChild]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            Settings.Default.BMSInstallDir = manualBmsRoot;
            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();
            dialog.CustomFolderAdditionalOutputBaseDirList.Add(additionalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);

            CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories, manualBmsRoot);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, normalOutputBase);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, additionalOutputBase);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, rootOutputChild);
            CollectionAssert.Contains(dialog.AvailableBMSDirectories, manualBmsRoot);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, normalOutputBase);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, additionalOutputBase);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, rootOutputChild);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void CollectNestedBmsSearchRootConflicts_DetectsParentChildOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        string parent = Path.Combine(root, "BMS");
        string child = Path.Combine(parent, "Child");
        string sibling = Path.Combine(root, "Sibling");

        IReadOnlyList<SettingsDialogViewModel.NestedBmsSearchRootConflict> conflicts =
            SettingsDialogViewModel.CollectNestedBmsSearchRootConflicts([child, sibling, parent]);

        Assert.AreEqual(1, conflicts.Count);
        Assert.AreEqual(parent, conflicts[0].ParentPath);
        Assert.AreEqual(child, conflicts[0].ChildPath);
    }

    [TestMethod]
    public void CheckValidationBeforeSave_RejectsNestedLr2JukeboxRoots()
    {
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        string previousSongDbPath = Settings.Default.LR2SongDBPath;
        string previousConfigXmlPath = Settings.Default.LR2ConfigXmlPath;
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        bool previousUsePlayerUbmplay = Settings.Default.UsePlayeruBMplay;
        bool previousUsePlayerLr2body = Settings.Default.UsePlayerLR2body;
        bool previousUsePlayerBmidxView = Settings.Default.UsePlayerBMIIDXView;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string parent = Path.Combine(tempRootPath, "BMS");
            string child = Path.Combine(parent, "Child");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);

            LR2Config config = CreateConfig(tempRootPath);
            string configPath = Path.Combine(tempRootPath, "LR2files", "Config", "config.xml");
            config.SetBMSSearchDirectories([parent, child]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2SongDBPath = songDbPath;
            Settings.Default.LR2ConfigXmlPath = configPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = parent;
            Settings.Default.UsePlayeruBMplay = false;
            Settings.Default.UsePlayerLR2body = false;
            Settings.Default.UsePlayerBMIIDXView = false;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            bool isValid = dialog.CheckValidationBeforeSave(out string errMsg);

            Assert.IsFalse(isValid);
            StringAssert.Contains(errMsg, "親子関係");
            StringAssert.Contains(errMsg, parent);
            StringAssert.Contains(errMsg, child);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.LR2SongDBPath = previousSongDbPath;
            Settings.Default.LR2ConfigXmlPath = previousConfigXmlPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            Settings.Default.UsePlayeruBMplay = previousUsePlayerUbmplay;
            Settings.Default.UsePlayerLR2body = previousUsePlayerLr2body;
            Settings.Default.UsePlayerBMIIDXView = previousUsePlayerBmidxView;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void CheckValidationBeforeSave_RejectsPreviousNormalOutputCoveredOnlyByManualParentRoot()
    {
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        string previousSongDbPath = Settings.Default.LR2SongDBPath;
        string previousConfigXmlPath = Settings.Default.LR2ConfigXmlPath;
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        bool previousUsePlayerUbmplay = Settings.Default.UsePlayeruBMplay;
        bool previousUsePlayerLr2body = Settings.Default.UsePlayerLR2body;
        bool previousUsePlayerBmidxView = Settings.Default.UsePlayerBMIIDXView;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string parent = Path.Combine(tempRootPath, "BMS");
            string normalOutputBase = Path.Combine(parent, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);

            LR2Config config = CreateConfig(tempRootPath);
            string configPath = Path.Combine(tempRootPath, "LR2files", "Config", "config.xml");
            config.SetBMSSearchDirectories([parent]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2SongDBPath = songDbPath;
            Settings.Default.LR2ConfigXmlPath = configPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = parent;
            Settings.Default.UsePlayeruBMplay = false;
            Settings.Default.UsePlayerLR2body = false;
            Settings.Default.UsePlayerBMIIDXView = false;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            bool isValid = dialog.CheckValidationBeforeSave(out string errMsg);

            Assert.IsFalse(isValid);
            StringAssert.Contains(errMsg, "登録済みBMSディレクトリ");
            StringAssert.Contains(errMsg, "通常出力先");
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.LR2SongDBPath = previousSongDbPath;
            Settings.Default.LR2ConfigXmlPath = previousConfigXmlPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            Settings.Default.UsePlayeruBMplay = previousUsePlayerUbmplay;
            Settings.Default.UsePlayerLR2body = previousUsePlayerLr2body;
            Settings.Default.UsePlayerBMIIDXView = previousUsePlayerBmidxView;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBase_ChildJukeboxRowsDoNotInvalidateRootOutputBase()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([manualBmsRoot, normalOutputBase, rootOutputChild]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = manualBmsRoot;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            object?[] args = [rootOutputBase, null];
            bool isValid = (bool)typeof(SettingsDialogViewModel)
                .GetMethod("ValidateCustomFolderAsRootOutputBaseDir", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(string).MakeByRefType()], null)!
                .Invoke(dialog, args)!;

            Assert.IsTrue(isValid, args[1] as string ?? string.Empty);
            Assert.AreEqual(0, InvokeCollectCustomFolderOutputBaseJukeboxAdoptionConflicts(dialog).Length);
            CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories, manualBmsRoot);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, normalOutputBase);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, rootOutputChild);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBaseSync_ReplacesAdoptedRootBaseWithPlaylistOutputRoots()
    {
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string playlistOutputRoot = Path.Combine(rootOutputBase, "PlaylistOutput");
            string staleOutputRoot = Path.Combine(rootOutputBase, "StaleOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(playlistOutputRoot);
            Directory.CreateDirectory(staleOutputRoot);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([manualBmsRoot, rootOutputBase, staleOutputRoot]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", string.Empty);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

            bool changed = (bool)typeof(SettingsDialogViewModel)
                .GetMethod("SyncRootCustomFolderOutputSearchRootsAfterSettingsChange", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, null)!;

            Assert.IsTrue(changed);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), manualBmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), playlistOutputRoot);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), staleOutputRoot);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBaseSync_RegistersAndCreatesMissingPlaylistOutputRoot()
    {
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string playlistOutputRoot = Path.Combine(rootOutputBase, "MissingPlaylistOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([manualBmsRoot, rootOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", string.Empty);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "MissingPlaylistOutput" }]);

            bool changed = (bool)typeof(SettingsDialogViewModel)
                .GetMethod("SyncRootCustomFolderOutputSearchRootsAfterSettingsChange", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, null)!;

            Assert.IsTrue(changed);
            Assert.IsTrue(Directory.Exists(playlistOutputRoot));
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), manualBmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), playlistOutputRoot);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBaseSync_AddsNormalOutputBaseWhenRemovedParentCoveredIt()
    {
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string parentBmsRoot = Path.Combine(tempRootPath, "BMS");
            string normalOutputBase = Path.Combine(parentBmsRoot, "NormalOutput");
            string additionalOutputBase = Path.Combine(parentBmsRoot, "AdditionalOutput");
            string rootOutputBase = Path.Combine(parentBmsRoot, "RootOutput");
            string playlistOutputRoot = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([parentBmsRoot, rootOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", string.Empty);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

            bool changed = (bool)typeof(SettingsDialogViewModel)
                .GetMethod("SyncRootCustomFolderOutputSearchRootsAfterSettingsChange", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, null)!;

            Assert.IsTrue(changed);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), normalOutputBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), additionalOutputBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), playlistOutputRoot);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), parentBmsRoot);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void StartupRepair_DoesNotRewriteManagedOutputInstallDir()
    {
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        string previousConfigXmlPath = Settings.Default.LR2ConfigXmlPath;
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            LR2Config config = CreateConfig(tempRootPath);
            string configPath = Path.Combine(tempRootPath, "LR2files", "Config", "config.xml");
            config.SetBMSSearchDirectories([manualBmsRoot, normalOutputBase, rootOutputBase, rootOutputChild]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2ConfigXmlPath = configPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = rootOutputChild;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");
            SetDialogField(dialog, "tempBMSInstallDir", rootOutputChild);

            typeof(MainWindowViewModel)
                .GetMethod("RepairCustomFolderOutputSearchRootsBeforeStartupValidation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [StartupSettingsSnapshot.CreateCurrent()]);

            Assert.AreEqual(rootOutputChild, Settings.Default.BMSInstallDir);
            Assert.AreEqual(rootOutputChild, GetDialogField<string>(dialog, "tempBMSInstallDir"));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.LR2ConfigXmlPath = previousConfigXmlPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void NormalOutputBase_CannotAdoptNewManualBmsRoot()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([manualBmsRoot, normalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            bool isValid = InvokeValidateCustomFolderOutputBaseDir(dialog, manualBmsRoot, out string errMsg);

            Assert.IsFalse(isValid);
            StringAssert.Contains(errMsg, "登録済みBMSディレクトリ");
            Assert.AreEqual(normalOutputBase, Settings.Default.LR2CustomFolderOutputBaseDir);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void NormalOutputBase_AllowsExistingUserConfigRootAlreadyInJukebox()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([normalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            bool isValid = InvokeValidateCustomFolderOutputBaseDir(dialog, normalOutputBase, out string errMsg);

            Assert.IsTrue(isValid, errMsg);
            Assert.AreEqual(0, InvokeCollectCustomFolderOutputBaseJukeboxAdoptionConflicts(dialog).Length);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void AdditionalOutputBase_PreviousNormalOutputBaseRequiresAdoptionWarning()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string previousNormalOutput = Path.Combine(tempRootPath, "NormalOutput");
            string currentNormalOutput = Path.Combine(tempRootPath, "NewNormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(previousNormalOutput);
            Directory.CreateDirectory(currentNormalOutput);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([previousNormalOutput]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = currentNormalOutput;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([previousNormalOutput]);
            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();
            dialog.CustomFolderAdditionalOutputBaseDirList.Add(previousNormalOutput);
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", previousNormalOutput);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            object[] conflicts = InvokeCollectCustomFolderOutputBaseJukeboxAdoptionConflicts(dialog);

            Assert.AreEqual(1, conflicts.Length);
            Assert.AreEqual(previousNormalOutput, GetConflictProperty(conflicts[0], "OutputBasePath"));
            Assert.AreEqual(previousNormalOutput, GetConflictProperty(conflicts[0], "JukeboxRootPath"));
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void NormalOutputBase_CannotAdoptPreviousAdditionalOutputBaseAsNewNormalRoot()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string previousAdditionalOutputBase = Path.Combine(tempRootPath, "AdditionalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(previousAdditionalOutputBase);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([normalOutputBase, previousAdditionalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(
                dialog,
                "tempLR2CustomFolderAdditionalOutputBaseDirs",
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([previousAdditionalOutputBase]));

            bool isValid = InvokeValidateCustomFolderOutputBaseDir(dialog, previousAdditionalOutputBase, out string errMsg);

            Assert.IsFalse(isValid);
            StringAssert.Contains(errMsg, "登録済みBMSディレクトリ");
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void NormalOutputBase_CannotAdoptPreviousRootOutputBaseAsNewNormalRoot()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string previousRootOutputBasePath = Path.Combine(tempRootPath, "RootOutput");
            string currentRootOutputBase = Path.Combine(tempRootPath, "NewRootOutput");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(previousRootOutputBasePath);
            Directory.CreateDirectory(currentRootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([normalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = currentRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", previousRootOutputBasePath);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            bool isValid = InvokeValidateCustomFolderOutputBaseDir(dialog, previousRootOutputBasePath, out string errMsg);

            Assert.IsFalse(isValid);
            StringAssert.Contains(errMsg, "登録済みBMSディレクトリ");
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBase_CanAdoptManualBmsRootParentWithSaveWarning()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRootParent = Path.Combine(tempRootPath, "ManualBmsRootParent");
            string manualBmsRoot = Path.Combine(manualBmsRootParent, "Songs");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([manualBmsRoot, normalOutputBase, rootOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            dialog.LR2CustomFolderAsRootOutputDir = manualBmsRootParent;

            object[] conflicts = InvokeCollectCustomFolderOutputBaseJukeboxAdoptionConflicts(dialog);

            Assert.AreEqual(manualBmsRootParent, Settings.Default.LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual(1, conflicts.Length);
            Assert.AreEqual(manualBmsRootParent, GetConflictProperty(conflicts[0], "OutputBasePath"));
            Assert.AreEqual(manualBmsRoot, GetConflictProperty(conflicts[0], "JukeboxRootPath"));
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void CustomFolderOutputBaseJukeboxAdoptionMessage_LimitsVisibleConflicts()
    {
        string message = InvokeBuildCustomFolderOutputBaseJukeboxAdoptionMessage(12);

        StringAssert.Contains(message, "Output10");
        Assert.IsFalse(message.Contains("Output11"));
        StringAssert.Contains(message, "2");
        StringAssert.Contains(message, "...");
    }

    [TestMethod]
    public void AdditionalOutputBaseRename_InvalidNameDoesNotChangeList()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string additionalOutputBaseParent = Path.Combine(tempRootPath, "AdditionalParent");
            string additionalOutputBase = Path.Combine(additionalOutputBaseParent, "AdditionalOutput");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);
            Directory.CreateDirectory(additionalOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([normalOutputBase, rootOutputBase, additionalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();
            dialog.CustomFolderAdditionalOutputBaseDirList.Add(additionalOutputBase);
            dialog.SelectedCustomFolderAdditionalOutputBaseDir = additionalOutputBase;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseRegistry.RenameLastDirectoryName(additionalOutputBase, "."));

            CollectionAssert.Contains(dialog.CustomFolderAdditionalOutputBaseDirList, additionalOutputBase);
            CollectionAssert.DoesNotContain(dialog.CustomFolderAdditionalOutputBaseDirList, additionalOutputBaseParent);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void NormalOutputBaseSearchRootSync_RemovesOldNormalOutputBaseEvenWhenUsedAsInstallDir()
    {
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string oldNormalOutputBase = Path.Combine(tempRootPath, "OldNormalOutput");
            string newNormalOutputBase = Path.Combine(tempRootPath, "NewNormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(oldNormalOutputBase);
            Directory.CreateDirectory(newNormalOutputBase);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([oldNormalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = newNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = oldNormalOutputBase;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", oldNormalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            CustomFolderOutputBaseSearchRootSyncPlan plan = InvokePrepareCustomFolderNormalOutputBaseSearchRootSync(dialog);
            CustomFolderOutputBaseSearchRootSyncService.CompleteAdditionalOutputBaseRootSync(config, plan);

            CollectionAssert.Contains(plan.RemovedPaths.ToArray(), oldNormalOutputBase);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), oldNormalOutputBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), newNormalOutputBase);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    private static MainWindowViewModel CreateViewModel(LR2Config config)
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        typeof(MainWindowViewModel)
            .GetField("lr2config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, config);
        typeof(SettingsDialogViewModel)
            .GetField("lr2ConfigValue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingDialog, config);
        SetDialogField(viewModel.SettingDialog, "operationModeLR2DB", true);
        return viewModel;
    }

    private static LR2Config CreateConfig(string tempRootPath)
    {
        string configDirectoryPath = Path.Combine(tempRootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectoryPath);
        string configPath = Path.Combine(configDirectoryPath, "config.xml");
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new LR2Config(configPath);
    }

    private static void SetDialogField(SettingsDialogViewModel dialog, string fieldName, object value)
    {
        typeof(SettingsDialogViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(dialog, value);
    }

    private static void InvokeBackupSavedSettings(SettingsDialogViewModel dialog)
    {
        typeof(SettingsDialogViewModel)
            .GetMethod("backupSavedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null);
    }

    private static void SetViewModelField(MainWindowViewModel viewModel, string fieldName, object value)
    {
        typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    private static void ReplacePlayHistoryDisplayTargetSets(
        MainWindowViewModel viewModel,
        IEnumerable<PlayHistoryDisplayTargetSet> targetSets)
    {
        string serializedTargetSets = PlayHistoryDisplayTargetSetStore.Serialize(targetSets);
        Settings.Default.PlayHistoryDisplayTargetSetsJson = serializedTargetSets;
        viewModel.PlayHistory.ReplaceDisplayTargetSetsFromSettings(
            serializedTargetSets,
            viewModel.PlaylistWorkspace.CapturePlaylistTreeTablesSnapshot(),
            queueRefreshWhenSelectionChanges: false);
    }

    private static T GetViewModelField<T>(MainWindowViewModel viewModel, string fieldName)
    {
        return (T)typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
    }

    private static void InvokeRefreshPlayHistoryDisplayTargetSetsFromSettings(
        MainWindowViewModel viewModel,
        bool queueRefreshWhenSelectionChanges)
    {
        typeof(MainWindowViewModel)
            .GetMethod("RefreshPlayHistoryDisplayTargetSetsFromSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [queueRefreshWhenSelectionChanges]);
    }

    private static void SetViewModelTables(MainWindowViewModel viewModel, string songDbPath, BMSTable[] tables)
    {
        var playlist = new BMSPlaylist(songDbPath)
        {
            BMSTables = new DispatcherCollection<BMSTable>(
                new ObservableCollection<BMSTable>(tables),
                Dispatcher.CurrentDispatcher)
        };
        SetViewModelField(viewModel, "tables", playlist);
        viewModel.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
    }

    private static object[] InvokeCollectCustomFolderOutputBaseJukeboxAdoptionConflicts(SettingsDialogViewModel dialog)
    {
        return ((System.Collections.IEnumerable)typeof(SettingsDialogViewModel)
            .GetMethod("CollectCustomFolderOutputBaseJukeboxAdoptionConflicts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null)!)
            .Cast<object>()
            .ToArray();
    }

    private static bool InvokeValidateCustomFolderOutputBaseDir(
        SettingsDialogViewModel dialog,
        string path,
        out string errMsg)
    {
        object?[] args = [path, null];
        bool result = (bool)typeof(SettingsDialogViewModel)
            .GetMethod("ValidateCustomFolderOutputBaseDir", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(string).MakeByRefType()], null)!
            .Invoke(dialog, args)!;
        errMsg = (string)args[1]!;
        return result;
    }

    private static CustomFolderOutputBaseSearchRootSyncPlan InvokePrepareCustomFolderNormalOutputBaseSearchRootSync(
        SettingsDialogViewModel dialog)
    {
        return (CustomFolderOutputBaseSearchRootSyncPlan)typeof(SettingsDialogViewModel)
            .GetMethod("PrepareCustomFolderNormalOutputBaseSearchRootSync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null)!;
    }

    private static string GetConflictProperty(object conflict, string propertyName)
    {
        return (string)conflict.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(conflict)!;
    }

    private static string InvokeBuildCustomFolderOutputBaseJukeboxAdoptionMessage(int conflictCount)
    {
        Type dialogType = typeof(SettingsDialogViewModel);
        Type conflictType = dialogType
            .GetNestedType("CustomFolderOutputBaseJukeboxAdoptionConflict", BindingFlags.NonPublic)!;
        Type listType = typeof(List<>).MakeGenericType(conflictType);
        var conflicts = (System.Collections.IList)Activator.CreateInstance(listType)!;
        ConstructorInfo constructor = conflictType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(string), typeof(string)],
            modifiers: null)!;
        for (int index = 1; index <= conflictCount; index++)
        {
            conflicts.Add(constructor.Invoke([
                "Label",
                "Output" + index.ToString("00"),
                "Jukebox" + index.ToString("00")
            ]));
        }

        return (string)dialogType
            .GetMethod("BuildCustomFolderOutputBaseJukeboxAdoptionMessage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [conflicts])!;
    }

    private static bool InvokeValidatePlayHistoryFolderDisplayPresets(
        SettingsDialogViewModel dialog,
        out string errMsg)
    {
        object?[] args = [null];
        bool result = (bool)typeof(SettingsDialogViewModel)
            .GetMethod("ValidatePlayHistoryFolderDisplayPresets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, args)!;
        errMsg = (string)args[0]!;
        return result;
    }

    private static T GetDialogField<T>(SettingsDialogViewModel dialog, string fieldName)
    {
        return (T)typeof(SettingsDialogViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!;
    }

    private static BMSTable CreatePresetTable(int playlistId, string name, string symbol)
    {
        return new BMSTable
        {
            playlist_id = playlistId,
            name = name,
            org_name = name,
            symbol = symbol,
            org_symbol = symbol
        };
    }

    private static PlayHistoryDisplayTargetSet CreateTargetSet(string name, int playlistId = 101)
    {
        return new PlayHistoryDisplayTargetSet
        {
            Name = name,
            Targets =
            [
                new PlayHistoryDisplayTargetReference
                {
                    PlaylistId = playlistId
                }
            ]
        };
    }

    private static void AssertTargetSetEquals(PlayHistoryDisplayTargetSet expected, PlayHistoryDisplayTargetSet actual)
    {
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Targets.Count, actual.Targets.Count);
        for (int index = 0; index < expected.Targets.Count; index++)
        {
            Assert.AreEqual(expected.Targets[index].PlaylistId, actual.Targets[index].PlaylistId);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
