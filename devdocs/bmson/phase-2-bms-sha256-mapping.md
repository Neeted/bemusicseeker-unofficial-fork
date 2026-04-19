# Phase 2: BMS の md5-sha256 マッピング

## 目的

- 既存 BMS に対して `sha256` を計算し、`md5 -> sha256` を参照できるようにする
- `sha256` プレイリストで既存 BMS を所持判定できるようにする
- `md5` 直結の判定箇所を、徐々に「複数ハッシュ対応」に寄せる

## このフェーズで得たい成果

- 既存ライブラリの BMS に `sha256` が付与される
- `sha256` しか持たない playlist entry でも BMS 所持判定できる
- `md5` しか持たない entry の従来動作もそのまま維持される
- プレイリスト summary と detail が `sha256` ベースでも正しく owned 判定できる
- 初回 `sha256` バックフィル中の進捗がユーザーに見える

## スコープ内

- BMS 用 `md5-sha256` マップテーブル追加
- 初期化時またはメンテ時の `sha256` バックフィル
- 初回 `sha256` バックフィルの進捗表示
- プレイリスト参照マップを `md5/sha256` 対応に拡張
- owned hash snapshot を `sha256` も見られる形に拡張

## スコープ外

- `bmson_song` テーブル追加
- `.bmson` 実ファイルの所持管理
- Pending package の `.bmson` 対応

## 想定する DB 変更

### `chart_digest_map`

- `md5 TEXT PRIMARY KEY`
- `sha256 TEXT NULL`
- `last_seen_path TEXT NULL`
- `updated_at TEXT NOT NULL`

### インデックス

- `chart_digest_map_idx_sha256 (sha256)`

## 主な対象コード

- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPlaylistReferenceService.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`

## 着手前確認

- Phase 1 の前提は満たしている
  - `playlist_entry.sha256` が利用可能
  - playlist 再同期は `sha256` fallback 可能
  - UI には `SHA256 HASH` 列の受け皿がある
- Phase 1.5 を先に入れる前提で進める
  - 初回バックフィルや app-owned table 変更は警告ダイアログ同意後のみ開始する
- したがって、Phase 2 のプラン作成に進んで問題ない
- ただし現時点では `BMSFile.sha256` は placeholder なので、このフェーズで実値供給方法を決める必要がある
- `song` テーブルには列追加しない前提を維持する
- app-owned table は `song.db` 内に追加する前提で進めるのが自然

## 実装タスク

1. `chart_digest_map` の作成と migration を追加する
2. BMS 実ファイルから `sha256` を計算する共通処理を追加する
3. 初期化時に既存 BMS の `md5/sha256` 対応表をロードする
4. 足りない行をバックフィルする
5. 初回バックフィルの進捗表示を追加する
6. プレイリスト参照マップを `md5` と `sha256` の両方で構築できるようにする
7. 所持判定 snapshot を `md5` だけでなく `sha256` も見られる形にする
8. playlist summary の owned 判定を調整する

## 設計メモ

- `song` テーブルには `sha256` を持たせない
- `BMSFile` 本体にすぐ `sha256` を常駐させるかは実装時に再判断する
  - 最初は補助インデックスで十分
- Phase 1 で `BMSFile.sha256` の read-only placeholder は追加済み
  - Phase 2 ではここに実値を載せるか、別 snapshot/resolver に留めるかを決める
- `sha256` の計算コストがあるため、一括バックフィルはログを厚めに出す
- 初回バックフィルはかなり時間がかかってよい
  - その代わり進捗表示を必須にする
- ここでの目的は「BMS が `sha256` でも引けるようにする」ことであり、`md5` 主導の既所持確認を捨てることではない
- `chart_digest_map` は app-owned table なので、migration と index 追加は `BmsLibraryDbGateway` 側で一元管理した方が扱いやすい
- owned 判定は summary と detail の両方に跨るため、`playlist reference map` と `owned hash snapshot` を同時に直す前提で計画する
- 進捗表示は「総件数」「完了件数」「現在処理中のパスまたはフォルダ」「キャンセル可否」を最初に決めておく
- バックフィル開始前の警告文面と進捗表示の責務は分ける
  - 警告は Phase 1.5
  - 実行中の可視化は Phase 2

## テスト追加方針

- 新規 `md5-sha256` マップ用テスト
- `BmsLibraryPlaylistReferenceServiceTests`
  - `sha256` で table 参照が付く
- `BmsPlaylistUpdateTests` または summary 系テスト
  - `sha256` playlist の owned 数が正しい
- 初期化系テスト
  - バックフィル時に既存挙動を壊さない
- `MainWindowViewModel` 系
  - playlist detail の owned / not-owned 表示が `sha256` entry でも崩れない
- 進捗表示テスト
  - 初回バックフィル中に progress snapshot が更新される
  - バックフィル完了時に progress 状態が解除される

## 完了条件

- 既存 BMS ライブラリに `sha256` 対応表が作られる
- `sha256` プレイリストで所持判定できる
- `md5` のみの既存挙動が維持される
- 初回バックフィルの進捗が UI から確認できる

## 実装確認メモ

- 完了
  - `chart_digest_map` を `song.db` 内の app-owned table として追加済み
  - 起動前 preflight / 警告同意後に `chart_digest_map` schema が作成される
  - `BMSFile.sha256` は placeholder ではなく実値保持に変更済み
  - `LoadSongTable()` で `chart_digest_map` から既存 `sha256` をロードできる
  - 起動完了前に未計算 BMS の `sha256` をバックフィルする
  - startup progress に `SHA-256 生成 [x/y]` を表示する
  - playlist detail / summary / table reference は `md5` 優先、`sha256` fallback で owned 判定できる
  - `sha256` only playlist entry でも BMS 実体へ参照付けできる
- 既知の境界
  - このフェーズで対応したのは既存 BMS ライブラリのみ
  - `.bmson` 実体のカタログ化や owned 解決はまだ未着手
  - LR2 依存の `song.hash(md5)` 前提機能は従来どおり据え置き

## Phase 3 着手可否

- 進めてよい
  - Phase 3 で必要な `md5/sha256` 両対応の playlist 受け皿は整った
  - preflight と移行警告の導線も整っているため、`bmson_song` 追加を同じ流儀で進められる
  - owned 判定まわりは `md5 -> sha256` fallback 前提へ寄せ終わっている
- Phase 3 で最初に決めるべき点
  - `bmson_song` を `BMSFile` と分離した別モデルで持つこと
  - playlist detail の real file 解決に、BMS だけでなく `bmson` catalog snapshot をどう混ぜるか

## リスク

- 大規模ライブラリでの初回バックフィル時間
- `md5` と `sha256` の二重索引でキャッシュ無効化が複雑になる
- summary/detail で一致判定の実装漏れが出やすい
- バックフィルの実行タイミング次第で初期化時間の印象が大きく変わる
- `sha256` 実値の保持場所を曖昧にすると、Phase 3 の `bmson_song` 設計にしわ寄せが出る
- 進捗更新頻度が高すぎると UI スレッド負荷やログ量が増える
