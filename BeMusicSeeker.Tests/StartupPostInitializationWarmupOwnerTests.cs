#nullable disable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupPostInitializationWarmupOwnerTests
{
    [TestMethod]
    public async Task Schedule_RunsAdjacentStagesThenVirtualAndCompletesOnce()
    {
        var harness = new Harness();
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        var request = new StartupPostInitializationWarmupRequest("complete", 17L, 4L);

        Assert.IsTrue(owner.Schedule(request));
        await harness.RunQueuedAsync();

        CollectionAssert.AreEqual(
            new[] { "real_path", "overlay", "primary", "playlist_hash", "virtual" },
            harness.StageOrder);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreSame(request, harness.Completions[0].Request);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, harness.Completions[0].Kind);
        Assert.IsTrue(harness.Lease.Completion.IsCompleted);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
    }

    [TestMethod]
    public async Task AdjacentFailure_SkipsRemainingAdjacentStagesButStillAttemptsVirtual()
    {
        var failure = new InvalidOperationException("overlay failed");
        var harness = new Harness { FailureStage = "overlay", Failure = failure };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();

        Assert.IsTrue(owner.Schedule(new StartupPostInitializationWarmupRequest("complete", 18L, 4L)));
        await harness.RunQueuedAsync();

        CollectionAssert.AreEqual(new[] { "real_path", "overlay", "virtual" }, harness.StageOrder);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Failed, harness.Completions[0].Kind);
        Assert.AreEqual("install_destination_overlay", harness.Completions[0].FailedStage);
        Assert.AreSame(failure, harness.Completions[0].Exception);
    }

    [TestMethod]
    public void SameIdentity_IsDeduplicatedBeforeASecondLeaseOrQueue()
    {
        var harness = new Harness();
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        var request = new StartupPostInitializationWarmupRequest("complete", 19L, 5L);

        Assert.IsTrue(owner.Schedule(request));
        Assert.IsFalse(owner.Schedule(new StartupPostInitializationWarmupRequest("duplicate", 19L, 5L)));

        Assert.AreEqual(1, harness.AcquireCount);
        Assert.AreEqual(1, harness.QueueCount);
    }

    [TestMethod]
    public void SchedulerRejection_CancelsLeaseAndPublishesOneRejectedReceipt()
    {
        var harness = new Harness { AcceptQueue = false };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();

        Assert.IsFalse(owner.Schedule(new StartupPostInitializationWarmupRequest("complete", 20L, 6L)));

        Assert.AreEqual(1, harness.CancelLeaseCount);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Rejected, harness.Completions[0].Kind);
        Assert.IsTrue(harness.Lease.Token.IsCancellationRequested);
        Assert.IsTrue(harness.Lease.Completion.IsCompleted);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
    }

    [TestMethod]
    public void ReservationGenerationMismatchRejectsBeforeLeaseAcquisition()
    {
        var harness = new Harness { SchedulerGeneration = 2L };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();

        Assert.IsFalse(owner.Schedule(new StartupPostInitializationWarmupRequest("stale", 201L, 1L)));

        Assert.AreEqual(0, harness.AcquireCount);
        Assert.AreEqual(0, harness.QueueCount);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Rejected, harness.Completions[0].Kind);
        Assert.AreEqual("reservation", harness.Completions[0].FailedStage);
    }

    [TestMethod]
    public async Task StaleGenerationScheduleDoesNotSupersedeCurrentQueuedWork()
    {
        var harness = new Harness { SchedulerGeneration = 0L };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        var currentRequest = new StartupPostInitializationWarmupRequest("current", 202L, 0L);
        var staleRequest = new StartupPostInitializationWarmupRequest("stale", 203L, 0L);

        Assert.IsTrue(owner.Schedule(currentRequest));
        harness.SchedulerGeneration = 1L;
        Assert.IsFalse(owner.Schedule(staleRequest));

        Assert.AreEqual(1, harness.AcquireCount);
        Assert.AreEqual(1, harness.QueueCount);
        Assert.AreEqual(0, harness.CancelLeaseCount);
        Assert.AreEqual(0, harness.CancelQueuedCount);
        Assert.AreEqual(1, harness.Completions.Count);
        StartupPostInitializationWarmupCompletion staleCompletion =
            harness.Completions.Find(completion => ReferenceEquals(completion.Request, staleRequest));
        Assert.IsNotNull(staleCompletion);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Rejected, staleCompletion.Kind);
        Assert.AreEqual("reservation", staleCompletion.FailedStage);

        await harness.RunQueuedAsync();

        Assert.AreEqual(2, harness.Completions.Count);
        StartupPostInitializationWarmupCompletion currentCompletion =
            harness.Completions.Find(completion => ReferenceEquals(completion.Request, currentRequest));
        Assert.IsNotNull(currentCompletion);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, currentCompletion.Kind);
        CollectionAssert.AreEqual(
            new[] { "real_path", "overlay", "primary", "playlist_hash", "virtual" },
            harness.StageOrder);
        Assert.AreEqual(1, harness.OwnedCancellations.Count);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
    }

    [TestMethod]
    public void Reset_CancelsQueuedLeaseAndDiscardRaceCompletesExactlyOnce()
    {
        var harness = new Harness();
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        owner.Schedule(new StartupPostInitializationWarmupRequest("complete", 21L, 7L));

        owner.Reset("new_startup");

        Assert.AreEqual(1, harness.CancelLeaseCount);
        Assert.AreEqual(1, harness.CancelQueuedCount);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Cancelled, harness.Completions[0].Kind);
        Assert.IsTrue(harness.Lease.Token.IsCancellationRequested);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
    }

    [TestMethod]
    public void SchedulerDiscard_CancelsLeaseAndPublishesOneDiscardedReceipt()
    {
        var harness = new Harness();
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        owner.Schedule(new StartupPostInitializationWarmupRequest("complete", 23L, 9L));

        harness.Discard("shutdown");
        harness.Discard("duplicate_shutdown");

        Assert.AreEqual(1, harness.CancelLeaseCount);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Discarded, harness.Completions[0].Kind);
        Assert.AreEqual("shutdown", harness.Completions[0].FailedStage);
        Assert.IsTrue(harness.Lease.Token.IsCancellationRequested);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
    }

    [TestMethod]
    public void QueueException_CancelsLeaseAndDisposesPreRunCancellationExactlyOnce()
    {
        var failure = new InvalidOperationException("queue failed");
        var harness = new Harness
        {
            QueueFailure = failure,
            ThrowOnCancelLease = true,
            ThrowOnCompletion = true,
            ThrowOnLogFailure = true,
            ThrowOnCancellationDispose = true,
            ThrowAfterCancelQueuedBoundary = true
        };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();

        Assert.IsFalse(owner.Schedule(new StartupPostInitializationWarmupRequest("complete", 27L, 12L)));

        Assert.AreEqual(1, harness.CancelLeaseCount);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Failed, harness.Completions[0].Kind);
        Assert.AreSame(failure, harness.Completions[0].Exception);
        Assert.AreEqual(1, harness.CancelQueuedCount);
        Assert.AreEqual(1, harness.DiscardCallbackCount);
        Assert.IsFalse(harness.HasQueuedWork);
        Assert.IsNotNull(harness.LastQueuedReservation);
        Assert.AreSame(harness.LastQueuedReservation, harness.LastCancelledReservation);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeFailureCount);
        Assert.IsTrue(harness.Lease.Completion.IsCompleted);
        Assert.IsTrue(harness.LogFailureCount > 0);
        Assert.AreEqual(0, harness.StageOrder.Count);
    }

    [TestMethod]
    public async Task ResetDuringRunningStage_IgnoresStaleFinallyCompletion()
    {
        var harness = new Harness();
        StartupPostInitializationWarmupOwner owner = null;
        harness.AfterStage = stage =>
        {
            if (stage == "real_path")
            {
                owner.Reset("profile_changed");
            }
        };
        owner = harness.CreateOwner();
        owner.Schedule(new StartupPostInitializationWarmupRequest("complete", 22L, 8L));

        await harness.RunQueuedAsync();

        CollectionAssert.AreEqual(new[] { "real_path" }, harness.StageOrder);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Cancelled, harness.Completions[0].Kind);
    }

    [TestMethod]
    public async Task ResetDuringLeaseAcquisition_CancelsAttachedLeaseWithoutQueueingStaleWork()
    {
        var harness = new Harness { GateLeaseAcquisition = true };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        Task<bool> staleSchedule = StartLongRunning(() => owner.Schedule(
            new StartupPostInitializationWarmupRequest("old", 24L, 10L)));
        Assert.IsTrue(harness.LeaseAcquireEntered.Wait(TimeSpan.FromSeconds(5)));

        owner.Reset("new_generation");
        Assert.AreEqual(1, harness.Completions.Count);
        harness.ReleaseLeaseAcquisition.Set();

        Assert.IsFalse(await staleSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, harness.QueueCount);
        Assert.AreEqual(1, harness.CancelLeaseCount);
        Assert.IsTrue(harness.Lease.Completion.IsCompleted);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Cancelled, harness.Completions[0].Kind);
        Assert.AreEqual(0, harness.StageOrder.Count);

        harness.GateLeaseAcquisition = false;
        Assert.IsTrue(owner.Schedule(new StartupPostInitializationWarmupRequest("new", 26L, 11L)));
        await harness.RunQueuedAsync();
        Assert.AreEqual(2, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, harness.Completions[1].Kind);
        CollectionAssert.AreEqual(
            new[] { "real_path", "overlay", "primary", "playlist_hash", "virtual" },
            harness.StageOrder);
    }

    [TestMethod]
    public async Task ResetWhileQueueBoundaryIsInFlight_RemovesLateQueueWithoutStartingStaleWork()
    {
        var harness = new Harness
        {
            GateQueue = true,
            GateFirstCancelAfterMiss = true
        };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        Task<bool> staleSchedule = StartLongRunning(() => owner.Schedule(
            new StartupPostInitializationWarmupRequest("old", 25L, 11L)));
        Assert.IsTrue(harness.QueueEntered.Wait(TimeSpan.FromSeconds(5)));

        Task reset = StartLongRunning(() => owner.Reset("new_generation"));
        Assert.IsTrue(harness.CancelMissObserved.Wait(TimeSpan.FromSeconds(5)));
        harness.ReleaseQueue.Set();

        Assert.IsFalse(await staleSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        harness.ReleaseFirstCancel.Set();
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, harness.QueueCount);
        Assert.AreEqual(2, harness.CancelQueuedCount);
        Assert.AreEqual(1, harness.CancelLeaseCount);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(0, harness.StageOrder.Count);
        Assert.IsFalse(harness.HasQueuedWork);
    }

    [TestMethod]
    public async Task ResetGenerationRejectsStaleLateSubmitAndStaleCancelLeavesNewWorkQueued()
    {
        var harness = new Harness
        {
            GateQueue = true,
            GateFirstCancelAfterMiss = true,
            SchedulerGeneration = 0L
        };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        Task<bool> staleSchedule = StartLongRunning(() => owner.Schedule(
            new StartupPostInitializationWarmupRequest("old", 31L, 0L)));
        Assert.IsTrue(harness.QueueEntered.Wait(TimeSpan.FromSeconds(5)));

        Task reset = StartLongRunning(() => owner.Reset("new_generation"));
        Assert.IsTrue(harness.CancelMissObserved.Wait(TimeSpan.FromSeconds(5)));
        harness.SchedulerGeneration = 1L;
        harness.ReleaseQueue.Set();
        Assert.IsFalse(await staleSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        harness.QueueEntered.Reset();
        harness.ReleaseQueue.Reset();

        Task<bool> newSchedule = StartLongRunning(() => owner.Schedule(
            new StartupPostInitializationWarmupRequest("new", 32L, 1L)));
        Assert.IsTrue(harness.QueueEntered.Wait(TimeSpan.FromSeconds(5)));
        harness.ReleaseFirstCancel.Set();
        await reset.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ReleaseQueue.Set();
        Assert.IsTrue(await newSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        await harness.RunQueuedAsync();

        Assert.AreEqual(2, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Cancelled, harness.Completions[0].Kind);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, harness.Completions[1].Kind);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
        Assert.AreEqual(1, harness.OwnedCancellations[1].DisposeCount);
        Assert.AreEqual(0, harness.CancelQueuedCount % 2, "Both stale cancellation attempts must remain reservation-scoped.");
    }

    [TestMethod]
    public async Task ReservationCallbackOutsideOwnerLockAllowsResetAndCleansLateReceipt()
    {
        var harness = new Harness();
        var progressLikeLock = new object();
        harness.ReserveExternalLock = progressLikeLock;
        harness.GateNextReservation = true;
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        Task<bool> staleSchedule;
        Task reset;

        lock (progressLikeLock)
        {
            staleSchedule = StartLongRunning(() => owner.Schedule(
                new StartupPostInitializationWarmupRequest("late", 33L, 0L)));
            Assert.IsTrue(harness.ReserveEntered.Wait(TimeSpan.FromSeconds(5)));

            reset = StartLongRunning(() => owner.Reset("progress_reset"));
            Assert.IsTrue(
                reset.Wait(TimeSpan.FromSeconds(5)),
                "Reset must not wait for the scheduler reservation callback while the progress lock is held.");
        }

        Assert.IsFalse(await staleSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, harness.AcquireCount);
        Assert.AreEqual(0, harness.QueueCount);
        Assert.AreEqual(1, harness.CancelQueuedCount);
        Assert.AreEqual(1, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Failed, harness.Completions[0].Kind);
        Assert.AreEqual("reservation", harness.Completions[0].FailedStage);
        Assert.AreEqual(0, harness.StageOrder.Count);
    }

    [TestMethod]
    public async Task NewerScheduleCommitsWhileOlderReservationIsBlocked()
    {
        var harness = new Harness();
        var progressLikeLock = new object();
        harness.ReserveExternalLock = progressLikeLock;
        harness.GateNextReservation = true;
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        Task<bool> oldSchedule;
        Task<bool> newSchedule;

        lock (progressLikeLock)
        {
            oldSchedule = StartLongRunning(() => owner.Schedule(
                new StartupPostInitializationWarmupRequest("old", 34L, 0L)));
            Assert.IsTrue(harness.ReserveEntered.Wait(TimeSpan.FromSeconds(5)));

            newSchedule = StartLongRunning(() => owner.Schedule(
                new StartupPostInitializationWarmupRequest("new", 35L, 0L)));
            Assert.IsTrue(
                newSchedule.Wait(TimeSpan.FromSeconds(5)),
                "A newer schedule must be able to advance the owner revision while an older reserve callback is blocked.");
            Assert.IsTrue(newSchedule.Result);
        }

        Assert.IsFalse(await oldSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, harness.QueueCount);
        Assert.AreEqual(0, harness.CancelQueuedCount);

        await harness.RunQueuedAsync();

        Assert.AreEqual(2, harness.Completions.Count);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Rejected, harness.Completions[0].Kind);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, harness.Completions[1].Kind);
        Assert.AreEqual(1, harness.OwnedCancellations.Count);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
    }

    [TestMethod]
    public async Task LowerSequenceLateReservationCannotMutateNewerCommittedWarmup()
    {
        var harness = new Harness
        {
            GateNextReservation = true,
            GateLeaseAcquisition = true,
            SchedulerGeneration = 0L
        };
        var progressLikeLock = new object();
        harness.ReserveExternalLock = progressLikeLock;
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        var oldRequest = new StartupPostInitializationWarmupRequest("old", 36L, 0L);
        var newRequest = new StartupPostInitializationWarmupRequest("new", 37L, 0L);
        Task<bool> oldSchedule;
        Task<bool> newSchedule;

        lock (progressLikeLock)
        {
            oldSchedule = StartLongRunning(() => owner.Schedule(oldRequest));
            Assert.IsTrue(harness.ReserveEntered.Wait(TimeSpan.FromSeconds(5)));

            newSchedule = StartLongRunning(() => owner.Schedule(newRequest));
            Assert.IsTrue(harness.LeaseAcquireEntered.Wait(TimeSpan.FromSeconds(5)));
        }

        Assert.IsFalse(await oldSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, harness.AcquireCount);
        Assert.AreEqual(0, harness.QueueCount);
        Assert.AreEqual(0, harness.CancelQueuedCount);

        harness.ReleaseLeaseAcquisition.Set();
        Assert.IsTrue(await newSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, harness.QueueCount);

        await harness.RunQueuedAsync();

        Assert.AreEqual(2, harness.Completions.Count);
        StartupPostInitializationWarmupCompletion oldCompletion =
            harness.Completions.Find(completion => ReferenceEquals(completion.Request, oldRequest));
        StartupPostInitializationWarmupCompletion newCompletion =
            harness.Completions.Find(completion => ReferenceEquals(completion.Request, newRequest));
        Assert.IsNotNull(oldCompletion);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Rejected, oldCompletion.Kind);
        Assert.IsNotNull(newCompletion);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, newCompletion.Kind);
        Assert.AreEqual(1, harness.OwnedCancellations.Count);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
    }

    [TestMethod]
    public async Task HigherSchedulerReservationCommitsAfterLowerOwnerSubmitIsRejected()
    {
        var harness = new Harness { GateReservationAcceptance = true };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        var oldRequest = new StartupPostInitializationWarmupRequest("old", 38L, 0L);
        var newRequest = new StartupPostInitializationWarmupRequest("new", 39L, 0L);

        Task<bool> oldSchedule = StartLongRunning(() => owner.Schedule(oldRequest));
        Assert.IsTrue(harness.ReservationOneAccepted.Wait(TimeSpan.FromSeconds(5)));

        Task<bool> newSchedule = StartLongRunning(() => owner.Schedule(newRequest));
        Assert.IsTrue(harness.ReservationTwoAccepted.Wait(TimeSpan.FromSeconds(5)));

        harness.ReleaseReservationOne.Set();
        Assert.IsFalse(await oldSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, harness.QueueCount);
        Assert.AreEqual(1, harness.CancelQueuedCount);

        harness.ReleaseReservationTwo.Set();
        Assert.IsTrue(await newSchedule.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, harness.QueueCount);
        Assert.IsTrue(harness.HasQueuedWork);

        await harness.RunQueuedAsync();

        Assert.AreEqual(2, harness.Completions.Count);
        StartupPostInitializationWarmupCompletion oldCompletion =
            harness.Completions.Find(completion => ReferenceEquals(completion.Request, oldRequest));
        StartupPostInitializationWarmupCompletion newCompletion =
            harness.Completions.Find(completion => ReferenceEquals(completion.Request, newRequest));
        Assert.IsNotNull(oldCompletion);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Rejected, oldCompletion.Kind);
        Assert.IsNotNull(newCompletion);
        Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, newCompletion.Kind);
        Assert.AreEqual(1, harness.CancelLeaseCount);
        Assert.AreEqual(2, harness.OwnedCancellations.Count);
        Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
        Assert.AreEqual(1, harness.OwnedCancellations[1].DisposeCount);
    }

    private static Task StartLongRunning(Action action)
    {
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static Task<T> StartLongRunning<T>(Func<T> action)
    {
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private sealed class Harness
    {
        private readonly object leaseSyncRoot = new();
        private readonly object reservationSyncRoot = new();
        private readonly Dictionary<RegularChartListPrewarmLease, CancellationTokenSource> leaseCancellations = [];
        private Func<Task> queuedWork;
        private Action<string> discard;
        private StartupBackgroundTaskReservation queuedReservation;
        private StartupBackgroundTaskReservation currentReservation;
        private long reservationId;
        private long latestAcceptedReservationSequence = long.MinValue;
        private long latestAcceptedReservationGeneration = long.MinValue;

        internal bool AcceptQueue { get; set; } = true;
        internal long? SchedulerGeneration { get; set; }
        internal bool GateLeaseAcquisition { get; set; }
        internal bool GateQueue { get; set; }
        internal bool GateFirstCancelAfterMiss { get; set; }
        internal bool GateNextReservation { get; set; }
        internal bool GateReservationAcceptance { get; set; }
        internal object ReserveExternalLock { get; set; }
        internal Exception QueueFailure { get; set; }
        internal bool ThrowOnCancelLease { get; set; }
        internal bool ThrowOnCompletion { get; set; }
        internal bool ThrowOnLogFailure { get; set; }
        internal bool ThrowOnCancellationDispose { get; set; }
        internal bool ThrowAfterCancelQueuedBoundary { get; set; }
        internal string FailureStage { get; set; } = string.Empty;
        internal Exception Failure { get; set; }
        internal Action<string> AfterStage { get; set; }
        internal int AcquireCount { get; private set; }
        internal int QueueCount { get; private set; }
        internal int CancelLeaseCount { get; private set; }
        internal int CancelQueuedCount { get; private set; }
        internal int DiscardCallbackCount { get; private set; }
        internal int LogFailureCount { get; private set; }
        internal StartupBackgroundTaskReservation LastQueuedReservation { get; private set; }
        internal StartupBackgroundTaskReservation LastCancelledReservation { get; private set; }
        internal RegularChartListPrewarmLease Lease { get; private set; }
        internal ManualResetEventSlim LeaseAcquireEntered { get; } = new();
        internal ManualResetEventSlim ReleaseLeaseAcquisition { get; } = new();
        internal ManualResetEventSlim QueueEntered { get; } = new();
        internal ManualResetEventSlim ReleaseQueue { get; } = new();
        internal ManualResetEventSlim ReserveEntered { get; } = new();
        internal ManualResetEventSlim CancelMissObserved { get; } = new();
        internal ManualResetEventSlim ReleaseFirstCancel { get; } = new();
        internal ManualResetEventSlim ReservationOneAccepted { get; } = new();
        internal ManualResetEventSlim ReleaseReservationOne { get; } = new();
        internal ManualResetEventSlim ReservationTwoAccepted { get; } = new();
        internal ManualResetEventSlim ReleaseReservationTwo { get; } = new();
        internal bool HasQueuedWork => queuedWork != null;
        internal List<string> StageOrder { get; } = [];
        internal List<StartupPostInitializationWarmupCompletion> Completions { get; } = [];
        internal List<RecordingCancellationTokenSource> OwnedCancellations { get; } = [];

        private int firstCancelMissGated;
        private int reserveExternalLockGated;

        internal StartupPostInitializationWarmupOwner CreateOwner()
        {
            return new StartupPostInitializationWarmupOwner(
                () =>
                {
                    AcquireCount++;
                    if (GateLeaseAcquisition)
                    {
                        LeaseAcquireEntered.Set();
                        if (!ReleaseLeaseAcquisition.Wait(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("Lease acquisition gate was not released.");
                        }
                    }
                    var cancellation = new CancellationTokenSource();
                    Lease = new RegularChartListPrewarmLease(AcquireCount, cancellation.Token, null);
                    lock (leaseSyncRoot)
                    {
                        leaseCancellations.Add(Lease, cancellation);
                    }
                    return Lease;
                },
                lease =>
                {
                    CancelLeaseCount++;
                    CancellationTokenSource cancellation;
                    lock (leaseSyncRoot)
                    {
                        cancellation = leaseCancellations[lease];
                    }
                    cancellation.Cancel();
                    lease.Dispose();
                    if (ThrowOnCancelLease)
                    {
                        throw new InvalidOperationException("cancel lease secondary failure");
                    }
                },
                (_, _, _) => RecordStage("real_path"),
                (_, _, _) => RecordStage("overlay"),
                (_, _, _) => RecordStage("primary"),
                (_, _, _) => RecordStage("playlist_hash"),
                (_, _) => RecordStage("virtual"),
                (name, expectedGeneration, ownerSequence) =>
                {
                    if (GateNextReservation
                        && Interlocked.CompareExchange(ref reserveExternalLockGated, 1, 0) == 0)
                    {
                        ReserveEntered.Set();
                        lock (ReserveExternalLock)
                        {
                        }
                    }
                    if (SchedulerGeneration.HasValue
                        && SchedulerGeneration.Value != expectedGeneration)
                    {
                        return null;
                    }
                    StartupBackgroundTaskReservation acceptedReservation;
                    lock (reservationSyncRoot)
                    {
                        if (latestAcceptedReservationGeneration != expectedGeneration)
                        {
                            latestAcceptedReservationGeneration = expectedGeneration;
                            latestAcceptedReservationSequence = long.MinValue;
                        }
                        if (ownerSequence <= latestAcceptedReservationSequence)
                        {
                            return null;
                        }
                        latestAcceptedReservationSequence = ownerSequence;
                        currentReservation = new StartupBackgroundTaskReservation(
                            name,
                            ++reservationId,
                            expectedGeneration,
                            ownerSequence);
                        acceptedReservation = currentReservation;
                    }
                    if (GateReservationAcceptance)
                    {
                        ManualResetEventSlim accepted = ownerSequence == 1L
                            ? ReservationOneAccepted
                            : ownerSequence == 2L
                                ? ReservationTwoAccepted
                                : null;
                        ManualResetEventSlim release = ownerSequence == 1L
                            ? ReleaseReservationOne
                            : ownerSequence == 2L
                                ? ReleaseReservationTwo
                                : null;
                        if (accepted != null && release != null)
                        {
                            accepted.Set();
                            if (!release.Wait(TimeSpan.FromSeconds(5)))
                            {
                                throw new TimeoutException("Reservation acceptance gate was not released.");
                            }
                        }
                    }
                    return acceptedReservation;
                },
                (reservation, reason, work, onDiscard) =>
                {
                    QueueCount++;
                    if (!ReferenceEquals(currentReservation, reservation)
                        || (SchedulerGeneration.HasValue
                            && reservation.ExpectedGeneration != SchedulerGeneration.Value))
                    {
                        return false;
                    }
                    if (GateQueue)
                    {
                        QueueEntered.Set();
                        if (!ReleaseQueue.Wait(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("Queue gate was not released.");
                        }
                    }
                    if (!AcceptQueue)
                    {
                        return false;
                    }
                    if (QueueFailure != null)
                    {
                        queuedWork = work;
                        discard = onDiscard;
                        queuedReservation = reservation;
                        LastQueuedReservation = reservation;
                        throw QueueFailure;
                    }
                    queuedWork = work;
                    discard = onDiscard;
                    queuedReservation = reservation;
                    LastQueuedReservation = reservation;
                    return true;
                },
                (reservation, reason) =>
                {
                    CancelQueuedCount++;
                    LastCancelledReservation = reservation;
                    Action<string> queuedDiscard = discard;
                    if (queuedWork == null
                        || queuedDiscard == null
                        || !ReferenceEquals(queuedReservation, reservation))
                    {
                        if (GateFirstCancelAfterMiss
                            && Interlocked.CompareExchange(ref firstCancelMissGated, 1, 0) == 0)
                        {
                            CancelMissObserved.Set();
                            if (!ReleaseFirstCancel.Wait(TimeSpan.FromSeconds(5)))
                            {
                                throw new TimeoutException("First cancel miss gate was not released.");
                            }
                        }
                        return false;
                    }
                    queuedWork = null;
                    discard = null;
                    queuedReservation = null;
                    DiscardCallbackCount++;
                    queuedDiscard(reason);
                    if (ThrowAfterCancelQueuedBoundary)
                    {
                        throw new InvalidOperationException("typed cancel secondary failure");
                    }
                    return true;
                },
                completion =>
                {
                    Completions.Add(completion);
                    if (ThrowOnCompletion)
                    {
                        throw new InvalidOperationException("completion secondary failure");
                    }
                },
                (_, _) =>
                {
                    LogFailureCount++;
                    if (ThrowOnLogFailure)
                    {
                        throw new InvalidOperationException("log secondary failure");
                    }
                },
                token =>
                {
                    var cancellation = new RecordingCancellationTokenSource(token)
                    {
                        ThrowOnDispose = ThrowOnCancellationDispose
                    };
                    OwnedCancellations.Add(cancellation);
                    return cancellation;
                });
        }

        internal Task RunQueuedAsync()
        {
            Func<Task> work = queuedWork;
            queuedWork = null;
            discard = null;
            queuedReservation = null;
            return work();
        }

        internal void Discard(string reason)
        {
            Action<string> queuedDiscard = discard;
            queuedWork = null;
            discard = null;
            queuedReservation = null;
            DiscardCallbackCount++;
            queuedDiscard?.Invoke(reason);
        }

        private void RecordStage(string stage)
        {
            StageOrder.Add(stage);
            AfterStage?.Invoke(stage);
            if (FailureStage == stage)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingCancellationTokenSource : CancellationTokenSource
    {
        private readonly CancellationTokenRegistration linkedRegistration;

        internal RecordingCancellationTokenSource(CancellationToken token)
        {
            linkedRegistration = token.Register(Cancel);
        }

        internal int DisposeCount { get; private set; }
        internal int DisposeFailureCount { get; private set; }

        internal bool ThrowOnDispose { get; set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                linkedRegistration.Dispose();
            }
            base.Dispose(disposing);
            if (disposing && ThrowOnDispose)
            {
                DisposeFailureCount++;
                throw new InvalidOperationException("cancellation dispose secondary failure");
            }
        }
    }
}
