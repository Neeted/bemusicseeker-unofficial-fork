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
using ChartInfoExportTool;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

public sealed partial class ChartInfoMetadataOwnerTests
{
    [TestMethod]
    public void BackfillChartInfos_ParsesMissingRowsSkipsCurrentRowsAndReparsesStaleRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "backfill.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            var file = BMSFile.CreateBMSFileFromFile(chartPath);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService();
            List<Tuple<int, int, string>> progress = [];

            ChartInfoBackfillResult first = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                (total, processed, currentPath) => progress.Add(Tuple.Create(total, processed, currentPath)));

            Assert.AreEqual(1, first.TargetCount);
            Assert.AreEqual(1, first.ProcessedCount);
            Assert.AreEqual(1, first.BackfilledCount);
            Assert.AreEqual(0, first.FailedCount);
            Assert.AreEqual("full", first.Mode);
            Assert.AreEqual(1, first.FileReadCount);
            Assert.AreEqual(new FileInfo(chartPath).Length, first.FileReadBytes);
            Assert.AreEqual(1L, CountChartInfoRows(songDbPath, file.sha256));
            Assert.AreEqual(1, progress.Last().Item1);
            Assert.AreEqual(1, progress.Last().Item2);

            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    file.hash,
                    file.sha256,
                    chartPath,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "current failure must lose to current info",
                    null)
            ]);
            ChartInfoBackfillResult second = BackfillChartInfos(service,
                gateway,
                [file],
                []);

            Assert.AreEqual(0, second.TargetCount);
            Assert.AreEqual(0, second.BackfilledCount);
            Assert.AreEqual(1, second.CurrentRowSkippedCount);
            Assert.AreEqual(0, second.FileReadCount);
            Assert.AreEqual(0L, second.FileReadBytes);

            gateway.UpsertChartInfos([CreateChartInfoRow(file.sha256, file.hash, parserVersion: 0)]);
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    file.hash,
                    file.sha256,
                    chartPath,
                    parserVersion: 0,
                    "parse_failed",
                    "InvalidDataException",
                    "stale failure permits reparse",
                    null)
            ]);
            ChartInfoBackfillResult third = BackfillChartInfos(service,
                gateway,
                [file],
                []);

            Assert.AreEqual(1, third.TargetCount);
            Assert.AreEqual(1, third.BackfilledCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, verify.ExecuteScalar<int>("SELECT parser_version FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReadsOnceAndPersistsDigestAndInfoForMissingSha256()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "single-read.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var readCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var service = new ChartInfoBuildService(delegate (string path)
            {
                readCounts[path] = readCounts.TryGetValue(path, out int count) ? count + 1 : 1;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            List<string> logs = [];
            object logsSync = new();
            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("INFO " + message);
                    }
                },
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("WARN " + message);
                    }
                });

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ProcessedCount);
            Assert.AreEqual(1, result.DigestTargetCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.AreEqual(1, readCounts[chartPath]);
            Assert.AreEqual(1, result.FileReadCount);
            Assert.AreEqual(new FileInfo(chartPath).Length, result.FileReadBytes);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill total=", StringComparison.Ordinal) && message.Contains("fileReadCount=1") && message.Contains("fileReadBytes=" + new FileInfo(chartPath).Length)));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_UpdatesOnlyChartInfoSongProjectionAndPreservesOtherColumns()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "generated-columns.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#PLAYLEVEL 12\r\n#DIFFICULTY 4\r\n#BPM 135\r\n#00111:01\r\n",
                Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile { path = chartPath };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.Execute(
                    "UPDATE song SET title = ?, subtitle = ?, artist = ?, subartist = ?, genre = ?, type = ?, "
                    + "folder = ?, stagefile = ?, banner = ?, backbmp = ?, parent = ?, mode = ?, judge = ?, "
                    + "date = ?, txt = ?, favorite = ?, tag = ?, adddate = ?, "
                    + "level = ?, difficulty = ?, maxbpm = ?, minbpm = ?, bga = ?, exlevel = ?, longnote = ?, random = ?, karinotes = ? "
                    + "WHERE path = ?;",
                    "keep-title",
                    "keep-subtitle",
                    "keep-artist",
                    "keep-subartist",
                    "keep-genre",
                    77,
                    "keep-folder",
                    "keep-stagefile",
                    "keep-banner",
                    "keep-backbmp",
                    "keep-parent",
                    14,
                    9,
                    111111,
                    1,
                    7,
                    "keep-user-tag",
                    123456,
                    1,
                    1,
                    1,
                    2,
                    9,
                    9,
                    9,
                    9,
                    9,
                    chartPath);
            }

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                []);

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.SongProjectionRequestedCount);
            Assert.AreEqual(1, result.SongProjectionMatchedCount);
            Assert.AreEqual(1, result.SongProjectionChangedCount);
            Assert.AreEqual(0, result.SongProjectionMissingCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info chartInfo = verify.Query<LR2SongDBExtended.chart_info>(
                "SELECT * FROM chart_info WHERE sha256 = ?;",
                file.sha256).Single();
            LR2SongDB.song song = verify.Query<LR2SongDB.song>(
                "SELECT * FROM song WHERE path = ?;",
                chartPath).Single();
            Assert.AreEqual(chartInfo.level, song.level);
            Assert.AreEqual(Lr2ChartInfoSongProjection.NormalizeDifficulty(chartInfo.difficulty), song.difficulty);
            Assert.AreEqual((int?)chartInfo.maxbpm, song.maxbpm);
            Assert.AreEqual((int?)chartInfo.minbpm, song.minbpm);
            Assert.AreEqual(chartInfo.bga, song.bga);
            Assert.AreEqual(chartInfo.exlevel ?? 0, song.exlevel);
            Assert.AreEqual(chartInfo.notes, song.karinotes);
            Assert.AreEqual(0, song.longnote);
            Assert.AreEqual(0, song.random);
            Assert.AreEqual(chartInfo.level, file.level);
            Assert.AreEqual(Lr2ChartInfoSongProjection.NormalizeDifficulty(chartInfo.difficulty), file.difficulty);
            Assert.AreEqual((int?)chartInfo.maxbpm, file.maxbpm);
            Assert.AreEqual((int?)chartInfo.minbpm, file.minbpm);
            Assert.AreEqual(chartInfo.bga, file.bga);
            Assert.AreEqual(chartInfo.exlevel ?? 0, file.exlevel);
            Assert.AreEqual(chartInfo.notes, file.karinotes);
            Assert.AreEqual(0, file.longnote);
            Assert.AreEqual(0, file.random);
            Assert.AreEqual(file.hash, song.hash);
            Assert.AreEqual(chartPath, song.path);
            Assert.AreEqual("keep-title", song.title);
            Assert.AreEqual("keep-subtitle", song.subtitle);
            Assert.AreEqual("keep-artist", song.artist);
            Assert.AreEqual("keep-subartist", song.subartist);
            Assert.AreEqual("keep-genre", song.genre);
            Assert.AreEqual(77, song.type);
            Assert.AreEqual("keep-folder", song.folder);
            Assert.AreEqual("keep-stagefile", song.stagefile);
            Assert.AreEqual("keep-banner", song.banner);
            Assert.AreEqual("keep-backbmp", song.backbmp);
            Assert.AreEqual("keep-parent", song.parent);
            Assert.AreNotEqual(chartInfo.mode, song.mode);
            Assert.AreEqual(14, song.mode);
            Assert.AreEqual(9, song.judge);
            Assert.AreEqual(111111, song.date);
            Assert.AreEqual(1, song.txt);
            Assert.AreEqual(7, song.favorite);
            Assert.AreEqual("keep-user-tag", song.tag);
            Assert.AreEqual(123456, song.adddate);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReusedCurrentRowProjectsSongColumnsForMissingDigestCandidate()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "reuse-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE reuse\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var file = new TestableBmsFile { path = chartPath };
            file.SetHash(snapshot.Md5);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            LR2SongDBExtended.chart_info current = CreateChartInfoRow(
                snapshot.Sha256,
                snapshot.Md5,
                BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            current.level = 17;
            current.difficulty = 5;
            current.mode = 14;
            current.updated_at = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            gateway.UpsertChartInfos([current]);
            var committedRows = new List<LR2SongDBExtended.chart_info>();

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                [],
                storageCommitPublished: publication => committedRows.AddRange(publication.AppliedRows),
                existingRowsSnapshot: new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
                {
                    [snapshot.Sha256] = current
                });

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(snapshot.Sha256, file.sha256);
            Assert.AreEqual(17, file.level);
            Assert.AreEqual(5, file.difficulty);
            Assert.AreEqual(5, file.mode);
            Assert.AreEqual(1, committedRows.Count);
            Assert.AreSame(current, committedRows[0]);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(17, song.level);
            Assert.AreEqual(5, song.difficulty);
            Assert.AreEqual(5, song.mode);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", snapshot.Sha256));
            Assert.AreEqual(current.updated_at, verify.ExecuteScalar<DateTime>("SELECT updated_at FROM chart_info WHERE sha256 = ?;", snapshot.Sha256));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", snapshot.Md5, snapshot.Sha256));
        });
    }

    [DataTestMethod]
    [DataRow(null, 2)]
    [DataRow(-1, 2)]
    [DataRow(6, 2)]
    [DataRow(4, 4)]
    public void BackfillChartInfos_ProjectionNormalizationMatchesNewChartPath(
        int? difficulty,
        int expectedDifficulty)
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "projection-parity.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE parity\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var file = new TestableBmsFile { path = chartPath, level = 1, difficulty = 1, mode = 5 };
            file.SetHash(snapshot.Md5);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            LR2SongDBExtended.chart_info current = CreateChartInfoRow(
                snapshot.Sha256,
                snapshot.Md5,
                BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            current.level = null;
            current.difficulty = difficulty;
            current.difficulty_defined = difficulty.HasValue;
            current.maxbpm = 199.9;
            current.minbpm = null;
            current.mode = 14;
            current.judge = 100;
            current.bga = null;
            current.exlevel = null;
            current.feature = 1 | 4 | 32;
            current.notes = 2468;
            gateway.UpsertChartInfos([current]);
            var expected = new TestableBmsFile { path = chartPath, mode = 7 };
            expected.SetHash(snapshot.Md5);
            Lr2SongRowEnricher.EnrichFromChartInfo(expected, current);

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                [],
                existingRowsSnapshot: new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
                {
                    [snapshot.Sha256] = current
                });

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.SongProjectionMatchedCount);
            Assert.AreEqual(expectedDifficulty, expected.difficulty);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(expected.level, song.level);
            Assert.AreEqual(expected.difficulty, song.difficulty);
            Assert.AreEqual(expected.maxbpm, song.maxbpm);
            Assert.AreEqual(expected.minbpm, song.minbpm);
            Assert.AreEqual(expected.bga, song.bga);
            Assert.AreEqual(expected.exlevel, song.exlevel);
            Assert.AreEqual(expected.longnote, song.longnote);
            Assert.AreEqual(expected.random, song.random);
            Assert.AreEqual(expected.karinotes, song.karinotes);
            Assert.AreEqual(5, song.mode);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_MissingSongRowCommitsFactsWithoutInsertingSong()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-song-row.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile { path = chartPath, level = 2, difficulty = 1 };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
            }
            var warnings = new List<string>();
            var committedRows = new List<LR2SongDBExtended.chart_info>();

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                [],
                logInstallPerformanceWarn: warnings.Add,
                storageCommitPublished: publication => committedRows.AddRange(publication.AppliedRows));

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.SongProjectionRequestedCount);
            Assert.AreEqual(0, result.SongProjectionMatchedCount);
            Assert.AreEqual(0, result.SongProjectionChangedCount);
            Assert.AreEqual(1, result.SongProjectionMissingCount);
            CollectionAssert.Contains(result.SongProjectionMissingPaths, chartPath);
            Assert.IsTrue(warnings.Any(message => message.StartsWith("chart_info_backfill song_projection_missing", StringComparison.Ordinal)));
            Assert.AreEqual(1, committedRows.Count);
            Assert.AreEqual(2, file.level);
            Assert.AreEqual(1, file.difficulty);
            Assert.AreEqual(digest.sha256, file.sha256);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", digest.sha256));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", digest.hash, digest.sha256));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReparsesCurrentVersionRowWithMismatchedMd5()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "identity-mismatch.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(
                    file.sha256,
                    new string('f', 32),
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            ]);

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(),
                gateway,
                [file],
                []);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.CurrentRowSkippedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                file.hash,
                verify.ExecuteScalar<string>("SELECT md5 FROM chart_info WHERE sha256 = ?;", file.sha256));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_IgnoresMaintenanceEncodingAndUsesBeatorajaDefaultDecode()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "ms932-fullwidth-level.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL １\r\n"
                    + "#00111:01\r\n",
                Encoding.GetEncoding(932));

            var digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            file.SetMaintenanceInfo(
                new BMSFileMaintenanceInfo(file)
                {
                    encoding = "ks_c_5601-1987?"
                },
                suppressPropertyChanged: true);

            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                []);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info row = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", file.sha256).Single();
            Assert.AreEqual(1, row.level);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_GroupsDuplicateMissingSha256TargetsByMd5()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartAPath = Path.Combine(tempRootPath, "duplicate-a.bms");
            string chartBPath = Path.Combine(tempRootPath, "duplicate-b.bms");
            string text = "#PLAYER 1\r\n#PLAYLEVEL 11\r\n#BPM 120\r\n#00111:01\r\n";
            File.WriteAllText(chartAPath, text, Encoding.ASCII);
            File.WriteAllText(chartBPath, text, Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartAPath);
            var fileA = new TestableBmsFile { path = chartAPath };
            var fileB = new TestableBmsFile { path = chartBPath };
            fileA.SetHash(digest.hash);
            fileB.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([fileA, fileB]);
            int readCount = 0;
            var service = new ChartInfoBuildService(delegate (string path)
            {
                readCount++;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [fileA, fileB],
                []);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(2, result.DigestTargetCount);
            Assert.AreEqual(2, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, readCount);
            Assert.AreEqual(fileA.sha256, fileB.sha256);
            Assert.AreEqual(1L, CountChartInfoRows(songDbPath, fileA.sha256));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info chartInfo = verify.Query<LR2SongDBExtended.chart_info>(
                "SELECT * FROM chart_info WHERE sha256 = ?;",
                fileA.sha256).Single();
            List<LR2SongDB.song> songs = verify.Query<LR2SongDB.song>(
                "SELECT * FROM song WHERE hash = ? ORDER BY path;",
                fileA.hash);
            Assert.AreEqual(2, songs.Count);
            Assert.IsTrue(songs.All(song => song.level == chartInfo.level));
            Assert.AreEqual(11, fileA.level);
            Assert.AreEqual(11, fileB.level);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", fileA.hash, fileA.sha256));
            CollectionAssert.AreEquivalent(new[] { chartAPath, chartBPath }, songs.Select(song => song.path).ToArray());
        });
    }

    [TestMethod]
    public void ChartInfoBuildTargetMapper_SeparatesBmsDigestAndBmsonIdentityRules()
    {
        string bmsMd5 = new string('a', 32);
        string bmsSha256 = new string('b', 64);
        string bmsonMd5 = new string('c', 32);
        string bmsonSha256 = new string('d', 64);
        var bmsFile = new TestableBmsFile { path = @"C:\Charts\a.bms" };
        bmsFile.SetHash(bmsMd5);
        bmsFile.SetSha256(bmsSha256);
        LR2SongDBExtended.bmson_song bmsonSong = new()
        {
            path = @"C:\Charts\b.bmson",
            md5 = bmsonMd5,
            sha256 = bmsonSha256
        };

        ChartFile bmsChart = ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false);
        ChartFile bmsonChart = ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false);

        Assert.AreEqual("md5:" + bmsMd5, ChartInfoBuildTargetMapper.BuildKey(bmsChart));
        Assert.AreEqual("sha256:" + bmsonSha256, ChartInfoBuildTargetMapper.BuildKey(bmsonChart));
        Assert.IsFalse(ChartInfoBuildTargetMapper.ShouldSkipBackfillTarget(bmsChart));
        Assert.IsFalse(ChartInfoBuildTargetMapper.IsDigestBackfillTarget(bmsChart));

        var missingDigestBmsFile = new TestableBmsFile { path = @"C:\Charts\missing-digest.bms" };
        missingDigestBmsFile.SetHash(new string('e', 32));
        ChartFile missingDigestBmsChart = ChartFileProjection.FromBmsFile(missingDigestBmsFile, includeWarningSnapshot: false);
        Assert.IsTrue(ChartInfoBuildTargetMapper.IsDigestBackfillTarget(missingDigestBmsChart));

        var noHashBmsFile = new TestableBmsFile { path = @"C:\Charts\no-hash.bms" };
        ChartFile noHashBmsChart = ChartFileProjection.FromBmsFile(noHashBmsFile, includeWarningSnapshot: false);
        Assert.IsTrue(ChartInfoBuildTargetMapper.ShouldSkipBackfillTarget(noHashBmsChart));
    }

    [TestMethod]
    public void ChartInfoBuildTarget_ApplyDigestUpdatesOnlyMissingBmsSha256()
    {
        string sharedDigest = new string('f', 64);
        var bmsFile = new TestableBmsFile { path = @"C:\Charts\a.bms" };
        bmsFile.SetHash(new string('a', 32));
        LR2SongDBExtended.bmson_song bmsonSong = new()
        {
            path = @"C:\Charts\b.bmson",
            md5 = new string('b', 32),
            sha256 = new string('c', 64)
        };
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false));
        target.AddChart(ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false));
        var completedDigestFiles = new List<BMSFile>();
        var digestChanges = new List<LibraryChartDigestChange>();

        int applied = target.ApplyDigest(sharedDigest, completedDigestFiles, digestChanges);

        Assert.AreEqual(1, applied);
        Assert.AreEqual(sharedDigest, bmsFile.sha256);
        Assert.AreEqual(new string('c', 64), bmsonSong.sha256);
        CollectionAssert.Contains(completedDigestFiles, bmsFile);
        Assert.AreEqual(1, digestChanges.Count);
        Assert.AreEqual(LibraryChartKind.Bms, digestChanges[0].Kind);
        Assert.AreEqual(bmsFile.path, digestChanges[0].Path);
        Assert.AreEqual(new string('a', 32), digestChanges[0].OldMd5);
        Assert.IsTrue(string.IsNullOrWhiteSpace(digestChanges[0].OldSha256));
        Assert.AreEqual(new string('a', 32), digestChanges[0].NewMd5);
        Assert.AreEqual(sharedDigest, digestChanges[0].NewSha256);
        Assert.IsFalse(digestChanges[0].Md5Changed);
        Assert.IsTrue(digestChanges[0].Sha256Changed);
        Assert.IsFalse(digestChanges[0].PrimaryHashChanged);
    }

    [TestMethod]
    public void ChartStorageOwnerMutator_AppliesBmsonDigestOnlyAfterCommittedStorageWrite()
    {
        string oldMd5 = new string('b', 32);
        string oldSha256 = new string('c', 64);
        string newMd5 = new string('d', 32);
        string newSha256 = new string('e', 64);
        LR2SongDBExtended.bmson_song bmsonSong = new()
        {
            path = @"C:\Charts\b.bmson",
            md5 = oldMd5,
            sha256 = oldSha256
        };
        ChartFile chart = ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false);
        var snapshot = new ChartFileSnapshot(bmsonSong.path, [1, 2, 3], DateTime.UtcNow, newMd5, newSha256);
        var digestChanges = new List<LibraryChartDigestChange>();

        LR2SongDBExtended.bmson_song persistenceCopy = ChartStorageOwnerMutator.CreateBmsonPersistenceCopy(
            chart,
            snapshot.Md5,
            snapshot.Sha256,
            snapshot.LastWriteTimeUtc);

        Assert.AreEqual(oldMd5, bmsonSong.md5);
        Assert.AreEqual(oldSha256, bmsonSong.sha256);
        Assert.AreEqual(newMd5, persistenceCopy.md5);
        Assert.AreEqual(newSha256, persistenceCopy.sha256);

        int applied = ChartStorageOwnerMutator.ApplyCommittedSnapshot(
            chart,
            snapshot.Md5,
            snapshot.Sha256,
            snapshot.LastWriteTimeUtc,
            row: null,
            digestChanges: digestChanges);

        Assert.AreEqual(0, applied);
        Assert.AreEqual(newMd5, bmsonSong.md5);
        Assert.AreEqual(newSha256, bmsonSong.sha256);
        Assert.AreEqual(1, digestChanges.Count);
        Assert.AreEqual(LibraryChartKind.Bmson, digestChanges[0].Kind);
        Assert.AreEqual(bmsonSong.path, digestChanges[0].Path);
        Assert.AreEqual(oldMd5, digestChanges[0].OldMd5);
        Assert.AreEqual(oldSha256, digestChanges[0].OldSha256);
        Assert.AreEqual(newMd5, digestChanges[0].NewMd5);
        Assert.AreEqual(newSha256, digestChanges[0].NewSha256);
    }

    [TestMethod]
    public void BackfillChartInfos_AppliesChartInfoToBmsonStorageOwner()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "backfill-target.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"title\":\"backfill target\",\"level\":6,\"mode_hint\":\"beat-7k\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"lines\":[{\"y\":0}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(chartPath);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [],
                [song]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.DigestTargetCount);
            Assert.AreEqual(0, result.DigestBackfilledCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info row = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", song.sha256).Single();
            Assert.AreEqual(song.md5, row.md5);
            Assert.AreEqual(song.sha256, row.sha256);
            Assert.AreEqual(6, row.level);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'song';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_TransactionFailureDoesNotPublishCanonicalDigestSongOrIndex()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "rollback.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile { path = chartPath, level = 2 };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            bool indexPublished = false;
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            AggregateException exception = Assert.ThrowsException<AggregateException>(() =>
                service.BackfillChartInfos(
                    gateway,
                    CreateChartSnapshot([file], []),
                    storageCommitPublished: _ => indexPublished = true,
                    chartInfoChunkWriter: request => ApplyChartInfoStorageRequest(
                        gateway,
                        request,
                        _ => throw new InvalidOperationException("injected transaction failure"))));

            Assert.IsTrue(exception.Flatten().InnerExceptions.Any(inner => inner.Message.Contains("injected transaction failure", StringComparison.Ordinal)));
            Assert.IsFalse(indexPublished);
            Assert.IsTrue(string.IsNullOrWhiteSpace(file.sha256));
            Assert.AreEqual(2, file.level);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(2, song.level);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_LaterChunkFailureKeepsEarlierPublicationAndDoesNotPublishFailedChunk()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "first-chunk.bms");
            string secondPath = Path.Combine(tempRootPath, "second-chunk.bms");
            File.WriteAllText(firstPath, "#PLAYER 1\r\n#PLAYLEVEL 8\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(secondPath, "#PLAYER 1\r\n#PLAYLEVEL 10\r\n#BPM 140\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile firstDigest = BMSFile.CreateBMSFileFromFile(firstPath);
            BMSFile secondDigest = BMSFile.CreateBMSFileFromFile(secondPath);
            var firstFile = new TestableBmsFile { path = firstPath, level = 2 };
            var secondFile = new TestableBmsFile { path = secondPath, level = 3 };
            firstFile.SetHash(firstDigest.hash);
            secondFile.SetHash(secondDigest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([firstFile, secondFile]);
            var service = new ChartInfoBuildService(
                File.ReadAllBytes,
                workerCountOverride: 1,
                commitChunkSizeOverride: 1);
            int writerCalls = 0;
            var publications = new List<ChartInfoStorageCommitPublication>();

            Assert.ThrowsException<AggregateException>(() =>
                service.BackfillChartInfos(
                    gateway,
                    CreateChartSnapshot([firstFile, secondFile], []),
                    storageCommitPublished: publications.Add,
                    chartInfoChunkWriter: request =>
                    {
                        writerCalls++;
                        return ApplyChartInfoStorageRequest(
                            gateway,
                            request,
                            writerCalls == 2
                                ? _ => throw new InvalidOperationException("injected later chunk failure")
                                : null);
                    }));

            Assert.AreEqual(2, writerCalls);
            Assert.AreEqual(1, publications.Count);
            string committedPath = publications[0].DigestChanges.Single().Path;
            TestableBmsFile committedFile = string.Equals(committedPath, firstPath, StringComparison.OrdinalIgnoreCase)
                ? firstFile
                : secondFile;
            TestableBmsFile failedFile = ReferenceEquals(committedFile, firstFile) ? secondFile : firstFile;
            string committedSha256 = ReferenceEquals(committedFile, firstFile) ? firstDigest.sha256 : secondDigest.sha256;
            int committedLevel = ReferenceEquals(committedFile, firstFile) ? 8 : 10;
            int failedLevel = ReferenceEquals(failedFile, firstFile) ? 2 : 3;
            Assert.AreEqual(committedSha256, committedFile.sha256);
            Assert.AreEqual(committedLevel, committedFile.level);
            Assert.IsTrue(string.IsNullOrWhiteSpace(failedFile.sha256));
            Assert.AreEqual(failedLevel, failedFile.level);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(committedLevel, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", committedFile.path));
            Assert.AreEqual(failedLevel, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", failedFile.path));
        });
    }

}
