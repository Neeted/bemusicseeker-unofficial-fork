using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// BMS・bmsonの詳細解析と外部参照値に対する互換性を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoParserBehaviorTests
{
    /// <summary>旧0.21・versionless・現代versionの旧別名を同じJSON境界で受理し、旧形式だけresolutionを240へ固定してSTOPをpulse長のまま扱います。</summary>
    [DataTestMethod]
    [DataRow("\"version\":\"0.21\",", 480)]
    [DataRow("", 480)]
    [DataRow("\"version\":\"1.0.0\",", 240)]
    public void ParseBmson_LegacyAliasesMatchModernTimingAndHash(string version, int resolution)
    {
        string legacy = "{" + version + "\"info\":{\"initBPM\":120,\"resolution\":" + resolution + "},"
            + "\"bpmNotes\":[{\"y\":240,\"v\":240}],\"stopNotes\":[{\"y\":240,\"v\":240}],"
            + "\"soundChannel\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0},{\"x\":2,\"y\":240},{\"x\":3,\"y\":480}]}]}";
        const string modern = """
            {"version":"1.0.0","info":{"init_bpm":120,"resolution":240},
             "bpm_events":[{"y":240,"bpm":240}],"stop_events":[{"y":240,"duration":240}],
             "sound_channels":[{"name":"sound.wav","notes":[{"x":1,"y":0},{"x":2,"y":240},{"x":3,"y":480}]}]}
            """;
        ChartInfoParser.ChartInfoParseResult actual = ChartInfoParser.ParseBytesDetailed(Encoding.UTF8.GetBytes(legacy), "legacy.bmson");
        ChartInfoParser.ChartInfoParseResult expected = ChartInfoParser.ParseBytesDetailed(Encoding.UTF8.GetBytes(modern), "modern.bmson");
        Assert.AreEqual(120d, actual.Row.minbpm);
        Assert.AreEqual(240d, actual.Row.maxbpm);
        Assert.AreEqual(3, actual.Row.notes);
        Assert.AreEqual(1000, actual.Row.length);
        Assert.AreEqual(expected.ChartString, actual.ChartString);
        Assert.AreEqual(expected.Row.charthash, actual.Row.charthash);
        Assert.AreEqual(expected.Row.speedchange, actual.Row.speedchange);
        Assert.AreEqual(expected.Row.distribution, actual.Row.distribution);
        if (version.Contains("1.0.0", StringComparison.Ordinal))
        {
            Assert.AreEqual(480, BmsonJsonParser.Parse(legacy.Replace("\"resolution\":240", "\"resolution\":480")).Info.Resolution);
        }
    }

    /// <summary>各原文objectの現代名を位置にかかわらず優先し、null・空配列・大小文字違いを旧値で補いません。別info objectのpopulateと重複キーの順序も保持します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BmsonJsonParser_ModernNamesWinWithinEachOriginalObject(bool legacyFirst)
    {
        string Pair(string modern, string legacy) => legacyFirst ? legacy + "," + modern : modern + "," + legacy;
        string json = "{\"info\":{" + Pair("\"init_bpm\":null", "\"initBPM\":999") + "},"
            + Pair("\"bpm_events\":[]", "\"bpmNotes\":[{\"v\":999}]") + ","
            + Pair("\"STOP_EVENTS\":null", "\"stopNotes\":[{\"v\":999}]") + ","
            + Pair("\"sound_channels\":[]", "\"soundChannel\":[{\"name\":\"ignored.wav\"}]") + "}";
        BmsonDocument empty = BmsonJsonParser.Parse(json);
        Assert.AreEqual(0d, empty.Info.InitBpm);
        Assert.AreEqual(0, empty.BpmEvents.Length);
        Assert.AreEqual(0, empty.StopEvents.Length);
        Assert.AreEqual(0, empty.SoundChannels.Length);

        BmsonDocument events = BmsonJsonParser.Parse("{\"info\":{\"initBPM\":120},\"info\":{\"title\":\"kept\"},"
            + "\"bpmNotes\":[{" + Pair("\"bpm\":150,\"bpm\":180", "\"v\":999") + "}],"
            + "\"stopNotes\":[{" + Pair("\"DURATION\":null", "\"v\":999") + "}]}");
        Assert.AreEqual(120d, events.Info.InitBpm);
        Assert.AreEqual("kept", events.Info.Title);
        Assert.AreEqual(180d, events.BpmEvents.Single().Bpm);
        Assert.AreEqual(0d, events.StopEvents.Single().Duration);
        BmsonDocument duplicates = BmsonJsonParser.Parse("{\"bpmNotes\":[{\"v\":120}],\"bpmNotes\":[{\"v\":150,\"v\":180}]}");
        Assert.AreEqual(180d, duplicates.BpmEvents.Single().Bpm);
        // 旧形式の判定は最後のinfoに従い、先行infoの旧名を理由に現代形式のresolutionを上書きしません。
        BmsonDocument modernInfoLast = BmsonJsonParser.Parse("{\"info\":{\"initBPM\":120},\"info\":{\"init_bpm\":150,\"resolution\":480}}");
        Assert.AreEqual(150d, modernInfoLast.Info.InitBpm);
        Assert.AreEqual(480, modernInfoLast.Info.Resolution);
        BmsonDocument legacyInfoLast = BmsonJsonParser.Parse("{\"info\":{\"init_bpm\":150},\"info\":{\"initBPM\":120,\"resolution\":480}}");
        Assert.AreEqual(120d, legacyInfoLast.Info.InitBpm);
        Assert.AreEqual(240, legacyInfoLast.Info.Resolution);
    }

    /// <summary>現代名の不正値は旧値へfallbackせず入力失敗にし、旧resolutionの変換前固定でも構文不良を消して受理しません。</summary>
    [DataTestMethod]
    [DataRow("{\"info\":{\"initBPM\":120,\"init_bpm\":{}}}")]
    [DataRow("{\"bpmNotes\":[],\"bpm_events\":5}")]
    [DataRow("{\"bpmNotes\":[{\"v\":120,\"bpm\":{}}]}")]
    [DataRow("{\"stopNotes\":[{\"v\":120,\"duration\":{}}]}")]
    [DataRow("{\"soundChannel\":[],\"sound_channels\":5}")]
    [DataRow("{\"version\":\"0.21\",\"info\":{\"resolution\":{broken}}}")]
    public void BmsonJsonParser_ModernInvalidValuesAndMalformedLegacyResolutionRemainFatal(string json)
    {
        try
        {
            BmsonJsonParser.Parse(json);
            Assert.Fail("不正な現代名の値と壊れたJSONは受理しません。");
        }
        catch (Newtonsoft.Json.JsonException) { }
    }

    /// <summary>旧resolutionの型変換は行わず240へ固定し、単一引用符・コメント・escaped keyという既存JSON受理範囲を保持します。</summary>
    [TestMethod]
    public void BmsonJsonParser_LegacyResolutionIsFixedBeforeConversionWithoutReserializingValues()
    {
        BmsonDocument document = BmsonJsonParser.Parse("{\r\n'version':'0.21',/*旧形式*/'info':{'init\\u0042PM':120,'resolution':{}},'bpmNotes':[{'v':180}]}");
        Assert.AreEqual(240, document.Info.Resolution);
        Assert.AreEqual(120d, document.Info.InitBpm);
        Assert.AreEqual(180d, document.BpmEvents.Single().Bpm);
    }

    /// <summary>通常音符・LN・scratch・地雷・BGAを含むBMSから詳細を生成し、統計、明示難度・TOTAL、判定値、保存ハッシュの取違えを検出する。</summary>
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
                    + "#BMP01 bg.png\r\n#00104:01\r\n"
                    + "#00111:0100\r\n"
                    + "#00112:0001\r\n"
                    + "#00116:0100\r\n"
                    + "#00104:0100\r\n"
                    + "#00251:0101\r\n"
                    + "#003D1:01\r\n",
                Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath, digest.Md5, digest.Sha256);
            Assert.AreEqual(digest.Md5, row.md5);
            Assert.AreEqual(digest.Sha256, row.sha256);
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

    /// <summary>EXLEVEL未指定でDEFEXRANKだけあるBMSを読み、別ヘッダーをexlevelとして誤採用する互換違反を検出する。</summary>
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
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath, digest.Md5, digest.Sha256);

            Assert.AreEqual(0, row.exlevel);
        });
    }

    /// <summary>多数の小数小節倍率を積み上げても参照互換の長さとspeedchange終端を維持し、減算誤差が最終時刻をずらす誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(104600, row.length);
            Assert.AreEqual("180.0,0.0,180.0,104600.0", row.speedchange);
        });
    }

    /// <summary>小さいBPMをspeedchangeへ保存するときJava互換の指数表記を維持し、文字列化の差による外部比較不一致を検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            StringAssert.Contains(row.speedchange, "1.0E-4");
            Assert.IsFalse(row.speedchange.Contains("0.0001E0"));
        });
    }

    /// <summary>長い十進BPMをJavaと同じdoubleへ丸め、speedchangeとchart stringへ異なる近傍値を保存する誤りを検出する。</summary>
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
            StringAssert.Contains((result.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")), "B(114.15384615384616)");
        });
    }

    /// <summary>bmsonのJSON数値の原文から参照互換のBPMを生成し、中間変換で末尾桁が変わってハッシュ入力をずらす誤りを検出する。</summary>
    [DataTestMethod]
    [DataRow("modern")]
    [DataRow("legacy")]
    [DataRow("versionless")]
    public void ParseBmson_RawJsonDoublesUseJavaParseDoubleRounding(string format)
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-parse-double-bpm.bmson");
            string json = "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":114.15384615384615384615384615,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"bpm_events\":[{\"y\":240,\"bpm\":131.4889812233735}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":480}]}]"
                    + "}";
            if (format != "modern")
            {
                json = json.Replace("1.0.0", "0.21").Replace("init_bpm", "initBPM")
                    .Replace("bpm_events", "bpmNotes").Replace("\"bpm\"", "\"v\"").Replace("sound_channels", "soundChannel");
                if (format == "versionless") { json = json.Replace("\"version\":\"0.21\",", string.Empty); }
            }
            File.WriteAllText(chartPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            StringAssert.Contains(result.Row.speedchange, "131.4889812233735");
            StringAssert.Contains(result.Row.speedchange, "114.15384615384616");
            Assert.IsFalse(result.Row.speedchange.Contains("131.48898122337351"));
            StringAssert.Contains((result.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")), "B(131.4889812233735)");
        });
    }

    /// <summary>符号付きゼロ・非有限・subnormal・指数境界・最大値をJDK21互換の文字列へ変換し、保存表現の桁や指数の退行を検出する。</summary>
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

    /// <summary>十進・十六進表記と丸め境界を既知のIEEEビットへ変換し、符号、underflow、overflowや境界丸めの互換違反を検出する。</summary>
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

        AssertJavaDoubleParseBits("114.15384615384615384615384615", "405c89d89d89d89e");
        AssertJavaDoubleParseBits("131.4889812233735", "40606fa5bbf357e9");
    }

    /// <summary>音符として無効でも小節番号を持つ行を時間軸へ反映し、末尾speedchangeの参照互換時刻を短縮する誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(2352, row.length);
            Assert.AreEqual("102.0,0.0,102.0,439999.0", row.speedchange);
        });
    }

    /// <summary>STOP・scroll・charge LN・地雷を含むbmsonから詳細を生成し、各統計・feature・TOTAL換算とレーン別保存値の取り違えを検出する。</summary>
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
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath, digest.Md5, digest.Sha256);
            Assert.AreEqual(digest.Md5, row.md5);
            Assert.AreEqual(digest.Sha256, row.sha256);
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

    /// <summary>scroll指定の次のtimelineで既定rateへ戻し、一度の指定を後続時刻へ持ち越してspeedchangeを変える誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual("120.0,0.0,240.0,500.0,120.0,1000.0", row.speedchange);
            Assert.AreEqual(2, row.speedchange_count);
        });
    }

    /// <summary>重複するscroll・BGAキーは最後の値を採用し、省略されたinfoとrateは入力規格の互換値へ補完して前値の混在を検出する。</summary>
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

    /// <summary>途中で切れたJSONを失敗として伝え、破損データを既定内容で正常受理する誤りを検出する。</summary>
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

    /// <summary>level未指定と明示0を区別して小数値は参照通り切り捨て、未知levelの0化や難度の誤確定を検出する。</summary>
    [TestMethod]
    public void ParseBmson_LevelMissingExplicitZeroAndFloatTruncated()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string missingPath = Path.Combine(tempRootPath, "missing-level.bmson");
            File.WriteAllText(
                missingPath,
                "{\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            BeMusicSeeker.Models.ChartDetails missing = ChartInfoParser.Parse(missingPath);

            Assert.IsFalse(missing.level.HasValue);
            Assert.AreEqual(1, missing.difficulty);
            Assert.IsFalse(missing.difficulty_defined);

            string zeroPath = Path.Combine(tempRootPath, "zero-level.bmson");
            File.WriteAllText(
                zeroPath,
                "{\"info\":{\"level\":0,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            BeMusicSeeker.Models.ChartDetails zero = ChartInfoParser.Parse(zeroPath);

            Assert.AreEqual(0, zero.level);

            string floatPath = Path.Combine(tempRootPath, "float-level.bmson");
            File.WriteAllText(
                floatPath,
                "{\"info\":{\"level\":12.9,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240.9},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            BeMusicSeeker.Models.ChartDetails floatLevel = ChartInfoParser.Parse(floatPath);

            Assert.AreEqual(12, floatLevel.level);
        });
    }

    /// <summary>BASE62指定の小文字IDを定義と音符の両方へ適用し、IDの大小文字を同一視してBPM変更や音符を落とす誤りを検出する。</summary>
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
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath, digest.Md5, digest.Sha256);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(120.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.speedchange_count);
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    /// <summary>コロン区切りのBPM定義を参照互換で受理し、定義の未解決により最大・主BPMを元値へ戻す誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(120.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    /// <summary>小節0でBPMを変更しても初期値を最小最大へ含め、再生前の定義を統計から落とす誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(160.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    /// <summary>通常音符を一個ずつ持つ二枝のRANDOM入力を通常音符1・スクラッチ0として解析し、複数枝の音符混在やrandom featureの消失を検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(0, row.s);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    /// <summary>空白のない無効RANDOM指定を診断しつつ音符は解析し、無効指定をrandom featureへ誤分類する誤りを検出する。</summary>
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

    /// <summary>一日を超えるが保存可能な低BPM譜面を受理し、任意の一日上限で従来入力を拒否する誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() > 86400 * 1000);
        });
    }

    /// <summary>ミリ秒整数の保存範囲を超える時間軸を明示失敗へ伝え、overflowした長さで成功扱いする誤りを検出する。</summary>
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

    /// <summary>大きいSTOPの参照互換wrapを保持して情報診断を返し、正常音符やwrapのraw・変換時刻情報を落とす誤りを検出する。</summary>
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

    /// <summary>枝1が保存時間範囲を超えたRANDOM入力では解析可能な後続枝を選び、全譜面を失敗扱いする誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(2000, row.length.GetValueOrDefault());
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    /// <summary>不正なBPM・STOP・TOTAL定義を含んでも有効音符を解析し、TOTALを明示値扱いする誤りや全体拒否を検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsFalse(row.total_defined);
            Assert.IsTrue(row.total.GetValueOrDefault() > 0.0);
        });
    }

    /// <summary>完全な整数指定、無効指定からの推論、後続無効難度を区別し、明示状態や先行有効値を誤って上書きする互換違反を検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails valid = ChartInfoParser.Parse(validPath);

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

            BeMusicSeeker.Models.ChartDetails inferred = ChartInfoParser.Parse(inferredPath);

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

            BeMusicSeeker.Models.ChartDetails invalidAfterValid = ChartInfoParser.Parse(invalidAfterValidPath);

            Assert.IsFalse(invalidAfterValid.level.HasValue);
            Assert.AreEqual(4, invalidAfterValid.difficulty);
            Assert.IsTrue(invalidAfterValid.difficulty_defined);
        });
    }

    /// <summary>空白省略・等号区切り・Unicode数字のヘッダーを参照互換で読むことで、有効なlevel・難度・rankを取り落とす誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(28, row.level.GetValueOrDefault());
            Assert.AreEqual(2, row.difficulty);
            Assert.IsTrue(row.difficulty_defined);
            Assert.AreEqual(100, row.judge);
        });
    }

    /// <summary>TOTALの末尾ごみを未定義として扱い、小数の有効値は保持して部分文字列の誤受理や整数化を検出する。</summary>
    [TestMethod]
    public void ParseBms_TotalRejectsTrailingGarbageButAcceptsDecimal()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string invalidPath = Path.Combine(tempRootPath, "total-invalid.bms");
            File.WriteAllText(invalidPath, "#BPM 120\r\n#TOTAL 100abc\r\n#00111:01\r\n", Encoding.ASCII);

            BeMusicSeeker.Models.ChartDetails invalid = ChartInfoParser.Parse(invalidPath);

            Assert.IsFalse(invalid.total_defined);

            string decimalPath = Path.Combine(tempRootPath, "total-decimal.bms");
            File.WriteAllText(decimalPath, "#BPM 120\r\n#TOTAL 100.5\r\n#00111:01\r\n", Encoding.ASCII);

            BeMusicSeeker.Models.ChartDetails decimalTotal = ChartInfoParser.Parse(decimalPath);

            Assert.IsTrue(decimalTotal.total_defined);
            Assert.AreEqual(100.5, decimalTotal.total.GetValueOrDefault(), 0.000001);
        });
    }

    /// <summary>初期BPMも時刻0の代替定義もない入力を失敗にし、時刻を計算できない譜面の正常受理を検出する。</summary>
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

    /// <summary>初期BPMが省略されても時刻0のindexed定義で解析し、参照互換の主BPM・音符と最小値を失う誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(0.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.notes);
        });
    }

    /// <summary>初期BPM省略時に時刻0の直接BPMで解析を開始し、indexed経路だけを受理する誤りや統計値の退行を検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(0.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.notes);
        });
    }

    /// <summary>ゼロ・負値・非数値の初期BPMを代替定義なしで受理せず、不正な時間計算を成功扱いする誤りを検出する。</summary>
    [TestMethod]
    public void ParseBms_InvalidInitialBpmWithoutTimelineZeroBpmIsFatal()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string[] texts =
            [
                // 時刻0に代替定義がないゼロ・負値・非数値をそれぞれ検査する。
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

    /// <summary>枝1に初期BPMがなくても後続枝の時刻0定義で再解析し、random譜面の有効枝を取り落とす誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    /// <summary>小節倍率で保存時間範囲を超える枝を避けて後続枝を解析し、倍率由来の失敗を再試行不能にする誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() < 86400 * 1000);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    /// <summary>IF終了後の共通音符を読むことで、RANDOMの枝状態が後続の主データまで除外する誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() > 0);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    /// <summary>ENDRANDOMで枝の適用範囲を閉じて後続音符を読み、非選択枝の状態をブロック外へ持ち越す誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    /// <summary>未知JSON欄を無視し未対応modeの参照互換7鍵として解析を維持して、有効音符の拒否や統計欠落を検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(7, row.mode);
            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    /// <summary>LNのchart stringをbeatoraja互換の数字markerでハッシュ化し、通常音符と混同する誤りを独立した期待ハッシュで検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual("c903e9b613077c6540374617cd9f5fd1916bcc77ba8e3e8770d30bea10b30319", row.charthash);
            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.ln);
        });
    }

    /// <summary>同時刻同レーンの地雷を後の通常音符で置換し、統計・feature・レーン数へ古い音符が残る誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(0, row.ln);
            Assert.AreEqual(0, row.s);
            Assert.AreEqual(0, row.ls);
            Assert.AreEqual(0, row.feature & FeatureMine);
            StringAssert.StartsWith(row.lanenotes, "1,0,0,");
        });
    }

    /// <summary>LN終端確定時に範囲内の通常音符を除き、二重計数やレーン別統計の残留を検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(0, row.n);
            Assert.AreEqual(1, row.ln);
            StringAssert.StartsWith(row.lanenotes, "0,1,0,");
        });
    }

    /// <summary>コロン以外の区切りを持つ既存互換入力から参照と同じ音符数を生成し、従来受理した行の取り落としを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(4, row.notes);
            Assert.AreEqual(4, row.n);
            StringAssert.StartsWith(row.lanenotes, "4,0,0,");
        });
    }

    /// <summary>同時刻継続音符の探索とLNの音声継続時刻を反映し、音符数が同じでも異なる音声区間のchart hashを同一化する誤りを検出する。</summary>
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

            BeMusicSeeker.Models.ChartDetails first = ChartInfoParser.Parse(firstPath);
            BeMusicSeeker.Models.ChartDetails second = ChartInfoParser.Parse(secondPath);

            Assert.AreEqual(first.notes, second.notes);
            Assert.AreEqual(first.ln, second.ln);
            Assert.AreNotEqual(first.charthash, second.charthash);
        });
    }

    /// <summary>実BMS千件を独立beatoraja期待へ照合し、核となる詳細・ハッシュ・整数BPMの差分と既存許容幅を超える分布・密度・長さの退行を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。期待または解析結果にRandomがある行は値比較から除外する。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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
            diffs.Add(expected, fromBytesResult.Row, (fromBytesResult.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")));
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

    /// <summary>重複JSONキーを持つ二つの実bmsonを参照DBへ照合し、キー採用順によるハッシュ・音符・長さの互換違反を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

            BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
        }
    }

    /// <summary>実bmson千三十四件の比較対象の詳細・ハッシュ・長さ・密度・TOTALを参照DBへ照合し、互換正規化と許容誤差内の一致を維持する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。levelの期待0と解析結果nullを同等とし、min/max BPMは整数化、浮動小数値は1e-6以内の差を許容する。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

            BeMusicSeeker.Models.ChartDetails actual;
            ChartInfoParser.ChartInfoParseResult fromBytesResult;
            try
            {
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
            actual = fromBytesResult.Row;
            diffs.Add(expected, actual, (fromBytesResult.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")));
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

    /// <summary>既知の遅い四件を分離した実BMS七百五十五件を期限内に参照DBへ照合し、新規timeout・解析失敗・保存値の差分を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。期待または解析結果にRandomがある行は値比較から除外する。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ProductionDiffFull")]
    [TestCategory("LargeFixture")]
    public void ParseProductionDiffFixture_AllNonTimeoutRowsMatchBeatoraja()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("本番diff全件の互換検査にはBMS_TEST_PRODUCTION_DIFF_FULL=1の明示指定が必要。");
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
                diffs.AddParsed(expected, result.Row, (result.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")), parseStopwatch.ElapsedMilliseconds);
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

    /// <summary>通常群から分離した既知の遅い四件を明示実行時の期限内で解析し、解析完了と参照値一致の退行を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。期待または解析結果にRandomがある行は値比較から除外する。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilitySlow")]
    [TestCategory("LargeFixture")]
    public void ParseProductionDiffFixture_KnownTimeoutRows_PerformanceAndExpectedValues()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_CHART_INFO_SLOW"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("遅いchart_info fixtureの検査にはBMS_TEST_CHART_INFO_SLOW=1の明示指定が必要。");
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
            diffs.AddParsed(expected, result.Row, (result.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")), parseStopwatch.ElapsedMilliseconds);
        }

        string reportPath = diffs.WriteReport("production_diff_known_timeout");
        Trace.WriteLine("chart_info production diff known-timeout done report=" + reportPath + " " + diffs);
        Assert.AreEqual(rows.Count, diffs.ParsedCount, diffs.ToString());
        Assert.AreEqual(0, diffs.TotalDiffs, diffs.ToString());
    }

    /// <summary>実STOP overflow入力の長さ・音符・featureを参照DBへ照合し、Java整数wrapの互換を合成入力だけでは見落とす誤りを検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

    /// <summary>衝突を含む四つの実譜面を参照DBへ照合し、通常・LN・scratch各計数とレーン別保存値の互換違反を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_NoteCollisionCountsMatchBeatorajaSamples()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        // 個々の外部入力はSHA-256と参照DBのfixture_pathで特定する。
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

            BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.n, actual.n, expected.sha256);
            Assert.AreEqual(expected.ln, actual.ln, expected.sha256);
            Assert.AreEqual(expected.s, actual.s, expected.sha256);
            Assert.AreEqual(expected.ls, actual.ls, expected.sha256);
            Assert.AreEqual(expected.lanenotes, actual.lanenotes, expected.sha256);
        }
    }

    /// <summary>無効な省略RANDOMを含む実譜面で参照hashとfeatureを保ち、地雷は残してrandomを誤設定する分類違反を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

    /// <summary>報告された七つの実差分入力をJDK21参照へ照合し、ハッシュ・長さ・分布・速度・密度の再発を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ProductionDiffFull")]
    [TestCategory("LargeFixture")]
    public void ParseProductionLatestDiffFixture_MatchesJdk21ReferenceForReportedFields()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("最新の本番diff全件の互換検査にはBMS_TEST_PRODUCTION_DIFF_FULL=1の明示指定が必要。");
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

            BeMusicSeeker.Models.ChartDetails actual = result.Row;
            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
            Assert.AreEqual(expected.distribution, actual.distribution, expected.sha256);
            Assert.AreEqual(expected.speedchange, actual.speedchange, expected.sha256);
            AssertNullableDouble(expected.density, actual.density, expected.sha256 + " density");
            AssertNullableDouble(expected.peakdensity, actual.peakdensity, expected.sha256 + " peakdensity");
            AssertNullableDouble(expected.enddensity, actual.enddensity, expected.sha256 + " enddensity");
        }
    }

    /// <summary>timeoutと解析失敗を成功差分とは別に数え、後から成功解析した行だけのハッシュ差を集計して失敗行の混入を検出する。</summary>
    [TestMethod]
    public void CompatibilityDiffCounts_RecordsSkippedProductionDiffRowsWithoutCountingDiffs()
    {
        RealChartInfoExpectedRow expected = CreateExpectedCompatibilityRow(new string('a', 64), new string('c', 64));
        BeMusicSeeker.Models.ChartDetails actual = CreateChartInfoRow(expected.sha256, expected.md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
        actual = actual with { charthash = new string('d', 64) };

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

    /// <summary>時刻0の定義で初期BPMを補う実譜面を参照DBへ照合し、ハッシュ・長さ・音符・BPM統計の互換違反を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

        BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mainbpm.GetValueOrDefault(), actual.mainbpm.GetValueOrDefault(), 0.000001);
        Assert.AreEqual((int)(expected.minbpm ?? 0.0), (int)(actual.minbpm ?? 0.0));
        Assert.AreEqual((int)(expected.maxbpm ?? 0.0), (int)(actual.maxbpm ?? 0.0));
    }

    /// <summary>長い時間軸の二つの実入力を参照DBへ照合し、長さだけでなく分布・密度・ハッシュを変える時刻退行を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

            BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
            Assert.AreEqual(expected.distribution, actual.distribution, expected.sha256);
            Assert.AreEqual(expected.density.GetValueOrDefault(), actual.density.GetValueOrDefault(), 0.000001, expected.sha256);
        }
    }

    /// <summary>overflowから再試行する四つの実random入力を解析し、例外なくrandom状態と正の音符・非負の長さを成立させる保証の退行を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

            BeMusicSeeker.Models.ChartDetails actual;
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

    /// <summary>実RANDOM/ENDIFの範囲入力で参照の音符・scratch・長さを保ち、枝状態による大量の主データ脱落を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

        BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(
            chartPath,
            "a0dfd7d70a53e4752d39e09b23877cff",
            "b862bf34bf6fbe034a18cceb9178a7e44a864a77e3475b9d478b9b5e5a46ff01");

        Assert.IsTrue((actual.feature & FeatureRandom) != 0);
        Assert.AreEqual(2295, actual.notes);
        Assert.AreEqual(2255, actual.n);
        Assert.AreEqual(40, actual.s);
        Assert.AreEqual(136083, actual.length);
    }

    /// <summary>参照でも初期BPM不足として失敗する二つの実入力を拒否し、既定BPM補完で失敗を隠す誤りを検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
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

    /// <summary>専用環境の明示実行でJava参照toolを実譜面へ接続し、成功JSONとchart hashの出力が失われる誤りを検出する。</summary>
    /// <remarks>ローカルJDKと参照リポジトリを準備する専用環境で実行し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("CompatibilityTool")]
    [TestCategory("EnvironmentIntegration")]
    public async Task ChartStringDumpTool_BuildsAndDumpsReferenceChartString()
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

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = "-ExecutionPolicy Bypass -File \"" + scriptPath + "\" \"" + chartPath + "\"",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? process = null;
        Task[] ownedTasks = [];
        Exception? primaryFailure = null;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start chartstring-dump.");
            Task exitTask = process.WaitForExitAsync();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            ownedTasks = [exitTask, stdoutTask, stderrTask];
            try
            {
                await Task.WhenAll(ownedTasks).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Assert.Fail("chartstring-dump did not finish with both output streams within 30 seconds.");
            }
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            Assert.AreEqual(0, process.ExitCode, stdout + Environment.NewLine + stderr);
            StringAssert.Contains(stdout, "\"ok\":true");
            StringAssert.Contains(stdout, "\"charthash\":");
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
            try
            {
                await Task.WhenAll(ownedTasks).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                cleanupFailure = cleanupFailure is null ? ex : new AggregateException(cleanupFailure, ex);
            }
            process?.Dispose();
            if (cleanupFailure is not null)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["chartstring-dump.cleanup"] = cleanupFailure;
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

    }

}
