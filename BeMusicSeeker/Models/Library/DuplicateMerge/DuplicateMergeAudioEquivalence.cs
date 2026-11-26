using System;
using System.Runtime.Intrinsics;

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
    /// 同名のOGGが、同名のWAVからの通常のVorbis変換（libvorbisのVBR品質4、約128kbps相当）として説明できるかを返します。
    /// Vorbisが保存する情報（低域の波形、区間ごとの音量）だけを根拠にします。
    /// <list type="number">
    /// <item><description>
    /// 形式：チャンネル数が異なるか3以上なら同等ではありません。有限でない値（非数・無限大）を含む音源も同等ではありません。
    /// この検査は、比べる範囲への切詰めと変換の前の全サンプルに行います。
    /// </description></item>
    /// <item><description>
    /// 比べる範囲：サンプルレートが同じでフレーム数が同じなら全体を比べます。サンプルレートが同じ44100Hzまたは48000Hzで、OGGのフレーム数からWAVのフレーム数を引いた値が
    /// −128以上1152以下なら、長い側の超過分がすべて0（−0.0を含む）のときだけ、先頭から短い側のフレーム数を比べます。
    /// WAVが48000Hz・OGGが44100Hzで、<c>|147·Nw − 160·No|</c> が294以下なら、OGGを48000Hzへ変換した列をWAVのフレーム数で比べます。それ以外は同等ではありません。
    /// </description></item>
    /// <item><description>無音：比べる範囲が両方とも全サンプル0なら同等、一方だけなら同等ではありません（0フレームは両方とも0）。</description></item>
    /// <item><description>
    /// 波形と音量：次の四つの条件をすべて満たすと同等です。（1）すべてのチャンネルのすべての区間で、1000Hz以下と4000Hz以下の低域の波形の誤差エネルギーが、
    /// 区間の低域・全帯域のエネルギーと全区間の最大値 <c>P</c> から決まる上限以下。（2）両チャンネルを合算した全帯域の正規化相関が0.5以上。
    /// （3）個別に見るチャンネル（<c>Σw²</c> と <c>Σo²</c> の大きい方が最大値 <c>E</c> の30分の1以上）の全帯域の音量比が0.63以上1.15以下で、
    /// <c>Σw₄²</c> が <c>E</c> の1000分の1以上のチャンネルは4000Hz以下の音量比が0.84以上1.07以下。
    /// （4）個別に見るチャンネルのすべての区間（2048フレーム。末尾の端数を含む）で、エネルギーの大きい方が <c>P</c> の100分の1以上なら、小さい方が大きい方の−12dB以上。
    /// </description></item>
    /// </list>
    /// 位置をずらした照合、長さの伸縮、チャンネルの入替えの吸収、周波数領域の比較は行いません。入力のサンプルは書き換えず、フィルタ後の波形を全長で保持しません。
    /// 手順の詳細としきい値の根拠は、仕様の「WAVとOGGの同等性検査」と、定数を定める <see cref="DuplicateMergeSafetyPolicy"/> にあります。
    /// </summary>
    /// <param name="wav">WAVの音源。</param>
    /// <param name="ogg">OGGの音源。</param>
    internal static bool Judge(DuplicateMergeAudioSamples wav, DuplicateMergeAudioSamples ogg)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(ogg);
        int channels = wav.Channels;
        if (channels != ogg.Channels) { return false; }
        // チャンネル数が3以上の並びは、形式によって異なるため比べない。
        if (channels >= 3) { return false; }
        long wavFrames = wav.FrameCount, oggFrames = ogg.FrameCount;
        int frames;
        double[] converted = null;
        if (wav.SampleRate == ogg.SampleRate)
        {
            if (wavFrames != oggFrames)
            {
                if (wav.SampleRate != DuplicateMergeSafetyPolicy.AudioLengthToleranceSampleRateFirst
                    && wav.SampleRate != DuplicateMergeSafetyPolicy.AudioLengthToleranceSampleRateSecond) { return false; }
                long difference = oggFrames - wavFrames;
                if (difference < -DuplicateMergeSafetyPolicy.AudioWavLongerExcessMaximumFrames
                    || difference > DuplicateMergeSafetyPolicy.AudioOggLongerExcessMaximumFrames) { return false; }
                // 超過分は「0でない」を検査する。非数・無限大は 0 と等しくないため、ここで同等でないとなる。
                float[] longer = wavFrames > oggFrames ? wav.Samples : ogg.Samples;
                if (!AllZero(longer.AsSpan((int)(Math.Min(wavFrames, oggFrames) * channels)))) { return false; }
            }
            frames = (int)Math.Min(wavFrames, oggFrames);
            // 比べる範囲の有限でない値は、走査で得る全帯域の二乗和が有限かどうかで検出する（下の統計の後。独立した全サンプルの走査をしない）。
        }
        else if (wav.SampleRate == DuplicateMergeSafetyPolicy.AudioResampleWavSampleRate
            && ogg.SampleRate == DuplicateMergeSafetyPolicy.AudioResampleOggSampleRate
            && Math.Abs(DuplicateMergeSafetyPolicy.AudioResampleInputUnits * wavFrames - DuplicateMergeSafetyPolicy.AudioResampleOutputUnits * oggFrames)
                <= DuplicateMergeSafetyPolicy.AudioResampleLengthToleranceUnits)
        {
            // 変換は有限でない値を広げる・消すため、変換の前の全サンプルを検査する。
            if (!AllFinite(wav.Samples) || !AllFinite(ogg.Samples)) { return false; }
            frames = (int)wavFrames;
            converted = DuplicateMergeAudioResampler.Convert(ogg.Samples, channels, frames);
        }
        else
        {
            return false;
        }

        int segmentLength = DuplicateMergeSafetyPolicy.AudioSegmentFrames;
        int segmentCount = (frames + segmentLength - 1) / segmentLength;
        var statistics = new SegmentStatistics[channels][];
        double[] wideEnergy = new double[segmentCount];
        double[] narrowEnergy = new double[segmentCount];
        double[] fullEnergy = new double[segmentCount];
        double wavTotal = 0, oggTotal = 0, productTotal = 0, largest = 0;
        double[] wavChannel = new double[channels];
        double[] oggChannel = new double[channels];
        double[] wavWide = new double[channels];
        double[] oggWide = new double[channels];
        for (int channel = 0; channel < channels; channel++)
        {
            SegmentStatistics[] current = Measure(wav.Samples, ogg.Samples, converted, channel, channels, frames, wav.SampleRate);
            statistics[channel] = current;
            for (int segment = 0; segment < current.Length; segment++)
            {
                ref readonly SegmentStatistics item = ref current[segment];
                wavChannel[channel] += item.WavEnergy;
                oggChannel[channel] += item.OggEnergy;
                wavWide[channel] += item.WavWideEnergy;
                oggWide[channel] += item.OggWideEnergy;
                productTotal += item.Product;
                fullEnergy[segment] = Math.Max(fullEnergy[segment], Math.Max(item.WavEnergy, item.OggEnergy));
                wideEnergy[segment] = Math.Max(wideEnergy[segment], Math.Max(item.WavWideEnergy, item.OggWideEnergy));
                narrowEnergy[segment] = Math.Max(narrowEnergy[segment], Math.Max(item.WavNarrowEnergy, item.OggNarrowEnergy));
            }
            wavTotal += wavChannel[channel];
            oggTotal += oggChannel[channel];
            largest = Math.Max(largest, Math.Max(wavChannel[channel], oggChannel[channel]));
        }

        // 有限なfloatの二乗の和は倍精度で桁あふれしないため、和が有限でないのは非数・無限大のサンプルを含む場合だけ（非数は和へ伝わる）。
        // 無音の判定と各条件の比較より前に、同等でないとする。
        if (!double.IsFinite(wavTotal) || !double.IsFinite(oggTotal)) { return false; }

        // 有限なfloatの二乗は倍精度で桁あふれ・桁落ちしないため、エネルギーが0であることが全サンプルが0であることと同じ。
        if (wavTotal == 0 || oggTotal == 0) { return wavTotal == 0 && oggTotal == 0; }

        double peak = 0;
        foreach (double energy in fullEnergy) { peak = Math.Max(peak, energy); }

        // 低域の波形。弱いチャンネルも含めて見る。
        foreach (SegmentStatistics[] channelSegments in statistics)
        {
            for (int segment = 0; segment < channelSegments.Length; segment++)
            {
                ref readonly SegmentStatistics item = ref channelSegments[segment];
                double narrowLimit = DuplicateMergeSafetyPolicy.AudioNarrowBandErrorLowCoefficient * narrowEnergy[segment]
                    + DuplicateMergeSafetyPolicy.AudioNarrowBandErrorFullCoefficient * fullEnergy[segment]
                    + DuplicateMergeSafetyPolicy.AudioNarrowBandErrorPeakCoefficient * peak;
                double wideLimit = DuplicateMergeSafetyPolicy.AudioWideBandErrorLowCoefficient * wideEnergy[segment]
                    + DuplicateMergeSafetyPolicy.AudioWideBandErrorPeakCoefficient * peak;
                if (!(item.NarrowError <= narrowLimit) || !(item.WideError <= wideLimit)) { return false; }
            }
        }

        // 全帯域の相関（両チャンネルを合算）。
        double correlation = productTotal / Math.Sqrt(wavTotal * oggTotal);
        if (!(correlation >= DuplicateMergeSafetyPolicy.AudioFullBandCorrelationMinimum)) { return false; }

        for (int channel = 0; channel < channels; channel++)
        {
            double wavEnergy = wavChannel[channel], oggEnergy = oggChannel[channel];
            if (!(Math.Max(wavEnergy, oggEnergy) * DuplicateMergeSafetyPolicy.AudioIndividualChannelEnergyDivisor >= largest)) { continue; }

            // 個別に見るチャンネルは両方が0になることがなく、一方だけが0なら音量比を満たさない。
            if (wavEnergy == 0 || oggEnergy == 0) { return false; }
            double ratio = Math.Sqrt(oggEnergy / wavEnergy);
            if (!(ratio >= DuplicateMergeSafetyPolicy.AudioVolumeRatioMinimum && ratio <= DuplicateMergeSafetyPolicy.AudioVolumeRatioMaximum)) { return false; }

            if (wavWide[channel] * DuplicateMergeSafetyPolicy.AudioLowBandVolumeEnergyDivisor >= largest)
            {
                double wideRatio = Math.Sqrt(oggWide[channel] / wavWide[channel]);
                if (!(wideRatio >= DuplicateMergeSafetyPolicy.AudioLowBandVolumeRatioMinimum
                    && wideRatio <= DuplicateMergeSafetyPolicy.AudioLowBandVolumeRatioMaximum)) { return false; }
            }

            foreach (SegmentStatistics item in statistics[channel])
            {
                double larger = Math.Max(item.WavEnergy, item.OggEnergy);
                if (larger * DuplicateMergeSafetyPolicy.AudioSegmentEnergyFloorDivisor < peak) { continue; }
                if (!(Math.Min(item.WavEnergy, item.OggEnergy) >= larger * DuplicateMergeSafetyPolicy.AudioSegmentEnergyRatioMinimum)) { return false; }
            }
        }
        return true;
    }

    private static bool AllFinite(float[] samples)
    {
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample)) { return false; }
        }
        return true;
    }

    private static bool AllZero(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            if (sample != 0) { return false; }
        }
        return true;
    }

    /// <summary>
    /// 一つのチャンネルについて、区間ごとの全帯域・4000Hz以下・1000Hz以下の統計を、先頭から一度の走査で求めます。フィルタ後の波形は保持しません。
    /// サンプルレートが遮断周波数の4倍以下の列には、フィルタを掛けず元の列を使います。
    /// </summary>
    private static SegmentStatistics[] Measure(
        float[] wavSamples, float[] oggSamples, double[] oggConverted, int channel, int channels, int frames, int sampleRate)
    {
        int segmentLength = DuplicateMergeSafetyPolicy.AudioSegmentFrames;
        var result = new SegmentStatistics[(frames + segmentLength - 1) / segmentLength];
        bool wideFiltered = sampleRate > DuplicateMergeSafetyPolicy.AudioLowPassSampleRateMultiple * DuplicateMergeSafetyPolicy.AudioLowPassWideCutoffHertz;
        bool narrowFiltered = sampleRate > DuplicateMergeSafetyPolicy.AudioLowPassSampleRateMultiple * DuplicateMergeSafetyPolicy.AudioLowPassNarrowCutoffHertz;
        var wavWide = Cascade.Create(sampleRate, DuplicateMergeSafetyPolicy.AudioLowPassWideCutoffHertz);
        Cascade oggWide = wavWide;
        var wavNarrow = Cascade.Create(sampleRate, DuplicateMergeSafetyPolicy.AudioLowPassNarrowCutoffHertz);
        Cascade oggNarrow = wavNarrow;

        if (wideFiltered && narrowFiltered)
        {
            MeasureFiltered(wavSamples, oggSamples, oggConverted, channel, channels, frames, sampleRate, result);
            return result;
        }

        for (int segment = 0; segment < result.Length; segment++)
        {
            int start = segment * segmentLength;
            int end = Math.Min(frames, start + segmentLength);
            var item = new SegmentStatistics();
            int index = start * channels + channel;
            for (int frame = start; frame < end; frame++, index += channels)
            {
                double w = wavSamples[index];
                double o = oggConverted != null ? oggConverted[index] : oggSamples[index];
                item.Product += w * o;
                item.WavEnergy += w * w;
                item.OggEnergy += o * o;

                double w4 = w, o4 = o;
                if (wideFiltered)
                {
                    w4 = wavWide.Step(w);
                    o4 = oggWide.Step(o);
                }
                double w1 = w, o1 = o;
                if (narrowFiltered)
                {
                    w1 = wavNarrow.Step(w);
                    o1 = oggNarrow.Step(o);
                }
                item.WavWideEnergy += w4 * w4;
                item.OggWideEnergy += o4 * o4;
                item.WideError += (o4 - w4) * (o4 - w4);
                item.WavNarrowEnergy += w1 * w1;
                item.OggNarrowEnergy += o1 * o1;
                item.NarrowError += (o1 - w1) * (o1 - w1);
            }
            result[segment] = item;
        }
        return result;
    }

    /// <summary>
    /// 両方の低域通過フィルタを掛ける場合の <see cref="Measure"/> です。WAVの4000Hz・OGGの4000Hz・WAVの1000Hz・OGGの1000Hz の四つの列を一つのベクトルの
    /// レーンに載せて、双2次フィルタを進めます。各レーンの演算の順序はスカラーの <see cref="Biquad.Step"/> と同じで、結果はスカラーの実装と一致します（積和の融合はしません）。
    /// </summary>
    private static void MeasureFiltered(
        float[] wavSamples, float[] oggSamples, double[] oggConverted, int channel, int channels, int frames, int sampleRate, SegmentStatistics[] result)
    {
        int segmentLength = DuplicateMergeSafetyPolicy.AudioSegmentFrames;
        var first = VectorStage.Create(
            Biquad.CreateLowPass(sampleRate, DuplicateMergeSafetyPolicy.AudioLowPassWideCutoffHertz, DuplicateMergeSafetyPolicy.AudioButterworthFirstStageQ),
            Biquad.CreateLowPass(sampleRate, DuplicateMergeSafetyPolicy.AudioLowPassNarrowCutoffHertz, DuplicateMergeSafetyPolicy.AudioButterworthFirstStageQ));
        var second = VectorStage.Create(
            Biquad.CreateLowPass(sampleRate, DuplicateMergeSafetyPolicy.AudioLowPassWideCutoffHertz, DuplicateMergeSafetyPolicy.AudioButterworthSecondStageQ),
            Biquad.CreateLowPass(sampleRate, DuplicateMergeSafetyPolicy.AudioLowPassNarrowCutoffHertz, DuplicateMergeSafetyPolicy.AudioButterworthSecondStageQ));

        for (int segment = 0; segment < result.Length; segment++)
        {
            int start = segment * segmentLength;
            int end = Math.Min(frames, start + segmentLength);
            double product = 0, wavEnergy = 0, oggEnergy = 0;
            Vector256<double> energies = Vector256<double>.Zero, errors = Vector256<double>.Zero;
            int index = start * channels + channel;
            for (int frame = start; frame < end; frame++, index += channels)
            {
                double w = wavSamples[index];
                double o = oggConverted != null ? oggConverted[index] : oggSamples[index];
                product += w * o;
                wavEnergy += w * w;
                oggEnergy += o * o;

                Vector256<double> filtered = second.Step(first.Step(Vector256.Create(w, o, w, o)));
                energies += filtered * filtered;
                // 対の入替えとの差。レーン0・2が o − w（レーン1・3は符号だけが逆で、二乗は同じ）。
                Vector256<double> difference = Vector256.Shuffle(filtered, Vector256.Create(1L, 0L, 3L, 2L)) - filtered;
                errors += difference * difference;
            }
            result[segment] = new SegmentStatistics
            {
                Product = product,
                WavEnergy = wavEnergy,
                OggEnergy = oggEnergy,
                WavWideEnergy = energies.GetElement(0),
                OggWideEnergy = energies.GetElement(1),
                WideError = errors.GetElement(0),
                WavNarrowEnergy = energies.GetElement(2),
                OggNarrowEnergy = energies.GetElement(3),
                NarrowError = errors.GetElement(2)
            };
        }
    }

    /// <summary>双2次フィルタ1段を、四つの列（レーン0・1が4000Hz、2・3が1000Hz）へ同時に掛けます。状態は0から始めます。</summary>
    private struct VectorStage
    {
        private Vector256<double> b0, b1, a1, a2;
        private Vector256<double> x1, x2, y1, y2;

        internal static VectorStage Create(Biquad wide, Biquad narrow) => new()
        {
            b0 = Vector256.Create(wide.B0, wide.B0, narrow.B0, narrow.B0),
            b1 = Vector256.Create(wide.B1, wide.B1, narrow.B1, narrow.B1),
            a1 = Vector256.Create(wide.A1, wide.A1, narrow.A1, narrow.A1),
            a2 = Vector256.Create(wide.A2, wide.A2, narrow.A2, narrow.A2)
        };

        internal Vector256<double> Step(Vector256<double> x)
        {
            // b2 = b0。スカラーの Biquad.Step と同じ順序で評価する。
            Vector256<double> y = b0 * x + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;
            return y;
        }
    }

    /// <summary>一つのチャンネルの一つの区間の統計です。Wide は4000Hz以下、Narrow は1000Hz以下の列です。誤差は差の二乗和を直接累積します。</summary>
    private struct SegmentStatistics
    {
        internal double Product;
        internal double WavEnergy;
        internal double OggEnergy;
        internal double WavWideEnergy;
        internal double OggWideEnergy;
        internal double WideError;
        internal double WavNarrowEnergy;
        internal double OggNarrowEnergy;
        internal double NarrowError;
    }

    /// <summary>4次バターワース低域通過フィルタ（直接形Iの双2次フィルタ2段の縦続）です。状態は0から始め、コピーすると状態も複製されます。</summary>
    private struct Cascade
    {
        private Biquad first, second;

        internal static Cascade Create(int sampleRate, double cutoffHertz) => new()
        {
            first = Biquad.CreateLowPass(sampleRate, cutoffHertz, DuplicateMergeSafetyPolicy.AudioButterworthFirstStageQ),
            second = Biquad.CreateLowPass(sampleRate, cutoffHertz, DuplicateMergeSafetyPolicy.AudioButterworthSecondStageQ)
        };

        internal double Step(double x) => second.Step(first.Step(x));
    }

    /// <summary>直接形Iの双2次低域通過フィルタです。</summary>
    private struct Biquad
    {
        private double b0, b1, a1, a2;
        private double x1, x2, y1, y2;

        internal double B0 => b0;
        internal double B1 => b1;
        internal double A1 => a1;
        internal double A2 => a2;

        internal static Biquad CreateLowPass(int sampleRate, double cutoffHertz, double q)
        {
            double omega = 2 * Math.PI * cutoffHertz / sampleRate;
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
