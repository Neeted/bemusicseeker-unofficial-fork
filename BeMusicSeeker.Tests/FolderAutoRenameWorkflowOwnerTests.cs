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
    [TestMethod]
    public async Task SelectedRequest_PublishesTerminalProgressBeforeCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            ChartFolderAutoRenameRequest? observedRequest = null;
            var events = new List<string>();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            FolderAutoRenameCompletionReceipt? receipt = null;
            var owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    observedRequest = selectedRequest;
                    progress(2, 1, "source");
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action =>
                {
                    action();
                    return Task.CompletedTask;
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
            await completion.Task;
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ProgressWriter_BoundsSelectedDispatchAndDropsLateProgressAfterSeal()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
                new NoopFolderAutoRenamePlaybackPort(),
                action =>
                {
                    Task scheduled = Task.Factory.StartNew(
                        action,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
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
            Assert.IsTrue(
                mutationPort.Started.Wait(TimeSpan.FromSeconds(5)),
                "The selected progress mutation did not start.");
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
            Assert.IsTrue(
                mutationPort.Returned.Wait(TimeSpan.FromSeconds(5)),
                "The selected progress mutation did not return after release.");
            while (!terminalPublished.IsSet)
            {
                DrainNotifications(notifications);
                if (terminalPublished.IsSet)
                {
                    break;
                }
                Assert.IsTrue(
                    await notificationSignal.WaitAsync(TimeSpan.FromSeconds(5)),
                    "The terminal notification dispatcher must be signaled.");
            }
            Assert.IsTrue(terminalPublished.IsSet, "The terminal notification was not published.");
            await owner!.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await mutationTask!.WaitAsync(TimeSpan.FromSeconds(5));
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
                    await mutationTask.WaitAsync(TimeSpan.FromSeconds(5));
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
                        if (!await notificationSignal.WaitAsync(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("The folder auto-rename cleanup did not publish terminal notification.");
                        }
                    }
                    await idle.WaitAsync(TimeSpan.FromSeconds(5));
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

    [TestMethod]
    public async Task AllRequestWithoutActionableTargets_DoesNotStartProgressOrMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            int executorCalls = 0;
            int progressCalls = 0;
            int callerThreadId = Thread.CurrentThread.ManagedThreadId;
            int checkerThreadId = 0;
            var dialogs = new AcceptedFolderDialogService();
            var owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
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
                    TaskScheduler.Default),
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AllRequestAccepted_ConfirmsAndSchedulesAllFoldersOnce()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var dialogs = new AcceptedFolderDialogService();
            int completionCount = 0;
            int schedulerCalls = 0;
            int executorCalls = 0;
            string? observedParentDirectory = null;
            var owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    observedParentDirectory = parentDirectory;
                    Interlocked.Increment(ref executorCalls);
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory) => true,
                action =>
                {
                    schedulerCalls++;
                    action();
                    return Task.CompletedTask;
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AllRequest_DurableFinalizationFailurePublishesFailureWithoutSuccessCompletion(bool failSuppressionCleanup)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var finalizationFailure = new IOException("auto finalization failed");
            var cleanupFailure = new IOException("auto cleanup failed");
            string cleanupPath = Path.Combine(root, "cleanup-source");
            var mutationReceipt = new FileDbMutationBatchReceipt([
                new FileDbMutationReceipt(
                    Guid.NewGuid(),
                    FileDbMutationTerminalState.DurableFinalizationFailed,
                    durableCommit: true,
                    compensationAttemptCount: 0,
                    cleanupAttemptCount: 1,
                    sourcePaths: [cleanupPath],
                    destinationPaths: [Path.Combine(root, "destination")],
                    stagingPaths: [],
                    backupPaths: [],
                    recoveryPaths: [cleanupPath],
                    failure: finalizationFailure,
                    finalizationFailure: finalizationFailure,
                    cleanupFailure: cleanupFailure)]);
            var mutationResult = new AutoRenameBatchResult(
                hasActionablePlan: true,
                appliedPlanCount: 1,
                mutationReceipt,
                primaryFailure: ExceptionDispatchInfo.Capture(finalizationFailure));
            var mutationPort = new TerminalFolderAutoRenameMutationPort(mutationResult);
            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            var owner = new FolderAutoRenameWorkflowOwner(
                gate,
                activity,
                mutationPort,
                new NoopFolderAutoRenamePlaybackPort(),
                action => Task.Run(action),
                action => action(),
                new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            var scopeFailure = new IOException("suppression cleanup failed");
            owner.RefreshSuppressionChanged += (_, args) =>
            {
                if (failSuppressionCleanup && !args.IsSuppressed) throw scopeFailure;
            };
            var failurePublished = new TaskCompletionSource<FolderAutoRenameFailure>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int completionCount = 0;
            int terminalCount = 0;
            bool releasedAtPublication = false;
            owner.FailurePublished += failure =>
            {
                bool acquired = gate.TryEnter(out IDisposable probe);
                releasedAtPublication = !activity.IsActive && acquired;
                if (acquired) probe.Dispose();
                failurePublished.TrySetResult(failure);
            };
            owner.CompletionPublished += _ => Interlocked.Increment(ref completionCount);
            owner.TerminalPublished += () => Interlocked.Increment(ref terminalCount);

            await owner.RequestStartAllAsync(root);
            FolderAutoRenameFailure failure = await failurePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.WaitForIdleAsync();

            Assert.IsTrue(releasedAtPublication);
            Assert.AreSame(failSuppressionCleanup ? scopeFailure : finalizationFailure, failure.Exception);
            Assert.IsTrue(failure.HasDurableCommit);
            Assert.IsTrue(failure.HasDurableFinalizationFailure);
            Assert.IsFalse(failure.CompletedWithCleanupFailure);
            CollectionAssert.Contains(failure.RecoveryPaths.ToArray(), cleanupPath);
            Assert.IsNotNull(failure.MutationResult);
            Assert.IsTrue(failure.MutationResult.MutationReceipt.Receipts[0].HasCleanupFailure);
            Assert.AreEqual(1, mutationPort.HasTargetsCallCount);
            Assert.AreEqual(1, mutationPort.RenameAllWithReceiptCallCount);
            Assert.AreEqual(0, completionCount);
            Assert.AreEqual(1, terminalCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AllRequestCancelled_DoesNotScheduleMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var dialogs = new AcceptedFolderDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            };
            int executorCalls = 0;
            var owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return new FolderAutoRenameExecutionResult();
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AllRequestDialogFailure_IsPropagatedBeforeMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var dialogs = new AcceptedFolderDialogService
            {
                ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog failed"))
            };
            int executorCalls = 0;
            var owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return new FolderAutoRenameExecutionResult();
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AllRequestLibraryReplacementDuringConfirmation_DoesNotScheduleOldRun()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            BMSLibrary second = CreateLibrary(secondRoot, "song.db");
            var dialogs = new AcceptedFolderDialogService { DeferConfirmation = true };
            int executorCalls = 0;
            var owner = CreateOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return new FolderAutoRenameExecutionResult();
                },
                (current, parentDirectory) => true,
                action => Task.Run(action),
                action => action(),
                dialogs: dialogs);
            owner.AttachLibrary(first);

            Task requestTask = owner.RequestStartAllAsync(firstRoot);
            await dialogs.ConfirmationStarted.Task;
            owner.AttachLibrary(second);
            dialogs.ReleaseConfirmation();
            await requestTask;

            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, executorCalls);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task NextRequestWaitsForQueuedTerminalPublication()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            string replacementRoot = Path.Combine(root, "replacement");
            Directory.CreateDirectory(replacementRoot);
            BMSLibrary replacement = CreateLibrary(replacementRoot, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var notifications = new Queue<Action>();
            int completionCount = 0;
            var owner = CreateOwner(
                (current, selectedRequest, progress) => new FolderAutoRenameExecutionResult { RefreshRequired = true },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action =>
                {
                    action();
                    return Task.CompletedTask;
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Start_RejectsDuplicateWhileSelectedRequestIsActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    started.TrySetResult(true);
                    release.Wait(TimeSpan.FromSeconds(5));
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => completed.TrySetResult(true);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await started.Task;
            Assert.IsFalse(owner.RequestStartSelected(targets));
            release.Set();
            await completed.Task;
            await owner.WaitForIdleAsync();
        }
        finally
        {
            release.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AttachLibrary_SuppressesStaleCompletionAndAllowsReplacement()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var releaseFirst = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            BMSLibrary second = CreateLibrary(secondRoot, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int completionCount = 0;
            var owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    if (ReferenceEquals(current, first))
                    {
                        firstStarted.TrySetResult(true);
                        releaseFirst.Wait(TimeSpan.FromSeconds(5));
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
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(first);
            owner.CompletionPublished += _ =>
            {
                Interlocked.Increment(ref completionCount);
                completion.TrySetResult(true);
            };

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await firstStarted.Task;
            owner.AttachLibrary(second);
            releaseFirst.Set();
            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, completionCount);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await secondStarted.Task;
            await completion.Task;
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, completionCount);
        }
        finally
        {
            releaseFirst.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SelectedRequest_FailsFastWhenSharedChartFileGateIsBusyThenRunsAfterRelease()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
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
            var owner = new FolderAutoRenameWorkflowOwner(
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
                new NoopFolderAutoRenamePlaybackPort(),
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AttachLibraryResetSurvivesStaleRunCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var releaseFirst = new ManualResetEventSlim(false);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new Queue<Action>();
        try
        {
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            BMSLibrary second = CreateLibrary(secondRoot, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            int resetCount = 0;
            var owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    if (ReferenceEquals(current, first))
                    {
                        firstStarted.TrySetResult(true);
                        releaseFirst.Wait(TimeSpan.FromSeconds(5));
                    }
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
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
            await firstStarted.Task;
            owner.AttachLibrary(second);
            releaseFirst.Set();
            await owner.WaitForIdleAsync();

            DrainNotifications(notifications);

            Assert.AreEqual(1, resetCount);
        }
        finally
        {
            releaseFirst.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RequestShutdown_DrainsActiveRequestAndRejectsLaterRequest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = CreateOwner(
                (current, selectedRequest, progress) =>
                {
                    started.TrySetResult(true);
                    release.Wait(TimeSpan.FromSeconds(5));
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await started.Task;
            owner.RequestShutdown();
            Assert.IsFalse(owner.RequestStartSelected(targets));
            release.Set();
            await owner.WaitForIdleAsync();
        }
        finally
        {
            release.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ExecutorFailure_PublishesFailureAndLeavesOwnerIdle()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? observed = null;
            var owner = CreateOwner(
                (current, selectedRequest, progress) => throw new InvalidOperationException("rename failed"),
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                action => action(),
                reportWorkflowFailure: exception => observed = exception,
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.TrySetResult(true);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            await failure.Task;
            Assert.IsInstanceOfType(observed, typeof(InvalidOperationException));
            await owner.WaitForIdleAsync();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SchedulerFailureAndNotificationFailure_DoNotLeaveOwnerActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            int workflowFailures = 0;
            int notificationFailures = 0;
            var owner = CreateOwner(
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
        finally
        {
            DeleteRoot(root);
        }
    }

    private static FolderAutoRenameWorkflowOwner CreateOwner(
        Func<BMSLibrary, ChartFolderAutoRenameRequest, Action<int, int, string>, FolderAutoRenameExecutionResult> executeSelected,
        Func<BMSLibrary, string, Action<int, int, string>, FolderAutoRenameExecutionResult> executeAll,
        Func<BMSLibrary, string, bool> hasAllTargets,
        Func<Action, Task> schedule,
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
            new NoopFolderAutoRenamePlaybackPort(),
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
        using (var initialize = new LR2SongDBExtended(path))
        {
        }
        return new TestBmsLibrary(path, null, null, string.Empty);
    }

    private static IReadOnlyList<ChartOperationTarget> CreateSelectedTargets()
    {
        var bmsFile = new TestableBmsFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        {
            path = @"C:\Library\Source\chart.bms"
        };
        ChartFile chart = ChartFileProjection.FromBmsFile(bmsFile);
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
        IFolderAutoRenameProgressMutationPort,
        IDisposable
    {
        private readonly int progressCount;

        private readonly ManualResetEventSlim release = new(false);

        private IFolderAutoRenameProgressWriter progressWriter = null!;

        internal BoundedProgressFolderAutoRenameMutationPort(int progressCount)
        {
            this.progressCount = progressCount;
        }

        internal ManualResetEventSlim Started { get; } = new(false);

        internal ManualResetEventSlim Returned { get; } = new(false);

        internal bool MutationIsBlocked => Started.IsSet && !release.IsSet;

        public bool HasTargets(BMSLibrary library, string parentDirectory) => false;

        public FolderAutoRenameExecutionResult RenameSelected(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            Action<int, int, string> progressReporter)
        {
            throw new AssertFailedException("The writer-only folder route was not selected.");
        }

        public bool RenameAll(
            BMSLibrary library,
            string parentDirectory,
            Action<int, int, string> progressReporter)
        {
            throw new AssertFailedException("The writer-only folder route was not selected.");
        }

        public AutoRenameBatchResult RenameAllWithReceipt(
            BMSLibrary library,
            string parentDirectory,
            Action<int, int, string> progressReporter)
        {
            throw new AssertFailedException("The writer-only folder route was not selected.");
        }

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
            Started.Set();
            release.Wait();
            Returned.Set();
            return new FolderAutoRenameExecutionResult { RefreshRequired = true };
        }

        public bool RenameAllWithProgress(
            BMSLibrary library,
            string parentDirectory,
            IFolderAutoRenameProgressWriter progressWriter)
        {
            throw new AssertFailedException("all route was not expected");
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
            Returned.Dispose();
            Started.Dispose();
            release.Dispose();
        }
    }

    private sealed class DelegateFolderAutoRenameMutationPort : IFolderAutoRenameMutationPort
    {
        private readonly Func<BMSLibrary, ChartFolderAutoRenameRequest, Action<int, int, string>, FolderAutoRenameExecutionResult> executeSelected;
        private readonly Func<BMSLibrary, string, Action<int, int, string>, FolderAutoRenameExecutionResult> executeAll;
        private readonly Func<BMSLibrary, string, bool> hasAllTargets;

        internal DelegateFolderAutoRenameMutationPort(
            Func<BMSLibrary, ChartFolderAutoRenameRequest, Action<int, int, string>, FolderAutoRenameExecutionResult> executeSelected,
            Func<BMSLibrary, string, Action<int, int, string>, FolderAutoRenameExecutionResult> executeAll,
            Func<BMSLibrary, string, bool> hasAllTargets)
        {
            this.executeSelected = executeSelected ?? throw new ArgumentNullException(nameof(executeSelected));
            this.executeAll = executeAll ?? throw new ArgumentNullException(nameof(executeAll));
            this.hasAllTargets = hasAllTargets ?? throw new ArgumentNullException(nameof(hasAllTargets));
        }

        public bool HasTargets(BMSLibrary library, string parentDirectory) => hasAllTargets(library, parentDirectory);

        public FolderAutoRenameExecutionResult RenameSelected(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            Action<int, int, string> progressReporter) => executeSelected(library, request, progressReporter);

        public bool RenameAll(
            BMSLibrary library,
            string parentDirectory,
            Action<int, int, string> progressReporter) => executeAll(library, parentDirectory, progressReporter)?.RefreshRequired == true;
    }

    private sealed class TerminalFolderAutoRenameMutationPort :
        IFolderAutoRenameMutationPort,
        IFolderAutoRenameTerminalMutationPort
    {
        private readonly AutoRenameBatchResult result;

        internal TerminalFolderAutoRenameMutationPort(AutoRenameBatchResult result)
        {
            this.result = result ?? throw new ArgumentNullException(nameof(result));
        }

        internal int HasTargetsCallCount { get; private set; }

        internal int RenameAllWithReceiptCallCount { get; private set; }

        public bool HasTargets(BMSLibrary library, string parentDirectory)
        {
            HasTargetsCallCount++;
            return true;
        }

        public FolderAutoRenameExecutionResult RenameSelected(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            Action<int, int, string> progressReporter) =>
            throw new AssertFailedException("selected route was not expected");

        public bool RenameAll(
            BMSLibrary library,
            string parentDirectory,
            Action<int, int, string> progressReporter) =>
            throw new AssertFailedException("legacy all-folder route was not expected");

        public AutoRenameBatchResult RenameAllWithReceipt(
            BMSLibrary library,
            string parentDirectory,
            Action<int, int, string> progressReporter)
        {
            RenameAllWithReceiptCallCount++;
            return result;
        }
    }

    private sealed class NoopFolderAutoRenamePlaybackPort : IFolderAutoRenamePlaybackPort
    {
        public void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts)
        {
        }

        public void StopPlaybackForFolderMutation()
        {
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

    private sealed class TestableBmsFile : BMSFile
    {
        internal TestableBmsFile(string hash)
        {
            this.hash = hash;
        }
    }
}
