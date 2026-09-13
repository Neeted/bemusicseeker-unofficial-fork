# U5a 導入済み索引の状態所有移行 — Test Contract Packet

状態: 独立test-contract-designerのPhase A→B後にroot承認（2026-09-13）。実装比較版はU4b完了commit。

## 根拠・必要性

[統合計画](library-mutation-unification-plan.md) section 3～5/U5具体的分割、[data/indexes](../spec/data-and-indexes.md) installed snapshot/pending currentness、[性能](../spec/performance-and-scale.md)、[並行性](../spec/workflow-concurrency-and-complexity.md)をauthorityとする。

内部構成refactor。新しい恒久テストを必須とする不足は確認されていない。U1～U4の本番入口coverageを再利用し、必要なsignature/helper更新はassertion semanticsを変えないmechanical変更とする。新characterization/red/mutant/source不在テスト不要。Phase Aの期待値を仕様だけで固定してから既存caseを配置確認した。

## 維持するContract

| Contract ID | 実入口・結果 | 許容差分・識別対象 | 確認 |
| --- | --- | --- | --- |
| installed-membership | merge/削除/配置/導入/digest/scan→確定facts→lookup。MD5/SHA、同hash複数owner、directory所属数、last owner、候補順、primary MD5を維持。旧snapshot/excluding不変 | state/private名/helper構成自由。旧hash除去漏れ、owner数縮小、可変bucket共有を検出 | 実FSDB→lookup既存caseとcount/excluding補助case |
| cold-warm-cost | primary-onlyはfull/refを作らず、sourceなしmergeはlookup前終了。cold/明示replacementの必要全構築は可。warm16/128独立2操作とgetterで全構築/root copyへ戻さない | 内部方式自由。owner毎回初期化、facade二重cache、新stateへのobserver接続漏れを検出 | U1～U3の実source/root観測とcold正対照をそのまま再利用 |
| snapshot-generation | pending再推定のsnapshot/generation公開・composite照合・entry適用を同じ狭い同期境界で行う。stamp一致時だけ再利用、不一致時同段階最大1回再評価、再変化skip、全terminalでSEARCHING解除 | 世代の具体整数/型は固定しない。snapshotとgeneration別時点、旧facade世代照合、gate分断を検出 | 既存実再推定/queue lifecycle＋同期経路の静的照合。installed単独generationの識別まで確認済みとはしない |
| publication-failure | 既存受付→durable→必須反映→解放後通知。commit前の成功factsなし、必須失敗を成功化しない。Busy/取消/inline/shutdown維持 | 必要先行関係以外の順序固定なし。通知先行、例外握潰し、再受付、lock保持中の別owner待ちを検出 | 既存実操作/digest failure/通知/queueと変更lock経路の静的確認 |

全項目はbehavior。内部所有の配置はreviewで確認し、source文字列、exact翻訳、broad snapshot/private reflectionを追加しない。

## 既存配置・検証

- 実操作: `BmsLibraryDuplicateServiceTests`、`OwnedChartCollectionLibraryMutationTests`、`BmsLibraryFolderRenameRefreshTests`、`BmsLibraryPackageInstallServiceTests` のU1～3 warm/cold/no-op/2操作/old snapshot各case。
- digest: U4a後の `OwnedChartCollectionInlineDigestTests` の実producer/失敗/公開case。意味を弱めず接続先だけ追従。
- 補助: `BmsLibraryInstallEstimationServiceTests` のdigest bucket/count/last owner/affected bucket/excluding、`OwnedChartCollectionInstalledOverlayTests` のprimary-only。
- currentness: `BmsLibraryPendingPackageRegroupTests.PendingInstallEstimate_InstalledCollectionChangesDuringAttempt_RebuildsPartitionAndReleasesSearchingState` と `PendingInstallEstimateQueueProcessorTests`。前者はcollection変化も伴うためgeneration単独比較漏れの独立証明ではない。既存gateの維持を静的にも確認する。
- scan: U4bの実scan受入と `BMSFilesReplacement_InvalidatesOwnedCollectionVersionAndRebuildsOnNextView` を再利用。
- 固有FS/SQLite、既存observer/scheduler、同期return/Task/terminalを維持。新DNP、固定sleep、正常完了timeout不要。Quickは上記fixture、Functionalと凍結reviewはroot（300秒budget/180秒target）。大規模速度は未測定。

## root設計・退役

primary/full installedのstate/initialized/generation/専用lock/observer/read/build/apply/invalidateを既存CatalogOwnedCollectionOwnerへ一緒に移す。同classのpartial fileは可、新owner/二重state/cache/ledgerなし。BMSLibraryは公開facadeと既存pending currentness境界を保ち、index state/lockを外部公開しない。storage/canonicalは既存ownerへの明示依存から取得し、全copy callbackを導入しない。

playlist所有移動はU5b。rootの `InvokeApplyLibraryMutationDelta` / `InvokeApplyInstalledChartStorageTargets` 等の利用者全移行はU5cで行い、U5aを理由に無関係なtest整理をしない。generation/gate lifetime/受付の意味変更が必要なら再分類してrootへ返し、旧oracleを変更しない。
