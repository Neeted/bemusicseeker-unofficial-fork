using System;
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
    public void HasPendingSettingChanges_TracksSettingsChangesAndReset()
    {
        bool previousShowRecommUpdatedMsg = Settings.Default.ShowRecommUpdatedMsg;
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
            SetDialogField(dialog, "operationModeLR2DB", !GetDialogField<bool>(dialog, "tempOperationModeLR2DB"));

            Assert.IsTrue(dialog.HasPendingSettingChanges());

            SetDialogField(dialog, "operationModeLR2DB", GetDialogField<bool>(dialog, "tempOperationModeLR2DB"));
            typeof(MainWindowViewModel.SettingDialogViewModel)
                .GetMethod("backupSavedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, null);

            Assert.AreEqual(previousShowRecommUpdatedMsg, Settings.Default.ShowRecommUpdatedMsg);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            Settings.Default.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
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

            PlayHistoryFolderDisplayPresetEditSession canceledEditSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(secondPreset);
            PlayHistoryDisplayTargetSet appliedTargetSetBeforeCancel = secondPreset.ToTargetSet();
            canceledEditSession.Name = "Canceled";
            canceledEditSession.PlaylistOptions.ToList().ForEach(option => option.IsSelected = !option.IsSelected);
            Assert.AreEqual("Second", secondPreset.Name);
            AssertTargetSetEquals(appliedTargetSetBeforeCancel, secondPreset.ToTargetSet());

            Assert.IsTrue(dialog.PersistPlayHistoryFolderDisplayPresetsIfChanged());
            Assert.IsFalse(string.IsNullOrWhiteSpace(Settings.Default.PlayHistoryDisplayTargetSetsJson));

            Assert.AreEqual(5, viewModel.PlayHistoryDisplayTargets.Count);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.All, viewModel.PlayHistoryDisplayTargets[0].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistoryDisplayTargets[1].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.TargetSet, viewModel.PlayHistoryDisplayTargets[2].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.Playlist, viewModel.PlayHistoryDisplayTargets[3].Kind);
            Assert.AreEqual(PlayHistoryDisplayTargetKind.Playlist, viewModel.PlayHistoryDisplayTargets[4].Kind);
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
                            PlaylistId = 101,
                            PlaylistName = "Satellite",
                            PlaylistSymbol = "SAT"
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
    public void NormalOutputBase_CannotBeChangedToManualBmsRoot()
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
    public void RootOutputBase_CannotBeChangedToManualBmsRootParent()
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

            Assert.AreEqual(rootOutputBase, Settings.Default.LR2CustomFolderOutputBaseDirRootType);
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
            Assert.AreEqual(expected.Targets[index].PlaylistName, actual.Targets[index].PlaylistName);
            Assert.AreEqual(expected.Targets[index].PlaylistSymbol, actual.Targets[index].PlaylistSymbol);
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
