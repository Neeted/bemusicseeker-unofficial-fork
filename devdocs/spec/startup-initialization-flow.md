# 起動・初期化フロー 現行仕様

この資料は、BeMusicSeeker の起動、再初期化、リロードで現在正本として扱う初期化フローをまとめる。実装履歴ではなく、現行動作と守るべき境界だけを書く。

## 目的

初期化は次の 2 つを分けて扱う。

- 導入可能 readiness: 譜面の導入先推定と導入開始に必要な情報が揃った状態。
- 初期化全体の完了: UI 操作可能後に走る startup background task まで含めて収束した状態。

導入可能を早くするために必要な background work を単に後回しへ隠すのではなく、`startup_install_estimation_ready` と `startup_initialization_complete` の両方を観測する。

2026-05-09 時点では、通常起動と空 DB 初回構築を分けて読む。

- 通常起動 / 差分なしに近い起動では、導入可能 readiness は概ね 20 秒前後、UI 操作可能は 21 秒前後まで短縮済みである。この場合の background tail は `chart_info_hydration` が中心で、`startup_initialization_complete` は 39 秒前後まで短縮済みである。
- 空 DB 初回構築では、`song` / `bmson_song` / `maintenance` / inline `chart_info` を新規構築するため、全 chart file bytes の read が支配的になる。`chart_digest_map` はこの file read の副産物として必要範囲が追加・更新される partial cache であり、startup migration が全量補完するものではない。直近ログでは `parse_read_bytes_estimate=14067633086`、`startup_install_estimation_ready elapsedMs=401434`、`startup_ready_operable elapsedMs=463762`、`startup_initialization_complete elapsedMs=667710` である。同じ 14GB 級の単純 read benchmark が `453.572 sec` であるため、この環境では初回 song/maintenance table 構築の高速化は一旦完了扱いとし、以後は通常起動や background tail と分けて評価する。

## Startup

```text
Startup
  -> LR2 mode の場合だけ bmson migration preflight
  -> 必要なら警告と startup migration
  -> LR2 mode の場合だけ final preflight
  -> metadata bundle import
  -> catalog DB load
  -> active score source の DB load
  -> file enumeration / native canonical resource index build
  -> file diff apply
  -> pending package restore
  -> install readiness
  -> UI refresh / operable
  -> startup background scheduler
```

`Startup` の migration と metadata import は DB load より前に完了させる。`chart_info` hydration/backfill は bmson schema migration の代替ではない。

## Library Profile

起動時はまず library profile を決め、以後の DB path / root / LR2 固有機能はその profile に従う。

| Profile | `song.db` | Search roots | LR2 config / score | LR2 固有機能 |
| --- | --- | --- | --- | --- |
| LR2 linked | `LR2files\Database\song.db` | `config.xml` の BMS search directories | `config.xml` と LR2 player score DB を使う | custom folder 出力、LR2 backup、LR2IR/ranking を許可 |
| Standalone | アプリ配下 `data\song.db` | 設定画面の複数 BMS ディレクトリ | 使わない。beatoraja score.db は設定 ON の場合だけ使う | LR2 custom folder 実出力、LR2 backup、LR2IR/ranking を無効化 |

Standalone profile の `data\song.db` は portable app data であり、`%LOCALAPPDATA%` には保存しない。初期化順は `data` directory 作成、`song.db` 作成、library schema、playlist schema、bmson/chart_info schema の順に揃える。schema は LR2 `song.db` 互換を維持し、playlist / custom folder 出力用の設定値保存は DB 内に残せる。ただし standalone profile では `.lr2folder` 実出力と `config.xml` 書き換えを行わない。

設定画面の `スタンドアローン(LR2と連携しない)` は standalone profile を選ぶ。BMS ディレクトリは複数登録でき、保存時は存在する path だけを正規化する。重複は大小文字無視で排除するが、親子関係や用途の違う root はユーザーが追加した単位を保持する。旧 `BMSRootPath` は初回移行元として扱い、standalone root list が空で存在する場合だけ取り込む。standalone profile でも BMS インストール先は必須で、登録済み BMS root のいずれかを選ぶ。

LR2 linked / standalone の profile 切替は、同一プロセス内の `FullReinitialize` や hot reload では反映しない。起動済み profile が存在する通常運用時は、設定ダイアログで mode のトグルを切り替えた時点で再起動確認を出し、承認された場合だけ永続化済み設定を reload した上で動作モードのみ保存してアプリを再起動する。他の未保存設定は保存しない。キャンセル時は保存済み mode へ表示を戻し、実行中の library profile と startup progress は変更しない。切替後の設定不足は、再起動後の `Startup` validation と既存の設定ダイアログ表示で案内する。

初回起動や validation 失敗で有効な実行中 profile がまだ一度も成立していない場合は、mode トグルは再起動境界にしない。この状態ではトグルは設定ダイアログ内の draft 選択だけを変え、OK 時に通常 validation と保存を行った後、同一プロセスで `Initialize()` を開始する。初期設定保存では runtime post-save action を走らせず、search root、player、playlist などの実行時反映は直後の `Initialize()` に任せる。ただし LR2 linked の `config.xml` など永続化対象の設定ファイルは保存する。validation に失敗した場合は既存の `Msg_invalid_setting` ダイアログで不足項目を表示し、設定ダイアログに留まる。

初回起動時に validation が未成立の場合は、OS 標準 MessageBox ではなくアプリ内 overlay の `InitialSetupLanguageDialog` を先に表示する。この dialog は言語選択と初回設定案内だけを担当し、`settingDialog.Languages` / `settingDialog.Language` をそのまま使って選択言語を即時適用する。`設定へ進む` を押すと既存の設定ダイアログへ遷移し、動作モード、BMS ディレクトリ、LR2 ディレクトリ、インストール先などの必須項目は設定ダイアログで入力する。通常起動時の設定不備は従来どおり `Msg_init_settings_check` の themed MessageBox で案内する。

設定値の getter は validation のために永続設定を消してはならない。特に `LR2CustomFolderOutputDir`、`LR2CustomFolderAsRootOutputDir`、`BMSInstallDir` は、mode 切替直後や設定ダイアログ表示中に一時的に現在 profile と合わないことがあるため、表示時は保存値を返し、無効理由は `CheckValidation()` のエラーとして扱う。

再生タブの LR2body 選択は library profile とは別のプレイヤー設定である。Standalone profile でも LR2body を BMS 再生用アプリとして選べるため、LR2 linked / standalone の切替で無効化しない。LR2body 再生では LR2 実行ファイルと player config を検証するが、ライブラリ用の LR2 `song.db` 連携とは扱いを分ける。

## Operation Modes

| Mode | 入口 | 目的 | 主な処理 |
| --- | --- | --- | --- |
| `Startup` | アプリ起動 | DB と file system から正本 catalog / resource index / UI を構築する | migration、metadata import、catalog load、file diff、background hydration |
| `FullReinitialize` | ライブラリ右クリック `初期化再実行` | 外部 DB 編集や状態修復を想定して library 初期化を再実行する | catalog load、file enumeration、file diff、必要な background 補完 |
| `ReloadFileDiff` | ライブラリ右クリック `リロード` | DB は読み直さず、所持ファイルの追加・削除・更新だけを memory / DB に反映する | file enumeration、file diff、playlist reference apply |
| `ReloadTables` | playlist/table reload | playlist/table 系だけを再読込し、外部 playlist 同期を再スケジュールする | table header reload、playlist entries hydration、external playlist sync |
| `ScoreOnly` | score DB 設定変更 | score source / score.db の切り替えだけを反映する | score DB load、score snapshot rebuild、score hydration、必要なら LR2 ranking refresh |

起動中の `song.db` は原則 BeMusicSeeker が更新するため、`ReloadFileDiff` は in-memory catalog と file scan result の差分を正本にする。LR2 や手動編集で DB が変わった可能性まで拾う場合は `FullReinitialize` を使う。

mode 切替は上記 operation ではなく process restart として扱う。これは active `song.db`、LR2 config provider、score source、background scheduler、UI cache の境界が変わるためで、current process で `Initialize()` を再実行して profile を差し替えない。

BMS search root の追加・削除は mode 切替ではないため、保存後に runtime の `BMSLibrary.SearchTargets` を現在 profile の root set へ同期してから `ReloadFileDiff` を実行する。これにより、standalone の BMS ディレクトリ追加や LR2 linked の search directory 変更は再起動待ちにならない。

起動時 UI ではプレイリスト root を常に展開する。これは root item の展開だけで、配下 playlist の再帰展開ではない。

## bmson Migration

`BmsonMigrationPreflightService.Inspect()` は read-only 判定だけを行う。

| 判定 | 意味 | 起動時の扱い |
| --- | --- | --- |
| `NeedsPlaylistEntrySha256Migration` | 既存 `playlist_entry` schema が現行ではない | 警告対象。OK 後に `BMSPlaylist.EnsureSchema()` で移行 |
| `NeedsBmsonAppSchemaMigration` | `app_schema_version(name='bmson_app_schema')` が現行ではない | 必ずしも警告対象ではない。既存 bmson app-owned table や旧 version row がある場合だけ警告対象 |
| `RepairRequired` | `chart_digest_map` / `bmson_song` / index の schema が欠損または互換外 | 警告なしで startup preparation / repair により収束させる。`chart_digest_map` の row coverage は判定しない |

startup migration 後は必ず final preflight を行い、上記の未収束が残る場合は起動失敗として扱う。

`playlist` / `playlist_entry` が存在しない LR2 `song.db` へ app 用 playlist schema を追加するだけの場合や、`chart_digest_map` / `bmson_song` / `app_schema_version` を初回連携用に追加するだけの場合は、互換性に影響する migration warning を出さない。既存 `playlist_entry` に `sha256` を足す、既存 `playlist_entry_idx_uniq` を作り直す、既存 `bmson_app_schema` version を更新する、または version row が無い状態で既存 `chart_digest_map` / `bmson_song` を持つ場合は、既存 app-owned schema/data へ手を入れる migration として警告対象にする。

`BmsLibraryDbGateway.EnsureBmsonSchema()` は schema/index presence の修復 helper であり、migration 完了印を書かない。初回連携用に bmson app-owned tables を作って current version を記録するだけの場合は `EnsureBmsonStartupSchema()` を使う。既存 app-owned data の schema 正規化と current version 記録が必要な場合は `CompleteBmsonStartupMigration()` を使う。

`CompleteBmsonStartupMigration()` は既存 `chart_digest_map` の `md5` / `sha256` row を保持しながら current schema へ正規化するが、`song` table 全件を走査して実ファイルから SHA-256 を生成しない。missing digest は file diff / install / inline `chart_info` / chart info backfill など、譜面 bytes を読む後続 pipeline の責務とする。

初回設定後の `Msg_init_completed` は `files_initialize_done` 直後ではなく、startup scheduler が idle になり `startup_initialization_complete` を記録した後に表示する。これにより、初回完了メッセージは critical path だけでなく通常の起動時 background 初期化まで終えた境界を表す。

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
| Score DB load | active score source を読み、LR2 source の場合だけ `LR2ID` 確定後に `ir_score_prefetch` を開始する | standalone + beatoraja 無効なら score source は `None` |
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

導入可能 readiness の支配項は起動状態で異なる。通常起動 / 差分なしに近い起動では Everything scan / native bridge が支配項である。`song_tbl_load` 由来の catalog load は 3 秒台まで短縮済みだが、file enumeration と並走しており、現状の導入可能 wall clock では Everything scan に隠れる。`song_tbl_load` の micro optimization は、通常起動の導入可能短縮の主対象にはしない。

空 DB 初回構築では、Everything scan そのものよりも file diff apply 内の全譜面 read、lightweight parse、inline maintenance、encoding detection、DB commit が支配的になる。この経路では追加/更新 chart の `ChartFileSnapshot` を起点に `song` 登録、inline `chart_info`、inline `maintenance`、encoding 補正をまとめて処理する。非 Shift_JIS が確定した BMS の metadata reload は snapshot bytes から raw `title` / `subtitle` / `artist` / `subartist` / `genre` を再適用し、旧 setter 合成に戻さない。

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

`Startup` では scheduler は `startup_ready_operable` 到達まで開始しない。`ScoreOnly` / `ReloadTables` / `ReloadFileDiff` / `FullReinitialize` は既に UI operable 後の operation なので、operation 開始時の reset 後も scheduler を runnable に保つ。これは reload 中に `playlist_entries_hydration`、`external_playlist_sync`、`score_hydration_deferred`、`ranking_refresh_deferred` など operation ごとの background task を queue したまま止めないための仕様である。`ReloadTables` 自体は score/ranking を queue しない。

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

`playlist_url_completion` は、MD5-URL mapping TSV と Stella Uploader Full (`score_upload_full.json`) を process-local snapshot として保持する。同じ起動中の playlist reload / external sync / reset では再 download せず、保持済み snapshot を再適用する。設定画面で TSV URI または Stella Uploader Full 補完設定が変わった場合だけ、次回 schedule で必要な source を再取得してよい。候補適用時は TSV を優先し、TSV に同じ MD5 がない場合だけ Stella Full の `url` / `url_diff` を URL1/URL2 補完に使う。

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

ranking cache / `ir_data` は、LR2IR の local cache XML を hash 単位で読み、対象 `LR2ID` 用の ranking summary を `ir_data` に保持する。

- XML reload 対象判定は `ir_data.lastcacheupdate` と XML 末尾 `lastupdate` を使う。
- reload は明示 degree の bounded worker pipeline で行う。既定 degree は `max(1, Environment.ProcessorCount - 1)`。
- ranking cache XML parser は startup refresh、manual download、`LR2IRCache` wrapper で共通化する。refresh 用には full ranking list を materialize せず、`<score>` block を 1 pass で読み、`players_num`、`average`、sample stddev、対象 player score、rank を集計する。
- `id`、`clear`、`notes`、`combo`、`pg`、`gr`、`minbp` は 0 以上の整数だけを valid score row として扱う。負数や parse 不能値を含む row は集計対象から外す。
- rank は `count(score > targetScore) + 1` として算出する。対象 `LR2ID` がない場合は従来同様 `NO_PLAY` / `rank=-1` の synthetic row を作る。
- `lastupdate` は XML 末尾の date parse を優先し、空 / 不正 / NUL tail の場合は cache file last write time に fallback する。
- parser が失敗した XML は skip する。旧 full parser fallback は使わず、`xmlFallbackLoads` は互換 metric として残る。
- `SkipEstimateOfflineScoreRanking=false` で local score が IR row より高い場合だけ、offline ranking estimation 用に compact rank calculator を on-demand load する。startup refresh で reload 済みの hash は同じ lookup を使うため、同じ refresh 内では再読込しない。

初回構築では `ir_data` が対象 `LR2ID` で空の場合、dedupe 済み rows を 1 transaction の bulk insert で書き込む。既存 row がある場合や guard に失敗した場合は通常の hash 単位 upsert に fallback する。`ir_data` table schema は互換維持のため unique 制約を追加しないが、lookup / delete guard 用に非 unique 複合 index `ir_data_idx_lr2id_hash(lr2id, hash)` を持つ。

`ranking_cache_refresh done` は既存 `xmlReloadMs` / `upsertMs` に加え、`xmlReloadDegree`、`xmlScoresParsed`、`xmlParseFailed`、`xmlFallbackLoads`、`bulkInsertUsed`、`offlineEstimateXmlLoads` を出す。2026-05-09 の実測では、初回 `ir_data` 書き込みは `upsertMs` 約 50s から bulk insert 約 0.3s まで短縮し、全体は主に XML read / summary parse に寄った。

## ReloadFileDiff

```text
ReloadFileDiff
  -> file enumeration / resource index build
  -> in-memory catalog との差分検出
  -> added/updated charts の snapshot read
  -> parser workers による lightweight parse
  -> post-parse worker による inline chart_info / inline maintenance
  -> single DB writer による chunk commit
  -> deleted charts の unregister
  -> memory catalog / resource index swap
  -> playlist reference apply
```

差分が 0 件の場合は DB commit、chart_info hydration/backfill、installable maintenance を発生させない。

差分がある場合、reader は 1 本、parser は `max(1, Environment.ProcessorCount - 1)` を既定とする。parser output queue は inline `chart_info` batch size 以上を確保し、既定 batch size は 2048 件、DB commit chunk size は 10000 件とする。inline maintenance は post-parse worker 内で bounded parallelism により実行し、`SongTableFileCheckResult` と DB chunk への反映は集約後に行う。

手動 `ReloadFileDiff` は、prefetch の有無、reason/progress/UI 更新、後段 playlist reference scheduling を除き、`Startup` の file diff と同じ `ApplyFileScanDiff()` 経路を使う。軽量 parse、inline `chart_info`、inline `maintenance`、snapshot 由来 encoding reload の意味論は起動時 file diff と揃える。

## ReloadTables

```text
ReloadTables
  -> table header reload
  -> playlist_entries_hydration を startup scheduler へ queue
  -> external_playlist_sync を playlist_entries_hydration dependency 付きで queue
```

`ReloadTables` は post-startup operation なので、scheduler reset 後も scheduler は runnable である。score DB load、score snapshot rebuild、score hydration、ranking refresh は行わない。`playlist_entries_hydration` は `UpdateBMSTables` callback まで終えてから completed version を publish し、その後 `external_playlist_sync` が進む。playlist reference replacement は reload 中の direct callback で適用され、必要に応じて `PlaylistReferenceApplied` phase として追跡される。

playlist reload の実行部分は bounded parallel の共通 batch を使う。`ReloadTables` は DB から table header を再読込した後、`is_external_sync` が有効で absolute URI を持つ playlist だけを batch 対象にする。プレイリスト単体リロードやサマリー選択範囲リロードは、選択された playlist を `is_external_sync` に関係なく batch 対象にする。手動範囲リロードの失敗は個別 dialog ではなく、ログと playlist summary の `STATUS` に集約する。

## ScoreOnly

```text
ScoreOnly
  -> score source を再選択
  -> score DB load
  -> score snapshot rebuild
  -> 現在の BMSFiles へ score を置換適用
  -> score_hydration_deferred を必要に応じて queue
  -> LR2 source の場合だけ ranking_refresh_deferred を必要に応じて queue
```

`ScoreOnly` は score DB 設定変更専用の post-startup operation である。playlist header reload、playlist entries hydration、playlist reference apply、external playlist sync は行わない。beatoraja score source が有効な場合は LR2 score DB rows を読み込まず、LR2IR / ranking refresh も行わない。LR2 source へ切り替えた場合だけ、LR2 score / ranking 系の後続更新を score 情報の一部として扱う。

## 守るべき境界

- migration 完了印を `chart_info` backfill の副作用として書かない。
- startup hydration worker 内で schema ensure、migration、repair、hidden write を行わない。
- read-only loader と write-capable transaction path を同じ phase に混ぜない。
- DB connection を保持したまま、大量の memory owner attach や UI notification を行わない。
- 導入可能 readiness を、playlist hydration、score/ranking refresh、chart_info hydration、maintenance hydration の完了に依存させない。
- score DB 設定変更では `ScoreOnly` を使い、playlist/table reload や external playlist sync を起動しない。
- startup background task を expected phase から外して初期化完了を短く見せない。必要な task は `startup_background_summary` に残す。

## 関連資料

- `devdocs/spec/startup-reload-progress.md`
- `devdocs/spec/chart-file-read-pipeline.md`
- `devdocs/plan/empty-db-first-startup-optimization-plan.md`
- `devdocs/plan/bmson/library-scan-fast-path-resource-index-plan.md`
