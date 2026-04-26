// JavaDoubleToStringJdk17.cs
// .NET Framework 4.7.2 compatible implementation of JDK 17 Double.toString(double).
// Requires: System.Numerics.dll reference.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class JavaDoubleToStringJdk17
{
    private const int ExpShift = 52;
    private const int ExpBias = 1023;
    private const int MaxSmallBinExp = 62;
    private const int MinSmallBinExp = -21;

    private const ulong SignBitMask = 0x8000000000000000UL;
    private const ulong ExpBitMask = 0x7ff0000000000000UL;
    private const ulong SignifBitMask = 0x000fffffffffffffUL;
    private const ulong FractHob = 0x0010000000000000UL; // 1 << 52
    private const ulong ExpOne = 0x3ff0000000000000UL;

    private static readonly int[] InsignificantDigitsNumber =
    {
        0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 4, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7,
        8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 11, 11, 11,
        12, 12, 12, 12, 13, 13, 13, 14, 14, 14,
        15, 15, 15, 15, 16, 16, 16, 17, 17, 17,
        18, 18, 18, 19
    };

    // Approximately ceil(log2(5^i)), as in JDK 17 FloatingDecimal.
    private static readonly int[] N5Bits =
    {
        0,
        3,
        5,
        7,
        10,
        12,
        14,
        17,
        19,
        21,
        24,
        26,
        28,
        31,
        33,
        35,
        38,
        40,
        42,
        45,
        47,
        49,
        52,
        54,
        56,
        59,
        61
    };

    private static readonly int[] Small5Pow =
    {
        1,
        5,
        25,
        125,
        625,
        3125,
        15625,
        78125,
        390625,
        1953125,
        9765625,
        48828125,
        244140625,
        1220703125
    };

    private static readonly long[] Long5Pow =
    {
        1L,
        5L,
        25L,
        125L,
        625L,
        3125L,
        15625L,
        78125L,
        390625L,
        1953125L,
        9765625L,
        48828125L,
        244140625L,
        1220703125L,
        6103515625L,
        30517578125L,
        152587890625L,
        762939453125L,
        3814697265625L,
        19073486328125L,
        95367431640625L,
        476837158203125L,
        2384185791015625L,
        11920928955078125L,
        59604644775390625L,
        298023223876953125L,
        1490116119384765625L
    };

    // Enough for binary64 output conversion. JDK's FDBigInteger cache uses 340.
    private static readonly BigInteger[] Big5Pow = BuildBig5Pow(350);

    [ThreadStatic] private static char[] t_digits;
    [ThreadStatic] private static char[] t_buffer;

    /// <summary>
    /// Returns the same string as JDK 17's Double.toString(double), including pre-JDK-19 quirks.
    /// </summary>
    public static string ToString(double value)
    {
        ulong bits = DoubleToRawLongBits(value);
        bool isNegative = (bits & SignBitMask) != 0;
        ulong fractBitsU = bits & SignifBitMask;
        int binExp = (int)((bits & ExpBitMask) >> ExpShift);

        if (binExp == 0x7ff)
        {
            if (fractBitsU == 0)
                return isNegative ? "-Infinity" : "Infinity";
            return "NaN";
        }

        if (binExp == 0 && fractBitsU == 0)
            return isNegative ? "-0.0" : "0.0";

        long fractBits;
        int nSignificantBits;

        if (binExp == 0)
        {
            int leadingZeros = NumberOfLeadingZeros(fractBitsU);
            int shift = leadingZeros - (63 - ExpShift);
            fractBitsU <<= shift;
            fractBits = unchecked((long)fractBitsU);
            binExp = 1 - shift;
            nSignificantBits = 64 - leadingZeros;
        }
        else
        {
            fractBits = unchecked((long)(fractBitsU | FractHob));
            nSignificantBits = ExpShift + 1;
        }

        binExp -= ExpBias;

        char[] digits = GetDigitsBuffer();
        int decExponent;
        int nDigits = Dtoa(binExp, fractBits, nSignificantBits, true, digits, out decExponent);

        char[] buffer = GetFormatBuffer();
        int len = FormatJava(isNegative, digits, nDigits, decExponent, buffer);
        return new string(buffer, 0, len);
    }

    private static int Dtoa(
        int binExp,
        long fractBits,
        int nSignificantBits,
        bool isCompatibleFormat,
        char[] digits,
        out int decExponent)
    {
        unchecked
        {
            int tailZeros = NumberOfTrailingZeros((ulong)fractBits);
            int nFractBits = ExpShift + 1 - tailZeros;
            int nTinyBits = Math.Max(0, nFractBits - binExp - 1);

            // Easy integer case from JDK 17 FloatingDecimal.
            if (binExp <= MaxSmallBinExp && binExp >= MinSmallBinExp)
            {
                if (nTinyBits < Long5Pow.Length && nFractBits + N5Bits[nTinyBits] < 64)
                {
                    if (nTinyBits == 0)
                    {
                        int insignificant = binExp > nSignificantBits
                            ? InsignificantDigitsForPow2(binExp - nSignificantBits - 1)
                            : 0;

                        long lvalue = binExp >= ExpShift
                            ? fractBits << (binExp - ExpShift)
                            : (long)((ulong)fractBits >> (ExpShift - binExp));

                        return DevelopLongDigits(0, lvalue, insignificant, digits, out decExponent);
                    }
                }
            }

            int decExp = EstimateDecExp(fractBits, binExp);

            int b5 = Math.Max(0, -decExp);
            int b2 = b5 + nTinyBits + binExp;
            int s5 = Math.Max(0, decExp);
            int s2 = s5 + nTinyBits;
            int m5 = b5;
            int m2 = b2 - nSignificantBits;

            fractBits = (long)((ulong)fractBits >> tailZeros);
            b2 -= nFractBits - 1;

            int common2 = Math.Min(b2, s2);
            b2 -= common2;
            s2 -= common2;
            m2 -= common2;

            // Symmetric stopping test hack from old FloatingDecimal.
            if (nFractBits == 1)
                m2 -= 1;

            if (m2 < 0)
            {
                b2 -= m2;
                s2 -= m2;
                m2 = 0;
            }

            int bbits = nFractBits + b2 + (b5 < N5Bits.Length ? N5Bits[b5] : b5 * 3);
            int tenSbits = s2 + 1 + ((s5 + 1) < N5Bits.Length ? N5Bits[s5 + 1] : (s5 + 1) * 3);

            int nDigits;
            bool low;
            bool high;
            long lowDigitDifference;

            if (bbits < 64 && tenSbits < 64)
            {
                if (bbits < 32 && tenSbits < 32)
                {
                    nDigits = DtoaIntPath(
                        (int)fractBits, b5, b2, s5, s2, m5, m2,
                        isCompatibleFormat, ref decExp, digits,
                        out low, out high, out lowDigitDifference);
                }
                else
                {
                    nDigits = DtoaLongPath(
                        fractBits, b5, b2, s5, s2, m5, m2,
                        isCompatibleFormat, ref decExp, digits,
                        out low, out high, out lowDigitDifference);
                }
            }
            else
            {
                nDigits = DtoaBigIntegerPath(
                    fractBits, b5, b2, s5, s2, m5, m2,
                    isCompatibleFormat, ref decExp, digits,
                    out low, out high, out lowDigitDifference);
            }

            decExponent = decExp + 1;

            // Last digit is rounded according to the stopping condition.
            if (high)
            {
                if (low)
                {
                    if (lowDigitDifference == 0)
                    {
                        if ((digits[nDigits - 1] & 1) != 0)
                            Roundup(digits, nDigits, ref decExponent);
                    }
                    else if (lowDigitDifference > 0)
                    {
                        Roundup(digits, nDigits, ref decExponent);
                    }
                }
                else
                {
                    Roundup(digits, nDigits, ref decExponent);
                }
            }

            return nDigits;
        }
    }

    private static int DtoaIntPath(
        int fractBits,
        int b5,
        int b2,
        int s5,
        int s2,
        int m5,
        int m2,
        bool isCompatibleFormat,
        ref int decExp,
        char[] digits,
        out bool low,
        out bool high,
        out long lowDigitDifference)
    {
        unchecked
        {
            int b = ((fractBits * Small5Pow[b5]) << b2);
            int s = (Small5Pow[s5] << s2);
            int m = (Small5Pow[m5] << m2);
            int tens = s * 10;

            int nDigit = 0;
            int q = b / s;
            b = 10 * (b % s);
            m *= 10;
            low = b < m;
            high = (b + m) > tens;

            if (q == 0 && !high)
            {
                decExp--;
            }
            else
            {
                digits[nDigit++] = (char)('0' + q);
            }

            if (!isCompatibleFormat || decExp < -3 || decExp >= 8)
            {
                high = false;
                low = false;
            }

            while (!low && !high)
            {
                q = b / s;
                b = 10 * (b % s);
                m *= 10;

                if (m > 0)
                {
                    low = b < m;
                    high = (b + m) > tens;
                }
                else
                {
                    low = true;
                    high = true;
                }

                digits[nDigit++] = (char)('0' + q);
            }

            int diff = (b << 1) - tens;
            lowDigitDifference = diff;
            return nDigit;
        }
    }

    private static int DtoaLongPath(
        long fractBits,
        int b5,
        int b2,
        int s5,
        int s2,
        int m5,
        int m2,
        bool isCompatibleFormat,
        ref int decExp,
        char[] digits,
        out bool low,
        out bool high,
        out long lowDigitDifference)
    {
        unchecked
        {
            long b = (fractBits * Long5Pow[b5]) << b2;
            long s = Long5Pow[s5] << s2;
            long m = Long5Pow[m5] << m2;
            long tens = s * 10L;

            int nDigit = 0;
            int q = (int)(b / s);
            b = 10L * (b % s);
            m *= 10L;
            low = b < m;
            high = (b + m) > tens;

            if (q == 0 && !high)
            {
                decExp--;
            }
            else
            {
                digits[nDigit++] = (char)('0' + q);
            }

            if (!isCompatibleFormat || decExp < -3 || decExp >= 8)
            {
                high = false;
                low = false;
            }

            while (!low && !high)
            {
                q = (int)(b / s);
                b = 10L * (b % s);
                m *= 10L;

                if (m > 0L)
                {
                    low = b < m;
                    high = (b + m) > tens;
                }
                else
                {
                    low = true;
                    high = true;
                }

                digits[nDigit++] = (char)('0' + q);
            }

            lowDigitDifference = (b << 1) - tens;
            return nDigit;
        }
    }

    private static int DtoaBigIntegerPath(
        long fractBits,
        int b5,
        int b2,
        int s5,
        int s2,
        int m5,
        int m2,
        bool isCompatibleFormat,
        ref int decExp,
        char[] digits,
        out bool low,
        out bool high,
        out long lowDigitDifference)
    {
        BigInteger sVal = Pow52(s5, s2);
        BigInteger bVal = new BigInteger(fractBits) * Pow52(b5, b2);

        // JDK's quoRemIteration multiplies the remainder by 10, so these are 10*M and 10*S.
        BigInteger mVal = Pow52(m5 + 1, m2 + 1);
        BigInteger tenSVal = Pow52(s5 + 1, s2 + 1);

        int nDigit = 0;
        BigInteger remainder;
        BigInteger qBI = BigInteger.DivRem(bVal, sVal, out remainder);
        int q = (int)qBI;
        bVal = remainder * 10;

        low = bVal.CompareTo(mVal) < 0;
        high = (bVal + mVal).CompareTo(tenSVal) >= 0;

        if (q == 0 && !high)
        {
            decExp--;
        }
        else
        {
            digits[nDigit++] = (char)('0' + q);
        }

        if (!isCompatibleFormat || decExp < -3 || decExp >= 8)
        {
            high = false;
            low = false;
        }

        while (!low && !high)
        {
            qBI = BigInteger.DivRem(bVal, sVal, out remainder);
            q = (int)qBI;
            bVal = remainder * 10;
            mVal *= 10;

            low = bVal.CompareTo(mVal) < 0;
            high = (bVal + mVal).CompareTo(tenSVal) >= 0;

            digits[nDigit++] = (char)('0' + q);
        }

        if (high && low)
        {
            int cmp = (bVal << 1).CompareTo(tenSVal);
            lowDigitDifference = cmp < 0 ? -1L : (cmp > 0 ? 1L : 0L);
        }
        else
        {
            lowDigitDifference = 0L;
        }

        return nDigit;
    }

    private static int DevelopLongDigits(
        int decExponent,
        long lvalue,
        int insignificantDigits,
        char[] digits,
        out int outDecExponent)
    {
        unchecked
        {
            if (insignificantDigits != 0)
            {
                long pow10 = Long5Pow[insignificantDigits] << insignificantDigits;
                long residue = lvalue % pow10;
                lvalue /= pow10;
                decExponent += insignificantDigits;

                if (residue >= (pow10 >> 1))
                    lvalue++;
            }

            int digitNo = digits.Length - 1;
            int c;

            if (lvalue <= int.MaxValue)
            {
                int ivalue = (int)lvalue;
                c = ivalue % 10;
                ivalue /= 10;

                while (c == 0)
                {
                    decExponent++;
                    c = ivalue % 10;
                    ivalue /= 10;
                }

                while (ivalue != 0)
                {
                    digits[digitNo--] = (char)(c + '0');
                    decExponent++;
                    c = ivalue % 10;
                    ivalue /= 10;
                }

                digits[digitNo] = (char)(c + '0');
            }
            else
            {
                c = (int)(lvalue % 10L);
                lvalue /= 10L;

                while (c == 0)
                {
                    decExponent++;
                    c = (int)(lvalue % 10L);
                    lvalue /= 10L;
                }

                while (lvalue != 0L)
                {
                    digits[digitNo--] = (char)(c + '0');
                    decExponent++;
                    c = (int)(lvalue % 10L);
                    lvalue /= 10L;
                }

                digits[digitNo] = (char)(c + '0');
            }

            int nDigits = digits.Length - digitNo;
            if (digitNo != 0)
                Array.Copy(digits, digitNo, digits, 0, nDigits);

            outDecExponent = decExponent + 1;
            return nDigits;
        }
    }

    private static void Roundup(char[] digits, int nDigits, ref int decExponent)
    {
        int i = nDigits - 1;
        int q = digits[i];

        if (q == '9')
        {
            while (q == '9' && i > 0)
            {
                digits[i] = '0';
                q = digits[--i];
            }

            if (q == '9')
            {
                decExponent += 1;
                digits[0] = '1';
                return;
            }
        }

        digits[i] = (char)(q + 1);
    }

    private static int FormatJava(
        bool isNegative,
        char[] digits,
        int nDigits,
        int decExponent,
        char[] result)
    {
        int i = 0;
        if (isNegative)
            result[i++] = '-';

        if (decExponent > 0 && decExponent < 8)
        {
            int charLength = Math.Min(nDigits, decExponent);
            Array.Copy(digits, 0, result, i, charLength);
            i += charLength;

            if (charLength < decExponent)
            {
                int zeroCount = decExponent - charLength;
                for (int k = 0; k < zeroCount; k++)
                    result[i++] = '0';
                result[i++] = '.';
                result[i++] = '0';
            }
            else
            {
                result[i++] = '.';
                if (charLength < nDigits)
                {
                    int t = nDigits - charLength;
                    Array.Copy(digits, charLength, result, i, t);
                    i += t;
                }
                else
                {
                    result[i++] = '0';
                }
            }
        }
        else if (decExponent <= 0 && decExponent > -3)
        {
            result[i++] = '0';
            result[i++] = '.';

            if (decExponent != 0)
            {
                int zeroCount = -decExponent;
                for (int k = 0; k < zeroCount; k++)
                    result[i++] = '0';
            }

            Array.Copy(digits, 0, result, i, nDigits);
            i += nDigits;
        }
        else
        {
            result[i++] = digits[0];
            result[i++] = '.';

            if (nDigits > 1)
            {
                Array.Copy(digits, 1, result, i, nDigits - 1);
                i += nDigits - 1;
            }
            else
            {
                result[i++] = '0';
            }

            result[i++] = 'E';
            int e;
            if (decExponent <= 0)
            {
                result[i++] = '-';
                e = -decExponent + 1;
            }
            else
            {
                e = decExponent - 1;
            }

            if (e <= 9)
            {
                result[i++] = (char)(e + '0');
            }
            else if (e <= 99)
            {
                result[i++] = (char)(e / 10 + '0');
                result[i++] = (char)(e % 10 + '0');
            }
            else
            {
                result[i++] = (char)(e / 100 + '0');
                e %= 100;
                result[i++] = (char)(e / 10 + '0');
                result[i++] = (char)(e % 10 + '0');
            }
        }

        return i;
    }

    private static int EstimateDecExp(long fractBits, int binExp)
    {
        double d2 = LongBitsToDouble(ExpOne | ((ulong)fractBits & SignifBitMask));
        double d = (d2 - 1.5D) * 0.289529654D
                 + 0.176091259D
                 + (double)binExp * 0.301029995663981D;

        ulong dBits = DoubleToRawLongBits(d);
        int exponent = (int)((dBits & ExpBitMask) >> ExpShift) - ExpBias;
        bool isNegative = (dBits & SignBitMask) != 0;

        if (exponent >= 0 && exponent < 52)
        {
            ulong mask = SignifBitMask >> exponent;
            int r = (int)(((dBits & SignifBitMask) | FractHob) >> (ExpShift - exponent));
            return isNegative ? (((mask & dBits) == 0UL) ? -r : -r - 1) : r;
        }

        if (exponent < 0)
            return ((dBits & ~SignBitMask) == 0UL) ? 0 : (isNegative ? -1 : 0);

        return (int)d;
    }

    private static int InsignificantDigitsForPow2(int p2)
    {
        if (p2 > 1 && p2 < InsignificantDigitsNumber.Length)
            return InsignificantDigitsNumber[p2];
        return 0;
    }

    private static BigInteger Pow52(int p5, int p2)
    {
        return Big5Pow[p5] << p2;
    }

    private static BigInteger[] BuildBig5Pow(int count)
    {
        var a = new BigInteger[count];
        a[0] = BigInteger.One;
        for (int i = 1; i < count; i++)
            a[i] = a[i - 1] * 5;
        return a;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DoubleUInt64
    {
        [FieldOffset(0)] public double Double;
        [FieldOffset(0)] public ulong UInt64;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong DoubleToRawLongBits(double value)
    {
        return new DoubleUInt64 { Double = value }.UInt64;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double LongBitsToDouble(ulong bits)
    {
        return new DoubleUInt64 { UInt64 = bits }.Double;
    }

    private static int NumberOfLeadingZeros(ulong i)
    {
        if (i == 0UL)
            return 64;

        int n = 1;
        uint x = (uint)(i >> 32);
        if (x == 0)
        {
            n += 32;
            x = (uint)i;
        }
        if ((x >> 16) == 0)
        {
            n += 16;
            x <<= 16;
        }
        if ((x >> 24) == 0)
        {
            n += 8;
            x <<= 8;
        }
        if ((x >> 28) == 0)
        {
            n += 4;
            x <<= 4;
        }
        if ((x >> 30) == 0)
        {
            n += 2;
            x <<= 2;
        }
        n -= (int)(x >> 31);
        return n;
    }

    private static int NumberOfTrailingZeros(ulong i)
    {
        if (i == 0UL)
            return 64;

        int n = 0;
        while ((i & 1UL) == 0UL)
        {
            n++;
            i >>= 1;
        }
        return n;
    }

    private static char[] GetDigitsBuffer()
    {
        char[] b = t_digits;
        if (b == null)
        {
            b = new char[20];
            t_digits = b;
        }
        return b;
    }

    private static char[] GetFormatBuffer()
    {
        char[] b = t_buffer;
        if (b == null)
        {
            b = new char[32];
            t_buffer = b;
        }
        return b;
    }
}
