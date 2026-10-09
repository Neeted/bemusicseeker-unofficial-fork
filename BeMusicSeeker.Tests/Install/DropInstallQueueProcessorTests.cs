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
    private static Func<DroppedInstallBatchRequest, CancellationToken, Task> CompleteBatch(
        Action<DroppedInstallBatchRequest, CancellationToken> action) => (request, token) =>
        {
            action(request, token);
            return Task.CompletedTask;
        };

    [TestMethod]
    public async Task WaitForIdleAsync_CompletesAfterTerminalStatusNotificationReturns()
    {
        using var terminalEntered = new ManualResetEventSlim(false);
        using var releaseTerminal = new ManualResetEventSlim(false);
        var processor = new DropInstallQueueProcessor(
            CompleteBatch((_, _) => { }),
            snapshot =>
            {
                if (!snapshot.IsActive)
                {
                    terminalEntered.Set();
                    releaseTerminal.Wait();
                }
            });
        Task? idle = null;
        try
        {
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["chart.zip"])));
            idle = processor.WaitForIdleAsync();
            terminalEntered.Wait();
            Assert.IsFalse(idle.IsCompleted, "Idle completion must follow terminal status publication.");
            releaseTerminal.Set();
            await idle!;
        }
        finally
        {
            releaseTerminal.Set();
            if (idle != null)
            {
                await idle!;
            }
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
                releaseCleanup.Wait();
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
            cleanupEntered.Wait();
            Assert.IsFalse(disposition.IsCompleted, "Disposition must follow ingress cleanup.");
            releaseCleanup.Set();
            await abandon;
            await disposition;
        }
        finally
        {
            releaseCleanup.Set();
            await Task.WhenAll(abandon, disposition);
        }
    }



    [TestMethod]
    public async Task Enqueue_RejectsAdditionalRequestWithoutReservationAndAcceptsFreshRequest()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> processed = [];
        var processor = new DropInstallQueueProcessor(async (request, _) =>
        {
            processed.Add(request.DisplayName);
            started.TrySetResult(true);
            await release.Task;
        }, _ => { });
        try
        {
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["first.zip"])));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.started");
            Assert.IsFalse(processor.TryEnqueue(new DroppedInstallBatchRequest(["rejected.zip"])));
            Task firstIdle = processor.WaitForIdleAsync();
            release.TrySetResult(true);
            await firstIdle;
            CollectionAssert.AreEqual(new[] { "first.zip" }, processed);
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["fresh.zip"])));
            Task freshIdle = processor.WaitForIdleAsync();
            Assert.AreNotSame(firstIdle, freshIdle, "先行実終端後の明示要求は独立したidle寿命を持ちます。");
            await freshIdle;
            CollectionAssert.AreEqual(new[] { "first.zip", "fresh.zip" }, processed);
        }
        finally
        {
            release.TrySetResult(true);
            await processor.WaitForIdleAsync();
        }
    }

    [TestMethod]
    public void CancelAll_CancelsActiveBatchAndRejectsAdditionalRequest()
    {
        List<string> startedBatches = [];
        List<DropInstallQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        Exception? backgroundFailure = null;
        var firstStarted = new ManualResetEventSlim(initialState: false);
        var tokenCancelled = new ManualResetEventSlim(initialState: false);
        var queueBecameInactive = new ManualResetEventSlim(initialState: false);
        var processor = new DropInstallQueueProcessor(
            CompleteBatch(delegate (DroppedInstallBatchRequest request, CancellationToken token)
            {
                lock (syncRoot)
                {
                    startedBatches.Add(request.DisplayName);
                }
                firstStarted.Set();
                int signaledIndex = WaitHandle.WaitAny([token.WaitHandle]);
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
            }),
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
        Assert.IsFalse(processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"])));
        firstStarted.Wait();

        processor.CancelAll();

        tokenCancelled.Wait();
        queueBecameInactive.Wait();

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
    public async Task BatchFailure_DoesNotPreventFreshRequest()
    {
        List<string> processed = [];
        List<string> errors = [];
        object syncRoot = new();
        var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new DropInstallQueueProcessor(
            CompleteBatch(delegate (DroppedInstallBatchRequest request, CancellationToken token)
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
            }),
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
        await processor.WaitForIdleAsync();
        processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"]));

        await TestUiDispatcherHost.AwaitNotificationAsync(secondFinished.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.secondFinished");

        lock (syncRoot)
        {
            CollectionAssert.AreEqual(new[] { "second.zip" }, processed);
            CollectionAssert.AreEqual(new[] { "boom" }, errors);
        }
    }

    [TestMethod]
    public async Task ThrowingFailureCallback_DoesNotStrandLifecycleOrFreshRequest()
    {
        using var firstStarted = new ManualResetEventSlim(initialState: false);
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int processCalls = 0;
        int failureCallbackCalls = 0;
        bool firstEnqueued = false;
        bool secondEnqueued = false;
        var processor = new DropInstallQueueProcessor(
            CompleteBatch((request, _) =>
            {
                Interlocked.Increment(ref processCalls);
                if (request.DisplayName == "first.zip")
                {
                    firstStarted.Set();
                    releaseFirst.Wait();
                    throw new InvalidOperationException("batch failed");
                }
                secondFinished.TrySetResult(true);
            }),
            _ => { },
            _ =>
            {
                Interlocked.Increment(ref failureCallbackCalls);
                throw new InvalidOperationException("failure callback failed");
            });
        Task? idle = null;
        try
        {
            firstEnqueued = processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\first.zip"]));
            Assert.IsTrue(firstEnqueued);
            firstStarted.Wait();
            Assert.IsFalse(processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"])));
            releaseFirst.Set();
            await processor.WaitForIdleAsync();
            secondEnqueued = processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"]));
            Assert.IsTrue(secondEnqueued);

            await TestUiDispatcherHost.AwaitNotificationAsync(secondFinished.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.secondFinished");
            idle = AssertProcessorIdleAsync(processor);
            await idle!;
            Assert.AreEqual(2, Volatile.Read(ref processCalls));
            Assert.AreEqual(1, Volatile.Read(ref failureCallbackCalls));
        }
        finally
        {
            releaseFirst.Set();
            if (firstEnqueued && idle == null)
            {
                idle = AssertProcessorIdleAsync(processor);
            }
            if (firstEnqueued)
            {
                await idle!;
            }
            if (secondEnqueued)
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(secondFinished.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.secondFinished");
            }
        }
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
            CompleteBatch(delegate (DroppedInstallBatchRequest request, CancellationToken token)
            {
                processor.ReportActiveBatchCurrentWork(1, 3, "a.zip");
                releaseBatch.Wait();
                processor.ReportActiveBatchProgress(1);
            }),
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

        Task? idle = null;
        try
        {
            processor.TryEnqueue(new DroppedInstallBatchRequest(
                [@"C:\queue\a.zip", @"C:\queue\b.zip", @"C:\queue\c.zip"]));

            currentWorkReported.Wait();
            releaseBatch.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(queueBecameInactive.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.queueBecameInactive");
            idle = AssertProcessorIdleAsync(processor);
            await idle!;

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
        finally
        {
            releaseBatch.Set();
            if (idle == null)
            {
                idle = AssertProcessorIdleAsync(processor);
            }
            await Task.WhenAll(idle!, queueBecameInactive.Task);
        }
    }

    [TestMethod]
    public async Task AcceptedMultipleInputs_RemainReadableUntilConsumerReturns()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(DropInstallQueueProcessorTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "a.bms");
        string second = Path.Combine(root, "b.bms");
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var processor = new DropInstallQueueProcessor(async (request, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            CollectionAssert.AreEqual(new[] { "a", "b" }, request.Paths.Select(File.ReadAllText).ToArray());
        }, _ => { }, exception => failure = exception);
        try
        {
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(
                [first, second], ["a.bms", "b.bms"], [root], DeleteDirectory, null)));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.started");
            Assert.IsTrue(File.Exists(first));
            Assert.IsTrue(File.Exists(second));
            release.TrySetResult(true);
            await processor.WaitForIdleAsync();
            Assert.IsNull(failure);
            Assert.IsFalse(Directory.Exists(root));
        }
        finally
        {
            release.TrySetResult(true);
            await processor.WaitForIdleAsync();
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task CancelAll_DeletesAcceptedOwnedRootButNeverExternalOriginal()
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
        DropInstallQueueProcessor? processor = null;
        Task? idle = null;
        try
        {
            processor = new DropInstallQueueProcessor(
                CompleteBatch((_, token) =>
                {
                    activeStarted.Set();
                    WaitHandle.WaitAny([token.WaitHandle, releaseActive.WaitHandle]);
                    token.ThrowIfCancellationRequested();
                }),
                _ => { });
            processor.TryEnqueue(CreateOwnedRequest(activeRoot, original, "active.zip"));
            activeStarted.Wait();
            DroppedInstallBatchRequest rejected = CreateOwnedRequest(pendingRoot, original, "pending.zip");
            Assert.IsFalse(processor.TryEnqueue(rejected));
            Assert.IsTrue(rejected.TryAbandonUnconsumedSources());

            processor.CancelAll();

            idle = AssertProcessorIdleAsync(processor);
            await idle!;
            Assert.IsFalse(Directory.Exists(pendingRoot));
            Assert.IsFalse(Directory.Exists(activeRoot));
            Assert.IsTrue(File.Exists(original));
        }
        finally
        {
            releaseActive.Set();
            if (processor != null)
            {
                idle ??= AssertProcessorIdleAsync(processor);
                await idle!;
            }
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
                CompleteBatch((request, _) => Assert.IsTrue(request.TransferSourceOwnershipToInstaller())),
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
    public async Task CancelAll_BlockedAcceptedCleanupDefersIdleAndFreshAdmission()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(DropInstallQueueProcessorTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim(false);
        int processCalls = 0;
        bool transferredAfterCancellation = false;
        var processor = new DropInstallQueueProcessor(async (request, token) =>
        {
            Interlocked.Increment(ref processCalls);
            if (request.DisplayName == "active.zip")
            {
                started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    transferredAfterCancellation = request.TransferSourceOwnershipToInstaller();
                    cancelled.TrySetResult(true);
                }
            }
        }, _ => { });
        try
        {
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(
                [Path.Combine(root, "active.zip")], ["active.zip"], [root],
                path =>
                {
                    cleanupStarted.TrySetResult(true);
                    releaseCleanup.Wait();
                    DeleteDirectory(path);
                }, null)));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.started");
            processor.CancelAll();
            await TestUiDispatcherHost.AwaitNotificationAsync(cancelled.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.cancelled");
            Assert.IsFalse(transferredAfterCancellation);
            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupStarted.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.cleanupStarted");
            Assert.IsFalse(processor.IsIdle);
            Assert.IsFalse(processor.WaitForIdleAsync().IsCompleted);
            Assert.IsFalse(processor.TryEnqueue(new DroppedInstallBatchRequest(["rejected.zip"])));
            releaseCleanup.Set();
            await processor.WaitForIdleAsync();
            Assert.AreEqual(1, processCalls);
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["fresh.zip"])));
            await processor.WaitForIdleAsync();
            Assert.AreEqual(2, processCalls);
        }
        finally
        {
            releaseCleanup.Set();
            processor.CancelAll();
            await processor.WaitForIdleAsync();
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
                CompleteBatch((request, token) =>
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
                }),
                _ => { },
                exception => backgroundFailure = exception);
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(
                [Path.Combine(failedCleanupRoot, "active.zip")],
                ["active.zip"],
                [failedCleanupRoot],
                _ => throw new IOException("cleanup failed"),
                (_, exception) =>
                {
                    if (exception is IOException)
                    {
                        cleanupFailureReported.TrySetResult(true);
                    }
                })));
            activeStarted.Wait();

            processor.CancelAll();

            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupFailureReported.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.cleanupFailureReported");
            await AssertProcessorIdleAsync(processor);
            Assert.IsTrue(Directory.Exists(failedCleanupRoot));
            Assert.IsNull(backgroundFailure);
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["fresh.zip"])));
            await TestUiDispatcherHost.AwaitNotificationAsync(freshProcessed.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.freshProcessed");
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
