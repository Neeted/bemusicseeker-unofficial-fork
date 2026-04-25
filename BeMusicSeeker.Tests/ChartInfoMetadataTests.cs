using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartInfoMetadataTests
{
    private const int FeatureMine = 2;

    private const int FeatureRandom = 4;

    private const int FeatureLongByLnMode = 8;

    private const int FeatureChargeNote = 16;

    private const int FeatureStop = 64;

    private const int FeatureScroll = 128;

    [TestMethod]
    public void EnsureChartInfoSchema_CreatesTableIndexesAndVersionWithoutAlteringSongTable()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            string songTableSqlBefore;
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songTableSqlBefore = songDb.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'song';");
            }

            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(songTableSqlBefore, verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'song';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_info';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_md5';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_charthash';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_parser_version';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'chart_info_schema' AND version = 1;"));
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "sha256",
                    "md5",
                    "charthash",
                    "level",
                    "difficulty",
                    "mainbpm",
                    "maxbpm",
                    "minbpm",
                    "length",
                    "mode",
                    "judge",
                    "feature",
                    "notes",
                    "n",
                    "ln",
                    "s",
                    "ls",
                    "total",
                    "total_defined",
                    "density",
                    "peakdensity",
                    "enddensity",
                    "distribution",
                    "speedchange",
                    "speedchange_count",
                    "lanenotes",
                    "parser_version",
                    "updated_at"
                },
                verify.Query<ColumnNameRow>("PRAGMA table_info(chart_info);").Select((ColumnNameRow row) => row.name).ToArray());
            Assert.IsTrue(gateway.IsChartInfoSchemaCurrent());
        });
    }

    [TestMethod]
    public void DeleteSongsAndMaintenance_LeavesChartInfoRows()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            TestableBmsFile file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "Songs", "delete.bms")
            };
            file.SetHash(new string('a', 32));
            file.SetSha256(new string('b', 64));

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(file, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = file.path, hash = file.hash }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(CreateChartInfoRow(file.sha256, file.hash, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = file.hash,
                    sha256 = file.sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            new BmsLibraryDbGateway(songDbPath).DeleteSongsAndMaintenance(new[] { file });

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + file.path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = '" + file.path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void ParseBms_SimpleFixture_ComputesChartMetadata()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "simple.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE Chart Info Test\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 12\r\n"
                    + "#DIFFICULTY 3\r\n"
                    + "#RANK 3\r\n"
                    + "#TOTAL 300\r\n"
                    + "#00111:0100\r\n"
                    + "#00112:0001\r\n"
                    + "#00116:0100\r\n"
                    + "#00251:0101\r\n"
                    + "#003D1:01\r\n",
                Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);
            LR2SongDBExtended.chart_info bytesRow = ChartInfoParser.ParseBytes(File.ReadAllBytes(chartPath), chartPath, digest.hash, digest.sha256);

            AssertChartInfoEquivalent(row, bytesRow);
            Assert.AreEqual(digest.hash, row.md5);
            Assert.AreEqual(digest.sha256, row.sha256);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, row.parser_version);
            Assert.AreEqual(12, row.level);
            Assert.AreEqual(3, row.difficulty);
            Assert.AreEqual(5, row.mode);
            Assert.AreEqual(100, row.judge);
            Assert.AreEqual(120.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(120.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(120.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(4, row.notes);
            Assert.AreEqual(2, row.n);
            Assert.AreEqual(1, row.ln);
            Assert.AreEqual(1, row.s);
            Assert.AreEqual(0, row.ls);
            Assert.IsTrue(row.total_defined);
            Assert.AreEqual(300.0, row.total.GetValueOrDefault(), 0.0001);
            Assert.IsTrue((row.feature & 1) != 0);
            Assert.IsTrue((row.feature & FeatureMine) != 0);
            Assert.AreEqual(0, row.speedchange_count);
            StringAssert.StartsWith(row.distribution, "#");
            StringAssert.StartsWith(row.lanenotes, "1,1,1,");
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    [TestMethod]
    public void ParseBmson_SimpleFixture_ComputesChartMetadata()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "simple.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"level\":10,\"mode_hint\":\"beat-7k\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240,\"ln_type\":2},"
                    + "\"lines\":[{\"y\":0},{\"y\":960}],"
                    + "\"bpm_events\":[{\"y\":480,\"bpm\":180}],"
                    + "\"stop_events\":[{\"y\":240,\"duration\":120}],"
                    + "\"scroll_events\":[{\"y\":720,\"rate\":0.5}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0},{\"x\":8,\"y\":240},{\"x\":2,\"y\":480,\"l\":240,\"t\":2}]}],"
                    + "\"mine_channels\":[{\"notes\":[{\"x\":3,\"y\":960,\"damage\":1.0}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);
            LR2SongDBExtended.chart_info bytesRow = ChartInfoParser.ParseBytes(File.ReadAllBytes(chartPath), chartPath, digest.hash, digest.sha256);

            AssertChartInfoEquivalent(row, bytesRow);
            Assert.AreEqual(digest.hash, row.md5);
            Assert.AreEqual(digest.sha256, row.sha256);
            Assert.AreEqual(10, row.level);
            Assert.AreEqual(7, row.mode);
            Assert.AreEqual(100, row.judge);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(4, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(2, row.ln);
            Assert.AreEqual(1, row.s);
            Assert.AreEqual(0, row.ls);
            Assert.IsTrue(row.total_defined);
            Assert.AreEqual(260.0, row.total.GetValueOrDefault(), 0.0001);
            Assert.IsTrue((row.feature & FeatureChargeNote) != 0);
            Assert.IsTrue((row.feature & FeatureMine) != 0);
            Assert.IsTrue((row.feature & FeatureStop) != 0);
            Assert.IsTrue((row.feature & FeatureScroll) != 0);
            Assert.AreEqual(3, row.speedchange_count);
            StringAssert.StartsWith(row.distribution, "#");
            Assert.AreEqual(24, row.lanenotes.Split(',').Length);
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    [TestMethod]
    public void BmsonJsonParser_DuplicateKeysUseLastValueAndDefaultsMatchReference()
    {
        string json = "{"
            + "\"unknown\":1,"
            + "\"scroll_events\":[{\"y\":1,\"rate\":0.5}],"
            + "\"scroll_events\":[{\"y\":2}],"
            + "\"bga\":{\"bga_events\":[{\"y\":1}]},"
            + "\"bga\":{\"bga_events\":[{\"y\":2}]}"
            + "}";

        BmsonDocument document = BmsonJsonParser.Parse(json);

        Assert.IsNotNull(document.Info);
        Assert.AreEqual("beat-7k", document.Info.ModeHint);
        Assert.AreEqual(100, document.Info.JudgeRank);
        Assert.AreEqual(100.0, document.Info.Total, 0.000001);
        Assert.AreEqual(240.0, document.Info.Resolution, 0.000001);
        Assert.AreEqual(0.0, document.Info.Level.GetValueOrDefault(), 0.000001);
        Assert.AreEqual(0, document.Lines.Length);
        Assert.AreEqual(0, document.SoundChannels.Length);
        Assert.AreEqual(1, document.ScrollEvents.Length);
        Assert.AreEqual(2, document.ScrollEvents[0].Y);
        Assert.AreEqual(1.0, document.ScrollEvents[0].Rate, 0.000001);
        Assert.AreEqual(1, document.Bga.BgaEvents.Length);
        Assert.AreEqual(2, document.Bga.BgaEvents[0].Y);
    }

    [TestMethod]
    public void BmsonJsonParser_InvalidJsonRemainsFatal()
    {
        try
        {
            BmsonJsonParser.Parse("{\"info\":");
            Assert.Fail("Invalid bmson JSON should fail.");
        }
        catch (Exception ex)
        {
            StringAssert.Contains(ex.GetType().Name, "Json");
        }
    }

    [TestMethod]
    public void ParseBms_Base62Fixture_UsesBase62ForIndexedDefinitionsAndDataTokens()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "base62.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#BASE 62\r\n"
                    + "#BPMa0 180\r\n"
                    + "#00108:a0\r\n"
                    + "#00111:a0\r\n",
                Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(120.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.speedchange_count);
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    [TestMethod]
    public void ParseBms_IndexedBpmCommandAcceptsColonSeparator()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "indexed-bpm-colon.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#BPM01:180\r\n"
                    + "#00108:01\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(120.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    [TestMethod]
    public void ParseBms_InitialBpmIsIncludedInMinMaxBpmEvenWhenMeasureZeroChangesBpm()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "measure-zero-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 160\r\n"
                    + "#BPMB4 180\r\n"
                    + "#00003:B4\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(160.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    [TestMethod]
    public void ParseBms_RandomFixture_UsesStableSelectedBranchOne()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM 2\r\n"
                    + "#IF 2\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 1\r\n"
                    + "#00112:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(0, row.s);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_TimelineLongerThanOneDayIsAllowed()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "longer-than-one-day.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 2.7\r\n"
                    + "#99911:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() > 86400 * 1000);
        });
    }

    [TestMethod]
    public void ParseBms_TimelineLongerThanIntMillisecondsIsFatal()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "too-long.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 0.1\r\n"
                    + "#99911:01\r\n",
                Encoding.ASCII);

            InvalidDataException ex = Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath));
            StringAssert.Contains(ex.Message, "BMS timeline length is too large.");
        });
    }

    [TestMethod]
    public void ParseBms_RandomRetrySkipsTimelineLongerThanIntMilliseconds()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-too-long.bms");
            File.WriteAllText(
                chartPath,
                "#RANDOM 2\r\n"
                    + "#IF 1\r\n"
                    + "#BPM 0.1\r\n"
                    + "#99911:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 2\r\n"
                    + "#BPM 120\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(2000, row.length.GetValueOrDefault());
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_MalformedKnownCommandsAreNonFatalAndTotalUndefined()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "malformed.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#BPMzz nope\r\n"
                    + "#STOPzz nope\r\n"
                    + "#TOTAL nope\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsFalse(row.total_defined);
            Assert.IsTrue(row.total.GetValueOrDefault() > 0.0);
        });
    }

    [TestMethod]
    public void ParseBms_MissingInitialBpmIsFatal()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-bpm.bms");
            File.WriteAllText(chartPath, "#00111:01\r\n", Encoding.ASCII);

            Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath));
        });
    }

    [TestMethod]
    public void ParseBms_MeasureZeroIndexedBpmCanDefineInitialTimelineBpm()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "measure-zero-indexed-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#BPM01 150\r\n"
                    + "#00008:01\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(0.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.notes);
        });
    }

    [TestMethod]
    public void ParseBms_MeasureZeroDirectBpmCanDefineInitialTimelineBpm()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "measure-zero-direct-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#00003:96\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(0.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.notes);
        });
    }

    [TestMethod]
    public void ParseBms_InvalidInitialBpmWithoutTimelineZeroBpmIsFatal()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string[] texts =
            {
                "#BPM 0\r\n#00111:01\r\n",
                "#BPM -120\r\n#00111:01\r\n",
                "#BPM nope\r\n#00111:01\r\n"
            };

            for (int index = 0; index < texts.Length; index++)
            {
                string chartPath = Path.Combine(tempRootPath, "invalid-bpm-" + index.ToString(CultureInfo.InvariantCulture) + ".bms");
                File.WriteAllText(chartPath, texts[index], Encoding.ASCII);

                Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath));
            }
        });
    }

    [TestMethod]
    public void ParseBms_RandomRetryUsesLaterBranchWhenBranchOneHasInvalidInitialBpm()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-initial-bpm-retry.bms");
            File.WriteAllText(
                chartPath,
                "#RANDOM 2\r\n"
                    + "#IF 1\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 2\r\n"
                    + "#BPM01 150\r\n"
                    + "#00008:01\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_RandomRetryUsesLaterBranchWhenBranchOneTimelineIsTooLong()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-long-timeline-retry.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM 2\r\n"
                    + "#IF 1\r\n"
                    + "#00102:1100000\r\n"
                    + "#00211:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 2\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() < 86400 * 1000);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBmson_UnknownFieldsAreIgnoredAndUnsupportedModeFallsBackToBeat7()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "unknown.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"unknown_root\":1,"
                    + "\"info\":{\"level\":3,\"mode_hint\":\"unsupported-mode\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240,\"unknown_info\":2},"
                    + "\"sound_channels\":[{\"unknown_channel\":3,\"notes\":[{\"x\":1,\"y\":0,\"unknown_note\":4}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(7, row.mode);
            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    [TestMethod]
    public void ParseBms_LongNoteChartHashUsesBeatorajaNumericLongNoteMarker()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "long-charthash.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#00151:0101\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual("c903e9b613077c6540374617cd9f5fd1916bcc77ba8e3e8770d30bea10b30319", row.charthash);
            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.ln);
        });
    }

    [TestMethod]
    public void ParseBmson_LongNoteAudioDurationAffectsChartHash()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "duration-a.bmson");
            string secondPath = Path.Combine(tempRootPath, "duration-b.bmson");
            File.WriteAllText(firstPath, CreateBmsonLongNoteWithContinuation(240), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(secondPath, CreateBmsonLongNoteWithContinuation(360), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info first = ChartInfoParser.Parse(firstPath);
            LR2SongDBExtended.chart_info second = ChartInfoParser.Parse(secondPath);

            Assert.AreEqual(first.notes, second.notes);
            Assert.AreEqual(first.ln, second.ln);
            Assert.AreNotEqual(first.charthash, second.charthash);
        });
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealBeatorajaCompatibilitySample_ReducesKnownDiffs()
    {
        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_real expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(1000, rows.Count);
        CompatibilityDiffCounts diffs = new CompatibilityDiffCounts();
        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(chartPath), "Missing real chart fixture: " + expected.fixture_path);

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);
            ChartInfoParser.ChartInfoParseResult fromBytesResult = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256);
            LR2SongDBExtended.chart_info fromBytes = fromBytesResult.Row;
            AssertChartInfoEquivalent(actual, fromBytes);
            diffs.Add(expected, actual, fromBytesResult.ChartString);
        }

        Assert.AreEqual(0, diffs.CoreDiffs, diffs.ToString());
        Assert.IsTrue(diffs.DensityDiffs <= 100, diffs.ToString());
        Assert.IsTrue(diffs.PeakDensityDiffs <= 85, diffs.ToString());
        Assert.IsTrue(diffs.EndDensityDiffs <= 85, diffs.ToString());
        Assert.IsTrue(diffs.DistributionDiffs <= 400, diffs.ToString());
        Assert.IsTrue(diffs.SpeedChangeDiffs <= 180, diffs.ToString());
        Assert.IsTrue(diffs.LengthDiffs <= 130, diffs.ToString());
        Assert.AreEqual(0, diffs.ChartHashDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.BpmIntegerDiffs, diffs.ToString());
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealBmsonDuplicateKeyFixtures_MatchBeatorajaHash()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_bmson_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_bmson_real expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "WHERE sc.sha256 IN ('296314aeb18ba9c44eda264784711861df4fd9a91a9f82a133911e1e5b926749','b7e399df46bc7f800c91c4d81002b806f32b6da70314c47bcc3064467d21e6b1') "
                + "ORDER BY sc.fixture_id;");
        Assert.AreEqual(2, rows.Count);

        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
        }
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealBmsonBeatorajaCompatibility_AllFixturesMatch()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_bmson_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_bmson_real expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(1034, rows.Count);
        BmsonCompatibilityDiffCounts diffs = new BmsonCompatibilityDiffCounts();
        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(chartPath), "Missing bmson chart fixture: " + expected.fixture_path);

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);
            ChartInfoParser.ChartInfoParseResult fromBytesResult = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256);
            LR2SongDBExtended.chart_info fromBytes = fromBytesResult.Row;
            AssertChartInfoEquivalent(actual, fromBytes);
            diffs.Add(expected, actual, fromBytesResult.ChartString);
        }

        Assert.AreEqual(0, diffs.CoreDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.ChartHashDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.LengthDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.DistributionDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.BpmIntegerDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.TotalDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.DensityDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.PeakDensityDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.EndDensityDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.MainBpmDiffs, diffs.ToString());
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealEdgeCase_InitialBpmDefinedByTimelineZeroMatchesBeatoraja()
    {
        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "WHERE sc.reason = 'initial_bpm_success';").Single();
        string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

        LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mainbpm.GetValueOrDefault(), actual.mainbpm.GetValueOrDefault(), 0.000001);
        Assert.AreEqual((int)(expected.minbpm ?? 0.0), (int)(actual.minbpm ?? 0.0));
        Assert.AreEqual((int)(expected.maxbpm ?? 0.0), (int)(actual.maxbpm ?? 0.0));
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealEdgeCases_LongTimelineReferenceChartsMatchBeatoraja()
    {
        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "WHERE sc.reason = 'timeline_long_reference' "
                + "ORDER BY sc.fixture_id;");
        Assert.AreEqual(2, rows.Count);
        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
            Assert.AreEqual(expected.distribution, actual.distribution, expected.sha256);
            Assert.AreEqual(expected.density.GetValueOrDefault(), actual.density.GetValueOrDefault(), 0.000001, expected.sha256);
        }
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealEdgeCases_RandomOverflowFixturesDoNotOverflow()
    {
        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<EdgeCaseSampleChartRow> samples = connection.Query<EdgeCaseSampleChartRow>(
            "SELECT * FROM sample_chart WHERE reason = 'overflow_retry' ORDER BY fixture_id;");
        Assert.AreEqual(4, samples.Count);
        foreach (EdgeCaseSampleChartRow sample in samples)
        {
            string chartPath = Path.Combine(fixtureRootPath, sample.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            LR2SongDBExtended.chart_info actual;
            try
            {
                actual = ChartInfoParser.Parse(chartPath, sample.md5, sample.sha256);
            }
            catch (Exception ex)
            {
                Assert.Fail(sample.sha256 + " failed with " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            Assert.IsTrue((actual.feature & FeatureRandom) != 0, sample.sha256);
            Assert.IsTrue(actual.notes > 0, sample.sha256);
            Assert.IsTrue(actual.length.GetValueOrDefault() >= 0, sample.sha256);
        }
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealEdgeCases_InitialBpmReferenceFatalChartsRemainFatal()
    {
        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<EdgeCaseSampleChartRow> samples = connection.Query<EdgeCaseSampleChartRow>(
            "SELECT * FROM sample_chart WHERE reason = 'initial_bpm_fatal_reference' ORDER BY fixture_id;");
        Assert.AreEqual(2, samples.Count);
        foreach (EdgeCaseSampleChartRow sample in samples)
        {
            string chartPath = Path.Combine(fixtureRootPath, sample.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath, sample.md5, sample.sha256), sample.sha256);
        }
    }

    [TestMethod]
    [TestCategory("CompatibilityTool")]
    [Microsoft.VisualStudio.TestTools.UnitTesting.Ignore("Manual smoke check for local JDK/reference repos. Normal dotnet test must not depend on Java.")]
    public void ChartStringDumpTool_BuildsAndDumpsReferenceChartString()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "tools", "chartstring-dump", "run.ps1");
        string chartPath = Path.Combine(
            repoRoot,
            "BeMusicSeeker.Tests",
            "TestData",
            "chart_info_real",
            "charts",
            "00",
            "00ac147d2ad720b50087e9480708c62240e2816d74bcdcf6ec22dbe7d413f2c6.bms");
        Assert.IsTrue(File.Exists(scriptPath), "chartstring-dump run script is missing.");
        Assert.IsTrue(File.Exists(chartPath), "chartstring-dump smoke fixture is missing.");

        string powershellPath = File.Exists(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")
            ? @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
            : "pwsh";
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = "-ExecutionPolicy Bypass -File \"" + scriptPath + "\" \"" + chartPath + "\"",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start chartstring-dump.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        Assert.AreEqual(0, process.ExitCode, stdout + Environment.NewLine + stderr);
        StringAssert.Contains(stdout, "\"ok\":true");
        StringAssert.Contains(stdout, "\"charthash\":");
    }

    [TestMethod]
    public void BackfillChartInfos_ParsesMissingRowsSkipsCurrentRowsAndReparsesStaleRows()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "backfill.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            ChartInfoBuildService service = new ChartInfoBuildService();
            List<Tuple<int, int, string>> progress = new List<Tuple<int, int, string>>();

            ChartInfoBackfillResult first = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                (total, processed, currentPath) => progress.Add(Tuple.Create(total, processed, currentPath)));

            Assert.AreEqual(1, first.TargetCount);
            Assert.AreEqual(1, first.ProcessedCount);
            Assert.AreEqual(1, first.BackfilledCount);
            Assert.AreEqual(0, first.FailedCount);
            Assert.IsNotNull(file.ChartInfo);
            Assert.AreEqual(file.sha256, file.ChartInfo.sha256);
            Assert.AreEqual(1L, CountChartInfoRows(songDbPath, file.sha256));
            Assert.AreEqual(1, progress.Last().Item1);
            Assert.AreEqual(1, progress.Last().Item2);

            ChartInfoBackfillResult second = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(0, second.TargetCount);
            Assert.AreEqual(0, second.BackfilledCount);

            gateway.UpsertChartInfos(new[] { CreateChartInfoRow(file.sha256, file.hash, parserVersion: 0) });
            ChartInfoBackfillResult third = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, third.TargetCount);
            Assert.AreEqual(1, third.BackfilledCount);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, verify.ExecuteScalar<int>("SELECT parser_version FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReadsOnceAndPersistsDigestAndInfoForMissingSha256()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "single-read.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            TestableBmsFile file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            Dictionary<string, int> readCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ChartInfoBuildService service = new ChartInfoBuildService(delegate(string path)
            {
                readCounts[path] = readCounts.TryGetValue(path, out int count) ? count + 1 : 1;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            List<string> logs = new List<string>();
            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ProcessedCount);
            Assert.AreEqual(1, result.DigestTargetCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.AreEqual(1, readCounts[chartPath]);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            Assert.IsNotNull(file.ChartInfo);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_GroupsDuplicateMissingSha256TargetsByMd5()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartAPath = Path.Combine(tempRootPath, "duplicate-a.bms");
            string chartBPath = Path.Combine(tempRootPath, "duplicate-b.bms");
            string text = "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n";
            File.WriteAllText(chartAPath, text, Encoding.ASCII);
            File.WriteAllText(chartBPath, text, Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartAPath);
            TestableBmsFile fileA = new TestableBmsFile { path = chartAPath };
            TestableBmsFile fileB = new TestableBmsFile { path = chartBPath };
            fileA.SetHash(digest.hash);
            fileB.SetHash(digest.hash);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            int readCount = 0;
            ChartInfoBuildService service = new ChartInfoBuildService(delegate(string path)
            {
                readCount++;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { fileA, fileB },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(2, result.DigestTargetCount);
            Assert.AreEqual(2, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, readCount);
            Assert.AreEqual(fileA.sha256, fileB.sha256);
            Assert.IsNotNull(fileA.ChartInfo);
            Assert.AreSame(fileA.ChartInfo, fileB.ChartInfo);
        });
    }

    [TestMethod]
    public void BackfillChartInfosForTargets_ParsesOnlyProvidedTargets()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string targetChartPath = Path.Combine(tempRootPath, "target.bms");
            string untouchedChartPath = Path.Combine(tempRootPath, "untouched.bms");
            File.WriteAllText(targetChartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(untouchedChartPath, "#PLAYER 1\r\n#BPM 150\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile targetDigest = BMSFile.CreateBMSFileFromFile(targetChartPath);
            BMSFile untouchedDigest = BMSFile.CreateBMSFileFromFile(untouchedChartPath);
            TestableBmsFile targetFile = new TestableBmsFile { path = targetChartPath };
            TestableBmsFile untouchedFile = new TestableBmsFile { path = untouchedChartPath };
            targetFile.SetHash(targetDigest.hash);
            untouchedFile.SetHash(untouchedDigest.hash);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            Dictionary<string, int> readCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ChartInfoBuildService service = new ChartInfoBuildService(delegate(string path)
            {
                readCounts[path] = readCounts.TryGetValue(path, out int count) ? count + 1 : 1;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 1);
            List<string> logs = new List<string>();

            ChartInfoBackfillResult result = service.BackfillChartInfosForTargets(
                gateway,
                new[] { targetFile },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, readCounts[targetChartPath]);
            Assert.IsFalse(readCounts.ContainsKey(untouchedChartPath));
            Assert.IsNotNull(targetFile.ChartInfo);
            Assert.IsNull(untouchedFile.ChartInfo);
            Assert.IsTrue(logs.Any((string message) => message.StartsWith("INFO chart_info_backfill start mode=added", StringComparison.Ordinal)));
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + targetFile.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + untouchedDigest.sha256 + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfosForTargets_AppliesExistingCurrentRowWithoutReading()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            TestableBmsFile file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "already-current.bms")
            };
            file.SetHash(new string('a', 32));
            file.SetSha256(new string('b', 64));
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            LR2SongDBExtended.chart_info expected = CreateChartInfoRow(file.sha256, file.hash, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos(new[] { expected });
            ChartInfoBuildService service = new ChartInfoBuildService(delegate
            {
                throw new InvalidOperationException("The existing current chart_info row should be reused.");
            }, workerCountOverride: 1);

            ChartInfoBackfillResult result = service.BackfillChartInfosForTargets(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(0, result.TargetCount);
            Assert.AreEqual(0, result.BackfilledCount);
            Assert.IsNotNull(file.ChartInfo);
            Assert.AreEqual(expected.sha256, file.ChartInfo.sha256);
            Assert.AreEqual(expected.md5, file.ChartInfo.md5);
        });
    }

    [TestMethod]
    public void BackfillChartInfosForTargets_ParseFailureStillPersistsDigest()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-target.bmson");
            File.WriteAllText(chartPath, "not json", Encoding.ASCII);
            TestableBmsFile file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(new string('a', 32));
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            ChartInfoBuildService service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = service.BackfillChartInfosForTargets(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(0, result.BackfilledCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void InstallBMSPackages_AddsBmsAndQueuesTargetedChartInfoBackfill()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string sourceDir = Path.Combine(tempRootPath, "SourceBms");
            string installDir = Path.Combine(tempRootPath, "InstalledBms");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(installDir);
            string sourceChartPath = Path.Combine(sourceDir, "install.bms");
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE install bms\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            PendingChartEntry pendingChart = PendingChartEntry.CreateFromFilePath(sourceChartPath);
            BMSPackage package = new BMSPackage(new[] { pendingChart })
            {
                path = sourceDir,
                delete_parent = false
            };
            BMSLibrary library = new BMSLibrary(songDbPath, null, null, null, new RecordingDialogService());

            InvokeInstallBmsPackages(library, new[] { package }, installDir);

            Assert.IsTrue(WaitForChartInfoBackfill(library), "chart_info targeted backfill did not complete.");
            BMSFile installedFile = library.BMSFiles.Single();
            Assert.IsNotNull(installedFile.ChartInfo);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + installedFile.path.Replace("'", "''") + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + installedFile.hash + "' AND sha256 = '" + installedFile.sha256 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + installedFile.sha256 + "';"));
        });
    }

    [TestMethod]
    public void InstallBMSPackages_AddsBmsonAndQueuesTargetedChartInfoBackfill()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string sourceDir = Path.Combine(tempRootPath, "SourceBmson");
            string installDir = Path.Combine(tempRootPath, "InstalledBmson");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(installDir);
            string sourceChartPath = Path.Combine(sourceDir, "install.bmson");
            File.WriteAllText(
                sourceChartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"title\":\"install bmson\",\"level\":1,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            PendingChartEntry pendingChart = PendingChartEntry.CreateFromFilePath(sourceChartPath);
            BMSPackage package = new BMSPackage(new[] { pendingChart })
            {
                path = sourceDir,
                delete_parent = false
            };
            BMSLibrary library = new BMSLibrary(songDbPath, null, null, null, new RecordingDialogService());

            InvokeInstallBmsPackages(library, new[] { package }, installDir);

            Assert.IsTrue(WaitForChartInfoBackfill(library), "chart_info targeted backfill did not complete.");
            LR2SongDBExtended.bmson_song installedSong = library.BmsonSongs.Single();
            Assert.IsNotNull(installedSong.ChartInfo);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song WHERE path = '" + installedSong.path.Replace("'", "''") + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + installedSong.sha256 + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ParseFailureStillPersistsDigest()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad.bmson");
            File.WriteAllText(chartPath, "not json", Encoding.ASCII);
            TestableBmsFile file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(new string('a', 32));
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            ChartInfoBuildService service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 2);
            List<string> logs = new List<string>();

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(0, result.DigestFailedCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.BackfilledCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            Assert.IsTrue(logs.Count >= 2);
            Assert.IsTrue(logs.Any((string message) => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReadFailureLogsWarnImmediately()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            TestableBmsFile file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "missing-file.bms")
            };
            file.SetHash(new string('a', 32));
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            ChartInfoBuildService service = new ChartInfoBuildService(delegate
            {
                throw new IOException("read boom");
            }, workerCountOverride: 1);
            List<string> logs = new List<string>();

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ReadFailedCount);
            Assert.AreEqual(0, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.IsTrue(logs.Count >= 2);
            Assert.IsTrue(logs.Any((string message) => message.StartsWith("WARN chart_info_backfill read_failed", StringComparison.Ordinal)));
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
        });
    }

    [TestMethod]
    public void ParseBytesDetailed_ZeroTimeoutThrowsDedicatedTimeout()
    {
        string text = "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        ChartInfoParser.ChartInfoParseTimeoutException ex = Assert.ThrowsException<ChartInfoParser.ChartInfoParseTimeoutException>(delegate
        {
            ChartInfoParser.ParseBytesDetailed(bytes, ".bms", new string('a', 32), new string('b', 64), null, TimeSpan.Zero);
        });

        StringAssert.Contains(ex.Message, "timed out");
    }

    [TestMethod]
    public void BackfillChartInfos_TimeoutStillPersistsDigest()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "timeout.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            TestableBmsFile file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            ChartInfoBuildService service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1, commitChunkSizeOverride: 2, parseTimeoutOverride: TimeSpan.Zero);
            List<string> logs = new List<string>();

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(1, result.TimeoutFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(0, result.BackfilledCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            Assert.IsTrue(logs.Any((string message) => message.Contains("exception=\"ChartInfoParseTimeoutException\"")));
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_CommitsInChunksAndLogsPhaseBoundaries()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            List<TestableBmsFile> files = new List<TestableBmsFile>();
            for (int index = 0; index < 5; index++)
            {
                string chartPath = Path.Combine(tempRootPath, "chunk-" + index.ToString(CultureInfo.InvariantCulture) + ".bms");
                File.WriteAllText(
                    chartPath,
                    "#PLAYER 1\r\n#TITLE chunk " + index.ToString(CultureInfo.InvariantCulture) + "\r\n#BPM " + (120 + index).ToString(CultureInfo.InvariantCulture) + "\r\n#00111:01\r\n",
                    Encoding.ASCII);
                BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
                TestableBmsFile file = new TestableBmsFile
                {
                    path = chartPath
                };
                file.SetHash(digest.hash);
                files.Add(file);
            }
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            ChartInfoBuildService service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 2, commitChunkSizeOverride: 2);
            List<string> logs = new List<string>();

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                files,
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(5, result.TargetCount);
            Assert.AreEqual(5, result.ProcessedCount);
            Assert.AreEqual(5, result.DigestBackfilledCount);
            Assert.AreEqual(5, result.BackfilledCount);
            Assert.AreEqual(3, result.CommitChunks);
            Assert.AreEqual(3, logs.Count((string message) => message.StartsWith("INFO chart_info_backfill db_commit_chunk_done", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any((string message) => message.StartsWith("INFO chart_info_backfill start", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any((string message) => message.StartsWith("INFO chart_info_backfill parse_done", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any((string message) => message.StartsWith("INFO chart_info_backfill slow_parse_top", StringComparison.Ordinal)));
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(5L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(5L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void RetryIfLockedOrBusy_RespectsMaxRetryCount()
    {
        int attempts = 0;

        Assert.ThrowsException<SQLiteException>(delegate
        {
            SQLiteConnectionEx.RetryIfLockedOrBusy(delegate
            {
                attempts++;
                throw CreateSQLiteException(SQLite3.Result.Busy, "busy");
            }, null, 0u);
        });

        Assert.AreEqual(1, attempts);
    }

    private static SQLiteException CreateSQLiteException(SQLite3.Result result, string message)
    {
        System.Reflection.ConstructorInfo constructor = typeof(SQLiteException).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            new[] { typeof(SQLite3.Result), typeof(string) },
            null);
        Assert.IsNotNull(constructor, "SQLiteException internal constructor was not found.");
        return (SQLiteException)constructor.Invoke(new object[] { result, message });
    }

    private static long CountChartInfoRows(string songDbPath, string sha256)
    {
        using LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath);
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + sha256 + "';");
    }

    private static void AssertChartInfoEquivalent(LR2SongDBExtended.chart_info expected, LR2SongDBExtended.chart_info actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.mainbpm, actual.mainbpm);
        Assert.AreEqual(expected.maxbpm, actual.maxbpm);
        Assert.AreEqual(expected.minbpm, actual.minbpm);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.feature, actual.feature);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.n, actual.n);
        Assert.AreEqual(expected.ln, actual.ln);
        Assert.AreEqual(expected.s, actual.s);
        Assert.AreEqual(expected.ls, actual.ls);
        Assert.AreEqual(expected.total, actual.total);
        Assert.AreEqual(expected.total_defined, actual.total_defined);
        Assert.AreEqual(expected.density, actual.density);
        Assert.AreEqual(expected.peakdensity, actual.peakdensity);
        Assert.AreEqual(expected.enddensity, actual.enddensity);
        Assert.AreEqual(expected.distribution, actual.distribution);
        Assert.AreEqual(expected.speedchange, actual.speedchange);
        Assert.AreEqual(expected.speedchange_count, actual.speedchange_count);
        Assert.AreEqual(expected.lanenotes, actual.lanenotes);
        Assert.AreEqual(expected.parser_version, actual.parser_version);
    }

    private static LR2SongDBExtended.chart_info CreateChartInfoRow(string sha256, string md5, int parserVersion)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            charthash = new string('c', 64),
            level = 1,
            difficulty = 1,
            mainbpm = 120.0,
            maxbpm = 120.0,
            minbpm = 120.0,
            length = 0,
            mode = 7,
            judge = 100,
            feature = FeatureLongByLnMode,
            notes = 1,
            n = 1,
            ln = 0,
            s = 0,
            ls = 0,
            total = 260.0,
            total_defined = false,
            density = 1.0,
            peakdensity = 1.0,
            enddensity = 0.0,
            distribution = "#",
            speedchange = "120.0,0.0",
            speedchange_count = 0,
            lanenotes = "1,0,0",
            parser_version = parserVersion,
            updated_at = DateTime.UtcNow
        };
    }

    private static string CreateBmsonLongNoteWithContinuation(int continuationY)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"level\":1,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240,\"ln_type\":2},"
            + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0,\"l\":480},{\"x\":0,\"y\":" + continuationY.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"c\":true}]}]"
            + "}";
    }

    private static void WithTemporarySongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, Array.Empty<byte>());
        try
        {
            testAction(tempRootPath, songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static void InvokeInstallBmsPackages(BMSLibrary library, IEnumerable<BMSPackage> packages, string installDirectory)
    {
        MethodInfo method = typeof(BMSLibrary).GetMethod("installBMSPackages", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "installBMSPackages method was not found.");
        method.Invoke(
            library,
            new object[]
            {
                packages,
                installDirectory,
                null,
                new List<BMSPackage>(),
                null,
                null,
                false,
                false
            });
    }

    private static bool WaitForChartInfoBackfill(BMSLibrary library)
    {
        return SpinWait.SpinUntil(
            () => library.ChartInfoBackfillRequestedVersion > 0
                && library.ChartInfoBackfillCompletedVersion == library.ChartInfoBackfillRequestedVersion
                && !library.ChartInfoBackfillRunning,
            10000);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker-decomp.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            ApplySha256(value);
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    private sealed class RealChartInfoExpectedRow
    {
        public int fixture_id { get; set; }

        public string fixture_path { get; set; } = string.Empty;

        public string sha256 { get; set; } = string.Empty;

        public string md5 { get; set; } = string.Empty;

        public string charthash { get; set; } = string.Empty;

        public int? level { get; set; }

        public int? difficulty { get; set; }

        public double? mainbpm { get; set; }

        public double? maxbpm { get; set; }

        public double? minbpm { get; set; }

        public int? length { get; set; }

        public int mode { get; set; }

        public int judge { get; set; }

        public int feature { get; set; }

        public int notes { get; set; }

        public int n { get; set; }

        public int ln { get; set; }

        public int s { get; set; }

        public int ls { get; set; }

        public double? total { get; set; }

        public double? density { get; set; }

        public double? peakdensity { get; set; }

        public double? enddensity { get; set; }

        public string distribution { get; set; } = string.Empty;

        public string speedchange { get; set; } = string.Empty;

        public int speedchange_count { get; set; }

        public string lanenotes { get; set; } = string.Empty;
    }

    private sealed class EdgeCaseSampleChartRow
    {
        public int fixture_id { get; set; }

        public string source_path { get; set; } = string.Empty;

        public string fixture_path { get; set; } = string.Empty;

        public string reason { get; set; } = string.Empty;

        public string md5 { get; set; } = string.Empty;

        public string sha256 { get; set; } = string.Empty;

        public int has_beatoraja_song { get; set; }
    }

    private sealed class BmsonCompatibilityDiffCounts
    {
        private readonly List<string> samples = new List<string>();

        public int CoreDiffs { get; private set; }

        public int ChartHashDiffs { get; private set; }

        public int LengthDiffs { get; private set; }

        public int DistributionDiffs { get; private set; }

        public int BpmIntegerDiffs { get; private set; }

        public int TotalDiffs { get; private set; }

        public int DensityDiffs { get; private set; }

        public int PeakDensityDiffs { get; private set; }

        public int EndDensityDiffs { get; private set; }

        public int MainBpmDiffs { get; private set; }

        public void Add(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString)
        {
            CoreDiffs += CountCoreDiffs(expected, actual);
            if (!string.Equals(expected.charthash, actual.charthash, StringComparison.OrdinalIgnoreCase))
            {
                ChartHashDiffs++;
                AddSample("charthash", expected, expected.charthash, actual.charthash, chartString);
            }
            if (expected.length != actual.length)
            {
                LengthDiffs++;
                AddSample("length", expected, expected.length.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), actual.length.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), chartString);
            }
            if (!string.Equals(expected.distribution, actual.distribution, StringComparison.Ordinal))
            {
                DistributionDiffs++;
            }
            if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0)
                || (int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
            {
                BpmIntegerDiffs++;
                AddSample(
                    "bpm",
                    expected,
                    (expected.minbpm ?? 0.0).ToString("R") + "/" + (expected.maxbpm ?? 0.0).ToString("R"),
                    (actual.minbpm ?? 0.0).ToString("R") + "/" + (actual.maxbpm ?? 0.0).ToString("R"),
                    chartString);
            }
            if (!NullableDoubleEquals(expected.total, actual.total))
            {
                TotalDiffs++;
            }
            if (!NullableDoubleEquals(expected.density, actual.density))
            {
                DensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.peakdensity, actual.peakdensity))
            {
                PeakDensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.enddensity, actual.enddensity))
            {
                EndDensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.mainbpm, actual.mainbpm))
            {
                MainBpmDiffs++;
                AddSample(
                    "mainbpm",
                    expected,
                    (expected.mainbpm ?? 0.0).ToString("R", CultureInfo.InvariantCulture),
                    (actual.mainbpm ?? 0.0).ToString("R", CultureInfo.InvariantCulture),
                    chartString);
            }
        }

        public override string ToString()
        {
            return "chart_info bmson compatibility diffs: "
                + "core=" + CoreDiffs
                + " charthash=" + ChartHashDiffs
                + " length=" + LengthDiffs
                + " distribution=" + DistributionDiffs
                + " bpmInteger=" + BpmIntegerDiffs
                + " total=" + TotalDiffs
                + " density=" + DensityDiffs
                + " peakdensity=" + PeakDensityDiffs
                + " enddensity=" + EndDensityDiffs
                + " mainbpm=" + MainBpmDiffs
                + (samples.Count == 0 ? string.Empty : " samples=" + string.Join(" | ", samples));
        }

        private void AddSample(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, string chartString)
        {
            if (samples.Count >= 25 || samples.Count((string item) => item.StartsWith(field + " ", StringComparison.Ordinal)) >= 5)
            {
                return;
            }
            string artifactPath = WriteChartStringArtifact(expected, chartString);
            samples.Add(
                field
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " expected=" + expectedValue
                    + " actual=" + actualValue
                    + " chartString=" + artifactPath);
        }

        private static string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoBmsonDiffs");
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        private static int CountCoreDiffs(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
        {
            int count = 0;
            count += string.Equals(expected.sha256, actual.sha256, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            count += string.Equals(expected.md5, actual.md5, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            count += expected.level == actual.level ? 0 : 1;
            count += expected.difficulty == actual.difficulty ? 0 : 1;
            count += expected.mode == actual.mode ? 0 : 1;
            count += expected.judge == actual.judge ? 0 : 1;
            count += expected.feature == actual.feature ? 0 : 1;
            count += expected.notes == actual.notes ? 0 : 1;
            count += expected.n == actual.n ? 0 : 1;
            count += expected.ln == actual.ln ? 0 : 1;
            count += expected.s == actual.s ? 0 : 1;
            count += expected.ls == actual.ls ? 0 : 1;
            count += string.Equals(expected.lanenotes, actual.lanenotes, StringComparison.Ordinal) ? 0 : 1;
            return count;
        }

        private static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }
    }

    private sealed class CompatibilityDiffCounts
    {
        private readonly List<string> samples = new List<string>();

        public int CoreDiffs { get; private set; }

        public int ChartHashDiffs { get; private set; }

        public int LengthDiffs { get; private set; }

        public int DensityDiffs { get; private set; }

        public int PeakDensityDiffs { get; private set; }

        public int EndDensityDiffs { get; private set; }

        public int DistributionDiffs { get; private set; }

        public int SpeedChangeDiffs { get; private set; }

        public int BpmIntegerDiffs { get; private set; }

        public void Add(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString)
        {
            CoreDiffs += CountCoreDiffs(expected, actual);
            if (!string.Equals(expected.charthash, actual.charthash, StringComparison.OrdinalIgnoreCase))
            {
                ChartHashDiffs++;
                AddSample("charthash", expected, expected.charthash, actual.charthash, chartString);
            }
            if (expected.length != actual.length)
            {
                LengthDiffs++;
            }
            if (!NullableDoubleEquals(expected.density, actual.density))
            {
                DensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.peakdensity, actual.peakdensity))
            {
                PeakDensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.enddensity, actual.enddensity))
            {
                EndDensityDiffs++;
            }
            if (!string.Equals(expected.distribution, actual.distribution, StringComparison.Ordinal))
            {
                DistributionDiffs++;
            }
            if (!string.Equals(expected.speedchange, actual.speedchange, StringComparison.Ordinal))
            {
                SpeedChangeDiffs++;
            }
            if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0)
                || (int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
            {
                BpmIntegerDiffs++;
                AddSample(
                    "bpm",
                    expected,
                    (expected.minbpm ?? 0.0).ToString("R") + "/" + (expected.maxbpm ?? 0.0).ToString("R"),
                    (actual.minbpm ?? 0.0).ToString("R") + "/" + (actual.maxbpm ?? 0.0).ToString("R"),
                    chartString);
            }
        }

        public override string ToString()
        {
            return "chart_info real compatibility diffs: "
                + "core=" + CoreDiffs
                + " charthash=" + ChartHashDiffs
                + " length=" + LengthDiffs
                + " density=" + DensityDiffs
                + " peakdensity=" + PeakDensityDiffs
                + " enddensity=" + EndDensityDiffs
                + " distribution=" + DistributionDiffs
                + " speedchange=" + SpeedChangeDiffs
                + " bpmInteger=" + BpmIntegerDiffs
                + (samples.Count == 0 ? string.Empty : " samples=" + string.Join(" | ", samples));
        }

        private void AddSample(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, string chartString)
        {
            if (samples.Count >= 5)
            {
                return;
            }
            string artifactPath = WriteChartStringArtifact(expected, chartString);
            samples.Add(
                field
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " expected=" + expectedValue
                    + " actual=" + actualValue
                    + " chartString=" + artifactPath);
        }

        private static string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoDiffs");
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        private static int CountCoreDiffs(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
        {
            int count = 0;
            count += expected.notes == actual.notes ? 0 : 1;
            count += expected.n == actual.n ? 0 : 1;
            count += expected.ln == actual.ln ? 0 : 1;
            count += expected.s == actual.s ? 0 : 1;
            count += expected.ls == actual.ls ? 0 : 1;
            count += expected.judge == actual.judge ? 0 : 1;
            count += expected.feature == actual.feature ? 0 : 1;
            count += expected.mode == actual.mode ? 0 : 1;
            count += NullableDoubleEquals(expected.mainbpm, actual.mainbpm) ? 0 : 1;
            count += string.Equals(expected.lanenotes, actual.lanenotes, StringComparison.Ordinal) ? 0 : 1;
            count += NullableDoubleEquals(expected.total, actual.total) ? 0 : 1;
            return count;
        }

        private static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }
    }
}
