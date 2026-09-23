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
public sealed class PackageInstallWorkflowOwnerTests
{
    [TestMethod]
    public async Task ProductionPackageInstallDispatcher_QueuesAtNormalWithoutSynchronousUiWait()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        string installDirectory = Path.Combine(root, "install-source");
        Directory.CreateDirectory(installDirectory);
        File.WriteAllBytes(songDbPath, []);
        var settings = new Settings();
        var scheduler = new QueuedPackageInstallUiScheduler();
        Task? enqueueTask = null;
        PackageInstallWorkflowOwner? workflow = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            try
            {
                MainWindowViewModel? viewModel = null;
                TestBmsLibrary? library = null;
                TestUiDispatcherHost.Invoke(() =>
                {
                    using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
                    {
                    }
                    var composition = new ApplicationComposition(
                        settingsEditSession: new NoOpSettingsEditSession(settings),
                        uiScheduler: scheduler,
                        applicationLifetime: TestApplicationContext.CreateLifetime(),
                        cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                    viewModel = composition.CreateMainWindowViewModel();
                    scheduler.ReleaseAll();
                    library = new TestBmsLibrary(
                        songDbPath,
                        null,
                        null,
                        string.Empty,
                        () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
                    viewModel.PackageInstallWorkflow.AttachLibrary(library);
                    scheduler.ReleaseAll();
                });

                Assert.IsNotNull(viewModel);
                Assert.IsNotNull(library);
                workflow = viewModel.PackageInstallWorkflow;
                var observations = new List<string>();
                workflow.StatusChanged += snapshot =>
                    observations.Add(snapshot.IsActive ? "active" : "inactive");
                int invokeCountBefore = scheduler.InvokeCount;
                int invokeAsyncCountBefore = scheduler.InvokeAsyncCount;

                enqueueTask = Task.Factory.StartNew(
                    () => viewModel.PackageInstallWorkflow.Enqueue([installDirectory]),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                // enqueue 受付と通常優先度の通知を、UI側の実際の完了で観測する。
                await scheduler.WaitForPendingCountAsync(2);
                await enqueueTask;
                QueuedPackageInstallUiScheduler.ScheduledOperation activeDispatch =
                    scheduler.PeekNext();
                Assert.AreEqual(UiSchedulePriority.Normal, activeDispatch.Priority);
                Assert.IsTrue(activeDispatch.IsAccepted);
                Assert.IsFalse(activeDispatch.IsCompleted);
                Assert.AreEqual(0, observations.Count);
                Assert.AreEqual(invokeCountBefore, scheduler.InvokeCount);
                Assert.AreEqual(invokeAsyncCountBefore, scheduler.InvokeAsyncCount);

                Task activeDispatchRelease = TestUiDispatcherHost.Dispatcher.InvokeAsync(
                    () => scheduler.Release(activeDispatch)).Task;
                await Task.WhenAll(activeDispatchRelease, activeDispatch.Completion);
                Assert.AreEqual(1, observations.Count);
                Assert.AreEqual("active", observations[0]);

                Task<QueuedPackageInstallUiScheduler.ScheduledOperation> terminalDispatchTask =
                    scheduler.WaitForNextAsync();
                QueuedPackageInstallUiScheduler.ScheduledOperation terminalDispatch =
                    await terminalDispatchTask;
                Assert.AreEqual(UiSchedulePriority.Normal, terminalDispatch.Priority);
                Assert.IsTrue(terminalDispatch.IsAccepted);
                Assert.IsFalse(terminalDispatch.IsCompleted);
                Assert.AreEqual(1, observations.Count);

                Task terminalDispatchRelease = TestUiDispatcherHost.Dispatcher.InvokeAsync(
                    () => scheduler.Release(terminalDispatch)).Task;
                Task idle = viewModel.PackageInstallWorkflow.WaitForIdleAsync();
                await Task.WhenAll(terminalDispatchRelease, terminalDispatch.Completion, idle);

                Assert.AreEqual("active|inactive", string.Join("|", observations));
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
            // The worker can enqueue its terminal status after the first drain and
            // just before the enqueue task completes. Drain again before observing
            // owner idleness so no accepted UI operation can outlive this fixture.
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
            // A final drain closes the race where the terminal callback is queued
            // while the workflow transitions to idle.
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

    [TestMethod]
    public async Task ActiveProgressBurst_QueuesOneLatestStatusBeforeCompletionAndInactive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var published = new List<string>();
            PackageInstallWorkflowOwner owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    for (int index = 1; index <= 20; index++)
                    {
                        onArchive("archive-" + index + ".zip", index, 20);
                        onPath();
                    }
                    return [new ChartPackage()];
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

            await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public async Task ProgressWriter_BoundsActiveDispatchAndDropsLateProgressAfterSeal()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                new ChartMutationActivityOwner(),
                mutationPort,
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
            Assert.IsTrue(
                mutationPort.Started.Wait(TimeSpan.FromSeconds(5)),
                "The package progress mutation did not start.");
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
            Assert.IsTrue(
                mutationPort.Returned.Wait(TimeSpan.FromSeconds(5)),
                "The package progress mutation did not return after release.");
            while (!terminalPublished.IsSet)
            {
                DrainNotifications(notifications);
                if (terminalPublished.IsSet)
                {
                    break;
                }
                Assert.IsTrue(
                    await notificationSignal.WaitAsync(TimeSpan.FromSeconds(5)),
                    "The terminal inactive notification dispatcher must be signaled.");
            }
            Assert.IsTrue(terminalPublished.IsSet, "The terminal inactive notification was not published.");
            DrainNotifications(notifications);
            await owner!.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
            if (mutationPort.Started.IsSet)
            {
                try
                {
                    if (!mutationPort.Returned.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The package progress mutation did not return during cleanup.");
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            if (owner != null)
            {
                try
                {
                    Task idle = owner.WaitForIdleAsync();
                    while (!idle.IsCompleted)
                    {
                        DrainNotifications(notifications);
                        if (idle.IsCompleted)
                        {
                            break;
                        }
                        if (!await notificationSignal.WaitAsync(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("The package install cleanup did not publish terminal notification.");
                        }
                    }
                    await idle.WaitAsync(TimeSpan.FromSeconds(5));
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
            if (mutationPort.WorkerThread is { } workerThread
                && workerThread != Thread.CurrentThread)
            {
                try
                {
                    if (!workerThread.Join(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The package install worker thread did not stop during cleanup.");
                    }
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

    [TestMethod]
    public async Task ProgressWriter_SealsBeforeLateSourceCanReachNextBatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                new ChartMutationActivityOwner(),
                mutationPort,
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
            Assert.IsTrue(
                mutationPort.FirstStarted.Wait(TimeSpan.FromSeconds(5)),
                "The first package progress mutation did not start.");
            DrainNotifications(notifications);
            while (notificationSignal.Wait(0))
            {
            }
            statusValues.Clear();

            owner.Enqueue(Enumerable.Range(1, 64).Select(index => "seal-second-" + index + ".zip"));
            DrainNotifications(notifications);
            while (notificationSignal.Wait(0))
            {
            }
            statusValues.Clear();
            mutationPort.ReleaseFirst();
            Assert.IsTrue(
                mutationPort.FirstReturned.Wait(TimeSpan.FromSeconds(5)),
                "The first package progress mutation did not return after release.");
            Assert.IsTrue(
                mutationPort.SecondStarted.Wait(TimeSpan.FromSeconds(5)),
                "The second package progress mutation did not start.");

            while (!statusValues.Any(value => value.StartsWith("active:", StringComparison.Ordinal)))
            {
                DrainNotifications(notifications);
                if (statusValues.Any(value => value.StartsWith("active:", StringComparison.Ordinal)))
                {
                    break;
                }
                Assert.IsTrue(
                    await notificationSignal.WaitAsync(TimeSpan.FromSeconds(5)),
                    "The second-batch active notification dispatcher must be signaled.");
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
            Assert.IsTrue(
                mutationPort.SecondReturned.Wait(TimeSpan.FromSeconds(5)),
                "The second package progress mutation did not return after release.");
            while (!terminalPublished.IsSet)
            {
                DrainNotifications(notifications);
                if (terminalPublished.IsSet)
                {
                    break;
                }
                Assert.IsTrue(
                    await notificationSignal.WaitAsync(TimeSpan.FromSeconds(5)),
                    "The terminal inactive notification dispatcher must be signaled.");
            }
            Assert.IsTrue(terminalPublished.IsSet, "The terminal inactive notification was not published.");
            DrainNotifications(notifications);
            await owner!.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
            if (mutationPort.FirstStarted.IsSet)
            {
                try
                {
                    if (!mutationPort.FirstReturned.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The first package progress mutation did not return during cleanup.");
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            if (mutationPort.SecondStarted.IsSet)
            {
                try
                {
                    if (!mutationPort.SecondReturned.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The second package progress mutation did not return during cleanup.");
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            if (owner != null)
            {
                try
                {
                    Task idle = owner.WaitForIdleAsync();
                    while (!idle.IsCompleted)
                    {
                        DrainNotifications(notifications);
                        if (idle.IsCompleted)
                        {
                            break;
                        }
                        if (!await notificationSignal.WaitAsync(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("The package install cleanup did not publish terminal notification.");
                        }
                    }
                    await idle.WaitAsync(TimeSpan.FromSeconds(5));
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
            if (mutationPort.WorkerThread is { } workerThread
                && workerThread != Thread.CurrentThread)
            {
                try
                {
                    if (!workerThread.Join(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The package install worker thread did not stop during cleanup.");
                    }
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

    [TestMethod]
    public async Task GenerationReplacement_BusyRequestFailsFastThenFreshRequestRunsAfterRelease()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        string firstDb = Path.Combine(firstRoot, "song.db");
        string secondDb = Path.Combine(secondRoot, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim(false);
        var busyFailure = new TaskCompletionSource<PackageInstallFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            var calls = new List<string>();
            int completions = 0;
            owner = CreateOwner(
                (library, paths, token, onPath, onArchive) =>
                {
                    lock (calls)
                    {
                        calls.Add(Path.GetFileName(paths.FirstOrDefault() ?? string.Empty));
                    }
                    if (ReferenceEquals(library, first))
                    {
                        firstStarted.TrySetResult(true);
                        releaseFirst.Wait();
                    }
                    return [new ChartPackage()];
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.FailurePublished += failure => busyFailure.TrySetResult(failure);
            owner.CompletionPublished += _ => Interlocked.Increment(ref completions);
            owner.AttachLibrary(first);

            owner.Enqueue(["first.zip"]);
            await firstStarted.Task;
            owner.AttachLibrary(second);
            Assert.IsFalse(owner.Enqueue(["second.zip"]));
            Assert.IsFalse(busyFailure.Task.IsCompleted, "未受理を実行済み batch の失敗として通知しない。");
            lock (calls)
            {
                CollectionAssert.AreEqual(new[] { "first.zip" }, calls);
            }

            releaseFirst.Set();
            await AssertOwnerIdleAsync(owner);
            owner.Enqueue(["second-fresh.zip"]);
            await AssertOwnerIdleAsync(owner);
            lock (calls)
            {
                CollectionAssert.AreEqual(new[] { "first.zip", "second-fresh.zip" }, calls);
            }
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

    [TestMethod]
    public async Task RetiredQueuePruningWaitsForTerminalReceiptAfterStatusDispatchStops()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            owner = CreateOwner(
                (library, paths, token, onPath, onArchive) =>
                {
                    if (ReferenceEquals(library, first))
                    {
                        firstStarted.TrySetResult(true);
                    }
                    return [];
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
            await firstStarted.Task;

            await terminalEntered.Task;
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
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref mutationCalls);
                return [];
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

    [TestMethod]
    public async Task AcquireAndTryEnqueueDroppedPaths_StagesTransientSourceThroughMutationPort()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
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
                (_, paths, _, _, _) =>
                {
                    installedPath = paths.Single();
                    mutationEntered.TrySetResult(true);
                    allowMutationRead.Wait();
                    installedContents = File.ReadAllText(installedPath);
                    mutationFinished.TrySetResult(true);
                    return [];
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
            await mutationEntered.Task;
            File.Delete(source);
            allowMutationRead.Set();
            await mutationFinished.Task;
            await AssertOwnerIdleAsync(owner);
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

    /// <summary>
    /// 先行 batch の失敗も後続の完了も、queue 全体の受付解放後に一度だけ通知します。
    /// </summary>
    [TestMethod]
    public async Task Enqueue_PublishesCompletionAfterLiveInstallReturnsAndContinuesAfterFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var calls = new List<string>();
            var failures = new List<PackageInstallFailure>();
            var eventOrder = new List<string>();
            owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
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
                    return [new ChartPackage()];
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
            await firstInstallEntered.Task;
            owner.Enqueue([Path.Combine(root, "second.zip")]);
            releaseFirstInstall.Set();

            await secondInstallEntered.Task;
            lock (observationLock)
            {
                Assert.AreEqual(0, failures.Count, "後続 batch の実行中に先行 batch の terminal を発行しない。");
            }
            bool admittedWhileRunning = chartFileOperations.TryEnter(out IDisposable duringInstall);
            duringInstall?.Dispose();
            Assert.IsFalse(admittedWhileRunning, "受理済みの後続 batch まで共通受付を保持する。");
            Assert.IsFalse(completed.Task.IsCompleted, "Completion must not be published before the live install returns.");
            Assert.IsFalse(terminalInactive.Task.IsCompleted, "The queue must remain active while the following batch is running.");
            Assert.IsFalse(owner.IsIdle, "The workflow must not become idle before the live install returns.");
            releaseSecondInstall.Set();

            await secondFinished.Task;
            await terminalInactive.Task;
            await AssertOwnerIdleAsync(owner);
            string[] callSnapshot;
            PackageInstallFailure[] failureSnapshot;
            string[] eventOrderSnapshot;
            lock (observationLock)
            {
                callSnapshot = calls.ToArray();
                failureSnapshot = failures.ToArray();
                eventOrderSnapshot = eventOrder.ToArray();
            }

            await completed.Task;
            Assert.AreEqual(1, failureSnapshot.Length);
            CollectionAssert.AreEqual(new[] { "first.zip", "second.zip" }, callSnapshot);
            CollectionAssert.AreEqual(new[] { "failure", "completion", "inactive" }, eventOrderSnapshot);
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

    [TestMethod]
    public async Task AttachLibrary_InvalidatesCompletionFromPreviousGeneration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateOwner(
                (library, paths, token, onPath, onArchive) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    return [new ChartPackage()];
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.CompletionPublished += _ => completion.TrySetResult(true);
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            await started.Task;
            owner.AttachLibrary(second);
            release.Set();

            await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public async Task Enqueue_RejectsBusyBeforeQueueingThenRunsAfterRelease()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var chartMutationActivity = new ChartMutationActivityOwner();
            int mutationCalls = 0;
            var failure = new TaskCompletionSource<PackageInstallFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<PackageInstallCompletionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                chartMutationActivity,
                new DelegatePackageInstallMutationPort(
                    (library, paths, token, onPath, onArchive) =>
                    {
                        Interlocked.Increment(ref mutationCalls);
                        return [new ChartPackage()];
                    }),
                action =>
                {
                    Task.Run(action);
                    return true;
                });
            owner.FailurePublished += published => failure.TrySetResult(published);
            owner.CompletionPublished += published => completion.TrySetResult(published);
            owner.AttachLibrary(first);

            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable incumbent));
            try
            {
                Assert.IsFalse(owner.Enqueue([Path.Combine(root, "first-generation.zip")]));
                await AssertOwnerIdleAsync(owner);
                Assert.IsFalse(failure.Task.IsCompleted);
                Assert.AreEqual(0, mutationCalls);
            }
            finally
            {
                incumbent.Dispose();
            }

            owner.Enqueue([Path.Combine(root, "second-generation.zip")]);
            await AssertOwnerIdleAsync(owner);
            await completion.Task;
            Assert.IsFalse(failure.Task.IsCompleted);
            Assert.AreEqual(1, mutationCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GateBusy_AbandonsOwnedIngressWithoutCallingInstaller()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var chartMutationActivity = new ChartMutationActivityOwner();
            int mutationCalls = 0;
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                chartFileOperations,
                chartMutationActivity,
                new DelegatePackageInstallMutationPort((_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [];
                }),
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
                await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public async Task CancelAll_FromSuppressionCallbackWinsBeforeHandoffAndDeletesIngress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            int completionPublished = 0;
            int failurePublished = 0;
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [];
                }),
                action =>
                {
                    action();
                    return true;
                });
            owner.CompletionPublished += _ => Interlocked.Increment(ref completionPublished);
            owner.FailurePublished += _ => Interlocked.Increment(ref failurePublished);
            owner.RefreshSuppressionChanged += (_, args) =>
            {
                if (args.IsSuppressed)
                {
                    owner.CancelAll();
                }
            };
            owner.AttachLibrary(library);

            Assert.IsTrue(owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "chart.bms")));

            await AssertOwnerIdleAsync(owner);
            Assert.AreEqual(0, mutationCalls);
            Assert.AreEqual(0, completionPublished);
            Assert.AreEqual(0, failurePublished);
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

    [TestMethod]
    public async Task DelayedFailurePublication_ReportsDiagnosticsAfterGenerationChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
        using var notificationQueued = new ManualResetEventSlim(false);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            int failurePublished = 0;
            int diagnosticReports = 0;
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (library, paths, token, onPath, onArchive) => throw new InvalidOperationException("install failed")),
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                        notificationQueued.Set();
                    }
                    return true;
                },
                _ => Interlocked.Increment(ref diagnosticReports));
            owner.FailurePublished += _ => Interlocked.Increment(ref failurePublished);
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "failed-generation.zip")]);

            await AssertOwnerIdleAsync(owner);
            notificationQueued.Wait();
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

    [TestMethod]
    public async Task DispatcherRejection_ReportsOriginalInstallFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (current, paths, token, onPath, onArchive) => throw new InvalidOperationException("install failed")),
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

            await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public async Task DispatcherException_ReportsOriginalInstallFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            var owner = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (current, paths, token, onPath, onArchive) => throw new InvalidOperationException("install failed")),
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

            await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public async Task FailureNotificationException_ReportsInstallAndNotificationFailures()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            PackageInstallWorkflowOwner owner = CreateOwner(
                (current, paths, token, onPath, onArchive) => throw new InvalidOperationException("install failed"),
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

            await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public async Task CancelAfterLiveApplyReturns_PublishesCompletionReceipt()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        using var release = new ManualResetEventSlim(false);
        PackageInstallWorkflowOwner? owner = null;
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    Assert.IsTrue(token.IsCancellationRequested, "The test must cancel while live apply is in progress.");
                    return [new ChartPackage()];
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
            await started.Task;
            owner.CancelAll();
            release.Set();

            await completion.Task;
            await AssertOwnerIdleAsync(owner);
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

    /// <summary>
    /// 型付き session の異常結果と終了処理の例外も、受付解放後に通知します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DurableFinalizationFailure_PublishesTypedCompletionWithoutRegisteredPackages(bool failSuppressionCleanup)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }

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
            var port = new DelegatePackageInstallTerminalMutationPort(
                (_, _, _, _, _) =>
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
                action =>
                {
                    action();
                    return true;
                });
            var cleanupFailure = new IOException("drop-scope-cleanup-marker");
            var failed = new TaskCompletionSource<PackageInstallFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.RefreshSuppressionChanged += (_, args) =>
            {
                if (failSuppressionCleanup && !args.IsSuppressed) throw cleanupFailure;
            };
            void RecordTerminalAdmission()
            {
                bool admissionAvailable = chartFileOperations.TryEnter(out IDisposable admission);
                admission?.Dispose();
                terminalAdmissions.Add(admissionAvailable);
            }
            owner.FailurePublished += failure =>
            {
                RecordTerminalAdmission();
                failed.TrySetResult(failure);
            };
            owner.CompletionPublished += published =>
            {
                RecordTerminalAdmission();
                completion.TrySetResult(published);
            };
            owner.AttachLibrary(new TestBmsLibrary(songDbPath, null, null, string.Empty));
            owner.Enqueue([Path.Combine(root, "source.zip")]);

            PackageInstallCompletionReceipt publishedReceipt;
            if (failSuppressionCleanup)
            {
                PackageInstallFailure failure = await failed.Task;
                Assert.AreSame(cleanupFailure, failure.Exception);
                Assert.IsNotNull(failure.CommandResult);
                Assert.AreSame(sessionReceipt, failure.CommandResult.SessionReceipt);
                Assert.IsFalse(completion.Task.IsCompleted);
                publishedReceipt = new PackageInstallCompletionReceipt(failure.Generation,
                    failure.CommandResult.RegisteredPackages, failure.CommandResult);
            }
            else
            {
                publishedReceipt = await completion.Task;
            }
            await AssertOwnerIdleAsync(owner);

            CollectionAssert.AreEqual(new[] { true }, terminalAdmissions,
                "型付き session の異常結果も workflow 終了処理の例外も、受付解放後に一度だけ通知する。");
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

    [TestMethod]
    public async Task AttachLibrary_BusyRequestFailsFastThenFreshRequestRunsAfterRelease()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var busyFailure = new TaskCompletionSource<PackageInstallFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<string>();
            int completions = 0;
            owner = CreateOwner(
                (library, paths, token, onPath, onArchive) =>
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
                        return [new ChartPackage()];
                    }
                    secondStarted.TrySetResult(true);
                    return [new ChartPackage()];
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
            await firstStarted.Task;

            owner.AttachLibrary(second);
            Assert.IsFalse(owner.Enqueue([Path.Combine(root, "second-generation.zip")]));
            Assert.IsFalse(busyFailure.Task.IsCompleted, "未受理の要求は batch として開始しない。");
            Assert.IsFalse(secondStarted.Task.IsCompleted, "A busy replacement request must not enter mutation.");

            releaseFirst.Set();
            await AssertOwnerIdleAsync(owner);
            owner.Enqueue([Path.Combine(root, "second-fresh-generation.zip")]);
            await secondStarted.Task;
            await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public async Task RequestShutdown_PreventsLaterEnqueueFromStartingLiveMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            PackageInstallWorkflowOwner owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [new ChartPackage()];
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.AttachLibrary(library);
            owner.RequestShutdown();
            owner.Enqueue([Path.Combine(root, "after-shutdown.zip")]);

            await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public async Task NotificationFailure_DoesNotStopQueueLifecycle()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PackageInstallWorkflowOwner owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    int call = Interlocked.Increment(ref mutationCalls);
                    if (call == 2)
                    {
                        secondFinished.TrySetResult(true);
                    }
                    return [new ChartPackage()];
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
            owner.Enqueue([Path.Combine(root, "second.zip")]);

            await secondFinished.Task;
            await AssertOwnerIdleAsync(owner);
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

    [TestMethod]
    public void TryEnqueue_AfterShutdownRejectsAndDeletesOwnedIngressRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PackageInstallWorkflowOwner owner = CreateOwner((_, _, _, _, _) => [], _ => true);
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

    [TestMethod]
    public async Task RequestShutdown_AfterPhysicalInsertionDrainsRequestWithoutPrivateLockCoordination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        using var enqueueStatusDispatchEntered = new ManualResetEventSlim(false);
        using var releaseEnqueueStatusDispatch = new ManualResetEventSlim(false);
        var chartFileOperations = new ChartFileOperationSynchronizer();
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            int blockNextDispatch = 0;
            int blockedDispatchConsumed = 0;
            PackageInstallWorkflowOwner owner = CreateOwner(
                (_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [];
                },
                action =>
                {
                    if (Volatile.Read(ref blockNextDispatch) != 0
                        && Interlocked.CompareExchange(ref blockedDispatchConsumed, 1, 0) == 0)
                    {
                        enqueueStatusDispatchEntered.Set();
                        releaseEnqueueStatusDispatch.Wait();
                    }
                    action();
                    return true;
                },
                chartFileOperations: chartFileOperations);
            owner.AttachLibrary(library);
            Volatile.Write(ref blockNextDispatch, 1);

            Task<bool> enqueue = Task.Run(() =>
                owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "chart.bms")));
            enqueueStatusDispatchEntered.Wait();

            var shutdown = Task.Run(owner.RequestShutdown);
            await shutdown;
            releaseEnqueueStatusDispatch.Set();

            bool enqueueAccepted = await enqueue;
            Assert.IsTrue(enqueueAccepted, "Physical insertion preceding shutdown remains an accepted transfer.");
            await AssertOwnerIdleAsync(owner);
            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable afterDrain));
            afterDrain.Dispose();
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));
        }
        finally
        {
            releaseEnqueueStatusDispatch.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CancelAll_AfterPhysicalInsertionCannotCancelFreshPostDrainAdmission()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        using var enqueueStatusDispatchEntered = new ManualResetEventSlim(false);
        using var releaseEnqueueStatusDispatch = new ManualResetEventSlim(false);
        var freshInstallCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chartFileOperations = new ChartFileOperationSynchronizer();
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            int blockNextDispatch = 0;
            int blockedDispatchConsumed = 0;
            PackageInstallWorkflowOwner owner = CreateOwner(
                (_, paths, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    if (paths.Contains("fresh.zip"))
                    {
                        freshInstallCalled.TrySetResult(true);
                    }
                    return [];
                },
                action =>
                {
                    if (Volatile.Read(ref blockNextDispatch) != 0
                        && Interlocked.CompareExchange(ref blockedDispatchConsumed, 1, 0) == 0)
                    {
                        enqueueStatusDispatchEntered.Set();
                        releaseEnqueueStatusDispatch.Wait();
                    }
                    action();
                    return true;
                },
                chartFileOperations: chartFileOperations);
            owner.AttachLibrary(library);
            Volatile.Write(ref blockNextDispatch, 1);

            Task<bool> enqueue = Task.Run(() =>
                owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "late.zip")));
            enqueueStatusDispatchEntered.Wait();

            owner.CancelAll();
            bool acquiredDuringDrain = chartFileOperations.TryEnter(out IDisposable duringDrain);
            duringDrain?.Dispose();
            Assert.IsFalse(acquiredDuringDrain, "取消要求だけでは受理済み queue の受付を解放しない。");
            releaseEnqueueStatusDispatch.Set();

            Assert.IsTrue(await enqueue);
            await AssertOwnerIdleAsync(owner);
            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable afterDrain));
            afterDrain.Dispose();
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));

            owner.Enqueue(["fresh.zip"]);
            await freshInstallCalled.Task;
            await AssertOwnerIdleAsync(owner);
            Assert.AreEqual(1, mutationCalls);
        }
        finally
        {
            releaseEnqueueStatusDispatch.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task AttachLibrary_DeletesOldPendingIngressButPreservesHandedOffActiveIngress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
        using var activeStarted = new ManualResetEventSlim(false);
        using var releaseActive = new ManualResetEventSlim(false);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var firstLibrary = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var secondLibrary = new TestBmsLibrary(secondDb, null, null, string.Empty);
            PackageInstallWorkflowOwner owner = CreateOwner(
                (library, _, token, _, _) =>
                {
                    if (ReferenceEquals(library, firstLibrary))
                    {
                        activeStarted.Set();
                        releaseActive.Wait();
                    }
                    return [];
                },
                _ => true);
            owner.AttachLibrary(firstLibrary);
            DroppedInstallBatchRequest activeRequest = CreateOwnedRequest(activeRoot, "active.zip");
            Assert.IsTrue(owner.TryEnqueue(activeRequest));
            activeStarted.Wait();
            DroppedInstallBatchRequest pendingRequest = CreateOwnedRequest(pendingRoot, "pending.zip");
            Assert.IsTrue(owner.TryEnqueue(pendingRequest));

            owner.AttachLibrary(secondLibrary);

            await pendingRequest.WaitForDispositionAsync();
            Assert.IsTrue(Directory.Exists(activeRoot), "Installer handoff must protect the active source from queue cleanup.");
            releaseActive.Set();
            await AssertOwnerIdleAsync(owner);
            Assert.IsTrue(Directory.Exists(activeRoot));
        }
        finally
        {
            releaseActive.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// model の OK 通知を UI に渡し、表示待ち・表示失敗で queue の終端を止めません。
    /// </summary>
    [TestMethod]
    public async Task OperationDialogs_AreDispatchedWithoutBlockingQueueOrChangingMutationResult()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
                new DelegatePackageInstallMutationPort((_, _, _, _, _) =>
                {
                    bufferedDialogs.Show("notice marker", "caption marker", UiDialogButton.OK,
                        UiDialogIcon.Information, UiDialogDefaultResult.OK);
                    return [new ChartPackage()];
                }),
                action =>
                {
                    lock (notifications) notifications.Enqueue(action);
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
            if (owner != null) await owner.WaitForIdleAsync();
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

    private static async Task AssertOwnerIdleAsync(PackageInstallWorkflowOwner owner)
    {
        await owner.WaitForIdleAsync();
    }

    private static PackageInstallWorkflowOwner CreateOwner(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> installBatch,
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
        IPackageInstallProgressMutationPort,
        IDisposable
    {
        private readonly int progressCount;

        private readonly ManualResetEventSlim release = new(false);

        private IPackageInstallProgressWriter progressWriter = null!;

        private Thread? workerThread;

        internal BoundedProgressPackageInstallMutationPort(int progressCount)
        {
            this.progressCount = progressCount;
        }

        internal ManualResetEventSlim Started { get; } = new(false);

        internal ManualResetEventSlim Returned { get; } = new(false);

        internal Thread? WorkerThread => Volatile.Read(ref workerThread);

        internal bool MutationIsBlocked => Started.IsSet && !release.IsSet;

        public IReadOnlyList<ChartPackage> Install(
            BMSLibrary library,
            IEnumerable<string> installPaths,
            CancellationToken token,
            Action onEachPathProcessed,
            Action<string, int, int> onEachArchiveExtractStarted)
        {
            throw new AssertFailedException("The writer-only package route was not selected.");
        }

        public PackageInstallCommandResult InstallWithProgress(
            BMSLibrary library,
            IEnumerable<string> installPaths,
            CancellationToken token,
            IPackageInstallProgressWriter progressWriter)
        {
            Volatile.Write(ref workerThread, Thread.CurrentThread);
            this.progressWriter = progressWriter ?? throw new ArgumentNullException(nameof(progressWriter));
            for (int index = 1; index <= progressCount; index++)
            {
                progressWriter.TryWrite(PackageInstallProgressUpdate.ArchiveExtractStarted(
                    "source-" + index + ".zip",
                    index,
                    progressCount));
                progressWriter.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
            }
            Started.Set();
            release.Wait();
            Returned.Set();
            return new PackageInstallCommandResult([new ChartPackage()], null);
        }

        internal void EmitLateProgress()
        {
            Volatile.Read(ref progressWriter)?.TryWrite(
                PackageInstallProgressUpdate.SourceProcessed());
        }

        internal void Release() => release.Set();

        public void Dispose()
        {
            Returned.Dispose();
            Started.Dispose();
            release.Dispose();
        }
    }

    private sealed class TwoBatchProgressPackageInstallMutationPort :
        IPackageInstallMutationPort,
        IPackageInstallProgressMutationPort,
        IDisposable
    {
        private readonly int progressCount;

        private readonly ManualResetEventSlim firstRelease = new(false);

        private readonly ManualResetEventSlim secondRelease = new(false);

        private IPackageInstallProgressWriter firstProgressWriter = null!;

        private Thread? workerThread;

        private int invocationCount;

        internal TwoBatchProgressPackageInstallMutationPort(int progressCount)
        {
            this.progressCount = progressCount;
        }

        internal ManualResetEventSlim FirstStarted { get; } = new(false);

        internal ManualResetEventSlim FirstReturned { get; } = new(false);

        internal ManualResetEventSlim SecondStarted { get; } = new(false);

        internal ManualResetEventSlim SecondReturned { get; } = new(false);

        internal Thread? WorkerThread => Volatile.Read(ref workerThread);

        public IReadOnlyList<ChartPackage> Install(
            BMSLibrary library,
            IEnumerable<string> installPaths,
            CancellationToken token,
            Action onEachPathProcessed,
            Action<string, int, int> onEachArchiveExtractStarted)
        {
            throw new AssertFailedException("The writer-only package route was not selected.");
        }

        public PackageInstallCommandResult InstallWithProgress(
            BMSLibrary library,
            IEnumerable<string> installPaths,
            CancellationToken token,
            IPackageInstallProgressWriter progressWriter)
        {
            Volatile.Write(ref workerThread, Thread.CurrentThread);
            int invocation = Interlocked.Increment(ref invocationCount);
            if (invocation == 1)
            {
                firstProgressWriter = progressWriter ?? throw new ArgumentNullException(nameof(progressWriter));
                for (int index = 1; index <= progressCount; index++)
                {
                    progressWriter.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
                }
                FirstStarted.Set();
                firstRelease.Wait();
                FirstReturned.Set();
            }
            else
            {
                if (progressWriter is null)
                {
                    throw new ArgumentNullException(nameof(progressWriter));
                }
                SecondStarted.Set();
                secondRelease.Wait();
                SecondReturned.Set();
            }
            return new PackageInstallCommandResult([new ChartPackage()], null);
        }

        internal void EmitLateProgressFromFirstBatch()
        {
            Volatile.Read(ref firstProgressWriter)?.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
        }

        internal void ReleaseFirst() => firstRelease.Set();

        internal void ReleaseSecond() => secondRelease.Set();

        public void Dispose()
        {
            SecondReturned.Dispose();
            SecondStarted.Dispose();
            FirstReturned.Dispose();
            FirstStarted.Dispose();
            secondRelease.Dispose();
            firstRelease.Dispose();
        }
    }

    private sealed class QueuedPackageInstallUiScheduler : IUiScheduler
    {
        private readonly object syncRoot = new();

        private readonly Queue<ScheduledOperation> pending = new();

        private TaskCompletionSource<ScheduledOperation>? nextScheduled;

        private TaskCompletionSource<bool>? pendingCountWaiter;

        private int pendingCountThreshold;

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
            TaskCompletionSource<bool>? pendingWaiter;
            lock (syncRoot)
            {
                pending.Enqueue(operation);
                waiter = nextScheduled;
                nextScheduled = null;
                pendingWaiter = pending.Count >= pendingCountThreshold
                    ? pendingCountWaiter
                    : null;
                if (pendingWaiter != null)
                {
                    pendingCountWaiter = null;
                    pendingCountThreshold = 0;
                }
            }
            waiter?.TrySetResult(operation);
            pendingWaiter?.TrySetResult(true);
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

        internal Task WaitForPendingCountAsync(int count)
        {
            lock (syncRoot)
            {
                if (pending.Count >= count)
                {
                    return Task.CompletedTask;
                }
                pendingCountThreshold = count;
                pendingCountWaiter = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return pendingCountWaiter.Task;
            }
        }

        internal ScheduledOperation PeekNext()
        {
            lock (syncRoot)
            {
                Assert.IsTrue(pending.Count > 0);
                return pending.Peek();
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
    private readonly Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> install;

    internal DelegatePackageInstallMutationPort(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> install)
    {
        this.install = install ?? throw new ArgumentNullException(nameof(install));
    }

    public IReadOnlyList<ChartPackage> Install(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted)
    {
        return install(library, installPaths, token, onEachPathProcessed, onEachArchiveExtractStarted);
    }
}

internal sealed class DelegatePackageInstallTerminalMutationPort :
    IPackageInstallMutationPort,
    IPackageInstallTerminalMutationPort
{
    private readonly Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, PackageInstallCommandResult> installWithResult;

    internal DelegatePackageInstallTerminalMutationPort(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, PackageInstallCommandResult> installWithResult)
    {
        this.installWithResult = installWithResult ?? throw new ArgumentNullException(nameof(installWithResult));
    }

    public IReadOnlyList<ChartPackage> Install(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted)
    {
        throw new AssertFailedException("The terminal package route was not selected.");
    }

    public PackageInstallCommandResult InstallWithResult(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted)
    {
        return installWithResult(
            library,
            installPaths,
            token,
            onEachPathProcessed,
            onEachArchiveExtractStarted);
    }
}
