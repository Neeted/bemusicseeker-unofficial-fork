using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 同等性の判定が返す診断（同等でない理由、比較打ち切り、測定値）を確認します。判定そのものの境界は、このクラスの <c>Judge_*</c> の既存のケースが担い、
/// ここでは「同等かどうか」を変えずに理由と測定値だけを確かめます。理由は判定が不成立を確定する順の一つだけで、複数が成り立つときは順位が早いほうです。
/// 測定値の期待値は、単純な波形では解析値（振幅比 0.5 なら −6.0206dB、相関 1）を直接書き、フィルタを通る量は、テスト側で独立に実装した
/// 4次バターワース（Q は0.5412・1.3066の双2次2段）の参照計算から求めます。測定値の照合は絶対誤差1e-3（dB・比・相関）です。
/// </summary>
public sealed partial class DuplicateMergeAudioEquivalenceTests
{
    private const double Tolerance = 1e-3;

    private static DuplicateMergeAudioJudgement Diagnose(float[] wav, float[] ogg) => Diagnose(Rate, wav, Rate, ogg);

    private static DuplicateMergeAudioJudgement Diagnose(int wavRate, float[] wav, int oggRate, float[] ogg)
        => DuplicateMergeAudioEquivalence.Judge(new(wavRate, 1, wav), new(oggRate, 1, ogg));

    private static DuplicateMergeAudioJudgement DiagnoseStereo(float[] wavLeft, float[] wavRight, float[] oggLeft, float[] oggRight)
        => DuplicateMergeAudioEquivalence.Judge(new(Rate, 2, Interleave(wavLeft, wavRight)), new(Rate, 2, Interleave(oggLeft, oggRight)));

    private static float[] Scaled(float[] samples, double gain) => [.. samples.Select(sample => (float)(sample * gain))];

    private static float[] HighAt(int frames, double hertz, double amplitude) => Wave(frames, n => amplitude * Sine(n, hertz));

    private static void AssertReason(DuplicateMergeAudioPairReason expected, DuplicateMergeAudioJudgement actual, string label)
    {
        Assert.AreEqual(expected, actual.Reason, label);
        Assert.AreEqual(expected == DuplicateMergeAudioPairReason.None, actual.Equivalent, label + "：同等かどうかは理由なしと同じです。");
    }

    private static void AssertNotCompared(DuplicateMergeAudioJudgement actual, string label)
        => Assert.IsNull(actual.Comparison, label + "：比較の測定値は未測定です。");

    // ---- 参照計算（テスト側の独立実装） ----

    private sealed record ExpectedComparison(
        double Correlation, double VolumeDb, double? LowVolumeDb, double ErrorRatio1k, double ErrorRatio4k, double? SegmentMinDb);

    private static double[] LowPass(double[] input, int rate, double cutoff)
    {
        double[] output = [.. input];
        foreach (double q in new[] { 0.54119610, 1.30656296 })
        {
            double omega = 2 * Math.PI * cutoff / rate, cosine = Math.Cos(omega), alpha = Math.Sin(omega) / (2 * q), a0 = 1 + alpha;
            double b0 = (1 - cosine) / 2 / a0, b1 = (1 - cosine) / a0, a1 = -2 * cosine / a0, a2 = (1 - alpha) / a0;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int index = 0; index < output.Length; index++)
            {
                double x = output[index];
                double y = b0 * x + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                output[index] = y;
            }
        }
        return output;
    }

    private static double[] ChannelOf(DuplicateMergeAudioSamples samples, int channel)
        => [.. Enumerable.Range(0, (int)samples.FrameCount).Select(frame => (double)samples.Samples[frame * samples.Channels + channel])];

    private static double DecibelsOf(double numerator, double denominator)
        => numerator == 0 ? double.NegativeInfinity : denominator == 0 ? double.PositiveInfinity : 10 * Math.Log10(numerator / denominator);

    /// <summary>仕様の定義（区間・個別に見るチャンネル・E・P）から、同じサンプルレート・同じフレーム数の組の比較の測定値を求めます。</summary>
    private static ExpectedComparison Expected(DuplicateMergeAudioSamples wav, DuplicateMergeAudioSamples ogg)
    {
        int channels = wav.Channels, frames = (int)wav.FrameCount, rate = wav.SampleRate, segmentCount = (frames + Segment - 1) / Segment;
        double product = 0, wavTotal = 0, oggTotal = 0;
        double[][] ew = new double[channels][], eo = new double[channels][];
        double[][] ew4 = new double[channels][], eo4 = new double[channels][], err4 = new double[channels][];
        double[][] ew1 = new double[channels][], eo1 = new double[channels][], err1 = new double[channels][];
        double[] fullMax = new double[segmentCount], wideMax = new double[segmentCount], narrowMax = new double[segmentCount];
        double[] wavChannel = new double[channels], oggChannel = new double[channels], wavWide = new double[channels], oggWide = new double[channels];
        for (int channel = 0; channel < channels; channel++)
        {
            double[] w = ChannelOf(wav, channel), o = ChannelOf(ogg, channel);
            double[] w4 = LowPass(w, rate, 4000), o4 = LowPass(o, rate, 4000), w1 = LowPass(w, rate, 1000), o1 = LowPass(o, rate, 1000);
            ew[channel] = new double[segmentCount]; eo[channel] = new double[segmentCount];
            ew4[channel] = new double[segmentCount]; eo4[channel] = new double[segmentCount]; err4[channel] = new double[segmentCount];
            ew1[channel] = new double[segmentCount]; eo1[channel] = new double[segmentCount]; err1[channel] = new double[segmentCount];
            for (int frame = 0; frame < frames; frame++)
            {
                int s = frame / Segment;
                product += w[frame] * o[frame];
                ew[channel][s] += w[frame] * w[frame]; eo[channel][s] += o[frame] * o[frame];
                ew4[channel][s] += w4[frame] * w4[frame]; eo4[channel][s] += o4[frame] * o4[frame];
                err4[channel][s] += (o4[frame] - w4[frame]) * (o4[frame] - w4[frame]);
                ew1[channel][s] += w1[frame] * w1[frame]; eo1[channel][s] += o1[frame] * o1[frame];
                err1[channel][s] += (o1[frame] - w1[frame]) * (o1[frame] - w1[frame]);
            }
            for (int s = 0; s < segmentCount; s++)
            {
                wavChannel[channel] += ew[channel][s]; oggChannel[channel] += eo[channel][s];
                wavWide[channel] += ew4[channel][s]; oggWide[channel] += eo4[channel][s];
                fullMax[s] = Math.Max(fullMax[s], Math.Max(ew[channel][s], eo[channel][s]));
                wideMax[s] = Math.Max(wideMax[s], Math.Max(ew4[channel][s], eo4[channel][s]));
                narrowMax[s] = Math.Max(narrowMax[s], Math.Max(ew1[channel][s], eo1[channel][s]));
            }
            wavTotal += wavChannel[channel];
            oggTotal += oggChannel[channel];
        }
        double largest = Enumerable.Range(0, channels).Max(c => Math.Max(wavChannel[c], oggChannel[c]));
        double peak = fullMax.Max();
        double ratio1 = 0, ratio4 = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            for (int s = 0; s < segmentCount; s++)
            {
                double limit1 = 0.05 * narrowMax[s] + 0.01 * fullMax[s] + 0.001 * peak;
                double limit4 = 0.2 * wideMax[s] + 0.005 * peak;
                ratio1 = Math.Max(ratio1, err1[channel][s] / limit1);
                ratio4 = Math.Max(ratio4, err4[channel][s] / limit4);
            }
        }
        double volume = 0; double? lowVolume = null, segmentMin = null;
        for (int channel = 0; channel < channels; channel++)
        {
            if (Math.Max(wavChannel[channel], oggChannel[channel]) * 30 < largest) { continue; }
            double db = DecibelsOf(oggChannel[channel], wavChannel[channel]);
            if (Math.Abs(db) > Math.Abs(volume)) { volume = db; }
            if (wavWide[channel] * 1000 >= largest)
            {
                double lowDb = DecibelsOf(oggWide[channel], wavWide[channel]);
                if (lowVolume is not { } current || Math.Abs(lowDb) > Math.Abs(current)) { lowVolume = lowDb; }
            }
            for (int s = 0; s < segmentCount; s++)
            {
                double larger = Math.Max(ew[channel][s], eo[channel][s]);
                if (larger * 100 < peak) { continue; }
                double segmentDb = DecibelsOf(Math.Min(ew[channel][s], eo[channel][s]), larger);
                if (segmentMin is not { } minimum || segmentDb < minimum) { segmentMin = segmentDb; }
            }
        }
        return new ExpectedComparison(product / Math.Sqrt(wavTotal * oggTotal), volume, lowVolume, ratio1, ratio4, segmentMin);
    }

    private static void AssertMeasured(ExpectedComparison expected, DuplicateMergeAudioJudgement actual, string label)
    {
        Assert.IsNotNull(actual.Comparison, label + "：比較の測定値があります。");
        DuplicateMergeAudioComparison measured = actual.Comparison.Value;
        Assert.AreEqual(expected.Correlation, measured.Correlation, Tolerance, label + "：相関");
        AssertDecibels(expected.VolumeDb, measured.VolumeDb, label + "：音量比");
        if (expected.LowVolumeDb is { } lowVolume)
        {
            Assert.IsNotNull(measured.LowVolumeDb, label + "：4000Hz以下の音量比があります。");
            AssertDecibels(lowVolume, measured.LowVolumeDb.Value, label + "：4000Hz以下の音量比");
        }
        else
        {
            Assert.IsNull(measured.LowVolumeDb, label + "：4000Hz以下の音量比は対象のチャンネルがなく未測定です。");
        }
        Assert.AreEqual(expected.ErrorRatio1k, measured.LowErrorRatio1k, Tolerance, label + "：1000Hz以下の誤差比");
        Assert.AreEqual(expected.ErrorRatio4k, measured.LowErrorRatio4k, Tolerance, label + "：4000Hz以下の誤差比");
        if (expected.SegmentMinDb is { } segmentMin)
        {
            Assert.IsNotNull(measured.SegmentMinDb, label + "：区間の最小比があります。");
            AssertDecibels(segmentMin, measured.SegmentMinDb.Value, label + "：区間の最小比");
        }
        else
        {
            Assert.IsNull(measured.SegmentMinDb, label + "：判定が見る区間がなく未測定です。");
        }
    }

    private static void AssertDecibels(double expected, double actual, string label)
    {
        if (double.IsInfinity(expected)) { Assert.AreEqual(expected, actual, label); }
        else { Assert.AreEqual(expected, actual, Tolerance, label); }
    }

    /// <summary>
    /// 保証：チャンネル数が異なる組は、有限でない値を重ねても理由が「チャンネル数違い」で、3チャンネル以上は有限でない値を重ねても「3チャンネル以上」である。
    /// 対象外のサンプルレートの組（44100/48000、48000/22050、96000/48000）は「対象外のサンプルレート」。いずれも比較は未測定で、フレーム差は同じレートのときだけ（OGG−WAV）。
    /// 検出する誤り：早期の棄却に有限でない値の走査を足して理由が「有限でない値」へ変わる誤り、理由の取違え、比較の測定値の混入、レート違いでのフレーム差の算出。
    /// </summary>
    [TestMethod]
    public void Judge_CheapRejections_ReportTheirOwnReasonBeforeAnyFiniteCheck()
    {
        double[] wave = [100, -50, 80, 20, -90, 10, 60, -30];
        float[] poisoned = [.. Make(Rate, 1, wave).Samples];
        poisoned[3] = float.NaN;

        DuplicateMergeAudioJudgement channels = DuplicateMergeAudioEquivalence.Judge(new(Rate, 1, poisoned), Make(Rate, 2, wave));
        AssertReason(DuplicateMergeAudioPairReason.ChannelCountMismatch, channels, "モノラルとステレオ（WAVに非数）");
        AssertNotCompared(channels, "チャンネル数違い");
        Assert.AreEqual(-4L, channels.FrameDifference, "OGGは4フレーム、WAVは8フレームです。");
        Assert.IsNull(channels.ExcessSilent);

        float[] three = Enumerable.Repeat(0.25f, 12).ToArray();
        three[4] = float.NaN;
        DuplicateMergeAudioJudgement multi = DuplicateMergeAudioEquivalence.Judge(new(Rate, 3, three), new(Rate, 3, [.. three]));
        AssertReason(DuplicateMergeAudioPairReason.MultiChannel, multi, "3チャンネル（非数を含む）");
        AssertNotCompared(multi, "3チャンネル");
        Assert.AreEqual(0L, multi.FrameDifference);

        float[] sound = Wave(4800, n => 0.5 * S441(n));
        foreach ((int wavRate, int oggRate) in new[] { (44100, 48000), (48000, 22050), (96000, 48000) })
        {
            DuplicateMergeAudioJudgement rates = Diagnose(wavRate, sound, oggRate, sound);
            AssertReason(DuplicateMergeAudioPairReason.UnsupportedSampleRates, rates, $"WAV {wavRate}Hz・OGG {oggRate}Hz");
            AssertNotCompared(rates, $"{wavRate}/{oggRate}");
            Assert.IsNull(rates.FrameDifference, "レートが異なる組のフレーム差は測定しません。");
        }
    }

    /// <summary>
    /// 保証：比べる範囲に当たらない長さの違い（OGGが+1153、WAVが+129、32000Hzで±1、レート違いで換算差が許容外）は「長さ違い（比較打ち切り）」で、有限でない値があっても同じ理由、
    /// 比較は未測定。許容範囲内でも長い側の超過分に0でない値（<c>float.Epsilon</c>。OGG側・WAV側、非数を含む）があれば「長さ違い・超過分に音あり」で、超過分は無音でない。
    /// 超過分が無音で許容範囲内なら同等で、フレーム差と「超過分が無音」の真と、先頭から短い側の長さで比べた比較の測定値（相関1）を持つ。同じ長さのときの「超過分が無音」は未測定。
    /// 検出する誤り：長さ違いでの相関の測定、理由6と7の取違え、超過分の有無と「超過分が無音」の食違い、フレーム差の符号（OGG−WAV）の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_LengthDifferences_ReportTheLengthReasonsAndTheExcessState()
    {
        float[] poisonedTail = Tailed(Core + 1153);
        poisonedTail[100] = float.NaN;
        DuplicateMergeAudioJudgement oggTooLong = Diagnose(Tailed(Core), Tailed(Core + 1153));
        AssertReason(DuplicateMergeAudioPairReason.LengthMismatch, oggTooLong, "OGGが+1153");
        AssertNotCompared(oggTooLong, "OGGが+1153");
        Assert.AreEqual(1153L, oggTooLong.FrameDifference);
        Assert.IsNull(oggTooLong.ExcessSilent, "許容範囲外は超過分を検査しません。");
        AssertReason(DuplicateMergeAudioPairReason.LengthMismatch, Diagnose(Tailed(Core), poisonedTail), "OGGが+1153で、範囲の中に非数があっても長さ違いが先です");

        DuplicateMergeAudioJudgement wavTooLong = Diagnose(Tailed(Core + 129), Tailed(Core));
        AssertReason(DuplicateMergeAudioPairReason.LengthMismatch, wavTooLong, "WAVが+129");
        Assert.AreEqual(-129L, wavTooLong.FrameDifference);

        DuplicateMergeAudioJudgement lowRate = Diagnose(32000, Tailed(Core), 32000, Tailed(Core + 1));
        AssertReason(DuplicateMergeAudioPairReason.LengthMismatch, lowRate, "32000HzでOGGが+1");
        Assert.AreEqual(1L, lowRate.FrameDifference);
        AssertReason(DuplicateMergeAudioPairReason.LengthMismatch, Diagnose(32000, Tailed(Core + 1), 32000, Tailed(Core)), "32000HzでWAVが+1");

        float[] Sound(int rate, int frames) => Wave(frames, n => 0.5 * Sine(n, 300, rate));
        DuplicateMergeAudioJudgement converted = Diagnose(48000, Sound(48000, 48125), 44100, Sound(44100, 44213));
        AssertReason(DuplicateMergeAudioPairReason.LengthMismatch, converted, "レート違いで換算差が+295");
        AssertNotCompared(converted, "レート違い");
        Assert.IsNull(converted.FrameDifference);
        AssertReason(DuplicateMergeAudioPairReason.LengthMismatch, Diagnose(48000, Sound(48000, 47875), 44100, Sound(44100, 43987)), "レート違いで換算差が−295");

        float[] oggAudible = Tailed(Core + 100), wavAudible = Tailed(Core + 100), oggNaN = Tailed(Core + 100);
        oggAudible[Core + 10] = float.Epsilon;
        wavAudible[Core + 10] = float.Epsilon;
        oggNaN[Core + 10] = float.NaN;
        DuplicateMergeAudioJudgement oggExcess = Diagnose(Tailed(Core), oggAudible);
        AssertReason(DuplicateMergeAudioPairReason.LengthExcessAudible, oggExcess, "OGGの超過分に0でない最小の値");
        AssertNotCompared(oggExcess, "OGGの超過分");
        Assert.AreEqual(100L, oggExcess.FrameDifference);
        Assert.AreEqual(false, oggExcess.ExcessSilent);
        DuplicateMergeAudioJudgement wavExcess = Diagnose(wavAudible, Tailed(Core));
        AssertReason(DuplicateMergeAudioPairReason.LengthExcessAudible, wavExcess, "WAVの超過分に0でない最小の値");
        Assert.AreEqual(-100L, wavExcess.FrameDifference);
        Assert.AreEqual(false, wavExcess.ExcessSilent);
        AssertReason(DuplicateMergeAudioPairReason.LengthExcessAudible, Diagnose(Tailed(Core), oggNaN), "超過分の非数は、有限でない値ではなく超過分に音ありです");

        DuplicateMergeAudioJudgement silentExcess = Diagnose(Tailed(Core), Tailed(Core + 100));
        AssertReason(DuplicateMergeAudioPairReason.None, silentExcess, "超過分が無音");
        Assert.AreEqual(100L, silentExcess.FrameDifference);
        Assert.AreEqual(true, silentExcess.ExcessSilent);
        Assert.IsNotNull(silentExcess.Comparison);
        Assert.AreEqual(1.0, silentExcess.Comparison.Value.Correlation, Tolerance, "短い側の長さで比べた全帯域の相関です。");

        DuplicateMergeAudioJudgement sameLength = Diagnose(Tailed(Core), Tailed(Core));
        Assert.AreEqual(0L, sameLength.FrameDifference);
        Assert.IsNull(sameLength.ExcessSilent, "同じ長さでは超過分を検査しません。");
    }

    /// <summary>
    /// 保証：比べる範囲の非数・無限大は「有限でない値」（同じ長さ・同じレートで、WAV側・OGG側のどちらでも。許容範囲内の長さ違いの比べる範囲の中でも、レート違いは変換前の全サンプル）で、
    /// 比較は未測定。一方だけが無音の組は（両方向、レート違いの組を含め）「片方だけ無音」で、比較は未測定。両方が無音の組と0フレームの組は同等で、比較は未測定。
    /// 検出する誤り：有限でない値と無音の取違え、非数が無音の判定へ紛れる誤り、無音の組での比較の測定値の混入、0フレームでの例外。
    /// </summary>
    [TestMethod]
    public void Judge_NonFiniteAndSilence_ReportTheirReasonsWithoutComparison()
    {
        float[] wave = Wave(Core, n => 0.5 * S441(n));
        foreach (float poison in new[] { float.NaN, float.PositiveInfinity })
        {
            float[] bad = [.. wave];
            bad[100] = poison;
            DuplicateMergeAudioJudgement inWav = Diagnose(bad, wave);
            AssertReason(DuplicateMergeAudioPairReason.NonFinite, inWav, $"{poison}：WAV側");
            AssertNotCompared(inWav, "WAV側");
            Assert.AreEqual(0L, inWav.FrameDifference);
            AssertReason(DuplicateMergeAudioPairReason.NonFinite, Diagnose(wave, bad), $"{poison}：OGG側");

            float[] longer = Tailed(Core + 100);
            longer[Core - 5] = poison;
            DuplicateMergeAudioJudgement inRange = Diagnose(Tailed(Core), longer);
            AssertReason(DuplicateMergeAudioPairReason.NonFinite, inRange, $"{poison}：許容範囲内の長さ違いの比べる範囲の中");
            Assert.AreEqual(true, inRange.ExcessSilent, "超過分は無音として検査を通っています。");

            float[] ogg44 = Analytic(44100, 17640);
            ogg44[17000] = poison;
            DuplicateMergeAudioJudgement converted = Diagnose(48000, Analytic(48000, 19200), 44100, ogg44);
            AssertReason(DuplicateMergeAudioPairReason.NonFinite, converted, $"{poison}：レート違いのOGG（変換前の全サンプル）");
            Assert.IsNull(converted.FrameDifference);
        }

        float[] silence = new float[Core];
        DuplicateMergeAudioJudgement oneWavSilent = Diagnose(silence, wave);
        AssertReason(DuplicateMergeAudioPairReason.OneSideSilent, oneWavSilent, "WAVだけ無音");
        AssertNotCompared(oneWavSilent, "WAVだけ無音");
        AssertReason(DuplicateMergeAudioPairReason.OneSideSilent, Diagnose(wave, silence), "OGGだけ無音");
        float[] silent48 = new float[4800], silent44 = new float[4410], sound44 = Analytic(44100, 4410);
        DuplicateMergeAudioJudgement converted44 = Diagnose(48000, silent48, 44100, sound44);
        AssertReason(DuplicateMergeAudioPairReason.OneSideSilent, converted44, "レート違いでWAVだけ無音");
        AssertNotCompared(converted44, "レート違い");
        AssertReason(DuplicateMergeAudioPairReason.OneSideSilent, Diagnose(48000, Analytic(48000, 4800), 44100, silent44), "レート違いでOGGだけ無音");

        DuplicateMergeAudioJudgement bothSilent = Diagnose(silence, [.. silence]);
        AssertReason(DuplicateMergeAudioPairReason.None, bothSilent, "両方無音");
        AssertNotCompared(bothSilent, "両方無音");
        DuplicateMergeAudioJudgement zeroFrames = Diagnose([], []);
        AssertReason(DuplicateMergeAudioPairReason.None, zeroFrames, "0フレーム");
        AssertNotCompared(zeroFrames, "0フレーム");
        DuplicateMergeAudioJudgement silentExcessOnly = Diagnose([], new float[1152]);
        AssertReason(DuplicateMergeAudioPairReason.None, silentExcessOnly, "WAVが空でOGGの超過分が無音");
        Assert.AreEqual(1152L, silentExcessOnly.FrameDifference);
        Assert.AreEqual(true, silentExcessOnly.ExcessSilent);
        AssertNotCompared(silentExcessOnly, "比べる範囲が0フレーム");
    }

    /// <summary>
    /// 保証：同等で無音でない組は、比較の測定値をすべて持つ（同じ波形では相関1、音量比0dB、4000Hz以下の音量比0dB、区間の最小比0dB、低域の誤差比0）。
    /// 4000Hzで o=0.5w の波形（300Hz）は、音量差（理由12）も成り立つが、順位が早い「低域波形の不一致」で、測定値は解析値（相関1、音量・低域音量・区間とも −6.0206dB）と
    /// 参照計算の誤差比（1を超える）に一致する。
    /// 検出する誤り：理由の順位の誤り、同等な組での測定値の欠落、最初の不成立での打ち切りによる測定値の欠落、dB・相関・誤差比の式の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandWaveformMismatch_IsReportedBeforeTheVolumeReason()
    {
        float[] wave = Low(Frames, 0.5);

        DuplicateMergeAudioJudgement same = Diagnose(wave, [.. wave]);
        AssertReason(DuplicateMergeAudioPairReason.None, same, "同じ波形");
        Assert.AreEqual(0L, same.FrameDifference);
        Assert.IsNull(same.ExcessSilent);
        DuplicateMergeAudioComparison identical = same.Comparison!.Value;
        Assert.AreEqual(1.0, identical.Correlation, Tolerance);
        Assert.AreEqual(0.0, identical.VolumeDb, Tolerance);
        Assert.AreEqual(0.0, identical.LowVolumeDb!.Value, Tolerance);
        Assert.AreEqual(0.0, identical.LowErrorRatio1k, Tolerance);
        Assert.AreEqual(0.0, identical.LowErrorRatio4k, Tolerance);
        Assert.AreEqual(0.0, identical.SegmentMinDb!.Value, Tolerance);

        float[] half = Low(Frames, 0.25);
        DuplicateMergeAudioJudgement judgement = Diagnose(wave, half);
        AssertReason(DuplicateMergeAudioPairReason.LowBandWaveform, judgement, "o=0.5w");
        Assert.AreEqual(1.0, judgement.Comparison!.Value.Correlation, Tolerance, "波形は比例するため相関は1です。");
        const double halfAmplitudeDb = -6.0205999;
        Assert.AreEqual(halfAmplitudeDb, judgement.Comparison.Value.VolumeDb, Tolerance, "全帯域の音量比です。");
        Assert.AreEqual(halfAmplitudeDb, judgement.Comparison.Value.LowVolumeDb!.Value, Tolerance, "4000Hz以下の音量比です。");
        Assert.AreEqual(halfAmplitudeDb, judgement.Comparison.Value.SegmentMinDb!.Value, Tolerance, "区間ごとの音量の最小比です。");
        Assert.IsTrue(judgement.Comparison.Value.LowErrorRatio4k > 1 || judgement.Comparison.Value.LowErrorRatio1k > 1, "誤差比が1を超えたことが不成立の理由です。");
        AssertMeasured(Expected(new(Rate, 1, wave), new(Rate, 1, half)), judgement, "o=0.5w");
    }

    /// <summary>
    /// 保証：低域の波形が一致し（12kHzの高域だけ。低域通過で落ちる）、全帯域の相関だけが不足する組（12kHzと11kHzの正弦波）は「相関不足」。
    /// 相関も音量比も外れる組（相関不足と音量差）は順位が早い「相関不足」。4000Hz以下のエネルギーが最大値の1000分の1に満たなければ、4000Hz以下の音量比は未測定。
    /// 検出する誤り：相関不足と他の理由の取違え、4000Hz以下の音量比の対象判定の誤り（未測定にならない）、相関の式の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_CorrelationShortfall_IsReportedWithAnUnmeasuredLowBandVolume()
    {
        float[] wav = HighAt(Frames, 12000, 0.5), ogg = HighAt(Frames, 11000, 0.5);

        DuplicateMergeAudioJudgement judgement = Diagnose(wav, ogg);

        AssertReason(DuplicateMergeAudioPairReason.Correlation, judgement, "12kHzと11kHz");
        Assert.IsTrue(judgement.Comparison!.Value.Correlation < 0.5);
        Assert.IsNull(judgement.Comparison.Value.LowVolumeDb, "4000Hz以下のエネルギーが最大値の1000分の1に満たないチャンネルは対象外です。");
        AssertMeasured(Expected(new(Rate, 1, wav), new(Rate, 1, ogg)), judgement, "相関不足");

        AssertReason(DuplicateMergeAudioPairReason.Correlation, Diagnose(wav, Scaled(ogg, 0.5)), "相関も音量比も外れる組");
    }

    /// <summary>
    /// 保証：低域の波形と相関が成り立ち、全帯域の音量比だけが範囲外（o=0.5w の12kHz）の組は「音量差」で、測定値は解析値（−6.0206dB、相関1、区間−6.0206dB）。
    /// 低域の波形・全帯域の音量比が成り立ち、4000Hz以下の音量比だけが範囲外（高域は同一、300Hzの成分だけ0.8倍）の組も「音量差」で、4000Hz以下の音量比は参照計算に一致する。
    /// 音量差と区間音量差が両方成り立つ組は順位が早い「音量差」。
    /// 検出する誤り：全帯域と4000Hz以下の音量比の見落とし、音量差と区間音量差の取違え、4000Hz以下の音量比の測定値の欠落。
    /// </summary>
    [TestMethod]
    public void Judge_VolumeDifference_IsReportedForFullBandAndLowBandRatios()
    {
        float[] wav = HighAt(Frames, 12000, 0.5);
        DuplicateMergeAudioJudgement full = Diagnose(wav, Scaled(wav, 0.5));
        AssertReason(DuplicateMergeAudioPairReason.Volume, full, "全帯域の音量比が0.5");
        Assert.AreEqual(1.0, full.Comparison!.Value.Correlation, Tolerance);
        Assert.AreEqual(-6.0205999, full.Comparison.Value.VolumeDb, Tolerance);
        Assert.AreEqual(-6.0205999, full.Comparison.Value.SegmentMinDb!.Value, Tolerance);
        AssertMeasured(Expected(new(Rate, 1, wav), new(Rate, 1, Scaled(wav, 0.5))), full, "全帯域");

        float[] mixedWav = Add(HighAt(Frames, 12000, 0.5), Low(Frames, 0.3));
        float[] mixedOgg = Add(HighAt(Frames, 12000, 0.5), Low(Frames, 0.3 * 0.8));
        DuplicateMergeAudioJudgement low = Diagnose(mixedWav, mixedOgg);
        AssertReason(DuplicateMergeAudioPairReason.Volume, low, "4000Hz以下の音量比が約0.8");
        Assert.IsNotNull(low.Comparison!.Value.LowVolumeDb);
        Assert.IsTrue(low.Comparison.Value.LowVolumeDb < 20 * Math.Log10(0.84), "4000Hz以下の音量比は0.84未満です。");
        AssertMeasured(Expected(new(Rate, 1, mixedWav), new(Rate, 1, mixedOgg)), low, "4000Hz以下");

        float[] segmentDrop = [.. wav];
        for (int frame = 5 * Segment; frame < 6 * Segment; frame++) { segmentDrop[frame] = (float)(segmentDrop[frame] * 0.5); }
        float[] both = Scaled(segmentDrop, 0.5);
        AssertReason(DuplicateMergeAudioPairReason.Volume, Diagnose(wav, both), "音量差と区間音量差が両方成り立つ組");
    }

    /// <summary>
    /// 保証：一つの区間だけ振幅を0.1倍にした組（それ以外は同じ12kHz）は「区間音量差」で、区間の最小比は 10·log10(0.01) = −20dB。全帯域の音量比と相関は範囲内。
    /// ステレオで左を0.95倍・右を1.1倍にした組は同等で、全帯域の音量比は0dBから最も遠い右の +0.828dB、区間の最小比は右の −0.828dB。
    /// 検出する誤り：区間の最小比の式・単位（10·log10）の誤り、チャンネルをまたぐ最大・最小の選択の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentVolumeAndStereoRatios_AreMeasuredPerChannel()
    {
        float[] wav = HighAt(Frames, 12000, 0.5);
        float[] dropped = [.. wav];
        for (int frame = 7 * Segment; frame < 8 * Segment; frame++) { dropped[frame] = (float)(dropped[frame] * 0.1); }

        DuplicateMergeAudioJudgement segment = Diagnose(wav, dropped);

        AssertReason(DuplicateMergeAudioPairReason.SegmentVolume, segment, "一つの区間だけ0.1倍");
        Assert.AreEqual(-20.0, segment.Comparison!.Value.SegmentMinDb!.Value, Tolerance);
        AssertMeasured(Expected(new(Rate, 1, wav), new(Rate, 1, dropped)), segment, "区間音量差");

        float[] left = HighAt(Frames, 12000, 0.5), right = HighAt(Frames, 11000, 0.5);
        DuplicateMergeAudioJudgement stereo = DiagnoseStereo(left, right, Scaled(left, 0.95), Scaled(right, 1.1));
        AssertReason(DuplicateMergeAudioPairReason.None, stereo, "左0.95倍・右1.1倍");
        Assert.AreEqual(20 * Math.Log10(1.1), stereo.Comparison!.Value.VolumeDb, Tolerance, "0dBから最も遠い右です。");
        Assert.AreEqual(-20 * Math.Log10(1.1), stereo.Comparison.Value.SegmentMinDb!.Value, Tolerance);
        AssertMeasured(
            Expected(new(Rate, 2, Interleave(left, right)), new(Rate, 2, Interleave(Scaled(left, 0.95), Scaled(right, 1.1)))), stereo, "ステレオ");
    }

    /// <summary>
    /// 保証：個別に見るチャンネルで一方のエネルギーが0の場合、音量比は無限大で測定される（OGGの右が無音なら負の無限大、WAVの右が無音なら正の無限大）。
    /// 区間の最小比は、小さい側が0なら負の無限大。理由は順位が早い低域波形の不一致（右の低域の誤差が上限を超える）。
    /// 検出する誤り：エネルギー0での0除算・非数の測定値、無限大の符号の誤り、無限大の測定値の欠落。
    /// </summary>
    [TestMethod]
    public void Judge_OneSidedZeroChannel_MeasuresInfiniteVolumeRatios()
    {
        float[] left = Low(Frames, 0.5), right = Low(Frames, 0.5, 90);
        float[] zeros = new float[Frames];

        DuplicateMergeAudioJudgement oggRightSilent = DiagnoseStereo(left, right, left, zeros);
        AssertReason(DuplicateMergeAudioPairReason.LowBandWaveform, oggRightSilent, "OGGの右が無音");
        Assert.IsTrue(double.IsNegativeInfinity(oggRightSilent.Comparison!.Value.VolumeDb));
        Assert.IsTrue(double.IsNegativeInfinity(oggRightSilent.Comparison.Value.LowVolumeDb!.Value));
        Assert.IsTrue(double.IsNegativeInfinity(oggRightSilent.Comparison.Value.SegmentMinDb!.Value));

        DuplicateMergeAudioJudgement wavRightSilent = DiagnoseStereo(left, zeros, left, right);
        AssertReason(DuplicateMergeAudioPairReason.LowBandWaveform, wavRightSilent, "WAVの右が無音");
        Assert.IsTrue(double.IsPositiveInfinity(wavRightSilent.Comparison!.Value.VolumeDb));
        Assert.IsTrue(double.IsNegativeInfinity(wavRightSilent.Comparison.Value.SegmentMinDb!.Value));
    }
}
