using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

/// <remarks>純粋な進捗判断はownerへ入力通知を渡して検査します。実起動・設定完了のケースはMainWindowのL/P解放・必須UI・後続scheduler接続の順序を検査するため実構成とDBを使い、Dispatcherでcallbackと終端を回収します。</remarks>
[TestClass]
public sealed class MainWindowViewModelStartupProgressTests
{

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
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Owner.SettingDialog.ApplySettingsAsync(), "startup-range-save");
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
    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(4)]
    [DoNotParallelize]
    public void StartupSortCachePriority_InvalidValueFailsOnlyOptionalWarmup(int priority)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture(isolateSortWarmup: true, settingsSessionFactory: values => new PriorityReloadSettingsSession(values, priority, 1));
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
        public void SaveOperationModeForRestart(bool mode, string identity) => throw new AssertFailedException("Unexpected mode restart.");
    }

    /// <summary>UI操作禁止を同じ値で繰り返し設定しても、真偽の実遷移だけを各一回通知して重複更新を防ぎます。</summary>
    [TestMethod]
    public void StartupProgress_InteractionBlockPublishesOnlyForStateTransitions()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        var changedProperties = new List<string>();
        owner.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

        owner.SetStartupUiInteractionBlocked(true);
        owner.SetStartupUiInteractionBlocked(true);
        owner.SetStartupUiInteractionBlocked(false);
        owner.SetStartupUiInteractionBlocked(false);

        Assert.IsFalse(owner.IsStartupUiInteractionBlocked);
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked),
                nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked)
            },
            changedProperties);
    }

    /// <summary>実起動の必須UI前はHTTP未開始、成功公開・解禁後は完了通知とHTTP本文を保持しても両索引準備が始まります。同期省略時のcatalog登録・受付識別・実親終端を標準接続で確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupRequiredUi_ActualTaskOwnsCompletionBeforeOptionalWorkStarts()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture();
            var server = new SingleRequestHttpServer(System.Text.Encoding.UTF8.GetBytes("[]"), holdBody: true);
            fixture.Settings.TableListURL = server.Address;
            var noticeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var noticeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var playlistIndexEntered = new TaskCompletionSource<StartupBackgroundTaskProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var chartIndexEntered = new TaskCompletionSource<StartupBackgroundTaskProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
            void ObserveRequest(StartupBackgroundTaskProgressSnapshot status)
            {
                requests.Enqueue(status);
                if (!status.IsRunning) { return; }
                if (status.Name == "playlist_library_index_prewarm") { playlistIndexEntered.TrySetResult(status); }
                if (status.Name == "playlist_virtual_order_prewarm") { chartIndexEntered.TrySetResult(status); }
            }
            fixture.Scheduler.ProgressChanged += ObserveRequest;
            fixture.Dialogs.MessageObservedAsync = _ => { noticeEntered.TrySetResult(); return noticeRelease.Task; };
            fixture.Ui.Arm();
            Task<bool> initialization = fixture.Start();
            try
            {
                AwaitArrival(fixture.Ui.Entered.Task, initialization, "startup-required-ui");
                fixture.AssertBeforeUiCompletion();
                Assert.IsFalse(initialization.IsCompleted);
                Assert.AreEqual(string.Empty, server.RequestMethod);
                fixture.Ui.Release.TrySetResult();
                AwaitArrival(noticeEntered.Task, initialization, "startup-notice-held");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(server.WaitForPhaseAsync(server.HeadersSent.Task, "catalog HTTP before notice completion"), "catalog-http-arrival");
                Assert.IsFalse(initialization.IsCompleted, "InitializeAsyncは実通知Taskを引き続き待ちます。");
                Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
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
                AwaitArrival(Task.WhenAll(playlistIndexEntered.Task, chartIndexEntered.Task), backgroundTerminal, "startup-indexes-before-http-release");
                Assert.IsFalse(server.ReleaseBody.Task.IsCompleted);
                Assert.IsFalse(requests.Any(status => status.Name == "external_table_catalog" && !status.IsRunning), "両索引はHTTP実終端を待たずに開始します。");
                foreach (StartupBackgroundTaskProgressSnapshot index in new[] { playlistIndexEntered.Task.GetAwaiter().GetResult(), chartIndexEntered.Task.GetAwaiter().GetResult() })
                {
                    Assert.AreEqual(additional.OperationToken, index.Request.OperationToken);
                    Assert.AreEqual(additional.Generation, index.Request.Generation);
                }
                noticeRelease.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-required-ui-terminal");
                Assert.IsTrue(initialization.GetAwaiter().GetResult());
                Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
                Assert.IsTrue(fixture.Owner.HasActiveLibraryProfile);
                Assert.IsFalse(fixture.Lifetime.IsFirstStartup);
                Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                Assert.IsFalse(fixture.Owner.IsLibraryOperationInProgress, "実成功公開後の通知・任意work・完了余韻でBusyを延ばしません。");
                AwaitArrival(fixture.OptionalEntered.Task, fixture.OptionalWork, "startup-optional-arrival");
                Assert.IsTrue(fixture.OptionalStartedAfterCompletion);
                Assert.IsFalse(fixture.OptionalWork.IsCompleted, "任意workを保持したまま必須起動が終端します。");
                Assert.AreEqual(1, fixture.CompletionNotifications);
                server.ReleaseBody.TrySetResult();
                fixture.OptionalRelease.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(backgroundTerminal, "startup-own-parent-terminal");
                Assert.IsFalse(fixture.Owner.ProgressHub.IsStartupBackgroundInitializationActive);
            }
            finally
            {
                fixture.Scheduler.ProgressChanged -= ObserveRequest;
                noticeRelease.TrySetResult();
                fixture.Ui.Release.TrySetResult();
                fixture.OptionalRelease.TrySetResult();
                server.ReleaseBody.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "catalog-startup-cleanup");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(server.DisposeAsync().AsTask(), "catalog-http-cleanup");
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

    /// <summary>進捗操作の開始前に失敗してもUI操作禁止を解除し、active操作がないことを理由に操作不能を残しません。</summary>
    [TestMethod]
    public void StartupProgress_FailureClearsInteractionBlockBeforeActiveOperationCheck()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        owner.SetStartupUiInteractionBlocked(true);

        owner.FailStartupProgressOperation("startup failed before operation");

        Assert.IsFalse(owner.IsStartupUiInteractionBlocked);
    }

    /// <summary>全再初期化が失敗した後は対応tokenのcleanup終端で再試行可へ進み、失敗状態を保ってUI操作禁止を解除します。</summary>
    [TestMethod]
    public void StartupProgress_FullReinitializeFailureBecomesRetryableAfterCleanup()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        long operationToken = owner.StartStartupProgressOperation(
            StartupProgressOperationKind.FullReinitialize);

        owner.FailStartupProgressOperation("directory preflight failed");
        owner.MarkStartupProgressFailureCleanupComplete(operationToken);

        Assert.IsTrue(owner.IsFailed);
        Assert.IsTrue(owner.IsRetryableFailure);
        Assert.IsFalse(owner.IsStartupUiInteractionBlocked);
    }

    /// <summary>実入口のUI失敗を親解放後に通知し、通知失敗でも元原因・cleanupを保持して次の明示初期化を受理します。</summary>
    [DataTestMethod]
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
    [DataTestMethod]
    [DataRow(false)] // 実モデルへ渡した設定snapshot読取りが処理段階で失敗します。
    [DataRow(true)] // ローカル処理成功後の必須UI Taskが失敗します。
    [DoNotParallelize]
    public void LimitedInitialization_ActualScoreFailureUsesOperationFailedWithoutSettingsGuidance(bool uiFails)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture(lifetime: new TestApplicationLifetime(firstStartup: false));
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
                IOException actual = Assert.ThrowsException<IOException>(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(reload, "reload-tables-safety-terminal"));
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
        string directory = Path.Combine(Path.GetTempPath(), "startup-notification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var log = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
        LoggingConfiguration? previousConfiguration = LogManager.Configuration;
        try
        {
            NLogWrapper.ConfigureApplicationFileLogging(directory, LogLevel.Info, enableInstallPerformanceLogging: false);
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, log);
            LogManager.Configuration = configuration;
            TestUiDispatcherHost.Invoke(() =>
            {
                var failure = new IOException("initial completion notice failed");
                var recorded = new List<StartupLibraryInitializationFailurePresentation>();
                using var fixture = new CompletionFixture(new CallbackFailurePresenter(recorded.Add));
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
    [DataTestMethod]
    [DataRow(false)] // 終了による取消だけなら進捗を実失敗へ分類しません。
    [DataRow(true)] // 終了中に生じた実UI失敗は取消へ置換しません。
    [DoNotParallelize]
    public void StartupRequiredUi_ShutdownWhileAwaitingKeepsRealFailureDistinct(bool realFailure)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var recorded = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new CompletionFixture(new CallbackFailurePresenter(recorded.Add));
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
            var player = new PlaybackPanelViewModelTests.FakeBmsPlayer { BeforeClose = () => { entered.TrySetResult(); release.Wait(); } };
            var failures = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new CompletionFixture(new CallbackFailurePresenter(failures.Add), () => player);
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
            Assert.IsTrue(fixture.Owner.SettingDialog.CheckValidation(out string validationFailure), validationFailure);
            string folder = Path.Combine(fixture.Settings.BMSInstallDir, "Leap");
            Directory.CreateDirectory(folder);
            Directory.SetLastWriteTime(folder, new DateTime(2024, 2, 29, 12, 0, 0));
            using (var db = new LR2SongDBExtended(fixture.Library.InitializationSongDbPath))
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
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture();
            var server = new SingleRequestHttpServer(System.Text.Encoding.UTF8.GetBytes("{}"), holdBody: true);
            fixture.Settings.SkipInitPlaylistLoad = false;
            var queued = new TaskCompletionSource<PlaylistExternalSyncRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Owner.PlaylistWorkspace.PlaylistExternalSyncQueued += (_, args) => queued.TrySetResult(args);
            using (var db = new LR2SongDBExtended(fixture.Library.InitializationSongDbPath))
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
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "external-sync-origin-startup");
                Assert.IsTrue(startup.GetAwaiter().GetResult());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(server.WaitForPhaseAsync(server.HeadersSent.Task, "external sync HTTP"), "external-sync-http");
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
            finally
            {
                server.ReleaseBody.TrySetResult();
                fixture.OptionalRelease.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "external-sync-origin-cleanup");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "external-sync-origin-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(server.DisposeAsync().AsTask(), "external-sync-origin-server-cleanup");
            }
        });
    }

    /// <summary>解禁通知で先着した新Pを起動任意同期が予約せず見送り、実後続終端とP解放後にも自動再投入しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupExternalSync_AdmissionAfterUnlockIsSkippedWithoutNetworkOrAutomaticRetry()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture();
            var server = new SingleRequestHttpServer([]);
            IDisposable? competing = null;
            try
            {
                fixture.Settings.SkipInitPlaylistLoad = false;
                using (var db = new LR2SongDBExtended(fixture.Library.InitializationSongDbPath))
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "startup-external-busy-required");
                Assert.IsTrue(startup.GetAwaiter().GetResult());
                // 実schedulerが呼んだ同期Taskと通知を対にし、終端したのに通知がない場合も即時に失敗させる。
                AwaitArrival(skipped.Task, syncWork.Task.Unwrap(), "startup-external-busy-skipped");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(syncWork.Task.Unwrap(), "startup-external-busy-work-terminal");
                Assert.IsTrue(skipped.Task.GetAwaiter().GetResult().WasSkipped);
                Assert.AreEqual(0, queued);
                Assert.AreEqual(string.Empty, server.RequestMethod);
                fixture.OptionalRelease.TrySetResult();
                AwaitArrival(fixture.OptionalEntered.Task, fixture.OptionalWork, "startup-external-optional-arrival");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.OptionalWork, "startup-external-optional-terminal");
                Assert.IsNotNull(competing);
                competing.Dispose();
                competing = null;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "startup-external-all-accepted-terminal");
                Assert.AreEqual(1, completed);
                Assert.AreEqual(0, queued);
                Assert.AreEqual(string.Empty, server.RequestMethod);
            }
            finally
            {
                competing?.Dispose();
                fixture.OptionalRelease.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(server.DisposeAsync().AsTask(), "startup-external-server-cleanup");
            }
        });
    }

    /// <summary>スコア再読込みの実要求と対応完了通知で必須進捗を完了し、操作名付きの完了表示へ進みます。</summary>
    [TestMethod]
    public void StartupProgress_ScoreOnlyCompletesThroughRuntimeRoutes()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ScoreOnly);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        owner.TrackStartupProgressScoreHydrationRequested(1);
        owner.TryCompleteStartupProgressScoreHydration(1);

        Assert.AreEqual(owner.Maximum, owner.Value);
        Assert.AreEqual(string.Format(Resources.Statusbar_progress_operation_completed_format, Resources.Statusbar_progress_reload_scores), owner.Label);
    }

    /// <summary>起動から表再読込みへ切り替えた後のスコア要求を受け付けず、現在操作の進捗を変える古い操作判定を検出します。</summary>
    [TestMethod]
    public void StartupProgress_RequestEligibilityUsesCurrentOperationAtomically()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);

        double valueBefore = owner.Value;
        double maximumBefore = owner.Maximum;
        owner.TrackStartupProgressScoreHydrationRequested(1);

        Assert.AreEqual(maximumBefore, owner.Maximum);
        Assert.AreEqual(valueBefore, owner.Value);
        Assert.AreEqual(StartupProgressOperationKind.ReloadTables, owner.CurrentOperationKind);
    }

    /// <summary>新しい操作を開始した後の旧token完了通知は進捗と名称を変えず、別操作を誤って完了させません。</summary>
    [TestMethod]
    public void StartupProgress_StaleTokenCannotCompleteNewOperation()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        long staleToken = owner.GetActiveStartupProgressOperationToken();
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);

        double valueBefore = owner.Value;
        double maximumBefore = owner.Maximum;
        owner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, staleToken);

        Assert.AreEqual(valueBefore, owner.Value);
        Assert.AreEqual(maximumBefore, owner.Maximum);
        Assert.AreEqual(Resources.Statusbar_progress_reload_tables, owner.Label);
    }

    /// <summary>旧操作token・scheduler世代の後続callbackを拒否し、現世代の必須登録閉鎖だけが後続workを開始してidleへ進めます。</summary>
    [TestMethod]
    public async Task StartupPostInitialization_StaleCallbackCannotOpenNewGenerationBarrier()
    {
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullyIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner scheduler = null!;
        scheduler = new(
            () => false,
            _ => { },
            _ => { },
            _ => { },
            value => value ?? string.Empty,
            (_, _) =>
            {
                if (scheduler.IsFullyIdle)
                {
                    fullyIdle.TrySetResult(true);
                }
            },
            new object());
        scheduler.Queue("post_initialize_gc", "test", null, () =>
        {
            postEntered.TrySetResult(true);
            return Task.CompletedTask;
        });

        long staleGeneration = scheduler.CurrentGeneration;
        scheduler.Reset(startImmediately: true);
        scheduler.MarkPostInitializationSchedulingComplete();

        const long currentOperationToken = 2L;
        bool staleAccepted = MainWindowViewModel.IsCurrentStartupPostInitializationCallback(
            operationToken: 1L,
            schedulerGeneration: staleGeneration,
            isOperationTokenCurrent: token => token == currentOperationToken,
            isSchedulerGenerationCurrent: scheduler.IsCurrentGeneration);

        Assert.IsFalse(staleAccepted);
        Assert.IsFalse(postEntered.Task.IsCompleted);

        long currentGeneration = scheduler.CurrentGeneration;
        bool currentAccepted = MainWindowViewModel.IsCurrentStartupPostInitializationCallback(
            currentOperationToken,
            currentGeneration,
            token => token == currentOperationToken,
            scheduler.IsCurrentGeneration);

        Assert.IsTrue(currentAccepted);
        Assert.IsTrue(scheduler.MarkRequiredInitializationSchedulingComplete(currentGeneration));
        await postEntered.Task;
        await fullyIdle.Task;
        Assert.IsTrue(scheduler.IsFullyIdle, scheduler.DescribeWaitState());
    }

    /// <summary>準備callbackへ新tokenを渡す前に現在tokenを公開し、callback内で旧操作を参照する順序の誤りを検出します。</summary>
    [TestMethod]
    public void StartupProgress_PublishesNewTokenBeforePrepareCallback()
    {
        StartupProgressWorkflowOwner owner = null!;
        long observedToken = 0L;
        long observedCallbackToken = 0L;
        owner = new StartupProgressWorkflowOwner(
            () => new StartupProgressVersionSnapshot(),
            (operationKind, operationToken) =>
            {
                observedCallbackToken = operationToken;
                observedToken = owner.GetActiveStartupProgressOperationToken();
            },
            action => action(),
            _ => { },
            _ => { },
            new object());

        long startedToken = owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);

        Assert.AreEqual(startedToken, observedCallbackToken);
        Assert.AreEqual(startedToken, observedToken);
    }

    /// <summary>要求を追跡していないスコア完了通知は再初期化の進捗を進めず、無関係な終端通知の混入を検出します。</summary>
    [TestMethod]
    public void StartupProgress_ScoreCompletionWithoutRequestIsIgnored()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);

        double valueBefore = owner.Value;
        double maximumBefore = owner.Maximum;
        owner.TryCompleteStartupProgressScoreHydration(1);

        Assert.AreEqual(valueBefore, owner.Value);
        Assert.AreEqual(maximumBefore, owner.Maximum);
        Assert.AreEqual(Resources.Statusbar_progress_full_reinitialize, owner.Label);
    }

    /// <summary>ファイル列挙・差分・UI適用・プレイリスト参照とhydrationの実通知を対応付け、ファイル再読込みを完了表示へ進めます。</summary>
    [TestMethod]
    public void StartupProgress_ReloadFileDiffCompletesFileAndPlaylistPhases()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadFileDiff);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        owner.TrackStartupProgressPlaylistReferenceRequest("ReloadFileDiff", 1);
        owner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(1, "ReloadFileDiff");
        owner.TryCompleteStartupProgressPlaylistReference(1);
        owner.TryCompleteStartupProgressPlaylistEntriesHydration(1);

        Assert.AreEqual(6.0, owner.Value);
        Assert.AreEqual(string.Format(Resources.Statusbar_progress_operation_completed_format, Resources.Statusbar_progress_reload_files), owner.Label);
    }

    /// <summary>必須初期化完了後の表示猶予gate中はactiveを維持し、猶予終端でinactiveを通知して完了表示の寿命を閉じます。</summary>
    [TestMethod]
    public async Task StartupProgress_CompletionHidePublishesInactiveLifecycleChange()
    {
        var delayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inactivePublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create(
            completionHideDelay: () =>
            {
                delayEntered.TrySetResult(true);
                return releaseDelay.Task;
            });
        owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        owner.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(StartupProgressWorkflowOwner.IsOperationActive)
                && !owner.IsOperationActive)
            {
                inactivePublished.TrySetResult(true);
            }
        };

        CompleteRequiredProgressForPresentation(owner);
        owner.CompleteRequiredInitialization(owner.GetActiveStartupProgressOperationToken());

        await delayEntered.Task;
        Assert.IsTrue(owner.IsOperationActive);
        releaseDelay.TrySetResult(true);
        await inactivePublished.Task;

        Assert.IsFalse(owner.IsOperationActive);
    }


    /// <summary>プレイリスト行の直接hydration要求にも完了通知を対応付け、別経路を通らない要求の進捗欠落を防ぎます。</summary>
    [TestMethod]
    public void StartupProgress_PlaylistEntriesHydrationCanCompleteDirectly()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        owner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(1, "test");
        owner.TryCompleteStartupProgressPlaylistEntriesHydration(1);

        Assert.AreEqual(2.0, owner.Value);
    }

    /// <summary>実追跡した要求だけを起動親へ所属させ、token・世代・source・機能版の違いと任意順位要求を拒否して親進捗を変えません。</summary>
    [TestMethod]
    public void StartupProgress_RequestMembershipRequiresActualTrackedOriginAndFeatureVersion()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ScoreOnly);
        long token = owner.GetActiveStartupProgressOperationToken();
        var score = new OperationProgressRequest(5, token, "score_hydration_deferred", 7);
        var ranking = new OperationProgressRequest(5, token, "ranking_refresh_deferred", 9);
        owner.TrackStartupProgressScoreHydrationRequested(7, score);
        double value = owner.Value;
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(score));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(ranking), "任意順位の要求を必須スコアの親へ混ぜません。");
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { OperationToken = 0 }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { OperationToken = token + 1 }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { Generation = 6 }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { Source = "scheduler:score_hydration_deferred" }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { Version = 9 }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { Source = "ranking_refresh_deferred" }));
        Assert.AreEqual(value, owner.Value);

    }

    /// <summary>同じ数値版を持つ外部同期・参照・行hydrationをsource別に追跡し、古い参照完了では進めず最新対応版の完了で一段だけ進めます。</summary>
    [TestMethod]
    public void StartupProgress_SharedPlaylistNumericSlotsDoNotConfuseDifferentRequestSources()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadTables);
        long token = owner.GetActiveStartupProgressOperationToken();
        var external = new OperationProgressRequest(4, token, "external_playlist_sync", 3);
        var reference = new OperationProgressRequest(4, token, "playlist_ref_apply", 3);
        var entries = new OperationProgressRequest(4, token, "playlist_entries_hydration", 3);
        owner.TrackStartupProgressExternalSyncRequest("ReloadTables", 3, token, external);
        owner.TrackStartupProgressPlaylistReferenceRequest("PlaylistEntriesHydration", 3, token, reference);
        owner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(3, "ReloadTables", token, entries);
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(external));
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(reference));
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(entries));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(reference with { Source = "scheduler:playlist_ref_apply" }));
        OperationProgressRequest latestReference = reference with { Version = 2 };
        owner.TrackStartupProgressPlaylistReferenceRequest("PlaylistEntriesHydration", 2, token, latestReference);
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(latestReference));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(reference));
        double before = owner.Value;
        owner.TryCompleteStartupProgressPlaylistReference(2, token);
        Assert.AreEqual(before, owner.Value, "表示用要求の実版を添えても既存 Math.Max による完了版判定を変えない。");
        owner.TryCompleteStartupProgressPlaylistReference(3, token);
        Assert.AreEqual(before + 1, owner.Value);
    }

    /// <summary>起動進捗が失敗しても別途稼働中のLR2同期行は名称・詳細・分子分母を維持し、親失敗による表示抑止を防ぎます。</summary>
    [TestMethod]
    public void Lr2SongDbSyncRunningStatus_IsVisibleWhenStartupProgressFailed()
    {

        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(
            new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.Running,
                Stage = "song_rows",
                StageProcessedCount = 4,
                StageTotalCount = 10
            },
            new DateTime(2026, 6, 5, 12, 0, 0));

        hub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        hub.UpdateLr2SongDbSyncStatus(status);
        Assert.IsTrue(hub.Rows.Any(row => row.Key == "lr2"));

        hub.StartupProgress.FailStartupProgressOperation("startup failed");

        Assert.IsTrue(hub.Rows.Any(row => row.Key == "lr2"));
        Assert.AreEqual(status.StatusText, hub.Rows.Single(row => row.Key == "lr2").Label);
        Assert.AreEqual(status.ProgressText, hub.Rows.Single(row => row.Key == "lr2").Detail);
        Assert.AreEqual(status.ProgressValue, hub.Rows.Single(row => row.Key == "lr2").Value);
        Assert.AreEqual(status.ProgressMaximum, hub.Rows.Single(row => row.Key == "lr2").Maximum);
    }

    /// <summary>DB・列挙・差分の実通知から子行を切り替え、件数と対象pathを表示して完了時に除去し、必須UI中だけUI準備行を表示します。</summary>
    [TestMethod]
    public void StartupProgress_LibraryChildrenFollowActualNotificationsAndUiPreparationBoundary()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        long token = owner.GetActiveStartupProgressOperationToken();
        Assert.AreEqual(string.Empty, owner.SubLabel);
        Assert.IsFalse(owner.DetailRows.Any());
        owner.UpdateStartupProgressLibraryInitializationStatuses([
            new(1, BMSLibrary.LibraryInitializationProgressStage.DatabaseLoad, "", 0, 0, "", token)]);
        Assert.AreEqual(Resources.Statusbar_progress_phase_library_db_load, owner.DetailRows.Single().Label);
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.UpdateStartupProgressLibraryInitializationStatuses([
            new(2, BMSLibrary.LibraryInitializationProgressStage.FileEnumeration, "Everything", 0, 0, "", token)]);
        Assert.AreEqual(Resources.Statusbar_progress_phase_file_enumeration + " (Everything)", owner.DetailRows.Single().Label);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.UpdateStartupProgressLibraryInitializationStatuses([
            new(3, BMSLibrary.LibraryInitializationProgressStage.FileDiff, "", 10, 3, "added.bms", token)]);
        Assert.AreEqual("[3/10] " + Resources.Statusbar_progress_phase_file_diff, owner.DetailRows.Single().Label);
        Assert.AreEqual("added.bms", owner.DetailRows.Single().Detail);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Assert.IsFalse(owner.DetailRows.Any());
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Assert.AreEqual(Resources.Statusbar_progress_phase_ui_prepare, owner.DetailRows.Single().Label);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Assert.AreEqual("task:ui_prepare", owner.DetailRows.Single().Key);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        Assert.IsFalse(owner.DetailRows.Any());
    }

    /// <summary>旧版の譜面情報進捗を無視し、現版の大量件数を保持して親の必須進捗へ任意workを加算しません。</summary>
    [TestMethod]
    public void StartupProgress_ChartInfoHydrationRejectsOldRequestAndKeepsCurrentCounts()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        foreach (StartupProgressPhase phase in new[]
        {
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ScoreHydrationDone,
        })
        {
            Skip(owner, phase);
        }
        owner.TrackStartupProgressChartInfoHydrationRequested(2);
        double parentValue = owner.Value;
        double parentMaximum = owner.Maximum;
        owner.UpdateStartupProgressChartInfoHydrationStatus(new(1, 99, 99, string.Empty));
        Assert.IsFalse(owner.DetailRows.Any(row => row.Key == "task:chart_info_hydration"));
        owner.UpdateStartupProgressChartInfoHydrationStatus(new(2, 209999, 1200, string.Empty));
        OperationProgressRow current = owner.DetailRows.Single(row => row.Key == "task:chart_info_hydration");
        Assert.AreEqual(209999d, current.Maximum);
        Assert.AreEqual(1200d, current.Value);
        owner.UpdateStartupProgressChartInfoHydrationStatus(new(1, 99, 99, string.Empty));
        Assert.AreEqual(current, owner.DetailRows.Single(row => row.Key == "task:chart_info_hydration"));

        StringAssert.Contains(current.Label, Resources.Statusbar_progress_phase_chart_info_load);
        Assert.AreEqual(string.Empty, current.Detail);
        Assert.AreEqual(parentValue, owner.Value);
        Assert.AreEqual(parentMaximum, owner.Maximum);
    }

    /// <summary>表再読込みの外部同期と参照適用を対応要求で完了し、不要なhydrationをskipして操作名付き完了表示へ進みます。</summary>
    [TestMethod]
    public void StartupProgress_ReloadTablesCompletesExternalSyncAndReferenceApply()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadTables);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        owner.TrackStartupProgressExternalSyncRequest("ReloadTables", 1);
        owner.TrackStartupProgressPlaylistReferenceRequest("DeferredExternalSync:ReloadTables", 1);
        owner.TryCompleteStartupProgressPlaylistReference(1);
        owner.TryCompleteStartupProgressExternalSync(1);
        Skip(owner, StartupProgressPhase.PlaylistEntriesHydrationDone);

        Assert.AreEqual(5.0, owner.Value);
        Assert.AreEqual(string.Format(Resources.Statusbar_progress_operation_completed_format, Resources.Statusbar_progress_reload_tables), owner.Label);
    }

    /// <summary>起動サービスの実接続でフォルダ修復reporterをHubへ配送し、修復件数と対象表を独立進捗行へ表示します。</summary>
    [TestMethod]
    public void StartupServiceAttachmentRoutesCustomFolderRepairProgressToHub()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_StartupRepairProgress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        MainWindowViewModel? owner = null;
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            owner = MainWindowViewModelTestFactory.Create(settings);
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, settings, owner);
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings, library: library);
            var profile = new LibraryProfile(
                operationModeLR2DB: false,
                songDbPath,
                [tempDirectory],
                lr2ConfigProvider: null,
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
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        new BmsLibraryDbGateway(scope.SongDbPath).EnsureAppOwnedSchema();
        PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
        TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(scope.SongDbPath, settings, owner);
        TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(scope.SongDbPath, settings, library: library);
        try
        {
            var profile = new LibraryProfile(false, scope.SongDbPath, [scope.DirectoryPath], null, null, false, false, false, false);
            IStartupLibraryApplicationPort applicationPort = owner;
            applicationPort.AttachStartupLibrary(library);
            applicationPort.AttachStartupServices(new StartupLibraryServices(profile, library, playlist));
            var failure = new IOException("controlled required LR2 failure for actual UI consumer");
            library.Lr2Synchronization.RecordLr2SongDbSyncFailure("consumer-signature", string.Empty, 0, failure, "failed");
            Assert.IsFalse(owner.ProgressHub.Rows.Any(row => row.Key == "lr2"), "結果投影前はこのfixtureに状態通知listenerを登録しない。");
            var result = new StartupRequiredInitializationResult(new LibraryFileInitializationResult(), failure);

            StartupRequiredInitializationResult consumed = await owner.InitializeStartupLibraryFilesAsync(() => Task.FromResult(result));

            Assert.AreSame(result, consumed);
            OperationProgressRow row = owner.ProgressHub.Rows.Single(value => value.Key == "lr2");
            Assert.IsFalse(row.IsChild);
            Assert.IsFalse(row.HasGauge);
            StringAssert.Contains(row.ToolTip, failure.Message);
            Assert.IsFalse(owner.IsInitializationCompleted, "結果の表示だけで初期化成功を確定しない。");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
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

    /// <summary>実Score適用のUI失敗はpendingと保存済み値を保持しURLを登録せず、次の明示Applyだけが成功後に実URL workへ進みます。保存通知だけの失敗は成立済み成功を保持します。</summary>
    [DataTestMethod]
    [DataRow(true)] // 必須UI失敗ではpendingを残し、次の明示ApplyまでURL workを開始しません。
    [DataRow(false)] // 保存通知だけの失敗では成立済み成功とURL workを維持します。
    [DoNotParallelize]
    public void SettingsScoreCompletion_RequiredUiFailureAndSavedNoticeFailureKeepDistinctFollowUp(bool uiFails)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var failures = new List<Exception>();
            using var fixture = new CompletionFixture(lr2Mode: true, lifetime: new TestApplicationLifetime(firstStartup: false), settingsFailureReporter: failures.Add);
            fixture.OptionalRelease.TrySetResult();
            Task<bool> initial = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(initial, "settings-score-initial");
            Assert.IsTrue(initial.GetAwaiter().GetResult());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "settings-score-prior-follow-up");
            var uiFailure = new IOException("required score UI failed");
            var noticeFailure = new IOException("saved backup notice failed");
            var notices = new List<UiMessageRequest>();
            var urlStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var urlRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var urlWork = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            int registrations = 0;
            int starts = 0;
            Func<string, string, string, Func<Task>, bool> scheduler = fixture.Playlist.StartupBackgroundTaskScheduler
                ?? throw new InvalidOperationException("Actual playlist scheduler is unavailable.");
            async Task RunUrlAsync(Func<Task> work)
            {
                Interlocked.Increment(ref starts);
                Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                Assert.IsFalse(fixture.Owner.IsLibraryOperationInProgress);
                urlStarted.TrySetResult();
                await urlRelease.Task;
                await work();
            }
            fixture.Playlist.StartupBackgroundTaskScheduler = (name, reason, dependency, work) =>
            {
                if (name != "playlist_url_completion") { return scheduler(name, reason, dependency, work); }
                registrations++;
                return scheduler(name, reason, dependency, () =>
                {
                    Task actual = RunUrlAsync(work);
                    urlWork.TrySetResult(actual);
                    return actual;
                });
            };
            SettingsDialogViewModel dialog = fixture.Owner.SettingDialog;
            dialog.BeatorajaPlayerId = "saved-score-player";
            dialog.EnablePlaylistUrlCompletion = true;
            dialog.PlaylistMd5UrlMappingTsvUri = string.Empty;
            dialog.EnableStellaFullPlaylistUrlCompletion = false;
            dialog.LR2BackupPath = fixture.Settings.BMSRootPath;
            dialog.IsLR2BackupEnabled = true;
            Assert.AreEqual(SettingsDialogViewModel.RestartMode.ScoreOnly, dialog.IsNeedRestartForSaved());
            fixture.Dialogs.MessageObservedAsync = request =>
            {
                notices.Add(request);
                return Task.FromException(noticeFailure);
            };
            fixture.Ui.IsCompletionBoundary = () => !fixture.Composition.OperationAdmission.IsActive
                && !fixture.Composition.PlaylistOperationAdmission.IsActive && fixture.Owner.IsLibraryOperationInProgress;
            fixture.Ui.Arm();
            Task apply = dialog.ApplySettingsAsync();
            fixture.Track(apply);
            try
            {
                AwaitArrival(fixture.Ui.Entered.Task, apply, "settings-score-required-ui");
                Assert.IsFalse(apply.IsCompleted);
                Assert.AreEqual(0, notices.Count, "保存案内は必須UIの終端より前に表示しません。");
                Assert.IsTrue(dialog.IsScoreReloadPending, "core成功だけでは必要UIまでのpendingを解除しません。");
                Assert.AreEqual(0, registrations);
                Assert.AreEqual("saved-score-player", fixture.Settings.BeatorajaPlayerId);
                Assert.IsTrue(fixture.Settings.EnablePlaylistUrlCompletion);
                if (uiFails) { fixture.Ui.Release.TrySetException(uiFailure); }
                else { fixture.Ui.Release.TrySetResult(); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "settings-score-terminal");
                Assert.AreEqual(uiFails ? 1 : 0, failures.Count);
                if (uiFails) { Assert.AreSame(uiFailure, failures.Single()); }
                Assert.AreEqual(1, notices.Count, "必須UIの成否にかかわらず、確定した保存の案内を一回試みます。");
                Assert.AreEqual(Resources.Msg_LR2ConfigBackupEnabledNextStartup, notices.Single().MessageBoxText);
                Assert.AreEqual(Resources.Confirm, notices.Single().Caption);
                Assert.AreEqual(System.Windows.MessageBoxImage.Asterisk, notices.Single().Icon);
                Assert.AreEqual(uiFails, dialog.IsScoreReloadPending);
                Assert.AreEqual(uiFails, dialog.HasPendingSettingChanges());
                if (uiFails)
                {
                    Assert.AreEqual(0, registrations, "保存済みURL変更でも必須UI失敗時は新規登録しません。");
                    Assert.AreEqual(0, starts);
                    dialog.RequestOpen();
                    Task retry = dialog.ApplySettingsAsync();
                    fixture.Track(retry);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(retry, "settings-score-next-explicit-apply");
                    Assert.IsFalse(dialog.IsScoreReloadPending);
                    Assert.IsFalse(dialog.HasPendingSettingChanges());
                    Assert.AreEqual(1, failures.Count);
                    Assert.AreEqual(1, notices.Count, "確定済み保存の案内を再試行で重ねません。");
                }
                Assert.AreEqual(1, registrations, "Score成功の共通ownerが保存済み設定で一回登録します。Settings側から重ねません。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(urlStarted.Task, urlWork.Task.Unwrap(), "settings-score-url-started"),
                    "settings-score-url-started");
                Assert.AreEqual(1, starts);
                urlRelease.TrySetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(urlWork.Task.Unwrap(), "settings-score-actual-url-terminal");
            }
            finally
            {
                fixture.Ui.Release.TrySetResult();
                urlRelease.TrySetResult();
                if (urlWork.Task.IsCompletedSuccessfully) { TestUiDispatcherHost.AwaitTaskOnDispatcher(urlWork.Task.Unwrap(), "settings-score-url-cleanup"); }
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
        TestUiDispatcherHost.Invoke(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "StartupScan-" + Guid.NewGuid().ToString("N"));
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
                    using var db = new LR2SongDBExtended(path.StandaloneSongDbPath);
                    db.RunInTransaction(() =>
                    {
                        db.CreateTable<LR2SongDB.song>();
                        ChartFile saved = ChartTestValues.Empty() with { Path = savedPath, Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Title = "Saved" };
                        db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(saved), typeof(LR2SongDB.song));
                    });
                }
                else { Assert.IsFalse(File.Exists(path.StandaloneSongDbPath)); }
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
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
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), applicationPathSnapshot: path,
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
                using (var db = new LR2SongDBExtended(path.StandaloneSongDbPath))
                {
                    string[] saved = db.Table<LR2SongDB.song>().Select(row => row.path).ToArray();
                    CollectionAssert.AreEqual(deleteRootAfterDatabaseRead ? new[] { savedPath } : Array.Empty<string>(), saved);
                }
                BMSLibrary createdLibrary = factory.CreatedLibrary ?? throw new AssertFailedException("実ライブラリが構成されませんでした。");
                string[] owned = createdLibrary.CreateOwnedChartInfoFullBackfillTargetSnapshotForDiagnostics().Select(chart => chart.Path).ToArray();
                CollectionAssert.AreEqual(deleteRootAfterDatabaseRead ? new[] { savedPath } : Array.Empty<string>(), owned);
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

    /// <summary>この終端領域だけで使う実UI Taskのgateです。Arm後の受付外の必須UI一回を保持します。</summary>
    internal sealed class CompletionUiScheduler(IUiScheduler inner) : IUiScheduler
    {
        private bool armed;
        private readonly object scheduledOperationsLock = new();
        private readonly List<Task> scheduledOperations = [];
        internal Func<bool> IsCompletionBoundary { get; set; } = () => false;
        internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm()
        {
            armed = true;
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public bool IsAvailable => inner.IsAvailable;
        public bool CanExecuteInline => inner.CanExecuteInline;
        public bool CheckAccess() => inner.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            IUiScheduledOperation operation = inner.Schedule(action, priority);
            lock (scheduledOperationsLock) { scheduledOperations.Add(operation.Completion); }
            return operation;
        }
        /// <summary>生成元の初期化・終了Taskを回収した後、既に登録された実表示Taskを古いsnapshotも含めて待ちます。</summary>
        internal Task JoinScheduledOperationsAsync()
        {
            lock (scheduledOperationsLock) { return Task.WhenAll(scheduledOperations); }
        }
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public async Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            if (armed && IsCompletionBoundary())
            {
                armed = false;
                Entered.TrySetResult();
                await Release.Task;
            }
            await inner.InvokeAsync(action, priority);
        }
        public async Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            if (armed && IsCompletionBoundary())
            {
                armed = false;
                Entered.TrySetResult();
                await Release.Task;
            }
            await inner.InvokeAsync(action, priority);
        }
    }

    /// <summary>通常起動の実構成・DB・L/Pを使い、必須UIと既存任意workだけを到達前に捕捉します。</summary>
    internal sealed class CompletionFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "StartupCompletion-" + Guid.NewGuid().ToString("N"));
        private readonly List<Task> tasks = [];
        private Func<string, string, string, Func<Task>, bool>? wrappedScheduler;
        private readonly bool isolateSortWarmup;
        internal Settings Settings { get; }
        internal ApplicationComposition Composition { get; }
        internal IApplicationLifetimePort Lifetime { get; }
        internal MainWindowViewModel Owner { get; }
        internal TestBmsLibrary Library { get; }
        internal TestBmsPlaylist Playlist { get; }
        internal CompletionUiScheduler Ui { get; } = new(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        internal CompletionDialogs Dialogs { get; } = new();
        internal RecordingSettingsDialogPresentationPort Presentation { get; } = new();
        internal Exception? OptionsFailure { get; set; }
        internal StartupBackgroundTaskSchedulerOwner Scheduler => GetPrivateField<StartupBackgroundTaskSchedulerOwner>(Owner, "startupBackgroundTaskScheduler");
        internal TaskCompletionSource OptionalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OptionalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object maintenanceRequestGate = new();
        private Func<bool>? deferredMaintenanceRequest;
        private bool maintenanceRequestReleased;
        internal TaskCompletionSource<StartupPostInitializationWarmupCompletion> SortWarmupCompletion { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ArmSortWarmupGate()
        {
            lock (maintenanceRequestGate)
            {
                Assert.IsNull(deferredMaintenanceRequest, "前回の保守要求を回収してから次の起動へ進みます。");
                maintenanceRequestReleased = false;
                SortWarmupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        /// <summary>保持した実要求を一度だけ元schedulerへ渡します。拒否を完了として扱わず失敗にします。</summary>
        internal void ReleaseDeferredMaintenance()
        {
            Func<bool>? submit;
            lock (maintenanceRequestGate)
            {
                maintenanceRequestReleased = true;
                submit = deferredMaintenanceRequest;
                deferredMaintenanceRequest = null;
            }
            if (submit != null && !submit())
            {
                throw new AssertFailedException("保留した保守要求が元schedulerに拒否されました。");
            }
        }
        private readonly TaskCompletionSource<Task> optionalWork = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task OptionalWork => optionalWork.Task.Unwrap();
        internal bool OptionalStartedAfterCompletion { get; private set; }
        internal int CompletionNotifications { get; private set; }
        internal CompletionFixture(IStartupLibraryInitializationFailurePresenter? failurePresenter = null,
            Func<IBMSPlayer>? defaultPlayer = null, bool lr2Mode = false, IApplicationLifetimePort? lifetime = null,
            Action<Exception>? settingsFailureReporter = null, Func<Settings, ISettingsEditSession>? settingsSessionFactory = null, bool isolateSortWarmup = false)
        {
            this.isolateSortWarmup = isolateSortWarmup;
            Lifetime = lifetime ?? new TestApplicationLifetime(firstStartup: true);
            Directory.CreateDirectory(directory);
            string lr2DatabaseDirectory = Path.Combine(directory, "LR2files", "Database");
            string lr2ConfigDirectory = Path.Combine(directory, "LR2files", "Config");
            string lr2SongDb = Path.Combine(lr2DatabaseDirectory, "song.db");
            string lr2Config = Path.Combine(lr2ConfigDirectory, "config.xml");
            if (lr2Mode)
            {
                Directory.CreateDirectory(lr2DatabaseDirectory);
                Directory.CreateDirectory(lr2ConfigDirectory);
                File.WriteAllText(lr2Config, "<config><system /><jukebox /></config>");
                File.WriteAllBytes(Path.Combine(directory, "LR2body.exe"), []);
                string songs = Path.Combine(directory, "Songs");
                Directory.CreateDirectory(songs);
                var config = new LR2Config(lr2Config);
                config.AddBMSSearchDirectories([songs]);
                config.Save();
                Directory.CreateDirectory(Path.Combine(directory, "NormalOutput"));
                Directory.CreateDirectory(Path.Combine(directory, "RootOutput"));
            }
            Settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = lr2Mode;
                if (lr2Mode)
                {
                    values.LR2RootPath = directory;
                    values.LR2SongDBPath = lr2SongDb;
                    values.LR2ConfigXmlPath = lr2Config;
                    values.LR2CustomFolderOutputBaseDir = Path.Combine(directory, "NormalOutput");
                    values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput");
                    values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
                }
                values.BMSRootPath = directory;
                values.StandaloneBmsRootPaths = directory;
                values.BMSInstallDir = lr2Mode ? Path.Combine(directory, "Songs") : directory;
                values.ScanBmsFilesOnStartup = false;
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
            var path = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory, "BeMusicSeeker.exe"));
            Directory.CreateDirectory(path.DataDirectoryPath);
            string songDb = lr2Mode ? lr2SongDb : path.StandaloneSongDbPath;
            StartupLibraryConstructionTestSupport.CreateSongDatabase(songDb);
            new BmsLibraryDbGateway(songDb).EnsureAppOwnedSchema();
            PlaylistPersistenceRepository.EnsureSchema(songDb);
            Composition = new ApplicationComposition(settingsEditSession: settingsSessionFactory?.Invoke(Settings) ?? new NoOpSettingsEditSession(Settings), reportSettingsApplyFailure: settingsFailureReporter,
                uiScheduler: Ui, applicationLifetime: Lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                applicationPathSnapshot: path, fileDbMutationDialogService: Dialogs, settingsDialogService: Dialogs,
                defaultBmsPlayerFactory: defaultPlayer);
            Library = new TestBmsLibrary(songDb, lr2Mode ? () => new LR2Config(lr2Config) : null, null, null, null,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), CaptureOptions,
                chartFileScanner: CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), []),
                operationAdmission: Composition.OperationAdmission, playlistOperationAdmission: Composition.PlaylistOperationAdmission);
            Playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDb, Settings,
                getLr2Config: lr2Mode ? () => new LR2Config(lr2Config) : null, library: Library);
            Owner = new MainWindowViewModel(Composition, new CompletionLibraryFactory(Library, Playlist),
                startupLibraryInitializationFailurePresenter: failurePresenter ?? new CallbackFailurePresenter(_ => { }));
            if (isolateSortWarmup)
            {
                StartupPostInitializationWarmupOwner warmup = GetPrivateField<StartupPostInitializationWarmupOwner>(Owner, "startupPostInitializationWarmupOwner");
                FieldInfo callbackField = typeof(StartupPostInitializationWarmupOwner).GetField("completed", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertFailedException("Warmup completion callback is unavailable.");
                var original = (Action<StartupPostInitializationWarmupCompletion>)(callbackField.GetValue(warmup)
                    ?? throw new AssertFailedException("Warmup completion callback is missing."));
                callbackField.SetValue(warmup, (Action<StartupPostInitializationWarmupCompletion>)(completion =>
                {
                    try { original(completion); SortWarmupCompletion.TrySetResult(completion); }
                    catch (Exception failure) { SortWarmupCompletion.TrySetException(failure); throw; }
                }));
            }
            Owner.SettingDialog.AttachPresentationPort(Presentation);
            Owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            Owner.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsInitializationCompleted) && Owner.IsInitializationCompleted) { CompletionNotifications++; }
            };
            Ui.IsCompletionBoundary = () => !Composition.OperationAdmission.IsActive && !Composition.PlaylistOperationAdmission.IsActive
                && Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked;
        }
        private BmsLibraryOptionsSnapshot CaptureOptions()
        {
            if (OptionsFailure != null) { throw OptionsFailure; }
            if (Library?.StartupBackgroundTaskScheduler is { } scheduler && scheduler != wrappedScheduler)
            {
                wrappedScheduler = (name, reason, dependency, work) =>
                {
                    if (isolateSortWarmup && name == "maintenance_hydration")
                    {
                        lock (maintenanceRequestGate)
                        {
                            if (!maintenanceRequestReleased)
                            {
                                Assert.IsNull(deferredMaintenanceRequest, "同じ初期化の保守要求を重複保持しません。");
                                deferredMaintenanceRequest = () => scheduler(name, reason, dependency, work);
                                return true;
                            }
                        }
                    }
                    return scheduler(name, reason, dependency,
                        name == "chart_info_backfill" ? () =>
                        {
                            Task actualWork = RunOptionalWorkAsync(work);
                            optionalWork.TrySetResult(actualWork);
                            return actualWork;
                        }
                    : work);
                };
                Library.StartupBackgroundTaskScheduler = wrappedScheduler;
            }
            return BmsLibraryOptionsSnapshot.CreateCurrent(Settings);
        }
        private async Task RunOptionalWorkAsync(Func<Task> work)
        {
            OptionalStartedAfterCompletion = Owner.IsInitializationCompleted && !Lifetime.IsFirstStartup
                && !Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked && Scheduler.IsPostInitializationSchedulingComplete;
            OptionalEntered.TrySetResult();
            await OptionalRelease.Task;
            await work();
        }
        internal Task<bool> Start()
        {
            Task<bool> task = Owner.InitializeAsync();
            Track(task);
            return task;
        }
        /// <summary>実schedulerの全後続終端が既存Hubへ届く通知だけを待ちます。必須成功の条件には使いません。</summary>
        internal async Task WaitForBackgroundTerminalAsync()
        {
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (!Owner.ProgressHub.IsStartupBackgroundInitializationActive && Scheduler.IsFullyIdle) { terminal.TrySetResult(); }
            }
            Owner.ProgressHub.PropertyChanged += Changed;
            try
            {
                if (!Owner.ProgressHub.IsStartupBackgroundInitializationActive && Scheduler.IsFullyIdle) { return; }
                await terminal.Task;
            }
            finally { Owner.ProgressHub.PropertyChanged -= Changed; }
        }
        internal void Track(Task task) => tasks.Add(task);
        internal void AssertBeforeUiCompletion()
        {
            Assert.IsFalse(Composition.OperationAdmission.IsActive);
            Assert.IsFalse(Composition.PlaylistOperationAdmission.IsActive);
            Assert.IsFalse(Owner.IsInitializationCompleted);
            Assert.IsFalse(Owner.HasActiveLibraryProfile);
            Assert.IsTrue(Lifetime.IsFirstStartup);
            Assert.IsTrue(Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsTrue(Owner.IsLibraryOperationInProgress, "必須UI実Taskの終端前は実成功を公開しません。");
            Assert.IsFalse(Scheduler.IsStarted);
            Assert.IsFalse(Scheduler.IsPostInitializationSchedulingComplete);
            Assert.AreEqual(0, CompletionNotifications);
        }
        public void Dispose()
        {
            OptionsFailure = null;
            Ui.Release.TrySetResult();
            OptionalRelease.TrySetResult();
            try { ReleaseDeferredMaintenance(); }
            finally
            {
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(tasks), "startup-completion-fixture-accepted-tasks"); }
                finally
                {
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "startup-completion-fixture-shutdown"); }
                    finally { Owner.SettingDialog.Dispose(); Directory.Delete(directory, recursive: true); }
                }
            }
        }
    }

    private sealed class CompletionLibraryFactory(BMSLibrary library, BMSPlaylist playlist) : IStartupLibraryFactory
    {
        public BMSLibrary CreateBmsLibrary(LibraryProfile profile) => library;
        public BMSPlaylist CreateBmsPlaylist(LibraryProfile profile, BMSLibrary source) => playlist;
    }
    private sealed class CallbackFailurePresenter(Action<StartupLibraryInitializationFailurePresentation> present) : IStartupLibraryInitializationFailurePresenter
    {
        public void Present(StartupLibraryInitializationFailurePresentation presentation) => present(presentation);
    }
    internal sealed class CompletionDialogs : IUiDialogService
    {
        internal Func<UiMessageRequest, Task>? MessageObservedAsync { get; set; }
        internal MessageBoxResult ConfirmationResult { get; set; } = MessageBoxResult.Yes;
        internal int ConfirmationCount { get; private set; }
        public async Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            if (MessageObservedAsync != null) { await MessageObservedAsync(request); }
            return UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        }
        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(ConfirmationResult));
        }
        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default) where TWindow : Window => throw new NotSupportedException();
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static StartupProgressWorkflowOwner Start(StartupProgressOperationKind operationKind)
    {

        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        owner.StartStartupProgressOperation(operationKind);
        return owner;
    }

    private static void Mark(StartupProgressWorkflowOwner owner, StartupProgressPhase phase)
    {
        owner.MarkStartupProgressPhaseCompleted(phase, owner.GetActiveStartupProgressOperationToken());
    }

    private static void Skip(StartupProgressWorkflowOwner owner, StartupProgressPhase phase)
    {
        owner.SkipStartupProgressPhaseIfExpected(
            phase,
            "test",
            owner.GetActiveStartupProgressOperationToken());
    }


    private static void CompleteRequiredProgressForPresentation(StartupProgressWorkflowOwner owner)
    {
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        foreach (StartupProgressPhase phase in new[]
        {
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ScoreHydrationDone,
        })
        {
            Skip(owner, phase);
        }
    }

    /// <summary>初期化失敗の実通知を記録し、実Window終了ケースでも新規提示を観測します。</summary>
    internal sealed class RecordingStartupLibraryInitializationFailurePresenter
        : IStartupLibraryInitializationFailurePresenter
    {
        internal List<StartupLibraryInitializationFailurePresentation> Presentations { get; } = new();

        public void Present(StartupLibraryInitializationFailurePresentation presentation)
            => Presentations.Add(presentation);
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        return (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;
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
    /// <summary>各操作は実行中と操作可能時の名称を保ち、失敗では操作名と原因詳細を分けて空原因でも名称を失いません。</summary>
    [DataTestMethod]
    [DataRow(StartupProgressOperationKind.Startup, "Statusbar_progress_startup")]
    [DataRow(StartupProgressOperationKind.ReloadFileDiff, "Statusbar_progress_reload_files")]
    [DataRow(StartupProgressOperationKind.ScoreOnly, "Statusbar_progress_reload_scores")]
    [DataRow(StartupProgressOperationKind.FullReinitialize, "Statusbar_progress_full_reinitialize")]
    [DataRow(StartupProgressOperationKind.ReloadTables, "Statusbar_progress_reload_tables")]
    public void StartupProgress_OperationNameAndFailureReasonStayDistinct(object operationKindValue, string resourceKey)
    {
        var operationKind = (StartupProgressOperationKind)operationKindValue;
        StartupProgressWorkflowOwner owner = Start(operationKind);
        string expected = Resources.ResourceManager.GetString(resourceKey) ?? throw new InvalidOperationException(resourceKey);
        Assert.AreEqual(expected, owner.Label);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        Assert.AreEqual(expected, owner.Label);
        Assert.AreEqual(string.Empty, owner.SubLabel);
        owner.FailStartupProgressOperation("");
        Assert.AreEqual(string.Format(Resources.Statusbar_progress_operation_failed_format, expected), owner.Label);
        Assert.AreEqual(string.Empty, owner.SubLabel);
        owner.FailStartupProgressOperation("failure detail");
        Assert.AreEqual("failure detail", owner.SubLabel);
    }

}
