# 起動・初期化フロー 現行仕様

この資料は、BeMusicSeeker の起動、再初期化、リロードで現在正本として扱う初期化フローをまとめる。実装履歴ではなく、現行動作と守るべき境界だけを書く。

## 目的

初期化は次の 2 つを分けて扱う。

- 導入可能 readiness: 譜面の導入先推定と導入開始に必要な情報が揃った状態。
- 初期化全体の完了: UI 操作可能後に走る startup background task まで含めて収束した状態。

導入可能を早くするために必要な background work を単に後回しへ隠すのではなく、`startup_install_estimation_ready` と `startup_initialization_complete` の両方を観測する。

2026-05-07 時点の通常起動では、導入可能 readiness は概ね 20 秒前後、UI 操作可能は 21 秒前後まで短縮済みである。一方で `startup_initialization_complete` は 39 秒前後で、操作可能後の background tail がまだ残る。以後の主対象は導入可能 critical path ではなく、`chart_info_hydration` を中心とした startup background tail である。

## Startup

```text
Startup
  -> bmson migration preflight
  -> 必要なら警告と startup migration
  -> final preflight
  -> metadata bundle import
  -> catalog DB load
  -> score DB load と LR2IR player score XML prefetch
  -> file enumeration / native canonical resource index build
  -> file diff apply
  -> pending package restore
  -> install readiness
  -> UI refresh / operable
  -> startup background scheduler
```

`Startup` の migration と metadata import は DB load より前に完了させる。`chart_info` hydration/backfill は bmson schema migration の代替ではない。

## Operation Modes

| Mode | 入口 | 目的 | 主な処理 |
| --- | --- | --- | --- |
| `Startup` | アプリ起動 | DB と file system から正本 catalog / resource index / UI を構築する | migration、metadata import、catalog load、file diff、background hydration |
| `FullReinitialize` | ライブラリ右クリック `初期化再実行` | 外部 DB 編集や状態修復を想定して library 初期化を再実行する | catalog load、file enumeration、file diff、必要な background 補完 |
| `ReloadFileDiff` | ライブラリ右クリック `リロード` | DB は読み直さず、所持ファイルの追加・削除・更新だけを memory / DB に反映する | file enumeration、file diff、playlist reference apply |
| `ReloadTables` | playlist/table reload | playlist/table/score 系を再読込し、外部 playlist 同期を再スケジュールする | table header reload、playlist entries hydration、external playlist sync |

起動中の `song.db` は原則 BeMusicSeeker が更新するため、`ReloadFileDiff` は in-memory catalog と file scan result の差分を正本にする。LR2 や手動編集で DB が変わった可能性まで拾う場合は `FullReinitialize` を使う。

起動時 UI ではプレイリスト root を常に展開する。これは root item の展開だけで、配下 playlist の再帰展開ではない。

## bmson Migration

`BmsonMigrationPreflightService.Inspect()` は read-only 判定だけを行う。

| 判定 | 意味 | 起動時の扱い |
| --- | --- | --- |
| `NeedsPlaylistEntrySha256Migration` | playlist entry schema が現行ではない | 警告対象。OK 後に `BMSPlaylist.EnsureSchema()` で移行 |
| `NeedsBmsonAppSchemaMigration` | `app_schema_version(name='bmson_app_schema')` が現行ではない | 警告対象。OK 後に `CompleteBmsonStartupMigration()` で完了 |
| `RepairRequired` | `chart_digest_map` / `bmson_song` / index が欠損または互換外 | startup migration / repair で収束させる |

startup migration 後は必ず final preflight を行い、上記の未収束が残る場合は起動失敗として扱う。

`BmsLibraryDbGateway.EnsureBmsonSchema()` は schema/index presence の修復 helper であり、migration 完了印を書かない。`bmson_app_schema` を current にする正本 API は `CompleteBmsonStartupMigration()` である。

## Metadata Bundle Import

metadata bundle は所持譜面から生成した DB 由来情報ではなく、外部配布または同梱された `chart_info` 補助データである。

- import は `Startup` の DB load 前に行う。
- import 済み bundle は `imported_metadata/` へ退避する。
- `FullReinitialize` / `ReloadFileDiff` では再 import しない。
- bundle import は bmson migration の代替ではない。

## Library Load

`Startup` / `FullReinitialize` の library load は、migration と metadata import が終わった DB を前提にする。

| Phase | 正本の処理 | 備考 |
| --- | --- | --- |
| Catalog DB load | `song`, `bmson_song`, `chart_digest_map` などを読む | `maintenance` と `chart_info` 全件 hydration は background |
| Score DB load | LR2 score DB を読み、`LR2ID` 確定後に `ir_score_prefetch` を開始する | prefetch は DB に触らない |
| File enumeration | native bridge `EBridge_ScanChartAndResources` で root 配下を列挙し、native canonical resource index を作る | Everything API / service が使えない場合は managed scan に fallback する |
| File diff | in-memory catalog と scan result を比較し、新規・更新・削除を DB と memory に反映する | 新規・更新譜面の inline `chart_info` / maintenance はここで処理する |

native bridge と C# 側は同一ビルド成果物として扱う。Everything が使えない場合の managed scan fallback は残すが、古い native DLL / 旧 ABI / contract mismatch への互換 fallback は行わない。

Everything scan は install readiness に必要な destination resource index と reverse lookup surface を完成させる処理である。現行契約では、通常起動で全 audio / image / movie result を列挙し、resource-key -> candidate directory reverse lookup まで native scan 成果物に含める。これを未完成のまま `startup_install_estimation_ready` にしたり、pending package batch 側の lazy build へ持ち越したりしない。

resource index は chart-relative resource key を正本にする。`foo.wav` は `foo`、`sound/foo.wav` は `sound/foo` として扱い、旧 basename-only matching は使わない。native bridge / managed fallback scan は audio / image / movie のカテゴリ別 index とカテゴリ別 reverse lookup だけを作り、旧 all-resource surface は保持しない。folder-level hash が必要な箇所ではカテゴリ union をその場で派生する。

通常の native scan path では `LibraryResourceIndex` を native decoded arrays から直接構築し、`BmsScanResult` の resource dictionaries は materialize しない。`BmsScanResult` は file diff に必要な chart path / chart directory の carrier として使い、managed fallback scan とテスト用 merge path だけが resource dictionaries を持つ。

導入先推定は destination resource index を必須にする。`SkipInitFileCheck` のように起動時 file enumeration を明示的に省略した場合、resource index がないため導入先推定は `resource_index_unavailable` として推定不可になることがある。

## Install Readiness

導入先推定 / 導入開始に必要な情報は次の 3 つである。

- 所持 catalog: BMS / BMSON の path、hash、timestamp、installed membership、推定用 metadata。
- destination resource index: file enumeration 由来の audio / image / movie chart-relative resource key と reverse lookup。
- pending package state: pending package list と、source package resource surface を復元済みまたは推定開始時に構築可能であること。

`StartupInstallReadinessState` は `CatalogLoaded && DestinationResourceIndexReady && PendingPackagesRestored` を満たしたとき `InstallEstimationReady` に遷移する。`maintenance_hydration`、`chart_info_hydration`、playlist hydration、score/ranking refresh は install readiness の blocker にしない。

導入可能 readiness の最新の支配項は Everything scan / native bridge である。`song_tbl_load` 由来の catalog load は 3 秒台まで短縮済みだが、file enumeration と並走しており、現状の導入可能 wall clock では Everything scan に隠れる。`song_tbl_load` の micro optimization は、導入可能短縮の主対象にはしない。

| Log | 意味 |
| --- | --- |
| `startup_install_estimation_ready` | pending estimate queue を開始できる |
| `startup_install_ready` | 現行では install estimation readiness と同じ境界 |
| `startup_ready_data` / `startup_ready_ui` / `startup_ready_install` / `startup_ready_operable` | UI 側の表示・操作可能境界 |
| `startup_initialization_complete` | expected background phase と startup scheduler queue が空になった |
| `startup_background_summary` | background task の queue/start/complete/failed/elapsed/lane/dependency summary |

## Startup Background Scheduler

startup background scheduler は `MainWindowViewModel.QueueStartupBackgroundTask()` 経由で登録される task を、dependency と lane concurrency に従って実行する。

| Lane | Task | 並列数 | Dependency |
| --- | --- | --- | --- |
| `read_hydration` | `playlist_entries_hydration`, `chart_info_hydration`, `maintenance_hydration` | 2 | なし |
| `playlist_followup` | `playlist_url_completion`, `playlist_ref_apply`, `external_playlist_sync` | 1 | playlist entries 完了後、または task 内の ensure 後に進める |
| `dependent_maintenance` | `installable_maintenance` | 1 | `chart_info_hydration,maintenance_hydration` |
| `default` | その他の短い prewarm など | 1 | task ごと |

全体 concurrency は 3。`startup_background_summary` は task ごとに lane と dependency を出す。

`Startup` では scheduler は `startup_ready_operable` 到達まで開始しない。`ReloadTables` / `ReloadFileDiff` / `FullReinitialize` は既に UI operable 後の operation なので、operation 開始時の reset 後も scheduler を runnable に保つ。これは reload 中に `playlist_entries_hydration` や `external_playlist_sync` を queue したまま止めないための仕様である。

直近ログでは、background tail の支配項は `chart_info_hydration` である。`playlist_entries_hydration` と `maintenance_hydration` は lane により並走するが、`chart_info_hydration` は full `chart_info` row load / materialize が重く、`startup_initialization_complete` までの最後の長い task になりやすい。次に短縮する場合は、task を expected phase から外すのではなく、`chart_info_hydration` の no-op skip / persistent hydrated index / projection 設計を見直す。

## DB Access Policy

startup hydration の主要 read phase は `OpenSongDbReadOnly()` / `OpenScoreDbReadOnly()` を使う。read-only connection は `SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex` で開き、process-local static monitor を取得しない。

read-only hydration loader のルール:

- loader 内で `CreateTable`、schema ensure、migration、repair を行わない。
- DB read phase は row / DTO / dictionary を返すだけにし、DB connection を閉じてから memory owner attach を行う。
- cleanup、backfill、metadata update、`ir_score` replace、`ir_data` upsert、file diff commit は短い write-capable transaction path として明示する。
- loader log は `readOnly=true` と `dbLockWaitMs` を出す。

読み取り専用化済みの主な処理:

- startup score table load
- playlist header load
- `playlist_entries_hydration`
- `chart_info_hydration`
- `maintenance_hydration`
- `ranking_refresh_deferred` の `ir_score` / `ir_data` read

`song.dbアクセス最適化PRAGMAを有効にする` が有効な場合、接続ローカルに `temp_store=MEMORY`、`cache_size=-262144`、`mmap_size=2147483648` を適用する。

## Background Hydration

| Task | 正本の責務 |
| --- | --- |
| `playlist_entries_hydration` | `playlist_id IS NOT NULL` の playlist entries を raw reader / bulk factory で materialize し、`BMSTable.entries` setter で table に attach する |
| `playlist_ref_apply` | playlist entries 完了後に library item と playlist reference を結び直す |
| `playlist_url_completion` / `external_playlist_sync` | playlist entries 完了後に URL 補完・外部 playlist 同期を行う |
| `chart_info_hydration` | DB の current `chart_info` と current parse failure を memory owner / session index へ適用する |
| `chart_info_backfill` | 不足がある場合だけ補完する。全 owner が current の場合は `reason=hydration_all_current` で skip する |
| `maintenance_hydration` | DB の persisted maintenance snapshot を owner へ attach し、resource health index を valid snapshot から rebuild する |
| `installable_maintenance` | `chart_info_hydration` と `maintenance_hydration` 完了後に missing/stale maintenance を補完する |
| `score_hydration_deferred` | DB ではなく memory score snapshot を `BMSFile` へ attach する |
| `ranking_refresh_deferred` | `ir_score` 系の未送信検出と `ir_data` / cache XML 系の ranking 情報を更新する |

`maintenance` は通常、譜面導入時または明示 rescan 時に計算された snapshot として扱う。`BMSFile.maintenanceInfo` の lazy default は `MaintenanceInfoOrigin.Placeholder` であり、resource health の valid snapshot ではない。valid snapshot は DB 由来 `DbHydrated`、file diff / 導入 / 手動 rescan 由来 `Calculated` に限定する。

不足 resource を後から追加した場合や、隣接 resource を削除した場合の再評価は通常起動では行わない。行右クリックの `ファイルスキャン > 再スキャン` は選択行、`ファイルスキャン > 全譜面を再スキャン` は owned BMS 全件 + installed bmson 全件を重い明示操作として再計算する。

## Ranking Refresh

`ranking_refresh_deferred` は 2 系統に分かれる。

| 系統 | DB table | 用途 | 設定 |
| --- | --- | --- | --- |
| player score XML / `ir_score` | `ir_score`, `ir_score_refresh_metadata` | `SCORE_UNSENT` と LR2 custom folder `UNSENT SONGS` | `LR2IRのスコアをDLしIR未送信を検出する` |
| ranking cache / `ir_data` | `ir_data` | ranking 表示、offline score ranking estimation | `SkipEstimateOfflineScoreRanking` など |

`LR2IRのスコアをDLしIR未送信を検出する` が false の場合、player score XML fetch、`ir_score` DB 更新、`ir_score` 由来の未送信検出を使わない。`ir_data` / ranking cache は維持する。

`ir_score_prefetch` は LR2ID 確定直後に player score XML fetch、XML parse、normalized score digest 計算までを先行する。`ranking_refresh_deferred` は current な prefetch result を consume し、metadata read、既存 `ir_score` read、replace / metadata upsert、memory merge を行う。LR2IR player score XML の `lastupdate` は譜面 hash 側の LR2IR 更新時刻として変わる可能性があるため、normalized digest では無視する。

## ReloadFileDiff

```text
ReloadFileDiff
  -> file enumeration / resource index build
  -> in-memory catalog との差分検出
  -> added/updated charts の snapshot read / parse / inline chart_info / inline maintenance / commit
  -> deleted charts の unregister
  -> memory catalog / resource index swap
  -> playlist reference apply
```

差分が 0 件の場合は DB commit、chart_info hydration/backfill、installable maintenance を発生させない。

## ReloadTables

```text
ReloadTables
  -> table header reload
  -> score only initialize
  -> playlist_entries_hydration を startup scheduler へ queue
  -> external_playlist_sync を playlist_entries_hydration dependency 付きで queue
```

`ReloadTables` は post-startup operation なので、scheduler reset 後も scheduler は runnable である。`playlist_entries_hydration` は `UpdateBMSTables` callback まで終えてから completed version を publish し、その後 `playlist_ref_apply` / `external_playlist_sync` が進む。

## 守るべき境界

- migration 完了印を `chart_info` backfill の副作用として書かない。
- startup hydration worker 内で schema ensure、migration、repair、hidden write を行わない。
- read-only loader と write-capable transaction path を同じ phase に混ぜない。
- DB connection を保持したまま、大量の memory owner attach や UI notification を行わない。
- 導入可能 readiness を、playlist hydration、score/ranking refresh、chart_info hydration、maintenance hydration の完了に依存させない。
- startup background task を expected phase から外して初期化完了を短く見せない。必要な task は `startup_background_summary` に残す。

## 関連資料

- `devdocs/current-startup-reload-progress.md`
- `devdocs/current-chart-file-read-pipeline.md`
- `devdocs/empty-db-first-startup-optimization-plan.md`
- `devdocs/bmson/library-scan-fast-path-resource-index-plan.md`
