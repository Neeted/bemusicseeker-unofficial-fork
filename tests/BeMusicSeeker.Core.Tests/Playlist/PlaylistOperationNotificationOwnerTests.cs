using BeMusicSeeker.Models.BmsLibraryInternal;

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

        owner.QueueInformation(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Success)), nameof(Properties.Resources.Success));
        owner.QueueWarning(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Warning)));

        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();

        Assert.AreEqual(2, receipt.Notifications.Count);
        Assert.AreEqual(Properties.Resources.Success, receipt.Notifications[0].FormatMessage());
        Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information, receipt.Notifications[0].Severity);
        Assert.AreEqual(Properties.Resources.Warning, receipt.Notifications[1].FormatMessage());
        Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, receipt.Notifications[1].Severity);
        Assert.AreEqual(0, session.TakeReceipt().Notifications.Count);
    }

    /// <summary>一session内の別公開段階は独立receiptを持ち、後から積んだ通知が先のreceiptへ混ざる誤りを検出します。</summary>
    [TestMethod]
    public void Session_AllowsIndependentReceiptsForMultiplePresentationBoundaries()
    {
        var owner = new PlaylistOperationNotificationOwner();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = owner.BeginSession();

        owner.QueueWarning(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Error)));
        PlaylistOperationNotificationOwner.OperationNotificationReceipt first = session.TakeReceipt();
        owner.QueueWarning(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Failure)));
        PlaylistOperationNotificationOwner.OperationNotificationReceipt second = session.TakeReceipt();

        Assert.AreEqual(1, first.Notifications.Count);
        Assert.AreEqual(Properties.Resources.Error, first.Notifications[0].FormatMessage());
        Assert.AreEqual(1, second.Notifications.Count);
        Assert.AreEqual(Properties.Resources.Failure, second.Notifications[0].FormatMessage());
    }

    /// <summary>子sessionの終了は親を復元して前後通知を保持し、他ownerへの通知は拒否して親通知の脱落とowner混入を検出します。</summary>
    [TestMethod]
    public void NestedSessionsRestoreParentAndDoNotMixOwners()
    {
        var owner = new PlaylistOperationNotificationOwner();
        var otherOwner = new PlaylistOperationNotificationOwner();
        using PlaylistOperationNotificationOwner.OperationNotificationSession parent = owner.BeginSession();
        owner.QueueWarning(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Success)));

        using (PlaylistOperationNotificationOwner.OperationNotificationSession child = owner.BeginSession())
        {
            owner.QueueInformation(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Failure)), nameof(Properties.Resources.Success));
            Assert.AreEqual(1, child.TakeReceipt().Notifications.Count);
        }

        owner.QueueWarning(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Warning)));
        PlaylistOperationNotificationOwner.OperationNotificationReceipt parentReceipt = parent.TakeReceipt();
        Assert.AreEqual(2, parentReceipt.Notifications.Count);
        Assert.AreEqual(Properties.Resources.Success, parentReceipt.Notifications[0].FormatMessage());
        Assert.AreEqual(Properties.Resources.Warning, parentReceipt.Notifications[1].FormatMessage());

        Assert.ThrowsExactly<InvalidOperationException>(() => otherOwner.QueueWarning(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Error))));
    }

    /// <summary>sessionなしの通知要求は失敗し、空sessionは空receiptを返すことで、通知の無所属受付と空通知を検出します。</summary>
    [TestMethod]
    public void QueueWithoutSessionFailsClosedAndEmptyReceiptIsSilent()
    {
        var owner = new PlaylistOperationNotificationOwner();

        Assert.ThrowsExactly<InvalidOperationException>(() => owner.QueueWarning(PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Properties.Resources.Error))));
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = owner.BeginSession();
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();

        Assert.IsTrue(receipt.IsEmpty);
        Assert.AreEqual(0, receipt.Notifications.Count);
    }

}
