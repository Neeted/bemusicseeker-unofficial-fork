using System.Collections.ObjectModel;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.Tests.SettingsRootTestFixture;
using static BeMusicSeeker.TestSupport.Components.SettingsMediaTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>出力設定の保存・生成と失敗後の再操作を検証します。</summary>
/// <remarks>共有 culture・theme・Bass 受付と composition 購読を使うため非並列にします。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingDialogOutputConfirmationTests
{
    private TestWorkspace? ownedWorkspace;

    private static PlaylistSummaryRow BuildOutputSummary(PlaylistWorkspaceViewModel workspace)
    {
        workspace.RequestPlaylistSummaryMode(true);
        workspace.RebuildPlaylistSummaryView(runAsync: false);
        TestUiDispatcherHost.AwaitTaskOnDispatcher(workspace.WaitForPlaylistSummaryDataBuildIdleAsync(), "output-current-summary");
        return workspace.PlaylistSummaryView.Single();
    }

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>このクラスの準備とモデルで共有するDBの所有者です。</summary>
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>追加出力先の複数追加・改名・削除の設定VMケースが共有する、既存の追加出力先・実XML・保存先観測を準備して本体を実行します。</summary>
    /// <remarks>ライブラリプロファイルは非アクティブで、確認の応答は <paramref name="confirmation"/> です。追加出力先の保存要求は専用providerで観測し、終了時に元のproviderと所有領域を回収します。</remarks>
    private void RunAdditionalOutputCase(string[] existingNames, System.Windows.MessageBoxResult confirmation, Action<AdditionalOutputCase> body)
    {
        string directory = CreateTemporaryRoot(CaseWorkspace);
        string settingsPath = Path.Combine(directory, "settings", "user.config");
        string[] existing = [.. existingNames.Select(name => Path.Combine(directory, name))];
        Settings values = CreateValidCustomFolderSettings(directory, existing, TestSettingsFactory.OpenSettings(settingsPath));
        values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories(existing);
        values.Save();
        System.Configuration.SettingsProperty property = values.Properties[nameof(Settings.LR2CustomFolderAdditionalOutputBaseDirs)];
        System.Configuration.SettingsProvider originalProvider = property.Provider;
        var provider = new InstallAdmissionObservingSettingsProvider(originalProvider, () => { });
        property.Provider = provider;
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(confirmation) };
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: UndeclaredDialogs.Create());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                SetActiveLibraryProfile(owner, false);
                try { body(new AdditionalOutputCase(directory, settingsPath, values, dialogs, failures, owner.SettingDialog, provider, existing)); }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "additional-output-case-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally
        {
            property.Provider = originalProvider;
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>追加出力先のケース本体へ渡す、実VM・実保存先・観測の組です。</summary>
    private sealed record AdditionalOutputCase(string Directory, string SettingsPath, Settings Values, RecordingRootDialogService Dialogs,
        List<Exception> Failures, SettingsDialogViewModel Dialog, InstallAdmissionObservingSettingsProvider Provider, string[] Existing)
    {
        /// <summary>設定画面が候補を作る正規化と同じ形で、ケース内のフォルダのパスを返します。</summary>
        internal string Path(params string[] parts) => CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(System.IO.Path.Combine(new[] { Directory }.Concat(parts).ToArray()));

        /// <summary>確定済みの追加出力先の公開値です。</summary>
        internal string[] Published() => [.. CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(Values.LR2CustomFolderAdditionalOutputBaseDirs)];

        /// <summary>保存先を読み直した追加出力先です。</summary>
        internal string[] Saved() => [.. CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(TestSettingsFactory.OpenSettings(SettingsPath).LR2CustomFolderAdditionalOutputBaseDirs)];

        /// <summary>設定画面の追加出力先の候補一覧です。</summary>
        internal string[] Candidates() => [.. Dialog.CustomFolderAdditionalOutputBaseDirList];

        /// <summary>LR2設定XMLに登録された検索ディレクトリです。</summary>
        internal string[] XmlRoots() => [.. BeMusicSeeker.TestSupport.Components.TestLr2ConfigFactory.Load(Values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking()];
    }

    /// <summary>
    /// 条件：既存の追加出力先 [E1, E2] へ、一回の「追加」で選んだ複数フォルダ（有効・同じ末尾名・通常出力先と同名・既存と同名などの組合せ）を渡します。
    /// 期待結果：拒否したフォルダだけを除き、受理したフォルダは選択順にすべて追加して、一回の確定で保存・公開・XML登録します（途中の部分集合を保存しません）。拒否が一件以上あればパスと理由を一回の通知にまとめ、0件なら通知しません。全件拒否・単独拒否は保存・登録・確認なしで一覧と選択を変えず、確認の取消は保存せず確定配置へ戻して選択を残します。確定後の選択は最後に受理したフォルダです。
    /// 検出する誤り：後続の拒否で先に受理したフォルダまで消える、同じ選択内で先に受理したフォルダを比較に含めない、拒否ごとの通知・通知漏れ、受理分を一件ずつ保存する、拒否で既存候補を戻す・外す、確定後の選択が先頭へ移る。
    /// </summary>
    [TestMethod]
    [DataRow("valid-dup-valid")]
    [DataRow("valid-valid-rejected")]
    [DataRow("all-valid")]
    [DataRow("all-rejected")]
    [DataRow("single-rejected")]
    [DataRow("rejected-then-confirmation-cancel")]
    public void AdditionalOutputMultiAdd_SavesAcceptedInOneCommitAndReportsRejectionsOnce(string scenario)
    {
        bool cancelConfirmation = scenario == "rejected-then-confirmation-cancel";
        RunAdditionalOutputCase(["E1", "E2"], cancelConfirmation ? System.Windows.MessageBoxResult.Cancel : System.Windows.MessageBoxResult.OK, context =>
        {
            SettingsDialogViewModel dialog = context.Dialog;
            string e1 = context.Path("E1"), e2 = context.Path("E2");
            string v1 = Directory.CreateDirectory(context.Path("n1", "V1")).FullName, duplicate = Directory.CreateDirectory(context.Path("n2", "V1")).FullName;
            string v2 = Directory.CreateDirectory(context.Path("n3", "V2")).FullName;
            string sameAsNormal = Directory.CreateDirectory(context.Path("n4", "NormalOutput")).FullName;
            string sameAsExisting = Directory.CreateDirectory(context.Path("n5", "E1")).FullName;
            (string[] Paths, string[] Accepted, (string Path, string Reason)[] Rejected) plan = scenario switch
            {
                "valid-dup-valid" => ([v1, duplicate, v2], [v1, v2], [(duplicate, Resources.Error_AdditionalOutputBaseNameDuplicate)]),
                "valid-valid-rejected" => ([v1, v2, sameAsNormal], [v1, v2], [(sameAsNormal, Resources.Error_AdditionalOutputBaseNameMatchesNormalOutput)]),
                "all-valid" => ([v1, v2], [v1, v2], []),
                "all-rejected" => ([sameAsNormal, sameAsExisting], [], [(sameAsNormal, Resources.Error_AdditionalOutputBaseNameMatchesNormalOutput), (sameAsExisting, Resources.Error_AdditionalOutputBaseNameDuplicate)]),
                "single-rejected" => ([sameAsNormal], [], [(sameAsNormal, Resources.Error_AdditionalOutputBaseNameMatchesNormalOutput)]),
                _ => ([v1, sameAsNormal], [v1], [(sameAsNormal, Resources.Error_AdditionalOutputBaseNameMatchesNormalOutput)])
            };
            if (scenario == "single-rejected") { dialog.SelectedCustomFolderAdditionalOutputBaseDir = e2; }
            if (cancelConfirmation)
            {
                string staged = Directory.CreateDirectory(context.Path("StagedSongs")).FullName;
                dialog.AddBmsSearchRootPathsAsync([staged]).GetAwaiter().GetResult();
            }
            string selectedBefore = dialog.SelectedCustomFolderAdditionalOutputBaseDir;
            byte[] settingsBefore = File.ReadAllBytes(context.SettingsPath);
            byte[] xmlBefore = File.ReadAllBytes(context.Values.LR2ConfigXmlPath);

            if (dialog.AddCustomFolderAdditionalOutputBaseDirs(plan.Paths))
            { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "additional-output-multi-add"); }

            Assert.AreEqual(0, context.Failures.Count, string.Join(Environment.NewLine, context.Failures));
            if (plan.Rejected.Length == 0) { Assert.AreEqual(0, context.Dialogs.MessageCount, "拒否が0件なら拒否通知を出しません。"); }
            else
            {
                string message = context.Dialogs.MessageRequests.Single().MessageBoxText;
                StringAssert.Contains(message, Resources.Error_AdditionalOutputBaseRejectedHeading);
                int searchFrom = 0;
                foreach ((string path, string reason) in plan.Rejected)
                {
                    int pathIndex = message.IndexOf(path, searchFrom, StringComparison.OrdinalIgnoreCase);
                    Assert.IsTrue(pathIndex >= 0, "拒否したフォルダのパスを通知へ含めます: " + path);
                    int reasonIndex = message.IndexOf(reason, pathIndex, StringComparison.Ordinal);
                    Assert.IsTrue(reasonIndex >= 0, "各パスの後にその理由を含めます: " + reason);
                    searchFrom = reasonIndex;
                }
            }
            if (plan.Accepted.Length == 0 || cancelConfirmation)
            {
                Assert.AreEqual(0, context.Provider.SaveCount, "受理分がないか確認を取り消したときは保存要求を出しません。");
                CollectionAssert.AreEqual(settingsBefore, File.ReadAllBytes(context.SettingsPath));
                CollectionAssert.AreEqual(xmlBefore, File.ReadAllBytes(context.Values.LR2ConfigXmlPath));
                CollectionAssert.AreEqual(context.Existing.Select(path => CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path)).ToArray(), context.Candidates());
                Assert.AreEqual(selectedBefore, dialog.SelectedCustomFolderAdditionalOutputBaseDir, "拒否・取消では選択を操作前のまま残します。");
                Assert.AreEqual(cancelConfirmation ? 1 : 0, context.Dialogs.ConfirmationCount);
                return;
            }
            string[] expected = [e1, e2, .. plan.Accepted];
            string serialized = CustomFolderOutputBaseRegistry.SerializeBaseDirectories(expected);
            CollectionAssert.AreEqual(expected, context.Published());
            CollectionAssert.AreEqual(expected, context.Saved());
            CollectionAssert.AreEqual(expected, context.Candidates());
            foreach (string accepted in plan.Accepted) { CollectionAssert.Contains(context.XmlRoots(), accepted); }
            Assert.IsTrue(context.Provider.AdditionalOutputBaseDirsWrites.Count >= 1);
            Assert.IsTrue(context.Provider.AdditionalOutputBaseDirsWrites.All(write => write == serialized), "途中の部分集合を保存せず、最終の集合だけを一回の確定で保存します。");
            Assert.AreEqual(plan.Accepted[^1], dialog.SelectedCustomFolderAdditionalOutputBaseDir, "確定後の選択は最後に受理したフォルダです。");
            Assert.AreEqual(CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(plan.Accepted[^1]), dialog.SelectedCustomFolderAdditionalOutputBaseName);
        });
    }

    /// <summary>
    /// 条件：追加出力先の改名・削除を確定します。確定が成立する場合と、拒否・確定の確認の取消・保存失敗で確定配置へ戻る場合を扱います。
    /// 期待結果：成立後は確定前の選択（改名なら改名後）が新しい一覧に残っていればその項目を指し続け（改名欄も同じ項目の末尾名）、残っていない削除だけが先頭（空ならなし）へ移ります。確定配置へ戻す場合は、操作対象の確定側の項目（改名前・削除対象）が戻した一覧に残っていればそれを選択します。
    /// 検出する誤り：確定後の再同期が常に先頭を選ぶ、戻した一覧で操作対象でない先頭を選ぶ、改名欄が選択と食い違う。
    /// </summary>
    [TestMethod]
    [DataRow("rename")]
    [DataRow("delete")]
    [DataRow("delete-last")]
    [DataRow("rename-rejected")]
    [DataRow("rename-confirmation-cancel")]
    [DataRow("rename-save-failure")]
    [DataRow("delete-confirmation-cancel")]
    public void AdditionalOutputConfirmation_KeepsSelectionWhileItRemains(string scenario)
    {
        bool cancelConfirmation = scenario.EndsWith("confirmation-cancel", StringComparison.Ordinal);
        string[] names = scenario switch { "delete" or "delete-confirmation-cancel" => ["A", "B", "C"], "delete-last" => ["A"], _ => ["A", "B"] };
        RunAdditionalOutputCase(names, cancelConfirmation ? System.Windows.MessageBoxResult.Cancel : System.Windows.MessageBoxResult.OK, context =>
        {
            SettingsDialogViewModel dialog = context.Dialog;
            string a = context.Path("A"), b = context.Path("B"), c = context.Path("C");
            string[] published = [.. context.Existing.Select(path => CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path))];
            bool rename = scenario.StartsWith("rename", StringComparison.Ordinal);
            string target = scenario == "delete-last" ? a : b;
            dialog.SelectedCustomFolderAdditionalOutputBaseDir = target;
            if (rename) { dialog.SelectedCustomFolderAdditionalOutputBaseName = scenario == "rename-rejected" ? "A" : "C"; }
            if (cancelConfirmation)
            {
                string staged = Directory.CreateDirectory(context.Path("StagedSongs")).FullName;
                dialog.AddBmsSearchRootPathsAsync([staged]).GetAwaiter().GetResult();
            }
            if (rename) { dialog.RenameSelectedCustomFolderAdditionalOutputBaseDir(); }
            else { dialog.RemoveSelectedCustomFolderAdditionalOutputBaseDir(); }
            if (!context.Candidates().SequenceEqual(published, StringComparer.OrdinalIgnoreCase))
            {
                if (scenario == "rename-save-failure")
                {
                    using (File.Open(context.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "additional-output-selection-save-failure"); }
                }
                else { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "additional-output-selection"); }
            }

            (string[] List, string? Selected) expected = scenario switch
            {
                "rename" => ([a, c], c),
                "delete" => ([a, c], a),
                "delete-last" => ([], null),
                "delete-confirmation-cancel" => (published, b),
                _ => (published, b)
            };
            CollectionAssert.AreEqual(expected.List, context.Candidates());
            Assert.AreEqual(expected.Selected, dialog.SelectedCustomFolderAdditionalOutputBaseDir);
            Assert.AreEqual(CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(expected.Selected), dialog.SelectedCustomFolderAdditionalOutputBaseName, "改名欄は選択中の項目の末尾名です。");
            Assert.AreEqual(scenario == "rename-save-failure" ? 1 : 0, context.Failures.Count);
            Assert.AreEqual(scenario == "rename-rejected" ? 1 : 0, context.Dialogs.MessageCount);
            Assert.AreEqual(cancelConfirmation ? 1 : 0, context.Dialogs.ConfirmationCount);
            if (expected.List.SequenceEqual(published)) { CollectionAssert.AreEqual(published, context.Published()); }
            else { CollectionAssert.AreEqual(expected.List, context.Saved()); }
        });
    }

    /// <summary>有効な導入先の出力採用と確認中のP先着を実保存・XML・DB・配置変更前に拒否し、P解放だけでは再実行しません。別の確定ルートを導入先にした後の次の明示操作で実出力を完了し、初期化検査にも通ります。</summary>
    [TestMethod]
    public void OutputConfirmation_ProtectsInstallDirectoryUntilAnotherConfirmedRootIsSelected()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string settingsPath = Path.Combine(root, "settings", "user.config");
            string alternative = Path.Combine(root, "OtherSongs");
            Settings values = CreateValidCustomFolderSettings(root, [alternative], TestSettingsFactory.OpenSettings(settingsPath));
            values.Save();
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: UndeclaredDialogs.Create());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                var library = new TestBmsLibrary(composition.DatabaseScope, values.LR2SongDBPath, configOwner, null, null, new UnexpectedBmsLibraryDialogService(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), optionsSnapshotProvider: null, operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: values);
                SetPrivateField(owner, "files", library);
                owner.InstallTree.AttachLibrary(library);
                string pendingPath = Path.Combine(values.BMSInstallDir, "pending.bms");
                File.WriteAllText(pendingPath, "#PLAYER 1\n#TITLE Pending adopted root\n#BPM 120\n");
                string unrelatedPath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "UnrelatedPending")).FullName, "pending.bms");
                File.WriteAllText(unrelatedPath, "#TITLE Unrelated\n#BPM 120\n");
                var adopted = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingPath)))]); adopted.path = values.BMSInstallDir;
                var unrelated = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(unrelatedPath)))]); unrelated.path = Path.GetDirectoryName(unrelatedPath) ?? throw new InvalidOperationException();
                library.ChartPackagesPending = new ObservableCollection<ChartPackage>([adopted, unrelated]);
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(composition.DatabaseScope, values.LR2SongDBPath, db =>
                { db.CreateTable<LR2SongDBExtended.install>(); db.InsertOrReplace(adopted, typeof(LR2SongDBExtended.install)); db.InsertOrReplace(unrelated, typeof(LR2SongDBExtended.install)); });

                var table = new BMSTable
                {
                    playlist_id = 7198,
                    name = "Table",
                    symbol = "T",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                    entries = [PlaylistTestFixture.CreateEntry(new string('d', 32), "Folder")],
                    Folder_order = ["Folder"]
                };
                var playlist = new TestBmsPlaylist(composition.DatabaseScope, values.LR2SongDBPath, configOwner, null, null,
                    () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
                    composition.CustomFolderOutputSettingsProvider,
                    PlaylistTestFixture.CreateDeterministicLr2PlaylistFolderSynchronizationPort(composition.DatabaseScope, values.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                    mutationAdmission: composition.PlaylistOperationAdmission, settings: values)
                { BMSTables = new ObservableCollection<BMSTable>([table]) };
                SetPrivateField(owner, "tables", playlist);
                owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                var confirmationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? rejectedOutput = null;
                IDisposable? confirmationLease = null;
                try
                {
                    playlist.CommitBMSTableWithEntriesToDB(table);
                    string oldOutput = values.LR2CustomFolderOutputBaseDir;
                    string install = values.BMSInstallDir;
                    string oldFile = Path.Combine(oldOutput, "Table", "0000.lr2folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
                    File.WriteAllText(oldFile, "preserved output");
                    byte[] oldSettings = File.ReadAllBytes(settingsPath);
                    byte[] oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    owner.SettingDialog.LR2CustomFolderOutputDir = install;
                    StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, install));
                    Assert.AreEqual(oldOutput, owner.SettingDialog.LR2CustomFolderOutputDir);
                    Assert.AreEqual(0, failures.Count);
                    Assert.AreEqual(0, dialogs.ConfirmationCount);
                    CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Assert.AreEqual("preserved output", File.ReadAllText(oldFile));
                    Assert.IsFalse(Directory.Exists(Path.Combine(install, "Table")));
                    using (LR2SongDBExtended reader = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly())
                    {
                        Assert.AreEqual(1, reader.Table<BMSTable>().Count());
                        Assert.AreEqual(0, reader.Table<LR2SongDB.folder>().Count(), "実library準備済みfolder表へ、拒否操作から行を追加しません。");
                    }
                    owner.SettingDialog.SelectBmsInstallDirectoryAsync(alternative).GetAwaiter().GetResult();
                    oldSettings = File.ReadAllBytes(settingsPath);
                    oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    LR2SongDBExtended.playlist previousHeader;
                    using (LR2SongDBExtended reader = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly())
                    { previousHeader = reader.Table<LR2SongDBExtended.playlist>().Single(); }
                    dialogs.ConfirmationObservedAsync = () => { confirmationEntered.TrySetResult(); return releaseConfirmation.Task; };
                    owner.SettingDialog.LR2CustomFolderOutputDir = install;
                    rejectedOutput = owner.SettingDialog.CommitOutputSettingsAsync("normal");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(confirmationEntered.Task, rejectedOutput), "output-adoption-confirmation-entered");
                    Assert.IsTrue(confirmationEntered.Task.IsCompletedSuccessfully);
                    Assert.IsFalse(rejectedOutput.IsCompleted);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable acquired));
                    confirmationLease = acquired;
                    releaseConfirmation.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedOutput, "output-adoption-P-rejected");
                    void AssertOutputUnchanged()
                    {
                        CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(settingsPath));
                        CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                        Assert.AreEqual(oldOutput, values.LR2CustomFolderOutputBaseDir);
                        Assert.AreEqual("preserved output", File.ReadAllText(oldFile));
                        Assert.IsFalse(Directory.Exists(Path.Combine(install, "Table")));
                        using LR2SongDBExtended reader = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly();
                        LR2SongDBExtended.playlist currentHeader = reader.Table<LR2SongDBExtended.playlist>().Single();
                        Assert.AreEqual(previousHeader.output_dir, currentHeader.output_dir);
                        Assert.AreEqual(previousHeader.custom_folder_output_base_name, currentHeader.custom_folder_output_base_name);
                        Assert.AreEqual(0, reader.Table<LR2SongDB.folder>().Count(), "実library準備済みfolder表へ、拒否操作から行を追加しません。");
                    }
                    AssertOutputUnchanged();
                    Assert.AreEqual(1, dialogs.ConfirmationCount);
                    Assert.AreEqual(2, dialogs.MessageCount, "導入先保護とP受付拒否をそれぞれ一回通知します。");
                    Assert.AreEqual(oldOutput, owner.SettingDialog.LR2CustomFolderOutputDir);
                    confirmationLease.Dispose();
                    confirmationLease = null;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertOutputUnchanged();
                    Assert.AreEqual(1, dialogs.ConfirmationCount, "P解放だけでは再確認・再実行を予約しません。");
                    owner.SettingDialog.LR2CustomFolderOutputDir = install;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "adopt-output-after-install-change");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(install, TestSettingsFactory.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(alternative, TestSettingsFactory.OpenSettings(settingsPath).BMSInstallDir);
                    Assert.IsTrue(File.Exists(Path.Combine(install, "Table", "0000.lr2folder")));
                    Assert.IsFalse(File.Exists(oldFile));
                    CollectionAssert.AreEqual(new[] { unrelated.path }, library.ChartPackagesPending.Select(package => package.path).ToArray());
                    CollectionAssert.AreEqual(new[] { unrelated.path }, owner.InstallTree.ChartPackagesPending.Select(package => package.path).ToArray());
                    Assert.IsTrue(File.Exists(pendingPath)); Assert.IsTrue(File.Exists(unrelatedPath));
                    using (LR2SongDBExtended pendingDb = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly())
                    { CollectionAssert.AreEqual(new[] { unrelated.path }, pendingDb.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray()); }
                    Assert.AreEqual(string.Empty, SettingsDeficiency.FormatDiagnostic(owner.SettingDialog.BuildDeficiencies()));
                }
                finally
                {
                    releaseConfirmation.TrySetResult();
                    confirmationLease?.Dispose();
                    if (rejectedOutput != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedOutput, "output-adoption-rejection-cleanup"); }
                    library.RequestShutdown("install-directory-output-fixture");
                    playlist.RequestShutdown("install-directory-output-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "install-directory-output-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>追加基点の削除で既定先へ集まる表が同一配置となる場合、設定・XML・DB・正本・ファイルを変更する前に拒否します。実設定portの接続と媒体保存順を確認します。</summary>
    [TestMethod]
    public void OutputConfirmation_RejectsOverlapAfterAdditionalBaseRemovalBeforeSaving()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(settingsPath));
            string extra = Path.Combine(root, "Extra");
            Directory.CreateDirectory(extra);
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([extra]);
            values.Save();
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: UndeclaredDialogs.Create());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                LR2Config config = TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                SetPrivateField(owner.SettingDialog, "statePort", new ActiveSettingsDialogStatePort());
                var parent = new BMSTable { playlist_id = 7196, name = "Parent" };
                var child = new BMSTable { playlist_id = 7197, name = "Child", Output_dir = "Parent", custom_folder_output_base_name = "Extra" };
                var playlist = new TestBmsPlaylist(composition.DatabaseScope, values.LR2SongDBPath, configOwner, null, null,
                    () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
                    composition.CustomFolderOutputSettingsProvider,
                    PlaylistTestFixture.CreateDeterministicLr2PlaylistFolderSynchronizationPort(composition.DatabaseScope, values.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                    mutationAdmission: composition.PlaylistOperationAdmission, settings: values)
                { BMSTables = new ObservableCollection<BMSTable>([parent, child]) };
                SetPrivateField(owner, "tables", playlist);
                owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                try
                {
                    playlist.CommitBMSTableHeadersToDB([parent, child]);
                    byte[] oldSettings = File.ReadAllBytes(settingsPath);
                    byte[] oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    string oldFile = Path.Combine(extra, "Parent", "0000.lr2folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(oldFile) ?? throw new AssertFailedException());
                    File.WriteAllText(oldFile, "preserved output");
                    int notices = 0;
                    child.PropertyChanged += (_, _) => notices++;
                    owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir = extra;
                    owner.SettingDialog.RemoveSelectedCustomFolderAdditionalOutputBaseDir();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("additional"), "reject-output-overlap");
                    Assert.AreEqual(1, failures.Count);
                    StringAssert.Contains(failures[0].Message, Resources.Error_ManagedPlaylistOutputOverlap);
                    CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Assert.AreEqual(0, notices);
                    Assert.AreEqual("Extra", child.custom_folder_output_base_name);
                    Assert.AreEqual("preserved output", File.ReadAllText(oldFile));
                    using LR2SongDBExtended reader = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly();
                    Assert.AreEqual("Extra", reader.Table<BMSTable>().Single(table => table.playlist_id == 7197).custom_folder_output_base_name);
                    Assert.IsFalse(Directory.Exists(Path.Combine(values.LR2CustomFolderOutputBaseDir, "Parent")));
                }
                finally
                {
                    playlist.RequestShutdown("output-overlap-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-overlap-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>user.config失敗ではXMLへ進まず、部分保存後の同じ入力でも成功済みconfigを再保存します。Closeと別dirty保存は先行保存を戻さず、次の同区分・別区分選択は公開値と今回入力だけを保存します。</summary>
    [TestMethod]
    [DataRow("normal")]
    [DataRow("root")]
    [DataRow("additional")]
    public void OutputConfirmation_LatestSelectionResavesBothMediaAfterPartialFailure(string section)
    {
        string directory = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string settingsPath = Path.Combine(directory, "settings", "user.config");
            Settings values = CreateValidCustomFolderSettings(directory, [], TestSettingsFactory.OpenSettings(settingsPath));
            values.Save();
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add,
                    settingsDialogService: new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) },
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: UndeclaredDialogs.Create());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                SetActiveLibraryProfile(owner, false);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                Lr2ConfigOwner xml = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                string normal = values.LR2CustomFolderOutputBaseDir, root = values.LR2CustomFolderOutputBaseDirRootType;
                string first = Path.Combine(directory, "First"), latest = Path.Combine(directory, "Latest");
                void Choose(string path)
                {
                    if (section == "normal") { dialog.LR2CustomFolderOutputDir = path; }
                    else if (section == "root") { dialog.LR2CustomFolderAsRootOutputDir = path; }
                    else { dialog.AddCustomFolderAdditionalOutputBaseDirs([path]); }
                }
                string Read(Settings settings) => section == "normal" ? settings.LR2CustomFolderOutputBaseDir
                    : section == "root" ? settings.LR2CustomFolderOutputBaseDirRootType : settings.LR2CustomFolderAdditionalOutputBaseDirs;
                string Expected(string path) => section == "additional" ? CustomFolderOutputBaseRegistry.SerializeBaseDirectories([path]) : path;
                try
                {
                    byte[] oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    string old = Read(values);
                    if (section != "normal") { dialog.LR2CustomFolderOutputDir = Path.Combine(directory, "NormalCandidate"); }
                    if (section != "root") { dialog.LR2CustomFolderAsRootOutputDir = Path.Combine(directory, "RootCandidate"); }
                    Choose(first);
                    using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-config-failure"); }
                    Assert.AreEqual(1, failures.Count);
                    Assert.AreEqual(old, Read(values));
                    Assert.AreEqual(old, Read(TestSettingsFactory.OpenSettings(settingsPath)));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Choose(first);
                    using (File.Open(values.LR2ConfigXmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-partial-save");
                    }
                    Assert.AreEqual(2, failures.Count);
                    Assert.AreEqual(old, Read(values));
                    Settings partial = TestSettingsFactory.OpenSettings(settingsPath);
                    Assert.AreEqual(Expected(first), Read(partial));
                    if (section != "normal") { Assert.AreEqual(normal, partial.LR2CustomFolderOutputBaseDir); }
                    if (section != "root") { Assert.AreEqual(root, partial.LR2CustomFolderOutputBaseDirRootType); }
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Choose(first);
                    using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-config-resave-failure"); }
                    Assert.AreEqual(3, failures.Count, "先行成功したuser.configも同じ入力で再保存します。");
                    Assert.AreEqual(old, Read(values));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    values.TreeViewWidth = 321d; values.Save();
                    Assert.AreEqual(Expected(first), Read(TestSettingsFactory.OpenSettings(settingsPath)));
                    byte[] saved = File.ReadAllBytes(settingsPath);
                    dialog.CloseCommand.Execute();
                    CollectionAssert.AreEqual(saved, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Assert.AreEqual(normal, dialog.LR2CustomFolderOutputDir);
                    Assert.AreEqual(root, dialog.LR2CustomFolderAsRootOutputDir);
                    Assert.AreEqual(0, dialog.CustomFolderAdditionalOutputBaseDirList.Count);
                    if (section == "normal")
                    {
                        string other = Path.Combine(directory, "OtherAdditional");
                        dialog.AddCustomFolderAdditionalOutputBaseDirs([other]);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "output-other-section-after-partial");
                        Settings otherSaved = TestSettingsFactory.OpenSettings(settingsPath);
                        Assert.AreEqual(first, otherSaved.LR2CustomFolderOutputBaseDir, "別区分の保存は、先行成功した未公開のnormal保存値を上書きしません。");
                        Assert.AreEqual(normal, values.LR2CustomFolderOutputBaseDir);
                        Assert.AreEqual(normal, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                        Assert.AreEqual(normal, composition.BmsLibraryOptionsProvider().LR2CustomFolderOutputBaseDir);
                        string expectedAdditional = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([other]);
                        Assert.AreEqual(expectedAdditional, otherSaved.LR2CustomFolderAdditionalOutputBaseDirs);
                        Assert.AreEqual(expectedAdditional, values.LR2CustomFolderAdditionalOutputBaseDirs);
                        Assert.AreEqual(expectedAdditional, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderAdditionalOutputBaseDirs);
                        CollectionAssert.DoesNotContain(TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), first);
                    }
                    Choose(latest);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-latest-save");
                    Assert.AreEqual(3, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(Expected(latest), Read(values));
                    Assert.AreEqual(Expected(latest), Read(TestSettingsFactory.OpenSettings(settingsPath)));
                    CollectionAssert.DoesNotContain(TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), first);
                    CollectionAssert.DoesNotContain(xml.Capture().RegisteredRoots.ToArray(), first);
                    if (section != "additional")
                    {
                        // 同値候補のsetter通知は保存せず、明示確定だけが共有違反を検出します。
                        Choose(latest);
                        Assert.AreEqual(3, failures.Count);
                        using (File.Open(values.LR2ConfigXmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-explicit-same-save"); }
                        Assert.AreEqual(4, failures.Count);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-explicit-same-success");
                        Assert.AreEqual(Expected(latest), Read(TestSettingsFactory.OpenSettings(settingsPath)));
                    }
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-partial-owner-cleanup");
                    dialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>改名のDB失敗後、同じruntimeは旧公開名から内部空白を含む新名へ保存・出力します。fresh ownerはdiskの新定義だけを読み、保存されなかった旧名を過去の改名対応で補わず、表の再選択で解消します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OutputConfirmation_HeaderFailureUsesPublishedNamesOrFreshSavedDefinitions(bool reopenOwner)
    {
        string directory = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string settingsPath = Path.Combine(directory, "settings", "user.config");
            string original = Path.Combine(directory, "Original"), failed = Path.Combine(directory, "Failed"), latest = Path.Combine(directory, "My Charts");
            Settings values = CreateValidCustomFolderSettings(directory, [original], TestSettingsFactory.OpenSettings(settingsPath));
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([original]);
            values.Save();
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                (MainWindowViewModel Owner, TestBmsPlaylist Playlist, ApplicationComposition Composition) OpenOwner(Settings settings)
                {
                    var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                    var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(settings),
                        reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs,
                        uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                        applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                        dialogService: UndeclaredDialogs.Create());
                    MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                    Lr2ConfigOwner config = composition.ResolveLr2Config(settings.LR2ConfigXmlPath);
                    SetPrivateField(owner, "lr2config", config);
                    SetActiveLibraryProfile(owner, true);
                    SetPrivateField(owner.SettingDialog, "statePort", new ActiveSettingsDialogStatePort());
                    var playlist = new TestBmsPlaylist(composition.DatabaseScope, settings.LR2SongDBPath, config, null, null,
                        () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings),
                        composition.CustomFolderOutputSettingsProvider,
                        PlaylistTestFixture.CreateDeterministicLr2PlaylistFolderSynchronizationPort(composition.DatabaseScope, settings.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                        mutationAdmission: composition.PlaylistOperationAdmission, settings: settings)
                    { BMSTables = new ObservableCollection<BMSTable>(new PlaylistPersistenceRepository(composition.DatabaseScope, settings.LR2SongDBPath).LoadPlaylistHeaders()) };
                    SetPrivateField(owner, "tables", playlist);
                    owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                    return (owner, playlist, composition);
                }
                void CloseOwner(MainWindowViewModel owner, TestBmsPlaylist playlist)
                {
                    playlist.RequestShutdown("output-header-owner");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-header-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
                (MainWindowViewModel Owner, TestBmsPlaylist Playlist, ApplicationComposition Composition) current = OpenOwner(values);
                try
                {
                    var table = new BMSTable
                    {
                        playlist_id = 7199,
                        name = "Table",
                        symbol = "T",
                        custom_folder_output_base_name = "Original",
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                        entries = [PlaylistTestFixture.CreateEntry(new string('d', 32), "Folder")],
                        Folder_order = ["Folder"]
                    };
                    current.Playlist.BMSTables.Add(table);
                    current.Playlist.CommitBMSTableWithEntriesToDB(table);
                    current.Owner.PlaylistWorkspace.RefreshPlaylistTreeTables(current.Playlist);
                    PlaylistSummaryRow summaryRow = BuildOutputSummary(current.Owner.PlaylistWorkspace);
                    Assert.AreEqual("Original", summaryRow.OutputBaseName);
                    using (LR2SongDBExtended db = current.Composition.DatabaseScope.OpenSongWrite(values.LR2SongDBPath))
                    { db.Execute("CREATE TRIGGER fail_output_header BEFORE INSERT ON playlist WHEN NEW.playlist_id = 7199 BEGIN SELECT RAISE(ABORT, 'header failed'); END;"); }
                    SettingsDialogViewModel dialog = current.Owner.SettingDialog;
                    dialog.SelectedCustomFolderAdditionalOutputBaseDir = original;
                    dialog.SelectedCustomFolderAdditionalOutputBaseName = "Failed";
                    dialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                    Assert.AreEqual("Original", summaryRow.OutputBaseName, "未公開draftを表示しません。");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "output-header-failure");
                    Assert.AreSame(summaryRow, current.Owner.PlaylistWorkspace.PlaylistSummaryView.Single());
                    Assert.AreEqual("Original", summaryRow.OutputBaseName);
                    Assert.AreEqual("Original", summaryRow.OutputBaseDisplayName);
                    Assert.AreEqual(1, failures.Count);
                    Assert.IsInstanceOfType<SQLite.SQLiteException>(failures[0]);
                    Assert.AreEqual("Original", table.custom_folder_output_base_name);
                    CollectionAssert.AreEqual(new[] { original }, dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                    Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([original]), values.LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([failed]), TestSettingsFactory.OpenSettings(settingsPath).LR2CustomFolderAdditionalOutputBaseDirs);
                    using (LR2SongDBExtended read = new BmsLibraryDbGateway(current.Composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly())
                    { Assert.AreEqual("Original", read.Table<BMSTable>().Single().custom_folder_output_base_name); }
                    using (LR2SongDBExtended db = current.Composition.DatabaseScope.OpenSongWrite(values.LR2SongDBPath)) { db.Execute("DROP TRIGGER fail_output_header;"); }
                    if (reopenOwner)
                    {
                        CloseOwner(current.Owner, current.Playlist);
                        values = TestSettingsFactory.OpenSettings(settingsPath);
                        current = OpenOwner(values);
                        dialog = current.Owner.SettingDialog;
                        table = current.Playlist.BMSTables.Single();
                        summaryRow = BuildOutputSummary(current.Owner.PlaylistWorkspace);
                    }
                    dialog.SelectedCustomFolderAdditionalOutputBaseDir = reopenOwner ? failed : original;
                    dialog.SelectedCustomFolderAdditionalOutputBaseName = "My Charts";
                    dialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "output-header-next-request");
                    Assert.AreEqual(1, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(reopenOwner ? "Original" : "My Charts", table.custom_folder_output_base_name);
                    Assert.AreSame(summaryRow, current.Owner.PlaylistWorkspace.PlaylistSummaryView.Single());
                    Assert.AreEqual(table.custom_folder_output_base_name, summaryRow.OutputBaseName);
                    Assert.AreEqual(reopenOwner ? "Original" : "My Charts", summaryRow.OutputBaseDisplayName);
                    Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([latest]), values.LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([latest]), TestSettingsFactory.OpenSettings(settingsPath).LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.AreEqual(!reopenOwner, File.Exists(Path.Combine(latest, "Table", "0000.lr2folder")));
                    Assert.IsFalse(Directory.Exists(Path.Combine(values.LR2CustomFolderOutputBaseDir, "Table")));
                    if (reopenOwner)
                    {
                        Task<PlaylistPropertyDialogViewModel> opening = current.Owner.PlaylistWorkspace.OpenPropertyDialogAsync(table);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(opening, "output-header-open-property");
                        PlaylistPropertyDialogViewModel property = opening.GetAwaiter().GetResult() ?? throw new AssertFailedException("Missing property dialog.");
                        using (property)
                        {
                            Assert.AreEqual("Original", property.custom_folder_output_base_option.BaseName);
                            property.custom_folder_output_base_option = property.OutputBaseOptions.Single(option => option.BaseName == "My Charts");
                            Task<PlaylistPropertyDialogOperationResult> saving = property.SaveAndApplyAsync();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(saving, "output-header-reselect");
                            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, saving.GetAwaiter().GetResult());
                        }
                        Assert.IsTrue(File.Exists(Path.Combine(latest, "Table", "0000.lr2folder")));
                    }
                    using LR2SongDBExtended verify = new BmsLibraryDbGateway(current.Composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly();
                    Assert.AreEqual("My Charts", verify.Table<BMSTable>().Single().custom_folder_output_base_name);
                    dialog.SelectedCustomFolderAdditionalOutputBaseDir = latest;
                    dialog.RemoveSelectedCustomFolderAdditionalOutputBaseDir();
                    Assert.AreEqual("My Charts", summaryRow.OutputBaseName, "削除draftを保存前に表示しません。");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "output-header-remove-base");
                    Assert.AreEqual(string.Empty, summaryRow.OutputBaseName);
                    Assert.AreEqual(Path.GetFileName(values.LR2CustomFolderOutputBaseDir), summaryRow.OutputBaseDisplayName);
                    Assert.AreEqual(1, failures.Count);
                }
                finally
                {
                    using (LR2SongDBExtended db = current.Composition.DatabaseScope.OpenSongWrite(values.LR2SongDBPath)) { db.Execute("DROP TRIGGER IF EXISTS fail_output_header;"); }
                    CloseOwner(current.Owner, current.Playlist);
                }
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>生成失敗と旧先のファイル・DB行整理失敗を区別し、採用済み設定を保ちます。整理失敗は対象と原因を通知して後続同期へ進み、次の明示選択で過去旧先を全回収しません。</summary>
    [TestMethod]
    [DataRow("generation")]
    [DataRow("lr2-sync")]
    [DataRow("file-cleanup")]
    [DataRow("row-cleanup")]
    public void OutputConfirmation_GenerationAndCleanupFailuresAllowLatestRequest(string failureKind)
    {
        bool syncFailure = failureKind == "lr2-sync";
        bool cleanupOnly = failureKind is "file-cleanup" or "row-cleanup", rowCleanup = failureKind == "row-cleanup";
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string settingsPath = Path.Combine(root, "settings", "user.config");
            string alternative = Path.Combine(root, "OtherSongs");
            Settings values = CreateValidCustomFolderSettings(root, [alternative], TestSettingsFactory.OpenSettings(settingsPath));
            values.Save();
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: UndeclaredDialogs.Create());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                SetPrivateField(owner.SettingDialog, "statePort", new ActiveSettingsDialogStatePort());
                var table = new BMSTable
                {
                    playlist_id = 7198,
                    name = "Table",
                    symbol = "T",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                    entries = [PlaylistTestFixture.CreateEntry(new string('d', 32), "Folder")],
                    Folder_order = ["Folder"]
                };
                var playlist = new TestBmsPlaylist(composition.DatabaseScope, values.LR2SongDBPath, configOwner, null, null,
                    () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
                    composition.CustomFolderOutputSettingsProvider,
                    PlaylistTestFixture.CreateDeterministicLr2PlaylistFolderSynchronizationPort(composition.DatabaseScope, values.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                    mutationAdmission: composition.PlaylistOperationAdmission, settings: values)
                { BMSTables = new ObservableCollection<BMSTable>([table]) };
                SetPrivateField(owner, "tables", playlist);
                owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                // 通知は受付の解放後に、窓口へ依頼された組（同じ見出し・重要度は一件へまとまる）で観測します。
                IEnumerable<string> warnings() => dialogs.MessageRequests.ToArray().Select(request => request.MessageBoxText);
                var externalSync = new RecordingOutputSettingsSyncRuntime { FailNext = syncFailure };
                SetPrivateField(owner.SettingDialog, "lr2SongDbSyncWorkflow", new Lr2SongDbSyncWorkflowOwner(externalSync));
                try
                {
                    playlist.CommitBMSTableWithEntriesToDB(table);
                    PlaylistSummaryRow summaryRow = BuildOutputSummary(owner.PlaylistWorkspace);
                    string originalDisplay = summaryRow.OutputBaseDisplayName;
                    int entryRevision = table.PlaylistEntriesRevision, totalCharts = summaryRow.TotalCharts;
                    string oldFile = Path.Combine(values.LR2CustomFolderOutputBaseDir, "Table", "0000.lr2folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
                    File.WriteAllText(oldFile, "old output");
                    string selected = Path.Combine(root, "SelectedOutput"), latest = Path.Combine(root, "LatestOutput");
                    string selectedFile = Path.Combine(selected, "Table", "0000.lr2folder");
                    string blockedFile = cleanupOnly ? oldFile : selectedFile;
                    if (rowCleanup)
                    {
                        using LR2SongDBExtended db = composition.DatabaseScope.OpenSongWrite(values.LR2SongDBPath);
                        db.CreateTable<LR2SongDB.folder>();
                        db.InsertOrReplace(new LR2SongDB.folder { path = oldFile, title = "old output", type = 2 }, typeof(LR2SongDB.folder));
                        db.Execute("CREATE TRIGGER fail_old_output_row BEFORE DELETE ON folder WHEN OLD.path = '" + oldFile.Replace("'", "''", StringComparison.Ordinal)
                            + "' BEGIN SELECT RAISE(ABORT, 'old-output-row-delete-fault'); END;");
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(blockedFile)!);
                        File.WriteAllText(blockedFile, "blocked output");
                    }
                    using (rowCleanup || syncFailure ? null : File.Open(blockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    {
                        owner.SettingDialog.LR2CustomFolderOutputDir = selected;
                        Assert.AreEqual(originalDisplay, summaryRow.OutputBaseDisplayName, "draftは確定表示を変えません。");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "output-selected-failure");
                        Assert.AreSame(summaryRow, owner.PlaylistWorkspace.PlaylistSummaryView.Single());
                        Assert.AreEqual("SelectedOutput", summaryRow.OutputBaseDisplayName);
                        Assert.AreEqual(entryRevision, table.PlaylistEntriesRevision);
                        Assert.AreEqual(totalCharts, summaryRow.TotalCharts);
                        Assert.AreEqual(string.Empty, summaryRow.OutputBaseName, "既定出力基点の変更はheaderを改名しません。");
                    }
                    Assert.AreEqual(cleanupOnly ? 0 : 1, failures.Count);
                    Assert.AreEqual(selected, values.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(selected, TestSettingsFactory.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(selected, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                    CollectionAssert.Contains(TestLr2ConfigFactory.Load(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), selected);
                    Assert.AreEqual(cleanupOnly || syncFailure, File.ReadAllText(selectedFile) != "blocked output");
                    Assert.AreEqual(!rowCleanup && !syncFailure, File.Exists(oldFile));
                    Assert.AreEqual(cleanupOnly || syncFailure ? 1 : 0, externalSync.ExternalSyncCount, "旧先整理だけの失敗でも必要な外部同期へ進みます。");
                    if (cleanupOnly) { Assert.IsTrue(warnings().Any(warning => warning.Contains(Path.GetDirectoryName(oldFile)!, StringComparison.Ordinal))); }
                    if (rowCleanup)
                    {
                        Assert.IsTrue(warnings().Any(warning => warning.Contains("old-output-row-delete-fault", StringComparison.Ordinal)));
                        using LR2SongDBExtended read = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly();
                        Assert.AreEqual(1, read.Table<LR2SongDB.folder>().Count(row => row.path == oldFile));
                        Assert.AreEqual(1, read.Table<LR2SongDB.folder>().Count(row => row.path == selectedFile));
                        using LR2SongDBExtended db = composition.DatabaseScope.OpenSongWrite(values.LR2SongDBPath);
                        db.Execute("DROP TRIGGER fail_old_output_row;");
                    }
                    owner.SettingDialog.LR2CustomFolderOutputDir = latest;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "output-latest-after-failure");
                    Assert.AreEqual(cleanupOnly ? 0 : 1, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(latest, values.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual("LatestOutput", summaryRow.OutputBaseDisplayName);
                    Assert.IsTrue(File.Exists(Path.Combine(latest, "Table", "0000.lr2folder")));
                    Assert.AreEqual(cleanupOnly || syncFailure ? 2 : 1, externalSync.ExternalSyncCount);
                    if (rowCleanup)
                    {
                        using LR2SongDBExtended read = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly();
                        Assert.AreEqual(1, read.Table<LR2SongDB.folder>().Count(row => row.path == oldFile), "次操作は過去の旧行整理を再開しません。");
                    }
                    else { Assert.AreEqual(!syncFailure, File.Exists(oldFile), "次操作は過去の旧先回収を再開しません。"); }

                }
                finally
                {
                    using (LR2SongDBExtended db = composition.DatabaseScope.OpenSongWrite(values.LR2SongDBPath)) { db.Execute("DROP TRIGGER IF EXISTS fail_old_output_row;"); }
                    playlist.RequestShutdown("install-directory-output-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "install-directory-output-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }


    /// <summary>設定操作から必要外部同期への接続を観測し、生成と旧行整理の実DB検証には関与しません。</summary>
    private sealed class RecordingOutputSettingsSyncRuntime : ILr2SongDbSyncWorkflowRuntime
    {
        public bool IsLr2ModeEnabled => true;
        public bool IsLibraryAvailable => true;
        internal int ExternalSyncCount { get; private set; }

        public Task<bool> QueueAsync(string reason, bool force, bool prepareGeneratedData = false,
            LibraryFileInitializationResult? initializationResult = null, LibraryFileMutationCapability? capability = null,
            bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null,
            Lr2SongDbSyncPreparedDataSurface? preparedSurface = null, OperationProgressRequest? originatingRequest = null,
            BmsLibraryOptionsSnapshot? optionsSnapshot = null, Action<bool>? admissionResult = null)
            => throw new AssertFailedException("Output settings must use the external-folder continuation.");

        internal bool FailNext { get; set; }

        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason,
            LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
        {
            Assert.IsNotNull(capability);
            Assert.IsNotNull(playlistCapability);
            ExternalSyncCount++;
            if (FailNext) { FailNext = false; return Task.FromException(new IOException("output-sync-failed")); }
            return Task.CompletedTask;
        }
    }

    private sealed class ActiveSettingsDialogStatePort : ISettingsDialogStatePort
    {
        public bool HasActiveLibraryProfile => true;

        public bool IsLibraryOperationInProgress => false;


        public Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
            => Task.FromResult(result with { CompletionPublished = result.Outcome == StartupInitializationOutcome.Succeeded });

        public Task<LeapYearFolderRepairApproval?> PrepareLibraryInitializationAsync() => Task.FromResult<LeapYearFolderRepairApproval?>(null);

        public Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification? notification) => Task.CompletedTask;

        public Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null,
            LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

        public Task PresentLibraryDirectoryWarningAsync(BeMusicSeeker.Models.BmsLibraryInternal.LibraryDirectoryPreflightException failure) => Task.CompletedTask;

        public void ReevaluatePendingPackagesForSearchRoots(LibraryFileMutationCapability capability, out Action publication) { publication = () => { }; }

        public Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null, Lr2SearchRootSnapshot? searchRoots = null) => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler? LibraryOperationAvailabilityChanged;

        public event Action<Lr2PlayHistorySchemaStatusSnapshot>? Lr2PlayHistorySchemaStatusChanged;
#pragma warning restore CS0067
    }
}
