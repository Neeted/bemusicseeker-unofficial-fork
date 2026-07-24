# PLAN_STATUS

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md) / [Codex 共通実行ルール](./00_Codex共通実行ルール.md)

最終更新日: 2026-07-25

## Baseline

- code baseline commit: `44fd2fde`
- Release Freeze: active
- `git push` / tag / release / publish: Gate前は禁止。Gate後もユーザーの明示指示まで禁止

## Active outcome

### `UI-05 Shell closure`

状態: in progress

- active outcome base commit: `6d170cb9`
- observed production checkpoint: `464040f6`
- active execution package: `UI05-T Terminal shell closure`
- execution anchor: `UI05-T3 Outcome closure`

目的:

root binding relay、feature workflow、code-behind orchestration、非event `async void`の残件を有限inventoryとして閉じる。正当なWPF view-host処理や後続migration boundaryをroute単位で再抽出せず、UI-05をoutcome verificationまで到達させる。

Acceptance criteria:

- feature View / UserControlがchild ownerをbinding rootとし、rootはchild ViewModelのcomposition propertyを除いてleaf property / command / `PropertyChanged`を再公開しない。
- `MainWindowViewModel`に非event `async void`、feature-local mutable state、feature workflow、broad callback hostが残らない。
- `MainWindow.cs`のevent handlerは、一つのfeature command / queryへのrequest変換と、dialog / focus / selection / scroll / hit-test / drag visual / WPF property mappingなどのview-host applyで説明できる。
- typed immutable presentation eventのterminal applyをbroad callback hostと誤認しない。
- UI observable behavior、失敗契約、setting key / serialized value、DB schema / data、外部ファイル形式、external syncのcancellation / progressを維持する。
- Full verification、Release executable UI smoke、fresh outcome reviewを完了する。
- structural size triggerを完了条件にせず、triggerを超えるscopeが許可boundaryまたは明示ownerでcohesiveに説明できることをreviewする。

Non-goals:

- pending estimated-install broad hostとplaylist custom-folder status persistenceの解消（`OWN-01`）。
- global settings、application / dispatcher、path、process、native / UI technologyの最終境界化（`MIG-01`〜`MIG-04`）。
- WPF固有のselection、focus、scroll、hit-test、virtualization、drag visual、ContextMenu mappingを行数のためだけにView外へ移すこと。

## Stable terminal steps

| Step | State | Exit condition |
|---|---|---|
| `UI05-T1 Closure inventory and classification` | completed | 現行root / View / XAML / presentation / test surfaceを有限分類し、T2 batchをmaterializeした |
| `UI05-T2 Grouped residual closure` | completed | `BLOCKING`を最大3 owner-family unitで閉じる |
| `UI05-T3 Outcome closure` | active | Full verification、UI smoke、fresh outcome review、修正、UI-05 completionと次Outcomeのready化 |

## Active implementation batch

状態: completed

`UI05-T2`はT1 plannerが作成した有限batchであり、B1から依存順に実装する。batchに`active`または`pending`がある間はplannerを再起動しない。

| Batch | Unit | State | Closure family |
|---|---|---|---|
| `UI05-T2` | `B1` | `completed` | `root shell / lifecycle / composition` |
| `UI05-T2` | `B2` | `completed` | `view-host / binding / typed presentation` |
| `UI05-T2` | `B3` | `completed` | `production-route legacy seam / test surface` |

## Current code evidence

- `MainWindowViewModel.cs`: 5,616行。非eventを含め`async void`宣言は検出されない。
- `MainWindow.cs`: 7,062行。検出される`async void`はWPF event handlerである。
- rootは`MainChartList`、`PlaylistWorkspace`、`ChartFilters`、`LibraryFolderTree`、`InstallTree`、`MaintenanceTree`、`PlayHistory`、`PlaybackPanel`、`ProgressHub`、`SettingDialog`をchild composition propertyとして公開し、XAMLはこれらをbinding rootとして使用している。
- MainWindowにはtyped owner query / commandとWPF control mappingへ整理済みのrouteが多い。event数や行数だけで追加owner抽出を行わず、T1でfeature decision / orchestrationの実在を判定する。
- `Settings.Default`、`Application.Current`、dispatcher、process等の残参照は、UI feature ownership違反でない限り`MIG-01`〜`MIG-04`へ分類する。
- UI-05-T2 の grouped residual closure は完了した。T3 の outcome-wide Full verification、Release smoke、fresh outcome review、completion status更新が残る。

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

許可する状態は`not started`、`ready`、`in progress`、`blocked`、`completed`、`gate met`。internal complexity、行数trigger超過、複数caller、broad routeは`blocked`理由にしない。

## Gate scorecard

| Gate area | State | Current evidence / owner |
|---|---|---|
| UI ownership | in progress | child binding rootsと非event `async void`除去は確認できる。T1 finite inventory、必要なgrouped closure、Full / smoke / outcome reviewが残る |
| Library ownership | not met | pending estimated-install broad host等を`OWN-01`で閉じる |
| Playlist ownership | not met | custom-folder output status persistence等を`OWN-01` / `MIG-01`で閉じる |
| Configuration ownership | not met | global settings、application / dispatcher contextを`MIG-01`で境界化する |
| Platform boundary | not met | path / process / updater、native / UI host、HintPath / output layoutを`MIG-02`〜`MIG-04`で閉じる |
| Migration readiness | not met | disposable `net10.0-windows` restore / build rehearsalを`MIG-05`で実施する |
| Structural cohesion / size | review required | 現行計測では`MainWindow.cs`とtop-level `BMSLibrary*.cs`の2 scopeがtrigger超。数値はfailureではなく、T1 / OWN-01 / Gate reviewで責務を判定する |
| Quality | in progress | UI-05 outcome-wide Full verification、Release smoke、fresh outcome reviewと後続Outcome / Gate evidenceが未完了 |

## Active external blocker

なし。
