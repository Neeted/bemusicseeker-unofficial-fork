using System;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartFileOperationSynchronizerTests
{
    [TestMethod]
    public void TryEnter_IsFailFastNonReentrantAndLeaseIsIdempotent()
    {
        var synchronizer = new ChartFileOperationSynchronizer();

        Assert.IsTrue(synchronizer.TryEnter(out IDisposable first));
        Assert.IsFalse(synchronizer.TryEnter(out _));

        first.Dispose();
        first.Dispose();

        Assert.IsTrue(synchronizer.TryEnter(out IDisposable second));
        try
        {
            // A stale, repeated release must not clear the current lease.
            first.Dispose();
            Assert.IsFalse(synchronizer.TryEnter(out _));
        }
        finally
        {
            second.Dispose();
        }

        Assert.IsTrue(synchronizer.TryEnter(out IDisposable finalLease));
        finalLease.Dispose();
    }

    [TestMethod]
    public async Task TryEnter_LeaseCanBeDisposedByAnotherThread()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        IDisposable workerLease = await Task.Run(() =>
        {
            Assert.IsTrue(synchronizer.TryEnter(out IDisposable lease));
            return lease;
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(synchronizer.TryEnter(out _));
        workerLease.Dispose();

        Assert.IsTrue(synchronizer.TryEnter(out IDisposable nextLease));
        nextLease.Dispose();
    }
}
