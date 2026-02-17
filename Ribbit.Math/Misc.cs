using System;
using System.Collections.Generic;
using System.Linq;

namespace Ribbit.Math;

public static class Misc
{
	private const byte Magic8 = 29;

	private const int Magic8Shift = 4;

	private static readonly int[] NtzTable8;

	private static readonly int[] NlzTable8;

	private const ushort Magic16 = 3885;

	private const int Magic16Shift = 11;

	private static readonly int[] NtzTable16;

	private static readonly int[] NlzTable16;

	private const uint Magic32 = 130379417u;

	private const int Magic32Shift = 26;

	private static readonly int[] NtzTable32;

	private static readonly int[] NlzTable32;

	private const ulong Magic64 = 283912815948585169uL;

	private const int Magic64Shift = 57;

	private static readonly int[] NtzTable64;

	private static readonly int[] NlzTable64;

	static Misc()
	{
		NtzTable8 = new int[15]
		{
			8, 0, -1, 1, 6, -1, -1, 2, 7, -1,
			5, -1, -1, 4, 3
		};
		NlzTable8 = new int[15]
		{
			8, 7, -1, 6, 1, -1, -1, 5, 0, -1,
			2, -1, -1, 3, 4
		};
		NtzTable16 = new int[31]
		{
			16, 0, -1, 1, -1, 8, -1, 2, 14, -1,
			-1, 9, -1, 11, -1, 3, 15, -1, 7, -1,
			13, -1, 10, -1, -1, 6, 12, -1, 5, -1,
			4
		};
		NlzTable16 = new int[31]
		{
			16, 15, -1, 14, -1, 7, -1, 13, 1, -1,
			-1, 6, -1, 4, -1, 12, 0, -1, 8, -1,
			2, -1, 5, -1, -1, 9, 3, -1, 10, -1,
			11
		};
		NtzTable32 = new int[63]
		{
			32, 0, -1, 1, -1, 10, -1, 2, 29, -1,
			11, -1, 25, -1, -1, 3, 30, -1, -1, 23,
			-1, 12, 14, -1, -1, 26, -1, 16, -1, 19,
			-1, 4, 31, -1, 9, -1, 28, -1, 24, -1,
			-1, 22, -1, 13, -1, 15, 18, -1, -1, 8,
			27, -1, 21, -1, -1, 17, 7, -1, 20, -1,
			6, -1, 5
		};
		NlzTable32 = new int[63]
		{
			32, 31, -1, 30, -1, 21, -1, 29, 2, -1,
			20, -1, 6, -1, -1, 28, 1, -1, -1, 8,
			-1, 19, 17, -1, -1, 5, -1, 15, -1, 12,
			-1, 27, 0, -1, 22, -1, 3, -1, 7, -1,
			-1, 9, -1, 18, -1, 16, 13, -1, -1, 23,
			4, -1, 10, -1, -1, 14, 24, -1, 11, -1,
			25, -1, 26
		};
		NtzTable64 = new int[127]
		{
			64, 0, -1, 1, -1, 12, -1, 2, 60, -1,
			13, -1, -1, 53, -1, 3, 61, -1, -1, 21,
			-1, 14, -1, 42, -1, 24, 54, -1, -1, 28,
			-1, 4, 62, -1, 58, -1, 19, -1, 22, -1,
			-1, 17, 15, -1, -1, 33, -1, 43, -1, 50,
			-1, 25, 55, -1, -1, 35, -1, 38, 29, -1,
			-1, 45, -1, 5, 63, -1, 11, -1, 59, -1,
			52, -1, -1, 20, -1, 41, 23, -1, 27, -1,
			-1, 57, 18, -1, 16, -1, 32, -1, 49, -1,
			-1, 34, 37, -1, 44, -1, -1, 10, -1, 51,
			-1, 40, -1, 26, 56, -1, -1, 31, 48, -1,
			36, -1, 9, -1, 39, -1, -1, 30, 47, -1,
			8, -1, -1, 46, 7, -1, 6
		};
		NlzTable64 = new int[127]
		{
			64, 63, -1, 62, -1, 51, -1, 61, 3, -1,
			50, -1, -1, 10, -1, 60, 2, -1, -1, 42,
			-1, 49, -1, 21, -1, 39, 9, -1, -1, 35,
			-1, 59, 1, -1, 5, -1, 44, -1, 41, -1,
			-1, 46, 48, -1, -1, 30, -1, 20, -1, 13,
			-1, 38, 8, -1, -1, 28, -1, 25, 34, -1,
			-1, 18, -1, 58, 0, -1, 52, -1, 4, -1,
			11, -1, -1, 43, -1, 22, 40, -1, 36, -1,
			-1, 6, 45, -1, 47, -1, 31, -1, 14, -1,
			-1, 29, 26, -1, 19, -1, -1, 53, -1, 12,
			-1, 23, -1, 37, 7, -1, -1, 32, 15, -1,
			27, -1, 54, -1, 24, -1, -1, 33, 16, -1,
			55, -1, -1, 17, 56, -1, 57
		};
	}

	public static int NTZ(byte x)
	{
		return NtzTable8[(byte)(29 * (x & (byte)(-(sbyte)x))) >> 4];
	}

	public static int NTZ(ushort x)
	{
		return NtzTable16[(ushort)(3885 * (x & (ushort)(-(short)x))) >> 11];
	}

	public static int NTZ(uint x)
	{
		return NtzTable32[130379417 * (x & (0 - x)) >> 26];
	}

	public static int NTZ(ulong x)
	{
		return NtzTable64[283912815948585169L * (x & (0L - x)) >> 57];
	}

	public static int NTZ(sbyte x)
	{
		return NTZ((byte)x);
	}

	public static int NTZ(short x)
	{
		return NTZ((ushort)x);
	}

	public static int NTZ(int x)
	{
		return NTZ((uint)x);
	}

	public static int NTZ(long x)
	{
		return NTZ((ulong)x);
	}

	public static int NLZ(byte y)
	{
		uint num = y;
		num |= num >> 4;
		num |= num >> 2;
		num |= num >> 1;
		num ^= num >> 1;
		return NlzTable8[(byte)(29 * num) >> 4];
	}

	public static int NLZ(ushort y)
	{
		uint num = y;
		num |= num >> 8;
		num |= num >> 4;
		num |= num >> 2;
		num |= num >> 1;
		num ^= num >> 1;
		return NlzTable16[(ushort)(3885 * num) >> 11];
	}

	public static int NLZ(uint x)
	{
		x |= x >> 16;
		x |= x >> 8;
		x |= x >> 4;
		x |= x >> 2;
		x |= x >> 1;
		x ^= x >> 1;
		return NlzTable32[130379417 * x >> 26];
	}

	public static int NLZ(ulong x)
	{
		x |= x >> 32;
		x |= x >> 16;
		x |= x >> 8;
		x |= x >> 4;
		x |= x >> 2;
		x |= x >> 1;
		x ^= x >> 1;
		return NlzTable64[283912815948585169L * x >> 57];
	}

	public static int NLZ(sbyte x)
	{
		return NLZ((byte)x);
	}

	public static int NLZ(short x)
	{
		return NLZ((ushort)x);
	}

	public static int NLZ(int x)
	{
		return NLZ((uint)x);
	}

	public static int NLZ(long x)
	{
		return NLZ((ulong)x);
	}

	public static int LCM(int u, int v)
	{
		return u / GCD(u, v) * v;
	}

	public static uint LCM(uint u, uint v)
	{
		return u / GCD(u, v) * v;
	}

	public static long LCM(long u, long v)
	{
		return u / GCD(u, v) * v;
	}

	public static ulong LCM(ulong u, ulong v)
	{
		return u / GCD(u, v) * v;
	}

	public static int LCM(int u, int v, params int[] args)
	{
		return args.Aggregate(LCM(u, v), LCM);
	}

	public static uint LCM(uint u, uint v, params uint[] args)
	{
		return args.Aggregate(LCM(u, v), LCM);
	}

	public static long LCM(long u, long v, params long[] args)
	{
		return args.Aggregate(LCM(u, v), LCM);
	}

	public static ulong LCM(ulong u, ulong v, params ulong[] args)
	{
		return args.Aggregate(LCM(u, v), LCM);
	}

	public static int LCM(IEnumerable<int> nums)
	{
		return nums.Aggregate(1, LCM);
	}

	public static uint LCM(IEnumerable<uint> nums)
	{
		return nums.Aggregate(1u, LCM);
	}

	public static long LCM(IEnumerable<long> nums)
	{
		return nums.Aggregate(1L, LCM);
	}

	public static ulong LCM(IEnumerable<ulong> nums)
	{
		return nums.Aggregate(1uL, LCM);
	}

	public static int GCD(int u, int v)
	{
		if (u < 0)
		{
			u = -u;
		}
		if (v < 0)
		{
			v = -v;
		}
		return (int)GCD((uint)u, (uint)v);
	}

	private static uint GCD(uint u, uint v)
	{
		if (u < 2 || v < 2)
		{
			return 1u;
		}
		int num = NTZ(u);
		int num2 = NTZ(v);
		int num3 = System.Math.Min(num, num2);
		u >>= num;
		v >>= num2;
		while (u != v)
		{
			if (u > v)
			{
				u -= v;
				u >>= NTZ(u);
			}
			else
			{
				v -= u;
				v >>= NTZ(v);
			}
		}
		return u << num3;
	}

	public static long GCD(long u, long v)
	{
		if (u < 0)
		{
			u = -u;
		}
		if (v < 0)
		{
			v = -v;
		}
		return (long)GCD((ulong)u, (ulong)v);
	}

	private static ulong GCD(ulong u, ulong v)
	{
		if (u < 2 || v < 2)
		{
			return 1uL;
		}
		int num = NTZ(u);
		int num2 = NTZ(v);
		int num3 = System.Math.Min(num, num2);
		u >>= num;
		v >>= num2;
		while (u != v)
		{
			if (u > v)
			{
				u -= v;
				u >>= NTZ(u);
			}
			else
			{
				v -= u;
				v >>= NTZ(v);
			}
		}
		return u << num3;
	}
}
