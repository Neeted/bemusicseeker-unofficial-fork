using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.PlayHistoryPresetTestFixture;

namespace BeMusicSeeker.Tests;

/// <remarks>実root・設定VMの共有ResourceService購読と実LR2 XML・出力窓口の配送を非並列で確認します。描画接続はcompiled category/Window系列が分担します。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingDialogCustomFolderOutputBaseTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>このケースの準備とモデルで共有するDBの所有者です。</summary>
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実portの準備結果を同じ構成のXML所有者へ保存・公開します。設定本体の二媒体・一回保存は局所確定テストが分担します。</summary>
    private bool SavePreparedCustomFolderSearchRoots(MainWindowViewModel viewModel,
        string previousRoot, CustomFolderOutputSettingsSnapshot settings)
    {
        Lr2ConfigOwner owner = MainWindowViewModelTestFactory.GetComposition(viewModel).ResolveLr2Config(testSettings.LR2ConfigXmlPath);
        IReadOnlyList<string> before = owner.CaptureSaved().RegisteredRoots;
        IReadOnlyList<string> next = ((ISettingsDialogCustomFolderOutputPort)viewModel.PlaylistWorkspace)
            .PrepareCustomFolderOutputSearchRoots(previousRoot, settings, before);
        bool changed = !next.SequenceEqual(before, StringComparer.OrdinalIgnoreCase);
        if (changed) { owner.SaveRoots(new Lr2RootChange(before, next)); }
        return changed;
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings(
        values => values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson);



    /// <summary>同名の異なる表を ID で区別し、無効編集と取消では確定recordを維持し、有効編集だけを保存・実選択肢へ配送することを確認します。</summary>
    [TestMethod]
    public void PlayHistoryFolderDisplayPresetEditor_DraftsSaveValidationAndIdMatching()
    {
        string tempDirectory = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (databaseScope.OpenSongWrite(songDbPath))
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

            PlayHistoryFolderDisplayPresetEditSession newPreset = dialog.CreatePlayHistoryFolderDisplayPresetEditSession(null);
            newPreset.PlaylistOptions.First().IsSelected = true;
            Assert.IsTrue(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(newPreset, out string addError), addError);

            PlayHistoryFolderDisplayPresetEditor preset = dialog.PlayHistoryFolderDisplayPresets.Single();
            Assert.AreSame(preset, dialog.SelectedPlayHistoryFolderDisplayPreset);
            Assert.AreEqual(1, PlayHistoryDisplayTargetSetStore.Deserialize(testSettings.PlayHistoryDisplayTargetSetsJson).Count);
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
            PlayHistoryFolderDisplayPresetEditSession anotherPreset = dialog.CreatePlayHistoryFolderDisplayPresetEditSession(null);
            anotherPreset.PlaylistOptions.First().IsSelected = true;
            Assert.IsTrue(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(anotherPreset, out string anotherError), anotherError);
            PlayHistoryFolderDisplayPresetEditSession duplicateSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(dialog.SelectedPlayHistoryFolderDisplayPreset);
            PlayHistoryFolderDisplayPresetEditor secondPreset = dialog.SelectedPlayHistoryFolderDisplayPreset;
            viewModel.PlayHistory.SelectedDisplayTargetIdentity = PlayHistoryDisplayTargetItem.FromTargetSetProjectionOnly(secondPreset.ToTargetSet()).Identity;
            string selectedIdentityBeforeRename = viewModel.PlayHistory.SelectedDisplayTargetIdentity;
            string secondPresetNameBeforeDuplicateValidation = secondPreset.Name;
            PlayHistoryDisplayTargetSet secondPresetTargetSetBeforeDuplicateValidation = secondPreset.ToTargetSet();
            duplicateSession.Name = preset.Name;
            Assert.IsFalse(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(duplicateSession, out string duplicateNameError));
            Assert.AreEqual(selectedIdentityBeforeRename, viewModel.PlayHistory.SelectedDisplayTargetIdentity);
            StringAssert.Contains(duplicateNameError, Resources.Error_PlayHistoryFolderPresetDuplicateName.Split(':')[0]);
            Assert.AreEqual(secondPresetNameBeforeDuplicateValidation, secondPreset.Name);
            AssertTargetSetEquals(secondPresetTargetSetBeforeDuplicateValidation, secondPreset.ToTargetSet());

            PlayHistoryFolderDisplayPresetEditSession editSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(dialog.SelectedPlayHistoryFolderDisplayPreset);
            editSession.Name = "Second";
            editSession.PlaylistOptions.ToList().ForEach(option => option.IsSelected = false);
            SetViewModelField(viewModel, "hasActiveLibraryProfile", true);
            Assert.IsFalse(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(editSession, out string noPlaylistError));
            Assert.AreEqual(selectedIdentityBeforeRename, viewModel.PlayHistory.SelectedDisplayTargetIdentity);
            StringAssert.Contains(noPlaylistError, Resources.Error_PlayHistoryFolderPresetNoPlaylist.Split(':')[0]);
            Assert.AreEqual(secondPresetNameBeforeDuplicateValidation, secondPreset.Name);
            AssertTargetSetEquals(secondPresetTargetSetBeforeDuplicateValidation, secondPreset.ToTargetSet());
            editSession.PlaylistOptions.Last().IsSelected = true;
            editSession.SearchText = "definitely-no-match";
            Assert.AreEqual(0, editSession.FilteredPlaylistOptions.Count);
            Assert.IsTrue(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(editSession, out string applyPresetError), applyPresetError);
            Assert.AreEqual("set-folder:SECOND", viewModel.PlayHistory.SelectedDisplayTargetIdentity);
            Assert.AreEqual("set-folder:SECOND", testSettings.PlayHistorySelectedDisplayTargetIdentity);
            Assert.AreEqual<int?>(202, viewModel.PlayHistory.SelectedDisplayTarget.TargetSet.Targets.Single().PlaylistId);

            PlayHistoryFolderDisplayPresetEditSession canceledEditSession =
                dialog.CreatePlayHistoryFolderDisplayPresetEditSession(secondPreset);
            PlayHistoryDisplayTargetSet appliedTargetSetBeforeCancel = secondPreset.ToTargetSet();
            canceledEditSession.Name = "Canceled";
            canceledEditSession.PlaylistOptions.ToList().ForEach(option => option.IsSelected = !option.IsSelected);
            Assert.AreEqual("Second", secondPreset.Name);
            AssertTargetSetEquals(appliedTargetSetBeforeCancel, secondPreset.ToTargetSet());

            // 確定後の別sessionへの入力は保存済み値を変えません。別record確定時の取消候補非混入はSettingsDialogBehaviorTestsが分担します。
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

    /// <summary>保存済み出力だけをBMS候補から除き、他セクションの出力候補では表示を変更しません。欠落登録と親・兄弟・順序を保ちます。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Lr2BmsDirectoryChoices_ExcludeOnlyConfirmedCustomFolderOutputs(bool configureRootOutputs)
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manual = Path.Combine(root, "Manual");
            string missing = Path.Combine(root, "Missing");
            string normal = Path.Combine(root, "Normal");
            string additional = Path.Combine(root, "Additional");
            string rootOutput = Path.Combine(root, "Root");
            string sibling = normal + "Sibling";
            string candidateNormal = Path.Combine(root, "CandidateNormal");
            string candidateRoot = Path.Combine(root, "CandidateRoot");
            string[] registered = [manual, missing, normal, Path.Combine(normal, "Child"), additional, Path.Combine(rootOutput, "Playlist"), sibling, candidateNormal];
            foreach (string path in registered.Where(path => path != missing)) { Directory.CreateDirectory(path); }
            LR2Config config = CreateConfig(root);
            config.SetBMSSearchDirectories(registered);
            string[] retainedRegistrations = config.GetBMSSearchDirectoriesForChangeTracking().ToArray();
            testSettings.LR2CustomFolderOutputBaseDir = normal;
            testSettings.LR2CustomFolderOutputBaseDirRootType = configureRootOutputs ? rootOutput : string.Empty;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additional]);
            testSettings.BMSInstallDir = manual;
            MainWindowViewModel owner = CreateViewModel(config);
            using SettingsDialogViewModel dialog = owner.SettingDialog;
            string[] expected = configureRootOutputs
                ? [manual, missing, sibling, candidateNormal]
                : [manual, missing, Path.Combine(rootOutput, "Playlist"), sibling, candidateNormal];
            CollectionAssert.AreEqual(expected, dialog.LR2ConfigBMSDirectories);
            CollectionAssert.AreEqual(expected, dialog.ConfirmedBMSDirectories.ToArray());
            dialog.LR2CustomFolderOutputDir = candidateNormal;
            dialog.LR2CustomFolderAsRootOutputDir = candidateRoot;
            CollectionAssert.AreEqual(expected, dialog.LR2ConfigBMSDirectories);
            CollectionAssert.AreEqual(expected, dialog.ConfirmedBMSDirectories.ToArray());
            Assert.AreEqual(normal, testSettings.LR2CustomFolderOutputBaseDir);
            CollectionAssert.AreEquivalent(retainedRegistrations, new LR2Config(config.ConfigFilePath).GetBMSSearchDirectoriesForChangeTracking().ToArray());
            testSettings.BMSInstallDir = missing;
            Assert.AreEqual(Resources.Error_InvalidBmsInstallDir, dialog.BmsInstallDirValidationMessage);
            testSettings.BMSInstallDir = manual;
            Assert.AreEqual(string.Empty, dialog.BmsInstallDirValidationMessage);
        }
        finally { TryDeleteDirectory(root); }
    }

    /// <summary>保存済み基点がドライブ・UNCルートでも一般一覧と導入候補から全子孫を除き、欠落登録・別領域・必要なjukebox登録を保ちます。</summary>
    /// <remarks>実XMLと設定ownerを接続しますが合成パスの存在確認や出力は行いません。実在管理先の保存拒否は実ownerの導入・削除代表が担当します。</remarks>
    [TestMethod]
    [DataRow("root", @"V:\", @"W:\Songs")]
    [DataRow("root", @"\\test-server\share\", @"\\test-server\share2\Songs")]
    [DataRow("root", @"\\test-server\share", @"\\other-server\share\Songs")]
    [DataRow("normal", @"V:\", @"W:\Songs")]
    [DataRow("normal", @"\\test-server\share\", @"\\test-server\share2\Songs")]
    [DataRow("additional", @"V:\", @"W:\Songs")]
    [DataRow("additional", @"\\test-server\share\", @"\\test-server\share2\Songs")]
    public void Lr2BmsDirectoryChoices_RootShapedOutputsKeepOnlyUnmanagedCandidates(string role, string managed, string unrelated)
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manual = Path.Combine(root, "Manual");
            string missing = Path.Combine(root, "Missing");
            string normal = role == "normal" ? managed : Path.Combine(root, "Normal");
            string additional = role == "additional" ? managed : Path.Combine(root, "Additional");
            string rootOutput = role == "root" ? managed : Path.Combine(root, "Root");
            string[] registered = [manual, missing, normal, additional, rootOutput, Path.Combine(managed, "Table"), Path.Combine(managed, "Table", "Level"), unrelated];
            LR2Config config = CreateConfig(root);
            config.SetBMSSearchDirectories(registered);
            string[] retainedRegistrations = config.GetBMSSearchDirectoriesForChangeTracking().ToArray();
            testSettings.LR2CustomFolderOutputBaseDir = normal;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutput;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additional]);
            testSettings.BMSInstallDir = manual;
            MainWindowViewModel owner = CreateViewModel(config);
            using SettingsDialogViewModel dialog = owner.SettingDialog;
            string[] expected = [manual, missing, unrelated];
            CollectionAssert.AreEquivalent(expected, dialog.LR2ConfigBMSDirectories);
            CollectionAssert.AreEquivalent(expected, dialog.AvailableBMSDirectories);
            CollectionAssert.AreEquivalent(expected, dialog.ConfirmedBMSDirectories.ToArray());
            Lr2ConfigOwner configOwner = MainWindowViewModelTestFactory.GetComposition(owner).ResolveLr2Config(config.ConfigFilePath);
            CollectionAssert.AreEquivalent(retainedRegistrations, configOwner.Capture().RegisteredRoots.ToArray());
            CollectionAssert.AreEquivalent(retainedRegistrations, new LR2Config(config.ConfigFilePath).GetBMSSearchDirectoriesForChangeTracking().ToArray());
        }
        finally { TryDeleteDirectory(root); }
    }

    /// <summary>他基点・有効導入先と交差しないドライブ・UNCルートを新しいROOT候補として受理し、未確定の選択では公開候補を変更しません。</summary>
    /// <remarks>登録はローカルの実パスだけにして、合成UNCへの存在確認・実I/Oを伴わずsetterの入力検査を確認します。</remarks>
    [TestMethod]
    [DataRow(@"V:\")]
    [DataRow(@"\\test-server\share\")]
    public void RootOutputCandidate_AcceptsDriveAndShareRootsWithoutPublishing(string candidate)
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manual = Directory.CreateDirectory(Path.Combine(root, "Manual")).FullName;
            string original = Path.Combine(root, "Root");
            LR2Config config = CreateConfig(root);
            config.SetBMSSearchDirectories([manual]);
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(root, "Normal");
            testSettings.LR2CustomFolderOutputBaseDirRootType = original;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = manual;
            MainWindowViewModel owner = CreateViewModel(config);
            using SettingsDialogViewModel dialog = owner.SettingDialog;
            dialog.LR2CustomFolderAsRootOutputDir = candidate;
            Assert.AreEqual(candidate, dialog.LR2CustomFolderAsRootOutputDir);
            Assert.AreEqual(original, testSettings.LR2CustomFolderOutputBaseDirRootType);
            CollectionAssert.AreEqual(new[] { manual }, dialog.AvailableBMSDirectories);
            CollectionAssert.AreEqual(new[] { manual }, dialog.ConfirmedBMSDirectories.ToArray());
        }
        finally { TryDeleteDirectory(root); }
    }

    /// <summary>実 LR2 登録に親子を保存し、保存前検証が両対象を示す既存リソースで拒否することを確認します。</summary>
    [TestMethod]
    public void CheckValidationBeforeSave_RejectsNestedLr2JukeboxRoots()
    {
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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

            bool isValid = dialog.CheckBmsRootsBeforeConfirmation(out string errMsg);

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
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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

            bool isValid = dialog.CheckOutputBeforeConfirmation(out string errMsg);

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
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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

            bool changed = SavePreparedCustomFolderSearchRoots(viewModel,
                    string.Empty,
                    CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings));

            Assert.IsTrue(changed);
            config = new LR2Config(config.ConfigFilePath);
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
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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

            bool changed = SavePreparedCustomFolderSearchRoots(viewModel,
                    string.Empty,
                    CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings));

            Assert.IsTrue(changed);
            Assert.IsTrue(Directory.Exists(playlistOutputRoot));
            config = new LR2Config(config.ConfigFilePath);
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
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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
            Assert.ThrowsExactly<ArgumentException>(() =>
                SavePreparedCustomFolderSearchRoots(viewModel,
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

    /// <summary>通常・追加・ROOTの同一先と包含親を採用前に拒否します。大小文字・末尾区切りを同一視し、BMS2は別のパスとして許可して単純prefix判定を検出します。未登録・欠落した元から不正な導入先の段階設定は妨げません。
    /// 拒否は利用者へのエラー通知を一件だけ出し（導入先の保護の拒否理由とそのパスを含む）、受理では通知を出さず、確認も出ないことを、記録用の窓口で表明します。拒否理由が利用者へ届く保証と、通知の欠落・重複・取り違えを検出します。</summary>
    [TestMethod]
    [DataRow("normal", "same", false)]
    [DataRow("additional", "same", false)]
    [DataRow("root", "same", false)]
    [DataRow("normal", "parent", false)]
    [DataRow("additional", "parent", false)]
    [DataRow("root", "parent", false)]
    [DataRow("normal", "case-separator", false)]
    [DataRow("normal", "sibling", true)]
    [DataRow("normal", "unregistered", true)]
    [DataRow("root", "missing", true)]
    public void OutputBase_ProtectsConfirmedInstallDirectory(string role, string relation, bool accepted)
    {
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string bms = Path.Combine(tempRootPath, "BMS");
            string install = relation is "parent" or "missing" ? Path.Combine(bms, "Songs") : bms;
            string candidate = relation switch
            {
                "parent" or "missing" => bms,
                "case-separator" => install.ToUpperInvariant() + Path.DirectorySeparatorChar,
                "sibling" => Path.Combine(tempRootPath, "BMS2"),
                _ => install
            };
            string normal = Path.Combine(tempRootPath, "NormalOutput");
            string root = Path.Combine(tempRootPath, "RootOutput");
            foreach (string directory in new[] { install, normal, root, candidate })
            { if (relation != "missing" || directory != install) { Directory.CreateDirectory(directory); } }
            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories(relation == "unregistered" ? [] : [install]);
            testSettings.LR2CustomFolderOutputBaseDir = normal;
            testSettings.LR2CustomFolderOutputBaseDirRootType = root;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.BMSInstallDir = install;
            var dialogs = new SettingsDialogHarness.RecordingDialogService { MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            MainWindowViewModel owner = CreateViewModel(config, dialogs);
            using SettingsDialogViewModel dialog = owner.SettingDialog;
            switch (role)
            {
                case "normal": dialog.LR2CustomFolderOutputDir = candidate; break;
                case "root": dialog.LR2CustomFolderAsRootOutputDir = candidate; break;
                case "additional": dialog.AddCustomFolderAdditionalOutputBaseDir(candidate); break;
            }
            Assert.AreEqual(accepted && role == "normal" ? candidate : normal, dialog.LR2CustomFolderOutputDir);
            Assert.AreEqual(accepted && role == "root" ? candidate : root, dialog.LR2CustomFolderAsRootOutputDir);
            CollectionAssert.AreEqual(accepted && role == "additional" ? new[] { candidate } : Array.Empty<string>(), dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
            Assert.AreEqual(install, testSettings.BMSInstallDir);
            Assert.AreEqual(normal, testSettings.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(root, testSettings.LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            Assert.AreEqual(accepted ? 0 : 1, dialogs.MessageCount);
            if (!accepted)
            {
                StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, install));
            }
        }
        finally { TryDeleteDirectory(tempRootPath); }
    }

    /// <summary>通常出力先の保存指定あり・復元指定なしの双方で、同一登録を出力先へ採用でき、譜面候補だけから除くことを確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NormalOutputBase_AllowsRegisteredRootWithOrWithoutSavedOutputSetting(bool restoreOutput)
    {
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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
            if (restoreOutput) { CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories, normalOutputBase); }
            else { CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, normalOutputBase); }
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), normalOutputBase);

            bool isValid = dialog.CheckOutputBeforeConfirmation(out string errMsg);

            Assert.IsTrue(isValid, errMsg);
        }
        finally
        {
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>追加指定を編集で消しても旧追加領域を通常出力へ転用する保存を拒否し、旧役割の保護を失う誤りを検出します。拒否理由（通常出力先と追加出力先の重なり）が利用者へ一件だけ通知され、確認が出ないことも、記録用の窓口で表明します。</summary>
    [TestMethod]
    public void NormalOutputBase_CannotAdoptPreviousAdditionalOutputBaseAsNewNormalRoot()
    {
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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
            var dialogs = new SettingsDialogHarness.RecordingDialogService { MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            MainWindowViewModel viewModel = CreateViewModel(config, dialogs);
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();
            dialog.LR2CustomFolderOutputDir = previousAdditionalOutputBase;
            Assert.AreEqual(normalOutputBase, dialog.LR2CustomFolderOutputDir);
            Assert.AreEqual(normalOutputBase, testSettings.LR2CustomFolderOutputBaseDir);
            AssertSingleOverlapNotification(dialogs, Resources.Label_AdditionalOutputBaseFolder);
        }
        finally
        {
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>ルート出力先を別の場所へ移しても旧領域の通常出力への転用を拒否し、旧役割の保護を失う誤りを検出します。拒否理由（通常出力先とルート出力先の重なり）が利用者へ一件だけ通知され、確認が出ないことも、記録用の窓口で表明します。</summary>
    [TestMethod]
    public void NormalOutputBase_CannotAdoptPreviousRootOutputBaseAsNewNormalRoot()
    {
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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
            var dialogs = new SettingsDialogHarness.RecordingDialogService { MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            MainWindowViewModel viewModel = CreateViewModel(config, dialogs);
            using SettingsDialogViewModel dialogLifetime = viewModel.SettingDialog;
            SettingsDialogViewModel dialog = viewModel.SettingDialog;

            dialog.LR2CustomFolderAsRootOutputDir = currentRootOutputBase;
            dialog.LR2CustomFolderOutputDir = previousRootOutputBasePath;
            Assert.AreEqual(normalOutputBase, dialog.LR2CustomFolderOutputDir);
            Assert.AreEqual(previousRootOutputBasePath, testSettings.LR2CustomFolderOutputBaseDirRootType);
            AssertSingleOverlapNotification(dialogs, Resources.Label_RootOutputBase);
        }
        finally
        {
            TryDeleteDirectory(tempRootPath);
        }
    }

    /// <summary>通常出力先と指定の旧役割の出力先の重なりを拒否した通知が一件だけ届き、確認が出ないことを確認します。組立ての細部（接頭辞）は固定しません。</summary>
    private static void AssertSingleOverlapNotification(SettingsDialogHarness.RecordingDialogService dialogs, string otherLabel)
    {
        Assert.AreEqual(0, dialogs.ConfirmationCount);
        Assert.AreEqual(1, dialogs.MessageCount);
        StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBasesOverlapFormat, Resources.Label_NormalOutputBase, otherLabel));
    }

    /// <summary>手動譜面ルートの親をルート型出力へ選択でき、採用確認の比較元として親出力と子登録を返すことを確認します。</summary>
    [TestMethod]
    public void RootOutputBase_CanAdoptManualBmsRootParentWithSaveWarning()
    {
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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

            Assert.AreEqual(manualBmsRootParent, dialog.LR2CustomFolderAsRootOutputDir);
            Assert.AreEqual(rootOutputBase, testSettings.LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual(1, conflicts.Count);
            Assert.AreEqual(manualBmsRootParent, conflicts[0].OutputBasePath);
            Assert.AreEqual(manualBmsRoot, conflicts[0].JukeboxRootPath);
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
            string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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
                bool changed = SavePreparedCustomFolderSearchRoots(viewModel, startupSettings.LR2CustomFolderOutputBaseDirRootType, startupSettings);

                Assert.IsTrue(changed);
                config = new LR2Config(config.ConfigFilePath);
                Assert.AreEqual(rootOutputChild, testSettings.BMSInstallDir);
                Assert.AreEqual(savedDialogInstallDir, dialog.BMSInstallDir);
                CollectionAssert.Contains(config.GetBMSSearchDirectories(), manualBmsRoot);
                CollectionAssert.Contains(config.GetBMSSearchDirectories(), normalOutputBase);
                CollectionAssert.Contains(config.GetBMSSearchDirectories(), rootOutputChild);
                CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);

                testSettings.BMSInstallDir = manualBmsRoot;
                dialog.CancelBmsRoots();

                Assert.AreEqual(manualBmsRoot, dialog.BMSInstallDir);
                Assert.AreEqual(manualBmsRoot, testSettings.BMSInstallDir);
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
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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

            bool changed = SavePreparedCustomFolderSearchRoots(viewModel, startupSettings.LR2CustomFolderOutputBaseDirRootType, startupSettings);

            Assert.IsTrue(changed);
            config = new LR2Config(config.ConfigFilePath);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), rootOutputChild);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), rootOutputBase);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), changedRootOutputBase);
        }
        finally
        {
            TryDeleteDirectory(tempRootPath);
        }
    }

    private MainWindowViewModel CreateViewModel(LR2Config config, IUiDialogService? settingsDialogs = null)
    {
        testSettings.OperationModeLR2DB = true;
        testSettings.LR2ConfigXmlPath = Path.Combine(config.LR2RootPath, "LR2files", "Config", "config.xml");
        config.Save(config.ConfigFilePath);
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(testSettings, settingsDialogs: settingsDialogs);
        // 初期化を走らせないfixtureでも、設定と出力窓口へ同じ構成寿命のXML所有者を接続します。
        // SettingsDialog の購読 lifetime は各呼出元が実観測の終端まで所有します。
        try
        {
            Lr2ConfigOwner owner = MainWindowViewModelTestFactory.GetComposition(viewModel).ResolveLr2Config(config.ConfigFilePath);
            SetViewModelField(viewModel, "lr2config", owner);
            return viewModel;
        }
        catch
        {
            viewModel.SettingDialog.Dispose();
            throw;
        }
    }

    private static void SetViewModelField(MainWindowViewModel viewModel, string fieldName, object value)
    {
        FieldInfo field = typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("指定された画面fixtureフィールドがありません: " + fieldName);
        field.SetValue(viewModel, value);
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
        MainWindowViewModelTestFactory.GetComposition(viewModel).Lr2ConfigSession.TryResolve(testSettings.LR2ConfigXmlPath, out Lr2ConfigOwner? config);
        TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope, songDbPath, testSettings, getLr2Config: config);
        playlist.BMSTables = new ObservableCollection<BMSTable>(tables);
        SetViewModelField(viewModel, "tables", playlist);
        viewModel.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
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
}
