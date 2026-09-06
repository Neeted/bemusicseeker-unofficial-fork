using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryOptionsSnapshotTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void AdditionalOutputDirectoriesAreCopiedWhenSnapshotIsCreated()
    {
        var source = new List<string> { "first", "second" };
        BmsLibraryOptionsSnapshot snapshot = new()
        {
            LR2CustomFolderAdditionalOutputBaseDirs = source
        };

        source[0] = "changed";
        source.Add("third");

        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            snapshot.LR2CustomFolderAdditionalOutputBaseDirs.ToArray());
        Assert.ThrowsException<NotSupportedException>(() =>
            ((IList<string>)snapshot.LR2CustomFolderAdditionalOutputBaseDirs)[0] = "not allowed");
    }

    [TestMethod]
    public void CreateCurrentReturnsValuesFromTheCurrentSettingsEachTime()
    {
        int original = testSettings.PendingInstallEstimateMaxParallelPackages;
        try
        {
            testSettings.PendingInstallEstimateMaxParallelPackages = 3;
            BmsLibraryOptionsSnapshot first = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);

            testSettings.PendingInstallEstimateMaxParallelPackages = 5;
            BmsLibraryOptionsSnapshot second = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);

            Assert.AreEqual(3, first.PendingInstallEstimateMaxParallelPackages);
            Assert.AreEqual(5, second.PendingInstallEstimateMaxParallelPackages);
        }
        finally
        {
            testSettings.PendingInstallEstimateMaxParallelPackages = original;
        }
    }

    [TestMethod]
    public void CreateCurrentRetainsRawAdditionalOutputConfiguration()
    {
        string previous = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        const string raw = "[\"missing-additional-base\"]";
        try
        {
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = raw;

            BmsLibraryOptionsSnapshot snapshot = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);

            Assert.AreEqual(raw, snapshot.LR2CustomFolderAdditionalOutputBaseDirsSerialized);
        }
        finally
        {
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previous;
        }
    }
}
