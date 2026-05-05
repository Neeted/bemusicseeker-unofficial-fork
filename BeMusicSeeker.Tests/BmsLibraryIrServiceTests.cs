using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
    }

    [TestMethod]
    public void RefreshRankingScoresFromCache_MissingDbRowReloadsXmlAndUpsertsOnce()
    {
        using TempIrEnvironment env = TempIrEnvironment.Create();
        BmsLibraryIrService service = new BmsLibraryIrService();
        string hash = "22222222222222222222222222222222";
        DateTime cacheUpdate = new DateTime(2026, 2, 1, 12, 0, 0);
        env.WriteCacheXml(hash, cacheUpdate, cacheUpdate, lr2Id: 123, pg: 700, gr: 100);
        BmsLibraryDbGateway gateway = env.CreateGateway();
        List<BMSScore> scores = new List<BMSScore>();
        TestableBmsFile file = CreateFile(hash);

        IrCacheRefreshResult result = service.RefreshRankingScoresFromCache(123, env.ScoreDbPath, gateway, scores, new BMSFile[] { file }, skipEstimateOfflineScoreRanking: true);

        Assert.AreEqual(1, result.CacheFilesScanned);
        Assert.AreEqual(1, result.CacheFilesReloaded);
        Assert.AreEqual(1, result.IrDataUpsertCount);
        Assert.AreEqual(0, result.DbFallbackAppliedCount);
        Assert.AreEqual(1, result.XmlAppliedCount);
        Assert.AreEqual(1, scores.Count);
        Assert.AreSame(scores[0], file.bmsScore);
        Assert.AreEqual(1, gateway.LoadIrData(123).Count((LR2IRData data) => data.hash == hash));
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

    private static TestableBmsFile CreateFile(string hash)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.SetHash(hash);
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
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
            songDb.CreateTable<LR2SongDBExtended.ir_data>();
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
            string xml =
                "<root>\n"
                + "<ranking>\n"
                + "\t\t<score>\n"
                + "\t\t<id>" + lr2Id + "</id>\n"
                + "\t\t<clear>4</clear>\n"
                + "\t\t<notes>1000</notes>\n"
                + "\t\t<combo>900</combo>\n"
                + "\t\t<pg>" + pg + "</pg>\n"
                + "\t\t<gr>" + gr + "</gr>\n"
                + "\t\t<minbp>12</minbp>\n"
                + "\t\t</score>\n"
                + "</ranking>\n"
                + "<lastupdate>" + lastUpdate.ToString("yyyy-MM-dd HH:mm:ss") + "</lastupdate>\n";
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
