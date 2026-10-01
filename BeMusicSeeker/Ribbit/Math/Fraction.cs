#nullable enable
using System;
using System.Globalization;
using System.Numerics;

namespace Ribbit.Math;

/// <summary>任意精度の既約な有限有理数です。既定値も論理的な 0/1 を表します。</summary>
public readonly struct Fraction : IComparable<Fraction>, IEquatable<Fraction>
{
    private readonly BigInteger numerator;
    private readonly BigInteger denominator;

    /// <summary>符号を含む既約分子です。</summary>
    public BigInteger Numerator => numerator;
    /// <summary>常に正の既約分母です。</summary>
    public BigInteger Denominator => denominator.IsZero ? BigInteger.One : denominator;
    /// <summary>有限のゼロです。</summary>
    public static Fraction Zero => default;

    /// <summary>整数を値の変更なしで受け取ります。</summary>
    public Fraction(long value) : this(new BigInteger(value), BigInteger.One, true) { }

    /// <summary>分母ゼロを拒否し、符号と既約性を正規化します。</summary>
    public Fraction(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) throw new DivideByZeroException();
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        this.numerator = numerator / gcd;
        this.denominator = denominator / gcd;
    }

    /// <summary>decimal の96bit係数とscaleを厳密に取り込みます。</summary>
    public Fraction(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger coefficient = (uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        if (bits[3] < 0) coefficient = -coefficient;
        this = new Fraction(coefficient, BigInteger.Pow(10, (bits[3] >> 16) & 255));
    }

    // 呼出元が既約性を証明した結果には再度GCDを適用しません。
    private Fraction(BigInteger numerator, BigInteger denominator, bool reduced)
    {
        this.numerator = numerator;
        this.denominator = numerator.IsZero ? BigInteger.One : denominator;
    }

    /// <summary>整数除算でゼロ方向へ切り捨てた後にlong範囲を検査します。</summary>
    public long ToInt64() => checked((long)(Numerator / Denominator));

    /// <summary>分子分母の上位bitと相対scaleから表示用のdoubleを生成します。</summary>
    public double ToDouble()
    {
        if (numerator.IsZero) return 0;
        var absolute = BigInteger.Abs(numerator);
        long numeratorShift = System.Math.Max(0, absolute.GetBitLength() - 54);
        long denominatorShift = System.Math.Max(0, Denominator.GetBitLength() - 54);
        long exponent = numeratorShift - denominatorShift;
        if (exponent > 1100) return numerator.Sign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        if (exponent < -1100) return numerator.Sign > 0 ? 0d : -0d;
        double ratio = (double)(absolute >> checked((int)numeratorShift)) / (double)(Denominator >> checked((int)denominatorShift));
        return numerator.Sign * System.Math.ScaleB(ratio, (int)exponent);
    }

    /// <summary>正規形の分子と分母を読み取って等値を判定します。</summary>
    public bool Equals(Fraction other) => Numerator == other.Numerator && Denominator == other.Denominator;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Fraction other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    /// <summary>整数の交差積だけで大小を比較します。</summary>
    public int CompareTo(Fraction other) => Denominator == other.Denominator
        ? Numerator.CompareTo(other.Numerator)
        : (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    /// <summary>診断用の既約整数比を返します。</summary>
    public override string ToString() => Denominator.IsOne ? Numerator.ToString(CultureInfo.InvariantCulture) : string.Concat(Numerator.ToString(CultureInfo.InvariantCulture), "/", Denominator.ToString(CultureInfo.InvariantCulture));

    /// <summary>分母のGCDと、和の分子との残りの共通因数を除去します。</summary>
    public static Fraction operator +(Fraction left, Fraction right)
    {
        if (left.Numerator.IsZero) return right;
        if (right.Numerator.IsZero) return left;
        var gcd = BigInteger.GreatestCommonDivisor(left.Denominator, right.Denominator);
        BigInteger leftScale = right.Denominator / gcd;
        BigInteger rightScale = left.Denominator / gcd;
        BigInteger sum = left.Numerator * leftScale + right.Numerator * rightScale;
        var remaining = BigInteger.GreatestCommonDivisor(BigInteger.Abs(sum), gcd);
        return new Fraction(sum / remaining, rightScale * (right.Denominator / remaining), true);
    }
    /// <summary>既約性を保ったまま符号を反転します。</summary>
    public static Fraction operator -(Fraction value) => new(-value.Numerator, value.Denominator, true);
    /// <summary>有限有理数の厳密な差です。</summary>
    public static Fraction operator -(Fraction left, Fraction right) => left + -right;
    /// <summary>交差約分を行ってから乗算します。</summary>
    public static Fraction operator *(Fraction left, Fraction right)
    {
        if (left.Numerator.IsZero || right.Numerator.IsZero) return Zero;
        var a = BigInteger.GreatestCommonDivisor(BigInteger.Abs(left.Numerator), right.Denominator);
        var b = BigInteger.GreatestCommonDivisor(BigInteger.Abs(right.Numerator), left.Denominator);
        return new Fraction((left.Numerator / a) * (right.Numerator / b), (left.Denominator / b) * (right.Denominator / a), true);
    }
    /// <summary>ゼロ除算を拒否し、交差約分してから除算します。</summary>
    public static Fraction operator /(Fraction left, Fraction right)
    {
        if (right.Numerator.IsZero) throw new DivideByZeroException();
        if (left.Numerator.IsZero) return Zero;
        var a = BigInteger.GreatestCommonDivisor(BigInteger.Abs(left.Numerator), BigInteger.Abs(right.Numerator));
        var b = BigInteger.GreatestCommonDivisor(left.Denominator, right.Denominator);
        BigInteger n = (left.Numerator / a) * (right.Denominator / b);
        BigInteger d = (left.Denominator / b) * (right.Numerator / a);
        return d.Sign < 0 ? new Fraction(-n, -d, true) : new Fraction(n, d, true);
    }
    /// <summary>整数を厳密に変換します。</summary>
    public static implicit operator Fraction(long value) => new(value);
    /// <summary>decimalを厳密に変換します。</summary>
    public static implicit operator Fraction(decimal value) => new(value);
    /// <summary>正規形の等値です。</summary>
    public static bool operator ==(Fraction left, Fraction right) => left.Equals(right);
    /// <summary>正規形の不等値です。</summary>
    public static bool operator !=(Fraction left, Fraction right) => !left.Equals(right);
    /// <summary>厳密な小なり比較です。</summary>
    public static bool operator <(Fraction left, Fraction right) => left.CompareTo(right) < 0;
    /// <summary>厳密な大なり比較です。</summary>
    public static bool operator >(Fraction left, Fraction right) => left.CompareTo(right) > 0;
    /// <summary>厳密な以下比較です。</summary>
    public static bool operator <=(Fraction left, Fraction right) => left.CompareTo(right) <= 0;
    /// <summary>厳密な以上比較です。</summary>
    public static bool operator >=(Fraction left, Fraction right) => left.CompareTo(right) >= 0;
}
