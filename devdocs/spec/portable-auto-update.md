# ポータブル自動アップデート仕様

## 概要

BeMusicSeeker はインストーラを使わず、zip 配布のポータブル運用を維持したまま更新する。

更新確認は GitHub raw の `update.json` を正とし、ユーザーが選択した GitHub Release asset をダウンロードして SHA-256 とサイズを検証する。更新適用は実行中の `BeMusicSeeker.exe` ではなく、配布 zip に同梱された `BeMusicSeeker.Updater.exe` を `update_work/current/` にコピーしてから別プロセスで行う。

## Manifest

正本 URL:

```text
https://raw.githubusercontent.com/Neeted/bemusicseeker-unofficial-fork/main/update.json
```

`update.json` は schema version 1 のみ対応する。

必須フィールド:

- `schemaVersion`: `1`
- `version`: 更新先バージョン
- `releaseTag`: `v{version}`
- `releasePageUrl`: GitHub Release ページ URL
- `packageFormatVersion`: `1`
- `minimumUpdaterVersion`: updater protocol version。現在は `1`
- `assets`: 1 件以上。`kind = app` は必須

asset 種別:

- `app`: 本体のみ
- `app-with-metadata`: `chart-info-metadata.7z` 同梱版

各 asset は `fileName`, `url`, `sha256`, `sizeBytes`, `includesChartInfoMetadata` を持つ。
`label` は manifest 上では英語の識別用文字列とし、UI 表示名と説明文はアプリ側の多言語リソースで `kind` から決める。

`--update-manifest-url=` を指定するとローカル検証用に manifest URL を差し替えられる。この override では失敗を fallback せず、そのまま失敗として扱う。
`scripts/prepare-local-update-test.ps1 -StartServer` は通常版と metadata 同梱版の zip が `dist/` にある場合、2 asset を含むローカル `update.json` を生成して HTTP 配信する。

## 更新確認

起動時に `UpdateCheckService` が非同期で `update.json` を取得する。現在バージョンより新しい場合、更新ダイアログを表示する。

`update.json` が通信失敗または timeout で取得できない場合のみ、互換用に `version.txt` を参照する。`update.json` の JSON 不正、schema 不一致、asset 検証失敗、未対応 updater version は fallback しない。

`version.txt` fallback では自動適用に必要な asset 情報がないため、従来通り手動確認の通知だけを行う。

## 更新ダイアログ

`update.json` に asset がある場合、通常版と metadata 同梱版を選択できる。

更新適用ボタンは `MainWindowViewModel.IsStartupProgressActive` が `false` のときのみ有効になる。これは起動初期化の進捗ゲージが消えた後に更新適用へ進ませるためで、永続設定としては保持しない。

更新適用ボタンを押した場合も Release ページを開く。これにより、自動更新開始前にユーザーが配布ページやリリースノートを確認できる。

## ダウンロードと検証

選択された asset は `update_work/downloads/` に一時ダウンロードする。

検証内容:

- ダウンロード後のサイズが `sizeBytes` と一致すること
- SHA-256 が `sha256` と一致すること

検証後、アプリは packaged updater を `update_work/current/BeMusicSeeker.Updater.exe` にコピーして起動し、自身を終了する。

## Updater

updater protocol version は `1`。`BeMusicSeeker.Updater.exe --version` で確認できる。

開発時の Release build では、`BeMusicSeeker.csproj` が `BeMusicSeeker.Updater` を build dependency として扱い、`BeMusicSeeker.Updater.exe` を `bin/Release/net472/` へコピーする。これにより、`dotnet build BeMusicSeeker-decomp.sln -c Release` 後の app output はローカル自動更新検証に必要な updater を含む。

updater 引数:

- `--app-dir`: アプリ本体ディレクトリ
- `--package`: 検証済み zip
- `--backup-dir`: `update_backup`
- `--pid`: 終了待ち対象の BeMusicSeeker process id
- `--restart-exe`: 更新後に起動する exe

updater は `--pid` の終了を最大 60 秒待つ。

## ファイル保持ポリシー

常に保持する top-level:

- `config/`
- `data/`
- `logs/`
- `update_backup/`
- `update_work/`

`imported_metadata/` は保持対象ではない。

配布 zip は `update-managed-files.txt` を同梱する。この manifest に含まれるファイルだけをアプリ管理ファイルとして扱う。更新時は、新パッケージの管理ファイルで既存管理ファイルを置換し、新パッケージから消えた管理ファイルを削除する。

初回更新元に `update-managed-files.txt` がない場合は、新パッケージと同じ相対パスに既に存在するファイルだけを置換対象として扱う。旧パッケージから新パッケージで削除されたファイルの掃除より、ユーザー追加ファイルを消さないことを優先する。

## Backup と rollback

更新前の管理ファイルは `update_backup/previous/` に退避する。保持数は 1 世代。

更新中に失敗した場合は、今回の package path だけを削除して `update_backup/previous/` から復元する。ユーザー追加ファイルや保持対象ディレクトリは rollback でも触らない。

ダウンロード済み zip は更新成功後に削除する。

## Release 運用

`scripts/publish.ps1` は以下を行う。

- `BeMusicSeeker.exe` と `BeMusicSeeker.Updater.exe` を Release build
- 通常版 zip を作成
- `-IncludeMetadata` 指定時に `chart-info-metadata.7z` 同梱版 zip を作成
- `update-managed-files.txt` と `dist/update-v{version}.json` を生成
- 公開リポジトリへ同期する際、同一 version の古い zip を削除して stale asset 混入を避ける

`scripts/release.ps1 -CreateDraft` は以下を行う。

- 公開リポジトリの `version.txt` と `dist/` zip から `update.json` を生成
- release commit と tag を作成
- tag のみ push
- release notes の `release notes/v{version} リリースノート.md` を使って GitHub Release draft を作成または更新
- 公開ブランチは push しない

`scripts/release.ps1 -PublishDraft` は以下を行う。

- `main` ブランチ上で実行されていることを確認
- remote tag と local tag の commit 一致を確認
- GitHub Release asset の名前と size が `update.json` 作成元の local zip と一致することを確認
- release commit を `origin/main` へ push
- remote `main` が release commit を指すことを確認
- draft Release を publish

この順序により、Release 公開後に raw GitHub の `update.json` が未公開になる状態を避ける。

既存 draft を更新する場合、今回の local zip に存在しない余剰 Release asset は削除してから asset を再アップロードする。これにより、古い metadata 同梱 zip などが draft に残ったまま publish されることを避ける。
