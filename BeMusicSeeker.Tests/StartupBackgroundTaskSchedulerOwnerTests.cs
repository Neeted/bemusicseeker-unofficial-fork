using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupBackgroundTaskSchedulerOwnerTests
{
    [TestMethod]
    public async Task QueueBeforeStartWaitsUntilSchedulerStarts()
    {
        var entered = new ManualResetEventSlim();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        Assert.IsTrue(owner.Queue("playlist_library_index_prewarm", "startup", null, async () =>
        {
            entered.Set();
            await release.Task.ConfigureAwait(false);
        }));
        Assert.IsFalse(entered.IsSet);

        owner.Start();
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        release.SetResult(true);
        await WaitForIdleAsync(owner);
    }

    [TestMethod]
    public async Task DependencyAndPriorityOrderingArePreserved()
    {
        var order = new ConcurrentQueue<string>();
        var dependencyRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("maintenance_hydration", "startup", null, async () =>
        {
            order.Enqueue("maintenance");
            await dependencyRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("installable_maintenance", "startup", "maintenance_hydration", () =>
        {
            order.Enqueue("installable");
            return Task.CompletedTask;
        });
        owner.Start();

        await Task.Delay(100);
        CollectionAssert.AreEqual(new[] { "maintenance" }, order.ToArray());
        dependencyRelease.SetResult(true);
        await WaitForIdleAsync(owner);

        CollectionAssert.AreEqual(new[] { "maintenance", "installable" }, order.ToArray());
    }

    [TestMethod]
    public async Task HigherPriorityRequestStartsBeforeEarlierLowerPriorityRequestInSameLane()
    {
        var order = new ConcurrentQueue<string>();
        var highPriorityEntered = new ManualResetEventSlim();
        var highPriorityRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("external_playlist_sync", "low", null, async () =>
        {
            order.Enqueue("low");
            await Task.CompletedTask.ConfigureAwait(false);
        });
        owner.Queue("playlist_ref_apply", "middle", null, async () =>
        {
            order.Enqueue("middle");
            await Task.CompletedTask.ConfigureAwait(false);
        });
        owner.Queue("playlist_url_completion", "high", null, async () =>
        {
            order.Enqueue("high");
            highPriorityEntered.Set();
            await highPriorityRelease.Task.ConfigureAwait(false);
        });

        owner.Start();
        Assert.IsTrue(highPriorityEntered.Wait(TimeSpan.FromSeconds(5)));
        CollectionAssert.AreEqual(new[] { "high" }, order.ToArray());

        highPriorityRelease.SetResult(true);
        await WaitForIdleAsync(owner);
        CollectionAssert.AreEqual(new[] { "high", "middle", "low" }, order.ToArray());
    }

    [TestMethod]
    public async Task SameNameRerunBlocksDependentUntilLatestVersionCompletes()
    {
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new ManualResetEventSlim();
        var latestEntered = new ManualResetEventSlim();
        var dependentEntered = new ManualResetEventSlim();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("playlist_ref_apply", "first", null, async () =>
        {
            firstEntered.Set();
            await firstRelease.Task.ConfigureAwait(false);
        });
        owner.Start();
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        owner.Queue("playlist_ref_apply", "latest", null, async () =>
        {
            latestEntered.Set();
            await latestRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("default_after_ref", "dependent", "playlist_ref_apply", () =>
        {
            dependentEntered.Set();
            return Task.CompletedTask;
        });

        firstRelease.SetResult(true);
        Assert.IsTrue(latestEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(dependentEntered.IsSet);

        latestRelease.SetResult(true);
        Assert.IsTrue(dependentEntered.Wait(TimeSpan.FromSeconds(5)));
        await WaitForIdleAsync(owner);
    }

    [TestMethod]
    public async Task CompletionVersionDoesNotRegressWhenOlderWorkerFinishesAfterLatestWorker()
    {
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var followupBlockerRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new ManualResetEventSlim();
        var latestEntered = new ManualResetEventSlim();
        var maintenanceEntered = new ManualResetEventSlim();
        var installableEntered = new ManualResetEventSlim();
        var followupBlockerEntered = new ManualResetEventSlim();
        var dependentEntered = new ManualResetEventSlim();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("playlist_entries_hydration", "first", null, async () =>
        {
            firstEntered.Set();
            await firstRelease.Task.ConfigureAwait(false);
        });
        owner.Start();
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(5)));
        owner.Queue("playlist_entries_hydration", "latest", null, async () =>
        {
            latestEntered.Set();
            await latestRelease.Task.ConfigureAwait(false);
        });
        Assert.IsTrue(latestEntered.Wait(TimeSpan.FromSeconds(5)));

        owner.Queue("maintenance_hydration", "blocker", null, async () =>
        {
            maintenanceEntered.Set();
            await blockerRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("installable_maintenance", "blocker", null, async () =>
        {
            installableEntered.Set();
            await blockerRelease.Task.ConfigureAwait(false);
        });
        Assert.IsTrue(maintenanceEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(installableEntered.Wait(TimeSpan.FromSeconds(5)));
        owner.Queue("playlist_url_completion", "lane_blocker", null, async () =>
        {
            followupBlockerEntered.Set();
            await followupBlockerRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("default_after_entries", "dependent", "playlist_entries_hydration", () =>
        {
            dependentEntered.Set();
            return Task.CompletedTask;
        });

        latestRelease.SetResult(true);
        Assert.IsTrue(followupBlockerEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(dependentEntered.IsSet);

        firstRelease.SetResult(true);
        Assert.IsTrue(dependentEntered.Wait(TimeSpan.FromSeconds(5)));
        blockerRelease.SetResult(true);
        followupBlockerRelease.SetResult(true);
        await WaitForIdleAsync(owner);
    }

    [TestMethod]
    public async Task CoalescingRunsOnlyTheLatestRequest()
    {
        int firstRuns = 0;
        int latestRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        Assert.IsTrue(owner.Queue("playlist_ref_apply", "first", null, () =>
        {
            Interlocked.Increment(ref firstRuns);
            return Task.CompletedTask;
        }));
        Assert.IsTrue(owner.Queue("playlist_ref_apply", "latest", null, () =>
        {
            Interlocked.Increment(ref latestRuns);
            return Task.CompletedTask;
        }));

        owner.Start();
        await WaitForIdleAsync(owner);

        Assert.AreEqual(0, firstRuns);
        Assert.AreEqual(1, latestRuns);
    }

    [TestMethod]
    public async Task LaneAndTotalConcurrencyLimitsAreEnforced()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumActive = 0;
        int readHydrationActive = 0;
        int maximumReadHydrationActive = 0;
        int started = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        string[] readHydrationNames = ["chart_info_hydration", "playlist_entries_hydration"];
        for (int i = 0; i < readHydrationNames.Length; i++)
        {
            int index = i;
            owner.Queue(readHydrationNames[index], "test", null, async () =>
            {
                int currentActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, currentActive);
                int currentRead = Interlocked.Increment(ref readHydrationActive);
                UpdateMaximum(ref maximumReadHydrationActive, currentRead);
                Interlocked.Increment(ref started);
                await release.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref readHydrationActive);
                Interlocked.Decrement(ref active);
            });
        }
        owner.Queue("default_a", "test", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            Interlocked.Increment(ref started);
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref active);
        });
        owner.Queue("maintenance_hydration", "test", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            Interlocked.Increment(ref started);
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref active);
        });
        owner.Queue("installable_maintenance", "test", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            Interlocked.Increment(ref started);
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref active);
        });
        owner.Queue("default_a", "test", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            Interlocked.Increment(ref started);
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref active);
        });

        owner.Start();
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref started) == 4, TimeSpan.FromSeconds(5)));
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref maximumReadHydrationActive) == 2, TimeSpan.FromSeconds(5)));
        Assert.IsFalse(owner.IsIdle);

        owner.Queue("chart_info_hydration", "queued_read", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            int currentRead = Interlocked.Increment(ref readHydrationActive);
            UpdateMaximum(ref maximumReadHydrationActive, currentRead);
            Interlocked.Increment(ref started);
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref readHydrationActive);
            Interlocked.Decrement(ref active);
        });

        release.SetResult(true);
        await WaitForIdleAsync(owner);
        Assert.AreEqual(6, Volatile.Read(ref started));
        Assert.IsTrue(Volatile.Read(ref maximumActive) <= 4);
        Assert.IsTrue(Volatile.Read(ref maximumReadHydrationActive) <= 2);
    }

    [TestMethod]
    public async Task ResetRetainsQueuedWorkAndPreservesRunningConcurrencyAccounting()
    {
        var oldRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldEntered = new ManualResetEventSlim();
        var counters = new ConcurrencyCounters();
        int queuedRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("default_a", "old", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref counters.Active);
            UpdateMaximum(ref counters.MaximumActive, currentActive);
            oldEntered.Set();
            await oldRelease.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref counters.Active);
        });
        owner.Start();
        Assert.IsTrue(oldEntered.Wait(TimeSpan.FromSeconds(5)));

        owner.Queue("default_b", "retained", null, () =>
        {
            Interlocked.Increment(ref queuedRuns);
            return Task.CompletedTask;
        });
        owner.Reset(startImmediately: true);

        QueueGatedWork(owner, "playlist_entries_hydration", newRelease, counters);
        QueueGatedWork(owner, "chart_info_hydration", newRelease, counters);
        QueueGatedWork(owner, "maintenance_hydration", newRelease, counters);
        QueueGatedWork(owner, "installable_maintenance", newRelease, counters);
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref counters.NewStarted) >= 3, TimeSpan.FromSeconds(5)));
        await Task.Delay(100).ConfigureAwait(false);
        Assert.IsTrue(Volatile.Read(ref counters.MaximumActive) <= 4);

        oldRelease.SetResult(true);
        newRelease.SetResult(true);
        await WaitForIdleAsync(owner);
        Assert.AreEqual(4, Volatile.Read(ref counters.NewStarted));
        Assert.AreEqual(1, Volatile.Read(ref queuedRuns));
    }

    [TestMethod]
    public async Task ResetPreservesCompletedDependencyForRetainedQueuedWork()
    {
        var followupRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyCompleted = new ManualResetEventSlim();
        var followupEntered = new ManualResetEventSlim();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("playlist_url_completion", "lane_blocker", null, async () =>
        {
            await followupRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("playlist_entries_hydration", "dependency", null, () =>
        {
            dependencyCompleted.Set();
            return Task.CompletedTask;
        });
        owner.Queue("external_playlist_sync", "retained", "playlist_entries_hydration", async () =>
        {
            followupEntered.Set();
            await Task.CompletedTask.ConfigureAwait(false);
        });

        owner.Start();
        Assert.IsTrue(dependencyCompleted.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(SpinWait.SpinUntil(
            () =>
            {
                string summary = owner.BuildSummaryLog(0L);
                return summary.Contains("playlist_entries_hydration{") && summary.Contains("completed=1");
            },
            TimeSpan.FromSeconds(5)));
        Assert.IsFalse(followupEntered.IsSet);

        owner.Reset(startImmediately: true);
        Assert.IsFalse(followupEntered.IsSet);
        followupRelease.SetResult(true);
        Assert.IsTrue(followupEntered.Wait(TimeSpan.FromSeconds(5)));
        await WaitForIdleAsync(owner);
    }

    [TestMethod]
    public async Task FailureCompletesDependencyAndRecordsSummary()
    {
        var dependentRan = new ManualResetEventSlim();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("maintenance_hydration", "test", null, () =>
            Task.FromException(new InvalidOperationException("expected failure")));
        owner.Queue("installable_maintenance", "test", "maintenance_hydration", () =>
            Task.Run(() => dependentRan.Set()));

        owner.Start();
        Assert.IsTrue(dependentRan.Wait(TimeSpan.FromSeconds(5)));
        await WaitForIdleAsync(owner);

        string summary = owner.BuildSummaryLog(12L);
        StringAssert.Contains(summary, "failed=1");
        StringAssert.Contains(summary, "installable_maintenance");
    }

    [TestMethod]
    public async Task ShutdownDrainsRequiredWorkAndDiscardsOtherWorkOutsideLock()
    {
        bool shutdownRequested = false;
        var requiredEntered = new ManualResetEventSlim();
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new ManualResetEventSlim();
        bool discardedProbeSucceeded = false;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(() => shutdownRequested);

        owner.Queue("chart_info_hydration", "shutdown", null, async () =>
        {
            requiredEntered.Set();
            await requiredRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("external_playlist_sync", "shutdown", "chart_info_hydration", () => Task.CompletedTask, reason =>
        {
            discardedProbeSucceeded = ProbeOwnerFromAnotherThread(owner);
            discarded.Set();
        });
        owner.Start();
        Assert.IsTrue(requiredEntered.Wait(TimeSpan.FromSeconds(5)));

        shutdownRequested = true;
        owner.RequestShutdown("window_close");
        Assert.IsTrue(discarded.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(discardedProbeSucceeded);
        Assert.IsFalse(owner.Queue("post_shutdown", "shutdown", null, () => Task.CompletedTask));

        requiredRelease.SetResult(true);
        await WaitForIdleAsync(owner);
        StringAssert.Contains(owner.BuildSummaryLog(1L), "external_playlist_sync");
    }

    [TestMethod]
    public async Task ShutdownDiscardMarksLatestSameNameVersionComplete()
    {
        bool shutdownRequested = false;
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new ManualResetEventSlim();
        var discarded = new ManualResetEventSlim();
        var dependentEntered = new ManualResetEventSlim();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(() => shutdownRequested);

        owner.Queue("default_a", "first", null, async () =>
        {
            firstEntered.Set();
            await firstRelease.Task.ConfigureAwait(false);
        });
        owner.Start();
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(5)));
        owner.Queue("default_a", "latest", null, () => Task.CompletedTask, reason => discarded.Set());

        shutdownRequested = true;
        owner.RequestShutdown("window_close");
        Assert.IsTrue(discarded.Wait(TimeSpan.FromSeconds(5)));
        shutdownRequested = false;
        owner.Queue("default_after_discard", "dependent", "default_a", () =>
        {
            dependentEntered.Set();
            return Task.CompletedTask;
        });
        firstRelease.SetResult(true);
        Assert.IsTrue(dependentEntered.Wait(TimeSpan.FromSeconds(5)));
        await WaitForIdleAsync(owner);
    }

    [TestMethod]
    public async Task IdleNotificationRunsAfterAccountingOutsideOwnerLock()
    {
        StartupBackgroundTaskSchedulerOwner owner = null!;
        var notified = new ManualResetEventSlim();
        bool observedIdle = false;
        bool idleProbeSucceeded = false;
        owner = CreateOwner(schedulerIdleChanged: () =>
        {
            observedIdle = owner.IsIdle;
            idleProbeSucceeded = ProbeOwnerFromAnotherThread(owner);
            notified.Set();
        });

        owner.Queue("playlist_library_index_prewarm", "test", null, () => Task.CompletedTask);
        owner.Start();

        Assert.IsTrue(notified.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(observedIdle);
        Assert.IsTrue(idleProbeSucceeded);
        await WaitForIdleAsync(owner);
    }

    private sealed class ConcurrencyCounters
    {
        internal int Active;

        internal int MaximumActive;

        internal int NewStarted;
    }

    private static void QueueGatedWork(
        StartupBackgroundTaskSchedulerOwner owner,
        string name,
        TaskCompletionSource<bool> release,
        ConcurrencyCounters counters)
    {
        owner.Queue(name, "reset", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref counters.Active);
            UpdateMaximum(ref counters.MaximumActive, currentActive);
            Interlocked.Increment(ref counters.NewStarted);
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref counters.Active);
        });
    }

    private static bool ProbeOwnerFromAnotherThread(StartupBackgroundTaskSchedulerOwner owner)
    {
        var completed = new ManualResetEventSlim();
        bool succeeded = false;
        _ = Task.Run(() =>
        {
            owner.DescribeWaitState();
            succeeded = true;
            completed.Set();
        });
        return completed.Wait(TimeSpan.FromSeconds(2)) && succeeded;
    }

    private static StartupBackgroundTaskSchedulerOwner CreateOwner(
        Func<bool>? isShutdownRequested = null,
        Action? schedulerIdleChanged = null)
    {
        return new StartupBackgroundTaskSchedulerOwner(
            isShutdownRequested ?? (() => false),
            _ => { },
            _ => { },
            _ => { },
            value => value ?? string.Empty,
            schedulerIdleChanged ?? (() => { }));
    }

    private static async Task WaitForIdleAsync(StartupBackgroundTaskSchedulerOwner owner)
    {
        for (int i = 0; i < 500 && !owner.IsIdle; i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.IsTrue(owner.IsIdle);
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            int current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}
