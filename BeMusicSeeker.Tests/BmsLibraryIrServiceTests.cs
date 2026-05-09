using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Codeplex.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryIrServiceTests
{
    [TestMethod]
    public void ApplyKnownScoresToFiles_AssignsMatchingScoreOnly()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();
        TestableBmsFile matched = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        TestableBmsFile unmatched = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        BMSScore score = new BMSScore
        {
            hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            perfect = 500,
            great = 234
        };

        service.ApplyKnownScoresToFiles(new BMSFile[] { matched, unmatched }, new BMSScore[] { score });

        Assert.AreSame(score, matched.bmsScore);
        Assert.IsNull(unmatched.bmsScore);
    }

    [TestMethod]
    public void ApplyKnownScoresToFilesAndCount_ReturnsMatchedScoreCount()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();
        TestableBmsFile matched = CreateFile("cccccccccccccccccccccccccccccccc");
        TestableBmsFile unmatched = CreateFile("dddddddddddddddddddddddddddddddd");
        BMSScore score = new BMSScore
        {
            hash = "cccccccccccccccccccccccccccccccc"
        };

        int matchedScoreCount = service.ApplyKnownScoresToFilesAndCount(new BMSFile[] { matched, unmatched }, new BMSScore[] { score });

        Assert.AreEqual(1, matchedScoreCount);
        Assert.AreSame(score, matched.bmsScore);
        Assert.IsNull(unmatched.bmsScore);
    }

    [TestMethod]
    public void ApplyKnownScoresToFilesAndCount_ReturnsZeroWhenTargetsAreEmpty()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();

        int matchedScoreCount = service.ApplyKnownScoresToFilesAndCount(new BMSFile[0], new BMSScore[0]);

        Assert.AreEqual(0, matchedScoreCount);
    }

    [TestMethod]
    public void ApplyKnownScoresToFilesAndCount_UsesHashIndexSnapshot()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();
        TestableBmsFile matched = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        TestableBmsFile unmatched = CreateFile("ffffffffffffffffffffffffffffffff");
        BMSScore score = new BMSScore
        {
            hash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            perfect = 321,
            great = 123
        };
        Dictionary<string, BMSScore> scoresByHash = new Dictionary<string, BMSScore>(System.StringComparer.OrdinalIgnoreCase)
        {
            [score.hash] = score
        };

        int matchedScoreCount = service.ApplyKnownScoresToFilesAndCount(new BMSFile[] { matched, unmatched }, scoresByHash);

        Assert.AreEqual(1, matchedScoreCount);
        Assert.AreSame(score, matched.bmsScore);
        Assert.IsNull(unmatched.bmsScore);
    }

    [TestMethod]
    public void ApplyKnownScoresToFilesAndCount_PrefersSha256ScoreWhenBothIndexesAreProvided()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();
        TestableBmsFile file = CreateFile("12121212121212121212121212121212");
        file.SetSha256("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BMSScore lr2Score = new BMSScore
        {
            hash = file.hash,
            clear = ClearType.FAILED,
            perfect = 10,
            great = 20
        };
        BMSScore beatorajaScore = new BMSScore
        {
            hash = file.sha256,
            clear = ClearType.INVALID,
            perfect = 400,
            great = 100,
            totalnotes = 500,
            maxcombo = 450,
            minbp = 12,
            rank = RankType.AAA
        };
        Dictionary<string, BMSScore> scoresByHash = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
        {
            [lr2Score.hash] = lr2Score
        };
        Dictionary<string, BMSScore> scoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
        {
            [beatorajaScore.hash] = beatorajaScore
        };

        int matchedScoreCount = service.ApplyKnownScoresToFilesAndCount(new BMSFile[] { file }, scoresByHash, scoresBySha256);

        Assert.AreEqual(1, matchedScoreCount);
        Assert.AreNotSame(beatorajaScore, file.bmsScore);
        Assert.AreEqual(file.hash, file.bmsScore.hash);
        Assert.AreEqual(ClearType.INVALID, file.bmsScore.clear);
        Assert.AreEqual("ASSIST", file.ClearDisplayText);
        Assert.AreEqual(900, file.bmsScore.score);
        Assert.AreEqual(450, file.maxcombo);
        Assert.AreEqual(12, file.minbp);
    }

    [TestMethod]
    public void BeatorajaScoreDbLoader_ReadsModeZeroScoresOnly()
    {
        string rootDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerBeatorajaScoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectoryPath);
        string scoreDbPath = Path.Combine(rootDirectoryPath, "score.db");
        try
        {
            using (SQLiteConnection connection = new SQLiteConnection(scoreDbPath))
            {
                connection.Execute(
                    "CREATE TABLE score (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER, PRIMARY KEY(sha256, mode));");
                connection.Execute(
                    "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 0, 2, 100, 20, 30, 10, 200, 180, 5, 7, 3);
                connection.Execute(
                    "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 10000, 7, 500, 0, 0, 0, 500, 500, 0, 1, 1);
            }

            BeatorajaScoreDbLoader loader = new BeatorajaScoreDbLoader();
            Dictionary<string, BMSScore> scores = loader.LoadModeZeroScores(scoreDbPath);

            Assert.AreEqual(1, scores.Count);
            Assert.IsTrue(scores.TryGetValue("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", out BMSScore score));
            Assert.AreEqual(ClearType.INVALID, score.clear);
            Assert.AreEqual(120, score.perfect);
            Assert.AreEqual(40, score.great);
            Assert.AreEqual(200, score.totalnotes);
            Assert.AreEqual(180, score.maxcombo);
            Assert.AreEqual(5, score.minbp);
            Assert.AreEqual(280, score.score);
            Assert.AreEqual(70, score.rate);
            Assert.AreEqual(RankType.A, score.rank);
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootDirectoryPath))
                {
                    Directory.Delete(rootDirectoryPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public void LoadScoreTable_UsesBeatorajaAsExclusiveSourceWhenEnabled()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        CreateLr2ScoreDb(env.ScoreDbPath);
        string beatorajaScoreDbPath = Path.Combine(env.RootDirectoryPath, "beatoraja", "player1", "score.db");
        CreateBeatorajaScoreDb(beatorajaScoreDbPath);
        BmsLibraryInitializationService service = new BmsLibraryInitializationService();

        ScoreTableLoadResult result = service.LoadScoreTable(
            env.CreateGateway(),
            new BmsLibraryOptionsSnapshot
            {
                UseBeatorajaScoreDb = true,
                BeatorajaScoreDbPath = beatorajaScoreDbPath
            });

        Assert.AreEqual(ActiveScoreSource.Beatoraja, result.ActiveScoreSource);
        Assert.AreEqual(0, result.Scores.Count);
        Assert.AreEqual(0, result.LR2Id);
        Assert.AreEqual(1, result.BeatorajaScoresBySha256.Count);
        Assert.IsTrue(result.BeatorajaScoresBySha256.ContainsKey("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [TestMethod]
    public void LoadScoreTable_UsesLr2SourceWhenBeatorajaIsDisabled()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        CreateLr2ScoreDb(env.ScoreDbPath);
        BmsLibraryInitializationService service = new BmsLibraryInitializationService();

        ScoreTableLoadResult result = service.LoadScoreTable(
            env.CreateGateway(),
            new BmsLibraryOptionsSnapshot
            {
                UseBeatorajaScoreDb = false
            });

        Assert.AreEqual(ActiveScoreSource.Lr2, result.ActiveScoreSource);
        Assert.AreEqual(1, result.Scores.Count);
        Assert.AreEqual(123, result.LR2Id);
        Assert.AreEqual(0, result.BeatorajaScoresBySha256.Count);
    }

    [TestMethod]
    public void RefreshRankingScoresFromCache_UnchangedXmlAppliesDbRowWithoutUpsert()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService();
        string hash = "11111111111111111111111111111111";
        DateTime cacheUpdate = new DateTime(2026, 1, 1, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(new[]
        {
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
        });
        List<BMSScore> scores = new List<BMSScore>();
        TestableBmsFile file = CreateFile(hash);

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, scores, new BMSFile[] { file }, skipEstimateOfflineScoreRanking: true);

        Assert.AreEqual(1, result.CacheFilesScanned);
        Assert.AreEqual(0, result.CacheFilesReloaded);
        Assert.AreEqual(0, result.IrDataUpsertCount);
        Assert.AreEqual(1, result.DbFallbackAppliedCount);
        Assert.AreEqual(1, scores.Count);
        Assert.AreSame(scores[0], file.bmsScore);
        Assert.AreEqual(10, scores[0].ranking);
    }

    [TestMethod]
    public void LoadIrDataWithMetrics_FiltersByLr2IdInSqlLoader()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(new[]
        {
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
        });

        IrDataLoadResult result = gateway.LoadIrDataWithMetrics(123);

        Assert.AreEqual(1, result.Rows.Count);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result.Rows[0].hash);
        Assert.IsTrue(result.DbReadMs >= 0);
        Assert.IsTrue(result.MaterializeMs >= 0);
        Assert.IsTrue(result.ReadOnly);
        Assert.AreEqual(0L, result.DbLockWaitMs);
    }

    [TestMethod]
    public void RefreshRankingScoresFromCache_MissingDbRowReloadsXmlAndUpsertsOnce()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService(1);
        string hash = "22222222222222222222222222222222";
        DateTime cacheUpdate = new DateTime(2026, 2, 1, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        List<BMSScore> scores = new List<BMSScore>();
        TestableBmsFile file = CreateFile(hash);

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, scores, new BMSFile[] { file }, skipEstimateOfflineScoreRanking: true);

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
        Assert.AreSame(scores[0], file.bmsScore);
        Assert.AreEqual(1, gateway.LoadIrData(123).Count((LR2IRData data) => data.hash == hash));
    }

    [TestMethod]
    public void RankingCacheSummaryParser_TargetPlayerUsesUnifiedExpectedValues()
    {
        string hash = "21212121212121212121212121212121";
        DateTime cacheUpdate = new DateTime(2026, 4, 1, 12, 0, 0);
        DateTime lastUpdate = new DateTime(2026, 4, 2, 13, 14, 15);
        string xml = BuildRankingCacheXml(lastUpdate, new[]
        {
            new RankingScoreRow { Lr2Id = 100, Notes = 1000, Combo = 850, Pg = 400, Gr = 40, MinBp = 20, Clear = 3 },
            new RankingScoreRow { Lr2Id = 123, Notes = 1000, Combo = 950, Pg = 500, Gr = 20, MinBp = 5, Clear = 4 },
            new RankingScoreRow { Lr2Id = 200, Notes = 1000, Combo = 900, Pg = 450, Gr = 50, MinBp = 10, Clear = 2 }
        });

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

    [TestMethod]
    public void RankingCacheSummaryParser_MissingPlayerUsesUnifiedExpectedValues()
    {
        string hash = "23232323232323232323232323232323";
        DateTime cacheUpdate = new DateTime(2026, 4, 1, 12, 0, 0);
        DateTime lastUpdate = new DateTime(2026, 4, 2, 13, 14, 15);
        string xml = BuildRankingCacheXml(lastUpdate, new[]
        {
            new RankingScoreRow { Lr2Id = 100, Notes = 800, Combo = 850, Pg = 500, Gr = 0, MinBp = 20, Clear = 3 },
            new RankingScoreRow { Lr2Id = 200, Notes = 1200, Combo = 950, Pg = 500, Gr = 0, MinBp = 5, Clear = 4 },
            new RankingScoreRow { Lr2Id = 300, Notes = 1600, Combo = 900, Pg = 100, Gr = 50, MinBp = 10, Clear = 2 }
        });

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

    [TestMethod]
    public void RankingCacheSummaryParser_EmptyLastupdateWithNulFallsBackToCacheWriteTime()
    {
        string hash = "24242424242424242424242424242424";
        DateTime cacheUpdate = new DateTime(2026, 4, 3, 9, 0, 0);
        string xml = BuildRankingCacheXml(null, new[]
        {
            new RankingScoreRow { Lr2Id = 123, Notes = 1000, Combo = 850, Pg = 400, Gr = 40, MinBp = 20, Clear = 3 }
        }, appendNul: true);

        bool parsed = BmsLibraryIrService.TryParseRankingCacheSummary(xml, hash, 123, cacheUpdate, out LR2IRData actual, out int scoresParsed);

        Assert.IsTrue(parsed);
        Assert.AreEqual(1, scoresParsed);
        Assert.AreEqual(cacheUpdate, actual.lastupdate);
        Assert.AreEqual(cacheUpdate, actual.lastcacheupdate);
    }

    [TestMethod]
    public void LR2IRCacheWrapper_UsesUnifiedParserSemantics()
    {
        string hash = "34343434343434343434343434343434";
        DateTime cacheUpdate = new DateTime(2026, 4, 3, 9, 0, 0);
        DateTime lastUpdate = new DateTime(2026, 4, 3, 10, 0, 0);
        string xml = BuildRankingCacheXml(lastUpdate, new[]
        {
            new RankingScoreRow { Lr2Id = 123, Notes = 1000, Combo = 850, Pg = 400, Gr = 40, MinBp = 20, Clear = 3 },
            new RankingScoreRow { Lr2Id = 456, Notes = 1000, Combo = 950, Pg = 500, Gr = 20, MinBp = -1, Clear = 4 },
            new RankingScoreRow { Lr2Id = 789, Notes = 1000, Combo = 900, Pg = 450, Gr = 50, MinBp = 10, Clear = 2 }
        });

        LR2IRCache cache = new LR2IRCache(xml, hash, cacheUpdate);
        bool parsed = BmsLibraryIrService.TryParseRankingCacheSummary(xml, hash, 123, cacheUpdate, out LR2IRData summary, out int scoresParsed);
        LR2IRData fromCache = cache.GetLR2IRData(123);

        Assert.IsTrue(parsed);
        Assert.AreEqual(2, scoresParsed);
        Assert.AreEqual(2, cache.GetRankingNum());
        AssertIrDataEquivalent(summary, fromCache);
        Assert.AreEqual(2, cache.GetRankFromScore(fromCache.score));
    }


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

    [TestMethod]
    public void RankingCacheSummaryParser_SkipsNegativeNumericFields()
    {
        string hash = "33333333333333333333333333333334";
        DateTime cacheUpdate = new DateTime(2026, 4, 4, 12, 0, 0);
        DateTime lastUpdate = new DateTime(2026, 4, 4, 13, 0, 0);
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

    [TestMethod]
    public void RefreshRankingScoresFromCache_EmptyIrDataBulkInsertsMultipleXmlRows()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService(2);
        DateTime cacheUpdate = new DateTime(2026, 4, 5, 12, 0, 0);
        env.WriteCacheXml("26262626262626262626262626262626", cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        env.WriteCacheXml("27272727272727272727272727272727", cacheUpdate, cacheUpdate, lr2Id: 123, pg: 600, gr: 100);
        BmsLibraryDbGateway gateway = env.CreateGateway();

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, new List<BMSScore>(), new BMSFile[0], skipEstimateOfflineScoreRanking: true);

        Assert.AreEqual(2, result.CacheFilesReloaded);
        Assert.AreEqual(2, result.IrDataUpsertCount);
        Assert.AreEqual(2, result.XmlScoresParsed);
        Assert.AreEqual(0, result.XmlParseFailedCount);
        Assert.IsTrue(result.BulkInsertUsed);
        Assert.AreEqual(2, gateway.LoadIrData(123).Count);
    }

    [TestMethod]
    public void RefreshRankingScoresFromCache_ProducesSameRowsWithDifferentXmlReloadDegree()
    {
        using TempIrEnvironment envDegree1 = TempIrEnvironment.Create();
        using TempIrEnvironment envDegree2 = TempIrEnvironment.Create();
        DateTime cacheUpdate = new DateTime(2026, 4, 5, 12, 0, 0);
        string[] hashes =
        {
            "37373737373737373737373737373737",
            "38383838383838383838383838383838"
        };
        envDegree1.WriteCacheXml(hashes[0], cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        envDegree1.WriteCacheXml(hashes[1], cacheUpdate, cacheUpdate, lr2Id: 123, pg: 600, gr: 100);
        envDegree2.WriteCacheXml(hashes[0], cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        envDegree2.WriteCacheXml(hashes[1], cacheUpdate, cacheUpdate, lr2Id: 123, pg: 600, gr: 100);

        IrCacheRefreshResult resultDegree1 = new BmsLibraryIrService(1).RefreshRankingScoresFromCache(123, envDegree1.ScoreDbPath, envDegree1.CreateGateway(), new List<BMSScore>(), new BMSFile[0], skipEstimateOfflineScoreRanking: true);
        IrCacheRefreshResult resultDegree2 = new BmsLibraryIrService(2).RefreshRankingScoresFromCache(123, envDegree2.ScoreDbPath, envDegree2.CreateGateway(), new List<BMSScore>(), new BMSFile[0], skipEstimateOfflineScoreRanking: true);

        List<LR2IRData> rowsDegree1 = envDegree1.CreateGateway().LoadIrData(123).OrderBy(row => row.hash).ToList();
        List<LR2IRData> rowsDegree2 = envDegree2.CreateGateway().LoadIrData(123).OrderBy(row => row.hash).ToList();
        Assert.AreEqual(2, resultDegree1.IrDataUpsertCount);
        Assert.AreEqual(2, resultDegree2.IrDataUpsertCount);
        Assert.AreEqual(rowsDegree1.Count, rowsDegree2.Count);
        for (int i = 0; i < rowsDegree1.Count; i++)
        {
            AssertIrDataEquivalent(rowsDegree1[i], rowsDegree2[i]);
        }
    }

    [TestMethod]
    public void RefreshRankingScoresFromCache_NonEmptyIrDataUsesIncrementalUpsert()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService(1);
        string hash = "28282828282828282828282828282828";
        DateTime currentUpdate = new DateTime(2026, 4, 5, 12, 0, 0);
        DateTime newerUpdate = new DateTime(2026, 4, 6, 12, 0, 0);
        env.WriteCacheXml(hash, newerUpdate, newerUpdate, lr2Id: 123, pg: 700, gr: 100);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(new[]
        {
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
        });

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, new List<BMSScore>(), new BMSFile[0], skipEstimateOfflineScoreRanking: true);

        Assert.AreEqual(1, result.CacheFilesReloaded);
        Assert.AreEqual(1, result.IrDataUpsertCount);
        Assert.IsFalse(result.BulkInsertUsed);
        Assert.AreEqual(1, gateway.LoadIrData(123).Count((LR2IRData data) => data.hash == hash));
        Assert.AreEqual(1500, gateway.LoadIrData(123).Single((LR2IRData data) => data.hash == hash).score);
    }

    [TestMethod]
    public void UpsertIrData_ReplacesSameHashAndLr2IdWithoutDuplicates()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "29292929292929292929292929292929";

        gateway.UpsertIrData(new[] { new LR2IRData(hash) { lr2id = 123, rank = 10, pg = 100 } });
        gateway.UpsertIrData(new[] { new LR2IRData(hash) { lr2id = 123, rank = 1, pg = 500 } });

        List<LR2IRData> rows = gateway.LoadIrData(123).Where((LR2IRData data) => data.hash == hash).ToList();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(1, rows[0].rank);
        Assert.AreEqual(1000, rows[0].score);
    }

    [TestMethod]
    public void UpsertIrData_PreservesSameHashForDifferentLr2Id()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "30303030303030303030303030303030";

        gateway.UpsertIrData(new[]
        {
            new LR2IRData(hash) { lr2id = 123, rank = 10 },
            new LR2IRData(hash) { lr2id = 456, rank = 20 }
        });

        Assert.AreEqual(1, gateway.LoadIrData(123).Count((LR2IRData data) => data.hash == hash));
        Assert.AreEqual(1, gateway.LoadIrData(456).Count((LR2IRData data) => data.hash == hash));
    }

    [TestMethod]
    public void EnsureIrDataSchema_AllowsLegacyDuplicateRows()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        using (LR2SongDBExtended songDb = new LR2SongDBExtended(env.SongDbPath))
        {
            songDb.Execute("INSERT INTO ir_data (hash, lr2id) VALUES (?, ?);", "31313131313131313131313131313131", 123);
            songDb.Execute("INSERT INTO ir_data (hash, lr2id) VALUES (?, ?);", "31313131313131313131313131313131", 123);
            BmsLibraryDbGateway.EnsureIrDataSchema(songDb);
        }

        Assert.AreEqual(2, env.CreateGateway().LoadIrData(123).Count((LR2IRData data) => data.hash == "31313131313131313131313131313131"));
    }

    [TestMethod]
    public void RefreshRankingScoresFromCache_UsesReloadedLookupForOfflineEstimate()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService(1);
        string hash = "32323232323232323232323232323232";
        DateTime cacheUpdate = new DateTime(2026, 4, 7, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 300, gr: 0);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        BMSScore score = new BMSScore
        {
            hash = hash,
            perfect = 500,
            great = 0
        };

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, new List<BMSScore> { score }, new BMSFile[0], skipEstimateOfflineScoreRanking: false);

        Assert.AreEqual(1, result.CacheFilesReloaded);
        Assert.AreEqual(0, result.OfflineEstimateXmlLoadCount);
        Assert.AreEqual(1, score.ranking);
    }

    [TestMethod]
    public void RefreshRankingScoresFromCache_LoadsCompactLookupOnDemandForDbFallbackOfflineEstimate()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService(1);
        string hash = "35353535353535353535353535353535";
        DateTime cacheUpdate = new DateTime(2026, 4, 7, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 300, gr: 0);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(new[]
        {
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
        });
        BMSScore score = new BMSScore
        {
            hash = hash,
            perfect = 500,
            great = 0
        };

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, new List<BMSScore> { score }, new BMSFile[0], skipEstimateOfflineScoreRanking: false);

        Assert.AreEqual(0, result.CacheFilesReloaded);
        Assert.AreEqual(1, result.OfflineEstimateXmlLoadCount);
        Assert.AreEqual(1, score.ranking);
    }

    [TestMethod]
    public void RefreshRankingScoresFromCache_SkipOfflineEstimateAvoidsXmlLoadForHigherLocalScore()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService();
        string hash = "33333333333333333333333333333333";
        DateTime cacheUpdate = new DateTime(2026, 3, 1, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 300, gr: 0);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        gateway.UpsertIrData(new[]
        {
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
        });
        BMSScore score = new BMSScore
        {
            hash = hash,
            perfect = 500,
            great = 0
        };

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, new List<BMSScore> { score }, new BMSFile[0], skipEstimateOfflineScoreRanking: true);

        Assert.AreEqual(0, result.CacheFilesReloaded);
        Assert.AreEqual(0, result.OfflineEstimateXmlLoadCount);
        Assert.AreEqual(80, score.rankingNum);
        Assert.AreEqual(0, score.ranking);
    }

    [TestMethod]
    public void DownloadIRData_ParsesDownloadedXmlWithUnifiedParser()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService(2);
        string hash = "36363636363636363636363636363636";
        DateTime lastUpdate = new DateTime(2026, 4, 8, 12, 0, 0);
        string xml = BuildRankingCacheXml(lastUpdate, new[]
        {
            new RankingScoreRow { Lr2Id = 123, Notes = 1000, Combo = 900, Pg = 500, Gr = 0, MinBp = 10, Clear = 4 },
            new RankingScoreRow { Lr2Id = 456, Notes = 1000, Combo = 900, Pg = 600, Gr = 0, MinBp = -1, Clear = 4 }
        });
        FakeIrClient irClient = new FakeIrClient(string.Empty);
        irClient.SetRankingXml(hash, xml);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        List<BMSScore> scores = new List<BMSScore>();
        TestableBmsFile file = CreateFile(hash);
        List<BMSLibrary.IRDataCacheInfo> cacheInfo = new List<BMSLibrary.IRDataCacheInfo>
        {
            CreateCacheInfo(hash, lastUpdate)
        };

        List<BMSLibrary.IRDataCacheInfo> failed = service.DownloadIRData(
            123,
            cacheInfo,
            env.IrDirectoryPath,
            gateway,
            irClient,
            new Uri("https://example.invalid/ranking"),
            scores,
            new BMSFile[] { file },
            skipEstimateOfflineScoreRanking: false);

        Assert.AreEqual(0, failed.Count);
        Assert.AreEqual(1, scores.Count);
        Assert.AreSame(scores[0], file.bmsScore);
        Assert.AreEqual(1, scores[0].ranking);
        Assert.AreEqual(1, scores[0].rankingNum);
        Assert.AreEqual(1, gateway.LoadIrData(123).Count((LR2IRData data) => data.hash == hash));
    }

    [TestMethod]
    public void UpdateIrScoreTableWithMetrics_SameScoreDigestSkipsReplaceForSameXml()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "44444444444444444444444444444444";
        FakeIrClient client = new FakeIrClient(BuildPlayerScoreXml(hash, pg: 700, gr: 50));

        IrScoreTableUpdateResult first = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);
        IrScoreTableUpdateResult second = service.UpdateIrScoreTableWithMetrics(123, gateway, client, PlayerScoreRegex);

        Assert.AreEqual("changed", first.SkipReason);
        Assert.IsFalse(first.Skipped);
        Assert.AreEqual("score_digest_same", second.SkipReason);
        Assert.IsTrue(second.Skipped);
        Assert.AreEqual(1, second.ParsedRows);
        Assert.AreEqual(1, second.LoadedRows);
        Assert.AreEqual(hash, second.ScoreTable[0].hash);
    }

    [TestMethod]
    public void UpdateIrScoreTableWithMetrics_SameScoreDigestSkipsReplace()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "55555555555555555555555555555555";
        FakeIrClient client = new FakeIrClient(BuildPlayerScoreXml(hash, pg: 600, gr: 25));

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

    [TestMethod]
    public void UpdateIrScoreTableWithMetrics_LastupdateOnlyChangeSkipsReplace()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "66666666666666666666666666666666";
        FakeIrClient client = new FakeIrClient(BuildPlayerScoreXml(hash, pg: 600, gr: 25, lastUpdate: 20260505));

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

    [TestMethod]
    public void UpdateIrScoreTableWithMetrics_UsesPrefetchedScoreSnapshotWithoutFetchingAgain()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService();
        BmsLibraryDbGateway gateway = env.CreateGateway();
        string hash = "77777777777777777777777777777777";
        FakeIrClient prefetchClient = new FakeIrClient(BuildPlayerScoreXml(hash, pg: 500, gr: 100));
        IrScorePrefetchResult prefetch = service.PrefetchIrScoreTableWithMetrics(123, prefetchClient, PlayerScoreRegex);
        FakeIrClient fallbackClient = new FakeIrClient(BuildPlayerScoreXml("88888888888888888888888888888888", pg: 1, gr: 1));

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

    private static TestableBmsFile CreateFile(string hash)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.SetHash(hash);
        file.path = hash + ".bms";
        return file;
    }

    private static void CreateLr2ScoreDb(string scoreDbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(scoreDbPath));
        using SQLiteConnection connection = new SQLiteConnection(scoreDbPath);
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
    }

    private static void CreateBeatorajaScoreDb(string scoreDbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(scoreDbPath));
        using SQLiteConnection connection = new SQLiteConnection(scoreDbPath);
        connection.Execute(
            "CREATE TABLE score (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER, PRIMARY KEY(sha256, mode));");
        connection.Execute(
            "INSERT INTO score (sha256, mode, clear, epg, lpg, egr, lgr, notes, combo, minbp, playcount, clearcount) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, 2, 100, 20, 30, 10, 200, 180, 5, 7, 3);
    }

    private static readonly Regex PlayerScoreRegex = new Regex("\\t<score>\\r?\\n\\t\\t<hash>([a-f0-9]+)</hash>\\r?\\n\\t\\t<clear>(\\d+)</clear>\\r?\\n\\t\\t<notes>(\\d+)</notes>\\r?\\n\\t\\t<combo>(\\d+)</combo>\\r?\\n\\t\\t<pg>(\\d+)</pg>\\r?\\n\\t\\t<gr>(\\d+)</gr>\\r?\\n\\t\\t<gd>(\\d+)</gd>\\r?\\n\\t\\t<bd>(\\d+)</bd>\\r?\\n\\t\\t<pr>(\\d+)</pr>\\r?\\n\\t\\t<minbp>(\\d+)</minbp>\\r?\\n\\t\\t<option>(\\d+)</option>\\r?\\n\\t\\t<lastupdate>(\\d+)</lastupdate>\\r?\\n\\t</score>\\r?\\n", RegexOptions.Compiled);

    private static string BuildPlayerScoreXml(string hash, int pg, int gr, int lastUpdate = 20260505)
    {
        return "<root>\n"
            + "\t<score>\n"
            + "\t\t<hash>" + hash + "</hash>\n"
            + "\t\t<clear>4</clear>\n"
            + "\t\t<notes>1000</notes>\n"
            + "\t\t<combo>900</combo>\n"
            + "\t\t<pg>" + pg + "</pg>\n"
            + "\t\t<gr>" + gr + "</gr>\n"
            + "\t\t<gd>10</gd>\n"
            + "\t\t<bd>2</bd>\n"
            + "\t\t<pr>1</pr>\n"
            + "\t\t<minbp>12</minbp>\n"
            + "\t\t<option>0</option>\n"
            + "\t\t<lastupdate>" + lastUpdate + "</lastupdate>\n"
            + "\t</score>\n"
            + "</root>\n";
    }

    private static string BuildRankingCacheXml(DateTime? lastUpdate, IEnumerable<RankingScoreRow> scores, bool appendNul = false)
    {
        StringBuilder builder = new StringBuilder();
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
            builder.Append(lastUpdate.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        builder.Append("</lastupdate>\n");
        if (appendNul)
        {
            builder.Append('\0');
        }
        return builder.ToString();
    }

    private static BMSLibrary.IRDataCacheInfo CreateCacheInfo(string md5, DateTime lastUpdate)
    {
        dynamic json = DynamicJson.Parse("{\"md5\":\"" + md5 + "\",\"size\":1,\"lastupdate\":\"" + lastUpdate.ToString("yyyy-MM-dd HH:mm:ss") + "\"}");
        return new BMSLibrary.IRDataCacheInfo(json);
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

    private sealed class FakeIrClient : IBmsLibraryIrClient
    {
        private readonly Dictionary<string, string> rankingXmlByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public FakeIrClient(string playerScoreXml)
        {
            PlayerScoreXml = playerScoreXml;
        }

        public string PlayerScoreXml { get; set; }

        public int PlayerScoreXmlRequestCount { get; private set; }

        public string GetPlayerScoresXml(int lr2Id)
        {
            PlayerScoreXmlRequestCount++;
            return PlayerScoreXml;
        }

        public List<BMSLibrary.IRDataCacheInfo> GetRankingInfo(Uri rankingInfoUrl, IEnumerable<string> md5s)
        {
            return new List<BMSLibrary.IRDataCacheInfo>();
        }

        public void SetRankingXml(string md5, string xml)
        {
            rankingXmlByHash[md5] = xml;
        }

        public void DownloadRankingData(Uri rankingDataUrl, string md5, string destinationPath)
        {
            if (!rankingXmlByHash.TryGetValue(md5, out string xml))
            {
                throw new InvalidOperationException("ranking xml is not registered.");
            }
            File.WriteAllText(destinationPath, xml, Encoding.GetEncoding("shift_jis"));
        }

        public BMSLibrary.IRSongInfo GetSongInfo(Uri songInfoUrl, string md5OrLr2BmsId, bool searchAggressively)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
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
            Directory.CreateDirectory(Path.GetDirectoryName(ScoreDbPath));
            Directory.CreateDirectory(IrDirectoryPath);
            using LR2SongDBExtended songDb = new LR2SongDBExtended(SongDbPath);
            BmsLibraryDbGateway.EnsureIrDataSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.ir_score>();
            songDb.CreateTable<LR2SongDBExtended.ir_score_refresh_metadata>();
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
            string xml = BuildRankingCacheXml(lastUpdate, new[]
            {
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
            });
            string path = Path.Combine(IrDirectoryPath, hash + ".xml");
            File.WriteAllText(path, xml, Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTime(path, fileWriteTime);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDirectoryPath))
                {
                    Directory.Delete(RootDirectoryPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
