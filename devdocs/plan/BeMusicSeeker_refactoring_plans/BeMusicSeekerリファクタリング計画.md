# BeMusicSeeker リファクタリング完了計画

この文書は、リファクタリングの目的、完了 Gate、順序付き outcome backlog の正本である。
現在の作業位置は [PLAN_STATUS](./PLAN_STATUS.md)、実行方法は [Codex 共通実行ルール](./00_Codex共通実行ルール.md)、移行阻害要因は [.NET 10 migration blocker register](./DOTNET10_MIGRATION_BLOCKERS.md) を参照する。

## 目的

次を完了し、その後の課題を純粋な `.NET 10` / `net10.0-windows` 移行として扱える状態にする。

- `MainWindow` と `MainWindowViewModel` を MVVM の shell / composition / view-host 境界へ縮小する。
- 巨大型に集中した application workflow、domain workflow、状態所有を、機能単位の owner へ移す。
- `BMSLibrary` と `BMSPlaylist` を application-facing facade / aggregate entry へ縮小する。
- settings、DB、native interop、外部 process、WPF / WinForms、出力 layout を明示的な adapter / gateway 境界へ閉じる。
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

compatibility-only debt は一括 public cleanup にせず、production caller、XAML、tests の更新と旧 wrapper 削除までを閉じる bounded unit で整理する。最初の対象は `BMSPlaylist.GetCustomFolderOutputDirectory(BMSTable)` の test-only / `Settings.Default`-backed overload とし、tests は明示的 settings 引数を取る overload または snapshot を使う。

UI-01 の完了状態は public surface の変更だけを理由に再開しない。次の候補は production / XAML consumer と supported external consumer を監査し、外部 consumer がなく tests または root 内部だけで使われる場合に `ChartListSortParameters`、`ChartModeFilter`、`ChartFilters` へ置換して削除する。

- `MainWindowViewModel.cSortParameters`
- `MainWindowViewModel.ModeFilterType`
- `MainWindowViewModel.ModeFilter`
- `MainWindowViewModel.KeywordFilter`
- `ToCompatibilitySortParameters`

`PlaylistFilterType` は UI-03 で canonical feature type へ統合してよく、外部互換性を理由に残さない。

## 計画単位

### Outcome

Gate を一段進める、利用者またはアーキテクチャから見て完了を判定できる責務移管。複数 commit で構成してよい。

### Implementation unit

独立して検証・静的レビュー・commit できる変更単位。同じ outcome ID を使い、完了まで連続して進める。

### Subtask

DTO、result、helper、rename、test fixture、partial split、signature 調整など。これらだけを outcome、ticket、checkpoint、decision record にしない。

次は禁止する。

- production code を変えず「次の 1 seam」を選ぶ checkpoint。
- private helper や reflection test の残数を起点にした無期限の ticket 生成。
- 旧 owner / 旧経路を残したまま coordinator、host、adapter だけを成果とすること。
- Git 履歴で確認できる完了作業、テスト件数、行数推移を計画資料へ追記すること。

## 目標アーキテクチャ

### UI shell

`MainWindow.xaml` / `MainWindow.cs` に残せる責務:

- Window lifecycle、focus、selection、scroll、hit test。
- WPF / WinForms host、OS window handle、View 固有型を扱う薄い adapter。
- command / request へ即時委譲する event entry point。
- feature View / UserControl の composition。

`MainWindowViewModel` に残せる責務:

- application composition と lifecycle。
- child ViewModel / application service の所有。
- dialog request、progress、UI thread の境界。
- feature 間をまたぐ最小限の navigation / coordination。

main chart list、playback、playlist workspace、play history、settings、library refresh、package operation の state と workflow は feature owner が持つ。root の binding relay、PropertyChanged 再中継、private workflow host は完了後に削除する。

main table 周辺は、次の三つの ownership boundary を混ぜない。

- **Main table presentation owner**: 現在 rows、column presentation、selection、table sort interaction、atomic apply、旧 rows の dispose、PropertyChanged と適用完了通知を持つ。
- **Mode workflow owner**: request identity、source / query / filter / sort の意味、cache、cancellation、freshness、presentation result の生成を mode ごとに持つ。regular chart は UI-01、playlist workspace と play history の workflow はそれぞれ UI-03 / UI-04 の owner が最終的に持つ。
- **Shell / composition**: 現在 mode の選択、mode owner と table presentation owner の接続、Window 固有 event の request 変換、feature 間の最小限の coordination だけを持つ。

mode workflow owner は immutable な presentation result を生成し、table presentation owner が terminal apply を atomic に行う。UI-01 は playlist / play-history workflow 全体を MainChartList へ吸収せず、現 owner から result を受ける production contract と table 側の適用責務までを閉じる。

### Domain

`BMSLibrary` は現在の production composition に必要な application-facing facade、event surface、依存の composition、複数 owner をまたぐ最小限の coordination に限定する。過去の public surface の維持を目的にしない。scan core、package / install destination、library file operation、catalog storage / mutation、maintenance / resource health、LR2 song.db sync、playlist reference は、下記の owner 境界へ分離する。

`BMSPlaylist` は現在の production composition に必要な application-facing facade と playlist aggregate entry に限定し、過去の public surface の維持を目的にしない。persistence / reload、external sync、custom-folder output、BMT export、recommended-table build、operation notification は独立して検証できる owner へ移す。

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
5. owner family は複数の協調 class に分けてよい。一つの `BMSLibrary` を別名の巨大 class へ移すことを完了としない。
6. 先行 owner が恒久的な receipt / event facts を先に公開し、後続 consumer owner がまだない場合は、`BMSLibrary` の composition が既存 consumer 処理へ一時的に接続してよい。receipt に consumer 固有 state を混ぜず、新しい broad host を作らず、該当 consumer outcome でその接続を直接 owner へ移して削除する。

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

`b6f2ef7e` を library ownership 再編時の bridge baseline とする。この snapshot に既に存在する cross-owner bridge は、下表の retirement outcome まで一時的に残してよいが、member、factory、callback category を追加または拡張しない。上記 handoff 規則と completion rule の cross-owner handoff 条件は新規または恒久 contract と各 retirement outcome の完了判定に適用し、下表の baseline bridge だけは担当 outcome までの限定例外として先行 outcome の完了を妨げない。新 owner を接続できた category は同じ outcome 内で facade forwarding と旧 route を削除する。

| Residual bridge category | Production owner を成立させる outcome | 削除対象 |
|---|---|---|
| current install destination cleanup、pending / installed package overlay、installable maintenance queue / mode commit / package target selection / progress、package / file-operation orchestration | `LIB-02` | scan host の package callback、installable maintenance host / queue state、package / file-operation host 群の facade-owned state forwarding |
| storage rows、owned collection、non-scan runtime maintenance-table durable write、resource-health input / index、catalog write serialization / mutation apply / dispatch、post-scan maintenance、maintenance-table hydration、chart-info hydration / backfill | `LIB-03` | scan storage host / coordinator factory、mutation-delta broad host、installed-target apply host、maintenance hydration apply / dispatch host、chart-info queue / workflow state、resource-health state forwarding / rebuild / mutation host |
| LR2 folder diff / normal-folder sync、LR2 request / run reservation、scan-surface、trust / freshness、request / run / status | `LIB-04` | scan LR2 host、scan host の LR2 callback、LR2-specific mutation-block callback、LR2 request / status host と facade-owned mutable state |
| playlist reference maps / apply / display | `LIB-05` | playlist-reference host、facade-owned manager / apply workflow、catalog mutation fan-out 内の reference-specific branch |
| progress / dialog / diagnostics / DB gateway の残る scan composition と、上記削除後に空になる nested host / factory | `LIB-06` | `LibraryFileScanPipelineHost` 相当の broad host、残存 nested host / forwarding、test-only production seam |

実装順は `LIB-01` の再基準化監査を閉じた後、canonical catalog writer を先に成立させるため `LIB-03` → `LIB-02` → `LIB-04` → `LIB-05` → `LIB-06` とする。`LIB-02` の package / file operation と `LIB-04` の LR2 sync は、`LIB-03` の catalog snapshot / mutation request / receipt / lease を恒久 contract として使う。後続 owner が未成立のため vertical unit を作れない状態は external blocker として扱わず、不要な adapter を追加せずに prerequisite outcome を進める。

library ownership phase の slicing は workflow / state boundary で行う。

- `LIB-03`: canonical runtime catalog state を移す unit では、その state の production writer、write lock / version、failure fallback を同じ unit で owner へ移し、scan、generic mutation、installed-target apply、maintenance / resource-health の該当 production route から旧 writer を削除する。interface / receipt だけを先に追加せず、下記の残存責務の実行境界に従って各 category の旧 host を削除する。
- `LIB-02`: package / install-destination workflow と generic library file-operation workflow は別の vertical unit に分けてよいが、各 unit は file-system side effect、`LIB-03` への mutation request、state apply、failure / notification までを閉じる。host 一個ずつの移動にしない。
- `LIB-04`: request / schedule / cancellation / run status と trust / freshness / folder sync を同じ owner family に置き、scan commit facts と `LIB-03` の catalog lease を production 接続した unit で対応する LR2 callback を削除する。
- `LIB-05`: reference index state と table / chart apply を同じ owner に置き、owned / pending / installed の各 source を permanent snapshot / receipt へ接続した unit で facade apply host を削除する。
- `LIB-06`: 新しい domain abstraction を設計する outcome ではない。既に成立した owner を直接 compose し、残る progress / dialog / diagnostics port を用途別に接続して broad host / factory / forwarding を削除する。

#### `LIB-03` 残存責務の実行境界

`LIB-03` の残作業は private helper、host member、coordinator file ごとに分けず、次の ownership boundary を順に閉じる。以下の owner 名は責務を示す概念名であり、C# type 名を固定しない。各 unit は記載した production route、state / behavior、旧 route 削除、behavior test を一つの未コミット差分で完了する。

1. **Resource-health state owner closure**
   - owner は index snapshot、invalidation、input / index version、mutation depth、invalidation suppression、full-owned target freshness と、full rebuild / delta / defer / failure invalidation / warning projection query を同じ境界で所有する。
   - catalog mutation / generic delta、installed-target apply、maintenance hydration、owned maintenance / manual rescan、merge-directory、pending estimated-install、view query の production route をこの owner へ直接接続する。呼び出し側は catalog mutation receipt、versioned target snapshot、resource-health mutation facts のいずれかを渡し、owner の lock、version field、mutable snapshot を取得しない。
   - `BMSLibrary` の resource-health private state と、それを列挙する full-rebuild / mutation-dispatch / input-mutation host、nested forwarding、test-only access を同じ unit で削除する。application-facing query / event が必要な場合は facade が owner へ委譲してよいが、state を複製しない。
   - `LIB-02` まで残る installable maintenance / pending estimated-install host は package scheduling / composition の baseline bridge に限定し、resource-health currentness、suppression、mutation を private callback で所有しない。immutable package-target / mutation facts から resource-health owner の command / query を呼ぶ。
   - `BmsLibraryMaintenanceService` はこの unit では resource-health projection の純粋な評価 capability として使用してよい。maintenance orchestration、DB、queue state を resource-health owner に吸収せず、次の unit で閉じる。
   - full / delta の freshness、suppression 中の failure invalidation、stale full-owned target、deferred update、projection version の behavior test を owner の observable result で検証する。private field や nested host の reflection test は置き換える。
2. **Catalog-owned maintenance and maintenance-table hydration owner closure**
   - owner は maintenance-table hydration の requested / running / completed version と queue serialization、startup scheduling / shutdown、maintenance table load、owned chart への attach、stale-row detection、owned / installable target の maintenance evaluation、durable write request、resource-health 更新要求、progress / failure terminal result を所有する。
   - startup initialization、post-scan、deferred maintenance hydration、owned-chart manual rescan、merge-directory、package install、fix-install、pending estimated-install と、baseline package bridge からの deferred installable maintenance request の production route をこの owner へ接続する。resource-health owner には immutable target / mutation facts、catalog storage / mutation owner には immutable maintenance upsert / stale-delete request と result で接続する。
   - song / bmson row と maintenance row を同時に更新する non-scan runtime transaction を分割しない。maintenance owner は DB gateway を直接書かず、catalog mutation owner を hydration stale cleanup、maintenance rescan / installable maintenance、generic mutation の durable write / delete command owner とする。`LIB-01` の scan durable commit は committed facts を返す別境界として維持し、同じ scan result を再永続化しない。
   - `IMaintenanceHydrationHost`、apply host、dispatch host、`BMSLibrary` の hydration queue state / lock と nested forwarding、および package-install / fix-install / pending estimated-install host から facade private `setMaintenanceInfo` へ入る forwarding を同じ unit で削除する。package / file-operation host 自体は `LIB-02` の baseline bridge として残してよいが、maintenance evaluator / writer の callback member を保持しない。facade に残せるのは application-facing command / status query / event と、用途別の scheduler、progress、DB gateway、logging / failure port の composition だけである。
   - pending / installed package を対象にする installable maintenance の queue / mode commit / package target selection / progress は `LIB-02` に残す。mode commit → immutable maintenance request → resource-health result の ordering を維持し、既存 installable host は `LIB-02` で削除するまで scheduling / composition だけを行う。package state を catalog maintenance owner に吸収せず、catalog-owned target と installable target の mutable lifecycle を共有しない。
   - queue coalescing、shutdown / scheduler reject、DB load、attach、stale cleanup、cleanup failure invalidation、owned rescan、package install / fix-install / pending estimated-install の mutation → maintenance → resource-health ordering と failure、progress / terminal notification を behavior test で検証し、method名を固定する source-text test は置き換える。
3. **Chart-info hydration and backfill owner closure**
   - owner は chart-info hydration / backfill の request / pending / running / completed version、queue serialization、all-current snapshot / display-index currentness、progress と、DB load / materialize / attach、candidate classification、parse / digest backfill / chunk write の lifecycle を所有する。
   - startup metadata bundle import / deferred hydration、file-scan committed-row index upsert、LR2 sync committed-row callback / wait / all-current fast path、package install / pending estimated-install の inline build、user-facing parse-failure delete、lazy display-index load、full backfill の production route をこの owner へ接続する。scan core は chart-info / inline-maintenance の build と durable scan commit を維持し、commit 後の immutable facts だけを chart-info owner に渡して session index / projection を適用する。package、LR2 orchestration も各 outcome に残し、versioned owned-chart / committed-row / package-target snapshot と immutable command / result だけを渡す。LR2 completion / trust は `LIB-04` まで baseline residual bridge から immutable input として受け、LR2 private state や callback を chart-info owner に取り込まない。
   - `BMSLibrary` の chart-info hydration / backfill queue、lock、mutable progress / currentness state、`UpsertChartInfoIndexRows` / `BuildAndPersistInlineChartInfoForInstalledCharts` 相当の private writer workflow、startup importer / parse-failure delete への direct DB writer route と、facade private state を列挙する inline-build host を同じ unit で削除する。metadata importer / DB gateway を owner 内の capability として使用してよいが、owner 外から write を開始しない。application-facing status / progress が必要な場合は owner の snapshot / event を facade が委譲し、同じ state を複製しない。
   - queue coalescing、shutdown / scheduler reject、metadata import → hydration ordering / idempotence / failure、scan commit 後の index apply と非二重永続化、BMS / bmson attach、SHA-256 / MD5 currentness、all-current fast path、lazy display-index load、no-candidate skip、parse failure delete → currentness / presentation notification、chunk write、zero-note / digest projection 更新を behavior test で検証し、private method invocation test は production command / result の検証へ置き換える。
4. **Generic catalog mutation route closure and outcome audit**
   - generic mutation production route は canonical catalog mutation owner と resource-health owner を直接使う。catalog owner の bounded mutation lease / receipt により、storage rows、baseline residual package / LR2 state apply、owned collection、resource-health completion、failure fallback、notification の既存順序と atomicity を維持し、composition や consumer が catalog owner の lock / mutable state を取得しない。
   - `BmsLibraryStateApplier.ApplyLibraryMutationDelta` に残る BMS / bmson row の path / folder / parent mutation、unregister と、song / maintenance row の combined transaction は catalog owner が lease 内で所有する。maintenance facts / delete request は maintenance owner から受け、旧 state applier の catalog / maintenance-table 直接 writer を削除する。state applier を残す場合は immutable receipt / lease を消費する package / LR2 residual apply だけへ縮小する。
   - `ILibraryMutationDeltaApplyHost`、`LibraryMutationDeltaApplyHost` と、catalog / resource-health private method を列挙する broad callback route を削除する。既存 package state apply と LR2 mutation block / normal-folder sync は、それぞれ `LIB-02` / `LIB-04` の baseline residual bridge として composition に残してよいが、新しい catalog host の callback member に移し替えない。
   - `LIB-02` / `LIB-04` の owner 成立後は各 consumer が immutable catalog receipt を直接適用する。未成立の consumer を先取りした総合 result / callback host は作らず、この unit では baseline residual bridge と catalog lease / receipt の間の composition だけを許可する。
   - 全 operation-specific caller と旧 host construction test を同じ unit で更新する。normal / failure path の ordering、storage / collection version と receipt、DB failure 時に live path が変化しないこと、package prune / LR2 normal-folder sync、resource-health / cache fallback、catalog-changed notification を public modifier や private 配置に依存しない behavior test で検証する。
   - unit の最後に `LIB-03` の全 production route を監査する。resource-health / maintenance state、hydration host、mutation-delta broad host、catalog state への直接 writer が残る場合は完了にせず、同じ owner 境界の修正を続ける。Full verification、startup / catalog mutation / maintenance progress / failure の UI smoke check、outcome review、`PLAN_STATUS.md` の completion を最後の code unit に含める。

次の場合は adapter や host を追加せず、この境界を再監査する。package / installable state が必要なら `LIB-02` の residual bridge に留め、LR2 request / reservation / freshness が必要なら `LIB-04` の residual bridge に留める。shared maintenance evaluator や LR2 fast path があることだけを理由に mutable lifecycle owner を統合しない。上記4境界のいずれにも属さない `LIB-03` state / writer が見つかった場合だけ、production route と retirement scope を正本へ追記してから planner を再実行する。

本再編後の `LIB-01` は scan core の ownership だけを完了条件とする。post-scan maintenance、storage apply、install-destination cleanup、LR2 freshness の owner 化、恒久的な catalog handoff contract、residual host 削除を `LIB-01` のために実装しない。恒久的な immutable request / snapshot / receipt は consumer となる `LIB-03` で production 接続する。既存実装が縮小後の criteria を満たすかを outcome-wide に監査し、欠陥がなければ再基準化監査の例外で閉じる。

### Configuration と platform boundary

settings の load / edit / save / upgrade、application base directory、managed / native dependency layout、P/Invoke、audio、external process、updater、WPF / WinForms host は明示した adapter / gateway が所有する。application / domain workflow は snapshot または用途別 interface を受け取る。

`APP-01 Application composition and settings lifecycle boundary` の完了境界は application composition と settings の load / edit / save / upgrade lifecycle までとする。feature 内に残る `Settings.Default` や global-backed convenience path の除去は、各 feature outcome と最終的な `MIG-01` の責務であり、APP-01 の完了を取り消す理由にしない。APP-01 は再開しない。

## Refactoring Completion Gate

次をすべて満たしたときだけ Gate を通過できる。

1. UI ownership
   - 各 feature View が child ViewModel を直接 DataContext とし、root binding relay が残っていない。
   - code-behind の `async void` は event entry point に限られ、処理本体を持たない。
   - root ViewModel は上記の許可責務だけで説明できる。
2. Library ownership
   - 列挙した library workflow が明示的 owner を持ち、state と behavior が同じ境界にある。
   - catalog mutation の canonical writer、write serialization、failure atomicity が一つの owner 境界にあり、他 owner は request / snapshot / receipt / lease で接続される。
   - consumer 固有の cache / freshness / reference 更新を一つの巨大 mutation result に束ねていない。
   - `BMSLibrary` の private state を写すだけの巨大 host interface、coordinator factory、test 専用 production seam が残っていない。
3. Playlist ownership
   - persistence / reload と external sync / output が `BMSPlaylist` から分離され、単体で検証できる。
4. Configuration / platform ownership
   - `Settings.Default`、DB、native、external process、UI technology への直接依存が、許可した adapter / gateway に限定される。
5. Migration readiness
   - [.NET 10 migration blocker register](./DOTNET10_MIGRATION_BLOCKERS.md) の各未解決項目が、project / package / runtime / adapter の移行作業として説明できる。
   - blocker 解消のために巨大型内部の workflow 分割や MVVM 再設計を必要としない。
6. Quality
   - build、test、format / whitespace、`git diff --check` が通る。analyzer が正常終了し、今回差分による warning が増えていない。
   - outcome 全体のサブエージェント静的レビューで重大な指摘がない。

## Size guardrail

行数は唯一の完成判定ではないが、巨大コード整理の外側 guardrail とする。

| 対象 | 2026-07-10 baseline | Gate guardrail |
|---|---:|---:|
| `MainWindowViewModel.cs` root | 23,413 | 8,000 以下 |
| `MainWindow.cs` | 10,327 | 5,000 以下 |
| top-level `BMSLibrary*.cs` family | 20,157 | 12,000 以下 |
| top-level `BMSPlaylist*.cs` family | 10,273 | 6,000 以下 |

partial split、nested type 移動、host file 追加で数値を達成したことにはしない。logical owner family、残る責務、依存方向、最大 workflow、testability を併せて確認する。baseline 値は履歴として更新せず、Gate audit 時は実ソースから再計測する。

## Ordered outcome backlog

Codex は [PLAN_STATUS](./PLAN_STATUS.md) の active outcome を進める。active outcome が未指定なら、次の順で最初の `ready` outcome を選ぶ。Outcome ID は既存参照を保つ識別子であり、実行順は下表の `Order` を正本とする。将来 outcome の class / interface 名は着手時まで固定しないが、上記 owner 境界、handoff direction、依存順、bridge retirement outcome は変更しない。

| Order | Outcome | Exit condition |
|---:|---|---|
| 1 | `UI-01 Main table presentation and regular chart ownership` | main table presentation と regular chart workflow の owner が閉じ、playlist / play-history owner からの result contract が production 接続され、XAML の child binding と root relay / callback host 削除が完了する |
| 2 | `APP-01 Application composition and settings lifecycle boundary` | application composition と settings load / edit / save / upgrade lifecycle が明示的 owner を通る。feature 内の `Settings.Default` 除去は各 feature outcome / MIG-01 で閉じ、APP-01 は再開しない |
| 3 | `UI-02 Playback ownership` | playback state / command / progress が child View / ViewModel と player adapter に移り、code-behind は view-host 操作だけになる |
| 4 | `UI-03 Playlist workspace ownership` | tree、source query / cache / cancellation、detail / summary result generation、feature-local interaction、drag-drop、reload / edit workflow が workspace owner に移り、result の main-table terminal apply を除く root / code-behind の playlist workflow がなくなる |
| 5 | `UI-04 Play history ownership` | source query / cache / cancellation、filter、result generation、selected row の feature-local action interpretation が feature owner に移り、main-table selection state / terminal apply を除く root relay と UI workflow がなくなる |
| 6 | `LIB-01 Scan pipeline core ownership` | scan request lifecycle、列挙、file diff、parse、durable commit、progress / failure / terminal result が pipeline owner にあり、後続 owner の state を scan core の責務に含めず、再基準化監査が完了する |
| 7 | `LIB-03 Catalog storage, mutation, maintenance and resource-health ownership` | runtime catalog state の canonical writer と mutation serialization、storage apply、owned collection、maintenance-table / chart-info hydration、chart-info backfill、catalog-owned maintenance / resource-health が bounded owner family に移り、scan は request / snapshot / receipt で直接接続される。後続 consumer 用の receipt / lease が恒久 contract として成立する |
| 8 | `LIB-02 Package, install-destination and file-operation ownership` | package / install-destination owner と library file-operation owner が `LIB-03` の catalog contract を使う production route を持ち、scan は immutable cleanup snapshot を直接受け取り、該当する facade state / host callback が削除される |
| 9 | `LIB-04 LR2 synchronization ownership` | input build、request / schedule / cancellation、LR2 run reservation、run / publish、status、scan-surface、trust / freshness、folder sync が `LIB-03` の catalog contract を使う LR2 owner に移り、scan / catalog から LR2-specific callback と facade mutable state がなくなる |
| 10 | `LIB-05 Playlist-reference ownership` | reference maps / index / display と table / chart apply が playlist-reference owner に移り、catalog mutation receipt と package / table change を直接受け、LR2 lifecycle から独立する |
| 11 | `LIB-06 Library facade and scan integration closure` | established owner を production composition で直接接続し、scan nested host、storage / mutation factory、残存 facade private workflow / forwarding / test seam を削除して Library Gate を閉じる |
| 12 | `PL-01 Playlist persistence and reload ownership` | DB transaction、hydration、diff、reload decision、catalog-change による playlist aggregate / summary invalidation が repository / workflow owner に移り、application-facing facade から分離される |
| 13 | `PL-02 Playlist external-sync and output ownership` | HTTP sync、custom-folder、BMT、recommended-table output が個別に検証できる owner へ移る |
| 14 | `UI-05 Shell closure` | settings dialog、library refresh、package operation、残存 feature の root pass-through と code-behind workflow がなくなり、UI Gate を満たす |
| 15 | `MIG-01 Platform boundary closure` | settings/config、native load、managed/native output、external process、updater、WPF / WinForms の残依存が migration adapter / project task に限定される |
| 16 | `GATE-01 Refactoring completion audit` | 全 Gate evidence、実測値、全体検証、静的レビューを確認し、残作業を `.NET 10` migration plan に引き渡せる |

internal owner dependency により active outcome の安全な vertical unit がなくなった場合は、bridge / adapter を追加して継続せず、上記依存順と retirement outcome に従う。`blocked` はユーザー入力または外部状態変更なしに解消できない条件にだけ使う。

## Outcome completion rule

各 outcome は次を満たすまで完了にしない。

- state と behavior の新 owner が明確である。
- production の通常経路が新 owner を使う。
- 旧 owner、旧 binding、旧 callback / host、不要な compatibility path を削除している。
- library ownership phase で retirement outcome が明記された既存 residual bridge だけは、bridge baseline から追加・拡張せず、当該 outcome が担当する category を減らした場合に限り一時的に残してよい。`LIB-06` 完了時には例外を残さない。
- 新規または恒久的な cross-owner handoff が immutable request / snapshot / receipt / event facts / lease であり、相手 owner の private state や callback 一覧を contract にしていない。前項の baseline residual bridge は担当 retirement outcome までの限定例外とする。
- private 実装配置ではなく behavior を検証する test がある。
- outcome 開始時より root の責務または migration blocker が測定可能に減っている。
- outcome 全体の検証と静的レビューが完了している。

implementation unit は seam や private helper の個数ではなく、1 つの user-visible workflow または ownership boundary を通常経路から旧経路削除まで閉じる大きさにする。新 owner への委譲だけを追加して旧 owner を残す commit、調査結果だけの commit、完了証跡だけの docs commit は作らない。plan rebaseline audit と `GATE-01` の audit/status commit だけは [Codex 共通実行ルール](./00_Codex共通実行ルール.md) の限定例外に従う。consumer 固有の invalidation / freshness / presentation 更新を一つの総合 mutation result や host に集約する変更も作らない。

UI outcome の完了時は、移管済み child owner から `Application.Current`、`DispatcherHelper.UIDispatcher`、`MainWindowViewModel` の nested contract、`*ForTest` production method、root PropertyChanged relay、root callback / workflow host がなくなっていることを検索と静的レビューで確認する。例外が必要なら暗黙に残さず、当該 outcome の acceptance criteria で許可境界として説明する。

完了結果の履歴は Git commit に残し、計画資料には outcome の状態だけを記録する。

UI-03 の完了時は、上記に加えて次を満たす。

- `PlaylistWorkspaceViewModel` の global-backed convenience constructor、optional provider fallback、暗黙の current-settings provider を production seam として残さない。production composition と tests は必要な provider / port を明示的に渡す。
- `IPlaylistPropertySaveInteraction` のように多数の root callback を束ねる広い host を残さない。workflow state / behavior は workspace ownerへ収め、shell coordination または domain capability が必要な箇所だけを用途別の細い port に分ける。

## Gate 後

Gate 通過後は `.NET 10` 移行を別計画として開始する。TFM、package、runtime layout、user settings migration、native deployment、updater 方針をそこで決定する。リリース作業は、Gate 通過後もユーザーの明示指示なしには開始しない。
