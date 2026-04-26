// JavaDoubleToStringJdk21.cs
// .NET Framework 4.7.2 compatible implementation of JDK 21 Double.toString(double).
// Requires unsafe compilation and System.Numerics.dll reference.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static unsafe class JavaDoubleToStringJdk21
{
    public const int MaxChars = 24;

    private const int P = 53;
    private const int W = 11;
    private const int QMin = (-1 << (W - 1)) - P + 3; // -1074

    private const long CMin = 1L << (P - 1);
    private const long CTiny = 3;
    private const int H = 17;

    private const int BqMask = (1 << W) - 1;
    private const ulong TMask = (1UL << (P - 1)) - 1;
    private const long Mask63 = long.MaxValue;
    private const int Mask28 = (1 << 28) - 1;

    private const int Q10 = 41;
    private const long C10 = 661971961083L;
    private const long A10 = -274743187321L;

    private const int Q2 = 38;
    private const long C2 = 913124641741L;

    private const int GeMin = -342;
    private const int GeMax = 324;

    private static readonly long[] Pow10 =
    {
        1L,
        10L,
        100L,
        1000L,
        10000L,
        100000L,
        1000000L,
        10000000L,
        100000000L,
        1000000000L,
        10000000000L,
        100000000000L,
        1000000000000L,
        10000000000000L,
        100000000000000L,
        1000000000000000L,
        10000000000000000L,
        100000000000000000L,
        1000000000000000000L
    };

    // Generated once at type initialization. Replace with the OpenJDK MathUtils.g table
    // if first-use chart parsing cost ever becomes visible in profiling.
    private static readonly long[] G = BuildG();

    public static string ToString(double value)
    {
        long raw = BitConverter.DoubleToInt64Bits(value);
        ulong bits = unchecked((ulong)raw);
        long t = (long)(bits & TMask);
        int bq = (int)((bits >> (P - 1)) & (ulong)BqMask);

        if (bq == BqMask)
        {
            if (t != 0)
            {
                return "NaN";
            }
            return raw < 0 ? "-Infinity" : "Infinity";
        }

        if (bq == 0 && t == 0)
        {
            return raw < 0 ? "-0.0" : "0.0";
        }

        char* buffer = stackalloc char[MaxChars];
        int index = 0;

        if (raw < 0)
        {
            buffer[index++] = '-';
        }

        if (bq != 0)
        {
            int mq = -QMin + 1 - bq;
            long c = CMin | t;

            if (0 < mq && mq < P)
            {
                long f = c >> mq;
                if ((f << mq) == c)
                {
                    index = ToChars(buffer, index, f, 0);
                    return new string(buffer, 0, index);
                }
            }

            index = ToDecimal(buffer, index, -mq, c, 0);
            return new string(buffer, 0, index);
        }
        else
        {
            long c = t;
            index = c < CTiny
                ? ToDecimal(buffer, index, QMin, 10 * c, -1)
                : ToDecimal(buffer, index, QMin, c, 0);

            return new string(buffer, 0, index);
        }
    }

    private static int ToDecimal(char* str, int index, int q, long c, int dk)
    {
        int outBit = (int)c & 1;

        long cb = unchecked(c << 2);
        long cbr = unchecked(cb + 2);
        long cbl;
        int k;

        if (c != CMin || q == QMin)
        {
            cbl = unchecked(cb - 2);
            k = Flog10Pow2(q);
        }
        else
        {
            cbl = unchecked(cb - 1);
            k = Flog10ThreeQuartersPow2(q);
        }

        int h = q + Flog2Pow10(-k) + 2;

        long g1 = G1(-k);
        long g0 = G0(-k);

        long vb = Rop(g1, g0, unchecked(cb << h));
        long vbl = Rop(g1, g0, unchecked(cbl << h));
        long vbr = Rop(g1, g0, unchecked(cbr << h));

        long s = vb >> 2;

        if (s >= 100)
        {
            long sp10 = unchecked(10 * MultiplyHigh(s, 115292150460684698L << 4));
            long tp10 = unchecked(sp10 + 10);

            bool upin = unchecked(vbl + outBit) <= unchecked(sp10 << 2);
            bool wpin = unchecked((tp10 << 2) + outBit) <= vbr;

            if (upin != wpin)
            {
                return ToChars(str, index, upin ? sp10 : tp10, k);
            }
        }

        long w = unchecked(s + 1);

        bool uin = unchecked(vbl + outBit) <= unchecked(s << 2);
        bool win = unchecked((w << 2) + outBit) <= vbr;

        if (uin != win)
        {
            return ToChars(str, index, uin ? s : w, k + dk);
        }

        long cmp = unchecked(vb - ((s + w) << 1));
        bool away = cmp > 0 || (cmp == 0 && (s & 1) != 0);

        return ToChars(str, index, away ? w : s, k + dk);
    }

    private static long Rop(long g1, long g0, long cp)
    {
        long x1 = MultiplyHigh(g0, cp);
        long y0 = unchecked(g1 * cp);
        long y1 = MultiplyHigh(g1, cp);
        long z = unchecked((long)((ulong)y0 >> 1) + x1);
        long vbp = unchecked(y1 + (long)((ulong)z >> 63));
        long round = (long)((ulong)unchecked((z & Mask63) + Mask63) >> 63);
        return vbp | round;
    }

    private static int ToChars(char* str, int index, long f, int e)
    {
        int bitLength = 64 - NumberOfLeadingZeros((ulong)f);
        int len = Flog10Pow2(bitLength);

        if (f >= Pow10[len])
        {
            len++;
        }

        f = unchecked(f * Pow10[H - len]);
        e += len;

        long hm = (long)((ulong)MultiplyHigh(f, 193428131138340668L) >> 20);
        int l = (int)(f - 100000000L * hm);
        int h = (int)((ulong)unchecked(hm * 1441151881L) >> 57);
        int m = (int)(hm - 100000000L * h);

        if (0 < e && e <= 7)
        {
            return ToCharsPlainNoLeadingZeroes(str, index, h, m, l, e);
        }

        if (-3 < e && e <= 0)
        {
            return ToCharsPlainWithLeadingZeroes(str, index, h, m, l, e);
        }

        return ToCharsScientific(str, index, h, m, l, e);
    }

    private static int ToCharsPlainNoLeadingZeroes(char* str, int index, int h, int m, int l, int e)
    {
        index = PutDigit(str, index, h);

        int y = Y(m);
        int i = 1;

        for (; i < e; i++)
        {
            int t = unchecked(10 * y);
            index = PutDigit(str, index, (int)((uint)t >> 28));
            y = t & Mask28;
        }

        str[index++] = '.';

        for (; i <= 8; i++)
        {
            int t = unchecked(10 * y);
            index = PutDigit(str, index, (int)((uint)t >> 28));
            y = t & Mask28;
        }

        return LowDigits(str, index, l);
    }

    private static int ToCharsPlainWithLeadingZeroes(char* str, int index, int h, int m, int l, int e)
    {
        index = PutDigit(str, index, 0);
        str[index++] = '.';

        for (; e < 0; e++)
        {
            index = PutDigit(str, index, 0);
        }

        index = PutDigit(str, index, h);
        index = Put8Digits(str, index, m);

        return LowDigits(str, index, l);
    }

    private static int ToCharsScientific(char* str, int index, int h, int m, int l, int e)
    {
        index = PutDigit(str, index, h);
        str[index++] = '.';
        index = Put8Digits(str, index, m);
        index = LowDigits(str, index, l);
        return Exponent(str, index, e - 1);
    }

    private static int LowDigits(char* str, int index, int l)
    {
        if (l != 0)
        {
            index = Put8Digits(str, index, l);
        }

        return RemoveTrailingZeroes(str, index);
    }

    private static int Exponent(char* str, int index, int e)
    {
        str[index++] = 'E';

        if (e < 0)
        {
            str[index++] = '-';
            e = -e;
        }

        if (e < 10)
        {
            return PutDigit(str, index, e);
        }

        if (e >= 100)
        {
            int d = (e * 1311) >> 17;
            index = PutDigit(str, index, d);
            e -= 100 * d;
        }

        int q = (e * 103) >> 10;
        index = PutDigit(str, index, q);
        return PutDigit(str, index, e - 10 * q);
    }

    private static int Put8Digits(char* str, int index, int m)
    {
        int y = Y(m);

        for (int i = 0; i < 8; i++)
        {
            int t = unchecked(10 * y);
            str[index + i] = (char)('0' + (int)((uint)t >> 28));
            y = t & Mask28;
        }

        return index + 8;
    }

    private static int RemoveTrailingZeroes(char* str, int index)
    {
        while (str[index - 1] == '0')
        {
            index--;
        }

        if (str[index - 1] == '.')
        {
            index++;
        }

        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PutDigit(char* str, int index, int d)
    {
        str[index] = (char)('0' + d);
        return index + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Y(int a)
    {
        long x = ((long)(a + 1)) << 28;
        return (int)((ulong)MultiplyHigh(x, 193428131138340668L) >> 20) - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Flog10Pow2(int q)
    {
        return (int)(((long)q * C10) >> Q10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Flog10ThreeQuartersPow2(int q)
    {
        return (int)((((long)q * C10) + A10) >> Q10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Flog2Pow10(int e)
    {
        return (int)(((long)e * C2) >> Q2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long G1(int e)
    {
        return G[(e - GeMin) << 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long G0(int e)
    {
        return G[((e - GeMin) << 1) | 1];
    }

    private static int NumberOfLeadingZeros(ulong x)
    {
        if (x == 0)
        {
            return 64;
        }

        int n = 0;
        if ((x >> 32) == 0) { n += 32; x <<= 32; }
        if ((x >> 48) == 0) { n += 16; x <<= 16; }
        if ((x >> 56) == 0) { n += 8; x <<= 8; }
        if ((x >> 60) == 0) { n += 4; x <<= 4; }
        if ((x >> 62) == 0) { n += 2; x <<= 2; }
        if ((x >> 63) == 0) { n += 1; }
        return n;
    }

    private static long MultiplyHigh(long x, long y)
    {
        ulong ux = unchecked((ulong)x);
        ulong uy = unchecked((ulong)y);

        ulong x0 = ux & 0xffffffffUL;
        ulong x1 = ux >> 32;
        ulong y0 = uy & 0xffffffffUL;
        ulong y1 = uy >> 32;

        ulong p11 = x1 * y1;
        ulong p01 = x0 * y1;
        ulong p10 = x1 * y0;
        ulong p00 = x0 * y0;

        ulong middle = (p00 >> 32) + (p10 & 0xffffffffUL) + (p01 & 0xffffffffUL);
        ulong high = p11 + (p10 >> 32) + (p01 >> 32) + (middle >> 32);

        if (x < 0)
        {
            high = unchecked(high - uy);
        }
        if (y < 0)
        {
            high = unchecked(high - ux);
        }

        return unchecked((long)high);
    }

    private static long[] BuildG()
    {
        long[] result = new long[(GeMax - GeMin + 1) * 2];

        int maxAbs = Math.Max(-GeMin, GeMax);
        BigInteger[] p10 = new BigInteger[maxAbs + 1];
        p10[0] = BigInteger.One;
        for (int i = 1; i < p10.Length; i++)
        {
            p10[i] = p10[i - 1] * 10;
        }

        BigInteger mask63 = (BigInteger.One << 63) - 1;

        for (int e = GeMin; e <= GeMax; e++)
        {
            int r = Flog2Pow10(e) - 125;
            BigInteger g;

            if (e >= 0)
            {
                BigInteger p = p10[e];
                g = r >= 0 ? (p >> r) + 1 : (p << -r) + 1;
            }
            else
            {
                BigInteger numerator = BigInteger.One << -r;
                BigInteger denominator = p10[-e];
                g = numerator / denominator + 1;
            }

            int j = (e - GeMin) << 1;
            result[j] = (long)(g >> 63);
            result[j + 1] = (long)(g & mask63);
        }

        return result;
    }
}
