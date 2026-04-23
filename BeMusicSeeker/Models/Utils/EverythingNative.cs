using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace BeMusicSeeker.Models.Utils;

internal static class EverythingNative
{
	private const string BridgeDllName = "EverythingBridge_x64.dll";

	internal const string GroupedEnumerationBackendName = "everything_bridge";

	internal const string FixedScanNativeBridgeReason = "everything_bridge_fixed_scan";

	private static IntPtr loadedBridgeModule = IntPtr.Zero;

	private static bool bridgeExportsProbed;

	private static bool bridgeFixedScanAvailable;

	private static bool bridgeFreeResultAvailable;

	private static bool bridgeGroupedEnumerationQueryAvailable;

	private static bool bridgeFreeGroupedEnumerationResultAvailable;

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
			IntPtr module = LoadLibraryW(expectedBridgeDllPath);
			if (module == IntPtr.Zero)
			{
				reason = "bridge_dll_load_failed:" + Marshal.GetLastWin32Error();
				return false;
			}
			loadedBridgeModule = module;
		}
		ProbeBridgeExports();
		return true;
	}

	private static void ProbeBridgeExports()
	{
		if (bridgeExportsProbed)
		{
			return;
		}
		bridgeFixedScanAvailable = GetProcAddress(loadedBridgeModule, "EBridge_ScanChartAndResources") != IntPtr.Zero;
		bridgeFreeResultAvailable = GetProcAddress(loadedBridgeModule, "EBridge_FreeResult") != IntPtr.Zero;
		bridgeGroupedEnumerationQueryAvailable = GetProcAddress(loadedBridgeModule, "EBridge_EnumerateGroupedFiles") != IntPtr.Zero;
		bridgeFreeGroupedEnumerationResultAvailable = GetProcAddress(loadedBridgeModule, "EBridge_FreeGroupedFilesResult") != IntPtr.Zero;
		bridgeExportsProbed = true;
	}

	private static bool EnsureFixedScanAvailable(out string reason)
	{
		if (!EnsureBridgeLoaded(out reason))
		{
			return false;
		}
		if (!bridgeFixedScanAvailable || !bridgeFreeResultAvailable)
		{
			reason = "bridge_fixed_scan_export_missing";
			return false;
		}
		reason = null;
		return true;
	}

	private static bool EnsureGroupedEnumerationAvailable(out string reason)
	{
		if (!EnsureBridgeLoaded(out reason))
		{
			return false;
		}
		if (!bridgeGroupedEnumerationQueryAvailable || !bridgeFreeGroupedEnumerationResultAvailable)
		{
			reason = "bridge_grouped_enumeration_export_missing";
			return false;
		}
		reason = null;
		return true;
	}

	internal static string BuildFilesQuery(string[] roots, string[] extensions)
	{
		string ext = string.Join(";", extensions ?? Array.Empty<string>());
		string paths = "<" + string.Join("|", Array.ConvertAll(roots ?? Array.Empty<string>(), (string root) => "path:" + QuotePath(PathWithTrailingSeparator(root)))) + ">";
		return "file: " + paths + " <ext:" + ext + ">";
	}

	internal static string BuildAllFilesQuery(string[] roots)
	{
		string paths = "<" + string.Join("|", Array.ConvertAll(roots ?? Array.Empty<string>(), (string root) => "path:" + QuotePath(PathWithTrailingSeparator(root)))) + ">";
		return "file: " + paths;
	}

	internal static bool TryEnumerateGroupedFiles(IReadOnlyList<BridgeGroupedQuery> queries, out BridgeGroupedEnumerationResult result, out string reason)
	{
		result = null;
		reason = null;
		if (queries == null || queries.Count == 0)
		{
			result = new BridgeGroupedEnumerationResult();
			return true;
		}
		if (!EnsureGroupedEnumerationAvailable(out reason))
		{
			return false;
		}

		IntPtr resultPtr = IntPtr.Zero;
		IntPtr nativeQueries = IntPtr.Zero;
		List<IntPtr> allocatedStrings = new List<IntPtr>();
		try
		{
			nativeQueries = Marshal.AllocHGlobal(checked(Marshal.SizeOf<EBridgeGroupedQueryNative>() * queries.Count));
			for (int i = 0; i < queries.Count; i++)
			{
				IntPtr queryText = Marshal.StringToHGlobalUni(queries[i].QueryText ?? string.Empty);
				allocatedStrings.Add(queryText);
				EBridgeGroupedQueryNative nativeQuery = new EBridgeGroupedQueryNative
				{
					group_id = queries[i].GroupId,
					query_text = queryText
				};
				Marshal.StructureToPtr(nativeQuery, IntPtr.Add(nativeQueries, i * Marshal.SizeOf<EBridgeGroupedQueryNative>()), false);
			}

			int status = EBridge_EnumerateGroupedFiles(nativeQueries, (uint)queries.Count, out resultPtr);
			if (status != 0)
			{
				reason = "bridge_grouped_query_failed:" + status;
				return false;
			}
			if (resultPtr == IntPtr.Zero)
			{
				reason = "bridge_grouped_query_empty_result";
				return false;
			}

			EBridgeGroupedFilesResultHeader header = Marshal.PtrToStructure<EBridgeGroupedFilesResultHeader>(resultPtr);
			if (header.status != 0)
			{
				reason = "bridge_grouped_status_failed:" + header.error_code;
				return false;
			}

			result = ReadGroupedEnumerationResult(header);
			reason = "ok";
			return true;
		}
		catch (Exception ex)
		{
			reason = "bridge_grouped_exception:" + ex.Message;
			return false;
		}
		finally
		{
			if (resultPtr != IntPtr.Zero)
			{
				try
				{
					EBridge_FreeGroupedFilesResult(resultPtr);
				}
				catch
				{
				}
			}
			foreach (IntPtr queryText in allocatedStrings)
			{
				if (queryText != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(queryText);
				}
			}
			if (nativeQueries != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(nativeQueries);
			}
		}
	}

	internal static BmsScanExecutionResult ExecuteScan(string chartQuery, string audioQuery, string imageQuery, string movieQuery)
	{
		if (!TryExecuteBridgeScan(chartQuery, audioQuery, imageQuery, movieQuery, out BmsScanExecutionResult result, out string reason, out long elapsedMs))
		{
			return Failed(reason, elapsedMs);
		}
		if (result == null || !result.Success || result.Result == null || result.Result.ChartFilePaths.Count == 0)
		{
			return Failed("bridge_empty_result", elapsedMs);
		}
		return result;
	}

	private static bool TryExecuteBridgeScan(string chartQuery, string audioQuery, string imageQuery, string movieQuery, out BmsScanExecutionResult result, out string reason, out long elapsedMs)
	{
		result = null;
		reason = null;
		elapsedMs = 0L;
		IntPtr resultPtr = IntPtr.Zero;
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			if (!EnsureFixedScanAvailable(out reason))
			{
				return false;
			}

			Stopwatch nativeBridgeStopwatch = Stopwatch.StartNew();
			int status = EBridge_ScanChartAndResources(chartQuery, audioQuery, imageQuery, movieQuery, out resultPtr);
			nativeBridgeStopwatch.Stop();
			long nativeBridgeMs = nativeBridgeStopwatch.ElapsedMilliseconds;
			if (status != 0)
			{
				reason = "bridge_scan_failed:" + status;
				return false;
			}
			if (resultPtr == IntPtr.Zero)
			{
				reason = "bridge_scan_empty_result";
				return false;
			}
			EBridgeResultHeader header = Marshal.PtrToStructure<EBridgeResultHeader>(resultPtr);
			if (header.status != 0)
			{
				reason = "bridge_status_failed:" + header.error_code;
				return false;
			}

			Stopwatch decodeStopwatch = Stopwatch.StartNew();
			FixedScanDecodedResult decodedResult = DecodeFixedScanResult(header);
			decodeStopwatch.Stop();

			Stopwatch materializeStopwatch = Stopwatch.StartNew();
			result = CreateExecutionResult(header, nativeBridgeMs, decodeStopwatch.ElapsedMilliseconds, decodedResult);
			materializeStopwatch.Stop();
			result.ManagedMaterializeMs = materializeStopwatch.ElapsedMilliseconds;
			result.BridgeRawBufferBytes = header.raw_buffer_size;

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
			if (resultPtr != IntPtr.Zero)
			{
				try
				{
					EBridge_FreeResult(resultPtr);
				}
				catch
				{
				}
			}
		}
	}

	private static FixedScanDecodedResult DecodeFixedScanResult(EBridgeResultHeader header)
	{
		string[] chartPaths = ReadStringArray(header.chart_count, header.chart_offsets, header.chart_blob);
		string[] chartDirectories = ReadStringArray(header.dir_count, header.dir_offsets, header.dir_blob);
		return new FixedScanDecodedResult
		{
			ChartPaths = chartPaths,
			ChartDirectories = chartDirectories,
			AllBaseHashes = ReadHashGroupArray(header.dir_count, header.all_hash_offsets, header.all_hash_lengths, header.all_hashes_blob),
			AudioBaseHashes = ReadHashGroupArray(header.dir_count, header.audio_base_hash_offsets, header.audio_base_hash_lengths, header.audio_base_hashes_blob),
			ImageBaseHashes = ReadHashGroupArray(header.dir_count, header.image_base_hash_offsets, header.image_base_hash_lengths, header.image_base_hashes_blob),
			MovieBaseHashes = ReadHashGroupArray(header.dir_count, header.movie_base_hash_offsets, header.movie_base_hash_lengths, header.movie_base_hashes_blob),
			AudioRelativeHashes = ReadHashGroupArray(header.dir_count, header.audio_relative_hash_offsets, header.audio_relative_hash_lengths, header.audio_relative_hashes_blob),
			ImageRelativeHashes = ReadHashGroupArray(header.dir_count, header.image_relative_hash_offsets, header.image_relative_hash_lengths, header.image_relative_hashes_blob),
			MovieRelativeHashes = ReadHashGroupArray(header.dir_count, header.movie_relative_hash_offsets, header.movie_relative_hash_lengths, header.movie_relative_hashes_blob),
			SelfOwnedAllBaseHashes = ReadHashGroupArray(header.dir_count, header.self_all_hash_offsets, header.self_all_hash_lengths, header.self_all_hashes_blob),
			SelfOwnedAudioBaseHashes = ReadHashGroupArray(header.dir_count, header.self_audio_base_hash_offsets, header.self_audio_base_hash_lengths, header.self_audio_base_hashes_blob),
			SelfOwnedImageBaseHashes = ReadHashGroupArray(header.dir_count, header.self_image_base_hash_offsets, header.self_image_base_hash_lengths, header.self_image_base_hashes_blob),
			SelfOwnedMovieBaseHashes = ReadHashGroupArray(header.dir_count, header.self_movie_base_hash_offsets, header.self_movie_base_hash_lengths, header.self_movie_base_hashes_blob),
			SelfOwnedAudioRelativeHashes = ReadHashGroupArray(header.dir_count, header.self_audio_relative_hash_offsets, header.self_audio_relative_hash_lengths, header.self_audio_relative_hashes_blob),
			SelfOwnedImageRelativeHashes = ReadHashGroupArray(header.dir_count, header.self_image_relative_hash_offsets, header.self_image_relative_hash_lengths, header.self_image_relative_hashes_blob),
			SelfOwnedMovieRelativeHashes = ReadHashGroupArray(header.dir_count, header.self_movie_relative_hash_offsets, header.self_movie_relative_hash_lengths, header.self_movie_relative_hashes_blob)
		};
	}

	private static string[] ReadStringArray(ulong count, IntPtr offsets, IntPtr blob)
	{
		string[] values = new string[checked((int)count)];
		for (ulong i = 0; i < count; i += 1)
		{
			uint byteOffset = (uint)Marshal.ReadInt32(offsets, checked((int)(i * 4)));
			values[checked((int)i)] = ReadUtf16FromBlob(blob, byteOffset) ?? string.Empty;
		}
		return values;
	}

	private static BridgeGroupedEnumerationResult ReadGroupedEnumerationResult(EBridgeGroupedFilesResultHeader header)
	{
		BridgeGroupedEnumerationResult result = new BridgeGroupedEnumerationResult
		{
			TotalFileCount = (int)header.total_file_count,
			EnumerationMs = header.enumeration_ms
		};
		int groupHeaderSize = Marshal.SizeOf<EBridgeGroupedResultGroupHeader>();
		for (ulong i = 0; i < header.group_count; i += 1)
		{
			EBridgeGroupedResultGroupHeader groupHeader = Marshal.PtrToStructure<EBridgeGroupedResultGroupHeader>(
				IntPtr.Add(header.groups, checked((int)(i * (ulong)groupHeaderSize))));
			result.Groups[groupHeader.group_id] = new BridgeGroupedEnumerationGroupResult
			{
				GroupId = groupHeader.group_id,
				HitCount = groupHeader.hit_count,
				QueryMs = groupHeader.query_ms,
				Paths = new HashSet<string>(ReadStringList(groupHeader.path_count, groupHeader.path_offsets, header.path_blob), StringComparer.OrdinalIgnoreCase)
			};
		}
		return result;
	}

	private static List<string> ReadStringList(ulong count, IntPtr offsets, IntPtr blob)
	{
		List<string> values = new List<string>();
		for (ulong i = 0; i < count; i += 1)
		{
			uint byteOffset = (uint)Marshal.ReadInt32(offsets, checked((int)(i * 4)));
			string value = ReadUtf16FromBlob(blob, byteOffset);
			if (!string.IsNullOrWhiteSpace(value))
			{
				values.Add(value);
			}
		}
		return values;
	}

	private static uint[][] ReadHashGroupArray(ulong dirCount, IntPtr hashOffsets, IntPtr hashLengths, IntPtr hashesBlob)
	{
		uint[][] hashesByDirectoryIndex = new uint[checked((int)dirCount)][];
		for (ulong i = 0; i < dirCount; i += 1)
		{
			uint hashOffset = (uint)Marshal.ReadInt32(hashOffsets, checked((int)(i * 4)));
			uint hashLength = (uint)Marshal.ReadInt32(hashLengths, checked((int)(i * 4)));
			if (hashLength == 0)
			{
				hashesByDirectoryIndex[checked((int)i)] = Array.Empty<uint>();
				continue;
			}
			int[] temp = new int[checked((int)hashLength)];
			Marshal.Copy(IntPtr.Add(hashesBlob, checked((int)hashOffset)), temp, 0, temp.Length);
			uint[] hashes = new uint[temp.Length];
			Buffer.BlockCopy(temp, 0, hashes, 0, temp.Length * 4);
			hashesByDirectoryIndex[checked((int)i)] = hashes;
		}
		return hashesByDirectoryIndex;
	}

	private static HashSet<string> MaterializeStringSet(IEnumerable<string> values)
	{
		HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string value in values ?? Array.Empty<string>())
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				set.Add(value);
			}
		}
		return set;
	}

	private static Dictionary<string, uint[]> MaterializeHashMap(string[] chartDirectories, uint[][] hashesByDirectoryIndex)
	{
		Dictionary<string, uint[]> map = new Dictionary<string, uint[]>(chartDirectories?.Length ?? 0, StringComparer.OrdinalIgnoreCase);
		if (chartDirectories == null || hashesByDirectoryIndex == null)
		{
			return map;
		}
		int count = Math.Min(chartDirectories.Length, hashesByDirectoryIndex.Length);
		for (int i = 0; i < count; i++)
		{
			string chartDirectory = chartDirectories[i];
			if (string.IsNullOrWhiteSpace(chartDirectory))
			{
				continue;
			}
			map[chartDirectory] = hashesByDirectoryIndex[i] ?? Array.Empty<uint>();
		}
		return map;
	}

	private static BmsScanExecutionResult CreateExecutionResult(
		EBridgeResultHeader header,
		long nativeBridgeMs,
		long managedDecodeMs,
		FixedScanDecodedResult decodedResult)
	{
		HashSet<string> chartFilePaths = MaterializeStringSet(decodedResult?.ChartPaths);
		HashSet<string> chartDirectories = MaterializeStringSet(decodedResult?.ChartDirectories);
		Dictionary<string, uint[]> allBase = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.AllBaseHashes);
		Dictionary<string, uint[]> audioBase = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.AudioBaseHashes);
		Dictionary<string, uint[]> imageBase = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.ImageBaseHashes);
		Dictionary<string, uint[]> movieBase = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.MovieBaseHashes);
		Dictionary<string, uint[]> audioRelative = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.AudioRelativeHashes);
		Dictionary<string, uint[]> imageRelative = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.ImageRelativeHashes);
		Dictionary<string, uint[]> movieRelative = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.MovieRelativeHashes);
		Dictionary<string, uint[]> selfOwnedAllBase = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.SelfOwnedAllBaseHashes);
		Dictionary<string, uint[]> selfOwnedAudioBase = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.SelfOwnedAudioBaseHashes);
		Dictionary<string, uint[]> selfOwnedImageBase = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.SelfOwnedImageBaseHashes);
		Dictionary<string, uint[]> selfOwnedMovieBase = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.SelfOwnedMovieBaseHashes);
		Dictionary<string, uint[]> selfOwnedAudioRelative = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.SelfOwnedAudioRelativeHashes);
		Dictionary<string, uint[]> selfOwnedImageRelative = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.SelfOwnedImageRelativeHashes);
		Dictionary<string, uint[]> selfOwnedMovieRelative = MaterializeHashMap(decodedResult?.ChartDirectories, decodedResult?.SelfOwnedMovieRelativeHashes);
		ulong hashDirCount = (ulong)chartDirectories.Count;
		ulong hashEntryCount = header.all_base_hash_count;
		return new BmsScanExecutionResult
		{
			Success = true,
			NativeBridgeUsed = true,
			NativeBridgeMs = nativeBridgeMs,
			NativeBridgeReason = FixedScanNativeBridgeReason,
			ManagedDecodeMs = managedDecodeMs,
			BuildResultMs = 0L,
			HashBuildMs = 0L,
			HashDirCount = hashDirCount,
			HashEntryCount = hashEntryCount,
			ChartQueryHitCount = header.chart_query_hits,
			AudioQueryHitCount = header.audio_query_hits,
			ImageQueryHitCount = header.image_query_hits,
			MovieQueryHitCount = header.movie_query_hits,
			ChartQueryMs = header.chart_query_ms,
			AudioQueryMs = header.audio_query_ms,
			ImageQueryMs = header.image_query_ms,
			MovieQueryMs = header.movie_query_ms,
			AssignMs = header.assign_ms,
			DedupeMs = header.dedupe_ms,
			PackMs = header.pack_ms,
			ChartDirectoryCount = header.chart_directory_count,
			AudioAssignedCount = header.audio_assigned_count,
			ImageAssignedCount = header.image_assigned_count,
			MovieAssignedCount = header.movie_assigned_count,
			AllBaseHashCount = header.all_base_hash_count,
			AudioBaseHashCount = header.audio_base_hash_count,
			ImageBaseHashCount = header.image_base_hash_count,
			MovieBaseHashCount = header.movie_base_hash_count,
			AudioRelativeHashCount = header.audio_relative_hash_count,
			ImageRelativeHashCount = header.image_relative_hash_count,
			MovieRelativeHashCount = header.movie_relative_hash_count,
			AudioResourceDirCount = header.audio_resource_dir_count,
			ImageResourceDirCount = header.image_resource_dir_count,
			MovieResourceDirCount = header.movie_resource_dir_count,
			OwnerCacheHitCount = header.owner_cache_hit_count,
			OwnerCacheMissCount = header.owner_cache_miss_count,
			RelativePrefixCacheHitCount = header.relative_prefix_cache_hit_count,
			RelativePrefixCacheMissCount = header.relative_prefix_cache_miss_count,
			AudioGroupMs = header.audio_group_ms,
			AudioAssignMs = header.audio_assign_ms,
			AudioMergeMs = header.audio_merge_ms,
			ImageGroupMs = header.image_group_ms,
			ImageAssignMs = header.image_assign_ms,
			ImageMergeMs = header.image_merge_ms,
			MovieGroupMs = header.movie_group_ms,
			MovieAssignMs = header.movie_assign_ms,
			MovieMergeMs = header.movie_merge_ms,
			Result = new BmsScanResult
			{
				ChartFilePaths = chartFilePaths,
				ChartDirectories = chartDirectories,
				AllResourceBaseNameHashesByChartDirectory = allBase,
				AudioBaseNameHashesByChartDirectory = audioBase,
				ImageBaseNameHashesByChartDirectory = imageBase,
				MovieBaseNameHashesByChartDirectory = movieBase,
				AudioRelativePathHashesByChartDirectory = audioRelative,
				ImageRelativePathHashesByChartDirectory = imageRelative,
				MovieRelativePathHashesByChartDirectory = movieRelative,
				SelfOwnedAllResourceBaseNameHashesByChartDirectory = selfOwnedAllBase,
				SelfOwnedAudioBaseNameHashesByChartDirectory = selfOwnedAudioBase,
				SelfOwnedImageBaseNameHashesByChartDirectory = selfOwnedImageBase,
				SelfOwnedMovieBaseNameHashesByChartDirectory = selfOwnedMovieBase,
				SelfOwnedAudioRelativePathHashesByChartDirectory = selfOwnedAudioRelative,
				SelfOwnedImageRelativePathHashesByChartDirectory = selfOwnedImageRelative,
				SelfOwnedMovieRelativePathHashesByChartDirectory = selfOwnedMovieRelative
			}
		};
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

	[DllImport(BridgeDllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_ScanChartAndResources")]
	private static extern int EBridge_ScanChartAndResources(string chartQuery, string audioQuery, string imageQuery, string movieQuery, out IntPtr outResult);

	[DllImport(BridgeDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_FreeResult")]
	private static extern void EBridge_FreeResult(IntPtr result);

	[DllImport(BridgeDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_EnumerateGroupedFiles")]
	private static extern int EBridge_EnumerateGroupedFiles(IntPtr queries, uint queryCount, out IntPtr outResult);

	[DllImport(BridgeDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_FreeGroupedFilesResult")]
	private static extern void EBridge_FreeGroupedFilesResult(IntPtr result);

	internal readonly struct BridgeGroupedQuery
	{
		internal BridgeGroupedQuery(uint groupId, string queryText)
		{
			GroupId = groupId;
			QueryText = queryText ?? string.Empty;
		}

		internal uint GroupId { get; }

		internal string QueryText { get; }
	}

	internal sealed class BridgeGroupedEnumerationGroupResult
	{
		internal uint GroupId { get; set; }

		internal HashSet<string> Paths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		internal ulong HitCount { get; set; }

		internal long QueryMs { get; set; }
	}

	internal sealed class BridgeGroupedEnumerationResult
	{
		internal Dictionary<uint, BridgeGroupedEnumerationGroupResult> Groups { get; } = new Dictionary<uint, BridgeGroupedEnumerationGroupResult>();

		internal int TotalFileCount { get; set; }

		internal long EnumerationMs { get; set; }
	}

	private sealed class FixedScanDecodedResult
	{
		internal string[] ChartPaths { get; set; } = Array.Empty<string>();

		internal string[] ChartDirectories { get; set; } = Array.Empty<string>();

		internal uint[][] AllBaseHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] AudioBaseHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] ImageBaseHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] MovieBaseHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] AudioRelativeHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] ImageRelativeHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] MovieRelativeHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] SelfOwnedAllBaseHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] SelfOwnedAudioBaseHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] SelfOwnedImageBaseHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] SelfOwnedMovieBaseHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] SelfOwnedAudioRelativeHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] SelfOwnedImageRelativeHashes { get; set; } = Array.Empty<uint[]>();

		internal uint[][] SelfOwnedMovieRelativeHashes { get; set; } = Array.Empty<uint[]>();
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct EBridgeGroupedQueryNative
	{
		public uint group_id;
		public IntPtr query_text;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct EBridgeGroupedResultGroupHeader
	{
		public uint group_id;
		public ulong hit_count;
		public long query_ms;
		public ulong path_count;
		public IntPtr path_offsets;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct EBridgeGroupedFilesResultHeader
	{
		public int status;
		public int error_code;
		public ulong group_count;
		public IntPtr groups;
		public IntPtr path_blob;
		public ulong total_file_count;
		public long enumeration_ms;
		public ulong raw_buffer_size;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct EBridgeResultHeader
	{
		public int status;
		public int error_code;
		public ulong chart_count;
		public IntPtr chart_offsets;
		public IntPtr chart_blob;
		public ulong dir_count;
		public IntPtr dir_offsets;
		public IntPtr dir_blob;
		public IntPtr all_hash_offsets;
		public IntPtr all_hash_lengths;
		public IntPtr all_hashes_blob;
		public IntPtr audio_base_hash_offsets;
		public IntPtr audio_base_hash_lengths;
		public IntPtr audio_base_hashes_blob;
		public IntPtr image_base_hash_offsets;
		public IntPtr image_base_hash_lengths;
		public IntPtr image_base_hashes_blob;
		public IntPtr movie_base_hash_offsets;
		public IntPtr movie_base_hash_lengths;
		public IntPtr movie_base_hashes_blob;
		public IntPtr audio_relative_hash_offsets;
		public IntPtr audio_relative_hash_lengths;
		public IntPtr audio_relative_hashes_blob;
		public IntPtr image_relative_hash_offsets;
		public IntPtr image_relative_hash_lengths;
		public IntPtr image_relative_hashes_blob;
		public IntPtr movie_relative_hash_offsets;
		public IntPtr movie_relative_hash_lengths;
		public IntPtr movie_relative_hashes_blob;
		public ulong chart_query_hits;
		public ulong audio_query_hits;
		public ulong image_query_hits;
		public ulong movie_query_hits;
		public long chart_query_ms;
		public long audio_query_ms;
		public long image_query_ms;
		public long movie_query_ms;
		public long assign_ms;
		public long dedupe_ms;
		public long pack_ms;
		public ulong chart_directory_count;
		public ulong audio_assigned_count;
		public ulong image_assigned_count;
		public ulong movie_assigned_count;
		public ulong all_base_hash_count;
		public ulong audio_base_hash_count;
		public ulong image_base_hash_count;
		public ulong movie_base_hash_count;
		public ulong audio_relative_hash_count;
		public ulong image_relative_hash_count;
		public ulong movie_relative_hash_count;
		public ulong audio_resource_dir_count;
		public ulong image_resource_dir_count;
		public ulong movie_resource_dir_count;
		public ulong owner_cache_hit_count;
		public ulong owner_cache_miss_count;
		public ulong relative_prefix_cache_hit_count;
		public ulong relative_prefix_cache_miss_count;
		public long audio_group_ms;
		public long audio_assign_ms;
		public long audio_merge_ms;
		public long image_group_ms;
		public long image_assign_ms;
		public long image_merge_ms;
		public long movie_group_ms;
		public long movie_assign_ms;
		public long movie_merge_ms;
		public ulong raw_buffer_size;
		public IntPtr self_all_hash_offsets;
		public IntPtr self_all_hash_lengths;
		public IntPtr self_all_hashes_blob;
		public IntPtr self_audio_base_hash_offsets;
		public IntPtr self_audio_base_hash_lengths;
		public IntPtr self_audio_base_hashes_blob;
		public IntPtr self_image_base_hash_offsets;
		public IntPtr self_image_base_hash_lengths;
		public IntPtr self_image_base_hashes_blob;
		public IntPtr self_movie_base_hash_offsets;
		public IntPtr self_movie_base_hash_lengths;
		public IntPtr self_movie_base_hashes_blob;
		public IntPtr self_audio_relative_hash_offsets;
		public IntPtr self_audio_relative_hash_lengths;
		public IntPtr self_audio_relative_hashes_blob;
		public IntPtr self_image_relative_hash_offsets;
		public IntPtr self_image_relative_hash_lengths;
		public IntPtr self_image_relative_hashes_blob;
		public IntPtr self_movie_relative_hash_offsets;
		public IntPtr self_movie_relative_hash_lengths;
		public IntPtr self_movie_relative_hashes_blob;
	}
}
