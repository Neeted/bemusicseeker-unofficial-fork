using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
    /// <summary>実ownerは設定担当から準備中・必須UI待ち・局所BMT待ちのTaskを直接取得し、実終端前に保存・runtime終了へ進みません。</summary>
    /// <remarks>実MainWindow・モーダル・compiled開始/BMTボタンとCloseを使用します。BMT物理媒体の整合は局所BMT系列が分担し、ここは実P内のport終端を保持します。</remarks>
    [DataTestMethod]
    [DataRow("preparation")]
    [DataRow("required-ui")]
    [DataRow("local-bmt")]
    public void MainWindow_OwnerShutdownWaitsForAcceptedSettingsOperationBeforeRuntimeShutdown(string boundary)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lifetime = new SettingsOperationLifetime();
            Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")));
            string validSong = values.LR2SongDBPath;
            string oldBeatoraja = Path.Combine(root, "old-beatoraja");
            WriteBeatorajaRoot(oldBeatoraja, "old", ["old"], createScoreFiles: false);
            values.BeatorajaRootPath = oldBeatoraja;
            values.BeatorajaPlayerId = "old";
            values.BeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(oldBeatoraja, "old");
            values.BeatorajaBmtTablePath = BeatorajaConfigService.GetTablePath(oldBeatoraja);
            values.EnableBeatorajaBmtOutput = boundary == "local-bmt";
            // 実activationを案内まで終え、後の設定モーダルは同じownerが表示します。
            values.LR2SongDBPath = string.Empty;
            values.Save();
            var session = new CountingSettingsEditSession(values) { PersistOwnedValues = true };
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No) };
            var ui = new MainWindowViewModelStartupProgressTests.CompletionUiScheduler(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            var failures = new List<Exception>();
            var composition = new ApplicationComposition(settingsEditSession: session, uiScheduler: ui, applicationLifetime: lifetime,
                reportSettingsApplyFailure: failures.Add, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                settingsDialogService: dialogs, fileDbMutationDialogService: dialogs, applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                rootFileEnumerator: new FastRootFileEnumerator(), audioDeviceCatalog: new TestAudioDeviceCatalog());
            MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
            Window? previousMain = Application.Current.MainWindow;
            MainWindow? main = null;
            SettingsWindow? settingsWindow = null;
            Task? operation = null;
            Task? interaction = null;
            var ownership = new MainWindowTestLifetime(owner, lifetime.ShutdownRequested.Task);
            Exception? bodyFailure = null;
            try
            {
                owner.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellActivationWorkflow.ActivateRenderedShell(() => { }, _ => { }, () => false), "settings-operation-initial-guidance");
                values.Commit((nameof(Settings.LR2SongDBPath), validSong));
                if (boundary == "preparation")
                {
                    new BmsLibraryDbGateway(validSong).EnsureAppOwnedSchema();
                    string candidate = Path.Combine(root, "LeapCandidate");
                    Directory.CreateDirectory(candidate);
                    Directory.SetLastWriteTime(candidate, new DateTime(2024, 2, 29, 12, 0, 0));
                    using var db = new LR2SongDBExtended(validSong);
                    db.CreateTable<LR2SongDB.song>(); db.CreateTable<LR2SongDB.folder>();
                    db.Insert(new LR2SongDB.folder { path = candidate + Path.DirectorySeparatorChar, type = 1, adddate = 0 });
                    dialogs.ConfirmationObservedAsync = () => { entered.TrySetResult(); return release.Task; };
                }
                if (boundary == "required-ui")
                {
                    ui.IsCompletionBoundary = () => !composition.OperationAdmission.IsActive && !composition.PlaylistOperationAdmission.IsActive
                        && owner.IsLibraryOperationInProgress && !owner.IsInitializationCompleted;
                    ui.Arm();
                }
                if (boundary == "local-bmt")
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.InitializeAsync(), "settings-operation-bmt-initialization");
                    AwaitStartupBackgroundCompletionOnDispatcher(owner, "settings-operation-bmt-background-terminal");
                    owner.SettingDialog.Dispose();
                    var workspace = new SettingsOperationWorkspace(() => { entered.TrySetResult(); return release.Task; });
                    var dialog = new SettingsDialogViewModel(owner, workspace, owner.PlaylistWorkspace, owner.PlayHistory, owner.LibraryFolderTree,
                        composition, owner.PlaybackPanel, owner.Lr2SongDbSyncWorkflow, session, reportApplyFailure: failures.Add,
                        applicationLifetime: lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                        externalShellGateway: ExternalShellGatewayPolicy.Current, applicationPathSnapshot: ApplicationPathPolicy.Current,
                        audioDeviceCatalog: new TestAudioDeviceCatalog(), audioSettingsGateway: new SettingsAudioGateway(() => values),
                        audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
                    PropertyInfo settingDialogProperty = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SettingDialog))
                        ?? throw new AssertFailedException("設定ViewModelの接続プロパティがありません。");
                    settingDialogProperty.SetValue(owner, dialog);
                }
                BassAudioRuntime.Initialize();
                main = ownership.CreateWindow(() => new MainWindow(owner, created =>
                {
                    settingsWindow = created;
                    scope.PrepareForOwnedPresentation(created);
                    created.ContentRendered += (_, _) => created.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                        (Action)(() => interaction = ExerciseAsync(created)));
                }));
                scope.PrepareForOwnedPresentation(main);
                Application.Current.MainWindow = main;
                main.Show(); main.UpdateLayout();
                owner.SettingDialog.OpenCommand.Execute();
                Assert.IsNotNull(interaction);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(interaction, "settings-operation-owner-interaction");

                async Task ExerciseAsync(SettingsWindow presented)
                {
                    MainWindow shell = main ?? throw new AssertFailedException("ownerの表示前に設定操作を開始しました。");
                    try
                    {
                        if (boundary == "local-bmt")
                        {
                            FindDescendants<Button>(presented).Single(button => Equals(button.Tag, "confirm:bmt"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        else { ((Button)presented.FindName("buttonOK")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                        operation = owner.SettingDialog.SettingsOperationCompletionTask;
                        Task arrival = boundary == "required-ui" ? ui.Entered.Task : entered.Task;
                        await TestUiDispatcherHost.AwaitNotificationAsync(arrival, operation, "accepted-settings-operation-boundary");
                        Assert.IsFalse(operation.IsCompleted);
                        shell.Close();
                        await Task.Yield();
                        Assert.IsFalse(presented.IsVisible);
                        Assert.IsNull(presented.DataContext);
                        FieldInfo settingsWindowField = typeof(MainWindow).GetField("settingsWindow", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? throw new AssertFailedException("設定Windowの所有フィールドがありません。");
                        Assert.IsNull(settingsWindowField.GetValue(shell));
                        Assert.IsFalse(operation.IsCompleted);
                        Assert.IsFalse(shell.CloseCompletion.IsCompleted);
                        Assert.AreEqual(0, session.SaveCount);
                        Assert.IsFalse(lifetime.ShutdownRequested.Task.IsCompleted);
                        Assert.IsFalse(BassAudioRuntime.OperationGate.AdmissionClosed);
                        release.TrySetResult(); ui.Release.TrySetResult();
                        await operation;
                        await shell.CloseCompletion;
                        Assert.IsTrue(lifetime.ShutdownRequested.Task.IsCompleted);
                        Assert.IsTrue(session.SaveCount > 0);
                    }
                    finally { release.TrySetResult(); ui.Release.TrySetResult(); }
                }
            }
            catch (Exception failure) { bodyFailure = failure; throw; }
            finally
            {
                release.TrySetResult(); ui.Release.TrySetResult();
                try
                {
                    if (operation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "settings-operation-terminal-cleanup"); }
                    if (interaction != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(interaction, "settings-operation-interaction-cleanup"); }
                }
                finally
                {
                    settingsWindow?.CloseForOwnerShutdown();
                    ownership.DisposeAfterBodyFailure(bodyFailure);
                    Application.Current.MainWindow = previousMain;
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    /// <summary>手動同期の実受付でSettingsモーダルだけが先に閉じても、MainWindowは引き継いだ実Taskの終端まで終了・保存・runtime終了を待ちます。</summary>
    /// <remarks>実MainWindow・実モーダル・実ボタン/確認・通常owner/実LR2受付を接続します。実同期完了後のruntime返却だけを保持し、library自身の終了待ちによってSettings Taskの引継ぎ欠落が隠れない条件にします。</remarks>
    [TestMethod]
    public void MainWindow_ManualResyncModalClosesBeforeTaskAndShellShutdownJoinsTransferredOperation()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var lifetime = new SettingsOperationLifetime();
            CountingSettingsEditSession? session = null;
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture(lr2Mode: true, lifetime: lifetime, rootFileEnumerator: new FastRootFileEnumerator(),
                settingsSessionFactory: values =>
                {
                    values.Save();
                    return session = new CountingSettingsEditSession(values) { PersistOwnedValues = true };
                });
            CountingSettingsEditSession settingsSession = session ?? throw new AssertFailedException("設定sessionが構成されていません。");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var actualRuntime = new BmsLr2SongDbSyncWorkflowRuntime(() => fixture.Library, () => fixture.Playlist, () => fixture.Settings.OperationModeLR2DB);
            var terminalRuntime = new SettingsOperationTerminalRuntime(actualRuntime, entered, release.Task);
            MainWindowViewModel owner = fixture.Owner;
            Window? previousMain = Application.Current.MainWindow;
            SettingsWindow? settingsWindow = null;
            Window? confirmationWindow = null;
            Exception? confirmationFailure = null;
            Task? operation = null;
            Exception? bodyFailure = null;
            var ownership = new MainWindowTestLifetime(owner, lifetime.ShutdownRequested.Task);
            try
            {
                Assert.IsTrue(owner.SettingDialog.CheckValidationForInitialization(out string validationFailure), validationFailure);
                fixture.OptionalRelease.TrySetResult();
                Task initialization = owner.ShellActivationWorkflow.ActivateRenderedShell(() => { }, _ => { }, () => false);
                fixture.Track(initialization);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "manual-resync-shell-initialization");
                Assert.IsTrue(owner.IsInitializationCompleted && owner.HasActiveLibraryProfile);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "manual-resync-shell-background-terminal");
                owner.SettingDialog.Dispose();
                var dialog = new SettingsDialogViewModel(owner, owner.PlaylistWorkspace, owner.PlaylistWorkspace, owner.PlayHistory, owner.LibraryFolderTree,
                    fixture.Composition, owner.PlaybackPanel, new Lr2SongDbSyncWorkflowOwner(terminalRuntime), settingsSession,
                    applicationLifetime: lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    externalShellGateway: ExternalShellGatewayPolicy.Current, applicationPathSnapshot: fixture.Composition.ApplicationPathSnapshot,
                    audioDeviceCatalog: new TestAudioDeviceCatalog(), audioSettingsGateway: new SettingsAudioGateway(() => fixture.Settings),
                    audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
                PropertyInfo settingDialogProperty = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SettingDialog))
                    ?? throw new AssertFailedException("設定ViewModelの接続プロパティがありません。");
                settingDialogProperty.SetValue(owner, dialog);
                BassAudioRuntime.Initialize();
                MainWindow main = ownership.CreateWindow(() => new MainWindow(owner, created =>
                {
                    settingsWindow = created;
                    scope.PrepareForOwnedPresentation(created);
                    created.ContentRendered += (_, _) => created.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                    {
                        // 実確認のモーダルloopで既存OKボタンを承認します。物理入力やフォーカスは使いません。
                        created.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                        {
                            try
                            {
                                confirmationWindow = Application.Current.Windows.OfType<Window>()
                                    .Single(window => window.IsVisible && ReferenceEquals(window.Owner, created));
                                FindDescendants<Button>(confirmationWindow).Single(button => Equals(button.Content, "OK"))
                                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            catch (Exception failure) { confirmationFailure = failure; confirmationWindow?.Close(); created.CloseForOwnerShutdown(); }
                        }));
                        FindDescendants<Button>(created).Single(button => Equals(button.Content, Resources.Lr2_song_db_sync_data_resync))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        operation = owner.SettingDialog.SettingsOperationCompletionTask;
                    }));
                }));
                scope.PrepareForOwnedPresentation(main);
                Application.Current.MainWindow = main;
                main.Show(); main.UpdateLayout();
                int savesBefore = settingsSession.SaveCount;
                owner.SettingDialog.OpenCommand.Execute();
                // Executeの復帰はShowDialogとMainWindowのfinallyを通過した後です。この後で初めてShellを閉じます。
                Assert.IsNull(confirmationFailure);
                Assert.IsNotNull(confirmationWindow);
                Assert.IsNotNull(settingsWindow);
                Assert.IsNotNull(operation);
                Assert.AreEqual(SettingsWindowCloseReason.ManualResync, settingsWindow.CloseReason);
                Assert.IsFalse(settingsWindow.IsVisible);
                Assert.IsNull(settingsWindow.DataContext);
                Assert.AreSame(operation, owner.SettingDialog.SettingsOperationCompletionTask);
                Assert.IsFalse(owner.SettingDialog.IsSettingsOperationAvailable);
                Assert.IsTrue(owner.SettingDialog.IsEditCancellationEnabled, "自動閉鎖後も同じTaskを保持し、通常の再表示・閉鎖は実操作の終端と分けます。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation,
                    "manual-resync-runtime-return-gate"), "manual-resync-runtime-return-gate");
                Assert.IsTrue(terminalRuntime.WasAccepted);
                Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
                Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
                Assert.IsFalse(operation.IsCompleted);
                main.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "manual-resync-shell-preparation-terminal");
                Assert.IsTrue(owner.ShellShutdownWorkflow.IsShutdownPrepared);
                // UI上の同期呼出しだけでは終了用Dispatcher操作がまだ始まらないため、実際に配送を進めます。
                TestUiDispatcherHost.AwaitTaskOnDispatcher(main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task,
                    "manual-resync-shell-terminal-dispatch");
                Assert.IsFalse(main.CloseCompletion.IsCompleted);
                Assert.IsFalse(lifetime.ShutdownRequested.Task.IsCompleted);
                Assert.IsFalse(BassAudioRuntime.OperationGate.AdmissionClosed);
                Assert.AreEqual(savesBefore, settingsSession.SaveCount);
                release.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "manual-resync-transferred-task-terminal");
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(main.CloseCompletion, "manual-resync-shell-terminal");
                Assert.IsTrue(lifetime.ShutdownRequested.Task.IsCompleted);
                Assert.IsTrue(settingsSession.SaveCount > savesBefore);
            }
            catch (Exception failure) { bodyFailure = failure; throw; }
            finally
            {
                release.TrySetResult(); fixture.OptionalRelease.TrySetResult();
                try
                {
                    if (confirmationWindow?.IsVisible == true) { confirmationWindow.Close(); }
                    if (operation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "manual-resync-shell-cleanup-operation"); }
                }
                finally
                {
                    settingsWindow?.CloseForOwnerShutdown();
                    ownership.DisposeAfterBodyFailure(bodyFailure);
                    Application.Current.MainWindow = previousMain;
                }
            }
        });
    }

    /// <summary>実LR2処理と両受付の解放を終えた後の返却Taskだけを保持し、Shellが独立に所有するSettings Taskの待機を識別します。</summary>
    private sealed class SettingsOperationTerminalRuntime(ILr2SongDbSyncWorkflowRuntime actual, TaskCompletionSource entered, Task release) : ILr2SongDbSyncWorkflowRuntime
    {
        public bool IsLr2ModeEnabled => actual.IsLr2ModeEnabled;
        public bool IsLibraryAvailable => actual.IsLibraryAvailable;
        internal bool WasAccepted { get; private set; }
        public async Task<bool> QueueAsync(string reason, bool force, bool prepareGeneratedData = false, bool allowIncompleteToQueue = true,
            LibraryFileInitializationResult? initializationResult = null, LibraryFileMutationCapability? capability = null,
            bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null,
            Lr2SongDbSyncPreparedDataSurface? preparedSurface = null, OperationProgressRequest? originatingRequest = null,
            BmsLibraryOptionsSnapshot? optionsSnapshot = null, Action<bool>? admissionResult = null)
        {
            bool accepted = await actual.QueueAsync(reason, force, prepareGeneratedData, allowIncompleteToQueue, initializationResult,
                capability, acceptedBackground, includeBuiltinGeneratedData, playlistCapability, preparedSurface, originatingRequest,
                optionsSnapshot, value => { WasAccepted = value; admissionResult?.Invoke(value); });
            if (accepted) { entered.TrySetResult(); await release; }
            return accepted;
        }
        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason,
            LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
            => actual.SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(reason, capability, playlistCapability);
    }

    private sealed class SettingsOperationLifetime : IApplicationLifetimePort
    {
        internal TaskCompletionSource ShutdownRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsFirstStartup => false;
        public void CompleteFirstStartup() { }
        public void MarkCoordinatedShutdownStarted(string reason) { }
        public void ExitForUnrestoredDestination(Exception failure) => throw new InvalidOperationException("Unexpected exit", failure);
        public void RequestShutdown() => ShutdownRequested.TrySetResult();
        public Task RestartApplicationAsync() => Task.CompletedTask;
    }

    private sealed class SettingsOperationWorkspace(Func<Task> export) : ISettingsDialogWorkspacePort
    {
        public bool HasPlaylistTables => true;
        public long PlaylistCatalogVersion => 0;
        public IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots() => [];
        public bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string root) => false;
        public void SchedulePlaylistUrlCompletionRefresh(string reason) { }
        public Task ExportBeatorajaBmtAsync(string reason, string cleanup, LibraryFileMutationCapability capability, bool forceProjection = false, BeatorajaBmtOptionsSnapshot? options = null) => export();
        public Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string name) => operation();
        public event EventHandler<PlaylistCatalogChangedEventArgs> PlaylistCatalogChanged { add { } remove { } }
    }
}
