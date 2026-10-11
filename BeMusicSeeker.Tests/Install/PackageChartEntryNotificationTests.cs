using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PackageChartEntryNotificationTests
{
    /// <summary>保留通知のreleaseが抑制を先に終え、次の状態変更と遅延公開を各一回通知することから公開取消後の抑制残留を検出します。</summary>
    [TestMethod]
    public void DeferredPublicationReleaseEndsSuppressionBeforeUiPublication()
    {
        PackageChartEntry entry = ChartPackageTestExtensions.CreateEntry(ChartTestValues.Empty());
        int notificationCount = 0;
        entry.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(PackageChartEntry.Chart), StringComparison.Ordinal))
            {
                notificationCount++;
            }
        };

        Func<Action> release = entry.DeferPropertyChangedNotificationPublication();
        entry.SetSearchingStatus(isSearching: true);
        Action publication = release();
        Assert.AreEqual(0, notificationCount);

        entry.SetSearchingStatus(isSearching: false);
        Assert.AreEqual(
            1,
            notificationCount,
            "A canceled UI publication must not leave future package-entry changes suppressed.");

        publication();
        Assert.AreEqual(2, notificationCount);
    }
}
