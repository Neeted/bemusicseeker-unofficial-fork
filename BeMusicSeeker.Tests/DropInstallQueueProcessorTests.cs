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
    public async Task WaitForIdleAsync_CompletesAfterTerminalStatusNotificationReturns()
    {
        using var terminalEntered = new ManualResetEventSlim(false);
        using var releaseTerminal = new ManualResetEventSlim(false);
        var processor = new DropInstallQueueProcessor(
            (_, _) => { },
            snapshot =>
            {
                if (!snapshot.IsActive)
                {
                    terminalEntered.Set();
                    Assert.IsTrue(releaseTerminal.Wait(5000));
                }
            });
        try
        {
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["chart.zip"])));
            Task idle = processor.WaitForIdleAsync();
            Assert.IsTrue(terminalEntered.Wait(5000), "The terminal status was not published.");
            Assert.IsFalse(idle.IsCompleted, "Idle completion must follow terminal status publication.");
            releaseTerminal.Set();
            await idle;
        }
        finally
        {
            releaseTerminal.Set();
        }
    }

    [TestMethod]
    public async Task RequestDisposition_CompletesAfterAbandonmentCleanupReturns()
    {
        using var cleanupEntered = new ManualResetEventSlim(false);
        using var releaseCleanup = new ManualResetEventSlim(false);
        var request = new DroppedInstallBatchRequest(
            ["chart.zip"],
            ["chart.zip"],
            ["owned-root"],
            _ =>
            {
                cleanupEntered.Set();
                Assert.IsTrue(releaseCleanup.Wait(5000));
            },
            null);
        Task disposition = request.WaitForDispositionAsync();
        Task abandon = Task.Factory.StartNew(
            request.TryAbandonUnconsumedSources,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            Assert.IsTrue(cleanupEntered.Wait(5000));
            Assert.IsFalse(disposition.IsCompleted, "Disposition must follow ingress cleanup.");
            releaseCleanup.Set();
            await abandon;
            await disposition;
        }
        finally
        {
            releaseCleanup.Set();
        }
    }

    [TestMethod]
    public async Task TerminalStatusReenqueue_CompletesOldIdleReceiptAndCreatesNewLifecycleReceipt()
    {
        using var firstStarted = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var secondProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondIdleCaptured = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DropInstallQueueProcessor? processor = null;
        int terminalEnqueueCount = 0;
        processor = new DropInstallQueueProcessor(
            (request, _) =>
            {
                if (request.DisplayName == "first.zip")
                {
                    firstStarted.Set();
                    Assert.IsTrue(releaseFirst.Wait(5000));
                }
                else
                {
                    secondProcessed.TrySetResult(true);
                }
            },
            snapshot =>
            {
                if (!snapshot.IsActive
                    && Interlocked.Exchange(ref terminalEnqueueCount, 1) == 0)
                {
                    Assert.IsTrue(processor!.TryEnqueue(
                        new DroppedInstallBatchRequest(["second.zip"])));
                    secondIdleCaptured.TrySetResult(processor.WaitForIdleAsync());
                }
            });

        try
        {
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["first.zip"])));
            Assert.IsTrue(firstStarted.Wait(5000));
            Task firstIdle = processor.WaitForIdleAsync();
            releaseFirst.Set();

            Task secondIdle = await secondIdleCaptured.Task;
            Assert.AreNotSame(firstIdle, secondIdle, "Each queue lifecycle must own a distinct idle receipt.");
            await firstIdle;
            await secondProcessed.Task;
            await secondIdle;
        }
        finally
        {
            releaseFirst.Set();
        }
    }

    [TestMethod]
    public async Task Enqueue_ProcessesBatchesSequentiallyAndReportsPendingCount()
    {
        List<string> processed = [];
        List<DropInstallQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        Exception? backgroundFailure = null;
        var firstStarted = new ManualResetEventSlim(initialState: false);
        var releaseFirst = new ManualResetEventSlim(initialState: false);
        var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingReported = new ManualResetEventSlim(initialState: false);
        var queueBecameInactive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    secondFinished.TrySetResult(true);
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
                    queueBecameInactive.TrySetResult(true);
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
        await secondFinished.Task;
        await AssertProcessorIdleAsync(processor);

        lock (syncRoot)
        {
            if (backgroundFailure != null)
            {
                throw backgroundFailure;
            }
            CollectionAssert.AreEqual(new[] { "first.zip", "second.zip" }, processed);
        }

        await queueBecameInactive.Task;
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
    public async Task BatchFailure_DoesNotPreventFollowingBatch()
    {
        List<string> processed = [];
        List<string> errors = [];
        object syncRoot = new();
        var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                secondFinished.TrySetResult(true);
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

        await secondFinished.Task;

        lock (syncRoot)
        {
            CollectionAssert.AreEqual(new[] { "second.zip" }, processed);
            CollectionAssert.AreEqual(new[] { "boom" }, errors);
        }
    }

    [TestMethod]
    public async Task ThrowingFailureCallback_DoesNotStrandFollowingBatchOrQueueLifecycle()
    {
        using var firstStarted = new ManualResetEventSlim(initialState: false);
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                secondFinished.TrySetResult(true);
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

        await secondFinished.Task;
        await AssertProcessorIdleAsync(processor);
        Assert.AreEqual(2, Volatile.Read(ref processCalls));
        Assert.AreEqual(1, Volatile.Read(ref failureCallbackCalls));
    }

    [TestMethod]
    public async Task ReportActiveBatchCurrentWork_ReportsAndClearsCurrentWork()
    {
        List<DropInstallQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        var currentWorkReported = new ManualResetEventSlim(initialState: false);
        var releaseBatch = new ManualResetEventSlim(initialState: false);
        var queueBecameInactive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    queueBecameInactive.TrySetResult(true);
                }
            });

        processor.TryEnqueue(new DroppedInstallBatchRequest(
            [@"C:\queue\a.zip", @"C:\queue\b.zip", @"C:\queue\c.zip"]));

        Assert.IsTrue(currentWorkReported.Wait(3000), "Current work progress was not reported.");
        releaseBatch.Set();
        await queueBecameInactive.Task;

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
    public async Task PendingTransientBatch_RemainsReadableUntilConsumerStarts()
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
        var secondRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    secondRead.TrySetResult(true);
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
            await secondRead.Task;
            await AssertProcessorIdleAsync(processor);
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
    public async Task CancelAll_DeletesPendingOwnedRootButNeverExternalOriginal()
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

            await AssertProcessorIdleAsync(processor);
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
    public async Task InstallerHandoff_PreventsQueueFinallyFromDeletingOwnedRoot()
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

            await AssertProcessorIdleAsync(processor);
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
    public async Task CancelAll_BlockedPendingCleanupCancelsActiveBeforeHandoffAndDefersIdle()
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
        var lateCleanupCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalInactive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        freshProcessed.TrySetResult(true);
                        return;
                    }
                    Interlocked.Increment(ref unexpectedProcessCalls);
                },
                snapshot =>
                {
                    if (!snapshot.IsActive)
                    {
                        terminalInactive.TrySetResult(true);
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
            await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(processor.IsIdle, "Detached pending cleanup is part of queue drain state.");
            Assert.IsFalse(terminalInactive.Task.IsCompleted, "Inactive status must wait for detached cleanup.");

            var rejectedDuringDrain = new DroppedInstallBatchRequest(
                [Path.Combine(lateRoot, "late.zip")],
                ["late.zip"],
                [lateRoot],
                path =>
                {
                    DeleteDirectory(path);
                    lateCleanupCompleted.TrySetResult(true);
                },
                null);
            Assert.IsFalse(
                processor.TryEnqueue(rejectedDuringDrain),
                "A request must not be accepted and swept after cancellation has linearized.");
            Assert.IsTrue(Directory.Exists(lateRoot), "Rejected request ownership remains with the caller.");
            releasePendingCleanup.Set();

            await AssertProcessorIdleAsync(processor);
            await terminalInactive.Task;
            Assert.AreEqual(0, unexpectedProcessCalls, "Rejected drain-time requests must never reach the worker.");

            Assert.IsTrue(rejectedDuringDrain.TryAbandonUnconsumedSources());
            await lateCleanupCompleted.Task;

            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["fresh.zip"])));
            await freshProcessed.Task;
            await AssertProcessorIdleAsync(processor);
            Assert.IsNull(backgroundFailure);
        }
        finally
        {
            releasePendingCleanup.Set();
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task CancelAll_CleanupFailureStillCompletesDrainAndAcceptsFreshBatch()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(DropInstallQueueProcessorTests),
            Guid.NewGuid().ToString("N"));
        string failedCleanupRoot = Path.Combine(root, "failed-cleanup");
        Directory.CreateDirectory(failedCleanupRoot);
        using var activeStarted = new ManualResetEventSlim(false);
        var cleanupFailureReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        freshProcessed.TrySetResult(true);
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
                        cleanupFailureReported.TrySetResult(true);
                    }
                })));

            processor.CancelAll();

            await cleanupFailureReported.Task;
            await AssertProcessorIdleAsync(processor);
            Assert.IsTrue(Directory.Exists(failedCleanupRoot));
            Assert.IsNull(backgroundFailure);
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["fresh.zip"])));
            await freshProcessed.Task;
            await AssertProcessorIdleAsync(processor);
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

    private static async Task AssertProcessorIdleAsync(DropInstallQueueProcessor processor)
    {
        await processor.WaitForIdleAsync();
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
