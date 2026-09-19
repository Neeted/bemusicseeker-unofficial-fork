using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelStartupProgressTests
{
    [TestMethod]
    public void StartupProgress_InitialExpectedCounts_ArePublishedByOwner()
    {
        Assert.AreEqual(13.0, Start(StartupProgressOperationKind.Startup).Maximum);
        Assert.AreEqual(6.0, Start(StartupProgressOperationKind.ReloadFileDiff).Maximum);
        Assert.AreEqual(4.0, Start(StartupProgressOperationKind.ScoreOnly).Maximum);
        Assert.AreEqual(14.0, Start(StartupProgressOperationKind.FullReinitialize).Maximum);
        Assert.AreEqual(5.0, Start(StartupProgressOperationKind.ReloadTables).Maximum);
    }

    [TestMethod]
    public void StartupProgress_StartupDoesNotWaitForPostInitializationMaintenance()
    {
        StartupProgressPhase expected = StartupProgressWorkflowOwner.GetInitialExpectedStartupProgressPhases(StartupProgressOperationKind.Startup);

        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.PlaylistReferenceApplied);
        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.ExternalPlaylistSyncDone);
        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.RankingRefreshDone);
        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.MaintenanceDeferredDone);
        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.InstallableMaintenanceDeferredDone);
        Assert.AreNotEqual(StartupProgressPhase.None, expected & StartupProgressPhase.PlaylistEntriesHydrationDone);
        Assert.AreNotEqual(StartupProgressPhase.None, expected & StartupProgressPhase.ChartInfoHydrationDone);
        Assert.AreNotEqual(StartupProgressPhase.None, expected & StartupProgressPhase.StartupBackgroundTasksDone);
    }

    [TestMethod]
    public void StartupProgress_RequestForUnexpectedPhaseDoesNotChangeOwnerState()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadTables);

        owner.TrackStartupProgressScoreHydrationRequested(1);

        Assert.AreEqual(5.0, owner.Maximum);
        Assert.AreEqual(1.0, owner.Value);
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

    [TestMethod]
    public void StartupReadyOperable_StartsSchedulerAfterLatchingBackgroundPresentation()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            try
            {
                StartupProgressWorkflowOwner progress = owner.ProgressHub.StartupProgress;
                long operationToken = progress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
                progress.ApplyPresentation(false, null, null, 0.0, 1.0);
                owner.ProgressHub.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(OperationProgressHubViewModel.IsStartupBackgroundInitializationActive))
                    {
                        throw new InvalidOperationException("background presentation observer failed");
                    }
                };
                // owner の生成から通知購読、private 境界、後始末まで同じ UI dispatcher で観測する。
                SetPrivateField(owner, "startupReadyUiReached", true);
                SetPrivateField(owner, "startupReadyOperableStopwatch", Stopwatch.StartNew());

                InvokePrivate(owner, "TryLogStartupReadyOperable", [(object)operationToken]);

                StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
                    owner,
                    "startupBackgroundTaskScheduler");
                Assert.IsTrue(scheduler.IsStarted);
                progress.ApplyPresentation(false, null, null, 0.0, 1.0);
                Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive);
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void StartupPostInitializationCompositeTerminal_ClearsBackgroundPresentation()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        try
        {
            owner.ProgressHub.BeginStartupBackgroundInitializationPresentation();
            Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive);

            StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
                owner,
                "startupBackgroundTaskScheduler");
            scheduler.Start();
            scheduler.MarkPostInitializationSchedulingComplete();
            SetPrivateField(owner, "startupPostInitializationCompletionTracking", true);
            SetPrivateField(owner, "startupInitializationCompleteLogged", true);
            SetPrivateField(owner, "startupPostInitializationWarmupScheduled", true);
            SetPrivateField(owner, "startupPostInitializationWarmupCompleted", true);

            InvokePrivate(owner, "TryLogStartupPostInitializationComplete", []);

            Assert.IsFalse(owner.ProgressHub.IsStartupBackgroundInitializationActive);
            Assert.IsTrue(GetPrivateField<bool>(owner, "startupPostInitializationCompletionLogged"));
        }
        finally
        {
            owner.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public void NonStartupProgressOperation_ResetsBackgroundPresentationLatch()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        try
        {
            owner.ProgressHub.BeginStartupBackgroundInitializationPresentation();
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

    [TestMethod]
    public async Task StartupProgress_BackgroundTasksWaitForRequiredSchedulingClosure()
    {
        bool requiredSchedulingClosed = false;
        var hideEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHide = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create(
            completionHideDelay: () =>
            {
                hideEntered.TrySetResult(true);
                return releaseHide.Task;
            },
            backgroundTasksIdle: () => true,
            requiredInitializationSchedulingComplete: () => requiredSchedulingClosed);
        owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        CompleteUntilBackground(owner);

        owner.TryCompleteStartupBackgroundTasksPhaseIfIdle(owner.GetActiveStartupProgressOperationToken());
        Assert.AreNotEqual(Resources.Statusbar_progress_complete, owner.Label);

        requiredSchedulingClosed = true;
        owner.TryCompleteStartupBackgroundTasksPhaseIfIdle(owner.GetActiveStartupProgressOperationToken());
        await hideEntered.Task;
        Assert.AreEqual(Resources.Statusbar_progress_complete, owner.Label);

        releaseHide.TrySetResult(true);
    }

    [TestMethod]
    public async Task StartupLibraryInitializationFailure_PreservesRootFailurePolicyAndStopsBeforeReadiness()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var settings = new Settings();
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var failurePresenter = new RecordingStartupLibraryInitializationFailurePresenter();
        var viewModel = new MainWindowViewModel(
            composition,
            composition,
            startupLibraryInitializationFailurePresenter: failurePresenter);
        var settingsPresentation = new RecordingSettingsDialogPresentationPort();
        viewModel.SettingDialog.AttachPresentationPort(settingsPresentation);
        StartupProgressWorkflowOwner progress = viewModel.ProgressHub.StartupProgress;
        long operationToken = progress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        progress.SetStartupUiInteractionBlocked(true);
        var failure = new InvalidOperationException("file initialization failed");

        try
        {
            bool initialized = await viewModel.InitializeStartupLibraryFilesAsync(
                () => throw failure,
                operationToken,
                startupCustomFolderSettings: null);
            TestUiDispatcherHost.Drain();

            Assert.IsFalse(initialized);
            Assert.AreEqual(1, failurePresenter.Presentations.Count);
            Assert.AreSame(failure, failurePresenter.Presentations[0].Exception);
            // 再表示はファイル初期化の内側ではなく、外側の排他解放後に行う。
            Assert.AreEqual(0, settingsPresentation.Requests.Count);
            Assert.IsTrue(progress.IsFailed);
            Assert.IsTrue(progress.IsRetryableFailure);
            Assert.IsFalse(progress.IsStartupUiInteractionBlocked);
            Assert.IsFalse(progress.IsStartupInitializationRequiredProgressComplete(operationToken));
            Assert.AreEqual(1.0, progress.Value);
            Assert.IsFalse(viewModel.IsInitializationCompleted);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);
        }
        finally
        {
            viewModel.SettingDialog.DetachPresentationPort(settingsPresentation);
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public async Task StartupLibraryInitializationFailure_WhenPresenterThrows_PreservesPrimaryFailureAndCleanup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var settings = new Settings();
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var presentationFailure = new InvalidOperationException("failure presentation failed");
        var failurePresenter = new ThrowingStartupLibraryInitializationFailurePresenter(presentationFailure);
        var viewModel = new MainWindowViewModel(
            composition,
            composition,
            startupLibraryInitializationFailurePresenter: failurePresenter);
        var settingsPresentation = new RecordingSettingsDialogPresentationPort();
        viewModel.SettingDialog.AttachPresentationPort(settingsPresentation);
        StartupProgressWorkflowOwner progress = viewModel.ProgressHub.StartupProgress;
        long operationToken = progress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        progress.SetStartupUiInteractionBlocked(true);
        var initializationFailure = new InvalidOperationException("file initialization failed");
        using var gate = new SemaphoreSlim(1, 1);
        var gateOwner = new StartupLibraryInitializationWorkflowOwner(gate);

        try
        {
            bool initialized;
            using (await gateOwner.AcquireGateAsync())
            {
                initialized = await viewModel.InitializeStartupLibraryFilesAsync(
                    () => throw initializationFailure,
                    operationToken,
                    startupCustomFolderSettings: null);
            }
            TestUiDispatcherHost.Drain();

            Assert.IsFalse(initialized);
            Assert.AreEqual(1, failurePresenter.Presentations.Count);
            Assert.AreSame(initializationFailure, failurePresenter.Presentations[0].Exception);
            // 再表示はファイル初期化の内側ではなく、外側の排他解放後に行う。
            Assert.AreEqual(0, settingsPresentation.Requests.Count);
            Assert.IsTrue(progress.IsFailed);
            Assert.IsTrue(progress.IsRetryableFailure);
            Assert.AreEqual(initializationFailure.Message, progress.SubLabel);
            Assert.IsFalse(progress.IsStartupUiInteractionBlocked);
            Assert.IsFalse(progress.IsStartupInitializationRequiredProgressComplete(operationToken));
            Assert.AreEqual(1.0, progress.Value);
            Assert.IsFalse(viewModel.IsInitializationCompleted);
            Assert.IsFalse(viewModel.HasActiveLibraryProfile);

            Task<StartupLibraryInitializationGateLease> nextAcquire = gateOwner.AcquireGateAsync();
            Assert.IsTrue(nextAcquire.IsCompletedSuccessfully);
            using StartupLibraryInitializationGateLease nextLease = await nextAcquire;
        }
        finally
        {
            viewModel.SettingDialog.DetachPresentationPort(settingsPresentation);
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public void StartupProgress_ScoreOnlyCompletesThroughRuntimeRoutes()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ScoreOnly);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        owner.TrackStartupProgressScoreHydrationRequested(1);
        owner.TryCompleteStartupProgressScoreHydration(1);
        owner.TrackStartupProgressRankingRefreshRequested(1);
        owner.TryCompleteStartupProgressRankingRefresh(1);

        Assert.AreEqual(4.0, owner.Value);
        Assert.AreEqual(4.0, owner.Maximum);
        Assert.AreEqual(Resources.Statusbar_progress_complete_scores, owner.Label);
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
    public void StartupPostInitializationWarmupEligibilityWaitsForFullIdleAndAllowsLr2NoOp()
    {
        Assert.IsFalse(MainWindowViewModel.ShouldScheduleStartupPostInitializationWarmup(
            shutdownRequested: false,
            schedulerStarted: true,
            postInitializationSchedulingComplete: true,
            schedulerFullyIdle: false,
            completionTracking: true,
            initializationCompleteLogged: true,
            warmupScheduled: false));
        Assert.IsFalse(MainWindowViewModel.ShouldScheduleStartupPostInitializationWarmup(
            shutdownRequested: false,
            schedulerStarted: true,
            postInitializationSchedulingComplete: false,
            schedulerFullyIdle: true,
            completionTracking: true,
            initializationCompleteLogged: true,
            warmupScheduled: false));
        Assert.IsTrue(MainWindowViewModel.ShouldScheduleStartupPostInitializationWarmup(
            shutdownRequested: false,
            schedulerStarted: true,
            postInitializationSchedulingComplete: true,
            schedulerFullyIdle: true,
            completionTracking: true,
            initializationCompleteLogged: true,
            warmupScheduled: false));
        Assert.IsFalse(MainWindowViewModel.ShouldScheduleStartupPostInitializationWarmup(
            shutdownRequested: false,
            schedulerStarted: true,
            postInitializationSchedulingComplete: true,
            schedulerFullyIdle: true,
            completionTracking: true,
            initializationCompleteLogged: true,
            warmupScheduled: true));
        Assert.IsFalse(MainWindowViewModel.ShouldScheduleStartupPostInitializationWarmup(
            shutdownRequested: true,
            schedulerStarted: true,
            postInitializationSchedulingComplete: true,
            schedulerFullyIdle: true,
            completionTracking: true,
            initializationCompleteLogged: true,
            warmupScheduled: false));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StartupPostInitializationIdleRouteEnrollsWarmupOnceAfterPredecessors(
        bool enrollmentQueuesLr2)
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_StartupWarmup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        File.WriteAllBytes(songDbPath, []);
        using (var songDb = new LR2SongDBExtended(songDbPath))
        {
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDB.folder>();
            songDb.CreateTable<LR2SongDBExtended.maintenance>();
            songDb.CreateTable<LR2SongDBExtended.bmson_song>();
        }

        Thread writerThread = null!;
        var releaseWriterGuard = new ManualResetEventSlim(false);
        var writerGuardReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerThreadCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, new Settings());
            library.BMSFiles = [];
            IStartupLibraryApplicationPort applicationPort = owner;
            applicationPort.AttachStartupLibrary(library);

            StartupProgressWorkflowOwner progress = owner.ProgressHub.StartupProgress;
            long operationToken = progress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            progress.ApplyPresentation(false, null, null, 0.0, 1.0);
            owner.ProgressHub.BeginStartupBackgroundInitializationPresentation();
            Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive);
            SetPrivateField(owner, "startupPostInitializationCompletionTracking", true);
            SetPrivateField(owner, "startupInitializationCompleteLogged", true);
            SetPrivateField(owner, "startupCompletionContinuationToken", operationToken);
            StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
                owner,
                "startupBackgroundTaskScheduler");
            object warmupOwner = GetPrivateField<object>(owner, "startupPostInitializationWarmupOwner");
            var predecessorStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePredecessor = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            writerThread = new Thread((ThreadStart)delegate
            {
                IDisposable? writerGuard = null;
                try
                {
                    writerGuard = AcquireBmsFileWriterGuard(library);
                    writerGuardReady.TrySetResult(true);
                    releaseWriterGuard.Wait();
                }
                catch (Exception exception)
                {
                    writerGuardReady.TrySetException(exception);
                }
                finally
                {
                    try
                    {
                        writerGuard?.Dispose();
                        writerThreadCompleted.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        writerThreadCompleted.TrySetException(exception);
                    }
                }
            })
            {
                IsBackground = true,
                Name = nameof(StartupPostInitializationIdleRouteEnrollsWarmupOnceAfterPredecessors)
                    + ".WriterGuard"
            };
            writerThread.Start();
            await writerGuardReady.Task;

            Assert.IsTrue(scheduler.Queue(
                "lr2_song_db_sync_enrollment",
                "test",
                null,
                async () =>
                {
                    if (enrollmentQueuesLr2)
                    {
                        Assert.IsTrue(scheduler.Queue(
                            "lr2_song_db_sync",
                            "test",
                            null,
                            async () =>
                            {
                                predecessorStarted.TrySetResult(true);
                                await releasePredecessor.Task;
                            }));
                        return;
                    }

                    predecessorStarted.TrySetResult(true);
                    await releasePredecessor.Task;
                }));

            scheduler.Start();
            scheduler.MarkPostInitializationSchedulingComplete();
            await predecessorStarted.Task;
            progress.ApplyPresentation(false, null, null, 0.0, 1.0);
            Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive);
            Assert.IsFalse(GetPrivateField<bool>(owner, "startupPostInitializationWarmupScheduled"));

            releasePredecessor.TrySetResult(true);
            Assert.IsTrue(
                SpinWait.SpinUntil(
                    () => owner.RegularChartList.IsVirtualOrderPrewarmRunning,
                    TimeSpan.FromSeconds(5)),
                scheduler.DescribeWaitState());
            Assert.IsTrue(GetPrivateField<bool>(owner, "startupPostInitializationWarmupScheduled"));
            Assert.IsFalse(GetPrivateField<bool>(owner, "startupPostInitializationWarmupCompleted"));
            Assert.IsTrue(owner.ProgressHub.IsStartupBackgroundInitializationActive);

            releaseWriterGuard.Set();
            await writerThreadCompleted.Task;
            Assert.IsTrue(
                SpinWait.SpinUntil(
                    () => GetPrivateField<bool>(owner, "startupPostInitializationCompletionLogged"),
                    TimeSpan.FromSeconds(5)),
                scheduler.DescribeWaitState());
            Assert.IsTrue(GetPrivateField<bool>(owner, "startupPostInitializationWarmupCompleted"));
            Assert.IsFalse(owner.ProgressHub.IsStartupBackgroundInitializationActive);
            Assert.IsTrue(scheduler.IsFullyIdle, scheduler.DescribeWaitState());

            Assert.AreEqual(1L, GetPrivateField<long>(warmupOwner, "reservationAttemptSequence"));
            InvokePrivate(owner, "TryLogStartupPostInitializationComplete", []);
            InvokePrivate(owner, "TryLogStartupPostInitializationComplete", []);
            Assert.AreEqual(1L, GetPrivateField<long>(warmupOwner, "reservationAttemptSequence"));
        }
        finally
        {
            releaseWriterGuard.Set();
            if (writerThread != null && writerThread.IsAlive)
            {
                writerThread.Join(TimeSpan.FromSeconds(5));
            }
            owner.SettingDialog.Dispose();
            releaseWriterGuard.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task StartupRequiredCompletionDoesNotEnrollWarmupBeforePostWorkIsIdle()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        StartupProgressWorkflowOwner progress = owner.ProgressHub.StartupProgress;
        long operationToken = progress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
            owner,
            "startupBackgroundTaskScheduler");
        object warmupOwner = GetPrivateField<object>(owner, "startupPostInitializationWarmupOwner");
        var postWorkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePostWork = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.IsTrue(scheduler.Queue(
            "playlist_ref_apply",
            "test",
            null,
            async () =>
            {
                postWorkStarted.TrySetResult(true);
                await releasePostWork.Task;
            }));
        scheduler.Start();
        scheduler.MarkPostInitializationSchedulingComplete();
        await postWorkStarted.Task;
        CompleteUntilBackground(progress);
        Assert.IsTrue(scheduler.MarkRequiredInitializationSchedulingComplete(scheduler.CurrentGeneration));
        progress.TryCompleteStartupBackgroundTasksPhaseIfIdle(operationToken);

        InvokePrivate(owner, "TryLogStartupInitializationComplete", [operationToken]);

        Assert.IsTrue(GetPrivateField<bool>(owner, "startupInitializationCompleteLogged"));
        Assert.IsFalse(GetPrivateField<bool>(owner, "startupPostInitializationWarmupScheduled"));
        Assert.AreEqual(0L, GetPrivateField<long>(warmupOwner, "reservationAttemptSequence"));

        releasePostWork.TrySetResult(true);
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () => GetPrivateField<bool>(owner, "startupPostInitializationCompletionLogged"),
                TimeSpan.FromSeconds(5)),
            scheduler.DescribeWaitState());
        Assert.AreEqual(1L, GetPrivateField<long>(warmupOwner, "reservationAttemptSequence"));
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
            () => true,
            (_, _) => true,
            () => true,
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
        Assert.AreEqual(Resources.Statusbar_progress_complete_reload, owner.Label);
    }

    [TestMethod]
    public void StartupProgress_BackgroundTasksCompleteAfterSkippedPhases()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        SkipAllOptionalStartupPhases(owner);
        Mark(owner, StartupProgressPhase.StartupBackgroundTasksDone);

        Assert.AreEqual(13.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_complete, owner.Label);
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

        CompleteUntilBackground(owner);
        Skip(owner, StartupProgressPhase.InstallableMaintenanceDeferredDone);
        Mark(owner, StartupProgressPhase.StartupBackgroundTasksDone);

        await delayEntered.Task;
        Assert.IsTrue(owner.IsOperationActive);
        releaseDelay.TrySetResult(true);
        await inactivePublished.Task;

        Assert.IsFalse(owner.IsOperationActive);
    }

    [TestMethod]
    public void StartupProgress_SkipKeepsMaximumAndRequestAfterSkipKeepsCompletion()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        Skip(owner, StartupProgressPhase.ChartDigestBackfillDone);
        double valueAfterSkip = owner.Value;

        owner.TrackStartupProgressChartDigestBackfillRequested(1);
        owner.TryCompleteStartupProgressChartDigestBackfill(1);

        Assert.AreEqual(14.0, owner.Maximum);
        Assert.AreEqual(valueAfterSkip, owner.Value);
    }

    [TestMethod]
    public void StartupProgress_OperableWithBackgroundWorkUsesOperableLabel()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, owner.Label);
        Assert.AreEqual(Resources.Statusbar_progress_phase_playlist_load, owner.SubLabel);
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
    public void StartupProgress_InstallableMaintenanceRequiresRequest()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        owner.TryCompleteStartupProgressInstallableMaintenance(1);
        Assert.AreEqual(1.0, owner.Value);

        owner.TrackStartupProgressInstallableMaintenanceRequested(1);
        owner.TryCompleteStartupProgressInstallableMaintenance(1);

        Assert.AreEqual(2.0, owner.Value);
    }

    [TestMethod]
    public void StartupProgress_InstallableMaintenanceUsesDedicatedSubLabel()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        CompleteUntilBackground(owner);
        owner.TrackStartupProgressInstallableMaintenanceRequested(1);

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, owner.Label);
        Assert.AreEqual(Resources.Statusbar_progress_phase_installable_maintenance, owner.SubLabel);
    }

    [TestMethod]
    public void Lr2SongDbSyncWarningStatus_IsVisibleWhenStartupProgressFailed()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
        Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);

        hub.StartupProgress.FailStartupProgressOperation("startup failed");

        Assert.IsTrue(hub.IsLr2SongDbSyncStatusActive);
        Assert.AreEqual(status.StatusText, hub.Lr2SongDbSyncStatusLabel);
        Assert.AreEqual(status.ProgressText, hub.Lr2SongDbSyncStatusSubLabel);
        Assert.AreEqual(status.ProgressValue, hub.Lr2SongDbSyncStatusProgressValue);
        Assert.AreEqual(status.ProgressMaximum, hub.Lr2SongDbSyncStatusProgressMaximum);
    }

    [TestMethod]
    public void StartupProgress_LibrarySubLabelFollowsLibraryStage()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        Assert.AreEqual(Resources.Statusbar_progress_phase_library_db_load, owner.SubLabel);

        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.UpdateStartupProgressLibraryInitializationStatus(
            BMSLibrary.LibraryInitializationProgressStage.FileEnumeration,
            "Everything",
            0,
            0,
            string.Empty);
        Assert.AreEqual(Resources.Statusbar_progress_phase_file_enumeration + " (Everything)", owner.SubLabel);

        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.UpdateStartupProgressLibraryInitializationStatus(
            BMSLibrary.LibraryInitializationProgressStage.FileDiff,
            string.Empty,
            10,
            3,
            "C:\\BMS\\added.bms");
        Assert.AreEqual("[3/10] " + Resources.Statusbar_progress_phase_file_diff + " added.bms", owner.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_ChartInfoBackfillUsesCountSubLabel()
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
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
        owner.TrackStartupProgressChartInfoBackfillRequested(1);
        owner.UpdateStartupProgressChartInfoBackfillStatus(209999, 6695, "C:\\BMS\\metadata.bms");

        Assert.AreEqual("[6695/209999] " + Resources.Statusbar_progress_phase_chart_info + " metadata.bms", owner.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_ChartInfoHydrationHidesFraction()
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
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
        owner.TrackStartupProgressChartInfoHydrationRequested(1);
        owner.UpdateStartupProgressChartInfoHydrationStatus(209999, 1200);

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, owner.Label);
        Assert.AreEqual(Resources.Statusbar_progress_phase_chart_info_load, owner.SubLabel);
        Assert.IsFalse(owner.SubLabel.Contains("["));
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
        Assert.AreEqual(Resources.Statusbar_progress_complete_reload, owner.Label);
    }

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
            var settings = new Settings();
            TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, settings);
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings);
            owner = MainWindowViewModelTestFactory.Create(settings);
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
                CurrentTableName = "repair target"
            });
            TestUiDispatcherHost.Drain();

            Assert.IsTrue(owner.ProgressHub.IsPlaylistSyncProgressActive);
            Assert.AreEqual(3.0, owner.ProgressHub.PlaylistSyncProgressMaximum);
            Assert.AreEqual(1.0, owner.ProgressHub.PlaylistSyncProgressValue);
            Assert.AreEqual("repair target", owner.ProgressHub.PlaylistSyncProgressSubLabel);
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

    [TestMethod]
    public void StartupReadyUiMask_RequiresInstallTreeOnly()
    {
        Assert.IsFalse(StartupPresentationPolicy.IsReadyUiMaskSatisfied(false, true, true));
        Assert.IsTrue(StartupPresentationPolicy.IsReadyUiMaskSatisfied(true, false, false));
    }

    [TestMethod]
    public void StartupPresentationDeferPolicyKeepsBasicCatalogFlushRules()
    {
        Assert.IsFalse(StartupPresentationPolicy.IsPresentationDeferred(
            MainViewUpdateMode.FolderFilterSelected, true, true, false, false, false));
        Assert.IsFalse(StartupPresentationPolicy.IsPresentationDeferred(
            MainViewUpdateMode.FolderFilterSelected, true, false, true, false, false));
        Assert.IsTrue(StartupPresentationPolicy.IsPresentationDeferred(
            MainViewUpdateMode.FolderFilterSelected, true, false, false, false, true));
        Assert.IsTrue(StartupPresentationPolicy.IsPresentationDeferred(
            MainViewUpdateMode.FileMissingFilterSelected, true, true, false, false, false));
    }

    [TestMethod]
    public void LibraryFolderContinuation_ReusesOnlyCurrentStartupAdmission()
    {
        Assert.IsFalse(StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
            deferredContinuation: true,
            continuationOperationToken: 42,
            activeOperationToken: 42,
            startupOperationActive: true));
        Assert.IsTrue(StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
            deferredContinuation: true,
            continuationOperationToken: 0,
            activeOperationToken: 42,
            startupOperationActive: true));
        Assert.IsTrue(StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
            deferredContinuation: true,
            continuationOperationToken: 41,
            activeOperationToken: 42,
            startupOperationActive: true));
        Assert.IsTrue(StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
            deferredContinuation: false,
            continuationOperationToken: 42,
            activeOperationToken: 42,
            startupOperationActive: true));
    }

    [TestMethod]
    public async Task StartupReadiness_OperableAndRequiredSchedulerStartBeforeBlockedFolderReaderCompletes()
    {
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_StartupReadiness_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);

        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }

            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            StartupProgressWorkflowOwner startupProgress = owner.ProgressHub.StartupProgress;
            long operationToken = startupProgress.StartStartupProgressOperation(
                StartupProgressOperationKind.Startup);
            SetPrivateField(owner, "startupReadyInstallStopwatch", Stopwatch.StartNew());
            SetPrivateField(owner, "startupReadyOperableStopwatch", Stopwatch.StartNew());

            Type refreshChannelType = typeof(MainWindowViewModel).GetNestedType(
                "UiRefreshChannel",
                BindingFlags.NonPublic)!;
            object readyMask = Enum.ToObject(refreshChannelType, 1 | 4 | 8);
            object folderMask = Enum.ToObject(refreshChannelType, 2);
            InvokePrivate(
                owner,
                "TryLogStartupReadyUi",
                new[] { readyMask, (object)operationToken });

            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = []
            };
            library.SearchTargets = [tempRootPath];
            var writerGuardReady = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var writerThreadCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseWriterGuard = new ManualResetEventSlim();
            Exception? writerThreadFailure = null;
            // ReaderWriterLockSlim guards are thread-affine, so the dedicated
            // holder owns acquisition, the blocked interval, and disposal.
            var writerThread = new Thread((ThreadStart)delegate
            {
                IDisposable? writerGuard = null;
                try
                {
                    writerGuard = AcquireBmsFileWriterGuard(library);
                    writerGuardReady.TrySetResult(true);
                    releaseWriterGuard.Wait();
                }
                catch (Exception exception)
                {
                    writerThreadFailure = exception;
                    writerGuardReady.TrySetException(exception);
                }
                finally
                {
                    try
                    {
                        writerGuard?.Dispose();
                    }
                    catch (Exception exception)
                    {
                        writerThreadFailure ??= exception;
                        writerGuardReady.TrySetException(exception);
                    }

                    if (writerThreadFailure is null)
                    {
                        writerThreadCompleted.TrySetResult(true);
                    }
                    else
                    {
                        writerThreadCompleted.TrySetException(writerThreadFailure);
                    }
                }
            })
            {
                IsBackground = true,
                Name = nameof(StartupReadiness_OperableAndRequiredSchedulerStartBeforeBlockedFolderReaderCompletes)
                    + ".WriterGuard"
            };
            try
            {
                writerThread.Start();
                await writerGuardReady.Task;
                int refreshCompletions = 0;
                owner.LibraryFolderTree.DeferredRefreshCompleted += (_, _) => refreshCompletions++;
                owner.LibraryFolderTree.AttachLibrary(library);
                Task<int> folderListRead = Task.Run(
                    () => owner.LibraryFolderTree.BMSParentFolderList.Count);
                Assert.IsTrue(
                    folderListRead.Wait(TimeSpan.FromSeconds(1)),
                    "Folder-tree presentation reads must not wait for the model reader guard.");
                StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
                    owner,
                    "startupBackgroundTaskScheduler");
                var requiredTaskStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.IsTrue(scheduler.Queue(
                    "startup_readiness_required_test",
                    "test",
                    null,
                    () =>
                    {
                        requiredTaskStarted.TrySetResult(true);
                        return Task.CompletedTask;
                    }));

                InvokePrivate(
                    owner,
                    "FlushPendingUiRefresh",
                    new[]
                    {
                        folderMask,
                        (object)operationToken,
                        (object)false,
                        (object)true
                    });

                Assert.IsTrue(scheduler.IsStarted);
                await requiredTaskStarted.Task;
                Assert.AreEqual(0, refreshCompletions);
                Task deferredRefreshIdle = owner.LibraryFolderTree.WaitForDeferredRefreshIdleAsync();
                Assert.IsFalse(deferredRefreshIdle.IsCompleted);

                releaseWriterGuard.Set();
                await writerThreadCompleted.Task;
                await deferredRefreshIdle;
            }
            finally
            {
                releaseWriterGuard.Set();
                Assert.IsTrue(
                    writerThread.Join(TimeSpan.FromSeconds(10)),
                    "The dedicated BMS-file writer-lock thread did not terminate in time.");
            }
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static StartupProgressWorkflowOwner Start(StartupProgressOperationKind operationKind)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var owner = TestStartupProgressOwnerFactory.Create();
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
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
    }

    private static void CompleteUntilBackground(StartupProgressWorkflowOwner owner)
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
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
    }

    private sealed class RecordingStartupLibraryInitializationFailurePresenter
        : IStartupLibraryInitializationFailurePresenter
    {
        internal List<StartupLibraryInitializationFailurePresentation> Presentations { get; } = new();

        public void Present(StartupLibraryInitializationFailurePresentation presentation)
            => Presentations.Add(presentation);
    }

    private sealed class ThrowingStartupLibraryInitializationFailurePresenter
        : IStartupLibraryInitializationFailurePresenter
    {
        private readonly Exception failure;

        internal ThrowingStartupLibraryInitializationFailurePresenter(Exception failure)
        {
            this.failure = failure;
        }

        internal List<StartupLibraryInitializationFailurePresentation> Presentations { get; } = new();

        public void Present(StartupLibraryInitializationFailurePresentation presentation)
        {
            Presentations.Add(presentation);
            throw failure;
        }
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
}
