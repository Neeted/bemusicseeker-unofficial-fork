using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BeMusicSeeker.Models.Utils;

internal static class EverythingNative
{
	private const string EverythingDllName = "Everything3_x64.dll";

	private const string BridgeDllName = "EverythingBridge_x64.dll";

	private static IntPtr loadedBridgeModule = IntPtr.Zero;

	private static bool bridgeExportsChecked;

	private static bool bridgeExportsAvailable;

	internal static string GetExpectedDllPath()
	{
		return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", EverythingDllName);
	}

	internal static string GetExpectedBridgeDllPath()
	{
		return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", BridgeDllName);
	}

	internal static bool EnsureBridgeAvailable(out string reason)
	{
		reason = null;
		if (IntPtr.Size != 8)
		{
			reason = "unsupported_architecture";
			return false;
		}
		string expectedDllPath = GetExpectedDllPath();
		if (!File.Exists(expectedDllPath))
		{
			reason = "dll_not_found:" + expectedDllPath;
			return false;
		}
		return EnsureBridgeLoaded(out reason);
	}

	private static bool EnsureBridgeLoaded(out string reason)
	{
		reason = null;
		if (loadedBridgeModule == IntPtr.Zero)
		{
			string expectedBridgeDllPath = GetExpectedBridgeDllPath();
			if (!File.Exists(expectedBridgeDllPath))
			{
				reason = "bridge_dll_not_found:" + expectedBridgeDllPath;
				return false;
			}
			IntPtr intPtr = LoadLibraryW(expectedBridgeDllPath);
			if (intPtr == IntPtr.Zero)
			{
				reason = "bridge_dll_load_failed:" + Marshal.GetLastWin32Error();
				return false;
			}
			loadedBridgeModule = intPtr;
		}
		if (!bridgeExportsChecked)
		{
			bridgeExportsAvailable = GetProcAddress(loadedBridgeModule, "EBridge_Scan") != IntPtr.Zero && GetProcAddress(loadedBridgeModule, "EBridge_FreeResult") != IntPtr.Zero;
			bridgeExportsChecked = true;
		}
		if (!bridgeExportsAvailable)
		{
			reason = "bridge_export_missing";
			return false;
		}
		return true;
	}

	internal static string BuildBmsFilesQuery(string[] roots, string[] extensions)
	{
		string str = string.Join(";", extensions);
		string str2 = "<" + string.Join("|", Array.ConvertAll(roots, (string root) => "path:" + QuotePath(PathWithTrailingSeparator(root)))) + ">";
		return "file: " + str2 + " <ext:" + str + ">";
	}

	internal static string BuildSiblingFilesQuery(string[] roots, string[] extensions)
	{
		string str = string.Join(";", extensions);
		string str2 = "<" + string.Join("|", Array.ConvertAll(roots, (string root) => "path:" + QuotePath(PathWithTrailingSeparator(root)))) + ">";
		return "file: " + str2 + " sibling:<ext:" + str + ">";
	}

	internal static BmsScanExecutionResult ExecuteScan(string bmsQuery, string siblingQuery)
	{
		if (!TryExecuteBridgeScan(bmsQuery, siblingQuery, out var result, out var reason, out var elapsedMs))
		{
			return Failed(reason, elapsedMs);
		}
		if (result == null || !result.Success || result.Result == null || result.Result.BmsFilePaths.Count == 0)
		{
			return Failed("bridge_empty_result", elapsedMs);
		}
		return result;
	}

	private static bool TryExecuteBridgeScan(string bmsQuery, string siblingQuery, out BmsScanExecutionResult result, out string reason, out long elapsedMs)
	{
		result = null;
		elapsedMs = 0L;
		Stopwatch stopwatch = Stopwatch.StartNew();
		IntPtr intPtr = IntPtr.Zero;
		try
		{
			if (!EnsureBridgeLoaded(out reason))
			{
				return false;
			}
			int num = EBridge_Scan(bmsQuery, siblingQuery, out intPtr);
			if (num != 0)
			{
				reason = "bridge_scan_failed:" + num;
				return false;
			}
			if (intPtr == IntPtr.Zero)
			{
				reason = "bridge_scan_empty_result";
				return false;
			}
			EBridgeResultHeader eBridgeResultHeader = Marshal.PtrToStructure<EBridgeResultHeader>(intPtr);
			if (eBridgeResultHeader.status != 0)
			{
				reason = "bridge_status_failed:" + eBridgeResultHeader.error_code;
				return false;
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, uint[]> dictionary = new Dictionary<string, uint[]>((int)eBridgeResultHeader.dir_count, StringComparer.OrdinalIgnoreCase);
			ulong num2 = 0uL;
			for (ulong i = 0uL; i < eBridgeResultHeader.bms_count; i += 1)
			{
				uint byteOffset = (uint)Marshal.ReadInt32(eBridgeResultHeader.bms_offsets, checked((int)(i * 4)));
				string text = ReadUtf16FromBlob(eBridgeResultHeader.bms_blob, byteOffset);
				if (!string.IsNullOrWhiteSpace(text))
				{
					hashSet.Add(text);
				}
			}
			for (ulong j = 0uL; j < eBridgeResultHeader.dir_count; j += 1)
			{
				uint byteOffset2 = (uint)Marshal.ReadInt32(eBridgeResultHeader.dir_offsets, checked((int)(j * 4)));
				string text2 = ReadUtf16FromBlob(eBridgeResultHeader.dir_blob, byteOffset2);
				uint num3 = (uint)Marshal.ReadInt32(eBridgeResultHeader.dir_hash_offsets, checked((int)(j * 4)));
				uint num4 = (uint)Marshal.ReadInt32(eBridgeResultHeader.dir_hash_lengths, checked((int)(j * 4)));
				if (string.IsNullOrWhiteSpace(text2) || num4 == 0)
				{
					continue;
				}
				int[] array = new int[checked((int)num4)];
				Marshal.Copy(IntPtr.Add(eBridgeResultHeader.hashes_blob, checked((int)num3)), array, 0, array.Length);
				uint[] array2 = new uint[array.Length];
				Buffer.BlockCopy(array, 0, array2, 0, array.Length * 4);
				dictionary[text2] = array2;
				num2 += (ulong)array2.Length;
			}
			result = new BmsScanExecutionResult
			{
				Success = true,
				NativeBridgeUsed = true,
				NativeBridgeMs = stopwatch.ElapsedMilliseconds,
				NativeBridgeReason = "ok",
				HashBuildMs = 0L,
				HashDirCount = (ulong)dictionary.Count,
				HashEntryCount = num2,
				Result = new BmsScanResult
				{
					BmsFilePaths = hashSet,
					FilesByDirectory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
					FileNameHashesByDirectory = dictionary
				}
			};
			reason = "ok";
			return true;
		}
		catch (Exception ex)
		{
			reason = "bridge_exception:" + ex.Message;
			return false;
		}
		finally
		{
			stopwatch.Stop();
			elapsedMs = stopwatch.ElapsedMilliseconds;
			if (intPtr != IntPtr.Zero)
			{
				try
				{
					EBridge_FreeResult(intPtr);
				}
				catch
				{
				}
			}
		}
	}

	private static string ReadUtf16FromBlob(IntPtr blobBase, uint byteOffset)
	{
		if (blobBase == IntPtr.Zero)
		{
			return null;
		}
		return Marshal.PtrToStringUni(IntPtr.Add(blobBase, checked((int)byteOffset)));
	}

	private static BmsScanExecutionResult Failed(string reason, long bridgeMs = 0L)
	{
		return new BmsScanExecutionResult
		{
			Success = false,
			ErrorReason = reason,
			NativeBridgeUsed = false,
			NativeBridgeMs = bridgeMs,
			NativeBridgeReason = reason
		};
	}

	private static string PathWithTrailingSeparator(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return path;
		}
		if (!path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
		{
			return path + Path.DirectorySeparatorChar;
		}
		return path;
	}

	private static string QuotePath(string path)
	{
		return "\"" + (path ?? string.Empty).Replace("\"", "\"\"") + "\"";
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr LoadLibraryW(string lpFileName);

	[DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

	[DllImport(BridgeDllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_Scan")]
	private static extern int EBridge_Scan(string bmsQuery, string siblingQuery, out IntPtr outResult);

	[DllImport(BridgeDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_FreeResult")]
	private static extern void EBridge_FreeResult(IntPtr result);

	[StructLayout(LayoutKind.Sequential)]
	private struct EBridgeResultHeader
	{
		public int status;

		public int error_code;

		public ulong bms_count;

		public IntPtr bms_offsets;

		public IntPtr bms_blob;

		public ulong dir_count;

		public IntPtr dir_offsets;

		public IntPtr dir_blob;

		public IntPtr dir_hash_offsets;

		public IntPtr dir_hash_lengths;

		public IntPtr hashes_blob;

		public ulong raw_buffer_size;
	}
}
