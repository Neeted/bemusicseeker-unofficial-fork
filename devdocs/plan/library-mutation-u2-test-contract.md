# U2 配置変更の共通反映 — Test Contract Packet

状態: 独立test-contract-designerのPhase A→Bを経てrootが承認（2026-09-13）。U2実装はU1完了commit後。baseのcommitはU1完了時に記録する。

## Authority・分類

挙動維持のrefactorと既存assertion semanticsの置換であり、実入口・連続操作・warm索引の不足を補う。[統合計画](library-mutation-unification-plan.md) section 3～5、[path identity](../spec/path-identity.md)、[data/indexes](../spec/data-and-indexes.md)、[変更境界](../spec/library-mutation-boundary.md) のdurable・repair、[FSDB](../spec/file-db-consistency.md) section 3～5、[並行性](../spec/workflow-concurrency-and-complexity.md) section 5～6、[性能](../spec/performance-and-scale.md)を根拠とする。現行expected、翻訳、source配置はoracleにしない。

rootの設計判断: 各配置変更のcallerが指定する索引失効booleanを除き、storage/path・folder・overlay・installed package pathの実変更から共通applyが判断する。auto renameは外側live capabilityをcoordinatorまで渡し、既存capability付きcommon applyへ接続する。capabilityを受けない `WithoutLr2NormalFolderSync` callbackを削除する。LR2/normal refreshのbatch集約、itemごとのFS/DB確定、先行成功、durable後失敗と解放後通知を維持する。新しいgate/scheduler/永続stateを追加しない。

## Contract

全項目はbehavior。内部型・method配置・observer名は固定しない。

| Contract ID | 本番入口・前提 | 必須結果 | 許容差分・識別する誤実装 | 確認方法 |
| --- | --- | --- | --- | --- |
| 配置事実 | 選択移動/通常一覧編集→`MoveLibraryRootFolderWithReceipt` / `RenameChartFolderWithReceipt`→folder coordinator→capability付きapply。収束済catalog、確認済対象、process-exclusive DB | 成功対象の旧exact key/実destinationをDB・正本・installed配置・playlist・package/overlayへ反映。user列・未対象行・同hash別配置・resource参照・LR2結果を保持 | storage旧view内のlive owner.pathは変わり得るがimmutable lookup旧pathは固定。旧key残留、対象外変更、overlay旧配置、user列再生成を検出 | BMS/BMSON、nestedとprefix類似対象外、同hash別配置を少数caseへ分配。実FS・SQLite readback・公開結果 |
| warm連続操作 | 実配置変更/auto/repair入口、測定前に必要なwarmup完了。背景16/128、固定Δの2操作、getter各2回/通知/prewarm | primary/full/hash/playlist結果が入力に一致。path-onlyではhash集合とcontent Version保持。旧immutable snapshot不変。全catalog訪問/root materialize/full rebuildを後続処理へ転嫁しない | cold必要初回、影響bucket、木探索対数差、既存親prefix/重複graph/明示一覧列挙は許容。getter全構築、全snapshot copy、path-onlyでhash再構築を検出 | 実source訪問/materialize/builderと結果を同時観測。query回数やelapsed閾値だけでは判定しない |
| auto batch | auto workflow選択/全件→外側lease→coordinator→live capability付きapply→batch finalization | 複数成功を反映しLR2/normalrefreshをbatch集約。成功prefix・failed/未実行itemを区別。durable後必須反映失敗を成功にせず通知は解放後 | 内部callback形・通常progress間引きは自由。内側新規受付による自己Busy、二重同期、prefix消失、失敗後success refreshを検出 | 実複数folder、LR2 readback、集約refresh、既存SQLite failure/receipt、workflow idle |
| repair | pending workflow→`FixInstallationDirectoryCharts`→file owner/executor→catalog→既存予約内maintenance | 選択譜面だけを実collision destinationへ移動。兄弟/resource/親を削除しない。owner/DB/参照/overlay/user列一致。移動先resourceで再検査し、後段保守/承認済み重複削除失敗でも先行durable移動を保持 | 新rollback/retryを追加しない。内容不変ならhash仕事省略可。自己Busy、旧警告、basenameでdestination再計算、receipt破棄を検出 | 既存BMS/BMSON repair/collision/user列/保守失敗を維持、warm readback不足のみextend |
| 通常拡張子修正 | 選択workflow→`RenameBMSFilesExtensions(..., unregister: true)`。既存の成功対象だけ登録解除 | FSの拡張子変更後に旧catalog membershipを解除し、同hash残存owner/last ownerに応じてhash/lookup/playlistを反映。失敗対象の登録は維持 | 通常moveの「hash集合不変」は適用しない。所持に残す、失敗対象も除去、同hash全owner除去を検出 | 実UIと同じモデル入口のFS/DB/索引結果。service delta-only bool assertの代用を廃止 |
| 受付・公開・pending境界 | 各workflow→既存モデル受付。pending-only raw受付、catalog依存は収束確認 | Busy/未収束はFS/catalog/成功公開無副作用。内部処理は同じlive ownership。通知で確定状態を読め、任意subscriber失敗でdurable結果を再分類/再実行しない。pending-onlyの範囲と許可維持 | 文言と任意通知の非契約順は自由。pending一律拒否、lease保持中subscriber再入、通知失敗のDB失敗化を検出 | 既存workflow/拒否/再入coverageを維持、変更branchの不足だけ追加 |

U2に独立した承認済みbug再現はないため、挙動維持testがbaseでgreenでもよい。U1 mergeのredを重複実施しない。概念上の誤実装をすべてmutant実行する要求はない。observerが実builder/列挙を捕捉しない場合は観測不足をrootへ返し、caller boolや成功数で代用しない。

## 配置・退役・同期

| 対象 | 配置 | 退役 |
| --- | --- | --- |
| 配置事実/warm | `BmsLibraryFolderRenameRefreshTests.RenameIngress_CapturesOnlyLocalBmsRangeFacts` の16/128を2操作・4索引・後続readbackへextend。move/repairは近傍の実caseに不足を追加 | 特定queryの正値を要求するassertは結果＋仕事量上限に変更可。全体query方式を固定しない |
| service projection | `BmsLibraryLibraryFileOperationsServiceTests` のBuildFolderMoveDelta各caseの失効bool部分を実operation結果へreplace。独立した入力分類の意味は維持 | `InvalidateInstalledDirectoryIndex` / `InvalidateParentFolderCache` / `ClearDuplicatedCache` のtrue固定。case全体は一意coverageを移してから削除 |
| auto | `BmsLibraryFolderRenameRefreshTests` のBatchesMultipleFolderMutationsIntoOneRefresh、BatchesSuccessfulMovesWhenOnePlanFails、Lr2FinalizationFailure、PublicPublicationFailureを維持しwarm不足だけextend | 外向きWithoutLr2NormalFolderSync callback/専用forwarding。batch集約は残す |
| repair | 同fixtureのRechecksResourcesUnderExistingReservation、UsesCollisionPathForOwnerAndDb、BmsChartPreservesExistingLr2SongUserColumns、BmsonDuplicateHonorsApprovalAndKeepsSiblingを維持・不足のみextend | failure直積やoverlay-only代替caseを重複新設しない |
| 受付/pending | RegularChartFolderRenameTests、SelectedChartMutationWorkflowOwnerTests、FolderAutoRenameWorkflowOwnerTests、BmsLibraryPendingLegacyMutationTestsを原則維持 | 新DNP、固定sleep、local dispatcher pump、新アプリharnessなし |

GUID付き一時FS/SQLite、明示options、既存schedulerを使う。model同期return、event/TCS、workflow `CompletionPublished` / `WaitForIdleAsync` / `StopAsync`、既存prewarm Taskで待つ。WPFは `TestUiDispatcherHost` と既存AwaitTaskOnDispatcher。正常完了をtimeoutで推定しない。Functionalは共通300秒deadline、180秒超成功はelapsed報告。

Quick候補は上記6fixture名の `FullyQualifiedName~` をOR結合し、`scripts/verify-refactor.ps1 -Mode Quick -TestFilter` から実行する。反復は変更caseへ絞る。Functional/最終review/commitはrootが担当する。

## root決定と引継ぎ

- 独立designerが通常拡張子修正のmembershipについてNEEDS_ROOT_DECISIONを返した。rootは**既存UIの登録解除を維持**するAを採用した。利用者が求めた変更管理の整理は登録維持への製品仕様変更を含まない。登録維持モードは本番callerが確認できた場合だけ別caseとする。計画の単なるpath更新という説明を訂正する。
- TestBmsLibraryの直接readiness設定は本番到達性の根拠にしない。通常の有効なcatalog/実FSのfixture準備として使えるが、未到達alias/fake-only状態から回復契約を作らない。必要なら既存captured scanner/ReloadFileDiffを利用する。
- U1の実装やobserverを再設計しない。U1完了handoffから確定した観測口と退役caseだけを確認する。
- rootのproduction割当はBMSLibrary.cs、LibraryFileOperationOwner、PackageInstall、BmsLibraryLibraryFileOperationsService、AutoRenameBatchCoordinator、必要な既存配置coordinator。同じsource/fixtureを並列編集しない。所有外変更や意味変更が必要ならrootへ戻す。
