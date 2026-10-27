using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupUpdateWorkflowOwnerTests
{
    /// <summary>
    /// 保証：起動時更新確認は一度だけ受理し、旧一時物の清掃後に確認して更新なしの終端を一度公開する。
    /// </summary>
    [TestMethod]
    public async Task Start_CleansUpChecksAndPublishesNoUpdateTerminal()
    {
        var events = new List<string>();
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () =>
            {
                events.Add("check");
                return Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
            },
            cleanup: () => events.Add("cleanup"),
            schedule: action =>
            {
                events.Add("schedule");
                return StartLongRunningAsync(action);
            });
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        Assert.IsFalse(owner.Start());
        await WaitForCompletion(owner);

        CollectionAssert.AreEqual(new[] { "schedule", "cleanup", "check", "terminal" }, events);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.NoUpdate, receipt.Outcome);
    }

    /// <summary>
    /// 保証：journal・receiptなしで同期ロックを排他保持する新版起動時も実清掃から更新なし終端へ一度進み、
    /// lock/currentを保持して古いdownloadsを回収する。清掃の省略とロック解放待ちによる停滞を検出する。
    /// </summary>
    [TestMethod]
    public async Task Start_CleansUpWithHeldTransactionLockAndPublishesNoUpdateTerminal()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StartupUpdateWorkflowOwnerTests", Guid.NewGuid().ToString("N"));
        string workRoot = Path.Combine(root, "update_work");
        string currentDirectory = Path.Combine(workRoot, "current");
        string updaterPath = Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe");
        string lockPath = Path.Combine(workRoot, "update-transaction.lock");
        int checkCount = 0;
        int terminalCount = 0;
        int failurePresentationCount = 0;
        Task worker = Task.CompletedTask;
        Exception? primaryFailure = null;

        try
        {
            Directory.CreateDirectory(currentDirectory);
            Directory.CreateDirectory(Path.Combine(workRoot, "downloads"));
            File.WriteAllText(updaterPath, "running-updater");
            File.WriteAllText(Path.Combine(workRoot, "downloads", "stale.zip"), "stale-package");
            using FileStream heldLock = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                new UnexpectedUpdaterProcessGateway());
            StartupUpdateWorkflowOwner owner = CreateOwner(
                () =>
                {
                    Interlocked.Increment(ref checkCount);
                    return Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
                },
                cleanup: service.CleanupPreviousWorkDirectory,
                schedule: action => worker = StartLongRunningAsync(action));
            StartupUpdateWorkflowCompletionReceipt? receipt = null;
            owner.TerminalPublished += published =>
            {
                receipt = published;
                Interlocked.Increment(ref terminalCount);
            };
            owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);

            Assert.IsTrue(owner.Start());
            // 排他を解放する前に実workerとownerの両終端を待つ。
            await worker;
            await WaitForCompletion(owner);

            Assert.IsNotNull(receipt);
            Assert.AreEqual(StartupUpdateWorkflowOutcome.NoUpdate, receipt.Outcome);
            Assert.AreEqual(1, checkCount);
            Assert.AreEqual(1, terminalCount);
            Assert.AreEqual(0, failurePresentationCount);
            Assert.IsTrue(File.Exists(lockPath));
            Assert.AreEqual("running-updater", File.ReadAllText(updaterPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(workRoot, "downloads")));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await worker;
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString();
            }
            finally
            {
                DeleteCleanupTestDirectory(root, primaryFailure);
            }
        }
    }

    /// <summary>
    /// 保証：実清掃で古いdownloadの排他保持による削除失敗が起きたら更新確認へ進まず、
    /// 実際の削除例外を失敗提示と終端へ渡す。清掃例外の一括抑制で成功へ進む誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task CleanupFailurePublishesFailureBeforeCheckingForUpdates()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StartupUpdateWorkflowOwnerTests", Guid.NewGuid().ToString("N"));
        string downloadsDirectory = Path.Combine(root, "update_work", "downloads");
        string stalePackagePath = Path.Combine(downloadsDirectory, "stale.zip");
        int checkCount = 0;
        int failurePresentationCount = 0;
        Exception? actualCleanupFailure = null;
        Exception? presentedFailure = null;
        Task worker = Task.CompletedTask;
        Exception? primaryFailure = null;

        try
        {
            Directory.CreateDirectory(downloadsDirectory);
            File.WriteAllText(stalePackagePath, "stale-package");
            using FileStream heldDownload = File.Open(stalePackagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                new UnexpectedUpdaterProcessGateway());
            StartupUpdateWorkflowOwner owner = CreateOwner(
                () =>
                {
                    Interlocked.Increment(ref checkCount);
                    return Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
                },
                cleanup: () =>
                {
                    try
                    {
                        service.CleanupPreviousWorkDirectory();
                    }
                    catch (Exception exception)
                    {
                        actualCleanupFailure = exception;
                        throw;
                    }
                },
                schedule: action => worker = StartLongRunningAsync(action));
            StartupUpdateWorkflowCompletionReceipt? receipt = null;
            owner.TerminalPublished += published => receipt = published;
            owner.FailurePresentationRequested += exception =>
            {
                presentedFailure = exception;
                Interlocked.Increment(ref failurePresentationCount);
            };

            Assert.IsTrue(owner.Start());
            await worker;
            await WaitForCompletion(owner);

            Assert.IsNotNull(receipt);
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
            Assert.IsNotNull(actualCleanupFailure);
            Assert.IsInstanceOfType<IOException>(actualCleanupFailure);
            StringAssert.Contains(actualCleanupFailure.Message, "stale.zip");
            Assert.AreSame(actualCleanupFailure, receipt.Exception);
            Assert.AreSame(actualCleanupFailure, presentedFailure);
            Assert.AreEqual(1, failurePresentationCount);
            Assert.AreEqual(0, checkCount);
            Assert.IsTrue(File.Exists(stalePackagePath));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await worker;
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString();
            }
            finally
            {
                DeleteCleanupTestDirectory(root, primaryFailure);
            }
        }
    }

    /// <summary>
    /// 保証：永続した更新失敗の提示が成功してから記録を既読化し、提示前に失敗記録を消さない。
    /// </summary>
    [TestMethod]
    public async Task FailurePresentationAcknowledgesDurableReceiptOnlyAfterPresentationSucceeds()
    {
        int presentationCount = 0;
        bool acknowledged = false;
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () => acknowledged = true);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            cleanup: () => throw receiptException,
            schedule: action => StartLongRunningAsync(action));
        owner.FailurePresentationRequested += exception =>
        {
            Assert.AreSame(receiptException, exception);
            Interlocked.Increment(ref presentationCount);
        };

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        await owner.WaitForTerminalAsync();
        Assert.AreEqual(1, presentationCount);
        Assert.IsTrue(acknowledged);
    }

    /// <summary>
    /// 保証：更新失敗の提示そのものが失敗した場合は永続記録を既読化せず、次回に提示できる事実を残す。
    /// </summary>
    [TestMethod]
    public async Task FailurePresentationKeepsDurableReceiptWhenPresentationFails()
    {
        bool acknowledged = false;
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () => acknowledged = true);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            cleanup: () => throw receiptException,
            schedule: action => StartLongRunningAsync(action));
        owner.FailurePresentationRequested += _ => throw new InvalidOperationException("presentation failed");

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        await owner.WaitForTerminalAsync();
        Assert.IsFalse(acknowledged);
    }

    /// <summary>
    /// 保証：提示側が既読化を延期した更新失敗は終端後も未読として残し、表示要求だけで確認済みとしない。
    /// </summary>
    [TestMethod]
    public async Task FailurePresentationKeepsDurableReceiptWhenPresentationIsDeferred()
    {
        bool acknowledged = false;
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () => acknowledged = true);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            cleanup: () => throw receiptException,
            schedule: action => StartLongRunningAsync(action));
        owner.FailurePresentationRequested += _ => receiptException.DeferAcknowledge();

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        await owner.WaitForTerminalAsync();
        Assert.IsFalse(acknowledged);
    }

    /// <summary>
    /// 保証：利用者が更新提示を取消した場合は取消終端を返し、ダウンロードや適用へ勝手に進まない。
    /// </summary>
    [TestMethod]
    public async Task AvailableUpdate_CancelledPresentationPublishesCancelledTerminal()
    {
        var events = new List<string>();
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request =>
        {
            events.Add("present");
            request.Complete(null);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);

        CollectionAssert.AreEqual(new[] { "present", "terminal" }, events);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, receipt.Outcome);
    }

    /// <summary>
    /// 保証：更新承認後は取得・起動準備・updater開始・終了準備を経て適用終端を公開してからアプリ終了を要求する。
    /// </summary>
    [TestMethod]
    public async Task AvailableUpdate_CompletesDownloadPrepareShutdownStartBeforeApplicationShutdown()
    {
        var events = new List<string>();
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = Task.CompletedTask;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset =>
            {
                events.Add("download");
                return Task.FromResult("package.zip");
            },
            prepare: packagePath =>
            {
                events.Add("prepare");
                return new FakePreparedUpdaterLaunch(() =>
                {
                    events.Add("start");
                    return new UpdaterLaunchReceipt();
                });
            },
            schedule: action => worker = StartLongRunningAsync(action));
        owner.PresentationRequested += request =>
        {
            events.Add("present");
            request.Complete(CreateAvailableResult().Assets[0]);
        };
        owner.BindShutdownPreparation(reason =>
        {
            events.Add("shutdown_prepare");
            return Task.FromResult(new ShutdownPreparationResult(reason, 12L, false, 0));
        });
        owner.ApplicationShutdownRequested += () =>
        {
            events.Add("application_shutdown");
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        try
        {
            Assert.IsTrue(owner.Start());
            await WaitForCompletion(owner);
            await TestUiDispatcherHost.AwaitNotificationAsync(applicationShutdown.Task, worker, nameof(worker));

            CollectionAssert.AreEqual(
                new[] { "present", "download", "prepare", "start", "shutdown_prepare", "terminal", "application_shutdown" },
                events);
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, receipt.Outcome);
            Assert.IsTrue(receipt.ShutdownPrepared);
        }
        finally
        {
            await worker;
        }
    }

    /// <summary>
    /// 保証：予約したUI処理が実行されるまでは終了準備を開始せず、提示・終了準備・終端・終了要求の受信を待つ。
    /// </summary>
    [TestMethod]
    public async Task AvailableUpdate_WaitsForUiDispatcherBeforeShutdownPreparation()
    {
        var dispatched = new TaskCompletionSource<Action>[]
        {
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        int dispatchIndex = 0;
        int consumedDispatchCount = 0;
        int shutdownPreparationCount = 0;
        Task worker = Task.CompletedTask;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            schedule: action => worker = StartLongRunningAsync(action),
            dispatch: action =>
            {
                int index = Interlocked.Increment(ref dispatchIndex) - 1;
                if (index >= dispatched.Length)
                {
                    throw new InvalidOperationException("Unexpected startup update UI dispatch.");
                }
                dispatched[index].TrySetResult(action);
            });
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(reason =>
        {
            Interlocked.Increment(ref shutdownPreparationCount);
            return Task.FromResult(new ShutdownPreparationResult(reason, 1L, false, 0));
        });

        try
        {
            Assert.IsTrue(owner.Start());
            await TestUiDispatcherHost.AwaitNotificationAsync(dispatched[0].Task, worker, nameof(worker));
            Action presentationAction = await dispatched[0].Task;
            Assert.IsFalse(owner.IsIdle);
            Assert.AreEqual(0, Volatile.Read(ref shutdownPreparationCount));

            consumedDispatchCount++;
            presentationAction();
            await TestUiDispatcherHost.AwaitNotificationAsync(dispatched[1].Task, worker, nameof(worker));
            Action preparationAction = await dispatched[1].Task;
            Assert.AreEqual(0, Volatile.Read(ref shutdownPreparationCount));
            consumedDispatchCount++;
            preparationAction();
            await TestUiDispatcherHost.AwaitNotificationAsync(dispatched[2].Task, worker, nameof(worker));
            Action terminalAction = await dispatched[2].Task;
            consumedDispatchCount++;
            terminalAction();
            await TestUiDispatcherHost.AwaitNotificationAsync(dispatched[3].Task, worker, nameof(worker));
            Action shutdownAction = await dispatched[3].Task;
            consumedDispatchCount++;
            shutdownAction();
            await WaitForCompletion(owner);

            Assert.AreEqual(1, Volatile.Read(ref shutdownPreparationCount));
        }
        finally
        {
            while (consumedDispatchCount < dispatched.Length)
            {
                TaskCompletionSource<Action> pending = dispatched[consumedDispatchCount];
                await TestUiDispatcherHost.AwaitNotificationAsync(pending.Task, worker, nameof(worker));
                consumedDispatchCount++;
                (await pending.Task)();
            }
            await worker;
        }
    }

    /// <summary>
    /// 保証：更新確認の完了前に閉鎖されたら、後から更新が見つかっても提示せず閉鎖終端で終了する。
    /// </summary>
    [TestMethod]
    public async Task ClosingWhileCheckIsPendingSuppressesPresentation()
    {
        var checkCompletion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int presentationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => checkCompletion.Task,
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => Interlocked.Increment(ref presentationCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        owner.NotifyClosing();
        checkCompletion.SetResult(CreateAvailableResult());

        await WaitForCompletion(owner);
        Assert.AreEqual(0, presentationCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, receipt.Outcome);
    }

    /// <summary>
    /// 保証：ダウンロード中に閉鎖されたら取得Taskを回収して閉鎖終端へ進み、終了準備を新しく開始しない。
    /// </summary>
    [TestMethod]
    public async Task ClosingWhileDownloadIsPendingCancelsBeforeShutdownPreparation()
    {
        var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int shutdownPreparationCount = 0;
        Task worker = Task.CompletedTask;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset =>
            {
                downloadStarted.TrySetResult(true);
                return downloadCompletion.Task;
            },
            schedule: action => worker = StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ =>
        {
            Interlocked.Increment(ref shutdownPreparationCount);
            return Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        });
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        try
        {
            Assert.IsTrue(owner.Start());
            await TestUiDispatcherHost.AwaitNotificationAsync(downloadStarted.Task, worker, nameof(worker));
            Assert.IsTrue(owner.NotifyClosing());
            downloadCompletion.SetResult("package.zip");

            await WaitForCompletion(owner);
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, receipt.Outcome);
            Assert.AreEqual(0, shutdownPreparationCount);
        }
        finally
        {
            owner.NotifyClosing();
            downloadCompletion.TrySetResult("package.zip");
            await worker;
        }
    }

    /// <summary>
    /// 保証：更新取得の失敗はエラー提示と失敗終端へ返し、終了準備やアプリ終了を要求しない。
    /// </summary>
    [TestMethod]
    public async Task DownloadFailureRequestsErrorPresentationWithoutApplicationShutdown()
    {
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => throw new InvalidOperationException("download failed"),
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);

        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(0, shutdownCount);
        Assert.IsFalse(applicationShutdown.Task.IsCompleted);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsFalse(receipt.ShutdownPrepared);
    }

    /// <summary>
    /// 保証：updater開始後の終了準備が失敗した場合は失敗を提示し、適用成功へ読み替えずアプリ終了を要求する。
    /// </summary>
    [TestMethod]
    public async Task ShutdownPreparationFailureRequestsErrorPresentationAndApplicationShutdown()
    {
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        Task worker = Task.CompletedTask;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => Task.FromResult("package.zip"),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => new UpdaterLaunchReceipt()),
            schedule: action => worker = StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        try
        {
            Assert.IsTrue(owner.Start());
            await WaitForCompletion(owner);

            Assert.AreEqual(1, failurePresentationCount);
            await TestUiDispatcherHost.AwaitNotificationAsync(applicationShutdown.Task, worker, nameof(worker));
            Assert.AreEqual(1, Volatile.Read(ref shutdownCount));
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
            Assert.IsFalse(receipt.ShutdownPrepared);
        }
        finally
        {
            await worker;
        }
    }

    /// <summary>
    /// 保証：必須の終了準備窓口がない場合は更新を失敗として提示し、成功した終了準備を捏造せずアプリを終端する。
    /// </summary>
    [TestMethod]
    public async Task AvailableUpdateWithoutShutdownPreparationPortFailsExplicitly()
    {
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        Task worker = Task.CompletedTask;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => new UpdaterLaunchReceipt()),
            schedule: action => worker = StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        try
        {
            Assert.IsTrue(owner.Start());
            await WaitForCompletion(owner);

            Assert.AreEqual(1, failurePresentationCount);
            await TestUiDispatcherHost.AwaitNotificationAsync(applicationShutdown.Task, worker, nameof(worker));
            Assert.AreEqual(1, Volatile.Read(ref shutdownCount));
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
            Assert.IsFalse(receipt.ShutdownPrepared);
        }
        finally
        {
            await worker;
        }
    }

    /// <summary>
    /// 保証：更新確認が活動中は終了準備窓口の差替えを拒否し、処理途中に別の終了先へ接続しない。
    /// </summary>
    [TestMethod]
    public async Task BindShutdownPreparationRejectsReplacementWhileActive()
    {
        var checkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkCompletion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = Task.CompletedTask;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () =>
            {
                checkStarted.TrySetResult(true);
                return checkCompletion.Task;
            },
            schedule: action => worker = StartLongRunningAsync(action));
        Func<string, Task<ShutdownPreparationResult>> firstPort = _ => Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        owner.BindShutdownPreparation(firstPort);

        try
        {
            Assert.IsTrue(owner.Start());
            await TestUiDispatcherHost.AwaitNotificationAsync(checkStarted.Task, worker, nameof(worker));
            Assert.ThrowsException<InvalidOperationException>(() => owner.BindShutdownPreparation(_ => Task.FromResult(new ShutdownPreparationResult("update", 2L, false, 0))));

            owner.NotifyClosing();
            checkCompletion.SetResult(CreateAvailableResult());
            await owner.WaitForIdleAsync();
        }
        finally
        {
            owner.NotifyClosing();
            checkCompletion.TrySetResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
            await worker;
        }
    }

    /// <summary>
    /// 保証：終了準備の失敗を遅れたUI受信にも保持し、起動中止を一度行ってから失敗提示とアプリ終了を実際に受信する。
    /// </summary>
    [TestMethod]
    public async Task ShutdownPreparationFailureRetainsBoundaryForDelayedUiNotification()
    {
        var failurePresentation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int launchAbortCount = 0;
        int applicationShutdownCount = 0;
        bool shutdownPreparationWasObserved = false;
        Task worker = Task.CompletedTask;
        var consumers = new System.Collections.Concurrent.ConcurrentQueue<Task>();
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            prepare: packagePath => new FakePreparedUpdaterLaunch(
                () => new UpdaterLaunchReceipt(() => Interlocked.Increment(ref launchAbortCount))),
            schedule: action => worker = StartLongRunningAsync(action),
            dispatch: action => consumers.Enqueue(Task.Run(action)));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ =>
        {
            shutdownPreparationWasObserved = owner.IsShutdownPreparationStarted;
            failurePresentation.TrySetResult(true);
        };
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref applicationShutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        try
        {
            Assert.IsTrue(owner.Start());
            // worker終端で予約集合が確定します。遅延したUI受信は、workerではなく実consumer Taskで観測します。
            await worker;
            var received = Task.WhenAll(consumers.ToArray());
            await TestUiDispatcherHost.AwaitNotificationAsync(failurePresentation.Task, received, "delayed-update-failure-presentation");
            await WaitForCompletion(owner);
            Assert.IsTrue(shutdownPreparationWasObserved);
            Assert.AreEqual(1, launchAbortCount);
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
            await TestUiDispatcherHost.AwaitNotificationAsync(applicationShutdown.Task, received, "delayed-update-shutdown");
            Assert.AreEqual(1, Volatile.Read(ref applicationShutdownCount));
        }
        finally
        {
            await worker;
            await Task.WhenAll(consumers.ToArray());
        }
    }

    /// <summary>
    /// 保証：終了準備前にupdater起動が失敗すると取得物を削除して失敗を提示し、アプリを開いたまま終了準備を開始しない。
    /// </summary>
    [TestMethod]
    public async Task ProcessStartFailureBeforeShutdownPreparationDeletesPackageAndKeepsApplicationOpen()
    {
        var events = new List<string>();
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => Task.FromResult("package.zip"),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => null!),
            delete: packagePath => events.Add("delete"),
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        int shutdownPreparationCount = 0;
        owner.BindShutdownPreparation(_ =>
        {
            Interlocked.Increment(ref shutdownPreparationCount);
            return Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        });
        owner.ApplicationShutdownRequested += () =>
        {
            events.Add("application_shutdown");
            applicationShutdown.TrySetResult(true);
        };
        owner.FailurePresentationRequested += _ => events.Add("failure_presentation");
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);

        CollectionAssert.AreEqual(new[] { "delete", "failure_presentation", "terminal" }, events);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.AreEqual(0, shutdownPreparationCount);
        Assert.IsFalse(receipt.ShutdownPrepared);
        Assert.IsFalse(applicationShutdown.Task.IsCompleted);
    }

    /// <summary>
    /// 保証：schedulerへの投入失敗でも失敗提示と終端を公開してidleへ戻り、開始済み状態を永久に残さない。
    /// </summary>
    [TestMethod]
    public async Task SchedulerFailurePublishesFailureAndLeavesOwnerIdle()
    {
        int failurePresentationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            schedule: action => throw new InvalidOperationException("scheduler failed"));
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        Assert.IsTrue(owner.IsIdle);
        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
    }

    private static Task StartLongRunningAsync(Func<Task> action)
    {
        return Task.Factory.StartNew(
                action,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    private static async Task WaitForCompletion(StartupUpdateWorkflowOwner owner)
    {
        await Task.WhenAll(owner.WaitForIdleAsync(), owner.WaitForTerminalAsync());
    }

    private static StartupUpdateWorkflowOwner CreateOwner(
        Func<Task<UpdateCheckResult>> check,
        Func<UpdateAssetInfo, Task<string>>? download = null,
        Func<string, IPreparedUpdaterLaunch>? prepare = null,
        Action? cleanup = null,
        Action<string>? delete = null,
        Func<Func<Task>, Task>? schedule = null,
        Action<Action>? dispatch = null)
    {
        return new StartupUpdateWorkflowOwner(
            check,
            download ?? (asset => Task.FromResult("package.zip")),
            prepare ?? (packagePath => new FakePreparedUpdaterLaunch(() => new UpdaterLaunchReceipt())),
            cleanup ?? (() => { }),
            delete ?? (_ => { }),
            schedule ?? (action => StartLongRunningAsync(action)),
            dispatch ?? (action => action()));
    }

    private static UpdateCheckResult CreateAvailableResult()
    {
        return UpdateCheckResult.Available(
            new Version(2, 0, 0, 0),
            "1.0.0.0",
            "2.0.0.0",
            [new UpdateAssetInfo
            {
                Kind = "app",
                Label = "App",
                FileName = "app.zip",
                Url = "https://example.test/app.zip",
                Sha256 = new string('a', 64),
                SizeBytes = 1,
                IncludesChartInfoMetadata = false
            }],
            "https://example.test/releases/v2.0.0.0");
    }

    /// <summary>
    /// workerと所有ハンドルの終端後に固有領域を回収し、元の失敗と回収失敗・絶対パスを分けます。
    /// </summary>
    private static void DeleteCleanupTestDirectory(string root, Exception? primaryFailure)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception cleanupFailure)
        {
            var diagnostic = new IOException("一時領域を回収できません: " + root, cleanupFailure);
            if (primaryFailure is null)
            {
                throw diagnostic;
            }
            primaryFailure.Data["DirectorySecondaryDiagnostics"] = diagnostic.ToString();
        }
    }

    private sealed class UnexpectedUpdaterProcessGateway : IUpdaterProcessGateway
    {
        public IPreparedUpdaterLaunch Prepare(UpdaterProcessLaunchRequest request)
        {
            throw new AssertFailedException("更新なしの清掃で更新ツールを準備してはいけません。");
        }

        public bool RecoverIncompleteTransaction(string executablePath, string applicationDirectory)
        {
            throw new AssertFailedException("journalなしの清掃で回復を開始してはいけません。");
        }
    }

    private sealed class FakePreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        private readonly Func<UpdaterLaunchReceipt> start;

        internal FakePreparedUpdaterLaunch(Func<UpdaterLaunchReceipt> start)
        {
            this.start = start;
        }

        public UpdaterLaunchReceipt Start()
        {
            return start();
        }
    }
}
