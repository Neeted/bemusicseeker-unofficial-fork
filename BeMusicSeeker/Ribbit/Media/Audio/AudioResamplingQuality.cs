using System;

namespace Ribbit.Media.Audio;

/// <summary>BASSのサンプルレート変換で利用できる品質値を定義します。</summary>
internal static class AudioResamplingQuality
{
    /// <summary>設定が存在しない場合に使う品質値です。</summary>
    internal const int Default = 4;

    /// <summary>利用できる最小の品質値です。</summary>
    internal const int Minimum = 2;

    /// <summary>利用できる最大の品質値です。</summary>
    internal const int Maximum = 6;

    /// <summary>品質値を丸めずに利用可能か判定します。</summary>
    internal static bool IsValid(int quality) => quality is >= Minimum and <= Maximum;

    /// <summary>BASSがサポートする範囲外の品質値を拒否します。</summary>
    internal static int Validate(int quality, string parameterName = "quality")
    {
        if (!IsValid(quality))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                quality,
                $"Audio sample-rate conversion quality must be between {Minimum} and {Maximum}.");
        }

        return quality;
    }

    /// <summary>品質値に対応するsinc点数を返します。</summary>
    internal static int GetSincPointCount(int quality) => 1 << (Validate(quality) + 2);
}
