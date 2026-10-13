using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartMutationActivityOwnerTests
{
    /// <summary>
    /// 保証：変更処理の入れ子では最外開始・最終解放だけを通知し、内側解放や同じ権限の再解放で活動状態を変えない。
    /// </summary>
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

    /// <summary>
    /// 保証：複数threadの受付を保持してから全権限を解放すると非活動へ収束し、途中解放による早期終了や重複通知を防ぐ。
    /// </summary>
    [TestMethod]
    public void ConcurrentLeases_ConvergeToInactiveWithoutUnderflow()
    {
        var owner = new ChartMutationActivityOwner();
        int notifications = 0;
        owner.ActivityChanged += (_, _) => Interlocked.Increment(ref notifications);
        using var start = new ManualResetEventSlim();
        using var entered = new CountdownEvent(8);
        using var release = new ManualResetEventSlim();
        var workers = new Task[8];

        for (int index = 0; index < workers.Length; index++)
        {
            workers[index] = Task.Factory.StartNew(() =>
            {
                IDisposable? lease = null;
                try
                {
                    start.Wait();
                    try { lease = owner.Enter(); }
                    finally { entered.Signal(); }
                    release.Wait();
                }
                finally { lease?.Dispose(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        start.Set();
        try
        {
            entered.Wait();
            Assert.IsTrue(owner.IsActive);
        }
        finally
        {
            release.Set();
            Task.WaitAll(workers);
        }

        Assert.IsFalse(owner.IsActive);
        Assert.AreEqual(2, notifications);
    }

    /// <summary>
    /// 保証：表示購読者が開始・終了通知で失敗しても活動中のleaseを返し、後続購読者にも実際の活動状態を知らせます。
    /// </summary>
    [TestMethod]
    public void NotificationFailure_ReturnsActiveLeaseAndContinuesLaterObservers()
    {
        var owner = new ChartMutationActivityOwner();
        var transitions = new List<bool>();
        owner.ActivityChanged += (_, _) => throw new InvalidOperationException("publish failed");
        owner.ActivityChanged += (_, _) => transitions.Add(owner.IsActive);

        IDisposable lease = owner.Enter();
        Assert.IsTrue(owner.IsActive);
        CollectionAssert.AreEqual(new[] { true }, transitions);

        lease.Dispose();
        Assert.IsFalse(owner.IsActive);
        CollectionAssert.AreEqual(new[] { true, false }, transitions);
    }

    /// <summary>
    /// 保証：開始通知中に入れ子の活動期間が成立してから通知が失敗しても、返却した外側leaseと内側leaseを共に保持します。
    /// 外側の解放だけでは非活動へ戻らず、最後の内側解放で終了します。
    /// </summary>
    [TestMethod]
    public void BeginNotificationFailure_PreservesOuterAndNestedLeases()
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

        IDisposable outer = owner.Enter();
        Assert.IsTrue(owner.IsActive);

        outer.Dispose();
        Assert.IsTrue(owner.IsActive);
        Assert.IsNotNull(survivingLease);
        survivingLease.Dispose();
        Assert.IsFalse(owner.IsActive);
    }

    /// <summary>
    /// 保証：最終解放通知が失敗しても非活動状態と後続購読者への通知は成立し、再解放は冪等です。
    /// 次の明示的なEnterも受け付け、過去の通知失敗で活動数を壊しません。
    /// </summary>
    [TestMethod]
    public void FinalReleaseNotificationFailure_KeepsInactiveAndNextEnterAvailable()
    {
        var owner = new ChartMutationActivityOwner();
        var transitions = new List<bool>();
        owner.ActivityChanged += (_, _) =>
        {
            if (!owner.IsActive)
            {
                throw new InvalidOperationException("release publish failed");
            }
        };
        owner.ActivityChanged += (_, _) => transitions.Add(owner.IsActive);

        IDisposable lease = owner.Enter();
        lease.Dispose();
        Assert.IsFalse(owner.IsActive);
        lease.Dispose();
        CollectionAssert.AreEqual(new[] { true, false }, transitions);

        using (owner.Enter()) { Assert.IsTrue(owner.IsActive); }
        Assert.IsFalse(owner.IsActive);
        CollectionAssert.AreEqual(new[] { true, false, true, false }, transitions);
    }

    /// <summary>
    /// 保証：using本体の失敗が最終解放時の表示購読者の失敗で置き換わらず、非活動へ戻って次のEnterを受け付けます。
    /// </summary>
    [TestMethod]
    public void UsingBodyFailure_SurvivesFinalReleaseNotificationFailure()
    {
        var owner = new ChartMutationActivityOwner();
        owner.ActivityChanged += (_, _) =>
        {
            if (!owner.IsActive) { throw new InvalidOperationException("release publish failed"); }
        };

        InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using (owner.Enter()) { throw new InvalidOperationException("mutation failed"); }
        });

        Assert.AreEqual("mutation failed", failure.Message);
        Assert.IsFalse(owner.IsActive);
        using (owner.Enter()) { Assert.IsTrue(owner.IsActive); }
        Assert.IsFalse(owner.IsActive);
    }
}
