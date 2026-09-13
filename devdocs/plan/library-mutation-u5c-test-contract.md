# U5c 変更factsと実操作境界の移行 — Test Contract Packet

状態: 独立Phase A/B後にroot承認。比較版はU5b完了commit。内部refactorと旧入口退役、既存更新・置換・重複削除が中心。U5c2の機械的移動・改名だけには追加test不要。

## 根拠と判定基準

[統合計画](library-mutation-unification-plan.md)「U5cの境界判断」、[変更境界](../spec/library-mutation-boundary.md)、[索引](../spec/data-and-indexes.md)、[FSDB](../spec/file-db-consistency.md) §2～5、[並行性](../spec/workflow-concurrency-and-complexity.md) §5～6、[性能](../spec/performance-and-scale.md) §2～4をauthorityとする。Phase Aは仕様だけで固定後、Phase Bでproduction/helper配置を確認した。調査HEADは0dd2c478、U5a未確定diffを期待値に使わない。

前提は既存admission/path readiness、process-exclusive DB/single writer、captured scan surface。全項目behaviorで、型名・private配置・source不在・翻訳文言・広いsnapshotはoracleにしない。

| Contract ID | 本番経路・必須結果 | 許容差分と誤実装の識別 | 確認 |
| --- | --- | --- | --- |
| 旧factsによる対象限定 | 実remove/rename/merge/extension→file owner→catalog→collection/package/索引。旧kind/exact/MD5/SHAと確定新pathで同じ対象集合へ反映。残存hash owner/移動先保護、BMS/BMSON区別 | 内部型・組立て自由。live pathから旧key推測、summary件数から対象決定、同hash別owner巻込みを検出 | 実操作U1～U3と専門owner coverageを再利用、旧helperは同じ実対象へ置換 |
| 導入・走査の確定反映 | 自動/推定/force→確定target→writer/common、ReloadFileDiff→replacement/resource。durableだけconsumerへ、BMSONをsongへ混入しない。empty residualでもfull replacement維持 | 旧Delta任意追加API不要。報告nochangeを理由に必要保存/反映省略、本番だけ旧経路を検出 | U3/U4実操作、旧追加caseは実導入または同じproduction target ownerへ |
| 索引と捕捉view | 未利用索引は不要構築なし。warm16/128固定Δ独立2操作/gettersで全再構築なし。旧hash/installed/playlist保持。storage viewはmembership/order/version固定、live要素path変更は許容。hash集合不変content version維持 | cold/full replacement/既存BMSON初回正規化許容。2回目/getterへ全構築押出し、lastowner誤判定を検出 | 既存実work observer/cold正対照/old snapshot、時間閾値を新設しない |
| 公開と操作終端 | 実入口→lease→durable/必須反映→操作固有LR2/batch→解放公開。通知内current、Busy無副作用、subscriber failureでdurable結果変更なし | 文言/内部callback自由、通知数を操作間で一律化しない。item毎重複公開/lease内公開/LR2前完了を検出 | U1～4通知readback/lease/terminalとLR2 coverage |
| 成功prefixと報告 | 実FSDB failure→receipt/terminal。確定prefix保持、未実行混入なし、commit失敗で成功receiptなし。既存限定補償とdurable後非rollback維持。件数/failure/timingは報告用 | 時間値/内部報告型自由。summary失敗でprefix破棄、cleanup failureをfresh failureへ変える誤りを検出 | 既存実remove/install/extension/merge failureとprefix coverage |

非bugfixなのでred/mutant一律不要。旧helperがcompileしないことはred証拠ではない。置換で識別力が失われる場合だけrootへ返す。新private reflection/source-absence/characterization不要。

## 配置と退役

- OwnedChartCollectionLibraryMutationTests、DuplicateService、FolderRenameRefresh、CatalogRelocation、StateApplier: 実削除/配置へreplace、同invariant重複は統合削除可。専門owner型変更はmechanical。
- InstalledOverlay、PackageInstallService、OwnedRefresh、Lr2SongDbSync: 任意追加を既存target/実install/reloadへreplace、不足observableだけextend。
- PlaylistSummaryResolveIndex/MutationAndWarm/CountAndPresentation、InstalledOverlay: representative昇格/exact置換/move/primary-only/hash-onlyを同じproduction target/remove条件で維持。
- U3 install/U4 metadata/scanとPackageLifecycle/LR2: 公開と終端を再利用。standalone Busy例外型・翻訳文言は固定しない。
- 実failure: `RemoveLibraryCharts_CatalogFailureReturnsAfterConfirmedFilesystemDeletion`、`InstallPendingPackagesToEstimatedDestinations_AppliesDurablePrefixBeforeManualRecoveryStopsSuffix`、extensionのdurable failure/先行効果、LR2 commit failureを保持。
- OwnedChartCollectionTestSupport、PlaylistSummaryAggregationTestSupport、FolderRename/LR2内の `InvokeApplyLibraryMutationDelta` / `InvokeApplyInstalledChartStorageTargets` を退役。同義の任意root APIで名前だけ置換しない。移動後production seamを使う場合は実callerと同target/capability/receipt条件を示す。
- 固有FS/SQLite、既存factory/dispatcher/instance observer、captured scanner、既存lane使用。LR2の既存Settings復元/class DNP維持、新DNP/固定sleep/local timeout無し。同期return/receipt、既存通知Taskを終端とする。Functional300秒/180秒報告、root実行。

## rootが閉じた到達性判断

1. InstalledOverlayの `InvalidatesOwnedCollectionOnFailure` / `ForceInvalidatesResourceHealthIndexOnFailureDuringSuppression` は重複exact targetを旧helperへ注入する下流recovery case。本番producer経路未確認。**代替不要で削除承認**し、実install commit failure coverageは保持する。
2. 任意current-owned非空overlay更新の `ApplyInstallDestinationChange` / `OverlayOnlyClearsMetadataCache...` / `SearchEstimatedInstallationDirectory_OverlayMutationRefreshesMetadataProfileThroughRealRoute` はmutation triggerの本番生成が未確認。**旧helper退役に伴う削除を承認**。検索自体が実入口でもtriggerの到達証明にはしない。
3. FolderRenameのoverlay seed、Refreshのmixed notificationも同制限。実操作の残るinvariantを弱めず、seed依存assertionだけ分離削除/既存focused cleanupへ統合する。U4b typed residualのexact cleanup内部契約と実pending/rename/delete参照更新は維持する。
4. 上記はユーザーの複雑性最小化、workflowの到達性条件、計画の旧入口退役と非空生成未確認の既存制限に基づく。新setter/producer/recovery保証を追加せず、実装の出力から期待値を変更しない。

U5c1は一人で全producer/consumer/helperを移行、U5c2は後で実owner移動・改名。専門ownerの全caseを新規test対象にしない。fields/signature mechanicsは自由だが、成功対象/exact/種類別snapshot/公開/失敗分類は維持する。
