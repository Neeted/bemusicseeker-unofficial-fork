using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

/// <summary>
/// WAVとOGGの同等性の判定（純粋な計算）を、仕様の手順1〜4（形式、比べる範囲と変換、無音、低域の波形・全帯域の相関・音量比・区間ごとの音量）の境界で確認します。
/// 総和が倍精度で厳密になる波形（交番・整数比の振幅）では「ちょうど」と「わずかに外」を対にし、厳密にできない境界（<c>10^-1.2</c>、フィルタを通る量）では両側に余裕を取った合成波形を使います。
/// 期待値のしきい値は、実装の定数ではなく契約の数値を直接書きます。
/// 記号：<c>S441</c> は441Hzの正弦波、<c>Alt</c> は1フレームごとに符号が変わる波形（ナイキスト周波数。4000Hz・1000Hzの低域通過フィルタが取り除く高域）、
/// 「高域」は12kHzの正弦波（44100Hzで4000Hz・1000Hzの低域通過フィルタが取り除く）、「低域」は300Hzの正弦波です。サンプルレートを指定しないケースは44100Hzです。
/// </summary>
[TestClass]
public sealed partial class DuplicateMergeAudioEquivalenceTests
{
    private const int Rate = 44100;

    /// <summary>既定の長さ（20区間分）です。</summary>
    private const int Frames = 40960;

    private const int Segment = 2048;

    private static DuplicateMergeAudioSamples Mono(params double[] over256) => Make(Rate, 1, over256);

    private static DuplicateMergeAudioSamples Make(int rate, int channels, double[] over256)
        => new(rate, channels, [.. over256.Select(value => (float)(value / 256))]);

    private static double[] Repeat(double[] pattern, int times) => [.. Enumerable.Range(0, times).SelectMany(_ => pattern)];

    private static float[] Wave(int frames, Func<int, double> value) => [.. Enumerable.Range(0, frames).Select(frame => (float)value(frame))];

    private static float[] Negate(float[] samples) => [.. samples.Select(sample => -sample)];

    private static float[] Interleave(float[] left, float[] right)
    {
        float[] samples = new float[left.Length * 2];
        for (int index = 0; index < left.Length; index++)
        {
            samples[index * 2] = left[index];
            samples[index * 2 + 1] = right[index];
        }
        return samples;
    }

    /// <summary>判定が同等かだけを返します（判定の戻り値の形が理由と測定値を持つようになっても、既存の判定の保証が見る値は同等かだけです）。</summary>
    private static bool JudgeEquivalent(DuplicateMergeAudioSamples wav, DuplicateMergeAudioSamples ogg)
        => DuplicateMergeAudioEquivalence.Judge(wav, ogg).Equivalent;

    private static bool JudgeMono(int rate, float[] wav, float[] ogg)
        => JudgeEquivalent(new(rate, 1, wav), new(rate, 1, ogg));

    private static bool JudgeMono(int wavRate, float[] wav, int oggRate, float[] ogg)
        => JudgeEquivalent(new(wavRate, 1, wav), new(oggRate, 1, ogg));

    private static bool JudgeStereo(int rate, float[] wavLeft, float[] wavRight, float[] oggLeft, float[] oggRight)
        => JudgeEquivalent(new(rate, 2, Interleave(wavLeft, wavRight)), new(rate, 2, Interleave(oggLeft, oggRight)));

    private static bool JudgeStereo(float[] wavLeft, float[] wavRight, float[] oggLeft, float[] oggRight)
        => JudgeStereo(Rate, wavLeft, wavRight, oggLeft, oggRight);

    private static double Sine(int frame, double hertz, double rate = Rate, double phase = 0) => Math.Sin(2 * Math.PI * hertz * frame / rate + phase);

    private static double Degrees(double degrees) => degrees * Math.PI / 180;

    private static double S441(int frame) => Sine(frame, 441);

    private static double Alt(int frame) => (frame & 1) == 0 ? 1 : -1;

    /// <summary>低域（300Hz）の正弦波です。</summary>
    private static float[] Low(int frames, double amplitude, double phaseDegrees = 0, int rate = Rate)
        => Wave(frames, n => amplitude * Sine(n, 300, rate, Degrees(phaseDegrees)));

    /// <summary>高域（12kHz）の正弦波です。</summary>
    private static float[] High(int frames, double amplitude, int rate = Rate) => Wave(frames, n => amplitude * Sine(n, 12000, rate));

    private static float[] Add(float[] first, float[] second) => [.. first.Zip(second, (a, b) => a + b)];

    /// <summary>高域の振幅 <paramref name="high"/> に対して、低域のエネルギーが全体の <paramref name="share"/> になる低域の振幅です。</summary>
    private static double LowAmplitudeForShare(double high, double share) => high * Math.Sqrt(share / (1 - share));

    /// <summary>10kHz主体＋300Hz＋3kHz（いずれも12kHz以下）の解析信号を、指定のレートで標本化します。<paramref name="delay"/> フレーム遅らせます。</summary>
    private static float[] Analytic(int rate, int frames, int delay = 0)
        => Wave(frames, n => 0.4 * Sine(n - delay, 10000, rate) + 0.1 * Sine(n - delay, 300, rate) + 0.1 * Sine(n - delay, 3000, rate));

    private const int Core = 8192;

    /// <summary>先頭の <see cref="Core"/> フレームだけに音があり、その後ろが無音の列です（長さの違いを試すための共通の内容）。</summary>
    private static float[] Tailed(int total) => Wave(total, n => n < Core ? 0.5 * S441(n) : 0);

    /// <summary>
    /// 保証：チャンネル数が異なる音源（モノラル2Nとステレオ N。並びは同一）は、向きを入れ替えても同等でない。
    /// 検出する誤り：チャンネル数違いの正規化（並びどおりの比較）。
    /// </summary>
    [TestMethod]
    public void Judge_DifferentChannelCount_IsNotEquivalent()
    {
        double[] wave = [100, -50, 80, 20, -90, 10, 60, -30];

        Assert.IsTrue(JudgeEquivalent(Make(Rate, 1, wave), Make(Rate, 1, wave)), "前提：同形式で同じ波形は同等です。");
        Assert.IsFalse(JudgeEquivalent(Make(Rate, 1, wave), Make(Rate, 2, wave)), "モノラル2Nとステレオ N（並びは同一）です。");
        Assert.IsFalse(JudgeEquivalent(Make(Rate, 2, wave), Make(Rate, 1, wave)), "向きを入れ替えても同じです。");
    }

    /// <summary>
    /// 保証：チャンネル数が3以上の音源は、同一の並びでも同等でない（形式によって並びが異なるため比べない）。
    /// 検出する誤り：3チャンネル以上を並びどおりに比べて同等にする。
    /// </summary>
    [TestMethod]
    public void Judge_ThreeOrMoreChannels_IsNotEquivalent()
    {
        float[] noise = DuplicateMergeAudioTestFiles.Noise(3000, seed: 3);

        Assert.IsFalse(JudgeEquivalent(new(Rate, 3, noise), new(Rate, 3, [.. noise])));
    }

    /// <summary>
    /// 保証：長さの違いとレート違いを許す組合せ（同じレートの44100Hz・48000Hz、WAV 48000Hz・OGG 44100Hz）以外のレートの組合せは、同じ内容でも同等でない。
    /// 検出する誤り：レート条件の拡大（レートの無視、逆向きの組合せの許容、他のレートの組合せの許容）。
    /// </summary>
    [TestMethod]
    public void Judge_UnsupportedSampleRateCombinations_AreNotEquivalent()
    {
        float[] wave = Wave(4800, n => 0.5 * S441(n));

        Assert.IsTrue(JudgeMono(Rate, wave, Rate, wave), "前提：同じレートで同じ波形は同等です。");
        Assert.IsFalse(JudgeMono(44100, wave, 48000, wave), "WAV 44100Hz・OGG 48000Hz です（レート違いはWAV 48000Hz・OGG 44100Hzだけ）。");
        Assert.IsFalse(JudgeMono(48000, wave, 22050, wave), "WAV 48000Hz・OGG 22050Hz です。");
        Assert.IsFalse(JudgeMono(96000, wave, 48000, wave), "WAV 96000Hz・OGG 48000Hz です。");
    }

    /// <summary>
    /// 保証：WAVとOGGのどちらかに非数・無限大のサンプルが一つでもあれば、他のサンプルが一致していても同等でない。
    /// 有限でない値は、長さ違いの比べる範囲の中と超過分の中、レート違いの比べる範囲でも見つける。
    /// 検出する誤り：切詰め・変換の後に検査して超過分や変換前の値を見逃す誤り、非数を通す比較（<c>&lt;</c> による棄却）、非数で判定するチャンネルがなくなって同等になる誤り。
    /// </summary>
    [TestMethod]
    public void Judge_NonFiniteSample_IsNotEquivalent()
    {
        foreach (float poison in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            float[] Poisoned(float[] source, int index) { float[] copy = [.. source]; copy[index] = poison; return copy; }

            float[] wave = Wave(Core, n => 0.5 * S441(n));
            Assert.IsTrue(JudgeMono(Rate, wave, wave), "前提：同じ波形は同等です。");
            Assert.IsFalse(JudgeMono(Rate, Poisoned(wave, 100), wave), $"{poison}：WAVに有限でない値があります。");
            Assert.IsFalse(JudgeMono(Rate, wave, Poisoned(wave, 100)), $"{poison}：OGGに有限でない値があります。");

            float[] longer = Tailed(Core + 100);
            Assert.IsTrue(JudgeMono(Rate, Tailed(Core), longer), "前提：OGGが100フレーム長く、超過分が0なら同等です。");
            Assert.IsFalse(JudgeMono(Rate, Tailed(Core), Poisoned(longer, Core - 5)), $"{poison}：長さ違いのOGGの比べる範囲の中に有限でない値があります。");
            Assert.IsFalse(JudgeMono(Rate, Tailed(Core), Poisoned(longer, Core + 50)), $"{poison}：OGGの超過分の中に有限でない値があります。");
            Assert.IsFalse(JudgeMono(Rate, Poisoned(longer, Core + 50), Tailed(Core)), $"{poison}：WAVの超過分の中に有限でない値があります。");

            float[] wav48 = Analytic(48000, 19200), ogg44 = Analytic(44100, 17640);
            Assert.IsTrue(JudgeMono(48000, wav48, 44100, ogg44), "前提：レート違いの正例は同等です。");
            Assert.IsFalse(JudgeMono(48000, wav48, 44100, Poisoned(ogg44, 17000)), $"{poison}：レート違いのOGGに有限でない値があります。");
            Assert.IsFalse(JudgeMono(48000, Poisoned(wav48, 100), 44100, ogg44), $"{poison}：レート違いのWAVに有限でない値があります。");
        }
    }

    /// <summary>
    /// 保証：同じレート（44100Hz・48000Hz）の長さ違いは、OGGが1152フレーム長いまで・WAVが128フレーム長いまでを、超過分がすべて0のときだけ比べる（境界を含む。許容は非対称）。
    /// 超過分に0でない値が一つでもあれば、OGGが長い場合もWAVが長い場合も同等でなく、−0.0は0として扱う。比べるのは先頭から短い側のフレーム数で、周期を持たない内容でも真になる。
    /// 32000Hz・22050Hzは、1フレームの無音の超過でも同等でない。
    /// 検出する誤り：許容の対称化、1ずれ、レートの制限漏れ、超過分の検査の欠落、末尾にそろえる比較、0でない最小の値の見逃し（0との等値比較の誤り）。
    /// </summary>
    [TestMethod]
    public void Judge_LengthDifference_IsAllowedOnlyWithinTheLimitsAndWithSilentExcess()
    {
        foreach (int rate in new[] { 44100, 48000 })
        {
            Assert.IsTrue(JudgeMono(rate, Tailed(Core), Tailed(Core + 1152)), $"{rate}Hz：OGGが1152フレーム長いです。");
            Assert.IsFalse(JudgeMono(rate, Tailed(Core), Tailed(Core + 1153)), $"{rate}Hz：OGGが1153フレーム長いです。");
            Assert.IsTrue(JudgeMono(rate, Tailed(Core + 128), Tailed(Core)), $"{rate}Hz：WAVが128フレーム長いです。");
            Assert.IsFalse(JudgeMono(rate, Tailed(Core + 129), Tailed(Core)), $"{rate}Hz：WAVが129フレーム長いです。");

            float[] oggLong = Tailed(Core + 100), wavLong = Tailed(Core + 100);
            oggLong[Core + 10] = float.Epsilon;
            wavLong[Core + 10] = float.Epsilon;
            Assert.IsFalse(JudgeMono(rate, Tailed(Core), oggLong), $"{rate}Hz：OGGの超過分に、0でない最小の値があります。");
            Assert.IsFalse(JudgeMono(rate, wavLong, Tailed(Core)), $"{rate}Hz：WAVの超過分に、0でない最小の値があります。");

            float[] negativeZero = Tailed(Core + 100);
            negativeZero[Core + 10] = -0f;
            Assert.IsTrue(JudgeMono(rate, Tailed(Core), negativeZero), $"{rate}Hz：超過分の−0.0は0です。");
        }

        float[] noise = DuplicateMergeAudioTestFiles.Noise(Core, seed: 7);
        Assert.IsTrue(JudgeMono(Rate, noise, [.. noise, .. new float[1000]]), "周期を持たない内容でも、先頭から比べます。");

        foreach (int rate in new[] { 32000, 22050 })
        {
            Assert.IsFalse(JudgeMono(rate, Tailed(Core), Tailed(Core + 1)), $"{rate}Hz：OGGが1フレーム長いです。");
            Assert.IsFalse(JudgeMono(rate, Tailed(Core + 1), Tailed(Core)), $"{rate}Hz：WAVが1フレーム長いです。");
        }
    }

    /// <summary>
    /// 保証：比べる範囲が0フレーム（WAVが空、OGGが無音1152フレーム）なら両方とも0として扱って同等で、その超過分に0でない値があれば同等でない。
    /// 検出する誤り：0フレームでの例外・0除算、超過分の検査の欠落。
    /// </summary>
    [TestMethod]
    public void Judge_ZeroFrameComparison_IsEquivalentOnlyWhenTheExcessIsSilent()
    {
        float[] silence = new float[1152];
        float[] sound = new float[1152];
        sound[1000] = 0.5f;

        Assert.IsTrue(JudgeMono(Rate, [], silence), "WAVが空で、OGGの超過分が無音です。");
        Assert.IsFalse(JudgeMono(Rate, [], sound), "WAVが空で、OGGの超過分に0でない値があります。");
    }

    /// <summary>
    /// 保証：レート違い（WAV 48000Hz・OGG 44100Hz）で、同じ解析信号（10kHz主体＋300Hz＋3kHz）を各レートで標本化した列は同等である。
    /// 検出する誤り：変換なしの拒否、変換の比率の逆転、1フレーム以上のずれ、変換後の音量の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_SameSignalSampledAt48000And44100_IsEquivalent()
    {
        Assert.IsTrue(JudgeMono(48000, Analytic(48000, 4800 * 4), 44100, Analytic(44100, 4410 * 4)));
    }

    /// <summary>
    /// 保証：ステレオでも、WAV 48000Hz・OGG 44100Hzに左右で異なる同じ解析信号を各レートで標本化した列は同等で、OGGの左右を入れ替えると同等でない。
    /// 検出する誤り：変換のチャンネル添字の取違え（左右の混入・入替え）。
    /// </summary>
    [TestMethod]
    public void Judge_StereoSameSignalSampledAt48000And44100_IsEquivalentOnlyWithTheSameChannelOrder()
    {
        static float[] Left(int rate, int frames)
            => Wave(frames, n => 0.1 * Sine(n, 300, rate) + 0.1 * Sine(n, 3000, rate) + 0.4 * Sine(n, 10000, rate));
        static float[] Right(int rate, int frames)
            => Wave(frames, n => 0.1 * Sine(n, 500, rate) + 0.1 * Sine(n, 2000, rate) + 0.4 * Sine(n, 8000, rate));

        var wav = new DuplicateMergeAudioSamples(48000, 2, Interleave(Left(48000, 4800 * 4), Right(48000, 4800 * 4)));
        var ogg = new DuplicateMergeAudioSamples(44100, 2, Interleave(Left(44100, 4410 * 4), Right(44100, 4410 * 4)));
        var swapped = new DuplicateMergeAudioSamples(44100, 2, Interleave(Right(44100, 4410 * 4), Left(44100, 4410 * 4)));

        Assert.IsTrue(JudgeEquivalent(wav, ogg), "左右が同じ並びです。");
        Assert.IsFalse(JudgeEquivalent(wav, swapped), "OGGの左右を入れ替えます。");
    }

    /// <summary>
    /// 保証：レート違いの長さの許容は <c>|147·Nw − 160·No|</c> が294以下で、境界（±294）は比べ、±295は同等でない。
    /// 検出する誤り：許容の開閉の誤り、符号・絶対値の誤り（正負の一方だけを許す）。
    /// </summary>
    [TestMethod]
    public void Judge_RateDifferenceLengthTolerance_IsInclusiveAt294()
    {
        static float[] Sound(int rate, int frames) => Wave(frames, n => 0.5 * Sine(n, 300, rate));

        Assert.IsTrue(JudgeMono(48000, Sound(48000, 48002), 44100, Sound(44100, 44100)), "147·Nw − 160·No = +294 です。");
        Assert.IsTrue(JudgeMono(48000, Sound(48000, 47998), 44100, Sound(44100, 44100)), "147·Nw − 160·No = −294 です。");
        Assert.IsFalse(JudgeMono(48000, Sound(48000, 48125), 44100, Sound(44100, 44213)), "147·Nw − 160·No = +295 です。");
        Assert.IsFalse(JudgeMono(48000, Sound(48000, 47875), 44100, Sound(44100, 43987)), "147·Nw − 160·No = −295 です。");
    }

    /// <summary>
    /// 保証：レート違いでは、OGGを1入力フレーム遅らせた内容は同等でない（遅延の探索をしない）。
    /// 検出する誤り：変換後の遅延探索、位置合わせによる吸収。
    /// </summary>
    [TestMethod]
    public void Judge_RateDifferenceWithOneFrameDelay_IsNotEquivalent()
    {
        Assert.IsFalse(JudgeMono(48000, Analytic(48000, 19200), 44100, Analytic(44100, 17640, delay: 1)));
    }

    /// <summary>
    /// 保証：両方の全サンプルが0なら同等で、一方だけが全0なら（両方向とも）同等でない。レート違いでも同じ。
    /// 検出する誤り：0除算の結果（非数）の扱い誤り、無音同士の不一致、無音と有音の一致。
    /// </summary>
    [TestMethod]
    public void Judge_Silence_IsEquivalentOnlyWhenBothAreSilent()
    {
        Assert.IsTrue(JudgeEquivalent(Mono(0, 0, 0, 0), Mono(0, 0, 0, 0)), "両方が全0です。");
        Assert.IsFalse(JudgeEquivalent(Mono(0, 0, 0, 0), Mono(0, 1, 0, 0)), "OGGだけが有音です。");
        Assert.IsFalse(JudgeEquivalent(Mono(0, 1, 0, 0), Mono(0, 0, 0, 0)), "WAVだけが有音です。");

        float[] silent48 = new float[4800], silent44 = new float[4410];
        float[] sound48 = [.. silent48], sound44 = [.. silent44];
        sound48[1000] = 0.5f;
        sound44[1000] = 0.5f;
        Assert.IsTrue(JudgeMono(48000, silent48, 44100, silent44), "レート違いで、両方が全0です。");
        Assert.IsFalse(JudgeMono(48000, silent48, 44100, sound44), "レート違いで、OGGだけが有音です。");
        Assert.IsFalse(JudgeMono(48000, sound48, 44100, silent44), "レート違いで、WAVだけが有音です。");
    }

    /// <summary>
    /// 保証：同じ位置のサンプル同士だけを比べる。ステレオの左右入替えと、モノラルの1フレームの遅れは、雑音波形では同等でない。
    /// 検出する誤り：チャンネルの入替えの吸収、位置をずらした照合、周波数領域だけの比較。
    /// </summary>
    [TestMethod]
    public void Judge_ComparesAlignedInterleavedSamplesWithoutChannelSwapOrShift()
    {
        float[] noise = DuplicateMergeAudioTestFiles.Noise(2000, seed: 1);
        float[] swapped = [.. noise];
        for (int index = 0; index + 1 < swapped.Length; index += 2) { (swapped[index], swapped[index + 1]) = (swapped[index + 1], swapped[index]); }
        Assert.IsTrue(JudgeEquivalent(new(Rate, 2, noise), new(Rate, 2, [.. noise])), "前提：同一の雑音は同等です。");
        Assert.IsFalse(JudgeEquivalent(new(Rate, 2, noise), new(Rate, 2, swapped)), "左右を入れ替えます。");

        float[] mono = DuplicateMergeAudioTestFiles.Noise(2000, seed: 2);
        float[] delayed = [0, .. mono.Take(mono.Length - 1)];
        Assert.IsFalse(JudgeEquivalent(new(Rate, 1, mono), new(Rate, 1, delayed)), "1フレーム遅らせます。");
    }

    /// <summary>
    /// 保証：1000Hz以下の低域の波形は、振幅0.5の300Hzの位相が10°ずれる（誤差エネルギーが信号の約3%）なら同等、20°ずれる（約12%）なら同等でない。
    /// 20°は4000Hz以下の上限（約20%）を満たし、1000Hz以下の条件（約6%）だけで結果が分かれる。
    /// 検出する誤り：1000Hzの低域の条件の欠落。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandWaveformBelow1000Hz_IsLimited()
    {
        float[] wav = Low(Frames, 0.5);

        Assert.IsTrue(JudgeMono(Rate, wav, Low(Frames, 0.5, 10)), "位相が10°ずれます。");
        Assert.IsFalse(JudgeMono(Rate, wav, Low(Frames, 0.5, 20)), "位相が20°ずれます。");
    }

    /// <summary>
    /// 保証：4000Hz以下の低域の波形は、2.5kHzの位相が15°ずれる（誤差エネルギーが信号の約7%）なら同等、35°ずれる（約36%）なら同等でない。
    /// 2.5kHzは1000Hzのフィルタで落ちるため、1000Hzの条件は両方とも満たし、4000Hzの条件（約21%）だけで結果が分かれる。
    /// 検出する誤り：4000Hzの低域の条件の欠落。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandWaveformBelow4000Hz_IsLimited()
    {
        float[] wav = Wave(Frames, n => 0.5 * Sine(n, 2500));

        Assert.IsTrue(JudgeMono(Rate, wav, Wave(Frames, n => 0.5 * Sine(n, 2500, Rate, Degrees(15)))), "位相が15°ずれます。");
        Assert.IsFalse(JudgeMono(Rate, wav, Wave(Frames, n => 0.5 * Sine(n, 2500, Rate, Degrees(35)))), "位相が35°ずれます。");
    }

    /// <summary>
    /// 保証：低域の波形は区間ごとに見る。一つの区間だけ位相を40°ずらす（その区間の誤差は上限を超えるが、ファイル全体でまとめれば上限内に収まる大きさ）と同等でない。
    /// 検出する誤り：ファイル全体でまとめた判定。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandWaveformIsJudgedPerSegment()
    {
        float[] wav = Low(Frames, 0.5);
        float[] ogg = Wave(Frames, n => 0.5 * Sine(n, 300, Rate, n / Segment == 10 ? Degrees(40) : 0));

        Assert.IsFalse(JudgeMono(Rate, wav, ogg));
    }

    /// <summary>
    /// 保証：1000Hz以下の誤差の上限には、区間の全帯域のエネルギー <c>F(s)</c> の項（0.01倍）がある。高域が中心で低域の割合が2%の音は、低域の位相が30°ずれても同等
    /// （誤差は上限の約0.45倍。<c>0.05·L₁</c> だけの上限なら約5.4倍で超える）。低域の割合が20%の音は、低域の位相が22°ずれると同等でない
    /// （1000Hzの誤差は上限の約1.4倍で、4000Hzの誤差は上限の約0.65倍に収まり、1000Hzの条件だけで結果が分かれる。F項の係数が約2倍なら上限の約0.94倍で同等になる）。
    /// 検出する誤り：F項の欠落（低域の割合が小さい音の正しい変換を同等でないとする誤り）、F項の過大。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandWaveformLimit_HasAFullBandTermForSoundsWithLittleLowBand()
    {
        foreach ((double share, double phase, bool expected) in new[] { (0.02, 30.0, true), (0.20, 22.0, false) })
        {
            double low = LowAmplitudeForShare(0.5, share);
            float[] wav = Add(High(Frames, 0.5), Low(Frames, low));
            float[] ogg = Add(High(Frames, 0.5), Low(Frames, low, phase));

            Assert.AreEqual(expected, JudgeMono(Rate, wav, ogg), $"低域の割合が{share:P0}で、低域の位相が{phase}°ずれます。");
        }
    }

    /// <summary>
    /// 保証：低域の波形の上限には、全区間の最大のエネルギー <c>P</c> の項（1000Hz以下は0.001倍）がある。最大の区間の1万分の1の静かな区間で波形が反転しても同等で、1000分の1なら同等でない。
    /// 検出する誤り：P項の欠落（静かな区間の反転の誤棄却）、P項の過大。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandWaveformLimit_HasAPeakTermForQuietSegments()
    {
        foreach ((double quietShare, bool expected) in new[] { (1e-4, true), (1e-3, false) })
        {
            double quiet = 0.5 * Math.Sqrt(quietShare);
            float[] wav = Wave(Frames, n => (n / Segment == 10 ? quiet : 0.5) * Sine(n, 300));
            float[] ogg = Wave(Frames, n => (n / Segment == 10 ? -quiet : 0.5) * Sine(n, 300));

            Assert.AreEqual(expected, JudgeMono(Rate, wav, ogg), $"最大の区間の{quietShare}倍の区間で波形が反転します。");
        }
    }

    /// <summary>
    /// 保証：低域の波形の基準（<c>F(s)</c>・<c>L(s)</c>・<c>P</c>）は全チャンネルで共通である。最大の1000分の1のエネルギーの右チャンネルが反転しても同等
    /// （チャンネルごとの基準なら誤差が上限を超える）。最大の40分の1（個別に見ない弱いチャンネル）の右チャンネルが反転すると、弱いチャンネルも低域の波形では見るため同等でない。
    /// 検出する誤り：チャンネルごとの基準、弱いチャンネルの低域の波形の見落とし。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandWaveformReference_IsSharedAcrossChannelsAndIncludesWeakChannels()
    {
        float[] left = Wave(Frames, n => 0.5 * S441(n));
        foreach ((double share, bool expected) in new[] { (1.0 / 1000, true), (1.0 / 40, false) })
        {
            float[] right = Low(Frames, 0.5 * Math.Sqrt(share));

            Assert.AreEqual(expected, JudgeStereo(left, right, left, Negate(right)), $"右チャンネルのエネルギーは最大の{share:G3}倍で、反転します。");
        }
    }

    /// <summary>
    /// 保証：全帯域の相関（両チャンネルを合算）は、ちょうど0.5で同等、わずかに下回れば同等でない。符号が反転した波形も同等でない。
    /// 検出する誤り：しきい値の誤り、「以上」を「超える」にする誤り、位相反転の見逃し。
    /// </summary>
    [TestMethod]
    public void Judge_FullBandCorrelationThreshold_IsInclusiveAtHalf()
    {
        float[] wav = Wave(Frames, n => 0.5 * Alt(n));
        float[] Ogg(int length) => Wave(Frames, n => 0.5 * Alt(n) * (n >= 20000 && n < 20000 + length ? -1 : 1));

        Assert.IsTrue(JudgeMono(Rate, wav, Ogg(Frames / 4)), "相関がちょうど0.5です。");
        Assert.IsFalse(JudgeMono(Rate, wav, Ogg(Frames / 4 + 1)), "相関が0.5未満です。");
        Assert.IsFalse(JudgeMono(Rate, wav, Negate(wav)), "符号が反転した波形です。");
    }

    /// <summary>
    /// 保証：全帯域の相関は両チャンネルを合算して求める。左の相関1・右（エネルギーが左の10分の1）の相関−1は、合算で約0.82となり同等
    /// （チャンネル別や平均で求める実装は偽にする）。高域だけが無相関で相関が0.55・音量比が1の音は同等（音量比を射影 <c>Σwo/Σw²</c>（0.55）で求める実装は偽にする）。
    /// 検出する誤り：チャンネル別・平均の相関、音量比の射影による計算。
    /// </summary>
    [TestMethod]
    public void Judge_FullBandCorrelationIsAggregatedAndTheVolumeRatioUsesEnergy()
    {
        float[] left = Wave(Frames, n => 0.5 * S441(n));
        float[] right = Wave(Frames, n => Math.Sqrt(0.0125) * Alt(n));
        Assert.IsTrue(JudgeStereo(left, right, left, Negate(right)), "左の相関が1、右の相関が−1です。");

        double lowAmplitude = 0.5, highAmplitude = lowAmplitude * Math.Sqrt(0.45 / 0.55);
        float[] wav = Add(Wave(Frames, n => lowAmplitude * S441(n)), Wave(Frames, n => highAmplitude * Sine(n, 10000)));
        float[] ogg = Add(Wave(Frames, n => lowAmplitude * S441(n)), Wave(Frames, n => highAmplitude * Math.Cos(2 * Math.PI * 10000 * n / Rate)));
        Assert.IsTrue(JudgeMono(Rate, wav, ogg), "高域だけが無相関です（相関は約0.55）。");
    }

    /// <summary>
    /// 保証：全帯域の音量比 <c>√(Σo²/Σw²)</c> は0.63以上1.15以下で同等で、範囲外は同等でない（境界は含む）。44100Hzのナイキスト交番（フィルタが取り除くため低域の条件に影響しない）を、WAVを200/256として、
    /// OGGを126/256・125/256・230/256・231/256にする。
    /// 検出する誤り：上下限の取り違え・開閉の誤り（二乗した形での比較を含む）、分子と分母の取り違え。
    /// </summary>
    [TestMethod]
    public void Judge_FullBandVolumeRatioThresholds_AreInclusiveFrom063To115()
    {
        double[] Alternating(double value) => Repeat([value, -value], Frames / 2);
        double[] wav = Alternating(200);

        Assert.IsTrue(JudgeEquivalent(Make(Rate, 1, wav), Make(Rate, 1, Alternating(126))), "音量比がちょうど0.63です。");
        Assert.IsFalse(JudgeEquivalent(Make(Rate, 1, wav), Make(Rate, 1, Alternating(125))), "音量比が0.63未満です。");
        Assert.IsTrue(JudgeEquivalent(Make(Rate, 1, wav), Make(Rate, 1, Alternating(230))), "音量比がちょうど1.15です。");
        Assert.IsFalse(JudgeEquivalent(Make(Rate, 1, wav), Make(Rate, 1, Alternating(231))), "音量比が1.15を超えます。");
    }

    /// <summary>
    /// 保証：4000Hz以下の音量比 <c>√(Σo₄²/Σw₄²)</c> は0.84以上1.07以下で同等で、範囲外は同等でない（境界は含む）。サンプルレート4000Hz（どの低域通過フィルタも掛からず w₄ = w）で、
    /// WAVを±200/256、OGGを±168/256・±167/256・±214/256・±215/256にする（全帯域の音量比は範囲内に保たれ、低域の音量比だけで結果が分かれる）。
    /// 検出する誤り：低域の音量比の上下限・開閉の誤り（二乗した形での比較を含む）、分子と分母の取り違え。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandVolumeRatioThresholds_AreInclusiveFrom084To107()
    {
        const int rate = 4000;
        double[] Alternating(double value) => Repeat([value, -value], Frames / 2);
        double[] wav = Alternating(200);

        Assert.IsTrue(JudgeEquivalent(Make(rate, 1, wav), Make(rate, 1, Alternating(168))), "低域の音量比がちょうど0.84です。");
        Assert.IsFalse(JudgeEquivalent(Make(rate, 1, wav), Make(rate, 1, Alternating(167))), "低域の音量比が0.84未満です。");
        Assert.IsTrue(JudgeEquivalent(Make(rate, 1, wav), Make(rate, 1, Alternating(214))), "低域の音量比がちょうど1.07です。");
        Assert.IsFalse(JudgeEquivalent(Make(rate, 1, wav), Make(rate, 1, Alternating(215))), "低域の音量比が1.07を超えます。");
    }

    /// <summary>
    /// 保証：低域の音量比は、WAVの4000Hz以下のエネルギー <c>Σw₄²</c> が最大のエネルギー <c>E</c> の1000分の1以上のチャンネルにだけ適用する。
    /// (a) 高域が中心のWAVの低域が <c>E</c> の0.0012倍でOGGが低域を落とすと同等でなく、0.0008倍なら同等。(b) WAVに低域がなく、OGGにだけ <c>E</c> の0.004倍の低域があっても同等。
    /// 検出する誤り：OGG側や大きい方で適用を決める誤り、<c>E</c> の1000分の1の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandVolumeRatio_AppliesOnlyWhenTheWavLowBandIsAtLeastOneThousandthOfTheLargestEnergy()
    {
        float[] high = High(Frames, 0.5);
        foreach ((double share, bool expected) in new[] { (0.0012, false), (0.0008, true) })
        {
            float[] wav = Add(high, Low(Frames, LowAmplitudeForShare(0.5, share)));

            Assert.AreEqual(expected, JudgeMono(Rate, wav, high), $"(a) WAVの低域が全体の{share}倍で、OGGに低域がありません。");
        }

        float[] oggWithLow = Add(high, Low(Frames, LowAmplitudeForShare(0.5, 0.004)));
        Assert.IsTrue(JudgeMono(Rate, high, oggWithLow), "(b) WAVに低域がなく、OGGにだけ全体の0.004倍の低域があります。");
    }

    /// <summary>
    /// 保証：意図的な音量変更は低域の音量比で見分ける。高域が中心で低域が3%の音の−3dB（全帯域の音量比0.71は範囲内）、低域のある音の−6dB・+3dBは、同等でない。
    /// 検出する誤り：低域の音量比の欠落（全帯域の音量比だけでは−3dBを同等とする）。
    /// </summary>
    [TestMethod]
    public void Judge_IntentionalVolumeChange_IsNotEquivalent()
    {
        float[] soundWithLittleLow = Add(High(Frames, 0.5), Low(Frames, LowAmplitudeForShare(0.5, 0.03)));
        Assert.IsFalse(JudgeMono(Rate, soundWithLittleLow, [.. soundWithLittleLow.Select(sample => sample * (float)Math.Pow(10, -3.0 / 20))]), "高域中心の音の−3dBです。");

        float[] sound = Add(Low(Frames, 0.3), High(Frames, 0.3));
        Assert.IsFalse(JudgeMono(Rate, sound, [.. sound.Select(sample => sample * (float)Math.Pow(10, -6.0 / 20))]), "−6dBです。");
        Assert.IsFalse(JudgeMono(Rate, sound, [.. sound.Select(sample => sample * (float)Math.Pow(10, 3.0 / 20))]), "+3dBです。");
    }

    /// <summary>
    /// 保証：個別に見るチャンネル（エネルギーが最大の30分の1以上）の境界は、ちょうど30分の1を見て、わずかに下回れば見ない。サンプルレート8000Hz、左は値1を30フレーム（エネルギー30）、
    /// 右は1サンプルだけの値（エネルギー1＝ちょうど30分の1、または1−2⁻²⁴）、OGGは左が同じで右を欠く。
    /// 検出する誤り：30分の1の誤り、「以上」を「超える」にする誤り、合計を基準にする誤り。
    /// </summary>
    [TestMethod]
    public void Judge_IndividualChannelThreshold_IsInclusiveAtOneThirtieth()
    {
        const int rate = 8000, frames = 4096;
        float[] left = Wave(frames, n => n < 30 ? 1 : 0);
        float[] silent = new float[frames];
        float[] Right(float value) => Wave(frames, n => n == 100 ? value : 0);

        Assert.IsFalse(JudgeStereo(rate, left, Right(1), left, silent), "右のエネルギーがちょうど最大の30分の1です（個別に見る）。");
        Assert.IsTrue(JudgeStereo(rate, left, Right(1 - MathF.Pow(2, -24)), left, silent), "右のエネルギーが最大の30分の1をわずかに下回ります（個別に見ない）。");
    }

    /// <summary>
    /// 保証：チャンネルごとに見る。個別に見るチャンネルがOGGで欠ければ同等でなく、最大の1000分の1に満たないチャンネルの欠落は同等。OGGにだけある右が最大の30分の1以上なら同等でない。
    /// 個別に見るかは、WAVとOGGの大きい方で決める（WAVの右が40分の1でOGGの右が25分の1なら同等でない）。
    /// 検出する誤り：個別に見るチャンネルの選択をWAVだけで行う誤り、全チャンネルをまとめた判定。
    /// </summary>
    [TestMethod]
    public void Judge_JudgesEachIndividuallyJudgedChannel()
    {
        float[] left = Wave(Frames, n => 0.5 * S441(n));
        float[] silent = new float[Frames];

        Assert.IsFalse(JudgeStereo(left, Low(Frames, 0.15), left, silent), "OGGの右チャンネル（最大の約9%）が欠けています。");
        Assert.IsTrue(JudgeStereo(left, Low(Frames, 0.004), left, silent), "右チャンネルがもともとほぼ無音です。");
        Assert.IsFalse(JudgeStereo(left, silent, left, Low(Frames, 0.1)), "OGGだけに、最大の30分の1以上の右チャンネルがあります。");
        Assert.IsFalse(JudgeStereo(left, Low(Frames, 0.5 / Math.Sqrt(40)), left, Low(Frames, 0.5 / Math.Sqrt(25))), "WAVの右が最大の40分の1で、OGGの右が25分の1です。");
    }

    /// <summary>
    /// 保証：区間ごとの音量は、小さい側が大きい側の−12dB（エネルギー比 <c>10^-1.2</c>、約0.063）以上でなければならない。高域の交番で一区間の振幅比0.27（エネルギー比0.073）は同等、0.24（0.058）は同等でない。
    /// 検出する誤り：振幅比とエネルギー比の取り違え、しきい値の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentLevelDifference_IsLimitedToMinus12dB()
    {
        float[] wav = Wave(Frames, n => 0.5 * Alt(n));
        float[] Ogg(double gain) => Wave(Frames, n => 0.5 * Alt(n) * (n >= 14336 && n < 16384 ? gain : 1));

        Assert.IsTrue(JudgeMono(Rate, wav, Ogg(0.27)), "振幅比0.27（エネルギー比0.073）です。");
        Assert.IsFalse(JudgeMono(Rate, wav, Ogg(0.24)), "振幅比0.24（エネルギー比0.058）です。");
    }

    /// <summary>
    /// 保証：区間の音量は、区間のエネルギーの大きい方が最大の区間エネルギー <c>P</c> の100分の1以上なら見る（ちょうどを含む）。
    /// ピーク区間を交番0.625（エネルギー800）、対象区間を交番1/16（エネルギーちょうど8）とし、対象区間をOGGで欠くと同等でなく、わずかに下回る値なら同等。
    /// 検出する誤り：1000分の1の残り、開閉の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentEnergyFloor_IsInclusiveAtOneHundredthOfThePeak()
    {
        const int frames = 10 * Segment;
        float[] Wav(double target) => Wave(frames, n => n / Segment == 3 ? 0.625 * Alt(n) : n / Segment == 6 ? target * Alt(n) : 0);
        float[] ogg = Wave(frames, n => n / Segment == 3 ? 0.625 * Alt(n) : 0);

        Assert.IsFalse(JudgeMono(Rate, Wav(0.0625), ogg), "対象区間のエネルギーがちょうど最大の100分の1です。");
        Assert.IsTrue(JudgeMono(Rate, Wav(0.0625 - Math.Pow(2, -28)), ogg), "対象区間のエネルギーが100分の1をわずかに下回ります。");
    }

    /// <summary>
    /// 保証：区間の音量の基準 <c>P</c> は全チャンネルで共通で、区間の音量は個別に見るチャンネルだけで見る。
    /// 右（最大の20分の1）の静かな区間（<c>P</c> の200分の1）が欠けても同等。弱いチャンネル（40分の1）の通常の区間が欠けても同等。個別に見る右（20分の1）の通常の区間が欠ければ同等でない。
    /// 検出する誤り：チャンネルごとの <c>P</c>、すべてのチャンネルで区間を見る誤り、個別に見るチャンネルの区間の見落とし。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentEnergyReferenceIsShared_AndOnlyIndividuallyJudgedChannelsAreChecked()
    {
        float[] left = Wave(Frames, n => 0.5 * Alt(n));
        const int quietSegment = 12;

        // 右の全エネルギーを最大（左の512 × 20区間）の20分の1にし、一つの区間だけを P（512）の200分の1（2.56）にする。
        double quiet = Math.Sqrt(2.56 / Segment), normal = Math.Sqrt((512 - 2.56) / (19 * Segment));
        float[] rightWithQuiet = Wave(Frames, n => (n / Segment == quietSegment ? quiet : normal) * Alt(n));
        float[] rightWithoutQuiet = Wave(Frames, n => n / Segment == quietSegment ? 0 : normal * Alt(n));
        Assert.IsTrue(JudgeStereo(left, rightWithQuiet, left, rightWithoutQuiet), "右（最大の20分の1）の静かな区間（Pの200分の1）が欠けます。");

        double weak = Math.Sqrt(256.0 / (20 * Segment)), individual = Math.Sqrt(512.0 / (20 * Segment));
        Assert.IsTrue(JudgeStereo(left, Wave(Frames, n => weak * Alt(n)), left, Wave(Frames, n => n / Segment == quietSegment ? 0 : weak * Alt(n))),
            "弱いチャンネル（最大の40分の1）の通常の区間（Pの40分の1）が欠けます。");
        Assert.IsFalse(JudgeStereo(left, Wave(Frames, n => individual * Alt(n)), left, Wave(Frames, n => n / Segment == quietSegment ? 0 : individual * Alt(n))),
            "個別に見る右（最大の20分の1）の通常の区間（Pの20分の1）が欠けます。");
    }

    /// <summary>
    /// 保証：途中の区間と、末尾の端数の区間（2048フレームに満たない区間）が欠けたOGGは同等でない。高域だけで <c>20·2048+100</c> フレームの末尾100フレームが欠けても同等でない。
    /// 検出する誤り：区間ごとの音量の条件の欠落、末尾の端数の区間を無視する・手前の区間へまとめる誤り。
    /// </summary>
    [TestMethod]
    public void Judge_MissingSegmentInTheMiddleOrAtTheTail_IsNotEquivalent()
    {
        float[] wav = Wave(Frames, n => 0.5 * S441(n));
        Assert.IsFalse(JudgeMono(Rate, wav, Wave(Frames, n => n >= 10240 && n < 12288 ? 0 : 0.5 * S441(n))), "途中の区間が欠けています。");

        int tailFrames = 2048 * 10 + 1000;
        float[] tailWav = Wave(tailFrames, n => 0.5 * S441(n));
        Assert.IsFalse(JudgeMono(Rate, tailWav, Wave(tailFrames, n => n >= 2048 * 10 ? 0 : 0.5 * S441(n))), "末尾の端数の区間が欠けています。");

        int highFrames = 20 * Segment + 100;
        Assert.IsFalse(JudgeMono(Rate, Wave(highFrames, n => 0.5 * Alt(n)), Wave(highFrames, n => n >= 20 * Segment ? 0 : 0.5 * Alt(n))), "高域だけで、末尾100フレームが欠けています。");
    }

    /// <summary>
    /// 保証：区間は先頭から2048フレームごとに区切る。音が区間の後半1024フレームから始まる区間で、OGGにだけ、音の直前512フレームに交番の信号（その区間の音の部分の5%のエネルギー）があっても、
    /// 同じ2048フレームの区間の中の差は許容内で同等である。
    /// 検出する誤り：1024フレームやサンプル単位の区間で比べる誤り（交番だけの区間をWAVの0に対して見る区間として扱い、同等でないと誤判定する）。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentsAreTwoThousandFortyEightFramesLong()
    {
        const int soundStart = 5 * Segment + 1024;
        double alternating = Math.Sqrt(0.05 * 1024 * 0.125 / 512);
        float[] wav = Wave(Frames, n => n >= soundStart ? 0.5 * S441(n) : 0);
        float[] ogg = Wave(Frames, n => n >= soundStart ? 0.5 * S441(n) : n >= soundStart - 512 ? alternating * Alt(n) : 0);

        Assert.IsTrue(JudgeMono(Rate, wav, ogg));
    }

    /// <summary>
    /// 保証：判定は入力のサンプル配列を書き換えない（フィルタ後の波形や切詰め・変換の結果を元の配列へ書き戻さない）。同じ長さ・長さ違い・レート違いのいずれでも同じ。
    /// 検出する誤り：その場でのフィルタ処理・切詰め・変換。
    /// </summary>
    [TestMethod]
    public void Judge_DoesNotModifyTheInputSamples()
    {
        float[] left = Add(Wave(Frames, n => 0.6 * S441(n)), High(Frames, 0.2));
        float[] oggLeft = Add(Wave(Frames, n => 0.6 * S441(n)), Wave(Frames, n => 0.2 * Sine(n, 12000, Rate, Degrees(90))));
        AssertUnchanged(new DuplicateMergeAudioSamples(Rate, 2, Interleave(left, left)), new DuplicateMergeAudioSamples(Rate, 2, Interleave(oggLeft, oggLeft)), "同じ長さ");
        AssertUnchanged(new DuplicateMergeAudioSamples(Rate, 1, Tailed(Core)), new DuplicateMergeAudioSamples(Rate, 1, Tailed(Core + 500)), "長さ違い");
        AssertUnchanged(new DuplicateMergeAudioSamples(48000, 1, Analytic(48000, 19200)), new DuplicateMergeAudioSamples(44100, 1, Analytic(44100, 17640)), "レート違い");
    }

    private static void AssertUnchanged(DuplicateMergeAudioSamples wav, DuplicateMergeAudioSamples ogg, string label)
    {
        float[] wavBefore = [.. wav.Samples];
        float[] oggBefore = [.. ogg.Samples];

        Assert.IsTrue(JudgeEquivalent(wav, ogg), $"{label}：前提：同等です。");
        CollectionAssert.AreEqual(wavBefore, wav.Samples, $"{label}：WAVのサンプルが変わりました。");
        CollectionAssert.AreEqual(oggBefore, ogg.Samples, $"{label}：OGGのサンプルが変わりました。");
    }

    /// <summary>
    /// 保証：低域通過フィルタは、サンプルレートが遮断周波数の4倍を超えるときだけ掛かる（「以下」は掛けない）。
    /// 4000Hzの列：250Hz＋交番（エネルギー比0.2）の交番を反転すると、16000Hz（フィルタなし）は同等でなく、16001Hz（フィルタあり、交番が取り除かれる）は同等。
    /// 1000Hzの列：100Hz＋交番の交番を反転すると、4000Hzは同等でなく、4001Hzは同等。
    /// 検出する誤り：「以下」と「未満」の取り違え、二つの列の遮断周波数・しきい値の取り違え。
    /// </summary>
    [TestMethod]
    public void Judge_LowPassFilterIsAppliedOnlyAboveFourTimesTheCutoff()
    {
        foreach ((int rate, bool expected) in new[] { (16000, false), (16001, true) })
        {
            double alternating = 0.5 * Math.Sqrt(0.1);
            float[] wav = Wave(Frames, n => 0.5 * Sine(n, 250, rate) + alternating * Alt(n));
            float[] ogg = Wave(Frames, n => 0.5 * Sine(n, 250, rate) - alternating * Alt(n));

            Assert.AreEqual(expected, JudgeMono(rate, wav, ogg), $"4000Hzの列：サンプルレート{rate}Hzです。");
        }

        foreach ((int rate, bool expected) in new[] { (4000, false), (4001, true) })
        {
            double alternating = 0.5 * Math.Sqrt(0.015);
            float[] wav = Wave(Frames, n => 0.5 * Sine(n, 100, rate) + alternating * Alt(n));
            float[] ogg = Wave(Frames, n => 0.5 * Sine(n, 100, rate) - alternating * Alt(n));

            Assert.AreEqual(expected, JudgeMono(rate, wav, ogg), $"1000Hzの列：サンプルレート{rate}Hzです。");
        }
    }
}
