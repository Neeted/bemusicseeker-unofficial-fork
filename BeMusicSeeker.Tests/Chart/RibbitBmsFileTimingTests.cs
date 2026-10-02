using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Math;

namespace BeMusicSeeker.Tests;

/// <summary>Ribbit解析の正確な小節内tickと既存の入力・制御解釈を検証します。</summary>
[TestClass]
public sealed class RibbitBmsFileTimingTests
{
    [TestMethod]
    public void MeasureBoundariesDiscardFractionalTicksInsteadOfCompensatingLater()
    {
        WithChart("#BPM 7\n#00611:01\n", bms => Assert.AreEqual(2399999994L, bms.Duration.Ticks));
    }

    [TestMethod]
    public void DecimalBpmAboveOneTickBoundaryProducesZeroTicks()
    {
        WithChart("#BPM 2400000000.00000001\n#00011:01\n", bms => Assert.AreEqual(0L, bms.Duration.Ticks));
        WithChart("#BPM 120\n#BPM01 2400000000.00000000000000000001\n#00008:01\n#00011:01\n", bms =>
            Assert.AreEqual(0L, bms.Duration.Ticks));
    }

    [TestMethod]
    public void StopsAccumulateFractionalTicksAndIgnoreMeasureLengthMultiplier()
    {
        WithChart("#BPM 7\n#STOP01 3\n#00009:00010100\n#00011:00000001\n", bms =>
            Assert.AreEqual(267857142L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks));
        WithChart("#BPM 120\n#00002:2\n#STOP01 48\n#00009:0001\n#00011:0001\n", bms =>
        {
            Assert.AreEqual(TimeSpan.FromTicks(5000000), bms.Measures[0].Stop.Single().Value);
            Assert.AreEqual(20000000L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
            Assert.AreEqual(45000000L, bms.Duration.Ticks);
        });
    }

    [TestMethod]
    public void FiniteOverflowTextRemainsExactAndUnderflowKeepsTheOldPositiveGuard()
    {
        WithChart("#BPM 1e400\n#00002:1e400\n#00011:0001\n", bms =>
        {
            Assert.AreEqual(2400000000L, bms.Duration.Ticks);
            Assert.AreEqual(1200000000L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
            Assert.AreEqual(new Fraction(BigInteger.Pow(10, 400), 1), RequireFinite(bms.Bpm));
        });
        WithChart("#BPM 120\n#BPM 1e-400\n#STOP01 48\n#STOP01 1e-400\n#00009:01\n#00011:01\n", bms =>
        {
            Assert.AreEqual(new Fraction(120), RequireFinite(bms.Bpm));
            Assert.AreEqual(TimeSpan.FromTicks(5000000), bms.Measures[0].Stop.Single().Value);
        });
        WithChart("#BPM 1e400\n#STOP01 1e400\n#00009:01\n#00011:01\n", bms =>
            Assert.AreEqual(TimeSpan.FromTicks(12500000), bms.Measures[0].Stop.Single().Value));
    }

    [DataTestMethod]
    [DataRow("1e2", 100L, 120L)]
    [DataRow("120+", 120L, 120L)]
    [DataRow("240+", 120L, 240L)]
    [DataRow("120-", 120L, 120L)]
    public void NumericChannelsKeepTheirExistingLexicalRules(string input, long direct, long extended)
    {
        WithChart("#BPM 120\n#BPM " + input + "\n#BPM01 120\n#BPM01 " + input + "\n#00008:01\n#00011:01\n", bms =>
        {
            Assert.AreEqual(new Fraction(direct), RequireFinite(bms.Bpm));
            Assert.AreEqual(new Fraction(extended), RequireFinite((BmsNumber)bms.Measures[0].ExBpm.Single().Value));
        });
    }

    [DataTestMethod]
    [DataRow("en-US", "1,200.5")]
    [DataRow("de-DE", "1.200,5")]
    [DataRow("fr-FR", "1 200,5")]
    public void DecimalAndGroupSeparatorsFollowTheCurrentCulture(string culture, string input)
    {
        WithChart("#BPM " + input + "\n#BPM01 " + input + "\n#00008:01\n#00011:01\n", bms =>
        {
            Assert.AreEqual(new Fraction(2401, 2), RequireFinite(bms.Bpm));
            Assert.AreEqual(new Fraction(2401, 2), RequireFinite((BmsNumber)bms.Measures[0].ExBpm.Single().Value));
            Assert.AreEqual(1999167L, bms.Duration.Ticks);
        }, culture);
    }

    [TestMethod]
    public void OnlyMeasureLengthRemovesEmbeddedAsciiSpacesAndInvalidDefinitionsKeepPreviousValues()
    {
        WithChart("#BPM 120\n#BPM 2 40\n#BPM01 120\n#BPM01 invalid\n#STOP01 48\n#STOP01 4 8\n#00002:1 0\n#00002:invalid\n#00008:01\n#00009:01\n#00011:01\n", bms =>
        {
            Assert.AreEqual(new Fraction(120), RequireFinite(bms.Bpm));
            Assert.AreEqual(new Fraction(10), RequireFinite(bms.Measures[0].Length));
            Assert.AreEqual(new Fraction(120), RequireFinite((BmsNumber)bms.Measures[0].ExBpm.Single().Value));
            Assert.AreEqual(TimeSpan.FromTicks(5000000), bms.Measures[0].Stop.Single().Value);
        });
    }

    [TestMethod]
    public void OddDataTailsKeepTheirChannelSpecificMeaning()
    {
        WithChart("#BPM 120\n#BPM01 120\n#STOP01 48\n#00011:010\n#000D1:010\n#00008:010\n#00009:010\n#00003:001\n", bms =>
        {
            Assert.AreEqual(1, bms.Measures[0].Note1PVis01.Count);
            Assert.AreEqual(1, bms.Measures[0].Note1PBom01.Count);
            Assert.AreEqual(1, bms.Measures[0].ExBpm.Count);
            Assert.AreEqual(1, bms.Measures[0].Stop.Count);
            Assert.AreEqual(new Fraction(1), bms.Measures[0].Bpm.Single().Position);
            Assert.AreEqual(1, bms.Measures[0].Bpm.Single().Value);
            CollectionAssert.AreEqual(new[] { BMSFile.Chart.Note.NoteType.BAR_LINE, BMSFile.Chart.Note.NoteType.BPM },
                bms.Measures[0].Control.Where(n => n.Position == new Fraction(1)).Select(n => n.Type).ToArray());
        });
    }

    [TestMethod]
    public void TerminalOddZeroBpmKeepsItsControlAndTheAlreadyStampedMeasureTime()
    {
        WithChart("#BPM 120\n#00003:000\n#00011:01\n", bms =>
        {
            Assert.AreEqual(20000000L, bms.Duration.Ticks);
            Assert.AreEqual(0L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
            Assert.AreEqual(new Fraction(1), bms.Measures[0].Bpm.Single().Position);
            Assert.AreEqual(0, bms.Measures[0].Bpm.Single().Value);
            CollectionAssert.AreEqual(new[] { BMSFile.Chart.Note.NoteType.BAR_LINE, BMSFile.Chart.Note.NoteType.BPM },
                bms.Measures[0].Control.Where(note => note.Position == new Fraction(1)).Select(note => note.Type).ToArray());
            foreach (BMSFile.Chart.Note control in bms.Measures[0].Control.Where(note => note.Position == new Fraction(1)))
                Assert.AreEqual(20000000L, control.AbsoluteTime.Ticks);
        });
    }

    [DataTestMethod]
    [DataRow("03", "78")]
    [DataRow("08", "01")]
    public void NextMeasureStartCanRestorePositiveBpmBeforeAnyPositionAdvance(string channel, string data)
    {
        WithChart($"#BPM 120\n#BPM01 120\n#00003:000\n#00011:01\n#001{channel}:{data}\n#00111:01\n", bms =>
        {
            BMSFile.Chart.Note restored = bms.Measures[1].Control.Single(note => note.Type == (channel == "03"
                ? BMSFile.Chart.Note.NoteType.BPM : BMSFile.Chart.Note.NoteType.EX_BPM));
            Assert.AreEqual(Fraction.Zero, restored.Position);
            Assert.AreEqual(20000000L, restored.AbsoluteTime.Ticks);
            Assert.AreEqual(20000000L, bms.Measures[1].Note1PVis01.Single().AbsoluteTime.Ticks);
            Assert.AreEqual(40000000L, bms.Measures[1].BarLine.Single().AbsoluteTime.Ticks);
            Assert.AreEqual(40000000L, bms.Duration.Ticks);
        });
    }

    [TestMethod]
    public void ZeroBpmStillFailsWhenTheNextMeasureNeedsNonzeroPositionAdvance()
    {
        Assert.ThrowsException<BMSFile.InvalidBmsFileException>(() =>
            WithChart("#BPM 120\n#00003:000\n#00011:01\n#00111:01\n", _ => { }));
    }

    [TestMethod]
    public void SamePositionControlsStampNotesBeforeChangesAndOnlyLastStopAffectsLaterTime()
    {
        const string source = "#BPM 120\n#BPM01 240\n#STOP01 48\n#STOP02 96\n#00003:0078\n#00008:0001\n#00009:0001\n#00009:0002\n#00011:00000101\n";
        WithChart(source, bms =>
        {
            CollectionAssert.AreEqual(new[] { BMSFile.Chart.Note.NoteType.BPM, BMSFile.Chart.Note.NoteType.EX_BPM,
                BMSFile.Chart.Note.NoteType.STOP, BMSFile.Chart.Note.NoteType.STOP },
                bms.Measures[0].Control.Where(n => n.Position == new Fraction(1, 2)).Select(n => n.Type).ToArray());
            Assert.AreEqual(10000000L, bms.Measures[0].Note1PVis01[0].AbsoluteTime.Ticks);
            Assert.AreEqual(17500000L, bms.Measures[0].Note1PVis01[1].AbsoluteTime.Ticks);
            Assert.AreEqual(20000000L, bms.Duration.Ticks);
        });
        WithChart(source.Replace("#STOP02 96\n", string.Empty), bms =>
        {
            Assert.AreEqual(12500000L, bms.Measures[0].Note1PVis01[1].AbsoluteTime.Ticks);
            Assert.IsNull(bms.Measures[0].Stop[1].Value);
        });
    }

    [TestMethod]
    public void ExplicitInfiniteBpmHasZeroCoefficientAndUnusedInfiniteStopIsAccepted()
    {
        string infinity = CultureInfo.GetCultureInfo("en-US").NumberFormat.PositiveInfinitySymbol;
        WithChart($"#BPM {infinity}\n#STOP01 {infinity}\n#00011:0001\n", bms =>
        {
            Assert.IsTrue(bms.Bpm is { IsPositiveInfinity: true });
            Assert.AreEqual(0L, bms.Duration.Ticks);
            Assert.AreEqual(0L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
        });
        WithChart($"#BPM {infinity}\n#STOP01 {infinity}\n#00009:01\n#00011:01\n", bms =>
        {
            Assert.AreEqual(0L, bms.Duration.Ticks);
            Assert.AreEqual(TimeSpan.Zero, bms.Measures[0].Stop.Single().Value);
            Assert.AreEqual(0L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
        });
    }

    [TestMethod]
    public void HugeFiniteBpmKeepsInfiniteStopZeroAndFiniteStopExact()
    {
        string infinity = CultureInfo.GetCultureInfo("en-US").NumberFormat.PositiveInfinitySymbol;
        WithChart($"#BPM 1e400\n#00002:1e400\n#STOP01 {infinity}\n#00009:01\n#00011:0001\n", bms =>
        {
            Assert.AreEqual(TimeSpan.Zero, bms.Measures[0].Stop.Single().Value);
            Assert.AreEqual(1200000000L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
            Assert.AreEqual(2400000000L, bms.Duration.Ticks);
            var exact = new BmsNumber(new Fraction(BigInteger.Pow(10, 400), 1));
            Assert.AreEqual(exact, bms.Bpm.GetValueOrDefault());
            Assert.AreEqual(exact.GetHashCode(), bms.Bpm.GetValueOrDefault().GetHashCode());
        });
        WithChart("#BPM 1e400\n#00002:1e400\n#STOP01 1e400\n#00009:01\n#00011:0001\n", bms =>
        {
            Assert.AreEqual(TimeSpan.FromTicks(12500000), bms.Measures[0].Stop.Single().Value);
            Assert.AreEqual(1212500000L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
            Assert.AreEqual(2412500000L, bms.Duration.Ticks);
        });
    }

    [DataTestMethod]
    [DataRow("03", "78")]
    [DataRow("08", "01")]
    public void TempoChangeReplacesTheInitialInfiniteStopInputClassification(string channel, string value)
    {
        string infinity = CultureInfo.GetCultureInfo("en-US").NumberFormat.PositiveInfinitySymbol;
        Assert.ThrowsException<BMSFile.InvalidBmsFileException>(() =>
            WithChart($"#BPM 1e400\n#BPM01 120\n#STOP01 {infinity}\n#000{channel}:{value}00\n#00009:0001\n#00011:01\n", _ => { }));
    }

    [DataTestMethod]
    [DataRow("120", "02")]
    [DataRow("explicit-infinity", "02")]
    [DataRow("1e400", "02")]
    [DataRow("120", "09")]
    public void ReferencedInfiniteLengthAndFiniteBpmInfiniteStopPreserveParsingFailure(string bpm, string channel)
    {
        string infinity = CultureInfo.GetCultureInfo("en-US").NumberFormat.PositiveInfinitySymbol;
        if (bpm == "explicit-infinity") bpm = infinity;
        string definition = channel == "02" ? $"#00002:{infinity}\n" : $"#STOP01 {infinity}\n#00009:01\n";
        Assert.ThrowsException<BMSFile.InvalidBmsFileException>(() =>
            WithChart("#BPM " + bpm + "\n" + definition + "#00011:01\n", _ => { }));
    }

    [TestMethod]
    public void AuditReportsEachUsedAndSkippedRandomChoiceOnceInOrder()
    {
        var observed = new List<(int Range, int Value, bool Used)>();
        var options = new BmsParseOptions { RandomChoice = choice => observed.Add((choice.Range, choice.Value, choice.Used)) };
        WithChart("#BPM 120\n#RANDOM 2\n#IF 2\n#RANDOM 3\n#ENDIF\n#RANDOM 4\n#00011:01\n", bms =>
        {
            (int Range, int Value, bool Used)[] expected = [(2, 1, true), (3, 2, false), (4, 3, true)];
            CollectionAssert.AreEqual(expected, observed.ToArray());
            CollectionAssert.AreEqual(expected, bms.RandomPattern.Select(choice => (choice.Range, choice.Value, choice.Used)).ToArray());
        }, options: options, choices: new Queue<int>([1, 2, 3]));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AuditKeepsOnlyCompletedRandomChoicesWhenTheNextSelectionFails(bool argumentOutOfRange)
    {
        var observed = new List<(int Range, int Value, bool Used)>();
        Exception cause = argumentOutOfRange ? new ArgumentOutOfRangeException("range") : new InvalidOperationException("random failed");
        var options = new BmsParseOptions
        {
            RandomSource = new FailOnUnspecifiedRandom(cause),
            RandomChoice = choice => observed.Add((choice.Range, choice.Value, choice.Used))
        };
        BMSFile.InvalidBmsFileException failure = Assert.ThrowsException<BMSFile.InvalidBmsFileException>(() =>
            WithChart("#BPM 120\n#RANDOM 2\n#IF 2\n#RANDOM 3\n#ENDIF\n#RANDOM 4\n#00011:01\n", _ => { },
                options: options, choices: new Queue<int>([1, 2])));
        Assert.AreSame(cause, failure.InnerException);
        Assert.IsFalse(failure.IsInputFailure, "通常範囲の乱数実装故障を譜面入力不良に変換しません。");
        CollectionAssert.AreEqual(new[] { (2, 1, true), (3, 2, false) }, observed.ToArray());
    }

    private sealed class FailOnUnspecifiedRandom(Exception failure) : Random
    {
        public override int Next(int minValue, int maxValue) => throw failure;
    }

    private static Fraction RequireFinite(BmsNumber? value)
    {
        if (value is not BmsNumber number || number.FiniteValue is not Fraction finite)
            throw new AssertFailedException("定義された有限値が必要です。");
        return finite;
    }

    private static void WithChart(string source, Action<BMSFile> inspect, string culture = "en-US",
        BmsParseOptions? options = null, Queue<int>? choices = null)
    {
        CultureInfo savedCulture = CultureInfo.CurrentCulture;
        string directory = Path.Combine(Path.GetTempPath(), "ribbit-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string path = Path.Combine(directory, "test.bms");
            File.WriteAllText(path, source, new UTF8Encoding(false));
            inspect(options == null ? new BMSFile(path) : BMSFile.ParseForAudit(path, options, choices));
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
            Directory.Delete(directory, true);
        }
    }
}
