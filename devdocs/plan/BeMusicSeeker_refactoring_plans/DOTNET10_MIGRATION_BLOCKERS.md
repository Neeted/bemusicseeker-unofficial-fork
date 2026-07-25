# .NET 10 migration blocker register

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md)に対する living blocker register。現在の `net472` behavior を維持しながら、Refactoring Completion Gate 前に ownership / adapter / project boundaryへ閉じる課題と、Gate後の純粋な `.NET 10` 移行課題を区別する。

この文書は調査履歴や次 ticket の候補を保存しない。blockerの owner、境界、state、migration分類が変わった場合だけ更新する。

## Current project baseline

- app / tests / updater: `net472`, x64
- app: WPF + WinForms
- SDK: `global.json` で .NET SDK 10 系
- config: `app.config`、`System.Configuration`、custom portable settings provider
- dependencies: NuGet と `libs/*.dll` HintPath の混在
- native: `native/*.dll`、`vendor/native/x64/*.dll`、custom output relocation
- migration rehearsal: 未実施

## Blockers

| ID | Area | Current coupling | Refactoring Gate の境界条件 | Owner outcome | Gate後の migration work | State |
|---|---|---|---|---|---|---|
| CFG-01 | `Settings.Default` / `System.Configuration` | configuration adapter / editorがload、edit、save、upgradeを所有し、workflowはsnapshot / 用途別storeを受け取る | load / edit / save / upgradeをconfiguration ownerへ集約し、workflowはsnapshot /用途別storeを受け取る。View固有設定もview settings adapterを通す | `MIG-01` | ConfigurationManager継続か新storeかを決め、既存user.config migrationを実装 | boundary met |
| CTX-01 | Application / Dispatcher context | App compositionがlifetime、culture catalog、UI schedulerを所有し、ViewModel / model / aggregateはport経由で利用する | application lifetime、UI scheduling、shutdown / rejectionをapplication context / scheduler portまたはView boundaryへ限定する | `MIG-01` | .NET 10 WPF dispatcher / startup / shutdown behaviorを再検証 | boundary met |
| DB-01 | SQLite / raw SQL / schema / transaction | catalog mutation、playlist persistence、custom-folder output statusはgateway / repository / ownerがraw connection / SQL / transactionを所有する。facade / aggregateのwriter residualはない | scan、catalog mutation、LR2、playlist persistence、custom-folder output statusごとにtransaction ownerを明示し、application-facing facade / aggregateがraw connection / SQLを直接所有しない | `OWN-01` | SQLite provider / native runtimeの互換性と必要なpackage / API移行を検証 | boundary met |
| CONC-01 | Mutation serialization / lock ordering | pending estimated-install、library writer、external registration preparationはcanonical ownerのguard / immutable factsへ移り、facade lock / private callback / mutable collection callback hostの残件はない | canonical ownerのguard / transaction / live apply / receipt順序を維持し、owner間はimmutable request / receipt /用途別capabilityで接続する。別ownerのlock / mutable stateをhostで露出しない | `OWN-01` | cancellation、synchronization primitive、dispatcher interaction、failure / shutdown orderingを再検証 | boundary met |
| PATH-01 | Base directory / assembly location | settings、native、update、metadata、language catalogがprocess / assembly layoutへ直接依存 | application path policyを用途別providerが所有し、workflowへraw assembly / executable path取得を漏らさない | `MIG-02` | framework-dependent / self-contained / single-file対応範囲を決定 | open |
| PROC-01 | External process | player、explorer、browser、score viewer、restartのpath / quoting / shell policyがcall siteへ分散 | 用途別process gatewayとimmutable request contractへ集約する | `MIG-02` | apphost location、UseShellExecute、quoting behaviorを再検証 | open |
| UPD-01 | Updater coupling | updaterは`net472` exe、custom copy、restart pathを前提としapplication workflowと結合 | update check / launch / restartをapplication workflowからgatewayへ分離し、path policyを`MIG-02`へ置く | `MIG-02` | updater同時移行か外部legacy tool継続かを決定 | open |
| NAT-01 | Native DLL layout | app / testsでcopy先とload pathが不統一 | base directoryとnative asset resolution / loadを用途別adapterへ閉じる | `MIG-03` / `MIG-04` | RID native asset、explicit copy、publish layoutを決定 | open |
| INT-01 | P/Invoke / manual load / CAS | Everything、filesystem、window、audioにP/Invoke、manual load、legacy security attributeが残る | call siteをplatform adapter内に限定し、application / domain contractへnative handle / loader policyを漏らさない | `MIG-03` | platform annotation、interop方式、CAS削除、loader error policyを決定 | open |
| UIH-01 | WPF + WinForms + WebBrowser / COM | MainWindow、player host、dialog、resourceがUI technologyと結合 | WPF / WinForms / COM型をView / view-host adapterの外へ出さない。View固有code量は問題にせずdependency directionで判定する | `UI-05` / `MIG-03` | WinForms継続、WebBrowser / WebView2、System.Drawingの扱いを決定 | open |
| LAYOUT-01 | `app.config` probing / managed output | build後にmanaged DLLを`libs`へ移動しrootから削除 | output policy、probing、copy / removalをproject / loader boundaryへ閉じ、business workflowへ漏らさない | `MIG-04` | deps.json / apphost / publish layoutへ置換 | open |
| DEP-01 | HintPath managed DLL | Livet、MetroRadiance、Expression、sqlite.net、Bass.Net等のcompatibility / identityが未確定 | DLL固有型をapplication / domain contractから排除し、各依存のusage / load policyをproject境界で説明できる | `MIG-04` | NuGet / replacement / retentionを依存ごとに決定 | open |
| DEPLOY-01 | `System.Deployment` / updater project | project referenceとcustom updater copyがあるがsource usage / release policyが不明確 | 実使用をcode / buildから確定し、不要referenceまたはdeployment-specific codeをproject boundaryへ限定する | `MIG-04` | 不要なら削除、必要なら.NET対応deploymentを別設計 | open |
| PROBE-01 | `.NET 10` migration rehearsal | `net10.0-windows` restore / buildで表面化するerror分類が未確認 | disposable worktree / copyで最小TFM probeを実行し、残失敗が既存blockerのpackage / API / runtime / layout / deployment / data migrationへ分類され、owner / MVVM再設計が残っていない | `MIG-05` | 分類済みerrorを入力にproduction migrationを実装 | open |

## Gate classification rule

Gate audit時、`PROBE-01`以外は次のどちらかでなければならない。

1. `boundary met`: architecture上のowner / adapter / gateway / project boundaryがあり、残作業がpackage、API、TFM、runtime、deployment、data migrationの変更だけである。
2. `not applicable`: 実使用がなく、安全に削除済みである。

`PROBE-01` は rehearsal実行と分類が完了した `verified` でなければならない。

「巨大型のprivate workflowを分けないと移行判断できない」「UI / domain ownerが未定」「global settingsのsave timingが複数箇所に分散」「複数workflowが同じfacade lock / callback hostを共有する」は `boundary met` ではない。行数trigger超過だけは blockerではない。

## Update rule

- implementation unitごとの調査結果、候補class名、full build logを追記しない。
- owner / boundaryが成立したOutcome commitでStateとCurrent couplingを更新する。
- `MIG-05` は probeのerror categoryと対応blockerのclassificationだけを反映し、temporary source / project差分を保存しない。
- package version、replacement、runtime layoutの最終決定とproduction TFM変更はGate後の`.NET 10` migration planに置く。
- blockerをcloseするときは対応するcode / project / test / rehearsal evidenceを使い、完了履歴はcommitに残す。
