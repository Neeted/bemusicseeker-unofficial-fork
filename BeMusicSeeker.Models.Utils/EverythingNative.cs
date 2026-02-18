using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BeMusicSeeker.Models.Utils;

internal static class EverythingNative
{
	private const string DllName = "Everything3_x64.dll";

	private const string BridgeDllName = "EverythingBridge_x64.dll";

	private const uint EverythingPropertyIdName = 0u;

	private const uint EverythingPropertyIdPath = 1u;

	private static IntPtr loadedModule = IntPtr.Zero;

	private static IntPtr loadedBridgeModule = IntPtr.Zero;

	private static bool bridgeExportsChecked;

	private static bool bridgeExportsAvailable;

	internal static string GetExpectedDllPath()
	{
		return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", DllName);
	}

	internal static string GetExpectedBridgeDllPath()
	{
		return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", BridgeDllName);
	}

	internal static bool EnsureLoaded(out string reason)
	{
		reason = null;
		if (IntPtr.Size != 8)
		{
			reason = "unsupported_architecture";
			return false;
		}
		if (loadedModule != IntPtr.Zero)
		{
			return true;
		}
		string expectedDllPath = GetExpectedDllPath();
		if (!File.Exists(expectedDllPath))
		{
			reason = "dll_not_found:" + expectedDllPath;
			return false;
		}
		IntPtr intPtr = LoadLibraryW(expectedDllPath);
		if (intPtr == IntPtr.Zero)
		{
			reason = "dll_load_failed:" + Marshal.GetLastWin32Error();
			return false;
		}
		loadedModule = intPtr;
		return true;
	}

	private static bool EnsureBridgeLoaded(out string reason)
	{
		reason = null;
		if (IntPtr.Size != 8)
		{
			reason = "unsupported_architecture";
			return false;
		}
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

	internal static BmsScanExecutionResult ExecuteScan(string bmsQuery, string siblingQuery, string[] bmsExtensions, bool verboseLog = false, bool collectDetailedFiles = false)
	{
		_ = bmsExtensions;
		_ = verboseLog;
		string text = "not_attempted";
		long num = 0L;
		if (!collectDetailedFiles && TryExecuteBridgeScan(bmsQuery, siblingQuery, out var result, out text, out num))
		{
			if (result != null && result.Success && result.Result != null && result.Result.BmsFilePaths.Count > 0)
			{
				return result;
			}
			text = "bridge_empty_result";
		}
		if (collectDetailedFiles)
		{
			text = "disabled_for_verify";
		}
		IntPtr intPtr = IntPtr.Zero;
		try
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			intPtr = TryConnect(out var connectInfo);
			stopwatch.Stop();
			if (intPtr == IntPtr.Zero)
			{
				return Failed("connect_failed:" + connectInfo);
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, HashSet<uint>> dictionary = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, HashSet<string>> dictionary2 = (collectDetailedFiles ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) : null);
			if (!TryExecuteQuery(intPtr, bmsQuery, out var elapsedMs, out var searchMs, out var readMs, out var hitCount, out var errorReason, delegate(string dir, string name)
			{
				if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(name))
				{
					hashSet.Add(CombinePathAndName(dir, name));
					hashSet2.Add(dir);
					AddFileNameHash(dictionary, dir, name);
					if (collectDetailedFiles)
					{
						AddDetailedFileName(dictionary2, dir, name);
					}
				}
			}))
			{
				return Failed(errorReason);
			}
			if (!TryExecuteQuery(intPtr, siblingQuery, out var elapsedMs2, out var searchMs2, out var readMs2, out var hitCount2, out var errorReason2, delegate(string dir, string name)
			{
				if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(name) && hashSet2.Contains(dir))
				{
					AddFileNameHash(dictionary, dir, name);
					if (collectDetailedFiles)
					{
						AddDetailedFileName(dictionary2, dir, name);
					}
				}
			}))
			{
				return Failed(errorReason2);
			}
			Stopwatch stopwatch2 = Stopwatch.StartNew();
			Dictionary<string, uint[]> dictionary3 = new Dictionary<string, uint[]>(dictionary.Count, StringComparer.OrdinalIgnoreCase);
			ulong num2 = 0uL;
			foreach (KeyValuePair<string, HashSet<uint>> item in dictionary)
			{
				uint[] array = item.Value.ToArray();
				dictionary3[item.Key] = array;
				num2 += (ulong)array.Length;
			}
			stopwatch2.Stop();
			Stopwatch stopwatch3 = Stopwatch.StartNew();
			Dictionary<string, List<string>> dictionary4 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			if (collectDetailedFiles && dictionary2 != null)
			{
				foreach (KeyValuePair<string, HashSet<string>> item2 in dictionary2)
				{
					dictionary4[item2.Key] = item2.Value.ToList();
				}
			}
			stopwatch3.Stop();
			return new BmsScanExecutionResult
			{
				Success = true,
				NativeBridgeUsed = false,
				NativeBridgeMs = num,
				NativeBridgeReason = text,
				ConnectMs = stopwatch.ElapsedMilliseconds,
				BmsQueryMs = elapsedMs,
				BmsSearchMs = searchMs,
				BmsReadMs = readMs,
				SiblingQueryMs = elapsedMs2,
				SiblingSearchMs = searchMs2,
				SiblingReadMs = readMs2,
				BuildResultMs = stopwatch3.ElapsedMilliseconds,
				HashBuildMs = stopwatch2.ElapsedMilliseconds,
				HashDirCount = (ulong)dictionary3.Count,
				HashEntryCount = num2,
				BmsQueryHitCount = hitCount,
				SiblingQueryHitCount = hitCount2,
				Result = new BmsScanResult
				{
					BmsFilePaths = hashSet,
					FilesByDirectory = dictionary4,
					FileNameHashesByDirectory = dictionary3
				}
			};
		}
		catch (Exception ex)
		{
			return Failed("exception:" + ex.Message);
		}
		finally
		{
			if (intPtr != IntPtr.Zero)
			{
				Everything3_DestroyClient(intPtr);
			}
		}
	}

	private static void AddFileNameHash(Dictionary<string, HashSet<uint>> hashesByDirectory, string directoryPath, string fileName)
	{
		if (!hashesByDirectory.TryGetValue(directoryPath, out var value))
		{
			value = new HashSet<uint>();
			hashesByDirectory[directoryPath] = value;
		}
		value.Add(BMSDirectoryFileNameHash.GetFileNameHash(fileName));
	}

	private static void AddDetailedFileName(Dictionary<string, HashSet<string>> filesByDirectory, string directoryPath, string fileName)
	{
		if (!filesByDirectory.TryGetValue(directoryPath, out var value))
		{
			value = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			filesByDirectory[directoryPath] = value;
		}
		value.Add(fileName);
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

	private static string CombinePathAndName(string directoryPath, string fileName)
	{
		if (string.IsNullOrEmpty(directoryPath))
		{
			return fileName ?? string.Empty;
		}
		char c = directoryPath[directoryPath.Length - 1];
		if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
		{
			return directoryPath + (fileName ?? string.Empty);
		}
		return directoryPath + Path.DirectorySeparatorChar + (fileName ?? string.Empty);
	}

	private static bool TryGetResultPath(IntPtr resultList, UIntPtr index, StringBuilder buffer, out string path)
	{
		path = null;
		for (int i = 0; i < 4; i++)
		{
			buffer.Length = 0;
			UIntPtr wbuf_size_in_wchars = new UIntPtr((uint)buffer.Capacity);
			ulong num = Everything3_GetResultPathW(resultList, index, buffer, wbuf_size_in_wchars).ToUInt64();
			if (num == 0)
			{
				return false;
			}
			if (num < (ulong)buffer.Capacity)
			{
				path = buffer.ToString();
				return true;
			}
			buffer.EnsureCapacity(buffer.Capacity * 2);
		}
		return false;
	}

	private static bool TryGetResultName(IntPtr resultList, UIntPtr index, StringBuilder buffer, out string name)
	{
		name = null;
		for (int i = 0; i < 4; i++)
		{
			buffer.Length = 0;
			UIntPtr wbuf_size_in_wchars = new UIntPtr((uint)buffer.Capacity);
			ulong num = Everything3_GetResultNameW(resultList, index, buffer, wbuf_size_in_wchars).ToUInt64();
			if (num == 0)
			{
				return false;
			}
			if (num < (ulong)buffer.Capacity)
			{
				name = buffer.ToString();
				return true;
			}
			buffer.EnsureCapacity(buffer.Capacity * 2);
		}
		return false;
	}

	private static bool TryExecuteQuery(IntPtr client, string query, out long elapsedMs, out long searchMs, out long readMs, out ulong hitCount, out string errorReason, Action<string, string> onResult)
	{
		elapsedMs = 0L;
		searchMs = 0L;
		readMs = 0L;
		hitCount = 0uL;
		errorReason = null;
		IntPtr intPtr = IntPtr.Zero;
		IntPtr intPtr2 = IntPtr.Zero;
		Stopwatch stopwatch = Stopwatch.StartNew();
		Stopwatch stopwatch2 = new Stopwatch();
		Stopwatch stopwatch3 = new Stopwatch();
		try
		{
			intPtr = Everything3_CreateSearchState();
			if (intPtr == IntPtr.Zero)
			{
				errorReason = "create_search_state_failed:" + Everything3_GetLastError();
				return false;
			}
			if (!Everything3_SetSearchTextW(intPtr, query))
			{
				errorReason = "set_search_text_failed:" + Everything3_GetLastError();
				return false;
			}
			Everything3_ClearSearchPropertyRequests(intPtr);
			Everything3_AddSearchPropertyRequest(intPtr, EverythingPropertyIdPath);
			Everything3_AddSearchPropertyRequest(intPtr, EverythingPropertyIdName);
			Everything3_SetSearchViewportOffset(intPtr, UIntPtr.Zero);
			Everything3_SetSearchViewportCount(intPtr, new UIntPtr(ulong.MaxValue));
			stopwatch2.Start();
			intPtr2 = Everything3_Search(client, intPtr);
			stopwatch2.Stop();
			if (intPtr2 == IntPtr.Zero)
			{
				errorReason = "search_failed:" + Everything3_GetLastError();
				return false;
			}
			uint num = Everything3_GetLastError();
			if (num != 0)
			{
				errorReason = "search_error:" + num;
				return false;
			}
			hitCount = Everything3_GetResultListViewportCount(intPtr2).ToUInt64();
			StringBuilder stringBuilder = new StringBuilder(1024);
			StringBuilder stringBuilder2 = new StringBuilder(512);
			stopwatch3.Start();
			for (ulong i = 0uL; i < hitCount; i += 1)
			{
				if (TryGetResultPath(intPtr2, new UIntPtr(i), stringBuilder, out var path) && TryGetResultName(intPtr2, new UIntPtr(i), stringBuilder2, out var name))
				{
					onResult?.Invoke(path, name);
				}
			}
			stopwatch3.Stop();
			return true;
		}
		catch (Exception ex)
		{
			errorReason = "query_exception:" + ex.Message;
			return false;
		}
		finally
		{
			stopwatch.Stop();
			elapsedMs = stopwatch.ElapsedMilliseconds;
			searchMs = stopwatch2.ElapsedMilliseconds;
			readMs = stopwatch3.ElapsedMilliseconds;
			if (intPtr2 != IntPtr.Zero)
			{
				Everything3_DestroyResultList(intPtr2);
			}
			if (intPtr != IntPtr.Zero)
			{
				Everything3_DestroySearchState(intPtr);
			}
		}
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

	private static BmsScanExecutionResult Failed(string reason)
	{
		return new BmsScanExecutionResult
		{
			Success = false,
			ErrorReason = reason
		};
	}

	private static IntPtr TryConnect(out string connectInfo)
	{
		connectInfo = "no_instance_tried";
		string[] array = new string[2] { "1.5a", null };
		foreach (string text in array)
		{
			for (int i = 0; i < 20; i++)
			{
				IntPtr intPtr = Everything3_ConnectW(text);
				if (intPtr != IntPtr.Zero)
				{
					connectInfo = "instance=" + (text ?? "(default)") + " attempt=" + (i + 1);
					return intPtr;
				}
				uint lastError = Everything3_GetLastError();
				connectInfo = "instance=" + (text ?? "(default)") + " error=" + lastError + " attempt=" + (i + 1);
				if (lastError != 3758096386u)
				{
					break;
				}
				Thread.Sleep(100);
			}
		}
		return IntPtr.Zero;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr LoadLibraryW(string lpFileName);

	[DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

	[DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
	internal static extern IntPtr Everything3_ConnectW(string instance_name);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern bool Everything3_DestroyClient(IntPtr client);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern uint Everything3_GetLastError();

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern IntPtr Everything3_CreateSearchState();

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern bool Everything3_DestroySearchState(IntPtr search_state);

	[DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
	internal static extern bool Everything3_SetSearchTextW(IntPtr search_state, string search);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern bool Everything3_ClearSearchPropertyRequests(IntPtr search_state);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern bool Everything3_AddSearchPropertyRequest(IntPtr search_state, uint property_id);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern bool Everything3_SetSearchViewportOffset(IntPtr search_state, UIntPtr offset);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern bool Everything3_SetSearchViewportCount(IntPtr search_state, UIntPtr count);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern IntPtr Everything3_Search(IntPtr client, IntPtr search_state);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern bool Everything3_DestroyResultList(IntPtr result_list);

	[DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
	internal static extern UIntPtr Everything3_GetResultListViewportCount(IntPtr result_list);

	[DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
	internal static extern UIntPtr Everything3_GetResultPathW(IntPtr result_list, UIntPtr result_index, StringBuilder out_wbuf, UIntPtr wbuf_size_in_wchars);

	[DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
	internal static extern UIntPtr Everything3_GetResultNameW(IntPtr result_list, UIntPtr result_index, StringBuilder out_wbuf, UIntPtr wbuf_size_in_wchars);

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
