using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
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
    /// AA/aaの両定義は明示62と従来推測のどちらでも別音源として発音し、16/36宣言で強制的に同一視する誤りも検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("#BASE 62\n")]
    [DataRow("#BASE 16\n")]
    [DataRow("#BASE 36\n")]
    [DataRow("")]
    public void UpperAndLowerResourceDefinitionsKeepDistinctAudioEvents(string declaration)
    {
        WithChart(declaration + "#BPM 120\n#WAVAA upper.wav\n#WAVaa lower.wav\n#00111:AAaa\n", bms =>
            AssertScheduledAudio(bms, ("upper.wav", 96000L), ("lower.wav", 144000L)));
    }

    /// <summary>
    /// 明示62ではLNOBJと大小文字だけ異なる通常音を2秒に残し、3秒の終端自身は鳴らさない。
    /// 両定義と通常音だけの定義を両方向で通し、定義件数に依存した同一視と通常音の誤除去を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("#WAVAA upper.wav\n#WAVaa lower.wav\n", "AA", "aa", "upper.wav")]
    [DataRow("#WAVAA upper.wav\n#WAVaa lower.wav\n", "aa", "AA", "lower.wav")]
    [DataRow("#WAVAA upper.wav\n", "AA", "aa", "upper.wav")]
    [DataRow("#WAVaa lower.wav\n", "aa", "AA", "lower.wav")]
    public void ExplicitBase62KeepsNormalAudioAndSilencesOppositeCaseLnobj(
        string definitions, string normalId, string endId, string expectedResource)
    {
        WithChart("#BASE 62\n#BPM 120\n" + definitions + $"#LNOBJ {endId}\n#00111:{normalId}{endId}\n", bms =>
            AssertScheduledAudio(bms, (expectedResource, 96000L)));
    }

    /// <summary>
    /// 宣言なしのAA/aaとGG/ggは単一定義へ従来どおり同一視し、拡張IDの+/も受理して、明示62対応の互換性退行を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("AA", "AAaa", true)] // 16進推測でも大小文字の両ノートを残します。
    [DataRow("GG", "GGgg", true)] // 36進推測でも大小文字の両ノートを残します。
    [DataRow("+/", "+/", false)] // 既存の拡張IDを標準62文字だけへ制限しません。
    public void MissingBaseDeclarationKeepsLegacyCaseInferenceAndExtendedIds(string definedId, string notes, bool hasSecondNote)
    {
        (string Resource, long StartFrame)[] expected = hasSecondNote
            ? [("legacy.wav", 96000L), ("legacy.wav", 144000L)]
            : [("legacy.wav", 96000L)];
        WithChart($"#BPM 120\n#WAV{definedId} legacy.wav\n#00111:{notes}\n", bms => AssertScheduledAudio(bms, expected));
    }

    /// <summary>
    /// 最後の有効BASEを宣言前の定義・LNOBJ・ノートにも適用し、62なら通常音だけ一回、従来推測ならLNOBJと同一視して無音にする。
    /// 不正宣言での上書き、16/36への復帰漏れと、整数書式の過剰な拒否を検出する。
    /// </summary>
    [DataTestMethod]
    // 62の強制は後続16/36で解除し、逆順なら宣言前の通常音を残します。
    [DataRow("#BASE 62\n", "#BASE 36\n", false)]
    [DataRow("#BASE 62\n", "#BASE 16\n", false)]
    [DataRow("#BASE 36\n", "#BASE 62\n", true)]
    [DataRow("#BASE 16\n", "#BASE 62\n", true)]
    // 不正文字列、未対応値、整数overflow、小数形式は最後の有効宣言を変更しません。
    [DataRow("#BASE 62\n", "#BASE bogus\n#BASE 64\n#BASE 2147483648\n", true)]
    [DataRow("", "#BASE bogus\n#BASE 64\n#BASE 2147483648\n#BASE 62.0\n", false)]
    // 既存の整数指示と同じ符号・先頭ゼロ・前後空白を受理します。
    [DataRow("", "#BASE +62\n", true)]
    [DataRow("", "#BASE 062\n", true)]
    [DataRow("", "#BASE   62  \n", true)]
    public void LastValidBaseDeclarationAppliesToEarlierNotesAndLnobj(string before, string after, bool keepsNormalAudio)
    {
        string source = "#BPM 120\n" + before + "#WAVAA upper.wav\n#LNOBJ aa\n#00111:AAaa\n" + after;
        (string Resource, long StartFrame)[] expected = keepsNormalAudio ? [("upper.wav", 96000L)] : [];
        WithChart(source, bms => AssertScheduledAudio(bms, expected));
    }

    /// <summary>
    /// 通常読込みと監査準備のBOM有無で共通部分と採用枝の有効BASEを全体へ適用し、非採用末尾宣言による上書きを検出する。
    /// 採用62は宣言前の通常音を残し、採用36は同一視へ戻す。採用64は先頭62を保持し、先頭指示の欠落を識別する。
    /// 全条件でENDIF後の8秒の共通音を一回残す。
    /// </summary>
    [DataTestMethod]
    [DataRow(36, 62, 36, true, false, false)]
    [DataRow(36, 62, 36, true, true, false)]
    [DataRow(36, 62, 36, true, false, true)]
    [DataRow(36, 62, 36, true, true, true)]
    [DataRow(62, 36, 62, false, false, false)]
    [DataRow(62, 36, 62, false, true, false)]
    [DataRow(62, 36, 62, false, false, true)]
    [DataRow(62, 36, 62, false, true, true)]
    // 後続の採用宣言を不正値とし、先頭62の欠落で2秒のupperが消える条件を両入口・BOM有無で確認します。
    [DataRow(62, 64, 36, true, false, false)]
    [DataRow(62, 64, 36, true, true, false)]
    [DataRow(62, 64, 36, true, false, true)]
    [DataRow(62, 64, 36, true, true, true)]
    public void SelectedBranchBaseControlsNormalAndPreparedInputWithOrWithoutBom(
        int commonBase, int selectedBase, int skippedBase, bool keepsNormalAudio, bool includeBom, bool preparedInput)
    {
        string source = $"#BASE {commonBase}\n#BPM 120\n#WAVAA upper.wav\n#WAV01 common.wav\n#LNOBJ aa\n#00111:AAaa\n"
            + $"#RANDOM 1\n#IF 1\n#BASE {selectedBase}\n#ELSE\n#BASE {skippedBase}\n#ENDIF\n#00401:01\n";
        var encoding = new UTF8Encoding(includeBom);
        byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        string directory = Path.Combine(Path.GetTempPath(), "ribbit-base-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "input.bms");
            File.WriteAllBytes(path, bytes);
            BMSFile bms;
            if (preparedInput)
            {
                string decoded = BMSFile.DecodeForAudit(bytes, out Encoding detectedEncoding);
                bms = BMSFile.ParsePreparedForAudit(path, decoded, detectedEncoding,
                    Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(), new BmsParseOptions());
            }
            else
            {
                bms = PlaybackChart.Load(path).Bms ?? throw new AssertFailedException("BMS解析結果が必要です。");
            }
            (string Resource, long StartFrame)[] expected = keepsNormalAudio
                ? [("upper.wav", 96000L), ("common.wav", 384000L)]
                : [("common.wav", 384000L)];
            AssertScheduledAudio(bms, expected);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>実在する親階層のBMPと譜面側同名画像を置き、slash・backslashの親参照がBmpArrayでも意図した参照先を保つ。</summary>
    [DataTestMethod]
    [DataRow("../../images/art.bmp")]
    [DataRow("..\\..\\images\\art.bmp")]
    public void BmpParentResourceReferenceKeepsTheIntendedHierarchy(string reference)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ribbit-parent-image-" + Guid.NewGuid().ToString("N"));
        string chartDirectory = Path.Combine(directory, "a", "b", "c");
        string expectedImage = Path.Combine(directory, "a", "images", "art.bmp");
        Directory.CreateDirectory(chartDirectory);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "a", "images"));
            Directory.CreateDirectory(Path.Combine(chartDirectory, "images"));
            File.WriteAllBytes(expectedImage, [11, 22]);
            File.WriteAllBytes(Path.Combine(chartDirectory, "images", "art.bmp"), [33, 44]);
            string path = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(path, "#BPM 120\n#BMP01 " + reference + "\n#00004:01\n", new UTF8Encoding(false));
            var bms = new BMSFile(path);
            string resolved = Path.GetFullPath(Path.Combine(chartDirectory, bms.BmpArray[1]));
            Assert.AreEqual(expectedImage, resolved);
            CollectionAssert.AreEqual(new byte[] { 11, 22 }, File.ReadAllBytes(resolved));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 先頭のBPM・TITLE・WAVをUTF-8 BOM有無で通常読込みと監査準備へ渡し、指示の欠落と001小節の発音時刻の変化を検出する。
    /// 通常読込みのhashは元bytesから独立に求め、BOM有無を同じ本文のhashへ置き換える誤りも検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("BPM", false)]
    [DataRow("BPM", true)]
    [DataRow("TITLE", false)]
    [DataRow("TITLE", true)]
    [DataRow("WAV", false)]
    [DataRow("WAV", true)]
    public void Utf8BomKeepsFirstDirectiveAndPlaybackTimeWhileHashUsesOriginalBytes(string firstDirective, bool includeBom)
    {
        string source = firstDirective switch
        {
            "BPM" => "#BPM 120\n#TITLE bom-title\n#WAV01 audio.wav\n#00101:01\n",
            "TITLE" => "#TITLE bom-title\n#BPM 120\n#WAV01 audio.wav\n#00101:01\n",
            "WAV" => "#WAV01 audio.wav\n#BPM 120\n#TITLE bom-title\n#00101:01\n",
            _ => throw new ArgumentOutOfRangeException(nameof(firstDirective))
        };
        var encoding = new UTF8Encoding(includeBom);
        byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        string actualHash = AssertDecodedPlaybackInputs(bytes, source, "bom-title", "audio.wav");
        byte[] oppositeBomBytes = includeBom ? Encoding.UTF8.GetBytes(source) : Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
        Assert.AreNotEqual(Convert.ToHexString(MD5.HashData(oppositeBomBytes)).ToLowerInvariant(), actualHash);
    }

    /// <summary>
    /// BOMなしCP932の日本語資源名を両復号経路でそのまま採用し、UTF-8優先化による既存入力の文字化けを検出する。
    /// </summary>
    [TestMethod]
    public void BomlessCp932KeepsJapaneseResourceNamesInNormalAndPreparedInput()
    {
        const string source = "#BPM 120\n#TITLE cp932-title\n#WAV01 日本語.wav\n#00101:01\n";
        AssertDecodedPlaybackInputs(Encoding.GetEncoding(932).GetBytes(source), source, "cp932-title", "日本語.wav", 932);
    }

    /// <summary>
    /// 既存判別でUTF-8となる本文のU+FEFFはBOM有無によらず曲名へ残し、先頭以外まで削除する過剰な補正を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Utf8BomRemovalPreservesByteOrderMarkInsideTheBody(bool includeBom)
    {
        // U+FEFF直後の空白により、BOMなしでも別の文字コードとして読める曖昧な入力を避けます。
        const string source = "#BPM 120\n#TITLE before\uFEFF after\n#WAV01 audio.wav\n#00101:01\n";
        var encoding = new UTF8Encoding(includeBom);
        AssertDecodedPlaybackInputs(encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray(), source,
            "before\uFEFF after", "audio.wav", 65001);
    }

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
    /// IF・二つのELSEIF・ELSEを固定選択し、採用枝の定義とイベントだけを一回反映して後続枝の混入・上書きを検出する。
    /// 未選択枝も含む全資源pathの収集と、選択監査の一回通知は選択結果と別に確認する。
    /// </summary>
    [DataTestMethod]
    [DataRow(1, "if", 0)]
    [DataRow(2, "elseif1", 1)]
    [DataRow(3, "elseif2", 2)]
    [DataRow(4, "else", 3)]
    public void ConditionalBranchesApplyOnlyTheSelectedDefinitionsAndEvents(int choice, string branch, int measure)
    {
        var observed = new List<(int Range, int Value, bool Used)>();
        var options = new BmsParseOptions { RandomChoice = value => observed.Add((value.Range, value.Value, value.Used)) };
        WithChart(ConditionalBranchesSource(), bms =>
        {
            AssertSelectedBranch(bms, branch, measure);
            AssertCollectedBranchResources(bms, ["if", "elseif1", "elseif2", "else"]);
            (int Range, int Value, bool Used)[] expected = [(4, choice, true)];
            CollectionAssert.AreEqual(expected, observed.ToArray());
            CollectionAssert.AreEqual(expected, bms.RandomPattern.Select(value => (value.Range, value.Value, value.Used)).ToArray());
        }, options: options, choices: new Queue<int>([choice]));
    }

    /// <summary>
    /// 親採用時は内ELSEIFだけ、親不採用時は内側全体を飛ばして外ELSEだけを採用し、内外の停止命令の混同を検出する。
    /// 内側を飛ばす場合も全資源pathと未使用乱数を収集し、後続乱数まで記述順で一回記録する。
    /// </summary>
    [DataTestMethod]
    [DataRow(1, 2, "inner-elseif", 1, true)]
    [DataRow(2, 3, "outer-else", 3, false)]
    public void NestedConditionalBranchesKeepSelectionResourcesAndAuditAtTheirOwnDepth(
        int outerChoice, int innerChoice, string branch, int measure, bool innerUsed)
    {
        string source = "#BPM 120\n#WAV02 common.wav\n#RANDOM 2\n#IF 1\n#RANDOM 3\n#IF 1\n"
            + BranchSource("inner-if", 0) + "#ELSEIF 2\n" + BranchSource("inner-elseif", 1)
            + "#ELSE\n" + BranchSource("inner-else", 2) + "#ENDIF\n#ELSE\n"
            + BranchSource("outer-else", 3) + "#ENDIF\n#RANDOM 4\n#00401:02\n";
        var observed = new List<(int Range, int Value, bool Used)>();
        var options = new BmsParseOptions { RandomChoice = value => observed.Add((value.Range, value.Value, value.Used)) };
        WithChart(source, bms =>
        {
            AssertSelectedBranch(bms, branch, measure);
            AssertCollectedBranchResources(bms, ["inner-if", "inner-elseif", "inner-else", "outer-else"]);
            (int Range, int Value, bool Used)[] expected = [(2, outerChoice, true), (3, innerChoice, innerUsed), (4, 3, true)];
            CollectionAssert.AreEqual(expected, observed.ToArray());
            CollectionAssert.AreEqual(expected, bms.RandomPattern.Select(value => (value.Range, value.Value, value.Used)).ToArray());
        }, options: options, choices: new Queue<int>([outerChoice, innerChoice, 3]));
    }

    /// <summary>
    /// ELSEIF採用結果を再生境界へ投影し、曲名・音源名・二つの発音時刻に未選択枝や重複が戻る接続の誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PlaybackProjectionUsesOnlySelectedBranchMetadataResourcesAndEventTimes()
    {
        WithChart(ConditionalBranchesSource(), bms =>
        {
            var chart = PlaybackChart.FromBms(bms);
            Assert.AreEqual("elseif1-title", chart.Title);
            CollectionAssert.AreEqual(new[] { (1, "elseif1.wav"), (2, "common.wav") },
                chart.ResourceNames.Select((path, index) => (index, path)).Where(value => !string.IsNullOrEmpty(value.path)).ToArray());
            CollectionAssert.AreEquivalent(new[] { (1, 20000000L), (2, 80000000L) },
                chart.AudioEvents.Select(value => (value.ResourceIndex, value.Start.ToTimeSpan().Ticks)).ToArray());
        }, options: new BmsParseOptions(), choices: new Queue<int>([2]));
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

    private static void AssertScheduledAudio(BMSFile bms, params (string Resource, long StartFrame)[] expected)
    {
        CollectionAssert.AreEquivalent(expected, BmsAudioFrameSchedule.Create(bms, 48000).Events
            .Select(audioEvent => (bms.WavArray[audioEvent.WavIndex], audioEvent.StartFrame)).ToArray());
    }

    private static string AssertDecodedPlaybackInputs(byte[] bytes, string expectedSource, string expectedTitle,
        string expectedResource, int? expectedCodePage = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ribbit-decoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "input.bms");
            File.WriteAllBytes(path, bytes);
            string expectedHash = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            var loaded = PlaybackChart.Load(path);
            Assert.AreEqual(expectedHash, loaded.Hash);
            AssertDecodedPlaybackChart(loaded, expectedTitle, expectedResource);

            string decoded = BMSFile.DecodeForAudit(bytes, out Encoding encoding);
            Assert.AreEqual(expectedSource, decoded);
            if (expectedCodePage.HasValue)
            {
                Assert.AreEqual(expectedCodePage.Value, encoding.CodePage);
                Assert.AreEqual(expectedCodePage.Value, loaded.Bms?.Encode.CodePage);
            }
            var prepared = BMSFile.ParsePreparedForAudit(path, decoded, encoding, expectedHash, new BmsParseOptions());
            // 準備入口は呼出側で計算したhashを受け渡す契約だけを確認し、生産hashの保証には用いません。
            Assert.AreEqual(expectedHash, prepared.Md5);
            AssertDecodedPlaybackChart(PlaybackChart.FromBms(prepared), expectedTitle, expectedResource);
            return loaded.Hash;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void AssertDecodedPlaybackChart(PlaybackChart chart, string expectedTitle, string expectedResource)
    {
        Assert.AreEqual(new Fraction(120), chart.Bpm);
        Assert.AreEqual(expectedTitle, chart.Title);
        CollectionAssert.AreEqual(new[] { (1, expectedResource) }, chart.ResourceNames.Select((path, index) => (index, path))
            .Where(value => !string.IsNullOrEmpty(value.path)).ToArray());
        CollectionAssert.AreEqual(new[] { (1, 20000000L) },
            chart.AudioEvents.Select(value => (value.ResourceIndex, value.Start.ToTimeSpan().Ticks)).ToArray());
    }

    private static string ConditionalBranchesSource() =>
        "#BPM 120\n#WAV02 common.wav\n#RANDOM 4\n#IF 1\n" + BranchSource("if", 0)
        + "#ELSEIF 2\n" + BranchSource("elseif1", 1) + "#eLsEiF 3\n" + BranchSource("elseif2", 2)
        + "#ELSE\n" + BranchSource("else", 3) + "#ENDIF\n#00401:02\n";

    private static string BranchSource(string branch, int measure) =>
        $"#TITLE {branch}-title\n#ARTIST {branch}-artist\n#GENRE {branch}-genre\n"
        + $"#WAV01 {branch}.wav\n#BMP01 {branch}.bmp\n#STAGEFILE {branch}-stage.bmp\n"
        + $"#BANNER {branch}-banner.bmp\n#BACKBMP {branch}-back.bmp\n#{measure:000}01:01\n#{measure:000}04:01\n";

    private static void AssertSelectedBranch(BMSFile bms, string branch, int measure)
    {
        Assert.AreEqual(branch + "-title", bms.Title);
        Assert.AreEqual(branch + "-artist", bms.Artist);
        Assert.AreEqual(branch + "-genre", bms.Genre);
        Assert.AreEqual(branch + "-stage.bmp", bms.Stagefile);
        Assert.AreEqual(branch + "-banner.bmp", bms.Banner);
        Assert.AreEqual(branch + "-back.bmp", bms.Backbmp);
        CollectionAssert.AreEqual(new[] { (1, branch + ".wav"), (2, "common.wav") },
            bms.WavArray.Select((path, index) => (index, path)).Where(value => !string.IsNullOrEmpty(value.path)).ToArray());
        CollectionAssert.AreEqual(new[] { (1, branch + ".bmp") },
            bms.BmpArray.Select((path, index) => (index, path)).Where(value => !string.IsNullOrEmpty(value.path)).ToArray());
        CollectionAssert.AreEquivalent(new[] { (measure, Fraction.Zero, 1), (4, Fraction.Zero, 2) },
            bms.Measures.BgmNotes.Select(note => (note.Measure.Index, note.Position, note.Index)).ToArray());
        CollectionAssert.AreEqual(new[] { (measure, Fraction.Zero, 1) },
            bms.Measures.BgaBaseNotes.Select(note => (note.Measure.Index, note.Position, note.Index)).ToArray());
    }

    private static void AssertCollectedBranchResources(BMSFile bms, string[] branches)
    {
        string[] expected = branches.SelectMany(branch => new[] { branch + ".wav", branch + ".bmp",
            branch + "-stage.bmp", branch + "-banner.bmp", branch + "-back.bmp" }).Append("common.wav").ToArray();
        CollectionAssert.AreEquivalent(expected, bms.Resources.FilePaths.ToArray());
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
