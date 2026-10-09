using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Tests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// Owns chart-info lookup, hydration, inline evaluation, and candidate cases.
/// </summary>
[TestClass]
public sealed class ChartInfoInlineHydrationTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

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

            Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
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

            Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
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

            Dictionary<string, BeMusicSeeker.Models.ChartDetails> bySha256 = gateway.LoadChartInfosBySha256([secondSha]);
            Dictionary<string, BeMusicSeeker.Models.ChartDetails> byMd5 = gateway.LoadChartInfosByMd5([md5]);

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
            var lockHolder = Task.Run(delegate
            {
                Assert.IsTrue(LR2SongDBExtended.Lock(TimeSpan.FromSeconds(5)));
                try
                {
                    lockAcquired.Set();
                    releaseLock.Wait();
                }
                finally
                {
                    LR2SongDBExtended.Unlock();
                }
            });

            lockAcquired.Wait();
            Task<Dictionary<string, BeMusicSeeker.Models.ChartDetails>> lookupTask = Task.Run(() => gateway.LoadChartInfosBySha256([sha256]));
            try
            {
                Assert.IsTrue(lookupTask.Wait(TimeSpan.FromSeconds(2)), "chart_info lookup should not wait for the writable process lock when schema is current.");
            }
            finally
            {
                releaseLock.Set();
                lockHolder.Wait();
            }

            Dictionary<string, BeMusicSeeker.Models.ChartDetails> rows = lookupTask.Result;
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(md5, rows[sha256].md5);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DeferredChartInfoHydration_BuildsSessionIndexAndUsesSha256BeforeMd5()
    {

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
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);

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
            ChartFile targetDigest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(targetChartPath));
            ChartFile untouchedDigest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(untouchedChartPath));
            ChartFile targetFile = (ChartTestValues.Empty() with { Path = targetChartPath });
            ChartFile untouchedFile = (ChartTestValues.Empty() with { Path = untouchedChartPath });
            targetFile = targetFile with { Md5 = targetDigest.Md5 };
            untouchedFile = untouchedFile with { Md5 = untouchedDigest.Md5 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                gateway,
                [InlineChartSnapshotTarget.FromChart((targetFile), ChartFileContentReader.ReadSnapshot(targetChartPath))],
                new Dictionary<string, BeMusicSeeker.Models.ChartParseFailure>(StringComparer.OrdinalIgnoreCase));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(targetFile.Md5, result.AppliedRows[0].md5);
            Assert.AreEqual(0, untouchedFile.Sha256?.Length ?? 0);
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
            ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstPath });
            ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondPath });
            firstFile = firstFile with { Md5 = firstSnapshot.Md5 };
            secondFile = secondFile with { Md5 = secondSnapshot.Md5 };
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                new BmsLibraryDbGateway(songDbPath),
                [
                    InlineChartSnapshotTarget.FromChart((firstFile), firstSnapshot),
                    InlineChartSnapshotTarget.FromChart((secondFile), secondSnapshot)
                ],
                new Dictionary<string, BeMusicSeeker.Models.ChartParseFailure>(StringComparer.OrdinalIgnoreCase));

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
            ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstPath });
            ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondPath });
            firstFile = firstFile with { Md5 = firstSnapshot.Md5 };
            secondFile = secondFile with { Md5 = firstSnapshot.Md5 };
            var service = new ChartInfoInlineBuildService(
                new ChartInfoBuildService(),
                parserDegree: 1,
                batchSizeOverride: 1);

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                new BmsLibraryDbGateway(songDbPath),
                [
                    (firstFile),
                    (secondFile)
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
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = snapshot.Md5 };
            file = file with { Sha256 = snapshot.Sha256 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            BeMusicSeeker.Models.ChartDetails expected = CreateChartInfoRow(file.Sha256, file.Md5, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([expected]);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            var currentFailures = new Dictionary<string, BeMusicSeeker.Models.ChartParseFailure>(StringComparer.OrdinalIgnoreCase)
            {
                [file.Md5] = CreateChartInfoParseFailureRow(
                    file.Md5,
                    file.Sha256,
                    chartPath,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "current failure must lose to current info",
                    null)
            };

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                gateway,
                [InlineChartSnapshotTarget.FromChart((file), snapshot)],
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
        ChartFile file = (ChartTestValues.Empty() with { Path = @"C:\Charts\current.bms" });
        file = file with { Md5 = md5 };
        file = file with { Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, bytes, DateTime.UtcNow, md5, sha256);
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(
            (file));
        BeMusicSeeker.Models.ChartDetails currentRow = CreateChartInfoRow(
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
        ChartFile file = (ChartTestValues.Empty() with { Path = @"C:\Charts\mismatch.bms" });
        file = file with { Md5 = md5 };
        file = file with { Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, bytes, DateTime.UtcNow, md5, sha256);
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(
            (file));
        BeMusicSeeker.Models.ChartDetails incompatibleRow = CreateChartInfoRow(
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
            ChartFile captured = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath)) with { Sha256 = string.Empty };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                gateway,
                [captured]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(0, result.SuccessCount);
            Assert.IsTrue(string.IsNullOrWhiteSpace(captured.Sha256));
            Assert.AreEqual(1, result.StorageApplications.Count);
            var mutationOwner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), gateway);
            CatalogChartInfoStorageWriteReceipt receipt = mutationOwner.ApplyChartInfoStorageWrite(
                new CatalogChartInfoStorageWriteRequest(
                    result.StorageApplications.Select(application => application.CreateCurrentValue()),
                    new CatalogChartInfoWriteRequest(
                        chartInfoRows: result.ChartInfoRows,
                        parseFailureRows: result.ParseFailureRows,
                        parseFailureDeleteMd5s: result.ParseFailureDeleteMd5s)));
            Assert.IsTrue(receipt.Applied);
            ChartFile committed = result.StorageApplications.Single().CreateCurrentValue();
            Assert.IsFalse(string.IsNullOrWhiteSpace(committed.Sha256));
            Assert.IsTrue(string.IsNullOrWhiteSpace(captured.Sha256));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + committed.Md5 + "' AND sha256 = '" + committed.Sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            BeMusicSeeker.Models.ChartParseFailure failure = verify.Query<BeMusicSeeker.Models.ChartParseFailure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", committed.Md5).Single();
            Assert.AreEqual(committed.Sha256, failure.sha256);
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

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string bmsSha = new('1', 64);
            string bmsonSha = new('2', 64);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = Path.Combine(tempRootPath, "hydrated.bms")
            };
            file = file with { Md5 = new string('a', 32) };
            file = file with { Sha256 = bmsSha };
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempRootPath, "hydrated.bmson"),
                Md5 = new string('b', 32),
                Sha256 = bmsonSha
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            BeMusicSeeker.Models.ChartDetails bmsRow = CreateChartInfoRow(bmsSha, file.Md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            BeMusicSeeker.Models.ChartDetails bmsonRow = CreateChartInfoRow(bmsonSha, bmsonSong.Md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([bmsRow, bmsonRow]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
            {
                BmsCharts = [file],
                BmsonCharts = [bmsonSong]
            };

            InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false);

            await AwaitChartInfoHydrationAsync(library);
            Assert.IsFalse(library.ChartInfoHydrationRunning);
            Assert.AreEqual(2, library.ChartInfoHydrationTotalCount);
            Assert.AreEqual(0, library.ChartInfoHydrationAppliedCount);
            BeMusicSeeker.Models.ChartDetails resolvedBmsRow = library.ResolveChartInfo(file.Sha256, file.Md5);
            BeMusicSeeker.Models.ChartDetails resolvedBmsonRow = library.ResolveChartInfo(bmsonSong.Sha256, bmsonSong.Md5);
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

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "current.bms");
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = md5 };
            file = file with { Sha256 = sha };
            BeMusicSeeker.Models.ChartDetails row = CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            row = row with { speedchange = "120.0,0.0;240.0,1000.0" };
            row = row with { lanenotes = "1,2,3,4" };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(row), typeof(LR2SongDBExtended.chart_info));
            }
            bool originalOperationMode = testSettings.OperationModeLR2DB;
            try
            {
                testSettings.OperationModeLR2DB = operationModeLr2Db;
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
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
                {
                    BmsCharts = [file]
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

                BeMusicSeeker.Models.ChartDetails resolved = library.ResolveChartInfo(sha, md5);

                Assert.IsNotNull(resolved);
                Assert.AreEqual(sha, resolved.sha256);
                AssertChartInfoDisplayProjectionEquivalent(row, resolved);
            }
            finally
            {
                testSettings.OperationModeLR2DB = originalOperationMode;
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
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = md5 };
            file = file with { Sha256 = sha256 };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha256), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                    CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
            }

            var gateway = new BmsLibraryDbGateway(songDbPath);
            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            storageRowsOwner.ReplaceCharts([file], null, replaceBmson: false);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            ownedCollectionOwner.ReplaceCharts(storageRowsOwner.BmsRows, storageRowsOwner.BmsonRows);
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, gateway);
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
            owner.ConfigureWorkflow(gateway, mutationOwner, ownedCollectionOwner, logs.Enqueue, events.Enqueue);

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
    public async Task CatalogChartInfoOwner_BackfillProgressKeepsCapturedRequestWhenAnotherRequestIsAccepted()
    {
        await WithTemporarySongDb(async (tempRootPath, songDbPath) =>
        {
            string chartPath = Path.Combine(tempRootPath, "request-progress.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE progress\r\n#BPM 130\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, file.Md5);
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            storageRowsOwner.ReplaceCharts([file], null, replaceBmson: false);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            ownedCollectionOwner.ReplaceCharts(storageRowsOwner.BmsRows, storageRowsOwner.BmsonRows);
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, gateway);
            long operationToken = 11;
            var execution = new ConcurrentQueue<(OperationProgressRequest Request, bool Running)>();
            var snapshots = new ConcurrentQueue<ChartInfoWorkflowProgressSnapshot>();
            var processedRequests = new ConcurrentQueue<int>();
            var captured = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(false);
            int gateUsed = 0;
            CatalogChartInfoOwner? owner = null;
            owner = new CatalogChartInfoOwner(
                propertyName =>
                {
                    if (propertyName == nameof(BMSLibrary.ChartInfoBackfillProgressSnapshot)
                        && owner is { } currentOwner)
                    {
                        snapshots.Enqueue(currentOwner.BackfillProgressSnapshot);
                    }
                },
                () => false,
                (_, _) => false,
                null,
                _ => { });
            owner.ProgressRequestFactory = (name, version) => new(7, operationToken, name, version);
            owner.RequestProgressReporter = (request, running) => execution.Enqueue((request, running));
            owner.ConfigureWorkflow(gateway, mutationOwner, ownedCollectionOwner, _ => { }, _ => { }, beginDigestMutationWindow: () =>
                {
                    processedRequests.Enqueue(owner.ChartInfoBackfillRequestedVersion);
                    if (Interlocked.Exchange(ref gateUsed, 1) == 0)
                    {
                        captured.TrySetResult(owner.ChartInfoBackfillRequestedVersion);
                        release.Wait();
                    }
                    return new TestMutationWindow();
                });

            var worker = Task.Run(() => owner.QueueBackfill("first", processSynchronously: true));
            try
            {
                Task first = await Task.WhenAny(captured.Task, worker);
                if (first == worker)
                {
                    await worker;
                    Assert.Fail("Backfillの実処理が要求版捕捉ゲートへ到達しませんでした。");
                }
                int firstVersion = await captured.Task;
                operationToken = 22;
                owner.QueueBackfill("next");
                int nextVersion = owner.ChartInfoBackfillRequestedVersion;
                Assert.IsTrue(nextVersion > firstVersion);
                snapshots.Clear();
                release.Set();
                await worker;

                ChartInfoWorkflowProgressSnapshot retainedFirst = snapshots.First(snapshot =>
                    snapshot.RequestVersion == firstVersion && snapshot.ProcessedCount > 0);
                Assert.AreEqual(1, retainedFirst.TotalCount);
                Assert.AreEqual(1, retainedFirst.ProcessedCount);
                Assert.AreEqual(chartPath, retainedFirst.CurrentPath);
                Assert.IsTrue(snapshots.Any(snapshot => snapshot.RequestVersion == nextVersion
                    && snapshot.TotalCount == 0 && snapshot.ProcessedCount == 0));
                Assert.IsTrue(processedRequests.Contains(nextVersion));
                Assert.AreEqual(nextVersion, owner.BackfillProgressSnapshot.RequestVersion);
                Assert.AreEqual(nextVersion, owner.ChartInfoBackfillCompletedVersion);
                Assert.IsFalse(owner.ChartInfoBackfillRunning);
                Assert.AreEqual(new OperationProgressRequest(7, 11, "chart_info_backfill", firstVersion), retainedFirst.Request);
                Assert.IsTrue(execution.Contains((new(7, 11, "chart_info_backfill", firstVersion), true)));
                Assert.IsTrue(execution.Contains((new(7, 11, "chart_info_backfill", firstVersion), false)));
                Assert.IsTrue(execution.Contains((new(7, 22, "chart_info_backfill", nextVersion), true)));
                Assert.IsTrue(execution.Contains((new(7, 22, "chart_info_backfill", nextVersion), false)));
                Assert.AreEqual(firstVersion, retainedFirst.RequestVersion);
                Assert.AreEqual(1, retainedFirst.ProcessedCount);
                Assert.AreEqual(chartPath, retainedFirst.CurrentPath);
            }
            finally
            {
                release.Set();
                await worker;
            }
        });
    }

    [TestMethod]
    public async Task CatalogChartInfoOwner_HydrationProgressKeepsCapturedRequestWhenAnotherRequestIsAccepted()
    {
        await WithTemporarySongDb(async (tempRootPath, songDbPath) =>
        {
            string md5 = new('a', 32);
            string sha256 = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "hydration-progress.bms");
            ChartFile file = (ChartTestValues.Empty() with { Path = chartPath });
            file = file with { Md5 = md5 };
            file = file with { Sha256 = sha256 };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha256), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            storageRowsOwner.ReplaceCharts([file], null, replaceBmson: false);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            ownedCollectionOwner.ReplaceCharts(storageRowsOwner.BmsRows, storageRowsOwner.BmsonRows);
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, gateway);
            long operationToken = 11;
            var execution = new ConcurrentQueue<(OperationProgressRequest Request, bool Running)>();
            var snapshots = new ConcurrentQueue<ChartInfoWorkflowProgressSnapshot>();
            var captured = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(false);
            Func<Task>? scheduledProcess = null;
            int gateUsed = 0;
            CatalogChartInfoOwner? owner = null;
            owner = new CatalogChartInfoOwner(
                propertyName =>
                {
                    if (owner is not { } currentOwner)
                    {
                        return;
                    }
                    if (propertyName == nameof(BMSLibrary.ChartInfoHydrationProgressSnapshot))
                    {
                        snapshots.Enqueue(currentOwner.HydrationProgressSnapshot);
                    }
                    if (propertyName == nameof(BMSLibrary.ChartInfoIndexVersion)
                        && Interlocked.Exchange(ref gateUsed, 1) == 0)
                    {
                        captured.TrySetResult(currentOwner.ChartInfoHydrationRequestedVersion);
                        release.Wait();
                    }
                },
                () => false,
                (_, _) => false,
                () => (_, _, _, process) =>
                {
                    scheduledProcess = process;
                    return true;
                },
                _ => { });
            owner.ProgressRequestFactory = (name, version) => new(7, operationToken, name, version);
            owner.RequestProgressReporter = (request, running) => execution.Enqueue((request, running));
            owner.ConfigureWorkflow(gateway, mutationOwner, ownedCollectionOwner, _ => { }, _ => { });
            owner.QueueDeferredHydration("first", queueFullBackfillAfterHydration: false);
            Assert.IsNotNull(scheduledProcess);
            var worker = Task.Run(scheduledProcess);
            try
            {
                Task first = await Task.WhenAny(captured.Task, worker);
                if (first == worker)
                {
                    await worker;
                    Assert.Fail("Hydrationの実処理が索引変更ゲートへ到達しませんでした。");
                }
                int firstVersion = await captured.Task;
                operationToken = 22;
                owner.QueueDeferredHydration("next", queueFullBackfillAfterHydration: false);
                int nextVersion = owner.ChartInfoHydrationRequestedVersion;
                Assert.IsTrue(nextVersion > firstVersion);
                snapshots.Clear();
                release.Set();
                await worker;

                ChartInfoWorkflowProgressSnapshot retainedFirst = snapshots.First(snapshot =>
                    snapshot.RequestVersion == firstVersion && snapshot.TotalCount == 1);
                Assert.AreEqual(0, retainedFirst.ProcessedCount);
                Assert.IsTrue(snapshots.Any(snapshot => snapshot.RequestVersion == nextVersion
                    && snapshot.TotalCount == 1 && snapshot.ProcessedCount == 0));
                Assert.AreEqual(nextVersion, owner.HydrationProgressSnapshot.RequestVersion);
                Assert.AreEqual(nextVersion, owner.ChartInfoHydrationCompletedVersion);
                Assert.IsFalse(owner.ChartInfoHydrationRunning);
                Assert.AreEqual(0, owner.ChartInfoBackfillRequestedVersion);
                Assert.AreEqual(new OperationProgressRequest(7, 11, "chart_info_hydration", firstVersion), retainedFirst.Request);
                Assert.IsTrue(execution.Contains((new(7, 11, "chart_info_hydration", firstVersion), true)));
                Assert.IsTrue(execution.Contains((new(7, 11, "chart_info_hydration", firstVersion), false)));
                Assert.IsTrue(execution.Contains((new(7, 22, "chart_info_hydration", nextVersion), true)));
                Assert.IsTrue(execution.Contains((new(7, 22, "chart_info_hydration", nextVersion), false)));
                Assert.AreEqual(firstVersion, retainedFirst.RequestVersion);
                Assert.AreEqual(1, retainedFirst.TotalCount);
                Assert.AreEqual(0, retainedFirst.ProcessedCount);
            }
            finally
            {
                release.Set();
                await worker;
            }
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
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            string oldMd5 = file.Md5;
            string oldSha256 = file.Sha256;
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE publication order after\r\n#PLAYLEVEL 7\r\n#BPM 130\r\n#00111:01\r\n",
                Encoding.ASCII);
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
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
                    Assert.IsNotNull(library.ResolveChartInfo(library.BmsCharts.Single().Sha256, library.BmsCharts.Single().Md5));
                }
                else if (args.PropertyName == nameof(BMSLibrary.OwnedCollectionVersion))
                {
                    publicationOrder.Add("digest_effects");
                    Assert.IsNotNull(library.ResolveChartInfo(library.BmsCharts.Single().Sha256, library.BmsCharts.Single().Md5));
                    Assert.IsTrue(library.GetOwnedChartHashIndexSnapshot().ContainsMd5(library.BmsCharts.Single().Md5));
                    PlaylistLibraryResolveIndexSnapshot notificationResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                        CancellationToken.None,
                        out bool cacheHit,
                        out int staleRetries);
                    Assert.IsTrue(cacheHit);
                    Assert.AreEqual(0, staleRetries);
                    Assert.IsNull(notificationResolve.ResolveChartForPlaylistHash(oldMd5, null));
                    Assert.AreEqual(
                        chartPath,
                        notificationResolve.ResolveChartForPlaylistHash(library.BmsCharts.Single().Md5, null).Path);
                    playlistResolvedAtDigestNotification = true;
                }
            };

            ChartInfoInlineBuildResult result = OwnedChartCollectionTestSupport.InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "publication_order",
                [(file)]);

            Assert.IsTrue(publicationOrder.IndexOf("session_index") >= 0);
            Assert.IsTrue(publicationOrder.IndexOf("digest_effects") >= 0);
            Assert.IsTrue(
                publicationOrder.IndexOf("session_index") < publicationOrder.IndexOf("digest_effects"),
                "chart-info session index must be observable before digest-dependent collection notification.");
            Assert.IsTrue(playlistResolvedAtDigestNotification);
            Assert.AreEqual(oldMd5, file.Md5);
            file = library.BmsCharts.Single();
            Assert.AreNotEqual(oldMd5, file.Md5);
            Assert.AreNotEqual(oldSha256, file.Sha256);
            Assert.IsTrue(result.DigestChanges.Count > 0);
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task DeferredChartInfoHydration_MissingCurrentRowBackfillsRegardlessOfCompletedLr2Status(bool operationModeLr2Db)
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 13\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
            file = file with { Sha256 = digest.Sha256 };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, file.Md5);
                songDb.InsertOrReplace(CreateChartDigestRow(file.Md5, file.Sha256), typeof(LR2SongDBExtended.chart_digest_map));
                if (operationModeLr2Db)
                {
                    string signature = Lr2SongDbSyncSignatureBuilder.Build(new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true });
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, signature, "unit-test", 1, DateTime.UtcNow);
                }
            }
            bool originalOperationMode = testSettings.OperationModeLR2DB;
            try
            {
                testSettings.OperationModeLR2DB = operationModeLr2Db;
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
                {
                    BmsCharts = [file]
                };

                ChartFile captured = library.BmsCharts.Single();
                int initialOwnedVersion = library.OwnedCollectionVersion;
                InvokeDeferredChartInfoHydration(library, "unit_test_missing", queueFullBackfillAfterHydration: true);

                await AwaitChartInfoHydrationAsync(library);
                await AwaitChartInfoBackfillAsync(library);
                using var verify = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", file.Sha256));
                Assert.AreEqual(13, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
                Assert.AreEqual(13, library.BmsCharts.Single().Level);
                Assert.AreSame(captured.Token, library.BmsCharts.Single().Token);
                Assert.AreEqual(initialOwnedVersion + 1, library.OwnedCollectionVersion);
                Assert.IsNull(file.Level);
            }
            finally
            {
                testSettings.OperationModeLR2DB = originalOperationMode;
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DeferredChartInfoHydration_SkipsFullBackfillWhenNoCandidates()
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);

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
                BeMusicSeeker.Models.ChartDetails currentRow = CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
                currentRow = currentRow with { level = null };
                currentRow = currentRow with { difficulty = 3 };
                currentRow = currentRow with { difficulty_defined = false };
                currentRow = currentRow with { mainbpm = 123.5 };
                currentRow = currentRow with { total = null };
                currentRow = currentRow with { total_defined = true };
                currentRow = currentRow with { density = 12.25 };
                currentRow = currentRow with { speedchange_count = 2 };
                currentRow = currentRow with { updated_at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
                BeMusicSeeker.Models.ChartDetails staleRow = CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1);
                staleRow = staleRow with { charthash = new string('e', 64) };
                staleRow = staleRow with { distribution = "1,2,3" };
                staleRow = staleRow with { speedchange = "120.0,0.0;240.0,1.0" };
                staleRow = staleRow with { lanenotes = "1,2,3,4" };

                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(currentRow), typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(staleRow), typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                        CreateChartInfoParseFailureRow(currentFailureMd5, currentFailureSha, Path.Combine(tempRootPath, "current-failure.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null)), typeof(LR2SongDBExtended.chart_info_parse_failure));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                        CreateChartInfoParseFailureRow(staleFailureMd5, staleFailureSha, Path.Combine(tempRootPath, "stale-timeout.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "timeout", "ChartInfoParseTimeoutException", "old timeout", 500)), typeof(LR2SongDBExtended.chart_info_parse_failure));
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

                Dictionary<string, BeMusicSeeker.Models.ChartDetails> fullRows = gateway.LoadChartInfosBySha256([currentSha, staleSha]);

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
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1)), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                    CreateChartInfoParseFailureRow(failureMd5, failureSha, Path.Combine(tempRootPath, "failure.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null)), typeof(LR2SongDBExtended.chart_info_parse_failure));
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
            Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
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

    private sealed class TestMutationWindow : IDisposable
    {
        public void Dispose() { }
    }

    private sealed record TargetedFailureQueryRun(
        int ReturnedRows,
        int FullScanSteps,
        int VmSteps,
        int ProfileCount);

}
