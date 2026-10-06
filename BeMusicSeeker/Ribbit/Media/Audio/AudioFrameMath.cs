using System;

namespace Ribbit.Media.Audio;

/// <summary>BMS時刻、mixer frame、source frameの整数変換をまとめます。</summary>
internal static class AudioFrameMath
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>絶対時刻を最近傍frameへ変換し、中間値を偶数へ丸めます。</summary>
    /// <param name="time">変換する100ns単位の絶対時刻。</param>
    /// <param name="sampleRate">出力mixerの実効サンプルレート。</param>
    /// <returns>符号付きframe位置。負時刻のclampは呼出側が行います。</returns>
    internal static long TimeToFrame(TimeSpan time, int sampleRate)
    {
        ValidateRate(sampleRate, nameof(sampleRate));
        Int128 numerator = (Int128)time.Ticks * sampleRate;
        return checked((long)DivideRoundToNearestEven(numerator, TicksPerSecond));
    }

    /// <summary>mixer frameを最近傍100ns tickへ変換し、中間値を偶数へ丸めます。</summary>
    /// <param name="frame">符号付きmixer frame。</param>
    /// <param name="sampleRate">mixerの実効サンプルレート。</param>
    /// <returns>表示・既存時刻APIへ渡す時刻。</returns>
    internal static TimeSpan FrameToTime(long frame, int sampleRate)
    {
        ValidateRate(sampleRate, nameof(sampleRate));
        Int128 numerator = (Int128)frame * TicksPerSecond;
        long ticks = checked((long)DivideRoundToNearestEven(numerator, sampleRate));
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>有限source区間が別レートの出力格子に占めるframe数を切り上げます。</summary>
    /// <param name="sourceFrameCount">有限sourceのframe数。</param>
    /// <param name="sourceSampleRate">sourceの実効サンプルレート。</param>
    /// <param name="outputSampleRate">出力mixerの実効サンプルレート。</param>
    /// <returns>source時間区間を覆う出力frame数。</returns>
    internal static long CeilingOutputFrameCount(
        long sourceFrameCount,
        int sourceSampleRate,
        int outputSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceFrameCount);
        ValidateRate(sourceSampleRate, nameof(sourceSampleRate));
        ValidateRate(outputSampleRate, nameof(outputSampleRate));

        Int128 numerator = (Int128)sourceFrameCount * outputSampleRate;
        Int128 quotient = numerator / sourceSampleRate;
        if (numerator % sourceSampleRate != 0)
        {
            quotient++;
        }

        return checked((long)quotient);
    }

    /// <summary>mixer上の経過frameをsource格子へ切り捨て変換します。</summary>
    /// <param name="elapsedMixerFrames">区間開始から経過した非負mixer frame数。</param>
    /// <param name="sourceSampleRate">sourceの実効サンプルレート。</param>
    /// <param name="mixerSampleRate">mixerの実効サンプルレート。</param>
    /// <returns>source sample grid上の位置。</returns>
    internal static long SourceFrameFromMixerFrames(
        long elapsedMixerFrames,
        int sourceSampleRate,
        int mixerSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMixerFrames);
        ValidateRate(sourceSampleRate, nameof(sourceSampleRate));
        ValidateRate(mixerSampleRate, nameof(mixerSampleRate));

        Int128 numerator = (Int128)elapsedMixerFrames * sourceSampleRate;
        return checked((long)(numerator / mixerSampleRate));
    }

    /// <summary>frame位置をFloat32 interleaved PCMのbyte位置へchecked変換します。</summary>
    /// <param name="frame">非負frame位置。</param>
    /// <param name="channelCount">実際のchannel数。</param>
    /// <returns>frame先頭のbyte位置。</returns>
    internal static long Float32FrameToBytePosition(long frame, int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        return checked(frame * channelCount * sizeof(float));
    }

    private static Int128 DivideRoundToNearestEven(Int128 numerator, long denominator)
    {
        Int128 quotient = numerator / denominator;
        Int128 remainder = numerator % denominator;
        Int128 absoluteRemainder = remainder < 0 ? -remainder : remainder;
        Int128 doubledRemainder = absoluteRemainder * 2;
        if (doubledRemainder > denominator
            || (doubledRemainder == denominator && quotient % 2 != 0))
        {
            quotient += numerator < 0 ? -1 : 1;
        }

        return quotient;
    }

    private static void ValidateRate(int sampleRate, string parameterName)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
