using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public class ResourceHealthFullOwnedTargetFreshnessTests
{
    [TestMethod]
    public void IsCurrent_ReturnsTrueForMatchingFullOwnedVersions()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new StorageRowsVersionSnapshot(10, 20),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: true);

        Assert.IsTrue(isCurrent);
    }

    [TestMethod]
    public void IsCurrent_ReturnsFalseForStaleStorageRowsVersion()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new StorageRowsVersionSnapshot(10, 21),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: true);

        Assert.IsFalse(isCurrent);
    }

    [TestMethod]
    public void IsCurrent_ReturnsFalseForStaleOwnedCollectionVersion()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new StorageRowsVersionSnapshot(10, 20),
            currentOwnedCollectionVersion: 31,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: true);

        Assert.IsFalse(isCurrent);
    }

    [TestMethod]
    public void IsCurrent_ReturnsFalseForStaleResourceHealthInputVersion()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new StorageRowsVersionSnapshot(10, 20),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 41,
            currentResourceHealthInputVersionStable: true);

        Assert.IsFalse(isCurrent);
    }

    [TestMethod]
    public void IsCurrent_ReturnsFalseForUnstableCurrentResourceHealthInputVersion()
    {
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new StorageRowsVersionSnapshot(10, 20),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: false);

        Assert.IsFalse(isCurrent);
    }

    [TestMethod]
    public void IsCurrent_ReturnsFalseForSubsetTarget()
    {
        ResourceMaintenanceTargetSet targetSet = ResourceMaintenanceTargetSet.ForSubset([]);

        bool isCurrent = ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            new StorageRowsVersionSnapshot(10, 20),
            currentOwnedCollectionVersion: 30,
            currentResourceHealthInputVersion: 40,
            currentResourceHealthInputVersionStable: true);

        Assert.IsFalse(isCurrent);
    }

    private static ResourceMaintenanceTargetSet CreateFullOwnedTargetSet()
    {
        return ResourceMaintenanceTargetSet.ForFullOwned(
            new List<ChartFile>(),
            new StorageRowsVersionSnapshot(10, 20),
            ownedCollectionVersion: 30,
            resourceHealthInputVersion: 40);
    }
}
