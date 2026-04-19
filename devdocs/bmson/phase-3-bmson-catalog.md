# Phase 3: bmson カタログ導入

## 目的

- `.bmson` を LR2 とは独立したアプリ管理対象として保持する
- `bmson` の軽量メタ情報を読み取り、所持譜面一覧に統合できる基盤を作る
- `md5` と `sha256` の両方を識別子として `bmson` を扱えるようにする

## このフェーズで得たい成果

- `bmson_song` テーブルで `.bmson` が管理できる
- `bmson` をスキャンしてタイトル、アーティスト、レベル、モード、リソース参照を取得できる
- プレイリストで `md5` または `sha256` が一致する `bmson` を所持譜面として解決できる

## 現状メモ

- `bmson_song` テーブル、軽量パーサ、スキャン、playlist 詳細での `md5/sha256` 解決は実装済み
- `bmson` 重複時の代表選択も実装済み
  - 同一 hash に複数パスがある場合は `path` 昇順先頭を代表にする
- 起動前 preflight と app-owned schema repair の導線にも `bmson_song` が組み込まれている
- playlist 詳細の表示 / 経路整理は当初未完だったが、この残件は [Phase 3.5](phase-3-5-bmson-playlist-detail-fix.md) で解消済み
- したがって、現在は Phase 3 を「完了」と扱ってよい

## スコープ内

- `bmson_song` テーブル追加
- `.bmson` 軽量パーサの新設
- `.bmson` スキャン経路追加
- プレイリスト詳細画面への `bmson` 所持反映

## スコープ外

- `.bmson` 再生
- `bmson` 専用 maintenance 機能の深掘り
- Pending package への導入

## 想定する DB 変更

### `bmson_song`

- `path TEXT PRIMARY KEY`
- `md5 TEXT NULL`
- `sha256 TEXT NULL`
- `title TEXT NULL`
- `subtitle TEXT NULL`
- `artist TEXT NULL`
- `subartist TEXT NULL`
- `genre TEXT NULL`
- `level REAL NULL`
- `mode_hint TEXT NULL`
- `judge_rank REAL NULL`
- `total REAL NULL`
- `banner TEXT NULL`
- `backbmp TEXT NULL`
- `stagefile TEXT NULL`
- `preview_music TEXT NULL`
- `folder_path TEXT NULL`
- `last_write_time TEXT NULL`
- `updated_at TEXT NOT NULL`

### インデックス

- `bmson_song_idx_md5 (md5)`
- `bmson_song_idx_sha256 (sha256)`
- `bmson_song_idx_folder_path (folder_path)`

## スキーマ上の注意

- `path` はユニーク
- `md5` は非ユニーク
- `sha256` は非ユニーク
  - 同一内容の `bmson` が別パスに複数存在し得るため

## 主な対象コード

- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs`
- 新規 `bmson` モデル/パーサ
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`

## 着手前確認

- Phase 1.5 は完了している
  - 起動前警告と preflight 導線がある
- Phase 2 は完了している
  - `chart_digest_map`
  - `BMSFile.sha256`
  - playlist の `md5/sha256` owned 判定
  - 初回バックフィル進捗表示
- したがって Phase 3 は、`bmson` 実体を新規 catalog として足す作業に集中できる

## 実装タスク

1. `bmson_song` モデルと DB アクセスを追加する
2. `.bmson` 軽量パーサを追加する
3. `"D:\github-clone\jbms-parser\src\bms\model\BMSONDecoder.java"` を参考に、最低限以下を取る
   - `md5`
   - `sha256`
   - `info.title`
   - `info.subtitle`
   - `info.chart_name`
   - `info.artist`
   - `info.subartists`
   - `info.genre`
   - `info.level`
   - `info.mode_hint`
   - `banner/back_image/eyecatch_image/preview_music`
   - `sound_channels`, `bga` 由来の参照ファイル
4. `.bmson` スキャン結果を `bmson_song` に保存する
5. playlist 詳細画面で `md5` または `sha256` 一致の `bmson` を実体解決できるようにする

## 設計メモ

- `BMSFile` は `LR2SongDB.song` 継承で `md5` 前提が深いため、`bmson` を無理に同型へ寄せない
- 最初は「playlist 行の owned 解決に必要なメタ情報」だけ取ればよい
- `mode_hint` は文字列のまま保持し、既存 `mode int` とは切り離す
- `sha256` は外部プレイリスト受け皿として重要だが、既所持確認自体は `md5` でも成立してよい
- したがって `bmson_song` は `md5` と `sha256` を両方持つ前提で進める

## テスト追加方針

- `bmson` パーサ単体テスト
- 初期化/スキャンで `bmson_song` に保存されるテスト
- プレイリスト `sha256` entry が `bmson` 実体へ解決されるテスト
- プレイリスト `md5` entry が `bmson` 実体へ解決されるテスト

## 完了条件

- `.bmson` をカタログ化できる
- `md5` または `sha256` playlist entry から `bmson` 所持判定できる
- `md5` / `sha256` 重複の別パス複数件を許容できる

## 完了状況の整理

- 完了
  - `.bmson` の catalog 化
  - `bmson_song` の保存 / 再読込
  - `md5` / `sha256` での `bmson` 実体解決
  - 重複 hash に対する代表選択
  - 起動前 warning / repair 導線での `bmson_song` schema 取扱い
- Phase 3 単体の未完
  - なし
- Phase 3 で入れて、後続フェーズで仕上げたもの
  - playlist 詳細での `bmson owned` 行の表示整合性
  - `NO SONG` fallback の維持
  - 単一 playlist reload 時の詳細再構築性能の是正

## リスク

- `bmson` JSON の揺れ
- `mode_hint` の種類が広く、既存 UI では表現しきれない
- リソース参照の扱いを広げすぎると Phase 3 が重くなる

## Phase 4 への判断

- Phase 3 の前提は満たせている
- ただし Phase 4 では playlist 詳細ではなく Pending / install estimation 側へ `bmson` を拡張するため、playlist 側の安定化が済んだ現在の状態を前提に進めるのがよい
