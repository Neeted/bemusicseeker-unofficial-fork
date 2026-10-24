using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
    /// <summary>初回root選択と修復時の詳細XML確定から履歴対象を更新し、候補・保存失敗では旧対象を保ち、保存後のschema操作が新playerのDBだけへ届くことを確認します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Lr2EnvironmentConfirmation_RefreshesHistoryTargetOnlyAfterSaving(bool initialRootSelection)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot();
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
                    using var database = new SQLite.SQLiteConnection(score);
                    database.RunInTransaction(() =>
                    {
                        database.CreateTable<LR2ScoreDB.score>();
                        database.CreateTable<LR2ScoreDB.player>();
                        database.Execute("CREATE TABLE retained_data (value TEXT);");
                        database.Execute("INSERT INTO retained_data VALUES (?);", score);
                    });
                }
                var schema = new Lr2PlayHistorySchemaService();
                Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, schema.InstallOrRepair(oldScore, isLr2LinkedProfile: true).Status);
                byte[] previousDatabase = File.ReadAllBytes(oldScore);
                string settingsPath = Path.Combine(root, "user.config");
                Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(settingsPath));
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
                Assert.AreEqual(nextConfig, PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2ConfigXmlPath);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.InstallOrRepairLr2PlayHistorySchemaAsync(), "history-new-target-install");
                Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, schema.Check(nextScore, isLr2LinkedProfile: true).Status);
                Assert.AreEqual(nextScore, dialog.Lr2PlayHistorySchemaStatusSnapshot.ScoreDbPath);
                Assert.AreEqual(initialRootSelection ? 2 : 1, dialogs.ConfirmationCount, "初回rootの詳細候補破棄と、新対象のschema導入だけを確認します。");
                Assert.AreEqual(1, failures.Count);
                CollectionAssert.AreEqual(previousDatabase, File.ReadAllBytes(oldScore), "旧DBのschemaと保存行は変更しません。");
                using var verify = new SQLite.SQLiteConnection(nextScore, SQLite.SQLiteOpenFlags.ReadOnly);
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

    /// <summary>モードは保存済み個別組（空rootも可）、root選択は今回の標準組だけを保存します。詳細候補・Busy・実保存失敗から混成値や遅延適用を公開しません。</summary>
    [TestMethod]
    public void Lr2AutomaticSelection_UsesConfirmedModeTupleAndSelectedRootAndKeepsFailureRepairable()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string path = Path.Combine(root, "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(path));
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
            Settings saved = PortableSettingsPersistenceTests.OpenSettings(path);
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
            Assert.AreEqual(savedSong, PortableSettingsPersistenceTests.OpenSettings(path).LR2SongDBPath);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { dialog.SelectLr2RootAsync(Path.Combine(root, "selected")).GetAwaiter().GetResult(); }
            Assert.AreEqual(1, failures.Count);
            Assert.AreEqual(string.Empty, composition.CustomFolderOutputSettingsProvider().LR2RootPath, "保存失敗では出力readerへ新rootを公開しません。");
            Assert.AreEqual(savedSong, values.LR2SongDBPath);
            Assert.AreEqual(savedConfig, values.LR2ConfigXmlPath);
            dialog.SelectLr2RootAsync(Path.Combine(root, "selected")).GetAwaiter().GetResult();
            saved = PortableSettingsPersistenceTests.OpenSettings(path);
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

    /// <summary>再起動確認中に実Pだけが先着した場合も保存前に拒否します。P解放・再表示では予約実行せず、その後の明示操作だけがL/P保持中にshell保存へ進みます。</summary>
    [TestMethod]
    public void Lr2AutomaticSelection_PlaylistOperationAcceptedDuringConfirmationRejectsWithoutDeferredRestart()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(path));
            (values.LR2SongDBPath, values.LR2ConfigXmlPath) = CreateValidLr2Layout(Path.Combine(root, "lr2"));
            values.Save();
            var session = new CountingSettingsEditSession(values) { PersistOwnedValues = true };
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            MainWindowViewModel owner = CreateViewModel(session, firstStartup: false, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
            SettingsDialogViewModel dialog = owner.SettingDialog;
            IDisposable? heldPlaylist = null;
            try
            {
                SetActiveLibraryProfile(owner, true);
                int restartRequests = 0;
                owner.ShellShutdownWorkflow.OperationModeRestartRequested += () => restartRequests++;
                string staged = Directory.CreateDirectory(Path.Combine(root, "PendingBms")).FullName;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.AddBmsSearchRootPathsAsync([staged]), "mode-confirmation-bms-input");
                byte[] before = File.ReadAllBytes(path);
                dialogs.ConfirmationObserved = request =>
                {
                    StringAssert.Contains(request.MessageBoxText, Resources.Confirm_RestartForOperationModeChange);
                    StringAssert.Contains(request.MessageBoxText, Resources.Standalone_BMSDirectories.Trim().TrimEnd(':', '：'));
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable acquired));
                    heldPlaylist = acquired;
                    Assert.IsFalse(owner.IsLibraryOperationInProgress, "P-onlyの先着は起動・譜面変更状態だけでは検出できません。");
                };
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectOperationModeAsync(true), "mode-confirmation-playlist-wins");
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, staged);
                Assert.AreEqual(0, session.SaveCount);
                Assert.AreEqual(0, restartRequests);
                Assert.IsFalse(values.OperationModeLR2DB);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
                Assert.IsFalse(composition.OperationAdmission.IsActive, "P取得失敗時は取得済みLも解放します。");
                Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                Assert.IsFalse(dialog.IsEditCompletionInProgress);
                heldPlaylist?.Dispose();
                heldPlaylist = null;
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectOperationModeAsync(true), "mode-confirmation-explicit-retry");
                Assert.AreEqual(2, dialogs.ConfirmationCount);
                Assert.AreEqual(1, session.SaveCount);
                Assert.AreEqual(1, restartRequests);
                Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(path).OperationModeLR2DB);
                Assert.IsFalse(composition.OperationAdmission.IsActive, "受理後は終了join前にLを解放します。");
                Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive, "受理後は終了join前にPを解放します。");
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                session.SaveObserved = null;
                heldPlaylist?.Dispose();
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
            string root = CreateTemporaryRoot();
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "old"), [], PortableSettingsPersistenceTests.OpenSettings(path));
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
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "LR2-individual-picker-terminal");
                    Assert.AreEqual(oldSong, PortableSettingsPersistenceTests.OpenSettings(path).LR2SongDBPath);
                    Assert.AreEqual(oldConfig, values.LR2ConfigXmlPath);
                    Assert.AreEqual(0, restarts);
                }
                FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:environment")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "LR2-detailed-confirmation-terminal");
                Settings saved = PortableSettingsPersistenceTests.OpenSettings(path);
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
        string root = CreateTemporaryRoot();
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                string settingsPath = Path.Combine(root, "user.config");
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
                new BmsLibraryDbGateway(values.LR2SongDBPath).EnsureAppOwnedSchema();
                string folder = Path.Combine(root, "LeapCandidate");
                Directory.CreateDirectory(folder);
                Directory.SetLastWriteTime(folder, new DateTime(2024, 2, 29, 12, 0, 0));
                using (var db = new LR2SongDBExtended(values.LR2SongDBPath))
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
                    applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge, rootFileEnumerator: new FastRootFileEnumerator());
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
                    Assert.AreEqual(captured, EnvironmentSettingsChange.Capture(PortableSettingsPersistenceTests.OpenSettings(settingsPath)));
                    Assert.AreEqual(oldBeatoraja, values.BeatorajaRootPath);
                    bool ordinary = !values.ShowNewPackageInstallConfirmMsg;
                    dialog.ShowNewPackageInstallConfirmMsg = ordinary;
                    Assert.AreEqual(ordinary, PortableSettingsPersistenceTests.OpenSettings(settingsPath).ShowNewPackageInstallConfirmMsg);
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
            string root = CreateTemporaryRoot();
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")));
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
                applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "application", "BeMusicSeeker.exe")),
                rootFileEnumerator: new FastRootFileEnumerator(), audioDeviceCatalog: new TestAudioDeviceCatalog(),
                chartFileScanner: CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [root] = [] }, [root]));
            MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
            var window = new SettingsWindow { DataContext = owner.SettingDialog };
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
                        await window.WaitForOperationCompletionAsync();
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
    [DataTestMethod]
    [DataRow(false)] // playerは存在しますが、そのscore.dbがありません。
    [DataRow(true)] // 構成にplayer名があっても、playerディレクトリがありません。
    public void BeatorajaSelection_InvalidResolvedScoreDatabaseRejectsBeforeSideEffects(bool missingPlayer)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string oldRoot = Path.Combine(root, "old-beatoraja");
            string newRoot = Path.Combine(root, "new-beatoraja");
            WriteBeatorajaRoot(oldRoot, "old-player", ["old-player"], createScoreFiles: true);
            WriteBeatorajaRoot(newRoot, "new-player", missingPlayer ? [] : ["new-player"], createScoreFiles: false);
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(settingsPath));
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
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
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
                    Assert.AreEqual(oldRoot, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BeatorajaRootPath);
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
            string root = CreateTemporaryRoot();
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")));
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
                window = new SettingsWindow { DataContext = settings };
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

    /// <summary>実root pickerの同値選択は保存済み表記・候補・再試行表示を保ち、有効な別rootの選択は旧root前提候補を確認し、確定方針でplayer・派生先だけ保存して関係候補を破棄します。</summary>
    /// <remarks>configured採用と一覧先頭の既存解決、保存失敗後の明示修復を確認します。同値での保存・処理要求0は狭いBMT方針検査も分担します。</remarks>
    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)] // 実providerの保存拒否では組を公開せず、同じ局所候補を明示再試行できます。
    public void BeatorajaRootPicker_SavesConfirmedPolicyAndDiscardsOnlyRelatedDrafts(bool configuredPlayerExists, bool failSave)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            SettingsWindow? window = null;
            MainWindowViewModel? owner = null;
            try
            {
                string oldRoot = Path.Combine(root, "old-beatoraja");
                string newRoot = Path.Combine(root, "new-beatoraja");
                WriteBeatorajaRoot(oldRoot, "old-player", ["old-player", "draft-player"], createScoreFiles: true);
                WriteBeatorajaRoot(newRoot, configuredPlayerExists ? "z-player" : "missing", ["a-player", "z-player"], createScoreFiles: true);
                string settingsPath = Path.Combine(root, "settings", "user.config");
                Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(settingsPath));
                values.BeatorajaRootPath = oldRoot;
                values.BeatorajaPlayerId = "old-player";
                values.BeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(oldRoot, "old-player");
                values.BeatorajaBmtTablePath = BeatorajaConfigService.GetTablePath(oldRoot);
                values.UseBeatorajaScoreDb = true;
                values.Save();
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, audioDeviceCatalog: new TestAudioDeviceCatalog(),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                owner = composition.CreateMainWindowViewModelForTest();
                SettingsDialogViewModel dialog = owner.SettingDialog;
                window = new SettingsWindow(dialogs) { DataContext = dialog, PlaybackPanel = owner.PlaybackPanel };
                scope.ShowAndWaitForContentRendered(window);
                ComboBox scorePlayer = FindDescendants<ComboBox>(window).Single(item => item.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BeatorajaPlayerId));
                SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(window).Single(item => item.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.BeatorajaRootPath));
                Button scoreRetry = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "retry:score"));
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
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "beatoraja-equivalent-root-picker-terminal");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(oldRoot, values.BeatorajaRootPath);
                    Assert.AreEqual(oldRoot, dialog.BeatorajaRootPath);
                    Assert.AreEqual("old-player", dialog.BeatorajaPlayerId);
                    Assert.AreEqual(values.BeatorajaScoreDbPath, dialog.EnvironmentBeatorajaScoreDbPath);
                    Assert.AreEqual(values.BeatorajaBmtTablePath, dialog.EnvironmentBeatorajaBmtTablePath);
                    Assert.IsFalse(dialog.HasScoreSelectionChanges);
                    Assert.IsFalse(dialog.CanRetryScoreSettings);
                    Assert.AreNotEqual(Visibility.Visible, scoreRetry.Visibility);
                    Assert.AreEqual(0, saves);
                    Assert.AreEqual(0, selectionChanges, "同値pickerは自動反映の選択通知も発行しません。");
                    Assert.AreEqual(0, failures.Count);
                    CollectionAssert.AreEqual(originalSettings, File.ReadAllBytes(settingsPath));
                    Assert.AreEqual(oldRoot, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BeatorajaRootPath);
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "beatoraja-root-discard-cancelled");
                Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                Assert.AreEqual(oldRoot, dialog.BeatorajaRootPath);
                CollectionAssert.AreEqual(beforeRejectedRoot, File.ReadAllBytes(settingsPath));
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.CancelledByUser));
                FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "beatoraja-root-picker-cancelled");
                Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
                Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable? refusedLease));
                using (refusedLease)
                {
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [newRoot]));
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "beatoraja-root-admission-refused");
                    Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                    Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                    CollectionAssert.AreEqual(beforeRejectedRoot, File.ReadAllBytes(settingsPath));
                }
                dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [newRoot]));
                using (FileStream? blocker = failSave ? File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                {
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "beatoraja-root-picker-terminal");
                }
                if (failSave)
                {
                    Settings unchanged = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                    Assert.AreEqual(oldRoot, unchanged.BeatorajaRootPath);
                    Assert.AreEqual("old-player", unchanged.BeatorajaPlayerId);
                    Assert.AreEqual(BeatorajaConfigService.GetScoreDbPath(oldRoot, "old-player"), unchanged.BeatorajaScoreDbPath);
                    Assert.AreEqual(BeatorajaConfigService.GetTablePath(oldRoot), unchanged.BeatorajaBmtTablePath);
                    Assert.AreEqual(1, failures.Count);
                    Assert.AreEqual("draft-player", dialog.BeatorajaPlayerId);
                    Assert.IsTrue(dialog.EnableBeatorajaBmtOutput);
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [newRoot]));
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "beatoraja-root-save-explicit-retry");
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                string expectedPlayer = configuredPlayerExists ? "z-player" : "a-player";
                Settings saved = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "beatoraja-player-selection-terminal");
                Assert.AreEqual(nextPlayer, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BeatorajaPlayerId);
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

    /// <summary>機器を必要としない実beatoraja構成とplayer/score媒体を、ケース固有rootに作ります。</summary>
    private static void WriteBeatorajaRoot(string root, string configuredPlayer, IReadOnlyList<string> players, bool createScoreFiles)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(root, BeatorajaConfigService.ConfigFileName), System.Text.Json.JsonSerializer.Serialize(
            new { playerpath = "profiles", playername = configuredPlayer, tablepath = "tables", tableURL = Array.Empty<string>() }));
        foreach (string player in players)
        {
            string directory = Path.Combine(root, "profiles", player);
            Directory.CreateDirectory(directory);
            if (createScoreFiles) { File.WriteAllText(Path.Combine(directory, "score.db"), string.Empty); }
        }
    }

    /// <summary>局所root保存後の旧新BMT・URL整理を実媒体で待ち、旧URL又は新出力失敗後も同じ旧新対象を明示修復します。BMT候補を混ぜず、未了中のLR2再起動を拒否して修復後だけ受理します。</summary>
    [DataTestMethod]
    [DataRow("success")]
    [DataRow("old-url-failure")]
    [DataRow("new-output-failure")]
    public void BeatorajaRootConfirmation_RepairsOriginalOldAndNewBmtTargetsWithoutRestart(string failureStage)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string oldRoot = Path.Combine(root, "old-beatoraja");
            string newRoot = Path.Combine(root, "new-beatoraja");
            foreach (string path in new[] { oldRoot, newRoot })
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "beatoraja.jar"), string.Empty);
                File.WriteAllText(Path.Combine(path, BeatorajaConfigService.ConfigFileName), "{\"tablepath\":\"table\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
                Directory.CreateDirectory(Path.Combine(path, "table"));
                File.WriteAllText(Path.Combine(path, "table", "unmanaged.bmt"), "unmanaged");
            }
            string configPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(configPath));
            (values.LR2SongDBPath, values.LR2ConfigXmlPath) = CreateValidLr2Layout(Path.Combine(root, "lr2"));
            values.BeatorajaRootPath = oldRoot;
            values.BeatorajaBmtTablePath = Path.Combine(oldRoot, "table");
            values.EnableBeatorajaBmtOutput = true;
            values.RegisterBeatorajaBmtUrls = true;
            values.Save();
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var events = new List<string>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                dialogs.MessageObserved = () =>
                {
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                    Assert.AreEqual(newRoot, PortableSettingsPersistenceTests.OpenSettings(configPath).BeatorajaRootPath);
                    events.Add("notification");
                };
                SetActiveLibraryProfile(owner, true);
                string database = BmsPlaylistTestSupport.CreateTempSongDbPath(root);
                PlaylistPersistenceRepository.EnsureSchema(database);
                BMSPlaylist playlist = AttachPlaylistTables(owner, root, existingDatabasePath: database);
                var table = new BMSTable
                {
                    playlist_id = 671,
                    name = "Environment",
                    Folder_order = ["Folder"],
                    entries = [new BMSTableEntry { md5 = new string('a', 32), sha256 = new string('b', 64), title = "Song", folder = "Folder" }]
                };
                playlist.BMSTables.Add(table);
                Task? shutdown = null;
                try
                {
                    playlist.CommitBMSTablesWithEntriesToDB([table]);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(playlist.BmtOutput.ExportAllAsync("old-environment"), "environment-initial-bmt");
                    Assert.AreEqual(2, Directory.GetFiles(values.BeatorajaBmtTablePath, "*.bmt").Length);
                    if (failureStage == "new-output-failure")
                    {
                        File.Delete(Path.Combine(newRoot, "table", "unmanaged.bmt"));
                        Directory.Delete(Path.Combine(newRoot, "table"));
                        File.WriteAllText(Path.Combine(newRoot, "table"), "directory unavailable");
                    }
                    owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
                    {
                        if (request.Receipt.Notifications.Count == 0) { return; }
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(composition.PlaylistOperationAdmission.IsActive);
                        Assert.AreEqual(newRoot, PortableSettingsPersistenceTests.OpenSettings(configPath).BeatorajaRootPath);
                        events.Add("notification");
                    };
                    owner.ShellShutdownWorkflow.OperationModeRestartRequested += () =>
                    {
                        events.Add("close");
                        shutdown = owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                    };
                    owner.SettingDialog.BeatorajaRootPath = newRoot;
                    owner.SettingDialog.EnableBeatorajaBmtOutput = false;
                    owner.SettingDialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
                    owner.SettingDialog.UseExternalPanelImage = true;
                    string oldConfig = Path.Combine(oldRoot, BeatorajaConfigService.ConfigFileName);
                    using (FileStream? blocker = failureStage == "old-url-failure" ? File.Open(oldConfig, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitBeatorajaRootSettingsAsync(), "beatoraja-local-bmt-terminal"); }
                    Settings saved = PortableSettingsPersistenceTests.OpenSettings(configPath);
                    Assert.AreEqual(newRoot, saved.BeatorajaRootPath);
                    Assert.IsFalse(saved.UseBeatorajaScoreDb);
                    Assert.IsFalse(saved.UseExternalPanelImage);
                    Assert.IsTrue(saved.EnableBeatorajaBmtOutput, "未確定BMT候補をroot保存へ混ぜません。");
                    Assert.IsFalse(owner.SettingDialog.IsEditCompletionInProgress);
                    Assert.IsNull(shutdown);
                    if (failureStage != "success")
                    {
                        Assert.IsTrue(failures.Count + events.Count(item => item == "notification") > 0);
                        Assert.IsTrue(owner.SettingDialog.IsBmtProcessingRetryRequired);
                        Assert.IsTrue(owner.SettingDialog.CanRetryBeatorajaRootSettings);
                        byte[] beforeRestart = File.ReadAllBytes(configPath);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SelectOperationModeAsync(true), "pending-real-bmt-restart-refused");
                        Assert.IsNull(shutdown, "旧新BMTの未了修復を失うLR2再起動は受理しません。");
                        Assert.IsFalse(values.OperationModeLR2DB);
                        CollectionAssert.AreEqual(beforeRestart, File.ReadAllBytes(configPath));
                        Assert.IsFalse(owner.SettingDialog.IsBeatorajaSelectionEnabled);
                        owner.SettingDialog.BeatorajaRootPath = Path.Combine(root, "third-root");
                        Assert.AreEqual(newRoot, owner.SettingDialog.BeatorajaRootPath, "修復入力を別rootで上書きしません。");
                        owner.SettingDialog.ShowNewPackageInstallConfirmMsg = !values.ShowNewPackageInstallConfirmMsg;
                        if (failureStage == "old-url-failure")
                        { Assert.AreEqual(2, Directory.GetFiles(Path.Combine(oldRoot, "table"), "*.bmt").Length); }
                        if (failureStage == "new-output-failure")
                        {
                            File.Delete(Path.Combine(newRoot, "table"));
                            Directory.CreateDirectory(Path.Combine(newRoot, "table"));
                            File.WriteAllText(Path.Combine(newRoot, "table", "unmanaged.bmt"), "unmanaged");
                            File.WriteAllText(Path.Combine(newRoot, BeatorajaConfigService.ConfigFileName),
                                "{\"tablepath\":\"later-tables\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
                        }
                        int notificationsBeforeReopen = events.Count(item => item == "notification");
                        owner.SettingDialog.SetPresentationActive(false);
                        owner.SettingDialog.SetPresentationActive(true);
                        Assert.AreEqual(Path.Combine(newRoot, "table"), owner.SettingDialog.EnvironmentBeatorajaBmtTablePath);
                        Assert.IsTrue(owner.SettingDialog.CanRetryBeatorajaRootSettings);
                        Assert.AreEqual(notificationsBeforeReopen, events.Count(item => item == "notification"), "再表示は修復を自動実行しません。");
                        using (File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (FileStream? completedOldUrl = failureStage == "new-output-failure" ? File.Open(oldConfig, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                        { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitBeatorajaRootSettingsAsync(), "beatoraja-local-bmt-explicit-repair"); }
                    }
                    else { Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures)); }
                    Assert.IsFalse(owner.SettingDialog.IsBmtProcessingRetryRequired);
                    Assert.AreEqual(2, Directory.GetFiles(Path.Combine(newRoot, "table"), "*.bmt").Length);
                    CollectionAssert.Contains(BeatorajaConfigService.ReadTableUrls(newRoot).ToArray(), "bemusicseeker://playlist/671");
                    Assert.IsFalse(Directory.Exists(Path.Combine(newRoot, "later-tables")), "再試行は保存時に捕捉した出力先で物理出力とURL同期を完了します。");
                    CollectionAssert.AreEqual(new[] { "https://example.com/unmanaged" }, BeatorajaConfigService.ReadTableUrls(oldRoot).ToArray());
                    Assert.AreEqual(1, Directory.GetFiles(Path.Combine(oldRoot, "table"), "*.bmt").Length);
                    Assert.AreEqual("unmanaged", File.ReadAllText(Path.Combine(oldRoot, "table", "unmanaged.bmt")));
                    Assert.AreEqual("unmanaged", File.ReadAllText(Path.Combine(newRoot, "table", "unmanaged.bmt")));
                    Assert.IsNull(shutdown);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.SelectOperationModeAsync(true), "repaired-real-bmt-restart-accepted");
                    Assert.IsNotNull(shutdown, "自身の明示修復後はLR2再起動を受理します。");
                    Assert.AreEqual(1, events.Count(item => item == "close"));
                }
                finally
                {
                    playlist.RequestShutdown("environment-bmt-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown ?? owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "environment-fixture-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
