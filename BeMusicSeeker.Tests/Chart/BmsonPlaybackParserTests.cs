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

    [TestMethod]
    public void FixedSubtickContributionsRoundTiesToEven()
    {
        PlaybackChart chart = Parse(Chart("4096", "[{\"y\":1},{\"y\":3}]", info: ",\"resolution\":1073741824"));
        Assert.AreEqual(new BigInteger(585938), chart.AudioEvents[0].Start.Subticks);
        Assert.AreEqual(new BigInteger(1757812), chart.AudioEvents[1].Start.Subticks);
    }

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

    [TestMethod]
    public void ResolutionCanBeDoubledOrAbsoluteMinimumIntegerWithoutWrapping()
    {
        Assert.AreEqual(Parse(Chart("120", "[{\"y\":240}]")).AudioEvents[0].Start,
            Parse(Chart("120", "[{\"y\":480}]", info: ",\"resolution\":480")).AudioEvents[0].Start);
        PlaybackChart minimum = Parse(Chart("120", "[{\"y\":9223372036854775808}]", info: ",\"resolution\":-9223372036854775808"));
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), minimum.AudioEvents[0].Start.ToTimeSpan());
    }

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
            Assert.AreEqual(modern.AudioEvents[0].Start, Parse("{" + version + Chart("120", "[{\"y\":240}]")[1..].Replace("\"version\":\"1.0.0\",", "")).AudioEvents[0].Start);
        PlaybackChart mixed = Parse("{\"info\":{\"init_bpm\":120,\"initBPM\":60},\"sound_channels\":[],\"soundChannel\":[{\"notes\":[{\"y\":240}]}],\"bpm_events\":[],\"bpmNotes\":[{\"y\":0,\"v\":60}]}");
        Assert.AreEqual(0, mixed.AudioEvents.Count);
        Assert.AreEqual(120d, mixed.Bpm.ToDouble());
    }

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

    [TestMethod]
    [DoNotParallelize]
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

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DecimalCoefficientLimitCountsZerosButExcludesSignAndPoint(bool quoted)
    {
        string Token(string value) => quoted ? "\"" + value + "\"" : value;
        foreach (string value in new[] { "1" + new string('0', 4095), "0." + new string('0', 4094) + "1" })
            Assert.AreEqual(1, Parse(Chart(Token(value), "[{\"y\":0}]", ",\"lines\":[]")).AudioEvents.Count);
        foreach (string value in new[] { "0." + new string('0', 4095), "-1" + new string('0', 4095) })
            Assert.AreEqual(120d, Parse(Chart("120", "[]", ",\"bpm_events\":[{\"y\":0,\"bpm\":" + Token(value) + "}]")).Bpm.ToDouble());
        foreach (string value in new[] { "1" + new string('0', 4096), "0." + new string('0', 4096), "-1" + new string('0', 4096) })
        {
            InvalidBmsonFileException failure = Assert.ThrowsException<InvalidBmsonFileException>(() =>
                Parse(Chart("120", "[]", ",\"bpm_events\":[{\"y\":0,\"bpm\":" + Token(value) + "}]")));
            Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
        }
    }

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
