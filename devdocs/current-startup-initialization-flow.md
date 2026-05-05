# 起動・初期化フロー 現行仕様

この資料は、移行前 `song.db` と移行後 `song.db` の両方に対して、BeMusicSeeker が取るべき唯一の起動・初期化フローをまとめる。

方針は「preflight で状態を判定し、必要な migration を起動直後に完了させ、最後に再検査で収束を確認する」ことである。互換のために古い migration 経路を background 処理や `chart_info` backfill の副作用として残さない。

## 正本フロー

```text
Startup
  -> bmson migration preflight inspect
       read-only
  -> warning required?
       OK: continue
       Cancel: shutdown
  -> startup migration
       BMSPlaylist.EnsureSchema()
       CompleteBmsonStartupMigration() when bmson app schema migration or repair is needed
       EnsureBmsonSchema() when only app-owned schema/index presence must be guaranteed
  -> final preflight inspect
       playlist sha256 migration, bmson app schema migration, repair issue が残れば fail
  -> metadata bundle import
  -> catalog DB load
       song / bmson_song / chart_digest_map
       maintenance / chart_info are not loaded here
  -> file enumeration / resource index build
  -> file diff
       new/updated charts are read, parsed, committed, and reflected here
  -> operation ready
  -> startup background tasks
       maintenance hydration
       playlist hydration
       chart_info hydration / full backfill for DB-derived candidates
       installable maintenance for DB-derived missing/stale rows
       score / ranking refresh
```

## Operation Modes

| Mode | 入口 | 目的 | DB read / background 補完 |
| --- | --- | --- | --- |
| `Startup` | アプリ起動時 | migration と metadata import 済み DB から正本を構築する | 行う |
| `ReloadFileDiff` | ライブラリ右クリック `リロード` | アプリ外のファイル追加・削除・移動だけを検出し、memory / DB に反映する | 行わない |
| `FullReinitialize` | ライブラリ右クリック `初期化再実行` | DB 外部編集や状態修復を想定し、起動時に近い初期化を再実行する | 行う |
| `ReloadTables` | playlist/table reload | table / playlist / score 系を更新する | ライブラリ file diff は行わない |

起動中の `song.db` は原則 BeMusicSeeker が更新するため、通常の `リロード` は in-memory catalog と file scan result の差分だけを見る。LR2 や手動編集で DB が変わった可能性まで拾う場合は `FullReinitialize` を使う。

起動時 UI ではプレイリスト root を常に展開する。これは root item の展開だけであり、配下プレイリストを再帰展開しない。

## bmson Migration

### Preflight

`BmsonMigrationPreflightService.Inspect()` は read-only の判定だけを行う。

| 判定 | 意味 | 起動時の扱い |
| --- | --- | --- |
| `NeedsPlaylistEntrySha256Migration` | playlist entry schema が現行ではない | 警告対象。OK 後に `BMSPlaylist.EnsureSchema()` で移行 |
| `NeedsBmsonAppSchemaMigration` | `app_schema_version(name='bmson_app_schema')` が現行ではない | 警告対象。OK 後に `CompleteBmsonStartupMigration()` で完了 |
| `RepairRequired` | `chart_digest_map` / `bmson_song` / index が欠損または互換外 | 警告不要の修復、または警告後の修復として扱う |

preflight は DB を変更しない。起動時 UI は、この結果だけを見て警告表示と migration 実行の要否を決める。

### Startup Migration API

| API | 責務 | `bmson_app_schema` version |
| --- | --- | --- |
| `BMSPlaylist.EnsureSchema(path)` | playlist / playlist_entry の現行 schema 化 | 書かない |
| `BmsLibraryDbGateway.EnsureBmsonSchema()` | app-owned table / index の作成・修復 | 書かない |
| `BmsLibraryDbGateway.CompleteBmsonStartupMigration()` | bmson startup migration の完了処理。`EnsureBmsonSchema()`、`chart_digest_map` 整合修復、version 書き込みを同一 transaction で行う | 書く |

`EnsureBmsonSchema()` は schema 修復 helper であり、migration 完了印を付ける API ではない。移行前 DB を起動時に current 化する経路は `CompleteBmsonStartupMigration()` だけにする。

### 収束確認

startup migration 後は必ず `Inspect()` をもう一度実行する。

次のいずれかが残る場合、起動 migration は失敗として扱う。

- `NeedsPlaylistEntrySha256Migration`
- `NeedsBmsonAppSchemaMigration`
- `RepairRequired`

これにより、移行前 DB で `bmson_app_schema` が書かれないまま起動を継続し、次回起動でも同じ警告が出る状態を防ぐ。

## chart_info と bmson Migration の分離

`chart_info` hydration / full backfill は、DB 由来の譜面メタデータ補完である。bmson app schema migration の完了印を書いてはいけない。

特に次の経路は正本ではない。

- full backfill 完了時に `bmson_app_schema` を current として mark する。
- no-candidate backfill の skip を migration 完了扱いにする。
- `chart_info` parser version 更新と bmson app schema version を同じ判断で扱う。

bmson app schema の current 判定は preflight と `CompleteBmsonStartupMigration()` の責務で完結させる。

## metadata Bundle Import

metadata bundle は、リリースパッケージ同梱または外部配布の `chart_info` 補助データである。所持譜面から生成された DB 由来の補助情報ではない。

- import タイミングは起動直後に固定する。
- `.db` / `.7z` の探索と import history 判定は、DB load より前に行う。
- import 済み bundle は `imported_metadata/` へ退避し、次回以降の SHA-256 計算と展開を避ける。
- import は bmson migration の代替ではない。bundle の有無にかかわらず、preflight migration は先に収束させる。

## Library Load

`Startup` / `FullReinitialize` の本体は、migration と metadata import が終わった DB を前提に動く。metadata bundle import は `Startup` 専用で、`FullReinitialize` と `ReloadFileDiff` では再 import しない。

| Phase | 主な処理 | 備考 |
| --- | --- | --- |
| Catalog DB load | `song`, `bmson_song`, `chart_digest_map` などを読む | `maintenance` と `chart_info` 全件 hydration は起動 critical path から外す |
| File enumeration | native bridge `EBridge_ScanChartAndResources` で root 配下を列挙し、native canonical resource index を作る。Everything API / service が使えない場合は managed scan に fallback する | 導入先推定に必要な destination resource index の正本 |
| File diff | 新規・更新譜面を snapshot read し、軽量 parse、LR2 parent/folder、inline `chart_info`、inline maintenance、chunk commit まで行う | 新規・更新ファイル由来の補助情報はここで処理する |

差分なしの場合は、導入先推定に必要な index を最速で公開し、DB 由来の補助情報は background へ回す。

差分ありの場合は、読んだファイルの近くで DB と memory へ反映し、再起動しないと正しくならない状態を作らない。

`song.dbアクセス最適化PRAGMAを有効にする` が有効な場合、`song.db` の DB load と file diff commit 用接続へ `temp_store=MEMORY`、`cache_size=-262144`、`mmap_size=2147483648` を接続ローカルに適用する。設定キーと既存ログ名は互換性のため `EnableReadOptimizedPragmas` / `db_read_pragmas` を維持する。

現行の `LR2SongDBExtended` / `LR2ScoreDBExtended` は、接続生成時に process-local static `Monitor` を取得し、`Dispose()` まで保持する。これは write の安全性には寄与しているが、startup background の read-only hydration も writer 相当の排他として扱ってしまう。次フェーズでは、startup migration / schema repair が完了していることを前提に、hydration DB access を read-only loader へ移し、write-capable transaction path と分離する。

## Install Readiness

導入先推定 / 導入の readiness は、UI の `startup_ready_*` とは別に model 側で判定する。

| Log | 意味 | UI operable との関係 |
| --- | --- | --- |
| `startup_install_estimation_ready` | catalog、destination resource index、pending package state が揃い、pending estimate queue を開始できる | UI refresh 完了を待たない |
| `startup_install_ready` | 現時点では `InstallEstimationReady` 直後の導入 readiness ログ境界 | 現状は enable 条件や lock 構造を変更しない |
| `startup_initialization_complete` | startup progress の expected background phase と startup scheduler queue が完了した | 導入可能より後。初期化全体の比較用 |
| `startup_background_summary` | startup background task の queue / start / complete / failed / elapsed summary | `startup_initialization_complete` と同じタイミングで出る |
| `startup_ready_data` / `startup_ready_ui` / `startup_ready_install` / `startup_ready_operable` | UI refresh / 操作可能表示の進捗 | install readiness とは別の観測点 |

Phase 0-1 時点では、`InstallEstimationReady` の完了位置は旧 `startup_ready_installable` と同じである。これは高速化ではなく、後続の DB projection / resource index 統合で「導入可能まで」と「初期化全体」を分けて測るための境界固定である。

Phase 2A 以降、`startup_install_estimation_ready` は catalog load、file enumeration / resource index build、file diff apply、pending package restore が揃った時点で出る。DB `maintenance` 全件は `maintenance_hydration` background task で同じ `BMSFile` / `bmson_song` instance へ in-place apply されるため、導入先推定の blocker ではない。

2026-05-05 08:23 の Phase 0 実測では、`startup_install_estimation_ready` / `startup_install_ready` は `elapsedMs=37971`、`startup_ready_operable` は `elapsedMs=39914`、`startup_initialization_complete` は `elapsedMs=99997` だった。`wait_continuation_start_ms`、`wait_continuation_signal_ms`、`wait_continuation_tasks_ms` はすべて 0ms であり、この回の critical path は DB load / materialize と file enumeration / resource index build である。

2026-05-05 09:09 の Phase 2A 実測では、`startup_install_estimation_ready` / `startup_install_ready` は `elapsedMs=35961`、`startup_ready_operable` は `elapsedMs=37401`、`startup_initialization_complete` は `elapsedMs=103325` だった。catalog load から `maintenance` 全件が外れたため導入可能までは短縮しているが、初期化全体は background cost が残っている。

同じ実測で `startup_background_summary` は `queued=10 started=10 completed=10 failed=0` だった。background 側の重い処理は `ranking_refresh_deferred=53007ms`、`playlist_entries_hydration=31501ms`、`maintenance_tbl_check_deferred=22749ms`、`chart_info_hydration=17103ms`、`reverse_lookup_warmup_deferred=16586ms` であり、導入可能までとは別に初期化全体の短縮対象として扱う。

Phase 2A 後の `startup_background_summary` は `queued=11 started=11 completed=11 failed=0` で、主な内訳は `ranking_refresh_deferred=47962ms`、`playlist_entries_hydration=26865ms`、`maintenance_tbl_check_deferred=18147ms`、`maintenance_hydration=17820ms`、`chart_info_hydration=15458ms`、`reverse_lookup_warmup_deferred=12633ms` である。

Phase 8B 以降、`maintenance_tbl_check_deferred` は存在しない。orphan maintenance cleanup は `maintenance_hydration` 内で、DB から読んだ `maintenance` key と current `BMSFiles` / `BmsonSongs` owner path set の差分として処理する。stale 判定と DB delete は同じ BMS catalog lock の内側で行い、判定後に追加された live owner の row を削除しない。`chart_info_hydration` 後に全 owner が current `chart_info` または current parse failure で収束している場合は、`chart_info_backfill candidate_summary` を呼ばず `reason=hydration_all_current` で skip する。

2026-05-05 10:09 の Phase 8B 実測では、`startup_install_estimation_ready=38926ms`、`startup_ready_operable=40209ms`、`startup_initialization_complete=103890ms` だった。`startup_background_summary` は `queued=10 started=10 completed=10 failed=0` で、`maintenance_tbl_check_deferred` は出ていない。`chart_info_hydration` は `ownerCount=210030 currentChartInfoOwners=210006 currentParseFailureOwners=24 backfillCandidateOwners=0 totalMs=16303` で、`chart_info_backfill skipped reason=hydration_all_current` により candidate summary を呼んでいない。`maintenance_hydration` は `readMs=3671 materializeMs=3655 mapBuildMs=170 applyMs=18447 cleanupDeleted=0 cleanupMs=0 ownerPathCount=210030 stalePathCount=0 elapsedMs=22308` で、cleanup 統合は成功しているが apply cost が次の大きな対象として残る。

Phase 4B / 5A 以降、通常時の resource index は native bridge の canonical result を正本とする。`reverse_lookup_warmup_deferred` は存在せず、導入先推定に必要な resource-key -> candidate directory reverse lookup は file enumeration / resource index build 完了時点で揃う。Everything API / service が使えない場合は managed scan に fallback し、同じ chart-relative semantics の resource index を作る。native bridge contract mismatch / header mismatch / fixed scan export missing は古い DLL 不一致として扱い、fallback せず初期化失敗にする。導入先推定は `foo.wav` を `foo`、`sound/foo.wav` を `sound/foo` という拡張子なし chart-relative resource key として評価し、旧 basename-only fast path は使わない。

Phase 4B / 5A 追加修正後、native contract `2026050503` では旧 `base` field も chart-relative resource key hash を返す。base / relative が同じ category は native packed result 内で同じ blob を指し、managed decode と `DirectoryResourceLookupCache` materialize も配列を再利用する。startup の正本として `DirectoryRelativePathHashIndex` は構築しない。native reverse map は flat pair sort で作り、managed decode は native indices から `string[]` を直接作る。

2026-05-05 19:15 の Phase 8H / 8I / 8J 実測では、`startup_install_estimation_ready=31712ms`、`startup_ready_operable=32899ms`、`startup_initialization_complete=64765ms` だった。`startup_background_summary` は `queued=9 started=9 completed=9 failed=0` で、主な内訳は `playlist_entries_hydration=11375ms`、`ranking_refresh_deferred=11004ms`、`chart_info_hydration=10337ms`、`score_hydration_deferred=4817ms`、`maintenance_hydration=4339ms` である。playlist は `projection=startup_entries` の gateway loader、ranking は `ir_data WHERE lr2id = ?` loader、chart_info は gateway hydration loader を使う。ranking の残コストは `irScoreDbReplaceMs=6617` が支配的であり、Phase 8K では player score XML が同一の場合に `ir_score` replace を skip する。

`ranking_refresh_deferred` は 2 系統に分かれる。`player score XML / ir_score` 系は LR2IR の player score XML を取得し、ローカル score と IR score の差分から `SCORE_UNSENT` と LR2 custom folder `UNSENT SONGS` を作る。`ranking cache / ir_data` 系は譜面ごとの ranking cache XML と `ir_data` を使い、ランキング表示や offline score ranking estimation を行う。設定 `LR2IRのスコアをDLしIR未送信を検出する` が false の場合、前者だけを完全に無効化し、後者は維持する。

LR2IR player score XML の `lastupdate` はプレイヤー単位の最終更新ではなく、譜面 hash 側の LR2IR 更新時刻として変わる可能性がある。`ir_score.lastupdate` は `SCORE_UNSENT` 判定や表示用 `rankingLastupdate` の正本ではないため、player score XML の normalized digest では無視する。表示用の ranking update は `ir_data` / ranking cache 側から反映する。

Phase 8M 以降、`player score XML / ir_score` 系は LR2ID が score table load で確定した直後に `ir_score_prefetch` を開始する。prefetch は DB に触れず、LR2IR player score XML の fetch、XML parse、normalized digest 計算までを行う。`ranking_refresh_deferred` は score hydration 完了後に prefetch result を検証し、current な結果だけを consume する。DB metadata read、既存 `ir_score` read、replace / metadata upsert、`BMSScores` / `BMSFiles` への未送信反映は従来通り `ranking_refresh_deferred` 側に残す。これにより、LR2IR network 待ちを file scan / DB load / UI 初期化の裏へ移しつつ、score owner attach の順序は維持する。

2026-05-06 の Phase 8M 実測では、`ir_score_prefetch` は `score_tbl_load` 直後に開始し `elapsedMs=1287` で完了した。`ranking_refresh_deferred` は `irScorePrefetchUsed=True`, `irScorePrefetchWaitMs=0`, `irScoreXmlFetchMs=0`, `irScoreXmlParseMs=0`, `irScoreDigestMs=0`, `irScoreMs=558`, `elapsedMs=2562` だった。player score XML fetch/parse/digest は初期化中の空き時間へ移動し、`ranking_refresh_deferred` の中では DB read と memory merge だけが残る。

導入先推定に必要な情報は次の 3 つに整理する。

- 所持 catalog: BMS / BMSON の path、hash、timestamp、installed membership、推定用代表 metadata。
- destination resource index: native file enumeration 由来の audio / image / movie chart-relative resource key と reverse lookup。
- pending package state: pending package list と、source package resource surface を復元済みまたは推定開始時に構築可能であること。

playlist hydration、score / ranking refresh、chart_info hydration / backfill、maintenance hydration は install readiness の blocker にしない。
ただし `startup_initialization_complete` と `startup_background_summary` で background を含む初期化完了も観測し、導入可能までの短縮が初期化全体の悪化を隠さないようにする。

## ReloadFileDiff

`ReloadFileDiff` は DB を読み直さず、現在の `BMSFiles` / `BmsonSongs` を正本として file scan result と比較する。

```text
ReloadFileDiff
  -> file enumeration / resource index build
  -> file diff against in-memory catalog
  -> added/updated charts
       snapshot read
       lightweight parse
       inline chart_info
       inline maintenance
       chunk commit
  -> deleted charts
       unregister from DB / memory
  -> memory catalog and resource index swap
  -> playlist reference apply
```

差分が 0 件の場合は DB commit、chart_info hydration/backfill、installable maintenance deferred を発生させない。

## Startup Background Tasks

background task は、既に DB に存在している owner の補助情報を補完するためのものに限定する。

Startup background の DB hydration は gateway 経由の明示 loader を使う。`Table<T>().ToList()` を worker 内で直接呼ぶ形は避け、loader ごとに projection 名、row count、SQLite query/materialize elapsed、group / assign の timing をログできるようにする。sqlite-net の `Query<T>` は reader と object materialize が一体なので、現行の `dbReadMs` / `materializeMs` は loader 境界の同一 elapsed を示す。これは playlist、chart_info、ranking のように初期化完了時間を支配しやすい処理で、DB 読み込み方針がばらつくことを防ぐためのルールである。

Phase 8L 以降、startup hydration の主要 read phase は `OpenSongDbReadOnly()` / `OpenScoreDbReadOnly()` を使う。read-only connection は `SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex` で開き、`LR2SongDBExtended` / `LR2ScoreDBExtended` の process-local static monitor を取得しない。これにより、`playlist_entries_hydration` の長い song DB read が `ranking_refresh_deferred` の `ir_score` / `ir_data` read を同じ monitor で待たせる状態を避ける。

read-only hydration loader のルール:

- `OpenSongDbReadOnly()` / `OpenScoreDbReadOnly()` 相当の読み取り専用経路を使う。
- loader 内で `CreateTable`、`EnsureBmsonSchema`、`EnsureChartInfoSchema`、`CompleteBmsonStartupMigration` を呼ばない。
- 必要 schema がない場合は、startup migration / preflight の漏れとして fail させる。
- DB read phase は row / DTO / dictionary を返すだけにし、DB connection を保持したまま memory owner attach を行わない。
- memory apply phase は DB connection を閉じた後、必要最小限の catalog / score lock で行う。
- cleanup、backfill、metadata update、`ir_score` replace、`ir_data` upsert、file diff commit は read-only hydration と同じ loader に混ぜず、短い write-capable transaction path として明示する。

| Task | 正本の責務 |
| --- | --- |
| `maintenance_hydration` | DB の既存 `maintenance` row を persisted health snapshot として memory owner へ in-place attach し、warning / health projection を更新する。orphan maintenance cleanup もここで行う |
| `chart_info_hydration` | DB の current `chart_info` を memory owner / session index へ適用する |
| full `chart_info` backfill | 旧 DB や外部操作により不足している `chart_info` を補完する。hydration 時点で全 owner が current 済みなら skip する |
| `installable_maintenance_deferred` | `maintenance_hydration` と `chart_info_hydration` の完了後、DB 由来の missing/stale maintenance を補完する |
| score / ranking / playlist hydration | 操作可能後に反映できる DB 由来データを適用する |

`playlist_entries_hydration` は `projection=startup_entries` の gateway loader で `playlist_id IS NOT NULL` の playlist entry だけを読み、active / removed row の両方を既存 semantics のまま memory table へ attach する。`ranking_refresh_deferred` は `ir_data` を `WHERE lr2id = ?` で読み、`ranking_cache_refresh` と LR2IR score table 更新の内訳を分けてログする。`chart_info_hydration` は full row load を維持するが、`chart_info` と current parse failure を gateway loader で読み、DB read と materialize を分けて観測する。

DB read-only 化済みの対象は、`playlist_entries_hydration`、`chart_info_hydration`、`maintenance_hydration`、`ranking_refresh_deferred` の `ir_score` / `ir_data` read、起動時 `LoadScoreTable()`、playlist header load である。`score_hydration_deferred` は DB を読まず、memory score snapshot を `BMSFile` へ反映する task なので、DB lock 分離ではなく memory apply 側の改善対象として扱う。

各 loader log は `readOnly=true` と `dbLockWaitMs` を出す。read-only connection は static monitor を取らないため、writer monitor 起因の待ち時間は 0 になる。schema ensure、migration、repair、table creation は hydration loader 内では行わず、startup constructor / migration phase または明示 write path の責務にする。

2026-05-06 の Phase 8L 実測では、`score_tbl_load`、`playlist_init_header`、`playlist_entries_hydration`、`ranking_cache_refresh`、`ranking_refresh_deferred` の `ir_score` load、`chart_info_hydration`、`maintenance_hydration` がすべて `readOnly=true dbLockWaitMs=0` で動作した。`startup_initialization_complete=64012ms`、`playlist_entries_hydration=10887ms`、`ranking_refresh_deferred=10516ms`、`chart_info_hydration=10115ms`、`maintenance_hydration=4305ms` であり、DB monitor 待ちは解消した。以後は DB lock ではなく、各 task の実 materialize / network / memory apply を個別に削る。

新規・更新ファイル由来の `chart_info` と maintenance を background へ押し出さない。

`maintenance` は通常、譜面がライブラリへ導入された時点、または明示的な再スキャンで計算された snapshot であり、通常起動のたびに全譜面の WAV/BGA/MOV health を再検証するものではない。catalog load 直後に `BMSFile.maintenanceInfo` の lazy default が作られても、それは `MaintenanceInfoOrigin.Placeholder` であり、DB 由来または正しく計算済みの health と同じ意味を持たない。resource health warning、WAV/BGA/MOV 率、ignored state の正本は、DB から hydrate された `DbHydrated`、file diff / 導入処理 / 手動再スキャンで計算された `Calculated` に限定する。DB 由来 snapshot は一部カテゴリだけが入った既存 row でも warning 投影に使えるが、bmson parse 直後の encoding-only placeholder は正本として扱わない。

導入後に不足 resource を後から追加した場合や、隣接 resource を削除した場合の再評価は、通常起動の background hydration ではなく再スキャン操作で扱う。行右クリックの `ファイルスキャン > 再スキャン` は選択行だけ、`ファイルスキャン > 全譜面を再スキャン` は owned BMS 全件 + installed bmson 全件を重い明示操作として再計算し、専用 status bar progress を表示する。

2026-05-05 17:40 の保留 package なし通常起動では、`maintenance_hydration done` は `rows=210027`, `readMs=3298`, `applyMs=1085`, `attachMs=280`, `indexBuildMs=396`, `validSnapshotCount=210027`, `placeholderCount=3`, `elapsedMs=4568` だった。通常起動では persisted snapshot attach と resource health index rebuild だけを行い、全譜面再計算は行っていない。

## 実装上の禁止事項

- migration 完了印を `chart_info` backfill の副作用として書かない。
- `EnsureBmsonSchema()` を「migration 完了」とみなさない。
- test からしか呼ばれない旧 migration API を残さない。
- current `chart_info` skip row を file diff result / index delta として大量 publish しない。
- file diff 由来の `WAVfiles` / `BGAfiles` を long-lived model に残さない。
- 起動 critical path の判断を、後続 background task の偶然の完了順に依存させない。
- startup hydration worker 内で schema ensure、migration、repair、compatibility fallback、hidden write を行わない。
- read-only DB loader と write-capable transaction path を同じ helper / 同じ phase に混ぜない。
- DB connection を保持したまま、大量の memory owner attach や UI notification を行わない。

## 関連資料

- `devdocs/current-startup-reload-progress.md`
- `devdocs/current-chart-file-read-pipeline.md`
- `devdocs/empty-db-first-startup-optimization-plan.md`
