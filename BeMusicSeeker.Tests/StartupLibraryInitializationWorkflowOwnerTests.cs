using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupLibraryInitializationWorkflowOwnerTests
{
    [TestMethod]
    public async Task GateLease_ReleasesExactlyOnceAndAllowsImmediateNextAcquire()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        StartupLibraryInitializationGateLease first = await owner.AcquireGateAsync();
        Task<StartupLibraryInitializationGateLease> secondAcquire = owner.AcquireGateAsync();

        Assert.AreEqual(0, gate.CurrentCount);
        Assert.IsFalse(secondAcquire.IsCompleted);

        first.Dispose();
        first.Dispose();

        using StartupLibraryInitializationGateLease second =
            await secondAcquire;
        Assert.AreEqual(0, gate.CurrentCount);
        second.Dispose();
        Assert.AreEqual(1, gate.CurrentCount);
    }

    [TestMethod]
    public async Task InitializeAsync_SuccessInvokesInitializationExactlyOnce()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        int initializeCalls = 0;

        using (await owner.AcquireGateAsync())
        {
            await owner.InitializeAsync(() => Interlocked.Increment(ref initializeCalls));
            Assert.AreEqual(1, initializeCalls);
            Assert.AreEqual(0, gate.CurrentCount);
        }

        Assert.AreEqual(1, gate.CurrentCount);
    }

    [TestMethod]
    public async Task InitializeAsync_FailurePreservesExceptionAndLeaseStillReleases()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        var failure = new InvalidOperationException("startup library initialization failed");

        InvalidOperationException exception;
        using (await owner.AcquireGateAsync())
        {
            exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.InitializeAsync(() => throw failure));
            Assert.AreEqual(0, gate.CurrentCount);
        }

        Assert.AreSame(failure, exception);
        Assert.AreEqual(1, gate.CurrentCount);
        using StartupLibraryInitializationGateLease next =
            await owner.AcquireGateAsync();
    }

    [TestMethod]
    public async Task InitializeAsync_RejectsNullOperationWithoutInvokingWork()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => owner.InitializeAsync(null));
    }

    [TestMethod]
    public async Task StartupReadiness_AdmitsImportBeforeReadinessAndRunsItAfterward()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Uri uri = new("https://example.test/table.json");
        Assert.IsTrue(coordinator.TryAdmitExternalPlaylistImports([uri], out bool shouldStartDrain));
        Assert.IsTrue(shouldStartDrain);
        Task readinessWait = coordinator.WaitForRequiredPlaylistReadinessAsync();
        Assert.IsFalse(readinessWait.IsCompleted, "The import consumer must await the readiness task.");

        Task<IReadOnlyList<Uri>> consumer = Task.Run(async () =>
        {
            Assert.IsTrue(coordinator.TryBeginExternalPlaylistImportDrain());
            await readinessWait;
            return coordinator.DequeueExternalPlaylistImportBatch();
        });
        Assert.IsFalse(consumer.IsCompleted, "The import consumer must await the readiness task.");

        coordinator.MarkRequiredPlaylistReady();
        IReadOnlyList<Uri> admittedUris = await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, admittedUris.Count);
        Assert.AreEqual(uri, admittedUris[0]);
        Assert.IsTrue(coordinator.IsRequiredPlaylistReady);
        Assert.IsFalse(coordinator.IsInstallEstimationReady);
    }

    [TestMethod]
    public async Task StartupReadiness_ShutdownTerminalizesWaiterAndImportQueue()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Task readinessWaiter = coordinator.WaitForRequiredPlaylistReadinessAsync();
        Assert.IsTrue(coordinator.TryAdmitExternalPlaylistImports(
            [new Uri("https://example.test/table.json")],
            out _));
        var consumerEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lateMutationCount = 0;
        Task consumer = Task.Run(async () =>
        {
            Assert.IsTrue(coordinator.TryBeginExternalPlaylistImportDrain());
            consumerEntered.TrySetResult(true);
            try
            {
                await coordinator.WaitForRequiredPlaylistReadinessAsync();
                Interlocked.Increment(ref lateMutationCount);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                coordinator.CompleteExternalPlaylistImportDrain();
            }
        });
        await consumerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.RequestShutdown("test");

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => readinessWaiter.WaitAsync(TimeSpan.FromSeconds(5)));
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, lateMutationCount);
        Assert.IsTrue(coordinator.ExternalImportDrainCompletion.IsCompleted);
        Assert.AreEqual(0, coordinator.DequeueExternalPlaylistImportBatch().Count);
        Assert.IsFalse(coordinator.TryAdmitExternalPlaylistImports(
            [new Uri("https://example.test/later.json")],
            out _));
    }

    [TestMethod]
    public async Task StartupReadiness_FailedInitializationTerminalizesPendingImportDrain()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Assert.IsTrue(coordinator.TryAdmitExternalPlaylistImports(
            [new Uri("https://example.test/table.json")],
            out bool shouldStartDrain));
        Assert.IsTrue(shouldStartDrain);
        Assert.IsTrue(coordinator.TryBeginExternalPlaylistImportDrain());
        Task readinessWaiter = coordinator.WaitForRequiredPlaylistReadinessAsync();
        var failure = new InvalidOperationException("playlist initialization failed");

        coordinator.FailRequiredPlaylistReadiness(failure);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => readinessWaiter.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(coordinator.CompleteExternalPlaylistImportDrain());
        Assert.AreEqual(0, coordinator.DequeueExternalPlaylistImportBatch().Count);
        Assert.IsFalse(coordinator.TryAdmitExternalPlaylistImports(
            [new Uri("https://example.test/later.json")],
            out _));
    }

    [TestMethod]
    public void InitializationService_RunInitialize_PropagatesContinuationFailure()
    {
        var service = new BmsLibraryInitializationService();
        using var semaphore = new SemaphoreSlim(1, 1);
        var failure = new InvalidOperationException("initialization continuation failed");

        AggregateException exception = Assert.ThrowsException<AggregateException>(() =>
            service.RunInitialize(
                [
                    () =>
                    {
                        try
                        {
                            throw failure;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }
                ],
                semaphore,
                phase1: null,
                phase2: null,
                phase3: null));

        Assert.AreSame(failure, exception.InnerException);
    }
}
