using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationFileScanTests
{
    [TestMethod]
    public void Initialize_StartupPublishesScanAfterLeaseReleaseAndIsolatesTerminalSubscriber()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string lr2RootPath = Path.Combine(scope.DirectoryPath, "LR2");
        string songDbPath = scope.SongDbPath;
        string bmsDirectoryPath = Path.Combine(
            ChartInfoMetadataTestSupport.FindRepoRoot(),
            "BeMusicSeeker.Tests",
            "TestData",
            "chart_info_real",
            "charts",
            "00");
        string chartPath = Path.Combine(
            bmsDirectoryPath,
            "0011a110d2d54f455a1dbb9a03598aaa1cc092238800ca9b1c1260a8bb78db36.bms");

        string configDirectoryPath = Path.Combine(lr2RootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectoryPath);
        string configPath = Path.Combine(configDirectoryPath, "config.xml");
        string escapedBmsDirectoryPath = System.Security.SecurityElement.Escape(
            ToFolderPath(bmsDirectoryPath));
        File.WriteAllText(
            configPath,
            "<config><system><customfolder>0</customfolder><titleflash>24</titleflash></system><jukebox><path>"
            + escapedBmsDirectoryPath
            + "</path></jukebox></config>",
            Encoding.UTF8);
        var lr2Config = new LR2Config(configPath);
        BmsLibraryOptionsSnapshot options = new()
        {
            OperationModeLR2DB = true,
            LR2RootPath = lr2RootPath,
            ScanBmsFilesOnStartup = true,
            UpdateLr2IrRankingCacheOnStartup = false,
            EnableDownloadLr2IrScoreAndDetectUnsent = false,
            UseBeatorajaScoreDb = false,
            EnableReadOptimizedPragmas = false,
            PendingInstallEstimateMaxParallelPackages = 1
        };
        IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
            [chartPath],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [bmsDirectoryPath] = []
            },
            [bmsDirectoryPath]);
        var library = new TestBmsLibrary(
            songDbPath,
            getLR2Config: () => lr2Config,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner);
        library.StartupBackgroundTaskScheduler = (_, _, _, _) => false;

        int scanNotificationObserved = 0;
        bool initializationWriterHeldAtNotification = false;
        bool mutationAdmissionAvailable = false;
        int subscriberFailureObserved = 0;
        int handledNotificationVersion = 0;
        System.ComponentModel.PropertyChangedEventHandler scanNotificationSubscriber = (_, args) =>
        {
            if (!string.Equals(
                    args.PropertyName,
                    nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion),
                    StringComparison.Ordinal))
            {
                return;
            }

            NormalLibraryRefreshNotificationBatch notificationBatch =
                library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            if (notificationBatch.LatestVersion > handledNotificationVersion)
            {
                handledNotificationVersion = notificationBatch.LatestVersion;
            }
            if (notificationBatch.LatestVersion <= 0
                || notificationBatch.ResetsPriorNotifications
                || !notificationBatch.HasRefreshNotification
                || Interlocked.Exchange(ref scanNotificationObserved, 1) != 0)
            {
                return;
            }

            initializationWriterHeldAtNotification |= library.IsWriteLockHeldInitializeAll
                || library.IsWriteLockHeldInitializeMin
                || library.IsWriteLockHeldInitializeBMSFiles;
            using LibraryFileMutationLease reentryLease = library.TryBeginLibraryFileMutation(
                "test_initialize_startup_post_lease_reentry",
                showMessage: false);
            mutationAdmissionAvailable |= reentryLease != null;
            if (Interlocked.Exchange(ref subscriberFailureObserved, 1) == 0)
            {
                throw new InvalidOperationException("forced initialize catalog subscriber failure");
            }
        };
        library.PropertyChanged += scanNotificationSubscriber;
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
        }
        finally
        {
            library.PropertyChanged -= scanNotificationSubscriber;
        }

        Assert.IsTrue(Volatile.Read(ref scanNotificationObserved) != 0);
        Assert.IsFalse(initializationWriterHeldAtNotification);
        Assert.IsTrue(mutationAdmissionAvailable);
        Assert.IsTrue(Volatile.Read(ref subscriberFailureObserved) != 0);
        Assert.IsTrue(library.BMSFiles.Any(file =>
            string.Equals(file?.path, chartPath, StringComparison.OrdinalIgnoreCase)));

        var synchronizationOwner =
            (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        Lr2SongDbSyncInput scanInput = synchronizationOwner.CreateLr2SongDbSyncInput();
        Assert.IsTrue(scanInput.ScanSurfaceGeneration > 0);
        Assert.AreEqual(library.OwnedChartCollectionVersion, scanInput.OwnedChartCollectionVersion);
        Assert.IsNotNull(synchronizationOwner.CommittedPathReceipt);
        Assert.AreEqual(scanInput.BmsRowsVersion, synchronizationOwner.CommittedPathReceipt.BmsRowsVersion);

        using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
        Assert.IsTrue(verify.Table<BMSFile>().Any(row =>
            string.Equals(row?.path, chartPath, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Initialize_StartupParseFailureStillOpensCatalogFileMutationAdmission()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string chartDirectoryPath = Path.Combine(scope.DirectoryPath, "InvalidBmson");
        Directory.CreateDirectory(chartDirectoryPath);
        string invalidBmsonPath = Path.Combine(chartDirectoryPath, "invalid.bmson");
        File.WriteAllText(invalidBmsonPath, "{\"version\":\"1.0.0\",\"info\":");
        BmsLibraryOptionsSnapshot options = new()
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = true,
            UpdateLr2IrRankingCacheOnStartup = false,
            EnableDownloadLr2IrScoreAndDetectUnsent = false,
            UseBeatorajaScoreDb = false,
            EnableReadOptimizedPragmas = false,
            PendingInstallEstimateMaxParallelPackages = 1
        };
        IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
            [invalidBmsonPath],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [chartDirectoryPath] = []
            },
            [chartDirectoryPath]);
        var dialogService = new RecordingDialogService();
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner,
            dialogService: dialogService)
        {
            SearchTargets = [chartDirectoryPath],
            StartupBackgroundTaskScheduler = (_, _, _, _) => false
        };

        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);

            Assert.AreEqual(0, library.BmsonSongs.Count);
            int dialogCountBeforeMutation = dialogService.Calls.Count;
            library.RenameBMSFilesExtensions([], ".invalid");
            Assert.AreEqual(dialogCountBeforeMutation, dialogService.Calls.Count);
        }
        finally
        {
            library.RequestShutdown("parse-failure-convergence-test");
        }
    }

    [TestMethod]
    public void Initialize_StartupWithoutFileScanKeepsPlaylistLeaseAvailableAndUsesSettingWarningForCatalogMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        BmsLibraryOptionsSnapshot options = new()
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = false,
            UpdateLr2IrRankingCacheOnStartup = false,
            EnableDownloadLr2IrScoreAndDetectUnsent = false,
            UseBeatorajaScoreDb = false,
            EnableReadOptimizedPragmas = false,
            PendingInstallEstimateMaxParallelPackages = 1
        };
        var dialogService = new RecordingDialogService();
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            dialogService: dialogService)
        {
            StartupBackgroundTaskScheduler = (_, _, _, _) => false
        };

        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);

            using (LibraryFileMutationLease mutationLease = library.TryBeginLibraryFileMutation(
                "test_playlist_owned_mutation_after_scan_skip",
                showMessage: false))
            {
                Assert.IsNotNull(mutationLease);
            }

            int dialogCountBeforeCatalogMutation = dialogService.Calls.Count;
            library.RenameBMSFilesExtensions([], ".invalid");

            Assert.AreEqual(dialogCountBeforeCatalogMutation + 1, dialogService.Calls.Count);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Warn_CatalogFileMutationRequiresStartupScan,
                dialogService.Calls[^1].Message);
        }
        finally
        {
            library.RequestShutdown("playlist-mutation-after-scan-skip-test");
        }
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesPrefetchedScanAndClearsStaleInstallDestination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string newDirectoryPath = Path.Combine(lr2RootPath, "New");
            string staleDirectoryPath = Path.Combine(lr2RootPath, "Stale");
            Directory.CreateDirectory(keepDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            File.WriteAllText(Path.Combine(keepDirectoryPath, "keep.bms"), "#PLAYER 1\r\n#TITLE Keep\r\n");
            File.WriteAllText(Path.Combine(newDirectoryPath, "added.bms"), "#PLAYER 1\r\n#TITLE Added\r\n");

            var keepFile = new TestableBmsFile
            {
                path = Path.Combine(keepDirectoryPath, "keep.bms")
            };
            keepFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            keepFile.date = ToUnixSeconds(File.GetLastWriteTimeUtc(keepFile.path));
            var deletedFile = new TestableBmsFile
            {
                path = Path.Combine(lr2RootPath, "Deleted", "deleted.bms")
            };
            deletedFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            int executeScanCount = 0;
            int catalogProjectionAppliedCount = 0;
            List<ChartFile> cleanupCharts = [ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(keepFile, includeWarningSnapshot: false),
                staleDirectoryPath,
                string.Empty,
                string.Empty,
                [])];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [keepFile, deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    NativeBridgeUsed = true,
                    NativeBridgeMs = 234L,
                    NativeBridgeReason = "everything_bridge_fixed_scan",
                    ManagedDecodeMs = 12L,
                    ManagedMaterializeMs = 7L,
                    BridgeRawBufferBytes = 4096UL,
                    Result = CreateScanResult(
                        [keepFile.path, Path.Combine(newDirectoryPath, "added.bms")],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() },
                            { newDirectoryPath, Array.Empty<string>() }
                        })
                },
                123L,
                delegate
                {
                    Interlocked.Increment(ref executeScanCount);
                    return null;
                },
                null,
                catalogProjectionApplied: _ => catalogProjectionAppliedCount++);
            ProjectCatalogState(result, [keepFile, deletedFile], [], cleanupCharts);

            Assert.AreEqual(0, executeScanCount);
            Assert.IsTrue(result.PrefetchedScanUsed);
            Assert.IsTrue(result.HasDbDiff);
            Assert.AreEqual(1, result.FileDiffParserDegree);
            Assert.AreEqual(1, result.FileDiffPostParseWorkerDegree);
            CollectionAssert.Contains(result.DeletedPaths, deletedFile.path);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(2, result.NextFiles.Count);
            Assert.AreEqual(234L, result.NativeBridgeMs);
            Assert.AreEqual("everything_bridge_fixed_scan", result.NativeBridgeReason);
            Assert.AreEqual(12L, result.ManagedDecodeMs);
            Assert.AreEqual(7L, result.ManagedMaterializeMs);
            Assert.AreEqual(4096UL, result.BridgeRawBufferBytes);
            Assert.AreEqual(1, catalogProjectionAppliedCount);
            ChartFile installDestinationChange = result.ClearedInstallDestinationCharts.Single();
            Assert.AreSame(keepFile, installDestinationChange.GetBmsStorageOwner());
            Assert.IsTrue(string.IsNullOrWhiteSpace(installDestinationChange.InstallDestination));
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), keepDirectoryPath);
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), newDirectoryPath);

            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            List<BMSFile> dbFiles = [.. songDb.Table<BMSFile>()];
            Assert.AreEqual(1, dbFiles.Count);
            Assert.AreEqual(Path.Combine(newDirectoryPath, "added.bms"), dbFiles[0].path);
            List<LR2SongDBExtended.chart_digest_map> digestRows = [.. songDb.Table<LR2SongDBExtended.chart_digest_map>()];
            Assert.AreEqual(1, digestRows.Count);
            Assert.AreEqual(dbFiles[0].hash, digestRows[0].md5);
            Assert.AreEqual(result.AddedFiles[0].sha256, digestRows[0].sha256);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReportsEverythingFallbackMetadata()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            const string fallbackReason = "empty_results_with_roots";
            var dialogService = new RecordingDialogService();
            List<string> logs = [];
            var service = new BmsLibraryInitializationService();

            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    FallbackUsed = true,
                    FallbackReason = fallbackReason,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                dialogService,
                logInstallPerformance: logs.Add,
                logEverythingScan: logs.Add,
                currentBmsonSongs: []);

            Assert.IsTrue(result.ScanFallbackUsed);
            Assert.AreEqual(fallbackReason, result.ScanFallbackReason);
            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.IsTrue(logs.Any(message => message.Contains("fallback_used=true") || message.Contains("fallback=true")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_IncompleteScanSkipsDiffAndKeepsExistingDb()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string existingDirectoryPath = Path.Combine(lr2RootPath, "Existing");
            string existingPath = Path.Combine(existingDirectoryPath, "chart.bms");
            string addedDirectoryPath = Path.Combine(lr2RootPath, "Added");
            string addedPath = Path.Combine(addedDirectoryPath, "added.bms");
            Directory.CreateDirectory(existingDirectoryPath);
            Directory.CreateDirectory(addedDirectoryPath);
            File.WriteAllText(existingPath, "#PLAYER 1\r\n#TITLE Existing\r\n");
            File.WriteAllText(addedPath, "#PLAYER 1\r\n#TITLE Added\r\n");

            var existingFile = new TestableBmsFile
            {
                path = existingPath
            };
            existingFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            existingFile.date = ToUnixSeconds(File.GetLastWriteTimeUtc(existingPath));
            ExecuteSongDbFixtureTransaction(songDbPath, songDbConnection =>
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            bool scanCompleted = false;
            bool fileDiffStarted = false;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = false,
                    IsComplete = false,
                    ErrorReason = "directory_enumeration_failed:" + existingDirectoryPath,
                    IncompleteReason = "directory_enumeration_failed:" + existingDirectoryPath,
                    Result = CreateScanResult(
                        [addedPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { addedDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                currentBmsonSongs: [],
                scanCompleted: () => scanCompleted = true,
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsTrue(scanCompleted);
            Assert.IsFalse(fileDiffStarted);
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);
            Assert.AreEqual(0, result.DeletedBmsonPaths.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual("directory_enumeration_failed:" + existingDirectoryPath, result.ScanFallbackReason);

            // 初期化結果の観測はSELECTだけなので、writer接続を保持せずread-only入口を使います。
            using LR2SongDBExtended songDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", existingPath));
            Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", addedPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_EmptyFallbackScanWithExistingDbSkipsDiffAndKeepsExistingDb()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string existingDirectoryPath = Path.Combine(lr2RootPath, "Existing");
            string existingPath = Path.Combine(existingDirectoryPath, "chart.bms");
            Directory.CreateDirectory(existingDirectoryPath);
            File.WriteAllText(existingPath, "#PLAYER 1\r\n#TITLE Existing\r\n");

            var existingFile = new TestableBmsFile
            {
                path = existingPath
            };
            existingFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            existingFile.date = ToUnixSeconds(File.GetLastWriteTimeUtc(existingPath));
            ExecuteSongDbFixtureTransaction(songDbPath, songDbConnection =>
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            bool scanCompleted = false;
            bool fileDiffStarted = false;
            List<string> logs = [];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    ScanSource = ChartScanSource.Fallback,
                    Success = true,
                    IsComplete = true,
                    FallbackUsed = true,
                    FallbackReason = "everything_not_running",
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                scanCompleted: () => scanCompleted = true,
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsTrue(scanCompleted);
            Assert.IsFalse(fileDiffStarted);
            Assert.IsTrue(result.EmptyScanWithExistingDbSkipped);
            StringAssert.Contains(result.EmptyScanWithExistingDbSkipReason, "empty_scan_with_existing_db");
            StringAssert.Contains(result.EmptyScanWithExistingDbSkipReason, "scanSource=fallback");
            StringAssert.Contains(result.EmptyScanWithExistingDbSkipReason, "fallbackReason=everything_not_running");
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.IsTrue(logs.Any(message => message.Contains("reason=empty_scan_with_existing_db")));

            using LR2SongDBExtended songDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", existingPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_EmptyEverythingScanWithExistingDbSkipsDiffAndKeepsExistingDb()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string existingDirectoryPath = Path.Combine(lr2RootPath, "Existing");
            string existingPath = Path.Combine(existingDirectoryPath, "chart.bms");
            Directory.CreateDirectory(existingDirectoryPath);
            File.WriteAllText(existingPath, "#PLAYER 1\r\n#TITLE Existing\r\n");

            var existingFile = new TestableBmsFile
            {
                path = existingPath
            };
            existingFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            existingFile.date = ToUnixSeconds(File.GetLastWriteTimeUtc(existingPath));
            ExecuteSongDbFixtureTransaction(songDbPath, songDbConnection =>
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            bool fileDiffStarted = false;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    ScanSource = ChartScanSource.Everything,
                    Success = true,
                    IsComplete = true,
                    NativeBridgeUsed = true,
                    NativeBridgeReason = EverythingNative.FixedScanNativeBridgeReason,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                currentBmsonSongs: [],
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsFalse(fileDiffStarted);
            Assert.IsTrue(result.EmptyScanWithExistingDbSkipped);
            StringAssert.Contains(result.EmptyScanWithExistingDbSkipReason, "scanSource=everything");
            Assert.IsFalse(result.EmptyScanWithExistingDbSkipReason.Contains("fallbackReason="));
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);

            using LR2SongDBExtended songDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", existingPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_EmptyFallbackScanWithoutExistingDbAllowsEmptyResult()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            bool scanCompleted = false;
            bool fileDiffStarted = false;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    ScanSource = ChartScanSource.Fallback,
                    Success = true,
                    IsComplete = true,
                    FallbackUsed = true,
                    FallbackReason = "empty_results_with_roots",
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                currentBmsonSongs: [],
                scanCompleted: () => scanCompleted = true,
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsTrue(scanCompleted);
            Assert.IsTrue(fileDiffStarted);
            Assert.IsFalse(result.EmptyScanWithExistingDbSkipped);
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_IncompleteBmsonScanSkipsDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonDirectoryPath = Path.Combine(lr2RootPath, "Bmson");
            string bmsonPath = Path.Combine(bmsonDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(bmsonDirectoryPath);
            File.WriteAllText(bmsonPath, "{\"version\":\"1.0.0\"}");
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            bool scanCompleted = false;
            bool fileDiffStarted = false;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                currentBmsonSongs: [],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = false,
                    IsComplete = false,
                    ErrorReason = "bmson_directory_enumeration_failed:" + bmsonDirectoryPath,
                    IncompleteReason = "bmson_directory_enumeration_failed:" + bmsonDirectoryPath,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsonDirectoryPath, Array.Empty<string>() }
                        })
                },
                scanCompleted: () => scanCompleted = true,
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsTrue(scanCompleted);
            Assert.IsFalse(fileDiffStarted);
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);
            Assert.AreEqual(0, result.DeletedBmsonPaths.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual("bmson_directory_enumeration_failed:" + bmsonDirectoryPath, result.ScanFallbackReason);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReadFailureIsAggregatedWithoutInitializationDialog()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "ReadFailure");
            Directory.CreateDirectory(chartDirectoryPath);
            string missingPath = Path.Combine(chartDirectoryPath, "missing.bms");

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var dialogService = new RecordingDialogService();
            List<string> scanLogs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [missingPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService,
                logEverythingScan: scanLogs.Add,
                currentBmsonSongs: []);

            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(1, result.FileScanFailures.Count);
            Assert.AreEqual(missingPath, result.FileScanFailures[0].Path);
            Assert.AreEqual("bms", result.FileScanFailures[0].ChartKind);
            Assert.AreEqual("read", result.FileScanFailures[0].Stage);
            Assert.IsTrue(scanLogs.Any(message => message.Contains("bms_scan_failed") && message.Contains("stage=read")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_LongPathBmsIsRegisteredAndLr2CompatibilityWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = CreateLongPathDirectory(lr2RootPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "kabukin_______________________________________________________________________________________________________________________________________________________________________________________________________________________________.bms");
            Directory.CreateDirectory(LongPathFileSystem.ToExtendedPath(chartDirectoryPath));
            File.WriteAllText(LongPathFileSystem.ToExtendedPath(bmsPath), CreateValidBmsText("Long Path"), Encoding.ASCII);
            Assert.IsTrue(bmsPath.Length > 260);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var dialogService = new RecordingDialogService();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService,
                currentBmsonSongs: []);

            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.AreEqual(0, result.FileScanFailures.Count);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(bmsPath, result.AddedFiles[0].path);
            Assert.IsTrue(result.AddedFiles[0].Warnings.Contains(ChartWarningKind.Lr2PathTooLong));
            Assert.IsFalse(string.IsNullOrWhiteSpace(BMSFile.DetectEncodingOfBMSFile(bmsPath)));
            BMSFile.ReloadBMSFileWithEncoding(result.AddedFiles[0], "shift_jis");
            Assert.AreEqual("Long Path", result.AddedFiles[0].title);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_LongPathBmsonIsRegistered()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = CreateLongPathDirectory(lr2RootPath);
            string bmsonPath = Path.Combine(chartDirectoryPath, "long_bmson____________________________________________________________________________________________________________________________________________________________________________________________________________________________.bmson");
            Directory.CreateDirectory(LongPathFileSystem.ToExtendedPath(chartDirectoryPath));
            File.WriteAllText(LongPathFileSystem.ToExtendedPath(bmsonPath), CreateBmsonJson("Long Bmson", "", "", "Artist", "Genre", 5, "beat-7k"), Encoding.UTF8);
            Assert.IsTrue(bmsonPath.Length > 260);

            ExecuteSongDbFixtureTransaction(songDbPath, songDbConnection =>
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            });

            var dialogService = new RecordingDialogService();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService,
                currentBmsonSongs: []);

            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.AreEqual(0, result.FileScanFailures.Count);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual(bmsonPath, result.AddedBmsonSongs[0].path);
            Assert.AreEqual("Long Bmson", result.AddedBmsonSongs[0].title);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", bmsonPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BmsonReadFailureIsAggregatedWithoutInitializationDialog()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "BmsonReadFailure");
            Directory.CreateDirectory(chartDirectoryPath);
            string missingPath = Path.Combine(chartDirectoryPath, "missing.bmson");

            ExecuteSongDbFixtureTransaction(songDbPath, songDbConnection =>
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            });

            var dialogService = new RecordingDialogService();
            List<string> scanLogs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [missingPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService,
                logEverythingScan: scanLogs.Add,
                currentBmsonSongs: []);

            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.FileScanFailures.Count);
            Assert.AreEqual(missingPath, result.FileScanFailures[0].Path);
            Assert.AreEqual("bmson", result.FileScanFailures[0].ChartKind);
            Assert.AreEqual("read", result.FileScanFailures[0].Stage);
            Assert.IsTrue(scanLogs.Any(message => message.Contains("bmson_scan_failed") && message.Contains("stage=read")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ClearsStaleBmsonInstallDestination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepBmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            string staleDirectoryPath = Path.Combine(lr2RootPath, "Stale");
            Directory.CreateDirectory(Path.GetDirectoryName(keepBmsonPath)!);
            File.WriteAllText(keepBmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));

            LR2SongDBExtended.bmson_song keepSong = BmsonSongParser.Parse(keepBmsonPath);
            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepSong, typeof(LR2SongDBExtended.bmson_song));
            });

            List<ChartFile> cleanupCharts = [ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(keepSong, includeWarningSnapshot: false),
                staleDirectoryPath,
                string.Empty,
                string.Empty,
                [])];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [keepSong],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepBmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepBmsonPath)!, Array.Empty<string>() }
                        })
                });
            ProjectCatalogState(result, [], [keepSong], cleanupCharts);

            ChartFile installDestinationChange = result.ClearedInstallDestinationCharts.Single();
            Assert.AreSame(keepSong, installDestinationChange.GetBmsonStorageOwner());
            Assert.IsTrue(string.IsNullOrWhiteSpace(installDestinationChange.InstallDestination));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CatalogProjectionCallbackRunsBeforeBreakdownLog()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            List<string> events = [];
            var service = new BmsLibraryInitializationService();
            service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                logInstallPerformance: message => events.Add(message),
                catalogProjectionApplied: projection =>
                {
                    projection.ApplyMs = 17;
                    projection.InstlDstCleanupMs = 23;
                    events.Add("catalog_projection");
                });

            int projectionIndex = events.IndexOf("catalog_projection");
            int breakdownIndex = events.FindIndex(message => message.StartsWith("song_tbl_file_check_breakdown", StringComparison.Ordinal));
            Assert.IsTrue(projectionIndex >= 0);
            Assert.IsTrue(breakdownIndex > projectionIndex);
            StringAssert.Contains(events[breakdownIndex], "apply_ms=17");
            StringAssert.Contains(events[breakdownIndex], "instl_dst_cleanup_ms=23");
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReportsCombinedBmsAndBmsonParseProgress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Added");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            string bmsonPath = Path.Combine(chartDirectoryPath, "added.bmson");
            File.WriteAllText(bmsPath, CreateValidBmsText("Added"), Encoding.ASCII);
            File.WriteAllText(Path.Combine(chartDirectoryPath, "sound.wav"), string.Empty, Encoding.ASCII);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Added", "", "", "Artist", "Genre", 7, "beat-7k"));

            ExecuteSongDbFixtureTransaction(songDbPath, songDbConnection =>
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            });

            int scanCompletedCount = 0;
            int fileDiffStartedCount = 0;
            object progressLock = new();
            List<(int Total, int Processed, string Path)> progress = [];
            List<string> logs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath, bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, ["sound.wav"] }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: message => logs.Add(message),
                currentBmsonSongs: [],
                scanCompleted: () => scanCompletedCount++,
                fileDiffStarted: () => fileDiffStartedCount++,
                reportParseProgress: (total, processed, path) =>
                {
                    lock (progressLock)
                    {
                        progress.Add((total, processed, path));
                    }
                });

            Assert.AreEqual(1, scanCompletedCount);
            Assert.AreEqual(1, fileDiffStartedCount);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(1, result.FileDiffParserDegree);
            Assert.AreEqual(result.NewFileParseMs, result.BmsParseMs);
            Assert.IsTrue(result.BmsonParseMs >= 0);
            long expectedReadBytesEstimate = new FileInfo(bmsPath).Length + new FileInfo(bmsonPath).Length;
            Assert.AreEqual(expectedReadBytesEstimate, result.ParseReadBytesEstimate);
            Assert.AreEqual(1, result.DbCommitChunks);
            Assert.AreEqual(10000, result.DbCommitChunkSize);
            Assert.IsTrue(result.DbCommitMaxChunkMs >= 0);
            Assert.IsTrue(result.DbCommitApplyMs >= 0);
            Assert.IsTrue(result.DbCommitBmsUpsertMs >= 0);
            Assert.IsTrue(result.DbCommitMaintenanceUpsertMs >= 0);
            Assert.IsTrue(result.DbCommitChartInfoMs >= 0);
            Assert.IsTrue(result.DbCommitSqliteCommitMs >= 0);
            Assert.AreEqual(1, result.DbCommitBmsChangedCount);
            Assert.IsTrue(result.FileDiffReadMs >= 0);
            Assert.IsTrue(result.FileDiffParseMs >= 0);
            Assert.IsTrue(result.SnapshotQueueHighWatermark > 0);
            Assert.AreEqual(2048, result.InlineChartInfoBatchSize);
            Assert.AreEqual(ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, 2), result.FileDiffReaderDegree);
            Assert.AreEqual(ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(result.FileDiffParserDegree, result.FileDiffReaderDegree), result.ReadQueueCapacity);
            Assert.AreEqual(FileScanParseCommitOwner.ResolveFileDiffParsedQueueCapacity(result.FileDiffParserDegree), result.ParsedQueueCapacity);
            Assert.AreEqual(FileScanParseCommitOwner.ResolveFileDiffPostParseQueueCapacity(result.FileDiffPostParseWorkerDegree), result.PostParseQueueCapacity);
            Assert.AreEqual(1, result.CommitQueueCapacity);
            Assert.AreEqual(2, result.CommitWriterQueueCapacity);
            Assert.IsTrue(result.CommitStreamingEnabled);
            Assert.AreEqual("none", result.CommitStreamingBarrierReason);
            Assert.AreEqual(1, result.FileDiffPostParseWorkerDegree);
            Assert.IsTrue(result.PostParseOutputWaitMs >= 0);
            Assert.IsTrue(result.CommitWriterQueueWaitMs >= 0);
            Assert.AreEqual(2, result.PostParseWorkItemCount);
            Assert.AreEqual(1, result.InlineMaintenanceDegree);
            Assert.IsTrue(result.PostParseWallMs >= 0);
            Assert.IsTrue(result.InlineChartInfoWallMs >= 0);
            Assert.IsTrue(result.InlineMaintenanceWallMs >= 0);
            Assert.IsTrue(result.InlineMaintenanceResourceIndexHitCount > 0);
            Assert.IsTrue(progress.Any(item => item.Total == 2 && item.Processed == 0));
            Assert.IsTrue(progress.Any(item => item.Total == 2 && item.Processed == 2));
            Assert.IsTrue(progress.All(item => item.Total == 2));
            Assert.IsTrue(logs.Any(message => message.Contains("bms_added_target_count=1")
                && message.Contains("bmson_upsert_target_count=1")
                && message.Contains("file_diff_reader_degree=" + result.FileDiffReaderDegree)
                && message.Contains("file_diff_parser_degree=1")
                && message.Contains("file_diff_post_parse_worker_degree=1")
                && message.Contains("read_queue_capacity=" + result.ReadQueueCapacity)
                && message.Contains("parsed_queue_capacity=" + result.ParsedQueueCapacity)
                && message.Contains("post_parse_queue_capacity=" + result.PostParseQueueCapacity)
                && message.Contains("commit_queue_capacity=1")
                && message.Contains("commit_writer_queue_capacity=2")
                && message.Contains("commit_streaming_enabled=true")
                && message.Contains("commit_streaming_barrier=none")
                && message.Contains("post_parse_output_wait_ms=")
                && message.Contains("commit_writer_queue_wait_ms=")
                && message.Contains("post_parse_work_item_count=2")
                && message.Contains("inline_chart_info_target_count=2")
                && message.Contains("inline_chart_info_batch_size=2048")
                && message.Contains("inline_maintenance_degree=1")
                && message.Contains("inline_maintenance_resource_index_hit=")
                && message.Contains("inline_maintenance_resource_set_cache_hit=")
                && message.Contains("inline_maintenance_resource_set_cache_entries=")
                && message.Contains("parse_read_bytes_estimate=" + expectedReadBytesEstimate)
                && message.Contains("db_commit_apply_ms=")
                && message.Contains("db_commit_bms_upsert_ms=")
                && message.Contains("db_commit_maintenance_upsert_ms=")
                && message.Contains("db_commit_sqlite_commit_ms=")
                && message.Contains("db_commit_chunks=1")
                && message.Contains("db_commit_chunk_size=10000")));
            Assert.IsTrue(logs.Any(message => message.Contains("song_tbl_file_check db_commit_chunk_done chunk=1")
                && message.Contains("applyMs=")
                && message.Contains("bmsUpsertMs=")
                && message.Contains("bmsChanged=1")
                && message.Contains("maintenanceUpsertMs=")
                && message.Contains("chartInfoMs=")
                && message.Contains("sqliteCommitMs=")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_PublishesCommittedBmsReceiptPathsAfterSuccessfulChunkCommit()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Receipt");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "committed.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Receipt"), Encoding.ASCII);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            SongTableFileCheckResult result = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1).ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [chartDirectoryPath] = []
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            CollectionAssert.AreEquivalent(new[] { bmsPath }, result.CommittedLr2SongDbSyncBmsPaths.ToArray());
            using LR2SongDBExtended verifyConnection = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verifyConnection.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CommitsChunksThroughPostParseWriter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "ManyAdded");
            Directory.CreateDirectory(chartDirectoryPath);
            List<string> paths = [];
            for (int i = 0; i < 120; i++)
            {
                string path = Path.Combine(chartDirectoryPath, "added-" + i.ToString("D4") + ".bms");
                File.WriteAllText(path, CreateValidBmsText("Added " + i.ToString("D4")), Encoding.ASCII);
                paths.Add(path);
            }

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            List<string> events = [];
            object eventLock = new();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, inlineChartInfoBatchSizeOverride: 32, fileDiffCommitChunkSizeOverride: 50);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        paths,
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: delegate (string message)
                {
                    lock (eventLock)
                    {
                        if (message.Contains("song_tbl_file_check db_commit_chunk_done"))
                        {
                            events.Add(message);
                        }
                    }
                },
                reportParseProgress: delegate (int total, int processed, string path)
                {
                    lock (eventLock)
                    {
                        events.Add("progress " + processed + "/" + total);
                    }
                });

            Assert.AreEqual(120, result.AddedFiles.Count);
            Assert.AreEqual(120, result.DbCommitBmsChangedCount);
            Assert.AreEqual((int)Math.Ceiling(paths.Count / 50.0), result.DbCommitChunks);
            Assert.AreEqual(50, result.DbCommitChunkSize);
            Assert.AreEqual(32, result.InlineChartInfoBatchSize);
            Assert.AreEqual(120, result.PostParseWorkItemCount);
            Assert.IsTrue(result.PostParseWallMs >= 0);
            Assert.IsTrue(result.CommitQueueWaitMs >= 0);
            int firstCommitIndex = events.FindIndex(item => item.Contains("db_commit_chunk_done chunk=1"));
            int finalProgressIndex = events.FindIndex(item => item == "progress 120/120");
            int firstCommitChunkProgressIndex = events.FindIndex(item => item == "progress 50/120");
            List<string> progressEvents = [.. events.Where(item => item.StartsWith("progress ", StringComparison.Ordinal))];
            Assert.IsTrue(firstCommitIndex >= 0, "first commit chunk log was not recorded.");
            Assert.AreEqual(121, progressEvents.Count, "file diff progress should be reported for the initial state and each prepared chart.");
            Assert.IsTrue(progressEvents.Contains("progress 1/120"), "first prepared chart progress was not recorded.");
            Assert.IsTrue(progressEvents.Contains("progress 119/120"), "near-final per-chart progress was not recorded.");
            Assert.IsTrue(finalProgressIndex >= 0, "final parse progress was not recorded.");
            Assert.IsTrue(firstCommitChunkProgressIndex >= 0, "first commit chunk prepared progress was not recorded.");
            Assert.IsTrue(firstCommitChunkProgressIndex < firstCommitIndex, "file diff progress should report DB-ready rows before their commit completion.");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(120L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(120L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(120L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AggregatesInlineRowsAcrossPostParseWorkers()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "ParallelPostParse");
            Directory.CreateDirectory(chartDirectoryPath);
            const int count = 300;
            List<string> paths = [];
            for (int i = 0; i < count; i++)
            {
                string path = Path.Combine(chartDirectoryPath, "added-" + i.ToString("D4") + ".bms");
                File.WriteAllText(path, CreateValidBmsText("Parallel PostParse " + i.ToString("D4")), Encoding.ASCII);
                paths.Add(path);
            }

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(paths[0]);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos([CreateMinimalChartInfoRow(firstSnapshot.Sha256, firstSnapshot.Md5)]);
            List<string> logs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 2, inlineChartInfoBatchSizeOverride: 512, fileDiffCommitChunkSizeOverride: 50);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                gateway,
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        paths,
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add);

            Assert.AreEqual(count, result.AddedFiles.Count);
            Assert.AreEqual(2, result.FileDiffParserDegree);
            Assert.AreEqual(2, result.FileDiffPostParseWorkerDegree);
            Assert.AreEqual(count, result.PostParseWorkItemCount);
            Assert.AreEqual((int)Math.Ceiling(count / 50.0), result.DbCommitChunks, "unexpected DB commit chunk count: " + result.DbCommitChunks);
            Assert.AreEqual(50, result.DbCommitChunkSize);
            Assert.AreEqual(count, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoCurrentSkippedCount);
            Assert.AreEqual(count, result.InlineMaintenanceSuccessCount);
            Assert.AreEqual(1, result.InlineMaintenanceResourceSetCacheEntries);
            Assert.IsTrue(result.PostParseOutputWaitMs >= 0);
            Assert.IsTrue(logs.Any(message => message.Contains("file_diff_chart_info_snapshot")
                && message.Contains("status=loaded")));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(count, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(count, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(count, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM maintenance;"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_StreamingWriterFailureDoesNotBlockPipeline()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "WriterFailure");
            Directory.CreateDirectory(chartDirectoryPath);
            List<string> paths = [];
            for (int i = 0; i < 160; i++)
            {
                string path = Path.Combine(chartDirectoryPath, "added-" + i.ToString("D4") + ".bms");
                File.WriteAllText(path, CreateValidBmsText("Writer Failure " + i.ToString("D4")), Encoding.ASCII);
                paths.Add(path);
            }

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 2, inlineChartInfoBatchSizeOverride: 8, fileDiffCommitChunkSizeOverride: 8);
            var injectedException = new InvalidOperationException("injected streaming writer failure");
            Task<SongTableFileCheckResult> task = Task.Run(() => service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        paths,
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: delegate (string message)
                {
                    if (message.Contains("song_tbl_file_check db_commit_chunk_start chunk=1"))
                    {
                        throw injectedException;
                    }
                }));

            bool completed;
            try
            {
                completed = task.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
                completed = true;
            }
            Assert.IsTrue(completed, "file diff pipeline did not complete after streaming writer failure.");
            Assert.IsTrue(task.IsFaulted, "streaming writer failure should fault the pipeline.");
            StringAssert.Contains(task.Exception.ToString(), injectedException.Message);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsWithLr2FolderAndParentHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Lr2Crc");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("CRC Added"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);
            ProjectCatalogState(result, []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            BMSFile added = result.AddedFiles[0];
            Assert.IsFalse(string.IsNullOrWhiteSpace(added.folder));
            Assert.IsFalse(string.IsNullOrWhiteSpace(added.parent));
            Assert.IsTrue(added.parent.Length <= 8);
            Assert.IsFalse(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            Assert.AreEqual(0, result.NextFiles.Count(file => string.IsNullOrWhiteSpace(file.parent)));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(added.folder, verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(added.parent, verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_PreservesShiftJisUnsupportedPathWithoutParentAndWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Emoji😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Emoji Added"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);
            ProjectCatalogState(result, []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            BMSFile added = result.AddedFiles[0];
            Assert.IsTrue(string.IsNullOrWhiteSpace(added.folder));
            Assert.IsTrue(string.IsNullOrWhiteSpace(added.parent));
            Assert.IsTrue(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            StringAssert.Contains(added.Warnings.BuildTooltipText(), "Shift_JIS");
            Assert.AreEqual(1, result.NextFiles.Count(file => string.IsNullOrWhiteSpace(file.parent)));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath)));
        });
    }

    [TestMethod]
    public void SQLiteConnectionEx_ReadOptimizedPragmas_AppliesAndReportsState()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "bemusicseeker-pragmas-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var disabled = new SQLiteConnectionEx(dbPath))
            {
                List<string> disabledLogs = disabled.TryApplyReadOptimizedPragmas(enabled: false);
                CollectionAssert.AreEqual(new[] { "enabled=false" }, disabledLogs);
            }

            using var enabled = new SQLiteConnectionEx(dbPath);
            List<string> enabledLogs = enabled.TryApplyReadOptimizedPragmas(enabled: true);
            Assert.AreEqual(3, enabledLogs.Count);
            Assert.AreEqual("temp_store=MEMORY:ok", enabledLogs[0]);
            Assert.AreEqual("cache_size=-262144:ok", enabledLogs[1]);
            StringAssert.StartsWith(enabledLogs[2], "mmap_size=2147483648:ok(");
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [TestMethod]
    public void SongTableFileCheckResult_ReleasePostApplyTransientBuffers_ClearsTransientListsOnly()
    {
        var result = new SongTableFileCheckResult();
        var added = new BMSFile();
        var addedBmson = new LR2SongDBExtended.bmson_song();
        var next = new BMSFile();
        var nextBmson = new LR2SongDBExtended.bmson_song();
        result.Pragmas.Add("pragma");
        result.AddedFiles.Add(added);
        result.AddedBmsonSongs.Add(addedBmson);
        result.InlineChartInfoRows.Add(new LR2SongDBExtended.chart_info());
        result.InlineChartInfoAppliedRows.Add(new LR2SongDBExtended.chart_info());
        result.InlineChartInfoParseFailureRows.Add(new LR2SongDBExtended.chart_info_parse_failure());
        result.InlineChartInfoParseFailureDeleteMd5s.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        result.FileScanFailures.Add(new ChartFileScanFailure("failed.bms", "bms", "read", "IOException", "failed"));
        result.DeletedPaths.Add("deleted.bms");
        result.DeletedBmsonPaths.Add("deleted.bmson");
        result.ClearedInstallDestinationCharts.Add(
            ChartFileProjection.FromBmsFile(new BMSFile { path = "cleared.bms" }));
        result.NextFiles.Add(next);
        result.NextBmsonSongs.Add(nextBmson);
        result.NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache();

        result.ReleasePostApplyTransientBuffers();

        Assert.AreEqual(0, result.Pragmas.Count);
        Assert.AreEqual(0, result.AddedFiles.Count);
        Assert.AreEqual(0, result.AddedBmsonSongs.Count);
        Assert.AreEqual(0, result.InlineChartInfoRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoParseFailureRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoParseFailureDeleteMd5s.Count);
        Assert.AreEqual(0, result.FileScanFailures.Count);
        Assert.AreEqual(0, result.DeletedPaths.Count);
        Assert.AreEqual(0, result.DeletedBmsonPaths.Count);
        Assert.AreEqual(0, result.ClearedInstallDestinationCharts.Count);
        Assert.AreEqual(1, result.NextFiles.Count);
        Assert.AreSame(next, result.NextFiles[0]);
        Assert.AreEqual(1, result.NextBmsonSongs.Count);
        Assert.AreSame(nextBmson, result.NextBmsonSongs[0]);
        Assert.IsNotNull(result.NextDirectoryResourceLookupCache);
    }

    [TestMethod]
    public void ApplyFileScanDiff_InvalidBmsonLogsAndReportsProgress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InvalidBmson");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsonPath = Path.Combine(chartDirectoryPath, "invalid.bmson");
            File.WriteAllText(bmsonPath, "{\"version\":\"1.0.0\",\"info\":");

            ExecuteSongDbFixtureTransaction(songDbPath, songDbConnection =>
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            });

            object progressLock = new();
            List<(int Total, int Processed, string Path)> progress = [];
            List<string> logs = [];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logEverythingScan: message => logs.Add(message),
                currentBmsonSongs: [],
                reportParseProgress: (total, processed, path) =>
                {
                    lock (progressLock)
                    {
                        progress.Add((total, processed, path));
                    }
                });

            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(1, result.FileScanFailures.Count);
            Assert.AreEqual("parse", result.FileScanFailures[0].Stage);
            Assert.AreEqual(new FileInfo(bmsonPath).Length, result.ParseReadBytesEstimate);
            Assert.IsTrue(progress.Any(item => item.Total == 1 && item.Processed == 0));
            Assert.IsTrue(progress.Any(item => item.Total == 1 && item.Processed == 1 && string.Equals(item.Path, bmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(logs.Any(message => message.Contains("bmson_scan_failed") && message.Contains("stage=parse") && message.Contains("invalid.bmson")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BuildsCachesDirectlyFromScanHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string chartPath = Path.Combine(chartDirectoryPath, "keep.bms");
            Directory.CreateDirectory(chartDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Keep\r\n");

            var keepFile = new TestableBmsFile
            {
                path = chartPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(chartPath).hash);

            uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\sound");
            uint imageRelativeHash = ChartResourceKeyHash.GetLookupHash("bg");

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = new ChartScanResult
                    {
                        ChartFilePaths = new HashSet<string>(StringComparer.Ordinal) { chartPath },
                        ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDirectoryPath },
                        AudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { audioRelativeHash } }
                        },
                        ImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { imageRelativeHash } }
                        },
                        MovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        },
                        SelfOwnedAudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { audioRelativeHash } }
                        },
                        SelfOwnedImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { imageRelativeHash } }
                        },
                        SelfOwnedMovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        }
                    }
                },
                0L,
                () => null,
                null);

            DirectoryResourceLookupCache.Entry entry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(chartDirectoryPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(1, entry.AudioFileNameHashCount);
            Assert.AreEqual(1, entry.ImageFileNameHashCount);
            Assert.AreEqual(0, entry.MovieFileNameHashCount);
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(audioRelativeHash));
            Assert.IsTrue(entry.ImageRelativePathHashes.Contains(imageRelativeHash));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_MergedScanHashesAreSortedDistinctAndSearchable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Merged");
            uint firstHash = 30u;
            uint sharedHash = 20u;
            uint lastHash = 10u;

            ChartScanResult CreateResult(uint[] hashes) => new()
            {
                ChartDirectories = new HashSet<string>([chartDirectoryPath], StringComparer.OrdinalIgnoreCase),
                AudioRelativePathHashesByChartDirectory =
                    new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [chartDirectoryPath] = hashes
                    }
            };

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateResult([firstHash, sharedHash])
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateResult([sharedHash, lastHash])
                });

            DirectoryResourceLookupCache.Entry entry =
                result.NextDirectoryResourceLookupCache.GetEntryOrNull(chartDirectoryPath);
            CollectionAssert.AreEqual(
                new uint[] { lastHash, sharedHash, firstHash },
                entry.AudioRelativePathHashArray);
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(firstHash));
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(sharedHash));
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(lastHash));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesNativeResourceIndexWithoutMaterializingScanHashMaps()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string chartPath = Path.Combine(chartDirectoryPath, "keep.bms");
            Directory.CreateDirectory(chartDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Keep\r\n");

            var keepFile = new TestableBmsFile
            {
                path = chartPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(chartPath).hash);

            uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\sound");
            uint movieRelativeHash = ChartResourceKeyHash.GetLookupHash("movie");
            var nativeIndex = LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                [chartDirectoryPath],
                [[audioRelativeHash]],
                [[]],
                [[movieRelativeHash]],
                [[audioRelativeHash]],
                [[]],
                [[movieRelativeHash]],
                new Dictionary<uint, string[]> { { audioRelativeHash, new[] { chartDirectoryPath } } },
                new Dictionary<uint, string[]>(),
                new Dictionary<uint, string[]> { { movieRelativeHash, new[] { chartDirectoryPath } } });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    NativeBridgeReason = EverythingNative.FixedScanNativeBridgeReason,
                    AudioResourceKeyHashCount = 1,
                    MovieResourceKeyHashCount = 1,
                    ResourceIndex = nativeIndex,
                    Result = new ChartScanResult
                    {
                        ChartFilePaths = new HashSet<string>(StringComparer.Ordinal) { chartPath },
                        ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDirectoryPath }
                    }
                },
                0L,
                () => null,
                null);

            DirectoryResourceLookupCache.Entry entry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(chartDirectoryPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(1, entry.AudioFileNameHashCount);
            Assert.AreEqual(0, entry.ImageFileNameHashCount);
            Assert.AreEqual(1, entry.MovieFileNameHashCount);
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(audioRelativeHash));
            Assert.IsTrue(entry.MovieRelativePathHashes.Contains(movieRelativeHash));
            Assert.AreEqual(1ul, result.AudioResourceKeyHashEntryCount);
            Assert.AreEqual(0ul, result.ImageResourceKeyHashEntryCount);
            Assert.AreEqual(1ul, result.MovieResourceKeyHashEntryCount);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_RemovesOrphanChartDigestRowsForDeletedSongs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string deletedChartPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(deletedChartPath)!);
            File.WriteAllText(deletedChartPath, "#PLAYER 1\r\n#TITLE Deleted\r\n");

            var deletedFile = new TestableBmsFile
            {
                path = deletedChartPath
            };
            var source = BMSFile.CreateBMSFileFromFile(deletedChartPath);
            deletedFile.SetHash(source.hash);
            deletedFile.SetSha256(source.sha256);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(deletedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = deletedFile.hash,
                    sha256 = deletedFile.sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null);

            CollectionAssert.Contains(result.DeletedPaths, deletedChartPath);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + deletedFile.hash + "';"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_KeepsSharedChartDigestRowsWhenAnotherSongStillUsesSameMd5()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepChartPath = Path.Combine(lr2RootPath, "Keep", "keep.bms");
            string deletedChartPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(keepChartPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(deletedChartPath)!);
            File.WriteAllText(keepChartPath, "#PLAYER 1\r\n#TITLE Same\r\n");
            File.Copy(keepChartPath, deletedChartPath, overwrite: true);

            var sourceKeep = BMSFile.CreateBMSFileFromFile(keepChartPath);
            var sourceDeleted = BMSFile.CreateBMSFileFromFile(deletedChartPath);
            var keepFile = new TestableBmsFile
            {
                path = keepChartPath
            };
            keepFile.SetHash(sourceKeep.hash);
            keepFile.SetSha256(sourceKeep.sha256);
            var deletedFile = new TestableBmsFile
            {
                path = deletedChartPath
            };
            deletedFile.SetHash(sourceDeleted.hash);
            deletedFile.SetSha256(sourceDeleted.sha256);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(deletedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = keepFile.hash,
                    sha256 = keepFile.sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [keepFile, deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepChartPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepChartPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);

            CollectionAssert.Contains(result.DeletedPaths, deletedChartPath);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + keepFile.hash + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartDigests_ComputesOnlyMissingHashesAndPersistsThem()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartAPath = Path.Combine(lr2RootPath, "Songs", "a.bms");
            string chartBPath = Path.Combine(lr2RootPath, "Songs", "b.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartAPath)!);
            File.WriteAllText(chartAPath, "#PLAYER 1\r\n#TITLE A\r\n");
            File.WriteAllText(chartBPath, "#PLAYER 1\r\n#TITLE B\r\n");

            var chartA = new TestableBmsFile
            {
                path = chartAPath
            };
            chartA.SetHash(BMSFile.CreateBMSFileFromFile(chartAPath).hash);
            var chartB = new TestableBmsFile
            {
                path = chartBPath
            };
            chartB.SetHash(BMSFile.CreateBMSFileFromFile(chartBPath).hash);
            chartB.SetSha256(new string('c', 64));

            var service = new BmsLibraryInitializationService();
            List<(int Total, int Processed, string Path)> progress = [];
            ChartDigestBackfillResult result = service.BackfillChartDigests(
                new BmsLibraryDbGateway(songDbPath),
                [chartA, chartB],
                (total, processed, path) => progress.Add((total, processed, path)));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(chartA.sha256));
            Assert.AreEqual(new string('c', 64), chartB.sha256);
            Assert.IsTrue(progress.Any((item) => item.Total == 1 && item.Processed == 1));

            using LR2SongDBExtended songDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            List<LR2SongDBExtended.chart_digest_map> rows = [.. songDb.Table<LR2SongDBExtended.chart_digest_map>()];
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(chartA.hash, rows[0].md5);
            Assert.AreEqual(chartA.sha256, rows[0].sha256);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_TracksBmsonAddsDeletesAndUpdatesDatabase()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepBmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            string addedBmsonPath = Path.Combine(lr2RootPath, "Added", "added.bmson");
            string deletedBmsonPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(keepBmsonPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(addedBmsonPath)!);
            File.WriteAllText(keepBmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));
            File.WriteAllText(addedBmsonPath, CreateBmsonJson("Added", "", "", "Artist", "Genre", 7, "beat-7k"));

            LR2SongDBExtended.bmson_song keepSong = BmsonSongParser.Parse(keepBmsonPath);
            var deletedSong = new LR2SongDBExtended.bmson_song
            {
                path = deletedBmsonPath,
                folder = Path.GetDirectoryName(deletedBmsonPath),
                title = "Deleted",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = DateTime.UtcNow.AddDays(-1)
            };
            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(deletedSong, typeof(LR2SongDBExtended.bmson_song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [keepSong, deletedSong],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepBmsonPath, addedBmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepBmsonPath)!, Array.Empty<string>() },
                            { Path.GetDirectoryName(addedBmsonPath)!, Array.Empty<string>() }
                        })
                });
            ProjectCatalogState(result, [], [keepSong, deletedSong]);

            CollectionAssert.Contains(result.DeletedBmsonPaths, deletedBmsonPath);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.IsTrue(result.AddedBmsonSongs[0].HasFreshResourceReferences);
            Assert.AreEqual(2, result.NextBmsonSongs.Count);
            Assert.IsTrue(result.NextBmsonSongs.Any(song => string.Equals(song.path, keepBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.NextBmsonSongs.Any(song => string.Equals(song.path, addedBmsonPath, StringComparison.OrdinalIgnoreCase)));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            List<LR2SongDBExtended.bmson_song> rows = [.. verify.Table<LR2SongDBExtended.bmson_song>()];
            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows.Any(song => string.Equals(song.path, keepBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(rows.Any(song => string.Equals(song.path, addedBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(rows.Any(song => string.Equals(song.path, deletedBmsonPath, StringComparison.OrdinalIgnoreCase)));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UpdatedBmsDateMismatchWithSameMd5UpdatesDateOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "Updated", "same-md5.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath)!);
            File.WriteAllText(bmsPath, CreateValidBmsText("Same Md5"), Encoding.ASCII);
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 12345,
                tag = "keep"
            };
            existingFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);
            existingFile.SetFavorite(1);
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(1, result.NextFiles.Count);
            Assert.AreSame(existingFile, result.NextFiles[0]);
            Assert.AreEqual(ToUnixSeconds(newTimestamp), existingFile.date);
            Assert.AreEqual(12345, existingFile.adddate);
            Assert.AreEqual("keep", existingFile.tag);
            Assert.IsTrue(result.HasDbDiff);
            Assert.IsFalse(result.CommittedLr2SongDbSyncBmsPaths.Any(path =>
                string.Equals(path, bmsPath, StringComparison.OrdinalIgnoreCase)));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(ToUnixSeconds(newTimestamp), row.date);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DateOnlySameMd5RequiresFullFollowUpForGeneratedSongColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectory = Path.Combine(lr2RootPath, "DateOnlyFollowUp");
            Directory.CreateDirectory(chartDirectory);
            string bmsPath = Path.Combine(chartDirectory, "stale-generated.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current Generated"), Encoding.ASCII);
            DateTime oldTimestamp = new(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            DateTime newTimestamp = new(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            var parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 654321,
                tag = "preserve-date-only-user-data"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(6);
            existingFile.title = "Stale Generated Title";
            existingFile.artist = "Stale Generated Artist";
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            SongTableFileCheckResult fileDiffResult = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1).ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true
                },
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [chartDirectory] = []
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(fileDiffResult, [existingFile]);

            Assert.AreEqual(1, fileDiffResult.BmsDateOnlyUpdateCount);
            Assert.IsFalse(fileDiffResult.CommittedLr2SongDbSyncBmsPaths.Any(path =>
                string.Equals(path, bmsPath, StringComparison.OrdinalIgnoreCase)));

            int readCount = 0;
            using var followUpDb = new LR2SongDBExtended(songDbPath);
            Lr2SongDbSyncResult followUpResult = Lr2SongDbSyncService.Run(followUpDb, new Lr2SongDbSyncRequest
            {
                Signature = "date-only-generated-follow-up",
                RunId = "date-only-generated-follow-up",
                SongRows = [existingFile],
                ChartFileBufferReader = path =>
                {
                    Assert.AreEqual(bmsPath, path);
                    Interlocked.Increment(ref readCount);
                    return ChartFileContentReader.ReadBuffer(path);
                },
                ChartInfoChunkWriter = Lr2SongDbSyncTestSupport.CreateDirectChartInfoWriter(followUpDb),
                StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
            });

            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, followUpResult.FinalStage);
            Assert.AreEqual(1, followUpResult.SongRowProcessedCount);
            Assert.AreEqual(0, followUpResult.SongRowSkippedCount);
            Assert.AreEqual(1, readCount);
            Assert.AreEqual("Current Generated", followUpDb.ExecuteScalar<string>(
                "SELECT title FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual("Artist", followUpDb.ExecuteScalar<string>(
                "SELECT artist FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(6, followUpDb.ExecuteScalar<int>(
                "SELECT favorite FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(654321, followUpDb.ExecuteScalar<int>(
                "SELECT adddate FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual("preserve-date-only-user-data", followUpDb.ExecuteScalar<string>(
                "SELECT tag FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ProtectedLegacyMigrationSkipsExistingBmsMetadataRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "Updated", "legacy-date.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath)!);
            File.WriteAllText(bmsPath, CreateValidBmsText("Legacy Date"), Encoding.ASCII);
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 12345,
                tag = "keep"
            };
            existingFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);
            existingFile.SetFavorite(1);
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                protectExistingBmsRowsFromLr2SongDbSyncMigration: true);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsLegacyExistingProtectedCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(1, result.NextFiles.Count);
            Assert.AreSame(existingFile, result.NextFiles[0]);
            Assert.AreEqual(ToUnixSeconds(oldTimestamp), existingFile.date);
            Assert.AreEqual(12345, existingFile.adddate);
            Assert.AreEqual("keep", existingFile.tag);
            Assert.IsFalse(result.HasDbDiff);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(ToUnixSeconds(oldTimestamp), row.date);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ProtectedLegacyMigrationSkipsExistingBmsTextRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectory = Path.Combine(lr2RootPath, "LegacyMigrationText");
            Directory.CreateDirectory(chartDirectory);
            string bmsPath = Path.Combine(chartDirectory, "legacy-date-text.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Legacy Date Text"), Encoding.ASCII);
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            var parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 12345,
                tag = "keep"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(1);
            existingFile.SetTextGroupFlagForTest(0);
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectory, ["readme.txt"] }
                        })
                },
                0L,
                () => null,
                null,
                protectExistingBmsRowsFromLr2SongDbSyncMigration: true);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsLegacyExistingProtectedCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, result.BmsTextOnlyUpdateCount);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreSame(existingFile, result.NextFiles.Single());
            Assert.AreEqual(ToUnixSeconds(oldTimestamp), existingFile.date);
            Assert.AreEqual(0, existingFile.txt);
            Assert.AreEqual(12345, existingFile.adddate);
            Assert.AreEqual("keep", existingFile.tag);
            Assert.IsFalse(result.HasDbDiff);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(ToUnixSeconds(oldTimestamp), row.date);
            Assert.AreEqual(0, row.txt);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UpdatedBmsDateMismatchWithChangedMd5ReplacesCatalog()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "Updated", "changed-md5.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath)!);
            File.WriteAllText(bmsPath, CreateValidBmsText("Old"), Encoding.ASCII);
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            var oldParsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 23456,
                tag = "preserve"
            };
            existingFile.SetHash(oldParsed.hash);
            existingFile.SetFavorite(1);

            File.WriteAllText(bmsPath, CreateValidBmsText("New"), Encoding.ASCII);
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.NextFiles.Count);
            Assert.AreEqual("New", result.NextFiles[0].title);
            Assert.AreEqual(ToUnixSeconds(newTimestamp), result.NextFiles[0].date);
            Assert.AreEqual(23456, result.NextFiles[0].adddate);
            Assert.AreEqual("preserve", result.NextFiles[0].tag);
            Assert.AreNotEqual(oldParsed.hash, result.NextFiles[0].hash);
            Assert.IsTrue(result.HasDbDiff);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(ToUnixSeconds(newTimestamp), row.date);
            Assert.AreEqual(23456, row.adddate);
            Assert.AreEqual("preserve", row.tag);
        });
    }

    /// <summary>
    /// 同一 MD5 の一対一移動では、通常差分と case-only 差分のどちらでも
    /// 現在の生成値を保ったまま、旧行の三つの保存列を DB と memory へ引き継ぎます。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyFileScanDiff_MovedBmsWithSameMd5PreservesUserSongColumns(bool caseOnlyPathChange)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string oldPath = caseOnlyPathChange
                ? Path.Combine(lr2RootPath, "caseonly", "CHART.BMS")
                : Path.Combine(lr2RootPath, "Old", "moved.bms");
            string newPath = caseOnlyPathChange
                ? Path.Combine(lr2RootPath, "CaseOnly", "chart.bms")
                : Path.Combine(lr2RootPath, "New", "moved.bms");
            string staleMaintenancePath = caseOnlyPathChange
                ? Path.Combine(lr2RootPath, "CASEONLY", "Chart.bms")
                : Path.Combine(lr2RootPath, "Stale", "stale.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            if (!caseOnlyPathChange)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
            }

            string bmsText = CreateValidBmsText("Moved Same Md5");
            File.WriteAllText(newPath, bmsText, Encoding.ASCII);
            if (!caseOnlyPathChange)
            {
                File.WriteAllText(oldPath, bmsText, Encoding.ASCII);
            }
            var timestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(newPath, timestamp);
            var parsed = BMSFile.CreateBMSFileFromFile(newPath);
            var existingFile = new TestableBmsFile
            {
                path = oldPath,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 34567,
                tag = "moved-tag"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(3);
            existingFile.SetTextGroupFlagForTest(0);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = oldPath,
                    hash = parsed.hash,
                    encoding = "shift_jis",
                    wav_files_defined = 1,
                    wav_files_existing = 1
                }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = staleMaintenancePath,
                    hash = parsed.hash,
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            });

            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            ChartScanExecutionResult scanResult = new()
            {
                Success = true,
                Result = CreateScanResult(
                    [newPath],
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { Path.GetDirectoryName(newPath)!, ["readme.txt"] }
                    })
            };
            var service = new BmsLibraryInitializationService(
                fileDiffParserDegreeOverride: 1,
                fileDiffCommitChunkSizeOverride: 1);
            SongTableFileCheckResult first = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                options,
                [existingFile],
                scanResult,
                0L,
                () => null,
                null);
            ProjectCatalogState(first, [existingFile]);

            Assert.AreEqual(1, first.BmsAddedTargetCount);
            Assert.AreEqual(1, first.BmsDeletedTargetCount);
            Assert.AreEqual(0, first.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, first.BmsTextOnlyUpdateCount);
            Assert.AreEqual(1, first.BmsMovedHashRelinkCount);
            Assert.AreEqual(0, first.BmsMovedHashRelinkAmbiguousCount);
            Assert.AreEqual(1, first.AddedFiles.Count);
            Assert.IsTrue(first.HasDbDiff);
            CollectionAssert.Contains(first.DeletedPaths, oldPath);
            Assert.AreEqual(oldPath, existingFile.path);
            Assert.AreEqual(ToUnixSeconds(timestamp.AddDays(-1)), existingFile.date);
            Assert.AreEqual(0, existingFile.txt);
            Assert.AreEqual(34567, existingFile.adddate);
            Assert.AreEqual("moved-tag", existingFile.tag);

            BMSFile moved = first.NextFiles.Single();
            Assert.AreEqual(newPath, moved.path);
            Assert.AreEqual(parsed.hash, moved.hash);
            Assert.AreEqual("Moved Same Md5", moved.title);
            Assert.AreEqual(ToUnixSeconds(timestamp), moved.date);
            BMSFileMaintenanceInfo movedMaintenance = moved.TryGetMaintenanceInfoWithoutCreating();
            Assert.IsNotNull(movedMaintenance);
            Assert.AreEqual(1, movedMaintenance.wav_files_defined);
            Assert.AreEqual(0, movedMaintenance.wav_files_existing);
            Assert.AreEqual(1, moved.txt);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(newPath)),
                moved.folder);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(lr2RootPath),
                moved.parent);
            Assert.AreEqual(3, moved.favorite);
            Assert.AreEqual(34567, moved.adddate);
            Assert.AreEqual("moved-tag", moved.tag);

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", oldPath));
                LR2SongDB.song row = verify.Find<LR2SongDB.song>(newPath);
                Assert.IsNotNull(row);
                Assert.AreEqual(newPath, row.path);
                Assert.AreEqual(parsed.hash, row.hash);
                Assert.AreEqual("Moved Same Md5", row.title);
                Assert.AreEqual(ToUnixSeconds(timestamp), row.date);
                Assert.AreEqual(
                    Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(newPath)),
                    row.folder);
                Assert.AreEqual(
                    Lr2SongFolderParentNormalizer.ComputeDirectoryHash(lr2RootPath),
                    row.parent);
                Assert.AreEqual(1, row.txt);
                Assert.AreEqual(3, row.favorite);
                Assert.AreEqual(34567, row.adddate);
                Assert.AreEqual("moved-tag", row.tag);
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", oldPath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", staleMaintenancePath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", newPath));
                Assert.AreEqual(parsed.hash, verify.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", newPath));
                LR2SongDBExtended.maintenance currentMaintenance = verify.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == newPath);
                Assert.AreEqual(1, currentMaintenance.wav_files_defined);
                Assert.AreEqual(0, currentMaintenance.wav_files_existing);
            }

            SongTableFileCheckResult second = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                options,
                first.NextFiles,
                scanResult,
                0L,
                () => null,
                null);
            ProjectCatalogState(second, first.NextFiles);

            Assert.AreEqual(0, second.BmsAddedTargetCount);
            Assert.AreEqual(0, second.BmsDeletedTargetCount);
            Assert.AreEqual(0, second.BmsMovedHashRelinkCount);
            Assert.AreEqual(0, second.BmsMovedHashRelinkAmbiguousCount);
            Assert.IsFalse(second.HasDbDiff);
            BMSFile reapplied = second.NextFiles.Single();
            Assert.AreEqual(newPath, reapplied.path);
            Assert.AreEqual(ToUnixSeconds(timestamp), reapplied.date);
            BMSFileMaintenanceInfo reappliedMaintenance = reapplied.TryGetMaintenanceInfoWithoutCreating();
            Assert.IsNotNull(reappliedMaintenance);
            Assert.AreEqual(1, reappliedMaintenance.wav_files_defined);
            Assert.AreEqual(0, reappliedMaintenance.wav_files_existing);
            Assert.AreEqual(1, reapplied.txt);
            Assert.AreEqual(3, reapplied.favorite);
            Assert.AreEqual(34567, reapplied.adddate);
            Assert.AreEqual("moved-tag", reapplied.tag);

            using LR2SongDBExtended verifyAfterReapply = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verifyAfterReapply.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", oldPath));
            LR2SongDB.song rowAfterReapply = verifyAfterReapply.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(newPath, rowAfterReapply.path);
            Assert.AreEqual(3, rowAfterReapply.favorite);
            Assert.AreEqual(34567, rowAfterReapply.adddate);
            Assert.AreEqual("moved-tag", rowAfterReapply.tag);
        });
    }

    /// <summary>
    /// 同一 MD5 の既存 destination は、通常差分と case-only 差分のどちらでも
    /// stale source の保存値による上書きを受けず、exact path の行を維持します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyFileScanDiff_MovedBmsWithExistingDestinationPreservesDestinationUserColumns(bool caseOnlyPathChange)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string oldPath = caseOnlyPathChange
                ? Path.Combine(lr2RootPath, "existing-destination", "OLD.BMS")
                : Path.Combine(lr2RootPath, "Old", "moved.bms");
            string currentPath = caseOnlyPathChange
                ? Path.Combine(lr2RootPath, "Existing-Destination", "old.bms")
                : Path.Combine(lr2RootPath, "New", "moved.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
            string bmsText = CreateValidBmsText("Existing Destination");
            File.WriteAllText(currentPath, bmsText, Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 3, 1, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(currentPath, timestamp);
            var parsed = BMSFile.CreateBMSFileFromFile(currentPath);
            var staleSource = new TestableBmsFile
            {
                path = oldPath,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 34567,
                tag = "stale-source"
            };
            staleSource.SetHash(parsed.hash);
            staleSource.SetFavorite(3);
            var currentDestination = new TestableBmsFile
            {
                path = currentPath,
                date = ToUnixSeconds(timestamp),
                adddate = 76543,
                tag = "current-destination",
                txt = 1,
                folder = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(currentPath)),
                parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(lr2RootPath)
            };
            currentDestination.SetHash(parsed.hash);
            currentDestination.SetFavorite(9);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(staleSource, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(currentDestination, typeof(LR2SongDB.song));
            });

            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            ChartScanExecutionResult scanResult = new()
            {
                Success = true,
                Result = CreateScanResult(
                    [currentPath],
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { Path.GetDirectoryName(currentPath)!, ["readme.txt"] }
                    })
            };
            var service = new BmsLibraryInitializationService(
                fileDiffParserDegreeOverride: 1,
                fileDiffCommitChunkSizeOverride: 1);
            SongTableFileCheckResult first = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                options,
                [staleSource, currentDestination],
                scanResult,
                0L,
                () => null,
                null);
            ProjectCatalogState(first, [staleSource, currentDestination]);

            Assert.AreEqual(0, first.BmsAddedTargetCount);
            Assert.AreEqual(1, first.BmsDeletedTargetCount);
            Assert.AreEqual(0, first.BmsMovedHashRelinkCount);
            Assert.AreEqual(0, first.BmsMovedHashRelinkAmbiguousCount);
            Assert.IsTrue(first.HasDbDiff);
            CollectionAssert.Contains(first.DeletedPaths, oldPath);
            Assert.AreEqual(1, first.NextFiles.Count);
            BMSFile firstDestination = first.NextFiles.Single();
            Assert.AreEqual(currentPath, firstDestination.path);
            Assert.AreEqual(parsed.hash, firstDestination.hash);
            Assert.AreEqual(9, firstDestination.favorite);
            Assert.AreEqual(76543, firstDestination.adddate);
            Assert.AreEqual("current-destination", firstDestination.tag);

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", oldPath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", currentPath));
                LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
                Assert.AreEqual(currentPath, row.path);
                Assert.AreEqual(parsed.hash, row.hash);
                Assert.AreEqual(9, row.favorite);
                Assert.AreEqual(76543, row.adddate);
                Assert.AreEqual("current-destination", row.tag);
            }

        });
    }

    /// <summary>
    /// 同一 MD5 の旧候補が複数ある場合は、通常 path と case-only path が混在しても
    /// 保存列を移植せず、現在の exact membership だけを反映します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyFileScanDiff_MovedBmsWithAmbiguousSourceMd5DoesNotPreserveUserSongColumns(bool includeCaseOnlySourceCandidate)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string oldPath1 = includeCaseOnlySourceCandidate
                ? Path.Combine(lr2RootPath, "new", "DUPLICATE.BMS")
                : Path.Combine(lr2RootPath, "OldA", "duplicate.bms");
            string oldPath2 = Path.Combine(lr2RootPath, "OldB", "duplicate.bms");
            string newPath = Path.Combine(lr2RootPath, "New", "duplicate.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            string bmsText = CreateValidBmsText("Ambiguous Source");
            File.WriteAllText(newPath, bmsText, Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 3, 2, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(newPath, timestamp);
            var parsed = BMSFile.CreateBMSFileFromFile(newPath);
            var existingFile1 = new TestableBmsFile
            {
                path = oldPath1,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 34567,
                tag = "source-a"
            };
            existingFile1.SetHash(parsed.hash);
            existingFile1.SetFavorite(3);
            var existingFile2 = new TestableBmsFile
            {
                path = oldPath2,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 45678,
                tag = "source-b"
            };
            existingFile2.SetHash(parsed.hash);
            existingFile2.SetFavorite(4);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile1, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(existingFile2, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [existingFile1, existingFile2],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [newPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(newPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile1, existingFile2]);

            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(2, result.BmsDeletedTargetCount);
            Assert.AreEqual(0, result.BmsMovedHashRelinkCount);
            Assert.AreEqual(1, result.BmsMovedHashRelinkAmbiguousCount);
            BMSFile moved = result.NextFiles.Single();
            Assert.AreEqual(newPath, moved.path);
            Assert.IsNull(moved.favorite);
            Assert.AreNotEqual(34567, moved.adddate);
            Assert.AreNotEqual(45678, moved.adddate);
            Assert.AreNotEqual("source-a", moved.tag);
            Assert.AreNotEqual("source-b", moved.tag);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(newPath, row.path);
            Assert.IsNull(row.favorite);
            Assert.AreNotEqual(34567, row.adddate);
            Assert.AreNotEqual(45678, row.adddate);
            Assert.AreNotEqual("source-a", row.tag);
            Assert.AreNotEqual("source-b", row.tag);
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", oldPath1));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", oldPath2));
        });
    }

    /// <summary>
    /// 同一 MD5 の新規候補が複数ある場合は、通常 path と case-only path が混在しても
    /// 旧行の保存列を選択的に移植せず、全候補を exact membership として反映します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyFileScanDiff_MovedBmsWithAmbiguousDestinationMd5DoesNotPreserveUserSongColumns(bool includeCaseOnlyDestinationCandidate)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string oldPath = Path.Combine(lr2RootPath, "Old", "duplicate.bms");
            string newPath1 = includeCaseOnlyDestinationCandidate
                ? Path.Combine(lr2RootPath, "old", "DUPLICATE.BMS")
                : Path.Combine(lr2RootPath, "NewA", "duplicate.bms");
            string newPath2 = Path.Combine(lr2RootPath, "NewB", "duplicate.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(newPath1)!);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath2)!);
            string bmsText = CreateValidBmsText("Ambiguous Destination");
            File.WriteAllText(newPath1, bmsText, Encoding.ASCII);
            File.WriteAllText(newPath2, bmsText, Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 3, 3, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(newPath1, timestamp);
            File.SetLastWriteTimeUtc(newPath2, timestamp);
            var parsed = BMSFile.CreateBMSFileFromFile(newPath1);
            var existingFile = new TestableBmsFile
            {
                path = oldPath,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 34567,
                tag = "moved-tag"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(3);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [newPath1, newPath2],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(newPath1)!, Array.Empty<string>() },
                            { Path.GetDirectoryName(newPath2)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(2, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsDeletedTargetCount);
            Assert.AreEqual(0, result.BmsMovedHashRelinkCount);
            Assert.AreEqual(2, result.BmsMovedHashRelinkAmbiguousCount);
            Assert.AreEqual(2, result.NextFiles.Count);
            CollectionAssert.AreEquivalent(
                new[] { newPath1, newPath2 },
                result.NextFiles.Select(file => file.path).ToList());
            foreach (BMSFile moved in result.NextFiles)
            {
                Assert.IsNull(moved.favorite);
                Assert.AreNotEqual(34567, moved.adddate);
                Assert.AreNotEqual("moved-tag", moved.tag);
            }

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            List<LR2SongDB.song> rows = [.. verify.Table<LR2SongDB.song>()];
            Assert.AreEqual(2, rows.Count);
            CollectionAssert.AreEquivalent(
                new[] { newPath1, newPath2 },
                rows.Select(row => row.path).ToList());
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", oldPath));
            foreach (LR2SongDB.song row in rows)
            {
                Assert.IsNull(row.favorite);
                Assert.AreNotEqual(34567, row.adddate);
                Assert.AreNotEqual("moved-tag", row.tag);
            }
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_NewBmsSetsTxtFromDirectTextGroupOnlyWhenLr2SongDbSyncEnabled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directDirectory = Path.Combine(lr2RootPath, "DirectText");
            string nestedDirectory = Path.Combine(lr2RootPath, "NestedText");
            Directory.CreateDirectory(directDirectory);
            Directory.CreateDirectory(nestedDirectory);
            string directBmsPath = Path.Combine(directDirectory, "direct.bms");
            string nestedBmsPath = Path.Combine(nestedDirectory, "nested.bms");
            File.WriteAllText(directBmsPath, CreateValidBmsText("Direct Text"), Encoding.ASCII);
            File.WriteAllText(nestedBmsPath, CreateValidBmsText("Nested Text"), Encoding.ASCII);
            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [directBmsPath, nestedBmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { directDirectory, ["readme.txt"] },
                            { nestedDirectory, [Path.Combine("docs", "readme.txt")] }
                        })
                },
                0L,
                () => null,
                null);

            Assert.AreEqual(2, result.AddedFiles.Count);
            Assert.AreEqual(1, result.AddedFiles.Single(file => file.path == directBmsPath).txt);
            Assert.AreEqual(0, result.AddedFiles.Single(file => file.path == nestedBmsPath).txt);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", directBmsPath));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", nestedBmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ExistingBmsTextGroupChangeUpdatesTxtOnlyWhenLr2SongDbSyncEnabled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectory = Path.Combine(lr2RootPath, "TextOnly");
            Directory.CreateDirectory(chartDirectory);
            string bmsPath = Path.Combine(chartDirectory, "text-only.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Text Only"), Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 4, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            var parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(timestamp),
                adddate = 45678,
                tag = "text-tag"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(4);
            existingFile.SetTextGroupFlagForTest(0);
            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectory, ["readme.txt"] }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(1, result.BmsTextOnlyUpdateCount);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreSame(existingFile, result.NextFiles.Single());
            Assert.AreEqual(1, existingFile.txt);
            Assert.AreEqual(45678, existingFile.adddate);
            Assert.AreEqual("text-tag", existingFile.tag);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(1, row.txt);
            Assert.AreEqual(ToUnixSeconds(timestamp), row.date);
            Assert.AreEqual(4, row.favorite);
            Assert.AreEqual(45678, row.adddate);
            Assert.AreEqual("text-tag", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotResetTxtOutsideLr2Mode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectory = Path.Combine(lr2RootPath, "TextDisabled");
            Directory.CreateDirectory(chartDirectory);
            string bmsPath = Path.Combine(chartDirectory, "text-disabled.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Text Disabled"), Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 4, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(timestamp)
            };
            existingFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);
            existingFile.SetTextGroupFlagForTest(1);
            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                },
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null);

            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(0, result.BmsTextOnlyUpdateCount);
            Assert.AreEqual(1, existingFile.txt);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UpdatedBmsonUsesSnapshotTimestamp()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Updated", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath)!);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Old", "", "", "Artist", "Genre", 5, "beat-5k"));
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, oldTimestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);

            File.WriteAllText(bmsonPath, CreateBmsonJson("New", "", "", "Artist", "Genre", 7, "beat-7k"));
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, newTimestamp);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
            });

            List<ChartFile> cleanupCharts = [ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(existingSong, includeWarningSnapshot: false),
                Path.Combine(lr2RootPath, "Stale"),
                string.Empty,
                string.Empty,
                [])];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);
            ProjectCatalogState(result, [], [existingSong], cleanupCharts);

            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual("New", result.AddedBmsonSongs[0].title);
            Assert.AreEqual(newTimestamp, result.AddedBmsonSongs[0].updated_at);
            Assert.IsTrue(result.AddedBmsonSongs[0].HasFreshResourceReferences);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.AreEqual("New", result.NextBmsonSongs[0].title);
            ChartFile installDestinationChange = result.ClearedInstallDestinationCharts.Single();
            Assert.AreSame(existingSong, installDestinationChange.GetBmsonStorageOwner());
            Assert.IsTrue(string.IsNullOrWhiteSpace(installDestinationChange.InstallDestination));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.bmson_song row = verify.Table<LR2SongDBExtended.bmson_song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(newTimestamp, row.updated_at);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CaseOnlyBmsonPathMismatchAddsExactPathWithoutMigratingMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "BmsonCase", "chart.bmson");
            string oldCasePath = Path.Combine(lr2RootPath, "BmsonCase", "CHART.BMSON");
            string staleMaintenancePath = Path.Combine(lr2RootPath, "BMSONCASE", "Chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath)!);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Case", "", "", "Artist", "Genre", 5, "beat-5k"));
            var timestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, timestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);
            existingSong.path = oldCasePath;
            existingSong.folder = Path.GetDirectoryName(oldCasePath);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = staleMaintenancePath,
                    hash = existingSong.md5,
                    encoding = "utf-8"
                }, typeof(LR2SongDBExtended.maintenance));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);

            Assert.AreEqual(1, result.BmsonDeletedTargetCount);
            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.IsTrue(result.HasDbDiff);
            Assert.AreEqual(oldCasePath, existingSong.path);
            Assert.AreEqual(Path.GetDirectoryName(oldCasePath), existingSong.folder);
            Assert.AreEqual(bmsonPath, result.AddedBmsonSongs.Single().path);
            Assert.AreEqual(Path.GetDirectoryName(bmsonPath), result.AddedBmsonSongs.Single().folder);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song WHERE path = ?;", oldCasePath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song WHERE path = ?;", bmsonPath));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", oldCasePath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", staleMaintenancePath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", bmsonPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CaseOnlyBmsonPathMismatchReplacesExactPathAndConverges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "BmsonMtimeCase", "chart.bmson");
            string oldCasePath = Path.Combine(lr2RootPath, "bmsonmtimecase", "CHART.BMSON");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath)!);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Case Mtime", "", "", "Artist", "Genre", 5, "beat-5k"));
            var timestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, timestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);
            existingSong.path = oldCasePath;
            existingSong.folder = Path.GetDirectoryName(oldCasePath);
            existingSong.updated_at = timestamp.AddDays(-1);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = oldCasePath,
                    hash = existingSong.md5,
                    encoding = "utf-8"
                }, typeof(LR2SongDBExtended.maintenance));
            });

            var service = new BmsLibraryInitializationService();
            ChartScanExecutionResult scanResult = new()
            {
                Success = true,
                Result = CreateScanResult(
                    [bmsonPath],
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { Path.GetDirectoryName(bmsonPath)!, Array.Empty<string>() }
                    })
            };

            SongTableFileCheckResult first = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                scanResult,
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);
            ProjectCatalogState(first, [], [existingSong]);

            Assert.AreEqual(1, first.BmsonDeletedTargetCount);
            Assert.AreEqual(1, first.BmsonUpsertTargetCount);
            Assert.AreEqual(1, first.AddedBmsonSongs.Count);
            Assert.IsTrue(first.HasDbDiff);
            Assert.AreEqual(bmsonPath, first.AddedBmsonSongs.Single().path);

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song WHERE path = ?;", oldCasePath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song WHERE path = ?;", bmsonPath));
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", oldCasePath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", bmsonPath));
            }

            SongTableFileCheckResult second = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                scanResult,
                0L,
                () => null,
                null,
                currentBmsonSongs: first.NextBmsonSongs);

            Assert.AreEqual(0, second.BmsonUpsertTargetCount);
            Assert.IsFalse(second.HasDbDiff);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesScanMtimeForUnchangedBms()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "Keep", "keep.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath)!);
            File.WriteAllText(bmsPath, CreateValidBmsText("Keep"));
            var scanTimestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, scanTimestamp.AddDays(1));
            var parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(scanTimestamp)
            };
            currentFile.SetHash(parsed.hash);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
            });

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { Path.GetDirectoryName(bmsPath)!, Array.Empty<string>() }
                });
            scanResult.ChartFileEntriesByPath[bmsPath] = new RootFileEnumerationEntry(bmsPath, scanTimestamp);

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null);

            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, result.BmsMtimeFallbackCount);
            Assert.IsFalse(result.HasDbDiff);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UnchangedBmsonDoesNotParseOrMarkFresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath)!);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));
            var timestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, timestamp);
            var currentSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                folder = Path.GetDirectoryName(bmsonPath),
                title = "Keep",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = timestamp
            };
            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(currentSong, typeof(LR2SongDBExtended.bmson_song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [currentSong]);
            ProjectCatalogState(result, [], [currentSong]);

            Assert.AreEqual(0, result.BmsonUpsertTargetCount);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.IsFalse(result.NextBmsonSongs[0].HasFreshResourceReferences);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesScanMtimeForUnchangedBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath)!);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));
            var scanTimestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, scanTimestamp.AddDays(1));
            var currentSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                folder = Path.GetDirectoryName(bmsonPath),
                title = "Keep",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = scanTimestamp
            };
            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(currentSong, typeof(LR2SongDBExtended.bmson_song));
            });

            ChartScanResult scanResult = CreateScanResult(
                [bmsonPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { Path.GetDirectoryName(bmsonPath)!, Array.Empty<string>() }
                });
            scanResult.ChartFileEntriesByPath[bmsonPath] = new RootFileEnumerationEntry(bmsonPath, scanTimestamp);

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [currentSong]);

            Assert.AreEqual(0, result.BmsonUpsertTargetCount);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(0, result.BmsonMtimeFallbackCount);
            Assert.IsFalse(result.HasDbDiff);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_InvalidUpdatedBmsonKeepsExistingCatalogWithoutFreshRefs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Updated", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath)!);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Old", "", "", "Artist", "Genre", 5, "beat-5k"));
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, oldTimestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);
            existingSong.HasFreshResourceReferences = false;

            File.WriteAllText(bmsonPath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, newTimestamp);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath)!, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);
            ProjectCatalogState(result, [], [existingSong]);

            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.AreEqual("Old", result.NextBmsonSongs[0].title);
            Assert.IsFalse(result.NextBmsonSongs[0].HasFreshResourceReferences);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_MergesBmsonOnlyDirectoriesIntoInstallCandidateCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsDir = Path.Combine(lr2RootPath, "BmsKeep");
            string bmsonDir = Path.Combine(lr2RootPath, "BmsonOnly");
            Directory.CreateDirectory(bmsDir);
            Directory.CreateDirectory(bmsonDir);
            string bmsPath = Path.Combine(bmsDir, "keep.bms");
            string bmsonPath = Path.Combine(bmsonDir, "chart.bmson");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Keep\r\n");
            File.WriteAllText(bmsonPath, CreateBmsonJson("Title", "Sub", "Chart", "Artist", "Genre", 12, "beat-7k"));

            var keepFile = new TestableBmsFile
            {
                path = bmsPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);

            ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
            });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsDir, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService: null,
                logInstallPerformance: null,
                logEverythingScan: null,
                currentBmsonSongs: [],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsonDir, new[] { Path.Combine("sound", "song.ogg") } }
                        })
                });

            Assert.IsNotNull(result.NextDirectoryResourceLookupCache);
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), bmsDir);
            Assert.IsTrue(result.NextDirectoryResourceLookupCache.Keys.Contains(bmsonDir, StringComparer.OrdinalIgnoreCase));
            DirectoryResourceLookupCache.Entry bmsonEntry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(bmsonDir);
            Assert.IsNotNull(bmsonEntry);
            Assert.AreEqual(1, bmsonEntry.AudioFileNameHashCount);
            Assert.AreEqual(1, bmsonEntry.AudioRelativePathHashArray.Length);
        });
    }

}
