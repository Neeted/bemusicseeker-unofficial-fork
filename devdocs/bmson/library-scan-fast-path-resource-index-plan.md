# Library Scan Fast Path / Resource Index Simplification Plan

## Summary

通常起動で `startup_ready_operable` が約 40 秒になっている主因は、差分 0 件でも file diff 前に audio/image/movie の full resource scan と resource index 構築を必ず実行していることにある。

最新ログでは PRAGMA は適用済みだが、次が critical path に残っている。

- `init_library phase1_min_load_ms=20597`
- `init_library phase2_scan_maint_ms=18035`
- `everything_scan totalMs=27210`
- `nativeBridgeMs=26212`
- `audioQueryHits=11380435`
- `bridgeRawBufferBytes=331802176`
- `dirhash_build_ms=10379`
  - `resource_lookup_cache_ms=6631`
  - `relative_path_hash_index_ms=3708`
- `added_count=0`
- `deleted_count=0`

差分 0 件では、譜面追加/削除/更新の検出に audio/image/movie resource surface は不要である。まず chart-only diff を行い、差分がある場合だけ resource scan / inline maintenance 用 cache を作る。

また、relative path 対応以降に導入した「隣接ファイル名」と「サブディレクトリ付きファイル名」の特別扱いは整理する。譜面が参照する resource 名は、譜面ファイルのあるディレクトリから見た相対パスとして一貫して扱う。

## Goals

- 差分 0 件の通常起動で full resource scan / resource lookup cache rebuild を避ける。
- `Everything` native bridge の library startup mainline を chart-only diff と resource scan に分離する。
- `DirectoryRelativePathHashIndex` と `DirectoryResourceLookupCache.Entry` の重複を縮小する。
- resource reference の照合 semantics を「chart-relative path」一系統に寄せる。
- 後回し化ではなく、初期化全体から不要な処理を取り除く。

## Non Goals

- `chart_info_hydration` や playlist hydration を operable 後へさらに移すこと。
- resource health の正確性を捨てること。
- 初回起動や差分あり起動で inline maintenance を省略すること。
- native bridge ABI を一度に大きく破壊すること。

## Current Bottleneck

現在の startup file diff は次の順序になっている。

```text
Everything fixed 4-query scan
  chart/audio/image/movie
  native ownership/hash aggregation
  managed materialize
resource indexes build
  BMSDirectoryFileNameHash
  DirectoryResourceLookupCache
  DirectoryRelativePathHashIndex
path diff
  added/deleted/upserted detection
```

このため、差分 0 件でも次を支払う。

- 1100 万件級の audio result transfer
- chart directory owner 探索
- baseName / relativePath / selfOwned hash surface 生成
- `DirectoryResourceLookupCache` materialize
- `DirectoryRelativePathHashIndex` materialize

差分検出に必要なのは chart path set だけなので、resource surface 構築が早すぎる。

## Target Startup Flow

### Startup / FullReinitialize

```text
DB load
chart-only enumeration
chart path diff
  if no diff:
    reuse existing resource indexes
    skip resource scan
    skip DB commit
    publish catalog as unchanged
  if diff exists:
    resource enumeration for affected scope
    resource lookup cache build/update
    lightweight parse
    inline chart_info
    inline maintenance
    chunk commit
    memory catalog / resource index swap
```

### ReloadFileDiff

`ReloadFileDiff` も同じ chart-only diff を入口にする。

```text
ReloadFileDiff
  chart-only enumeration
  diff against in-memory catalog
  if no diff:
    no DB commit
    no resource scan
    no maintenance deferred
  if diff exists:
    resource enumeration for affected scope
    inline chart_info / maintenance
    commit and apply
```

## Phase 1: Chart-only Diff Fast Path

### Key Changes

- `EverythingFileScanner` / fallback scanner に chart-only scan mode を追加する。
- startup prefetch はまず chart-only scan を実行する。
- `BmsLibraryInitializationService.ApplyFileScanDiff(...)` は、resource index 構築前に chart path diff を計算する。
- 差分 0 件の場合:
  - `DirectoryResourceLookupCache.CreateFromScanResult(...)` を呼ばない。
  - `DirectoryRelativePathHashIndex.CreateFromScanResult(...)` を呼ばない。
  - `NextFolderAllFileList` は既存 snapshot を維持するか、chart directories だけの軽量 index にする。
  - `db_commit_chunks=0` のまま終了する。
  - `song_tbl_file_check_breakdown` に `chart_only_scan_ms`, `resource_scan_skipped=true`, `resource_scan_skip_reason=no_file_diff` を出す。

### Acceptance Criteria

- 差分 0 件の通常起動で `audioQueryHits`, `imageQueryHits`, `movieQueryHits` が startup critical path に出ない。
- 差分 0 件の `dirhash_build_ms` が resource lookup / relative path index 構築に比例しない。
- `startup_ready_operable` が 40 秒台から、少なくとも `resource_lookup_cache_ms + relative_path_hash_index_ms` 相当ぶん短縮する。
- 差分あり時の inline maintenance / chart_info commit は従来通り実行される。

### Risks

- 譜面ファイルは変わっていないが resource だけ外部変更されたケースは通常起動 fast path では拾わない。
- このケースは「構成ファイルフルスキャン」や health 修復系で扱う。

## Phase 2: Resource Scan Scope Reduction

Phase 1 では差分あり時に従来 full resource scan を使ってよい。Phase 2 で差分あり時の scope を絞る。

### Key Changes

- 追加/更新譜面の chart directories を affected directories として抽出する。
- affected directories に必要な resource surface だけを作る。
- 削除だけの場合は、既存 cache から該当 directory を remove し、full resource scan を避ける。
- 追加/更新 BMS の inline maintenance は affected resource surface で実行する。

### Acceptance Criteria

- 少数追加時に audio/image/movie scan が library root 全体に比例しない。
- resource cache mutation log に added/removed/replaced directory count が出る。
- 既存の package install / move / regroup 後の resource cache 更新と semantics が揃う。

## Phase 3: Relative Path Semantics Simplification

譜面 resource reference は、すべて chart-relative path として扱う。

この phase は library health だけでなく、導入先推定の candidate discovery / broad filter / final evaluation まで含む。  
resource reference の意味が library 側と install estimation 側でずれると、起動時 index を簡素化しても推定のために basename-only index を残し続けることになるため、同じ phase で semantics を揃える。

### Target Semantics

- `foo.wav` は relative path `foo.wav`。
- `sound/foo.wav` は relative path `sound/foo.wav`。
- `foo.wav` と `sound/foo.wav` は別 key。
- bare filename resource は basename-only ではなく、chart-relative path `foo.wav` として扱う。
- basename-only fallback は、相対パスの代替ではなく、旧 index 欠損時の診断 / 互換 fallback に限定する。
- 隣接ファイルとサブディレクトリファイルを別カテゴリとして特別視しない。

### Key Changes

- `BMSFile` の resource health 判定を relative path key 一系統へ寄せる。
- `ChartResourceSnapshot` の broad filter / final evaluation で、bare filename と path-aware filename を同じ relative path key surface として扱う。
- `ChartResourceSnapshot.EnumerateBroadFilterBaseNameHashes()` を導入先推定の正本から外し、`foo.wav` も relative hash seed として broad filter に流す。
- `ApplyPathAwareBroadFilter(...)` は basename seed と path-aware seed の二系統ではなく、category relative path seed 一系統に寄せる。
- `CandidateResourceView` は final match の正本を `Audio/Visual/MovieRelativePathHashes` に寄せ、`AllBaseNameHashes` / category basename set は legacy fallback / diagnostics / tie-break 用へ縮小する。
- `ResolveCandidateResourceSource(...)` は cache / relative index 欠損時に `BMSDirectoryFileNameHash` の basename-only array へ自動で落ちないようにする。落とす場合は `legacyBasenameFallback=true` のように明示し、auto apply 対象にしない。
- `EvaluateCandidateBasenameOnlyFastPath(...)` は削除、または compatibility mode として隔離する。通常の導入先推定は `EvaluateCandidateRelativeStrict(...)` 相当へ一本化する。
- `IsReferenceMatched(...)` は `reference.IsPathAware` で basename と relative を切り替えず、bare filename も relative hash で照合する。
- `PackageInstallEstimationSnapshot` の `DefinedResources` / `BundledResources` / `SourceCandidateResources` は、target chart の chart directory 基準で materialize する。
- source package に root chart と nested chart が混在する場合、source root-relative surface ではなく、対象 chart directory-relative surface を評価に渡す。
- `MergeCandidateOnly` は `candidate only` の mode semantics を維持するが、candidate surface も destination chart directory-relative key で評価する。
- `selfOwned*` の意味を縮小し、ancestor owner 抑制が必要な箇所だけに閉じる。
- install estimation の broad filter から basename-only 優先の構造を外す。

### Acceptance Criteria

- `foo.wav` と `sound/foo.wav` の混同が起きない。
- relative path 譜面の導入先推定で、basename-only candidate が過剰に残らない。
- bare filename resource だけの譜面でも、導入先推定は relative path `foo.wav` として候補発見 / final evaluation する。
- lookup cache がない candidate が basename-only fallback だけで high confidence / auto apply にならない。
- root chart + nested chart が混在する package で、source root-relative surface と chart-relative surface の key ずれにより正しい候補が落ちない。
- `MergeCandidateOnly` で、basename-only の偶然一致ではなく destination chart-relative resource surface に基づいて候補が評価される。
- health 判定で `File.Exists` fallback が増えない、または増えた箇所がログで説明できる。

### Install Estimation Test Impact

更新対象:

- `BmsLibraryInstallEstimationServiceTests`
  - `basename_fast_path` を期待するテストは relative-only semantics へ更新する。
  - `sound\\bgm1.wav` と `bgm1.wav` の互換一致を期待するテストは、別 key として期待値を修正する。
  - cacheless fallback 系は `legacy_basename_fallback` として明示し、confidence / auto apply の期待を下げる。
  - path-aware broad filter が basename-only candidate を落とすテストは強化する。
  - root chart + nested chart package の source/bundled surface が chart directory-relative で評価されることを追加する。
  - `MergeCandidateOnly` が relative-only candidate evaluation で normal mode と同じ key semantics を使うことを追加する。

観測ログ:

- `estimate_install start`
  - `resourceKeyMode=chart_relative`
  - `basenameFastPath=false`
  - `legacyBasenameFallback=...`
- `estimate_install done`
  - `candidateDirsAfterRelativeFilter=...`
  - `legacyFallbackCandidateCount=...`
  - `autoApplyBlockedByLegacyFallback=...`

## Phase 4: Index Consolidation

`DirectoryRelativePathHashIndex` は `DirectoryResourceLookupCache.Entry` とほぼ同じ情報を持っているため、段階的に統合する。

### Key Changes

- `ResourceHealthLookupContext` は `DirectoryResourceLookupCache` を正本にする。
- `DirectoryRelativePathHashIndex` 参照箇所を `DirectoryResourceLookupCache.Entry` へ移す。
- `DirectoryRelativePathHashIndex` はテスト用 shim を経て削除する。
- `song_tbl_file_check_cache_counts` から重複した relative index counts を整理する。

### Acceptance Criteria

- startup で relative path index の二重 materialize が消える。
- `resource_lookup_cache_ms` と `relative_path_hash_index_ms` の合算が単一 cache build として観測される。
- install estimation / maintenance / file operation tests が同じ cache 正本で通る。

## Phase 5: Native Payload Reduction

Phase 1-4 で managed 側 semantics を固めた後、native bridge contract を縮小する。

### Key Changes

- chart-only scan request / result を正式 surface にする。
- resource scan result から不要な `baseName` / `selfOwned` / ancestor owner payload を減らす。
- native / managed の ABI version を明示し、古い DLL と混在した場合は fallback する。

### Acceptance Criteria

- `bridgeRawBufferBytes` が現在の 331MB 級から減る。
- `nativeBridgeMs` が audio/image/movie result size と owner aggregation に比例しにくくなる。
- fallback scanner と native scanner の result parity がテストで固定される。

## Logging

追加・変更したいログ:

- `chart_only_scan start/done`
  - `charts=`
  - `dirs=`
  - `elapsedMs=`
  - `nativeBridgeMs=`
- `file_diff_fast_path`
  - `resourceScanSkipped=`
  - `reason=no_file_diff`
  - `deleted=`
  - `added=`
  - `bmsonDeleted=`
  - `bmsonUpserted=`
- `resource_scan start/done`
  - `scope=full|affected`
  - `affectedDirs=`
  - `audioQueryHits=`
  - `imageQueryHits=`
  - `movieQueryHits=`
  - `resourceLookupCacheMs=`
- `resource_index_consolidation`
  - `directoryResourceEntries=`
  - `relativeIndexBuilt=false`

## Test Plan

- chart-only diff
  - 差分 0 件で resource scan が呼ばれない。
  - 差分 0 件で DB commit chunk が 0。
  - 差分 0 件で既存 memory catalog / resource cache が維持される。
- added / deleted / updated
  - 追加 BMS で resource scan が走り、inline maintenance が作られる。
  - 削除 BMS で DB / memory / maintenance / resource cache から消える。
  - bmson upsert で従来の bmson parse / maintenance が維持される。
- relative path semantics
  - `foo.wav` と `sound/foo.wav` が別 key。
  - `sound/foo.wav` を要求する譜面が `foo.wav` だけの候補に一致しない。
  - bare `foo.wav` は `foo.wav` relative key として一致する。
- scanner parity
  - Everything と fallback の chart-only result が一致する。
  - affected resource scan と full resource scan の対象 directory result が一致する。

## Operational Notes

- `ReloadFileDiff` は外部ファイル操作による譜面追加/削除/移動差分検出が目的なので、chart-only fast path と相性がよい。
- resource だけを外部変更した可能性まで常時拾う必要はない。
- resource health の再評価は maintenance の明示操作、構成ファイルフルスキャン、または差分あり時の inline maintenance に寄せる。
- 初回空 DB は差分あり扱いになるため、Phase 1 だけでは初回 scan は大きく短縮しない。Phase 2 以降で初回/大量追加の resource scan scope と payload を削る。
