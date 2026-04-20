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
	int status;
	int error_code;
	unsigned long long chart_count;
	unsigned int* chart_offsets;
	wchar_t* chart_blob;
	unsigned long long dir_count;
	unsigned int* dir_offsets;
	wchar_t* dir_blob;
	unsigned int* all_hash_offsets;
	unsigned int* all_hash_lengths;
	unsigned char* all_hashes_blob;
	unsigned int* audio_base_hash_offsets;
	unsigned int* audio_base_hash_lengths;
	unsigned char* audio_base_hashes_blob;
	unsigned int* image_base_hash_offsets;
	unsigned int* image_base_hash_lengths;
	unsigned char* image_base_hashes_blob;
	unsigned int* movie_base_hash_offsets;
	unsigned int* movie_base_hash_lengths;
	unsigned char* movie_base_hashes_blob;
	unsigned int* audio_relative_hash_offsets;
	unsigned int* audio_relative_hash_lengths;
	unsigned char* audio_relative_hashes_blob;
	unsigned int* image_relative_hash_offsets;
	unsigned int* image_relative_hash_lengths;
	unsigned char* image_relative_hashes_blob;
	unsigned int* movie_relative_hash_offsets;
	unsigned int* movie_relative_hash_lengths;
	unsigned char* movie_relative_hashes_blob;
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
	unsigned long long all_base_hash_count;
	unsigned long long audio_base_hash_count;
	unsigned long long image_base_hash_count;
	unsigned long long movie_base_hash_count;
	unsigned long long audio_relative_hash_count;
	unsigned long long image_relative_hash_count;
	unsigned long long movie_relative_hash_count;
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
};

__declspec(dllexport) int __cdecl EBridge_ScanChartAndResources(const wchar_t* chartQuery, const wchar_t* audioQuery, const wchar_t* imageQuery, const wchar_t* movieQuery, EBridgeResult** outResult);
__declspec(dllexport) void __cdecl EBridge_FreeResult(EBridgeResult* result);
}

namespace {

constexpr unsigned int EVERYTHING3_OK = 0;
constexpr unsigned int EVERYTHING3_ERROR_IPC_PIPE_NOT_FOUND = 0xE0000002u;
constexpr unsigned int EVERYTHING3_PROPERTY_ID_NAME = 0u;
constexpr unsigned int EVERYTHING3_PROPERTY_ID_PATH = 1u;

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
	BRIDGE_INTERNAL_ERROR = 9
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
};

struct ScanAggregate {
	std::vector<std::wstring> chartPaths;
	std::vector<std::wstring> chartDirectories;
	std::unordered_map<std::wstring, uint32_t> chartDirIndex;
	std::vector<std::vector<uint32_t>> allBaseHashes;
	std::vector<std::vector<uint32_t>> audioBaseHashes;
	std::vector<std::vector<uint32_t>> imageBaseHashes;
	std::vector<std::vector<uint32_t>> movieBaseHashes;
	std::vector<std::vector<uint32_t>> audioRelativeHashes;
	std::vector<std::vector<uint32_t>> imageRelativeHashes;
	std::vector<std::vector<uint32_t>> movieRelativeHashes;
};

struct QueryExecutionStats {
	unsigned long long hitCount = 0;
	long long elapsedMs = 0;
};

struct BridgeExecutionStats {
	QueryExecutionStats chartQuery;
	QueryExecutionStats audioQuery;
	QueryExecutionStats imageQuery;
	QueryExecutionStats movieQuery;
	long long assignMs = 0;
	long long dedupeMs = 0;
	long long packMs = 0;
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

struct ResourceDirectoryInfo {
	bool initialized = false;
	std::wstring ownerDirectory;
	std::wstring relativePrefix;
	uint32_t chartDirIndex = 0;
};

struct ResourceAssignmentContext {
	std::unordered_map<std::wstring, ResourceDirectoryInfo> infosByResourceDirectory;
};

struct CategoryRawHits {
	std::unordered_map<std::wstring, std::vector<std::wstring>> fileNamesByDirectory;
};

struct WorkerCategoryBuffers {
	std::unordered_map<uint32_t, std::vector<uint32_t>> allBaseHashesByChartIndex;
	std::unordered_map<uint32_t, std::vector<uint32_t>> categoryBaseHashesByChartIndex;
	std::unordered_map<uint32_t, std::vector<uint32_t>> categoryRelativeHashesByChartIndex;
	unsigned long long assignedCount = 0;
};

struct CategoryProcessingMetrics {
	unsigned long long resourceDirCount = 0;
	unsigned long long assignedCount = 0;
	long long groupMs = 0;
	long long assignMs = 0;
	long long mergeMs = 0;
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

bool GetResultPath(void* resultList, size_t index, std::vector<wchar_t>& buffer, std::wstring& out) {
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
	}
	return false;
}

bool GetResultName(void* resultList, size_t index, std::vector<wchar_t>& buffer, std::wstring& out) {
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

std::wstring NormalizeFileNameForLookup(const std::wstring& fileName) {
	if (fileName.empty()) {
		return std::wstring();
	}
	std::wstring normalized = ReplaceAltSeparators(fileName);
	size_t pos = normalized.find_last_of(L'\\');
	if (pos != std::wstring::npos) {
		normalized = normalized.substr(pos + 1);
	}
	return NormalizeExtensionAlias(normalized);
}

std::wstring NormalizeLookupFileNameFast(const std::wstring& fileName) {
	return NormalizeFileNameForLookup(fileName);
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

template <typename Callback>
bool ExecuteQuery(void* client, const wchar_t* query, Callback&& onResult, QueryExecutionStats* stats = nullptr) {
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
		g_api.AddSearchPropertyRequest(state, EVERYTHING3_PROPERTY_ID_PATH);
		g_api.AddSearchPropertyRequest(state, EVERYTHING3_PROPERTY_ID_NAME);
		g_api.SetSearchViewportOffset(state, 0);
		g_api.SetSearchViewportCount(state, static_cast<size_t>(-1));
		result = g_api.Search(client, state);
		if (!result || g_api.GetLastError() != EVERYTHING3_OK) {
			break;
		}
		size_t hitCount = g_api.GetResultListViewportCount(result);
		if (stats) {
			stats->hitCount = static_cast<unsigned long long>(hitCount);
		}
		std::vector<wchar_t> pathBuffer(1024);
		std::vector<wchar_t> nameBuffer(512);
		std::wstring path;
		std::wstring name;
		for (size_t i = 0; i < hitCount; i++) {
			if (!GetResultPath(result, i, pathBuffer, path) || !GetResultName(result, i, nameBuffer, name)) {
				continue;
			}
			onResult(path, name);
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

uint32_t EnsureChartDirectory(ScanAggregate& aggregate, const std::wstring& chartDirectory) {
	auto it = aggregate.chartDirIndex.find(chartDirectory);
	if (it != aggregate.chartDirIndex.end()) {
		return it->second;
	}
	uint32_t index = static_cast<uint32_t>(aggregate.chartDirectories.size());
	aggregate.chartDirectories.push_back(chartDirectory);
	aggregate.chartDirIndex.emplace(chartDirectory, index);
	aggregate.allBaseHashes.emplace_back();
	aggregate.audioBaseHashes.emplace_back();
	aggregate.imageBaseHashes.emplace_back();
	aggregate.movieBaseHashes.emplace_back();
	aggregate.audioRelativeHashes.emplace_back();
	aggregate.imageRelativeHashes.emplace_back();
	aggregate.movieRelativeHashes.emplace_back();
	return index;
}

std::wstring FindOwningChartDirectory(const ScanAggregate& aggregate, const std::wstring& resourceDirectory) {
	std::wstring current = TrimTrailingSeparators(resourceDirectory);
	while (!current.empty()) {
		if (aggregate.chartDirIndex.find(current) != aggregate.chartDirIndex.end()) {
			return current;
		}
		current = GetParentDirectory(current);
	}
	return std::wstring();
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
	if (desired > 8u) {
		desired = 8u;
	}
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
		return !outInfo.ownerDirectory.empty();
	}

	ResourceDirectoryInfo info;
	info.initialized = true;
	info.ownerDirectory = FindOwningChartDirectory(aggregate, resourceDirectory);
	stats.ownerCacheMissCount += 1;
	stats.relativePrefixCacheMissCount += 1;
	if (!info.ownerDirectory.empty()) {
		info.chartDirIndex = aggregate.chartDirIndex.at(info.ownerDirectory);
		info.relativePrefix = NormalizeRelativeDirectoryPrefixForLookup(info.ownerDirectory, resourceDirectory);
	}
	context.infosByResourceDirectory.emplace(resourceDirectory, info);
	outInfo = info;
	return !outInfo.ownerDirectory.empty();
}

void AppendHashVector(std::unordered_map<uint32_t, std::vector<uint32_t>>& target, uint32_t chartDirIndex, uint32_t hashValue) {
	if (hashValue == 0u) {
		return;
	}
	target[chartDirIndex].push_back(hashValue);
}

struct CategoryMergeTargets {
	std::vector<std::vector<uint32_t>>* allBaseHashes;
	std::vector<std::vector<uint32_t>>* categoryBaseHashes;
	std::vector<std::vector<uint32_t>>* categoryRelativeHashes;
};

CategoryMergeTargets GetCategoryMergeTargets(ScanAggregate& aggregate, ResourceCategory category) {
	switch (category) {
	case ResourceCategory::Audio:
		return { &aggregate.allBaseHashes, &aggregate.audioBaseHashes, &aggregate.audioRelativeHashes };
	case ResourceCategory::Image:
		return { &aggregate.allBaseHashes, &aggregate.imageBaseHashes, &aggregate.imageRelativeHashes };
	case ResourceCategory::Movie:
		return { &aggregate.allBaseHashes, &aggregate.movieBaseHashes, &aggregate.movieRelativeHashes };
	default:
		return { &aggregate.allBaseHashes, &aggregate.audioBaseHashes, &aggregate.audioRelativeHashes };
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
			for (size_t i = startIndex; i < endIndex; i++) {
				const ResourceDirectoryInfo& info = workItems[i].first;
				const std::wstring& relativePrefix = info.relativePrefix;
				for (const std::wstring& fileName : workItems[i].second) {
					std::wstring normalizedBaseName = NormalizeLookupFileNameFast(fileName);
					if (normalizedBaseName.empty()) {
						continue;
					}
					std::wstring normalizedRelativePath = NormalizeRelativeLookupPathFast(relativePrefix, normalizedBaseName);
					if (normalizedRelativePath.empty()) {
						continue;
					}
					uint32_t baseHash = GetLookupHashFast(normalizedBaseName);
					uint32_t relativeHash = GetLookupHashFast(normalizedRelativePath);
					AppendHashVector(buffers.allBaseHashesByChartIndex, info.chartDirIndex, baseHash);
					AppendHashVector(buffers.categoryBaseHashesByChartIndex, info.chartDirIndex, baseHash);
					AppendHashVector(buffers.categoryRelativeHashesByChartIndex, info.chartDirIndex, relativeHash);
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
		for (const auto& item : buffers.allBaseHashesByChartIndex) {
			auto& destination = (*mergeTargets.allBaseHashes)[item.first];
			destination.insert(destination.end(), item.second.begin(), item.second.end());
		}
		for (const auto& item : buffers.categoryBaseHashesByChartIndex) {
			auto& destination = (*mergeTargets.categoryBaseHashes)[item.first];
			destination.insert(destination.end(), item.second.begin(), item.second.end());
		}
		for (const auto& item : buffers.categoryRelativeHashesByChartIndex) {
			auto& destination = (*mergeTargets.categoryRelativeHashes)[item.first];
			destination.insert(destination.end(), item.second.begin(), item.second.end());
		}
	}
	metrics.mergeMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - mergeStartedAt).count();
	ApplyCategoryMetrics(stats, category, metrics);
}

void DedupeAggregate(ScanAggregate& aggregate) {
	std::sort(aggregate.chartPaths.begin(), aggregate.chartPaths.end());
	aggregate.chartPaths.erase(std::unique(aggregate.chartPaths.begin(), aggregate.chartPaths.end()), aggregate.chartPaths.end());
	for (auto* hashes : { &aggregate.allBaseHashes, &aggregate.audioBaseHashes, &aggregate.imageBaseHashes, &aggregate.movieBaseHashes, &aggregate.audioRelativeHashes, &aggregate.imageRelativeHashes, &aggregate.movieRelativeHashes }) {
		for (auto& vector : *hashes) {
			std::sort(vector.begin(), vector.end());
			vector.erase(std::unique(vector.begin(), vector.end()), vector.end());
		}
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

int BuildResultBuffer(const ScanAggregate& aggregate, const BridgeExecutionStats& stats, EBridgeResult** outResult) {
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}

	size_t chartCount = aggregate.chartPaths.size();
	size_t dirCount = aggregate.chartDirectories.size();

	size_t chartBlobChars = SumBlobChars(aggregate.chartPaths);
	size_t dirBlobChars = SumBlobChars(aggregate.chartDirectories);
	size_t allHashCount = SumHashCount(aggregate.allBaseHashes);
	size_t audioBaseHashCount = SumHashCount(aggregate.audioBaseHashes);
	size_t imageBaseHashCount = SumHashCount(aggregate.imageBaseHashes);
	size_t movieBaseHashCount = SumHashCount(aggregate.movieBaseHashes);
	size_t audioRelativeHashCount = SumHashCount(aggregate.audioRelativeHashes);
	size_t imageRelativeHashCount = SumHashCount(aggregate.imageRelativeHashes);
	size_t movieRelativeHashCount = SumHashCount(aggregate.movieRelativeHashes);

	size_t cursor = AlignUp(sizeof(EBridgeResult), 8);
	size_t chartOffsetsPos = cursor; cursor += chartCount * sizeof(uint32_t);
	size_t dirOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);

	size_t allOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t allLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t audioBaseOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t audioBaseLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t imageBaseOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t imageBaseLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t movieBaseOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t movieBaseLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t audioRelativeOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t audioRelativeLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t imageRelativeOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t imageRelativeLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t movieRelativeOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t movieRelativeLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);

	cursor = AlignUp(cursor, alignof(wchar_t));
	size_t chartBlobPos = cursor; cursor += chartBlobChars * sizeof(wchar_t);
	size_t dirBlobPos = cursor; cursor += dirBlobChars * sizeof(wchar_t);

	cursor = AlignUp(cursor, alignof(uint32_t));
	size_t allHashesPos = cursor; cursor += allHashCount * sizeof(uint32_t);
	size_t audioBaseHashesPos = cursor; cursor += audioBaseHashCount * sizeof(uint32_t);
	size_t imageBaseHashesPos = cursor; cursor += imageBaseHashCount * sizeof(uint32_t);
	size_t movieBaseHashesPos = cursor; cursor += movieBaseHashCount * sizeof(uint32_t);
	size_t audioRelativeHashesPos = cursor; cursor += audioRelativeHashCount * sizeof(uint32_t);
	size_t imageRelativeHashesPos = cursor; cursor += imageRelativeHashCount * sizeof(uint32_t);
	size_t movieRelativeHashesPos = cursor; cursor += movieRelativeHashCount * sizeof(uint32_t);
	size_t totalBytes = cursor;

	uint8_t* raw = reinterpret_cast<uint8_t*>(std::malloc(totalBytes));
	if (!raw) {
		return BRIDGE_OUT_OF_MEMORY;
	}
	std::memset(raw, 0, totalBytes);

	auto* result = reinterpret_cast<EBridgeResult*>(raw);
	WriteStringBlob(raw, chartOffsetsPos, chartBlobPos, aggregate.chartPaths, result->chart_offsets, result->chart_blob);
	WriteStringBlob(raw, dirOffsetsPos, dirBlobPos, aggregate.chartDirectories, result->dir_offsets, result->dir_blob);
	WriteHashGroup(raw, allOffsetsPos, allLengthsPos, allHashesPos, aggregate.allBaseHashes, result->all_hash_offsets, result->all_hash_lengths, result->all_hashes_blob);
	WriteHashGroup(raw, audioBaseOffsetsPos, audioBaseLengthsPos, audioBaseHashesPos, aggregate.audioBaseHashes, result->audio_base_hash_offsets, result->audio_base_hash_lengths, result->audio_base_hashes_blob);
	WriteHashGroup(raw, imageBaseOffsetsPos, imageBaseLengthsPos, imageBaseHashesPos, aggregate.imageBaseHashes, result->image_base_hash_offsets, result->image_base_hash_lengths, result->image_base_hashes_blob);
	WriteHashGroup(raw, movieBaseOffsetsPos, movieBaseLengthsPos, movieBaseHashesPos, aggregate.movieBaseHashes, result->movie_base_hash_offsets, result->movie_base_hash_lengths, result->movie_base_hashes_blob);
	WriteHashGroup(raw, audioRelativeOffsetsPos, audioRelativeLengthsPos, audioRelativeHashesPos, aggregate.audioRelativeHashes, result->audio_relative_hash_offsets, result->audio_relative_hash_lengths, result->audio_relative_hashes_blob);
	WriteHashGroup(raw, imageRelativeOffsetsPos, imageRelativeLengthsPos, imageRelativeHashesPos, aggregate.imageRelativeHashes, result->image_relative_hash_offsets, result->image_relative_hash_lengths, result->image_relative_hashes_blob);
	WriteHashGroup(raw, movieRelativeOffsetsPos, movieRelativeLengthsPos, movieRelativeHashesPos, aggregate.movieRelativeHashes, result->movie_relative_hash_offsets, result->movie_relative_hash_lengths, result->movie_relative_hashes_blob);

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
	result->chart_directory_count = static_cast<unsigned long long>(dirCount);
	result->audio_assigned_count = stats.audioAssignedCount;
	result->image_assigned_count = stats.imageAssignedCount;
	result->movie_assigned_count = stats.movieAssignedCount;
	result->all_base_hash_count = static_cast<unsigned long long>(allHashCount);
	result->audio_base_hash_count = static_cast<unsigned long long>(audioBaseHashCount);
	result->image_base_hash_count = static_cast<unsigned long long>(imageBaseHashCount);
	result->movie_base_hash_count = static_cast<unsigned long long>(movieBaseHashCount);
	result->audio_relative_hash_count = static_cast<unsigned long long>(audioRelativeHashCount);
	result->image_relative_hash_count = static_cast<unsigned long long>(imageRelativeHashCount);
	result->movie_relative_hash_count = static_cast<unsigned long long>(movieRelativeHashCount);
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

	unsigned int connectError = EVERYTHING3_OK;
	void* client = TryConnectClient(&connectError);
	if (!client) {
		return BRIDGE_CONNECT_FAILED;
	}

	ScanAggregate aggregate;
	BridgeExecutionStats stats;
	ResourceAssignmentContext assignmentContext;
	CategoryRawHits audioRawHits;
	CategoryRawHits imageRawHits;
	CategoryRawHits movieRawHits;
	bool okChart = ExecuteQuery(client, chartQuery, [&aggregate](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		std::wstring chartDirectory = TrimTrailingSeparators(ReplaceAltSeparators(path));
		std::wstring chartPath = CombinePathAndName(chartDirectory, name);
		aggregate.chartPaths.push_back(chartPath);
		EnsureChartDirectory(aggregate, chartDirectory);
	}, &stats.chartQuery);
	if (!okChart) {
		g_api.DestroyClient(client);
		return BRIDGE_CHART_QUERY_FAILED;
	}

	bool okAudio = ExecuteQuery(client, audioQuery, [&audioRawHits](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		audioRawHits.fileNamesByDirectory[TrimTrailingSeparators(ReplaceAltSeparators(path))].push_back(name);
	}, &stats.audioQuery);
	if (!okAudio) {
		g_api.DestroyClient(client);
		return BRIDGE_AUDIO_QUERY_FAILED;
	}

	bool okImage = ExecuteQuery(client, imageQuery, [&imageRawHits](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		imageRawHits.fileNamesByDirectory[TrimTrailingSeparators(ReplaceAltSeparators(path))].push_back(name);
	}, &stats.imageQuery);
	if (!okImage) {
		g_api.DestroyClient(client);
		return BRIDGE_IMAGE_QUERY_FAILED;
	}

	bool okMovie = ExecuteQuery(client, movieQuery, [&movieRawHits](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		movieRawHits.fileNamesByDirectory[TrimTrailingSeparators(ReplaceAltSeparators(path))].push_back(name);
	}, &stats.movieQuery);
	g_api.DestroyClient(client);
	if (!okMovie) {
		return BRIDGE_MOVIE_QUERY_FAILED;
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

extern "C" __declspec(dllexport) void __cdecl EBridge_FreeResult(EBridgeResult* result) {
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
