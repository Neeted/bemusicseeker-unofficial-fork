using System;
using System.Threading;
using System.Threading.Tasks;
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
            await secondAcquire.WaitAsync(TimeSpan.FromSeconds(5));
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
            await owner.AcquireGateAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task InitializeAsync_RejectsNullOperationWithoutInvokingWork()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => owner.InitializeAsync(null));
    }
}
