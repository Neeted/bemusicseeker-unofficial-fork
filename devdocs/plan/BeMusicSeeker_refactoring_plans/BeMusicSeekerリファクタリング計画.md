# BeMusicSeeker リファクタリング完了計画

この文書は、リファクタリングの目的、完了 Gate、順序付き outcome backlog の正本である。
現在の作業位置は [PLAN_STATUS](./PLAN_STATUS.md)、実行方法は [Codex 共通実行ルール](./00_Codex共通実行ルール.md)、移行阻害要因は [.NET 10 migration blocker register](./DOTNET10_MIGRATION_BLOCKERS.md) を参照する。

## 目的

この計画は完成させること自体が目的ではない。目的は、既存 behavior を維持しながら WPF application を MVVM の ownership と dependency direction で整理し、残作業を純粋な `.NET 10` / `net10.0-windows` 移行として扱える状態にすることである。計画上の数値、Outcome 数、既存の完了宣言が production design と衝突する場合は、設計を数値へ合わせず、production evidence に基づいて計画と判定基準を修正する。

次を完了する。

- `MainWindow` と `MainWindowViewModel` を MVVM の shell / composition / view-host 境界へ縮小する。
- 巨大型に集中した application workflow、domain workflow、状態所有を、機能単位の owner へ移す。
- `BMSLibrary` と `BMSPlaylist` を application-facing facade / aggregate entry へ縮小し、durable writer、lock ownership、consumer-specific workflow を外へ出す。
- settings、application / dispatcher context、DB、native interop、外部 process、WPF / WinForms、出力 layout を明示的な adapter / gateway 境界へ閉じる。
- disposable な `.NET 10` migration rehearsal により、残作業が TFM、package、runtime、deployment、data migration の問題へ限定されたことを実測する。
- 既存の UI observable behavior、失敗契約、DB schema / data、setting key / serialized value、外部ファイル形式、supported external contract を、別途承認された変更なしに変えない。

## Release Freeze と権限

Refactoring Completion Gate を満たすまでリリース作業を凍結する。Gate 通過はリリースの自動許可ではない。

Codex は、各 implementation unit の検証とサブエージェント静的レビュー後に、自発的に commit してよい。一方、次はユーザーの明示指示があるまで禁止する。

- `git push`、Git tag、GitHub Release、Release draft。
- version、release notes、publish / release script のリリース目的変更。
- 配布 package の作成と公開。
- Gate 通過を理由にした自動的なリリース準備。

build / test のローカル artifact と、移行 blocker を解消するための release 関連コードの refactor は許可する。

## 互換性契約と internal surface

C# の `public` / `protected` 修飾子だけでは互換性契約とみなさない。維持対象は UI observable behavior、失敗契約、setting key / serialized value、DB schema / data、外部ファイル形式、および明示的にサポートしている SDK / plugin / CLI / IPC / COM / automation contract とする。

同一リポジトリ内の production caller、test project、XAML binding、resource lookup、reflection string は内部実装 consumer である。全 consumer を同じ implementation unit で更新し、build / test / UI smoke check を通す場合は、public member、nested enum、call shape を変更してよい。test または過去の内部 call shape のためだけに旧 public member、forwarding property、`[Obsolete]` wrapper を残さない。

互換性を理由に escalation する前に、具体的な supported out-of-repository consumer を特定する。特定できなければ内部リファクタリングとして進める。

compatibility-only debt は、該当する production owner を変更する bounded unit で production caller、XAML、tests を新しい call shape へ更新し、不要になった wrapper / forwarding member まで削除する。完了済み outcome は public modifier や過去の内部 call shape だけを理由に再開せず、現行の supported external contract または acceptance criteria の未達が確認された場合にだけ再開する。

## 計画単位

### Outcome

Gate を一段進める、利用者またはアーキテクチャから見て完了を判定できる責務移管。複数 commit で構成してよい。

### Implementation unit

独立して検証・静的レビュー・commit できる変更単位。同じ outcome ID を使い、完了まで連続して進める。commit は内部 checkpoint であり、outcome が未完ならユーザーへの応答を挟まず次 unit へ進む。

### Execution package

一つの残存境界が複数の安全な unit を必要とする場合に、正本が依存順、各 responsibility corridor、残せる baseline residual、最終 retirement unit を定義した実行列。planner は現在の committed code から最初の未完 unit を判定し、そこから 1〜5 unit の実装 sequence を返す。最初の候補が広い場合は package を prepare / durable write / live apply / receipt publish / consumer residual / host retirement の corridor へ再分解し、停止判定にしない。

### Subtask

DTO、result、helper、rename、test fixture、partial split、signature 調整など。これらだけを outcome、ticket、checkpoint、decision record にしない。

計画資料には target architecture、outcome の完了条件、現在の active boundary と依存順を置く。実装単位は production route と旧 owner の削除までを閉じ、checkpoint、helper 数、過去 cursor、完了作業、テスト件数、行数推移は Git の diff / commit で追跡する。

## 目標アーキテクチャ

### UI shell

`MainWindow.xaml` / `MainWindow.cs` に残せる責務:

- Window lifecycle、focus、selection、scroll、hit test、virtualization、visual tree、geometry。
- drag event data、clipboard、WPF routed event、WinForms host、OS window handle など View 固有型の変換と terminal view apply。
- request を組み立てて一つの feature command / query へ委譲する event entry point。`async void` は WPF event handler に限り、複数 service の順序制御、domain decision、retry / fallback、durable write、feature state mutation を処理本体として持たない。
- feature View / UserControl の composition。

`MainWindowViewModel` に残せる責務:

- application composition と lifecycle。
- child ViewModel / application service の所有と公開。
- dialog request、progress presentation、UI scheduler の境界。
- feature 間をまたぐ最小限の navigation / coordination。

root が child ViewModel を composition property として公開することは許可する。root が child の leaf property / command / `PropertyChanged` を再公開する binding relay、feature の mutable state、feature-local workflow、private callback host を持つことは許可しない。XAML / UserControl は可能な限り child owner を binding root とする。

main chart list、playback、playlist workspace、play history、settings、library refresh、package operation の state と workflow は feature owner が持つ。WPF 固有の selection、scroll、hit test、drag visual、virtualization などを行数削減のためだけに ViewModel / service へ移さない。

main table 周辺は、次の三つの ownership boundary を混ぜない。

- **Main table presentation owner**: 現在 rows、column presentation、selection、table sort interaction、atomic apply、旧 rows の dispose、PropertyChanged と適用完了通知を持つ。
- **Mode workflow owner**: request identity、source / query / filter / sort の意味、cache、cancellation、freshness、presentation result の生成を mode ごとに持つ。regular chart は UI-01、playlist workspace と play history の workflow はそれぞれ UI-03 / UI-04 の owner が最終的に持つ。
- **Shell / composition**: 現在 mode の選択、mode owner と table presentation owner の接続、Window 固有 event の request 変換、feature 間の最小限の coordination だけを持つ。

mode workflow owner は immutable な presentation result を生成し、table presentation owner が terminal apply を atomic に行う。UI-01 は playlist / play-history workflow 全体を MainChartList へ吸収せず、現 owner から result を受ける production contract と table 側の適用責務までを閉じる。

### Domain

`BMSLibrary` は現在の production composition に必要な application-facing facade、event surface、依存の composition、複数 owner をまたぐ最小限の coordination に限定する。過去の public surface の維持を目的にしない。facade private method、lock、mutable collection、durable writer を列挙する broad host は facade 縮小とみなさない。scan core、package / install destination、library file operation、catalog storage / mutation、maintenance / resource health、LR2 song.db sync、playlist reference は、下記の owner 境界へ分離する。

`BMSPlaylist` は現在の production composition に必要な application-facing facade と playlist aggregate entry に限定し、過去の public surface の維持を目的にしない。aggregate entry は raw SQL、transaction、global dispatcher、`Application.Current`、`Settings.Default` を workflow convenience として直接所有しない。persistence / reload、external sync、custom-folder output、BMT export、recommended-table build、operation notification は独立して検証できる owner へ移す。

新しい owner は `Window`、`Control`、`MessageBox`、`Settings.Default`、`NLog`、無制御な DB connection / transaction、無制御な `Dispatcher` を直接参照しない。必要な能力は用途別の小さな contract として受け取る。

active outcome の成立に不可欠な platform / composition contract は、production 経路へ即座に接続する最小単位に限り後続 outcome から前倒ししてよい。未使用 interface、総合 facade、将来用の state 保存は追加しない。

### Library owner 境界と依存順

次の contract 名は責務を示す概念名であり、将来の C# type 名を固定しない。

| Boundary | 同じ境界が所有する state / behavior | 恒久的な handoff | 所有しないもの |
|---|---|---|---|
| `LIB-01 Scan pipeline core` | scan request generation、overlap / cancel / scan-request freshness、列挙、file diff、parse、durable DB commit、progress / failure / terminal result | immutable な scan result / commit facts と catalog mutation request | package overlay、共有 catalog の in-memory apply、maintenance / resource-health、LR2 freshness |
| `LIB-02 Package, install-destination and file-operation owners` | pending / installed package state、install destination overlay / cache、install / repair / force-install、installable maintenance の queue / mode commit / package target selection / progress、merge / move / rename / delete の orchestration、file-system side effect | immutable な install-destination / package-target snapshot、cleanup target、catalog mutation request と maintenance request | catalog rows / owned collection、maintenance-table durable write、resource-health index、LR2 status |
| `LIB-03 Catalog storage, mutation, maintenance and resource-health owners` | session BMS / bmson catalog rows、owned chart collection、digest / owned-hash / parent-folder / duplicate / resource / directory / chart-info index、version、non-scan runtime write serialization、mutation ordering / failure atomicity、maintenance-table hydration、chart-info hydration / backfill、catalog-owned maintenance、resource-health input / index / rebuild | immutable な catalog snapshot、catalog mutation request / receipt、catalog-changed notification | scan parse / durable commit、package、LR2、playlist の consumer-specific cache / status |
| `LIB-04 LR2 synchronization owner` | request identity、schedule / cancel / run / publish、status、LR2 run reservation、scan-surface、trust / freshness、normal-folder / LR2-folder sync、app-managed output scope | catalog snapshot / mutation lease を入力にした LR2 status / freshness snapshot | catalog の canonical state、package state、playlist reference |
| `LIB-05 Playlist-reference owner` | reference maps / index、table replace / remove / synchronize、owned / pending / installed chart への reference apply、display lookup | table change、catalog mutation receipt、package snapshot | playlist persistence、LR2 sync、catalog mutation |
| `BMSLibrary` / `LIB-06` | owner composition、application-facing request / query / event facade、複数 owner をまたぐ最小限の lifecycle coordination | 上記 owner の command / query / event を直接接続 | workflow private state、private state を写す nested host、coordinator factory |

cross-owner handoff は次の規則に従う。

1. handoff は immutable な request、snapshot、receipt、event facts、または用途を限定し相手 owner の private state を露出しない mutation lease とする。相手 owner の private method を列挙する callback host にしない。
2. runtime catalog mutation の canonical writer と write serialization は `LIB-03` に一つだけ置く。scan、package、file operation、LR2 は catalog state を直接書かず、mutation request または mutation lease を使う。LR2 固有の request / run reservation は `LIB-04` が所有し、catalog write 自体は `LIB-03` の lease に従う。
3. catalog mutation receipt は added / removed / moved identity、affected path / hash、version、initialization kind など canonical facts だけを持つ。install-destination、LR2 freshness、playlist summary / reference など consumer 固有の mutation plan を一つの巨大 result に束ねず、各 owner が receipt から自分の state を更新する。
4. owner は他 owner の lock、version field、mutable collection を直接取得しない。必要な読み取りは snapshot、書き込みは command / lease、通知は event facts を使う。
5. durable DB と live catalog を同時に変える mutation は、immutable request の prepare / validation → catalog guard 下の durable transaction → canonical live apply / version update → guard release → immutable receipt / canonical event publish の順で行う。durable failure 時は live catalog と consumer residual を変更しない。既存の owned-collection version / PropertyChanged timing は behavior test で維持し、必要なら internal version reservation と public catalog-changed event を分離する。
6. package、LR2、playlist、UI など consumer 固有の residual apply は receipt publish 後に行い、catalog guard を保持したまま別 owner の callback、lock、mutable state へ入らない。既存の外側 operation lock は担当 outcome まで残してよいが、catalog owner の guard と循環取得させない。
7. owner family は複数の協調 class に分けてよい。一つの `BMSLibrary` を別名の巨大 class へ移すことを完了としない。
8. 先行 owner が恒久的な receipt / event facts を先に公開し、後続 consumer owner がまだない場合は、`BMSLibrary` の composition が既存 consumer 処理へ一時的に接続してよい。receipt に consumer 固有 state を混ぜず、新しい broad host を作らず、該当 consumer outcome でその接続を直接 owner へ移して削除する。

`LIB-01` が所有する scan parse / durable commit transaction は `LIB-03` の non-scan runtime writer と競合させない。scan core は song / bmson / chart-info / inline-maintenance の commit を完了して immutable committed-row / maintenance facts を発行し、`LIB-03` が session catalog、chart-info index、maintenance attach / resource-health を適用する。scan core は `LIB-03` owner の mutable state や lock を直接書かず、`LIB-03` は scan transaction を二重に永続化しない。

現在一つの mutation dispatch に束ねられている derived projection / notification は、次の owner へ分ける。

| Derived concern | Owner / handoff |
|---|---|
| owned collection version、digest / owned-hash snapshot、parent-folder、duplicate、directory-resource lookup、catalog-intrinsic index | `LIB-03` |
| install destination、installed lookup、install estimation metadata、pending / installed package path | `LIB-02` |
| catalog-owned maintenance state / warning と、owned / package target を入力にする resource-health index / projection | `LIB-03` |
| installable maintenance の queue / mode / progress と package-local status | `LIB-02` が package-target snapshot / maintenance result を使う |
| LR2 normal-folder output、trust / freshness / status | `LIB-04` |
| playlist reference / resolve display | `LIB-05` |
| playlist summary result cache / reload decision | `PL-01` が `LIB-03` の versioned owned-hash snapshot / catalog mutation receipt を消費 |
| normal library refresh | `LIB-03` が canonical catalog-changed event を発行し、shell 側の表示接続は `UI-05` で閉じる |

完了済み library outcome の implementation sequence、temporary bridge、個別 host / helper の退役履歴は Git に残す。総合計画では上記 owner boundary、handoff、mutation ordering と Gate criteria を恒久的な設計契約として維持する。

### Configuration と platform boundary

settings の load / edit / save / upgrade、application / dispatcher context、application base directory、managed / native dependency layout、P/Invoke、audio、external process、updater、WPF / WinForms host は明示した adapter / gateway が所有する。application / domain workflow は snapshot または用途別 interface を受け取る。直接参照数をゼロにすること自体を目的にせず、直接参照を許可した boundary の外へ global / platform type が漏れていないことを判定する。

`APP-01 Application composition and settings lifecycle boundary` の完了境界は application composition と settings の load / edit / save / upgrade lifecycle までとする。feature 内に残る `Settings.Default` や global-backed convenience path の除去は、各 feature outcome と `MIG-01` の責務であり、APP-01 の完了を取り消す理由にしない。APP-01 は再開しない。

platform closure は一つの巨大 Outcome にせず、次の依存順で閉じる。

1. `MIG-01 Configuration and application-context closure`: settings、`Application.Current`、`DispatcherHelper`、UI scheduler / application lifetime の依存方向。
2. `MIG-02 Path, process and updater closure`: base path、assembly location、player / explorer / browser / restart、updater request。
3. `MIG-03 Native interop and UI-host closure`: P/Invoke、manual native load、audio / Everything、WPF / WinForms / WebBrowser host。
4. `MIG-04 Build, dependency and output closure`: HintPath DLL、managed/native copy、probing、updater project、`System.Deployment` の実使用判断。
5. `MIG-05 .NET 10 migration rehearsal and handoff`: clean な disposable worktree / copy で `net10.0-windows` restore / build を試し、残失敗を blocker ID へ分類する。probe 変更は production branch へ commit しない。

### Residual ownership reconciliation

完了済み Outcome の主 corridor が成立していても、現行 production code に Gate 違反の residual が残っている場合は、行数削減や過去 Outcome の全面再開ではなく `OWN-01 Residual owner-boundary reconciliation` で限定的に閉じる。

`OWN-01` の execution package は次の順とする。

1. `OWN01-A Pending estimated-install corridor`: `IPendingEstimatedInstallHost` 相当の facade lock / private callback host を廃止し、package owner、catalog mutation owner、maintenance / resource-health ownerを immutable request / receipt /用途別 capability で直接接続する。
2. `OWN01-B Playlist custom-folder persistence corridor`: `playlist_custom_folder_output_status` の raw SQL / transaction と output status lifecycle を repository / output ownerへ移し、`BMSPlaylist` を aggregate entry / composition に戻す。
3. `OWN01-C Facade residual closure`: `BMSLibrary` / `BMSPlaylist` の broad host、facade-owned durable writer、cross-owner lock callback、test-only production seam を bounded audit し、実在する残件だけを同じ unit で削除して DB / concurrency blocker を再分類する。

`OWN-01` は cohesive な parsing、projection、query algorithm、View 固有処理を行数のためだけに移動しない。既存 Outcome の責務境界を破壊する新しい総合 owner も作らない。

## Refactoring Completion Gate

次をすべて満たしたときだけ Gate を通過できる。この Gate は「ファイルが小さいこと」ではなく、MVVM ownership、dependency direction、testability、migration boundary が成立したことを判定する。

1. UI ownership
   - feature View / UserControl が child owner を binding root とし、root は child ViewModel の composition property を除いて leaf binding relay を持たない。
   - code-behind の `async void` は WPF event entry point に限られ、処理本体は一つの feature command / query への委譲と View 固有 apply だけで説明できる。
   - root ViewModel に非 event の `async void`、feature-local mutable state、feature workflow、private callback host が残っていない。
   - focus、selection、scroll、hit test、virtualization、drag visual、WinForms / OS handle など View 固有処理は View / view-host に残してよい。
2. Library ownership
   - 列挙した library workflow が明示的 owner を持ち、state と behavior が同じ境界にある。
   - catalog mutation の canonical writer、write serialization、failure atomicity が一つの owner 境界にあり、他 owner は request / snapshot / receipt / lease で接続される。
   - consumer 固有の cache / freshness / reference 更新を一つの巨大 mutation result に束ねていない。
   - `BMSLibrary` の private state、lock、private operation を写す broad host interface、coordinator factory、test 専用 production seam が残っていない。
3. Playlist ownership
   - persistence / reload と external sync / output が `BMSPlaylist` から分離され、単体で検証できる。
   - aggregate entry が raw SQL / transaction、global dispatcher / application、settings save timingを直接所有しない。
4. Configuration / platform ownership
   - `Settings.Default`、`Application.Current` / dispatcher、DB、native、external process、UI technology への直接依存が、用途を説明できる adapter / gateway / view-host に限定される。
   - direct reference 数がゼロであることではなく、許可 boundary の外へ platform type、global state、path policy、process launch が漏れていないことを確認する。
5. Migration readiness
   - [.NET 10 migration blocker register](./DOTNET10_MIGRATION_BLOCKERS.md) の各項目が `boundary met`、`not applicable`、または rehearsal 固有の `verified` である。
   - disposable な `net10.0-windows` restore / build rehearsal を実行し、残失敗が package、API、TFM、runtime layout、deployment、data migration のいずれかとして blocker に分類されている。
   - blocker 解消のために巨大型内部の workflow 分割、owner 未確定、root ViewModel の feature redesign を必要としない。
6. Structural cohesion / size
   - [Structural size guardrail](#structural-size-guardrail) に従い、trigger を超える scope は責務 inventory と最大 workflow / dependency / testability の review を完了している。
   - trigger 超過だけでは失敗にせず、trigger 未満だけでも通過にしない。
7. Quality
   - build、test、format / whitespace、`git diff --check` が通る。analyzer が正常終了し、今回差分による warning が増えていない。
   - outcome 全体のサブエージェント静的レビューで重大な指摘がない。

## Structural size guardrail

従来の Size guardrail を、行数上限ではなく structural review trigger として再定義する。行数は architecture の代理指標であり、Gate の上限値、削減目標、implementation unit の目的ではない。次の数値は、巨大 scope に未分離の責務が残っていないかを必ず調べる **structural review trigger** として維持する。

| 対象 | 2026-07-10 baseline | Structural review trigger |
|---|---:|---:|
| `MainWindowViewModel.cs` root | 23,413 | 8,000 超 |
| `MainWindow.cs` | 10,327 | 5,000 超 |
| top-level `BMSLibrary*.cs` family | 20,157 | 12,000 超 |
| top-level `BMSPlaylist*.cs` family | 10,273 | 6,000 超 |

trigger を超える場合、Gate review は少なくとも次を確認する。

- 残る各 responsibility corridor が、許可された shell / facade / aggregate / view-host 責務、または明示した owner の cohesive な algorithm / projection / query で説明できる。
- feature-local state / workflow、raw durable writer、cross-owner lock、global / platform dependency、root leaf relay、broad callback host が混在していない。
- 最大 workflow を behavior test で検証でき、依存方向が inward で、変更理由が一つの owner にまとまっている。
- partial、nested type、別 host file、forwarding service へ移しただけのコードを「削減」と数えない。root の責務のままなら structural scope に含めて評価する。

上記を満たす cohesive な scope は trigger を超えたまま Gate を通過してよく、waiver や追加の行数削減 unit を要求しない。反対に、trigger 未満でも ownership 違反があれば Gate を通過しない。

次を禁止する。

- 「行数を減らす」だけを acceptance criteria にした unit。
- cohesive な transaction、failure handling、WPF view-host logic、algorithm を数値のために分断すること。
- partial split、type 移動、薄い forwarding class、service locator、broad host の追加で見かけの行数だけを減らすこと。
- testability、dependency direction、state / behavior ownershipを改善しない helper 抽出。

baseline / trigger は履歴として更新せず、Gate audit 時は実ソースから再計測する。数値の推移は計画資料へ記録せず Git と review evidence に残す。

## Ordered outcome backlog

Codex は [PLAN_STATUS](./PLAN_STATUS.md) の active outcome を進める。active outcome が未指定なら、次の順で最初の `ready` outcome を選ぶ。Outcome ID は既存参照を保つ識別子であり、実行順は下表の `Order` を正本とする。将来 outcome の class / interface / method 名は着手時まで固定しないが、owner 境界、handoff direction、依存順、residual retirement outcome は変更しない。

| Order | Outcome | Exit condition |
|---:|---|---|
| 1 | `UI-01 Main table presentation and regular chart ownership` | main table presentation と regular chart workflow の owner が閉じ、playlist / play-history owner からの result contract が production 接続され、XAML の child binding と root relay / callback host 削除が完了する |
| 2 | `APP-01 Application composition and settings lifecycle boundary` | application composition と settings load / edit / save / upgrade lifecycle が明示的 owner を通る。feature 内の global settings 除去は各 feature outcome / MIG-01 で閉じ、APP-01 は再開しない |
| 3 | `UI-02 Playback ownership` | playback state / command / progress が child View / ViewModel と player adapter に移り、code-behind は view-host 操作だけになる |
| 4 | `UI-03 Playlist workspace ownership` | tree、source query / cache / cancellation、detail / summary result generation、feature-local interaction、drag-drop、reload / edit workflow が workspace owner に移り、result の main-table terminal apply を除く root / code-behind の playlist workflow がなくなる |
| 5 | `UI-04 Play history ownership` | source query / cache / cancellation、filter、result generation、selected row の feature-local action interpretation が feature owner に移り、main-table selection state / terminal apply を除く root relay と UI workflow がなくなる |
| 6 | `LIB-01 Scan pipeline core ownership` | scan request lifecycle、列挙、file diff、parse、durable commit、progress / failure / terminal result が pipeline owner にあり、後続 owner の state を scan core の責務に含めず、再基準化監査が完了する |
| 7 | `LIB-03 Catalog storage, mutation, maintenance and resource-health ownership` | runtime catalog state の canonical writer と mutation serialization、storage apply、owned collection、maintenance-table / chart-info hydration、chart-info backfill、catalog-owned maintenance / resource-health が bounded owner family に移る。generic mutation は prepare → durable commit → canonical live apply → guard release → canonical receipt publish を通る |
| 8 | `LIB-02 Package, install-destination and file-operation ownership` | package / install-destination owner と library file-operation owner が `LIB-03` の catalog contract を使う production route を持ち、scan は immutable cleanup snapshot を直接受け取る |
| 9 | `LIB-04 LR2 synchronization ownership` | input build、request / schedule / cancellation、LR2 run reservation、run / publish、status、scan-surface、trust / freshness、folder sync が `LIB-03` の catalog contract を使う LR2 owner に移る |
| 10 | `LIB-05 Playlist-reference ownership` | reference maps / index / display と table / chart apply が playlist-reference owner に移り、catalog mutation receipt と package / table change を直接受ける |
| 11 | `LIB-06 Library facade and scan integration closure` | established owner を production composition で直接接続し、scan nested host、storage / mutation factory、当時の残存 facade private workflow / forwarding / test seam を削除する |
| 12 | `PL-01 Playlist persistence and reload ownership` | DB transaction、hydration、diff、reload decision、catalog-change による playlist aggregate / summary invalidation が repository / workflow owner に移る |
| 13 | `PL-02 Playlist external-sync and output ownership` | HTTP sync、custom-folder、BMT、recommended-table output が個別に検証できる owner へ移る |
| 14 | `UI-05 Shell closure` | 下記 `UI05-R1`〜`R4` を閉じ、root leaf relay、非 event `async void`、feature workflow を除去し、View 固有処理だけを view-host に残す |
| 15 | `OWN-01 Residual owner-boundary reconciliation` | pending estimated-install broad host、playlist custom-folder status の facade-owned persistence、実在する facade residual を `OWN01-A`〜`C` で閉じ、DB / concurrency Gate を満たす |
| 16 | `MIG-01 Configuration and application-context closure` | global settings、application lifetime、dispatcher / scheduler の直接依存が用途別 store / context / port または View boundary に限定される |
| 17 | `MIG-02 Path, process and updater closure` | base path、assembly location、external player / explorer / browser / restart、updaterが用途別 policy / gateway を通る |
| 18 | `MIG-03 Native interop and UI-host closure` | P/Invoke、manual native load、audio / Everything、WPF / WinForms / WebBrowser 型が platform adapter / view-host 内に限定される |
| 19 | `MIG-04 Build, dependency and output closure` | HintPath DLL、managed/native relocation、probing、updater project、deployment reference の owner / output policy が project boundary で説明できる |
| 20 | `MIG-05 .NET 10 migration rehearsal and handoff` | disposable probe で `net10.0-windows` restore / build を実行し、残失敗を blocker register へ分類し、MVVM / owner 再設計が残っていないことを確認する |
| 21 | `GATE-01 Refactoring completion audit` | 全 Gate evidence、structural review、Full verification、UI smoke、静的レビューを確認し、残作業を実装可能な `.NET 10` migration plan に引き渡せる |

### UI-05 remaining execution package

`UI-05` の残りは次の corridor で進める。planner は現行 HEAD で完了済みの route を再実装せず、最初の未完 production route から開始する。各 corridor が複数 workflow を含む場合は 1〜5 個の vertical unit に分解する。

1. `UI05-R1 Playlist presentation / view-host residual`: playlist tree / table / summary の selection、context action、dialog、drag-drop のうち feature decision / multi-step workflow を workspace / application ownerへ移し、View は hit-test、selection、drag data、terminal view apply へ限定する。
2. `UI05-R2 Library, package and chart-action residual`: scan / maintenance / duplicate / package / file / external-action の確認、request generation、progress / cancellation、結果処理を用途別 ownerへ移し、code-behind は一つの command 呼び出しへ縮小する。
3. `UI05-R3 Shell lifecycle, progress and async boundary`: root ViewModel の非 event `async void` を `Task` / command / lifecycle ownerへ置換し、startup / reload / shutdown、progress / dialog / scheduler の責務を許可 shell boundaryへ揃える。
4. `UI05-R4 Binding and composition closure`: child binding root、root leaf relay、legacy pass-through、callback host、test-only production seam を監査・削除し、Full verification、Release executable smoke、outcome reviewまで閉じる。

### Residual and migration execution policy

`OWN-01` と `MIG-01`〜`MIG-04` は direct reference 数や行数をゼロへ近づける作業ではない。各 unit は一つの dependency corridor を production caller から adapter / owner、behavior test、旧 route 削除まで閉じる。既に正しい boundary 内にある WPF / native / DB / process codeは移動しない。

`MIG-05` は migration rehearsal であり、production branch の TFM / package / deployment を変更しない。probe で owner / MVVM redesign の不足が見つかった場合は `GATE-01` へ進まず、最も近い未達 Outcome を active にして bounded implementation sequence へ戻す。package / API / runtime だけの失敗は blocker registerへ残し、Gate 後の migration planへ渡す。

internal owner dependency、複数 caller、broad host、lock / transaction の複雑性により最初の候補が閉じない場合は、bridge / adapter を追加せず、execution package と responsibility corridor へ再分解して継続する。`blocked` はユーザー入力または外部状態変更なしに解消できない `EXTERNAL_BLOCKER` にだけ使う。

## Outcome completion rule

各 outcome は次を満たすまで完了にしない。

- state と behavior の新 owner が明確である。
- production の通常経路が新 owner を使う。
- 旧 owner、旧 binding、旧 callback / host、不要な compatibility path を削除している。
- cross-owner handoff が immutable request / snapshot / receipt / event facts / lease であり、相手 owner の private state や callback 一覧を contract にしていない。
- private 実装配置ではなく behavior を検証する test がある。
- outcome 開始時より ownership violation、global / platform leak、broad route、migration blocker のいずれかが実質的に減っている。行数だけの減少は evidence にしない。
- outcome 全体の検証と静的レビューが完了している。

implementation unit は seam や private helper の個数ではなく、1 つの user-visible workflow、ownership boundary、dependency corridor、または broad route を横断する responsibility corridor を production 経路から behavior test まで閉じる大きさにする。移管した corridor の旧 writer / callback / binding / seam は同じ unit で削除する。正本に named retirement unit がある broad host と他 corridor の residual は、surface を増やさず各 unit で減らす限り残してよい。新 owner と production route、旧 route 削除、behavior test を同じ unit で閉じる。

行数削減を planner の unit objective、acceptance criteria、review finding にしない。trigger を超える scope は responsibility inventory を行い、ownership violation が見つかった corridorだけを実装 unit にする。cohesive な algorithm、transaction、View 固有処理を分けても ownership、dependency direction、testabilityが改善しない場合は分割しない。

plan rebaseline audit、`MIG-05`、`GATE-01` の audit/status commit は [Codex 共通実行ルール](./00_Codex共通実行ルール.md) の限定条件に従う。consumer 固有の invalidation / freshness / presentation 更新は各 owner に置く。

UI outcome の完了時は、移管済み child owner から `Application.Current`、`DispatcherHelper.UIDispatcher`、`MainWindowViewModel` の nested contract、`*ForTest` production method、root PropertyChanged leaf relay、root callback / workflow host がなくなっていることを検索と静的レビューで確認する。ただし View / view-host が ownership を持つ WPF lifecycle、selection、focus、scroll、hit-test、virtualization、drag visual、OS handle は残してよい。例外は「行数が多いから」ではなく owner と dependency direction で説明する。

`OWN-01` の完了時は、少なくとも pending estimated-install route が facade の lock / private callback hostを要求せず、playlist custom-folder output status の raw SQL / transaction が aggregate entry の外にあり、DB failure / live state / notification ordering の behavior testがあることを確認する。

`MIG-01`〜`MIG-04` の完了時は direct reference 数ではなく、各 call site が許可 boundary内にあること、workflow contractが framework typeを漏らさないこと、旧global / path / process / loader routeが削除されたことを確認する。

完了結果の履歴は Git commit に残し、計画資料には outcome の状態と現在の Gate evidenceだけを記録する。

UI-03 の既存完了境界は次を維持する。

- `PlaylistWorkspaceViewModel` の global-backed convenience constructor、optional provider fallback、暗黙の current-settings provider を production seam として残さない。production composition と tests は必要な provider / port を明示的に渡す。
- `IPlaylistPropertySaveInteraction` のように多数の root callback を束ねる広い host を残さない。workflow state / behavior は workspace ownerへ収め、shell coordination または domain capability が必要な箇所だけを用途別の細い port に分ける。

## Gate 後

Gate 通過後は、`MIG-05` の分類結果を入力に `.NET 10` 移行を別計画として開始する。production branch の TFM、package replacement / upgrade、runtime layout、user settings migration、native deployment、updater方式をそこで実装する。

Gate 前の rehearsal は移行可能性を測るための disposable probe であり、実際の移行、package 一括更新、配布物変更を意味しない。リリース作業は、Gate 通過後もユーザーの明示指示なしには開始しない。
