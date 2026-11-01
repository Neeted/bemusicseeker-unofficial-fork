using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.MaintenanceRescanWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MaintenanceRescanWorkflowOwnerTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 承認後もライブラリ不在または実行中なら未開始で返し、対象のないworker起動と重複実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task RequestStartAsync_AcceptedButUnavailableOrActiveReturnsNotStarted()
    {
        using var release = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token, capability) =>
            {
                started.TrySetResult(true);
                release.Wait();
                return new MaintenanceWorkflowResult();
            },
            action => Task.Factory.StartNew(
                action,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default),
            action => action(),
            dialogs: new AcceptedDialogService());
        try
        {
            MaintenanceRescanStartResult unavailable = await owner.RequestStartAsync();
            Assert.AreEqual(MaintenanceRescanStartStatus.NotStarted, unavailable.Status);

            try
            {
                owner.AttachLibrary(CreateLibrary(root, "song.db"));
                MaintenanceRescanStartResult startedResult = await owner.RequestStartAsync();
                Assert.AreEqual(MaintenanceRescanStartStatus.Started, startedResult.Status);
                await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.started");
                MaintenanceRescanStartResult active = await owner.RequestStartAsync();
                Assert.AreEqual(MaintenanceRescanStartStatus.NotStarted, active.Status);
                release.Set();
                await owner.WaitForIdleAsync();
            }
            finally
            {
                release.Set();
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                release.Set();
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 初期・途中・終端の進捗を完了receiptより先に公開し、更新結果と件数を保持して、終端進捗の欠落や順序逆転を検出する。
    /// </summary>
    [TestMethod]
    public async Task Start_PublishesTerminalProgressBeforeCompletion()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var events = new List<string>();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MaintenanceRescanCompletionReceipt receipt = null!;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) =>
                {
                    progress(new MaintenanceWorkflowProgress
                    {
                        TotalCount = 3,
                        ProcessedCount = 1,
                        EvaluatedCount = 1,
                        CurrentPath = "first.bms"
                    });
                    return new MaintenanceWorkflowResult
                    {
                        HasUpdates = true,
                        CheckedFileCount = 3
                    };
                },
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.ProgressChanged += progress =>
            {
                lock (events)
                {
                    events.Add(progress.IsCompleted
                        ? "terminal"
                        : progress.ProcessedCount == 0 ? "initial" : "progress");
                }
            };
            owner.CompletionPublished += publishedReceipt =>
            {
                receipt = publishedReceipt;
                lock (events)
                {
                    events.Add("completion");
                }
                completion.TrySetResult(true);
            };

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.completion");
            await owner.WaitForIdleAsync();
            CollectionAssert.AreEqual(
                new[] { "initial", "progress", "terminal", "completion" },
                events.ToArray());
            Assert.IsNotNull(receipt);
            Assert.IsTrue(receipt.Result.HasUpdates);
            Assert.AreEqual(3, receipt.Result.CheckedFileCount);
            Assert.IsFalse(receipt.Canceled);
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 到達済みの再走査を保持中は二件目を拒否し、解放後に完了通知と実終端を回収して、同時実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task Start_RejectsDuplicateWhileTheCurrentRunIsActive()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        using var release = new ManualResetEventSlim(false);
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    return new MaintenanceWorkflowResult { Canceled = token.IsCancellationRequested };
                },
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => completed.TrySetResult(true);

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.started");
            Assert.IsFalse(await StartConfirmed(owner), "A second request must not overlap the active rescan.");
            release.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(completed.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.completed");
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                release.Set();
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 実行中のCancelで即時に取消表示へ切替え、workerの取消tokenと完了receiptを一致させ、取消後の未取消進捗を検出する。
    /// </summary>
    [TestMethod]
    public async Task Cancel_CancelsLiveRunAndPublishesCanceledCompletion()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        using var release = new ManualResetEventSlim(false);
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool canceledProgressReceived = false;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool tokenWasCanceled = false;
            bool receiptWasCanceled = false;
            bool uncanceledProgressAfterCancel = false;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    tokenWasCanceled = token.IsCancellationRequested;
                    progress(new MaintenanceWorkflowProgress
                    {
                        TotalCount = 3,
                        ProcessedCount = 2,
                        CurrentPath = "after-cancel.bms"
                    });
                    return new MaintenanceWorkflowResult { Canceled = token.IsCancellationRequested };
                },
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.ProgressChanged += progress =>
            {
                if (progress.IsCanceled && !progress.IsCompleted)
                {
                    canceledProgressReceived = true;
                }
                if (progress.ProcessedCount == 2 && !progress.IsCanceled)
                {
                    uncanceledProgressAfterCancel = true;
                }
            };
            owner.CompletionPublished += receipt =>
            {
                receiptWasCanceled = receipt.Canceled;
                completed.TrySetResult(true);
            };

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.started");
            owner.Cancel();
            Assert.IsTrue(canceledProgressReceived, "Cancel must immediately disable the active progress state.");
            release.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(completed.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.completed");
            Assert.IsTrue(tokenWasCanceled);
            Assert.IsTrue(receiptWasCanceled);
            Assert.IsFalse(uncanceledProgressAfterCancel);
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                release.Set();
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// executorが戻る際のCancelも完了receiptへ残し、完了直前の取消を成功扱いする誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task CancelDuringExecutorReturn_PublishesCanceledReceipt()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool receiptWasCanceled = false;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) =>
                {
                    if (owner is null) { throw new InvalidOperationException("再走査ownerが未構築です。"); }
                    owner.Cancel();
                    return new MaintenanceWorkflowResult();
                },
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.CompletionPublished += receipt =>
            {
                receiptWasCanceled = receipt.Canceled;
                completion.TrySetResult(true);
            };

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.completion");
            Assert.IsTrue(receiptWasCanceled);
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 旧ライブラリのworkerを保持して接続を交換し、旧完了を公開せず、新規要求では現在ライブラリの完了だけを一回公開して世代混線を検出する。
    /// </summary>
    [TestMethod]
    public async Task AttachLibrary_DropsStaleGenerationAndAllowsTheReplacementToRun()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        using var releaseFirst = new ManualResetEventSlim(false);
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            string firstRoot = Path.Combine(root, "first");
            string secondRoot = Path.Combine(root, "second");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            BMSLibrary second = CreateLibrary(secondRoot, "song.db");
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int completionCount = 0;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) =>
                {
                    if (ReferenceEquals(current, first))
                    {
                        firstStarted.TrySetResult(true);
                        releaseFirst.Wait();
                    }
                    else
                    {
                        secondStarted.TrySetResult(true);
                    }
                    return new MaintenanceWorkflowResult();
                },
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(first);
            owner.CompletionPublished += _ =>
            {
                Interlocked.Increment(ref completionCount);
                completion.TrySetResult(true);
            };

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(firstStarted.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.firstStarted");
            owner.AttachLibrary(second);
            releaseFirst.Set();
            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, completionCount, "A replaced generation must not publish completion.");

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(secondStarted.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.secondStarted");
            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.completion");
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, completionCount);
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                releaseFirst.Set();
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 到達済みの再走査へ終了要求を渡し、後続開始を拒否して実終端を回収することで終了後の受付残存を検出する。
    /// </summary>
    [TestMethod]
    public async Task RequestShutdown_CancelsActiveRunAndBlocksLaterStart()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        using var release = new ManualResetEventSlim(false);
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    return new MaintenanceWorkflowResult { Canceled = token.IsCancellationRequested };
                },
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.started");
            owner.RequestShutdown();
            Assert.IsFalse(await StartConfirmed(owner), "Shutdown must prevent a new rescan.");
            release.Set();
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                release.Set();
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// worker起動自体が例外を投げたら起動原因を通知してidleへ戻し、開始失敗による受付の残存を検出する。
    /// </summary>
    [TestMethod]
    public async Task SchedulerFailure_PublishesFailureAndLeavesOwnerIdle()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception observed = null!;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) => new MaintenanceWorkflowResult(),
                action => throw new InvalidOperationException("scheduler failed"),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += publishedFailure =>
            {
                observed = publishedFailure.Exception;
                failure.TrySetResult(true);
            };

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(failure.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.failure");
            Assert.IsInstanceOfType(observed, typeof(InvalidOperationException));
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 起動portが取消済みTaskを返しても失敗を通知してidleへ戻り、実行状態が永久に残る誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task CanceledSchedulerTask_DoesNotLeaveOwnerActive()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) => new MaintenanceWorkflowResult(),
                action => Task.FromCanceled(new CancellationToken(true)),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.TrySetResult(true);

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(failure.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.failure");
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 再走査executorの例外を処理失敗として通知してidleへ戻し、失敗の成功化や受付の残存を検出する。
    /// </summary>
    [TestMethod]
    public async Task ExecutorFailure_PublishesFailureAndLeavesOwnerIdle()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception observed = null!;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) => throw new InvalidOperationException("executor failed"),
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => action(),
                reportWorkflowFailure: exception => observed = exception,
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.TrySetResult(true);

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(failure.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.failure");
            Assert.IsInstanceOfType(observed, typeof(InvalidOperationException));
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// executorのnull結果は失敗を通知し正常完了を出さず、不正結果を空の成功へ読み替える誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task NullExecutorResult_PublishesFailureInsteadOfSuccessfulCompletion()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) => null!,
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.TrySetResult(true);
            owner.CompletionPublished += _ => completion.TrySetResult(true);

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(failure.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.failure");
            Assert.IsFalse(completion.Task.IsCompleted);
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 取消要求がないexecutorのOperationCanceledExceptionは失敗を通知し、要求済み取消として隠す誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task UnrequestedOperationCanceledException_PublishesFailure()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) => throw new OperationCanceledException("unexpected cancellation"),
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.TrySetResult(true);
            owner.CompletionPublished += _ => completion.TrySetResult(true);

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(failure.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.failure");
            Assert.IsFalse(completion.Task.IsCompleted);
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 処理とUI投稿がともに失敗しても処理失敗を一回、通知失敗を一回以上報告し、idle待機が終端する。
    /// </summary>
    [TestMethod]
    public async Task ExecutorFailure_IsReportedEvenWhenUiDispatchFails()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            int workflowFailures = 0;
            int notificationFailures = 0;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) => throw new InvalidOperationException("executor failed"),
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => throw new InvalidOperationException("dispatcher failed"),
                reportNotificationFailure: _ => Interlocked.Increment(ref notificationFailures),
                reportWorkflowFailure: _ => Interlocked.Increment(ref workflowFailures),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);

            Assert.IsTrue(await StartConfirmed(owner));
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, workflowFailures);
            Assert.IsTrue(notificationFailures > 0);
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// UI投稿が失敗しても通知診断を残して実処理のidleへ戻り、表示障害による受付の残存を検出する。
    /// </summary>
    [TestMethod]
    public async Task DispatcherFailure_IsReportedWithoutLeavingOwnerActive()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            int notificationFailures = 0;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) => new MaintenanceWorkflowResult(),
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => throw new InvalidOperationException("dispatcher failed"),
                reportNotificationFailure: _ => Interlocked.Increment(ref notificationFailures),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);

            Assert.IsTrue(await StartConfirmed(owner));
            await owner.WaitForIdleAsync();
            Assert.IsTrue(notificationFailures > 0);
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    /// <summary>
    /// 進捗購読者が例外を投げても診断して後続の完了通知を届け、購読者の失敗で終端公開が止まる誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task ObserverFailure_DoesNotSuppressFollowingCompletionNotification()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        Exception? testFailure = null;
        MaintenanceRescanWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int notificationFailures = 0;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability) => new MaintenanceWorkflowResult(),
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => action(),
                reportNotificationFailure: _ => Interlocked.Increment(ref notificationFailures),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.ProgressChanged += _ => throw new InvalidOperationException("observer failed");
            owner.CompletionPublished += _ => completion.TrySetResult(true);

            Assert.IsTrue(await StartConfirmed(owner));
            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "MaintenanceRescanWorkflowOwnerTests.completion");
            Assert.IsTrue(notificationFailures > 0);
            await owner.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                try { if (owner is not null) { await owner.WaitForIdleAsync(); } }
                finally { DeleteRoot(root); }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }

        }
    }

    private static string CreateRoot(TestWorkspace workspace)
    {
        string root = Path.Combine(workspace.RootDirectory, nameof(MaintenanceRescanWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private BMSLibrary CreateLibrary(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, []);
        return new TestBmsLibrary(databaseScope, path, null, null, string.Empty);
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        Directory.Delete(root, recursive: true);
    }

    private static async Task<bool> StartConfirmed(MaintenanceRescanWorkflowOwner owner)
    {
        MaintenanceRescanStartResult result = await owner.RequestStartAsync();
        return result.Started;
    }

}
