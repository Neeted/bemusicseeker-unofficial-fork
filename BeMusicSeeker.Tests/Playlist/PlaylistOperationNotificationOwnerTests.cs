using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistOperationNotificationOwnerTests
{
    /// <summary>情報と警告を順序・重要度付きreceiptへ捕捉し、次の取出しには残さないことで、通知の分類違いと再通知を検出します。</summary>
    [TestMethod]
    public void Session_TakeReceiptCopiesAndDrainsOrderedFacts()
    {
        var owner = new PlaylistOperationNotificationOwner();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = owner.BeginSession();

        owner.QueueInformation("info", "caption");
        owner.QueueWarning("warning");

        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();

        Assert.AreEqual(2, receipt.Notifications.Count);
        Assert.AreEqual("info", receipt.Notifications[0].Message);
        Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information, receipt.Notifications[0].Severity);
        Assert.AreEqual("warning", receipt.Notifications[1].Message);
        Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, receipt.Notifications[1].Severity);
        Assert.AreEqual(0, session.TakeReceipt().Notifications.Count);
    }

    /// <summary>一session内の別公開段階は独立receiptを持ち、後から積んだ通知が先のreceiptへ混ざる誤りを検出します。</summary>
    [TestMethod]
    public void Session_AllowsIndependentReceiptsForMultiplePresentationBoundaries()
    {
        var owner = new PlaylistOperationNotificationOwner();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = owner.BeginSession();

        owner.QueueWarning("external-sync");
        PlaylistOperationNotificationOwner.OperationNotificationReceipt first = session.TakeReceipt();
        owner.QueueWarning("custom-folder");
        PlaylistOperationNotificationOwner.OperationNotificationReceipt second = session.TakeReceipt();

        Assert.AreEqual(1, first.Notifications.Count);
        Assert.AreEqual("external-sync", first.Notifications[0].Message);
        Assert.AreEqual(1, second.Notifications.Count);
        Assert.AreEqual("custom-folder", second.Notifications[0].Message);
    }

    /// <summary>子sessionの終了は親を復元して前後通知を保持し、他ownerへの通知は拒否して親通知の脱落とowner混入を検出します。</summary>
    [TestMethod]
    public void NestedSessionsRestoreParentAndDoNotMixOwners()
    {
        var owner = new PlaylistOperationNotificationOwner();
        var otherOwner = new PlaylistOperationNotificationOwner();
        using PlaylistOperationNotificationOwner.OperationNotificationSession parent = owner.BeginSession();
        owner.QueueWarning("parent-before");

        using (PlaylistOperationNotificationOwner.OperationNotificationSession child = owner.BeginSession())
        {
            owner.QueueInformation("child", "caption");
            Assert.AreEqual(1, child.TakeReceipt().Notifications.Count);
        }

        owner.QueueWarning("parent-after");
        PlaylistOperationNotificationOwner.OperationNotificationReceipt parentReceipt = parent.TakeReceipt();
        Assert.AreEqual(2, parentReceipt.Notifications.Count);
        Assert.AreEqual("parent-before", parentReceipt.Notifications[0].Message);
        Assert.AreEqual("parent-after", parentReceipt.Notifications[1].Message);

        Assert.ThrowsException<InvalidOperationException>(() => otherOwner.QueueWarning("must fail"));
    }

    /// <summary>sessionなしの通知要求は失敗し、空sessionは空receiptを返すことで、通知の無所属受付と空通知を検出します。</summary>
    [TestMethod]
    public void QueueWithoutSessionFailsClosedAndEmptyReceiptIsSilent()
    {
        var owner = new PlaylistOperationNotificationOwner();

        Assert.ThrowsException<InvalidOperationException>(() => owner.QueueWarning("must fail"));
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = owner.BeginSession();
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();

        Assert.IsTrue(receipt.IsEmpty);
        Assert.AreEqual(0, receipt.Notifications.Count);
    }
}
