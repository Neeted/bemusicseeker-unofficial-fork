using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
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
        public List<LR2IRData> DbRows { get; } = new List<LR2IRData>();

        public List<LR2IRData> XmlRows { get; } = new List<LR2IRData>();

        public Dictionary<string, LR2IRCache> XmlCachesByHash { get; } = new Dictionary<string, LR2IRCache>(StringComparer.OrdinalIgnoreCase);

        public IrCacheRefreshResult Result { get; } = new IrCacheRefreshResult();
    }

    private sealed class RankingCacheReloadResult
    {
        public LR2IRData IrData { get; set; }

        public LR2IRCache FullCache { get; set; }

        public int ScoresParsed { get; set; }

        public bool UsedFallback { get; set; }

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
        foreach (var item in bmsFiles.Join(bmsScores, (BMSFile file) => file.hash, (BMSScore score) => score.hash, (BMSFile file, BMSScore score) => new
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

    public void ApplyIrDataToScoresAndFiles(LR2IRData data, LR2IRCache cache, string scoreDbPath, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool skipEstimateOfflineScoreRanking)
    {
        if (data == null || bmsScores == null)
        {
            return;
        }
        Dictionary<string, BMSScore> scoresByHash = BuildScoreIndex(bmsScores);
        Dictionary<string, List<BMSFile>> filesByHash = BuildFileIndex(bmsFiles);
        int offlineEstimateXmlLoadCount = 0;
        ApplyIrDataToScoresAndFilesIndexed(data, cache, scoreDbPath, bmsScores, scoresByHash, filesByHash, skipEstimateOfflineScoreRanking, ref offlineEstimateXmlLoadCount);
    }

    private static Dictionary<string, BMSScore> BuildScoreIndex(IEnumerable<BMSScore> bmsScores)
    {
        Dictionary<string, BMSScore> scoresByHash = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSScore score in bmsScores ?? Enumerable.Empty<BMSScore>())
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
        Dictionary<string, List<BMSFile>> filesByHash = new Dictionary<string, List<BMSFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile bmsFile in bmsFiles ?? Enumerable.Empty<BMSFile>())
        {
            if (bmsFile == null || string.IsNullOrWhiteSpace(bmsFile.hash))
            {
                continue;
            }
            if (!filesByHash.TryGetValue(bmsFile.hash, out List<BMSFile> files))
            {
                files = new List<BMSFile>();
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

    private void ApplyIrDataToScoresAndFilesIndexed(
        LR2IRData data,
        LR2IRCache cache,
        string scoreDbPath,
        List<BMSScore> bmsScores,
        Dictionary<string, BMSScore> scoresByHash,
        IReadOnlyDictionary<string, List<BMSFile>> filesByHash,
        bool skipEstimateOfflineScoreRanking,
        ref int offlineEstimateXmlLoadCount)
    {
        if (data == null || bmsScores == null || string.IsNullOrWhiteSpace(data.hash))
        {
            return;
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
                    if (!skipEstimateOfflineScoreRanking)
                    {
                        cache = EnsureIrCacheLoaded(cache, cachePath, ref offlineEstimateXmlLoadCount);
                        if (cache != null)
                        {
                            score.ranking = cache.GetRankFromScore(score.score);
                        }
                    }
                    score.rankingNum = data.players_num;
                    score.rankingLastupdate = data.lastupdate;
                }
                score.SetStdDevVal(data.average, data.sigma);
                score.SetScoreDiffic(data.average, data.sigma);
                return;
            }
            score = new BMSScore(data);
            bmsScores.Add(score);
            scoresByHash[data.hash] = score;
            AttachScoreToFiles(score, filesByHash);
            return;
        }
        if (score != null)
        {
            if (!skipEstimateOfflineScoreRanking)
            {
                cache = EnsureIrCacheLoaded(cache, cachePath, ref offlineEstimateXmlLoadCount);
                if (cache != null)
                {
                    score.ranking = cache.GetRankFromScore(score.score);
                }
            }
            score.rankingNum = data.players_num;
            score.rankingLastupdate = data.lastupdate;
            score.SetStdDevVal(data.average, data.sigma);
            score.SetScoreDiffic(data.average, data.sigma);
            return;
        }
        score = new BMSScore(data);
        bmsScores.Add(score);
        scoresByHash[data.hash] = score;
        AttachScoreToFiles(score, filesByHash);
    }

    public List<LR2IRScore> UpdateIrScoreTable(int lr2Id, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient, Regex lr2IrScoreRegex)
    {
        return UpdateIrScoreTableWithMetrics(lr2Id, dbGateway, irClient, lr2IrScoreRegex).ScoreTable;
    }

    public IrScorePrefetchResult PrefetchIrScoreTableWithMetrics(int lr2Id, IBmsLibraryIrClient irClient, Regex lr2IrScoreRegex)
    {
        IrScorePrefetchResult result = new IrScorePrefetchResult
        {
            Lr2Id = lr2Id
        };
        if (irClient == null || lr2IrScoreRegex == null || lr2Id == 0)
        {
            result.FailureReason = "unavailable";
            return result;
        }
        string playerXml;
        try
        {
            Stopwatch fetchStopwatch = Stopwatch.StartNew();
            playerXml = irClient.GetPlayerScoresXml(lr2Id);
            fetchStopwatch.Stop();
            result.XmlFetchMs = fetchStopwatch.ElapsedMilliseconds;
        }
        catch
        {
            result.FailureReason = "fetch_failed";
            return result;
        }
        try
        {
            Stopwatch parseStopwatch = Stopwatch.StartNew();
            result.ScoreTable.AddRange(ParsePlayerScoreXml(playerXml, lr2IrScoreRegex));
            parseStopwatch.Stop();
            result.XmlParseMs = parseStopwatch.ElapsedMilliseconds;
            result.ParsedRows = result.ScoreTable.Count;
        }
        catch
        {
            result.FailureReason = "parse_failed";
            return result;
        }
        try
        {
            Stopwatch digestStopwatch = Stopwatch.StartNew();
            result.ScoreDigestSha256 = ComputeScoreDigest(result.ScoreTable);
            digestStopwatch.Stop();
            result.DigestMs = digestStopwatch.ElapsedMilliseconds;
        }
        catch
        {
            result.FailureReason = "digest_failed";
        }
        return result;
    }

    public IrScoreTableUpdateResult UpdateIrScoreTableWithMetrics(int lr2Id, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient, Regex lr2IrScoreRegex, IrScorePrefetchResult prefetchedScore = null)
    {
        IrScoreTableUpdateResult result = new IrScoreTableUpdateResult();
        if (dbGateway == null || irClient == null || lr2IrScoreRegex == null || lr2Id == 0 || string.IsNullOrWhiteSpace(dbGateway.ScoreDbPath))
        {
            result.SkipReason = "unavailable";
            return result;
        }
        string playerXml;
        LR2SongDBExtended.ir_score_refresh_metadata metadata = null;
        try
        {
            metadata = dbGateway.LoadIrScoreRefreshMetadata(lr2Id, out long metadataDbLockWaitMs);
            result.DbLockWaitMs += metadataDbLockWaitMs;
        }
        catch
        {
        }
        List<LR2IRScore> scoreTable;
        string scoreDigest;
        if (prefetchedScore != null && prefetchedScore.Succeeded && prefetchedScore.Lr2Id == lr2Id)
        {
            scoreTable = prefetchedScore.ScoreTable.ToList();
            scoreDigest = prefetchedScore.ScoreDigestSha256;
            result.PrefetchUsed = true;
            result.PrefetchXmlFetchMs = prefetchedScore.XmlFetchMs;
            result.PrefetchXmlParseMs = prefetchedScore.XmlParseMs;
            result.PrefetchDigestMs = prefetchedScore.DigestMs;
            result.ParsedRows = scoreTable.Count;
        }
        else
        {
            try
            {
                Stopwatch fetchStopwatch = Stopwatch.StartNew();
                playerXml = irClient.GetPlayerScoresXml(lr2Id);
                fetchStopwatch.Stop();
                result.XmlFetchMs = fetchStopwatch.ElapsedMilliseconds;
            }
            catch
            {
                result.SkipReason = "unavailable";
                return result;
            }
            try
            {
                Stopwatch parseStopwatch = Stopwatch.StartNew();
                scoreTable = ParsePlayerScoreXml(playerXml, lr2IrScoreRegex);
                parseStopwatch.Stop();
                result.XmlParseMs = parseStopwatch.ElapsedMilliseconds;
                result.ParsedRows = scoreTable.Count;
            }
            catch
            {
                result.SkipReason = "unavailable";
                return result;
            }
            try
            {
                Stopwatch digestStopwatch = Stopwatch.StartNew();
                scoreDigest = ComputeScoreDigest(scoreTable);
                digestStopwatch.Stop();
                result.DigestMs = digestStopwatch.ElapsedMilliseconds;
            }
            catch
            {
                result.SkipReason = "unavailable";
                return result;
            }
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
            Stopwatch dbStopwatch = Stopwatch.StartNew();
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
        return (from e in lr2IrScoreRegex.Matches(playerXml ?? string.Empty).Cast<Match>().Select(delegate (Match m)
                {
                    try
                    {
                        return new LR2IRScore(m.Groups[1].Value)
                        {
                            clear = ClearTypeStorageConverter.FromLr2Value(int.Parse(m.Groups[2].Value)),
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
                select e.First()).ToList();
    }

    private static List<LR2IRScore> LoadExistingIrScoresForDigestComparison(BmsLibraryDbGateway dbGateway, IrScoreTableUpdateResult result)
    {
        try
        {
            Stopwatch loadStopwatch = Stopwatch.StartNew();
            IrScoreRowsLoadResult loadResult = dbGateway.LoadIrScoreRowsWithMetrics();
            loadStopwatch.Stop();
            List<LR2IRScore> scoreTable = loadResult.Rows;
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
            result.DbLockWaitMs += loadResult.DbLockWaitMs;
            result.LoadedRows = scoreTable?.Count ?? 0;
            return scoreTable ?? new List<LR2IRScore>();
        }
        catch
        {
            return new List<LR2IRScore>();
        }
    }

    private static void LoadExistingIrScores(BmsLibraryDbGateway dbGateway, IrScoreTableUpdateResult result)
    {
        try
        {
            Stopwatch loadStopwatch = Stopwatch.StartNew();
            IrScoreRowsLoadResult loadResult = dbGateway.LoadIrScoreRowsWithMetrics();
            result.ScoreTable = loadResult.Rows;
            loadStopwatch.Stop();
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
            result.DbLockWaitMs += loadResult.DbLockWaitMs;
            result.LoadedRows = result.ScoreTable?.Count ?? 0;
        }
        catch
        {
            result.ScoreTable = new List<LR2IRScore>();
        }
    }

    internal static string ComputeScoreDigest(IEnumerable<LR2IRScore> scoreTable)
    {
        StringBuilder builder = new StringBuilder();
        foreach (LR2IRScore score in (scoreTable ?? Enumerable.Empty<LR2IRScore>())
            .Where((LR2IRScore score) => score != null && !string.IsNullOrWhiteSpace(score.hash))
            .OrderBy((LR2IRScore score) => score.hash, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(score.hash.ToLowerInvariant()).Append('\t')
                .Append(ClearTypeStorageConverter.ToLr2Value(score.clear)).Append('\t')
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
        using SHA256 sha256 = SHA256.Create();
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        StringBuilder builder = new StringBuilder(bytes.Length * 2);
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
        List<BMSScore> existingScores = currentScores.ToList();
        if (detectUnsentScores)
        {
            foreach (BMSFile item in (from ls in existingScores
                                      join os in scoreTable on ls.hash equals os.hash into os
                                      select new
                                      {
                                          bmsScore = ls,
                                          irScores = os.DefaultIfEmpty()
                                      } into grp
                                      from a in grp.irScores
                                      select new
                                      {
                                          bmsScore = grp.bmsScore,
                                          irScore = a
                                      } into b
                                      where b.irScore == null || b.bmsScore.score != b.irScore.score || b.bmsScore.minbp != b.irScore.minbp || (b.bmsScore.clear != b.irScore.clear && (b.bmsScore.clear != ClearType.PA || b.irScore.clear != ClearType.FC))
                                      select b).Join(bmsFiles ?? Enumerable.Empty<BMSFile>(), b => b.bmsScore.hash, bmsFile => bmsFile.hash, (a, bmsFile) => bmsFile))
            {
                item.status |= BMSFile.BMSFileStatus.SCORE_UNSENT;
            }
        }
        IEnumerable<BMSScore> appendedScores = (from os in scoreTable
                                                join ls in existingScores on os.hash equals ls.hash into ls
                                                select new
                                                {
                                                    irScore = os,
                                                    bmsScores = ls.DefaultIfEmpty(new BMSScore(os))
                                                } into grp
                                                from a in grp.bmsScores
                                                select new
                                                {
                                                    irScore = grp.irScore,
                                                    bmsScore = a
                                                }).Select(b =>
                                            {
                                                if (b.irScore.clear > b.bmsScore.clear)
                                                {
                                                    b.bmsScore.clear = b.irScore.clear;
                                                }
                                                if (b.irScore.score > b.bmsScore.score)
                                                {
                                                    b.bmsScore.Overwrite(b.irScore);
                                                }
                                                return b.bmsScore;
                                            }).Except(existingScores);
        List<BMSScore> mergedScores = existingScores.Concat(appendedScores).ToList();
        ApplyKnownScoresToFiles(bmsFiles, appendedScores);
        return mergedScores;
    }

    public List<LR2IRData> LoadIrData(int lr2Id, BmsLibraryDbGateway dbGateway)
    {
        return LoadIrDataWithMetrics(lr2Id, dbGateway).Rows;
    }

    public IrDataLoadResult LoadIrDataWithMetrics(int lr2Id, BmsLibraryDbGateway dbGateway)
    {
        return dbGateway?.LoadIrDataWithMetrics(lr2Id) ?? new IrDataLoadResult();
    }

    public List<BMSLibrary.IRDataCacheInfo> GetIRDataNeedUpdates(int lr2Id, IEnumerable<string> md5s, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient, Uri rankingInfoUrl)
    {
        List<BMSLibrary.IRDataCacheInfo> outer = irClient.GetRankingInfo(rankingInfoUrl, md5s);
        List<LR2IRData> inner = LoadIrData(lr2Id, dbGateway);
        return (from i in outer
                join d in inner on i.md5 equals d.hash into d
                select new
                {
                    irCacheInfo = i,
                    irDataDb = d.DefaultIfEmpty()
                } into grp
                from a in grp.irDataDb
                select new
                {
                    irCacheInfo = grp.irCacheInfo,
                    irDataDb = a
                } into b
                where b.irDataDb == null || b.irCacheInfo.lastupdate > b.irDataDb.lastupdate
                select b.irCacheInfo).ToList();
    }

    public List<BMSLibrary.IRDataCacheInfo> DownloadIRData(int lr2Id, IEnumerable<BMSLibrary.IRDataCacheInfo> cacheInfo, string irCacheDirPath, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient, Uri rankingDataUrl, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool skipEstimateOfflineScoreRanking)
    {
        List<BMSLibrary.IRDataCacheInfo> source = (cacheInfo ?? Enumerable.Empty<BMSLibrary.IRDataCacheInfo>()).ToList();
        List<BMSLibrary.IRDataCacheInfo> failed = new List<BMSLibrary.IRDataCacheInfo>();
        List<LR2IRData> irDataToBeCommitted = new List<LR2IRData>();
        object failedLock = new object();
        object commitLock = new object();
        source.AsParallel().WithDegreeOfParallelism(3).ForAll(delegate (BMSLibrary.IRDataCacheInfo info)
        {
            try
            {
                string cacheFilePath = Path.Combine(irCacheDirPath, info.md5 + ".xml");
                irClient.DownloadRankingData(rankingDataUrl, info.md5, cacheFilePath);
                LR2IRCache irCache = LoadIrCache(cacheFilePath);
                LR2IRData irData = irCache.GetLR2IRData(lr2Id);
                if (irData != null)
                {
                    lock (commitLock)
                    {
                        irDataToBeCommitted.Add(irData);
                    }
                    ApplyIrDataToScoresAndFiles(irData, irCache, dbGateway.ScoreDbPath, bmsScores, bmsFiles, skipEstimateOfflineScoreRanking);
                }
            }
            catch
            {
                lock (failedLock)
                {
                    failed.Add(info);
                }
            }
        });
        dbGateway.UpsertIrData(irDataToBeCommitted);
        return failed;
    }

    public BMSLibrary.IRSongInfo GetIRSongInfoCache(string md5OrLr2BmsId, bool searchAggressively, IBmsLibraryIrClient irClient, Uri songInfoUrl)
    {
        if (string.IsNullOrWhiteSpace(md5OrLr2BmsId))
        {
            throw new ArgumentException("md5orlr2bmsid");
        }
        if (!LR2SongDB.md5HashRegex.IsMatch(md5OrLr2BmsId) && !Regex.IsMatch(md5OrLr2BmsId, "^[0-9]+$"))
        {
            throw new ArgumentException("md5orlr2bmsid");
        }
        try
        {
            return irClient.GetSongInfo(songInfoUrl, md5OrLr2BmsId, searchAggressively);
        }
        catch
        {
            return irClient.GetSongInfo(songInfoUrl, md5OrLr2BmsId, searchAggressively: false);
        }
    }

    public IrCacheRefreshResult RefreshRankingScoresFromCache(int lr2Id, string scoreDbPath, BmsLibraryDbGateway dbGateway, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool skipEstimateOfflineScoreRanking)
    {
        IrCacheRefreshPlan plan = BuildRankingScoresRefreshPlan(lr2Id, scoreDbPath, dbGateway);
        ApplyRankingScoresRefreshPlan(plan, scoreDbPath, bmsScores, bmsFiles, skipEstimateOfflineScoreRanking);
        return plan.Result;
    }

    internal IrCacheRefreshPlan PrepareRankingScoresRefreshPlanForLibrary(int lr2Id, string scoreDbPath, BmsLibraryDbGateway dbGateway)
    {
        return BuildRankingScoresRefreshPlan(lr2Id, scoreDbPath, dbGateway);
    }

    internal IrCacheRefreshResult ApplyPreparedRankingScoresRefreshPlanForLibrary(IrCacheRefreshPlan preparedPlan, string scoreDbPath, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool skipEstimateOfflineScoreRanking)
    {
        IrCacheRefreshPlan plan = preparedPlan ?? new IrCacheRefreshPlan();
        ApplyRankingScoresRefreshPlan(plan, scoreDbPath, bmsScores, bmsFiles, skipEstimateOfflineScoreRanking);
        return plan.Result;
    }

    private IrCacheRefreshPlan BuildRankingScoresRefreshPlan(int lr2Id, string scoreDbPath, BmsLibraryDbGateway dbGateway)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        IrCacheRefreshPlan plan = new IrCacheRefreshPlan();
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
        plan.DbRows.AddRange(irDataDb.Where((LR2IRData data) => data != null && !string.IsNullOrWhiteSpace(data.hash)));
        result.DbRows = plan.DbRows.Count;
        Stopwatch indexStopwatch = Stopwatch.StartNew();
        Dictionary<string, LR2IRData> irDataByHash = new Dictionary<string, LR2IRData>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2IRData data in plan.DbRows)
        {
            if (!irDataByHash.ContainsKey(data.hash))
            {
                irDataByHash.Add(data.hash, data);
            }
        }
        indexStopwatch.Stop();
        result.IndexBuildMs = indexStopwatch.ElapsedMilliseconds;

        ConcurrentBag<LR2IRData> irDataToBeCommitted = new ConcurrentBag<LR2IRData>();
        int reloadDegree = ResolveRankingCacheXmlReloadDegree();
        result.XmlReloadDegree = reloadDegree;
        Stopwatch xmlCheckStopwatch = Stopwatch.StartNew();
        List<string> cacheFilePaths = Directory.EnumerateFiles(cacheDirectoryPath).ToList();
        result.CacheFilesScanned = cacheFilePaths.Count;
        List<string> reloadTargets = cacheFilePaths.AsParallel().WithDegreeOfParallelism(reloadDegree).Where(delegate (string filePath)
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
                using (StreamReader reader = new StreamReader(filePath))
                {
                    tail = reader.Tail(33, 19);
                }
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

        Stopwatch xmlReloadStopwatch = Stopwatch.StartNew();
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
            if (reloadResult.UsedFallback)
            {
                result.XmlFallbackLoadCount++;
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
            if (reloadResult.FullCache != null)
            {
                plan.XmlCachesByHash[irData.hash] = reloadResult.FullCache;
            }
        });
        xmlReloadStopwatch.Stop();
        result.XmlReloadMs = xmlReloadStopwatch.ElapsedMilliseconds;
        result.CacheFilesReloaded = reloadTargets.Count;
        List<LR2IRData> upsertRows = irDataToBeCommitted.Where((LR2IRData data) => data != null).ToList();
        result.IrDataUpsertCount = upsertRows.Count;
        if (upsertRows.Count > 0)
        {
            Stopwatch upsertStopwatch = Stopwatch.StartNew();
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
        List<string> targets = (reloadTargets ?? Enumerable.Empty<string>()).ToList();
        if (targets.Count == 0)
        {
            return;
        }
        int workerCount = Math.Max(1, Math.Min(degree, targets.Count));
        using BlockingCollection<string> workQueue = new BlockingCollection<string>(workerCount * 2);
        using BlockingCollection<RankingCacheReloadResult> resultQueue = new BlockingCollection<RankingCacheReloadResult>(workerCount * 4);
        Task collector = Task.Run(delegate
        {
            foreach (RankingCacheReloadResult result in resultQueue.GetConsumingEnumerable())
            {
                consumeResult(result);
            }
        });
        Task[] workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(delegate
        {
            foreach (string filePath in workQueue.GetConsumingEnumerable())
            {
                resultQueue.Add(LoadRankingCacheSummaryWithFallback(filePath, lr2Id));
            }
        })).ToArray();
        foreach (string filePath in targets)
        {
            workQueue.Add(filePath);
        }
        workQueue.CompleteAdding();
        Task.WaitAll(workers);
        resultQueue.CompleteAdding();
        collector.Wait();
    }

    private RankingCacheReloadResult LoadRankingCacheSummaryWithFallback(string filePath, int lr2Id)
    {
        RankingCacheReloadResult result = new RankingCacheReloadResult();
        try
        {
            string md5 = Path.GetFileNameWithoutExtension(filePath);
            DateTime cacheLastWriteTime = File.GetLastWriteTime(filePath);
            string xml = File.ReadAllText(filePath, Encoding.GetEncoding("shift_jis"));
            if (TryParseRankingCacheSummary(xml, md5, lr2Id, cacheLastWriteTime, out LR2IRData irData, out int scoresParsed))
            {
                result.IrData = irData;
                result.ScoresParsed = scoresParsed;
                return result;
            }
        }
        catch
        {
        }
        try
        {
            LR2IRCache cache = LoadIrCache(filePath);
            LR2IRData irData = cache.GetLR2IRData(lr2Id);
            if (irData == null)
            {
                result.Failed = true;
                return result;
            }
            result.IrData = irData;
            result.FullCache = cache;
            result.ScoresParsed = cache.GetRankingNum();
            result.UsedFallback = true;
            return result;
        }
        catch
        {
            result.Failed = true;
            return result;
        }
    }

    internal static bool TryParseRankingCacheSummary(string rankingXml, string md5, int lr2Id, DateTime cacheLastWriteTime, out LR2IRData irData, out int scoresParsed)
    {
        irData = null;
        scoresParsed = 0;
        if (string.IsNullOrEmpty(rankingXml) || string.IsNullOrWhiteSpace(md5) || !LR2SongDB.md5HashRegex.IsMatch(md5))
        {
            return false;
        }
        DateTime lastUpdate = ParseRankingCacheLastUpdate(rankingXml, cacheLastWriteTime);
        Dictionary<int, int> scoreFrequency = new Dictionary<int, int>();
        Dictionary<int, List<int>> notesByScore = new Dictionary<int, List<int>>();
        long sum = 0L;
        double sumSquares = 0.0;
        int bestTargetScore = int.MinValue;
        LR2IRData bestTarget = null;
        int firstNotes = 0;
        int position = 0;
        while ((position = rankingXml.IndexOf("<score>", position, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int blockStart = position + "<score>".Length;
            int blockEnd = rankingXml.IndexOf("</score>", blockStart, StringComparison.OrdinalIgnoreCase);
            if (blockEnd < 0)
            {
                break;
            }
            if (TryParseRankingScoreBlock(rankingXml, blockStart, blockEnd, md5, lastUpdate, cacheLastWriteTime, out LR2IRData row))
            {
                int score = row.score;
                scoresParsed++;
                sum += score;
                sumSquares += (double)score * score;
                if (!scoreFrequency.ContainsKey(score))
                {
                    scoreFrequency.Add(score, 0);
                    notesByScore.Add(score, new List<int>());
                }
                scoreFrequency[score]++;
                notesByScore[score].Add(row.notes);
                if (scoresParsed == 1)
                {
                    firstNotes = row.notes;
                }
                if (row.lr2id == lr2Id && (bestTarget == null || score > bestTargetScore))
                {
                    bestTarget = row;
                    bestTargetScore = score;
                }
            }
            position = blockEnd + "</score>".Length;
        }
        if (scoresParsed == 0)
        {
            return false;
        }
        if (bestTarget != null)
        {
            irData = bestTarget;
            irData.rank = CountScoresGreaterThan(scoreFrequency, bestTargetScore) + 1;
        }
        else
        {
            irData = new LR2IRData(md5)
            {
                lr2id = lr2Id,
                notes = GetMedianScoreNotes(scoreFrequency, notesByScore, scoresParsed, firstNotes),
                clear = ClearType.NO_PLAY,
                pg = 0,
                gr = 0,
                rank = -1,
                lastupdate = lastUpdate,
                lastcacheupdate = cacheLastWriteTime
            };
        }
        double average = (double)sum / scoresParsed;
        irData.players_num = scoresParsed;
        irData.average = average;
        irData.sigma = scoresParsed > 1
            ? Math.Sqrt(Math.Max(0.0, (sumSquares - (sum * (double)sum / scoresParsed)) / (scoresParsed - 1)))
            : 0.0;
        return true;
    }

    private static DateTime ParseRankingCacheLastUpdate(string rankingXml, DateTime cacheLastWriteTime)
    {
        string trimmed = rankingXml.TrimEnd('\0');
        if (trimmed.Length > 0 && DateTime.TryParseExact(trimmed.Tail(33, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime lastUpdate))
        {
            return lastUpdate;
        }
        return cacheLastWriteTime;
    }

    private static bool TryParseRankingScoreBlock(string xml, int blockStart, int blockEnd, string md5, DateTime lastUpdate, DateTime cacheLastWriteTime, out LR2IRData row)
    {
        row = null;
        if (!TryReadIntTag(xml, "id", blockStart, blockEnd, out int parsedLr2Id)
            || !TryReadIntTag(xml, "clear", blockStart, blockEnd, out int clear)
            || !TryReadIntTag(xml, "notes", blockStart, blockEnd, out int notes)
            || !TryReadIntTag(xml, "combo", blockStart, blockEnd, out int combo)
            || !TryReadIntTag(xml, "pg", blockStart, blockEnd, out int pg)
            || !TryReadIntTag(xml, "gr", blockStart, blockEnd, out int gr)
            || !TryReadIntTag(xml, "minbp", blockStart, blockEnd, out int minbp))
        {
            return false;
        }
        row = new LR2IRData(md5)
        {
            lr2id = parsedLr2Id,
            clear = ClearTypeStorageConverter.FromLr2Value(clear),
            notes = notes,
            combo = combo,
            pg = pg,
            gr = gr,
            minbp = minbp,
            lastupdate = lastUpdate,
            lastcacheupdate = cacheLastWriteTime
        };
        return true;
    }

    private static bool TryReadIntTag(string xml, string tagName, int blockStart, int blockEnd, out int value)
    {
        value = 0;
        string openTag = "<" + tagName + ">";
        string closeTag = "</" + tagName + ">";
        int openIndex = xml.IndexOf(openTag, blockStart, blockEnd - blockStart, StringComparison.OrdinalIgnoreCase);
        if (openIndex < 0)
        {
            return false;
        }
        int valueStart = openIndex + openTag.Length;
        if (valueStart > blockEnd)
        {
            return false;
        }
        int closeIndex = xml.IndexOf(closeTag, valueStart, blockEnd - valueStart, StringComparison.OrdinalIgnoreCase);
        return closeIndex >= 0
            && int.TryParse(xml.Substring(valueStart, closeIndex - valueStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= 0;
    }

    private static int CountScoresGreaterThan(Dictionary<int, int> scoreFrequency, int targetScore)
    {
        int count = 0;
        foreach (KeyValuePair<int, int> pair in scoreFrequency)
        {
            if (pair.Key > targetScore)
            {
                count += pair.Value;
            }
        }
        return count;
    }

    private static int GetMedianScoreNotes(Dictionary<int, int> scoreFrequency, Dictionary<int, List<int>> notesByScore, int totalScores, int fallbackNotes)
    {
        int targetIndex = totalScores / 2;
        int offset = 0;
        foreach (int score in scoreFrequency.Keys.OrderByDescending((int key) => key))
        {
            int bucketCount = scoreFrequency[score];
            int nextOffset = offset + bucketCount;
            if (nextOffset > targetIndex && notesByScore.TryGetValue(score, out List<int> notes))
            {
                int notesIndex = targetIndex - offset;
                if (notesIndex >= 0 && notesIndex < notes.Count)
                {
                    return notes[notesIndex];
                }
                return notes.Count > 0 ? notes[0] : fallbackNotes;
            }
            offset = nextOffset;
        }
        return fallbackNotes;
    }

    private void ApplyRankingScoresRefreshPlan(IrCacheRefreshPlan plan, string scoreDbPath, List<BMSScore> bmsScores, IEnumerable<BMSFile> bmsFiles, bool skipEstimateOfflineScoreRanking)
    {
        if (plan == null || bmsScores == null)
        {
            return;
        }
        Dictionary<string, BMSScore> scoresByHash = BuildScoreIndex(bmsScores);
        Dictionary<string, List<BMSFile>> filesByHash = BuildFileIndex(bmsFiles);
        HashSet<string> xmlUpdatedHashes = new HashSet<string>(plan.XmlRows.Where((LR2IRData data) => data != null && !string.IsNullOrWhiteSpace(data.hash)).Select((LR2IRData data) => data.hash), StringComparer.OrdinalIgnoreCase);
        int offlineEstimateXmlLoadCount = 0;
        foreach (LR2IRData irData in plan.DbRows)
        {
            if (irData != null && !xmlUpdatedHashes.Contains(irData.hash))
            {
                ApplyIrDataToScoresAndFilesIndexed(irData, null, scoreDbPath, bmsScores, scoresByHash, filesByHash, skipEstimateOfflineScoreRanking, ref offlineEstimateXmlLoadCount);
                plan.Result.DbFallbackAppliedCount++;
            }
        }
        foreach (LR2IRData irData in plan.XmlRows)
        {
            if (irData == null)
            {
                continue;
            }
            plan.XmlCachesByHash.TryGetValue(irData.hash, out LR2IRCache cache);
            ApplyIrDataToScoresAndFilesIndexed(irData, cache, scoreDbPath, bmsScores, scoresByHash, filesByHash, skipEstimateOfflineScoreRanking, ref offlineEstimateXmlLoadCount);
            plan.Result.XmlAppliedCount++;
        }
        plan.Result.OfflineEstimateXmlLoadCount = offlineEstimateXmlLoadCount;
    }

    private static LR2IRCache EnsureIrCacheLoaded(LR2IRCache cache, string cachePath, ref int loadCount)
    {
        if (cache != null || string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            return cache;
        }
        try
        {
            LR2IRCache loaded = new LR2IRCache(File.ReadAllText(cachePath, Encoding.GetEncoding("shift_jis")), Path.GetFileNameWithoutExtension(cachePath), File.GetLastWriteTime(cachePath));
            loadCount++;
            return loaded;
        }
        catch
        {
            return null;
        }
    }
}
