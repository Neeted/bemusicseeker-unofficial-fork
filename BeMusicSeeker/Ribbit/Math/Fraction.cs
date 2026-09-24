using System;
using System.Globalization;

namespace Ribbit.Math;

[Serializable]
public struct Fraction : IComparable, IFormattable
{
    private enum Indeterminates
    {
        NaN = 0,
        PositiveInfinity = 1,
        NegativeInfinity = -1
    }

    public static readonly Fraction NaN = new(Indeterminates.NaN);

    public static readonly Fraction PositiveInfinity = new(Indeterminates.PositiveInfinity);

    public static readonly Fraction NegativeInfinity = new(Indeterminates.NegativeInfinity);

    public static readonly Fraction Zero = new(0L, 1L);

    public static readonly Fraction Epsilon = new(1L, long.MaxValue);

    private static readonly double EpsilonDouble = 1.0842021724855044E-19;

    private static readonly decimal EpsilonDecimal = 0.0000000000000000001084202172m;

    public static readonly Fraction MaxValue = new(long.MaxValue, 1L);

    public static readonly Fraction MinValue = new(long.MinValue, 1L);

    private long m_Numerator;

    private long m_Denominator;

    public long Numerator
    {
        readonly get
        {
            return m_Numerator;
        }
        set
        {
            m_Numerator = value;
        }
    }

    public long Denominator
    {
        readonly get
        {
            return m_Denominator;
        }
        set
        {
            m_Denominator = value;
        }
    }

    public Fraction(long wholeNumber)
    {
        if (wholeNumber == long.MinValue)
        {
            wholeNumber++;
        }
        m_Numerator = wholeNumber;
        m_Denominator = 1L;
    }

    public Fraction(double floatingPointNumber)
    {
        this = ToFraction(floatingPointNumber);
    }

    public Fraction(decimal floatingPointNumber)
    {
        this = ToFraction(floatingPointNumber);
    }

    public Fraction(string inValue)
    {
        this = ToFraction(inValue);
    }

    public Fraction(long numerator, long denominator)
    {
        if (numerator == long.MinValue)
        {
            numerator++;
        }
        if (denominator == long.MinValue)
        {
            denominator++;
        }
        m_Numerator = numerator;
        m_Denominator = denominator;
        ReduceFraction(ref this);
    }

    private Fraction(Indeterminates type)
    {
        m_Numerator = (long)type;
        m_Denominator = 0L;
    }

    public readonly int ToInt32()
    {
        if (m_Denominator == 0L)
        {
            throw new FractionException($"Cannot convert {IndeterminateTypeName(m_Numerator)} to Int32", new NotFiniteNumberException());
        }
        long num = m_Numerator / m_Denominator;
        if (num > int.MaxValue || num < int.MinValue)
        {
            throw new FractionException("Cannot convert to Int32", new OverflowException());
        }
        return (int)num;
    }

    public readonly long ToInt64()
    {
        if (m_Denominator == 0L)
        {
            throw new FractionException($"Cannot convert {IndeterminateTypeName(m_Numerator)} to Int64", new NotFiniteNumberException());
        }
        return m_Numerator / m_Denominator;
    }

    public readonly double ToDouble()
    {
        if (m_Denominator == 1)
        {
            return m_Numerator;
        }
        if (m_Denominator == 0L)
        {
            return NormalizeIndeterminate(m_Numerator) switch
            {
                Indeterminates.NegativeInfinity => double.NegativeInfinity,
                Indeterminates.PositiveInfinity => double.PositiveInfinity,
                _ => double.NaN,
            };
        }
        return (double)m_Numerator / (double)m_Denominator;
    }

    public readonly decimal ToDecimal()
    {
        if (m_Denominator == 1)
        {
            return m_Numerator;
        }
        if (m_Denominator == 0L)
        {
            return NormalizeIndeterminate(m_Numerator) switch
            {
                Indeterminates.NegativeInfinity => decimal.MinValue,
                Indeterminates.PositiveInfinity => decimal.MaxValue,
                _ => throw new InvalidOperationException(),
            };
        }
        return (decimal)m_Numerator / (decimal)m_Denominator;
    }

    public override string ToString()
    {
        if (m_Denominator == 1)
        {
            return m_Numerator.ToString();
        }
        if (m_Denominator == 0L)
        {
            return IndeterminateTypeName(m_Numerator);
        }
        return m_Numerator + "/" + m_Denominator;
    }

    public static Fraction ToFraction(long inValue)
    {
        return new Fraction(inValue);
    }

    public static Fraction ToFraction(double inValue)
    {
        if (double.IsNaN(inValue))
        {
            return NaN;
        }
        if (double.IsNegativeInfinity(inValue))
        {
            return NegativeInfinity;
        }
        if (double.IsPositiveInfinity(inValue))
        {
            return PositiveInfinity;
        }
        if (inValue == 0.0)
        {
            return Zero;
        }
        if (inValue > 9.223372036854776E+18)
        {
            throw new OverflowException($"Double {inValue} too large");
        }
        if (inValue < -9.223372036854776E+18)
        {
            throw new OverflowException($"Double {inValue} too small");
        }
        if (0.0 - EpsilonDouble < inValue && inValue < EpsilonDouble)
        {
            throw new ArithmeticException($"Double {inValue} cannot be represented");
        }
        return Approximate(inValue);
    }

    public static Fraction ToFraction(decimal inValue)
    {
        if (inValue == 0m)
        {
            return Zero;
        }
        if (inValue > 9223372036854775807m)
        {
            throw new OverflowException($"Decimal {inValue} too large");
        }
        if (inValue < -9223372036854775808m)
        {
            throw new OverflowException($"Decimal {inValue} too small");
        }
        if (-EpsilonDecimal < inValue && inValue < EpsilonDecimal)
        {
            throw new ArithmeticException($"Decimal {inValue} cannot be represented");
        }
        return Approximate(inValue);
    }

    public static Fraction ToFraction(string inValue)
    {
        if (inValue == null || inValue == string.Empty)
        {
            throw new ArgumentNullException("inValue");
        }
        NumberFormatInfo currentInfo = NumberFormatInfo.CurrentInfo;
        string text = inValue.Trim();
        if (text == currentInfo.NaNSymbol)
        {
            return NaN;
        }
        if (text == currentInfo.PositiveInfinitySymbol)
        {
            return PositiveInfinity;
        }
        if (text == currentInfo.NegativeInfinitySymbol)
        {
            return NegativeInfinity;
        }
        int num = inValue.IndexOf('/');
        if (num > -1)
        {
            long numerator = Convert.ToInt64(inValue.Substring(0, num));
            long denominator = Convert.ToInt64(inValue.Substring(num + 1));
            return new Fraction(numerator, denominator);
        }
        if (inValue.IndexOf(currentInfo.CurrencyDecimalSeparator) > -1)
        {
            return new Fraction(Convert.ToDouble(inValue));
        }
        return new Fraction(Convert.ToInt64(inValue));
    }

    public readonly bool IsNaN()
    {
        if (m_Denominator == 0L && NormalizeIndeterminate(m_Numerator) == Indeterminates.NaN)
        {
            return true;
        }
        return false;
    }

    public readonly bool IsDefault()
    {
        if (m_Numerator == m_Denominator)
        {
            return m_Numerator == 0;
        }
        return false;
    }

    public readonly bool IsInfinity()
    {
        if (m_Denominator == 0L && NormalizeIndeterminate(m_Numerator) != Indeterminates.NaN)
        {
            return true;
        }
        return false;
    }

    public readonly bool IsPositiveInfinity()
    {
        if (m_Denominator == 0L && NormalizeIndeterminate(m_Numerator) == Indeterminates.PositiveInfinity)
        {
            return true;
        }
        return false;
    }

    public readonly bool IsNegativeInfinity()
    {
        if (m_Denominator == 0L && NormalizeIndeterminate(m_Numerator) == Indeterminates.NegativeInfinity)
        {
            return true;
        }
        return false;
    }

    public Fraction Inverse()
    {
        return new Fraction
        {
            m_Numerator = m_Denominator,
            m_Denominator = m_Numerator
        };
    }

    public static Fraction Inverted(long value)
    {
        return new Fraction(value).Inverse();
    }

    public static Fraction Inverted(double value)
    {
        return new Fraction(value).Inverse();
    }

    public static Fraction operator -(Fraction left)
    {
        return Negate(left);
    }

    public static Fraction operator +(Fraction left, Fraction right)
    {
        return Add(left, right);
    }

    public static Fraction operator +(long left, Fraction right)
    {
        return Add(new Fraction(left), right);
    }

    public static Fraction operator +(Fraction left, long right)
    {
        return Add(left, new Fraction(right));
    }

    public static Fraction operator +(double left, Fraction right)
    {
        return Add(ToFraction(left), right);
    }

    public static Fraction operator +(Fraction left, double right)
    {
        return Add(left, ToFraction(right));
    }

    public static Fraction operator -(Fraction left, Fraction right)
    {
        return Add(left, -right);
    }

    public static Fraction operator -(long left, Fraction right)
    {
        return Add(new Fraction(left), -right);
    }

    public static Fraction operator -(Fraction left, long right)
    {
        return Add(left, new Fraction(-right));
    }

    public static Fraction operator -(double left, Fraction right)
    {
        return Add(ToFraction(left), -right);
    }

    public static Fraction operator -(Fraction left, double right)
    {
        return Add(left, ToFraction(0.0 - right));
    }

    public static Fraction operator *(Fraction left, Fraction right)
    {
        return Multiply(left, right);
    }

    public static Fraction operator *(long left, Fraction right)
    {
        return Multiply(new Fraction(left), right);
    }

    public static Fraction operator *(Fraction left, long right)
    {
        return Multiply(left, new Fraction(right));
    }

    public static Fraction operator *(double left, Fraction right)
    {
        return Multiply(ToFraction(left), right);
    }

    public static Fraction operator *(Fraction left, double right)
    {
        return Multiply(left, ToFraction(right));
    }

    public static Fraction operator /(Fraction left, Fraction right)
    {
        return Multiply(left, right.Inverse());
    }

    public static Fraction operator /(long left, Fraction right)
    {
        return Multiply(new Fraction(left), right.Inverse());
    }

    public static Fraction operator /(Fraction left, long right)
    {
        return Multiply(left, Inverted(right));
    }

    public static Fraction operator /(double left, Fraction right)
    {
        return Multiply(ToFraction(left), right.Inverse());
    }

    public static Fraction operator /(Fraction left, double right)
    {
        return Multiply(left, Inverted(right));
    }

    public static Fraction operator %(Fraction left, Fraction right)
    {
        return Modulus(left, right);
    }

    public static Fraction operator %(long left, Fraction right)
    {
        return Modulus(new Fraction(left), right);
    }

    public static Fraction operator %(Fraction left, long right)
    {
        return Modulus(left, right);
    }

    public static Fraction operator %(double left, Fraction right)
    {
        return Modulus(ToFraction(left), right);
    }

    public static Fraction operator %(Fraction left, double right)
    {
        return Modulus(left, right);
    }

    public static bool operator ==(Fraction left, Fraction right)
    {
        return left.CompareEquality(right, notEqualCheck: false);
    }

    public static bool operator ==(Fraction left, long right)
    {
        return left.CompareEquality(new Fraction(right), notEqualCheck: false);
    }

    public static bool operator ==(Fraction left, double right)
    {
        return left.CompareEquality(new Fraction(right), notEqualCheck: false);
    }

    public static bool operator !=(Fraction left, Fraction right)
    {
        return left.CompareEquality(right, notEqualCheck: true);
    }

    public static bool operator !=(Fraction left, long right)
    {
        return left.CompareEquality(new Fraction(right), notEqualCheck: true);
    }

    public static bool operator !=(Fraction left, double right)
    {
        return left.CompareEquality(new Fraction(right), notEqualCheck: true);
    }

    public static bool operator <(Fraction left, Fraction right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(Fraction left, Fraction right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(Fraction left, Fraction right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(Fraction left, Fraction right)
    {
        return left.CompareTo(right) >= 0;
    }

    public static implicit operator Fraction(long value)
    {
        return new Fraction(value);
    }

    public static implicit operator Fraction(double value)
    {
        return new Fraction(value);
    }

    public static implicit operator Fraction(decimal value)
    {
        return new Fraction(value);
    }

    public static implicit operator Fraction(string value)
    {
        return new Fraction(value);
    }

    public static explicit operator int(Fraction frac)
    {
        return frac.ToInt32();
    }

    public static explicit operator long(Fraction frac)
    {
        return frac.ToInt64();
    }

    public static explicit operator double(Fraction frac)
    {
        return frac.ToDouble();
    }

    public static explicit operator decimal(Fraction frac)
    {
        return frac.ToDecimal();
    }

    public static explicit operator string(Fraction frac)
    {
        return frac.ToString();
    }

    public override bool Equals(object obj)
    {
        if (obj == null || obj is not Fraction)
        {
            return false;
        }
        try
        {
            var right = (Fraction)obj;
            return CompareEquality(right, notEqualCheck: false);
        }
        catch
        {
            return false;
        }
    }

    public override int GetHashCode()
    {
        ReduceFraction(ref this);
        int hashCode = m_Numerator.GetHashCode();
        int hashCode2 = m_Denominator.GetHashCode();
        return hashCode ^ hashCode2;
    }

    public readonly int CompareTo(object obj)
    {
        if (obj == null)
        {
            return 1;
        }
        Fraction right;
        if (obj is Fraction)
        {
            right = (Fraction)obj;
        }
        else if (obj is long)
        {
            right = (long)obj;
        }
        else if (obj is double)
        {
            right = (double)obj;
        }
        else
        {
            if (obj is not string)
            {
                throw new ArgumentException("Must be convertible to Fraction", "obj");
            }
            right = (string)obj;
        }
        return CompareTo(right);
    }

    public readonly int CompareTo(Fraction right)
    {
        if (m_Denominator == 0L)
        {
            return IndeterminantCompare(NormalizeIndeterminate(m_Numerator), right);
        }
        if (right.m_Denominator == 0L)
        {
            return -IndeterminantCompare(NormalizeIndeterminate(right.m_Numerator), this);
        }
        checked
        {
            try
            {
                long num = m_Numerator * right.m_Denominator;
                long num2 = m_Denominator * right.m_Numerator;
                if (num < num2)
                {
                    return -1;
                }
                if (num > num2)
                {
                    return 1;
                }
                return 0;
            }
            catch (Exception innerException)
            {
                try
                {
                    decimal num3 = ToDecimal();
                    decimal num4 = ToDecimal();
                    if (num3 < num4)
                    {
                        return -1;
                    }
                    if (num3 > num4)
                    {
                        return 1;
                    }
                    return 0;
                }
                catch
                {
                    throw new FractionException($"CompareTo({this}, {right}) error", innerException);
                }
            }
        }
    }

    string IFormattable.ToString(string format, IFormatProvider formatProvider)
    {
        return m_Numerator.ToString(format, formatProvider) + "/" + m_Denominator.ToString(format, formatProvider);
    }

    public static void ReduceFraction(ref Fraction frac)
    {
        if (frac.m_Denominator == 0L)
        {
            frac.m_Numerator = (long)NormalizeIndeterminate(frac.m_Numerator);
            return;
        }
        if (frac.m_Numerator == 0L)
        {
            frac.m_Denominator = 1L;
            return;
        }
        long num = Misc.GCD(frac.m_Numerator, frac.m_Denominator);
        frac.m_Numerator /= num;
        frac.m_Denominator /= num;
        if (frac.m_Denominator < 0)
        {
            frac.m_Numerator = -frac.m_Numerator;
            frac.m_Denominator = -frac.m_Denominator;
        }
    }

    public static void CrossReducePair(ref Fraction frac1, ref Fraction frac2)
    {
        if (frac1.m_Denominator != 0L && frac2.m_Denominator != 0L)
        {
            long num = Misc.GCD(frac1.m_Numerator, frac2.m_Denominator);
            frac1.m_Numerator /= num;
            frac2.m_Denominator /= num;
            long num2 = Misc.GCD(frac1.m_Denominator, frac2.m_Numerator);
            frac2.m_Numerator /= num2;
            frac1.m_Denominator /= num2;
        }
    }

    public static Fraction Approximate(double target, long numeratorMax = long.MaxValue, long denominatorMax = long.MaxValue)
    {
        int num = ((target > 0.0) ? 1 : (-1));
        target *= num;
        if (target == 1.0)
        {
            return new Fraction(num, 1L);
        }
        if (target == 0.0)
        {
            return new Fraction(0L, 1L);
        }
        long num2 = 0L;
        long num3 = 1L;
        long num4 = 1L;
        long num5 = 1L;
        if (target > 1.0)
        {
            num2 = 1L;
            num3 = 1L;
            num4 = 1L;
            num5 = 0L;
        }

        bool isOflow = false;
        long outN;
        long outD;
        void action(long fromN, long fromD, long toN, long toD)
        {
            double num13 = (double)toN - target * (double)toD;
            double num14 = ((num13 == 0.0) ? 0.0 : ((target * (double)fromD - (double)fromN) / num13));
            if (num14 <= 0.0)
            {
                isOflow = true;
            }
            long num15 = System.Math.Min((toN == 0L) ? numeratorMax : ((numeratorMax - fromN) / toN), (toD == 0L) ? denominatorMax : ((denominatorMax - fromD) / toD));
            long num16 = (long)System.Math.Min(num14, num15);
            if (num16 < 0 || num14 > 9.223372036854776E+18 || num16 != (long)num14)
            {
                isOflow = true;
            }
            outN = fromN + num16 * toN;
            outD = fromD + num16 * toD;
        }
        long num6 = num2 + num4;
        long num7 = num3 + num5;
        double num8 = (double)num6 / (double)num7;
        if (target == num8)
        {
            return new Fraction(num * num6, num7);
        }
        if (!(target > num8))
        {
            goto IL_00fe;
        }
        goto IL_0153;
    IL_00fe:
        action(num4, num5, num2, num3);
        double num9 = (double)outN / (double)outD;
        if (target == num9)
        {
            return new Fraction(num * outN, outD);
        }
        num4 = outN;
        num5 = outD;
        if (!isOflow)
        {
            goto IL_0153;
        }
        goto IL_01a9;
    IL_01a9:
        double num10 = (double)num2 / (double)num3;
        double num11 = (double)num4 / (double)num5;
        if (System.Math.Abs(target - num10) <= System.Math.Abs(num11 - target))
        {
            return new Fraction(num * num2, num3);
        }
        return new Fraction(num * num4, num5);
    IL_0153:
        action(num2, num3, num4, num5);
        double num12 = (double)outN / (double)outD;
        if (target == num12)
        {
            return new Fraction(num * outN, outD);
        }
        num2 = outN;
        num3 = outD;
        if (!isOflow)
        {
            goto IL_00fe;
        }
        goto IL_01a9;
    }

    public static Fraction Approximate(decimal target, long numeratorMax = long.MaxValue, long denominatorMax = long.MaxValue)
    {
        int num = ((target > 0m) ? 1 : (-1));
        target *= (decimal)num;
        if (target == 1m)
        {
            return new Fraction(num, 1L);
        }
        if (target == 0m)
        {
            return new Fraction(0L, 1L);
        }
        long num2 = 0L;
        long num3 = 1L;
        long num4 = 1L;
        long num5 = 1L;
        if (target > 1m)
        {
            num2 = 1L;
            num3 = 1L;
            num4 = 1L;
            num5 = 0L;
        }

        bool isOflow = false;
        long outN;
        long outD;
        void action(long fromN, long fromD, long toN, long toD)
        {
            decimal num13 = (decimal)toN - target * (decimal)toD;
            decimal num14 = ((num13 == 0m) ? 0m : ((target * (decimal)fromD - (decimal)fromN) / num13));
            if (num14 <= 0m)
            {
                isOflow = true;
            }
            long num15 = System.Math.Min((toN == 0L) ? numeratorMax : ((numeratorMax - fromN) / toN), (toD == 0L) ? denominatorMax : ((denominatorMax - fromD) / toD));
            long num16 = (long)System.Math.Min(num14, num15);
            if (num14 > 9223372036854775807m || num16 != (long)num14)
            {
                isOflow = true;
            }
            outN = fromN + num16 * toN;
            outD = fromD + num16 * toD;
        }
        long num6 = num2 + num4;
        long num7 = num3 + num5;
        decimal num8 = (decimal)num6 / (decimal)num7;
        if (target == num8)
        {
            return new Fraction(num * num6, num7);
        }
        if (!(target > num8))
        {
            goto IL_0120;
        }
        goto IL_0186;
    IL_0120:
        action(num4, num5, num2, num3);
        decimal num9 = (decimal)outN / (decimal)outD;
        if (target == num9)
        {
            return new Fraction(num * outN, outD);
        }
        num4 = outN;
        num5 = outD;
        if (!isOflow)
        {
            goto IL_0186;
        }
        goto IL_01ed;
    IL_01ed:
        decimal num10 = (decimal)num2 / (decimal)num3;
        decimal num11 = (decimal)num4 / (decimal)num5;
        if (System.Math.Abs(target - num10) <= System.Math.Abs(num11 - target))
        {
            return new Fraction(num * num2, num3);
        }
        return new Fraction(num * num4, num5);
    IL_0186:
        action(num2, num3, num4, num5);
        decimal num12 = (decimal)outN / (decimal)outD;
        if (target == num12)
        {
            return new Fraction(num * outN, outD);
        }
        num2 = outN;
        num3 = outD;
        if (!isOflow)
        {
            goto IL_0120;
        }
        goto IL_01ed;
    }

    public static Fraction Approximate(Fraction frac, long numeratorMax = long.MaxValue, long denominatorMax = long.MaxValue)
    {
        if (frac.Numerator > numeratorMax || frac.Denominator > denominatorMax)
        {
            return Approximate(frac.ToDecimal(), numeratorMax, denominatorMax);
        }
        return frac;
    }

    public void Approximate(long numeratorMax = long.MaxValue, long denominatorMax = long.MaxValue)
    {
        this = Approximate(this, numeratorMax, denominatorMax);
    }



    private bool CompareEquality(Fraction right, bool notEqualCheck)
    {
        ReduceFraction(ref this);
        ReduceFraction(ref right);
        if (IsNaN() && right.IsNaN())
        {
            throw new InvalidOperationException("Comparison between NaN and NaN is invalid.\n For default valu check, se IsDefault().");
        }
        if (m_Numerator == right.m_Numerator && m_Denominator == right.m_Denominator)
        {
            return !notEqualCheck;
        }
        return notEqualCheck;
    }

    private static int IndeterminantCompare(Indeterminates leftType, Fraction right)
    {
        switch (leftType)
        {
            case Indeterminates.NaN:
                if (right.IsNaN())
                {
                    return 0;
                }
                if (right.IsNegativeInfinity())
                {
                    return 1;
                }
                return -1;
            case Indeterminates.NegativeInfinity:
                if (right.IsNegativeInfinity())
                {
                    return 0;
                }
                return -1;
            case Indeterminates.PositiveInfinity:
                if (right.IsPositiveInfinity())
                {
                    return 0;
                }
                return 1;
            default:
                return 0;
        }
    }

    private static Fraction Negate(Fraction frac)
    {
        return new Fraction(-frac.m_Numerator, frac.m_Denominator);
    }

    private static Fraction Add(Fraction left, Fraction right)
    {
        if (left.IsNaN() || right.IsNaN())
        {
            return NaN;
        }
        long num = Misc.GCD(left.m_Denominator, right.m_Denominator);
        long num2 = left.m_Denominator / num;
        long num3 = right.m_Denominator / num;
        try
        {
            long denominator;
            long num4;
            if (long.MaxValue / left.m_Denominator >= num3 && long.MinValue / left.m_Denominator <= num3)
            {
                denominator = checked(left.m_Denominator * num3);
                if (left.m_Numerator == 0L || num3 == 0L)
                {
                    num4 = 0L;
                    goto IL_00b6;
                }
                if (long.MaxValue / left.m_Numerator >= num3 && long.MinValue / left.m_Numerator <= num3)
                {
                    num4 = checked(left.m_Numerator * num3);
                    goto IL_00b6;
                }
            }
            goto end_IL_003c;
        IL_00fb:
            long num5;
            return new Fraction(checked(num4 + num5), denominator);
        IL_00b6:
            if (right.m_Numerator == 0L || num2 == 0L)
            {
                num5 = 0L;
                goto IL_00fb;
            }
            if (long.MaxValue / right.m_Numerator >= num2 && long.MinValue / right.m_Numerator <= num2)
            {
                num5 = checked(right.m_Numerator * num2);
                goto IL_00fb;
            }
        end_IL_003c:;
        }
        catch
        {
        }
        return new Fraction(left.ToDecimal() + right.ToDecimal());
    }

    private static Fraction Multiply(Fraction left, Fraction right)
    {
        if (left.IsNaN() || right.IsNaN())
        {
            return NaN;
        }
        CrossReducePair(ref left, ref right);
        try
        {
            if (left.m_Numerator == 0L || right.m_Numerator == 0L)
            {
                return Zero;
            }
            if (long.MaxValue / left.m_Numerator >= right.m_Numerator && long.MinValue / left.m_Numerator <= right.m_Numerator && long.MaxValue / left.m_Denominator >= right.m_Denominator)
            {
                checked
                {
                    long numerator = left.m_Numerator * right.m_Numerator;
                    long denominator = left.m_Denominator * right.m_Denominator;
                    return new Fraction(numerator, denominator);
                }
            }
        }
        catch
        {
        }
        return new Fraction(left.ToDecimal() * right.ToDecimal());
    }

    private static Fraction Modulus(Fraction left, Fraction right)
    {
        if (left.IsNaN() || right.IsNaN())
        {
            return NaN;
        }
        try
        {
            long num = (long)(left / right);
            var fraction = new Fraction(checked(num * right.m_Numerator), right.m_Denominator);
            return left - fraction;
        }
        catch (Exception innerException)
        {
            throw new FractionException("Modulus error", innerException);
        }
    }

    private static string IndeterminateTypeName(long numerator)
    {
        NumberFormatInfo currentInfo = NumberFormatInfo.CurrentInfo;
        return NormalizeIndeterminate(numerator) switch
        {
            Indeterminates.PositiveInfinity => currentInfo.PositiveInfinitySymbol,
            Indeterminates.NegativeInfinity => currentInfo.NegativeInfinitySymbol,
            _ => currentInfo.NaNSymbol,
        };
    }

    private static Indeterminates NormalizeIndeterminate(long numerator)
    {
        return System.Math.Sign(numerator) switch
        {
            1 => Indeterminates.PositiveInfinity,
            -1 => Indeterminates.NegativeInfinity,
            _ => Indeterminates.NaN,
        };
    }
}
