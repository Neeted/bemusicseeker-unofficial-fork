using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsLibraryOptionsSnapshotTests
{
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
        int original = Settings.Default.PendingInstallEstimateMaxParallelPackages;
        try
        {
            Settings.Default.PendingInstallEstimateMaxParallelPackages = 3;
            BmsLibraryOptionsSnapshot first = BmsLibraryOptionsSnapshot.CreateCurrent(Settings.Default);

            Settings.Default.PendingInstallEstimateMaxParallelPackages = 5;
            BmsLibraryOptionsSnapshot second = BmsLibraryOptionsSnapshot.CreateCurrent(Settings.Default);

            Assert.AreEqual(3, first.PendingInstallEstimateMaxParallelPackages);
            Assert.AreEqual(5, second.PendingInstallEstimateMaxParallelPackages);
        }
        finally
        {
            Settings.Default.PendingInstallEstimateMaxParallelPackages = original;
        }
    }
}
