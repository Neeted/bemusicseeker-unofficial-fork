using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models.Utils;

internal static class EverythingNative
{
    private const string BridgeDllName = "EverythingBridge_x64.dll";

    internal const string GroupedEnumerationBackendName = "everything_bridge";

    internal const string FixedScanNativeBridgeReason = "everything_bridge_fixed_scan";

    internal const string SourceRootScanBackendName = "everything_bridge_source_surface";

    private const uint FixedScanContractVersion = 2026060601u;

    private static IntPtr loadedBridgeModule = IntPtr.Zero;

    private static bool bridgeExportsProbed;

    private static bool bridgeFixedScanAvailable;

    private static bool bridgeFreeResultAvailable;

    private static bool bridgeSourceRootScanAvailable;

    private static bool bridgeFreeSourceRootResultAvailable;

    private static bool bridgeGroupedEnumerationQueryAvailable;

    private static bool bridgeFreeGroupedEnumerationResultAvailable;

    internal static string GetExpectedBridgeDllPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", BridgeDllName);
    }

    internal static bool EnsureBridgeAvailable(out string reason)
    {
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
        bridgeSourceRootScanAvailable = GetProcAddress(loadedBridgeModule, "EBridge_ScanSourceRoots") != IntPtr.Zero;
        bridgeFreeSourceRootResultAvailable = GetProcAddress(loadedBridgeModule, "EBridge_FreeSourceRootsResult") != IntPtr.Zero;
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

    private static bool EnsureSourceRootScanAvailable(out string reason)
    {
        if (!EnsureBridgeLoaded(out reason))
        {
            return false;
        }
        if (!bridgeSourceRootScanAvailable || !bridgeFreeSourceRootResultAvailable)
        {
            reason = "bridge_source_root_scan_export_missing";
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
        return BuildFilesQuery(roots, extensions, []);
    }

    internal static string BuildFilesQuery(string[] roots, string[] extensions, string[] excludedDirectories)
    {
        string ext = string.Join(";", extensions ?? []);
        string paths = "<" + string.Join("|", Array.ConvertAll(roots ?? [], root => "path:" + QuotePath(PathWithTrailingSeparator(root)))) + ">";
        string excludes = BuildExcludedDirectoryQuery(excludedDirectories);
        return "file: " + paths + excludes + " <ext:" + ext + ">";
    }

    internal static string BuildAllFilesQuery(string[] roots)
    {
        return BuildAllFilesQuery(roots, []);
    }

    internal static string BuildAllFilesQuery(string[] roots, string[] excludedDirectories)
    {
        string paths = "<" + string.Join("|", Array.ConvertAll(roots ?? [], root => "path:" + QuotePath(PathWithTrailingSeparator(root)))) + ">";
        return "file: " + paths + BuildExcludedDirectoryQuery(excludedDirectories);
    }

    internal static string BuildDirectoriesQuery(string[] roots)
    {
        return BuildDirectoriesQuery(roots, []);
    }

    internal static string BuildDirectoriesQuery(string[] roots, string[] excludedDirectories)
    {
        string paths = "<" + string.Join("|", Array.ConvertAll(roots ?? [], root => "path:" + QuotePath(PathWithoutTrailingSeparator(root)))) + ">";
        return "folder: " + paths + BuildExcludedDirectoryQuery(excludedDirectories, trailingSeparator: false);
    }

    private static string BuildExcludedDirectoryQuery(string[] excludedDirectories, bool trailingSeparator = true)
    {
        string[] exclusions = [.. (excludedDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => trailingSeparator ? PathWithTrailingSeparator(path) : PathWithoutTrailingSeparator(path))
            .Select(path => "path:" + QuotePath(path))];
        return exclusions.Length == 0
            ? string.Empty
            : " !" + "<" + string.Join("|", exclusions) + ">";
    }

    internal static bool TryScanSourceRoots(IReadOnlyList<string> rootDirectories, out BridgeSourceRootScanResult result, out string reason)
    {
        result = null;
        reason = null;
        List<string> normalizedRoots = [.. (rootDirectories ?? [])
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeSourceRootDirectory)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (normalizedRoots.Count == 0)
        {
            result = new BridgeSourceRootScanResult();
            return true;
        }
        if (!EnsureSourceRootScanAvailable(out reason))
        {
            return false;
        }

        IntPtr resultPtr = IntPtr.Zero;
        IntPtr nativeRoots = IntPtr.Zero;
        List<IntPtr> allocatedStrings = [];
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            nativeRoots = Marshal.AllocHGlobal(checked(Marshal.SizeOf<EBridgeSourceRootRequestNative>() * normalizedRoots.Count));
            for (int i = 0; i < normalizedRoots.Count; i++)
            {
                IntPtr rootPath = Marshal.StringToHGlobalUni(normalizedRoots[i]);
                allocatedStrings.Add(rootPath);
                var nativeRoot = new EBridgeSourceRootRequestNative
                {
                    root_id = (uint)i,
                    root_path = rootPath
                };
                Marshal.StructureToPtr(nativeRoot, IntPtr.Add(nativeRoots, i * Marshal.SizeOf<EBridgeSourceRootRequestNative>()), false);
            }

            string[] roots = [.. normalizedRoots];
            string chartQuery = BuildFilesQuery(roots, ChartDirectoryScanBuilder.ChartExtensions);
            string audioQuery = BuildFilesQuery(roots, ChartDirectoryScanBuilder.AudioExtensions);
            string imageQuery = BuildFilesQuery(roots, ChartDirectoryScanBuilder.ImageExtensions);
            string movieQuery = BuildFilesQuery(roots, ChartDirectoryScanBuilder.MovieExtensions);

            var nativeBridgeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            int status = EBridge_ScanSourceRoots(nativeRoots, (uint)normalizedRoots.Count, chartQuery, audioQuery, imageQuery, movieQuery, out resultPtr);
            nativeBridgeStopwatch.Stop();
            long nativeBridgeMs = nativeBridgeStopwatch.ElapsedMilliseconds;
            if (status != 0)
            {
                reason = "bridge_source_root_scan_failed:" + status;
                return false;
            }
            if (resultPtr == IntPtr.Zero)
            {
                reason = "bridge_source_root_empty_result";
                return false;
            }

            EBridgeSourceRootsResultHeader header = Marshal.PtrToStructure<EBridgeSourceRootsResultHeader>(resultPtr);
            if (header.status != 0)
            {
                reason = "bridge_source_root_status_failed:" + header.error_code;
                return false;
            }

            var decodeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            SourceRootDecodedResult decodedResult = DecodeSourceRootsResult(header);
            decodeStopwatch.Stop();

            var materializeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            result = CreateSourceRootScanResult(header, nativeBridgeMs, decodeStopwatch.ElapsedMilliseconds, decodedResult);
            materializeStopwatch.Stop();
            result.ManagedMaterializeMs = materializeStopwatch.ElapsedMilliseconds;
            result.RawBufferBytes = header.raw_buffer_size;
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            reason = "ok";
            return true;
        }
        catch (Exception ex)
        {
            reason = "bridge_source_root_exception:" + ex.Message;
            return false;
        }
        finally
        {
            if (resultPtr != IntPtr.Zero)
            {
                try
                {
                    EBridge_FreeSourceRootsResult(resultPtr);
                }
                catch
                {
                }
            }
            foreach (IntPtr rootPath in allocatedStrings)
            {
                if (rootPath != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(rootPath);
                }
            }
            if (nativeRoots != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(nativeRoots);
            }
        }
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
        List<IntPtr> allocatedStrings = [];
        try
        {
            nativeQueries = Marshal.AllocHGlobal(checked(Marshal.SizeOf<EBridgeGroupedQueryNative>() * queries.Count));
            for (int i = 0; i < queries.Count; i++)
            {
                IntPtr queryText = Marshal.StringToHGlobalUni(queries[i].QueryText ?? string.Empty);
                allocatedStrings.Add(queryText);
                var nativeQuery = new EBridgeGroupedQueryNative
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

    internal static ChartScanExecutionResult ExecuteScan(string chartQuery, string audioQuery, string imageQuery, string movieQuery, string textQuery = null)
    {
        if (!TryExecuteBridgeScan(chartQuery, audioQuery, imageQuery, movieQuery, textQuery, out ChartScanExecutionResult result, out string reason, out long elapsedMs))
        {
            return Failed(reason, elapsedMs);
        }
        if (result == null || !result.Success || result.Result == null || result.Result.ChartFilePaths.Count == 0)
        {
            return Failed("bridge_empty_result", elapsedMs);
        }
        return result;
    }

    private static bool TryExecuteBridgeScan(string chartQuery, string audioQuery, string imageQuery, string movieQuery, string textQuery, out ChartScanExecutionResult result, out string reason, out long elapsedMs)
    {
        result = null;
        reason = null;
        IntPtr resultPtr = IntPtr.Zero;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!EnsureFixedScanAvailable(out reason))
            {
                return false;
            }

            var nativeBridgeStopwatch = Stopwatch.StartNew();
            int status = EBridge_ScanChartAndResources(chartQuery, audioQuery, imageQuery, movieQuery, textQuery ?? string.Empty, out resultPtr);
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
            if (header.contract_version != FixedScanContractVersion)
            {
                reason = "bridge_contract_mismatch:expected=" + FixedScanContractVersion + ":actual=" + header.contract_version;
                return false;
            }
            int expectedHeaderSize = Marshal.SizeOf<EBridgeResultHeader>();
            if (header.header_size != expectedHeaderSize)
            {
                reason = "bridge_header_size_mismatch:expected=" + expectedHeaderSize + ":actual=" + header.header_size;
                return false;
            }
            if (header.status != 0)
            {
                reason = "bridge_status_failed:" + header.error_code;
                return false;
            }

            var decodeStopwatch = Stopwatch.StartNew();
            FixedScanDecodedResult decodedResult = DecodeFixedScanResult(header);
            decodeStopwatch.Stop();

            var materializeStopwatch = Stopwatch.StartNew();
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
        Dictionary<string, RootFileEnumerationEntry> chartFileEntries = ReadGroupedFileEntries(header.chart_count, header.chart_offsets, header.chart_last_write_filetimes, header.chart_blob);
        string[] chartPaths = [.. chartFileEntries.Keys];
        string[] chartDirectories = ReadStringArray(header.dir_count, header.dir_offsets, header.dir_blob);
        Dictionary<string, RootFileEnumerationEntry> textFileEntries = ReadGroupedFileEntries(header.text_count, header.text_offsets, header.text_last_write_filetimes, header.text_blob);
        uint[][] audioRelativeHashes = null;
        uint[][] imageRelativeHashes = null;
        uint[][] movieRelativeHashes = null;
        uint[][] selfOwnedAudioRelativeHashes = null;
        uint[][] selfOwnedImageRelativeHashes = null;
        uint[][] selfOwnedMovieRelativeHashes = null;
        Dictionary<uint, string[]> audioRelativeReverseDirectories = null;
        Dictionary<uint, string[]> imageRelativeReverseDirectories = null;
        Dictionary<uint, string[]> movieRelativeReverseDirectories = null;
        try
        {
            Parallel.Invoke(
                () =>
                {
                    audioRelativeHashes = ReadHashGroupArray(header.dir_count, header.audio_resource_key_hash_offsets, header.audio_resource_key_hash_lengths, header.audio_resource_key_hashes_blob);
                    selfOwnedAudioRelativeHashes = ReadHashGroupArray(header.dir_count, header.self_audio_resource_key_hash_offsets, header.self_audio_resource_key_hash_lengths, header.self_audio_resource_key_hashes_blob);
                    audioRelativeReverseDirectories = ReadReverseHashMap(header.audio_relative_reverse_key_count, header.audio_relative_reverse_keys, header.audio_relative_reverse_offsets, header.audio_relative_reverse_lengths, header.audio_relative_reverse_indices_blob, header.reverse_index_bytes, chartDirectories);
                },
                () =>
                {
                    imageRelativeHashes = ReadHashGroupArray(header.dir_count, header.image_resource_key_hash_offsets, header.image_resource_key_hash_lengths, header.image_resource_key_hashes_blob);
                    selfOwnedImageRelativeHashes = ReadHashGroupArray(header.dir_count, header.self_image_resource_key_hash_offsets, header.self_image_resource_key_hash_lengths, header.self_image_resource_key_hashes_blob);
                    imageRelativeReverseDirectories = ReadReverseHashMap(header.image_relative_reverse_key_count, header.image_relative_reverse_keys, header.image_relative_reverse_offsets, header.image_relative_reverse_lengths, header.image_relative_reverse_indices_blob, header.reverse_index_bytes, chartDirectories);
                },
                () =>
                {
                    movieRelativeHashes = ReadHashGroupArray(header.dir_count, header.movie_resource_key_hash_offsets, header.movie_resource_key_hash_lengths, header.movie_resource_key_hashes_blob);
                    selfOwnedMovieRelativeHashes = ReadHashGroupArray(header.dir_count, header.self_movie_resource_key_hash_offsets, header.self_movie_resource_key_hash_lengths, header.self_movie_resource_key_hashes_blob);
                    movieRelativeReverseDirectories = ReadReverseHashMap(header.movie_relative_reverse_key_count, header.movie_relative_reverse_keys, header.movie_relative_reverse_offsets, header.movie_relative_reverse_lengths, header.movie_relative_reverse_indices_blob, header.reverse_index_bytes, chartDirectories);
                });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
        return new FixedScanDecodedResult
        {
            ChartPaths = chartPaths,
            ChartFileEntries = chartFileEntries,
            ChartDirectories = chartDirectories,
            TextFileEntries = textFileEntries,
            AudioRelativeHashes = audioRelativeHashes,
            ImageRelativeHashes = imageRelativeHashes,
            MovieRelativeHashes = movieRelativeHashes,
            SelfOwnedAudioRelativeHashes = selfOwnedAudioRelativeHashes,
            SelfOwnedImageRelativeHashes = selfOwnedImageRelativeHashes,
            SelfOwnedMovieRelativeHashes = selfOwnedMovieRelativeHashes,
            AudioRelativeReverseDirectories = audioRelativeReverseDirectories,
            ImageRelativeReverseDirectories = imageRelativeReverseDirectories,
            MovieRelativeReverseDirectories = movieRelativeReverseDirectories
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
        var result = new BridgeGroupedEnumerationResult
        {
            ContractVersion = header.contract_version,
            HeaderSize = header.header_size,
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
                Paths = new HashSet<string>(ReadStringList(groupHeader.path_count, groupHeader.path_offsets, header.path_blob), StringComparer.OrdinalIgnoreCase),
                Entries = ReadGroupedFileEntries(groupHeader.path_count, groupHeader.path_offsets, groupHeader.last_write_filetimes, header.path_blob)
            };
        }
        return result;
    }

    private static Dictionary<string, RootFileEnumerationEntry> ReadGroupedFileEntries(ulong count, IntPtr offsets, IntPtr lastWriteFileTimes, IntPtr blob)
    {
        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        for (ulong i = 0; i < count; i += 1)
        {
            uint byteOffset = (uint)Marshal.ReadInt32(offsets, checked((int)(i * 4)));
            string value = ReadUtf16FromBlob(blob, byteOffset);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            ulong fileTime = lastWriteFileTimes == IntPtr.Zero
                ? 0UL
                : unchecked((ulong)Marshal.ReadInt64(lastWriteFileTimes, checked((int)(i * 8))));
            entries[value] = new RootFileEnumerationEntry(value, ConvertFileTimeUtc(fileTime));
        }
        return entries;
    }

    private static DateTime? ConvertFileTimeUtc(ulong fileTime)
    {
        if (fileTime == 0UL || fileTime == ulong.MaxValue || fileTime > (ulong)DateTime.MaxValue.ToFileTimeUtc())
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(unchecked((long)fileTime));
        }
        catch
        {
            return null;
        }
    }

    private static SourceRootDecodedResult DecodeSourceRootsResult(EBridgeSourceRootsResultHeader header)
    {
        string[] rootPaths = ReadStringArray(header.root_count, header.root_offsets, header.root_blob);
        var entries = new SourceRootDecodedEntry[checked((int)header.root_count)];
        int entryHeaderSize = Marshal.SizeOf<EBridgeSourceRootEntryHeader>();
        for (ulong i = 0; i < header.root_count; i += 1)
        {
            EBridgeSourceRootEntryHeader entryHeader = Marshal.PtrToStructure<EBridgeSourceRootEntryHeader>(
                IntPtr.Add(header.roots, checked((int)(i * (ulong)entryHeaderSize))));
            entries[checked((int)i)] = new SourceRootDecodedEntry
            {
                RootId = entryHeader.root_id,
                ChartPaths = [.. ReadStringList(entryHeader.chart_count, entryHeader.chart_offsets, header.chart_blob)],
                AudioRelativeHashes = ReadHashArray(entryHeader.audio_resource_key_hash_offset, entryHeader.audio_resource_key_hash_length, header.audio_resource_key_hashes_blob),
                ImageRelativeHashes = ReadHashArray(entryHeader.image_resource_key_hash_offset, entryHeader.image_resource_key_hash_length, header.image_resource_key_hashes_blob),
                MovieRelativeHashes = ReadHashArray(entryHeader.movie_resource_key_hash_offset, entryHeader.movie_resource_key_hash_length, header.movie_resource_key_hashes_blob),
                ChartFileCount = checked((int)entryHeader.chart_file_count),
                ResourceFileCount = checked((int)entryHeader.categorized_resource_file_count),
                TrackedFileCount = checked((int)entryHeader.tracked_file_count)
            };
        }
        return new SourceRootDecodedResult
        {
            RootPaths = rootPaths,
            Entries = entries
        };
    }

    private static List<string> ReadStringList(ulong count, IntPtr offsets, IntPtr blob)
    {
        List<string> values = [];
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

    private static unsafe uint[] ReadHashArray(uint byteOffset, uint length, IntPtr blob)
    {
        if (blob == IntPtr.Zero || length == 0)
        {
            return [];
        }
        int count = checked((int)length);
        uint[] hashes = new uint[count];
        fixed (uint* destination = hashes)
        {
            Buffer.MemoryCopy(
                (byte*)blob.ToPointer() + checked((int)byteOffset),
                destination,
                count * sizeof(uint),
                count * sizeof(uint));
        }
        return hashes;
    }

    private static unsafe uint[][] ReadHashGroupArray(ulong dirCount, IntPtr hashOffsets, IntPtr hashLengths, IntPtr hashesBlob)
    {
        uint[][] hashesByDirectoryIndex = new uint[checked((int)dirCount)][];
        uint* offsets = (uint*)hashOffsets.ToPointer();
        uint* lengths = (uint*)hashLengths.ToPointer();
        byte* blob = (byte*)hashesBlob.ToPointer();
        for (int i = 0; i < hashesByDirectoryIndex.Length; i += 1)
        {
            uint hashOffset = offsets[i];
            uint hashLength = lengths[i];
            if (hashLength == 0)
            {
                hashesByDirectoryIndex[i] = [];
                continue;
            }
            int count = checked((int)hashLength);
            uint[] hashes = new uint[count];
            fixed (uint* destination = hashes)
            {
                Buffer.MemoryCopy(
                    blob + checked((int)hashOffset),
                    destination,
                    count * sizeof(uint),
                    count * sizeof(uint));
            }
            hashesByDirectoryIndex[i] = hashes;
        }
        return hashesByDirectoryIndex;
    }

    private static unsafe Dictionary<uint, string[]> ReadReverseHashMap(ulong keyCount, IntPtr keys, IntPtr offsets, IntPtr lengths, IntPtr indicesBlob, uint indexBytes, string[] chartDirectories)
    {
        var map = new Dictionary<uint, string[]>(checked((int)keyCount));
        if (keyCount == 0 || keys == IntPtr.Zero || offsets == IntPtr.Zero || lengths == IntPtr.Zero || indicesBlob == IntPtr.Zero)
        {
            return map;
        }
        if (indexBytes != sizeof(ushort) && indexBytes != sizeof(uint))
        {
            throw new InvalidOperationException("unsupported_reverse_index_bytes:" + indexBytes);
        }
        uint* keyValues = (uint*)keys.ToPointer();
        uint* offsetValues = (uint*)offsets.ToPointer();
        uint* lengthValues = (uint*)lengths.ToPointer();
        ushort* indexValues16 = indexBytes == sizeof(ushort) ? (ushort*)indicesBlob.ToPointer() : null;
        uint* indexValues32 = indexBytes == sizeof(uint) ? (uint*)indicesBlob.ToPointer() : null;
        int count = checked((int)keyCount);
        for (int i = 0; i < count; i += 1)
        {
            uint key = keyValues[i];
            uint byteOffset = offsetValues[i];
            uint length = lengthValues[i];
            if (length == 0)
            {
                map[key] = [];
                continue;
            }
            if (byteOffset % indexBytes != 0)
            {
                throw new InvalidOperationException("unaligned_reverse_index_offset:" + byteOffset + ":bytes=" + indexBytes);
            }
            int indexOffset = checked((int)(byteOffset / indexBytes));
            if (length == 1)
            {
                int directoryIndex = ReadReverseDirectoryIndex(indexValues16, indexValues32, indexOffset);
                if (directoryIndex >= 0 && directoryIndex < (chartDirectories?.Length ?? 0))
                {
                    string directory = chartDirectories[directoryIndex];
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        map[key] = [directory];
                        continue;
                    }
                }
                map[key] = [];
                continue;
            }

            string[] directories = new string[checked((int)length)];
            int outputCount = 0;
            for (int j = 0; j < directories.Length; j++)
            {
                int directoryIndex = ReadReverseDirectoryIndex(indexValues16, indexValues32, indexOffset + j);
                if (directoryIndex < 0 || directoryIndex >= (chartDirectories?.Length ?? 0))
                {
                    continue;
                }
                string directory = chartDirectories[directoryIndex];
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    directories[outputCount++] = directory;
                }
            }
            if (outputCount == directories.Length)
            {
                map[key] = directories;
            }
            else if (outputCount == 0)
            {
                map[key] = [];
            }
            else
            {
                Array.Resize(ref directories, outputCount);
                map[key] = directories;
            }
        }
        return map;
    }

    private static unsafe int ReadReverseDirectoryIndex(ushort* indexValues16, uint* indexValues32, int offset)
    {
        return indexValues16 != null
            ? indexValues16[offset]
            : checked((int)indexValues32[offset]);
    }

    private static HashSet<string> MaterializeStringSet(IEnumerable<string> values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value);
            }
        }
        return set;
    }

    private static BridgeSourceRootScanResult CreateSourceRootScanResult(
        EBridgeSourceRootsResultHeader header,
        long nativeBridgeMs,
        long managedDecodeMs,
        SourceRootDecodedResult decodedResult)
    {
        var result = new BridgeSourceRootScanResult
        {
            BackendName = SourceRootScanBackendName,
            NativeBridgeMs = nativeBridgeMs,
            ManagedDecodeMs = managedDecodeMs,
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
            PackMs = header.pack_ms
        };
        string[] rootPaths = decodedResult?.RootPaths ?? [];
        SourceRootDecodedEntry[] entries = decodedResult?.Entries ?? [];
        int count = Math.Min(rootPaths.Length, entries.Length);
        for (int i = 0; i < count; i++)
        {
            string rootPath = NormalizeSourceRootDirectory(rootPaths[i]);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                continue;
            }

            SourceRootDecodedEntry decodedEntry = entries[i] ?? new SourceRootDecodedEntry();
            result.Entries[rootPath] = new BridgeSourceRootEntryResult
            {
                RootPath = rootPath,
                ChartPaths = decodedEntry.ChartPaths ?? [],
                ResourceEntry = new DirectoryResourceLookupCache.Entry(
                    decodedEntry.AudioRelativeHashes,
                    decodedEntry.ImageRelativeHashes,
                    decodedEntry.MovieRelativeHashes),
                ChartFileCount = decodedEntry.ChartFileCount,
                ResourceFileCount = decodedEntry.ResourceFileCount,
                TrackedFileCount = decodedEntry.TrackedFileCount
            };
        }
        return result;
    }

    private static string NormalizeSourceRootDirectory(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return string.Empty;
        }
        try
        {
            return Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static ChartScanExecutionResult CreateExecutionResult(
        EBridgeResultHeader header,
        long nativeBridgeMs,
        long managedDecodeMs,
        FixedScanDecodedResult decodedResult)
    {
        HashSet<string> chartFilePaths = MaterializeStringSet(decodedResult?.ChartPaths);
        HashSet<string> chartDirectories = MaterializeStringSet(decodedResult?.ChartDirectories);
        ulong hashDirCount = (ulong)chartDirectories.Count;
        ulong categoryResourceKeyHashEntryCount = header.audio_resource_key_hash_count + header.image_resource_key_hash_count + header.movie_resource_key_hash_count;
        var scanResult = new ChartScanResult
        {
            ChartFilePaths = chartFilePaths,
            ChartFileEntriesByPath = new Dictionary<string, RootFileEnumerationEntry>(
                decodedResult?.ChartFileEntries ?? [],
                StringComparer.OrdinalIgnoreCase),
            ChartDirectories = chartDirectories
        };
        IEnumerable<RootFileEnumerationEntry> textFileEntries = decodedResult?.TextFileEntries?.Values
            ?? Enumerable.Empty<RootFileEnumerationEntry>();
        ChartDirectoryScanBuilder.AddDirectTextFileEntries(scanResult, textFileEntries);
        return new ChartScanExecutionResult
        {
            Success = true,
            NativeBridgeUsed = true,
            NativeBridgeMs = nativeBridgeMs,
            NativeBridgeReason = FixedScanNativeBridgeReason,
            ManagedDecodeMs = managedDecodeMs,
            BuildResultMs = 0L,
            HashBuildMs = 0L,
            HashDirCount = hashDirCount,
            CategoryResourceKeyHashEntryCount = categoryResourceKeyHashEntryCount,
            ChartQueryHitCount = header.chart_query_hits,
            AudioQueryHitCount = header.audio_query_hits,
            ImageQueryHitCount = header.image_query_hits,
            MovieQueryHitCount = header.movie_query_hits,
            TextQueryHitCount = header.text_query_hits,
            ChartQueryMs = header.chart_query_ms,
            AudioQueryMs = header.audio_query_ms,
            ImageQueryMs = header.image_query_ms,
            MovieQueryMs = header.movie_query_ms,
            TextQueryMs = header.text_query_ms,
            AssignMs = header.assign_ms,
            DedupeMs = header.dedupe_ms,
            PackMs = header.pack_ms,
            PackReverseBuildMs = header.pack_reverse_build_ms,
            AudioReverseBuildMs = header.audio_reverse_build_ms,
            ImageReverseBuildMs = header.image_reverse_build_ms,
            MovieReverseBuildMs = header.movie_reverse_build_ms,
            PackLayoutMs = header.pack_layout_ms,
            PackAllocMs = header.pack_alloc_ms,
            PackWriteMs = header.pack_write_ms,
            ReverseIndexBytes = header.reverse_index_bytes,
            ChartSearchMs = header.chart_search_ms,
            ChartReadMs = header.chart_read_ms,
            AudioSearchMs = header.audio_search_ms,
            AudioReadMs = header.audio_read_ms,
            ImageSearchMs = header.image_search_ms,
            ImageReadMs = header.image_read_ms,
            MovieSearchMs = header.movie_search_ms,
            MovieReadMs = header.movie_read_ms,
            TextSearchMs = header.text_search_ms,
            TextReadMs = header.text_read_ms,
            ChartSdkReadMs = header.chart_sdk_read_ms,
            ChartCallbackMs = header.chart_callback_ms,
            AudioSdkReadMs = header.audio_sdk_read_ms,
            AudioCallbackMs = header.audio_callback_ms,
            ImageSdkReadMs = header.image_sdk_read_ms,
            ImageCallbackMs = header.image_callback_ms,
            MovieSdkReadMs = header.movie_sdk_read_ms,
            MovieCallbackMs = header.movie_callback_ms,
            TextSdkReadMs = header.text_sdk_read_ms,
            TextCallbackMs = header.text_callback_ms,
            ChartPathResizeCount = header.chart_path_resize_count,
            ChartNameResizeCount = header.chart_name_resize_count,
            AudioPathResizeCount = header.audio_path_resize_count,
            AudioNameResizeCount = header.audio_name_resize_count,
            ImagePathResizeCount = header.image_path_resize_count,
            ImageNameResizeCount = header.image_name_resize_count,
            MoviePathResizeCount = header.movie_path_resize_count,
            MovieNameResizeCount = header.movie_name_resize_count,
            TextPathResizeCount = header.text_path_resize_count,
            TextNameResizeCount = header.text_name_resize_count,
            ChartDirectoryCount = header.chart_directory_count,
            AudioAssignedCount = header.audio_assigned_count,
            ImageAssignedCount = header.image_assigned_count,
            MovieAssignedCount = header.movie_assigned_count,
            AudioResourceKeyHashCount = header.audio_resource_key_hash_count,
            ImageResourceKeyHashCount = header.image_resource_key_hash_count,
            MovieResourceKeyHashCount = header.movie_resource_key_hash_count,
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
            Result = scanResult,
            ResourceIndex = LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                decodedResult?.ChartDirectories,
                decodedResult?.AudioRelativeHashes,
                decodedResult?.ImageRelativeHashes,
                decodedResult?.MovieRelativeHashes,
                decodedResult?.SelfOwnedAudioRelativeHashes,
                decodedResult?.SelfOwnedImageRelativeHashes,
                decodedResult?.SelfOwnedMovieRelativeHashes,
                decodedResult?.AudioRelativeReverseDirectories,
                decodedResult?.ImageRelativeReverseDirectories,
                decodedResult?.MovieRelativeReverseDirectories)
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

    private static ChartScanExecutionResult Failed(string reason, long bridgeMs = 0L)
    {
        return new ChartScanExecutionResult
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

    private static string PathWithoutTrailingSeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }
        string root = Path.GetPathRoot(path);
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root.TrimEnd(Path.AltDirectorySeparatorChar);
        }
        return trimmed;
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
    private static extern int EBridge_ScanChartAndResources(string chartQuery, string audioQuery, string imageQuery, string movieQuery, string textQuery, out IntPtr outResult);

    [DllImport(BridgeDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_FreeResult")]
    private static extern void EBridge_FreeResult(IntPtr result);

    [DllImport(BridgeDllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_ScanSourceRoots")]
    private static extern int EBridge_ScanSourceRoots(IntPtr roots, uint rootCount, string chartQuery, string audioQuery, string imageQuery, string movieQuery, out IntPtr outResult);

    [DllImport(BridgeDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "EBridge_FreeSourceRootsResult")]
    private static extern void EBridge_FreeSourceRootsResult(IntPtr result);

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

        internal Dictionary<string, RootFileEnumerationEntry> Entries { get; set; } = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

        internal ulong HitCount { get; set; }

        internal long QueryMs { get; set; }
    }

    internal sealed class BridgeGroupedEnumerationResult
    {
        internal Dictionary<uint, BridgeGroupedEnumerationGroupResult> Groups { get; } = [];

        internal uint ContractVersion { get; set; }

        internal uint HeaderSize { get; set; }

        internal int TotalFileCount { get; set; }

        internal long EnumerationMs { get; set; }
    }

    internal sealed class BridgeSourceRootEntryResult
    {
        internal string RootPath { get; set; } = string.Empty;

        internal string[] ChartPaths { get; set; } = [];

        internal DirectoryResourceLookupCache.Entry ResourceEntry { get; set; } = new DirectoryResourceLookupCache.Entry();

        internal int ChartFileCount { get; set; }

        internal int ResourceFileCount { get; set; }

        internal int TrackedFileCount { get; set; }
    }

    internal sealed class BridgeSourceRootScanResult
    {
        internal Dictionary<string, BridgeSourceRootEntryResult> Entries { get; } = new Dictionary<string, BridgeSourceRootEntryResult>(StringComparer.OrdinalIgnoreCase);

        internal string BackendName { get; set; } = SourceRootScanBackendName;

        internal long TotalMs { get; set; }

        internal long NativeBridgeMs { get; set; }

        internal long ManagedDecodeMs { get; set; }

        internal long ManagedMaterializeMs { get; set; }

        internal ulong RawBufferBytes { get; set; }

        internal ulong ChartQueryHitCount { get; set; }

        internal ulong AudioQueryHitCount { get; set; }

        internal ulong ImageQueryHitCount { get; set; }

        internal ulong MovieQueryHitCount { get; set; }

        internal long ChartQueryMs { get; set; }

        internal long AudioQueryMs { get; set; }

        internal long ImageQueryMs { get; set; }

        internal long MovieQueryMs { get; set; }

        internal long AssignMs { get; set; }

        internal long DedupeMs { get; set; }

        internal long PackMs { get; set; }

        internal bool TryGetEntry(string rootPath, out BridgeSourceRootEntryResult entry)
        {
            return Entries.TryGetValue(NormalizeSourceRootDirectory(rootPath), out entry);
        }
    }

    private sealed class FixedScanDecodedResult
    {
        internal string[] ChartPaths { get; set; } = [];

        internal Dictionary<string, RootFileEnumerationEntry> ChartFileEntries { get; set; } = [];

        internal string[] ChartDirectories { get; set; } = [];

        internal Dictionary<string, RootFileEnumerationEntry> TextFileEntries { get; set; } = [];

        internal uint[][] AudioRelativeHashes { get; set; } = [];

        internal uint[][] ImageRelativeHashes { get; set; } = [];

        internal uint[][] MovieRelativeHashes { get; set; } = [];

        internal uint[][] SelfOwnedAudioRelativeHashes { get; set; } = [];

        internal uint[][] SelfOwnedImageRelativeHashes { get; set; } = [];

        internal uint[][] SelfOwnedMovieRelativeHashes { get; set; } = [];

        internal Dictionary<uint, string[]> AudioRelativeReverseDirectories { get; set; } = [];

        internal Dictionary<uint, string[]> ImageRelativeReverseDirectories { get; set; } = [];

        internal Dictionary<uint, string[]> MovieRelativeReverseDirectories { get; set; } = [];
    }

    private sealed class SourceRootDecodedEntry
    {
        internal uint RootId { get; set; }

        internal string[] ChartPaths { get; set; } = [];

        internal uint[] AudioRelativeHashes { get; set; } = [];

        internal uint[] ImageRelativeHashes { get; set; } = [];

        internal uint[] MovieRelativeHashes { get; set; } = [];

        internal int ChartFileCount { get; set; }

        internal int ResourceFileCount { get; set; }

        internal int TrackedFileCount { get; set; }
    }

    private sealed class SourceRootDecodedResult
    {
        internal string[] RootPaths { get; set; } = [];

        internal SourceRootDecodedEntry[] Entries { get; set; } = [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EBridgeSourceRootRequestNative
    {
        public uint root_id;
        public IntPtr root_path;
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
        public IntPtr last_write_filetimes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EBridgeGroupedFilesResultHeader
    {
        public uint contract_version;
        public uint header_size;
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
    private struct EBridgeSourceRootEntryHeader
    {
        public uint root_id;
        public ulong chart_count;
        public IntPtr chart_offsets;
        public uint audio_resource_key_hash_offset;
        public uint audio_resource_key_hash_length;
        public uint image_resource_key_hash_offset;
        public uint image_resource_key_hash_length;
        public uint movie_resource_key_hash_offset;
        public uint movie_resource_key_hash_length;
        public ulong chart_file_count;
        public ulong categorized_resource_file_count;
        public ulong tracked_file_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EBridgeSourceRootsResultHeader
    {
        public int status;
        public int error_code;
        public ulong root_count;
        public IntPtr roots;
        public IntPtr root_offsets;
        public IntPtr root_blob;
        public IntPtr chart_blob;
        public IntPtr audio_resource_key_hashes_blob;
        public IntPtr image_resource_key_hashes_blob;
        public IntPtr movie_resource_key_hashes_blob;
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
        public ulong raw_buffer_size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EBridgeResultHeader
    {
        public uint contract_version;
        public uint header_size;
        public int status;
        public int error_code;
        public ulong chart_count;
        public IntPtr chart_offsets;
        public IntPtr chart_blob;
        public IntPtr chart_last_write_filetimes;
        public ulong dir_count;
        public IntPtr dir_offsets;
        public IntPtr dir_blob;
        public IntPtr audio_resource_key_hash_offsets;
        public IntPtr audio_resource_key_hash_lengths;
        public IntPtr audio_resource_key_hashes_blob;
        public IntPtr image_resource_key_hash_offsets;
        public IntPtr image_resource_key_hash_lengths;
        public IntPtr image_resource_key_hashes_blob;
        public IntPtr movie_resource_key_hash_offsets;
        public IntPtr movie_resource_key_hash_lengths;
        public IntPtr movie_resource_key_hashes_blob;
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
        public ulong audio_resource_key_hash_count;
        public ulong image_resource_key_hash_count;
        public ulong movie_resource_key_hash_count;
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
        public IntPtr self_audio_resource_key_hash_offsets;
        public IntPtr self_audio_resource_key_hash_lengths;
        public IntPtr self_audio_resource_key_hashes_blob;
        public IntPtr self_image_resource_key_hash_offsets;
        public IntPtr self_image_resource_key_hash_lengths;
        public IntPtr self_image_resource_key_hashes_blob;
        public IntPtr self_movie_resource_key_hash_offsets;
        public IntPtr self_movie_resource_key_hash_lengths;
        public IntPtr self_movie_resource_key_hashes_blob;
        public ulong audio_relative_reverse_key_count;
        public IntPtr audio_relative_reverse_keys;
        public IntPtr audio_relative_reverse_offsets;
        public IntPtr audio_relative_reverse_lengths;
        public IntPtr audio_relative_reverse_indices_blob;
        public ulong image_relative_reverse_key_count;
        public IntPtr image_relative_reverse_keys;
        public IntPtr image_relative_reverse_offsets;
        public IntPtr image_relative_reverse_lengths;
        public IntPtr image_relative_reverse_indices_blob;
        public ulong movie_relative_reverse_key_count;
        public IntPtr movie_relative_reverse_keys;
        public IntPtr movie_relative_reverse_offsets;
        public IntPtr movie_relative_reverse_lengths;
        public IntPtr movie_relative_reverse_indices_blob;
        public long pack_reverse_build_ms;
        public long audio_reverse_build_ms;
        public long image_reverse_build_ms;
        public long movie_reverse_build_ms;
        public long pack_layout_ms;
        public long pack_alloc_ms;
        public long pack_write_ms;
        public uint reverse_index_bytes;
        public long chart_search_ms;
        public long chart_read_ms;
        public long audio_search_ms;
        public long audio_read_ms;
        public long image_search_ms;
        public long image_read_ms;
        public long movie_search_ms;
        public long movie_read_ms;
        public long chart_sdk_read_ms;
        public long chart_callback_ms;
        public long audio_sdk_read_ms;
        public long audio_callback_ms;
        public long image_sdk_read_ms;
        public long image_callback_ms;
        public long movie_sdk_read_ms;
        public long movie_callback_ms;
        public ulong chart_path_resize_count;
        public ulong chart_name_resize_count;
        public ulong audio_path_resize_count;
        public ulong audio_name_resize_count;
        public ulong image_path_resize_count;
        public ulong image_name_resize_count;
        public ulong movie_path_resize_count;
        public ulong movie_name_resize_count;
        public ulong text_count;
        public IntPtr text_offsets;
        public IntPtr text_blob;
        public IntPtr text_last_write_filetimes;
        public ulong text_query_hits;
        public long text_query_ms;
        public long text_search_ms;
        public long text_read_ms;
        public long text_sdk_read_ms;
        public long text_callback_ms;
        public ulong text_path_resize_count;
        public ulong text_name_resize_count;
    }
}
