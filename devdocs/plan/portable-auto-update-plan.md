# ポータブル自動アップデート実装計画

## Summary

BeMusicSeeker をインストーラ形式へ移行せず、現在の zip 配布とポータブル運用を維持したまま、自動アップデートを追加する。

外付け SSD などに BMS ファイルとアプリ本体を一緒に置く運用を支えるため、更新処理は OS へのインストールではなく、アプリ本体ディレクトリの再構成として扱う。`config/` と `data/` はユーザーデータとして保持し、それ以外の本体ファイルは新しいリリースパッケージの内容を正とする。

リリースパッケージは通常版と `chart-info-metadata.7z` 同梱版の 2 種類を想定し、更新通知ではユーザーがどちらを適用するか選べるようにする。

## Goals

- インストーラを使わず、現在の portable zip 配布を維持する。
- GitHub Release asset から通常版 / metadata 同梱版 zip を選んで更新できるようにする。
- `config/` と `data/` を保持し、設定とスタンドアロン DB を失わない。
- 古い DLL や削除済みファイルが残らないよう、新パッケージ基準で本体ファイルを同期する。
- ダウンロードした zip の SHA-256 とサイズを検証する。
- 更新適用は別プロセスで行い、実行中の `BeMusicSeeker.exe` を自己上書きしない。
- 失敗時は更新失敗を隠さず、旧本体へ戻せるようにする。
- `version.txt` だけに依存せず、更新対象 asset と検証情報を含む manifest を使う。

## Non-Goals

- MSI / ClickOnce / MSIX / Velopack などのインストーラ型配布へ移行しない。
- 差分更新は初回実装では行わない。常にフル zip を適用する。
- バックグラウンドで無確認更新しない。初回実装ではユーザー確認後に適用する。
- `config/` や `data/` の schema migration を updater が直接行わない。従来通り新バージョン起動時のアプリ本体処理に任せる。
- `imported_metadata/` は保持対象にしない。metadata bundle はリリースパッケージの一部として扱い、通常の本体同期で消えてよい。
- 更新中に BMS ファイルや LR2 / beatoraja 側のファイルへ触れない。

## Current State

- 起動時更新通知は `MainWindow.CheckForUpdatesAsync()` が `UpdateCheckService` 経由で GitHub raw の `update.json` を取得し、`AssemblyInformationalVersion` と比較する。
- `scripts/publish.ps1` は `AssemblyInformationalVersion` から zip 名を作り、`BeMusicSeeker.exe`、`BeMusicSeeker.exe.config`、`libs/`、`native/`、`lang/`、docs などを staging して zip 化する。
- `scripts/release.ps1` は開発リポジトリの `AssemblyInformationalVersion` から tag を作り、公開リポジトリ側の `dist/bemusicseeker-unofficial-fork-vX.X.X.X*.zip` を GitHub Release asset に添付する。
- 設定保存は独自 `PortableSettingsProvider` で、保存先は exe と同階層の `config/user.config`。
- スタンドアロン DB は exe と同階層の `data/song.db`。
- `config/` と `data/` はリリース zip に含めない。
- metadata 同梱版では zip root に `chart-info-metadata.7z` または `chart-info-metadata.db` を置く想定がある。

## Update Manifest

自動アップデートの正本は `update.json` のみとする。公開リポジトリの `version.txt` は `update.json` 導入以前のクライアントへ通知するために残してよいが、現行アプリは参照しない。

公開先:

```text
https://raw.githubusercontent.com/Neeted/bemusicseeker-unofficial-fork/main/update.json
```

`update.json` は raw GitHub のみを正とする。GitHub Pages 側には置かない。

manifest 例:

```json
{
  "schemaVersion": 1,
  "version": "2.1.0.0",
  "releaseTag": "v2.1.0.0",
  "releasePageUrl": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/tag/v2.1.0.0",
  "packageFormatVersion": 1,
  "publishedAt": "2026-06-22T00:00:00Z",
  "minimumUpdaterVersion": "1",
  "assets": [
    {
      "kind": "app",
      "label": "本体のみ",
      "fileName": "bemusicseeker-unofficial-fork-v2.1.0.0.zip",
      "url": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.0.0/bemusicseeker-unofficial-fork-v2.1.0.0.zip",
      "sha256": "...",
      "sizeBytes": 12345678,
      "includesChartInfoMetadata": false
    },
    {
      "kind": "app-with-metadata",
      "label": "譜面解析済みメタデータ同梱版",
      "fileName": "bemusicseeker-unofficial-fork-v2.1.0.0-with-metadata.zip",
      "url": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.0.0/bemusicseeker-unofficial-fork-v2.1.0.0-with-metadata.zip",
      "sha256": "...",
      "sizeBytes": 234567890,
      "includesChartInfoMetadata": true
    }
  ]
}
```

### Manifest Policy

- `schemaVersion` が未対応の場合は自動更新を中止し、ブラウザで release page を開く選択肢を出す。
- `packageFormatVersion` が未対応の場合も自動適用しない。
- `minimumUpdaterVersion` が現在の updater protocol version より大きい場合は自動適用しない。
- `version` は `System.Version` として parse できること。
- `releaseTag` は `v{version}` と一致すること。
- `releasePageUrl` は `https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/tag/{releaseTag}` と一致すること。
- `assets` は 1 件以上であること。
- asset `kind` は `app` または `app-with-metadata` のみ許可する。
- asset `kind` は重複不可。
- `app` asset は必須、`app-with-metadata` は任意。
- asset は `kind` で識別し、表示文言は `label` を使う。
- `sha256` は必須にする。空または不一致の場合は更新失敗にする。
- `sha256` は 64 桁の hex string のみ許可する。
- `sizeBytes` は必須にし、ダウンロード前後の sanity check に使う。
- `fileName` は空不可、path separator を含めない。
- URL は GitHub Release asset の固定 URL を基本とし、`https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/{releaseTag}/{fileName}` と一致すること。GitHub API への依存は必須にしない。
- `includesChartInfoMetadata=true` の asset は `kind=app-with-metadata` と一致すること。
- production release の metadata 同梱版は `.7z` bundle を含む。`.db` bundle は開発・手動 package 用には許容できるが、自動更新対象の public release では使わない。

validator 候補:

- `UpdateManifestValidator`
- `UpdateManifestValidationResult`
- `UpdateManifestValidationError`

## Release Operation

自動アップデートでは、manifest 公開の瞬間がユーザーに更新可能と見える瞬間になる。そのため、`update.json` は release asset が揃ってから最後に公開する。

`version.txt` は `update.json` 導入以前のクライアント向け更新シグナルとして公開リポジトリに残すため、draft release の確認中に新バージョンの `version.txt` を public default branch へ push しない。公開ユーザーへ更新を見せる操作は、GitHub Release draft を publish して release asset を公開し、その後に release commit を public default branch へ push して raw GitHub の `update.json` と `version.txt` を更新する段階へ集約する。

### Tag And Commit Boundary

draft release 用の tag は、新バージョンの公開ファイルを含む release commit を指す。ただし、その commit は draft 確認中に public default branch へ push しない。

方針:

1. 公開リポジトリの作業ツリーへ package、docs、生成済み `version.txt`、candidate `update.json` を同期する。
2. ローカルで `Release vX.X.X.X` commit を作る。
3. ローカルで `vX.X.X.X` tag を作る。
4. tag だけを push し、draft release をその tag から作る。
5. draft 確認中は default branch を push しないため、raw GitHub の `version.txt` / `update.json` は旧版のまま残る。
6. release を公開してよいと判断したら、draft release を publish する。
7. 同じ release commit を default branch へ push する。
8. raw GitHub に新しい `update.json` / `version.txt` が出ることを確認する。

この手順なら draft release の asset URL は先に確定し、かつ旧クライアントが draft 確認中に `version.txt` へ反応しない。

`gh release create` は tag が存在しない場合に default branch から tag を自動作成できるが、この運用では意図しない commit に紐づく危険があるため使わない。release script は remote tag の存在確認後、`gh release create --verify-tag --draft` を使う。

推奨手順:

1. `AssemblyInformationalVersion` を新バージョンへ更新する。
2. `scripts/publish.ps1 -IncludeMetadata` で通常版と metadata 同梱版 zip を作る。
3. 各 zip の SHA-256 とサイズを計算する。
4. `release notes/vX.X.X.X リリースノート.md` を release body として GitHub Release draft を作成し、両 zip を asset として添付する。
5. draft release の本文、asset、hash / size 候補を確認する。
6. draft release を publish する。
7. release commit を public default branch へ push する。
8. raw GitHub の `update.json` / `version.txt` が新バージョンを返すことを確認する。

asset URL は tag と file name から決定できるため、通常は release 作成後に API で URL を取得しなくてもよい。

```text
https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/vX.X.X.X/<fileName>
```

### Release Notes

次バージョンのリリースノート Markdown を GitHub Release の本文として使う。

既定パス:

```text
release notes/vX.X.X.X リリースノート.md
```

`gh release create` / `gh release edit` は `--notes-file` を使えるため、draft 作成時に本文を入れられる。

例:

```powershell
gh release create $tag @assetPaths --title $tag --notes-file $releaseNotesPath --verify-tag --draft
```

既存 draft / release の本文を更新する場合:

```powershell
gh release edit $tag --title $tag --notes-file $releaseNotesPath
```

リリースノートが見つからない場合は release script を失敗させる。`--generate-notes` は使わず、手書きのリリースノートを正とする。

### Script Changes

`scripts/publish.ps1`:

- package 作成後に zip の SHA-256 と size を返す、または sidecar manifest 用 object を生成する。
- `-IncludeMetadata` 指定時は通常版と metadata 同梱版の両方を manifest 候補に含める。
- `update.json` は GitHub Release の asset が公開され、公開判断後に raw GitHub へ出す必要があるため、publish 単独では candidate json を `dist/update-vX.X.X.X.json` に出す程度に留める。
- 旧クライアント向けの公開用 `version.txt` は、開発リポジトリの `version.txt` をコピーせず `AssemblyInformationalVersion` から生成する。
- `publish.ps1` は公開リポジトリ作業ツリーへファイルを同期してよいが、draft 確認中に `version.txt` / `update.json` が public raw へ出ないよう、commit / push は release script の明示段階で行う。

`scripts/release.ps1`:

- `-CreateDraft` 相当の段階では、GitHub Release draft を作成または更新し、asset を `--clobber` upload し、release notes を `--notes-file` で反映する。
- draft 作成段階では release commit と tag を作り、tag だけ push する。public default branch は push しない。
- `-PublishDraft` 相当の段階では、draft release を publish して release asset を公開した後、release commit を public default branch へ push して raw `update.json` / `version.txt` を公開する。
- 既存 release に `--clobber` で asset を再アップロードした場合も、candidate manifest を再生成する。
- 途中で失敗した場合、古い public `update.json` を更新しない。
- version / tag は開発リポジトリの `AssemblyInformationalVersion` から取得する。
- 既定の release notes path は `$devRoot\release notes\v$version リリースノート.md` とする。
- `gh release create` は `--verify-tag --draft --notes-file` を使い、tag 自動作成に任せない。

`-CreateDraft` の precondition:

- 公開リポジトリ working tree に release 対象の同期済みファイルがある。
- release commit が未作成の場合は作成する。
- local tag は release commit を指す。
- remote tag が存在する場合も release commit と同一でなければ失敗する。
- public default branch への push は行わない。
- draft release が既にある場合は、release notes、asset、candidate manifest を再生成して更新する。

`-PublishDraft` の precondition:

- GitHub Release が存在する。
- remote tag、local tag、現在の HEAD が同じ release commit を指す。
- candidate `update.json` の asset name / size が現在の release asset と一致する。
- GitHub Release が draft 状態なら publish する。既に公開済みの場合は raw 反映の再試行として扱う。
- 同じ release commit を public default branch へ push する。
- push 後に raw GitHub の `update.json` / `version.txt` が新バージョンを返すことを確認する。

draft 作成と公開シグナル反映を分けるため、release script は次のような入口に整理する。

```powershell
scripts\release.ps1 -CreateDraft
scripts\release.ps1 -PublishDraft
```

`-CreateDraft` は人間が GitHub UI で draft を確認するための操作で、`-PublishDraft` はユーザーへ更新通知を出してよいと判断した後の操作にする。

## App UI

既存の起動時通知を `update.json` ベースに置き換える。

表示候補:

- 最新版のバージョン
- 現在のバージョン
- release page へのリンク
- 通常版 zip のサイズ
- metadata 同梱版 zip のサイズ
- `本体のみを更新`
- `譜面解析済みメタデータ同梱版で更新`
- `ブラウザで開く`
- `今回は閉じる`

初回実装では設定項目を増やしすぎない。自動確認 ON/OFF は既存通知設定との統合を検討し、後から「このバージョンをスキップ」などの状態保存を追加する場合は別途設計する。

metadata 同梱版の説明は短くする。

- 本体のみ: ダウンロードが小さい。既存 DB と通常解析を使う。
- metadata 同梱版: ダウンロードが大きい。起動後に同梱 metadata を import できる。

### Startup Apply Gate

更新確認と通知表示は起動後すぐ非同期で開始してよい。

ただし、起動初期化中にアプリを閉じると危険なため、更新適用による終了 / 再起動は安全に閉じられる状態になるまで許可しない。

`CanApplyUpdateNow` は、ステータスバーの起動・リロード進捗ゲージが消えるタイミングと揃える。実装上は `MainWindowViewModel.IsStartupProgressActive == false` を基本条件にする。

この境界は `StartupReadyOperable` ではない。`StartupReadyOperable` 到達時点では `IsStartupUiInteractionBlocked` が false になり通常操作は可能になるが、`StartupBackgroundTasksDone` などの expected background phase が残っている場合は進捗ゲージが `操作可能(バックグラウンド更新中)` として継続する。更新適用は process exit を伴うため、通常操作可能化より強く、進捗 operation 全体が完了してゲージが非表示になってから許可する。

現行実装では、全 expected phase 完了後に完了ラベルを表示し、`ScheduleStartupProgressHide()` が約 2 秒後に progress state を clear して `IsStartupProgressActive=false` を反映する。ユーザーが見る「ゲージが消えるタイミング」はこの反映後である。

初回実装方針:

- `update.json` の確認と「更新があります」の表示は早期に行う。
- `IsStartupProgressActive=true` の間は `今すぐ更新して再起動` を disabled にする、または `起動完了後に適用` として予約する。
- `IsStartupProgressActive=false` になった後に `CanApplyUpdateNow` 相当の状態を true にし、更新適用ボタンを有効化する。
- ダウンロードだけ先に許可するかは UI 実装時に決める。閉じる処理だけは必ず gate する。
- `IsStartupUiInteractionBlocked=false` は通常操作可能の境界として扱い、更新適用可能の境界には使わない。
- `IsLibraryOperationInProgress` は `_IsStartupUiInteractionBlocked || startupProgressState.IsActive` 相当なので広い gate としては使えるが、UI の進捗ゲージ消滅と一致させる主条件は `IsStartupProgressActive=false` にする。
- `ReloadFileDiff` / `ScoreOnly` / `FullReinitialize` / `ReloadTables` など起動後 operation 中も `IsStartupProgressActive=true` になるため、同じ gate で更新適用を止める。
- 起動失敗やリロード失敗で progress state が failed 表示のまま残る場合は、自動適用は許可せず、release page を開く手動導線だけを残す。

## Update Apply Model

実行中 exe は直接置き換えられないため、適用処理は別プロセスに分離する。

候補:

- `BeMusicSeeker.Updater.exe`
- 本体プロジェクトとは別の小さな `net472` console / windows exe

採用する構成:

- project: `BeMusicSeeker.Updater/BeMusicSeeker.Updater.csproj`
- output: `BeMusicSeeker.Updater.exe`
- target framework: `net472`
- solution: `BeMusicSeeker.sln` に追加する。
- package location: release zip root に `BeMusicSeeker.Updater.exe` を同梱する。
- publish: `scripts/publish.ps1` が build output から updater exe を staging root へコピーする。
- protocol version: updater 側に `UpdaterProtocolVersion = 1` を持たせ、manifest `minimumUpdaterVersion` と比較する。

updater は app dir から直接実行しない。アプリ本体は packaged updater を updater 作業ディレクトリへコピーし、そのコピーを起動する。これにより、本体ディレクトリ同期時に実行中 updater 自身が退避 / 削除対象にならない。

作業ディレクトリ候補:

```text
<app>/update_work/current/
```

アプリ本体の責務:

- `update.json` の取得と検証。
- `update.json` が取得不能または invalid の場合は更新チェック失敗として扱い、別形式への fallback は行わない。
- command-line switch `--update-manifest-url` がある場合は、その URI を manifest source として使う。未指定時だけ raw GitHub を使う。
- ユーザー選択。
- zip のダウンロード。
- SHA-256 / size 検証。
- updater 作業ディレクトリへの展開または zip path 引き渡し。
- `Settings.Default.Save()` を明示的に呼んでから終了する。
- packaged updater を `update_work/current/` へコピーし、そのコピーを起動する。

updater の責務:

- 対象 process の終了待ち。
- zip の再検証。
- 一時ディレクトリへの展開。
- package structure の検証。
- backup 作成。
- 本体ディレクトリ同期。
- 新バージョン起動。
- 失敗時 rollback。
- 更新ログ出力。

## Directory Policy

保持対象:

- `config/`
- `data/`
- `logs/`
- `update_backup/`
- updater 作業ディレクトリ `update_work/`
- 更新ログ

保持しない対象:

- `imported_metadata/`
- 旧 package 由来の `libs/`
- 旧 package 由来の `native/`
- 旧 package 由来の `lang/`
- 旧 package 由来の docs / third_party / root files
- `chart-info-metadata.7z` / `chart-info-metadata.db`

`imported_metadata/` は保持しない。metadata 同梱版を適用した場合、新 zip root に含まれる `chart-info-metadata.7z` が配置され、次回起動時の importer が必要に応じて処理する。通常版を適用した場合は metadata bundle は配置されない。

### Sync Policy

単純上書きではなく、新パッケージの内容を正とする。

1. zip を一時展開する。
2. 展開 root に `BeMusicSeeker.exe` と `BeMusicSeeker.exe.config` があることを確認する。
3. 展開 root に `config/` または `data/` が含まれている場合は package error とする。
4. 現在の本体ディレクトリから保持対象以外を backup へ退避する。`update_backup/` と `update_work/` は本体同期対象から除外する。
5. 新パッケージの全ファイル / ディレクトリを本体ディレクトリへコピーする。
6. 新 `BeMusicSeeker.exe` の version が manifest version と一致することを確認する。

削除対象を個別列挙して消すのではなく、backup へ退避してから新規配置する。これにより、旧 DLL が残る問題を避ける。

### File Operation Guard

updater は破壊的な filesystem 操作を行うため、path 判定は文字列 prefix ではなく正規化済み absolute path で行う。

- app dir、extract dir、backup dir、work dir は `Path.GetFullPath` で正規化する。
- package entry の path traversal、absolute path、drive rooted path、空 path を拒否する。
- package entry の正規化後 path が extract root 配下に収まらない場合は拒否する。
- app dir 配下の reparse point / junction / symlink は追跡しない。保持対象でも本体同期対象でも、reparse point を見つけた場合は更新を中止して明示エラーにする。
- path 比較は Windows 前提で case-insensitive とする。
- root 直下の保持対象 directory は再帰的に保持する。
- backup / restore は保持対象以外を再帰的に扱う。
- read-only file に遭遇した場合は属性を勝手に変更せず、更新失敗として rollback または手動復旧案内に回す。
- package 内に `config/`、`data/`、`update_backup/`、`update_work/`、`logs/` が含まれる場合は package error とする。
- app dir 外の BMS ファイル、LR2 / beatoraja ファイルへは触れない。

## Backup And Rollback

backup 先:

```text
<app>/update_backup/previous/
```

backup 対象は保持対象以外の旧本体ファイル。

保持する backup は 1 つだけにする。ただし、新しい backup が完成する前に既存 backup を消すと rollback 不能になるため、次の順序にする。

1. 新 backup を updater 作業ディレクトリ内に作成する。
2. 本体同期と新 exe 検証が成功したら、既存 `update_backup/previous/` を削除する。
3. 新 backup を `update_backup/previous/` へ移動する。

更新成功後に rollback 用 backup を 1 世代だけ残す。2 世代以上の履歴や削除 UI は作らない。

rollback 条件:

- zip 展開に失敗した。
- package structure 検証に失敗した。
- 旧本体退避後、新本体コピーに失敗した。
- 新 exe の version 検証に失敗した。
- 新 exe 起動に失敗した。

rollback は backup を元の本体ディレクトリへ戻す。rollback 自体が失敗した場合は、更新ログに明示し、ユーザーが手動復旧できるよう backup path を残す。

## Download Cleanup

ダウンロード済み zip は更新成功後に削除する。

方針:

- zip は updater 作業ディレクトリに置く。
- SHA-256 / size 検証に失敗した zip は invalid として削除する。
- 更新成功後は zip と展開一時ディレクトリを削除する。
- 実行中 updater copy は自分自身で削除しない。
- 新バージョンの app 起動時に、前回成功済みの `update_work/` を cleanup する。
- 更新失敗時は原因調査のため updater 作業ディレクトリを残してよい。

## Logging

更新ログ候補:

```text
<app>/logs/update.log
```

または既存ログ方針に合わせる。

最低限出す内容:

- update check start / done / failed
- selected asset kind / version / size
- download start / done / failed
- sha256 expected / actual
- updater started
- wait process exit
- package validation result
- backup path
- sync start / done / failed
- rollback start / done / failed
- launch new app result

ユーザーに見える失敗では、詳細ログ path を表示する。更新失敗を通常起動成功に見せかけない。

## Implementation Progress Checklist

このセクションは引き継ぎ用の進捗正本として使う。実装に着手したら、完了した項目だけ `- [x]` に更新する。

チェックを入れる条件:

- 対応する code / script / document の変更が入っている。
- その Unit の「検証」に書いた最低限の確認が完了している。
- 未確認の項目がある場合はチェックせず、該当 Unit の下に短い NOTE を残す。

### Unit Progress

- [ ] Unit 1: Manifest と Release Script
  - [ ] `update.json` schema / generator がある。
  - [ ] `publish.ps1` が package hash / size を出力または candidate manifest 化できる。
  - [ ] `publish.ps1` が `BeMusicSeeker.Updater.exe` を release zip root へ同梱する。
  - [ ] `release.ps1 -CreateDraft` が release commit / tag / draft release / release notes / assets を処理できる。
  - [ ] `release.ps1 -CreateDraft` が branch push を行わず、tag だけ push する。
  - [ ] `release.ps1 -PublishDraft` が draft publish、public default branch push、raw 反映確認を処理できる。
  - [ ] `release.ps1 -PublishDraft` が remote tag commit、現在の HEAD と tag の一致、release asset name / size を検証する。
  - [ ] draft 確認中に public raw の `update.json` / `version.txt` が進まないことを確認済み。
- [ ] Unit 2: Update Check Read Model
  - [ ] `UpdateManifest` / `UpdateAsset` model がある。
  - [ ] `UpdateManifestValidator` が manifest policy の拒否条件を実装している。
  - [ ] `update.json` の取得、parse、validate、version 比較がある。
  - [ ] `CommandLineSwitches.UpdateManifestUrl` がある。
  - [ ] unsupported schema / package format で自動適用しない。
  - [ ] `minimumUpdaterVersion` が現在の updater protocol version より大きい場合は自動適用しない。
  - [ ] 更新チェックは `update.json` 専用で、別形式への fallback がない。
- [ ] Unit 3: Update Notification UI
  - [ ] 通常版 / metadata 同梱版を選べる更新通知 UI がある。
  - [ ] release page を開ける。
  - [ ] update dialog view model の `CanApplyUpdateNow` が `!IsStartupProgressActive` に連動する。
  - [ ] progress failed / reload 中は自動適用しない。
- [ ] Unit 4: Download And Verify
  - [ ] asset download がある。
  - [ ] `sizeBytes` と SHA-256 の検証がある。
  - [ ] 検証失敗時に updater を起動しない。
  - [ ] 更新成功後に zip が残らない。
- [ ] Unit 5: Updater Process
  - [ ] `BeMusicSeeker.Updater/BeMusicSeeker.Updater.csproj` が solution に追加されている。
  - [ ] updater exe がある。
  - [ ] updater protocol version がある。
  - [ ] updater copy を `update_work/current/` から実行する。
  - [ ] package structure validation がある。
  - [ ] path traversal / absolute path / reparse point / read-only file を安全に扱う。
  - [ ] `config/` と `data/` を保持し、`imported_metadata/` は保持しない。
  - [ ] `logs/` を保持する。
  - [ ] `update_backup/previous/` 1 世代 rollback がある。
  - [ ] 旧 package 由来の不要ファイルが残らない。
- [ ] Unit 6: App Shutdown Integration
  - [ ] 更新適用前に `Settings.Default.Save()` を明示実行する。
  - [ ] 通常 close path と更新 close path が二重実行しない。
  - [ ] single instance mutex が更新後の再起動を邪魔しない。
- [ ] Unit 7: Metadata 同梱版の扱い
  - [ ] metadata 同梱版で `chart-info-metadata.7z` が配置される。
  - [ ] 通常版で旧 metadata bundle / `imported_metadata/` が残らない。
  - [ ] metadata import 成否を updater 成否と混同しない。
- [ ] Unit 8: Local Update Verification
  - [ ] `--update-manifest-url` command-line switch がある。
  - [ ] local `update.json` / local package で E2E 更新確認できる。
  - [ ] hash 不一致、copy 失敗 rollback、startup progress gate を確認できる。

### Handoff Notes

- Current unit:
- Changed files:
- Unwired entry points:
- Unverified checks:
- Manual verification artifacts:
- Existing code to read next:
- Last verified command:
- Last manual verification:
- Known blockers:
- Next recommended step:

## Unit 1: Manifest と Release Script

- `update.json` schema を決める。
- `scripts/publish.ps1` が zip の hash / size を出力できるようにする。
- `BeMusicSeeker.Updater/BeMusicSeeker.Updater.csproj` を solution に追加し、release package root に `BeMusicSeeker.Updater.exe` を同梱できるようにする。
- updater protocol version は初期値 `1` とし、manifest `minimumUpdaterVersion` と比較できるようにする。
- `scripts/release.ps1 -CreateDraft` が release notes file を本文にした GitHub Release draft を作成 / 更新できるようにする。
- `scripts/release.ps1 -CreateDraft` が通常版 / metadata 同梱版 asset を draft release へ添付できるようにする。
- `scripts/release.ps1 -CreateDraft` は release commit と tag を作り、tag だけ push する。
- `scripts/release.ps1 -PublishDraft` が draft release を publish し、release commit を public default branch へ push して `update.json` と `version.txt` を public raw へ反映できるようにする。
- `version.txt` は `AssemblyInformationalVersion` から生成される旧クライアント向け公開物として扱い、開発リポジトリ側の正本にはしない。
- `update.json` は GitHub Release asset が公開され、公開判断後に raw GitHub に出す。
- 現行アプリは `version.txt` fallback を持たず、更新チェックは `update.json` 専用にする。

検証:

- 通常版のみの release で manifest が作れる。
- metadata 同梱版ありの release で assets が 2 件になる。
- release notes Markdown が draft release 本文に入る。
- asset upload 失敗時に `update.json` が更新されない。
- draft 確認中に raw GitHub の `update.json` / `version.txt` が新バージョンへ進まない。
- draft release が release commit の tag に紐づく。
- release zip root に `BeMusicSeeker.Updater.exe` が含まれる。

## Unit 2: Update Check Read Model

- `UpdateManifest` / `UpdateAsset` model を追加する。
- `UpdateManifestValidator` を追加し、manifest policy の受け入れ / 拒否条件を一箇所に集約する。
- `update.json` を取得して parse / validate する service を追加する。
- command-line switch は `CommandLineSwitches.UpdateManifestUrl` として追加し、`UpdateCheckService` の manifest source URI に渡す。
- 既存の `MainWindow.CheckForUpdatesAsync()` は `UpdateCheckService` 呼び出しへ移し、MainWindow から HTTP / JSON validation の詳細を外す。
- 現在の `AssemblyInformationalVersion` と manifest `version` を比較する。
- 未対応 schema / package format では自動適用を出さず、release page を開けるようにする。
- `update.json` が取得不能 / invalid の場合は更新チェック失敗として扱い、失敗を隠す fallback は行わない。

検証:

- 新バージョン manifest で更新ありになる。
- 同一 / 古い manifest で更新なしになる。
- asset hash 欠落、URL 欠落、size 欠落で invalid になる。
- duplicate asset kind、空 assets、`releaseTag` / `version` 不一致、`minimumUpdaterVersion` 超過で invalid になる。
- malformed JSON で更新失敗ログになる。

## Unit 3: Update Notification UI

- 更新通知 dialog を追加する。
- 通常版 / metadata 同梱版を選択できるようにする。
- asset が 1 件しかない場合は選べる項目を 1 件にする。
- release page をブラウザで開けるようにする。
- 初回実装では自動ダウンロード開始は行わず、ユーザー選択後に開始する。
- `IsStartupProgressActive=true` の間は更新適用による終了 / 再起動を gate する。
- `CanApplyUpdateNow` は update dialog view model 側の derived state とする。`MainWindowViewModel` には永続的な更新用 state を増やさず、dialog view model が owner `MainWindowViewModel.IsStartupProgressActive` の property change を購読して `!IsStartupProgressActive` を反映する。
- 起動完了待ちの予約が必要な場合は update dialog view model 側に `IsUpdateApplyReserved` を持たせ、`CanApplyUpdateNow` が true になった時点でユーザー確認または適用開始へ進む。

検証:

- 通常版と metadata 同梱版の両方が表示される。
- metadata 同梱版が manifest にない場合でも UI が破綻しない。
- dialog を閉じた場合は何も変更しない。
- `StartupReadyOperable` 到達後でも起動進捗ゲージが残っている間は更新適用を開始できない。
- 起動進捗ゲージが消え、`IsStartupProgressActive=false` になった後に更新適用を開始できる。
- リロード / 再初期化 progress 中は更新適用を開始できない。
- progress failed 表示中は自動適用ではなく手動導線になる。

## Unit 4: Download And Verify

- 選択 asset を一時ディレクトリへダウンロードする。
- `sizeBytes` と SHA-256 を検証する。
- 検証失敗時は updater を起動しない。
- ダウンロード中断 / HTTP error / timeout をユーザーへ表示し、ログへ残す。
- 更新成功後にダウンロード済み zip を削除する。

検証:

- 正しい hash で成功する。
- hash 不一致で失敗する。
- size 不一致で失敗する。
- 途中ファイルが残っても次回更新の妨げにならない。
- 成功後に zip が残らない。

## Unit 5: Updater Process

- 小さな updater exe を追加する。
- updater project は `BeMusicSeeker.Updater/BeMusicSeeker.Updater.csproj` とし、`BeMusicSeeker.sln` に追加する。
- 引数には少なくとも app directory、zip path、expected version、expected sha256、parent process id を渡す。
- アプリ本体は updater exe を `update_work/current/` へコピーしてから起動する。
- updater 側でも zip hash を再検証する。
- parent process 終了を待つ。
- package structure を検証する。
- File Operation Guard の path / reparse point / read-only file policy を実装する。
- backup を作る。
- 保持対象以外を backup へ退避し、新 package を配置する。
- backup は 1 世代だけ残す。
- 新 app を起動する。
- 失敗時は rollback する。

検証:

- 起動中 app を終了してから更新できる。
- `config/user.config` と `data/song.db` が残る。
- `imported_metadata/` は残らない。
- 旧 `libs` にだけ存在したファイルが更新後に残らない。
- copy 失敗を rollback できる。
- 既存 backup は新 backup が完成するまで消えない。
- 更新成功後の backup は `update_backup/previous/` だけになる。
- updater 自身が本体同期対象にならない。
- 成功後の `update_work/` cleanup は次回 app 起動に任せ、updater が実行中の自分自身を削除しない。

## Unit 6: App Shutdown Integration

- 更新適用前に `Settings.Default.Save()` を明示的に呼ぶ。
- MainWindow close path と競合しないよう、更新終了フラグを持つ。
- updater 起動後にアプリを終了する。
- 多重起動 mutex が updater / 新 app 起動を邪魔しないことを確認する。

検証:

- window size など終了時保存される設定が失われない。
- 更新再起動後に single instance 判定で弾かれない。
- 更新適用中に通常終了処理が二重実行されない。

## Unit 7: Metadata 同梱版の扱い

- metadata 同梱版を選んだ場合、zip root の `chart-info-metadata.7z` が新本体ディレクトリへ配置される。
- 通常版を選んだ場合、`chart-info-metadata.7z` は配置されない。
- 自動更新対象の public release では metadata 同梱 bundle は `.7z` 限定とする。`.db` は開発・手動 package 用の互換として publish script が扱ってもよいが、release manifest の `app-with-metadata` asset 検証では `.7z` 同梱を前提にする。
- `imported_metadata/` は保持対象ではないため、更新時に旧退避済み metadata は消えてよい。
- metadata import の成否は updater では扱わず、新バージョン起動時の既存 importer に任せる。

検証:

- metadata 同梱版適用後、次回起動で importer が bundle を検出できる。
- 通常版適用後、旧 `imported_metadata/` が残らない。
- metadata import 失敗時に updater の成功 / 失敗とは混同されない。

## Unit 8: Local Update Verification

自動アップデートは実機確認が重要なため、ローカルだけで end-to-end 確認できる経路を用意する。

本番の manifest は raw GitHub のみを正とするが、開発確認用に command-line switch の override を持つ。

採用する switch:

- `--update-manifest-url http://127.0.0.1:PORT/update.json`

初回実装では設定画面に保存せず、environment variable override も作らない。テスト / 手動検証時だけ command-line switch で指定する。

ローカル確認スクリプト候補:

```powershell
scripts\Test-PortableUpdate.ps1
```

確認フロー:

1. `artifacts/update-test/old-app/` に自動更新実装済みの N-1 相当 portable app を展開する。現行公開版そのものは updater / manifest override を持たないため、この E2E 対象にはしない。
2. `old-app/config/user.config` と `old-app/data/song.db` に sentinel file / sentinel setting を置く。
3. 旧 package にだけ存在する dummy DLL / dummy file を置く。
4. 新版 package zip と local `update.json` を `artifacts/update-test/server/` に置く。
5. `http://127.0.0.1:<port>/update.json` で local HTTP server を起動する。
6. 旧版 app を `--update-manifest-url` 付きで起動する。
7. 通常版 / metadata 同梱版を選んで更新する。
8. 新版 app が起動することを確認する。
9. `config/` と `data/` が残り、`imported_metadata/` と dummy file が消え、backup が 1 世代だけ残り、download zip が消えることを確認する。

検証:

- 通常版 package で end-to-end 更新できる。
- metadata 同梱版 package で end-to-end 更新できる。
- hash 不一致 manifest で更新が止まる。
- copy 失敗を人工的に起こして rollback できる。
- `StartupReadyOperable` 到達後でも進捗ゲージが残っている間は更新適用できない。
- 進捗ゲージが消えた後に更新適用できる。

## Decisions And Remaining Questions

決定済み:

- `update.json` は raw GitHub のみを正とする。
- 更新確認と通知表示は起動後すぐ非同期に行う。
- 更新適用による終了 / 再起動は `IsStartupProgressActive=false`、つまり起動・リロード進捗ゲージが消えるまで gate する。
- ダウンロード済み zip は更新成功後に削除する。
- backup は 1 世代だけ保持する。
- 更新チェックは `update.json` 専用とし、`update.json` が壊れている場合は更新チェック失敗として扱う。
- draft release 作成時は release commit に tag を打ち、tag だけ push する。公開時は draft release を publish してから public default branch を push し、raw `update.json` / `version.txt` を確認する。
- updater は `update_work/current/` へコピーしたものを実行する。
- local update verification 用 override は command-line switch `--update-manifest-url` のみにする。
- `CanApplyUpdateNow` は update dialog view model 側の derived state とし、`!IsStartupProgressActive` を基本条件にする。

残り:

- 実装中に見つかった未接続箇所は `Handoff Notes` の `Unwired entry points` に記録する。

## Implementation Readiness

この計画は Codex が Unit 1 から順に自走実装できる粒度を目標にする。

自走可能と判断する理由:

- release script、manifest、app read model、UI、download、updater、shutdown、metadata、local verification の作業単位が分かれている。
- 各 Unit に最低限の検証条件がある。
- 破壊的 filesystem 操作の保持対象、除外対象、rollback、path guard が明文化されている。
- release draft と public raw 更新の境界が明文化されている。
- startup progress gate と local manifest override の決定が済んでいる。
- `Implementation Progress Checklist` と `Handoff Notes` により、途中再開時に git diff だけへ依存しない。

実装中の運用:

- 1 turn で全 Unit を完了できない場合は、完了済み checkbox と `Handoff Notes` を更新してから中断する。
- checkbox は code change だけでなく検証完了後に更新する。
- 失敗を隠す fallback は追加しない。自動更新できない場合は手動 release page 導線へ落とす。
