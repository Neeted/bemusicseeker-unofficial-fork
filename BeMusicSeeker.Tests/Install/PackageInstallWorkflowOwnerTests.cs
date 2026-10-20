using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>再生停止の実Task終端まで導入の書込みと受付解放を止め、停止失敗なら元例外だけを通知して書込みを行わない。</summary>
    // falseは停止正常終端後の書込み、trueは停止失敗による導入中止。
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticInstall_WaitsForPlaybackStopAndDoesNotWriteOnStopFailure(bool failStop)
    {

        string root = Path.Combine(Path.GetTempPath(), "bms-install-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "song.db");
        string written = Path.Combine(root, "installed.txt");
        File.WriteAllBytes(database, []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new ChartFileOperationSynchronizer();
        var expected = new IOException("再生停止に失敗しました。");
        Exception? reported = null;
        var owner = new PackageInstallWorkflowOwner(
            new FileDbReportRecordingDialogs(), admission, new ChartMutationActivityOwner(),
            new DelegatePackageInstallMutationPort((_, _, _, _) =>
            {
                File.WriteAllText(written, "installed");
                return new PackageInstallCommandResult([], null);
            }), new AwaitablePlaybackStop(entered, stop.Task), action => { action(); return true; });
        owner.FailurePublished += failure => reported = failure.Exception;
        owner.AttachLibrary(new TestBmsLibrary(database, null, null, string.Empty));
        try
        {
            Assert.IsTrue(owner.Enqueue([Path.Combine(root, "source.zip")]));
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.entered");
            Assert.IsFalse(File.Exists(written));
            Assert.IsTrue(admission.IsActive);
            Assert.IsFalse(owner.WaitForIdleAsync().IsCompleted);
            if (failStop) { stop.SetException(expected); }
            else { stop.SetResult(); }
            await owner.WaitForIdleAsync();
            Assert.AreEqual(!failStop, File.Exists(written));
            Assert.AreSame(failStop ? expected : null, reported);
            Assert.IsFalse(admission.IsActive);
        }
        finally
        {
            stop.TrySetResult();
            await owner.WaitForIdleAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class AwaitablePlaybackStop(TaskCompletionSource entered, Task completion) : IChartMutationPlaybackPort
    {
        public Task StopPlaybackForMutationAsync()
        {
            entered.TrySetResult();
            return completion;
        }
    }

    /// <summary>本番compositionが接続した導入workflowはstatusをUIへ投稿し、enqueueや処理終端を同期UI呼出しで待たず、稼働通知後に非稼働へ終える。</summary>
    /// <remarks>実compositionからVMとworkflowを接続して、注入schedulerの投稿と実status受信を区別する。共有STAで構築するためこのケースのみ非並列とする。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task ProductionPackageInstallDispatcher_QueuesStatusWithoutSynchronousUiWait()
    {

        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        string installDirectory = Path.Combine(root, "install-source");
        Directory.CreateDirectory(installDirectory);
        File.WriteAllBytes(songDbPath, []);
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            });
        var scheduler = new QueuedPackageInstallUiScheduler();
        Task? enqueueTask = null;
        PackageInstallWorkflowOwner? workflow = null;
        MainWindowViewModel? viewModel = null;
        BMSLibrary? library = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            try
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
                    {
                    }
                    var composition = new ApplicationComposition(
                        settingsEditSession: new NoOpSettingsEditSession(settings),
                        uiScheduler: scheduler,
                        applicationLifetime: TestApplicationContext.CreateLifetime(),
                        cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                        fileDbMutationDialogService: new FileDbReportRecordingDialogs());
                    viewModel = composition.CreateMainWindowViewModel();
                    scheduler.ReleaseAll();
                    library = composition.CreateBmsLibrary(new LibraryProfile(false, songDbPath, [], () => null!, null,
                        false, false, false, false, "package-dispatch-test"));
                    viewModel.PackageInstallWorkflow.AttachLibrary(library);
                    scheduler.ReleaseAll();
                });

                Assert.IsNotNull(viewModel);
                Assert.IsNotNull(library);
                workflow = viewModel.PackageInstallWorkflow;
                var observations = new List<string>();
                var failures = new List<Exception>();
                workflow.FailurePublished += failure => failures.Add(failure.Exception);
                workflow.StatusChanged += snapshot =>
                    observations.Add(snapshot.IsActive ? "active" : "inactive");
                int invokeCountBefore = scheduler.InvokeCount;
                int invokeAsyncCountBefore = scheduler.InvokeAsyncCount;

                enqueueTask = Task.Factory.StartNew(
                    () => viewModel.PackageInstallWorkflow.Enqueue([installDirectory]),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                // Package-install と再生停止は同じ UI scheduler を共有する。再生側の DataBind 通知を
                // queue 上の位置で package-install 通知と誤認せず、StatusChanged を発行した操作を検証する。
                await enqueueTask;
                Assert.AreEqual(0, observations.Count);
                Assert.AreEqual(invokeCountBefore, scheduler.InvokeCount);
                Assert.AreEqual(invokeAsyncCountBefore, scheduler.InvokeAsyncCount);

                await ReleaseUntilStatusObservationAsync(scheduler, observations, expectedStatus: "active");
                Assert.AreEqual("active", observations[0]);

                Task idle = viewModel.PackageInstallWorkflow.WaitForIdleAsync();
                await ReleaseUntilStatusObservationAsync(scheduler, observations, expectedStatus: "inactive");
                await idle;

                Assert.IsTrue(observations.Count >= 2);
                Assert.IsTrue(
                    observations.Take(observations.Count - 1).All(status => status == "active"),
                    "Only active progress updates may precede the terminal inactive status.");
                Assert.AreEqual("inactive", observations[^1]);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.AreEqual(invokeCountBefore, scheduler.InvokeCount);
                Assert.AreEqual(invokeAsyncCountBefore, scheduler.InvokeAsyncCount);
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
        }
        finally
        {
            bool backgroundDrained = true;
            try
            {
                scheduler.ReleaseAll();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
                backgroundDrained = false;
            }
            if (enqueueTask != null)
            {
                try
                {
                    await enqueueTask;
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            // enqueue終端直前にworkerが終端statusを投稿できるため、idleの観測前にも投稿済み通知を実行する。
            try
            {
                scheduler.ReleaseAll();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
                backgroundDrained = false;
            }
            if (workflow != null)
            {
                try
                {
                    await workflow.WaitForIdleAsync();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            // idleへの遷移中に投稿された終端callbackも、fixture回収前に実行する。
            try
            {
                scheduler.ReleaseAll();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
                backgroundDrained = false;
            }
            if (workflow != null)
            {
                try
                {
                    await workflow.WaitForIdleAsync();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            if (backgroundDrained)
            {
                try
                {
                    TestUiDispatcherHost.Invoke(() => viewModel?.SettingDialog.Dispose());
                    library?.RequestShutdown("package-dispatch-fixture");
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
        }

        if (bodyFailure != null)
        {
            if (cleanupFailure != null)
            {
                throw new AggregateException(
                    "The package dispatch assertion failed and cleanup also failed.",
                    bodyFailure.SourceException,
                    cleanupFailure);
            }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    /// <summary>二十件分の進捗を保留中に最新一通知へ合流し、完了通知と非稼働通知より先に最新の全件完了数を公開する。</summary>
    [TestMethod]
    public async Task ActiveProgressBurst_QueuesOneLatestStatusBeforeCompletionAndInactive()
    {

        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var notifications = new Queue<Action>();
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var published = new List<string>();
            PackageInstallWorkflowOwner owner = CreateOwner(
                (current, paths, token, progressWriter) =>
                {
                    for (int index = 1; index <= 20; index++)
                    {
                        progressWriter.TryWrite(PackageInstallProgressUpdate.ArchiveExtractStarted("archive-" + index + ".zip", index, 20));
                        progressWriter.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
                    }
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                },
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }
                    return true;
                });
            owner.StatusChanged += snapshot => published.Add(
                snapshot.IsActive
                    ? "active:" + snapshot.CompletedPathCount
                    : "inactive");
            owner.CompletionPublished += _ => published.Add("completed");
            owner.AttachLibrary(library);
            DrainNotifications(notifications);
            published.Clear();

            owner.Enqueue(Enumerable.Range(1, 20).Select(index => "batch-" + index + ".zip"));

            await owner.WaitForIdleAsync();
            lock (notifications)
            {
                Assert.AreEqual(
                    3,
                    notifications.Count,
                    "One coalesced active status, completion, and inactive terminal status are expected.");
            }
            DrainNotifications(notifications);

            Assert.AreEqual(
                "active:20|completed|inactive",
                string.Join("|", published));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>受付保持中の大量進捗を最新一投稿へ制限し、再入受付を拒否したまま実終端後に完了・非稼働を届け、seal後の進捗を捨てる。</summary>
    [TestMethod]
    public async Task ProgressWriter_BoundsActiveDispatchAndDropsLateProgressAfterSeal()
    {

        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var notifications = new Queue<Action>();
        using var notificationSignal = new SemaphoreSlim(0);
        using var terminalPublished = new ManualResetEventSlim(false);
        int maximumQueuedNotifications = 0;
        var statusValues = new List<string>();
        var chartFileOperations = new ChartFileOperationSynchronizer();
        using var mutationPort = new BoundedProgressPackageInstallMutationPort(64);
        PackageInstallWorkflowOwner? owner = null;
        Exception? publishedFailure = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                new ChartMutationActivityOwner(),
                mutationPort,
                new NoOpChartMutationPlaybackPort(),
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                        maximumQueuedNotifications = Math.Max(
                            maximumQueuedNotifications,
                            notifications.Count);
                    }
                    notificationSignal.Release();
                    return true;
                });
            owner.FailurePublished += failure => publishedFailure = failure.Exception;
            owner.StatusChanged += snapshot =>
            {
                if (!snapshot.IsActive)
                {
                    statusValues.Add("inactive");
                    terminalPublished.Set();
                    return;
                }

                statusValues.Add("active:" + snapshot.CompletedPathCount);
                if (mutationPort.MutationIsBlocked)
                {
                    bool acquired = chartFileOperations.TryEnter(out IDisposable reentrantLease);
                    reentrantLease?.Dispose();
                    Assert.IsFalse(acquired, "A progress subscriber must fail fast while the batch lease is held.");
                }
            };
            owner.CompletionPublished += _ =>
            {
                statusValues.Add("completed");
                mutationPort.EmitLateProgress();
            };
            owner.AttachLibrary(library);
            DrainNotifications(notifications);
            terminalPublished.Reset();
            statusValues.Clear();

            owner.Enqueue(Enumerable.Range(1, 64).Select(index => "bounded-progress-" + index + ".zip"));
            Task batchIdle = owner.WaitForIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationPort.Started.Task, batchIdle, "install-progress.started");
            Assert.IsTrue(mutationPort.MutationIsBlocked);
            lock (notifications)
            {
                Assert.IsTrue(
                    notifications.Count <= 1,
                    "Latest-wins progress must leave at most one active dispatch pending.");
            }

            DrainNotifications(notifications);
            Assert.IsTrue(
                statusValues.Any(value => value.StartsWith("active:", StringComparison.Ordinal)),
                "An intermediate active status must be pumpable while the lease is held.");
            Assert.IsTrue(
                statusValues.Any(value => value == "active:64"),
                "The explicitly pumped status must contain the latest immutable progress fact.");
            while (notificationSignal.Wait(0))
            {
            }

            mutationPort.Release();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationPort.Returned.Task, batchIdle, "install-progress.returned");
            while (!terminalPublished.IsSet)
            {
                DrainNotifications(notifications);
                if (terminalPublished.IsSet)
                {
                    break;
                }
                using var notificationCancellation = new CancellationTokenSource();
                Task notification = notificationSignal.WaitAsync(notificationCancellation.Token);
                try
                {
                    Task completed = await Task.WhenAny(notification, batchIdle);
                    await completed;
                    if (completed == batchIdle)
                    {
                        // 実終端までに投稿された通知を消費してから、必要な表示到達を判定する。
                        DrainNotifications(notifications);
                        Assert.IsTrue(terminalPublished.IsSet, "実処理の終端までに必要な状態通知が投稿されませんでした。");
                    }
                }
                finally
                {
                    notificationCancellation.Cancel();
                    try { await notification; }
                    catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
                }
            }
            Assert.IsTrue(terminalPublished.IsSet, "The terminal inactive notification was not published.");
            DrainNotifications(notifications);
            await owner!.WaitForIdleAsync();
            Assert.IsNull(publishedFailure, "The bounded progress mutation must complete without publishing a failure.");
            CollectionAssert.AreEqual(
                new[] { "active:64", "completed", "inactive" },
                statusValues.ToArray());
            Assert.IsTrue(
                maximumQueuedNotifications <= 3,
                "Only the active, completion, and terminal notifications may be scheduled for the batch.");
        }
        catch (Exception exception)
        {
            bodyFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            bool backgroundDrained = true;
            mutationPort.Release();
            if (owner != null)
            {
                try
                {
                    Task idle = owner.WaitForIdleAsync();
                    await idle;
                    DrainNotifications(notifications);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            else
            {
                try
                {
                    DrainNotifications(notifications);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            if (backgroundDrained)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
        }

        if (bodyFailure != null)
        {
            if (cleanupFailure != null)
            {
                throw new AggregateException(
                    "The package progress assertion failed and cleanup also failed.",
                    bodyFailure.SourceException,
                    cleanupFailure);
            }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    /// <summary>先行batchのseal後に届いた進捗を次の明示要求へ混ぜず、新しいbatchは完了数ゼロから始めて二件とも終端する。</summary>
    [TestMethod]
    public async Task ProgressWriter_SealsBeforeLateSourceCanReachNextBatch()
    {

        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var notifications = new Queue<Action>();
        using var notificationSignal = new SemaphoreSlim(0);
        using var terminalPublished = new ManualResetEventSlim(false);
        var statusValues = new List<string>();
        var chartFileOperations = new ChartFileOperationSynchronizer();
        using var mutationPort = new TwoBatchProgressPackageInstallMutationPort(64);
        PackageInstallWorkflowOwner? owner = null;
        Exception? publishedFailure = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                new ChartMutationActivityOwner(),
                mutationPort,
                new NoOpChartMutationPlaybackPort(),
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }
                    notificationSignal.Release();
                    return true;
                });
            owner.FailurePublished += failure => publishedFailure = failure.Exception;
            owner.StatusChanged += snapshot =>
            {
                statusValues.Add(snapshot.IsActive
                    ? "active:" + snapshot.CompletedPathCount
                    : "inactive");
                if (!snapshot.IsActive)
                {
                    terminalPublished.Set();
                }
            };
            int completionCount = 0;
            owner.CompletionPublished += _ =>
            {
                completionCount++;
                if (completionCount == 1)
                {
                    mutationPort.EmitLateProgressFromFirstBatch();
                }
            };
            owner.AttachLibrary(library);
            DrainNotifications(notifications);
            while (notificationSignal.Wait(0))
            {
            }
            terminalPublished.Reset();
            statusValues.Clear();

            owner.Enqueue(Enumerable.Range(1, 64).Select(index => "seal-first-" + index + ".zip"));
            Task firstIdle = owner.WaitForIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationPort.FirstStarted.Task, firstIdle, "install-progress.first-started");
            DrainNotifications(notifications);
            while (notificationSignal.Wait(0))
            {
            }
            statusValues.Clear();

            Assert.IsFalse(owner.Enqueue(Enumerable.Range(1, 64).Select(index => "rejected-" + index + ".zip")));
            DrainNotifications(notifications);
            while (notificationSignal.Wait(0))
            {
            }
            statusValues.Clear();
            mutationPort.ReleaseFirst();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationPort.FirstReturned.Task, firstIdle, "install-progress.first-returned");
            await owner.WaitForIdleAsync();
            DrainNotifications(notifications);
            terminalPublished.Reset();
            statusValues.Clear();
            Assert.IsTrue(owner.Enqueue(Enumerable.Range(1, 64).Select(index => "seal-second-" + index + ".zip")));
            Task batchIdle = owner.WaitForIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationPort.SecondStarted.Task, batchIdle, "install-progress.second-started");

            while (!statusValues.Any(value => value.StartsWith("active:", StringComparison.Ordinal)))
            {
                DrainNotifications(notifications);
                if (statusValues.Any(value => value.StartsWith("active:", StringComparison.Ordinal)))
                {
                    break;
                }
                using var notificationCancellation = new CancellationTokenSource();
                Task notification = notificationSignal.WaitAsync(notificationCancellation.Token);
                try
                {
                    Task completed = await Task.WhenAny(notification, batchIdle);
                    await completed;
                    if (completed == batchIdle)
                    {
                        // 実終端までに投稿された通知を消費してから、必要な表示到達を判定する。
                        DrainNotifications(notifications);
                        Assert.IsTrue(statusValues.Any(value => value.StartsWith("active:", StringComparison.Ordinal)), "実処理の終端までに必要な状態通知が投稿されませんでした。");
                    }
                }
                finally
                {
                    notificationCancellation.Cancel();
                    try { await notification; }
                    catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
                }
            }
            DrainNotifications(notifications);
            string[] secondBatchActiveValues = statusValues
                .Where(value => value.StartsWith("active:", StringComparison.Ordinal))
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { "active:0" },
                secondBatchActiveValues,
                "A sealed first-batch writer must not publish stale progress into the next batch.");

            mutationPort.ReleaseSecond();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationPort.SecondReturned.Task, batchIdle, "install-progress.second-returned");
            while (!terminalPublished.IsSet)
            {
                DrainNotifications(notifications);
                if (terminalPublished.IsSet)
                {
                    break;
                }
                using var notificationCancellation = new CancellationTokenSource();
                Task notification = notificationSignal.WaitAsync(notificationCancellation.Token);
                try
                {
                    Task completed = await Task.WhenAny(notification, batchIdle);
                    await completed;
                    if (completed == batchIdle)
                    {
                        // 実終端までに投稿された通知を消費してから、必要な表示到達を判定する。
                        DrainNotifications(notifications);
                        Assert.IsTrue(terminalPublished.IsSet, "実処理の終端までに必要な状態通知が投稿されませんでした。");
                    }
                }
                finally
                {
                    notificationCancellation.Cancel();
                    try { await notification; }
                    catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
                }
            }
            Assert.IsTrue(terminalPublished.IsSet, "The terminal inactive notification was not published.");
            DrainNotifications(notifications);
            await owner!.WaitForIdleAsync();
            Assert.IsNull(publishedFailure, "The package progress mutations must complete without publishing a failure.");
            Assert.AreEqual(2, completionCount);
        }
        catch (Exception exception)
        {
            bodyFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            bool backgroundDrained = true;
            mutationPort.ReleaseFirst();
            mutationPort.ReleaseSecond();
            if (owner != null)
            {
                try
                {
                    Task idle = owner.WaitForIdleAsync();
                    await idle;
                    DrainNotifications(notifications);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            else
            {
                try
                {
                    DrainNotifications(notifications);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            if (backgroundDrained)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
        }

        if (bodyFailure != null)
        {
            if (cleanupFailure != null)
            {
                throw new AggregateException(
                    "The package progress assertion failed and cleanup also failed.",
                    bodyFailure.SourceException,
                    cleanupFailure);
            }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    /// <summary>表示上はidleでも旧世代の終端通知が実行中なら、ライブラリ再接続後のidle待機で旧processorを早期に破棄しない。</summary>
    [TestMethod]
    public async Task RetiredQueuePruningWaitsForTerminalReceiptAfterStatusDispatchStops()
    {

        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstDb = Path.Combine(firstDirectory, "song.db");
        string secondDb = Path.Combine(secondDirectory, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseTerminal = new ManualResetEventSlim(false);
        PackageInstallWorkflowOwner? owner = null;
        Task? idle = null;
        try
        {
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            owner = CreateOwner(
                (library, paths, token, progressWriter) =>
                {
                    if (ReferenceEquals(library, first))
                    {
                        firstStarted.TrySetResult(true);
                    }
                    return new PackageInstallCommandResult([], null);
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.StatusChanged += snapshot =>
            {
                if (!snapshot.IsActive
                    && snapshot.Sequence > 0)
                {
                    terminalEntered.TrySetResult(true);
                    releaseTerminal.Wait();
                }
            };
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            await TestUiDispatcherHost.AwaitNotificationAsync(firstStarted.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.firstStarted");

            await TestUiDispatcherHost.AwaitNotificationAsync(terminalEntered.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.terminalEntered");
            owner.AttachLibrary(second);

            Assert.IsTrue(owner.IsIdle, "The status getter should still report queue state as idle.");
            idle = owner.WaitForIdleAsync();
            Assert.IsFalse(
                idle.IsCompleted,
                "Owner idle must retain a retired processor until its terminal receipt completes.");

            releaseTerminal.Set();
            await idle!;
        }
        finally
        {
            releaseTerminal.Set();
            if (owner != null)
            {
                idle ??= owner.WaitForIdleAsync();
                await idle!;
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>ライブラリ未接続の所有入力を拒否し、導入や失敗診断を開始せず管理入力元を回収する。</summary>
    [TestMethod]
    public void TryEnqueue_BeforeLibraryAttachRejectsAndDeletesOwnedIngressRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int diagnosticReports = 0;
        int mutationCalls = 0;
        PackageInstallWorkflowOwner owner = CreateOwner(
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref mutationCalls);
                return new PackageInstallCommandResult([], null);
            },
            action =>
            {
                action();
                return true;
            },
            _ => Interlocked.Increment(ref diagnosticReports));

        bool accepted = owner.TryEnqueue(CreateOwnedRequest(root, "before-attach.zip"));

        Assert.IsFalse(accepted);
        Assert.IsTrue(owner.IsIdle);
        Assert.AreEqual(0, mutationCalls);
        Assert.AreEqual(0, diagnosticReports);
        Assert.IsFalse(Directory.Exists(root));
    }

    /// <summary>入力確保の前にBusyを拒否して元データへ触れず、実行中の追加要求も予約せず、解放後の新しい明示要求だけを実行する。</summary>
    [TestMethod]
    public async Task AcquireAndTryEnqueueDroppedPaths_RejectsBusyBeforeAcquisitionWithoutReservation()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var gate = new ChartFileOperationSynchronizer();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        int acquisitionCalls = 0;
        int mutationCalls = 0;
        var observedInputs = new List<string[]>();
        var materializer = new DroppedInstallIngressMaterializer(root, _ => false,
            () => { Interlocked.Increment(ref acquisitionCalls); return root; }, _ => { },
            getAttributes: path => { Interlocked.Increment(ref acquisitionCalls); return File.GetAttributes(path); });
        PackageInstallWorkflowOwner owner = CreateOwner((_, paths, _, _) =>
        {
            Interlocked.Increment(ref mutationCalls);
            observedInputs.Add(paths.ToArray());
            started.TrySetResult(true);
            release.Wait();
            return new PackageInstallCommandResult([], null);
        }, action => { action(); return true; },
            droppedInstallIngressMaterializer: materializer, chartFileOperations: gate);
        owner.AttachLibrary(new TestBmsLibrary(songDbPath));
        try
        {
            Assert.IsTrue(gate.TryEnter(out IDisposable lease));
            using (lease)
            {
                DroppedInstallIngressAcquisitionResult rejection = owner.AcquireAndTryEnqueueDroppedPaths([songDbPath]);
                Assert.AreEqual(DroppedInstallIngressFailureKind.QueueRejected, rejection.FailureKind);
                Assert.AreEqual(0, acquisitionCalls);
                Assert.AreEqual(0, mutationCalls);
            }
            Assert.IsTrue(owner.Enqueue(["first.zip", "folder"]));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.started");
            CollectionAssert.AreEqual(new[] { "first.zip", "folder" }, observedInputs.Single());
            Assert.AreEqual(DroppedInstallIngressFailureKind.QueueRejected,
                owner.AcquireAndTryEnqueueDroppedPaths([songDbPath]).FailureKind);
            Assert.AreEqual(0, acquisitionCalls);
            release.Set();
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, mutationCalls, "拒否した要求は現在の操作の終端後にも実行しません。");
            Assert.IsTrue(owner.Enqueue(["first.zip", "folder"]));
            await owner.WaitForIdleAsync();
            Assert.AreEqual(2, mutationCalls);
        }
        finally
        {
            release.Set();
            await owner.WaitForIdleAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>入力確保時のファイル不在を元例外で返して受付を解放し、後続の明示要求では借用元を残しながら確保した内容を導入へ渡す。</summary>
    [TestMethod]
    public async Task AcquireAndTryEnqueueDroppedPaths_MissingSourcePreservesFailureAndReleasesAdmissionForFreshRequest()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "system-temp");
        string ingressRoot = Path.Combine(root, "managed-ingress");
        Directory.CreateDirectory(sourceRoot);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        string missingSource = Path.Combine(sourceRoot, "disappeared.bms");
        File.WriteAllText(missingSource, "borrowed-chart");
        File.Delete(missingSource);
        string freshSource = Path.Combine(sourceRoot, "fresh.bms");
        File.WriteAllText(freshSource, "fresh-chart");
        var gate = new ChartFileOperationSynchronizer();
        Exception? sourceFailure = null;
        int ingressRootsCreated = 0;
        int mutationCalls = 0;
        string? installedContents = null;
        bool freshRequestAttempted = false;
        var materializer = new DroppedInstallIngressMaterializer(sourceRoot, _ => false,
            () =>
            {
                ingressRootsCreated++;
                Directory.CreateDirectory(ingressRoot);
                return ingressRoot;
            }, DeleteOwnedRoot,
            getAttributes: path =>
            {
                try { return File.GetAttributes(path); }
                catch (FileNotFoundException exception)
                {
                    sourceFailure = exception;
                    throw;
                }
            });
        PackageInstallWorkflowOwner owner = CreateOwner((_, paths, _, _) =>
        {
            mutationCalls++;
            installedContents = File.ReadAllText(paths.Single());
            return new PackageInstallCommandResult([], null);
        }, action => { action(); return true; },
            droppedInstallIngressMaterializer: materializer, chartFileOperations: gate);
        owner.AttachLibrary(new TestBmsLibrary(songDbPath));
        try
        {
            DroppedInstallIngressAcquisitionResult failure = owner.AcquireAndTryEnqueueDroppedPaths([missingSource]);
            Assert.IsFalse(failure.Succeeded);
            Assert.AreEqual(DroppedInstallIngressFailureKind.SourceUnavailable, failure.FailureKind);
            Assert.IsNotNull(sourceFailure);
            Assert.AreSame(sourceFailure, failure.Exception);
            Assert.AreEqual(0, ingressRootsCreated);
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(gate.IsActive);
            Assert.IsTrue(owner.IsIdle);
            Task failedRequestIdle = owner.WaitForIdleAsync();
            Assert.IsTrue(failedRequestIdle.IsCompleted, "同期的な入力確保の失敗後は受付と待機Taskが終端します。");
            await failedRequestIdle;

            freshRequestAttempted = true;
            DroppedInstallIngressAcquisitionResult fresh = owner.AcquireAndTryEnqueueDroppedPaths([freshSource]);
            Assert.IsTrue(fresh.Succeeded, fresh.Exception?.ToString());
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, ingressRootsCreated);
            Assert.AreEqual(1, mutationCalls);
            Assert.AreEqual("fresh-chart", installedContents);
            Assert.IsTrue(File.Exists(freshSource));
            Assert.IsFalse(gate.IsActive);
        }
        finally
        {
            // 入力確保の失敗までは同期処理だけ。workerを開始し得る次の要求を試した場合は終端を待って回収する。
            if (freshRequestAttempted)
            {
                await owner.WaitForIdleAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>一時的な借用元を管理領域へ確保してから元を消しても、導入portに同じ内容と相対構造を渡し、引渡し済み入力を回収しない。</summary>
    /// <remarks>実ファイルの確保後に借用元を削除し、導入portで実内容を読む。コピー成功だけで寿命保全の保証に代えない。</remarks>
    [TestMethod]
    public async Task AcquireAndTryEnqueueDroppedPaths_StagesTransientSourceThroughMutationPort()
    {

        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        string systemTempRoot = Path.Combine(root, "system-temp");
        string ingressRoot = Path.Combine(root, "managed-ingress");
        string source = Path.Combine(systemTempRoot, "archiver", "nested", "chart.bms");
        string songDbPath = Path.Combine(root, "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "borrowed-chart");
        File.WriteAllBytes(songDbPath, []);
        var mutationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowMutationRead = new ManualResetEventSlim(false);
        var mutationFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? installedPath = null;
        string? installedContents = null;
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var materializer = new DroppedInstallIngressMaterializer(
                systemTempRoot,
                _ => false,
                () =>
                {
                    Directory.CreateDirectory(ingressRoot);
                    return ingressRoot;
                },
                DeleteOwnedRoot);
            owner = CreateOwner(
                (_, paths, _, _) =>
                {
                    installedPath = paths.Single();
                    mutationEntered.TrySetResult(true);
                    allowMutationRead.Wait();
                    installedContents = File.ReadAllText(installedPath);
                    mutationFinished.TrySetResult(true);
                    return new PackageInstallCommandResult([], null);
                },
                action =>
                {
                    action();
                    return true;
                },
                droppedInstallIngressMaterializer: materializer);
            owner.AttachLibrary(library);

            DroppedInstallIngressAcquisitionResult result =
                owner.AcquireAndTryEnqueueDroppedPaths([source]);

            Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationEntered.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.mutationEntered");
            File.Delete(source);
            allowMutationRead.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationFinished.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.mutationFinished");
            await owner.WaitForIdleAsync();
            Assert.AreEqual(
                Path.Combine(ingressRoot, "archiver", "nested", "chart.bms"),
                installedPath);
            Assert.AreEqual("borrowed-chart", installedContents);
            Assert.IsFalse(File.Exists(source));
            Assert.IsTrue(
                File.Exists(installedPath),
                "Installer handoff keeps the managed source in the pending-package lifecycle.");
        }
        finally
        {
            allowMutationRead.Set();
            if (owner != null)
            {
                await owner.WaitForIdleAsync();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>先行導入の失敗後も次の明示要求を受理し、実導入の終端前に完了を通知せず、各失敗・完了を共通受付解放後に一度届ける。</summary>
    [TestMethod]
    public async Task Enqueue_PublishesCompletionAfterLiveInstallReturnsAndContinuesAfterFailure()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var firstInstallEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstInstall = new ManualResetEventSlim(false);
        var secondInstallEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSecondInstall = new ManualResetEventSlim(false);
        var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalInactive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        object observationLock = new object();
        var chartFileOperations = new ChartFileOperationSynchronizer();
        var terminalAdmissions = new List<bool>();
        PackageInstallWorkflowOwner? owner = null;
        bool workerStarted = false;
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var calls = new List<string>();
            var failures = new List<PackageInstallFailure>();
            var eventOrder = new List<string>();
            owner = CreateOwner(
                (current, paths, token, progressWriter) =>
                {
                    string displayName = Path.GetFileName(paths.FirstOrDefault() ?? string.Empty);
                    lock (observationLock)
                    {
                        calls.Add(displayName);
                    }
                    if (displayName == "first.zip")
                    {
                        firstInstallEntered.TrySetResult(true);
                        releaseFirstInstall.Wait();
                        throw new InvalidOperationException("first failed");
                    }
                    secondInstallEntered.TrySetResult(true);
                    releaseSecondInstall.Wait();
                    secondFinished.TrySetResult(true);
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                },
                action =>
                {
                    action();
                    return true;
                },
                chartFileOperations: chartFileOperations);
            owner.AttachLibrary(library);
            owner.StatusChanged += snapshot =>
            {
                if (!snapshot.IsActive)
                {
                    lock (observationLock)
                    {
                        eventOrder.Add("inactive");
                    }
                    terminalInactive.TrySetResult(true);
                }
            };
            owner.FailurePublished += failure =>
            {
                bool admissionAvailable = chartFileOperations.TryEnter(out IDisposable admission);
                admission?.Dispose();
                lock (observationLock)
                {
                    failures.Add(failure);
                    eventOrder.Add("failure");
                    terminalAdmissions.Add(admissionAvailable);
                }
            };
            owner.CompletionPublished += _ =>
            {
                bool admissionAvailable = chartFileOperations.TryEnter(out IDisposable admission);
                admission?.Dispose();
                lock (observationLock)
                {
                    eventOrder.Add("completion");
                    terminalAdmissions.Add(admissionAvailable);
                }
                completed.TrySetResult(true);
            };
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            workerStarted = true;
            await TestUiDispatcherHost.AwaitNotificationAsync(firstInstallEntered.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.firstInstallEntered");
            Assert.IsFalse(owner.Enqueue([Path.Combine(root, "second.zip")]));
            releaseFirstInstall.Set();
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, failures.Count);
            terminalInactive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(owner.Enqueue([Path.Combine(root, "second.zip")]));

            await TestUiDispatcherHost.AwaitNotificationAsync(secondInstallEntered.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.secondInstallEntered");
            lock (observationLock)
            {
                Assert.AreEqual(1, failures.Count, "先行要求の失敗は受付解放後に通知済みです。");
            }
            bool admittedWhileRunning = chartFileOperations.TryEnter(out IDisposable duringInstall);
            duringInstall?.Dispose();
            Assert.IsFalse(admittedWhileRunning, "新しい明示要求の実行中は共通受付を保持します。");
            Assert.IsFalse(completed.Task.IsCompleted, "Completion must not be published before the live install returns.");
            Assert.IsFalse(terminalInactive.Task.IsCompleted, "The queue must remain active while the following batch is running.");
            Assert.IsFalse(owner.IsIdle, "The workflow must not become idle before the live install returns.");
            releaseSecondInstall.Set();

            await TestUiDispatcherHost.AwaitNotificationAsync(secondFinished.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.secondFinished");
            await TestUiDispatcherHost.AwaitNotificationAsync(terminalInactive.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.terminalInactive");
            await owner.WaitForIdleAsync();
            string[] callSnapshot;
            PackageInstallFailure[] failureSnapshot;
            string[] eventOrderSnapshot;
            lock (observationLock)
            {
                callSnapshot = calls.ToArray();
                failureSnapshot = failures.ToArray();
                eventOrderSnapshot = eventOrder.ToArray();
            }

            await TestUiDispatcherHost.AwaitNotificationAsync(completed.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.completed");
            Assert.AreEqual(1, failureSnapshot.Length);
            CollectionAssert.AreEqual(new[] { "first.zip", "second.zip" }, callSnapshot);
            CollectionAssert.AreEqual(new[] { "failure", "inactive", "completion", "inactive" }, eventOrderSnapshot);
            CollectionAssert.AreEqual(new[] { true, true }, terminalAdmissions,
                "失敗・完了の subscriber を呼ぶ前に共通受付を解放する。");
            Assert.IsTrue(owner.IsIdle, "The workflow must be idle after its terminal inactive status.");
        }
        finally
        {
            releaseFirstInstall.Set();
            releaseSecondInstall.Set();
            if (workerStarted && owner != null)
            {
                await owner.WaitForIdleAsync();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>処理中にライブラリを再接続すると旧世代の完了通知を抑制し、実workerの終端を回収する。</summary>
    [TestMethod]
    public async Task AttachLibrary_InvalidatesCompletionFromPreviousGeneration()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstDb = Path.Combine(firstDirectory, "song.db");
        string secondDb = Path.Combine(secondDirectory, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        using var release = new ManualResetEventSlim(false);
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateOwner(
                (library, paths, token, progressWriter) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.CompletionPublished += _ => completion.TrySetResult(true);
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.started");
            owner.AttachLibrary(second);
            release.Set();

            await owner.WaitForIdleAsync();
            Assert.IsFalse(completion.Task.IsCompleted, "A replaced library generation must not publish a receipt.");
        }
        finally
        {
            release.Set();
            if (owner != null)
            {
                await owner.WaitForIdleAsync();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Busy要求をqueueへ積まず通知もしないで拒否し、解放後の明示要求だけを一回実行して発送済み完了通知を受信する。</summary>
    [TestMethod]
    public async Task Enqueue_RejectsBusyBeforeQueueingThenRunsAfterRelease()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstDb = Path.Combine(firstDirectory, "song.db");
        string secondDb = Path.Combine(secondDirectory, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        var notifications = new Queue<Action>();
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var chartMutationActivity = new ChartMutationActivityOwner();
            int mutationCalls = 0;
            var failure = new TaskCompletionSource<PackageInstallFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<PackageInstallCompletionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                chartMutationActivity,
                new DelegatePackageInstallMutationPort(
                    (library, paths, token, progressWriter) =>
                    {
                        Interlocked.Increment(ref mutationCalls);
                        return new PackageInstallCommandResult([new ChartPackage()], null);
                    }),
                new NoOpChartMutationPlaybackPort(),
                action =>
                {
                    lock (notifications) { notifications.Enqueue(action); }
                    return true;
                });
            owner.FailurePublished += published => failure.TrySetResult(published);
            owner.CompletionPublished += published => completion.TrySetResult(published);
            owner.AttachLibrary(first);
            DrainNotifications(notifications);

            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable incumbent));
            try
            {
                Assert.IsFalse(owner.Enqueue([Path.Combine(root, "first-generation.zip")]));
                await owner.WaitForIdleAsync();
                DrainNotifications(notifications);
                Assert.IsFalse(failure.Task.IsCompleted);
                Assert.IsFalse(completion.Task.IsCompleted);
                Assert.AreEqual(0, mutationCalls);
            }
            finally
            {
                incumbent.Dispose();
            }

            Assert.IsTrue(owner.Enqueue([Path.Combine(root, "second-generation.zip")]));
            await owner.WaitForIdleAsync();
            // queue idleは通知の発送までを含む。fixtureの発送先を実行してから通知欠落を判定する。
            DrainNotifications(notifications);
            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.completion");
            Assert.IsFalse(failure.Task.IsCompleted);
            Assert.AreEqual(1, mutationCalls);
        }
        finally
        {
            if (owner != null)
            {
                await owner.WaitForIdleAsync();
                DrainNotifications(notifications);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>共通受付がBusyの所有入力を放棄して管理領域を回収し、導入portや実行済み要求としての失敗通知を呼ばない。</summary>
    [TestMethod]
    public async Task GateBusy_AbandonsOwnedIngressWithoutCallingInstaller()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var chartMutationActivity = new ChartMutationActivityOwner();
            int mutationCalls = 0;
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                chartMutationActivity,
                new DelegatePackageInstallMutationPort((_, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return new PackageInstallCommandResult([], null);
                }),
                new NoOpChartMutationPlaybackPort(),
                action =>
                {
                    action();
                    return true;
                });
            owner.AttachLibrary(library);

            var failure = new TaskCompletionSource<PackageInstallFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.FailurePublished += published => failure.TrySetResult(published);
            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable incumbent));
            try
            {
                Assert.IsFalse(owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "chart.bms")));
                await owner.WaitForIdleAsync();
                Assert.IsFalse(failure.Task.IsCompleted);
            }
            finally
            {
                incumbent.Dispose();
            }

            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>旧世代の失敗通知を保留して再接続した場合、画面への失敗を抑えつつ発送先実行時に診断を一度行う。</summary>
    [TestMethod]
    public async Task DelayedFailurePublication_ReportsDiagnosticsAfterGenerationChanges()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstDb = Path.Combine(firstDirectory, "song.db");
        string secondDb = Path.Combine(secondDirectory, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        var notifications = new Queue<Action>();
        try
        {
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            int failurePublished = 0;
            int diagnosticReports = 0;
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (library, paths, token, progressWriter) => throw new InvalidOperationException("install failed")),
                new NoOpChartMutationPlaybackPort(),
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }
                    return true;
                },
                _ => Interlocked.Increment(ref diagnosticReports));
            owner.FailurePublished += _ => Interlocked.Increment(ref failurePublished);
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "failed-generation.zip")]);

            await owner.WaitForIdleAsync();
            owner.AttachLibrary(second);
            DrainNotifications(notifications);

            Assert.AreEqual(0, failurePublished);
            Assert.AreEqual(1, diagnosticReports);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>UI投稿を拒否された導入失敗は、元の導入例外を診断へ残して消さない。</summary>
    [TestMethod]
    public async Task DispatcherRejection_ReportsOriginalInstallFailure()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (current, paths, token, progressWriter) => throw new InvalidOperationException("install failed")),
                new NoOpChartMutationPlaybackPort(),
                _ => false,
                exception =>
                {
                    lock (diagnosticReports)
                    {
                        diagnosticReports.Add(exception);
                    }
                });
            owner.AttachLibrary(library);
            owner.Enqueue([Path.Combine(root, "dispatcher-rejected.zip")]);

            await owner.WaitForIdleAsync();
            lock (diagnosticReports)
            {
                Assert.IsTrue(
                    diagnosticReports.Any(exception => exception?.Message == "install failed"),
                    "The original install exception must be reported when UI dispatch is rejected.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>UI投稿そのものが失敗しても、元の導入失敗と投稿失敗の双方を診断へ残す。</summary>
    [TestMethod]
    public async Task DispatcherException_ReportsOriginalInstallFailure()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (current, paths, token, progressWriter) => throw new InvalidOperationException("install failed")),
                new NoOpChartMutationPlaybackPort(),
                _ => throw new InvalidOperationException("dispatcher failed"),
                exception =>
                {
                    lock (diagnosticReports)
                    {
                        diagnosticReports.Add(exception);
                    }
                });
            owner.AttachLibrary(library);
            owner.Enqueue([Path.Combine(root, "dispatcher-threw.zip")]);

            await owner.WaitForIdleAsync();
            lock (diagnosticReports)
            {
                Assert.IsTrue(
                    diagnosticReports.Any(exception => exception?.Message == "install failed"),
                    "The original install exception must be reported when UI dispatch throws.");
                Assert.IsTrue(
                    diagnosticReports.Any(exception => exception?.Message == "dispatcher failed"),
                    "The dispatcher exception must remain observable alongside the install failure.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>失敗通知subscriberが例外を投げても、元の導入失敗と通知先の失敗を別々に診断へ残す。</summary>
    [TestMethod]
    public async Task FailureNotificationException_ReportsInstallAndNotificationFailures()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            PackageInstallWorkflowOwner owner = CreateOwner(
                (current, paths, token, progressWriter) => throw new InvalidOperationException("install failed"),
                action =>
                {
                    action();
                    return true;
                },
                exception =>
                {
                    lock (diagnosticReports)
                    {
                        diagnosticReports.Add(exception);
                    }
                });
            owner.FailurePublished += _ => throw new InvalidOperationException("failure notification failed");
            owner.AttachLibrary(library);
            owner.Enqueue([Path.Combine(root, "notification-failed.zip")]);

            await owner.WaitForIdleAsync();
            lock (diagnosticReports)
            {
                Assert.IsTrue(diagnosticReports.Any(exception => exception?.Message == "install failed"));
                Assert.IsTrue(diagnosticReports.Any(exception => exception?.Message == "failure notification failed"));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>反映中に取消を要求しても反映portが正常に返した確定結果を失わせず、対象一件の完了receiptを通知する。</summary>
    [TestMethod]
    public async Task CancelAfterLiveApplyReturns_PublishesCompletionReceipt()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        using var release = new ManualResetEventSlim(false);
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateOwner(
                (current, paths, token, progressWriter) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    Assert.IsTrue(token.IsCancellationRequested, "The test must cancel while live apply is in progress.");
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.CompletionPublished += receipt =>
            {
                Assert.AreEqual(1, receipt.Packages.Count);
                completion.TrySetResult(true);
            };
            owner.AttachLibrary(library);
            owner.Enqueue([Path.Combine(root, "cancelled-after-apply.zip")]);
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.started");
            owner.CancelAll();
            release.Set();

            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.completion");
            await owner.WaitForIdleAsync();
        }
        finally
        {
            release.Set();
            if (owner != null)
            {
                await owner.WaitForIdleAsync();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>確定後の最終化失敗を型付きreceiptへ保持し、登録一覧が空でも受付解放後に一度届ける。</summary>
    [TestMethod]
    public async Task DurableFinalizationFailure_PublishesTypedCompletionWithoutRegisteredPackages()
    {

        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {

            var finalizationFailure = new IOException("package finalization failed");
            var sessionReceipt = new LibraryMutationSessionReceipt(
                confirmedTargets:
                [
                    new LibraryMutationSessionTarget(
                        Path.Combine(root, "source.zip"),
                        Path.Combine(root, "installed", "chart.bms"))
                ],
                durableCommit: true,
                finalizationFailure: finalizationFailure);
            int installCalls = 0;
            var port = new DelegatePackageInstallMutationPort(
                (_, _, _, _) =>
                {
                    Interlocked.Increment(ref installCalls);
                    return new PackageInstallCommandResult([], sessionReceipt);
                });
            var completion = new TaskCompletionSource<PackageInstallCompletionReceipt>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var terminalAdmissions = new List<bool>();
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                new ChartMutationActivityOwner(),
                port,
                new NoOpChartMutationPlaybackPort(),
                action =>
                {
                    action();
                    return true;
                });
            void RecordTerminalAdmission()
            {
                bool admissionAvailable = chartFileOperations.TryEnter(out IDisposable admission);
                admission?.Dispose();
                terminalAdmissions.Add(admissionAvailable);
            }
            owner.CompletionPublished += published =>
            {
                RecordTerminalAdmission();
                completion.TrySetResult(published);
            };
            owner.AttachLibrary(new TestBmsLibrary(songDbPath, null, null, string.Empty));
            owner.Enqueue([Path.Combine(root, "source.zip")]);

            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "package-install.receipt-publication");
            PackageInstallCompletionReceipt publishedReceipt = await completion.Task;
            await owner.WaitForIdleAsync();

            CollectionAssert.AreEqual(new[] { true }, terminalAdmissions,
                "型付き session の異常結果を受付解放後に一度だけ通知する。");
            Assert.AreEqual(1, installCalls);
            Assert.IsTrue(publishedReceipt.HasDurableFinalizationFailure);
            Assert.IsTrue(publishedReceipt.HasDurableCommit);
            Assert.AreEqual(0, publishedReceipt.Packages.Count);
            Assert.AreSame(finalizationFailure, publishedReceipt.SessionReceipt.FinalizationFailure);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>ライブラリ再接続中のBusy要求を予約せず、旧処理解放後の新しい要求だけを実行して旧世代の完了receiptを公開しない。</summary>
    [TestMethod]
    public async Task AttachLibrary_BusyRequestFailsFastThenFreshRequestRunsAfterRelease()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstDb = Path.Combine(firstDirectory, "song.db");
        string secondDb = Path.Combine(secondDirectory, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        using var releaseFirst = new ManualResetEventSlim(false);
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var busyFailure = new TaskCompletionSource<PackageInstallFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<string>();
            int completions = 0;
            owner = CreateOwner(
                (library, paths, token, progressWriter) =>
                {
                    string displayName = Path.GetFileName(paths.First());
                    lock (calls)
                    {
                        calls.Add(displayName);
                    }
                    if (ReferenceEquals(library, first))
                    {
                        firstStarted.TrySetResult(true);
                        releaseFirst.Wait();
                        return new PackageInstallCommandResult([new ChartPackage()], null);
                    }
                    secondStarted.TrySetResult(true);
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.FailurePublished += failure => busyFailure.TrySetResult(failure);
            owner.CompletionPublished += _ => Interlocked.Increment(ref completions);
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "first-generation.zip")]);
            await TestUiDispatcherHost.AwaitNotificationAsync(firstStarted.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.firstStarted");

            owner.AttachLibrary(second);
            Assert.IsFalse(owner.Enqueue([Path.Combine(root, "second-generation.zip")]));
            Assert.IsFalse(busyFailure.Task.IsCompleted, "未受理の要求は batch として開始しない。");
            Assert.IsFalse(secondStarted.Task.IsCompleted, "A busy replacement request must not enter mutation.");

            releaseFirst.Set();
            await owner.WaitForIdleAsync();
            owner.Enqueue([Path.Combine(root, "second-fresh-generation.zip")]);
            await TestUiDispatcherHost.AwaitNotificationAsync(secondStarted.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.secondStarted");
            await owner.WaitForIdleAsync();
            CollectionAssert.AreEqual(new[] { "first-generation.zip", "second-fresh-generation.zip" }, calls);
            Assert.AreEqual(1, completions, "Only the fresh admitted request may publish a receipt.");
        }
        finally
        {
            releaseFirst.Set();
            if (owner != null)
            {
                await owner.WaitForIdleAsync();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>shutdown後の新しい入力を導入portへ渡さず、実処理を開始しないままidleへ終える。</summary>
    [TestMethod]
    public async Task RequestShutdown_PreventsLaterEnqueueFromStartingLiveMutation()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            PackageInstallWorkflowOwner owner = CreateOwner(
                (current, paths, token, progressWriter) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.AttachLibrary(library);
            owner.RequestShutdown();
            owner.Enqueue([Path.Combine(root, "after-shutdown.zip")]);

            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, mutationCalls, "A request submitted after shutdown must not enter live mutation.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>statusや完了subscriberの失敗でqueueを停止させず、後続の明示要求も実処理を終える。</summary>
    [TestMethod]
    public async Task NotificationFailure_DoesNotStopQueueLifecycle()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PackageInstallWorkflowOwner owner = CreateOwner(
                (current, paths, token, progressWriter) =>
                {
                    int call = Interlocked.Increment(ref mutationCalls);
                    if (call == 2)
                    {
                        secondFinished.TrySetResult(true);
                    }
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.AttachLibrary(library);
            owner.StatusChanged += _ => throw new InvalidOperationException("status publication failed");
            owner.CompletionPublished += _ => throw new InvalidOperationException("completion publication failed");
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            await owner.WaitForIdleAsync();
            owner.Enqueue([Path.Combine(root, "second.zip")]);

            await TestUiDispatcherHost.AwaitNotificationAsync(secondFinished.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.secondFinished");
            await owner.WaitForIdleAsync();
            Assert.AreEqual(2, mutationCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>shutdown後の所有入力を拒否し、移譲されない管理入力元を回収する。</summary>
    [TestMethod]
    public void TryEnqueue_AfterShutdownRejectsAndDeletesOwnedIngressRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PackageInstallWorkflowOwner owner = CreateOwner((_, _, _, _) => new PackageInstallCommandResult([], null), _ => true);
        var request = new DroppedInstallBatchRequest(
            [Path.Combine(root, "chart.bms")],
            ["chart.bms"],
            [root],
            DeleteOwnedRoot,
            null);

        owner.RequestShutdown();
        bool accepted = owner.TryEnqueue(request);

        Assert.IsFalse(accepted);
        Assert.IsFalse(Directory.Exists(root));
    }

    /// <summary>queueへの物理挿入後にshutdownが入っても入力移譲は受理済みとし、導入を実行せず入力を回収して、実終端後の受付再取得を確認する。</summary>
    [TestMethod]
    public async Task RequestShutdown_AfterPhysicalInsertionDrainsRequestWithoutPrivateLockCoordination()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        var enqueueStatusDispatchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEnqueueStatusDispatch = new ManualResetEventSlim(false);
        var chartFileOperations = new ChartFileOperationSynchronizer();
        PackageInstallWorkflowOwner? owner = null;
        Task<bool>? enqueue = null;
        Task? shutdown = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            int blockNextDispatch = 0;
            int blockedDispatchConsumed = 0;
            owner = CreateOwner(
                (_, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return new PackageInstallCommandResult([], null);
                },
                action =>
                {
                    if (Volatile.Read(ref blockNextDispatch) != 0
                        && Interlocked.CompareExchange(ref blockedDispatchConsumed, 1, 0) == 0)
                    {
                        enqueueStatusDispatchEntered.TrySetResult();
                        releaseEnqueueStatusDispatch.Wait();
                    }
                    action();
                    return true;
                },
                chartFileOperations: chartFileOperations);
            owner.AttachLibrary(library);
            Volatile.Write(ref blockNextDispatch, 1);

            enqueue = Task.Run(() =>
                owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "chart.bms")));
            await TestUiDispatcherHost.AwaitNotificationAsync(enqueueStatusDispatchEntered.Task, enqueue, "package-install.physical-insertion");

            shutdown = Task.Run(owner.RequestShutdown);
            await shutdown;
            releaseEnqueueStatusDispatch.Set();

            bool enqueueAccepted = await enqueue;
            Assert.IsTrue(enqueueAccepted, "Physical insertion preceding shutdown remains an accepted transfer.");
            await owner.WaitForIdleAsync();
            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable afterDrain));
            afterDrain.Dispose();
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));
        }
        catch (Exception exception)
        {
            bodyFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            releaseEnqueueStatusDispatch.Set();
            try { if (enqueue != null) { await enqueue; } }
            catch (Exception exception) { cleanupFailure ??= exception; }
            try { if (shutdown != null) { await shutdown; } }
            catch (Exception exception) { cleanupFailure ??= exception; }
            try { if (owner != null) { await owner.WaitForIdleAsync(); } }
            catch (Exception exception) { cleanupFailure ??= exception; }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception exception) { cleanupFailure ??= exception; }
        }
        if (bodyFailure != null)
        {
            if (cleanupFailure != null) { bodyFailure.SourceException.Data["PackageInstallCleanupFailure"] = cleanupFailure; }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null) { ExceptionDispatchInfo.Capture(cleanupFailure).Throw(); }
    }

    /// <summary>物理挿入後の取消だけで受付を解放せず、取消対象の回収・終端後に新しい明示要求を受理して過去の取消を波及させない。</summary>
    [TestMethod]
    public async Task CancelAll_AfterPhysicalInsertionCannotCancelFreshPostDrainAdmission()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        var enqueueStatusDispatchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEnqueueStatusDispatch = new ManualResetEventSlim(false);
        var freshInstallCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chartFileOperations = new ChartFileOperationSynchronizer();
        PackageInstallWorkflowOwner? owner = null;
        Task<bool>? enqueue = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            int blockNextDispatch = 0;
            int blockedDispatchConsumed = 0;
            owner = CreateOwner(
                (_, paths, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    if (paths.Contains("fresh.zip"))
                    {
                        freshInstallCalled.TrySetResult(true);
                    }
                    return new PackageInstallCommandResult([], null);
                },
                action =>
                {
                    if (Volatile.Read(ref blockNextDispatch) != 0
                        && Interlocked.CompareExchange(ref blockedDispatchConsumed, 1, 0) == 0)
                    {
                        enqueueStatusDispatchEntered.TrySetResult();
                        releaseEnqueueStatusDispatch.Wait();
                    }
                    action();
                    return true;
                },
                chartFileOperations: chartFileOperations);
            owner.AttachLibrary(library);
            Volatile.Write(ref blockNextDispatch, 1);

            enqueue = Task.Run(() =>
                owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "late.zip")));
            await TestUiDispatcherHost.AwaitNotificationAsync(enqueueStatusDispatchEntered.Task, enqueue, "package-install.physical-insertion");

            owner.CancelAll();
            bool acquiredDuringDrain = chartFileOperations.TryEnter(out IDisposable duringDrain);
            duringDrain?.Dispose();
            Assert.IsFalse(acquiredDuringDrain, "取消要求だけでは受理済み queue の受付を解放しない。");
            releaseEnqueueStatusDispatch.Set();

            Assert.IsTrue(await enqueue);
            await owner.WaitForIdleAsync();
            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable afterDrain));
            afterDrain.Dispose();
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));

            owner.Enqueue(["fresh.zip"]);
            await TestUiDispatcherHost.AwaitNotificationAsync(freshInstallCalled.Task, owner.WaitForIdleAsync(), "PackageInstallWorkflowOwnerTests.freshInstallCalled");
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, mutationCalls);
        }
        catch (Exception exception)
        {
            bodyFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            releaseEnqueueStatusDispatch.Set();
            try { if (enqueue != null) { await enqueue; } }
            catch (Exception exception) { cleanupFailure ??= exception; }
            try { if (owner != null) { await owner.WaitForIdleAsync(); } }
            catch (Exception exception) { cleanupFailure ??= exception; }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception exception) { cleanupFailure ??= exception; }
        }
        if (bodyFailure != null)
        {
            if (cleanupFailure != null) { bodyFailure.SourceException.Data["PackageInstallCleanupFailure"] = cleanupFailure; }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null) { ExceptionDispatchInfo.Capture(cleanupFailure).Throw(); }
    }

    /// <summary>実行中の追加所有入力を拒否してdispositionの終端を待つ一方、再接続でも導入へ引渡し済みの入力元は旧処理の終端後も保持する。</summary>
    [TestMethod]
    public async Task AdditionalRequest_IsRejectedAndReclaimedWhileHandedOffActiveIngressIsPreserved()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string firstDb = Path.Combine(root, "first", "song.db");
        string secondDb = Path.Combine(root, "second", "song.db");
        string activeRoot = Path.Combine(root, "active-ingress");
        string pendingRoot = Path.Combine(root, "pending-ingress");
        Directory.CreateDirectory(Path.GetDirectoryName(firstDb)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondDb)!);
        Directory.CreateDirectory(activeRoot);
        Directory.CreateDirectory(pendingRoot);
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseActive = new ManualResetEventSlim(false);
        PackageInstallWorkflowOwner? owner = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var firstLibrary = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var secondLibrary = new TestBmsLibrary(secondDb, null, null, string.Empty);
            owner = CreateOwner(
                (library, _, token, _) =>
                {
                    if (ReferenceEquals(library, firstLibrary))
                    {
                        activeStarted.TrySetResult();
                        releaseActive.Wait();
                    }
                    return new PackageInstallCommandResult([], null);
                },
                _ => true);
            owner.AttachLibrary(firstLibrary);
            DroppedInstallBatchRequest activeRequest = CreateOwnedRequest(activeRoot, "active.zip");
            Assert.IsTrue(owner.TryEnqueue(activeRequest));
            await TestUiDispatcherHost.AwaitNotificationAsync(activeStarted.Task, owner.WaitForIdleAsync(), "package-install.active-handoff");
            DroppedInstallBatchRequest pendingRequest = CreateOwnedRequest(pendingRoot, "pending.zip");
            Assert.IsFalse(owner.TryEnqueue(pendingRequest));

            owner.AttachLibrary(secondLibrary);

            await pendingRequest.WaitForDispositionAsync();
            Assert.IsTrue(Directory.Exists(activeRoot), "Installer handoff must protect the active source from queue cleanup.");
            releaseActive.Set();
            await owner.WaitForIdleAsync();
            Assert.IsTrue(Directory.Exists(activeRoot));
        }
        catch (Exception exception)
        {
            bodyFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            releaseActive.Set();
            try { if (owner != null) { await owner.WaitForIdleAsync(); } }
            catch (Exception exception) { cleanupFailure ??= exception; }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception exception) { cleanupFailure ??= exception; }
        }
        if (bodyFailure != null)
        {
            if (cleanupFailure != null) { bodyFailure.SourceException.Data["PackageInstallCleanupFailure"] = cleanupFailure; }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null) { ExceptionDispatchInfo.Capture(cleanupFailure).Throw(); }
    }

    /// <summary>モデルからの案内をUI投稿へ渡し、表示保留や表示例外で導入結果とqueue終端を止めず、表示失敗だけを診断する。</summary>
    [TestMethod]
    public async Task OperationDialogs_AreDispatchedWithoutBlockingQueueOrChangingMutationResult()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var notifications = new Queue<Action>();
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            var immediateDialogs = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            var library = new TestBmsLibrary(
                songDbPath, null, null,
                new BmsLibraryInitializationTestSupport.TestFileMutationService(), immediateDialogs);
            var bufferedDialogs = new ScopedOperationDialogCoordinator(immediateDialogs);
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var reportedFailures = new List<Exception>();
            var displayFailure = new InvalidOperationException("notification failure marker");
            var displayed = new FileDbReportRecordingDialogs { MessageFailure = displayFailure };
            int completions = 0;
            int failures = 0;
            owner = new PackageInstallWorkflowOwner(
                displayed,
                chartFileOperations,
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((_, _, _, _) =>
                {
                    bufferedDialogs.Show("notice marker", "caption marker", UiDialogButton.OK,
                        UiDialogIcon.Information, UiDialogDefaultResult.OK);
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                }),
                new NoOpChartMutationPlaybackPort(),
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }

                    return true;
                },
                reportedFailures.Add);
            owner.CompletionPublished += _ => completions++;
            owner.FailurePublished += _ => failures++;
            owner.AttachLibrary(library);
            Assert.IsTrue(owner.Enqueue([Path.Combine(root, "source.zip")]));
            await owner.WaitForIdleAsync();

            Assert.AreEqual(0, immediateDialogs.Calls.Count, "worker から同期 dialog port を呼ばない。");
            Assert.AreEqual(0, displayed.Messages.Count, "UI dispatch を drain する前に表示処理を呼ばない。");
            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable afterCompletion));
            afterCompletion.Dispose();
            DrainNotifications(notifications);

            Assert.AreEqual(1, displayed.Messages.Count);
            Assert.AreEqual("notice marker", displayed.Messages[0].MessageBoxText);
            Assert.AreEqual(System.Windows.MessageBoxButton.OK, displayed.Messages[0].Button);
            Assert.AreEqual(1, completions);
            Assert.AreEqual(0, failures);
            CollectionAssert.AreEqual(new[] { displayFailure }, reportedFailures);
        }
        finally
        {
            if (owner != null)
            {
                await owner.WaitForIdleAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static DroppedInstallBatchRequest CreateOwnedRequest(string root, string originalPath)
    {
        return new DroppedInstallBatchRequest(
            [Path.Combine(root, originalPath)],
            [originalPath],
            [root],
            DeleteOwnedRoot,
            null);
    }

    private static void DeleteOwnedRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static PackageInstallWorkflowOwner CreateOwner(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, IPackageInstallProgressWriter, PackageInstallCommandResult> installBatch,
        Func<Action, bool> dispatchToUi,
        Action<Exception>? reportNotificationFailure = null,
        DroppedInstallIngressMaterializer? droppedInstallIngressMaterializer = null,
        ChartFileOperationSynchronizer? chartFileOperations = null)
    {
        return new PackageInstallWorkflowOwner(
            new FileDbReportRecordingDialogs(),
            chartFileOperations ?? new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new DelegatePackageInstallMutationPort(installBatch),
            new NoOpChartMutationPlaybackPort(),
            dispatchToUi,
            reportNotificationFailure,
            droppedInstallIngressMaterializer);
    }

    private static void DrainNotifications(Queue<Action> notifications)
    {
        while (true)
        {
            Action notification;
            lock (notifications)
            {
                if (notifications.Count == 0)
                {
                    return;
                }
                notification = notifications.Dequeue();
            }
            notification();
        }
    }

    private sealed class BoundedProgressPackageInstallMutationPort :
        IPackageInstallMutationPort,
        IDisposable
    {
        private readonly int progressCount;

        private readonly ManualResetEventSlim release = new(false);

        private IPackageInstallProgressWriter progressWriter = null!;

        internal BoundedProgressPackageInstallMutationPort(int progressCount)
        {
            this.progressCount = progressCount;
        }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool MutationIsBlocked => Started.Task.IsCompletedSuccessfully && !release.IsSet;

        public Task<PackageInstallCommandResult> InstallWithProgressAsync(
            BMSLibrary library,
            IEnumerable<string> installPaths,
            CancellationToken token,
            IPackageInstallProgressWriter progressWriter)
        {
            this.progressWriter = progressWriter ?? throw new ArgumentNullException(nameof(progressWriter));
            for (int index = 1; index <= progressCount; index++)
            {
                progressWriter.TryWrite(PackageInstallProgressUpdate.ArchiveExtractStarted(
                    "source-" + index + ".zip",
                    index,
                    progressCount));
                progressWriter.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
            }
            Started.TrySetResult();
            release.Wait();
            Returned.TrySetResult();
            return Task.FromResult(new PackageInstallCommandResult([new ChartPackage()], null));
        }

        internal void EmitLateProgress()
        {
            Volatile.Read(ref progressWriter)?.TryWrite(
                PackageInstallProgressUpdate.SourceProcessed());
        }

        internal void Release() => release.Set();

        public void Dispose()
        {
            release.Dispose();
        }
    }

    private sealed class TwoBatchProgressPackageInstallMutationPort :
        IPackageInstallMutationPort,
        IDisposable
    {
        private readonly int progressCount;

        private readonly ManualResetEventSlim firstRelease = new(false);

        private readonly ManualResetEventSlim secondRelease = new(false);

        private IPackageInstallProgressWriter firstProgressWriter = null!;

        private int invocationCount;

        internal TwoBatchProgressPackageInstallMutationPort(int progressCount)
        {
            this.progressCount = progressCount;
        }

        internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PackageInstallCommandResult> InstallWithProgressAsync(
            BMSLibrary library,
            IEnumerable<string> installPaths,
            CancellationToken token,
            IPackageInstallProgressWriter progressWriter)
        {
            int invocation = Interlocked.Increment(ref invocationCount);
            if (invocation == 1)
            {
                firstProgressWriter = progressWriter ?? throw new ArgumentNullException(nameof(progressWriter));
                for (int index = 1; index <= progressCount; index++)
                {
                    progressWriter.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
                }
                FirstStarted.TrySetResult();
                firstRelease.Wait();
                FirstReturned.TrySetResult();
            }
            else
            {
                if (progressWriter is null)
                {
                    throw new ArgumentNullException(nameof(progressWriter));
                }
                SecondStarted.TrySetResult();
                secondRelease.Wait();
                SecondReturned.TrySetResult();
            }
            return Task.FromResult(new PackageInstallCommandResult([new ChartPackage()], null));
        }

        internal void EmitLateProgressFromFirstBatch()
        {
            Volatile.Read(ref firstProgressWriter)?.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
        }

        internal void ReleaseFirst() => firstRelease.Set();

        internal void ReleaseSecond() => secondRelease.Set();

        public void Dispose()
        {
            secondRelease.Dispose();
            firstRelease.Dispose();
        }
    }

    private static async Task<QueuedPackageInstallUiScheduler.ScheduledOperation> ReleaseUntilStatusObservationAsync(
        QueuedPackageInstallUiScheduler scheduler,
        IReadOnlyList<string> observations,
        string expectedStatus)
    {
        while (true)
        {
            QueuedPackageInstallUiScheduler.ScheduledOperation operation =
                await scheduler.WaitForNextAsync();
            Assert.IsTrue(operation.IsAccepted);
            Assert.IsFalse(operation.IsCompleted);
            int countBeforeRelease = observations.Count;
            Task release = TestUiDispatcherHost.Dispatcher.InvokeAsync(
                () => scheduler.Release(operation)).Task;
            await Task.WhenAll(release, operation.Completion);
            if (observations.Count == countBeforeRelease)
            {
                continue;
            }

            Assert.AreEqual(countBeforeRelease + 1, observations.Count);
            string observedStatus = observations[^1];
            if (observedStatus == expectedStatus)
            {
                return operation;
            }

            Assert.AreEqual(
                "active",
                observedStatus,
                "Only active progress may be published before the terminal inactive status.");
        }
    }

    private sealed class QueuedPackageInstallUiScheduler : IUiScheduler
    {
        private readonly object syncRoot = new();

        private readonly Queue<ScheduledOperation> pending = new();

        private TaskCompletionSource<ScheduledOperation>? nextScheduled;

        internal int InvokeCount { get; private set; }

        internal int InvokeAsyncCount { get; private set; }

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            ArgumentNullException.ThrowIfNull(action);
            var operation = new ScheduledOperation(action, priority);
            TaskCompletionSource<ScheduledOperation>? waiter;
            lock (syncRoot)
            {
                pending.Enqueue(operation);
                waiter = nextScheduled;
                nextScheduled = null;
            }
            waiter?.TrySetResult(operation);
            return operation;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            InvokeCount++;
            throw new InvalidOperationException("Package-install UI dispatch must use Schedule, not Invoke.");
        }

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            InvokeCount++;
            throw new InvalidOperationException("Package-install UI dispatch must use Schedule, not Invoke.");
        }

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            InvokeAsyncCount++;
            throw new InvalidOperationException("Package-install UI dispatch must use Schedule, not InvokeAsync.");
        }

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            InvokeAsyncCount++;
            throw new InvalidOperationException("Package-install UI dispatch must use Schedule, not InvokeAsync.");
        }

        internal Task<ScheduledOperation> WaitForNextAsync()
        {
            lock (syncRoot)
            {
                if (pending.Count > 0)
                {
                    return Task.FromResult(pending.Peek());
                }
                nextScheduled = new TaskCompletionSource<ScheduledOperation>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return nextScheduled.Task;
            }
        }

        internal void Release(ScheduledOperation operation)
        {
            lock (syncRoot)
            {
                Assert.AreSame(operation, pending.Peek());
                pending.Dequeue();
            }
            try
            {
                operation.Action();
                operation.Complete();
            }
            catch (Exception exception)
            {
                operation.Fail(exception);
                throw;
            }
        }

        internal void ReleaseAll()
        {
            while (true)
            {
                ScheduledOperation? operation;
                lock (syncRoot)
                {
                    operation = pending.Count == 0 ? null : pending.Peek();
                }
                if (operation == null)
                {
                    return;
                }
                Release(operation);
            }
        }

        internal sealed class ScheduledOperation : IUiScheduledOperation
        {
            private readonly TaskCompletionSource<bool> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ScheduledOperation(Action action, UiSchedulePriority priority)
            {
                Action = action;
                Priority = priority;
            }

            internal Action Action { get; }

            internal UiSchedulePriority Priority { get; }

            public bool IsAccepted => true;

            public bool IsCompleted => completion.Task.IsCompleted;

            public bool IsAborted => completion.Task.IsCanceled;

            public string? RejectionReason => null;

            public Task Completion => completion.Task;

            public void Abort() => completion.TrySetCanceled();

            internal void Complete() => completion.TrySetResult(true);

            internal void Fail(Exception exception) => completion.TrySetException(exception);
        }
    }
}

internal sealed class DelegatePackageInstallMutationPort : IPackageInstallMutationPort
{
    private readonly Func<BMSLibrary, IEnumerable<string>, CancellationToken, IPackageInstallProgressWriter, PackageInstallCommandResult> install;

    internal DelegatePackageInstallMutationPort(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, IPackageInstallProgressWriter, PackageInstallCommandResult> install)
    {
        this.install = install ?? throw new ArgumentNullException(nameof(install));
    }

    public Task<PackageInstallCommandResult> InstallWithProgressAsync(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        IPackageInstallProgressWriter progressWriter)
    {
        return Task.FromResult(install(library, installPaths, token, progressWriter));
    }
}
