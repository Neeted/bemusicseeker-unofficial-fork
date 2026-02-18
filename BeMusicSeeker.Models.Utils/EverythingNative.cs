using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BeMusicSeeker.Models.Utils;

internal static class EverythingNative
{
	private const string DllName = "Everything3_x64.dll";

	private const uint EverythingPropertyIdName = 0u;

	private const uint EverythingPropertyIdPath = 1u;

	private static IntPtr loadedModule = IntPtr.Zero;

	internal static string GetExpectedDllPath()
	{
		return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", DllName);
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
		string path = GetExpectedDllPath();
		if (!File.Exists(path))
		{
			reason = "dll_not_found:" + path;
			return false;
		}
		IntPtr module = LoadLibraryW(path);
		if (module == IntPtr.Zero)
		{
			reason = "dll_load_failed:" + Marshal.GetLastWin32Error();
			return false;
		}
		loadedModule = module;
		return true;
	}

	internal static string BuildBmsFilesQuery(string[] roots, string[] extensions)
	{
		string extList = string.Join(";", extensions);
		string rootGroup = "<" + string.Join("|", Array.ConvertAll(roots, (string root) => "path:" + QuotePath(PathWithTrailingSeparator(root)))) + ">";
		return "file: " + rootGroup + " <ext:" + extList + ">";
	}

	internal static string BuildSiblingFilesQuery(string[] roots, string[] extensions)
	{
		string extList = string.Join(";", extensions);
		string rootGroup = "<" + string.Join("|", Array.ConvertAll(roots, (string root) => "path:" + QuotePath(PathWithTrailingSeparator(root)))) + ">";
		return "file: " + rootGroup + " sibling:<ext:" + extList + ">";
	}

	internal static BmsScanExecutionResult ExecuteScan(string bmsQuery, string siblingQuery, string[] bmsExtensions, bool verboseLog = false)
	{
		_ = bmsExtensions;
		_ = verboseLog;
		IntPtr client = IntPtr.Zero;
		try
		{
			Stopwatch stopwatchConnect = Stopwatch.StartNew();
			client = TryConnect(out var connectInfo);
			stopwatchConnect.Stop();
			if (client == IntPtr.Zero)
			{
				return Failed("connect_failed:" + connectInfo);
			}

			HashSet<string> bmsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

			if (!TryExecuteQuery(client, bmsQuery, out var bmsQueryMs, out var bmsSearchMs, out var bmsReadMs, out var bmsHitCount, out var bmsQueryErrorReason, delegate(string dir, string name)
			{
				if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name))
				{
					return;
				}
				if (!map.TryGetValue(dir, out var bmsNames))
				{
					bmsNames = new List<string>();
					map[dir] = bmsNames;
				}
				bmsNames.Add(name);
				bmsSet.Add(CombinePathAndName(dir, name));
			}))
			{
				return Failed(bmsQueryErrorReason);
			}

			if (!TryExecuteQuery(client, siblingQuery, out var siblingQueryMs, out var siblingSearchMs, out var siblingReadMs, out var siblingHitCount, out var siblingQueryErrorReason, delegate(string dir, string name)
			{
				if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name))
				{
					return;
				}
				if (!map.TryGetValue(dir, out var siblingNames))
				{
					return;
				}
				siblingNames.Add(name);
			}))
			{
				return Failed(siblingQueryErrorReason);
			}

			Stopwatch stopwatchBuild = Stopwatch.StartNew();
			Dictionary<string, List<string>> filesByDirectory = map;
			stopwatchBuild.Stop();

			return new BmsScanExecutionResult
			{
				Success = true,
				ConnectMs = stopwatchConnect.ElapsedMilliseconds,
				BmsQueryMs = bmsQueryMs,
				BmsSearchMs = bmsSearchMs,
				BmsReadMs = bmsReadMs,
				SiblingQueryMs = siblingQueryMs,
				SiblingSearchMs = siblingSearchMs,
				SiblingReadMs = siblingReadMs,
				BuildResultMs = stopwatchBuild.ElapsedMilliseconds,
				BmsQueryHitCount = bmsHitCount,
				SiblingQueryHitCount = siblingHitCount,
				Result = new BmsScanResult
				{
					BmsFilePaths = bmsSet,
					FilesByDirectory = filesByDirectory
				}
			};
		}
		catch (Exception ex)
		{
			return Failed("exception:" + ex.Message);
		}
		finally
		{
			if (client != IntPtr.Zero)
			{
				Everything3_DestroyClient(client);
			}
		}
	}

	private static string PathWithTrailingSeparator(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return path;
		}
		return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? path : path + Path.DirectorySeparatorChar;
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
			UIntPtr size = new UIntPtr((uint)buffer.Capacity);
			UIntPtr len = Everything3_GetResultPathW(resultList, index, buffer, size);
			ulong value = len.ToUInt64();
			if (value == 0)
			{
				return false;
			}
			if (value < (ulong)buffer.Capacity)
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
			UIntPtr size = new UIntPtr((uint)buffer.Capacity);
			UIntPtr len = Everything3_GetResultNameW(resultList, index, buffer, size);
			ulong value = len.ToUInt64();
			if (value == 0)
			{
				return false;
			}
			if (value < (ulong)buffer.Capacity)
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
		IntPtr state = IntPtr.Zero;
		IntPtr result = IntPtr.Zero;
		Stopwatch stopwatchTotal = Stopwatch.StartNew();
		Stopwatch stopwatchSearch = new Stopwatch();
		Stopwatch stopwatchRead = new Stopwatch();
		try
		{
			state = Everything3_CreateSearchState();
			if (state == IntPtr.Zero)
			{
				errorReason = "create_search_state_failed:" + Everything3_GetLastError();
				return false;
			}
			if (!Everything3_SetSearchTextW(state, query))
			{
				errorReason = "set_search_text_failed:" + Everything3_GetLastError();
				return false;
			}
			Everything3_ClearSearchPropertyRequests(state);
			Everything3_AddSearchPropertyRequest(state, EverythingPropertyIdPath);
			Everything3_AddSearchPropertyRequest(state, EverythingPropertyIdName);
			Everything3_SetSearchViewportOffset(state, UIntPtr.Zero);
			Everything3_SetSearchViewportCount(state, new UIntPtr(ulong.MaxValue));

			stopwatchSearch.Start();
			result = Everything3_Search(client, state);
			stopwatchSearch.Stop();
			if (result == IntPtr.Zero)
			{
				errorReason = "search_failed:" + Everything3_GetLastError();
				return false;
			}
			uint err = Everything3_GetLastError();
			if (err != 0)
			{
				errorReason = "search_error:" + err;
				return false;
			}

			hitCount = Everything3_GetResultListViewportCount(result).ToUInt64();
			StringBuilder pathBuffer = new StringBuilder(1024);
			StringBuilder nameBuffer = new StringBuilder(512);
			stopwatchRead.Start();
			for (ulong i = 0; i < hitCount; i++)
			{
				if (!TryGetResultPath(result, new UIntPtr(i), pathBuffer, out var path))
				{
					continue;
				}
				if (!TryGetResultName(result, new UIntPtr(i), nameBuffer, out var name))
				{
					continue;
				}
				onResult?.Invoke(path, name);
			}
			stopwatchRead.Stop();
			return true;
		}
		catch (Exception ex)
		{
			errorReason = "query_exception:" + ex.Message;
			return false;
		}
		finally
		{
			stopwatchTotal.Stop();
			elapsedMs = stopwatchTotal.ElapsedMilliseconds;
			searchMs = stopwatchSearch.ElapsedMilliseconds;
			readMs = stopwatchRead.ElapsedMilliseconds;
			if (result != IntPtr.Zero)
			{
				Everything3_DestroyResultList(result);
			}
			if (state != IntPtr.Zero)
			{
				Everything3_DestroySearchState(state);
			}
		}
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
		string[] instances = new string[2] { "1.5a", null };
		foreach (string instanceName in instances)
		{
			for (int attempt = 0; attempt < 20; attempt++)
			{
				IntPtr client = Everything3_ConnectW(instanceName);
				if (client != IntPtr.Zero)
				{
					connectInfo = "instance=" + (instanceName ?? "(default)") + " attempt=" + (attempt + 1);
					return client;
				}
				uint error = Everything3_GetLastError();
				connectInfo = "instance=" + (instanceName ?? "(default)") + " error=" + error + " attempt=" + (attempt + 1);
				if (error != 3758096386u)
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
}
