using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FileDiffReloadWorkflowOwnerTests
{
    [TestMethod]
    public async Task ReloadAsync_SuccessPreservesRequestIdentityAcrossOrderedStages()
    {
        var events = new List<string>();
        var runtime = new RecordingRuntime(events);
        FileDiffReloadRequest reloadRequest = null;
        PlaylistReferenceApplyQueueRequest playlistRequest = null;
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                reloadRequest = request;
                events.Add("reload");
                return Task.CompletedTask;
            },
            CreateSyncOwner(runtime),
            request =>
            {
                playlistRequest = request;
                events.Add("playlist");
            });
        var request = new FileDiffReloadRequest("ReloadFileDiff", 41L);

        FileDiffReloadWorkflowResult result = await owner.ReloadAsync(request);

        CollectionAssert.AreEqual(new[] { "reload", "lr2", "playlist" }, events);
        Assert.AreSame(request, reloadRequest);
        Assert.AreSame(request, result.Request);
        Assert.AreSame(request, result.Lr2QueueResult.Request);
        Assert.AreEqual(Lr2SongDbSyncQueueStatus.Queued, result.Lr2QueueResult.Status);
        Assert.AreSame(playlistRequest, result.PlaylistReferenceQueueRequest);
        Assert.AreEqual(request.Reason, playlistRequest.Reason);
        Assert.AreEqual(request.OperationToken, playlistRequest.OperationToken);
        Assert.AreEqual(request.Reason, runtime.LastQueueReason);
        Assert.IsTrue(runtime.LastAllowCommittedPathReceipt);
    }

    [TestMethod]
    public async Task ReloadAsync_Lr2UnavailableReturnsTypedSkipAndContinuesPlaylistQueue()
    {
        var events = new List<string>();
        var runtime = new RecordingRuntime(events)
        {
            IsLr2ModeEnabledValue = false
        };
        PlaylistReferenceApplyQueueRequest playlistRequest = null;
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                events.Add("reload");
                return Task.CompletedTask;
            },
            CreateSyncOwner(runtime),
            request =>
            {
                playlistRequest = request;
                events.Add("playlist");
            });
        var request = new FileDiffReloadRequest("ReloadFileDiff", 42L);

        FileDiffReloadWorkflowResult result = await owner.ReloadAsync(request);

        CollectionAssert.AreEqual(new[] { "reload", "playlist" }, events);
        Assert.AreEqual(0, runtime.QueueCount);
        Assert.IsTrue(result.Lr2QueueResult.WasSkippedUnavailable);
        Assert.AreSame(request, result.Lr2QueueResult.Request);
        Assert.AreSame(playlistRequest, result.PlaylistReferenceQueueRequest);
        Assert.AreEqual(42L, playlistRequest.OperationToken);
    }

    [TestMethod]
    public async Task ReloadAsync_ReloadFailureStopsLaterStagesAndPreservesException()
    {
        var failure = new IOException("file diff reload failed");
        var events = new List<string>();
        var runtime = new RecordingRuntime(events);
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                events.Add("reload");
                return Task.FromException(failure);
            },
            CreateSyncOwner(runtime),
            _ => events.Add("playlist"));

        IOException thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => owner.ReloadAsync(new FileDiffReloadRequest("ReloadFileDiff", 43L)));

        Assert.AreSame(failure, thrown);
        CollectionAssert.AreEqual(new[] { "reload" }, events);
    }

    [TestMethod]
    public async Task ReloadAsync_Lr2FailureStopsPlaylistAndPreservesException()
    {
        var failure = new InvalidOperationException("LR2 queue failed");
        var events = new List<string>();
        var runtime = new RecordingRuntime(events) { QueueFailure = failure };
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                events.Add("reload");
                return Task.CompletedTask;
            },
            CreateSyncOwner(runtime),
            _ => events.Add("playlist"));

        InvalidOperationException thrown =
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.ReloadAsync(new FileDiffReloadRequest("ReloadFileDiff", 44L)));

        Assert.AreSame(failure, thrown);
        CollectionAssert.AreEqual(new[] { "reload", "lr2" }, events);
    }

    [TestMethod]
    public async Task ReloadAsync_PlaylistFailureFollowsPriorStagesAndPreservesException()
    {
        var failure = new InvalidOperationException("playlist reference queue failed");
        var events = new List<string>();
        var runtime = new RecordingRuntime(events);
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                events.Add("reload");
                return Task.CompletedTask;
            },
            CreateSyncOwner(runtime),
            _ =>
            {
                events.Add("playlist");
                throw failure;
            });

        InvalidOperationException thrown =
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.ReloadAsync(new FileDiffReloadRequest("ReloadFileDiff", 45L)));

        Assert.AreSame(failure, thrown);
        CollectionAssert.AreEqual(new[] { "reload", "lr2", "playlist" }, events);
    }

    [TestMethod]
    public async Task ReloadAsync_CancellationStopsLaterStagesAndPreservesException()
    {
        var cancellation = new OperationCanceledException("file diff reload cancelled");
        var events = new List<string>();
        var runtime = new RecordingRuntime(events);
        var owner = new FileDiffReloadWorkflowOwner(
            async request =>
            {
                events.Add("reload");
                await Task.Yield();
                throw cancellation;
            },
            CreateSyncOwner(runtime),
            _ => events.Add("playlist"));

        OperationCanceledException thrown =
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => owner.ReloadAsync(new FileDiffReloadRequest("ReloadFileDiff", 46L)));

        Assert.AreSame(cancellation, thrown);
        CollectionAssert.AreEqual(new[] { "reload" }, events);
    }

    [TestMethod]
    public async Task MainWindowConsumer_UsesTypedOwnerWithoutSeparateWorkspaceQueue()
    {
        var events = new List<string>();
        var runtime = new RecordingRuntime(events);
        var reloadRequests = new List<FileDiffReloadRequest>();
        var playlistRequests = new List<PlaylistReferenceApplyQueueRequest>();
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                reloadRequests.Add(request);
                events.Add("reload");
                return Task.CompletedTask;
            },
            CreateSyncOwner(runtime),
            request =>
            {
                playlistRequests.Add(request);
                events.Add("playlist");
            });
        MainWindowViewModel viewModel = CreateMainWindowViewModel(owner);
        int unexpectedWorkspaceReferenceQueues = 0;
        int unexpectedExternalSyncQueues = 0;
        int unexpectedPlaylistPresentationRequests = 0;
        viewModel.PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queued +=
            (_, _) => unexpectedWorkspaceReferenceQueues++;
        viewModel.PlaylistWorkspace.PlaylistExternalSyncQueued +=
            (_, _) => unexpectedExternalSyncQueues++;
        viewModel.PlaylistWorkspace.PlaylistPresentationRefreshRequested +=
            (_, _) => unexpectedPlaylistPresentationRequests++;

        try
        {
            await viewModel.ReloadFileDiffAsync();

            CollectionAssert.AreEqual(new[] { "reload", "lr2", "playlist" }, events);
            Assert.AreEqual(1, reloadRequests.Count);
            Assert.AreEqual(1, playlistRequests.Count);
            Assert.AreEqual("ReloadFileDiff", reloadRequests[0].Reason);
            Assert.IsTrue(reloadRequests[0].OperationToken > 0L);
            Assert.AreEqual(reloadRequests[0].Reason, playlistRequests[0].Reason);
            Assert.AreEqual(reloadRequests[0].OperationToken, playlistRequests[0].OperationToken);
            Assert.AreEqual(0, unexpectedWorkspaceReferenceQueues);
            Assert.AreEqual(0, unexpectedExternalSyncQueues);
            Assert.AreEqual(0, unexpectedPlaylistPresentationRequests);
            Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsOperationActive);
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public async Task MainWindowConsumer_SharedGateSerializesCallsAndPublishesDistinctTokens()
    {
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<FileDiffReloadRequest>();
        int reloadCount = 0;
        var runtime = new RecordingRuntime(new List<string>());
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                requests.Add(request);
                reloadCount++;
                if (reloadCount == 1)
                {
                    firstEntered.SetResult();
                    return releaseFirst.Task;
                }

                secondEntered.SetResult();
                return Task.CompletedTask;
            },
            CreateSyncOwner(runtime),
            _ => { });
        MainWindowViewModel viewModel = CreateMainWindowViewModel(owner);

        try
        {
            Task first = viewModel.ReloadFileDiffAsync();
            await firstEntered.Task;
            Task second = viewModel.ReloadFileDiffAsync();

            Assert.AreEqual(1, reloadCount);
            Assert.IsFalse(second.IsCompleted);

            releaseFirst.SetResult();
            await first;
            await secondEntered.Task;
            await second;

            Assert.AreEqual(2, requests.Count);
            Assert.AreEqual("ReloadFileDiff", requests[0].Reason);
            Assert.AreEqual("ReloadFileDiff", requests[1].Reason);
            Assert.IsTrue(requests[0].OperationToken > 0L);
            Assert.IsTrue(requests[1].OperationToken > requests[0].OperationToken);
        }
        finally
        {
            releaseFirst.TrySetResult();
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public async Task MainWindowConsumer_FailureIsRetryableAndGateAllowsNextToken()
    {
        var failure = new IOException("file diff reload failed");
        var requests = new List<FileDiffReloadRequest>();
        int reloadCount = 0;
        var runtime = new RecordingRuntime(new List<string>());
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                requests.Add(request);
                reloadCount++;
                return reloadCount == 1
                    ? Task.FromException(failure)
                    : Task.CompletedTask;
            },
            CreateSyncOwner(runtime),
            _ => { });
        MainWindowViewModel viewModel = CreateMainWindowViewModel(owner);

        try
        {
            IOException thrown = await Assert.ThrowsExceptionAsync<IOException>(
                () => viewModel.ReloadFileDiffAsync());

            Assert.AreSame(failure, thrown);
            Assert.AreEqual(1, reloadCount);
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsFailed);
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsRetryableFailure);
            Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsFalse(viewModel.IsLibraryOperationInProgress);

            Task next = viewModel.ReloadFileDiffAsync();
            Assert.AreEqual(2, reloadCount);
            await next;

            Assert.AreEqual(2, requests.Count);
            Assert.IsTrue(requests[1].OperationToken > requests[0].OperationToken);
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public async Task MainWindowConsumer_PlaylistFailureUsesRootFailureCleanup()
    {
        var failure = new InvalidOperationException("playlist reference queue failed");
        var events = new List<string>();
        var runtime = new RecordingRuntime(events);
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                events.Add("reload");
                return Task.CompletedTask;
            },
            CreateSyncOwner(runtime),
            _ =>
            {
                events.Add("playlist");
                throw failure;
            });
        MainWindowViewModel viewModel = CreateMainWindowViewModel(owner);

        try
        {
            InvalidOperationException thrown =
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => viewModel.ReloadFileDiffAsync());

            Assert.AreSame(failure, thrown);
            CollectionAssert.AreEqual(new[] { "reload", "lr2", "playlist" }, events);
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsFailed);
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsRetryableFailure);
            Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsFalse(viewModel.IsLibraryOperationInProgress);
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public void Constructor_RejectsMissingStageDependencies()
    {
        var runtime = new RecordingRuntime(new List<string>());
        Lr2SongDbSyncWorkflowOwner syncOwner = CreateSyncOwner(runtime);

        Assert.ThrowsException<ArgumentNullException>(
            () => new FileDiffReloadWorkflowOwner(null, syncOwner, _ => { }));
        Assert.ThrowsException<ArgumentNullException>(
            () => new FileDiffReloadWorkflowOwner(_ => Task.CompletedTask, null, _ => { }));
        Assert.ThrowsException<ArgumentNullException>(
            () => new FileDiffReloadWorkflowOwner(_ => Task.CompletedTask, syncOwner, null));
    }

    private static MainWindowViewModel CreateMainWindowViewModel(
        FileDiffReloadWorkflowOwner owner)
    {
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(new Settings()),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        return new MainWindowViewModel(
            composition,
            composition,
            owner,
            new InitializedStatePort());
    }

    private static Lr2SongDbSyncWorkflowOwner CreateSyncOwner(
        RecordingRuntime runtime)
    {
        return new Lr2SongDbSyncWorkflowOwner(
            runtime,
            backgroundScheduler: action =>
            {
                action();
                return Task.CompletedTask;
            },
            taskLogger: (_, _) => { });
    }

    private sealed class RecordingRuntime : ILr2SongDbSyncWorkflowRuntime
    {
        private readonly IList<string> events;

        internal RecordingRuntime(IList<string> events)
        {
            this.events = events;
        }

        internal bool IsLr2ModeEnabledValue { get; set; } = true;

        internal bool IsLibraryAvailableValue { get; set; } = true;

        internal Exception QueueFailure { get; set; }

        internal int QueueCount { get; private set; }

        internal string LastQueueReason { get; private set; }

        internal bool LastAllowCommittedPathReceipt { get; private set; }

        public bool IsLr2ModeEnabled => IsLr2ModeEnabledValue;

        public bool IsLibraryAvailable => IsLibraryAvailableValue;

        public void DiscardCommittedPathReceipt(string reason)
        {
        }

        public void Queue(
            string reason,
            bool force,
            bool prepareGeneratedData = false,
            bool allowIncompleteToQueue = true,
            bool allowCommittedPathReceipt = false)
        {
            QueueCount++;
            LastQueueReason = reason;
            LastAllowCommittedPathReceipt = allowCommittedPathReceipt;
            events.Add("lr2");
            if (QueueFailure != null)
            {
                throw QueueFailure;
            }
        }

        public bool TryRunDataPreparation(
            string reason,
            bool includeBuiltinGeneratedData = false,
            Action queueAfterPreparation = null)
        {
            queueAfterPreparation?.Invoke();
            return true;
        }

        public void SyncExternalFolderRowsForCustomFolderOutputBaseChange(string reason)
        {
        }

    }

    private sealed class InitializedStatePort : IMainWindowInitializationStatePort
    {
        public bool IsInitializationCompleted => true;
    }
}
