using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

/// <summary>
/// WAVとOGGの同等性の判定（純粋な計算）を、仕様の手順1〜3の境界値で確認します。
/// 値は256分の整数で表し、総和・相関・ゲインが倍精度で誤差なく仕様の式どおりになる入力で、しきい値ちょうどの扱いを固定します。
/// </summary>
[TestClass]
public sealed class DuplicateMergeAudioEquivalenceTests
{
    private const int Rate = 44100;

    private static DuplicateMergeAudioSamples Mono(params double[] over256) => Make(Rate, 1, over256);

    private static DuplicateMergeAudioSamples Make(int rate, int channels, double[] over256)
        => new(rate, channels, [.. over256.Select(value => (float)(value / 256))]);

    private static double[] Repeat(double[] pattern, int times) => [.. Enumerable.Range(0, times).SelectMany(_ => pattern)];

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
    /// 保証：正規化相関は0.95ちょうどで同等、わずかに下回れば同等でない（ゲインは0.95で範囲内）。
    /// 検出する誤り：しきい値の「以上」を「超える」にする誤り、しきい値の取り違え。
    /// </summary>
    [TestMethod]
    public void Judge_CorrelationThreshold_IsInclusiveAt095()
    {
        double[] wav = [200, 0, 0, 0, 0];

        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(190, 60, 10, 10, 10)), "A6: 相関がちょうど0.95（ゲイン0.95）です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(190, 60, 10, 10, 11)), "A7: 末尾を11にして相関を0.95未満にします。");
    }

    /// <summary>
    /// 保証：ゲインは0.85以上1.15以下で同等で、範囲外は（相関が1でも）同等でない。符号が反転した波形（相関−1）も同等でない。
    /// 検出する誤り：ゲインの上下限の取り違え・境界の開閉の誤り、位相反転の見逃し。
    /// </summary>
    [TestMethod]
    public void Judge_GainThresholds_AreInclusiveFrom085To115()
    {
        double[] wav = Repeat([200, -200], 4);

        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([170, -170], 4))), "A8: ゲインがちょうど0.85です。");
        Assert.IsTrue(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([230, -230], 4))), "A8: ゲインがちょうど1.15です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([169, -169], 4))), "A9: ゲインが0.85未満です。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([231, -231], 4))), "A9: ゲインが1.15を超えます。");
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(wav), Mono(Repeat([-200, 200], 4))), "A9: 符号が反転した波形です。");
    }

    /// <summary>
    /// 保証：ゲインはWAVへのOGGの射影 Σwo / Σw² で、相関だけが範囲内でもゲインが0.85未満なら同等でない（OGGからWAVへの比ではない）。
    /// 検出する誤り：ゲインの分母をΣo²にする取り違え。
    /// </summary>
    [TestMethod]
    public void Judge_GainIsTheProjectionOfOggOntoWav()
    {
        // Σwo=200·168=33600、Σw²=40000、Σo²=168²+48²=30528。相関は約0.96、WAVに対するゲインは0.84。
        // 分母をΣo²にした誤りでは、33600/30528=1.10 となり範囲内に入ってしまう。
        Assert.IsFalse(DuplicateMergeAudioEquivalence.Judge(Mono(200, 0, 0, 0, 0), Mono(168, 48, 0, 0, 0)), "A10");
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
}
