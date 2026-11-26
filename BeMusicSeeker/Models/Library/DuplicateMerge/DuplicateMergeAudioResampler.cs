using System;

namespace BeMusicSeeker.Models;

/// <summary>
/// WAVとOGGの同等性の判定で、44100HzのOGGを48000Hzへ変換します。先頭の時刻と音量を保ち、帯域を制限します。
/// 長さの伸縮と遅延の探索は行いません。純粋な計算です。
/// </summary>
internal static class DuplicateMergeAudioResampler
{
    private const int Taps = DuplicateMergeSafetyPolicy.AudioResampleTaps;
    private const int Phases = DuplicateMergeSafetyPolicy.AudioResampleOutputUnits;

    /// <summary>位相ごとの32点の係数（位相 × 点）です。各位相の係数の和が1になるよう正規化しています。</summary>
    private static readonly double[] Coefficients = CreateCoefficients();

    /// <summary>
    /// チャンネルを交互に並べた入力を、<paramref name="outputFrames"/> フレームの48000Hzの列へ変換します。倍精度で求め、チャンネルごとに独立です。
    /// 出力のフレーム m は、入力の位置 <c>t = m·147/160</c> を中心に、<c>⌊t⌋ − 15</c> から <c>⌊t⌋ + 16</c> までの32点の重み付き和で、範囲外の入力は0です。
    /// </summary>
    /// <param name="input">44100Hzのサンプル。入力は書き換えません。</param>
    /// <param name="channels">チャンネル数。</param>
    /// <param name="outputFrames">出力のフレーム数（WAVのフレーム数）。</param>
    internal static double[] Convert(float[] input, int channels, int outputFrames)
    {
        long inputFrames = input.Length / channels;
        double[] output = new double[(long)outputFrames * channels];
        for (int frame = 0; frame < outputFrames; frame++)
        {
            long scaled = (long)frame * DuplicateMergeSafetyPolicy.AudioResampleInputUnits;
            long floor = scaled / Phases;
            ReadOnlySpan<double> weights = Coefficients.AsSpan((int)(scaled % Phases) * Taps, Taps);
            long first = floor - (Taps / 2 - 1);
            for (int channel = 0; channel < channels; channel++)
            {
                double sum = 0;
                for (int tap = 0; tap < Taps; tap++)
                {
                    long position = first + tap;
                    if (position >= 0 && position < inputFrames) { sum += weights[tap] * input[position * channels + channel]; }
                }
                output[(long)frame * channels + channel] = sum;
            }
        }
        return output;
    }

    private static double[] CreateCoefficients()
    {
        double[] table = new double[Phases * Taps];
        double beta = DuplicateMergeSafetyPolicy.AudioResampleKaiserBeta;
        double normalizer = BesselI0(beta);
        int half = Taps / 2;
        for (int phase = 0; phase < Phases; phase++)
        {
            double sum = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                // 入力の位置 k = ⌊t⌋ − 15 + tap に対する t − k。
                double x = (double)phase / Phases + (half - 1) - tap;
                double value = 0;
                if (Math.Abs(x) < half)
                {
                    double sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    double ratio = x / half;
                    value = sinc * BesselI0(beta * Math.Sqrt(1 - ratio * ratio)) / normalizer;
                }
                table[phase * Taps + tap] = value;
                sum += value;
            }
            for (int tap = 0; tap < Taps; tap++) { table[phase * Taps + tap] /= sum; }
        }
        return table;
    }

    /// <summary>第1種0次変形ベッセル関数 I₀(x) を、級数 <c>Σ ((x/2)^k / k!)²</c> で求めます。</summary>
    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, quarter = x * x / 4;
        for (int k = 1; k < 200; k++)
        {
            term *= quarter / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17) { break; }
        }
        return sum;
    }
}
