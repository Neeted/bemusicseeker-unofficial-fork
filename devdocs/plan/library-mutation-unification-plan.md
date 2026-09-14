# ライブラリ変更要求の統合と操作全体の性能改善計画

状態: U1～U5c1完了。最終単位U5c2のレビューP2修正・Functional再検証完了、fresh review中（2026-09-14）。利用者はU1～U5の実装と単位ごとのcommitを承認済み。

> 2026-09-14追記: 本計画で維持した「packageごとの公開」および一部のper-item durable receiptは、その後採用した `1 user operation = 1 mutation session / N changes` 契約で置き換える。実装移行の正本は [operation-scoped mutation session 実装計画](library-mutation-session-batching-plan.md) とする。本計画はU1～U5の履歴記録として残す。

## 目的と結果

実際の変更操作からFS・DB・正本・索引・公開まで、確定した同じ変更事実を使う。既存file ownerを `LibraryMutationOwner` へ再編し、rootの変更結果組立て・共通反映・公開順序を移管した。索引stateと読み書きは既存 `CatalogOwnedCollectionOwner` に揃えた。新しいbus、queue、global gate、永続変更台帳は追加していない。

調査基準は `6d35c789`、計画記録は `007b3216`。[利用者提供ログの再評価](../acceptance/duplicate-merge-performance-2026-09-13.md)では、2回のmergeとも全索引構築がモデル処理時間の87～92%を占めた。前計画R5bが確認できていなかった実mergeの旧facts伝達と連続操作を、本計画U1以降の受入に含めた。

現行の操作→要求→writer→索引→公開、残す専門callbackの理由は [変更境界](../spec/library-mutation-boundary.md#操作から確定公開までの対応) を正本とする。索引の所有・仕事量・snapshot契約は [データと索引](../spec/data-and-indexes.md)、exact identityは [path identity](../spec/path-identity.md)、確定・失敗は [FS/DB整合](../spec/file-db-consistency.md)、受付例外は [並行性](../spec/workflow-concurrency-and-complexity.md) へ反映した。

## 実施単位と検証

各単位を一人のworkerで直列実装し、root統合検証・凍結した独立レビュー後にcommitした。Functional欄は最終成功runのテスト実行時間であり、preflight/buildを含まない。全runとも11件skip、180秒reporting target超過・300秒上限内。大規模実環境の操作時間を示す数値ではない。

| 単位 | 実施内容・旧経路の退役 | commit | Functional成功件数・秒 |
| --- | --- | --- | --- |
| U1 削除・merge | kind/exact/旧hashを共通除去解決で捕捉。PathCleanupから既知hashを捨てる経路とcaller別全失効を解消。sourceなしはlookup前終了 | `e6b58ffc` | 4,808 / 257.5 |
| U2 配置変更 | move/rename/extension/repairのfacts反映を統合。権限なしauto rename専用callbackと重複cache命令を削除。live capability・batch LR2・通常拡張子登録解除を維持 | `6c25bbc1` | 4,809 / 256.5 |
| U3 全導入入口 | 自動・推定先・強制・resource-onlyの確定targetと共通完了へ統合。detached DB projectionをdurable後にliveへ適用し、一時path差替えscopeを削除 | `e54edd94` | 4,817 / 261.1 |
| U4a metadata | digestの二重組立てを単一結果と解放後Actionへ変更。通常/estimated maintenanceを同じ反映へ接続。旧prepare/dispatch/potential event退役 | `9d4ae7ee` | 4,820 / 239.5 |
| U4b scan | typed replacementとresidual facts。汎用adapter・失効3field退役。residual再接続を既存kind別exact lookupへ限定 | `0dd2c478` | 4,818 / 249.4 |
| U5a installed索引 | primary/fullのstate・lock・generation・observer・read/writeをCatalogOwnedCollectionOwnerへ移管。既存currentness同期は維持 | `bdb4df35` | 4,818 / 239.0 |
| U5b playlist索引 | resolveのstate・lock・version・observer・read/writeを同ownerへ移管。prewarmのTask共有・schedulerを維持 | `bbda9872` | 4,818 / 244.6 |
| U5c1 factsと旧入口 | immutable factsと操作reportを分離。LibraryMutationDelta・任意root apply・本番非使用の追加枝/helper退役。mergeの種類別通知を維持 | `ba40fb44` | 4,789 / 250.9 |
| U5c2 共通変更管理 | LibraryMutationOwnerへ組立て・semantic反映・必須完了・dispatchを移管。旧file owner、root同義実装、installed lookup等の迂回callbackを削除 | レビュー中 | 4,790 / 253.7 |

共有test setupの通知待ち修正は `25406ec4` に独立commitした。setterを既存TestUiDispatcherHostで実行し、ThreadPool workerが自分のlocal queueを通知待ちで塞がないようにした。本番scheduler、timeout、worker数、DNPは変更していない。

### U5c2の最終受入

- ownerの新partialが共通結果/storage/installed receipt、旧新facts組立て、semantic index、dispatch、file apply、installed required completion、scan/digest/maintenance反映を所有する。操作内mutable bookkeepingはprivateとし、外へは既存receiptか確定した公開Actionを返す。
- BMSLibrary本体やbroad hostを保持しない。catalog/storage/owned/resource/package/playlist/LR2は明示依存。rootの専門cache命令、表示通知、LR2専門capture、full resource target、folder命名、merge再受付とログ接続は理由を仕様へ列挙した。installed lookupへのroot forwarding 3種と単純subset target callbackは削除した。
- 旧 `LibraryFileOperationOwner` / `LibraryMutationDelta` / `ApplyLibraryMutationDelta` / 任意 `ApplyInstalledChartStorageTargets` / 権限なしauto rename旧経路はproduction/testとも参照0件。typed scan residual、chart-info narrow write、pending/package専用処理等、意味の異なる現行契約は維持する。
- 初回移管の関連Quick667件成功（`tests-quick-20260914-060014`）。callback整理後99件成功（`tests-quick-20260914-063000`）、実導入readback補完9件成功（`tests-quick-20260914-063220`）。primary snapshot、実FS/SQLite、receiptを確認。初回レビュー前Functional `tests-functional-20260914-065053` は4,789成功・11skip、264.2秒。P2修正後の最終Functional `tests-functional-20260914-071916` は4,790成功・11skip、253.7秒。format/analyzer/build成功。fresh reviewへ渡す。

## 受入で維持した契約

- 画面の確認済み要求は既存lease/capabilityで受理し、受付後に現在対象へ解決する。FS/DBの限定補償、durable後の必須反映、部分成功prefix、cancel、cleanupと通知失敗の分類を維持する。
- merge後maintenanceの明示解放・再取得、packageごとの公開、auto rename batch終端LR2、追加ZIP予約、pending自動推定、設定利用、通信待ち中の既存許可操作を維持する。
- 背景16/128で固定差分の独立2操作と後続getterを確認する。実source/root work observerとcold正対照を使い、全件再構築を次のgetterへ押し出さない。旧immutable snapshot、候補順、last owner、種類別exact identityも確認する。
- 通知中のhash/installed/playlist/resource/session読取りとlease解放を確認する。内部applyが成功しただけで実入口の受入としない。
- 手作り索引delta、任意rootapply、reflectionを新しい本番境界テストの代用にしない。旧test数の維持自体は目的にせず、実際に到達する契約を保つ。

### 到達性について閉じた判断

承認済み [U5c Test Contract Packet](library-mutation-u5c-test-contract.md) を正本とする。

- current-owned非空導入先overlayの任意更新と重複exact target注入によるrecoveryは、本番生成経路未確認。旧test専用入口とともに代替不要で退役。U4bのexact cleanup内部契約と実pending/rename/delete参照更新、実commit failureは保持した。新setter/producer/recoveryは設けない。
- U5c1で留保したprimary-only/hash-only/full-cold **target入口状態**は、U5c2の独立到達性確認で代替不要と判断。4導入入口はいずれも所有判定のfull lookupを取得してから共通target反映へ到達する。直接managerへ任意状態を注入する新testは設けない。
- 実導入後primaryの新旧readbackは有用だが、cold targetの検証とは呼ばない。将来の不要構築削減を妨げる「必ずfull構築」の追加assertionは統合時に撤回した。exact replacement一般・optional ref非構築の専門契約まで退役したわけではない。

### 調査・レビュー時の重要な修正

- U3 resource-only testはsource不足→destination充足の正対照と未充足警告維持を揃えた。production/runner変更なしのtest補完は関連Quickで確認し、既存Functionalに追加した。
- U5b初回Functional `tests-functional-20260913-233425` はportable-settings単独hostのFile.Replaceで停止。他host開始前であり同時実行原因と決めつけず、file scopeと関連Quick2件を確認。コード変更なしの再実行 `233856` は成功。単発I/O原因は未特定で、retry/fallback実装は追加していない。
- U5c1の通知待ち問題は旧HEADでも再現。同じ521件・Workers12/ClassLevelで共有setup修正後成功（`tests-quick-20260914-040757`、58.9929秒）。workerのresolver toolが利用不可のためworker/rootで同じ診断を代替した。
- U5c1レビューP2はmergeのpath-only移動に新たな全row同期が発生する通知policyの取り落とし。mergeはSuppressed、auto renameは捕捉済みpolicyを明示し、BMS除去＋BMSON移動でBMSのみ通知する実操作を追加。関連QuickとFunctional `tests-functional-20260914-043732`、fresh reviewで解消確認した。
- U5c2初回preflight `tests-functional-20260914-063810` はXML summary開始タグの移管漏れを検出。機械修正後の `064039` は事前検査・build成功、テスト1件が旧private公開methodのreflection参照で失敗。実ownerの同じ公開処理へhelperを移し、UI同期待機を禁止する既存判定を保持。関連Quick9件が19.1秒で成功し、Functionalを再実行した。詳細と限定reflectionの理由はU5c packetに記録した。
- U5c2独立レビューP2: resource healthのcold読取りでinitialized-min/storage reader scopeが移管時に欠落。Missing一覧の閲覧と導入writerが重なる経路を確認し、既存の同じreader scopeを戻す。新gate/retryは不要。他のfacts・prefix・LR2・種類別通知・差分反映には修正必須指摘なし。実ChartFilesNeedResourceFixの回帰testを追加し、修正前 `tests-quick-20260914-071523` でwriter保持中の早期完了を検出。scope復元後 `071633` の対象1件と `071743` の保守fixture86件成功。Functional再実行とfresh reviewへ進む。

## 残課題と測定範囲

- 約21万譜面・約3万フォルダ・約800万resource reverse keyの実環境wall-clock、SQL/resource全仕事量は未測定。本計画はコードと再現可能な実操作テストで明らかな余分な全件処理を除く範囲であり、短縮率や本番計測完了を主張しない。
- parent folderのraw prefix照合、duplicate graph再解析、consumerによる明示全件列挙、cold初回構築、明示全走査は残る。新incremental graph・永続cacheは追加しない。
- [前計画](BeMusicSeeker-library-mutation-performance.md) のR7等、今回のU1～U5に含まれない課題は別途扱う。

独立テスト設計の記録: [U1](library-mutation-u1-test-contract.md)、[U2](library-mutation-u2-test-contract.md)、[U3](library-mutation-u3-test-contract.md)、[U4a](library-mutation-u4a-test-contract.md)、[U4b](library-mutation-u4b-test-contract.md)、[U5a](library-mutation-u5a-test-contract.md)、[U5c](library-mutation-u5c-test-contract.md)。U5bは独立確認で既存coverage再利用・新test不要と判断した。
