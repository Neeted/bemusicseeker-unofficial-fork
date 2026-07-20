using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
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
            ChartFolderAutoRenameRequest request = CreateSelectedRequest();
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
                action => action());
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

            Assert.IsTrue(owner.StartSelected(request));
            Assert.IsTrue(completion.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            CollectionAssert.AreEqual(
                new[] { "initial", "progress", "terminal", "completion", "terminal-published" },
                events.ToArray());
            Assert.IsNotNull(receipt);
            Assert.AreSame(request, observedRequest);
            Assert.IsFalse(receipt.AllFolders);
            Assert.IsTrue(receipt.RefreshRequired);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void AllRequestWithoutActionableTargets_DoesNotStartProgressOrMutation()
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
                action => Task.Run(action),
                action => action());
            owner.AttachLibrary(library);
            owner.ProgressChanged += _ => Interlocked.Increment(ref progressCalls);

            Assert.IsTrue(owner.StartAll("C:\\Library"));
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, executorCalls);
            Assert.AreEqual(0, progressCalls);
            Assert.AreNotEqual(callerThreadId, checkerThreadId);
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
            ChartFolderAutoRenameRequest request = CreateSelectedRequest();
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
                });
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => Interlocked.Increment(ref completionCount);

            Assert.IsTrue(owner.StartSelected(request));
            Assert.IsTrue(owner.IsActive);
            Assert.IsFalse(owner.StartSelected(request));

            DrainNotifications(notifications);

            Assert.IsTrue(owner.IsIdle);
            Assert.AreEqual(1, completionCount);
            Assert.IsTrue(owner.StartSelected(request));
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
            ChartFolderAutoRenameRequest request = CreateSelectedRequest();
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
                action => action());
            owner.AttachLibrary(library);
            owner.CompletionPublished += _ => completed.Set();

            Assert.IsTrue(owner.StartSelected(request));
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(owner.StartSelected(request));
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
            ChartFolderAutoRenameRequest request = CreateSelectedRequest();
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
                action => action());
            owner.AttachLibrary(first);
            owner.CompletionPublished += _ =>
            {
                Interlocked.Increment(ref completionCount);
                completion.Set();
            };

            Assert.IsTrue(owner.StartSelected(request));
            Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(5)));
            owner.AttachLibrary(second);
            releaseFirst.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, completionCount);

            Assert.IsTrue(owner.StartSelected(request));
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
            ChartFolderAutoRenameRequest request = CreateSelectedRequest();
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
                });
            owner.ProgressChanged += progress =>
            {
                if (progress.IsCompleted)
                {
                    Interlocked.Increment(ref resetCount);
                }
            };
            owner.AttachLibrary(first);

            Assert.IsTrue(owner.StartSelected(request));
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
            ChartFolderAutoRenameRequest request = CreateSelectedRequest();
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
                action => action());
            owner.AttachLibrary(library);

            Assert.IsTrue(owner.StartSelected(request));
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            owner.RequestShutdown();
            Assert.IsFalse(owner.StartSelected(request));
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
            ChartFolderAutoRenameRequest request = CreateSelectedRequest();
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
                reportWorkflowFailure: exception => observed = exception);
            owner.AttachLibrary(library);
            owner.FailurePublished += _ => failure.Set();

            Assert.IsTrue(owner.StartSelected(request));
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
            ChartFolderAutoRenameRequest request = CreateSelectedRequest();
            int workflowFailures = 0;
            int notificationFailures = 0;
            var owner = new FolderAutoRenameWorkflowOwner(
                (current, selectedRequest, progress) => throw new InvalidOperationException("selected route was not expected"),
                (current, parentDirectory, progress) => throw new InvalidOperationException("all route was not expected"),
                (current, parentDirectory) => false,
                action => throw new InvalidOperationException("scheduler failed"),
                action => throw new InvalidOperationException("dispatcher failed"),
                reportNotificationFailure: _ => Interlocked.Increment(ref notificationFailures),
                reportWorkflowFailure: _ => Interlocked.Increment(ref workflowFailures));
            owner.AttachLibrary(library);

            Assert.IsTrue(owner.StartSelected(request));
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

    private static ChartFolderAutoRenameRequest CreateSelectedRequest()
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
        Assert.IsTrue(ChartFolderAutoRenameRequest.TryCreate([target], out ChartFolderAutoRenameRequest request));
        return request;
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

    private sealed class TestableBmsFile : BMSFile
    {
        internal TestableBmsFile(string hash)
        {
            this.hash = hash;
        }
    }
}
