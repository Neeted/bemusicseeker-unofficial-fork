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
using Ribbit.Util.Extensions;
using Ribbit.Logging;

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

    public void ApplyKnownScoresToFiles(IEnumerable<BMSFile> bmsFiles, IEnumerable<BMSScore> bmsScores)
    {
        ApplyKnownScoresToFilesAndCount(bmsFiles, bmsScores);
    }

    /// <summary>
    /// hash-index 化済み score snapshot を使って対象ファイルへ score を反映し、反映件数を返します。
    /// playlist score probe と deferred hydration で使う高速経路です。
    /// </summary>
    /// <param name="bmsFiles">score 反映対象の譜面一覧。</param>
    /// <param name="scoresByHash">MD5 hash をキーにした score snapshot。</param>
    /// <returns>score を反映できた件数。</returns>
    internal int ApplyKnownScoresToFilesAndCount(IEnumerable<BMSFile> bmsFiles, IReadOnlyDictionary<string, BMSScore> scoresByHash)
    {
        return ApplyKnownScoresToFilesAndCount(bmsFiles, scoresByHash, null);
    }

    internal int ApplyKnownScoresToFilesAndCount(
        IEnumerable<BMSFile> bmsFiles,
        IReadOnlyDictionary<string, BMSScore> scoresByHash,
        IReadOnlyDictionary<string, BMSScore> scoresBySha256)
    {
        if (bmsFiles == null)
        {
            return 0;
        }
        bool hasHashScores = scoresByHash != null && scoresByHash.Count > 0;
        bool hasSha256Scores = scoresBySha256 != null && scoresBySha256.Count > 0;
        if (!hasHashScores && !hasSha256Scores)
        {
            return 0;
        }
        int matchedScoreCount = 0;
        foreach (BMSFile bmsFile in bmsFiles)
        {
            if (bmsFile == null)
            {
                continue;
            }
            if (hasSha256Scores && !string.IsNullOrWhiteSpace(bmsFile.sha256) && scoresBySha256.TryGetValue(bmsFile.sha256, out BMSScore beatorajaScore))
            {
                bmsFile.bmsScore = CloneScoreForFileHash(beatorajaScore, bmsFile.hash);
                matchedScoreCount++;
                continue;
            }
            if (hasHashScores && !string.IsNullOrWhiteSpace(bmsFile.hash) && scoresByHash.TryGetValue(bmsFile.hash, out BMSScore value))
            {
                bmsFile.bmsScore = value;
                matchedScoreCount++;
            }
        }
        return matchedScoreCount;
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

    internal int ApplyKnownScoresToFilesAndCount(IEnumerable<BMSFile> bmsFiles, IEnumerable<BMSScore> bmsScores)
    {
        if (bmsFiles == null || bmsScores == null)
        {
            return 0;
        }
        int matchedScoreCount = 0;
        foreach (var item in bmsFiles.Join(bmsScores, file => file.hash, score => score.hash, (file, score) => new
        {
            File = file,
            Score = score
        }))
        {
            item.File.bmsScore = item.Score;
            matchedScoreCount++;
        }
        return matchedScoreCount;
    }

    public LR2IRCache LoadIrCache(string filePath)
    {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        string rankingXml = File.ReadAllText(filePath, Encoding.GetEncoding("shift_jis"));
        DateTime lastWriteTime = File.GetLastWriteTime(filePath);
        return new LR2IRCache(rankingXml, fileNameWithoutExtension, lastWriteTime);
    }

    public void ApplyIrDataToScoresAndFiles(LR2IRData data, LR2IRCache cache, string scoreDbPath, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool estimateOfflineScoreRanking)
    {
        ApplyIrDataToScoresAndFiles(data, cache?.Lookup, scoreDbPath, bmsScores, bmsFiles, estimateOfflineScoreRanking);
    }

    private void ApplyIrDataToScoresAndFiles(LR2IRData data, Lr2IrRankingLookup lookup, string scoreDbPath, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool estimateOfflineScoreRanking)
    {
        if (data == null || bmsScores == null)
        {
            return;
        }
        Dictionary<string, BMSScore> scoresByHash = BuildScoreIndex(bmsScores);
        Dictionary<string, List<BMSFile>> filesByHash = BuildFileIndex(bmsFiles);
        int offlineEstimateXmlLoadCount = 0;
        ApplyIrDataToScoresAndFilesIndexed(data, lookup, scoreDbPath, bmsScores, scoresByHash, filesByHash, estimateOfflineScoreRanking, ref offlineEstimateXmlLoadCount);
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

    private static Dictionary<string, List<BMSFile>> BuildFileIndex(IEnumerable<BMSFile> bmsFiles)
    {
        var filesByHash = new Dictionary<string, List<BMSFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile bmsFile in bmsFiles ?? [])
        {
            if (bmsFile == null || string.IsNullOrWhiteSpace(bmsFile.hash))
            {
                continue;
            }
            if (!filesByHash.TryGetValue(bmsFile.hash, out List<BMSFile> files))
            {
                files = [];
                filesByHash.Add(bmsFile.hash, files);
            }
            files.Add(bmsFile);
        }
        return filesByHash;
    }

    private static void AttachScoreToFiles(BMSScore score, IReadOnlyDictionary<string, List<BMSFile>> filesByHash)
    {
        if (score == null || string.IsNullOrWhiteSpace(score.hash) || filesByHash == null || !filesByHash.TryGetValue(score.hash, out List<BMSFile> files))
        {
            return;
        }
        foreach (BMSFile file in files)
        {
            if (file != null)
            {
                file.bmsScore = score;
            }
        }
    }

    private BMSScore ApplyIrDataToScoresAndFilesIndexed(
        LR2IRData data,
        Lr2IrRankingLookup lookup,
        string scoreDbPath,
        List<BMSScore> bmsScores,
        Dictionary<string, BMSScore> scoresByHash,
        IReadOnlyDictionary<string, List<BMSFile>> filesByHash,
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
            AttachScoreToFiles(score, filesByHash);
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
        AttachScoreToFiles(score, filesByHash);
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

    /// <summary>同じ要求の prefetch は失敗も消費し、取得失敗・終了キャンセルでは DB を変更しません。</summary>
    public IrScoreTableUpdateResult UpdateIrScoreTableWithMetrics(int lr2Id, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient, Regex lr2IrScoreRegex, IrScorePrefetchResult prefetchedScore = null, CancellationToken cancellationToken = default)
    {
        var result = new IrScoreTableUpdateResult();
        if (dbGateway == null || irClient == null || lr2IrScoreRegex == null || lr2Id == 0 || string.IsNullOrWhiteSpace(dbGateway.ScoreDbPath))
        {
            result.SkipReason = "unavailable";
            return result;
        }
        IrScorePrefetchResult fetched = prefetchedScore?.Lr2Id == lr2Id
            ? prefetchedScore
            : PrefetchIrScoreTableWithMetrics(lr2Id, irClient, lr2IrScoreRegex, cancellationToken);
        result.PrefetchUsed = ReferenceEquals(fetched, prefetchedScore);
        if (result.PrefetchUsed)
        {
            result.PrefetchXmlFetchMs = fetched.XmlFetchMs;
            result.PrefetchXmlParseMs = fetched.XmlParseMs;
            result.PrefetchDigestMs = fetched.DigestMs;
        }
        else
        {
            result.XmlFetchMs = fetched.XmlFetchMs;
            result.XmlParseMs = fetched.XmlParseMs;
            result.DigestMs = fetched.DigestMs;
        }
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

    public List<BMSScore> UpdateBmsScores(List<LR2IRScore> scoreTable, List<BMSScore> currentScores, IEnumerable<BMSFile> bmsFiles, bool detectUnsentScores = true)
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
        ApplyKnownScoresToFiles(bmsFiles, appendedScores);
        if (detectUnsentScores)
        {
            ApplyLr2IrScoreUnsentStatus(mergedScores, bmsFiles, scoreTable);
        }
        return mergedScores;
    }

    private static void ApplyLr2IrScoreUnsentStatus(IEnumerable<BMSScore> scores, IEnumerable<BMSFile> bmsFiles, IEnumerable<LR2IRScore> scoreTable)
    {
        var targetHashes = new HashSet<string>(
            (bmsFiles ?? []).Where(file => file != null && !string.IsNullOrWhiteSpace(file.hash)).Select(file => file.hash),
            StringComparer.OrdinalIgnoreCase);
        ILookup<string, LR2IRScore> irScoresByHash = (scoreTable ?? [])
            .Where(score => score != null && !string.IsNullOrWhiteSpace(score.hash))
            .ToLookup(score => score.hash, StringComparer.OrdinalIgnoreCase);
        foreach (BMSScore score in (scores ?? []).Where(score => score != null && targetHashes.Contains(score.hash)))
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
        IEnumerable<BMSFile> bmsFiles,
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
        Dictionary<string, List<BMSFile>> filesByHash = BuildFileIndex(bmsFiles);
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
            BMSScore changedScore = ApplyIrDataToScoresAndFilesIndexed(
                result.IrData,
                result.Lookup,
                dbGateway.ScoreDbPath,
                bmsScores,
                scoresByHash,
                filesByHash,
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

    public IrCacheRefreshResult RefreshRankingScoresFromCache(int lr2Id, string scoreDbPath, BmsLibraryDbGateway dbGateway, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool estimateOfflineScoreRanking)
    {
        IrCacheRefreshPlan plan = PrepareRankingScoresRefreshPlanForLibrary(
            lr2Id,
            scoreDbPath,
            dbGateway,
            estimateOfflineScoreRanking);
        ApplyRankingScoresRefreshPlan(plan, scoreDbPath, bmsScores, bmsFiles, estimateOfflineScoreRanking);
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

    internal IrCacheRefreshResult ApplyPreparedRankingScoresRefreshPlanForLibrary(IrCacheRefreshPlan preparedPlan, string scoreDbPath, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool estimateOfflineScoreRanking)
    {
        IrCacheRefreshPlan plan = preparedPlan ?? new IrCacheRefreshPlan();
        ApplyRankingScoresRefreshPlan(plan, scoreDbPath, bmsScores, bmsFiles, estimateOfflineScoreRanking);
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
        if (upsertRows.Count > 0)
        {
            var upsertStopwatch = Stopwatch.StartNew();
            if (plan.DbRows.Count == 0)
            {
                result.BulkInsertUsed = dbGateway.TryBulkInsertIrDataForEmptyLr2Id(lr2Id, upsertRows);
            }
            if (!result.BulkInsertUsed)
            {
                dbGateway.UpsertIrData(upsertRows);
            }
            upsertStopwatch.Stop();
            result.UpsertMs = upsertStopwatch.ElapsedMilliseconds;
        }
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

    private void ApplyRankingScoresRefreshPlan(IrCacheRefreshPlan plan, string scoreDbPath, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool estimateOfflineScoreRanking)
    {
        if (plan == null || bmsScores == null)
        {
            return;
        }
        Dictionary<string, BMSScore> scoresByHash = BuildScoreIndex(bmsScores);
        Dictionary<string, List<BMSFile>> filesByHash = BuildFileIndex(bmsFiles);
        var xmlUpdatedHashes = new HashSet<string>(plan.XmlRows.Where(data => data != null && !string.IsNullOrWhiteSpace(data.hash)).Select(data => data.hash), StringComparer.OrdinalIgnoreCase);
        int offlineEstimateXmlLoadCount = plan.Result.OfflineEstimateXmlLoadCount;
        foreach (LR2IRData irData in plan.DbRows)
        {
            if (irData != null && !xmlUpdatedHashes.Contains(irData.hash))
            {
                plan.XmlLookupsByHash.TryGetValue(
                    irData.hash,
                    out Lr2IrRankingLookup lookup);
                BMSScore changedScore = ApplyIrDataToScoresAndFilesIndexed(
                    irData,
                    lookup,
                    scoreDbPath,
                    bmsScores,
                    scoresByHash,
                    filesByHash,
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
            BMSScore changedScore = ApplyIrDataToScoresAndFilesIndexed(
                irData,
                lookup,
                scoreDbPath,
                bmsScores,
                scoresByHash,
                filesByHash,
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
