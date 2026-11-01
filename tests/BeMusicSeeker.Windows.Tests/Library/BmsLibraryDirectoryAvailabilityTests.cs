using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDirectoryAvailabilityTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryDirectoryAvailabilityTests
{
    /// <summary>
    /// 保証：差分再読込み前に登録ルートが消えたら型付き停止を返し、未接続ルートの譜面・bmson・folderと利用者列をDB・目録から削除しない。
    /// </summary>
    /// <remarks>実ライブラリの走査入口と実DBを使い、単なる検査例外だけでなく差分確定による誤削除が起きないことを確認する。</remarks>
    [TestMethod]
    public void ReloadFileDiff_WhenRegisteredRootDisappears_PreservesCatalogSentinel()
    {
        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string rootDirectoryA = Path.Combine(scope.DirectoryPath, "BMS-A");
        string rootDirectoryB = Path.Combine(scope.DirectoryPath, "BMS-B");
        string temporarilyUnavailableRoot = Path.Combine(scope.DirectoryPath, "BMS-B-unavailable");
        Directory.CreateDirectory(rootDirectoryA);
        Directory.CreateDirectory(rootDirectoryB);
        string chartPathA = Path.Combine(rootDirectoryA, "chart-a.bms");
        string chartPathB = Path.Combine(rootDirectoryB, "chart-b.bms");
        File.WriteAllText(chartPathA, "#TITLE A\r\n#00111:01\r\n");
        File.WriteAllText(chartPathB, "#TITLE B\r\n#00111:01\r\n");

        const string hashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string hashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
        {
            SeedCatalogSentinels(setup, rootDirectoryB, chartPathA, chartPathB, hashA, hashB);
        }

        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false
        };
        IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
            [chartPathA],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectoryA] = []
            },
            [rootDirectoryA]);
        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner, settings: TestBmsFactory.CreateRootSettings([rootDirectoryA, rootDirectoryB]))
        {

            BmsCharts =
            [
                ((ChartTestValues.Empty() with { Path = chartPathA })) with { Md5 = hashA, Favorite = 1 },
                ((ChartTestValues.Empty() with { Path = chartPathB })) with { Md5 = hashB, Favorite = 2 }
            ]
        };

        Directory.Move(rootDirectoryB, temporarilyUnavailableRoot);
        try
        {
            LibraryDirectoryPreflightException failure = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(
                () => library.ReloadFileDiff());
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, failure.Use);
            StringAssert.Contains(failure.DirectoryPath, "BMS-B");
        }
        finally
        {
            if (Directory.Exists(temporarilyUnavailableRoot))
            {
                Directory.Move(temporarilyUnavailableRoot, rootDirectoryB);
            }
        }

        using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
        LR2SongDB.song? preserved = verify.Table<LR2SongDB.song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preserved);
        LR2SongDB.song preservedSong = preserved!;
        Assert.AreEqual("keep-b", preservedSong.tag);
        Assert.AreEqual(2, preservedSong.favorite);
        Assert.AreEqual(2, preservedSong.adddate);
        LR2SongDBExtended.bmson_song? preservedBmson = verify.Table<LR2SongDBExtended.bmson_song>()
            .SingleOrDefault(row => string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preservedBmson);
        LR2SongDBExtended.bmson_song preservedBmsonRow = preservedBmson!;
        Assert.AreEqual("keep-bmson", preservedBmsonRow.title);
        LR2SongDB.folder? preservedFolder = verify.Table<LR2SongDB.folder>()
            .SingleOrDefault(row => string.Equals(
                row?.path,
                Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preservedFolder);
        LR2SongDB.folder preservedFolderRow = preservedFolder!;
        Assert.AreEqual("keep-b-folder", preservedFolderRow.title);
        Assert.IsTrue(library.BmsCharts.Any(row =>
            string.Equals(row?.Path, chartPathB, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 保証：全再初期化前に登録ルートが消えたら停止し、欠落を空ライブラリへ読み替えず既存譜面・bmson・folderを保持する。
    /// </summary>
    /// <remarks>実再初期化入口で登録ディレクトリの検査とDB保護の接続を確認する。</remarks>
    [TestMethod]
    public void Reinitialize_WhenRegisteredRootDisappears_PreservesCatalogSentinel()
    {
        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string rootDirectoryA = Path.Combine(scope.DirectoryPath, "BMS-A");
        string rootDirectoryB = Path.Combine(scope.DirectoryPath, "BMS-B");
        string temporarilyUnavailableRoot = Path.Combine(scope.DirectoryPath, "BMS-B-unavailable");
        Directory.CreateDirectory(rootDirectoryA);
        Directory.CreateDirectory(rootDirectoryB);
        string chartPathA = Path.Combine(rootDirectoryA, "chart-a.bms");
        string chartPathB = Path.Combine(rootDirectoryB, "chart-b.bms");
        File.WriteAllText(chartPathA, "#TITLE A\r\n#00111:01\r\n");
        File.WriteAllText(chartPathB, "#TITLE B\r\n#00111:01\r\n");

        const string hashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string hashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
        {
            SeedCatalogSentinels(setup, rootDirectoryB, chartPathA, chartPathB, hashA, hashB);
        }

        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false
        };
        IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
            [chartPathA],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectoryA] = []
            },
            [rootDirectoryA]);
        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner, settings: TestBmsFactory.CreateRootSettings([rootDirectoryA, rootDirectoryB]))
        {

            BmsCharts =
            [
                ((ChartTestValues.Empty() with { Path = chartPathA })) with { Md5 = hashA, Favorite = 1 },
                ((ChartTestValues.Empty() with { Path = chartPathB })) with { Md5 = hashB, Favorite = 2 }
            ]
        };

        Directory.Move(rootDirectoryB, temporarilyUnavailableRoot);
        try
        {
            LibraryDirectoryPreflightException failure = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(
                () => library.Reinitialize());
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, failure.Use);
            StringAssert.Contains(failure.DirectoryPath, "BMS-B");
        }
        finally
        {
            if (Directory.Exists(temporarilyUnavailableRoot))
            {
                Directory.Move(temporarilyUnavailableRoot, rootDirectoryB);
            }
        }

        using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
        Assert.IsNotNull(verify.Table<LR2SongDB.song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase)));
        Assert.IsNotNull(verify.Table<LR2SongDBExtended.bmson_song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase)));
        Assert.IsNotNull(verify.Table<LR2SongDB.folder>().SingleOrDefault(row =>
            string.Equals(
                row?.path,
                Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(library.BmsCharts.Any(row =>
            string.Equals(row?.Path, chartPathB, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 保証：登録root欠落で差分更新を停止した後、rootを戻して再実行でき、利用者のtag・favorite・adddateと所持譜面を維持する。
    /// </summary>
    /// <remarks>実差分更新入口の事前検査でroot欠落を起こし、復旧後の再受付と保存値の保持まで確認する。</remarks>
    [TestMethod]
    public void ReloadFileDiff_AfterPreflightFailureAndRootRestore_Succeeds()
    {
        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string rootDirectoryA = Path.Combine(scope.DirectoryPath, "BMS-A");
        string rootDirectoryB = Path.Combine(scope.DirectoryPath, "BMS-B");
        string temporarilyUnavailableRoot = Path.Combine(scope.DirectoryPath, "BMS-B-unavailable");
        Directory.CreateDirectory(rootDirectoryA);
        Directory.CreateDirectory(rootDirectoryB);
        string chartPathA = Path.Combine(rootDirectoryA, "chart-a.bms");
        string chartPathB = Path.Combine(rootDirectoryB, "chart-b.bms");
        File.WriteAllText(chartPathA, "#TITLE A\r\n#00111:01\r\n");
        File.WriteAllText(chartPathB, "#TITLE B\r\n#00111:01\r\n");

        const string hashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string hashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
        {
            setup.CreateTable<LR2SongDB.song>();
            setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(((ChartTestValues.Empty() with { Path = chartPathA, AddDate = 1, Tag = "keep-a" })) with { Md5 = hashA, Favorite = 1 }), typeof(LR2SongDB.song));
            setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(((ChartTestValues.Empty() with { Path = chartPathB, AddDate = 2, Tag = "keep-b" })) with { Md5 = hashB, Favorite = 2 }), typeof(LR2SongDB.song));
        }

        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false
        };
        IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
            [chartPathA, chartPathB],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectoryA] = [],
                [rootDirectoryB] = []
            },
            [rootDirectoryA, rootDirectoryB]);
        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner, settings: TestBmsFactory.CreateRootSettings([rootDirectoryA, rootDirectoryB]))
        {

            BmsCharts =
            [
                ((ChartTestValues.Empty() with { Path = chartPathA })) with { Md5 = hashA, Favorite = 1 },
                ((ChartTestValues.Empty() with { Path = chartPathB })) with { Md5 = hashB, Favorite = 2 }
            ]
        };

        Directory.Move(rootDirectoryB, temporarilyUnavailableRoot);
        try
        {
            Assert.ThrowsExactly<LibraryDirectoryPreflightException>(
                () => library.ReloadFileDiff());
        }
        finally
        {
            if (Directory.Exists(temporarilyUnavailableRoot))
            {
                Directory.Move(temporarilyUnavailableRoot, rootDirectoryB);
            }
        }

        library.ReloadFileDiff();

        using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
        LR2SongDB.song? preserved = verify.Table<LR2SongDB.song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preserved);
        LR2SongDB.song preservedSong = preserved!;
        Assert.AreEqual("keep-b", preservedSong.tag);
        Assert.AreEqual(2, preservedSong.favorite);
        Assert.AreEqual(2, preservedSong.adddate);
        Assert.IsTrue(library.BmsCharts.Any(row =>
            string.Equals(row?.Path, chartPathB, StringComparison.OrdinalIgnoreCase)));
    }

}
