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
        var staleTarget = ResourceMaintenanceTargetSet.ForFullOwned(
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

    /// <summary>R1-DELTA: 差分成功時は全件入力を取得せず、新しい対象を公開する。</summary>
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
        mutation.UpdatedTargets.Add(CreateChart(@"C:\charts\second.bms"));
        mutation.DeltaBaseResourceHealthInputVersion = lease.BaseInputVersion;
        mutation.DeltaTargetResourceHealthInputVersion = lease.TargetInputVersion;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "delta",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 2),
            RejectFullOwnedTarget);

        Assert.IsTrue(result.DeltaApplied);
        Assert.IsFalse(result.FullRebuilt);
        Assert.AreEqual(2, result.Snapshot.TargetCount);
        Assert.AreNotSame(initial, result.Snapshot);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    /// <summary>R1-SKIP: 失効時は全件取得せず、古い表示を返さない。</summary>
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
            currentVersion,
            RejectFullOwnedTarget);

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

    /// <summary>R1-SKIP: 失効は延期・全件再構築・差分に優先し、不要な取得を行わない。</summary>
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
            Defer = true,
            RebuildFull = true
        };
        mutation.UpdatedTargets.Add(chart);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "invalidate",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0),
            RejectFullOwnedTarget);

        Assert.IsFalse(result.Deferred);
        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(owner.IsCurrent());
    }

    /// <summary>R1-SKIP: 失敗時失効を指定した差分は、全件入力を先行取得しない。</summary>
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
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 4),
            RejectFullOwnedTarget);

        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(result.FullRebuilt);
        Assert.IsFalse(owner.IsCurrent());
    }

    /// <summary>R1-FULL: 必要な fallback だけが全件入力を取得し、指定済み入力は再利用する。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_DeltaFailureWithoutInvalidationFallsBackToFullRebuild(bool inputAlreadySupplied)
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
            FullOwnedTargetSet = inputAlreadySupplied ? target : default,
            DeltaBaseResourceHealthInputVersion = 0,
            DeltaTargetResourceHealthInputVersion = 4
        };
        mutation.UpdatedTargets.Add(chart);
        int captureCount = 0;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "delta_fallback",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0),
            _ =>
            {
                captureCount++;
                return target;
            });

        Assert.AreEqual(inputAlreadySupplied ? 0 : 1, captureCount);
        Assert.IsTrue(result.FullRebuilt);
        Assert.AreEqual(1, result.Snapshot.TargetCount);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    /// <summary>R1-FULL: 明示再構築を保ち、全件入力は不足する場合だけ一回取得する。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_FullRebuildAlwaysRebuildsEvenWhenSnapshotIsCurrent(bool inputAlreadySupplied)
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
            FullOwnedTargetSet = inputAlreadySupplied ? target : default
        };
        int captureCount = 0;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "full",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0),
            _ =>
            {
                captureCount++;
                return target;
            });

        Assert.AreEqual(inputAlreadySupplied ? 0 : 1, captureCount);
        Assert.IsTrue(result.FullRebuilt);
        Assert.AreEqual(1, result.Snapshot.TargetCount);
        Assert.AreNotSame(initial, result.Snapshot);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    /// <summary>R1-SKIP: 少数の差分を伴う延期は全件入力を取得せず、確定済み表示を保つ。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_DeferLeavesCurrentSnapshotUntouched(bool rebuildFull)
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
            Defer = true,
            RebuildFull = rebuildFull
        };
        mutation.UpdatedTargets.Add(chart);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "defer",
            new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0),
            RejectFullOwnedTarget);

        Assert.IsTrue(result.Deferred);
        Assert.AreSame(initial, result.Snapshot);
        Assert.AreSame(initial, owner.TryGetCurrentSnapshot());
    }

    /// <summary>R1-SKIP: 変更のない dispatch は全件取得も失効も行わない。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_NoChangesLeavesCurrentSnapshotUntouched(bool nullMutation)
    {
        var currentVersion = new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent("initial", CreateTarget(CreateChart(), 0), currentVersion);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            nullMutation ? null! : new ResourceHealthIndexMutation().ToFacts(),
            "no_change",
            currentVersion,
            RejectFullOwnedTarget);

        Assert.AreSame(initial, result.Snapshot);
        Assert.AreSame(initial, owner.TryGetCurrentSnapshot());
        Assert.AreEqual(0, owner.CurrentInputVersion);
        Assert.IsFalse(result.Deferred || result.DeltaApplied || result.FullRebuilt);
    }

    /// <summary>R1-FULL: 入力取得時に collection を構築しても、取得後の版で正しく公開する。</summary>
    [TestMethod]
    public void Apply_FullRebuildReadsVersionAfterCapturingInput()
    {
        var currentVersion = new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0);
        ResourceHealthIndexOwner owner = new(new BmsLibraryMaintenanceService(), _ => { }, () => currentVersion);
        int captureCount = 0;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            new ResourceHealthIndexMutation { RebuildFull = true }.ToFacts(),
            "build_collection",
            currentVersion,
            _ =>
            {
                captureCount++;
                currentVersion = new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 2, 0);
                return ResourceMaintenanceTargetSet.ForFullOwned(
                    [CreateChart(), CreateChart(@"C:\charts\second.bms")],
                    new StorageRowsVersionSnapshot(1, 1), 2, 0);
            });

        Assert.AreEqual(1, captureCount);
        Assert.IsTrue(result.FullRebuilt);
        Assert.AreEqual(2, result.Snapshot.TargetCount);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    /// <summary>R1-FULL: 指定済みの古い入力を再取得で隠さず、公開を拒否する。</summary>
    [TestMethod]
    public void Apply_StaleSuppliedFullInputIsNotRecaptured()
    {
        var currentVersion = new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(2, 1), 2, 0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);
        var mutation = new ResourceHealthIndexMutation
        {
            RebuildFull = true,
            FullOwnedTargetSet = CreateTarget(CreateChart(), 0)
        };

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(), "stale_full", currentVersion, RejectFullOwnedTarget);

        Assert.IsFalse(result.FullRebuilt);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, result.Snapshot);
        Assert.IsFalse(owner.IsCurrent());
    }

    /// <summary>R1-FULL: 取得中の writer 開始・完了によって古くなった入力を公開しない。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_FullInputOverlappingWriterIsNotPublished(bool writerCompletes)
    {
        var currentVersion = new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);
        ResourceHealthIndexOwner.ResourceHealthInputMutation? writer = null;
        int captureCount = 0;
        try
        {
            ResourceHealthIndexDispatchResult result = owner.Apply(
                new ResourceHealthIndexMutation { RebuildFull = true }.ToFacts(),
                "writer_during_capture",
                currentVersion,
                _ =>
                {
                    captureCount++;
                    ResourceMaintenanceTargetSet captured = CreateTarget(CreateChart(), owner.CurrentInputVersion);
                    writer = owner.BeginInputMutation();
                    if (writerCompletes)
                    {
                        writer.Dispose();
                    }
                    return captured;
                });

            Assert.AreEqual(1, captureCount);
            Assert.IsFalse(result.FullRebuilt);
            Assert.AreSame(ResourceHealthIndexSnapshot.Empty, result.Snapshot);
            Assert.IsFalse(owner.IsCurrent());
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>R1-FULL: 遅延取得した入力も storage owner から切り離し、公開済み表示を保つ。</summary>
    [TestMethod]
    public void Apply_LazyFullInputDetachesMaintenanceFromStorageOwner()
    {
        var file = new BMSFile { path = @"C:\charts\missing.bms", hash = new string('a', 32) };
        var maintenance = new BMSFileMaintenanceInfo
        {
            path = file.path,
            hash = file.hash,
            wav_files_defined = 1,
            wav_files_existing = 0
        };
        file.SetMaintenanceInfo(maintenance, suppressPropertyChanged: true);
        ChartFile chart = ChartFileProjection.FromBmsFile(
            file, includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false);
        var currentVersion = new ResourceHealthIndexCurrentVersion(new StorageRowsVersionSnapshot(1, 1), 1, 0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            new ResourceHealthIndexMutation { RebuildFull = true }.ToFacts(),
            "immutable_full", currentVersion, _ => CreateTarget(chart, 0));
        maintenance.wav_files_existing = 1;
        maintenance.is_files_warning_ignored = true;

        Assert.IsTrue(result.FullRebuilt);
        ChartFile published = result.Snapshot.ActiveTargets.Single();
        Assert.IsNull(published.GetBmsStorageOwner());
        Assert.IsNotNull(published.ResourceHealthMaintenanceSnapshot);
        Assert.AreEqual(0, published.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
        Assert.IsFalse(published.ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
        Assert.IsTrue(result.Snapshot.GetProjection(published).Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
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
        var exposed = (IList<ChartFile>)target.Charts;

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

    private static ResourceMaintenanceTargetSet RejectFullOwnedTarget(string reason)
    {
        throw new AssertFailedException("不要な全 owned 入力が取得された: " + reason);
    }

    private static ChartFile CreateChart(string path = @"C:\charts\sample.bms")
    {
        return new ChartFile(
            kind: ChartFileKind.Bms,
            path: path,
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
