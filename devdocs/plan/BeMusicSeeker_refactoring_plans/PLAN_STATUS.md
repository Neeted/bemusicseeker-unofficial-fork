# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-25

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate前は禁止。Gate後もユーザーの明示指示まで禁止

## Active outcome

### `OWN-01 Residual owner-boundary reconciliation`

状態: in progress

- active outcome base commit: `3088771c`
- observed production checkpoint: `3088771c`
- active execution package: `OWN-01 Residual owner-boundary reconciliation`
- execution anchor: `Owner-boundary closure`

目的:

pending estimated-install、playlist custom-folder status persistence、library facade-owned writer、cross-owner lock callback host、test-only production seamを、既存ownerまたは用途限定portへ閉じる。完了済みUI / library / playlist ownerを再抽出せず、production route、durable/live順序、旧surface削除、behavior verificationを一つのcorridorとして進める。

Acceptance criteria:

- pending estimated-install workflowがpackage、catalog、maintenance、resource-health ownerを直接接続し、`IPendingEstimatedInstallHost`とfacade lock / private-operation bridgeを退役させる。
- custom-folder output statusのread / batch write / delete / repair-row queryがrepositoryまたはoutput ownerへ移り、`BMSPlaylist`のraw connection / SQL / transactionを退役させる。
- library database writerとSQL boundaryがcatalog maintenance / mutation ownerまたはgatewayに収まり、generic facade callback writer、production `ForTest` seam、未使用writerを退役させる。
- playlist external registrationとURL completionが登録owner / URL ownerのproduction routeへ接続され、root lock / mutable collection callback hostとstatic test seamを退役させる。
- UI observable behavior、失敗契約、setting key / serialized value、DB schema / data、外部ファイル形式、external syncのcancellation / progressを維持する。
- 各unitでproduction route、behavior tests、旧route / relay / seam削除、targeted verification、fresh static reviewを完了する。
- outcome-wide Full verification、該当UI smoke、fresh outcome reviewを完了し、Gate scorecardを更新する。

Non-goals:

- `Settings.Default`、application lifetime、dispatcher / global scheduler、path、process、native / UI technologyの最終境界化（`MIG-01`〜`MIG-04`）。
- 既にcohesiveなcatalog、package、maintenance、resource-health、playlist persistence / output ownerの再分割。

## Stable terminal steps

| Step | State | Exit condition |
|---|---|---|
| `OWN-01-B1 Pending estimated-install ownership` | completed | pending estimated-installをworkflow ownerと用途別mutation capabilityへ接続し、旧hostを退役させた |
| `OWN-01-B2 Custom-folder status repository ownership` | completed | custom-folder statusのraw connection / SQL / transactionをrepository / output ownerへ移した |
| `OWN-01-B3 Library database writer and production seam closure` | completed | facade-owned writer、generic callback、production `ForTest` SQL seamをcatalog gatewayへ移した |
| `OWN-01-B4 External registration and URL completion closure` | active | external registration callback hostとURL completion static test seamを退役させ、OWN-01をcompletion auditへ進める |

## Active implementation batch

状態: in progress

`OWN-01` plannerが作成した有限batchであり、B1から依存順に実装する。batchに`active`または`pending`がある間はplannerを再起動しない。

| Batch | Unit | State | Closure family |
|---|---|---|---|
| `OWN-01` | `B1` | `completed` | `pending estimated-install` |
| `OWN-01` | `B2` | `completed` | `playlist custom-folder persistence` |
| `OWN-01` | `B3` | `completed` | `library DB writer / SQL seam` |
| `OWN-01` | `B4` | `active` | `playlist external registration / URL completion` |

## Current code evidence

- `MainWindowViewModel.cs`: 5,616行。非eventを含め`async void`宣言は検出されない。
- `MainWindow.cs`: 7,062行。検出される`async void`はWPF event handlerである。
- rootは`MainChartList`、`PlaylistWorkspace`、`ChartFilters`、`LibraryFolderTree`、`InstallTree`、`MaintenanceTree`、`PlayHistory`、`PlaybackPanel`、`ProgressHub`、`SettingDialog`をchild composition propertyとして公開し、XAMLはこれらをbinding rootとして使用している。
- MainWindowにはtyped owner query / commandとWPF control mappingへ整理済みのrouteが多い。event数や行数だけで追加owner抽出を行わず、T1でfeature decision / orchestrationの実在を判定する。
- `Settings.Default`、`Application.Current`、dispatcher、process等の残参照は、UI feature ownership違反でない限り`MIG-01`〜`MIG-04`へ分類する。
- UI-05-T2 の grouped residual closure と T3 の outcome-wide Full verification、Release smoke、fresh outcome review、completion status更新が完了した。Full verificationは成功し、repository Release executableは応答可能で、fresh outcome reviewにMajor / Moderate指摘はない。

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
| OWN-01 Residual owner-boundary reconciliation | in progress |
| MIG-01 Configuration and application-context closure | not started |
| MIG-02 Path, process and updater closure | not started |
| MIG-03 Native interop and UI-host closure | not started |
| MIG-04 Build, dependency and output closure | not started |
| MIG-05 .NET 10 migration rehearsal and handoff | not started |
| GATE-01 Refactoring completion audit | not started |

許可する状態は`not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。internal complexity、行数trigger超過、複数caller、broad routeは`blocked`理由にしない。

## Gate scorecard

| Gate area | State | Current evidence / owner |
|---|---|---|
| UI ownership | met | T1 finite inventory、T2 grouped closure、Full verification、repository Release executable smoke、fresh outcome reviewが完了した |
| Library ownership | in progress | pending estimated-install broad hostとlibrary facade-owned writerを`OWN-01`で閉じる |
| Playlist ownership | in progress | custom-folder output status persistenceとexternal registration hostを`OWN-01`で閉じる |
| Configuration ownership | not met | global settings、application / dispatcher contextを`MIG-01`で境界化する |
| Platform boundary | not met | path / process / updater、native / UI host、HintPath / output layoutを`MIG-02`〜`MIG-04`で閉じる |
| Migration readiness | not met | disposable `net10.0-windows` restore / build rehearsalを`MIG-05`で実施する |
| Structural cohesion / size | review required | 現行計測では`MainWindow.cs`とtop-level `BMSLibrary*.cs`の2 scopeがtrigger超。数値はfailureではなく、T1 / OWN-01 / Gate reviewで責務を判定する |
| Quality | in progress | UI-05のFull verification、Release smoke、fresh outcome reviewは完了。後続Outcome / Gate evidenceは未完了 |

## Active external blocker

なし。
