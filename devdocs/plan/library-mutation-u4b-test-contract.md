# U4b scan producerの共通反映 — Test Contract Packet

状態: 独立test-contract-designerのPhase A→B後にroot承認（2026-09-13）。比較版はU4a完了commit。非空residualの到達性の制限は末尾のroot判断に従う。

## 根拠・分類

[統合計画](library-mutation-unification-plan.md) section 3～5、[data/indexes](../spec/data-and-indexes.md)、[path identity](../spec/path-identity.md) authoritative scan/収束、[read pipeline](../spec/chart-file-read-pipeline.md) File Diff、[性能](../spec/performance-and-scale.md) 全置換と局所反映、[並行性](../spec/workflow-concurrency-and-complexity.md) section 6、[FSDB](../spec/file-db-consistency.md)をauthorityとする。挙動維持boundary refactorで恒久テストを既存更新/必要置換する。新fallback/recovery/gate/state、scan局所delta化、parser/scheduler/chunk policy変更は対象外。

Phase Aは仕様だけでoracleを凍結し、Phase Bの実装/fixture読取りは配置確認に限定した。任意外部DB変更を軽量reloadが取り込む保証、任意時点cancel/rollback保証は追加しない。

## Contract

全項目はbehavior。型名/source不在/広いsnapshot/event列/翻訳文言を恒久assertionにしない。

| Contract ID | 本番入口・前提 | 必須結果・失敗 | 許容差分・識別する誤実装 | 確認方法 |
| --- | --- | --- | --- | --- |
| scan-replacement | startup/FullReinitialize/ReloadFileDiff→authoritative scan→file diff→storage replacement。既存DB writer/lease | DB/canonical exact集合が成功入力に一致。旧exact削除、current保存値保護、BMS/BMSON区別。軽量reloadでDB再読増加なし | 全置換の正当な失効許容。local化によるstale owner、NOCASE削除によるcurrent巻込みを検出 | 実FSDB追加/削除/残存・繰返しscan。既存case-only coverage維持 |
| replacement-dependent-publication | 確定replacement→共通反映→post-lease consumer | installed MD5/SHA/directory、owned hash、playlist、resource index/cache/generationが正本に一致。旧snapshot不変。通知内readbackに必須反映が見え、lease/初期化writer解放済み | 正当full invalidation/初回lazy構築許容。versionだけ進めた旧root、混在resource世代、通知先行を検出 | 実ReloadFileDiff通知caseをextendし旧snapshot/通知内/return後getter2回を接続 |
| empty-residual | 実scanのcleared destination factsが空 | residual由来の追加mutation/通知/失効/全構築なし | scan自体のreplacement/resource公開は許容。全scan無副作用とは判定しない | pipeline factsと実library consumerの仕事量/通知を分担、replacement効果と分離 |
| scan-readiness | scan開始→diff/replacement/required handoff→catalog操作受付 | 開始で未確認、authoritative正常収束後だけ受付。不完全scan/empty保護/apply failureでは開かない。個別recoverable read/軽量parse/chart-info failure件数だけで非authoritativeにしない | UIコピー/内部構成自由。列挙終了だけの早期受付、failure件数だけの拒否を検出 | 既存incomplete/directory/terminal coverageと実scan後受付。setupのreadiness setterを成功証拠にしない |
| scan-failure | 受理済scan→read/parse/writer/required handoff→terminal | 未確定結果を成功公開しない。個別recoverable failure集約、chart-info parse failureで軽量登録を止めない。先行別transaction保持、primary failure維持。任意subscriber例外でdurableを取消さない | 既存chunk/部分成功維持。required失敗を成功化、未commit hash公開、通知例外で確定削除を検出 | 実file failure、既存required handoff/terminal subscriber cases。ログ文字列依存の注入不要 |
| bounded-full-work | scan/replacement/residual→通知→getter/prewarm | 必要な全件走査/全置換は許容、同情報の構築を共通反映接続で重複させない。nochange/empty residual由来の追加全構築なし | cold/明示full replacement費用は別区間。固定wall-clock閾値なし | 既存実work observerと小入力。後続getter/prewarmまで確認、大規模速度は未測定 |

refactorなのでbase greenを許容し、一律red/mutant不要。

## 配置・退役

- `BmsLibraryInitializationFileScanTests` のexact/BMS/BMSON/read failureと、`CatalogMutationOwnerTests` のreplacement receipt/noDBdiffは既存coverage維持/不足だけextend。owner単体を本番接続の代用にしない。
- `BmsLibraryLr2SongDbSyncTests.ReloadFileDiff_PublishesCatalogAfterLeaseReleaseAndIsolatesTerminalSubscriber` を旧snapshot/通知内readback/getter2回へextend。既存DNPは拡張しない。小合成FS/SQLite、explicit options/captured scannerを使用する。
- `LibraryFileScanPipelineOwnerTests` のfacts/handoff/readiness/failureをextend。`FileScanCatalogResidualEvent_RejectsUnsupportedGenericMutation` / `RejectsPackageEntryMutation` / `RejectsUnsupportedParentInvalidation` は広いadapter退役時に削除し、存在しない入力を新typed APIへ再現しない。
- `BmsLibraryDirectoryAvailabilityTests` のReloadFileDiff/Reinitialize failureを維持。`MarkCatalogPathConvergenceCompleted`の直呼びをscan成功の代用にしない。
- `OwnedChartCollectionRefreshTests.ApplyFileScanCatalogResidual_UpdatesInstallDestinationProjectionWithoutGenericMutation` は既存focused契約に限る。非空runtime保証の代用にせず、typed factsの実共通境界へ移せば本番非使用のroot内部applyを削除する。
- `BmsLibraryInitializationTestSupport`、`CapturedChartFileScanner`、`Lr2SongDbSyncTestSupport.TestDatabaseScope`、`TestBmsFactory.MissingEverythingBridge`、既存diagnostic snapshot/work observerを使用。新reflection/全アプリharness/固定sleep/正常待機timeoutなし。

Quickはfile-scan initialization/pipeline/catalog owner/LR2 ReloadFileDiff/directory availability/owned refreshを標準scriptで実行。Functional/凍結review/commitはroot。300秒budget/180秒targetを維持する。

## rootが閉じた到達性と実装判断

1. current-owned chartの非空install destinationを新規投入する本番経路は未確認。`InstallDestinationStateOwner.Apply`がruntime map投入を担い、削除/repairはclear、move/mergeは既存overlayまたはpending entryのrewriteを受ける。pending stateの存在だけではcurrent-ownedへ到達した証明にしない。
2. **非空residualは既存exact cleanup内部契約を保持し、新runtime保証を追加しない。** producerが解除したkind+exact集合だけ反映し、未変更membership/hash/別exact行を巻き込まない既存focused coverageを維持する。新setter/reflection/回復経路を追加せず、非空branchを削除する根拠にも使わない。実scan受入はreplacement/empty residual/readiness/publicationを対象とし、非空初期状態の本番生成経路は未確認と記録する。
3. 任意時点の取消caseは追加しない。既存shutdown/terminal/先行durable契約を維持する。
4. scan residualは専用の確定chart listを持つtyped factsにし、generic LibraryMutationDeltaのunsupported field adapterとproducerの失効booleanを退役する。common反映が実factsから効果を決める。失効3fieldが本番callerゼロになればLibraryMutationDeltaからも除去する。
5. full replacementのtyped request/receiptとresource公開/世代、noDBdiffの意味を維持する。旧全置換を局所deltaへ置換しない。新しいpersistent state/gate/fallback、受付・保存範囲の変更が必要ならrootへ戻す。
