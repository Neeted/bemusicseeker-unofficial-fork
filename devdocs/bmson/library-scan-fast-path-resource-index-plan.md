# Startup Initialization and Install Readiness Optimization Plan

## Summary

この計画の目的は、特定のログ区間を短く見せることではない。目的は次の 2 つを同時に満たすこと。

- 起動から譜面の導入先推定と導入が可能になるまでの時間を短縮する。
- background を含む初期化全体の完了時間を短縮する。

導入先推定には所持譜面 catalog だけでなく、導入先候補 directory の resource index が必要である。したがって、通常起動で chart-only diff を先行して resource scan / index 構築を省く方針は採用しない。起動時は現行と同じくファイル列挙を正本とし、その列挙結果から導入先推定に必要な resource index を作る。

一方で、現状は file diff 自体よりも、DB materialize、native scan payload、resource index の重複構築、background hydration が支配的である。最適化の主対象はここに置く。

## Phase 0 Log Findings

2026-05-05 08:23 頃の Phase 0-1 / Remaining Before Phase 2 実装後ログでは、旧 chart-only 実験ログと旧 `startup_ready_installable` は出ていない。

導入可能までの critical path は概ね次の通り。

- DB load / materialize
  - `phase1_min_load_ms=19694`
  - `song_tbl_load_ms=18933`
  - `song_read_ms=8829`
  - `maintenance_read_ms=3967`
- file enumeration / resource index
  - `song_tbl_file_check_ms=11348`
  - `nativeBridgeMs=25249`
  - `bridgeRawBufferBytes=331802176`
  - `dirhash_build_ms=10490`
  - `resource_lookup_cache_ms=6704`
  - `relative_path_hash_index_ms=3751`
- diff / apply
  - `diff_ms=626`
  - `apply_ms=40`
  - `db_commit_chunks=0`
- readiness
  - `startup_install_estimation_ready elapsedMs=37971`
  - `startup_install_ready elapsedMs=37971`
  - `startup_ready_operable elapsedMs=39914`
  - `startup_initialization_complete elapsedMs=99997`
  - `startup_background_summary queued=10 started=10 completed=10 failed=0`

`init_library` の latest log では `wait_continuation_start_ms=0`、`wait_continuation_signal_ms=0`、`wait_continuation_tasks_ms=0` で、continuation wait は今回の critical path ではない。次の主対象は `song.db` load / materialize と file enumeration / resource index build である。

初期化全体の後半では次が重い。

- `playlist_entries_hydration totalMs=31501`
- `chart_info_hydration totalMs=17103`。
  - 内訳として `chart_info_hydration totalMs=12337`、`chart_info_backfill candidate_summary_done elapsedMs=4733` かつ `candidates=0`。
- `reverse_lookup_warmup_deferred elapsedMs=16586`
- `ranking_refresh_deferred elapsedMs=53007`

このため、差分検出だけを速くしても主目的には届かない。導入可能までを短縮するには DB load と resource index 構築を削る必要があり、初期化全体を短縮するには background hydration / warmup の重複 work も削る必要がある。

## Readiness Model

### Install Estimation Ready

導入先推定を開始してよい状態。

必須:

- 所持譜面 catalog
  - BMS / BMSON path、hash、timestamp、folder、installed membership。
- destination resource index
  - audio / image / movie resource の directory-relative lookup。
  - chart-relative resource reference 評価に必要な relative path key。
  - candidate directory enumeration。
- pending package state
  - pending package 一覧。
  - source package resource surface は復元済み、または推定開始時に構築可能。
- install DB / file operation の排他状態
  - 導入処理が安全に DB write / file move を開始できること。

不要:

- playlist entry hydration。
- score / ranking refresh。
- chart_info hydration / backfill。
- deferred maintenance。
- reverse lookup warmup。

現行コード確認では、起動時の pending estimate queue は
`CatalogLoaded && DestinationResourceIndexReady && PendingPackagesRestored`
を満たすまで開始しない。これは妥当である。導入先推定本体は
`BMSLibrary.EvaluateInstallEstimation()` から
`BmsLibraryInstallEstimationService.EstimateInstallationDirectory(...)` へ入り、
所持 catalog、`BMSDirectoryFileNameHash`、`DirectoryResourceLookupCache`、
`DirectoryRelativePathHashIndex`、pending package の source surface を使う。

一方、実際の導入開始は推定済み `instl_dst` と pending package state を消費する段階であり、
導入開始時に resource index を再構築する必要はない。
ただし、推定が未完了の pending package を安全に導入可能扱いにしない。

### Install Ready

実際の導入操作を開始してよい状態。

Phase 0-1 時点では `Install Ready` は `Install Estimation Ready` 直後に出るログ境界であり、
UI enable 条件や lock 構造をまだ変更していない。Phase 2 以降で readiness を早める場合は、
このログ境界を実際の導入開始条件へ寄せる。

最終的には `Install Estimation Ready` に加えて、次を満たす。

- pending package list が UI/model で確定している。
- file move と DB write が同じ排他方針で実行できる。
- 導入後の catalog、resource index、maintenance、chart_info の更新境界が明確である。

### Initialization Complete

background を含む初期化が完了した状態。

`Install Ready` より後に完了してよいが、計画上は短縮対象であり、単に後回しにして済ませない。

## Invariants

- 通常起動の destination resource index は、現在のファイル列挙結果から作る。
- 所持譜面差分が検出された場合、前回起動時の resource 一覧は正本にしない。
- 導入先推定に必要な resource index がない状態で `Install Estimation Ready` にしない。
- `ReloadFileDiff` は起動中の memory catalog / memory resource index を正本にできるが、通常起動の代替にはしない。
- 互換名目の旧経路、テストからしか呼ばれない処理、使わないログは残さない。
- 新旧実装を長期併存させず、移行時は call site / tests / docs を現行仕様へ置き換える。

## Non Goals

- `startup_ready_operable` だけを短く見せること。
- 導入可能に必要な DB load / resource index 構築を未完了のまま ready にすること。
- 通常起動の main path に chart-only resource-skip を入れること。
- destination resource index を前回起動の永続 data から復元すること。
- playlist / ranking / score / chart_info を install readiness の条件に戻すこと。

## Phase 0: Re-baseline And Metrics

revert 後の状態を基準にし、導入可能までと初期化完了までを別々に観測する。Phase 0-1 の初回実装では、導入 readiness の token とログを追加し、完了位置は従来の `startup_ready_installable` と同じ場所に置く。

### Key Changes

- `startup_install_estimation_ready` を追加済み。
  - catalog / resource index / pending package state が揃った時点。
- `startup_install_ready` を追加済み。
  - 手動導入を安全に開始できる時点。
- `startup_initialization_complete` を追加済み。
  - startup progress の expected background phase がすべて完了し、scheduler queue が空になった時点。
- `RunInitialize` の continuation wait を明示ログ化済み。
  - `wait_continuation_start_ms`: continuation task 起動前の semaphore wait。
  - `wait_continuation_signal_ms`: phase 後の semaphore signal wait。
  - `wait_continuation_tasks_ms`: `Task.WaitAll` による continuation task 完了待ち。
  - latest log ではいずれも 0ms のため、次フェーズの短縮対象からは外す。
- `startup_background_summary` を追加済み。
  - startup scheduler 経由 task と BMSLibrary 直実行 task の queue / start / complete / failed / elapsed をまとめる。
  - reverse lookup warmup は progress phase として tracking し、summary に含める。
- `startup_ready_installable` は導入 readiness と意味が重複するため削除済み。
- 旧実験ログ名や旧 fast path 用ログは、実装が存在しないなら残さない。

### Remaining Before Phase 2

実装済み。Phase 2 に入る前の観測点として、次を現行仕様に固定した。

- `startup_initialization_complete`: background を含む初期化完了 elapsed。
- `startup_background_summary`: playlist / chart_info / ranking / reverse lookup / maintenance などの startup background task summary。
- `InstallReady`: 現時点ではログ境界であり、UI enable / file operation safety の実条件変更は未実装。

### Acceptance Criteria

- 導入可能までの elapsed と、初期化完了までの elapsed を別々に比較できる。
- Phase 0 log で、DB load、resource index、background hydration の寄与が分かる。
- 以後の phase は、片方の metric だけを改善してもう片方を悪化させた場合に検出できる。

## Phase 1: Install Readiness Contract

導入先推定 / 導入の readiness を `InitializedAll` や UI operable から分離する。初回実装では UI enable 条件と lock 構造は変えず、model 側の readiness token とログだけを固定する。

### Key Changes

- internal readiness state を定義済み。
  - `CatalogLoaded`
  - `DestinationResourceIndexReady`
  - `PendingPackagesRestored`
  - `InstallEstimationReady`
  - `InstallReady`
  - `InitializationComplete`
- pending estimate queue は `CanStartInstallEstimation()` を通して開始する。
- 手動推定 / 手動導入 UI の enable 条件切り替えは次単位に残す。
- playlist、score、ranking、chart_info、deferred maintenance は install readiness blocker にしない。
- readiness token は一方向遷移にし、後続 task の失敗で意味が曖昧にならないようにする。

### Acceptance Criteria

- pending estimate が playlist / score / chart_info background task を待たない。
- resource index がない場合は推定も導入も enable されない。
- `startup_ready_installable` より意味が明確な導入用 readiness log が得られる。

## Phase 2: Startup DB Projection Reduction

起動 early path で読む DB projection を、導入可能に必要な情報へ絞る。これにより導入可能までを短縮し、不要な再 materialize を消して初期化全体も短縮する。

### Phase 2A Current Implementation

実装済み。

- 起動 critical path の DB load は catalog projection になった。
  - 読むもの: `song`, `bmson_song`, `chart_digest_map`, folder normalization に必要な情報。
  - 読まないもの: `maintenance` 全件、`chart_info` 全件、score / ranking、playlist entry。
- `maintenance` 全件 hydration は `maintenance_hydration` background task へ分離した。
  - `maintenance_hydration` は同じ `BMSFile` / `bmson_song` instance へ in-place apply する。
  - hydration 後に warning / health projection を更新する。
  - `installable_maintenance_deferred` は `maintenance_hydration` と `chart_info_hydration` の完了後に開始する。
- install readiness は `CatalogLoaded && DestinationResourceIndexReady && PendingPackagesRestored` のまま維持する。
  - `maintenance_hydration` は `startup_install_estimation_ready` の blocker ではない。
- ログは `song_tbl_load_projection projection=catalog ...` と `maintenance_hydration start/done ...` へ分離した。
  - catalog load の `song_tbl_load_io` には `maintenance_read_ms` を出さない。

2026-05-05 09:09 頃の Phase 2A 実測では次の状態になった。

- `song_tbl_load_projection projection=catalog readMs=8596 materializeMs=8590 rows=208979 bmsonRows=1051 chartDigestRows=208870`
- `startup_install_estimation_ready elapsedMs=35961`
  - Phase 0 の `37971ms` から約 2 秒短縮。
  - `maintenance_hydration` はこの後に queue されており、install readiness の blocker から外れている。
- `maintenance_hydration done ... readMs=2939 materializeMs=2932 mapBuildMs=169 applyMs=14697 elapsedMs=17814`
  - DB read / materialize は critical path から外れた。
  - 一方で in-place apply と warning / health projection 更新が background 側で大きい。
- `installable_maintenance_deferred` は `dependency=chart_info_hydration,maintenance_hydration` で開始し、hydration 後の target は `maintenanceChecked=3` まで縮小した。

このため Phase 2A の readiness 短縮は想定どおりだが、`startup_initialization_complete` は `elapsedMs=103325` で Phase 0 実測より短縮していない。次は background 側の duplicate / no-op work を削る必要がある。

Phase 2 の前提は、partial `BMSFile` を UI 正本として出さないことである。
install-ready projection を導入する場合は、次のどちらかを実装単位で明確に選ぶ。

- install readiness 専用 DTO / index を作り、UI `BMSFiles` は display hydration 後に現行と同じ full model として公開する。
- 既存 `BMSFile` を正本にする場合は、early projection object を後続 hydration で in-place に埋め、同じ DB row を再 materialize しない。

どちらの場合も、early object を捨てて full object を作り直す二重 materialize は行わない。

### Key Changes

- install-ready catalog projection を定義する。
  - chart identity: path、parent path、folder id、BMS / BMSON 種別。
  - installed membership 判定: md5、sha256、last write timestamp、DB row identity。
  - install estimation の代表 metadata に必要な title / artist / subartist / genre / level などの最小 fields。
  - package restore / install plan に必要な package membership と pending install row identity。
- maintenance は install readiness の必須条件にしない。
  - resource health warning、WAV/BGA 率、encoding 補完は導入先推定 / 導入開始の blocker ではない。
  - pending source baseline は pending package source surface と chart resource refs から評価するため、DB `maintenance` 全件 materialize に依存させない。
- display-only fields、playlist-only fields、ranking / score、chart_info owner apply 用 data は early projection から外す。
- full model hydration が必要な場合も、early projection の object を捨てて再構築しない。
- `song_tbl_load_io` を projection 単位で出す。
  - `projection=install_ready`
  - `projection=display`
  - `projection=background`

### Acceptance Criteria

- `song_read_ms` / `song_materialize_ms` / `maintenance_read_ms` の early path が下がる。
- display hydration 後の UI 表示、sort、filter、warning projection は現行と一致する。
- 同じ DB rows を early path と background path で重複 materialize しない。
- `InstallEstimationReady` は playlist / score / chart_info / maintenance hydration を待たず、resource index と pending package state が揃った時点で出る。
- partial model が UI に出て、空 title / 空 artist / stale warning で表示される状態を作らない。

## Phase 3: Enumeration-Based Resource Index Consolidation

通常起動では file enumeration を正本として destination resource index を作る。その前提で、重複 index と重複 materialize を削る。

### Phase 3A Current Implementation

実装済みの範囲は「構築境界の統合」である。

- file enumeration result から `LibraryResourceIndex` を 1 回作り、その中に既存互換 view を保持する。
  - `BMSDirectoryFileNameHash`
  - `DirectoryResourceLookupCache`
  - `DirectoryRelativePathHashIndex`
- 旧 index を別々の top-level build step として作る流れはやめ、`resource_index_build source=enumeration ...` を正本ログにした。
- `song_tbl_file_check_breakdown` / `bms_scan` の内訳は `resource_index_build_ms` 系へ寄せた。
- Phase 3A では resource matching semantics は変更しない。
  - basename / relative path の既存互換 view は `LibraryResourceIndex` 内部で維持する。

未完了として次単位に残すもの:

- install estimation / resource health / file operation の引数を `LibraryResourceIndex` API へ完全移行する。
- 互換 view が不要になった時点で旧 index class と旧 tests を削除する。
- chart-relative semantics cleanup は Phase 5 で行う。

### Key Changes

- destination resource index の正本を 1 つに定義する。
  - directory。
  - category。
  - chart-relative / directory-relative resource key hash。
  - candidate directory membership。
- `DirectoryResourceLookupCache` と `DirectoryRelativePathHashIndex` を別々に構築しない。
- `BMSDirectoryFileNameHash` は正本から得られる view にするか、必要最小限の candidate directory set に置き換える。
- install estimation、resource health、file move、package install、folder rename は同じ resource index API を使う。
- call site / tests を同時に現行 API へ置き換え、使われなくなった index API は削除する。

### Acceptance Criteria

- `resource_lookup_cache_ms + relative_path_hash_index_ms` 相当の重複構築が消える。
- `dirhash_build_ms` の主成分が説明でき、不要な owner / relative prefix rebuild が消える。
- install estimation / maintenance / file operation の結果が現行と一致する。
- memory peak が下がる。

## Phase 4: Native Enumeration Payload Reduction

初回起動、通常起動、root 変更時に full file enumeration は必要である。その前提で native bridge payload と managed materialization を削る。

### Key Changes

- native bridge から managed に渡す payload を canonical resource index 生成に必要な形へ寄せる。
- raw full path transfer を減らし、root id + relative path / directory id のような compact representation を検討する。
- audio / image / movie の group / assign / merge / pack を重複しない流れにする。
- `__all__` 的な総列挙を main path に入れない。
- fallback scanner も同じ canonical resource index builder を通す。

### Acceptance Criteria

- `bridgeRawBufferBytes` が下がる。
- `nativeBridgeMs`、managed decode、managed materialize が下がる。
- 同じファイル集合から同じ install estimation / health 結果が得られる。
- 初期化全体の elapsed が下がる。

## Phase 5: Chart-Relative Resource Semantics Cleanup

譜面の resource reference はすべて chart-relative path として扱う。隣接ファイル名と subdirectory file を別ルールで扱わない。

### Target Semantics

- `foo.wav` は chart-relative path `foo.wav`。
- `sound/foo.wav` は chart-relative path `sound/foo.wav`。
- `foo.wav` と `sound/foo.wav` は別 key。
- source package 側も destination library 側も同じ key 体系で評価する。

### Key Changes

- BMS / BMSON health 判定を chart-relative key に統一する。
- install estimation の broad filter / final evaluation を chart-relative key で統一する。
- basename-only の alternate correctness path は削除する。
- path-aware resource がある package で、無関係な basename match が high confidence にならないようにする。
- `install-estimation-relative-path-foundation.md` と用語を揃え、古い挙動を前提にした tests を置き換える。

### Acceptance Criteria

- `foo.wav` と `sound/foo.wav` の混同が起きない。
- relative path resource を持つ譜面の導入先推定で、basename だけの候補が残らない。
- bare filename resource だけの譜面は `foo.wav` という relative path として評価される。
- source / destination / maintenance の resource matching 結果が同じ semantics になる。

## Phase 6: File Diff Apply Reduction

diff 自体は軽いが、差分 0 件時の catalog apply / property notification / playlist invalidation などの後処理は削れる。resource index は通常起動では列挙から作るが、不要な model replacement は避ける。

### Key Changes

- diff 0 件では `BMSFiles` / `BmsonSongs` の list instance を無意味に差し替えない。
- diff 0 件では catalog property changed、playlist reference apply、summary dirty、view rebuild を発生させない。
- diff あり時のみ、追加 / 更新 / 削除対象の parse、inline chart_info、inline maintenance、DB commit を行う。
- 削除 / rename / move では canonical resource index mutation API を使う。

### Acceptance Criteria

- diff 0 件通常起動で resource index は作るが、catalog apply 後処理が最小化される。
- diff 0 件 `ReloadFileDiff` は DB reload / full UI rebuild / playlist work を行わない。
- diff あり時の DB / memory / warning / playlist 所持状態は現行通り収束する。

## Phase 7: ReloadFileDiff Affected-Scope Refresh

起動時と異なり、`ReloadFileDiff` では memory catalog / memory resource index が正本として存在する。外部ファイル操作差分だけを反映する軽量経路にする。

### Key Changes

- diff 0 件では何も差し替えない。
- added / updated chart の parent directory を affected scope として resource refresh する。
- deleted-only は canonical resource index から該当 directory / resource を mutation で除去する。
- package install、folder move、folder rename も同じ resource index mutation API を使う。
- full reinitialize は従来通り DB 再読込と full enumeration を行う別操作として残す。

### Acceptance Criteria

- `ReloadFileDiff` no-op が短時間で終わる。
- 少数追加 / 削除で library root 全体の resource index rebuild が走らない。
- 外部移動による削除 + 追加が memory catalog / DB / resource index に反映される。

## Phase 8: Background Initialization Work Reduction

導入可能後に走る task も、初期化全体の完了時間として短縮する。後回しにするだけでなく、重複 read / materialize / no-op scan を削る。

### Phase 8A / 8B Current Implementation

実装済み。

- `maintenance_hydration` を startup background task として明示し、`startup_background_summary` の対象にした。
- `installable_maintenance_deferred` は `maintenance_hydration,chart_info_hydration` の両方を dependency として待つ。
- `reverse_lookup_warmup_deferred` は `DirectoryResourceLookupCache` の reverse lookup が既に full warmup 済みなら queue しない。
- `maintenance_tbl_check_deferred` は廃止した。
  - orphan maintenance cleanup は `maintenance_hydration` が読み込んだ `maintenance` key と current owner path set の差分から算出する。
  - cleanup は stale path がある場合だけ `DeleteMaintenanceRows(...)` の短い transaction で実行する。
  - stale 判定と delete は同じ BMS catalog lock の内側で行い、判定後の catalog mutation と競合させない。
  - `maintenance_hydration done` は `cleanupDeleted`, `cleanupMs`, `ownerPathCount`, `stalePathCount` を出す。
- `chart_info_hydration` は owner ごとの current 判定を集計する。
  - current `chart_info` または current parse failure が全 owner に揃っている場合、`chart_info_backfill candidate_summary` を呼ばず `reason=hydration_all_current` で skip する。
  - candidate が 1 件でもある場合は従来どおり candidate summary / backfill 経路へ進む。

Phase 2A 後の実測では、初期化完了を遅らせている候補は次の通り。

- `ranking_refresh_deferred lastMs=47962`
- `playlist_entries_hydration lastMs=26865`
- `maintenance_tbl_check_deferred lastMs=18147`
- `maintenance_hydration lastMs=17820`
- `chart_info_hydration lastMs=15458`
- `reverse_lookup_warmup_deferred lastMs=12633`

Phase 8B で `maintenance_tbl_check_deferred` は `maintenance_hydration` へ統合済み。次回実測では `startup_background_summary` の queued count が 1 件減り、`maintenance_tbl_check_deferred` が出ないことを確認する。

2026-05-05 10:09 の Phase 8B 実測では、統合自体は想定どおり動作している。

- `startup_install_estimation_ready elapsedMs=38926`
- `startup_ready_operable elapsedMs=40209`
- `startup_initialization_complete elapsedMs=103890`
- `startup_background_summary queued=10 started=10 completed=10 failed=0`
- `maintenance_tbl_check_deferred` は出ていない。
- `chart_info_hydration done ... ownerCount=210030 currentChartInfoOwners=210006 currentParseFailureOwners=24 backfillCandidateOwners=0 totalMs=16303`
- `chart_info_backfill skipped reason=hydration_all_current ... candidates=0`
  - `candidate_summary_start` は出ていない。
- `maintenance_hydration done ... readMs=3671 materializeMs=3655 mapBuildMs=170 applyMs=18447 cleanupDeleted=0 cleanupMs=0 ownerPathCount=210030 stalePathCount=0 elapsedMs=22308`
- `installable_maintenance_deferred done ... maintenanceChecked=3 ... deferred_ms=704`

Phase 8B により、重複していた maintenance table check と chart_info backfill candidate summary は削れた。一方で `startup_initialization_complete` は Phase 2A 実測 `103325ms` とほぼ同等で、短縮はまだ支配的ではない。今回の実測では、残る主要 background cost は `ranking_refresh_deferred=43091ms`、`maintenance_hydration=22314ms`、`playlist_entries_hydration=17552ms`、`chart_info_hydration=16398ms`、`reverse_lookup_warmup_deferred=14943ms` である。次の実装単位では `maintenance_hydration applyMs=18447` を直接削るか、ranking / playlist / reverse lookup の no-op / materialize cost を削る必要がある。

### Phase 8C Current Implementation: Ranking Cache Refresh Reduction

実装済み。

- `setRankingScore()` / `RefreshRankingScoresFromCache()` を DB ranking apply と XML delta refresh に分離した。
  - `ir_data` は `lr2id` 条件付きで DB から 1 回読む。
  - `irDataByHash`, `scoreByHash`, `filesByHash` を作り、DB row / score / file lookup の全件線形探索をやめた。
  - XML cache は file mtime と tail timestamp で差分確認し、必要な file だけ full parse する。
  - XML parse / delta 判定中に `BMSScores` / `BMSScore` を mutate せず、最後に単一 pass で in-memory apply する。
  - DB upsert は更新 row がある場合だけ 1 回行う。
- `setRankingScore()` の lock 範囲を縮小した。
  - DB read / XML enumerate / XML parse は `BMSScores` / `BMSFiles` lock 外で行う。
  - 最終 apply 時だけ `rwlockBMSScores` writer と `rwlockBMSFiles` reader を取る。
- startup cache refresh 中の明示 `GC.Collect()` と旧 `IR CACHE` trace は削除した。
- `ranking_cache_refresh done` と `ranking_refresh_deferred done` に内訳を追加した。
  - `dbReadMs`, `dbRows`, `indexBuildMs`, `cacheFiles`, `xmlCheckMs`, `reloadTargets`, `xmlReloadMs`, `dbApplyCount`, `xmlApplyCount`, `upsertRows`, `upsertMs`, `offlineEstimateXmlLoads`
  - `irScoreMs`, `cacheMs`, `elapsedMs`

2026-05-05 12:16 の Phase 8C 実測では、ranking cache refresh は想定どおり大きく短縮した。

- `ranking_cache_refresh done elapsedMs=2906 dbReadMs=280 dbRows=17708 indexBuildMs=4 cacheFiles=17950 xmlCheckMs=393 reloadTargets=99 xmlReloadMs=63 dbApplyCount=17708 xmlApplyCount=0 upsertRows=0 upsertMs=0 offlineEstimateXmlLoads=0`
- `ranking_refresh_deferred done version=1 irScoreMs=10970 cacheMs=2916 elapsedMs=13888`
- `score_snapshot_load completed reason=refresh_ranking_cache ... buildMs=4`

Phase 8B 実測の `ranking_refresh_deferred=43091ms` と比べると、ranking refresh 全体は約 29 秒短縮した。cache XML がほぼ変わっていない通常起動では、XML full parse / DB upsert がほぼ発生しないという前提どおりである。

残る ranking cost は主に `irScoreMs=10970`、つまり `updateLR2IRScoreTable()` による LR2IR player score XML 取得、`ir_score` replace、`BMSScores` merge、score snapshot refresh である。`setRankingScore()` 側は現時点では支配的ではないため、次に ranking を触る場合は `updateLR2IRScoreTable()` の no-op 判定 / fetch policy / DB replace 条件を別 phase として扱う。

同じ実測での初期化全体は次の状態。

- `startup_initialization_complete elapsedMs=93408`
- `startup_background_summary queued=10 started=10 completed=10 failed=0`
- major background:
  - `playlist_entries_hydration lastMs=18419`
  - `maintenance_hydration lastMs=16137`
  - `reverse_lookup_warmup_deferred lastMs=16001`
  - `ranking_refresh_deferred lastMs=13888`
  - `chart_info_hydration lastMs=13591`

この結果、Phase 8C 後は ranking cache refresh ではなく、playlist / maintenance / reverse lookup / chart_info が初期化全体短縮の主対象になった。

未完了として次単位に残すもの:

- `maintenance_hydration` の apply を chunk / aggregate notification 化し、1 件ごとの高コスト warning refresh を避ける。
- `chart_info_hydration` の DB count / version による no-op skip。
- playlist entries hydration の同一 startup 内二重 hydrate / presentation rebuild 削減。
- `updateLR2IRScoreTable()` の no-op 判定 / DB replace 条件整理。

## Next Implementation Unit: Phase 8D / 2B

次に進むべき単位は、install readiness のさらなる前倒しよりも、初期化全体を悪化させている background duplicate work の削減である。Phase 8C の ranking cache refresh reduction は完了したため、次の主対象は maintenance / playlist / reverse lookup / chart_info のいずれかに絞る。

### Phase 8D: Maintenance Apply / Hydration Micro Reduction

- `maintenance_hydration` の apply を chunk / aggregate notification 化する。
- warning / health projection の row ごとの property chain を抑え、UI rebuild をまとめる。
- orphan cleanup は Phase 8B の統合済み経路を維持し、別 task を再導入しない。

### Phase 2B: Catalog Load Micro Reduction

- `song` materialize 後の normalize loop `1480ms` と `crcRecalculated=19` を確認し、DB write 不要時の normalize / CRC 再計算をさらに絞る。
- `song_tbl_load_projection` は維持し、`maintenance` と `chart_info` を critical path に戻さない。
- `startup_install_estimation_ready` の比較対象は Phase 2A 実測 `35961ms` とする。

### Phase 8E: Chart Info Hydration No-op Reduction

- `chart_info_hydration` は DB count / schema version / hydrated index state から no-op 判定できる範囲を増やす。ただし session index が未 hydrated の通常起動では必要な load と owner apply は維持する。

### Phase 8F: Ranking Score Table No-op Reduction

- `updateLR2IRScoreTable()` の network fetch / DB replace / score merge を対象にする。
- LR2IR player score XML の取得結果が前回 DB 内容と同一なら、`ir_score` table replace と `BMSScores` merge を skip できるようにする。
- 実装する場合も `setRankingScore()` の cache delta refresh は維持し、ranking cache と score table の責務を混ぜない。

### Key Changes

- playlist entries hydration
  - 必要 projection を整理し、summary / detail / reference apply で同じ rows を重複 materialize しない。
  - 非表示時は heavy presentation rebuild を避けるが、data load 自体の重複をなくす。
- chart_info hydration / backfill
  - `candidates=0` を確認するためだけの高コスト summary を避ける。
  - version / count / dirty marker で no-op を判定できる場合は DB full scan をしない。
- reverse lookup warmup
  - 初期化完了を長引かせる全量 eager build を見直し、必要 surface だけを build する。
  - build する場合も canonical resource index から重複なく作る。
- ranking refresh
  - network / DB / materialize の内訳を分け、初期化完了 metric で観測する。
  - no-op refresh を短くする。
- maintenance deferred
  - inline maintenance 済み / current row 済みを再処理しない。

### Acceptance Criteria

- `startup_initialization_complete` が短縮する。
- playlist、chart_info、ranking、reverse lookup の elapsed と rows が task 別に説明できる。
- memory peak が下がる。
- 導入可能までの短縮が、background 全体時間の悪化で相殺されない。

## Logging

必要なログだけを残す。

- `startup_install_estimation_ready`
  - `elapsedMs`
  - `catalogRows`
  - `resourceIndexReady`
  - `resourceIndexSource=enumeration|memory|affected_refresh`
  - `pendingPackages`
- `startup_install_ready`
  - `elapsedMs`
  - `pendingPackages`
- `startup_initialization_complete`
  - `elapsedMs`
  - `playlistMs`
  - `chartInfoMs`
  - `rankingMs`
  - `reverseLookupMs`
  - `maintenanceMs`
- `resource_index_build`
  - `source=enumeration|memory|affected_refresh`
  - `directories`
  - `resources`
  - `buildMs`
  - `payloadBytes`
- `song_tbl_load_projection`
  - `projection=install_ready|display|background`
  - `readMs`
  - `materializeMs`
  - `rows`
- `ranking_cache_refresh`
  - `dbReadMs`
  - `dbRows`
  - `cacheFiles`
  - `xmlCheckMs`
  - `reloadTargets`
  - `xmlReloadMs`
  - `dbApplyCount`
  - `xmlApplyCount`
  - `upsertRows`
  - `offlineEstimateXmlLoads`
- `ranking_refresh_deferred`
  - `irScoreMs`
  - `cacheMs`
  - `elapsedMs`

旧実装が存在しないログ、テスト専用のログ、判断に使わないログは追加しない。

## Test Plan

- readiness
  - `InstallEstimationReady` は catalog、resource index、pending package state が揃うまで出ない。
  - playlist / score / chart_info / ranking 未完了でも推定は開始できる。
  - resource index 不在では推定 / 導入が enable されない。
- DB projection
  - install-ready projection で導入先推定に必要な fields が揃う。
  - display hydration 後に既存 UI 表示が一致する。
  - early projection と background projection が同じ rows を無駄に二重 materialize しない。
- resource index
  - file enumeration から canonical resource index が作られる。
  - install estimation / maintenance / file operations が canonical resource index で同じ結果になる。
  - duplicate index build が残っていないことを resource tests で確認する。
- chart-relative semantics
  - `foo.wav` と `sound/foo.wav` が別 key。
  - source package と destination library で同じ key semantics になる。
  - basename-only の alternate path を前提にした tests が残らない。
- file diff / reload
  - diff 0 件で catalog notification / playlist reference apply / DB commit が発生しない。
  - diff ありで added / deleted / moved charts が DB / memory / resource index へ反映される。
  - `ReloadFileDiff` は no-op 時に full reinitialize 相当の処理を呼ばない。
- background
  - playlist / chart_info / ranking / reverse lookup の no-op path が短い。
  - `startup_initialization_complete` が全 background task 完了後に出る。

## Operational Notes

- 通常起動はファイル列挙から destination resource index を作る。
- 所持譜面差分がある場合も、列挙結果を正本として catalog / resource index を収束させる。
- `ReloadFileDiff` は起動中の memory 正本を使えるため、通常起動とは別の軽量化を行う。
- 導入可能までを短くするために無関係 task は blocker から外すが、初期化全体の短縮対象から外さない。
- 計画の各 phase は、不要になったコード、テスト、ログを同時に削除して完了とする。
