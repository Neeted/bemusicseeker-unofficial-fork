using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using SQLite;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class Lr2PlayHistorySchemaUiTests
{
    /// <summary>このケースのモデルと接続で共有するDBの所有者です。</summary>
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 現在のLR2スコアDBに一致する検査通知だけを採用し、別パス/非LR2通知を除外する。resetとDispose後は表示状態を保持しない。
    /// </summary>
    /// <remarks>実設定画面VMと専用設定を使い、確定モード・状態購読と機能窓口の接続を確認する。共有構成を使うため並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public void SettingDialogSchemaStatusReceipt_FiltersPathAndClearsOnResetAndDispose()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        using SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
        Settings values = TestSettingsFactory.CreateIsolatedSettings(value => value.OperationModeLR2DB = true);
        var settingsSession = new SchemaSettingsSession(values);
        var statePort = new TestSettingsDialogStatePort(
            owner,
            () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)));
        IUiDialogService schemaDialogs = UndeclaredDialogs.Create();
        using var settingDialog = new SettingsDialogViewModel(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope,
            statePort,
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            new RecordingPlayHistoryPort(),
            owner.LibraryFolderTree,
            new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(), dialogService: UndeclaredDialogs.Create()),
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsEditSession: settingsSession,
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(), applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(schemaDialogs, new Lr2ApplicationDataUninstallStore(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope)), schemaDialogs: schemaDialogs);
        const string scoreDbPath = "C:\\lr2\\score.db";
        SetPrivateField(settingDialog, "operationModeLR2DB", true);
        SetPrivateField(settingDialog, "lr2PlayHistoryScoreDbPath", scoreDbPath);

        var matchingSnapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(
            new Lr2PlayHistorySchemaCheckResult
            {
                Status = Lr2PlayHistorySchemaStatus.Installed,
                ScoreDbPath = scoreDbPath,
                Reason = Lr2PlayHistorySchemaReason.Diagnostic,
                Diagnostic = "matching"
            });
        statePort.NotifyLr2PlayHistorySchemaStatusChanged(matchingSnapshot);
        Assert.AreSame(matchingSnapshot, settingDialog.Lr2PlayHistorySchemaStatusSnapshot);

        statePort.NotifyLr2PlayHistorySchemaStatusChanged(
            Lr2PlayHistorySchemaStatusSnapshot.FromResult(
                new Lr2PlayHistorySchemaCheckResult
                {
                    Status = Lr2PlayHistorySchemaStatus.Repairable,
                    ScoreDbPath = "C:\\other\\score.db",
                    Reason = Lr2PlayHistorySchemaReason.Diagnostic,
                    Diagnostic = "stale"
                }));
        Assert.AreSame(matchingSnapshot, settingDialog.Lr2PlayHistorySchemaStatusSnapshot);

        SetPrivateField(settingDialog, "operationModeLR2DB", false);
        var candidateUnchangedSnapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(new Lr2PlayHistorySchemaCheckResult
        {
            Status = Lr2PlayHistorySchemaStatus.Repairable,
            ScoreDbPath = scoreDbPath,
            Reason = Lr2PlayHistorySchemaReason.Diagnostic,
            Diagnostic = "confirmed LR2 despite candidate mode"
        });
        statePort.NotifyLr2PlayHistorySchemaStatusChanged(candidateUnchangedSnapshot);
        Assert.AreSame(candidateUnchangedSnapshot, settingDialog.Lr2PlayHistorySchemaStatusSnapshot,
            "未確定のモード候補は稼働中LR2の通知を拒否しません。");
        values.OperationModeLR2DB = false;
        statePort.NotifyLr2PlayHistorySchemaStatusChanged(
            Lr2PlayHistorySchemaStatusSnapshot.FromResult(
                new Lr2PlayHistorySchemaCheckResult
                {
                    Status = Lr2PlayHistorySchemaStatus.Repairable,
                    ScoreDbPath = scoreDbPath,
                    Reason = Lr2PlayHistorySchemaReason.Diagnostic,
                    Diagnostic = "non-lr2"
                }));
        Assert.AreSame(candidateUnchangedSnapshot, settingDialog.Lr2PlayHistorySchemaStatusSnapshot);
        SetPrivateField(settingDialog, "operationModeLR2DB", true);
        values.OperationModeLR2DB = true;

        statePort.NotifyLr2PlayHistorySchemaStatusChanged(Lr2PlayHistorySchemaStatusSnapshot.Reset);
        Assert.IsNull(settingDialog.Lr2PlayHistorySchemaStatusSnapshot);

        settingDialog.Dispose();
        statePort.NotifyLr2PlayHistorySchemaStatusChanged(matchingSnapshot);
        Assert.IsNull(settingDialog.Lr2PlayHistorySchemaStatusSnapshot);
    }

    private sealed class SchemaSettingsSession(Settings values) : ISettingsEditSession
    {
        public Settings Values => values;
        public void Reload() { }
        public void Save() => throw new AssertFailedException("通知の受信で保存しません。");
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string identity)
            => throw new AssertFailedException("通知の受信で環境を変更しません。");
    }

    /// <summary>
    /// 初期化中・ライブラリ変更中・playlist変更中は導入確認を出さず受付警告を返す。片側取得だけに成功した排他権も解放する。
    /// </summary>
    /// <remarks>実設定画面VMと構成を使い、受付/状態購読と機能窓口の接続を確認する。共有設定のedit sessionを使うため並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    [DataRow("initializing")]
    [DataRow("library")]
    [DataRow("playlist")]
    public async Task SettingDialogViewModel_InstallOrRepair_BlockedOperationUsesOwnerDialog(string blockedDomain)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        using SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
        ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
        var dialogs = new RecordingUiDialogService();
        using var settingDialog = new SettingsDialogViewModel(composition.DatabaseScope,
            owner,
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            owner.LibraryFolderTree,
            composition,
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsEditSession: SettingsEditSession.CreateDefault(),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            schemaDialogs: dialogs,
            schemaWindowDialogs: dialogs,
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(),
            operationAdmission: composition.OperationAdmission, applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(dialogs, new Lr2ApplicationDataUninstallStore(composition.DatabaseScope)));
        IDisposable? held = null;
        if (blockedDomain == "initializing") { owner.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true); }
        else { Assert.IsTrue((blockedDomain == "library" ? composition.OperationAdmission : composition.PlaylistOperationAdmission).TryEnter(out held)); }
        try
        {
            await settingDialog.InstallOrRepairLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            StringAssert.Contains(dialogs.LastMessage, blockedDomain == "initializing"
                ? Resources.Msg_settings_apply_blocked_during_initialization : Resources.Warn_LibraryOperationBusy);
            if (blockedDomain == "playlist") { Assert.IsFalse(composition.OperationAdmission.IsActive, "片側Busyでは取得したLを解放します。"); }
        }
        finally
        {
            held?.Dispose();
            owner.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
        }
    }

    /// <summary>導入確認は確定XMLのplayer DBと候補破棄案内を一回で示し、取消/L/P拒否/DB不成立では候補と両DBを保持します。成功時だけ関係XML候補を戻します。</summary>
    /// <remarks>実SQLiteを別playerで区別し、設定VMの通常入口からcache失効/必要score処理まで確認します。compiled buttonの閉鎖抑止はWindow系列へ分担します。</remarks>
    [DoNotParallelize]
    [TestMethod]
    [DataRow("cancel")]
    [DataRow("library-busy")]
    [DataRow("playlist-busy")]
    [DataRow("invalid-db")]
    [DataRow("accepted")]
    public void SettingDialogViewModel_InstallUsesConfirmedPlayerAndPreservesCandidatesUntilAccepted(string outcome)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = Path.Combine(caseWorkspace.RootDirectory, "Lr2SchemaConfirmed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindowViewModel? owner = null;
            SettingsDialogViewModel? dialog = null;
            IDisposable? held = null;
            try
            {
                Settings values = CreateConfirmedSchemaSettings(root);
                string score = Path.Combine(root, "LR2files", "Database", "Score", "player1.db");
                if (outcome == "invalid-db") { File.WriteAllText(score, "invalid SQLite"); }
                else { using SQLiteConnectionEx db = databaseScope.OpenProviderConnection(score); db.CreateTable<LR2ScoreDB.score>(); db.CreateTable<LR2ScoreDB.player>(); }
                owner = MainWindowViewModelTestFactory.Create();
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
                var dialogs = new RecordingUiDialogService { AcceptConfirmation = outcome == "accepted" };
                var history = new RecordingPlayHistoryPort();
                var state = new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)));
                dialog = new SettingsDialogViewModel(composition.DatabaseScope, state, owner.PlaylistWorkspace, owner.PlaylistWorkspace, history,
                    owner.LibraryFolderTree, composition, owner.PlaybackPanel, owner.Lr2SongDbSyncWorkflow,
                    settingsEditSession: new SettingsEditSession(values), applicationLifetime: TestApplicationContext.CreateLifetime(),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), schemaDialogs: dialogs, schemaWindowDialogs: dialogs,
                    externalShellGateway: ExternalShellGatewayPolicy.Current, applicationPathSnapshot: ApplicationPathPolicy.Current,
                    audioDeviceCatalog: new TestAudioDeviceCatalog(), audioSettingsGateway: new TestAudioSettingsGateway(),
                    audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(), operationAdmission: composition.OperationAdmission, applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(dialogs, new Lr2ApplicationDataUninstallStore(composition.DatabaseScope)));
                string draft = StageDifferentSchemaCandidate(dialog, root);
                byte[] saved = File.ReadAllBytes(Path.Combine(root, "user.config"));
                byte[] sourceBefore = File.ReadAllBytes(score);
                string candidateDb = Path.Combine(root, "LR2files", "Database", "Score", "player2.db");
                byte[] candidateBefore = File.ReadAllBytes(candidateDb);
                if (outcome is "library-busy" or "playlist-busy")
                { Assert.IsTrue((outcome == "library-busy" ? composition.OperationAdmission : composition.PlaylistOperationAdmission).TryEnter(out held)); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.InstallOrRepairLr2PlayHistorySchemaAsync(), "confirmed schema install");
                Assert.AreEqual(outcome == "accepted" ? values.LR2ConfigXmlPath : draft, dialog.LR2ConfigXmlPath);
                Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                CollectionAssert.AreEqual(saved, File.ReadAllBytes(Path.Combine(root, "user.config")));
                CollectionAssert.AreEqual(candidateBefore, File.ReadAllBytes(candidateDb));
                if (outcome is "accepted" or "cancel")
                {
                    Assert.AreEqual(1, dialogs.ConfirmationCount);
                    StringAssert.Contains(dialogs.LastConfirmation, score);
                    StringAssert.Contains(dialogs.LastConfirmation, Resources.Settings_lr2_detailed_paths);
                }
                else { Assert.AreEqual(0, dialogs.ConfirmationCount); }
                if (outcome == "accepted")
                {
                    Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, new Lr2PlayHistorySchemaService(composition.DatabaseScope).Check(score, true).Status);
                }
                else { CollectionAssert.AreEqual(sourceBefore, File.ReadAllBytes(score)); }
                held?.Dispose(); held = null;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(outcome is "accepted" or "cancel" ? 1 : 0, dialogs.ConfirmationCount, "受付解放だけで自動再操作しません。");
            }
            finally { held?.Dispose(); dialog?.Dispose(); owner?.SettingDialog.Dispose(); Directory.Delete(root, recursive: true); }
        });
    }

    /// <summary>
    /// 稼働profileの導入・修復・両削除は実DB操作後に成績と履歴を再取得し、取得Taskの終端まで設定操作を完了しない。
    /// </summary>
    /// <remarks>実設定画面VMと構成を使い、受付/状態購読と機能窓口の接続を確認する。共有設定のedit sessionを使うため並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    [DataRow("install")]
    [DataRow("repair")]
    [DataRow("triggers")]
    [DataRow("tables")]
    public async Task SettingDialogViewModel_SchemaChangeReloadsCapturedBatchAndWaitsForTerminal(string operation)
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directoryPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            Settings values = CreateConfirmedSchemaSettings(directoryPath);
            string scoreDbPath = Path.Combine(directoryPath, "LR2files", "Database", "Score", "player1.db");
            CreateInstalledScoreDb(scoreDbPath);
            bool installing = operation is "install" or "repair";
            if (operation == "install") { new Lr2PlayHistorySchemaService(databaseScope).Uninstall(scoreDbPath, true, Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers); }
            if (operation == "repair")
            {
                using SQLiteConnectionEx db = databaseScope.OpenProviderConnection(scoreDbPath);
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName);
            }
            Lr2PlayHistorySchemaStatus expectedStatus = installing ? Lr2PlayHistorySchemaStatus.Installed
                : operation == "tables" ? Lr2PlayHistorySchemaStatus.NotInstalled : Lr2PlayHistorySchemaStatus.Repairable;
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
            SetPrivateField(owner, "hasActiveLibraryProfile", true);
            var dialogs = new RecordingUiDialogService
            {
                AcceptConfirmation = true,
                AcceptUninstall = true,
                SelectedUninstallMode = operation == "tables" ? Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers : Lr2PlayHistorySchemaUninstallMode.TriggersOnly
            };
            int reloadCount = 0;
            ScorePlayHistorySnapshot? acquired = null;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statePort = new TestSettingsDialogStatePort(
                owner,
                () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)),
                reloadScoresOnly: async _ =>
                {
                    reloadCount++;
                    entered.TrySetResult();
                    await release.Task;
                    acquired = new BmsLibraryInitializationService().LoadScoreTable(
                        new BmsLibraryDbGateway(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath, scoreDbPath), new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true }).PlayHistorySnapshot;
                });
            var playHistory = new RecordingPlayHistoryPort();
            using var settingDialog = new SettingsDialogViewModel(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope,
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(), dialogService: UndeclaredDialogs.Create()),
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsEditSession: new SettingsEditSession(values),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            schemaDialogs: dialogs,
            schemaWindowDialogs: dialogs,
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(), applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(dialogs, new Lr2ApplicationDataUninstallStore(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope)));


            string candidate = StageDifferentSchemaCandidate(settingDialog, directoryPath);
            string candidateDb = Path.Combine(directoryPath, "LR2files", "Database", "Score", "player2.db");
            byte[] candidateBefore = File.ReadAllBytes(candidateDb);
            byte[] saved = File.ReadAllBytes(Path.Combine(directoryPath, "user.config"));
            Task operationTask = installing ? settingDialog.InstallOrRepairLr2PlayHistorySchemaAsync() : settingDialog.UninstallLr2PlayHistorySchemaAsync();
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operationTask, "schema reload entered");
                Assert.IsFalse(operationTask.IsCompleted);
                Assert.IsNull(acquired);
            }
            finally { release.TrySetResult(); await operationTask; }
            Assert.IsNotNull(acquired);
            Assert.AreEqual(expectedStatus, acquired.Lr2.SchemaStatus);
            Assert.AreEqual(dialogs.AcceptUninstall ? values.LR2ConfigXmlPath : candidate, settingDialog.LR2ConfigXmlPath);
            Assert.IsTrue(settingDialog.EnableBeatorajaBmtOutput);
            StringAssert.Contains(installing ? dialogs.LastConfirmation : dialogs.LastCandidateDiscardNotice, Resources.Settings_lr2_detailed_paths);
            CollectionAssert.AreEqual(candidateBefore, File.ReadAllBytes(candidateDb));
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(Path.Combine(directoryPath, "user.config")));

            Assert.AreEqual(installing ? 0 : 1, dialogs.WindowCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(installing ? Resources.Msg_success_lr2_play_history_schema_install_or_repair : Resources.Msg_success_lr2_play_history_schema_uninstall, dialogs.LastMessage);
            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual(expectedStatus, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using SQLiteConnectionEx verify = MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope.OpenProviderConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            Assert.AreEqual(operation == "tables" ? 0 : 1, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;",
                Lr2PlayHistorySchemaService.PlayHistoryTableName));
            Assert.AreEqual(installing ? 1 : 0, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'trigger' AND name = ?;",
                Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 完全解除は実履歴objectを除き、score表を残して成功通知・cache失効・NotInstalled表示を渡す。不要なscore再読込みはしない。
    /// </summary>
    /// <remarks>実設定画面VMと構成を使い、受付/状態購読と機能窓口の接続を確認する。共有設定のedit sessionを使うため並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task SettingDialogViewModel_Uninstall_TablesAndTriggersRemovesObjects()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directoryPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            Settings values = CreateConfirmedSchemaSettings(directoryPath);
            string scoreDbPath = Path.Combine(directoryPath, "LR2files", "Database", "Score", "player1.db");
            CreateInstalledScoreDb(scoreDbPath);
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
            var dialogs = new RecordingUiDialogService
            {
                AcceptUninstall = true,
                SelectedUninstallMode = Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers
            };
            int reloadCount = 0;
            var statePort = new TestSettingsDialogStatePort(
                owner,
                () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)),
                reloadScoresOnly: _ =>
                {
                    reloadCount++;
                    return Task.CompletedTask;
                });
            var playHistory = new RecordingPlayHistoryPort();
            using var settingDialog = new SettingsDialogViewModel(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope,
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(), dialogService: UndeclaredDialogs.Create()),
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsEditSession: new SettingsEditSession(values),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            schemaDialogs: dialogs,
            schemaWindowDialogs: dialogs,
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(), applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(dialogs, new Lr2ApplicationDataUninstallStore(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope)));


            string candidate = StageDifferentSchemaCandidate(settingDialog, directoryPath);
            string candidateDb = Path.Combine(directoryPath, "LR2files", "Database", "Score", "player2.db");
            byte[] candidateBefore = File.ReadAllBytes(candidateDb);
            byte[] saved = File.ReadAllBytes(Path.Combine(directoryPath, "user.config"));
            await settingDialog.UninstallLr2PlayHistorySchemaAsync();
            Assert.AreEqual(dialogs.AcceptUninstall ? values.LR2ConfigXmlPath : candidate, settingDialog.LR2ConfigXmlPath);
            Assert.IsTrue(settingDialog.EnableBeatorajaBmtOutput);
            StringAssert.Contains(dialogs.LastCandidateDiscardNotice, Resources.Settings_lr2_detailed_paths);
            CollectionAssert.AreEqual(candidateBefore, File.ReadAllBytes(candidateDb));
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(Path.Combine(directoryPath, "user.config")));

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(Resources.Msg_success_lr2_play_history_schema_uninstall, dialogs.LastMessage);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using SQLiteConnectionEx verify = MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope.OpenProviderConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            Assert.AreEqual(0, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE name LIKE 'bms_lr2_%' OR name LIKE 'idx_bms_lr2_%';"));
            Assert.AreEqual(1, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'score';"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 解除確認の取消では通知・再読込み・cache失効を行わず、実triggerとInstalled表示を維持する。
    /// </summary>
    /// <remarks>実設定画面VMと構成を使い、受付/状態購読と機能窓口の接続を確認する。共有設定のedit sessionを使うため並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task SettingDialogViewModel_Uninstall_CancelLeavesDurableAndLiveStateUnchanged()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directoryPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            Settings values = CreateConfirmedSchemaSettings(directoryPath);
            string scoreDbPath = Path.Combine(directoryPath, "LR2files", "Database", "Score", "player1.db");
            CreateInstalledScoreDb(scoreDbPath);
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
            var dialogs = new RecordingUiDialogService { AcceptUninstall = false };
            int reloadCount = 0;
            var statePort = new TestSettingsDialogStatePort(
                owner,
                () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)),
                reloadScoresOnly: _ =>
                {
                    reloadCount++;
                    return Task.CompletedTask;
                });
            var playHistory = new RecordingPlayHistoryPort();
            using var settingDialog = new SettingsDialogViewModel(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope,
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(), dialogService: UndeclaredDialogs.Create()),
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsEditSession: new SettingsEditSession(values),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            schemaDialogs: dialogs,
            schemaWindowDialogs: dialogs,
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(), applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(dialogs, new Lr2ApplicationDataUninstallStore(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope)));


            string candidate = StageDifferentSchemaCandidate(settingDialog, directoryPath);
            string candidateDb = Path.Combine(directoryPath, "LR2files", "Database", "Score", "player2.db");
            byte[] candidateBefore = File.ReadAllBytes(candidateDb);
            byte[] saved = File.ReadAllBytes(Path.Combine(directoryPath, "user.config"));
            await settingDialog.UninstallLr2PlayHistorySchemaAsync();
            Assert.AreEqual(dialogs.AcceptUninstall ? values.LR2ConfigXmlPath : candidate, settingDialog.LR2ConfigXmlPath);
            Assert.IsTrue(settingDialog.EnableBeatorajaBmtOutput);
            StringAssert.Contains(dialogs.LastCandidateDiscardNotice, Resources.Settings_lr2_detailed_paths);
            CollectionAssert.AreEqual(candidateBefore, File.ReadAllBytes(candidateDb));
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(Path.Combine(directoryPath, "user.config")));

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using SQLiteConnectionEx verify = MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope.OpenProviderConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            Assert.AreEqual(1, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'trigger' AND name = ?;",
                Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private string StageDifferentSchemaCandidate(SettingsDialogViewModel dialog, string root)
    {
        string draft = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "candidate")).FullName, "config.xml");
        File.WriteAllText(draft, "<config><system/><jukebox/><player><id>player2</id></player></config>");
        CreateInstalledScoreDb(Path.Combine(root, "LR2files", "Database", "Score", "player2.db"));
        dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), draft).GetAwaiter().GetResult();
        Assert.AreEqual(draft, dialog.LR2ConfigXmlPath, dialog.Lr2PathSelectionError);
        dialog.EnableBeatorajaBmtOutput = true;
        return draft;
    }

    private static Settings CreateConfirmedSchemaSettings(string root)
    {
        string config = Path.Combine(root, "LR2files", "Config", "config.xml");
        Directory.CreateDirectory(Path.Combine(root, "LR2files", "Config"));
        Directory.CreateDirectory(Path.Combine(root, "LR2files", "Database", "Score"));
        File.WriteAllText(config, "<config><system/><jukebox/><player><id>player1</id></player></config>");
        Settings values = TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config"));
        values.OperationModeLR2DB = true;
        values.LR2RootPath = root;
        values.LR2ConfigXmlPath = config;
        values.LR2SongDBPath = Path.Combine(root, "song.db");
        values.Save();
        return values;
    }

    private void CreateInstalledScoreDb(string scoreDbPath)
    {
        using (SQLiteConnectionEx db = databaseScope.OpenProviderConnection(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
        }
        new Lr2PlayHistorySchemaService(databaseScope).InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private sealed class RecordingUiDialogService : IUiDialogService, ILr2PlayHistorySchemaUninstallDialogPort
    {
        internal int MessageCount { get; private set; }

        internal int ConfirmationCount { get; private set; }

        internal int WindowCount { get; private set; }

        internal bool AcceptUninstall { get; set; }
        internal bool AcceptConfirmation { get; set; }
        internal string LastConfirmation { get; private set; } = string.Empty;
        internal string LastCandidateDiscardNotice { get; private set; } = string.Empty;

        internal Lr2PlayHistorySchemaUninstallMode SelectedUninstallMode { get; set; }

        internal string LastMessage { get; private set; } = string.Empty;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCount++;
            LastMessage = request.MessageBoxText;
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            LastConfirmation = request.MessageBoxText;
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(AcceptConfirmation ? MessageBoxResult.OK : MessageBoxResult.Cancel));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            WindowCount++;
            if (!AcceptUninstall)
            {
                return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.CancelledByUser));
            }
            return Task.FromResult(new UiWindowDialogResult<TResult>(
                UiDialogStatus.Accepted,
                (TResult)(object)SelectedUninstallMode,
                dialogResult: true));
        }

        public Task<UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>> ShowAsync(
            string scoreDbPath,
            CancellationToken cancellationToken = default, string? candidateDiscardNotice = null)
        {
            LastCandidateDiscardNotice = candidateDiscardNotice ?? string.Empty;
            WindowCount++;
            if (!AcceptUninstall)
            {
                return Task.FromResult(new UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>(UiInteractionStatus.CancelledByUser));
            }
            return Task.FromResult(new UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>(
                UiInteractionStatus.Accepted,
                SelectedUninstallMode,
                error: null));
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiFilePickerResult(UiDialogStatus.CancelledByUser));
        }

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiFolderPickerResult(UiDialogStatus.CancelledByUser));
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser));
        }

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiProgressResult(UiDialogStatus.CancelledByUser));
        }
    }

    private sealed class RecordingPlayHistoryPort : ISettingsDialogPlayHistoryPort
    {

        public void RefreshDisplayTargetSetsFromSettings(
            string serializedDisplayTargetSets,
            bool queueRefreshWhenSelectionChanges, string? previousName = null, string? confirmedName = null)
        {
        }
    }

}
