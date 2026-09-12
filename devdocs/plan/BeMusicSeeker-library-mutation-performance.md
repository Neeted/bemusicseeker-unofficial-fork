# ライブラリ変更操作の性能課題と実装計画

Status: Active

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
| R2c | resource-healthのchart identityがpathをcase-insensitive比較 | warning表示・maintenanceを伴う変更 | R5c前に正しさを修正 |
| RELINK-1 | file diffの一対一・同一MD5 relinkにcase-onlyだけの除外が残る | startup / file diff / 再初期化 | 採用済みの仕様統一。R2群直後の独立unit |
| R3 | LR2同期receipt用の全BMS path取得、二重sort、全祖先lookup作成 | delete、move、rename、merge | R2のexact factsと整合させる |
| R4a | move / renameで全reverse key・候補配列を走査・一時確保 | move、rename | 対象bucket更新へ |
| R4b | 削除sourceごとに全directory keysを複製・走査 | delete、merge | subtree検索を集約 |
| R4c | entry rootをpackageごとに全コピー | install、変更全般 | entry構造共有へ |
| R5a | storage rowリスト再作成、canonical list探索・順序map再構築 | install、delete、move、merge | row identity修正後に増分化 |
| R5b | 派生索引の全失効と次操作での再構築 | 変更全般、連続merge | 必要な旧／新factsを渡す |
| R5c | resource-health deltaでも全keyコピーと変更対象ごとの全key探索 | current索引へのdelta更新 | R2c後に独立unitで修正 |
| R6 | inlineの再read、全parse-failure map取得、不要な再通知の可能性 | install、merge後maintenance | 確認済み仕事と検討候補を分ける |
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

`ResourceHealthWarningProjection.cs`内の`ResourceHealthIndexSnapshot.ResourceHealthChartKey`は、`Equals`、`HasSameChartIdentity`、`GetHashCode`のpath部分に`OrdinalIgnoreCase`を使う（`261–285`）。そのため、同じkind / MD5でcase-only pathが二行あるとfull buildで同一keyになり、MD5が異なる場合もdelta時の同一chart判定が相手variantを巻き込める。

これはDB操作そのものではないが、DB上で別である譜面のwarning lookupと対象件数を混同する残件である。R1の入力取得制御とは別の修正とする。

path成分だけをexact identityへ合わせ、hashの既存比較規則と、同じexact pathのrehashで古いhashのprojectionを除く契約は維持する。`DistinctValidTargets`も同じkeyを使うため、full build・delta・projection lookupを一緒に確認する。`ResourceHealthIndexOwner.NormalizeTargets:517–520`は現在null除外のみであり、新たなcase-insensitive重複排除を追加しない。必要な変更箇所はこのproduction経路へ限定する。

受入条件は、case-only二行に異なるmaintenanceを与えたときの独立したwarning、target count、一方の更新・削除で他方が不変、同じexact pathのrehash、旧snapshotの不変。配置候補は`BmsLibraryMaintenanceServiceTests`のresource-health snapshot / deltaケースと`ResourceHealthIndexOwnerTests`。期待値の根拠はpath identityとwarning仕様であり、現在のcase-insensitive出力を正解として固定しない。

<a id="relink-1"></a>
### RELINK-1 — file diffの保存値relinkをpath差分の種類によらない規則へ統一する

**Status: Planned / 方針採用済み・未実装。** R2a / R2b / R2cの近接後続unitとするが、R2には実装・テスト期待値変更を混ぜない。これはcase-onlyの保存値引継ぎを変える仕様統一であり、速度改善や既存仕様への回帰修正と同一視しない。

#### Goal・採用済みdecision

正本は[path identityのrelink規則](../spec/path-identity.md#relink-policy)。既存の通常BMS moved-hash relinkを残し、一対一・同一MD5での保存値引継ぎをcase-onlyの組にも適用する。relinkの全面廃止、case-insensitiveなrow統合、新規の物理rename機能は行わない。

判定を次の二層に分ける。

- membership: 全pathをexactに扱い、旧keyの削除と現在pathの維持・追加・更新で収束させる。relinkに失敗・不成立でもこの規則を変えない。
- 保存列: そのfile diffの削除候補と新規追加候補をMD5で照合し、旧一件・新一件かつ旧保存値snapshotありの場合だけ`favorite` / `tag` / `adddate`を引き継ぐ。大小文字の関係を追加条件・優先順位にしない。

新pathの既存行は新規候補に含めない。`DB={A,B}` / `scan={B}`ではAの保存値で既存Bを上書きしない。旧二件・新一件、旧一件・新二件などは、case-onlyの候補が混ざっても曖昧として引き継がない。candidateのcase foldや保存値欠落行の除外で、一対一を作り出さない。

保存列は`Lr2SongUserColumns`の三列だけである。新path、mtime、folder / parent CRC、生成metadata、maintenanceは現在の入力を使う。maintenanceの自動移植もBMSONの新しいuser-column relinkも追加しない。通常のmove / merge receiptによる情報維持をfile diff推定へ置き換えない。

#### 現行コード・production到達

startup / `FullReinitialize` / `ReloadFileDiff` → `LibraryFileScanPipelineOwner` → `BmsLibraryInitializationService.ApplyFileScanDiff` → `FileScanParseCommitOwner.ApplyFileDiff`が対象経路である。外部アプリ由来のcase-only DB行は、DBを取り込む入口でin-memory current集合へ載った状態を使う。軽量reloadの途中で外部DB編集を再読込する新契約は作らない。

- `FileScanParseCommitOwner.cs:134–141`: exactな削除候補からMD5別の旧候補を作り、`CreateSongUserColumnSnapshot`で旧exact pathの保存値を取得する。
- 同`PrepareMovedBmsUserColumnRestores:1256–1317`: 旧一件・新一件を判定した後、`IsCaseOnlyPathPair`でcase-onlyだけを除外している。
- 同`TrackBmsRelinkDestinationCandidate:2878–2886,2957–2963`: 新規候補は`ExistingFile == null`のBMS。既存exact destinationへの引継ぎ防止を維持する。
- 同`ApplyFileDiff:270–274`、`FileScanDiffCommitContext.RestoreSongUserColumns:1973–2025`: DB writer barrier後にexact destinationへ保存値を復元する。復元のtransaction failureを伝播する。
- `BmsLibraryDbGateway.cs`: `Lr2SongUserColumns`、`CreateSongUserColumnSnapshot:269–289`、`ApplySongUserColumns`のmodel / DB両経路、`ReadSongUserColumns`は改修時に維持する境界である。

上記のファイル名のみの参照は`BeMusicSeeker/Models/BmsLibraryInternal/`配下。利用者に見える差分はfile diff後のfavorite / tag / adddateとDB保存値であり、private helperだけの到達を根拠にしない。

#### 実装手順・所有範囲

1. path specの採用済み規則からrelinkのoracleを固定し、既存の通常relink・case-only・曖昧候補testのcoverageを分ける。R2のrow集合oracleは再定義しない。
2. case-onlyを不適格にする条件を除去する。他にcallerがなければ専用`IsCaseOnlyPathPair`も退役する。path比較をNOCASEへ戻したり、候補数の判定・既存destination除外・旧snapshot取得を緩めたりしない。
3. file diff全体で候補の一意性を確定する既存collector / writerの順序を維持する。先着一件・chunkごとの一件を一意と誤認しない。旧snapshotは削除前に取得し、DB復元は確定後のexact destinationに適用する。新しい全catalog列挙、FS同一実体照会、別のscanを追加しない。
4. DBとmemoryで同じ三列を反映し、現在pathから作る生成列とmaintenanceを保持する。read / parse / DB / restore failureを成功のrelinkとして報告しない。既存の部分commitや失敗伝播を変更せず、全rollback・自動retry・永続recovery queueを追加しない。
5. `BmsLibraryInitializationFileScanTests`のcase-only BMS testを新契約に合わせて更新し、通常relink・曖昧候補・既存destination・BMSON maintenance非移植のcoverageを維持／補強する。既存testの名前だけを変更して期待値を残さない。
6. `path-identity.md`の実装状態と`chart-file-read-pipeline.md` / `lr2-song-db-generation.md`の未実装参照を更新し、本unitの検証結果を記録する。新旧方針を恒久的に併記しない。

主な書込対象は`FileScanParseCommitOwner.cs`、`BeMusicSeeker.Tests/BmsLibraryInitializationFileScanTests.cs`と上記spec。本体のgateway変更は、現行のexact read / restoreを保ったまま必要性が確認できた場合だけに限定する。R2a / R2bとの同時編集は避ける。

#### 受入条件・test placement

| Contract ID | 入力・観測する結果 | 検出する誤実装 |
| --- | --- | --- |
| RELINK-PAIR | `DB={A}` / `scan={B}`、同一MD5の旧一件・新一件をcase-onlyと通常差分で対にする。どちらも行集合は`{B}`、三保存列は旧A由来でDBとmemoryが一致する | case-only除外の残存、通常relinkまで廃止、DBまたはmemoryだけの反映 |
| RELINK-EXISTING | `DB={A,B}` / `scan={B}`、同一MD5でも既存Bの保存値を保持し、Aだけを除く。両種のpath差分で同じ | Bを新規候補とする、NOCASEによるkey変更・巻添え削除 |
| RELINK-AMBIGUOUS | 旧二件・新一件、および旧一件・新二件。同一MD5でcase-only候補と通常候補を混在させても引継がない | case-only優先、case fold、first-win、保存値のない候補を除いて一意化 |
| RELINK-INPUT | 異なるMD5、空hash、旧保存値snapshotなし、不成立の登録候補では引継がない。単なる同一FS解決を根拠にしない | 大小文字一致だけで引継ぐ、別行から保存値を補う |
| RELINK-GENERATED | 三保存列以外は現在入力を使い、path・mtime・CRC・maintenanceが旧Aの値にならない。BMSONは通常差分・case-onlyともmaintenanceを移植しない | 旧row全体のコピー、hash一致によるresource再評価の省略 |
| RELINK-ORDER | 複数chunk・異なるworker完了順でもfile diff全体の候補数で判定し、exactなDB復元とmemory結果が同じ | chunk単位・先着順で一意と判定、復元値を後続upsertで上書き |
| RELINK-FAILURE | 到達可能な既存read / parse / DB / restore失敗で、未確定の復元を成功扱いせず既存failureを伝播する | 復元失敗の握りつぶし、durable成功と推定候補の混同 |
| RELINK-REAPPLY | 同じ正常scanを再適用しても追加・削除・relinkが再発せず、既存Bの保存値を保つ | 毎回case補正・relinkを繰り返す |

主な既存coverageは`ApplyFileScanDiff_CaseOnlyBmsPathMismatchReplacesExactPathWithoutMigratingUserColumns`、`ApplyFileScanDiff_MovedBmsWithSameMd5PreservesUserSongColumns`、`ApplyFileScanDiff_MovedBmsWithAmbiguousSourceMd5DoesNotPreserveUserSongColumns`、`ApplyFileScanDiff_MovedBmsWithAmbiguousDestinationMd5DoesNotPreserveUserSongColumns`。case-only BMS testはmembership・CRC・maintenance・二回目scanのassertionを残し、relink countと三保存列の期待値を置換する。

一時DBにexact別行を入れ、既存scan seamと実file-diff入口を使う。case-onlyの物理ファイル二つの同時作成、live Everything、本番DBを必要条件にしない。failure / 並列順の検証は既存の到達可能なseamを再利用し、private helperへの直接入力だけで新しいruntime保証を作らない。case-only除外が消えたことのsource文字列assertionは追加しない。

**Done when:** case-onlyと通常差分が同じrelink規則・保護条件を使い、現行の除外と旧期待値が退役し、上記の結果を小規模fixtureで確認できる。RELINK-1単独でレビュー・コミットでき、R2の完了や大規模速度向上と混同しない。

### R3 — LR2同期に必要な範囲のfactsだけを作る

#### 現行コスト・根拠

BMSの削除またはpath変更で、`BMSLibrary.CreateLr2NormalFolderCurrentBmsSnapshotUnsafe`と`CreateLr2NormalFolderCatalogMutationReceipt`が全BMS pathを取得する（`BMSLibrary.cs:6506–6515,6553–6574`）。`LibraryChartRefIndexSnapshot.GetCurrentBmsChartPaths`でsortした後、`Lr2NormalFolderCatalogMutationReceipt` constructorが再びDistinct / sortし、`Lr2NormalFolderCurrentBmsLookup.CreateFromChartPaths`が全pathの祖先lookupを作る。全BMSのsortと概ね`C × h`の祖先処理が局所操作に入る。

LR2のDB側は`UseScopedExistingRows = true`を既に使う（`BMSLibrary.Lr2SynchronizationOwner.cs:842–856`）。追加だけのinstall receiptは全BMS snapshotを要求しない。この二点を退行させない。

#### 修正方針

- receiptのold / new / removed directoryとprune scope、その必要な祖先を先に求める。ancestorの残存判定は既存owned directory索引の件数・存在queryで行い、現在BMS pathの実列挙は同期対象scope内に限定する。
- `OwnedChartCollectionState` / `LibraryChartRefIndexSnapshot`にあるBMS subtree countと`GetBmsChartPathsUnderRealPath`を再利用候補とする。whole-library snapshotを取得するwrapperを挟んで差分queryの外観だけ作らない。
- 正しいowned collection versionの下で対象範囲の不変factsを捕捉する。live lookup delegateをそのままreceiptへ渡して後から別世代を読ませない。snapshot不可を空集合に変換してpruneしない。
- scope探索の比較規則と、DBへ渡すexact path集合を分離する。現在の`Distinct(OrdinalIgnoreCase)`をrow identityの正本として引き継がない。folder pruneのterminal SQLまで確認する。
- `Lr2NormalFolderCatalogMutationReceipt` / `Lr2NormalFolderSyncScopeBuilder`とcompositionを同じunitで変更し、通常mutationの全BMS constructor経路を退役する。全scan用の必要な全件処理は別に残せる。

受入条件: siblingにBMSが残る祖先を誤pruneしない、登録root境界、BMS / BMSONの区別、old / new path、case-only factsの非混同、snapshot不成立時とLR2 finalizer失敗時の既存結果を維持する。少数対象では全BMS enumeratorを使わないことを観測する。

配置候補: `Lr2NormalFolderSyncScopeBuilderTests`、`Lr2NormalFolderDbSyncServiceTests`、`BmsLibraryInitializationLr2NormalFolderTests`、`BmsLibraryCatalogRelocationTests`、`OwnedChartCollectionLibraryMutationTests`。

### R4a — move / renameのreverse更新を関連bucketに限定する

`DirectoryResourceLookupCache.RewriteCachedDirectoryPaths:1161–1202`は全reverse keyを列挙し、非empty bucketごとに変更有無の判定前から候補配列を確保する。費用は全`K`と候補総数に依存する。

`ReplaceDirsWithResult`が既に集める移動対象entryからAudio / Image / Movieのhash集合を取り、該当bucketのみ旧path→新pathへ置換する。候補配列の確保も実変更時に限定する。費用の目標は移動対象entryのhash総数と、関連bucketのfan-outに依存する形であり、全Kの処理ではない。

moveは候補位置を維持する置換である。installのremove→appendを流用して順序を変えない。複数old pathの同時置換、共有hashの非対象候補、置換先重複の順序・dedupeも保持する。hash=0、full / lazy、cached missと未キャッシュの区別を維持する。

まず外部overwriteなしの通常move / renameを対象にする。外部overwrite時に存在するinvalidate / warmup分岐を安易に変更しない。resource directory探索のcase-insensitive規則をDB row identityへ拡張しない。

受入条件: unrelated reverse baselineの列挙を禁止した格納部品で実ownerのmoveを行っても成立する、旧snapshotが不変、関連bucketだけ変更、候補順序が正しい。全Kを新たなmap構築時に列挙する代替実装は不合格。

配置候補: `DirectoryResourceLookupCacheTests`、`ResourceReverseLookupMapTests`、`LibraryResourceIndexOwnerTests`、`BmsLibraryCatalogRelocationTests`。

### R4b — 削除subtree探索の反復をなくす

`DirectoryResourceLookupCache.Keys:434–440`は全entry key配列を作る。`RemoveUnderSourceDirectory:610–625`はこれをsourceごとに使い、`LibraryResourceIndexOwner.RemoveUnderSourceDirectories:180–195`が各sourceを反復する。現状の探索費用は概ね`S × D`。

確認済みsource rootを重複・親子関係で整理する。resource entryと同じmembershipを持つdirectory索引から範囲取得する方式を優先する。小さく段階化する場合は、sourceの祖先判定用集合を作り一度だけentryを走査する方式も選択肢とするが、その段階は「全D列挙を一回に集約」と正確に記録する。

単一の全D走査の内側で全sourceに`Any`するだけでは`S × D`は消えない。列挙回数だけでなく比較回数を確認する。補助索引を使う場合はinstall / move / replacementでの増分保守費用も含め、毎commandの全D再構築を避ける。

既存の未公開cache一つ・最大一回公開・成功folderだけの適用・例外時旧snapshot保持を維持する。`A`と`AB`のprefixを混同せず、重複rootと親子rootを二重計上しない。

配置候補: `LibraryResourceIndexOwnerTests`、`DirectoryResourceLookupCacheTests`、`OwnedChartCollectionLibraryMutationTests`。全対象fixtureは小規模でよい。

### R4c — resource entry rootの構造共有

`DirectoryResourceLookupCache.EnsureEntriesRootWritableUnsafe:984–992`は最初の実変更時に全entry Dictionaryをコピーする。installのpackage単位公開により、entry側には`P × D`が残る。

entry rootを変更経路のみdetachする構造共有へ移す。六つのresource / SelfOwned集合の同値判定を先に行い、no-opでcopy / generation増加を起こさない。補助directory索引、enumerator、lookup、full / lazy queryが旧世代を壊さず利用できるようにする。

**packageごとの公開を最後にまとめる方法は不可。** 後続packageから先行成功を読めることと、途中失敗時の成功prefix保持が必要である。新しいentry mapも世代chainの線形走査や毎回の全D変換を持ち込まない。

配置候補: R4aのfixtureと`BmsLibraryPackageInstallServiceTests`。実library入口でpackage間の中間snapshotと失敗後の成功prefixを確認する既存coverageを維持する。単なる最終generation数だけを公開時点の証拠にしない。

### R5a — storage rowsとcanonical collectionの全件仕事を減らす

#### 確認済み箇所

- `CatalogStorageRowsOwner.ApplyInstalledTargets:93–129`と`ApplyCatalogMutation:145–234`はBMSの全list filter / 再作成、BMSONの全grouping / dictionary化 / sortを行う。
- `OwnedChartCollectionState.UpsertStorageRows:1342–1379`から、`RemoveMatchingStorageRows`の`charts.RemoveAll`、`InsertBmsChartsBeforeBmson`の`FindIndex`、`SortBmsonChartsByPath`へ進む（`1403–1480`）。削除にも`charts.RemoveAll`がある。pathによる対象発見が増分でも、listの保守が全体依存となる。
- `LibraryChartRefIndexSnapshot.ReorderAffectedPathsByStorageOrder:193–220`はaffected bucketのみsortする前に、`BuildStorageOrder(charts)`で全chartの順序mapを作る。

#### 方針

R2aのexact identityを先に固定する。path / ownerによる増分格納、kind別の順序構造、必要なread viewの構造共有を検討し、書込とconsumerの双方を改修する。BMS / BMSONの並びやrepresentative選択に意味がある箇所は保持する。

全list化をlazy getterに隠して各packageがそのgetterを呼ぶ構成は不可。`CatalogStorageRowsOwner`だけ直して、owned側の全chart順序mapをpackageごとに作り続ける段階も、未達として残す。大きいunitは「storage row格納」「canonical list / order」の二つに分割できるが、各段階の残存費用を明記する。

受入条件: 同じMD5の複数配置、exact pathとowner reference、storage version、旧view、混在kindの順序、package単位の反映・失敗時成功prefix、必要なUI refresh signalを維持する。compatibility viewのmaterialization回数も観測する。

配置候補: `CatalogMutationOwnerTests`、`OwnedChartCollectionLookupMembershipTests`、`OwnedChartCollectionReferenceIndexTests`、`OwnedChartCollectionLibraryMutationTests`、`BmsLibraryPackageInstallServiceTests`。

### R5b — 派生索引の失効範囲を限定する

`BuildMergeCatalogDelta`はinstalled-directory index等の失効を要求し、`BMSLibrary.DispatchOwnedChartCollectionMutation`にはinstalled / playlist resolve / owned hash / parent folder / duplicateの広い失効経路がある。必要性が異なるので一括して「失効不要」と判定しない。

先に各consumerについて、membership、path、MD5 / SHA-256、warning、displayだけの変更のどれへ依存するかを整理する。構築済み索引へdurable receiptの旧／新factsを渡し、影響key / pathだけ更新する。facts不足時の既存full invalidationは維持し、通常経路で必要な旧値が落ちている場合に限ってreceiptを補う。

未構築optional indexはmutationのためだけに構築しない。同じ失効への二重通知・予約は既存のcoalescing / single-flightで抑える。単に通知を消したり、新しい世代tokenを索引ごとに追加したりしない。

`InstalledChartLookupIndexSnapshot.cs`内のprimary hash lookupには既にexcluding wrapperがある。`CreateSnapshot:155–163`のdirty時のcount map copyと、full invalidateからの再構築を区別する。excluding操作のたびに全snapshotを新規作成していると誤認しない。

受入条件: 連続二回目のmerge / installで不要なcold rebuildが復活しない、同一hashの最後のownerだけを除去した時の判定が正しい、旧snapshotにlive owner変更が混入しない、必要な表示・warningは更新される。必要なrefreshが遅れて次の操作へ費用を移しただけで完了としない。

配置候補: 上記owned collection系fixtureと`PlaylistSummaryMutationAndWarmTests`。個別索引の近傍coverageを先に検索し、巨大な横断fixtureを新設しない。

### R5c — resource-health delta本体を差分仕事にする

`ResourceHealthWarningProjection.cs:121–172`の`ApplyDelta`は、`projectionsByKey`の全コピーと`targetKeys`の全コピーを行う。さらに`RemoveMatchingChartIdentity:190–205`が各updated / removed targetについて`nextTargetKeys`を全探索する。主な費用だけでも`H + W + ΔC × H`の仕事がある。加えてupdated / removed間の`Any`照合（`134`）もあり、両方を多数含む差分ではその積に依存する。R1のfull入力provider非呼出しだけではなくならない。

R2cでexact chart identityを固定してから、同一chartの旧hashを直接引けるmembership構造と、変更部分だけ共有するkey / projection構造へ移す。目的は、rehashで古いprojectionを除くための全H探索と全root copyを除去すること。active / ignored viewの順序と更新、TargetCount、削除優先、invalidated時の非公開、input mutation versionを保持する。

R1で固定した分岐優先度・full入力取得条件・公開前version照合を変更しない。必要なfull buildは残し、delta失敗時に勝手な全再構築や自動retryを追加しない。対象が同じexact pathでhashだけ変わる場合と、case-only別pathの場合を別に検証する。

配置候補: `BmsLibraryMaintenanceServiceTests.ResourceHealthIndexSnapshot_ApplyDeltaUpdatesOnlyAffectedTargets`、`ResourceHealthIndexOwnerTests`、`OwnedChartCollectionLibraryMutationTests`。少数更新に無関係なtarget membershipの列挙が不要であることを実格納部品の観測で示す。

### R6 — inline / maintenance入力の再利用と対象query

#### 確認済みの残件

`ChartInfoInlineBuildService.BuildForExistingCharts:116–153`はtargetのsnapshotを再readし、その前に`LoadCurrentChartInfoParseFailureMap`を呼ぶ。後者はparse-failure表全体を読む（`BmsLibraryDbGateway.cs:1369–1381`）。通常のchart_info自体は`LoadChartInfosBySha256:1299–1314`で対象SHA限定の取得があり、build内のgroup evaluation再利用も既にある。

#### 修正方針

- parse-failure情報は対象MD5検索を第一候補とする。session cacheを選ぶ場合はparser version、timeout条件、failure明示削除と保存結果の可視性を既存lifecycleへ合わせる。根拠のない恒久cacheを追加しない。
- package処理が保持するbytes / digest / parse result / resource列挙結果は、同じ入力であると保証できる寿命の範囲で再利用する。sourceとdestinationの存在検証は別物とする。内容hash一致だけでdestination resource検証を省かない。
- `currentSkipped`でも新ownerへのstorage applicationが必要なことがある。これをsession indexへの同値upsertや通知の必要性と分ける。実際のconsumerを追って不要性が確認できたものだけ除去する。
- merge後はdestination scanとmaintenanceがある（`BMSLibrary.LibraryFileOperationOwner.Merge.cs:147–163`）。movedBmsが0でもresource補完で既存譜面のwarningが変わり得るので、一律skipしない。確定したfile factsから再利用可能な範囲を判断する。

read / digest / query / parse / commit / owner apply / notificationの内訳を観測し、parserの仕事が既にskipされるケースへ並列度変更だけを適用しない。group再利用の範囲拡大と同値通知削減は、効果・契約を確認してから実装する候補であり、現時点で全処理が不要と確定したわけではない。

契約: [chart-info lifecycle](../spec/chart-info-lifecycle.md)、[chart file read pipeline](../spec/chart-file-read-pipeline.md)。配置候補: `ChartInfoInlineHydrationTests`、`ChartInfoInstallFailureRetryTests`、`ChartInfoBackfillStorageTests`、`BmsLibraryMaintenanceServiceTests`。

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
| R3 | LR2 receipt、scope builder、composition、必要なowned query | DB writer / prune契約、登録root | 既存scope / DB / relocation fixtureを拡張 |
| R4 | resource cache / owner / map、必要なdirectory query | scan ownership、install公開、候補順序 | 既存resource / package fixtureを拡張 |
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
