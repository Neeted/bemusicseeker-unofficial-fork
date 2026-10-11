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
    /// <summary>
    /// 保証：受理した事前計算は依存する実パス・重ね合わせ・一覧・ハッシュを先に行って仮想一覧へ進み、終端と取消資源を一度だけ確定する。
    /// </summary>
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

    /// <summary>
    /// 保証：依存する途中段階が失敗したら残る依存段階を止め、独立した仮想一覧は試みて元の失敗段階・例外を返す。
    /// </summary>
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

    /// <summary>
    /// 保証：同じ起動tokenとscheduler世代の事前計算は二重に権限取得・投入せず、先の受理を維持する。
    /// </summary>
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

    /// <summary>
    /// 保証：schedulerに拒否された事前計算は権限と取消資源を解放し、一度だけ拒否終端を公開する。
    /// </summary>
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

    /// <summary>
    /// 保証：予約時のscheduler世代が要求と異なる場合は権限取得前に拒否し、古い起動の計算を投入しない。
    /// </summary>
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

    /// <summary>
    /// 保証：古い世代の新要求を拒否しても、既に受理済みの現行事前計算を取消したり置き換えたりしない。
    /// </summary>
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

    /// <summary>
    /// 保証：リセットは未開始の事前計算と権限を取消し、廃棄callbackと競合しても取消終端・資源解放を一度だけ行う。
    /// </summary>
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

    /// <summary>
    /// 保証：schedulerの重複した廃棄通知でも元の廃棄理由を保ち、権限・取消資源・廃棄終端を一度だけ処理する。
    /// </summary>
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

    /// <summary>
    /// 保証：投入例外の後に取消・終端通知・ログ・解放まで失敗しても元の投入例外を保持し、未開始のqueueと権限を回収する。
    /// </summary>
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

    /// <summary>
    /// 保証：実行中の段階でリセットされると後段へ進まず取消終端を保ち、古いfinallyで完了へ上書きしない。
    /// </summary>
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

    /// <summary>
    /// 保証：権限取得中のリセットは遅れて返った権限を取消し、古い計算を投入せず次の世代の計算を受理する。
    /// </summary>
    [TestMethod]
    public async Task ResetDuringLeaseAcquisition_CancelsAttachedLeaseWithoutQueueingStaleWork()
    {
        var harness = new Harness { GateLeaseAcquisition = true };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        Task<bool> staleSchedule = StartLongRunning(() => owner.Schedule(
            new StartupPostInitializationWarmupRequest("old", 24L, 10L)));
        try
        {
            harness.LeaseAcquireEntered.Wait();

            owner.Reset("new_generation");
            Assert.AreEqual(1, harness.Completions.Count);
            harness.ReleaseLeaseAcquisition.Set();

            Assert.IsFalse(await staleSchedule);
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
        finally
        {
            // 途中の表明失敗でも、予約 callback と所有 schedule を解放して終端まで回収する。
            harness.ReleaseLeaseAcquisition.Set();
            await staleSchedule;
        }
    }

    /// <summary>
    /// 保証：投入中のリセットが最初の削除に間に合わなくても遅い投入を回収し、旧計算の実段階を開始しない。
    /// </summary>
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
        Task reset = null;
        try
        {
            harness.QueueEntered.Wait();

            reset = StartLongRunning(() => owner.Reset("new_generation"));
            harness.CancelMissObserved.Wait();
            harness.ReleaseQueue.Set();

            Assert.IsFalse(await staleSchedule);
            harness.ReleaseFirstCancel.Set();
            await reset;
            Assert.AreEqual(1, harness.QueueCount);
            Assert.AreEqual(2, harness.CancelQueuedCount);
            Assert.AreEqual(1, harness.CancelLeaseCount);
            Assert.AreEqual(1, harness.Completions.Count);
            Assert.AreEqual(0, harness.StageOrder.Count);
            Assert.IsFalse(harness.HasQueuedWork);
        }
        finally
        {
            harness.ReleaseQueue.Set();
            harness.ReleaseFirstCancel.Set();
            await staleSchedule;
            if (reset != null)
            {
                await reset;
            }
        }
    }

    /// <summary>
    /// 保証：リセット後の遅い旧投入と旧取消は予約単位で扱い、新世代のqueue・取消資源・完了結果を巻き込まない。
    /// </summary>
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
        Task reset = null;
        Task<bool> newSchedule = null;
        try
        {
            harness.QueueEntered.Wait();

            reset = StartLongRunning(() => owner.Reset("new_generation"));
            harness.CancelMissObserved.Wait();
            harness.SchedulerGeneration = 1L;
            harness.ReleaseQueue.Set();
            Assert.IsFalse(await staleSchedule);
            harness.QueueEntered.Reset();
            harness.ReleaseQueue.Reset();

            newSchedule = StartLongRunning(() => owner.Schedule(
                new StartupPostInitializationWarmupRequest("new", 32L, 1L)));
            harness.QueueEntered.Wait();
            harness.ReleaseFirstCancel.Set();
            await reset;

            harness.ReleaseQueue.Set();
            Assert.IsTrue(await newSchedule);
            await harness.RunQueuedAsync();

            Assert.AreEqual(2, harness.Completions.Count);
            Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Cancelled, harness.Completions[0].Kind);
            Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, harness.Completions[1].Kind);
            Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
            Assert.AreEqual(1, harness.OwnedCancellations[1].DisposeCount);
            Assert.AreEqual(0, harness.CancelQueuedCount % 2, "Both stale cancellation attempts must remain reservation-scoped.");
        }
        finally
        {
            harness.ReleaseQueue.Set();
            harness.ReleaseFirstCancel.Set();
            await staleSchedule;
            if (reset != null)
            {
                await reset;
            }
            if (newSchedule != null)
            {
                await newSchedule;
            }
        }
    }

    /// <summary>
    /// 保証：予約callbackが外部lock待ちでも所有者のリセットを完了でき、遅れて戻った旧予約を開始せず回収する。
    /// </summary>
    [TestMethod]
    public async Task ReservationCallbackOutsideOwnerLockAllowsResetAndCleansLateReceipt()
    {
        var harness = new Harness();
        object progressLikeLock = new object();
        harness.ReserveExternalLock = progressLikeLock;
        harness.GateNextReservation = true;
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        Task<bool> staleSchedule = null;
        Task reset = null;
        try
        {
            lock (progressLikeLock)
            {
                staleSchedule = StartLongRunning(() => owner.Schedule(
                    new StartupPostInitializationWarmupRequest("late", 33L, 0L)));
                harness.ReserveEntered.Wait();

                reset = StartLongRunning(() => owner.Reset("progress_reset"));
                reset.Wait();
            }

            Assert.IsFalse(await staleSchedule);
            await reset;
            Assert.AreEqual(0, harness.AcquireCount);
            Assert.AreEqual(0, harness.QueueCount);
            Assert.AreEqual(1, harness.CancelQueuedCount);
            Assert.AreEqual(1, harness.Completions.Count);
            Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Failed, harness.Completions[0].Kind);
            Assert.AreEqual("reservation", harness.Completions[0].FailedStage);
            Assert.AreEqual(0, harness.StageOrder.Count);
        }
        finally
        {
            if (staleSchedule != null)
            {
                await staleSchedule;
            }
            if (reset != null)
            {
                await reset;
            }
        }
    }

    /// <summary>
    /// 保証：古い予約が外部lockで止まっている間に新しい要求を受理・投入でき、遅い旧予約では新queueを取消しない。
    /// </summary>
    [TestMethod]
    public async Task NewerScheduleCommitsWhileOlderReservationIsBlocked()
    {
        var harness = new Harness();
        object progressLikeLock = new object();
        harness.ReserveExternalLock = progressLikeLock;
        harness.GateNextReservation = true;
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        Task<bool> oldSchedule = null;
        Task<bool> newSchedule = null;
        try
        {
            lock (progressLikeLock)
            {
                oldSchedule = StartLongRunning(() => owner.Schedule(
                    new StartupPostInitializationWarmupRequest("old", 34L, 0L)));
                harness.ReserveEntered.Wait();

                newSchedule = StartLongRunning(() => owner.Schedule(
                    new StartupPostInitializationWarmupRequest("new", 35L, 0L)));
                newSchedule.Wait();
                Assert.IsTrue(newSchedule.Result);
            }

            Assert.IsFalse(await oldSchedule);
            Assert.AreEqual(1, harness.QueueCount);
            Assert.AreEqual(0, harness.CancelQueuedCount);

            await harness.RunQueuedAsync();

            Assert.AreEqual(2, harness.Completions.Count);
            Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Rejected, harness.Completions[0].Kind);
            Assert.AreEqual(StartupPostInitializationWarmupCompletionKind.Completed, harness.Completions[1].Kind);
            Assert.AreEqual(1, harness.OwnedCancellations.Count);
            Assert.AreEqual(1, harness.OwnedCancellations[0].DisposeCount);
        }
        finally
        {
            if (oldSchedule != null)
            {
                await oldSchedule;
            }
            if (newSchedule != null)
            {
                await newSchedule;
            }
        }
    }

    /// <summary>
    /// 保証：低い順序の遅い予約は、新要求の権限取得中でも旧投入・旧取消を起こさず新しい計算の終端を維持する。
    /// </summary>
    [TestMethod]
    public async Task LowerSequenceLateReservationCannotMutateNewerCommittedWarmup()
    {
        var harness = new Harness
        {
            GateNextReservation = true,
            GateLeaseAcquisition = true,
            SchedulerGeneration = 0L
        };
        object progressLikeLock = new object();
        harness.ReserveExternalLock = progressLikeLock;
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        var oldRequest = new StartupPostInitializationWarmupRequest("old", 36L, 0L);
        var newRequest = new StartupPostInitializationWarmupRequest("new", 37L, 0L);
        Task<bool> oldSchedule = null;
        Task<bool> newSchedule = null;

        try
        {
            lock (progressLikeLock)
            {
                oldSchedule = StartLongRunning(() => owner.Schedule(oldRequest));
                harness.ReserveEntered.Wait();

                newSchedule = StartLongRunning(() => owner.Schedule(newRequest));
                harness.LeaseAcquireEntered.Wait();
            }

            Assert.IsFalse(await oldSchedule);
            Assert.AreEqual(1, harness.AcquireCount);
            Assert.AreEqual(0, harness.QueueCount);
            Assert.AreEqual(0, harness.CancelQueuedCount);

            harness.ReleaseLeaseAcquisition.Set();
            Assert.IsTrue(await newSchedule);
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
        finally
        {
            harness.ReleaseLeaseAcquisition.Set();
            if (oldSchedule != null)
            {
                await oldSchedule;
            }
            if (newSchedule != null)
            {
                await newSchedule;
            }
        }
    }

    /// <summary>
    /// 保証：低い順序のsubmitを拒否した後でも、高いscheduler予約を確定でき、旧取消による新queueの消去を防ぐ。
    /// </summary>
    [TestMethod]
    public async Task HigherSchedulerReservationCommitsAfterLowerOwnerSubmitIsRejected()
    {
        var harness = new Harness { GateReservationAcceptance = true };
        StartupPostInitializationWarmupOwner owner = harness.CreateOwner();
        var oldRequest = new StartupPostInitializationWarmupRequest("old", 38L, 0L);
        var newRequest = new StartupPostInitializationWarmupRequest("new", 39L, 0L);

        Task<bool> oldSchedule = null;
        Task<bool> newSchedule = null;
        try
        {
            oldSchedule = StartLongRunning(() => owner.Schedule(oldRequest));
            harness.ReservationOneAccepted.Wait();

            newSchedule = StartLongRunning(() => owner.Schedule(newRequest));
            harness.ReservationTwoAccepted.Wait();

            harness.ReleaseReservationOne.Set();
            Assert.IsFalse(await oldSchedule);
            Assert.AreEqual(1, harness.QueueCount);
            Assert.AreEqual(1, harness.CancelQueuedCount);

            harness.ReleaseReservationTwo.Set();
            Assert.IsTrue(await newSchedule);
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
        finally
        {
            harness.ReleaseReservationOne.Set();
            harness.ReleaseReservationTwo.Set();
            if (oldSchedule != null)
            {
                await oldSchedule;
            }
            if (newSchedule != null)
            {
                await newSchedule;
            }
        }
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
                        ReleaseLeaseAcquisition.Wait();
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
                            release.Wait();
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
                        ReleaseQueue.Wait();
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
                            ReleaseFirstCancel.Wait();
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
