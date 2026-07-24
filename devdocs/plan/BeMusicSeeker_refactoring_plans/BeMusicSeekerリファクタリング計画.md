# BeMusicSeeker リファクタリング完了計画

この文書は、リファクタリングの目的、target architecture、完了Gate、順序付きoutcome backlogの正本である。現在位置は[PLAN_STATUS](./PLAN_STATUS.md)、実行方法は[Codex 共通実行ルール](./00_Codex共通実行ルール.md)、移行阻害要因は[.NET 10 migration blocker register](./DOTNET10_MIGRATION_BLOCKERS.md)を参照する。

## 目的

計画を消化することや行数を減らすこと自体は目的ではない。既存behaviorを維持しながらWPF applicationをMVVMのownershipとdependency directionで整理し、残作業を純粋な`.NET 10` / `net10.0-windows`移行として扱える状態にする。

次を完了する。

- `MainWindow`と`MainWindowViewModel`をshell / composition / view-host境界へ整理する。
- application workflow、domain workflow、mutable state、durable writerを機能単位のownerへ置く。
- `BMSLibrary`と`BMSPlaylist`をapplication-facing facade / aggregate entryへ限定する。
- settings、application / dispatcher context、DB、native interop、external process、WPF / WinForms、output layoutを説明可能なadapter / gateway / view-host境界へ閉じる。
- disposableな`.NET 10` rehearsalにより、残失敗がTFM、package、API、runtime、deployment、data migrationへ分類できることを確認する。
- UI observable behavior、失敗契約、DB schema / data、setting key / serialized value、外部ファイル形式、supported external contractを、別途承認された変更なしに変えない。

production designと数値、既存の完了宣言、plannerが作ったunit名が衝突する場合は、production evidenceと本書のowner境界を優先して計画を修正する。

## Release Freeze と互換性

Refactoring Completion Gateを満たすまでrelease作業を凍結する。Gate通過はreleaseの自動許可ではない。`git push`、tag、release、publish、version / release notesのリリース目的変更、配布package作成は、Gate後もユーザーの明示指示まで行わない。

C#の`public` / `protected`修飾子だけでは互換性契約とみなさない。同一repository内のcaller、tests、XAML binding、resource lookup、reflection stringは内部consumerであり、同じimplementation unitで更新して検証できる場合はcall shapeを変更してよい。維持対象はUI observable behavior、失敗契約、persisted data / key / format、および具体的に特定できるsupported out-of-repository contractである。

## 計画単位

| 単位 | 意味 |
|---|---|
| Outcome | Gateを一段進め、利用者またはarchitectureから完了判定できる責務移管。複数commitで構成してよい |
| Execution package | Outcome内の一つの残存境界を閉じる安定したphase。依存順と終了条件を正本に置く |
| Execution anchor | package内の現在phaseを示すdurableな再開点。implementation unitごとには更新しない |
| Active implementation batch | plannerがanchor全体をinventoryして作る、原則2〜4個の有限な実装列。`PLAN_STATUS.md`へmaterializeし、未完unitがある間はplannerを再起動しない |
| Implementation unit | 一つのowner family、user-visible workflow、またはdependency corridorをproduction routeからbehavior testと旧route削除まで閉じる変更単位 |
| Subtask | helper、DTO、rename、signature調整、test fixtureなど。単独のunitやcursorにしない |

commitは内部checkpointであり、outcomeが未完ならユーザーへの応答境界にしない。planner生成のunit名はbatch-localであり、`UI05-R4-A`から`AG`のような長期識別子として正本へ増殖させない。

## 目標アーキテクチャ

### UI shell / view-host

`MainWindow.xaml` / `MainWindow.cs`に残せる責務:

- Window lifecycle、focus、selection、scroll、hit-test、virtualization、visual tree、geometry、drag visual、clipboard、WPF routed event、WinForms host、OS window handle。
- typed immutable presentation request / receiptを受け、dialog、focus、selection、scroll、ContextMenuの`Visibility` / `IsEnabled`などへterminal applyする処理。
- event dataからrequestを作り、一つのfeature command / queryへ委譲するentry point。`async void`はWPF event handlerに限る。
- feature View / UserControlのcomposition。

上記のcode量、event数、typed presentation subscription数だけを理由にView外へ移さない。特にContextMenuのcontrol探索とownerが返したstateのWPF mappingは、feature decisionを持たない限りview-host責務である。

`MainWindowViewModel`に残せる責務:

- application compositionとlifecycle。
- child ViewModel / application serviceの所有とcomposition propertyとしての公開。
- dialog request、progress presentation、UI schedulerの境界。
- feature間の最小限のnavigation / coordination。

rootがchildのleaf property / command / `PropertyChanged`を再公開するbinding relay、feature-local mutable state、feature workflow、private callback hostを持つことは許可しない。broad callback hostとは、child owner / serviceがrootのprivate state、lock、mutable collection、複数private operationをinterfaceやdelegate群として要求するものを指す。typed presentation eventとは区別する。

View / rootに次が残る場合はowner移管が必要である。

- domain decision、複数serviceの順序制御、retry / fallback、durable write、feature state mutation。
- 非eventの`async void`。
- feature ownerへ渡せるのにrootが保持するleaf binding relayやcompatibility pass-through。

### Domain / persistence

`BMSLibrary`はowner composition、application-facing request / query / event facade、複数ownerをまたぐ最小限のlifecycle coordinationに限定する。private state、lock、mutable collection、durable writer、private operationを列挙するhostをfacade縮小とみなさない。

`BMSPlaylist`はapplication-facing facadeとplaylist aggregate entryに限定し、raw SQL / transaction、global dispatcher、`Application.Current`、`Settings.Default`をworkflow convenienceとして直接所有しない。

確立済みのlibrary owner境界は次を維持する。

| Boundary | 所有するもの | 恒久handoff |
|---|---|---|
| `LIB-01` scan core | request lifecycle、enumeration、diff、parse、scan durable commit、progress / failure / terminal result | immutable commit facts / catalog mutation request |
| `LIB-02` package / file operation | package state、install destination、install / repair、file-system orchestration | package snapshot、catalog / maintenance request |
| `LIB-03` catalog / maintenance | canonical runtime catalog、write serialization、durable/live ordering、version、intrinsic projection、maintenance / resource-health | catalog snapshot、mutation request / receipt、catalog-changed facts |
| `LIB-04` LR2 sync | request / reservation / cancel / run / publish、trust / freshness、folder sync | catalog snapshot / lease、LR2 status facts |
| `LIB-05` playlist reference | reference index、table / chart apply、display lookup | table facts、catalog receipt、package snapshot |
| `LIB-06` facade closure | established ownerのcompositionとapplication-facing surface | owner command / query / eventの直接接続 |

cross-owner handoffはimmutable request、snapshot、receipt、event facts、または用途を限定したleaseとする。他ownerのprivate state、lock、callback一覧をcontractにしない。durable stateとlive stateを同時に変える場合は、prepare / validation → durable commit → canonical live apply / version → guard release → receipt publishの順序とfailure atomicityをbehavior testで固定する。

### Configuration / platform / migration

settings load / edit / save / upgrade、application / dispatcher context、base path、managed / native dependency layout、P/Invoke、audio、external process、updater、WPF / WinForms hostは明示したadapter / gateway / view-hostが所有する。直接参照数をゼロにすることではなく、許可boundaryの外へglobal state、platform type、path / process policyが漏れていないことを判定する。

platform closureは`MIG-01`〜`MIG-04`でdependency corridorごとに閉じ、`MIG-05`でtemporary worktree / disposable copyによる`net10.0-windows` restore / build rehearsalを行う。production branchのTFM、package、deploymentはGate後のmigration planで変更する。

## Refactoring Completion Gate

次をすべて満たしたときだけGateを通過できる。

1. **UI ownership**: child ownerがbinding rootとなり、root leaf relay、非event `async void`、feature workflow、broad callback hostがない。code-behindはtyped command / queryへの変換とview-host applyで説明できる。
2. **Library ownership**: canonical writer、serialization、failure atomicity、consumer handoffが明示ownerにあり、facade private state / lockを写すhost、coordinator factory、test-only production seamがない。
3. **Playlist ownership**: persistence / reloadとexternal sync / outputが分離され、aggregate entryがraw SQL / transaction、global application / dispatcher、settings save timingを直接所有しない。
4. **Configuration / platform ownership**: global settings、application context、DB、native、process、UI technologyへの依存が用途を説明できるboundary内にある。
5. **Migration readiness**: blocker registerが`boundary met`、`not applicable`、またはrehearsal固有の`verified`となり、`.NET 10` probeの残失敗がmigration categoryへ分類される。
6. **Structural cohesion**: size triggerを超えるscopeは責務inventory、dependency direction、最大workflowのtestabilityをreview済みである。trigger超過だけでは失敗にせず、trigger未満だけでも通過にしない。
7. **Quality**: build、tests、format / whitespace、analyzer、`git diff --check`、該当UI smoke、outcome / gate reviewが通る。

## Structural size guardrail

行数はarchitectureの代理指標であり、上限値、削減目標、implementation unitの目的ではない。次は責務inventoryを必須にするstructural review triggerとしてだけ使う。

| 対象 | 2026-07-10 baseline | Review trigger |
|---|---:|---:|
| `MainWindowViewModel.cs` root | 23,413 | 8,000超 |
| `MainWindow.cs` | 10,327 | 5,000超 |
| top-level `BMSLibrary*.cs` family | 20,157 | 12,000超 |
| top-level `BMSPlaylist*.cs` family | 10,273 | 6,000超 |

triggerを超えても、残るresponsibility corridorが許可されたshell / facade / aggregate / view-host責務、または一つのownerに属するcohesiveなalgorithm / projection / queryで説明でき、behavior testとdependency directionが適切ならGateを通過してよい。waiverや追加の行数削減unitを要求しない。

次を禁止する。

- 行数、file数、type数を減らすだけのunit。
- cohesiveなtransaction、failure handling、WPF view-host logic、algorithmの数値目的の分断。
- partial split、file move、thin forwarding class、service locator、broad hostで見かけの行数だけを減らすこと。
- ownership、dependency direction、testabilityを改善しないhelper抽出。

## Ordered outcome backlog

| Order | Outcome | Exit condition |
|---:|---|---|
| 1 | `UI-01 Main table presentation and regular chart ownership` | main tableとregular chart owner、child binding、旧relay削除 |
| 2 | `APP-01 Application composition and settings lifecycle boundary` | compositionとsettings lifecycle owner |
| 3 | `UI-02 Playback ownership` | playback state / command / progress owner |
| 4 | `UI-03 Playlist workspace ownership` | playlist tree / workflow / result owner |
| 5 | `UI-04 Play history ownership` | history query / cache / action owner |
| 6 | `LIB-01 Scan pipeline core ownership` | scan core owner |
| 7 | `LIB-03 Catalog storage, mutation, maintenance and resource-health ownership` | canonical catalog / maintenance owner |
| 8 | `LIB-02 Package, install-destination and file-operation ownership` | package / file operation owner |
| 9 | `LIB-04 LR2 synchronization ownership` | LR2 synchronization owner |
| 10 | `LIB-05 Playlist-reference ownership` | playlist reference owner |
| 11 | `LIB-06 Library facade and scan integration closure` | established ownerのdirect composition |
| 12 | `PL-01 Playlist persistence and reload ownership` | playlist repository / reload owner |
| 13 | `PL-02 Playlist external-sync and output ownership` | external sync / output owner |
| 14 | `UI-05 Shell closure` | `UI05-T1`〜`T3`でfinite inventory、grouped residual closure、outcome verificationを完了 |
| 15 | `OWN-01 Residual owner-boundary reconciliation` | pending estimated-install host、playlist custom-folder persistence、実在facade residualを閉じる |
| 16 | `MIG-01 Configuration and application-context closure` | settings / application / dispatcher boundary |
| 17 | `MIG-02 Path, process and updater closure` | path / process / updater gateway |
| 18 | `MIG-03 Native interop and UI-host closure` | native / WPF / WinForms / COM boundary |
| 19 | `MIG-04 Build, dependency and output closure` | project dependency / output policy |
| 20 | `MIG-05 .NET 10 migration rehearsal and handoff` | disposable restore / buildとblocker分類 |
| 21 | `GATE-01 Refactoring completion audit` | 全Gate evidence、Full verification、smoke、review |

## UI-05 terminal execution package

`UI05-R1`〜`R3`と多数の`R4` route移管により、UI-05はopen-endedな「次の未完route探索」ではなくterminal closureへ移る。今後は`UI05-R4-A`〜`AG`の続きを作らず、次の安定した三phaseだけを使う。

### `UI05-T1 Closure inventory and classification`

plannerをanchor入口で一度だけ起動し、現在のroot ViewModel、MainWindow、XAML binding、presentation subscription、test seamを有限集合としてinventoryする。各残件を次へ分類する。

- `BLOCKING`: UI-05 acceptance criteriaを満たすために修正が必要。
- `ALLOWED_BOUNDARY`: shell / composition / view-hostとして残してよい。
- `DEFERRED_OWNER`: `OWN-01`または`MIG-01`〜`MIG-04`の明示ownerへ渡す。owner / blocker IDを付ける。

最初に見つけたcallback、relay、methodだけでinventoryを打ち切らない。T1は単独commitにせず、0〜3個のT2 unitをactive batchへmaterializeする。`BLOCKING`がなければT2をskipしてT3へ進む。

### `UI05-T2 Grouped residual closure`

T1の`BLOCKING`だけを、最大3個のowner-family unitへまとめる。

1. root shell / lifecycle / composition residual。
2. view-host / binding / typed presentation residual。
3. production routeに直接結び付くlegacy seam / test surface residual。

存在しないfamilyは作らない。callback 1個、property 1個、event 1個、source-text test 1個を独立unitにしない。同じowner、同じbehavior、同じverification scopeの全残件を同じunitで閉じる。active batchに未完unitがある間はplannerを再起動しない。

次はそれ自体ではT2 blockerにしない。

- typed immutable presentation eventをMainWindowが受けてdialog / focus / selection / visualへapplyすること。
- ContextMenu control探索とownerが返したstateのWPF mapping。
- WPF event handlerの`async void`。
- `MIG-01`〜`MIG-04`の許可ownerへ明示的に渡したglobal / platform参照。
- structural size trigger超過。

### `UI05-T3 Outcome closure`

T2完了後はplannerを再起動せず、UI-05全体について次を同じclosure cycleで行う。

- acceptance criteriaのcurrent-code audit。
- Full verificationとRelease executableによる該当UI smoke。
- `active outcome base commit..HEAD + frozen worktree`のfresh outcome review。
- 重大指摘の修正、影響範囲の再検証、fresh review。
- `PLAN_STATUS.md`のUI-05 completion、次Outcomeの`ready`、Gate evidence更新を最後のcode unitへ含める。T1で`BLOCKING`が0件かつT3が無修正で通った場合だけ、限定的なoutcome audit/status commitを使う。

review findingを新しいA〜AG系列へ変換しない。T1 inventoryの前提を無効にする新しいproduction evidenceがある場合だけ、差異を限定してplannerを一度再実行する。

## 後続Outcomeの境界

`OWN-01`は次を限定的に閉じる。

1. pending estimated-installのfacade lock / broad host。
2. playlist custom-folder output statusのraw SQL / transaction。
3. 現行codeで確認できるfacade-owned writer、cross-owner lock callback、test-only production seam。

`MIG-01`〜`MIG-04`はdirect reference数を減らす作業ではなく、一つのdependency corridorをproduction callerからadapter / owner、behavior test、旧route削除まで閉じる。既に正しいboundary内にあるWPF / native / DB / process codeは移動しない。

`MIG-05`はproduction branchを変更しないrehearsalである。owner / MVVM redesign不足が見つかった場合はGateへ進まず、最も近い未達Outcomeへ戻す。package / API / runtimeだけの失敗はblocker registerへ分類し、Gate後のmigration planへ渡す。

## Outcome completion rule

各Outcomeは次を満たすまで完了にしない。

- stateとbehaviorのownerが明確で、productionの通常経路がそのownerを使う。
- 担当corridorの旧owner、旧route、旧binding、broad callback host、不要なcompatibility / test seamを削除している。
- cross-owner handoffがimmutable factsまたは用途限定capabilityである。
- private配置ではなくbehaviorを検証するtestsがある。
- outcome開始時よりownership violation、global / platform leak、broad route、migration blockerのいずれかが実質的に減っている。行数だけの減少はevidenceにしない。
- outcome-wide verification、該当UI smoke、fresh outcome reviewが完了している。

完了履歴、過去unit、テスト件数、行数推移はGitへ残し、計画資料にはtarget architecture、現在phase、現行Gate evidenceだけを置く。

## Gate 後

Gate通過後は`MIG-05`の分類結果を入力に、production TFM、package replacement / upgrade、runtime layout、user settings migration、native deployment、updater方式を扱う`.NET 10`移行計画を別途開始する。Gate前のrehearsalは移行可能性を測るprobeであり、実際の移行やreleaseを意味しない。
