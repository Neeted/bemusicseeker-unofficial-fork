using System.Buffers.Binary;
using System.Text;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// WAVとOGGの同等性検査のテストが使う、WAVのバイト列と決定的なPCMの作成補助です。
/// 製品のWAV読取りを使わず、期待する値をテスト側で独立に組み立てるため、フォーマットごとの量子化と並びをここで定めます。
/// </summary>
public static class DuplicateMergeAudioTestFiles
{
    /// <summary>作成するWAVのサンプル形式です。</summary>
    public enum WaveSampleFormat
    {
        Pcm8,
        Pcm16,
        Pcm24,
        Pcm32,
        Float32
    }

    /// <summary>量子化の分解能（フルスケールの何分の一か）の逆数を、形式ごとに返します。整数形式の値は、この倍数で表すと誤差なく往復します。</summary>
    internal static double Scale(WaveSampleFormat format) => format switch
    {
        WaveSampleFormat.Pcm8 => 128,
        WaveSampleFormat.Pcm16 => 32768,
        WaveSampleFormat.Pcm24 => 8388608,
        WaveSampleFormat.Pcm32 => 2147483648,
        _ => 1
    };

    /// <summary>
    /// WAVファイルのバイト列を作ります。
    /// </summary>
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="channels">チャンネル数。</param>
    /// <param name="format">サンプル形式。</param>
    /// <param name="samples">チャンネルを交互に並べたサンプル。整数形式では <c>Scale</c> を掛けて丸めます。</param>
    /// <param name="extensible"><c>WAVE_FORMAT_EXTENSIBLE</c> の <c>fmt </c> にするか。</param>
    /// <param name="chunkBetween"><c>fmt </c> と <c>data</c> の間へ置くチャンクの本文。奇数長ならパディングを付けます。</param>
    /// <param name="chunkAfter"><c>data</c> の後ろへ置くチャンクの本文。</param>
    /// <param name="declaredDataLength"><c>data</c> の申告長。指定しなければ実際の長さです。</param>
    internal static byte[] Wave(
        int sampleRate, int channels, WaveSampleFormat format, float[] samples,
        bool extensible = false, byte[]? chunkBetween = null, byte[]? chunkAfter = null, uint? declaredDataLength = null)
    {
        int bytesPerSample = format switch
        {
            WaveSampleFormat.Pcm8 => 1,
            WaveSampleFormat.Pcm16 => 2,
            WaveSampleFormat.Pcm24 => 3,
            _ => 4
        };
        byte[] data = new byte[samples.Length * bytesPerSample];
        for (int index = 0; index < samples.Length; index++)
        {
            Span<byte> target = data.AsSpan(index * bytesPerSample, bytesPerSample);
            double scaled = samples[index] * Scale(format);
            switch (format)
            {
                case WaveSampleFormat.Pcm8:
                    target[0] = (byte)Math.Clamp((int)Math.Round(scaled) + 128, 0, 255);
                    break;
                case WaveSampleFormat.Pcm16:
                    BinaryPrimitives.WriteInt16LittleEndian(target, (short)Math.Clamp((int)Math.Round(scaled), short.MinValue, short.MaxValue));
                    break;
                case WaveSampleFormat.Pcm24:
                    int value = Math.Clamp((int)Math.Round(scaled), -8388608, 8388607);
                    target[0] = (byte)value;
                    target[1] = (byte)(value >> 8);
                    target[2] = (byte)(value >> 16);
                    break;
                case WaveSampleFormat.Pcm32:
                    BinaryPrimitives.WriteInt32LittleEndian(target, (int)Math.Clamp(Math.Round(scaled), int.MinValue, int.MaxValue));
                    break;
                default:
                    BinaryPrimitives.WriteSingleLittleEndian(target, samples[index]);
                    break;
            }
        }

        ushort tag = format == WaveSampleFormat.Float32 ? (ushort)3 : (ushort)1;
        ushort bits = (ushort)(bytesPerSample * 8);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(extensible ? 40u : 16u);
        writer.Write(extensible ? (ushort)0xFFFE : tag);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((ushort)(channels * bytesPerSample));
        writer.Write(bits);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write(bits);
            writer.Write(channels == 1 ? 4u : 3u);
            writer.Write(tag);
            writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }

        if (chunkBetween != null) { WriteChunk(writer, "LIST", chunkBetween); }
        writer.Write("data"u8);
        writer.Write(declaredDataLength ?? (uint)data.Length);
        writer.Write(data);
        if (data.Length % 2 == 1 && declaredDataLength == null) { writer.Write((byte)0); }
        if (chunkAfter != null) { WriteChunk(writer, "LIST", chunkAfter); }
        writer.Flush();

        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static void WriteChunk(BinaryWriter writer, string id, byte[] body)
    {
        writer.Write(Encoding.ASCII.GetBytes(id));
        writer.Write((uint)body.Length);
        writer.Write(body);
        if (body.Length % 2 == 1) { writer.Write((byte)0); }
    }

    /// <summary>ファイルへ書きます。必要な親ディレクトリを作ります。</summary>
    internal static string WriteWave(string path, int sampleRate, int channels, float[] samples, WaveSampleFormat format = WaveSampleFormat.Float32)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Wave(sampleRate, channels, format, samples));
        return path;
    }

    /// <summary>seedから決まる、同じ長さの雑音です。値は±0.5の範囲です。</summary>
    internal static float[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        float[] samples = new float[count];
        for (int index = 0; index < count; index++) { samples[index] = (float)(random.NextDouble() - 0.5); }
        return samples;
    }

    /// <summary>リトルエンディアンのfloat32のバイト列（参照PCM）を、サンプル列へ読みます。</summary>
    internal static float[] ReadFloat32(byte[] bytes)
    {
        float[] samples = new float[bytes.Length / sizeof(float)];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float)));
        }
        return samples;
    }
}
