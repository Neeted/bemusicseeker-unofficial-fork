using BeMusicSeeker.Models;
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
    /// 保証：走査入力取得中に登録ルートが消えたらスキップを警告して終端し、既存保存値と所持目録を保全して部分入力の新候補も採用しない。
    /// </summary>
    /// <remarks>scanner開始時に実ディレクトリを移動し、再読込み入口から実DBの確定保護まで観測する。</remarks>
    [TestMethod]
    public void ReloadFileDiff_WhenRootDisappearsDuringPrefetch_WarnsAndPreservesCatalogWithoutApplyingCandidates()
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
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(scope.DatabaseScope,
            scope.SongDbPath,
            setup => SeedCatalogSentinels(setup, rootDirectoryB, chartPathA, chartPathB, hashA, hashB));

        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false
        };
        string candidatePath = Path.Combine(rootDirectoryA, "candidate.bms");
        File.WriteAllText(candidatePath, "#TITLE Candidate\r\n#BPM 130\r\n#00111:01\r\n");
        ChartScanExecutionResult capturedScan = new()
        {
            ScanSource = ChartScanSource.Everything,
            Success = true,
            IsComplete = true,
            Result = BmsLibraryInitializationTestSupport.CreateScanResult(
                [chartPathA, candidatePath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectoryA] = []
                },
                [rootDirectoryA])
        };
        var scanner = new DisconnectingChartFileScanner(
            () => Directory.Move(rootDirectoryB, temporarilyUnavailableRoot),
            capturedScan);
        var dialogs = new BmsLibraryInitializationTestSupport.RecordingDialogService();
        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: scanner,
            uiScheduler: new TestUiScheduler(() => null),
            dialogService: dialogs, settings: TestBmsFactory.CreateRootSettings([rootDirectoryA, rootDirectoryB]))
        {

            BmsCharts =
            [
                ((ChartTestValues.Empty() with { Path = chartPathA, Tag = "keep-a", AddDate = 1 })) with { Md5 = hashA, Favorite = 1 },
                ((ChartTestValues.Empty() with { Path = chartPathB, Tag = "keep-b", AddDate = 2 })) with { Md5 = hashB, Favorite = 2 }
            ]
        };

        try
        {
            library.ReloadFileDiff();
            Assert.IsTrue(scanner.ScanEntered);
            string warningPrefix = BeMusicSeeker.Properties.Resources.Warn_FileScanSkippedIncomplete.Split("{0}", StringSplitOptions.None)[0];
            Assert.IsTrue(dialogs.Calls.Any(call =>
                call.Message.StartsWith(warningPrefix, StringComparison.Ordinal)
                && call.Message.Contains(rootDirectoryB, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.OperationAdmission.IsActive);
            CollectionAssert.AreEquivalent(new[] { chartPathA, chartPathB }, library.BmsCharts.Select(row => row.Path).ToArray());
            ChartFile ownedSong = library.BmsCharts.Single(row => string.Equals(row.Path, chartPathB, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("keep-b", ownedSong.Tag);
            Assert.AreEqual(2, ownedSong.Favorite);
            Assert.AreEqual(2, ownedSong.AddDate);
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
        LR2SongDB.song preservedSong = preserved;
        Assert.AreEqual("keep-b", preservedSong.tag);
        Assert.AreEqual(2, preservedSong.favorite);
        Assert.AreEqual(2, preservedSong.adddate);
        LR2SongDBExtended.bmson_song? preservedBmson = verify.Table<LR2SongDBExtended.bmson_song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preservedBmson);
        Assert.AreEqual("keep-bmson", preservedBmson.title);
        Assert.AreEqual(hashB, preservedBmson.md5);
        Assert.AreEqual(rootDirectoryB, preservedBmson.folder);
        LR2SongDB.folder? preservedFolder = verify.Table<LR2SongDB.folder>().SingleOrDefault(row =>
            string.Equals(
                row?.path,
                Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preservedFolder);
        Assert.AreEqual("keep-b-folder", preservedFolder.title);
        Assert.AreEqual(2, preservedFolder.adddate);
        Assert.IsNull(verify.Table<LR2SongDB.song>().SingleOrDefault(row =>
            string.Equals(row.path, candidatePath, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class DisconnectingChartFileScanner : IChartFileScanner
    {
        private readonly Action disconnect;
        private readonly ChartScanExecutionResult result;
        private int scanEntered;

        internal DisconnectingChartFileScanner(
            Action disconnect,
            ChartScanExecutionResult result)
        {
            this.disconnect = disconnect ?? throw new ArgumentNullException(nameof(disconnect));
            this.result = result ?? throw new ArgumentNullException(nameof(result));
        }

        internal bool ScanEntered => Volatile.Read(ref scanEntered) != 0;

        public ChartScanExecutionResult Scan(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> chartExtensions,
            bool verboseLog = false,
            bool includeTextSurface = true,
            bool includeDirectorySurface = false)
        {
            if (Interlocked.Exchange(ref scanEntered, 1) == 0)
            {
                disconnect();
            }
            return result;
        }
    }

}
