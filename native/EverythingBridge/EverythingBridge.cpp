#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

extern "C" {

struct EBridgeResult {
	int status;
	int error_code;
	unsigned long long bms_count;
	unsigned int* bms_offsets;
	wchar_t* bms_blob;
	unsigned long long dir_count;
	unsigned int* dir_offsets;
	wchar_t* dir_blob;
	unsigned int* dir_hash_offsets;
	unsigned int* dir_hash_lengths;
	unsigned char* hashes_blob;
	unsigned long long raw_buffer_size;
};

__declspec(dllexport) int __cdecl EBridge_Scan(const wchar_t* bmsQuery, const wchar_t* siblingQuery, EBridgeResult** outResult);
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
	BRIDGE_BMS_QUERY_FAILED = 4,
	BRIDGE_SIBLING_QUERY_FAILED = 5,
	BRIDGE_OUT_OF_MEMORY = 6,
	BRIDGE_INTERNAL_ERROR = 7
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
	std::vector<std::wstring> bmsPaths;
	std::vector<std::wstring> dirPaths;
	std::vector<std::vector<uint32_t>> dirHashes;
	std::unordered_map<std::wstring, uint32_t> dirIndex;
};

HMODULE g_bridgeModule = nullptr;
EverythingApi g_api;
bool g_apiLoaded = false;

template <typename T>
T LoadProc(HMODULE module, const char* name) {
	auto proc = reinterpret_cast<T>(GetProcAddress(module, name));
	return proc;
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
	if (pos == std::wstring::npos) {
		return L"";
	}
	return path.substr(0, pos + 1);
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

std::wstring NormalizeExtensionForHash(const std::wstring& fileName) {
	std::wstring upper = ToUpperInvariant(fileName);
	size_t len = upper.size();
	if (len > 3 && upper[len - 4] == L'.') {
		wchar_t c1 = upper[len - 3];
		wchar_t c2 = upper[len - 2];
		wchar_t c3 = upper[len - 1];
		if ((c1 == L'O' && c2 == L'G' && c3 == L'G') || (c1 == L'M' && c2 == L'P' && c3 == L'3')) {
			upper[len - 3] = L'W';
			upper[len - 2] = L'A';
			upper[len - 1] = L'V';
		} else if ((c1 == L'B' && c2 == L'M' && c3 == L'P') || (c1 == L'J' && c2 == L'P' && c3 == L'G')) {
			upper[len - 3] = L'P';
			upper[len - 2] = L'N';
			upper[len - 1] = L'G';
		}
	}
	return upper;
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

uint32_t GetNormalizedFileNameHash(const std::wstring& fileName) {
	std::wstring normalized = NormalizeExtensionForHash(fileName);
	const uint8_t* bytes = reinterpret_cast<const uint8_t*>(normalized.data());
	size_t byteLen = normalized.size() * sizeof(wchar_t);
	return CalcXxHash32(bytes, byteLen, 0u);
}

template <typename Callback>
bool ExecuteQuery(void* client, const wchar_t* query, Callback&& onResult, uint64_t* outHitCount) {
	if (outHitCount) {
		*outHitCount = 0;
	}
	void* state = g_api.CreateSearchState();
	if (!state) {
		return false;
	}

	void* result = nullptr;
	bool ok = false;
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
		if (!result) {
			break;
		}
		if (g_api.GetLastError() != EVERYTHING3_OK) {
			break;
		}

		size_t hitCount = g_api.GetResultListViewportCount(result);
		if (outHitCount) {
			*outHitCount = static_cast<uint64_t>(hitCount);
		}
		std::vector<wchar_t> pathBuffer(1024);
		std::vector<wchar_t> nameBuffer(512);
		std::wstring path;
		std::wstring name;
		for (size_t i = 0; i < hitCount; i++) {
			if (!GetResultPath(result, i, pathBuffer, path)) {
				continue;
			}
			if (!GetResultName(result, i, nameBuffer, name)) {
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
	return ok;
}

size_t AlignUp(size_t value, size_t align) {
	return (value + (align - 1)) & ~(align - 1);
}

int BuildResultBuffer(const ScanAggregate& aggregate, EBridgeResult** outResult) {
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}

	size_t bmsCount = aggregate.bmsPaths.size();
	size_t dirCount = aggregate.dirPaths.size();

	size_t bmsBlobChars = 0;
	for (const auto& s : aggregate.bmsPaths) {
		bmsBlobChars += s.size() + 1;
	}
	size_t dirBlobChars = 0;
	for (const auto& s : aggregate.dirPaths) {
		dirBlobChars += s.size() + 1;
	}
	size_t totalHashes = 0;
	for (const auto& hashes : aggregate.dirHashes) {
		totalHashes += hashes.size();
	}

	size_t cursor = AlignUp(sizeof(EBridgeResult), 8);
	size_t bmsOffsetsPos = cursor; cursor += bmsCount * sizeof(uint32_t);
	size_t dirOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t dirHashOffsetsPos = cursor; cursor += dirCount * sizeof(uint32_t);
	size_t dirHashLengthsPos = cursor; cursor += dirCount * sizeof(uint32_t);

	cursor = AlignUp(cursor, alignof(wchar_t));
	size_t bmsBlobPos = cursor; cursor += bmsBlobChars * sizeof(wchar_t);
	size_t dirBlobPos = cursor; cursor += dirBlobChars * sizeof(wchar_t);

	cursor = AlignUp(cursor, alignof(uint32_t));
	size_t hashesPos = cursor; cursor += totalHashes * sizeof(uint32_t);
	size_t totalBytes = cursor;

	uint8_t* raw = reinterpret_cast<uint8_t*>(std::malloc(totalBytes));
	if (!raw) {
		return BRIDGE_OUT_OF_MEMORY;
	}
	std::memset(raw, 0, totalBytes);

	auto* result = reinterpret_cast<EBridgeResult*>(raw);
	auto* bmsOffsets = reinterpret_cast<uint32_t*>(raw + bmsOffsetsPos);
	auto* dirOffsets = reinterpret_cast<uint32_t*>(raw + dirOffsetsPos);
	auto* dirHashOffsets = reinterpret_cast<uint32_t*>(raw + dirHashOffsetsPos);
	auto* dirHashLengths = reinterpret_cast<uint32_t*>(raw + dirHashLengthsPos);
	auto* bmsBlob = reinterpret_cast<wchar_t*>(raw + bmsBlobPos);
	auto* dirBlob = reinterpret_cast<wchar_t*>(raw + dirBlobPos);
	auto* hashBlob = reinterpret_cast<uint8_t*>(raw + hashesPos);

	uint32_t bmsBlobOffsetBytes = 0;
	for (size_t i = 0; i < bmsCount; i++) {
		bmsOffsets[i] = bmsBlobOffsetBytes;
		const auto& s = aggregate.bmsPaths[i];
		size_t charCount = s.size() + 1;
		std::memcpy(reinterpret_cast<uint8_t*>(bmsBlob) + bmsBlobOffsetBytes, s.c_str(), charCount * sizeof(wchar_t));
		bmsBlobOffsetBytes += static_cast<uint32_t>(charCount * sizeof(wchar_t));
	}

	uint32_t dirBlobOffsetBytes = 0;
	uint32_t hashOffsetBytes = 0;
	for (size_t i = 0; i < dirCount; i++) {
		dirOffsets[i] = dirBlobOffsetBytes;
		const auto& dir = aggregate.dirPaths[i];
		size_t charCount = dir.size() + 1;
		std::memcpy(reinterpret_cast<uint8_t*>(dirBlob) + dirBlobOffsetBytes, dir.c_str(), charCount * sizeof(wchar_t));
		dirBlobOffsetBytes += static_cast<uint32_t>(charCount * sizeof(wchar_t));

		const auto& hashes = aggregate.dirHashes[i];
		dirHashOffsets[i] = hashOffsetBytes;
		dirHashLengths[i] = static_cast<uint32_t>(hashes.size());
		size_t bytes = hashes.size() * sizeof(uint32_t);
		if (bytes > 0) {
			std::memcpy(hashBlob + hashOffsetBytes, hashes.data(), bytes);
			hashOffsetBytes += static_cast<uint32_t>(bytes);
		}
	}

	result->status = 0;
	result->error_code = 0;
	result->bms_count = static_cast<unsigned long long>(bmsCount);
	result->bms_offsets = bmsOffsets;
	result->bms_blob = bmsBlob;
	result->dir_count = static_cast<unsigned long long>(dirCount);
	result->dir_offsets = dirOffsets;
	result->dir_blob = dirBlob;
	result->dir_hash_offsets = dirHashOffsets;
	result->dir_hash_lengths = dirHashLengths;
	result->hashes_blob = hashBlob;
	result->raw_buffer_size = static_cast<unsigned long long>(totalBytes);

	*outResult = result;
	return BRIDGE_OK;
}

void DedupeAggregate(ScanAggregate& aggregate) {
	std::sort(aggregate.bmsPaths.begin(), aggregate.bmsPaths.end());
	aggregate.bmsPaths.erase(std::unique(aggregate.bmsPaths.begin(), aggregate.bmsPaths.end()), aggregate.bmsPaths.end());

	for (auto& hashes : aggregate.dirHashes) {
		std::sort(hashes.begin(), hashes.end());
		hashes.erase(std::unique(hashes.begin(), hashes.end()), hashes.end());
	}
}

}  // namespace

extern "C" __declspec(dllexport) int __cdecl EBridge_Scan(const wchar_t* bmsQuery, const wchar_t* siblingQuery, EBridgeResult** outResult) {
	if (!outResult) {
		return BRIDGE_INVALID_ARGUMENT;
	}
	*outResult = nullptr;
	if (!bmsQuery || !siblingQuery || bmsQuery[0] == L'\0' || siblingQuery[0] == L'\0') {
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
	bool okBms = ExecuteQuery(client, bmsQuery, [&aggregate](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}

		auto it = aggregate.dirIndex.find(path);
		uint32_t dirIdx;
		if (it == aggregate.dirIndex.end()) {
			dirIdx = static_cast<uint32_t>(aggregate.dirPaths.size());
			aggregate.dirPaths.push_back(path);
			aggregate.dirHashes.emplace_back();
			aggregate.dirIndex.emplace(path, dirIdx);
		} else {
			dirIdx = it->second;
		}

		aggregate.bmsPaths.push_back(CombinePathAndName(path, name));
		aggregate.dirHashes[dirIdx].push_back(GetNormalizedFileNameHash(name));
	}, nullptr);

	if (!okBms) {
		g_api.DestroyClient(client);
		return BRIDGE_BMS_QUERY_FAILED;
	}

	bool okSibling = ExecuteQuery(client, siblingQuery, [&aggregate](const std::wstring& path, const std::wstring& name) {
		if (path.empty() || name.empty()) {
			return;
		}
		auto it = aggregate.dirIndex.find(path);
		if (it == aggregate.dirIndex.end()) {
			return;
		}
		aggregate.dirHashes[it->second].push_back(GetNormalizedFileNameHash(name));
	}, nullptr);

	g_api.DestroyClient(client);
	if (!okSibling) {
		return BRIDGE_SIBLING_QUERY_FAILED;
	}

	DedupeAggregate(aggregate);
	return BuildResultBuffer(aggregate, outResult);
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
