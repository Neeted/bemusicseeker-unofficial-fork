using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Ribbit.BMS;

public static class BMSBase36
{
    internal static readonly char[] B36E = new char[36]
    {
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
        'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T',
        'U', 'V', 'W', 'X', 'Y', 'Z'
    };

    internal static readonly int[] B36D = new int[256]
    {
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, 0, 1,
        2, 3, 4, 5, 6, 7, 8, 9, -1, -1,
        -1, -1, -1, -1, -1, 10, 11, 12, 13, 14,
        15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, -1, -1, -1, -1, -1, -1, 10, 11, 12,
        13, 14, 15, 16, 17, 18, 19, 20, 21, 22,
        23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1
    };

    public static readonly ReadOnlyCollection<int> MapFromBase36Set = Enumerable.Range(0, 1296).ToList().AsReadOnly();

    public static readonly ReadOnlyCollection<int> MapFromBase16Set = B36E.Take(16).SelectMany(delegate (char c1)
    {
        int upper = B36D[(uint)c1] * 36;
        return from c2 in B36E.Take(16)
               select upper + B36D[(uint)c2];
    }).ToList()
        .AsReadOnly();

    public static readonly ReadOnlyCollection<int> MapToBase36Set = MapFromBase36Set;

    public static readonly ReadOnlyCollection<int> MapToBase16Set = B36E.SelectMany(delegate (char c1)
    {
        int upper = BMSBase16.B16D[(uint)c1];
        return (upper == -1) ? Enumerable.Repeat(0, B36E.Length) : B36E.Select(delegate (char c2)
        {
            int num = BMSBase16.B16D[(uint)c2];
            return (num != -1) ? (upper * 16 + num) : 0;
        });
    }).ToList().AsReadOnly();

    public static readonly ReadOnlyCollection<int> MapToBase16Subset = MapToBase16Set.Select((int i) => MapFromBase16Set[i]).ToList().AsReadOnly();

    public static List<Tuple<string, string>> b = MapToBase16Subset.Select((int i, int j) => new Tuple<string, string>(FromInt(j), FromInt(i))).ToList();

    public const int MaxValue = 1295;

    public static string FromInt(int value)
    {
        if (value > 1295 || value < 0)
        {
            throw new ArgumentOutOfRangeException("value", "Argument should be 0 <= value <= " + 1295);
        }
        return new string(new char[2]
        {
            B36E[value / 36],
            B36E[value % 36]
        });
    }

    public static int ToInt(string s)
    {
        if (s == null || s.Length != 2)
        {
            throw new ArgumentException("Only length 2 is supported.", "s");
        }
        return B36D[(uint)s[0]] * 36 + B36D[(uint)s[1]];
    }

    public static bool IsBMSBase36(this string s)
    {
        if (s == null || s.Length != 2)
        {
            throw new ArgumentException("Only length 2 is supported.", "s");
        }
        return s.All(delegate (char c)
        {
            if ('A' <= c && c <= 'Z')
            {
                return true;
            }
            if ('0' <= c && c <= '9')
            {
                return true;
            }
            return 'a' <= c && c <= 'z';
        });
    }
}
