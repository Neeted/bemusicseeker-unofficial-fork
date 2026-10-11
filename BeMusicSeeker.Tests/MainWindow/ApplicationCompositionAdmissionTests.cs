using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class ApplicationCompositionTests
{
    /// <summary>実初期化後の共有受付がBusyならカタログ変更・再読込・再生開始・設定保存を開始せずdraftと選択を保ち、解放後は明示操作を通すことを確認し、構成された各入口の受付迂回を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StandardComposition_RejectsCatalogPlaybackAndSettingsBeforeSideEffectsWhileKeepingDraftAndSelection()
    {

        string root = StartupLibraryConstructionTestSupport.CreateTemporaryRoot();
        string path = Path.Combine(root, "admission.bms");
        File.WriteAllText(path, "#PLAYER 1\n#TITLE Admission\n#BPM 120\n#00111:01\n");
        BeMusicSeeker.Properties.Settings values = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config"));
        values.OperationModeLR2DB = false;
        values.BMSRootPath = root;
        values.StandaloneBmsRootPaths = root;
        values.BMSInstallDir = root;
        values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
        values.EnablePlaylistUrlCompletion = false;
        values.ScanBmsFilesOnStartup = true;
        values.StartupSelectInstallPending = false;
        values.SkipInitPlaylistLoad = true;
        values.UseBeatorajaScoreDb = false;
        values.EnableBeatorajaBmtOutput = false;
        values.UseExternalPanelImage = false;
        values.UsePlayeruBMplay = false;
        values.UsePlayerLR2body = false;
        values.UsePlayerBMIIDXView = false;
        values.IsLR2BackupEnabled = false;
        var session = new FakeSettingsEditSession { Values = values };
        var player = new PlaybackPanelViewModelTests.FakeBmsPlayer();
        var dialogs = new FileDbReportRecordingDialogs();
        var composition = new ApplicationComposition(settingsEditSession: session,
            defaultBmsPlayerFactory: () => player, uiScheduler: new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
            chartFileScanner: CapturedChartFileScanner.FromFixture([path], new Dictionary<string, IEnumerable<string>> { [root] = [] }, [root]),
            rootFileEnumerator: new FastRootFileEnumerator(),
            fileDbMutationDialogService: dialogs, settingsDialogService: dialogs);
        var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, []) { InitialSearchTargets = [root] };
        MainWindowViewModel viewModel = TestUiDispatcherHost.Dispatcher.Invoke(() => new MainWindowViewModel(composition, factory));
        IDisposable? lease = null;
        try
        {
            Task<bool> initialize = TestUiDispatcherHost.Dispatcher.Invoke(() => viewModel.InitializeAsync());
            Assert.IsTrue(await initialize);
            Assert.IsTrue(viewModel.IsInitializationCompleted);
            Assert.IsTrue(viewModel.MainChartList.Rows.OfType<LibraryChartRow>().Any(row => row.Title == "Admission"), "予約だけでなく今回の必須表示が反映されています。");

            // 独立後続の終了はBusy検証の準備だけに使い、必須成功の条件にはしません。
            StartupBackgroundTaskSchedulerOwner scheduler = typeof(MainWindowViewModel)
                .GetField("startupBackgroundTaskScheduler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(viewModel)
                as StartupBackgroundTaskSchedulerOwner ?? throw new InvalidOperationException("Actual startup scheduler is unavailable.");
            var backgroundTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void BackgroundChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (!viewModel.ProgressHub.IsStartupBackgroundInitializationActive && scheduler.IsFullyIdle) { backgroundTerminal.TrySetResult(); }
            }
            viewModel.ProgressHub.PropertyChanged += BackgroundChanged;
            try
            {
                if (viewModel.ProgressHub.IsStartupBackgroundInitializationActive || !scheduler.IsFullyIdle) { await backgroundTerminal.Task; }
            }
            finally { viewModel.ProgressHub.PropertyChanged -= BackgroundChanged; }
            ChartFile chart = (factory.CreatedLibrary ?? throw new InvalidOperationException("Actual library is unavailable.")).BmsCharts.Single();
            System.Collections.IList rows = viewModel.MainChartList.Rows;
            viewModel.MainChartList.SelectedIndex = 0;
            bool before = viewModel.SettingDialog.KeepInstallablePackagesPending;
            int stopsBeforeBusy = player.CloseProcessCount;
            Assert.IsTrue(composition.OperationAdmission.TryEnter(out lease));
            Assert.IsFalse((await viewModel.PackageCatalog.ClearAllAsync(PackageCatalogSection.Pending)).Succeeded);
            int messagesBeforeReload = dialogs.Messages.Count;
            await viewModel.ReloadFileDiffAsync();
            await viewModel.ReinitializeLibraryAsync();
            Assert.AreEqual(messagesBeforeReload + 2, dialogs.Messages.Count);
            await viewModel.PlaybackPanel.Start();
            await viewModel.PlaybackPanel.StartAtIndex(0);
            await viewModel.PlaybackPanel.Next();
            await viewModel.PlaybackPanel.Previous();
            await viewModel.PlaybackPanel.ExecuteTableRowActivation(0, chart);
            Assert.IsFalse(player.Commands.Any(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(stopsBeforeBusy, player.CloseProcessCount);
            Assert.IsNull(viewModel.PlaybackPanel.NowPlayingChart);
            viewModel.SettingDialog.KeepInstallablePackagesPending = !before;
            await viewModel.SettingDialog.SaveSettings();
            await viewModel.SettingDialog.ApplySettingsAsync();
            await viewModel.SettingDialog.AddBmsSearchRootPathFromMainWindowPicker(Path.GetDirectoryName(path));
            await viewModel.SettingDialog.RequestRemoveBmsSearchRootAsync(Path.GetDirectoryName(path));
            Assert.AreEqual(0, session.SaveCount);
            Assert.AreEqual(!before, viewModel.SettingDialog.KeepInstallablePackagesPending, "Busyでもdraftを捨てません。");
            Assert.IsFalse(viewModel.SettingDialog.IsEditCompletionInProgress);
            Assert.AreSame(rows, viewModel.MainChartList.Rows);
            viewModel.MainChartList.SelectedIndex = -1;
            viewModel.MainChartList.SelectedIndex = 0;
            Assert.AreEqual(0, viewModel.MainChartList.SelectedIndex);
            viewModel.SettingDialog.CancelCommand.Execute();
            Assert.AreEqual(before, viewModel.SettingDialog.KeepInstallablePackagesPending);
            lease?.Dispose();
            await viewModel.PlaybackPanel.StartAtIndex(0);
            Assert.IsTrue(player.Commands.Any(command => command == "PlayStart:" + path));
            viewModel.SettingDialog.SaveOperationModeForRestart(false);
            Assert.AreEqual(1, session.SaveCount);
        }
        finally
        {
            lease?.Dispose();
            try { await TestUiDispatcherHost.Dispatcher.Invoke(() => viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync()); }
            finally { viewModel.SettingDialog.Dispose(); Directory.Delete(root, recursive: true); }
        }
    }
    /// <summary>
    /// 稼働profileでLまたはPがBusyならmode選択と保存を未開始で終え、draft・XML・稼働値を保ちます。
    /// 解放後も拒否要求を再実行せず、次の明示保存だけを一回保存し、受付迂回やdraftの混入を検出します。
    /// </summary>
    /// <remarks>実初期化で稼働profileへ到達し、標準構成の受付共有を確認します。実Closeと確認競合はMainWindowViewHostTestsが分担します。</remarks>
    [DataTestMethod]
    [DataRow("library")]
    [DataRow("playlist")]
    [DoNotParallelize]
    public async Task StandardComposition_OperationModeBusyPreservesDraftUntilNextExplicitSave(string admission)
    {
        string root = StartupLibraryConstructionTestSupport.CreateTemporaryRoot();
        string path = Path.Combine(root, "admission.bms");
        File.WriteAllText(path, "#PLAYER 1\n#TITLE Admission\n#BPM 120\n#00111:01\n");
        BeMusicSeeker.Properties.Settings values = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config"));
        values.OperationModeLR2DB = false;
        values.BMSRootPath = root;
        values.StandaloneBmsRootPaths = root;
        values.BMSInstallDir = root;
        values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
        values.EnablePlaylistUrlCompletion = false;
        values.ScanBmsFilesOnStartup = true;
        values.StartupSelectInstallPending = false;
        values.SkipInitPlaylistLoad = true;
        values.UseBeatorajaScoreDb = false;
        values.EnableBeatorajaBmtOutput = false;
        values.UseExternalPanelImage = false;
        values.UsePlayeruBMplay = false;
        values.UsePlayerLR2body = false;
        values.UsePlayerBMIIDXView = false;
        values.IsLR2BackupEnabled = false;
        values.ShowScoreViewerRegisterConfirmMsg = true;
        values.Save();
        var session = new AdmissionModeSettingsSession(values);
        var player = new PlaybackPanelViewModelTests.FakeBmsPlayer();
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        var lifetime = new AdmissionModeLifetime();
        var composition = new ApplicationComposition(settingsEditSession: session,
            defaultBmsPlayerFactory: () => player, uiScheduler: new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
            chartFileScanner: CapturedChartFileScanner.FromFixture([path], new Dictionary<string, IEnumerable<string>> { [root] = [] }, [root]),
            rootFileEnumerator: new FastRootFileEnumerator(),
            fileDbMutationDialogService: dialogs, settingsDialogService: dialogs);
        var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, []) { InitialSearchTargets = [root] };
        MainWindowViewModel viewModel = TestUiDispatcherHost.Dispatcher.Invoke(() => new MainWindowViewModel(composition, factory));
        IDisposable? lease = null;
        try
        {
            Task<bool> initialize = TestUiDispatcherHost.Dispatcher.Invoke(() => viewModel.InitializeAsync());
            Assert.IsTrue(await initialize);
            Assert.IsTrue(viewModel.IsInitializationCompleted);
            Assert.IsTrue(viewModel.MainChartList.Rows.OfType<LibraryChartRow>().Any(row => row.Title == "Admission"), "予約だけでなく今回の必須表示が反映されています。");

            // 独立後続の終了はBusy検証の準備だけに使い、必須成功の条件にはしません。
            StartupBackgroundTaskSchedulerOwner scheduler = typeof(MainWindowViewModel)
                .GetField("startupBackgroundTaskScheduler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(viewModel)
                as StartupBackgroundTaskSchedulerOwner ?? throw new InvalidOperationException("Actual startup scheduler is unavailable.");
            var backgroundTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void BackgroundChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (!viewModel.ProgressHub.IsStartupBackgroundInitializationActive && scheduler.IsFullyIdle) { backgroundTerminal.TrySetResult(); }
            }
            viewModel.ProgressHub.PropertyChanged += BackgroundChanged;
            try
            {
                if (viewModel.ProgressHub.IsStartupBackgroundInitializationActive || !scheduler.IsFullyIdle) { await backgroundTerminal.Task; }
            }
            finally { viewModel.ProgressHub.PropertyChanged -= BackgroundChanged; }
            byte[] before = File.ReadAllBytes(Path.Combine(root, "user.config"));
            bool appliedMode = composition.CustomFolderOutputSettingsProvider().OperationModeLR2DB;
            values.ShowScoreViewerRegisterConfirmMsg = false;
            ChartFileOperationSynchronizer blocker = admission == "library"
                ? composition.OperationAdmission : composition.PlaylistOperationAdmission;
            Assert.IsTrue(blocker.TryEnter(out lease));
            TestUiDispatcherHost.Invoke(() =>
            {
                viewModel.SettingDialog.OperationModeLR2DB = true;
                Assert.IsTrue(viewModel.SettingDialog.OperationModeLR2DB);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, dialogs.LastMessageRequest.MessageBoxText);
                viewModel.SettingDialog.SaveOperationModeForRestart(true);
            });
            Assert.IsFalse(values.OperationModeLR2DB);
            Assert.AreEqual(appliedMode, composition.CustomFolderOutputSettingsProvider().OperationModeLR2DB);
            Assert.IsFalse(viewModel.SettingDialog.ShowScoreViewerRegisterConfirmMsg);
            Assert.IsNull(dialogs.LastConfirmationRequest);
            Assert.AreEqual(0, session.ModeSaveCount);
            Assert.AreEqual(0, session.SaveCount);
            Assert.AreEqual(0, lifetime.RestartCount);
            Assert.AreEqual(0, lifetime.MarkCount);
            Assert.IsFalse(viewModel.ShellShutdownWorkflow.IsShutdownPreparationStarted);
            Assert.IsFalse(viewModel.SettingDialog.IsEditCompletionInProgress);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(root, "user.config")));
            if (admission == "playlist") { Assert.IsFalse(composition.OperationAdmission.IsActive); }
            lease?.Dispose();
            lease = null;
            Assert.AreEqual(0, session.ModeSaveCount, "受付解放後に拒否要求を自動再実行しません。");
            TestUiDispatcherHost.Invoke(() => viewModel.SettingDialog.SaveOperationModeForRestart(true));
            Assert.AreEqual(1, session.ModeSaveCount);
            Assert.AreEqual(0, session.SaveCount);
            Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")).OperationModeLR2DB);
            Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")).ShowScoreViewerRegisterConfirmMsg,
                "modeだけの明示保存は未保存の別draftをXMLへ混ぜません。");
        }
        finally
        {
            lease?.Dispose();
            try { await TestUiDispatcherHost.Dispatcher.Invoke(() => viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync()); }
            finally { viewModel.SettingDialog.Dispose(); Directory.Delete(root, recursive: true); }
        }
    }

    private sealed class AdmissionModeLifetime : IApplicationLifetimePort
    {
        internal int RestartCount { get; private set; }
        internal int MarkCount { get; private set; }
        public bool IsFirstStartup => false;
        public void CompleteFirstStartup() { }
        public void MarkCoordinatedShutdownStarted(string reason) => MarkCount++;
        public void RequestShutdown() { }
        public Task RestartApplicationAsync() { RestartCount++; return Task.CompletedTask; }
    }

    /// <summary>mode保存を実設定sessionへ委譲し、通常保存とmode専用保存の受付回数を区別します。</summary>
    private sealed class AdmissionModeSettingsSession : ISettingsEditSession
    {
        private readonly SettingsEditSession inner;
        internal AdmissionModeSettingsSession(BeMusicSeeker.Properties.Settings values) => inner = new(values);
        internal int ModeSaveCount { get; private set; }
        internal int SaveCount { get; private set; }
        public BeMusicSeeker.Properties.Settings Values => inner.Values;
        public void Reload() => inner.Reload();
        public void Save() { SaveCount++; inner.Save(); }
        public void SaveOperationModeForRestart(bool mode, string historyIdentity)
        {
            ModeSaveCount++;
            inner.SaveOperationModeForRestart(mode, historyIdentity);
        }
    }

    /// <summary>実LR2同期の受付保持中は既存の試聴を止めず新規再生・保存・適用を拒否し、XMLとdraftを保って次の明示保存だけを通すことを確認し、同期受付の共有漏れや拒否時の編集破棄を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StandardComposition_ActualLr2KeepsExistingPlaybackAndRejectsNewStartsAndSavesWithoutDiscardingDraft()
    {

        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string chartPath = Path.Combine(scope.DirectoryPath, "preview.bms");
        File.WriteAllText(chartPath, "#TITLE Preview\n#BPM 120\n#00111:01\n");
        string configPath = Path.Combine(scope.DirectoryPath, "user.config");
        BeMusicSeeker.Properties.Settings values = PortableSettingsPersistenceTests.OpenSettings(configPath);
        values.OperationModeLR2DB = false;
        values.Save();
        var session = new FakeSettingsEditSession { Values = values, PersistOwnedValues = true };
        var player = new PlaybackPanelViewModelTests.FakeBmsPlayer();
        var dialogs = new FileDbReportRecordingDialogs();
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new System.Threading.ManualResetEventSlim();
        bool holdPreparation = false;
        var composition = new ApplicationComposition(() => options, settingsEditSession: session,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            rootFileEnumerator: new EmptyAdmissionRootFileEnumerator(),
            customFolderOutputSettingsProvider: () =>
            {
                var capturedSettings = CustomFolderOutputSettingsSnapshot.CreateCurrent(values);
                if (holdPreparation) { entered.TrySetResult(); release.Wait(); }
                return capturedSettings;
            },
            defaultBmsPlayerFactory: () => player,
            uiScheduler: new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            fileDbMutationDialogService: dialogs, settingsDialogService: dialogs);
        MainWindowViewModel viewModel = TestUiDispatcherHost.Dispatcher.Invoke(composition.CreateMainWindowViewModel);
        var profile = new LibraryProfile(true, scope.SongDbPath, [], () => null!, null, false, false, false, false, "admission-lr2-test");
        BMSLibrary library = composition.CreateBmsLibrary(profile);
        BMSPlaylist playlist = composition.CreateBmsPlaylist(profile, library);
        var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);
        ChartFile chart = ChartTestValues.Empty() with { Path = chartPath };
        viewModel.MainChartList.Rows = new List<object> { chart };
        viewModel.MainChartList.SelectedIndex = 0;
        Task? operation = null;
        try
        {
            await viewModel.PlaybackPanel.StartAtIndex(0);
            int playStarts = player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal));
            int stops = player.CloseProcessCount;
            holdPreparation = true;
            operation = Task.Run(() => runtime.QueueAsync("actual-composition-lr2", force: true, prepareGeneratedData: true));
            await Task.WhenAny(entered.Task, operation);
            if (!entered.Task.IsCompleted) { await operation; Assert.Fail("実LR2準備入力の捕捉へ進みませんでした。"); }
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(composition.OperationAdmission.IsActive);
            Assert.AreEqual(stops, player.CloseProcessCount, "DB同期だけでは既存の試聴を止めません。");
            bool before = viewModel.SettingDialog.KeepInstallablePackagesPending;
            viewModel.SettingDialog.KeepInstallablePackagesPending = !before;
            bool editedValue = values.KeepInstallablePackagesPending;
            byte[] xmlBeforeBusy = File.ReadAllBytes(configPath);
            int reloadsBeforeBusy = session.ReloadCount;
            await viewModel.PlaybackPanel.StartAtIndex(0);
            await viewModel.SettingDialog.SaveSettings();
            await viewModel.SettingDialog.ApplySettingsAsync();
            Assert.IsFalse((await viewModel.PackageCatalog.ClearAllAsync(PackageCatalogSection.Pending)).Succeeded);
            Assert.AreEqual(playStarts, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(stops, player.CloseProcessCount);
            Assert.AreEqual(0, session.SaveCount);
            CollectionAssert.AreEqual(xmlBeforeBusy, File.ReadAllBytes(configPath), "Busy保存・適用は所有XMLを変更しません。");
            Assert.AreEqual(reloadsBeforeBusy, session.ReloadCount, "拒否された保存・適用は再読込みも始めません。");
            Assert.AreEqual(editedValue, values.KeepInstallablePackagesPending, "編集後の値へBusy保存・適用自身が追加変更を行いません。");
            Assert.AreEqual(!before, viewModel.SettingDialog.KeepInstallablePackagesPending, "Busyでも編集値を保持します。");
            viewModel.MainChartList.SelectedIndex = -1;
            viewModel.MainChartList.SelectedIndex = 0;
            release.Set();
            await operation;
            Assert.IsFalse(composition.OperationAdmission.IsActive);
            await viewModel.SettingDialog.SaveSettings();
            Assert.AreEqual(1, session.SaveCount);
            Assert.AreEqual(!before, values.KeepInstallablePackagesPending, "終端後の明示保存で同じ編集値を反映します。");
            Assert.AreEqual(!before, PortableSettingsPersistenceTests.OpenSettings(configPath).KeepInstallablePackagesPending);
        }
        finally
        {
            release.Set();
            try { if (operation != null) { await operation; } }
            finally
            {
                library.RequestShutdown("actual-composition-finally");
                viewModel.PlaybackPanel.BeginShutdown();
                await viewModel.PlaybackPanel.CloseForShutdown();
                viewModel.SettingDialog.Dispose();
                viewModel.RegularChartList.Dispose();
            }
        }
    }

    /// <summary>受理済み権限を標準構成の実表出力とLR2同期へ貸し、題名・区分・categoryをファイルとfolderへ保存して外側受付を保つことを確認し、継続権限や出力連携の欠落を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StandardComposition_PlaylistBindingGeneratesFileAndPersistsFolderUnderAcceptedLr2Capability()
    {

        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string outputBase = Path.Combine(scope.DirectoryPath, "Output");
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2CustomFolderOutputBaseDir = outputBase };
        var outputSettings = new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = true, LR2CustomFolderOutputBaseDir = outputBase };
        var composition = new ApplicationComposition(() => options,
            settingsEditSession: new FakeSettingsEditSession(),
            customFolderOutputSettingsProvider: () => outputSettings,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            rootFileEnumerator: new EmptyAdmissionRootFileEnumerator(),
            uiScheduler: new TestUiScheduler(() => null),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var profile = new LibraryProfile(true, scope.SongDbPath, [], () => null!, null, false, true, false, false, "binding-output-test");
        BMSLibrary library = composition.CreateBmsLibrary(profile);
        BMSPlaylist playlist = composition.CreateBmsPlaylist(profile, library);
        var table = new BMSTable
        {
            playlist_id = 7301,
            name = "AcceptedTable",
            symbol = "AT",
            Output_dir = "AcceptedTable",
            ignore_folder_output = BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
            entries = [BmsPlaylistTestSupport.CreateEntry(new string('a', 32), "Folder A")],
            Folder_order = ["Folder A"]
        };
        playlist.BMSTables = new System.Collections.ObjectModel.ObservableCollection<BMSTable>([table]);
        var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);
        Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable lease));
        Task? operation = null;
        try
        {
            using LibraryFileMutationCapability capability = composition.OperationAdmission.CreateMutationCapability(lease);
            operation = runtime.QueueAsync("binding-output-test", force: true, prepareGeneratedData: true, capability: capability);
            await operation;
            Assert.IsTrue(composition.OperationAdmission.IsActive, "内部借用の終端は外側の受理済みleaseを解放しません。");
            string outputPath = Path.Combine(outputBase, "AcceptedTable", "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            string text = File.ReadAllText(outputPath, System.Text.Encoding.GetEncoding("shift_jis"));
            StringAssert.Contains(text, "#TITLE Folder A");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
            BeMusicSeeker.Models.LR2.LR2SongDB.folder folder = verify.Table<BeMusicSeeker.Models.LR2.LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual("Folder A", folder.title);
            Assert.AreEqual(2, folder.type);
            Assert.AreEqual("AcceptedTable", folder.category);
            StringAssert.Contains(folder.command, "playlist_entry");
        }
        finally
        {
            try { if (operation != null) { await operation; } }
            finally { lease.Dispose(); library.RequestShutdown("binding-output-cleanup"); }
        }
        Assert.IsFalse(composition.OperationAdmission.IsActive);
    }


    /// <summary>生成設定を捕捉した後でdraftを変えても今回のUNSENTファイルと行を保持し、次の明示同期で除去して両完了を保存することを確認し、実行途中の設定再読込や旧定義残留を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StandardComposition_Lr2UsesCapturedGenerationOptionAndAdoptsDraftOnNextExplicitRequest()
    {

        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string outputA = Path.Combine(scope.DirectoryPath, "OutputA");
        BeMusicSeeker.Properties.Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2CustomFolderOutputBaseDir = outputA;
                values.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            });
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCapture = new System.Threading.ManualResetEventSlim();
        bool holdCapture = false;
        var composition = new ApplicationComposition(() => BmsLibraryOptionsSnapshot.CreateCurrent(values),
            settingsEditSession: new FakeSettingsEditSession { Values = values },
            customFolderOutputSettingsProvider: () =>
            {
                var snapshot = CustomFolderOutputSettingsSnapshot.CreateCurrent(values);
                if (holdCapture)
                {
                    captured.TrySetResult();
                    releaseCapture.Wait();
                }
                return snapshot;
            },
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            rootFileEnumerator: new EmptyAdmissionRootFileEnumerator(),
            uiScheduler: new TestUiScheduler(() => null),
            applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var profile = new LibraryProfile(true, scope.SongDbPath, [], () => null!, null, false, true, false, false, "capture-output-test");
        BMSLibrary library = composition.CreateBmsLibrary(profile);
        BMSPlaylist playlist = composition.CreateBmsPlaylist(profile, library);
        var table = new BMSTable
        {
            playlist_id = 7302,
            name = "CapturedTable",
            symbol = "CT",
            Output_dir = "CapturedTable",
            ignore_folder_output = BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.OtherFolder,
            entries = [BmsPlaylistTestSupport.CreateEntry(new string('a', 32), "Folder A")],
            Folder_order = ["Folder A"]
        };
        playlist.BMSTables = new System.Collections.ObjectModel.ObservableCollection<BMSTable>([table]);
        var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);
        Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable lease));
        using LibraryFileMutationCapability capability = composition.OperationAdmission.CreateMutationCapability(lease);
        Task? first = null;
        Task? next = null;
        try
        {
            holdCapture = true;
            first = Task.Run(() => runtime.QueueAsync("capture-output-A", force: true, prepareGeneratedData: true, capability: capability));
            await Task.WhenAny(captured.Task, first);
            if (!captured.Task.IsCompleted) { await first; Assert.Fail("設定入力捕捉へ到達しませんでした。"); }
            values.EnableDownloadLr2IrScoreAndDetectUnsent = false;
            releaseCapture.Set();
            await first;
            Assert.IsTrue(File.Exists(Path.Combine(outputA, "CapturedTable", "0001.lr2folder")), "受理したAを生成処理が消費します。");
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed,
                library.GetLr2SongDbSyncStatusSnapshot().Status,
                "非配置optionのdraft編集は今回捕捉した生成入力を変えません。");
            {
                string[] files = Directory.GetFiles(Path.Combine(outputA, "CapturedTable"), "*.lr2folder", SearchOption.AllDirectories);
                Assert.IsTrue(files.Any(path => File.ReadAllText(path, System.Text.Encoding.GetEncoding("shift_jis")).Contains("#TITLE UNSENT SONGS", StringComparison.Ordinal)),
                    "捕捉したA=trueの未送信定義を実ファイルへ生成します。");
                using LR2SongDBExtended firstDb = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
                Assert.IsTrue(firstDb.Table<BeMusicSeeker.Models.LR2.LR2SongDB.folder>().Any(row => row.title == "UNSENT SONGS"));
                Assert.AreEqual("Completed", firstDb.Find<BeMusicSeeker.Models.LR2.LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).status);
            }
            lease.Dispose();
            next = runtime.QueueAsync("capture-output-B", force: true, prepareGeneratedData: true);
            await next;
            string outputPath = Path.Combine(outputA, "CapturedTable", "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, library.GetLr2SongDbSyncStatusSnapshot().Status);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual("Folder A", verify.Table<BeMusicSeeker.Models.LR2.LR2SongDB.folder>().Single(row => row.path == outputPath).title);
            {
                string[] files = Directory.GetFiles(Path.Combine(outputA, "CapturedTable"), "*.lr2folder", SearchOption.AllDirectories);
                Assert.IsFalse(files.Any(path => File.ReadAllText(path, System.Text.Encoding.GetEncoding("shift_jis")).Contains("#TITLE UNSENT SONGS", StringComparison.Ordinal)),
                    "次の明示要求ではdraft B=falseを消費して管理定義を除去します。");
                Assert.IsFalse(verify.Table<BeMusicSeeker.Models.LR2.LR2SongDB.folder>().Any(row => row.title == "UNSENT SONGS"));
                Assert.AreEqual("Completed", verify.Find<BeMusicSeeker.Models.LR2.LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).status);
            }
        }
        finally
        {
            releaseCapture.Set();
            try { if (first != null) { await first; } }
            finally
            {
                try { if (next != null) { await next; } }
                finally { lease.Dispose(); library.RequestShutdown("capture-output-cleanup"); }
            }
        }
    }

    /// <summary>実差分走査の受付保持中でも独立した表登録と対応出力を確定し、LR2全体同期が競合する条件では確定差分を保って見送ることを確認し、非交差操作の過剰排他や確定結果の破棄を検出します。</summary>
    [DataTestMethod]
    [DataRow(false, false)] // standalone: 表正本を登録しLR2出力は行いません。
    [DataRow(true, false)] // LR2: 表出力と差分後の全体同期まで確定します。
    [DataRow(true, true)] // LR2: 表受付が競合する全体同期だけ見送り、確定済み結果は残します。
    public async Task DailyLibraryDiffAndIndependentPlaylistRegistration_CompleteActualChangesConcurrently(bool lr2Mode, bool skipWhole)
    {

        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string music = Path.Combine(scope.DirectoryPath, "Music");
        string output = Path.Combine(scope.DirectoryPath, "Output");
        Directory.CreateDirectory(music);
        if (lr2Mode) { Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(scope.DirectoryPath, "RootOutput")); }
        PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
        string chart = Path.Combine(music, "chart.bms");
        File.WriteAllText(chart, "#PLAYER 1\n#TITLE Actual Diff\n#ARTIST Fixture\n#BPM 120\n#00111:01\n");
        BeMusicSeeker.Properties.Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = lr2Mode;
                values.LR2RootPath = scope.DirectoryPath;
                values.BMSRootPath = music;
                values.StandaloneBmsRootPaths = music;
                values.BMSInstallDir = music;
                values.LR2CustomFolderOutputBaseDir = output;
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(scope.DirectoryPath, "RootOutput");
                values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
                values.EnablePlaylistUrlCompletion = false;
                values.EnableBeatorajaBmtOutput = false;
                values.ScanBmsFilesOnStartup = false;
            });
        var composition = new ApplicationComposition(settingsEditSession: new FakeSettingsEditSession { Values = values },
            applicationPathSnapshot: TestBmsFactory.AvailableEverythingBridge, rootFileEnumerator: new FastRootFileEnumerator(),
            uiScheduler: new TestUiScheduler(() => null),
            applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var scanner = CapturedChartFileScanner.FromFixture([chart], new Dictionary<string, IEnumerable<string>>(), [music]);
        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScan = new System.Threading.ManualResetEventSlim();
        scanner.ScanObserved = () => { scanned.TrySetResult(); releaseScan.Wait(); };
        var library = new TestBmsLibrary(scope.SongDbPath, null, null, null, composition.BmsLibraryOptionsProvider,
            TestBmsFactory.AvailableEverythingBridge, scanner, new TestUiScheduler(() => null), new FastRootFileEnumerator(),
            operationAdmission: composition.OperationAdmission)
        { SearchTargets = [music], BmsCharts = [], BmsonCharts = [] };
        var profile = new LibraryProfile(lr2Mode, scope.SongDbPath, [music], () => null!, null, false, lr2Mode, false, false, "parallel-daily-test");
        BMSPlaylist playlist = composition.CreateBmsPlaylist(profile, library);
        var table = new BMSTable
        {
            name = "Independent",
            symbol = "I",
            Output_dir = "Independent",
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
            entries = [BmsPlaylistTestSupport.CreateEntry(new string('a', 32), "Daily Folder")],
            Folder_order = ["Daily Folder"]
        };
        var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => lr2Mode);
        int references = 0;
        var reloadOwner = new FileDiffReloadWorkflowOwner(request => Task.Run(() => library.ReloadFileDiff(request.Capability)),
            new Lr2SongDbSyncWorkflowOwner(runtime), _ => { references++; return playlist.ApplyRequiredLibraryReferencesAsync(); });
        Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable lease));
        using LibraryFileMutationCapability capability = composition.OperationAdmission.CreateMutationCapability(lease);
        Task<FileDiffReloadWorkflowResult>? reload = null;
        Task<BMSTable>? registration = null;
        IDisposable? heldPlaylist = null;
        try
        {
            reload = reloadOwner.ReloadAsync(new FileDiffReloadRequest("parallel-daily", 101, capability));
            await Task.WhenAny(scanned.Task, reload);
            if (!scanned.Task.IsCompleted) { await reload; Assert.Fail("実FileDiffの固定scanへ到達しませんでした。"); }
            Assert.IsTrue(composition.OperationAdmission.IsActive);
            registration = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(table, false, "parallel-daily-registration");
            await registration;
            Assert.IsFalse(reload.IsCompleted, "非交差PはLのscan終端を待ちません。");
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.playlist>().Count(row => row.name == "Independent"));
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.playlist_entry>().Count(row => row.folder == "Daily Folder"));
                if (lr2Mode)
                {
                    string generated = Path.Combine(output, "Independent", "0001.lr2folder");
                    StringAssert.Contains(File.ReadAllText(generated, System.Text.Encoding.GetEncoding("shift_jis")), "#TITLE Daily Folder");
                    Assert.AreEqual("Daily Folder", db.Table<LR2SongDB.folder>().Single(row => row.path == generated).title);
                }
                else { Assert.IsFalse(Directory.Exists(output)); }
            }
            if (skipWhole) { Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out heldPlaylist)); }
            releaseScan.Set();
            FileDiffReloadWorkflowResult result = await reload;
            Assert.AreEqual(1, references);
            if (skipWhole)
            {
                Assert.AreEqual(Lr2SongDbSyncQueueStatus.SkippedCompeting, result.Lr2QueueResult.Status);
                Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
                Assert.AreEqual(0, library.Lr2SongDbSyncCompletedVersion);
                Assert.IsFalse(library.Lr2SongDbSyncRunning);
            }
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(chart, db.Table<LR2SongDB.song>().Single().path);
                if (lr2Mode) { Assert.IsTrue(db.Table<LR2SongDB.folder>().Any(row => row.type == 1 && row.path.Contains("Music", StringComparison.Ordinal))); }
            }
            heldPlaylist?.Dispose(); lease.Dispose();
            Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable next)); next.Dispose();
            Assert.AreEqual(skipWhole ? 0 : library.Lr2SongDbSyncRequestedVersion, library.Lr2SongDbSyncCompletedVersion);
        }
        finally
        {
            releaseScan.Set();
            try { if (registration != null) { await registration; } }
            finally { try { if (reload != null) { await reload; } } finally { heldPlaylist?.Dispose(); lease.Dispose(); library.RequestShutdown("parallel-daily-cleanup"); } }
        }
    }

    private sealed class EmptyAdmissionRootFileEnumerator : IRootFileEnumerator
    {
        public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
        {
            var result = new RootFileEnumerationResult { Success = true, IsComplete = true, BackendName = "fixture", TotalFileCount = 0 };
            foreach (RootFileEnumerationGroup group in groups) { result.InitializeGroup(group.Name); }
            return result;
        }
    }

}
