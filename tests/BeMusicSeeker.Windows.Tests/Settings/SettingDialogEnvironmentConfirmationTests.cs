using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings;
using static BeMusicSeeker.Tests.SettingsRootTestFixture;
using static BeMusicSeeker.Tests.SettingsWindowTestFixture;
using static BeMusicSeeker.TestSupport.Components.SettingsMediaTestFixture;
using static BeMusicSeeker.TestSupport.Wpf.SettingsStartupTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>環境候補の確認・保存と初期化への接続を検証します。</summary>
/// <remarks>共有 culture・theme・Bass 受付と composition 購読を使うため非並列にします。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingDialogEnvironmentConfirmationTests
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

    /// <summary>このクラスの準備とモデルで共有するDBの所有者です。</summary>
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>初回・非初回修復中の未設定モード保存は再起動せず、実明示開始と保存物の次回通常起動は必須不足へ終端します。モデル工場・LR2 DB・出力へ進まないことをWindowなしで確認します。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnconfiguredLr2ModeSave_ExplicitStartAndFreshStartupRequireSettings(bool firstStartup)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(path));
                values.LR2RootPath = string.Empty;
                values.LR2SongDBPath = string.Empty;
                values.LR2ConfigXmlPath = string.Empty;
                values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "uncreated-output");
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "uncreated-root-output");
                values.Save();
                var applicationPath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "application", "BeMusicSeeker.exe"));
                foreach (bool freshStartup in new[] { false, true })
                {
                    Settings current = freshStartup ? TestSettingsFactory.OpenSettings(path) : values;
                    var dialogs = new RecordingRootDialogService();
                    var factory = new StartupLibraryConstructionTestSupport.NullStartupLibraryFactory();
                    var composition = new ApplicationComposition(
                        settingsEditSession: new SettingsEditSession(current),
                        uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher),
                        applicationLifetime: TestApplicationContext.CreateLifetime(freshStartup ? false : firstStartup),
                        cultureCatalog: TestApplicationContext.CreateCultureCatalog(), runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                        settingsDialogService: dialogs, fileDbMutationDialogService: dialogs,
                        dialogService: UndeclaredDialogs.Create());
                    MainWindowViewModel owner = new(composition, factory);
                    var sequence = new List<string>();
                    owner.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
                    dialogs.MessageObserved = () => sequence.Add("warning");
                    int restarts = 0;
                    owner.ShellShutdownWorkflow.OperationModeRestartRequested += () => restarts++;
                    try
                    {
                        if (!freshStartup)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SelectOperationModeAsync(true), "unconfigured-mode-in-place-save");
                            Assert.IsTrue(TestSettingsFactory.OpenSettings(path).OperationModeLR2DB);
                            Assert.IsTrue(owner.SettingDialog.IsEnvironmentSelectionEnabled);
                            Assert.IsTrue(owner.SettingDialog.IsInitialEnvironment);
                            Assert.AreEqual(0, factory.LibraryCallCount);
                            Assert.AreEqual(0, dialogs.MessageCount);
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.StartConfirmedEnvironmentAsync(), "unconfigured-mode-explicit-start");
                            CollectionAssert.AreEqual(new[] { "warning" }, sequence);
                        }
                        else
                        {
                            Task<bool> startup = owner.InitializeAsync();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "unconfigured-mode-fresh-startup");
                            Assert.IsFalse(startup.GetAwaiter().GetResult());
                            CollectionAssert.AreEqual(new[] { "warning", "open" }, sequence);
                        }
                        Assert.IsFalse(owner.IsInitializationCompleted);
                        Assert.IsFalse(owner.HasActiveLibraryProfile);
                        Assert.IsFalse(owner.SettingDialog.CanStartEnvironment);
                        Assert.IsTrue(owner.SettingDialog.IsEnvironmentSelectionEnabled);
                        Assert.IsFalse(owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                        Assert.AreEqual(0, restarts);
                        Assert.AreEqual(0, dialogs.ConfirmationCount);
                        Assert.AreEqual(1, dialogs.MessageCount);
                        // 明示開始・fresh起動のどちらも、起動時検査の一覧（LR2環境の項目を含む）を警告の行と設定画面へ同じ形で公開します。
                        Assert.IsTrue(owner.SettingDialog.Deficiencies.Any(item =>
                            item.Category == SettingsCategory.General && item.Field == SettingsDeficiencyField.Lr2Environment));
                        Assert.AreEqual(SettingsDeficiency.FormatStartupWarning(owner.SettingDialog.Deficiencies), dialogs.LastMessageText);
                        StringAssert.Contains(dialogs.LastMessageText, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                            Resources.SettingValidation_SectionMessageFormat, Resources.General, Resources.Error_InvalidLR2SongDbOrConfigPath));
                        Assert.AreEqual(0, factory.LibraryCallCount);
                        Assert.AreEqual(0, factory.PlaylistCallCount);
                        Assert.AreEqual(string.Empty, current.LR2RootPath);
                        Assert.AreEqual(string.Empty, current.LR2SongDBPath);
                        Assert.AreEqual(string.Empty, current.LR2ConfigXmlPath);
                        Assert.IsFalse(Directory.Exists(current.LR2CustomFolderOutputBaseDir));
                        Assert.IsFalse(Directory.Exists(current.LR2CustomFolderOutputBaseDirRootType));
                        Assert.IsFalse(File.Exists(applicationPath.StandaloneSongDbPath));
                    }
                    finally
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "unconfigured-mode-owner-terminal");
                        owner.SettingDialog.Dispose();
                    }
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    /// <summary>modeは旧禁止値のまま即時保存し、明示開始とfresh起動は構築前に訂正へ戻る。上位祖先baseへ訂正して開始した後の通常表編集・実出力でも旧builtinを変更しない。</summary>
    [TestMethod]
    public void ReservedOutputModeSave_FreshStartupRequiresCorrectionThenAncestorBaseAndPropertySaveSucceed()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string directory = CreateTemporaryRoot(CaseWorkspace);
            try
            {
                string lr2 = Path.Combine(directory, "LR2");
                string manual = Directory.CreateDirectory(Path.Combine(directory, "Music")).FullName;
                string chart = Path.Combine(manual, "chart.bms");
                File.WriteAllText(chart, BmsLibraryInitializationTestSupport.CreateValidBmsText("Accepted settings"));
                (string SongDb, string Config) layout = CreateValidLr2Layout(lr2);
                LR2Config xml = TestLr2ConfigFactory.Load(layout.Config);
                xml.SetBMSSearchDirectories([manual]); xml.Save(xml.ConfigFilePath);
                string builtin = Directory.CreateDirectory(Path.Combine(lr2, "LR2files", "CustomFolder")).FullName;
                string sentinel = Path.Combine(builtin, "sentinel.lr2folder");
                const string original = "#TITLE Builtin preserved\n#COMMAND 0\n";
                File.WriteAllText(sentinel, original);
                string settingsPath = Path.Combine(directory, "user.config");
                Settings values = CreateValidStandaloneSettings(manual, TestSettingsFactory.OpenSettings(settingsPath));
                values.LR2RootPath = lr2; values.LR2SongDBPath = layout.SongDb; values.LR2ConfigXmlPath = layout.Config;
                values.LR2CustomFolderOutputBaseDir = builtin;
                values.LR2CustomFolderOutputBaseDirRootType = Directory.CreateDirectory(Path.Combine(directory, "RootOutput")).FullName;
                values.LR2CustomFolderAdditionalOutputBaseDirs = "[]"; values.Save();
                foreach (bool fresh in new[] { false, true })
                {
                    Settings current = fresh ? TestSettingsFactory.OpenSettings(settingsPath) : values;
                    var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                    var failures = new List<Exception>();
                    var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(current),
                        reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, fileDbMutationDialogService: dialogs,
                        uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                        applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                        runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory, "application", "BeMusicSeeker.exe"))),
                        chartFileScanner: CapturedChartFileScanner.FromFixture([chart], new Dictionary<string, IEnumerable<string>> { [manual] = [] }, [manual]),
                        rootFileEnumerator: new FastRootFileEnumerator(), defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer(),
                        dialogService: UndeclaredDialogs.Create());
                    var calls = new List<string>();
                    var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, calls);
                    MainWindowViewModel owner = new(composition, factory);
                    owner.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort());
                    try
                    {
                        if (!fresh)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SelectOperationModeAsync(true), "reserved-mode-save");
                            Assert.IsTrue(TestSettingsFactory.OpenSettings(settingsPath).OperationModeLR2DB);
                            Assert.AreEqual(0, dialogs.MessageCount, "mode選択は保存し、検査は再起動後の共通入口へ委ねます。");
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.StartConfirmedEnvironmentAsync(), "reserved-explicit-start");
                        }
                        else
                        {
                            Task<bool> startup = owner.InitializeAsync();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "reserved-fresh-startup");
                            Assert.IsFalse(startup.GetAwaiter().GetResult());
                        }
                        Assert.IsFalse(owner.HasActiveLibraryProfile);
                        Assert.IsFalse(owner.IsInitializationCompleted);
                        Assert.AreEqual(0, calls.Count);
                        Assert.AreEqual(original, File.ReadAllText(sentinel));
                        if (!fresh) { continue; }
                        // LR2rootは直上LR2filesより上なので受理する。実効先包含の救済・再検査を後続へ足さない。
                        owner.SettingDialog.LR2CustomFolderOutputDir = lr2;
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "ancestor-base-correction");
                        Assert.AreEqual(lr2, TestSettingsFactory.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.StartConfirmedEnvironmentAsync(), "corrected-explicit-start");
                        Assert.IsTrue(owner.HasActiveLibraryProfile);
                        Assert.IsTrue(owner.IsInitializationCompleted);
                        AwaitStartupBackgroundCompletionOnDispatcher(owner, "corrected-startup-background");
                        BMSLibrary library = factory.CreatedLibrary ?? throw new AssertFailedException();
                        BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new AssertFailedException();
                        Lr2ConfigOwner config = composition.ResolveLr2Config(layout.Config);
                        var table = new BMSTable
                        {
                            playlist_id = 9091,
                            name = "Before",
                            is_bmt_output = false,
                            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                        };
                        playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
                        playlist.CommitBMSTableHeadersToDB([table]);
                        var service = new PlaylistPropertySaveService(() => playlist, () => library, () => config, composition.CustomFolderOutputSettingsProvider);
                        // この代表は実保存・出力までを担い、Windowを要しない表示通知は終端だけ受けます。
                        service.PlaylistPropertyCatalogChanged += (_, _) => { };
                        service.PlaylistPropertySummaryDataRefreshRequested += (_, _) => { };
                        Task<PlaylistPropertyEditSession> editing = service.CreateEditSessionAsync(table, false);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(editing, "accepted-property-edit");
                        using PlaylistPropertyEditSession session = editing.GetAwaiter().GetResult() ?? throw new AssertFailedException();
                        session.Values.Name = "CustomFolder"; session.Values.OutputDirectory = null;
                        Task<(PlaylistPropertySaveCommit Commit, PlaylistPropertyValidationError? ValidationError)> save = service.TrySaveAsync(session, session.Values);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(save, "accepted-property-save");
                        (PlaylistPropertySaveCommit Commit, PlaylistPropertyValidationError? ValidationError) saved = save.GetAwaiter().GetResult();
                        Assert.IsNotNull(saved.Commit); Assert.IsNull(saved.ValidationError); Assert.IsNull(saved.Commit.Exception);
                        string output = Path.Combine(lr2, "CustomFolder");
                        Assert.IsTrue(Directory.GetFiles(output, "*.lr2folder").Length > 0);
                        using (LR2SongDBExtended db = composition.DatabaseScope.OpenSongWrite(layout.SongDb))
                        { Assert.AreEqual("CustomFolder", db.Table<BMSTable>().Single(row => row.playlist_id == 9091).name); }
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(playlist.WaitForPlaylistMutationIdleAsync(), "accepted-property-mutation-idle");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.PlaylistWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync(), "accepted-property-summary-idle");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.PlaylistWorkspace.WaitForPlaylistReloadCleanupIdleAsync(), "accepted-property-cleanup-idle");
                        Assert.AreEqual(original, File.ReadAllText(sentinel));
                        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    }
                    finally
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "reserved-mode-owner-terminal");
                        owner.SettingDialog.Dispose();
                    }
                }
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    /// <summary>初回root選択と修復時の詳細XML確定から履歴対象を更新し、候補・保存失敗では旧対象を保ち、保存後のschema操作が新playerのDBだけへ届くことを確認します。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Lr2EnvironmentConfirmation_RefreshesHistoryTargetOnlyAfterSaving(bool initialRootSelection)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            try
            {
                string oldRoot = Path.Combine(root, "previous-lr2");
                string selectedRoot = Path.Combine(root, "selected-lr2");
                (string oldSong, string oldConfig) = CreateValidLr2Layout(oldRoot);
                (string nextSong, string nextConfig) = CreateValidLr2Layout(selectedRoot);
                File.WriteAllText(oldConfig, "<config><system /><player><id>previous</id></player><jukebox /></config>");
                File.WriteAllText(nextConfig, "<config><system /><player><id>selected</id></player><jukebox /></config>");
                string oldScore = Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(oldRoot, "previous");
                // 詳細XMLの個別指定ではrootを変えず、そのXML内playerに対応するDBを使います。
                string nextScore = Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(initialRootSelection ? selectedRoot : oldRoot, "selected");
                foreach (string score in new[] { oldScore, nextScore })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(score)!);
                    using SQLiteConnectionEx database = databaseScope.OpenProviderConnection(score);
                    database.RunInTransaction(() =>
                    {
                        database.CreateTable<LR2ScoreDB.score>();
                        database.CreateTable<LR2ScoreDB.player>();
                        database.Execute("CREATE TABLE retained_data (value TEXT);");
                        database.Execute("INSERT INTO retained_data VALUES (?);", score);
                    });
                }
                var schema = new Lr2PlayHistorySchemaService(databaseScope);
                Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, schema.InstallOrRepair(oldScore, isLr2LinkedProfile: true).Status);
                byte[] previousDatabase = File.ReadAllBytes(oldScore);
                string settingsPath = Path.Combine(root, "user.config");
                Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(settingsPath));
                values.OperationModeLR2DB = true;
                values.LR2RootPath = initialRootSelection ? string.Empty : oldRoot;
                values.LR2SongDBPath = initialRootSelection ? string.Empty : oldSong;
                values.LR2ConfigXmlPath = initialRootSelection ? string.Empty : oldConfig;
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: initialRootSelection,
                    dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                Assert.IsFalse(owner.HasActiveLibraryProfile);
                string previousTarget = initialRootSelection ? string.Empty : oldScore;
                Assert.AreEqual(previousTarget, dialog.Lr2PlayHistoryScoreDbPath);
                if (!initialRootSelection)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.InstallOrRepairLr2PlayHistorySchemaAsync(), "history-previous-status");
                    Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, dialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
                }
                Lr2PlayHistorySchemaStatusSnapshot? previousStatus = dialog.Lr2PlayHistorySchemaStatusSnapshot;
                dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), nextSong).GetAwaiter().GetResult();
                dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), nextConfig).GetAwaiter().GetResult();
                Assert.AreEqual(previousTarget, dialog.Lr2PlayHistoryScoreDbPath, "未確定XMLは履歴操作の対象を切り替えません。");
                Assert.AreSame(previousStatus, dialog.Lr2PlayHistorySchemaStatusSnapshot);
                Task ConfirmEnvironment() => initialRootSelection ? dialog.SelectLr2RootAsync(selectedRoot) : dialog.CommitEnvironmentAsync();
                using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(ConfirmEnvironment(), "history-environment-save-failure");
                }
                Assert.AreEqual(1, failures.Count);
                Assert.AreEqual(previousTarget, dialog.Lr2PlayHistoryScoreDbPath);
                Assert.AreSame(previousStatus, dialog.Lr2PlayHistorySchemaStatusSnapshot);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(ConfirmEnvironment(), "history-environment-confirmed");
                Assert.AreEqual(nextScore, dialog.Lr2PlayHistoryScoreDbPath);
                Assert.IsNull(dialog.Lr2PlayHistorySchemaStatusSnapshot, "保存終端で旧playerの検査状態を持ち越しません。");
                Assert.AreEqual(nextConfig, TestSettingsFactory.OpenSettings(settingsPath).LR2ConfigXmlPath);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.InstallOrRepairLr2PlayHistorySchemaAsync(), "history-new-target-install");
                Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, schema.Check(nextScore, isLr2LinkedProfile: true).Status);
                Assert.AreEqual(nextScore, dialog.Lr2PlayHistorySchemaStatusSnapshot.ScoreDbPath);
                Assert.AreEqual(initialRootSelection ? 2 : 1, dialogs.ConfirmationCount, "初回rootの詳細候補破棄と、新対象のschema導入だけを確認します。");
                Assert.AreEqual(1, failures.Count);
                CollectionAssert.AreEqual(previousDatabase, File.ReadAllBytes(oldScore), "旧DBのschemaと保存行は変更しません。");
                using SQLiteConnectionEx verify = MainWindowViewModelTestFactory.GetComposition(owner ?? throw new AssertFailedException()).DatabaseScope.OpenProviderConnection(nextScore, SQLite.SQLiteOpenFlags.ReadOnly);
                Assert.AreEqual(nextScore, verify.ExecuteScalar<string>("SELECT value FROM retained_data;"));
            }
            finally
            {
                if (owner != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "history-environment-owner-terminal");
                    owner.SettingDialog.Dispose();
                }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>
    /// 保証：有効なプロファイルがない状態の環境確定は、同じ設定XMLのまま実効LR2ルートが変わっても、採用済みの同じ所有者を確定後の実効ルートへ束縛し直す。
    /// song.db だけを個別指定へ変えるとルートなし、標準配置へ戻すと推測したルート、モードだけの確定でも単独動作はルートなし、LR2連携は推測したルートになる。
    /// 確定時に束縛し直さない誤りと、所有者を作り直す誤りを検出する。
    /// </summary>
    /// <remarks>設定画面の実際の確定入口と構成の所有者取得を接続して確認します。束縛し直しの内容は起動時の検査入口のテストが担います。</remarks>
    [TestMethod]
    public void InitialEnvironmentCommit_RebindsAdoptedLr2ConfigOwnerToConfirmedEffectiveRoot()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string confirmedRoot = Path.Combine(root, "confirmed");
            Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
            (string standardSong, string configPath) = CreateValidLr2Layout(confirmedRoot);
            (values.LR2SongDBPath, values.LR2ConfigXmlPath) = (standardSong, configPath);
            values.LR2RootPath = string.Empty;
            values.Save();
            string individualSong = Path.Combine(root, "individual", "song.db");
            Directory.CreateDirectory(Path.GetDirectoryName(individualSong)!);
            File.WriteAllBytes(individualSong, []);
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs, reportSettingsApplyFailure: _ => { });
            using SettingsDialogViewModel dialog = owner.SettingDialog;
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
            dialog.SelectOperationModeAsync(true).GetAwaiter().GetResult();
            Assert.IsFalse(owner.HasActiveLibraryProfile);
            Lr2ConfigOwner config = composition.ResolveLr2Config(configPath);
            Assert.AreEqual(confirmedRoot, config.Lr2RootPath, ignoreCase: true);

            dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), individualSong).GetAwaiter().GetResult();
            dialog.CommitEnvironmentAsync().GetAwaiter().GetResult();

            Assert.AreEqual(individualSong, values.LR2SongDBPath);
            Assert.AreSame(config, composition.ResolveLr2Config(configPath));
            Assert.AreEqual(string.Empty, config.Lr2RootPath);

            dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), standardSong).GetAwaiter().GetResult();
            dialog.CommitEnvironmentAsync().GetAwaiter().GetResult();

            Assert.AreEqual(standardSong, values.LR2SongDBPath);
            Assert.AreEqual(confirmedRoot, config.Lr2RootPath, ignoreCase: true);

            dialog.SelectOperationModeAsync(false).GetAwaiter().GetResult();
            Assert.IsFalse(values.OperationModeLR2DB);
            Assert.AreEqual(string.Empty, config.Lr2RootPath);

            dialog.SelectOperationModeAsync(true).GetAwaiter().GetResult();
            Assert.IsTrue(values.OperationModeLR2DB);
            Assert.AreSame(config, composition.ResolveLr2Config(configPath));
            Assert.AreEqual(confirmedRoot, config.Lr2RootPath, ignoreCase: true);
            Assert.AreEqual(string.Empty, values.LR2RootPath, "推測した値を設定へ保存しません。");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// 保証：環境確定の予約領域の検査は、保存するLR2ディレクトリではなく確定後の環境の実効LR2ルートで行う。LR2ディレクトリが空のまま
    /// song.db を標準配置へ変える確定で実効ルートが決まる場合、その組込みカスタムフォルダ配下にあるBMS登録を理由に確定を拒否し、設定を変えない。
    /// 保存値のルートだけで検査して受理する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void InitialEnvironmentCommit_ChecksReservedBuiltinFolderWithCandidateEffectiveRoot()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string lr2Root = Path.Combine(root, "confirmed");
            Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
            (string standardSong, string configPath) = CreateValidLr2Layout(lr2Root);
            string reservedRegistration = Path.Combine(lr2Root, "LR2files", "CustomFolder", "Registered");
            Directory.CreateDirectory(reservedRegistration);
            File.WriteAllText(configPath, "<config><system /><jukebox><path>" + System.Security.SecurityElement.Escape(reservedRegistration) + "\\</path></jukebox></config>");
            string individualSong = Path.Combine(root, "individual", "song.db");
            Directory.CreateDirectory(Path.GetDirectoryName(individualSong)!);
            File.WriteAllBytes(individualSong, []);
            values.OperationModeLR2DB = true;
            values.LR2RootPath = string.Empty;
            (values.LR2SongDBPath, values.LR2ConfigXmlPath) = (individualSong, configPath);
            values.Save();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs, reportSettingsApplyFailure: _ => { });
            using SettingsDialogViewModel dialog = owner.SettingDialog;
            Assert.IsFalse(owner.HasActiveLibraryProfile);

            dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), standardSong).GetAwaiter().GetResult();
            dialog.CommitEnvironmentAsync().GetAwaiter().GetResult();

            Assert.AreEqual(individualSong, values.LR2SongDBPath, "実効ルートの組込み領域にある登録を残したままの確定は保存しません。");
            Assert.AreEqual(string.Empty, values.LR2RootPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>モードは保存済み個別組（空rootも可）、root選択は今回の標準組だけを保存します。詳細候補・Busy・実保存失敗から混成値や遅延適用を公開しません。</summary>
    [TestMethod]
    public void Lr2AutomaticSelection_UsesConfirmedModeTupleAndSelectedRootAndKeepsFailureRepairable()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string path = Path.Combine(root, "user.config");
            Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(path));
            (values.LR2SongDBPath, values.LR2ConfigXmlPath) = CreateValidLr2Layout(Path.Combine(root, "confirmed"));
            values.LR2RootPath = string.Empty;
            values.Save();
            string savedSong = values.LR2SongDBPath;
            string savedConfig = values.LR2ConfigXmlPath;
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
            using SettingsDialogViewModel dialog = owner.SettingDialog;
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
            (string draftSong, string draftConfig) = CreateValidLr2Layout(Path.Combine(root, "draft"));
            dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), draftSong).GetAwaiter().GetResult();
            dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), draftConfig).GetAwaiter().GetResult();
            dialog.SelectOperationModeAsync(true).GetAwaiter().GetResult();
            Settings saved = TestSettingsFactory.OpenSettings(path);
            Assert.IsTrue(saved.OperationModeLR2DB);
            Assert.IsTrue(composition.CustomFolderOutputSettingsProvider().OperationModeLR2DB, "初回開始の出力readerも保存済みモードを読みます。");
            Assert.AreEqual(string.Empty, saved.LR2RootPath);
            Assert.AreEqual(savedSong, saved.LR2SongDBPath);
            Assert.AreEqual(savedConfig, saved.LR2ConfigXmlPath);
            Assert.IsTrue(dialog.IsInitialEnvironment);
            Assert.IsFalse(owner.HasActiveLibraryProfile);
            (string selectedSong, string selectedConfig) = CreateValidLr2Layout(Path.Combine(root, "selected"));
            dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), draftSong).GetAwaiter().GetResult();
            dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), draftConfig).GetAwaiter().GetResult();
            dialog.SelectLr2RootAsync(Path.Combine(root, "missing")).GetAwaiter().GetResult();
            Assert.AreEqual(savedSong, values.LR2SongDBPath);
            foreach (ChartFileOperationSynchronizer admission in new[] { composition.OperationAdmission, composition.PlaylistOperationAdmission })
            {
                Assert.IsTrue(admission.TryEnter(out IDisposable held));
                using (held) { dialog.SelectLr2RootAsync(Path.Combine(root, "selected")).GetAwaiter().GetResult(); }
                Assert.AreEqual(savedSong, values.LR2SongDBPath);
            }
            Assert.AreEqual(savedSong, TestSettingsFactory.OpenSettings(path).LR2SongDBPath);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { dialog.SelectLr2RootAsync(Path.Combine(root, "selected")).GetAwaiter().GetResult(); }
            Assert.AreEqual(1, failures.Count);
            Assert.AreEqual(Path.Combine(root, "confirmed"), composition.CustomFolderOutputSettingsProvider().LR2RootPath,
                "保存失敗では出力readerへ新rootを公開せず、保存済みの組から決まる実効LR2ルートのままにします。");
            Assert.AreEqual(savedSong, values.LR2SongDBPath);
            Assert.AreEqual(savedConfig, values.LR2ConfigXmlPath);
            dialog.SelectLr2RootAsync(Path.Combine(root, "selected")).GetAwaiter().GetResult();
            saved = TestSettingsFactory.OpenSettings(path);
            Assert.AreEqual(selectedSong, saved.LR2SongDBPath);
            Assert.AreEqual(Path.Combine(root, "selected"), composition.CustomFolderOutputSettingsProvider().LR2RootPath);
            Assert.AreEqual(selectedConfig, saved.LR2ConfigXmlPath);
            Assert.AreEqual(selectedSong, dialog.LR2SongDBPath);
            Assert.IsFalse(dialog.HasEnvironmentChanges);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { dialog.SelectLr2RootAsync(Path.Combine(root, "selected")).GetAwaiter().GetResult(); }
            Assert.AreEqual(1, failures.Count, "同値選択では再保存しません。");
            Assert.AreEqual(2, dialogs.ConfirmationCount, "初回は競合する詳細候補の破棄だけを確認し、再起動は要求しません。");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Pは未設定standaloneからLR2、Lは有効LR2からstandaloneの実UIで許可される入力を使い、確認中の先着拒否・候補保持と次の明示選択だけによる確定組保存を確認します。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Lr2AutomaticSelection_OperationAcceptedDuringConfirmationRejectsWithoutDeferredRestart(bool playlistOperation)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            string path = Path.Combine(root, "user.config");
            Settings values = playlistOperation
                ? CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(path))
                : CreateValidCustomFolderSettings(Path.Combine(root, "confirmed-lr2"), [], TestSettingsFactory.OpenSettings(path));
            if (playlistOperation)
            {
                values.LR2RootPath = string.Empty;
                values.LR2SongDBPath = string.Empty;
                values.LR2ConfigXmlPath = string.Empty;
            }
            values.Save();
            var confirmed = EnvironmentSettingsChange.Capture(values);
            bool targetMode = !confirmed.OperationModeLR2DB;
            var session = new CountingSettingsEditSession(values) { PersistOwnedValues = true };
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            MainWindowViewModel owner = CreateViewModel(session, firstStartup: false, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
            SettingsDialogViewModel dialog = owner.SettingDialog;
            IDisposable? heldOperation = null;
            try
            {
                SetActiveLibraryProfile(owner, true);
                int restartRequests = 0;
                owner.ShellShutdownWorkflow.OperationModeRestartRequested += () => restartRequests++;
                string staged = Directory.CreateDirectory(Path.Combine(root, "PendingBms")).FullName;
                (string draftSong, string draftConfig) = playlistOperation ? (string.Empty, string.Empty) : CreateValidLr2Layout(Path.Combine(root, "draft"));
                // 詳細欄はLR2選択時だけ有効です。PはstandaloneのBMS候補、Lは確定LR2の詳細候補を分担します。
                if (playlistOperation)
                { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AddBmsSearchRootPathsAsync([staged]), "mode-confirmation-bms-input"); }
                else
                {
                    Assert.IsTrue(dialog.OperationModeLR2DB);
                    CollectionAssert.Contains(dialog.AvailableBMSDirectories, values.BMSRootPath);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), draftSong), "mode-confirmation-song-input");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), draftConfig), "mode-confirmation-config-input");
                }
                byte[] before = File.ReadAllBytes(path);
                dialogs.ConfirmationObserved = request =>
                {
                    StringAssert.Contains(request.MessageBoxText, Resources.Confirm_RestartForOperationModeChange);
                    StringAssert.Contains(request.MessageBoxText, playlistOperation
                        ? Resources.Standalone_BMSDirectories.Trim().TrimEnd(':', '：') : Resources.Settings_lr2_detailed_paths);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    ChartFileOperationSynchronizer admission = playlistOperation ? composition.PlaylistOperationAdmission : composition.OperationAdmission;
                    Assert.IsTrue(admission.TryEnter(out IDisposable acquired));
                    heldOperation = acquired;
                    if (playlistOperation) { Assert.IsFalse(owner.IsLibraryOperationInProgress, "P-onlyの先着は起動・譜面変更状態だけでは検出できません。"); }
                };
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectOperationModeAsync(targetMode), "mode-confirmation-operation-wins");
                if (playlistOperation) { CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged); }
                Assert.AreEqual(draftSong, dialog.LR2SongDBPath);
                Assert.AreEqual(draftConfig, dialog.LR2ConfigXmlPath);
                Assert.AreEqual(0, session.SaveCount);
                Assert.AreEqual(0, restartRequests);
                Assert.AreEqual(confirmed, EnvironmentSettingsChange.Capture(values));
                Assert.AreEqual(confirmed.OperationModeLR2DB, dialog.OperationModeLR2DB);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
                Assert.AreEqual(!playlistOperation, composition.OperationAdmission.IsActive, "先着Pで拒否した場合は取得済みLも解放します。");
                Assert.AreEqual(playlistOperation, composition.PlaylistOperationAdmission.IsActive);
                Assert.IsFalse(dialog.IsEditCompletionInProgress);
                heldOperation?.Dispose();
                heldOperation = null;
                dialogs.ConfirmationObserved = null;
                dialog.SetPresentationActive(false);
                dialog.SetPresentationActive(true);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(1, dialogs.ConfirmationCount);
                Assert.AreEqual(0, session.SaveCount);
                Assert.AreEqual(0, restartRequests);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
                session.SaveObserved = () =>
                {
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                };
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectOperationModeAsync(targetMode), "mode-confirmation-explicit-retry");
                Assert.AreEqual(2, dialogs.ConfirmationCount);
                Assert.AreEqual(1, session.SaveCount);
                Assert.AreEqual(1, restartRequests);
                Assert.AreEqual(confirmed with { OperationModeLR2DB = targetMode }, EnvironmentSettingsChange.Capture(TestSettingsFactory.OpenSettings(path)));
                Assert.IsFalse(composition.OperationAdmission.IsActive, "受理後は終了join前にLを解放します。");
                Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive, "受理後は終了join前にPを解放します。");
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                session.SaveObserved = null;
                heldOperation?.Dispose();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "mode-confirmation-playlist-owner-terminal");
                dialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>実個別picker二つは同じLR2詳細候補を編集するだけで、詳細buttonの一回だけが両パスを選択保存して再起動を受理します。</summary>
    [TestMethod]
    public void Lr2DetailedPickers_ConfirmBothPathsOnceWithoutIncludingBeatorajaDraft()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "old"), [], TestSettingsFactory.OpenSettings(path));
                values.Save();
                (string song, string config) = CreateValidLr2Layout(Path.Combine(root, "individual"));
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, dialogs: dialogs);
                SetActiveLibraryProfile(owner, true);
                int restarts = 0;
                owner.ShellShutdownWorkflow.OperationModeRestartRequested += () => restarts++;
                window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog };
                scope.ShowAndWaitForContentRendered(window);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                Expander details = FindDescendants<Expander>(window).First(item => item.Content is DependencyObject content
                    && FindLogicalDescendants<Button>(content).Any(button => Equals(button.Tag, "confirm:environment")));
                details.IsExpanded = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                dialog.UseBeatorajaScoreDb = true;
                dialog.BeatorajaRootPath = Path.Combine(root, "unconfirmed-beatoraja");
                string oldSong = values.LR2SongDBPath;
                string oldConfig = values.LR2ConfigXmlPath;
                foreach ((string property, string selected) in new[] { (nameof(dialog.LR2SongDBPath), song), (nameof(dialog.LR2ConfigXmlPath), config) })
                {
                    dialogs.FileResults.Enqueue(new UiFilePickerResult(UiDialogStatus.Accepted, [selected]));
                    SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(window).Single(item => item.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == property);
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "LR2-individual-picker-terminal");
                    Assert.AreEqual(oldSong, TestSettingsFactory.OpenSettings(path).LR2SongDBPath);
                    Assert.AreEqual(oldConfig, values.LR2ConfigXmlPath);
                    Assert.AreEqual(0, restarts);
                }
                FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:environment")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "LR2-detailed-confirmation-terminal");
                Settings saved = TestSettingsFactory.OpenSettings(path);
                Assert.AreEqual(song, saved.LR2SongDBPath);
                Assert.AreEqual(config, saved.LR2ConfigXmlPath);
                Assert.IsFalse(saved.UseBeatorajaScoreDb);
                Assert.AreNotEqual(dialog.BeatorajaRootPath, saved.BeatorajaRootPath);
                Assert.AreEqual(1, restarts);
                Assert.AreEqual(3, dialogs.ConfirmationCount, "個別警告二回と一回の再起動確認です。");
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                if (owner != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "LR2-detail-owner-terminal");
                    owner.SettingDialog.Dispose();
                }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>通常起動の実閏年確認中はモデル構築・L/P取得前でも環境反映と明示開始を拒否し、普通項目の保存は続けられます。</summary>
    [TestMethod]
    public void NormalStartupPreparation_RejectsEnvironmentChangesBeforeLibraryCreation()
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                string settingsPath = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(settingsPath));
                new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).EnsureAppOwnedSchema();
                string folder = Path.Combine(root, "LeapCandidate");
                Directory.CreateDirectory(folder);
                Directory.SetLastWriteTime(folder, new DateTime(2024, 2, 29, 12, 0, 0));
                using (LR2SongDBExtended db = databaseScope.OpenSongWrite(values.LR2SongDBPath))
                {
                    db.CreateTable<LR2SongDB.song>(); db.CreateTable<LR2SongDB.folder>();
                    db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, adddate = 0 });
                }
                values.Save();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var dialogs = new RecordingRootDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No),
                    ConfirmationObservedAsync = () => { entered.TrySetResult(); return release.Task; }
                };
                var failures = new List<Exception>();
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values), settingsDialogService: dialogs,
                    fileDbMutationDialogService: dialogs, reportSettingsApplyFailure: failures.Add,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.MissingEverythingBridge), rootFileEnumerator: new FastRootFileEnumerator(),
                    dialogService: UndeclaredDialogs.Create());
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, []);
                var owner = new MainWindowViewModel(composition, factory);
                Task<bool> startup = owner.InitializeAsync();
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, startup, "startup-actual-preparation"), "startup-preparation-arrival");
                    Assert.IsTrue(owner.IsLibraryOperationInProgress);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    Assert.AreEqual(0, factory.Calls.Count);
                    SettingsDialogViewModel dialog = owner.SettingDialog;
                    var captured = EnvironmentSettingsChange.Capture(values);
                    string oldBeatoraja = values.BeatorajaRootPath;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.StartConfirmedEnvironmentAsync(), "preparation-reject-start");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectOperationModeAsync(false), "preparation-reject-mode");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectLr2RootAsync(root), "preparation-reject-root");
                    dialog.BeatorajaRootPath = Path.Combine(root, "other");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitScoreSettingsAsync(), "preparation-reject-beatoraja");
                    Assert.AreEqual(captured, EnvironmentSettingsChange.Capture(TestSettingsFactory.OpenSettings(settingsPath)));
                    Assert.AreEqual(oldBeatoraja, values.BeatorajaRootPath);
                    bool ordinary = !values.ShowNewPackageInstallConfirmMsg;
                    dialog.ShowNewPackageInstallConfirmMsg = ordinary;
                    Assert.AreEqual(ordinary, TestSettingsFactory.OpenSettings(settingsPath).ShowNewPackageInstallConfirmMsg);
                    Assert.AreEqual(0, factory.Calls.Count);
                    release.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "startup-preparation-terminal");
                    Assert.IsTrue(startup.GetAwaiter().GetResult(), string.Join(Environment.NewLine, failures));
                }
                finally
                {
                    release.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "startup-preparation-cleanup");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "startup-preparation-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>実通常起動の設定案内モーダルが未完了でも、解放済みの排他と抑止を使って有効設定を保存し、同じWindowから明示開始できます。</summary>
    [TestMethod]
    public void SettingsGuidance_ExplicitStartSucceedsWhileOriginalModalInitializationIsPending()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
            values.OperationModeLR2DB = true;
            values.LR2SongDBPath = string.Empty;
            values.LR2ConfigXmlPath = string.Empty;
            values.Save();
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService();
            var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, fileDbMutationDialogService: dialogs,
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "application", "BeMusicSeeker.exe"))),
                rootFileEnumerator: new FastRootFileEnumerator(), audioDeviceCatalog: new TestAudioDeviceCatalog(),
                chartFileScanner: CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [root] = [] }, [root]),
                dialogService: UndeclaredDialogs.Create());
            MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
            var window = new SettingsWindow(UndeclaredDialogs.Create()) { DataContext = owner.SettingDialog };
            Task<bool>? original = null;
            Task? interaction = null;
            owner.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(request =>
            {
                if (request == "open") { scope.PrepareForOwnedPresentation(window); window.ShowDialog(); }
                if (request == "close") { window.CloseFromPresentation(); }
            }));
            try
            {
                window.ContentRendered += (_, _) => window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => interaction = StartInsideGuidanceAsync()));
                // 同期ShowDialogの再入より先に参照を受け取り、実初期化を次のDispatcher継続で開始します。
                async Task<bool> StartNormalAsync() { await Task.Yield(); return await owner.InitializeAsync(); }
                original = StartNormalAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(original, "settings-guidance-original-terminal");
                Assert.IsNotNull(interaction);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(interaction, "settings-guidance-start-terminal");
                Assert.IsFalse(original.GetAwaiter().GetResult(), "元要求の設定不備結果を、後続の成功で読み替えません。");
                Assert.IsTrue(owner.HasActiveLibraryProfile, dialogs.LastMessageText + " / " + string.Join(Environment.NewLine, failures));
                Assert.IsFalse(window.IsVisible);
                Assert.AreEqual(SettingsWindowCloseReason.InitializationCompleted, window.CloseReason);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));

                async Task StartInsideGuidanceAsync()
                {
                    try
                    {
                        Assert.IsNotNull(original);
                        Assert.IsFalse(original.IsCompleted);
                        Assert.IsFalse(owner.IsLibraryOperationInProgress);
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                        await owner.SettingDialog.SelectOperationModeAsync(false);
                        Assert.IsFalse(owner.HasActiveLibraryProfile, "有効設定の自動反映だけでは走査を開始しません。");
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        var start = (Button)window.FindName("buttonOK");
                        Assert.IsTrue(start.IsEnabled);
                        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await owner.SettingDialog.SettingsOperationCompletionTask;
                    }
                    finally { if (window.IsVisible) { window.CloseForOwnerShutdown(); } }
                }
            }
            finally
            {
                if (window.IsVisible) { window.CloseForOwnerShutdown(); }
                if (interaction != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(interaction, "settings-guidance-interaction-cleanup"); }
                if (original != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(original, "settings-guidance-original-cleanup"); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "settings-guidance-owner-cleanup");
                owner.SettingDialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>確定スコア使用がONなら、有効rootでも解決player/score.dbが無効な局所組を保存・出力前に拒否し、スコアを無効化しません。</summary>
    [TestMethod]
    [DataRow(false)] // playerは存在しますが、そのscore.dbがありません。
    [DataRow(true)] // 構成にplayer名があっても、playerディレクトリがありません。
    public void BeatorajaSelection_InvalidResolvedScoreDatabaseRejectsBeforeSideEffects(bool missingPlayer)
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            string oldRoot = Path.Combine(root, "old-beatoraja");
            string newRoot = Path.Combine(root, "new-beatoraja");
            WriteBeatorajaRoot(oldRoot, "old-player", ["old-player"], createScoreFiles: true);
            WriteBeatorajaRoot(newRoot, "new-player", missingPlayer ? [] : ["new-player"], createScoreFiles: false);
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(settingsPath));
            values.BeatorajaRootPath = oldRoot;
            values.BeatorajaPlayerId = "old-player";
            values.BeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(oldRoot, "old-player");
            values.BeatorajaBmtTablePath = BeatorajaConfigService.GetTablePath(oldRoot);
            values.UseBeatorajaScoreDb = true;
            values.EnableBeatorajaBmtOutput = true;
            values.Save();
            byte[] savedBytes = File.ReadAllBytes(settingsPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: UndeclaredDialogs.Create());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                int restarts = 0;
                owner.ShellShutdownWorkflow.OperationModeRestartRequested += () => restarts++;
                SetActiveLibraryProfile(owner, true);
                try
                {
                    owner.SettingDialog.BeatorajaRootPath = newRoot;
                    owner.SettingDialog.BeatorajaPlayerId = "invalid-score-candidate";
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitBeatorajaRootSettingsAsync(), "beatoraja-invalid-score");
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreEqual(Resources.Error_InvalidBeatorajaScoreDbPath, dialogs.LastMessageText);
                    Assert.AreEqual(0, dialogs.ConfirmationCount);
                    Assert.AreEqual(0, failures.Count);
                    Assert.AreEqual(0, restarts);
                    Assert.IsFalse(owner.ShellShutdownWorkflow.IsShutdownRequested);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    CollectionAssert.AreEqual(savedBytes, File.ReadAllBytes(settingsPath));
                    Assert.AreEqual(oldRoot, values.BeatorajaRootPath);
                    Assert.AreEqual(oldRoot, TestSettingsFactory.OpenSettings(settingsPath).BeatorajaRootPath);
                    Assert.IsFalse(Directory.Exists(BeatorajaConfigService.GetTablePath(newRoot)), "拒否では新環境の実BMT出力を開始しません。");
                    Assert.AreEqual(newRoot, owner.SettingDialog.BeatorajaRootPath);
                    Assert.IsTrue(owner.SettingDialog.UseBeatorajaScoreDb);
                    Assert.AreEqual("invalid-score-candidate", owner.SettingDialog.BeatorajaPlayerId);
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "environment-invalid-owner-terminal");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>実環境ボタンは局所差分・既存検証・実受付で有効状態を更新し、無効候補も取消できます。取消後は差分を消し、保存を行いません。</summary>
    [TestMethod]
    public void EnvironmentButtons_TrackLocalValidityAdmissionAndCancellation()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
            values.OperationModeLR2DB = true;
            (values.LR2SongDBPath, values.LR2ConfigXmlPath) = CreateValidLr2Layout(Path.Combine(root, "lr2"));
            values.UseBeatorajaScoreDb = false;
            values.EnableBeatorajaBmtOutput = false;
            values.Save();
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(values);
            SetActiveLibraryProfile(owner, true);
            SettingsDialogViewModel settings = owner.SettingDialog;
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow(UndeclaredDialogs.Create()) { DataContext = settings };
                scope.ShowAndWaitForContentRendered(window);
                Expander details = FindDescendants<Expander>(window).First(item => item.Content is DependencyObject content
                    && FindLogicalDescendants<Button>(content).Any(button => Equals(button.Tag, "confirm:environment")));
                details.IsExpanded = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Button confirm = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:environment"));
                Button cancel = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "cancel:environment"));
                Assert.IsFalse(confirm.IsEnabled);
                Assert.IsFalse(cancel.IsEnabled);
                string song = Path.Combine(root, "candidate.db");
                File.WriteAllBytes(song, []);
                settings.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), song).GetAwaiter().GetResult();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(confirm.IsEnabled);
                Assert.IsTrue(cancel.IsEnabled);
                Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable? libraryLease));
                using (libraryLease)
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(confirm.IsEnabled);
                    Assert.IsTrue(cancel.IsEnabled);
                }
                Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable? outputLease));
                using (outputLease)
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(confirm.IsEnabled);
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(confirm.IsEnabled);
                File.Delete(song);
                settings.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), song).GetAwaiter().GetResult();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(confirm.IsEnabled);
                Assert.IsTrue(cancel.IsEnabled);
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(confirm.IsEnabled);
                Assert.IsFalse(cancel.IsEnabled);
                Assert.IsFalse(settings.HasEnvironmentChanges);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "environment-button-owner-terminal");
                settings.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>実root pickerの同値選択は保存済み表記・候補を保ち、有効な別rootの選択は旧root前提候補を確認し、確定方針でplayer・派生先だけ保存して関係候補を破棄します。</summary>
    /// <remarks>configured採用と一覧先頭の既存解決、保存失敗後の明示修復を確認します。同値での保存・処理要求0は狭いBMT方針検査も分担します。</remarks>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)] // 実providerの保存拒否では組を公開せず、同じ局所候補を明示再試行できます。
    public void BeatorajaRootPicker_SavesConfirmedPolicyAndDiscardsOnlyRelatedDrafts(bool configuredPlayerExists, bool failSave)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            SettingsWindow? window = null;
            MainWindowViewModel? owner = null;
            try
            {
                string oldRoot = Path.Combine(root, "old-beatoraja");
                string newRoot = Path.Combine(root, "new-beatoraja");
                WriteBeatorajaRoot(oldRoot, "old-player", ["old-player", "draft-player"], createScoreFiles: true);
                WriteBeatorajaRoot(newRoot, configuredPlayerExists ? "z-player" : "missing", ["a-player", "z-player"], createScoreFiles: true);
                string settingsPath = Path.Combine(root, "settings", "user.config");
                Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(settingsPath));
                values.BeatorajaRootPath = oldRoot;
                values.BeatorajaPlayerId = "old-player";
                values.BeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(oldRoot, "old-player");
                values.BeatorajaBmtTablePath = BeatorajaConfigService.GetTablePath(oldRoot);
                values.UseBeatorajaScoreDb = true;
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, audioDeviceCatalog: new TestAudioDeviceCatalog(),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: UndeclaredDialogs.Create());
                owner = composition.CreateMainWindowViewModelForTest();
                SettingsDialogViewModel dialog = owner.SettingDialog;
                window = new SettingsWindow(dialogs) { DataContext = dialog, PlaybackPanel = owner.PlaybackPanel };
                scope.ShowAndWaitForContentRendered(window);
                ComboBox scorePlayer = FindDescendants<ComboBox>(window).Single(item => item.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BeatorajaPlayerId));
                SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(window).Single(item => item.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BeatorajaRootPath));
                byte[] originalSettings = File.ReadAllBytes(settingsPath);
                int saves = 0;
                int selectionChanges = 0;
                System.Configuration.SettingsSavingEventHandler saving = (_, _) => saves++;
                EventHandler selectionChanged = (_, _) => selectionChanges++;
                values.SettingsSaving += saving;
                dialog.ScoreSelectionChanged += selectionChanged;
                try
                {
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [oldRoot.ToUpperInvariant()]));
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "beatoraja-equivalent-root-picker-terminal");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(oldRoot, values.BeatorajaRootPath);
                    Assert.AreEqual(oldRoot, dialog.BeatorajaRootPath);
                    Assert.AreEqual("old-player", dialog.BeatorajaPlayerId);
                    Assert.AreEqual(values.BeatorajaScoreDbPath, dialog.EnvironmentBeatorajaScoreDbPath);
                    Assert.AreEqual(values.BeatorajaBmtTablePath, dialog.EnvironmentBeatorajaBmtTablePath);
                    Assert.IsFalse(dialog.HasScoreSelectionChanges);
                    Assert.AreEqual(0, saves);
                    Assert.AreEqual(0, selectionChanges, "同値pickerは自動反映の選択通知も発行しません。");
                    Assert.AreEqual(0, failures.Count);
                    CollectionAssert.AreEqual(originalSettings, File.ReadAllBytes(settingsPath));
                    Assert.AreEqual(oldRoot, TestSettingsFactory.OpenSettings(settingsPath).BeatorajaRootPath);
                }
                finally
                {
                    values.SettingsSaving -= saving;
                    dialog.ScoreSelectionChanged -= selectionChanged;
                }
                File.Delete(BeatorajaConfigService.GetScoreDbPath(oldRoot, "draft-player"));
                scorePlayer.SetCurrentValue(ComboBox.SelectedItemProperty, "draft-player");
                dialog.EnableBeatorajaBmtOutput = true;
                dialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
                byte[] beforeRejectedRoot = File.ReadAllBytes(settingsPath);
                dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [newRoot]));
                FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "beatoraja-root-discard-cancelled");
                Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                Assert.AreEqual(oldRoot, dialog.BeatorajaRootPath);
                CollectionAssert.AreEqual(beforeRejectedRoot, File.ReadAllBytes(settingsPath));
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.CancelledByUser));
                FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "beatoraja-root-picker-cancelled");
                Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
                Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable? refusedLease));
                using (refusedLease)
                {
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [newRoot]));
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "beatoraja-root-admission-refused");
                    Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                    Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                    CollectionAssert.AreEqual(beforeRejectedRoot, File.ReadAllBytes(settingsPath));
                }
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [newRoot]));
                using (FileStream? blocker = failSave ? File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                {
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "beatoraja-root-picker-terminal");
                }
                if (failSave)
                {
                    Settings unchanged = TestSettingsFactory.OpenSettings(settingsPath);
                    Assert.AreEqual(oldRoot, unchanged.BeatorajaRootPath);
                    Assert.AreEqual("old-player", unchanged.BeatorajaPlayerId);
                    Assert.AreEqual(BeatorajaConfigService.GetScoreDbPath(oldRoot, "old-player"), unchanged.BeatorajaScoreDbPath);
                    Assert.AreEqual(BeatorajaConfigService.GetTablePath(oldRoot), unchanged.BeatorajaBmtTablePath);
                    Assert.AreEqual(1, failures.Count);
                    Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                    Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [newRoot]));
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "beatoraja-root-save-explicit-retry");
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                string expectedPlayer = configuredPlayerExists ? "z-player" : "a-player";
                Settings saved = TestSettingsFactory.OpenSettings(settingsPath);
                Assert.AreEqual(newRoot, saved.BeatorajaRootPath);
                Assert.AreEqual(expectedPlayer, saved.BeatorajaPlayerId);
                Assert.AreEqual(Path.Combine(newRoot, "profiles", expectedPlayer, "score.db"), saved.BeatorajaScoreDbPath);
                Assert.AreEqual(Path.Combine(newRoot, "tables"), saved.BeatorajaBmtTablePath);
                Assert.AreEqual(saved.BeatorajaScoreDbPath, dialog.EnvironmentBeatorajaScoreDbPath);
                Assert.AreEqual(saved.BeatorajaBmtTablePath, dialog.EnvironmentBeatorajaBmtTablePath);
                Assert.AreEqual(expectedPlayer, dialog.BeatorajaPlayerId);
                Assert.AreEqual(expectedPlayer, scorePlayer.SelectedItem);
                Assert.IsTrue(saved.UseBeatorajaScoreDb);
                Assert.IsFalse(saved.EnableBeatorajaBmtOutput);
                Assert.IsFalse(dialog.EnableBeatorajaBmtOutput);
                Assert.AreEqual(saved.BeatorajaBmtHashOutputMode, dialog.BeatorajaBmtHashOutputMode);
                Assert.AreEqual(failSave ? 4 : 3, dialogs.ConfirmationCount);
                Assert.IsTrue(window.IsVisible);
                Assert.IsTrue(dialog.IsInitialEnvironment);
                Assert.IsFalse(owner.ShellShutdownWorkflow.IsShutdownRequested);
                Assert.AreEqual(failSave ? 1 : 0, failures.Count, string.Join(Environment.NewLine, failures));
                if (failSave)
                {
                    File.WriteAllText(Path.Combine(newRoot, BeatorajaConfigService.ConfigFileName), System.Text.Json.JsonSerializer.Serialize(
                        new { playerpath = "profiles", playername = "z-player", tablepath = "tables", tableURL = Array.Empty<string>() }));
                }
                string nextPlayer = configuredPlayerExists ? "a-player" : "z-player";
                scorePlayer.SetCurrentValue(ComboBox.SelectedItemProperty, nextPlayer);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SettingsOperationCompletionTask, "beatoraja-player-selection-terminal");
                Assert.AreEqual(nextPlayer, TestSettingsFactory.OpenSettings(settingsPath).BeatorajaPlayerId);
                Assert.AreEqual(BeatorajaConfigService.GetScoreDbPath(newRoot, nextPlayer), dialog.EnvironmentBeatorajaScoreDbPath);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                if (owner != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "beatoraja-picker-owner-terminal");
                    owner.SettingDialog.Dispose();
                }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static System.Collections.Generic.IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) { continue; }
            if (element is T match) { yield return match; }
            foreach (T nested in FindLogicalDescendants<T>(element)) { yield return nested; }
        }
    }
}
