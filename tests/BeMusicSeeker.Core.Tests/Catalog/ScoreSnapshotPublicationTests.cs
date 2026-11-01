using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

/// <summary>実score setterと全lookup再公開から、独立rangeが通常queueへ登録されるmodel境界を検証します。</summary>
[TestClass]
public sealed class ScoreSnapshotPublicationTests
{
    /// <summary>補助通知だけの破棄は独立score結果を消さず、catalog resetの整理とも独立して同じrangeを返す。</summary>
    [TestMethod]
    public void Publisher_LegacyClearPreservesIndependentScoreRange()
    {
        var publisher = new NormalLibraryRefreshPublisher();
        var score = new ScoreSnapshotChange(4, 5, ["a"], []);
        int scoreNotification = publisher.Register(score);
        int legacy = publisher.Publish(new NormalLibraryRefreshPublishRequest { ResetsPriorNotifications = true });
        publisher.ClearLegacyNotification(legacy);
        publisher.ClearLegacyNotification(scoreNotification);
        NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(0);
        Assert.AreSame(score, batch.ScoreChanges.Single());
        Assert.IsTrue(batch.HasRefreshNotification);
    }

}
