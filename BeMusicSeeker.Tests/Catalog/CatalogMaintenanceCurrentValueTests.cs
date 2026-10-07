using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CatalogMaintenanceCurrentValueTests
{
    /// <summary>保守変更は現在値と対象通知へ反映し、捕捉値と集合版を変更しません。</summary>
    [TestMethod]
    public void MaintenanceCurrentValue_PublishesTargetWithoutMutatingCaptureOrCollectionVersion()
    {
        ChartFile captured = ChartFileProjection.WithMaintenance(
            ChartTestValues.Empty() with { Token = new OwnedChartToken(), Path = @"C:\Library\chart.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            new ResourceHealthMaintenanceSnapshot { Path = @"C:\Library\chart.bms", Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Encoding = "shift_jis", Origin = MaintenanceInfoOrigin.Calculated });
        var owner = new CatalogOwnedCollectionOwner();
        owner.ReplaceCharts([captured], []);
        int version = owner.OwnedCollectionVersion;
        ChartFile next = ChartFileProjection.WithMaintenance(captured,
            captured.ResourceHealthMaintenanceSnapshot with { Encoding = "utf-16" });
        owner.Collection.ApplyCurrentChartValue(next);
        var publisher = new NormalLibraryRefreshPublisher();
        publisher.Publish(new NormalLibraryRefreshPublishRequest
        {
            OwnedCollectionVersion = version,
            Effects = LibraryChartRefreshEffects.MaintenancePresentationChanged,
            ChangedCharts = [next]
        });
        NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(0);

        Assert.AreEqual("shift_jis", captured.ResourceHealthMaintenanceSnapshot.Encoding);
        Assert.AreEqual("utf-16", owner.BmsRows[0].ResourceHealthMaintenanceSnapshot.Encoding);
        Assert.AreSame(captured.Token, owner.BmsRows[0].Token);
        Assert.AreEqual(version, owner.OwnedCollectionVersion);
        Assert.AreEqual(1, batch.ChangedCharts.Count);
        Assert.AreSame(captured.Token, batch.ChangedCharts[0].Token);
        Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
    }
}
