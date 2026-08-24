using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupBackgroundTaskSchedulerOwnerTests
{
    [TestMethod]
    public async Task QueueBeforeStartWaitsUntilSchedulerStarts()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        Assert.IsTrue(owner.Queue("playlist_library_index_prewarm", "startup", null, async () =>
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
        }));
        Assert.IsFalse(entered.Task.IsCompleted);

        owner.Start();
        await entered.Task;
        release.SetResult(true);
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task CancelQueued_DiscardsOnlyTheReservedRequestExactlyOnce()
    {
        int runs = 0;
        int discards = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation reservation = Reserve(owner, "playlist_virtual_order_prewarm");
        Assert.IsNotNull(reservation);
        Assert.IsTrue(owner.QueueReserved(
            reservation,
            "startup",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref discards)));

        Assert.IsTrue(owner.CancelQueued(reservation, "startup_reset"));
        Assert.IsFalse(owner.CancelQueued(reservation, "duplicate_reset"));
        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(0, runs);
        Assert.AreEqual(1, discards);
    }

    [TestMethod]
    public async Task CancelQueued_DiagnosticFailureStillDiscardsAndNotifies()
    {
        int discardCount = 0;
        int shutdownProbeCount = 0;
        var idleNotified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(
            isShutdownRequested: () =>
            {
                Interlocked.Increment(ref shutdownProbeCount);
                return false;
            },
            schedulerIdleChanged: (_, _) => idleNotified.TrySetResult(true),
            logInfo: message =>
            {
                if (message.Contains(" cancelled ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("cancel log failure");
                }
            },
            logWarning: _ => throw new InvalidOperationException("warning log failure"));
        StartupBackgroundTaskReservation reservation = Reserve(owner, "library_folder_tree_refresh");

        Assert.IsTrue(owner.QueueReserved(
            reservation,
            "diagnostic_failure",
            "missing_dependency",
            () => Task.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref discardCount);
                throw new InvalidOperationException("discard callback failure");
            }));
        owner.Start();
        int probeBeforeCancel = Volatile.Read(ref shutdownProbeCount);

        Assert.IsTrue(owner.CancelQueued(reservation, "diagnostic_cancel"));
        await idleNotified.Task;
        Assert.IsTrue(Volatile.Read(ref shutdownProbeCount) > probeBeforeCancel);
        Assert.AreEqual(1, Volatile.Read(ref discardCount));
        Assert.IsTrue(owner.IsFullyIdle);
    }

    [TestMethod]
    public async Task NewReservationInvalidatesOldPreSubmitAndOnlyCurrentWorkRuns()
    {
        int oldRuns = 0;
        int newRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation oldReservation = Reserve(owner, "playlist_virtual_order_prewarm");
        StartupBackgroundTaskReservation newReservation = Reserve(owner, "playlist_virtual_order_prewarm");

        Assert.IsFalse(owner.QueueReserved(
            oldReservation,
            "old",
            () =>
            {
                Interlocked.Increment(ref oldRuns);
                return Task.CompletedTask;
            }));
        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new",
            () =>
            {
                Interlocked.Increment(ref newRuns);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(0, oldRuns);
        Assert.AreEqual(1, newRuns);
    }

    [TestMethod]
    public async Task SameGenerationLowerOrEqualOwnerSequenceCannotReplaceCurrentReservation()
    {
        int runs = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        long generation = owner.CurrentGeneration;
        StartupBackgroundTaskReservation first = owner.Reserve(
            "playlist_virtual_order_prewarm",
            generation,
            ownerSequence: 100L);

        Assert.IsNotNull(first);
        Assert.IsNull(owner.Reserve("playlist_virtual_order_prewarm", generation, ownerSequence: 99L));
        Assert.IsNull(owner.Reserve("playlist_virtual_order_prewarm", generation, ownerSequence: 100L));
        Assert.IsTrue(owner.QueueReserved(
            first,
            "first",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(1, Volatile.Read(ref runs));
    }

    [TestMethod]
    public async Task HigherOwnerSequenceReplacesLowerAndOldCancelCannotAffectIt()
    {
        int oldRuns = 0;
        int newRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        long generation = owner.CurrentGeneration;
        StartupBackgroundTaskReservation oldReservation = owner.Reserve(
            "playlist_virtual_order_prewarm",
            generation,
            ownerSequence: 200L);
        StartupBackgroundTaskReservation newReservation = owner.Reserve(
            "playlist_virtual_order_prewarm",
            generation,
            ownerSequence: 201L);

        Assert.IsNotNull(oldReservation);
        Assert.IsNotNull(newReservation);
        Assert.IsFalse(owner.QueueReserved(
            oldReservation,
            "old",
            () =>
            {
                Interlocked.Increment(ref oldRuns);
                return Task.CompletedTask;
            }));
        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new",
            () =>
            {
                Interlocked.Increment(ref newRuns);
                return Task.CompletedTask;
            }));
        Assert.IsFalse(owner.CancelQueued(oldReservation, "old_cancel"));

        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(0, Volatile.Read(ref oldRuns));
        Assert.AreEqual(1, Volatile.Read(ref newRuns));
    }

    [TestMethod]
    public async Task ResetIsolatesOwnerSequenceOrderingByGeneration()
    {
        int runs = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        long oldGeneration = owner.CurrentGeneration;
        StartupBackgroundTaskReservation oldReservation = owner.Reserve(
            "playlist_virtual_order_prewarm",
            oldGeneration,
            ownerSequence: 500L);

        owner.Reset(startImmediately: false);
        long newGeneration = owner.CurrentGeneration;
        StartupBackgroundTaskReservation newReservation = owner.Reserve(
            "playlist_virtual_order_prewarm",
            newGeneration,
            ownerSequence: 1L);

        Assert.IsNotNull(newReservation);
        Assert.IsFalse(owner.QueueReserved(
            oldReservation,
            "old_generation",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            }));
        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new_generation",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(1, Volatile.Read(ref runs));
    }

    [TestMethod]
    public async Task OldLateSubmitCannotReplaceNewSameNameQueue()
    {
        int oldRuns = 0;
        int newRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation oldReservation = Reserve(owner, "playlist_virtual_order_prewarm");
        StartupBackgroundTaskReservation newReservation = Reserve(owner, "playlist_virtual_order_prewarm");

        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new",
            () =>
            {
                Interlocked.Increment(ref newRuns);
                return Task.CompletedTask;
            }));
        Assert.IsFalse(owner.QueueReserved(
            oldReservation,
            "old_late",
            () =>
            {
                Interlocked.Increment(ref oldRuns);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(0, oldRuns);
        Assert.AreEqual(1, newRuns);
    }

    [TestMethod]
    public async Task OldReservationCancelCannotRemoveNewSameNameQueue()
    {
        int newRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation oldReservation = Reserve(owner, "playlist_virtual_order_prewarm");
        StartupBackgroundTaskReservation newReservation = Reserve(owner, "playlist_virtual_order_prewarm");

        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new",
            () =>
            {
                Interlocked.Increment(ref newRuns);
                return Task.CompletedTask;
            }));
        Assert.IsFalse(owner.CancelQueued(oldReservation, "old_reset"));

        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(1, newRuns);
    }

    [TestMethod]
    public async Task SameReservationReplayBeforeStartIsRejectedAndFirstDiscardIsRetained()
    {
        int firstRuns = 0;
        int firstDiscards = 0;
        int replayDiscards = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation reservation = Reserve(owner, "playlist_virtual_order_prewarm");

        Assert.IsTrue(owner.QueueReserved(
            reservation,
            "first_reason",
            "first_dependency",
            () =>
            {
                Interlocked.Increment(ref firstRuns);
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref firstDiscards)));
        Assert.IsFalse(owner.QueueReserved(
            reservation,
            "replay_reason",
            "replay_dependency",
            () =>
            {
                Interlocked.Increment(ref firstRuns);
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref replayDiscards)));

        Assert.IsTrue(owner.CancelQueued(reservation, "test_cancel"));
        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(0, Volatile.Read(ref firstRuns));
        Assert.AreEqual(1, Volatile.Read(ref firstDiscards));
        Assert.AreEqual(0, Volatile.Read(ref replayDiscards));
        StringAssert.Contains(owner.BuildSummaryLog(0L), "playlist_virtual_order_prewarm{queued=1,");
    }

    [TestMethod]
    public async Task ResetRetainsQueuedReservationButRejectsOldUnsubmittedReservation()
    {
        int retainedRuns = 0;
        int retainedDiscards = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation retainedReservation = Reserve(owner, "playlist_virtual_order_prewarm");
        StartupBackgroundTaskReservation preSubmitReservation = Reserve(owner, "library_folder_tree_refresh");

        Assert.IsTrue(owner.QueueReserved(
            retainedReservation,
            "retained",
            () =>
            {
                Interlocked.Increment(ref retainedRuns);
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref retainedDiscards)));
        long staleGeneration = owner.CurrentGeneration;
        owner.Reset(startImmediately: false);

        Assert.IsTrue(owner.CancelQueued(retainedReservation, "reset_cancel"));
        Assert.IsFalse(owner.CancelQueued(retainedReservation, "duplicate_cancel"));
        Assert.IsNull(Reserve(owner, "stale_generation", staleGeneration));
        Assert.IsFalse(owner.QueueReserved(
            preSubmitReservation,
            "stale",
            () =>
            {
                Interlocked.Increment(ref retainedRuns);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(0, retainedRuns);
        Assert.AreEqual(1, retainedDiscards);
    }

    [TestMethod]
    public async Task DequeuedReservationCannotBeSubmittedAgainAfterRunningStarts()
    {
        int runs = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation reservation = Reserve(owner, "playlist_virtual_order_prewarm");

        Assert.IsTrue(owner.QueueReserved(
            reservation,
            "first",
            async () =>
            {
                Interlocked.Increment(ref runs);
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }));

        owner.Start();
        await entered.Task;
        Assert.IsFalse(owner.QueueReserved(
            reservation,
            "same_receipt_after_dequeue",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            }));

        release.SetResult(true);
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(1, Volatile.Read(ref runs));
    }

    [TestMethod]
    public async Task DequeueKeepsNewerPreSubmitReservationCurrent()
    {
        int oldRuns = 0;
        int newRuns = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation oldReservation = Reserve(owner, "playlist_virtual_order_prewarm");

        Assert.IsTrue(owner.QueueReserved(
            oldReservation,
            "old",
            async () =>
            {
                Interlocked.Increment(ref oldRuns);
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }));
        StartupBackgroundTaskReservation newReservation = Reserve(owner, "playlist_virtual_order_prewarm");

        owner.Start();
        await entered.Task;
        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new",
            () =>
            {
                Interlocked.Increment(ref newRuns);
                return Task.CompletedTask;
            }));

        release.SetResult(true);
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(1, Volatile.Read(ref oldRuns));
        Assert.AreEqual(1, Volatile.Read(ref newRuns));
    }

    [TestMethod]
    public async Task LibraryFolderTreeRefreshUsesPostInitializationOwnerLane()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        Assert.IsTrue(owner.Queue("library_folder_tree_refresh", "deferred", null, async () =>
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
        }));
        owner.Start();

        await entered.Task;
        Assert.IsTrue(owner.IsIdle);
        Assert.IsFalse(owner.IsFullyIdle);
        release.SetResult(true);
        await WaitForFullyIdleAsync(owner);

        StringAssert.Contains(
            owner.BuildSummaryLog(0L),
            "library_folder_tree_refresh{queued=1,started=1,completed=1,failed=0,lastStatus=done,lastMs=");
        StringAssert.Contains(
            owner.BuildSummaryLog(0L),
            "lane=post_initialization_folder_tree_refresh");
    }

    [TestMethod]
    public async Task PostInitializationWorkDoesNotBlockRequiredIdleOrRequiredWorker()
    {
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("playlist_library_index_prewarm", "post", null, async () =>
        {
            postEntered.TrySetResult(true);
            await postRelease.Task.ConfigureAwait(false);
        });
        owner.Start();

        await postEntered.Task;
        Assert.IsTrue(owner.IsIdle);
        Assert.IsFalse(owner.IsFullyIdle);

        owner.Queue("lr2_song_db_sync", "required", null, () =>
        {
            requiredEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        await requiredEntered.Task;
        await WaitUntilAsync(owner, () => owner.IsIdle);
        Assert.IsFalse(owner.IsFullyIdle);

        postRelease.SetResult(true);
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task PostInitializationGarbageCollectionWaitsForRequiredWorkToBecomeIdle()
    {
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("chart_info_hydration", "required", null, async () =>
        {
            requiredEntered.TrySetResult(true);
            await requiredRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("post_initialize_gc", "post", null, () =>
        {
            postEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        owner.MarkRequiredInitializationSchedulingComplete();
        owner.MarkPostInitializationSchedulingComplete();
        owner.Start();

        await requiredEntered.Task;
        Assert.IsFalse(postEntered.Task.IsCompleted);
        Assert.IsFalse(owner.IsIdle);

        requiredRelease.SetResult(true);
        await postEntered.Task;
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task PostInitializationGarbageCollectionWaitsForRequiredSchedulingClosure()
    {
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("post_initialize_gc", "post", null, () =>
        {
            postEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        owner.Start();
        owner.MarkPostInitializationSchedulingComplete();

        Assert.IsFalse(postEntered.Task.IsCompleted);

        owner.Queue("lr2_song_db_sync", "required", null, async () =>
        {
            requiredEntered.TrySetResult(true);
            await requiredRelease.Task.ConfigureAwait(false);
        });
        Assert.IsFalse(postEntered.Task.IsCompleted);

        owner.MarkRequiredInitializationSchedulingComplete();
        await requiredEntered.Task;
        Assert.IsFalse(postEntered.Task.IsCompleted);

        requiredRelease.SetResult(true);
        await postEntered.Task;
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task StaleRequiredSchedulingClosureCannotReleaseNewGenerationGarbageCollection()
    {
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("post_initialize_gc", "post", null, () =>
        {
            postEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        long staleGeneration = owner.CurrentGeneration;
        owner.Reset(startImmediately: true);
        owner.MarkPostInitializationSchedulingComplete();

        Assert.IsFalse(owner.MarkRequiredInitializationSchedulingComplete(staleGeneration));
        Assert.IsFalse(postEntered.Task.IsCompleted);

        Assert.IsTrue(owner.MarkRequiredInitializationSchedulingComplete(owner.CurrentGeneration));
        await postEntered.Task;
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task RunningGarbageCollectionBlocksRequiredWorkAfterGenerationReset()
    {
        var garbageCollectionEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var garbageCollectionRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("post_initialize_gc", "post", null, async () =>
        {
            garbageCollectionEntered.TrySetResult(true);
            await garbageCollectionRelease.Task.ConfigureAwait(false);
        });
        owner.MarkRequiredInitializationSchedulingComplete();
        owner.MarkPostInitializationSchedulingComplete();
        owner.Start();

        await garbageCollectionEntered.Task;

        owner.Reset(startImmediately: true);
        owner.Queue("chart_info_hydration", "required", null, () =>
        {
            requiredEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        owner.MarkRequiredInitializationSchedulingComplete();
        owner.MarkPostInitializationSchedulingComplete();

        // This negative watchdog deliberately holds the old generation's GC gate long enough to prove
        // that new required work cannot enter while the contention condition remains active.
        Assert.IsFalse(requiredEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

        garbageCollectionRelease.SetResult(true);
        await requiredEntered.Task;
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task PostInitializationCompletionRequiresSchedulingClosureAndFullIdle()
    {
        var postRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int idleNotifications = 0;
        int finalIdleNotificationArmed = 0;
        var finalIdleNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(
            schedulerIdleChanged: (_, _) =>
            {
                Interlocked.Increment(ref idleNotifications);
                if (Volatile.Read(ref finalIdleNotificationArmed) != 0)
                {
                    finalIdleNotification.TrySetResult(true);
                }
            });

        owner.Queue("playlist_library_index_prewarm", "post", null, async () =>
        {
            await postRelease.Task.ConfigureAwait(false);
        });
        owner.Start();
        owner.MarkPostInitializationSchedulingComplete();

        Assert.IsTrue(owner.IsPostInitializationSchedulingComplete);
        Assert.IsFalse(owner.IsFullyIdle);

        int notificationsBeforeFinalRelease = Volatile.Read(ref idleNotifications);
        Volatile.Write(ref finalIdleNotificationArmed, 1);
        postRelease.SetResult(true);
        await Task.WhenAll(
            WaitForFullyIdleAsync(owner),
            finalIdleNotification.Task);
        Assert.IsTrue(Volatile.Read(ref idleNotifications) > notificationsBeforeFinalRelease);
    }

    [TestMethod]
    public async Task CaptureWorkSnapshot_ReportsQueuedRunningAndIdleBacklog()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        Assert.IsTrue(owner.Queue("playlist_library_index_prewarm", "startup", null, async () =>
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
        }));

        StartupBackgroundWorkSnapshot queued = owner.CaptureWorkSnapshot();
        Assert.AreEqual(1, queued.QueuedCount);
        Assert.AreEqual(0, queued.RunningCount);
        Assert.AreEqual(1, queued.BacklogCount);

        owner.Start();
        await entered.Task;
        StartupBackgroundWorkSnapshot running = owner.CaptureWorkSnapshot();
        Assert.AreEqual(0, running.QueuedCount);
        Assert.AreEqual(1, running.RunningCount);
        Assert.AreEqual(1, running.BacklogCount);

        release.SetResult(true);
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(
            new StartupBackgroundWorkSnapshot(0, 0),
            owner.CaptureWorkSnapshot());
    }

    [TestMethod]
    public async Task DependencyAndPriorityOrderingArePreserved()
    {
        var order = new ConcurrentQueue<string>();
        var dependencyRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("maintenance_hydration", "startup", null, async () =>
        {
            order.Enqueue("maintenance");
            maintenanceEntered.TrySetResult(true);
            await dependencyRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("installable_maintenance", "startup", "maintenance_hydration", () =>
        {
            order.Enqueue("installable");
            return Task.CompletedTask;
        });
        owner.Start();

        await maintenanceEntered.Task;
        CollectionAssert.AreEqual(new[] { "maintenance" }, order.ToArray());
        dependencyRelease.SetResult(true);
        await WaitForFullyIdleAsync(owner);

        CollectionAssert.AreEqual(new[] { "maintenance", "installable" }, order.ToArray());
    }

    [TestMethod]
    public async Task HigherPriorityRequestStartsBeforeEarlierLowerPriorityRequestInSameLane()
    {
        var order = new ConcurrentQueue<string>();
        var highPriorityEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            highPriorityEntered.TrySetResult(true);
            await highPriorityRelease.Task.ConfigureAwait(false);
        });

        owner.Start();
        await highPriorityEntered.Task;
        CollectionAssert.AreEqual(new[] { "high" }, order.ToArray());

        highPriorityRelease.SetResult(true);
        await WaitForFullyIdleAsync(owner);
        CollectionAssert.AreEqual(new[] { "high", "middle", "low" }, order.ToArray());
    }

    [TestMethod]
    public async Task SameNameRerunBlocksDependentUntilLatestVersionCompletes()
    {
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("playlist_ref_apply", "first", null, async () =>
        {
            firstEntered.TrySetResult(true);
            await firstRelease.Task.ConfigureAwait(false);
        });
        owner.Start();
        await firstEntered.Task;

        owner.Queue("playlist_ref_apply", "latest", null, async () =>
        {
            latestEntered.TrySetResult(true);
            await latestRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("default_after_ref", "dependent", "playlist_ref_apply", () =>
        {
            dependentEntered.TrySetResult(true);
            return Task.CompletedTask;
        });

        firstRelease.SetResult(true);
        await latestEntered.Task;
        Assert.IsFalse(dependentEntered.Task.IsCompleted);

        latestRelease.SetResult(true);
        await dependentEntered.Task;
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task CompletionVersionDoesNotRegressWhenOlderWorkerFinishesAfterLatestWorker()
    {
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentAfterLatestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentAfterOlderEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        try
        {
            owner.Queue("playlist_entries_hydration", "first", null, async () =>
            {
                firstEntered.TrySetResult(true);
                await firstRelease.Task.ConfigureAwait(false);
            });
            owner.Start();
            await firstEntered.Task;
            owner.Queue("playlist_entries_hydration", "latest", null, async () =>
            {
                latestEntered.TrySetResult(true);
                await latestRelease.Task.ConfigureAwait(false);
            });
            await latestEntered.Task;

            owner.Queue("default_after_latest", "dependent", "playlist_entries_hydration", () =>
            {
                dependentAfterLatestEntered.TrySetResult(true);
                return Task.CompletedTask;
            });

            latestRelease.SetResult(true);
            await dependentAfterLatestEntered.Task;

            firstRelease.SetResult(true);
            await WaitForFullyIdleAsync(owner);

            owner.Queue("default_after_older", "dependent", "playlist_entries_hydration", () =>
            {
                dependentAfterOlderEntered.TrySetResult(true);
                return Task.CompletedTask;
            });
            await dependentAfterOlderEntered.Task;
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            latestRelease.TrySetResult(true);
            firstRelease.TrySetResult(true);
        }
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
        await WaitForFullyIdleAsync(owner);

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
        var threeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoReadHydrationsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        void RecordStart(bool isReadHydration)
        {
            int currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            if (isReadHydration)
            {
                int currentRead = Interlocked.Increment(ref readHydrationActive);
                UpdateMaximum(ref maximumReadHydrationActive, currentRead);
                if (currentRead == 2)
                {
                    twoReadHydrationsStarted.TrySetResult(true);
                }
            }
            int currentStarted = Interlocked.Increment(ref started);
            if (currentStarted == 3)
            {
                threeStarted.TrySetResult(true);
            }
            if (currentStarted == 4)
            {
                fourStarted.TrySetResult(true);
            }
        }

        void QueueGatedWork(string name, bool isReadHydration)
        {
            owner.Queue(name, "test", null, async () =>
            {
                RecordStart(isReadHydration);
                await release.Task.ConfigureAwait(false);
                if (isReadHydration)
                {
                    Interlocked.Decrement(ref readHydrationActive);
                }
                Interlocked.Decrement(ref active);
            });
        }

        try
        {
            QueueGatedWork("chart_info_hydration", isReadHydration: true);
            QueueGatedWork("playlist_entries_hydration", isReadHydration: true);
            QueueGatedWork("default_a", isReadHydration: false);
            owner.Start();

            await Task.WhenAll(threeStarted.Task, twoReadHydrationsStarted.Task);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(0, 3), owner.CaptureWorkSnapshot());

            QueueGatedWork("chart_info_hydration", isReadHydration: true);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(1, 3), owner.CaptureWorkSnapshot());

            QueueGatedWork("maintenance_hydration", isReadHydration: false);
            await fourStarted.Task;
            QueueGatedWork("installable_maintenance", isReadHydration: false);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(2, 4), owner.CaptureWorkSnapshot());

            release.TrySetResult(true);
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(6, Volatile.Read(ref started));
            Assert.IsTrue(Volatile.Read(ref maximumActive) <= 4);
            Assert.IsTrue(Volatile.Read(ref maximumReadHydrationActive) <= 2);
        }
        finally
        {
            release.TrySetResult(true);
            try
            {
                await WaitForFullyIdleAsync(owner);
            }
            catch
            {
                // Cleanup releases all gated workers without replacing the test's primary failure.
            }
        }
    }

    [TestMethod]
    public async Task ResetRetainsQueuedWorkAndPreservesRunningConcurrencyAccounting()
    {
        var oldRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var counters = new ConcurrencyCounters();
        int queuedRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("default_a", "old", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref counters.Active);
            UpdateMaximum(ref counters.MaximumActive, currentActive);
            oldEntered.TrySetResult(true);
            await oldRelease.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref counters.Active);
        });
        owner.Start();
        await oldEntered.Task;

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
        await counters.ThreeNewStarted.Task;
        Assert.AreEqual(3, Volatile.Read(ref counters.NewStarted));
        Assert.AreEqual(4, Volatile.Read(ref counters.Active));
        Assert.AreEqual(4, Volatile.Read(ref counters.MaximumActive));
        Assert.AreEqual(new StartupBackgroundWorkSnapshot(2, 4), owner.CaptureWorkSnapshot());

        oldRelease.SetResult(true);
        newRelease.SetResult(true);
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(4, Volatile.Read(ref counters.NewStarted));
        Assert.AreEqual(1, Volatile.Read(ref queuedRuns));
    }

    [TestMethod]
    public async Task ResetPreservesCompletedDependencyForRetainedQueuedWork()
    {
        var followupRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var followupEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("playlist_url_completion", "lane_blocker", null, async () =>
        {
            await followupRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("playlist_entries_hydration", "dependency", null, () =>
        {
            dependencyCompleted.TrySetResult(true);
            return Task.CompletedTask;
        });
        owner.Queue("external_playlist_sync", "retained", "playlist_entries_hydration", async () =>
        {
            followupEntered.TrySetResult(true);
            await Task.CompletedTask.ConfigureAwait(false);
        });

        owner.Start();
        await dependencyCompleted.Task;
        await WaitUntilAsync(owner, () =>
        {
            string summary = owner.BuildSummaryLog(0L);
            return summary.Contains("playlist_entries_hydration{") && summary.Contains("completed=1");
        });
        Assert.IsFalse(followupEntered.Task.IsCompleted);

        owner.Reset(startImmediately: true);
        Assert.IsFalse(followupEntered.Task.IsCompleted);
        followupRelease.SetResult(true);
        await followupEntered.Task;
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task FailureCompletesDependencyAndRecordsSummary()
    {
        var dependentRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("maintenance_hydration", "test", null, () =>
            Task.FromException(new InvalidOperationException("expected failure")));
        owner.Queue("installable_maintenance", "test", "maintenance_hydration", () =>
        {
            dependentRan.TrySetResult(true);
            return Task.CompletedTask;
        });

        owner.Start();
        await dependentRan.Task;
        await WaitForFullyIdleAsync(owner);

        string summary = owner.BuildSummaryLog(12L);
        StringAssert.Contains(summary, "failed=1");
        StringAssert.Contains(summary, "installable_maintenance");
    }

    [TestMethod]
    public async Task ShutdownDrainsRequiredWorkAndDiscardsOtherWorkOutsideLock()
    {
        bool shutdownRequested = false;
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool discardedProbeSucceeded = false;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(() => shutdownRequested);

        owner.Queue("chart_info_hydration", "shutdown", null, async () =>
        {
            requiredEntered.TrySetResult(true);
            await requiredRelease.Task.ConfigureAwait(false);
        });
        owner.Queue("external_playlist_sync", "shutdown", "chart_info_hydration", () => Task.CompletedTask, reason =>
        {
            // The discard callback is synchronous, so keep it active until the probe acquires the owner locks.
            discardedProbeSucceeded = ProbeOwnerFromAnotherThreadAsync(owner).GetAwaiter().GetResult();
            discarded.TrySetResult(true);
        });
        owner.Start();
        await requiredEntered.Task;

        shutdownRequested = true;
        owner.RequestShutdown("window_close");
        await discarded.Task;
        Assert.IsTrue(discardedProbeSucceeded);
        Assert.IsFalse(owner.Queue("post_shutdown", "shutdown", null, () => Task.CompletedTask));

        requiredRelease.SetResult(true);
        await WaitForFullyIdleAsync(owner);
        StringAssert.Contains(owner.BuildSummaryLog(1L), "external_playlist_sync");
    }

    [TestMethod]
    public async Task ShutdownDiscardMarksLatestSameNameVersionComplete()
    {
        bool shutdownRequested = false;
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(() => shutdownRequested);

        owner.Queue("default_a", "first", null, async () =>
        {
            firstEntered.TrySetResult(true);
            await firstRelease.Task.ConfigureAwait(false);
        });
        owner.Start();
        await firstEntered.Task;
        owner.Queue("default_a", "latest", null, () => Task.CompletedTask, reason => discarded.TrySetResult(true));

        shutdownRequested = true;
        owner.RequestShutdown("window_close");
        await discarded.Task;
        shutdownRequested = false;
        owner.Reset(startImmediately: true);
        owner.Queue("default_after_discard", "dependent", "default_a", () =>
        {
            dependentEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        firstRelease.SetResult(true);
        await dependentEntered.Task;
        await WaitForFullyIdleAsync(owner);
    }

    [TestMethod]
    public async Task IdleNotificationRunsAfterAccountingOutsideOwnerLock()
    {
        StartupBackgroundTaskSchedulerOwner owner = null!;
        var notified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool observedIdle = false;
        bool idleProbeSucceeded = false;
        owner = CreateOwner(schedulerIdleChanged: (_, _) =>
        {
            observedIdle = owner.IsIdle;
            // The notification callback is synchronous, so keep it active until the probe acquires the owner locks.
            idleProbeSucceeded = ProbeOwnerFromAnotherThreadAsync(owner).GetAwaiter().GetResult();
            notified.TrySetResult(true);
        });

        owner.Queue("playlist_library_index_prewarm", "test", null, () => Task.CompletedTask);
        owner.Start();

        await notified.Task;
        Assert.IsTrue(observedIdle);
        Assert.IsTrue(idleProbeSucceeded);
        await WaitForFullyIdleAsync(owner);
    }

    private sealed class ConcurrencyCounters
    {
        internal int Active;

        internal int MaximumActive;

        internal int NewStarted;

        internal TaskCompletionSource<bool> ThreeNewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (Interlocked.Increment(ref counters.NewStarted) == 3)
            {
                counters.ThreeNewStarted.TrySetResult(true);
            }
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref counters.Active);
        });
    }

    private static async Task<bool> ProbeOwnerFromAnotherThreadAsync(StartupBackgroundTaskSchedulerOwner owner)
    {
        try
        {
            await Task.Run(owner.DescribeWaitState)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static long nextOwnerReservationSequence;

    private static StartupBackgroundTaskReservation Reserve(
        StartupBackgroundTaskSchedulerOwner owner,
        string name)
    {
        return owner.Reserve(
            name,
            owner.CurrentGeneration,
            Interlocked.Increment(ref nextOwnerReservationSequence));
    }

    private static StartupBackgroundTaskReservation Reserve(
        StartupBackgroundTaskSchedulerOwner owner,
        string name,
        long expectedGeneration)
    {
        return owner.Reserve(
            name,
            expectedGeneration,
            Interlocked.Increment(ref nextOwnerReservationSequence));
    }

    private static StartupBackgroundTaskSchedulerOwner CreateOwner(
        Func<bool>? isShutdownRequested = null,
        Action<long, long>? schedulerIdleChanged = null,
        Action<string>? logInfo = null,
        Action<string>? logWarning = null)
    {
        var notificationProbe = new SchedulerNotificationProbe();
        return new StartupBackgroundTaskSchedulerOwner(
            isShutdownRequested ?? (() => false),
            logInfo ?? (_ => { }),
            logWarning ?? (_ => { }),
            _ => { },
            value => value ?? string.Empty,
            (generation, revision) =>
            {
                notificationProbe.Publish(generation, revision);
                schedulerIdleChanged?.Invoke(generation, revision);
            },
            notificationProbe);
    }

    private static Task WaitForFullyIdleAsync(StartupBackgroundTaskSchedulerOwner owner)
    {
        return WaitUntilAsync(owner, () => owner.IsFullyIdle);
    }

    private static async Task WaitUntilAsync(StartupBackgroundTaskSchedulerOwner owner, Func<bool> predicate)
    {
        var notificationProbe = (SchedulerNotificationProbe)owner.ProgressSynchronization;
        while (!predicate())
        {
            Task<(long Generation, long Revision)> notification = notificationProbe.CaptureNextNotification();
            if (predicate())
            {
                return;
            }
            await notification.ConfigureAwait(false);
        }
    }

    private sealed class SchedulerNotificationProbe
    {
        private readonly object syncRoot = new();

        private TaskCompletionSource<(long Generation, long Revision)> nextNotification = CreateNotificationSource();

        internal Task<(long Generation, long Revision)> CaptureNextNotification()
        {
            lock (syncRoot)
            {
                return nextNotification.Task;
            }
        }

        internal void Publish(long generation, long revision)
        {
            TaskCompletionSource<(long Generation, long Revision)> notification;
            lock (syncRoot)
            {
                notification = nextNotification;
                nextNotification = CreateNotificationSource();
            }
            notification.TrySetResult((generation, revision));
        }

        private static TaskCompletionSource<(long Generation, long Revision)> CreateNotificationSource()
        {
            return new TaskCompletionSource<(long Generation, long Revision)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
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
