using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using Ribbit.Logging;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Applies IR cache/network data to snapshots owned by BMSLibrary.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryIrService
{
    private readonly int? rankingCacheXmlReloadDegreeOverride;

    internal sealed class IrCacheRefreshPlan
    {
        public List<LR2IRData> DbRows { get; } = [];

        public List<LR2IRData> XmlRows { get; } = [];

        internal List<LR2IRData> UpsertRows { get; } = [];

        public Dictionary<string, Lr2IrRankingLookup> XmlLookupsByHash { get; } = new Dictionary<string, Lr2IrRankingLookup>(StringComparer.OrdinalIgnoreCase);

        public IrCacheRefreshResult Result { get; } = new IrCacheRefreshResult();
    }

    internal sealed class RankingCacheReloadResult
    {
        public LR2IRData IrData { get; set; }

        public Lr2IrRankingLookup Lookup { get; set; }

        public int ScoresParsed { get; set; }

        public bool Failed { get; set; }
    }

    public BmsLibraryIrService()
        : this(null)
    {
    }

    internal BmsLibraryIrService(int? rankingCacheXmlReloadDegreeOverride)
    {
        this.rankingCacheXmlReloadDegreeOverride = rankingCacheXmlReloadDegreeOverride;
    }

    internal static BMSScore CloneScoreForFileHash(BMSScore source, string fileHash)
    {
        if (source == null)
        {
            return null;
        }

        return new BMSScore
        {
            hash = fileHash,
            clear = source.clear,
            perfect = source.perfect,
            great = source.great,
            good = source.good,
            bad = source.bad,
            poor = source.poor,
            totalnotes = source.totalnotes,
            maxcombo = source.maxcombo,
            minbp = source.minbp,
            playcount = source.playcount,
            clearcount = source.clearcount,
            failcount = source.failcount,
            rank = source.rank,
            rate = source.rate,
            clear_db = source.clear_db,
            op_history = source.op_history,
            scorehash = source.scorehash,
            ghost = source.ghost,
            clear_sd = source.clear_sd,
            clear_ex = source.clear_ex,
            op_best = source.op_best,
            rseed = source.rseed,
            complete = source.complete,
            ranking = source.ranking,
            rankingNum = source.rankingNum,
            rankingLastupdate = source.rankingLastupdate,
            stddevVal = source.stddevVal,
            scoreDifficulty = source.scoreDifficulty
        };
    }

    public LR2IRCache LoadIrCache(string filePath)
    {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        string rankingXml = File.ReadAllText(filePath, Encoding.GetEncoding("shift_jis"));
        DateTime lastWriteTime = File.GetLastWriteTime(filePath);
        return new LR2IRCache(rankingXml, fileNameWithoutExtension, lastWriteTime);
    }

    /// <summary>IRの取得値をスコア正本へ適用します。譜面への状態付着は行いません。</summary>
    public void ApplyIrDataToScores(LR2IRData data, LR2IRCache cache, string scoreDbPath, List<BMSScore> bmsScores, bool estimateOfflineScoreRanking)
    {
        ApplyIrDataToScores(data, cache?.Lookup, scoreDbPath, bmsScores, estimateOfflineScoreRanking);
    }

    private void ApplyIrDataToScores(LR2IRData data, Lr2IrRankingLookup lookup, string scoreDbPath, List<BMSScore> bmsScores, bool estimateOfflineScoreRanking)
    {
        if (data == null || bmsScores == null)
        {
            return;
        }
        Dictionary<string, BMSScore> scoresByHash = BuildScoreIndex(bmsScores);

        int offlineEstimateXmlLoadCount = 0;
        ApplyIrDataToScoresIndexed(data, lookup, scoreDbPath, bmsScores, scoresByHash, estimateOfflineScoreRanking, ref offlineEstimateXmlLoadCount);
    }

    private static Dictionary<string, BMSScore> BuildScoreIndex(IEnumerable<BMSScore> bmsScores)
    {
        var scoresByHash = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSScore score in bmsScores ?? [])
        {
            if (score != null && !string.IsNullOrWhiteSpace(score.hash) && !scoresByHash.ContainsKey(score.hash))
            {
                scoresByHash.Add(score.hash, score);
            }
        }
        return scoresByHash;
    }

    private BMSScore ApplyIrDataToScoresIndexed(
        LR2IRData data,
        Lr2IrRankingLookup lookup,
        string scoreDbPath,
        List<BMSScore> bmsScores,
        Dictionary<string, BMSScore> scoresByHash,
        bool estimateOfflineScoreRanking,
        ref int offlineEstimateXmlLoadCount,
        bool allowLookupFileLoad = true)
    {
        if (data == null || bmsScores == null || string.IsNullOrWhiteSpace(data.hash))
        {
            return null;
        }
        string cachePath = string.IsNullOrWhiteSpace(scoreDbPath)
            ? null
            : Path.Combine(Path.Combine(Path.GetDirectoryName(scoreDbPath), "..\\..\\Ir"), data.hash + ".xml");
        scoresByHash.TryGetValue(data.hash, out BMSScore score);
        if (data.score > 0)
        {
            if (score != null)
            {
                if (score.clear < data.clear)
                {
                    score.clear = data.clear;
                    score.op_history = ClearTypeStorageConverter.GetLr2IrDataOptionHistory(data.clear);
                }
                if (score.score < data.score)
                {
                    score.ranking = data.rank;
                    score.rankingNum = data.players_num;
                    score.rankingLastupdate = data.lastupdate;
                    score.perfect = data.pg;
                    score.great = data.gr;
                    score.totalnotes = data.notes;
                    score.maxcombo = data.combo;
                    score.minbp = data.minbp;
                    score.rate = ScoreValueCalculator.CalculateRatePercent(score.score, score.totalnotes) ?? 0;
                    score.rank = ScoreValueCalculator.CalculateRank(score.score, score.totalnotes);
                }
                else if (score.score == data.score)
                {
                    score.ranking = data.rank;
                    score.rankingNum = data.players_num;
                    score.rankingLastupdate = data.lastupdate;
                }
                else
                {
                    if (estimateOfflineScoreRanking && (lookup != null || allowLookupFileLoad))
                    {
                        lookup = EnsureRankingLookupLoaded(lookup, cachePath, ref offlineEstimateXmlLoadCount);
                        if (lookup != null)
                        {
                            score.ranking = lookup.GetRankFromScore(score.score);
                        }
                    }
                    score.rankingNum = data.players_num;
                    score.rankingLastupdate = data.lastupdate;
                }
                score.SetStdDevVal(data.average, data.sigma);
                score.SetScoreDiffic(data.average, data.sigma);
                return score;
            }
            score = new BMSScore(data);
            bmsScores.Add(score);
            scoresByHash[data.hash] = score;

            return score;
        }
        if (score != null)
        {
            if (estimateOfflineScoreRanking && (lookup != null || allowLookupFileLoad))
            {
                lookup = EnsureRankingLookupLoaded(lookup, cachePath, ref offlineEstimateXmlLoadCount);
                if (lookup != null)
                {
                    score.ranking = lookup.GetRankFromScore(score.score);
                }
            }
            score.rankingNum = data.players_num;
            score.rankingLastupdate = data.lastupdate;
            score.SetStdDevVal(data.average, data.sigma);
            score.SetScoreDiffic(data.average, data.sigma);
            return score;
        }
        score = new BMSScore(data);
        bmsScores.Add(score);
        scoresByHash[data.hash] = score;

        return score;
    }

    public List<LR2IRScore> UpdateIrScoreTable(int lr2Id, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient, Regex lr2IrScoreRegex)
    {
        return UpdateIrScoreTableWithMetrics(lr2Id, dbGateway, irClient, lr2IrScoreRegex).ScoreTable;
    }

    /// <summary>終了キャンセルと通信期限を保ったまま、一度だけ取得・解析して後続へ渡します。</summary>
    public IrScorePrefetchResult PrefetchIrScoreTableWithMetrics(int lr2Id, IBmsLibraryIrClient irClient, Regex lr2IrScoreRegex, CancellationToken cancellationToken = default)
    {
        var result = new IrScorePrefetchResult
        {
            Lr2Id = lr2Id
        };
        if (irClient == null || lr2IrScoreRegex == null || lr2Id == 0)
        {
            result.Failure = IrScoreFailure.Unavailable;
            result.FailureReason = "unavailable";
            return result;
        }
        string playerXml;
        try
        {
            var fetchStopwatch = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            playerXml = irClient.GetPlayerScoresXml(lr2Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            fetchStopwatch.Stop();
            result.XmlFetchMs = fetchStopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException ex)
        {
            result.Failure = cancellationToken.IsCancellationRequested ? IrScoreFailure.Cancelled : IrScoreFailure.TimedOut;
            result.FailureReason = result.Failure == IrScoreFailure.Cancelled ? "cancelled" : "timed_out";
            NLogWrapper.FileLogger?.Warn(ex, "IR score fetch ended lr2Id=" + lr2Id + " status=" + result.FailureReason);
            return result;
        }
        catch (Exception ex)
        {
            result.Failure = IrScoreFailure.Unavailable;
            NLogWrapper.FileLogger?.Warn(ex, "IR score fetch failed lr2Id=" + lr2Id);
            result.FailureReason = "fetch_failed";
            return result;
        }
        try
        {
            var parseStopwatch = Stopwatch.StartNew();
            result.ScoreTable.AddRange(ParsePlayerScoreXml(playerXml, lr2IrScoreRegex));
            parseStopwatch.Stop();
            result.XmlParseMs = parseStopwatch.ElapsedMilliseconds;
            result.ParsedRows = result.ScoreTable.Count;
        }
        catch (Exception ex)
        {
            result.Failure = IrScoreFailure.Unavailable;
            result.FailureReason = "parse_failed";
            NLogWrapper.FileLogger?.Warn(ex, "IR score parse failed lr2Id=" + lr2Id);
            return result;
        }
        try
        {
            var digestStopwatch = Stopwatch.StartNew();
            result.ScoreDigestSha256 = ComputeScoreDigest(result.ScoreTable);
            digestStopwatch.Stop();
            result.DigestMs = digestStopwatch.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            result.Failure = IrScoreFailure.Unavailable;
            result.FailureReason = "digest_failed";
            NLogWrapper.FileLogger?.Warn(ex, "IR score digest failed lr2Id=" + lr2Id);
        }
        return result;
    }

    /// <summary>通信・解析の結果を局所で渡し、同じ確定手順へ接続する公開入口です。</summary>
    public IrScoreTableUpdateResult UpdateIrScoreTableWithMetrics(int lr2Id, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient,
        Regex lr2IrScoreRegex, IrScorePrefetchResult prefetchedScore = null, CancellationToken cancellationToken = default)
    {
        IrScorePrefetchResult fetched = prefetchedScore?.Lr2Id == lr2Id ? prefetchedScore
            : PrefetchIrScoreTableWithMetrics(lr2Id, irClient, lr2IrScoreRegex, cancellationToken);
        IrScoreTableUpdateResult result = CommitPreparedIrScoreTableWithMetrics(lr2Id, dbGateway, fetched, cancellationToken);
        if (ReferenceEquals(fetched, prefetchedScore))
        {
            result.PrefetchUsed = true;
            result.XmlFetchMs = 0L;
            result.XmlParseMs = 0L;
            result.DigestMs = 0L;
        }
        return result;
    }

    /// <summary>
    /// 通信済み結果の変更成績と対象IDのmetadataを正本DBへ一体保存します。
    /// metadataが一致すれば無保存で既存成績を返し、未記録・不一致なら既存の実成績比較で必要な保存を選びます。
    /// 読取り・保存の通常失敗は適用不能な結果を返し、直接捕捉したメモリ不足は即時終了します。
    /// モデル側はL取得後に停止とscore contextを確認して呼びます。
    /// </summary>
    internal IrScoreTableUpdateResult CommitPreparedIrScoreTableWithMetrics(int lr2Id, BmsLibraryDbGateway dbGateway,
        IrScorePrefetchResult fetched, CancellationToken cancellationToken = default)
    {
        var result = new IrScoreTableUpdateResult();
        if (dbGateway == null || fetched == null || lr2Id == 0 || string.IsNullOrWhiteSpace(dbGateway.ScoreDbPath))
        {
            result.SkipReason = "unavailable";
            return result;
        }
        result.XmlFetchMs = fetched.XmlFetchMs;
        result.XmlParseMs = fetched.XmlParseMs;
        result.DigestMs = fetched.DigestMs;
        if (cancellationToken.IsCancellationRequested || !fetched.Succeeded)
        {
            result.Failure = cancellationToken.IsCancellationRequested ? IrScoreFailure.Cancelled
                : fetched.Failure == IrScoreFailure.None ? IrScoreFailure.Unavailable : fetched.Failure;
            result.SkipReason = cancellationToken.IsCancellationRequested ? "cancelled" : fetched.FailureReason;
            return result;
        }
        List<LR2IRScore> scoreTable = [.. fetched.ScoreTable];
        string scoreDigest = fetched.ScoreDigestSha256;
        result.ParsedRows = scoreTable.Count;
        LR2SongDBExtended.ir_score_refresh_metadata metadata;
        List<LR2IRScore> existingScoreTable;
        bool metadataDigestMatches;
        bool scoreDigestMatches;
        try
        {
            metadata = dbGateway.LoadIrScoreRefreshMetadata(lr2Id, out long metadataDbLockWaitMs);
            result.DbLockWaitMs += metadataDbLockWaitMs;
            var loadStopwatch = Stopwatch.StartNew();
            IrScoreRowsLoadResult loadResult = dbGateway.LoadIrScoreRowsWithMetrics();
            loadStopwatch.Stop();
            existingScoreTable = loadResult.Rows;
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
            result.DbLockWaitMs += loadResult.DbLockWaitMs;
            result.LoadedRows = existingScoreTable.Count;
            metadataDigestMatches = metadata != null
                && string.Equals(metadata.score_digest_sha256, scoreDigest, StringComparison.OrdinalIgnoreCase);
            scoreDigestMatches = metadataDigestMatches
                || string.Equals(ComputeScoreDigest(existingScoreTable), scoreDigest, StringComparison.OrdinalIgnoreCase);
        }
        catch (OutOfMemoryException ex)
        {
            Environment.FailFast("本人IRの保存状態読取り中にメモリが不足しました。", ex);
            throw;
        }
        catch (Exception ex)
        {
            result.Failure = IrScoreFailure.Unavailable;
            result.SkipReason = "db_read_failed";
            WarnIrScoreDatabaseFailure(ex, lr2Id, "read");
            return result;
        }
        try
        {
            if (!scoreDigestMatches)
            {
                var dbStopwatch = Stopwatch.StartNew();
                dbGateway.ReplaceIrScoreTableAndRefreshMetadata(scoreTable, lr2Id, scoreDigest);
                dbStopwatch.Stop();
                result.DbReplaceMs = dbStopwatch.ElapsedMilliseconds;
                result.MetadataUpdated = true;
            }
            else if (!metadataDigestMatches)
            {
                dbGateway.UpsertIrScoreRefreshMetadata(lr2Id, scoreDigest);
                result.MetadataUpdated = true;
            }
        }
        catch (OutOfMemoryException ex)
        {
            Environment.FailFast("本人IRの保存中にメモリが不足しました。", ex);
            throw;
        }
        catch (Exception ex)
        {
            result.Failure = IrScoreFailure.Unavailable;
            result.SkipReason = "db_save_failed";
            WarnIrScoreDatabaseFailure(ex, lr2Id, "save");
            return result;
        }
        result.ScoreTable = scoreDigestMatches ? existingScoreTable : scoreTable;
        result.Skipped = scoreDigestMatches;
        result.SkipReason = scoreDigestMatches ? "score_digest_same" : "changed";
        return result;
    }

    private static void WarnIrScoreDatabaseFailure(Exception exception, int lr2Id, string phase)
    {
        try
        {
            NLogWrapper.FileLogger?.Warn(exception, "IR score database " + phase + " failed lr2Id=" + lr2Id);
        }
        catch (OutOfMemoryException ex)
        {
            Environment.FailFast("本人IRのDB失敗診断中にメモリが不足しました。", ex);
            throw;
        }
        catch (Exception)
        {
            // 更新停止は確定済みです。診断先の通常失敗で結果を変えません。
        }
    }

    /// <summary>従来の有効行抽出を保ち、0件の応答だけは既知の空一覧と確認できる場合に限り採用します。</summary>
    private static List<LR2IRScore> ParsePlayerScoreXml(string playerXml, Regex lr2IrScoreRegex)
    {
        List<LR2IRScore> scores = [.. (from e in lr2IrScoreRegex.Matches(playerXml ?? string.Empty).Cast<Match>().Select(delegate (Match m)
                {
                    try
                    {
                        return new LR2IRScore(m.Groups[1].Value)
                        {
                            clearValue = int.Parse(m.Groups[2].Value),
                            notes = int.Parse(m.Groups[3].Value),
                            combo = int.Parse(m.Groups[4].Value),
                            pg = int.Parse(m.Groups[5].Value),
                            gr = int.Parse(m.Groups[6].Value),
                            gd = int.Parse(m.Groups[7].Value),
                            bd = int.Parse(m.Groups[8].Value),
                            pr = int.Parse(m.Groups[9].Value),
                            minbp = int.Parse(m.Groups[10].Value),
                            option = int.Parse(m.Groups[11].Value),
                            lastupdate = int.Parse(m.Groups[12].Value)
                        };
                    }
                    catch
                    {
                        return (LR2IRScore)null;
                    }
                })
                where e != null
                group e by e.hash into e
                select e.First())];
        // 有効行がある部分応答は従来どおり受理し、空のときだけ応答全体の既知外枠を識別します。
        const string xmlDeclaration = """<\?xml\s+version\s*=\s*(?:"1\.[0-9]+"|'1\.[0-9]+')"""
            + """(?:\s+encoding\s*=\s*(?:"[A-Za-z][A-Za-z0-9._-]*"|'[A-Za-z][A-Za-z0-9._-]*'))?"""
            + """(?:\s+standalone\s*=\s*(?:"(?:yes|no)"|'(?:yes|no)'))?\s*\?>""";
        if (scores.Count == 0 && !Regex.IsMatch(playerXml ?? string.Empty,
            @"\A[\s\uFEFF]*(?:#\s*)?(?:" + xmlDeclaration + @"\s*)?<scorelist\s*(?:/>|>\s*</scorelist>)(?:\s*<rivalname>[^<]*</rivalname>)?[\s\uFEFF]*\z"))
        {
            throw new FormatException("本人IR応答を正常な空成績一覧と判別できません。");
        }
        return scores;
    }

    /// <summary>hash順のraw成績を固定整数・タブ・LFで表し、地域に依存しない同一性判定用digestを返します。</summary>
    internal static string ComputeScoreDigest(IEnumerable<LR2IRScore> scoreTable)
    {
        var builder = new StringBuilder();
        foreach (LR2IRScore score in (scoreTable ?? [])
            .Where(score => score != null && !string.IsNullOrWhiteSpace(score.hash))
            .OrderBy(score => score.hash, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(score.hash.ToLowerInvariant()).Append('\t')
                .Append(CultureInfo.InvariantCulture, $"{score.clearValue}\t{score.notes}\t{score.combo}\t{score.pg}\t{score.gr}\t{score.gd}\t{score.bd}\t{score.pr}\t{score.minbp}\t{score.option}\n");
        }
        return ComputeSha256Hex(builder.ToString());
    }

    private static string ComputeSha256Hex(string value)
    {
        using var sha256 = SHA256.Create();
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    /// <summary>IR値をスコア正本へ反映し、所持BMSの未送信判定を既存索引で行います。</summary>
    public List<BMSScore> UpdateBmsScores(List<LR2IRScore> scoreTable, List<BMSScore> currentScores, Func<string, bool> isOwnedBmsHash, bool detectUnsentScores = true)
    {
        if (scoreTable == null || currentScores == null)
        {
            return currentScores;
        }
        List<BMSScore> existingScores = [.. currentScores];
        foreach (BMSScore score in existingScores)
        {
            if (score != null)
            {
                score.IsLr2IrScoreUnsent = false;
            }
        }
        List<BMSScore> appendedScores = [.. (from os in scoreTable
                                             join ls in existingScores on os.hash equals ls.hash into ls
                                             select new
                                             {
                                                 irScore = os,
                                                 bmsScores = ls.DefaultIfEmpty(new BMSScore(os))
                                             } into grp
                                             from a in grp.bmsScores
                                             select new
                                             {
                                                 grp.irScore,
                                                 bmsScore = a
                                             }).Select(b =>
                                         {
                                             if (b.irScore.clear > b.bmsScore.clear)
                                             {
                                                 b.bmsScore.clear = b.irScore.clear;
                                                 b.bmsScore.op_history = b.irScore.option;
                                             }
                                             if (b.irScore.score > b.bmsScore.score)
                                             {
                                                 b.bmsScore.Overwrite(b.irScore);
                                             }
                                             return b.bmsScore;
                                         }).Except(existingScores)];
        List<BMSScore> mergedScores = [.. existingScores, .. appendedScores];
        if (detectUnsentScores)
        {
            ApplyLr2IrScoreUnsentStatus(mergedScores, isOwnedBmsHash, scoreTable);
        }
        return mergedScores;
    }

    private static void ApplyLr2IrScoreUnsentStatus(IEnumerable<BMSScore> scores, Func<string, bool> isOwnedBmsHash, IEnumerable<LR2IRScore> scoreTable)
    {
        ILookup<string, LR2IRScore> irScoresByHash = (scoreTable ?? [])
            .Where(score => score != null && !string.IsNullOrWhiteSpace(score.hash))
            .ToLookup(score => score.hash, StringComparer.OrdinalIgnoreCase);
        foreach (BMSScore score in (scores ?? []).Where(score => score != null && isOwnedBmsHash(score.hash)))
        {
            score.IsLr2IrScoreUnsent = IsLr2IrScoreUnsent(score, irScoresByHash[score.hash]);
        }
    }

    private static bool IsLr2IrScoreUnsent(BMSScore score, IEnumerable<LR2IRScore> irScores)
    {
        LR2IRScore[] irScoreArray = [.. irScores ?? []];
        if (irScoreArray.Length == 0)
        {
            return true;
        }
        return irScoreArray.Any(irScore => IsDifferentFromIrScore(score, irScore));
    }

    private static bool IsDifferentFromIrScore(BMSScore score, LR2IRScore irScore)
    {
        return irScore == null
            || score.score != irScore.score
            || score.minbp != irScore.minbp
            || (score.clear != irScore.clear && (score.clear != ClearType.PA || irScore.clear != ClearType.FC));
    }

    public IrDataLoadResult LoadIrDataWithMetrics(int lr2Id, BmsLibraryDbGateway dbGateway)
    {
        return dbGateway?.LoadIrDataWithMetrics(lr2Id) ?? new IrDataLoadResult();
    }

    /// <summary>保存順位とcacheを読み、必要な保存を確定してから共有スコアへ反映します。</summary>
    /// <remarks>順位DBの読取り失敗は伝播し、この更新による保存と共有スコアへの採用を行いません。</remarks>
    public IrCacheRefreshResult RefreshRankingScoresFromCache(int lr2Id, string scoreDbPath, BmsLibraryDbGateway dbGateway, List<BMSScore> bmsScores, bool estimateOfflineScoreRanking)
    {
        IrCacheRefreshPlan plan = PrepareRankingScoresRefreshPlanForLibrary(
            lr2Id,
            scoreDbPath,
            dbGateway,
            estimateOfflineScoreRanking);
        CommitPreparedRankingScoresRefreshPlan(plan, lr2Id, dbGateway);
        ApplyRankingScoresRefreshPlan(plan, scoreDbPath, bmsScores, estimateOfflineScoreRanking);
        return plan.Result;
    }

    /// <summary>保存・共有スコアを変更せず更新計画を作り、順位DBの読取り失敗は操作の所有者へ伝播します。</summary>
    internal IrCacheRefreshPlan PrepareRankingScoresRefreshPlanForLibrary(
        int lr2Id,
        string scoreDbPath,
        BmsLibraryDbGateway dbGateway,
        bool estimateOfflineScoreRanking)
    {
        IrCacheRefreshPlan plan = BuildRankingScoresRefreshPlan(
            lr2Id,
            scoreDbPath,
            dbGateway);
        if (!estimateOfflineScoreRanking)
        {
            return plan;
        }
        int lookupLoadCount = 0;
        foreach (LR2IRData row in plan.DbRows)
        {
            if (row == null
                || string.IsNullOrWhiteSpace(row.hash)
                || plan.XmlLookupsByHash.ContainsKey(row.hash))
            {
                continue;
            }
            string cachePath = string.IsNullOrWhiteSpace(scoreDbPath)
                ? null
                : Path.Combine(
                    Path.Combine(
                        Path.GetDirectoryName(scoreDbPath),
                        "..\\..\\Ir"),
                    row.hash + ".xml");
            Lr2IrRankingLookup lookup = EnsureRankingLookupLoaded(
                null,
                cachePath,
                ref lookupLoadCount);
            if (lookup != null)
            {
                plan.XmlLookupsByHash[row.hash] = lookup;
            }
        }
        plan.Result.OfflineEstimateXmlLoadCount = lookupLoadCount;
        return plan;
    }

    /// <summary>cacheの並列読取り・解析が準備した行をL内で確定します。準備側はDBへ書込みません。</summary>
    internal void CommitPreparedRankingScoresRefreshPlan(IrCacheRefreshPlan plan, int lr2Id, BmsLibraryDbGateway dbGateway)
    {
        if (plan.UpsertRows.Count > 0)
        {
            var upsertStopwatch = Stopwatch.StartNew();
            if (plan.DbRows.Count == 0)
            {
                plan.Result.BulkInsertUsed = dbGateway.TryBulkInsertIrDataForEmptyLr2Id(lr2Id, plan.UpsertRows);
            }
            if (!plan.Result.BulkInsertUsed)
            {
                dbGateway.UpsertIrData(plan.UpsertRows);
            }
            upsertStopwatch.Stop();
            plan.Result.UpsertMs = upsertStopwatch.ElapsedMilliseconds;
        }
    }

    internal IrCacheRefreshResult ApplyPreparedRankingScoresRefreshPlanForLibrary(IrCacheRefreshPlan preparedPlan, string scoreDbPath, List<BMSScore> bmsScores, bool estimateOfflineScoreRanking)
    {
        IrCacheRefreshPlan plan = preparedPlan ?? new IrCacheRefreshPlan();
        ApplyRankingScoresRefreshPlan(plan, scoreDbPath, bmsScores, estimateOfflineScoreRanking);
        return plan.Result;
    }

    private IrCacheRefreshPlan BuildRankingScoresRefreshPlan(int lr2Id, string scoreDbPath, BmsLibraryDbGateway dbGateway)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var plan = new IrCacheRefreshPlan();
        IrCacheRefreshResult result = plan.Result;
        if (lr2Id == 0 || string.IsNullOrWhiteSpace(scoreDbPath) || dbGateway == null)
        {
            return plan;
        }
        string cacheDirectoryPath;
        try
        {
            cacheDirectoryPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(scoreDbPath), "..\\..\\Ir"));
        }
        catch
        {
            return plan;
        }
        if (!Directory.Exists(cacheDirectoryPath))
        {
            return plan;
        }
        IrDataLoadResult loadResult = LoadIrDataWithMetrics(lr2Id, dbGateway);
        List<LR2IRData> irDataDb = loadResult.Rows;
        result.DbReadMs = loadResult.DbReadMs;
        result.IrDataDbReadMs = loadResult.DbReadMs;
        result.IrDataMaterializeMs = loadResult.MaterializeMs;
        result.IrDataDbLockWaitMs = loadResult.DbLockWaitMs;
        if (irDataDb == null)
        {
            return plan;
        }
        plan.DbRows.AddRange(irDataDb.Where(data => data != null && !string.IsNullOrWhiteSpace(data.hash)));
        result.DbRows = plan.DbRows.Count;
        var indexStopwatch = Stopwatch.StartNew();
        var irDataByHash = new Dictionary<string, LR2IRData>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2IRData data in plan.DbRows)
        {
            if (!irDataByHash.ContainsKey(data.hash))
            {
                irDataByHash.Add(data.hash, data);
            }
        }
        indexStopwatch.Stop();
        result.IndexBuildMs = indexStopwatch.ElapsedMilliseconds;

        ConcurrentBag<LR2IRData> irDataToBeCommitted = [];
        int reloadDegree = ResolveRankingCacheXmlReloadDegree();
        result.XmlReloadDegree = reloadDegree;
        var xmlCheckStopwatch = Stopwatch.StartNew();
        List<string> cacheFilePaths = [.. Directory.EnumerateFiles(cacheDirectoryPath)];
        result.CacheFilesScanned = cacheFilePaths.Count;
        var reloadTargets = cacheFilePaths.AsParallel().WithDegreeOfParallelism(reloadDegree).Where(delegate (string filePath)
        {
            try
            {
                string md5 = Path.GetFileNameWithoutExtension(filePath);
                if (!LR2SongDB.md5HashRegex.IsMatch(md5))
                {
                    return false;
                }
                irDataByHash.TryGetValue(md5, out LR2IRData current);
                if (current == null)
                {
                    return true;
                }
                DateTime lastWriteTime = File.GetLastWriteTime(filePath);
                if (current.lastcacheupdate == lastWriteTime)
                {
                    return false;
                }
                string tail = string.Empty;
                using (var reader = new StreamReader(filePath))
                {
                    tail = reader.Tail(33, 19);
                }
                tail = tail?.TrimEnd('\0') ?? string.Empty;
                if (!DateTime.TryParseExact(tail, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedLastUpdate) || parsedLastUpdate > current.lastupdate)
                {
                    return true;
                }
                if (!current.lastcacheupdate.HasValue)
                {
                    current.lastcacheupdate = lastWriteTime;
                    irDataToBeCommitted.Add(current);
                }
                return false;
            }
            catch (Exception ex)
            {
                WarnRankingCacheFileFailure(ex, filePath, "reload-check");
                return false;
            }
        }).ToList();
        xmlCheckStopwatch.Stop();
        result.XmlCheckMs = xmlCheckStopwatch.ElapsedMilliseconds;

        var xmlReloadStopwatch = Stopwatch.StartNew();
        ReloadRankingCacheTargets(reloadTargets, lr2Id, reloadDegree, delegate (RankingCacheReloadResult reloadResult)
        {
            if (reloadResult == null)
            {
                return;
            }
            if (reloadResult.Failed)
            {
                result.XmlParseFailedCount++;
                return;
            }
            result.XmlScoresParsed += reloadResult.ScoresParsed;
            LR2IRData irData = reloadResult.IrData;
            if (irData == null)
            {
                result.XmlParseFailedCount++;
                return;
            }
            irDataToBeCommitted.Add(irData);
            plan.XmlRows.Add(irData);
            if (reloadResult.Lookup != null)
            {
                plan.XmlLookupsByHash[irData.hash] = reloadResult.Lookup;
            }
        });
        xmlReloadStopwatch.Stop();
        result.XmlReloadMs = xmlReloadStopwatch.ElapsedMilliseconds;
        result.CacheFilesReloaded = reloadTargets.Count;
        List<LR2IRData> upsertRows = [.. irDataToBeCommitted.Where(data => data != null)];
        result.IrDataUpsertCount = upsertRows.Count;
        plan.UpsertRows.AddRange(upsertRows);
        totalStopwatch.Stop();
        result.ElapsedMs = totalStopwatch.ElapsedMilliseconds;
        return plan;
    }

    private int ResolveRankingCacheXmlReloadDegree()
    {
        if (rankingCacheXmlReloadDegreeOverride.HasValue && rankingCacheXmlReloadDegreeOverride.Value > 0)
        {
            return rankingCacheXmlReloadDegreeOverride.Value;
        }
        return Math.Max(1, Environment.ProcessorCount - 1);
    }

    private void ReloadRankingCacheTargets(IEnumerable<string> reloadTargets, int lr2Id, int degree, Action<RankingCacheReloadResult> consumeResult)
    {
        List<string> targets = [.. (reloadTargets ?? [])];
        if (targets.Count == 0)
        {
            return;
        }
        int workerCount = Math.Max(1, Math.Min(degree, targets.Count));
        using var workQueue = new BlockingCollection<string>(workerCount * 2);
        using var resultQueue = new BlockingCollection<RankingCacheReloadResult>(workerCount * 4);
        var collector = Task.Run(delegate
        {
            foreach (RankingCacheReloadResult result in resultQueue.GetConsumingEnumerable())
            {
                consumeResult(result);
            }
        });
        Task[] workers = [.. Enumerable.Range(0, workerCount).Select(_ => Task.Run(delegate
        {
            foreach (string filePath in workQueue.GetConsumingEnumerable())
            {
                resultQueue.Add(LoadRankingCacheSummary(filePath, lr2Id));
            }
        }))];
        foreach (string filePath in targets)
        {
            workQueue.Add(filePath);
        }
        workQueue.CompleteAdding();
        Task.WaitAll(workers);
        resultQueue.CompleteAdding();
        collector.Wait();
    }

    private RankingCacheReloadResult LoadRankingCacheSummary(string filePath, int lr2Id)
    {
        var result = new RankingCacheReloadResult();
        try
        {
            string md5 = Path.GetFileNameWithoutExtension(filePath);
            DateTime cacheLastWriteTime = File.GetLastWriteTime(filePath);
            string xml = File.ReadAllText(filePath, Encoding.GetEncoding("shift_jis"));
            if (TryParseRankingCacheSummary(xml, md5, lr2Id, cacheLastWriteTime, out LR2IRData irData, out int scoresParsed, out Lr2IrRankingLookup lookup))
            {
                result.IrData = irData;
                result.Lookup = lookup;
                result.ScoresParsed = scoresParsed;
                return result;
            }
        }
        catch (Exception ex)
        {
            WarnRankingCacheFileFailure(ex, filePath, "read-summary");
        }
        result.Failed = true;
        return result;
    }

    /// <summary>対象の非採用が決まったファイル失敗を診断し、純診断の失敗は元の対象結果へ加えません。</summary>
    private static void WarnRankingCacheFileFailure(Exception exception, string filePath, string phase)
    {
        try
        {
            NLogWrapper.FileLogger?.Warn(exception, "Ranking cache file " + phase + " failed path=" + filePath);
        }
        catch (Exception)
        {
            // 未採用の対象を処理するための診断であり、記録失敗によって結果を変えません。
        }
    }

    internal static bool TryParseRankingCacheSummary(string rankingXml, string md5, int lr2Id, DateTime cacheLastWriteTime, out LR2IRData irData, out int scoresParsed)
    {
        return TryParseRankingCacheSummary(rankingXml, md5, lr2Id, cacheLastWriteTime, out irData, out scoresParsed, out _);
    }

    private static bool TryParseRankingCacheSummary(string rankingXml, string md5, int lr2Id, DateTime cacheLastWriteTime, out LR2IRData irData, out int scoresParsed, out Lr2IrRankingLookup lookup)
    {
        irData = null;
        scoresParsed = 0;
        lookup = null;
        if (string.IsNullOrEmpty(rankingXml) || string.IsNullOrWhiteSpace(md5) || !LR2SongDB.md5HashRegex.IsMatch(md5))
        {
            return false;
        }
        if (!Lr2IrRankingCacheParser.TryParseSummary(rankingXml, md5, cacheLastWriteTime, lr2Id, out Lr2IrRankingParseResult result))
        {
            return false;
        }
        irData = result.IrData;
        scoresParsed = result.ScoresParsed;
        lookup = result.Lookup;
        return irData != null && scoresParsed > 0;
    }

    private void ApplyRankingScoresRefreshPlan(IrCacheRefreshPlan plan, string scoreDbPath, List<BMSScore> bmsScores, bool estimateOfflineScoreRanking)
    {
        if (plan == null || bmsScores == null)
        {
            return;
        }
        Dictionary<string, BMSScore> scoresByHash = BuildScoreIndex(bmsScores);

        var xmlUpdatedHashes = new HashSet<string>(plan.XmlRows.Where(data => data != null && !string.IsNullOrWhiteSpace(data.hash)).Select(data => data.hash), StringComparer.OrdinalIgnoreCase);
        int offlineEstimateXmlLoadCount = plan.Result.OfflineEstimateXmlLoadCount;
        foreach (LR2IRData irData in plan.DbRows)
        {
            if (irData != null && !xmlUpdatedHashes.Contains(irData.hash))
            {
                plan.XmlLookupsByHash.TryGetValue(
                    irData.hash,
                    out Lr2IrRankingLookup lookup);
                ApplyIrDataToScoresIndexed(
                    irData,
                    lookup,
                    scoreDbPath,
                    bmsScores,
                    scoresByHash,
                    estimateOfflineScoreRanking,
                    ref offlineEstimateXmlLoadCount,
                    allowLookupFileLoad: false);
                plan.Result.DbFallbackAppliedCount++;
            }
        }
        foreach (LR2IRData irData in plan.XmlRows)
        {
            if (irData == null)
            {
                continue;
            }
            plan.XmlLookupsByHash.TryGetValue(irData.hash, out Lr2IrRankingLookup lookup);
            ApplyIrDataToScoresIndexed(
                irData,
                lookup,
                scoreDbPath,
                bmsScores,
                scoresByHash,
                estimateOfflineScoreRanking,
                ref offlineEstimateXmlLoadCount,
                allowLookupFileLoad: false);
            plan.Result.XmlAppliedCount++;
        }
        plan.Result.OfflineEstimateXmlLoadCount = offlineEstimateXmlLoadCount;
    }

    private static Lr2IrRankingLookup EnsureRankingLookupLoaded(Lr2IrRankingLookup lookup, string cachePath, ref int loadCount)
    {
        if (lookup != null || string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            return lookup;
        }
        try
        {
            string rankingXml = File.ReadAllText(cachePath, Encoding.GetEncoding("shift_jis"));
            string md5 = Path.GetFileNameWithoutExtension(cachePath);
            if (Lr2IrRankingCacheParser.TryParseLookup(rankingXml, md5, File.GetLastWriteTime(cachePath), false, out Lr2IrRankingLookup loaded))
            {
                loadCount++;
                return loaded;
            }
        }
        catch
        {
        }
        return null;
    }
}
