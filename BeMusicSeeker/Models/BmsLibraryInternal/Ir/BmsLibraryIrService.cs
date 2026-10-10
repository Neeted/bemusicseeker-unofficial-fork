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
using BeMusicSeeker.Models.Utils;
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

        public HashSet<BMSScore> ChangedScores { get; } = [];

        public IrCacheRefreshResult Result { get; } = new IrCacheRefreshResult();
    }

    internal sealed class RankingCacheDownloadBatch
    {
        internal List<BMSLibrary.IRDataCacheInfo> Source { get; } = [];

        internal List<BMSLibrary.IRDataCacheInfo> Failed { get; } = [];

        internal List<DownloadedRankingCacheFile> DownloadedFiles { get; } = [];
    }

    internal sealed class RankingCacheApplyPlan
    {
        internal RankingCacheDownloadBatch DownloadBatch { get; init; }

        internal List<RankingCacheReloadResult> ParsedResults { get; } = [];

        internal List<BMSLibrary.IRDataCacheInfo> Failed { get; } = [];

        internal HashSet<string> PromotedStagedPaths { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class RankingCacheApplyResult
    {
        internal List<BMSLibrary.IRDataCacheInfo> Failed { get; } = [];

        internal List<BMSScore> ChangedScores { get; } = [];
    }

    internal sealed class DownloadedRankingCacheFile
    {
        internal BMSLibrary.IRDataCacheInfo CacheInfo { get; init; }

        internal string StagedPath { get; init; }
    }

    internal sealed class RankingCacheReloadResult
    {
        public string FilePath { get; set; }

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
                    score.maxcombo = data.combo;
                    score.minbp = data.minbp;
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

    /// <summary>通信済み結果だけを正本DBへ確定します。モデル側はL取得後に停止とscore contextを確認して呼びます。</summary>
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
        LR2SongDBExtended.ir_score_refresh_metadata metadata = null;
        try
        {
            metadata = dbGateway.LoadIrScoreRefreshMetadata(lr2Id, out long metadataDbLockWaitMs);
            result.DbLockWaitMs += metadataDbLockWaitMs;
        }
        catch
        {
        }
        if (metadata != null && string.Equals(metadata.score_digest_sha256, scoreDigest, StringComparison.OrdinalIgnoreCase))
        {
            LoadExistingIrScores(dbGateway, result);
            result.Skipped = true;
            result.SkipReason = "score_digest_same";
            return result;
        }
        List<LR2IRScore> existingScoreTable = LoadExistingIrScoresForDigestComparison(dbGateway, result);
        if (string.Equals(ComputeScoreDigest(existingScoreTable), scoreDigest, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                dbGateway.UpsertIrScoreRefreshMetadata(lr2Id, scoreDigest);
                result.MetadataUpdated = true;
            }
            catch
            {
            }
            result.ScoreTable = existingScoreTable;
            result.LoadedRows = existingScoreTable.Count;
            result.Skipped = true;
            result.SkipReason = "score_digest_same";
            return result;
        }
        try
        {
            var dbStopwatch = Stopwatch.StartNew();
            dbGateway.ReplaceIrScoreTable(scoreTable);
            dbStopwatch.Stop();
            result.DbReplaceMs = dbStopwatch.ElapsedMilliseconds;
        }
        catch
        {
            result.SkipReason = "unavailable";
            return result;
        }
        try
        {
            dbGateway.UpsertIrScoreRefreshMetadata(lr2Id, scoreDigest);
            result.MetadataUpdated = true;
        }
        catch
        {
        }
        result.ScoreTable = scoreTable;
        result.Skipped = false;
        result.SkipReason = "changed";
        return result;
    }

    private static List<LR2IRScore> ParsePlayerScoreXml(string playerXml, Regex lr2IrScoreRegex)
    {
        return [.. (from e in lr2IrScoreRegex.Matches(playerXml ?? string.Empty).Cast<Match>().Select(delegate (Match m)
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
    }

    private static List<LR2IRScore> LoadExistingIrScoresForDigestComparison(BmsLibraryDbGateway dbGateway, IrScoreTableUpdateResult result)
    {
        try
        {
            var loadStopwatch = Stopwatch.StartNew();
            IrScoreRowsLoadResult loadResult = dbGateway.LoadIrScoreRowsWithMetrics();
            loadStopwatch.Stop();
            List<LR2IRScore> scoreTable = loadResult.Rows;
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
            result.DbLockWaitMs += loadResult.DbLockWaitMs;
            result.LoadedRows = scoreTable?.Count ?? 0;
            return scoreTable ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void LoadExistingIrScores(BmsLibraryDbGateway dbGateway, IrScoreTableUpdateResult result)
    {
        try
        {
            var loadStopwatch = Stopwatch.StartNew();
            IrScoreRowsLoadResult loadResult = dbGateway.LoadIrScoreRowsWithMetrics();
            result.ScoreTable = loadResult.Rows;
            loadStopwatch.Stop();
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
            result.DbLockWaitMs += loadResult.DbLockWaitMs;
            result.LoadedRows = result.ScoreTable?.Count ?? 0;
        }
        catch
        {
            result.ScoreTable = [];
        }
    }

    internal static string ComputeScoreDigest(IEnumerable<LR2IRScore> scoreTable)
    {
        var builder = new StringBuilder();
        foreach (LR2IRScore score in (scoreTable ?? [])
            .Where(score => score != null && !string.IsNullOrWhiteSpace(score.hash))
            .OrderBy(score => score.hash, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(score.hash.ToLowerInvariant()).Append('\t')
                .Append(score.clearValue).Append('\t')
                .Append(score.notes).Append('\t')
                .Append(score.combo).Append('\t')
                .Append(score.pg).Append('\t')
                .Append(score.gr).Append('\t')
                .Append(score.gd).Append('\t')
                .Append(score.bd).Append('\t')
                .Append(score.pr).Append('\t')
                .Append(score.minbp).Append('\t')
                .Append(score.option).Append('\n');
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

    public List<LR2IRData> LoadIrData(int lr2Id, BmsLibraryDbGateway dbGateway)
    {
        return LoadIrDataWithMetrics(lr2Id, dbGateway).Rows;
    }

    public IrDataLoadResult LoadIrDataWithMetrics(int lr2Id, BmsLibraryDbGateway dbGateway)
    {
        return dbGateway?.LoadIrDataWithMetrics(lr2Id) ?? new IrDataLoadResult();
    }

    internal List<BMSLibrary.IRDataCacheInfo> GetIRDataNeedUpdatesFromRankingInfo(
        int lr2Id,
        IEnumerable<BMSLibrary.IRDataCacheInfo> rankingInfo,
        BmsLibraryDbGateway dbGateway)
    {
        List<BMSLibrary.IRDataCacheInfo> outer = [.. (rankingInfo ?? [])];
        List<LR2IRData> inner = LoadIrData(lr2Id, dbGateway);
        return [.. (from i in outer
                join d in inner on i.md5 equals d.hash into d
                select new
                {
                    irCacheInfo = i,
                    irDataDb = d.DefaultIfEmpty()
                } into grp
                from a in grp.irDataDb
                select new
                {
                    grp.irCacheInfo,
                    irDataDb = a
                } into b
                where b.irDataDb == null || b.irCacheInfo.lastupdate > b.irDataDb.lastupdate
                select b.irCacheInfo)];
    }

    internal RankingCacheDownloadBatch DownloadRankingCacheFiles(
        IEnumerable<BMSLibrary.IRDataCacheInfo> cacheInfo,
        string stagingDirectoryPath,
        IBmsLibraryIrClient irClient,
        Uri rankingDataUrl)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectoryPath))
        {
            throw new ArgumentException("A ranking cache staging directory is required.", nameof(stagingDirectoryPath));
        }
        Directory.CreateDirectory(stagingDirectoryPath);
        var batch = new RankingCacheDownloadBatch();
        batch.Source.AddRange(cacheInfo ?? []);
        object failedLock = new();
        object downloadedLock = new();
        batch.Source.AsParallel().WithDegreeOfParallelism(3).ForAll(delegate (BMSLibrary.IRDataCacheInfo info)
        {
            try
            {
                string stagedPath = Path.Combine(stagingDirectoryPath, info.md5 + ".xml");
                irClient.DownloadRankingData(rankingDataUrl, info.md5, stagedPath);
                lock (downloadedLock)
                {
                    batch.DownloadedFiles.Add(new DownloadedRankingCacheFile
                    {
                        CacheInfo = info,
                        StagedPath = stagedPath
                    });
                }
            }
            catch
            {
                lock (failedLock)
                {
                    batch.Failed.Add(info);
                }
            }
        });
        return batch;
    }

    internal RankingCacheApplyPlan PrepareDownloadedRankingCache(
        int lr2Id,
        RankingCacheDownloadBatch batch)
    {
        if (batch == null)
        {
            throw new ArgumentNullException(nameof(batch));
        }
        var plan = new RankingCacheApplyPlan
        {
            DownloadBatch = batch
        };
        plan.Failed.AddRange(batch.Failed);
        object parsedLock = new();
        ReloadRankingCacheTargets(
            batch.DownloadedFiles.Select(downloadedFile => downloadedFile.StagedPath),
            lr2Id,
            ResolveRankingCacheXmlReloadDegree(),
            delegate (RankingCacheReloadResult result)
            {
                lock (parsedLock)
                {
                    plan.ParsedResults.Add(result);
                }
            });
        var sourceByHash = batch.Source
            .Where(info => info != null && !string.IsNullOrWhiteSpace(info.md5))
            .GroupBy(info => info.md5, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (RankingCacheReloadResult result in plan.ParsedResults)
        {
            if (result == null || result.Failed || result.IrData == null)
            {
                string failedHash = result?.FilePath == null
                    ? null
                    : Path.GetFileNameWithoutExtension(result.FilePath);
                if (!string.IsNullOrWhiteSpace(failedHash)
                    && sourceByHash.TryGetValue(
                        failedHash,
                        out BMSLibrary.IRDataCacheInfo failedInfo))
                {
                    plan.Failed.Add(failedInfo);
                }
            }
        }
        return plan;
    }

    internal void PromoteDownloadedRankingCache(
        RankingCacheApplyPlan plan,
        string irCacheDirPath)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }
        var parsedStagedPaths = new HashSet<string>(
            plan.ParsedResults
                .Where(result => result != null
                    && !result.Failed
                    && result.IrData != null
                    && !string.IsNullOrWhiteSpace(result.FilePath))
                .Select(result => result.FilePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (DownloadedRankingCacheFile downloadedFile in plan.DownloadBatch.DownloadedFiles)
        {
            if (!parsedStagedPaths.Contains(downloadedFile.StagedPath))
            {
                plan.Failed.Add(downloadedFile.CacheInfo);
                continue;
            }
            try
            {
                string cacheFilePath = Path.Combine(
                    irCacheDirPath,
                    downloadedFile.CacheInfo.md5 + ".xml");
                LongPathFileSystem.MoveFile(
                    downloadedFile.StagedPath,
                    cacheFilePath,
                    overwrite: true);
                plan.PromotedStagedPaths.Add(downloadedFile.StagedPath);
            }
            catch
            {
                plan.Failed.Add(downloadedFile.CacheInfo);
            }
        }
    }

    internal RankingCacheApplyResult ApplyPreparedDownloadedRankingCache(
        RankingCacheApplyPlan plan,
        BmsLibraryDbGateway dbGateway,
        List<BMSScore> bmsScores,
        bool estimateOfflineScoreRanking)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }
        var applyResult = new RankingCacheApplyResult();
        applyResult.Failed.AddRange(plan.Failed.Distinct());
        List<LR2IRData> irDataToBeCommitted = [];
        Dictionary<string, BMSScore> scoresByHash = BuildScoreIndex(bmsScores);

        var changedScores = new HashSet<BMSScore>();
        int offlineEstimateXmlLoadCount = 0;
        foreach (RankingCacheReloadResult result in plan.ParsedResults)
        {
            if (result == null
                || result.Failed
                || result.IrData == null
                || !plan.PromotedStagedPaths.Contains(result.FilePath))
            {
                continue;
            }

            irDataToBeCommitted.Add(result.IrData);
            BMSScore changedScore = ApplyIrDataToScoresIndexed(
                result.IrData,
                result.Lookup,
                dbGateway.ScoreDbPath,
                bmsScores,
                scoresByHash,
                estimateOfflineScoreRanking,
                ref offlineEstimateXmlLoadCount,
                allowLookupFileLoad: false);
            if (changedScore != null)
            {
                changedScores.Add(changedScore);
            }
        }

        dbGateway.UpsertIrData(irDataToBeCommitted);
        applyResult.ChangedScores.AddRange(changedScores);
        return applyResult;
    }

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
        List<LR2IRData> irDataDb;
        try
        {
            IrDataLoadResult loadResult = LoadIrDataWithMetrics(lr2Id, dbGateway);
            irDataDb = loadResult.Rows;
            result.DbReadMs = loadResult.DbReadMs;
            result.IrDataDbReadMs = loadResult.DbReadMs;
            result.IrDataMaterializeMs = loadResult.MaterializeMs;
            result.IrDataDbLockWaitMs = loadResult.DbLockWaitMs;
        }
        catch
        {
            return plan;
        }
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
            catch
            {
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
        var result = new RankingCacheReloadResult { FilePath = filePath };
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
        catch
        {
        }
        result.Failed = true;
        return result;
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
                BMSScore changedScore = ApplyIrDataToScoresIndexed(
                    irData,
                    lookup,
                    scoreDbPath,
                    bmsScores,
                    scoresByHash,
                    estimateOfflineScoreRanking,
                    ref offlineEstimateXmlLoadCount,
                    allowLookupFileLoad: false);
                if (changedScore != null)
                {
                    plan.ChangedScores.Add(changedScore);
                }
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
            BMSScore changedScore = ApplyIrDataToScoresIndexed(
                irData,
                lookup,
                scoreDbPath,
                bmsScores,
                scoresByHash,
                estimateOfflineScoreRanking,
                ref offlineEstimateXmlLoadCount,
                allowLookupFileLoad: false);
            if (changedScore != null)
            {
                plan.ChangedScores.Add(changedScore);
            }
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
