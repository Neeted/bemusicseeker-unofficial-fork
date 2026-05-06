# Startup Initialization and Install Readiness Optimization Plan

## Summary

この計画の目的は、特定のログ区間を短く見せることではない。目的は次の 2 つを同時に満たすこと。

- 起動から譜面の導入先推定と導入が可能になるまでの時間を短縮する。
- background を含む初期化全体の完了時間を短縮する。

導入先推定には所持譜面 catalog だけでなく、導入先候補 directory の resource index が必要である。したがって、通常起動で chart-only diff を先行して resource scan / index 構築を省く方針は採用しない。起動時は現行と同じくファイル列挙を正本とし、その列挙結果から導入先推定に必要な resource index を作る。

一方で、現状は file diff 自体よりも、DB materialize、native scan payload、resource index の重複構築、background hydration が支配的である。最適化の主対象はここに置く。

2026-05-05 時点で方針を再整理する。`reverse_lookup_warmup_deferred` は「初期化完了の対象から外してログを短く見せる」対象ではない。relative path 対応以前は、この種の deferred warmup なしでも、導入先推定に必要な resource index まで含めて 30 秒程度で初期化できていた。目標は、reverse lookup 相当の情報も含めて初期化全体を短縮することである。

したがって次の主方針は、C# 側で basename index / relative index / reverse lookup を後段で組み立てる構造をやめ、native scan result の時点で chart-relative resource index を完成形に近づけることである。C# は native packed result を 1 パスで詰め替え、旧互換 view を長期維持しない。

特に zip を保留画面へ複数投入するシナリオでは、zip ごとに推定 batch が分かれる。reverse lookup を lazy 評価に寄せると、重い zip の初回推定だけでなく、各 batch の初回候補探索へ構築 cost が漏れやすい。全 zip を単一 batch にまとめると軽い zip まで重い zip に巻き込まれるため採用しない。したがって、推定開始前に必要な reverse lookup surface は完成しているべきであり、その完成処理自体を native 側集約で 30 秒台へ戻すことを目標にする。

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
  - 導入先推定の broad filter に必要な resource-key -> candidate directory reverse lookup。
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
- separate deferred reverse lookup warmup。

補足: 導入先推定に reverse lookup が不要という意味ではない。必要な reverse lookup surface は destination resource index の一部であり、最終形では native scan / resource index build の完了時点で揃っているべきである。`reverse_lookup_warmup_deferred` のような C# 後段 task は暫定実装であり、初期化完了対象から外して済ませるものではなく、不要になるよう index contract を作り直す。

pending package は package / zip 単位で独立 batch として推定する。これは重い package が軽い package の推定完了を塞がないための重要な性質である。この前提では、reverse lookup の lazy build を各 batch に持ち込むと batch ごとの tail latency が増えるため、通常起動の install readiness では destination 側 reverse lookup surface を事前に揃える。

現行コード確認では、起動時の pending estimate queue は
`CatalogLoaded && DestinationResourceIndexReady && PendingPackagesRestored`
を満たすまで開始しない。これは妥当である。導入先推定本体は
`BMSLibrary.EvaluateInstallEstimation()` から
`BmsLibraryInstallEstimationService.EstimateInstallationDirectory(...)` へ入り、
所持 catalog、`DirectoryResourceLookupCache`、pending package の source surface を使う。

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
- 導入先推定に必要な reverse lookup surface は resource index の一部として扱い、初期化完了対象から隠さない。
- `ReloadFileDiff` は起動中の memory catalog / memory resource index を正本にできるが、通常起動の代替にはしない。
- 互換名目の旧経路、テストからしか呼ばれない処理、使わないログは残さない。
- 新旧実装を長期併存させず、移行時は call site / tests / docs を現行仕様へ置き換える。

## Non Goals

- `startup_ready_operable` だけを短く見せること。
- 導入可能に必要な DB load / resource index 構築を未完了のまま ready にすること。
- 通常起動の main path に chart-only resource-skip を入れること。
- destination resource index を前回起動の永続 data から復元すること。
- playlist / ranking / score / chart_info を install readiness の条件に戻すこと。
- `reverse_lookup_warmup_deferred` を初期化完了の expected task から外すだけで、初期化が速くなったように扱うこと。
- zip ごとの pending estimate batch を単一巨大 batch にまとめ、重い package に軽い package を巻き込ませること。
- reverse lookup の構築 cost を pending package の初回推定へ lazy に押し付けること。

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
- 旧 index を別々の top-level build step として作る流れはやめ、`resource_index_build source=native_canonical ...` を正本ログにした。
- `song_tbl_file_check_breakdown` / `bms_scan` の内訳は `resource_index_build_ms` 系へ寄せた。
- Phase 3A では resource matching semantics は変更しない。
  - basename / relative path の既存互換 view は `LibraryResourceIndex` 内部で維持する。

未完了として次単位に残すもの:

- file operation に残る `BMSDirectoryFileNameHash` 用途を `LibraryResourceIndex` / `DirectoryResourceLookupCache` API へ移行する。
- chart-relative semantics cleanup と C# 後段 reverse index build 削除は Phase 4B / 5A で実施済み。

### Key Changes

- destination resource index の正本を 1 つに定義する。
  - directory。
  - category。
  - chart-relative resource key hash。
  - resource-key -> candidate directory reverse lookup。
  - candidate directory membership。
- `DirectoryResourceLookupCache` をカテゴリ別 resource index の正本にし、別の relative path 補助 index は構築しない。
- `BMSDirectoryFileNameHash` / basename-only cache は正本から得られる transitional view に留め、最終的には削除する。
- install estimation、resource health、file move、package install、folder rename は同じ resource index API を使う。
- call site / tests を同時に現行 API へ置き換え、使われなくなった index API は削除する。

### Acceptance Criteria

- `resource_lookup_cache_ms + relative_path_hash_index_ms` 相当の重複構築が消える。
- `dirhash_build_ms` の主成分が説明でき、不要な owner / relative prefix rebuild が消える。
- `reverse_lookup_warmup_deferred` が不要になり、resource-key reverse lookup を含む初期化全体が短縮する。
- install estimation は chart-relative semantics に更新され、maintenance / file operation は既存挙動を維持する。
- memory peak が下がる。

## Phase 4: Native Chart-Relative Resource Index Contract

初回起動、通常起動、root 変更時に full file enumeration は必要である。その前提で native bridge payload と managed materialization を削る。

この phase では、単に payload を小さくするだけでなく、C# 側で `DirectoryResourceLookupCache` / reverse lookup warmup を再構築する必要がない native contract へ寄せる。

譜面 resource reference はすべて chart-relative resource key として扱う。resource key は拡張子を落とした path 込みファイル名であり、`foo.wav` は key `foo`、`sound/foo.wav` は key `sound/foo` である。basename-only と subdirectory relative path を別系統の推定材料として扱わない。

2026-05-05 の Phase 4B / 5A 追加修正では、native contract を `2026050503` へ更新し、native 側の `base` 系 hash もこの resource key を返すようにした。これにより C# 側では `DirectoryResourceLookupCache` だけで health / install estimation を処理する。native packed result では `base` と `relative` が同じ category は同じ blob / offset / length を指し、managed decoder も同じ配列 / dictionary を再利用する。

### Key Changes

- native bridge から managed に渡す payload を canonical chart-relative resource index 生成に必要な形へ寄せる。
- raw full path transfer を減らし、root id + directory id + normalized chart-relative resource key の compact representation に寄せる。
- audio / image / movie の group / assign / merge / pack を native 側で完結させる。
- resource-key -> candidate directory reverse lookup を native 側で構築、または packed result から C# が単純に詰め替えるだけにする。
- pending package / zip ごとの推定 batch では destination reverse lookup を構築しない。batch 側は package source surface と完成済み destination index を照合するだけにする。
- basename hash という名前の旧 field は互換名として残っているが、中身は chart-relative resource key hash である。basename-only fast path の正本にはしない。
- `__all__` 的な総列挙を main path に入れない。
- native bridge と managed decoder は 1 つの contract に固定する。bridge DLL が不一致なら fallback せず初期化失敗として扱う。

### Acceptance Criteria

- `bridgeRawBufferBytes` が下がる。特に base / relative の二重 blob を持たない。
- `nativeBridgeMs`、managed decode、managed materialize、`resource_index_build lookupMs` が下がる。
- `reverse_lookup_warmup_deferred` が起動 background task として不要になる。
- `startup_initialization_complete` が reverse lookup 完了込みで 30 秒台へ戻る方向に進む。
- 複数 zip の保留投入で、各 zip の初回推定が destination reverse lookup 構築を再実行しない。
- 同じファイル集合から chart-relative semantics に基づく install estimation / health 結果が得られる。
- 初期化全体の elapsed が下がる。

## Phase 5: Chart-Relative Resource Semantics Cleanup

譜面の resource reference はすべて chart-relative resource key として扱う。隣接ファイル名と subdirectory file を別ルールで扱わない。

### Target Semantics

- `foo.wav` は chart-relative resource key `foo`。
- `sound/foo.wav` は chart-relative resource key `sound/foo`。
- `foo.wav` と `sound/foo.wav` は別 key。
- `.ogg` / `.mp3` / `.flac`、`.bmp` / `.jpg` / `.jpeg` は従来どおり互換拡張子として扱った上で拡張子を落とす。
- source package 側も destination library 側も同じ key 体系で評価する。

### Key Changes

- BMS / BMSON health 判定を chart-relative resource key に統一する。
- install estimation の broad filter / final evaluation を chart-relative key で統一する。
- basename-only の alternate correctness path は削除する。
- `BMSDirectoryFileNameHash` を推定の正本から外す。
- `DirectoryResourceLookupCache` をカテゴリ別 resource key の単一 API にする。
- path-aware resource がある package で、無関係な basename match が high confidence にならないようにする。
- `install-estimation-relative-path-foundation.md` と用語を揃え、古い挙動を前提にした tests を置き換える。

### Acceptance Criteria

- `foo.wav` と `sound/foo.wav` の混同が起きない。
- relative path resource を持つ譜面の導入先推定で、basename だけの候補が残らない。
- bare filename resource だけの譜面は `foo` という chart-relative resource key として評価される。
- source / destination / maintenance の resource matching 結果が同じ semantics になる。
- basename-only fast path、relative-strict path の二重設計が残らない。

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
- `reverse_lookup_warmup_deferred` は Phase 4B / 5A で廃止した。導入先推定に必要な reverse lookup surface は native canonical resource index の成果物として持つ。
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

ただし `reverse_lookup_warmup_deferred` については、lazy 化や expected phase から外すことを次方針にしない。導入先推定には resource-key -> candidate directory lookup が必要であり、これを C# background で後から全量構築している現状が問題である。次の主実装は、native chart-relative resource index contract を作り直し、reverse lookup surface を scan/index build の成果物に含める方向へ戻す。

未完了として次単位に残すもの:

- `maintenance_hydration` を、全譜面の health 再計算ではなく DB に保存済みの maintenance snapshot を catalog owner へ高速に attach する処理として整理し、1 件ごとの高コスト warning refresh を避ける。
- `chart_info_hydration` の DB count / version による no-op skip。
- playlist entries hydration の同一 startup 内二重 hydrate / presentation rebuild 削減。
- `updateLR2IRScoreTable()` の no-op 判定 / DB replace 条件整理。

## Phase 4B / 5A: Native Canonical Resource Index

Phase 4 / Phase 5 を前倒しして、native chart-relative resource index contract を再構築した。目的は、`reverse_lookup_warmup_deferred` を初期化完了から外すことではなく、deferred warmup が不要な index を起動時 scan の成果物として作ることである。

相対パス対応以前の目標水準である「導入先推定に必要な情報まで含めて 30 秒程度」を比較対象に戻す。maintenance / playlist / chart_info の background 削減は残るが、現時点で最も設計負債が大きいのは basename-only と relative path を別系統にしている resource index / install estimation である。

### Phase 4B: Native Resource Index Payload Rebuild (implemented)

- `EBridge_ScanChartAndResources` の result contract は、chart-relative resource key と candidate directory reverse lookup surface を返す唯一の contract に置き換えた。
- native 側で root / directory / resource category / normalized chart-relative key / reverse lookup を集約し、packed arrays として返す。
- managed 側は native packed result から `LibraryResourceIndex` / `DirectoryResourceLookupCache` へ materialize する。reverse lookup は native result で完成済みとして扱う。
- C# 側の `DirectoryResourceLookupCache.WarmupReverseLookupStep()` 相当の全量 build と `reverse_lookup_warmup_deferred` task は削除した。
- C# と native bridge DLL は同一ビルド成果物としてセット配布する。旧 native ABI / V1 decode / verify compare は残さない。旧 bridge DLL や header contract 不一致は fallback せず初期化失敗にする。
- Everything API / service が使えない場合は managed file scan に fallback する。これは旧 native ABI 互換ではなく、同じ chart-relative semantics の managed scan result から `LibraryResourceIndex` を作る経路である。
- `resource key` は拡張子を落とした path 込み key とし、`foo.wav -> foo`、`sound/foo.wav -> sound/foo` に統一した。
- native packed result では base / relative が同一 semantics の category blob を alias し、managed decode / materialize でも配列と dictionary を再利用する。
- startup では別の relative path 補助 index を構築せず、resource index build log にも補助 index build 時間を出さない。
- native reverse map は巨大 `unordered_map<uint, vector<uint>>` ではなく、`(hash, directoryIndex)` の flat pair を sort して構築する。これにより 800 万 key 規模の allocation / pack cost を抑える。
- managed reverse map decode は `int[] + List<string>` の二重詰め替えを避け、native indices から `string[]` を直接作る。

### Phase 5A: Chart-Relative Install Estimation Cleanup (implemented)

- `ChartResourceSnapshot` の basename-only / path-aware dual source を chart-relative key source へ統一する。
- `BmsLibraryInstallEstimationService` の broad filter は canonical resource-key reverse lookup だけを使う。
- `EvaluateCandidateBasenameOnlyFastPath` と relative strict の二重評価を廃止し、single chart-relative evaluation にした。
- `foo.wav` と `sound/foo.wav` は別 key として扱う。bare filename は `foo.wav` という chart-relative path であり、subdirectory file へは一致しない。
- `BMSDirectoryFileNameHash` の basename-only matching は導入先推定の正本から外した。残る production use は health / file operation など別 surface の互換ではなく、今後 canonical resource index API へ畳み込む整理対象である。
- zip ごとに独立した pending estimate batch という性質は維持する。高速化は batch 統合ではなく、batch が参照する destination index の完成度と materialize cost 削減で行う。

### Phase 4B / 5A Performance Check

2026-05-05 15:25 の実環境ログでは、同一ライブラリ規模で次の状態になった。

- `everything_scan totalMs=30495`
  - `nativeBridgeMs=25424`
  - `managedDecodeMs=2659`
  - `managedMaterializeMs=2375`
  - `bridgeRawBufferBytes=379540328`
  - `packMs=2808`
- `resource_index_build buildMs=2206`
  - `lookupMs=2179`
  - relative path 補助 index の build log が出ない
  - `reverseLookupKeys=8123464`
- `startup_install_estimation_ready elapsedMs=35895`
- `pending_estimate_batch done source=startup_restore elapsedMs=32379`

比較対象として、修正前の Phase 4B / 5A 直後ログでは `everything_scan totalMs=107748`、`nativeBridgeMs=50980`、`managedDecodeMs=24061`、`managedMaterializeMs=32652`、`resource_index_build buildMs=32436`、`pending_estimate_batch done source=auto_install elapsedMs=326822` だった。最新実装では resource index は reverse lookup 完成込みで約 30 秒、startup restore の導入先推定 batch は約 32 秒まで戻っている。

残る 30 秒超過分の主因は Everything query (`audioQueryMs` が約 13 秒) と、全 resource entry を managed model へ materialize する約 5 秒である。これは lazy warmup に逃がす対象ではなく、次に削る場合は native payload / managed index representation のさらなる圧縮で扱う。

2026-05-06 の追加実装で、`EBridge_ScanChartAndResources` は chart / audio / image / movie query を別 Everything client / 別 search state で並列取得するようにした。query 結果は category ごとの raw hit buffer に集め、全 query 完了後に従来どおり chart directory owner assign、dedupe、pack を行うため、resource key semantics は変えない。

実機確認では、同一ライブラリ規模で次の結果になった。

- 変更前代表値:
  - `everything_scan totalMs=31860`
  - `nativeBridgeMs=26675`
  - `audioQueryMs=13990 imageQueryMs=1339 movieQueryMs=246`
  - `bms_scan totalMs=31886`
- 変更後確認値:
  - `everything_scan totalMs=30243`
  - `nativeBridgeMs=25187`
  - `audioQueryMs=13905 imageQueryMs=6066 movieQueryMs=5226`
  - `bms_scan totalMs=30267`
  - `startup_install_estimation_ready elapsedMs=31694`

Everything service 側で同時 query の内部競合があるため、個別の `imageQueryMs` / `movieQueryMs` は伸びる。ただし wall clock としては `nativeBridgeMs` が約 1 秒短縮した。次に同じ領域を触る場合は、query 並列度を増やすよりも、`bridgeRawBufferBytes=379540328` 規模の native payload / managed decode / managed materialize を削る方が効果見込みが大きい。

続く 2026-05-06 の payload / managed materialize 改善では、native decoded arrays から `LibraryResourceIndex` / `DirectoryResourceLookupCache` を直接構築し、`Dictionary<string,uint[]>` 経由の再 lookup と native reverse map の dictionary 再コピーを避けるようにした。あわせて managed hash array decode は `int[]` temporary を経由せず `uint[]` へ直接 copy する。

実機確認では次の状態になった。

- `everything_scan totalMs=28253`
- `nativeBridgeMs=25565`
- `managedDecodeMs=2425`
- `managedMaterializeMs=225`
- `resource_index_build buildMs=53 folderMs=8 lookupMs=44`
- `bms_scan totalMs=28279`
- `startup_install_estimation_ready elapsedMs=29553`

これにより `managedMaterializeMs` は約 2.3s から約 0.2s、`resource_index_build` は約 2.1s から約 0.05s まで縮小した。`bridgeRawBufferBytes` 自体はまだ約 380MB のままであり、次に同じ領域をさらに削る場合は native contract 側で送る hash group / reverse map payload そのものを小さくする必要がある。

続く allBase 削除では、native bridge / managed scan result から旧 all-resource surface を削除した。保持する正本は audio / image / movie のカテゴリ別 base / chart-relative hash とカテゴリ別 reverse lookup だけである。

- `EBridge_ScanChartAndResources` は `all_hash_*`, `self_all_hash_*`, `all_base_reverse_*`, `all_base_hash_count` を返さない。
- `BmsScanResult` は `AllResourceBaseNameHashesByChartDirectory` / `SelfOwnedAllResourceBaseNameHashesByChartDirectory` を持たない。
- `DirectoryResourceLookupCache.Entry` の `AllBaseNameHashArray` / `SelfOwnedAllBaseNameHashArray` 相当は保存値ではなく、audio / image / movie のカテゴリ配列から lazy に派生する union である。
- `FolderAllFileList` は self-owned category union から構築する。managed fallback scan でも同じカテゴリ辞書を正本にし、Everything unavailable 時の fallback は維持する。
- generic all-base reverse lookup は削除した。導入先推定と reverse lookup はカテゴリ別 chart-relative key を使う。
- resource health の WAV / BGA / MOV 存在判定もカテゴリ別 index を正本にする。譜面ファイルや別カテゴリ resource は、同じ stem でも存在扱いしない。
- 拡張子なし union は transitional view であり、health 判定の fallback には使わない。

この変更は payload 削減の第一段である。実機での効果確認は `everything_scan` の `bridgeRawBufferBytes`, `managedDecodeMs`, `managedMaterializeMs`, `folderUnionHashCount`、および `resource_index_build` の悪化有無で見る。

mixed package の既所持 chart hash から複数の配置先候補が見つかる場合も、extensionless union の health 補助では判定しない。候補集合を通常推定と同じ category resource final evaluation へ渡し、一意に勝つ candidate は自動設定、複数 viable candidate が残る場合は `InstalledDestinationAmbiguous` warning と suggestions に落とす。`DirectoryResourceLookupCache` がない場合は推定不可として `InstalledDestinationResolveFailed` を付ける。

続く整理では、導入先推定から cacheless 経路を削除した。通常推定、候補限定推定、merge / reinstall correction はすべて `DirectoryResourceLookupCache` を必須とし、`BMSDirectoryFileNameHash` へ fallback しない。候補 directory 集合も `DirectoryResourceLookupCache.Keys` から得る。resource index がない場合は `resource_index_unavailable` として推定不可にする。さらに `BmsLibraryInstallEstimationService` の推定 API から `BMSDirectoryFileNameHash` 引数を外し、候補 view 内部の extensionless all-base union も使わない形にした。

次フェーズでは、残っている extensionless resource union の利用箇所を全調査し、カテゴリ別 API へ置き換える。特に導入先 tie-break で union を使う必要は薄く、同率に近い候補は曖昧候補として提示し、順序安定だけが必要なら path 名順で十分とする。

2026-05-06 実機確認では次の状態になった。

- `everything_scan totalMs=26640`
- `nativeBridgeMs=23948`
- `managedDecodeMs=2255`
- `managedMaterializeMs=410`
- `bridgeRawBufferBytes=281218408`
- `folderUnionHashCount=12231908`
- `resource_index_build buildMs=171 folderMs=144 lookupMs=26`
- `startup_install_estimation_ready elapsedMs=29723`

payload は約 379MB から約 281MB へ減少した。allBase union は managed 側で sorted category arrays から線形 merge して派生するため、`resource_index_build folderMs` は旧 allBase payload 直受けより増えるが、全体の `everything_scan totalMs` と install readiness は悪化していない。未分類 allBase reverse lookup は復活させない。

### Phase 4B / 5A Normal Startup Baseline

2026-05-05 17:40 の保留 package なし通常起動ログでは、Phase 8D 後の通常起動想定として次の状態になった。17:41:48 以降に手動の `全譜面を再スキャン` が開始されているが、ここでは `startup_initialization_complete` までの通常起動分だけを評価する。

- install readiness / UI readiness
  - `startup_install_estimation_ready elapsedMs=38956`
  - `startup_install_ready elapsedMs=38956`
  - `startup_ready_operable elapsedMs=40189`
  - `pendingPackages=0`
  - `pendingEstimateQueueBatches=0`
- DB catalog / file scan
  - `song_tbl_load_projection projection=catalog readMs=10617 materializeMs=10591 rows=208979 bmsonRows=1051`
  - `everything_scan totalMs=37671 nativeBridgeMs=31279 managedDecodeMs=3689 managedMaterializeMs=2668`
  - `resource_index_build buildMs=2318 lookupMs=2274 reverseLookupKeys=8123464 payloadBytes=379540328`
  - `song_tbl_file_check_breakdown db_commit_chunks=0 deleted_count=0 added_count=0`
- startup background / initialization complete
  - `startup_initialization_complete elapsedMs=73932`
  - `playlist_entries_hydration lastMs=13031`
  - `ranking_refresh_deferred lastMs=12821`
  - `chart_info_hydration lastMs=10148`
  - `maintenance_hydration lastMs=4575`
  - `installable_maintenance lastMs=631`

Phase 8D 後の `maintenance_hydration` は、`rows=210027`, `readMs=3298`, `materializeMs=3290`, `mapBuildMs=169`, `applyMs=1085`, `attachMs=280`, `indexBuildMs=396`, `validSnapshotCount=210027`, `placeholderCount=3` で完了した。Phase 8D 前に観測していた `applyMs=11572` / `lastMs=14578` から、通常起動の background tail は約 10 秒短縮されており、全譜面の resource 再検証ではなく DB snapshot attach として動いている。

保留なし通常起動では、導入可能までの critical path は resource index 完成込みで約 39 秒である。`pendingEstimateQueueBatches=0` なので、起動直後に推定 batch は走らない。初期化全体は約 74 秒で、残る支配要因は `playlist_entries_hydration`、`ranking_refresh_deferred`、`chart_info_hydration`、および scan / DB catalog load 側である。`maintenance_hydration` はまだ 4.6 秒あるが、現時点では次の最優先ではない。

次の優先は、通常起動の初期化全体を短くする観点では `playlist_entries_hydration` / `ranking_refresh_deferred` / `chart_info_hydration` の DB load・materialize 短縮、導入可能までを短くする観点では `everything_scan` / native bridge / catalog load の短縮である。

### Phase 8H / 8I / 8J Normal Startup Measurement

2026-05-05 19:15 の保留 package なし通常起動では、`--log-level=info` 付き Release 起動で次の結果になった。

- install readiness / UI readiness
  - `startup_install_estimation_ready elapsedMs=31712`
  - `startup_ready_operable elapsedMs=32899`
  - `pendingPackages=0`
  - `pendingEstimateQueueBatches=0`
- scan / file diff
  - `song_tbl_file_check_breakdown scan_ms=30351`
  - `native_bridge_ms=25275`
  - `managed_decode_ms=2656`
  - `managed_materialize_ms=2359`
  - `resource_index_build_ms=2190`
  - `deleted_count=0 added_count=0 db_commit_chunks=0`
- startup background / initialization complete
  - `startup_initialization_complete elapsedMs=64765`
  - `playlist_entries_hydration lastMs=11375`
  - `ranking_refresh_deferred lastMs=11004`
  - `chart_info_hydration lastMs=10337`
  - `maintenance_hydration lastMs=4339`
  - `score_hydration_deferred lastMs=4817`

Phase 8H の playlist projection は `projection=startup_entries`, `rows=556649`, `activeEntryCount=550207`, `removedEntryCount=6442`, `dbReadMs=10838`, `groupMs=59`, `assignMs=311`, `totalMs=11245` だった。旧 `Table<BMSTableEntry>().ToList()` 直接呼びを gateway loader へ寄せる整理は完了しているが、row 数自体はほぼ変わらないため、短縮幅は限定的である。playlist をさらに短くするには、startup で全 entry を full `BMSTableEntry` として持つ前提そのものを見直す必要がある。

Phase 8I の ranking は `ranking_cache_refresh done elapsedMs=2828`, `irDataDbReadMs=242`, `dbRows=17708`, `xmlCheckMs=232`, `reloadTargets=99`, `upsertRows=0` で、cache refresh は軽い。一方 `ranking_refresh_deferred` は `irScoreMs=8156`, `irScoreXmlFetchMs=821`, `irScoreXmlParseMs=403`, `irScoreDbReplaceMs=6617`, `irScoreMergeMs=312`, `cacheMs=2838`, `elapsedMs=11004` で、支配要因は `ir_score` table replace である。`ir_score` は主に `SCORE_UNSENT` 付与と LR2 custom folder の `UNSENT SONGS` 条件生成に使う。ランキング表示 / ranking cache は `ir_data` 側であり、`ir_score` とは別系統である。

Phase 8K では `LR2IRのスコアをDLしIR未送信を検出する` 設定を追加する。既定は有効。無効時は LR2IR player score XML fetch、`ir_score` DB 更新、`ir_score` 由来の未送信検出を完全に使わない。既存 `ir_score` table は削除しないが、無効時の `SCORE_UNSENT` 付与や `UNSENT SONGS` 生成には使わない。

Phase 8K では player score XML の normalized score digest による no-op 判定も追加する。`ir_score_refresh_metadata` には LR2ID ごとの normalized score digest だけを保存し、digest が同じなら DB replace を skip して既存 `ir_score` rows を in-memory 反映に使う。digest が変わった場合だけ `ir_score` table replace を実行する。LR2IR player score XML の `lastupdate` はプレイヤーの最終スコア更新ではなく譜面 hash 側の更新時刻として揺れるため、normalized score digest から除外する。`ir_score.lastupdate` は未送信判定や表示の正本には使わず、表示用 ranking update は `ir_data` / ranking cache 側の `rankingLastupdate` を使う。

Phase 8J の chart_info は `chartInfoRows=209904`, `parseFailureRows=24`, `dbLoadMs=9773`, `indexBuildMs=291`, `ownerApplyMs=229`, `backfillCandidateOwners=0`, `totalMs=10303` だった。owner apply と index build は軽く、支配要因は full `chart_info` row load である。ここは単純な loader 整理では短くならないため、persistent hydrated index / no-op skip / projection の仕様判断が必要である。

現行実装では、`LR2SongDBExtended` と `LR2ScoreDBExtended` が接続生成時に static `Monitor` を取得し、`Dispose()` まで保持する。したがって `playlist_entries_hydration`、`chart_info_hydration`、`maintenance_hydration`、`ranking_refresh_deferred` の `ir_score` / `ir_data` 読み取りは、read-only でも同じ song DB に対して接続寿命単位で直列化される。Phase 8H-8K で gateway loader と timing は整理したが、loader が `OpenSongDb()` を使う限り、read-only 同士の並行性は得られない。score DB 側も `LoadScoreTable()` など `OpenScoreDb()` を使う読み取りは同じ構造で直列化される。なお `score_hydration_deferred` 自体は DB を読まず、memory score snapshot を `BMSFile` へ反映する task である。

2026-05-05 23:47 のログでは、`playlist_entries_hydration` が `23:47:24` から `23:47:35` まで song DB lock を保持している間に `ranking_refresh_deferred` が開始しており、`ranking_cache_refresh start` は playlist hydration 完了後の `23:47:36` まで遅れている。`ranking_refresh_deferred done` の `irScoreMs=9062` に対し、実処理内訳は `irScoreXmlFetchMs=737`, `irScoreXmlParseMs=363`, `irScoreDigestMs=92`, `irScoreDbLoadMs=229`, `irScoreMergeMs=378` 程度であり、多くは DB 接続取得待ちに見える。このため、次フェーズでは DB read phase と write phase の lock boundary を整理する。

この実測では `startup_initialization_complete` は Phase 8D baseline の `73932ms` から `64765ms` へ短縮した。ただし scan や DB のばらつきも含まれるため、Phase 8H は「大幅短縮」ではなく、DB hydration 方針統一と次フェーズ判断用の内訳取得として扱う。

### Phase 8D: Maintenance Snapshot Attach / Manual Rescan (implemented)

- `maintenance` は通常、譜面が初めてライブラリへ導入された時点、または明示的な再スキャンで計算される persisted snapshot として扱う。通常起動では resource file の存在を全譜面で再検証しない。
- `BMSFile.MaintenanceInfoOrigin` を追加し、`None` / `Placeholder` / `DbHydrated` / `Calculated` を明示する。lazy getter で作られる default は `Placeholder` であり、valid health snapshot ではない。
- `TryGetMaintenanceInfoWithoutCreating()` / `HasValidMaintenanceInfoSnapshot` を追加し、resource health warning / index build は lazy default を生成しない。ここでの valid は「DB hydrate または計算済み由来」を指し、WAV/BGA/MOV の一部だけが入った persisted snapshot も既存仕様どおり warning 投影に使う。一方、bmson parse 直後の encoding-only placeholder は valid snapshot に昇格しない。
- `maintenance_hydration` は、DB の persisted snapshot を `BMSFile` / `BmsonSong` へ attach する処理として整理した。DB row は `DbHydrated`、file diff / install / manual rescan は `Calculated` として扱う。
- hydration apply では全件 `checkBMSFileNeedToBeFixedAndSetWarnings()` と全件 `NotifyMaintenanceInfoChanged(true, true)` を行わない。ResourceHealth は valid snapshot から `ResourceHealthIndexSnapshot` を一括 build し、view-level refresh で反映する。
- orphan cleanup は Phase 8B の統合済み経路を維持し、別 task を再導入しない。
- `maintenance_hydration done` は `attachMs`, `indexBuildMs`, `validSnapshotCount`, `placeholderCount`, `viewRefreshQueued` を出す。
- `ファイルスキャン` 右クリックメニューに `全譜面を再スキャン` を追加した。これは owned BMS 全件 + installed bmson 全件を `forceUpdate=true` で再計算する重い明示操作で、通常起動や `ReloadFileDiff` には組み込まない。
- manual rescan は `maintenance_rescan start/progress/done/canceled` を出し、startup / install / playlist sync とは別の status bar progress を使う。cancel は section 境界で反映する。
- 2026-05-05 17:41 の手動 `全譜面を再スキャン` は未完了のため完了時間評価には含めない。ただし section 単位では `targetCount=1000` ごとにおおむね 9-14 秒、遅い section で 18 秒程度かかっており、全件再スキャンは意図どおり「重い明示操作」として扱うべき規模である。

### Phase 2B: Catalog Load Micro Reduction

- `song` materialize 後の normalize loop `1480ms` と `crcRecalculated=19` を確認し、DB write 不要時の normalize / CRC 再計算をさらに絞る。
- `song_tbl_load_projection` は維持し、`maintenance` と `chart_info` を critical path に戻さない。
- `startup_install_estimation_ready` の比較対象は Phase 2A 実測 `35961ms` とする。

### Phase 8E: Chart Info Hydration No-op Reduction

- `chart_info_hydration` は DB count / schema version / hydrated index state から no-op 判定できる範囲を増やす。ただし session index が未 hydrated の通常起動では必要な load と owner apply は維持する。
- 現行通常起動では `backfillCandidateOwners=0` で backfill skip は効いている。残りは `dbLoadMs=10104` が支配的で、owner apply は `229ms` と軽い。次に触るなら DB projection / persistent hydrated index の設計が必要で、Phase 8D より後に回す。

### Phase 8H: Playlist Entries Hydration Projection (implemented)

- 現行通常起動では `playlist_entries_hydration totalMs=10943`、うち `dbLoadMs=10584` が支配的である。
- startup hydration は `BmsLibraryDbGateway.LoadStartupPlaylistEntries()` を正本にし、`Table<BMSTableEntry>().ToList()` を直接呼ばない。
- loader は `projection=startup_entries` として必要列を明示した SQL を使い、`playlist_id IS NOT NULL` の row だけを読む。
- `is_removed` row は既存の playlist state 復元に必要なので読み込む。
- grouping / assignment の意味は変えず、`playlist_id -> List<BMSTableEntry>` へまとめてから `BMSTable.entries` に attach する。
- log は `playlist_entries_hydration projection=startup_entries rows=... dbReadMs=... materializeMs=... groupMs=... assignMs=... totalMs=...` とし、SQLite query/materialize elapsed と memory 側 grouping / assignment を分けて観測する。現行 sqlite-net `Query<T>` では reader と object materialize が一体なので、`dbReadMs` / `materializeMs` は同じ loader elapsed を示す。

### Phase 8I: Ranking DB Load Metrics / SQL Filter (implemented)

- `setRankingScore()` が読む `ir_data` は `BmsLibraryDbGateway.LoadIrDataWithMetrics(lr2Id)` を使い、`WHERE lr2id = ?` の SQL loader で対象 LR2ID の row だけを materialize する。
- `ranking_cache_refresh done` は `irDataDbReadMs` / `irDataMaterializeMs` を出し、cache XML check / reload / upsert と分けて確認できる。
- `updateLR2IRScoreTable()` の network fetch / XML parse / DB replace / in-memory merge を `ranking_refresh_deferred done` に出す。
- `setRankingScore()` の cache delta refresh は維持し、ranking cache と score table の責務を混ぜない。

### Phase 8K: LR2IR Player Score XML No-op / Unsent Detection Setting (implemented)

- `EnableDownloadLr2IrScoreAndDetectUnsent` を追加し、設定画面では `LR2IRのスコアをDLしIR未送信を検出する` と表示する。
- 設定が false の場合、`ranking_refresh_deferred` は player score XML fetch、XML parse、`ir_score` replace、`BMSScores` への `ir_score` 反映を行わない。`ranking_cache_refresh` は従来通り実行する。
- 設定が false の場合、既存 `ir_score` table が残っていても `SCORE_UNSENT` と LR2 custom folder `UNSENT SONGS` には使わない。
- 設定が true の場合、LR2ID ごとの normalized score digest で no-op 判定する。`score_digest_same` では DB replace を skip し、既存 `ir_score` rows を読み出して in-memory 反映する。normalized digest は `lastupdate` を含めず、score/clear/combo/minbp/option など未送信判定に関わる値だけを見る。
- `ranking_refresh_deferred done` は `irScoreSkipped`, `irScoreSkipReason`, `irScoreDigestMs`, `irScoreDbLoadMs`, `irScoreDbReplaceMs`, `irScoreLoadedRows`, `irScoreMetadataUpdated` を出す。

### Phase 8J: Chart Info Hydration Loader Metrics (implemented)

- `chart_info_hydration` は `BmsLibraryDbGateway.LoadChartInfoHydrationData()` を使い、`chart_info` と current parse failure を同一 gateway open の中で読む。
- full `chart_info` row load は維持する。session index と owner apply の正本として row 実体が必要なため、projection 削減や persistent hydrated index は次単位に分ける。
- log は `dbLoadMs` と `dbMaterializeMs`、`parseFailureRows`、owner counts を分け、`backfillCandidateOwners=0` の通常起動で no-op skip が維持されることを確認する。

### Phase 8L: Startup Hydration Read-only DB Path / DB Lock Boundary (implemented)

Phase 8L は、初期化全体を短縮するために DB lock boundary を整理する。目的は background を初期化完了対象から外すことではなく、read-only hydration が不必要に writer 相当の static lock を長時間保持し、他の read-only task を待たせる状態をなくすことである。

実装済み仕様:

- `LR2SongDBExtended` / `LR2ScoreDBExtended` は write-capable constructor では従来通り process-local static `Monitor` を保持する。
- read-only constructor は `SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex` で開き、process-local static `Monitor` を取得しない。
- `BmsLibraryDbGateway.OpenSongDbReadOnly()` / `OpenScoreDbReadOnly()` を追加し、startup hydration の read phase の正本にした。
- `LoadStartupPlaylistEntries()`、`LoadChartInfoHydrationData()`、`LoadMaintenanceTable()`、`LoadIrDataWithMetrics()`、`LoadIrScoreRows()`、`LoadIrScoreRefreshMetadata()`、起動時 `LoadScoresAndPlayerId()` は read-only connection を使う。
- `BMSPlaylist.Initialize()` の playlist header load も read-only connection を使う。
- `score_hydration_deferred` は DB を読まず、memory score snapshot の owner attach が中心であるため、Phase 8L の DB read-only 化対象には含めない。
- `UpsertIrScoreRefreshMetadata()`、`ReplaceIrScoreTable()`、`UpsertIrData()`、maintenance cleanup、chart_info backfill / inline commit、file diff commit は write-capable path として短い writer boundary に残す。
- `LoadChartInfoHydrationData()` と `ir_score` / `ir_data` read loader から schema ensure / table creation を外した。必要 schema は startup constructor / migration phase の責務であり、hydration loader 内では互換修復しない。
- loader log には `readOnly=true` と `dbLockWaitMs` を追加した。read-only connection は static monitor を取らないため、ここで測る wait は writer monitor 起因では 0 になる。

方針:

- `BmsLibraryDbGateway` に startup hydration 用の read-only connection 経路を追加する。
  - 例: `OpenSongDbReadOnly()` / `OpenScoreDbReadOnly()` または同等の read-only loader 専用 wrapper。
  - read-only connection は schema ensure / migration / repair / table creation を行わない。
  - 必要 schema がない場合は startup migration 漏れとして fail させ、hydration 内で互換修復しない。
- DB read phase と memory apply phase を分離する。
  - DB read phase は read-only connection で短寿命に行い、row / DTO / dictionary を返す。
  - memory apply phase は DB connection を閉じた後、必要最小限の catalog / score lock で owner へ attach する。
- read-only 同士は並行可能にする。
  - 少なくとも `playlist_entries_hydration`, `chart_info_hydration`, `maintenance_hydration`, `ranking_refresh_deferred` の `ir_score` / `ir_data` read は互いの長い read を待たないようにする。
  - 起動時 score DB load は `OpenScoreDbReadOnly()` 相当へ移す。`score_hydration_deferred` の残コストは memory apply / owner attach 側として別に扱う。
  - write-capable path は短い writer lock / transaction に集約し、read-only loader と同じ helper 名・同じ task 内へ混ぜない。
- 旧互換や fallback は残さない。
  - 古い schema を hydration loader が別 query で読む経路は作らない。
  - read-only loader 内で `CreateTable`, `EnsureBmsonSchema`, `EnsureChartInfoSchema`, `CompleteBmsonStartupMigration` を呼ばない。
  - migration / repair が必要なら startup migration phase で完了させる。
- lock wait を観測する。
  - `dbLockWaitMs` / `dbOpenMs` / `readOnly=true|false` を loader log に追加し、改善後に DB query 自体と lock wait を分けて評価できるようにする。

優先移行対象:

- `playlist_entries_hydration`: row 数が多く、現状では song DB lock を長く保持しやすい。
- `chart_info_hydration`: full `chart_info` row load が重く、read-only 化による並行性の効果が大きい。
- `maintenance_hydration`: DB snapshot attach の read phase は read-only。orphan cleanup delete は別 writer phase に分ける。
- `ranking_refresh_deferred`: `LoadIrScoreRefreshMetadata`, `LoadIrScoreRows`, `LoadIrDataWithMetrics` は read-only。metadata update / `ir_score` replace / `ir_data` upsert は writer phase に残す。
- 起動時 score table load: `LoadScoresAndPlayerId()` を score DB read-only loader 化し、score DB 側 static monitor の長時間保持を避ける。
- `score_hydration_deferred`: DB lock 分離対象ではない。必要なら別フェーズで memory apply / owner attach の chunking や notification を見る。

Acceptance criteria:

- `playlist_entries_hydration`、`chart_info_hydration`、`maintenance_hydration`、ranking の `ir_score` / `ir_data` read は write-capable monitor を保持しない。
- write path は `OpenSongDb()` / `OpenScoreDb()` と明示 transaction に残し、DB mutation の安全性を維持する。
- `startup_background_summary` と各 loader log で、read-only 化済みか、DB read/materialize elapsed、DB lock wait を分けて説明できる。
- hydration worker 内の startup read loader には schema ensure / migration / repair / compatibility fallback を残さない。

2026-05-06 の Phase 8L 実機確認では、主要 read loader は `readOnly=true dbLockWaitMs=0` になった。

- `score_tbl_load readOnly=true dbLockWaitMs=0 rows=17560`
- `playlist_init_header loadTablesMs=112 readOnly=true dbLockWaitMs=0`
- `playlist_entries_hydration ... readOnly=true dbLockWaitMs=0 dbReadMs=10353 totalMs=10781`
- `ranking_cache_refresh ... irDataDbReadMs=236 irDataDbLockWaitMs=0`
- `ranking_refresh_deferred ... irScoreDbLoadMs=181 irScoreDbLockWaitMs=0 elapsedMs=10516`
- `chart_info_hydration ... readOnly=true dbLockWaitMs=0 dbLoadMs=9556 totalMs=10079`
- `maintenance_hydration ... readOnly=true dbLockWaitMs=0 readMs=3054 elapsedMs=4298`
- `startup_initialization_complete elapsedMs=64012`

これにより、Phase 8K で観測していた「read-only task が song DB monitor で待つ」問題は解消した。残る tail は DB lock 待ちではなく、playlist / chart_info の実 materialize、LR2IR player score XML fetch、playlist URL completion / reference apply などの実処理時間として扱う。

### Phase 8M: LR2IR Player Score XML Prefetch (implemented)

Phase 8M では、LR2ID が確定した時点で LR2IR player score XML の network fetch / XML parse / normalized score digest 計算を先行開始する。

- `LR2ID` は score table load 後に確定するため、`score_tbl_load` 完了直後に `ir_score_prefetch` を開始する。
- prefetch は DB に触れない。実行するのは `GetPlayerScoresXml(lr2Id)`、regex parse、hash dedupe、normalized score digest 計算までである。
- `ir_score_refresh_metadata` read、既存 `ir_score` read、digest 比較、`ir_score` replace、metadata upsert、`BMSScores` / `BMSFiles` への未送信反映は従来通り `ranking_refresh_deferred` 側で実行する。
- `ranking_refresh_deferred` は prefetch result の `lr2Id` と現在の `LR2ID` / `scoreDbPath` / 設定を検証し、current の場合だけ consume する。
- stale / failed / disabled / not started の場合は従来の同期 fetch 経路へ fallback する。
- score hydration 完了前に `updateBMSScores()` は実行しない。score snapshot と file owner attach の順序は維持する。
- log:
  - `ir_score_prefetch start/done/failed`
  - `ir_score_prefetch consume status=used|stale|failed|unavailable waitMs=...`
  - `ranking_refresh_deferred done` に `irScorePrefetchUsed`, `irScorePrefetchStatus`, `irScorePrefetchWaitMs`, `irScorePrefetchFetchMs`, `irScorePrefetchParseMs`, `irScorePrefetchDigestMs` を出す。

この変更により、player score XML の network 待ちを file scan / DB load / UI 初期化の裏に移せる。特に LR2IR 応答が数秒かかる起動では、`ranking_refresh_deferred` の synchronous `irScoreXmlFetchMs` が 0 に近づき、代わりに prefetch 側の timing と consume wait として観測できる。

2026-05-06 の実機確認では、`score_tbl_load` 直後に `ir_score_prefetch` が開始し、`fetchMs=789 parseMs=383 digestMs=111 parsedRows=17202 elapsedMs=1287` で完了した。`ranking_refresh_deferred` 側では `irScorePrefetchUsed=True`, `irScorePrefetchWaitMs=0`, `irScoreXmlFetchMs=0`, `irScoreXmlParseMs=0`, `irScoreDigestMs=0` となり、`irScoreMs=558`, `elapsedMs=2562` まで短縮した。`ranking_cache_refresh` は従来通り実行され、`cacheMs=2002` だった。

### Phase 8N: Startup Background Scheduler / Playlist Hydration Materialize Improvement (implemented)

Phase 8N は、DB lock 待ちが解消した後に残っている `startup_initialization_complete` の tail を削る。対象は 2 つに絞る。

- startup background scheduler の直列実行を見直し、read-only hydration を安全に並列開始できるようにする。
- `playlist_entries_hydration` の sqlite-net object materialize cost を削る。

現状の scheduler は `MainWindowViewModel.QueueStartupBackgroundTask()` / `TryStartStartupBackgroundTaskWorker()` が 1 本の worker で priority 順に task を実行する。`playlist_entries_hydration` の priority は 10、`chart_info_hydration` は 50、`maintenance_hydration` は 55 であり、dependency がない task でも前の task が終わるまで start しない。そのため、DB read-only 化後も `playlist_entries_hydration` が約 11 秒かかると、`chart_info_hydration` と `maintenance_hydration` の開始が後ろへ寄る。

Phase 8N では scheduler に lane を導入した。

- `read_hydration` lane
  - `playlist_entries_hydration`
  - `chart_info_hydration`
  - `maintenance_hydration`
  - `ranking_refresh_deferred` の read phase は既に BMSLibrary 側 direct task なので、scheduler lane へ無理に戻さない。
- `playlist_followup` lane
  - `playlist_url_completion`
  - `playlist_ref_apply`
  - `external_playlist_sync`
  - これらは `playlist_entries_hydration` 完了後にだけ実行する。
- `dependent_maintenance` lane
  - `installable_maintenance`
  - `chart_info_hydration,maintenance_hydration` の完了後にだけ実行する。

並列数は無制限にしない。初回実装では read hydration lane の並列数を 2、全体上限を 3 にした。`startup_background_summary` は lane を出し、単に task を expected phase から外して速く見せることはしない。

`ReloadTables` / `ReloadFileDiff` / `FullReinitialize` は post-startup operation なので、operation 開始時に background scheduler の metrics / queue を reset しても scheduler 自体は runnable に保つ。`Startup` だけは `startup_ready_operable` まで scheduler を開始しない。`playlist_entries_hydration` は `UpdateBMSTables` callback を終えてから completed version を publish し、playlist follow-up が table replacement より先に走らないようにする。

`playlist_entries_hydration` は `BMSPlaylist.EnsureAllPlaylistEntriesLoadedAsync()` から `BmsLibraryDbGateway.LoadStartupPlaylistEntries()` を呼び、`songDb.Query<BMSTableEntry>(sql)` で 556k rows を materialize している。現行ログでは `dbReadMs` と `materializeMs` が同じ値で、sqlite-net の reader + object materialize 境界の elapsed を示す。group / assign は 300ms 未満で、支配項は `BMSTableEntry` materialize である。

Phase 8N では playlist entry loader を raw reader / lightweight row materializer へ寄せた。

- `StartupPlaylistEntryRow` のような hydration 専用 DTO を追加し、DB read では property changed / dynamic parse / parent resolution を起動しない。
- grouping は DTO の `playlist_id` で行い、table assign 直前に `BMSTableEntry` へ materialize するか、table entries の正本を `BMSTableEntry` のまま維持しつつ constructor side effect を抑えた bulk factory を使う。
- `BMSTable.entries` setter が行う parent 付与、normalize、folder state rebuild、loaded mark は維持する。ここを迂回して高速化しない。
- active / removed row、`url`, `url_diff`, `name_diff`, `org_md5`, `adddate`, `comment`, `memo`, `sha256` は現行 semantics のため維持する。
- `playlist_id IS NULL` を読まない既存 projection は維持する。
- single table lazy load、external sync、playlist reload merge、URL completion、LR2 custom folder export は現行結果と一致させる。

2026-05-06 の Phase 8N 実機確認では、lane 化と raw materialize の両方が想定どおり動作した。

- `startup_background_task start` は `playlist_entries_hydration` と `chart_info_hydration` を同時刻に開始し、`lane=read_hydration` / `laneRunning=1,2` を出した。
- `playlist_entries_hydration done ... rows=556649 dbReadMs=8141 materializeMs=8141 groupMs=78 assignMs=317 totalMs=8570 entryLoadRowsPerMs=68`
  - Phase 8M 実測の `totalMs=11448` から短縮。
- `chart_info_hydration done ... totalMs=15563`
  - 並列 read の影響で単体 elapsed は Phase 8M 実測より伸びた。
  - ただし start が大きく前倒しされたため、初期化全体の tail は短縮した。
- `maintenance_hydration done ... elapsedMs=6643`
- `startup_initialization_complete elapsedMs=49733`
  - Phase 8M 実測 `64764ms` から約 15 秒短縮。
- `startup_background_summary` は各 task に `lane=read_hydration|playlist_followup|dependent_maintenance|default` を出した。

Acceptance criteria:

- `playlist_entries_hydration totalMs` が現行の 10-12 秒台から明確に下がる。
- `chart_info_hydration` / `maintenance_hydration` の start が `playlist_entries_hydration` 完了待ちにならない。
- `startup_initialization_complete` が短縮し、memory peak が許容範囲に収まる。
- `startup_background_summary` で lane、parallel start、dependency wait を説明できる。
- playlist hydration 後の table entries、removed entries、URL completion、playlist reference apply、playlist summary は現行と一致する。

### Key Changes

- playlist entries hydration
  - 必要 projection を整理し、summary / detail / reference apply で同じ rows を重複 materialize しない。
  - 非表示時は heavy presentation rebuild を避けるが、data load 自体の重複をなくす。
- startup hydration DB lock boundary
  - read-only hydration loader と write-capable transaction path を分離する。
  - read-only DB access は schema repair や table creation を行わない。
  - DB read phase と memory apply phase を分け、DB connection を保持したまま owner attach を行わない。
- chart_info hydration / backfill
  - `candidates=0` を確認するためだけの高コスト summary を避ける。
  - version / count / dirty marker で no-op を判定できる場合は DB full scan をしない。
- reverse lookup / resource index
  - `reverse_lookup_warmup_deferred` は存在しない。
  - native chart-relative resource index contract により、導入先推定に必要な reverse lookup surface は scan/index build の成果物に含める。
  - C# 側では native canonical result を materialize し、導入先推定では chart-relative key の single evaluator を使う。
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
  - `source=native_canonical|managed`
  - `directories`
  - `resources`
  - `chartRelativeKeys`
  - `reverseLookupKeys`
  - `reverseLookupSource=native|managed`
  - `nativePackMs`
  - `managedMaterializeMs`
  - `buildMs`
  - `payloadBytes`
- `song_tbl_load_projection`
  - `projection=install_ready|display|background`
  - `readMs`
  - `materializeMs`
  - `rows`
- `ranking_cache_refresh`
  - `dbReadMs`
  - `irDataDbReadMs`
  - `irDataMaterializeMs`
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
  - `irScoreXmlFetchMs`
  - `irScoreXmlParseMs`
  - `irScoreDbReplaceMs`
  - `irScoreMergeMs`
  - `irScoreParsedRows`
  - `cacheMs`
  - `elapsedMs`
- `playlist_entries_hydration`
  - `projection=startup_entries`
  - `readOnly=true`
  - `dbLockWaitMs`
  - `rows`
  - `dbReadMs`
  - `materializeMs`
  - `groupMs`
  - `assignMs`
  - `totalMs`
- `chart_info_hydration`
  - `readOnly=true`
  - `dbLockWaitMs`
  - `dbLoadMs`
  - `dbMaterializeMs`
  - `chartInfoRows`
  - `parseFailureRows`
  - `ownerCount`
  - `backfillCandidateOwners`
- `maintenance_hydration`
  - `readOnly=true`
  - `dbLockWaitMs`
  - `readMs`
  - `materializeMs`
  - `attachMs`
  - `indexBuildMs`
- `ranking_refresh_deferred`
  - `irScoreDbLockWaitMs`
  - `irDataDbLockWaitMs`
  - `irScoreDbLoadMs`
  - `irDataDbReadMs`

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
  - native scan result から chart-relative resource key と reverse lookup surface が得られる。
  - Everything API / service unavailable 時は managed scan result から同じ semantics の resource index が作られる。
  - native bridge contract mismatch / header mismatch / missing fixed scan export は fallback せず失敗する。
  - install estimation / maintenance / file operations が canonical resource index で同じ結果になる。
  - duplicate index build が残っていないことを resource tests で確認する。
  - `reverse_lookup_warmup_deferred` なしで導入先推定が動作する。
  - 複数 pending zip を別 batch のまま推定しても、destination reverse lookup の lazy build が batch ごとに発生しない。
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
- DB lock boundary
  - read-only startup hydration loader が writer connection path を使わない。
  - read-only loader 内で schema ensure / table creation / migration / repair を呼ばない。
  - write path は `ExecuteSongDbTransaction` 相当の短い writer boundary に残る。
  - `playlist_entries_hydration` と `ranking_refresh_deferred` の read phase が同じ song DB monitor で直列化されない。
  - lock wait metrics が loader log に出る。

## Operational Notes

- 通常起動はファイル列挙から destination resource index を作る。
- destination resource index には導入先推定の reverse lookup surface を含める。これを lazy に package batch 側へ持ち越さない。
- 所持譜面差分がある場合も、列挙結果を正本として catalog / resource index を収束させる。
- `ReloadFileDiff` は起動中の memory 正本を使えるため、通常起動とは別の軽量化を行う。
- 導入可能までを短くするために無関係 task は blocker から外すが、初期化全体の短縮対象から外さない。
- startup hydration は DB 補助情報の attach であり、migration / repair / compatibility fallback / hidden write の実行場所ではない。write が必要な cleanup / backfill / metadata update は明示的な write-capable path として分離する。
- read-only DB connection は読み取り専用として扱い、`CreateTable` や schema ensure を行わない。必要な schema は startup migration で収束済みであることを前提にする。
- 計画の各 phase は、不要になったコード、テスト、ログを同時に削除して完了とする。
