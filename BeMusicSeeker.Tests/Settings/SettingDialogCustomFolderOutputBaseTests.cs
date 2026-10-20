using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>設定判断と実 LR2 XML・出力窓口の配送を確認します。描画接続は compiled category/Window 系列が分担します。</remarks>
[TestClass]
public sealed class SettingDialogCustomFolderOutputBaseTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings(
        values => values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson);
    /// <summary>保存比較値から確認設定・動作モードを変更して差分を検出し、明示 Reset で保存済み値と差分なし状態へ戻ることを確認します。</summary>
    [TestMethod]
    [DoNotParallelize] // ResetSettings が共有テーマと文化を復元します。
    public void HasPendingSettingChanges_UsesSnapshotDiffsAndReset()
    {
        string previousTheme = Settings.Default.AppearanceTheme;
        CultureInfo? previousResourceCulture = Resources.Culture;
        try
        {
            bool previousShowNewPackageInstallConfirmMsg = testSettings.ShowNewPackageInstallConfirmMsg;
            bool previousOperationMode = testSettings.OperationModeLR2DB;
            bool previousShowRecommUpdatedMsg = testSettings.ShowRecommUpdatedMsg;
            bool previousShowDuplicateFileCheckConfirmMsg = testSettings.ShowDuplicateFileCheckConfirmMsg;
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
            using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            testSettings.ShowRecommUpdatedMsg = !previousShowRecommUpdatedMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());

            testSettings.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            testSettings.ShowDuplicateFileCheckConfirmMsg = !previousShowDuplicateFileCheckConfirmMsg;

            Assert.IsTrue(dialog.HasPendingSettingChanges());

            testSettings.ShowDuplicateFileCheckConfirmMsg = previousShowDuplicateFileCheckConfirmMsg;

            Assert.IsFalse(dialog.HasPendingSettingChanges());

            dialog.ShowNewPackageInstallConfirmMsg = !previousShowNewPackageInstallConfirmMsg;
            Assert.IsTrue(dialog.HasPendingSettingChanges());
            dialog.ResetSettings();
            Assert.AreEqual(previousShowNewPackageInstallConfirmMsg, dialog.ShowNewPackageInstallConfirmMsg);
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
            AppThemeService.ApplyTheme(previousTheme);
            if (previousResourceCulture == null) { Resources.Culture = null; }
            else { ResourceService.Current.ChangeCulture(previousResourceCulture.Name); }
        }
    }

    /// <summary>profile 有効化だけを変更しても draft 差分を作らず、実設定を変えたときだけ未保存として検出することを確認します。</summary>
    [TestMethod]
    public void PendingSettingsRemainIndependentOfActiveLibraryProfile()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
        SettingsDialogViewModel dialog = viewModel.SettingDialog;

        Assert.IsFalse(dialog.HasPendingSettingChanges());

        SetViewModelField(viewModel, "hasActiveLibraryProfile", true);

        Assert.IsFalse(dialog.HasPendingSettingChanges());

        testSettings.ShowRecommUpdatedMsg = !testSettings.ShowRecommUpdatedMsg;

        Assert.IsTrue(dialog.HasPendingSettingChanges());
    }

    /// <summary>同名の異なる表を ID で区別し、無効編集と取消では draft を維持し、有効編集だけを保存・実選択肢へ配送することを確認します。</summary>
    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditor_DraftsSaveValidationAndIdMatching()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (new LR2SongDBExtended(songDbPath))
            {
            }
            testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
            using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
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

            // 二つの保存 preset の二種類の意味と二つの表が選択肢へ届くことを確認します。
            PlayHistoryDisplayTargetItem[] presetTargets = viewModel.PlayHistory.DisplayTargets
                .Where(item => item.Kind == PlayHistoryDisplayTargetKind.TargetSet).ToArray();
            foreach (PlayHistoryDisplayTargetMode mode in new[]
                { PlayHistoryDisplayTargetMode.FilterAndProject, PlayHistoryDisplayTargetMode.ProjectOnly })
            {
                CollectionAssert.AreEquivalent(new[] { preset.Name, "Second" },
                    presetTargets.Where(item => item.Mode == mode).Select(item => item.TargetSet.Name).ToArray());
            }
            Assert.IsTrue(viewModel.PlayHistory.DisplayTargets.Any(item => item.Kind == PlayHistoryDisplayTargetKind.All));
            CollectionAssert.AreEquivalent(new int?[] { 101, 202 }, viewModel.PlayHistory.DisplayTargets
                .Where(item => item.Kind == PlayHistoryDisplayTargetKind.Playlist)
                .Select(item => item.Table.playlist_id).ToArray());
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>トルコ語の大文字小文字・中間語・記号検索と空入力を与え、表示名の文化に従う検索と全候補復帰を確認します。</summary>
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

    /// <summary>検索で隠した選択を保存対象に残し、再作成した編集 session で検索文字だけをリセットして選択を失わないことを確認します。</summary>
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

    /// <summary>追加・改名した preset を取消用 Reset で保存済み JSON へ戻し、未保存 draft が残る誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditor_ResetSettingsRestoresSavedDraft()
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
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
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

    /// <summary>利用者が preset の投影だけの対象を選択すると、正しい識別子を設定へ保存する接続を確認します。</summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_PersistsSelectedIdentity()
    {
        testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
        testSettings.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;

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

    /// <summary>保存対象が初めはない場合に全体表示し、対象が後で現れたとき保存識別子へ復帰することを確認します。</summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_RestoresWhenSavedTargetAppearsLater()
    {
        testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
        testSettings.PlayHistorySelectedDisplayTargetIdentity = "set-folder:SAVED";
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;

        Assert.AreEqual(PlayHistoryDisplayTargetKind.All, viewModel.PlayHistory.SelectedDisplayTarget.Kind);

        ReplacePlayHistoryDisplayTargetSets(viewModel,
        [
            CreateTargetSet("Saved")
        ]);

        Assert.AreEqual("set-folder:SAVED", viewModel.PlayHistory.SelectedDisplayTarget.Identity);
    }

    /// <summary>有効な対象の選択後に null と空の一時通知が来ても、選択と保存識別子を消さないことを確認します。</summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_IgnoresTransientEmptyIdentity()
    {
        testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
        testSettings.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;

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

    /// <summary>全体表示を明示選択した場合は、空の一時通知と区別して全体の識別子を保存することを確認します。</summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_PersistsExplicitAllIdentity()
    {
        testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
        testSettings.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;

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

    /// <summary>履歴要求の開始時に保存 preset を再適用し、一時的な全体選択のまま照会を始める誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_BeginPlayHistoryRequestReappliesSavedIdentity()
    {
        testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
        testSettings.PlayHistorySelectedDisplayTargetIdentity = "set-folder:SAVED";
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
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

    /// <summary>識別子を保ったまま対象表を変更し、新内容と表示世代を更新して古い選択内容を再利用する誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetSelection_RequeuesWhenSameIdentityTargetSetChanges()
    {
        testSettings.PlayHistoryDisplayTargetSetsJson = string.Empty;
        testSettings.PlayHistorySelectedDisplayTargetIdentity = string.Empty;
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel viewModelSettingsLifetime = viewModel.SettingDialog;
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

    /// <summary>実LR2登録で現在/変更前の出力先を除き、親・兄弟と欠落登録・順序を保ちます。ROOT出力ありでは取得内のスナップショットを共有し、ROOT出力なし・候補0件では取得しません。</summary>
    [DataTestMethod]
    [DataRow(true)] // 現在/変更前ROOT出力を同じスナップショットで計算し、次の取得では入力変更を採用する。
    [DataRow(false)] // ROOT出力が両方空なら取得せず、通常/追加出力の除外と残る候補を保つ。
    public void Lr2BmsDirectoryChoices_ExcludeCustomFolderOutputBases(bool configureRootOutputs)
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manual = Path.Combine(root, "Manual");
            string missing = Path.Combine(root, "Missing");
            string oldNormal = Path.Combine(root, "Old", "Normal");
            string newNormal = Path.Combine(root, "New", "Normal");
            string oldAdditional = Path.Combine(root, "Old", "Additional");
            string newAdditional = Path.Combine(root, "New", "Additional");
            string oldRoot = Path.Combine(root, "Old", "Root");
            string newRoot = Path.Combine(root, "New", "Root");
            string oldOutside = Path.Combine(root, "Old", "Outside");
            string newOutside = Path.Combine(root, "New", "Outside");
            string changedOutside = Path.Combine(root, "New", "ChangedOutside");
            string parent = Path.Combine(root, "Old");
            string sibling = oldNormal + "Sibling";
            string[] registered = [manual, missing, parent, oldNormal, Path.Combine(oldNormal, "Child"), sibling,
                oldAdditional, Path.Combine(oldRoot, "Playlist"), oldOutside, newNormal,
                Path.Combine(newNormal, "Child"), newAdditional, Path.Combine(newRoot, "Playlist"), newOutside, changedOutside];
            foreach (string directory in registered.Where(path => path != missing)) { Directory.CreateDirectory(directory); }
            LR2Config config = CreateConfig(root);
            config.SetBMSSearchDirectories(registered);
            testSettings.LR2CustomFolderOutputBaseDir = oldNormal;
            testSettings.LR2CustomFolderOutputBaseDirRootType = configureRootOutputs ? oldRoot : string.Empty;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([oldAdditional]);
            testSettings.BMSInstallDir = manual;
            MainWindowViewModel owner = CreateViewModel(config);
            owner.SettingDialog.Dispose();
            var workspace = new CountingPlaylistSnapshotWorkspace(owner.PlaylistWorkspace)
            {
                Snapshots = [new PlaylistTablePresentationSnapshot(1, "Root", "Root", "R", "R", true,
                    Path.Combine("..", "Outside"), string.Empty)]
            };
            using var dialog = new SettingsDialogViewModel(owner, workspace, owner.PlaylistWorkspace, owner.PlayHistory,
                owner.LibraryFolderTree, new TestSettingsDialogPlayerFactoryPort(), new TestSettingsDialogPlaybackRuntimePort(),
                owner.Lr2SongDbSyncWorkflow, new NoOpSettingsEditSession(testSettings),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                externalShellGateway: ExternalShellGatewayPolicy.Current, applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(), audioSettingsGateway: new TestAudioSettingsGateway(),
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
            testSettings.LR2CustomFolderOutputBaseDir = newNormal;
            testSettings.LR2CustomFolderOutputBaseDirRootType = configureRootOutputs ? newRoot : string.Empty;
            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();
            dialog.CustomFolderAdditionalOutputBaseDirList.Add(newAdditional);
            string[] expectedWithRootOutputs = [manual, missing, parent, sibling, changedOutside];
            string[] expectedWithoutRootOutputs = [manual, missing, parent, sibling,
                Path.Combine(oldRoot, "Playlist"), oldOutside, Path.Combine(newRoot, "Playlist"), newOutside, changedOutside];
            int expectedCaptureCount = configureRootOutputs ? 1 : 0;
            workspace.CaptureCount = 0;
            CollectionAssert.AreEqual(configureRootOutputs ? expectedWithRootOutputs : expectedWithoutRootOutputs,
                dialog.LR2ConfigBMSDirectories);
            Assert.AreEqual(expectedCaptureCount, workspace.CaptureCount, "必要なROOT計算だけで全登録ルートのスナップショットを共有します。");
            workspace.CaptureCount = 0;
            testSettings.BMSInstallDir = missing;
            Assert.AreEqual(Resources.Error_InvalidBmsInstallDir, dialog.BmsInstallDirValidationMessage, "処理用の存在確認では欠落登録を有効にしません。");
            Assert.AreEqual(expectedCaptureCount, workspace.CaptureCount);
            workspace.CaptureCount = 0;
            testSettings.BMSInstallDir = manual;
            Assert.AreEqual(string.Empty, dialog.BmsInstallDirValidationMessage);
            Assert.AreEqual(expectedCaptureCount, workspace.CaptureCount);
            workspace.Snapshots = [new PlaylistTablePresentationSnapshot(1, "Root", "Root", "R", "R", true,
                Path.Combine("..", "ChangedOutside"), string.Empty)];
            workspace.CaptureCount = 0;
            string[] expectedAfterRootChange = [manual, missing, parent, sibling, oldOutside, newOutside];
            CollectionAssert.AreEqual(configureRootOutputs ? expectedAfterRootChange : expectedWithoutRootOutputs,
                dialog.LR2ConfigBMSDirectories);
            Assert.AreEqual(expectedCaptureCount, workspace.CaptureCount, "前回取得のスナップショットを永続キャッシュとして使い回しません。");

            // 欠落登録は変更検出に残るが、実処理候補は0件なので除外計算も全件取得も不要。
            config.SetBMSSearchDirectories([missing]);
            config.Save();
            dialog.LR2ConfigXmlPath = testSettings.LR2ConfigXmlPath;
            workspace.CaptureCount = 0;
            CollectionAssert.AreEqual(new[] { missing }, dialog.LR2ConfigBMSDirectories);
            Assert.AreEqual(expectedCaptureCount, workspace.CaptureCount);
            testSettings.BMSInstallDir = missing;
            workspace.CaptureCount = 0;
            Assert.AreEqual(Resources.Error_InvalidBmsInstallDir, dialog.BmsInstallDirValidationMessage);
            Assert.AreEqual(0, workspace.CaptureCount, "存在する候補が0件ならROOT設定があっても取得しません。");

            // 空登録では表示用・実処理用のどちらも全件スナップショットを追加しない。
            config.SetBMSSearchDirectories([]);
            config.Save();
            dialog.LR2ConfigXmlPath = testSettings.LR2ConfigXmlPath;
            workspace.CaptureCount = 0;
            Assert.AreEqual(0, dialog.LR2ConfigBMSDirectories.Count);
            Assert.AreEqual(0, workspace.CaptureCount);
            Assert.AreEqual(Resources.Error_InvalidBmsInstallDir, dialog.BmsInstallDirValidationMessage);
            Assert.AreEqual(0, workspace.CaptureCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>親子と無関係な兄弟パスを混ぜ、親子だけを正しい方向で競合として返すことを確認します。</summary>
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

    /// <summary>実 LR2 登録に親子を保存し、保存前検証が両対象を示す既存リソースで拒否することを確認します。</summary>
    [TestMethod]
    public void CheckValidationBeforeSave_RejectsNestedLr2JukeboxRoots()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>旧通常出力先が手動親ルートの配下だけで覆われる場合、過去の出力指定を理由に許可せず対象付きで拒否することを確認します。</summary>
    [TestMethod]
    public void CheckValidationBeforeSave_RejectsPreviousNormalOutputCoveredOnlyByManualParentRoot()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>既存ルート出力先の子登録は採用競合にせず、出力領域を候補から除いて手動ルートを保つことを確認します。</summary>
    [TestMethod]
    public void RootOutputBase_ChildJukeboxRowsDoNotInvalidateRootOutputBase()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>採用済みルート出力先と古い子登録を実表の出力子へ揃え、手動ルートを消したり古い出力先を残す誤りを検出します。</summary>
    [TestMethod]
    public void RootOutputBaseSync_ReplacesAdoptedRootBaseWithPlaylistOutputRoots()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>まだない表出力先を実作成・登録し、親基点を外しながら手動ルートを保つことを確認します。</summary>
    [TestMethod]
    public void RootOutputBaseSync_RegistersAndCreatesMissingPlaylistOutputRoot()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>出力先を包含する手動親登録がある同期要求を拒否し、実 XML と登録と出力ディレクトリを変更しないことを確認します。</summary>
    [TestMethod]
    public void RootOutputBaseSync_RejectsRegisteredParentWithoutChangingRootsOrFiles()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>通常出力先の保存指定あり・復元指定なしの双方で、同一登録を出力先へ採用でき、譜面候補だけから除くことを確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NormalOutputBase_AllowsRegisteredRootWithOrWithoutSavedOutputSetting(bool restoreOutput)
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>旧通常出力先を追加出力へ転用する要求を、同一登録を示す採用確認対象として返すことを確認します。</summary>
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

    /// <summary>追加指定を編集で消しても旧追加領域を通常出力へ転用する保存を拒否し、旧役割の保護を失う誤りを検出します。</summary>
    [TestMethod]
    public void NormalOutputBase_CannotAdoptPreviousAdditionalOutputBaseAsNewNormalRoot()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>ルート出力先を別の場所へ移しても旧領域の通常出力への転用を拒否し、旧役割の保護を失う誤りを検出します。</summary>
    [TestMethod]
    public void NormalOutputBase_CannotAdoptPreviousRootOutputBaseAsNewNormalRoot()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            testSettings.LR2CustomFolderOutputBaseDirRootType = currentRootOutputBase;
            testSettings.LR2CustomFolderOutputBaseDir = previousRootOutputBasePath;

            bool isValid = dialog.CheckValidationBeforeSave(out string errMsg);

            Assert.IsFalse(isValid);
            StringAssert.Contains(errMsg, Resources.Label_PreviousRootOutputBase);
        }
        finally
        {
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>手動譜面ルートの親をルート型出力へ選択でき、採用確認の比較元として親出力と子登録を返すことを確認します。</summary>
    [TestMethod]
    public void RootOutputBase_CanAdoptManualBmsRootParentWithSaveWarning()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
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
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>末尾名に現在ディレクトリ表現を指定し、出力先の名前変更要求を ArgumentException で拒否する既存判断を確認します。</summary>
    [TestMethod]
    public void AdditionalOutputBaseRename_RejectsCurrentDirectoryName()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        string additionalOutputBase = Path.Combine(root, "AdditionalOutput");
        Assert.ThrowsException<ArgumentException>(() =>
            CustomFolderOutputBaseRegistry.RenameLastDirectoryName(additionalOutputBase, "."));
    }

    /// <summary>通常出力先を移す同期で旧登録を外し新登録を実 LR2 設定へ追加することを確認します。</summary>
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

    /// <summary>実出力登録を修復しても導入先を変更せず、後の Reset が保存済み導入先を復元することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize] // ResetSettings が共有テーマと文化を復元します。
    public void StartupRepair_DoesNotRewriteManagedOutputInstallDir()
    {
        string previousTheme = Settings.Default.AppearanceTheme;
        CultureInfo? previousResourceCulture = Resources.Culture;
        try
        {
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
                using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
                SettingsDialogViewModel dialog = viewModel.SettingDialog;
                string savedDialogInstallDir = dialog.BMSInstallDir;
                string songDbPath = Path.Combine(tempRootPath, "song.db");
                File.WriteAllBytes(songDbPath, []);
                SetViewModelTables(
                    viewModel,
                    songDbPath,
                    [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

                var startupSettings = CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings);
                bool changed = ((ISettingsDialogCustomFolderOutputPort)viewModel.PlaylistWorkspace)
                    .SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(startupSettings.LR2CustomFolderOutputBaseDirRootType, startupSettings);

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
                TryDeleteDirectory(tempRootPath);
            }
        }
        finally
        {
            AppThemeService.ApplyTheme(previousTheme);
            if (previousResourceCulture == null) { Resources.Culture = null; }
            else { ResourceService.Current.ChangeCulture(previousResourceCulture.Name); }
        }
    }

    /// <summary>捕捉後に出力 draft を変更して同期し、捕捉済み旧出力構成を使い後の編集場所へ同期を逸らす誤りを検出します。</summary>
    [TestMethod]
    public void StartupRepair_UsesCapturedSettingsAfterEditSessionChanges()
    {
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
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            SetViewModelTables(
                viewModel,
                songDbPath,
                [new BMSTable { is_root_folder = true, Output_dir = "PlaylistOutput" }]);

            var startupSettings =
                CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings);
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempRootPath, "ChangedNormalOutput");
            testSettings.LR2CustomFolderOutputBaseDirRootType = changedRootOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[\"changed-additional\"]";
            testSettings.BMSInstallDir = manualBmsRoot;

            bool changed = ((ISettingsDialogCustomFolderOutputPort)viewModel.PlaylistWorkspace)
                .SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(startupSettings.LR2CustomFolderOutputBaseDirRootType, startupSettings);

            Assert.IsTrue(changed);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), rootOutputChild);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), changedRootOutputBase);
        }
        finally
        {
            TryDeleteDirectory(tempRootPath);
        }
    }

    private sealed class CountingPlaylistSnapshotWorkspace(ISettingsDialogWorkspacePort inner) : ISettingsDialogWorkspacePort
    {
        internal IReadOnlyList<PlaylistTablePresentationSnapshot> Snapshots { get; set; } = [];
        internal int CaptureCount { get; set; }
        public bool HasPlaylistTables => true;
        public long PlaylistCatalogVersion => inner.PlaylistCatalogVersion;
        public IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots()
        {
            CaptureCount++;
            return Snapshots;
        }
        public bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string path)
            => inner.HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(path);
        public void SchedulePlaylistUrlCompletionRefresh(string reason) => inner.SchedulePlaylistUrlCompletionRefresh(reason);
        public Task ExportBeatorajaBmtAsync(string reason, string path, LibraryFileMutationCapability capability, bool forceProjection = false)
            => inner.ExportBeatorajaBmtAsync(reason, path, capability, forceProjection);
        public Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string name)
            => inner.RunWithPlaylistOperationNotificationsAsync(operation, name);
        public event EventHandler<PlaylistCatalogChangedEventArgs> PlaylistCatalogChanged
        {
            add => inner.PlaylistCatalogChanged += value;
            remove => inner.PlaylistCatalogChanged -= value;
        }
    }

    private MainWindowViewModel CreateViewModel(LR2Config config)
    {
        testSettings.OperationModeLR2DB = true;
        testSettings.LR2ConfigXmlPath = Path.Combine(config.LR2RootPath, "LR2files", "Config", "config.xml");
        config.Save();
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings);
        // 初期化を走らせない fixture の実 LR2 設定を出力窓口へ渡します。
        // SettingsDialog の購読 lifetime は各呼出元が実観測の終端まで所有します。
        try
        {
            SetViewModelField(viewModel, "lr2config", config);
            return viewModel;
        }
        catch
        {
            viewModel.SettingDialog.Dispose();
            throw;
        }
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
        TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings);
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
