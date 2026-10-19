using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CatalogMaintenanceCurrentValueTests
{
    /// <summary>health・encoding・検査由来・警告を含むbatchの実変更は一回だけ版を進め、同値・対象なしと旧snapshotを区別する。</summary>
    [TestMethod]
    public void CurrentCommit_AdvancesOnceForEveryCurrentValueChangeAndKeepsPriorCapture()
    {
        var publisher = new NormalLibraryRefreshPublisher();
        var owner = new CatalogOwnedCollectionOwner { RefreshPublisher = publisher };
        owner.ReplaceCharts([Chart(1), Chart(2)], []);
        int handled = publisher.Version;
        CatalogChartCollectionSnapshot before = owner.CaptureSnapshot();
        IReadOnlyDictionary<string, OwnedChartToken> bmsMembership = owner.Collection.CapturePathMembershipIndex();
        ChartFile first = before.BmsRows[0], second = before.BmsRows[1];
        CatalogCommitReceipt commit = owner.CommitCurrentValues([first, second], (current, _) => current with
        {
            EncodingName = "utf-16",
            WAVHealth = 50,
            BGAHealth = 0,
            MovieHealth = null,
            ResourceHealthMaintenanceSnapshot = new() { Path = current.Path, Hash = current.Md5, Origin = MaintenanceInfoOrigin.Calculated, Encoding = "utf-16" },
            Warnings = [ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, "warning")]
        });
        Assert.AreEqual(before.CatalogRevision, commit.BaseRevision);
        Assert.AreEqual(before.CatalogRevision + 1, commit.Revision);
        Assert.AreEqual(2, commit.Changes.Count);
        Assert.AreSame(bmsMembership, owner.Collection.CapturePathMembershipIndex());
        Assert.AreEqual(commit.Revision, commit.Snapshot.CatalogRevision);
        Assert.AreEqual(string.Empty, first.EncodingName);
        Assert.IsNull(first.WAVHealth);
        Assert.AreEqual(0, first.Warnings.Count);
        Assert.AreEqual("utf-16", commit.Snapshot.BmsRows[0].EncodingName);
        Assert.AreEqual(50, commit.Snapshot.BmsRows[0].WAVHealth);
        Assert.IsTrue(commit.Snapshot.TryGetChart(first.Token, out ChartFile captured, out int position));
        Assert.AreEqual(0, position);
        Assert.AreEqual("utf-16", captured.EncodingName);
        Assert.IsFalse(owner.CommitCurrentValues([captured], (current, _) => current with { Warnings = current.Warnings.ToArray() }).HasChanges);
        Assert.IsFalse(owner.CommitCurrentValues([Chart(3)], (current, _) => current with { EncodingName = "other" }).HasChanges);
        Assert.AreEqual(commit.Revision, owner.CatalogRevision);
        NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(handled);
        Assert.AreEqual(1, batch.CatalogCommits.Count);
        Assert.AreSame(commit, batch.CatalogCommits[0]);
    }

    /// <summary>未配送の二確定と削除再加入でも確定順の旧新値・版・旧キーを保持し、後続currentを先行結果へ混ぜない。</summary>
    [TestMethod]
    public void CommitQueue_PreservesBeforeAfterAndCommitOrderAcrossRetirement()
    {
        var publisher = new NormalLibraryRefreshPublisher();
        var owner = new CatalogOwnedCollectionOwner { RefreshPublisher = publisher };
        owner.ReplaceCharts([Chart(1)], []);
        int handled = publisher.Version;
        ChartFile original = owner.BmsRows[0];
        CatalogCommitReceipt first = owner.CommitCurrentValues([original], (current, _) => current with { Title = "one" });
        CatalogCommitReceipt second = owner.CommitCurrentValues([original], (current, _) => current with { Title = "two" });
        owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(original)], [], out _);
        owner.ApplyCommittedChartUpsert([Chart(1) with { Title = "replacement" }], out _);
        NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(handled);
        Assert.AreEqual(4, batch.CatalogCommits.Count);
        CollectionAssert.AreEqual(Enumerable.Range(first.BaseRevision, 4).ToArray(), batch.CatalogCommits.Select(commit => commit.BaseRevision).ToArray());
        Assert.AreEqual("original", first.Changes[0].Before.Title);
        Assert.AreEqual("one", first.Changes[0].After.Title);
        Assert.AreEqual("one", second.Changes[0].Before.Title);
        Assert.AreEqual("two", second.Changes[0].After.Title);
        Assert.AreEqual(original.Path, batch.CatalogCommits[2].Changes[0].Before.Path);
        Assert.IsNull(batch.CatalogCommits[2].Changes[0].After);
        Assert.IsNull(owner.CaptureReadSnapshot([LibraryChartRef.FromChartFile(original)]).Resolve(LibraryChartRef.FromChartFile(original)));
        Assert.IsTrue(first.Snapshot.TryGetChart(original.Token, out ChartFile firstValue, out _));
        Assert.AreEqual("one", firstValue.Title);
        Assert.AreNotSame(original.Token, owner.BmsRows[0].Token);
    }

    /// <summary>後続の候補変更が確定済みでも、receipt自身の候補と現在値を同じ版で局所解決し、旧rootを変更しない。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void ReceiptSnapshot_ResolvesCapturedCandidateAfterLaterCommit(int count)
    {
        var owner = new CatalogOwnedCollectionOwner();
        ChartFile first = Chart(1) with { Path = @"C:\Library\a.bms", Md5 = new string('a', 32) };
        ChartFile second = Chart(2) with { Path = @"C:\Library\b.bms", Md5 = first.Md5 };
        owner.ReplaceCharts(new[] { first, second }.Concat(Enumerable.Range(3, count - 2).Select(Chart)), []);
        var key = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, first.Md5.ToUpperInvariant());
        owner.CapturePlaylistReadSnapshot([key]);
        CatalogChartCollectionSnapshot initial = owner.CaptureSnapshot();
        var work = new List<string>();
        owner.StoreWorkObserver = work.Add;
        owner.PlaylistLibraryResolveIndexStoreWorkObserver = work.Add;
        CatalogCommitReceipt removal = owner.Commit(state => state.RemoveChartRequests([OwnedChartRemoveRequest.FromChart(first)]));
        CatalogCommitReceipt later = owner.CommitCurrentValues([second], (current, _) => current with { Md5 = new string('f', 32), Title = "later" });

        Assert.IsTrue(removal.Snapshot.TryCapturePlaylistReadSnapshot([key], out CatalogReadSnapshot captured));
        Assert.AreEqual(removal.Revision, captured.CatalogRevision);
        Assert.AreSame(second.Token, captured.Resolve(key).Token);
        Assert.AreEqual("original", captured.Resolve(key).Title);
        Assert.AreEqual(first.Md5, captured.Resolve(key).Md5);
        Assert.AreEqual(1, captured.Charts.Count);
        Assert.IsTrue(initial.TryCapturePlaylistReadSnapshot([key], out CatalogReadSnapshot before));
        Assert.AreSame(first.Token, before.Resolve(key).Token);
        Assert.IsTrue(later.Snapshot.TryCapturePlaylistReadSnapshot([key], out CatalogReadSnapshot after));
        Assert.IsNull(after.Resolve(key));
        Assert.IsFalse(work.Any(value => value.Contains("source_entry_visited", StringComparison.Ordinal) || value.Contains("full_root_enumeration", StringComparison.Ordinal)));

        owner.ReplaceCharts([Chart(4)], []);
        Assert.IsFalse(owner.CaptureSnapshot().TryCapturePlaylistReadSnapshot([key], out _));
        Assert.IsFalse(owner.IsPlaylistLibraryResolveIndexWarm());
        Assert.IsTrue(removal.Snapshot.TryCapturePlaylistReadSnapshot([key], out captured));
        Assert.AreSame(second.Token, captured.Resolve(key).Token);
    }

    /// <summary>補助通知の失敗時削除は確定receiptを削除せず、current確定後の元例外があっても同じ結果を配送できる。</summary>
    [TestMethod]
    public void CommitQueue_KeptAfterApplyFailureAndLegacyNotificationClear()
    {
        var publisher = new NormalLibraryRefreshPublisher();
        var owner = new CatalogOwnedCollectionOwner { RefreshPublisher = publisher };
        owner.ReplaceCharts([Chart(1)], []);
        int handled = publisher.Version;
        ChartFile original = owner.BmsRows[0];
        var failure = new InvalidOperationException("after current apply");
        InvalidOperationException actual = Assert.ThrowsException<InvalidOperationException>(() => owner.Commit(state =>
        {
            state.ApplyCurrentChartValue(original with { Title = "committed" });
            throw failure;
        }));
        Assert.AreSame(failure, actual);
        int commitNotification = publisher.Version;
        int legacyNotification = publisher.Publish(new NormalLibraryRefreshPublishRequest { Effects = LibraryChartRefreshEffects.WarningPresentationChanged });
        publisher.ClearLegacyNotification(legacyNotification);
        publisher.ClearLegacyNotification(commitNotification);
        NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(handled);
        Assert.AreEqual(1, batch.CatalogCommits.Count);
        CatalogCommitReceipt receipt = batch.CatalogCommits[0];
        Assert.AreEqual(owner.CatalogRevision, receipt.Revision);
        Assert.AreEqual("original", receipt.Changes[0].Before.Title);
        Assert.AreEqual("committed", receipt.Changes[0].After.Title);
        Assert.AreSame(owner.BmsRows[0], receipt.Snapshot.BmsRows[0]);
    }

    /// <summary>背景16/128件のwarmな一件保守変更は候補root・所持hash内容版を再構築せず、現在値取得数も対象数に限定する。</summary>
    [TestMethod]
    public void MaintenanceCommit_UpdatesOnlyTargetAndKeepsSpecializedDependencies()
    {
        foreach (int count in new[] { 16, 128 })
        {
            var owner = new CatalogOwnedCollectionOwner();
            owner.ReplaceCharts(Enumerable.Range(1, count).Select(Chart), []);
            PlaylistLibraryResolveIndexSnapshot candidates = owner.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out _, out _);
            OwnedChartHashIndexVersionedSnapshot hashes = owner.GetHashIndexSnapshot(CancellationToken.None, out _, out _);
            owner.CompleteChartInfoInputs([], full: true);
            ChartFile first = owner.BmsRows[0];
            var work = new List<string>();
            owner.StoreWorkObserver = work.Add;
            owner.PlaylistLibraryResolveIndexStoreWorkObserver = work.Add;
            owner.CommitCurrentValues([first], (current, _) => current with { WAVHealth = 0, EncodingName = "utf-8" });
            CatalogReadSnapshot read = owner.CapturePlaylistReadSnapshot([new(PlaylistEntryLookupKeyKind.Md5, first.Md5)]);
            PlaylistLibraryResolveIndexSnapshot afterCandidates = owner.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out bool cacheHit, out _);
            OwnedChartHashIndexVersionedSnapshot afterHashes = owner.GetHashIndexSnapshot(CancellationToken.None, out _, out _);
            Assert.IsTrue(cacheHit);
            Assert.AreEqual(candidates.Version, afterCandidates.Version);
            Assert.AreEqual(hashes.Version, afterHashes.Version);
            Assert.IsTrue(owner.TryGetOwnedHashContentVersion(out int contentVersion));
            Assert.AreEqual(hashes.Version, contentVersion);
            Assert.IsTrue(owner.TryCapturePendingChartInfoInputs(out IReadOnlyList<ChartFile>? pending));
            Assert.AreEqual(0, pending.Count);
            Assert.AreEqual(owner.CatalogRevision, read.CatalogRevision);
            Assert.AreEqual(0, read.Resolve(new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, first.Md5)).WAVHealth);
            Assert.AreEqual(1, work.Count(value => value == "catalog_current_reference_lookup"));
            Assert.IsFalse(work.Any(value => value.Contains("source_entry_visited", StringComparison.Ordinal) || value.Contains("full_root_enumeration", StringComparison.Ordinal)));
        }
    }

    /// <summary>coldな任意索引を一件変更のために構築せず、解析依存が変わった項目だけを未完了入力へ集約する。</summary>
    [TestMethod]
    public void CurrentCommit_DoesNotBuildColdIndexesAndTracksOnlyAnalysisInputs()
    {
        var owner = new CatalogOwnedCollectionOwner();
        owner.ReplaceCharts([Chart(1), Chart(2)], []);
        ChartFile first = owner.BmsRows[0], second = owner.BmsRows[1];
        owner.CompleteChartInfoInputs([], full: true);
        CatalogCommitReceipt cold = owner.CommitCurrentValues([first], (current, _) => current with { Title = "title" });
        Assert.IsFalse(cold.Snapshot.TryCapturePlaylistReadSnapshot([new(PlaylistEntryLookupKeyKind.Md5, first.Md5)], out _));
        Assert.IsFalse(owner.IsHashIndexWarm());
        Assert.IsFalse(owner.TryGetOwnedHashContentVersion(out int contentVersion));
        Assert.AreEqual(0, contentVersion);
        Assert.IsFalse(owner.IsPlaylistLibraryResolveIndexWarm());
        owner.CommitCurrentValues([second], (current, _) => current with { Sha256 = new string('f', 64) });
        Assert.IsTrue(owner.TryCapturePendingChartInfoInputs(out IReadOnlyList<ChartFile>? pending));
        Assert.AreEqual(1, pending.Count);
        Assert.AreSame(second.Token, pending[0].Token);
    }

    /// <summary>hash内容版の非構築読取りはcoldを区別し、同hash別配置の加入・途中離脱では維持、最後の離脱で進行する。</summary>
    [TestMethod]
    public void OwnedHashContentVersion_TracksHashPresenceWithoutBuildingColdRoot()
    {
        var owner = new CatalogOwnedCollectionOwner();
        ChartFile first = Chart(1), kept = Chart(2);
        owner.ReplaceCharts([first, kept], []);
        var work = new List<string>();
        owner.StoreWorkObserver = work.Add;
        Assert.IsFalse(owner.TryGetOwnedHashContentVersion(out int coldVersion));
        Assert.AreEqual(0, coldVersion);
        Assert.AreEqual(0, work.Count);
        OwnedChartHashIndexVersionedSnapshot hashes = owner.GetHashIndexSnapshot(CancellationToken.None, out _, out _);
        Assert.IsTrue(owner.TryGetOwnedHashContentVersion(out int initialVersion));
        Assert.AreEqual(hashes.Version, initialVersion);
        work.Clear();
        ChartFile duplicate = Chart(3) with { Md5 = first.Md5, Sha256 = first.Sha256 };
        owner.ApplyCommittedChartUpsert([duplicate], out _);
        Assert.IsTrue(owner.TryGetOwnedHashContentVersion(out int duplicateVersion));
        Assert.AreEqual(initialVersion, duplicateVersion);
        owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(first)], [], out _);
        Assert.IsTrue(owner.TryGetOwnedHashContentVersion(out int stillPresentVersion));
        Assert.AreEqual(initialVersion, stillPresentVersion);
        owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(owner.BmsRows.Single(chart => chart.Path == duplicate.Path))], [], out _);
        Assert.IsTrue(owner.TryGetOwnedHashContentVersion(out int removedVersion));
        Assert.AreEqual(initialVersion + 1, removedVersion);
        Assert.IsFalse(work.Any(value => value.Contains("source_entry_visited", StringComparison.Ordinal) || value.Contains("full_root_enumeration", StringComparison.Ordinal)));
        owner.InvalidateHashIndexSnapshot();
        int beforeColdRead = work.Count;
        Assert.IsFalse(owner.TryGetOwnedHashContentVersion(out coldVersion));
        Assert.AreEqual(0, coldVersion);
        Assert.AreEqual(beforeColdRead, work.Count);
    }

    /// <summary>metadata profileは原題・作者・pathの旧新ディレクトリと既存祖先だけを失効し、health変更や無関係な領域を保持する。</summary>
    [TestMethod]
    public void MetadataProfiles_InvalidateOnlyActualMetadataDependenciesAndAncestors()
    {
        var owner = new CatalogOwnedCollectionOwner();
        owner.ReplaceCharts([Chart(10)], []);
        ChartFile first = owner.BmsRows[0];
        owner.MetadataProfiles[@"C:\Library"] = InstallEstimationMetadataProfile.Empty;
        owner.MetadataProfiles[@"C:\Other"] = InstallEstimationMetadataProfile.Empty;
        owner.CommitCurrentValues([first], (current, _) => current with { WAVHealth = 0 });
        Assert.IsTrue(owner.MetadataProfiles.ContainsKey(@"C:\Library"));
        owner.CommitCurrentValues([first], (current, _) => current with { RawTitle = "new raw title" });
        Assert.IsFalse(owner.MetadataProfiles.ContainsKey(@"C:\Library"));
        Assert.IsTrue(owner.MetadataProfiles.ContainsKey(@"C:\Other"));
        CatalogReadSnapshot read = owner.CapturePlaylistReadSnapshot([new(PlaylistEntryLookupKeyKind.Md5, first.Md5.ToLowerInvariant())]);
        Assert.AreEqual("new raw title", read.Resolve(new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, first.Md5.ToUpperInvariant())).RawTitle);
    }

    private static ChartFile Chart(int index) => ChartTestValues.Empty() with
    {
        Token = new OwnedChartToken(),
        Path = $@"C:\Library\{index}.bms",
        Md5 = index.ToString("x32"),
        Sha256 = index.ToString("x64"),
        Title = "original"
    };
}
