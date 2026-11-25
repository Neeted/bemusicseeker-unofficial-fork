using System;

namespace BeMusicSeeker.Models;

/// <summary>同等性の判定に使う、チャンネルを交互に並べたPCMです。値はフルスケールを±1とします。</summary>
internal sealed class DuplicateMergeAudioSamples
{
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="channels">チャンネル数。1以上です。</param>
    /// <param name="samples">チャンネルを交互に並べたサンプル。長さはチャンネル数の倍数で、所有権を受け取ります。</param>
    internal DuplicateMergeAudioSamples(int sampleRate, int channels, float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (samples.Length % channels != 0)
        {
            throw new ArgumentException("The interleaved samples must contain whole frames.", nameof(samples));
        }
        SampleRate = sampleRate;
        Channels = channels;
        Samples = samples;
    }

    /// <summary>サンプルレートです。</summary>
    internal int SampleRate { get; }

    /// <summary>チャンネル数です。</summary>
    internal int Channels { get; }

    /// <summary>チャンネルを交互に並べたサンプルです。</summary>
    internal float[] Samples { get; }

    /// <summary>総フレーム数です。</summary>
    internal long FrameCount => Samples.Length / Channels;
}

/// <summary>同名のWAVとOGGの波形が、変換前後の同じ音源とみなせるかを判定します。純粋な計算で、ファイルや音声ランタイムに触れません。</summary>
internal static class DuplicateMergeAudioEquivalence
{
    /// <summary>
    /// 二つの音源が同等かを返します。サンプルレート・チャンネル数・総フレーム数のいずれかが異なるか、チャンネル数が3以上なら同等ではありません。
    /// 全サンプルが0の音源は、両方が全0のときだけ同等です。それ以外は、同じ位置のサンプル同士の正規化相関
    /// <c>Σwo / √(Σw²·Σo²)</c> が下限以上で、かつWAVに対するOGGのゲイン <c>Σwo / Σw²</c> が範囲内のときに同等です。
    /// 位置をずらした照合と周波数領域の比較は行いません。しきい値は <see cref="DuplicateMergeSafetyPolicy"/> が定めます。
    /// </summary>
    /// <param name="wav">WAVの音源。</param>
    /// <param name="ogg">OGGの音源。</param>
    internal static bool Judge(DuplicateMergeAudioSamples wav, DuplicateMergeAudioSamples ogg)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(ogg);
        if (wav.SampleRate != ogg.SampleRate || wav.Channels != ogg.Channels || wav.FrameCount != ogg.FrameCount) { return false; }
        // チャンネル数が3以上の並びは、形式によって異なるため比べない。
        if (wav.Channels >= 3) { return false; }

        float[] first = wav.Samples;
        float[] second = ogg.Samples;
        double product = 0, wavEnergy = 0, oggEnergy = 0;
        for (int index = 0; index < first.Length; index++)
        {
            double w = first[index];
            double o = second[index];
            product += w * o;
            wavEnergy += w * w;
            oggEnergy += o * o;
        }

        // floatを二乗した値は倍精度で桁あふれ・桁落ちしないため、エネルギーが0であることが全サンプルが0であることと同じ。
        if (wavEnergy == 0 || oggEnergy == 0) { return wavEnergy == 0 && oggEnergy == 0; }
        double correlation = product / Math.Sqrt(wavEnergy * oggEnergy);
        double gain = product / wavEnergy;
        return correlation >= DuplicateMergeSafetyPolicy.AudioCorrelationMinimum
            && gain >= DuplicateMergeSafetyPolicy.AudioGainMinimum
            && gain <= DuplicateMergeSafetyPolicy.AudioGainMaximum;
    }
}
