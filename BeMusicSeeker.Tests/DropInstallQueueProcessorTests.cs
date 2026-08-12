using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\first.zip"]));
        Assert.IsTrue(firstStarted.Wait(3000), "The first batch did not start.");
        processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"]));

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

        processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\first.zip"]));
        processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"]));
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

        processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\first.zip"]));
        processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"]));

        Assert.IsTrue(secondFinished.Wait(3000), "The second batch did not complete after the first batch failed.");

        lock (syncRoot)
        {
            CollectionAssert.AreEqual(new[] { "second.zip" }, processed);
            CollectionAssert.AreEqual(new[] { "boom" }, errors);
        }
    }

    [TestMethod]
    public void ThrowingFailureCallback_DoesNotStrandFollowingBatchOrQueueLifecycle()
    {
        using var firstStarted = new ManualResetEventSlim(initialState: false);
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        using var secondFinished = new ManualResetEventSlim(initialState: false);
        int processCalls = 0;
        int failureCallbackCalls = 0;
        var processor = new DropInstallQueueProcessor(
            (request, _) =>
            {
                Interlocked.Increment(ref processCalls);
                if (request.DisplayName == "first.zip")
                {
                    firstStarted.Set();
                    Assert.IsTrue(releaseFirst.Wait(5000));
                    throw new InvalidOperationException("batch failed");
                }
                secondFinished.Set();
            },
            _ => { },
            _ =>
            {
                Interlocked.Increment(ref failureCallbackCalls);
                throw new InvalidOperationException("failure callback failed");
            });

        Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\first.zip"])));
        Assert.IsTrue(firstStarted.Wait(5000));
        Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"])));
        releaseFirst.Set();

        Assert.IsTrue(secondFinished.Wait(5000), "The throwing notification callback stranded the FIFO worker.");
        Assert.IsTrue(SpinWait.SpinUntil(() => processor.IsIdle, 5000));
        Assert.AreEqual(2, Volatile.Read(ref processCalls));
        Assert.AreEqual(1, Volatile.Read(ref failureCallbackCalls));
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

        processor.TryEnqueue(new DroppedInstallBatchRequest(
            [@"C:\queue\a.zip", @"C:\queue\b.zip", @"C:\queue\c.zip"]));

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

    [TestMethod]
    public void PendingTransientBatch_RemainsReadableUntilConsumerStarts()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(DropInstallQueueProcessorTests), Guid.NewGuid().ToString("N"));
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        string secondFile = Path.Combine(secondRoot, "chart.bms");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(secondFile, "staged");
        using var firstStarted = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        using var secondRead = new ManualResetEventSlim(false);
        Exception? failure = null;
        try
        {
            var processor = new DropInstallQueueProcessor(
                (request, _) =>
                {
                    if (request.DisplayName == "first.zip")
                    {
                        firstStarted.Set();
                        releaseFirst.Wait(5000);
                        return;
                    }
                    Assert.AreEqual("staged", File.ReadAllText(request.Paths.Single()));
                    secondRead.Set();
                },
                _ => { },
                exception => failure = exception);

            processor.TryEnqueue(new DroppedInstallBatchRequest(
                [Path.Combine(firstRoot, "first.zip")],
                ["first.zip"],
                [firstRoot],
                DeleteDirectory,
                null));
            Assert.IsTrue(firstStarted.Wait(5000));
            processor.TryEnqueue(new DroppedInstallBatchRequest(
                [secondFile],
                ["second.bms"],
                [secondRoot],
                DeleteDirectory,
                null));

            Assert.IsTrue(File.Exists(secondFile), "Pending ownership must keep the staged copy alive.");
            releaseFirst.Set();
            Assert.IsTrue(secondRead.Wait(5000));
            Assert.IsTrue(SpinWait.SpinUntil(() => processor.IsIdle, 5000));
            Assert.IsNull(failure);
            Assert.IsFalse(Directory.Exists(secondRoot), "An untransferred request is abandoned after its consumer returns.");
        }
        finally
        {
            releaseFirst.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CancelAll_DeletesPendingOwnedRootButNeverExternalOriginal()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(DropInstallQueueProcessorTests), Guid.NewGuid().ToString("N"));
        string original = Path.Combine(root, "external", "original.bms");
        string activeRoot = Path.Combine(root, "active");
        string pendingRoot = Path.Combine(root, "pending");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        Directory.CreateDirectory(activeRoot);
        Directory.CreateDirectory(pendingRoot);
        File.WriteAllText(original, "original");
        using var activeStarted = new ManualResetEventSlim(false);
        using var releaseActive = new ManualResetEventSlim(false);
        try
        {
            var processor = new DropInstallQueueProcessor(
                (_, token) =>
                {
                    activeStarted.Set();
                    WaitHandle.WaitAny([token.WaitHandle, releaseActive.WaitHandle], 5000);
                    token.ThrowIfCancellationRequested();
                },
                _ => { });
            processor.TryEnqueue(CreateOwnedRequest(activeRoot, original, "active.zip"));
            Assert.IsTrue(activeStarted.Wait(5000));
            processor.TryEnqueue(CreateOwnedRequest(pendingRoot, original, "pending.zip"));

            processor.CancelAll();

            Assert.IsTrue(SpinWait.SpinUntil(() => processor.IsIdle, 5000));
            Assert.IsFalse(Directory.Exists(pendingRoot));
            Assert.IsFalse(Directory.Exists(activeRoot));
            Assert.IsTrue(File.Exists(original));
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

    [TestMethod]
    public void InstallerHandoff_PreventsQueueFinallyFromDeletingOwnedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(DropInstallQueueProcessorTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var processor = new DropInstallQueueProcessor(
                (request, _) => Assert.IsTrue(request.TransferSourceOwnershipToInstaller()),
                _ => { });
            processor.TryEnqueue(new DroppedInstallBatchRequest(
                [Path.Combine(root, "chart.bms")],
                ["chart.bms"],
                [root],
                DeleteDirectory,
                null));

            Assert.IsTrue(SpinWait.SpinUntil(() => processor.IsIdle, 5000));
            Assert.IsTrue(Directory.Exists(root));
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
    public void CancelAll_BlockedPendingCleanupCancelsActiveBeforeHandoffAndDefersIdle()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(DropInstallQueueProcessorTests), Guid.NewGuid().ToString("N"));
        string activeRoot = Path.Combine(root, "active");
        string pendingRoot = Path.Combine(root, "pending");
        string lateRoot = Path.Combine(root, "late");
        Directory.CreateDirectory(activeRoot);
        Directory.CreateDirectory(pendingRoot);
        Directory.CreateDirectory(lateRoot);
        using var activeStarted = new ManualResetEventSlim(false);
        using var activeObservedCancellation = new ManualResetEventSlim(false);
        using var pendingCleanupStarted = new ManualResetEventSlim(false);
        using var releasePendingCleanup = new ManualResetEventSlim(false);
        using var lateCleanupCompleted = new ManualResetEventSlim(false);
        using var terminalInactive = new ManualResetEventSlim(false);
        using var freshProcessed = new ManualResetEventSlim(false);
        Exception? backgroundFailure = null;
        int unexpectedProcessCalls = 0;
        try
        {
            var processor = new DropInstallQueueProcessor(
                (request, token) =>
                {
                    if (request.DisplayName == "active.zip")
                    {
                        activeStarted.Set();
                        if (WaitHandle.WaitAny([token.WaitHandle], 5000) == WaitHandle.WaitTimeout)
                        {
                            backgroundFailure = new AssertFailedException("Active cancellation was delayed by pending cleanup.");
                            return;
                        }
                        if (request.TransferSourceOwnershipToInstaller())
                        {
                            backgroundFailure = new AssertFailedException("Cancellation must reserve abandonment before handoff.");
                        }
                        activeObservedCancellation.Set();
                        return;
                    }
                    if (request.DisplayName == "fresh.zip")
                    {
                        freshProcessed.Set();
                        return;
                    }
                    Interlocked.Increment(ref unexpectedProcessCalls);
                },
                snapshot =>
                {
                    if (!snapshot.IsActive)
                    {
                        terminalInactive.Set();
                    }
                },
                exception => backgroundFailure = exception);

            processor.TryEnqueue(CreateOwnedRequest(activeRoot, "unused", "active.zip"));
            Assert.IsTrue(activeStarted.Wait(5000));
            processor.TryEnqueue(new DroppedInstallBatchRequest(
                [Path.Combine(pendingRoot, "pending.zip")],
                ["pending.zip"],
                [pendingRoot],
                path =>
                {
                    pendingCleanupStarted.Set();
                    if (!releasePendingCleanup.Wait(5000))
                    {
                        throw new AssertFailedException("Pending cleanup was not released.");
                    }
                    DeleteDirectory(path);
                },
                null));

            Task cancellation = Task.Factory.StartNew(
                processor.CancelAll,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.IsTrue(pendingCleanupStarted.Wait(5000));
            Assert.IsTrue(activeObservedCancellation.Wait(5000));
            Assert.IsTrue(
                cancellation.Wait(5000),
                "CancelAll must return without waiting for recursive pending cleanup.");
            Assert.IsFalse(processor.IsIdle, "Detached pending cleanup is part of queue drain state.");
            Assert.IsFalse(terminalInactive.IsSet, "Inactive status must wait for detached cleanup.");

            var rejectedDuringDrain = new DroppedInstallBatchRequest(
                [Path.Combine(lateRoot, "late.zip")],
                ["late.zip"],
                [lateRoot],
                path =>
                {
                    DeleteDirectory(path);
                    lateCleanupCompleted.Set();
                },
                null);
            Assert.IsFalse(
                processor.TryEnqueue(rejectedDuringDrain),
                "A request must not be accepted and swept after cancellation has linearized.");
            Assert.IsTrue(Directory.Exists(lateRoot), "Rejected request ownership remains with the caller.");
            releasePendingCleanup.Set();

            Assert.IsTrue(SpinWait.SpinUntil(() => processor.IsIdle, 5000));
            Assert.IsTrue(
                terminalInactive.Wait(5000),
                "The terminal inactive notification must follow completion of detached cleanup.");
            Assert.AreEqual(0, unexpectedProcessCalls, "Rejected drain-time requests must never reach the worker.");

            Assert.IsTrue(rejectedDuringDrain.TryAbandonUnconsumedSources());
            Assert.IsTrue(lateCleanupCompleted.Wait(5000));

            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["fresh.zip"])));
            Assert.IsTrue(freshProcessed.Wait(5000), "An enqueue after the epoch closes must start a fresh worker.");
            Assert.IsTrue(SpinWait.SpinUntil(() => processor.IsIdle, 5000));
            Assert.IsNull(backgroundFailure);
        }
        finally
        {
            releasePendingCleanup.Set();
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void CancelAll_CleanupFailureStillCompletesDrainAndAcceptsFreshBatch()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(DropInstallQueueProcessorTests),
            Guid.NewGuid().ToString("N"));
        string failedCleanupRoot = Path.Combine(root, "failed-cleanup");
        Directory.CreateDirectory(failedCleanupRoot);
        using var activeStarted = new ManualResetEventSlim(false);
        using var cleanupFailureReported = new ManualResetEventSlim(false);
        using var freshProcessed = new ManualResetEventSlim(false);
        Exception? backgroundFailure = null;
        try
        {
            var processor = new DropInstallQueueProcessor(
                (request, token) =>
                {
                    if (request.DisplayName == "active.zip")
                    {
                        activeStarted.Set();
                        token.WaitHandle.WaitOne();
                        return;
                    }
                    if (request.DisplayName == "fresh.zip")
                    {
                        freshProcessed.Set();
                    }
                },
                _ => { },
                exception => backgroundFailure = exception);
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["active.zip"])));
            Assert.IsTrue(activeStarted.Wait(5000));
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(
                [Path.Combine(failedCleanupRoot, "pending.zip")],
                ["pending.zip"],
                [failedCleanupRoot],
                _ => throw new IOException("cleanup failed"),
                (_, exception) =>
                {
                    if (exception is IOException)
                    {
                        cleanupFailureReported.Set();
                    }
                })));

            processor.CancelAll();

            Assert.IsTrue(cleanupFailureReported.Wait(5000));
            Assert.IsTrue(SpinWait.SpinUntil(() => processor.IsIdle, 5000));
            Assert.IsTrue(Directory.Exists(failedCleanupRoot));
            Assert.IsNull(backgroundFailure);
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["fresh.zip"])));
            Assert.IsTrue(freshProcessed.Wait(5000));
            Assert.IsTrue(SpinWait.SpinUntil(() => processor.IsIdle, 5000));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static DroppedInstallBatchRequest CreateOwnedRequest(string ownedRoot, string original, string displayPath)
    {
        return new DroppedInstallBatchRequest(
            [Path.Combine(ownedRoot, displayPath)],
            [displayPath],
            [ownedRoot],
            DeleteDirectory,
            null);
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Queue cleanup can win between the existence probe and recursive test-fixture cleanup.
        }
    }
}
