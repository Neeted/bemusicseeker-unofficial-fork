using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryDirectoryAvailabilityTests
{
    [TestMethod]
    public void ReloadFileDiff_WhenRegisteredRootDisappears_PreservesCatalogSentinel()
    {
        using Lr2SongDbSyncTestSupport.TestDatabaseScope scope =
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
        using (var setup = new LR2SongDBExtended(scope.SongDbPath))
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
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner)
        {
            SearchTargets = [rootDirectoryA, rootDirectoryB],
            BMSFiles =
            [
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathA
                }.WithHashAndFavorite(hashA, 1),
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathB
                }.WithHashAndFavorite(hashB, 2)
            ]
        };

        Directory.Move(rootDirectoryB, temporarilyUnavailableRoot);
        try
        {
            LibraryDirectoryPreflightException failure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
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

        using var verify = new LR2SongDBExtended(scope.SongDbPath);
        LR2SongDB.song preserved = verify.Table<LR2SongDB.song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preserved);
        Assert.AreEqual("keep-b", preserved.tag);
        Assert.AreEqual(2, preserved.favorite);
        Assert.AreEqual(2, preserved.adddate);
        LR2SongDBExtended.bmson_song preservedBmson = verify.Table<LR2SongDBExtended.bmson_song>()
            .SingleOrDefault(row => string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preservedBmson);
        Assert.AreEqual("keep-bmson", preservedBmson.title);
        LR2SongDB.folder preservedFolder = verify.Table<LR2SongDB.folder>()
            .SingleOrDefault(row => string.Equals(
                row?.path,
                Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preservedFolder);
        Assert.AreEqual("keep-b-folder", preservedFolder.title);
        Assert.IsTrue(library.BMSFiles.Any(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ReloadFileDiff_WhenRootDisappearsDuringPrefetch_PreservesCatalogSentinel()
    {
        using Lr2SongDbSyncTestSupport.TestDatabaseScope scope =
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
        using (var setup = new LR2SongDBExtended(scope.SongDbPath))
        {
            SeedCatalogSentinels(setup, rootDirectoryB, chartPathA, chartPathB, hashA, hashB);
        }

        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false
        };
        ChartScanExecutionResult capturedScan = new()
        {
            ScanSource = ChartScanSource.Everything,
            Success = true,
            IsComplete = true,
            Result = BmsLibraryInitializationTestSupport.CreateScanResult(
                [chartPathA],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectoryA] = []
                },
                [rootDirectoryA])
        };
        var scanner = new DisconnectingChartFileScanner(
            () => Directory.Move(rootDirectoryB, temporarilyUnavailableRoot),
            capturedScan);
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: scanner)
        {
            SearchTargets = [rootDirectoryA, rootDirectoryB],
            BMSFiles =
            [
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathA
                }.WithHashAndFavorite(hashA, 1),
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathB
                }.WithHashAndFavorite(hashB, 2)
            ]
        };

        try
        {
            LibraryDirectoryPreflightException failure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
                () => library.ReloadFileDiff());
            Assert.IsTrue(scanner.ScanEntered);
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.NotFound, failure.Cause);
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, failure.Use);
        }
        finally
        {
            if (Directory.Exists(temporarilyUnavailableRoot))
            {
                Directory.Move(temporarilyUnavailableRoot, rootDirectoryB);
            }
        }

        using var verify = new LR2SongDBExtended(scope.SongDbPath);
        LR2SongDB.song preserved = verify.Table<LR2SongDB.song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preserved);
        Assert.AreEqual("keep-b", preserved.tag);
        Assert.AreEqual(2, preserved.favorite);
        Assert.AreEqual(2, preserved.adddate);
        Assert.IsNotNull(verify.Table<LR2SongDBExtended.bmson_song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase)));
        Assert.IsNotNull(verify.Table<LR2SongDB.folder>().SingleOrDefault(row =>
            string.Equals(
                row?.path,
                Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Reinitialize_WhenRegisteredRootDisappears_PreservesCatalogSentinel()
    {
        using Lr2SongDbSyncTestSupport.TestDatabaseScope scope =
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
        using (var setup = new LR2SongDBExtended(scope.SongDbPath))
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
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner)
        {
            SearchTargets = [rootDirectoryA, rootDirectoryB],
            BMSFiles =
            [
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathA
                }.WithHashAndFavorite(hashA, 1),
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathB
                }.WithHashAndFavorite(hashB, 2)
            ]
        };

        Directory.Move(rootDirectoryB, temporarilyUnavailableRoot);
        try
        {
            LibraryDirectoryPreflightException failure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
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

        using var verify = new LR2SongDBExtended(scope.SongDbPath);
        Assert.IsNotNull(verify.Table<LR2SongDB.song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase)));
        Assert.IsNotNull(verify.Table<LR2SongDBExtended.bmson_song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase)));
        Assert.IsNotNull(verify.Table<LR2SongDB.folder>().SingleOrDefault(row =>
            string.Equals(
                row?.path,
                Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(library.BMSFiles.Any(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ReloadFileDiff_AfterPreflightFailureAndRootRestore_Succeeds()
    {
        using Lr2SongDbSyncTestSupport.TestDatabaseScope scope =
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
        using (var setup = new LR2SongDBExtended(scope.SongDbPath))
        {
            setup.CreateTable<LR2SongDB.song>();
            setup.InsertOrReplace(
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathA,
                    adddate = 1,
                    tag = "keep-a"
                }.WithHashAndFavorite(hashA, 1),
                typeof(LR2SongDB.song));
            setup.InsertOrReplace(
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathB,
                    adddate = 2,
                    tag = "keep-b"
                }.WithHashAndFavorite(hashB, 2),
                typeof(LR2SongDB.song));
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
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner)
        {
            SearchTargets = [rootDirectoryA, rootDirectoryB],
            BMSFiles =
            [
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathA
                }.WithHashAndFavorite(hashA, 1),
                new Lr2SongDbSyncTestSupport.TestableBmsFile
                {
                    path = chartPathB
                }.WithHashAndFavorite(hashB, 2)
            ]
        };

        Directory.Move(rootDirectoryB, temporarilyUnavailableRoot);
        try
        {
            Assert.ThrowsException<LibraryDirectoryPreflightException>(
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

        using var verify = new LR2SongDBExtended(scope.SongDbPath);
        LR2SongDB.song preserved = verify.Table<LR2SongDB.song>().SingleOrDefault(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(preserved);
        Assert.AreEqual("keep-b", preserved.tag);
        Assert.AreEqual(2, preserved.favorite);
        Assert.AreEqual(2, preserved.adddate);
        Assert.IsTrue(library.BMSFiles.Any(row =>
            string.Equals(row?.path, chartPathB, StringComparison.OrdinalIgnoreCase)));
    }

    private static void SeedCatalogSentinels(
        LR2SongDBExtended setup,
        string rootDirectoryB,
        string chartPathA,
        string chartPathB,
        string hashA,
        string hashB)
    {
        setup.CreateTable<LR2SongDB.song>();
        setup.CreateTable<LR2SongDBExtended.bmson_song>();
        setup.CreateTable<LR2SongDB.folder>();
        setup.InsertOrReplace(
            new Lr2SongDbSyncTestSupport.TestableBmsFile
            {
                path = chartPathA,
                adddate = 1,
                tag = "keep-a"
            }.WithHashAndFavorite(hashA, 1),
            typeof(LR2SongDB.song));
        setup.InsertOrReplace(
            new Lr2SongDbSyncTestSupport.TestableBmsFile
            {
                path = chartPathB,
                adddate = 2,
                tag = "keep-b"
            }.WithHashAndFavorite(hashB, 2),
            typeof(LR2SongDB.song));
        setup.InsertOrReplace(
            new LR2SongDBExtended.bmson_song
            {
                path = chartPathB,
                folder = Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                title = "keep-bmson",
                md5 = hashB
            },
            typeof(LR2SongDBExtended.bmson_song));
        setup.InsertOrReplace(
            new LR2SongDB.folder
            {
                path = Lr2SongDbSyncTestSupport.ToFolderPath(rootDirectoryB),
                title = "keep-b-folder",
                type = 1,
                adddate = 2
            },
            typeof(LR2SongDB.folder));
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
