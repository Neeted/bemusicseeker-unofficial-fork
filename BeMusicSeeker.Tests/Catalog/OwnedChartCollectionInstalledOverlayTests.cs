using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionInstalledOverlayTests
{
    /// <summary>
    /// root直下の譜面だけならフォルダ一括改名の実行対象を作らず、利用者のroot自体を改名させない。
    /// </summary>
    /// <remarks>実ライブラリの一括改名入口と所持集合を接続し、root保全の実行計画を観測する。</remarks>
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
            var library = new TestBmsLibrary(songDbPath, settings: TestBmsFactory.CreateRootSettings([rootPath]))
            {

            };
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);

            Assert.IsFalse(library.HasAutoRenameAllChartFolderTargets(rootPath));
            Assert.IsFalse(library.AutoRenameAllChartFoldersWithProgress(
                rootPath,
                new RecordingFolderAutoRenameProgressWriter()).HasActionablePlan);
        });
    }

    /// <summary>
    /// 導入済み主hash検索だけを初回要求したときBMS・bmsonを含め、不要なディレクトリ索引を構築しない。
    /// </summary>
    /// <remarks>実ライブラリのhash要求入口を使い、初回索引の選択と両形式の所有捕捉を確認する。</remarks>
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

    /// <summary>
    /// 同じ主hashの一件だけの除外は残る所有数を保ち、全件除外では別hashの所有を残してディレクトリ索引を構築しない。
    /// </summary>
    /// <remarks>実ライブラリの除外snapshot入口を使い、現在の所有数と主hash索引の分担を確認する。</remarks>
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

    /// <summary>
    /// 実削除後は初期化済みの主hash検索から対象だけを除き、残る所有を保持してディレクトリ索引を追加構築しない。
    /// </summary>
    /// <remarks>実削除を初期化済みhash索引へ接続し、全ディレクトリ索引の不要な構築を検出する。</remarks>
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

    /// <summary>
    /// BMSの同パス重複を除外した後で実削除したとき、除外済みの別所有を復活させない。
    /// </summary>
    /// <remarks>実ライブラリで同パスの重複排除と物理削除を接続し、除外済みBMS所有の復活を検出する。</remarks>
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

    /// <summary>
    /// bmsonの同パス重複を除外した後で実削除したとき、除外済みの別所有を復活させない。
    /// </summary>
    /// <remarks>実ライブラリで同パスの重複排除と物理削除を接続し、除外済みbmson所有の復活を検出する。</remarks>
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

    /// <summary>
    /// 実ライブラリの配下参照索引は初回暖機でBMS・bmsonを捕捉し、二回目は同じ件数をcacheから返す。
    /// </summary>
    /// <remarks>実ライブラリの暖機入口から配下参照索引を構築し、二回目の再構築を検出する。</remarks>
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

    /// <summary>
    /// 導入済み主hash暖機は両種別のhashを一度構築し、ディレクトリ索引を作らず二回目の全件取得を省く。
    /// </summary>
    /// <remarks>実ライブラリの主hash暖機入口を使い、初回の両形式捕捉と二回目の全件取得省略を確認する。</remarks>
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
