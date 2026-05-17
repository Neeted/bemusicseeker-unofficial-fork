using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PendingInstallEstimateQueueProcessorTests
{
    [TestMethod]
    public void Enqueue_ProcessesBatchesSequentiallyAndReportsProgress()
    {
        List<string> processed = [];
        List<PendingInstallEstimateQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        Exception? backgroundFailure = null;
        var firstStarted = new ManualResetEventSlim(initialState: false);
        var releaseFirst = new ManualResetEventSlim(initialState: false);
        var secondFinished = new ManualResetEventSlim(initialState: false);
        var progressReported = new ManualResetEventSlim(initialState: false);
        PendingInstallEstimateQueueProcessor? processor = null;
        processor = new PendingInstallEstimateQueueProcessor(
            delegate (PendingInstallEstimateBatchRequest request, CancellationToken token)
            {
                lock (syncRoot)
                {
                    processed.Add(request.DisplayName);
                }
                if (request.DisplayName == "startup")
                {
                    firstStarted.Set();
                    processor!.ReportActiveBatchProgress(1);
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
            delegate (PendingInstallEstimateQueueStatusSnapshot snapshot)
            {
                lock (syncRoot)
                {
                    snapshots.Add(snapshot.Clone());
                }
                if (snapshot.IsActive && snapshot.CurrentDisplayName == "startup" && snapshot.CompletedPackageCount == 1)
                {
                    progressReported.Set();
                }
            },
            delegate (Exception ex)
            {
                lock (syncRoot)
                {
                    backgroundFailure = ex;
                }
            });

        processor.Enqueue(new PendingInstallEstimateBatchRequest(
            PendingInstallEstimateBatchSource.StartupRestore,
            new[] { CreatePackage(@"C:\pending\startup\a"), CreatePackage(@"C:\pending\startup\b") },
            "startup"));
        Assert.IsTrue(firstStarted.Wait(3000), "The first batch did not start.");

        processor.Enqueue(new PendingInstallEstimateBatchRequest(
            PendingInstallEstimateBatchSource.AutoInstall,
            new[] { CreatePackage(@"C:\pending\drop\c") },
            "drop"));

        Assert.IsTrue(progressReported.Wait(3000), "The active batch progress was not reported.");
        releaseFirst.Set();
        Assert.IsTrue(secondFinished.Wait(3000), "The second batch did not finish.");

        lock (syncRoot)
        {
            if (backgroundFailure != null)
            {
                throw backgroundFailure;
            }
            CollectionAssert.AreEqual(new[] { "startup", "drop" }, processed);
            Assert.IsTrue(snapshots.Any(snapshot => snapshot.IsActive && snapshot.CurrentDisplayName == "startup" && snapshot.PendingBatchCount == 1));
        }
    }

    [TestMethod]
    public void GetStatusSnapshot_ReturnsInactiveSnapshotAfterCompletion()
    {
        var completed = new ManualResetEventSlim(initialState: false);
        var processor = new PendingInstallEstimateQueueProcessor(
            delegate (PendingInstallEstimateBatchRequest request, CancellationToken token)
            {
                completed.Set();
            },
            delegate (PendingInstallEstimateQueueStatusSnapshot snapshot)
            {
            });

        processor.Enqueue(new PendingInstallEstimateBatchRequest(
            PendingInstallEstimateBatchSource.StartupRestore,
            new[] { CreatePackage(@"C:\pending\startup\a") },
            "startup"));

        Assert.IsTrue(completed.Wait(3000), "The batch did not complete.");
        Assert.IsTrue(SpinWait.SpinUntil(() => !processor.GetStatusSnapshot().IsActive, 3000), "The queue did not become inactive.");

        PendingInstallEstimateQueueStatusSnapshot snapshot = processor.GetStatusSnapshot();
        Assert.IsFalse(snapshot.IsActive);
        Assert.AreEqual(0, snapshot.PendingBatchCount);
        Assert.AreEqual(0, snapshot.CurrentPackageCount);
        Assert.AreEqual(0, snapshot.CompletedPackageCount);
    }

    private static ChartPackage CreatePackage(string path)
    {
        var package = new ChartPackage
        {
            path = path
        };
        return package;
    }
}
