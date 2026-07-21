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
        Assert.IsTrue(owner.IsIdle);
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
        Assert.IsTrue(owner.IsIdle);
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
        Assert.IsTrue(owner.IsIdle);
    }

    [TestMethod]
    public async Task RequestStartAsync_AcceptedButUnavailableOrActiveReturnsNotStarted()
    {
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        string root = CreateRoot();
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token) =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return new MaintenanceWorkflowResult();
            },
            action => Task.Run(action),
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
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
                MaintenanceRescanStartResult active = await owner.RequestStartAsync();
                Assert.AreEqual(MaintenanceRescanStartStatus.NotStarted, active.Status);
                release.Set();
                Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
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
    public void Start_PublishesTerminalProgressBeforeCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var events = new List<string>();
            var completion = new ManualResetEventSlim(false);
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
                completion.Set();
            };

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(completion.Wait(TimeSpan.FromSeconds(5)), "The rescan did not publish completion.");
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
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
    public void Start_RejectsDuplicateWhileTheCurrentRunIsActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new ManualResetEventSlim(false);
            var completed = new ManualResetEventSlim(false);
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return new MaintenanceWorkflowResult { Canceled = token.IsCancellationRequested };
                },
                action => Task.Run(action),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => completed.Set();

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)), "The first rescan did not start.");
            Assert.IsFalse(StartConfirmed(owner), "A second request must not overlap the active rescan.");
            release.Set();
            Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)), "The active rescan did not complete.");
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Cancel_CancelsLiveRunAndPublishesCanceledCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new ManualResetEventSlim(false);
            var canceledProgress = new ManualResetEventSlim(false);
            var completed = new ManualResetEventSlim(false);
            bool tokenWasCanceled = false;
            bool receiptWasCanceled = false;
            bool uncanceledProgressAfterCancel = false;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    tokenWasCanceled = token.IsCancellationRequested;
                    progress(new MaintenanceWorkflowProgress
                    {
                        TotalCount = 3,
                        ProcessedCount = 2,
                        CurrentPath = "after-cancel.bms"
                    });
                    return new MaintenanceWorkflowResult { Canceled = token.IsCancellationRequested };
                },
                action => Task.Run(action),
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
                completed.Set();
            };

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)), "The rescan did not start.");
            owner.Cancel();
            Assert.IsTrue(canceledProgress.IsSet, "Cancel must immediately disable the active progress state.");
            release.Set();
            Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)), "The canceled rescan did not publish completion.");
            Assert.IsTrue(tokenWasCanceled);
            Assert.IsTrue(receiptWasCanceled);
            Assert.IsFalse(uncanceledProgressAfterCancel);
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void CancelDuringExecutorReturn_PublishesCanceledReceipt()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var completion = new ManualResetEventSlim(false);
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
                completion.Set();
            };

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(completion.IsSet);
            Assert.IsTrue(receiptWasCanceled);
            Assert.IsTrue(owner.IsIdle);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void AttachLibrary_DropsStaleGenerationAndAllowsTheReplacementToRun()
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
            var firstStarted = new ManualResetEventSlim(false);
            var secondStarted = new ManualResetEventSlim(false);
            var completion = new ManualResetEventSlim(false);
            int completionCount = 0;
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    if (ReferenceEquals(current, first))
                    {
                        firstStarted.Set();
                        releaseFirst.Wait(TimeSpan.FromSeconds(5));
                    }
                    else
                    {
                        secondStarted.Set();
                    }
                    return new MaintenanceWorkflowResult();
                },
                action => Task.Run(action),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(first);
            owner.CompletionPublished += _ =>
            {
                Interlocked.Increment(ref completionCount);
                completion.Set();
            };

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(5)), "The first generation did not start.");
            owner.AttachLibrary(second);
            releaseFirst.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, completionCount, "A replaced generation must not publish completion.");

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(secondStarted.Wait(TimeSpan.FromSeconds(5)), "The replacement generation did not start.");
            Assert.IsTrue(completion.Wait(TimeSpan.FromSeconds(5)), "The replacement generation did not publish completion.");
            Assert.AreEqual(1, completionCount);
        }
        finally
        {
            releaseFirst.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void RequestShutdown_CancelsActiveRunAndBlocksLaterStart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var started = new ManualResetEventSlim(false);
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return new MaintenanceWorkflowResult { Canceled = token.IsCancellationRequested };
                },
                action => Task.Run(action),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            owner.RequestShutdown();
            Assert.IsFalse(StartConfirmed(owner), "Shutdown must prevent a new rescan.");
            release.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void SchedulerFailure_PublishesFailureAndLeavesOwnerIdle()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new ManualResetEventSlim(false);
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
                failure.Set();
            };

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(failure.IsSet);
            Assert.IsInstanceOfType(observed, typeof(InvalidOperationException));
            Assert.IsTrue(owner.IsIdle);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void CanceledSchedulerTask_DoesNotLeaveOwnerActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new ManualResetEventSlim(false);
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) => new MaintenanceWorkflowResult(),
                action => Task.FromCanceled(new CancellationToken(true)),
                action => action(),
                dialogs: new AcceptedDialogService());
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.Set();

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(failure.IsSet, "An unrequested canceled scheduler task must publish failure.");
            Assert.IsTrue(owner.IsIdle);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void ExecutorFailure_PublishesFailureAndLeavesOwnerIdle()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new ManualResetEventSlim(false);
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
            owner.FailurePublished += _ => failure.Set();

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(failure.IsSet);
            Assert.IsInstanceOfType(observed, typeof(InvalidOperationException));
            Assert.IsTrue(owner.IsIdle);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void NullExecutorResult_PublishesFailureInsteadOfSuccessfulCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new ManualResetEventSlim(false);
            var completion = new ManualResetEventSlim(false);
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
            owner.FailurePublished += _ => failure.Set();
            owner.CompletionPublished += _ => completion.Set();

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(failure.IsSet);
            Assert.IsFalse(completion.IsSet);
            Assert.IsTrue(owner.IsIdle);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void UnrequestedOperationCanceledException_PublishesFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var failure = new ManualResetEventSlim(false);
            var completion = new ManualResetEventSlim(false);
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
            owner.FailurePublished += _ => failure.Set();
            owner.CompletionPublished += _ => completion.Set();

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(failure.IsSet);
            Assert.IsFalse(completion.IsSet);
            Assert.IsTrue(owner.IsIdle);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void ExecutorFailure_IsReportedEvenWhenUiDispatchFails()
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

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(owner.IsIdle);
            Assert.AreEqual(1, workflowFailures);
            Assert.IsTrue(notificationFailures > 0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void DispatcherFailure_IsReportedWithoutLeavingOwnerActive()
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

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(owner.IsIdle);
            Assert.IsTrue(notificationFailures > 0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void ObserverFailure_DoesNotSuppressFollowingCompletionNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            var completion = new ManualResetEventSlim(false);
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
            owner.CompletionPublished += _ => completion.Set();

            Assert.IsTrue(StartConfirmed(owner));
            Assert.IsTrue(completion.IsSet);
            Assert.IsTrue(notificationFailures > 0);
            Assert.IsTrue(owner.IsIdle);
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
        return new BMSLibrary(path, null, null, string.Empty);
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        Directory.Delete(root, recursive: true);
    }

    private static bool StartConfirmed(MaintenanceRescanWorkflowOwner owner)
    {
        return owner.RequestStartAsync().GetAwaiter().GetResult().Started;
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
