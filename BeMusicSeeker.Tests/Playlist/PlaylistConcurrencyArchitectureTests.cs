using System;
using System.Collections.Generic;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>画面が購読するモデル値とロック状態の通知・購読解除を検証する。</summary>
/// <remarks>実モデル・実ロックを直接操作する。ソースの内部経路名では同期や設定捕捉を保証せず、それぞれの実所有者・出力テストへ分担する。</remarks>
[TestClass]
public sealed class PlaylistConcurrencyArchitectureTests
{
    /// <summary>rankingを変更して同じ値を再代入しても通知は対象プロパティに一回だけ届く。変更通知漏れと同値再代入の重複通知を検出する。</summary>
    [TestMethod]
    public void ModelPropertyMutation_PublishesExistingPropertyNameOnce()
    {
        BMSScore score = new();
        List<string> changedProperties = [];
        score.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName!);

        score.ranking = 7;
        score.ranking = 7;

        CollectionAssert.AreEqual(new[] { nameof(BMSScore.ranking) }, changedProperties);
    }

    /// <summary>実読取りロックの取得・解放で読取り回数のプロパティ通知を各一回届ける。ロック状態が変わっても画面へ通知しない誤りを検出する。</summary>
    [TestMethod]
    public void ReaderWriterLock_PublishesLockCountThroughBclNotificationContract()
    {
        ReaderWriterLockSlimWrapper readerWriterLock = new();
        List<string> changedProperties = [];
        readerWriterLock.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName!);

        readerWriterLock.EnterReadLock();
        readerWriterLock.ExitReadLock();

        CollectionAssert.AreEqual(new[] { nameof(ReaderWriterLockSlimWrapper.LockingReadCount), nameof(ReaderWriterLockSlimWrapper.LockingReadCount) }, changedProperties);
    }

    /// <summary>登録プロパティの変更を転送し、購読を破棄した後の変更は転送しない。購読解除漏れによる終端後の通知を検出する。</summary>
    [TestMethod]
    public void PropertyChangedSubscription_ForwardsRegisteredPropertyAndStopsAfterDispose()
    {
        BMSScore score = new();
        int callbackCount = 0;
        var subscription = PropertyChangedSubscription.Create(score);
        subscription.RegisterHandler(() => score.ranking, () => callbackCount++);

        score.ranking = 1;
        subscription.Dispose();
        score.ranking = 2;

        Assert.AreEqual(1, callbackCount);
    }

    /// <summary>正常または毎回throwする表示購読者の下でも実読取り・書込み・昇格可能読取りを取得し、既存の計上区分と全解放後の0・未保持を保ちます。通知を実lock取得・解放の失敗へ変える誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReaderWriterLockSlimWrapper_RawLockApiUpdatesObservableCounts(bool throwFromObserver)
    {
        var rwlock = new ReaderWriterLockSlimWrapper();
        int notificationCount = 0;
        PropertyChangedEventHandler observer = (_, _) =>
        {
            notificationCount++;
            if (throwFromObserver)
            {
                throw new InvalidOperationException("raw lock display notification failure");
            }
        };
        rwlock.PropertyChanged += observer;
        try
        {
            rwlock.EnterReadLock();
            try
            {
                Assert.AreEqual(1u, rwlock.LockingReadCount);
                Assert.IsTrue(rwlock.IsReadLockHeld);
            }
            finally
            {
                rwlock.ExitReadLock();
            }
            Assert.AreEqual(0u, rwlock.LockingReadCount);
            Assert.IsFalse(rwlock.IsReadLockHeld);

            rwlock.EnterWriteLock();
            try
            {
                Assert.AreEqual(1u, rwlock.LockingWriteCount);
                Assert.IsTrue(rwlock.IsWriteLockHeld);
            }
            finally
            {
                rwlock.ExitWriteLock();
            }
            Assert.AreEqual(0u, rwlock.LockingWriteCount);
            Assert.IsFalse(rwlock.IsWriteLockHeld);

            Assert.IsTrue(rwlock.TryEnterUpgradeableReadLock(TimeSpan.Zero));
            try
            {
                Assert.AreEqual(1u, rwlock.LockingWriteCount);
                Assert.AreEqual(0u, rwlock.LockingUpgradeCount);
                Assert.IsTrue(rwlock.IsUpgradeableReadLockHeld);
            }
            finally
            {
                rwlock.ExitUpgradeableReadLock();
            }
            Assert.AreEqual(0u, rwlock.LockingReadCount);
            Assert.AreEqual(0u, rwlock.LockingWriteCount);
            Assert.AreEqual(0u, rwlock.LockingUpgradeCount);
            Assert.IsFalse(rwlock.IsUpgradeableReadLockHeld);
            Assert.AreEqual(6, notificationCount);
        }
        finally
        {
            // 修正前の取得時通知故障でも、購読解除後に同じthreadの実lockを回収します。
            rwlock.PropertyChanged -= observer;
            if (rwlock.IsWriteLockHeld) { rwlock.ExitWriteLock(); }
            if (rwlock.IsUpgradeableReadLockHeld) { rwlock.ExitUpgradeableReadLock(); }
            if (rwlock.IsReadLockHeld) { rwlock.ExitReadLock(); }
            rwlock.Dispose();
        }
    }
}
