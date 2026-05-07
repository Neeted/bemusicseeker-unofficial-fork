#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cwctype>
#include <thread>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

extern "C" {

struct EBridgeResult {
	unsigned int contract_version;
	unsigned int header_size;
	int status;
	int error_code;
	unsigned long long chart_count;
	unsigned int* chart_offsets;
	wchar_t* chart_blob;
	unsigned long long dir_count;
	unsigned int* dir_offsets;
	wchar_t* dir_blob;
	unsigned int* audio_resource_key_hash_offsets;
	unsigned int* audio_resource_key_hash_lengths;
	unsigned char* audio_resource_key_hashes_blob;
	unsigned int* image_resource_key_hash_offsets;
	unsigned int* image_resource_key_hash_lengths;
	unsigned char* image_resource_key_hashes_blob;
	unsigned int* movie_resource_key_hash_offsets;
	unsigned int* movie_resource_key_hash_lengths;
	unsigned char* movie_resource_key_hashes_blob;
	unsigned long long chart_query_hits;
	unsigned long long audio_query_hits;
	unsigned long long image_query_hits;
	unsigned long long movie_query_hits;
	long long chart_query_ms;
	long long audio_query_ms;
	long long image_query_ms;
	long long movie_query_ms;
	long long assign_ms;
	long long dedupe_ms;
	long long pack_ms;
	unsigned long long chart_directory_count;
	unsigned long long audio_assigned_count;
	unsigned long long image_assigned_count;
	unsigned long long movie_assigned_count;
	unsigned long long audio_resource_key_hash_count;
	unsigned long long image_resource_key_hash_count;
	unsigned long long movie_resource_key_hash_count;
	unsigned long long audio_resource_dir_count;
	unsigned long long image_resource_dir_count;
	unsigned long long movie_resource_dir_count;
	unsigned long long owner_cache_hit_count;
	unsigned long long owner_cache_miss_count;
	unsigned long long relative_prefix_cache_hit_count;
	unsigned long long relative_prefix_cache_miss_count;
	long long audio_group_ms;
	long long audio_assign_ms;
	long long audio_merge_ms;
	long long image_group_ms;
	long long image_assign_ms;
	long long image_merge_ms;
	long long movie_group_ms;
	long long movie_assign_ms;
	long long movie_merge_ms;
	unsigned long long raw_buffer_size;
	unsigned int* self_audio_resource_key_hash_offsets;
	unsigned int* self_audio_resource_key_hash_lengths;
	unsigned char* self_audio_resource_key_hashes_blob;
	unsigned int* self_image_resource_key_hash_offsets;
	unsigned int* self_image_resource_key_hash_lengths;
	unsigned char* self_image_resource_key_hashes_blob;
	unsigned int* self_movie_resource_key_hash_offsets;
	unsigned int* self_movie_resource_key_hash_lengths;
	unsigned char* self_movie_resource_key_hashes_blob;
	unsigned long long audio_relative_reverse_key_count;
	unsigned int* audio_relative_reverse_keys;
	unsigned int* audio_relative_reverse_offsets;
	unsigned int* audio_relative_reverse_lengths;
	unsigned char* audio_relative_reverse_indices_blob;
	unsigned long long image_relative_reverse_key_count;
	unsigned int* image_relative_reverse_keys;
	unsigned int* image_relative_reverse_offsets;
	unsigned int* image_relative_reverse_lengths;
	unsigned char* image_relative_reverse_indices_blob;
	unsigned long long movie_relative_reverse_key_count;
	unsigned int* movie_relative_reverse_keys;
	unsigned int* movie_relative_reverse_offsets;
	unsigned int* movie_relative_reverse_lengths;
	unsigned char* movie_relative_reverse_indices_blob;
	long long pack_reverse_build_ms;
	long long audio_reverse_build_ms;
	long long image_reverse_build_ms;
	long long movie_reverse_build_ms;
	long long pack_layout_ms;
	long long pack_alloc_ms;
	long long pack_write_ms;
	unsigned int reverse_index_bytes;
	long long chart_search_ms;
	long long chart_read_ms;
	long long audio_search_ms;
	long long audio_read_ms;
	long long image_search_ms;
	long long image_read_ms;
	long long movie_search_ms;
	long long movie_read_ms;
	long long chart_sdk_read_ms;
	long long chart_callback_ms;
	long long audio_sdk_read_ms;
	long long audio_callback_ms;
	long long image_sdk_read_ms;
	long long image_callback_ms;
	long long movie_sdk_read_ms;
	long long movie_callback_ms;
	unsigned long long chart_path_resize_count;
	unsigned long long chart_name_resize_count;
	unsigned long long audio_path_resize_count;
	unsigned long long audio_name_resize_count;
	unsigned long long image_path_resize_count;
	unsigned long long image_name_resize_count;
	unsigned long long movie_path_resize_count;
	unsigned long long movie_name_resize_count;
};

static constexpr unsigned int EBRIDGE_SCAN_CONTRACT_VERSION = 2026050708u;

struct EBridgeGroupedQuery {
	unsigned int group_id;
	const wchar_t* query_text;
};

struct EBridgeGroupedResultGroup {
	unsigned int group_id;
	unsigned long long hit_count;
	long long query_ms;
	unsigned long long path_count;
	unsigned int* path_offsets;
};

struct EBridgeGroupedFilesResult {
	int status;
	int error_code;
	unsigned long long group_count;
	EBridgeGroupedResultGroup* groups;
	wchar_t* path_blob;
	unsigned long long total_file_count;
	long long enumeration_ms;
	unsigned long long raw_buffer_size;
};

struct EBridgeSourceRootRequest {
	unsigned int root_id;
	const wchar_t* root_path;
};

struct EBridgeSourceRootEntry {
	unsigned int root_id;
	unsigned long long chart_count;
	unsigned int* chart_offsets;
	unsigned int audio_resource_key_hash_offset;
	unsigned int audio_resource_key_hash_length;
	unsigned int image_resource_key_hash_offset;
	unsigned int image_resource_key_hash_length;
	unsigned int movie_resource_key_hash_offset;
	unsigned int movie_resource_key_hash_length;
	unsigned long long chart_file_count;
	unsigned long long categorized_resource_file_count;
	unsigned long long tracked_file_count;
};

struct EBridgeSourceRootsResult {
	int status;
	int error_code;
	unsigned long long root_count;
	EBridgeSourceRootEntry* roots;
	unsigned int* root_offsets;
	wchar_t* root_blob;
	wchar_t* chart_blob;
	unsigned char* audio_resource_key_hashes_blob;
	unsigned char* image_resource_key_hashes_blob;
	unsigned char* movie_resource_key_hashes_blob;
	unsigned long long chart_query_hits;
	unsigned long long audio_query_hits;
	unsigned long long image_query_hits;
	unsigned long long movie_query_hits;
	long long chart_query_ms;
	long long audio_query_ms;
	long long image_query_ms;
	long long movie_query_ms;
	long long assign_ms;
	long long dedupe_ms;
	long long pack_ms;
	unsigned long long raw_buffer_size;
};

__declspec(dllexport) int __cdecl EBridge_ScanChartAndResources(const wchar_t* chartQuery, const wchar_t* audioQuery, const wchar_t* imageQuery, const wchar_t* movieQuery, EBridgeResult** outResult);
__declspec(dllexport) int __cdecl EBridge_ScanSourceRoots(const EBridgeSourceRootRequest* roots, unsigned int rootCount, const wchar_t* chartQuery, const wchar_t* audioQuery, const wchar_t* imageQuery, const wchar_t* movieQuery, EBridgeSourceRootsResult** outResult);
__declspec(dllexport) int __cdecl EBridge_EnumerateGroupedFiles(const EBridgeGroupedQuery* queries, unsigned int queryCount, EBridgeGroupedFilesResult** outResult);
__declspec(dllexport) void __cdecl EBridge_FreeResult(EBridgeResult* result);
__declspec(dllexport) void __cdecl EBridge_FreeSourceRootsResult(EBridgeSourceRootsResult* result);
__declspec(dllexport) void __cdecl EBridge_FreeGroupedFilesResult(EBridgeGroupedFilesResult* result);
}

namespace {

constexpr unsigned int EVERYTHING3_OK = 0;
constexpr unsigned int EVERYTHING3_ERROR_IPC_PIPE_NOT_FOUND = 0xE0000002u;
constexpr unsigned int EVERYTHING3_PROPERTY_ID_NAME = 0u;
constexpr unsigned int EVERYTHING3_PROPERTY_ID_PATH = 1u;
constexpr unsigned int EVERYTHING3_PROPERTY_ID_PATH_AND_NAME = 240u;
constexpr size_t SDK_READ_TIMING_EXACT_HIT_LIMIT = 2u * 1024u * 1024u;
constexpr size_t SDK_READ_TIMING_SAMPLE_INTERVAL = 512;

enum BridgeError {
	BRIDGE_OK = 0,
	BRIDGE_INVALID_ARGUMENT = 1,
	BRIDGE_LOAD_API_FAILED = 2,
	BRIDGE_CONNECT_FAILED = 3,
	BRIDGE_CHART_QUERY_FAILED = 4,
	BRIDGE_AUDIO_QUERY_FAILED = 5,
	BRIDGE_IMAGE_QUERY_FAILED = 6,
	BRIDGE_MOVIE_QUERY_FAILED = 7,
	BRIDGE_OUT_OF_MEMORY = 8,
	BRIDGE_INTERNAL_ERROR = 9,
	BRIDGE_GROUPED_QUERY_FAILED = 10,
	BRIDGE_SOURCE_ROOT_QUERY_FAILED = 11
};

using Everything3_ConnectWFn = void*(__stdcall*)(const wchar_t* instance_name);
using Everything3_DestroyClientFn = int(__stdcall*)(void* client);
using Everything3_GetLastErrorFn = unsigned int(__stdcall*)();
using Everything3_CreateSearchStateFn = void*(__stdcall*)();
using Everything3_DestroySearchStateFn = int(__stdcall*)(void* search_state);
using Everything3_SetSearchTextWFn = int(__stdcall*)(void* search_state, const wchar_t* search);
using Everything3_ClearSearchPropertyRequestsFn = int(__stdcall*)(void* search_state);
using Everything3_AddSearchPropertyRequestFn = int(__stdcall*)(void* search_state, unsigned int property_id);
using Everything3_SetSearchViewportOffsetFn = int(__stdcall*)(void* search_state, size_t offset);
using Everything3_SetSearchViewportCountFn = int(__stdcall*)(void* search_state, size_t count);
using Everything3_SearchFn = void*(__stdcall*)(void* client, void* search_state);
using Everything3_DestroyResultListFn = int(__stdcall*)(void* result_list);
using Everything3_GetResultListViewportCountFn = size_t(__stdcall*)(const void* result_list);
using Everything3_GetResultPathWFn = size_t(__stdcall*)(const void* result_list, size_t result_index, wchar_t* out_wbuf, size_t wbuf_size_in_wchars);
using Everything3_GetResultNameWFn = size_t(__stdcall*)(const void* result_list, size_t result_index, wchar_t* out_wbuf, size_t wbuf_size_in_wchars);
using Everything3_GetResultFullPathNameWFn = size_t(__stdcall*)(const void* result_list, size_t result_index, wchar_t* out_wbuf, size_t wbuf_size_in_wchars);

struct EverythingApi {
	HMODULE everythingModule = nullptr;
	Everything3_ConnectWFn ConnectW = nullptr;
	Everything3_DestroyClientFn DestroyClient = nullptr;
	Everything3_GetLastErrorFn GetLastError = nullptr;
	Everything3_CreateSearchStateFn CreateSearchState = nullptr;
	Everything3_DestroySearchStateFn DestroySearchState = nullptr;
	Everything3_SetSearchTextWFn SetSearchTextW = nullptr;
	Everything3_ClearSearchPropertyRequestsFn ClearSearchPropertyRequests = nullptr;
	Everything3_AddSearchPropertyRequestFn AddSearchPropertyRequest = nullptr;
	Everything3_SetSearchViewportOffsetFn SetSearchViewportOffset = nullptr;
	Everything3_SetSearchViewportCountFn SetSearchViewportCount = nullptr;
	Everything3_SearchFn Search = nullptr;
	Everything3_DestroyResultListFn DestroyResultList = nullptr;
	Everything3_GetResultListViewportCountFn GetResultListViewportCount = nullptr;
	Everything3_GetResultPathWFn GetResultPathW = nullptr;
	Everything3_GetResultNameWFn GetResultNameW = nullptr;
	Everything3_GetResultFullPathNameWFn GetResultFullPathNameW = nullptr;
};

struct ScanAggregate {
	std::vector<std::wstring> chartPaths;
	std::vector<std::wstring> chartDirectories;
	std::unordered_map<std::wstring, uint32_t> chartDirIndex;
	std::vector<std::vector<uint32_t>> audioResourceKeyHashes;
	std::vector<std::vector<uint32_t>> imageResourceKeyHashes;
	std::vector<std::vector<uint32_t>> movieResourceKeyHashes;
	std::vector<std::vector<uint32_t>> selfAudioResourceKeyHashes;
	std::vector<std::vector<uint32_t>> selfImageResourceKeyHashes;
	std::vector<std::vector<uint32_t>> selfMovieResourceKeyHashes;
};

struct ReverseHashGroup {
	std::vector<uint32_t> keys;
	std::vector<uint32_t> indexOffsets;
	std::vector<uint32_t> indexLengths;
	std::vector<uint32_t> directoryIndices;
};

struct QueryExecutionStats {
	unsigned long long hitCount = 0;
	long long elapsedMs = 0;
	long long searchMs = 0;
	long long readMs = 0;
	long long sdkReadMs = 0;
	long long callbackMs = 0;
	unsigned long long pathResizeCount = 0;
	unsigned long long nameResizeCount = 0;
};

struct BridgeExecutionStats {
	QueryExecutionStats chartQuery;
	QueryExecutionStats audioQuery;
	QueryExecutionStats imageQuery;
	QueryExecutionStats movieQuery;
	long long assignMs = 0;
	long long dedupeMs = 0;
	long long packMs = 0;
	long long packReverseBuildMs = 0;
	long long packLayoutMs = 0;
	long long packAllocMs = 0;
	long long packWriteMs = 0;
	unsigned long long audioAssignedCount = 0;
	unsigned long long imageAssignedCount = 0;
	unsigned long long movieAssignedCount = 0;
	unsigned long long audioResourceDirCount = 0;
	unsigned long long imageResourceDirCount = 0;
	unsigned long long movieResourceDirCount = 0;
	unsigned long long ownerCacheHitCount = 0;
	unsigned long long ownerCacheMissCount = 0;
	unsigned long long relativePrefixCacheHitCount = 0;
	unsigned long long relativePrefixCacheMissCount = 0;
	long long audioGroupMs = 0;
	long long audioAssignMs = 0;
	long long audioMergeMs = 0;
	long long imageGroupMs = 0;
	long long imageAssignMs = 0;
	long long imageMergeMs = 0;
	long long movieGroupMs = 0;
	long long movieAssignMs = 0;
	long long movieMergeMs = 0;
};

enum class ResourceCategory {
	Audio,
	Image,
	Movie
};

struct ResourceOwnerInfo {
	uint32_t chartDirIndex = 0;
	std::wstring relativePrefix;
	bool selfOwned = false;
};

struct ResourceDirectoryInfo {
	bool initialized = false;
	std::vector<ResourceOwnerInfo> owners;
};

struct ResourceAssignmentContext {
	std::unordered_map<std::wstring, ResourceDirectoryInfo> infosByResourceDirectory;
};

struct CategoryRawHits {
	std::unordered_map<std::wstring, std::vector<std::wstring>> fileNamesByDirectory;
};

struct ChartRawHits {
	std::vector<std::pair<std::wstring, std::wstring>> files;
};

struct NoopHitCountCallback {
	void operator()(size_t) const noexcept {}
};

struct WorkerCategoryBuffers {
	std::unordered_map<uint32_t, std::vector<uint32_t>> categoryResourceKeyHashesByChartIndex;
	std::unordered_map<uint32_t, std::vector<uint32_t>> selfCategoryResourceKeyHashesByChartIndex;
	unsigned long long assignedCount = 0;
};

struct CategoryProcessingMetrics {
	unsigned long long resourceDirCount = 0;
	unsigned long long assignedCount = 0;
	long long groupMs = 0;
	long long assignMs = 0;
	long long mergeMs = 0;
};

struct GroupedQueryResult {
	uint32_t groupId = 0u;
	QueryExecutionStats stats;
	std::vector<std::wstring> fullPaths;
};

struct SourceRootAggregate {
	uint32_t rootId = 0u;
	std::wstring rootPath;
	std::vector<std::wstring> chartPaths;
	std::vector<std::wstring> audioPaths;
	std::vector<std::wstring> imagePaths;
	std::vector<std::wstring> moviePaths;
	std::vector<uint32_t> audioResourceKeyHashes;
	std::vector<uint32_t> imageResourceKeyHashes;
	std::vector<uint32_t> movieResourceKeyHashes;
	std::unordered_set<std::wstring> resourceTrackedPaths;
	std::unordered_set<std::wstring> trackedPaths;
};

HMODULE g_bridgeModule = nullptr;
EverythingApi g_api;
bool g_apiLoaded = false;

template <typename T>
T LoadProc(HMODULE module, const char* name) {
	return reinterpret_cast<T>(GetProcAddress(module, name));
}

std::wstring GetDirectoryPathFromModule(HMODULE module) {
	std::wstring path(MAX_PATH, L'\0');
	DWORD len = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
	if (len == 0) {
		return L"";
	}
	while (len == path.size()) {
		path.resize(path.size() * 2);
		len = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
		if (len == 0) {
			return L"";
		}
	}
	path.resize(len);
	size_t pos = path.find_last_of(L"\\/");
	return pos == std::wstring::npos ? L"" : path.substr(0, pos + 1);
}

bool EnsureEverythingApiLoaded() {
	if (g_apiLoaded) {
		return g_api.everythingModule != nullptr;
	}

	g_apiLoaded = true;
	HMODULE module = GetModuleHandleW(L"Everything3_x64.dll");
	if (!module) {
		std::wstring dir = GetDirectoryPathFromModule(g_bridgeModule);
		if (dir.empty()) {
			return false;
		}
		std::wstring dllPath = dir + L"Everything3_x64.dll";
		module = LoadLibraryW(dllPath.c_str());
		if (!module) {
			return false;
		}
	}

	g_api.everythingModule = module;
	g_api.ConnectW = LoadProc<Everything3_ConnectWFn>(module, "Everything3_ConnectW");
	g_api.DestroyClient = LoadProc<Everything3_DestroyClientFn>(module, "Everything3_DestroyClient");
	g_api.GetLastError = LoadProc<Everything3_GetLastErrorFn>(module, "Everything3_GetLastError");
	g_api.CreateSearchState = LoadProc<Everything3_CreateSearchStateFn>(module, "Everything3_CreateSearchState");
	g_api.DestroySearchState = LoadProc<Everything3_DestroySearchStateFn>(module, "Everything3_DestroySearchState");
	g_api.SetSearchTextW = LoadProc<Everything3_SetSearchTextWFn>(module, "Everything3_SetSearchTextW");
	g_api.ClearSearchPropertyRequests = LoadProc<Everything3_ClearSearchPropertyRequestsFn>(module, "Everything3_ClearSearchPropertyRequests");
	g_api.AddSearchPropertyRequest = LoadProc<Everything3_AddSearchPropertyRequestFn>(module, "Everything3_AddSearchPropertyRequest");
	g_api.SetSearchViewportOffset = LoadProc<Everything3_SetSearchViewportOffsetFn>(module, "Everything3_SetSearchViewportOffset");
	g_api.SetSearchViewportCount = LoadProc<Everything3_SetSearchViewportCountFn>(module, "Everything3_SetSearchViewportCount");
	g_api.Search = LoadProc<Everything3_SearchFn>(module, "Everything3_Search");
	g_api.DestroyResultList = LoadProc<Everything3_DestroyResultListFn>(module, "Everything3_DestroyResultList");
	g_api.GetResultListViewportCount = LoadProc<Everything3_GetResultListViewportCountFn>(module, "Everything3_GetResultListViewportCount");
	g_api.GetResultPathW = LoadProc<Everything3_GetResultPathWFn>(module, "Everything3_GetResultPathW");
	g_api.GetResultNameW = LoadProc<Everything3_GetResultNameWFn>(module, "Everything3_GetResultNameW");
	g_api.GetResultFullPathNameW = LoadProc<Everything3_GetResultFullPathNameWFn>(module, "Everything3_GetResultFullPathNameW");

	return g_api.ConnectW && g_api.DestroyClient && g_api.GetLastError && g_api.CreateSearchState && g_api.DestroySearchState &&
		g_api.SetSearchTextW && g_api.ClearSearchPropertyRequests && g_api.AddSearchPropertyRequest &&
		g_api.SetSearchViewportOffset && g_api.SetSearchViewportCount && g_api.Search &&
		g_api.DestroyResultList && g_api.GetResultListViewportCount && g_api.GetResultPathW && g_api.GetResultNameW;
}

void* TryConnectClient(unsigned int* lastError) {
	if (lastError) {
		*lastError = EVERYTHING3_OK;
	}

	const wchar_t* instances[2] = { L"1.5a", nullptr };
	for (const wchar_t* instance : instances) {
		for (int i = 0; i < 20; i++) {
			void* client = g_api.ConnectW(instance);
			if (client) {
				return client;
			}
			unsigned int err = g_api.GetLastError();
			if (lastError) {
				*lastError = err;
			}
			if (err != EVERYTHING3_ERROR_IPC_PIPE_NOT_FOUND) {
				break;
			}
			Sleep(100);
		}
	}
	return nullptr;
}

bool GetResultPath(void* resultList, size_t index, std::vector<wchar_t>& buffer, std::wstring& out, unsigned long long* resizeCount = nullptr) {
	for (int retry = 0; retry < 4; retry++) {
		size_t len = g_api.GetResultPathW(resultList, index, buffer.data(), buffer.size());
		if (len == 0) {
			return false;
		}
		if (len < buffer.size()) {
			out.assign(buffer.data());
			return true;
		}
		buffer.resize(len + 1);
		if (resizeCount) {
			*resizeCount += 1;
		}
	}
	return false;
}

bool GetResultName(void* resultList, size_t index, std::vector<wchar_t>& buffer, std::wstring& out, unsigned long long* resizeCount = nullptr) {
	for (int retry = 0; retry < 4; retry++) {
		size_t len = g_api.GetResultNameW(resultList, index, buffer.data(), buffer.size());
		if (len == 0) {
			return false;
		}
		if (len < buffer.size()) {
			out.assign(buffer.data());
			return !out.empty();
		}
		buffer.resize(len + 1);
		if (resizeCount) {
			*resizeCount += 1;
		}
	}
	return false;
}

void AssignNormalizedDirectoryPath(const wchar_t* value, size_t length, std::wstring& output) {
	while (length > 0 && (value[length - 1] == L'\\' || value[length - 1] == L'/')) {
		length--;
	}
	output.resize(length);
	for (size_t i = 0; i < length; i++) {
		wchar_t ch = value[i];
		output[i] = ch == L'/' ? L'\\' : ch;
	}
}

bool SplitFullPathBuffer(const wchar_t* fullPath, size_t length, std::wstring& directoryPath, std::wstring& fileName) {
	if (!fullPath || length == 0) {
		return false;
	}
	size_t pos = length;
	while (pos > 0) {
		wchar_t ch = fullPath[pos - 1];
		if (ch == L'\\' || ch == L'/') {
			break;
		}
		pos -= 1;
	}
	if (pos == 0) {
		directoryPath.clear();
		fileName.assign(fullPath, length);
		return !fileName.empty();
	}
	if (pos >= length) {
		return false;
	}
	AssignNormalizedDirectoryPath(fullPath, pos - 1, directoryPath);
	fileName.assign(fullPath + pos, length - pos);
	return !fileName.empty();
}

bool GetResultFullPathAndSplit(void* resultList, size_t index, std::vector<wchar_t>& buffer, std::wstring& directoryPath, std::wstring& fileName, unsigned long long* resizeCount = nullptr) {
	for (int retry = 0; retry < 4; retry++) {
		size_t len = g_api.GetResultFullPathNameW(resultList, index, buffer.data(), buffer.size());
		if (len == 0) {
			return false;
		}
		if (len < buffer.size()) {
			return SplitFullPathBuffer(buffer.data(), len, directoryPath, fileName);
		}
		buffer.resize(len + 1);
		if (resizeCount) {
			*resizeCount += 1;
		}
	}
	return false;
}

std::wstring CombinePathAndName(const std::wstring& directoryPath, const std::wstring& fileName) {
	if (directoryPath.empty()) {
		return fileName;
	}
	wchar_t tail = directoryPath[directoryPath.size() - 1];
	if (tail == L'\\' || tail == L'/') {
		return directoryPath + fileName;
	}
	return directoryPath + L'\\' + fileName;
}

std::wstring ToUpperInvariant(const std::wstring& input) {
	if (input.empty()) {
		return std::wstring();
	}
	int required = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, input.c_str(), static_cast<int>(input.size()), nullptr, 0, nullptr, nullptr, 0);
	if (required <= 0) {
		std::wstring fallback = input;
		for (auto& ch : fallback) {
			ch = static_cast<wchar_t>(towupper(ch));
		}
		return fallback;
	}
	std::wstring output(required, L'\0');
	int converted = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, input.c_str(), static_cast<int>(input.size()), output.data(), required, nullptr, nullptr, 0);
	if (converted <= 0) {
		std::wstring fallback = input;
		for (auto& ch : fallback) {
			ch = static_cast<wchar_t>(towupper(ch));
		}
		return fallback;
	}
	return output;
}

std::wstring ReplaceAltSeparators(const std::wstring& input) {
	std::wstring normalized = input;
	for (auto& ch : normalized) {
		if (ch == L'/') {
			ch = L'\\';
		}
	}
	return normalized;
}

std::wstring NormalizeExtensionAlias(const std::wstring& value) {
	if (value.empty()) {
		return value;
	}
	size_t dot = value.find_last_of(L'.');
	if (dot == std::wstring::npos) {
		return value;
	}
	std::wstring extension = ToUpperInvariant(value.substr(dot));
	std::wstring alias = extension;
	if (extension == L".OGG" || extension == L".MP3" || extension == L".FLAC") {
		alias = L".WAV";
	} else if (extension == L".BMP" || extension == L".JPG" || extension == L".JPEG") {
		alias = L".PNG";
	}
	if (alias == extension) {
		return value;
	}
	return value.substr(0, dot) + alias;
}

std::wstring StripLookupExtension(const std::wstring& value) {
	if (value.empty()) {
		return value;
	}
	size_t slash = value.find_last_of(L"\\/");
	size_t dot = value.find_last_of(L'.');
	if (dot == std::wstring::npos || (slash != std::wstring::npos && dot < slash)) {
		return value;
	}
	return value.substr(0, dot);
}

std::wstring NormalizeFileNameForLookup(const std::wstring& fileName) {
	if (fileName.empty()) {
		return std::wstring();
	}
	std::wstring normalized = ReplaceAltSeparators(fileName);
	size_t pos = normalized.find_last_of(L'\\');
	if (pos != std::wstring::npos) {
		normalized = normalized.substr(pos + 1);
	}
	return StripLookupExtension(NormalizeExtensionAlias(normalized));
}

std::wstring NormalizeLookupFileNameFast(const std::wstring& fileName) {
	return NormalizeFileNameForLookup(fileName);
}

bool NormalizeLookupFileNameInto(const std::wstring& fileName, std::wstring& output) {
	output.clear();
	if (fileName.empty()) {
		return false;
	}
	size_t start = 0;
	size_t slash = fileName.find_last_of(L"\\/");
	if (slash != std::wstring::npos) {
		start = slash + 1;
	}
	size_t end = fileName.size();
	size_t dot = fileName.find_last_of(L'.');
	if (dot != std::wstring::npos && dot >= start) {
		end = dot;
	}
	if (end <= start) {
		return false;
	}
	output.assign(fileName.data() + start, end - start);
	for (wchar_t& ch : output) {
		if (ch == L'/') {
			ch = L'\\';
		}
	}
	return !output.empty();
}

bool IsDotOrDotDot(const std::wstring& segment) {
	return segment == L"." || segment == L"..";
}

std::wstring NormalizeReferencePathForLookup(const std::wstring& path) {
	if (path.empty()) {
		return std::wstring();
	}
	std::wstring normalized = ReplaceAltSeparators(path);
	while (!normalized.empty() && (normalized.front() == L'\\' || normalized.front() == L'/')) {
		normalized.erase(normalized.begin());
	}
	if (normalized.empty()) {
		return std::wstring();
	}
	std::vector<std::wstring> segments;
	size_t start = 0;
	for (;;) {
		size_t sep = normalized.find(L'\\', start);
		std::wstring segment = sep == std::wstring::npos ? normalized.substr(start) : normalized.substr(start, sep - start);
		if (!segment.empty()) {
			if (IsDotOrDotDot(segment)) {
				return std::wstring();
			}
			segments.push_back(segment);
		}
		if (sep == std::wstring::npos) {
			break;
		}
		start = sep + 1;
	}
	if (segments.empty()) {
		return std::wstring();
	}
	std::wstring joined;
	for (size_t i = 0; i < segments.size(); i++) {
		if (i > 0) {
			joined.push_back(L'\\');
		}
		joined += segments[i];
	}
	return NormalizeExtensionAlias(joined);
}

std::wstring NormalizeResourceKeyForLookup(const std::wstring& path) {
	return StripLookupExtension(NormalizeReferencePathForLookup(path));
}

bool StartsWithDirectoryPrefix(const std::wstring& filePath, const std::wstring& directoryPath) {
	if (filePath.size() < directoryPath.size()) {
		return false;
	}
	if (_wcsnicmp(filePath.c_str(), directoryPath.c_str(), directoryPath.size()) != 0) {
		return false;
	}
	if (filePath.size() == directoryPath.size()) {
		return true;
	}
	wchar_t next = filePath[directoryPath.size()];
	return next == L'\\' || next == L'/';
}

std::wstring TrimTrailingSeparators(const std::wstring& path) {
	if (path.empty()) {
		return path;
	}
	size_t end = path.size();
	while (end > 0 && (path[end - 1] == L'\\' || path[end - 1] == L'/')) {
		end--;
	}
	return path.substr(0, end);
}

void NormalizeDirectoryPathInPlace(std::wstring& path) {
	for (auto& ch : path) {
		if (ch == L'/') {
			ch = L'\\';
		}
	}
	while (!path.empty() && (path.back() == L'\\' || path.back() == L'/')) {
		path.pop_back();
	}
}

std::wstring GetParentDirectory(const std::wstring& path) {
	std::wstring trimmed = TrimTrailingSeparators(path);
	size_t pos = trimmed.find_last_of(L"\\/");
	if (pos == std::wstring::npos) {
		return std::wstring();
	}
	return trimmed.substr(0, pos);
}

std::wstring NormalizeRelativePathForLookup(const std::wstring& chartDirectory, const std::wstring& filePath) {
	std::wstring normalizedChartDir = TrimTrailingSeparators(ReplaceAltSeparators(chartDirectory));
	std::wstring normalizedFilePath = ReplaceAltSeparators(filePath);
	if (!StartsWithDirectoryPrefix(normalizedFilePath, normalizedChartDir)) {
		return std::wstring();
	}
	if (normalizedFilePath.size() == normalizedChartDir.size()) {
		return std::wstring();
	}
	std::wstring relative = normalizedFilePath.substr(normalizedChartDir.size());
	while (!relative.empty() && (relative.front() == L'\\' || relative.front() == L'/')) {
		relative.erase(relative.begin());
	}
	return NormalizeReferencePathForLookup(relative);
}

std::wstring NormalizeRelativeDirectoryPrefixForLookup(const std::wstring& chartDirectory, const std::wstring& resourceDirectory) {
	std::wstring normalizedChartDir = TrimTrailingSeparators(ReplaceAltSeparators(chartDirectory));
	std::wstring normalizedResourceDir = TrimTrailingSeparators(ReplaceAltSeparators(resourceDirectory));
	if (!StartsWithDirectoryPrefix(normalizedResourceDir, normalizedChartDir)) {
		return std::wstring();
	}
	if (normalizedResourceDir.size() == normalizedChartDir.size()) {
		return std::wstring();
	}
	std::wstring relative = normalizedResourceDir.substr(normalizedChartDir.size());
	while (!relative.empty() && (relative.front() == L'\\' || relative.front() == L'/')) {
		relative.erase(relative.begin());
	}
	if (relative.empty()) {
		return std::wstring();
	}
	std::wstring normalized = NormalizeReferencePathForLookup(relative);
	if (normalized.empty()) {
		return std::wstring();
	}
	return normalized + L"\\";
}

std::wstring NormalizeRelativeLookupPathFast(const std::wstring& normalizedRelativePrefix, const std::wstring& normalizedFileName) {
	if (normalizedFileName.empty()) {
		return std::wstring();
	}
	if (normalizedRelativePrefix.empty()) {
		return normalizedFileName;
	}
	return normalizedRelativePrefix + normalizedFileName;
}

inline uint32_t RotateLeft(uint32_t value, int count) {
	return (value << count) | (value >> (32 - count));
}

inline uint32_t ReadUInt32LE(const uint8_t* data, size_t index) {
	uint32_t value;
	std::memcpy(&value, data + index, sizeof(uint32_t));
	return value;
}

uint32_t CalcSubHash(uint32_t value, const uint8_t* buf, size_t index) {
	uint32_t num = ReadUInt32LE(buf, index);
	value += num * 2246822519u;
	value = RotateLeft(value, 13);
	value *= 2654435761u;
	return value;
}

uint32_t CalcXxHash32(const uint8_t* buf, size_t len, uint32_t seed = 0u) {
	size_t i = 0;
	uint32_t h32;
	if (len >= 16) {
		size_t limit = len - 16;
		uint32_t v1 = seed + 2654435761u + 2246822519u;
		uint32_t v2 = seed + 2246822519u;
		uint32_t v3 = seed;
		uint32_t v4 = seed - 2654435761u;
		do {
			v1 = CalcSubHash(v1, buf, i); i += 4;
			v2 = CalcSubHash(v2, buf, i); i += 4;
			v3 = CalcSubHash(v3, buf, i); i += 4;
			v4 = CalcSubHash(v4, buf, i); i += 4;
		} while (i <= limit);
		h32 = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);
	} else {
		h32 = seed + 374761393u;
	}

	h32 += static_cast<uint32_t>(len);
	for (; i + 4 <= len; i += 4) {
		h32 += ReadUInt32LE(buf, i) * 3266489917u;
		h32 = RotateLeft(h32, 17) * 668265263u;
	}
	for (; i < len; i++) {
		h32 += buf[i] * 374761393u;
		h32 = RotateLeft(h32, 11) * 2654435761u;
	}
	h32 ^= h32 >> 15;
	h32 *= 2246822519u;
	h32 ^= h32 >> 13;
	h32 *= 3266489917u;
	h32 ^= h32 >> 16;
	return h32;
}

uint32_t GetLookupHash(const std::wstring& normalizedLookupValue) {
	if (normalizedLookupValue.empty()) {
		return 0u;
	}
	std::wstring upper = ToUpperInvariant(normalizedLookupValue);
	const uint8_t* bytes = reinterpret_cast<const uint8_t*>(upper.data());
	size_t byteLen = upper.size() * sizeof(wchar_t);
	return CalcXxHash32(bytes, byteLen, 0u);
}

uint32_t GetLookupHashFast(const std::wstring& normalizedLookupValue) {
	return GetLookupHash(normalizedLookupValue);
}

uint32_t GetLookupHashWithUpperScratch(const std::wstring& normalizedLookupValue, std::wstring& upperScratch) {
	if (normalizedLookupValue.empty()) {
		return 0u;
	}
	int required = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, normalizedLookupValue.c_str(), static_cast<int>(normalizedLookupValue.size()), nullptr, 0, nullptr, nullptr, 0);
	if (required <= 0) {
		upperScratch.assign(normalizedLookupValue);
		for (auto& ch : upperScratch) {
			ch = static_cast<wchar_t>(towupper(ch));
		}
	} else {
		upperScratch.resize(required);
		int converted = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, normalizedLookupValue.c_str(), static_cast<int>(normalizedLookupValue.size()), upperScratch.data(), required, nullptr, nullptr, 0);
		if (converted <= 0) {
			upperScratch.assign(normalizedLookupValue);
			for (auto& ch : upperScratch) {
				ch = static_cast<wchar_t>(towupper(ch));
			}
		} else if (static_cast<size_t>(converted) < upperScratch.size()) {
			upperScratch.resize(converted);
		}
	}
	const uint8_t* bytes = reinterpret_cast<const uint8_t*>(upperScratch.data());
	size_t byteLen = upperScratch.size() * sizeof(wchar_t);
	return CalcXxHash32(bytes, byteLen, 0u);
}

uint32_t GetLookupHashFromPartsFast(const std::wstring& normalizedRelativePrefix, const std::wstring& normalizedFileName, std::wstring& combinedScratch, std::wstring& upperScratch) {
	if (normalizedFileName.empty()) {
		return 0u;
	}
	combinedScratch.clear();
	combinedScratch.reserve(normalizedRelativePrefix.size() + normalizedFileName.size());
	combinedScratch.append(normalizedRelativePrefix);
	combinedScratch.append(normalizedFileName);
	return GetLookupHashWithUpperScratch(combinedScratch, upperScratch);
}

void ReserveCategoryRawHits(CategoryRawHits& rawHits, size_t hitCount) {
	if (hitCount == 0) {
		return;
	}
	size_t directoryReserve = hitCount / 128;
	if (directoryReserve < 1024) {
		directoryReserve = 1024;
	}
	if (directoryReserve > 131072) {
		directoryReserve = 131072;
	}
	rawHits.fileNamesByDirectory.reserve(directoryReserve);
}

void AppendCategoryRawHit(CategoryRawHits& rawHits, std::wstring&& directoryPath, std::wstring&& fileName, size_t initialFileReserve) {
	auto inserted = rawHits.fileNamesByDirectory.try_emplace(std::move(directoryPath));
	if (inserted.second && initialFileReserve > 0) {
		inserted.first->second.reserve(initialFileReserve);
	}
	inserted.first->second.push_back(std::move(fileName));
}

template <typename Callback, typename HitCountCallback>
bool ExecuteQuery(void* client, const wchar_t* query, Callback&& onResult, QueryExecutionStats* stats, HitCountCallback&& onHitCount, bool useFullPathRead = false) {
	void* state = g_api.CreateSearchState();
	if (!state) {
		return false;
	}
	void* result = nullptr;
	bool ok = false;
	auto startedAt = std::chrono::steady_clock::now();
	do {
		if (!g_api.SetSearchTextW(state, query)) {
			break;
		}
		g_api.ClearSearchPropertyRequests(state);
		if (useFullPathRead) {
			g_api.AddSearchPropertyRequest(state, EVERYTHING3_PROPERTY_ID_PATH_AND_NAME);
		} else {
			g_api.AddSearchPropertyRequest(state, EVERYTHING3_PROPERTY_ID_PATH);
			g_api.AddSearchPropertyRequest(state, EVERYTHING3_PROPERTY_ID_NAME);
		}
		g_api.SetSearchViewportOffset(state, 0);
		g_api.SetSearchViewportCount(state, static_cast<size_t>(-1));
		auto searchStartedAt = std::chrono::steady_clock::now();
		result = g_api.Search(client, state);
		if (!result || g_api.GetLastError() != EVERYTHING3_OK) {
			break;
		}
		size_t hitCount = g_api.GetResultListViewportCount(result);
		if (stats) {
			stats->hitCount = static_cast<unsigned long long>(hitCount);
			stats->searchMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - searchStartedAt).count();
		}
		onHitCount(hitCount);
		auto readStartedAt = std::chrono::steady_clock::now();
		std::chrono::steady_clock::duration sdkReadDuration{};
		size_t sdkReadSampleCount = 0;
		const bool sampleSdkReadTiming = hitCount > SDK_READ_TIMING_EXACT_HIT_LIMIT;
		std::vector<wchar_t> pathBuffer(1024);
		std::vector<wchar_t> nameBuffer(512);
		std::wstring path;
		std::wstring name;
		for (size_t i = 0; i < hitCount; i++) {
			const bool timeSdkRead = !sampleSdkReadTiming || (i % SDK_READ_TIMING_SAMPLE_INTERVAL) == 0;
			std::chrono::steady_clock::time_point sdkReadStartedAt;
			if (timeSdkRead) {
				sdkReadStartedAt = std::chrono::steady_clock::now();
			}
			unsigned long long pathResizeBefore = stats ? stats->pathResizeCount : 0;
			unsigned long long nameResizeBefore = stats ? stats->nameResizeCount : 0;
			bool gotResult = useFullPathRead
				? GetResultFullPathAndSplit(result, i, pathBuffer, path, name, stats ? &stats->pathResizeCount : nullptr)
				: (GetResultPath(result, i, pathBuffer, path, stats ? &stats->pathResizeCount : nullptr)
					&& GetResultName(result, i, nameBuffer, name, stats ? &stats->nameResizeCount : nullptr));
			if (timeSdkRead) {
				auto sdkReadElapsed = std::chrono::steady_clock::now() - sdkReadStartedAt;
				bool resizedDuringSample = stats
					&& (stats->pathResizeCount != pathResizeBefore || stats->nameResizeCount != nameResizeBefore);
				if (!sampleSdkReadTiming || !resizedDuringSample) {
					sdkReadDuration += sdkReadElapsed;
					sdkReadSampleCount++;
				}
			}
			if (!gotResult) {
				continue;
			}
			onResult(path, name);
		}
		if (stats) {
			long long readMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - readStartedAt).count();
			long long sdkReadMs = 0;
			if (sampleSdkReadTiming && sdkReadSampleCount > 0) {
				long long sampleNs = std::chrono::duration_cast<std::chrono::nanoseconds>(sdkReadDuration).count();
				long double scale = static_cast<long double>(hitCount) / static_cast<long double>(sdkReadSampleCount);
				sdkReadMs = static_cast<long long>((static_cast<long double>(sampleNs) * scale) / 1000000.0L);
				if (sdkReadMs > readMs) {
					sdkReadMs = readMs;
				}
			} else {
				sdkReadMs = std::chrono::duration_cast<std::chrono::milliseconds>(sdkReadDuration).count();
			}
			stats->readMs = readMs;
			stats->sdkReadMs = sdkReadMs;
			stats->callbackMs = readMs > sdkReadMs ? readMs - sdkReadMs : 0;
		}
		ok = true;
	} while (false);

	if (result) {
		g_api.DestroyResultList(result);
	}
	g_api.DestroySearchState(state);
	if (stats) {
		stats->elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - startedAt).count();
	}
	return ok;
}

template <typename Callback>
bool ExecuteQuery(void* client, const wchar_t* query, Callback&& onResult, QueryExecutionStats* stats = nullptr) {
	return ExecuteQuery(client, query, std::forward<Callback>(onResult), stats, NoopHitCountCallback{});
}

template <typename Callback, typename HitCountCallback>
bool ExecuteQueryWithNewClient(const wchar_t* query, Callback&& onResult, QueryExecutionStats* stats, HitCountCallback&& onHitCount, bool useFullPathRead = false) {
	unsigned int connectError = EVERYTHING3_OK;
	void* client = TryConnectClient(&connectError);
	if (!client) {
		return false;
	}
	bool ok = ExecuteQuery(client, query, std::forward<Callback>(onResult), stats, std::forward<HitCountCallback>(onHitCount), useFullPathRead);
	g_api.DestroyClient(client);
	return ok;
}

template <typename Callback>
bool ExecuteQueryWithNewClient(const wchar_t* query, Callback&& onResult, QueryExecutionStats* stats = nullptr) {
	unsigned int connectError = EVERYTHING3_OK;
	void* client = TryConnectClient(&connectError);
	if (!client) {
		return false;
	}
	bool ok = ExecuteQuery(client, query, std::forward<Callback>(onResult), stats);
	g_api.DestroyClient(client);
	return ok;
}

void DedupePaths(std::vector<std::wstring>& paths) {
	std::sort(paths.begin(), paths.end());
	paths.erase(std::unique(paths.begin(), paths.end()), paths.end());
}

size_t AlignUp(size_t value, size_t align);
size_t SumBlobChars(const std::vector<std::wstring>& values);
void WriteStringBlob(uint8_t* raw, size_t offsetsPos, size_t blobPos, const std::vector<std::wstring>& values, unsigned int*& outOffsets, wchar_t*& outBlob);
void AddUniqueHash(std::vector<uint32_t>& hashes, uint32_t hash);

int FindBestMatchingRootIndex(const std::wstring& filePath, const std::vector<SourceRootAggregate>& roots) {
	int bestIndex = -1;
	size_t bestLength = 0;
	for (size_t i = 0; i < roots.size(); i++) {
		const std::wstring& rootPath = roots[i].rootPath;
		if (rootPath.empty() || !StartsWithDirectoryPrefix(filePath, rootPath)) {
			continue;
		}
		if (rootPath.size() >= bestLength) {
			bestIndex = static_cast<int>(i);
			bestLength = rootPath.size();
		}
	}
	return bestIndex;
}

void ProcessSourceRootResourcePaths(
	const std::wstring& rootPath,
	const std::vector<std::wstring>& fullPaths,
	std::vector<uint32_t>& categoryResourceKeyHashes,
	std::unordered_set<std::wstring>& resourceTrackedPaths,
	std::unordered_set<std::wstring>& trackedPaths)
{
	for (const std::wstring& fullPath : fullPaths) {
		std::wstring normalizedBaseName = NormalizeLookupFileNameFast(fullPath);
		std::wstring normalizedRelativePath = NormalizeResourceKeyForLookup(NormalizeRelativePathForLookup(rootPath, fullPath));
		if (normalizedBaseName.empty() || normalizedRelativePath.empty()) {
			continue;
		}
		uint32_t relativeHash = GetLookupHashFast(normalizedRelativePath);
		AddUniqueHash(categoryResourceKeyHashes, relativeHash);
		resourceTrackedPaths.insert(fullPath);
		trackedPaths.insert(fullPath);
	}
}

void DedupeHashes(std::vector<uint32_t>& hashes) {
	std::sort(hashes.begin(), hashes.end());
	hashes.erase(std::unique(hashes.begin(), hashes.end()), hashes.end());
}

void DedupeSourceRootAggregate(SourceRootAggregate& aggregate) {
	DedupePaths(aggregate.chartPaths);
	DedupePaths(aggregate.audioPaths);
	DedupePaths(aggregate.imagePaths);
	DedupePaths(aggregate.moviePaths);
	DedupeHashes(aggregate.audioResourceKeyHashes);
	DedupeHashes(aggregate.imageResourceKeyHashes);
	DedupeHashes(aggregate.movieResourceKeyHashes);
}

size_t SumSourceRootChartCount(const std::vector<SourceRootAggregate>& roots) {
	size_t total = 0;
	for (const SourceRootAggregate& root : roots) {
		total += root.chartPaths.size();
	}
	return total;
}

int BuildSourceRootsResultBuffer(
	const std::vector<SourceRootAggregate>& roots,
	const BridgeExecutionStats& stats,
	EBridgeSourceRootsResult** outResult)
{
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}

	size_t rootCount = roots.size();
	size_t totalChartCount = SumSourceRootChartCount(roots);
	std::vector<std::wstring> rootPaths;
	rootPaths.reserve(rootCount);
	size_t chartBlobChars = 0;
	for (const SourceRootAggregate& root : roots) {
		rootPaths.push_back(root.rootPath);
		chartBlobChars += SumBlobChars(root.chartPaths);
	}
	size_t rootBlobChars = SumBlobChars(rootPaths);
	size_t audioResourceKeyHashCount = 0;
	size_t imageResourceKeyHashCount = 0;
	size_t movieResourceKeyHashCount = 0;
	for (const SourceRootAggregate& root : roots) {
		audioResourceKeyHashCount += root.audioResourceKeyHashes.size();
		imageResourceKeyHashCount += root.imageResourceKeyHashes.size();
		movieResourceKeyHashCount += root.movieResourceKeyHashes.size();
	}

	size_t cursor = AlignUp(sizeof(EBridgeSourceRootsResult), 8);
	size_t rootEntriesPos = cursor; cursor += sizeof(EBridgeSourceRootEntry) * rootCount;
	size_t rootOffsetsPos = cursor; cursor += sizeof(uint32_t) * rootCount;
	size_t chartOffsetsPos = cursor; cursor += sizeof(uint32_t) * totalChartCount;

	cursor = AlignUp(cursor, alignof(wchar_t));
	size_t rootBlobPos = cursor; cursor += rootBlobChars * sizeof(wchar_t);
	size_t chartBlobPos = cursor; cursor += chartBlobChars * sizeof(wchar_t);

	cursor = AlignUp(cursor, alignof(uint32_t));
	size_t audioResourceKeyHashesPos = cursor; cursor += audioResourceKeyHashCount * sizeof(uint32_t);
	size_t imageResourceKeyHashesPos = cursor; cursor += imageResourceKeyHashCount * sizeof(uint32_t);
	size_t movieResourceKeyHashesPos = cursor; cursor += movieResourceKeyHashCount * sizeof(uint32_t);
	size_t totalBytes = cursor;

	uint8_t* raw = reinterpret_cast<uint8_t*>(std::malloc(totalBytes));
	if (!raw) {
		return BRIDGE_OUT_OF_MEMORY;
	}
	std::memset(raw, 0, totalBytes);

	auto* result = reinterpret_cast<EBridgeSourceRootsResult*>(raw);
	result->roots = reinterpret_cast<EBridgeSourceRootEntry*>(raw + rootEntriesPos);
	unsigned int* rootOffsets = reinterpret_cast<unsigned int*>(raw + rootOffsetsPos);
	unsigned int* chartOffsetsBase = reinterpret_cast<unsigned int*>(raw + chartOffsetsPos);
	result->root_offsets = rootOffsets;
	result->root_blob = reinterpret_cast<wchar_t*>(raw + rootBlobPos);
	result->chart_blob = reinterpret_cast<wchar_t*>(raw + chartBlobPos);
	result->audio_resource_key_hashes_blob = reinterpret_cast<unsigned char*>(raw + audioResourceKeyHashesPos);
	result->image_resource_key_hashes_blob = reinterpret_cast<unsigned char*>(raw + imageResourceKeyHashesPos);
	result->movie_resource_key_hashes_blob = reinterpret_cast<unsigned char*>(raw + movieResourceKeyHashesPos);

	WriteStringBlob(raw, rootOffsetsPos, rootBlobPos, rootPaths, result->root_offsets, result->root_blob);

	unsigned char* chartBlobCursor = reinterpret_cast<unsigned char*>(result->chart_blob);
	uint32_t audioResourceKeyHashOffsetBytes = 0;
	uint32_t imageResourceKeyHashOffsetBytes = 0;
	uint32_t movieResourceKeyHashOffsetBytes = 0;
	size_t globalChartIndex = 0;
	for (size_t rootIndex = 0; rootIndex < rootCount; rootIndex++) {
		const SourceRootAggregate& root = roots[rootIndex];
		EBridgeSourceRootEntry& entry = result->roots[rootIndex];
		entry.root_id = root.rootId;
		entry.chart_count = static_cast<unsigned long long>(root.chartPaths.size());
		entry.chart_offsets = chartOffsetsBase + globalChartIndex;
		for (const std::wstring& chartPath : root.chartPaths) {
			size_t chars = chartPath.size() + 1;
			chartOffsetsBase[globalChartIndex++] = static_cast<unsigned int>(chartBlobCursor - reinterpret_cast<unsigned char*>(result->chart_blob));
			std::memcpy(chartBlobCursor, chartPath.c_str(), chars * sizeof(wchar_t));
			chartBlobCursor += chars * sizeof(wchar_t);
		}

		auto writeHashVector = [&raw](size_t blobPos, const std::vector<uint32_t>& hashes, uint32_t& offsetBytes, unsigned int& offsetField, unsigned int& lengthField) {
			offsetField = offsetBytes;
			lengthField = static_cast<unsigned int>(hashes.size());
			size_t bytes = hashes.size() * sizeof(uint32_t);
			if (bytes > 0) {
				std::memcpy(raw + blobPos + offsetBytes, hashes.data(), bytes);
				offsetBytes += static_cast<uint32_t>(bytes);
			}
		};

		writeHashVector(audioResourceKeyHashesPos, root.audioResourceKeyHashes, audioResourceKeyHashOffsetBytes, entry.audio_resource_key_hash_offset, entry.audio_resource_key_hash_length);
		writeHashVector(imageResourceKeyHashesPos, root.imageResourceKeyHashes, imageResourceKeyHashOffsetBytes, entry.image_resource_key_hash_offset, entry.image_resource_key_hash_length);
		writeHashVector(movieResourceKeyHashesPos, root.movieResourceKeyHashes, movieResourceKeyHashOffsetBytes, entry.movie_resource_key_hash_offset, entry.movie_resource_key_hash_length);
		entry.chart_file_count = static_cast<unsigned long long>(root.chartPaths.size());
		entry.categorized_resource_file_count = static_cast<unsigned long long>(root.resourceTrackedPaths.size());
		entry.tracked_file_count = static_cast<unsigned long long>(root.trackedPaths.size());
	}

	result->status = BRIDGE_OK;
	result->error_code = 0;
	result->root_count = static_cast<unsigned long long>(rootCount);
	result->chart_query_hits = stats.chartQuery.hitCount;
	result->audio_query_hits = stats.audioQuery.hitCount;
	result->image_query_hits = stats.imageQuery.hitCount;
	result->movie_query_hits = stats.movieQuery.hitCount;
	result->chart_query_ms = stats.chartQuery.elapsedMs;
	result->audio_query_ms = stats.audioQuery.elapsedMs;
	result->image_query_ms = stats.imageQuery.elapsedMs;
	result->movie_query_ms = stats.movieQuery.elapsedMs;
	result->assign_ms = stats.assignMs;
	result->dedupe_ms = stats.dedupeMs;
	result->pack_ms = stats.packMs;
	result->raw_buffer_size = static_cast<unsigned long long>(totalBytes);

	*outResult = result;
	return BRIDGE_OK;
}

int BuildGroupedFilesResultBuffer(const std::vector<GroupedQueryResult>& groupedResults, long long enumerationMs, EBridgeGroupedFilesResult** outResult) {
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}

	size_t groupCount = groupedResults.size();
	size_t totalPathCount = 0;
	size_t totalBlobBytes = 0;
	std::unordered_set<std::wstring> uniquePaths;
	for (const GroupedQueryResult& groupedResult : groupedResults) {
		totalPathCount += groupedResult.fullPaths.size();
		for (const std::wstring& fullPath : groupedResult.fullPaths) {
			totalBlobBytes += (fullPath.size() + 1) * sizeof(wchar_t);
			uniquePaths.insert(fullPath);
		}
	}

	size_t totalBytes = sizeof(EBridgeGroupedFilesResult)
		+ sizeof(EBridgeGroupedResultGroup) * groupCount
		+ sizeof(unsigned int) * totalPathCount
		+ totalBlobBytes;
	unsigned char* buffer = static_cast<unsigned char*>(std::malloc(totalBytes));
	if (!buffer) {
		return BRIDGE_OUT_OF_MEMORY;
	}
	std::memset(buffer, 0, totalBytes);

	unsigned char* cursor = buffer + sizeof(EBridgeGroupedFilesResult);
	EBridgeGroupedResultGroup* groupHeaders = reinterpret_cast<EBridgeGroupedResultGroup*>(cursor);
	cursor += sizeof(EBridgeGroupedResultGroup) * groupCount;
	unsigned int* pathOffsetsBase = reinterpret_cast<unsigned int*>(cursor);
	cursor += sizeof(unsigned int) * totalPathCount;
	wchar_t* pathBlob = reinterpret_cast<wchar_t*>(cursor);

	EBridgeGroupedFilesResult* result = reinterpret_cast<EBridgeGroupedFilesResult*>(buffer);
	result->status = BRIDGE_OK;
	result->error_code = 0;
	result->group_count = static_cast<unsigned long long>(groupCount);
	result->groups = groupHeaders;
	result->path_blob = pathBlob;
	result->total_file_count = static_cast<unsigned long long>(uniquePaths.size());
	result->enumeration_ms = enumerationMs;
	result->raw_buffer_size = static_cast<unsigned long long>(totalBytes);

	size_t globalPathIndex = 0;
	unsigned char* blobCursor = reinterpret_cast<unsigned char*>(pathBlob);
	for (size_t groupIndex = 0; groupIndex < groupCount; groupIndex++) {
		const GroupedQueryResult& groupedResult = groupedResults[groupIndex];
		EBridgeGroupedResultGroup& groupHeader = groupHeaders[groupIndex];
		groupHeader.group_id = groupedResult.groupId;
		groupHeader.hit_count = groupedResult.stats.hitCount;
		groupHeader.query_ms = groupedResult.stats.elapsedMs;
		groupHeader.path_count = static_cast<unsigned long long>(groupedResult.fullPaths.size());
		groupHeader.path_offsets = pathOffsetsBase + globalPathIndex;

		for (const std::wstring& fullPath : groupedResult.fullPaths) {
			size_t chars = fullPath.size() + 1;
			pathOffsetsBase[globalPathIndex++] = static_cast<unsigned int>(blobCursor - reinterpret_cast<unsigned char*>(pathBlob));
			std::memcpy(blobCursor, fullPath.c_str(), chars * sizeof(wchar_t));
			blobCursor += chars * sizeof(wchar_t);
		}
	}

	*outResult = result;
	return BRIDGE_OK;
}

uint32_t EnsureChartDirectory(ScanAggregate& aggregate, const std::wstring& chartDirectory) {
	auto it = aggregate.chartDirIndex.find(chartDirectory);
	if (it != aggregate.chartDirIndex.end()) {
		return it->second;
	}
	uint32_t index = static_cast<uint32_t>(aggregate.chartDirectories.size());
	aggregate.chartDirectories.push_back(chartDirectory);
	aggregate.chartDirIndex.emplace(chartDirectory, index);
	aggregate.audioResourceKeyHashes.emplace_back();
	aggregate.imageResourceKeyHashes.emplace_back();
	aggregate.movieResourceKeyHashes.emplace_back();
	aggregate.selfAudioResourceKeyHashes.emplace_back();
	aggregate.selfImageResourceKeyHashes.emplace_back();
	aggregate.selfMovieResourceKeyHashes.emplace_back();
	return index;
}

std::vector<std::wstring> FindOwningChartDirectories(const ScanAggregate& aggregate, const std::wstring& resourceDirectory) {
	std::vector<std::wstring> owners;
	std::wstring current = TrimTrailingSeparators(resourceDirectory);
	while (!current.empty()) {
		if (aggregate.chartDirIndex.find(current) != aggregate.chartDirIndex.end()) {
			owners.push_back(current);
		}
		current = GetParentDirectory(current);
	}
	return owners;
}

void AddUniqueHash(std::vector<uint32_t>& hashes, uint32_t hash) {
	if (hash != 0u) {
		hashes.push_back(hash);
	}
}

const wchar_t* GetCategoryName(ResourceCategory category) {
	switch (category) {
	case ResourceCategory::Audio:
		return L"audio";
	case ResourceCategory::Image:
		return L"image";
	case ResourceCategory::Movie:
		return L"movie";
	default:
		return L"unknown";
	}
}

unsigned int GetWorkerCount(size_t workItemCount) {
	unsigned int concurrency = std::thread::hardware_concurrency();
	if (concurrency <= 1) {
		return 1;
	}
	unsigned int desired = concurrency - 1;
	if (desired == 0u) {
		desired = 1u;
	}
	if (desired > workItemCount) {
		desired = static_cast<unsigned int>(workItemCount);
	}
	return desired == 0u ? 1u : desired;
}

bool EnsureResourceDirectoryInfo(
	const ScanAggregate& aggregate,
	ResourceAssignmentContext& context,
	BridgeExecutionStats& stats,
	const std::wstring& resourceDirectory,
	ResourceDirectoryInfo& outInfo)
{
	auto existing = context.infosByResourceDirectory.find(resourceDirectory);
	if (existing != context.infosByResourceDirectory.end()) {
		outInfo = existing->second;
		stats.ownerCacheHitCount += 1;
		stats.relativePrefixCacheHitCount += 1;
		return !outInfo.owners.empty();
	}

	ResourceDirectoryInfo info;
	info.initialized = true;
	std::vector<std::wstring> ownerDirectories = FindOwningChartDirectories(aggregate, resourceDirectory);
	stats.ownerCacheMissCount += 1;
	stats.relativePrefixCacheMissCount += 1;
	for (size_t i = 0; i < ownerDirectories.size(); i++) {
		ResourceOwnerInfo ownerInfo;
		ownerInfo.chartDirIndex = aggregate.chartDirIndex.at(ownerDirectories[i]);
		ownerInfo.relativePrefix = NormalizeRelativeDirectoryPrefixForLookup(ownerDirectories[i], resourceDirectory);
		ownerInfo.selfOwned = (i == 0);
		info.owners.push_back(std::move(ownerInfo));
	}
	context.infosByResourceDirectory.emplace(resourceDirectory, info);
	outInfo = info;
	return !outInfo.owners.empty();
}

void AppendHashVector(std::unordered_map<uint32_t, std::vector<uint32_t>>& target, uint32_t chartDirIndex, uint32_t hashValue) {
	if (hashValue == 0u) {
		return;
	}
	target[chartDirIndex].push_back(hashValue);
}

struct CategoryMergeTargets {
	std::vector<std::vector<uint32_t>>* categoryResourceKeyHashes;
	std::vector<std::vector<uint32_t>>* selfCategoryResourceKeyHashes;
};

CategoryMergeTargets GetCategoryMergeTargets(ScanAggregate& aggregate, ResourceCategory category) {
	switch (category) {
	case ResourceCategory::Audio:
		return { &aggregate.audioResourceKeyHashes, &aggregate.selfAudioResourceKeyHashes };
	case ResourceCategory::Image:
		return { &aggregate.imageResourceKeyHashes, &aggregate.selfImageResourceKeyHashes };
	case ResourceCategory::Movie:
		return { &aggregate.movieResourceKeyHashes, &aggregate.selfMovieResourceKeyHashes };
	default:
		return { &aggregate.audioResourceKeyHashes, &aggregate.selfAudioResourceKeyHashes };
	}
}

void ApplyCategoryMetrics(BridgeExecutionStats& stats, ResourceCategory category, const CategoryProcessingMetrics& metrics) {
	switch (category) {
	case ResourceCategory::Audio:
		stats.audioResourceDirCount = metrics.resourceDirCount;
		stats.audioAssignedCount = metrics.assignedCount;
		stats.audioGroupMs = metrics.groupMs;
		stats.audioAssignMs = metrics.assignMs;
		stats.audioMergeMs = metrics.mergeMs;
		break;
	case ResourceCategory::Image:
		stats.imageResourceDirCount = metrics.resourceDirCount;
		stats.imageAssignedCount = metrics.assignedCount;
		stats.imageGroupMs = metrics.groupMs;
		stats.imageAssignMs = metrics.assignMs;
		stats.imageMergeMs = metrics.mergeMs;
		break;
	case ResourceCategory::Movie:
		stats.movieResourceDirCount = metrics.resourceDirCount;
		stats.movieAssignedCount = metrics.assignedCount;
		stats.movieGroupMs = metrics.groupMs;
		stats.movieAssignMs = metrics.assignMs;
		stats.movieMergeMs = metrics.mergeMs;
		break;
	}
}

void ProcessResourceCategory(
	ScanAggregate& aggregate,
	ResourceAssignmentContext& context,
	BridgeExecutionStats& stats,
	ResourceCategory category,
	CategoryRawHits&& rawHits)
{
	using WorkItem = std::pair<ResourceDirectoryInfo, std::vector<std::wstring>>;

	CategoryProcessingMetrics metrics;
	auto groupStartedAt = std::chrono::steady_clock::now();
	std::vector<WorkItem> workItems;
	workItems.reserve(rawHits.fileNamesByDirectory.size());
	for (auto& entry : rawHits.fileNamesByDirectory) {
		ResourceDirectoryInfo info;
		if (!EnsureResourceDirectoryInfo(aggregate, context, stats, entry.first, info)) {
			continue;
		}
		workItems.emplace_back(std::move(info), std::move(entry.second));
	}
	metrics.resourceDirCount = static_cast<unsigned long long>(rawHits.fileNamesByDirectory.size());
	metrics.groupMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - groupStartedAt).count();
	if (workItems.empty()) {
		ApplyCategoryMetrics(stats, category, metrics);
		return;
	}

	unsigned int workerCount = GetWorkerCount(workItems.size());
	std::vector<WorkerCategoryBuffers> workerBuffers(workerCount);
	auto assignStartedAt = std::chrono::steady_clock::now();
	std::vector<std::thread> workers;
	workers.reserve(workerCount);
	size_t baseChunkSize = workItems.size() / workerCount;
	size_t remainder = workItems.size() % workerCount;
	size_t offset = 0;
	for (unsigned int workerIndex = 0; workerIndex < workerCount; workerIndex++) {
		size_t count = baseChunkSize + (workerIndex < remainder ? 1 : 0);
		size_t startIndex = offset;
		size_t endIndex = startIndex + count;
		offset = endIndex;
		workers.emplace_back([&workItems, &workerBuffers, workerIndex, startIndex, endIndex]() {
			WorkerCategoryBuffers& buffers = workerBuffers[workerIndex];
			std::wstring normalizedBaseNameScratch;
			std::wstring combinedLookupScratch;
			std::wstring upperLookupScratch;
			for (size_t i = startIndex; i < endIndex; i++) {
				const ResourceDirectoryInfo& info = workItems[i].first;
				for (const std::wstring& fileName : workItems[i].second) {
					if (!NormalizeLookupFileNameInto(fileName, normalizedBaseNameScratch)) {
						continue;
					}
					for (const ResourceOwnerInfo& ownerInfo : info.owners) {
						uint32_t relativeHash = GetLookupHashFromPartsFast(ownerInfo.relativePrefix, normalizedBaseNameScratch, combinedLookupScratch, upperLookupScratch);
						AppendHashVector(buffers.categoryResourceKeyHashesByChartIndex, ownerInfo.chartDirIndex, relativeHash);
						if (ownerInfo.selfOwned) {
							AppendHashVector(buffers.selfCategoryResourceKeyHashesByChartIndex, ownerInfo.chartDirIndex, relativeHash);
						}
					}
					buffers.assignedCount += 1;
				}
			}
		});
	}
	for (std::thread& worker : workers) {
		worker.join();
	}
	metrics.assignMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - assignStartedAt).count();

	auto mergeStartedAt = std::chrono::steady_clock::now();
	CategoryMergeTargets mergeTargets = GetCategoryMergeTargets(aggregate, category);
	for (const WorkerCategoryBuffers& buffers : workerBuffers) {
		metrics.assignedCount += buffers.assignedCount;
		for (const auto& item : buffers.categoryResourceKeyHashesByChartIndex) {
			auto& destination = (*mergeTargets.categoryResourceKeyHashes)[item.first];
			destination.insert(destination.end(), item.second.begin(), item.second.end());
		}
		for (const auto& item : buffers.selfCategoryResourceKeyHashesByChartIndex) {
			auto& destination = (*mergeTargets.selfCategoryResourceKeyHashes)[item.first];
			destination.insert(destination.end(), item.second.begin(), item.second.end());
		}
	}
	metrics.mergeMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - mergeStartedAt).count();
	ApplyCategoryMetrics(stats, category, metrics);
}

void DedupeAggregate(ScanAggregate& aggregate) {
	std::sort(aggregate.chartPaths.begin(), aggregate.chartPaths.end());
	aggregate.chartPaths.erase(std::unique(aggregate.chartPaths.begin(), aggregate.chartPaths.end()), aggregate.chartPaths.end());
	std::vector<std::vector<std::vector<uint32_t>>*> groups = {
		&aggregate.audioResourceKeyHashes,
		&aggregate.imageResourceKeyHashes,
		&aggregate.movieResourceKeyHashes,
		&aggregate.selfAudioResourceKeyHashes,
		&aggregate.selfImageResourceKeyHashes,
		&aggregate.selfMovieResourceKeyHashes
	};
	unsigned int workerCount = GetWorkerCount(groups.size());
	std::vector<std::thread> workers;
	workers.reserve(workerCount);
	size_t baseChunkSize = groups.size() / workerCount;
	size_t remainder = groups.size() % workerCount;
	size_t offset = 0;
	for (unsigned int workerIndex = 0; workerIndex < workerCount; workerIndex++) {
		size_t count = baseChunkSize + (workerIndex < remainder ? 1 : 0);
		size_t startIndex = offset;
		size_t endIndex = startIndex + count;
		offset = endIndex;
		workers.emplace_back([&groups, startIndex, endIndex]() {
			for (size_t groupIndex = startIndex; groupIndex < endIndex; groupIndex++) {
				for (auto& vector : *groups[groupIndex]) {
					std::sort(vector.begin(), vector.end());
					vector.erase(std::unique(vector.begin(), vector.end()), vector.end());
				}
			}
		});
	}
	for (std::thread& worker : workers) {
		worker.join();
	}
}

size_t AlignUp(size_t value, size_t align) {
	return (value + (align - 1)) & ~(align - 1);
}

size_t SumBlobChars(const std::vector<std::wstring>& values) {
	size_t total = 0;
	for (const auto& value : values) {
		total += value.size() + 1;
	}
	return total;
}

size_t SumHashCount(const std::vector<std::vector<uint32_t>>& hashGroups) {
	size_t total = 0;
	for (const auto& hashes : hashGroups) {
		total += hashes.size();
	}
	return total;
}

void SortEncodedHashDirectoryPairs(std::vector<uint64_t>& values) {
	if (values.size() < 65536) {
		std::sort(values.begin(), values.end());
		return;
	}

	constexpr uint64_t mask = 0xFFFFull;
	constexpr size_t bucketCount = 1u << 16;
	std::vector<uint64_t> buffer(values.size());
	std::vector<size_t> counts(bucketCount);
	for (int shift = 0; shift < 64; shift += 16) {
		std::fill(counts.begin(), counts.end(), 0);
		for (uint64_t value : values) {
			counts[static_cast<size_t>((value >> shift) & mask)]++;
		}
		size_t cursor = 0;
		for (size_t i = 0; i < counts.size(); i++) {
			size_t count = counts[i];
			counts[i] = cursor;
			cursor += count;
		}
		for (uint64_t value : values) {
			size_t bucket = static_cast<size_t>((value >> shift) & mask);
			buffer[counts[bucket]++] = value;
		}
		values.swap(buffer);
	}
}

ReverseHashGroup BuildReverseHashGroup(const std::vector<std::vector<uint32_t>>& hashGroups) {
	std::vector<uint64_t> hashDirectoryPairs;
	hashDirectoryPairs.reserve(SumHashCount(hashGroups));
	for (uint32_t directoryIndex = 0; directoryIndex < hashGroups.size(); directoryIndex++) {
		for (uint32_t hash : hashGroups[directoryIndex]) {
			if (hash == 0u) {
				continue;
			}
			hashDirectoryPairs.push_back((static_cast<uint64_t>(hash) << 32) | static_cast<uint64_t>(directoryIndex));
		}
	}

	ReverseHashGroup result;
	if (hashDirectoryPairs.empty()) {
		return result;
	}

	SortEncodedHashDirectoryPairs(hashDirectoryPairs);

	result.directoryIndices.reserve(hashDirectoryPairs.size());
	size_t index = 0;
	while (index < hashDirectoryPairs.size()) {
		uint32_t key = static_cast<uint32_t>(hashDirectoryPairs[index] >> 32);
		uint32_t offset = static_cast<uint32_t>(result.directoryIndices.size());
		bool hasLastDirectory = false;
		uint32_t lastDirectoryIndex = 0;
		while (index < hashDirectoryPairs.size() && static_cast<uint32_t>(hashDirectoryPairs[index] >> 32) == key) {
			uint32_t directoryIndex = static_cast<uint32_t>(hashDirectoryPairs[index] & 0xFFFFFFFFull);
			if (!hasLastDirectory || lastDirectoryIndex != directoryIndex) {
				result.directoryIndices.push_back(directoryIndex);
				lastDirectoryIndex = directoryIndex;
				hasLastDirectory = true;
			}
			index++;
		}
		result.keys.push_back(key);
		result.indexOffsets.push_back(offset);
		result.indexLengths.push_back(static_cast<uint32_t>(result.directoryIndices.size() - offset));
	}
	return result;
}

size_t SumReverseIndexCount(const ReverseHashGroup& group) {
	return group.directoryIndices.size();
}

uint32_t GetReverseIndexBytes(size_t directoryCount) {
	return directoryCount <= 0xFFFFu ? 2u : 4u;
}

void WriteStringBlob(uint8_t* raw, size_t offsetsPos, size_t blobPos, const std::vector<std::wstring>& values, unsigned int*& outOffsets, wchar_t*& outBlob) {
	outOffsets = reinterpret_cast<unsigned int*>(raw + offsetsPos);
	outBlob = reinterpret_cast<wchar_t*>(raw + blobPos);
	uint32_t blobOffsetBytes = 0;
	for (size_t i = 0; i < values.size(); i++) {
		outOffsets[i] = blobOffsetBytes;
		const auto& value = values[i];
		size_t charCount = value.size() + 1;
		std::memcpy(reinterpret_cast<uint8_t*>(outBlob) + blobOffsetBytes, value.c_str(), charCount * sizeof(wchar_t));
		blobOffsetBytes += static_cast<uint32_t>(charCount * sizeof(wchar_t));
	}
}

void WriteHashGroup(uint8_t* raw, size_t offsetsPos, size_t lengthsPos, size_t blobPos, const std::vector<std::vector<uint32_t>>& hashGroups, unsigned int*& outOffsets, unsigned int*& outLengths, unsigned char*& outBlob) {
	outOffsets = reinterpret_cast<unsigned int*>(raw + offsetsPos);
	outLengths = reinterpret_cast<unsigned int*>(raw + lengthsPos);
	outBlob = reinterpret_cast<unsigned char*>(raw + blobPos);
	uint32_t hashOffsetBytes = 0;
	for (size_t i = 0; i < hashGroups.size(); i++) {
		const auto& hashes = hashGroups[i];
		outOffsets[i] = hashOffsetBytes;
		outLengths[i] = static_cast<uint32_t>(hashes.size());
		size_t bytes = hashes.size() * sizeof(uint32_t);
		if (bytes > 0) {
			std::memcpy(outBlob + hashOffsetBytes, hashes.data(), bytes);
			hashOffsetBytes += static_cast<uint32_t>(bytes);
		}
	}
}

void WriteReverseHashGroup(
	uint8_t* raw,
	size_t keysPos,
	size_t offsetsPos,
	size_t lengthsPos,
	size_t indicesPos,
	uint32_t indexBytes,
	const ReverseHashGroup& group,
	unsigned int*& outKeys,
	unsigned int*& outOffsets,
	unsigned int*& outLengths,
	unsigned char*& outIndicesBlob)
{
	outKeys = reinterpret_cast<unsigned int*>(raw + keysPos);
	outOffsets = reinterpret_cast<unsigned int*>(raw + offsetsPos);
	outLengths = reinterpret_cast<unsigned int*>(raw + lengthsPos);
	outIndicesBlob = reinterpret_cast<unsigned char*>(raw + indicesPos);
	for (size_t i = 0; i < group.keys.size(); i++) {
		outKeys[i] = group.keys[i];
		outOffsets[i] = group.indexOffsets[i] * indexBytes;
		outLengths[i] = group.indexLengths[i];
	}

	if (group.directoryIndices.empty()) {
		return;
	}

	if (indexBytes == 2u) {
		auto* indicesBlob16 = reinterpret_cast<uint16_t*>(raw + indicesPos);
		for (size_t i = 0; i < group.directoryIndices.size(); i++) {
			indicesBlob16[i] = static_cast<uint16_t>(group.directoryIndices[i]);
		}
	} else {
		std::memcpy(outIndicesBlob, group.directoryIndices.data(), group.directoryIndices.size() * sizeof(uint32_t));
	}
}

int BuildResultBuffer(const ScanAggregate& aggregate, const BridgeExecutionStats& stats, EBridgeResult** outResult) {
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}

	auto layoutStartedAt = std::chrono::steady_clock::now();
	size_t chartCount = aggregate.chartPaths.size();
	size_t dirCount = aggregate.chartDirectories.size();

	size_t chartBlobChars = SumBlobChars(aggregate.chartPaths);
	size_t dirBlobChars = SumBlobChars(aggregate.chartDirectories);
	size_t audioResourceKeyHashCount = SumHashCount(aggregate.audioResourceKeyHashes);
	size_t imageResourceKeyHashCount = SumHashCount(aggregate.imageResourceKeyHashes);
	size_t movieResourceKeyHashCount = SumHashCount(aggregate.movieResourceKeyHashes);
	size_t selfAudioResourceKeyHashCount = SumHashCount(aggregate.selfAudioResourceKeyHashes);
	size_t selfImageResourceKeyHashCount = SumHashCount(aggregate.selfImageResourceKeyHashes);
	size_t selfMovieResourceKeyHashCount = SumHashCount(aggregate.selfMovieResourceKeyHashes);
	long long layoutMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - layoutStartedAt).count();

	auto reverseBuildStartedAt = std::chrono::steady_clock::now();
	ReverseHashGroup audioRelativeReverse;
	ReverseHashGroup imageRelativeReverse;
	ReverseHashGroup movieRelativeReverse;
	long long audioReverseBuildMs = 0;
	long long imageReverseBuildMs = 0;
	long long movieReverseBuildMs = 0;
	std::thread audioReverseWorker([&]() {
		auto startedAt = std::chrono::steady_clock::now();
		audioRelativeReverse = BuildReverseHashGroup(aggregate.audioResourceKeyHashes);
		audioReverseBuildMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - startedAt).count();
	});
	std::thread imageReverseWorker([&]() {
		auto startedAt = std::chrono::steady_clock::now();
		imageRelativeReverse = BuildReverseHashGroup(aggregate.imageResourceKeyHashes);
		imageReverseBuildMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - startedAt).count();
	});
	std::thread movieReverseWorker([&]() {
		auto startedAt = std::chrono::steady_clock::now();
		movieRelativeReverse = BuildReverseHashGroup(aggregate.movieResourceKeyHashes);
		movieReverseBuildMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - startedAt).count();
	});
	audioReverseWorker.join();
	imageReverseWorker.join();
	movieReverseWorker.join();
	long long reverseBuildMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - reverseBuildStartedAt).count();

	layoutStartedAt = std::chrono::steady_clock::now();
	size_t audioRelativeReverseIndexCount = SumReverseIndexCount(audioRelativeReverse);
	size_t imageRelativeReverseIndexCount = SumReverseIndexCount(imageRelativeReverse);
	size_t movieRelativeReverseIndexCount = SumReverseIndexCount(movieRelativeReverse);
	uint32_t reverseIndexBytes = GetReverseIndexBytes(dirCount);

	size_t cursor = AlignUp(sizeof(EBridgeResult), 8);
	size_t chartOffsetsPos = cursor; cursor += chartCount * sizeof(uint32_t);
	size_t dirOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);

	size_t audioResourceKeyOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t audioResourceKeyLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t imageResourceKeyOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t imageResourceKeyLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t movieResourceKeyOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t movieResourceKeyLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t selfAudioResourceKeyOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t selfAudioResourceKeyLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t selfImageResourceKeyOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t selfImageResourceKeyLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t selfMovieResourceKeyOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t selfMovieResourceKeyLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t audioRelativeReverseKeysPos = cursor; cursor += audioRelativeReverse.keys.size() * sizeof(uint32_t);
	size_t audioRelativeReverseOffsetsPos = cursor; cursor += audioRelativeReverse.keys.size() * sizeof(uint32_t);
	size_t audioRelativeReverseLengthsPos = cursor; cursor += audioRelativeReverse.keys.size() * sizeof(uint32_t);
	size_t imageRelativeReverseKeysPos = cursor; cursor += imageRelativeReverse.keys.size() * sizeof(uint32_t);
	size_t imageRelativeReverseOffsetsPos = cursor; cursor += imageRelativeReverse.keys.size() * sizeof(uint32_t);
	size_t imageRelativeReverseLengthsPos = cursor; cursor += imageRelativeReverse.keys.size() * sizeof(uint32_t);
	size_t movieRelativeReverseKeysPos = cursor; cursor += movieRelativeReverse.keys.size() * sizeof(uint32_t);
	size_t movieRelativeReverseOffsetsPos = cursor; cursor += movieRelativeReverse.keys.size() * sizeof(uint32_t);
	size_t movieRelativeReverseLengthsPos = cursor; cursor += movieRelativeReverse.keys.size() * sizeof(uint32_t);

	cursor = AlignUp(cursor, alignof(wchar_t));
	size_t chartBlobPos = cursor; cursor += chartBlobChars * sizeof(wchar_t);
	size_t dirBlobPos = cursor; cursor += dirBlobChars * sizeof(wchar_t);

	cursor = AlignUp(cursor, alignof(uint32_t));
	size_t audioResourceKeyHashesPos = cursor; cursor += audioResourceKeyHashCount * sizeof(uint32_t);
	size_t imageResourceKeyHashesPos = cursor; cursor += imageResourceKeyHashCount * sizeof(uint32_t);
	size_t movieResourceKeyHashesPos = cursor; cursor += movieResourceKeyHashCount * sizeof(uint32_t);
	size_t selfAudioResourceKeyHashesPos = cursor; cursor += selfAudioResourceKeyHashCount * sizeof(uint32_t);
	size_t selfImageResourceKeyHashesPos = cursor; cursor += selfImageResourceKeyHashCount * sizeof(uint32_t);
	size_t selfMovieResourceKeyHashesPos = cursor; cursor += selfMovieResourceKeyHashCount * sizeof(uint32_t);
	size_t audioRelativeReverseIndicesPos = cursor; cursor += audioRelativeReverseIndexCount * reverseIndexBytes;
	size_t imageRelativeReverseIndicesPos = cursor; cursor += imageRelativeReverseIndexCount * reverseIndexBytes;
	size_t movieRelativeReverseIndicesPos = cursor; cursor += movieRelativeReverseIndexCount * reverseIndexBytes;
	size_t totalBytes = cursor;
	layoutMs += std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - layoutStartedAt).count();

	auto allocStartedAt = std::chrono::steady_clock::now();
	uint8_t* raw = reinterpret_cast<uint8_t*>(std::malloc(totalBytes));
	if (!raw) {
		return BRIDGE_OUT_OF_MEMORY;
	}

	auto* result = reinterpret_cast<EBridgeResult*>(raw);
	std::memset(result, 0, sizeof(EBridgeResult));
	long long allocMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - allocStartedAt).count();

	auto writeStartedAt = std::chrono::steady_clock::now();
	WriteStringBlob(raw, chartOffsetsPos, chartBlobPos, aggregate.chartPaths, result->chart_offsets, result->chart_blob);
	WriteStringBlob(raw, dirOffsetsPos, dirBlobPos, aggregate.chartDirectories, result->dir_offsets, result->dir_blob);
	WriteHashGroup(raw, audioResourceKeyOffsetsPos, audioResourceKeyLengthsPos, audioResourceKeyHashesPos, aggregate.audioResourceKeyHashes, result->audio_resource_key_hash_offsets, result->audio_resource_key_hash_lengths, result->audio_resource_key_hashes_blob);
	WriteHashGroup(raw, imageResourceKeyOffsetsPos, imageResourceKeyLengthsPos, imageResourceKeyHashesPos, aggregate.imageResourceKeyHashes, result->image_resource_key_hash_offsets, result->image_resource_key_hash_lengths, result->image_resource_key_hashes_blob);
	WriteHashGroup(raw, movieResourceKeyOffsetsPos, movieResourceKeyLengthsPos, movieResourceKeyHashesPos, aggregate.movieResourceKeyHashes, result->movie_resource_key_hash_offsets, result->movie_resource_key_hash_lengths, result->movie_resource_key_hashes_blob);
	WriteHashGroup(raw, selfAudioResourceKeyOffsetsPos, selfAudioResourceKeyLengthsPos, selfAudioResourceKeyHashesPos, aggregate.selfAudioResourceKeyHashes, result->self_audio_resource_key_hash_offsets, result->self_audio_resource_key_hash_lengths, result->self_audio_resource_key_hashes_blob);
	WriteHashGroup(raw, selfImageResourceKeyOffsetsPos, selfImageResourceKeyLengthsPos, selfImageResourceKeyHashesPos, aggregate.selfImageResourceKeyHashes, result->self_image_resource_key_hash_offsets, result->self_image_resource_key_hash_lengths, result->self_image_resource_key_hashes_blob);
	WriteHashGroup(raw, selfMovieResourceKeyOffsetsPos, selfMovieResourceKeyLengthsPos, selfMovieResourceKeyHashesPos, aggregate.selfMovieResourceKeyHashes, result->self_movie_resource_key_hash_offsets, result->self_movie_resource_key_hash_lengths, result->self_movie_resource_key_hashes_blob);
	WriteReverseHashGroup(raw, audioRelativeReverseKeysPos, audioRelativeReverseOffsetsPos, audioRelativeReverseLengthsPos, audioRelativeReverseIndicesPos, reverseIndexBytes, audioRelativeReverse, result->audio_relative_reverse_keys, result->audio_relative_reverse_offsets, result->audio_relative_reverse_lengths, result->audio_relative_reverse_indices_blob);
	WriteReverseHashGroup(raw, imageRelativeReverseKeysPos, imageRelativeReverseOffsetsPos, imageRelativeReverseLengthsPos, imageRelativeReverseIndicesPos, reverseIndexBytes, imageRelativeReverse, result->image_relative_reverse_keys, result->image_relative_reverse_offsets, result->image_relative_reverse_lengths, result->image_relative_reverse_indices_blob);
	WriteReverseHashGroup(raw, movieRelativeReverseKeysPos, movieRelativeReverseOffsetsPos, movieRelativeReverseLengthsPos, movieRelativeReverseIndicesPos, reverseIndexBytes, movieRelativeReverse, result->movie_relative_reverse_keys, result->movie_relative_reverse_offsets, result->movie_relative_reverse_lengths, result->movie_relative_reverse_indices_blob);
	long long writeMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - writeStartedAt).count();

	result->contract_version = EBRIDGE_SCAN_CONTRACT_VERSION;
	result->header_size = static_cast<unsigned int>(sizeof(EBridgeResult));
	result->status = 0;
	result->error_code = 0;
	result->chart_count = static_cast<unsigned long long>(chartCount);
	result->dir_count = static_cast<unsigned long long>(dirCount);
	result->chart_query_hits = stats.chartQuery.hitCount;
	result->audio_query_hits = stats.audioQuery.hitCount;
	result->image_query_hits = stats.imageQuery.hitCount;
	result->movie_query_hits = stats.movieQuery.hitCount;
	result->chart_query_ms = stats.chartQuery.elapsedMs;
	result->audio_query_ms = stats.audioQuery.elapsedMs;
	result->image_query_ms = stats.imageQuery.elapsedMs;
	result->movie_query_ms = stats.movieQuery.elapsedMs;
	result->assign_ms = stats.assignMs;
	result->dedupe_ms = stats.dedupeMs;
	result->pack_ms = stats.packMs;
	result->pack_reverse_build_ms = reverseBuildMs;
	result->audio_reverse_build_ms = audioReverseBuildMs;
	result->image_reverse_build_ms = imageReverseBuildMs;
	result->movie_reverse_build_ms = movieReverseBuildMs;
	result->pack_layout_ms = layoutMs;
	result->pack_alloc_ms = allocMs;
	result->pack_write_ms = writeMs;
	result->reverse_index_bytes = reverseIndexBytes;
	result->chart_search_ms = stats.chartQuery.searchMs;
	result->chart_read_ms = stats.chartQuery.readMs;
	result->audio_search_ms = stats.audioQuery.searchMs;
	result->audio_read_ms = stats.audioQuery.readMs;
	result->image_search_ms = stats.imageQuery.searchMs;
	result->image_read_ms = stats.imageQuery.readMs;
	result->movie_search_ms = stats.movieQuery.searchMs;
	result->movie_read_ms = stats.movieQuery.readMs;
	result->chart_sdk_read_ms = stats.chartQuery.sdkReadMs;
	result->chart_callback_ms = stats.chartQuery.callbackMs;
	result->audio_sdk_read_ms = stats.audioQuery.sdkReadMs;
	result->audio_callback_ms = stats.audioQuery.callbackMs;
	result->image_sdk_read_ms = stats.imageQuery.sdkReadMs;
	result->image_callback_ms = stats.imageQuery.callbackMs;
	result->movie_sdk_read_ms = stats.movieQuery.sdkReadMs;
	result->movie_callback_ms = stats.movieQuery.callbackMs;
	result->chart_path_resize_count = stats.chartQuery.pathResizeCount;
	result->chart_name_resize_count = stats.chartQuery.nameResizeCount;
	result->audio_path_resize_count = stats.audioQuery.pathResizeCount;
	result->audio_name_resize_count = stats.audioQuery.nameResizeCount;
	result->image_path_resize_count = stats.imageQuery.pathResizeCount;
	result->image_name_resize_count = stats.imageQuery.nameResizeCount;
	result->movie_path_resize_count = stats.movieQuery.pathResizeCount;
	result->movie_name_resize_count = stats.movieQuery.nameResizeCount;
	result->chart_directory_count = static_cast<unsigned long long>(dirCount);
	result->audio_assigned_count = stats.audioAssignedCount;
	result->image_assigned_count = stats.imageAssignedCount;
	result->movie_assigned_count = stats.movieAssignedCount;
	result->audio_resource_key_hash_count = static_cast<unsigned long long>(audioResourceKeyHashCount);
	result->image_resource_key_hash_count = static_cast<unsigned long long>(imageResourceKeyHashCount);
	result->movie_resource_key_hash_count = static_cast<unsigned long long>(movieResourceKeyHashCount);
	result->audio_resource_dir_count = stats.audioResourceDirCount;
	result->image_resource_dir_count = stats.imageResourceDirCount;
	result->movie_resource_dir_count = stats.movieResourceDirCount;
	result->owner_cache_hit_count = stats.ownerCacheHitCount;
	result->owner_cache_miss_count = stats.ownerCacheMissCount;
	result->relative_prefix_cache_hit_count = stats.relativePrefixCacheHitCount;
	result->relative_prefix_cache_miss_count = stats.relativePrefixCacheMissCount;
	result->audio_group_ms = stats.audioGroupMs;
	result->audio_assign_ms = stats.audioAssignMs;
	result->audio_merge_ms = stats.audioMergeMs;
	result->image_group_ms = stats.imageGroupMs;
	result->image_assign_ms = stats.imageAssignMs;
	result->image_merge_ms = stats.imageMergeMs;
	result->movie_group_ms = stats.movieGroupMs;
	result->movie_assign_ms = stats.movieAssignMs;
	result->movie_merge_ms = stats.movieMergeMs;
	result->raw_buffer_size = static_cast<unsigned long long>(totalBytes);
	result->audio_relative_reverse_key_count = static_cast<unsigned long long>(audioRelativeReverse.keys.size());
	result->image_relative_reverse_key_count = static_cast<unsigned long long>(imageRelativeReverse.keys.size());
	result->movie_relative_reverse_key_count = static_cast<unsigned long long>(movieRelativeReverse.keys.size());

	*outResult = result;
	return BRIDGE_OK;
}

}  // namespace

extern "C" __declspec(dllexport) int __cdecl EBridge_ScanChartAndResources(const wchar_t* chartQuery, const wchar_t* audioQuery, const wchar_t* imageQuery, const wchar_t* movieQuery, EBridgeResult** outResult) {
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}
	*outResult = nullptr;
	if (!chartQuery || !audioQuery || !imageQuery || !movieQuery || chartQuery[0] == L'\0' || audioQuery[0] == L'\0' || imageQuery[0] == L'\0' || movieQuery[0] == L'\0') {
		return BRIDGE_INVALID_ARGUMENT;
	}
	if (!EnsureEverythingApiLoaded()) {
		return BRIDGE_LOAD_API_FAILED;
	}

	ScanAggregate aggregate;
	BridgeExecutionStats stats;
	ResourceAssignmentContext assignmentContext;
	ChartRawHits chartRawHits;
	CategoryRawHits audioRawHits;
	CategoryRawHits imageRawHits;
	CategoryRawHits movieRawHits;
	QueryExecutionStats chartQueryStats;
	QueryExecutionStats audioQueryStats;
	QueryExecutionStats imageQueryStats;
	QueryExecutionStats movieQueryStats;
	bool okChart = false;
	bool okAudio = false;
	bool okImage = false;
	bool okMovie = false;
	std::thread chartQueryWorker([&]() {
		okChart = ExecuteQueryWithNewClient(chartQuery, [&chartRawHits](std::wstring& path, std::wstring& name) {
			if (path.empty() || name.empty()) {
				return;
			}
			chartRawHits.files.emplace_back(std::move(path), std::move(name));
		}, &chartQueryStats, [&chartRawHits](size_t hitCount) {
			chartRawHits.files.reserve(hitCount);
		}, true);
	});
	std::thread audioQueryWorker([&]() {
		okAudio = ExecuteQueryWithNewClient(audioQuery, [&audioRawHits](std::wstring& path, std::wstring& name) {
			if (path.empty() || name.empty()) {
				return;
			}
			AppendCategoryRawHit(audioRawHits, std::move(path), std::move(name), 256);
		}, &audioQueryStats, [&audioRawHits](size_t hitCount) {
			ReserveCategoryRawHits(audioRawHits, hitCount);
		}, true);
	});
	std::thread imageQueryWorker([&]() {
		okImage = ExecuteQueryWithNewClient(imageQuery, [&imageRawHits](std::wstring& path, std::wstring& name) {
			if (path.empty() || name.empty()) {
				return;
			}
			AppendCategoryRawHit(imageRawHits, std::move(path), std::move(name), 64);
		}, &imageQueryStats, [&imageRawHits](size_t hitCount) {
			ReserveCategoryRawHits(imageRawHits, hitCount);
		}, true);
	});
	std::thread movieQueryWorker([&]() {
		okMovie = ExecuteQueryWithNewClient(movieQuery, [&movieRawHits](std::wstring& path, std::wstring& name) {
			if (path.empty() || name.empty()) {
				return;
			}
			AppendCategoryRawHit(movieRawHits, std::move(path), std::move(name), 4);
		}, &movieQueryStats, [&movieRawHits](size_t hitCount) {
			ReserveCategoryRawHits(movieRawHits, hitCount);
		}, true);
	});
	chartQueryWorker.join();
	audioQueryWorker.join();
	imageQueryWorker.join();
	movieQueryWorker.join();
	stats.chartQuery = chartQueryStats;
	stats.audioQuery = audioQueryStats;
	stats.imageQuery = imageQueryStats;
	stats.movieQuery = movieQueryStats;
	if (!okChart) {
		return BRIDGE_CHART_QUERY_FAILED;
	}
	if (!okAudio) {
		return BRIDGE_AUDIO_QUERY_FAILED;
	}
	if (!okImage) {
		return BRIDGE_IMAGE_QUERY_FAILED;
	}
	if (!okMovie) {
		return BRIDGE_MOVIE_QUERY_FAILED;
	}
	for (const auto& hit : chartRawHits.files) {
		const std::wstring& chartDirectory = hit.first;
		std::wstring chartPath = CombinePathAndName(chartDirectory, hit.second);
		aggregate.chartPaths.push_back(chartPath);
		EnsureChartDirectory(aggregate, chartDirectory);
	}
	ProcessResourceCategory(aggregate, assignmentContext, stats, ResourceCategory::Audio, std::move(audioRawHits));
	ProcessResourceCategory(aggregate, assignmentContext, stats, ResourceCategory::Image, std::move(imageRawHits));
	ProcessResourceCategory(aggregate, assignmentContext, stats, ResourceCategory::Movie, std::move(movieRawHits));
	stats.assignMs = stats.audioAssignMs + stats.imageAssignMs + stats.movieAssignMs;

	auto dedupeStartedAt = std::chrono::steady_clock::now();
	DedupeAggregate(aggregate);
	stats.dedupeMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - dedupeStartedAt).count();
	auto packStartedAt = std::chrono::steady_clock::now();
	int buildResult = BuildResultBuffer(aggregate, stats, outResult);
	stats.packMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - packStartedAt).count();
	if (buildResult == BRIDGE_OK && *outResult != nullptr) {
		(*outResult)->pack_ms = stats.packMs;
	}
	return buildResult;
}

extern "C" __declspec(dllexport) int __cdecl EBridge_ScanSourceRoots(
	const EBridgeSourceRootRequest* roots,
	unsigned int rootCount,
	const wchar_t* chartQuery,
	const wchar_t* audioQuery,
	const wchar_t* imageQuery,
	const wchar_t* movieQuery,
	EBridgeSourceRootsResult** outResult)
{
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}
	*outResult = nullptr;
	if (!roots || rootCount == 0u || !chartQuery || !audioQuery || !imageQuery || !movieQuery
		|| chartQuery[0] == L'\0' || audioQuery[0] == L'\0' || imageQuery[0] == L'\0' || movieQuery[0] == L'\0') {
		return BRIDGE_INVALID_ARGUMENT;
	}
	if (!EnsureEverythingApiLoaded()) {
		return BRIDGE_LOAD_API_FAILED;
	}

	std::vector<SourceRootAggregate> sourceRoots;
	sourceRoots.reserve(rootCount);
	for (unsigned int i = 0; i < rootCount; i++) {
		if (!roots[i].root_path || roots[i].root_path[0] == L'\0') {
			return BRIDGE_INVALID_ARGUMENT;
		}
		SourceRootAggregate aggregate;
		aggregate.rootId = roots[i].root_id;
		aggregate.rootPath = TrimTrailingSeparators(ReplaceAltSeparators(roots[i].root_path));
		if (aggregate.rootPath.empty()) {
			return BRIDGE_INVALID_ARGUMENT;
		}
		sourceRoots.push_back(std::move(aggregate));
	}

	unsigned int connectError = EVERYTHING3_OK;
	void* client = TryConnectClient(&connectError);
	if (!client) {
		return BRIDGE_CONNECT_FAILED;
	}

	BridgeExecutionStats stats;
	bool okChart = ExecuteQuery(client, chartQuery, [&sourceRoots](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		std::wstring fullPath = CombinePathAndName(TrimTrailingSeparators(ReplaceAltSeparators(path)), name);
		int rootIndex = FindBestMatchingRootIndex(fullPath, sourceRoots);
		if (rootIndex < 0) {
			return;
		}
		sourceRoots[static_cast<size_t>(rootIndex)].chartPaths.push_back(fullPath);
		sourceRoots[static_cast<size_t>(rootIndex)].trackedPaths.insert(fullPath);
	}, &stats.chartQuery);
	if (!okChart) {
		g_api.DestroyClient(client);
		return BRIDGE_CHART_QUERY_FAILED;
	}

	bool okAudio = ExecuteQuery(client, audioQuery, [&sourceRoots](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		std::wstring fullPath = CombinePathAndName(TrimTrailingSeparators(ReplaceAltSeparators(path)), name);
		int rootIndex = FindBestMatchingRootIndex(fullPath, sourceRoots);
		if (rootIndex >= 0) {
			sourceRoots[static_cast<size_t>(rootIndex)].audioPaths.push_back(fullPath);
		}
	}, &stats.audioQuery);
	if (!okAudio) {
		g_api.DestroyClient(client);
		return BRIDGE_AUDIO_QUERY_FAILED;
	}

	bool okImage = ExecuteQuery(client, imageQuery, [&sourceRoots](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		std::wstring fullPath = CombinePathAndName(TrimTrailingSeparators(ReplaceAltSeparators(path)), name);
		int rootIndex = FindBestMatchingRootIndex(fullPath, sourceRoots);
		if (rootIndex >= 0) {
			sourceRoots[static_cast<size_t>(rootIndex)].imagePaths.push_back(fullPath);
		}
	}, &stats.imageQuery);
	if (!okImage) {
		g_api.DestroyClient(client);
		return BRIDGE_IMAGE_QUERY_FAILED;
	}

	bool okMovie = ExecuteQuery(client, movieQuery, [&sourceRoots](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		std::wstring fullPath = CombinePathAndName(TrimTrailingSeparators(ReplaceAltSeparators(path)), name);
		int rootIndex = FindBestMatchingRootIndex(fullPath, sourceRoots);
		if (rootIndex >= 0) {
			sourceRoots[static_cast<size_t>(rootIndex)].moviePaths.push_back(fullPath);
		}
	}, &stats.movieQuery);
	g_api.DestroyClient(client);
	if (!okMovie) {
		return BRIDGE_MOVIE_QUERY_FAILED;
	}

	auto assignStartedAt = std::chrono::steady_clock::now();
	for (SourceRootAggregate& root : sourceRoots) {
		ProcessSourceRootResourcePaths(root.rootPath, root.audioPaths, root.audioResourceKeyHashes, root.resourceTrackedPaths, root.trackedPaths);
		ProcessSourceRootResourcePaths(root.rootPath, root.imagePaths, root.imageResourceKeyHashes, root.resourceTrackedPaths, root.trackedPaths);
		ProcessSourceRootResourcePaths(root.rootPath, root.moviePaths, root.movieResourceKeyHashes, root.resourceTrackedPaths, root.trackedPaths);
	}
	stats.assignMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - assignStartedAt).count();

	auto dedupeStartedAt = std::chrono::steady_clock::now();
	for (SourceRootAggregate& root : sourceRoots) {
		DedupeSourceRootAggregate(root);
	}
	stats.dedupeMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - dedupeStartedAt).count();

	auto packStartedAt = std::chrono::steady_clock::now();
	int buildResult = BuildSourceRootsResultBuffer(sourceRoots, stats, outResult);
	stats.packMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - packStartedAt).count();
	if (buildResult == BRIDGE_OK && *outResult != nullptr) {
		(*outResult)->pack_ms = stats.packMs;
	}
	return buildResult;
}

extern "C" __declspec(dllexport) int __cdecl EBridge_EnumerateGroupedFiles(const EBridgeGroupedQuery* queries, unsigned int queryCount, EBridgeGroupedFilesResult** outResult) {
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}
	*outResult = nullptr;
	if (!queries || queryCount == 0u) {
		return BRIDGE_INVALID_ARGUMENT;
	}
	if (!EnsureEverythingApiLoaded()) {
		return BRIDGE_LOAD_API_FAILED;
	}

	unsigned int connectError = EVERYTHING3_OK;
	void* client = TryConnectClient(&connectError);
	if (!client) {
		return BRIDGE_CONNECT_FAILED;
	}

	std::vector<GroupedQueryResult> groupedResults;
	groupedResults.reserve(queryCount);
	auto startedAt = std::chrono::steady_clock::now();
	for (unsigned int i = 0; i < queryCount; i++) {
		const EBridgeGroupedQuery& query = queries[i];
		if (!query.query_text || query.query_text[0] == L'\0') {
			g_api.DestroyClient(client);
			return BRIDGE_INVALID_ARGUMENT;
		}

		GroupedQueryResult groupedResult;
		groupedResult.groupId = query.group_id;
		bool ok = ExecuteQuery(client, query.query_text, [&groupedResult](const std::wstring& path, const std::wstring& name) {
			if (path.empty() || name.empty()) {
				return;
			}
			groupedResult.fullPaths.push_back(CombinePathAndName(TrimTrailingSeparators(ReplaceAltSeparators(path)), name));
		}, &groupedResult.stats);
		if (!ok) {
			g_api.DestroyClient(client);
			return BRIDGE_GROUPED_QUERY_FAILED;
		}
		DedupePaths(groupedResult.fullPaths);
		groupedResults.push_back(std::move(groupedResult));
	}
	g_api.DestroyClient(client);

	long long enumerationMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - startedAt).count();
	return BuildGroupedFilesResultBuffer(groupedResults, enumerationMs, outResult);
}

extern "C" __declspec(dllexport) void __cdecl EBridge_FreeResult(EBridgeResult* result) {
	if (!result) {
		return;
	}
	std::free(result);
}

extern "C" __declspec(dllexport) void __cdecl EBridge_FreeSourceRootsResult(EBridgeSourceRootsResult* result) {
	if (!result) {
		return;
	}
	std::free(result);
}

extern "C" __declspec(dllexport) void __cdecl EBridge_FreeGroupedFilesResult(EBridgeGroupedFilesResult* result) {
	if (!result) {
		return;
	}
	std::free(result);
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID) {
	if (fdwReason == DLL_PROCESS_ATTACH) {
		g_bridgeModule = hinstDLL;
	}
	return TRUE;
}
