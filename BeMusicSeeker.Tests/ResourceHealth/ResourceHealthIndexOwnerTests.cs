using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public class ResourceHealthIndexOwnerTests
{
    /// <summary>資源deltaの実適用中に無関係なTitleが確定しても、正常なdeltaを破棄せず全資源入力を再取得しない。</summary>
    [TestMethod]
    public void Apply_TitleCommitDuringDeltaDoesNotInvalidateResourceFacts()
    {
        var catalog = new CatalogOwnedCollectionOwner();
        catalog.ReplaceCharts([CreateChart() with { Token = new OwnedChartToken() }], []);
        ChartFile original = catalog.BmsRows[0];
        ResourceHealthIndexOwner resource = CreateOwner(new(0));
        ResourceHealthIndexSnapshot initial = resource.EnsureCurrent("initial",
            ResourceMaintenanceTargetSet.ForFullOwned([original], 0), new(0));
        ResourceHealthIndexOwner.ResourceHealthInputMutation lease = resource.BeginInputMutation();
        lease.Dispose();
        ChartFile updated = ChartFileProjection.WithMaintenance(original, new ResourceHealthMaintenanceSnapshot
        {
            Path = original.Path,
            Hash = original.Md5,
            Origin = MaintenanceInfoOrigin.Calculated,
            WavFilesDefined = 2,
            WavFilesExisting = 1
        });
        bool titleCommitted = false;
        initial.StoreWorkObserver = operation =>
        {
            if (titleCommitted || operation != "entry_lookup")
            {
                return;
            }

            titleCommitted = true;
            catalog.CommitCurrentValues([original], (current, _) => current with { Title = "latest title" });
        };
        try
        {
            var mutation = new ResourceHealthIndexMutation
            {
                DeltaBaseResourceHealthInputVersion = lease.BaseInputVersion,
                DeltaTargetResourceHealthInputVersion = lease.TargetInputVersion,
                InvalidateIfDeltaFails = true
            };
            mutation.UpdatedTargets.Add(updated);
            ResourceHealthIndexDispatchResult result = resource.Apply(mutation.ToFacts(), "title_during_delta",
                new(resource.CurrentInputVersion), RejectFullOwnedTarget);
            Assert.IsTrue(titleCommitted);
            Assert.IsTrue(result.DeltaApplied);
            Assert.IsFalse(result.FullRebuilt);
            Assert.IsTrue(resource.IsCurrent());
            Assert.IsTrue(result.Snapshot.GetProjection(updated).HasIssues);
            Assert.AreEqual("latest title", catalog.CaptureReadSnapshot([LibraryChartRef.FromChartFile(original)]).Resolve(LibraryChartRef.FromChartFile(original)).Title);
            Assert.AreEqual("Title", original.Title);
        }
        finally { initial.StoreWorkObserver = null; }
    }

    /// <summary>
    /// 全所有譜面の入力が現在の保存世代より古いとき、構築結果を公開せず空の非最新状態を維持する。
    /// </summary>
    [TestMethod]
    public void EnsureCurrent_StaleFullOwnedTargetDoesNotPublish()
    {
        ChartFile chart = CreateChart();
        var currentVersion = new ResourceHealthIndexCurrentVersion(
            inputVersion: 0);
        var staleTarget = ResourceMaintenanceTargetSet.ForFullOwned(
            [chart],
            resourceHealthInputVersion: -2);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);

        ResourceHealthIndexSnapshot snapshot = owner.EnsureCurrent(
            "stale",
            staleTarget,
            currentVersion);

        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, snapshot);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, owner.TryGetCurrentSnapshot());
        Assert.IsFalse(owner.IsCurrent());
    }

    /// <summary>
    /// 構築開始後に保存世代が変わるとき、公開直前にも世代を確認して古い結果を拒否する。
    /// </summary>
    [TestMethod]
    public void EnsureCurrent_RechecksVersionAfterBuildBeforePublishing()
    {
        ChartFile chart = CreateChart();
        var initialVersion = new ResourceHealthIndexCurrentVersion(
            inputVersion: 0);
        var changedVersion = new ResourceHealthIndexCurrentVersion(
            inputVersion: 2);
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

    /// <summary>
    /// 一度公開した後で保存世代が変わるとき、古い入力の再構築から以前の表示を返さない。
    /// </summary>
    [TestMethod]
    public void EnsureCurrent_StalePublishDoesNotReturnPreviousSnapshot()
    {
        ChartFile chart = CreateChart();
        var initialVersion = new ResourceHealthIndexCurrentVersion(
            inputVersion: 0);
        ResourceHealthIndexCurrentVersion currentVersion = initialVersion;
        ResourceHealthIndexOwner owner = new(
            new BmsLibraryMaintenanceService(),
            _ => { },
            () => currentVersion);
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent("initial", target, initialVersion);
        currentVersion = new ResourceHealthIndexCurrentVersion(
            inputVersion: 2);
        Assert.IsFalse(owner.IsCurrent());

        ResourceHealthIndexSnapshot stale = owner.EnsureCurrent("stale", target, initialVersion);

        Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, initial);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, stale);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, owner.TryGetCurrentSnapshot());
    }

    /// <summary>
    /// 入力変更の貸与した基点と終点に合う差分は全件取得をせず追加対象を含む新しい表示を公開する。
    /// </summary>
    [TestMethod]
    public void Apply_DeltaRequiresMatchingLeaseVersionsAndPublishesNewSnapshot()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(0));
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(0));
        ResourceHealthIndexOwner.ResourceHealthInputMutation lease = owner.BeginInputMutation();
        lease.Dispose();
        var mutation = new ResourceHealthIndexMutation();
        mutation.UpdatedTargets.Add(CreateChart(@"C:\charts\second.bms"));
        mutation.DeltaBaseResourceHealthInputVersion = lease.BaseInputVersion;
        mutation.DeltaTargetResourceHealthInputVersion = lease.TargetInputVersion;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "delta",
            new ResourceHealthIndexCurrentVersion(2),
            RejectFullOwnedTarget);

        Assert.IsTrue(result.DeltaApplied);
        Assert.IsFalse(result.FullRebuilt);
        Assert.AreEqual(2, result.Snapshot.TargetCount);
        Assert.AreNotSame(initial, result.Snapshot);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    /// <summary>
    /// 明示失効を受けたとき、全件取得をせず以前の表示を返却・保持しない。
    /// </summary>
    [TestMethod]
    public void Apply_InvalidateDoesNotReturnPreviousSnapshot()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        var currentVersion = new ResourceHealthIndexCurrentVersion(
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

    /// <summary>
    /// 入力変更中は表示を非最新とし、リソースの変更がない完了では既存表示を再利用して最新に戻す。
    /// </summary>
    [TestMethod]
    public void RebaseAfterInputMutation_PreservesCurrentSnapshotWhenNoResourceChangeOccurred()
    {
        ChartFile chart = CreateChart();
        var currentVersion = new ResourceHealthIndexCurrentVersion(
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
                mutation.TargetInputVersion));

        Assert.IsTrue(owner.IsCurrent());
        Assert.AreSame(initial, owner.TryGetCurrentSnapshot());
    }

    /// <summary>
    /// 失効・延期・再構築・差分が同時指定されても失効を優先し、全件取得や差分適用で覆さない。
    /// </summary>
    [TestMethod]
    public void Apply_InvalidateWinsOverDeferAndDelta()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(0));
        owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(0));
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
            new ResourceHealthIndexCurrentVersion(0),
            RejectFullOwnedTarget);

        Assert.IsFalse(result.Deferred);
        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(owner.IsCurrent());
    }

    /// <summary>
    /// 差分の世代が合わず失効指定があるとき、全件再構築へ隠れて切り替えず表示を失効させる。
    /// </summary>
    [TestMethod]
    public void Apply_DeltaFailureInvalidatesWithoutHiddenRebuild()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(0));
        owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(0));
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
            new ResourceHealthIndexCurrentVersion(4),
            RejectFullOwnedTarget);

        Assert.IsFalse(result.DeltaApplied);
        Assert.IsFalse(result.FullRebuilt);
        Assert.IsFalse(owner.IsCurrent());
    }

    /// <summary>
    /// 失効指定のない不成立差分は全件再構築し、入力の事前指定あり・なしに応じて取得回数を零・一回に保つ。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_DeltaFailureWithoutInvalidationFallsBackToFullRebuild(bool inputAlreadySupplied)
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(0));
        owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(0));
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
            new ResourceHealthIndexCurrentVersion(0),
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

    /// <summary>
    /// 現在の表示が最新でも明示再構築は新しい表示を作り、指定済み入力の再取得を避ける。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_FullRebuildAlwaysRebuildsEvenWhenSnapshotIsCurrent(bool inputAlreadySupplied)
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(0));
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(0));
        var mutation = new ResourceHealthIndexMutation
        {
            RebuildFull = true,
            FullOwnedTargetSet = inputAlreadySupplied ? target : default
        };
        int captureCount = 0;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "full",
            new ResourceHealthIndexCurrentVersion(0),
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

    /// <summary>
    /// 再構築指定の有無にかかわらず延期された差分では、全件取得をせず確定済み表示を保つ。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_DeferLeavesCurrentSnapshotUntouched(bool rebuildFull)
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(0));
        ResourceHealthIndexSnapshot initial = owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(0));
        var mutation = new ResourceHealthIndexMutation
        {
            Defer = true,
            RebuildFull = rebuildFull
        };
        mutation.UpdatedTargets.Add(chart);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(),
            "defer",
            new ResourceHealthIndexCurrentVersion(0),
            RejectFullOwnedTarget);

        Assert.IsTrue(result.Deferred);
        Assert.AreSame(initial, result.Snapshot);
        Assert.AreSame(initial, owner.TryGetCurrentSnapshot());
    }

    /// <summary>
    /// 変更要求がnullまたは空なら、全件取得・失効・入力世代変更をせず現在の表示を保つ。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_NoChangesLeavesCurrentSnapshotUntouched(bool nullMutation)
    {
        var currentVersion = new ResourceHealthIndexCurrentVersion(0);
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

    /// <summary>
    /// 全件入力の取得中に所有集合が構築されたとき、取得後の集合世代で対象全体を公開する。
    /// </summary>
    [TestMethod]
    public void Apply_FullRebuildReadsVersionAfterCapturingInput()
    {
        var currentVersion = new ResourceHealthIndexCurrentVersion(0);
        ResourceHealthIndexOwner owner = new(new BmsLibraryMaintenanceService(), _ => { }, () => currentVersion);
        int captureCount = 0;

        ResourceHealthIndexDispatchResult result = owner.Apply(
            new ResourceHealthIndexMutation { RebuildFull = true }.ToFacts(),
            "build_collection",
            currentVersion,
            _ =>
            {
                captureCount++;
                currentVersion = new ResourceHealthIndexCurrentVersion(0);
                return ResourceMaintenanceTargetSet.ForFullOwned(
                    [CreateChart(), CreateChart(@"C:\charts\second.bms")], 0);
            });

        Assert.AreEqual(1, captureCount);
        Assert.IsTrue(result.FullRebuilt);
        Assert.AreEqual(2, result.Snapshot.TargetCount);
        Assert.AreSame(result.Snapshot, owner.TryGetCurrentSnapshot());
    }

    /// <summary>
    /// 事前指定された全件入力が古いとき、取得し直して失敗を隠さず公開を拒否する。
    /// </summary>
    [TestMethod]
    public void Apply_StaleSuppliedFullInputIsNotRecaptured()
    {
        var currentVersion = new ResourceHealthIndexCurrentVersion(0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);
        var mutation = new ResourceHealthIndexMutation
        {
            RebuildFull = true,
            FullOwnedTargetSet = CreateTarget(CreateChart(), -2)
        };

        ResourceHealthIndexDispatchResult result = owner.Apply(
            mutation.ToFacts(), "stale_full", currentVersion, RejectFullOwnedTarget);

        Assert.IsFalse(result.FullRebuilt);
        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, result.Snapshot);
        Assert.IsFalse(owner.IsCurrent());
    }

    /// <summary>
    /// 全件入力の取得中に書込みが開始・完了した両条件で、取得済みの古い入力を公開しない。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Apply_FullInputOverlappingWriterIsNotPublished(bool writerCompletes)
    {
        var currentVersion = new ResourceHealthIndexCurrentVersion(0);
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

    /// <summary>
    /// 遅延取得した保守情報の元データを公開後に変更しても、公開済みの欠落警告と抑止設定を変えない。
    /// </summary>
    [TestMethod]
    public void Apply_LazyFullInputDetachesMaintenanceFromStorageOwner()
    {
        ChartFile file = (ChartTestValues.Empty() with { Path = @"C:\charts\missing.bms", Md5 = new string('a', 32) });
        var maintenance = new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, wav_files_defined = 1, wav_files_existing = 0 };
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(maintenance));
        ChartFile chart = (file);
        var currentVersion = new ResourceHealthIndexCurrentVersion(0);
        ResourceHealthIndexOwner owner = CreateOwner(currentVersion);

        ResourceHealthIndexDispatchResult result = owner.Apply(
            new ResourceHealthIndexMutation { RebuildFull = true }.ToFacts(),
            "immutable_full", currentVersion, _ => CreateTarget(chart, 0));
        maintenance.wav_files_existing = 1;
        maintenance.is_files_warning_ignored = true;

        Assert.IsTrue(result.FullRebuilt);
        LibraryChartRef published = result.Snapshot.ActiveTargets.Single();
        Assert.IsNull(published.Token);
        Assert.AreEqual(file.Path, published.Path);
        Assert.AreEqual(file.Md5, published.Md5);
        Assert.IsFalse(result.Snapshot.GetProjection(published).IsIgnored);
        Assert.IsTrue(result.Snapshot.GetProjection(published).Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
    }

    /// <summary>
    /// 入れ子の入力変更を外側から解放しても、最後の内側解放まで同じ変更区間を保ち終了世代を一度だけ進める。
    /// </summary>
    [TestMethod]
    public void InputMutation_NestedScopesPreserveOuterVersionWindow()
    {
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(0));

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

    /// <summary>
    /// 警告を持たない譜面は現在の索引でも失効後でも空の警告射影を返す。
    /// </summary>
    [TestMethod]
    public void Projection_ReturnsEmptyAfterInvalidation()
    {
        ChartFile chart = CreateChart();
        ResourceMaintenanceTargetSet target = CreateTarget(chart, inputVersion: 0);
        ResourceHealthIndexOwner owner = CreateOwner(
            new ResourceHealthIndexCurrentVersion(0));
        ResourceHealthIndexSnapshot snapshot = owner.EnsureCurrent(
            "initial",
            target,
            new ResourceHealthIndexCurrentVersion(0));

        ResourceHealthWarningProjection projection = owner.TryGetCurrentProjection(chart);

        Assert.AreSame(ResourceHealthWarningProjection.Empty, projection);
        Assert.AreEqual(1, snapshot.TargetCount);
        owner.ForceInvalidate("failure");
        Assert.AreSame(ResourceHealthWarningProjection.Empty, owner.TryGetCurrentProjection(chart));
    }

    /// <summary>
    /// 対象集合の公開リストへの追加を拒否し、捕捉済みの譜面数を外から変更させない。
    /// </summary>
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
                owner.CurrentInputVersion));
        return owner;
    }

    private static ResourceMaintenanceTargetSet CreateTarget(ChartFile chart, int inputVersion)
    {
        return ResourceMaintenanceTargetSet.ForFullOwned(
            [chart],
            inputVersion);
    }

    private static ResourceMaintenanceTargetSet RejectFullOwnedTarget(string reason)
    {
        throw new AssertFailedException("不要な全 owned 入力が取得された: " + reason);
    }

    private static ChartFile CreateChart(string path = @"C:\charts\sample.bms")
    {
        return new ChartFile(kind: ChartFileKind.Bms, path: path, md5: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sha256: new string('b', 64), title: "Title", rawTitle: "Title", artist: "Artist", genre: string.Empty, folder: "charts", tag: string.Empty, levelText: string.Empty, level: null, mode: null, chartInfo: null);
    }
}
