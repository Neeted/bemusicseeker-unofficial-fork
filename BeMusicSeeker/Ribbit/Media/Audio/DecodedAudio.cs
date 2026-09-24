using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ribbit.Media.Audio;

/// <summary>インターリーブPCMにおけるチャンネルのスピーカー位置を表します。</summary>
internal enum AudioSpeakerPosition : uint
{
    FrontLeft = 0x00000001,
    FrontRight = 0x00000002,
    FrontCenter = 0x00000004,
    LowFrequency = 0x00000008,
    BackLeft = 0x00000010,
    BackRight = 0x00000020,
    FrontLeftOfCenter = 0x00000040,
    FrontRightOfCenter = 0x00000080,
    BackCenter = 0x00000100,
    SideLeft = 0x00000200,
    SideRight = 0x00000400,
    TopCenter = 0x00000800,
    TopFrontLeft = 0x00001000,
    TopFrontCenter = 0x00002000,
    TopFrontRight = 0x00004000,
    TopBackLeft = 0x00008000,
    TopBackCenter = 0x00010000,
    TopBackRight = 0x00020000
}

/// <summary>インターリーブPCMの各チャンネルに割り当てるスピーカー配置を定義します。</summary>
internal sealed class AudioChannelLayout
{
    private const int MaximumChannelCount = 8;

    private static readonly AudioSpeakerPosition[] WaveSpeakerOrder =
    [
        AudioSpeakerPosition.FrontLeft,
        AudioSpeakerPosition.FrontRight,
        AudioSpeakerPosition.FrontCenter,
        AudioSpeakerPosition.LowFrequency,
        AudioSpeakerPosition.BackLeft,
        AudioSpeakerPosition.BackRight,
        AudioSpeakerPosition.FrontLeftOfCenter,
        AudioSpeakerPosition.FrontRightOfCenter,
        AudioSpeakerPosition.BackCenter,
        AudioSpeakerPosition.SideLeft,
        AudioSpeakerPosition.SideRight,
        AudioSpeakerPosition.TopCenter,
        AudioSpeakerPosition.TopFrontLeft,
        AudioSpeakerPosition.TopFrontCenter,
        AudioSpeakerPosition.TopFrontRight,
        AudioSpeakerPosition.TopBackLeft,
        AudioSpeakerPosition.TopBackCenter,
        AudioSpeakerPosition.TopBackRight
    ];

    private readonly AudioSpeakerPosition[] positions;

    /// <summary>重複しないスピーカー位置から変更不能なチャンネル順を作成します。</summary>
    internal AudioChannelLayout(ReadOnlySpan<AudioSpeakerPosition> positions)
    {
        if (positions.IsEmpty || positions.Length > MaximumChannelCount)
        {
            throw new ArgumentException("An audio channel layout must contain one through eight speakers.", nameof(positions));
        }

        this.positions = positions.ToArray();
        uint mask = 0;
        foreach (AudioSpeakerPosition position in this.positions)
        {
            uint bit = (uint)position;
            if (!Enum.IsDefined(position) || bit == 0 || BitOperations.PopCount(bit) != 1 || (mask & bit) != 0)
            {
                throw new ArgumentException("An audio channel layout must contain distinct known speaker positions.", nameof(positions));
            }
            mask |= bit;
        }

        SpeakerMask = mask;
    }

    /// <summary>インターリーブされたチャンネル数を取得します。</summary>
    internal int ChannelCount => positions.Length;

    /// <summary>この配置を表すWAVEスピーカーマスクを取得します。</summary>
    internal uint SpeakerMask { get; }

    /// <summary>0始まりのチャンネルに割り当てられたスピーカー位置を取得します。</summary>
    internal AudioSpeakerPosition this[int channel] => positions[channel];

    /// <summary>BASS mixer の標準論理順で出力側の配置を作成します。</summary>
    internal static AudioChannelLayout CreateBassOutput(int channels)
    {
        AudioSpeakerPosition[] layout = channels switch
        {
            1 => [AudioSpeakerPosition.FrontCenter],
            2 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight],
            3 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.FrontCenter],
            4 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.BackLeft, AudioSpeakerPosition.BackRight],
            5 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.BackLeft, AudioSpeakerPosition.BackRight],
            6 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.LowFrequency, AudioSpeakerPosition.BackLeft, AudioSpeakerPosition.BackRight],
            7 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.LowFrequency, AudioSpeakerPosition.BackCenter, AudioSpeakerPosition.SideLeft, AudioSpeakerPosition.SideRight],
            8 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.LowFrequency, AudioSpeakerPosition.BackLeft, AudioSpeakerPosition.BackRight, AudioSpeakerPosition.SideLeft, AudioSpeakerPosition.SideRight],
            _ => throw new ArgumentOutOfRangeException(nameof(channels), "BASS output layouts support one through eight channels.")
        };
        return new AudioChannelLayout(layout);
    }

    /// <summary>PCM を WAVE speaker bit 順へ並べ替えるための source index を返します。</summary>
    internal AudioChannelLayout ToWaveOrder(out int[] sourceChannelIndexes)
    {
        var ordered = new List<AudioSpeakerPosition>(ChannelCount);
        var indexes = new List<int>(ChannelCount);
        foreach (AudioSpeakerPosition position in WaveSpeakerOrder)
        {
            int sourceIndex = IndexOf(position);
            if (sourceIndex >= 0)
            {
                ordered.Add(position);
                indexes.Add(sourceIndex);
            }
        }

        if (ordered.Count != ChannelCount)
        {
            throw new ArgumentException("The channel layout contains a speaker position with no WAVE ordering.", nameof(positions));
        }

        sourceChannelIndexes = [.. indexes];
        return new AudioChannelLayout([.. ordered]);
    }

    private int IndexOf(AudioSpeakerPosition position)
    {
        for (int index = 0; index < positions.Length; index++)
        {
            if (positions[index] == position)
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>従来のWAVおよびBASS形式で使う標準モノラル・ステレオ配置を作成します。</summary>
    internal static AudioChannelLayout CreateStandard(int channels)
    {
        return channels switch
        {
            1 => new AudioChannelLayout([AudioSpeakerPosition.FrontCenter]),
            2 => new AudioChannelLayout([AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight]),
            _ => throw new ArgumentOutOfRangeException(nameof(channels), "A multi-channel input must provide an explicit speaker layout.")
        };
    }

    /// <summary>Vorbis mapping family 0で定義されたチャンネル順を作成します。</summary>
    internal static AudioChannelLayout CreateVorbis(int channels)
    {
        AudioSpeakerPosition[] layout = channels switch
        {
            1 => [AudioSpeakerPosition.FrontCenter],
            2 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight],
            3 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.FrontRight],
            4 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.BackLeft, AudioSpeakerPosition.BackRight],
            5 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.BackLeft, AudioSpeakerPosition.BackRight],
            6 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.BackLeft, AudioSpeakerPosition.BackRight, AudioSpeakerPosition.LowFrequency],
            7 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.SideLeft, AudioSpeakerPosition.SideRight, AudioSpeakerPosition.BackCenter, AudioSpeakerPosition.LowFrequency],
            8 => [AudioSpeakerPosition.FrontLeft, AudioSpeakerPosition.FrontCenter, AudioSpeakerPosition.FrontRight, AudioSpeakerPosition.SideLeft, AudioSpeakerPosition.SideRight, AudioSpeakerPosition.BackLeft, AudioSpeakerPosition.BackRight, AudioSpeakerPosition.LowFrequency],
            _ => throw new ArgumentOutOfRangeException(nameof(channels), "Vorbis mapping family zero supports one through eight channels.")
        };
        return new AudioChannelLayout(layout);
    }

    /// <summary>スピーカーマスクのビット順に従うWAVEチャンネル配置を作成します。</summary>
    internal static AudioChannelLayout CreateWaveMask(uint mask, int channels)
    {
        if (mask == 0)
        {
            throw new ArgumentException("A multi-channel WAVE input must declare its speaker mask.", nameof(mask));
        }

        var ordered = new List<AudioSpeakerPosition>(channels);
        uint remaining = mask;
        foreach (AudioSpeakerPosition position in WaveSpeakerOrder)
        {
            uint bit = (uint)position;
            if ((remaining & bit) != 0)
            {
                ordered.Add(position);
                remaining &= ~bit;
            }
        }

        if (remaining != 0 || ordered.Count != channels)
        {
            throw new ArgumentException("The WAVE speaker mask does not identify every channel.", nameof(mask));
        }
        return new AudioChannelLayout([.. ordered]);
    }
}

/// <summary>元のレートとスピーカー順を保持した有限のfloat32インターリーブPCMです。</summary>
internal sealed class DecodedAudio
{
    private readonly float[] samples;

    /// <summary>入力 PCM の配列所有権を受け取り、フレーム配置全体を検証して作成します。</summary>
    /// <param name="sampleRate">入力のサンプルレート。</param>
    /// <param name="channelLayout">PCM 配列の各チャンネルに対応する speaker 配置。</param>
    /// <param name="samples">インターリーブ PCM。所有権を移譲するため、呼び出し元は作成後に変更・再利用しません。</param>
    internal DecodedAudio(int sampleRate, AudioChannelLayout channelLayout, float[] samples)
    {
        ArgumentNullException.ThrowIfNull(channelLayout);
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "The source sample rate must be positive.");
        }
        if (samples.Length % channelLayout.ChannelCount != 0)
        {
            throw new ArgumentException("The interleaved PCM length must contain complete frames.", nameof(samples));
        }

        for (int index = 0; index < samples.Length; index++)
        {
            if (!float.IsFinite(samples[index]))
            {
                throw new ArgumentException("The decoded PCM contains a non-finite sample.", nameof(samples));
            }
        }

        SampleRate = sampleRate;
        ChannelLayout = channelLayout;
        this.samples = samples;
        FrameCount = samples.Length / channelLayout.ChannelCount;
    }

    /// <summary>元のサンプルレートをフレーム毎秒で取得します。</summary>
    internal int SampleRate { get; }

    /// <summary>インターリーブ順で明示されたチャンネル配置を取得します。</summary>
    internal AudioChannelLayout ChannelLayout { get; }

    /// <summary>チャンネル数を取得します。</summary>
    internal int ChannelCount => ChannelLayout.ChannelCount;

    /// <summary>完全なPCMフレームの正確な数を取得します。</summary>
    internal long FrameCount { get; }

    /// <summary>checked演算で求めたPCMデータのバイト数を取得します。</summary>
    internal long PcmByteCount => checked((long)samples.Length * sizeof(float));

    /// <summary>変更可能な配列を公開せずにインターリーブサンプルを1つ読み取ります。</summary>
    internal float GetSample(int sampleIndex) => samples[sampleIndex];

    /// <summary>フレーム境界に揃えたfloatサンプルを固定済みのnative領域へコピーします。</summary>
    internal void CopySamplesTo(IntPtr destination, int sourceSampleIndex, int sampleCount)
    {
        if (sourceSampleIndex < 0 || sampleCount < 0 || sourceSampleIndex > samples.Length - sampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSampleIndex));
        }
        System.Runtime.InteropServices.Marshal.Copy(samples, sourceSampleIndex, destination, sampleCount);
    }
}
