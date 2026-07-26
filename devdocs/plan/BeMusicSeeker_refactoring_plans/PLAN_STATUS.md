# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-25

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate前は禁止。Gate後もユーザーの明示指示まで禁止

## Completed outcomes

### `MIG-01 Configuration and application-context closure`

状態: completed

- active outcome base commit: `15f9b965`
- observed production checkpoint: `15f9b965`
- active execution package: `MIG-01 Configuration and application-context closure`
- execution anchor: `Configuration consumption closure` (completed)
- sequence cursor: `MIG-01-B4 Application context, scheduler and lifetime closure` (completed)
- next outcome: `MIG-02 Path, process and updater closure` (ready)

目的:

configuration snapshot / purpose storeとapplication context / scheduler / lifetime portをproduction compositionから注入し、library・playlist・playback・Viewがgenerated settingsやglobal application contextを直接取得しない状態へ閉じる。setting key、serialized value、save timing、UI observable behavior、失敗契約は維持する。

完了条件:

- library / playlist、playback/player、MainWindow view settings、application context / schedulerをB1〜B4のvertical unitで閉じる。
- 各unitでproduction route、behavior test、旧fallback / relay / broad host削除、検証、fresh static reviewを完了する。
- `Settings.Default`はconfiguration adapter / editor内、application / dispatcher globalはApp / View / view-host adapter内に限定し、path / process / native / UI technology残件は次Outcomeへ分類する。
- outcome-wide Full verification、該当UI smoke、fresh outcome reviewを完了し、CFG-01 / CTX-01を`boundary met`へ更新する。

Non-goals:

- path、process、updater、native、WPF / WinForms / COM technology、HintPath / output layoutの最終境界化（`MIG-02`〜`MIG-04`）。
- `System.Configuration`の.NET 10移行方式、package replacement、production TFM変更（Gate後）。

### `MIG-02 Path, process and updater closure`

状態: completed

- active outcome base commit: `a87aede7`
- observed production checkpoint: `06f36ef9`
- active execution package: `Path, process and updater boundary closure`
- execution anchor: `MIG-02-B4 Updater, application restart and outcome closure` (completed)
- sequence cursor: `MIG-02-B4 Updater, application restart and outcome closure` (completed)
- next outcome: `MIG-03 Native interop and UI-host closure` (ready)

目的:

application runtime path、external shell、external player process session、updater / restartを用途別ownerとtyped request / receiptへ閉じ、設定・ファイル配置・URL・process lifetime・restart / rollbackの既存契約を維持する。

完了条件:

- B1〜B4のvertical unitでproduction route、behavior test、旧runtime / process / updater route削除、検証、fresh static reviewを完了する。
- raw runtime path取得はapplication path policyまたは明示したMIG-03 / MIG-04 residualへ分類し、外部process起動はshell、player、application / updater gateway内へ閉じる。
- `PATH-01`、`PROC-01`、`UPD-01`を`boundary met`へ更新し、outcome-wide Full verification、Release executable smoke、fresh outcome reviewを完了する。

Non-goals:

- native interop、WPF / WinForms / COM technology、HintPath / output layoutの最終境界化（`MIG-03`〜`MIG-04`）。
- .NET 10 retarget、package replacement、production TFM変更（Gate後）。

## Active outcome

### `MIG-04 Build, dependency and output closure`

状態: completed

- active outcome base commit: `89134990`
- observed production checkpoint: `89134990`
- active execution package: `Build, dependency and output closure`
- execution anchor: `MIG-04-B4 Updater dependency, deployment boundary and MIG-04 closure` (completed)
- sequence cursor: `MIG-04-B4 Updater dependency, deployment boundary and MIG-04 closure` (completed)
- next outcome: `MIG-05 .NET 10 migration rehearsal and handoff` (ready)

目的:

managed dependency、observable collection / notification contract、output layout、updater build / deployment routeをproject / application boundaryへ閉じ、既存のportable layout、update protocol、UI observable behavior、失敗契約を維持する。

完了条件:

- B1〜B4のvertical unitでproduction route、behavior test、旧Livet / relocation / updater path seam削除、検証、fresh static reviewを完了した。
- `LAYOUT-01`、`DEP-01`、`DEPLOY-01`のowner境界を更新し、outcome-wide Full verification、Release executable smoke、fresh outcome reviewを完了した。

Non-goals:

- package version、replacement、production TFM変更、deps.json / apphost / RID / publish layoutへの移行（`MIG-05`以降のmigration plan）。
- release package作成、tag、push、publish（Refactoring Completion Gate後かつ明示指示後）。

## Stable terminal steps

| Step | State | Exit condition |
|---|---|---|
| `OWN-01-B1 Pending estimated-install ownership` | completed | pending estimated-installをworkflow ownerと用途別mutation capabilityへ接続し、旧hostを退役させた |
| `OWN-01-B2 Custom-folder status repository ownership` | completed | custom-folder statusのraw connection / SQL / transactionをrepository / output ownerへ移した |
| `OWN-01-B3 Library database writer and production seam closure` | completed | facade-owned writer、generic callback、production `ForTest` SQL seamをcatalog gatewayへ移した |
| `OWN-01-B4 External registration and URL completion closure` | completed | external registration callback hostとURL completion static test seamを退役させ、OWN-01のFull verificationとoutcome reviewを完了した |

## Completed implementation batch

状態: completed

`MIG-01` と `MIG-03` plannerが作成した完了済みの有限batchを記録する。各batchはB1から依存順に実装し、完了時にoutcome-wide verificationとreviewを閉じた。

| Batch | Unit | State | Closure family |
|---|---|---|---|
| `MIG-01` | `B1` | `completed` | `library / playlist configuration consumption` |
| `MIG-01` | `B2` | `completed` | `playback / player settings gateway` |
| `MIG-01` | `B3` | `completed` | `MainWindow view settings and configuration seam` |
| `MIG-01` | `B4` | `completed` | `application context / scheduler / lifetime` |
| `MIG-03` | `B1` | `completed` | `native file discovery and loader ownership` |
| `MIG-03` | `B2` | `completed` | `audio SDK boundary` |
| `MIG-03` | `B3` | `completed` | `external-player native window and host attachment` |
| `MIG-03` | `B4` | `completed` | `technology-neutral presentation contracts and MIG-03 closure` |

## Active implementation batch

状態: completed

`MIG-04` plannerが作成した有限batchであり、B1から依存順に実装する。activeまたはpendingのunitがある間はplannerを再起動しない。

| Batch | Unit | State | Closure family |
|---|---|---|---|
| `MIG-04` | `B1` | `completed` | `technology-neutral observable model contract` |
| `MIG-04` | `B2` | `completed` | `owner-held observable collections` |
| `MIG-04` | `B3` | `completed` | `managed dependency graph and output policy` |
| `MIG-04` | `B4` | `completed` | `updater dependency, deployment boundary and MIG-04 closure` |

## Current code evidence

- `MainWindowViewModel.cs`: 5,616行。非eventを含め`async void`宣言は検出されない。
- `MainWindow.cs`: 7,062行。検出される`async void`はWPF event handlerである。
- rootは`MainChartList`、`PlaylistWorkspace`、`ChartFilters`、`LibraryFolderTree`、`InstallTree`、`MaintenanceTree`、`PlayHistory`、`PlaybackPanel`、`ProgressHub`、`SettingDialog`をchild composition propertyとして公開し、XAMLはこれらをbinding rootとして使用している。
- MainWindowにはtyped owner query / commandとWPF control mappingへ整理済みのrouteが多い。event数や行数だけで追加owner抽出を行わず、T1でfeature decision / orchestrationの実在を判定する。
- `Settings.Default`、`Application.Current`、dispatcher、process等の残参照は、UI feature ownership違反でない限り`MIG-01`〜`MIG-04`へ分類する。
- `MIG-01-B1`〜`B4`でconfiguration snapshot、application lifetime、culture catalog、UI schedulerをproduction compositionから注入し、library / playlist / ViewModel / aggregateのglobal context fallbackを退役させた。設定値・serialized value・UI observable behavior・失敗契約はFull verificationで確認済み。
- UI-05-T2 の grouped residual closure と T3 の outcome-wide Full verification、Release smoke、fresh outcome review、completion status更新が完了した。Full verificationは成功し、repository Release executableは応答可能で、fresh outcome reviewにMajor / Moderate指摘はない。
- OWN-01-B1〜B4 の owner-boundary closure、outcome-wide Full verification、repository Release executable smoke、fresh outcome reviewが完了した。外部登録の準備はaggregate ownerのimmutable factsへ移り、URL completionはproduction scheduler routeで検証できる構造になっている。
- `MIG-02-B1`〜`B4`でpath、external shell、external player、updater / restartのproduction routeをtyped gatewayへ閉じ、global fallbackをcomposition / adapter境界へ限定した。Full verification、Release executable smoke、fresh outcome reviewが完了している。
- `MIG-03-B1`〜`B4`でnative file discovery、audio SDK、external-player window host、window placement / player-resolution、settings-dialog presentation contractを用途別adapterとtechnology-neutral contractへ閉じた。SettingsDialogViewModelのLR2 schema windowは専用の中立portへ接続し、WPF request factoryはview adapterへ限定した。Full verification、Release executable smoke、fresh outcome reviewが完了している。
- `MIG-04-B3`でresolved managed reference graphをproject-owned MSBuild output policyへ接続し、managed DLLを`libs`へ直接出力する旧copy-then-relocate targetを退役させた。Release verificationはroot managed DLL、legacy native directory、`libs/x86`を拒否し、`app.config`の`libs` probingとoutput layoutをbehavior / project testsで確認する。
- `MIG-04-B4`で未使用の`System.Deployment`参照を削除し、updater projectをbuild-only dependencyとして維持しながら`BeMusicSeeker.Updater.exe`をアプリ出力ルートへ配置するdeployment boundaryをproject / behavior test / Release verificationで確認した。updaterはmanaged dependencyの`libs`へ混在させず、missing outputはbuild failureとする。併せて初期化時のinstall table復元をcatalog writer / failure cleanup境界へ戻し、通常のpackage mutationとの競合を防ぐbehavior / source-order testを追加した。

## Outcome states

| Outcome | State |
|---|---|
| UI-01 Main table presentation and regular chart ownership | completed |
| APP-01 Application composition and settings lifecycle boundary | completed |
| UI-02 Playback ownership | completed |
| UI-03 Playlist workspace ownership | completed |
| UI-04 Play history ownership | completed |
| LIB-01 Scan pipeline core ownership | completed |
| LIB-03 Catalog storage, mutation, maintenance and resource-health ownership | completed |
| LIB-02 Package, install-destination and file-operation ownership | completed |
| LIB-04 LR2 synchronization ownership | completed |
| LIB-05 Playlist-reference ownership | completed |
| LIB-06 Library facade and scan integration closure | completed |
| PL-01 Playlist persistence and reload ownership | completed |
| PL-02 Playlist external-sync and output ownership | completed |
| UI-05 Shell closure | completed |
| OWN-01 Residual owner-boundary reconciliation | completed |
| MIG-01 Configuration and application-context closure | completed |
| MIG-02 Path, process and updater closure | completed |
| MIG-03 Native interop and UI-host closure | completed |
| MIG-04 Build, dependency and output closure | completed |
| MIG-05 .NET 10 migration rehearsal and handoff | ready |
| GATE-01 Refactoring completion audit | not started |

許可する状態は`not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。internal complexity、行数trigger超過、複数caller、broad routeは`blocked`理由にしない。

## Gate scorecard

| Gate area | State | Current evidence / owner |
|---|---|---|
| UI ownership | met | T1 finite inventory、T2 grouped closure、Full verification、repository Release executable smoke、fresh outcome reviewが完了した |
| Library ownership | met | pending estimated-install、library writer / SQL seamを`OWN-01`で閉じ、Full verificationとfresh outcome reviewを完了した |
| Playlist ownership | met | custom-folder output status persistence、external registration preparation、URL completion test seamを`OWN-01`で閉じ、Full verificationとfresh outcome reviewを完了した |
| Configuration ownership | met | `MIG-01-B1`〜`B4`でsettings snapshot、application lifetime、culture catalog、UI schedulerをcomposition boundaryへ閉じ、Full verification、Release smoke、fresh outcome reviewを完了した |
| Platform boundary | met | `MIG-02`でPATH-01 / PROC-01 / UPD-01、`MIG-03`でNAT-01 / INT-01 / UIH-01、`MIG-04-B3`でLAYOUT-01 / DEP-01、`MIG-04-B4`でDEPLOY-01のproduction / project boundaryを閉じた。migration rehearsalは`MIG-05`で実施する |
| Migration readiness | not met | disposable `net10.0-windows` restore / build rehearsalを`MIG-05`で実施する |
| Structural cohesion / size | review required | 現行計測では`MainWindow.cs`とtop-level `BMSLibrary*.cs`の2 scopeがtrigger超。数値はfailureではなく、T1 / OWN-01 / Gate reviewで責務を判定する |
| Quality | in progress | UI-05、OWN-01、MIG-01、MIG-02、MIG-03、MIG-04のFull verification、Release smoke、fresh outcome reviewは完了。MIG-05 / Gate evidenceは未完了 |

## Active external blocker

なし。
