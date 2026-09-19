using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
public sealed class SettingDialogCustomFolderOutputBaseTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new()
    {
        RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson
    };
    [TestMethod]
    public void HasPendingSettingChanges_UsesSnapshotDiffsAndReset()
    {
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        bool previousShowRecommUpdatedMsg = testSettings.ShowRecommUpdatedMsg;
        bool previousShowDuplicateFileCheckConfirmMsg = testSettings.ShowDuplicateFileCheckConfirmMsg;
        try
        {
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.IsFalse(dialog.HasPendingSettingChanges());

            testSettings.ShowRecommUpdatedMsg = !previousShowRecommUpdatedMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            testSettings.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            testSettings.ShowDuplicateFileCheckConfirmMsg = !previousShowDuplicateFileCheckConfirmMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            testSettings.ShowDuplicateFileCheckConfirmMsg = previousShowDuplicateFileCheckConfirmMsg;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            dialog.OperationModeLR2DB = !previousOperationMode;

            Assert.IsTrue(dialog.HasPendingSettingChanges());

            dialog.ResetSettings();

            Assert.AreEqual(previousShowRecommUpdatedMsg, testSettings.ShowRecommUpdatedMsg);
            Assert.AreEqual(previousShowDuplicateFileCheckConfirmMsg, testSettings.ShowDuplicateFileCheckConfirmMsg);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
            testSettings.ShowDuplicateFileCheckConfirmMsg = previousShowDuplicateFileCheckConfirmMsg;
        }
    }

    [TestMethod]
    public void PendingSettingsRemainIndependentOfActiveLibraryProfile()
    {
        bool previousShowRecommUpdatedMsg = testSettings.ShowRecommUpdatedMsg;
        try
        {
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            SetViewModelField(viewModel, "hasActiveLibraryProfile", true);

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            testSettings.ShowRecommUpdatedMsg = !testSettings.ShowRecommUpdatedMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());
        }
        finally
        {
            testSettings.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
        }
    }

    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditor_DraftsSaveValidationAndIdMatching()
    {
        string previousJson = testSettings.PlayHistoryDisplayTargetSetsJson;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (new LR2SongDBExtended(songDbPath))
            {
            }
            testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            BMSTable tableA = CreatePresetTable(101, "Satellite", "SAT");
            BMSTable tableB = CreatePresetTable(202, "Satellite", "SAT");
            tableA.symbol = string.Empty;
            SetViewModelTables(viewModel, songDbPath, [tableA, tableB]);

            dialog.AddPlayHistoryFolderDisplayPreset();

            PlayHistoryFolderDisplayPresetEditor preset = dialog.PlayHistoryFolderDisplayPresets.Single();
            Assert.AreSame(preset, dialog.SelectedPlayHistoryFolderDisplayPreset);
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            Assert.AreEqual(string.Empty, testSettings.PlayHistoryDisplayTargetSetsJson);
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

            PlayHistoryFolderDisplayPresetEditSession emptyNameSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(preset);
            emptyNameSession.Name = string.Empty;
            Assert.IsFalse(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(emptyNameSession, out string emptyNameError));
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
            editSession.SearchText = "definitely-no-match";
            Assert.AreEqual(0, editSession.FilteredPlaylistOptions.Count);
            Assert.IsTrue(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(editSession, out string applyPresetError), applyPresetError);
            secondPreset.Targets.Add(new PlayHistoryDisplayTargetReference { PlaylistId = 999 });

            PlayHistoryFolderDisplayPresetEditSession canceledEditSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(secondPreset);
            PlayHistoryDisplayTargetSet appliedTargetSetBeforeCancel = secondPreset.ToTargetSet();
            canceledEditSession.Name = "Canceled";
            canceledEditSession.PlaylistOptions.ToList().ForEach(option => option.IsSelected = !option.IsSelected);
            Assert.AreEqual("Second", secondPreset.Name);
            AssertTargetSetEquals(appliedTargetSetBeforeCancel, secondPreset.ToTargetSet());

            Assert.IsTrue(dialog.PersistPlayHistoryFolderDisplayPresetsIfChanged());
            Assert.IsFalse(string.IsNullOrWhiteSpace(testSettings.PlayHistoryDisplayTargetSetsJson));
            IReadOnlyList<PlayHistoryDisplayTargetSet> persistedTargetSets =
                PlayHistoryDisplayTargetSetStore.Deserialize(testSettings.PlayHistoryDisplayTargetSetsJson);
            Assert.AreEqual(2, persistedTargetSets.Count);
            Assert.AreEqual(preset.Name, persistedTargetSets[0].Name);
            CollectionAssert.AreEqual(
                new int?[] { 101 },
                persistedTargetSets[0].Targets.Select(reference => reference.PlaylistId).ToArray());
            Assert.AreEqual("Second", persistedTargetSets[1].Name);
            CollectionAssert.AreEqual(
                new int?[] { 202 },
                persistedTargetSets[1].Targets.Select(reference => reference.PlaylistId).ToArray());

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
            testSettings.PlayHistoryDisplayTargetSetsJson = previousJson;
            TryDeleteDirectory(tempDirectory);
        }
    }

    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditSession_SearchesDisplayNamesUsingCurrentCulture()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            PlayHistoryFolderPresetPlaylistOption istanbul = CreatePresetOption(
                101,
                "Istanbul",
                "SAT");
            PlayHistoryFolderPresetPlaylistOption middle = CreatePresetOption(
                202,
                "Alpha Middle",
                "MXX");
            PlayHistoryFolderPresetPlaylistOption qualifier = CreatePresetOption(
                303,
                "Library",
                "QUEUE");
            var session = new PlayHistoryFolderDisplayPresetEditSession(
                sourcePreset: null,
                name: "Search",
                playlistOptions: [istanbul, middle, qualifier]);

            CollectionAssert.AreEqual(
                new[] { istanbul, middle, qualifier },
                session.FilteredPlaylistOptions.ToArray());

            session.SearchText = "ı";
            CollectionAssert.AreEqual(
                new[] { istanbul },
                session.FilteredPlaylistOptions.ToArray(),
                "Filtered display names: " + string.Join(" | ", session.FilteredPlaylistOptions.Select(option => option.DisplayName)));

            session.SearchText = "PHA MİD";
            CollectionAssert.AreEqual(
                new[] { middle },
                session.FilteredPlaylistOptions.ToArray(),
                "Filtered display names: " + string.Join(" | ", session.FilteredPlaylistOptions.Select(option => option.DisplayName)));

            session.SearchText = "mxx";
            CollectionAssert.AreEqual(new[] { middle }, session.FilteredPlaylistOptions.ToArray());

            session.SearchText = " \t";
            CollectionAssert.AreEqual(
                new[] { istanbul, middle, qualifier },
                session.FilteredPlaylistOptions.ToArray());
            session.SearchText = null!;
            Assert.AreEqual(string.Empty, session.SearchText);
            CollectionAssert.AreEqual(
                new[] { istanbul, middle, qualifier },
                session.FilteredPlaylistOptions.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditSession_PreservesHiddenSelectionAndFilterAcrossSessions()
    {
        PlayHistoryFolderPresetPlaylistOption selectedOption = CreatePresetOption(
            101,
            "Selected",
            "SEL",
            isSelected: true);
        PlayHistoryFolderPresetPlaylistOption otherOption = CreatePresetOption(
            202,
            "Other",
            "OTH");
        var sourcePreset = new PlayHistoryFolderDisplayPresetEditor(
            "Saved",
            [new PlayHistoryDisplayTargetReference { PlaylistId = 101 }]);
        var session = new PlayHistoryFolderDisplayPresetEditSession(
            sourcePreset,
            sourcePreset.Name,
            [selectedOption, otherOption]);

        PlayHistoryDisplayTargetSet sourceBeforeSearch = sourcePreset.ToTargetSet();
        session.SearchText = "no-match";

        Assert.AreEqual(0, session.FilteredPlaylistOptions.Count);
        Assert.IsTrue(selectedOption.IsSelected);
        AssertTargetSetEquals(sourceBeforeSearch, sourcePreset.ToTargetSet());
        CollectionAssert.AreEqual(
            new int?[] { 101 },
            session.ToTargetSet().Targets.Select(reference => reference.PlaylistId).ToArray());

        string persistedJson = PlayHistoryDisplayTargetSetStore.Serialize([session.ToTargetSet()]);
        IReadOnlyList<PlayHistoryDisplayTargetSet> recreatedTargetSets =
            PlayHistoryDisplayTargetSetStore.Deserialize(persistedJson);
        Assert.AreEqual("Saved", recreatedTargetSets.Single().Name);
        CollectionAssert.AreEqual(
            new int?[] { 101 },
            recreatedTargetSets.Single().Targets.Select(reference => reference.PlaylistId).ToArray());

        var reopened = new PlayHistoryFolderDisplayPresetEditSession(
            sourcePreset,
            recreatedTargetSets.Single().Name,
            [
                CreatePresetOption(101, "Selected", "SEL", isSelected: true),
                CreatePresetOption(202, "Other", "OTH")
            ]);
        Assert.AreEqual(string.Empty, reopened.SearchText);
        CollectionAssert.AreEqual(
            reopened.PlaylistOptions.ToArray(),
            reopened.FilteredPlaylistOptions.ToArray());
        reopened.SearchText = "no-match";
        Assert.AreEqual(0, reopened.FilteredPlaylistOptions.Count);
        Assert.IsTrue(reopened.PlaylistOptions.Single(option => option.Table.PlaylistId == 101).IsSelected);
        reopened.SearchText = string.Empty;
        Assert.IsTrue(reopened.FilteredPlaylistOptions.Single(option => option.Table.PlaylistId == 101).IsSelected);
    }

    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditor_ResetSettingsRestoresSavedDraft()
    {
        string previousJson = testSettings.PlayHistoryDisplayTargetSetsJson;
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
            testSettings.PlayHistoryDisplayTargetSetsJson = savedJson;
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            dialog.AddPlayHistoryFolderDisplayPreset();
            dialog.SelectedPlayHistoryFolderDisplayPreset.Name = "Unsaved";

            Assert.IsTrue(dialog.HasPendingSettingChanges());

            dialog.ResetPlayHistoryFolderDisplayPresetsForCancel();

            Assert.AreEqual(savedJson, testSettings.PlayHistoryDisplayTargetSetsJson);
            Assert.AreEqual(1, dialog.PlayHistoryFolderDisplayPresets.Count);
            Assert.AreEqual("Saved", dialog.SelectedPlayHistoryFolderDisplayPreset.Name);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
        }
        finally
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = previousJson;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_PersistsSelectedIdentity()
    {
        string previousJson = testSettings.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = testSettings.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);

            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved")
            ]);
            PlayHistoryDisplayTargetItem target = viewModel.PlayHistory.DisplayTargets.Single(item =>
                item.Kind == PlayHistoryDisplayTargetKind.TargetSet
                && item.Mode == PlayHistoryDisplayTargetMode.ProjectOnly
                && item.TargetSet.Name == "Saved");

            viewModel.PlayHistory.SelectedDisplayTargetIdentity = target.Identity;

            Assert.AreEqual(target.Identity, testSettings.PlayHistorySelectedDisplayTargetIdentity);
        }
        finally
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = previousJson;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_RestoresWhenSavedTargetAppearsLater()
    {
        string previousJson = testSettings.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = testSettings.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = "set-folder:SAVED";
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);

            Assert.AreEqual(PlayHistoryDisplayTargetKind.All, viewModel.PlayHistory.SelectedDisplayTarget.Kind);

            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved")
            ]);

            Assert.AreEqual("set-folder:SAVED", viewModel.PlayHistory.SelectedDisplayTarget.Identity);
        }
        finally
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = previousJson;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_IgnoresTransientEmptyIdentity()
    {
        string previousJson = testSettings.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = testSettings.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);

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
            Assert.AreEqual(target.Identity, testSettings.PlayHistorySelectedDisplayTargetIdentity);
        }
        finally
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = previousJson;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_PersistsExplicitAllIdentity()
    {
        string previousJson = testSettings.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = testSettings.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);

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
            Assert.AreEqual(PlayHistoryDisplayTargetItem.All.Identity, testSettings.PlayHistorySelectedDisplayTargetIdentity);
        }
        finally
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = previousJson;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_BeginPlayHistoryRequestReappliesSavedIdentity()
    {
        string previousJson = testSettings.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = testSettings.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = "set-folder:SAVED";
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);
            ReplacePlayHistoryDisplayTargetSets(viewModel,
            [
                CreateTargetSet("Saved")
            ]);
            viewModel.PlayHistory.SelectedDisplayTarget = PlayHistoryDisplayTargetItem.All;
            viewModel.PlayHistory.RestoreDisplayTargetIdentity("set-folder:SAVED");

            viewModel.PlayHistory.ActivatePeriod(
                PlayHistoryPeriodRequest.All(),
                viewModel.ChartFilters.KeywordFilter);

            Assert.AreEqual("set-folder:SAVED", viewModel.PlayHistory.SelectedDisplayTarget.Identity);
        }
        finally
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = previousJson;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_RequeuesWhenSameIdentityTargetSetChanges()
    {
        string previousJson = testSettings.PlayHistoryDisplayTargetSetsJson;
        string previousSelectedIdentity = testSettings.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
            var viewModel = MainWindowViewModelTestFactory.Create(testSettings);
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
            testSettings.PlayHistoryDisplayTargetSetsJson = PlayHistoryDisplayTargetSetStore.Serialize(
            [
                CreateTargetSet("Saved", playlistId: 202)
            ]);

            viewModel.PlayHistory.RefreshDisplayTargetSetsFromSettings(
                testSettings.PlayHistoryDisplayTargetSetsJson,
                queueRefreshWhenSelectionChanges: true);

            Assert.AreEqual(target.Identity, viewModel.PlayHistory.SelectedDisplayTarget.Identity);
            Assert.AreEqual(202, viewModel.PlayHistory.SelectedDisplayTarget.TargetSet.Targets.Single().PlaylistId);
            Assert.IsTrue(
                owner.DisplayTargetRevision > revisionBeforeChange,
                "The active play-history filter must be re-applied when the selected target set keeps the same identity but changes content.");
        }
        finally
        {
            testSettings.PlayHistoryDisplayTargetSetsJson = previousJson;
            testSettings.PlayHistorySelectedDisplayTargetIdentity = previousSelectedIdentity;
        }
    }

    [TestMethod]
    public void Lr2BmsDirectoryChoices_ExcludeCustomFolderOutputBases()
    {
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
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
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            testSettings.BMSInstallDir = manualBmsRoot;
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

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
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.BMSInstallDir = previousBmsInstallDir;
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
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        string previousSongDbPath = testSettings.LR2SongDBPath;
        string previousConfigXmlPath = testSettings.LR2ConfigXmlPath;
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
        bool previousUsePlayerUbmplay = testSettings.UsePlayeruBMplay;
        bool previousUsePlayerLr2body = testSettings.UsePlayerLR2body;
        bool previousUsePlayerBmidxView = testSettings.UsePlayerBMIIDXView;
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
            config.SetBMSSearchDirectories([parent, child]);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2SongDBPath = songDbPath;
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = parent;
            testSettings.UsePlayeruBMplay = false;
            testSettings.UsePlayerLR2body = false;
            testSettings.UsePlayerBMIIDXView = false;
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            bool isValid = dialog.CheckValidationBeforeSave(out string errMsg);

            Assert.IsFalse(isValid);
            string expectedDetail = string.Format(
                Resources.Validation_NestedBmsSearchRootPathsFormat,
                string.Format(Resources.Validation_NestedBmsSearchRootPathLineFormat, parent, child));
            StringAssert.Contains(errMsg, expectedDetail);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2SongDBPath = previousSongDbPath;
            testSettings.LR2ConfigXmlPath = previousConfigXmlPath;
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.BMSInstallDir = previousBmsInstallDir;
            testSettings.UsePlayeruBMplay = previousUsePlayerUbmplay;
            testSettings.UsePlayerLR2body = previousUsePlayerLr2body;
            testSettings.UsePlayerBMIIDXView = previousUsePlayerBmidxView;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void CheckValidationBeforeSave_RejectsPreviousNormalOutputCoveredOnlyByManualParentRoot()
    {
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        string previousSongDbPath = testSettings.LR2SongDBPath;
        string previousConfigXmlPath = testSettings.LR2ConfigXmlPath;
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
        bool previousUsePlayerUbmplay = testSettings.UsePlayeruBMplay;
        bool previousUsePlayerLr2body = testSettings.UsePlayerLR2body;
        bool previousUsePlayerBmidxView = testSettings.UsePlayerBMIIDXView;
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
            config.SetBMSSearchDirectories([parent]);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2SongDBPath = songDbPath;
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = parent;
            testSettings.UsePlayeruBMplay = false;
            testSettings.UsePlayerLR2body = false;
            testSettings.UsePlayerBMIIDXView = false;
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            bool isValid = dialog.CheckValidationBeforeSave(out string errMsg);

            Assert.IsFalse(isValid);
            string expectedDetail = string.Format(
                Resources.Validation_OutputBaseNestedWithBmsRootFormat,
                Resources.Label_NormalOutputBase,
                normalOutputBase,
                parent);
            StringAssert.Contains(errMsg, expectedDetail);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2SongDBPath = previousSongDbPath;
            testSettings.LR2ConfigXmlPath = previousConfigXmlPath;
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.BMSInstallDir = previousBmsInstallDir;
            testSettings.UsePlayeruBMplay = previousUsePlayerUbmplay;
            testSettings.UsePlayerLR2body = previousUsePlayerLr2body;
            testSettings.UsePlayerBMIIDXView = previousUsePlayerBmidxView;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBase_ChildJukeboxRowsDoNotInvalidateRootOutputBase()
    {
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
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
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = manualBmsRoot;
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            IReadOnlyList<SettingsDialogViewModel.CustomFolderOutputBaseJukeboxAdoptionConflict> conflicts =
                SettingsDialogViewModel.CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(
                    [new SettingsDialogViewModel.CustomFolderOutputBaseCandidate(Resources.Label_RootOutputBase, rootOutputBase)],
                    config.GetBMSSearchDirectoriesForChangeTracking(),
                    [rootOutputBase]);

            Assert.AreEqual(0, conflicts.Count);
            CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories, manualBmsRoot);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, normalOutputBase);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, rootOutputChild);
        }
        finally
        {
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBaseSync_ReplacesAdoptedRootBaseWithPlaylistOutputRoots()
    {
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
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
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            MainWindowViewModel viewModel = CreateViewModel(config);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

            bool changed = ((ISettingsDialogCustomFolderOutputPort)viewModel.PlaylistWorkspace)
                .SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
                    string.Empty,
                    CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings));

            Assert.IsTrue(changed);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), manualBmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), playlistOutputRoot);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), staleOutputRoot);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBaseSync_RegistersAndCreatesMissingPlaylistOutputRoot()
    {
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
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
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            MainWindowViewModel viewModel = CreateViewModel(config);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "MissingPlaylistOutput" }]);

            bool changed = ((ISettingsDialogCustomFolderOutputPort)viewModel.PlaylistWorkspace)
                .SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
                    string.Empty,
                    CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings));

            Assert.IsTrue(changed);
            Assert.IsTrue(Directory.Exists(playlistOutputRoot));
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), manualBmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), playlistOutputRoot);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBaseSync_RejectsRegisteredParentWithoutChangingRootsOrFiles()
    {
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
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
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

            List<string> before = config.GetBMSSearchDirectoriesForChangeTracking();
            byte[] savedXml = File.ReadAllBytes(testSettings.LR2ConfigXmlPath);
            Assert.ThrowsException<ArgumentException>(() =>
                ((ISettingsDialogCustomFolderOutputPort)viewModel.PlaylistWorkspace)
                    .SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
                        string.Empty, CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings)));

            CollectionAssert.AreEqual(before, config.GetBMSSearchDirectoriesForChangeTracking());
            CollectionAssert.AreEqual(savedXml, File.ReadAllBytes(testSettings.LR2ConfigXmlPath));
            Assert.IsFalse(Directory.Exists(playlistOutputRoot));
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NormalOutputBase_AllowsRegisteredRootWithOrWithoutSavedOutputSetting(bool restoreOutput)
    {
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousSongDbPath = testSettings.LR2SongDBPath;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        bool previousUsePlayerUbmplay = testSettings.UsePlayeruBMplay;
        bool previousUsePlayerLr2body = testSettings.UsePlayerLR2body;
        bool previousUsePlayerBmidxView = testSettings.UsePlayerBMIIDXView;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);
            Directory.CreateDirectory(manualBmsRoot);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([manualBmsRoot, normalOutputBase]);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2SongDBPath = songDbPath;
            testSettings.LR2CustomFolderOutputBaseDir = restoreOutput ? string.Empty : normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = manualBmsRoot;
            testSettings.UsePlayeruBMplay = false;
            testSettings.UsePlayerLR2body = false;
            testSettings.UsePlayerBMIIDXView = false;
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            dialog.LR2CustomFolderOutputDir = normalOutputBase;
            Assert.AreEqual(normalOutputBase, dialog.LR2CustomFolderOutputDir);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, normalOutputBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), normalOutputBase);

            bool isValid = dialog.CheckValidationBeforeSave(out string errMsg);

            Assert.IsTrue(isValid, errMsg);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.LR2SongDBPath = previousSongDbPath;
            testSettings.BMSInstallDir = previousBmsInstallDir;
            testSettings.UsePlayeruBMplay = previousUsePlayerUbmplay;
            testSettings.UsePlayerLR2body = previousUsePlayerLr2body;
            testSettings.UsePlayerBMIIDXView = previousUsePlayerBmidxView;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void AdditionalOutputBase_PreviousNormalOutputBaseRequiresAdoptionWarning()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        string previousNormalOutput = Path.Combine(tempRootPath, "NormalOutput");

        IReadOnlyList<SettingsDialogViewModel.CustomFolderOutputBaseJukeboxAdoptionConflict> conflicts =
            SettingsDialogViewModel.CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(
                [new SettingsDialogViewModel.CustomFolderOutputBaseCandidate(Resources.Label_AdditionalOutputBaseFolder, previousNormalOutput)],
                [previousNormalOutput],
                [],
                previousNormalOutput);

        Assert.AreEqual(1, conflicts.Count);
        Assert.AreEqual(previousNormalOutput, conflicts[0].OutputBasePath);
        Assert.AreEqual(previousNormalOutput, conflicts[0].JukeboxRootPath);
    }

    [TestMethod]
    public void NormalOutputBase_CannotAdoptPreviousAdditionalOutputBaseAsNewNormalRoot()
    {
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousSongDbPath = testSettings.LR2SongDBPath;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        bool previousUsePlayerUbmplay = testSettings.UsePlayeruBMplay;
        bool previousUsePlayerLr2body = testSettings.UsePlayerLR2body;
        bool previousUsePlayerBmidxView = testSettings.UsePlayerBMIIDXView;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string previousAdditionalOutputBase = Path.Combine(tempRootPath, "AdditionalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(previousAdditionalOutputBase);
            Directory.CreateDirectory(rootOutputBase);
            Directory.CreateDirectory(manualBmsRoot);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([manualBmsRoot, normalOutputBase, previousAdditionalOutputBase]);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2SongDBPath = songDbPath;
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([previousAdditionalOutputBase]);
            testSettings.BMSInstallDir = manualBmsRoot;
            testSettings.UsePlayeruBMplay = false;
            testSettings.UsePlayerLR2body = false;
            testSettings.UsePlayerBMIIDXView = false;
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            testSettings.LR2CustomFolderOutputBaseDir = previousAdditionalOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();

            bool isValid = dialog.CheckValidationBeforeSave(out string errMsg);

            Assert.IsFalse(isValid);
            StringAssert.Contains(errMsg, Resources.Label_PreviousAdditionalOutputBase);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.LR2SongDBPath = previousSongDbPath;
            testSettings.BMSInstallDir = previousBmsInstallDir;
            testSettings.UsePlayeruBMplay = previousUsePlayerUbmplay;
            testSettings.UsePlayerLR2body = previousUsePlayerLr2body;
            testSettings.UsePlayerBMIIDXView = previousUsePlayerBmidxView;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void NormalOutputBase_CannotAdoptPreviousRootOutputBaseAsNewNormalRoot()
    {
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousSongDbPath = testSettings.LR2SongDBPath;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        bool previousUsePlayerUbmplay = testSettings.UsePlayeruBMplay;
        bool previousUsePlayerLr2body = testSettings.UsePlayerLR2body;
        bool previousUsePlayerBmidxView = testSettings.UsePlayerBMIIDXView;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string previousRootOutputBasePath = Path.Combine(tempRootPath, "RootOutput");
            string currentRootOutputBase = Path.Combine(tempRootPath, "NewRootOutput");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(previousRootOutputBasePath);
            Directory.CreateDirectory(currentRootOutputBase);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([normalOutputBase]);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2SongDBPath = songDbPath;
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBasePath;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = normalOutputBase;
            testSettings.UsePlayeruBMplay = false;
            testSettings.UsePlayerLR2body = false;
            testSettings.UsePlayerBMIIDXView = false;
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            testSettings.LR2CustomFolderOutputBaseDirRootType = currentRootOutputBase;
            testSettings.LR2CustomFolderOutputBaseDir = previousRootOutputBasePath;

            bool isValid = dialog.CheckValidationBeforeSave(out string errMsg);

            Assert.IsFalse(isValid);
            StringAssert.Contains(errMsg, Resources.Label_PreviousRootOutputBase);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.LR2SongDBPath = previousSongDbPath;
            testSettings.BMSInstallDir = previousBmsInstallDir;
            testSettings.UsePlayeruBMplay = previousUsePlayerUbmplay;
            testSettings.UsePlayerLR2body = previousUsePlayerLr2body;
            testSettings.UsePlayerBMIIDXView = previousUsePlayerBmidxView;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBase_CanAdoptManualBmsRootParentWithSaveWarning()
    {
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
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
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            dialog.LR2CustomFolderAsRootOutputDir = manualBmsRootParent;

            IReadOnlyList<SettingsDialogViewModel.CustomFolderOutputBaseJukeboxAdoptionConflict> conflicts =
                SettingsDialogViewModel.CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(
                    [new SettingsDialogViewModel.CustomFolderOutputBaseCandidate(Resources.Label_RootOutputBase, manualBmsRootParent)],
                    config.GetBMSSearchDirectoriesForChangeTracking(),
                    [rootOutputBase]);

            Assert.AreEqual(manualBmsRootParent, testSettings.LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual(1, conflicts.Count);
            Assert.AreEqual(manualBmsRootParent, conflicts[0].OutputBasePath);
            Assert.AreEqual(manualBmsRoot, conflicts[0].JukeboxRootPath);
        }
        finally
        {
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void AdditionalOutputBaseRename_InvalidNameDoesNotChangeList()
    {
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
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
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            dialog.SelectedCustomFolderAdditionalOutputBaseDir = additionalOutputBase;

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseRegistry.RenameLastDirectoryName(additionalOutputBase, "."));

            CollectionAssert.Contains(dialog.CustomFolderAdditionalOutputBaseDirList, additionalOutputBase);
            CollectionAssert.DoesNotContain(dialog.CustomFolderAdditionalOutputBaseDirList, additionalOutputBaseParent);
        }
        finally
        {
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void NormalOutputBaseSearchRootSync_RemovesOldNormalOutputBase()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string oldNormalOutputBase = Path.Combine(tempRootPath, "OldNormalOutput");
            string newNormalOutputBase = Path.Combine(tempRootPath, "NewNormalOutput");
            Directory.CreateDirectory(oldNormalOutputBase);
            Directory.CreateDirectory(newNormalOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([oldNormalOutputBase]);
            CustomFolderOutputBaseSearchRootSyncPlan plan = CustomFolderOutputBaseSearchRootSyncService.PrepareNormalOutputBaseRoots(
                config,
                oldNormalOutputBase,
                newNormalOutputBase,
                "[]",
                "[]");
            CustomFolderOutputBaseSearchRootSyncService.CompleteAdditionalOutputBaseRootSync(config, plan);

            CollectionAssert.Contains(plan.RemovedPaths.ToArray(), oldNormalOutputBase);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), oldNormalOutputBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), newNormalOutputBase);
        }
        finally
        {
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void StartupRepair_DoesNotRewriteManagedOutputInstallDir()
    {
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        string previousConfigXmlPath = testSettings.LR2ConfigXmlPath;
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
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
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = rootOutputChild;

            MainWindowViewModel viewModel = CreateViewModel(config);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            string savedDialogInstallDir = dialog.BMSInstallDir;
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

            bool changed = viewModel.PlaylistWorkspace.RepairRootCustomFolderOutputSearchRootsAfterStartup(
                CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings));

            Assert.IsTrue(changed);
            Assert.AreEqual(rootOutputChild, testSettings.BMSInstallDir);
            Assert.AreEqual(savedDialogInstallDir, dialog.BMSInstallDir);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), manualBmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), normalOutputBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), rootOutputChild);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);

            testSettings.BMSInstallDir = manualBmsRoot;
            dialog.ResetSettings();

            Assert.AreEqual(savedDialogInstallDir, dialog.BMSInstallDir);
            Assert.AreEqual(rootOutputChild, testSettings.BMSInstallDir);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2ConfigXmlPath = previousConfigXmlPath;
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void StartupRepair_UsesCapturedSettingsAfterEditSessionChanges()
    {
        bool previousOperationMode = testSettings.OperationModeLR2DB;
        string previousConfigXmlPath = testSettings.LR2ConfigXmlPath;
        string previousNormalOutputBase = testSettings.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = testSettings.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = testSettings.BMSInstallDir;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            string changedRootOutputBase = Path.Combine(tempRootPath, "ChangedRootOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([manualBmsRoot, normalOutputBase, rootOutputBase, rootOutputChild]);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = rootOutputChild;

            MainWindowViewModel viewModel = CreateViewModel(config);
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

            CustomFolderOutputSettingsSnapshot startupSettings =
                CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings);
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempRootPath, "ChangedNormalOutput");
            testSettings.LR2CustomFolderOutputBaseDirRootType = changedRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[\"changed-additional\"]";
            testSettings.BMSInstallDir = manualBmsRoot;

            bool changed = viewModel.PlaylistWorkspace.RepairRootCustomFolderOutputSearchRootsAfterStartup(
                startupSettings);

            Assert.IsTrue(changed);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), rootOutputChild);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), changedRootOutputBase);
        }
        finally
        {
            testSettings.OperationModeLR2DB = previousOperationMode;
            testSettings.LR2ConfigXmlPath = previousConfigXmlPath;
            testSettings.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            testSettings.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    private MainWindowViewModel CreateViewModel(LR2Config config)
    {
        testSettings.OperationModeLR2DB = true;
        testSettings.LR2ConfigXmlPath = Path.Combine(config.LR2RootPath, "LR2files", "Config", "config.xml");
        config.Save();
        var viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        // The test factory intentionally does not run startup initialization. Keep the
        // in-memory LR2 configuration that owns the prepared fixture available to the
        // workspace's typed custom-folder output port.
        SetViewModelField(viewModel, "lr2config", config);
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

    private static void SetViewModelField(MainWindowViewModel viewModel, string fieldName, object value)
    {
        typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    private void ReplacePlayHistoryDisplayTargetSets(
        MainWindowViewModel viewModel,
        IEnumerable<PlayHistoryDisplayTargetSet> targetSets)
    {
        string serializedTargetSets = PlayHistoryDisplayTargetSetStore.Serialize(targetSets);
        testSettings.PlayHistoryDisplayTargetSetsJson = serializedTargetSets;
        viewModel.PlayHistory.RefreshDisplayTargetSetsFromSettings(
            serializedTargetSets,
            queueRefreshWhenSelectionChanges: false);
    }

    private void SetViewModelTables(MainWindowViewModel viewModel, string songDbPath, BMSTable[] tables)
    {
        var playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings);
        playlist.BMSTables = new ObservableCollection<BMSTable>(tables);
        SetViewModelField(viewModel, "tables", playlist);
        viewModel.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
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

    private static PlayHistoryFolderPresetPlaylistOption CreatePresetOption(
        int playlistId,
        string name,
        string symbol,
        bool isSelected = false)
    {
        return new PlayHistoryFolderPresetPlaylistOption(
            PlaylistTablePresentationSnapshot.From(CreatePresetTable(playlistId, name, symbol)),
            isSelected,
            selectionChanged: null);
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
