using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistTablesReloadWorkflowOwnerTests
{
    [TestMethod]
    public async Task ReloadAsync_QueuesExactSyncOnlyAfterReloadAndUsesFalseBmtExportFlag()
    {
        var events = new List<string>();
        PlaylistTablesReloadRequest? capturedReloadRequest = null;
        PlaylistTablesExternalSyncRequest? capturedSyncRequest = null;
        var reloadEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaylistTablesReloadWorkflowOwner owner = new(
            request =>
            {
                events.Add("reload");
                capturedReloadRequest = request;
                reloadEntered.TrySetResult(true);
                return releaseReload.Task;
            },
            request =>
            {
                events.Add("queue");
                capturedSyncRequest = request;
            });

        Task<PlaylistTablesReloadWorkflowResult> operation = owner.ReloadAsync(
            new PlaylistTablesReloadRequest(17L));
        await reloadEntered.Task;

        Assert.IsFalse(operation.IsCompleted);
        Assert.IsNull(capturedSyncRequest);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("reload", events[0]);

        releaseReload.SetResult(true);
        PlaylistTablesReloadWorkflowResult result = await operation;

        Assert.IsNotNull(capturedReloadRequest);
        Assert.AreEqual(17L, capturedReloadRequest!.OperationToken);
        Assert.IsFalse(capturedReloadRequest!.QueueBeatorajaBmtExportAfterHydration);
        Assert.IsNotNull(capturedSyncRequest);
        Assert.AreEqual("ReloadTables", capturedSyncRequest!.Reason);
        Assert.IsTrue(capturedSyncRequest!.FromReloadTables);
        Assert.IsTrue(capturedSyncRequest!.PublishReferenceReceipt);
        Assert.AreEqual(17L, capturedSyncRequest!.OperationToken);
        Assert.AreSame(capturedReloadRequest, result.ReloadRequest);
        Assert.AreSame(capturedSyncRequest, result.ExternalSyncRequest);
        CollectionAssert.AreEqual(new[] { "reload", "queue" }, events);
    }

    [TestMethod]
    public async Task ReloadAsync_ReloadFailureDoesNotQueueAndPreservesException()
    {
        var queueCalls = 0;
        var failure = new InvalidOperationException("table reload failed");
        PlaylistTablesReloadWorkflowOwner owner = new(
            _ => Task.FromException(failure),
            _ => queueCalls++);

        InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.ReloadAsync(new PlaylistTablesReloadRequest(23L)));

        Assert.AreSame(failure, actual);
        Assert.AreEqual(0, queueCalls);
    }

    [TestMethod]
    public async Task ReloadAsync_QueueFailureIsPropagatedAfterSuccessfulReload()
    {
        var reloadCalls = 0;
        var failure = new InvalidOperationException("external sync queue failed");
        PlaylistTablesReloadWorkflowOwner owner = new(
            _ =>
            {
                reloadCalls++;
                return Task.CompletedTask;
            },
            _ => throw failure);

        InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.ReloadAsync(new PlaylistTablesReloadRequest(29L)));

        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, reloadCalls);
    }

    [TestMethod]
    public async Task ReloadAsync_CancellationDoesNotQueueExternalSync()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        var queueCalls = 0;
        PlaylistTablesReloadWorkflowOwner owner = new(
            _ => Task.FromCanceled(cancellation.Token),
            _ => queueCalls++);

        try
        {
            await owner.ReloadAsync(new PlaylistTablesReloadRequest(31L));
            Assert.Fail("A canceled table reload must propagate cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.AreEqual(0, queueCalls);
    }
}
