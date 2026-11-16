using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面情報の導入、解析失敗、警告、再評価、DB競合の境界を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoInstallFailureRetryTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>削除要求の空値を除きMD5の空白・大小文字を正規化して重複をまとめ、同じ対象の二重削除や有効hashの脱落を検出する。</summary>
    [TestMethod]
    public void NormalizeChartInfoParseFailureMd5s_RemovesBlankDuplicatesAndNormalizesCase()
    {
        CollectionAssert.AreEqual(
            new[] { new string('a', 32), new string('b', 32) },
            new ChartInfoParseFailureRemovalRequest([null, " ", new string('A', 32), new string('a', 32), " " + new string('B', 32) + " "]).Md5s.ToArray());
    }

    /// <summary>詳細解析が失敗しても捕捉SHAのdigest対応を確定し、失敗件数と警告を最終集計前に残して次回照合の手掛かりを失う誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_ParseFailureStillPersistsDigest()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = new string('a', 32) };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 2);
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
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(0, result.DigestFailedCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.BackfilledCount);
            string committedSha256 = result.DigestChanges.Single().NewSha256;
            Assert.IsFalse(string.IsNullOrWhiteSpace(committedSha256));
            Assert.IsTrue(logs.Count >= 2);
            Assert.IsTrue(logs.Any(message => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.Md5 + "' AND sha256 = '" + committedSha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    /// <summary>現在版の保存失敗を同じMD5の補完で再利用して再読込みを省き、失敗を新規発生扱いする誤りや不要な再解析を検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_SkipsCurrentPersistedParseFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-skip.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile firstFile = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            firstFile = firstFile with { Md5 = new string('a', 32) };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            var firstService = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult first = BackfillChartInfos(firstService,
                gateway,
                [firstFile],
                [],
                null,
                null);

            Assert.AreEqual(1, first.ParseFailedCount);
            Assert.AreEqual(1, first.FailurePersistedCount);

            int readCount = 0;
            ChartFile secondFile = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            secondFile = secondFile with { Md5 = firstFile.Md5 };
            var secondService = new ChartInfoBuildService(delegate (string path)
            {
                readCount++;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 1);
            List<string> logs = [];
            object logsSync = new();

            ChartInfoBackfillResult second = BackfillChartInfos(secondService,
                gateway,
                [secondFile],
                [],
                (current, total, target) => { },
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

            Assert.AreEqual(0, second.TargetCount);
            Assert.AreEqual(1, second.FailureSkippedCount);
            Assert.AreEqual(0, second.ParseFailedCount);
            Assert.AreEqual(0, second.FailedCount);
            Assert.AreEqual(0, readCount);
            Assert.AreEqual(0, second.FileReadCount);
            Assert.AreEqual(0L, second.FileReadBytes);
            Assert.IsFalse(logs.Any(message => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any(message => message.Contains("parseFailureSkipped=1")));
        });
    }

    /// <summary>旧解析版の保存失敗を再解析して現在の失敗へ更新し、古い履歴を永続的な省略理由として扱う誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_ReparsesStalePersistedParseFailureAndUpdatesRecord()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-stale.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = new string('a', 32) };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.Md5, string.Empty, chartPath, 0, "parse_failed", "JsonReaderException", "old failure", null)
            ]);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                null);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(0, result.FailureSkippedCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5).Single();
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, failure.parser_version);
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreNotEqual("old failure", failure.message);
        });
    }

    /// <summary>現在より短い期限のtimeout履歴は再解析し、今回の非timeout失敗へ更新して古い時間上限で処理を省く誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_ReparsesShorterTimeoutFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-timeout-stale.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = new string('a', 32) };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.Md5, string.Empty, chartPath, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "timeout", "ChartInfoParseTimeoutException", "old timeout", 0)
            ]);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1, parseTimeoutOverride: TimeSpan.FromSeconds(1));

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                null);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(0, result.FailureSkippedCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5).Single();
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.IsFalse(failure.parse_timeout_ms.HasValue);
        });
    }

    /// <summary>旧失敗のある有効BMSを補完して詳細を確定し同MD5の失敗を消し、成功後も失敗警告の原因を残す誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_SuccessClearsStaleParseFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "good-clear.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.Md5, string.Empty, chartPath, 0, "parse_failed", "InvalidDataException", "old failure", null)
            ]);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                null);

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.FailureClearedCount);
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE md5 = ?;", file.Md5));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5));
        });
    }

    /// <summary>読取りportのIOExceptionを読取り失敗として集計・最終summary前に警告し、解析失敗履歴へ誤保存する誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_ReadFailureLogsWarnBeforeFinalSummary()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = Path.Combine(tempRootPath, "missing-file.bms")
            };
            file = file with { Md5 = new string('a', 32) };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService(delegate
            {
                throw new IOException("read boom");
            }, workerCountOverride: 1);
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
            Assert.AreEqual(1, result.ReadFailedCount);
            Assert.AreEqual(0, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.FileReadCount);
            Assert.AreEqual(0L, result.FileReadBytes);
            Assert.IsTrue(logs.Count >= 2);
            Assert.IsTrue(logs.Any(message => message.StartsWith("WARN chart_info_backfill read_failed", StringComparison.Ordinal)));
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        });
    }

    /// <summary>解析期限0を専用timeout例外で伝え、通常の解析成功や一般失敗へ分類を置き換える誤りを検出する。</summary>
    [TestMethod]
    public void ParseBytesDetailed_ZeroTimeoutThrowsDedicatedTimeout()
    {
        string text = "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        ChartInfoParser.ChartInfoParseTimeoutException ex = Assert.ThrowsExactly<ChartInfoParser.ChartInfoParseTimeoutException>(delegate
        {
            ChartInfoParser.ParseBytesDetailed(bytes, ".bms", new string('a', 32), new string('b', 64), null, TimeSpan.Zero);
        });

        StringAssert.Contains(ex.Message, "timed out");
    }

    /// <summary>期限0で詳細がtimeoutしてもdigest対応と期限付き失敗履歴を実保存し、捕捉hashや再評価に必要な失敗分類を失う誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_TimeoutStillPersistsDigest()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "timeout.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1, commitChunkSizeOverride: 2, parseTimeoutOverride: TimeSpan.Zero);
            var logs = new ConcurrentQueue<string>();

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                message => logs.Enqueue("INFO " + message),
                message => logs.Enqueue("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(1, result.TimeoutFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(0, result.BackfilledCount);
            string committedSha256 = result.DigestChanges.Single().NewSha256;
            Assert.IsFalse(string.IsNullOrWhiteSpace(committedSha256));
            Assert.IsTrue(logs.Any(message => message.Contains("exception=\"ChartInfoParseTimeoutException\"")));
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.Md5 + "' AND sha256 = '" + committedSha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5).Single();
            Assert.AreEqual(committedSha256, failure.sha256);
            Assert.AreEqual("timeout", failure.failure_kind);
            Assert.AreEqual("ChartInfoParseTimeoutException", failure.exception_type);
            Assert.AreEqual(0, failure.parse_timeout_ms);
        });
    }

    /// <summary>五つの異なる譜面を指定chunkサイズ2で実保存し、三回の確定ログと解析・最終集計の順序を保って対象の欠落や二重保存を検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_CommitsInChunksAndLogsPhaseBoundaries()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            List<ChartFile> files = [];
            for (int index = 0; index < 5; index++)
            {
                string chartPath = Path.Combine(tempRootPath, "chunk-" + index.ToString(CultureInfo.InvariantCulture) + ".bms");
                File.WriteAllText(
                    chartPath,
                    "#PLAYER 1\r\n#TITLE chunk " + index.ToString(CultureInfo.InvariantCulture) + "\r\n#BPM " + (120 + index).ToString(CultureInfo.InvariantCulture) + "\r\n#00111:01\r\n",
                    Encoding.ASCII);
                ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Md5 = digest.Md5 };
                files.Add(file);
            }
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 2, commitChunkSizeOverride: 2);
            List<string> logs = [];
            object logsSync = new();

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                files,
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

            Assert.AreEqual(5, result.TargetCount);
            Assert.AreEqual(5, result.ProcessedCount);
            Assert.AreEqual(5, result.DigestBackfilledCount);
            Assert.AreEqual(5, result.BackfilledCount);
            Assert.AreEqual(3, result.CommitChunks);
            Assert.AreEqual(3, logs.Count(message => message.StartsWith("INFO chart_info_backfill db_commit_chunk_done", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill start", StringComparison.Ordinal)
                && message.Contains("readerCount=" + result.ReaderCount)
                && message.Contains("queueCapacity=" + result.QueueCapacity)));
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill parse_done", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill slow_parse_top", StringComparison.Ordinal)));
            int parseDoneIndex = logs.FindIndex(message => message.StartsWith("INFO chart_info_backfill parse_done", StringComparison.Ordinal));
            int finalSummaryIndex = logs.FindIndex(message => message.StartsWith("INFO chart_info_backfill total=", StringComparison.Ordinal));
            int lastCommitDoneIndex = logs.FindLastIndex(message => message.StartsWith("INFO chart_info_backfill db_commit_chunk_done", StringComparison.Ordinal));
            Assert.IsTrue(parseDoneIndex >= 0);
            Assert.IsTrue(finalSummaryIndex > parseDoneIndex);
            Assert.IsTrue(finalSummaryIndex > lastCommitDoneIndex);
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            Assert.AreEqual(5L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(5L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    /// <summary>空・片側・両側の保存要求とnullable入力を受理して同hashの行を置換し、無関係の行や更新日時・明示状態を取り違える誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void UpsertChartInfoBackfillChunk_AcceptsNullableInputsAndReplacesRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoBackfillChunk(null, null);
            gateway.UpsertChartInfoBackfillChunk([], []);

            string md5A = new('a', 32);
            string md5B = new('b', 32);
            string shaA = new('1', 64);
            string shaB = new('2', 64);
            string shaC = new('3', 64);
            var updatedAt = new DateTime(2026, 4, 27, 1, 2, 3, DateTimeKind.Utc);
            BeMusicSeeker.Models.ChartDetails rowA = CreateChartInfoRow(shaA, md5A, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            rowA = rowA with { level = null };
            rowA = rowA with { difficulty = null };
            rowA = rowA with { difficulty_defined = false };
            rowA = rowA with { mainbpm = null };
            rowA = rowA with { total = null };
            rowA = rowA with { total_defined = false };
            rowA = rowA with { updated_at = updatedAt };
            BeMusicSeeker.Models.ChartDetails rowB = CreateChartInfoRow(shaB, md5B, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            rowB = rowB with { level = 7 };

            gateway.UpsertChartInfoBackfillChunk([new ChartDigestBackfillEntry(md5A, shaA)], null);
            gateway.UpsertChartInfoBackfillChunk(null, [rowA]);
            gateway.UpsertChartInfoBackfillChunk([new ChartDigestBackfillEntry(md5B, shaB)], [rowB]);

            BeMusicSeeker.Models.ChartDetails replacement = CreateChartInfoRow(shaA, md5A, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            replacement = replacement with { level = 12 };
            replacement = replacement with { difficulty_defined = true };
            replacement = replacement with { total_defined = true };
            replacement = replacement with { updated_at = updatedAt.AddMinutes(1) };
            gateway.UpsertChartInfoBackfillChunk(
                [new ChartDigestBackfillEntry(md5A, shaC)],
                [replacement]);

            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", md5A, shaC));
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            BeMusicSeeker.Models.ChartDetails storedA = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", shaA).Single();
            BeMusicSeeker.Models.ChartDetails storedB = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", shaB).Single();
            Assert.AreEqual(12, storedA.level);
            Assert.IsTrue(storedA.difficulty_defined);
            Assert.IsTrue(storedA.total_defined);
            Assert.AreEqual(replacement.updated_at, storedA.updated_at);
            Assert.AreEqual(7, storedB.level);
        });
    }

    /// <summary>参照互換の時間wrapを含むBMSを詳細補完の成功件数へ集計し、情報診断を失敗警告や失敗件数へ誤分類する誤りを検出する。</summary>
    /// <remarks>補完入口を実DB窓口へ接続して成功分類と診断を観測する。保存と失敗履歴の読戻しはBackfillChartInfos_SuccessClearsStaleParseFailureが担当する。</remarks>
    [TestMethod]
    public void BackfillChartInfos_JavaIntWrappedTimelineLogsParseDiagnosticAsSuccess()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-int-wrap-backfill.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 1\r\n"
                    + "#STOP01 90000\r\n"
                    + "#00009:" + string.Concat(Enumerable.Repeat("01", 40)) + "\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1, commitChunkSizeOverride: 1);
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

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.ParseFailedCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill parse_diagnostic", StringComparison.Ordinal)
                && message.Contains("code=\"BMS_JAVA_INT_TIME_WRAP\"")
                && message.Contains("parseFailed=false")));
            Assert.IsFalse(logs.Any(message => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
        });
    }

}
