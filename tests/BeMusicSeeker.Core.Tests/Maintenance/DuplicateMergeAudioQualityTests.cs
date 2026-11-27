using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 音質の目安（<see cref="DuplicateMergeAudioQuality"/>）を、測定値を直接渡して境界で確認します。目安は測定値だけから導き、未測定の値がからむ目安は成立しません。
/// 平均ビットレートは、ファイルサイズ・フレーム数・サンプルレートから式（サイズ×8÷秒÷1000）で得ます。
/// </summary>
[TestClass]
public sealed class DuplicateMergeAudioQualityTests
{
    private static DuplicateMergeWavInfo Wav(int bits = 16, int rate = 44100, int channels = 2)
        => new(1, bits, rate, channels, 44100, 1000);

    /// <summary>1チャンネル数・長さ・平均ビットレート（kbps）を指定したOGGです。ファイルサイズは式の逆算で求めます。</summary>
    private static DuplicateMergeOggInfo Ogg(int channels, long frames, double kbps, int rate = 44100)
        => new(rate, channels, frames, (long)Math.Round(kbps * 1000 / 8 * frames / rate));

    /// <summary>
    /// 保証（WAVはOGGと同じチャンネル数）：OGGが低ビットレートは、OGGの長さが1秒以上（1秒ちょうどを含む）のときだけで、ステレオは96kbps未満（ちょうどは成立しない）、モノラルは48kbps未満（ちょうどは成立しない）。
    /// 3チャンネル以上は対象外。0.999秒は成立しない。
    /// 検出する誤り：1秒の境界の開閉の誤り、しきい値の境界の誤り、チャンネル数ごとのしきい値の取違え、3チャンネル以上の誤成立。
    /// </summary>
    [TestMethod]
    public void Evaluate_OggLowBitrate_UsesTheLengthAndChannelThresholds()
    {
        DuplicateMergeAudioQualityHints Hints(DuplicateMergeOggInfo ogg) => DuplicateMergeAudioQuality.Evaluate(Wav(channels: ogg.Channels ?? 2), ogg);

        Assert.AreEqual(DuplicateMergeAudioQualityHints.OggLowBitrate, Hints(Ogg(2, 44100, 95.9)), "ステレオ95.9kbps・1秒ちょうどです。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, Hints(Ogg(2, 44100, 96)), "ステレオ96kbpsちょうどです。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.OggLowBitrate, Hints(Ogg(1, 44100, 47.9)), "モノラル47.9kbpsです。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, Hints(Ogg(1, 44100, 48)), "モノラル48kbpsちょうどです。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, Hints(Ogg(2, 44099, 50)), "0.999秒（44099フレーム）です。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.OggLowBitrate, Hints(Ogg(2, 441000, 50)), "10秒です。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, Hints(Ogg(3, 44100, 10)), "3チャンネルは対象外です。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, Hints(Ogg(2, 0, 0)), "0フレームはビットレートが未測定です。");
    }

    /// <summary>
    /// 保証：WAVが8ビットは8ビットだけ（16ビットは成立しない）。WAVが低サンプルレートは44100Hz未満だけ（44099Hzは成立、44100Hzは成立しない）。
    /// OGGのサンプルレートがWAVより低いは、WAVより低いときだけ（同じは成立しない）。
    /// 検出する誤り：ビット深度・サンプルレートの境界の誤り、比較の向きの逆転。
    /// </summary>
    [TestMethod]
    public void Evaluate_BitDepthAndSampleRates_UseTheirBoundaries()
    {
        DuplicateMergeOggInfo ogg = Ogg(2, 44100, 128);

        Assert.AreEqual(DuplicateMergeAudioQualityHints.Wav8Bit, DuplicateMergeAudioQuality.Evaluate(Wav(bits: 8), ogg));
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, DuplicateMergeAudioQuality.Evaluate(Wav(bits: 16), ogg));
        Assert.AreEqual(
            DuplicateMergeAudioQualityHints.WavLowSampleRate,
            DuplicateMergeAudioQuality.Evaluate(Wav(rate: 44099), Ogg(2, 44100, 128, rate: 44099)),
            "WAV 44099Hz（OGGも同じレート）です。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, DuplicateMergeAudioQuality.Evaluate(Wav(rate: 44100), ogg), "WAV 44100Hzは低サンプルレートに当たりません。");
        Assert.AreEqual(
            DuplicateMergeAudioQualityHints.OggLowerSampleRate,
            DuplicateMergeAudioQuality.Evaluate(Wav(rate: 48000), ogg), "WAV 48000Hz・OGG 44100Hzです。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, DuplicateMergeAudioQuality.Evaluate(Wav(rate: 44100), Ogg(2, 44100, 128, rate: 44100)), "同じレートです。");
    }

    /// <summary>
    /// 保証：WAVだけモノラルは「WAVが1チャンネルでOGGが2チャンネル以上」、OGGだけモノラルは「OGGが1チャンネルでWAVが2チャンネル以上」。両方モノラル・両方ステレオは成立しない。
    /// 測定できなかった値（復号できないファイルの未測定のレート・チャンネル数・フレーム数）がからむ目安は成立しない。複数の目安は同時に成立し、和集合で表す。
    /// 検出する誤り：片側だけモノラルの向きの取違え、未測定を0・既定値として扱う誤成立、複数成立の取りこぼし。
    /// </summary>
    [TestMethod]
    public void Evaluate_MonoAndUnmeasuredValues_AndSeveralHintsAtOnce()
    {
        Assert.AreEqual(DuplicateMergeAudioQualityHints.WavOnlyMono, DuplicateMergeAudioQuality.Evaluate(Wav(channels: 1), Ogg(2, 44100, 128)));
        Assert.AreEqual(DuplicateMergeAudioQualityHints.OggOnlyMono, DuplicateMergeAudioQuality.Evaluate(Wav(channels: 2), Ogg(1, 44100, 64)));
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, DuplicateMergeAudioQuality.Evaluate(Wav(channels: 1), Ogg(1, 44100, 64)), "両方モノラルです。");
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, DuplicateMergeAudioQuality.Evaluate(Wav(channels: 2), Ogg(2, 44100, 128)), "両方ステレオです。");

        var undecodableWav = new DuplicateMergeWavInfo(2, null, null, null, null, 1000);
        var undecodableOgg = new DuplicateMergeOggInfo(null, null, null, 1000);
        Assert.AreEqual(DuplicateMergeAudioQualityHints.None, DuplicateMergeAudioQuality.Evaluate(undecodableWav, undecodableOgg), "すべて未測定です。");
        Assert.AreEqual(
            DuplicateMergeAudioQualityHints.Wav8Bit,
            DuplicateMergeAudioQuality.Evaluate(new DuplicateMergeWavInfo(1, 8, null, null, null, 1000), undecodableOgg),
            "WAVのビット深度だけが測定済みです。");

        DuplicateMergeAudioQualityHints all = DuplicateMergeAudioQuality.Evaluate(
            new DuplicateMergeWavInfo(1, 8, 22050, 1, 22050, 1000), Ogg(2, 22050, 20, rate: 11025));
        Assert.AreEqual(
            DuplicateMergeAudioQualityHints.OggLowBitrate | DuplicateMergeAudioQualityHints.Wav8Bit | DuplicateMergeAudioQualityHints.WavLowSampleRate
            | DuplicateMergeAudioQualityHints.OggLowerSampleRate | DuplicateMergeAudioQualityHints.WavOnlyMono,
            all,
            "OGG 11025Hz・ステレオ・20kbps（2秒）と、WAV 22050Hz・8ビット・モノラルです。");
    }

    /// <summary>
    /// 保証：OGGの平均ビットレートは「ファイルサイズ×8÷長さ（秒）÷1000」で、フレーム数が0または未測定なら未測定。長さはフレーム数÷サンプルレート（未測定なら未測定）。
    /// 検出する誤り：単位（バイトとビット、kbpsの1000と1024）の誤り、0フレームでの0除算。
    /// </summary>
    [TestMethod]
    public void OggInfo_AverageBitrateAndSeconds_FollowTheFormula()
    {
        var ogg = new DuplicateMergeOggInfo(44100, 2, 88200, 32000);

        Assert.AreEqual(2.0, ogg.Seconds!.Value, 1e-9);
        Assert.AreEqual(128.0, ogg.AverageKbps!.Value, 1e-9, "32000バイト×8÷2秒÷1000です。");
        Assert.IsNull(new DuplicateMergeOggInfo(44100, 2, 0, 32000).AverageKbps);
        Assert.IsNull(new DuplicateMergeOggInfo(44100, 2, null, 32000).AverageKbps);
        Assert.IsNull(new DuplicateMergeOggInfo(null, 2, 88200, 32000).Seconds);
        Assert.AreEqual(0.5, new DuplicateMergeWavInfo(1, 16, 8000, 1, 4000, 8044).Seconds!.Value, 1e-9);
    }
}
