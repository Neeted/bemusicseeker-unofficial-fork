using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionInstalledOverlayTests
{
    [TestMethod]
    public void AutoRenameAllChartFolders_RootOnlyChartsAreNotActionableTargets()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "LibraryRoot");
            Directory.CreateDirectory(rootPath);
            string chartPath = Path.Combine(rootPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                SearchTargets = [rootPath]
            };
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);

            Assert.IsFalse(library.HasAutoRenameAllChartFolderTargets(rootPath));
            Assert.IsFalse(library.AutoRenameAllChartFoldersWithProgress(
                rootPath,
                new RecordingFolderAutoRenameProgressWriter()).HasActionablePlan);
        });
    }

    [TestMethod]
    public void CreateInstalledChartKeySnapshotExcludingCharts_BuildsPrimaryLookupWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            IPrimaryHashLookup lookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsTrue(lookup.ContainsPrimaryHash(bmsFile.hash));
            Assert.IsTrue(lookup.ContainsPrimaryHash(bmsonSong.md5));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void CreateInstalledChartKeySnapshotExcludingCharts_ExcludesOnlyPrimaryHashCounts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestableBmsFile firstBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            TestableBmsFile secondBmsFile = CreateFile(firstBmsFile.hash, Path.Combine("C:\\Installed", "Second", "chart.bms"));
            TestableBmsFile otherBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Other", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [firstBmsFile, secondBmsFile, otherBmsFile],
                BmsonSongs = []
            };

            IPrimaryHashLookup excludingOne = InvokeCreateInstalledChartKeySnapshotExcludingCharts(
                library,
                [ChartFileProjection.FromBmsFile(firstBmsFile, includeWarningSnapshot: false, includeResourceReferences: false)]);
            IPrimaryHashLookup excludingBoth = InvokeCreateInstalledChartKeySnapshotExcludingCharts(
                library,
                [
                    ChartFileProjection.FromBmsFile(firstBmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
                    ChartFileProjection.FromBmsFile(secondBmsFile, includeWarningSnapshot: false, includeResourceReferences: false)
                ]);

            Assert.AreEqual(1, excludingOne.GetPrimaryHashCount(firstBmsFile.hash));
            Assert.IsTrue(excludingOne.ContainsPrimaryHash(firstBmsFile.hash));
            Assert.AreEqual(0, excludingBoth.GetPrimaryHashCount(firstBmsFile.hash));
            Assert.IsFalse(excludingBoth.ContainsPrimaryHash(firstBmsFile.hash));
            Assert.IsTrue(excludingBoth.ContainsPrimaryHash(otherBmsFile.hash));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_UpdatesPrimaryLookupWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed");
            string removedDirectoryPath = Path.Combine(rootPath, "Removed");
            string keptDirectoryPath = Path.Combine(rootPath, "Kept");
            Directory.CreateDirectory(removedDirectoryPath);
            Directory.CreateDirectory(keptDirectoryPath);
            string removedPath = Path.Combine(removedDirectoryPath, "chart.bms");
            string keptPath = Path.Combine(keptDirectoryPath, "chart.bms");
            File.WriteAllText(removedPath, "#PLAYER 1");
            File.WriteAllText(keptPath, "#PLAYER 1");
            TestableBmsFile removedBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", removedPath);
            TestableBmsFile keptBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", keptPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null)
            {
                BMSFiles = [removedBmsFile, keptBmsFile],
                BmsonSongs = []
            };
            IPrimaryHashLookup initialLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(removedBmsFile.hash));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(removedBmsFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            IPrimaryHashLookup updatedLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(removedBmsFile.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(keptBmsFile.hash));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_DoesNotPruneSamePathDifferentBmsOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "Bms");
            Directory.CreateDirectory(chartDirectory);
            string sharedPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(sharedPath, "#PLAYER 1");
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath, new string('b', 64));
            TestableBmsFile duplicateOwner = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath, new string('c', 64));
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null)
            {
                BMSFiles = [bmsFile, duplicateOwner],
                BmsonSongs = []
            };

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(bmsFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(duplicateOwner, library.BMSFiles[0]);
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_DoesNotPruneSamePathDifferentBmsonOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "Bmson");
            Directory.CreateDirectory(chartDirectory);
            string sharedPath = Path.Combine(chartDirectory, "chart.bmson");
            File.WriteAllText(sharedPath, "{}");
            LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(sharedPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            LR2SongDBExtended.bmson_song duplicateOwner = CreateBmsonSong(sharedPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null)
            {
                BMSFiles = [],
                BmsonSongs = [bmsonSong, duplicateOwner]
            };

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsonSong(bmsonSong)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreSame(duplicateOwner, library.BmsonSongs[0]);
        });
    }

    [TestMethod]
    public void WarmOwnedRealPathDirectoryView_BuildsAndReusesOwnedRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.Combine("C:\\Installed", "Warmup");
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(root, "Bms", "chart.bms"));
            LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine(root, "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };
            BMSLibrary.OwnedAdjacentIndexWarmupResult first = library.WarmOwnedRealPathDirectoryView("test");
            BMSLibrary.OwnedAdjacentIndexWarmupResult second = library.WarmOwnedRealPathDirectoryView("test");

            Assert.AreEqual("real_path", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(2, first.ChartRefCount);
            Assert.IsTrue(first.DirectDirectoryCount >= 2);
            Assert.IsTrue(first.SubtreeDirectoryCount >= 1);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.ChartRefCount, second.ChartRefCount);
        });
    }

    [TestMethod]
    public void WarmInstalledPrimaryHashLookup_BuildsWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "PrimaryWarmup", "Bms", "chart.bms"));
            LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "PrimaryWarmup", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };
            Assert.IsFalse(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));

            BMSLibrary.InstalledPrimaryHashWarmupResult first = library.WarmInstalledPrimaryHashLookup("test");
            BMSLibrary.InstalledPrimaryHashWarmupResult second = library.WarmInstalledPrimaryHashLookup("test");

            Assert.AreEqual("installed_primary_hash", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(2, first.PrimaryHashCount);
            Assert.AreEqual(1, first.BmsCount);
            Assert.AreEqual(1, first.BmsonCount);
            Assert.IsFalse(first.FullDirectoryLookupInitialized);
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.PrimaryHashCount, second.PrimaryHashCount);
            Assert.AreEqual(0, second.BmsCount);
            Assert.AreEqual(0, second.BmsonCount);
            Assert.IsFalse(second.FullDirectoryLookupInitialized);
        });
    }


}
