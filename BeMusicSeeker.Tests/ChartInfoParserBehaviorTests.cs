using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using ChartInfoExportTool;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// Owns BMS/BMSON chart-info parser behavior and compatibility cases.
/// </summary>
[TestClass]
public sealed class ChartInfoParserBehaviorTests
{
    [TestMethod]
    public void ParseBms_SimpleFixture_ComputesChartMetadata()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "simple.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE Chart Info Test\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 12\r\n"
                    + "#DIFFICULTY 3\r\n"
                    + "#DEFEXRANK 120\r\n"
                    + "#EXLEVEL 9\r\n"
                    + "#RANK 3\r\n"
                    + "#TOTAL 300\r\n"
                    + "#BMP01 bg.png\r\n"
                    + "#00111:0100\r\n"
                    + "#00112:0001\r\n"
                    + "#00116:0100\r\n"
                    + "#00104:0100\r\n"
                    + "#00251:0101\r\n"
                    + "#003D1:01\r\n",
                Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);

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
            Assert.AreEqual(1, row.bga);
            Assert.AreEqual(9, row.exlevel);
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
    public void ParseBms_ExLevelDefaultsToZeroAndIgnoresDefExRank()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "exlevel-default.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE ExLevel Default\r\n"
                    + "#BPM 120\r\n"
                    + "#DEFEXRANK 120\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);

            Assert.AreEqual(0, row.exlevel);
        });
    }

    [TestMethod]
    public void ParseBms_SectionRateUsesParsedRateWithoutSubtractionDrift()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "section-rate.bms");
            var chart = new StringBuilder();
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
    public void JavaDoubleToStringJdk21_MatchesKnownCompatibilityCases()
    {
        Assert.AreEqual("0.0", JavaDoubleToStringJdk21.ToString(0.0));
        Assert.AreEqual("-0.0", JavaDoubleToStringJdk21.ToString(-0.0));
        Assert.AreEqual("NaN", JavaDoubleToStringJdk21.ToString(double.NaN));
        Assert.AreEqual("Infinity", JavaDoubleToStringJdk21.ToString(double.PositiveInfinity));
        Assert.AreEqual("-Infinity", JavaDoubleToStringJdk21.ToString(double.NegativeInfinity));
        Assert.AreEqual("4.9E-324", JavaDoubleToStringJdk21.ToString(double.Epsilon));
        Assert.AreEqual("9.9E-324", JavaDoubleToStringJdk21.ToString(1e-323));
        Assert.AreEqual("1.0E23", JavaDoubleToStringJdk21.ToString(1e23));
        Assert.AreEqual("2.0E23", JavaDoubleToStringJdk21.ToString(2e23));
        Assert.AreEqual("8.41E21", JavaDoubleToStringJdk21.ToString(8.41e21));
        Assert.AreEqual("7.6999669989E7", JavaDoubleToStringJdk21.ToString(7.6999669989E7));
        Assert.AreEqual("3.141592653589793", JavaDoubleToStringJdk21.ToString(Math.PI));
        Assert.AreEqual("1.7976931348623157E308", JavaDoubleToStringJdk21.ToString(double.MaxValue));
        Assert.AreEqual("1.1451419198103644E18", JavaDoubleToStringJdk21.ToString(1.1451419198103644E18));
        Assert.AreEqual("8.492905781983985E17", JavaDoubleToStringJdk21.ToString(8.492905781983985E17));
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);

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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);

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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
    public void ParseBms_InvalidCompactRandomCommandDoesNotSetRandomFeature()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "invalid-compact-random.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM2\r\n"
                    + "#IF 1\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n",
                Encoding.ASCII);

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            Assert.AreEqual(1, result.Row.notes);
            Assert.AreEqual(0, result.Row.feature & FeatureRandom);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "BMS_RANDOM_INVALID"));
        });
    }

    [TestMethod]
    public void ParseBms_TimelineLongerThanOneDayIsAllowed()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
            ChartInfoParser.ChartInfoParseDiagnostic diagnostic = result.Diagnostics.Single(item => item.Code == "BMS_JAVA_INT_TIME_WRAP");
            Assert.AreEqual(ChartInfoParser.ChartInfoParseDiagnosticSeverity.Info, diagnostic.Severity);
            StringAssert.Contains(diagnostic.Message, "rawMs=");
            StringAssert.Contains(diagnostic.Message, "wrappedMs=");
        });
    }

    [TestMethod]
    public void ParseBms_RandomRetrySkipsTimelineLongerThanIntMilliseconds()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-bpm.bms");
            File.WriteAllText(chartPath, "#00111:01\r\n", Encoding.ASCII);

            Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath));
        });
    }

    [TestMethod]
    public void ParseBms_MeasureZeroIndexedBpmCanDefineInitialTimelineBpm()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string[] texts =
            [
                "#BPM 0\r\n#00111:01\r\n",
                "#BPM -120\r\n#00111:01\r\n",
                "#BPM nope\r\n#00111:01\r\n"
            ];

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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
    public void ParseBms_RandomEndIfDoesNotSkipFollowingMainData()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-endif-main-data.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM 2\r\n"
                    + "#IF 1\r\n"
                    + "#00104:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 2\r\n"
                    + "#00104:02\r\n"
                    + "#ENDIF\r\n"
                    + "#00211:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() > 0);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_RandomEndRandomCanCloseRandomBlock()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-endrandom-main-data.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM 2\r\n"
                    + "#IF 2\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n"
                    + "#00211:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBmson_UnknownFieldsAreIgnoredAndUnsupportedModeFallsBackToBeat7()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        BmsonSoundNote[] notesWithEqualY =
        [
            new BmsonSoundNote { Y = 0, Continue = false },
            new BmsonSoundNote { Y = 0, Continue = true },
            new BmsonSoundNote { Y = 240, Continue = true }
        ];
        int nextDistinctNoteIndex = 0;
        Assert.AreSame(
            notesWithEqualY[2],
            ChartInfoParser.AdvanceToNextBmsonContinuationNote(
                notesWithEqualY,
                noteIndex: 0,
                ref nextDistinctNoteIndex));
        Assert.AreSame(
            notesWithEqualY[2],
            ChartInfoParser.AdvanceToNextBmsonContinuationNote(
                notesWithEqualY,
                noteIndex: 1,
                ref nextDistinctNoteIndex));
        Assert.IsNull(ChartInfoParser.AdvanceToNextBmsonContinuationNote(
            notesWithEqualY,
            noteIndex: 2,
            ref nextDistinctNoteIndex));

        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealBeatorajaCompatibilitySample_ReducesKnownDiffs()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the full BMS chart_info parser compatibility fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_real expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(1000, rows.Count);
        var diffs = new CompatibilityDiffCounts();
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

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
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
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealBmsonBeatorajaCompatibility_AllFixturesMatch()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the full BMSON chart_info parser compatibility fixture");

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_bmson_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_bmson_real expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(1034, rows.Count);
        var diffs = new BmsonCompatibilityDiffCounts();
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
    [TestCategory("ProductionDiffFull")]
    [TestCategory("LargeFixture")]
    public void ParseProductionDiffFixture_AllNonTimeoutRowsMatchBeatoraja()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_PRODUCTION_DIFF_FULL=1 to run the full production diff compatibility fixture.");
        }

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(759, rows.Count);
        List<RealChartInfoExpectedRow> rowsToVerify = [.. rows.Where(row => !ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))];
        List<RealChartInfoExpectedRow> knownTimeoutRows = [.. rows.Where(row => ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))];
        Assert.AreEqual(755, rowsToVerify.Count);
        Assert.AreEqual(4, knownTimeoutRows.Count);

        var diffs = new CompatibilityDiffCounts();
        var parserTimeout = TimeSpan.FromSeconds(ReadPositiveIntEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_TIMEOUT_SECONDS", 30));
        var totalStopwatch = Stopwatch.StartNew();
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

            var parseStopwatch = Stopwatch.StartNew();
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
    [TestCategory("ParserCompatibilitySlow")]
    [TestCategory("LargeFixture")]
    public void ParseProductionDiffFixture_KnownTimeoutRows_PerformanceAndExpectedValues()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_CHART_INFO_SLOW"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_CHART_INFO_SLOW=1 to run the slow chart_info parser fixtures.");
        }

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = [.. connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;")
            .Where(row => ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))];

        Assert.AreEqual(4, rows.Count);
        var diffs = new CompatibilityDiffCounts();
        var parserTimeout = TimeSpan.FromSeconds(ReadPositiveIntEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_SLOW_TIMEOUT_SECONDS", 60));

        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            var parseStopwatch = Stopwatch.StartNew();
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
        [
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
        ];

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
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
        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
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
        [
            "4cf26b3ba8d762de8db62eec0b7790a37da600b303aecffa6391018d83680266",
            "dfc23c232b435b8abcfc9363a15400b4115a7bec0d66b0d405e6e6cdfcf224e2",
            "418806ce0bcd1eecc2256b022d1aad8c9616f7c21389eab61e280952e0f68558",
            "0f9297f384c02a4060f31962e768dc6a34d83aa551fbc5a01c19a6b7c6c40b77"
        ];

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
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
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_InvalidCompactRandomDoesNotSetRandomFeature()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        const string sha256 = "e570cff02a4a43809229060f3b0d146efc2635060786a0f9a4f9158bf5265b9d";
        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "WHERE sc.sha256 = ?;",
            sha256).Single();
        string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

        ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256);

        Assert.AreEqual(expected.charthash, result.Row.charthash);
        Assert.AreEqual(expected.feature, result.Row.feature);
        Assert.AreEqual(0, result.Row.feature & FeatureRandom);
        Assert.IsTrue((result.Row.feature & FeatureMine) != 0);
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ProductionDiffFull")]
    [TestCategory("LargeFixture")]
    public void ParseProductionLatestDiffFixture_MatchesJdk21ReferenceForReportedFields()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_PRODUCTION_DIFF_FULL=1 to run the latest production diff compatibility fixture.");
        }

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_latest_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_latest_diff expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");
        Assert.AreEqual(7, rows.Count);

        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(
                File.ReadAllBytes(chartPath),
                chartPath,
                expected.md5,
                expected.sha256,
                timeout: TimeSpan.FromSeconds(60));

            LR2SongDBExtended.chart_info actual = result.Row;
            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
            Assert.AreEqual(expected.distribution, actual.distribution, expected.sha256);
            Assert.AreEqual(expected.speedchange, actual.speedchange, expected.sha256);
            AssertNullableDouble(expected.density, actual.density, expected.sha256 + " density");
            AssertNullableDouble(expected.peakdensity, actual.peakdensity, expected.sha256 + " peakdensity");
            AssertNullableDouble(expected.enddensity, actual.enddensity, expected.sha256 + " enddensity");
        }
    }

    [TestMethod]
    public void CompatibilityDiffCounts_RecordsSkippedProductionDiffRowsWithoutCountingDiffs()
    {
        RealChartInfoExpectedRow expected = CreateExpectedCompatibilityRow(new string('a', 64), new string('c', 64));
        LR2SongDBExtended.chart_info actual = CreateChartInfoRow(expected.sha256, expected.md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
        actual.charthash = new string('d', 64);

        var diffs = new CompatibilityDiffCounts();
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
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCase_InitialBpmDefinedByTimelineZeroMatchesBeatoraja()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the chart_info initial-BPM edge-case fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
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
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCases_LongTimelineReferenceChartsMatchBeatoraja()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the full chart_info long-timeline edge-case fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
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
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCases_RandomOverflowFixturesDoNotOverflow()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the chart_info RANDOM-overflow edge-case fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
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
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCases_RandomEndIfScopeReferenceMatchesBeatorajaCounts()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the chart_info RANDOM/ENDIF reference fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string chartPath = Path.Combine(fixtureRootPath, "charts", "b862bf34bf6fbe034a18cceb9178a7e44a864a77e3475b9d478b9b5e5a46ff01.bms");
        Assert.IsTrue(File.Exists(chartPath), "random_endif_scope_reference fixture is missing.");

        LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(
            chartPath,
            "a0dfd7d70a53e4752d39e09b23877cff",
            "b862bf34bf6fbe034a18cceb9178a7e44a864a77e3475b9d478b9b5e5a46ff01");

        Assert.IsTrue((actual.feature & FeatureRandom) != 0);
        Assert.AreEqual(2295, actual.notes);
        Assert.AreEqual(2255, actual.n);
        Assert.AreEqual(40, actual.s);
        Assert.AreEqual(136083, actual.length);
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCases_InitialBpmReferenceFatalChartsRemainFatal()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the chart_info fatal initial-BPM edge-case fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
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
        var startInfo = new ProcessStartInfo
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

}
