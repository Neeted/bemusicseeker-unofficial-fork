using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;


/// <remarks>行の受付・世代・集計はOperationProgressHubの下位ケースが担い、実Windowではcompiled表示と行ボタンの接続を確認します。MainWindowTestLifetimeで共有Resourcesを所有するため並列実行しません。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
[DoNotParallelize]
public sealed class MainWindowProgressStatusBarWpfTests
{
    /// <summary>実起動の任意後続作業をgateで保持し、status barが稼働中を表示して同じ作業の終端で行を除くことを確認し、compiled Bindingの接続漏れや進捗行の残留を検出します。</summary>
    [TestMethod]
    public void StartupPostInitialization_ActualWorkReflectsThroughCompiledStatusBarBinding()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            using var fixture = new StartupCompletionTestFixture.CompletionFixture(lifetime: lifetime);
            fixture.Owner.StartupUpdateWorkflow.NotifyClosing();
            using var ownership = new MainWindowTestLifetime(fixture.Owner, lifetime.ShutdownRequested.Task);
            var terminals = new MainWindowProgressStatusBarTerminals(() => { }, () => { }, () => { }, () => { });
            MainWindow window = ownership.CreateWindow(() => new MainWindow(fixture.Owner, settingsWindowCreated: null, libraryReloadMenuTerminal: null,
                regularLibraryTreeTerminal: null, maintenanceTreeTerminal: null, installTreeTerminal: null,
                zeroNoteRecheckTerminal: null,
                folderAutoRenameTerminal: null, duplicateMaintenanceTerminal: null, maintenanceRescanTerminal: null,
                packageCatalogTerminal: null, pendingInstallEstimationTerminal: null, pendingInstallationTerminal: null,
                installedLocationRepairTerminal: null, pendingBulkMaintenanceTerminal: null, mainChartCellEditTerminal: null,
                progressStatusBarTerminals: terminals));
            object content = window.Content;
            window.Content = null;
            var host = new Window { Width = 1100, Height = 700, Resources = window.Resources, Content = content, DataContext = fixture.Owner };
            try
            {
                scope.ShowAndWaitForContentRendered(host);
                Task<bool> initialization = fixture.Start();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "binding-real-startup");
                Assert.IsTrue(initialization.GetAwaiter().GetResult());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(fixture.OptionalEntered.Task, fixture.OptionalWork, "binding-optional-arrival"), "binding-optional-arrival");
                StatusBar statusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
                ItemsControl rows = GetNamedElement<ItemsControl>(window, "progressRows");
                host.UpdateLayout();
                Assert.IsTrue(fixture.Owner.ProgressHub.IsStartupBackgroundInitializationActive);
                Assert.IsTrue(statusBar.IsVisible);
                ProgressBar parent = FindVisualChildren<ProgressBar>(rows).Single(bar =>
                    ((OperationProgressRow)bar.DataContext).Key == "startup_background");
                var parentRow = (OperationProgressRow)parent.DataContext;
                Assert.IsFalse(parent.IsIndeterminate);
                Assert.IsTrue(parent.IsVisible);
                Assert.AreEqual(parentRow.Value, parent.Value);
                Assert.AreEqual(parentRow.Maximum, parent.Maximum);
                Assert.IsTrue(parent.Maximum > parent.Value);
                StringAssert.Contains(parentRow.Label, "[" + parent.Value + "/" + parent.Maximum + "]");
                TextBlock child = FindVisualChildren<TextBlock>(rows).First(text =>
                    text.DataContext is OperationProgressRow { ParentKey: "startup_background" } && text.Inlines.Count > 0);
                Assert.AreEqual(20d, ((Grid)VisualTreeHelper.GetParent(child)).Margin.Left);
                fixture.OptionalRelease.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.OptionalWork, "binding-optional-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "binding-scheduler-terminal");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                host.UpdateLayout();
                Assert.IsFalse(fixture.Owner.ProgressHub.IsStartupBackgroundInitializationActive);
                Assert.IsFalse(FindVisualChildren<ProgressBar>(rows).Any(bar => ((OperationProgressRow)bar.DataContext).Key == "startup_background"));
            }
            finally
            {
                fixture.Ui.Release.TrySetResult();
                fixture.OptionalRelease.TrySetResult();
                host.Close();
            }
        });
    }

    /// <summary>起動・導入推定・詳細読込の同時進捗を実status barへ描画し、長文の全文tooltipと階層・可変幅・必要高さを保つことを確認し、行の重なりや固定幅による内容欠落を検出します。</summary>
    [TestMethod]
    public void ProgressStatusBar_RendersConcurrentRowsWithFlexibleLabelWidthAndMeasuredHeight()
    {
        var terminals = new MainWindowProgressStatusBarTerminals(() => { }, () => { }, () => { }, () => { });
        RunConstructorOnlyWithStatusBarTerminals(terminals, (viewModel, window, host) =>
        {
            OperationProgressHubViewModel hub = viewModel.ProgressHub;
            StatusBar statusBar = GetNamedElement<StatusBar>(window, "progressStatusBar");
            Assert.AreSame(hub, statusBar.DataContext);
            Assert.AreEqual(Visibility.Collapsed, statusBar.Visibility);
            long token = hub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            var request = new OperationProgressRequest(1, token, "chart_info_hydration", 1);
            hub.StartupProgress.TrackStartupProgressChartInfoHydrationRequested(1, request);
            hub.StartupProgress.ApplyPresentation(true, new string('あ', 200), "phase", 2, 13);
            host.UpdateLayout();
            double oneRowHeight = statusBar.ActualHeight;
            hub.UpdateInstallEstimationProgress(new BeMusicSeeker.Models.InstallEstimationProgressSnapshot
            {
                IsActive = true,
                TotalWorkCount = 8,
                CompletedWorkCount = 3,
                CurrentDisplayName = new string('長', 200)
            });
            hub.BeginBackgroundProgressGeneration(1);
            hub.UpdateBackgroundTaskProgress(new("chart_info_hydration", 1, 1, false, true, request));
            statusBar.ApplyTemplate();
            host.UpdateLayout();
            Assert.AreEqual(Visibility.Visible, statusBar.Visibility);
            Assert.IsTrue(double.IsNaN(statusBar.Height));
            Assert.IsTrue(statusBar.ActualHeight >= oneRowHeight * 2);
            ProgressBar[] bars = FindVisualChildren<ProgressBar>(statusBar).ToArray();
            Assert.AreEqual(3, bars.Length);
            Assert.AreEqual(13d, bars.Single(bar => ((OperationProgressRow)bar.DataContext).Key == "startup").Maximum);
            TextBlock[] labels = FindVisualChildren<TextBlock>(statusBar)
                .Where(text => text.DataContext is OperationProgressRow && text.Inlines.Count > 0).ToArray();
            Assert.AreEqual(3, labels.Length);
            Assert.IsTrue(labels.All(text => text.ActualWidth > 200));
            TextBlock child = labels.Single(text => ((OperationProgressRow)text.DataContext).IsChild);
            var childGrid = (Grid)VisualTreeHelper.GetParent(child);
            Assert.IsTrue(childGrid.Margin.Left > 0d);
            StringAssert.Contains((string)childGrid.ToolTip, ((OperationProgressRow)child.DataContext).Label);
            TextBlock longLabel = labels.Single(text => ((OperationProgressRow)text.DataContext).Key == "startup");
            StringAssert.Contains((string)((Grid)VisualTreeHelper.GetParent(longLabel)).ToolTip, new string('あ', 200));
            double wideLabelWidth = labels[0].ActualWidth;
            host.Width = 800;
            host.UpdateLayout();
            Assert.IsTrue(labels[0].ActualWidth < wideLabelWidth);
            Assert.IsTrue(labels.All(text => text.ActualWidth > 0));
            Assert.IsTrue(window.MinHeight >= statusBar.ActualHeight);
        });
    }

    /// <summary>実起動状態のStartupProgressと型付きplaylist進捗が、言語変更で実status barのTextBlockとtooltipをHub行の現在語へ更新し、窓を閉じた後はRows通知もStartupProgress通知も出さないことを確認します。</summary>
    /// <remarks>ApplyPresentationを直接呼ばず、実状態でStartupProgressを開始します。入口は本番の <see cref="ResourceService.ChangeCulture"/> で、標準構成ではMainWindow構築でAttach・閉鎖でDetachされます。</remarks>
    [TestMethod]
    public void ProgressStatusBar_LanguageChangeUpdatesBoundRowsAndStopsAfterClose()
    {
        CultureInfo? previous = Resources.Culture;
        try
        {
            var terminals = new MainWindowProgressStatusBarTerminals(() => { }, () => { }, () => { }, () => { });
            RunConstructorOnlyWithStatusBarTerminals(terminals, (viewModel, window, host) =>
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                OperationProgressHubViewModel hub = viewModel.ProgressHub;
                ItemsControl rows = GetNamedElement<ItemsControl>(window, "progressRows");
                viewModel.ProgressHub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
                viewModel.PlaylistWorkspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
                {
                    Source = "w6_import",
                    IsActive = true,
                    TotalTableCount = 3,
                    CompletedTableCount = 1,
                    CurrentTableName = "表A",
                    Kind = PlaylistSyncProgressKind.ExternalPlaylistImport,
                    ImportPhase = PlaylistImportProgressPhase.RegisterPlaylists
                });
                TestUiDispatcherHost.ProcessQueuedPresentation();
                host.UpdateLayout();

                void AssertBoundToHub(string stage)
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    host.UpdateLayout();
                    OperationProgressRow[] expected = [.. hub.Rows];
                    Assert.IsTrue(expected.Length >= 2, stage);
                    TextBlock[] texts = [.. FindVisualChildren<TextBlock>(rows).Where(text => text.DataContext is OperationProgressRow && text.Inlines.Count > 0)];
                    Assert.AreEqual(expected.Length, texts.Length, stage);
                    foreach (OperationProgressRow row in expected)
                    {
                        TextBlock text = texts.Single(item => ((OperationProgressRow)item.DataContext).Key == row.Key);
                        Assert.AreEqual(row.Label + " " + row.Detail, string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text)), stage + " " + row.Key);
                        Assert.AreEqual(row.FullText, (string)((Grid)VisualTreeHelper.GetParent(text)).ToolTip, stage + " " + row.Key);
                    }
                    Assert.AreEqual(string.Format(Resources.Playlist_import_progress_label_format, 1, 3), expected.Single(row => row.Key == "playlist:w6_import:0").Label, stage);
                    Assert.AreEqual(Resources.Playlist_import_progress_phase_register_playlists + ": 表A", expected.Single(row => row.Key == "playlist:w6_import:0").Detail, stage);
                    Assert.IsTrue(expected.Single(row => row.Key == "startup").Label.EndsWith(Resources.Statusbar_progress_startup, StringComparison.Ordinal), stage);
                }
                AssertBoundToHub("ja-JP");
                string japanese = string.Join("|", hub.Rows.Select(row => row.FullText));
                ResourceService.Current.ChangeCulture("en-US");
                AssertBoundToHub("en-US");
                Assert.AreNotEqual(japanese, string.Join("|", hub.Rows.Select(row => row.FullText)));
                ResourceService.Current.ChangeCulture("ja-JP");
                AssertBoundToHub("ja-JP again");
                Assert.AreEqual(japanese, string.Join("|", hub.Rows.Select(row => row.FullText)));

                // 窓を実際に閉じた後は、言語変更でHub・StartupProgressの通知を出しません。
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "progress-language.close-completion");
                if (!closed.Task.IsCompleted) { window.Close(); }
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "progress-language.closed");
                var notifications = new List<string>();
                hub.PropertyChanged += (_, args) => notifications.Add("hub:" + args.PropertyName);
                hub.StartupProgress.PropertyChanged += (_, args) => notifications.Add("startup:" + args.PropertyName);
                string labelAfterClose = hub.StartupProgress.Label;
                ResourceService.Current.ChangeCulture("en-US");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                CollectionAssert.DoesNotContain(notifications, "hub:" + nameof(OperationProgressHubViewModel.Rows));
                CollectionAssert.DoesNotContain(notifications, "hub:" + nameof(OperationProgressHubViewModel.HasRows));
                CollectionAssert.DoesNotContain(notifications, "startup:" + nameof(StartupProgressWorkflowOwner.Label));
                CollectionAssert.DoesNotContain(notifications, "startup:" + nameof(StartupProgressWorkflowOwner.SubLabel));
                CollectionAssert.DoesNotContain(notifications, "startup:" + nameof(StartupProgressWorkflowOwner.IsActive));
                Assert.AreEqual(labelAfterClose, hub.StartupProgress.Label);
            });
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previous?.Name ?? "en-US");
            Resources.Culture = previous;
            TestUiDispatcherHost.ProcessQueuedPresentation();
        }
    }

    /// <summary>導入・URL取得・メンテナンス・起動時の更新の各進捗ボタンから対応する取消入口だけを一回呼ぶことを確認し、compiled行テンプレートで別作業を取り消す誤りを検出します。</summary>
    [TestMethod]
    public void ProgressStatusBarButtons_InvokeEachRowsOwnerExactlyOnce()
    {
        var calls = new List<string>();
        var terminals = new MainWindowProgressStatusBarTerminals(
            () => calls.Add("install"), () => calls.Add("maintenance"),
            () => calls.Add("url"), () => calls.Add("update"));
        RunConstructorOnlyWithStatusBarTerminals(terminals, (viewModel, window, host) =>
        {
            viewModel.ProgressHub.StartupProgress.ApplyPresentation(true, "startup", "", 1, 13);
            ItemsControl rows = GetNamedElement<ItemsControl>(window, "progressRows");
            rows.ItemsSource = new[]
            {
                new OperationProgressRow("install", "install", "", Action: OperationProgressAction.CancelInstall),
                new OperationProgressRow("url", "url", "", Action: OperationProgressAction.CancelUrlDownload),
                new OperationProgressRow("maintenance", "maintenance", "", Action: OperationProgressAction.CancelMaintenance),
                new OperationProgressRow("update", "update", "", Action: OperationProgressAction.CancelStartupUpdate)
            };
            rows.ApplyTemplate();
            host.UpdateLayout();
            foreach (Button button in FindVisualChildren<Button>(rows).Where(button => button.Visibility == Visibility.Visible))
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
        });
        CollectionAssert.AreEqual(new[] { "install", "url", "maintenance", "update" }, calls);
    }

    /// <summary>実URL取得を保持した間でも導入取消とURL取消を別主体へ届け、それぞれの終端通知を一回保つことを確認し、構成標準の取消接続が別作業を巻き込む誤りを検出します。</summary>
    [TestMethod]
    public async Task DefaultFactory_MapsEveryOwnerRouteAndKeepsCancellationIndependent()
    {
        Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            });
        var composition = new ApplicationComposition(
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
            settingsEditSession: new NoOpSettingsEditSession(settings),
            playlistWorkspaceDialogService: new AcceptedStatusBarTestDialogService(),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            dialogService: UndeclaredDialogs.Create());
        using PlaylistWorkspaceTestPorts.OwnedPlaylistStore ownedPlaylistStore =
            PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore(composition.DatabaseScope);
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
        TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(composition.DatabaseScope,
            ownedPlaylistStore.SongDbPath,
            settings,
            viewModel);
        IStartupLibraryApplicationPort applicationPort = viewModel;
        applicationPort.AttachStartupLibrary(library);
        applicationPort.AttachStartupServices(
            new StartupLibraryServices(
                StartupLibraryConstructionTestSupport.CreateProfile(
                    Path.GetDirectoryName(ownedPlaylistStore.SongDbPath)!,
                    ownedPlaylistStore.SongDbPath),
                library,
                ownedPlaylistStore.Store));
        var terminals = MainWindowProgressStatusBarTerminals.Create(viewModel);
        int packageCancelRequests = 0;
        int maintenanceCancelRequests = 0;
        int playlistCancelableSnapshots = 0;
        int playlistCanceledSnapshots = 0;
        viewModel.PackageInstallWorkflow.CancelAllRequested += () => packageCancelRequests++;
        viewModel.MaintenanceRescanWorkflow.CancellationRequested += () => maintenanceCancelRequests++;
        viewModel.PlaylistWorkspace.PlaylistUrlDownloadStatusChanged += (_, snapshot) =>
        {
            if (snapshot.IsActive && snapshot.CanCancel)
            {
                Interlocked.Increment(ref playlistCancelableSnapshots);
            }
            else if (snapshot.IsActive)
            {
                Interlocked.Increment(ref playlistCanceledSnapshots);
            }
        };

        try
        {
            Assert.IsFalse(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            terminals.CancelInstallPipeline();
            Assert.AreEqual(1, packageCancelRequests);

            terminals.CancelMaintenanceRescan();
            Assert.AreEqual(1, maintenanceCancelRequests);

            await using var server = new BlockingLoopbackHttpServer();
            Task download = viewModel.PlaylistWorkspace.RunPlaylistUrlBatchAsync(
                [server.DownloadUri],
                isDiffUrl: false);
            await server.RequestAccepted.Task;
            Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            Assert.AreEqual(1, Volatile.Read(ref playlistCancelableSnapshots));

            terminals.Invoke(OperationProgressAction.CancelInstall);
            Assert.AreEqual(2, packageCancelRequests);
            Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            terminals.Invoke(OperationProgressAction.CancelUrlDownload);

            await download;
            Assert.IsFalse(viewModel.PlaylistWorkspace.IsPlaylistUrlDownloadRunning);
            Assert.AreEqual(2, packageCancelRequests);
            Assert.AreEqual(1, Volatile.Read(ref playlistCanceledSnapshots));
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }

    /// <summary>
    /// 実際の構成と実際の取得サービスで、更新ダイアログの選択の直後に始まった転送を状態表示バーの取消行から取り消す。
    /// ローカルのHTTPサーバーがZIPの本文を一部だけ送って止めるため、取消行が現れ、状態表示バーの取消先を呼ぶと取消の終端になり、
    /// 一時ファイルも検証済みZIPも残らず、宣言した更新ダイアログ以外のダイアログも更新ツールの準備もないことで、構成上の接続漏れや後始末漏れを検出する。
    /// </summary>
    /// <remarks>受信量の値は表明しません。アプリの場所はケース専有の一時領域です。</remarks>
    [TestMethod]
    public void StartupUpdateTransfer_IsCancelledThroughTheStatusBarTerminalAndLeavesNoPackage()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string appRoot = Path.Combine(workspace.RootDirectory, "update-app");
        Directory.CreateDirectory(appRoot);
        var server = new UpdatePackageServer();
        try
        {
            var dialogs = new UpdateSelectionDialogService(server.CreateAsset());
            var updater = new RecordingUpdaterProcessGateway();
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
                MainWindowViewModel viewModel = new ApplicationComposition(
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(
                        ApplicationPathSnapshot.FromExecutablePath(Path.Combine(appRoot, "BeMusicSeeker.exe")),
                        server.ManifestUri.ToString()),
                    settingsEditSession: new NoOpSettingsEditSession(TestSettingsFactory.CreateIsolatedSettings()),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: dialogs,
                    updaterProcessGateway: updater)
                    .CreateMainWindowViewModelForTest();
                var progressRow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var terminal = new TaskCompletionSource<StartupUpdateWorkflowCompletionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
                viewModel.StartupUpdateWorkflow.ProgressChanged += snapshot =>
                {
                    if (snapshot.IsActive && snapshot.CanCancel)
                    {
                        progressRow.TrySetResult(true);
                    }
                };
                viewModel.StartupUpdateWorkflow.TerminalPublished += receipt => terminal.TrySetResult(receipt);
                var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
                ownership.Run(() =>
                {
                    // 窓の構築が、更新確認と、ダイアログの選択の直後の取得を開始する。初期化は始めないため、取得は初期化と並行したまま待つ。
                    MainWindow window = ownership.CreateWindow(() => new MainWindow(
                        viewModel,
                        settingsWindowCreated: null,
                        libraryReloadMenuTerminal: null,
                        regularLibraryTreeTerminal: null,
                        maintenanceTreeTerminal: null,
                        installTreeTerminal: null,
                        zeroNoteRecheckTerminal: null,
                        folderAutoRenameTerminal: null,
                        duplicateMaintenanceTerminal: null,
                        maintenanceRescanTerminal: null,
                        packageCatalogTerminal: null,
                        pendingInstallEstimationTerminal: null,
                        pendingInstallationTerminal: null,
                        installedLocationRepairTerminal: null,
                        pendingBulkMaintenanceTerminal: null,
                        mainChartCellEditTerminal: null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(server.PartialBodySent.Task, "update-partial-body");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(progressRow.Task, "update-progress-row");

                    OperationProgressRow row = viewModel.ProgressHub.Rows.Single(candidate => candidate.Key == "update");
                    Assert.AreEqual(OperationProgressAction.CancelStartupUpdate, row.Action);
                    Assert.IsTrue(row.CanCancel);
                    Assert.IsFalse(terminal.Task.IsCompleted, "取得を保持している間に終端してはいけません。");

                    MainWindowProgressStatusBarTerminals.Create(viewModel).Invoke(row.Action);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal.Task, "update-cancelled-terminal");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, terminal.Task.Result.Outcome);
                    Assert.IsFalse(viewModel.ProgressHub.Rows.Any(candidate => candidate.Key == "update"), "終端の後に行が残ってはいけません。");
                    string downloads = Path.Combine(appRoot, "update_work", "downloads");
                    Assert.IsFalse(Directory.Exists(downloads) && Directory.EnumerateFileSystemEntries(downloads).Any(),
                        "一時ファイルも検証済みZIPも残してはいけません。");
                    Assert.AreEqual(1, dialogs.UpdateDialogCount);
                    Assert.AreEqual(0, dialogs.OtherDialogCount, "宣言した更新ダイアログ以外のダイアログを表示してはいけません。");
                    Assert.IsNull(updater.Request, "更新ツールの準備を呼んではいけません。");
                });
            });
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>更新ダイアログだけを宣言し、テストが選択結果を与える窓口です。それ以外の要求は数えて、失敗として表明します。</summary>
    private sealed class UpdateSelectionDialogService(UpdateAssetInfo selection) : IUiDialogService
    {
        private int updateDialogCount;
        private int otherDialogCount;

        internal int UpdateDialogCount => Volatile.Read(ref updateDialogCount);

        internal int OtherDialogCount => Volatile.Read(ref otherDialogCount);

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref otherDialogCount);
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref otherDialogCount);
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            if (typeof(TWindow) == typeof(UpdateAvailableDialog) && selection is TResult value)
            {
                Interlocked.Increment(ref updateDialogCount);
                return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.Accepted, value));
            }
            Interlocked.Increment(ref otherDialogCount);
            throw new NotSupportedException("Only the update dialog is declared.");
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref otherDialogCount);
            throw new NotSupportedException();
        }

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref otherDialogCount);
            throw new NotSupportedException();
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref otherDialogCount);
            throw new NotSupportedException();
        }

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref otherDialogCount);
            throw new NotSupportedException();
        }
    }

    /// <summary>更新情報を返し、ZIPの本文はヘッダーと一部だけ送って、破棄まで止めるローカルのHTTPサーバーです。</summary>
    private sealed class UpdatePackageServer : IAsyncDisposable
    {
        private const long PackageBytes = 1_048_576;
        private const string PackageFileName = "BeMusicSeeker-status-bar-test.zip";
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<Task> connections = [];
        private readonly Task acceptLoop;
        private readonly int port;

        internal UpdatePackageServer()
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptLoop = AcceptAsync();
        }

        internal Uri ManifestUri => new($"http://127.0.0.1:{port}/update.json");

        internal TaskCompletionSource<bool> PartialBodySent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>更新情報の先頭の資産と同じ内容の資産を作ります。</summary>
        internal UpdateAssetInfo CreateAsset() => new()
        {
            FileName = PackageFileName,
            Url = $"http://127.0.0.1:{port}/package.zip",
            Sha256 = new string('a', 64),
            SizeBytes = PackageBytes,
            IncludesChartInfoMetadata = false
        };

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            Task[] pending;
            lock (connections)
            {
                pending = [.. connections];
            }
            foreach (Task task in pending.Append(acceptLoop))
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException or IOException) { }
            }
            lifetime.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                Task connection = HandleAsync(client);
                lock (connections)
                {
                    connections.Add(connection);
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                string request = await ReadRequestHeadAsync(stream).ConfigureAwait(false);
                if (request.StartsWith("GET /update.json", StringComparison.Ordinal))
                {
                    UpdateAssetInfo asset = CreateAsset();
                    string manifest = "{\"version\":\"9999.0.0.0\",\"releasePageUrl\":\"http://127.0.0.1:" + port + "/release\","
                        + "\"assets\":[{\"fileName\":\"" + asset.FileName + "\",\"url\":\"" + asset.Url + "\",\"sha256\":\"" + asset.Sha256
                        + "\",\"sizeBytes\":" + asset.SizeBytes + ",\"includesChartInfoMetadata\":false}]}";
                    byte[] body = Encoding.UTF8.GetBytes(manifest);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    await stream.WriteAsync(body, lifetime.Token).ConfigureAwait(false);
                    await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    return;
                }

                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: " + PackageBytes + "\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                await stream.WriteAsync(new byte[4096], lifetime.Token).ConfigureAwait(false);
                await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                PartialBodySent.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token).ConfigureAwait(false);
            }
        }

        private async Task WriteAsync(NetworkStream stream, string text)
            => await stream.WriteAsync(Encoding.ASCII.GetBytes(text), lifetime.Token).ConfigureAwait(false);

        private async Task<string> ReadRequestHeadAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            byte[] buffer = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The update client closed before sending a complete request.");
                }
                head.Append((char)buffer[0]);
            }
            return head.ToString();
        }
    }

    private static void RunConstructorOnlyWithStatusBarTerminals(
        MainWindowProgressStatusBarTerminals terminals,
        Action<MainWindowViewModel, MainWindow, Window> test,
        IUiScheduler? uiScheduler = null)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            MainWindowViewModel viewModel = new ApplicationComposition(
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
                settingsEditSession: new NoOpSettingsEditSession(TestSettingsFactory.CreateIsolatedSettings()),
                uiScheduler: uiScheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                dialogService: UndeclaredDialogs.Create())
                .CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            ownership.Run(() =>
            {
                MainWindowPresentationTestHarness.PrepareConstructorOnlyShell(viewModel);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                MainWindow window = ownership.CreateWindow(() => new MainWindow(
                    viewModel,
                    settingsWindowCreated: null,
                    libraryReloadMenuTerminal: null,
                    regularLibraryTreeTerminal: null,
                    maintenanceTreeTerminal: null,
                    installTreeTerminal: null,
                    zeroNoteRecheckTerminal: null,

                    folderAutoRenameTerminal: null,
                    duplicateMaintenanceTerminal: null,
                    maintenanceRescanTerminal: null,
                    packageCatalogTerminal: null,
                    pendingInstallEstimationTerminal: null,
                    pendingInstallationTerminal: null,
                    installedLocationRepairTerminal: null,
                    pendingBulkMaintenanceTerminal: null,
                    mainChartCellEditTerminal: null,
                    selectedChartContextMenuTerminals: null,
                    playbackTerminal: null,
                    playlistWorkspaceTerminals: null,
                    progressStatusBarTerminals: terminals));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                object content = window.Content;
                window.Content = null;
                var host = new Window { Width = 1100, Height = 700, Resources = window.Resources, Content = content, DataContext = viewModel };
                try
                {
                    scope.ShowAndWaitForContentRendered(host);
                    test(viewModel, window, host);
                }
                finally { host.Close(); }
            });
        });
    }

    private static IReadOnlyList<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var result = new List<T>();
        Visit(root, result, new HashSet<DependencyObject>());
        return result;
    }

    private static void Visit<T>(DependencyObject current, List<T> result, HashSet<DependencyObject> visited)
        where T : DependencyObject
    {
        if (current == null || !visited.Add(current))
        {
            return;
        }
        if (current is T match)
        {
            result.Add(match);
        }
        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                Visit(VisualTreeHelper.GetChild(current, index), result, visited);
            }
        }
        foreach (object childValue in LogicalTreeHelper.GetChildren(current))
        {
            if (childValue is DependencyObject child)
            {
                Visit(child, result, visited);
            }
        }
    }

    private static T GetNamedElement<T>(MainWindow window, string name)
        where T : class
    {
        object? element = window.FindName(name);
        Assert.IsInstanceOfType(element, typeof(T), name);
        return (T)element!;
    }

    private sealed class AcceptedStatusBarTestDialogService : IUiDialogService
    {
        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BlockingLoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task serverTask;

        internal BlockingLoopbackHttpServer()
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            DownloadUri = new Uri($"http://127.0.0.1:{port}/status-bar-owner.zip");
            serverTask = HoldAcceptedRequestAsync();
        }

        internal Uri DownloadUri { get; }

        internal TaskCompletionSource<object?> RequestAccepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (SocketException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        private async Task HoldAcceptedRequestAsync()
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            byte[] requestBuffer = new byte[4096];
            int requestLength = 0;
            while (!ContainsHeaderTerminator(requestBuffer, requestLength))
            {
                int read = await stream.ReadAsync(
                    requestBuffer.AsMemory(requestLength, requestBuffer.Length - requestLength),
                    lifetime.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The playlist download client closed before sending an HTTP request.");
                }
                requestLength += read;
                if (requestLength == requestBuffer.Length && !ContainsHeaderTerminator(requestBuffer, requestLength))
                {
                    throw new IOException("The playlist download request headers exceeded the test server limit.");
                }
            }

            byte[] responseHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: application/zip\r\n"
                + "Content-Disposition: attachment; filename=status-bar-owner.zip\r\n"
                + "Content-Length: 1048576\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, lifetime.Token).ConfigureAwait(false);
            await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            RequestAccepted.TrySetResult(null);
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token).ConfigureAwait(false);
        }

        private static bool ContainsHeaderTerminator(byte[] buffer, int length)
        {
            for (int index = 3; index < length; index++)
            {
                if (buffer[index - 3] == '\r'
                    && buffer[index - 2] == '\n'
                    && buffer[index - 1] == '\r'
                    && buffer[index] == '\n')
                {
                    return true;
                }
            }
            return false;
        }
    }

}
