using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FileDiffReloadWorkflowOwnerTests
{
    [TestMethod]
    public async Task ReloadAsync_SuccessPreservesRequestIdentityAcrossOrderedStages()
    {
        var events = new List<string>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime(events) { QueueTask = terminal.Task };
        var admission = new ChartFileOperationSynchronizer();
        Assert.IsTrue(admission.TryEnter(out IDisposable lease));
        using IDisposable acceptedLease = lease;
        using LibraryFileMutationCapability capability = admission.CreateMutationCapability(lease);
        FileDiffReloadRequest? reloadRequest = null;
        PlaylistReferenceApplyQueueRequest? playlistRequest = null;
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                reloadRequest = request;
                events.Add("reload");
                return Task.FromResult(new LibraryFileInitializationResult());
            },
            CreateSyncOwner(runtime),
            request =>
            {
                playlistRequest = request;
                events.Add("playlist");
                return Task.CompletedTask;
            });
        var progressRequest = new OperationProgressRequest(9, 41L, "reload_file_diff", 1);
        var request = new FileDiffReloadRequest("ReloadFileDiff", 41L, capability, progressRequest);

        Task<FileDiffReloadWorkflowResult> operation = owner.ReloadAsync(request);
        FileDiffReloadWorkflowResult result;
        try
        {
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsNull(playlistRequest);
            Assert.AreSame(capability, runtime.LastCapability);
            Assert.AreSame(progressRequest, runtime.LastProgressRequest);
            capability.Validate(admission);
            terminal.TrySetResult();
            result = await operation;
        }
        finally { terminal.TrySetResult(); await operation; }

        CollectionAssert.AreEqual(new[] { "reload", "lr2", "playlist" }, events);
        Assert.AreSame(request, reloadRequest);
        Assert.AreSame(request, result.Request);
        Assert.AreSame(request, result.Lr2QueueResult.Request);
        Assert.AreEqual(Lr2SongDbSyncQueueStatus.Queued, result.Lr2QueueResult.Status);
        Assert.AreSame(playlistRequest, result.PlaylistReferenceQueueRequest);
        PlaylistReferenceApplyQueueRequest completedPlaylistRequest = playlistRequest!;
        Assert.AreEqual(request.Reason, completedPlaylistRequest.Reason);
        Assert.AreEqual(request.OperationToken, completedPlaylistRequest.OperationToken);
        Assert.AreEqual(request.Reason, runtime.LastQueueReason);
        Assert.IsNotNull(runtime.LastInitializationResult);
    }

    [TestMethod]
    public async Task ReloadAsync_Lr2UnavailableReturnsTypedSkipAndContinuesPlaylistQueue()
    {
        var events = new List<string>();
        var runtime = new RecordingRuntime(events)
        {
            IsLr2ModeEnabledValue = false
        };
        PlaylistReferenceApplyQueueRequest? playlistRequest = null;
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                events.Add("reload");
                return Task.FromResult(new LibraryFileInitializationResult());
            },
            CreateSyncOwner(runtime),
            request =>
            {
                playlistRequest = request;
                events.Add("playlist");
                return Task.CompletedTask;
            });
        var request = new FileDiffReloadRequest("ReloadFileDiff", 42L);

        FileDiffReloadWorkflowResult result = await owner.ReloadAsync(request);

        CollectionAssert.AreEqual(new[] { "reload", "playlist" }, events);
        Assert.AreEqual(0, runtime.QueueCount);
        Assert.IsTrue(result.Lr2QueueResult.WasSkippedUnavailable);
        Assert.AreSame(request, result.Lr2QueueResult.Request);
        Assert.AreSame(playlistRequest, result.PlaylistReferenceQueueRequest);
        PlaylistReferenceApplyQueueRequest completedPlaylistRequest = playlistRequest!;
        Assert.AreEqual(42L, completedPlaylistRequest.OperationToken);
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
                return Task.FromException<LibraryFileInitializationResult>(failure);
            },
            CreateSyncOwner(runtime),
            _ => { events.Add("playlist"); return Task.CompletedTask; });

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
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime(events) { QueueTask = terminal.Task };
        var admission = new ChartFileOperationSynchronizer();
        Assert.IsTrue(admission.TryEnter(out IDisposable lease));
        using IDisposable acceptedLease = lease;
        using LibraryFileMutationCapability capability = admission.CreateMutationCapability(lease);
        var owner = new FileDiffReloadWorkflowOwner(
            request =>
            {
                events.Add("reload");
                return Task.FromResult(new LibraryFileInitializationResult());
            },
            CreateSyncOwner(runtime),
            _ => { events.Add("playlist"); return Task.CompletedTask; });

        Task<FileDiffReloadWorkflowResult> operation = owner.ReloadAsync(new FileDiffReloadRequest("ReloadFileDiff", 44L, capability));
        try
        {
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreSame(capability, runtime.LastCapability);
            capability.Validate(admission);
            terminal.SetException(failure);
            InvalidOperationException thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => operation);
            Assert.AreSame(failure, thrown);
            CollectionAssert.AreEqual(new[] { "reload", "lr2" }, events);
        }
        finally
        {
            terminal.TrySetResult();
            try { await operation; } catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
        }
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
                return Task.FromResult(new LibraryFileInitializationResult());
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
            _ => { events.Add("playlist"); return Task.CompletedTask; });

        OperationCanceledException thrown =
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => owner.ReloadAsync(new FileDiffReloadRequest("ReloadFileDiff", 46L)));

        Assert.AreSame(cancellation, thrown);
        CollectionAssert.AreEqual(new[] { "reload" }, events);
    }

    [TestMethod]
    public void Constructor_RejectsMissingStageDependencies()
    {
        var runtime = new RecordingRuntime(new List<string>());
        Lr2SongDbSyncWorkflowOwner syncOwner = CreateSyncOwner(runtime);

        Assert.ThrowsException<ArgumentNullException>(
            () => new FileDiffReloadWorkflowOwner(null, syncOwner, _ => Task.CompletedTask));
        Assert.ThrowsException<ArgumentNullException>(
            () => new FileDiffReloadWorkflowOwner(_ => Task.FromResult(new LibraryFileInitializationResult()), null, _ => Task.CompletedTask));
        Assert.ThrowsException<ArgumentNullException>(
            () => new FileDiffReloadWorkflowOwner(_ => Task.FromResult(new LibraryFileInitializationResult()), syncOwner, null));
    }

    private static async Task<LibraryFileInitializationResult> CompleteAfterGateAsync(Task gate)
    {
        await gate;
        return new();
    }

    private static Lr2SongDbSyncWorkflowOwner CreateSyncOwner(
        RecordingRuntime runtime)
    {
        return new Lr2SongDbSyncWorkflowOwner(runtime);
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

        internal Task QueueTask { get; set; } = Task.CompletedTask;

        internal LibraryFileMutationCapability? LastCapability { get; private set; }

        internal OperationProgressRequest? LastProgressRequest { get; private set; }

        internal Exception? QueueFailure { get; set; }

        internal int QueueCount { get; private set; }

        internal string? LastQueueReason { get; private set; }

        internal LibraryFileInitializationResult? LastInitializationResult { get; private set; }

        public bool IsLr2ModeEnabled => IsLr2ModeEnabledValue;

        public bool IsLibraryAvailable => IsLibraryAvailableValue;

        public async Task<bool> QueueAsync(
            string reason,
            bool force,
            bool prepareGeneratedData = false,
            bool allowIncompleteToQueue = true,
            LibraryFileInitializationResult? initializationResult = null, LibraryFileMutationCapability? capability = null, bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null, Lr2SongDbSyncPreparedDataSurface? preparedSurface = null, OperationProgressRequest? originatingRequest = null, BmsLibraryOptionsSnapshot? optionsSnapshot = null)
        {
            QueueCount++;
            LastCapability = capability;
            LastProgressRequest = originatingRequest;
            LastQueueReason = reason;
            LastInitializationResult = initializationResult;
            events.Add("lr2");
            if (QueueFailure != null)
            {
                throw QueueFailure;
            }
            await QueueTask.ConfigureAwait(false);
            return true;
        }



        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason, LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
        {
            return Task.CompletedTask;
        }

    }

    private sealed class InitializedStatePort : IMainWindowInitializationStatePort
    {
        public bool IsInitializationCompleted => true;
    }
}
