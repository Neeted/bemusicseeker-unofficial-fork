# Phase 1: Playlist SHA-256 基盤

## 目的

- `playlist_entry` に `sha256` を追加する
- プレイリスト読込・保存・再同期が `md5` だけでなく `sha256` も扱える状態にする
- UI で `sha256` を表示できる土台を作る

## このフェーズで得たい成果

- `sha256` を持つ外部プレイリストを読み込める
- `md5` と `sha256` のどちらか片方だけを持つ entry を扱える
- `playlist_entry` を DB に永続化できる
- プレイリスト詳細行に `sha256` を表示できる
- 既存 `md5` プレイリストの挙動を壊さない

## スコープ内

- `playlist_entry.sha256` 列の追加
- `playlist_entry` 用インデックス追加
- `BMSTableEntry` の JSON 読込・書出し対応
- `BMSPlaylist` の保存、再同期、一意条件見直し
- `PlaylistDetailSourceRow` / `PlaylistDetailRow` / `GridRowResolver` の `sha256` 対応
- DataGrid の `sha256` カラム表示

## スコープ外

- 既存 BMS の `sha256` 計算
- `bmson` の実ファイル走査
- `bmson` 所持譜面の表示
- 導入先推定ロジック変更

## 想定する DB 変更

### `playlist_entry`

- `sha256 TEXT NULL`

### インデックス

- `playlist_entry_idx_sha256 (sha256, playlist_id, is_removed)`
- 既存 unique 条件は `sha256` を含めて再定義する

## 主な対象コード

- `BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs`
- `BeMusicSeeker/Models/BMSTableEntry.cs`
- `BeMusicSeeker/Models/BMSPlaylist.cs`
- `BeMusicSeeker/ViewModels/PlaylistDetailSourceRow.cs`
- `BeMusicSeeker/ViewModels/PlaylistDetailRow.cs`
- `BeMusicSeeker/ViewModels/GridRowResolver.cs`
- `BeMusicSeeker/ViewModels/dataGridColumnsSettings.cs`
- `BeMusicSeeker/Views/MainWindow.xaml`

## 実装タスク

1. `playlist_entry` のスキーマ拡張と migration 方針を決める
2. `BMSTableEntry` に `sha256` プロパティを追加する
3. 外部 JSON 読込で `sha256` を受け取れるようにする
4. JSON 出力にも `sha256` を含める
5. `BMSPlaylist.CommitBMSTable` / `CommitBMSTableEntry` の一意条件を更新する
6. 再同期マージで `md5` と `sha256` の両方を使った対応付け方針を実装する
7. playlist 詳細表示行へ `sha256` を通す
8. DataGrid カラムと列設定を追加する

## 設計メモ

- 既存実装は `md5` だけで多くの matching をしているため、Phase 1 では `sha256` を「読める・持てる・見える」段階までに留める
- matching 優先順位は当面以下を想定する
  - `md5`
  - `sha256`
  - `lr2_bmsid`
  - `title`
- ただしこの優先順位は BMS / bmson の種別固定ではなく、entry が保持している識別子に対して適用する
- 外部テーブルの再同期時に、`md5` が無く `sha256` のみを持つ行も維持できるようにする
- `sha256` 対応の主目的は、外部プレイリストが `sha256` 識別子を持つケースの受け皿を先に作ること

## テスト追加方針

- `BmsPlaylistExternalLoadTests`
  - `sha256` のみを持つエントリを読める
- `PlaylistReloadMergeTests`
  - `md5` 不在でも `sha256` で再同期マージできる
- `PlaylistViewPipelineTests`
  - `sha256` 表示値が playlist row に伝播する
- `PlaylistUrlCompletionTests`
  - `sha256` 追加後も既存 `md5` 永続化が壊れない

## 完了条件

- `sha256` を持つ `playlist_entry` が読込・保存・再読込できる
- 既存 `md5` 系テストが壊れていない
- UI で `sha256` カラムを表示できる

## 実装確認メモ

- 完了
- `playlist_entry.sha256` の schema migration が実装済み
  - `BMSPlaylist` 初期化時と `LoadPlaylistDump` 復元時の両方で schema helper を通す
- 完了
- `playlist_entry_idx_sha256` が追加済み
- 完了
- unique index は `sha256` を含む形に更新済み
- 完了
- `BMSTableEntry` は `sha256` の JSON 読込・書出し、正規化、invalid 値の握りつぶしに対応済み
- 完了
- 再同期マージは `md5 -> sha256 -> lr2_bmsid -> title` の順で fallback する
- 完了
- `CommitBMSTableEntry` の一意条件に `sha256` を含めている
- 完了
- playlist row と shared DataGrid に `SHA256 HASH` 列が追加済み
- 完了
- `BMSFile.sha256` は Phase 1 用の placeholder 実装で、現時点では空文字返却
- 完了
- Phase 1 の確認テストは追加済み
  - external load
  - reload merge
  - view pipeline
  - schema migration
  - commit uniqueness

## Phase 1 完了時点で未着手のもの

- BMS 実ファイルの `sha256` 計算
- `sha256` を使った library owned 判定
- playlist summary の `sha256` ベース owned 集計
- `md5 -> sha256` マップテーブル

## リスク

- `CreateTable<T>()` だけでは既存 DB に列が増えない
- `CommitBMSTableEntry` の DELETE 条件を漏らすと重複行が残る
- DataGrid の列追加で既存の列設定互換が崩れる可能性がある
