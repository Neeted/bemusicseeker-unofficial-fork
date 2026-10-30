using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Tests.Helpers;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryIrServiceTests
{
    /// <summary>順位cacheの高得点/新規は入力notesとAAA・90%を採り、低得点/同点は旧成績を保ちながらclear・順位等の別更新を続けます。</summary>
    [DataTestMethod]
    [DataRow("new")]
    [DataRow("higher-zero-notes")]
    [DataRow("higher-other-notes")]
    [DataRow("same")]
    [DataRow("lower")]
    public void ApplyIrDataToScores_AdoptsDerivedValuesOnlyWithHigherScore(string scenario)
    {
        const string hash = "91919191919191919191919191919191";
        bool isNew = scenario == "new";
        bool same = scenario == "same";
        bool lower = scenario == "lower";
        BMSScore previous = CreateScore(hash, ClearType.EASY,
            pg: same ? 800 : lower ? 900 : 500, gr: same ? 200 : lower ? 100 : 0, minbp: 7);
        previous.totalnotes = scenario == "higher-zero-notes" ? 0 : scenario == "higher-other-notes" ? 2000 : 1200;
        previous.rank = same ? RankType.A : lower ? RankType.AA : scenario == "higher-zero-notes" ? RankType.INVALID : RankType.E;
        previous.rate = same ? 75 : lower ? 79 : scenario == "higher-zero-notes" ? 0 : 25;
        previous.maxcombo = 950;
        previous.ranking = 80;
        previous.IsLr2IrScoreUnsent = true;
        List<BMSScore> scores = isNew ? [] : [previous];
        var updatedAt = new DateTime(2026, 1, 2, 3, 4, 5);
        var data = new LR2IRData(hash)
        {
            clear = ClearType.HARD,
            notes = 1000,
            pg = 800,
            gr = 200,
            combo = 900,
            minbp = 12,
            rank = 4,
            players_num = 77,
            average = 1000,
            sigma = 100,
            lastupdate = updatedAt
        };

        new BmsLibraryIrService().ApplyIrDataToScores(data, null, null, scores, estimateOfflineScoreRanking: false);

        BMSScore adopted = scores.Single();
        bool retainsScore = same || lower;
        Assert.AreEqual(lower ? 1900 : 1800, adopted.score);
        Assert.AreEqual(retainsScore ? 1200 : 1000, adopted.totalnotes);
        Assert.AreEqual(same ? RankType.A : lower ? RankType.AA : RankType.AAA, adopted.rank);
        Assert.AreEqual(same ? 75 : lower ? 79 : 90, adopted.rate);
        Assert.AreEqual(retainsScore ? 950 : 900, adopted.maxcombo);
        Assert.AreEqual(retainsScore ? 7 : 12, adopted.minbp);
        Assert.AreEqual(ClearType.HARD, adopted.clear);
        Assert.AreEqual(0, adopted.op_history);
        Assert.AreEqual(lower ? 80 : 4, adopted.ranking);
        Assert.AreEqual(77, adopted.rankingNum);
        Assert.AreEqual(updatedAt, adopted.rankingLastupdate);
        Assert.AreEqual(!isNew, adopted.IsLr2IrScoreUnsent);
    }

    /// <summary>両IR上書きはnotes0を拒否や別notes補完にせず、旧AAA/90をINVALID/整数率0へ更新します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BmsScoreOverwrite_ZeroNotesClearsStaleDerivedValues(bool rankingCache)
    {
        const string hash = "92929292929292929292929292929292";
        BMSScore score = CreateScore(hash, ClearType.HARD, pg: 800, gr: 200, minbp: 12);
        score.rank = RankType.AAA;
        score.rate = 90;
        if (rankingCache)
        {
            score.Overwrite(new LR2IRData(hash) { notes = 0, pg = 10, gr = 5, sigma = 1 });
        }
        else
        {
            score.Overwrite(new LR2IRScore(hash) { notes = 0, pg = 10, gr = 5 });
        }
        Assert.AreEqual(25, score.score);
        Assert.AreEqual(0, score.totalnotes);
        Assert.AreEqual(RankType.INVALID, score.rank);
        Assert.AreEqual(0, score.rate);
    }

    /// <summary>本人IRの新規成績も採用score/notesからAAA/90を持ち、所持対象の通常未送信判定へ同じ値を渡します。</summary>
    [TestMethod]
    public void UpdateBmsScores_NewIrScoreInitializesDerivedValues()
    {
        const string hash = "93939393939393939393939393939393";
        LR2IRScore irScore = CreateIrScore(hash, ClearType.HARD, pg: 800, gr: 200, minbp: 12);

        BMSScore adopted = new BmsLibraryIrService().UpdateBmsScores([irScore], [], _ => true).Single();

        Assert.AreEqual(1800, adopted.score);
        Assert.AreEqual(1000, adopted.totalnotes);
        Assert.AreEqual(RankType.AAA, adopted.rank);
        Assert.AreEqual(90, adopted.rate);
        Assert.AreEqual(ClearType.HARD, adopted.clear);
        Assert.AreEqual(12, adopted.minbp);
        Assert.AreEqual(900, adopted.maxcombo);
        Assert.IsFalse(adopted.IsLr2IrScoreUnsent);
    }

    /// <summary>実保存成績と取得内容が一致しlastupdateだけ異なる場合、未記録・不一致のmetadataだけを保存し、全score列と返却lastupdateを保持する。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CommitPreparedIrScoreTable_MatchingScoresUpdatesOnlyMissingOrDifferentMetadata(bool missingMetadata)
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        var service = new BmsLibraryIrService();
        const string hash = "12345678901234567890123456789012";
        IrScorePrefetchResult saved = service.PrefetchIrScoreTableWithMetrics(123,
            new FakeIrClient(BuildPlayerScoreXml(hash, 321, 45, lastUpdate: 20260504)), PlayerScoreRegex);
        gateway.ReplaceIrScoreTableAndRefreshMetadata(saved.ScoreTable, 123, "old-digest");
        if (missingMetadata)
        {
            using var db = new SQLiteConnection(env.SongDbPath);
            db.Execute("DELETE FROM ir_score_refresh_metadata WHERE lr2id = 123;");
        }
        string beforeScores = CaptureRawIrDatabase(env.SongDbPath).Split('\n')[0];
        IrScorePrefetchResult fetched = service.PrefetchIrScoreTableWithMetrics(123,
            new FakeIrClient(BuildPlayerScoreXml(hash, 321, 45, lastUpdate: 20260505)), PlayerScoreRegex);

        IrScoreTableUpdateResult result = service.CommitPreparedIrScoreTableWithMetrics(123, gateway, fetched);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(IrScoreFailure.None, result.Failure);
        Assert.IsTrue(result.MetadataUpdated);
        Assert.IsTrue(result.Skipped);
        Assert.AreEqual(20260504, result.ScoreTable.Single().lastupdate);
        Assert.AreEqual(saved.ScoreDigestSha256, BmsLibraryIrService.ComputeScoreDigest(result.ScoreTable));
        Assert.AreEqual(beforeScores, CaptureRawIrDatabase(env.SongDbPath).Split('\n')[0]);
        LR2SongDBExtended.ir_score_refresh_metadata metadata = gateway.LoadIrScoreRefreshMetadata(123);
        Assert.AreEqual(fetched.ScoreDigestSha256, metadata.score_digest_sha256);
        Assert.IsTrue(metadata.updated_at > DateTime.MinValue);
    }

    /// <summary>実score一致・metadata不一致の保存がDELETE journalのreaderでcommit Busyとなった場合、commit到達と失敗結果、全score列・全metadata・時刻の保持を確認する。</summary>
    [TestMethod]
    public void CommitPreparedIrScoreTable_MetadataOnlyCommitBusyPreservesDatabase()
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway setupGateway = env.CreateGateway();
        var service = new BmsLibraryIrService();
        IrScorePrefetchResult fetched = service.PrefetchIrScoreTableWithMetrics(123,
            new FakeIrClient(BuildPlayerScoreXml("12345678901234567890123456789012", 321, 45)), PlayerScoreRegex);
        setupGateway.ReplaceIrScoreTableAndRefreshMetadata(fetched.ScoreTable, 123, "old-digest");
        setupGateway.UpsertIrScoreRefreshMetadata(456, "other-digest");
        using (var db = new SQLiteConnection(env.SongDbPath))
        {
            Assert.AreEqual("delete", db.ExecuteScalar<string>("PRAGMA journal_mode=DELETE;"));
        }
        string before = CaptureRawIrDatabase(env.SongDbPath);
        using var observation = new SqliteStatementObservation();
        var gateway = new BmsLibraryDbGateway(env.SongDbPath, env.ScoreDbPath, path =>
        {
            LR2SongDBExtended writer = observation.OpenSongDb(path);
            writer.BusyTimeout = TimeSpan.Zero;
            return writer;
        });
        IrScoreTableUpdateResult result;
        using (var reader = new SQLiteConnection(env.SongDbPath, SQLiteOpenFlags.ReadOnly))
        {
            reader.BeginTransaction();
            try
            {
                Assert.AreEqual("old-digest", reader.ExecuteScalar<string>(
                    "SELECT score_digest_sha256 FROM ir_score_refresh_metadata WHERE lr2id=123;"));
                result = service.CommitPreparedIrScoreTableWithMetrics(123, gateway, fetched);
            }
            finally { reader.Rollback(); }
        }

        observation.ThrowIfCallbackFailed();
        Assert.IsTrue(observation.Statements.Any(statement => statement.Sql.TrimStart()
            .StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)), "statement失敗をcommit失敗として数えません。");
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(IrScoreFailure.Unavailable, result.Failure);
        Assert.IsNull(result.ScoreTable);
        Assert.IsFalse(result.MetadataUpdated);
        Assert.AreEqual(before, CaptureRawIrDatabase(env.SongDbPath));
    }

    /// <summary>異なる正常取得のmetadata書込みが実DB triggerで失敗したら、本人score全列と全IDのdigest・時刻を一体で保持し、取得値を返さない。</summary>
    [TestMethod]
    public void CommitPreparedIrScoreTable_MetadataStatementFailureRollsBackScoreAndAllMetadata()
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.ReplaceIrScoreTableAndRefreshMetadata([new LR2IRScore("12345678901234567890123456789012") { pg = 321, gr = 45 }], 123, "retained-digest");
        gateway.UpsertIrScoreRefreshMetadata(456, "other-digest");
        using (var db = new SQLiteConnection(env.SongDbPath))
        {
            db.Execute("CREATE TRIGGER reject_ir_metadata BEFORE INSERT ON ir_score_refresh_metadata BEGIN SELECT RAISE(ABORT, 'controlled metadata failure'); END;");
        }
        string before = CaptureRawIrDatabase(env.SongDbPath);
        var service = new BmsLibraryIrService();
        IrScorePrefetchResult fetched = service.PrefetchIrScoreTableWithMetrics(123,
            new FakeIrClient(BuildPlayerScoreXml("12345678901234567890123456789012", 999, 1)), PlayerScoreRegex);

        IrScoreTableUpdateResult result = service.CommitPreparedIrScoreTableWithMetrics(123, gateway, fetched);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(IrScoreFailure.Unavailable, result.Failure);
        Assert.IsNull(result.ScoreTable);
        Assert.IsFalse(result.MetadataUpdated);
        Assert.AreEqual(before, CaptureRawIrDatabase(env.SongDbPath));
    }

    /// <summary>
    /// IRのEASYを上書きすると古いASSIST履歴ビットを置換し、LR2 clear2をEASYとして解決する。
    /// </summary>
    [TestMethod]
    public void BmsScoreOverwriteLr2IrDataAppliesEasyOptionHistoryForIrEasyClear()
    {
        string hash = "12121212121212121212121212121212";
        BMSScore score = CreateScore(hash, ClearType.EASY, pg: 100, gr: 50, minbp: 20);
        score.clearValue = 2;
        score.op_history = ClearTypeStorageConverter.OptionHistoryAssist;

        score.Overwrite(new LR2IRData(hash)
        {
            clear = ClearType.EASY,
            notes = 1000,
            combo = 900,
            pg = 200,
            gr = 50,
            minbp = 10
        });

        Assert.AreEqual(ClearTypeStorageConverter.OptionHistoryEasy, score.op_history);
        Assert.AreEqual(ClearType.EASY, score.clear);
    }

    /// <summary>
    /// 実IR保存行のEASYをlocal scoreへ反映し、旧ASSISTビットを残してclearを誤解決しない。
    /// </summary>
    [TestMethod]
    public void RefreshRankingScoresFromCache_AppliesEasyOptionHistoryWhenIrClearIsApplied()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService();
        string hash = "14141414141414141414141414141414";
        var cacheUpdate = new DateTime(2026, 6, 1, 12, 0, 0);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(
        [
            new LR2IRData(hash)
            {
                lr2id = 123,
                clear = ClearType.EASY,
                notes = 1000,
                combo = 500,
                pg = 200,
                gr = 50,
                minbp = 10,
                rank = 20,
                players_num = 80,
                average = 500,
                sigma = 40,
                lastupdate = cacheUpdate,
                lastcacheupdate = cacheUpdate
            }
        ]);
        BMSScore score = CreateScore(hash, ClearType.EASY, pg: 100, gr: 0, minbp: 30);
        score.clearValue = 2;
        score.op_history = ClearTypeStorageConverter.OptionHistoryAssist;

        service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, [score], estimateOfflineScoreRanking: false);

        Assert.AreEqual(ClearTypeStorageConverter.OptionHistoryEasy, score.op_history);
        Assert.AreEqual(ClearType.EASY, score.clear);
    }

    /// <summary>
    /// IRスコアがlocalより低くてもclear改善はoption履歴を置換し、ASSISTからEASYへの更新を失わない。
    /// </summary>
    [TestMethod]
    public void UpdateBmsScores_ClearOnlyIrScoreUpdateReplacesOptionHistory()
    {
        var service = new BmsLibraryIrService();
        string hash = "15151515151515151515151515151515";
        BMSScore score = CreateScore(hash, ClearType.EASY, pg: 300, gr: 0, minbp: 20);
        score.clearValue = 2;
        score.op_history = ClearTypeStorageConverter.OptionHistoryAssist;
        score.rank = RankType.E;
        score.rate = 30;
        LR2IRScore irScore = CreateIrScore(hash, ClearType.EASY, pg: 100, gr: 0, minbp: 20);
        irScore.notes = 2000;
        irScore.option = ClearTypeStorageConverter.OptionHistoryEasy;

        service.UpdateBmsScores([irScore], [score], _ => false);

        Assert.AreEqual(ClearTypeStorageConverter.OptionHistoryEasy, score.op_history);
        Assert.AreEqual(ClearType.EASY, score.clear);
        Assert.AreEqual(600, score.score);
        Assert.AreEqual(1000, score.totalnotes);
        Assert.AreEqual(RankType.E, score.rank);
        Assert.AreEqual(30, score.rate);
    }

    /// <summary>
    /// 複数root断片・不正行・途中切断の混在でも有効行を採用し、clear2のoption区別と重複hashの先頭優先を保つ。
    /// </summary>
    [TestMethod]
    public void UpdateIrScoreTable_ParsesLr2Clear2ByOptionHistory()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService();
        string assistHash = "16161616161616161616161616161616";
        string easyHash = "17171717171717171717171717171717";
        string xml = BuildPlayerScoreXml(assistHash, pg: 100, gr: 20, clear: 2, option: 0)
            + BuildPlayerScoreXml("18181818181818181818181818181818", pg: 1, gr: 1).Replace("<pg>1</pg>", "<pg>2147483648</pg>")
            + BuildPlayerScoreXml(assistHash, pg: 999, gr: 1, clear: 2, option: ClearTypeStorageConverter.OptionHistoryEasy)
            + BuildPlayerScoreXml(easyHash, pg: 120, gr: 30, clear: 2, option: ClearTypeStorageConverter.OptionHistoryEasy)
                .Replace("</root>\n", "")
            + "\t<score>\n\t\t<hash>";
        var client = new FakeIrClient(xml);

        List<LR2IRScore> scores = service.UpdateIrScoreTable(123, env.CreateGateway(), client, PlayerScoreRegex);

        Assert.AreEqual(2, scores.Count);
        LR2IRScore assistScore = scores.Single(score => score.hash == assistHash);
        LR2IRScore easyScore = scores.Single(score => score.hash == easyHash);
        Assert.AreEqual(100, assistScore.pg);
        Assert.AreEqual(20, assistScore.gr);
        Assert.AreEqual(ClearType.INVALID, assistScore.clear);
        Assert.AreEqual(ClearType.EASY, easyScore.clear);
    }

    /// <summary>
    /// 固定タブ・LF文字列の独立SHA-256を異文化でも保ち、同じ得点のFAILEDとraw clear2/ASSISTを区別する。
    /// </summary>
    [DataTestMethod]
    [DoNotParallelize]
    [DataRow("ja-JP")]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    [DataRow("ar-SA")]
    public void ComputeScoreDigestKeepsRawLr2Clear2DistinctFromFailed(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            string hash = "18181818181818181818181818181818";
            LR2IRScore failedScore = CreateIrScore(hash, ClearType.FAILED, pg: 100, gr: 20, minbp: 12);
            LR2IRScore assistScore = CreateIrScore(hash, ClearType.EASY, pg: 100, gr: 20, minbp: 12);
            assistScore.clearValue = 2;
            assistScore.option = 0;

            string failedDigest = BmsLibraryIrService.ComputeScoreDigest([failedScore]);
            string assistDigest = BmsLibraryIrService.ComputeScoreDigest([assistScore]);

            const string failedInput = "18181818181818181818181818181818\t1\t1000\t900\t100\t20\t0\t0\t0\t12\t0\n";
            const string assistInput = "18181818181818181818181818181818\t2\t1000\t900\t100\t20\t0\t0\t0\t12\t0\n";
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(failedInput))), failedDigest);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(assistInput))), assistDigest);
            Assert.AreNotEqual(failedDigest, assistDigest);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// local scoreだけがある所持譜面は未送信として判定し、更新後snapshotへ未送信とLR2のBP -1に対応する総ノーツ数を渡す。
    /// </summary>
    [TestMethod]
    public void UpdateBmsScores_ProjectsMissingIrScoreAsUnsentChartStatus()
    {
        var service = new BmsLibraryIrService();
        string hash = "13131313131313131313131313131313";
        ChartFile file = CreateFile(hash);
        BMSScore score = CreateScore(hash, ClearType.HARD, pg: 300, gr: 50, minbp: -1);
        score.totalnotes = 200;
        file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));

        service.UpdateBmsScores([], [score], candidate => string.Equals(candidate, hash, StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(score.IsLr2IrScoreUnsent);
        ChartFile chart = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));
        Assert.IsTrue(chart.Status.HasFlag(ChartFileStatus.SCORE_UNSENT));
        Assert.AreEqual(200, chart.Score.MinBp);
        Assert.AreEqual(-1, score.minbp);
    }

    /// <summary>
    /// 同じIR成績が見つかれば古い未送信を消し、譜面statusに未送信表示を残さない。
    /// </summary>
    [TestMethod]
    public void UpdateBmsScores_ClearsStaleUnsentProjectionWhenIrScoreMatches()
    {
        var service = new BmsLibraryIrService();
        string hash = "14141414141414141414141414141414";
        ChartFile file = CreateFile(hash);
        BMSScore score = CreateScore(hash, ClearType.HARD, pg: 300, gr: 50, minbp: 12);
        score.rank = RankType.E;
        score.rate = 32;
        score.IsLr2IrScoreUnsent = true;
        file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));

        LR2IRScore irScore = CreateIrScore(hash, ClearType.HARD, pg: 300, gr: 50, minbp: 12);
        irScore.notes = 2000;
        service.UpdateBmsScores([irScore], [score], candidate => string.Equals(candidate, hash, StringComparison.OrdinalIgnoreCase));

        Assert.IsFalse(score.IsLr2IrScoreUnsent);
        Assert.AreEqual(650, score.score);
        Assert.AreEqual(1000, score.totalnotes);
        Assert.AreEqual(RankType.E, score.rank);
        Assert.AreEqual(32, score.rate);
        ChartFile chart = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));
        Assert.IsFalse(chart.Status.HasFlag(ChartFileStatus.SCORE_UNSENT));
    }

    /// <summary>
    /// 検出を明示無効にした要求はIR不在でも未送信を付けず、設定を無視して警告状態を作らない。
    /// </summary>
    [TestMethod]
    public void UpdateBmsScores_DoesNotProjectUnsentWhenDetectionIsDisabled()
    {
        var service = new BmsLibraryIrService();
        string hash = "15151515151515151515151515151515";
        ChartFile file = CreateFile(hash);
        BMSScore score = CreateScore(hash, ClearType.HARD, pg: 300, gr: 50, minbp: 12);
        file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));

        service.UpdateBmsScores([], [score], candidate => string.Equals(candidate, hash, StringComparison.OrdinalIgnoreCase), detectUnsentScores: false);

        Assert.IsFalse(score.IsLr2IrScoreUnsent);
        ChartFile chart = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));
        Assert.IsFalse(chart.Status.HasFlag(ChartFileStatus.SCORE_UNSENT));
    }

    /// <summary>
    /// IR側のより良い得点/BPとnotesからAAA/90を採用し、未送信を残さず更新済みsnapshotへ状態を渡す。
    /// </summary>
    [TestMethod]
    public void UpdateBmsScores_DoesNotLeaveUnsentAfterIrScoreOverwrite()
    {
        var service = new BmsLibraryIrService();
        string hash = "16161616161616161616161616161616";
        ChartFile file = CreateFile(hash);
        BMSScore score = CreateScore(hash, ClearType.EASY, pg: 100, gr: 10, minbp: 50);
        score.totalnotes = 0;
        LR2IRScore irScore = CreateIrScore(hash, ClearType.HARD, pg: 800, gr: 200, minbp: 12);
        file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));

        service.UpdateBmsScores([irScore], [score], candidate => string.Equals(candidate, hash, StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(irScore.score, score.score);
        Assert.AreEqual(1000, score.totalnotes);
        Assert.AreEqual(RankType.AAA, score.rank);
        Assert.AreEqual(90, score.rate);
        Assert.AreEqual(12, score.minbp);
        Assert.IsFalse(score.IsLr2IrScoreUnsent);
        ChartFile chart = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));
        Assert.IsFalse(chart.Status.HasFlag(ChartFileStatus.SCORE_UNSENT));
    }

    /// <summary>
    /// local PAと同成績のIR FCは送信済みとして扱い、IRが持たないPA区別だけで未送信にしない。
    /// </summary>
    [TestMethod]
    public void UpdateBmsScores_TreatsLocalPaAndIrFcAsSent()
    {
        var service = new BmsLibraryIrService();
        string hash = "17171717171717171717171717171717";
        ChartFile file = CreateFile(hash);
        BMSScore score = CreateScore(hash, ClearType.PA, pg: 300, gr: 50, minbp: 12);
        file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));

        service.UpdateBmsScores([CreateIrScore(hash, ClearType.FC, pg: 300, gr: 50, minbp: 12)], [score], candidate => string.Equals(candidate, hash, StringComparison.OrdinalIgnoreCase));

        Assert.IsFalse(score.IsLr2IrScoreUnsent);
    }

    /// <summary>
    /// 実beatoraja DBのmode0だけを読み、成績変換と未記録BP・実績0・正数の両一覧への共通投影を確認する。
    /// 投影は元DBと成績を変えず、外部DB更新後の再読込みでは実績BPへ戻ることを確認する。
    /// </summary>
    [TestMethod]
    public void BeatorajaScoreDbLoader_ReadsModeZeroScoresOnly()
    {
        string rootDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerBeatorajaScoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectoryPath);
        string scoreDbPath = Path.Combine(rootDirectoryPath, "score.db");
        try
        {
            using (var connection = new SQLiteConnection(scoreDbPath))
            {
                connection.Execute(
                    "CREATE TABLE score (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER, PRIMARY KEY(sha256, mode));");
                connection.RunInTransaction(() =>
                {
                    connection.Execute(
                        "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 0, 2, 100, 20, 30, 10, 200, 180, 5, 7, 3);
                    connection.Execute(
                        "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", 0, 4, 150, 25, 40, 15, 300, 240, 6, 8, 4);
                    connection.Execute(
                        "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 10000, 7, 500, 0, 0, 0, 500, 500, 0, 1, 1);
                    connection.Execute(
                        "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        new string('d', 64), 0, 5, 0, 0, 0, 0, 0, 0, int.MaxValue, 9, 2);
                    connection.Execute(
                        "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        new string('e', 64), 0, 8, 100, 20, 30, 10, 200, 180, 0, 4, 1);
                });
            }

            var loader = new BeatorajaScoreDbLoader();
            Dictionary<string, BMSScore> scores = loader.LoadModeZeroScores(scoreDbPath);

            Assert.AreEqual(4, scores.Count);
            Assert.IsTrue(scores.TryGetValue("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", out BMSScore? score));
            Assert.IsNotNull(score);
            BMSScore loadedScore = score;
            Assert.AreEqual(ClearType.INVALID, loadedScore.clear);
            Assert.AreEqual(120, loadedScore.perfect);
            Assert.AreEqual(40, loadedScore.great);
            Assert.AreEqual(200, loadedScore.totalnotes);
            Assert.AreEqual(180, loadedScore.maxcombo);
            Assert.AreEqual(5, loadedScore.minbp);
            Assert.AreEqual(280, loadedScore.score);
            Assert.AreEqual(70, loadedScore.rate);
            Assert.AreEqual(RankType.A, loadedScore.rank);
            Assert.AreEqual(7, loadedScore.playcount);
            Assert.AreEqual(3, loadedScore.clearcount);
            AssertProjectedBp(loadedScore, expectedBp: 5, ClearType.INVALID);
            Assert.IsTrue(scores.TryGetValue("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", out BMSScore? easyScore));
            Assert.IsNotNull(easyScore);
            BMSScore loadedEasyScore = easyScore;
            Assert.AreEqual(ClearType.EASY, loadedEasyScore.clear);

            BMSScore unrecordedScore = scores[new string('d', 64)];
            AssertProjectedBp(unrecordedScore, expectedBp: null, ClearType.CLEAR);
            Assert.AreEqual(int.MaxValue, unrecordedScore.minbp);
            Assert.AreEqual(0, unrecordedScore.totalnotes);
            Assert.AreEqual(9, unrecordedScore.playcount);
            Assert.AreEqual(2, unrecordedScore.clearcount);
            BMSScore zeroBpScore = scores[new string('e', 64)];
            AssertProjectedBp(zeroBpScore, expectedBp: 0, ClearType.FC);
            Assert.AreEqual(0, zeroBpScore.minbp);
            Assert.AreEqual(4, zeroBpScore.playcount);
            Assert.AreEqual(1, zeroBpScore.clearcount);

            using (var observation = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex))
            {
                Assert.AreEqual(int.MaxValue, observation.ExecuteScalar<int>("SELECT minbp FROM score WHERE sha256 = ? AND mode = 0;", unrecordedScore.hash));
                Assert.AreEqual(0, observation.ExecuteScalar<int>("SELECT minbp FROM score WHERE sha256 = ? AND mode = 0;", zeroBpScore.hash));
            }
            using (var externalPlayer = new SQLiteConnection(scoreDbPath))
            {
                externalPlayer.Execute("UPDATE score SET epg = ?, lpg = ?, egr = ?, lgr = ?, notes = ?, combo = ?, minbp = ?, playcount = ?, clearcount = ? WHERE sha256 = ? AND mode = 0;", 100, 20, 30, 10, 200, 180, 12, 10, 3, unrecordedScore.hash);
            }

            BMSScore replayedScore = loader.LoadModeZeroScores(scoreDbPath)[unrecordedScore.hash];
            AssertProjectedBp(replayedScore, expectedBp: 12, ClearType.CLEAR);
            Assert.AreEqual(12, replayedScore.minbp);
            Assert.AreEqual(200, replayedScore.totalnotes);
            Assert.AreEqual(10, replayedScore.playcount);
            Assert.AreEqual(3, replayedScore.clearcount);
            Assert.AreEqual(int.MaxValue, unrecordedScore.minbp);
        }
        finally
        {
            if (Directory.Exists(rootDirectoryPath))
            {
                Directory.Delete(rootDirectoryPath, recursive: true);
            }
        }

        static void AssertProjectedBp(BMSScore score, int? expectedBp, ClearType expectedClear)
        {
            ChartFile file = ChartTestValues.Empty() with
            {
                Md5 = new string('1', 32),
                Sha256 = score.hash,
                Path = score.hash + ".bms"
            };
            ChartFile chart = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(score, file.Path));
            var libraryRow = LibraryChartRow.FromChartFile(chart);
            var librarySource = ChartListSourceRow.FromChartFile(chart);
            var playlistSource = new PlaylistDetailSourceRow(new BMSTableEntry(file), file, scoreSnapshot: score);
            PlaylistDetailRow playlistRow = playlistSource.CreateViewRow();

            Assert.AreEqual(expectedBp, chart.Score.MinBp);
            Assert.AreEqual(expectedBp, libraryRow.minbp);
            Assert.AreEqual(expectedBp, librarySource.MinBp);
            Assert.AreEqual(expectedBp, playlistSource.minbp);
            Assert.AreEqual(expectedBp, playlistRow.minbp);
            Assert.AreEqual(score.hash, playlistSource.sha256);
            Assert.AreEqual(expectedClear, libraryRow.clear);
            Assert.AreEqual(expectedClear, librarySource.Clear);
            Assert.AreEqual(expectedClear, playlistSource.clear);
            Assert.AreEqual(expectedClear, playlistRow.clear);
        }
    }

    /// <summary>
    /// beatoraja利用時は実LR2 DBがあってもbeatorajaだけを読み、LR2 score/IDを合算しない。
    /// </summary>
    [TestMethod]
    public void LoadScoreTable_UsesBeatorajaAsExclusiveSourceWhenEnabled()
    {
        using var env = TempIrEnvironment.Create();
        CreateLr2ScoreDb(env.ScoreDbPath);
        string beatorajaScoreDbPath = Path.Combine(env.RootDirectoryPath, "beatoraja", "player1", "score.db");
        CreateBeatorajaScoreDb(beatorajaScoreDbPath);
        var service = new BmsLibraryInitializationService();

        ScoreTableLoadResult result = service.LoadScoreTable(
            env.CreateGateway(),
            new BmsLibraryOptionsSnapshot
            {
                UseBeatorajaScoreDb = true,
                BeatorajaScoreDbPath = beatorajaScoreDbPath
            });

        Assert.AreEqual(ActiveScoreSource.Beatoraja, result.ActiveScoreSource);
        Assert.AreEqual(ScoreTableLoadStatus.Loaded, result.Status);
        Assert.AreEqual(0, result.Scores.Count);
        Assert.AreEqual(0, result.LR2Id);
        Assert.AreEqual(1, result.BeatorajaScoresBySha256.Count);
        Assert.IsTrue(result.BeatorajaScoresBySha256.ContainsKey("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    /// <summary>
    /// beatoraja無効時は実LR2 scoreとplayer IDを読み、beatoraja側の結果を残さない。
    /// </summary>
    [TestMethod]
    public void LoadScoreTable_UsesLr2SourceWhenBeatorajaIsDisabled()
    {
        using var env = TempIrEnvironment.Create();
        CreateLr2ScoreDb(env.ScoreDbPath);
        var service = new BmsLibraryInitializationService();

        ScoreTableLoadResult result = service.LoadScoreTable(
            env.CreateGateway(),
            new BmsLibraryOptionsSnapshot
            {
                UseBeatorajaScoreDb = false
            });

        Assert.AreEqual(ActiveScoreSource.Lr2, result.ActiveScoreSource);
        Assert.AreEqual(ScoreTableLoadStatus.Loaded, result.Status);
        Assert.AreEqual(1, result.Scores.Count);
        Assert.AreEqual(123, result.LR2Id);
        Assert.AreEqual(0, result.BeatorajaScoresBySha256.Count);
    }

    /// <summary>
    /// 選択したbeatoraja DBが不在なら元取得失敗を返し、実在LR2 DBへ黙って切り替えない。
    /// </summary>
    [TestMethod]
    public void LoadScoreTable_reportsBeatorajaFailureWithoutFallingBackToLr2()
    {
        using var env = TempIrEnvironment.Create();
        CreateLr2ScoreDb(env.ScoreDbPath);
        string missingBeatorajaScoreDbPath = Path.Combine(env.RootDirectoryPath, "beatoraja", "player1", "score.db");
        var service = new BmsLibraryInitializationService();

        ScoreTableLoadResult result = service.LoadScoreTable(
            env.CreateGateway(),
            new BmsLibraryOptionsSnapshot
            {
                UseBeatorajaScoreDb = true,
                BeatorajaScoreDbPath = missingBeatorajaScoreDbPath
            });

        Assert.AreEqual(ActiveScoreSource.Beatoraja, result.ActiveScoreSource);
        Assert.AreEqual(ScoreTableLoadStatus.Failed, result.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureMessage));
        Assert.AreEqual(0, result.Scores.Count);
        Assert.AreEqual(0, result.BeatorajaScoresBySha256.Count);
    }

    /// <summary>
    /// LR2/beatorajaの選択DBが空でもLoadedと空scoreを返し、別接続での後続テーブル作成が成功する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LoadScoreTable_EmptySelectedDatabaseIsLoaded(bool beatoraja)
    {
        using var env = TempIrEnvironment.Create();
        using (var db = new SQLiteConnection(env.ScoreDbPath))
        {
            db.RunInTransaction(() =>
            {
                if (beatoraja)
                {
                    db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
                }
                else
                {
                    db.CreateTable<BMSScore>();
                    db.CreateTable<LR2ScoreDB.player>();
                    db.Insert(new LR2ScoreDB.player { id = "player", irid = 0 });
                }
            });
        }
        var service = new BmsLibraryInitializationService();
        ScoreTableLoadResult result = service.LoadScoreTable(env.CreateGateway(), new BmsLibraryOptionsSnapshot
        {
            UseBeatorajaScoreDb = beatoraja,
            BeatorajaScoreDbPath = beatoraja ? env.ScoreDbPath : null
        });
        Assert.AreEqual(ScoreTableLoadStatus.Loaded, result.Status);
        Assert.AreEqual(0, result.Scores.Count + result.BeatorajaScoresBySha256.Count);
        using var writeAfterRead = new SQLiteConnection(env.ScoreDbPath);
        writeAfterRead.Execute("CREATE TABLE released_connection (value INTEGER)");
    }

    /// <summary>
    /// LR2/beatorajaどちらも選択パス未設定ならNotConfiguredと空scoreを返し、取得失敗や架空DBへ置き換えない。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LoadScoreTable_NoSelectedDatabaseIsNotConfigured(bool beatoraja)
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryInitializationService();
        ScoreTableLoadResult result = service.LoadScoreTable(new BmsLibraryDbGateway(env.SongDbPath, null),
            new BmsLibraryOptionsSnapshot { UseBeatorajaScoreDb = beatoraja });
        Assert.AreEqual(ScoreTableLoadStatus.NotConfigured, result.Status);
        Assert.AreEqual(0, result.Scores.Count + result.BeatorajaScoresBySha256.Count);
    }

    /// <summary>
    /// XML時刻が保存行と同じなら再解析/再保存せず実DBの順位をlocal scoreへ渡す。
    /// </summary>
    /// <remarks>実XMLの時刻と実SQLiteの保存時刻を揃え、DB再利用時の解析・保存件数とlocal順位を確認する。</remarks>
    [TestMethod]
    public void RefreshRankingScoresFromCache_UnchangedXmlAppliesDbRowWithoutUpsert()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService();
        string hash = "11111111111111111111111111111111";
        var cacheUpdate = new DateTime(2026, 1, 1, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(
        [
            new LR2IRData(hash)
            {
                lr2id = 123,
                clear = ClearType.HARD,
                notes = 1000,
                combo = 900,
                pg = 450,
                gr = 50,
                minbp = 12,
                rank = 10,
                players_num = 100,
                average = 700,
                sigma = 50,
                lastupdate = cacheUpdate,
                lastcacheupdate = cacheUpdate
            }
        ]);
        List<BMSScore> scores = [];

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, scores, estimateOfflineScoreRanking: false);

        Assert.AreEqual(1, result.CacheFilesScanned);
        Assert.AreEqual(0, result.CacheFilesReloaded);
        Assert.AreEqual(0, result.IrDataUpsertCount);
        Assert.AreEqual(1, result.DbFallbackAppliedCount);
        Assert.AreEqual(1, scores.Count);
        Assert.AreEqual(hash, scores[0].hash);
        Assert.AreEqual(10, scores[0].ranking);
    }

    /// <summary>順位DBの実読取りが失敗したら更新を成功として返さず、全保存列と既存score値・hash集合を保持する。</summary>
    [TestMethod]
    public void RefreshRankingScoresFromCache_SavedRankingReadFailurePreservesDatabaseAndScores()
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        const string hash = "11111111111111111111111111111111";
        const string otherHash = "22222222222222222222222222222222";
        var cacheUpdate = new DateTime(2026, 1, 1, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, pg: 700, gr: 100);
        gateway.UpsertIrData(
        [
            new LR2IRData(hash) { lr2id = 123, rank = 20, players_num = 100, pg = 400, gr = 20, lastupdate = cacheUpdate.AddDays(-1) },
            new LR2IRData(otherHash) { lr2id = 123, rank = 30, players_num = 100, pg = 300, gr = 10 },
            new LR2IRData(hash) { lr2id = 456, rank = 40, pg = 200 }
        ]);
        using (var db = new SQLiteConnection(env.SongDbPath))
        {
            db.Execute("UPDATE ir_data SET hash = 'unmaterializable-hash' WHERE hash = ? AND lr2id = 123", otherHash);
        }
        bool readFailed = false;
        try { gateway.LoadIrDataWithMetrics(123); }
        catch (Exception) { readFailed = true; }
        Assert.IsTrue(readFailed, "正常空ではなく、実Gatewayの順位DB読取りが失敗するfixtureが必要です。");
        string databaseBefore = CaptureRawRankingDatabase(env.SongDbPath);
        List<BMSScore> scores = [CreateScore(hash, ClearType.EASY, pg: 100, gr: 10, minbp: 40)];
        scores[0].ranking = 50;
        scores[0].rankingNum = 200;
        string scoresBefore = JsonConvert.SerializeObject(scores);
        var service = new BmsLibraryIrService();
        bool refreshFailed = false;
        try { service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, scores, estimateOfflineScoreRanking: false); }
        catch (Exception) { refreshFailed = true; }

        Assert.IsTrue(refreshFailed, "読取り失敗を空plan・更新不要の成功へ変換しません。");
        Assert.AreEqual(databaseBefore, CaptureRawRankingDatabase(env.SongDbPath));
        Assert.AreEqual(scoresBefore, JsonConvert.SerializeObject(scores));
    }

    /// <summary>
    /// 実IR保存行を指定LR2 IDだけへ絞り、読取り専用接続でprocess lockを取らない。他プレイヤー行の混入を検出する。
    /// </summary>
    [TestMethod]
    public void LoadIrDataWithMetrics_FiltersByLr2IdInSqlLoader()
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(
        [
            new LR2IRData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            {
                lr2id = 123,
                rank = 10
            },
            new LR2IRData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            {
                lr2id = 456,
                rank = 20
            }
        ]);

        IrDataLoadResult result = gateway.LoadIrDataWithMetrics(123);

        Assert.AreEqual(1, result.Rows.Count);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result.Rows[0].hash);
        Assert.IsTrue(result.ReadOnly);
        Assert.AreEqual(0L, result.DbLockWaitMs);
    }

    /// <summary>
    /// XMLに対応する保存行がなければ指定並列度で解析し、一度bulk保存してlocal scoreへ適用する。
    /// </summary>
    /// <remarks>実XMLから保存行のない実SQLiteへ取り込み、保存行の読戻しとlocal scoreへの反映を確認する。</remarks>
    [TestMethod]
    public void RefreshRankingScoresFromCache_MissingDbRowReloadsXmlAndUpsertsOnce()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService(1);
        string hash = "22222222222222222222222222222222";
        var cacheUpdate = new DateTime(2026, 2, 1, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        List<BMSScore> scores = [];

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, scores, estimateOfflineScoreRanking: false);

        Assert.AreEqual(1, result.CacheFilesScanned);
        Assert.AreEqual(1, result.CacheFilesReloaded);
        Assert.AreEqual(1, result.XmlReloadDegree);
        Assert.AreEqual(1, result.XmlScoresParsed);
        Assert.AreEqual(0, result.XmlFallbackLoadCount);
        Assert.AreEqual(0, result.XmlParseFailedCount);
        Assert.AreEqual(1, result.IrDataUpsertCount);
        Assert.IsTrue(result.BulkInsertUsed);
        Assert.AreEqual(0, result.DbFallbackAppliedCount);
        Assert.AreEqual(1, result.XmlAppliedCount);
        Assert.AreEqual(1, scores.Count);
        Assert.AreEqual(hash, scores[0].hash);
        Assert.AreEqual(1, gateway.LoadIrData(123).Count(data => data.hash == hash));
    }

    /// <summary>
    /// 複数プレイヤーXMLから対象の成績/順位と全体の平均/標準偏差を算出し、更新/捕捉時刻を区別して保持する。
    /// </summary>
    [DataTestMethod]
    [DoNotParallelize]
    [DataRow("ja-JP")]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    [DataRow("ar-SA")]
    public void RankingCacheSummaryParser_TargetPlayerUsesUnifiedExpectedValues(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            string hash = "21212121212121212121212121212121";
            var cacheUpdate = new DateTime(2026, 4, 1, 12, 0, 0);
            var lastUpdate = new DateTime(2026, 4, 2, 13, 14, 15);
            string xml = BuildRankingCacheXml(lastUpdate,
            [
                new RankingScoreRow { Lr2Id = 100, Notes = 1000, Combo = 850, Pg = 400, Gr = 40, MinBp = 20, Clear = 3 },
                new RankingScoreRow { Lr2Id = 123, Notes = 1000, Combo = 950, Pg = 500, Gr = 20, MinBp = 5, Clear = 4 },
                new RankingScoreRow { Lr2Id = 200, Notes = 1000, Combo = 900, Pg = 450, Gr = 50, MinBp = 10, Clear = 2 }
            ]);

            bool parsed = BmsLibraryIrService.TryParseRankingCacheSummary(xml, hash, 123, cacheUpdate, out LR2IRData actual, out int scoresParsed);

            Assert.IsTrue(parsed);
            Assert.AreEqual(3, scoresParsed);
            Assert.AreEqual(hash, actual.hash);
            Assert.AreEqual(123, actual.lr2id);
            Assert.AreEqual(1, actual.rank);
            Assert.AreEqual(3, actual.players_num);
            Assert.AreEqual(936.6666666666666, actual.average, 0.0000001);
            Assert.AreEqual(90.73771725877467, actual.sigma, 0.0000001);
            Assert.AreEqual(ClearType.HARD, actual.clear);
            Assert.AreEqual(1000, actual.notes);
            Assert.AreEqual(950, actual.combo);
            Assert.AreEqual(500, actual.pg);
            Assert.AreEqual(20, actual.gr);
            Assert.AreEqual(5, actual.minbp);
            Assert.AreEqual(lastUpdate, actual.lastupdate);
            Assert.AreEqual(cacheUpdate, actual.lastcacheupdate);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// 対象プレイヤー不在は未プレイ/順位-1と全体統計を返し、他人の成績を対象へ流用しない。
    /// </summary>
    [TestMethod]
    public void RankingCacheSummaryParser_MissingPlayerUsesUnifiedExpectedValues()
    {
        string hash = "23232323232323232323232323232323";
        var cacheUpdate = new DateTime(2026, 4, 1, 12, 0, 0);
        var lastUpdate = new DateTime(2026, 4, 2, 13, 14, 15);
        string xml = BuildRankingCacheXml(lastUpdate,
        [
            new RankingScoreRow { Lr2Id = 100, Notes = 800, Combo = 850, Pg = 500, Gr = 0, MinBp = 20, Clear = 3 },
            new RankingScoreRow { Lr2Id = 200, Notes = 1200, Combo = 950, Pg = 500, Gr = 0, MinBp = 5, Clear = 4 },
            new RankingScoreRow { Lr2Id = 300, Notes = 1600, Combo = 900, Pg = 100, Gr = 50, MinBp = 10, Clear = 2 }
        ]);

        bool parsed = BmsLibraryIrService.TryParseRankingCacheSummary(xml, hash, 999, cacheUpdate, out LR2IRData actual, out int scoresParsed);

        Assert.IsTrue(parsed);
        Assert.AreEqual(3, scoresParsed);
        Assert.AreEqual(hash, actual.hash);
        Assert.AreEqual(999, actual.lr2id);
        Assert.AreEqual(-1, actual.rank);
        Assert.AreEqual(3, actual.players_num);
        Assert.AreEqual(750.0, actual.average, 0.0000001);
        Assert.AreEqual(433.0127018922193, actual.sigma, 0.0000001);
        Assert.AreEqual(ClearType.NO_PLAY, actual.clear);
        Assert.AreEqual(1200, actual.notes);
        Assert.AreEqual(0, actual.pg);
        Assert.AreEqual(0, actual.gr);
        Assert.AreEqual(lastUpdate, actual.lastupdate);
        Assert.AreEqual(cacheUpdate, actual.lastcacheupdate);
    }

    /// <summary>
    /// lastupdate空と末尾NULを受理し、更新時刻に独立して渡したcache書込時刻を採用する。
    /// </summary>
    [TestMethod]
    public void RankingCacheSummaryParser_EmptyLastupdateWithNulFallsBackToCacheWriteTime()
    {
        string hash = "24242424242424242424242424242424";
        var cacheUpdate = new DateTime(2026, 4, 3, 9, 0, 0);
        string xml = BuildRankingCacheXml(null,
        [
            new RankingScoreRow { Lr2Id = 123, Notes = 1000, Combo = 850, Pg = 400, Gr = 40, MinBp = 20, Clear = 3 }
        ], appendNul: true);

        bool parsed = BmsLibraryIrService.TryParseRankingCacheSummary(xml, hash, 123, cacheUpdate, out LR2IRData actual, out int scoresParsed);

        Assert.IsTrue(parsed);
        Assert.AreEqual(1, scoresParsed);
        Assert.AreEqual(cacheUpdate, actual.lastupdate);
        Assert.AreEqual(cacheUpdate, actual.lastcacheupdate);
    }

    /// <summary>
    /// IR cache入口でも負のBP行を除外して2人分の順位を返し、summary入口との必要な成績/統計の受渡しを保つ。
    /// </summary>
    [TestMethod]
    public void LR2IRCacheWrapper_UsesUnifiedParserSemantics()
    {
        string hash = "34343434343434343434343434343434";
        var cacheUpdate = new DateTime(2026, 4, 3, 9, 0, 0);
        var lastUpdate = new DateTime(2026, 4, 3, 10, 0, 0);
        string xml = BuildRankingCacheXml(lastUpdate,
        [
            new RankingScoreRow { Lr2Id = 123, Notes = 1000, Combo = 850, Pg = 400, Gr = 40, MinBp = 20, Clear = 3 },
            new RankingScoreRow { Lr2Id = 456, Notes = 1000, Combo = 950, Pg = 500, Gr = 20, MinBp = -1, Clear = 4 },
            new RankingScoreRow { Lr2Id = 789, Notes = 1000, Combo = 900, Pg = 450, Gr = 50, MinBp = 10, Clear = 2 }
        ]);

        var cache = new LR2IRCache(xml, hash, cacheUpdate);
        bool parsed = BmsLibraryIrService.TryParseRankingCacheSummary(xml, hash, 123, cacheUpdate, out LR2IRData summary, out int scoresParsed);
        LR2IRData fromCache = cache.GetLR2IRData(123);

        Assert.IsTrue(parsed);
        Assert.AreEqual(2, scoresParsed);
        Assert.AreEqual(2, cache.GetRankingNum());
        AssertIrDataEquivalent(summary, fromCache);
        Assert.AreEqual(2, cache.GetRankFromScore(fromCache.score));
    }


    /// <summary>
    /// 閉じていないXMLは解析不成立・結果なし・0件を返し、一部成功の成績を補わない。
    /// </summary>
    [TestMethod]
    public void RankingCacheSummaryParser_MalformedXmlReturnsFalse()
    {
        bool parsed = BmsLibraryIrService.TryParseRankingCacheSummary(
            "<root><ranking><score><id>123</id><pg>1</pg></score></ranking>",
            "25252525252525252525252525252525",
            123,
            new DateTime(2026, 4, 4),
            out LR2IRData actual,
            out int scoresParsed);

        Assert.IsFalse(parsed);
        Assert.IsNull(actual);
        Assert.AreEqual(0, scoresParsed);
    }

    /// <summary>
    /// 負のBPを持つ行を統計/順位から除き、有効な対象一人だけの平均と順位を返す。
    /// </summary>
    [TestMethod]
    public void RankingCacheSummaryParser_SkipsNegativeNumericFields()
    {
        string hash = "33333333333333333333333333333334";
        var cacheUpdate = new DateTime(2026, 4, 4, 12, 0, 0);
        var lastUpdate = new DateTime(2026, 4, 4, 13, 0, 0);
        string xml = "<root>\n"
            + "<ranking>\n"
            + "\t\t<score>\n"
            + "\t\t<id>123</id>\n"
            + "\t\t<clear>4</clear>\n"
            + "\t\t<notes>1000</notes>\n"
            + "\t\t<combo>900</combo>\n"
            + "\t\t<pg>500</pg>\n"
            + "\t\t<gr>0</gr>\n"
            + "\t\t<minbp>12</minbp>\n"
            + "\t\t</score>\n"
            + "\t\t<score>\n"
            + "\t\t<id>456</id>\n"
            + "\t\t<clear>4</clear>\n"
            + "\t\t<notes>1000</notes>\n"
            + "\t\t<combo>900</combo>\n"
            + "\t\t<pg>600</pg>\n"
            + "\t\t<gr>0</gr>\n"
            + "\t\t<minbp>-12</minbp>"
            + "\t\t</score>\n"
            + "</ranking>\n"
            + "<lastupdate>" + lastUpdate.ToString("yyyy-MM-dd HH:mm:ss") + "</lastupdate>\n";

        bool parsed = BmsLibraryIrService.TryParseRankingCacheSummary(xml, hash, 123, cacheUpdate, out LR2IRData actual, out int scoresParsed);

        Assert.IsTrue(parsed);
        Assert.AreEqual(1, scoresParsed);
        Assert.AreEqual(1, actual.players_num);
        Assert.AreEqual(1, actual.rank);
        Assert.AreEqual(1000, actual.average);
    }

    /// <summary>
    /// IR保存が空の二XMLを解析して二行bulk保存し、件数の欠落や単件だけの反映を検出する。
    /// </summary>
    /// <remarks>二つの実XMLを空の実SQLiteへ取り込み、bulk保存の件数をDBから読み戻して確認する。</remarks>
    [TestMethod]
    public void RefreshRankingScoresFromCache_EmptyIrDataBulkInsertsMultipleXmlRows()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService(2);
        var cacheUpdate = new DateTime(2026, 4, 5, 12, 0, 0);
        env.WriteCacheXml("26262626262626262626262626262626", cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        env.WriteCacheXml("27272727272727272727272727272727", cacheUpdate, cacheUpdate, lr2Id: 123, pg: 600, gr: 100);
        BmsLibraryDbGateway gateway = env.CreateGateway();

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, [], estimateOfflineScoreRanking: false);

        Assert.AreEqual(2, result.CacheFilesReloaded);
        Assert.AreEqual(2, result.IrDataUpsertCount);
        Assert.AreEqual(2, result.XmlScoresParsed);
        Assert.AreEqual(0, result.XmlParseFailedCount);
        Assert.IsTrue(result.BulkInsertUsed);
        Assert.AreEqual(2, gateway.LoadIrData(123).Count);
    }

    /// <summary>
    /// 同じ二XMLは並列度1/2の双方で二行保存し、全成績/統計/時刻を一致させる。並列化による取り落しを検出する。
    /// </summary>
    /// <remarks>同じ実XMLを二つの実SQLiteへ異なる並列度で取り込み、保存行の成績・統計・時刻を比較する。</remarks>
    [TestMethod]
    public void RefreshRankingScoresFromCache_ProducesSameRowsWithDifferentXmlReloadDegree()
    {
        using var envDegree1 = TempIrEnvironment.Create();
        using var envDegree2 = TempIrEnvironment.Create();
        var cacheUpdate = new DateTime(2026, 4, 5, 12, 0, 0);
        string[] hashes =
        [
            "37373737373737373737373737373737",
            "38383838383838383838383838383838"
        ];
        envDegree1.WriteCacheXml(hashes[0], cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        envDegree1.WriteCacheXml(hashes[1], cacheUpdate, cacheUpdate, lr2Id: 123, pg: 600, gr: 100);
        envDegree2.WriteCacheXml(hashes[0], cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        envDegree2.WriteCacheXml(hashes[1], cacheUpdate, cacheUpdate, lr2Id: 123, pg: 600, gr: 100);

        IrCacheRefreshResult resultDegree1 = new BmsLibraryIrService(1).RefreshRankingScoresFromCache(123, envDegree1.ScoreDbPath, envDegree1.CreateGateway(), [], estimateOfflineScoreRanking: false);
        IrCacheRefreshResult resultDegree2 = new BmsLibraryIrService(2).RefreshRankingScoresFromCache(123, envDegree2.ScoreDbPath, envDegree2.CreateGateway(), [], estimateOfflineScoreRanking: false);

        List<LR2IRData> rowsDegree1 = [.. envDegree1.CreateGateway().LoadIrData(123).OrderBy(row => row.hash)];
        List<LR2IRData> rowsDegree2 = [.. envDegree2.CreateGateway().LoadIrData(123).OrderBy(row => row.hash)];
        Assert.AreEqual(2, resultDegree1.IrDataUpsertCount);
        Assert.AreEqual(2, resultDegree2.IrDataUpsertCount);
        Assert.AreEqual(rowsDegree1.Count, rowsDegree2.Count);
        for (int i = 0; i < rowsDegree1.Count; i++)
        {
            AssertIrDataEquivalent(rowsDegree1[i], rowsDegree2[i]);
        }
    }

    /// <summary>
    /// 既存IR保存行がある新XMLは増分置換を一度行い、同じhashの重複を作らず新得点へ更新する。
    /// </summary>
    /// <remarks>実XMLを既存行のある実SQLiteへ取り込み、増分保存後の行数と得点を読み戻して確認する。</remarks>
    [TestMethod]
    public void RefreshRankingScoresFromCache_NonEmptyIrDataUsesIncrementalUpsert()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService(1);
        string hash = "28282828282828282828282828282828";
        var currentUpdate = new DateTime(2026, 4, 5, 12, 0, 0);
        var newerUpdate = new DateTime(2026, 4, 6, 12, 0, 0);
        env.WriteCacheXml(hash, newerUpdate, newerUpdate, lr2Id: 123, pg: 700, gr: 100);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(
        [
            new LR2IRData(hash)
            {
                lr2id = 123,
                clear = ClearType.CLEAR,
                pg = 100,
                gr = 0,
                rank = 20,
                players_num = 30,
                lastupdate = currentUpdate,
                lastcacheupdate = currentUpdate
            }
        ]);

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, [], estimateOfflineScoreRanking: false);

        Assert.AreEqual(1, result.CacheFilesReloaded);
        Assert.AreEqual(1, result.IrDataUpsertCount);
        Assert.IsFalse(result.BulkInsertUsed);
        Assert.AreEqual(1, gateway.LoadIrData(123).Count(data => data.hash == hash));
        Assert.AreEqual(1500, gateway.LoadIrData(123).Single(data => data.hash == hash).score);
    }

    /// <summary>
    /// 同じhash/IDを二度保存すると一行に置換し、順位と得点を最後の値へ更新する。
    /// </summary>
    [TestMethod]
    public void UpsertIrData_ReplacesSameHashAndLr2IdWithoutDuplicates()
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "29292929292929292929292929292929";

        gateway.UpsertIrData([new LR2IRData(hash) { lr2id = 123, rank = 10, pg = 100 }]);
        gateway.UpsertIrData([new LR2IRData(hash) { lr2id = 123, rank = 1, pg = 500 }]);

        List<LR2IRData> rows = [.. gateway.LoadIrData(123).Where(data => data.hash == hash)];
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(1, rows[0].rank);
        Assert.AreEqual(1000, rows[0].score);
    }

    /// <summary>
    /// 同じhashでも別LR2 IDの保存行は双方を保持し、プレイヤーを跨いで上書きしない。
    /// </summary>
    [TestMethod]
    public void UpsertIrData_PreservesSameHashForDifferentLr2Id()
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "30303030303030303030303030303030";

        gateway.UpsertIrData(
        [
            new LR2IRData(hash) { lr2id = 123, rank = 10 },
            new LR2IRData(hash) { lr2id = 456, rank = 20 }
        ]);

        Assert.AreEqual(1, gateway.LoadIrData(123).Count(data => data.hash == hash));
        Assert.AreEqual(1, gateway.LoadIrData(456).Count(data => data.hash == hash));
    }

    /// <summary>
    /// 旧DBの同一hash/ID重複行をschema準備後も読み取れ、互換入力を一意制約で拒否/消去しない。
    /// </summary>
    [TestMethod]
    public void EnsureIrDataSchema_AllowsLegacyDuplicateRows()
    {
        using var env = TempIrEnvironment.Create();
        using (var songDb = new LR2SongDBExtended(env.SongDbPath))
        {
            songDb.Execute("INSERT INTO ir_data (hash, lr2id) VALUES (?, ?);", "31313131313131313131313131313131", 123);
            songDb.Execute("INSERT INTO ir_data (hash, lr2id) VALUES (?, ?);", "31313131313131313131313131313131", 123);
            BmsLibraryDbGateway.EnsureIrDataSchema(songDb);
        }

        Assert.AreEqual(2, env.CreateGateway().LoadIrData(123).Count(data => data.hash == "31313131313131313131313131313131"));
    }

    /// <summary>
    /// 新XMLの解析で得た順位索引をlocal上位scoreへ再利用し、推定のため同じXMLを二度読み込まない。
    /// </summary>
    /// <remarks>実XMLとDB窓口を通して順位推定を行い、追加XML読取り件数とlocal順位を確認する。</remarks>
    [TestMethod]
    public void RefreshRankingScoresFromCache_UsesReloadedLookupForOfflineEstimate()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService(1);
        string hash = "32323232323232323232323232323232";
        var cacheUpdate = new DateTime(2026, 4, 7, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 300, gr: 0);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        var score = new BMSScore
        {
            hash = hash,
            perfect = 500,
            great = 0
        };

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, [score], estimateOfflineScoreRanking: true);

        Assert.AreEqual(1, result.CacheFilesReloaded);
        Assert.AreEqual(0, result.OfflineEstimateXmlLoadCount);
        Assert.AreEqual(1, score.ranking);
    }

    /// <summary>
    /// DB行再利用時もlocal上位scoreの推定には必要なXML索引だけを一度読み、再解析全体を起動しない。
    /// </summary>
    /// <remarks>実SQLiteの保存行と実XMLを使い、DB再利用時の推定用XML読取り件数とlocal順位を確認する。</remarks>
    [TestMethod]
    public void RefreshRankingScoresFromCache_LoadsCompactLookupOnDemandForDbFallbackOfflineEstimate()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService(1);
        string hash = "35353535353535353535353535353535";
        var cacheUpdate = new DateTime(2026, 4, 7, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 300, gr: 0);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(
        [
            new LR2IRData(hash)
            {
                lr2id = 123,
                clear = ClearType.CLEAR,
                pg = 300,
                gr = 0,
                rank = 5,
                players_num = 1,
                lastupdate = cacheUpdate,
                lastcacheupdate = cacheUpdate
            }
        ]);
        var score = new BMSScore
        {
            hash = hash,
            perfect = 500,
            great = 0
        };

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, [score], estimateOfflineScoreRanking: true);

        Assert.AreEqual(0, result.CacheFilesReloaded);
        Assert.AreEqual(1, result.OfflineEstimateXmlLoadCount);
        Assert.AreEqual(1, score.ranking);
    }

    /// <summary>
    /// 準備後にXMLを削除しても通知抑止下で準備済み順位索引を適用し、local順位を反映し追加読取りを行わない。
    /// </summary>
    /// <remarks>実SQLiteの保存行と実XMLから準備し、XML削除後の適用結果と準備段階の読取り件数を確認する。</remarks>
    [TestMethod]
    public void PreparedRankingRefresh_DoesNotReadFallbackXmlDuringGuardedApply()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService(1);
        string hash = "37373737373737373737373737373737";
        var cacheUpdate = new DateTime(2026, 4, 7, 13, 0, 0);
        env.WriteCacheXml(
            hash,
            cacheUpdate,
            cacheUpdate,
            lr2Id: 123,
            pg: 300,
            gr: 0);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(
        [
            new LR2IRData(hash)
            {
                lr2id = 123,
                clear = ClearType.CLEAR,
                pg = 300,
                gr = 0,
                rank = 5,
                players_num = 1,
                lastupdate = cacheUpdate,
                lastcacheupdate = cacheUpdate
            }
        ]);
        var score = new BMSScore
        {
            hash = hash,
            perfect = 500,
            great = 0
        };

        BmsLibraryIrService.IrCacheRefreshPlan plan =
            service.PrepareRankingScoresRefreshPlanForLibrary(
                123,
                env.ScoreDbPath,
                gateway,
                estimateOfflineScoreRanking: true);
        File.Delete(Path.Combine(env.IrDirectoryPath, hash + ".xml"));
        using (BMSScore.SuppressPropertyChangedScope())
        {
            service.ApplyPreparedRankingScoresRefreshPlanForLibrary(
                plan,
                env.ScoreDbPath,
                [score],
                estimateOfflineScoreRanking: true);
        }

        Assert.AreEqual(1, plan.Result.OfflineEstimateXmlLoadCount);
        Assert.AreEqual(1, score.ranking);
    }

    /// <summary>
    /// offline推定無効ならlocalが高くてもXMLを追加読取せず、参加者数を保持して推定順位を補わない。
    /// </summary>
    /// <remarks>実XMLと実SQLiteの保存時刻を揃え、推定無効時の追加XML読取り件数とlocalの順位・参加者数を確認する。</remarks>
    [TestMethod]
    public void RefreshRankingScoresFromCache_EstimateDisabledAvoidsXmlLoadForHigherLocalScore()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService();
        string hash = "33333333333333333333333333333333";
        var cacheUpdate = new DateTime(2026, 3, 1, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 300, gr: 0);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(
        [
            new LR2IRData(hash)
            {
                lr2id = 123,
                clear = ClearType.CLEAR,
                notes = 1000,
                combo = 500,
                pg = 300,
                gr = 0,
                minbp = 30,
                rank = 20,
                players_num = 80,
                average = 500,
                sigma = 40,
                lastupdate = cacheUpdate,
                lastcacheupdate = cacheUpdate
            }
        ]);
        var score = new BMSScore
        {
            hash = hash,
            perfect = 500,
            great = 0
        };

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, [score], estimateOfflineScoreRanking: false);

        Assert.AreEqual(0, result.CacheFilesReloaded);
        Assert.AreEqual(0, result.OfflineEstimateXmlLoadCount);
        Assert.AreEqual(80, score.rankingNum);
        Assert.AreEqual(0, score.ranking);
    }

    /// <summary>
    /// 初回の正常変更は全score列と対象metadataを保存し、同じ取得XMLの再処理では書込み接続を開かず保存値・時刻を保持して返す。
    /// </summary>
    [DataTestMethod]
    [DoNotParallelize]
    [DataRow("ja-JP")]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    [DataRow("ar-SA")]
    public void UpdateIrScoreTableWithMetrics_SameScoreDigestSkipsReplaceForSameXml(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            using var env = TempIrEnvironment.Create();
            var service = new BmsLibraryIrService();
            int writeConnections = 0;
            var gateway = new BmsLibraryDbGateway(env.SongDbPath, env.ScoreDbPath, path =>
            {
                writeConnections++;
                return new LR2SongDBExtended(path);
            });
            string hash = "44444444444444444444444444444444";
            var client = new FakeIrClient(BuildPlayerScoreXml(hash, pg: 700, gr: 50));

            IrScoreTableUpdateResult first = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);
            Assert.IsTrue(first.Succeeded);
            Assert.AreEqual(1, first.ScoreTable.Count);
            Assert.IsTrue(first.MetadataUpdated);
            List<LR2IRScore> saved = gateway.LoadIrScoreRows();
            Assert.AreEqual(1, saved.Count);
            foreach (LR2IRScore score in first.ScoreTable.Concat(saved))
            {
                Assert.AreEqual(hash, score.hash);
                CollectionAssert.AreEqual(new[] { 4, 1000, 900, 700, 50, 10, 2, 1, 12, 0, 20260505 },
                    new[] { score.clearValue, score.notes, score.combo, score.pg, score.gr, score.gd, score.bd,
                        score.pr, score.minbp, score.option, score.lastupdate });
            }
            LR2SongDBExtended.ir_score_refresh_metadata metadata = gateway.LoadIrScoreRefreshMetadata(123);
            Assert.AreEqual(BmsLibraryIrService.ComputeScoreDigest(first.ScoreTable), metadata.score_digest_sha256);
            Assert.IsTrue(metadata.updated_at > DateTime.MinValue);
            string before = CaptureRawIrDatabase(env.SongDbPath);
            writeConnections = 0;
            IrScoreTableUpdateResult second = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);

            Assert.AreEqual("changed", first.SkipReason);
            Assert.IsFalse(first.Skipped);
            Assert.AreEqual("score_digest_same", second.SkipReason);
            Assert.IsTrue(second.Skipped);
            Assert.AreEqual(1, second.ParsedRows);
            Assert.AreEqual(1, second.LoadedRows);
            Assert.AreEqual(hash, second.ScoreTable[0].hash);
            Assert.IsFalse(second.MetadataUpdated);
            Assert.AreEqual(0, writeConnections);
            Assert.AreEqual(before, CaptureRawIrDatabase(env.SongDbPath));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// 改行だけが増えたXMLは成績digest一致で置換/metadata更新を省き、表面的なXML差分で再保存しない。
    /// </summary>
    [TestMethod]
    public void UpdateIrScoreTableWithMetrics_SameScoreDigestSkipsReplace()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "55555555555555555555555555555555";
        var client = new FakeIrClient(BuildPlayerScoreXml(hash, pg: 600, gr: 25));

        IrScoreTableUpdateResult first = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);
        client.PlayerScoreXml = BuildPlayerScoreXml(hash, pg: 600, gr: 25) + "\n";
        IrScoreTableUpdateResult second = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);

        Assert.AreEqual("changed", first.SkipReason);
        Assert.AreEqual("score_digest_same", second.SkipReason);
        Assert.IsTrue(second.Skipped);
        Assert.AreEqual(1, second.ParsedRows);
        Assert.AreEqual(1, second.LoadedRows);
        Assert.AreEqual(0, second.DbReplaceMs);
        Assert.IsFalse(second.MetadataUpdated);
    }

    /// <summary>
    /// lastupdateだけが変わったXMLは成績一致でDB置換を省き、時刻差だけを成績変更にしない。
    /// </summary>
    [TestMethod]
    public void UpdateIrScoreTableWithMetrics_LastupdateOnlyChangeSkipsReplace()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "66666666666666666666666666666666";
        var client = new FakeIrClient(BuildPlayerScoreXml(hash, pg: 600, gr: 25, lastUpdate: 20260505));

        IrScoreTableUpdateResult first = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);
        client.PlayerScoreXml = BuildPlayerScoreXml(hash, pg: 600, gr: 25, lastUpdate: 20260506);
        IrScoreTableUpdateResult second = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);

        Assert.AreEqual("changed", first.SkipReason);
        Assert.AreEqual("score_digest_same", second.SkipReason);
        Assert.IsTrue(second.Skipped);
        Assert.AreEqual(1, second.ParsedRows);
        Assert.AreEqual(1, second.LoadedRows);
        Assert.AreEqual(0, second.DbReplaceMs);
    }

    /// <summary>
    /// 先行取得の成績面を適用し、別clientを再要求/再解析しない。別取得hashで置き換わる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void UpdateIrScoreTableWithMetrics_UsesPrefetchedScoreSnapshotWithoutFetchingAgain()
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "77777777777777777777777777777777";
        var prefetchClient = new FakeIrClient(BuildPlayerScoreXml(hash, pg: 500, gr: 100));
        IrScorePrefetchResult prefetch = service.PrefetchIrScoreTableWithMetrics(123, prefetchClient, PlayerScoreRegex);
        var fallbackClient = new FakeIrClient(BuildPlayerScoreXml("88888888888888888888888888888888", pg: 1, gr: 1));

        IrScoreTableUpdateResult result = service.UpdateIrScoreTableWithMetrics(123, gateway, fallbackClient, PlayerScoreRegex, prefetch);

        Assert.IsTrue(prefetch.Succeeded);
        Assert.AreEqual(1, prefetchClient.PlayerScoreXmlRequestCount);
        Assert.AreEqual(0, fallbackClient.PlayerScoreXmlRequestCount);
        Assert.IsTrue(result.PrefetchUsed);
        Assert.AreEqual(0, result.XmlFetchMs);
        Assert.AreEqual(0, result.XmlParseMs);
        Assert.AreEqual(0, result.DigestMs);
        Assert.AreEqual(1, result.ParsedRows);
        Assert.AreEqual(hash, result.ScoreTable[0].hash);
    }

    /// <summary>
    /// 実取得の失敗・判別不能応答は再要求せず失敗を返し、成績全列と全IDのmetadata・保存時刻・構造を保持する。
    /// 正常0件との判別条件はここへ集約し、起動試験は非採用・独立cache・次要求の接続を分担する。
    /// </summary>
    [DataTestMethod]
    [DataRow("fetch_failure")]
    [DataRow("null_body")]
    [DataRow("empty_body")]
    [DataRow("unknown_root")]
    [DataRow("html_error")]
    [DataRow("wrapped_empty_list")]
    [DataRow("malformed_declaration")]
    [DataRow("unreadable_rows")]
    [DataRow("truncated_row")]
    public void UpdateIrScoreTableWithMetrics_FailedPrefetchPreservesNonemptyDatabaseWithoutRetry(string responseKind)
    {
        using var env = TempIrEnvironment.Create();
        var service = new BmsLibraryIrService();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        const string hash = "12345678901234567890123456789012";
        gateway.ReplaceIrScoreTableAndRefreshMetadata([new LR2IRScore(hash)
        {
            clearValue = 4, notes = 1000, combo = 900, pg = 321, gr = 45,
            gd = 10, bd = 2, pr = 1, minbp = 12, option = 32, lastupdate = 20261010
        }], 123, "retained-digest");
        gateway.UpsertIrScoreRefreshMetadata(456, "other-player-digest");
        string before = CaptureRawIrDatabase(env.SongDbPath);
        string? xml = responseKind switch
        {
            "fetch_failure" => null,
            "null_body" => null,
            "empty_body" => "",
            "unknown_root" => "<root></root>",
            "html_error" => "<html><body>Service unavailable</body></html>",
            "wrapped_empty_list" => "<unknown><scorelist/></unknown>",
            "malformed_declaration" => "<?xml garbage?><scorelist/>",
            "unreadable_rows" => BuildPlayerScoreXml(hash, pg: 1, gr: 1).Replace("<pg>1</pg>", "<pg>2147483648</pg>"),
            "truncated_row" => "<scorelist>\n\t<score>\n\t\t<hash>" + hash + "</hash>\n\t\t<clear>4</clear>",
            _ => throw new AssertFailedException("未定義の応答条件です。")
        };
        var client = new FakeIrClient(xml);
        if (responseKind == "fetch_failure")
        {
            client.FetchFailure = new IOException("controlled unavailable IR");
        }
        IrScorePrefetchResult prefetch = service.PrefetchIrScoreTableWithMetrics(123, client, PlayerScoreRegex);

        IrScoreTableUpdateResult result = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex, prefetch);

        Assert.AreEqual(1, client.PlayerScoreXmlRequestCount, "失敗済みの同じ要求を再取得しない。");
        Assert.IsFalse(prefetch.Succeeded);
        Assert.AreEqual(responseKind == "fetch_failure" ? "fetch_failed" : "parse_failed", prefetch.FailureReason);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(IrScoreFailure.Unavailable, result.Failure);
        Assert.IsTrue(result.PrefetchUsed);
        Assert.IsNull(result.ScoreTable);
        Assert.IsFalse(result.MetadataUpdated);
        Assert.AreEqual(before, CaptureRawIrDatabase(env.SongDbPath));
    }

    /// <summary>
    /// 取得取消はcaller token取消時だけCancelled、未取消ならTimedOutへ分類し、終了取消と期限失敗を混同しない。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrefetchIrScoreTable_ClassifiesDeadlineAndShutdownSeparately(bool shutdown)
    {
        using var cancellation = new CancellationTokenSource();
        var client = new FakeIrClient("") { FetchFailure = new OperationCanceledException() };
        if (shutdown)
        {
            cancellation.Cancel();
        }

        var service = new BmsLibraryIrService();
        IrScorePrefetchResult result = service.PrefetchIrScoreTableWithMetrics(123, client, PlayerScoreRegex, cancellation.Token);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(shutdown ? IrScoreFailure.Cancelled : IrScoreFailure.TimedOut, result.Failure);
    }

    /// <summary>
    /// 既知の空scorelist応答は実取得から採用し、空DBの未記録metadataを保存でき、旧成績があれば空へ置換して対象IDの履歴だけ更新する。
    /// 自己終了・内部空白・BOM/#/XML宣言/rivalnameの代表を分担し、外枠の違いを取得失敗と混同しない。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, "<scorelist/>")]
    [DataRow(true, "<scorelist> \r\n\t</scorelist>")]
    [DataRow(true, "\uFEFF \n#\n<?xml version=\"1.0\" encoding=\"Shift_JIS\"?>\n<scorelist/>\n<rivalname>Player</rivalname>\n\uFEFF")]
    public void UpdateIrScoreTableWithMetrics_RecognizedEmptyResponseCommitsEmptyScores(bool existingScores, string xml)
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        Assert.IsNull(gateway.LoadIrScoreRefreshMetadata(123));
        if (existingScores)
        {
            gateway.ReplaceIrScoreTableAndRefreshMetadata(
                [new LR2IRScore { hash = "12345678901234567890123456789012", pg = 321, gr = 45 }], 123, "retained-digest");
        }
        gateway.UpsertIrScoreRefreshMetadata(456, "other-player-digest");
        LR2SongDBExtended.ir_score_refresh_metadata otherMetadata = gateway.LoadIrScoreRefreshMetadata(456);
        var client = new FakeIrClient(xml);

        IrScoreTableUpdateResult result = new BmsLibraryIrService()
            .UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);

        Assert.AreEqual(1, client.PlayerScoreXmlRequestCount);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(IrScoreFailure.None, result.Failure);
        Assert.AreEqual(0, result.ScoreTable.Count);
        Assert.AreEqual(0, gateway.LoadIrScoreRows().Count);
        Assert.IsTrue(result.MetadataUpdated);
        Assert.AreEqual(existingScores ? "changed" : "score_digest_same", result.SkipReason);
        Assert.AreEqual(BmsLibraryIrService.ComputeScoreDigest([]), gateway.LoadIrScoreRefreshMetadata(123).score_digest_sha256);
        Assert.AreEqual(otherMetadata.score_digest_sha256, gateway.LoadIrScoreRefreshMetadata(456).score_digest_sha256);
        Assert.AreEqual(otherMetadata.updated_at, gateway.LoadIrScoreRefreshMetadata(456).updated_at);
    }

    /// <summary>metadata表の読取りが不成立なら異なる取得成績も採用せず、旧スコア・退避metadata・構造を保持する。</summary>
    [TestMethod]
    public void CommitPreparedIrScoreTable_MetadataReadFailurePreservesDatabase()
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        const string hash = "12345678901234567890123456789012";
        gateway.ReplaceIrScoreTableAndRefreshMetadata([new LR2IRScore { hash = hash, pg = 321, gr = 45 }], 123, "retained-digest");
        var service = new BmsLibraryIrService();
        IrScorePrefetchResult fetched = service.PrefetchIrScoreTableWithMetrics(
            123, new FakeIrClient(BuildPlayerScoreXml(hash, pg: 999, gr: 1)), PlayerScoreRegex);
        Assert.IsTrue(fetched.Succeeded);
        using (var db = new SQLiteConnection(env.SongDbPath))
        {
            db.Execute("ALTER TABLE ir_score_refresh_metadata RENAME TO retained_ir_score_refresh_metadata");
        }
        bool readFailed = false;
        try { gateway.LoadIrScoreRefreshMetadata(123); }
        catch (Exception) { readFailed = true; }
        Assert.IsTrue(readFailed, "準備した故障が実metadata読取りで観測されません。");
        string before = CaptureRawIrDatabase(env.SongDbPath, "retained_ir_score_refresh_metadata");

        IrScoreTableUpdateResult result = service.CommitPreparedIrScoreTableWithMetrics(123, gateway, fetched);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(IrScoreFailure.Unavailable, result.Failure);
        Assert.IsNull(result.ScoreTable);
        Assert.IsFalse(result.MetadataUpdated);
        Assert.AreEqual(before, CaptureRawIrDatabase(env.SongDbPath, "retained_ir_score_refresh_metadata"));
    }

    /// <summary>
    /// 保存hashの実体化不能は更新失敗で全保存列とmetadata・構造を保持する。
    /// 未記録metadata＋正常空取得と、metadata digest一致＋正常非空取得の両経路で空成功への変換を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CommitPreparedIrScoreTable_SavedScoreReadFailurePreservesDatabase(bool matchingMetadata)
    {
        using var env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        var service = new BmsLibraryIrService();
        string xml = matchingMetadata
            ? BuildPlayerScoreXml("12345678901234567890123456789012", pg: 999, gr: 1)
            : "<scorelist/>";
        IrScorePrefetchResult fetched = service.PrefetchIrScoreTableWithMetrics(123, new FakeIrClient(xml), PlayerScoreRegex);
        Assert.IsTrue(fetched.Succeeded);
        if (matchingMetadata)
        {
            gateway.UpsertIrScoreRefreshMetadata(123, fetched.ScoreDigestSha256);
        }
        using (var db = new SQLiteConnection(env.SongDbPath))
        {
            db.Execute("INSERT INTO ir_score (hash, clear, notes, combo, pg, gr, gd, bd, pr, minbp, option, lastupdate) VALUES ('unmaterializable-hash', 4, 1000, 900, 321, 45, 10, 2, 1, 12, 0, 20261010)");
        }
        bool readFailed = false;
        try { gateway.LoadIrScoreRows(); }
        catch (Exception) { readFailed = true; }
        Assert.IsTrue(readFailed, "準備した故障が実スコア読取りで観測されません。");
        string before = CaptureRawIrDatabase(env.SongDbPath);

        IrScoreTableUpdateResult result = service.CommitPreparedIrScoreTableWithMetrics(123, gateway, fetched);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(IrScoreFailure.Unavailable, result.Failure);
        Assert.AreEqual("db_read_failed", result.SkipReason);
        Assert.IsNull(result.ScoreTable);
        Assert.IsFalse(result.MetadataUpdated);
        Assert.AreEqual(before, CaptureRawIrDatabase(env.SongDbPath));
    }

    private static string CaptureRawRankingDatabase(string path)
    {
        using var db = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);
        return db.ExecuteScalar<string>("SELECT group_concat(value, ';') FROM (SELECT quote(hash)||','||quote(rank)||','||quote(players_num)||','||quote(lr2id)||','||quote(clear)||','||quote(notes)||','||quote(combo)||','||quote(pg)||','||quote(gr)||','||quote(minbp)||','||quote(average)||','||quote(sigma)||','||quote(lastupdate)||','||quote(lastcacheupdate) AS value FROM ir_data ORDER BY lr2id, hash)");
    }

    private static string CaptureRawIrDatabase(string path, string metadataTable = "ir_score_refresh_metadata")
    {
        using var db = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);
        string scores = db.ExecuteScalar<string>("SELECT group_concat(value, ';') FROM (SELECT quote(hash)||','||quote(clear)||','||quote(notes)||','||quote(combo)||','||quote(pg)||','||quote(gr)||','||quote(gd)||','||quote(bd)||','||quote(pr)||','||quote(minbp)||','||quote(option)||','||quote(lastupdate) AS value FROM ir_score ORDER BY hash)");
        string metadata = db.ExecuteScalar<string>("SELECT group_concat(value, ';') FROM (SELECT quote(lr2id)||','||quote(score_digest_sha256)||','||quote(updated_at) AS value FROM " + metadataTable + " ORDER BY lr2id)");
        string schema = db.ExecuteScalar<string>("SELECT group_concat(value, ';') FROM (SELECT quote(type)||','||quote(name)||','||quote(tbl_name)||','||quote(sql) AS value FROM sqlite_master ORDER BY type, name)");
        return scores + "\n" + metadata + "\n" + schema;
    }
    private static ChartFile CreateFile(string hash)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = hash };
        file = file with { Path = hash + ".bms" };
        return file;
    }

    private static BMSScore CreateScore(string hash, ClearType clear, int pg, int gr, int minbp)
    {
        return new BMSScore
        {
            hash = hash,
            clear = clear,
            perfect = pg,
            great = gr,
            totalnotes = 1000,
            maxcombo = 900,
            minbp = minbp
        };
    }

    private static LR2IRScore CreateIrScore(string hash, ClearType clear, int pg, int gr, int minbp)
    {
        return new LR2IRScore(hash)
        {
            clear = clear,
            notes = 1000,
            combo = 900,
            pg = pg,
            gr = gr,
            minbp = minbp
        };
    }

    private static void CreateLr2ScoreDb(string scoreDbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(scoreDbPath)!);
        using var connection = new SQLiteConnection(scoreDbPath);
        connection.RunInTransaction(() =>
        {
            connection.CreateTable<BMSScore>();
            connection.CreateTable<LR2ScoreDB.player>();
            connection.Insert(new BMSScore
            {
                hash = "12121212121212121212121212121212",
                clear = ClearType.HARD,
                perfect = 300,
                great = 50,
                totalnotes = 400,
                maxcombo = 350,
                minbp = 20
            });
            connection.Insert(new LR2ScoreDB.player
            {
                id = "player",
                irid = 123
            });
        });
    }

    private static void CreateBeatorajaScoreDb(string scoreDbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(scoreDbPath)!);
        using var connection = new SQLiteConnection(scoreDbPath);
        connection.RunInTransaction(() =>
        {
            connection.Execute(
                "CREATE TABLE score (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER, PRIMARY KEY(sha256, mode));");
            connection.Execute(
                "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, 2, 100, 20, 30, 10, 200, 180, 5, 7, 3);
        });
    }

    private static readonly Regex PlayerScoreRegex = new("\\t<score>\\r?\\n\\t\\t<hash>([a-f0-9]+)</hash>\\r?\\n\\t\\t<clear>(\\d+)</clear>\\r?\\n\\t\\t<notes>(\\d+)</notes>\\r?\\n\\t\\t<combo>(\\d+)</combo>\\r?\\n\\t\\t<pg>(\\d+)</pg>\\r?\\n\\t\\t<gr>(\\d+)</gr>\\r?\\n\\t\\t<gd>(\\d+)</gd>\\r?\\n\\t\\t<bd>(\\d+)</bd>\\r?\\n\\t\\t<pr>(\\d+)</pr>\\r?\\n\\t\\t<minbp>(\\d+)</minbp>\\r?\\n\\t\\t<option>(\\d+)</option>\\r?\\n\\t\\t<lastupdate>(\\d+)</lastupdate>\\r?\\n\\t</score>\\r?\\n", RegexOptions.Compiled);

    private static string BuildPlayerScoreXml(string hash, int pg, int gr, int lastUpdate = 20260505, int clear = 4, int option = 0)
    {
        return "<root>\n"
            + "\t<score>\n"
            + "\t\t<hash>" + hash + "</hash>\n"
            + "\t\t<clear>" + clear + "</clear>\n"
            + "\t\t<notes>1000</notes>\n"
            + "\t\t<combo>900</combo>\n"
            + "\t\t<pg>" + pg + "</pg>\n"
            + "\t\t<gr>" + gr + "</gr>\n"
            + "\t\t<gd>10</gd>\n"
            + "\t\t<bd>2</bd>\n"
            + "\t\t<pr>1</pr>\n"
            + "\t\t<minbp>12</minbp>\n"
            + "\t\t<option>" + option + "</option>\n"
            + "\t\t<lastupdate>" + lastUpdate + "</lastupdate>\n"
            + "\t</score>\n"
            + "</root>\n";
    }

    private static string BuildRankingCacheXml(DateTime? lastUpdate, IEnumerable<RankingScoreRow> scores, bool appendNul = false)
    {
        var builder = new StringBuilder();
        builder.Append("<root>\n");
        builder.Append("<ranking>\n");
        foreach (RankingScoreRow score in scores)
        {
            builder.Append("\t\t<score>\n");
            builder.Append("\t\t<id>").Append(score.Lr2Id).Append("</id>\n");
            builder.Append("\t\t<clear>").Append(score.Clear).Append("</clear>\n");
            builder.Append("\t\t<notes>").Append(score.Notes).Append("</notes>\n");
            builder.Append("\t\t<combo>").Append(score.Combo).Append("</combo>\n");
            builder.Append("\t\t<pg>").Append(score.Pg).Append("</pg>\n");
            builder.Append("\t\t<gr>").Append(score.Gr).Append("</gr>\n");
            builder.Append("\t\t<minbp>").Append(score.MinBp).Append("</minbp>\n");
            builder.Append("\t\t</score>\n");
        }
        builder.Append("</ranking>\n");
        builder.Append("<lastupdate>");
        if (lastUpdate.HasValue)
        {
            builder.Append(lastUpdate.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        builder.Append("</lastupdate>\n");
        if (appendNul)
        {
            builder.Append('\0');
        }
        return builder.ToString();
    }

    private static void AssertIrDataEquivalent(LR2IRData expected, LR2IRData actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.hash, actual.hash);
        Assert.AreEqual(expected.lr2id, actual.lr2id);
        Assert.AreEqual(expected.rank, actual.rank);
        Assert.AreEqual(expected.players_num, actual.players_num);
        Assert.AreEqual(expected.average, actual.average, 0.0000001);
        Assert.AreEqual(expected.sigma, actual.sigma, 0.0000001);
        Assert.AreEqual(expected.clear, actual.clear);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.combo, actual.combo);
        Assert.AreEqual(expected.pg, actual.pg);
        Assert.AreEqual(expected.gr, actual.gr);
        Assert.AreEqual(expected.minbp, actual.minbp);
        Assert.AreEqual(expected.lastupdate, actual.lastupdate);
        Assert.AreEqual(expected.lastcacheupdate, actual.lastcacheupdate);
    }

    private sealed class RankingScoreRow
    {
        public int Lr2Id { get; set; }

        public int Clear { get; set; }

        public int Notes { get; set; }

        public int Combo { get; set; }

        public int Pg { get; set; }

        public int Gr { get; set; }

        public int MinBp { get; set; }
    }

    private sealed class FakeIrClient(string? playerScoreXml) : IBmsLibraryIrClient
    {
        public string? PlayerScoreXml { get; set; } = playerScoreXml;

        public int PlayerScoreXmlRequestCount { get; private set; }

        public Exception? FetchFailure { get; set; }

        public string? GetPlayerScoresXml(int lr2Id, CancellationToken cancellationToken = default)
        {
            PlayerScoreXmlRequestCount++;
            if (FetchFailure != null)
            {
                throw FetchFailure;
            }

            return PlayerScoreXml;
        }


    }


    private sealed class TempIrEnvironment : IDisposable
    {
        private TempIrEnvironment(string rootDirectoryPath)
        {
            RootDirectoryPath = rootDirectoryPath;
            SongDbPath = Path.Combine(rootDirectoryPath, "song.db");
            ScoreDbPath = Path.Combine(rootDirectoryPath, "LR2files", "Database", "score.db");
            IrDirectoryPath = Path.Combine(rootDirectoryPath, "Ir");
            Directory.CreateDirectory(Path.GetDirectoryName(ScoreDbPath)!);
            Directory.CreateDirectory(IrDirectoryPath);
            using var songDb = new LR2SongDBExtended(SongDbPath);
            songDb.RunInTransaction(() =>
            {
                BmsLibraryDbGateway.EnsureIrDataSchema(songDb);
                songDb.CreateTable<LR2SongDBExtended.ir_score>();
                songDb.CreateTable<LR2SongDBExtended.ir_score_refresh_metadata>();
            });
        }

        public string RootDirectoryPath { get; }

        public string SongDbPath { get; }

        public string ScoreDbPath { get; }

        public string IrDirectoryPath { get; }

        public static TempIrEnvironment Create()
        {
            string rootDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerIrTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDirectoryPath);
            return new TempIrEnvironment(rootDirectoryPath);
        }

        public BmsLibraryDbGateway CreateGateway()
        {
            return new BmsLibraryDbGateway(SongDbPath, ScoreDbPath);
        }

        public void WriteCacheXml(string hash, DateTime fileWriteTime, DateTime lastUpdate, int lr2Id = 123, int pg = 450, int gr = 50)
        {
            string xml = BuildRankingCacheXml(lastUpdate,
            [
                new RankingScoreRow
                {
                    Lr2Id = lr2Id,
                    Clear = 4,
                    Notes = 1000,
                    Combo = 900,
                    Pg = pg,
                    Gr = gr,
                    MinBp = 12
                }
            ]);
            string path = Path.Combine(IrDirectoryPath, hash + ".xml");
            File.WriteAllText(path, xml, Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTime(path, fileWriteTime);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectoryPath))
            {
                Directory.Delete(RootDirectoryPath, recursive: true);
            }
        }
    }
}
