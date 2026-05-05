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
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Applies IR cache/network data to snapshots owned by BMSLibrary.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryIrService
{
    internal sealed class IrCacheRefreshPlan
    {
        public List<LR2IRData> DbRows { get; } = new List<LR2IRData>();

        public List<LR2IRData> XmlRows { get; } = new List<LR2IRData>();

        public Dictionary<string, LR2IRCache> XmlCachesByHash { get; } = new Dictionary<string, LR2IRCache>(StringComparer.OrdinalIgnoreCase);

        public IrCacheRefreshResult Result { get; } = new IrCacheRefreshResult();
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
        if (bmsFiles == null || scoresByHash == null || scoresByHash.Count == 0)
        {
            return 0;
        }
        int matchedScoreCount = 0;
        foreach (BMSFile bmsFile in bmsFiles)
        {
            if (bmsFile != null && !string.IsNullOrWhiteSpace(bmsFile.hash) && scoresByHash.TryGetValue(bmsFile.hash, out BMSScore value))
            {
                bmsFile.bmsScore = value;
                matchedScoreCount++;
            }
        }
        return matchedScoreCount;
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

    public IrScoreTableUpdateResult UpdateIrScoreTableWithMetrics(int lr2Id, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient, Regex lr2IrScoreRegex)
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
            metadata = dbGateway.LoadIrScoreRefreshMetadata(lr2Id);
        }
        catch
        {
        }
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
        List<LR2IRScore> scoreTable;
        try
        {
            Stopwatch parseStopwatch = Stopwatch.StartNew();
            scoreTable = (from e in lr2IrScoreRegex.Matches(playerXml).Cast<Match>().Select(delegate (Match m)
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
            parseStopwatch.Stop();
            result.XmlParseMs = parseStopwatch.ElapsedMilliseconds;
            result.ParsedRows = scoreTable.Count;
        }
        catch
        {
            result.SkipReason = "unavailable";
            return result;
        }
        string scoreDigest;
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

    private static List<LR2IRScore> LoadExistingIrScoresForDigestComparison(BmsLibraryDbGateway dbGateway, IrScoreTableUpdateResult result)
    {
        try
        {
            Stopwatch loadStopwatch = Stopwatch.StartNew();
            List<LR2IRScore> scoreTable = dbGateway.LoadIrScoreRows();
            loadStopwatch.Stop();
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
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
            result.ScoreTable = dbGateway.LoadIrScoreRows();
            loadStopwatch.Stop();
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
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
        Stopwatch xmlCheckStopwatch = Stopwatch.StartNew();
        List<string> cacheFilePaths = Directory.EnumerateFiles(cacheDirectoryPath).ToList();
        result.CacheFilesScanned = cacheFilePaths.Count;
        List<string> reloadTargets = cacheFilePaths.AsParallel().Where(delegate (string filePath)
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
        object xmlRowsLock = new object();
        reloadTargets.AsParallel().ForAll(delegate (string filePath)
        {
            LR2IRCache irCache;
            try
            {
                irCache = LoadIrCache(filePath);
            }
            catch
            {
                return;
            }
            LR2IRData irData = irCache.GetLR2IRData(lr2Id);
            if (irData == null)
            {
                return;
            }
            irDataToBeCommitted.Add(irData);
            lock (xmlRowsLock)
            {
                plan.XmlRows.Add(irData);
                plan.XmlCachesByHash[irData.hash] = irCache;
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
            dbGateway.UpsertIrData(upsertRows);
            upsertStopwatch.Stop();
            result.UpsertMs = upsertStopwatch.ElapsedMilliseconds;
        }
        totalStopwatch.Stop();
        result.ElapsedMs = totalStopwatch.ElapsedMilliseconds;
        return plan;
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
