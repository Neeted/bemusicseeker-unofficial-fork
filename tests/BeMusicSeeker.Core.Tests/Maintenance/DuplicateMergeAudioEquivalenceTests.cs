using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

/// <summary>
/// WAVとOGGの同等性の判定（純粋な計算）を、仕様の手順1〜3の境界値で確認します。
/// 手順1・2（形式の不一致、有限でない値、無音）は少数の整数波形で、手順3（判定するチャンネル・音量比・全帯域と低域の相関・低域比・区間ごとの音量）は、
/// 総和が倍精度で厳密になる波形か、境界の両側に余裕を取った合成波形で確認します。期待値のしきい値は、実装の定数ではなく契約の数値を直接書きます。
/// 記号：<c>S441</c> は441Hzの正弦波、<c>q</c>・<c>c</c> はサンプルレートの4分の1の周波数（11025Hz）の正弦波・余弦波（低域通過フィルタで落ちる高域）、
/// <c>alt</c> は1フレームごとに符号が変わる波形、<c>sq</c> は32フレームごとに符号が変わる±1の矩形波です。
/// </summary>
[TestClass]
public sealed class DuplicateMergeAudioEquivalenceTests
{
    private const int Rate = 44100;

    /// <summary>既定の長さ（20区間分）です。</summary>
    private const int Frames = 40960;

    private static DuplicateMergeAudioSamples Mono(params double[] over256) => Make(Rate, 1, over256);

    private static DuplicateMergeAudioSamples Make(int rate, int channels, double[] over256)
        => new(rate, channels, [.. over256.Select(value => (float)(value / 256))]);

    private static double[] Repeat(double[] pattern, int times) => [.. Enumerable.Range(0, times).SelectMany(_ => pattern)];

    private static float[] Wave(int frames, Func<int, double> value) => [.. Enumerable.Range(0, frames).Select(frame => (float)value(frame))];

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

    private static bool JudgeMono(int rate, float[] wav, float[] ogg)
        => DuplicateMergeAudioEquivalence.Judge(new(rate, 1, wav), new(rate, 1, ogg));

    private static bool JudgeStereo(float[] wavLeft, float[] wavRight, float[] oggLeft, float[] oggRight)
        => DuplicateMergeAudioEquivalence.Judge(new(Rate, 2, Interleave(wavLeft, wavRight)), new(Rate, 2, Interleave(oggLeft, oggRight)));

    private static double Sine(int frame, double hertz, double rate = Rate, double phase = 0) => Math.Sin(2 * Math.PI * hertz * frame / rate + phase);

    private static double S441(int frame) => Sine(frame, 441);

    private static double Q(int frame) => (frame & 3) switch { 1 => 1, 3 => -1, _ => 0 };

    private static double C(int frame) => (frame & 3) switch { 0 => 1, 2 => -1, _ => 0 };

    private static double Alt(int frame) => (frame & 1) == 0 ? 1 : -1;

    private static double Sq(int frame) => ((frame / 32) & 1) == 0 ? 1 : -1;

    /// <summary>cosφ=0.8 となる位相です。</summary>
    private static readonly double Phase = Math.Acos(0.8);

    /// <summary>
    /// 保証：サンプルレート・チャンネル数・総フレーム数のどれかが異なれば、波形の並びが同じでも同等でない。
    /// 検出する誤り：レートの無視、チャンネル数違いの正規化（mono 2Nとstereo Nの同一視）、長さ違いの切詰め比較。
    /// </summary>
    [TestMethod]
    public void Judge_DifferentSampleRateChannelsOrFrameCount_IsNotEquivalent()
    {
        double[] wave = [100, -50, 80, 20, -90, 10, 60, -30];

        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(Make(Rate, 1, wave), Make(Rate, 1, wave)), "前提：同形式で同じ波形は同等です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Make(Rate, 1, wave), Make(48000, 1, wave)), "A1: レートだけが異なります。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Make(Rate, 1, wave), Make(Rate, 2, wave)), "A2: モノラル2Nとステレオ N（並びは同一）です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Make(Rate, 2, wave), Make(Rate, 1, wave)), "A2: 向きを入れ替えても同じです。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Make(Rate, 1, wave), Make(Rate, 1, [.. wave, 0])), "A3: OGGが1フレーム長い（末尾は無音）です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Make(Rate, 1, [.. wave, 0]), Make(Rate, 1, wave)), "A3: WAVが1フレーム長い場合も同じです。");
    }

    /// <summary>
    /// 保証：両方の全サンプルが0なら同等で、一方だけが全0なら（両方向とも）同等でない。
    /// 検出する誤り：0除算の結果（非数）の扱い誤り、無音同士の不一致、無音と有音の一致。
    /// </summary>
    [TestMethod]
    public void Judge_Silence_IsEquivalentOnlyWhenBothAreSilent()
    {
        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(Mono(0, 0, 0, 0), Mono(0, 0, 0, 0)), "A4: 両方が全0です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(0, 0, 0, 0), Mono(0, 1, 0, 0)), "A5: OGGだけが有音です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(0, 1, 0, 0), Mono(0, 0, 0, 0)), "A5: WAVだけが有音です。");
    }

    /// <summary>
    /// 保証：WAVとOGGのどちらかに非数のサンプルが一つでもあれば、他のサンプルが一致していても同等でない。
    /// 検出する誤り：非数で最大のエネルギーが非数になり、判定するチャンネルがなくなって同等になる誤り。
    /// </summary>
    [TestMethod]
    public void Judge_NaNSample_IsNotEquivalent()
    {
        double[] wave = [100, -50, 80, 20, -90, 10, 60, -30];
        double[] withNaN = [100, -50, double.NaN, 20, -90, 10, 60, -30];

        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(withNaN), Mono(wave)), "WAVに非数があります。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(wave), Mono(withNaN)), "OGGに非数があります。");
    }

    /// <summary>
    /// 保証：音量比 √(Σo²/Σw²) は0.85以上1.15以下で同等で、範囲外は（相関が1でも）同等でない。符号が反転した波形（相関−1）も同等でない。
    /// 検出する誤り：音量比の上下限の取り違え・境界の開閉の誤り（1.15ちょうどを二乗した形で比べて範囲外にする誤りを含む）、
    /// 分子と分母の取り違え（0.85未満と1.15超の入替え）、位相反転の見逃し。
    /// </summary>
    [TestMethod]
    public void Judge_VolumeRatioThresholds_AreInclusiveFrom085To115()
    {
        double[] wav = Repeat([200, -200], 4);

        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([170, -170], 4))), "K5: 音量比がちょうど0.85です。");
        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([230, -230], 4))), "K5: 音量比がちょうど1.15です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([169, -169], 4))), "K5: 音量比が0.85未満です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([231, -231], 4))), "K5: 音量比が1.15を超えます。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([-200, 200], 4))), "K5: 符号が反転した波形です。");
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
        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(new(Rate, 2, noise), new(Rate, 2, [.. noise])), "前提：同一の雑音は同等です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(new(Rate, 2, noise), new(Rate, 2, swapped)), "A11: 左右を入れ替えます。");

        float[] mono = DuplicateMergeAudioTestFiles.Noise(2000, seed: 2);
        float[] delayed = [0, .. mono.Take(mono.Length - 1)];
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(new(Rate, 1, mono), new(Rate, 1, delayed)), "A12: 1フレーム遅らせます。");
    }

    /// <summary>
    /// 保証：チャンネル数が3以上の音源は、同一の並びでも同等でない（形式によって並びが異なるため比べない）。
    /// 検出する誤り：3チャンネル以上を並びどおりに比べて同等にする。
    /// </summary>
    [TestMethod]
    public void Judge_ThreeOrMoreChannels_IsNotEquivalent()
    {
        float[] noise = DuplicateMergeAudioTestFiles.Noise(3000, seed: 3);

        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(new(Rate, 3, noise), new(Rate, 3, [.. noise])));
    }

    /// <summary>
    /// 保証：低域が一致し高域だけが無相関（全帯域の相関0.90・低域の相関1.00）な波形は同等で、音量が0.9倍でも同等（音量比は √(Σo²/Σw²)）。
    /// 検出する誤り：旧条件（全帯域の相関0.95以上）の残り、低域通過フィルタの欠落、音量比を旧式の射影 Σwo/Σw²（0.81）で求める誤り。
    /// </summary>
    [TestMethod]
    public void Judge_HighBandOnlyDifference_IsEquivalentAndVolumeRatioUsesEnergy()
    {
        float[] wav = Wave(Frames, n => 0.6 * S441(n) + 0.2 * Q(n));

        Assert.IsTrue(JudgeMono(Rate, wav, Wave(Frames, n => 0.6 * S441(n) + 0.2 * C(n))), "N1: 高域だけが無相関です。");
        Assert.IsTrue(JudgeMono(Rate, wav, Wave(Frames, n => 0.9 * (0.6 * S441(n) + 0.2 * C(n)))), "N2: OGGの音量が0.9倍です。");
    }

    /// <summary>
    /// 保証：低域の波形が変わる（低域の相関0.80、全帯域の相関0.90）と、全帯域の相関が0.85以上でも同等でない。
    /// 検出する誤り：低域の相関の条件の欠落。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandWaveformChange_IsNotEquivalent()
    {
        float[] wav = Wave(Frames, n => 0.4 * S441(n) + 0.4 * Q(n));
        float[] ogg = Wave(Frames, n => 0.4 * Sine(n, 441, Rate, Phase) + 0.4 * Q(n));

        Assert.IsFalse(JudgeMono(Rate, wav, ogg), "N3");
    }

    /// <summary>
    /// 保証：低域の相関は、WAVの低域のエネルギーがWAVのチャンネル全体の5%未満なら見ず、それ以上なら見る（r=0.02は見ず、r=0.2は見る）。
    /// 低域比はWAVで決める（OGGの低域を大きくしても、WAVの低域比が小さければ見ない）。
    /// 検出する誤り：5%の除外の欠落、しきい値の誤り、低域比をOGG側や大きい方から求める誤り。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandCorrelationIsSkippedOnlyWhenWavLowBandEnergyIsBelowFivePercent()
    {
        static float[] Wav(double r) => Wave(Frames, n => 0.6 * Math.Sqrt(r) * S441(n) + 0.6 * Math.Sqrt(1 - r) * Q(n));
        static float[] Ogg(double r, double lowAmplitude) => Wave(Frames, n => lowAmplitude * Sine(n, 441, Rate, Phase) + 0.6 * Math.Sqrt(1 - r) * Q(n));

        Assert.IsTrue(JudgeMono(Rate, Wav(0.02), Ogg(0.02, 0.6 * Math.Sqrt(0.02))), "N4: 低域比0.02は低域の相関を見ません。");
        Assert.IsFalse(JudgeMono(Rate, Wav(0.2), Ogg(0.2, 0.6 * Math.Sqrt(0.2))), "N4: 低域比0.2は低域の相関（0.80）を見ます。");
        Assert.IsTrue(JudgeMono(Rate, Wav(0.02), Ogg(0.02, 0.6 * Math.Sqrt(0.1))), "N4b: OGGの低域が大きくても、低域比はWAVで決めます。");
    }

    /// <summary>
    /// 保証：全帯域の相関は0.85ちょうどで同等、わずかに下回れば同等でない。
    /// 検出する誤り：「以上」を「超える」にする誤り、しきい値の誤り（低域比がほぼ0のため低域の相関は見ない）。
    /// </summary>
    [TestMethod]
    public void Judge_FullBandCorrelationThreshold_IsInclusiveAt085()
    {
        float[] wav = Wave(Frames, n => 0.5 * Alt(n));
        float[] Ogg(int length) => Wave(Frames, n => 0.5 * Alt(n) * (n >= 20000 && n < 20000 + length ? -1 : 1));

        Assert.IsTrue(JudgeMono(Rate, wav, Ogg(3072)), "N5: 相関がちょうど0.85です。");
        Assert.IsFalse(JudgeMono(Rate, wav, Ogg(3073)), "N5: 相関が0.85未満です。");
    }

    /// <summary>
    /// 保証：低域の相関は0.95ちょうどで同等、わずかに下回れば同等でない。
    /// 検出する誤り：「以上」を「超える」にする誤り、しきい値の誤り（フィルタを掛けない16000Hzの経路で厳密に作る）。
    /// </summary>
    [TestMethod]
    public void Judge_LowBandCorrelationThreshold_IsInclusiveAt095()
    {
        float[] wav = Wave(Frames, n => 0.5 * Sq(n));
        float[] Ogg(int length) => Wave(Frames, n => 0.5 * Sq(n) * (n >= 20000 && n < 20000 + length ? -1 : 1));

        Assert.IsTrue(JudgeMono(16000, wav, Ogg(1024)), "N6: 相関がちょうど0.95です。");
        Assert.IsFalse(JudgeMono(16000, wav, Ogg(1025)), "N6: 相関が0.95未満です。");
    }

    /// <summary>
    /// 保証：サンプルレートが16000Hz以下の音源には低域通過フィルタを掛けず（全帯域の相関に0.95を要求）、16001Hz以上には掛ける。
    /// 検出する誤り：「未満」と「以下」の取り違え、常にフィルタを掛ける・掛けない誤り。
    /// </summary>
    [TestMethod]
    public void Judge_LowPassFilterIsAppliedOnlyAboveSampleRate16000()
    {
        foreach ((int rate, bool expected) in new[] { (16000, false), (16001, true) })
        {
            float[] wav = Wave(Frames, n => 0.6 * Sine(n, 250, rate) + 0.6 * Math.Sqrt(0.025) * Alt(n));
            float[] ogg = Wave(Frames, n => 0.6 * Sine(n, 250, rate) - 0.6 * Math.Sqrt(0.025) * Alt(n));

            Assert.AreEqual(expected, JudgeMono(rate, wav, ogg), $"N7: サンプルレート{rate}Hzです。");
        }
    }

    /// <summary>
    /// 保証：チャンネルごとに判定する。片チャンネルが欠けたOGG・片チャンネルが反転したOGG・WAVにない音を持つOGGは同等でなく、
    /// チャンネルのエネルギー（WAVとOGGの大きい方）が両音源の全チャンネルの最大の1万分の1未満のチャンネルは、OGGで欠けていても判定しない。
    /// 検出する誤り：全チャンネルをまとめた判定、ほぼ無音のチャンネルも判定する誤り、判定するチャンネルをWAVのエネルギーだけで選ぶ誤り、相関だけをまとめる誤り。
    /// </summary>
    [TestMethod]
    public void Judge_JudgesEachChannelAndSkipsNearlySilentChannels()
    {
        float[] left = Wave(Frames, n => 0.5 * S441(n));
        float[] Right(double amplitude) => Wave(Frames, n => amplitude * Sine(n, 300, Rate));
        float[] silent = new float[Frames];

        Assert.IsFalse(JudgeStereo(left, Right(0.15), left, silent), "N8a: OGGの右チャンネルが欠けています。");
        Assert.IsTrue(JudgeStereo(left, Right(0.004), left, silent), "N8b: 右チャンネルがもともとほぼ無音です。");
        Assert.IsFalse(JudgeStereo(left, silent, left, Right(0.05)), "N8c: OGGだけに右チャンネルの音があります。");
        Assert.IsFalse(JudgeStereo(left, Right(0.07), left, [.. Right(0.07).Select(sample => -sample)]), "N8d: 右チャンネルが反転しています。");
    }

    /// <summary>
    /// 保証：チャンネルのエネルギーが最大の1万分の1ちょうどなら判定し（OGGで欠けていれば同等でない）、それ未満なら判定しない。
    /// 検出する誤り：「未満」と「以下」の取り違え。
    /// </summary>
    [TestMethod]
    public void Judge_ChannelEnergyFloorIsOneTenThousandthOfTheLargest()
    {
        const int frames = 10000;
        float[] left = Wave(frames, Sq);
        float[] silent = new float[frames];
        float[] Right(float value) => Wave(frames, n => n == 5000 ? value : 0);

        Assert.IsFalse(JudgeStereo(left, Right(1), left, silent), "N9: エネルギーがちょうど1万分の1です。");
        Assert.IsTrue(JudgeStereo(left, Right(1 - MathF.Pow(2, -24)), left, silent), "N9: エネルギーが1万分の1未満です。");
    }

    /// <summary>
    /// 保証：2048フレームごとの区間のうち、途中の区間と末尾の端数の区間が欠けたOGGは同等でない。
    /// 検出する誤り：区間ごとの音量の条件の欠落、末尾の端数の区間を無視する誤り。
    /// </summary>
    [TestMethod]
    public void Judge_MissingSegmentInTheMiddleOrAtTheTail_IsNotEquivalent()
    {
        float[] wav = Wave(Frames, n => 0.5 * S441(n));
        Assert.IsFalse(JudgeMono(Rate, wav, Wave(Frames, n => n >= 10240 && n < 12288 ? 0 : 0.5 * S441(n))), "N10a: 途中の区間が欠けています。");

        int tailFrames = 2048 * 10 + 1000;
        float[] tailWav = Wave(tailFrames, n => 0.5 * S441(n));
        Assert.IsFalse(JudgeMono(Rate, tailWav, Wave(tailFrames, n => n >= 2048 * 10 ? 0 : 0.5 * S441(n))), "N10b: 末尾の端数の区間が欠けています。");
    }

    /// <summary>
    /// 保証：最大の区間エネルギーの1000分の1ちょうどの区間は見て（OGGで欠けていれば同等でない）、それ未満の区間は見ない。
    /// 検出する誤り：境界の開閉の誤り、小さい区間を除外しない誤り。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentEnergyFloorIsOneThousandthOfThePeak()
    {
        const int rate = 8000;
        float[] Wav(float value) => Wave(4096, n => n < 1000 ? Sq(n) : n == 3000 ? value : 0);
        float[] ogg = Wave(4096, n => n < 1000 ? Sq(n) : 0);

        Assert.IsFalse(JudgeMono(rate, Wav(1), ogg), "N11: 区間のエネルギーがちょうど1000分の1です。");
        Assert.IsTrue(JudgeMono(rate, Wav(1 - MathF.Pow(2, -24)), ogg), "N11: 区間のエネルギーが1000分の1未満です。");
    }

    /// <summary>
    /// 保証：区間の小さい側のエネルギーは大きい側の−12dB（エネルギー比で10^-1.2、約0.063）以上が必要で、
    /// 振幅比0.27は同等、0.24は同等でない。
    /// 検出する誤り：振幅比とエネルギー比の取り違え、しきい値の誤り。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentLevelDifference_IsLimitedToMinus12dB()
    {
        const int rate = 8000;
        const int frames = 2048 * 10;
        float[] wav = Wave(frames, n => 0.5 * Sq(n));
        float[] Ogg(double gain) => Wave(frames, n => 0.5 * Sq(n) * (n >= 14336 && n < 16384 ? gain : 1));

        Assert.IsTrue(JudgeMono(rate, wav, Ogg(0.27)), "N12: 振幅比0.27（エネルギー比0.073）です。");
        Assert.IsFalse(JudgeMono(rate, wav, Ogg(0.24)), "N12: 振幅比0.24（エネルギー比0.058）です。");
    }

    /// <summary>
    /// 保証：区間のエネルギーの基準（最大の区間エネルギー）は判定するチャンネル全体で共通で、
    /// 小さいチャンネルの区間の欠けは、他のチャンネルの最大の区間エネルギーの1000分の1未満なら見ない。
    /// 検出する誤り：基準をチャンネルごとに求める誤り。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentPeakIsSharedAcrossJudgedChannels()
    {
        float[] left = Wave(Frames, n => 0.5 * S441(n));
        float[] right = Wave(Frames, n => 0.5 * Math.Sqrt(5e-4) * Sine(n, 300, Rate));
        float[] oggRight = Wave(Frames, n => n >= 14336 && n < 16384 ? 0 : 0.5 * Math.Sqrt(5e-4) * Sine(n, 300, Rate));

        Assert.IsTrue(JudgeStereo(left, right, left, oggRight), "N13");
    }

    /// <summary>
    /// 保証：区間は先頭から2048フレームごとに区切る。OGGだけにある短く小さい雑音（音の直前512フレーム）が、音と同じ2048フレームの区間に入れば、
    /// その区間のエネルギーの差は許容内で、同等である。
    /// 検出する誤り：サンプル単位や1024フレームの区間で比べる誤り（雑音だけの区間（WAVは0）を見る区間として扱い、同等でないと誤判定する）。
    /// </summary>
    [TestMethod]
    public void Judge_SegmentsAreTwoThousandFortyEightFramesLong()
    {
        float[] noise = DuplicateMergeAudioTestFiles.Noise(512, seed: 5);
        float[] wav = Wave(Frames, n => n >= 11264 ? 0.5 * S441(n) : 0);
        float[] ogg = Wave(Frames, n => n >= 11264 ? 0.5 * S441(n) : n >= 10752 ? 0.1 * noise[n - 10752] : 0);

        Assert.IsTrue(JudgeStereo(wav, wav, ogg, ogg), "N14: 雑音が同じ2048フレームの区間の音の中に収まります。");
    }

    /// <summary>
    /// 保証：判定は入力のサンプル配列を書き換えない（フィルタ後の波形を元の配列へ書き戻さない）。
    /// 検出する誤り：その場でのフィルタ処理。
    /// </summary>
    [TestMethod]
    public void Judge_DoesNotModifyTheInputSamples()
    {
        float[] left = Wave(Frames, n => 0.6 * S441(n) + 0.2 * Q(n));
        float[] oggLeft = Wave(Frames, n => 0.6 * S441(n) + 0.2 * C(n));
        var wav = new DuplicateMergeAudioSamples(Rate, 2, Interleave(left, left));
        var ogg = new DuplicateMergeAudioSamples(Rate, 2, Interleave(oggLeft, oggLeft));
        float[] wavBefore = [.. wav.Samples];
        float[] oggBefore = [.. ogg.Samples];

        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(wav, ogg), "N15: 前提：高域だけが異なるステレオは同等です。");
        CollectionAssert.AreEqual(wavBefore, wav.Samples, "N15: WAVのサンプルが変わりました。");
        CollectionAssert.AreEqual(oggBefore, ogg.Samples, "N15: OGGのサンプルが変わりました。");
    }
}
