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
	std::atomic<int> searches{0};

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
		HMODULE productReference = module;
#ifdef BMS_PATH_IDENTITY_FIXTURE
		// fixture保持分とは別の参照を製品Shutdownへ移す。初期化失敗時は保持分も回収する。
		productReference = LoadLibraryExW(sdkPath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
		if (!productReference) {
			FreeLibrary(module);
			throw std::runtime_error("SDK bridge reference failed");
		}
#endif
		active = this;
		g_api = {};
		g_api.everythingModule = productReference;
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
			++active->searches;
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
#ifdef BMS_PATH_IDENTITY_FIXTURE
		// native callの終了後だけ実行し、初期化後にscanできなかった場合の参照も回収する。
		EBridge_Shutdown();
#endif
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

// 独立した大文字相対キーのUTF-16LE xxHash32(seed0)定数で全segmentと三種別を検査する。
// 試験側で製品normalizerを使って期待値を作らず、末尾alias・親別名・自己所有の混同を検出する。
struct IdentityParent { const wchar_t* name; std::array<uint32_t, 3> hashes; };
void CheckIdentityPack(Fixture& f, const std::vector<IdentityParent>& parents, const char* label) {
 const std::wstring root = L"C:\\identity";
 f.queries = {{L"chart", {{{root, L"root.bms"}}}}, {L"audio", {}}, {L"image", {}}, {L"movie", {}}, {L"text", {}}};
 for (const auto& parent : parents) {
  auto path = root + L"\\" + parent.name;
  f.queries[L"audio"].rows.push_back({path, L"tone.wav"});
  f.queries[L"audio"].rows.push_back({path, L"tone.ogg"});
  f.queries[L"image"].rows.push_back({path, L"cover.jpg"});
  f.queries[L"image"].rows.push_back({path, L"cover.png"});
  f.queries[L"movie"].rows.push_back({path, L"video.mpg"});
 }
 int searchesBefore = f.searches;
 EBridgeResult* raw = nullptr;
 int status = EBridge_ScanChartAndResources(L"chart", L"audio", L"image", L"movie", L"text", &raw);
 std::unique_ptr<EBridgeResult, decltype(&EBridge_FreeResult)> result(raw, EBridge_FreeResult);
 f.CheckReleased();
 Require(status == BRIDGE_OK && result && result->dir_count == 1, "identity scan failed");
 std::cout << "WORK " << label << " query_hits=" << result->chart_query_hits << ',' << result->audio_query_hits << ',' << result->image_query_hits << ',' << result->movie_query_hits
  << " dirs=" << result->audio_resource_dir_count << ',' << result->image_resource_dir_count << ',' << result->movie_resource_dir_count
  << " cache=" << result->owner_cache_hit_count << '/' << result->owner_cache_miss_count << ',' << result->relative_prefix_cache_hit_count << '/' << result->relative_prefix_cache_miss_count
  << " assigned=" << result->audio_assigned_count << ',' << result->image_assigned_count << ',' << result->movie_assigned_count << " bytes=" << result->raw_buffer_size << '\n';
 const unsigned char* blobs[]{result->audio_resource_key_hashes_blob, result->image_resource_key_hashes_blob, result->movie_resource_key_hashes_blob};
 const unsigned int* offsets[]{result->audio_resource_key_hash_offsets, result->image_resource_key_hash_offsets, result->movie_resource_key_hash_offsets};
 const unsigned int* lengths[]{result->audio_resource_key_hash_lengths, result->image_resource_key_hash_lengths, result->movie_resource_key_hash_lengths};
 const unsigned char* selfBlobs[]{result->self_audio_resource_key_hashes_blob, result->self_image_resource_key_hashes_blob, result->self_movie_resource_key_hashes_blob};
 const unsigned int* selfOffsets[]{result->self_audio_resource_key_hash_offsets, result->self_image_resource_key_hash_offsets, result->self_movie_resource_key_hash_offsets};
 const unsigned int* selfLengths[]{result->self_audio_resource_key_hash_lengths, result->self_image_resource_key_hash_lengths, result->self_movie_resource_key_hash_lengths};
 const unsigned long long reverseCounts[]{result->audio_relative_reverse_key_count, result->image_relative_reverse_key_count, result->movie_relative_reverse_key_count};
 const unsigned int* reverseKeys[]{result->audio_relative_reverse_keys, result->image_relative_reverse_keys, result->movie_relative_reverse_keys};
 const unsigned int* reverseOffsets[]{result->audio_relative_reverse_offsets, result->image_relative_reverse_offsets, result->movie_relative_reverse_offsets};
 const unsigned int* reverseLengths[]{result->audio_relative_reverse_lengths, result->image_relative_reverse_lengths, result->movie_relative_reverse_lengths};
 const unsigned char* reverseIndices[]{result->audio_relative_reverse_indices_blob, result->image_relative_reverse_indices_blob, result->movie_relative_reverse_indices_blob};
 for (size_t c = 0; c < 3; ++c) {
  std::vector<uint32_t> expected;
  for (const auto& p : parents) expected.push_back(p.hashes[c]);
  std::sort(expected.begin(), expected.end());
  auto observed = Hashes(blobs[c], offsets[c][0], lengths[c][0]);
  std::cout << "HASH " << label << " category=" << c << " count=" << observed.size() << " expected=" << expected.size() << " self_count=" << selfLengths[c][0] << " reverse_keys=" << reverseCounts[c];
  for (auto h : observed) std::cout << ' ' << std::hex << h << std::dec;
  std::cout << '\n';
  Require(observed == expected, "directory identity aggregate hash/count mismatch");
  Require(Hashes(selfBlobs[c], selfOffsets[c][0], selfLengths[c][0]) == expected, "directory identity self hash/count mismatch");
  Require(reverseCounts[c] == expected.size(), "directory identity reverse count mismatch");
  size_t reversePairs = 0;
  for (size_t i = 0; i < reverseCounts[c]; ++i) reversePairs += reverseLengths[c][i];
  std::cout << "REVERSE " << label << " category=" << c << " pairs=" << reversePairs << '\n';
  std::vector<uint32_t> keys(reverseKeys[c], reverseKeys[c] + reverseCounts[c]);
  Require(keys == expected, "directory identity reverse keys mismatch");
  for (size_t i = 0; i < expected.size(); ++i) {
   uint32_t index = 0;
   std::memcpy(&index, reverseIndices[c] + reverseOffsets[c][i], result->reverse_index_bytes);
   Require(reverseLengths[c][i] == 1 && index == 0, "directory identity reverse owner mismatch");
  }
 }
 size_t observedGetterCalls = 0;
 for (const auto& key : {L"chart", L"audio", L"image", L"movie"}) observedGetterCalls += f.completedCalls.at(key).size();
 std::cout << "GETTERS " << label << " fullpath_calls=" << observedGetterCalls << " capacity=1024 searches=" << (f.searches-searchesBefore) << "\n";
 Require(f.searches-searchesBefore == 5, "identity search work mismatch");
 size_t count = parents.size();
 Require(result->chart_query_hits == 1 && result->audio_query_hits == count*2 && result->image_query_hits == count*2 && result->movie_query_hits == count, "identity query work mismatch");
 Require(result->audio_resource_dir_count == count && result->image_resource_dir_count == count && result->movie_resource_dir_count == count, "identity directory work mismatch");
 Require(result->owner_cache_miss_count == count && result->owner_cache_hit_count == count*2 && result->relative_prefix_cache_miss_count == count && result->relative_prefix_cache_hit_count == count*2, "identity prefix cache work mismatch");
 Require(result->audio_assigned_count == count*2 && result->image_assigned_count == count*2 && result->movie_assigned_count == count, "identity assigned work mismatch");
 for (const auto& key : {L"chart", L"audio", L"image", L"movie"}) {
  const auto& calls = f.completedCalls.at(key);
  Require(calls.size() == f.queries.at(key).rows.size(), "identity short getter work mismatch");
  for (size_t i=0;i<calls.size();++i) Require(calls[i].index == i && calls[i].getter == Getter::FullPath && calls[i].capacity == 1024, "identity getter kind/index/capacity mismatch");
 }
}

void PrepareIdentityConnection(Fixture& f, const std::wstring& root) {
 const auto child = root + L"\\pack.ogg";
 const auto own = root + L"\\audio.ogg";
 const auto nested = child + L"\\sound.jpg";
 f.queries = {{L"chart", {{{root, L"root.bms"}, {child, L"child.bms"}}}},
  {L"audio", {{{own, L"tone.ogg"}, {own, L"tone.wav"}, {nested, L"tone.wav"}}}},
  {L"image", {{{own, L"cover.jpg"}, {nested, L"cover.png"}}}},
  {L"movie", {{{own, L"video.mpg"}, {nested, L"video.mpg"}}}}, {L"text", {}}};
}


void IdentityOwnership(Fixture& f) {
 const std::wstring root = L"C:\\identity";
 PrepareIdentityConnection(f, root);
 EBridgeResult* raw = nullptr;
 int status = EBridge_ScanChartAndResources(L"chart", L"audio", L"image", L"movie", L"text", &raw);
 std::unique_ptr<EBridgeResult, decltype(&EBridge_FreeResult)> result(raw, EBridge_FreeResult);
 f.CheckReleased();
 Require(status == BRIDGE_OK && result && result->dir_count == 2, "identity ownership scan failed");
 // 明示キーAUDIO.OGG\{TONE,COVER,VIDEO}、PACK.OGG\SOUND.JPG\...、SOUND.JPG\...から独立算出。
 const std::array<uint32_t,3> own = {0x4e880b6cu, 0x064ff08eu, 0xf5e7743du}, parent = {0xd3aa72eau, 0x5c9056a9u, 0xce65fd42u}, child = {0x040ea5b9u, 0xa98283edu, 0x86741d76u};
 for (size_t i=0;i<result->dir_count;++i) {
  bool isRoot = PackedString(result->dir_blob, result->dir_offsets[i]) == root;
  auto check = [&](const unsigned char* blob, const unsigned int* offsets, const unsigned int* lengths,
   const unsigned char* selfBlob, const unsigned int* selfOffsets, const unsigned int* selfLengths, size_t c) {
   std::vector<uint32_t> expected = isRoot ? std::vector<uint32_t>{own[c],parent[c]} : std::vector<uint32_t>{child[c]};
   std::sort(expected.begin(),expected.end());
   Require(Hashes(blob,offsets[i],lengths[i]) == expected, "identity ancestor aggregate mismatch");
   Require(Hashes(selfBlob,selfOffsets[i],selfLengths[i]) == std::vector<uint32_t>{isRoot ? own[c] : child[c]}, "identity nearest self owner mismatch");
  };
  check(result->audio_resource_key_hashes_blob,result->audio_resource_key_hash_offsets,result->audio_resource_key_hash_lengths,result->self_audio_resource_key_hashes_blob,result->self_audio_resource_key_hash_offsets,result->self_audio_resource_key_hash_lengths,0);
  check(result->image_resource_key_hashes_blob,result->image_resource_key_hash_offsets,result->image_resource_key_hash_lengths,result->self_image_resource_key_hashes_blob,result->self_image_resource_key_hash_offsets,result->self_image_resource_key_hash_lengths,1);
  check(result->movie_resource_key_hashes_blob,result->movie_resource_key_hash_offsets,result->movie_resource_key_hash_lengths,result->self_movie_resource_key_hashes_blob,result->self_movie_resource_key_hash_offsets,result->self_movie_resource_key_hash_lengths,2);
 }
}

void DirectoryIdentity(Fixture& f) {
 const std::vector<IdentityParent> table{
		{L"audio.ogg", {0x4e880b6cu, 0x064ff08eu, 0xf5e7743du}},
		{L"audio.mp3", {0xf4798780u, 0xc6e97388u, 0xcd356b22u}},
		{L"audio.flac", {0xba197ff4u, 0xc4ef2f3fu, 0x99434f6bu}},
		{L"audio.wav", {0xcd9dc06cu, 0xe3af260bu, 0x7ec53a22u}},
		{L"image.bmp", {0x82c77419u, 0x6559cc41u, 0x151242cfu}},
		{L"image.jpg", {0x35f05d52u, 0x81f81cddu, 0xa5fdaae4u}},
		{L"image.jpeg", {0x93ba0632u, 0xdfccf2d3u, 0xbe1af20eu}},
		{L"image.png", {0x92b4ff7cu, 0xad67d31fu, 0x5fce05ccu}},
		{L"has.ogg.middle", {0x772563beu, 0x49f52420u, 0x42ac14efu}},
		{L"part.ogg.extra", {0x4c8f19bfu, 0xe7c7d8b1u, 0x24172cd2u}},
		{L"upper.ogg\\plain", {0xf50d0ad5u, 0x3561998au, 0xa56b90dcu}},
		{L"upper.ogg\\inner.jpg", {0xf514312au, 0x052b7e10u, 0xd71f4d64u}},
		{L"日本語.ogg", {0xe7d2c09cu, 0xa75f1a49u, 0xb0b73063u}},
 };
 CheckIdentityPack(f, {table[8], table[9]}, "control");
 CheckIdentityPack(f, {table[0], table[3]}, "distinct-parents");
 CheckIdentityPack(f, table, "parent-table");
}

// rootのSDK分離列はC:でも、実full-pathと公開directoryはC:\でなければならない。
// 通常/UNCの末尾なしcanonical、親子所有、三種とsource/groupedの同一性も同じ公開境界で検査する。
void RootIdentity(Fixture& f, const std::wstring& sdkParent, const std::wstring& expectedParent, const char* label) {
	const std::wstring prefix = expectedParent + (expectedParent.back() == L'\\' ? L"" : L"\\");
	const std::wstring child = prefix + L"Songs";
	f.queries = {{L"chart", {{{sdkParent, L"root.bms"}, {child, L"child.bmson"}}}},
		{L"audio", {{{sdkParent, L"tone.wav"}, {child, L"tone.ogg"}}}},
		{L"image", {{{sdkParent, L"cover.png"}, {child, L"cover.jpg"}}}},
		{L"movie", {{{sdkParent, L"video.mpg"}, {child, L"video.mpg"}}}},
		{L"text", {{{sdkParent, L"folderinfo.txt"}}}}};
	OwnedResult probe(f.queries.at(L"chart"), L"root-probe");
	wchar_t fullPath[1024]{};
	Require(f.getters[2](static_cast<ResultList*>(&probe), 0, fullPath, 1024) > 0 && fullPath == prefix + L"root.bms", "root fixture full-path precondition failed");
	const int beforeSearches = f.searches;
	EBridgeResult* raw = nullptr;
	int status = EBridge_ScanChartAndResources(L"chart", L"audio", L"image", L"movie", L"text", &raw);
	std::unique_ptr<EBridgeResult, decltype(&EBridge_FreeResult)> result(raw, EBridge_FreeResult);
	f.CheckReleased();
	Require(status == BRIDGE_OK && result && result->chart_count == 2 && result->dir_count == 2, "root fixed scan failed");
	const std::array<uint32_t, 3> own{0x324026b0u, 0x81b5b12fu, 0x7641638cu};
	const std::array<uint32_t, 3> inherited{0x739bd3f0u, 0x4b10593bu, 0x460d6a50u}; // SONGS\\{TONE,COVER,VIDEO}、独立UTF16LE xxHash32。
	for (size_t i = 0; i < result->dir_count; ++i) {
		const auto directory = PackedString(result->dir_blob, result->dir_offsets[i]);
		std::wcout << L"ROOT " << label << L" directory=" << directory << L'\n';
		Require(directory == expectedParent || directory == child, "absolute root directory identity lost");
		const bool parent = directory == expectedParent;
		auto check = [&](const unsigned char* blob, const unsigned int* offsets, const unsigned int* lengths,
			const unsigned char* selfBlob, const unsigned int* selfOffsets, const unsigned int* selfLengths, size_t c) {
			std::vector<uint32_t> expected = parent ? std::vector<uint32_t>{own[c], inherited[c]} : std::vector<uint32_t>{own[c]};
			std::sort(expected.begin(), expected.end());
			Require(Hashes(blob, offsets[i], lengths[i]) == expected, "root ancestor aggregate lost");
			Require(Hashes(selfBlob, selfOffsets[i], selfLengths[i]) == std::vector<uint32_t>{own[c]}, "root nearest self owner lost");
		};
		check(result->audio_resource_key_hashes_blob,result->audio_resource_key_hash_offsets,result->audio_resource_key_hash_lengths,result->self_audio_resource_key_hashes_blob,result->self_audio_resource_key_hash_offsets,result->self_audio_resource_key_hash_lengths,0);
		check(result->image_resource_key_hashes_blob,result->image_resource_key_hash_offsets,result->image_resource_key_hash_lengths,result->self_image_resource_key_hashes_blob,result->self_image_resource_key_hash_offsets,result->self_image_resource_key_hash_lengths,1);
		check(result->movie_resource_key_hashes_blob,result->movie_resource_key_hash_offsets,result->movie_resource_key_hash_lengths,result->self_movie_resource_key_hashes_blob,result->self_movie_resource_key_hash_offsets,result->self_movie_resource_key_hash_lengths,2);
	}
	std::vector<std::wstring> charts;
	for (size_t i=0;i<result->chart_count;++i) {
		charts.push_back(PackedString(result->chart_blob,result->chart_offsets[i]));
		Require(result->chart_last_write_filetimes[i] == 132000000000000000ull, "root chart time lost");
	}
	std::sort(charts.begin(),charts.end());
	std::vector<std::wstring> expectedCharts{prefix+L"root.bms", child+L"\\child.bmson"};
	std::sort(expectedCharts.begin(),expectedCharts.end());
	Require(charts == expectedCharts, "root full chart paths lost");
	Require(result->text_count == 1 && PackedString(result->text_blob,result->text_offsets[0]) == prefix+L"folderinfo.txt"
		&& result->text_last_write_filetimes[0] == 132000000000000000ull, "root text path/time lost");
	Require(result->audio_relative_reverse_key_count == 2 && result->image_relative_reverse_key_count == 2 && result->movie_relative_reverse_key_count == 2, "root reverse key count lost");
	for (const auto lengths : {result->audio_relative_reverse_lengths,result->image_relative_reverse_lengths,result->movie_relative_reverse_lengths})
		Require(lengths[0]+lengths[1] == 3, "root reverse pairs lost");
	auto checkReverse = [&](const unsigned int* keys, const unsigned int* offsets, const unsigned int* lengths, const unsigned char* indices, size_t c) {
		for (size_t i=0;i<2;++i) {
			Require(keys[i] == own[c] || keys[i] == inherited[c], "root reverse hash lost");
			std::vector<std::wstring> owners;
			for (size_t j=0;j<lengths[i];++j) {
				uint32_t index = 0;
				std::memcpy(&index,indices+offsets[i]+j*result->reverse_index_bytes,result->reverse_index_bytes);
				Require(index < result->dir_count, "root reverse owner index invalid");
				owners.push_back(PackedString(result->dir_blob,result->dir_offsets[index]));
			}
			std::sort(owners.begin(),owners.end());
			std::vector<std::wstring> expected = keys[i] == own[c] ? std::vector<std::wstring>{expectedParent,child} : std::vector<std::wstring>{expectedParent};
			std::sort(expected.begin(),expected.end());
			Require(owners == expected, "root reverse owner identity lost");
		}
	};
	checkReverse(result->audio_relative_reverse_keys,result->audio_relative_reverse_offsets,result->audio_relative_reverse_lengths,result->audio_relative_reverse_indices_blob,0);
	checkReverse(result->image_relative_reverse_keys,result->image_relative_reverse_offsets,result->image_relative_reverse_lengths,result->image_relative_reverse_indices_blob,1);
	checkReverse(result->movie_relative_reverse_keys,result->movie_relative_reverse_offsets,result->movie_relative_reverse_lengths,result->movie_relative_reverse_indices_blob,2);
	size_t getterCount = 0;
	for (const auto key : {L"chart",L"audio",L"image",L"movie",L"text"}) {
		const auto& calls = f.completedCalls.at(key);
		getterCount += calls.size();
		Require(calls.size() == f.queries.at(key).rows.size(), "root full getter work changed");
		for (size_t i=0;i<calls.size();++i) Require(calls[i].getter == Getter::FullPath && calls[i].index == i && calls[i].capacity == 1024, "root getter contract changed");
	}
	Require(f.searches-beforeSearches == 5 && getterCount == 9, "root query/getter work changed");
	Require(result->chart_query_hits == 2 && result->audio_query_hits == 2 && result->image_query_hits == 2 && result->movie_query_hits == 2, "root query hit work changed");
	Require(result->audio_resource_dir_count == 2 && result->image_resource_dir_count == 2 && result->movie_resource_dir_count == 2, "root directory work changed");
	Require(result->owner_cache_hit_count == 4 && result->owner_cache_miss_count == 2 && result->relative_prefix_cache_hit_count == 4 && result->relative_prefix_cache_miss_count == 2, "root cache work changed");
	Require(result->audio_assigned_count == 2 && result->image_assigned_count == 2 && result->movie_assigned_count == 2, "root assignment work changed");
	std::cout << "ROOTWORK " << label << " hits=" << result->chart_query_hits << '/' << result->audio_query_hits << '/' << result->image_query_hits << '/' << result->movie_query_hits
		<< " dirs=" << result->audio_resource_dir_count << ',' << result->image_resource_dir_count << ',' << result->movie_resource_dir_count
		<< " cache=4/2,4/2 assigned=2/2/2 getters=" << getterCount << " searches=5 six_faces=15 reverse_keys/pairs=6/9 bytes=" << result->raw_buffer_size << '\n';
	// source要求の末尾表記はcanonicalへ揃え、より具体的な通常directoryとのsegment境界を保つ。
	const std::wstring requestedRoot = prefix;
	EBridgeSourceRootRequest request{91,requestedRoot.c_str()};
	EBridgeSourceRootsResult* sourceRaw = nullptr;
	status = EBridge_ScanSourceRoots(&request,1,L"chart",L"audio",L"image",L"movie",&sourceRaw);
	std::unique_ptr<EBridgeSourceRootsResult,decltype(&EBridge_FreeSourceRootsResult)> source(sourceRaw,EBridge_FreeSourceRootsResult);
	Require(status == BRIDGE_OK && source && source->root_count == 1 && source->roots[0].root_id == 91
		&& PackedString(source->root_blob,source->root_offsets[0]) == expectedParent && source->roots[0].chart_count == 2
		&& source->roots[0].categorized_resource_file_count == 6, "source absolute root identity/count lost");
	const auto& entry = source->roots[0];
	std::vector<uint32_t> expectedAudio{own[0],inherited[0]}; std::sort(expectedAudio.begin(),expectedAudio.end());
	Require(Hashes(source->audio_resource_key_hashes_blob,entry.audio_resource_key_hash_offset,entry.audio_resource_key_hash_length) == expectedAudio, "source root relative keys lost");
	auto sourceGetterCount = [&]() {
		size_t count = 0;
		for (const auto key : {L"chart",L"audio",L"image",L"movie"}) count += f.completedCalls.at(key).size();
		return count;
	};
	const size_t rootSourceGetters = sourceGetterCount();
	Require(rootSourceGetters == 16, "source root getter work changed");
	// Songs2を追加してもSongsだけを要求した結果へ混ぜず、通常directoryのsegment境界を維持する。
	f.queries[L"chart"].rows.push_back({prefix+L"Songs2",L"sibling.bms"});
	f.queries[L"audio"].rows.push_back({prefix+L"Songs2",L"outside.wav"});
	EBridgeSourceRootRequest childRequest{93,child.c_str()}; EBridgeSourceRootsResult* childRaw = nullptr;
	status = EBridge_ScanSourceRoots(&childRequest,1,L"chart",L"audio",L"image",L"movie",&childRaw);
	std::unique_ptr<EBridgeSourceRootsResult,decltype(&EBridge_FreeSourceRootsResult)> childResult(childRaw,EBridge_FreeSourceRootsResult);
	Require(status == BRIDGE_OK && childResult && childResult->root_count == 1 && childResult->roots[0].chart_count == 1
		&& childResult->roots[0].categorized_resource_file_count == 3
		&& PackedString(childResult->chart_blob,childResult->roots[0].chart_offsets[0]) == child+L"\\child.bmson", "source sibling prefix mixed");
	const size_t childSourceGetters = sourceGetterCount();
	Require(childSourceGetters == 20, "source child getter work changed");
	EBridgeGroupedQuery query{92,L"text"}; EBridgeGroupedFilesResult* groupedRaw = nullptr;
	status = EBridge_EnumerateGroupedFiles(&query,1,&groupedRaw);
	std::unique_ptr<EBridgeGroupedFilesResult,decltype(&EBridge_FreeGroupedFilesResult)> grouped(groupedRaw,EBridge_FreeGroupedFilesResult);
	Require(status == BRIDGE_OK && grouped && grouped->group_count == 1 && grouped->groups[0].path_count == 1
		&& PackedString(grouped->path_blob,grouped->groups[0].path_offsets[0]) == prefix+L"folderinfo.txt"
		&& grouped->groups[0].last_write_filetimes[0] == 132000000000000000ull, "grouped root full path/time lost");
	const size_t groupedGetters = f.completedCalls.at(L"text").size();
	Require(groupedGetters == 2 && f.searches-beforeSearches == 14, "root complete API query/getter work changed");
	std::cout << "ROOTAPIS " << label << " searches=" << f.searches-beforeSearches << " fixed_fullpath=" << getterCount
		<< " source_path_name=" << rootSourceGetters << '+' << childSourceGetters << " grouped_path_name=" << groupedGetters << " preparation_fullpath_probe=1\n";
	f.CheckReleased();
}

} // namespace

#ifndef BMS_PATH_IDENTITY_FIXTURE
int wmain(int argc, wchar_t** argv) {
	try {
		Require(argc == 2, "run test-x64.ps1 with the pinned SDK");
		Fixture fixture(argv[1]);
		QueryBoundaries(fixture);
		QueryFailures(fixture);
		PublicResults(fixture);
		DirectoryIdentity(fixture);
		IdentityOwnership(fixture);
		RootIdentity(fixture, L"C:\\root-identity", L"C:\\root-identity", "normal-control");
		RootIdentity(fixture, L"\\\\server\\share", L"\\\\server\\share", "unc-control");
		RootIdentity(fixture, L"C:", L"C:\\", "drive-root");
		fixture.CheckReleased();
		std::cout << "PASS: SDK getter boundaries, query reuse/failure, packed API results, resource cleanup\n";
		return 0;
	} catch (const std::exception& ex) {
		std::cerr << "FAIL: " << ex.what() << '\n';
		return 1;
	}
}

#else
namespace { std::unique_ptr<Fixture> connectionFixture; }
// 制御exportは試験DLLにだけ存在し、走査とpacked解放は製品の公開exportをそのまま使う。
extern "C" __declspec(dllexport) int __cdecl PathIdentityFixtureBegin(const wchar_t* sdk, const wchar_t* root) {
 try {
  Require(!connectionFixture && sdk && root, "invalid fixture begin");
  connectionFixture = std::make_unique<Fixture>(sdk);
  PrepareIdentityConnection(*connectionFixture, root);
  return 0;
 } catch (...) { connectionFixture.reset(); return 1; }
}
extern "C" __declspec(dllexport) int __cdecl PathIdentityFixtureEnd() {
 try {
  Require(connectionFixture != nullptr, "fixture missing");
  connectionFixture->CheckReleased();
  Require(!g_apiLoaded && g_api.everythingModule == nullptr, "product shutdown missing");
  Require(connectionFixture->searches == 5, "fixture searches incomplete");
  connectionFixture.reset();
  return 0;
 } catch (...) { connectionFixture.reset(); return 1; }
}
#endif
