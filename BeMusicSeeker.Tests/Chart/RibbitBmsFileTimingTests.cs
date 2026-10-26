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
[DoNotParallelize]
public sealed class RibbitBmsFileTimingTests
{
    /// <summary>
    /// 各小節境界で捨てた端数tickを後続へ補償せず、六小節後の終端時刻から既存時計とのずれを検出する。
    /// </summary>
    [TestMethod]
    public void MeasureBoundariesDiscardFractionalTicksInsteadOfCompensatingLater()
    {
        WithChart("#BPM 7\n#00611:01\n", bms => Assert.AreEqual(2399999994L, bms.Duration.Ticks));
    }

    /// <summary>
    /// 開始・拡張BPMが一tick境界の直上なら小節時間をゼロとし、十進精度を失って一tick増える誤りを検出する。
    /// </summary>
    [TestMethod]
    public void DecimalBpmAboveOneTickBoundaryProducesZeroTicks()
    {
        WithChart("#BPM 2400000000.00000001\n#00011:01\n", bms => Assert.AreEqual(0L, bms.Duration.Ticks));
        WithChart("#BPM 120\n#BPM01 2400000000.00000000000000000001\n#00008:01\n#00011:01\n", bms =>
            Assert.AreEqual(0L, bms.Duration.Ticks));
    }

    /// <summary>
    /// 複数STOPの端数を累積し、小節長倍率はSTOP時間へ掛けずに発音と終端へ反映して停止時計の誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// double範囲を超える有限値は正確に合成し、極小の非正相当入力では以前の正値を保って桁あふれ・過小値の誤解釈を検出する。
    /// </summary>
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

    /// <summary>
    /// 指数表記と末尾符号の受理を直接BPM・拡張BPMそれぞれの規則で保ち、チャンネル間の字句規則の混同を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("1e2", 100L, 120L)]
    [DataRow("120+", 120L, 120L)]
    [DataRow("240+", 120L, 240L)]
    [DataRow("120-", 120L, 120L)]
    public void NumericChannelsKeepTheirExistingLexicalRules(string input, long direct, long extended)
    {
        foreach (string culture in new[] { "ja-JP", "en-US", "de-DE", "fr-FR", "th-TH", "different-signs" })
        {
            WithChart("#BPM 120\n#BPM " + input + "\n#BPM01 120\n#BPM01 " + input + "\n#00008:01\n#00011:01\n", bms =>
            {
                Assert.AreEqual(new Fraction(direct), RequireFinite(bms.Bpm));
                Assert.AreEqual(new Fraction(extended), RequireFinite((BmsNumber)bms.Measures[0].ExBpm.Single().Value));
            }, culture);
        }
    }

    /// <summary>
    /// 同じen-US数値文字列を異なる地域設定で読み、桁区切り、小数、符号、整数分岐と発音・STOP・終端tickを一定に保つ。
    /// </summary>
    [DataTestMethod]
    [DataRow("ja-JP")]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    [DataRow("ar-SA")]
    [DataRow("different-signs")]
    public void NumericInputAndEventTicksAreIndependentOfCurrentCulture(string culture)
    {
        const string source = "#BPM +1,200.5\n#BPM01 +1,200.5\n#STOP01 +48.5\n#RANDOM +1\n#IF +1\n#RANK +3\n#DIFFICULTY +4\n#TOTAL +1,200.5\n#PLAYLEVEL +12.5\n#00008:01\n#00009:0001\n#00011:0001\n#ENDIF\n";
        WithChart(source, bms =>
        {
            Assert.AreEqual(new Fraction(2401, 2), RequireFinite(bms.Bpm));
            Assert.AreEqual(new Fraction(2401, 2), RequireFinite((BmsNumber)bms.Measures[0].ExBpm.Single().Value));
            Assert.AreEqual(3, bms.Rank);
            Assert.AreEqual(4, bms.Difficulty);
            Assert.AreEqual(1200.5, bms.Total);
            Assert.AreEqual(12.5, bms.Playlevel);
            Assert.AreEqual(999583L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
            Assert.AreEqual(TimeSpan.FromTicks(504997), bms.Measures[0].Stop.Single().Value);
            Assert.AreEqual(2504164L, bms.Duration.Ticks);
        }, culture);
    }

    /// <summary>
    /// 埋込ASCII空白を小節長だけで除去し、不正BPM・STOP・小節長は前値を保って指示ごとの受理規則の混同を検出する。
    /// </summary>
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

    /// <summary>
    /// 奇数桁データの末尾は各チャンネルの従来解釈を保ち、小節端BPM制御の位置と順序を失う誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// 小節端のゼロBPM制御を既に確定した時刻へ残し、過去の発音や小節終端を再計算する誤りを検出する。
    /// </summary>
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
            {
                Assert.AreEqual(20000000L, control.AbsoluteTime.Ticks);
            }
        });
    }

    /// <summary>
    /// 次小節の位置ゼロで直接・拡張BPMを正値へ戻せば発音と終端が進み、位置を進める前の復旧を拒否する誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// 次小節が非ゼロの位置進行を必要とするとゼロBPMは解析失敗となり、不正な時計で成功する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ZeroBpmStillFailsWhenTheNextMeasureNeedsNonzeroPositionAdvance()
    {
        Assert.ThrowsException<BMSFile.InvalidBmsFileException>(() =>
            WithChart("#BPM 120\n#00003:000\n#00011:01\n#00111:01\n", _ => { }));
    }

    /// <summary>
    /// 同位置の発音を制御変更前に刻み、最後の有効STOPだけを後続へ適用して制御順や未定義STOP扱いの誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// 明示無限BPMの時間係数をゼロとして未使用・使用STOPも受理し、巨大な有限値との分類混同を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("ja-JP")]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    public void ExplicitInfiniteBpmHasZeroCoefficientAndUnusedInfiniteStopIsAccepted(string culture)
    {
        string infinity = CultureInfo.GetCultureInfo("en-US").NumberFormat.PositiveInfinitySymbol;
        WithChart($"#BPM {infinity}\n#STOP01 {infinity}\n#00011:0001\n", bms =>
        {
            Assert.IsTrue(bms.Bpm is { IsPositiveInfinity: true });
            Assert.AreEqual(0L, bms.Duration.Ticks);
            Assert.AreEqual(0L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
        }, culture);
        WithChart($"#BPM {infinity}\n#STOP01 {infinity}\n#00009:01\n#00011:01\n", bms =>
        {
            Assert.AreEqual(0L, bms.Duration.Ticks);
            Assert.AreEqual(TimeSpan.Zero, bms.Measures[0].Stop.Single().Value);
            Assert.AreEqual(0L, bms.Measures[0].Note1PVis01.Single().AbsoluteTime.Ticks);
        }, culture);
    }

    /// <summary>
    /// 巨大な有限BPMでは無限STOPをゼロ、有限STOPを正確な時間にし、無限入力と有限値の分類が再生時計を変える誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// 直接・拡張のテンポ変更後には開始時の巨大有限BPMのSTOP分類を引き継がず、無限STOPを誤受理する誤りを検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("03", "78")]
    [DataRow("08", "01")]
    public void TempoChangeReplacesTheInitialInfiniteStopInputClassification(string channel, string value)
    {
        string infinity = CultureInfo.GetCultureInfo("en-US").NumberFormat.PositiveInfinitySymbol;
        Assert.ThrowsException<BMSFile.InvalidBmsFileException>(() =>
            WithChart($"#BPM 1e400\n#BPM01 120\n#STOP01 {infinity}\n#000{channel}:{value}00\n#00009:0001\n#00011:01\n", _ => { }));
    }

    /// <summary>
    /// 参照する無限小節長と有限BPMの無限STOPは入力失敗とし、無限値の不正な時間計算を成功させる誤りを検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("120", "02")]
    [DataRow("explicit-infinity", "02")]
    [DataRow("1e400", "02")]
    [DataRow("120", "09")]
    public void ReferencedInfiniteLengthAndFiniteBpmInfiniteStopPreserveParsingFailure(string bpm, string channel)
    {
        string infinity = CultureInfo.GetCultureInfo("en-US").NumberFormat.PositiveInfinitySymbol;
        if (bpm == "explicit-infinity")
        {
            bpm = infinity;
        }

        string definition = channel == "02" ? $"#00002:{infinity}\n" : $"#STOP01 {infinity}\n#00009:01\n";
        Assert.ThrowsException<BMSFile.InvalidBmsFileException>(() =>
            WithChart("#BPM " + bpm + "\n" + definition + "#00011:01\n", _ => { }));
    }

    /// <summary>
    /// 採用・不採用のRANDOM選択を記述順に一回ずつ通知して解析結果にも残し、監査履歴の重複・欠落を検出する。
    /// </summary>
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

    /// <summary>
    /// 次の乱数選択が故障したら完了した選択だけを通知し、元原因を実装故障として保持して入力不良へ読み替える誤りを検出する。
    /// </summary>
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
        {
            throw new AssertFailedException("定義された有限値が必要です。");
        }

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
            if (culture == "different-signs")
            {
                var changedSigns = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
                changedSigns.NumberFormat.PositiveSign = "plus";
                changedSigns.NumberFormat.NegativeSign = "minus";
                CultureInfo.CurrentCulture = changedSigns;
            }
            else
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            }
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
