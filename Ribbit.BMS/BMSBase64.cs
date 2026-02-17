using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Ribbit.BMS;

public static class BMSBase64
{
	private static readonly char[] B64E = new char[64]
	{
		'0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
		'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
		'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T',
		'U', 'V', 'W', 'X', 'Y', 'Z', 'a', 'b', 'c', 'd',
		'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n',
		'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x',
		'y', 'z', '+', '/'
	};

	private static readonly int[] B64D = new int[256]
	{
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
		-1, -1, -1, 62, -1, -1, -1, 63, 0, 1,
		2, 3, 4, 5, 6, 7, 8, 9, -1, -1,
		-1, -1, -1, -1, -1, 10, 11, 12, 13, 14,
		15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
		25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
		35, -1, -1, -1, -1, -1, -1, 36, 37, 38,
		39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
		49, 50, 51, 52, 53, 54, 55, 56, 57, 58,
		59, 60, 61, -1, -1, -1, -1, -1, -1, -1,
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

	public static readonly ReadOnlyCollection<int> MapFromBase64Set = Enumerable.Range(0, 4096).ToList().AsReadOnly();

	public static readonly ReadOnlyCollection<int> MapFromBase36Set = B64E.Take(36).SelectMany(delegate(char c1)
	{
		int upper = B64D[(uint)c1] << 6;
		return from c2 in B64E.Take(36)
			select upper + B64D[(uint)c2];
	}).ToList()
		.AsReadOnly();

	public static readonly ReadOnlyCollection<int> MapFromBase16Set = B64E.Take(16).SelectMany(delegate(char c1)
	{
		int upper = B64D[(uint)c1] << 6;
		return from c2 in B64E.Take(16)
			select upper + B64D[(uint)c2];
	}).ToList()
		.AsReadOnly();

	public static readonly ReadOnlyCollection<int> MapToBase64Set = MapFromBase64Set;

	public static readonly ReadOnlyCollection<int> MapToBase36Set = B64E.SelectMany(delegate(char c1)
	{
		int upper = BMSBase36.B36D[(uint)c1];
		return (upper == -1) ? Enumerable.Repeat(0, B64E.Length) : B64E.Select(delegate(char c2)
		{
			int num = BMSBase36.B36D[(uint)c2];
			return (num != -1) ? (upper * 36 + num) : 0;
		});
	}).ToList().AsReadOnly();

	public static readonly ReadOnlyCollection<int> MapToBase16Set = B64E.SelectMany(delegate(char c1)
	{
		int upper = BMSBase16.B16D[(uint)c1];
		return (upper == -1) ? Enumerable.Repeat(0, B64E.Length) : B64E.Select(delegate(char c2)
		{
			int num = BMSBase16.B16D[(uint)c2];
			return (num != -1) ? (upper * 16 + num) : 0;
		});
	}).ToList().AsReadOnly();

	public static readonly ReadOnlyCollection<int> MapToBase36Subset = MapToBase36Set.Select((int i) => MapFromBase36Set[i]).ToList().AsReadOnly();

	public static readonly ReadOnlyCollection<int> MapToBase16Subset = MapToBase16Set.Select((int i) => MapFromBase16Set[i]).ToList().AsReadOnly();

	public static readonly ReadOnlyCollection<ReadOnlyCollection<int>> AlternativesForBase36Set = MapToBase36Subset.Select((int v, int i) => MapToBase36Subset.Where((int num, int num2) => num == v && num2 != i).ToList().AsReadOnly()).ToList().AsReadOnly();

	public const int MaxValue = 4095;

	public static string FromInt(int value)
	{
		if (value > 4095 || value < 0)
		{
			throw new ArgumentOutOfRangeException("value", "Argument should be 0 <= value <= " + 4095);
		}
		return new string(new char[2]
		{
			B64E[(value & 0xFC0) >> 6],
			B64E[value & 0x3F]
		});
	}

	public static int ToInt(string s)
	{
		if (s == null || s.Length != 2)
		{
			throw new ArgumentException("Only length 2 is supported.", "s");
		}
		return (B64D[(uint)s[0]] << 6) + B64D[(uint)s[1]];
	}

	public static bool IsBMSBase64(this string s)
	{
		if (s == null || s.Length != 2)
		{
			throw new ArgumentException("Only length 2 is supported.", "s");
		}
		return s.All(delegate(char c)
		{
			if ('A' <= c && c <= 'Z')
			{
				return true;
			}
			if ('0' <= c && c <= '9')
			{
				return true;
			}
			if ('a' <= c && c <= 'z')
			{
				return true;
			}
			return '+' == c || c == '/';
		});
	}
}
