# U3 全導入経路の確定事実と共通反映 — Test Contract Packet

状態: 独立test-contract-designerのPhase A→Bを経てroot承認（2026-09-13）。比較版はU2完了commit。実装はU2後に直列で行う。

## 根拠と分類

[統合計画](library-mutation-unification-plan.md) section 3～5、[変更境界](../spec/library-mutation-boundary.md) のdurable・destination coherence・cleanup・deferred effects、[FSDB整合](../spec/file-db-consistency.md)、[path identity](../spec/path-identity.md)、[性能](../spec/performance-and-scale.md)、[並行性](../spec/workflow-concurrency-and-complexity.md) section 6をauthorityとする。挙動維持のrefactorと実入口warm処理量coverageの補完であり、恒久テストを必要とする。current implementation/output/expected/翻訳をoracleにしない。

前提はprocess-exclusive DB、single writer、catalog依存導入のpath収束、現在のpending packageと承認済み導入先、操作内設定snapshot。新owner/gate/command bus、batch全体transaction、narrow writeのupsert化、pure pending API吸収、retry/recoveryは対象外。

## 判定基準

全項目はbehavior契約。内部型・helper・採番表記は固定しない。

| Contract ID | 本番入口・前提 | 必須結果・失敗 | 許容差分・検出する誤実装 | 確認方法 |
| --- | --- | --- | --- | --- |
| 通常導入の確定先 | drop→auto、pending→estimated/force→package service→FS executor→catalog。実sourceと収束済catalog | 実bytes、receipt destination、DB exact path、正本、package entry/登録、lookupが一致。衝突旧file/row維持。commit前に未確定先を公開しない | 採番表記は自由。basename/root再計算、一時live path漏れ、旧宛先DB上書きを検出 | BMS/BMSON・single/nested代表。実FS/SQLite/正本/lookup。通常3入口と別resource-only入口の接続を確認 |
| exact identityと内容再利用 | 同hash別配置のforce、BMS/BMSON、inline metadata | 旧owner/利用者列を保持して新配置を反映。解析再利用でもdestination resource検査を実施。narrow metadataの保存範囲維持 | object identityや解析cache方式は自由。hashをmembership keyにする、対象外置換、resource検査省略を検出 | exact SQL readback、実inline成功/parse failureを維持。任意stale同keyのdirect upsertを本番代用にしない |
| resource-only | pending→OverwritePendingInstalledOnlyPackagesResources→estimated execution→terminal。既所持の解決済destinationと実resource target | catalog membershipを増やさずbytes/health/warning、必要なpackage/destination状態を反映。既存譜面/保存値維持。cleanup-onlyに架空chart destinationを作らない | chart差分ゼロをno-op扱い、通常追加へ変換、source検査結果だけ流用を検出 | 通常導入と別case。BMS/adapterless BMSON代表、exact行集合、bytes、警告、package結果 |
| package境界と終端 | 各batchの本番入口、packageごとのcommit/required apply/cleanup、実token取消 | 先行durable receipt/FSDBを保持。次packageは先行確定を含む所有で判断、未commitを予約に数えない。precommit/manual recovery/required finalization/cleanup-onlyを区別。必須失敗でsuffixと当該成功公開を停止。取消でもprefix維持 | 独立package継続は既存契約。batch rollback、receipt保存前throwによるprefix喪失、未実行所持予約、必須失敗のwarning化を検出 | 実FSDB prefix、実gateway SQLite trigger、既存file adapter。callback-only偽durableで代用しない |
| cleanup承認範囲 | 同一操作の設定/独立所持snapshot→preflight→durable→cleanup | Preserveは消費分のみ。追加削除許可も全候補が読取/hash確定・独立所持の場合だけ。非譜面/未所持等があれば追加分全保持。destinationと祖先保護。receiptを後段前に保持 | 新回復保証不要。source自身/未実行予約を証拠にする、追加一部だけ削除、packageごと設定再読を検出 | 既存ON/OFF、範囲外所持、未所持/非譜面混在、parent保護を維持。通常/resource-onlyのcaller接続 |
| 解放後公開と受付 | workflow→admission→required apply/terminal→lease/scope解放→subscriber/report | 通知で確定DB/lookupを読め再入可能。通知失敗でdurable/primary failureを変えず後続公開を妨げない。Busy無副作用、ZIP予約/pure pending/推定/設定/オンライン例外維持 | 任意通知集約/文言は自由。解放前通知、自己Busy、subscriberによる結果喪失、新global gateを検出 | 実model subscriberとworkflow awaited terminalを分担。新private lock reflectionなし。無変更例外は既存coverageとroute review |
| warm仕事量 | 通常3入口/resource-onlyの実model、背景16/128・固定Δ/fanout、事前warm | 正しいFSDB/正本/lookupとともに2操作・各getter2回・通知readback/既存prewarmまで全catalog/巨大root反復列挙/copy/sortなし。旧snapshot維持 | cold必要初回を別区間。root参照capture自体は全コピーでない。後続getterへの全構築転嫁、全row取得、毎package root複製を検出 | 実source-entry/root-key訪問と局所更新。未観測SQL/storage/resource指標を0扱いしない。wall-clock閾値なし |

## 配置・退役・観測

- `BmsLibraryPackageInstallServiceTests` の実library、collision、format shapes、prefix/manual recovery、hash予約、cleanup、resource-only、解放後通知casesをextend/replaceする。一意のFS shape/failure coverageは維持し、同契約を移したcallback-only代用だけ退役する。
- `InstalledOnlyResourceOverwriteValidationTests` は独立destination解決coverageとして維持し、U3統合の代用にはしない。
- `ChartInfoInstallFailureRetryTests` のBMS/BMSON inline・parse failureは原則維持。`ChartInfoMetadataTestSupport.InvokeInstallChartPackages` は推定先本番入口を使うため名称だけで消さない。
- `PendingPackageWorkflowOwnerTests` は実modelとの分担でterminal/outer cleanupを維持。queue/drop受付を変更した場合だけ `PackageInstallWorkflowOwnerTests` を追加検証。
- 固有temp FS/SQLite・明示options・既存file adapter/schedulerを使う。model return、receipt、owner Task、既存completion/idleで待つ。新DNP、固定sleep、reflection invocation、全アプリharness、test-only public API不要。
- 新規observerはcold実構築で背景訪問を捕捉する対照を含める。U1のhash/playlist/installed observerはDB全row/storage sequence/resource全体を保証しない。変更した処理の既存診断境界へ最小接続し、不足は明示する。private resource owner reflectionを新判定に採用しない。
- live path一時差替え、導入専用の重複facts/dispatchの退役は静的レビューで確認し、source文字列の恒久testを作らない。U1 red再実行・U3一律base-red不要。

Quickは上記4fixtureを標準scriptで実行。Functional/凍結review/commitはroot担当。300秒budget/180秒reporting targetをアプリ性能の閾値にしない。21万譜面wall-clockは未測定。

## root設計・実装への引継ぎ

本番4入口は既存 `ApplyInstalledChartStorageTargetsForFileMutation` からcatalog upsertへ到達する。確定destinationを持つ既存chart/target requestを使い、DB用detached projectionとdurable後のlive適用を分離する。新しい永続状態や汎用callback hostを作らない。共通semantic反映・公開へ接続し、packageごとの確定と先行receiptを維持する。

既存補償/通知/受付/保存範囲の意味変更、計測不足を全失効許容で埋める変更はrootへ戻す。任意stale version/pathless owner/同exact key状態は新本番保証にしない。

約15filesまたは3subsystemを超える場合はU3a（通常3入口・detached facts・共通commit/publication）→U3b（resource-only/cleanup-only・後続所有再評価・専用warm受入）へ直列分割する。同一巨大sourceの並列編集は禁止する。
