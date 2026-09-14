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

## U5c2 target入口の到達性補足（2026-09-14、独立確認後root承認）

U5c1で留保した「primary-only/hash-only/full-coldのままtarget反映へ入る」旧テストの置換義務を、この状態の組合せに限って退役する。自動・強制・推定先・resource-onlyの本番callerは、所有判定のfull lookupを実構築・取得してから `LibraryMutationOwner.ApplyInstalledChartStorageTargetsForFileMutation` へ到達する。空対象やskipはcold targetの証拠にならず、必須完了失敗後に索引をcoldへ戻してsuffix targetを続行する正規経路もない。任意状態のmanager直接構築は同じ本番条件の置換にならないため、新fixture/入口は設けない。

Phase Aで既存packetの仕様・許容差分を確認後、Phase BでPackageInstallの4caller→BMSLibraryのversioned getter→CatalogOwnedCollectionOwner.Installedの実full構築と、failure後のsuffix停止を追跡した。実導入後primaryを読む追加assertionはreadback/旧snapshotの検証であり、cold targetやhash-only旧facts捕捉の検証とは呼ばない。将来の前処理改善を妨げる「必ずfullを構築する」というassertionも新設しない。

exact key置換の一般契約、optional ref indexを不要構築しない専門owner契約、既存実操作のwarm16/128・独立2操作・全構築observer・cold正対照・DB/FS・旧snapshotは維持する。`CatalogMutationOwnerTests.ApplyInstalledTargetUpsert_PreservesEveryExactKey` 等の専門writer coverageと、実導入の別path追加coverageは区別し、同等のexact replacementテストへ移行できたとは報告しない。新たなruntime挙動や回復保証は追加しない。

## U5c2 UI通知helperの移管（判定内容の変更なし）

Functionalで `RegularChartNormalLibraryRefreshTests.AttachedNormalLibraryRefreshSource_DoesNotWaitForUiWhileCatalogWriterHeld` の旧private publisher reflectionが移管前のBMSLibraryを参照していたため失敗した。既存helperを実LibraryMutationOwnerの同じexternal replacement公開処理へ機械的に接続する。writer保持中にもproducerがUIを同期待機しないこと、UI側reader到達、解放後の適用という判定・待機条件は変更しない。

この既存UI fixtureはconcrete BMSLibraryの通知sourceを購読し、意図的に別のwriterを保持して公開だけを起動する。全mutationを代用すると公開前のwriter待ちを試す別契約になるため、fixture内の限定reflectionを旧methodから実owner fieldへ移し、既存internal公開methodを型付きで呼ぶ例外をroot承認した。新root APIや通知payloadの写経は追加しない。型/field名の存在自体はassertせず、既存productionのtyped通知sourceを差し込めるようになった時点でhelperを退役する。これを理由に今その抽象化を新設しない。

## U5c2 resource health cold読取り整合性（独立Phase A/B後root承認）

分類はレビューP2のbugfix。比較対象はU5c2のreader欠落がある修正前worktreeであり、欠落前のHEAD単体ではない。根拠はrootの到達性判断、既存「索引と捕捉view」「公開と操作終端」、resource healthのvalid snapshot、並行性section 6。Phase Aの判定を固定後、Phase Bで既存warm/delta coverageの不足と配置を確認した。

| 項目 | 入口・前提 | 必須結果・許容差分・誤実装 | 配置・確認 |
| --- | --- | --- | --- |
| 更新中のcold保守一覧 | Maintenance tree→一覧更新→ChartFilesNeedResourceFix→実getter。catalog初期化済み、未ignore不足resourceあり、所持collectionは通常の本番readで構築済み、resource indexだけcold。導入writerが先行snapshot取得後に入れる | writer保持中のcold readは早期完了せず、解放後に既知警告譜面のkind/exact pathを返す。空・欠落を成功扱いしない。内部配置・lock型・回数・時間値・文言・順序は自由。単なるスケジュール遅延や待つだけで対象を落とす実装を区別する | BmsLibraryMaintenanceServiceTestsへ1件extend。固有DB、既存警告builder、実property、既存writer/worker helper。reader到達または早期完了を観測し、writer解放後にtaskとmembershipを確認。修正前red→修正後Quick→保守fixture Quick |

修正はcold branchを既存EnterFolderMoveReadScope相当（initialized-min/storage reader）で囲むだけ。warm read、新gate/retry、任意input mutation注入は対象外。collectionまでcoldにして別のstorage snapshot待ちで欠落を隠さない。実install全体の競合再現をこの小さい境界テストの結果として報告しない。

writer guardは同じthreadで取得・finally解放し、その後worker完了/faultを回収してからDBを片付ける。既存WaitingReadCountを同期補助として使用可。watchdogは近傍の10秒以内、timeoutを正常待機の証拠にしない。既存RegularChartListOwnerTestSupport.GetCatalogStorageRowsWriteGateの限定reflectionは競合区間制御のため再利用を許可し、新reflection/private名assertionは追加しない。型付き同期取得へ置換できた時点で退役する。新fixture/lane/DNPやtargeted mutantは不要。
