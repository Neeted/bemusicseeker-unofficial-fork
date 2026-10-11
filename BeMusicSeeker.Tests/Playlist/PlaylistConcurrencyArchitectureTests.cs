using System;
using System.Collections.Generic;
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

    /// <summary>読取り・書込み・昇格可能読取りの実取得で対応する回数を増やし、各解放で0へ戻す。Lock APIと観測状態のずれを検出する。</summary>
    [TestMethod]
    public void ReaderWriterLockSlimWrapper_RawLockApiUpdatesObservableCounts()
    {
        var rwlock = new ReaderWriterLockSlimWrapper();

        rwlock.EnterReadLock();
        try
        {
            Assert.AreEqual(1u, rwlock.LockingReadCount);
        }
        finally
        {
            rwlock.ExitReadLock();
        }
        Assert.AreEqual(0u, rwlock.LockingReadCount);

        rwlock.EnterWriteLock();
        try
        {
            Assert.AreEqual(1u, rwlock.LockingWriteCount);
        }
        finally
        {
            rwlock.ExitWriteLock();
        }
        Assert.AreEqual(0u, rwlock.LockingWriteCount);

        Assert.IsTrue(rwlock.TryEnterUpgradeableReadLock(TimeSpan.Zero));
        try
        {
            Assert.AreEqual(1u, rwlock.LockingWriteCount);
        }
        finally
        {
            rwlock.ExitUpgradeableReadLock();
        }
        Assert.AreEqual(0u, rwlock.LockingWriteCount);
    }
}
