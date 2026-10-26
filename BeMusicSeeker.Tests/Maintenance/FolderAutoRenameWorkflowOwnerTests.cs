using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FolderAutoRenameWorkflowOwnerTests
{
    /// <summary>
    /// 実移動だけで管理出力との交差を判定し、診断だけなら再生を止めず、実交差のBusyではFS・DBを変更せず、解放後の新規要求で確定することで予約や交差誤判定を検出する。
    /// </summary>
    /// <remarks>実計画・本番port・共通受付からFS移動とSQLite保存、通知終端まで接続する。局所portでは実交差判定と保存の接続を代用できない。</remarks>
    [DataTestMethod]
    // mixedは正常移動と管理範囲内の診断が混在、diagnosticsは診断のみ、intersectionは実移動が管理範囲へ交差する。
    [DataRow("mixed")]
    [DataRow("diagnostics")]
    [DataRow("intersection")]
    public async Task PreparedAutoRename_UsesPhysicalTargetsForAdmissionAndPlaybackStop(string scenario)
    {
        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        IDisposable? held = null;
        try
        {
            string managed = Path.Combine(root, "Managed");
            string first = Path.Combine(scenario == "intersection" ? managed : root, "First");
            string second = Path.Combine(root, "Second");
            string failed = Path.Combine(managed, "Failed");
            string noChange = Path.Combine(managed, "NoChange");
            string third = Path.Combine(root, "Third");
            string[] sources = [first, second, failed, noChange, third];
            string[] titles = ["FirstMoved", "SecondMoved", "Failure", "NoChange", "ThirdMoved"];
            var charts = new List<ChartFile>();
            for (int index = 0; index < sources.Length; index++)
            {
                Directory.CreateDirectory(sources[index]);
                string path = Path.Combine(sources[index], "chart.bms");
                File.WriteAllText(path, sources[index]);
                charts.Add(ChartTestValues.Empty() with { Path = path, Md5 = index.ToString("x32"), Title = titles[index], RawTitle = titles[index], Token = new OwnedChartToken() });
            }
            string songDb = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDb, []);
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2CustomFolderOutputBaseDir = managed, FolderNameFormat = "%TITLE%" };
            var library = new TestBmsLibrary(songDb, new Lr2ConfigOwner(BmsPlaylistTestSupport.CreateLr2Config(root)), null, null, new FileDbReportRecordingDialogs(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), () => options)
            { BmsCharts = charts };
            new BmsLibraryDbGateway(songDb).UpsertSongs(charts);
            ChartFile[] selected = scenario == "diagnostics" ? [charts[2], charts[3]] : charts.Take(4).ToArray();
            var planningFailure = new IOException("failed rename plan");
            var service = new BmsLibraryLibraryFileOperationsService();
            var batch = new FolderMoveBatch(service.BuildAutoRenamePlans(selected, [], [], true, _ => selected,
                (children, parent) => children.Single().Title == "Failure" ? throw planningFailure : Path.Combine(parent, children.Single().Title)));
            var playback = new NoOpChartMutationPlaybackPort();
            int terminalCount = 0;
            int refreshCount = 0;
            FolderAutoRenameFailure? failure = null;
            FolderAutoRenameCompletionReceipt? completion = null;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion)) { refreshCount++; }
            };
            owner = new FolderAutoRenameWorkflowOwner(library.OperationAdmission, new ChartMutationActivityOwner(),
                new BmsLibraryFolderAutoRenameMutationPort(preparedBatch: batch), playback, action => action(), action => action(),
                new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += value => failure = value;
            owner.CompletionPublished += value => completion = value;
            owner.TerminalPublished += () => terminalCount++;
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out held));
            Assert.IsTrue(owner.RequestStartSelected(selected.Select(CreatePhysicalTarget).ToArray()));
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, terminalCount);
            Assert.IsFalse(library.OperationAdmission.IsActive);
            if (scenario == "intersection")
            {
                Assert.IsNotNull(failure);
                StringAssert.Contains(failure.Exception.Message, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
                Assert.IsNull(completion);
                Assert.AreEqual(0, playback.StopCount);
                Assert.AreEqual(0, refreshCount);
                foreach (ChartFile chart in charts) { Assert.IsTrue(File.Exists(chart.Path)); }
                held.Dispose();
                held = null;
                Assert.IsTrue(File.Exists(charts[0].Path), "Pの解放だけでは拒否した操作を実行しません。");
                failure = null;
                Assert.IsTrue(owner.RequestStartSelected(selected.Select(CreatePhysicalTarget).ToArray()));
                await owner.WaitForIdleAsync();
                Assert.IsNull(failure);
                Assert.AreEqual(2, terminalCount);
            }
            else { Assert.IsNull(failure); }
            Assert.IsNotNull(completion);
            int expected = scenario == "diagnostics" ? 0 : 2;
            Assert.AreEqual(expected == 0 ? 0 : 1, playback.StopCount);
            Assert.AreEqual(expected, completion.SessionReceipt.ConfirmedChangeCount);
            Assert.AreEqual(expected == 0 ? 0 : 1, refreshCount);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly())
            {
                for (int index = 0; index < charts.Count; index++)
                {
                    string destination = expected > 0 && index < 2 ? Path.Combine(Path.GetDirectoryName(sources[index])!, titles[index], "chart.bms") : charts[index].Path;
                    Assert.IsTrue(File.Exists(destination));
                    Assert.IsNotNull(db.Find<LR2SongDB.song>(destination));
                    if (expected > 0 && index < 2) { Assert.IsNull(db.Find<LR2SongDB.song>(charts[index].Path)); }
                }
            }
            held?.Dispose();
            held = null;
            owner = new FolderAutoRenameWorkflowOwner(library.OperationAdmission, new ChartMutationActivityOwner(),
                new BmsLibraryFolderAutoRenameMutationPort(), playback, action => action(), action => action(), new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            FolderAutoRenameFailure? nextFailure = null;
            owner.FailurePublished += value => nextFailure = value;
            Assert.IsTrue(owner.RequestStartSelected([CreatePhysicalTarget(charts[4])]));
            await owner.WaitForIdleAsync();
            Assert.IsNull(nextFailure, nextFailure?.Exception.ToString());
            string thirdDestination = Path.Combine(root, "ThirdMoved", "chart.bms");
            Assert.IsTrue(File.Exists(thirdDestination));
            Assert.IsFalse(File.Exists(charts[4].Path));
            using LR2SongDBExtended finalDb = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly();
            Assert.IsNotNull(finalDb.Find<LR2SongDB.song>(thirdDestination));
            Assert.IsFalse(library.OperationAdmission.IsActive);
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
                held?.Dispose();
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

    private static ChartOperationTarget CreatePhysicalTarget(ChartFile chart) => new(chart, null, ChartOperationSourceScope.Library,
        isOwned: true, isPending: false, isPlaylistMissing: false, ChartOperationCapabilities.MoveInLibrary);

    /// <summary>
    /// 選択要求の同じ譜面を処理へ渡し、初期・途中・終端進捗から完了receipt・終端通知の順に公開して、対象差替えや終端順序の逆転を検出する。
    /// </summary>
    [TestMethod]
    public async Task SelectedRequest_PublishesTerminalProgressBeforeCompletion()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            ChartFolderAutoRenameRequest? observedRequest = null;
            var events = new List<string>();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            FolderAutoRenameCompletionReceipt? receipt = null;
            owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    observedRequest = selectedRequest;
                    progress.TryWrite(new FolderAutoRenameProgressUpdate(2, 1, "source"));
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action =>
                {
                    return action();
                },
                action => action(),
                dialogs: new AcceptedFolderDialogService());
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
            owner.TerminalPublished += () =>
            {
                lock (events)
                {
                    events.Add("terminal-published");
                }
            };

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.completion");
            await owner.WaitForIdleAsync();
            CollectionAssert.AreEqual(
                new[] { "initial", "progress", "terminal", "completion", "terminal-published" },
                events.ToArray());
            Assert.IsNotNull(receipt);
            ChartFolderAutoRenameRequest completedRequest = observedRequest!;
            FolderAutoRenameCompletionReceipt completedReceipt = receipt!;
            Assert.AreSame(targets[0].Chart, completedRequest.Charts[0]);
            Assert.IsFalse(completedReceipt.AllFolders);
            Assert.IsTrue(completedReceipt.RefreshRequired);
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
    /// 途中進捗の購読例外を通知診断へ残してmutation callbackを続け、正常終端・完了receiptを公開してidleへ戻ることで、購読例外を処理失敗へ混同する誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task SelectedRequest_IntermediateProgressSubscriberFailureStillCompletesAndReturnsIdle()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var progressSubscriberFailure = new InvalidOperationException("intermediate progress subscriber failure");
            var mutationReachedProgress = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var mutationContinuedAfterProgress = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var mutationReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var injectedProgressFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workflowFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminalProgress = new TaskCompletionSource<FolderAutoRenameProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<FolderAutoRenameCompletionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failurePublished = new TaskCompletionSource<FolderAutoRenameFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminalPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    mutationReachedProgress.TrySetResult(true);
                    progress.TryWrite(new FolderAutoRenameProgressUpdate(2, 1, "source"));
                    mutationContinuedAfterProgress.TrySetResult(true);
                    progress.TryWrite(new FolderAutoRenameProgressUpdate(2, 2, "source"));
                    mutationReturned.TrySetResult(true);
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action =>
                {
                    return action();
                },
                action => action(),
                dialogs: new AcceptedFolderDialogService(),
                reportNotificationFailure: exception => notificationFailure.TrySetResult(exception),
                reportWorkflowFailure: exception => workflowFailure.TrySetResult(exception));
            owner.AttachLibrary(library);
            owner.ProgressChanged += progress =>
            {
                if (progress.IsCompleted)
                {
                    terminalProgress.TrySetResult(progress);
                }
                else if (progress.ProcessedCount == 1)
                {
                    injectedProgressFailure.TrySetResult(true);
                    throw progressSubscriberFailure;
                }
            };
            owner.CompletionPublished += receipt => completion.TrySetResult(receipt);
            owner.FailurePublished += failure => failurePublished.TrySetResult(failure);
            owner.TerminalPublished += () => terminalPublished.TrySetResult(true);

            bool accepted = owner.RequestStartSelected(targets);
            await owner.WaitForIdleAsync();

            Assert.IsTrue(accepted);
            Assert.IsTrue(mutationReachedProgress.Task.IsCompleted);
            Assert.IsTrue(injectedProgressFailure.Task.IsCompleted);
            Assert.IsTrue(notificationFailure.Task.IsCompleted);
            Assert.AreSame(progressSubscriberFailure, notificationFailure.Task.Result);
            Assert.IsTrue(mutationContinuedAfterProgress.Task.IsCompleted);
            Assert.IsTrue(mutationReturned.Task.IsCompleted);
            Assert.IsTrue(completion.Task.IsCompleted);
            Assert.IsTrue(terminalProgress.Task.IsCompleted);
            Assert.IsTrue(terminalProgress.Task.Result.IsCompleted);
            Assert.IsTrue(terminalPublished.Task.IsCompleted);
            Assert.IsFalse(failurePublished.Task.IsCompleted);
            Assert.IsFalse(workflowFailure.Task.IsCompleted);
            Assert.IsTrue(owner.IsIdle);
            Assert.IsTrue(completion.Task.Result.RefreshRequired);
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
    /// 実処理中の64件進捗を最新一件のUI投稿へまとめ、排他中の再入を拒否し、終端seal後の古い進捗を捨てて、投稿増殖や完了後の状態上書きを検出する。
    /// </summary>
    [TestMethod]
    public async Task ProgressWriter_BoundsSelectedDispatchAndDropsLateProgressAfterSeal()
    {

        string root = CreateRoot();
        var notifications = new Queue<Action>();
        using var notificationSignal = new SemaphoreSlim(0);
        using var terminalPublished = new ManualResetEventSlim(false);
        int maximumQueuedNotifications = 0;
        int dispatchInvocationCount = 0;
        var observations = new List<string>();
        var chartFileOperations = new ChartFileOperationSynchronizer();
        using var mutationPort = new BoundedProgressFolderAutoRenameMutationPort(64);
        Task? mutationTask = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        Exception? publishedFailure = null;
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            owner = new FolderAutoRenameWorkflowOwner(
                chartFileOperations,
                new ChartMutationActivityOwner(),
                mutationPort,
                new NoOpChartMutationPlaybackPort(),
                action =>
                {
                    Task scheduled = Task.Factory.StartNew(
                        action,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default).Unwrap();
                    mutationTask = scheduled;
                    return scheduled;
                },
                action =>
                {
                    Interlocked.Increment(ref dispatchInvocationCount);
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                        maximumQueuedNotifications = Math.Max(
                            maximumQueuedNotifications,
                            notifications.Count);
                    }
                    notificationSignal.Release();
                },
                new AcceptedFolderDialogService());
            owner.ProgressChanged += progress =>
            {
                observations.Add(progress.IsCompleted
                    ? "terminal"
                    : "progress:" + progress.ProcessedCount);
                if (!progress.IsCompleted && mutationPort.MutationIsBlocked)
                {
                    bool acquired = chartFileOperations.TryEnter(out IDisposable reentrantLease);
                    reentrantLease?.Dispose();
                    Assert.IsFalse(acquired, "A progress subscriber must fail fast while the batch lease is held.");
                }
            };
            owner.FailurePublished += failure => publishedFailure = failure.Exception;
            owner.CompletionPublished += _ =>
            {
                observations.Add("completion");
                mutationPort.EmitLateProgress();
            };
            owner.TerminalPublished += () =>
            {
                observations.Add("terminal-published");
                terminalPublished.Set();
            };
            owner.AttachLibrary(library);
            DrainNotifications(notifications);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            Task batchIdle = owner.WaitForIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationPort.Started.Task, mutationTask!, "folder-progress.started");
            Assert.IsTrue(mutationPort.MutationIsBlocked);
            lock (notifications)
            {
                Assert.IsTrue(
                    notifications.Count <= 1,
                    "Latest-wins progress must leave at most one UI dispatch pending.");
            }

            DrainNotifications(notifications);
            CollectionAssert.AreEqual(new[] { "progress:64" }, observations.ToArray());

            mutationPort.Release();
            await TestUiDispatcherHost.AwaitNotificationAsync(mutationPort.Returned.Task, mutationTask!, "folder-progress.returned");
            while (!terminalPublished.IsSet)
            {
                DrainNotifications(notifications);
                if (terminalPublished.IsSet)
                {
                    break;
                }
                using var notificationCancellation = new CancellationTokenSource();
                Task notification = notificationSignal.WaitAsync(notificationCancellation.Token);
                try
                {
                    Task completed = await Task.WhenAny(notification, batchIdle);
                    await completed;
                    if (completed == batchIdle)
                    {
                        // 実終端までに投稿された通知を消費してから、必要な終端到達を判定する。
                        DrainNotifications(notifications);
                        Assert.IsTrue(terminalPublished.IsSet, "実処理の終端までに必要な終端通知が投稿されませんでした。");
                    }
                }
                finally
                {
                    notificationCancellation.Cancel();
                    try { await notification; }
                    catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
                }
            }
            Assert.IsTrue(terminalPublished.IsSet, "The terminal notification was not published.");
            await owner!.WaitForIdleAsync();
            await mutationTask!;
            Assert.IsNull(publishedFailure, "The bounded progress mutation must complete without publishing a failure.");

            CollectionAssert.AreEqual(
                new[] { "progress:64", "terminal", "completion", "terminal-published" },
                observations.ToArray());
            Assert.AreEqual(1, maximumQueuedNotifications);
            Assert.AreEqual(
                2,
                dispatchInvocationCount,
                "The batch may schedule one intermediate and one terminal notification; sealed late progress must not schedule another.");
        }
        catch (Exception exception)
        {
            bodyFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            bool backgroundDrained = true;
            mutationPort.Release();
            if (mutationTask != null)
            {
                try
                {
                    await mutationTask;
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            if (owner != null)
            {
                try
                {
                    Task idle = owner.WaitForIdleAsync();
                    while (!idle.IsCompleted)
                    {
                        DrainNotifications(notifications);
                        if (idle.IsCompleted)
                        {
                            break;
                        }
                        using var notificationCancellation = new CancellationTokenSource();
                        Task notification = notificationSignal.WaitAsync(notificationCancellation.Token);
                        try
                        {
                            Task completed = await Task.WhenAny(notification, idle);
                            await completed;
                        }
                        finally
                        {
                            notificationCancellation.Cancel();
                            try { await notification; }
                            catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
                        }
                    }
                    await idle;
                    DrainNotifications(notifications);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            else
            {
                try
                {
                    DrainNotifications(notifications);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    backgroundDrained = false;
                }
            }
            if (backgroundDrained)
            {
                try
                {
                    if (owner is not null) { await owner.WaitForIdleAsync(); }
                    DeleteRoot(root);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
        }

        if (bodyFailure != null)
        {
            if (cleanupFailure != null)
            {
                throw new AggregateException(
                    "The folder progress assertion failed and cleanup also failed.",
                    bodyFailure.SourceException,
                    cleanupFailure);
            }
            bodyFailure.Throw();
        }
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    /// <summary>
    /// 全フォルダ要求の対象判定は呼出し元と別threadで行い、実対象がなければ進捗・変更を始めず、UI上の重い判定や空実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task AllRequestWithoutActionableTargets_DoesNotStartProgressOrMutation()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            int executorCalls = 0;
            int progressCalls = 0;
            int callerThreadId = Thread.CurrentThread.ManagedThreadId;
            int checkerThreadId = 0;
            var dialogs = new AcceptedFolderDialogService();
            owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return CreateCommittedRenameResult();
                },
                (current, parentDirectory) =>
                {
                    checkerThreadId = Thread.CurrentThread.ManagedThreadId;
                    return false;
                },
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap(),
                action => action(),
                dialogs: dialogs);
            owner.AttachLibrary(library);
            owner.ProgressChanged += _ => Interlocked.Increment(ref progressCalls);

            await owner.RequestStartAllAsync(root);
            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, executorCalls);
            Assert.AreEqual(0, progressCalls);
            Assert.AreNotEqual(callerThreadId, checkerThreadId);
            Assert.AreEqual(1, dialogs.ConfirmationCalls);
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
    /// 承認した全フォルダ要求は指定親を一回だけworkerへ渡して完了を一回公開し、二重起動や親対象の差替えを検出する。
    /// </summary>
    [TestMethod]
    public async Task AllRequestAccepted_ConfirmsAndSchedulesAllFoldersOnce()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var dialogs = new AcceptedFolderDialogService();
            int completionCount = 0;
            int schedulerCalls = 0;
            int executorCalls = 0;
            string? observedParentDirectory = null;
            owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    observedParentDirectory = parentDirectory;
                    Interlocked.Increment(ref executorCalls);
                    return CreateCommittedRenameResult();
                },
                (current, parentDirectory) => true,
                action =>
                {
                    schedulerCalls++;
                    return action();
                },
                action => action(),
                dialogs: dialogs);
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => completionCount++;

            await owner.RequestStartAllAsync(root);

            Assert.AreEqual(1, dialogs.ConfirmationCalls);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_rename_folders, dialogs.LastConfirmationRequest.MessageBoxText);
            Assert.AreEqual(MessageBoxButton.OKCancel, dialogs.LastConfirmationRequest.Button);
            Assert.AreEqual(MessageBoxImage.Question, dialogs.LastConfirmationRequest.Icon);
            Assert.AreEqual(MessageBoxResult.Cancel, dialogs.LastConfirmationRequest.DefaultResult);
            Assert.AreEqual(1, schedulerCalls);
            Assert.AreEqual(1, completionCount);
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, executorCalls);
            Assert.AreEqual(root, observedParentDirectory);
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
    /// 保存確定後の必須反映失敗では元receiptと片付け原因・回復パスを保ち、失敗通知終端まで受付を保持して、成功通知や早期受付解放を検出する。
    /// </summary>
    // falseは元の必須反映失敗、trueは表示抑制解除の失敗も加える。両方で保存確定事実を維持する。
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AllRequest_DurableFinalizationFailurePublishesFailureWithoutSuccessCompletion(bool failSuppressionCleanup)
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var finalizationFailure = new IOException("auto finalization failed");
            var cleanupFailure = new IOException("auto cleanup failed");
            string cleanupPath = Path.Combine(root, "cleanup-source");
            var sessionReceipt = new LibraryMutationSessionReceipt(
                [new LibraryMutationSessionTarget(cleanupPath, Path.Combine(root, "destination"))],
                durableCommit: true,
                catalogFolderPathChangeCount: 1,
                folderReferenceMoveCount: 1,
                finalizationFailure: finalizationFailure,
                cleanupFailure: cleanupFailure);
            var mutationResult = new AutoRenameBatchResult(
                hasActionablePlan: true,
                appliedPlanCount: 1,
                sessionReceipt,
                primaryFailure: ExceptionDispatchInfo.Capture(finalizationFailure));
            var mutationPort = new TerminalFolderAutoRenameMutationPort(mutationResult);
            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            owner = new FolderAutoRenameWorkflowOwner(
                gate,
                activity,
                mutationPort,
                new NoOpChartMutationPlaybackPort(),
                action => Task.Run(action),
                action => action(),
                new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            var scopeFailure = new IOException("suppression cleanup failed");
            owner.RefreshSuppressionChanged += (_, args) =>
            {
                if (failSuppressionCleanup && !args.IsSuppressed)
                {
                    throw scopeFailure;
                }
            };
            var failurePublished = new TaskCompletionSource<FolderAutoRenameFailure>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int completionCount = 0;
            int terminalCount = 0;
            bool terminalAdmissionHeld = false;
            owner.FailurePublished += failure =>
            {
                bool acquired = gate.TryEnter(out IDisposable probe);
                terminalAdmissionHeld = !activity.IsActive && !acquired;
                if (acquired)
                {
                    probe.Dispose();
                }

                failurePublished.TrySetResult(failure);
            };
            owner.CompletionPublished += _ => Interlocked.Increment(ref completionCount);
            owner.TerminalPublished += () => Interlocked.Increment(ref terminalCount);

            await owner.RequestStartAllAsync(root);
            await TestUiDispatcherHost.AwaitNotificationAsync(failurePublished.Task, owner.WaitForIdleAsync(), "folder-auto-rename.failure-publication");
            FolderAutoRenameFailure failure = await failurePublished.Task;
            await owner.WaitForIdleAsync();

            Assert.IsTrue(terminalAdmissionHeld, "活動と短いモデル保護の回収後も、必要な失敗通知の実終端まで論理受付を保持します。");
            Assert.IsTrue(gate.TryEnter(out IDisposable next));
            next.Dispose();
            Assert.AreSame(failSuppressionCleanup ? scopeFailure : finalizationFailure, failure.Exception);
            Assert.IsTrue(failure.HasDurableCommit);
            Assert.IsTrue(failure.HasDurableFinalizationFailure);
            Assert.IsFalse(failure.CompletedWithCleanupFailure);
            CollectionAssert.Contains(failure.RecoveryPaths.ToArray(), cleanupPath);
            Assert.IsNotNull(failure.MutationResult);
            Assert.AreSame(cleanupFailure, failure.MutationResult.SessionReceipt.CleanupFailure);
            Assert.AreEqual(1, mutationPort.HasTargetsCallCount);
            Assert.AreEqual(1, mutationPort.RenameAllWithReceiptWithProgressCallCount);
            Assert.AreEqual(0, completionCount);
            Assert.AreEqual(1, terminalCount);
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
    /// 全フォルダの確認Cancelは確認だけで終えて変更workerを呼ばず、未承認の一括改名を検出する。
    /// </summary>
    [TestMethod]
    public async Task AllRequestCancelled_DoesNotScheduleMutation()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var dialogs = new AcceptedFolderDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            };
            int executorCalls = 0;
            owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
                },
                (current, parentDirectory) => true,
                action => Task.Run(action),
                action => action(),
                dialogs: dialogs);
            owner.AttachLibrary(library);

            await owner.RequestStartAllAsync(root);

            Assert.AreEqual(1, dialogs.ConfirmationCalls);
            Assert.AreEqual(0, executorCalls);
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
    /// 全フォルダ確認の表示失敗は呼出し元へ伝え、変更workerを起動せず、確認できない要求の実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task AllRequestDialogFailure_IsPropagatedBeforeMutation()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var dialogs = new AcceptedFolderDialogService
            {
                ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog failed"))
            };
            int executorCalls = 0;
            owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
                },
                (current, parentDirectory) => true,
                action => Task.Run(action),
                action => action(),
                dialogs: dialogs);
            owner.AttachLibrary(library);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.RequestStartAllAsync(root));

            Assert.AreEqual(1, dialogs.ConfirmationCalls);
            Assert.AreEqual(0, executorCalls);
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
    /// 全フォルダ確認の待機中にライブラリを交換すると、旧要求は確認が承認されても変更を始めず、旧対象への遅延実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task AllRequestLibraryReplacementDuringConfirmation_DoesNotScheduleOldRun()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        FolderAutoRenameWorkflowOwner? owner = null;
        AcceptedFolderDialogService? dialogs = null;
        Task? requestTask = null;
        try
        {
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            BMSLibrary second = CreateLibrary(secondRoot, "song.db");
            dialogs = new AcceptedFolderDialogService { DeferConfirmation = true };
            int executorCalls = 0;
            owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
                },
                (current, parentDirectory) => true,
                action => Task.Run(action),
                action => action(),
                dialogs: dialogs);
            owner.AttachLibrary(first);

            requestTask = owner.RequestStartAllAsync(firstRoot);
            await TestUiDispatcherHost.AwaitNotificationAsync(dialogs.ConfirmationStarted.Task, requestTask, "folder-all.confirmation-started");
            owner.AttachLibrary(second);
            dialogs.ReleaseConfirmation();
            await requestTask;

            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, executorCalls);
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
                dialogs?.ReleaseConfirmation();
                try
                {
                    if (requestTask is not null) { await requestTask; }
                    if (owner is not null) { await owner.WaitForIdleAsync(); }
                }
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
    /// 投稿済み終端が未受信の間はidleと次要求を止め、接続交換後は旧完了を捨て、受信後の新規要求だけを公開して早期終端や旧世代公開を検出する。
    /// </summary>
    [TestMethod]
    public async Task NextRequestWaitsForQueuedTerminalPublication()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        var notifications = new Queue<Action>();
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            string replacementRoot = Path.Combine(root, "replacement");
            Directory.CreateDirectory(replacementRoot);
            BMSLibrary replacement = CreateLibrary(replacementRoot, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            int completionCount = 0;
            owner = CreateOwner(
                (current, selectedRequest, progress) => new FolderAutoRenameExecutionResult { RefreshRequired = true },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action =>
                {
                    return action();
                },
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }
                },
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => Interlocked.Increment(ref completionCount);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            Assert.IsTrue(owner.IsActive);
            Assert.IsFalse(owner.RequestStartSelected(targets));
            Task firstIdle = owner.WaitForIdleAsync();
            Assert.IsFalse(firstIdle.IsCompleted, "Folder idle receipt must wait for queued terminal publication.");

            owner.AttachLibrary(replacement);
            Assert.IsFalse(firstIdle.IsCompleted, "A generation change must not complete the stale terminal before its notification is drained.");

            DrainNotifications(notifications);

            await owner.WaitForIdleAsync();
            Assert.IsTrue(firstIdle.IsCompleted);
            Assert.AreEqual(0, completionCount, "A stale terminal must not publish completion.");
            Assert.IsTrue(owner.RequestStartSelected(targets));
            DrainNotifications(notifications);
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
                DrainNotifications(notifications);
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
    /// 到達済みの選択改名を保持中は二件目を拒否し、解放後に完了通知と実終端を回収して、同時変更を検出する。
    /// </summary>
    [TestMethod]
    public async Task Start_RejectsDuplicateWhileSelectedRequestIsActive()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        using var release = new ManualResetEventSlim(false);
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap(),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => completed.TrySetResult(true);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.started");
            Assert.IsFalse(owner.RequestStartSelected(targets));
            release.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(completed.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.completed");
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
    /// 実行中の選択改名で接続を交換して旧完了を公開せず、現在ライブラリの新規要求だけ一回完了して、世代混線や新規受付の欠落を検出する。
    /// </summary>
    [TestMethod]
    public async Task AttachLibrary_SuppressesStaleCompletionAndAllowsReplacement()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        using var releaseFirst = new ManualResetEventSlim(false);
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            BMSLibrary second = CreateLibrary(secondRoot, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int completionCount = 0;
            owner = CreateOwner(
                (current, selectedRequest, progress) =>
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
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap(),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(first);
            owner.CompletionPublished += _ =>
            {
                Interlocked.Increment(ref completionCount);
                completion.TrySetResult(true);
            };

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await TestUiDispatcherHost.AwaitNotificationAsync(firstStarted.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.firstStarted");
            owner.AttachLibrary(second);
            releaseFirst.Set();
            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, completionCount);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await TestUiDispatcherHost.AwaitNotificationAsync(secondStarted.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.secondStarted");
            await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.completion");
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
    /// 共通譜面排他がBusyなら選択要求の変更を未開始で失敗通知し、解放後の新規要求だけ一回変更して、競合中の作用や自動再実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task SelectedRequest_FailsFastWhenSharedChartFileGateIsBusyThenRunsAfterRelease()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        string firstRoot = Path.Combine(root, "first");
        Directory.CreateDirectory(firstRoot);
        try
        {
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var chartMutationActivity = new ChartMutationActivityOwner();
            int mutationCalls = 0;
            Exception? observedFailure = null;
            owner = new FolderAutoRenameWorkflowOwner(
                chartFileOperations,
                chartMutationActivity,
                new DelegateFolderAutoRenameMutationPort(
                    (current, selectedRequest, progress) =>
                    {
                        Interlocked.Increment(ref mutationCalls);
                        return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                    },
                    (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                    (current, parentDirectory) => false),
                new NoOpChartMutationPlaybackPort(),
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap(),
                action => action(),
                new AcceptedFolderDialogService());
            owner.AttachLibrary(first);
            owner.FailurePublished += failure => observedFailure = failure.Exception;

            Assert.IsTrue(chartFileOperations.TryEnter(out IDisposable incumbent));
            try
            {
                Assert.IsTrue(owner.RequestStartSelected(targets));
                await owner.WaitForIdleAsync();
                Assert.AreEqual(0, mutationCalls);
                Assert.IsInstanceOfType(observedFailure, typeof(InvalidOperationException));
            }
            finally
            {
                incumbent.Dispose();
            }

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, mutationCalls);
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
    /// 接続交換時の表示リセットを保持し、旧workerの遅い完了で追加リセットを出さず、現在接続の表示初期化が失われる誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task AttachLibraryResetSurvivesStaleRunCompletion()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        using var releaseFirst = new ManualResetEventSlim(false);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new Queue<Action>();
        FolderAutoRenameWorkflowOwner? owner = null;
        Task? worker = null;
        try
        {
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            BMSLibrary second = CreateLibrary(secondRoot, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            int resetCount = 0;
            owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    if (ReferenceEquals(current, first))
                    {
                        firstStarted.TrySetResult(true);
                        releaseFirst.Wait();
                    }
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action =>
                {
                    worker = Task.Factory.StartNew(
                        action,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default).Unwrap();
                    return worker;
                },
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }
                },
                dialogs: new AcceptedFolderDialogService());
            owner.ProgressChanged += progress =>
            {
                if (progress.IsCompleted)
                {
                    Interlocked.Increment(ref resetCount);
                }
            };
            owner.AttachLibrary(first);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await TestUiDispatcherHost.AwaitNotificationAsync(firstStarted.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.firstStarted");
            owner.AttachLibrary(second);
            releaseFirst.Set();
            await owner.WaitForIdleAsync();

            DrainNotifications(notifications);

            Assert.AreEqual(1, resetCount);
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
                try
                {
                    if (worker is not null) { await worker; }
                    DrainNotifications(notifications);
                    if (owner != null) { await owner.WaitForIdleAsync(); }
                }
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
    /// 到達済みの選択改名は終了要求後も実終端まで回収し、後続要求を拒否して終了後の受付残存を検出する。
    /// </summary>
    [TestMethod]
    public async Task RequestShutdown_DrainsActiveRequestAndRejectsLaterRequest()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        using var release = new ManualResetEventSlim(false);
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    started.TrySetResult(true);
                    release.Wait();
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap(),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.started");
            owner.RequestShutdown();
            Assert.IsFalse(owner.RequestStartSelected(targets));
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
    /// 選択改名のexecutor例外を処理失敗として通知してidleへ戻し、失敗の隠蔽や受付の残存を検出する。
    /// </summary>
    [TestMethod]
    public async Task ExecutorFailure_PublishesFailureAndLeavesOwnerIdle()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? observed = null;
            owner = CreateOwner(
                (current, selectedRequest, progress) => throw new InvalidOperationException("rename failed"),
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action =>
                {
                    return action();
                },
                action => action(),
                reportWorkflowFailure: exception => observed = exception,
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.TrySetResult(true);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await TestUiDispatcherHost.AwaitNotificationAsync(failure.Task, owner.WaitForIdleAsync(), "FolderAutoRenameWorkflowOwnerTests.failure");
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
    /// worker起動とUI投稿がともに失敗しても主処理失敗を一回、通知失敗を一回以上報告し、idle待機が終端する。
    /// </summary>
    [TestMethod]
    public async Task SchedulerFailureAndNotificationFailure_DoNotLeaveOwnerActive()
    {

        string root = CreateRoot();
        Exception? testFailure = null;
        FolderAutoRenameWorkflowOwner? owner = null;
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            int workflowFailures = 0;
            int notificationFailures = 0;
            owner = CreateOwner(
                (current, selectedRequest, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => throw new InvalidOperationException("scheduler failed"),
                action => throw new InvalidOperationException("dispatcher failed"),
                reportNotificationFailure: _ => Interlocked.Increment(ref notificationFailures),
                reportWorkflowFailure: _ => Interlocked.Increment(ref workflowFailures),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);

            Assert.IsTrue(owner.RequestStartSelected(targets));
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

    private static AutoRenameBatchResult CreateCommittedRenameResult() =>
        new(
            hasActionablePlan: true,
            appliedPlanCount: 1,
            new LibraryMutationSessionReceipt(
                [new LibraryMutationSessionTarget("source", "destination")],
                durableCommit: true));

    private static FolderAutoRenameWorkflowOwner CreateOwner(
        Func<BMSLibrary, ChartFolderAutoRenameRequest, IFolderAutoRenameProgressWriter, FolderAutoRenameExecutionResult> executeSelected,
        Func<BMSLibrary, string, IFolderAutoRenameProgressWriter, AutoRenameBatchResult> executeAll,
        Func<BMSLibrary, string, bool> hasAllTargets,
        Func<Func<Task>, Task> schedule,
        Action<Action> dispatchToUi,
        IUiDialogService dialogs,
        Action<string>? logInfo = null,
        Action<Exception>? reportNotificationFailure = null,
        Action<Exception>? reportWorkflowFailure = null)
    {
        return new FolderAutoRenameWorkflowOwner(
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new DelegateFolderAutoRenameMutationPort(executeSelected, executeAll, hasAllTargets),
            new NoOpChartMutationPlaybackPort(),
            schedule,
            dispatchToUi,
            dialogs,
            logInfo,
            reportNotificationFailure,
            reportWorkflowFailure);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(FolderAutoRenameWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static BMSLibrary CreateLibrary(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, []);
        return new TestBmsLibrary(path, null, null, string.Empty);
    }

    private static IReadOnlyList<ChartOperationTarget> CreateSelectedTargets()
    {
        ChartFile bmsFile = ChartTestValues.Empty() with
        {
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Path = @"C:\Library\Source\chart.bms"
        };
        ChartFile chart = (bmsFile);
        var target = new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.MoveInLibrary);
        return [target];
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DrainNotifications(Queue<Action> notifications)
    {
        while (true)
        {
            Action notification;
            lock (notifications)
            {
                if (notifications.Count == 0)
                {
                    return;
                }
                notification = notifications.Dequeue();
            }
            notification();
        }
    }

    private sealed class BoundedProgressFolderAutoRenameMutationPort :
        IFolderAutoRenameMutationPort,
        IDisposable
    {
        private readonly int progressCount;

        private readonly ManualResetEventSlim release = new(false);

        private IFolderAutoRenameProgressWriter progressWriter = null!;

        internal BoundedProgressFolderAutoRenameMutationPort(int progressCount)
        {
            this.progressCount = progressCount;
        }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool MutationIsBlocked => Started.Task.IsCompleted && !release.IsSet;

        public bool HasTargets(BMSLibrary library, string parentDirectory) => false;

        public FolderAutoRenameExecutionResult RenameSelectedWithProgress(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            IFolderAutoRenameProgressWriter progressWriter)
        {
            this.progressWriter = progressWriter ?? throw new ArgumentNullException(nameof(progressWriter));
            for (int index = 1; index <= progressCount; index++)
            {
                progressWriter.TryWrite(new FolderAutoRenameProgressUpdate(
                    progressCount,
                    index,
                    "source-" + index));
            }
            Started.TrySetResult();
            release.Wait();
            Returned.TrySetResult();
            return new FolderAutoRenameExecutionResult { RefreshRequired = true };
        }

        public AutoRenameBatchResult RenameAllWithReceiptWithProgress(
            BMSLibrary library,
            string parentDirectory,
            IFolderAutoRenameProgressWriter progressWriter)
        {
            throw new AssertFailedException("all route was not expected");
        }

        internal void EmitLateProgress()
        {
            Volatile.Read(ref progressWriter)?.TryWrite(new FolderAutoRenameProgressUpdate(
                progressCount,
                progressCount,
                "late"));
        }

        internal void Release() => release.Set();

        public void Dispose()
        {
            release.Dispose();
        }
    }

    private sealed class DelegateFolderAutoRenameMutationPort : IFolderAutoRenameMutationPort
    {
        private readonly Func<BMSLibrary, ChartFolderAutoRenameRequest, IFolderAutoRenameProgressWriter, FolderAutoRenameExecutionResult> executeSelected;
        private readonly Func<BMSLibrary, string, IFolderAutoRenameProgressWriter, AutoRenameBatchResult> executeAll;
        private readonly Func<BMSLibrary, string, bool> hasAllTargets;

        internal DelegateFolderAutoRenameMutationPort(
            Func<BMSLibrary, ChartFolderAutoRenameRequest, IFolderAutoRenameProgressWriter, FolderAutoRenameExecutionResult> executeSelected,
            Func<BMSLibrary, string, IFolderAutoRenameProgressWriter, AutoRenameBatchResult> executeAll,
            Func<BMSLibrary, string, bool> hasAllTargets)
        {
            this.executeSelected = executeSelected ?? throw new ArgumentNullException(nameof(executeSelected));
            this.executeAll = executeAll ?? throw new ArgumentNullException(nameof(executeAll));
            this.hasAllTargets = hasAllTargets ?? throw new ArgumentNullException(nameof(hasAllTargets));
        }

        public bool HasTargets(BMSLibrary library, string parentDirectory) => hasAllTargets(library, parentDirectory);

        public FolderAutoRenameExecutionResult RenameSelectedWithProgress(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            IFolderAutoRenameProgressWriter progressWriter) => executeSelected(library, request, progressWriter);

        public AutoRenameBatchResult RenameAllWithReceiptWithProgress(
            BMSLibrary library,
            string parentDirectory,
            IFolderAutoRenameProgressWriter progressWriter) => executeAll(library, parentDirectory, progressWriter);
    }

    private sealed class TerminalFolderAutoRenameMutationPort :
        IFolderAutoRenameMutationPort
    {
        private readonly AutoRenameBatchResult result;

        internal TerminalFolderAutoRenameMutationPort(AutoRenameBatchResult result)
        {
            this.result = result ?? throw new ArgumentNullException(nameof(result));
        }

        internal int HasTargetsCallCount { get; private set; }

        internal int RenameAllWithReceiptWithProgressCallCount { get; private set; }

        public bool HasTargets(BMSLibrary library, string parentDirectory)
        {
            HasTargetsCallCount++;
            return true;
        }

        public FolderAutoRenameExecutionResult RenameSelectedWithProgress(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            IFolderAutoRenameProgressWriter progressWriter) =>
            throw new AssertFailedException("selected route was not expected");

        public AutoRenameBatchResult RenameAllWithReceiptWithProgress(
            BMSLibrary library,
            string parentDirectory,
            IFolderAutoRenameProgressWriter progressWriter)
        {
            RenameAllWithReceiptWithProgressCallCount++;
            return result;
        }
    }

    private sealed class AcceptedFolderDialogService : IUiDialogService
    {
        private readonly TaskCompletionSource<UiDialogResult> pendingConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal bool DeferConfirmation { get; set; }

        internal TaskCompletionSource<bool> ConfirmationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConfirmationCalls { get; private set; }

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; } = null!;

        internal void ReleaseConfirmation()
        {
            pendingConfirmation.TrySetResult(ConfirmationResult);
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCalls++;
            LastConfirmationRequest = request;
            ConfirmationStarted.TrySetResult(true);
            return DeferConfirmation
                ? pendingConfirmation.Task
                : Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
