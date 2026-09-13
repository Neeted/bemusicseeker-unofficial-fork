# ライブラリ変更操作の性能課題と実装計画

Status: R3〜R6 Completed / R7 Pending

### 2026-09-13 の実行範囲

- 利用者の依頼により R3〜R6 を進める。本番環境の計測・チューニングは行わず、コード上の全件走査・コピー・再読込を削減する。R7 の大規模実測・I/O 条件・並列度の調整は対象外であり、改善率や実運用速度を検証済みとはしない。
- 開始 revision は `1f63ae274c738f746c024e5b0ed5e9d0430cdcf9`、作業ツリーは clean。root は設計、計画・仕様、統合検証、commit を担当する。各 unit の実装担当はコードと対応テストを所有し、共有ファイルを並行編集しない。
- 順序は R3 → R4a → R4b → R4c → R5a-storage → R5a-canonical → R5b → R5c → R6。R4b は新しい長寿命索引を増やす必要がなければ、削除 root の祖先集合との照合による全 directory 一回走査を採用する。R6 は対象 MD5 query を採用し、新規 session cache は追加しない。
- 各 unit は独立したテスト設計を承認してから実装する。既存 fixture を優先し、結果・旧 snapshot・部分成功・対象外列挙の不要性に不足する coverage だけを追加／更新する。反復は filtered Quick、最後の統合 snapshot は Functional を原則一回実行し、凍結して静的レビューする。
- 受付、writer、durable finalization、package ごとの公開、exact row と filesystem scope の比較規則を維持する。snapshot 不成立、旧／新 facts の不足、順序・所有権の変更、新しい永続状態や回復処理が必要になった場合は root が再計画する。
- 現在: 計画点検・R3〜R6の独立test設計とconsumer判断を完了。R3/R4/R5a/R5b-core/R5b-installed-full-snapshot/R5c/R6は関連Quickと全件処理を検出する識別力確認を完了。playlist-resolveも関連Quickと識別力確認を完了。親フォルダの照合仕様変更と重複graphの局所更新は採用せず、残る費用をR5bに明記する。全体Functional・凍結レビュー完了。

- 最終統合検証: 2026-09-13 の Functional は成功4,802件・skip11件・失敗0件。実test executionは241.8秒で180秒reporting targetを超え、300秒hard budget内の成功。retryなし。restore・format・analyzer・Release buildも成功し、analyzer指摘/コンパイラ警告/エラーは0件。本番計測、Full、R7は対象外。

- 凍結レビュー: R3〜R6の本番入口、consumer、失敗・通知境界、旧snapshot、候補順、exact identityと独立test packetの対応を確認し、修正必須の指摘なし。R3〜R6を完了とする。R7と本番規模の速度評価は未着手であり、今回の完了条件には含めない。

## 1. 目的・対象・読み方

ライブラリの局所的な変更に対して、実ファイル、DB、正本コレクション、派生索引の更新を正しく完了させるまでの仕事量を減らす。主指標は操作完了までの経過時間とし、省メモリやUIの初回表示だけを改善の代替にしない。

対象操作は、保留画面からのインストール、ライブラリ譜面の削除、譜面フォルダの別ルートへの移動、フォルダ名変更、重複ファイルチェックからのマージである。「ルート移動」は譜面フォルダを別の登録ルートへ移す操作を指し、検索ルート設定全体の変更を新たに設計するものではない。

本書は現行コードから確認できる処理と、これから行う修正を分けた作業計画である。R1～R7およびRELINK-1は本書内の作業識別子であり、他資料の番号を知らなくても読める。RELINK-1はR2に隣接して進める保存値引継ぎの仕様統一であり、R2のidentity修正・性能改善とは別の実装・レビュー・コミット単位とする。仕様の正本を置き換えるものではない。

### コード基準

- 基点commit: `6983582c4c1e9512a941f5b19f6956b4fa3b9a4b`。
- コード確認基準tree: `e1165049e65658fb6ef59c926f8557dc970a95c2`（本書と関連仕様の文書更新前のソース断面）。
- 対象断面には、`ResourceHealthIndexOwner.Apply`が`Func<string, ResourceMaintenanceTargetSet> fullOwnedTargetProvider`を受け取り、実full分岐だけで同期取得する実装が含まれる。
- 行番号はこの断面に対する補助情報。別revisionでは、先にsymbolと呼出経路を再確認する。既に解消した課題を再実装しない。
- 以下の「確認済み」は静的に確認できる処理構造を意味する。大規模実測の合格や改善率は示さない。

### 先に読む仕様

[性能要件](../spec/performance-and-scale.md)、[path identity](../spec/path-identity.md)、[データと索引](../spec/data-and-indexes.md)、[ファイルとDBの整合性](../spec/file-db-consistency.md)、[変更操作境界](../spec/library-mutation-boundary.md)、[並行性](../spec/workflow-concurrency-and-complexity.md)を優先する。実装・テスト作業はルート`AGENTS.md`、[エージェント運用](../spec/codex-agent-workflow.md)、[テスト契約](../spec/test-authoring-contract.md)、[テスト運用](../spec/testing-strategy.md)に従う。

新規の汎用mutation基盤、永続recovery queue、全操作の単一巨大transaction、case-only renameの新機能、起動scan全体の再設計、parser仕様変更は対象外。file diffのcase-only relink除外を外す作業はRELINK-1だけで扱い、新しい物理rename機能は追加しない。個別unitで必要性を立証しない限り範囲を広げない。

## 2. 操作経路と守る境界

UI側の代表経路は`PendingPackageWorkflowOwner`、`SelectedChartMutationWorkflowOwner`、`RegularChartListOwner`、`DuplicateMaintenanceWorkflowOwner`である（`BeMusicSeeker/ViewModels/MainWindow/`配下）。以下はそこから呼ばれるmodel側の代表入口と、変更対象へ到達する経路を示す。テストのためにprivate workflowだけを直接呼ぶ構成へ置き換えない。

| 操作 | 代表的なmodel入口・経路 | 終了時に維持する結果 |
| --- | --- | --- |
| 保留からの導入 | `BMSLibrary.PackageInstall.cs`: `InstallPendingPackagesToEstimatedDestinationsWithReceipt` / `ForceInstallPendingPackagesWithReceipt` → `BmsLibraryPackageInstallService` → catalog owner / resource owner | package単位のFS・DB確定、正本反映、resource候補公開、inline情報、pending / installedの整合、typed terminal result |
| ライブラリ削除 | `BMSLibrary.RemoveLibraryCharts` → `LibraryFileOperationOwner` → `BmsLibraryLibraryFileOperationsService` → catalog mutation / resource owner | 確認済み削除結果だけを反映。失敗・未実行を成功扱いしない |
| 別ルートへの移動 | `BMSLibrary.MoveLibraryRootFolderWithReceipt` → folder move workflow / `LibraryFileOperationOwner` → `CatalogMutationOwner` / `LibraryResourceIndexOwner.UpdateMovedFolderReferences` | old / new path、関連参照、LR2同期、成功済みitemの保持 |
| フォルダ名変更 | `BMSLibrary.RenameChartFolderWithReceipt` → folder rename workflow → 上記と共通のmove適用 | 実際の新path、必要なDB・索引更新、既存の上書き・失敗契約 |
| 重複マージ | `BMSLibrary.MergeChartDirectory` → `BMSLibrary.LibraryFileOperationOwner.Merge.cs` → package executor → `BuildMergeCatalogDelta` → catalog mutation → destination scan / maintenance | sourceの移動分と重複除去分の区別、destination保護、resource再反映、必要なwarning・maintenance |

共通の不変条件は次のとおり。

1. **確定事実を使う。** FS、DB、canonical stateは一つの原子的transactionではない。既存のdurable receipt、部分成功、ManualRecoveryRequired、durable finalization failure、cleanup failureを維持する。後段失敗を理由に先行成功分を一括rollbackしない。
2. **公開境界を維持する。** installは先行packageの成功を後続packageから参照できること。deleteは物理削除に成功したフォルダ集合を一回のresource owner commandへ渡すこと。旧snapshotへ後続変更を混入させない。
3. **比較規則を分離する。** DB行とowned chart identityはexact path、FS探索・resource候補探索はそれぞれの既存規則を使う。探索用のcase-insensitive集合を、行の更新・削除の認可集合へそのまま流さない。旧DB行の削除keyと現在pathの登録値を分け、行集合の収束とrelinkの保存列移行も別に判定する。
4. **受付・所有を弱めない。** 既存lease、writer予約、version検証、shutdown、WPF thread affinityを維持する。新しいBusy制約や操作queueを性能修正に便乗して追加しない。
5. **通知を消して速くしない。** 任意のprewarmや表示通知は必須内部反映と区別できるが、必要なDB・cache反映を操作完了の外へ移しただけで改善としない。

## 3. 全体規模と操作差分

設計上の代表規模は約21万譜面、約3万譜面ディレクトリ、800万規模のresource reverse keyであり、入力上限ではない。詳細な規模の定義は性能要件を正本とする。

| 記号 | 意味 |
| --- | --- |
| `C` | 所持譜面・catalog rowの全体規模。BMS / BMSONを区別して数える |
| `D` | resource entryが対応するdirectoryの全体規模 |
| `K` | resource逆引きのdistinct key数。実ファイル数やentry総数と同一ではない |
| `E` | directoryごとのresource参照entry総数 |
| `P` | 1操作内のpackage数 |
| `ΔC / ΔD / ΔK` | 実際に変更する譜面・directory・関連keyの数 |
| `S` | 確認済み削除root数 |
| `H` | resource-health索引が保持する対象key数 |
| `W` | resource-healthのwarning projection / warning対象数 |
| `h` | pathの祖先深さ |

目標は局所変更の処理を変更対象・関連bucket・必要な祖先へ限定すること。全件処理が残る場合は、件数、回数、実行条件を明示する。関数名が`delta`や`snapshot`であっても、処理量の根拠にはしない。

## 4. 現行で既に成立していること

### R1: resource-healthの不要な全件入力取得の除去 — 実装済み

`ResourceHealthIndexOwner.Apply`はno-op → invalidate → defer → delta → 必要なfullの順で処理し、次の分岐ではfull入力providerを呼ばない。

- no-op、invalidate、defer。
- 成功したdelta。
- `InvalidateIfDeltaFails`を指定したdelta失敗。

full rebuildまたは必要なdelta fallbackでは、version付きfull入力が未指定の場合だけproviderを一回呼ぶ。取得はstate lock外の同期処理であり、`ResourceHealthIndexMutationFacts.SnapshotTargetSet`でstorage ownerから切り離した後のversionと、公開直前のversionを照合する。指定済みfull入力は保持しているversionで検証し、staleな入力を無条件再取得で隠さない。

`BMSLibrary.DispatchOwnedChartCollectionMutation`は、`RebuildFull && OwnedCollectionChanged`時にcollection変更前のfull入力を破棄する。`DispatchResourceHealthIndexMutation`自身はfull入力を先行作成しない。必要なfull read / view側の構築と再利用は存続する。

**この完了範囲は入力準備の除去であり、resource-health delta内部の全keyコピー・走査が解消したという意味ではない。残件はR5cに分離する。**

根拠: `BeMusicSeeker/Models/BMSLibrary.cs:9691–9696,10967–11031`、`BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealthIndexOwner.cs:153–228,335–363`、同`ResourceHealthIndexMutation.cs:129–150`。近傍coverage: `ResourceHealthIndexOwnerTests`、`ResourceHealthFullOwnedTargetFreshnessTests`、`BmsLibraryMaintenanceServiceTests`、`OwnedChartCollectionLibraryMutationTests`。

### resource逆引きと削除公開

`ResourceReverseLookupMap`は不変baselineと構造共有する変更mapを持ち、fork時に全baselineを列挙しない。未キャッシュと空候補は別状態として扱う。最終候補列が順序も含めて同一ならbucketを書き換えない。

SelfOwnedのみの変更でも、既存のremove→appendで候補が`[A, B] → [B, A]`になれば書込みが発生する。「参照集合が不変なら無書込」と一般化しない。

`LibraryResourceIndexOwner.RemoveUnderSourceDirectories`は確認済み削除群を未公開cacheへ適用し、実変更時に最大一世代を公開する。installのpackage単位公開、mergeのsource removal＋destination scanの一括公開も維持対象である。R4はこれらを撤去する計画ではない。

根拠: `ResourceReverseLookupMap.cs`、`DirectoryResourceLookupCache.cs:397–431,962–992`、`LibraryResourceIndexOwner.cs:172–195,344–365`（いずれも`BeMusicSeeker/Models/BmsLibraryInternal/`）。契約: `devdocs/spec/data-and-indexes.md`。

## 5. 課題と進捗の一覧

| ID | 現行の問題 | 対象操作 | 種別・順序 |
| --- | --- | --- | --- |
| R2a | PathCleanup・storage・owned・package・導入lookupのexact path統一 | merge、install、catalog変更 | 実装済み |
| R2a-GATE | file diffによるpath収束前にもcatalog依存file mutationを受理できる | delete、merge、install、move、rename等のP0 file mutation | 実装済み(将来gateを前倒し) |
| R2b | PathCleanupの全表materialize・行ごとの削除・孤児確認を対象集合SQLへ統合 | 主にmerge、PathCleanupを使う修正経路 | 実装済み |
| R2c | resource-healthのchart identityがpathをcase-insensitive比較 | warning表示・maintenanceを伴う変更 | 実装済み |
| RELINK-1 | file diffの一対一・同一MD5 relinkをpath差分の種類によらず適用 | startup / file diff / 再初期化 | 完了。R2群とは独立した仕様統一 |
| R3 | LR2同期receipt用の全BMS path取得、二重sort、全祖先lookup作成 | delete、move、rename、merge | 完了 |
| R4a | move / renameで全reverse key・候補配列を走査・一時確保 | move、rename | 完了 |
| R4b | 削除sourceごとに全directory keysを複製・走査 | delete、merge | 完了 |
| R4c | entry rootをpackageごとに全コピー | install、変更全般 | 完了 |
| R5a | storage rowリスト再作成、canonical list探索・順序map再構築 | install、delete、move、merge | 完了 |
| R5b | 派生索引の全失効と次操作での再構築 | 変更全般、連続merge | 完了 |
| R5c | resource-health deltaでも全keyコピーと変更対象ごとの全key探索 | current索引へのdelta更新 | 完了 |
| R6 | inlineの再read、全parse-failure map取得、不要な再通知の可能性 | install、merge後maintenance | 完了 |
| R7 | 物理I/O条件・並列度・長期cacheの費用が未評価 | 変更全般 | 仕事量削減後に必要範囲を測定 |

## 6. 実装unit

### R2a — mutation経路のexact path identityを揃える

Status: Implemented. 恒久的なpath identity契約とテスト対応表は[path identity](../spec/path-identity.md#r2a-test-map)を正本とする。

#### 完了記録

- merge分類、catalog cleanup / upsert、DB gateway、storage rows、owned / ref lookup、installed package反映、install-destination state / overlayで、DB / catalog rowを表すpathを未加工のexact keyとして扱うようにした。hash identity、directory探索、resource探索などrow identityではない比較規則は変更していない。
- row identityとphysical filesystem target identityを分離した。複数の選択済みexact rowが同じ物理fileへ解決される場合、filesystem操作は既存の正規化・case-insensitive identityで一回にまとめ、その結果を選択済みexact targetへだけ反映する。未選択rowを物理alias一致だけでcatalog mutation対象へ追加しない。
- mergeのdetached packageでは、旧exact keyをcatalog mutation集合に保持したまま、物理source / owner copy / package rootを既存FS規則へ揃えて同じ物理sourceの二重予約を避けるようにした。
- whole-folder削除後のinstall-destination clearは、削除folderの判定をfilesystem規則のまま維持しつつ、clear対象のcatalog row bindingとclear済み集合をexact pathで識別する。
- R2a単体では起動時file diffを省略した未収束rowが局所mutationへ到達し得る既存境界を維持した。後続のR2a-GATEで採用済みの将来境界を前倒しし、現在読み込んだcatalog generationのpath収束が確認されるまでcatalog依存file mutationを受付拒否する。R2aのexact row identityとphysical I/O identityの分離はgate導入後も維持する。
- file diffの保存値relinkはRELINK-1、PathCleanupの全表materialize削減はR2b、resource-health keyはR2c、storage list再構築削減はR5aへ残した。schema、新しいretry / replay / persistent state、lease / publication境界は追加していない。
- R2aでは新しい全catalog走査・全件copyを追加していない。既存のcleanup時全表materialize、孤児hash確認、storage list再作成、package走査の費用は後続unitの対象とする。

### R2a-GATE — file diff path収束前のcatalog依存file mutationを拒否する

Status: Implemented. 採用済みだった将来の受付境界をR2a直後の独立unitとして前倒しする。恒久契約は[path identity](../spec/path-identity.md#r2a-test-map)、[library mutation boundary](../spec/library-mutation-boundary.md)、[chart file read pipeline](../spec/chart-file-read-pipeline.md)を正本とする。

#### Goal・decision

起動時file diffを省略した、またはauthoritativeなpath収束を完了できなかったcatalog generationでは、DB上のcase-only / dot alias等が実在fileとの対応を一意に表さないことがある。その状態でexact rowだけを削除・mergeすると、利用者が選んだ実在fileを消して別のstale rowだけ残す結果を作り得る。個別mutationごとの全catalog ambiguity走査は追加せず、現在generationについてauthoritative file diffとcanonical catalog replacementが完了したというprocess-local factを受付条件にする。

- process開始時は未確認とし、Startup / FullReinitialize / ReloadFileDiffでcatalog/file-diff収束を開始すると未確認へ戻す。ScoreOnlyはcatalog path membershipを再構成しないため既存factを維持する。
- authoritative scan surfaceからexact path diffを適用し、canonical catalog/storage replacementと必要なpublication handoffまで正常に完了したときだけConvergedへ進める。startup scan省略、non-authoritative / incomplete scan、empty-scan-with-existing-db保護、diff / apply失敗では開かない。
- 個別譜面のrecoverable read / lightweight parse failureはpath surfaceのauthoritativenessを失わせない。読めずcatalogへ追加されなかった譜面の存在を理由にgateを閉じず、`FileScanFailures.Count == 0`をreadiness条件にしない。directory enumeration自体が不完全なscanとは区別する。
- 現在のlibrary catalog pathをauthorityとして実library BMS fileまたはcatalog membershipを変更するcatalog依存file mutationは、既存exclusive mutation leaseを先に取得し、そのlease内でprocess-local readinessをO(1)で確認する。未確認ならleaseを解放してFS / catalog DB / owned stateを変更せず受付拒否する。少数操作のたびの全catalog走査・case-insensitive group作成は行わない。playlist / pending-only処理のshared mutation lane自体はgateせず、auto-install ingressはdiscoveryとpending投入を許可して、未収束時は実libraryへのinstallだけを抑止する。
- Startup / ReloadFileDiff / FullReinitializeはgateを開く側なので、従来のraw LR2/exclusive mutation leaseを使い、readiness gate自身では止めない。
- schema、永続readiness列、retry / replay / recovery queue、別generation tokenは追加しない。R2aのexact catalog row identityとphysical filesystem identityの分離は変更しない。

#### Test Contract Packet — `CATALOG-FILE-MUTATION-GATE-20260912`

Authority: 利用者要件と`path-identity.md`で採用済みだった「必要なfile diff収束前はfile mutationを受付しない」境界。現在実装や既存出力をoracleにしない。

| Contract ID | Production ingress / setup | 完了時に観測する結果 | 検出する誤実装 |
| --- | --- | --- | --- |
| CFG-01 / R2-MUTATION-GATE | Startupでfile scanを省略し、DBに未収束のphysical alias表記row（case-only / dot aliasを含み得る）、FSに実在fileを残した状態からmergeを要求 | merge未適用、FS / SQLite / owned row不変 | readiness既定Ready、FS mutation後の遅いgate、未収束rowの局所mutation継続 |
| CFG-02 | CFG-01の状態からauthoritative `ReloadFileDiff`を完了し、同じmergeを再要求 | reloadでcatalog pathが収束し、その後のmergeは通常どおり受理・完了 | gateが一度閉じると開かない、StartupだけでReadyにする、収束operation自身をgateする |
| CFG-03 | authoritative startup scanに個別のrecoverable chart read / parse failureを含める | 読めないchartをcatalogへ追加しなくてもpath convergenceはReadyとなり、mutation leaseを取得できる | `FileScanFailures.Count == 0`等をreadiness条件にする |
| CFG-04 | Startupでfile scanを省略した未収束状態からplaylist shared lease、pending clear/remove、auto-install ingressを要求 | playlist leaseとpending-only操作は通常どおり利用でき、auto-install ingressは実libraryへinstallせず候補をpendingへ保持する | shared mutation lane全体をgateする、pending-only stateまで止める、package投入自体を拒否する |

Fixture候補は`BmsLibraryDuplicateServiceTests`のstartup-scan-skip merge case、`BmsLibraryInitializationFileScanTests`のinvalid BMSON / playlist lease case、`BmsLibraryPackageInstallServiceTests`のpending clear / auto-install case。completion signalは同期的なInitialize / ReloadFileDiff返却、mutation admission結果、FS状態、SQLite row、owned / pending状態とする。

#### 完了記録

- `CatalogFileMutationReadinessOwner`をprocess-localなreadiness factとして追加し、catalog依存file mutation用admission ownerで既存LR2/exclusive lease取得後に確認する。block reasonは設定OFF起動とその他の未収束だけを区別し、警告文言の種類を増やしすぎない。hot pathの追加費用はreadinessのatomic readだけで、catalog件数に比例する処理を追加しない。
- folder move / rename / chart delete / merge / actual library install / installed-resource overwrite等、current library catalog pathを実fileまたはcatalog membershipのauthorityとして使うP0 mutationだけをcatalog-path admissionへ接続した。playlist reload / playlist DB / LR2 custom-folder生成と、pending packageの追加・remove・clear・source rename/deleteはraw exclusive mutation laneのままとした。auto-install ingressは未収束でもpackage discoveryとpending投入を許可し、実libraryへのauto install candidateだけをpendingへ保持する。
- production Startup / FullReinitialize / ReloadFileDiffは収束開始時にfactをresetし、authoritative pipeline成功時だけpublishする。unit fixtureがproduction initializationを通さず直接catalogを構成する場合は、そのfixture前提として明示的にConvergedを設定する。
- CFG-01〜04を恒久testへ反映した。標準test実行は本レビュー依頼の条件に従い別途扱い、実装時にはstatic contract reviewを行う。

### R2b — PathCleanupの全DB読込を対象限定・集合更新へ移す

Status: Implemented（2026-09-12）。恒久契約と実装・テスト対応は[path identity](../spec/path-identity.md#局所-pathcleanup-の対象集合r2b)、[データと索引](../spec/data-and-indexes.md#consistency-updates)を正本とする。

#### 完了記録

- `BmsLibraryDbGateway`でexact cleanup keyとowner由来の現在削除pathを統合し、既存のpath/hash temp集合とbulk SQLへ接続した。BMS / BMSON / maintenanceの全表managed filter、行ごとの削除、hashごとの孤児確認を退役した。
- 存続するrelocation destinationのexact保護、maintenance-only cleanup、削除前DB hashとowner hashの候補、残存BMS ownerのdigest保持、BMSON分離を維持した。relocation→cleanup→upsert、transaction失敗伝播、commit後のstorage / canonical反映、受付・lease・公開境界は変更していない。
- reviewで見つかったhashidxの広域走査も、temp pathを外側に固定したexact path主キー検索へ変更した。file diffと同じbulk helperを使い、新schema、mirror cache、fallback、retryを追加していない。
- 独立テスト設計後、既存`CatalogMutationOwnerTests`へ実SQLiteの小規模対照を追加した。背景BMS / BMSON各16行と128行で同じ4 pathをcleanupし、DB / storage / ownedの残存exact集合、digest保持、返却行数、実statementのFULLSCAN_STEP / VM_STEPを確認する。既存exact path・destination保護・DB failure・file diff bulk coverageを維持した。
- 返却catalog行は両条件0、VM_STEPは両条件1,980。旧全表materializationと旧hashidx走査を一時復帰する二つの負の対照で、検証が誤実装を検出することを確認し、復元後に再成功した。条件・数値・補助query planの制限は[受入記録](../acceptance/r2b-path-cleanup-db-work-2026-09-12.md)を参照する。
- 関連Quick 103件成功。最終Functionalは4,755件成功・11件スキップ、230.4秒（180秒目安超過・300秒以内）。共用SQL補正前のFunctionalも239.5秒で成功した。書式・静的解析・build・`git diff --check`を通過し、独立再レビューは修正必須指摘なし。
- 約21万譜面での操作terminal wall-clockは未測定。storage / canonical listの全件仕事はR5a、resource-health identityはR2c、保存値relinkはRELINK-1へ残す。今回の小規模処理量確認を操作全体の大規模性能合格と扱わない。

### R2c — resource-healthのchart path identityをexactにする

Status: Implemented（2026-09-12）。multi-agent機能を利用せず、同一セッション内でoracle-firstのテスト設計、実装、検証、fresh reviewを順に分離した。

修正前の`ResourceHealthWarningProjection.cs`内の`ResourceHealthIndexSnapshot.ResourceHealthChartKey`は、`Equals`、`HasSameChartIdentity`、`GetHashCode`のpath部分に`OrdinalIgnoreCase`を使っていた。そのため、同じkind / MD5でcase-only pathが二行あるとfull buildで同一keyになり、MD5が異なる場合もdelta時の同一chart判定が相手variantを巻き込める。

これはDB操作そのものではないが、DB上で別である譜面のwarning lookupと対象件数を混同する残件である。R1の入力取得制御とは別の修正とする。

R2cではpath成分だけをexact identityへ合わせ、hashの既存比較規則と、同じexact pathのrehashで古いhashのprojectionを除く契約を維持する。`DistinctValidTargets`も同じkeyを使うため、full build・delta・projection lookupを一緒に確認する。`ResourceHealthIndexOwner.NormalizeTargets:517–520`は現在null除外のみであり、新たなcase-insensitive重複排除を追加しない。必要な変更箇所はこのproduction経路へ限定する。

受入条件は、case-only二行に異なるmaintenanceを与えたときの独立したwarning、target count、一方の更新・削除で他方が不変、同じexact pathのrehash、旧snapshotの不変。配置候補は`BmsLibraryMaintenanceServiceTests`のresource-health snapshot / deltaケースと`ResourceHealthIndexOwnerTests`。期待値の根拠はpath identityとwarning仕様であり、現在のcase-insensitive出力を正解として固定しない。

#### Test Contract Packet — `RESOURCE-HEALTH-EXACT-PATH-20260912`

Authority: 本R2cを実装するという利用者指示、[path identity](../spec/path-identity.md)の永続chart path exact identity、[WARNINGモデル](../spec/warning-model.md)のresource-health projection契約。current implementation、current output、既存test expectedはoracleにしない。

Production route: startup / maintenance hydrationまたはmanual resource-health rescan → `BMSLibrary`のfull / delta resource-health dispatch → `ResourceHealthIndexOwner` → `ResourceHealthIndexSnapshot` → 通常一覧warning projection / `GetChartsNeedResourceFix`。入口前提は、owned collection上でcase-only pathが別exact chart rowとして成立していること。

| Contract ID | 入力・観測する結果 | 許容差分 | 検出する誤実装 |
| --- | --- | --- | --- |
| `R2C-FULL-LOOKUP` | 同kind・同MD5でpathだけがcase-onlyに異なる二chartへ異なるresource maintenanceを与える。full snapshotの`TargetCount`は2、各exact path lookupは各chart固有のwarningを返し、第三のcase表記は一致しない。通常一覧のfix対象も二exact rowを保持する | target列挙順、warning message文言 | pathの`OrdinalIgnoreCase` key、case-fold dedupe、projection lookupだけNOCASEのまま残す |
| `R2C-DELTA-REHASH` | case-only二chartを保持したsnapshotへ、一方または双方のdeltaを適用する。同じexact pathのrehashは旧hash projectionだけを除き、相手variantを保持する。一方の削除でも相手variantは不変。更新前snapshotと各中間snapshotは後続deltaで変化しない | snapshot version / build time | `HasSameChartIdentity`のNOCASE残存、`DistinctValidTargets`のcase-fold、rehashで相手variantを除去、snapshotのin-place mutation |
| `R2C-HASH-RULE` | case-only path以外のkind / hash比較規則は既存契約を維持し、同じexact pathのhash違いはdelta rehashとして置換する | 内部key表現 | path修正と同時にhash比較をcase-sensitive化、同じexact pathの旧hashを残す |

配置は`BmsLibraryMaintenanceServiceTests`へ`R2C-FULL-LOOKUP`と`R2C-DELTA-REHASH`をextendする。`ResourceHealthIndexOwner.NormalizeTargets`はnull除外だけの現行責務を維持し、新たなcase-insensitive dedupeを追加しない。修正前redは上記二caseを既存実装で実行して確認する。

#### 完了記録

- `ResourceHealthChartKey.Equals` / `HasSameChartIdentity` / `GetHashCode`のpath成分だけをordinal exactへ変更した。kindとMD5のcase-insensitive比較、同じexact pathのrehashで旧hash projectionを除く既存契約は維持した。`ResourceHealthIndexOwner.NormalizeTargets`には変更を加えていない。
- `BmsLibraryMaintenanceServiceTests`へfull owned index / lookup対照とsnapshot delta / rehash / removal / immutability対照を追加した。同kind・同MD5のcase-only二行、hash case、第三のcase表記、同一delta batch内のcase-only同hash、後続rehashと削除を観測し、current outputをoracleにはしていない。
- 旧`OrdinalIgnoreCase` path規則へ同じ入力を当てる静的識別確認では、full keyとdelta chart identityの双方がcase-only二行を同一扱いする一方、exact規則では別扱いになることを確認した。したがって追加testは対象誤実装を区別する。修正前red / runtime negative-controlは未取得だが、適用後の`scripts/verify-refactor.ps1`は利用者側で成功済みである。本記録では実行mode・件数・elapsedは未取得のため断定しない。
- `git diff --check`は成功し、production ingressからsnapshot build / delta / projection lookup / fix対象列挙までを再追跡したfresh static reviewでは修正必須指摘なし。新しいcatalog走査、DB query、全件copy、fallback、retryは追加していない。
- 約21万譜面規模のterminal wall-clockはR2cの受入条件ではなく未測定。storage listの全件再構築削減はR5a、保存値relinkはRELINK-1の独立unitとして残す。

<a id="relink-1"></a>
### RELINK-1 — file diffの保存値relinkをpath差分の種類によらない規則へ統一する

**Status: Done（2026-09-13）。** 基準commitは`9add27c51b03a5b653c6042024e22745a8aa6076`（開始時clean）。R2群から独立した仕様統一として実施した。

- `FileScanParseCommitOwner.PrepareMovedBmsUserColumnRestores`のcase-only除外と専用helperを退役し、同一MD5の削除候補・新規候補が一対一で旧exact keyの保存値snapshotがある場合に、`favorite` / `tag` / `adddate`をDBとmemoryへ引き継ぐ共通処理へ統一した。
- exact membership、既存destinationの保護、file diff全体での候補数判定、削除前snapshot、writer barrier後のexact復元、既存failure伝播を維持した。現在のpath・mtime・CRC・metadata・maintenanceは現在入力を使用し、BMSONへのrelinkや新しいscan・query経路・persistent state・retryは追加していない。
- 利用者の追加指示に従い、通常移動とcase-onlyを同じ機能テストの入力例とした。旧case-only仕様そのものを誤実装として狙うテスト、source上の除外不在のassertion、旧実装のred・除外復活mutantは作っていない。
- 独立designerのPhase Aで利用者指示と採用済みpath仕様から判定基準を固定し、Phase Bで到達経路と配置を確認した`RELINK-1-UNIFIED-MOVE`をrootが承認した。PAIR / GENERATED / REAPPLYを一般移動data testへ統合し、EXISTINGとAMBIGUOUSを同じfixtureで確認する。期待は入力した三保存列と現在譜面・resource surfaceから定め、内部構造・順序・ログ文言は固定しない。通常移動と旧case-onlyの重複test body、旧非引継ぎexpected、復元ログ文字列assertionを退役した。
- 本番経路はstartup / `FullReinitialize` / `ReloadFileDiff` → `LibraryFileScanPipelineOwner` → `BmsLibraryInitializationService.ApplyFileScanDiff` → `FileScanParseCommitOwner.ApplyFileDiff`。外部DB内容はstartup / reinitでcurrent集合へ読込済みとし、既存exclusive DB / single writerと完全scanの前提を維持する。テストは既存の一時SQLite、captured scan、同期service完了とproduction projectionを用い、DB・memory双方を観測する。
- filtered Quick（`FullyQualifiedName~BmsLibraryInitializationFileScanTests`）は57/57成功。統合Functionalは一回で成功（4,762件成功・11件skip・失敗0件）し、test executionは216.9秒（180秒目標超過、300秒制限内）だった。事前analyzerは指摘なし、buildは警告・エラーなし。独立静的レビューは修正必須の指摘なし。
- 未変更のINPUT / ORDER / FAILURE / BMSONは既存coverageと静的確認の範囲。snapshot欠落・空hash・実restore transaction failureの専用relink testは追加していない。既存writer failure testはログcallback例外によるもので、実restore transaction failureの直接検証とは扱わない。
- 約21万譜面規模のterminal wall-clockは未測定。新しい全catalog走査・コピーはなく、新たに成立する組を既存復元処理へ渡す。小規模機能テスト成功を大規模性能合格とは扱わない。

恒久仕様と実装・テスト対応は[path identityのrelink規則](../spec/path-identity.md#relink-policy)、[file read pipeline](../spec/chart-file-read-pipeline.md)、[LR2 song DB生成](../spec/lr2-song-db-generation.md)へ反映した。後続の性能unitとリリース操作は別作業とする。

### R3 — LR2同期に必要な範囲のfactsだけを作る

Status: Implemented（2026-09-13）、Functional・凍結レビュー完了。

全BMS path捕捉・二重sort・全祖先lookup生成を退役し、変更対象の範囲queryと祖先countをreceiptへ捕捉する。通常renameとauto-renameは同じ局所factsを使い、auto-renameは確定したold/new pathで予定factsを補正する。exact DB key、残存譜面、root除外、部分成功・失敗時のLR2同期境界を維持した。

関連Quick 95/95成功。背景16/128で通常rename・auto-renameの実query訪問を確認し、全BMS列挙を戻す限定変異は4/4で不要訪問を検出した。恒久契約と対応表: [LR2生成の局所同期](../spec/lr2-song-db-generation.md#局所catalog変更後のnormal-folder同期)。全件を必要とするfull scanの処理は維持する。

### R4a — move / renameのreverse更新を関連bucketに限定する

Status: Implemented（R4a/b/c、2026-09-13）、Functional・凍結レビュー完了。

移動entryのaudio/image/movie hashから関連bucketだけを調べ、変わる候補配列だけを置換する。全reverse key走査と変更rootの全freeze copyを退役した。cached miss、未構築lookup、full/lazy lookup、旧snapshot、候補順、外部上書き時の失効を維持する。

R4全体の関連Quick 244/244成功。候補順の変異、fork時全entry copy、sourceごとの全directory走査の各限定変異を識別し、復元した。恒久契約と対応表: [resource索引](../spec/data-and-indexes.md#resource-index)。

### R4b — 削除subtree探索の反復をなくす

Status: Implemented（2026-09-13）。

削除sourceを正規化・重複除去・包含整理し、祖先との照合でcommand内の全directory走査を一回にまとめる。mergeは同じ採取結果をcollect/removeへ渡す。sourceごとの全keys copy/scanを退役した。全D走査一回は必要な残存処理とし、専用subtree indexは追加しない。

### R4c — resource entry rootの構造共有

Status: Implemented（2026-09-13）。

entryのexactな格納順を不変rootで共有し、forkの全entry複製を除く。同command内の削除slot再利用と、次commandでは残存順から末尾追加する旧候補順を維持する。snapshotを越えた空き位置の持越し、世代chain、全件compactionは追加しない。明示的な全lookup構築・候補全列挙の費用は残る。

### R5a — storage rowsとcanonical collectionの全件仕事を減らす

Status: Implemented（2026-09-13）、Functional・凍結レビュー完了。

raw storageとcanonical collectionに不変sequenceと対象entryへの索引を使い、局所upsert/remove/relocationとO(1) capture/getterへ移した。全row再copy、canonical順序map再構築、局所ref取得の全走査を退役する。rawとcanonicalのBMS/BMSON順序規則は統一せず、live ownerと捕捉済みviewの契約を維持した。

初回canonical BMSON順序正規化はBMSON suffixだけを処理し、発生factをreceiptへ渡す。raw BMSONは実移動後の次BMSON upsertで必要な再整列を残す。いずれも全BMSを巻き込まない。full replacement・cold build・明示的な一覧列挙は維持する。

storage Quick 40/40、canonical Quick 259/259成功。getter全materialize変異と初回BMSON正規化に全sequence捕捉を戻す変異を実列挙・訪問assertで識別し、復元した。恒久契約と対応表: [格納と捕捉](../spec/data-and-indexes.md#storage行の格納と捕捉)、[canonical順序と局所参照](../spec/data-and-indexes.md#canonical-collectionの順序と局所参照)。

### R5b — 派生索引の失効範囲を限定する

Status: Implemented（2026-09-13）、Functional・凍結レビュー完了。

- 所持ハッシュ: MD5/SHA別のowner数を差分更新する。両hash集合が同じならcontent Versionとsummary count cacheを維持し、source versionだけを適切な公開境界で更新する。HashSetとcountの二重stateを除き、digest・通知前の可視性、旧snapshot、full replacement・旧facts不足時の全失効を維持した。
- primary/full installed lookup: countとdirectory候補の不変rootを共有し、snapshot時の全map複製・全bucket整列・参照数の全map Sumを除く。同MD5/SHA-onlyのnet差分を相殺し、影響bucketだけを更新する。last-owner・excluding・旧snapshotを保持する。
- playlist resolve: kind＋exact pathで全候補を保持し、対象MD5/SHA bucketの差分で代表削除・置換・移動・digest変更へ追従する。代表用の重複mapは作らずbucket先頭から解決する。optional ref indexを構築せず、digest window終端の無条件失効と旧全ref生成routeを退役した。初回BMSON順正規化とfacts不足の既存失効は残す。
- parent folder: 捕捉済みpath listの再copyとrootごとのoutput-base正規化を除く。登録rootの `root\.` 等では正規化済みsubtree countに置き換えると候補採否が変わるため、この置換は採用しない。既存raw prefix照合・FS探索/失敗・standalone・公開versionを保持する。
- duplicate: 削除による分割・追加による結合を含む全体graph解析とwarning clear/applyを維持する。新しいincremental graphや必要通知の抑止は追加しない。

core Quick 202/202、installed full Quick 148/148、playlist resolve Quick 216/216成功。full source/root copy、参照数Sum、resolve全再構築、digest終端無条件失効の限定変異を対応assertで識別し、復元した。恒久契約と対応表: [installed lookup](../spec/data-and-indexes.md#導入済み譜面lookupのsnapshot)、[所持hash](../spec/data-and-indexes.md#所持ハッシュ索引の差分と公開)、[playlist解決](../spec/data-and-indexes.md#プレイリストから所持譜面を解決する索引)。親フォルダのpath捕捉・root×path照合とduplicate全体処理は残件である。

### R5c — resource-health delta本体を差分仕事にする

Status: Implemented（2026-09-13）、Functional・凍結レビュー完了。

kind＋exact pathでhash/projection/順序を一つのentryに束ね、対象identityから旧hashへ直接到達する。全membership/projection copyと対象ごとの全H探索を退役し、active/ignoredの順序sequenceも構造共有する。getterへ全W materializationを移さず、旧snapshot、未変更の相対順、更新対象の末尾配置、healthy membershipを維持した。

R1のno-op/invalidate/defer/delta/full分岐、provider取得、input version照合、失敗時非公開を維持する。path-change/PathCleanupの既存失効、必要なfull build・明示一覧列挙は残す。関連Quick 155/155成功。getter全列挙・materialize変異を実view列挙assertで識別し、復元した。恒久契約と対応表: [resource health](../spec/data-and-indexes.md#maintenance-and-resource-health)、[path identity](../spec/path-identity.md#resource-health-index-の-chart-path-identityr2c)。

### R6 — inline / maintenance入力の再利用と対象query

Status: Implemented（2026-09-13）、Functional・凍結レビュー完了。

実read snapshotのMD5だけをparameter化したparse-failure queryへ渡し、全failure map取得を除いた。discovery時の旧MD5を使わず、empty入力ではqueryしない。全件hydrationのAPI、current判定、明示save/deleteの可視性を維持し、新session cacheは追加しない。

関連するR3/R4a/bとの統合Quick 264/264成功。全SELECT＋managed filterを戻す限定変異は実SQLite ROW数の増加で失敗し、復元後も対象Quick成功。恒久契約と対応表: [chart-info lifecycle](../spec/chart-info-lifecycle.md#既存pathからのinline解析のfailure取得)。

通常installのdiscovery→destination間に同一寿命の再利用可能bytesは保持されていないため、destination再read/resource検証を維持する。currentSkippedでも必要なstorage/digest/session反映・通知、mergeのresource-only補完は省略しない。本番計測・並列度調整は未実施。

### R7 — I/O・並列度・長期cacheを条件別に評価する

同一volumeの移動・rename、別volumeへのcopy / move、resource-only merge、既存先衝突、ごみ箱削除を区別する。件数だけでなくbytes、既存ファイル、retry・失敗数を観測する。

先に重複列挙・read・digestを減らし、その後で独立したread / hash / parseの並列化を検討する。同一destination変更、DB writer、durable finalizationを無条件に並列化しない。`LongPathFileSystem`、`IFileMutationService`、`FileDbMutationExecutor`のtype / reparse / 所有権・衝突確認と補償契約を維持する。

resource reverse mapは世代chainではないが、baselineと変更済みdistinct keyのpayloadは保持する。長期のlookup、割当、GC、操作完了への影響は未実測である。必要性の確認前に毎操作compactや定期full rebuildを加えて全K処理を再導入しない。compactionが必要なら既存index replacement等の境界で別途設計する。

これは測定・限定実装のunitであり、現時点の特定並列度やcache容量を最適値とするものではない。

## 7. 推奨順序・分割・実装開始条件

推奨順は`R2a → R2a-GATE → R2b → R2c → RELINK-1 → R3 → R4a → R4b → R4c → R5a → R5b → R5c → R6`。R2a-GATEは採用済みだった将来の受付境界を前倒しした独立unitであり、R2aのexact identity修正とは分けて完了・reviewする。RELINK-1はR2群の直後に配置するが、R2の受入条件やコミットへ混ぜず、それぞれ独立に完了・検証する。R2cとRELINK-1は相互に実装依存せず、R2a完了後の近接unitとして順序を調整してよい。R2cは少なくともR5cより先に完了する。R7の必要な観測は各unitへ添え、測定基盤全体の新設を先行条件にしない。resource-health deltaが実際の主因と確認できた場合は、R2c後にR5cを前倒ししてよい。

R2aをR2bと同じ巨大変更に埋めず、identity修正と速度改善の根拠を分ける。RELINK-1も別unit・別レビュー・別コミットにし、file diff保存値の期待値変更をR2の単なるcomparer修正に便乗させない。R3とR5aは`BMSLibrary.cs`やowned collectionを共有するため並行編集しない。R4各unitも同じcache / ownerを触るため原則直列とする。

| unit群 | 主な書込対象 | 読取・維持対象 | 恒久テストの扱い |
| --- | --- | --- | --- |
| R2a | merge delta、catalog request、storage owner、DB gateway、必要なpackage反映 | path spec、file diff、通常move、DB transaction | 既存fixtureへexact identity・集合反映の不足caseを追加／更新 |
| R2a-GATE | file-scan readiness、catalog依存file mutation admission、対象P0 ingress、warning resource | path / mutation / read-pipeline spec、raw shared/convergence lease、playlist / pending-only ingress、ScoreOnly | scan-skip拒否→reload許可、個別parse/read failureでもReady、playlist / pending-only非gate、auto-install→pending退避のcontractを追加／更新 |
| R2b | PathCleanup gateway、対象集合SQL、必要なdigest cleanup | R2a exact集合、DB transaction、R2a-GATE admission | R2a identity oracleを維持し、対象外背景rowを含むgateway観測を追加／更新 |
| R2c / R5c | resource-health snapshot / keyと必要なowner入力整形 | R1の取得・version契約、warning仕様 | 既存health fixtureへ独立identity・処理量caseを追加／更新 |
| RELINK-1 | file diff relink判定、file-scan test、path / pipeline / LR2 specの実装状態 | R2のexact集合、旧保存値snapshot、DB復元境界、maintenance生成 | case-only BMS既存testの期待値を置換し、通常／曖昧／既存destinationとの対になるcoverageを補う |
| R3 | LR2 receipt、scope builder、composition、必要なowned query | DB writer / prune契約、登録root | 完了 |
| R4 | resource cache / owner / map、必要なdirectory query | scan ownership、install公開、候補順序 | 完了 |
| R5a / R5b | storage / owned / 隣接索引・receipt・dispatchの対象経路 | consumer順序、表示、既存single-flight | 既存coverageを優先。差分ごとに不足を判断 |
| R6 / R7 | 対象read / query / reuse / I/O policyのみ | parser / metadata lifecycle、FS安全性 | 機械的整理だけなら追加不要。挙動・失敗・処理量に不足がある時だけ追加 |

実装agentはunitを一つ選び、次を作業計画へ記入して開始する。

```text
Unit / Goal:
現在のrevisionと、本書の該当symbolの再確認:
Production ingress → owner → consumer:
入口のassumptionと、維持する失敗・公開境界:
変更前の全件処理と、変更後に残る処理量:
書込対象 / 読取専用対象 / 対象外:
採用するpath identity・順序・snapshotの契約:
既存coverageと不足 / 変更分類 / test追加の必要性:
必要なTest Contract Packet・独立oracle・誤実装の識別方法:
Quick filter / 最終検証 / 未実行時の理由:
旧経路の退役 / 残件 / 再計画条件:
```

runtimeの例外条件や下流回復を追加する前に、production入口からの到達とdurable / user-visible impactを立証する。実装上選択が必要な項目は、workerへ未決のsemanticsとして丸投げせずrootが既存仕様から決める。新しいschema、永続state、操作制限、既存failure保証の変更が必要になった場合は、性能修正のまま進めずunitを分けて再計画する。case-onlyのBMS三保存列relinkはRELINK-1で採用済みの範囲に限り実装し、再び未決の方針として扱わない。BMSONのrelink新設、maintenance移植、曖昧候補の優先選択などへの拡張は、この統一化には含めない。

## 8. 検証・完了判定・成果物

### 機能・構造の確認

通常のリポジトリテストは小規模で、結果の正しさ、不要な全件仕事の非実行、旧snapshot、部分成功、exact identityを確認する。wall-clock閾値、大規模本番DB、Everything、実ユーザーのライブラリを合格条件にしない。

観測は実provider / gateway / 格納部品へ接続する。counterを期待値に合わせただけのtest、private method名・collection型・source文字列の固定を避ける。非同期ケースは既存の決定的completion signalを使い、sleepや巨大timeoutを追加しない。

```powershell
# unitに対応する既存fixtureを指定して反復する
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<対象fixtureのMSTest filter>'

# 最終断面の通常機能確認
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

例えばR2では`FullyQualifiedName~CatalogMutationOwnerTests|FullyQualifiedName~BmsLibraryStateApplierTests|FullyQualifiedName~OwnedChartCollectionLookupMembershipTests`を出発点とし、実際に触るfile diff / install入口のfixtureも選ぶ。filterはcoverage検索後に確定し、上記だけで全経路が検証済みとはしない。環境上実行できない場合は静的確認と未実行を分ける。

RELINK-1のQuick filterは`FullyQualifiedName~BmsLibraryInitializationFileScanTests`を出発点にする。R2のfilterだけでrelinkを検証済みにせず、候補数・既存destination保護・commit後復元のcoverageを確認する。これは仕様統一の機能確認であり、性能benchmarkをDone条件にはしない。

### 速度の確認は別工程

構造上の全件処理除去と、実時間の改善率を別に記録する。実測が必要な場合は同じ結果・同じ完了範囲で、復元可能な合成または複製fixtureを比較する。実ユーザーの本番運用確認は任意の別工程であり、実装完了のために本番で破壊的操作を要求しない。

`Δ`固定で背景`C / D / K / H`を変える比較、背景固定でpackage / source数を変える比較を選ぶ。cold / warm、startup中 / idle、同一 / 別volumeを混ぜない。基準commit、入力、DB、設定、build、hardware、cache条件、反復値を残し、未測定の効果を保証しない。

操作terminalまでのFS / DB / canonical / 必須resource・LR2反映、required inline処理を主区間に含める。UI初回表示、optional prewarm、後続操作のcold costは別に記録する。`moveMs`のような既存markerは実測範囲を読んで使い、内包stageや並列worker累積時間をtotalへ加算しない。

### 各unitの提出内容

変更目的、実際の書込file、契約と処理量の変化、実行したQuick / Functional、未実行・未測定、残存全件処理、静的review結果を短く残す。成果物は限定したgit patchまたは通常の差分とし、無関係な整形・schema変更・版更新を混ぜない。恒久契約になった事項は対応specへ反映し、本書の該当unitを完了または残件に更新する。

各unitの実装完了は、保持契約と処理量の受入条件を満たす実装、および実施した機能・構造検証で判断する。検証未実行の場合は確認待ちとして区別する。本番規模benchmarkの成功は一律の必須条件にしない。計画全体は未完了unitを明示して管理し、実速度の評価結果は、その条件とともに別途記録する。
