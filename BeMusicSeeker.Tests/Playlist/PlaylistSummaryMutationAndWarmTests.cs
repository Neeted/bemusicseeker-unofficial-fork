using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>ライブラリ変更を所持ハッシュ索引へ反映し、再利用と局所更新を検証する。</summary>
/// <remarks>実ライブラリと専用DB・削除対象ファイルを使う。所持判断の入力変換だけでは検出できない変更確定後の索引失効・旧スナップショット不変を担う。</remarks>
[TestClass]
public sealed class PlaylistSummaryMutationAndWarmTests
{
    /// <summary>ライブラリ集合を別ハッシュへ差替えると索引世代が進み、旧ハッシュを除いて新ハッシュを含める。集合差替後の索引再利用を検出する。</summary>
    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_RebuildsAfterLibraryOwnershipChanges()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts =
                [
                    CreateLibraryFile(@"C:\Songs\old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };
            OwnedChartHashIndexVersionedSnapshot first = library.GetOwnedChartHashIndexSnapshot();

            library.BmsCharts =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];
            OwnedChartHashIndexVersionedSnapshot second = library.GetOwnedChartHashIndexSnapshot();

            Assert.IsTrue(second.Version > first.Version);
            CollectionAssert.DoesNotContain(new List<string>(second.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(second.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

    /// <summary>実譜面の削除確定後は削除ハッシュを除き、残る譜面のハッシュと新世代を返す。ライブラリ変更と所持索引の接続漏れを検出する。</summary>
    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_AppliesRemovalAfterLibraryMutation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Songs");
            Directory.CreateDirectory(rootPath);
            string removedPath = Path.Combine(rootPath, "removed.bms");
            string keptPath = Path.Combine(rootPath, "kept.bms");
            File.WriteAllText(removedPath, "#PLAYER 1");
            File.WriteAllText(keptPath, "#PLAYER 1");
            ChartFile removedFile = CreateLibraryFile(removedPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            ChartFile keptFile = CreateLibraryFile(keptPath, "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts = [removedFile, keptFile]
            };
            OwnedChartHashIndexVersionedSnapshot first = library.GetOwnedChartHashIndexSnapshot();
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((removedFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            OwnedChartHashIndexVersionedSnapshot second = library.GetOwnedChartHashIndexSnapshot();

            Assert.IsTrue(second.Version > first.Version);
            CollectionAssert.DoesNotContain(new List<string>(second.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(second.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

    /// <summary>初回の事前構築を再利用し、ライブラリ差替後は新しい所持・索引世代で再構築する。毎回の再構築と失効後の旧結果再利用を検出する。</summary>
    [TestMethod]
    public void WarmOwnedChartHashIndexSnapshot_BuildsReusesAndRebuildsAfterOwnershipChange()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts =
                [
                    CreateLibraryFile(@"C:\Songs\old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };

            OwnedHashIndexWarmupResult first = library.WarmOwnedChartHashIndexSnapshot("test");
            OwnedHashIndexWarmupResult second = library.WarmOwnedChartHashIndexSnapshot("test");

            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(1, first.Md5Count);
            Assert.AreEqual(1, first.Sha256Count);
            Assert.AreEqual(library.CatalogRevision, first.CatalogRevision);
            Assert.IsTrue(first.CatalogRevision > 0);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.SnapshotVersion, second.SnapshotVersion);
            Assert.AreEqual(first.InvalidationVersion, second.InvalidationVersion);
            Assert.AreEqual(first.CatalogRevision, second.CatalogRevision);

            library.BmsCharts =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];

            OwnedHashIndexWarmupResult third = library.WarmOwnedChartHashIndexSnapshot("test");
            OwnedChartHashIndexVersionedSnapshot snapshot = library.GetOwnedChartHashIndexSnapshot();

            Assert.AreEqual("built", third.Status);
            Assert.IsTrue(third.SnapshotVersion > second.SnapshotVersion);
            Assert.IsTrue(third.InvalidationVersion > second.InvalidationVersion);
            Assert.AreEqual(library.CatalogRevision, third.CatalogRevision);
            Assert.IsTrue(third.CatalogRevision > second.CatalogRevision);
            Assert.AreEqual(third.SnapshotVersion, snapshot.Version);
            CollectionAssert.DoesNotContain(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

    /// <summary>同じハッシュを持つBMS・bmsonを順に削除し、最後の所有者が消えた時だけ所属ハッシュ集合の世代を進める。早すぎる未所持化と旧索引の可変共有を検出する。</summary>
    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_TracksBmsAndBmsonOwnerCountsAndKeepsOldSnapshot()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureBmsonSchema();
            const string sharedMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string sharedSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Songs");
            Directory.CreateDirectory(rootPath);
            string bmsPath = Path.Combine(rootPath, "shared.bms");
            string bmsonPath = Path.Combine(rootPath, "shared.bmson");
            File.WriteAllText(bmsPath, "#PLAYER 1");
            File.WriteAllText(bmsonPath, "{}");
            ChartFile bmsFile = CreateLibraryFile(bmsPath, sharedMd5, sharedSha256);
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = bmsonPath,
                Md5 = sharedMd5,
                Sha256 = sharedSha256
            };
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts = [bmsFile],
                BmsonCharts = [bmsonSong]
            };

            OwnedChartHashIndexVersionedSnapshot initial = library.GetOwnedChartHashIndexSnapshot();
            Assert.AreEqual(2, initial.GetMd5OwnerCount(sharedMd5));
            Assert.AreEqual(2, initial.GetSha256OwnerCount(sharedSha256));
            Assert.IsTrue(initial.ContainsMd5(sharedMd5));
            Assert.IsTrue(initial.ContainsSha256(sharedSha256));

            LibraryChartRemovalOutcome removeBms = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((bmsFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removeBms.HasError);
            OwnedChartHashIndexVersionedSnapshot oneOwner = library.GetOwnedChartHashIndexSnapshot();

            Assert.AreEqual(initial.Version, oneOwner.Version);
            Assert.AreEqual(1, oneOwner.GetMd5OwnerCount(sharedMd5));
            Assert.AreEqual(1, oneOwner.GetSha256OwnerCount(sharedSha256));
            Assert.IsTrue(oneOwner.ContainsMd5(sharedMd5));
            Assert.IsTrue(oneOwner.ContainsSha256(sharedSha256));

            LibraryChartRemovalOutcome removeBmson = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((bmsonSong))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removeBmson.HasError);
            OwnedChartHashIndexVersionedSnapshot empty = library.GetOwnedChartHashIndexSnapshot();

            Assert.IsTrue(empty.Version > oneOwner.Version);
            Assert.AreEqual(0, empty.GetMd5OwnerCount(sharedMd5));
            Assert.AreEqual(0, empty.GetSha256OwnerCount(sharedSha256));
            Assert.IsFalse(empty.ContainsMd5(sharedMd5));
            Assert.IsFalse(empty.ContainsSha256(sharedSha256));

            Assert.AreEqual(2, initial.GetMd5OwnerCount(sharedMd5));
            Assert.AreEqual(2, initial.GetSha256OwnerCount(sharedSha256));
            Assert.AreEqual(1, oneOwner.GetMd5OwnerCount(sharedMd5));
            Assert.AreEqual(1, oneOwner.GetSha256OwnerCount(sharedSha256));
        });
    }

    /// <summary>初回の全譜面走査後は既知の削除差分で対象キーだけを更新し、全譜面・全キーを走査しない。少数変更に対する全件再構築の退行を検出する。</summary>
    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_ColdBuildEnumeratesSourceThenKnownDeltaAvoidsFullBuilder()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Songs");
            Directory.CreateDirectory(rootPath);
            string removedPath = Path.Combine(rootPath, "removed.bms");
            string keptPath = Path.Combine(rootPath, "kept.bms");
            File.WriteAllText(removedPath, "#PLAYER 1");
            File.WriteAllText(keptPath, "#PLAYER 1");
            ChartFile removedFile = CreateLibraryFile(
                removedPath,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            ChartFile keptFile = CreateLibraryFile(
                keptPath,
                "cccccccccccccccccccccccccccccccc",
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts = [removedFile, keptFile]
            };
            List<string> storeWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = storeWork.Add;

            OwnedChartHashIndexVersionedSnapshot cold = library.GetOwnedChartHashIndexSnapshot();
            Assert.IsTrue(storeWork.Contains("owned_hash_source_enumeration"));
            Assert.AreEqual(2, storeWork.Count(operation => operation == "owned_hash_source_entry_visited"));
            Assert.IsTrue(storeWork.Contains("owned_hash_root_capture"));
            Assert.AreEqual(2, cold.GetMd5OwnerCount("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                + cold.GetMd5OwnerCount("cccccccccccccccccccccccccccccccc"));

            storeWork.Clear();
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((removedFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            OwnedChartHashIndexVersionedSnapshot updated = library.GetOwnedChartHashIndexSnapshot();

            Assert.AreEqual(0, storeWork.Count(operation => operation == "owned_hash_source_enumeration"));
            Assert.AreEqual(0, storeWork.Count(operation => operation == "owned_hash_source_entry_visited"));
            Assert.IsTrue(storeWork.Contains("owned_hash_delta_apply"));
            Assert.IsTrue(storeWork.Count(operation => operation == "owned_hash_count_root_update") <= 2);
            Assert.AreEqual(0, storeWork.Count(operation => operation == "owned_hash_root_enumeration"));
            Assert.AreEqual(0, storeWork.Count(operation => operation == "owned_hash_root_key_visited"));
            Assert.AreEqual(0, updated.GetMd5OwnerCount("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.AreEqual(1, updated.GetMd5OwnerCount("cccccccccccccccccccccccccccccccc"));
            Assert.AreEqual(0, updated.GetSha256OwnerCount("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
            Assert.AreEqual(1, updated.GetSha256OwnerCount("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"));
        });
    }


}
