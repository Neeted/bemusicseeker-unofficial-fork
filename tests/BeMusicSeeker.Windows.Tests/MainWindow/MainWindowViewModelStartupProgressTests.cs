using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
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
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;
using static BeMusicSeeker.TestSupport.Wpf.StartupCompletionTestFixture;

namespace BeMusicSeeker.Tests;


/// <remarks>純粋な進捗判断はownerへ入力通知を渡して検査します。実起動・設定完了のケースはMainWindowのL/P解放・必須UI・後続scheduler接続の順序を検査するため実構成とDBを使い、Dispatcherでcallbackと終端を回収します。</remarks>
[TestClass]
public sealed class MainWindowViewModelStartupProgressTests
{
    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;


    /// <summary>Reload前3/後0の起動を実warmupまで通し、保存3の後も同ownerの全再初期化は0、新compositionは3を使うことを確認します。</summary>
    /// <remarks>実scheduler・起動接続の診断と実cache生成を観測します。設定捕捉から実MainWindow・descriptor・warmupへの接続を保証し、保守との本番順序は対象外です。既存受付portで保守要求を登録前に保持し、warmup観測後・失敗時finallyに一度だけ元schedulerへ渡して実workも回収します。保存拒否・永続化互換は設定系列へ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void StartupSortCachePriority_CapturesAfterReloadAndSurvivesFullReinitialization()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            FieldInfo loggerProvider = typeof(NLogWrapper).GetField("_FileLogger", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertFailedException("File logger provider is unavailable.");
            object? previousLogger = loggerProvider.GetValue(null);
            LoggingConfiguration? previousConfiguration = LogManager.Configuration;
            using var log = new MemoryTarget { Layout = "${message}" };
            try
            {
                var configuration = new LoggingConfiguration();
                configuration.AddRule(LogLevel.Info, LogLevel.Fatal, log);
                LogManager.Configuration = configuration;
                loggerProvider.SetValue(null, (Func<Logger>)(() => LogManager.GetLogger("startup-range-test")));
                int savedPriority;
                using (var fixture = new CompletionFixture(isolateSortWarmup: true, settingsSessionFactory: values => new PriorityReloadSettingsSession(values, savedPriority: 0, beforeReloadPriority: 3)))
                {
                    UseOwnedEmptyCatalog(fixture);
                    fixture.OptionalRelease.TrySetResult();
                    Task<bool> initial = fixture.Start();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initial, "startup-range-initial");
                    Assert.IsTrue(initial.GetAwaiter().GetResult());
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.SortWarmupCompletion.Task, "startup-range-warmup");
                    Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, fixture.SortWarmupCompletion.Task.GetAwaiter().GetResult().Kind);
                    CollectionAssert.AreEqual(new[] { "virtual_order_prewarm selected maxPriority=0 descriptorCount=0" },
                        log.Logs.Where(message => message.StartsWith("virtual_order_prewarm selected", StringComparison.Ordinal)).ToArray());
                    fixture.ReleaseDeferredMaintenance();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "startup-range-background");
                    fixture.Owner.SettingDialog.StartupSortCachePrewarmPriority = 3;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Owner.SettingDialog.StartConfirmedEnvironmentAsync(), "startup-range-save");
                    savedPriority = fixture.Settings.StartupSortCachePrewarmPriority;
                    Assert.AreEqual(3, savedPriority);
                    Assert.AreEqual(1, log.Logs.Count(message => message.StartsWith("virtual_order_prewarm selected", StringComparison.Ordinal)), "設定保存だけでwarmupを追加しません。");
                    fixture.ArmSortWarmupGate();
                    Task reinitialize = fixture.Owner.ReinitializeLibraryAsync();
                    fixture.Track(reinitialize);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reinitialize, "startup-range-reinitialize");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.SortWarmupCompletion.Task, "startup-range-reinitialize-warmup");
                    Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, fixture.SortWarmupCompletion.Task.GetAwaiter().GetResult().Kind);
                    CollectionAssert.AreEqual(new[] { "virtual_order_prewarm selected maxPriority=0 descriptorCount=0", "virtual_order_prewarm selected maxPriority=0 descriptorCount=0" },
                        log.Logs.Where(message => message.StartsWith("virtual_order_prewarm selected", StringComparison.Ordinal)).ToArray());
                    fixture.ReleaseDeferredMaintenance();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "startup-range-reinitialize-background");
                }
                using (var restarted = new CompletionFixture(isolateSortWarmup: true, settingsSessionFactory: values => new PriorityReloadSettingsSession(values, savedPriority, beforeReloadPriority: 0)))
                {
                    UseOwnedEmptyCatalog(restarted);
                    restarted.OptionalRelease.TrySetResult();
                    Task<bool> initial = restarted.Start();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initial, "startup-range-restart");
                    Assert.IsTrue(initial.GetAwaiter().GetResult());
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(restarted.SortWarmupCompletion.Task, "startup-range-restart-warmup");
                    Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, restarted.SortWarmupCompletion.Task.GetAwaiter().GetResult().Kind);
                    string[] selected = log.Logs.Where(message => message.StartsWith("virtual_order_prewarm selected", StringComparison.Ordinal)).ToArray();
                    Assert.AreEqual(3, selected.Length);
                    Assert.AreEqual("virtual_order_prewarm selected maxPriority=3 descriptorCount=78", selected[2]);
                    RegularChartListOwner regular = GetPrivateField<RegularChartListOwner>(restarted.Owner, "regularChartListOwner");
                    IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors = RegularChartListOwner.CreateVirtualOrderPrewarmDescriptors(3);
                    Assert.AreEqual(78, descriptors.Count);
                    foreach (VirtualNormalLibrarySortDescriptor descriptor in descriptors)
                    {
                        NormalLibrarySortCacheKey key = regular.CreateVirtualOrderKey(regular.SourceGeneration, regular.SortKeyGeneration,
                            descriptor.ColumnName, descriptor.Direction, 0, new RegularChartListExternalVersions(restarted.Library.ScoreSnapshotVersion,
                                restarted.Library.ChartInfoIndexVersion, restarted.Library.MaintenanceHydrationCompletedVersion));
                        Assert.IsTrue(regular.TryGetVirtualOrder(key, out _), descriptor.ColumnName + " " + descriptor.Direction);
                    }
                    restarted.ReleaseDeferredMaintenance();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(restarted.WaitForBackgroundTerminalAsync(), "startup-range-restart-background");
                }
            }
            finally
            {
                loggerProvider.SetValue(null, previousLogger);
                LogManager.Configuration = previousConfiguration;
            }
        });
    }

    /// <summary>範囲外の保存値でも実必須起動は成功・操作解禁し、後続warmupだけをvirtual_orderの元例外付きFailedとして終えます。</summary>
    /// <remarks>保守との本番順序は対象外とし、既存受付portで保守要求を登録前に保持します。warmup観測後・失敗時finallyに元schedulerへ一度だけ渡して実workを回収し、実必須起動とdescriptor失敗の接続を観測します。</remarks>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(4)]
    [DoNotParallelize]
    public void StartupSortCachePriority_InvalidValueFailsOnlyOptionalWarmup(int priority)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture(isolateSortWarmup: true, settingsSessionFactory: values => new PriorityReloadSettingsSession(values, priority, 1));
            UseOwnedEmptyCatalog(fixture);
            fixture.OptionalRelease.TrySetResult();
            StartupPostInitializationWarmupOwner warmup = GetPrivateField<StartupPostInitializationWarmupOwner>(fixture.Owner, "startupPostInitializationWarmupOwner");
            FieldInfo callbackField = typeof(StartupPostInitializationWarmupOwner).GetField("completed", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertFailedException("Warmup completion callback is unavailable.");
            var original = (Action<StartupPostInitializationWarmupCompletion>)(callbackField.GetValue(warmup)
                ?? throw new AssertFailedException("Warmup completion callback is missing."));
            var completions = new List<StartupPostInitializationWarmupCompletion>();
            // 実接続を置換せず、既存の終端callbackへ到達した結果を観測します。fixtureの終了まで同じownerが所有します。
            callbackField.SetValue(warmup, (Action<StartupPostInitializationWarmupCompletion>)(completion => { completions.Add(completion); original(completion); }));
            Task<bool> initialization = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "invalid-range-required-startup");
            Assert.IsTrue(initialization.GetAwaiter().GetResult());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.SortWarmupCompletion.Task, "invalid-range-warmup-terminal");
            fixture.ReleaseDeferredMaintenance();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "invalid-range-optional-terminal");
            Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
            Assert.IsTrue(fixture.Owner.HasActiveLibraryProfile);
            Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.AreEqual(priority, fixture.Settings.StartupSortCachePrewarmPriority);
            Assert.AreEqual(1, completions.Count);
            Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Failed, completions[0].Kind);
            Assert.AreEqual("virtual_order", completions[0].FailedStage);
            Assert.IsInstanceOfType<ArgumentOutOfRangeException>(completions[0].Exception);
            RegularChartListOwner regular = GetPrivateField<RegularChartListOwner>(fixture.Owner, "regularChartListOwner");
            Assert.IsFalse(regular.IsVirtualOrderPrewarmRunning);
        });
    }

    /// <summary>取得失敗を扱わないケースだけに専有の空catalogを渡し、実file取得・解析とfixtureの全Task終端後の回収を保ちます。</summary>
    private static void UseOwnedEmptyCatalog(CompletionFixture fixture)
    {
        string path = Path.Combine(fixture.Composition.ApplicationPathSnapshot.DataDirectoryPath, "empty-table-list.json");
        File.WriteAllText(path, "[]");
        fixture.Settings.TableListURL = new Uri(path);
    }

    /// <summary>Reload前後を意図的に変え、起動捕捉の時期を識別します。保存は次の構成へ渡す確定値を保持します。</summary>
    private sealed class PriorityReloadSettingsSession : ISettingsEditSession
    {
        private int savedPriority;
        public Settings Values { get; }
        internal PriorityReloadSettingsSession(Settings values, int savedPriority, int beforeReloadPriority)
        {
            Values = values;
            this.savedPriority = savedPriority;
            Values.StartupSortCachePrewarmPriority = beforeReloadPriority;
        }
        public void Reload() => Values.StartupSortCachePrewarmPriority = savedPriority;
        public void Save() => savedPriority = Values.StartupSortCachePrewarmPriority;
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string identity) => throw new AssertFailedException("Unexpected mode restart.");
    }

    /// <summary>実起動の必須UI前はHTTP未開始で実設定Windowの開始を無効にし、成功公開後は元の起動TaskとWindowが存続したまま開始表示を閉じます。通知とHTTP本文を保持しても両索引準備が始まり、catalog登録・受付識別・実親終端を標準接続で確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupRequiredUi_ActualTaskOwnsCompletionBeforeOptionalWorkStarts()
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        var httpTasks = new List<Task>();
        void AwaitHttpTask(Task task, string name)
        {
            httpTasks.Add(task);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(task.WaitAsync(httpOperation.Token), name);
        }
        void AwaitHttpArrival(Task phase, Task operation, string name)
            => AwaitHttpTask(TestUiDispatcherHost.AwaitNotificationAsync(phase, operation, name), name);

        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var fixture = new CompletionFixture(ownHttpSettings: true);
            SettingsWindow? settingsWindow = null;
            SingleRequestHttpServer? server = null;
            var noticeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var noticeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var playlistIndexEntered = new TaskCompletionSource<StartupBackgroundTaskProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var chartIndexEntered = new TaskCompletionSource<StartupBackgroundTaskProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
            var catalogTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveRequest(StartupBackgroundTaskProgressSnapshot status)
            {
                requests.Enqueue(status);
                if (status.Name == "external_table_catalog" && !status.IsRunning) { catalogTerminal.TrySetResult(); }
                if (!status.IsRunning) { return; }
                if (status.Name == "playlist_library_index_prewarm") { playlistIndexEntered.TrySetResult(status); }
                if (status.Name == "playlist_virtual_order_prewarm") { chartIndexEntered.TrySetResult(status); }
            }
            fixture.Scheduler.ProgressChanged += ObserveRequest;
            fixture.Dialogs.MessageObservedAsync = _ => { noticeEntered.TrySetResult(); return noticeRelease.Task; };
            fixture.Ui.Arm();
            Exception? httpPrimaryFailure = null;
            try
            {
                server = new SingleRequestHttpServer(httpOperation.Token, System.Text.Encoding.UTF8.GetBytes("[]"), holdBody: true);
                fixture.Settings.TableListURL = server.Address;
                Task<bool> initialization = fixture.Start();
                AwaitHttpArrival(fixture.Ui.Entered.Task, initialization, "startup-required-ui");
                fixture.AssertBeforeUiCompletion();
                SettingsDialogViewModel settings = fixture.Owner.SettingDialog;
                settingsWindow = new SettingsWindow(fixture.Dialogs) { DataContext = settings, PlaybackPanel = fixture.Owner.PlaybackPanel };
                scope.ShowAndWaitForContentRendered(settingsWindow);
                var start = (System.Windows.Controls.Button)settingsWindow.FindName("buttonOK");
                Assert.IsTrue(settingsWindow.IsVisible);
                Assert.AreEqual(Visibility.Visible, start.Visibility);
                Assert.IsFalse(start.IsEnabled);
                var captured = EnvironmentSettingsChange.Capture(fixture.Settings);
                string confirmedBeatoraja = fixture.Settings.BeatorajaRootPath;
                AwaitHttpTask(settings.StartConfirmedEnvironmentAsync(), "startup-ui-reject-duplicate-start");
                AwaitHttpTask(settings.SelectOperationModeAsync(!captured.OperationModeLR2DB), "startup-ui-reject-mode");
                AwaitHttpTask(settings.SelectLr2RootAsync(fixture.Settings.LR2RootPath), "startup-ui-reject-root");
                settings.BeatorajaRootPath = Path.Combine(fixture.Settings.BMSRootPath, "other-beatoraja");
                AwaitHttpTask(settings.CommitScoreSettingsAsync(), "startup-ui-reject-beatoraja");
                Assert.AreEqual(captured, EnvironmentSettingsChange.Capture(fixture.Settings));
                Assert.AreEqual(confirmedBeatoraja, fixture.Settings.BeatorajaRootPath);
                Assert.IsFalse(settings.CanStartEnvironment);
                Assert.IsFalse(settings.IsEnvironmentSelectionEnabled);
                settings.ShowNewPackageInstallConfirmMsg = !fixture.Settings.ShowNewPackageInstallConfirmMsg;
                Assert.IsFalse(initialization.IsCompleted);
                Assert.AreEqual(string.Empty, server.RequestMethod);
                fixture.Ui.Release.TrySetResult();
                AwaitHttpArrival(noticeEntered.Task, initialization, "startup-notice-held");
                AwaitHttpTask(server.WaitForPhaseAsync(server.HeadersSent.Task, "catalog HTTP before notice completion", catalogTerminal.Task, initialization), "catalog-http-arrival");
                Assert.IsFalse(initialization.IsCompleted, "InitializeAsyncは実通知Taskを引き続き待ちます。");
                Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(settingsWindow.IsVisible, "通常起動成功は開いている設定Windowを閉じません。");
                Assert.AreEqual(Visibility.Collapsed, start.Visibility, "成功公開の実通知で同じ開始buttonの表示を更新します。");
                Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                Assert.IsTrue(fixture.Settings.SkipInitPlaylistLoad);
                StartupAdditionalProgressSnapshot additional = fixture.Scheduler.CaptureAdditionalProgressSnapshot();
                Assert.AreNotEqual(0, additional.OperationToken);
                StartupBackgroundTaskProgressSnapshot catalog = requests.Single(status => status.Name == "external_table_catalog" && status.IsRunning);
                Assert.AreEqual(additional.Generation, catalog.Request.Generation);
                Assert.AreEqual(additional.OperationToken, catalog.Request.OperationToken);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                OperationProgressRow child = fixture.Owner.ProgressHub.Rows.Single(row => row.Key.StartsWith("background:external_table_catalog"));
                Assert.AreEqual("startup_background", child.ParentKey);
                Task backgroundTerminal = fixture.WaitForBackgroundTerminalAsync();
                AwaitHttpArrival(Task.WhenAll(playlistIndexEntered.Task, chartIndexEntered.Task), backgroundTerminal, "startup-indexes-before-http-release");
                Assert.IsFalse(server.ReleaseBody.Task.IsCompleted);
                Assert.IsFalse(requests.Any(status => status.Name == "external_table_catalog" && !status.IsRunning), "両索引はHTTP実終端を待たずに開始します。");
                foreach (StartupBackgroundTaskProgressSnapshot index in new[] { playlistIndexEntered.Task.GetAwaiter().GetResult(), chartIndexEntered.Task.GetAwaiter().GetResult() })
                {
                    Assert.AreEqual(additional.OperationToken, index.Request.OperationToken);
                    Assert.AreEqual(additional.Generation, index.Request.Generation);
                }
                noticeRelease.TrySetResult();
                AwaitHttpTask(initialization, "startup-required-ui-terminal");
                Assert.IsTrue(initialization.GetAwaiter().GetResult());
                Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
                Assert.IsTrue(fixture.Owner.HasActiveLibraryProfile);
                Assert.IsFalse(fixture.Lifetime.IsFirstStartup);
                Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                Assert.IsFalse(fixture.Owner.IsLibraryOperationInProgress, "実成功公開後の通知・任意work・完了余韻でBusyを延ばしません。");
                AwaitHttpArrival(fixture.OptionalEntered.Task, fixture.OptionalWork, "startup-optional-arrival");
                Assert.IsTrue(fixture.OptionalStartedAfterCompletion);
                Assert.IsFalse(fixture.OptionalWork.IsCompleted, "任意workを保持したまま必須起動が終端します。");
                Assert.AreEqual(1, fixture.CompletionNotifications);
                server.ReleaseBody.TrySetResult();
                fixture.OptionalRelease.TrySetResult();
                AwaitHttpTask(backgroundTerminal, "startup-own-parent-terminal");
                Assert.IsFalse(fixture.Owner.ProgressHub.IsStartupBackgroundInitializationActive);
            }
            catch (Exception httpPrimary) { httpPrimaryFailure = httpPrimary; throw; }
            finally
            {
                using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var httpCleanupFailures = new List<Exception>();
                fixture.Scheduler.ProgressChanged -= ObserveRequest;
                noticeRelease.TrySetResult();
                fixture.Ui.Release.TrySetResult();
                fixture.OptionalRelease.TrySetResult();
                fixture.Owner.PlaylistWorkspace.CancelExternalTableCollectionLoadForShutdown();
                Task? shutdown = null;
                try { shutdown = fixture.Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(); }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                httpOperation.Cancel();
                Task[] ownedHttpTasks = httpTasks.Concat(new Task?[] { shutdown, server?.Completion }.OfType<Task>()).ToArray();
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token,
                        httpPrimaryFailure, httpTasks.Concat(new Task?[] { shutdown }.OfType<Task>()), server == null ? [] : [server]), "startup-http-cleanup");
                }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                try { fixture.Dispose(httpCleanup.Token, ownedHttpTasks, settingsWindow); }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                if (httpCleanupFailures.Count > 0)
                {
                    if (httpPrimaryFailure != null) { httpCleanupFailures.Insert(0, httpPrimaryFailure); }
                    throw new AggregateException(httpCleanupFailures);
                }
            }
        });
    }

    /// <summary>ツリー全再初期化はL/P受付解放後も必須UIの終端まで操作禁止を保ち、その終端後だけ後続登録を閉鎖して開始します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void TreeFullReinitialization_RequiredUiOwnsTerminalBeforeOptionalRegistration()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture();
            UseOwnedEmptyCatalog(fixture);
            fixture.OptionalRelease.TrySetResult();
            Task<bool> initial = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(initial, "tree-full-initial");
            Assert.IsTrue(initial.GetAwaiter().GetResult());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "tree-full-prior-optional");
            int publications = fixture.CompletionNotifications;
            fixture.Ui.Arm();
            Task reinitialize = fixture.Owner.ReinitializeLibraryAsync();
            fixture.Track(reinitialize);
            AwaitArrival(fixture.Ui.Entered.Task, reinitialize, "tree-full-required-ui");
            Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
            Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
            Assert.IsFalse(reinitialize.IsCompleted);
            Assert.IsFalse(fixture.Scheduler.IsStarted);
            Assert.IsFalse(fixture.Scheduler.IsPostInitializationSchedulingComplete);
            Assert.AreEqual(publications, fixture.CompletionNotifications);
            Assert.IsTrue(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            fixture.Ui.Release.TrySetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(reinitialize, "tree-full-terminal");
            Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsTrue(fixture.Scheduler.IsPostInitializationSchedulingComplete);
            Assert.IsTrue(fixture.Scheduler.IsStarted);
            Assert.IsFalse(fixture.Library.IsShutdownRequested);
            Assert.IsFalse(fixture.Playlist.IsShutdownRequested);
        });
    }

    /// <summary>起動任意workの表示中に別の再読込みを開始すると起動表示latchを解除し、古い起動進捗を残しません。</summary>
    [TestMethod]
    public void NonStartupProgressOperation_ResetsBackgroundPresentationLatch()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        try
        {
            owner.ProgressHub.BeginStartupBackgroundInitializationPresentation(1, 1);
            Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive);

            StartupProgressWorkflowOwner progress = owner.ProgressHub.StartupProgress;
            progress.StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff);
            progress.ApplyPresentation(false, null, null, 0.0, 1.0);

            Assert.IsFalse(owner.ProgressHub.IsStartupBackgroundInitializationActive);
        }
        finally
        {
            owner.SettingDialog.Dispose();
        }
    }

    /// <summary>実入口のUI失敗を親解放後に通知し、通知失敗でも元原因・cleanupを保持して次の明示初期化を受理します。</summary>
    [TestMethod]
    [DataRow(false)] // 元の必須UI失敗を通常の通知先へ渡します。
    [DataRow(true)] // 通知先の二次失敗でも元原因と再受付を保ちます。
    [DoNotParallelize]
    public void StartupRequiredUi_RealFailurePreservesPrimaryCauseAndNextAdmission(bool presenterThrows)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var failure = new InvalidOperationException("required UI failed");
            var recorded = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new CompletionFixture(new CallbackFailurePresenter(presentation =>
            {
                recorded.Add(presentation);
                if (presenterThrows) { throw new InvalidOperationException("failure presenter failed"); }
            }));
            UseOwnedEmptyCatalog(fixture);
            bool failUi = true;
            fixture.Owner.RequiredStartupUiApplying += () => { if (failUi) { throw failure; } };
            fixture.Ui.Arm();
            Task<bool> initialization = fixture.Start();
            AwaitArrival(fixture.Ui.Entered.Task, initialization, "startup-ui-failure-arrival");
            fixture.Ui.Release.TrySetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-ui-failure-terminal");
            Assert.IsFalse(initialization.GetAwaiter().GetResult());
            Assert.AreEqual(1, recorded.Count);
            Assert.AreSame(failure, recorded[0].Exception);
            Assert.AreEqual(0, fixture.Presentation.Requests.Count);
            Assert.IsTrue(fixture.Owner.ProgressHub.StartupProgress.IsFailed);
            Assert.IsTrue(fixture.Owner.ProgressHub.StartupProgress.IsRetryableFailure);
            StringAssert.Contains(fixture.Owner.ProgressHub.StartupProgress.SubLabel, failure.Message);
            Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
            Assert.IsFalse(fixture.Owner.HasActiveLibraryProfile);
            Assert.IsTrue(fixture.Lifetime.IsFirstStartup);
            Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
            Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
            Assert.IsFalse(fixture.Scheduler.IsStarted);
            failUi = false;
            Task<bool> retry = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(retry, "startup-ui-failure-next-explicit");
            Assert.IsTrue(retry.GetAwaiter().GetResult());
            Assert.AreEqual(1, fixture.CompletionNotifications);
        });
    }

    /// <summary>実Score処理・親解放後の必須UIの失敗をOperationFailedで返し、設定不備や成功にせず、元原因・解放・次の明示要求を保持します。</summary>
    [TestMethod]
    [DataRow(false)] // 実モデルへ渡した設定snapshot読取りが処理段階で失敗します。
    [DataRow(true)] // ローカル処理成功後の必須UI Taskが失敗します。
    [DoNotParallelize]
    public void LimitedInitialization_ActualScoreFailureUsesOperationFailedWithoutSettingsGuidance(bool uiFails)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture(lifetime: new TestApplicationLifetime(firstStartup: false));
            UseOwnedEmptyCatalog(fixture);
            fixture.OptionalRelease.TrySetResult();
            Task<bool> startup = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "limited-score-initial");
            Assert.IsTrue(startup.GetAwaiter().GetResult());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "limited-score-prior-background");
            var failure = new IOException("required score operation failed");
            ISettingsDialogStatePort port = fixture.Owner;
            StartupInitializationResult result;
            Assert.IsTrue(fixture.Composition.OperationAdmission.TryEnter(out IDisposable admission));
            using (admission)
            using (LibraryFileMutationCapability capability = fixture.Composition.OperationAdmission.CreateMutationCapability(admission))
            {
                if (!uiFails) { fixture.OptionsFailure = failure; }
                Task<StartupInitializationResult> core = port.ReloadScoresOnlyAsync(capability);
                fixture.Track(core);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(core, "limited-score-core");
                result = core.GetAwaiter().GetResult();
                Assert.AreEqual(uiFails ? StartupInitializationOutcome.Succeeded : StartupInitializationOutcome.OperationFailed, result.Outcome);
                Assert.IsNull(result.ValidationFailure);
                Assert.IsFalse(result.CompletionPublished);
                if (!uiFails) { Assert.AreSame(failure, result.Failure); }
            }
            fixture.OptionsFailure = null;
            fixture.Ui.IsCompletionBoundary = () => !fixture.Composition.OperationAdmission.IsActive && !fixture.Composition.PlaylistOperationAdmission.IsActive;
            if (uiFails) { fixture.Ui.Arm(); }
            Task<StartupInitializationResult> completion = port.CompleteRequiredInitializationAfterAdmissionAsync(result);
            fixture.Track(completion);
            if (uiFails)
            {
                AwaitArrival(fixture.Ui.Entered.Task, completion, "limited-score-required-ui");
                Assert.IsFalse(completion.IsCompleted);
                fixture.Ui.Release.TrySetException(failure);
            }
            TestUiDispatcherHost.AwaitTaskOnDispatcher(completion, "limited-score-failure-terminal");
            result = completion.GetAwaiter().GetResult();
            Assert.AreEqual(StartupInitializationOutcome.OperationFailed, result.Outcome);
            Assert.AreSame(failure, result.Failure);
            Assert.IsNull(result.ValidationFailure);
            Assert.IsFalse(result.CompletionPublished);
            Assert.AreEqual(0, fixture.Presentation.Requests.Count);
            Assert.IsFalse(fixture.Owner.IsLibraryOperationInProgress);
            Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
            Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
            Task retry = fixture.Owner.ReloadScoresOnlyAsync();
            fixture.Track(retry);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(retry, "limited-score-next-explicit");
            Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsFailed);
            Assert.AreEqual(0, fixture.Presentation.Requests.Count);
        });
    }

    /// <summary>実表再読込みの必須UIが失敗した場合は成功を公開せず元原因を返し、Pを解放しても安全性未確認の進捗を再試行可へ読み替えません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void ReloadTables_RequiredPublicationFailureRetainsExistingSafetyStop()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture(lifetime: new TestApplicationLifetime(firstStartup: false));
            UseOwnedEmptyCatalog(fixture);
            fixture.OptionalRelease.TrySetResult();
            Task<bool> startup = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "reload-tables-safety-initial");
            Assert.IsTrue(startup.GetAwaiter().GetResult());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "reload-tables-safety-prior-background");
            var failure = new IOException("required playlist publication failed");
            fixture.Ui.IsCompletionBoundary = () => !fixture.Composition.PlaylistOperationAdmission.IsActive && fixture.Owner.IsLibraryOperationInProgress;
            fixture.Ui.Arm();
            Task reload = fixture.Owner.ReloadTablesAsync();
            try
            {
                AwaitArrival(fixture.Ui.Entered.Task, reload, "reload-tables-safety-required-ui");
                Assert.IsFalse(reload.IsCompleted);
                Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
                fixture.Ui.Release.TrySetException(failure);
                IOException actual = Assert.ThrowsExactly<IOException>(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(reload, "reload-tables-safety-terminal"));
                Assert.AreSame(failure, actual);
                Assert.IsTrue(fixture.Owner.ProgressHub.StartupProgress.IsFailed);
                Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsRetryableFailure);
                Assert.IsTrue(fixture.Owner.IsLibraryOperationInProgress);
                Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
                Assert.AreEqual(0, fixture.Presentation.Requests.Count);
            }
            finally
            {
                fixture.Ui.Release.TrySetResult();
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(reload, "reload-tables-safety-cleanup"); }
                catch (IOException actual) when (ReferenceEquals(actual, failure))
                {
                    // 期待する元失敗は主表明で検査し、cleanupは実Taskの回収だけを担当します。
                }
            }
        });
    }

    /// <summary>解禁後の初回案内が失敗してもAPI・必須進捗・初回完了を成功のまま保ち、通知の実終端と局所診断を待ってから任意workを開始します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupCompletionNotice_FailureKeepsPublishedSuccessAndUnblockedOperations()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, "startup-notification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var log = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
        LoggingConfiguration? previousConfiguration = LogManager.Configuration;
        try
        {
            NLogWrapper.ConfigureApplicationFileLogging(Path.Combine(directory, "log"), LogLevel.Info, enableInstallPerformanceLogging: false);
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, log);
            LogManager.Configuration = configuration;
            TestUiDispatcherHost.Invoke(() =>
            {
                var failure = new IOException("initial completion notice failed");
                var recorded = new List<StartupLibraryInitializationFailurePresentation>();
                using var fixture = new CompletionFixture(new CallbackFailurePresenter(recorded.Add));
                UseOwnedEmptyCatalog(fixture);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var observed = new List<(string Message, bool LibraryBusy, bool PlaylistBusy, bool Initialized, bool ActiveProfile, bool FirstStartup, bool UiBlocked)>();
                fixture.Dialogs.MessageObservedAsync = request =>
                {
                    observed.Add((request.MessageBoxText, fixture.Composition.OperationAdmission.IsActive,
                        fixture.Composition.PlaylistOperationAdmission.IsActive, fixture.Owner.IsInitializationCompleted,
                        fixture.Owner.HasActiveLibraryProfile, fixture.Lifetime.IsFirstStartup,
                        fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked));
                    entered.TrySetResult();
                    return release.Task;
                };
                Task optionalWork = fixture.OptionalWork;
                Task optionalEntered = fixture.OptionalEntered.Task;
                Task<bool> initialization = fixture.Start();
                try
                {
                    AwaitArrival(entered.Task, initialization, "startup-completion-notice-entered");
                    Assert.IsFalse(initialization.IsCompleted, "表示の成否を分離しても実通知Taskの待機は省略しません。");
                    AwaitArrival(optionalEntered, optionalWork, "startup-notice-pending-optional-arrival");
                    Assert.IsTrue(fixture.OptionalStartedAfterCompletion);
                    release.TrySetException(failure);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-initial-completion-notice-failure");
                    Assert.IsTrue(initialization.GetAwaiter().GetResult(), "完了案内だけの失敗を必須初期化の失敗にしません。");
                    Assert.AreEqual(1, observed.Count);
                    Assert.AreEqual((Resources.Msg_init_completed, false, false, true, true, false, false), observed[0]);
                    Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
                    Assert.IsTrue(fixture.Owner.HasActiveLibraryProfile);
                    Assert.IsFalse(fixture.Lifetime.IsFirstStartup);
                    Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                    Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsFailed);
                    Assert.AreEqual(1, fixture.CompletionNotifications);
                    Assert.AreEqual(0, recorded.Count, "案内の失敗から初期化失敗の再通知を始めません。");
                    LogManager.Flush();
                    string diagnostic = log.Logs.Single(line => line.StartsWith("startup_notification_failed ", StringComparison.Ordinal));
                    StringAssert.Contains(diagnostic, failure.Message);
                    StringAssert.Contains(diagnostic, nameof(IOException));
                    Assert.IsTrue(fixture.Scheduler.IsStarted);
                    Assert.IsTrue(fixture.Scheduler.IsPostInitializationSchedulingComplete);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(optionalEntered, optionalWork,
                        "startup-notice-failure-optional-entered"), "startup-notice-failure-optional-arrival");
                    Assert.IsTrue(fixture.OptionalStartedAfterCompletion);
                    fixture.OptionalRelease.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(optionalWork, "startup-notice-failure-optional-terminal");
                }
                finally
                {
                    release.TrySetResult();
                    fixture.OptionalRelease.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-notice-cleanup");
                }
            });
        }
        finally
        {
            LogManager.Flush();
            LogManager.Configuration = previousConfiguration;
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>親解放後のUI待機で終了が先着した場合だけ取消へ分類します。実UI障害の原因通知と登録済み表示Taskの終端を待ち、古い表示を含む反映後も失敗原因を保持します。</summary>
    [TestMethod]
    [DataRow(false)] // 終了による取消だけなら進捗を実失敗へ分類しません。
    [DataRow(true)] // 終了中に生じた実UI失敗は取消へ置換しません。
    [DoNotParallelize]
    public void StartupRequiredUi_ShutdownWhileAwaitingKeepsRealFailureDistinct(bool realFailure)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var recorded = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new CompletionFixture(new CallbackFailurePresenter(recorded.Add));
            UseOwnedEmptyCatalog(fixture);
            fixture.Ui.Arm();
            Task<bool> initialization = fixture.Start();
            AwaitArrival(fixture.Ui.Entered.Task, initialization, "startup-ui-shutdown-arrival");
            fixture.AssertBeforeUiCompletion();
            Task shutdown = fixture.Owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
            fixture.Track(shutdown);
            var failure = new IOException("real UI failure during shutdown");
            var causePresented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            StartupProgressWorkflowOwner progress = fixture.Owner.ProgressHub.StartupProgress;
            void PresentationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if ((string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(StartupProgressWorkflowOwner.SubLabel))
                    && progress.SubLabel.Contains(failure.Message, StringComparison.Ordinal)) { causePresented.TrySetResult(); }
            }
            progress.PropertyChanged += PresentationChanged;
            try
            {
                if (realFailure) { fixture.Ui.Release.TrySetException(failure); }
                else { fixture.Ui.Release.TrySetResult(); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(initialization, shutdown), "startup-ui-shutdown-terminal");
                Task presentation = fixture.Ui.JoinScheduledOperationsAsync();
                if (realFailure)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(causePresented.Task, presentation,
                        "startup-ui-shutdown-failure-presentation"), "startup-ui-shutdown-failure-notification");
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(presentation, "startup-ui-shutdown-presentation-terminal");
                Assert.IsFalse(initialization.GetAwaiter().GetResult());
                Assert.IsTrue(fixture.Lifetime.IsFirstStartup);
                Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
                Assert.IsFalse(fixture.Owner.HasActiveLibraryProfile);
                Assert.IsFalse(fixture.Scheduler.IsPostInitializationSchedulingComplete);
                Assert.IsFalse(fixture.OptionalEntered.Task.IsCompleted, "受理済み必須cleanupのdrainは許可し、任意workは開始しません。");
                Assert.IsTrue(fixture.Scheduler.IsFullyIdle);
                Assert.AreEqual(0, recorded.Count, "終了開始後に新しい失敗画面を開きません。");
                Assert.AreEqual(realFailure, progress.IsFailed);
                if (realFailure) { StringAssert.Contains(progress.SubLabel, failure.Message); }
                Assert.AreEqual(0, fixture.CompletionNotifications);
            }
            finally
            {
                fixture.Ui.Release.TrySetResult();
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(initialization, shutdown), "startup-ui-shutdown-task-cleanup"); }
                finally
                {
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Ui.JoinScheduledOperationsAsync(), "startup-ui-shutdown-presentation-cleanup"); }
                    finally { progress.PropertyChanged -= PresentationChanged; }
                }
            }
        });
    }

    /// <summary>実player置換をawait中の終了は構成失敗へ読み替えず、開始済み置換Taskと終了Taskを両方回収します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupPlayerReplacement_ShutdownWaitsForActualReplacementAndKeepsFirstStartup()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new FakeBmsPlayer { BeforeClose = () => { entered.TrySetResult(); release.Wait(); } };
            var failures = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new CompletionFixture(new CallbackFailurePresenter(failures.Add), () => player);
            UseOwnedEmptyCatalog(fixture);
            fixture.Settings.UsePlayeruBMplay = true;
            fixture.Settings.uBMplayPath = Path.Combine(fixture.Composition.ApplicationPathSnapshot.DataDirectoryPath, "uBMplay.exe");
            File.WriteAllBytes(fixture.Settings.uBMplayPath, []);
            Task<bool>? initialization = null;
            try
            {
                initialization = fixture.Start();
                AwaitArrival(entered.Task, initialization, "startup-player-replacement-close");
                Assert.IsTrue(fixture.Composition.OperationAdmission.IsActive);
                Assert.IsTrue(fixture.Composition.PlaylistOperationAdmission.IsActive);
                Assert.IsFalse(initialization.IsCompleted);
                Task shutdown = fixture.Owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                fixture.Track(shutdown);
                Assert.IsFalse(shutdown.IsCompleted);
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(initialization, shutdown), "startup-player-replacement-shutdown");
                Assert.IsFalse(initialization.GetAwaiter().GetResult());
                Assert.IsTrue(fixture.Lifetime.IsFirstStartup);
                Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
                Assert.IsFalse(fixture.Scheduler.IsStarted);
                Assert.AreEqual(0, failures.Count);
                Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
                Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
            }
            finally
            {
                release.Set();
                if (initialization != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-player-replacement-cleanup"); }
            }
        });
    }

    /// <summary>受付外の実修復警告から次の明示起動が開始しても、旧結果は新tokenの必須UIや解禁を完了しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupRepairWarning_ReentryCannotPublishCompletionForNewOperation()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture(lr2Mode: true);
            UseOwnedEmptyCatalog(fixture);
            Assert.IsTrue(fixture.Owner.SettingDialog.CheckValidationForInitialization(out string validationFailure), validationFailure);
            string folder = Path.Combine(fixture.Settings.BMSInstallDir, "Leap");
            Directory.CreateDirectory(folder);
            Directory.SetLastWriteTime(folder, new DateTime(2024, 2, 29, 12, 0, 0));
            using (LR2SongDBExtended db = fixture.Composition.DatabaseScope.OpenSongWrite(fixture.Library.InitializationSongDbPath))
            {
                db.InsertOrReplace(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, title = "Leap", parent = "e2977170", type = 1, date = null, adddate = 0 });
            }
            fixture.Dialogs.ConfirmationResult = MessageBoxResult.No;
            Task<bool>? next = null;
            bool observed = false;
            fixture.Dialogs.MessageObservedAsync = async request =>
            {
                if (request.MessageBoxText != Resources.Warn_LR2LeapYearBugDetected || observed) { return; }
                Assert.AreEqual(1, fixture.Dialogs.ConfirmationCount);
                observed = true;
                // 通知前から開始した旧後続を回収し、新規L/Pを受理できる条件で再入します。
                fixture.OptionalRelease.TrySetResult();
                await fixture.WaitForBackgroundTerminalAsync();
                Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
                Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
                fixture.Ui.Arm();
                next = fixture.Start();
                await TestUiDispatcherHost.AwaitNotificationAsync(fixture.Ui.Entered.Task, next, "startup-warning-reentrant-ui");
            };
            Task<bool> previous = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(previous, "startup-warning-old-terminal");
            Assert.IsTrue(observed);
            Assert.IsNotNull(next);
            Assert.IsTrue(previous.GetAwaiter().GetResult(), "旧要求は実UIと成功公開後に正常通知を終えています。");
            Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
            Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
            Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
            Assert.IsTrue(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsFalse(fixture.Scheduler.IsStarted);
            Assert.IsFalse(fixture.Scheduler.IsPostInitializationSchedulingComplete);
            Assert.IsFalse(fixture.Lifetime.IsFirstStartup);
            Assert.AreEqual(1, fixture.CompletionNotifications);
            Assert.IsFalse(next.IsCompleted);
            fixture.Ui.Release.TrySetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(next, "startup-warning-new-terminal");
            Assert.IsTrue(next.GetAwaiter().GetResult());
            Assert.AreEqual(2, fixture.CompletionNotifications);
        });
    }

    /// <summary>実起動から業務token=0の外部同期を開始してHTTPを保持し、専用件数通知が受付時の起動識別を保って追加親の一行だけになることを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupExternalSync_DedicatedProgressKeepsStartupOriginWithBusinessTokenZero()
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        var httpTasks = new List<Task>();
        void AwaitHttpTask(Task task, string name)
        {
            httpTasks.Add(task);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(task.WaitAsync(httpOperation.Token), name);
        }

        TestUiDispatcherHost.Invoke(() =>
        {
            var fixture = new CompletionFixture(ownHttpSettings: true);
            SingleRequestHttpServer? server = null;
            fixture.Settings.SkipInitPlaylistLoad = false;
            var queued = new TaskCompletionSource<PlaylistExternalSyncRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Owner.PlaylistWorkspace.PlaylistExternalSyncQueued += (_, args) => queued.TrySetResult(args);
            var syncTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Owner.PlaylistWorkspace.PlaylistExternalSyncCompleted += (_, _) => syncTerminal.TrySetResult();
            Exception? httpPrimaryFailure = null;
            try
            {
                server = new SingleRequestHttpServer(httpOperation.Token, System.Text.Encoding.UTF8.GetBytes("{}"), holdBody: true);
                using (LR2SongDBExtended db = fixture.Composition.DatabaseScope.OpenSongWrite(fixture.Library.InitializationSongDbPath))
                {
                    db.Insert(new LR2SongDBExtended.playlist
                    {
                        name = "startup sync origin",
                        header_url = server.Address.AbsoluteUri,
                        is_external_sync = true,
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders,
                        output_dir = "Network"
                    });
                }
                Task<bool> startup = fixture.Start();

                AwaitHttpTask(startup, "external-sync-origin-startup");
                Assert.IsTrue(startup.GetAwaiter().GetResult());
                AwaitHttpTask(server.WaitForPhaseAsync(server.HeadersSent.Task, "external sync HTTP", syncTerminal.Task, startup), "external-sync-http");
                Assert.IsTrue(queued.Task.IsCompletedSuccessfully);
                PlaylistExternalSyncRequestEventArgs accepted = queued.Task.GetAwaiter().GetResult();
                StartupAdditionalProgressSnapshot additional = fixture.Scheduler.CaptureAdditionalProgressSnapshot();
                Assert.AreEqual(0, accepted.OperationToken);
                Assert.IsNotNull(accepted.ProgressRequest);
                Assert.AreEqual(additional.OperationToken, accepted.ProgressRequest.OperationToken);
                Assert.AreNotEqual(0, accepted.ProgressRequest.OperationToken);
                Assert.AreEqual(additional.Generation, accepted.ProgressRequest.Generation);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                OperationProgressRow[] rows = fixture.Owner.ProgressHub.Rows.Where(row =>
                    row.Key.StartsWith("playlist:external_playlist_sync") || row.Key.StartsWith("background:external_playlist_sync")).ToArray();
                Assert.AreEqual(1, rows.Length);
                Assert.IsTrue(rows[0].Key.StartsWith("playlist:"));
                Assert.AreEqual("startup_background", rows[0].ParentKey);
                Assert.AreEqual(1d, rows[0].Maximum);
            }
            catch (Exception httpPrimary) { httpPrimaryFailure = httpPrimary; throw; }
            finally
            {
                using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var httpCleanupFailures = new List<Exception>();
                fixture.OptionalRelease.TrySetResult();
                Task? shutdown = null;
                try { shutdown = fixture.Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(); }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                httpOperation.Cancel();
                Task[] ownedHttpTasks = httpTasks.Concat(new Task?[] { shutdown, server?.Completion }.OfType<Task>()).ToArray();
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token,
                        httpPrimaryFailure, httpTasks.Concat(new Task?[] { shutdown }.OfType<Task>()), server == null ? [] : [server]), "startup-http-cleanup");
                }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                try { fixture.Dispose(httpCleanup.Token, ownedHttpTasks); }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                if (httpCleanupFailures.Count > 0)
                {
                    if (httpPrimaryFailure != null) { httpCleanupFailures.Insert(0, httpPrimaryFailure); }
                    throw new AggregateException(httpCleanupFailures);
                }
            }
        });
    }

    /// <summary>解禁通知で先着した新Pを起動任意同期が予約せず見送り、実後続終端とP解放後にも自動再投入しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupExternalSync_AdmissionAfterUnlockIsSkippedWithoutNetworkOrAutomaticRetry()
    {
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));
        var httpTasks = new List<Task>();
        void AwaitHttpTask(Task task, string name)
        {
            httpTasks.Add(task);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(task.WaitAsync(httpOperation.Token), name);
        }
        void AwaitHttpArrival(Task phase, Task operation, string name)
            => AwaitHttpTask(TestUiDispatcherHost.AwaitNotificationAsync(phase, operation, name), name);

        TestUiDispatcherHost.Invoke(() =>
        {
            var fixture = new CompletionFixture(ownHttpSettings: true);
            SingleRequestHttpServer? server = null;
            IDisposable? competing = null;
            Exception? httpPrimaryFailure = null;
            try
            {
                server = new SingleRequestHttpServer(httpOperation.Token, []);
                fixture.Settings.SkipInitPlaylistLoad = false;
                using (LR2SongDBExtended db = fixture.Composition.DatabaseScope.OpenSongWrite(fixture.Library.InitializationSongDbPath))
                {
                    db.Insert(new LR2SongDBExtended.playlist
                    {
                        name = "network fixture",
                        header_url = server.Address.AbsoluteUri,
                        is_external_sync = true,
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders,
                        output_dir = "Network"
                    });
                }
                int queued = 0;
                int completed = 0;
                var syncWork = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<string, Func<Task>, bool> actualScheduler = GetPrivateField<Func<string, Func<Task>, bool>>(
                    fixture.Owner.PlaylistWorkspace, "playlistExternalSyncScheduler");
                SetPrivateField(fixture.Owner.PlaylistWorkspace, "playlistExternalSyncScheduler", (Func<string, Func<Task>, bool>)((reason, work) =>
                    actualScheduler(reason, () =>
                    {
                        try { Task actualWork = work(); syncWork.TrySetResult(actualWork); return actualWork; }
                        catch (Exception failure) { syncWork.TrySetException(failure); throw; }
                    })));
                var skipped = new TaskCompletionSource<PlaylistExternalSyncCompletionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Owner.PlaylistWorkspace.PlaylistExternalSyncQueued += (_, _) => queued++;
                fixture.Owner.PlaylistWorkspace.PlaylistExternalSyncCompleted += (_, args) => { completed++; skipped.TrySetResult(args); };
                fixture.Owner.ProgressHub.StartupProgress.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked)
                        && !fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked && competing == null)
                    {
                        Assert.IsTrue(fixture.Composition.PlaylistOperationAdmission.TryEnter(out competing));
                    }
                };
                Task<bool> startup = fixture.Start();
                AwaitHttpTask(startup, "startup-external-busy-required");
                Assert.IsTrue(startup.GetAwaiter().GetResult());
                // 実schedulerが呼んだ同期Taskと通知を対にし、終端したのに通知がない場合も即時に失敗させる。
                AwaitHttpArrival(skipped.Task, syncWork.Task.Unwrap(), "startup-external-busy-skipped");
                AwaitHttpTask(syncWork.Task.Unwrap(), "startup-external-busy-work-terminal");
                Assert.IsTrue(skipped.Task.GetAwaiter().GetResult().WasSkipped);
                Assert.AreEqual(0, queued);
                Assert.AreEqual(string.Empty, server.RequestMethod);
                fixture.OptionalRelease.TrySetResult();
                AwaitHttpArrival(fixture.OptionalEntered.Task, fixture.OptionalWork, "startup-external-optional-arrival");
                AwaitHttpTask(fixture.OptionalWork, "startup-external-optional-terminal");
                Assert.IsNotNull(competing);
                competing.Dispose();
                competing = null;
                AwaitHttpTask(fixture.Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "startup-external-all-accepted-terminal");
                Assert.AreEqual(1, completed);
                Assert.AreEqual(0, queued);
                Assert.AreEqual(string.Empty, server.RequestMethod);
            }
            catch (Exception httpPrimary) { httpPrimaryFailure = httpPrimary; throw; }
            finally
            {
                using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var httpCleanupFailures = new List<Exception>();
                competing?.Dispose();
                fixture.OptionalRelease.TrySetResult();
                Task? shutdown = null;
                try { shutdown = fixture.Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(); }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                httpOperation.Cancel();
                Task[] ownedHttpTasks = httpTasks.Concat(new Task?[] { shutdown, server?.Completion }.OfType<Task>()).ToArray();
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token,
                        httpPrimaryFailure, httpTasks.Concat(new Task?[] { shutdown }.OfType<Task>()), server == null ? [] : [server]), "startup-http-cleanup");
                }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                try { fixture.Dispose(httpCleanup.Token, ownedHttpTasks); }
                catch (Exception failure) { httpCleanupFailures.Add(failure); }
                if (httpCleanupFailures.Count > 0)
                {
                    if (httpPrimaryFailure != null) { httpCleanupFailures.Insert(0, httpPrimaryFailure); }
                    throw new AggregateException(httpCleanupFailures);
                }
            }
        });
    }

    /// <summary>起動サービスの実接続でフォルダ修復reporterをHubへ配送し、修復件数と対象表を独立進捗行へ表示します。</summary>
    [TestMethod]
    public void StartupServiceAttachmentRoutesCustomFolderRepairProgressToHub()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            "BeMusicSeeker_StartupRepairProgress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        MainWindowViewModel? owner = null;
        try
        {
            Settings settings = TestSettingsFactory.CreateIsolatedSettings();
            owner = MainWindowViewModelTestFactory.Create(settings);
            ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope;
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope, songDbPath, settings, owner);
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(library.DatabaseScope, songDbPath, settings, library: library);
            var profile = new LibraryProfile(
                operationModeLR2DB: false,
                songDbPath,
                [tempDirectory],
                lr2ConfigOwner: null,
                lr2ScoreDbPath: null,
                canWriteLr2Config: false,
                canOutputLr2Folders: false,
                canUseLr2Backup: false,
                canUseLr2IrScore: false);
            IStartupLibraryApplicationPort applicationPort = owner;
            applicationPort.AttachStartupLibrary(library);
            applicationPort.AttachStartupServices(new StartupLibraryServices(profile, library, playlist));

            Assert.IsNotNull(playlist.CustomFolderOutputRepairProgressReporter);
            playlist.CustomFolderOutputRepairProgressReporter(new PlaylistSyncProgressSnapshot
            {
                IsActive = true,
                TotalTableCount = 3,
                CompletedTableCount = 1,
                CurrentTableName = "repair target",
                Source = "custom_folder_repair"
            });
            TestUiDispatcherHost.ProcessQueuedPresentation();

            OperationProgressRow row = owner.ProgressHub.Rows.Single(value => value.Key == "playlist:custom_folder_repair:0");
            Assert.AreEqual(3d, row.Maximum);
            Assert.AreEqual(1d, row.Value);
            Assert.AreEqual("repair target", row.Detail);
        }
        finally
        {
            owner?.SettingDialog.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>通知listener未登録の実画面入口でも型付きLR2失敗を消費し、既存Failed状態を独立警告行へ反映します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StartupRequiredResult_ActualMainWindowConsumerProjectsLr2FailureWithoutAnotherSnapshotStore()
    {
        Settings settings = TestSettingsFactory.CreateIsolatedSettings();
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
        ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope;
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).EnsureAppOwnedSchema();
        PlaylistPersistenceRepository.EnsureSchema(databaseScope, scope.SongDbPath);
        TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(MainWindowViewModelTestFactory.GetComposition(owner).DatabaseScope, scope.SongDbPath, settings, owner);
        TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(library.DatabaseScope, scope.SongDbPath, settings, library: library);
        try
        {
            var profile = new LibraryProfile(false, scope.SongDbPath, [scope.DirectoryPath], null, null, false, false, false, false);
            IStartupLibraryApplicationPort applicationPort = owner;
            applicationPort.AttachStartupLibrary(library);
            applicationPort.AttachStartupServices(new StartupLibraryServices(profile, library, playlist));
            var failure = new IOException("controlled required LR2 failure for actual UI consumer");
            library.Lr2Synchronization.RecordLr2SongDbSyncFailure(string.Empty, 0, failure, "failed");
            Assert.IsFalse(owner.ProgressHub.Rows.Any(row => row.Key == "lr2"), "結果投影前はこのfixtureに状態通知listenerを登録しない。");
            var result = new StartupRequiredInitializationResult(new LibraryFileInitializationResult(), failure);

            StartupRequiredInitializationResult consumed = await owner.InitializeStartupLibraryFilesAsync(() => Task.FromResult(result));

            Assert.AreSame(result, consumed);
            OperationProgressRow row = owner.ProgressHub.Rows.Single(value => value.Key == "lr2");
            Assert.IsFalse(row.IsChild);
            Assert.IsFalse(row.HasGauge);
            StringAssert.Contains(row.ToolTip, failure.Message);
            Assert.IsFalse(owner.IsInitializationCompleted, "結果の表示だけで初期化成功を確定しない。");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual("Failed", verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).status);
        }
        finally
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "required-result-consumer-close");
                owner.SettingDialog.Dispose();
            });
        }
    }

    /// <summary>実必須UIは独立folder最終readを待たずに終端し、表示readもmodel writer解放待ちへ接続しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupRequiredUi_CompletesWhileIndependentFolderReaderIsBlocked()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture();
            UseOwnedEmptyCatalog(fixture);
            fixture.Ui.Arm();
            Task<bool> initialization = fixture.Start();
            AwaitArrival(fixture.Ui.Entered.Task, initialization, "startup-reader-ui-arrival");
            using var releaseWriter = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? writer = null;
            fixture.Owner.RequiredStartupUiApplying += () =>
            {
                // 必須一覧の反映が終わった後だけwriterを保持し、独立した最終readを止める。
                writer = Task.Run(() =>
                {
                    using IDisposable guard = AcquireBmsFileWriterGuard(fixture.Library);
                    entered.TrySetResult();
                    releaseWriter.Wait();
                });
                AwaitArrival(entered.Task, writer, "startup-independent-writer");
            };
            try
            {
                Assert.AreEqual(0, fixture.Owner.LibraryFolderTree.BMSParentFolderList.Count,
                    "画面側の確定済みfolder一覧はwriter待ちを行いません。");
                int completions = 0;
                fixture.Owner.LibraryFolderTree.DeferredRefreshCompleted += (_, _) => completions++;
                fixture.Ui.Release.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-independent-reader-required-terminal");
                Assert.IsTrue(initialization.GetAwaiter().GetResult());
                Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                Assert.IsNotNull(writer);
                Task reader = fixture.Owner.LibraryFolderTree.WaitForDeferredRefreshIdleAsync();
                Assert.IsFalse(reader.IsCompleted);
                Assert.AreEqual(0, completions);
                releaseWriter.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(writer, reader), "startup-independent-reader-terminal");
            }
            finally
            {
                releaseWriter.Set();
                if (writer != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(writer, "startup-reader-writer-cleanup"); }
            }
        });
    }

    /// <summary>バックアップ項目の保存通知故障は保存済み値を保ち、有効な実beatoraja DBへの使用切替で起こるScore確定の必須UI失敗後は次の有効入力を確定できます。Score成功・必須UI失敗・次入力確定はURL補完を登録せず、通常の共通score入口でも同じ契約を保ちます。</summary>
    /// <remarks>履歴要求は取得前に消さず、新取得の実batch配送で置き換わります。必須UI gateでは取得組の公開と意味上の選択を、配送後にはUI成否によらない旧要求の取消・拒否と新要求の条件維持を確認します。</remarks>
    [TestMethod]
    [DataRow(true)] // 必須UI失敗後も次の入力を確定し、URL workを開始しません。
    [DataRow(false)] // 別項目の保存通知故障はScore成功を変えず、起動・全再初期化のURL補完契機は保ちます。
    [DoNotParallelize]
    public void SettingsScoreCompletion_RequiredUiFailureAndSavedNoticeFailureKeepDistinctFollowUp(bool uiFails)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var failures = new List<Exception>();
            using var fixture = new CompletionFixture(lr2Mode: true, lifetime: new TestApplicationLifetime(firstStartup: false), settingsFailureReporter: failures.Add);
            UseOwnedEmptyCatalog(fixture);
            fixture.OptionalRelease.TrySetResult();
            var initialUrlProgress = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
            fixture.Scheduler.ProgressChanged += progress =>
            {
                if (progress.Name == "playlist_url_completion") { initialUrlProgress.Enqueue(progress); }
            };
            Task<bool> initial = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(initial, "settings-score-initial");
            Assert.IsTrue(initial.GetAwaiter().GetResult());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "settings-score-prior-follow-up");
            Assert.IsTrue(initialUrlProgress.Any(progress => progress.IsRunning), "通常起動のURL補完契機を保ちます。");
            var uiFailure = new IOException("required score UI failed");
            var noticeFailure = new IOException("saved backup notice failed");
            var notices = new List<UiMessageRequest>();
            int registrations = 0;
            Func<string, string, string, Func<Task>, bool> scheduler = fixture.Playlist.StartupBackgroundTaskScheduler
                ?? throw new InvalidOperationException("Actual playlist scheduler is unavailable.");
            fixture.Playlist.StartupBackgroundTaskScheduler = (name, reason, dependency, work) =>
            {
                if (name == "playlist_url_completion") { registrations++; }
                return scheduler(name, reason, dependency, work);
            };
            SettingsDialogViewModel dialog = fixture.Owner.SettingDialog;
            string beatoraja = Path.Combine(fixture.Settings.BMSRootPath, "beatoraja");
            string playerDirectory = Directory.CreateDirectory(Path.Combine(beatoraja, "player", "saved-score-player")).FullName;
            File.WriteAllText(Path.Combine(beatoraja, "beatoraja.jar"), string.Empty);
            File.WriteAllText(Path.Combine(beatoraja, BeatorajaConfigService.ConfigFileName), "{\"playername\":\"saved-score-player\"}");
            string scorePath = Path.Combine(playerDirectory, "score.db");
            using (SQLiteConnectionEx scoreDb = fixture.Composition.DatabaseScope.OpenProviderConnection(scorePath))
            {
                scoreDb.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
            }
            fixture.Settings.Commit((nameof(Settings.BeatorajaRootPath), beatoraja),
                (nameof(Settings.BeatorajaPlayerId), "saved-score-player"), (nameof(Settings.BeatorajaScoreDbPath), scorePath));
            dialog.SetPresentationActive(false);
            dialog.SetPresentationActive(true);
            dialog.UseBeatorajaScoreDb = true;
            Assert.IsTrue(dialog.IsScoreSelectionValid);
            // 通常項目の確定をScoreへ混ぜず、後続が採用する既存の確定方針をfixtureへ与えます。
            fixture.Settings.EnablePlaylistUrlCompletion = true;
            fixture.Settings.PlaylistMd5UrlMappingTsvUri = string.Empty;
            fixture.Settings.EnableStellaFullPlaylistUrlCompletion = false;
            dialog.LR2BackupPath = fixture.Settings.BMSRootPath;
            fixture.Dialogs.MessageObservedAsync = request =>
            {
                notices.Add(request);
                return Task.FromException(noticeFailure);
            };
            dialog.IsLR2BackupEnabled = true;
            Assert.IsTrue(fixture.Settings.IsLR2BackupEnabled);
            Assert.AreEqual(1, notices.Count, "バックアップ案内はその項目の保存直後に通知します。");
            fixture.Ui.IsCompletionBoundary = () => !fixture.Composition.OperationAdmission.IsActive
                && !fixture.Composition.PlaylistOperationAdmission.IsActive && fixture.Owner.IsLibraryOperationInProgress;
            var historyPeriod = PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc);
            const string historyKeyword = "title:history-score";
            fixture.Owner.ChartFilters.KeywordFilter = historyKeyword;
            var initialHistoryAdopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void InitialHistoryRows(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainChartListViewModel.Rows)
                    && fixture.Owner.MainChartList.LastAppliedColumnMode == MainViewUpdateMode.PlayHistorySelected)
                { initialHistoryAdopted.TrySetResult(); }
            }
            PlayHistoryViewRequest historyRequest;
            fixture.Owner.MainChartList.PropertyChanged += InitialHistoryRows;
            try
            {
                historyRequest = fixture.Owner.PlayHistory.ActivatePeriod(historyPeriod, historyKeyword);
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(initialHistoryAdopted.Task, "settings-score-initial-history-adopted");
            }
            finally { fixture.Owner.MainChartList.PropertyChanged -= InitialHistoryRows; }
            BMSLibrary.ScoreSnapshot previousScore = fixture.Library.CaptureScoreSnapshotForView();
            Assert.IsNotNull(previousScore.PlayHistorySnapshot);
            CancellationToken historyCancellation = fixture.Owner.PlayHistory.GetCancellationToken(historyRequest.RequestId);
            var historyBatchDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ScoreBatchDelivered(object? sender, NormalLibraryRefreshAppliedEventArgs args)
            {
                if (args.NotificationBatch.ScoreChanges.Count > 0) { historyBatchDelivered.TrySetResult(); }
            }
            fixture.Owner.RegularChartList.NormalLibraryRefreshApplied += ScoreBatchDelivered;
            fixture.Ui.Arm();
            Task apply = dialog.CommitScoreSettingsAsync();
            fixture.Track(apply);
            Task ownedOperation = dialog.SettingsOperationCompletionTask;
            try
            {
                AwaitArrival(fixture.Ui.Entered.Task, apply, "settings-score-required-ui");
                Assert.IsFalse(apply.IsCompleted);
                Assert.AreEqual(1, notices.Count, "Scoreは別項目の案内を再表示しません。");
                Assert.AreSame(ownedOperation, dialog.SettingsOperationCompletionTask);
                Assert.IsFalse(ownedOperation.IsCompleted);
                Assert.IsFalse(dialog.IsSettingsOperationAvailable, "親L/P解放後も必須UIまで設定Taskを所有します。");
                BMSLibrary.ScoreSnapshot acquiredScore = fixture.Library.CaptureScoreSnapshotForView();
                Assert.AreEqual(ActiveScoreSource.Beatoraja, acquiredScore.ActiveScoreSource);
                Assert.AreEqual(scorePath, acquiredScore.ScoreDbPath);
                Assert.IsNotNull(acquiredScore.PlayHistorySnapshot);
                Assert.AreNotSame(previousScore.PlayHistorySnapshot, acquiredScore.PlayHistorySnapshot);
                Assert.IsTrue(acquiredScore.SourceGeneration > previousScore.SourceGeneration);
                AssertHistorySelection();
                Assert.AreEqual(0, registrations);
                Assert.AreEqual("saved-score-player", fixture.Settings.BeatorajaPlayerId);
                Assert.IsTrue(fixture.Settings.UseBeatorajaScoreDb);
                Assert.AreEqual(scorePath, fixture.Settings.BeatorajaScoreDbPath);
                Assert.IsTrue(fixture.Settings.EnablePlaylistUrlCompletion);
                if (uiFails) { fixture.Ui.Release.TrySetException(uiFailure); }
                else { fixture.Ui.Release.TrySetResult(); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "settings-score-terminal");
                Assert.AreEqual(uiFails ? 1 : 0, failures.Count);
                if (uiFails) { Assert.AreSame(uiFailure, failures.Single()); }
                Assert.AreEqual(1, notices.Count, "必須UIの成否にかかわらず、別項目の保存案内を重ねません。");
                Assert.AreEqual(Resources.Msg_LR2ConfigBackupEnabledNextStartup, notices.Single().MessageBoxText);
                Assert.AreEqual(Resources.Confirm, notices.Single().Caption);
                Assert.AreEqual(System.Windows.MessageBoxImage.Asterisk, notices.Single().Icon);
                Assert.IsTrue(ownedOperation.IsCompleted);
                Assert.IsTrue(dialog.IsScoreSelectionEnabled);
                // ScoreNotificationは必須UIより先に発火する。gateの前後どちらにdrainされても、実配送の後でだけ要求置換を判定する。
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(historyBatchDelivered.Task, "settings-score-history-batch-delivered");
                Assert.IsTrue(historyCancellation.IsCancellationRequested);
                Assert.IsFalse(fixture.Owner.PlayHistory.IsCurrentRequest(historyRequest.RequestId), "旧結果は配送後の現在要求として採用しません。");
                PlayHistoryViewRequest currentHistory = AssertHistorySelection();
                Assert.AreNotEqual(historyRequest.RequestId, currentHistory.RequestId);
                Assert.AreSame(acquiredScore.PlayHistorySnapshot, currentHistory.SourceSnapshot);
                if (uiFails)
                {
                    Assert.AreEqual(0, registrations, "保存済みURL変更でも必須UI失敗時は新規登録しません。");
                    dialog.RequestOpen();
                    dialog.UseBeatorajaScoreDb = false;
                    Task retry = dialog.CommitScoreSettingsAsync();
                    fixture.Track(retry);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(retry, "settings-score-next-explicit-apply");
                    Assert.IsFalse(fixture.Settings.UseBeatorajaScoreDb);
                    Assert.AreEqual(1, failures.Count);
                    Assert.AreEqual(1, notices.Count, "確定済み保存の案内を再試行で重ねません。");
                }
                Assert.AreEqual(0, registrations, "ScoreOnlyの共通完了はURL補完を予約しません。");
                Task reload = fixture.Owner.ReloadScoresOnlyAsync();
                fixture.Track(reload);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(reload, "ordinary-score-only-terminal");
                Assert.AreEqual(0, registrations, "通常のスコア再読込み入口でも予約しません。");
                if (!uiFails)
                {
                    Task reinitialize = fixture.Owner.ReinitializeLibraryAsync();
                    fixture.Track(reinitialize);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reinitialize, "full-reinitialize-url-trigger");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "full-reinitialize-url-terminal");
                    Assert.AreEqual(1, registrations, "全再初期化のURL補完契機を保ちます。");
                }
            }
            finally
            {
                fixture.Owner.RegularChartList.NormalLibraryRefreshApplied -= ScoreBatchDelivered;
                fixture.Ui.Release.TrySetResult();
            }

            PlayHistoryViewRequest AssertHistorySelection()
            {
                PlayHistoryViewRequest current = fixture.Owner.PlayHistory.SnapshotActiveRequest();
                Assert.IsNotNull(current);
                Assert.IsTrue(fixture.Owner.PlayHistory.IsViewActive);
                Assert.IsTrue(fixture.Owner.PlayHistory.IsCurrentRequest(current.RequestId));
                Assert.AreEqual(historyPeriod.Kind, current.PeriodRequest.Kind);
                Assert.AreEqual(historyPeriod.PlayedAtFromInclusive, current.PeriodRequest.PlayedAtFromInclusive);
                Assert.AreEqual(historyPeriod.PlayedAtToExclusive, current.PeriodRequest.PlayedAtToExclusive);
                Assert.AreEqual(historyPeriod.FinalizationFilter, current.PeriodRequest.FinalizationFilter);
                Assert.AreEqual(historyKeyword, fixture.Owner.ChartFilters.KeywordFilter);
                Assert.AreEqual(historyKeyword, fixture.Owner.PlayHistory.CurrentKeywordIdentity);
                Assert.AreEqual("all", fixture.Owner.PlayHistory.SelectedDisplayTargetIdentity);
                Assert.AreEqual("all", fixture.Owner.PlayHistory.CurrentDisplayTargetIdentity);
                Assert.AreEqual(fixture.Owner.PlayHistory.KeywordRevision, current.KeywordFilterRevision);
                Assert.AreEqual(fixture.Owner.PlayHistory.DisplayTargetRevision, current.DisplayTargetRevision);
                return current;
            }
        });
    }

    /// <summary>新しいportable DBの正常な作成・読取り後に走査が例外終了しても、警告して空の保存内容で操作可能になります。</summary>
    /// <remarks>実MainWindowViewModelと標準compositionを使い、scannerだけを制御します。共有通知を所有するため非並列です。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void StartupScan_NewPortableDatabaseFailureWarnsAndStartsWithSavedEmptyCatalog()
        => AssertStartupScanFailureStartsWithSavedCatalog(deleteRootAfterDatabaseRead: false);

    /// <summary>保存内容の読取り後にrootが消失した場合、先行取得済みの追加候補を採用せず保存済み譜面で操作可能になります。</summary>
    /// <remarks>初期設定検査が正常な実構成を使い、DB読取り通知後に専用Songsだけを削除して後段再検査へ到達させます。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void StartupScan_RootDeletedAfterDatabaseReadWarnsAndKeepsSavedCatalog()
        => AssertStartupScanFailureStartsWithSavedCatalog(deleteRootAfterDatabaseRead: true);

    private static void AssertStartupScanFailureStartsWithSavedCatalog(bool deleteRootAfterDatabaseRead)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var databaseScope = new ApplicationDatabaseScope();
        TestUiDispatcherHost.Invoke(() =>
        {
            string directory = Path.Combine(migrationWorkspace.RootDirectory, "StartupScan-" + Guid.NewGuid().ToString("N"));
            string songs = Path.Combine(directory, "Songs");
            Directory.CreateDirectory(songs);
            var scanner = new StartupScanGate(deleteRootAfterDatabaseRead, Path.Combine(songs, "candidate.bms"));
            MainWindowViewModel? owner = null;
            Task<bool>? initialization = null;
            var dbRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var path = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory, "BeMusicSeeker.exe"));
                Directory.CreateDirectory(path.DataDirectoryPath);
                string savedPath = Path.Combine(songs, "saved.bms");
                if (deleteRootAfterDatabaseRead)
                {
                    File.WriteAllText(savedPath, "#PLAYER 1\n#TITLE Saved\n#BPM 120\n#00111:01\n");
                    File.WriteAllText(scanner.CandidatePath, "#PLAYER 1\n#TITLE Candidate\n#BPM 120\n#00111:01\n");
                    using LR2SongDBExtended db = databaseScope.OpenSongWrite(path.StandaloneSongDbPath);
                    db.RunInTransaction(() =>
                    {
                        db.CreateTable<LR2SongDB.song>();
                        ChartFile saved = ChartTestValues.Empty() with { Path = savedPath, Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Title = "Saved" };
                        db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(saved), typeof(LR2SongDB.song));
                    });
                }
                else { Assert.IsFalse(File.Exists(path.StandaloneSongDbPath)); }
                Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
                {
                    values.OperationModeLR2DB = false;
                    values.BMSRootPath = songs;
                    values.StandaloneBmsRootPaths = songs;
                    values.BMSInstallDir = songs;
                    values.ScanBmsFilesOnStartup = true;
                    values.SkipInitPlaylistLoad = true;
                    values.EnablePlaylistUrlCompletion = false;
                    values.EnableBeatorajaBmtOutput = false;
                    values.UseBeatorajaScoreDb = false;
                    values.UsePlayeruBMplay = false;
                    values.UsePlayerLR2body = false;
                    values.UsePlayerBMIIDXView = false;
                    values.IsLR2BackupEnabled = false;
                    values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
                });
                var messages = new List<UiMessageRequest>();
                var dialogs = new CompletionDialogs { MessageObservedAsync = message => { messages.Add(message); return Task.CompletedTask; } };
                var failures = new RecordingStartupLibraryInitializationFailurePresenter();
                var composition = new ApplicationComposition(settingsEditSession: new NoOpSettingsEditSession(settings),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: new TestApplicationLifetime(firstStartup: false),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(path),
                    fileDbMutationDialogService: dialogs, settingsDialogService: dialogs, chartFileScanner: scanner);
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                {
                    LibraryCreator = profile =>
                    {
                        BMSLibrary library = composition.CreateBmsLibrary(profile);
                        library.PropertyChanged += (_, args) =>
                        {
                            if (args.PropertyName == nameof(BMSLibrary.LibraryDatabaseLoadCompletedVersion)
                                && library.LibraryDatabaseLoadCompletedVersion > 0) { dbRead.TrySetResult(); }
                        };
                        return library;
                    }
                };
                owner = new MainWindowViewModel(composition, factory, startupLibraryInitializationFailurePresenter: failures);
                var presentation = new RecordingSettingsDialogPresentationPort();
                owner.SettingDialog.AttachPresentationPort(presentation);
                owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
                initialization = owner.InitializeAsync();
                AwaitArrival(scanner.Entered.Task, initialization, "startup-scan-entered");
                AwaitArrival(dbRead.Task, initialization, "startup-scan-database-read");
                if (deleteRootAfterDatabaseRead)
                {
                    Assert.AreEqual(Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
                        Path.GetDirectoryName(Path.GetFullPath(songs)) + Path.DirectorySeparatorChar);
                    Directory.Delete(songs, recursive: true);
                }
                scanner.Release.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-scan-terminal");
                Assert.IsTrue(initialization.GetAwaiter().GetResult(), string.Join(Environment.NewLine, failures.Presentations.Select(failure => failure.Exception.ToString())));
                Assert.IsTrue(owner.IsInitializationCompleted);
                Assert.IsTrue(owner.HasActiveLibraryProfile);
                Assert.IsFalse(owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                Assert.IsFalse(owner.IsLibraryOperationInProgress);
                Assert.AreEqual(0, failures.Presentations.Count);
                Assert.AreEqual(1, scanner.ScanCount);
                string expectedReason = deleteRootAfterDatabaseRead ? "BmsRoot" : "test_startup_scan_failed";
                string[] warningParts = Resources.Warn_FileScanSkippedIncomplete.Split("{0}", StringSplitOptions.None);
                Assert.AreEqual(1, messages.Count(message => message.MessageBoxText.StartsWith(warningParts[0], StringComparison.Ordinal)
                    && message.MessageBoxText.EndsWith(warningParts[1], StringComparison.Ordinal)
                    && message.MessageBoxText.Contains(expectedReason, StringComparison.Ordinal)));
                using (LR2SongDBExtended db = composition.DatabaseScope.OpenSongWrite(path.StandaloneSongDbPath))
                {
                    string[] saved = db.Table<LR2SongDB.song>().Select(row => row.path).ToArray();
                    CollectionAssert.AreEqual(deleteRootAfterDatabaseRead ? new[] { savedPath } : Array.Empty<string>(), saved);
                }
                BMSLibrary createdLibrary = factory.CreatedLibrary ?? throw new AssertFailedException("実ライブラリが構成されませんでした。");
                string[] owned = createdLibrary.CreateOwnedChartInfoFullBackfillTargetSnapshotForDiagnostics().Select(chart => chart.Path).ToArray();
                CollectionAssert.AreEqual(deleteRootAfterDatabaseRead ? new[] { savedPath } : Array.Empty<string>(), owned);
                // 警告と保存済みcatalogは必須起動の終端で検査します。次操作の受付は、同じLを使う起動後保守の実終端後に確認します。
                TestUiDispatcherHost.AwaitTaskOnDispatcher(WaitForStartupBackgroundTerminalAsync(owner), "startup-scan-background-terminal");
                Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable admission));
                admission.Dispose();
            }
            finally
            {
                scanner.Release.TrySetResult();
                try
                {
                    if (initialization != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-scan-owned-terminal"); }
                }
                finally
                {
                    try
                    {
                        if (owner != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "startup-scan-shutdown"); }
                    }
                    finally { owner?.SettingDialog.Dispose(); Directory.Delete(directory, recursive: true); }
                }
            }
        });
    }

    /// <summary>実先行走査の完了だけを制御し、通常例外か取得済み候補を返します。</summary>
    private sealed class StartupScanGate(bool returnCompleteCandidate, string candidatePath) : IChartFileScanner
    {
        internal string CandidatePath { get; } = candidatePath;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ScanCount { get; private set; }
        public ChartScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> chartExtensions,
            bool verboseLog = false, bool includeTextSurface = true, bool includeDirectorySurface = false)
        {
            ScanCount++;
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            if (!returnCompleteCandidate) { throw new IOException("test_startup_scan_failed"); }
            return new ChartScanExecutionResult
            {
                Success = true,
                IsComplete = true,
                Result = new ChartScanResult
                {
                    ChartFilePaths = new HashSet<string>([CandidatePath], StringComparer.Ordinal),
                    ChartDirectories = new HashSet<string>(rootDirectories, StringComparer.OrdinalIgnoreCase)
                }
            };
        }
    }

    private static void AwaitArrival(Task arrival, Task initialization, string name)
    {
        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(arrival, initialization, name), name);
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }


    private static IDisposable AcquireBmsFileWriterGuard(BMSLibrary library)
    {
        PropertyInfo property = typeof(BMSLibrary)
            .GetProperty("rwlockBMSFiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object wrapper = property.GetValue(library)!;
        return (IDisposable)wrapper.GetType()
            .GetMethod("GetWriterGuard", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(wrapper, null)!;
    }

}
