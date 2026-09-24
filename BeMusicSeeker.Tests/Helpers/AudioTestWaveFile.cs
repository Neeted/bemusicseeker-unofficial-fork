using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace BeMusicSeeker.Tests.Helpers;

/// <summary>testが実際のencoder outputから読むPCM WAV chunk情報です。</summary>
internal sealed record AudioTestWaveFile(
    byte[] Bytes,
    ushort Format,
    ushort Channels,
    int SampleRate,
    ushort BitsPerSample,
    int DataOffset,
    int DataLength);

/// <summary>RIFF chunk順序に依存せず、test出力のWAV PCM chunkを読むparserです。</summary>
internal static class AudioTestWaveFileReader
{
    /// <summary>fmt/data chunkを検査し、PCM/WAVE形式情報とsample領域を返します。</summary>
    internal static AudioTestWaveFile Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 12
            || !string.Equals(Encoding.ASCII.GetString(bytes, 0, 4), "RIFF", StringComparison.Ordinal)
            || !string.Equals(Encoding.ASCII.GetString(bytes, 8, 4), "WAVE", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The encoder output is not a RIFF/WAVE file.");
        }

        ushort? format = null;
        ushort? channels = null;
        int? sampleRate = null;
        ushort? bitsPerSample = null;
        int? dataOffset = null;
        int? dataLength = null;
        for (int chunkOffset = 12; chunkOffset <= bytes.Length - 8;)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, chunkOffset, 4);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunkOffset + 4, 4));
            int chunkDataOffset = checked(chunkOffset + 8);
            if (chunkLength > bytes.Length - chunkDataOffset || chunkLength > int.MaxValue)
            {
                throw new InvalidDataException("The encoder output contains a truncated WAV chunk.");
            }

            int length = (int)chunkLength;
            if (chunkId == "fmt ")
            {
                if (length < 16)
                {
                    throw new InvalidDataException("The WAV fmt chunk is shorter than the PCM header.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataOffset, 2));
                if (format == 0xFFFE)
                {
                    if (length < 40)
                    {
                        throw new InvalidDataException("The extensible WAV fmt chunk is incomplete.");
                    }
                    var subFormat = new Guid(bytes.AsSpan(chunkDataOffset + 24, 16));
                    format = subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1
                        : subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3
                        : throw new InvalidDataException("The extensible WAV subformat is not PCM or float.");
                }
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataOffset + 2, 2));
                sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunkDataOffset + 4, 4)));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataOffset + 14, 2));
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkDataOffset;
                dataLength = length;
            }

            chunkOffset = checked(chunkDataOffset + length + (length & 1));
        }

        if (!format.HasValue
            || !channels.HasValue
            || channels.Value == 0
            || !sampleRate.HasValue
            || sampleRate.Value <= 0
            || !bitsPerSample.HasValue
            || !dataOffset.HasValue
            || !dataLength.HasValue)
        {
            throw new InvalidDataException("The WAV output is missing a valid fmt or data chunk.");
        }

        return new AudioTestWaveFile(
            bytes,
            format.Value,
            channels.Value,
            sampleRate.Value,
            bitsPerSample.Value,
            dataOffset.Value,
            dataLength.Value);
    }
}
