#nullable enable
using System;
using System.Numerics;
using Ribbit.Math;

namespace Ribbit.BMS;

/// <summary>100ns tick を 2^32 分割した全曲共通の再生時刻です。音声frameへの変換まで精度を保持します。</summary>
public readonly record struct PlaybackTime(BigInteger Subticks) : IComparable<PlaybackTime>
{
    /// <summary>一つのtickに含まれる固定刻み数です。</summary>
    public static BigInteger SubticksPerTick => BigInteger.One << 32;
    /// <summary>BMSの整数tickを厳密に昇格します。</summary>
    public static PlaybackTime FromTimeSpan(TimeSpan time) => new((BigInteger)time.Ticks << 32);
    /// <summary>局所有理tickの寄与を最近傍偶数で一度だけ固定刻みへ変換します。</summary>
    internal static PlaybackTime FromTicks(Fraction ticks) => new(RoundEven(ticks.Numerator << 32, ticks.Denominator));
    /// <summary>表示と既存UI API向けに最近傍偶数の整数tickを返します。</summary>
    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks(checked((long)RoundEven(Subticks, SubticksPerTick)));
    /// <summary>絶対出力境界を途中で整数tickにせず、最近傍偶数のframeへ変換します。</summary>
    internal long ToOutputFrame(int rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        return checked((long)RoundEven(Subticks * rate, (BigInteger)TimeSpan.TicksPerSecond << 32));
    }
    /// <summary>非負音源境界を直接source格子へ切り捨てます。</summary>
    internal long ToSourceFrame(int rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        if (Subticks.Sign < 0) throw new ArgumentOutOfRangeException(nameof(Subticks));
        return checked((long)(Subticks * rate / ((BigInteger)TimeSpan.TicksPerSecond << 32)));
    }
    /// <summary>符号付き整数比を最近傍偶数へ丸めます。</summary>
    internal static BigInteger RoundEven(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        int comparison = (BigInteger.Abs(remainder) * 2).CompareTo(denominator);
        return comparison > 0 || (comparison == 0 && !quotient.IsEven)
            ? quotient + numerator.Sign : quotient;
    }
    /// <inheritdoc/>
    public int CompareTo(PlaybackTime other) => Subticks.CompareTo(other.Subticks);
    /// <summary>固定刻み上で寄与を加算します。</summary>
    public static PlaybackTime operator +(PlaybackTime left, PlaybackTime right) => new(left.Subticks + right.Subticks);
    /// <summary>同じ全曲時計上の時刻差を求めます。</summary>
    public static PlaybackTime operator -(PlaybackTime left, PlaybackTime right) => new(left.Subticks - right.Subticks);
}
