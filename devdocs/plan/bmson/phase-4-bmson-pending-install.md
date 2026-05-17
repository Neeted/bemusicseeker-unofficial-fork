# Phase 4: bmson Pending / 導入先推定

## 目的

- `.bmson` を Pending package として BMS と同様に扱えるようにする
- `bmson` の既所持判定、導入先推定、手動導入先編集を Pending workflow に統合する
- Pending / install estimation / regroup を `BMS only` 前提から `BMS + bmson` 混在前提へ拡張する

## 着手前条件

- Phase 3 が完了している
- Phase 3.5 が完了している
  - playlist 詳細で `bmson owned` / `truly missing` が整理済み
  - `NO SONG` 表示崩れが解消済み
  - 単一 playlist reload の性能回帰が解消済み
  - 全体同期も単体リロードと同じ参照差し替え戦略で動作する
  - 外部プレイリスト更新判定は `playlist_entry active row` 比較へ整理済み
  - diff fingerprint ログで差分内容を追跡できる

## このフェーズで得られた成果

- `.bmson` を含む Pending package を認識できる
- pure `bmson` package と mixed package を扱える
- `.bmson` 単体ドロップでも BMS 単体と同様に package 化できる
- `bmson` の既所持判定と導入先推定が Pending workflow に統合されている
- `bmson` の WARNING / WAV / BGA / MOVIE 列を表示できる
- `bmson` 差分の導入先として `bms only` フォルダも選べる

## 現時点の達成状況

- 完了
  - `PendingChartEntry` により BMS / bmson を共通 wrapper で扱える
  - package discovery は pure `bmson` / mixed package / `.bmson` 単体ドロップを扱える
  - installed hash index は `md5/sha256 + BMS/bmson` 横断に拡張済み
  - `bmson` の resource 解析を Pending health 算出へ流し、`WARNING / WAV / BGA / MOVIE` 列が埋まる
  - `BMSONファイル単体です` を含む bmson 用 warning 表示が入っている
  - `bmson` の導入先推定と SEARCHING 状態が Pending workflow に統合されている
  - Pending BMS の `KEYS` 列回帰は `mode` コピーで解消済み
- ブラッシュアップ完了
  - 所持判定・導入先推定・Pending regroup / installed-only 判定は `md5優先 + sha256 fallback` から `key-selection` へ整理済み
  - 起動後ロジックでは旧 DB / 移行途中 DB を考慮せず、`md5` があれば `md5`、無ければ `sha256` の一度きり判定に統一済み

## スコープ内

- Pending package 検出の `.bmson` 対応
- install estimation の `md5/sha256` / `bmson_song` 対応
- `bms only` フォルダを導入先候補に含める
- regroup と installed-only overwrite の `bmson` 対応
- Pending health / warning / SEARCHING 表示の `bmson` 対応

## スコープ外

- `.bmson` 再生
- LR2 専用 maintenance 機能の `bmson` 対応
- mode 表示や UI polish の仕上げ

## 主な対象コード

- `BeMusicSeeker/Models/PendingChartEntry.cs`
- `BeMusicSeeker/Models/ChartPackage.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsonSongParser.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInstallEstimationService.cs`
- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/ViewModels/GridRowResolver.cs`
- `BeMusicSeeker/Views/MainWindow.cs`

## 実装済みタスク整理

1. package 検出対象へ `.bmson` を追加し、pure `bmson` / mixed package を扱えるようにした
2. `PendingChartEntry` で BMS / bmson を共通に保持し、Pending 表示や health 算出の入口を揃えた
3. `sound_channels` / `bga` / `preview_music` などを `BmsonSongParser` で拾い、`WAVfiles` / `BGAfiles` を使った warning 算出へ流した
4. `bmson` でも BMS と同じ warning 初期化経路に通し、single-file warning と missing-file warning を表示できるようにした
5. `.bmson` 単体ドロップや複数選択を BMS と同じ package 解釈へ寄せた
6. `bmson` の導入先推定と SEARCHING 表示を common estimation path に載せた
7. Pending BMS の `mode` コピー漏れを修正し、`KEYS=?` 回帰を解消した
8. Pending / install estimation / installed-only / regroup のハッシュ照合を `key-selection` へ整理した

## 設計メモ

- `BMSFile.bmsExtensions` を単純拡張せず、Pending / install estimation 側で `bmson` を扱う方針を維持した
- `bmson` は Phase 4 でも `BMSFile` 本体へ統合せず、`PendingChartEntry` ベースの共通 wrapper として扱う
- 導入先候補の定義は「既知の譜面ディレクトリ」に広げ、`bms only` フォルダも受理する
- 起動後ロジックでは、所持側 DB に `md5` と `sha256` が揃っている前提で判定する
- そのため所持判定や導入先推定は、入力側 chart / row が持っている主キーだけを使う `key-selection` を採用した
- playlist 側は外部入力の都合で `md5 only` / `sha256 only` があり得るため、row が持つ主キーに応じた両対応を維持する

## テスト追加方針 / 実施内容

- package discovery で `.bmson` を検出できるテスト
- `.bmson` 単体ドロップが BMS 単体と同様に package 化されるテスト
- `md5` 一致 / `sha256 only` 一致の導入先解決テスト
- `md5` がある chart は `sha256` 一致だけで installed 扱いにならないテスト
- `bmson` でも WARNING / WAV / BGA / MOVIE 列が埋まるテスト
- Pending BMS の `KEYS` 列が `mode` コピー漏れで `?` にならないテスト
- regroup / installed-only overwrite が `key-selection` 後も想定どおり動くテスト

## 完了条件

- `.bmson` を Pending へ積める
- `.bmson` 単体ドロップでも BMS 単体と同様に package 化される
- `bmson` の既所持判定と導入先推定が動く
- `bmson` の WARNING / WAV / BGA / MOVIE 列が埋まる
- Pending BMS の `KEYS` 列回帰が解消している
- Pending / install estimation / installed-only / regroup のハッシュ照合が `key-selection` に整理されている
- 既存 BMS / bmson ChartPackage 処理が壊れていない

## リスク

- mixed package で BMS と `bmson` が同居する場合の推定競合
- `md5` / `sha256` のどちらを主キーにするかで期待とズレるケース
- `Warn_InstallDirMustContainBms` 文言・仕様と「既知の譜面ディレクトリ」判定のズレ
- `bmson` の resource 解析を広げることで、Phase 3 の「詳細再生まではしない」方針を越えて実装が膨らむこと

## 次フェーズへの引き継ぎ

- Phase 4 は完了としてよい
- 残る論点は UI / mode 表示 / TAG 列の意味整理 / BMS 専用操作の見せ方であり、Phase 5 の領域
