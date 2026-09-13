# U4a metadata producerの共通反映 — Test Contract Packet

状態: 独立test-contract-designerのPhase A→B後にroot承認（2026-09-13）。比較版はU3完了commit。U4b走査全置換・U5状態移動は対象外。

## 根拠・分類

[統合計画](library-mutation-unification-plan.md) section 3～5、[chart-info lifecycle](../spec/chart-info-lifecycle.md) Identity/Currentness/Durable/Failure、[data/indexes](../spec/data-and-indexes.md)、[変更境界](../spec/library-mutation-boundary.md)、[read pipeline](../spec/chart-file-read-pipeline.md) Manual Rescan/Encoding Fix、[並行性](../spec/workflow-concurrency-and-complexity.md) section 6、[性能](../spec/performance-and-scale.md)をauthorityとする。挙動維持refactorと境界coverage更新で恒久テストが必要。Phase Aを仕様だけで確定後、実装/fixtureをPhase Bで配置確認した。既存expected、現在の出力、翻訳、source列をoracleにしない。

## Contract

全項目はbehavior。receipt名・event列・helper配置・任意通知文言は固定しない。

| Contract ID | 本番入口・前提 | 必須結果・失敗 | 許容差分・検出する誤実装 | 確認方法 |
| --- | --- | --- | --- | --- |
| digest公開 | install→inline、startup→hydration/backfill→writer→共通反映。single writer/受理済対象 | DB→正本digest→依存索引→session index→解放後公開。通知読取りで新hash/解決先/sessionが揃う。同hash別owner維持、last ownerのみ除去、旧snapshot不変 | 必要先行関係以外は自由。二重適用によるowner数誤り、旧hash残留、session前公開を検出 | 実producer・通知/操作後readback。MD5変化とmissing SHA補完を分け、入力bytes/owner集合から期待値を決める |
| 限定保存 | background owner→build service→ApplyChartInfoStorageWrite、通常identity一致対象 | full backfill BMSはpath+正規化MD5一致行の指定9列だけ。基本/mode/judge/user列維持。同MD5各owner適用。BMSONをsong/digest mapへ混入しない。inline generated row保存維持 | SQL/chunk粒度自由。全row upsert、先頭ownerだけ更新、BMSON混入を検出 | 異なるsentinelを実SQLiteで確認。既存focused writer coverageと本番owner接続を分担 |
| failure/currentness | 実producerのDB/parse failure、partial cacheと実read identity | 当該transaction失敗で正本/索引/session/成功公開を進めない。先行package/chunk維持。current info優先、current failure skip、成功時failure削除、次回candidate維持 | 診断文言自由。導入成功までのrollbackは要求しない。未確定公開、失敗をcurrent化、先行成功喪失を検出 | 既存DB failure/実解析入力、DB/lookup/session/通知比較。新failure直積不要 |
| maintenance反映 | 選択resource workflow→RescanResourceHealthCharts→owner→narrow writer。encodingも既存入口 | 保存とhealth/warning確定、healthy化でwarning除去・TargetCount維持。未変更対象/旧snapshot保持。encodingは指定raw列のみ、digest/無関係保存列維持。manual rescanでchart-info生成しない | 関連範囲の計算方式自由。healthyをmembership除去、全row保存、hash不変全失効を検出 | 実rescan2回、missing→healthy等の実入力、DBsentinel/currentとold health/lookup |
| 局所仕事量/no-change | warm実consumer後、固定小Δinline/選択maintenance2回、背景16/128 | 通知とgetter2回まで全catalog/巨大root反復列挙/copy/構築をしない。cold別区間。同一maintenance rowはupsert無し、hash不変content/version保持 | 関連bucket/対数探索/明示一覧列挙許容。background候補全件調査は別区間。no-op前コピー、後続getter全構築、facts二重適用を検出 | instance-local実work observerと結果。query回数だけを証拠にしない |
| 受付/解放/終端 | 実install内inline、startup background、選択maintenance | inline再受付Busyなし。競合要求は既存無副作用拒否。scheduler受理/shutdown drain維持。DB/model lock内subscriber待ちなし | 既存signalを使う構成自由。新gate、未受理後日実行、読取りlock阻害を検出 | 既存受付/scheduler維持、変更bridgeのみ通知readback/terminalを補完 |

no-changeは全write/全通知ゼロを意味しない。missing digest補完/current info新owner適用には必要な保存・session・公開がある。無変更maintenance rowとhash依存索引を個別判定する。bugfix red/mutantは必須でない。

## 配置・退役

- `OwnedChartCollectionInlineDigestTests` と実install `ChartInfoInstallFailureRetryTests` をextend/replace。`CatalogChartInfoOwner_InlinePublicationOrdersDigestIndexesBeforeSessionIndexAndEvents` のenum列完全一致をbehaviorへ置換し、owner直接callbackだけを本番bridge合格にしない。
- `ChartInfoBackfillStorageTests` のnarrow列/reuse/duplicate/BMSON/transaction/later chunk failureを維持し、本番接続の不足だけ補完。
- `ChartInfoInlineHydrationTests` のbackground公開は実startup/captured optionsを使う。追加caseでprivate `InvokeDeferredChartInfoHydration` reflectionを使わず、既存 `AwaitChartInfoHydrationAsync` / `AwaitChartInfoBackfillAsync` を使用する。
- `BmsLibraryMaintenanceServiceTests.RescanResourceHealthCharts_UpdatesCurrentResourceHealthIndexByDelta` を16/128・2操作・旧snapshot・後続lookupへextend。encoding/無変更narrow保存は既存coverageを分担。
- `PlaylistWorkspaceDetailRefreshTests` / `CatalogMutationOwnerTests` / `StartupBackgroundTaskSchedulerOwnerTests` は原則維持し、該当consumer/writer/schedulerを変更した場合だけ追加検証。
- 固有FS/SQLite、既存helper/dispatcher/scheduler、同期model returnまたはPropertyChangedに結び付いたTask/terminalを使う。新DNP/固定sleep/local timeout/test-only public API/productionコピー不要。既存有限failure watchdogは維持。

任意に設定した旧hashは到達証拠にしない。MD5変化はdiscovery後のbytes変化、SHA補完はpartial digest cacheから作る。missing row/identity mismatch/receipt無しのfake状態からrecoveryを追加しない。

Quickはinline digest/hydration/backfill/install failure/maintenanceの5fixtureを標準scriptで実行し、変更対象に応じwriter/playlist/schedulerを追加。Functional/凍結review/commitはroot。300秒budget/180秒target、16/128の仕事量確認を21万譜面wall-clock保証へ読み替えない。

## root設計・引継ぎ

digestのprepare/publicationを同じ操作内の確定receiptと既存deferred effectでつなぎ、同じfactsから2回反映結果を組み立てない。DB/正本と依存索引をsession公開前に確定する必要なphaseは保持する。既存のnarrow callbackを整理できるが、新persistent ledger/世代state/広いcallback hostを追加しない。producerの未使用event/factoryは本番callerゼロを確認して退役する。

maintenanceも既存receiptから共通反映を組み立て、通常/estimatedの重複組立を除く。既存resource input mutation、保守narrow write、解放後効果、失敗分類は保持する。受付/scheduler/shutdown/保存範囲の変更が必要ならrootへ返す。U3後に経路が変わった場合は配置だけ再確認し、oracleを変更しない。
