using System;
using System.Threading;

namespace Ribbit.Media.Audio;

/// <summary>出力gainを適用した結果のpeakを表します。</summary>
internal readonly record struct AudioOutputProcessResult(double Peak, bool ExceededFullScale);

/// <summary>callback gain処理が失敗した段階を表します。</summary>
internal enum AudioOutputProcessFailure
{
    NonFiniteInput,
    NonFiniteOutput
}

/// <summary>callback gain処理の失敗を管理側で保持する型付き診断です。</summary>
internal sealed class AudioOutputProcessException : InvalidOperationException
{
    internal AudioOutputProcessException(AudioOutputProcessFailure failure)
        : base("Output gain processing failed at " + failure + ".")
    {
        Failure = failure;
    }

    /// <summary>gain処理が失敗した段階を取得します。</summary>
    internal AudioOutputProcessFailure Failure { get; }
}

/// <summary>
/// DSP後のinterleaved Float32音声へ共通master gainを一度適用し、native callback間で
/// gain rampを連続させます。
/// </summary>
internal sealed class AudioOutputProcessor
{
    private const double RampDurationSeconds = 0.005d;

    private readonly int rampFrameCount;
    private long targetGainBits;
    private double observedTargetGain;
    private double currentGain;
    private double rampStep;
    private int rampFramesRemaining;

    /// <summary>初回gainを即時適用するprocessorを作成します。</summary>
    internal AudioOutputProcessor(int sampleRate, double initialGain)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (!double.IsFinite(initialGain))
        {
            throw new ArgumentOutOfRangeException(nameof(initialGain), "Output gain must be finite.");
        }

        rampFrameCount = System.Math.Max(
            1,
            checked((int)System.Math.Round(
                sampleRate * RampDurationSeconds,
                MidpointRounding.ToEven)));
        currentGain = initialGain;
        observedTargetGain = initialGain;
        targetGainBits = BitConverter.DoubleToInt64Bits(initialGain);
    }

    /// <summary>設定rateにおけるgain rampのframe数を取得します。</summary>
    internal int RampFrameCount => rampFrameCount;

    /// <summary>直近の処理frameへ適用したgain係数を取得します。</summary>
    internal double CurrentGain => currentGain;

    /// <summary>
    /// callback threadへ新しい目標gainを通知します。次に処理するframeから、直前frameへ
    /// 適用した係数を起点にrampします。
    /// </summary>
    internal void SetTargetGain(double gain)
    {
        if (!double.IsFinite(gain))
        {
            throw new ArgumentOutOfRangeException(nameof(gain), "Output gain must be finite.");
        }

        Interlocked.Exchange(ref targetGainBits, BitConverter.DoubleToInt64Bits(gain));
    }

    /// <summary>interleaved PCMの完全なframeへ現在のrampをin-placeで適用します。</summary>
    internal AudioOutputProcessResult Process(Span<float> interleavedSamples, int channelCount)
    {
        if (TryProcess(
                interleavedSamples,
                channelCount,
                out AudioOutputProcessResult result,
                out AudioOutputProcessFailure failure))
        {
            return result;
        }

        throw new AudioOutputProcessException(failure);
    }

    /// <summary>callback用に非有限PCMやgain結果を返却値で報告します。</summary>
    internal bool TryProcess(
        Span<float> interleavedSamples,
        int channelCount,
        out AudioOutputProcessResult result,
        out AudioOutputProcessFailure failure)
    {
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }
        if (interleavedSamples.Length % channelCount != 0)
        {
            throw new ArgumentException("The output data must contain complete interleaved frames.", nameof(interleavedSamples));
        }

        double requestedGain = BitConverter.Int64BitsToDouble(Volatile.Read(ref targetGainBits));
        if (requestedGain != observedTargetGain)
        {
            observedTargetGain = requestedGain;
            rampFramesRemaining = rampFrameCount;
            rampStep = (requestedGain - currentGain) / rampFrameCount;
        }

        double peak = 0d;
        bool exceededFullScale = false;
        for (int sampleOffset = 0; sampleOffset < interleavedSamples.Length; sampleOffset += channelCount)
        {
            if (rampFramesRemaining > 0)
            {
                currentGain += rampStep;
                rampFramesRemaining--;
                if (rampFramesRemaining == 0)
                {
                    currentGain = observedTargetGain;
                }
            }

            for (int channelOffset = 0; channelOffset < channelCount; channelOffset++)
            {
                int sampleIndex = sampleOffset + channelOffset;
                float input = interleavedSamples[sampleIndex];
                if (!float.IsFinite(input))
                {
                    result = default;
                    failure = AudioOutputProcessFailure.NonFiniteInput;
                    return false;
                }

                double output = input * currentGain;
                float converted = (float)output;
                if (!double.IsFinite(output) || !float.IsFinite(converted))
                {
                    result = default;
                    failure = AudioOutputProcessFailure.NonFiniteOutput;
                    return false;
                }

                interleavedSamples[sampleIndex] = converted;
                double absolute = System.Math.Abs((double)converted);
                peak = System.Math.Max(peak, absolute);
                exceededFullScale |= absolute > 1d;
            }
        }

        result = new AudioOutputProcessResult(peak, exceededFullScale);
        failure = default;
        return true;
    }

    /// <summary>offline PCMへ一定gainをin-placeで適用し、適用後peakを返します。</summary>
    internal static double ApplyConstantGain(Span<float> interleavedSamples, double gain)
    {
        if (!double.IsFinite(gain))
        {
            throw new ArgumentOutOfRangeException(nameof(gain), "Output gain must be finite.");
        }

        double peak = 0d;
        for (int sampleIndex = 0; sampleIndex < interleavedSamples.Length; sampleIndex++)
        {
            float input = interleavedSamples[sampleIndex];
            if (!float.IsFinite(input))
            {
                throw new InvalidOperationException("Offline PCM contains a non-finite sample.");
            }

            double output = input * gain;
            float converted = (float)output;
            if (!double.IsFinite(output) || !float.IsFinite(converted))
            {
                throw new InvalidOperationException("Offline gain produced a non-finite sample.");
            }

            interleavedSamples[sampleIndex] = converted;
            peak = System.Math.Max(peak, System.Math.Abs((double)converted));
        }

        return peak;
    }
}

/// <summary>整数encoderへ渡す正規化PCMが表現可能範囲を超えたことを報告します。</summary>
internal sealed class AudioOutputRangeException : InvalidOperationException
{
    internal AudioOutputRangeException(double peak)
        : base(CreateMessage(peak))
    {
        Peak = peak;
        RequiredAttenuationDb = 20d * System.Math.Log10(peak);
    }

    /// <summary>full scaleを超えたabsolute peakを取得します。</summary>
    internal double Peak { get; }

    /// <summary>peakをfull scale以下に収めるために必要な減衰量(dB)を取得します。</summary>
    internal double RequiredAttenuationDb { get; }

    private static string CreateMessage(double peak)
    {
        if (!double.IsFinite(peak) || peak <= 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(peak));
        }

        return "The integer encoder input exceeds full scale: peak=" + peak
            + " requiredAttenuationDb=" + (20d * System.Math.Log10(peak));
    }
}
