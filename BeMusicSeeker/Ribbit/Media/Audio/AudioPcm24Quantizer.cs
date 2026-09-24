using System;

namespace Ribbit.Media.Audio;

/// <summary>float PCMへ24bit整数出力用のTPDFと量子化を適用します。</summary>
internal static class AudioPcm24Quantizer
{
    private const double Scale = 1 << 23;
    private const int Minimum = -(1 << 23);
    private const int Maximum = (1 << 23) - 1;

    /// <summary>
    /// 有限かつ[-1, 1]内のPCMへTPDFを加え、24bit符号付き格子をfloatで正確に表して保存します。
    /// </summary>
    /// <param name="source">量子化するPCMです。</param>
    /// <param name="destination">sourceと同じ長さの格納先です。</param>
    /// <param name="random">sessionが再利用する乱数生成器です。</param>
    internal static void Apply(ReadOnlySpan<float> source, Span<float> destination, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("The destination must fit the source PCM.", nameof(destination));
        }

        for (int sampleIndex = 0; sampleIndex < source.Length; sampleIndex++)
        {
            float sample = source[sampleIndex];
            if (!float.IsFinite(sample) || sample < -1f || sample > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(source), "24-bit encoder input must be finite and within [-1, 1].");
            }

            double dithered = ((double)sample * Scale) + random.NextDouble() - random.NextDouble();
            long rounded = checked((long)System.Math.Round(dithered, MidpointRounding.ToEven));
            int quantized = (int)System.Math.Clamp(rounded, Minimum, Maximum);
            destination[sampleIndex] = (float)(quantized / Scale);
        }
    }
}
