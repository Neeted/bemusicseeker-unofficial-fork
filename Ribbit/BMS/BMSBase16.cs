using System;
using System.Linq;

namespace Ribbit.BMS;

public static class BMSBase16
{
	internal static readonly char[] B16E = new char[16]
	{
		'0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
		'A', 'B', 'C', 'D', 'E', 'F'
	};

	internal static readonly int[] B16D = new int[256]
	{
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, 0, 1,
		2, 3, 4, 5, 6, 7, 8, 9, -1, -1,
		-1, -1, -1, -1, -1, 10, 11, 12, 13, 14,
		15, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, 10, 11, 12,
		13, 14, 15, -1, -1, -1, -1, -1, -1, -1,
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
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1
	};

	public const int MaxValue = 255;

	public static string FromInt(int value)
	{
		if (value > 255 || value < 0)
		{
			throw new ArgumentOutOfRangeException("value", "Argument should be 0 <= value < " + 255);
		}
		return new string(new char[2]
		{
			B16E[value / 16],
			B16E[value % 16]
		});
	}

	public static int ToInt(string s)
	{
		if (s == null || s.Length != 2)
		{
			throw new ArgumentException("Only length 2 is supported.", "s");
		}
		return B16D[(uint)s[0]] * 16 + B16D[(uint)s[1]];
	}

	public static bool IsBMSBase16(this string s)
	{
		if (s == null || s.Length != 2)
		{
			throw new ArgumentException("Only length 2 is supported.", "s");
		}
		return s.All(delegate(char c)
		{
			if ('0' <= c && c <= '9')
			{
				return true;
			}
			if ('A' <= c && c <= 'F')
			{
				return true;
			}
			return 'a' <= c && c <= 'f';
		});
	}
}
