using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryIrService
{
    public void ApplyKnownScoresToFiles(IEnumerable<BMSFile> bmsFiles, IEnumerable<BMSScore> bmsScores)
    {
        if (bmsFiles == null || bmsScores == null)
        {
            return;
        }
        foreach (var item in bmsFiles.Join(bmsScores, (BMSFile file) => file.hash, (BMSScore score) => score.hash, (BMSFile file, BMSScore score) => new
        {
            File = file,
            Score = score
        }))
        {
            item.File.bmsScore = item.Score;
        }
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
        string cachePath = string.IsNullOrWhiteSpace(scoreDbPath)
            ? null
            : Path.Combine(Path.Combine(Path.GetDirectoryName(scoreDbPath), "..\\..\\Ir"), data.hash + ".xml");
        BMSScore score = bmsScores.FirstOrDefault((BMSScore s) => s.hash == data.hash);
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
                        cache = EnsureIrCacheLoaded(cache, cachePath);
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
            ApplyKnownScoresToFiles(bmsFiles, new[] { score });
            return;
        }
        if (score != null)
        {
            if (!skipEstimateOfflineScoreRanking)
            {
                cache = EnsureIrCacheLoaded(cache, cachePath);
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
        ApplyKnownScoresToFiles(bmsFiles, new[] { score });
    }

    public List<LR2IRScore> UpdateIrScoreTable(int lr2Id, BmsLibraryDbGateway dbGateway, IBmsLibraryIrClient irClient, Regex lr2IrScoreRegex)
    {
        if (dbGateway == null || irClient == null || lr2IrScoreRegex == null || lr2Id == 0 || string.IsNullOrWhiteSpace(dbGateway.ScoreDbPath))
        {
            return null;
        }
        string playerXml;
        try
        {
            playerXml = irClient.GetPlayerScoresXml(lr2Id);
        }
        catch
        {
            return null;
        }
        List<LR2IRScore> scoreTable;
        try
        {
            scoreTable = (from e in lr2IrScoreRegex.Matches(playerXml).Cast<Match>().Select(delegate (Match m)
                {
                    try
                    {
                        return new LR2IRScore(m.Groups[1].Value)
                        {
                            clear = (ClearType)int.Parse(m.Groups[2].Value),
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
        catch
        {
            return null;
        }
        try
        {
            dbGateway.ReplaceIrScoreTable(scoreTable);
        }
        catch
        {
            return null;
        }
        return scoreTable;
    }

    public List<BMSScore> UpdateBmsScores(List<LR2IRScore> scoreTable, List<BMSScore> currentScores, IEnumerable<BMSFile> bmsFiles)
    {
        if (scoreTable == null || currentScores == null)
        {
            return currentScores;
        }
        List<BMSScore> existingScores = currentScores.ToList();
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
        return dbGateway?.LoadIrData(lr2Id) ?? new List<LR2IRData>();
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

    private static LR2IRCache EnsureIrCacheLoaded(LR2IRCache cache, string cachePath)
    {
        if (cache != null || string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            return cache;
        }
        try
        {
            return new LR2IRCache(File.ReadAllText(cachePath, Encoding.GetEncoding("shift_jis")), Path.GetFileNameWithoutExtension(cachePath), File.GetLastWriteTime(cachePath));
        }
        catch
        {
            return null;
        }
    }
}
