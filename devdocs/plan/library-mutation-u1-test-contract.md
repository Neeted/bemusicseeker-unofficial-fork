# U1 通常削除・マージの確定事実と連続操作 — Test Contract Packet

状態: 独立test-contract-designerがPhase A（仕様から判定）→Phase B（配置確認）の順に設計し、rootが2026-09-13に承認。比較版 `007b3216`。実装・redは未実施。

## Authorityと範囲

利用者が [統合計画](library-mutation-unification-plan.md) U1～U5を承認した。U1は性能不具合修正と共通反映へのrefactorである。[性能仕様](../spec/performance-and-scale.md) section 1/3/4、[path identity](../spec/path-identity.md) 基本方針/R2a/R2b、[data/indexes](../spec/data-and-indexes.md) の順序・snapshot契約、[FS/DB](../spec/file-db-consistency.md) section 2～5、[変更境界](../spec/library-mutation-boundary.md) のlease・削除・durable契約を根拠とする。ログ、現行実装や既存expectedはoracleにしない。

本番経路は重複workflow production port→`BMSLibrary.MergeChartDirectory`→file owner→package executor/catalog commit、および選択workflow `DeleteAsync`→production store→`RemoveLibraryCharts`→canonical解決/FS/catalog apply。authoritative file diff完了、process-exclusive DB/single writer、確認済み対象、既存admissionを前提とする。rootの内部apply直呼びは実操作性能の代用にしない。

## Contract

| Contract ID | 必須結果 | 許容差分・検出する誤実装 | 確認方法 |
| --- | --- | --- | --- |
| warm連続操作 | 同じlibraryで異なる対象へ2回実merge/削除。FS・DB・正本・lookupが一致。warm primary/full installed・所持hash・playlist解決を全失効しない。通知読取り、各getter2回、既存prewarmまで全構築/materializeを押し出さない。旧lookup snapshotは不変 | cold必要初回構築、影響bucket、木探索の対数増分、既存親prefix/重複graph/明示consumer列挙は許容。更新の省略や次getterで全構築する誤実装を検出 | 背景16/128、固定Δとbucket fan-out。実source訪問・map materializeと結果を同時観測。mergeのbase-redを実施 |
| sourceなし | 有効なsource/destinationとreadinessを持つがsourceに所持譜面なし。lookup構築前に未適用終了し、FS/DB/正本/旧snapshot不変 | 受付とsource確認は許容。missing fileを成功削除扱いするケースとは分離 | cold/warmの対照、実build/source訪問を観測 |
| 旧factsと候補 | BMS/BMSON、同hash別配置で、破壊前のkind/exact path/MD5/SHA/候補順を反映。非最後のownerで所持維持、最後で除去。installed配置とplaylist代表が入力から決まる結果になり、旧snapshotは不変 | DTO/内部名/構造共有方式は自由。live storage owner自体のpath不変は要求しない。未計算hashと伝達漏れを区別。hash全owner除去、変更後ownerから旧値取得、BMSONのsong混入を検出 | 実mergeの移動/duplicate skip、実削除の非最後→最後。両hash、既存snapshotからの再読取り |
| 確認済みexact集合 | 確認した旧exact keyをDB/storage/canonical/索引へ同じ集合で反映。destination利用者列と未対象行を保持 | FSの同一対象一回処理は許容。NOCASEで対象縮小/拡大、代表以外の行残留を検出。同FS複数表記の新保証は下記root判断に従う | 通常の異なる実pathを本番入口で確認。既存exact source casesは維持 |
| failureと部分成功 | 確認済FS対象だけ反映。DB失敗に成功factsを付けず削除済FS事実を保持。durable後失敗は未commit/通常成功に変換しない。先行成功とmergeの限定補償を維持 | 削除FS復元は要求しない。既存cleanup残留を許容。失敗対象の索引除去、成功先行公開、必須例外握潰しを検出 | 既存I/O失敗adapter、実SQLite abort trigger、merge finalization失敗caseへwarm/通知結果を追加。不必要な全failure組合せは追加しない |
| 受付と通知再入 | BusyはFS/DB/正本無副作用。必須反映後の通知が現在の索引を読める。解放済み境界で通知再入が成立し、subscriber失敗でdurable結果を変えない | merge後保守の解放/再取得を維持。非契約の通知順は自由。新外側gateや旧索引公開を検出 | 既存workflow Taskと実model再取得probe。未変更bridgeは既存coverageを維持 |

resource-healthはU1でmerge後再検査のdefer契約を変更しない。warm全失効を防ぐ対象は表の4索引群であり、正当なresource入力変更を無視するテストにしない。

## 観測・red

- 既存 `OwnedChartHashIndexStoreWorkObserver` / `PlaylistLibraryResolveIndexStoreWorkObserver` を利用する。installed snapshotにだけ付けたobserverでは新stateの再構築を取り逃がすため、実source列挙・map列挙・bucket copyへ届く既存内部診断境界の最小拡張を許可する。公開test API、ログ文言assertion、新しい常時I/Oは追加しない。
- SQL仕事量を観測する場合、`SqliteStatementObservation` は実gateway接続へ接続する。readback接続の件数を本番queryの証拠にしない。PROFILEのFULLSCAN_STEP/VM_STEPと実rowを区別する。観測できないSQL指標を0として合格させない。
- 実mergeのwarm連続操作を比較版で実行し、全構築またはsource訪問違反をredとして確認する。compile/setup失敗はredではない。構造上のbase実行不可だけではmutant必須としないが、observerが新stateを取り逃がす懸念が残る場合は成功merge後全失効の限定変異で識別力を確認し、必ず復元する。
- 固定背景の全仕事量完全一致を要求しない。全件走査/コピーの回数・訪問と関連bucketの増分を分ける。小規模計算量確認を21万譜面の速度検証とは報告しない。

## 配置と退役

| Contract | 配置・方法 | 退役 |
| --- | --- | --- |
| warm/sourceなし/旧facts | `BmsLibraryDuplicateServiceTests` をextend。候補名 `MergeChartDirectory_TwoWarmOperationsKeepIndexesCurrentWithoutFullRebuild`、`MergeChartDirectory_NoSourceChartsDoesNotBuildLookups`。既存duplicate-skip/exact casesへ必要なsnapshot確認 | mergeのpath-only索引facts/caller全失効。source absence testは作らない |
| warm/旧facts/部分失敗 | `OwnedChartCollectionLibraryMutationTests` をextend。候補名 `RemoveLibraryCharts_TwoWarmOperationsPreserveRemainingOwnersWithoutFullRebuild`、既存OnlySuccessful/CatalogFailure cases | `RoutesUnregisterThroughOwnedMutationAndInstalledLookupDelta` は同じ契約を新caseが覆えば統合削除可 |
| commit/通知 | `BmsLibraryFolderRenameRefreshTests` の `ApplyLibraryMutationDelta_DurableCatalogFailureLeavesConsumerStateUnchanged` / `CommitsCatalogBeforePublishingOwnedCollectionChange` / `PublicNotificationFailureKeepsCatalogCommit` を実削除fixtureへreplace | 置換した3 direct-delta case。同helperは他caseが使う間は残す |
| prewarm | `PlaylistWorkspaceDetailRefreshTests` の既存scheduler harnessを参照・必要時extend。実 `PlaylistDetailDataSource` を同libraryへ接続し、queued workと `GetPlaylistLibraryIndexPrewarmTask()` を完了させる | fake datasourceのcall countだけで性能合格としない。新harnessや固定sleep不要 |
| 受付/通知 | Selected/Duplicate workflow testsと実merge finalization/reentry testsを原則維持。不足するbridgeだけextend | mutation boundary testsは主にdialog scopeのためBusy検証の代用にしない |

既存 `WithTemporarySongDb`、GUID temp FS、実SQLite、既存IFileMutationServiceを使う。共有Settings書換えや新DoNotParallelizeは不要。同期receipt/outcome return、workflow Task、prewarm Taskで正常完了を待つ。dispatcher内は `TestUiDispatcherHost.AwaitTaskOnDispatcher`。失敗期限は既存runnerを継承する。

反復は変更caseへ絞った `scripts/verify-refactor.ps1 -Mode Quick -TestFilter`。統合候補filterは `BmsLibraryDuplicateServiceTests|OwnedChartCollectionLibraryMutationTests|BmsLibraryFolderRenameRefreshTests|SelectedChartMutationWorkflowOwnerTests|DuplicateMaintenanceWorkflowOwnerTests|BmsLibraryMutationBoundaryTests|PlaylistWorkspaceDetailRefreshTests` の各 `FullyQualifiedName~` 条件をOR結合する。Functionalと凍結レビューはrootが担当する。

## rootが閉じた到達可能性の判断

1. ownerなしDB-only cleanupをU1のUI入口で新たに認可するproducerは確認されていない。**既存catalogのcleanup契約を保持し、U1は到達可能なowner付き除去で統合を保証する。** 新しいorphan探索、fake-only下流状態、復旧機構は追加しない。既存DB-only coverageは削除しない。
2. 同FS複数exact行の既存fixtureはreadinessを直接設定している。**既存caseを温存し、新しいsame-FS runtime保証は追加しない。** 通常の複数実pathでexact伝達を確認し、未収束の入口拒否を維持する。収束規則を弱めたり複数exact key伝達契約を削除する許可ではない。

workerは表の結果・許容差分を変更しない。fixture構成・test名・内部observer接続の調整は可。認可・readiness・補償・通知再入の意味変更、所有path外の必要変更はrootへ戻す。
