using System;
using System.Collections.Generic;
using System.IO;
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
public sealed class MaintenanceRescanWorkflowOwnerTests
{
    [TestMethod]
    public async Task RequestStartAsync_RejectedConfirmationDoesNotSchedule()
    {
        int executionCalls = 0;
        var dialogs = new AcceptedDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token) =>
            {
                executionCalls++;
                return new MaintenanceWorkflowResult();
            },
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            action => action(),
            dialogs: dialogs);

        MaintenanceRescanStartResult result = await owner.RequestStartAsync();

        Assert.AreEqual(MaintenanceRescanStartStatus.Rejected, result.Status);
        Assert.AreEqual(0, executionCalls);
        await owner.WaitForIdleAsync();
    }

    [TestMethod]
    public async Task RequestStartAsync_DialogFailureIsExplicit()
    {
        var dialogs = new AcceptedDialogService
        {
            ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog unavailable"))
        };
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token) => new MaintenanceWorkflowResult(),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            action => action(),
            dialogs: dialogs);

        MaintenanceRescanStartResult result = await owner.RequestStartAsync();

        Assert.AreEqual(MaintenanceRescanStartStatus.Failed, result.Status);
        Assert.IsNotNull(result.Failure);
        await owner.WaitForIdleAsync();
    }

    [TestMethod]
    public async Task RequestStartAsync_PreservesConfirmationRequestAndTreatsWindowCloseAsRejected()
    {
        var dialogs = new AcceptedDialogService
        {
            ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.Cancel)
        };
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token) => new MaintenanceWorkflowResult(),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            action => action(),
            dialogs: dialogs);

        MaintenanceRescanStartResult result = await owner.RequestStartAsync();

        Assert.AreEqual(MaintenanceRescanStartStatus.Rejected, result.Status);
        Assert.AreEqual(1, dialogs.ConfirmationCalls);
        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_rescan_all_charts_confirm, dialogs.LastConfirmationRequest.MessageBoxText);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Confirm, dialogs.LastConfirmationRequest.Caption);
        Assert.AreEqual(MessageBoxButton.OKCancel, dialogs.LastConfirmationRequest.Button);
        Assert.AreEqual(MessageBoxImage.Question, dialogs.LastConfirmationRequest.Icon);
        Assert.AreEqual(MessageBoxResult.Cancel, dialogs.LastConfirmationRequest.DefaultResult);
    }

    [TestMethod]
    public async Task RequestStartAsync_DialogUnavailableIsFailureInsteadOfRejection()
    {
        var dialogs = new AcceptedDialogService
        {
            ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable)
        };
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token) => new MaintenanceWorkflowResult(),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            action => action(),
            dialogs: dialogs);

        MaintenanceRescanStartResult result = await owner.RequestStartAsync();

        Assert.AreEqual(MaintenanceRescanStartStatus.Failed, result.Status);
        Assert.IsNotNull(result.Failure);
        await owner.WaitForIdleAsync();
    }

    [TestMethod]
    public async Task RequestStartAsync_AcceptedButUnavailableOrActiveReturnsNotStarted()
    {
        var release = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string root = CreateRoot();
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token) =>
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
                await started.Task;
                MaintenanceRescanStartResult active = await owner.RequestStartAsync();
                Assert.AreEqual(MaintenanceRescanStartStatus.NotStarted, active.Status);
                release.Set();
                await owner.WaitForIdleAsync();
            }
            finally
            {
                release.Set();
                DeleteRoot(root);
            }
        }
        finally
        {
            release.Set();
        }
    }

    [TestMethod]
    public async Task Start_PublishesTerminalProgressBeforeCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var events = new List<string>();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MaintenanceRescanCompletionReceipt receipt = null!;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
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
            await completion.Task;
            await owner.WaitForIdleAsync();
            CollectionAssert.AreEqual(
                new[] { "initial", "progress", "terminal", "completion" },
                events.ToArray());
            Assert.IsNotNull(receipt);
            Assert.IsTrue(receipt.Result.HasUpdates);
            Assert.AreEqual(3, receipt.Result.CheckedFileCount);
            Assert.IsFalse(receipt.Canceled);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Start_RejectsDuplicateWhileTheCurrentRunIsActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    started.TrySetResult(true);
                    release.Wait(TimeSpan.FromSeconds(5));
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
            await started.Task;
            Assert.IsFalse(await StartConfirmed(owner), "A second request must not overlap the active rescan.");
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
    public async Task Cancel_CancelsLiveRunAndPublishesCanceledCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceledProgress = new ManualResetEventSlim(false);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool tokenWasCanceled = false;
            bool receiptWasCanceled = false;
            bool uncanceledProgressAfterCancel = false;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
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
                    canceledProgress.Set();
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
            await started.Task;
            owner.Cancel();
            Assert.IsTrue(canceledProgress.IsSet, "Cancel must immediately disable the active progress state.");
            release.Set();
            await completed.Task;
            Assert.IsTrue(tokenWasCanceled);
            Assert.IsTrue(receiptWasCanceled);
            Assert.IsFalse(uncanceledProgressAfterCancel);
            await owner.WaitForIdleAsync();
        }
        finally
        {
            release.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CancelDuringExecutorReturn_PublishesCanceledReceipt()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool receiptWasCanceled = false;
            MaintenanceRescanWorkflowOwner owner = null!;
            owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
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
            await completion.Task;
            Assert.IsTrue(receiptWasCanceled);
            await owner.WaitForIdleAsync();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AttachLibrary_DropsStaleGenerationAndAllowsTheReplacementToRun()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var releaseFirst = new ManualResetEventSlim(false);
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
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
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
            await firstStarted.Task;
            owner.AttachLibrary(second);
            releaseFirst.Set();
            await owner.WaitForIdleAsync();
            Assert.AreEqual(0, completionCount, "A replaced generation must not publish completion.");

            Assert.IsTrue(await StartConfirmed(owner));
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
    public async Task RequestShutdown_CancelsActiveRunAndBlocksLaterStart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    started.TrySetResult(true);
                    release.Wait(TimeSpan.FromSeconds(5));
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
            await started.Task;
            owner.RequestShutdown();
            Assert.IsFalse(await StartConfirmed(owner), "Shutdown must prevent a new rescan.");
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
    public async Task SchedulerFailure_PublishesFailureAndLeavesOwnerIdle()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception observed = null!;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => new MaintenanceWorkflowResult(),
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
    public async Task CanceledSchedulerTask_DoesNotLeaveOwnerActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => new MaintenanceWorkflowResult(),
                action => Task.FromCanceled(new CancellationToken(true)),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.TrySetResult(true);

            Assert.IsTrue(await StartConfirmed(owner));
            await failure.Task;
            await owner.WaitForIdleAsync();
        }
        finally
        {
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
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception observed = null!;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => throw new InvalidOperationException("executor failed"),
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
    public async Task NullExecutorResult_PublishesFailureInsteadOfSuccessfulCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => null!,
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
            await failure.Task;
            Assert.IsFalse(completion.Task.IsCompleted);
            await owner.WaitForIdleAsync();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UnrequestedOperationCanceledException_PublishesFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => throw new OperationCanceledException("unexpected cancellation"),
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
            await failure.Task;
            Assert.IsFalse(completion.Task.IsCompleted);
            await owner.WaitForIdleAsync();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ExecutorFailure_IsReportedEvenWhenUiDispatchFails()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            int workflowFailures = 0;
            int notificationFailures = 0;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => throw new InvalidOperationException("executor failed"),
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task DispatcherFailure_IsReportedWithoutLeavingOwnerActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            int notificationFailures = 0;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => new MaintenanceWorkflowResult(),
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
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ObserverFailure_DoesNotSuppressFollowingCompletionNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int notificationFailures = 0;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => new MaintenanceWorkflowResult(),
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
            await completion.Task;
            Assert.IsTrue(notificationFailures > 0);
            await owner.WaitForIdleAsync();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(MaintenanceRescanWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
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

    private sealed class AcceptedDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal int ConfirmationCalls { get; private set; }

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; } = null!;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCalls++;
            LastConfirmationRequest = request;
            return Task.FromResult(ConfirmationResult);
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
