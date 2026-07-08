using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public class ResourceHealthIndexMutationDispatcherTests
{
    [TestMethod]
    public void Dispatch_RebuildFullStaleTargetReturnsCurrentSnapshotWithoutFullRebuilt()
    {
        var host = new CapturingHost
        {
            RebuildStaleFullOwnedTarget = true
        };
        var dispatcher = new ResourceHealthIndexMutationDispatcher(host);
        var mutation = new ResourceHealthIndexMutation
        {
            RebuildFull = true,
            FullOwnedTargetSet = CreateFullOwnedTargetSet()
        };

        ResourceHealthIndexDispatchResult result = dispatcher.Dispatch(mutation, "maintenance_hydration");

        Assert.AreSame(host.CurrentSnapshot, result.Snapshot);
        Assert.IsFalse(result.FullRebuilt);
        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(result.Deferred);
        Assert.AreEqual(0, result.IndexMs);
        Assert.IsTrue(host.RebuildCalled);
        Assert.AreEqual("maintenance_hydration", host.RebuildReason);
        Assert.AreEqual(mutation.FullOwnedTargetSet.Count, host.RebuildTargetSet.Count);
    }

    [TestMethod]
    public void Dispatch_RebuildFullFreshTargetMarksFullRebuilt()
    {
        var host = new CapturingHost();
        var dispatcher = new ResourceHealthIndexMutationDispatcher(host);
        var mutation = new ResourceHealthIndexMutation
        {
            RebuildFull = true,
            FullOwnedTargetSet = CreateFullOwnedTargetSet()
        };

        ResourceHealthIndexDispatchResult result = dispatcher.Dispatch(mutation, "maintenance_hydration");

        Assert.AreSame(host.RebuildSnapshot, result.Snapshot);
        Assert.IsTrue(result.FullRebuilt);
        Assert.IsFalse(result.DeltaApplied);
        Assert.AreEqual(host.RebuildSnapshot.BuildMs, result.IndexMs);
    }

    [TestMethod]
    public void Dispatch_DeltaSuccessMarksDeltaApplied()
    {
        var host = new CapturingHost
        {
            DeltaApplies = true
        };
        var dispatcher = new ResourceHealthIndexMutationDispatcher(host);
        var mutation = new ResourceHealthIndexMutation();
        mutation.UpdatedTargets.Add(CreateChart());

        ResourceHealthIndexDispatchResult result = dispatcher.Dispatch(mutation, "delta");

        Assert.AreSame(host.DeltaSnapshot, result.Snapshot);
        Assert.IsTrue(result.DeltaApplied);
        Assert.IsFalse(result.FullRebuilt);
        Assert.AreEqual(host.DeltaSnapshot.BuildMs, result.IndexMs);
        Assert.IsTrue(host.TryApplyDeltaCalled);
    }

    [TestMethod]
    public void Dispatch_DeltaFailureInvalidatesWhenRequested()
    {
        var host = new CapturingHost();
        var dispatcher = new ResourceHealthIndexMutationDispatcher(host);
        var mutation = new ResourceHealthIndexMutation
        {
            InvalidateIfDeltaFails = true
        };
        mutation.UpdatedTargets.Add(CreateChart());

        ResourceHealthIndexDispatchResult result = dispatcher.Dispatch(mutation, "delta_failed");

        Assert.AreSame(host.CurrentSnapshot, result.Snapshot);
        Assert.IsTrue(host.Invalidated);
        Assert.AreEqual("delta_failed", host.InvalidateReason);
        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(result.FullRebuilt);
    }

    [TestMethod]
    public void Dispatch_InvalidateTakesPriorityOverDeferAndDelta()
    {
        var host = new CapturingHost
        {
            DeltaApplies = true
        };
        var dispatcher = new ResourceHealthIndexMutationDispatcher(host);
        var mutation = new ResourceHealthIndexMutation
        {
            Invalidate = true,
            Defer = true
        };
        mutation.UpdatedTargets.Add(CreateChart());

        ResourceHealthIndexDispatchResult result = dispatcher.Dispatch(mutation, "invalidate");

        Assert.AreSame(host.CurrentSnapshot, result.Snapshot);
        Assert.IsTrue(host.Invalidated);
        Assert.AreEqual("invalidate", host.InvalidateReason);
        Assert.IsFalse(result.Deferred);
        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(host.DeferredLogged);
        Assert.IsFalse(host.TryApplyDeltaCalled);
    }

    [TestMethod]
    public void Dispatch_DeferLogsAndMarksDeferred()
    {
        var host = new CapturingHost();
        var dispatcher = new ResourceHealthIndexMutationDispatcher(host);
        var mutation = new ResourceHealthIndexMutation
        {
            Defer = true
        };
        mutation.UpdatedTargets.Add(CreateChart());

        ResourceHealthIndexDispatchResult result = dispatcher.Dispatch(mutation, "deferred");

        Assert.AreSame(host.CurrentSnapshot, result.Snapshot);
        Assert.IsTrue(result.Deferred);
        Assert.IsTrue(host.DeferredLogged);
        Assert.AreEqual("deferred", host.DeferredReason);
        Assert.AreEqual(1, host.DeferredUpdateTargetCount);
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

    private sealed class CapturingHost : IResourceHealthIndexMutationDispatchHost
    {
        public ResourceHealthIndexSnapshot CurrentSnapshot { get; } = ResourceHealthIndexSnapshot.Build([], null, 101);

        public ResourceHealthIndexSnapshot DeltaSnapshot { get; } = ResourceHealthIndexSnapshot.Build([], null, 102);

        public ResourceHealthIndexSnapshot RebuildSnapshot { get; } = ResourceHealthIndexSnapshot.Build([], null, 103);

        public bool DeltaApplies { get; set; }

        public bool RebuildStaleFullOwnedTarget { get; set; }

        public bool Invalidated { get; private set; }

        public string InvalidateReason { get; private set; } = string.Empty;

        public bool DeferredLogged { get; private set; }

        public string DeferredReason { get; private set; } = string.Empty;

        public int DeferredUpdateTargetCount { get; private set; }

        public bool TryApplyDeltaCalled { get; private set; }

        public bool RebuildCalled { get; private set; }

        public string RebuildReason { get; private set; } = string.Empty;

        public ResourceMaintenanceTargetSet RebuildTargetSet { get; private set; }

        public ResourceHealthIndexSnapshot GetPublishedResourceHealthIndexSnapshotOrEmpty()
        {
            return CurrentSnapshot;
        }

        public void InvalidateResourceHealthIndex(string reason)
        {
            Invalidated = true;
            InvalidateReason = reason;
        }

        public void LogResourceHealthIndexDeferred(
            string reason,
            ResourceHealthIndexSnapshot snapshot,
            int updateTargetCount)
        {
            DeferredLogged = true;
            DeferredReason = reason;
            DeferredUpdateTargetCount = updateTargetCount;
        }

        public bool TryApplyResourceHealthIndexDelta(
            string reason,
            IEnumerable<ChartFile> updatedTargets,
            IEnumerable<ChartFile> removedTargets,
            int? deltaBaseResourceHealthInputVersion,
            int? deltaTargetResourceHealthInputVersion,
            out ResourceHealthIndexSnapshot snapshot)
        {
            TryApplyDeltaCalled = true;
            snapshot = DeltaSnapshot;
            return DeltaApplies;
        }

        public ResourceHealthIndexSnapshot RebuildResourceHealthIndexSnapshot(
            string reason,
            ResourceMaintenanceTargetSet fullOwnedTargetSet,
            out bool staleFullOwnedTarget)
        {
            RebuildCalled = true;
            RebuildReason = reason;
            RebuildTargetSet = fullOwnedTargetSet;
            staleFullOwnedTarget = RebuildStaleFullOwnedTarget;
            return RebuildStaleFullOwnedTarget ? CurrentSnapshot : RebuildSnapshot;
        }
    }
}
