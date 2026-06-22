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
            var viewModel = new MainWindowViewModel();
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;

            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsFalse(SettingDialog.ShouldResetSettingsOnCancel(dialog));

            Settings.Default.ShowRecommUpdatedMsg = !previousShowRecommUpdatedMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsTrue(SettingDialog.ShouldResetSettingsOnCancel(dialog));

            Settings.Default.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            Settings.Default.ShowDuplicateFileCheckConfirmMsg = !previousShowDuplicateFileCheckConfirmMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsTrue(SettingDialog.ShouldResetSettingsOnCancel(dialog));

            Settings.Default.ShowDuplicateFileCheckConfirmMsg = previousShowDuplicateFileCheckConfirmMsg;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            SetDialogField(dialog, "operationModeLR2DB", !GetDialogField<bool>(dialog, "tempOperationModeLR2DB"));

            Assert.IsTrue(dialog.HasPendingSettingChanges());

            SetDialogField(dialog, "operationModeLR2DB", GetDialogField<bool>(dialog, "tempOperationModeLR2DB"));
            typeof(MainWindowViewModel.SettingDialogViewModel)
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
    public void HasFreshLr2PlayHistorySchemaCheckResult_MatchesPathAndOperationMode()
    {
        var viewModel = new MainWindowViewModel();
        MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
        SetDialogField(dialog, "operationModeLR2DB", true);
        string expectedScoreDbPath = dialog.Lr2PlayHistoryScoreDbPath;
        var result = new Lr2PlayHistorySchemaCheckResult
        {
            ScoreDbPath = expectedScoreDbPath,
            Status = Lr2PlayHistorySchemaStatus.Installed
        };

        dialog.ApplyLr2PlayHistorySchemaCheckResult(result);

        Assert.IsTrue(dialog.HasFreshLr2PlayHistorySchemaCheckResult(expectedScoreDbPath, isLr2LinkedProfile: true));
        Assert.IsFalse(dialog.HasFreshLr2PlayHistorySchemaCheckResult(@"C:\LR2\Score\other.db", isLr2LinkedProfile: true));
        Assert.IsFalse(dialog.HasFreshLr2PlayHistorySchemaCheckResult(expectedScoreDbPath, isLr2LinkedProfile: false));
        Assert.IsFalse(SettingDialog.ShouldRefreshLr2PlayHistorySchemaStatus(dialog, force: false, expectedScoreDbPath, expectedOperationMode: true));
        Assert.IsTrue(SettingDialog.ShouldRefreshLr2PlayHistorySchemaStatus(dialog, force: true, expectedScoreDbPath, expectedOperationMode: true));
        Assert.IsTrue(SettingDialog.ShouldRefreshLr2PlayHistorySchemaStatus(dialog, force: false, @"C:\LR2\Score\other.db", expectedOperationMode: true));
        Assert.IsTrue(SettingDialog.ShouldRefreshLr2PlayHistorySchemaStatus(dialog, force: false, expectedScoreDbPath, expectedOperationMode: false));

        dialog.ResetLr2PlayHistorySchemaStatus();

        Assert.IsTrue(dialog.HasFreshLr2PlayHistorySchemaCheckResult(expectedScoreDbPath, isLr2LinkedProfile: true));
        Assert.IsFalse(SettingDialog.ShouldRefreshLr2PlayHistorySchemaStatus(dialog, force: false, expectedScoreDbPath, expectedOperationMode: true));

        SetDialogField(dialog, "operationModeLR2DB", false);
        dialog.ResetLr2PlayHistorySchemaStatus();

        Assert.IsFalse(dialog.HasFreshLr2PlayHistorySchemaCheckResult(expectedScoreDbPath, isLr2LinkedProfile: true));
        Assert.IsTrue(SettingDialog.ShouldRefreshLr2PlayHistorySchemaStatus(dialog, force: false, expectedScoreDbPath, expectedOperationMode: true));
    }

    [TestMethod]
    public void ShouldCloseSettingsWithoutSave_ClosesOnlyActiveUnchangedProfiles()
    {
        bool previousShowRecommUpdatedMsg = Settings.Default.ShowRecommUpdatedMsg;
        try
        {
            var viewModel = new MainWindowViewModel();
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;

            Assert.IsFalse(SettingDialog.ShouldCloseSettingsWithoutSave(viewModel, dialog));

            SetViewModelField(viewModel, "hasActiveLibraryProfile", true);

            Assert.IsTrue(SettingDialog.ShouldCloseSettingsWithoutSave(viewModel, dialog));

            Settings.Default.ShowRecommUpdatedMsg = !Settings.Default.ShowRecommUpdatedMsg;

            Assert.IsFalse(SettingDialog.ShouldCloseSettingsWithoutSave(viewModel, dialog));
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
            var viewModel = new MainWindowViewModel();
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            BMSTable tableA = CreatePresetTable(101, "Satellite", "SAT");
            BMSTable tableB = CreatePresetTable(202, "Satellite", "SAT");
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
            Assert.IsTrue(PlayHistoryFolderPresetPlaylistOption.Matches(tableA, draft.Targets[0]));
            Assert.IsFalse(PlayHistoryFolderPresetPlaylistOption.Matches(tableB, draft.Targets[0]));

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
            Assert.IsFalse(SettingDialog.ShouldCloseSettingsWithoutSave(viewModel, dialog));
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

            Assert.AreEqual(7, viewModel.PlayHistoryDisplayTargets.Count);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.All, viewModel.PlayHistoryDisplayTargets[0].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistoryDisplayTargets[1].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistoryDisplayTargets[2].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetMode.FilterAndProject, viewModel.PlayHistoryDisplayTargets[1].Mode);
            Assert.AreEqual(PlayHistoryDisplayTargetMode.FilterAndProject, viewModel.PlayHistoryDisplayTargets[2].Mode);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistoryDisplayTargets[3].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistoryDisplayTargets[4].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetMode.ProjectOnly, viewModel.PlayHistoryDisplayTargets[3].Mode);
            Assert.AreEqual(PlayHistoryDisplayTargetMode.ProjectOnly, viewModel.PlayHistoryDisplayTargets[4].Mode);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.Playlist, viewModel.PlayHistoryDisplayTargets[5].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.Playlist, viewModel.PlayHistoryDisplayTargets[6].Kind);
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
            var viewModel = new MainWindowViewModel();
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
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
    public void Lr2BmsDirectoryChoices_ExcludeManagedCustomFolderOutputBases()
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
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
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
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = manualBmsRoot;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            object?[] args = [rootOutputBase, null];
            bool isValid = (bool)typeof(MainWindowViewModel.SettingDialogViewModel)
                .GetMethod("ValidateCustomFolderAsRootOutputBaseDir", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(string).MakeByRefType()], null)!
                .Invoke(dialog, args)!;

            Assert.IsTrue(isValid, args[1] as string ?? string.Empty);
            Assert.AreEqual(0, InvokeCollectCustomFolderOutputBaseJukeboxAdoptionConflicts(dialog).Length);
            CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories, manualBmsRoot);
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
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", string.Empty);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

            bool changed = (bool)typeof(MainWindowViewModel.SettingDialogViewModel)
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
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", string.Empty);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "MissingPlaylistOutput" }]);

            bool changed = (bool)typeof(MainWindowViewModel.SettingDialogViewModel)
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
    public void StartupRepair_MovesManagedOutputInstallDirToFirstUserBmsRoot()
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
            config.SetBMSSearchDirectories([manualBmsRoot, normalOutputBase, rootOutputBase, rootOutputChild]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = rootOutputChild;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            bool repaired = (bool)typeof(MainWindowViewModel)
                .GetMethod("RepairBmsInstallDirIfManagedOutputSearchRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, null)!;

            Assert.IsTrue(repaired);
            Assert.AreEqual(manualBmsRoot, Settings.Default.BMSInstallDir);
            Assert.AreEqual(manualBmsRoot, GetDialogField<string>(dialog, "tempBMSInstallDir"));
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
    public void NormalOutputBase_CanAdoptManualBmsRootWithSaveWarning()
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
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            dialog.LR2CustomFolderOutputDir = manualBmsRoot;

            object[] conflicts = InvokeCollectCustomFolderOutputBaseJukeboxAdoptionConflicts(dialog);

            Assert.AreEqual(manualBmsRoot, Settings.Default.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(1, conflicts.Length);
            Assert.AreEqual(manualBmsRoot, GetConflictProperty(conflicts[0], "OutputBasePath"));
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
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
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
    public void AdditionalOutputBaseRename_CannotMoveToOldAdditionalOutputBaseParent()
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
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
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

            dialog.SelectedCustomFolderAdditionalOutputBaseName = ".";
            dialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();

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

    private static MainWindowViewModel CreateViewModel(LR2Config config)
    {
        var viewModel = new MainWindowViewModel();
        typeof(MainWindowViewModel)
            .GetField("lr2config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, config);
        SetDialogField(viewModel.settingDialog, "operationModeLR2DB", true);
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

    private static void SetDialogField(MainWindowViewModel.SettingDialogViewModel dialog, string fieldName, object value)
    {
        typeof(MainWindowViewModel.SettingDialogViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(dialog, value);
    }

    private static void InvokeBackupSavedSettings(MainWindowViewModel.SettingDialogViewModel dialog)
    {
        typeof(MainWindowViewModel.SettingDialogViewModel)
            .GetMethod("backupSavedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null);
    }

    private static void SetViewModelField(MainWindowViewModel viewModel, string fieldName, object value)
    {
        typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
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
    }

    private static object[] InvokeCollectCustomFolderOutputBaseJukeboxAdoptionConflicts(MainWindowViewModel.SettingDialogViewModel dialog)
    {
        return ((System.Collections.IEnumerable)typeof(MainWindowViewModel.SettingDialogViewModel)
            .GetMethod("CollectCustomFolderOutputBaseJukeboxAdoptionConflicts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null)!)
            .Cast<object>()
            .ToArray();
    }

    private static string GetConflictProperty(object conflict, string propertyName)
    {
        return (string)conflict.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(conflict)!;
    }

    private static string InvokeBuildCustomFolderOutputBaseJukeboxAdoptionMessage(int conflictCount)
    {
        Type dialogType = typeof(MainWindowViewModel.SettingDialogViewModel);
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
        MainWindowViewModel.SettingDialogViewModel dialog,
        out string errMsg)
    {
        object?[] args = [null];
        bool result = (bool)typeof(MainWindowViewModel.SettingDialogViewModel)
            .GetMethod("ValidatePlayHistoryFolderDisplayPresets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, args)!;
        errMsg = (string)args[0]!;
        return result;
    }

    private static T GetDialogField<T>(MainWindowViewModel.SettingDialogViewModel dialog, string fieldName)
    {
        return (T)typeof(MainWindowViewModel.SettingDialogViewModel)
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
