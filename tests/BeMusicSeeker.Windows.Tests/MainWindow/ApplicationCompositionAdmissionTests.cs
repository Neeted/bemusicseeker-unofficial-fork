using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ApplicationCompositionTestSupport;

namespace BeMusicSeeker.Tests;


public sealed partial class ApplicationCompositionTests
{
    /// <summary>実初期化後のBusyは依存操作を副作用前に拒否し、通常項目保存・候補・選択を維持します。通常Closeは未保存候補だけを確定値へ戻し、保存bytesを変えません。解放後の明示再生で共有受付を確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StandardComposition_RejectsDependentOperationsWhileSavingOrdinaryItemsAndKeepingCandidates()
    {

        var ownedWorkspace = TestWorkspace.Create(Path.GetTempPath());
        string root = ownedWorkspace.RootDirectory;
        string path = Path.Combine(root, "admission.bms");
        File.WriteAllText(path, "#PLAYER 1\n#TITLE Admission\n#BPM 120\n#00111:01\n");
        BeMusicSeeker.Properties.Settings values = TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config"));
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
        var player = new FakeBmsPlayer();
        var dialogs = new FileDbReportRecordingDialogs();
        var composition = new ApplicationComposition(settingsEditSession: session,
            defaultBmsPlayerFactory: () => player, uiScheduler: new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe"))),
            chartFileScanner: CapturedChartFileScanner.FromFixture([path], new Dictionary<string, IEnumerable<string>> { [root] = [] }, [root]),
            rootFileEnumerator: new FastRootFileEnumerator(),
            fileDbMutationDialogService: dialogs, settingsDialogService: dialogs);
        var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, []);
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
            Assert.AreEqual(!before, TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")).KeepInstallablePackagesPending);
            viewModel.SettingDialog.BeatorajaPlayerId = "pending-score-player";

            await viewModel.SettingDialog.CommitScoreSettingsAsync();
            await viewModel.SettingDialog.AddBmsSearchRootPathsAsync([Path.GetDirectoryName(path)]);
            await viewModel.SettingDialog.CommitBmsRootsAsync();

            Assert.AreEqual(0, session.SaveCount);
            Assert.AreEqual(!before, viewModel.SettingDialog.KeepInstallablePackagesPending, "Busyでも通常項目の保存値を保ちます。");
            Assert.IsFalse(viewModel.SettingDialog.IsEditCompletionInProgress);
            Assert.AreSame(rows, viewModel.MainChartList.Rows);
            viewModel.MainChartList.SelectedIndex = -1;
            viewModel.MainChartList.SelectedIndex = 0;
            Assert.AreEqual(0, viewModel.MainChartList.SelectedIndex);
            Assert.AreEqual("pending-score-player", viewModel.SettingDialog.BeatorajaPlayerId, "Busyによる確定拒否では未確定候補を破棄しません。");
            string settingsPath = Path.Combine(root, "user.config");
            string confirmedPlayer = TestSettingsFactory.OpenSettings(settingsPath).BeatorajaPlayerId;
            Assert.AreNotEqual("pending-score-player", confirmedPlayer);
            byte[] savedSettingsBeforeClose = File.ReadAllBytes(settingsPath);
            viewModel.SettingDialog.CloseCommand.Execute();
            Assert.AreEqual(!before, viewModel.SettingDialog.KeepInstallablePackagesPending);
            Assert.AreEqual(confirmedPlayer, viewModel.SettingDialog.BeatorajaPlayerId, "通常Closeでは未確定候補を最新の確定値へ戻します。");
            CollectionAssert.AreEqual(savedSettingsBeforeClose, File.ReadAllBytes(settingsPath), "通常Closeは候補を保存せず、保存済み通常項目も変更しません。");
            lease?.Dispose();
            await viewModel.PlaybackPanel.StartAtIndex(0);
            Assert.IsTrue(player.Commands.Any(command => command == "PlayStart:" + path));
            Assert.AreEqual(confirmedPlayer, viewModel.SettingDialog.BeatorajaPlayerId);
            Assert.AreEqual(confirmedPlayer, TestSettingsFactory.OpenSettings(settingsPath).BeatorajaPlayerId);
        }
        finally
        {
            lease?.Dispose();
            try { await TestUiDispatcherHost.Dispatcher.Invoke(() => viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync()); }
            finally { viewModel.SettingDialog.Dispose(); }
            ownedWorkspace.Dispose();
        }
    }


    /// <summary>実LR2同期の受付保持中は既存試聴を保ち新規再生を拒否します。無関係な通常項目は実保存でき、終了時に別の保存や再読込みを合成しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StandardComposition_ActualLr2KeepsPlaybackAndAllowsIndependentOrdinarySettings()
    {

        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string chartPath = Path.Combine(scope.DirectoryPath, "preview.bms");
        File.WriteAllText(chartPath, "#TITLE Preview\n#BPM 120\n#00111:01\n");
        string configPath = Path.Combine(scope.DirectoryPath, "user.config");
        BeMusicSeeker.Properties.Settings values = TestSettingsFactory.OpenSettings(configPath);
        values.OperationModeLR2DB = true;
        values.LR2ConfigXmlPath = TestLr2ConfigFactory.CreateLr2Config(scope.DirectoryPath).ConfigFilePath;
        values.Save();
        var session = new FakeSettingsEditSession { Values = values, PersistOwnedValues = true };
        var player = new FakeBmsPlayer();
        var dialogs = new FileDbReportRecordingDialogs();
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new System.Threading.ManualResetEventSlim();
        bool holdPreparation = false;
        var composition = new ApplicationComposition(bmsLibraryOptionsProvider: () => options, settingsEditSession: session,
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.MissingEverythingBridge),
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
        var profile = new LibraryProfile(true, scope.SongDbPath, [], composition.ResolveLr2Config(values.LR2ConfigXmlPath), null, false, false, false, false, "admission-lr2-test");
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

            Assert.IsFalse((await viewModel.PackageCatalog.ClearAllAsync(PackageCatalogSection.Pending)).Succeeded);
            Assert.AreEqual(playStarts, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(stops, player.CloseProcessCount);
            Assert.AreEqual(0, session.SaveCount);
            CollectionAssert.AreEqual(xmlBeforeBusy, File.ReadAllBytes(configPath), "新規再生拒否は保存済み通常項目を変更しません。");
            Assert.AreEqual(reloadsBeforeBusy, session.ReloadCount, "新規再生拒否で設定を再読込みしません。");
            Assert.AreEqual(editedValue, values.KeepInstallablePackagesPending, "実行中の同期が後から保存した通常項目を戻しません。");
            Assert.AreEqual(!before, viewModel.SettingDialog.KeepInstallablePackagesPending, "Busyでも編集値を保持します。");
            viewModel.MainChartList.SelectedIndex = -1;
            viewModel.MainChartList.SelectedIndex = 0;
            release.Set();
            await operation;
            Assert.IsFalse(composition.OperationAdmission.IsActive);

            Assert.AreEqual(0, session.SaveCount);
            Assert.AreEqual(!before, values.KeepInstallablePackagesPending, "通常項目は同期の終端を待たず保存済みです。");
            Assert.AreEqual(!before, TestSettingsFactory.OpenSettings(configPath).KeepInstallablePackagesPending);
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

}
