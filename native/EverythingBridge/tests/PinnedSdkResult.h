#pragma once

#include <array>
#include <cstddef>
#include <stdexcept>

// Everything SDK 3.0.0.9の文字列getterに渡す結果だけを組み立てるfixture。
// 対応する一次ソース: https://github.com/voidtools/everything_sdk3/blob/3.0.0.9/src/Everything3.c
// 結果構造の出典: Copyright (C) 2025 voidtools / David Carpenter (MIT).
// 許諾全文: ../../../third_party/licenses/16-Everything3-MIT.txt
// _everything3_result_list_{property_request,item}_t / _everything3_result_list_t と
// _everything3_pool_t、_everything3_utf8_pstring_tの配置に対応する。
// test-x64.ps1が版/hashを照合する。SDKの検索・解放へは渡さず、下記の所有者が回収する。
// 公開fixture APIが得られた場合、またはSDK更新時に、この限定的な非公開接続を再検討する。
namespace sdk_fixture {

inline void Require(bool condition, const char* message) {
	if (!condition) throw std::runtime_error(message);
}

struct PropertyRequest {
	size_t offset;
	DWORD id;
	DWORD flags;
	DWORD valueType;
};
struct Sort { DWORD id; DWORD flags; };
struct Item { void* propertyData; };
struct PoolChunk;
struct Pool { PoolChunk* first; PoolChunk* last; BYTE* position; size_t available; };
struct ResultList {
	uint64_t totalSize = 0;
	size_t folderCount = 0;
	size_t fileCount = 0;
	size_t viewportOffset = 0;
	size_t viewportCount = 0;
	DWORD validFlags = 0;
	Sort* sorts = nullptr;
	size_t sortCount = 0;
	PropertyRequest* properties = nullptr;
	size_t propertyCount = 0;
	PropertyRequest** sortedProperties = nullptr;
	Item* items = nullptr;
	Pool pool{};
};
static_assert(sizeof(wchar_t) == 2 && sizeof(void*) == 8, "Windows x64 fixture only");
static_assert(sizeof(PropertyRequest) == 24 && sizeof(ResultList) == 128);
static_assert(offsetof(ResultList, viewportCount) == 32 && offsetof(ResultList, items) == 88);

enum class Getter { Name, Path, FullPath };
enum class Failure { None, Initial, Capacity, Refetch };
struct Row {
	std::wstring path;
	std::wstring name;
	uint64_t modified = 132000000000000000ull;
};

inline void CheckComponents(const Row& row) {
	std::wstring full = row.path + L"\\" + row.name;
	size_t begin = 0;
	for (;;) {
		size_t end = full.find(L'\\', begin);
		Require((end == std::wstring::npos ? full.size() : end) - begin <= 255, "fixture component exceeds normal Windows limit");
		if (end == std::wstring::npos) break;
		begin = end + 1;
	}
}

struct Query {
	std::vector<Row> rows;
	Getter failedGetter = Getter::FullPath;
	Failure failure = Failure::None;
	size_t failedIndex = 1;
};
struct Call {
	size_t index;
	Getter getter;
	size_t capacity;
};

inline std::vector<unsigned char> PascalUtf8(const std::wstring& value) {
	int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
	Require(count > 0, "invalid fixture UTF-16");
	size_t length = static_cast<size_t>(count);
	size_t prefix = length < 255 ? 1 : 1 + sizeof(size_t);
	std::vector<unsigned char> bytes(prefix + length);
	bytes[0] = length < 255 ? static_cast<unsigned char>(length) : 255;
	if (length >= 255) std::memcpy(bytes.data() + 1, &length, sizeof(length));
	Require(WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
		reinterpret_cast<char*>(bytes.data() + prefix), count, nullptr, nullptr) == count, "fixture UTF-8 conversion failed");
	return bytes;
}

// ResultList基底のアドレスだけをSDKへ渡す。各queryの文字列・property値は構築後不変。
// SDKのpoolへ割り当てず、C++ vectorが寿命を所有するためSDK DestroyResultListは使用しない。
struct OwnedResult : ResultList {
	const Query& query;
	std::wstring queryName;
	// include/Everything3.hのNAME=0、PATH=1、PATH_AND_NAME=240、VALUE_TYPE_PSTRING=8。
	std::array<PropertyRequest, 3> requests{{{0, 0, 0, 8}, {sizeof(void*), 1, 0, 8}, {2 * sizeof(void*), 240, 0, 8}}};
	std::array<PropertyRequest*, 3> ordered{};
	std::vector<std::array<std::vector<unsigned char>, 3>> strings;
	std::vector<std::array<void*, 3>> values;
	std::vector<Item> itemStorage;
	std::vector<Call> calls;
	bool capacityQueried = false;

	OwnedResult(const Query& input, const std::wstring& name) : query(input), queryName(name) {
		viewportCount = fileCount = query.rows.size();
		propertyCount = requests.size();
		properties = requests.data();
		for (size_t p = 0; p < requests.size(); ++p) ordered[p] = &requests[p];
		sortedProperties = ordered.data();
		strings.resize(viewportCount);
		values.resize(viewportCount);
		itemStorage.resize(viewportCount);
		for (size_t i = 0; i < viewportCount; ++i) {
			const auto& row = query.rows[i];
			CheckComponents(row);
			strings[i] = {PascalUtf8(row.name), PascalUtf8(row.path), PascalUtf8(row.path + L"\\" + row.name)};
			for (size_t p = 0; p < requests.size(); ++p) values[i][p] = strings[i][p].data();
			itemStorage[i].propertyData = values[i].data();
		}
		items = itemStorage.data();
	}
	OwnedResult(const OwnedResult&) = delete;
	OwnedResult& operator=(const OwnedResult&) = delete;
};

inline OwnedResult& Owner(const void* result) {
	return *static_cast<OwnedResult*>(const_cast<ResultList*>(static_cast<const ResultList*>(result)));
}

} // namespace sdk_fixture
