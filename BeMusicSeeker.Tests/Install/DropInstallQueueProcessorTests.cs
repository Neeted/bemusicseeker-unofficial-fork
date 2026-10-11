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

    /// <summary>終端状態の通知先を保持している間はidleを完了せず、通知返却後にだけ完了する順序違反を検出します。</summary>
    [TestMethod]
    public async Task WaitForIdleAsync_CompletesAfterTerminalStatusNotificationReturns()
    {
        var terminalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseTerminal = new ManualResetEventSlim(false);
        var processor = new DropInstallQueueProcessor(
            CompleteBatch((_, _) => { }),
            snapshot =>
            {
                if (!snapshot.IsActive)
                {
                    terminalEntered.TrySetResult();
                    releaseTerminal.Wait();
                }
            });
        Task? idle = null;
        try
        {
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["chart.zip"])));
            idle = processor.WaitForIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(terminalEntered.Task, processor.WaitForIdleAsync(), "drop-queue.terminalEntered");
            Assert.IsFalse(idle.IsCompleted, "Idle completion must follow terminal status publication.");
            releaseTerminal.Set();
            await idle;
        }
        finally
        {
            releaseTerminal.Set();
            if (idle != null)
            {
                await idle;
            }
        }
    }

    /// <summary>未消費入力の回収を保持している間はdispositionを完了せず、実回収の終端より早い完了を検出します。</summary>
    [TestMethod]
    public async Task RequestDisposition_CompletesAfterAbandonmentCleanupReturns()
    {
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim(false);
        var request = new DroppedInstallBatchRequest(
            ["chart.zip"],
            ["chart.zip"],
            ["owned-root"],
            _ =>
            {
                cleanupEntered.TrySetResult();
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
            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupEntered.Task, abandon, "drop-queue.cleanupEntered");
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



    /// <summary>受理処理中の追加要求は予約せず拒否し、先行実終端後の明示要求だけを独立idleで処理する受付漏れを検出します。</summary>
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

    /// <summary>追加要求を拒否した受理バッチへ取消を渡して終端まで待ち、取消通知の欠落や拒否要求の実行を検出します。</summary>
    [TestMethod]
    public async Task CancelAll_CancelsActiveBatchAndRejectsAdditionalRequest()
    {
        List<string> startedBatches = [];
        List<DropInstallQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueBecameInactive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new DropInstallQueueProcessor(
            CompleteBatch(delegate (DroppedInstallBatchRequest request, CancellationToken token)
            {
                lock (syncRoot)
                {
                    startedBatches.Add(request.DisplayName);
                }
                firstStarted.TrySetResult();
                token.WaitHandle.WaitOne();
                tokenCancelled.TrySetResult();
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
                    queueBecameInactive.TrySetResult();
                }
            });

        try
        {
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\first.zip"])));
            Assert.IsFalse(processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"])));
            await TestUiDispatcherHost.AwaitNotificationAsync(firstStarted.Task, processor.WaitForIdleAsync(), "drop-queue.active");
            processor.CancelAll();
            Task idle = processor.WaitForIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(tokenCancelled.Task, idle, "drop-queue.token-cancelled");
            await TestUiDispatcherHost.AwaitNotificationAsync(queueBecameInactive.Task, idle, "drop-queue.terminal");
            await idle;
            lock (syncRoot)
            {
                CollectionAssert.AreEqual(new[] { "first.zip" }, startedBatches);
                Assert.IsTrue(snapshots.Any(snapshot => snapshot.IsCancellationRequested || !snapshot.CanCancel));
            }
        }
        finally
        {
            processor.CancelAll();
            await processor.WaitForIdleAsync();
        }
    }

    /// <summary>先行バッチの例外を一回報告し、次の明示要求を実終端まで処理することから失敗後の受付残留を検出します。</summary>
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

        try
        {
            processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\first.zip"]));
            await processor.WaitForIdleAsync();
            processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"]));

            await TestUiDispatcherHost.AwaitNotificationAsync(secondFinished.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.secondFinished");

            await processor.WaitForIdleAsync();

            lock (syncRoot)
            {
                CollectionAssert.AreEqual(new[] { "second.zip" }, processed);
                CollectionAssert.AreEqual(new[] { "boom" }, errors);
            }
        }
        finally
        {
            processor.CancelAll();
            await processor.WaitForIdleAsync();
        }
    }

    /// <summary>失敗の通知callback自身が例外でもidleを完了して次要求を処理し、通知失敗でlifecycleが残る誤りを検出します。</summary>
    [TestMethod]
    public async Task ThrowingFailureCallback_DoesNotStrandLifecycleOrFreshRequest()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    firstStarted.TrySetResult();
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
            await TestUiDispatcherHost.AwaitNotificationAsync(firstStarted.Task, processor.WaitForIdleAsync(), "drop-queue.firstStarted");
            Assert.IsFalse(processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"])));
            releaseFirst.Set();
            await processor.WaitForIdleAsync();
            secondEnqueued = processor.TryEnqueue(new DroppedInstallBatchRequest([@"C:\queue\second.zip"]));
            Assert.IsTrue(secondEnqueued);

            await TestUiDispatcherHost.AwaitNotificationAsync(secondFinished.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.secondFinished");
            idle = processor.WaitForIdleAsync();
            await idle;
            Assert.AreEqual(2, Volatile.Read(ref processCalls));
            Assert.AreEqual(1, Volatile.Read(ref failureCallbackCalls));
        }
        finally
        {
            releaseFirst.Set();
            if (firstEnqueued)
            {
                idle ??= processor.WaitForIdleAsync();
                await idle;
            }
            if (secondEnqueued)
            {
                await processor.WaitForIdleAsync();
            }
        }
    }

    /// <summary>バッチ中の現在対象と完了数を別に公開し、対象完了・バッチ終端で表示値を消すことから古い進捗の残留を検出します。</summary>
    [TestMethod]
    public async Task ReportActiveBatchCurrentWork_ReportsAndClearsCurrentWork()
    {
        List<DropInstallQueueStatusSnapshot> snapshots = [];
        object syncRoot = new();
        var currentWorkReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBatch = new ManualResetEventSlim(initialState: false);
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
                    currentWorkReported.TrySetResult();
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

            await TestUiDispatcherHost.AwaitNotificationAsync(currentWorkReported.Task, processor.WaitForIdleAsync(), "drop-queue.currentWorkReported");
            releaseBatch.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(queueBecameInactive.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.queueBecameInactive");
            idle = processor.WaitForIdleAsync();
            await idle;

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
                idle = processor.WaitForIdleAsync();
            }
            await idle;
        }
    }

    /// <summary>複数入力の所有rootはconsumerが読み終えるまで残り、読取り完了前の回収と処理終端後の回収漏れを検出します。</summary>
    /// <remarks>実rootと入力ファイルでqueueの所有移譲・回収を観測します。導入の保存と物理変更はinstaller側の代表ケースが担います。</remarks>
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

    /// <summary>取消した受理rootと拒否要求のrootだけを回収し、外部元ファイルへの誤った削除を検出します。</summary>
    /// <remarks>実rootと入力ファイルでqueueの所有移譲・回収を観測します。導入の保存と物理変更はinstaller側の代表ケースが担います。</remarks>
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
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseActive = new ManualResetEventSlim(false);
        DropInstallQueueProcessor? processor = null;
        Task? idle = null;
        try
        {
            processor = new DropInstallQueueProcessor(
                CompleteBatch((_, token) =>
                {
                    activeStarted.TrySetResult();
                    WaitHandle.WaitAny([token.WaitHandle, releaseActive.WaitHandle]);
                    token.ThrowIfCancellationRequested();
                }),
                _ => { });
            processor.TryEnqueue(CreateOwnedRequest(activeRoot, original, "active.zip"));
            await TestUiDispatcherHost.AwaitNotificationAsync(activeStarted.Task, processor.WaitForIdleAsync(), "drop-queue.activeStarted");
            DroppedInstallBatchRequest rejected = CreateOwnedRequest(pendingRoot, original, "pending.zip");
            Assert.IsFalse(processor.TryEnqueue(rejected));
            Assert.IsTrue(rejected.TryAbandonUnconsumedSources());

            processor.CancelAll();

            idle = processor.WaitForIdleAsync();
            await idle;
            Assert.IsFalse(Directory.Exists(pendingRoot));
            Assert.IsFalse(Directory.Exists(activeRoot));
            Assert.IsTrue(File.Exists(original));
        }
        finally
        {
            releaseActive.Set();
            if (processor != null)
            {
                idle ??= processor.WaitForIdleAsync();
                await idle;
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>installerへ所有を移したrootをqueue終端では削除せず、移譲後の二重回収を検出します。</summary>
    /// <remarks>実rootと入力ファイルでqueueの所有移譲・回収を観測します。導入の保存と物理変更はinstaller側の代表ケースが担います。</remarks>
    [TestMethod]
    public async Task InstallerHandoff_PreventsQueueFinallyFromDeletingOwnedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(DropInstallQueueProcessorTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DropInstallQueueProcessor? processor = null;
        try
        {
            processor = new DropInstallQueueProcessor(
                CompleteBatch((request, _) => Assert.IsTrue(request.TransferSourceOwnershipToInstaller())),
                _ => { });
            processor.TryEnqueue(new DroppedInstallBatchRequest(
                [Path.Combine(root, "chart.bms")],
                ["chart.bms"],
                [root],
                DeleteDirectory,
                null));

            await processor.WaitForIdleAsync();
            Assert.IsTrue(Directory.Exists(root));
        }
        finally
        {
            if (processor != null)
            {
                processor.CancelAll();
                await processor.WaitForIdleAsync();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>取消後の入力回収を保持する間はidleと再受付を止め、所有移譲の拒否と解放後の新規受付から早期終端を検出します。</summary>
    /// <remarks>実rootと入力ファイルでqueueの所有移譲・回収を観測します。導入の保存と物理変更はinstaller側の代表ケースが担います。</remarks>
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

    /// <summary>取消時のroot削除失敗は専用通知し、残ったrootを保持しても次要求の実終端を可能にすることから回収失敗による受付残留を検出します。</summary>
    /// <remarks>実rootと入力ファイルでqueueの所有移譲・回収を観測します。導入の保存と物理変更はinstaller側の代表ケースが担います。</remarks>
    [TestMethod]
    public async Task CancelAll_CleanupFailureStillCompletesDrainAndAcceptsFreshBatch()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(DropInstallQueueProcessorTests),
            Guid.NewGuid().ToString("N"));
        string failedCleanupRoot = Path.Combine(root, "failed-cleanup");
        Directory.CreateDirectory(failedCleanupRoot);
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailureReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? backgroundFailure = null;
        DropInstallQueueProcessor? processor = null;
        try
        {
            processor = new DropInstallQueueProcessor(
                CompleteBatch((request, token) =>
                {
                    if (request.DisplayName == "active.zip")
                    {
                        activeStarted.TrySetResult();
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
            await TestUiDispatcherHost.AwaitNotificationAsync(activeStarted.Task, processor.WaitForIdleAsync(), "drop-queue.activeStarted");

            processor.CancelAll();

            await TestUiDispatcherHost.AwaitNotificationAsync(cleanupFailureReported.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.cleanupFailureReported");
            await processor.WaitForIdleAsync();
            Assert.IsTrue(Directory.Exists(failedCleanupRoot));
            Assert.IsNull(backgroundFailure);
            Assert.IsTrue(processor.TryEnqueue(new DroppedInstallBatchRequest(["fresh.zip"])));
            await TestUiDispatcherHost.AwaitNotificationAsync(freshProcessed.Task, processor.WaitForIdleAsync(), "DropInstallQueueProcessorTests.freshProcessed");
            await processor.WaitForIdleAsync();
        }
        finally
        {
            if (processor != null)
            {
                processor.CancelAll();
                await processor.WaitForIdleAsync();
            }
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
            // 存在確認とfixture削除の間にqueue側の回収が先に完了する場合があります。
        }
    }
}
