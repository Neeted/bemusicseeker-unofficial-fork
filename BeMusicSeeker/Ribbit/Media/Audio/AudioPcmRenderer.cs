using System;
using ManagedBass;

namespace Ribbit.Media.Audio;

/// <summary>音声フレームの取得時に発生したnativeまたはPCM契約違反を表します。</summary>
internal sealed class AudioPcmRenderException : Exception
{
    internal AudioPcmRenderException(
        int channel,
        AudioPcmRenderStage stage,
        Errors? nativeError = null,
        long expectedFrames = 0,
        long actualFrames = 0,
        Exception innerException = null)
        : base(
            "Audio PCM channel " + channel
            + " failed at " + stage
            + (nativeError.HasValue ? " nativeError=" + nativeError.Value : string.Empty)
            + (stage == AudioPcmRenderStage.UnexpectedEnd
                ? " expectedFrames=" + expectedFrames + " actualFrames=" + actualFrames
                : string.Empty),
            innerException)
    {
        Channel = channel;
        Stage = stage;
        NativeError = nativeError;
        ExpectedFrames = expectedFrames;
        ActualFrames = actualFrames;
    }

    /// <summary>失敗したBASS channelを取得します。</summary>
    internal int Channel { get; }

    /// <summary>失敗した取得段階を取得します。</summary>
    internal AudioPcmRenderStage Stage { get; }

    /// <summary>native callが返したBASS errorを取得します。</summary>
    internal Errors? NativeError { get; }

    /// <summary>予期しない終端までに要求したframe数を取得します。</summary>
    internal long ExpectedFrames { get; }

    /// <summary>予期しない終端までに取得したframe数を取得します。</summary>
    internal long ActualFrames { get; }
}

/// <summary>音声frame取得が失敗した段階を表します。</summary>
internal enum AudioPcmRenderStage
{
    NativeRead,
    InvalidReadLength,
    UnalignedFrame,
    Stalled,
    NonFiniteSample,
    UnexpectedEnd
}

/// <summary>interleaved PCM全体のpeakとRMSを保持します。</summary>
internal readonly record struct AudioPcmLevels(double Peak, double Rms);

/// <summary>decode channelからfloat PCMを取得するためのBASS境界です。</summary>
internal interface IAudioPcmNative
{
    /// <summary>float bufferへdecode dataを読み込み、書き込んだbyte数を返します。</summary>
    int ChannelGetData(int channel, float[] buffer, int lengthBytes);

    /// <summary>直前のBASS callが残したnative errorを取得します。</summary>
    Errors LastError { get; }
}

/// <summary>PCM frame境界のManagedBass実装です。</summary>
internal sealed class ManagedBassAudioPcmNative : IAudioPcmNative
{
    /// <summary>状態を持たないproduction境界を取得します。</summary>
    internal static ManagedBassAudioPcmNative Instance { get; } = new();

    private ManagedBassAudioPcmNative()
    {
    }

    /// <inheritdoc />
    public int ChannelGetData(int channel, float[] buffer, int lengthBytes) =>
        Bass.ChannelGetData(channel, buffer, lengthBytes);

    /// <inheritdoc />
    public Errors LastError => Bass.LastError;
}

/// <summary>
/// BASS channelからinterleaved Float32 frameを取得し、callbackと有限offline renderで
/// 共通するframe単位の規則を適用します。
/// </summary>
internal sealed class AudioPcmRenderer
{
    private const int PullBufferSamples = 32768;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private readonly int channel;
    private readonly int sampleRate;
    private readonly int channelCount;
    private readonly int bytesPerFrame;
    private readonly IAudioPcmNative native;
    private readonly float[] pullBuffer;

    /// <summary>Float32 BASS decode channel用のframe rendererを作成します。</summary>
    internal AudioPcmRenderer(
        int channel,
        int sampleRate,
        int channelCount,
        IAudioPcmNative native = null)
    {
        if (channel == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        this.channel = channel;
        this.sampleRate = sampleRate;
        this.channelCount = channelCount;
        bytesPerFrame = checked(channelCount * sizeof(float));
        this.native = native ?? ManagedBassAudioPcmNative.Instance;
        pullBuffer = new float[System.Math.Max(
            channelCount,
            PullBufferSamples - PullBufferSamples % channelCount)];
    }

    /// <summary>絶対時刻のframe変換に使うsample rateを取得します。</summary>
    internal int SampleRate => sampleRate;

    /// <summary>各frameのinterleaved channel数を取得します。</summary>
    internal int ChannelCount => channelCount;

    /// <summary>取得元channelとしてrendererを初期化したBASS handleを取得します。</summary>
    internal int Channel => channel;

    /// <summary>
    /// 要求frame数まで取得します。正の短いreadは続け、BASSが<see cref="Errors.Ended"/>を
    /// 報告したときだけ明示的な終端結果を返します。
    /// </summary>
    internal AudioPcmReadResult ReadFrames(
        float[] destination,
        int destinationFrameOffset,
        int requestedFrames)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destinationFrameOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationFrameOffset));
        }
        if (requestedFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedFrames));
        }

        int requestedSamples = checked(requestedFrames * channelCount);
        int destinationSampleOffset = checked(destinationFrameOffset * channelCount);
        if (requestedSamples > destination.Length
            || destinationSampleOffset > destination.Length - requestedSamples)
        {
            throw new ArgumentException("The destination does not contain the requested PCM frames.", nameof(destination));
        }

        return ReadFrames(channel, destination.AsSpan(destinationSampleOffset, requestedSamples), requestedFrames);
    }

    /// <summary>
    /// caller-owned bufferからFloat32 frameを取得し、正の短いreadを続けて要求数を満たします。
    /// unmanaged callback bufferもSpanとして渡せます。
    /// </summary>
    internal AudioPcmReadResult ReadFrames(Span<float> destination, int requestedFrames)
    {
        return ReadFrames(channel, destination, requestedFrames);
    }

    /// <summary>取得エラーを例外にせず、callback側がnative threadを越えて管理側へ渡せるようにします。</summary>
    internal bool TryReadFrames(
        int sourceChannel,
        Span<float> destination,
        int requestedFrames,
        out AudioPcmReadResult result,
        out AudioPcmRenderStage failureStage,
        out Errors? nativeError)
    {
        if (sourceChannel == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceChannel));
        }
        if (requestedFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedFrames));
        }
        int requestedSamples = checked(requestedFrames * channelCount);
        if (requestedSamples > destination.Length)
        {
            throw new ArgumentException("The destination does not contain the requested PCM frames.", nameof(destination));
        }

        int framesRead = 0;
        while (framesRead < requestedFrames)
        {
            int framesToRead = System.Math.Min(
                requestedFrames - framesRead,
                pullBuffer.Length / channelCount);
            int requestedBytes = checked(framesToRead * bytesPerFrame);
            int actualBytes;
            try
            {
                actualBytes = native.ChannelGetData(sourceChannel, pullBuffer, requestedBytes);
            }
            catch
            {
                result = new AudioPcmReadResult(framesRead, ReachedEnd: false);
                failureStage = AudioPcmRenderStage.NativeRead;
                nativeError = CaptureLastError();
                return false;
            }

            if (actualBytes < 0)
            {
                nativeError = CaptureLastError();
                if (nativeError == Errors.Ended)
                {
                    result = new AudioPcmReadResult(framesRead, ReachedEnd: true);
                    failureStage = default;
                    return true;
                }

                result = new AudioPcmReadResult(framesRead, ReachedEnd: false);
                failureStage = AudioPcmRenderStage.NativeRead;
                return false;
            }
            if (actualBytes > requestedBytes)
            {
                result = new AudioPcmReadResult(framesRead, ReachedEnd: false);
                failureStage = AudioPcmRenderStage.InvalidReadLength;
                nativeError = CaptureLastError();
                return false;
            }
            if (actualBytes == 0)
            {
                nativeError = CaptureLastError();
                if (nativeError == Errors.Ended)
                {
                    result = new AudioPcmReadResult(framesRead, ReachedEnd: true);
                    failureStage = default;
                    return true;
                }

                result = new AudioPcmReadResult(framesRead, ReachedEnd: false);
                failureStage = AudioPcmRenderStage.Stalled;
                return false;
            }
            if (actualBytes % bytesPerFrame != 0)
            {
                result = new AudioPcmReadResult(framesRead, ReachedEnd: false);
                failureStage = AudioPcmRenderStage.UnalignedFrame;
                nativeError = CaptureLastError();
                return false;
            }

            int actualFrames = actualBytes / bytesPerFrame;
            int actualSamples = checked(actualFrames * channelCount);
            for (int sampleIndex = 0; sampleIndex < actualSamples; sampleIndex++)
            {
                if (!float.IsFinite(pullBuffer[sampleIndex]))
                {
                    result = new AudioPcmReadResult(framesRead, ReachedEnd: false);
                    failureStage = AudioPcmRenderStage.NonFiniteSample;
                    nativeError = null;
                    return false;
                }
            }

            pullBuffer.AsSpan(0, actualSamples).CopyTo(
                destination.Slice(checked(framesRead * channelCount), actualSamples));
            framesRead = checked(framesRead + actualFrames);
        }

        result = new AudioPcmReadResult(framesRead, ReachedEnd: false);
        failureStage = default;
        nativeError = null;
        return true;
    }

    /// <summary>指定channelからFloat32 frameを取得し、caller-owned Spanへ書き込みます。</summary>
    internal AudioPcmReadResult ReadFrames(
        int sourceChannel,
        Span<float> destination,
        int requestedFrames)
    {
        if (TryReadFrames(
                sourceChannel,
                destination,
                requestedFrames,
                out AudioPcmReadResult result,
                out AudioPcmRenderStage failureStage,
                out Errors? nativeError))
        {
            return result;
        }

        throw CreateException(sourceChannel, failureStage, nativeError);
    }

    /// <summary>有限区間を取得し、要求したframe数に達しなければ失敗します。</summary>
    internal void ReadFramesExactly(
        float[] destination,
        int destinationFrameOffset,
        int requestedFrames)
    {
        AudioPcmReadResult result = ReadFrames(destination, destinationFrameOffset, requestedFrames);
        if (result.FramesRead != requestedFrames)
        {
            throw new AudioPcmRenderException(
                channel,
                AudioPcmRenderStage.UnexpectedEnd,
                nativeError: Errors.Ended,
                expectedFrames: requestedFrames,
                actualFrames: result.FramesRead);
        }
    }

    /// <summary>絶対ticksを最近傍output frameへ変換し、ちょうど中間なら偶数へ丸めます。</summary>
    internal static long TimeToFrame(TimeSpan time, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        Int128 numerator = (Int128)time.Ticks * sampleRate;
        Int128 quotient = numerator / TicksPerSecond;
        Int128 remainder = numerator % TicksPerSecond;
        Int128 absoluteRemainder = remainder < 0 ? -remainder : remainder;
        Int128 doubledRemainder = absoluteRemainder * 2;
        bool roundAwayFromZero = doubledRemainder > TicksPerSecond
            || (doubledRemainder == TicksPerSecond && quotient % 2 != 0);
        if (roundAwayFromZero)
        {
            quotient += numerator < 0 ? -1 : 1;
        }

        return checked((long)quotient);
    }

    /// <summary>PCMを消費・複製せず、peakとbuffer全体のRMSを測定します。</summary>
    internal static AudioPcmLevels Measure(float[] interleavedPcm, int channelCount)
    {
        ArgumentNullException.ThrowIfNull(interleavedPcm);
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }
        if (interleavedPcm.Length % channelCount != 0)
        {
            throw new ArgumentException("The PCM data must contain complete interleaved frames.", nameof(interleavedPcm));
        }
        if (interleavedPcm.Length == 0)
        {
            return new AudioPcmLevels(0d, 0d);
        }

        double peak = 0d;
        double sum = 0d;
        double compensation = 0d;
        foreach (float sample in interleavedPcm)
        {
            if (!float.IsFinite(sample))
            {
                throw new ArgumentException("PCM samples must be finite.", nameof(interleavedPcm));
            }

            double value = sample;
            peak = System.Math.Max(peak, System.Math.Abs(value));
            double square = value * value;
            double corrected = square - compensation;
            double next = sum + corrected;
            compensation = (next - sum) - corrected;
            sum = next;
        }

        return new AudioPcmLevels(peak, System.Math.Sqrt(sum / interleavedPcm.Length));
    }

    private Errors CaptureLastError()
    {
        try
        {
            return native.LastError;
        }
        catch
        {
            return Errors.Unknown;
        }
    }

    private static AudioPcmRenderException CreateException(
        int sourceChannel,
        AudioPcmRenderStage stage,
        Errors? nativeError) =>
        new(sourceChannel, stage, nativeError);
}

/// <summary>float PCM取得で完了したframe数と終端状態を表します。</summary>
internal readonly record struct AudioPcmReadResult(int FramesRead, bool ReachedEnd);
