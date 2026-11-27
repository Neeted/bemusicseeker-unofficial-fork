using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupUpdateWorkflowOwnerTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>ケース開始後だけ利用できる、可変ファイル領域の所有者です。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>constructorと既存field準備の成功後にケース専有領域を取得します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>既存finallyと接続・Task終端の後にケース専有領域を回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

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
                return ConcurrencyProbe.StartDedicatedAsync(action);
            });
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
        Assert.IsFalse(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
        await WaitForCompletion(owner);

        CollectionAssert.AreEqual(new[] { "schedule", "cleanup", "check", "terminal" }, events);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.NoUpdate, receipt.Outcome);
    }

    /// <summary>
    /// 保証：開始状態と終端の実receiptは、終端通知の発行前から同じ値で保持される。購読の時機に依らず、
    /// 開始前を終端として補わず、終端後の照会も通知と同じreceiptを返す。
    /// </summary>
    [TestMethod]
    public async Task CaptureState_RetainsTheRealReceiptBeforeTerminalNotification()
    {
        var gate = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupUpdateWorkflowOwner owner = CreateOwner(() => gate.Task);
        StartupUpdateWorkflowCompletionReceipt? published = null;
        StartupUpdateWorkflowCompletionReceipt? retainedDuringNotification = null;
        owner.TerminalPublished += receipt =>
        {
            published = receipt;
            owner.CaptureState(out retainedDuringNotification);
        };

        Assert.AreEqual(StartupUpdateWorkflowState.NotStarted, owner.CaptureState(out StartupUpdateWorkflowCompletionReceipt? none));
        Assert.IsNull(none);
        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
        Assert.AreEqual(StartupUpdateWorkflowState.Running, owner.CaptureState(out none));
        Assert.IsNull(none);

        gate.SetResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
        await WaitForCompletion(owner);

        Assert.AreEqual(StartupUpdateWorkflowState.Terminal, owner.CaptureState(out StartupUpdateWorkflowCompletionReceipt? retained));
        Assert.IsNotNull(retained);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.NoUpdate, retained.Outcome);
        Assert.AreSame(published, retained);
        Assert.AreSame(retained, retainedDuringNotification, "終端通知の発行時点で、保持済みの受領を照会できる必要があります。");
    }

    /// <summary>
    /// 保証：開始せずに閉鎖された所有者は終端を持たない。閉鎖後の開始拒否を終端種別へ読み替えない。
    /// </summary>
    [TestMethod]
    public void CaptureState_DoesNotInventATerminalForAClosedUnstartedWorkflow()
    {
        StartupUpdateWorkflowOwner owner = CreateOwner(() => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")));
        owner.NotifyClosing();

        Assert.IsFalse(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
        Assert.AreEqual(StartupUpdateWorkflowState.NotStarted, owner.CaptureState(out StartupUpdateWorkflowCompletionReceipt? receipt));
        Assert.IsNull(receipt);
    }

    /// <summary>
    /// 保証：journal・receiptなしで同期ロックを排他保持する新版起動時も実清掃から更新なし終端へ一度進み、
    /// lock/currentを保持して古いdownloadsを回収する。清掃の省略とロック解放待ちによる停滞を検出する。
    /// </summary>
    [TestMethod]
    public async Task Start_CleansUpWithHeldTransactionLockAndPublishesNoUpdateTerminal()
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_StartupUpdateWorkflowOwnerTests", Guid.NewGuid().ToString("N"));
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
                new UnexpectedUpdaterProcessGateway(),
                []);
            StartupUpdateWorkflowOwner owner = CreateOwner(
                () =>
                {
                    Interlocked.Increment(ref checkCount);
                    return Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
                },
                cleanup: service.CleanupPreviousWorkDirectory,
                schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action));
            StartupUpdateWorkflowCompletionReceipt? receipt = null;
            owner.TerminalPublished += published =>
            {
                receipt = published;
                Interlocked.Increment(ref terminalCount);
            };
            owner.FailurePresentationRequested += _ =>
            {
                Interlocked.Increment(ref failurePresentationCount);
                return Task.CompletedTask;
            };

            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_StartupUpdateWorkflowOwnerTests", Guid.NewGuid().ToString("N"));
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
                new UnexpectedUpdaterProcessGateway(),
                []);
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
                schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action));
            StartupUpdateWorkflowCompletionReceipt? receipt = null;
            owner.TerminalPublished += published => receipt = published;
            owner.FailurePresentationRequested += exception =>
            {
                presentedFailure = exception;
                Interlocked.Increment(ref failurePresentationCount);
                return Task.CompletedTask;
            };

            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action));
        owner.FailurePresentationRequested += exception =>
        {
            Assert.AreSame(receiptException, exception);
            Interlocked.Increment(ref presentationCount);
            return Task.CompletedTask;
        };

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action));
        owner.FailurePresentationRequested += _ => throw new InvalidOperationException("presentation failed");

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action));
        owner.FailurePresentationRequested += _ =>
        {
            receiptException.DeferAcknowledge();
            return Task.CompletedTask;
        };

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action));
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

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action));
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
            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action),
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
            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action));
        owner.PresentationRequested += request => Interlocked.Increment(ref presentationCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
        owner.NotifyClosing();
        checkCompletion.SetResult(CreateAvailableResult());

        await WaitForCompletion(owner);
        Assert.AreEqual(0, presentationCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, receipt.Outcome);
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
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.FailurePresentationRequested += _ =>
        {
            Interlocked.Increment(ref failurePresentationCount);
            return Task.CompletedTask;
        };
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ =>
        {
            Interlocked.Increment(ref failurePresentationCount);
            return Task.CompletedTask;
        };
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        try
        {
            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.FailurePresentationRequested += _ =>
        {
            Interlocked.Increment(ref failurePresentationCount);
            return Task.CompletedTask;
        };
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        try
        {
            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action));
        Func<string, Task<ShutdownPreparationResult>> firstPort = _ => Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        owner.BindShutdownPreparation(firstPort);

        try
        {
            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
            await TestUiDispatcherHost.AwaitNotificationAsync(checkStarted.Task, worker, nameof(worker));
            Assert.ThrowsExactly<InvalidOperationException>(() => owner.BindShutdownPreparation(_ => Task.FromResult(new ShutdownPreparationResult("update", 2L, false, 0))));

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
            schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action),
            dispatch: action => consumers.Enqueue(Task.Run(action)));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ =>
        {
            shutdownPreparationWasObserved = owner.IsShutdownPreparationStarted;
            failurePresentation.TrySetResult(true);
            return Task.CompletedTask;
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
            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action));
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
        owner.FailurePresentationRequested += _ =>
        {
            events.Add("failure_presentation");
            return Task.CompletedTask;
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
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
        owner.FailurePresentationRequested += _ =>
        {
            Interlocked.Increment(ref failurePresentationCount);
            return Task.CompletedTask;
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
        await owner.WaitForIdleAsync();
        Assert.IsTrue(owner.IsIdle);
        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
    }

    /// <summary>
    /// 保証：永続した更新失敗の記録は、表示の Task が完了した後にだけ、一度だけ既読にする。
    /// 表示が未完了の間に既読にして、利用者が見る前に失敗の記録を消す誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task FailurePresentation_PendingPresentationAcknowledgesOnlyAfterItCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var warningLogged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int presentationCount = 0;
        int acknowledgeCount = 0;
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () =>
            {
                Interlocked.Increment(ref acknowledgeCount);
                acknowledged.TrySetResult(true);
            });
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            cleanup: () => throw receiptException,
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action),
            logWarning: _ => warningLogged.TrySetResult(true));
        owner.FailurePresentationRequested += exception =>
        {
            Interlocked.Increment(ref presentationCount);
            return gate.Task;
        };

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
        // 終端は表示を待たずに公開済みです。表示は未完了なので、既読にしません。
        await WaitForCompletion(owner);
        Assert.AreEqual(1, Volatile.Read(ref presentationCount));
        Assert.AreEqual(0, Volatile.Read(ref acknowledgeCount));

        gate.SetResult();
        Task first = await Task.WhenAny(acknowledged.Task, warningLogged.Task);
        Assert.AreSame(acknowledged.Task, first, "表示の成功後は、警告ではなく既読化が行われる必要があります。");
        Assert.AreEqual(1, Volatile.Read(ref acknowledgeCount));
    }

    /// <summary>
    /// 保証：表示が同期の例外・失敗した Task・取消済みの Task のいずれで終わっても、既読にせず警告の診断へ残す。
    /// 表示の失敗を成功へ読み替えて、利用者が見ていない失敗の記録を消す誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("throws")]
    [DataRow("faulted")]
    [DataRow("canceled")]
    public async Task FailurePresentation_FailedPresentationKeepsReceiptAndLogsWarning(string failureForm)
    {
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var warningLogged = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextLogged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int acknowledgeCount = 0;
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () =>
            {
                Interlocked.Increment(ref acknowledgeCount);
                acknowledged.TrySetResult(true);
            });
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            cleanup: () => throw receiptException,
            schedule: action => ConcurrencyProbe.StartDedicatedAsync(action),
            logInfo: message =>
            {
                if (message.Contains("startup_update failure presentation failed", StringComparison.Ordinal))
                {
                    contextLogged.TrySetResult(message);
                }
            },
            logWarning: exception => warningLogged.TrySetResult(exception));
        owner.FailurePresentationRequested += _ => failureForm switch
        {
            "throws" => throw new InvalidOperationException("presentation failed"),
            "faulted" => Task.FromException(new InvalidOperationException("presentation failed")),
            _ => Task.FromCanceled(new CancellationToken(canceled: true)),
        };

        Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
        await WaitForCompletion(owner);

        Task first = await Task.WhenAny(warningLogged.Task, acknowledged.Task);
        Assert.AreSame(warningLogged.Task, first, "表示の失敗は既読化ではなく警告の診断になる必要があります。");
        Exception logged = await warningLogged.Task;
        if (failureForm == "canceled")
        {
            Assert.IsInstanceOfType<OperationCanceledException>(logged);
        }
        else
        {
            Assert.IsInstanceOfType<InvalidOperationException>(logged);
            Assert.AreEqual("presentation failed", logged.Message);
        }
        StringAssert.Contains(await contextLogged.Task, "startup_update failure presentation failed");
        Assert.AreEqual(0, Volatile.Read(ref acknowledgeCount));
    }

    /// <summary>
    /// 保証：未完了の失敗の表示を、終端の公開やアプリ終了の要求の前に待たない。
    /// 表示の完了を待って、終了を要求する通知を止めてしまう誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task FailurePresentation_PendingPresentationDoesNotDelayTerminalOrApplicationShutdown()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = Task.CompletedTask;
        int failurePresentationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => Task.FromResult("package.zip"),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => new UpdaterLaunchReceipt()),
            schedule: action => worker = ConcurrencyProbe.StartDedicatedAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ =>
        {
            Interlocked.Increment(ref failurePresentationCount);
            return gate.Task;
        };
        owner.ApplicationShutdownRequested += () => applicationShutdown.TrySetResult(true);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        try
        {
            Assert.IsTrue(owner.Start(StartupUpdateInitializationTestSupport.SucceededTask()));
            await WaitForCompletion(owner);

            Assert.AreEqual(1, Volatile.Read(ref failurePresentationCount));
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
            await TestUiDispatcherHost.AwaitNotificationAsync(applicationShutdown.Task, worker, nameof(worker));
            Assert.IsTrue(applicationShutdown.Task.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
            await worker;
        }
    }

    /// <summary>
    /// 保証：更新ダイアログの完了より前には取得を始めず、完了の直後に、起動時の初期化の完了を待たず取得を始める。
    /// 初期化の完了まで取得を保留して、初期化中の更新を遅らせる誤りと、押下前に取得を始める誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task Transfer_StartsRightAfterDialogCompletionWithoutWaitingForInitialization()
    {
        var rig = new TransferRig(holdPresentation: true);
        try
        {
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.PresentationRequested.Task, "presentation");

            Assert.AreEqual(0, rig.TransferCount, "ダイアログの完了前に取得を始めてはいけません。");
            rig.HeldPresentation!.Complete(rig.Asset);
            await rig.AwaitAsync(rig.TransferEntered.Task, "transfer");

            Assert.AreEqual(1, rig.TransferCount);
            Assert.IsFalse(rig.Initialization.Task.IsCompleted, "取得は初期化の完了を待たずに始まる必要があります。");
            Assert.IsNull(rig.Receipt);
            Assert.AreEqual(0, rig.PrepareCount);
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：更新ツールの起動準備と起動は、ZIPの検証の成功と初期化の成功がそろった時点で一度だけ行い、どちらが先でも同じ順序で適用へ進む。
    /// 片方だけで起動する誤りと、後着の側を待たない誤り、起動準備を重複する誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Launch_WaitsForBothVerifiedPackageAndInitializationSuccess(bool initializationFirst)
    {
        var rig = new TransferRig();
        try
        {
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.TransferEntered.Task, "transfer");
            if (initializationFirst)
            {
                rig.Initialization.SetResult(StartupUpdateInitializationTestSupport.Succeeded);
                Assert.AreEqual(0, rig.PrepareCount, "ZIPの検証前に起動準備を始めてはいけません。");
                rig.Release();
            }
            else
            {
                rig.Release();
                await rig.AwaitAsync(rig.WaitingObserved.Task, "waiting");
                Assert.AreEqual(0, rig.PrepareCount, "初期化の成功前に起動準備を始めてはいけません。");
                Assert.IsNull(rig.Receipt);
                rig.Initialization.SetResult(StartupUpdateInitializationTestSupport.Succeeded);
            }

            await rig.AwaitAsync(rig.Terminal.Task, "terminal");

            Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, rig.Receipt!.Outcome);
            Assert.AreEqual(1, rig.PrepareCount);
            Assert.AreEqual(1, rig.StartCount);
            Assert.AreEqual(1, rig.ProceedCount);
            Assert.AreEqual(0, rig.DeleteCount);
            Assert.AreEqual(0, rig.FailureCount);
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：初期化が成功以外で終わると、取得中は取得を取り消してその終わりを待ち、初期化待ちなら検証済みZIPを削除し、
    /// 起動準備・失敗の表示・アプリの終了要求なしで取消の終端を一度だけ公開する。初期化の結果の種類にも段階にも依らない。
    /// </summary>
    [TestMethod]
    [DataRow(false, "failed")]
    [DataRow(false, "faulted")]
    [DataRow(true, "failed")]
    [DataRow(true, "faulted")]
    public async Task InitializationNotSucceeded_CancelsTransferAndDiscardsVerifiedPackage(bool waitingStage, string form)
    {
        var rig = new TransferRig();
        try
        {
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.TransferEntered.Task, "transfer");
            if (waitingStage)
            {
                rig.Release();
                await rig.AwaitAsync(rig.WaitingObserved.Task, "waiting");
            }

            if (form == "failed")
            {
                rig.Initialization.SetResult(new StartupInitializationResult(StartupInitializationOutcome.SettingsRequired));
            }
            else
            {
                rig.Initialization.SetException(new InvalidOperationException("initialization failed"));
            }

            if (waitingStage)
            {
                await rig.AwaitAsync(rig.Terminal.Task, "terminal");
                CollectionAssert.AreEqual(new[] { "delete:package.zip", "terminal" }, rig.TerminalEvents());
            }
            else
            {
                await rig.AwaitAsync(rig.CancelObserved.Task, "cancel");
                Assert.IsNull(rig.Receipt, "取得を保持している間は終端を公開してはいけません。");
                Assert.IsFalse(rig.Owner.IsIdle);
                rig.Release();
                await rig.AwaitAsync(rig.Terminal.Task, "terminal");
                Assert.AreEqual(0, rig.DeleteCount);
            }

            Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, rig.Receipt!.Outcome);
            Assert.AreEqual(1, rig.TerminalCount);
            Assert.AreEqual(0, rig.PrepareCount);
            Assert.AreEqual(0, rig.FailureCount);
            Assert.AreEqual(0, rig.ShutdownRequestCount);
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：ダイアログの完了時点で初期化がすでに成功以外で終わっていれば、取得を始めず取消の終端にする。
    /// </summary>
    [TestMethod]
    public async Task InitializationAlreadyNotSucceededAtSelection_DoesNotStartTransfer()
    {
        var rig = new TransferRig();
        try
        {
            rig.Initialization.SetResult(new StartupInitializationResult(StartupInitializationOutcome.ExitRequested));
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.Terminal.Task, "terminal");

            Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, rig.Receipt!.Outcome);
            Assert.AreEqual(0, rig.TransferCount);
            Assert.AreEqual(0, rig.PrepareCount);
            Assert.AreEqual(0, rig.FailureCount);
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：明示取消は転送中と初期化待ちのどちらでも受け付け、取得の終わりと一時物・検証済みZIPの回収の後に取消の終端を公開する。
    /// 取消を無視して検証済みのZIPを返した取得でも破棄する。失敗の表示も更新ダイアログの再表示も起動もしない。
    /// </summary>
    [TestMethod]
    [DataRow("transferring", false)]
    [DataRow("transferring", true)]
    [DataRow("waiting", false)]
    public async Task ExplicitCancel_RecoversBeforeCancelledTerminalAndKeepsInitializationUntouched(string stage, bool transferIgnoresCancel)
    {
        var rig = new TransferRig { TransferIgnoresCancel = transferIgnoresCancel };
        try
        {
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.TransferEntered.Task, "transfer");
            bool waiting = stage == "waiting";
            if (waiting)
            {
                rig.Release();
                await rig.AwaitAsync(rig.WaitingObserved.Task, "waiting");
            }

            Assert.IsTrue(rig.Owner.RequestCancel());
            Assert.IsFalse(rig.Owner.RequestCancel(), "取消を受け付けた後の取消は効果がありません。");
            if (!waiting)
            {
                await rig.AwaitAsync(rig.CancelObserved.Task, "cancel");
                Assert.IsNull(rig.Receipt, "取得を保持している間は終端を公開してはいけません。");
                Assert.IsFalse(rig.Owner.IsIdle);
                Assert.AreEqual(0, rig.DeleteCount);
                rig.Release();
            }

            await rig.AwaitAsync(rig.Terminal.Task, "terminal");

            Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, rig.Receipt!.Outcome);
            Assert.AreEqual(waiting || transferIgnoresCancel ? 1 : 0, rig.DeleteCount);
            if (rig.DeleteCount == 1)
            {
                CollectionAssert.AreEqual(new[] { "delete:package.zip", "terminal" }, rig.TerminalEvents());
            }

            Assert.AreEqual(1, rig.TerminalCount);
            Assert.AreEqual(1, rig.PresentationCount, "更新ダイアログを再表示してはいけません。");
            Assert.IsFalse(rig.Owner.Start(rig.Initialization.Task), "更新確認は起動ごとに一度だけです。");
            Assert.IsFalse(rig.Owner.RequestCancel());
            Assert.AreEqual(0, rig.FailureCount);
            Assert.AreEqual(0, rig.ShutdownRequestCount);

            rig.Initialization.SetResult(StartupUpdateInitializationTestSupport.Succeeded);
            Assert.AreEqual(0, rig.PrepareCount, "取消の後に初期化が成功しても起動してはいけません。");
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：更新ツールの起動を確定した後の明示取消は効果がなく、起動の準備から適用までを従来の順序で終える。
    /// </summary>
    [TestMethod]
    public async Task ExplicitCancel_AfterLaunchIsCommittedHasNoEffect()
    {
        bool? cancelAccepted = null;
        TransferRig? created = null;
        var rig = new TransferRig(prepare: () => cancelAccepted = created!.Owner.RequestCancel());
        created = rig;
        try
        {
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.TransferEntered.Task, "transfer");
            rig.Initialization.SetResult(StartupUpdateInitializationTestSupport.Succeeded);
            rig.Release();
            await rig.AwaitAsync(rig.Terminal.Task, "terminal");

            Assert.AreEqual(false, cancelAccepted, "起動準備の呼出し中は取消を受け付けません。");
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, rig.Receipt!.Outcome);
            Assert.AreEqual(1, rig.ProceedCount);
            Assert.AreEqual(0, rig.DeleteCount);
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：更新ツールの起動前の終了通知は、転送中でも初期化待ちでも取得を取り消し、取得の終わりと回収の後に終了の終端を公開する。
    /// 初期化の結果が永久に来なくても待ちが残らず、起動準備・失敗の表示は行わない。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClosingBeforeLaunch_CancelsTransferAndPublishesClosingAfterRecovery(bool waitingStage)
    {
        var rig = new TransferRig();
        try
        {
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.TransferEntered.Task, "transfer");
            if (waitingStage)
            {
                rig.Release();
                await rig.AwaitAsync(rig.WaitingObserved.Task, "waiting");
            }

            Assert.IsTrue(rig.Owner.NotifyClosing());
            Task idle = rig.Owner.WaitForIdleAsync();
            Task terminal = rig.Owner.WaitForTerminalAsync();
            if (!waitingStage)
            {
                await rig.AwaitAsync(rig.CancelObserved.Task, "cancel");
                Assert.IsFalse(idle.IsCompleted, "取得を保持している間は終了の待機を完了してはいけません。");
                Assert.IsFalse(terminal.IsCompleted);
                Assert.IsNull(rig.Receipt);
                rig.Release();
            }

            await rig.AwaitAsync(rig.Terminal.Task, "recovery");
            await Task.WhenAll(idle, terminal);

            Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, rig.Receipt!.Outcome);
            Assert.AreEqual(waitingStage ? 1 : 0, rig.DeleteCount);
            CollectionAssert.AreEqual(
                waitingStage ? new[] { "delete:package.zip", "terminal" } : new[] { "terminal" },
                rig.TerminalEvents(),
                "検証済みZIPの削除は終端の公開より前でなければなりません。");
            Assert.AreEqual(1, rig.TerminalCount);
            Assert.AreEqual(0, rig.PrepareCount);
            Assert.AreEqual(0, rig.FailureCount);
            Assert.AreEqual(0, rig.ShutdownRequestCount);
            Assert.IsFalse(rig.Initialization.Task.IsCompleted);
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：終了の通知を呼んだスレッド上では、更新側の後続処理（検証済みZIPの削除、終端の公開、UIへの配送）を同期実行しない。
    /// 取消の登録処理が同期的に継続する取得でも、終了側が保持するlockの下で回収と終端の公開が走る誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClosingNotification_DoesNotRunFollowUpOnTheCallingThread(bool waitingStage)
    {
        int callerThreadId = 0;
        int inCall = 0;
        int violations = 0;
        void RecordIfOnCaller()
        {
            if (Volatile.Read(ref inCall) == 1 && Environment.CurrentManagedThreadId == Volatile.Read(ref callerThreadId))
            {
                Interlocked.Increment(ref violations);
            }
        }

        var rig = new TransferRig(
            dispatch: action =>
            {
                RecordIfOnCaller();
                action();
            },
            onDelete: RecordIfOnCaller,
            onTerminal: RecordIfOnCaller,
            transferFactory: (token, release) =>
            {
                // 取消の登録処理が、同期的に継続するTaskを直接取り消す。
                var transfer = new TaskCompletionSource<string>();
                token.Register(() => transfer.TrySetCanceled(token));
                release.Task.ContinueWith(
                    _ => transfer.TrySetResult("package.zip"),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return transfer.Task;
            });
        try
        {
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.TransferEntered.Task, "transfer");
            if (waitingStage)
            {
                rig.Release();
                await rig.AwaitAsync(rig.WaitingObserved.Task, "waiting");
            }

            Volatile.Write(ref callerThreadId, Environment.CurrentManagedThreadId);
            Volatile.Write(ref inCall, 1);
            try
            {
                Assert.IsTrue(rig.Owner.NotifyClosing());
            }
            finally
            {
                Volatile.Write(ref inCall, 0);
            }

            await rig.AwaitAsync(rig.Terminal.Task, "terminal");

            Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, rig.Receipt!.Outcome);
            Assert.AreEqual(0, Volatile.Read(ref violations), "終了通知の呼出し中に、呼出し元のスレッドで後続処理が走りました。");
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：取得・検証の失敗は、初期化の完了を待たず直ちに失敗として公開し、失敗の表示を一度だけ要求して起動しない。
    /// その後に初期化が成功以外で終わっても結果を変えない。
    /// </summary>
    [TestMethod]
    public async Task TransferFailure_PublishesFailedImmediatelyWithoutWaitingForInitialization()
    {
        var rig = new TransferRig();
        try
        {
            Assert.IsTrue(rig.Owner.Start(rig.Initialization.Task));
            await rig.AwaitAsync(rig.TransferEntered.Task, "transfer");
            var failure = new InvalidOperationException("transfer failed");
            rig.Release(failure);
            await rig.AwaitAsync(rig.Terminal.Task, "terminal");

            Assert.IsFalse(rig.Initialization.Task.IsCompleted, "初期化の終端を待たず公開する必要があります。");
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, rig.Receipt!.Outcome);
            Assert.AreSame(failure, rig.Receipt.Exception);
            Assert.AreEqual(1, rig.FailureCount);
            Assert.AreEqual(0, rig.PrepareCount);
            Assert.AreEqual(0, rig.ShutdownRequestCount);

            rig.Initialization.SetResult(new StartupInitializationResult(StartupInitializationOutcome.OperationFailed));
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, rig.Receipt.Outcome);
            Assert.AreEqual(1, rig.TerminalCount);
            Assert.AreEqual(1, rig.FailureCount);
        }
        finally
        {
            await rig.FinishAsync();
        }
    }

    /// <summary>更新の取得の代替と、その保持・解放・観測を一箇所にまとめた、管理主体の判断を検査するための組立です。</summary>
    private sealed class TransferRig
    {
        private readonly object syncRoot = new();
        private readonly List<string> events = [];
        private readonly Func<CancellationToken, TaskCompletionSource<Exception?>, Task<string>>? transferFactory;
        private readonly Action? onDelete;
        private readonly Action? onTerminal;
        private readonly Action? prepare;
        private readonly bool holdPresentation;
        private int transferCount;
        private int prepareCount;
        private int startCount;
        private int proceedCount;
        private int deleteCount;
        private int failureCount;
        private int shutdownRequestCount;
        private int presentationCount;
        private int terminalCount;
        private Task worker = Task.CompletedTask;

        internal TransferRig(
            bool holdPresentation = false,
            Action<Action>? dispatch = null,
            Action? onDelete = null,
            Action? onTerminal = null,
            Func<CancellationToken, TaskCompletionSource<Exception?>, Task<string>>? transferFactory = null,
            Action? prepare = null)
        {
            this.holdPresentation = holdPresentation;
            this.onDelete = onDelete;
            this.onTerminal = onTerminal;
            this.transferFactory = transferFactory;
            this.prepare = prepare;
            Asset = CreateAvailableResult().Assets[0];
            Owner = new StartupUpdateWorkflowOwner(
                () => Task.FromResult(CreateAvailableResult()),
                TransferAsync,
                PrepareLaunch,
                () => { },
                DeletePackage,
                action => worker = ConcurrencyProbe.StartDedicatedAsync(action),
                dispatch ?? (action => action()));
            Owner.PresentationRequested += request =>
            {
                Interlocked.Increment(ref presentationCount);
                if (holdPresentation)
                {
                    HeldPresentation = request;
                    PresentationRequested.TrySetResult(true);
                    return;
                }
                request.Complete(Asset);
                PresentationRequested.TrySetResult(true);
            };
            Owner.FailurePresentationRequested += _ =>
            {
                Interlocked.Increment(ref failureCount);
                return Task.CompletedTask;
            };
            Owner.ApplicationShutdownRequested += () => Interlocked.Increment(ref shutdownRequestCount);
            Owner.BindShutdownPreparation(reason => Task.FromResult(new ShutdownPreparationResult(reason, 1L, false, 0)));
            Owner.ProgressChanged += snapshot =>
            {
                if (snapshot.Phase == StartupUpdateProgressPhase.WaitingForInitialization)
                {
                    WaitingObserved.TrySetResult(true);
                }
            };
            Owner.TerminalPublished += published =>
            {
                onTerminal?.Invoke();
                lock (syncRoot)
                {
                    events.Add("terminal");
                }
                Receipt = published;
                Interlocked.Increment(ref terminalCount);
                Terminal.TrySetResult(true);
            };
        }

        internal StartupUpdateWorkflowOwner Owner { get; }

        internal UpdateAssetInfo Asset { get; }

        internal StartupUpdatePresentationRequest? HeldPresentation { get; private set; }

        internal TaskCompletionSource<StartupInitializationResult> Initialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> PresentationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> TransferEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> WaitingObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<Exception?> ReleaseSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>取消を受けても完了せず、検証済みのZIPを返す取得を再現します。</summary>
        internal bool TransferIgnoresCancel { get; init; }

        internal StartupUpdateWorkflowCompletionReceipt? Receipt { get; private set; }

        internal int TransferCount => Volatile.Read(ref transferCount);

        internal int PrepareCount => Volatile.Read(ref prepareCount);

        internal int StartCount => Volatile.Read(ref startCount);

        internal int ProceedCount => Volatile.Read(ref proceedCount);

        internal int DeleteCount => Volatile.Read(ref deleteCount);

        internal int FailureCount => Volatile.Read(ref failureCount);

        internal int ShutdownRequestCount => Volatile.Read(ref shutdownRequestCount);

        internal int PresentationCount => Volatile.Read(ref presentationCount);

        internal int TerminalCount => Volatile.Read(ref terminalCount);

        /// <summary>保持している取得を、成功または指定の失敗で解放します。</summary>
        internal void Release(Exception? failure = null) => ReleaseSignal.TrySetResult(failure);

        internal string[] TerminalEvents()
        {
            lock (syncRoot)
            {
                return [.. events];
            }
        }

        /// <summary>通知を待ち、通知前の失敗や通知欠落を表面化します。局所的な制限時間は付けません。</summary>
        internal Task AwaitAsync(Task notification, string name)
            => TestUiDispatcherHost.AwaitNotificationAsync(notification, worker, name);

        /// <summary>全ての待合せを解放し、終了を通知して、作業と終端の受領を回収します。</summary>
        internal async Task FinishAsync()
        {
            Release();
            HeldPresentation?.Complete(null);
            Owner.NotifyClosing();
            await worker;
            await Task.WhenAll(Owner.WaitForIdleAsync(), Owner.WaitForTerminalAsync());
        }

        private async Task<string> TransferAsync(UpdateAssetInfo asset, CancellationToken token, IProgress<UpdateDownloadProgress> progress)
        {
            Interlocked.Increment(ref transferCount);
            token.Register(() => CancelObserved.TrySetResult(true));
            TransferEntered.TrySetResult(true);
            if (transferFactory != null)
            {
                return await transferFactory(token, ReleaseSignal).ConfigureAwait(false);
            }

            Exception? failure = await ReleaseSignal.Task.ConfigureAwait(false);
            if (failure != null)
            {
                throw failure;
            }
            if (!TransferIgnoresCancel)
            {
                token.ThrowIfCancellationRequested();
            }
            return "package.zip";
        }

        private IPreparedUpdaterLaunch PrepareLaunch(string packagePath)
        {
            Interlocked.Increment(ref prepareCount);
            prepare?.Invoke();
            return new FakePreparedUpdaterLaunch(() =>
            {
                Interlocked.Increment(ref startCount);
                return new UpdaterLaunchReceipt(proceed: () => Interlocked.Increment(ref proceedCount));
            });
        }

        private void DeletePackage(string packagePath)
        {
            onDelete?.Invoke();
            lock (syncRoot)
            {
                events.Add("delete:" + packagePath);
            }
            Interlocked.Increment(ref deleteCount);
        }
    }

    private static async Task WaitForCompletion(StartupUpdateWorkflowOwner owner)
    {
        await Task.WhenAll(owner.WaitForIdleAsync(), owner.WaitForTerminalAsync());
    }

    private static StartupUpdateWorkflowOwner CreateOwner(
        Func<Task<UpdateCheckResult>> check,
        Func<UpdateAssetInfo, Task<string>>? download = null,
        Func<UpdateAssetInfo, CancellationToken, IProgress<UpdateDownloadProgress>, Task<string>>? transfer = null,
        Func<string, IPreparedUpdaterLaunch>? prepare = null,
        Action? cleanup = null,
        Action<string>? delete = null,
        Func<Func<Task>, Task>? schedule = null,
        Action<Action>? dispatch = null,
        Action<string>? logInfo = null,
        Action<Exception>? logWarning = null)
    {
        return new StartupUpdateWorkflowOwner(
            check,
            transfer ?? (download == null
                ? (asset, _, _) => Task.FromResult("package.zip")
                : (asset, _, _) => download(asset)),
            prepare ?? (packagePath => new FakePreparedUpdaterLaunch(() => new UpdaterLaunchReceipt())),
            cleanup ?? (() => { }),
            delete ?? (_ => { }),
            schedule ?? (action => ConcurrencyProbe.StartDedicatedAsync(action)),
            dispatch ?? (action => action()),
            logInfo,
            logWarning);
    }

    private static UpdateCheckResult CreateAvailableResult()
    {
        return UpdateCheckResult.Available(
            new Version(2, 0, 0, 0),
            "1.0.0.0",
            "2.0.0.0",
            [new UpdateAssetInfo
            {
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
