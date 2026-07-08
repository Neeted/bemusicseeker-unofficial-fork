using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public class ResourceHealthIndexFullRebuildCoordinatorTests
{
    [TestMethod]
    public void Rebuild_StaleFullOwnedTargetReturnsCurrentSnapshotWithoutBuildLog()
    {
        var host = new CapturingHost
        {
            PublishFullOwnedStale = true
        };
        var coordinator = new ResourceHealthIndexFullRebuildCoordinator(host);
        ResourceMaintenanceTargetSet targetSet = CreateFullOwnedTargetSet();

        ResourceHealthIndexFullRebuildResult result = coordinator.Rebuild("maintenance_hydration", targetSet);

        Assert.AreSame(host.CurrentSnapshot, result.Snapshot);
        Assert.IsTrue(result.StaleFullOwnedTarget);
        Assert.IsTrue(host.BuildCalled);
        Assert.IsTrue(host.PublishFullOwnedCalled);
        Assert.IsFalse(host.PublishSnapshotCalled);
        Assert.IsFalse(host.BuildLogged);
        Assert.AreEqual("maintenance_hydration", host.PublishFullOwnedReason);
        Assert.AreEqual(targetSet.Count, host.PublishFullOwnedTargetCount);
    }

    [TestMethod]
    public void Rebuild_FreshFullOwnedTargetPublishesAndLogsBuild()
    {
        var host = new CapturingHost();
        var coordinator = new ResourceHealthIndexFullRebuildCoordinator(host);

        ResourceHealthIndexFullRebuildResult result = coordinator.Rebuild("resource_health", CreateFullOwnedTargetSet());

        Assert.AreSame(host.BuiltSnapshot, result.Snapshot);
        Assert.IsFalse(result.StaleFullOwnedTarget);
        Assert.IsTrue(host.PublishFullOwnedCalled);
        Assert.IsFalse(host.PublishSnapshotCalled);
        Assert.IsTrue(host.BuildLogged);
        Assert.AreSame(host.BuiltSnapshot, host.LoggedSnapshot);
    }

    [TestMethod]
    public void Rebuild_UnspecifiedTargetCreatesFullOwnedTarget()
    {
        var host = new CapturingHost();
        var coordinator = new ResourceHealthIndexFullRebuildCoordinator(host);

        ResourceHealthIndexFullRebuildResult result = coordinator.Rebuild("resource_health", default);

        Assert.AreSame(host.BuiltSnapshot, result.Snapshot);
        Assert.IsTrue(host.CreateFullOwnedTargetSetCalled);
        Assert.AreEqual("resource_health", host.CreateFullOwnedReason);
        Assert.IsTrue(host.PublishFullOwnedCalled);
    }

    [TestMethod]
    public void Rebuild_SpecifiedSubsetTargetFallsBackToFullOwnedTarget()
    {
        var host = new CapturingHost();
        var coordinator = new ResourceHealthIndexFullRebuildCoordinator(host);

        ResourceHealthIndexFullRebuildResult result = coordinator.Rebuild(
            "resource_health",
            ResourceMaintenanceTargetSet.ForSubset([CreateChart()]));

        Assert.AreSame(host.BuiltSnapshot, result.Snapshot);
        Assert.IsTrue(host.CreateFullOwnedTargetSetCalled);
        Assert.IsTrue(host.PublishFullOwnedCalled);
        Assert.AreEqual(host.DefaultTargetSet.Count, host.PublishFullOwnedTargetSet.Count);
    }

    private static ResourceMaintenanceTargetSet CreateFullOwnedTargetSet()
    {
        return ResourceMaintenanceTargetSet.ForFullOwned(
            [CreateChart()],
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 2,
            resourceHealthInputVersion: 3);
    }

    private static ChartFile CreateChart()
    {
        return new ChartFile(
            kind: ChartFileKind.Bms,
            path: @"C:\charts\sample.bms",
            md5: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256: new string('b', 64),
            title: "Title",
            rawTitle: "Title",
            artist: "Artist",
            genre: string.Empty,
            folder: "charts",
            tag: string.Empty,
            levelText: string.Empty,
            level: null,
            mode: null,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: null);
    }

    private sealed class CapturingHost : IResourceHealthIndexFullRebuildHost
    {
        public ResourceHealthIndexSnapshot CurrentSnapshot { get; } = ResourceHealthIndexSnapshot.Build([], null, 101);

        public ResourceHealthIndexSnapshot BuiltSnapshot { get; } = ResourceHealthIndexSnapshot.Build([], null, 102);

        public ResourceMaintenanceTargetSet DefaultTargetSet { get; } = CreateFullOwnedTargetSet();

        public bool PublishFullOwnedStale { get; set; }

        public bool CreateFullOwnedTargetSetCalled { get; private set; }

        public string CreateFullOwnedReason { get; private set; } = string.Empty;

        public bool BuildCalled { get; private set; }

        public bool PublishFullOwnedCalled { get; private set; }

        public string PublishFullOwnedReason { get; private set; } = string.Empty;

        public ResourceMaintenanceTargetSet PublishFullOwnedTargetSet { get; private set; }

        public int PublishFullOwnedTargetCount { get; private set; }

        public bool PublishSnapshotCalled { get; private set; }

        public bool BuildLogged { get; private set; }

        public ResourceHealthIndexSnapshot LoggedSnapshot { get; private set; } = ResourceHealthIndexSnapshot.Empty;

        public ResourceMaintenanceTargetSet CreateFullOwnedResourceMaintenanceTargetSet(string reason)
        {
            CreateFullOwnedTargetSetCalled = true;
            CreateFullOwnedReason = reason;
            return DefaultTargetSet;
        }

        public ResourceHealthIndexSnapshot BuildResourceHealthIndexSnapshot(IEnumerable<ChartFile> targets)
        {
            BuildCalled = true;
            return BuiltSnapshot;
        }

        public ResourceHealthIndexFullOwnedPublishResult PublishFullOwnedSnapshot(
            string reason,
            ResourceMaintenanceTargetSet targetSet,
            ResourceHealthIndexSnapshot snapshot,
            int targetCount)
        {
            PublishFullOwnedCalled = true;
            PublishFullOwnedReason = reason;
            PublishFullOwnedTargetSet = targetSet;
            PublishFullOwnedTargetCount = targetCount;
            return PublishFullOwnedStale
                ? new ResourceHealthIndexFullOwnedPublishResult(CurrentSnapshot, staleFullOwnedTarget: true)
                : new ResourceHealthIndexFullOwnedPublishResult(snapshot, staleFullOwnedTarget: false);
        }

        public void PublishSnapshot(ResourceHealthIndexSnapshot snapshot)
        {
            PublishSnapshotCalled = true;
        }

        public void LogResourceHealthIndexBuild(string reason, ResourceHealthIndexSnapshot snapshot)
        {
            BuildLogged = true;
            LoggedSnapshot = snapshot;
        }
    }
}
