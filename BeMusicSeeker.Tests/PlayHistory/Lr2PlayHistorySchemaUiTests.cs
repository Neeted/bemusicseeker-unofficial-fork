using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2PlayHistorySchemaUiTests
{
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
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(value => value.OperationModeLR2DB = true);
        var settingsSession = new SchemaSettingsSession(values);
        var statePort = new TestSettingsDialogStatePort(
            owner,
            () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)));
        using var settingDialog = new SettingsDialogViewModel(
            statePort,
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            new RecordingPlayHistoryPort(),
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsEditSession: settingsSession,
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
        const string scoreDbPath = "C:\\lr2\\score.db";
        SetPrivateField(settingDialog, "operationModeLR2DB", true);
        SetPrivateField(settingDialog, "lr2PlayHistoryScoreDbPath", scoreDbPath);

        var matchingSnapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(
            new Lr2PlayHistorySchemaCheckResult
            {
                Status = Lr2PlayHistorySchemaStatus.Installed,
                ScoreDbPath = scoreDbPath,
                Message = "matching"
            });
        statePort.NotifyLr2PlayHistorySchemaStatusChanged(matchingSnapshot);
        Assert.AreSame(matchingSnapshot, settingDialog.Lr2PlayHistorySchemaStatusSnapshot);

        statePort.NotifyLr2PlayHistorySchemaStatusChanged(
            Lr2PlayHistorySchemaStatusSnapshot.FromResult(
                new Lr2PlayHistorySchemaCheckResult
                {
                    Status = Lr2PlayHistorySchemaStatus.Repairable,
                    ScoreDbPath = "C:\\other\\score.db",
                    Message = "stale"
                }));
        Assert.AreSame(matchingSnapshot, settingDialog.Lr2PlayHistorySchemaStatusSnapshot);

        SetPrivateField(settingDialog, "operationModeLR2DB", false);
        var candidateUnchangedSnapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(new Lr2PlayHistorySchemaCheckResult
        {
            Status = Lr2PlayHistorySchemaStatus.Repairable,
            ScoreDbPath = scoreDbPath,
            Message = "confirmed LR2 despite candidate mode"
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
                    Message = "non-lr2"
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
    /// LR2標準のplayer DB位置を組み立て、実在時だけ返す。未設定・不在・設定読取失敗を存在するDBへ置き換えない。
    /// </summary>
    [TestMethod]
    public void Lr2ScoreDbPathResolver_ResolvesExistingPlayerScoreDbOnly()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2ScoreDbPathResolver_" + Guid.NewGuid().ToString("N"));
        try
        {
            string scoreDirectoryPath = Path.Combine(directoryPath, "LR2files", "Database", "Score");
            Directory.CreateDirectory(scoreDirectoryPath);
            string scoreDbPath = Path.Combine(scoreDirectoryPath, "player1.db");
            File.WriteAllText(scoreDbPath, string.Empty);

            Assert.AreEqual(scoreDbPath, Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(directoryPath, "player1"));
            Assert.AreEqual(Path.Combine(scoreDirectoryPath, "missing.db"), Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(directoryPath, "missing"));
            Assert.AreEqual(scoreDbPath, Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, "player1"));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, "missing"));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, (string)null!));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, () => throw new InvalidOperationException("config")));
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
    /// 未導入だけ導入、修復可能だけ修復を許可し、導入済み/手動修復/読取不能/対象外は両操作を禁止して状態文言を渡す。
    /// </summary>
    [TestMethod]
    public void Lr2PlayHistorySchemaStatusPresentation_MapsActionsAndLabels()
    {
        Lr2PlayHistorySchemaStatusPresentation notInstalled = CreatePresentation(Lr2PlayHistorySchemaStatus.NotInstalled);
        Assert.IsTrue(notInstalled.CanInstall);
        Assert.IsFalse(notInstalled.CanRepair);
        Assert.AreEqual(Resources.Lr2_play_history_schema_status_not_installed, notInstalled.StatusText);

        Lr2PlayHistorySchemaStatusPresentation repairable = CreatePresentation(Lr2PlayHistorySchemaStatus.Repairable);
        Assert.IsFalse(repairable.CanInstall);
        Assert.IsTrue(repairable.CanRepair);
        Assert.AreEqual(Resources.Lr2_play_history_schema_status_repairable, repairable.StatusText);

        foreach (Lr2PlayHistorySchemaStatus status in new[]
        {
            Lr2PlayHistorySchemaStatus.Installed,
            Lr2PlayHistorySchemaStatus.ManualRepairRequired,
            Lr2PlayHistorySchemaStatus.Unreadable,
            Lr2PlayHistorySchemaStatus.SkippedProfile
        })
        {
            Lr2PlayHistorySchemaStatusPresentation presentation = CreatePresentation(status);
            Assert.IsFalse(presentation.CanInstall, status.ToString());
            Assert.IsFalse(presentation.CanRepair, status.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(presentation.StatusText), status.ToString());
        }
    }

    /// <summary>
    /// 初期化中・ライブラリ変更中・playlist変更中は導入確認を出さず受付警告を返す。片側取得だけに成功した排他権も解放する。
    /// </summary>
    /// <remarks>実設定画面VMと構成を使い、受付/状態購読と機能窓口の接続を確認する。共有設定のedit sessionを使うため並列実行しない。</remarks>
    [DoNotParallelize]
    [DataTestMethod]
    [DataRow("initializing")]
    [DataRow("library")]
    [DataRow("playlist")]
    public async Task SettingDialogViewModel_InstallOrRepair_BlockedOperationUsesOwnerDialog(string blockedDomain)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        using SettingsDialogViewModel ownerSettingsLifetime = owner.SettingDialog;
        ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
        var dialogs = new RecordingUiDialogService();
        using var settingDialog = new SettingsDialogViewModel(
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
            operationAdmission: composition.OperationAdmission);
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
    [DataTestMethod]
    [DataRow("cancel")]
    [DataRow("library-busy")]
    [DataRow("playlist-busy")]
    [DataRow("invalid-db")]
    [DataRow("accepted")]
    public void SettingDialogViewModel_InstallUsesConfirmedPlayerAndPreservesCandidatesUntilAccepted(string outcome)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "Lr2SchemaConfirmed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindowViewModel? owner = null;
            SettingsDialogViewModel? dialog = null;
            IDisposable? held = null;
            try
            {
                Settings values = CreateConfirmedSchemaSettings(root);
                string score = Path.Combine(root, "LR2files", "Database", "Score", "player1.db");
                if (outcome == "invalid-db") { File.WriteAllText(score, "invalid SQLite"); }
                else { using var db = new SQLiteConnection(score); db.CreateTable<LR2ScoreDB.score>(); db.CreateTable<LR2ScoreDB.player>(); }
                owner = MainWindowViewModelTestFactory.Create();
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
                var dialogs = new RecordingUiDialogService { AcceptConfirmation = outcome == "accepted" };
                var history = new RecordingPlayHistoryPort();
                var state = new TestSettingsDialogStatePort(owner, () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded)));
                dialog = new SettingsDialogViewModel(state, owner.PlaylistWorkspace, owner.PlaylistWorkspace, history,
                    owner.LibraryFolderTree, composition, owner.PlaybackPanel, owner.Lr2SongDbSyncWorkflow,
                    settingsEditSession: new SettingsEditSession(values), applicationLifetime: TestApplicationContext.CreateLifetime(),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), schemaDialogs: dialogs, schemaWindowDialogs: dialogs,
                    externalShellGateway: ExternalShellGatewayPolicy.Current, applicationPathSnapshot: ApplicationPathPolicy.Current,
                    audioDeviceCatalog: new TestAudioDeviceCatalog(), audioSettingsGateway: new TestAudioSettingsGateway(),
                    audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(), operationAdmission: composition.OperationAdmission);
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
                    Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, new Lr2PlayHistorySchemaService().Check(score, true).Status);
                    Assert.AreEqual(1, history.InvalidationCount);
                }
                else { CollectionAssert.AreEqual(sourceBefore, File.ReadAllBytes(score)); Assert.AreEqual(0, history.InvalidationCount); }
                held?.Dispose(); held = null;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(outcome == "accepted" ? 1 : 0, history.InvalidationCount, "受付解放だけで自動再操作しません。");
            }
            finally { held?.Dispose(); dialog?.Dispose(); owner?.SettingDialog.Dispose(); Directory.Delete(root, recursive: true); }
        });
    }

    /// <summary>
    /// 稼働profileの導入・修復・両削除は実DB操作後に成績と履歴を再取得し、取得Taskの終端まで設定操作を完了しない。
    /// </summary>
    /// <remarks>実設定画面VMと構成を使い、受付/状態購読と機能窓口の接続を確認する。共有設定のedit sessionを使うため並列実行しない。</remarks>
    [DoNotParallelize]
    [DataTestMethod]
    [DataRow("install")]
    [DataRow("repair")]
    [DataRow("triggers")]
    [DataRow("tables")]
    public async Task SettingDialogViewModel_SchemaChangeReloadsCapturedBatchAndWaitsForTerminal(string operation)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            Settings values = CreateConfirmedSchemaSettings(directoryPath);
            string scoreDbPath = Path.Combine(directoryPath, "LR2files", "Database", "Score", "player1.db");
            CreateInstalledScoreDb(scoreDbPath);
            bool installing = operation is "install" or "repair";
            if (operation == "install") { new Lr2PlayHistorySchemaService().Uninstall(scoreDbPath, true, Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers); }
            if (operation == "repair")
            {
                using var db = new SQLiteConnection(scoreDbPath);
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
                        new BmsLibraryDbGateway(values.LR2SongDBPath, scoreDbPath), new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true }).PlayHistorySnapshot;
                });
            var playHistory = new RecordingPlayHistoryPort();
            using var settingDialog = new SettingsDialogViewModel(
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
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
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());


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
            Assert.AreEqual(installing ? "lr2_play_history_schema_install_or_repair" : "lr2_play_history_schema_uninstall", playHistory.LastInvalidationReason);
            Assert.AreEqual(1, playHistory.InvalidationCount);
            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual(expectedStatus, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
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
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
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
            using var settingDialog = new SettingsDialogViewModel(
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
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
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());


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
            Assert.AreEqual("lr2_play_history_schema_uninstall", playHistory.LastInvalidationReason);
            Assert.AreEqual(1, playHistory.InvalidationCount);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
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
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
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
            using var settingDialog = new SettingsDialogViewModel(
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
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
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());


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
            Assert.AreEqual(0, playHistory.InvalidationCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
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

    private static Lr2PlayHistorySchemaStatusPresentation CreatePresentation(Lr2PlayHistorySchemaStatus status)
    {
        return Lr2PlayHistorySchemaStatusPresentation.Create(
            Lr2PlayHistorySchemaStatusSnapshot.FromResult(new Lr2PlayHistorySchemaCheckResult
            {
                Status = status,
                Message = status.ToString()
            }));
    }

    private static string StageDifferentSchemaCandidate(SettingsDialogViewModel dialog, string root)
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
        Settings values = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config"));
        values.OperationModeLR2DB = true;
        values.LR2RootPath = root;
        values.LR2ConfigXmlPath = config;
        values.LR2SongDBPath = Path.Combine(root, "song.db");
        values.Save();
        return values;
    }

    private static void CreateInstalledScoreDb(string scoreDbPath)
    {
        using (var db = new SQLiteConnection(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
        }
        new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
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
        internal int InvalidationCount { get; private set; }

        internal string? LastInvalidationReason { get; private set; }

        public void InvalidateReadCache(string reason)
        {
            InvalidationCount++;
            LastInvalidationReason = reason;
        }

        public void RefreshDisplayTargetCatalog(bool queueRefreshWhenSelectionChanges = true)
        {
        }

        public void RefreshDisplayTargetSetsFromSettings(
            string serializedDisplayTargetSets,
            bool queueRefreshWhenSelectionChanges)
        {
        }
    }

}
