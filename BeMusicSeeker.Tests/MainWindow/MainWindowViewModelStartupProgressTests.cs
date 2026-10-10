using System;
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
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelStartupProgressTests
{
    [TestMethod]
    public void StartupProgress_RequestForUnexpectedPhaseDoesNotChangeOwnerState()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadTables);

        double maximum = owner.Maximum;
        double value = owner.Value;
        owner.TrackStartupProgressScoreHydrationRequested(1);

        Assert.AreEqual(maximum, owner.Maximum);
        Assert.AreEqual(value, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_reload_tables, owner.Label);
    }

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

    /// <summary>実起動の必須UIを保持し、親解放→UI実終端→初回完了・解禁→任意登録閉鎖と開始の順を観測します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupRequiredUi_ActualTaskOwnsCompletionBeforeOptionalWorkStarts()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var fixture = new CompletionFixture();
            fixture.Ui.Arm();
            Task<bool> initialization = fixture.Start();
            AwaitArrival(fixture.Ui.Entered.Task, initialization, "startup-required-ui");
            fixture.AssertBeforeUiCompletion();
            Assert.IsFalse(initialization.IsCompleted);
            fixture.Ui.Release.TrySetResult();
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
        });
    }

    /// <summary>ツリー全再初期化でも実必須UIの外側へ後続登録を集約し、別hostやサービス構築を追加しません。</summary>
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

    [TestMethod]
    [DoNotParallelize]
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

    [TestMethod]
    public void StartupProgress_FailureClearsInteractionBlockBeforeActiveOperationCheck()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        owner.SetStartupUiInteractionBlocked(true);

        owner.FailStartupProgressOperation("startup failed before operation");

        Assert.IsFalse(owner.IsStartupUiInteractionBlocked);
    }

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
    [DataRow(false)]
    [DataRow(true)]
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

    /// <summary>解禁後の初回案内が失敗してもlocal/UI成功・初回完了を維持し、元の実通知失敗を一回報告し、登録を閉鎖した実後続も終端します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void StartupCompletionNotice_FailureKeepsPublishedSuccessAndUnblockedOperations()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var failure = new IOException("initial completion notice failed");
            var recorded = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new CompletionFixture(new CallbackFailurePresenter(recorded.Add));
            fixture.Dialogs.MessageObservedAsync = request =>
            {
                Assert.AreEqual(Resources.Msg_init_completed, request.MessageBoxText);
                Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
                Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
                Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
                Assert.IsTrue(fixture.Owner.HasActiveLibraryProfile);
                Assert.IsFalse(fixture.Lifetime.IsFirstStartup);
                Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                return Task.FromException(failure);
            };
            Task optionalWork = fixture.OptionalWork;
            Task optionalEntered = fixture.OptionalEntered.Task;
            Task<bool> initialization = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-initial-completion-notice-failure");
            Assert.IsFalse(initialization.GetAwaiter().GetResult(), "案内の実失敗をAPIの通常成功に読み替えません。");
            Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
            Assert.IsTrue(fixture.Owner.HasActiveLibraryProfile);
            Assert.IsFalse(fixture.Lifetime.IsFirstStartup);
            Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.AreEqual(1, fixture.CompletionNotifications);
            Assert.AreEqual(1, recorded.Count);
            Assert.AreSame(failure, recorded[0].Exception);
            Assert.IsTrue(fixture.Scheduler.IsStarted);
            Assert.IsTrue(fixture.Scheduler.IsPostInitializationSchedulingComplete);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(optionalEntered, optionalWork,
                "startup-notice-failure-optional-entered"), "startup-notice-failure-optional-arrival");
            Assert.IsTrue(fixture.OptionalStartedAfterCompletion);
            fixture.OptionalRelease.TrySetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(optionalWork, "startup-notice-failure-optional-terminal");
        });
    }

    /// <summary>親解放後のUI待機で終了が先着した場合だけ取消へ分類し、同時に発生した実UI障害は保持します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
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
            if (realFailure) { fixture.Ui.Release.TrySetException(new IOException("real UI failure during shutdown")); }
            else { fixture.Ui.Release.TrySetResult(); }
            TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "startup-ui-shutdown-terminal");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "startup-ui-shutdown-drain");
            Assert.IsFalse(initialization.GetAwaiter().GetResult());
            Assert.IsTrue(fixture.Lifetime.IsFirstStartup);
            Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
            Assert.IsFalse(fixture.Owner.HasActiveLibraryProfile);
            Assert.IsFalse(fixture.Scheduler.IsPostInitializationSchedulingComplete);
            Assert.IsFalse(fixture.OptionalEntered.Task.IsCompleted, "受理済み必須cleanupのdrainは許可し、任意workは開始しません。");
            Assert.IsTrue(fixture.Scheduler.IsFullyIdle);
            Assert.AreEqual(0, recorded.Count, "終了開始後に新しい失敗画面を開きません。");
            Assert.AreEqual(realFailure, fixture.Owner.ProgressHub.StartupProgress.IsFailed);
            if (realFailure) { StringAssert.Contains(fixture.Owner.ProgressHub.StartupProgress.SubLabel, "real UI failure"); }
            Assert.AreEqual(0, fixture.CompletionNotifications);
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

    [TestMethod]
    public void StartupProgress_RequestEligibilityUsesCurrentOperationAtomically()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);

        owner.TrackStartupProgressScoreHydrationRequested(1);

        Assert.AreEqual(5.0, owner.Maximum);
        Assert.AreEqual(1.0, owner.Value);
        Assert.AreEqual(StartupProgressOperationKind.ReloadTables, owner.CurrentOperationKind);
    }

    [TestMethod]
    public void StartupProgress_StaleTokenCannotCompleteNewOperation()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        long staleToken = owner.GetActiveStartupProgressOperationToken();
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);

        owner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, staleToken);

        Assert.AreEqual(1.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_reload_tables, owner.Label);
    }

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

    [TestMethod]
    public void StartupProgress_ScoreCompletionWithoutRequestIsIgnored()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);

        owner.TryCompleteStartupProgressScoreHydration(1);

        Assert.AreEqual(1.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_full_reinitialize, owner.Label);
    }

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

    [TestMethod]
    public void StartupProgress_OperableKeepsOperationLabelAndEmptySubLabel()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);

        Assert.AreEqual(Resources.Statusbar_progress_full_reinitialize, owner.Label);
        Assert.AreEqual(string.Empty, owner.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_PlaylistEntriesHydrationCanCompleteDirectly()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        owner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(1, "test");
        owner.TryCompleteStartupProgressPlaylistEntriesHydration(1);

        Assert.AreEqual(2.0, owner.Value);
    }

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

    [TestMethod]
    public void Lr2SongDbSyncWarningStatus_IsVisibleWhenStartupProgressFailed()
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

    [TestMethod]
    [DoNotParallelize]
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
    [DataRow(true)]
    [DataRow(false)]
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
                Assert.AreEqual(Resources.Msg_LR2ConfigBackupEnabledNextStartup, request.MessageBoxText);
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
                Assert.IsTrue(dialog.IsScoreReloadPending, "core成功だけでは必要UIまでのpendingを解除しません。");
                Assert.AreEqual(0, registrations);
                Assert.AreEqual("saved-score-player", fixture.Settings.BeatorajaPlayerId);
                Assert.IsTrue(fixture.Settings.EnablePlaylistUrlCompletion);
                if (uiFails) { fixture.Ui.Release.TrySetException(uiFailure); }
                else { fixture.Ui.Release.TrySetResult(); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "settings-score-terminal");
                Assert.AreEqual(1, failures.Count);
                Assert.AreSame(uiFails ? uiFailure : noticeFailure, failures.Single());
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
                }
                Assert.AreEqual(1, registrations, "Score成功の共通ownerが保存済み設定で一回登録します。Settings側から重ねません。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(urlStarted.Task, "settings-score-url-started");
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

    private static void AwaitArrival(Task arrival, Task initialization, string name)
    {
        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(arrival, initialization, name), name);
    }

    /// <summary>この終端領域だけで使う実UI Taskのgateです。Arm後の受付外の必須UI一回を保持します。</summary>
    internal sealed class CompletionUiScheduler(IUiScheduler inner) : IUiScheduler
    {
        private bool armed;
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
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Schedule(action, priority);
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
        internal Settings Settings { get; }
        internal ApplicationComposition Composition { get; }
        internal IApplicationLifetimePort Lifetime { get; }
        internal MainWindowViewModel Owner { get; }
        internal TestBmsLibrary Library { get; }
        internal TestBmsPlaylist Playlist { get; }
        internal CompletionUiScheduler Ui { get; } = new(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        internal CompletionDialogs Dialogs { get; } = new();
        internal StartupBackgroundTaskSchedulerOwner Scheduler => GetPrivateField<StartupBackgroundTaskSchedulerOwner>(Owner, "startupBackgroundTaskScheduler");
        internal TaskCompletionSource OptionalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OptionalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Task> optionalWork = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task OptionalWork => optionalWork.Task.Unwrap();
        internal bool OptionalStartedAfterCompletion { get; private set; }
        internal int CompletionNotifications { get; private set; }
        internal CompletionFixture(IStartupLibraryInitializationFailurePresenter? failurePresenter = null,
            Func<IBMSPlayer>? defaultPlayer = null, bool lr2Mode = false, IApplicationLifetimePort? lifetime = null,
            Action<Exception>? settingsFailureReporter = null)
        {
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
            Composition = new ApplicationComposition(settingsEditSession: new NoOpSettingsEditSession(Settings), reportSettingsApplyFailure: settingsFailureReporter,
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
            Owner.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort());
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
            if (Library?.StartupBackgroundTaskScheduler is { } scheduler && scheduler != wrappedScheduler)
            {
                wrappedScheduler = (name, reason, dependency, work) => scheduler(name, reason, dependency,
                    name == "chart_info_backfill" ? () =>
                    {
                        Task actualWork = RunOptionalWorkAsync(work);
                        optionalWork.TrySetResult(actualWork);
                        return actualWork;
                    }
                : work);
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
            Ui.Release.TrySetResult();
            OptionalRelease.TrySetResult();
            try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(tasks), "startup-completion-fixture-accepted-tasks"); }
            finally
            {
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "startup-completion-fixture-shutdown"); }
                finally { Owner.SettingDialog.Dispose(); Directory.Delete(directory, recursive: true); }
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

    private static void SkipAllOptionalStartupPhases(StartupProgressWorkflowOwner owner)
    {
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

    private static void InvokePrivate(object target, string name, object[] arguments)
    {
        MethodInfo method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name
                && candidate.GetParameters().Length == arguments.Length);
        method.Invoke(target, arguments);
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
    [DataTestMethod]
    [DataRow(1, "Statusbar_progress_startup")]
    [DataRow(2, "Statusbar_progress_reload_files")]
    [DataRow(3, "Statusbar_progress_reload_scores")]
    [DataRow(4, "Statusbar_progress_full_reinitialize")]
    [DataRow(5, "Statusbar_progress_reload_tables")]
    public void StartupProgress_OperationNameAndFailureReasonStayDistinct(int operationKind, string resourceKey)
    {
        StartupProgressWorkflowOwner owner = Start((StartupProgressOperationKind)operationKind);
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
