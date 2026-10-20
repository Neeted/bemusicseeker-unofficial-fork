using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryOptionsSnapshotTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
    /// <summary>
    /// 保証：追加出力先を捕捉した後の元リスト変更を反映せず、公開リストへの書込みも拒否して操作中の設定を固定する。
    /// </summary>
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

    /// <summary>
    /// 保証：設定の並列上限と自動推定後の導入を変更して再捕捉すると新しい値を取得し、先に捕捉した操作入力の値は保持する。
    /// </summary>
    [TestMethod]
    public void CreateCurrentReturnsValuesFromTheCurrentSettingsEachTime()
    {
        int original = testSettings.PendingInstallEstimateMaxParallelPackages;
        bool originalAutoInstall = testSettings.AutoInstallAfterAutomaticEstimation;
        try
        {
            testSettings.PendingInstallEstimateMaxParallelPackages = 3;
            testSettings.AutoInstallAfterAutomaticEstimation = true;
            var first = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);

            testSettings.PendingInstallEstimateMaxParallelPackages = 5;
            testSettings.AutoInstallAfterAutomaticEstimation = false;
            var second = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);

            Assert.AreEqual(3, first.PendingInstallEstimateMaxParallelPackages);
            Assert.IsTrue(first.AutoInstallAfterAutomaticEstimation);
            Assert.AreEqual(5, second.PendingInstallEstimateMaxParallelPackages);
            Assert.IsFalse(second.AutoInstallAfterAutomaticEstimation);
        }
        finally
        {
            testSettings.PendingInstallEstimateMaxParallelPackages = original;
            testSettings.AutoInstallAfterAutomaticEstimation = originalAutoInstall;
        }
    }

    /// <summary>
    /// 保証：追加出力先の保存文字列を捕捉時に保持し、存在しない出力先の記述を空設定へ読み替えない。
    /// </summary>
    [TestMethod]
    public void CreateCurrentRetainsRawAdditionalOutputConfiguration()
    {
        string previous = testSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        const string raw = "[\"missing-additional-base\"]";
        try
        {
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = raw;

            var snapshot = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);

            Assert.AreEqual(raw, snapshot.LR2CustomFolderAdditionalOutputBaseDirsSerialized);
        }
        finally
        {
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = previous;
        }
    }
}
