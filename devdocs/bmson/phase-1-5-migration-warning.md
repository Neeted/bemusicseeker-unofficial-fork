# Phase 1.5: bmson 対応前の移行警告

## 目的

- `bmson` 対応に伴う DB 変更と初回 `sha256` バックフィルの前に、ユーザーへ明示的な警告を出す
- 過去バージョンとの互換性リスクを事前に案内する
- 同意が得られない場合は何も変更せず安全に終了する

## このフェーズで得たい成果

- アプリ起動時、`bmson` 対応のための移行が必要な場合だけ警告ダイアログが出る
- ダイアログには以下が明記される
  - 初回は `sha256` 生成のためかなり時間がかかること
  - `playlist_entry` など app-owned table に変更を加えること
  - 従来版 BeMusicSeeker や本フォーク版 `v1.2.1.0` 以前との互換性に注意が必要なこと
- ユーザーがキャンセルした場合は、何もせずアプリを終了する

## スコープ内

- 起動直後の移行前チェック
- 移行警告ダイアログ表示
- キャンセル時の即時終了
- 「どの移行が未実施か」を判定する軽量な preflight
- 今後の Phase 2 バックフィル開始条件としての同意フラグ管理

## スコープ外

- `sha256` バックフィル本体
- `bmson_song` 追加
- バージョン番号変更

## 想定する実装方針

- 起動時に `BmsonMigrationPreflightService` による軽量チェックを実行する
- 判定対象は少なくとも以下
  - `playlist_entry.sha256` migration が未適用
  - `chart_digest_map` が未作成、または未バックフィル
- いずれかに該当する場合、通常初期化より前に警告ダイアログを表示する
- ユーザーが同意した場合のみ、以降の migration / backfill を許可する
- キャンセル時は DB 書き込みやバックフィルを開始せず終了する

## ダイアログに含めるべき警告

- `bmson` 対応のため、初回起動時にプレイリストやハッシュ管理用テーブルへ変更を加える
- 初回の `sha256` 生成はライブラリ規模によってかなり時間がかかる
- 更新後の DB は、従来版 BeMusicSeeker や本フォーク版 `v1.2.1.0` 以前と互換性がない可能性がある
- 互換性に不安がある場合は、事前バックアップを推奨する

## 主な対象コード

- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/Views/MainWindow.xaml.cs`
- `BeMusicSeeker/Models/BMSPlaylist.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs`
- 必要なら新規
  - `BeMusicSeeker/Models/BmsLibraryInternal/BmsonMigrationPreflightService.cs`

## 実装タスク

1. `playlist_entry.sha256` と Phase 2 の `chart_digest_map` を対象にした preflight 判定を定義する
2. 起動時のどのタイミングで警告を出すか決める
3. migration 実行前に必ず警告を挟めるよう、暗黙 migration 経路を整理する
4. 警告ダイアログ文面を実装する
5. 同意時だけ初期化を続行し、キャンセル時は何もせず終了する
6. 「警告済みかつ続行許可済み」の一時状態を起動セッション中に保持する

## 設計メモ

- Phase 1 で `playlist_entry.sha256` migration はすでに実装済みなので、このフェーズでは「黙って走る migration」を preflight 配下へ移す整理が必要
- 将来の `chart_digest_map` 作成と初回バックフィルも同じ警告導線に載せる
- 警告は一度だけ出せばよいが、「未移行のまま再起動した場合」は再表示する
- バージョンは全フェーズ完了まで変更しない
  - `bmson` 対応完了後は `v2.0.0.0` を想定するが、このフェーズでは計画に含めない

## テスト追加方針

- preflight 判定単体テスト
  - `playlist_entry.sha256` 未適用を検出できる
  - `chart_digest_map` 未作成を検出できる
- 起動導線テスト
  - 警告必要時にダイアログ表示へ進む
  - キャンセル時に migration を走らせず終了する
  - 同意時にだけ通常初期化へ進む

## 完了条件

- 移行が必要な環境では、バックフィルや schema 変更の前に必ず警告ダイアログが出る
- キャンセル時は DB 変更を行わず終了する
- 同意時のみ migration / backfill 開始が許可される
- 従来版 BeMusicSeeker と本フォーク版 `v1.2.1.0` 以前との互換性警告が表示される

## 実装確認メモ

- 完了
  - `BmsonMigrationPreflightService` で `playlist_entry.sha256` 未適用を検出できる
  - 起動時に preflight が migration 前に実行される
  - キャンセル時は schema 変更をせず `Shutdown()` で終了する
  - 同意後のみ `BMSPlaylist.EnsureSchema()` が呼ばれる
  - 警告文に `SHA-256`、互換性、`v1.2.1.0`、バックアップ推奨が含まれる
- Phase 2 反映後の補足
  - preflight 対象は `chart_digest_map` 未作成および初回バックフィル未完了にも拡張済み
  - 警告導線は Phase 2 の初回バックフィルにもそのまま使えている

## リスク

- 既存の暗黙 migration 経路を残すと、警告前に DB が更新されてしまう
- 起動フローに組み込む位置を誤ると、初期化途中で UI が不整合になる
- 文面が弱いと互換性破壊の注意喚起として不十分になる
