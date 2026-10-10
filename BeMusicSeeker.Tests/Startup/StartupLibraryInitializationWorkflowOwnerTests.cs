using System;
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
    public async Task StartupReadiness_TracksRequiredCompletionIndependentlyFromInstallEstimation()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Task readiness = coordinator.WaitForRequiredPlaylistReadinessAsync();
        Assert.IsFalse(readiness.IsCompleted);
        coordinator.MarkRequiredPlaylistReady();
        await readiness;
        Assert.IsTrue(coordinator.IsRequiredPlaylistReady);
        Assert.IsFalse(coordinator.IsInstallEstimationReady);
    }

    [TestMethod]
    public async Task StartupReadiness_ShutdownTerminalizesWaitersDespiteCallbackFailure()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Task capturedRequiredReadiness = coordinator.RequiredPlaylistReadiness;
        Task capturedInstallEstimationReadiness = coordinator.InstallEstimationReadiness;
        Task readinessWaiter = coordinator.WaitForRequiredPlaylistReadinessAsync();
        var cancellationCallbackFailure = new InvalidOperationException("shutdown cancellation callback failed");
        bool callbackAfterFailureRan = false;
        using CancellationTokenRegistration callbackAfterFailure = coordinator.ShutdownToken.Register(() => callbackAfterFailureRan = true);
        using CancellationTokenRegistration failingCallback = coordinator.ShutdownToken.Register(() => throw cancellationCallbackFailure);
        AggregateException shutdownFailure = Assert.ThrowsException<AggregateException>(() => coordinator.RequestShutdown("test"));
        Assert.AreSame(cancellationCallbackFailure, shutdownFailure.Flatten().InnerExceptions[0]);
        Assert.IsTrue(callbackAfterFailureRan);
        Assert.IsTrue(capturedRequiredReadiness.IsCanceled);
        Assert.IsTrue(capturedInstallEstimationReadiness.IsCanceled);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => readinessWaiter);
    }

    [TestMethod]
    public async Task StartupReadiness_FailedInitializationPreservesOriginalFailure()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Task readinessWaiter = coordinator.WaitForRequiredPlaylistReadinessAsync();
        var failure = new InvalidOperationException("playlist initialization failed");
        coordinator.FailRequiredPlaylistReadiness(failure);
        Exception observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => readinessWaiter);
        Assert.AreSame(failure, observed);
        Assert.IsFalse(coordinator.IsRequiredPlaylistReady);
    }

    [TestMethod]
    public void InitializationService_RunInitialization_PropagatesContinuationFailure()
    {
        var service = new BmsLibraryInitializationService();
        using var semaphore = new SemaphoreSlim(1, 1);
        var failure = new InvalidOperationException("initialization continuation failed");

        AggregateException exception = Assert.ThrowsException<AggregateException>(() =>
            service.RunInitialization(
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
                loadSavedData: null,
                applyFilesAndProjection: null));

        Assert.AreSame(failure, exception.InnerException);
    }
}
