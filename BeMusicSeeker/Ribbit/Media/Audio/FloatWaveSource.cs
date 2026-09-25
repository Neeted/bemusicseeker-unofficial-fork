using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Ribbit.Media.Audio;

/// <summary>変更不能な復号音源をシーク可能なIEEE float32 WAVEとして公開します。</summary>
internal sealed class FloatWaveSource
{
    private const int HeaderLength = 80;
    private readonly byte[] header;
    private readonly DecodedAudio audio;
    private readonly int[] sourceChannelIndexesInWaveOrder;
    private readonly bool hasIdentityChannelOrder;
    private long position;

    /// <summary>簡潔なWAVEヘッダーを作成し、PCM本体は唯一の音声データとして保持します。</summary>
    internal FloatWaveSource(DecodedAudio audio, string path)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        ChannelLayout = audio.ChannelLayout.ToWaveOrder(out sourceChannelIndexesInWaveOrder);
        hasIdentityChannelOrder = true;
        for (int channel = 0; channel < sourceChannelIndexesInWaveOrder.Length; channel++)
        {
            if (sourceChannelIndexesInWaveOrder[channel] != channel)
            {
                hasIdentityChannelOrder = false;
                break;
            }
        }

        long dataLength = audio.PcmByteCount;
        long riffLength = checked(HeaderLength - 8L + dataLength);
        if (dataLength > uint.MaxValue || riffLength > uint.MaxValue || audio.FrameCount > uint.MaxValue)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.CreateBassSource,
                path,
                "The decoded audio is too large for the WAVE container used by BASS.");
        }

        long blockAlign = checked((long)audio.ChannelCount * sizeof(float));
        long averageBytesPerSecond = checked((long)audio.SampleRate * blockAlign);
        if (blockAlign > ushort.MaxValue || averageBytesPerSecond > uint.MaxValue)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.CreateBassSource,
                path,
                "The decoded audio format exceeds the WAVE header limits.");
        }

        TotalLength = checked(HeaderLength + dataLength);
        header = CreateHeader(
            audio,
            ChannelLayout,
            checked((uint)dataLength),
            checked((uint)riffLength),
            checked((ushort)blockAlign),
            checked((uint)averageBytesPerSecond));
    }

    /// <summary>仮想WAVE全体のバイト長を取得します。</summary>
    internal long TotalLength { get; }

    /// <summary>BASS が読む WAVE 順に並べたチャンネル配置を取得します。</summary>
    internal AudioChannelLayout ChannelLayout { get; }

    /// <summary>出力レートにおいて有限音源が占めるSRC後のフレーム数を切り上げて取得します。</summary>
    /// <param name="outputSampleRate">出力サンプルレート。</param>
    /// <returns>区間 [0, FrameCount / SampleRate) に含まれる出力格子のフレーム数。</returns>
    internal long GetOutputFrameCount(int outputSampleRate)
    {
        if (outputSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        }

        long scaledFrameCount = checked(audio.FrameCount * outputSampleRate);
        long wholeFrames = scaledFrameCount / audio.SampleRate;
        return scaledFrameCount % audio.SampleRate == 0 ? wholeFrames : checked(wholeFrames + 1);
    }

    /// <summary>現在位置からWAVEヘッダーとインターリーブPCMをまたいで読み取ります。</summary>
    internal int Read(IntPtr destination, int requestedLength)
    {
        if (destination == IntPtr.Zero || requestedLength < 0)
        {
            return -1;
        }
        if (position >= TotalLength)
        {
            return -1;
        }

        int count = checked((int)System.Math.Min(requestedLength, TotalLength - position));
        if (count == 0)
        {
            return 0;
        }

        int copied = 0;
        if (position < header.Length)
        {
            int headerBytes = (int)System.Math.Min(count, header.Length - position);
            Marshal.Copy(header, checked((int)position), destination, headerBytes);
            position += headerBytes;
            copied += headerBytes;
        }

        if (copied < count)
        {
            long pcmByteOffset = position - header.Length;
            CopyPcmBytes(IntPtr.Add(destination, copied), pcmByteOffset, count - copied);
            position += count - copied;
        }

        return count;
    }

    /// <summary>ファイル先頭から正確な終端までの任意のバイト位置へシークします。</summary>
    internal bool Seek(long offset)
    {
        if (offset < 0 || offset > TotalLength)
        {
            return false;
        }
        position = offset;
        return true;
    }

    private void CopyPcmBytes(IntPtr destination, long sourceByteOffset, int byteCount)
    {
        int copied = 0;
        int remainder = (int)(sourceByteOffset & (sizeof(float) - 1));
        if (remainder != 0)
        {
            int leadingBytes = System.Math.Min(sizeof(float) - remainder, byteCount);
            for (int index = 0; index < leadingBytes; index++)
            {
                WritePcmByte(destination, index, sourceByteOffset + index);
            }
            copied += leadingBytes;
            sourceByteOffset += leadingBytes;
        }

        int alignedBytes = (byteCount - copied) & ~(sizeof(float) - 1);
        if (alignedBytes > 0)
        {
            int sampleIndex = checked((int)(sourceByteOffset / sizeof(float)));
            int sampleCount = alignedBytes / sizeof(float);
            if (hasIdentityChannelOrder)
            {
                audio.CopySamplesTo(IntPtr.Add(destination, copied), sampleIndex, sampleCount);
            }
            else
            {
                int channelCount = audio.ChannelCount;
                for (int index = 0; index < sampleCount; index++)
                {
                    int outputSampleIndex = sampleIndex + index;
                    int frameIndex = outputSampleIndex / channelCount;
                    int outputChannel = outputSampleIndex % channelCount;
                    int inputSampleIndex = checked(
                        frameIndex * channelCount + sourceChannelIndexesInWaveOrder[outputChannel]);
                    int bits = BitConverter.SingleToInt32Bits(audio.GetSample(inputSampleIndex));
                    Marshal.WriteInt32(
                        IntPtr.Add(destination, copied + index * sizeof(float)),
                        bits);
                }
            }
            copied += alignedBytes;
            sourceByteOffset += alignedBytes;
        }

        int trailingBytes = byteCount - copied;
        for (int index = 0; index < trailingBytes; index++)
        {
            WritePcmByte(destination, copied + index, sourceByteOffset + index);
        }
    }

    private void WritePcmByte(IntPtr destination, int destinationOffset, long sourceByteOffset)
    {
        int sampleIndex = checked((int)(sourceByteOffset / sizeof(float)));
        int frameIndex = sampleIndex / audio.ChannelCount;
        int outputChannel = sampleIndex % audio.ChannelCount;
        int inputSampleIndex = checked(
            frameIndex * audio.ChannelCount + sourceChannelIndexesInWaveOrder[outputChannel]);
        int byteInSample = (int)(sourceByteOffset & (sizeof(float) - 1));
        int bits = BitConverter.SingleToInt32Bits(audio.GetSample(inputSampleIndex));
        Marshal.WriteByte(destination, destinationOffset, (byte)(bits >> (byteInSample * 8)));
    }

    private static byte[] CreateHeader(
        DecodedAudio audio,
        AudioChannelLayout channelLayout,
        uint dataLength,
        uint riffLength,
        ushort blockAlign,
        uint averageBytesPerSecond)
    {
        byte[] bytes = new byte[HeaderLength];
        WriteFourCc(bytes, 0, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), riffLength);
        WriteFourCc(bytes, 8, "WAVE");
        WriteFourCc(bytes, 12, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), checked((ushort)audio.ChannelCount));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), checked((uint)audio.SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), averageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(36), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(38), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), channelLayout.SpeakerMask);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(50), 0x0010);
        bytes[52] = 0x80;
        bytes[53] = 0x00;
        bytes[54] = 0x00;
        bytes[55] = 0xAA;
        bytes[56] = 0x00;
        bytes[57] = 0x38;
        bytes[58] = 0x9B;
        bytes[59] = 0x71;
        WriteFourCc(bytes, 60, "fact");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68), checked((uint)audio.FrameCount));
        WriteFourCc(bytes, 72, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), dataLength);
        return bytes;
    }

    private static void WriteFourCc(Span<byte> destination, int offset, string value)
    {
        for (int index = 0; index < 4; index++)
        {
            destination[offset + index] = checked((byte)value[index]);
        }
    }
}
