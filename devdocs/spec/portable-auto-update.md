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
- `assets`: 1 件以上。`kind = app` は必須。生成スクリプトと更新ダイアログでは `app` を `app-with-metadata` より先に並べる。

asset 種別:

- `app`: 本体のみ
- `app-with-metadata`: `chart-info-metadata.7z` 同梱版

各 asset は `fileName`, `url`, `sha256`, `sizeBytes`, `includesChartInfoMetadata` を持つ。
`label` は manifest 上では英語の識別用文字列とし、UI 表示名と説明文はアプリ側の多言語リソースで `kind` から決める。

`--update-manifest-url=` を指定するとローカル検証用に manifest URL を差し替えられる。
`scripts/prepare-local-update-test.ps1 -StartServer` は通常版と metadata 同梱版の zip が `dist/` にある場合、2 asset を含むローカル `update.json` を生成して HTTP 配信する。

## 更新確認

起動時に `UpdateCheckService` が非同期で `update.json` を取得する。現在バージョンより新しい場合、更新ダイアログを表示する。

更新確認の正本は `update.json` のみとする。`update.json` の取得失敗、timeout、JSON 不正、schema 不一致、asset 検証失敗、未対応 updater version は更新チェック失敗として扱い、別形式への fallback は行わない。

## 更新ダイアログ

`update.json` に asset がある場合、通常版と metadata 同梱版を選択できる。通常版を先頭に表示し、初期選択も通常版にする。

更新適用ボタンは `ProgressHub.StartupProgress.IsActive` が `false` のときのみ有効になる。これは起動初期化の進捗ゲージが消えた後に更新適用へ進ませるためで、永続設定としては保持しない。

更新適用ボタンを押した場合も Release ページを開く。これにより、自動更新開始前にユーザーが配布ページやリリースノートを確認できる。

## ダウンロードと検証

選択された asset は `update_work/downloads/` に一時ダウンロードする。

検証内容:

- ダウンロード後のサイズが `sizeBytes` と一致すること
- SHA-256 が `sha256` と一致すること

検証後、アプリは packaged updater の self-contained single-file exe だけを `update_work/current/` にコピーして、同じディレクトリから起動する。updater は request を parse できた時点で `current/updater-ready.txt` を公開し、アプリはこの ready handshake を確認してから shutdown preparation を行う。preparation 成功時だけ `current/updater-decision.txt` に `proceed` を公開し、updater はそれを受けてから親プロセス終了待ちと適用を開始する。preparation が長引いてもアプリ process が生存している間は decision 待ちを延長し、`proceed` 前に 60 秒の終了待ちを消費しない。preparation 失敗や中断時は `cancel` を公開し、updater は適用せず終了する。ready 前に updater が終了または起動できなかった場合、アプリは終了せず既存の update failure dialog へ通知する。再起動直後のアプリ初期化は、実行中 updater が保持している `current/` を削除対象にせず、`downloads/` や `extracted/` などの一時領域だけを掃除する。`current/` は次回の updater payload 準備時に上書きする。

updater がアプリ終了後に適用または rollback に失敗した場合は、`update_work/update-failure.txt` を一時ファイルから atomic に公開して失敗内容を記録してから終了する。atomic 移動に失敗しても `update-failure.txt.tmp` を durable fallback として残し、次回起動時に同じ receipt として扱う。startup cleanup は receipt を読み取って一時領域を掃除し、既存の update failure dialog が正常に戻った後で receipt を acknowledge（削除）する。shell 終了などで dialog を抑止した場合は acknowledge を延期して receipt を保持する。これにより updater の stderr だけに失敗を残さず、再起動後のユーザー操作で失敗を観測できる。rollback 自体が失敗した場合は primary failure と rollback failure の両方を receipt に残し、既存の backup、work、journal を保持して次回の recovery に委ねる。
transaction の preflight journal が作成された時点で、`update_work/current/BeMusicSeeker.Updater.exe --recover --app-dir <app-dir>` を実行する per-user `RunOnce` recovery handoff と、同じ command を持つ persistent `Run` supervisor を登録する。`RunOnce` value 名は `!` prefix と retry generation suffix を持ち、recovery は開始時に新しい generation の handoff を登録して lease contention、rollback failure、recovery 自身の中断後にも次回ログオンで再試行できる状態を維持する（RunOnce 実行後に旧 generation が削除されても persistent supervisor が consumer を提供する）。cancel、commit、または recovery 完了時だけ app directory hash に紐づく handoff／supervisor をすべて削除する。rollback は復元完了を `rolled-back` phase として journal に durable に記録し、旧 restart executable の起動成功後に backup／journal を掃除するため、cleanup 中断時に復元処理を再実行しない。Process.Start 成功後に記録された `Restarting` の正の PID は commit evidence として扱い、PID がない起動未確定窓だけを executable identity で判定する。startup recovery または通常 updater が同一 app executable の生存を検出した場合は app tree を変更せず exit 2 と handoff 保持で watchdog／次回ログオンへ委ねる。

## Updater

updater protocol version は `1`。`BeMusicSeeker.Updater.exe --version` で確認できる。

開発時の x64 Release build は main app の実行確認用であり、updaterを配布物へ混在させない。配布時は `Properties/PublishProfiles/WinX64SelfContained.pubxml` で main app のuntrimmed managed bundle＋ReadyToRun SCDを、`BeMusicSeeker.Updater/Properties/PublishProfiles/WinX64SelfContainedSingleFile.pubxml` で updater の self-contained single-file SCD をそれぞれ生成する。main appはnative self-extractを使わず、SDK／SQLite／WPF native runtimeをexe隣接に置く。`scripts/publish.ps1` が clean publish output を組み合わせ、updater は root に exe 一つだけを配置する。managed dependency の解決は `app.config` の private probing に依存しない。

updater 引数:

- `--app-dir`: アプリ本体ディレクトリ
- `--package`: `app-dir/update_work/downloads/` 配下にある検証済み zip（この境界外のパスは拒否）
- `--backup-dir`: `update_backup`
- `--ready-file`: `app-dir/update_work/current/updater-ready.txt`（request 受理 handshake。境界外のパスは拒否）
- `--decision-file`: `app-dir/update_work/current/updater-decision.txt`（`proceed`／`cancel` の二段階 launch decision。境界外のパスは拒否）
- `--pid`: 終了待ち対象の BeMusicSeeker process id
- `--restart-exe`: 更新後に起動する exe（必須。app directory 内の既存ファイルで、更新 package に同じ相対パスを含み、更新後も存在する必要がある）

updater は `--pid` の終了を最大 60 秒待つ。
`--backup-dir` は app directory 直下の `update_backup/` と完全一致しなければならない。

## ファイル保持ポリシー

常に保持する top-level:

- `config/`
- `data/`
- `log/`
- `logs/`
- `update_backup/`
- `update_work/`

`imported_metadata/`、root の `chart-info-metadata.db`、root の `chart-info-metadata.7z` は metadata bundle 用のアプリ管理 artifact として扱う。

更新時、updater は既存の `imported_metadata/` と root の metadata bundle を掃除する。metadata 同梱版を適用した場合は、新 package の root に含まれる `chart-info-metadata.7z` だけが配置される。通常版を適用した場合は metadata bundle は配置されない。

次回起動時、metadata importer は root の `chart-info-metadata.7z` を import し、`imported_metadata/chart-info-metadata.7z` へ移動する。`imported_metadata/` は最後に import した metadata bundle のアプリ管理 cache であり、必要な場合は同ファイルを root に戻して再 import できる。

配布 zip は `update-managed-files.txt` を同梱する。通常の配布ファイル同期では、この manifest に含まれるファイルをアプリ管理ファイルとして扱う。更新時は、新パッケージの管理ファイルで既存管理ファイルを置換し、新パッケージから消えた管理ファイルを削除する。

初回更新元に `update-managed-files.txt` がない場合は、新パッケージと同じ相対パスに既に存在するファイルだけを置換対象として扱う。旧パッケージから新パッケージで削除されたファイルの掃除より、ユーザー追加ファイルを消さないことを優先する。

## Backup と rollback

更新前の管理ファイルは `update_backup/previous/` に退避する。保持数は 1 世代。

`BackingUp` または live tree の mutation に入る前に、既存の managed path を preflight する。ファイルは排他アクセスで開けることを確認し、managed directory は配下の managed file 全体を確認する。排他 lock などで確認できない path が一つでもあれば、backup rotation と canonical tree の mutation を開始せず、package と `data/`、`config/`、unmanaged file を保持したまま failure receipt を残して終了する。この preflight 後に発生する TOCTOU race や OS の電源断を完全に吸収する契約ではない。

更新適用から restart executable の `Process.Start` 成功までは rollback 可能な段階とし、失敗時は今回の package path と extracted directory を掃除して `update_backup/previous/` から復元する。rollback は canonical の新しい path を先に一括削除せず、`update_work/` 配下の sibling staging に backup をコピーしてから、既存 file は replace、型が変わる entry は sibling quarantine を経由して promote する。これにより rollback 中も backup と現在の tree の authority を失わない。ユーザー追加ファイルや保持対象ディレクトリは rollback でも触らない。通常の適用失敗では復元後に旧 restart executable を自動起動せず、failure receipt と recovery material を残して終了する。

rollback 自体が失敗した場合は適用を成功扱いにせず、restart と commit を行わない。primary failure と rollback failure を同じ persistent receipt に残し、唯一の backup、work、journal を削除しない。lock などの fault が除去された後は、既存の journal recovery が保持された backup から old または new の complete tree へ収束できる状態を維持する。

restart executable の起動に成功した時点を更新の commit point とする。適用後、restart 前に zip／extract directory の cleanup を試み、失敗しても rollback せず警告を stderr に出して、再起動後の startup cleanup に委ねる。restart 後に updater が一時領域を同時に掃除しないことで、起動済み新プロセスとの cleanup 競合を避ける。

## Release 運用

`scripts/publish.ps1` は以下を行う。

- main appはuntrimmed managed bundle＋ReadyToRun SCD、updaterはself-contained single-file exeを別々のpublish profileから生成し、main appのSDK-owned native runtimeとapplication content／native owner directoryを隣接配置する
- x64 BASS native family（`bass.dll`、`bassasio.dll`、`bassenc.dll`、`bassmix.dll`、`basswasapi.dll`、`bass_fx.dll`）と `lang/*.json` を明示 inventory で同梱し、incremental build の残骸を取り込まない
- 通常版 zip を作成
- `-IncludeMetadata` 指定時に `chart-info-metadata.7z` 同梱版 zip を作成
- `update-managed-files.txt` と `dist/update-v{version}.json` を生成
- `update.json` の assets は通常版、metadata 同梱版の順に並べる
- 旧クライアント向けの公開用 `version.txt` を `AssemblyInformationalVersion` から生成する
- 公開リポジトリへ同期する際、同一 version の古い zip を削除して stale asset 混入を避ける

`scripts/release.ps1 -CreateDraft` は以下を行う。

- 開発リポジトリの `AssemblyInformationalVersion` と公開リポジトリの `dist/` zip から `update.json` を生成
- release commit と tag を作成
- tag のみ push
- release notes の `release notes/v{version} リリースノート.md` を使って GitHub Release draft を作成または更新
- 公開ブランチは push しない

`scripts/release.ps1 -PublishDraft` は以下を行う。

- `main` ブランチ上で実行されていることを確認
- GitHub Release が存在することを確認
- remote tag、local tag、現在の HEAD が同じ release commit を指すことを確認
- GitHub Release asset の名前と size が `update.json` 作成元の local zip と一致することを確認
- GitHub Release が draft 状態なら publish する。既に公開済みの場合は公開ブランチへの反映を確認する
- release commit を `origin/main` へ push
- remote `main` が release commit を指すことを確認

この順序により、`origin/main` の `update.json` が未公開の Release asset を指す状態を避ける。
`raw.githubusercontent.com` は GitHub 側のキャッシュやブランチ参照の反映遅延により、release commit を push した直後に古い内容を返すことがあるため、publish 成功判定には使わない。

既存 draft を更新する場合、今回の local zip に存在しない余剰 Release asset は削除してから asset を再アップロードする。これにより、古い metadata 同梱 zip などが draft に残ったまま publish されることを避ける。
