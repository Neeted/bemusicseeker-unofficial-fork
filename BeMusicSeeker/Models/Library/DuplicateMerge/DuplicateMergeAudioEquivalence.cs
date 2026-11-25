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
    /// <summary>4次バターワース低域通過フィルタを構成する、双2次フィルタ2段のQです。</summary>
    private static readonly double[] ButterworthStageQs = [0.54119610, 1.30656296];

    /// <summary>
    /// 二つの音源が同等かを返します。
    /// <para>
    /// サンプルレート・チャンネル数・総フレーム数のいずれかが異なるか、チャンネル数が3以上なら同等ではありません。
    /// 有限でない値（非数・無限大）のサンプルを含む音源は同等ではありません。
    /// 全サンプルが0の音源は、両方が全0のときだけ同等です。
    /// </para>
    /// <para>
    /// それ以外は、チャンネルごとに同じ位置のサンプル同士を比べます。以下、あるチャンネルのWAVのサンプル列を w、OGGのサンプル列を o とし、
    /// エネルギーはサンプルの二乗和とします。
    /// </para>
    /// <para>
    /// 判定するチャンネル：そのチャンネルのエネルギー（<c>Σw²</c> と <c>Σo²</c> の大きい方）が、両音源の全チャンネルの <c>Σw²</c>・<c>Σo²</c> のうち最大の値の
    /// 1万分の1未満のチャンネルは判定しません（どの条件も見ません）。それ以外のチャンネルを判定するチャンネルとし、そのすべてが次の条件をすべて満たすと同等です。
    /// </para>
    /// <list type="number">
    /// <item><description>音量比 <c>√(Σo² / Σw²)</c> が0.85以上1.15以下。<c>Σw²</c> と <c>Σo²</c> の一方だけが0なら満たしません。</description></item>
    /// <item><description>全帯域の正規化相関 <c>Σwo / √(Σw²·Σo²)</c> が0.85以上。</description></item>
    /// <item><description>
    /// 遮断周波数4000Hzの4次バターワース低域通過フィルタを w・o に掛けた列 w′・o′ の正規化相関 <c>Σw′o′ / √(Σw′²·Σo′²)</c> が0.95以上。
    /// WAVの低域のエネルギー <c>Σw′²</c> がそのチャンネルのWAVのエネルギー <c>Σw²</c> の5%未満なら、この条件を見ません。見る場合に <c>Σo′²</c> が0なら満たしません。
    /// サンプルレートが16000Hz以下の音源はフィルタを掛けず、w′ = w、o′ = o とします。
    /// </description></item>
    /// <item><description>
    /// 先頭から2048フレームごとに区切った区間（末尾の端数の区間を含む）ごとに、そのチャンネルのWAVのエネルギー Ew とOGGのエネルギー Eo を求めます。
    /// 基準 P は、判定するチャンネルの全区間について、WAVとOGGの両方を通じて最大の区間エネルギーで、チャンネルによらず一つです。
    /// <c>max(Ew, Eo)</c> が P の1000分の1以上の区間のすべてで、<c>min(Ew, Eo)</c> が <c>max(Ew, Eo)</c> の−12dB（<c>10^(-1.2)</c> 倍）以上。
    /// <c>max(Ew, Eo)</c> が P の1000分の1未満の区間は見ません。
    /// </description></item>
    /// </list>
    /// <para>
    /// 高域の波形を保存しないVorbisでも低域の波形と全体の音量は保たれることを使い、全帯域の相関だけでは同等と判定できない打楽器を拾います。
    /// チャンネルごと・区間ごとに比べることで、片チャンネルや一部の区間が欠けたOGGを同等と判定しないようにします（判定しないチャンネルと見ない区間の差は問いません）。
    /// 位置をずらした照合とチャンネルの入替えの吸収は行いません。入力のサンプルは書き換えず、フィルタ後の波形も保持しません。
    /// しきい値と定数は <see cref="DuplicateMergeSafetyPolicy"/> が定めます。
    /// </para>
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

        int channels = wav.Channels;
        int frames = (int)wav.FrameCount;
        bool filtered = wav.SampleRate > DuplicateMergeSafetyPolicy.AudioLowPassSampleRateMaximum;
        var statistics = new ChannelStatistics[channels];
        double wavTotal = 0, oggTotal = 0, largestChannelEnergy = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            ChannelStatistics current = Measure(wav.Samples, ogg.Samples, channel, channels, frames, wav.SampleRate, filtered);
            statistics[channel] = current;
            wavTotal += current.WavEnergy;
            oggTotal += current.OggEnergy;
            largestChannelEnergy = Math.Max(largestChannelEnergy, Math.Max(current.WavEnergy, current.OggEnergy));
        }

        // 有限なfloatの二乗和は倍精度で桁あふれしないため、合計が有限でないのは非数・無限大のサンプルを含む場合だけ。
        // 非数があると最大のエネルギーも非数になり、判定するチャンネルがなくなって同等と誤るため、ごみ箱へ送る判定の前に同等でないとする。
        if (!double.IsFinite(wavTotal) || !double.IsFinite(oggTotal)) { return false; }

        // floatを二乗した値は倍精度で桁あふれ・桁落ちしないため、エネルギーが0であることが全サンプルが0であることと同じ。
        if (wavTotal == 0 || oggTotal == 0) { return wavTotal == 0 && oggTotal == 0; }

        double segmentPeak = 0;
        foreach (ChannelStatistics current in statistics)
        {
            if (!IsJudged(current, largestChannelEnergy)) { continue; }
            foreach (double energy in current.WavSegments) { segmentPeak = Math.Max(segmentPeak, energy); }
            foreach (double energy in current.OggSegments) { segmentPeak = Math.Max(segmentPeak, energy); }
        }

        foreach (ChannelStatistics current in statistics)
        {
            if (IsJudged(current, largestChannelEnergy) && !IsEquivalentChannel(current, segmentPeak)) { return false; }
        }
        return true;
    }

    private static bool IsJudged(ChannelStatistics channel, double largestChannelEnergy)
        => Math.Max(channel.WavEnergy, channel.OggEnergy) * DuplicateMergeSafetyPolicy.AudioChannelEnergyFloorDivisor >= largestChannelEnergy;

    private static bool IsEquivalentChannel(ChannelStatistics channel, double segmentPeak)
    {
        // 判定するチャンネルは両方が0になることがなく、一方だけが0なら音量比を満たさない。
        if (channel.WavEnergy == 0 || channel.OggEnergy == 0) { return false; }
        double ratio = Math.Sqrt(channel.OggEnergy / channel.WavEnergy);
        if (ratio < DuplicateMergeSafetyPolicy.AudioVolumeRatioMinimum || ratio > DuplicateMergeSafetyPolicy.AudioVolumeRatioMaximum) { return false; }

        double fullCorrelation = channel.Product / Math.Sqrt(channel.WavEnergy * channel.OggEnergy);
        if (!(fullCorrelation >= DuplicateMergeSafetyPolicy.AudioFullBandCorrelationMinimum)) { return false; }

        // 低域にほとんど音がないチャンネルでは、低域の相関が雑音で決まるため見ない。
        if (!(channel.WavLowEnergy < channel.WavEnergy * DuplicateMergeSafetyPolicy.AudioLowBandEnergyRatioMinimum))
        {
            if (channel.OggLowEnergy == 0) { return false; }
            double lowCorrelation = channel.LowProduct / Math.Sqrt(channel.WavLowEnergy * channel.OggLowEnergy);
            if (!(lowCorrelation >= DuplicateMergeSafetyPolicy.AudioLowBandCorrelationMinimum)) { return false; }
        }

        for (int segment = 0; segment < channel.WavSegments.Length; segment++)
        {
            double wavEnergy = channel.WavSegments[segment];
            double oggEnergy = channel.OggSegments[segment];
            double larger = Math.Max(wavEnergy, oggEnergy);
            if (larger * DuplicateMergeSafetyPolicy.AudioSegmentEnergyFloorDivisor < segmentPeak) { continue; }
            if (Math.Min(wavEnergy, oggEnergy) < larger * DuplicateMergeSafetyPolicy.AudioSegmentEnergyRatioMinimum) { return false; }
        }
        return true;
    }

    /// <summary>一つのチャンネルについて、全帯域・低域・区間ごとのエネルギーと内積を一度の走査で求めます。フィルタ後の波形は保持しません。</summary>
    private static ChannelStatistics Measure(float[] wavSamples, float[] oggSamples, int channel, int channels, int frames, int sampleRate, bool filtered)
    {
        int segmentLength = DuplicateMergeSafetyPolicy.AudioSegmentFrames;
        var result = new ChannelStatistics((frames + segmentLength - 1) / segmentLength);
        Biquad wavFirst = default, wavSecond = default, oggFirst = default, oggSecond = default;
        if (filtered)
        {
            wavFirst = Biquad.CreateLowPass(sampleRate, ButterworthStageQs[0]);
            wavSecond = Biquad.CreateLowPass(sampleRate, ButterworthStageQs[1]);
            oggFirst = wavFirst;
            oggSecond = wavSecond;
        }

        double product = 0, wavEnergy = 0, oggEnergy = 0;
        double lowProduct = 0, wavLowEnergy = 0, oggLowEnergy = 0;
        int index = channel;
        for (int frame = 0; frame < frames; frame++, index += channels)
        {
            double w = wavSamples[index];
            double o = oggSamples[index];
            double wEnergy = w * w;
            double oEnergy = o * o;
            product += w * o;
            wavEnergy += wEnergy;
            oggEnergy += oEnergy;
            result.WavSegments[frame / segmentLength] += wEnergy;
            result.OggSegments[frame / segmentLength] += oEnergy;
            if (filtered)
            {
                double lowWav = wavSecond.Step(wavFirst.Step(w));
                double lowOgg = oggSecond.Step(oggFirst.Step(o));
                lowProduct += lowWav * lowOgg;
                wavLowEnergy += lowWav * lowWav;
                oggLowEnergy += lowOgg * lowOgg;
            }
        }

        result.Product = product;
        result.WavEnergy = wavEnergy;
        result.OggEnergy = oggEnergy;
        // フィルタを掛けない音源では、低域の波形を全帯域の波形そのものとして扱う。
        result.LowProduct = filtered ? lowProduct : product;
        result.WavLowEnergy = filtered ? wavLowEnergy : wavEnergy;
        result.OggLowEnergy = filtered ? oggLowEnergy : oggEnergy;
        return result;
    }

    /// <summary>一つのチャンネルの全帯域・低域の総和と、区間ごとのエネルギーです。</summary>
    private sealed class ChannelStatistics(int segmentCount)
    {
        internal double Product;
        internal double WavEnergy;
        internal double OggEnergy;
        internal double LowProduct;
        internal double WavLowEnergy;
        internal double OggLowEnergy;
        internal double[] WavSegments { get; } = new double[segmentCount];
        internal double[] OggSegments { get; } = new double[segmentCount];
    }

    /// <summary>直接形Iの双2次低域通過フィルタです。状態は0から始め、コピーすると状態も複製されます。</summary>
    private struct Biquad
    {
        private double b0, b1, a1, a2;
        private double x1, x2, y1, y2;

        internal static Biquad CreateLowPass(int sampleRate, double q)
        {
            double omega = 2 * Math.PI * DuplicateMergeSafetyPolicy.AudioLowPassCutoffHertz / sampleRate;
            double cosine = Math.Cos(omega);
            double alpha = Math.Sin(omega) / (2 * q);
            double a0 = 1 + alpha;
            return new Biquad
            {
                b0 = (1 - cosine) / 2 / a0,
                b1 = (1 - cosine) / a0,
                a1 = -2 * cosine / a0,
                a2 = (1 - alpha) / a0
            };
        }

        internal double Step(double x)
        {
            // b2 = b0
            double y = b0 * x + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;
            return y;
        }
    }
}
