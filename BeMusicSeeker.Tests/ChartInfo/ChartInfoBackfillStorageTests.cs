using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面情報の補完、保存投影、確定後公開の実DB境界を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoBackfillStorageTests
{
    /// <summary>未登録・現在版・旧版の実DB行を順に補完し、現在の成功行を失敗履歴より優先して再読込みを省き、旧版の再解析漏れを検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, verify.ExecuteScalar<int>("SELECT parser_version FROM chart_info WHERE sha256 = '" + file.Sha256 + "';"));
        });
    }

    /// <summary>実補完から詳細由来のsong列を更新し、基本情報・利用者列・mode・judgeを保持して保存投影の過剰な上書きを検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
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

    /// <summary>SHA未捕捉候補の現在詳細を再利用し、更新日時を変えずdigest対応とsong投影を確定して再解析や保存・確定通知の欠落を検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            Assert.AreEqual(snapshot.Sha256, result.DigestChanges.Single().NewSha256);
            Assert.AreEqual(17, committedValues.Single().Level);
            Assert.AreEqual(5, committedValues.Single().Difficulty);
            Assert.AreEqual(1, committedRows.Count);
            Assert.AreSame(current, committedRows[0]);
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(17, song.level);
            Assert.AreEqual(5, song.difficulty);
            Assert.AreEqual(5, song.mode);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", snapshot.Sha256));
            Assert.AreEqual(current.updated_at, verify.ExecuteScalar<DateTime>("SELECT updated_at FROM chart_info WHERE sha256 = ?;", snapshot.Sha256));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", snapshot.Md5, snapshot.Sha256));
        });
    }

    /// <summary>song行が消えた候補でも詳細・digest対応と必須公開を確定し、診断先が失敗してもsongを再挿入せず完了します。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BackfillChartInfos_MissingSongRowCommitsFactsWithoutInsertingSong(bool failDiagnostics)
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
                logInstallPerformance: _ =>
                {
                    if (failDiagnostics) { throw new IOException("performance log failed"); }
                },
                logInstallPerformanceWarn: message =>
                {
                    warnings.Add(message);
                    if (failDiagnostics) { throw new IOException("warning log failed"); }
                },
                storageCommitPublished: publication => { committedRows.AddRange(publication.AppliedRows); committedValues.AddRange(publication.CurrentValues); });

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.SongProjectionRequestedCount);
            Assert.AreEqual(0, result.SongProjectionMatchedCount);
            Assert.AreEqual(0, result.SongProjectionChangedCount);
            Assert.AreEqual(1, result.SongProjectionMissingCount);
            CollectionAssert.Contains(result.SongProjectionMissingPaths, chartPath);
            Assert.IsTrue(warnings.Any(message => message.StartsWith("chart_info_backfill song_projection_missing", StringComparison.Ordinal)));
            Assert.AreEqual(1, committedRows.Count);
            Assert.AreEqual(digest.Sha256, result.DigestChanges.Single().NewSha256);
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", digest.Sha256));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", digest.Md5, digest.Sha256));
        });
    }

    /// <summary>解析版が現在でもMD5の異なる行を再解析し、SHA一致だけで別内容の保存詳細を再利用する誤りを検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(
                file.Md5,
                verify.ExecuteScalar<string>("SELECT md5 FROM chart_info WHERE sha256 = ?;", file.Sha256));
        });
    }

    /// <summary>保守用encoding指定と異なるCP932入力を参照互換のdecodeで詳細化し、一覧保守設定によって保存levelが変わる誤りを検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            BeMusicSeeker.Models.ChartDetails row = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", result.DigestChanges.Single().NewSha256).Single();
            Assert.AreEqual(1, row.level);
        });
    }

    /// <summary>同じMD5の二つの実譜面を一回の読込み・解析へまとめ、双方のsong列と一つの詳細・digest対応を確定して重複処理やfanout漏れを検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            BeMusicSeeker.Models.ChartDetails chartInfo = verify.Query<BeMusicSeeker.Models.ChartDetails>(
                "SELECT * FROM chart_info WHERE sha256 = ?;",
                digest.Sha256).Single();
            List<LR2SongDB.song> songs = verify.Query<LR2SongDB.song>(
                "SELECT * FROM song WHERE hash = ? ORDER BY path;",
                fileA.Md5);
            Assert.AreEqual(2, songs.Count);
            Assert.IsTrue(songs.All(song => song.level == chartInfo.level));
            Assert.IsTrue(songs.All(song => song.level == 11));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", fileA.Md5, digest.Sha256));
            CollectionAssert.AreEquivalent(new[] { chartAPath, chartBPath }, songs.Select(song => song.path).ToArray());
            Assert.IsTrue(logs.Any(message => message.StartsWith("chart_info_backfill total=", StringComparison.Ordinal)
                && message.Contains("fileReadCount=1")
                && message.Contains("fileReadBytes=" + new FileInfo(chartAPath).Length)));
        });
    }

    /// <summary>BMSはMD5、bmsonはSHAでまとめ、SHA未捕捉の補完対象とhashなしの除外を区別して形式間の同一性混同を検出する。</summary>
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

    /// <summary>bmsonの詳細を実DBへ保存し、BMS専用digest対応やsong表を作らないことで形式固有の保存境界への漏出を検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            BeMusicSeeker.Models.ChartDetails row = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", song.Sha256).Single();
            Assert.AreEqual(song.Md5, row.md5);
            Assert.AreEqual(song.Sha256, row.sha256);
            Assert.AreEqual(6, row.level);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'song';"));
        });
    }

    /// <summary>二workerの内部/資源故障でbounded queueを排出し、全Task終端まで操作失敗を待ち、先行成功の確定だけを保持する。</summary>
    /// <remarks>実BMS・SQLite・CatalogMutationOwnerを使い、解析境界だけで故障を注入する。reader背圧とcollector/writerをgateで観測し、解放順の両行で各段階の寿命を識別する。finallyで全gateを解放して操作を待つ。</remarks>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task BackfillChartInfos_WorkerFaultDrainsInputsAndWaitsForAllStages(bool resourceFailure, bool releaseWriterFirst)
    {
        await WithTemporarySongDb(async (tempRootPath, songDbPath) =>
        {
            const int workerCount = 2;
            int readerCount = ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, 20);
            int capacity = ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(workerCount, readerCount);
            int backpressureReadCount = 1 + workerCount + capacity + readerCount;
            int targetCount = backpressureReadCount + 4;
            List<ChartFile> charts = [];
            for (int index = 0; index < targetCount; index++)
            {
                string path = Path.Combine(tempRootPath, index + " parse_failed target.bms");
                File.WriteAllText(path, "#TITLE chart" + index + "\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
                ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
                charts.Add(chart with { Sha256 = string.Empty, Level = 2 });
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(charts);
            gateway.EnsureChartInfoBackfillSchema();
            var mutationOwner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), gateway);
            using var successPublished = new ManualResetEventSlim();
            using var releaseFaults = new ManualResetEventSlim();
            using var releaseCollector = new ManualResetEventSlim();
            using var releaseWriter = new ManualResetEventSlim();
            var faultsEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var backpressureReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var collectorEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var collectorExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var writerExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            List<ChartInfoStorageCommitPublication> publications = [];
            List<(int Total, int Processed)> progress = [];
            ConcurrentBag<string> warnings = [];
            string? successPath = null;
            int reads = 0;
            int parseCalls = 0;
            int faultCalls = 0;
            int activeCallbacks = 0;
            var service = new ChartInfoBuildService(
                path =>
                {
                    Interlocked.Increment(ref activeCallbacks);
                    try
                    {
                        int readNumber = Interlocked.Increment(ref reads);
                        if (readNumber == 1) { Volatile.Write(ref successPath, path); }
                        else { successPublished.Wait(); }
                        if (readNumber == backpressureReadCount) { backpressureReached.TrySetResult(true); }
                        return File.ReadAllBytes(path);
                    }
                    finally { Interlocked.Decrement(ref activeCallbacks); }
                }, workerCountOverride: workerCount, commitChunkSizeOverride: 1,
                parseBytes: (bytes, path, md5, sha256, timeout) =>
                {
                    Interlocked.Increment(ref activeCallbacks);
                    try
                    {
                        Interlocked.Increment(ref parseCalls);
                        if (string.Equals(path, Volatile.Read(ref successPath), StringComparison.Ordinal))
                        {
                            return ChartInfoParser.ParseBytesDetailed(bytes, path, md5, sha256,
                                timeout: timeout, retainChartString: false);
                        }
                        if (Interlocked.Increment(ref faultCalls) == workerCount) { faultsEntered.TrySetResult(true); }
                        releaseFaults.Wait();
                        return ThrowBackfillParserFault(resourceFailure);
                    }
                    finally { Interlocked.Decrement(ref activeCallbacks); }
                });
            Task<ChartInfoBackfillResult> operation = Task.Run(() => service.BackfillChartInfos(
                gateway, charts,
                reportProgress: (total, processed, _) => progress.Add((total, processed)),
                logInstallPerformance: message =>
                {
                    if (!message.StartsWith("chart_info_backfill parse_done", StringComparison.Ordinal)) { return; }
                    Interlocked.Increment(ref activeCallbacks);
                    try
                    {
                        collectorEntered.TrySetResult(true);
                        releaseCollector.Wait();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeCallbacks);
                        collectorExited.TrySetResult(true);
                    }
                },
                logInstallPerformanceWarn: warnings.Add,
                storageCommitPublished: publication =>
                {
                    Interlocked.Increment(ref activeCallbacks);
                    try
                    {
                        publications.Add(publication);
                        successPublished.Set();
                        releaseWriter.Wait();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeCallbacks);
                        writerExited.TrySetResult(true);
                    }
                },
                chartInfoChunkWriter: mutationOwner.ApplyChartInfoStorageWrite));
            try
            {
                await AwaitPipelineStage(faultsEntered.Task, operation);
                await AwaitPipelineStage(backpressureReached.Task, operation);
                Assert.IsFalse(operation.IsCompleted);
                Assert.AreEqual(1 + workerCount, Volatile.Read(ref parseCalls));
                releaseFaults.Set();
                await AwaitPipelineStage(collectorEntered.Task, operation);
                Assert.IsFalse(operation.IsCompleted, "collectorの開始済み処理を待つ必要があります。");
                if (releaseWriterFirst)
                {
                    releaseWriter.Set();
                    await AwaitPipelineStage(writerExited.Task, operation);
                    Assert.IsFalse(operation.IsCompleted, "writer公開後もcollectorの開始済み処理を待つ必要があります。");
                    releaseCollector.Set();
                }
                else
                {
                    releaseCollector.Set();
                    await AwaitPipelineStage(collectorExited.Task, operation);
                    Assert.IsFalse(operation.IsCompleted, "collector終端後もwriterの開始済み公開を待つ必要があります。");
                    releaseWriter.Set();
                }
                AggregateException failure = await Assert.ThrowsExceptionAsync<AggregateException>(() => operation);
                Exception[] causes = failure.Flatten().InnerExceptions.ToArray();
                Assert.AreEqual(workerCount, causes.Length);
                foreach (Exception cause in causes)
                {
                    Assert.AreEqual(resourceFailure ? typeof(OutOfMemoryException) : typeof(InvalidOperationException), cause.GetType());
                    Assert.AreEqual("injected parse_failed parser fault", cause.Message);
                    Assert.IsNotNull(cause.StackTrace);
                    StringAssert.Contains(cause.StackTrace, nameof(ThrowBackfillParserFault));
                }
                Assert.AreEqual(1 + workerCount, parseCalls);
                Assert.AreEqual(targetCount, reads);
                Assert.AreEqual(0, activeCallbacks);
                Assert.AreEqual(1, publications.Count);
                Assert.AreEqual(successPath, publications.Single().CurrentValues.Single().Path);
                Assert.AreEqual(2, warnings.Count(message => message.StartsWith("chart_info_backfill internal_failed", StringComparison.Ordinal)
                    && message.Contains("injected parse_failed parser fault", StringComparison.Ordinal)
                    && message.Contains(" parse_failed target.bms", StringComparison.Ordinal)));
                Assert.IsFalse(progress.Any(item => item.Processed == item.Total));
                Assert.AreEqual(1, progress.Max(item => item.Processed));
                using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
                foreach (ChartFile chart in charts)
                {
                    Assert.AreEqual(chart.Path == successPath ? 9 : 2,
                        verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chart.Path));
                }
            }
            finally
            {
                successPublished.Set();
                releaseFaults.Set();
                releaseCollector.Set();
                releaseWriter.Set();
                try { await operation; }
                catch (Exception) { /* 操作失敗は本体で検査し、全開始Taskの終端後だけfixtureを解放します。 */ }
            }
        });
    }

    private static async Task AwaitPipelineStage(Task stage, Task operation)
    {
        // 期限は故障検出とfinallyの全gate解放を開始するためだけに使います。
        // 実operationの終端待機は、その解放後もfinallyが所有します。
        Task completed = await Task.WhenAny(stage, operation).WaitAsync(TimeSpan.FromSeconds(60));
        if (completed == operation)
        {
            await operation;
            Assert.Fail("必要なpipeline段階の観測前に操作が完了しました。");
        }
        await stage;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ChartInfoParser.ChartInfoParseResult ThrowBackfillParserFault(bool resourceFailure)
    {
        if (resourceFailure) { throw new OutOfMemoryException("injected parse_failed parser fault"); }
        throw new InvalidOperationException("injected parse_failed parser fault");
    }

    /// <summary>本番保存ownerのtransactionが失敗したとき詳細・digest対応・song更新をrollbackし、未確定の索引通知を公開する誤りを検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(2, song.level);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    /// <summary>後続chunkの実DB保存だけを失敗させ、先行確定・通知を保持して失敗分の公開や全体rollbackを検出する。</summary>
    /// <remarks>実SQLiteの確定・rollbackと本番補完経路を観測し、保存前の計算結果だけで成功としない。</remarks>
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
            Assert.AreEqual(committedSha256, publications[0].DigestChanges.Single().NewSha256);
            Assert.AreEqual(committedLevel, publications[0].CurrentValues.Single().Level);
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(committedLevel, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", committedFile.Path));
            Assert.AreEqual(failedLevel, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", failedFile.Path));
        });
    }

}
