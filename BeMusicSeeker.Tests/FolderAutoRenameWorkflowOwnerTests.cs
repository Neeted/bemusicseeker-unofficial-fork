using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FolderAutoRenameWorkflowOwnerTests
{
    [TestMethod]
    public void SelectedRequest_PublishesTerminalProgressBeforeCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            ChartFolderAutoRenameRequest observedRequest = null!;
            var events = new List<string>();
            var completion = new ManualResetEventSlim(false);
            FolderAutoRenameCompletionReceipt receipt = null!;
            var owner = new FolderAutoRenameWorkflowOwner(
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
                completion.Set();
            };
            owner.TerminalPublished += () =>
            {
                lock (events)
                {
                    events.Add("terminal-published");
                }
            };

            Assert.IsTrue(owner.RequestStartSelected(targets));
            Assert.IsTrue(completion.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            CollectionAssert.AreEqual(
                new[] { "initial", "progress", "terminal", "completion", "terminal-published" },
                events.ToArray());
            Assert.IsNotNull(receipt);
            Assert.AreSame(targets[0].Chart, observedRequest.Charts[0]);
            Assert.IsFalse(receipt.AllFolders);
            Assert.IsTrue(receipt.RefreshRequired);
        }
        finally
        {
            DeleteRoot(root);
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
            var owner = new FolderAutoRenameWorkflowOwner(
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
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
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
            var completion = new ManualResetEventSlim(false);
            int executorCalls = 0;
            string observedParentDirectory = null!;
            var owner = new FolderAutoRenameWorkflowOwner(
                (current, request, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) =>
                {
                    observedParentDirectory = parentDirectory;
                    Interlocked.Increment(ref executorCalls);
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory) => true,
                action => Task.Run(action),
                action => action(),
                dialogs: dialogs);
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => completion.Set();

            await owner.RequestStartAllAsync(root);

            Assert.AreEqual(1, dialogs.ConfirmationCalls);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_rename_folders, dialogs.LastConfirmationRequest.MessageBoxText);
            Assert.AreEqual(MessageBoxButton.OKCancel, dialogs.LastConfirmationRequest.Button);
            Assert.AreEqual(MessageBoxImage.Question, dialogs.LastConfirmationRequest.Icon);
            Assert.AreEqual(MessageBoxResult.Cancel, dialogs.LastConfirmationRequest.DefaultResult);
            Assert.IsTrue(completion.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, executorCalls);
            Assert.AreEqual(root, observedParentDirectory);
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
            var owner = new FolderAutoRenameWorkflowOwner(
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
            Assert.IsTrue(owner.IsIdle);
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
            var owner = new FolderAutoRenameWorkflowOwner(
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
            Assert.IsTrue(owner.IsIdle);
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
            var owner = new FolderAutoRenameWorkflowOwner(
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
            Assert.IsTrue(dialogs.ConfirmationStarted.Wait(TimeSpan.FromSeconds(5)));
            owner.AttachLibrary(second);
            dialogs.ReleaseConfirmation();
            await requestTask;

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, executorCalls);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void NextRequestWaitsForQueuedTerminalPublication()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var notifications = new Queue<Action>();
            int completionCount = 0;
            var owner = new FolderAutoRenameWorkflowOwner(
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

            DrainNotifications(notifications);

            Assert.IsTrue(owner.IsIdle);
            Assert.AreEqual(1, completionCount);
            Assert.IsTrue(owner.RequestStartSelected(targets));
            DrainNotifications(notifications);
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(2, completionCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Start_RejectsDuplicateWhileSelectedRequestIsActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var started = new ManualResetEventSlim(false);
            var completed = new ManualResetEventSlim(false);
            var owner = new FolderAutoRenameWorkflowOwner(
                (current, selectedRequest, progress) =>
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Run(action),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => completed.Set();

            Assert.IsTrue(owner.RequestStartSelected(targets));
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(owner.RequestStartSelected(targets));
            release.Set();
            Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void AttachLibrary_SuppressesStaleCompletionAndAllowsReplacement()
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
            var firstStarted = new ManualResetEventSlim(false);
            var secondStarted = new ManualResetEventSlim(false);
            var completion = new ManualResetEventSlim(false);
            int completionCount = 0;
            var owner = new FolderAutoRenameWorkflowOwner(
                (current, selectedRequest, progress) =>
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
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Run(action),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(first);
            owner.CompletionPublished += _ =>
            {
                Interlocked.Increment(ref completionCount);
                completion.Set();
            };

            Assert.IsTrue(owner.RequestStartSelected(targets));
            Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(5)));
            owner.AttachLibrary(second);
            releaseFirst.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, completionCount);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            Assert.IsTrue(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(completion.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, completionCount);
        }
        finally
        {
            releaseFirst.Set();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void AttachLibraryResetSurvivesStaleRunCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var releaseFirst = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        var notifications = new Queue<Action>();
        try
        {
            BMSLibrary first = CreateLibrary(firstRoot, "song.db");
            BMSLibrary second = CreateLibrary(secondRoot, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            int resetCount = 0;
            var owner = new FolderAutoRenameWorkflowOwner(
                (current, selectedRequest, progress) =>
                {
                    if (ReferenceEquals(current, first))
                    {
                        firstStarted.Set();
                        releaseFirst.Wait(TimeSpan.FromSeconds(5));
                    }
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Run(action),
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
            Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(5)));
            owner.AttachLibrary(second);
            releaseFirst.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));

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
    public void RequestShutdown_DrainsActiveRequestAndRejectsLaterRequest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        var release = new ManualResetEventSlim(false);
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var started = new ManualResetEventSlim(false);
            var owner = new FolderAutoRenameWorkflowOwner(
                (current, selectedRequest, progress) =>
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return new FolderAutoRenameExecutionResult { RefreshRequired = true };
                },
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => Task.Run(action),
                action => action(),
                dialogs: new AcceptedFolderDialogService());
            owner.AttachLibrary(library);

            Assert.IsTrue(owner.RequestStartSelected(targets));
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            owner.RequestShutdown();
            Assert.IsFalse(owner.RequestStartSelected(targets));
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
    public void ExecutorFailure_PublishesFailureAndLeavesOwnerIdle()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            var failure = new ManualResetEventSlim(false);
            Exception observed = null!;
            var owner = new FolderAutoRenameWorkflowOwner(
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
            owner.FailurePublished += _ => failure.Set();

            Assert.IsTrue(owner.RequestStartSelected(targets));
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
    public void SchedulerFailureAndNotificationFailure_DoNotLeaveOwnerActive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = CreateRoot();
        try
        {
            BMSLibrary library = CreateLibrary(root, "song.db");
            IReadOnlyList<ChartOperationTarget> targets = CreateSelectedTargets();
            int workflowFailures = 0;
            int notificationFailures = 0;
            var owner = new FolderAutoRenameWorkflowOwner(
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
            Assert.IsTrue(owner.IsIdle);
            Assert.AreEqual(1, workflowFailures);
            Assert.IsTrue(notificationFailures > 0);
        }
        finally
        {
            DeleteRoot(root);
        }
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
        return new BMSLibrary(path, null, null, string.Empty);
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

    private sealed class AcceptedFolderDialogService : IUiDialogService
    {
        private readonly TaskCompletionSource<UiDialogResult> pendingConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal bool DeferConfirmation { get; set; }

        internal ManualResetEventSlim ConfirmationStarted { get; } = new(false);

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
            ConfirmationStarted.Set();
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
