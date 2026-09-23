using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartMutationActivityOwnerTests
{
    [TestMethod]
    public void EnterAndRelease_PublishesOnlyOuterTransitions()
    {
        var owner = new ChartMutationActivityOwner();
        var transitions = new List<bool>();
        owner.ActivityChanged += (_, _) => transitions.Add(owner.IsActive);

        IDisposable outer = owner.Enter();
        IDisposable inner = owner.Enter();
        Assert.IsTrue(owner.IsActive);

        inner.Dispose();
        Assert.IsTrue(owner.IsActive);
        outer.Dispose();
        Assert.IsFalse(owner.IsActive);
        outer.Dispose();

        CollectionAssert.AreEqual(new[] { true, false }, transitions);
    }

    [TestMethod]
    public void ConcurrentLeases_ConvergeToInactiveWithoutUnderflow()
    {
        var owner = new ChartMutationActivityOwner();
        int notifications = 0;
        owner.ActivityChanged += (_, _) => Interlocked.Increment(ref notifications);
        using var start = new ManualResetEventSlim();
        using var entered = new CountdownEvent(8);
        using var release = new ManualResetEventSlim();
        var leases = new IDisposable[8];
        var workers = new Thread[8];

        for (int index = 0; index < workers.Length; index++)
        {
            int workerIndex = index;
            workers[index] = new Thread(() =>
            {
                start.Wait();
                leases[workerIndex] = owner.Enter();
                entered.Signal();
                release.Wait();
            });
            workers[index].Start();
        }

        start.Set();
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        release.Set();
        foreach (Thread worker in workers)
        {
            worker.Join();
        }
        foreach (IDisposable lease in leases)
        {
            lease.Dispose();
        }

        Assert.IsFalse(owner.IsActive);
        Assert.AreEqual(2, notifications);
    }

    [TestMethod]
    public void BeginNotificationFailure_RollsBackToInactive()
    {
        var owner = new ChartMutationActivityOwner();
        bool throwOnActive = true;
        owner.ActivityChanged += (_, _) =>
        {
            if (throwOnActive && owner.IsActive)
            {
                throwOnActive = false;
                throw new InvalidOperationException("publish failed");
            }
        };

        Assert.ThrowsException<InvalidOperationException>(() => owner.Enter());
        Assert.IsFalse(owner.IsActive);
    }

    [TestMethod]
    public void BeginNotificationFailure_DoesNotReleaseAConcurrentSurvivingLease()
    {
        var owner = new ChartMutationActivityOwner();
        IDisposable? survivingLease = null;
        bool throwOnActive = true;
        owner.ActivityChanged += (_, _) =>
        {
            if (throwOnActive && owner.IsActive)
            {
                throwOnActive = false;
                survivingLease = owner.Enter();
                throw new InvalidOperationException("publish failed");
            }
        };

        Assert.ThrowsException<InvalidOperationException>(() => owner.Enter());
        Assert.IsTrue(owner.IsActive);

        survivingLease!.Dispose();
        Assert.IsFalse(owner.IsActive);
    }

    [TestMethod]
    public void FinalReleaseNotificationFailure_LeavesOwnerInactive()
    {
        var owner = new ChartMutationActivityOwner();
        bool throwOnInactive = true;
        owner.ActivityChanged += (_, _) =>
        {
            if (throwOnInactive && !owner.IsActive)
            {
                throwOnInactive = false;
                throw new InvalidOperationException("release publish failed");
            }
        };

        IDisposable lease = owner.Enter();
        Assert.ThrowsException<InvalidOperationException>(() => lease.Dispose());
        Assert.IsFalse(owner.IsActive);
        lease.Dispose();
    }
}
