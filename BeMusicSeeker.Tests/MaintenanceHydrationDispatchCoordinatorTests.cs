using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MaintenanceHydrationDispatchCoordinatorTests
{
    [TestMethod]
    public void Dispatch_BuildsPlanAndCopiesResourceHealthIndexMs()
    {
        var host = new CapturingHost(resourceHealthIndexMs: 123);
        var coordinator = new MaintenanceHydrationDispatchCoordinator(host);
        var hydrationResult = new MaintenanceTableHydrationResult();
        ResourceMaintenanceTargetSet targetSet = ResourceMaintenanceTargetSet.ForFullOwned(
            new List<ChartFile>(),
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 2,
            resourceHealthInputVersion: 3);

        coordinator.Dispatch(hydrationResult, targetSet);

        Assert.AreEqual(123, hydrationResult.ResourceHealthIndexMs);
        Assert.IsNotNull(host.LastPlan);
        Assert.IsTrue(host.LastPlan.WarningPresentationChanged);
        Assert.IsTrue(host.LastPlan.MaintenancePresentationChanged);
        Assert.IsTrue(host.LastPlan.ResourceHealthMutation.RebuildFull);
        Assert.AreEqual(targetSet.Count, host.LastPlan.ResourceHealthMutation.FullOwnedTargetSet.Count);
        Assert.IsTrue(host.LastPlan.ResourceHealthMutation.FullOwnedTargetSet.HasFullOwnedVersion);
    }

    [TestMethod]
    public void Dispatch_AllowsNullHydrationResult()
    {
        var host = new CapturingHost(resourceHealthIndexMs: 456);
        var coordinator = new MaintenanceHydrationDispatchCoordinator(host);

        coordinator.Dispatch(null, ResourceMaintenanceTargetSet.ForSubset(new List<ChartFile>()));

        Assert.IsNotNull(host.LastPlan);
        Assert.IsTrue(host.LastPlan.ResourceHealthMutation.RebuildFull);
    }

    private sealed class CapturingHost(long resourceHealthIndexMs) : IMaintenanceHydrationDispatchHost
    {
        public MaintenanceHydrationDispatchPlan LastPlan { get; private set; } = null!;

        public long DispatchMaintenanceHydration(MaintenanceHydrationDispatchPlan plan)
        {
            LastPlan = plan;
            return resourceHealthIndexMs;
        }
    }
}
