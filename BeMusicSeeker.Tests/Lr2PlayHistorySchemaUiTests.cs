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
using BeMusicSeeker.Properties;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
// These tests use WPF dispatcher state and the process-wide Settings.Default instance.
[DoNotParallelize]
public sealed class Lr2PlayHistorySchemaUiTests
{
    [TestMethod]
    public void SettingDialogSchemaStatusReceipt_FiltersPathAndClearsOnResetAndDispose()
    {
        var owner = MainWindowViewModelTestFactory.Create();
        var statePort = new TestSettingsDialogStatePort(
            owner,
            () => Task.FromResult(StartupInitializationOutcome.Succeeded));
        var settingDialog = new SettingsDialogViewModel(
            statePort,
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            new RecordingPlayHistoryPort(),
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
            owner.PlaybackPanel,
            owner.Lr2SongDbSyncWorkflow,
            settingsEditSession: SettingsEditSession.CreateDefault(),
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

        Lr2PlayHistorySchemaStatusSnapshot matchingSnapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(
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
        statePort.NotifyLr2PlayHistorySchemaStatusChanged(
            Lr2PlayHistorySchemaStatusSnapshot.FromResult(
                new Lr2PlayHistorySchemaCheckResult
                {
                    Status = Lr2PlayHistorySchemaStatus.Repairable,
                    ScoreDbPath = scoreDbPath,
                    Message = "non-lr2"
                }));
        Assert.AreSame(matchingSnapshot, settingDialog.Lr2PlayHistorySchemaStatusSnapshot);
        SetPrivateField(settingDialog, "operationModeLR2DB", true);

        statePort.NotifyLr2PlayHistorySchemaStatusChanged(Lr2PlayHistorySchemaStatusSnapshot.Reset);
        Assert.IsNull(settingDialog.Lr2PlayHistorySchemaStatusSnapshot);

        settingDialog.Dispose();
        statePort.NotifyLr2PlayHistorySchemaStatusChanged(matchingSnapshot);
        Assert.IsNull(settingDialog.Lr2PlayHistorySchemaStatusSnapshot);
    }

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

    [TestMethod]
    public async Task SettingDialogViewModel_InstallOrRepair_BlockedOperationUsesOwnerDialog()
    {
        var owner = MainWindowViewModelTestFactory.Create();
        var dialogs = new RecordingUiDialogService();
        var settingDialog = new SettingsDialogViewModel(
            owner,
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
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
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
        owner.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
        try
        {
            await settingDialog.InstallOrRepairLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            StringAssert.Contains(dialogs.LastMessage, Resources.Msg_settings_apply_blocked_during_initialization);
        }
        finally
        {
            owner.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
        }
    }

    [TestMethod]
    public async Task SettingDialogViewModel_Uninstall_TriggersOnlyPublishesStateAndReloads()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string scoreDbPath = Path.Combine(directoryPath, "score.db");
            CreateInstalledScoreDb(scoreDbPath);
            var owner = MainWindowViewModelTestFactory.Create();
            SetPrivateField(owner, "hasActiveLibraryProfile", true);
            var dialogs = new RecordingUiDialogService
            {
                AcceptUninstall = true,
                SelectedUninstallMode = Lr2PlayHistorySchemaUninstallMode.TriggersOnly
            };
            int reloadCount = 0;
            var statePort = new TestSettingsDialogStatePort(
                owner,
                () => Task.FromResult(StartupInitializationOutcome.Succeeded),
                reloadScoresOnly: () =>
                {
                    reloadCount++;
                    return Task.CompletedTask;
                });
            var playHistory = new RecordingPlayHistoryPort();
            var settingDialog = new SettingsDialogViewModel(
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
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
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
            SetPrivateField(settingDialog, "operationModeLR2DB", true);
            SetPrivateField(settingDialog, "lr2PlayHistoryScoreDbPath", scoreDbPath);

            await settingDialog.UninstallLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(Resources.Msg_success_lr2_play_history_schema_uninstall, dialogs.LastMessage);
            Assert.AreEqual("lr2_play_history_schema_uninstall", playHistory.LastInvalidationReason);
            Assert.AreEqual(1, playHistory.InvalidationCount);
            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using var verify = new SQLiteConnection(scoreDbPath);
            Assert.AreEqual(1, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;",
                Lr2PlayHistorySchemaService.PlayHistoryTableName));
            Assert.AreEqual(0, verify.ExecuteScalar<int>(
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

    [TestMethod]
    public async Task SettingDialogViewModel_Uninstall_TablesAndTriggersRemovesObjects()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string scoreDbPath = Path.Combine(directoryPath, "score.db");
            CreateInstalledScoreDb(scoreDbPath);
            var owner = MainWindowViewModelTestFactory.Create();
            var dialogs = new RecordingUiDialogService
            {
                AcceptUninstall = true,
                SelectedUninstallMode = Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers
            };
            int reloadCount = 0;
            var statePort = new TestSettingsDialogStatePort(
                owner,
                () => Task.FromResult(StartupInitializationOutcome.Succeeded),
                reloadScoresOnly: () =>
                {
                    reloadCount++;
                    return Task.CompletedTask;
                });
            var playHistory = new RecordingPlayHistoryPort();
            var settingDialog = new SettingsDialogViewModel(
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
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
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
            SetPrivateField(settingDialog, "operationModeLR2DB", true);
            SetPrivateField(settingDialog, "lr2PlayHistoryScoreDbPath", scoreDbPath);

            await settingDialog.UninstallLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(Resources.Msg_success_lr2_play_history_schema_uninstall, dialogs.LastMessage);
            Assert.AreEqual("lr2_play_history_schema_uninstall", playHistory.LastInvalidationReason);
            Assert.AreEqual(1, playHistory.InvalidationCount);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using var verify = new SQLiteConnection(scoreDbPath);
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

    [TestMethod]
    public async Task SettingDialogViewModel_Uninstall_CancelLeavesDurableAndLiveStateUnchanged()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string scoreDbPath = Path.Combine(directoryPath, "score.db");
            CreateInstalledScoreDb(scoreDbPath);
            var owner = MainWindowViewModelTestFactory.Create();
            var dialogs = new RecordingUiDialogService { AcceptUninstall = false };
            int reloadCount = 0;
            var statePort = new TestSettingsDialogStatePort(
                owner,
                () => Task.FromResult(StartupInitializationOutcome.Succeeded),
                reloadScoresOnly: () =>
                {
                    reloadCount++;
                    return Task.CompletedTask;
                });
            var playHistory = new RecordingPlayHistoryPort();
            var settingDialog = new SettingsDialogViewModel(
            statePort,
                owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            playHistory,
            owner.LibraryFolderTree,
            new ApplicationComposition(uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog()),
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
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
            SetPrivateField(settingDialog, "operationModeLR2DB", true);
            SetPrivateField(settingDialog, "lr2PlayHistoryScoreDbPath", scoreDbPath);

            await settingDialog.UninstallLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(0, playHistory.InvalidationCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, settingDialog.Lr2PlayHistorySchemaStatusSnapshot.Status);
            using var verify = new SQLiteConnection(scoreDbPath);
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
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
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
            CancellationToken cancellationToken = default)
        {
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
