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

## 目標

- 初回起動と直後の再起動で、メンテナンス画面の件数が大きく変わらないようにする。
- BeMusicSeeker が LR2 `song.karinotes` を補完しないようにし、zero-note 表示は `chart_info.notes` へ寄せる。
- Shift_JIS 非対応パスを削除せず、LR2 parent を設定できない譜面として可視化する。
- `installable_maintenance_deferred` を進捗に含め、重い background 更新を見える化する。
- bmson 再パースや 2 回目以降の全件 warning 再適用を減らす。

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

### Phase 6: file diff pipeline 化と chunk commit

次に優先する。目的は、初回空DBの file diff で全件 staging を抱えたまま最後に一括 commit する構造をやめ、backfill と同じ pipeline / chunk 方針へ寄せることである。

#### 現状

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

#### 修正方針

- file diff も backfill と同じく pipeline 化する。
  - 単一 reader が `ChartFileSnapshot` を読み、bounded queue へ投入する。
  - parser worker が lightweight parse と inline `chart_info` を行う。
  - result collector が 1 件ごとに結果を集計し、進捗 callback を呼ぶ。
  - commit writer が chunk ごとに DB 永続化する。
- commit chunk size は `chart_info` backfill と揃えて 1000 件を既定にする。
- 進捗 callback は 1 件ごとに呼ぶ。
  - UI 側の `ReportLibraryInitializationProgress()` は 150ms throttle を持つため、1 件ごとに通知しても UI 更新は過剰になりにくい。
  - sub label は従来通り `ファイル差分確認 [processed/total] fileName` とする。
- DB 保存対象を `FileDiffCommitChunk` のような短命構造にまとめ、保存後は chunk list を破棄する。
- `NextFiles` / `NextBmsonSongs` は最終 in-memory catalog 用に必要だが、commit 用 staging と二重に全件保持しない。
  - 追加 BMS は parse 成功後に最終 builder へ append する。
  - DB commit 用 list は chunk 保存後に clear する。
- inline `chart_info` の成功 row / failure row / delete md5 も chunk commit 後に破棄する。
- 削除 row / update row も 1000 件単位で保存できるよう、既存の一括 transaction から chunk transaction へ移す。
- `SongTableFileCheckResult` は全件 row を返すのではなく、count / elapsed / changed summary と最終 catalog を返す形に寄せる。

#### byte cap について

Phase 6 では byte cap は必須にしない。bounded queue によって reader が先行しすぎない設計にする。

- reader queue capacity は `parserDegree * 2` 程度を既定にする。
- parser が遅い場合は queue が詰まり reader が止まる。
- file read が遅い場合は worker が待つ。
- どちらが bottleneck かは `readMs` / `parseMs` / queue wait 系 log で見る。

将来、大きい譜面が多く byte peak が問題になった場合に備え、観測 log は追加する。

- `file_diff_queue_capacity`
- `file_diff_commit_chunk_count`
- `file_diff_snapshot_bytes_max`
- `file_diff_snapshot_queue_high_watermark`
- `file_diff_added_files_buffered_max`
- `file_diff_inline_rows_buffered_max`

#### 期待効果

- file diff 中のメモリピークを下げる。
- DB commit のロック時間を短くし、失敗時の rollback 範囲を小さくする。
- `ファイル差分確認` の進捗が 1 件単位で滑らかになる。
- backfill と file diff の読み取り/解析/commit 方針が揃い、以後の調整がしやすくなる。

### Phase 7: chart_info backfill の事前候補判定

Phase 6 の次に行う。目的は、full backfill が不要なときに全 owner を走査してから 0 件と判定する固定費をなくすことである。

#### 現状

- 起動時 hydration 完了後、基本的に full backfill request を queue する。
- `BackfillChartInfosCore()` は `currentFiles` / `currentBmsonSongs` を全件 list 化する。
- `LoadChartInfoMap()` と current parse failure map を読み、`BuildTargets()` で各 owner を見る。
- `sha256` があり current `chart_info` がある場合は file read 前に skip する。
- current parse failure がある場合も file read 前に skip する。
- ただし全件 current の場合でも `BuildTargets()` 自体は全件走査するため、実 target 0 件でも `targetBuildMs` が大きくなる。

#### 修正方針

- `BmsLibraryDbGateway` に backfill 候補数を DB 側で集計する API を追加する。
  - 例: `GetChartInfoBackfillCandidateSummary(parseTimeout)`
- summary は少なくとも次を返す。
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
  - progress phase は request しない、または request 済みの場合は skip 完了にする。
- `CandidateOwnerCount > 0` の場合だけ従来 pipeline を起動する。
- `BuildTargets()` は残す。
  - DB summary は起動判断用。
  - 実行時の race や memory model 反映のため、最終 target selection は従来通り service 内で行う。

#### 期待効果

- metadata bundle import + file diff inline で全件 current になっている初回空DBでは、full backfill の 95 秒級固定費を消せる。
- 旧バージョン DB / 外部で更新された DB / digest 欠落 DB では、必要なときだけ backfill を実行できる。
- 「0件 fast path」ではなく「backfill が必要かどうかの事前判定」として仕様化できる。

### Phase 8: health 計算の cache-aware 化と bounded 並列

Phase 8 では `installable_maintenance_deferred` の `set_health_ms` を下げる。Phase 5 で warning projection は軽くなったため、残る対象は実 health 判定である。

#### 現状

- `UpdateMaintenanceInfo()` は未チェック、または `forceUpdate` 対象を抽出し、1000 件 section に分ける。
- section 内は `AsParallel().ForAll()` で処理するが、degree は明示されていない。
- BMS health は `BMSFile.SetHealthStatus(folderAllFileList, ...)` が担当する。
- `SetHealthStatus()` は以下を行う。
  - `WAVfiles` / `BGAfiles` が null の場合は BMS ファイルを再読込して refs を作る。
  - local refs は `BMSDirectoryFileNameHash` の basename hash で確認する。
  - nonlocal refs は `File.Exists` で相対パスを確認する。
  - stagefile / backbmp / banner も `File.Exists` 系で確認する。
- file scan で作った `DirectoryResourceLookupCache` / `DirectoryRelativePathHashIndex` は主に導入先推定用で、health には使っていない。

#### 修正方針

- maintenance の並列度を bounded にする。
  - file diff / chart_info と同じ `min(4, max(1, Environment.ProcessorCount - 1))` を既定にする。
  - internal override を持たせてテスト可能にする。
- `SetHealthStatus()` の cache-aware 版を追加する。
  - `BMSDirectoryFileNameHash` に加えて `DirectoryResourceLookupCache` / `DirectoryRelativePathHashIndex` を受け取れるようにする。
  - local basename だけでなく、relative path hash / category hash で見られるものは scan result から判定する。
  - `File.Exists` は fallback に寄せる。
- `maintenance_update` log に health 判定の内訳を追加する。
  - `healthDegree`
  - `healthCacheHit`
  - `healthFileExistsFallback`
  - `healthNonlocalRefs`
  - `healthOptionalRefs`
  - `healthSections`
- 初回と2回目以降で対象数と fallback 数がどう変わるかを観測できるようにする。

#### 期待効果

- 初回全件 health の `set_health_ms` を下げる。
- I/O と CPU のスパイクを抑える。
- 導入先推定用に作っている resource index を health にも使い、scan 結果の再利用率を上げる。

### Phase 9: 新規追加分 maintenance の file diff chunk 連携

Phase 9 は Phase 6 / 8 の後に検討する。目的は、新規追加譜面について file diff で得た parse 結果と scan cache を使い、maintenance 作成をより近い場所で行うことである。

#### 方針

- file diff pipeline で lightweight parse が成功した譜面について、同じ chunk 内で maintenance row を作れるようにする。
- BMS は `CreateBMSFileFromSnapshot()` で `WAVfiles` / `BGAfiles` を持っているため、health 計算に再読込は不要。
- bmson は Phase 4 の fresh resource refs を使う。
- health 計算には Phase 8 の cache-aware 判定を使う。
- DB 保存は file diff commit chunk に含めるか、maintenance 専用 chunk として直後に流す。
- 操作可能化の critical path に入れすぎないよう、次のどちらにするかは実測で判断する。
  - file diff commit に含めて初回表示の整合性を最大化する。
  - file diff 後の deferred chunk として流し、操作可能化を優先する。

#### 期待効果

- 初回 `installable_maintenance_deferred` の対象を「既存DB補完」や「file diff で扱えなかったもの」中心へ減らせる。
- 新規追加譜面の refs / resource cache を近いタイミングで使える。
- 初回空DBでの総完了時間を短縮できる可能性が高い。

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
- file diff の DB 永続化が 1000 件前後の chunk に分割され、chunk 保存後に inline `chart_info` row / failure row / commit staging が破棄されること。
- file diff chunk commit 中に例外が発生した場合、失敗 chunk の transaction だけ rollback され、既に commit 済みの chunk と in-memory catalog の整合性が保たれること。
- full `chart_info` backfill の事前候補判定で candidate 0 の場合、backfill request が queue されず `chart_info_backfill skipped reason=no_candidates` が出ること。
- stale parser version、digest missing、current parse failure、current chart_info の各条件で backfill candidate summary が期待通りになること。
- maintenance の bounded degree が適用され、`healthDegree` log と実処理の並列度が一致すること。
- cache-aware health 判定で `DirectoryResourceLookupCache` / `DirectoryRelativePathHashIndex` が使われ、`File.Exists` fallback 数が観測できること。
- 新規追加分 maintenance を file diff chunk と連携する場合、追加 BMS / bmson の maintenance row が chunk 単位で保存され、後続 `installable_maintenance_deferred` の対象数が減ること。

## 注意点

- Shift_JIS 非対応 path は LR2 で選曲不能の可能性が高い。アプリ上では削除せず可視化するが、最終対応は改名/移動である。
- `chart_info.notes` は parser failure の譜面では存在しない。その譜面はゼロノート検索から除外する。
- 正規表現ベースの zero-note 判定は詳細 parser より粗い。今後は「安全側の補助チェック」として扱う。
- resource health warning は DB 永続化されない。通常一覧の WARNING 表示では `maintenanceInfo` / resource health index から投影される。
