using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public class ResourceHealthIndexOwnerTests
{
    [TestMethod]
    public void EnsureCurrent_StaleFullOwnedTargetDoesNotPublish()
    {
        ChartFile chart = CreateChart();
        var currentVersion = new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(2, 2),
            ownedCollectionVersion: 2,
            inputVersion: 0);
        ResourceMaintenanceTargetSet staleTarget = ResourceMaintenanceTargetSet.ForFullOwned(
            [chart],
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 1,
            resourceHealthInputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);

        ResourceHealthIndexSnapshot snapshot = owner.EnsureCurrent(
            "stale",
            staleTarget,
            currentVersion);

        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, snapshot);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, owner.TryGetCurrentSnapshot());
        Assert.IsFalse(owner.IsCurrent());
    }

    [TestMethod]
    public void EnsureCurrent_RechecksVersionAfterBuildBeforePublishing()
    {
        ChartFile chart = CreateChart();
        var initialVersion = new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 1,
            inputVersion: 0);
        var changedVersion = new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(2, 1),
            ownedCollectionVersion: 1,
            inputVersion: 0);
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = new(
            new BmsLibraryMaintenanceService(),
            _ => { },
            () => changedVersion);

        ResourceHealthIndexSnapshot snapshot = owner.EnsureCurrent(
            "publish_race",
            target,
            initialVersion);

        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, snapshot);
        Assert.IsFalse(owner.IsCurrent());
    }

    [TestMethod]
    public void EnsureCurrent_StalePublishDoesNotReturnPreviousSnapshot()
    {
        ChartFile chart = CreateChart();
        var initialVersion = new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 1,
            inputVersion: 0);
        ResourceHealthIndexCurrentVersion currentVersion = initialVersion;
        ResourceHealthIndexOwner owner = new(
            new BmsLibraryMaintenanceService(),
            _ => { },
            () => currentVersion);
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent("initial", target, initialVersion);
        currentVersion = new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(2, 1),
            ownedCollectionVersion: 1,
            inputVersion: 0);
        Assert.IsFalse(owner.IsCurrent());

        ResourceHealthIndexSnapshot stale = owner.EnsureCurrent("stale", target, initialVersion);

        Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, initial);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, stale);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, owner.TryGetCurrentSnapshot());
    }

    [TestMethod]
    public void Apply_DeltaRequiresMatchingLeaseVersionsAndPublishesNewSnapshot()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        ResourceHealthIndexOwner.ResourceHealthInputMutation lease = owner.BeginInputMutation();
        lease.Dispose();
        var mutation = new ResourceHealthIndexMutation();
        mutation.UpdatedTargets.Add(chart);
        mutation.DeltaBaseResourceHealthInputVersion = lease.BaseInputVersion;
        mutation.DeltaTargetResourceHealthInputVersion = lease.TargetInputVersion;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "delta",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 2));

        Assert.IsTrue(result.DeltaApplied);
        Assert.IsFalse(result.FullRebuilt);
        Assert.AreNotSame(initial, result.Snapshot);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    [TestMethod]
    public void Apply_InvalidateDoesNotReturnPreviousSnapshot()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        var currentVersion = new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 1,
            inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent("initial", target, currentVersion);
        var mutation = new ResourceHealthIndexMutation
        {
            Invalidate = true
        };

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "invalidate",
            currentVersion);

        Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, initial);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, result.Snapshot);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, owner.TryGetCurrentSnapshot());
        Assert.IsFalse(owner.IsCurrent());
    }

    [TestMethod]
    public void RebaseAfterInputMutation_PreservesCurrentSnapshotWhenNoResourceChangeOccurred()
    {
        ChartFile chart = CreateChart();
        var currentVersion = new ResourceHealthIndexCurrentVersion(
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 1,
            inputVersion: 0);
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent("initial", target, currentVersion);

        ResourceHealthIndexOwner.ResourceHealthInputMutation mutation = owner.BeginInputMutation();
        Assert.IsFalse(owner.IsCurrent());
        mutation.Dispose();

        owner.RebaseAfterInputMutation(
            mutation,
            new ResourceHealthIndexCurrentVersion(
                currentVersion.StorageRowsVersion,
                currentVersion.OwnedCollectionVersion,
                mutation.TargetInputVersion));

        Assert.IsTrue(owner.IsCurrent());
        Assert.AreSame(initial, owner.TryGetCurrentSnapshot());
    }

    [TestMethod]
    public void Apply_InvalidateWinsOverDeferAndDelta()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        var mutation = new ResourceHealthIndexMutation
        {
            Invalidate = true,
            Defer = true
        };
        mutation.UpdatedTargets.Add(chart);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "invalidate",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));

        Assert.IsFalse(result.Deferred);
        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(owner.IsCurrent());
    }

    [TestMethod]
    public void Apply_DeltaFailureInvalidatesWithoutHiddenRebuild()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        var mutation = new ResourceHealthIndexMutation
        {
            InvalidateIfDeltaFails = true
        };
        mutation.UpdatedTargets.Add(chart);
        mutation.DeltaBaseResourceHealthInputVersion = 0;
        mutation.DeltaTargetResourceHealthInputVersion = 4;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "delta_failed",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 4));

        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(result.FullRebuilt);
        Assert.IsFalse(owner.IsCurrent());
    }

    [TestMethod]
    public void Apply_DeltaFailureWithoutInvalidationFallsBackToFullRebuild()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        var mutation = new ResourceHealthIndexMutation
        {
            FullOwnedTargetSet = target,
            DeltaBaseResourceHealthInputVersion = 0,
            DeltaTargetResourceHealthInputVersion = 4
        };
        mutation.UpdatedTargets.Add(chart);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "delta_fallback",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));

        Assert.IsTrue(result.FullRebuilt);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    [TestMethod]
    public void Apply_FullRebuildAlwaysRebuildsEvenWhenSnapshotIsCurrent()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        var mutation = new ResourceHealthIndexMutation
        {
            RebuildFull = true,
            FullOwnedTargetSet = target
        };

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "full",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));

        Assert.IsTrue(result.FullRebuilt);
        Assert.AreNotSame(initial, result.Snapshot);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    [TestMethod]
    public void Apply_DeferLeavesCurrentSnapshotUntouched()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        var mutation = new ResourceHealthIndexMutation
        {
            Defer = true
        };
        mutation.UpdatedTargets.Add(chart);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "defer",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));

        Assert.IsTrue(result.Deferred);
        Assert.AreSame(initial, result.Snapshot);
        Assert.AreSame(initial, owner.TryGetCurrentSnapshot());
    }

    [TestMethod]
    public void InputMutation_NestedScopesPreserveOuterVersionWindow()
    {
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));

        ResourceHealthIndexOwner.ResourceHealthInputMutation outer = owner.BeginInputMutation();
        ResourceHealthIndexOwner.ResourceHealthInputMutation inner = owner.BeginInputMutation();
        outer.Dispose();
        Assert.AreEqual(1, outer.TargetInputVersion);
        inner.Dispose();

        Assert.AreEqual(2, inner.TargetInputVersion);
        Assert.AreEqual(outer.BaseInputVersion + 1, inner.BaseInputVersion);
        owner.ForceInvalidate("nested_complete");
        Assert.IsFalse(owner.IsCurrent());
    }

    [TestMethod]
    public void Projection_ReturnsEmptyAfterInvalidation()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));
        ResourceHealthIndexSnapshot snapshot = owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0));

        ResourceHealthWarningProjection projection = owner.TryGetCurrentProjection(chart);

        Assert.AreSame(ResourceHealthWarningProjection.Empty, projection);
        Assert.AreEqual(1, snapshot.TargetCount);
        owner.ForceInvalidate("failure");
        Assert.AreSame(ResourceHealthWarningProjection.Empty, owner.TryGetCurrentProjection(chart));
    }

    [TestMethod]
    public void TargetSet_ExposesReadOnlyChartSnapshot()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        IList<ChartFile> exposed = (IList<ChartFile>)target.Charts;

        Assert.ThrowsException<NotSupportedException>(() => exposed.Add(CreateChart()));
        Assert.AreEqual(1, target.Count);
    }

    private static ResourceHealthIndexOwner CreateOwner(ResourceHealthIndexCurrentVersion currentVersion)
    {
        ResourceHealthIndexOwner owner = null!;
        owner = new ResourceHealthIndexOwner(
            new BmsLibraryMaintenanceService(),
            _ => { },
            () => new ResourceHealthIndexCurrentVersion(
                currentVersion.StorageRowsVersion,
                currentVersion.OwnedCollectionVersion,
                owner.CurrentInputVersion));
        return owner;
    }

    private static ResourceMaintenanceTargetSet CreateTarget(ChartFile chart, int inputVersion)
    {
        return ResourceMaintenanceTargetSet.ForFullOwned(
            [chart],
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 1,
            inputVersion);
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
}
