# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-21

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate前は禁止。Gate後もユーザーの明示指示まで禁止

## Active outcome

### `UI-05 Shell closure`

状態: in progress

active outcome base commit: `6d170cb9`
active execution package: `UI05-R Remaining shell closure`
sequence cursor: `UI05-R2B-U2 Package bulk maintenance workflow`

目的:

残るroot binding relay、feature workflow、code-behind orchestration、非event `async void`を、child owner / application workflow / view-hostの正しい境界へ移す。行数削減ではなく、state / behavior ownershipとdependency directionでUI Gateを閉じる。

Acceptance criteria:

- feature View / UserControlがchild ownerをbinding rootとし、rootはchild ViewModelのcomposition propertyを除いてleaf property / command / `PropertyChanged`を再公開しない。
- `MainWindowViewModel`に非eventの`async void`、feature-local mutable state、feature workflow、private callback hostが残らない。
- `MainWindow.cs`のevent handlerは、一つのfeature command / queryへのrequest変換と、focus / selection / scroll / hit-test / drag visual / dialog presentation等のView固有applyだけで説明できる。
- settings dialog、normal library refresh、package operation、playlist / library / chart actionの残存routeがproductionの通常経路でowner / shell compositionへ直接接続され、旧pass-through、workflow body、callback host、test-only seamが担当unitで削除される。
- UI observable behavior、失敗契約、setting key / serialized value、DB schema / data、外部ファイル形式、external syncのcancellation / progressを維持する。
- 各execution unitのbehavior test、UI-05 outcome-wide Full verification、Release executable UI smoke、重大指摘なしのstatic / outcome reviewを完了する。
- Structural size triggerの達成を完了条件にせず、triggerを超える残scopeの責務が許可shell / view-host boundaryで説明できることをoutcome reviewで確認する。

Non-goals:

- feature / model内に残るglobal settings、application / dispatcher contextの最終除去（`MIG-01`）。
- pending estimated-install broad hostとplaylist custom-folder status persistenceの解消（`OWN-01`）。
- native / process / path / output layout、`.NET 10` TFM / package変更（`MIG-02`〜`MIG-05`とGate後migration）。
- WPF固有のselection、focus、scroll、hit-test、virtualization、drag visualを行数のためだけにView外へ移すこと。

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
| UI-05 Shell closure | in progress |
| OWN-01 Residual owner-boundary reconciliation | not started |
| MIG-01 Configuration and application-context closure | not started |
| MIG-02 Path, process and updater closure | not started |
| MIG-03 Native interop and UI-host closure | not started |
| MIG-04 Build, dependency and output closure | not started |
| MIG-05 .NET 10 migration rehearsal and handoff | not started |
| GATE-01 Refactoring completion audit | not started |

許可する状態は`not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。internal complexity、行数trigger超過、複数caller、broad routeはexecution packageへ分解し、`blocked`理由にしない。production evidenceが別outcomeの明示boundaryに属する場合は、ordered backlogの次ownerへ引き渡す。

## Gate scorecard

| Gate area | State | Current evidence / owner |
|---|---|---|
| UI ownership | not met | UI-05は継続中。root ViewModelに非event `async void`、MainWindow code-behindに複数のfeature workflow / multi-step actionが残る。`UI05-R1`〜`R4`で閉じる |
| Library ownership | not met | `IPendingEstimatedInstallHost`相当がfacade lock / private operationを広く露出する。`OWN01-A` / `C`で閉じる |
| Playlist ownership | not met | `BMSPlaylist`にcustom-folder output statusのraw SQL / transactionとglobal dispatcher / application residualが残る。`OWN01-B`と`MIG-01`で閉じる |
| Configuration ownership | not met | `Settings.Default`、application / dispatcher contextの直接依存がView / ViewModel / modelへ残る。`MIG-01`で境界化する |
| Platform boundary | not met | HintPath DLL、custom managed/native layout、P/Invoke、external process、updater、WPF / WinForms / WebBrowserが混在する。`MIG-02`〜`MIG-04`で閉じる |
| Migration readiness | not met | `net10.0-windows` disposable restore / build rehearsalが未実施。`MIG-05`で分類する |
| Structural cohesion / size | review required | 現行静的計測では4 scope中3 scopeがreview trigger超。数値自体はfailureではなく、残責務inventoryをUI-05 / OWN-01 / Gate reviewで判定する。trigger未満のplaylistにもownership違反があるため、行数だけでは通過させない |
| Quality | in progress | committed UI-05 unitsは個別verification / review済み。UI-05 outcome-wide verification / smoke / reviewと後続Outcome、最終Gate evidenceは未完了 |

## Active outcome blockers

なし。
