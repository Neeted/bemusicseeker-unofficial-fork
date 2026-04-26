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

    private static readonly HashSet<string> ProductionDiffKnownTimeoutSha256s = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "273433f8e72e768603d18c986b50900538e94c2bd3e50d4487b66d5c0c3c8201",
        "a56958ab747ebb7fd4332afed2493f75a414c00be694e61182cbd3a363871a43",
        "ae3d8c2c5eb88da961df62a6e7fa6ca043b528f1a36de67643eb463b18864d6f",
        "bd496f28d4a61aba6e9315f61fda463209cd908f3b08f0c2e7e06150034e2e59"
    };

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
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'chart_info_schema' AND version = 2;"));
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "sha256",
                    "md5",
                    "charthash",
                    "level",
                    "difficulty",
                    "difficulty_defined",
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
    public void EnsureChartInfoSchema_RecreatesOldTableWithoutDifficultyDefined()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.Execute(
                    "CREATE TABLE chart_info ("
                        + "sha256 TEXT PRIMARY KEY,"
                        + "md5 TEXT,"
                        + "charthash TEXT,"
                        + "level INTEGER,"
                        + "difficulty INTEGER,"
                        + "parser_version INTEGER,"
                        + "updated_at DATETIME"
                        + ");");
                songDb.Execute("INSERT INTO chart_info (sha256, md5, charthash, level, difficulty, parser_version, updated_at) VALUES ('" + new string('a', 64) + "', '" + new string('b', 32) + "', '" + new string('c', 64) + "', 1, 1, 6, CURRENT_TIMESTAMP);");
            }

            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.IsTrue(verify.Query<ColumnNameRow>("PRAGMA table_info(chart_info);").Any((ColumnNameRow row) => row.name == "difficulty_defined"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
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
            Assert.IsTrue(row.difficulty_defined);
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
    public void ParseBms_SectionRateUsesParsedRateWithoutSubtractionDrift()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "section-rate.bms");
            StringBuilder chart = new StringBuilder();
            chart.Append("#PLAYER 1\r\n#BPM 180\r\n");
            chart.Append("#00102:0.5\r\n");
            for (int section = 2; section <= 87; section++)
            {
                chart.Append('#').Append(section.ToString("000", CultureInfo.InvariantCulture)).Append("02:0.9\r\n");
            }
            chart.Append("#08711:0001\r\n");
            File.WriteAllText(chartPath, chart.ToString(), Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(104600, row.length);
            Assert.AreEqual("180.0,0.0,180.0,104600.0", row.speedchange);
        });
    }

    [TestMethod]
    public void ParseBms_SpeedChangeUsesJavaStyleSmallExponentText()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "small-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 218.607\r\n"
                    + "#BPM01 0.0001\r\n"
                    + "#00108:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            StringAssert.Contains(row.speedchange, "1.0E-4");
            Assert.IsFalse(row.speedchange.Contains("0.0001E0"));
        });
    }

    [TestMethod]
    public void ParseBms_DoubleValuesUseJavaParseDoubleRounding()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-parse-double-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 212\r\n"
                    + "#BPM07 114.15384615384615384615384615\r\n"
                    + "#00108:07\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            StringAssert.Contains(result.Row.speedchange, "114.15384615384616");
            Assert.IsFalse(result.Row.speedchange.Contains("114.15384615384615,"));
            StringAssert.Contains(result.ChartString, "B(114.15384615384616)");
        });
    }

    [TestMethod]
    public void ParseBmson_RawJsonDoublesUseJavaParseDoubleRounding()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-parse-double-bpm.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"bpm_events\":[{\"y\":240,\"bpm\":131.4889812233735}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":480}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            StringAssert.Contains(result.Row.speedchange, "131.4889812233735");
            Assert.IsFalse(result.Row.speedchange.Contains("131.48898122337351"));
            StringAssert.Contains(result.ChartString, "B(131.4889812233735)");
        });
    }

    [TestMethod]
    public void JavaDoubleToStringJdk17_MatchesKnownCompatibilityCases()
    {
        Assert.AreEqual("0.0", JavaDoubleToStringJdk17.ToString(0.0));
        Assert.AreEqual("-0.0", JavaDoubleToStringJdk17.ToString(-0.0));
        Assert.AreEqual("NaN", JavaDoubleToStringJdk17.ToString(double.NaN));
        Assert.AreEqual("Infinity", JavaDoubleToStringJdk17.ToString(double.PositiveInfinity));
        Assert.AreEqual("-Infinity", JavaDoubleToStringJdk17.ToString(double.NegativeInfinity));
        Assert.AreEqual("4.9E-324", JavaDoubleToStringJdk17.ToString(double.Epsilon));
        Assert.AreEqual("1.0E-323", JavaDoubleToStringJdk17.ToString(1e-323));
        Assert.AreEqual("9.999999999999999E22", JavaDoubleToStringJdk17.ToString(1e23));
        Assert.AreEqual("1.9999999999999998E23", JavaDoubleToStringJdk17.ToString(2e23));
        Assert.AreEqual("8.409999999999999E21", JavaDoubleToStringJdk17.ToString(8.41e21));
        Assert.AreEqual("7.6999669989E7", JavaDoubleToStringJdk17.ToString(7.6999669989E7));
        Assert.AreEqual("3.141592653589793", JavaDoubleToStringJdk17.ToString(Math.PI));
        Assert.AreEqual("1.7976931348623157E308", JavaDoubleToStringJdk17.ToString(double.MaxValue));
    }

    [TestMethod]
    public void JavaDoubleParserJdk17_MatchesKnownCompatibilityCases()
    {
        AssertJavaDoubleParseBits("0", "0000000000000000");
        AssertJavaDoubleParseBits("-0", "8000000000000000");
        AssertJavaDoubleParseBits("NaN", "7ff8000000000000");
        AssertJavaDoubleParseBits("-NaN", "7ff8000000000000");
        AssertJavaDoubleParseBits("Infinity", "7ff0000000000000");
        AssertJavaDoubleParseBits("-Infinity", "fff0000000000000");
        AssertJavaDoubleParseBits("1e23", "44b52d02c7e14af6");
        AssertJavaDoubleParseBits("2e23", "44c52d02c7e14af6");
        AssertJavaDoubleParseBits("1e-323", "0000000000000002");
        AssertJavaDoubleParseBits("4e-324", "0000000000000001");
        AssertJavaDoubleParseBits("2.4703282292062327e-324", "0000000000000000");
        AssertJavaDoubleParseBits("2.4703282292062328e-324", "0000000000000001");
        AssertJavaDoubleParseBits("2.2250738585072014e-308", "0010000000000000");
        AssertJavaDoubleParseBits("1.7976931348623157e308", "7fefffffffffffff");
        AssertJavaDoubleParseBits("1.7976931348623159e308", "7ff0000000000000");
        AssertJavaDoubleParseBits("0x1p0", "3ff0000000000000");
        AssertJavaDoubleParseBits("0x1.8p1", "4008000000000000");
        AssertJavaDoubleParseBits("0x1.fffffffffffffp1023", "7fefffffffffffff");
        AssertJavaDoubleParseBits("0x1.fffffffffffff8p1023", "7ff0000000000000");
        AssertJavaDoubleParseBits("0x0.0000000000001p-1022", "0000000000000001");
        AssertJavaDoubleParseBits("0x1p-1075", "0000000000000000");
        AssertJavaDoubleParseBits("0x1.8p-1075", "0000000000000001");

        Assert.AreEqual("114.15384615384616", JavaDoubleToStringJdk17.ToString(JavaDoubleParserJdk17.ParseDouble("114.15384615384615384615384615")));
        Assert.AreEqual("131.4889812233735", JavaDoubleToStringJdk17.ToString(JavaDoubleParserJdk17.ParseDouble("131.4889812233735")));
    }

    [TestMethod]
    public void ParseBms_InvalidChartLikeLineExtendsTimelineSections()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "invalid-section-tail.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 102\r\n"
                    + "#00111:01\r\n"
                    + "#187???\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(2352, row.length);
            Assert.AreEqual("102.0,0.0,102.0,439999.0", row.speedchange);
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
            Assert.AreEqual(1, row.difficulty);
            Assert.IsFalse(row.difficulty_defined);
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
            Assert.AreEqual(4, row.speedchange_count);
            StringAssert.StartsWith(row.distribution, "#");
            Assert.AreEqual(24, row.lanenotes.Split(',').Length);
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    [TestMethod]
    public void ParseBmson_ScrollDoesNotCarryToLaterTimelines()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "scroll-reset.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"scroll_events\":[{\"y\":240,\"rate\":2.0}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":480}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual("120.0,0.0,240.0,500.0,120.0,1000.0", row.speedchange);
            Assert.AreEqual(2, row.speedchange_count);
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
        Assert.AreEqual(240, document.Info.Resolution);
        Assert.IsFalse(document.Info.Level.HasValue);
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
    public void ParseBmson_LevelMissingNullExplicitZeroAndFloatTruncated()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string missingPath = Path.Combine(tempRootPath, "missing-level.bmson");
            File.WriteAllText(
                missingPath,
                "{\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info missing = ChartInfoParser.Parse(missingPath);

            Assert.IsFalse(missing.level.HasValue);
            Assert.AreEqual(1, missing.difficulty);
            Assert.IsFalse(missing.difficulty_defined);

            string zeroPath = Path.Combine(tempRootPath, "zero-level.bmson");
            File.WriteAllText(
                zeroPath,
                "{\"info\":{\"level\":0,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info zero = ChartInfoParser.Parse(zeroPath);

            Assert.AreEqual(0, zero.level);

            string floatPath = Path.Combine(tempRootPath, "float-level.bmson");
            File.WriteAllText(
                floatPath,
                "{\"info\":{\"level\":12.9,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240.9},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info floatLevel = ChartInfoParser.Parse(floatPath);

            Assert.AreEqual(12, floatLevel.level);
        });
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
    public void ParseBms_JavaIntWrappedTimelineAddsDiagnostic()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-int-wrap.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 1\r\n"
                    + "#STOP01 90000\r\n"
                    + "#00009:" + string.Concat(Enumerable.Repeat("01", 40)) + "\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            Assert.AreEqual(1, result.Row.notes);
            Assert.IsTrue(result.Row.length.GetValueOrDefault() > 0);
            ChartInfoParser.ChartInfoParseDiagnostic diagnostic = result.Diagnostics.Single((ChartInfoParser.ChartInfoParseDiagnostic item) => item.Code == "BMS_JAVA_INT_TIME_WRAP");
            Assert.AreEqual(ChartInfoParser.ChartInfoParseDiagnosticSeverity.Info, diagnostic.Severity);
            StringAssert.Contains(diagnostic.Message, "rawMs=");
            StringAssert.Contains(diagnostic.Message, "wrappedMs=");
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
    public void ParseBms_LevelAndDifficultyUseStrictReferenceParsingAndDifficultyInference()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string validPath = Path.Combine(tempRootPath, "valid-difficulty.bms");
            File.WriteAllText(
                validPath,
                "#BPM 120\r\n"
                    + "#PLAYLEVEL 12\r\n"
                    + "#DIFFICULTY 4\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info valid = ChartInfoParser.Parse(validPath);

            Assert.AreEqual(12, valid.level);
            Assert.AreEqual(4, valid.difficulty);
            Assert.IsTrue(valid.difficulty_defined);

            string inferredPath = Path.Combine(tempRootPath, "inferred-difficulty.bms");
            File.WriteAllText(
                inferredPath,
                "#TITLE Strict Parse\r\n"
                    + "#SUBTITLE [Hyper]\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 12abc\r\n"
                    + "#DIFFICULTY 0\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info inferred = ChartInfoParser.Parse(inferredPath);

            Assert.IsFalse(inferred.level.HasValue);
            Assert.AreEqual(3, inferred.difficulty);
            Assert.IsFalse(inferred.difficulty_defined);

            string invalidAfterValidPath = Path.Combine(tempRootPath, "invalid-after-valid-difficulty.bms");
            File.WriteAllText(
                invalidAfterValidPath,
                "#BPM 120\r\n"
                    + "#PLAYLEVEL 12.5\r\n"
                    + "#DIFFICULTY 4\r\n"
                    + "#DIFFICULTY nope\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info invalidAfterValid = ChartInfoParser.Parse(invalidAfterValidPath);

            Assert.IsFalse(invalidAfterValid.level.HasValue);
            Assert.AreEqual(4, invalidAfterValid.difficulty);
            Assert.IsTrue(invalidAfterValid.difficulty_defined);
        });
    }

    [TestMethod]
    public void ParseBms_HeaderCommandsAcceptBeatorajaReserveWordFormsAndUnicodeDigits()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "reserve-word-headers.bms");
            File.WriteAllText(
                chartPath,
                "#TITLELegacy Title\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 2８\r\n"
                    + "#DIFFICULTY=2\r\n"
                    + "#RANK ３\r\n"
                    + "#00111:01\r\n",
                Encoding.GetEncoding(932));

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(28, row.level.GetValueOrDefault());
            Assert.AreEqual(2, row.difficulty);
            Assert.IsTrue(row.difficulty_defined);
            Assert.AreEqual(100, row.judge);
        });
    }

    [TestMethod]
    public void ParseBms_TotalRejectsTrailingGarbageButAcceptsDecimal()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string invalidPath = Path.Combine(tempRootPath, "total-invalid.bms");
            File.WriteAllText(invalidPath, "#BPM 120\r\n#TOTAL 100abc\r\n#00111:01\r\n", Encoding.ASCII);

            LR2SongDBExtended.chart_info invalid = ChartInfoParser.Parse(invalidPath);

            Assert.IsFalse(invalid.total_defined);

            string decimalPath = Path.Combine(tempRootPath, "total-decimal.bms");
            File.WriteAllText(decimalPath, "#BPM 120\r\n#TOTAL 100.5\r\n#00111:01\r\n", Encoding.ASCII);

            LR2SongDBExtended.chart_info decimalTotal = ChartInfoParser.Parse(decimalPath);

            Assert.IsTrue(decimalTotal.total_defined);
            Assert.AreEqual(100.5, decimalTotal.total.GetValueOrDefault(), 0.000001);
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
    public void ParseBms_NormalNoteCollisionOverwritesExistingNoteLikeBeatoraja()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "normal-overwrite.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#001D1:01\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(0, row.ln);
            Assert.AreEqual(0, row.s);
            Assert.AreEqual(0, row.ls);
            Assert.AreEqual(0, row.feature & FeatureMine);
            StringAssert.StartsWith(row.lanenotes, "1,0,0,");
        });
    }

    [TestMethod]
    public void ParseBms_LongNoteEndRemovesInsideLaneNotesLikeBeatoraja()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "ln-inside-collision.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#00111:000100\r\n"
                    + "#00151:010001\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(0, row.n);
            Assert.AreEqual(1, row.ln);
            StringAssert.StartsWith(row.lanenotes, "0,1,0,");
        });
    }

    [TestMethod]
    public void ParseBms_MalformedChannelLineWithoutColonIsStillDecodedLikeBeatoraja()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "malformed-channel.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#00111;00001800\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(4, row.notes);
            Assert.AreEqual(4, row.n);
            StringAssert.StartsWith(row.lanenotes, "4,0,0,");
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

            ChartInfoParser.ChartInfoParseResult fromBytesResult;
            try
            {
                fromBytesResult = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256, timeout: TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Assert.Fail("Production diff fixture parse failed: fixture_id="
                    + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " exception=" + ex.GetType().Name
                    + " message=" + ex.Message);
                throw;
            }
            diffs.Add(expected, fromBytesResult.Row, fromBytesResult.ChartString);
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

            LR2SongDBExtended.chart_info actual;
            ChartInfoParser.ChartInfoParseResult fromBytesResult;
            try
            {
                actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);
                fromBytesResult = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256);
            }
            catch (Exception ex)
            {
                Assert.Fail("Production diff fixture parse failed: fixture_id="
                    + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " exception=" + ex.GetType().Name
                    + " message=" + ex.Message);
                throw;
            }
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
    public void ParseProductionDiffFixture_AllNonTimeoutRowsMatchBeatoraja()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_PRODUCTION_DIFF_FULL=1 to run the full production diff compatibility fixture.");
        }

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(759, rows.Count);
        List<RealChartInfoExpectedRow> rowsToVerify = rows
            .Where((RealChartInfoExpectedRow row) => !ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))
            .ToList();
        List<RealChartInfoExpectedRow> knownTimeoutRows = rows
            .Where((RealChartInfoExpectedRow row) => ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))
            .ToList();
        Assert.AreEqual(755, rowsToVerify.Count);
        Assert.AreEqual(4, knownTimeoutRows.Count);

        CompatibilityDiffCounts diffs = new CompatibilityDiffCounts();
        TimeSpan parserTimeout = TimeSpan.FromSeconds(ReadPositiveIntEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_TIMEOUT_SECONDS", 30));
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Trace.WriteLine("chart_info production diff non-timeout start total=" + rowsToVerify.Count
            + " excludedKnownTimeout=" + knownTimeoutRows.Count
            + " timeoutSeconds=" + parserTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
            + " reportDir=" + CompatibilityDiffCounts.GetReportDirectory());

        int processed = 0;
        foreach (RealChartInfoExpectedRow expected in rowsToVerify)
        {
            processed++;
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(chartPath), "Missing production diff chart fixture: " + expected.fixture_path);

            Stopwatch parseStopwatch = Stopwatch.StartNew();
            try
            {
                ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(
                    File.ReadAllBytes(chartPath),
                    chartPath,
                    expected.md5,
                    expected.sha256,
                    timeout: parserTimeout);
                parseStopwatch.Stop();
                diffs.AddParsed(expected, result.Row, result.ChartString, parseStopwatch.ElapsedMilliseconds);
            }
            catch (ChartInfoParser.ChartInfoParseTimeoutException ex)
            {
                parseStopwatch.Stop();
                diffs.AddTimeout(expected, parseStopwatch.ElapsedMilliseconds, ex);
            }
            catch (Exception ex)
            {
                parseStopwatch.Stop();
                diffs.AddParseFailure(expected, parseStopwatch.ElapsedMilliseconds, ex);
            }

            if (processed % 50 == 0 || processed == rowsToVerify.Count)
            {
                Trace.WriteLine("chart_info production diff progress processed=" + processed
                    + " parsed=" + diffs.ParsedCount
                    + " timeout=" + diffs.TimeoutCount
                    + " failed=" + diffs.ParseFailureCount
                    + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds);
            }
        }

        totalStopwatch.Stop();
        string reportPath = diffs.WriteReport("production_diff_non_timeout");
        Trace.WriteLine("chart_info production diff non-timeout done elapsedMs=" + totalStopwatch.ElapsedMilliseconds + " report=" + reportPath + " " + diffs);
        Assert.AreEqual(rowsToVerify.Count, diffs.ParsedCount, diffs.ToString());
        Assert.AreEqual(0, diffs.TimeoutCount, diffs.ToString());
        Assert.AreEqual(0, diffs.ParseFailureCount, diffs.ToString());
        Assert.AreEqual(0, diffs.TotalDiffs, diffs.ToString());
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    [Microsoft.VisualStudio.TestTools.UnitTesting.Ignore("Known slow production-diff fixtures currently hit the test timeout. Enable manually while working on parser performance.")]
    public void ParseProductionDiffFixture_KnownTimeoutRows_PerformanceAndExpectedValues()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;")
            .Where((RealChartInfoExpectedRow row) => ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))
            .ToList();

        Assert.AreEqual(4, rows.Count);
        CompatibilityDiffCounts diffs = new CompatibilityDiffCounts();
        TimeSpan parserTimeout = TimeSpan.FromSeconds(ReadPositiveIntEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_SLOW_TIMEOUT_SECONDS", 60));

        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            Stopwatch parseStopwatch = Stopwatch.StartNew();
            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(
                File.ReadAllBytes(chartPath),
                chartPath,
                expected.md5,
                expected.sha256,
                timeout: parserTimeout);
            parseStopwatch.Stop();
            diffs.AddParsed(expected, result.Row, result.ChartString, parseStopwatch.ElapsedMilliseconds);
        }

        string reportPath = diffs.WriteReport("production_diff_known_timeout");
        Trace.WriteLine("chart_info production diff known-timeout done report=" + reportPath + " " + diffs);
        Assert.AreEqual(rows.Count, diffs.ParsedCount, diffs.ToString());
        Assert.AreEqual(0, diffs.TotalDiffs, diffs.ToString());
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_ParsePathAndBytesAgreeForSamples()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        string[] sha256s =
        {
            "cf3203eb2b057ca03f6a1579eb50c1169c6cf2b6956058077313ab3eed5f5a3a",
            "dfc23c232b435b8abcfc9363a15400b4115a7bec0d66b0d405e6e6cdfcf224e2",
            "45d530304e95f336578c639f4e38551c380053a7b9b843d38106bad11cc4ce5e",
            "4cf26b3ba8d762de8db62eec0b7790a37da600b303aecffa6391018d83680266",
            "9aa0dd20de15bd0f7d0166afcdaf87f05c0e331fe8cdc3017d931781526a8559",
            "850175e80107119b507b42aa992b632bd3976a9ca7d1b348eb8af7bf854570e6",
            "418806ce0bcd1eecc2256b022d1aad8c9616f7c21389eab61e280952e0f68558",
            "0f9297f384c02a4060f31962e768dc6a34d83aa551fbc5a01c19a6b7c6c40b77",
            "2125eeb135073c7d1f20968bef763ac1dc6290fd836fcc29309c5d351b60667e",
            "b40404294f647c238462a47adf9f5e8a5a38832805c6bed7d8ed81926004afb6"
        };

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        foreach (string sha256 in sha256s)
        {
            RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
                "SELECT sc.fixture_id, sc.fixture_path, e.* "
                    + "FROM FixtureSampleChart sc "
                    + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                    + "WHERE sc.sha256 = ?;",
                sha256).Single();
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            LR2SongDBExtended.chart_info fromPath = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);
            LR2SongDBExtended.chart_info fromBytes = ChartInfoParser.ParseBytesDetailed(
                File.ReadAllBytes(chartPath),
                chartPath,
                expected.md5,
                expected.sha256,
                timeout: TimeSpan.FromSeconds(10)).Row;

            AssertChartInfoEquivalent(fromPath, fromBytes);
        }
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_JavaIntWrappedStopLengthMatchesBeatoraja()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        const string sha256 = "2125eeb135073c7d1f20968bef763ac1dc6290fd836fcc29309c5d351b60667e";
        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "WHERE sc.sha256 = ?;",
            sha256).Single();
        string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

        ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(
            File.ReadAllBytes(chartPath),
            chartPath,
            expected.md5,
            expected.sha256,
            timeout: TimeSpan.FromSeconds(10));

        Assert.AreEqual(expected.length, result.Row.length);
        Assert.AreEqual(expected.notes, result.Row.notes);
        Assert.AreEqual(expected.feature, result.Row.feature);
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_NoteCollisionCountsMatchBeatorajaSamples()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        string[] sha256s =
        {
            "4cf26b3ba8d762de8db62eec0b7790a37da600b303aecffa6391018d83680266",
            "dfc23c232b435b8abcfc9363a15400b4115a7bec0d66b0d405e6e6cdfcf224e2",
            "418806ce0bcd1eecc2256b022d1aad8c9616f7c21389eab61e280952e0f68558",
            "0f9297f384c02a4060f31962e768dc6a34d83aa551fbc5a01c19a6b7c6c40b77"
        };

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        foreach (string sha256 in sha256s)
        {
            RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
                "SELECT sc.fixture_id, sc.fixture_path, e.* "
                    + "FROM FixtureSampleChart sc "
                    + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                    + "WHERE sc.sha256 = ?;",
                sha256).Single();
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.n, actual.n, expected.sha256);
            Assert.AreEqual(expected.ln, actual.ln, expected.sha256);
            Assert.AreEqual(expected.s, actual.s, expected.sha256);
            Assert.AreEqual(expected.ls, actual.ls, expected.sha256);
            Assert.AreEqual(expected.lanenotes, actual.lanenotes, expected.sha256);
        }
    }

    [TestMethod]
    public void CompatibilityDiffCounts_RecordsSkippedProductionDiffRowsWithoutCountingDiffs()
    {
        RealChartInfoExpectedRow expected = CreateExpectedCompatibilityRow(new string('a', 64), new string('c', 64));
        LR2SongDBExtended.chart_info actual = CreateChartInfoRow(expected.sha256, expected.md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
        actual.charthash = new string('d', 64);

        CompatibilityDiffCounts diffs = new CompatibilityDiffCounts();
        diffs.AddTimeout(expected, 10001, new ChartInfoParser.ChartInfoParseTimeoutException(10000, "test"));
        diffs.AddParseFailure(expected, 12, new InvalidDataException("synthetic parse failure"));

        Assert.AreEqual(0, diffs.ParsedCount);
        Assert.AreEqual(1, diffs.TimeoutCount);
        Assert.AreEqual(1, diffs.ParseFailureCount);
        Assert.AreEqual(0, diffs.ChartHashDiffs);
        Assert.AreEqual(0, diffs.CoreDiffs);

        diffs.AddParsed(expected, actual, string.Empty, 30);

        Assert.AreEqual(1, diffs.ParsedCount);
        Assert.AreEqual(1, diffs.ChartHashDiffs);
        StringAssert.Contains(diffs.ToString(), "timeout=1");
        StringAssert.Contains(diffs.ToString(), "parseFailure=1");
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
    public void BackfillChartInfos_IgnoresMaintenanceEncodingAndUsesBeatorajaDefaultDecode()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "ms932-fullwidth-level.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL １\r\n"
                    + "#00111:01\r\n",
                Encoding.GetEncoding(932));

            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            TestableBmsFile file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            file.SetMaintenanceInfo(
                new BMSFileMaintenanceInfo(file)
                {
                    encoding = "ks_c_5601-1987?"
                },
                suppressPropertyChanged: true,
                registerEventHandlers: false);

            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            ChartInfoBuildService service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.IsNotNull(file.ChartInfo);
            Assert.AreEqual(1, file.ChartInfo.level);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info row = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", file.sha256).Single();
            Assert.AreEqual(1, row.level);
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
    public void BackfillChartInfos_JavaIntWrappedTimelineLogsParseDiagnosticAsSuccess()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-int-wrap-backfill.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 1\r\n"
                    + "#STOP01 90000\r\n"
                    + "#00009:" + string.Concat(Enumerable.Repeat("01", 40)) + "\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            TestableBmsFile file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            ChartInfoBuildService service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1, commitChunkSizeOverride: 1);
            List<string> logs = new List<string>();

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.ParseFailedCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.IsTrue(logs.Any((string message) => message.StartsWith("INFO chart_info_backfill parse_diagnostic", StringComparison.Ordinal)
                && message.Contains("code=\"BMS_JAVA_INT_TIME_WRAP\"")
                && message.Contains("parseFailed=false")));
            Assert.IsFalse(logs.Any((string message) => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
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

    private static void AssertJavaDoubleParseBits(string value, string expectedHexBits)
    {
        Assert.AreEqual(expectedHexBits, JavaDoubleParserJdk17.ParseDoubleBits(value).ToString("x16", CultureInfo.InvariantCulture), value);
    }

    private static void AssertChartInfoEquivalent(LR2SongDBExtended.chart_info expected, LR2SongDBExtended.chart_info actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.difficulty_defined, actual.difficulty_defined);
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
            difficulty_defined = true,
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

    private static RealChartInfoExpectedRow CreateExpectedCompatibilityRow(string sha256, string charthash)
    {
        return new RealChartInfoExpectedRow
        {
            fixture_id = 1,
            fixture_path = "synthetic.bms",
            sha256 = sha256,
            md5 = new string('b', 32),
            charthash = charthash,
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
            density = 1.0,
            peakdensity = 1.0,
            enddensity = 0.0,
            distribution = "#",
            speedchange = "120.0,0.0",
            speedchange_count = 0,
            lanenotes = "1,0,0"
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

    private static int ReadPositiveIntEnvironmentVariable(string name, int defaultValue)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }
        return defaultValue;
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
            count += LevelEqualsBeatoraja(expected.level, actual.level) ? 0 : 1;
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

        private static bool LevelEqualsBeatoraja(int? expected, int? actual)
        {
            if (expected == actual)
            {
                return true;
            }
            return expected.GetValueOrDefault() == 0 && !actual.HasValue;
        }
    }

    private sealed class CompatibilityDiffCounts
    {
        private readonly List<string> samples = new List<string>();

        private readonly List<FieldDiffRecord> fieldDiffRecords = new List<FieldDiffRecord>();

        private readonly Dictionary<string, int> fieldDiffCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        private readonly List<long> parseElapsedMilliseconds = new List<long>();

        private readonly List<ParseRecord> parseRecords = new List<ParseRecord>();

        private readonly List<string> skippedSamples = new List<string>();

        public int ParsedCount { get; private set; }

        public int TimeoutCount { get; private set; }

        public int ParseFailureCount { get; private set; }

        public long TotalElapsedMs => parseElapsedMilliseconds.Sum();

        public double AvgParseMs => parseElapsedMilliseconds.Count == 0 ? 0.0 : parseElapsedMilliseconds.Average();

        public long MaxParseMs => parseElapsedMilliseconds.Count == 0 ? 0L : parseElapsedMilliseconds.Max();

        public long ParseP95Ms
        {
            get
            {
                if (parseElapsedMilliseconds.Count == 0)
                {
                    return 0L;
                }
                long[] sorted = parseElapsedMilliseconds.OrderBy((long value) => value).ToArray();
                int index = Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
                return sorted[index];
            }
        }

        public int CoreDiffs { get; private set; }

        public int ChartHashDiffs { get; private set; }

        public int LengthDiffs { get; private set; }

        public int DensityDiffs { get; private set; }

        public int PeakDensityDiffs { get; private set; }

        public int EndDensityDiffs { get; private set; }

        public int DistributionDiffs { get; private set; }

        public int SpeedChangeDiffs { get; private set; }

        public int BpmIntegerDiffs { get; private set; }

        public int TotalDiffs => fieldDiffRecords.Count;

        public static string GetReportDirectory()
        {
            string configured = Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_REPORT_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }
            return Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoProductionDiff");
        }

        public void AddParsed(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString, long elapsedMilliseconds)
        {
            ParsedCount++;
            RecordParse(expected, elapsedMilliseconds, "success");
            Add(expected, actual, chartString);
        }

        public void AddTimeout(RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            TimeoutCount++;
            RecordParse(expected, elapsedMilliseconds, "timeout");
            AddSkippedSample("timeout", expected, elapsedMilliseconds, exception);
        }

        public void AddParseFailure(RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            ParseFailureCount++;
            RecordParse(expected, elapsedMilliseconds, "failed");
            AddSkippedSample("failed", expected, elapsedMilliseconds, exception);
        }

        public void Add(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString)
        {
            if (IsRandomFeature(expected.feature) || IsRandomFeature(actual.feature))
            {
                return;
            }
            AddCoreDiffs(expected, actual);
            if (!string.Equals(expected.charthash, actual.charthash, StringComparison.OrdinalIgnoreCase))
            {
                ChartHashDiffs++;
                AddFieldDiff("charthash", expected, expected.charthash, actual.charthash);
                AddSample("charthash", expected, expected.charthash, actual.charthash, chartString);
            }
            if (expected.length != actual.length)
            {
                LengthDiffs++;
                AddFieldDiff("length", expected, FormatValue(expected.length), FormatValue(actual.length));
            }
            if (!NullableDoubleEquals(expected.density, actual.density))
            {
                DensityDiffs++;
                AddFieldDiff("density", expected, FormatValue(expected.density), FormatValue(actual.density));
            }
            if (!NullableDoubleEquals(expected.peakdensity, actual.peakdensity))
            {
                PeakDensityDiffs++;
                AddFieldDiff("peakdensity", expected, FormatValue(expected.peakdensity), FormatValue(actual.peakdensity));
            }
            if (!NullableDoubleEquals(expected.enddensity, actual.enddensity))
            {
                EndDensityDiffs++;
                AddFieldDiff("enddensity", expected, FormatValue(expected.enddensity), FormatValue(actual.enddensity));
            }
            if (!string.Equals(expected.distribution, actual.distribution, StringComparison.Ordinal))
            {
                DistributionDiffs++;
                AddFieldDiff("distribution", expected, expected.distribution, actual.distribution);
            }
            if (!string.Equals(expected.speedchange, actual.speedchange, StringComparison.Ordinal))
            {
                SpeedChangeDiffs++;
                AddFieldDiff("speedchange", expected, expected.speedchange, actual.speedchange);
            }
            if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0)
                || (int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
            {
                BpmIntegerDiffs++;
                if ((int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
                {
                    AddFieldDiff("minbpm_integer", expected, FormatValue(expected.minbpm), FormatValue(actual.minbpm));
                }
                if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0))
                {
                    AddFieldDiff("maxbpm_integer", expected, FormatValue(expected.maxbpm), FormatValue(actual.maxbpm));
                }
                AddSample(
                    "bpm",
                    expected,
                    (expected.minbpm ?? 0.0).ToString("R") + "/" + (expected.maxbpm ?? 0.0).ToString("R"),
                    (actual.minbpm ?? 0.0).ToString("R") + "/" + (actual.maxbpm ?? 0.0).ToString("R"),
                    chartString);
            }
        }

        public string WriteReport(string name)
        {
            string directory = Path.Combine(GetReportDirectory(), name);
            Directory.CreateDirectory(directory);

            WriteLines(
                Path.Combine(directory, "field_diffs.csv"),
                new[] { "field,count" }.Concat(fieldDiffCounts
                    .OrderByDescending((KeyValuePair<string, int> pair) => pair.Value)
                    .ThenBy((KeyValuePair<string, int> pair) => pair.Key, StringComparer.Ordinal)
                    .Select((KeyValuePair<string, int> pair) => Csv(pair.Key) + "," + pair.Value.ToString(CultureInfo.InvariantCulture))));

            WriteLines(
                Path.Combine(directory, "diff_rows.csv"),
                new[] { "fixture_id,sha256,fixture_path,field,expected,actual" }.Concat(fieldDiffRecords.Select((FieldDiffRecord record) => record.ToCsv())));

            WriteLines(
                Path.Combine(directory, "skipped_rows.csv"),
                new[] { "sample" }.Concat(skippedSamples.Select(Csv)));

            WriteLines(
                Path.Combine(directory, "slow_parse_top.csv"),
                new[] { "rank,status,elapsed_ms,fixture_id,sha256,fixture_path" }.Concat(parseRecords
                    .OrderByDescending((ParseRecord record) => record.ElapsedMilliseconds)
                    .Take(20)
                    .Select((ParseRecord record, int index) =>
                        (index + 1).ToString(CultureInfo.InvariantCulture)
                            + "," + Csv(record.Status)
                            + "," + record.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                            + "," + record.FixtureId.ToString(CultureInfo.InvariantCulture)
                            + "," + Csv(record.Sha256)
                            + "," + Csv(record.FixturePath))));

            WriteLines(
                Path.Combine(directory, "summary.txt"),
                new[] { ToString() });

            return directory;
        }

        public override string ToString()
        {
            return "chart_info real compatibility diffs: "
                + "parsed=" + ParsedCount
                + " timeout=" + TimeoutCount
                + " parseFailure=" + ParseFailureCount
                + " totalParseMs=" + TotalElapsedMs
                + " avgParseMs=" + AvgParseMs.ToString("0.###", CultureInfo.InvariantCulture)
                + " maxParseMs=" + MaxParseMs
                + " p95ParseMs=" + ParseP95Ms
                + " core=" + CoreDiffs
                + " charthash=" + ChartHashDiffs
                + " length=" + LengthDiffs
                + " density=" + DensityDiffs
                + " peakdensity=" + PeakDensityDiffs
                + " enddensity=" + EndDensityDiffs
                + " distribution=" + DistributionDiffs
                + " speedchange=" + SpeedChangeDiffs
                + " bpmInteger=" + BpmIntegerDiffs
                + " totalDiffs=" + TotalDiffs
                + (fieldDiffCounts.Count == 0 ? string.Empty : " fieldTop=" + string.Join(" | ", fieldDiffCounts
                    .OrderByDescending((KeyValuePair<string, int> pair) => pair.Value)
                    .ThenBy((KeyValuePair<string, int> pair) => pair.Key, StringComparer.Ordinal)
                    .Take(20)
                    .Select((KeyValuePair<string, int> pair) => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture))))
                + (samples.Count == 0 ? string.Empty : " samples=" + string.Join(" | ", samples))
                + (skippedSamples.Count == 0 ? string.Empty : " skipped=" + string.Join(" | ", skippedSamples))
                + (parseRecords.Count == 0 ? string.Empty : " slowTop=" + string.Join(" | ", GetSlowTopRecords()));
        }

        private void AddCoreDiffs(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
        {
            CompareCoreField("level", expected, FormatValue(expected.level), FormatValue(actual.level), LevelEqualsBeatoraja(expected.level, actual.level));
            CompareCoreField("difficulty", expected, FormatValue(expected.difficulty), FormatValue(actual.difficulty), expected.difficulty == actual.difficulty);
            CompareCoreField("notes", expected, FormatValue(expected.notes), FormatValue(actual.notes), expected.notes == actual.notes);
            CompareCoreField("n", expected, FormatValue(expected.n), FormatValue(actual.n), expected.n == actual.n);
            CompareCoreField("ln", expected, FormatValue(expected.ln), FormatValue(actual.ln), expected.ln == actual.ln);
            CompareCoreField("s", expected, FormatValue(expected.s), FormatValue(actual.s), expected.s == actual.s);
            CompareCoreField("ls", expected, FormatValue(expected.ls), FormatValue(actual.ls), expected.ls == actual.ls);
            CompareCoreField("judge", expected, FormatValue(expected.judge), FormatValue(actual.judge), expected.judge == actual.judge);
            CompareCoreField("feature", expected, FormatValue(expected.feature), FormatValue(actual.feature), expected.feature == actual.feature);
            CompareCoreField("mode", expected, FormatValue(expected.mode), FormatValue(actual.mode), expected.mode == actual.mode);
            CompareCoreField("mainbpm", expected, FormatValue(expected.mainbpm), FormatValue(actual.mainbpm), NullableDoubleEquals(expected.mainbpm, actual.mainbpm));
            CompareCoreField("lanenotes", expected, expected.lanenotes, actual.lanenotes, string.Equals(expected.lanenotes, actual.lanenotes, StringComparison.Ordinal));
            CompareCoreField("total", expected, FormatValue(expected.total), FormatValue(actual.total), NullableDoubleEquals(expected.total, actual.total));
        }

        private static bool IsRandomFeature(int? feature)
        {
            return ((feature ?? 0) & 4) != 0;
        }

        private void CompareCoreField(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, bool equals)
        {
            if (equals)
            {
                return;
            }
            CoreDiffs++;
            AddFieldDiff(field, expected, expectedValue, actualValue);
        }

        private void AddFieldDiff(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue)
        {
            int count;
            fieldDiffCounts.TryGetValue(field, out count);
            fieldDiffCounts[field] = count + 1;
            fieldDiffRecords.Add(new FieldDiffRecord
            {
                FixtureId = expected.fixture_id,
                Sha256 = expected.sha256,
                FixturePath = expected.fixture_path,
                Field = field,
                Expected = expectedValue ?? string.Empty,
                Actual = actualValue ?? string.Empty
            });
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

        private void RecordParse(RealChartInfoExpectedRow expected, long elapsedMilliseconds, string status)
        {
            parseElapsedMilliseconds.Add(elapsedMilliseconds);
            parseRecords.Add(new ParseRecord
            {
                FixtureId = expected.fixture_id,
                Sha256 = expected.sha256,
                FixturePath = expected.fixture_path,
                ElapsedMilliseconds = elapsedMilliseconds,
                Status = status
            });
        }

        private void AddSkippedSample(string status, RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            if (skippedSamples.Count >= 10)
            {
                return;
            }
            skippedSamples.Add(
                status
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " elapsedMs=" + elapsedMilliseconds
                    + " exception=" + exception.GetType().Name
                    + " message=" + (exception.Message ?? string.Empty));
        }

        private IEnumerable<string> GetSlowTopRecords()
        {
            return parseRecords
                .OrderByDescending((ParseRecord record) => record.ElapsedMilliseconds)
                .Take(10)
                .Select((ParseRecord record) =>
                    "ranked"
                        + " status=" + record.Status
                        + " elapsedMs=" + record.ElapsedMilliseconds
                        + " fixture_id=" + record.FixtureId
                        + " path=" + record.FixturePath
                        + " sha256=" + record.Sha256);
        }

        private static string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoDiffs");
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        private static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }

        private static bool LevelEqualsBeatoraja(int? expected, int? actual)
        {
            if (expected == actual)
            {
                return true;
            }
            return expected.GetValueOrDefault() == 0 && !actual.HasValue;
        }

        private static string FormatValue(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatValue(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string FormatValue(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static void WriteLines(string path, IEnumerable<string> lines)
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private sealed class ParseRecord
        {
            public int FixtureId { get; set; }

            public string Sha256 { get; set; } = string.Empty;

            public string FixturePath { get; set; } = string.Empty;

            public long ElapsedMilliseconds { get; set; }

            public string Status { get; set; } = string.Empty;
        }

        private sealed class FieldDiffRecord
        {
            public int FixtureId { get; set; }

            public string Sha256 { get; set; } = string.Empty;

            public string FixturePath { get; set; } = string.Empty;

            public string Field { get; set; } = string.Empty;

            public string Expected { get; set; } = string.Empty;

            public string Actual { get; set; } = string.Empty;

            public string ToCsv()
            {
                return FixtureId.ToString(CultureInfo.InvariantCulture)
                    + "," + Csv(Sha256)
                    + "," + Csv(FixturePath)
                    + "," + Csv(Field)
                    + "," + Csv(Expected)
                    + "," + Csv(Actual);
            }
        }
    }
}
