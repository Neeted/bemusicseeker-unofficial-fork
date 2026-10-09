using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionInstalledOverlayTests
{
    [TestMethod]
    public void AutoRenameAllChartFolders_RootOnlyChartsAreNotActionableTargets()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "LibraryRoot");
            Directory.CreateDirectory(rootPath);
            string chartPath = Path.Combine(rootPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
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

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [bmsFile],
                BmsonCharts = [bmsonSong]
            };

            IPrimaryHashLookup lookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsTrue(lookup.ContainsPrimaryHash(bmsFile.Md5));
            Assert.IsTrue(lookup.ContainsPrimaryHash(bmsonSong.Md5));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void CreateInstalledChartKeySnapshotExcludingCharts_ExcludesOnlyPrimaryHashCounts()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile firstBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            ChartFile secondBmsFile = CreateFile(firstBmsFile.Md5, Path.Combine("C:\\Installed", "Second", "chart.bms"));
            ChartFile otherBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Other", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [firstBmsFile, secondBmsFile, otherBmsFile],
                BmsonCharts = []
            };

            IPrimaryHashLookup excludingOne = InvokeCreateInstalledChartKeySnapshotExcludingCharts(
                library,
                [(firstBmsFile)]);
            IPrimaryHashLookup excludingBoth = InvokeCreateInstalledChartKeySnapshotExcludingCharts(
                library,
                [
                    (firstBmsFile),
                    (secondBmsFile)
                ]);

            Assert.AreEqual(1, excludingOne.GetPrimaryHashCount(firstBmsFile.Md5));
            Assert.IsTrue(excludingOne.ContainsPrimaryHash(firstBmsFile.Md5));
            Assert.AreEqual(0, excludingBoth.GetPrimaryHashCount(firstBmsFile.Md5));
            Assert.IsFalse(excludingBoth.ContainsPrimaryHash(firstBmsFile.Md5));
            Assert.IsTrue(excludingBoth.ContainsPrimaryHash(otherBmsFile.Md5));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_UpdatesPrimaryLookupWithoutFullDirectoryLookup()
    {

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
            ChartFile removedBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", removedPath);
            ChartFile keptBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", keptPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null)
            {
                BmsCharts = [removedBmsFile, keptBmsFile],
                BmsonCharts = []
            };
            IPrimaryHashLookup initialLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(removedBmsFile.Md5));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((removedBmsFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            IPrimaryHashLookup updatedLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(removedBmsFile.Md5));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(keptBmsFile.Md5));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_DoesNotPromoteSkippedDuplicateBmsExactPath()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "Bms");
            Directory.CreateDirectory(chartDirectory);
            string sharedPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(sharedPath, "#PLAYER 1");
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath, new string('b', 64));
            ChartFile duplicateOwner = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath, new string('c', 64));
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null)
            {
                BmsCharts = [bmsFile, duplicateOwner],
                BmsonCharts = []
            };

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((bmsFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(0, library.BmsCharts.Count);
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_DoesNotPromoteSkippedDuplicateBmsonExactPath()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "Bmson");
            Directory.CreateDirectory(chartDirectory);
            string sharedPath = Path.Combine(chartDirectory, "chart.bmson");
            File.WriteAllText(sharedPath, "{}");
            ChartFile bmsonSong = CreateBmsonSong(sharedPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile duplicateOwner = CreateBmsonSong(sharedPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null)
            {
                BmsCharts = [],
                BmsonCharts = [bmsonSong, duplicateOwner]
            };

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((bmsonSong))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(0, library.BmsonCharts.Count);
        });
    }

    [TestMethod]
    public void WarmOwnedRealPathDirectoryView_BuildsAndReusesOwnedRefIndex()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.Combine("C:\\Installed", "Warmup");
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(root, "Bms", "chart.bms"));
            ChartFile bmsonSong = CreateBmsonSong(Path.Combine(root, "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [bmsFile],
                BmsonCharts = [bmsonSong]
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

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "PrimaryWarmup", "Bms", "chart.bms"));
            ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "PrimaryWarmup", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [bmsFile],
                BmsonCharts = [bmsonSong]
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
