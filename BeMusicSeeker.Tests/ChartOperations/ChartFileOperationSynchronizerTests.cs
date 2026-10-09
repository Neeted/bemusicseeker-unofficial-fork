using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
        });

        Assert.IsFalse(synchronizer.TryEnter(out _));
        workerLease.Dispose();

        Assert.IsTrue(synchronizer.TryEnter(out IDisposable nextLease));
        nextLease.Dispose();
    }
    [TestMethod]
    public void MutationCapability_RejectsNullForeignAndReleasedAuthorityAndBorrowDoesNotReleaseOwner()
    {
        var owner = new ChartFileOperationSynchronizer();
        var foreign = new ChartFileOperationSynchronizer();
        Assert.IsTrue(owner.TryEnter(out IDisposable lease));
        using LibraryFileMutationCapability capability = owner.CreateMutationCapability(lease);
        try
        {
            Assert.ThrowsException<ArgumentNullException>(() => owner.Borrow(null));
            Assert.ThrowsException<InvalidOperationException>(() => foreign.Borrow(capability));
            using (owner.Borrow(capability)) { Assert.IsTrue(owner.IsActive); }
            Assert.IsTrue(owner.IsActive, "借用した内側scopeは外側受付を解放しません。");
            capability.Validate(owner);
        }
        finally { lease.Dispose(); }
        Assert.ThrowsException<InvalidOperationException>(() => owner.Borrow(capability));
        Assert.IsTrue(owner.TryEnter(out IDisposable next));
        try { lease.Dispose(); Assert.IsTrue(owner.IsActive); }
        finally { next.Dispose(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClosedAdmissionRejectsNewRequestsAndRetainsAcceptedContinuation(bool activeAtClose)
    {
        var owner = new ChartFileOperationSynchronizer();
        IDisposable? initial = null;
        Task<IDisposable>? continuation = null;
        try
        {
            if (activeAtClose) { Assert.IsTrue(owner.TryEnter(out initial)); }
            owner.CloseAdmission();
            owner.CloseAdmission();
            Assert.IsFalse(owner.TryEnter(out _), "Closeはidle/activeのどちらからも新規受付を閉じます。");
            if (initial != null)
            {
                using LibraryFileMutationCapability capability = owner.CreateMutationCapability(initial);
                using (owner.Borrow(capability)) { capability.Validate(owner); }
                Assert.IsTrue(owner.IsActive, "閉鎖は受理済み権限を失効させません。");
            }
            continuation = owner.EnterAcceptedBackgroundAsync();
            if (activeAtClose) { Assert.IsFalse(continuation.IsCompleted); }
            initial?.Dispose();
            using (IDisposable accepted = await continuation)
            {
                Assert.IsTrue(owner.IsActive);
                Assert.IsFalse(owner.WaitForIdleAsync().IsCompleted);
                Assert.IsFalse(owner.TryEnter(out _));
            }
            Assert.IsTrue(owner.WaitForIdleAsync().IsCompletedSuccessfully);
            Assert.IsFalse(owner.TryEnter(out _), "受理済み処理の終端でも受付を再開しません。");
        }
        finally
        {
            initial?.Dispose();
            if (continuation != null) { (await continuation).Dispose(); }
        }
    }

}
