using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSummaryMutationAndWarmTests
{
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

            Assert.AreEqual("catalog_owned_hash", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(1, first.Md5Count);
            Assert.AreEqual(1, first.Sha256Count);
            Assert.AreEqual(0, first.StaleRetryCount);
            Assert.AreEqual(library.OwnedCollectionVersion, first.OwnedCollectionVersion);
            Assert.IsTrue(first.OwnedCollectionVersion > 0);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.SnapshotVersion, second.SnapshotVersion);
            Assert.AreEqual(first.InvalidationVersion, second.InvalidationVersion);
            Assert.AreEqual(first.OwnedCollectionVersion, second.OwnedCollectionVersion);

            library.BmsCharts =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];

            OwnedHashIndexWarmupResult third = library.WarmOwnedChartHashIndexSnapshot("test");
            OwnedChartHashIndexVersionedSnapshot snapshot = library.GetOwnedChartHashIndexSnapshot();

            Assert.AreEqual("built", third.Status);
            Assert.IsTrue(third.SnapshotVersion > second.SnapshotVersion);
            Assert.IsTrue(third.InvalidationVersion > second.InvalidationVersion);
            Assert.AreEqual(library.OwnedCollectionVersion, third.OwnedCollectionVersion);
            Assert.IsTrue(third.OwnedCollectionVersion > second.OwnedCollectionVersion);
            Assert.AreEqual(third.SnapshotVersion, snapshot.Version);
            CollectionAssert.DoesNotContain(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

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

    /// <summary>R5b-LocalWork: cold source走査後の既知deltaはroot局所更新だけで再buildしません。</summary>
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
