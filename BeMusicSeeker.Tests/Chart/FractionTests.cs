using System;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Math;

namespace BeMusicSeeker.Tests;

/// <summary>有限有理数の正規形、任意精度演算と出力境界を検証します。</summary>
[TestClass]
public sealed class FractionTests
{
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
