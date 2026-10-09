using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistTablesReloadWorkflowOwnerTests
{
    [TestMethod]
    public async Task ReloadAsync_BorrowsSameCapabilityAndAwaitsBothRequiredTasks()
    {
        var events = new List<string>();
        PlaylistTablesReloadRequest? reload = null;
        PlaylistTablesExternalSyncRequest? sync = null;
        var reloadEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ChartFileOperationSynchronizer();
        Assert.IsTrue(gate.TryEnter(out IDisposable admission));
        using (admission)
        using (LibraryFileMutationCapability capability = gate.CreateMutationCapability(admission))
        {
            PlaylistTablesReloadWorkflowOwner owner = new(
                request => { reload = request; events.Add("reload"); reloadEntered.TrySetResult(true); return releaseReload.Task; },
                request => { sync = request; events.Add("sync"); syncEntered.TrySetResult(true); return releaseSync.Task; });
            Task<PlaylistTablesReloadWorkflowResult> operation = owner.ReloadAsync(new PlaylistTablesReloadRequest(17L, capability));
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(reloadEntered.Task, operation, "playlist-reload.reload-entry");
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsNull(sync);
                releaseReload.TrySetResult(true);
                await TestUiDispatcherHost.AwaitNotificationAsync(syncEntered.Task, operation, "playlist-reload.sync-entry");
                Assert.IsFalse(operation.IsCompleted, "The reload cannot finish before the actual external sync.");
                Assert.IsFalse(gate.TryEnter(out _));
                Assert.AreSame(capability, reload!.Capability);
                Assert.AreSame(capability, sync!.Capability);
                Assert.AreEqual(17L, sync.OperationToken);
                Assert.AreEqual("ReloadTables", sync.Reason);
                Assert.IsTrue(sync.FromReloadTables);
                Assert.IsTrue(sync.PublishReferenceReceipt);
                releaseSync.TrySetResult(true);
                PlaylistTablesReloadWorkflowResult result = await operation;
                Assert.AreSame(reload, result.ReloadRequest);
                Assert.AreSame(sync, result.ExternalSyncRequest);
                CollectionAssert.AreEqual(new[] { "reload", "sync" }, events);
            }
            finally
            {
                releaseReload.TrySetResult(true);
                releaseSync.TrySetResult(true);
                await operation;
            }
        }
    }

    [TestMethod]
    public async Task ReloadAsync_ReloadFailureDoesNotStartSyncAndPreservesException()
    {
        int syncCalls = 0;
        var failure = new InvalidOperationException("table reload failed");
        PlaylistTablesReloadWorkflowOwner owner = new(_ => Task.FromException(failure),
            _ => { syncCalls++; return Task.CompletedTask; });
        InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.ReloadAsync(new PlaylistTablesReloadRequest(23L)));
        Assert.AreSame(failure, actual);
        Assert.AreEqual(0, syncCalls);
    }

    [TestMethod]
    public async Task ReloadAsync_ActualSyncFailureIsPropagatedAfterSuccessfulReload()
    {
        int reloadCalls = 0;
        var failure = new InvalidOperationException("external sync failed");
        PlaylistTablesReloadWorkflowOwner owner = new(
            _ => { reloadCalls++; return Task.CompletedTask; }, _ => Task.FromException(failure));
        InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.ReloadAsync(new PlaylistTablesReloadRequest(29L)));
        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, reloadCalls);
    }

    [TestMethod]
    public async Task ReloadAsync_CancellationDoesNotStartExternalSync()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        int syncCalls = 0;
        PlaylistTablesReloadWorkflowOwner owner = new(_ => Task.FromCanceled(cancellation.Token),
            _ => { syncCalls++; return Task.CompletedTask; });
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => owner.ReloadAsync(new PlaylistTablesReloadRequest(31L)));
        Assert.AreEqual(0, syncCalls);
    }
}
