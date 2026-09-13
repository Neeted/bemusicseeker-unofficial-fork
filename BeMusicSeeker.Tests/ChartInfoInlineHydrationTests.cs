using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Tests.Helpers;
using ChartInfoExportTool;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
using OwnedChartCollectionTestSupport = BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// Owns chart-info lookup, hydration, inline evaluation, and candidate cases.
/// </summary>
[TestClass]
public sealed class ChartInfoInlineHydrationTests
{
    [TestMethod]
    public void LoadCurrentChartInfoParseFailuresByMd5_QueriesOnlyRequestedRows()
    {
        TargetedFailureQueryRun smallRun = ExecuteTargetedFailureQueryCase(backgroundCount: 16);
        TargetedFailureQueryRun largeRun = ExecuteTargetedFailureQueryCase(backgroundCount: 128);

        Assert.AreEqual(1, smallRun.ReturnedRows);
        Assert.AreEqual(1, largeRun.ReturnedRows);
        Assert.IsTrue(smallRun.ProfileCount > 0, "16行背景の対象 failure query のPROFILE callbackを観測できませんでした。");
        Assert.IsTrue(largeRun.ProfileCount > 0, "128行背景の対象 failure query のPROFILE callbackを観測できませんでした。");
        Assert.AreEqual(
            smallRun.FullScanSteps,
            largeRun.FullScanSteps,
            "対象 failure query の FULLSCAN_STEP が背景行数に依存しました。16="
            + smallRun.FullScanSteps + ", 128=" + largeRun.FullScanSteps);
        Assert.AreEqual(
            smallRun.VmSteps,
            largeRun.VmSteps,
            "対象 failure query の VM_STEP が背景行数に依存しました。16="
            + smallRun.VmSteps + ", 128=" + largeRun.VmSteps);
    }

    [TestMethod]
    public void LoadCurrentChartInfoParseFailuresByMd5_EmptyInputDoesNotReadFailureTable()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            using var observation = new SqliteStatementObservation();
            var gateway = new BmsLibraryDbGateway(songDbPath, songDbFactory: observation.OpenSongDb);

            Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
                [],
                TimeSpan.FromSeconds(60));
            observation.ThrowIfCallbackFailed();

            Assert.AreEqual(0, result.Count);
            Assert.IsFalse(
                observation.Statements.Any(statement => statement.Sql.Contains(
                    "chart_info_parse_failure",
                    StringComparison.OrdinalIgnoreCase)),
                "空の対象集合で failure table への SQL が実行されました。");
        });
    }

    [TestMethod]
    public void LoadCurrentChartInfoParseFailuresByMd5_PreservesParserAndTimeoutCurrentness()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string currentMd5 = new('a', 32);
            string staleParserMd5 = new('b', 32);
            string shortTimeoutMd5 = new('c', 32);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    currentMd5,
                    new string('1', 64),
                    Path.Combine(tempRootPath, "current.bms"),
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "current",
                    null),
                CreateChartInfoParseFailureRow(
                    staleParserMd5,
                    new string('2', 64),
                    Path.Combine(tempRootPath, "stale-parser.bms"),
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1,
                    "parse_failed",
                    "InvalidDataException",
                    "stale parser",
                    null),
                CreateChartInfoParseFailureRow(
                    shortTimeoutMd5,
                    new string('3', 64),
                    Path.Combine(tempRootPath, "short-timeout.bms"),
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "timeout",
                    "ChartInfoParseTimeoutException",
                    "short timeout",
                    500)
            ]);

            Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
                [currentMd5, staleParserMd5, shortTimeoutMd5],
                TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.ContainsKey(currentMd5));
            Assert.IsFalse(result.ContainsKey(staleParserMd5));
            Assert.IsFalse(result.ContainsKey(shortTimeoutMd5));
        });
    }

    [TestMethod]
    public void LoadChartInfosByHash_LoadsRequestedRowsAndUsesStableMd5Representative()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string firstSha = new('1', 64);
            string secondSha = new('2', 64);
            string unrelatedSha = new('3', 64);
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(secondSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(firstSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(unrelatedSha, new string('b', 32), BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            ]);

            Dictionary<string, LR2SongDBExtended.chart_info> bySha256 = gateway.LoadChartInfosBySha256([secondSha]);
            Dictionary<string, LR2SongDBExtended.chart_info> byMd5 = gateway.LoadChartInfosByMd5([md5]);

            Assert.AreEqual(1, bySha256.Count);
            Assert.AreEqual(secondSha, bySha256[secondSha].sha256);
            Assert.AreEqual(1, byMd5.Count);
            Assert.AreEqual(firstSha, byMd5[md5].sha256);
            Assert.IsFalse(bySha256.ContainsKey(unrelatedSha));
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadChartInfosByHash_UsesReadOnlyCurrentSchemaWhileProcessLockHeld()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string sha256 = new('1', 64);
            gateway.UpsertChartInfos([CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);

            using var lockAcquired = new ManualResetEventSlim(false);
            using var releaseLock = new ManualResetEventSlim(false);
            Task lockHolder = Task.Run(delegate
            {
                Assert.IsTrue(LR2SongDBExtended.Lock(TimeSpan.FromSeconds(5)));
                try
                {
                    lockAcquired.Set();
                    Assert.IsTrue(releaseLock.Wait(TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    LR2SongDBExtended.Unlock();
                }
            });

            Assert.IsTrue(lockAcquired.Wait(TimeSpan.FromSeconds(5)));
            Task<Dictionary<string, LR2SongDBExtended.chart_info>> lookupTask = Task.Run(() => gateway.LoadChartInfosBySha256([sha256]));
            try
            {
                Assert.IsTrue(lookupTask.Wait(TimeSpan.FromSeconds(2)), "chart_info lookup should not wait for the writable process lock when schema is current.");
            }
            finally
            {
                releaseLock.Set();
                lockHolder.Wait(TimeSpan.FromSeconds(5));
            }

            Dictionary<string, LR2SongDBExtended.chart_info> rows = lookupTask.Result;
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(md5, rows[sha256].md5);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DeferredChartInfoHydration_BuildsSessionIndexAndUsesSha256BeforeMd5()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string firstSha = new('1', 64);
            string secondSha = new('2', 64);
            string unrelatedSha = new('3', 64);
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(secondSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(firstSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(unrelatedSha, new string('b', 32), BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            ]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());

            Assert.IsFalse(library.ChartInfoIndexHydrated);
            Assert.AreEqual(0, library.ChartInfoIndexVersion);
            Assert.IsNull(library.ResolveChartInfo(firstSha, md5));

            InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false);

            await AwaitChartInfoHydrationAsync(library);
            Assert.IsTrue(library.ChartInfoIndexHydrated);
            Assert.IsTrue(library.ChartInfoIndexVersion > 0);
            Assert.AreEqual(secondSha, library.ResolveChartInfo(secondSha, md5).sha256, "sha256 match should win over md5 fallback.");
            Assert.AreEqual(firstSha, library.ResolveChartInfo(null, md5).sha256, "md5 fallback should use the stable sha256-ordered representative.");
            Assert.AreEqual(unrelatedSha, library.ResolveChartInfo(unrelatedSha, null).sha256);
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_ParsesOnlyProvidedSnapshots()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string targetChartPath = Path.Combine(tempRootPath, "target.bms");
            string untouchedChartPath = Path.Combine(tempRootPath, "untouched.bms");
            File.WriteAllText(targetChartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(untouchedChartPath, "#PLAYER 1\r\n#BPM 150\r\n#00111:01\r\n", Encoding.ASCII);
            var targetDigest = BMSFile.CreateBMSFileFromFile(targetChartPath);
            var untouchedDigest = BMSFile.CreateBMSFileFromFile(untouchedChartPath);
            var targetFile = new TestableBmsFile { path = targetChartPath };
            var untouchedFile = new TestableBmsFile { path = untouchedChartPath };
            targetFile.SetHash(targetDigest.hash);
            untouchedFile.SetHash(untouchedDigest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                gateway,
                [InlineChartSnapshotTarget.FromBmsFile(targetFile, ChartFileContentReader.ReadSnapshot(targetChartPath))],
                new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(targetFile.hash, result.AppliedRows[0].md5);
            Assert.AreEqual(0, untouchedFile.sha256?.Length ?? 0);
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_GroupsDuplicateBmsMd5AndFansOutStorageApplications()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "duplicate-a.bms");
            string secondPath = Path.Combine(tempRootPath, "duplicate-b.bms");
            const string content = "#PLAYER 1\r\n#TITLE duplicate\r\n#PLAYLEVEL 9\r\n#BPM 140\r\n#00111:01\r\n";
            File.WriteAllText(firstPath, content, Encoding.ASCII);
            File.WriteAllText(secondPath, content, Encoding.ASCII);
            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondPath);
            var firstFile = new TestableBmsFile { path = firstPath };
            var secondFile = new TestableBmsFile { path = secondPath };
            firstFile.SetHash(firstSnapshot.Md5);
            secondFile.SetHash(secondSnapshot.Md5);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                new BmsLibraryDbGateway(songDbPath),
                [
                    InlineChartSnapshotTarget.FromBmsFile(firstFile, firstSnapshot),
                    InlineChartSnapshotTarget.FromBmsFile(secondFile, secondSnapshot)
                ],
                new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase));

            Assert.AreEqual(firstSnapshot.Md5, secondSnapshot.Md5);
            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(2, result.StorageApplications.Count);
            CollectionAssert.AreEquivalent(
                new[] { firstPath, secondPath },
                result.StorageApplications.Select(application => application.Chart.Path).ToArray());
            Assert.IsTrue(result.StorageApplications.All(application =>
                application.Row == result.ChartInfoRows[0]));
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_GroupsDuplicateBmsMd5AcrossReadBatches()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "duplicate-batch-a.bms");
            string secondPath = Path.Combine(tempRootPath, "duplicate-batch-b.bms");
            const string content = "#PLAYER 1\r\n#TITLE duplicate batch\r\n#PLAYLEVEL 6\r\n#BPM 125\r\n#00111:01\r\n";
            File.WriteAllText(firstPath, content, Encoding.ASCII);
            File.WriteAllText(secondPath, content, Encoding.ASCII);
            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            var firstFile = new TestableBmsFile { path = firstPath };
            var secondFile = new TestableBmsFile { path = secondPath };
            firstFile.SetHash(firstSnapshot.Md5);
            secondFile.SetHash(firstSnapshot.Md5);
            var service = new ChartInfoInlineBuildService(
                new ChartInfoBuildService(),
                parserDegree: 1,
                batchSizeOverride: 1);

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                new BmsLibraryDbGateway(songDbPath),
                [
                    ChartFileProjection.FromBmsFile(firstFile, includeWarningSnapshot: false),
                    ChartFileProjection.FromBmsFile(secondFile, includeWarningSnapshot: false)
                ]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(2, result.StorageApplications.Count);
            CollectionAssert.AreEquivalent(
                new[] { firstPath, secondPath },
                result.StorageApplications.Select(application => application.Chart.Path).ToArray());
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_AppliesExistingCurrentRowWithoutParsing()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "already-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE current\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(snapshot.Md5);
            file.SetSha256(snapshot.Sha256);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            LR2SongDBExtended.chart_info expected = CreateChartInfoRow(file.sha256, file.hash, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([expected]);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            var currentFailures = new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase)
            {
                [file.hash] = CreateChartInfoParseFailureRow(
                    file.hash,
                    file.sha256,
                    chartPath,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "current failure must lose to current info",
                    null)
            };

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                gateway,
                [InlineChartSnapshotTarget.FromBmsFile(file, snapshot)],
                currentFailures);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(0, result.SuccessCount);
            Assert.AreEqual(1, result.CurrentSkippedCount);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(expected.sha256, result.AppliedRows[0].sha256);
            Assert.AreEqual(expected.md5, result.AppliedRows[0].md5);
        });
    }

    [TestMethod]
    public void ChartInfoSnapshotEvaluator_CurrentRowTakesPrecedenceOverCurrentFailure()
    {
        string md5 = new('a', 32);
        string sha256 = new('b', 64);
        byte[] bytes = Encoding.ASCII.GetBytes("not a parseable chart");
        var file = new TestableBmsFile { path = @"C:\Charts\current.bms" };
        file.SetHash(md5);
        file.SetSha256(sha256);
        var snapshot = new ChartFileSnapshot(file.path, bytes, DateTime.UtcNow, md5, sha256);
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false));
        LR2SongDBExtended.chart_info currentRow = CreateChartInfoRow(
            sha256,
            md5,
            BmsLibraryDbGateway.CurrentChartInfoParserVersion);

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot,
            target,
            currentRow,
            hasCurrentParseFailure: true);

        Assert.AreSame(currentRow, result.Row);
        Assert.IsTrue(result.CurrentRowSkipped);
        Assert.IsFalse(result.SkippedPersistedFailure);
        Assert.IsFalse(result.ParseFailed);
    }

    [TestMethod]
    public void ChartInfoSnapshotEvaluator_IdentityMismatchParsesAndNormalizesFailure()
    {
        string md5 = new('c', 32);
        string sha256 = new('d', 64);
        byte[] bytes = Encoding.ASCII.GetBytes("#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n");
        var file = new TestableBmsFile { path = @"C:\Charts\mismatch.bms" };
        file.SetHash(md5);
        file.SetSha256(sha256);
        var snapshot = new ChartFileSnapshot(file.path, bytes, DateTime.UtcNow, md5, sha256);
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false));
        LR2SongDBExtended.chart_info incompatibleRow = CreateChartInfoRow(
            sha256,
            new string('e', 32),
            BmsLibraryDbGateway.CurrentChartInfoParserVersion);

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot,
            target,
            incompatibleRow,
            hasCurrentParseFailure: false);

        Assert.IsTrue(result.ParseFailed);
        Assert.IsFalse(result.CurrentRowSkipped);
        Assert.IsNotNull(result.ParseFailureRow);
        Assert.AreEqual(md5, result.ParseFailureRow.md5);
        Assert.IsFalse(result.ParseFailureRow.message.Contains('\r'));
        Assert.IsFalse(result.ParseFailureRow.message.Contains('\n'));
        Assert.IsTrue(result.ParseFailureRow.message.Length <= 1024);
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_ParseFailureCanBePersistedWithSong()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-target.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                gateway,
                [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(0, result.SuccessCount);
            Assert.IsTrue(string.IsNullOrWhiteSpace(file.sha256));
            Assert.AreEqual(1, result.StorageApplications.Count);
            var mutationOwner = new CatalogMutationOwner(
                new CatalogStorageRowsOwner(),
                new CatalogOwnedCollectionOwner(),
                gateway);
            CatalogChartInfoStorageWriteReceipt receipt = mutationOwner.ApplyChartInfoStorageWrite(
                new CatalogChartInfoStorageWriteRequest(
                    result.StorageApplications.Select(application => application.CreateBmsPersistenceCopy()),
                    result.StorageApplications.Select(application => application.CreateBmsonPersistenceCopy()),
                    new CatalogChartInfoWriteRequest(
                        chartInfoRows: result.ChartInfoRows,
                        parseFailureRows: result.ParseFailureRows,
                        parseFailureDeleteMd5s: result.ParseFailureDeleteMd5s)));
            Assert.IsTrue(receipt.Applied);
            foreach (ChartInfoStorageApplication application in result.StorageApplications)
            {
                application.ApplyCommitted(result.DigestChanges);
            }
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.hash).Single();
            Assert.AreEqual(file.sha256, failure.sha256);
            Assert.AreEqual(chartPath, failure.path);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, failure.parser_version);
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreEqual("InvalidDataException", failure.exception_type);
            Assert.IsFalse(failure.parse_timeout_ms.HasValue);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DeferredChartInfoHydration_IndexesExistingRowsForBmsAndBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string bmsSha = new('1', 64);
            string bmsonSha = new('2', 64);
            var file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "hydrated.bms")
            };
            file.SetHash(new string('a', 32));
            file.SetSha256(bmsSha);
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempRootPath, "hydrated.bmson"),
                md5 = new string('b', 32),
                sha256 = bmsonSha
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            LR2SongDBExtended.chart_info bmsRow = CreateChartInfoRow(bmsSha, file.hash, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            LR2SongDBExtended.chart_info bmsonRow = CreateChartInfoRow(bmsonSha, bmsonSong.md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([bmsRow, bmsonRow]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
            {
                BMSFiles = [file],
                BmsonSongs = [bmsonSong]
            };

            InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false);

            await AwaitChartInfoHydrationAsync(library);
            Assert.IsFalse(library.ChartInfoHydrationRunning);
            Assert.AreEqual(2, library.ChartInfoHydrationTotalCount);
            Assert.AreEqual(0, library.ChartInfoHydrationAppliedCount);
            LR2SongDBExtended.chart_info resolvedBmsRow = library.ResolveChartInfo(file.sha256, file.hash);
            LR2SongDBExtended.chart_info resolvedBmsonRow = library.ResolveChartInfo(bmsonSong.sha256, bmsonSong.md5);
            Assert.IsNotNull(resolvedBmsRow);
            Assert.AreEqual(bmsSha, resolvedBmsRow.sha256);
            Assert.IsNotNull(resolvedBmsonRow);
            Assert.AreEqual(bmsonSha, resolvedBmsonRow.sha256);
            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);

            InvokeDeferredChartInfoHydration(library, "unit_test_repeat", queueFullBackfillAfterHydration: false);

            await AwaitChartInfoHydrationAsync(library);
            Assert.AreEqual(2, library.ChartInfoHydrationTotalCount);
            Assert.AreEqual(0, library.ChartInfoHydrationAppliedCount);
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task DeferredChartInfoHydration_UsesActualDataInBothModesAndSkipsFullBackfillWhenCurrent(bool operationModeLr2Db)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "current.bms");
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(md5);
            file.SetSha256(sha);
            LR2SongDBExtended.chart_info row = CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            row.speedchange = "120.0,0.0;240.0,1000.0";
            row.lanenotes = "1,2,3,4";
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_info));
            }
            bool originalOperationMode = Settings.Default.OperationModeLR2DB;
            try
            {
                Settings.Default.OperationModeLR2DB = operationModeLr2Db;
                var options = new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = operationModeLr2Db,
                };
                if (operationModeLr2Db)
                {
                    string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
                    using var songDb = new LR2SongDBExtended(songDbPath);
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, signature, "unit-test", 1, DateTime.UtcNow);
                }
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
                {
                    BMSFiles = [file]
                };

                Assert.IsFalse(library.ChartInfoIndexHydrated);
                Assert.AreEqual(0, library.ChartInfoIndexVersion);
                Assert.IsNull(library.ResolveChartInfo(sha, md5));

                InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: true);

                await AwaitChartInfoHydrationAsync(library);
                Assert.IsTrue(library.ChartInfoIndexHydrated);
                Assert.IsTrue(library.ChartInfoIndexVersion > 0);
                Assert.AreEqual(1, library.ChartInfoBackfillRequestedVersion);
                Assert.AreEqual(1, library.ChartInfoBackfillCompletedVersion);
                Assert.AreEqual(1, library.ChartInfoHydrationTotalCount);

                LR2SongDBExtended.chart_info resolved = library.ResolveChartInfo(sha, md5);

                Assert.IsNotNull(resolved);
                Assert.AreEqual(sha, resolved.sha256);
                AssertChartInfoDisplayProjectionEquivalent(row, resolved);
            }
            finally
            {
                Settings.Default.OperationModeLR2DB = originalOperationMode;
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CatalogChartInfoOwner_ActualDataAllCurrentSnapshotSkipsCandidateSummary(bool operationModeLr2Db)
    {
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha256 = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "current-snapshot.bms");
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(md5);
            file.SetSha256(sha256);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha256), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(
                    CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                    typeof(LR2SongDBExtended.chart_info));
            }

            var gateway = new BmsLibraryDbGateway(songDbPath);
            var storageRowsOwner = new CatalogStorageRowsOwner();
            storageRowsOwner.ReplaceBmsRows([file]);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            ownedCollectionOwner.EnsureCurrent(storageRowsOwner);
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, gateway);
            var logs = new ConcurrentQueue<string>();
            var events = new ConcurrentQueue<CatalogChartInfoOwnerEvent>();
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CatalogChartInfoOwner? owner = null;
            void SignalCompletion()
            {
                try
                {
                    CatalogChartInfoOwner currentOwner = owner!;
                    if (currentOwner.ChartInfoHydrationRequestedVersion > 0
                        && currentOwner.ChartInfoHydrationCompletedVersion == currentOwner.ChartInfoHydrationRequestedVersion
                        && currentOwner.ChartInfoBackfillRequestedVersion > 0
                        && currentOwner.ChartInfoBackfillCompletedVersion == currentOwner.ChartInfoBackfillRequestedVersion
                        && !currentOwner.ChartInfoHydrationRunning
                        && !currentOwner.ChartInfoBackfillRunning)
                    {
                        completion.TrySetResult(null);
                    }
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }
            owner = new CatalogChartInfoOwner(
                _ => SignalCompletion(),
                () => false,
                (_, _) => false,
                null,
                logs.Enqueue);
            owner.ConfigureWorkflow(
                gateway,
                mutationOwner,
                storageRowsOwner,
                ownedCollectionOwner,
                logs.Enqueue,
                events.Enqueue);

            owner.QueueDeferredHydration("unit_test_all_current", queueFullBackfillAfterHydration: true);

            SignalCompletion();
            await completion.Task;
            Assert.IsTrue(logs.Any(message => message.StartsWith("chart_info_backfill skipped reason=hydration_all_current", StringComparison.Ordinal)));
            Assert.IsFalse(logs.Any(message => message.StartsWith("chart_info_backfill candidate_summary_start", StringComparison.Ordinal)));
            Assert.AreEqual(0, owner.ChartInfoBackfillTotalCount);
            Assert.AreEqual(0, owner.ChartInfoBackfillProcessedCount);
            Assert.IsTrue(events.Any(ownerEvent =>
                ownerEvent.Kind == CatalogChartInfoOwnerEventKind.StartupMemoryCheckpoint
                && ownerEvent.CheckpointStage == "chart_info_backfill"
                && ownerEvent.CheckpointStatus == "skipped"));
        });
    }

    [TestMethod]
    public void CatalogChartInfoOwner_InlinePublicationOrdersDigestEffectsAfterSessionIndex()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "publication-order.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE publication order before\r\n#PLAYLEVEL 7\r\n#BPM 130\r\n#00111:01\r\n",
                Encoding.ASCII);
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            string oldMd5 = file.hash;
            string oldSha256 = file.sha256;
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE publication order after\r\n#PLAYLEVEL 7\r\n#BPM 130\r\n#00111:01\r\n",
                Encoding.ASCII);
            var library = new TestBmsLibrary(songDbPath);
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [file]);
            OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(library, []);
            OwnedChartCollectionTestSupport.SetDuplicateChartGroupsWithoutNotification(library, []);
            PlaylistLibraryResolveIndexSnapshot initialResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out _,
                out _);
            Assert.AreEqual(chartPath, initialResolve.ResolveChartForPlaylistHash(oldMd5, null).Path);
            List<string> publicationOrder = [];
            bool playlistResolvedAtDigestNotification = false;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.ChartInfoIndexVersion))
                {
                    publicationOrder.Add("session_index");
                    Assert.IsTrue(library.ChartInfoIndexVersion > 0);
                    Assert.IsNotNull(library.ResolveChartInfo(file.sha256, file.hash));
                }
                else if (args.PropertyName == nameof(BMSLibrary.OwnedChartCollectionVersion))
                {
                    publicationOrder.Add("digest_effects");
                    Assert.IsNotNull(library.ResolveChartInfo(file.sha256, file.hash));
                    Assert.IsTrue(library.GetOwnedChartHashIndexSnapshot().ContainsMd5(file.hash));
                    PlaylistLibraryResolveIndexSnapshot notificationResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                        CancellationToken.None,
                        out bool cacheHit,
                        out int staleRetries);
                    Assert.IsTrue(cacheHit);
                    Assert.AreEqual(0, staleRetries);
                    Assert.IsNull(notificationResolve.ResolveChartForPlaylistHash(oldMd5, null));
                    Assert.AreEqual(
                        chartPath,
                        notificationResolve.ResolveChartForPlaylistHash(file.hash, null).Path);
                    playlistResolvedAtDigestNotification = true;
                }
            };

            ChartInfoInlineBuildResult result = OwnedChartCollectionTestSupport.InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "publication_order",
                [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)]);

            Assert.IsTrue(publicationOrder.IndexOf("session_index") >= 0);
            Assert.IsTrue(publicationOrder.IndexOf("digest_effects") >= 0);
            Assert.IsTrue(
                publicationOrder.IndexOf("session_index") < publicationOrder.IndexOf("digest_effects"),
                "chart-info session index must be observable before digest-dependent collection notification.");
            Assert.IsTrue(playlistResolvedAtDigestNotification);
            Assert.AreNotEqual(oldMd5, file.hash);
            Assert.AreNotEqual(oldSha256, file.sha256);
            Assert.IsTrue(result.DigestChanges.Count > 0);
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task DeferredChartInfoHydration_MissingCurrentRowBackfillsRegardlessOfCompletedLr2Status(bool operationModeLr2Db)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 13\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            file.SetSha256(digest.sha256);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, file.hash);
                songDb.InsertOrReplace(CreateChartDigestRow(file.hash, file.sha256), typeof(LR2SongDBExtended.chart_digest_map));
                if (operationModeLr2Db)
                {
                    string signature = Lr2SongDbSyncSignatureBuilder.Build(new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true });
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, signature, "unit-test", 1, DateTime.UtcNow);
                }
            }
            bool originalOperationMode = Settings.Default.OperationModeLR2DB;
            try
            {
                Settings.Default.OperationModeLR2DB = operationModeLr2Db;
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
                {
                    BMSFiles = [file]
                };

                InvokeDeferredChartInfoHydration(library, "unit_test_missing", queueFullBackfillAfterHydration: true);

                await AwaitChartInfoHydrationAsync(library);
                await AwaitChartInfoBackfillAsync(library);
                using var verify = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", file.sha256));
                Assert.AreEqual(13, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
                Assert.AreEqual(13, file.level);
            }
            finally
            {
                Settings.Default.OperationModeLR2DB = originalOperationMode;
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DeferredChartInfoHydration_SkipsFullBackfillWhenNoCandidates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());

            InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: true);

            await AwaitChartInfoHydrationAsync(library);
            Assert.AreEqual(1, library.ChartInfoBackfillRequestedVersion);
            Assert.AreEqual(1, library.ChartInfoBackfillCompletedVersion);
            Assert.IsFalse(library.ChartInfoBackfillRunning);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadChartInfoHydrationData_UsesRawProjectionAndPreservesCurrentness()
    {
        string? previousMode = Environment.GetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE", null);
        try
        {
            WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
            {
                var gateway = new BmsLibraryDbGateway(songDbPath);
                string currentMd5 = new('a', 32);
                string staleMd5 = new('b', 32);
                string currentFailureMd5 = new('c', 32);
                string staleFailureMd5 = new('d', 32);
                string currentSha = new('1', 64);
                string staleSha = new('2', 64);
                string currentFailureSha = new('3', 64);
                string staleFailureSha = new('4', 64);
                LR2SongDBExtended.chart_info currentRow = CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
                currentRow.level = null;
                currentRow.difficulty = 3;
                currentRow.difficulty_defined = false;
                currentRow.mainbpm = 123.5;
                currentRow.total = null;
                currentRow.total_defined = true;
                currentRow.density = 12.25;
                currentRow.speedchange_count = 2;
                currentRow.updated_at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                LR2SongDBExtended.chart_info staleRow = CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1);
                staleRow.charthash = new string('e', 64);
                staleRow.distribution = "1,2,3";
                staleRow.speedchange = "120.0,0.0;240.0,1.0";
                staleRow.lanenotes = "1,2,3,4";

                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                    songDb.InsertOrReplace(currentRow, typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(staleRow, typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(
                        CreateChartInfoParseFailureRow(currentFailureMd5, currentFailureSha, Path.Combine(tempRootPath, "current-failure.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null),
                        typeof(LR2SongDBExtended.chart_info_parse_failure));
                    songDb.InsertOrReplace(
                        CreateChartInfoParseFailureRow(staleFailureMd5, staleFailureSha, Path.Combine(tempRootPath, "stale-timeout.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "timeout", "ChartInfoParseTimeoutException", "old timeout", 500),
                        typeof(LR2SongDBExtended.chart_info_parse_failure));
                }

                ChartInfoHydrationLoadResult result = gateway.LoadChartInfoHydrationData(TimeSpan.FromMilliseconds(1000));

                Assert.AreEqual("raw_string_display", result.MaterializeMode);
                Assert.AreEqual(2, result.ChartInfoRows);
                Assert.AreEqual(2, result.ParseFailureRows);
                Assert.AreEqual(4, result.RawRows);
                Assert.AreEqual(2, result.ChartInfoBySha256.Count);
                Assert.IsTrue(result.CurrentChartInfoSha256s.Contains(currentSha));
                Assert.IsFalse(result.CurrentChartInfoSha256s.Contains(staleSha));
                Assert.IsTrue(result.CurrentParseFailureMd5s.Contains(currentFailureMd5));
                Assert.IsFalse(result.CurrentParseFailureMd5s.Contains(staleFailureMd5));
                AssertChartInfoDisplayProjectionEquivalent(currentRow, result.ChartInfoBySha256[currentSha]);
                AssertChartInfoDisplayProjectionEquivalent(staleRow, result.ChartInfoBySha256[staleSha]);
                Assert.AreEqual(currentRow.updated_at, result.ChartInfoBySha256[currentSha].updated_at);
                Assert.AreEqual(staleRow.updated_at, result.ChartInfoBySha256[staleSha].updated_at);

                Dictionary<string, LR2SongDBExtended.chart_info> fullRows = gateway.LoadChartInfosBySha256([currentSha, staleSha]);

                AssertChartInfoEquivalent(currentRow, fullRows[currentSha]);
                AssertChartInfoEquivalent(staleRow, fullRows[staleSha]);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE", previousMode);
        }
    }

    [TestMethod]
    public void GetChartInfoBackfillCandidateSummary_ClassifiesCurrentFailureAndStaleRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string currentMd5 = new('a', 32);
            string missingDigestMd5 = new('b', 32);
            string staleMd5 = new('c', 32);
            string failureMd5 = new('d', 32);
            string currentSha = new('1', 64);
            string staleSha = new('2', 64);
            string failureSha = new('3', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "current.bms"), currentMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "missing-digest.bms"), missingDigestMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "stale.bms"), staleMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "failure.bms"), failureMd5);
                songDb.InsertOrReplace(CreateChartDigestRow(currentMd5, currentSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartDigestRow(staleMd5, staleSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartDigestRow(failureMd5, failureSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(
                    CreateChartInfoParseFailureRow(failureMd5, failureSha, Path.Combine(tempRootPath, "failure.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null),
                    typeof(LR2SongDBExtended.chart_info_parse_failure));
            }

            ChartInfoBackfillCandidateSummary summary = gateway.GetChartInfoBackfillCandidateSummary(TimeSpan.FromSeconds(30));

            Assert.AreEqual(4, summary.BmsOwnerCount);
            Assert.AreEqual(0, summary.BmsonOwnerCount);
            Assert.AreEqual(1, summary.CurrentChartInfoOwnerCount);
            Assert.AreEqual(1, summary.CurrentParseFailureOwnerCount);
            Assert.AreEqual(1, summary.MissingDigestOwnerCount);
            Assert.AreEqual(0, summary.MissingChartInfoOwnerCount);
            Assert.AreEqual(1, summary.StaleChartInfoOwnerCount);
            Assert.AreEqual(2, summary.CandidateOwnerCount);
        });
    }

    private static TargetedFailureQueryRun ExecuteTargetedFailureQueryCase(int backgroundCount)
    {
        const string targetMd5 = "ffffffffffffffffffffffffffffffff";
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_ChartInfoTargetedFailureQuery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var setupGateway = new BmsLibraryDbGateway(songDbPath);
            setupGateway.EnsureChartInfoSchema();
            setupGateway.UpsertChartInfoParseFailures(
                Enumerable.Range(0, backgroundCount)
                    .Select(index => CreateChartInfoParseFailureRow(
                        index.ToString("x32", CultureInfo.InvariantCulture),
                        index.ToString("x64", CultureInfo.InvariantCulture),
                        Path.Combine(tempRootPath, "background-" + index.ToString(CultureInfo.InvariantCulture) + ".bms"),
                        BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                        "parse_failed",
                        "InvalidDataException",
                        "background",
                        null))
                    .Append(CreateChartInfoParseFailureRow(
                        targetMd5,
                        new string('e', 64),
                        Path.Combine(tempRootPath, "target.bms"),
                        BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                        "parse_failed",
                        "InvalidDataException",
                        "target",
                        null)));

            using var observation = new SqliteStatementObservation();
            var gateway = new BmsLibraryDbGateway(songDbPath, songDbFactory: observation.OpenSongDb);
            Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
                [targetMd5],
                TimeSpan.FromSeconds(60));
            observation.ThrowIfCallbackFailed();

            IReadOnlyList<SqliteStatementObservation.SqliteObservedStatement> statements = observation.Statements;
            SqliteStatementObservation.SqliteObservedStatement[] targetedStatements = statements
                .Where(IsTargetedFailureStatement)
                .ToArray();
            Assert.AreEqual(1, targetedStatements.Length, "対象 failure query が一つにまとまりませんでした。");
            SqliteStatementObservation.SqliteObservedStatement targetedStatement = targetedStatements[0];
            Assert.AreEqual(1, targetedStatement.RowCount);
            Assert.IsTrue(targetedStatement.ProfileCount > 0, "対象 failure query の PROFILE callback を観測できませんでした。");
            Assert.IsTrue(targetedStatement.VmSteps > 0, "対象 failure query の VM_STEP を観測できませんでした。");
            Assert.AreEqual(0, targetedStatement.FullScanSteps, "対象 failure query が全表走査になりました。");
            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.ContainsKey(targetMd5));
            return new TargetedFailureQueryRun(
                targetedStatement.RowCount,
                targetedStatement.FullScanSteps,
                targetedStatement.VmSteps,
                targetedStatement.ProfileCount);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static bool IsTargetedFailureStatement(SqliteStatementObservation.SqliteObservedStatement statement)
    {
        return statement != null
            && statement.Sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && statement.Sql.Contains(
                "FROM chart_info_parse_failure",
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TargetedFailureQueryRun(
        int ReturnedRows,
        int FullScanSteps,
        int VmSteps,
        int ProfileCount);

}
