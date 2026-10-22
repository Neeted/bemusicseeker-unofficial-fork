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
    /// <summary>実ownerは設定の準備中・必須UI待ち・局所BMT待ちのTaskを表示参照の消去前に捕捉し、実終端前に保存・runtime終了へ進みません。</summary>
    /// <remarks>実MainWindow・モーダル・compiled開始/player入力とCloseを使用します。BMT物理媒体の整合は局所BMT系列が分担し、ここは実P内のport終端を保持します。</remarks>
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
            string newBeatoraja = Path.Combine(root, "new-beatoraja");
            WriteBeatorajaRoot(oldBeatoraja, "old", ["old"], createScoreFiles: false);
            WriteBeatorajaRoot(newBeatoraja, "invalid", ["invalid", "valid"], createScoreFiles: false);
            File.WriteAllBytes(BeatorajaConfigService.GetScoreDbPath(newBeatoraja, "valid"), []);
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
                            owner.SettingDialog.UseBeatorajaScoreDb = true;
                            owner.SettingDialog.BeatorajaRootPath = newBeatoraja;
                            ComboBox player = FindDescendants<ComboBox>(presented).Single(item => item.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BeatorajaPlayerId));
                            player.SetCurrentValue(ComboBox.SelectedItemProperty, "valid");
                        }
                        else { ((Button)presented.FindName("buttonOK")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                        operation = presented.WaitForOperationCompletionAsync();
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
