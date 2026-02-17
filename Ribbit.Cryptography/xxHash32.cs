using System;

namespace Ribbit.Cryptography;

public class xxHash32
{
	public struct XXH_State
	{
		public ulong total_len;

		public uint seed;

		public uint v1;

		public uint v2;

		public uint v3;

		public uint v4;

		public int memsize;

		public byte[] memory;
	}

	private const uint PRIME32_1 = 2654435761u;

	private const uint PRIME32_2 = 2246822519u;

	private const uint PRIME32_3 = 3266489917u;

	private const uint PRIME32_4 = 668265263u;

	private const uint PRIME32_5 = 374761393u;

	protected XXH_State _state;

	public static uint CalculateHash(string str)
	{
		byte[] array = new byte[str.Length * 2];
		Buffer.BlockCopy(str.ToCharArray(), 0, array, 0, array.Length);
		return CalculateHash(array);
	}

	public static uint CalculateHash(byte[] buf, int len = -1, uint seed = 0u)
	{
		int i = 0;
		if (len == -1)
		{
			len = buf.Length;
		}
		uint num2;
		if (len >= 16)
		{
			int num = len - 16;
			uint value = (uint)((int)seed + -1640531535 + -2048144777);
			uint value2 = seed + 2246822519u;
			uint value3 = seed;
			uint value4 = seed - 2654435761u;
			do
			{
				value = CalcSubHash(value, buf, i);
				i += 4;
				value2 = CalcSubHash(value2, buf, i);
				i += 4;
				value3 = CalcSubHash(value3, buf, i);
				i += 4;
				value4 = CalcSubHash(value4, buf, i);
				i += 4;
			}
			while (i <= num);
			num2 = RotateLeft(value, 1) + RotateLeft(value2, 7) + RotateLeft(value3, 12) + RotateLeft(value4, 18);
		}
		else
		{
			num2 = seed + 374761393;
		}
		num2 += (uint)len;
		for (; i <= len - 4; i += 4)
		{
			num2 += (uint)((int)BitConverter.ToUInt32(buf, i) * -1028477379);
			num2 = RotateLeft(num2, 17) * 668265263;
		}
		for (; i < len; i++)
		{
			num2 += (uint)(buf[i] * 374761393);
			num2 = RotateLeft(num2, 11) * 2654435761u;
		}
		num2 ^= num2 >> 15;
		num2 *= 2246822519u;
		num2 ^= num2 >> 13;
		num2 *= 3266489917u;
		return num2 ^ (num2 >> 16);
	}

	public void Init(uint seed = 0u)
	{
		_state.seed = seed;
		_state.v1 = (uint)((int)seed + -1640531535 + -2048144777);
		_state.v2 = seed + 2246822519u;
		_state.v3 = seed;
		_state.v4 = seed - 2654435761u;
		_state.total_len = 0uL;
		_state.memsize = 0;
		_state.memory = new byte[16];
	}

	public bool Update(byte[] input, int len)
	{
		int num = 0;
		_state.total_len += (uint)len;
		if (_state.memsize + len < 16)
		{
			Array.Copy(input, 0, _state.memory, _state.memsize, len);
			_state.memsize += len;
			return true;
		}
		if (_state.memsize > 0)
		{
			Array.Copy(input, 0, _state.memory, _state.memsize, 16 - _state.memsize);
			_state.v1 = CalcSubHash(_state.v1, _state.memory, num);
			num += 4;
			_state.v2 = CalcSubHash(_state.v2, _state.memory, num);
			num += 4;
			_state.v3 = CalcSubHash(_state.v3, _state.memory, num);
			num += 4;
			_state.v4 = CalcSubHash(_state.v4, _state.memory, num);
			num += 4;
			num = 0;
			_state.memsize = 0;
		}
		if (num <= len - 16)
		{
			int num2 = len - 16;
			uint num3 = _state.v1;
			uint num4 = _state.v2;
			uint num5 = _state.v3;
			uint num6 = _state.v4;
			do
			{
				num3 = CalcSubHash(num3, input, num);
				num += 4;
				num4 = CalcSubHash(num4, input, num);
				num += 4;
				num5 = CalcSubHash(num5, input, num);
				num += 4;
				num6 = CalcSubHash(num6, input, num);
				num += 4;
			}
			while (num <= num2);
			_state.v1 = num3;
			_state.v2 = num4;
			_state.v3 = num5;
			_state.v4 = num6;
		}
		if (num < len)
		{
			Array.Copy(input, num, _state.memory, 0, len - num);
			_state.memsize = len - num;
		}
		return true;
	}

	public uint Digest()
	{
		int i = 0;
		uint num = ((_state.total_len < 16) ? (_state.seed + 374761393) : (RotateLeft(_state.v1, 1) + RotateLeft(_state.v2, 7) + RotateLeft(_state.v3, 12) + RotateLeft(_state.v4, 18)));
		num += (uint)(int)_state.total_len;
		for (; i <= _state.memsize - 4; i += 4)
		{
			num += (uint)((int)BitConverter.ToUInt32(_state.memory, i) * -1028477379);
			num = RotateLeft(num, 17) * 668265263;
		}
		for (; i < _state.memsize; i++)
		{
			num += (uint)(_state.memory[i] * 374761393);
			num = RotateLeft(num, 11) * 2654435761u;
		}
		num ^= num >> 15;
		num *= 2246822519u;
		num ^= num >> 13;
		num *= 3266489917u;
		return num ^ (num >> 16);
	}

	private static uint CalcSubHash(uint value, byte[] buf, int index)
	{
		uint num = BitConverter.ToUInt32(buf, index);
		value += (uint)((int)num * -2048144777);
		value = RotateLeft(value, 13);
		value *= 2654435761u;
		return value;
	}

	private static uint RotateLeft(uint value, int count)
	{
		return (value << count) | (value >> 32 - count);
	}
}
