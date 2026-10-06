#nullable enable
using System;
using Ribbit.Math;

namespace Ribbit.BMS;

/// <summary>BMS入力の有限値と明示的な正∞を区別します。未定義は外側のnullableで表します。</summary>
public readonly struct BmsNumber : IComparable<BmsNumber>, IEquatable<BmsNumber>
{
    private readonly Fraction finite;
    /// <summary>有限入力を旧doubleパースが正∞に分類した印です。明示∞STOPの旧ゼロ作用だけに使います。</summary>
    internal bool WasParsedAsPositiveInfinity { get; }
    /// <summary>明示的な正∞の場合だけtrueです。</summary>
    public bool IsPositiveInfinity { get; }
    /// <summary>有限の厳密値です。正∞の場合はnullです。</summary>
    public Fraction? FiniteValue => IsPositiveInfinity ? null : finite;
    /// <summary>BMS境界の明示的な正∞です。</summary>
    public static BmsNumber PositiveInfinity => new(true);
    /// <summary>有限値を厳密に保持します。</summary>
    public BmsNumber(Fraction value) : this(value, false) { }
    /// <summary>有限の厳密値と特殊STOP受理に必要な旧入力分類を保持します。分類は数学的等値に含めません。</summary>
    internal BmsNumber(Fraction value, bool wasParsedAsPositiveInfinity)
    {
        finite = value;
        IsPositiveInfinity = false;
        WasParsedAsPositiveInfinity = wasParsedAsPositiveInfinity;
    }
    private BmsNumber(bool infinity) { finite = default; IsPositiveInfinity = infinity; WasParsedAsPositiveInfinity = false; }
    /// <summary>表示・再生接続だけに用いる近似値です。</summary>
    public double ToDouble() => IsPositiveInfinity ? double.PositiveInfinity : finite.ToDouble();
    /// <summary>有限値は厳密に比較し、正∞はすべての有限値より大きいとします。</summary>
    public int CompareTo(BmsNumber other) => IsPositiveInfinity ? (other.IsPositiveInfinity ? 0 : 1) : (other.IsPositiveInfinity ? -1 : finite.CompareTo(other.finite));
    /// <inheritdoc/>
    public bool Equals(BmsNumber other) => IsPositiveInfinity == other.IsPositiveInfinity && (IsPositiveInfinity || finite.Equals(other.finite));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BmsNumber other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => IsPositiveInfinity ? int.MaxValue : finite.GetHashCode();
    /// <summary>有限値をBMS境界へ接続します。</summary>
    public static implicit operator BmsNumber(Fraction value) => new(value);
    /// <summary>整数をBMS境界へ接続します。</summary>
    public static implicit operator BmsNumber(long value) => new(new Fraction(value));
    /// <summary>等値を比較します。</summary>
    public static bool operator ==(BmsNumber left, BmsNumber right) => left.Equals(right);
    /// <summary>不等値を比較します。</summary>
    public static bool operator !=(BmsNumber left, BmsNumber right) => !left.Equals(right);
}
