using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.BMS;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

/// <summary>再生用bmsonの全曲時計、入力正規化、共有sliceを音源なしで検証します。</summary>
[TestClass]
public sealed class BmsonPlaybackParserTests
{
    /// <summary>
    /// 小節線の省略・null・空・不規則・開始点重複を表示制御へだけ反映し、全曲pulseの発音時計を変更する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void GlobalPulseAndDisplayLinesDoNotChangeAudioClock()
    {
        PlaybackChart baseline = Parse(Chart("120", "[{\"y\":0},{\"y\":240},{\"y\":480}]"));
        CollectionAssert.AreEqual(new long[] { 0, 5000000, 10000000 }, baseline.AudioEvents.Select(item => item.Start.ToTimeSpan().Ticks).ToArray());
        foreach (string lines in new[] { "[]", "[{\"y\":13},{\"y\":701}]", "null" })
        {
            PlaybackChart changed = Parse(Chart("120", "[{\"y\":0},{\"y\":240},{\"y\":480}]", ",\"lines\":" + lines));
            CollectionAssert.AreEqual(baseline.AudioEvents.ToArray(), changed.AudioEvents.ToArray());
        }
        PlaybackChart empty = Parse(Chart("120", "[{\"y\":960}]", ",\"lines\":[]"));
        PlaybackChart omitted = Parse(Chart("120", "[{\"y\":960}]"));
        PlaybackChart nullLines = Parse(Chart("120", "[{\"y\":960}]", ",\"lines\":null"));
        Assert.AreEqual(0, empty.LastMeasure);
        Assert.IsFalse(empty.Controls.Any(item => item.Kind is PlaybackControlKind.BarLine or PlaybackControlKind.InitialBarLine));
        Assert.AreEqual(1, omitted.LastMeasure);
        CollectionAssert.AreEqual(omitted.Controls.ToArray(), nullLines.Controls.ToArray());
        Assert.AreEqual(omitted.LastMeasure, nullLines.LastMeasure);
        foreach (int initialLines in new[] { 0, 1, 2 })
        {
            string lines = "[" + string.Concat(Enumerable.Repeat("{\"y\":0},", initialLines)) + "{\"y\":240},{\"y\":480}]";
            PlaybackChart chart = Parse(Chart("120", "[{\"y\":0},{\"y\":240},{\"y\":480}]", ",\"lines\":" + lines));
            Assert.AreEqual(initialLines, chart.Controls.Count(item => item.Kind == PlaybackControlKind.InitialBarLine));
            Assert.AreEqual(2, chart.LastMeasure);
            CollectionAssert.AreEqual(baseline.AudioEvents.ToArray(), chart.AudioEvents.ToArray());
        }
    }

    /// <summary>
    /// 長い十進BPM・指数表記・数値文字列を同じ正確な時刻へ変換し、途中double化や無関係なノートによる丸め差を検出する。
    /// </summary>
    [TestMethod]
    public void DecimalLexemesAndExponentKeepExactTimeAndIgnoreAdditionalNotes()
    {
        const string bpm = "123.456789012345678901234567890";
        PlaybackChart chart = Parse(Chart(bpm, "[{\"y\":1000000}]"));
        BigInteger numerator = (BigInteger)1000000 * 600000000 * BigInteger.Pow(10, 27) * (BigInteger.One << 32);
        BigInteger denominator = (BigInteger)240 * BigInteger.Parse("123456789012345678901234567890");
        Assert.AreEqual(RoundEven(numerator, denominator), chart.AudioEvents[0].Start.Subticks);
        PlaybackChart exponent = Parse(Chart("1.23456789012345678901234567890e2", "[{\"y\":17},{\"y\":1000000}]"));
        Assert.AreEqual(chart.AudioEvents[0].Start, exponent.AudioEvents[1].Start);
        PlaybackChart numericString = Parse(Chart("\"" + bpm + "\"", "[{\"y\":1000000}]"));
        Assert.AreEqual(chart.AudioEvents[0].Start, numericString.AudioEvents[0].Start);
    }

    /// <summary>
    /// 非正のBPM変更を時計・終端・範囲から除外し、同pulseの有効な最終BPMとSTOP計算を置換する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void IgnoredNonpositiveBpmChangesDoNotCreateAnchorsExtendDurationOrReplaceLastValidBpm()
    {
        const string notes = "[{\"y\":0},{\"y\":240,\"c\":true},{\"y\":480,\"c\":true}]";
        PlaybackChart baseline = Parse(Chart("123.456789012345678901234567890", notes));
        PlaybackChart ignored = Parse(Chart("123.456789012345678901234567890", notes,
            ",\"bpm_events\":[{\"y\":17,\"bpm\":0},{\"y\":400,\"bpm\":-3},{\"y\":2000,\"bpm\":0}]"));
        CollectionAssert.AreEqual(baseline.AudioEvents.ToArray(), ignored.AudioEvents.ToArray());
        Assert.AreEqual(baseline.Duration, ignored.Duration);
        Assert.AreEqual(baseline.MinBpm, ignored.MinBpm);
        Assert.AreEqual(baseline.MaxBpm, ignored.MaxBpm);
        Assert.AreEqual(0, ignored.Controls.Count(item => item.Kind == PlaybackControlKind.Bpm));
        PlaybackChart samePulse = Parse(Chart("120", "[{\"y\":240},{\"y\":480}]",
            ",\"bpm_events\":[{\"y\":240,\"bpm\":60},{\"y\":240,\"bpm\":0},{\"y\":240,\"bpm\":180},{\"y\":240,\"bpm\":-2}],\"stop_events\":[{\"y\":240,\"duration\":240}]"));
        Assert.AreEqual(60d, samePulse.MinBpm.ToDouble());
        Assert.AreEqual(180d, samePulse.MaxBpm.ToDouble());
        BigInteger grid = BigInteger.One << 32;
        Assert.AreEqual(5000000 * grid + 2 * RoundEven(10000000 * grid, 3), samePulse.AudioEvents[1].Start.Subticks);
    }

    /// <summary>
    /// 同pulseの発音は変更前時刻、後続は最終BPMとSTOP合計で進め、制御の適用順やSTOPの取りこぼしを検出する。
    /// </summary>
    [TestMethod]
    public void SamePulseSoundsBeforeLastBpmAndSummedStops()
    {
        PlaybackChart chart = Parse(Chart("120", "[{\"y\":240},{\"y\":360},{\"y\":480},{\"y\":720}]",
            ",\"bpm_events\":[{\"y\":240,\"bpm\":60},{\"y\":240,\"bpm\":180}],\"stop_events\":[{\"y\":240,\"duration\":60},{\"y\":240,\"duration\":180}]"));
        BigInteger grid = BigInteger.One << 32;
        BigInteger anchor = 5000000 * grid + RoundEven(10000000 * grid, 3);
        CollectionAssert.AreEqual(new[] { 5000000 * grid, anchor + RoundEven(5000000 * grid, 3), anchor + RoundEven(10000000 * grid, 3), anchor + RoundEven(20000000 * grid, 3) },
            chart.AudioEvents.Select(item => item.Start.Subticks).ToArray());
    }

    /// <summary>
    /// 継続sliceは再開始後の壁時計を使い、終端尾部の重なりとSTOP時間も保持して音源区間の切断・ずれを検出する。
    /// </summary>
    [TestMethod]
    public void ContinuationsUseRestartWallTimeAndEofTailsOverlap()
    {
        PlaybackChart chart = Parse(Chart("120", "[{\"y\":0,\"c\":true},{\"y\":240,\"c\":true},{\"y\":480},{\"y\":720,\"c\":true}]"));
        CollectionAssert.AreEqual(new long[] { 0, 5000000, 0, 5000000 }, chart.AudioEvents.Select(item => item.SourceStart.ToTimeSpan().Ticks).ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), chart.AudioEvents[0].SourceEnd?.ToTimeSpan());
        Assert.IsNull(chart.AudioEvents[1].SourceEnd);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), chart.AudioEvents[2].SourceEnd?.ToTimeSpan());
        Assert.IsNull(chart.AudioEvents[3].SourceEnd);
        PlaybackChart stopped = Parse(Chart("120", "[{\"y\":0},{\"y\":240,\"c\":true},{\"y\":480,\"c\":true}]",
            ",\"stop_events\":[{\"y\":240,\"duration\":240}]"));
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), stopped.AudioEvents[2].SourceStart.ToTimeSpan());
    }

    /// <summary>
    /// 同チャンネル・同pulseの層を一sliceへまとめ、記述順にかかわらず再開始を優先して音声とノート数の重複を検出する。
    /// </summary>
    [TestMethod]
    public void SameChannelPulseIsOneSliceAndAnyRestartWinsRegardlessOfNoteOrder()
    {
        foreach (string notes in new[] { "[{\"y\":240,\"x\":1,\"c\":true},{\"y\":240,\"x\":2},{\"y\":480,\"c\":true}]",
            "[{\"y\":240,\"x\":2},{\"y\":240,\"x\":1,\"c\":true},{\"y\":480,\"c\":true}]" })
        {
            PlaybackChart chart = Parse(Chart("120", notes));
            Assert.AreEqual(2, chart.AudioEvents.Count);
            Assert.AreEqual(BigInteger.Zero, chart.AudioEvents[0].SourceStart.Subticks);
            Assert.AreEqual(TimeSpan.FromSeconds(0.5), chart.AudioEvents[1].SourceStart.ToTimeSpan());
            Assert.AreEqual(2, chart.TotalNoteCount);
        }
    }

    /// <summary>
    /// 十進BPM変更とSTOPの合成誤差を固定格子の許容範囲に収め、区間ごとの丸め誤差の過剰な累積を検出する。
    /// </summary>
    [TestMethod]
    public void MultipleDecimalControlContributionsStayWithinTheFixedGridErrorBound()
    {
        PlaybackChart chart = Parse(Chart("7.1", "[{\"y\":0},{\"y\":3,\"c\":true}]",
            ",\"bpm_events\":[{\"y\":1,\"bpm\":11.3},{\"y\":2,\"bpm\":13.7}],\"stop_events\":[{\"y\":2,\"duration\":0.5}]"));
        BigInteger denominator = 2 * 240 * 71 * 113 * 137;
        BigInteger numerator = (BigInteger)6000000000 * (2 * 113 * 137 + 2 * 71 * 137 + 3 * 71 * 113);
        var difference = BigInteger.Abs(chart.AudioEvents[1].Start.Subticks * denominator - (numerator << 32));
        Assert.IsTrue(2 * difference <= 4 * denominator, "Two boundary deltas, one STOP and one local note contribute at most four half-subticks.");
        Assert.AreEqual(chart.AudioEvents[1].Start, chart.AudioEvents[1].SourceStart);
    }

    /// <summary>
    /// 固定subtickのちょうど半分を偶数へ丸め、二つの境界入力から丸め方向の偏りを検出する。
    /// </summary>
    [TestMethod]
    public void FixedSubtickContributionsRoundTiesToEven()
    {
        PlaybackChart chart = Parse(Chart("4096", "[{\"y\":1},{\"y\":3}]", info: ",\"resolution\":1073741824"));
        Assert.AreEqual(new BigInteger(585938), chart.AudioEvents[0].Start.Subticks);
        Assert.AreEqual(new BigInteger(1757812), chart.AudioEvents[1].Start.Subticks);
    }

    /// <summary>
    /// 解像度の欠落・null・ゼロ・負値・数値文字列でも四分音符時刻とBGM扱いを保ち、正規化で発音時刻がずれる誤りを検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("")]
    [DataRow(",\"resolution\":null")]
    [DataRow(",\"resolution\":0")]
    [DataRow(",\"resolution\":-240")]
    [DataRow(",\"resolution\":\"240\"")]
    public void ResolutionDefaultsAndNumericStringsKeepQuarterTime(string resolution)
    {
        PlaybackChart chart = Parse(Chart("\"120\"", "[{\"x\":null,\"y\":\"240\",\"l\":\"0\"}]", info: resolution));
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), chart.AudioEvents.Single().Start.ToTimeSpan());
        Assert.AreEqual(0, chart.TotalNoteCount);
        Assert.AreEqual("", chart.Title);
        Assert.AreEqual(100d, chart.Total);
        Assert.AreEqual("beat-7k", chart.ModeHint);
    }

    /// <summary>
    /// 解像度とpulseの倍増では時刻を保ち、最小整数の絶対値でも桁が回り込んで時計が変わる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ResolutionCanBeDoubledOrAbsoluteMinimumIntegerWithoutWrapping()
    {
        Assert.AreEqual(Parse(Chart("120", "[{\"y\":240}]")).AudioEvents[0].Start,
            Parse(Chart("120", "[{\"y\":480}]", info: ",\"resolution\":480")).AudioEvents[0].Start);
        PlaybackChart minimum = Parse(Chart("120", "[{\"y\":9223372036854775808}]", info: ",\"resolution\":-9223372036854775808"));
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), minimum.AudioEvents[0].Start.ToTimeSpan());
    }

    /// <summary>
    /// 旧版・版なし・未知版の受理を保ち、旧キーと新キー併記では新形式を優先して互換入力を誤解する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void LegacyAndVersionlessAdaptersShareModernTimingAndModernKeysWin()
    {
        PlaybackChart modern = Parse(Chart("120", "[{\"y\":240}]"));
        foreach (string version in new[] { "", "\"version\":\"0.21\"," })
        {
            PlaybackChart legacy = Parse("{" + version + "\"info\":{\"initBPM\":120,\"resolution\":480},\"soundChannel\":[{\"name\":\"a.wav\",\"notes\":[{\"y\":240}]}]}");
            Assert.AreEqual(modern.AudioEvents[0].Start, legacy.AudioEvents[0].Start);
        }
        foreach (string version in new[] { "", "\"version\":\"future\"," })
        {
            Assert.AreEqual(modern.AudioEvents[0].Start, Parse("{" + version + Chart("120", "[{\"y\":240}]")[1..].Replace("\"version\":\"1.0.0\",", "")).AudioEvents[0].Start);
        }

        PlaybackChart mixed = Parse("{\"info\":{\"init_bpm\":120,\"initBPM\":60},\"sound_channels\":[],\"soundChannel\":[{\"notes\":[{\"y\":240}]}],\"bpm_events\":[],\"bpmNotes\":[{\"y\":0,\"v\":60}]}");
        Assert.AreEqual(0, mixed.AudioEvents.Count);
        Assert.AreEqual(120d, mixed.Bpm.ToDouble());
    }

    /// <summary>
    /// 小数STOPと数値文字列STOPを同じ正確な寄与へ変換し、停止点の発音時刻や後続時刻がずれる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void FractionalStopsAndNumericStringStopsHaveExactContributions()
    {
        PlaybackChart chart = Parse(Chart("120", "[{\"y\":240},{\"y\":480}]", ",\"stop_events\":[{\"y\":240,\"duration\":0.5}]"));
        BigInteger grid = BigInteger.One << 32;
        Assert.AreEqual(5000000 * grid, chart.AudioEvents[0].Start.Subticks);
        Assert.AreEqual(10000000 * grid + RoundEven(10000000 * grid, 960), chart.AudioEvents[1].Start.Subticks);
        PlaybackChart strings = Parse(Chart("\"120\"", "[{\"y\":\"240\",\"l\":\"0\"},{\"y\":\"480\"}]",
            ",\"stop_events\":[{\"y\":\"240\",\"duration\":\"0.5\"}]", ",\"resolution\":\"240\""));
        CollectionAssert.AreEqual(chart.AudioEvents.Select(item => item.Start).ToArray(), strings.AudioEvents.Select(item => item.Start).ToArray());
    }

    /// <summary>
    /// 層付きLN頭と解放音は音声を残し、ノート数とcount時刻は重複させず、音声と計数の同一性を混同する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void LayeredLongNoteHeadsAndReleaseSoundsUseSeparateAudioAndCountIdentities()
    {
        PlaybackChart chart = Parse(LayeredPlayableInput());
        Assert.AreEqual(5, chart.AudioEvents.Count);
        Assert.AreEqual(3, chart.TotalNoteCount);
        CollectionAssert.AreEqual(new long[] { 0, 2500000, 5000000 }, chart.CountTimes.Select(time => time.ToTimeSpan().Ticks).ToArray());
        Assert.IsTrue(chart.AudioEvents.Where(item => item.Start.Subticks.IsZero).All(item => item.SourceEnd == null));
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), chart.AudioEvents.Last().Start.ToTimeSpan());
    }

    private static string LayeredPlayableInput() =>
        "{\"version\":\"1.0.0\",\"info\":{\"init_bpm\":120,\"mode_hint\":\"custom-mode\"},\"sound_channels\":["
        + "{\"name\":\"same.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":240}]},"
        + "{\"name\":\"same.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":240}]},"
        + "{\"name\":\"release.wav\",\"notes\":[{\"x\":1,\"y\":240,\"up\":true}]},"
        + "{\"name\":\"short.wav\",\"notes\":[{\"x\":2,\"y\":120}]},"
        + "{\"name\":\"short.wav\",\"notes\":[{\"x\":2,\"y\":120}]}],"
        + "\"key_channels\":[{\"notes\":[{\"y\":480}]}],\"mine_channels\":[{\"notes\":[{\"y\":480}]}],\"scroll_events\":[{\"y\":0,\"rate\":2}]}";

    /// <summary>登録順・LN終端の包含を区別する独立入力です。125 BPM/240 resolutionで1pulseは0.002秒です。</summary>
    internal static string ConflictingPlayableInput(string scenario)
    {
        string Channel(string notes) => "{\"name\":\"a.wav\",\"notes\":[" + notes + "]}";
        const string normal = "{\"x\":1,\"y\":240}";
        const string shortLn = "{\"x\":1,\"y\":240,\"l\":240}";
        const string longLn = "{\"x\":1,\"y\":240,\"l\":480}";
        string channels = scenario switch
        {
            "normal-ln" => Channel(normal) + "," + Channel(shortLn),
            "ln-normal" => Channel(shortLn) + "," + Channel(normal),
            "short-long" => Channel(shortLn) + "," + Channel(longLn),
            "long-short" => Channel(longLn) + "," + Channel(shortLn),
            "stable-channel-order" => Channel("{\"x\":1,\"y\":720},{\"x\":1,\"y\":240,\"l\":480},"
                + "{\"x\":2,\"y\":240,\"l\":240},{\"x\":2,\"y\":240,\"l\":480},"
                + "{\"x\":3,\"y\":240,\"l\":480},{\"x\":3,\"y\":240,\"l\":240}")
                + "," + Channel("{\"x\":1,\"y\":480},{\"x\":1,\"y\":600,\"l\":480},"
                + "{\"x\":1,\"y\":720,\"l\":240},{\"x\":1,\"y\":721},{\"x\":4,\"y\":480}"),
            "later-channel-earlier-head" => Channel("{\"x\":1,\"y\":480},{\"x\":2,\"y\":600,\"l\":120},{\"x\":3,\"y\":720}")
                + "," + Channel("{\"x\":1,\"y\":240,\"l\":480},{\"x\":2,\"y\":240,\"l\":480},"
                + "{\"x\":3,\"y\":240,\"l\":480},{\"x\":4,\"y\":240,\"l\":480}"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        return "{\"info\":{\"init_bpm\":125,\"resolution\":240},\"lines\":[],\"sound_channels\":[" + channels + "]}";
    }

    /// <summary>
    /// 競合ノートの正常解析だけが除外数付き警告を一回出し、正常な層や解析失敗で誤通知・重複通知する誤りを検出する。
    /// </summary>
    [TestMethod]
    [DoNotParallelize] // プロセス共通のNLog設定を一時置換して警告を観測する。
    public void ConflictingPlayableNotesWarnOnceWithRejectedCountButNormalLayersAndFailedParseDoNot()
    {
        _ = NLogWrapper.GetLogger(nameof(BmsonPlaybackParser));
        LoggingConfiguration? original = LogManager.Configuration;
        using var warnings = new MemoryTarget { Layout = "${event-properties:item=IgnoredPlayableNotes}" };
        var configuration = new LoggingConfiguration();
        configuration.AddRule(LogLevel.Warn, LogLevel.Warn, warnings);
        LogManager.Configuration = configuration;
        try
        {
            string conflicts = ConflictingPlayableInput("stable-channel-order");
            Assert.AreEqual(8, Parse(conflicts).TotalNoteCount);
            Assert.AreEqual(1, warnings.Logs.Count);
            Assert.AreEqual("6", warnings.Logs[0]);
            warnings.Logs.Clear();
            Assert.AreEqual(3, Parse(LayeredPlayableInput()).TotalNoteCount);
            Assert.AreEqual(0, warnings.Logs.Count);
            InvalidBmsonFileException failure = Assert.ThrowsException<InvalidBmsonFileException>(() =>
                Parse(conflicts.Replace("\"init_bpm\":125", "\"init_bpm\":125,\"total\":\"NaN\"", StringComparison.Ordinal)));
            Assert.IsInstanceOfType<FormatException>(failure.InnerException);
            Assert.AreEqual(0, warnings.Logs.Count, "解析を正常完了する前は競合WARNを出しません。");
        }
        finally { LogManager.Configuration = original; }
    }

    /// <summary>
    /// 計数から拒否した競合ノートも全音声slice・音源区間・表示終端を保持し、衝突処理が再生内容を失う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void RejectedPlayableNotesKeepEveryAudioSliceSourceWindowAndLogicalDisplayEnd()
    {
        string Input(bool collides) =>
            "{\"info\":{\"init_bpm\":125,\"resolution\":240},\"lines\":[],\"sound_channels\":["
            + "{\"name\":\"anchor.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":960}]},"
            + "{\"name\":\"tone.wav\",\"notes\":[{\"x\":" + (collides ? 1 : 2) + ",\"y\":240,\"c\":false},"
            + "{\"x\":" + (collides ? 1 : 3) + ",\"y\":480,\"l\":1440,\"c\":true},"
            + "{\"x\":1,\"y\":720,\"up\":true,\"c\":true},"
            + "{\"x\":" + (collides ? 1 : 4) + ",\"y\":960,\"c\":false}]},"
            + "{\"name\":\"release.wav\",\"notes\":[{\"x\":1,\"y\":1440,\"up\":true}]}]}";
        PlaybackChart collided = Parse(Input(true));
        PlaybackChart separate = Parse(Input(false));
        Assert.AreEqual(2, collided.TotalNoteCount);
        CollectionAssert.AreEqual(new long[] { 0, 19200000 }, collided.CountTimes.Select(time => time.ToTimeSpan().Ticks).ToArray());
        Assert.AreEqual(6, collided.AudioEvents.Count);
        Assert.AreEqual(TimeSpan.FromTicks(38400000), collided.Duration);
        CollectionAssert.AreEqual(separate.ResourceNames.ToArray(), collided.ResourceNames.ToArray());
        CollectionAssert.AreEqual(separate.AudioEvents.ToArray(), collided.AudioEvents.ToArray());
        CollectionAssert.AreEqual(separate.Controls.ToArray(), collided.Controls.ToArray());
        Assert.AreEqual(separate.Duration, collided.Duration);
        (long Start, long? End, long SourceStart, long? SourceEnd)[] expected =
        [
            (4800000, 9600000, 0, 4800000),
            (9600000, 14400000, 4800000, 9600000),
            (14400000, null, 9600000, null),
            (19200000, null, 0, null)
        ];
        CollectionAssert.AreEqual(expected, collided.AudioEvents.Where(item => item.ResourceIndex == 1)
            .Select(item => (item.Start.ToTimeSpan().Ticks, item.End?.ToTimeSpan().Ticks,
                item.SourceStart.ToTimeSpan().Ticks, item.SourceEnd?.ToTimeSpan().Ticks)).ToArray());
        Assert.AreEqual(TimeSpan.FromTicks(28800000), collided.AudioEvents.Single(item => item.ResourceIndex == 2).Start.ToTimeSpan());
    }

    /// <summary>
    /// 非正・非数・構文不正・表現不能の開始BPMを元原因とファイル名付きの入力失敗にし、正常時計の捏造を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("\"NaN\"")]
    [DataRow("\"120x\"")]
    [DataRow("\"0\"")]
    [DataRow("1e-1000")]
    public void InvalidOrUnrepresentableInitialBpmIsClassifiedWithOriginalInputFailure(string bpm)
    {
        InvalidBmsonFileException failure = Assert.ThrowsException<InvalidBmsonFileException>(() => Parse(Chart(bpm, "[{\"y\":1}]")));
        Assert.IsNotNull(failure.InnerException);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.FileName));
    }

    /// <summary>
    /// 巨大でも有限の十進BPMは受理し、BPM欠落・負pulse・小数LN長・null版・不正JSONは失敗として互換境界の混同を検出する。
    /// </summary>
    [TestMethod]
    public void FiniteDecimalOutsideDoubleRangeIsNotRejectedAndMissingBpmIsNotInvented()
    {
        Assert.AreEqual(1, Parse(Chart("1e1000", "[{\"y\":1}]")).AudioEvents.Count);
        Assert.ThrowsException<InvalidBmsonFileException>(() => Parse("{\"info\":{},\"sound_channels\":[]}"));
        Assert.ThrowsException<InvalidBmsonFileException>(() => Parse(Chart("120", "[{\"y\":-1}]")));
        Assert.ThrowsException<InvalidBmsonFileException>(() => Parse(Chart("120", "[{\"y\":0,\"l\":0.5}]")));
        Assert.ThrowsException<InvalidBmsonFileException>(() => Parse(Chart("120", "[]").Replace("\"1.0.0\"", "null")));
        Assert.ThrowsException<InvalidBmsonFileException>(() => Parse(Chart("NaN", "[]")));
    }

    private static string Chart(string bpm, string notes, string extra = "", string info = "") =>
        "{\"version\":\"1.0.0\",\"info\":{\"init_bpm\":" + bpm + info + "},\"sound_channels\":[{\"name\":\"a.wav\",\"notes\":" + notes + "}]" + extra + "}";

    /// <summary>
    /// 引用有無の両方で係数4096桁までは符号と小数点を除いて受理し、超過は元原因付き失敗として桁数判定の誤りを検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DecimalCoefficientLimitCountsZerosButExcludesSignAndPoint(bool quoted)
    {
        string Token(string value) => quoted ? "\"" + value + "\"" : value;
        foreach (string value in new[] { "1" + new string('0', 4095), "0." + new string('0', 4094) + "1" })
        {
            Assert.AreEqual(1, Parse(Chart(Token(value), "[{\"y\":0}]", ",\"lines\":[]")).AudioEvents.Count);
        }

        foreach (string value in new[] { "0." + new string('0', 4095), "-1" + new string('0', 4095) })
        {
            Assert.AreEqual(120d, Parse(Chart("120", "[]", ",\"bpm_events\":[{\"y\":0,\"bpm\":" + Token(value) + "}]")).Bpm.ToDouble());
        }

        foreach (string value in new[] { "1" + new string('0', 4096), "0." + new string('0', 4096), "-1" + new string('0', 4096) })
        {
            InvalidBmsonFileException failure = Assert.ThrowsException<InvalidBmsonFileException>(() =>
                Parse(Chart("120", "[]", ",\"bpm_events\":[{\"y\":0,\"bpm\":" + Token(value) + "}]")));
            Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
        }
    }

    /// <summary>
    /// 引用有無の両方で指数4096の相殺と先頭ゼロを受理し、指数超過は元原因付きで拒否して有効範囲との混同を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitExponentLimitAllowsScaleAndResolutionToCancelBeyondEffectiveRange(bool quoted)
    {
        string Token(string value) => quoted ? "\"" + value + "\"" : value;
        foreach ((string bpm, string resolution) in new[] {
            ("1e-4096", "1e4096"), ("0.1e-4096", "10e4096"),
            ("0." + new string('0', 4094) + "1e-4096", "1" + new string('0', 4095) + "e4096") })
        {
            PlaybackChart chart = Parse(Chart(Token(bpm), "[{\"y\":1}]", ",\"lines\":[]", ",\"resolution\":" + Token(resolution)));
            Assert.AreEqual(600000000L, chart.AudioEvents.Single().Start.ToTimeSpan().Ticks);
        }
        PlaybackChart leadingZeros = Parse(Chart(Token("1e" + new string('0', 5000) + "2"), "[{\"y\":240}]"));
        Assert.AreEqual(6000000L, leadingZeros.AudioEvents.Single().Start.ToTimeSpan().Ticks);
        foreach (string value in new[] { "1e4097", "1e-4097", "0e5000" })
        {
            InvalidBmsonFileException failure = Assert.ThrowsException<InvalidBmsonFileException>(() =>
                Parse(Chart("120", "[]", ",\"bpm_events\":[{\"y\":0,\"bpm\":" + Token(value) + "}]")));
            Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
        }
        Assert.AreEqual(120d, Parse(Chart("120", "[]", ",\"bpm_events\":[{\"y\":0,\"bpm\":" + Token("0e4096") + "}]")).Bpm.ToDouble());
    }

    internal static PlaybackChart Parse(string json)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsonPlaybackParserTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "test.bmson");
        try { File.WriteAllText(path, json); return PlaybackChart.Load(path); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static BigInteger RoundEven(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        int comparison = (remainder * 2).CompareTo(denominator);
        return comparison > 0 || (comparison == 0 && !quotient.IsEven) ? quotient + 1 : quotient;
    }
}
