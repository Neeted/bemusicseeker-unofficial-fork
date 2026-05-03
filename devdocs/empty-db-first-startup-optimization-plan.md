# 初回空DB起動の軽量化とメンテナンス表示整合計画

## 概要

空に近い `song.db` で初回起動すると、ファイルスキャンで大量の譜面が一括追加される。このとき、現在は次の問題が同時に出やすい。

- `LR2データベース未登録` が初回だけ大量表示され、再起動後は 0 件になる。
- `ゼロノート検索` が初回だけ大量表示され、再起動後は少数に落ち着く。
- `installable_maintenance_deferred` が進捗ゲージに含まれず、操作可能表示後も長時間重い background 処理が続く。
- 初回 health 計算で `maintenance` 未作成の全譜面を対象にするため、resource health / bmson 再パース / warning 適用が重い。

この資料では、初回空DBを主対象として、表示の一貫性と軽量化の修正方針を整理する。

## 観測ログ

実測では、metadata bundle import 済みでも次の処理が長時間化した。

```text
song_tbl_file_check_breakdown ... added_count=208936 bmson_upsert_count=1048 bms_parse_ms=1335198 ...
startup_ready_installable ... elapsedMs=1430571
maintenance_update checked=209984 bmsResourceTargets=208936 bmsonResourceTargets=1048 maintenanceUpserted=209984 bmsonReparsed=1048 elapsedMs=520358
installable_maintenance_deferred done ... set_health_ms=525181 set_zero_note_ms=96768 deferred_ms=625289
```

Phase 1-4 の譜面ファイル読み込み重複整理により、新規・更新譜面の lightweight parse と `chart_info` parse は原則 single-read 化済みである。その後の Phase 1-5 で、LR2 互換情報の正規化、zero-note の責務整理、maintenance の進捗表示、bmson 再パース削減、resource health warning projection 分離までは実装済みである。

Phase 5 までの実装後に、ほぼ空DBで再計測した結果では、機能面の目標はおおむね達成できている。

```text
chart_info_metadata_import ... elapsedMs=57426 chartInfoImported=209875 digestImported=209896
everything_scan ... charts=209989 totalMs=27728 bridgeRawBufferBytes=331780516
song_tbl_file_check_breakdown ... added_count=208938 bms_parse_ms=1332756 inline_chart_info_current_skipped_count=209962 db_commit_ms=17761
startup_ready_operable ... elapsedMs=1526709
chart_info_backfill start ... targets=0 currentRowSkipped=209965 failureSkipped=21 targetBuildMs=95070
maintenance_update checked=209986 maintenanceUpserted=209986 bmsonReparsed=0 bmsonResourceRefsReused=1048 resourceHealthIndexMs=417 elapsedMs=354688
installable_maintenance_deferred done ... set_health_ms=356968 deferred_ms=359993
main_view_build mode=FileMissingFilterSelected ... folderMs=242 totalMs=293 folderCount=29531
main_view_build mode=FileMissingIgnoredFilterSelected ... folderMs=0 totalMs=3 folderCount=0
```

この時点で `LR2非対応パス` / `ゼロノート検索` / `解析エラー` / `構成ファイルフルスキャン` の件数は初回と再起動後で大きく揺れにくくなった。一方で、性能面では次が残っている。

- 初回 file diff の BMS lightweight parse が約22分で、操作可能化までの支配要因になっている。
- file diff は 512 件ごとに parse しているが、DB 永続化は最後に全件まとめて行うため、`AddedFiles` / `NextFiles` / inline `chart_info` row などの staging が長時間・大量に残る。
- file diff の進捗は batch 完了後にまとめて進むため、表示粒度が 512 件単位に見える。
- full `chart_info` backfill は実 target 0 件でも全 owner を走査しており、約95秒の固定費が出ている。
- 初回 `installable_maintenance_deferred` の health 計算は約6分で、resource health index 化後も実 health 判定そのものが重い。
- Everything scan の raw buffer と resource hash index は数百MB級になり、file diff の全件 staging と重なるとメモリピークが高くなりやすい。
- Phase 6/7.5 後も、初期化中に Private Bytes が数GB級まで上がり、`startup_background_task done` 後に一気に下がるケースがある。
  - 完了後に下がるため恒久的な leak というより、background task の scope が終わるまで一時 collection / parser result / maintenance working set が root され続ける構造が疑わしい。
  - 初期化中は多少の停止を許容できるため、メモリ逼迫で処理全体が遅くなるより、phase 境界で参照を切り、必要なら明示 GC / LOH compact を行う方針へ寄せる。

## 目標

- 初回起動と直後の再起動で、メンテナンス画面の件数が大きく変わらないようにする。
- BeMusicSeeker が LR2 `song.karinotes` を補完しないようにし、zero-note 表示は `chart_info.notes` へ寄せる。
- Shift_JIS 非対応パスを削除せず、LR2 parent を設定できない譜面として可視化する。
- `installable_maintenance_deferred` を進捗に含め、重い background 更新を見える化する。
- bmson 再パースや 2 回目以降の全件 warning 再適用を減らす。

## 初期化フローの正本

今後の設計では、処理を「新規・更新ファイル由来」と「DB 由来の補助情報」に分けて考える。

### 基本原則

- 差分ファイルがない場合は、導入先推定に必要な情報を最速で使える状態にする。
  - DB load、ファイル列挙、resource hash/index 構築を優先する。
  - 譜面ファイル本文の read は行わない。
  - `chart_info` hydration/backfill、maintenance 補完、score/ranking など DB 由来の補助情報は background に回してよい。
- 差分ファイル、つまり新規・更新ファイルがある場合は、ファイル read 直後の近い場所で必要な反映を終わらせる。
  - lightweight parse。
  - LR2 `folder` / `parent` 正規化。
  - `chart_digest_map` 更新。
  - inline `chart_info` 解析または current row / parse failure 判定。
  - 新規ファイル由来の maintenance row / resource health 判定。
  - DB chunk commit と in-memory catalog 反映。
- background 処理へ回してよいのは、既に DB に存在している owner に対する補助情報の補完である。
  - 旧バージョン DB の `chart_info` 補完。
  - DB load 済み row の maintenance 補完。
  - score / ranking / playlist hydration。
- 補助情報であっても、新規・更新ファイル由来なら後回しにしない。
  - そのファイルの snapshot bytes、軽量 parse 結果、resource refs、scan cache が生きている間に処理する。
  - 「追加直後は未反映で、再起動または background 補完後に初めて正しくなる」状態を作らない。
- metadata bundle import は、本アプリのリリースパッケージ同梱物または外部配布物から取り込むメタデータであり、「所持譜面から生成された DB 由来補助情報」ではない。
  - 頻繁に import するためのデータではなく、リリース直後またはユーザーが bundle を配置した起動時に一度だけ取り込む。
  - import タイミングは起動直後に固定する。
  - 差分有無や target count によって background / foreground を切り替えない。
  - import 済み bundle は `imported_metadata/` 退避と import history で再処理を避ける。

### 差分なし fast path

差分なしの起動・ReloadFiles は次を最短経路にする。

```text
DB load
  -> file enumeration / resource index build
  -> diff 0 判定
  -> in-memory catalog は DB 由来のまま維持
  -> 導入先推定に必要な index を公開
  -> 操作可能
  -> background:
       chart_info hydration/backfill if DB-derived candidates exist
       maintenance補完 if DB-derived missing/stale rows exist
       score/ranking/playlist hydration
```

metadata bundle import はこの fast path の前段で一度だけ試行する。bundle が存在しない、または import history 済みで退避済みの場合は、通常の差分なし fast path に影響しない。

差分なし fast path では、`song` / `bmson_song` を作り直すための譜面 read、current `chart_info` row の大量再 publish、resource health warning の全件 mutation を行わない。

### 差分あり path

新規・更新ファイルがある場合は、1 件の譜面を次の単位で処理する。

```text
changed path
  -> ChartFileSnapshot read
  -> lightweight parse
  -> LR2 parent/folder normalize
  -> inline chart_info
       current row exists: modelへ適用するが file_diff result として大量保持しない
       current parse failure exists: parse skip
       missing/stale: parseして新規 row / failure row を作る
  -> maintenance row / resource health
       新規ファイル由来の refs と scan cache を利用
  -> commit chunk
       song / bmson_song
       chart_digest_map
       generated chart_info / parse failure
       generated maintenance
  -> memory catalog apply
```

ここで重要なのは、`current chart_info` で parse skip した row を file diff の成果物として全件蓄積しないことである。current row は DB 由来の既存補助情報であり、session index の正本更新は hydration が担当する。file diff inline が runtime index に公開するのは、原則としてその場で新規生成または更新した `chart_info` row だけにする。

### background の責務

background task は「現在の file diff で直接扱っていない DB 由来の補助情報」を補完する。

| 処理 | background 可否 | 理由 |
| --- | --- | --- |
| metadata bundle import | 不可 | リリース同梱または外部配布 metadata を起動直後に一度取り込む処理で、差分ファイル由来/DB由来補完とは別枠 |
| 旧DBの full `chart_info` backfill | 可 | 譜面は既に DB catalog に存在し、新規 file read の近傍ではない |
| current `chart_info` hydration | 可 | DB row を memory index / owner へ適用する処理 |
| 新規・更新ファイルの `chart_info` 生成 | 不可 | snapshot bytes がある間に処理すべき |
| 新規・更新ファイルの maintenance row 作成 | 原則不可 | lightweight parse refs と scan cache がある間に処理すべき |
| DB 由来の missing/stale maintenance 補完 | 可 | file diff 由来ではない補助情報の補完 |
| score / ranking / playlist hydration | 可 | song catalog 反映後に遅延適用可能 |

### メモリ方針

- snapshot bytes は bounded queue と parser worker の寿命を超えて保持しない。
- commit chunk は「メモリ制御」ではなく「transaction 範囲と rollback 範囲」の制御として扱う。
  - 1000 件固定は安全側だが、`song.db` は 21 万件規模でも 800MB 程度であり、chunk staging が bounded に破棄されるなら 5000-10000 件程度へ粗くしてもよい。
  - 目安は `db_commit_max_chunk_ms` と memory checkpoint で判断する。commit 時間が安定しており、rollback 範囲を許容できるなら 10000 件を候補にする。
  - commit chunk を粗くしても、snapshot bytes や current skip row を chunk 外へ持ち越さないことを前提にする。
- current skip した `chart_info` row は大量に `SongTableFileCheckResult` や callback collection へ載せない。
- final catalog 用 list は必要最小限にし、`AddedFiles` / `NextFiles` の二重保持がピークになる場合は、次段で catalog builder 方式へ移行する。
- resource index は導入先推定と health 判定の共通基盤として使い、同じ情報を別構造で重複構築しない。
- 大量初期化中は managed heap / LOH が膨らみやすい。常時動作の低停止アプリではないため、初期化 phase 境界では明示 GC を許容する。
  - inner loop や各譜面ごとの GC は行わない。
  - `file diff commit/catalog switch`、`chart_info hydration/backfill done`、`installable_maintenance_deferred done` など、大きな一時 collection の寿命が終わった直後だけを候補にする。
  - phase 終了時は、まず large local collection / staging / diagnostic row list を clear/null 化し、その後に必要なら blocking full GC + LOH compact を行う。
  - DB transaction、library write lock、UI dispatcher 同期処理の内側では GC しない。
- メモリ調整は必ず log で観測する。
  - `PrivateBytes`
  - `WorkingSet`
  - `GC.GetTotalMemory(false)`
  - Gen0/1/2 collection count
  - phase before / after / after explicit GC
  - chunk high-watermark

### 責務境界

| Component | 責務 |
| --- | --- |
| `BMSLibrary` | 初期化 orchestration、locks、progress、background task 依存関係、in-memory catalog 切替 |
| `BmsLibraryInitializationService` | scan / diff 判定、新規・更新ファイル由来の chunk assembly、file-derived progress |
| `ChartInfoInlineBuildService` | snapshot から `chart_info` row / parse failure row を作る。current row skip 判定は行うが、大量保持はしない |
| `BmsLibraryMaintenanceService` | maintenance row / resource health issue の生成。file-derived と DB-derived の呼び出し元を分ける |
| `BmsLibraryDbGateway` | schema、transaction、chunk upsert、candidate summary。workflow 判断は持たない |
| `MainWindowViewModel` | progress phase の表示、startup background scheduler の dependency 実行 |

`chart_info_hydration -> full backfill -> installable maintenance` の順序保証は、アプリ起動時の startup background scheduler 配下で成立する。`BMSLibrary` 単体の fallback `Task.Run` 経路では dependency scheduler がないため、将来的には fallback 側でも同じ順序を保つか、アプリ起動時のみの保証として明示する。

### 進捗方針

- `LibraryFileDiffDone` は、新規・更新ファイル由来の DB / memory 反映が完了するまで完了にしない。
- `ChartInfoBackfillDone` は DB 由来の full backfill のみを表す。
- 新規・更新ファイルの inline `chart_info` は `LibraryFileDiffDone` の内側に含める。
- 新規・更新ファイルの inline maintenance も、実装後は `LibraryFileDiffDone` または専用 sublabel の内側で扱う。
- `InstallableMaintenanceDeferredDone` は DB 由来の missing/stale maintenance 補完を表す。新規ファイル由来の大量 maintenance 作成をここへ押し出さない。

## LR2データベース未登録

### 現状

`LR2データベース未登録` は、実際には `song` テーブル未登録ではなく、`BMSFilesUnregistered => parent が空` の譜面を表示している。

初回空DBでは、file diff で追加された `BMSFile` が `folder` / `parent` 未設定のまま `song` へ insert される。そのため初回だけ大量に表示される。再起動後は `LoadSongTable()` の正規化で `folder` / `parent` CRC が補正されるため、0 件になる。

また、`docs/sjis-path-validation-note.md` の通り、`folder` / `parent` CRC は Shift_JIS で計算する。パスに Shift_JIS 非対応文字が含まれると現在は例外経由で削除対象になり、直後の file diff で再追加される往復更新が起きる。

### 修正方針

- file diff で新規 BMS を作った直後、DB insert 前に `LoadSongTable()` と同じ `folder` / `parent` CRC を設定する。
- CRC 計算 helper を共通化し、`LoadSongTable()` と file diff 追加時で同じ判定にする。
- Shift_JIS 非対応パスでは削除しない。
  - `folder` / `parent` は空のままにする。
  - structured warning を付ける。
  - `LR2データベース未登録` は「パスに Shift_JIS 非対応文字が含まれるため LR2 が解釈可能な parent を設定できない譜面」を示す画面へ意味を寄せる。
- 既存 `song` の正規化時も、Shift_JIS 非対応による削除ではなく warning 付与にする。

### Warning 案

| Kind | Category | DigestLabel | HighlightRow | Message |
| --- | --- | --- | --- | --- |
| `Lr2PathEncodingUnsupported` | `Lr2Compatibility` | `LR2パス非対応` | true | `譜面パスに Shift_JIS で表現できない文字が含まれているため、LR2 の folder/parent ID を計算できません。` |

warning は DB 永続化しない。起動時・file diff 追加時の判定で再付与する。

### 期待効果

- 初回空DBで `LR2データベース未登録` が全件表示される問題を解消できる。
- Shift_JIS 非対応パスの削除→再追加往復を止められる。
- 未登録画面の意味が、実際にユーザー対応が必要な LR2 互換問題へ絞られる。

## ゼロノート

### 旧仕様

`BMSFilesZeroNote` は `file.notes == 0` を見る。`file.notes` は LR2 `song.karinotes` の wrapper であり、`chart_info.notes` ではない。

BeMusicSeeker が `karinotes = 0` を入れる経路は、`setZeroNoteAndCommitToDB()` -> `UpdateZeroNoteAndCommit()` -> `BMSFile.SetNotesIfZeroNote()` である。これは正規表現ベースで可視ノート行が存在しない場合だけ `karinotes` に 0 を入れる。非 0 の note count は入れない。

この挙動はゼロノート検索画面のための補助だったと考えられるが、現在は `chart_info` parser があり、より正確な note count を持てる。

### 現行仕様

- BeMusicSeeker から LR2 `song.karinotes` を補完する処理は廃止済み。
  - 起動時、遅延メンテナンス、package install 後の zero-note commit は行わない。
  - `UpdateZeroNoteAndCommit()` / `SetNotesIfZeroNote()` は production flow から削除した。
- `ゼロノート検索` は `chart_info.notes == 0` の BMS 譜面を表示する。
  - bmson は対象外。
  - `chart_info` 未生成 / parse failure は表示対象外。
- `karinotes` は LR2 が管理する値として扱い、本アプリでは書き換えない。

### 正規表現ベース機能として残すもの

`BMSFile.IsZeroNoteBMSFile()` は、詳細 parser とは別用途で残す。

- `ゼロノート記述確認`
  - 旧: `karinotes = 0` の妥当性再確認。
  - 新: `chart_info.notes = 0` の譜面について、正規表現上は可視ノートらしき記述が存在するか確認する。
  - RANDOM 分岐、不正な LN ペア、構文不整合などにより parser では 0 notes になるが、譜面本文には可視ノート風の記述があるケースを `ZeroNoteMismatch` warning として検出する。
  - UI 文言は `ゼロノート記述確認` とする。
- 保留画面の「ゼロノート譜面を無効な拡張子に変更」
  - 詳細 parse をせず、本文に確実に可視ノートらしき記述がない譜面だけを安全側に処理する用途として残す。
  - 現行の正規表現は RDM 記法 LN も可視ノート扱いにする。

### 期待効果

- 初回空DBで `karinotes` 補完待ちによりゼロノート画面が大量表示される問題を解消できる。
- `set_zero_note_ms` 相当の全件ファイル読み込みを起動時 maintenance から除外できる。
- LR2 テーブルの `karinotes` をアプリが独自補完しないため、責務が明確になる。

## installable_maintenance_deferred

### 現状

`installable_maintenance_deferred` は操作可能化後に起動される background 更新で、進捗ゲージの `MaintenanceDeferredDone` とは別物である。

主な処理:

1. `setModeAndCommitToDB()`
2. `setMaintenanceInfo(... includeInstalledBmson: true)`

初回空DBでは `maintenance` が空のため、`setMaintenanceInfo` が全譜面を未チェックとして処理する。bmson は file diff で一度 `ParseSnapshot()` されているが、maintenance 側で resource references のため再度 `BmsonSongParser.Parse()` される。

2回目以降は `maintenance` が埋まるため heavy health 再計算対象は減る。ただし `setMaintenanceInfo()` の最後で全 `maintenanceTargets` に `ApplyNeedToBeFixedWarnings()` を行うため、全件 warning 再構築の固定費が残る。

### 修正方針

#### 進捗

- `installable_maintenance_deferred` を startup / reload progress の expected phase に含める。
- phase 名は `InstallableMaintenanceDeferredDone` とする。
- sublabel は `保守情報更新` とする。
- 操作可能後に残る場合は、既存と同じく `操作可能(バックグラウンド更新中)` と表示する。
- requested / completed version を public property として公開し、ViewModel が request-before-complete 方式で tracking する。
- 失敗時も completed version を進め、進捗が詰まらないようにする。

#### bmson 再パース削減

- file diff で `BmsonSongParser.ParseSnapshot()` した結果から、maintenance に必要な resource references を引き渡す。
- 初回追加 bmson は maintenance 側の `TryRefreshBmsonResourceReferences()` 再パースを skip する。
- 既存 DB の bmson で parser version や updated_at が古い場合は従来通り再パースする。

#### 初回 health 計算

- 初回空DBでは全件 maintenance 作成が必要なため、単純な対象削減では大きく軽くならない。
- ただし処理順序を file diff 追加と近づけ、追加譜面の directory/resource cache を使い回す余地がある。
- health 計算の並列度を bounded にし、file diff / chart_info と同じ程度に制御する。
- section 単位 DB upsert の batch サイズと transaction 方針を見直す。

#### 2回目以降の warning 適用

- maintenance 不足がない場合、DB 値から次が確定する。
  - warning なし
  - WAV/BGA/MOVIE/STAGEFILE/BANNER/BACKBMP health は 100% または DB 上の既存 health
- 全件に eager warning 適用しない。
- 表示・フィルタで必要になった時に `maintenanceInfo` から warning を lazy 構築する案を検討する。
- まずは `UpdateMaintenanceInfo()` が実際にチェック/更新した file だけ warning 再適用し、未更新 file は既存 structured warning を保持または必要時 lazy 化する段階実装が安全。

### 期待効果

- 進捗ゲージが消えた後も重い処理が続く状態を解消できる。
- 初回の bmson 二重 parse を削減できる。
- 2回目以降の `set_health_ms` 固定費を減らせる。
- ゼロノート正規表現 scan は Phase 2 で production flow から外れたため、`set_zero_note_ms` は互換ログとして 0 を維持する。

## 実装 Phase 案

### Phase 1: LR2 parent 正規化と Shift_JIS warning

完了済み。

- `folder` / `parent` CRC 計算 helper を共通化した。
- file diff 追加時に `folder` / `parent` を設定してから DB insert する。
- `UpsertSongs()` 前にも同じ補正を行い、package install などの保存経路も保護する。
- Shift_JIS 非対応 path では削除せず、`parent = null` と `Lr2PathEncodingUnsupported` warning にする。
- `LR2データベース未登録` の表示名を `LR2非対応パス` に変更し、画面の意味を Shift_JIS 非対応 path の可視化へ寄せた。

### Phase 2: zero-note source of truth 移行

完了済み。

- `setZeroNoteAndCommitToDB()` を production flow から外した。
- `BMSFilesZeroNote` を `chart_info.notes == 0` ベースへ変更した。
- `RecheckZeroNoteWarnings()` の候補を `chart_info.notes == 0` へ移した。
- 正規表現ベース機能の UI 文言を `ゼロノート記述確認` へ変更した。

### Phase 3: installable maintenance 進捗と観測性

完了済み。

- `InstallableMaintenanceDeferredDone` phase を追加し、Startup / ReloadFiles の expected phase に含めた。
- requested / completed version と running state を public property 化し、ViewModel が tracking できるようにした。
- 操作可能後に残る場合は `操作可能(バックグラウンド更新中)`、sub label は `保守情報更新` と表示する。
- queue / done / failed log に対象件数を追加した。
  - `snapshotCount`
  - `setModeTargets`
  - `maintenanceChecked`
  - `bmsResourceTargets`
  - `bmsonResourceTargets`
  - `maintenanceUpserted`
  - `bmsonReparsed`
  - `bmsonReparseFailed`
  - `songReloaded`
- zero-note legacy scan は行わず、`set_zero_note_ms=0` を互換ログとして維持する。

### Phase 4: bmson maintenance 再パース削減

完了済み。

- `bmson_song` に runtime-only の fresh resource refs state を追加した。
- `BmsonSongParser.Parse()` / `ParseSnapshot()` で構築した `wav_files` / `bga_files` / stage/banner/backbmp/preview を、同一プロセス内の maintenance へ引き継ぐ。
- `forceUpdate == false`、fresh state あり、かつ `updated_at` が現在の `LastWriteTimeUtc` と一致する bmson は、`TryRefreshBmsonResourceReferences()` の再パースを skip する。
- DB からロードした bmson は fresh state を持たないため、安全側で従来通り再パースする。
- `forceUpdate == true` は明示再確認として従来通り再パースする。
- `maintenance_update` と `installable_maintenance_deferred` log に `bmsonResourceRefsReused` を追加した。

### Phase 5: health / warning 適用の軽量化

完了済み。

- `maintenanceInfo` から resource health issue を判定する処理を副作用なし helper として分離した。
- `ResourceHealthIndexSnapshot` を runtime-only で作成し、欠損あり / 無視リストの所属は index から返す。
- `BMSFilesNeedToBeFixed` / `BMSFilesNeedToBeFixedIgnored` の取得では、全件 `ResourceHealth` warning 再構築を行わない。
- `LibraryChartRow` は source `BMSFile` の non-resource warning と、resource health index 由来の projection を合成して WARNING 列を表示する。
- `setMaintenanceInfo()` 後の全件 `ApplyNeedToBeFixedWarnings()` loop を廃止した。
- `lazy` は主戦略にしない。欠損一覧は membership 判定で全件評価が必要なため、速度面では side-effect-free index を一度作る方を正とする。
- `resource_health_index_build` / `resource_health_projection` log を追加し、`maintenance_update` / `installable_maintenance_deferred` には `resourceHealthIndexMs`, `warningReapplyTargets=0`, `warningChanged=0` を出す。

### Phase 6: file diff chunk commit と進捗粒度改善

完了済み。

目的は、初回空DBの file diff で最後に単一 transaction を長時間保持する構造をやめ、DB 永続化を bounded chunk 方針へ寄せることである。Phase 7.7 以降の既定 chunk は 10000 件で、rollback 範囲と commit overhead のバランスを取る。

#### 旧課題

- `ApplyFileScanDiff()` は `addedPaths` / `addedOrUpdatedBmsonPaths` を 512 件 batch に分ける。
- batch 内では `AsParallel()` で `ChartFileContentReader.ReadSnapshot()`、lightweight parse、inline `chart_info` parse を行う。
- batch 結果は `SongTableFileCheckResult` の次の list に蓄積される。
  - `AddedFiles`
  - `AddedBmsonSongs`
  - `InlineChartInfoRows`
  - `InlineChartInfoAppliedRows`
  - `InlineChartInfoParseFailureRows`
  - `NextFiles`
  - `NextBmsonSongs`
- DB 保存は最後に `song` / `bmson_song` / `chart_digest_map` / `chart_info` / `chart_info_parse_failure` を同一 transaction でまとめて行う。
- 進捗通知は batch の `ToList()` が完了した後に candidate を列挙するため、実際には 512 件単位で進んで見える。

#### 現行仕様

- file diff の lightweight parse は reader / parser workers / collector の bounded pipeline で行う。
  - reader は 1 本で `ChartFileContentReader.ReadSnapshot()` を実行し、bounded queue に `ChartFileSnapshot` を流す。
  - queue capacity は `fileDiffParserDegree * 2`。
  - parser worker degree は `max(1, Environment.ProcessorCount - 1)`。
  - worker は BMS / bmson lightweight parse を行い、collector が inline `chart_info` 解析 batch へ渡す。
  - inline `chart_info` 解析は collector 側で順次実行し、pipeline worker と chart_info parser の入れ子並列を避ける。
  - snapshot bytes は collector の batch flush 後に破棄され、全件分を保持しない。
- progress callback は parsed candidate の collector 到達ごとに 1 件単位で呼ぶ。
  - UI 側の `ReportLibraryInitializationProgress()` は 150ms throttle を持つため、1 件ごとに通知しても UI 更新は過剰になりにくい。
  - sub label は従来通り `ファイル差分確認 [processed/total] fileName` とする。
- DB 保存対象を `FileScanDiffCommitChunk` にまとめ、既定 10000 件単位で transaction commit する。
  - BMS追加/削除、bmson追加/更新/削除、`chart_digest_map`、inline `chart_info`、parse failure upsert/delete を同じ chunk 保存経路で扱う。
  - 同一譜面由来の `chart_info` / failure delete は、可能な範囲で同じ chunk に寄せる。
- runtime の chart_info index / UI 通知は chunk ごとには行わない。
  - DB commit は既定 10000 件単位で進める。
  - `BMSFiles` / `BmsonSongs` の in-memory catalog を切り替えた後、file diff で新規生成または更新した inline row だけを `file_diff_inline` として index に公開する。
  - current `chart_info` skip で取得した既存 row は、対象 model へ一時適用してもよいが、file diff の runtime index delta として全件蓄積しない。
  - current row の session index 正本更新は、直後の `chart_info_hydration` が担当する。
  - chunk ごとの `chart_info_index_delta` と大量の `PropertyChanged` を避ける。
- 全体 atomicity は持たない。
  - chunk 単位で atomic。
  - 途中失敗時は例外を伝播し、in-memory catalog は切り替えない。
  - DB の部分反映は次回 scan で収束させる。
- `SongTableFileCheckResult` に次の観測値を追加した。
  - `DbCommitChunkSize`
  - `DbCommitChunks`
  - `DbCommitMaxChunkMs`
  - `FileDiffReadMs`
  - `FileDiffParseMs`
  - `SnapshotQueueHighWatermark`

#### byte cap について

Phase 6 では byte cap は入れていない。件数ベースの bounded parallelism を維持し、必要性は追加ログで判断する。

- `file_diff_read_ms`
- `file_diff_parse_ms`
- `snapshot_queue_high_watermark`
- `db_commit_chunks`
- `db_commit_chunk_size`
- `db_commit_max_chunk_ms`

将来、大きい譜面が多く byte peak が問題になった場合は、次を追加候補にする。

- `file_diff_snapshot_bytes_max`
- `file_diff_added_files_buffered_max`
- `file_diff_inline_rows_buffered_max`

#### 期待効果

- DB commit のロック時間を短くし、失敗時の rollback 範囲を小さくする。
- `ファイル差分確認` の進捗が 1 件単位で滑らかになる。
- backfill と file diff の commit 方針が揃い、以後の調整がしやすくなる。
- snapshot bytes の全件 staging は解消する。
- `AddedFiles` / `AddedBmsonSongs` / `NextFiles` / `NextBmsonSongs` など最終 catalog 用 list は互換のため残す。ここがメモリピークになる場合は、次段で catalog builder 方式へ移行し、重複 list を減らす。
- `inline_chart_info_current_skipped_count` が大きい環境でも、`chart_info_index_delta reason=file_diff_inline` の upsert 件数は新規生成 row 近辺に収まることを期待値にする。

### Phase 7: chart_info backfill の事前候補判定

完了済み。

目的は、full backfill が不要なときに全 owner を走査してから 0 件と判定する固定費をなくすことである。

#### 現状

- 起動時 hydration 完了後、基本的に full backfill request を queue する。
- `BackfillChartInfosCore()` は `currentFiles` / `currentBmsonSongs` を全件 list 化する。
- `LoadChartInfoMap()` と current parse failure map を読み、`BuildTargets()` で各 owner を見る。
- `sha256` があり current `chart_info` がある場合は file read 前に skip する。
- current parse failure がある場合も file read 前に skip する。
- ただし全件 current の場合でも `BuildTargets()` 自体は全件走査するため、実 target 0 件でも `targetBuildMs` が大きくなる。

#### 現行仕様

- `BmsLibraryDbGateway.GetChartInfoBackfillCandidateSummary(parseTimeout)` で、backfill 候補数を事前集計する。
  - 複雑な multi JOIN / multi COUNT SQL は使わない。
  - `chart_digest_map`、`chart_info`、current `chart_info_parse_failure` を最小列で読み、C# の `Dictionary` / `HashSet` で `song` / `bmson_song` owner を一回ずつ分類する。
  - `lower(trim(...))` 付き JOIN により SQLite index が効かなくなる回帰を避ける。
- summary は次を返す。
  - `MissingDigestOwnerCount`
  - `MissingChartInfoOwnerCount`
  - `StaleChartInfoOwnerCount`
  - `CurrentParseFailureOwnerCount`
  - `CurrentChartInfoOwnerCount`
  - `CandidateOwnerCount`
- current 判定は既存と同じにする。
  - `chart_info.parser_version >= CurrentChartInfoParserVersion`
  - parse failure は parser version と timeout 条件を満たすものだけ current
- `CandidateOwnerCount == 0` の場合は full backfill を queue しない。
  - log: `chart_info_backfill skipped reason=no_candidates ...`
  - 実処理は起動しないが、progress phase 用に no-op の requested/completed version を発行する。
- `CandidateOwnerCount > 0` の場合だけ従来 pipeline を起動する。
- `BuildTargets()` は残す。
  - DB summary は起動判断用。
  - 実行時の race や memory model 反映のため、最終 target selection は従来通り service 内で行う。
- Startup / ReloadFiles では `chart_info_hydration` background task の中で、必要な full backfill まで同期的に完了させる。
  - その後に `installable_maintenance` task を実行する。
  - 起動直後の `chart_info` 大量反映と maintenance resource health 計算が同時に走ることを避ける。
  - progress 上は `ChartInfoHydrationDone` / `ChartInfoBackfillDone` / `InstallableMaintenanceDeferredDone` の順に進む。
  - ただし新規・更新ファイル由来の inline `chart_info` は hydration/backfill に回さず、file diff 内で完了済みにする。

#### 期待効果

- metadata bundle import + file diff inline で全件 current になっている初回空DBでは、full backfill の 95 秒級固定費を消せる。
- 旧バージョン DB / 外部で更新された DB / digest 欠落 DB では、必要なときだけ backfill を実行できる。
- 「0件 fast path」ではなく「backfill が必要かどうかの事前判定」として仕様化できる。

### Phase 7.5: file diff inline result の責務修正

完了済み。Phase 6/7 の後、metadata bundle import 済みの空DB初回起動で、`inline_chart_info_current_skipped_count` が約 20 万件あるにもかかわらず `chart_info_index_delta reason=file_diff_inline` も約 20 万件になり、メモリピークが 15GB 前後まで上がるケースが観測された。

これは bounded queue や chunk commit の問題ではなく、current skip した既存 `chart_info` row を file diff の成果物として後段に蓄積していることが主因である。

#### 現行仕様

- `FileScanDiffCommitChunk` は DB へ新規 upsert する `chart_info` row と、model に適用した既存 current row を区別する。
- chunk commit callback が `BMSLibrary` 側へ渡す row は、新規生成・更新した `chart_info` row に限定する。
- current skip row は、必要なら parse 中の model へ直接適用するだけにし、`committedInlineChartInfoRows` へ蓄積しない。
- `SongTableFileCheckResult.InlineChartInfoAppliedRows` は、テストや診断で必要な最小範囲に縮小する。production で callback がある場合は current skip row を保持しない。
- `UpsertChartInfoIndexRows(..., "file_diff_inline")` の入力件数は、`inline_chart_info_success_count` 近辺になることを期待値にする。
- 既存 DB row の全量 index 構築は `chart_info_hydration` の `ReplaceChartInfoIndex(...)` が担当する。
- `song_tbl_file_check_breakdown` には `inline_chart_info_index_published_count` を出し、runtime index delta に流した row 数を確認できるようにする。

#### 完了条件

- metadata bundle import 済みで `inline_chart_info_current_skipped_count` が大きい場合でも、file diff 由来の `chart_info_index_delta upserted=` が current skip 件数に比例しない。
- file diff 中の `AppliedChartInfoRows` / `committedInlineChartInfoRows` がメモリピークの支配要因にならない。
- `BMSFilesZeroNote` など chart_info 依存 view は、inline 新規 row と後続 hydration の組み合わせで正しく更新される。

### Phase 7.6: metadata bundle import の位置づけ整理

資料上の方針を整理済み。実装変更は不要とする。

metadata bundle は「所持譜面から生成された DB 由来補助情報」ではなく、「本アプリのリリースパッケージ同梱物、または外部から取得できるメタデータ」である。頻繁に import するデータではないため、import タイミングは起動直後に固定する。

#### 現行仕様

- 起動直後、`Initialize()` 本体の前に bundle import を試みる。
- `.db` / `.7z` が存在しない場合は何もしない。
- 同じ bundle が import 済みの場合は skip し、import 完了済み bundle は `imported_metadata/` へ退避する。
- import 成功 row の runtime index 全量 publish は行わず、後続 `chart_info_hydration` が session index の正本更新を担当する。
- diff target count によって import を background 化したり、file diff 後へ遅延したりしない。

#### 完了条件

- bundle が配置された起動では、file diff より前に import が完了し、inline `chart_info` current skip に利用できる。
- import 済み bundle は退避され、毎回 SHA-256 / 展開 / import 判定が critical path を圧迫しない。
- bundle がない通常起動では、起動直後 import check は軽い no-op になる。

### Phase 7.7: 初期化メモリ圧整理

Phase 8 の health 高速化へ進む前に、初期化中のメモリピークを抑える。対象は `file diff`、inline/full `chart_info`、`installable_maintenance_deferred` の一時 object lifetime と GC 境界である。

#### 追加で判明した前提

- `WAVfiles` / `BGAfiles` は BMS 本文の `#WAVxx` / `#BMPxx` などから得た、譜面が要求するリソース参照名の集合である。
- BMS では `#WAV` 定義が数百から 1000 件近くになる譜面も珍しくない。空DB初回起動で 20 万件規模を追加すると、この `HashSet<string>` と派生 cache が memory peak の支配要因になり得る。
- 現状の file diff では、`CreateBMSFileFromSnapshot()` 直後に `BMSFile.WAVfiles` / `BGAfiles` が作られ、その `BMSFile` が `AddedFiles` / `NextFiles` / 最終 `BMSFiles` へ進む。
- `WAVfiles` / `BGAfiles` は `SetHealthStatus(..., memClear: true)` の後に null 化されるが、その呼び出しは後続 `installable_maintenance_deferred` 側である。つまり file diff 完了から maintenance 完了まで、大量の要求リソース集合が正本 model にぶら下がったままになる。
- Phase 7.7 の明示 GC / LOH compact は「参照が切れた後の heap 整理」には有効だが、`BMSFile` 正本が参照を保持している間は大きく回収できない。

#### 前提

- メモリの山は maintenance 周辺で特に大きいが、軽量 parse / inline `chart_info` / chunk commit 周辺でも staging が重なる。
- `startup_background_task done` 後に Private Bytes が一気に下がる場合、恒久的 leak ではなく、task scope 内の一時参照が長く残っている可能性が高い。
- 初期化処理中は多少の停止を許容する。メモリ逼迫で paging や GC 遅延を起こすより、明示的に停止してでも heap を整理し、総初期化時間を短くすることを優先する。

#### 実装済み/短期方針

- memory checkpoint log を追加する。
  - `startup_memory_checkpoint phase=... point=before|after|after_gc privateBytes=... workingSet=... managedBytes=... gen0=... gen1=... gen2=...`
  - 主な checkpoint は `file_diff_start/done`、`chart_info_hydration_done`、`chart_info_backfill_done/skipped`、`installable_maintenance_deferred_start/done`、`startup_background_task_done`。
- phase 境界で大きな一時参照を明示的に切る。
  - file diff: commit 済み chunk staging、current skip row、inline result、diagnostic rows。
  - chart_info: hydration/backfill の snapshot map、commit buffer、current map。
  - maintenance: `filesSnapshot`、section targets、resource health working set、upsert rows、workflow 内の一時 collection。
  - 長寿命 object が持つ巨大 `List<T>` は `Clear()` だけでは backing array が残るため、空 list への差し替え、専用 `ReleaseLargeBuffers()`、または owner ごとの short-lived scope 化を優先する。
- explicit GC は phase 境界だけで行う。
  - `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` を設定し、blocking full GC を行う helper を用意する。
  - 実行条件は「startup/reload の heavy phase 完了後」かつ「PrivateBytes または managedBytes がしきい値以上」を基本にする。
  - 初期値としては、初回空DBや大量追加のような heavy path ではしきい値に関係なく `installable_maintenance_deferred done` 後に 1 回実行してよい。
  - GC 実行は DB transaction / write lock / UI dispatcher 同期処理の外で行う。
  - 進捗 phase は増やさない。必要なら log だけで `startup_memory_cleanup` として観測する。
- DB commit chunk size を見直す。
  - 1000 件は rollback 範囲を小さくするには安全だが、DB commit 回数と commit-side staging / callback overhead を増やす。
  - snapshot bytes を持ち越さず、current skip row も持ち越さない前提なら、5000-10000 件の方が総時間と heap churn のバランスがよい可能性がある。
  - Phase 7.7 では `FileDiffCommitChunkSize` を internal constant / test override 化し、既定を 10000 件にする。実測で `db_commit_max_chunk_ms` が悪化する場合は 5000 件へ戻す。
- Everything scan の raw buffer と resource index の寿命を明確化する。
  - raw buffer は resource index 構築後に不要なら破棄する。
  - resource index は導入先推定と Phase 8 health 判定で使うため保持するが、同等情報の二重構造を作らない。

#### 次に必要な寿命設計

Phase 7.7 の GC 境界だけでは、`WAVfiles` / `BGAfiles` が `BMSFile` 正本に残っている間の memory peak は解決しない。次の実装単位では、Phase 9 の file diff chunk maintenance 連携を前倒しし、譜面要求リソース参照の寿命を file diff chunk 内へ閉じる。

- 新規 BMS は `CreateBMSFileFromSnapshot()` 直後に `WAVfiles` / `BGAfiles` を使い、Everything / fallback scan 由来の resource index と照合して maintenance row を作る。
- maintenance row 作成後、最終 `BMSFiles` に載せる前、または遅くとも chunk commit 直後に `WAVfiles` / `BGAfiles` / 派生 hash cache を破棄する。
- 新規 bmson は `ParseSnapshot()` 済みの fresh resource refs を使い、同じく chunk 近傍で maintenance row を作る。
- 追加ファイル由来の maintenance row は `installable_maintenance_deferred` へ送らない。deferred は DB 由来の missing/stale maintenance 補完、force update、file diff で扱えなかった例外的対象に寄せる。
- `WAVfiles` / `BGAfiles` を「長期保持して後で使う」のではなく、「chunk 内で health/maintenance row へ畳み込み、破棄する」ことを正とする。

#### 完了条件

- 初期化中の memory checkpoint で、重い phase の after_gc で PrivateBytes / managedBytes が明確に下がることを確認できる。
- `startup_background_task done` まで一時 collection が残り続ける箇所を減らし、phase done 直後に解放できる。
- `startup_memory_cleanup reason=... elapsedMs=... managedBefore=... managedAfter=... privateBefore=... privateAfter=... compactLoh=...` で明示 GC の効果を確認できる。
- `inline_chart_info_current_skipped_count` が大きいケースでも、current skip row が heap peak の主因にならない。
- DB commit chunk size 変更後も、途中失敗時は chunk 単位 rollback と次回 scan 収束の方針を維持する。既定値は file diff / chart_info backfill とも 10000 件とする。
- GC は inner loop では発生させず、phase 境界のみで観測可能に実行される。

### Phase 8: health 計算の cache-aware 化と bounded 並列

Phase 8 では `installable_maintenance_deferred` の `set_health_ms` を下げる。Phase 5 で warning projection は軽くなったため、残る対象は実 health 判定である。

ただし、空DB初回起動の memory peak という観点では、Phase 8 単体よりも Phase 9 の「file diff chunk 内 maintenance 作成」と組み合わせることが重要である。Phase 8 の cache-aware 判定は、Phase 9 で新規追加譜面の `WAVfiles` / `BGAfiles` を早期に破棄するための共通 helper としても使う。

#### 現状

- `UpdateMaintenanceInfo()` は未チェック、または `forceUpdate` 対象を抽出し、section に分ける。
- section 内は `AsParallel().ForAll()` で処理するが、degree は明示されていない。
- BMS health は `BMSFile.SetHealthStatus(folderAllFileList, ...)` が担当する。
- `SetHealthStatus()` は以下を行う。
  - `WAVfiles` / `BGAfiles` が null の場合は BMS ファイルを再読込して refs を作る。
  - local refs は `BMSDirectoryFileNameHash` の basename hash で確認する。
  - nonlocal refs は `File.Exists` で相対パスを確認する。
  - stagefile / backbmp / banner も `File.Exists` 系で確認する。
- file scan で作った `DirectoryResourceLookupCache` / `DirectoryRelativePathHashIndex` は主に導入先推定用で、health には使っていない。

#### 実装後仕様

- maintenance の並列度を bounded にする。
  - file diff / chart_info と同じく `max(1, Environment.ProcessorCount - 1)` を既定にする。
  - internal override を持たせてテスト可能にする。
  - `AsParallel()` ではなく `Parallel.ForEach` + 明示 degree を使い、他の background pipeline と多重並列になりにくい形にする。
- `SetHealthStatus()` の cache-aware 経路を追加する。
  - `BMSDirectoryFileNameHash` に加えて `DirectoryResourceLookupCache` / `DirectoryRelativePathHashIndex` を受け取れるようにする。
  - local basename だけでなく、relative path hash / category hash で見られるものは scan result から判定する。
  - `File.Exists` は fallback に寄せる。
- 新規 file diff 由来の BMS では、BMS 本文再読込を避け、軽量 parse 済みの `WAVfiles` / `BGAfiles` を入力として health 判定する。
- DB 由来補完で refs がない場合のみ、安全側として path read / fallback 経路を許容する。
- `maintenance_update` log に health 判定の内訳を追加する。
  - `healthDegree`
  - `healthTargetCount`
  - `healthMs`
  - `encodingMs`
  - `bmsonRefreshMs`
  - `healthCacheHit`
  - `healthFileExistsFallback`
- 初回と2回目以降で対象数と fallback 数がどう変わるかを観測できるようにする。

#### 期待効果

- 初回全件 health の `set_health_ms` を下げる。
- I/O と CPU のスパイクを抑える。
- 導入先推定用に作っている resource index を health にも使い、scan 結果の再利用率を上げる。

### Phase 9: 譜面要求リソース参照の寿命整理と file diff chunk maintenance 連携

Phase 9 は Phase 8 後の単なる高速化ではなく、Phase 7.7 の memory peak を根本的に下げるために前倒しする。目的は、新規追加譜面について file diff で得た parse 結果と scan cache を使い、maintenance row 作成と `WAVfiles` / `BGAfiles` 破棄を同じ chunk 内で完了させることである。

#### 方針

- file diff pipeline で lightweight parse が成功した譜面について、同じ chunk 内で maintenance row を作れるようにする。
- BMS は `CreateBMSFileFromSnapshot()` で `WAVfiles` / `BGAfiles` を持っているため、health 計算に再読込は不要。
  - この集合は BMSFile 正本の長期 field として残すのではなく、chunk 内で maintenance row へ畳み込む一時入力として扱う。
  - maintenance row 作成後は `WAVfiles` / `BGAfiles` / `localWAVfilesNameHashArray` / `localBGAfilesNameHashArray` / nonlocal list などを破棄する。
- bmson は Phase 4 の fresh resource refs を使う。
- health 計算には Phase 8 の cache-aware 判定を使う。ただし Phase 8 の全体実装を待たず、file diff 由来の refs と scan cache を入力にできる最小 helper を先に切り出してよい。
- DB 保存は file diff commit chunk に含めることを第一候補にする。
  - `song` / `bmson_song` と同じ mutation count に紐づけ、同じ既定 10000 件 chunk で保存する。
  - 追加ファイル由来の maintenance row は `installable_maintenance_deferred` へ送らない。
  - 失敗時は対象 chunk のみ rollback し、次回 scan で収束させる。
- inline maintenance rows は inline `chart_info` と同じく chunk commit 後に長期 staging しない。
  - `committedInlineChartInfoRows` のような全件集約 collection を maintenance では作らない。
  - commit callback や diagnostic result に current/生成済み maintenance row を大量保持しない。
- 操作可能化の critical path へ入れる範囲は「新規ファイル由来の row 作成」に限定する。
  - DB 由来の missing/stale maintenance 補完は引き続き `installable_maintenance_deferred` でよい。
  - 新規ファイル由来の処理まで deferred へ押し出すと、初回と再起動後で欠損一覧・警告件数が揺れるため避ける。
- 進捗上は `LibraryFileDiffDone` に含める。
  - 進捗 sublabel は当面 `ファイル差分確認` のままでよい。
  - 必要なら詳細 counter として `inline_maintenance_*` log を追加する。

#### 期待効果

- 初回 `installable_maintenance_deferred` の対象を「既存DB補完」や「file diff で扱えなかったもの」中心へ減らせる。
- 新規追加譜面の refs / resource cache を近いタイミングで使える。
- `WAVfiles` / `BGAfiles` の大量 `HashSet<string>` を `BMSFiles` 正本へ長時間ぶら下げずに済む。
- 初回空DBでの総完了時間を短縮できる可能性が高い。
- 差分ファイルがある起動でも、操作可能直後のメンテナンス画面が再起動後に近い状態になる。

## Test Plan

- 空DB初回起動相当で、file diff 追加直後の `BMSFilesUnregistered` が Shift_JIS 非対応 path のみになること。
- Shift_JIS 非対応 path の既存 `song` が削除されず、warning 付きで残ること。
- `docs/sjis-path-validation-note.md` の削除→再追加往復が発生しないこと。
- `BMSFilesZeroNote` が `karinotes` ではなく `chart_info.notes == 0` を見ること。
- `setZeroNoteAndCommitToDB()` 廃止後、`song.karinotes` がアプリ起動・リロード・インストールで変更されないこと。
- `RecheckZeroNoteWarnings()` が `chart_info.notes == 0` 譜面を対象に、正規表現上の可視ノート風記述を warning 化すること。
- `installable_maintenance_deferred` が進捗ゲージに含まれ、完了前に progress が非表示にならないこと。
- bmson 初回追加で maintenance 再パースが発生しないこと。
- 2回目起動で maintenance 不足がない場合、全件 warning 再適用を避け、resource health index だけで欠損一覧を表示できること。
- file diff pipeline 化後、進捗 callback が 1 件単位で呼ばれ、UI 表示は throttle されつつ `[processed/total]` が滑らかに進むこと。
- file diff の DB 永続化が既定 10000 件前後の chunk に分割され、chunk 保存後に inline `chart_info` row / failure row / commit staging が破棄されること。
- file diff chunk commit 中に例外が発生した場合、失敗 chunk の transaction だけ rollback され、既に commit 済みの chunk と in-memory catalog の整合性が保たれること。
- full `chart_info` backfill の事前候補判定で candidate 0 の場合、backfill request が queue されず `chart_info_backfill skipped reason=no_candidates` が出ること。
- stale parser version、digest missing、current parse failure、current chart_info の各条件で backfill candidate summary が期待通りになること。
- current `chart_info` skip が大量にある file diff で、`file_diff_inline` の runtime index delta と retained row count が current skip 件数に比例しないこと。
- current skip row が大量にある file diff で、`committedInlineChartInfoRows` 相当の collection がメモリピークを作らないこと。
- maintenance の bounded degree が適用され、`healthDegree` log と実処理の並列度が一致すること。
- cache-aware health 判定で `DirectoryResourceLookupCache` / `DirectoryRelativePathHashIndex` が使われ、`File.Exists` fallback 数が観測できること。
- 新規追加分 maintenance を file diff chunk と連携する場合、追加 BMS / bmson の maintenance row が chunk 単位で保存され、後続 `installable_maintenance_deferred` の対象数が減ること。

## 注意点

- Shift_JIS 非対応 path は LR2 で選曲不能の可能性が高い。アプリ上では削除せず可視化するが、最終対応は改名/移動である。
- `chart_info.notes` は parser failure の譜面では存在しない。その譜面はゼロノート検索から除外する。
- 正規表現ベースの zero-note 判定は詳細 parser より粗い。今後は「安全側の補助チェック」として扱う。
- resource health warning は DB 永続化されない。通常一覧の WARNING 表示では `maintenanceInfo` / resource health index から投影される。
