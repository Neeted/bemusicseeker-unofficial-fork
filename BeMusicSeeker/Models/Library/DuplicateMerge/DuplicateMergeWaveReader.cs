using System;
using System.Buffers.Binary;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

/// <summary>
/// 同等性検査のためのWAVの読取りです。再生の音声セッションを使わず、RIFF/WAVEの無圧縮PCM
/// （整数8・16・24・32ビット、32ビット浮動小数点。<c>WAVE_FORMAT_EXTENSIBLE</c> の同じ形式を含む）だけを、フルスケールを±1とするPCMへ読みます。
/// </summary>
/// <remarks>
/// 対応しない形式や壊れた入力は、復号できないものとして <c>null</c> を返します。読取りの入出力の失敗は例外のまま呼出元へ伝え、
/// 復号できないことと区別します。<c>fmt </c> と <c>data</c> 以外のチャンクは奇数長のパディングを含めて読み飛ばし、
/// <c>data</c> の後ろのチャンクは読みません。<c>data</c> の申告長がファイルの残りを超える場合は、実在する範囲をフレーム単位に切り捨てて読みます。
/// </remarks>
internal static class DuplicateMergeWaveReader
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;
    private const int ChunkHeaderLength = 8;
    private const int ReadBufferLength = 64 * 1024;

    /// <summary><c>KSDATAFORMAT_SUBTYPE_*</c> のGUIDのうち、先頭2バイトのフォーマット番号を除く14バイトです。</summary>
    private static ReadOnlySpan<byte> SubFormatSuffix => [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    private readonly record struct Format(int Channels, int SampleRate, int BytesPerSample, bool IsFloat);

    /// <summary>ファイルを読みます。入出力の失敗は例外のまま伝えます。</summary>
    /// <param name="path">WAVファイルのパス。</param>
    /// <returns>読めたPCM。復号できなければ <c>null</c>。</returns>
    internal static DuplicateMergeAudioSamples ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        return Read(stream);
    }

    /// <summary>シーク可能なストリームを現在の位置から読みます。入出力の失敗は例外のまま伝えます。</summary>
    /// <param name="stream">RIFFヘッダーの先頭に位置するストリーム。</param>
    /// <returns>読めたPCM。復号できなければ <c>null</c>。</returns>
    internal static DuplicateMergeAudioSamples Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;
        Span<byte> header = stackalloc byte[ChunkHeaderLength + 4];
        if (length - stream.Position < header.Length) { return null; }
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) { return null; }

        Format? format = null;
        Span<byte> chunkHeader = stackalloc byte[ChunkHeaderLength];
        while (length - stream.Position >= ChunkHeaderLength)
        {
            stream.ReadExactly(chunkHeader);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            long bodyStart = stream.Position;
            if (chunkHeader[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16 || size > length - bodyStart) { return null; }
                format = ReadFormat(stream, (int)Math.Min(size, 40));
                if (format == null) { return null; }
            }
            else if (chunkHeader[..4].SequenceEqual("data"u8))
            {
                return format == null ? null : ReadData(stream, format.Value, Math.Min(size, length - bodyStart));
            }
            stream.Seek(bodyStart + size + (size & 1), SeekOrigin.Begin);
        }
        return null;
    }

    private static Format? ReadFormat(Stream stream, int available)
    {
        Span<byte> body = stackalloc byte[40];
        body = body[..available];
        stream.ReadExactly(body);
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
        if (tag == FormatExtensible)
        {
            // 拡張部(cbSize 2バイト、有効ビット 2バイト、チャンネルマスク 4バイト)に続くサブフォーマットのGUIDで、実際の形式を示す。
            if (available < 40 || !body[26..40].SequenceEqual(SubFormatSuffix)) { return null; }
            tag = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
        }
        if (channels < 1 || sampleRate <= 0) { return null; }
        return (tag, bits) switch
        {
            (FormatPcm, 8 or 16 or 24 or 32) => new Format(channels, sampleRate, bits / 8, IsFloat: false),
            (FormatFloat, 32) => new Format(channels, sampleRate, 4, IsFloat: true),
            _ => null
        };
    }

    private static DuplicateMergeAudioSamples ReadData(Stream stream, Format format, long dataLength)
    {
        long frameLength = (long)format.Channels * format.BytesPerSample;
        long frames = dataLength / frameLength;
        long sampleCount = frames * format.Channels;
        if (sampleCount > Array.MaxLength) { return null; }

        float[] samples = new float[sampleCount];
        int framesPerRead = (int)Math.Max(1, ReadBufferLength / frameLength);
        byte[] buffer = new byte[framesPerRead * frameLength];
        long done = 0;
        while (done < sampleCount)
        {
            int count = (int)Math.Min(sampleCount - done, (long)framesPerRead * format.Channels);
            stream.ReadExactly(buffer, 0, count * format.BytesPerSample);
            for (int index = 0; index < count; index++)
            {
                float value = Convert(buffer.AsSpan(index * format.BytesPerSample, format.BytesPerSample), format);
                if (!float.IsFinite(value)) { return null; }
                samples[done + index] = value;
            }
            done += count;
        }
        return new DuplicateMergeAudioSamples(format.SampleRate, format.Channels, samples);
    }

    private static float Convert(ReadOnlySpan<byte> bytes, Format format) => format.BytesPerSample switch
    {
        1 => (bytes[0] - 128) / 128f,
        2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        3 => ((bytes[0] | (bytes[1] << 8) | ((sbyte)bytes[2] << 16))) / 8388608f,
        _ => format.IsFloat
            ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
            : (float)(BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0)
    };
}
