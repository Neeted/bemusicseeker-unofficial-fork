using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面情報の補完、保存投影、確定後公開の実DB境界を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoBackfillStorageTests
{
    /// <summary>実DBの現在行・古い行・未登録行を同じ補完入口で評価し、解析版による再解析境界を確認する。</summary>
    [TestMethod]
    public void BackfillChartInfos_ParsesMissingRowsSkipsCurrentRowsAndReparsesStaleRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "backfill.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
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
            Assert.AreEqual(1L, CountChartInfoRows(songDbPath, file.Sha256));
            Assert.AreEqual(1, progress.Last().Item1);
            Assert.AreEqual(1, progress.Last().Item2);

            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    file.Md5,
                    file.Sha256,
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

            gateway.UpsertChartInfos([CreateChartInfoRow(file.Sha256, file.Md5, parserVersion: 0)]);
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    file.Md5,
                    file.Sha256,
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
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, verify.ExecuteScalar<int>("SELECT parser_version FROM chart_info WHERE sha256 = '" + file.Sha256 + "';"));
        });
    }

    /// <summary>補完で更新する譜面情報由来9列だけを実DBへ反映し、利用者列と基本列を保持する。</summary>
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
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = (ChartTestValues.Empty() with { Path = chartPath });
            file = file with { Md5 = digest.Md5 };
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
            BeMusicSeeker.Models.ChartDetails chartInfo = verify.Query<BeMusicSeeker.Models.ChartDetails>(
                "SELECT * FROM chart_info WHERE sha256 = ?;",
                digest.Sha256).Single();
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
            Assert.IsNull(file.ChartInfo);
            Assert.IsNull(file.Sha256);
            Assert.IsNull(file.Level);
            Assert.IsNull(file.Difficulty);
            Assert.AreEqual(file.Md5, song.hash);
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

    /// <summary>現在のchart_infoを再利用する欠落SHA候補でも、所持BMSへ9列投影を確定後に適用する。</summary>
    [TestMethod]
    public void BackfillChartInfos_ReusedCurrentRowProjectsSongColumnsForMissingDigestCandidate()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "reuse-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE reuse\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = (ChartTestValues.Empty() with { Path = chartPath });
            file = file with { Md5 = snapshot.Md5 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            BeMusicSeeker.Models.ChartDetails current = CreateChartInfoRow(
                snapshot.Sha256,
                snapshot.Md5,
                BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            current = current with { level = 17 };
            current = current with { difficulty = 5 };
            current = current with { mode = 14 };
            current = current with { updated_at = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc) };
            gateway.UpsertChartInfos([current]);
            var committedRows = new List<BeMusicSeeker.Models.ChartDetails>();
            var committedValues = new List<ChartFile>();

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                [],
                storageCommitPublished: publication => { committedRows.AddRange(publication.AppliedRows); committedValues.AddRange(publication.CurrentValues); },
                existingRowsSnapshot: new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase)
                {
                    [snapshot.Sha256] = current
                });

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.IsNull(file.Sha256);
            Assert.AreEqual(snapshot.Sha256, result.DigestChanges.Single().NewSha256);
            Assert.AreEqual(17, committedValues.Single().Level);
            Assert.AreEqual(5, committedValues.Single().Difficulty);
            Assert.IsNull(file.Mode);
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

    [TestMethod]
    public void BackfillChartInfos_MissingSongRowCommitsFactsWithoutInsertingSong()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-song-row.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = (ChartTestValues.Empty() with { Path = chartPath, Level = 2, Difficulty = 1 });
            file = file with { Md5 = digest.Md5 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
            }
            var warnings = new List<string>();
            var committedRows = new List<BeMusicSeeker.Models.ChartDetails>();
            var committedValues = new List<ChartFile>();

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                [],
                logInstallPerformanceWarn: warnings.Add,
                storageCommitPublished: publication => { committedRows.AddRange(publication.AppliedRows); committedValues.AddRange(publication.CurrentValues); });

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.SongProjectionRequestedCount);
            Assert.AreEqual(0, result.SongProjectionMatchedCount);
            Assert.AreEqual(0, result.SongProjectionChangedCount);
            Assert.AreEqual(1, result.SongProjectionMissingCount);
            CollectionAssert.Contains(result.SongProjectionMissingPaths, chartPath);
            Assert.IsTrue(warnings.Any(message => message.StartsWith("chart_info_backfill song_projection_missing", StringComparison.Ordinal)));
            Assert.AreEqual(1, committedRows.Count);
            Assert.AreEqual(2, file.Level);
            Assert.AreEqual(1, file.Difficulty);
            Assert.IsNull(file.Sha256);
            Assert.AreEqual(digest.Sha256, result.DigestChanges.Single().NewSha256);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", digest.Sha256));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", digest.Md5, digest.Sha256));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReparsesCurrentVersionRowWithMismatchedMd5()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "identity-mismatch.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(
                    file.Sha256,
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
                file.Md5,
                verify.ExecuteScalar<string>("SELECT md5 FROM chart_info WHERE sha256 = ?;", file.Sha256));
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

            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, encoding = "ks_c_5601-1987?" }));

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
            BeMusicSeeker.Models.ChartDetails row = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", result.DigestChanges.Single().NewSha256).Single();
            Assert.AreEqual(1, row.level);
        });
    }

    /// <summary>同じMD5の二つの実ファイルを一回だけ読み、両方のSHAと一つの情報行へ反映する。</summary>
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
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartAPath));
            ChartFile fileA = (ChartTestValues.Empty() with { Path = chartAPath });
            ChartFile fileB = (ChartTestValues.Empty() with { Path = chartBPath });
            fileA = fileA with { Md5 = digest.Md5 };
            fileB = fileB with { Md5 = digest.Md5 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([fileA, fileB]);
            int readCount = 0;
            List<string> logs = [];
            object logsSync = new();
            var service = new ChartInfoBuildService(delegate (string path)
            {
                readCount++;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [fileA, fileB],
                [],
                null,
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add(message);
                    }
                });

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(2, result.DigestTargetCount);
            Assert.AreEqual(2, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, readCount);
            Assert.AreEqual(1, result.FileReadCount);
            Assert.AreEqual(new FileInfo(chartAPath).Length, result.FileReadBytes);
            Assert.AreEqual(digest.Sha256, result.DigestChanges.Last().NewSha256);
            Assert.AreEqual(1L, CountChartInfoRows(songDbPath, digest.Sha256));
            using var verify = new LR2SongDBExtended(songDbPath);
            BeMusicSeeker.Models.ChartDetails chartInfo = verify.Query<BeMusicSeeker.Models.ChartDetails>(
                "SELECT * FROM chart_info WHERE sha256 = ?;",
                digest.Sha256).Single();
            List<LR2SongDB.song> songs = verify.Query<LR2SongDB.song>(
                "SELECT * FROM song WHERE hash = ? ORDER BY path;",
                fileA.Md5);
            Assert.AreEqual(2, songs.Count);
            Assert.IsTrue(songs.All(song => song.level == chartInfo.level));
            Assert.IsNull(fileA.Level);
            Assert.IsNull(fileB.Level);
            Assert.IsTrue(songs.All(song => song.level == 11));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", fileA.Md5, digest.Sha256));
            CollectionAssert.AreEquivalent(new[] { chartAPath, chartBPath }, songs.Select(song => song.path).ToArray());
            Assert.IsTrue(logs.Any(message => message.StartsWith("chart_info_backfill total=", StringComparison.Ordinal)
                && message.Contains("fileReadCount=1")
                && message.Contains("fileReadBytes=" + new FileInfo(chartAPath).Length)));
        });
    }

    [TestMethod]
    public void ChartInfoBuildTargetMapper_SeparatesBmsDigestAndBmsonIdentityRules()
    {
        string bmsMd5 = new string('a', 32);
        string bmsSha256 = new string('b', 64);
        string bmsonMd5 = new string('c', 32);
        string bmsonSha256 = new string('d', 64);
        ChartFile bmsFile = (ChartTestValues.Empty() with { Path = @"C:\Charts\a.bms" });
        bmsFile = bmsFile with { Md5 = bmsMd5 };
        bmsFile = bmsFile with { Sha256 = bmsSha256 };
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Charts\b.bmson",
            Md5 = bmsonMd5,
            Sha256 = bmsonSha256
        };

        ChartFile bmsChart = (bmsFile);
        ChartFile bmsonChart = (bmsonSong);

        Assert.AreEqual("md5:" + bmsMd5, ChartInfoBuildTargetMapper.BuildKey(bmsChart));
        Assert.AreEqual("sha256:" + bmsonSha256, ChartInfoBuildTargetMapper.BuildKey(bmsonChart));
        Assert.IsFalse(ChartInfoBuildTargetMapper.ShouldSkipBackfillTarget(bmsChart));
        Assert.IsFalse(ChartInfoBuildTargetMapper.IsDigestBackfillTarget(bmsChart));

        ChartFile missingDigestBmsFile = (ChartTestValues.Empty() with { Path = @"C:\Charts\missing-digest.bms" });
        missingDigestBmsFile = missingDigestBmsFile with { Md5 = new string('e', 32) };
        ChartFile missingDigestBmsChart = (missingDigestBmsFile);
        Assert.IsTrue(ChartInfoBuildTargetMapper.IsDigestBackfillTarget(missingDigestBmsChart));

        ChartFile noHashBmsFile = (ChartTestValues.Empty() with { Path = @"C:\Charts\no-hash.bms" });
        ChartFile noHashBmsChart = (noHashBmsFile);
        Assert.IsTrue(ChartInfoBuildTargetMapper.ShouldSkipBackfillTarget(noHashBmsChart));
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
            ChartFile song = ChartTestValues.ReadBmson(chartPath);
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
            BeMusicSeeker.Models.ChartDetails row = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", song.Sha256).Single();
            Assert.AreEqual(song.Md5, row.md5);
            Assert.AreEqual(song.Sha256, row.sha256);
            Assert.AreEqual(6, row.level);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'song';"));
        });
    }

    /// <summary>本番のCatalogMutationOwnerでDB確定に失敗した場合、譜面・song・索引を公開しない。</summary>
    [TestMethod]
    public void BackfillChartInfos_TransactionFailureDoesNotPublishCanonicalDigestSongOrIndex()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "rollback.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = (ChartTestValues.Empty() with { Path = chartPath, Level = 2 });
            file = file with { Md5 = digest.Md5 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            bool indexPublished = false;
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);
            gateway.EnsureChartInfoBackfillSchema();
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.Execute("CREATE TRIGGER fail_chart_info_storage BEFORE INSERT ON chart_info BEGIN SELECT RAISE(ABORT, 'injected transaction failure'); END;");
            }
            var mutationOwner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), gateway);

            AggregateException exception = Assert.ThrowsException<AggregateException>(() =>
                service.BackfillChartInfos(
                    gateway,
                    CreateChartSnapshot([file], []),
                    storageCommitPublished: _ => indexPublished = true,
                    chartInfoChunkWriter: mutationOwner.ApplyChartInfoStorageWrite));

            Assert.IsTrue(exception.Flatten().InnerExceptions.Any(inner => inner.Message.Contains("injected transaction failure", StringComparison.Ordinal)));
            Assert.IsFalse(indexPublished);
            Assert.IsTrue(string.IsNullOrWhiteSpace(file.Sha256));
            Assert.AreEqual(2, file.Level);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(2, song.level);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    /// <summary>分割補完の後半だけがDB失敗した場合、先行確定分を保持し失敗分を公開しない。</summary>
    [TestMethod]
    public void BackfillChartInfos_LaterChunkFailureKeepsEarlierPublicationAndDoesNotPublishFailedChunk()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "first-chunk.bms");
            string secondPath = Path.Combine(tempRootPath, "second-chunk.bms");
            File.WriteAllText(firstPath, "#PLAYER 1\r\n#PLAYLEVEL 8\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(secondPath, "#PLAYER 1\r\n#PLAYLEVEL 10\r\n#BPM 140\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile firstDigest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstPath));
            ChartFile secondDigest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondPath));
            ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstPath, Level = 2 });
            ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondPath, Level = 3 });
            firstFile = firstFile with { Md5 = firstDigest.Md5 };
            secondFile = secondFile with { Md5 = secondDigest.Md5 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([firstFile, secondFile]);
            var service = new ChartInfoBuildService(
                File.ReadAllBytes,
                workerCountOverride: 1,
                commitChunkSizeOverride: 1);
            int writerCalls = 0;
            var publications = new List<ChartInfoStorageCommitPublication>();
            gateway.EnsureChartInfoBackfillSchema();
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.Execute("CREATE TRIGGER fail_later_chart_info_chunk BEFORE INSERT ON chart_info WHEN (SELECT COUNT(1) FROM chart_info) >= 1 BEGIN SELECT RAISE(ABORT, 'injected later chunk failure'); END;");
            }
            var mutationOwner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), gateway);

            Assert.ThrowsException<AggregateException>(() =>
                service.BackfillChartInfos(
                    gateway,
                    CreateChartSnapshot([firstFile, secondFile], []),
                    storageCommitPublished: publications.Add,
                    chartInfoChunkWriter: request =>
                    {
                        writerCalls++;
                        return mutationOwner.ApplyChartInfoStorageWrite(request);
                    }));

            Assert.AreEqual(2, writerCalls);
            Assert.AreEqual(1, publications.Count);
            string committedPath = publications[0].DigestChanges.Single().Path;
            ChartFile committedFile = string.Equals(committedPath, firstPath, StringComparison.OrdinalIgnoreCase)
                ? firstFile
                : secondFile;
            ChartFile failedFile = ReferenceEquals(committedFile, firstFile) ? secondFile : firstFile;
            string committedSha256 = ReferenceEquals(committedFile, firstFile) ? firstDigest.Sha256 : secondDigest.Sha256;
            int committedLevel = ReferenceEquals(committedFile, firstFile) ? 8 : 10;
            int failedLevel = ReferenceEquals(failedFile, firstFile) ? 2 : 3;
            Assert.IsNull(committedFile.Sha256);
            Assert.AreEqual(committedSha256, publications[0].DigestChanges.Single().NewSha256);
            Assert.AreEqual(committedLevel, publications[0].CurrentValues.Single().Level);
            Assert.IsTrue(string.IsNullOrWhiteSpace(failedFile.Sha256));
            Assert.AreEqual(failedLevel, failedFile.Level);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(committedLevel, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", committedFile.Path));
            Assert.AreEqual(failedLevel, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", failedFile.Path));
        });
    }

}
