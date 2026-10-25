// 製品の実装を同じ翻訳単位で動かし、非公開のSDK関数ポインター境界だけを接続する。
// テスト用exportや取得アルゴリズムの複製を製品へ持ち込まない。
#include "../EverythingBridge.cpp"
#include "PinnedSdkResult.h"
#include <atomic>
#include <iostream>
#include <memory>

namespace {
using namespace sdk_fixture;

struct Fixture;
Fixture* active = nullptr;

struct Fixture {
	EverythingApi previousApi = g_api;
	bool previousLoaded = g_apiLoaded;
	HMODULE module = nullptr;
	std::array<Everything3_GetResultPathWFn, 3> getters{};
	std::unordered_map<std::wstring, Query> queries;
	std::unordered_map<std::wstring, std::vector<Call>> completedCalls;
	std::mutex completedMutex;
	std::atomic<int> clients{0}, states{0}, results{0};

	explicit Fixture(const wchar_t* sdkPath) {
		module = LoadLibraryExW(sdkPath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
		Require(module != nullptr, "pinned SDK load failed");
		getters = {LoadProc<Everything3_GetResultPathWFn>(module, "Everything3_GetResultNameW"),
			LoadProc<Everything3_GetResultPathWFn>(module, "Everything3_GetResultPathW"),
			LoadProc<Everything3_GetResultPathWFn>(module, "Everything3_GetResultFullPathNameW")};
		if (!getters[0] || !getters[1] || !getters[2]) {
			FreeLibrary(module);
			throw std::runtime_error("pinned SDK getter missing");
		}
		active = this;
		g_api = {};
		g_api.everythingModule = module;
		g_apiLoaded = true;
		g_api.ConnectW = [](const wchar_t*) -> void* { ++active->clients; return new int(0); };
		g_api.DestroyClient = [](void* p) { delete static_cast<int*>(p); --active->clients; return 1; };
		g_api.GetLastError = []() -> unsigned int { return EVERYTHING3_OK; };
		g_api.CreateSearchState = []() -> void* { ++active->states; return new std::wstring; };
		g_api.DestroySearchState = [](void* p) { delete static_cast<std::wstring*>(p); --active->states; return 1; };
		g_api.SetSearchTextW = [](void* p, const wchar_t* text) { *static_cast<std::wstring*>(p) = text; return 1; };
		g_api.ClearSearchPropertyRequests = [](void*) { return 1; };
		g_api.AddSearchPropertyRequest = [](void*, unsigned int) { return 1; };
		g_api.SetSearchViewportOffset = [](void*, size_t) { return 1; };
		g_api.SetSearchViewportCount = [](void*, size_t) { return 1; };
		g_api.Search = [](void*, void* p) -> void* {
			const auto& name = *static_cast<std::wstring*>(p);
			const auto& inputs = active->queries; // workersは同じ不変入力表を読むだけ。
			auto result = new OwnedResult(inputs.at(name), name);
			++active->results;
			return static_cast<ResultList*>(result);
		};
		g_api.DestroyResultList = [](void* p) {
			auto& result = Owner(p);
			{
				std::lock_guard<std::mutex> lock(active->completedMutex);
				active->completedCalls[result.queryName] = std::move(result.calls);
			}
			delete &result;
			--active->results;
			return 1;
		};
		g_api.GetResultListViewportCount = [](const void* p) -> size_t { return Owner(p).viewportCount; };
		g_api.GetResultNameW = [](const void* p, size_t i, wchar_t* b, size_t n) { return Read(Getter::Name, p, i, b, n); };
		g_api.GetResultPathW = [](const void* p, size_t i, wchar_t* b, size_t n) { return Read(Getter::Path, p, i, b, n); };
		g_api.GetResultFullPathNameW = [](const void* p, size_t i, wchar_t* b, size_t n) { return Read(Getter::FullPath, p, i, b, n); };
		g_api.FindPropertyW = [](void*, const wchar_t*) -> unsigned int { return 6; };
		g_api.GetResultDateModified = [](const void* p, size_t i) -> unsigned long long { return Owner(p).query.rows[i].modified; };
	}
	~Fixture() {
		// 公開APIは全開始workerをjoinして戻る。入替えはその前後のmain threadだけで行う。
		g_api = previousApi;
		g_apiLoaded = previousLoaded;
		active = nullptr;
		FreeLibrary(module);
	}
	void CheckReleased() const { Require(clients == 0 && states == 0 && results == 0, "query resource leak"); }
	static size_t Read(Getter kind, const void* p, size_t i, wchar_t* b, size_t n) {
		auto& result = Owner(p);
		result.calls.push_back({i, kind, n});
		const auto& q = result.query;
		bool fail = q.failedGetter == kind && q.failedIndex == i;
		if (fail && (q.failure == Failure::Initial || (q.failure == Failure::Capacity && n == 0))) return 0;
		if (fail && q.failure == Failure::Refetch && n == 0) result.capacityQueried = true;
		size_t length = active->getters[static_cast<size_t>(kind)](p, i, b, n);
		// 再取得だけ不整合な書込み長を返す。完全性判定とcallbackは製品に任せる。
		if (fail && q.failure == Failure::Refetch && result.capacityQueried && n > 0 && length > 0) b[--length] = L'\0';
		return length;
	}
};

std::wstring DirectoryOfLength(size_t length) {
	std::wstring path = L"C:\\fixture";
	while (path.size() < length) {
		size_t remaining = length - path.size();
		if (remaining == 1) { path += L'x'; break; }
		size_t part = (std::min)(size_t(100), remaining - 1);
		path += L'\\' + std::wstring(part, L'd');
	}
	return path;
}
Row FullPathOfLength(size_t length) { return {DirectoryOfLength(length - 9), L"song.bms"}; }

void RunQuery(Fixture& fixture, const std::wstring& key, bool fullPath, bool expectedSuccess = true) {
	const auto& query = fixture.queries.at(key);
	std::vector<Row> observed;
	QueryExecutionStats stats;
	bool ok = ExecuteQueryWithNewClient(key.c_str(), [&](const std::wstring& path, const std::wstring& name, uint64_t modified) {
		observed.push_back({path, name, modified});
	}, &stats, NoopHitCountCallback{}, fullPath, true);
	Require(ok == expectedSuccess, "query completion mismatch");
	size_t expectedCount = expectedSuccess ? query.rows.size() : query.failedIndex;
	Require(observed.size() == expectedCount, "partial/extra callback");
	for (size_t i = 0; i < observed.size(); ++i) {
		Require(observed[i].path == query.rows[i].path && observed[i].name == query.rows[i].name, "callback path/name truncated or mixed");
		Require(observed[i].modified == query.rows[i].modified, "callback mtime lost");
	}
	fixture.CheckReleased();
}

size_t CallCount(const std::vector<Call>& calls, size_t index, Getter kind) {
	return std::count_if(calls.begin(), calls.end(), [&](const Call& call) { return call.index == index && call.getter == kind; });
}

void QueryBoundaries(Fixture& f) {
	// 通常短パスは完全一致かつ1getter。全件事前照会・fullpath後の分離取得を検出する。
	for (size_t length : {100, 250, 1021}) {
		Row row = FullPathOfLength(length);
		row.path.replace(3, 3, L"日\U0001f3b5");
		f.queries[L"short"] = {{row}};
		RunQuery(f, L"short", true);
		Require(f.completedCalls[L"short"].size() == 1, "short full path requires extra getter");
		RunQuery(f, L"short", false);
		Require(f.completedCalls[L"short"].size() == 2, "short split path requires extra getter");
	}
	// 収まる境界と初回不足を実SDKで通し、書込み長とNUL込み容量の混同を検出する。
	for (size_t length : {1022, 1023, 1024, 1200}) {
		f.queries[L"boundary"] = {{FullPathOfLength(length)}};
		RunQuery(f, L"boundary", true);
		f.queries[L"boundary"] = {{{DirectoryOfLength(length), L"song.bms"}}};
		RunQuery(f, L"boundary", false);
	}
	// 残り1単位では補助平面文字を書かない実SDKの返却1022と、収まる対照を区別する。
	for (size_t prefix : {size_t(1009), size_t(1018), size_t(1022)}) {
		Row row{DirectoryOfLength(prefix) + L"\U0001f3b5後続", L"song.bms"};
		Query query{{row}};
		OwnedResult raw(query, L"raw");
		std::vector<wchar_t> buffer(1024);
		size_t length = f.getters[2](static_cast<ResultList*>(&raw), 0, buffer.data(), buffer.size());
		if (prefix == 1022) Require(length == 1022, "SDK surrogate fixture mismatch");
		f.queries[L"surrogate"] = query;
		RunQuery(f, L"surrogate", true);
		RunQuery(f, L"surrogate", false);
	}
	// 名前にも同じ取得責務を適用。小bufferで合法な名前の境界を作り、巨大な単一成分を使わない。
	for (const auto& name : {std::wstring(L"song.bms"), std::wstring(L"日本語\U0001f3b5.bms"), std::wstring(200, L'n') + L".bms"}) {
		Query query{{{L"C:\\fixture", name}}};
		OwnedResult raw(query, L"name");
		std::vector<wchar_t> buffer(10);
		std::wstring observed;
		Require(GetResultName(static_cast<ResultList*>(&raw), 0, buffer, observed) && observed == name, "name boundary mismatch");
	}
	// query内の拡張bufferを使い続け、短い後続への末尾混入と全件再確保を検出する。
	for (bool full : {true, false}) {
		f.queries[L"reuse"] = {{FullPathOfLength(100), FullPathOfLength(1400), FullPathOfLength(1200), FullPathOfLength(80), FullPathOfLength(1401)}};
		RunQuery(f, L"reuse", full);
		const auto& calls = f.completedCalls[L"reuse"];
		Getter getter = full ? Getter::FullPath : Getter::Path;
		Require(CallCount(calls, 2, getter) == 1 && CallCount(calls, 3, getter) == 1, "grown query buffer not reused");
		f.queries[L"next"] = {{{L"C:\\other", L"next.bmson"}}};
		RunQuery(f, L"next", full);
	}
	// 正常0件ではgetter無しで成功し、取得失敗との混同を検出する。
	f.queries[L"empty"] = {};
	RunQuery(f, L"empty", true);
	Require(f.completedCalls[L"empty"].empty(), "empty query invokes getter");
}

void QueryFailures(Fixture& f) {
	// 先行成功後の取得失敗・照会失敗・再取得不一致はcallbackを止めquery失敗にする。
	// Nameの容量不足は小bufferで同じ製品取得関数へ接続し、実NTFSの名前上限と混同しない。
	for (Getter getter : {Getter::FullPath, Getter::Path, Getter::Name}) {
		for (Failure failure : {Failure::Initial, Failure::Capacity, Failure::Refetch}) {
			Query q{{FullPathOfLength(100), FullPathOfLength(1200)}, getter, failure, 1};
			if (getter == Getter::Name && failure != Failure::Initial) {
				OwnedResult raw(q, L"name-failure");
				std::vector<wchar_t> buffer(4);
				std::wstring output;
				Require(!GetResultName(static_cast<ResultList*>(&raw), 1, buffer, output), "incomplete name accepted");
			} else {
				f.queries[L"failure"] = q;
				RunQuery(f, L"failure", getter == Getter::FullPath, false);
			}
		}
	}
}

std::vector<uint32_t> Hashes(const unsigned char* blob, unsigned int offset, unsigned int count) {
	std::vector<uint32_t> values(count);
	if (count) std::memcpy(values.data(), blob + offset, count * sizeof(uint32_t));
	return values;
}

std::wstring PackedString(const wchar_t* blob, unsigned int byteOffset) {
	return reinterpret_cast<const wchar_t*>(reinterpret_cast<const unsigned char*>(blob) + byteOffset);
}

void PublicResults(Fixture& f) {
	// 少数の親子譜面と各資源・textを実packまで通す。期待キーは独立した相対名から固定する。
	std::wstring parent = DirectoryOfLength(1200);
	std::wstring child = parent + L"\\child";
	f.queries = {{L"chart", {{{parent, L"parent.bms"}, {child, L"child.bmson"}}}},
		{L"audio", {{{child, L"tone.wav"}}}}, {L"image", {{{child, L"cover.png"}}}},
		{L"movie", {{{child, L"video.mpg"}}}}, {L"text", {{{parent, L"folderinfo.txt"}}}}};
	EBridgeResult* raw = nullptr;
	int status = EBridge_ScanChartAndResources(L"chart", L"audio", L"image", L"movie", L"text", &raw);
	std::unique_ptr<EBridgeResult, decltype(&EBridge_FreeResult)> result(raw, EBridge_FreeResult);
	Require(status == BRIDGE_OK && result && result->chart_count == 2 && result->dir_count == 2, "fixed scan failed");
	std::vector<std::wstring> charts;
	for (size_t i = 0; i < result->chart_count; ++i) {
		charts.push_back(PackedString(result->chart_blob, result->chart_offsets[i]));
		Require(result->chart_last_write_filetimes[i] == 132000000000000000ull, "chart time lost");
	}
	std::sort(charts.begin(), charts.end());
	std::vector<std::wstring> expected{parent + L"\\parent.bms", child + L"\\child.bmson"};
	std::sort(expected.begin(), expected.end());
	Require(charts == expected, "packed chart path truncated");
	Require(result->text_count == 1 && PackedString(result->text_blob, result->text_offsets[0]) == parent + L"\\folderinfo.txt"
		&& result->text_last_write_filetimes[0] == 132000000000000000ull, "packed text/time lost");
	// 値はCHILD\TONE / CHILD\COVER / CHILD\VIDEOとTONE / COVER / VIDEOの
	// UTF-16LE xxHash32(seed 0)。独立したxxhashで算出し、製品の正規化関数から生成しない。
	const std::array<uint32_t, 3> parentHashes{0x37f66d88u, 0xf479892bu, 0x6f101cfeu};
	const std::array<uint32_t, 3> childHashes{0x324026b0u, 0x81b5b12fu, 0x7641638cu};
	for (size_t i = 0; i < result->dir_count; ++i) {
		std::wstring directory = PackedString(result->dir_blob, result->dir_offsets[i]);
		Require(directory == parent || directory == child, "packed directory truncated");
		bool self = directory == child;
		auto check = [&](const unsigned char* blob, const unsigned int* offsets, const unsigned int* lengths,
			const unsigned char* selfBlob, const unsigned int* selfOffsets, const unsigned int* selfLengths, size_t category) {
			Require(Hashes(blob, offsets[i], lengths[i]) == std::vector<uint32_t>{self ? childHashes[category] : parentHashes[category]}, "aggregate resource key/owner mismatch");
			Require(Hashes(selfBlob, selfOffsets[i], selfLengths[i]) == (self ? std::vector<uint32_t>{childHashes[category]} : std::vector<uint32_t>{}), "self resource owner mismatch");
		};
		check(result->audio_resource_key_hashes_blob, result->audio_resource_key_hash_offsets, result->audio_resource_key_hash_lengths,
			result->self_audio_resource_key_hashes_blob, result->self_audio_resource_key_hash_offsets, result->self_audio_resource_key_hash_lengths, 0);
		check(result->image_resource_key_hashes_blob, result->image_resource_key_hash_offsets, result->image_resource_key_hash_lengths,
			result->self_image_resource_key_hashes_blob, result->self_image_resource_key_hash_offsets, result->self_image_resource_key_hash_lengths, 1);
		check(result->movie_resource_key_hashes_blob, result->movie_resource_key_hash_offsets, result->movie_resource_key_hash_lengths,
			result->self_movie_resource_key_hashes_blob, result->self_movie_resource_key_hash_offsets, result->self_movie_resource_key_hash_lengths, 2);
	}
	EBridgeSourceRootRequest root{37, parent.c_str()};
	EBridgeSourceRootsResult* sourceRaw = nullptr;
	status = EBridge_ScanSourceRoots(&root, 1, L"chart", L"audio", L"image", L"movie", &sourceRaw);
	std::unique_ptr<EBridgeSourceRootsResult, decltype(&EBridge_FreeSourceRootsResult)> source(sourceRaw, EBridge_FreeSourceRootsResult);
	Require(status == BRIDGE_OK && source && source->root_count == 1 && source->roots[0].root_id == 37, "source root identity lost");
	Require(PackedString(source->root_blob, source->root_offsets[0]) == parent && source->roots[0].chart_count == 2, "source root path/count lost");
	std::vector<std::wstring> sourceCharts;
	for (size_t i = 0; i < 2; ++i) sourceCharts.push_back(PackedString(source->chart_blob, source->roots[0].chart_offsets[i]));
	std::sort(sourceCharts.begin(), sourceCharts.end());
	Require(sourceCharts == expected, "source chart path truncated");
	EBridgeGroupedQuery query{73, L"text"};
	EBridgeGroupedFilesResult* groupedRaw = nullptr;
	status = EBridge_EnumerateGroupedFiles(&query, 1, &groupedRaw);
	std::unique_ptr<EBridgeGroupedFilesResult, decltype(&EBridge_FreeGroupedFilesResult)> grouped(groupedRaw, EBridge_FreeGroupedFilesResult);
	Require(status == BRIDGE_OK && grouped && grouped->group_count == 1 && grouped->groups[0].group_id == 73
		&& grouped->groups[0].path_count == 1, "group identity/count lost");
	Require(PackedString(grouped->path_blob, grouped->groups[0].path_offsets[0]) == parent + L"\\folderinfo.txt"
		&& grouped->groups[0].last_write_filetimes[0] == 132000000000000000ull, "group path/time lost");
	f.CheckReleased();

	// 各公開APIで先行項目があっても失敗をnull出力へ伝える。黙示skipや部分成功を検出する。
	for (Getter getter : {Getter::FullPath, Getter::Path, Getter::Name}) {
		f.queries[L"chart"] = {{FullPathOfLength(100), FullPathOfLength(1200)}, getter, Failure::Initial, 1};
		if (getter == Getter::FullPath) {
			EBridgeResult* failed = nullptr;
			status = EBridge_ScanChartAndResources(L"chart", L"audio", L"image", L"movie", L"text", &failed);
			std::unique_ptr<EBridgeResult, decltype(&EBridge_FreeResult)> cleanup(failed, EBridge_FreeResult);
			Require(status == BRIDGE_CHART_QUERY_FAILED && failed == nullptr, "fixed partial success");
		} else if (getter == Getter::Path) {
			EBridgeSourceRootsResult* failed = nullptr;
			status = EBridge_ScanSourceRoots(&root, 1, L"chart", L"audio", L"image", L"movie", &failed);
			std::unique_ptr<EBridgeSourceRootsResult, decltype(&EBridge_FreeSourceRootsResult)> cleanup(failed, EBridge_FreeSourceRootsResult);
			Require(status == BRIDGE_CHART_QUERY_FAILED && failed == nullptr, "source partial success");
		} else {
			EBridgeGroupedQuery failedQuery{73, L"chart"};
			EBridgeGroupedFilesResult* failed = nullptr;
			status = EBridge_EnumerateGroupedFiles(&failedQuery, 1, &failed);
			std::unique_ptr<EBridgeGroupedFilesResult, decltype(&EBridge_FreeGroupedFilesResult)> cleanup(failed, EBridge_FreeGroupedFilesResult);
			Require(status == BRIDGE_GROUPED_QUERY_FAILED && failed == nullptr, "group partial success");
		}
		f.CheckReleased();
	}
}
} // namespace

int wmain(int argc, wchar_t** argv) {
	try {
		Require(argc == 2, "run test-x64.ps1 with the pinned SDK");
		Fixture fixture(argv[1]);
		QueryBoundaries(fixture);
		QueryFailures(fixture);
		PublicResults(fixture);
		fixture.CheckReleased();
		std::cout << "PASS: SDK getter boundaries, query reuse/failure, packed API results, resource cleanup\n";
		return 0;
	} catch (const std::exception& ex) {
		std::cerr << "FAIL: " << ex.what() << '\n';
		return 1;
	}
}
