using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualBasic.FileIO;
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
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };
            OwnedChartHashIndexVersionedSnapshot first = library.GetOwnedChartHashIndexSnapshot();

            library.BMSFiles =
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
    public void GetOwnedChartHashIndexSnapshot_RebuildsAfterLibraryMutationDelta()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            BMSFile removedFile = CreateLibraryFile(@"C:\Songs\removed.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            BMSFile keptFile = CreateLibraryFile(@"C:\Songs\kept.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = [removedFile, keptFile]
            };
            OwnedChartHashIndexVersionedSnapshot first = library.GetOwnedChartHashIndexSnapshot();
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedFile));

            InvokeApplyLibraryMutationDelta(library, delta);
            OwnedChartHashIndexVersionedSnapshot second = library.GetOwnedChartHashIndexSnapshot();

            Assert.IsTrue(second.Version > first.Version);
            CollectionAssert.DoesNotContain(new List<string>(second.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(second.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }

    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_RebuildsAfterInstalledChartUpsert()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string chartPath = @"C:\Songs\replace.bms";
            BMSFile replacedFile = CreateLibraryFile(chartPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            BMSFile newFile = CreateLibraryFile(chartPath, "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = [replacedFile]
            };
            OwnedChartHashIndexVersionedSnapshot first = library.GetOwnedChartHashIndexSnapshot();

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([newFile], []));
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
                BMSFiles =
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
            Assert.AreEqual(library.OwnedChartCollectionVersion, first.OwnedCollectionVersion);
            Assert.IsTrue(first.BmsRowsVersion > 0);
            Assert.AreEqual(0, first.BmsonRowsVersion);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.SnapshotVersion, second.SnapshotVersion);
            Assert.AreEqual(first.InvalidationVersion, second.InvalidationVersion);
            Assert.AreEqual(first.OwnedCollectionVersion, second.OwnedCollectionVersion);
            Assert.AreEqual(first.BmsRowsVersion, second.BmsRowsVersion);
            Assert.AreEqual(first.BmsonRowsVersion, second.BmsonRowsVersion);

            library.BMSFiles =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];

            OwnedHashIndexWarmupResult third = library.WarmOwnedChartHashIndexSnapshot("test");
            OwnedChartHashIndexVersionedSnapshot snapshot = library.GetOwnedChartHashIndexSnapshot();

            Assert.AreEqual("built", third.Status);
            Assert.IsTrue(third.SnapshotVersion > second.SnapshotVersion);
            Assert.IsTrue(third.InvalidationVersion > second.InvalidationVersion);
            Assert.AreEqual(library.OwnedChartCollectionVersion, third.OwnedCollectionVersion);
            Assert.IsTrue(third.BmsRowsVersion > second.BmsRowsVersion);
            Assert.AreEqual(third.SnapshotVersion, snapshot.Version);
            CollectionAssert.DoesNotContain(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        });
    }


}
