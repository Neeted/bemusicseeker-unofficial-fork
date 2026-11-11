using System.Globalization;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlayHistoryPresetTestFixture;

namespace BeMusicSeeker.Tests;

/// <remarks>設定判断と実 LR2 XML・出力窓口の配送を確認します。描画接続は compiled category/Window 系列が分担します。</remarks>
[TestClass]
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

    /// <summary>設定受理の予約領域条件を集約する。出力基点三種は組込み自身・配下・直上を拒否し、LR2rootより上の祖先、BMS親・drive、兄弟と未知LR2rootを維持する。実drive/UNCへアクセスしない。</summary>
    [TestMethod]
    [DataRow("normal", "same", true)]
    [DataRow("normal", "child", true)]
    [DataRow("normal", "ancestor", true)]
    [DataRow("additional", "same", true)]
    [DataRow("additional", "child", true)]
    [DataRow("additional", "ancestor", true)]
    [DataRow("root", "same", true)]
    [DataRow("root", "child", true)]
    [DataRow("root", "ancestor", true)]
    [DataRow("normal", "drive", false)]
    [DataRow("normal", "lr2-root", false)]
    [DataRow("additional", "lr2-root", false)]
    [DataRow("root", "lr2-root", false)]
    [DataRow("normal", "sibling", false)]
    [DataRow("additional", "sibling", false)]
    [DataRow("root", "sibling", false)]
    [DataRow("normal", "boundary", false)]
    [DataRow("normal", "unknown-root", false)]
    [DataRow("bms", "same", true)]
    [DataRow("bms-under-ancestor-output", "same", true)]
    [DataRow("bms", "child", true)]
    [DataRow("bms", "ancestor", false)]
    [DataRow("bms", "drive", false)]
    [DataRow("bms", "sibling", false)]
    [DataRow("standalone-bms", "same", true)]
    [DataRow("unused-output", "same", false)]
    public async Task SettingsAcceptance_ReservesBuiltinOnlyAtConfigurationBoundary(string role, string position, bool reject)
    {
        if (role == "bms-under-ancestor-output")
        {
            string directory = Path.Combine(CaseWorkspace.RootDirectory, "bms-settings-acceptance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string reserved = Path.Combine(directory, "LR2files", "CustomFolder");
                string configPath = Path.Combine(directory, "config.xml");
                File.WriteAllText(configPath, "<config><system/><player/><jukebox/></config>");
                var config = new LR2Config(configPath);
                config.SetBMSSearchDirectories([reserved]); config.Save(configPath);
                var startup = new StartupSettingsSnapshot
                {
                    OperationModeLR2DB = true,
                    LR2RootPath = directory,
                    LR2ConfigXmlPath = configPath,
                    LR2CustomFolderOutputBaseDir = directory,
                    LR2CustomFolderAdditionalOutputBaseDirs = "[]"
                };
                LibraryDirectoryPreflightException failure = await Assert.ThrowsExactlyAsync<LibraryDirectoryPreflightException>(
                    () => StartupLibraryConstructionOwner.PrepareDirectoriesAsync(startup, new CustomFolderOutputSettingsSnapshot(), new Lr2ConfigSession()));
                Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, failure.Use);
                Assert.AreEqual(LibraryDirectoryPreflightFailureCause.InvalidConfiguration, failure.Cause);
                Assert.IsFalse(Directory.Exists(reserved), "出力基点に含まれる明示登録も、利用可能性の検査より前に受理検査します。");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return;
        }
        const string lr2 = @"C:\LR2-acceptance";
        string builtin = Path.Combine(lr2, "LR2files", "CustomFolder");
        string path = position switch
        {
            "child" => Path.Combine(builtin, "Child"),
            "ancestor" => Path.Combine(lr2, "LR2files"),
            "drive" => @"C:\",
            "lr2-root" => lr2,
            "sibling" => Path.Combine(lr2, "LR2files", "Safe"),
            "boundary" => builtin + "Other",
            _ => builtin
        };
        bool bms = role is "bms" or "standalone-bms" or "bms-under-ancestor-output";
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = role is not "standalone-bms" and not "unused-output",
            LR2RootPath = position == "unknown-root" ? null : lr2,
            LR2CustomFolderOutputBaseDir = role == "bms-under-ancestor-output" ? lr2 : role is "normal" or "unused-output" ? path : null,
            LR2CustomFolderOutputBaseDirRootType = role == "root" ? path : null,
            LR2CustomFolderAdditionalOutputBaseDirsSerialized = CustomFolderOutputBaseRegistry.SerializeBaseDirectories(role == "additional" ? [path] : [])
        };
        string[] registeredRoots = bms ? [path] : [];
        LibraryDirectoryPreflightRequest request = new LibraryDirectoryPreflightService().CreateRequest(registeredRoots, registeredRoots, options);
        if (reject)
        {
            LibraryDirectoryPreflightException error = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(() => LibraryDirectoryPreflightService.ValidateSettingsAcceptance(registeredRoots, request, options));
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.InvalidConfiguration, error.Cause);
            Assert.IsNotNull(error.InnerException);
        }
        else { LibraryDirectoryPreflightService.ValidateSettingsAcceptance(registeredRoots, request, options); }
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

    /// <summary>親子と無関係な兄弟パスを混ぜ、親子だけを正しい方向で競合として返すことを確認します。</summary>
    [TestMethod]
    public void CollectNestedBmsSearchRootConflicts_DetectsParentChildOnly()
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        string parent = Path.Combine(root, "BMS");
        string child = Path.Combine(parent, "Child");
        string sibling = Path.Combine(root, "Sibling");

        IReadOnlyList<SettingsDialogViewModel.NestedBmsSearchRootConflict> conflicts =
            SettingsDialogViewModel.CollectNestedBmsSearchRootConflicts([child, sibling, parent]);

        Assert.AreEqual(1, conflicts.Count);
        Assert.AreEqual(parent, conflicts[0].ParentPath);
        Assert.AreEqual(child, conflicts[0].ChildPath);
    }

    /// <summary>旧通常出力先を追加出力へ転用する要求を、同一登録を示す採用確認対象として返すことを確認します。</summary>
    [TestMethod]
    public void AdditionalOutputBase_PreviousNormalOutputBaseRequiresAdoptionWarning()
    {
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>改名確定時に現在ディレクトリ表現と空白だけの名前を拒否し、生入力を保持する画面でも無効な出力先を作らない既存判断を確認します。</summary>
    [TestMethod]
    [DataRow(".")]
    [DataRow("   ")]
    public void AdditionalOutputBaseRename_RejectsCurrentDirectoryName(string name)
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        string additionalOutputBase = Path.Combine(root, "AdditionalOutput");
        Assert.ThrowsExactly<ArgumentException>(() =>
            CustomFolderOutputBaseRegistry.RenameLastDirectoryName(additionalOutputBase, name));
    }

    /// <summary>通常出力先を移す同期で旧登録を外し新登録を実 LR2 設定へ追加することを確認します。</summary>
    [TestMethod]
    public void NormalOutputBaseSearchRootSync_RemovesOldNormalOutputBase()
    {
        string tempRootPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
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
}
