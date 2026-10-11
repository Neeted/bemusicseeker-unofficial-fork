using System;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Math;

namespace BeMusicSeeker.Tests;

/// <summary>有限有理数の正規形、任意精度演算と出力境界を検証します。</summary>
[TestClass]
public sealed class FractionTests
{
    /// <summary>
    /// 保証：零・負の分母・約分値を同じ正規形とハッシュで扱い、零除算を拒否して比較や集合の同値性を壊さない。
    /// </summary>
    [TestMethod]
    public void DefaultZeroSignsAndHashesUseNormalizedValues()
    {
        Fraction zero = default;
        Assert.AreEqual(BigInteger.Zero, zero.Numerator);
        Assert.AreEqual(BigInteger.One, zero.Denominator);
        Assert.AreEqual(new Fraction(0, -12), zero);
        Assert.AreEqual(new Fraction(-2, 6), new Fraction(2, -6));
        Assert.AreEqual(new Fraction(2, 6).GetHashCode(), new Fraction(1, 3).GetHashCode());
        Assert.AreEqual(zero.GetHashCode(), new Fraction(0).GetHashCode());
        Assert.IsTrue(new Fraction(-1, 3) < zero);
        Assert.ThrowsException<DivideByZeroException>(() => new Fraction(1, 0));
        Assert.ThrowsException<DivideByZeroException>(() => new Fraction(1) / zero);
    }

    /// <summary>
    /// 保証：Int64最小値と負数を任意精度で保持し、整数変換は商だけを範囲判定して途中計算の桁あふれや丸め方向の誤りを防ぐ。
    /// </summary>
    [TestMethod]
    public void LongMinimumAndTruncationPreserveValuesAndCheckOnlyTheQuotient()
    {
        Assert.AreEqual(long.MinValue, new Fraction(long.MinValue).ToInt64());
        Assert.AreEqual(new Fraction(BigInteger.One << 63, 1), -new Fraction(long.MinValue));
        Assert.AreEqual(new Fraction(1, BigInteger.One << 63), new Fraction(-1, long.MinValue));
        Assert.AreEqual(long.MaxValue, new Fraction(2 * (BigInteger)long.MaxValue + 1, 2).ToInt64());
        Assert.AreEqual(-1L, new Fraction(-3, 2).ToInt64());
        Assert.ThrowsException<OverflowException>(() => new Fraction((BigInteger)long.MaxValue + 1, 1).ToInt64());
    }

    /// <summary>
    /// 保証：128ビットを超える有理数の四則演算と比較が独立した代数上の値に一致し、固定幅の途中計算による桁あふれを検出する。
    /// </summary>
    [TestMethod]
    public void ArithmeticBeyond128BitsMatchesIndependentAlgebra()
    {
        BigInteger a = BigInteger.One << 160;
        var x = new Fraction(a + 1, a - 1);
        var y = new Fraction(a - 1, a + 1);
        Assert.AreEqual(new Fraction(1), x * y);
        Assert.AreEqual(new Fraction(2 * (a * a + 1), a * a - 1), x + y);
        Assert.AreEqual(new Fraction(4 * a, a * a - 1), x - y);
        Assert.AreEqual(new Fraction((a + 1) * (a + 1), (a - 1) * (a - 1)), x / y);
        Assert.IsTrue(x > y);
        Assert.IsFalse(x == y);
    }

    /// <summary>
    /// 保証：decimalの小数桁を正確に保持し、巨大な分子・分母の比を有限doubleへ変換して途中の無限大化を防ぐ。
    /// </summary>
    [TestMethod]
    public void DecimalScaleIsExactAndHugeRatiosRemainFiniteAsDouble()
    {
        Assert.AreEqual(new Fraction(1234567890123456789, BigInteger.Pow(10, 18)), new Fraction(1.234567890123456789m));
        BigInteger large = BigInteger.One << 4096;
        double result = new Fraction(3 * large + 1, 2 * large + 1).ToDouble();
        Assert.IsTrue(double.IsFinite(result));
        Assert.AreEqual(1.5d, result, 1e-14);
    }
}
