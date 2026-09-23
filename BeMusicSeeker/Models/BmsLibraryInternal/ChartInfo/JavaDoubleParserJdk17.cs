// JavaDoubleParserJdk17.cs
// .NET Framework 4.7.2 compatible implementation of Java JDK 17 Double.parseDouble.
//
// Requirements:
//   - Add a reference to System.Numerics.dll.
//   - No Span<T>, Int128, or .NET Core APIs are used.
//
// Public API:
//   double JavaDoubleParserJdk17.ParseDouble(string s)
//   bool   JavaDoubleParserJdk17.TryParseDouble(string s, out double value)
//   ulong  JavaDoubleParserJdk17.ParseDoubleBits(string s)
//
// Notes:
//   - The accepted syntax intentionally follows JDK 17 FloatingDecimal.readJavaFormatString:
//       Java trim() whitespace only (chars <= U+0020), optional sign, NaN, Infinity,
//       decimal syntax, hexadecimal syntax, and optional f/F/d/D suffix for numeric literals.
//   - Decimal conversion has a JDK-like fast path for small inputs and an exact BigInteger
//     fallback using round-to-nearest-even.
//   - Hexadecimal conversion is exact and uses only the significant leading hexadecimal bits
//     needed for binary64 rounding; very long tails are collapsed into a sticky digit.

using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class JavaDoubleParserJdk17
{
    private const int MaxDecimalDigits = 15;
    private const int MaxNDigits = 1100;      // JDK 17 FloatingDecimal.MAX_NDIGITS
    private const int MaxHexDigits = 400;     // More than enough for binary64 plus sticky information.

    private const long DecimalExponentCap = 10000000000L;
    private const long JavaIntMax = 2147483647L;

    private const ulong SignMask = 0x8000000000000000UL;
    private const ulong PositiveInfinityBits = 0x7ff0000000000000UL;
    private const ulong CanonicalNaNBits = 0x7ff8000000000000UL;
    private const ulong MinNormalBits = 0x0010000000000000UL;

    private static readonly BigInteger BigTwoPow52 = BigInteger.One << 52;
    private static readonly BigInteger BigTwoPow53 = BigInteger.One << 53;

    private static readonly double[] Small10Pow =
    [
        1.0e0,
        1.0e1, 1.0e2, 1.0e3, 1.0e4, 1.0e5,
        1.0e6, 1.0e7, 1.0e8, 1.0e9, 1.0e10,
        1.0e11, 1.0e12, 1.0e13, 1.0e14, 1.0e15,
        1.0e16, 1.0e17, 1.0e18, 1.0e19, 1.0e20,
        1.0e21, 1.0e22
    ];

    private static readonly object Pow10Lock = new();
    private static BigInteger[] Pow10Cache = BuildInitialPow10Cache();

    [ThreadStatic]
    private static char[] threadDigits;

    [StructLayout(LayoutKind.Explicit)]
    private struct DoubleLongUnion
    {
        [FieldOffset(0)] public double DoubleValue;
        [FieldOffset(0)] public long LongValue;
    }

    public static double ParseDouble(string s)
    {
        if (s == null)
            throw new ArgumentNullException("s");

        if (!TryParseDoubleBits(s, out ulong bits))
            throw new FormatException("For input string: \"" + JavaTrimForMessage(s) + "\"");

        return LongBitsToDouble(bits);
    }

    public static bool TryParseDouble(string s, out double value)
    {
        if (TryParseDoubleBits(s, out ulong bits))
        {
            value = LongBitsToDouble(bits);
            return true;
        }

        value = 0.0;
        return false;
    }

    public static ulong ParseDoubleBits(string s)
    {
        if (s == null)
            throw new ArgumentNullException("s");

        if (!TryParseDoubleBits(s, out ulong bits))
            throw new FormatException("For input string: \"" + JavaTrimForMessage(s) + "\"");

        return bits;
    }

    public static bool TryParseDoubleBits(string s, out ulong bits)
    {
        bits = 0UL;
        if (s == null)
            return false;

        JavaTrimBounds(s, out int start, out int end);
        if (start == end)
            return false;

        int i = start;
        bool isNegative = false;

        char c = s[i];
        if (c == '-' || c == '+')
        {
            isNegative = c == '-';
            i++;
            if (i >= end)
                return false;
        }

        if (MatchesExact(s, i, end, "NaN"))
        {
            // Java returns the canonical positive NaN even for "-NaN".
            bits = CanonicalNaNBits;
            return true;
        }

        if (MatchesExact(s, i, end, "Infinity"))
        {
            bits = PositiveInfinityBits | (isNegative ? SignMask : 0UL);
            return true;
        }

        if (s[i] == '0' && i + 1 < end && (s[i + 1] == 'x' || s[i + 1] == 'X'))
            return TryParseHex(s, start, end, out bits);

        return TryParseDecimal(s, start, end, isNegative, i, out bits);
    }

    private static bool TryParseDecimal(string s, int start, int end, bool isNegative, int i, out ulong bits)
    {
        bits = 0UL;
        int mantissaStart = i;

        bool decimalPointSeen = false;
        int totalDigits = 0;
        int pointDigits = 0;
        int firstNonZero = -1;
        int lastNonZero = -1;

        while (i < end)
        {
            char c = s[i];
            if (c >= '0' && c <= '9')
            {
                if (c != '0')
                {
                    if (firstNonZero < 0)
                        firstNonZero = totalDigits;
                    lastNonZero = totalDigits;
                }
                totalDigits++;
                i++;
            }
            else if (c == '.')
            {
                if (decimalPointSeen)
                    return false;
                decimalPointSeen = true;
                pointDigits = totalDigits;
                i++;
            }
            else
            {
                break;
            }
        }

        int mantissaEnd = i;
        if (!decimalPointSeen)
            pointDigits = totalDigits;

        if (totalDigits == 0)
            return false;

        bool isZero = firstNonZero < 0;
        long decExp = 0;
        int nTotalDigits = 0;

        if (!isZero)
        {
            decExp = (long)pointDigits - firstNonZero;
            nTotalDigits = lastNonZero - firstNonZero + 1;
        }

        if (i < end && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            int expSign = 1;
            if (i < end && (s[i] == '-' || s[i] == '+'))
            {
                if (s[i] == '-')
                    expSign = -1;
                i++;
            }

            int expAt = i;
            long expVal = 0L;
            bool expOverflow = false;

            while (i < end && s[i] >= '0' && s[i] <= '9')
            {
                int d = s[i] - '0';
                if (expVal <= DecimalExponentCap)
                {
                    expVal = expVal * 10L + d;
                    if (expVal > DecimalExponentCap)
                        expOverflow = true;
                }
                else
                {
                    expOverflow = true;
                }
                i++;
            }

            if (i == expAt)
                return false;

            if (!isZero)
            {
                if (expOverflow)
                    decExp = expSign > 0 ? DecimalExponentCap : -DecimalExponentCap;
                else
                    decExp += expSign * expVal;
            }
        }

        if (i < end)
        {
            if (i != end - 1)
                return false;

            char suffix = s[i];
            if (suffix != 'f' && suffix != 'F' && suffix != 'd' && suffix != 'D')
                return false;
            i++;
        }

        if (i != end)
            return false;

        ulong sign = isNegative ? SignMask : 0UL;

        if (isZero)
        {
            bits = sign;
            return true;
        }

        // decExp is the decimal exponent of the first significant digit plus one.
        if (decExp > 309L)
        {
            bits = sign | PositiveInfinityBits;
            return true;
        }

        if (decExp <= -324L)
        {
            bits = sign;
            return true;
        }

        int nDigits = nTotalDigits > MaxNDigits ? MaxNDigits + 1 : nTotalDigits;
        char[] digits = RentDigits(nDigits);
        int count = 0;
        int digitIndex = 0;

        for (int j = mantissaStart; j < mantissaEnd; j++)
        {
            char ch = s[j];
            if (ch == '.')
                continue;

            if (digitIndex >= firstNonZero && digitIndex <= lastNonZero)
            {
                if (nTotalDigits > MaxNDigits && count == MaxNDigits)
                {
                    // JDK 17 FloatingDecimal collapses very long tails this way.
                    digits[count++] = '1';
                    break;
                }

                if (count < nDigits)
                    digits[count++] = ch;
            }

            digitIndex++;
        }

        if (count != nDigits)
            return false; // Should be unreachable for syntactically valid input.

        ulong smallValue = 0UL;
        int smallLimit = Math.Min(nDigits, 19);
        for (int j = 0; j < smallLimit; j++)
            smallValue = smallValue * 10UL + (ulong)(digits[j] - '0');

        if (nDigits <= MaxDecimalDigits)
        {
            if (TryFastDecimal(isNegative, smallValue, nDigits, decExp, out ulong fastBits))
            {
                bits = fastBits;
                return true;
            }
        }

        BigInteger significand = BigInteger.Zero;
        for (int j = 0; j < nDigits; j++)
            significand = significand * 10 + (digits[j] - '0');

        int scale10 = (int)(decExp - nDigits);
        BigInteger numerator;
        BigInteger denominator;

        if (scale10 >= 0)
        {
            numerator = significand * Pow10(scale10);
            denominator = BigInteger.One;
        }
        else
        {
            numerator = significand;
            denominator = Pow10(-scale10);
        }

        bits = RoundPositiveRationalToDoubleBits(numerator, denominator) | sign;
        return true;
    }

    private static bool TryFastDecimal(bool isNegative, ulong lValue, int nDigits, long decExp, out ulong bits)
    {
        bits = 0UL;

        int exp = (int)(decExp - nDigits);
        double dValue = (double)lValue;
        double rValue;

        if (exp == 0 || dValue == 0.0)
        {
            rValue = dValue;
        }
        else if (exp >= 0)
        {
            if (exp <= 22)
            {
                rValue = dValue * Small10Pow[exp];
            }
            else
            {
                int slop = MaxDecimalDigits - nDigits;
                if (exp > 22 + slop)
                    return false;

                dValue *= Small10Pow[slop];
                rValue = dValue * Small10Pow[exp - slop];
            }
        }
        else
        {
            if (exp < -22)
                return false;

            rValue = dValue / Small10Pow[-exp];
        }

        if (isNegative)
            rValue = -rValue;

        bits = DoubleToRawLongBits(rValue);
        return true;
    }

    private static bool TryParseHex(string s, int start, int end, out ulong bits)
    {
        bits = 0UL;
        int i = start;
        bool isNegative = false;

        if (i < end && (s[i] == '-' || s[i] == '+'))
        {
            isNegative = s[i] == '-';
            i++;
        }

        if (i + 1 >= end || s[i] != '0' || (s[i + 1] != 'x' && s[i + 1] != 'X'))
            return false;
        i += 2;

        int mantissaStart = i;
        bool pointSeen = false;
        int totalHexDigits = 0;
        int fracHexDigits = 0;
        int firstNonZero = -1;
        int lastNonZero = -1;

        while (i < end)
        {
            int hv = HexValue(s[i]);
            if (hv >= 0)
            {
                if (hv != 0)
                {
                    if (firstNonZero < 0)
                        firstNonZero = totalHexDigits;
                    lastNonZero = totalHexDigits;
                }

                totalHexDigits++;
                if (pointSeen)
                    fracHexDigits++;
                i++;
            }
            else if (s[i] == '.')
            {
                if (pointSeen)
                    return false;
                pointSeen = true;
                i++;
            }
            else
            {
                break;
            }
        }

        int mantissaEnd = i;

        if (totalHexDigits == 0)
            return false;

        if (i >= end || (s[i] != 'p' && s[i] != 'P'))
            return false;
        i++;

        int expSign = 1;
        if (i < end && (s[i] == '-' || s[i] == '+'))
        {
            if (s[i] == '-')
                expSign = -1;
            i++;
        }

        int expAt = i;
        long expVal = 0L;
        bool expOverflow = false;

        while (i < end && s[i] >= '0' && s[i] <= '9')
        {
            int d = s[i] - '0';
            if (expVal <= JavaIntMax)
            {
                expVal = expVal * 10L + d;
                if (expVal > JavaIntMax)
                    expOverflow = true;
            }
            else
            {
                expOverflow = true;
            }
            i++;
        }

        if (i == expAt)
            return false;

        if (i < end)
        {
            if (i != end - 1)
                return false;

            char suffix = s[i];
            if (suffix != 'f' && suffix != 'F' && suffix != 'd' && suffix != 'D')
                return false;
            i++;
        }

        if (i != end)
            return false;

        ulong sign = isNegative ? SignMask : 0UL;

        // JDK returns signed zero for a zero significand even if the hex exponent overflows.
        if (firstNonZero < 0)
        {
            bits = sign;
            return true;
        }

        if (expOverflow)
        {
            bits = sign | (expSign > 0 ? PositiveInfinityBits : 0UL);
            return true;
        }

        long rawExponent = expSign > 0 ? expVal : -expVal;
        int nHexDigits = lastNonZero - firstNonZero + 1;
        int trailingZeroHexDigits = totalHexDigits - 1 - lastNonZero;

        int leadingDigit = 0;
        int digitIndexForLead = 0;
        for (int j = mantissaStart; j < mantissaEnd; j++)
        {
            if (s[j] == '.')
                continue;

            if (digitIndexForLead == firstNonZero)
            {
                leadingDigit = HexValue(s[j]);
                break;
            }
            digitIndexForLead++;
        }

        long binaryExponent = rawExponent - 4L * fracHexDigits + 4L * trailingZeroHexDigits;
        long bitLengthSig = ((long)nHexDigits - 1L) * 4L + BitLengthSmall(leadingDigit);
        long floorLog2 = bitLengthSig - 1L + binaryExponent;

        if (floorLog2 > 1023L)
        {
            bits = sign | PositiveInfinityBits;
            return true;
        }

        if (floorLog2 < -1075L)
        {
            bits = sign;
            return true;
        }

        int keepHexDigits = nHexDigits > MaxHexDigits ? MaxHexDigits : nHexDigits;
        long exponentAdjust = nHexDigits > MaxHexDigits ? 4L * (nHexDigits - keepHexDigits) : 0L;

        BigInteger significand = BigInteger.Zero;
        int digitIndex = 0;
        int kept = 0;

        for (int j = mantissaStart; j < mantissaEnd && kept < keepHexDigits; j++)
        {
            if (s[j] == '.')
                continue;

            if (digitIndex >= firstNonZero && digitIndex <= lastNonZero)
            {
                int hv = HexValue(s[j]);
                if (nHexDigits > MaxHexDigits && kept == keepHexDigits - 1)
                    hv = 1; // Sticky surrogate for all discarded nonzero information.

                significand = (significand << 4) + hv;
                kept++;
            }

            digitIndex++;
        }

        if (kept != keepHexDigits)
            return false; // Should be unreachable for syntactically valid input.

        bits = RoundBigIntegerTimesPowerOfTwoToDoubleBits(significand, binaryExponent + exponentAdjust) | sign;
        return true;
    }

    private static ulong RoundPositiveRationalToDoubleBits(BigInteger numerator, BigInteger denominator)
    {
        int e = FloorLog2(numerator, denominator);

        if (e > 1023)
            return PositiveInfinityBits;

        if (e < -1022)
        {
            BigInteger k = RoundQuotient(numerator << 1074, denominator);
            if (k.IsZero)
                return 0UL;

            if (k >= BigTwoPow52)
                return MinNormalBits;

            return (ulong)k;
        }

        int p = e - 52;
        BigInteger rounded;
        if (p >= 0)
            rounded = RoundQuotient(numerator, denominator << p);
        else
            rounded = RoundQuotient(numerator << (-p), denominator);

        if (rounded == BigTwoPow53)
        {
            e++;
            rounded >>= 1;
            if (e > 1023)
                return PositiveInfinityBits;
        }

        ulong fraction = (ulong)(rounded - BigTwoPow52);
        return ((ulong)(e + 1023) << 52) | fraction;
    }

    private static ulong RoundBigIntegerTimesPowerOfTwoToDoubleBits(BigInteger significand, long binaryExponent)
    {
        if (significand.Sign == 0)
            return 0UL;

        long floorLog2 = (long)BitLength(significand) - 1L + binaryExponent;

        if (floorLog2 > 1023L)
            return PositiveInfinityBits;

        if (floorLog2 < -1022L)
        {
            BigInteger k = RoundShift(significand, binaryExponent + 1074L);
            if (k.IsZero)
                return 0UL;

            if (k >= BigTwoPow52)
                return MinNormalBits;

            return (ulong)k;
        }

        int e = (int)floorLog2;
        long shift = binaryExponent - ((long)e - 52L);
        BigInteger rounded = RoundShift(significand, shift);

        if (rounded == BigTwoPow53)
        {
            e++;
            rounded >>= 1;
            if (e > 1023)
                return PositiveInfinityBits;
        }

        ulong fraction = (ulong)(rounded - BigTwoPow52);
        return ((ulong)(e + 1023) << 52) | fraction;
    }

    private static BigInteger RoundShift(BigInteger n, long shift)
    {
        if (shift >= 0L)
        {
            if (shift > int.MaxValue)
                throw new OverflowException("Shift too large.");
            return n << (int)shift;
        }

        long right = -shift;
        return RoundRightShift(n, right);
    }

    private static BigInteger RoundRightShift(BigInteger n, long right)
    {
        if (right <= 0L)
            return n;

        int nBits = BitLength(n);
        if (right > (long)nBits + 1L)
            return BigInteger.Zero;

        if (right > int.MaxValue)
            return BigInteger.Zero;

        int s = (int)right;
        BigInteger q = n >> s;
        BigInteger rem = n - (q << s);
        BigInteger half = BigInteger.One << (s - 1);

        int cmp = rem.CompareTo(half);
        if (cmp > 0 || (cmp == 0 && !q.IsEven))
            q += BigInteger.One;

        return q;
    }

    private static BigInteger RoundQuotient(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        int cmp = (remainder << 1).CompareTo(denominator);

        if (cmp > 0 || (cmp == 0 && !quotient.IsEven))
            quotient += BigInteger.One;

        return quotient;
    }

    private static int FloorLog2(BigInteger numerator, BigInteger denominator)
    {
        int e = BitLength(numerator) - BitLength(denominator);

        if (e >= 0)
        {
            if (numerator < (denominator << e))
                e--;
        }
        else
        {
            if ((numerator << (-e)) < denominator)
                e--;
        }

        return e;
    }

    private static int BitLength(BigInteger value)
    {
        if (value.Sign < 0)
            value = BigInteger.Abs(value);

        if (value.IsZero)
            return 0;

        byte[] bytes = value.ToByteArray(); // Little-endian two's complement.
        int len = bytes.Length;

        while (len > 1 && bytes[len - 1] == 0)
            len--;

        byte top = bytes[len - 1];
        int bits = (len - 1) * 8;
        while (top != 0)
        {
            bits++;
            top >>= 1;
        }

        return bits;
    }

    private static int BitLengthSmall(int value)
    {
        int bits = 0;
        while (value != 0)
        {
            bits++;
            value >>= 1;
        }
        return bits;
    }

    private static BigInteger Pow10(int n)
    {
        if (n < 0)
            throw new ArgumentOutOfRangeException("n");

        BigInteger[] cache = Pow10Cache;
        if (n < cache.Length)
            return cache[n];

        lock (Pow10Lock)
        {
            cache = Pow10Cache;
            if (n >= cache.Length)
            {
                int newLength = cache.Length;
                while (newLength <= n)
                    newLength *= 2;

                var expanded = new BigInteger[newLength];
                Array.Copy(cache, expanded, cache.Length);
                for (int i = cache.Length; i < expanded.Length; i++)
                    expanded[i] = expanded[i - 1] * 10;

                Pow10Cache = expanded;
            }

            return Pow10Cache[n];
        }
    }

    private static BigInteger[] BuildInitialPow10Cache()
    {
        var a = new BigInteger[32];
        a[0] = BigInteger.One;
        for (int i = 1; i < a.Length; i++)
            a[i] = a[i - 1] * 10;
        return a;
    }

    private static char[] RentDigits(int length)
    {
        char[] a = threadDigits;
        if (a == null || a.Length < length)
        {
            int size = Math.Max(length, MaxNDigits + 1);
            a = new char[size];
            threadDigits = a;
        }
        return a;
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'A' && c <= 'F')
            return c - 'A' + 10;
        if (c >= 'a' && c <= 'f')
            return c - 'a' + 10;
        return -1;
    }

    private static bool MatchesExact(string s, int start, int end, string literal)
    {
        if (end - start != literal.Length)
            return false;

        for (int i = 0; i < literal.Length; i++)
        {
            if (s[start + i] != literal[i])
                return false;
        }

        return true;
    }

    private static void JavaTrimBounds(string s, out int start, out int end)
    {
        start = 0;
        end = s.Length;

        while (start < end && s[start] <= '\u0020')
            start++;

        while (end > start && s[end - 1] <= '\u0020')
            end--;
    }

    private static string JavaTrimForMessage(string s)
    {
        JavaTrimBounds(s, out int start, out int end);
        return s.Substring(start, end - start);
    }

    private static double LongBitsToDouble(ulong bits)
    {
        var u = new DoubleLongUnion
        {
            LongValue = unchecked((long)bits)
        };
        return u.DoubleValue;
    }

    private static ulong DoubleToRawLongBits(double value)
    {
        var u = new DoubleLongUnion
        {
            DoubleValue = value
        };
        return unchecked((ulong)u.LongValue);
    }
}
