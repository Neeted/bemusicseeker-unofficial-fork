using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public class ResourceHealthFullOwnedTargetFreshnessTests
{
    /// <summary>
    /// 保証：全所持対象の捕捉世代と現在の所持・リソース入力世代が一致して安定している場合だけ検査結果を最新として受理する。
    /// </summary>
    [TestMethod]
    public void IsCurrent_ReturnsTrueForMatchingFullOwnedVersions()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new OwnedChartCollectionVersionSnapshot(30),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: true);

        Assert.IsTrue(isCurrent);
    }

    /// <summary>
    /// 保証：所持世代が進んだ全所持対象を最新とせず、古い対象集合の検査結果を現行へ適用しない。
    /// </summary>
    [TestMethod]
    public void IsCurrent_ReturnsFalseForStaleOwnedCollectionVersion()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new OwnedChartCollectionVersionSnapshot(31),
            currentOwnedCollectionVersion: 31,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: true);

        Assert.IsFalse(isCurrent);
    }

    /// <summary>
    /// 保証：リソース入力世代が進んだ全所持対象を最新とせず、変更前のリソース検査結果を適用しない。
    /// </summary>
    [TestMethod]
    public void IsCurrent_ReturnsFalseForStaleResourceHealthInputVersion()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new OwnedChartCollectionVersionSnapshot(30),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 41,
            currentResourceHealthInputVersionStable: true);

        Assert.IsFalse(isCurrent);
    }

    /// <summary>
    /// 保証：世代番号が一致しても現在のリソース入力が不安定なら受理せず、更新途中の結果を最新扱いしない。
    /// </summary>
    [TestMethod]
    public void IsCurrent_ReturnsFalseForUnstableCurrentResourceHealthInputVersion()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new OwnedChartCollectionVersionSnapshot(30),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: false);

        Assert.IsFalse(isCurrent);
    }

    /// <summary>
    /// 保証：部分対象には全所持の最新性判定を適用せず、未検査の所持譜面まで検査済みとして扱わない。
    /// </summary>
    [TestMethod]
    public void IsCurrent_ReturnsFalseForSubsetTarget()
    {
        var targetSet = ResourceMaintenanceTargetSet.ForSubset([]);

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new OwnedChartCollectionVersionSnapshot(30),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: true);

        Assert.IsFalse(isCurrent);
    }

    private static ResourceMaintenanceTargetSet CreateFullOwnedTargetSet()
    {
        return ResourceMaintenanceTargetSet.ForFullOwned(
            new List<ChartFile>(),
            new OwnedChartCollectionVersionSnapshot(30),
            ownedCollectionVersion: 30,
            resourceHealthInputVersion: 40);
    }
}
