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
        List<string> processed = new List<string>();
        List<DropInstallQueueStatusSnapshot> snapshots = new List<DropInstallQueueStatusSnapshot>();
        object syncRoot = new object();
        Exception? backgroundFailure = null;
        ManualResetEventSlim firstStarted = new ManualResetEventSlim(initialState: false);
        ManualResetEventSlim releaseFirst = new ManualResetEventSlim(initialState: false);
        ManualResetEventSlim secondFinished = new ManualResetEventSlim(initialState: false);
        ManualResetEventSlim pendingReported = new ManualResetEventSlim(initialState: false);
        ManualResetEventSlim queueBecameInactive = new ManualResetEventSlim(initialState: false);
        DropInstallQueueProcessor processor = new DropInstallQueueProcessor(
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

        processor.Enqueue(new string[1] { @"C:\queue\first.zip" });
        Assert.IsTrue(firstStarted.Wait(3000), "The first batch did not start.");
        processor.Enqueue(new string[1] { @"C:\queue\second.zip" });

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
        List<string> startedBatches = new List<string>();
        List<DropInstallQueueStatusSnapshot> snapshots = new List<DropInstallQueueStatusSnapshot>();
        object syncRoot = new object();
        Exception? backgroundFailure = null;
        ManualResetEventSlim firstStarted = new ManualResetEventSlim(initialState: false);
        ManualResetEventSlim tokenCancelled = new ManualResetEventSlim(initialState: false);
        ManualResetEventSlim queueBecameInactive = new ManualResetEventSlim(initialState: false);
        DropInstallQueueProcessor processor = new DropInstallQueueProcessor(
            delegate (DroppedInstallBatchRequest request, CancellationToken token)
            {
                lock (syncRoot)
                {
                    startedBatches.Add(request.DisplayName);
                }
                firstStarted.Set();
                int signaledIndex = WaitHandle.WaitAny(new WaitHandle[1] { token.WaitHandle }, 3000);
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

        processor.Enqueue(new string[1] { @"C:\queue\first.zip" });
        processor.Enqueue(new string[1] { @"C:\queue\second.zip" });
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
            Assert.IsTrue(snapshots.Any((DropInstallQueueStatusSnapshot snapshot) => snapshot.IsCancellationRequested || !snapshot.CanCancel));
        }
    }

    [TestMethod]
    public void BatchFailure_DoesNotPreventFollowingBatch()
    {
        List<string> processed = new List<string>();
        List<string> errors = new List<string>();
        object syncRoot = new object();
        ManualResetEventSlim secondFinished = new ManualResetEventSlim(initialState: false);
        DropInstallQueueProcessor processor = new DropInstallQueueProcessor(
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

        processor.Enqueue(new string[1] { @"C:\queue\first.zip" });
        processor.Enqueue(new string[1] { @"C:\queue\second.zip" });

        Assert.IsTrue(secondFinished.Wait(3000), "The second batch did not complete after the first batch failed.");

        lock (syncRoot)
        {
            CollectionAssert.AreEqual(new[] { "second.zip" }, processed);
            CollectionAssert.AreEqual(new[] { "boom" }, errors);
        }
    }
}
