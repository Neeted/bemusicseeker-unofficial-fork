using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PackageChartEntryNotificationTests
{
    [TestMethod]
    public void DeferredPublicationReleaseEndsSuppressionBeforeUiPublication()
    {
        PackageChartEntry entry = ChartPackageTestExtensions.CreateEntry(new BMSFile());
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
        Assert.IsNotNull(publication);
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
