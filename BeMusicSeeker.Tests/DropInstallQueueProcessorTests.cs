using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DropInstallQueueProcessorTests
{
    [TestMethod]
    public void Enqueue_ProcessesBatchesSequentiallyAndReportsPendingCount()
    {
        List<string> processed = [];
        List<DropInstallQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        Exception? backgroundFailure = null;
        var firstStarted = new ManualResetEventSlim(initialState: false);
        var releaseFirst = new ManualResetEventSlim(initialState: false);
        var secondFinished = new ManualResetEventSlim(initialState: false);
        var pendingReported = new ManualResetEventSlim(initialState: false);
        var queueBecameInactive = new ManualResetEventSlim(initialState: false);
        var processor = new DropInstallQueueProcessor(
            delegate (DroppedInstallBatchRequest request, CancellationToken token)
            {
                lock (syncRoot)
                {
                    processed.Add(request.DisplayName);
                }
                if (request.DisplayName == "first.zip")
                {
                    firstStarted.Set();
                    if (!releaseFirst.Wait(3000))
                    {
                        lock (syncRoot)
                        {
                            backgroundFailure = new AssertFailedException("The first batch was not released in time.");
                        }
                    }
                }
                else
                {
                    secondFinished.Set();
                }
            },
            delegate (DropInstallQueueStatusSnapshot snapshot)
            {
                lock (syncRoot)
                {
                    snapshots.Add(snapshot);
                }
                if (snapshot.IsActive && snapshot.CurrentDisplayName == "first.zip" && snapshot.PendingBatchCount == 1)
                {
                    pendingReported.Set();
                }
                if (!snapshot.IsActive)
                {
                    queueBecameInactive.Set();
                }
            },
            delegate (Exception ex)
            {
                lock (syncRoot)
                {
                    backgroundFailure = ex;
                }
            });

        processor.Enqueue([@"C:\queue\first.zip"]);
        Assert.IsTrue(firstStarted.Wait(3000), "The first batch did not start.");
        processor.Enqueue([@"C:\queue\second.zip"]);

        Assert.IsTrue(pendingReported.Wait(5000), "Pending batch count was not reported.");

        releaseFirst.Set();
        Assert.IsTrue(secondFinished.Wait(3000), "The second batch did not complete.");
        Assert.IsTrue(SpinWait.SpinUntil(delegate
        {
            lock (syncRoot)
            {
                return processed.Count == 2;
            }
        }, 3000), "Both batches were not processed.");

        lock (syncRoot)
        {
            if (backgroundFailure != null)
            {
                throw backgroundFailure;
            }
            CollectionAssert.AreEqual(new[] { "first.zip", "second.zip" }, processed);
        }

        Assert.IsTrue(queueBecameInactive.Wait(5000), "Queue did not return to the inactive state.");
    }

    [TestMethod]
    public void CancelAll_CancelsActiveBatchAndClearsPendingBatches()
    {
        List<string> startedBatches = [];
        List<DropInstallQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        Exception? backgroundFailure = null;
        var firstStarted = new ManualResetEventSlim(initialState: false);
        var tokenCancelled = new ManualResetEventSlim(initialState: false);
        var queueBecameInactive = new ManualResetEventSlim(initialState: false);
        var processor = new DropInstallQueueProcessor(
            delegate (DroppedInstallBatchRequest request, CancellationToken token)
            {
                lock (syncRoot)
                {
                    startedBatches.Add(request.DisplayName);
                }
                firstStarted.Set();
                int signaledIndex = WaitHandle.WaitAny([token.WaitHandle], 3000);
                if (signaledIndex == WaitHandle.WaitTimeout)
                {
                    lock (syncRoot)
                    {
                        backgroundFailure = new AssertFailedException("The active batch token was not cancelled.");
                    }
                    return;
                }
                tokenCancelled.Set();
                token.ThrowIfCancellationRequested();
            },
            delegate (DropInstallQueueStatusSnapshot snapshot)
            {
                lock (syncRoot)
                {
                    snapshots.Add(snapshot);
                }
                if (!snapshot.IsActive)
                {
                    queueBecameInactive.Set();
                }
            });

        processor.Enqueue([@"C:\queue\first.zip"]);
        processor.Enqueue([@"C:\queue\second.zip"]);
        Assert.IsTrue(firstStarted.Wait(3000), "The first batch did not start.");

        processor.CancelAll();

        Assert.IsTrue(tokenCancelled.Wait(3000), "The active batch did not observe cancellation.");
        Assert.IsTrue(queueBecameInactive.Wait(5000), "Queue did not become inactive after cancellation.");

        lock (syncRoot)
        {
            if (backgroundFailure != null)
            {
                throw backgroundFailure;
            }
            CollectionAssert.AreEqual(new[] { "first.zip" }, startedBatches);
            Assert.IsTrue(snapshots.Any(snapshot => snapshot.IsCancellationRequested || !snapshot.CanCancel));
        }
    }

    [TestMethod]
    public void BatchFailure_DoesNotPreventFollowingBatch()
    {
        List<string> processed = [];
        List<string> errors = [];
        object syncRoot = new();
        var secondFinished = new ManualResetEventSlim(initialState: false);
        var processor = new DropInstallQueueProcessor(
            delegate (DroppedInstallBatchRequest request, CancellationToken token)
            {
                if (request.DisplayName == "first.zip")
                {
                    throw new InvalidOperationException("boom");
                }
                lock (syncRoot)
                {
                    processed.Add(request.DisplayName);
                }
                secondFinished.Set();
            },
            delegate (DropInstallQueueStatusSnapshot snapshot)
            {
            },
            delegate (Exception ex)
            {
                lock (syncRoot)
                {
                    errors.Add(ex.Message);
                }
            });

        processor.Enqueue([@"C:\queue\first.zip"]);
        processor.Enqueue([@"C:\queue\second.zip"]);

        Assert.IsTrue(secondFinished.Wait(3000), "The second batch did not complete after the first batch failed.");

        lock (syncRoot)
        {
            CollectionAssert.AreEqual(new[] { "second.zip" }, processed);
            CollectionAssert.AreEqual(new[] { "boom" }, errors);
        }
    }

    [TestMethod]
    public void ReportActiveBatchCurrentWork_ReportsAndClearsCurrentWork()
    {
        List<DropInstallQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        var currentWorkReported = new ManualResetEventSlim(initialState: false);
        var releaseBatch = new ManualResetEventSlim(initialState: false);
        var queueBecameInactive = new ManualResetEventSlim(initialState: false);
        DropInstallQueueProcessor processor = null!;
        processor = new DropInstallQueueProcessor(
            delegate (DroppedInstallBatchRequest request, CancellationToken token)
            {
                processor.ReportActiveBatchCurrentWork(1, 3, "a.zip");
                if (!releaseBatch.Wait(3000))
                {
                    throw new AssertFailedException("The active batch was not released in time.");
                }
                processor.ReportActiveBatchProgress(1);
            },
            delegate (DropInstallQueueStatusSnapshot snapshot)
            {
                lock (syncRoot)
                {
                    snapshots.Add(snapshot);
                }
                if (snapshot.IsActive
                    && snapshot.IsCurrentWorkInProgress
                    && snapshot.CurrentWorkIndex == 1
                    && snapshot.CurrentWorkTotal == 3
                    && snapshot.CurrentWorkDisplayName == "a.zip")
                {
                    currentWorkReported.Set();
                }
                if (!snapshot.IsActive)
                {
                    queueBecameInactive.Set();
                }
            });

        processor.Enqueue([@"C:\queue\a.zip", @"C:\queue\b.zip", @"C:\queue\c.zip"]);

        Assert.IsTrue(currentWorkReported.Wait(3000), "Current work progress was not reported.");
        releaseBatch.Set();
        Assert.IsTrue(queueBecameInactive.Wait(5000), "Queue did not return to the inactive state.");

        lock (syncRoot)
        {
            DropInstallQueueStatusSnapshot currentWorkSnapshot = snapshots.First(snapshot => snapshot.IsCurrentWorkInProgress);
            Assert.AreEqual(0, currentWorkSnapshot.CompletedPathCount);
            Assert.AreEqual(1, currentWorkSnapshot.CurrentWorkIndex);
            Assert.AreEqual(3, currentWorkSnapshot.CurrentWorkTotal);
            Assert.AreEqual("a.zip", currentWorkSnapshot.CurrentWorkDisplayName);

            DropInstallQueueStatusSnapshot completedSnapshot = snapshots.First(snapshot => snapshot.IsActive && snapshot.CompletedPathCount == 1);
            Assert.IsFalse(completedSnapshot.IsCurrentWorkInProgress);
            Assert.AreEqual(0, completedSnapshot.CurrentWorkIndex);
            Assert.AreEqual(string.Empty, completedSnapshot.CurrentWorkDisplayName);

            DropInstallQueueStatusSnapshot inactiveSnapshot = snapshots.Last();
            Assert.IsFalse(inactiveSnapshot.IsActive);
            Assert.IsFalse(inactiveSnapshot.IsCurrentWorkInProgress);
            Assert.AreEqual(0, inactiveSnapshot.CurrentWorkIndex);
            Assert.AreEqual(0, inactiveSnapshot.CurrentWorkTotal);
            Assert.AreEqual(string.Empty, inactiveSnapshot.CurrentWorkDisplayName);
        }
    }
}
