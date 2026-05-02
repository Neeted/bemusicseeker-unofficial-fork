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

Phase 1-4 の譜面ファイル読み込み重複整理により、新規・更新譜面の lightweight parse と `chart_info` parse は原則 single-read 化済みである。残る主要課題は、LR2 互換情報の正規化、zero-note の責務整理、maintenance/health の初回全件処理である。

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

- `UpdateMaintenanceInfo()` の実チェック対象と warning 再適用対象を分離する。
- 2回目以降、maintenance 不足がない場合は全件 warning eager 再構築を避ける。
- 必要なら `BMSFilesNeedToBeFixed` 系 filter で lazy warning / lazy health projection を導入する。

## Test Plan

- 空DB初回起動相当で、file diff 追加直後の `BMSFilesUnregistered` が Shift_JIS 非対応 path のみになること。
- Shift_JIS 非対応 path の既存 `song` が削除されず、warning 付きで残ること。
- `docs/sjis-path-validation-note.md` の削除→再追加往復が発生しないこと。
- `BMSFilesZeroNote` が `karinotes` ではなく `chart_info.notes == 0` を見ること。
- `setZeroNoteAndCommitToDB()` 廃止後、`song.karinotes` がアプリ起動・リロード・インストールで変更されないこと。
- `RecheckZeroNoteWarnings()` が `chart_info.notes == 0` 譜面を対象に、正規表現上の可視ノート風記述を warning 化すること。
- `installable_maintenance_deferred` が進捗ゲージに含まれ、完了前に progress が非表示にならないこと。
- bmson 初回追加で maintenance 再パースが発生しないこと。
- 2回目起動で maintenance 不足がない場合、全件 warning 再適用を避けられること。

## 注意点

- Shift_JIS 非対応 path は LR2 で選曲不能の可能性が高い。アプリ上では削除せず可視化するが、最終対応は改名/移動である。
- `chart_info.notes` は parser failure の譜面では存在しない。その譜面はゼロノート検索から除外する。
- 正規表現ベースの zero-note 判定は詳細 parser より粗い。今後は「安全側の補助チェック」として扱う。
- health warning は runtime structured warning であり、DB 永続化されない。lazy 化する場合、フィルタ・ソート・行ハイライトの整合性テストが必要になる。
