# Startup Initialization and Install Readiness Optimization Plan

## Summary

この計画の目的は、特定のログ区間を短く見せることではない。目的は次の 2 つを同時に満たすこと。

- 起動から譜面の導入先推定と導入が可能になるまでの時間を短縮する。
- background を含む初期化全体の完了時間を短縮する。

導入先推定には所持譜面 catalog だけでなく、導入先候補 directory の resource index が必要である。したがって、通常起動で chart-only diff を先行して resource scan / index 構築を省く方針は採用しない。起動時は現行と同じくファイル列挙を正本とし、その列挙結果から導入先推定に必要な resource index を作る。

一方で、現状は file diff 自体よりも、DB materialize、native scan payload、resource index の重複構築、background hydration が支配的である。最適化の主対象はここに置く。

## Phase 0 Log Findings

2026-05-05 06:05 頃の reverted 後ログでは、旧 chart-only 実験ログは出ていない。

導入可能までの critical path は概ね次の通り。

- DB load / materialize
  - `phase1_min_load_ms=20619`
  - `song_tbl_load_ms=19922`
  - `song_read_ms=9591`
  - `maintenance_read_ms=4476`
- file enumeration / resource index
  - `everything_scan totalMs=27183`
  - `nativeBridgeMs=26202`
  - `bridgeRawBufferBytes=331802176`
  - `dirhash_build_ms=10406`
  - `resource_lookup_cache_ms=6677`
  - `relative_path_hash_index_ms=3702`
- diff / apply
  - `diff_ms=615`
  - `apply_ms=39`
  - `db_commit_chunks=0`
- readiness
  - `startup_ready_installable elapsedMs=38825`
  - `startup_ready_operable elapsedMs=40895`

初期化全体の後半では次が重い。

- `playlist_entries_hydration totalMs=29232`
- `chart_info_hydration totalMs=11818`
- `chart_info_backfill candidate_summary_done elapsedMs=4522` かつ `candidates=0`
- `reverse_lookup_warmup_deferred elapsedMs=15191`
- `ranking_refresh_deferred elapsedMs=49692`

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

### Install Ready

実際の導入操作を開始してよい状態。

`Install Estimation Ready` に加えて、次を満たす。

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

revert 後の状態を基準にし、導入可能までと初期化完了までを別々に観測する。

### Key Changes

- `startup_install_estimation_ready` を追加する。
  - catalog / resource index / pending package state が揃った時点。
- `startup_install_ready` を追加する。
  - 手動導入を安全に開始できる時点。
- `startup_initialization_complete` を追加する。
  - background hydration / warmup / refresh が完了した時点。
- `startup_background_summary` を追加する。
  - playlist、chart_info、ranking、reverse lookup、maintenance の elapsed / rows / skipped reason をまとめる。
- 旧実験ログ名や旧 fast path 用ログは、実装が存在しないなら残さない。

### Acceptance Criteria

- 導入可能までの elapsed と、初期化完了までの elapsed を別々に比較できる。
- Phase 0 log で、DB load、resource index、background hydration の寄与が分かる。
- 以後の phase は、片方の metric だけを改善してもう片方を悪化させた場合に検出できる。

## Phase 1: Install Readiness Contract

導入先推定 / 導入の readiness を `InitializedAll` や UI operable から分離する。

### Key Changes

- internal readiness state を定義する。
  - `CatalogLoaded`
  - `DestinationResourceIndexReady`
  - `PendingPackagesRestored`
  - `InstallEstimationReady`
  - `InstallReady`
  - `InitializationComplete`
- pending estimate queue は `InstallEstimationReady` 後に開始する。
- 手動推定 / 手動導入 UI は `InstallEstimationReady` / `InstallReady` を見る。
- playlist、score、ranking、chart_info、deferred maintenance は install readiness blocker にしない。
- readiness token は一方向遷移にし、後続 task の失敗で意味が曖昧にならないようにする。

### Acceptance Criteria

- pending estimate が playlist / score / chart_info background task を待たない。
- resource index がない場合は推定も導入も enable されない。
- `startup_ready_installable` より意味が明確な導入用 readiness log が得られる。

## Phase 2: Startup DB Projection Reduction

起動 early path で読む DB projection を、導入可能に必要な情報へ絞る。これにより導入可能までを短縮し、不要な再 materialize を消して初期化全体も短縮する。

### Key Changes

- install-ready catalog projection を定義する。
  - path / parent path / folder id。
  - md5 / sha256 / last write timestamp。
  - BMS / BMSON identity。
  - install destination / package membership。
  - install safety に必要な最小 maintenance fields。
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

## Phase 3: Enumeration-Based Resource Index Consolidation

通常起動では file enumeration を正本として destination resource index を作る。その前提で、重複 index と重複 materialize を削る。

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
