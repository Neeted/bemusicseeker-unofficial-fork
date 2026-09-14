# Operation-scoped Library Mutation Session 実装計画

状態: S1 auto rename、S2 manual / multi-folder move 実装済み（2026-09-15）。次の実装単位は S3 delete / extension rename。S3 以降も調査待ちではなく、本計画の確定スコープとして実装する。

## 目的

複数選択・複数 package を一回の利用者操作で変更するとき、item ごとの filesystem 処理や依存判定と、canonical DB / state / index / publication の確定粒度を分離する。

中心契約は次の一行とする。

```text
1 user operation = 1 LibraryMutationSession = N confirmed changes
```

同じ操作の item loop の中で `ApplyLibraryMutationFactsForFileMutation`、`ApplyInstalledChartStorageTargetsForFileMutation`、owned/index invalidation、LR2 sync、required publication を item ごとに完結させない。後続 item の判定が先行成功に依存する場合は session-local overlay を使い、canonical DB commit を依存判定の通信手段にしない。

この変更は「全 surface を一つの DB transaction にする」計画ではない。catalog DB、package/install rows、外部出力等の durable surface は既存 owner の transaction 境界を維持しつつ、一つの user operation の facts を各 surface へ operation 単位で渡す。

恒久契約は [library-mutation-boundary.md](../spec/library-mutation-boundary.md#mutation-session-契約)、性能契約は [performance-and-scale.md](../spec/performance-and-scale.md#35-操作単位の-mutation-session)、障害時の保証範囲は [file-db-consistency.md](../spec/file-db-consistency.md) を正本とする。

## 背景と採用判断

`2a36701a` は pending estimated install で、先行 package の「予約」ではなく実成功だけを後続 package の所有判定へ反映する必要から package 分類・実行を逐次化した。この correctness 要件は維持する。ただし必要なのは後続判断へ先行成功を渡すことであり、package ごとに canonical storage / catalog apply と publication まで完結することではない。

`0ae8e615` は file/DB 補償境界の導入時に、auto rename の「全 rename の変更 facts を蓄積して最後に一括反映する」構造を、folder ごとの `FileDbMutationExecutor` + durable DB apply へ変更した。限定補償自体を全廃するのではなく、通常発生しない FS / DB / in-memory 例外の完全補償を正常系の per-item commit 粒度の根拠にしない。

## HEAD の構造監査と対象操作

ログの値ではなく、production code の loop と common apply 呼出しを基準に分類する。

| 操作 | 現在の構造 | 本計画の確定変更 |
| --- | --- | --- |
| auto rename | `AutoRenameBatchCoordinator` が plan を `foreach` し、各 folder で `FileDbMutationExecutor.Execute` → `ApplyLibraryMutationFactsForFileMutation` → reverse lookup finalizer。外側は batch でも canonical apply は folder 単位 | S1 で一つの session に folder change を蓄積し、catalog / package refs / reverse lookup / LR2 / publication を一回へ集約する |
| 選択した複数 root folder の移動 | `LibraryFolderMoveCoordinator.MoveLibraryRootFolderWithReceipt` が `foreach (plan)` で `MoveLibraryChartFolder` を呼び、各 folder が common apply を完結 | S2 で auto rename と同じ folder-move session path へ統合する |
| 複数譜面の導入先修正 | `FixInstallationDirectoryAfterAdmission` が `foreach (DetachedFixTarget)` で `MovePackageFilesWithReceipt` を呼び、各譜面で `ApplyLibraryMutationFactsForFileMutation` を完結 | S4 で全成功 path change / install-destination change を同一 session へ集約する。承認済み重複削除は同じ user operation の session change として接続する |
| pending estimated install | `ExecuteEstimatedInstallBatchPlan` は selected package を入力順に処理し、成功 package を `PlannedDestinationOwnershipLookup.AddCommittedEntries` へ追加。各 package の install callback は durable storage apply を完結 | S5 で逐次分類は維持し、lookup を session-success overlay に改名・限定する。全 package の storage/package/catalog effects は session commit で一括反映する |
| auto / force install | `InstallChartPackagesAutoWithProgress`、`ForceInstallPackagesCore` 等が共通 package install core / `MovePackageFilesWithReceipt` を通り、package receipt と storage apply を積み上げる | S5 で estimated と同じ install session core へ統合し、入口ごとの差は request / terminal policy に限定する |
| installed-only resource overwrite / cleanup-only | `ExecuteInstalledOnlyResourceOverwrite` が package loop 内で install または pending source cleanup receipt を確定 | S5 で resource-only / cleanup-only change も install session に集約し、pending/install-row 更新と required publication を操作終端へ寄せる |
| 複数譜面削除 | `RemoveLibraryChartsCore` は FS 結果を集約して `ApplyLibraryMutationFactsForFileMutation` を一回だけ呼ぶ。canonical apply の粒度は既に batch。ただし reverse lookup removal は catalog commit より前に直接反映 | S3 で session API へ載せ替え、reverse lookup change も session commit の derived-state phase へ移す。per-item 化はしない |
| 複数 invalid-extension rename | `InvalidExtensionRenameCoordinator` は複数 FS 結果から `CatalogFacts` を作り `ApplyLibraryMutationFactsUnderExistingReservation` を一回呼ぶ | S3 で既存 batch 構造を保持したまま session API へ移す。性能構造を変えない |
| duplicate folder merge | 現在は一つの merge request に対して common apply は一回。item loop 回帰ではない | S6 で単一-change session へ統一し、merge 後 maintenance を同じ user operation の post-commit phase として terminal result に保持する |
| 単一 folder rename / move | 一対象でも `FileDbMutationExecutor` + common apply | S2 で同じ session API の `N=1` とする。別の互換 write path は残さない |

pending package の追加・単純削除・推定先設定など、owned library catalog を変更しない package-lifecycle-only 操作は `PackageLifecycleOwner` の専門契約を維持する。ただし install / repair の一部として lifecycle change が発生する場合は、その user operation の library mutation session に接続する。

## 1. `LibraryMutationSession` の設計

### 1.1 所有場所と API

`LibraryMutationOwner` に operation-scoped session を追加する。実装量を分離するため、主実装は新しい partial `BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.Session.cs` に置く。

想定 API は次の責務に固定する。名称は実装時に既存型との衝突だけ調整してよいが、粒度は変えない。

```csharp
LibraryMutationSession BeginMutationSession(
    string reason,
    LibraryFileMutationCapability capability,
    LibraryMutationSessionOptions options);

session.AppendCatalogChange(LibraryCatalogMutationFacts facts);
session.AppendPackageReferenceChange(LibraryPackageReferenceFacts facts);
session.AppendInstalledStorageTargets(PackageInstallExecutionResult result);
session.AppendFolderReferenceMove(string oldPath, string newPath);
session.AppendResourceDirectoryRemoval(string path);
session.AppendLr2NormalFolderPathChanges(...);
session.RegisterPostCommitCleanup(...);
LibraryMutationSessionReceipt receipt = session.Commit();
```

`Append*` は canonical DB / collection / index / notification を変更しない。入力をコピーまたは immutable facts として保持するだけとする。操作別 summary、elapsed time、UI report は session change の入力にしない。

`LibraryMutationSessionReceipt` は operation 単位の terminal fact とし、少なくとも次を保持する。

- confirmed change count と change category 別件数。
- durable surface ごとの apply 成否。
- physical failure / DB failure / required internal apply failure / cleanup failure。
- confirmed-success targets、failed target、unprocessed targets を UI report が区別するための bounded facts。
- post-lease publication / terminal report に必要な immutable data。

既存 `FileDbMutationBatchReceipt` の `N個のper-item durable receipt` を新 session の正本にしない。移行対象 route では session receipt へ置換し、互換のために偽の item durable receipt を生成しない。

### 1.2 Commit の一回性

`LibraryMutationSession.Commit()` は含まれる facts を正規化し、該当する surface について次を operation 単位で行う。

1. catalog relocation / removal を `CatalogMutationOwner.ApplyCatalogMutation` へ一回渡す。
2. installed target / storage rows を、install session 全体の `ChartStorageTargetSet` として一回反映する。
3. install destination / installed package path / pending-install-row 等の package reference facts を一回反映する。
4. live owner / owned collection / primary/full installed lookup / playlist index / duplicate / parent cache の semantic apply / invalidation を集約する。
5. reverse lookup の moved / added / removed directories を集合で一回反映する。
6. LR2 normal-folder sync を対象 path 集合で一回実行する。
7. required publication を一回準備し、lease 解放後の public notification / terminal report を一回発行する。

一つの physical DB transaction にまとめられない surface は既存 owner の transaction を順に使う。ここでいう一回は「同じ surface へ item 数だけ同じ apply を呼ばない」という意味である。gateway 内部の SQL parameter chunking や bulk writer の chunk transaction は許容する。

### 1.3 Session-local overlay

install の後続判断用に `LibraryMutationSession` または install 専門 child state が operation-local overlay を持つ。

- baseline: session 開始時の `IInstalledChartLookupIndex` / install destination state。
- successful additions: physical install が成功し、session change として受理した package entry の hash / destination directory。
- removals / moves: 後続判断に必要な場合だけ exact identity で反映する。
- canonical index generation、public notification、durable receipt の代用にはしない。

現行 `PlannedDestinationOwnershipLookup.AddCommittedEntries` は `AddSuccessfulEntries` 等へ改名し、「DB committed」という意味を除く。`2a36701a` が保証した、手動で `INSTL DST` を消した package や実行失敗 package を先行所有扱いしない契約をこの overlay の test で維持する。

### 1.4 障害時の扱い

正常系の速度を per-item compensation のために分解しない。共通方針は次とする。

- missing source、destination exists、stale target、承認済み no-op 等、事前または item 開始時に確定できる deterministic result は既存 feature semantics に従って skip / reject し、成功 change に入れない。
- 予期しない filesystem exception は、現在 item の結果が不確定ならその item と依存 suffix を成功扱いせず停止する。既に確認済みの成功 change は失わず、一回の session commit を試みて terminal report に残す。
- session DB commit が失敗した場合、全 filesystem change の自動 rollback、transaction replay、再帰的 compensation を新設しない。確認済み physical change と DB failure を terminal result / UI / diagnostic へ渡す。
- required in-memory/index apply failure も通常成功に格下げしないが、DB durable success 後に filesystem rollback へ戻らない。
- cleanup と optional notification の失敗は durable state の意味を変えず、既存の terminal distinction を operation 単位で保持する。

`FileDbMutationExecutor` の既存 per-item staging / backup は、まだ移行していない単一 route または本当に上書き保護が必要な narrow operation では残してよい。multi-change を per-item DB commit に戻すための共通前提にはしない。

## 2. 実装単位

各単位で production ingress、owner、DB / derived state、terminal workflow を一緒に閉じる。後の単位を「調査して必要なら対応」にはしない。

### S1 — auto rename を最初の session 実装にする

対象:

- `BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.cs`
- 新規 `BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.Session.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/AutoRenameBatchCoordinator.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs`
- 必要に応じ `CatalogMutationOwner.cs` / session facts 型
- `BmsLibraryFolderRenameRefreshTests`、`BmsLibraryMutationBoundaryTests`、`CatalogMutationOwnerTests`

実装:

1. outer `LibraryFileMutationLease` / capability の中で session を一回開始する。
2. 各 auto-rename plan は current preflight / source existence / destination collision / drive-root / duplicate-source 判定を維持する。
3. destination が存在しない folder rename は per-item `FileDbMutationExecutor.Execute(...DB callback...)` を使わず、既存 `IFileMutationService` の folder move を直接実行する narrow physical step に戻す。move 前に `LibraryFolderMoveFacts` を構築し、move 成功後だけ session へ append する。
4. 全成功 folder の `CatalogFacts` / `PackageReferenceFacts` / `StorageRowPathNotificationPolicy` / LR2 path change / reverse lookup move を session に集約する。
5. loop 終端または予期しない physical failure 後に session commit を一回だけ行う。`ApplyLibraryMutationFactsForFileMutation` を folder loop から除去する。
6. reverse lookup は `UpdateMovedFolderReferences` 相当へ全 move 集合を一回渡す。LR2 normal-folder sync と owned/index dispatch / publication も一回にする。
7. `AutoRenameBatchResult` は `mutationReceipts=N` を正本にせず、operation-scoped session receipt と diagnostics を返す。progress は現在どおり item 単位でよい。
8. `BmsLibraryDbGateway.ReplaceFolderRecords` の `songDb.Table<folder>().ToList()` 全表 materialize は廃止する。session の old folder path 集合を parameterized exact lookup で chunk 取得し、対象 row だけ delete/update/insert する。一つの session transaction 内で実行する。

受入:

- 2 folder / 43 folder 等、N>1 の auto rename で catalog apply、folder-row target query batch、owned mutation dispatch、reverse lookup apply、LR2 sync、required publication が operation 単位の期待回数になる。
- 各 folder の path、install destination / installed package reference、BMS / BMSON storage row、LR2 folder row が全成功集合と一致する。
- missing source / duplicate source / destination collision は成功 change に入らない。
- 途中の physical exception では既成功 change を一回反映し、失敗対象と未処理対象を terminal で区別する。全成功 change の rollback は要求しない。
- 単一 folder でも同じ session path で動く。

### S2 — 手動 rename と複数 folder move を同じ folder session path へ統合する

対象:

- `BmsLibraryInternal/LibraryFolderMoveCoordinator.cs`
- `BMSLibrary.LibraryMutationOwner.cs` / `.Session.cs`
- `RegularChartListOwner`、`SelectedChartMutationWorkflowOwner` の terminal receipt 接続
- `BmsLibraryFolderRenameRefreshTests`、`SelectedChartMutationWorkflowOwnerTests`

実装:

1. `MoveLibraryRootFolderWithReceipt` の `foreach (plan) -> MoveLibraryChartFolder -> common apply` を廃止し、plan 全体で session を一つ開始する。
2. 各 source/destination の preflight と physical move は item ごとに実行し、成功 facts を append する。auto rename と共通の `AppendFolderMove` / aggregation path を使う。
3. manual single rename は `N=1` の同 session API にする。per-item DB callback のためだけの `TryMoveLibraryChartFolder` 分岐を残さない。
4. notify-storage-row-path policy は change ごとに保持し、session commit で対象 kind を正しく合成する。auto rename の抑制と手動 move の通知を reason 文字列で分岐しない。
5. reverse lookup、folder cache、duplicate、playlist、LR2、normal refresh の invalidation / publication を操作全体で一回にする。
6. ViewModel terminal は `FileDbMutationBatchReceipt` の item 数ではなく session receipt の確定 change / failure facts を表示する。

受入:

- 複数選択 folder move で common catalog apply / derived dispatch が folder 数に比例しない。
- single rename の observable behavior、destination-exists、root拒否、dialog解放後表示を維持する。
- BMS-only / BMSON-only / mixed move の storage-row notification semantics を維持する。

#### S2 Test Contract Packet — `LIBMUT-S2-20260915`

本 unit は user が S2 実装を明示承認した本計画と `library-mutation-boundary.md` の mutation-session / folder-terminal 契約を authority とする。current implementation や既存 expected 値は oracle にしない。

| Contract ID | production ingress -> observable impact | 必須結果 / 検出すべき誤実装 | 配置 |
| --- | --- | --- | --- |
| `S2-SESSION-AGGREGATE` | `BMSLibrary.MoveLibraryRootFolderWithReceipt` -> catalog/state/refresh publication | N folder の confirmed facts を一つのsessionでcommitし、owned/normal publicationは操作単位。per-folder common applyへ戻る実装を検出する | `BmsLibraryFolderRenameRefreshTests` extend |
| `S2-FAILURE-PREFIX` | 同 ingress -> physical failure / DB apply failure terminal | physical failure はconfirmed prefixを一回commitしてfailed/unprocessedを区別する。DB apply failureはconfirmed physical moveを自動rollbackしない | 旧 manual-recovery test を replace |
| `S2-STORAGE-NOTIFY` | 同 ingress -> normal refresh storage-row flags | BMS-only / BMSON-only / mixed のkind通知を保持し、mixedは一回のoperation publicationで両kindを通知する | 同 fixture extend |
| `S2-TERMINAL-SESSION` | `SelectedChartMutationWorkflowOwner.MoveAsync` / regular rename -> terminal dialog | per-item batch receiptを合成せずsession factsを保持し、gate/activity/suppression解放後に一回reportする | `SelectedChartMutationWorkflowOwnerTests` / 既存 regular rename fixture update |

許容差分は内部helper名、diagnostic reason、対象列挙順、localized copy。source textやprivate call orderは固定しない。共有資源は既存の固有temp DB/FSとlocal dialog portsを用い、完了はmodel return / workflow Taskで待つ。

### S3 — 既に batch の delete / extension rename を session API へ統一する

対象:

- `BMSLibrary.LibraryMutationOwner.cs` の `RemoveLibraryChartsCore`
- `BmsLibraryInternal/InvalidExtensionRenameCoordinator.cs`
- pending extension rename の package-lifecycle接続
- `OwnedChartCollectionLibraryMutationTests`、`BmsLibraryFolderRenameRefreshTests`、`SelectedChartMutationWorkflowOwnerTests`

実装:

1. `RemoveLibraryChartsCore` は現行の「FS結果を全対象から作り、common apply一回」を維持し、そのfactsを `LibraryMutationSession` にappendしてcommitする。
2. `resourceIndexOwner.RemoveUnderSourceDirectories(...)` を catalog commit 前の直接mutationから外し、成功 deleted directories を session の derived-state facts として登録する。catalog成功後の同session applyで一回反映する。
3. invalid-extension rename は既存の複数FS処理→`CatalogFacts`一括反映をそのまま session API へ移す。per-file receiptを追加しない。
4. pending extension rename で install row / pending entry removal が伴う場合は、owned catalog sessionを必要としない pure lifecycle route と、library catalog changeを伴うrouteを混同しない。
5. delete / rename terminal report はsession receiptを使い、既存のsuccess-only selection / UI refresh semanticsを維持する。

受入:

- delete / extension rename は移行前より canonical apply 回数を増やさない。
- delete catalog failure 時に reverse lookupだけ先行して消えない。
- parent/child folder delete、partial failure、BMS/BMSON、duplicate hash の既存結果を維持する。

### S4 — 複数譜面の導入先修正を session 化する

対象:

- `BMSLibrary.LibraryMutationOwner.cs` の `FixInstallationDirectoryAfterAdmission` / `FixInstallationDirectoryCharts`
- package physical move helper
- `BmsLibraryFolderRenameRefreshTests`、`PendingPackageWorkflowOwnerTests`、関連 repair tests

実装:

1. `foreach (DetachedFixTarget)` の前で一つの session を開始する。
2. `CreateInstalledChartKeySnapshotExcludingChartsUnsafe` は一回の baseline とし、修復成功を後続 duplicate 判定へ反映する必要がある場合は session-local overlay を使う。
3. 各 `MovePackageFilesWithReceipt` が DB callback を呼ぶ形を session 用 physical result へ分離する。成功した moved chart から `CreateFixMutationFacts` を append し、loop 内で `ApplyLibraryMutationFactsForFileMutation` を呼ばない。
4. 重複と判定され、利用者が削除を承認した chart は `ChartsToRemove` を別の後段 `RemoveLibraryChartsCore` へ再入させず、同一 session の removal change へ接続する。削除 filesystem phase が必要なため、session が repair moves と approved removals の確定 facts をまとめて commit する。
5. resource maintenance target は全成功 repair から集合化し、session commit 後の required post-commit phase として一回処理する。
6. terminal result は moved / duplicate skipped / approved removed / physical failure / catalog failure / maintenance failure を一つの user operation receipt で保持する。

受入:

- N件修復で catalog/path/package-reference common apply がN回にならない。
- duplicate-before-move の既存意味、承認済み削除、stale target、BMS/BMSON を維持する。
- repair成功後maintenance失敗で先行path moveをrollbackしないが、通常成功にもしない。

### S5 — 全 package install 入口を operation-scoped install session へ統合する

対象:

- `BMSLibrary.PackageInstall.cs`
- `BmsLibraryInternal/BmsLibraryPackageInstallService.cs`
- `PackageInstallExecutionResult.cs`、`PendingInstallBatchResult.cs`、force / auto / overwrite result 型
- 必要な install storage/catalog owner
- `BmsLibraryPackageInstallServiceTests`、`PendingPackageWorkflowOwnerTests`、`PackageInstallWorkflowOwnerTests`

対象 ingress をすべて同じ変更で列挙して閉じる。

- auto install: `InstallChartPackagesAutoWithProgress` / `ApplyAutoInstallWorkflowWithFileMutationReceipts`
- pending estimated install: `ExecutePendingEstimatedInstall` / `ExecuteEstimatedInstallBatchPlan`
- force install: `ForceInstallPackagesCore`
- manual / explicit destination install が共有する `installChartPackages` core
- installed-only resource overwrite: `OverwritePendingInstalledOnlyPackagesResources` / `ExecuteInstalledOnlyResourceOverwrite`
- cleanup-only package source removal が install command の成功後処理として発生する経路

実装:

1. outer ingress が一つの install session を作り、package service に渡す。nested package call が別 session / durable publication を作らない。
2. package の classify / destination resolve / component enumeration / physical file change は入力順に実行してよい。`2a36701a` の correctness のため、先行 physical success は `PlannedDestinationOwnershipLookup` を改名した session-success overlay へ直ちに追加する。
3. failed / skipped / DST-cleared package は overlay へ追加しない。未実行予約を「既所持」にしない。
4. `MovePackageFilesWithReceipt` の `applyDurableStorageRows` callback を session route から外す。package physical result は AddedCharts / AddedEntries / excluded resources / destination / cleanup action / failure facts を返し、session が蓄積する。
5. `ApplyInstalledChartStorageTargetsForFileMutation` は package loop 内から呼ばず、全成功 package の `ChartStorageTargetSet` を union して session commit で一回呼ぶ形へ再編する。同一 chart identity の重複 target は既存 exact identity 規則で正規化する。
6. pending package removal、installed package display record、install row delete/update、install destination clear、deferred maintenance target を operation 単位で集約する。package collection publication も一回にする。
7. reverse lookup / resource scan は成功 destination directory の集合を作り、重複 directory を除いて一回の apply / warmup にする。resource-only package も chart追加0を理由に落とさない。
8. source cleanup は、保存先とsession durable applyが成功した package を集合で処理する。cleanup failure は package別factsを残すが、canonical applyを再実行しない。cleanup-only item はその成功factsだけを session の lifecycle changesへ追加する。
9. packageごとのprogress / skip detail は維持するが、required publication / success dialog / installed-tree refresh は session terminal から一回行う。

受入:

- 1 package と複数 package の結果が同等で、複数 package 時の storage/catalog/package publication 回数がpackage数に比例しない。
- 同一hashの先行packageが実成功した場合だけ後続判定へ影響し、手動DST clear、physical failure、skip、cancelled packageは影響しない。
- smart overwrite ON/OFF、resource-only、cleanup-only、split destination、manual hold、force、auto の既存observable contractを各production ingressで検証する。
- 途中のunexpected failureは未処理suffixを成功扱いせず、既成功packageと失敗packageを一つのsession terminalへ保持する。

### S6 — merge を単一-change session と post-commit phase へ統一する

対象:

- `BMSLibrary.LibraryMutationOwner.Merge.cs`
- `DuplicateMaintenanceWorkflowOwner`
- package physical merge helper
- `BmsLibraryDuplicateServiceTests`、`DuplicateMaintenanceWorkflowOwnerTests`

実装:

1. merge request ごとに `N=1` session を開始し、`MovePackageFilesWithReceipt` の per-item durable callbackではなく、physical merge successから catalog/package facts をappendする。
2. merge destinationのcatalog apply、reverse lookup、required publicationをsession commitで一回行う。
3. merge後resource maintenanceは、deadlock回避に必要な既存reservation解放・再取得自体は維持してよいが、logicalには同じ user operation の `PostCommitMaintenance` phase としてsession terminalへ結果を戻す。二つ目のlibrary mutation sessionとして成功表示を分割しない。
4. maintenance failureはmerge durable successをrollbackせず、merge success + maintenance failureを同じ terminal resultで表す。

受入:

- 現在の一merge一applyより仕事量を増やさない。
- destination type conflict、sourceなし、resource-only merge、maintenance failure、通知解放後の既存契約を維持する。

### S7 — 旧 per-item apply API と receipt 前提を退役する

S1～S6 の production route 移行後に行う cleanup unit。対象routeを残したまま名前だけ消さない。

- `ApplyLibraryMutationFactsForFileMutation` は session commit の内部primitiveへ狭め、operation coordinator / item loop から直接呼べない形にする。
- `ApplyInstalledChartStorageTargetsForFileMutation` も install session commit 内部へ狭める。
- `FileDbMutationBatchReceipt` を「複数 user change の正本」とする model / ViewModel API を session receiptへ置換する。単一の既存executorを残すrouteがあれば、そのlocal resultに限定する。
- `PlannedDestinationOwnershipLookup.AddCommittedEntries` 等、DB durableとsession successを混同する命名を残さない。
- reason文字列によるsession policy分岐を追加しない。notification policy / operation options はtyped valueで渡す。
- operation内で同じ reverse lookup / playlist prewarm / cache invalidation / LR2 sync を複数回発火する旧 callback を削除する。

## 3. Gateway / set-oriented DB 変更

session化だけで itemごとのtransactionは減るが、operation単位のDB処理も対象行集合に比例させる。

### Folder table

`BmsLibraryDbGateway.ReplaceFolderRecords` は現在 `songDb.Table<LR2SongDB.folder>().ToList()` でfolder table全件をmaterializeしてからold path辞書でfilterする。S1で次へ変更する。

- old folder path の distinct exact set を作る。
- SQLite parameter limit を超えないchunkで `WHERE path IN (...)` 相当のparameterized queryを発行する。
- 取得したrowだけdelete / new pathへinsert-or-replaceする。
- 同一transaction内で全chunkを処理する。
- target rowが存在しない場合は既存の意味に従いno-opとし、全table fallback scanをしない。

### Install / path rows

S4/S5では、同じ session の path change / installed target / install-row change を collection request として gatewayへ渡す。既存 bulk / set SQL がある場合は再利用し、item loop 内で transaction を開閉しない。新しい ORM abstraction は作らず、現在の `BmsLibraryDbGateway` / catalog owner をwriterとして維持する。

## 4. 構造上の性能受入 marker

wall-clockだけでなく、設計退行を直接検出できる operation-level markerを追加する。同期per-item logを増やさず、session終端で集約値を一回出す。

例:

```text
library_mutation_session_done
  reason=auto_rename_folders
  changeCount=43
  catalogApplyCount=1
  installedTargetApplyCount=0
  packageReferenceApplyCount=1
  reverseLookupApplyCount=1
  lr2SyncCount=1
  requiredPublicationCount=1
  folderDbTargetRows=43
  folderDbFullScanCount=0
```

operationによって該当しないsurfaceは0でよい。S1～S7の各testでは秒数ではなく、少なくとも relevant apply count と永続結果を検証する。実環境 benchmark は別途性能受入として有用だが、`N changes -> N canonical applies` を許容するための前提にはしない。

## 5. テスト方針

恒久testは実production ingress / ownerを通し、source text検索だけを契約testにしない。各unitで既存fixtureを優先し、次の独立性を持たせる。

- **structural count:** fake / observerでcatalog apply、installed target apply、reverse lookup、LR2、publication回数を確認する。
- **persistent result:** temp filesystem / SQLiteで全成功changeのold/new path、row、package stateを確認する。
- **sequential dependency:** installは先行実成功だけが後続判定へ入ることを確認する。
- **failure terminal:** deterministic skipとunexpected failureを分け、confirmed success / failed / unprocessedを確認する。
- **notification timing:** model lease / dialog scope解放後のpublic notificationを確認する。
- **negative control:** S1は旧per-folder apply、S4は旧per-package storage applyを再現できる観測点を一つ持ち、修正前にN回となること、修正後にoperation-level回数になることを示す。

各unitの関連Quick成功後、通常の最終snapshotでFunctionalを一回実行する。performanceのwall-clock比較を行う場合は [performance-and-scale.md](../spec/performance-and-scale.md) の同条件・同完了範囲を使う。

## 6. 実施順と完了条件

| Unit | 状態 | 完了条件 |
| --- | --- | --- |
| S1 auto rename | 完了 | `LibraryMutationSession`を導入し、auto renameのconfirmed physical moveをoperation単位で一括commitする経路へ移行。folder DBはexact pathのchunk queryへ変更し、session terminal / partial-failure suffix / operation-level publicationを既存auto-rename testsへ反映。恒久契約は `library-mutation-boundary.md` / `file-db-consistency.md` の既存 `FSDB-SESSION` / `FSDB-REPORT` を使用。 |
| S2 manual / multi-folder move | 完了 | manual rename と複数 folder move を共通 `LibraryMutationSession` pathへ統合し、confirmed prefix / failed / unprocessed、storage-row policy合成、operation-level reverse lookup / LR2 / publication、ViewModel session terminalへ移行。旧 per-item folder `FileDbMutationExecutor` callback と manual-recovery前提を撤去。 |
| S3 delete / extension rename | 未着手 | 既存batch構造をsession APIへ統一、delete reverse lookupのcommit前直接mutation撤去 |
| S4 installation-directory repair | 未着手 | N chart repair + approved removalが同session、per-chart DB callback撤去、maintenance terminal統合 |
| S5 all install ingress | 未着手 | auto/estimated/force/manual/resource-only/cleanup-onlyがinstall session、success overlay、per-package canonical apply撤去 |
| S6 merge | 未着手 | single-change session、post-commit maintenanceを同一logical operation terminalへ統合 |
| S7 old API cleanup | 未着手 | item-loopから旧apply API参照0、per-item batch receipt前提をproduction terminalから除去、operation-level marker整備 |

全体完了は、上表の全unitが完了し、複数選択 / 複数package の production route に「item loop 内で canonical mutation owner を完結する」経路が残っていないこととする。新しいcompatibility route、retry queue、persistent journal、global rollback mechanismを追加して完了条件を満たしたことにはしない。
