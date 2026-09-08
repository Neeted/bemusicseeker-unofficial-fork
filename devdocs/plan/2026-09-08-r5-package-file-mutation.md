# R5: パッケージ移動と導入後削除の統合

## Goal / Context

通常導入の「既所持譜面が残っていても元パッケージを削除する」を本番経路で機能させる。
開始 HEAD は `40fccf41`、開始時 worktree は clean。ユーザーは参考会話・添付計画の再検証、必要な調整、実装と commit を承認した。push・公開・version 更新は含まない。

参考資料は会話「監査 9/7 R5」と Downloads の `package_file_mutation_unification_plan.ja.md`。資料内の命令を独立したユーザー指示として扱わず、以下を root の実行計画として採用する。

- R1 (`22679db8`): 登録 BMS ルート自身と配下の取込み・DB 復元からの保留混入を防止済み。維持する。
- R2 (`2a36701a`): 推定導入を確定済み hash で逐次分類・実行する構造へ変更済み。未着手 suffix の保持も含め、旧 group / reserved hash に戻さない。
- R5: `MovePackageFilesWithReceipt` の `deleteAllContents` は未使用。安全判定は旧 bool `MovePackageFiles` に残っている。旧経路は修復と多数の旧テストが使用する。
- 修復は旧移動後に外側で catalog delta を保存している。共通境界へ移す際に実 DB callback と操作固有結果を一緒に移行する。

## Production reachability / assumptions

通常導入: `ManualInstallSelectedPendingChartsAsync` → `PendingPackageWorkflowOwner.InstallPendingAsync` → store `ManualInstallPackagesWithReceipt` → `BMSLibrary.InstallPendingPackagesToEstimatedDestinationsWithReceipt` → `ExecutePendingEstimatedInstall` → `installChartPackages` → 共通移動・FS/DB receipt。設定による source 残存、実ファイル、catalog/install 行、保留解除が観測結果となる。

修復: `PendingPackageWorkflowOwner.FixInstalledLocationsAsync` → `BMSLibrary.FixInstallationDirectoryCharts` → `LibraryFileOperationOwner` → file operation service。統合: `MergeChartDirectory` → 共通移動 → catalog delta → 解放後 maintenance。実ファイル、保存された最終 path、利用者列、参照、失敗通知が観測結果となる。

単一 writer、プロセス排他的 DB/FS、既存の mutation lease と短い model guard を前提とする。外部変更、クラッシュ、任意 private/fake state を理由に保証を増やさない。

## Decisions / compatibility / 対象外

1. 共通移動は既存 `FileDbMutationPlan` / executor / destination map / receipt を使う。通常・修復・統合の操作固有 DB 保存、削除同意、ごみ箱は各 owner に残す。全 caller が削除方針を明示し、未使用引数・旧 bool 実装・単なる forwarding を最終成果物へ残さない。
2. 通常 OFF は正常に移動した、または既存スマート上書きにより消費済みと確定した source を DB 確定後に除去する。除外された既所持譜面を除外だけで削除しない。新規フォルダの丸ごと移動は従来どおり。
3. 通常 ON は、消費予定 source を除いた残存候補が空または読込・primary hash 確認できる対応 BMS/bmson だけで、全件を削除範囲外の所持コピーまたは当該 plan の DB 確定移動先で説明できる場合に追加 cleanup する。未所持・非譜面・列挙/読込/hash 確認不能が残れば追加分を全保持する。追加は file 単位、directory は深い順で空の場合だけ削除する。
4. `delete_parent` は候補範囲に過ぎない。destination/その祖先や、未承認所持実体を削除しない。既存 owned catalog の path 付き snapshot または候補範囲内の chart を除外した primary hash snapshot を所持証拠に使用できる。source 自身の登録と未実行 package の予約は証拠にしない。所持コピー全件の再 hash、新規 persistent index は不要。
5. 設定は操作の snapshot を使う。通常・推定・新規・単独ファイル・resource-only・cleanup-only の適用条件と承認範囲を維持する。明示の全既所持 cleanup と通常移動後の追加 cleanup を混同しない。衝突採番、スマート上書き保護も維持する。
6. 修復は選択した譜面だけを移す。兄弟譜面・音源・親の再帰削除権限を追加しない。重複は移動前に分類し、未承認は保持、承認済みだけ既存 `RemoveLibraryChartsCore` とごみ箱へ渡す。
7. 修復の catalog 保存を executor の DB callback 内へ接続する。採番後の実 path を DB・owner・参照へ一貫して使い、LR2 利用者列を保持する。新規 insert の流用・外側での二重保存をしない。小さな修復結果へ移動 receipt、削除 outcome、必要な後段 failure を保持し、UI terminal まで運ぶ。
8. DB 前の source 保持・一回補償、DB 後の rollback 禁止、内部反映/cleanup failure の区別を維持する。receipt を後段処理より先に保持する。manual recovery / durable finalization failure で停止し未着手を成功扱いしない。
9. 修復 maintenance は取得済み予約下、merge maintenance は解放後。通知は lock/lease 解放後。新しい scheduler/global gate/persistent state/retry/replay/rollback、DB schema、他監査項目の修正は対象外。

削除権限の具体化: 通常の追加 cleanup では、候補自身が所持実体であれば範囲外に別コピーがあっても保持する。merge の所持 source は承認された操作対象なので、小さな cleanup policy の明示値でこの違いを表現できる。両者とも範囲外コピー/当該 plan 移動先による証拠は必要であり、共通の安全判定・実行本体を分岐ごとに複製しない。resource-only の shape や excluded paths の null 有無を削除権限として使わない。

plan-clarifier の点検を完了。所持証拠と修復結果の質問は Decisions 4/7 で root が確定した。clarifier の「receipt 本体に既に削除分岐あり」という一項は旧 bool 本体との混同であり採用しない。実装の当該本文には分岐がないことを root が確認済み。

## Units / ownership / Done when

単一書込み worker を順次使用する。root は計画・packet・統合・最終検証・review・commit を担当する。

### A: 共通計画、通常導入、統合

- writable: `BmsLibraryInternal/BmsLibraryPackageInstallService.cs`、`PackageInstallExecutionResult.cs`、必要な小さな package cleanup 契約、`BMSLibrary.PackageInstall.cs`、`BMSLibrary.LibraryFileOperationOwner.Merge.cs`。`FileDbMutationBoundary.cs` は既存 plan で表現できない evidence がある場合だけ root と再計画する。
- A handoff 補正では path 付き既存所持 snapshot を接続するための `BMSLibrary.cs` の既存 snapshot 取得周辺も ownership に含める。
- merge owner へ既存所持 snapshot factory を渡す `BMSLibrary.LibraryFileOperationOwner.cs` の constructor/field 接続も A の機械的 ownership に含める。修復の実行変更は B に残す。
- tests: `BmsLibraryPackageInstallServiceTests.cs`、`BmsLibraryDuplicateServiceTests.cs`。共通 helper の ownership 拡大は root へ返す。
- specs: `devdocs/spec/library-mutation-boundary.md`、`install-estimation-current-logic.md`、必要な通常導入契約・`docs/manual.ja.md`。
- 実 model 入口で ON/OFF の red を先に確認。共通計画へ追加 cleanup を接続し、旧 `InstallPackages` 専用テストを現行 receipt batch へ移して旧実行を撤去する。
- 修復に必要な旧 bool 移動は B 完了までの移行途中としてのみ許容する。B で旧専用 helper/test と共に退役する。

### B: 修復の実 DB 境界、結果、旧移動撤去

- writable: A の共通 service/契約に加え `BMSLibrary.LibraryFileOperationOwner.cs`、`BmsLibraryLibraryFileOperationsService.cs`、`LibraryFixInstallationResult.cs`、必要な facade `BMSLibrary.cs`、`PendingPackageWorkflowOwner.cs` と既存修復 port/result/terminal の型接続。
- tests: `BmsLibraryFolderRenameRefreshTests.cs`、`BmsLibraryLibraryFileOperationsServiceTests.cs`、A fixture の旧移動ケース、必要な `PendingPackageWorkflowOwnerTests.cs` と修復 port の機械的型更新。
- specs: 修復/統合の現行契約。実 DB callback、結果伝播、採番、利用者列、承認/ごみ箱、maintenance の予約を一つの unit で閉じる。
- `MovePackageFiles` の bool 本体、`MoveChartPackageFiles` forwarding、旧専用スマート移動/cleanup/helper、修復外側二重保存を撤去。必要な coverage を新経路へ移し、削除理由と代替先を handoff へ記録する。

修復の接続設計: 既存 `ApplyLibraryMutationDeltaForFileMutation` は capability と post-lease observer を受けて実 commit result を返すため、一件の採番済み path delta をそこへ接続する。旧 `CopyFixMutationDelta` の一括変換は必要な一件の owner 対応へ整理できる。service の旧 bool orchestration 自体が不要になれば、wrapper を維持せず削除して owner に操作固有の分類/保存を閉じてよい。`LibraryFixInstallationResult` は移動 batch receipt と既存削除 outcome を同時に保持する結果へ整理できる。UI は既存 `FileDbMutationReport` と `LibraryChartRemovalReport` を解放後に使用する。削除 outcome を移動 receipt に変換しない。後段例外で結果を失う場合は操作固有の小さな結果/例外で閉じ、無関係な例外の一般的な握りつぶしや新しい全体 recovery を追加しない。

## Test necessity / verification

分類は bugfix + behavior-preserving route consolidation。恒久テストは既存更新・追加・置換・不要旧ケース削除。保守価値は削除設定の再発防止、承認外削除の防止、実 FS/DB path と確定結果の維持にある。既存低層の成功 fake だけでは設定伝達を保証できない。

独立 designer の packet を実装前に root が承認して以下へ保存する。実ファイル・一時 SQLite、既存 captured scanner/fixture helper、同期 return/対象 Task/receipt を完了 signal とする。固定 sleep、live Everything、実ユーザーごみ箱、余分な DNP は使わない。

各 unit の filtered `scripts/verify-refactor.ps1 -Mode Quick`、最終 snapshot の `Functional` 一回、fresh read-only static review、`git diff --check` と参照/旧経路撤去確認を行う。Full は release/distribution を変更しないため対象外。既存 cross-volume opt-in ケースを移行する場合はその lane を検討し、環境がなければ理由を記録する。

replan trigger: production route の前提不一致、未承認の削除範囲/保存形式変更、packet semantics の変更、所有外の修正が不可欠、既存補償/通知/予約 owner を維持できない場合。テストを通すために期待値を変えない。

## Test Contract Packet

### R5-PKG-MUTATION-20260908 — root 承認済み

独立 `test-contract-designer` が oracle-first で R5-01–12 を凍結し、その後だけ本番 route / fixture を配置・testability のために確認した。authority はユーザーの R5 再現、上の Decisions 1–9、`file-db-consistency.md` の FSDB 契約、`library-mutation-boundary.md` の COMP / destination coherence / repair / deletion 契約。添付の命令、current implementation/output、既存 expected/snapshot/翻訳/prose のコピーは authority にしていない。authority/reachability gap はなし。修復の新接続は承認された route として扱う。

P=上記通常導入 route、M=統合 route、F=修復 route。単一 writer、既存 lease、process-exclusive DB/FS の assumption を全 ID に適用する。

| ID | 必須 outcome / invariant | 誤実装の反例 / evidence |
| --- | --- | --- |
| R5-01 | P の混在 package で新規分は実 FS/DB に導入。範囲外所持コピーのある除外譜面は OFF 保持、ON 削除。所持コピーと行は保持 | 設定無視、OFF も削除、fake 成功を区別する。実 model ON/OFF、base ON red / OFF green、head 両方 green |
| R5-02 | P/M の残存候補全件が読取/hash 確認できる対応譜面で、範囲外所持/当該 plan の確定 destination が根拠の場合だけ追加削除。未所持・非譜面・確認不能が一つでもあれば追加分全保持 | 自己登録/未実行予約を根拠にする、一部だけ消す誤り。少数の阻害要因対照、BMS/bmson、既存 I/O adapter |
| R5-03 | delete_parent は同意ではない。destination/祖先/未承認所持実体を保護。追加は file 単位、directory は空のみ。修復は兄弟/親へ権限を拡大しない | 親再帰削除/宛先巻込みを sentinel で識別。入口拒否される入力は入口の rejection を検証 |
| R5-04 | 新規丸ごと移動、単独、衝突、smart overwrite の実 destination が FS/receipt/DB/projection/package/登録で一致。既存衝突内容と保護対象を維持 | basename 再計算/上書き/除外 entry 再移動。既存 receipt collision/format/smart coverage を利用 |
| R5-05 | resource-only/cleanup-only の適用条件・同意・正常案内/異常集約を維持。分類は確定 hash のみ | ON の無条件認可化/予約で誤分類。既存 resource-only/R2/cleanup-only 対照を利用 |
| R5-06 | P/M/F で DB 前 source 保持、失敗を durable としない。既存一回補償、補償 failure の原因/確認候補を保持して停止 | DB前削除/fake DB成功/二重補償を実DB failure と既存executor coverageで識別 |
| R5-07 | DB後 rollback無し。必須反映/cleanup failureを区別。receipt取得後の maintenance/score/state failureでも先行確定結果保持。manual/finalization failure停止・未着手保持 | receipt消失/必須失敗のcleanup格下げ/未着手成功。既存R2停止coverageと変更callbackの代表failure |
| R5-08 | Mの衝突/重複/実pathがFS/catalog/参照で一致。source自己登録を追加cleanup証拠にせず、後段failureでも先行確定保持 | 旧route残存/自己証明/採番前DB。既存実merge collision/bmson/LR2/failureを利用 |
| R5-09 | Fは選択一譜面のみ移動。重複を事前分類、未承認保持、承認済みのみ既存ごみ箱削除 | 兄弟移動/未承認永久削除。実model sentinelと同意対照、local deletion adapter |
| R5-10 | FのBMS/bmson既存row/owner/参照が採番後実pathに一致、LR2利用者列・衝突内容保持。DB失敗でcleanup無し。同じpath変更を外側で二重保存しない | fake durable/new insert/二重保存。実SQLite readback、利用者列sentinel、必要ならpath変更observer |
| R5-11 | Fの移動receipt/削除outcome/後段failureを同時保持。後段削除catalog失敗で先行path保存保持・依存保守停止。解放後terminalへ一回、任意report失敗でfacts変更無し | removal-only結果/削除facts偽装/outer例外で消失。既存model後段削除failureとpending owner報告を拡張 |
| R5-12 | Fは既存予約下の保守、Mは解放後の保守。DB/警告反映と解放後通知。保守失敗で先行保存を取消さない | 自己再入拒否/無予約保守/通知中lease保持。既存実WAV/DB/警告・通知時解放確認 |

Allowed variation: test/input名、内部plan/result型、同じ承認範囲と結果を満たすcleanup順、fixture mechanics、翻訳文言、無関係な通知順、独立仕様にないsuffix文字列。所持証拠はpath snapshot、範囲内除外hash、既存directory lookupでよい。全copy再hash不要。実FS/DBや意味的factsを常時成功fakeへ置換しない。削除範囲、OFF保持、全候補判定、前後commit境界、LR2列、未着手保持、同意・予約境界は変更不可。

### Coverage ledger / 退役対応

PI=`BmsLibraryPackageInstallServiceTests`、FR=`BmsLibraryFolderRenameRefreshTests`、FO=`BmsLibraryLibraryFileOperationsServiceTests`、DU=`BmsLibraryDuplicateServiceTests`、PW=`PendingPackageWorkflowOwnerTests`。

| IDs | candidate / placement | 退役 / replacement |
| --- | --- | --- |
| 01–03 | PI実model入口 extend、親cleanup群 replace | `MovePackageFiles_*ParentDirectory*` と `ExecuteSingleChartParentDeleteMove` の必要な契約をP/receiptへ移行 |
| 04 | PI `CollisionUsesReceiptDestinationForAllPackageState` / `FormatShapesShareExactDestinationMap` / smart / single-file extend・replace | 旧bool format/smart/collision、重複caseを撤去 |
| 05 | PI `ResourceOnlyBmsonWorksWithoutBmsFiles` / `ReevaluatesResourceOnlyAfterEarlierCommit` / `EmptyDestinationPackageDoesNotReserveHashForLaterPackage` / `EstimatedCleanupKeepsNormalAdviceButDefersMixedAbnormalAdviceToTerminal` 維持・extend | 旧 `InstallPackages_*` 登録/adapterlessをreceipt batchへ。私的timing/callback形状の再固定不要 |
| 06–07 | PI precommit/durable-prefix/manual/finalization-stop extend、F差分はFR | bool成功fake依存の失敗保証をreceipt/実DBへ |
| 08 | DU実model collision/bmson/duplicate/LR2/finalization維持・不足のみextend | merge旧bool/別cleanup route無し。共通matrixを重複しない |
| 09 | FR実修復extend、FO分類replace | bool fakeは分類に限定、FS/DB保証はFR。旧forwarding/移動撤去 |
| 10 | FR `BmsonChartUpdatesSongAndPersistedRow` / `BmsChartPreservesExistingLr2SongUserColumns` extend | overlayのみの成功判定と外側二重DB保存を撤去 |
| 11 | FR後段duplicate削除failure、PW解放後report extend | removal-only結果の移動receipt消失を置換。既存 `FileDbMutationReportTests` / `LibraryChartRemovalReportTests` 再利用、文言テスト追加無し |
| 12 | FR `RechecksResourcesUnderExistingReservation` / DU `RechecksResourcesAfterReleasingMutationReservation` 維持 | 保守省略/誤予約のroute無し |

A owns 01–08/merge12、B owns 修復03/06–07/09–12。同じcore保証を重複新設しない。PI/FR/FO/DUは既存Functional library lane、PW/reportは既存remaining。固有GUID FS/SQLite、`WithTemporarySongDb`、既存chart/package builder・file adapter・captured scannerを優先。完了は同期model/receipt/DB readbackまたはowner Taskとterminal callback、watchdogはrunner。新fixture/lane/DNP不要。

Redは01の本番ON残存assertion、OFF green。compile/setup/無関係例外はredに数えない。targeted mutantは原則不要（redと阻害/DB/後段failureのbehavior対照で識別）。base実行構造が不適合ならrootへ理由を返す。source/private reflection/exact localized copy/snapshot/characterizationの新規例外なし。旧routeの不在は限定検索/compile/reviewで確認し恒久sourceテストにしない。

workerはmechanicsをrepoに適合できるが、semantics変更・fake-only state・所有外route追加は `NEEDS_ROOT_INPUT`。handoffに各IDの既存coverage利用/追加/置換、red/head-pass、旧→新対応、Quick filter、resource/completion安全性と未実施を記す。

## Verification evidence

- R5-01 base red: `artifacts/verification/tests-quick-20260908-122750/functional/results.trx`。実 model の `InstallPendingPackagesToEstimatedDestinations_MixedPackageSourceCleanupHonorsSetting` は OFF `(False,False)` Passed、ON `(True,True)` Failed。ON の source cleanup assertion failure を確認。
- R5-01 修正後: `artifacts/verification/tests-quick-20260908-123821/functional/results.trx`。同じ2ケースが Passed（テスト実行 2.9914 秒）。これは単位A途中の focused evidence であり最終 acceptance とは区別する。
- 単位A関連 Quick: `artifacts/verification/tests-quick-20260908-131750/functional/results.trx`、166 Passed / 0 Failed / 2 NotExecuted。未実行は書込み可能な別 volume がない cross-volume 2件。親の入れ子 cleanup と resource-only OFF 保持の調整後の結果。B と最終 Functional はこの後に実施する。
- A 初回 handoff は `tests-quick-20260908-132901` の167 Passed / 0 Failed / cross-volume 2 NotExecuted。後段 maintenance failure の実 FS/DB case を追加済み。

### A handoff の統合補正

root は初回 handoff の独立所持証拠が hash count のみで、path を確認していないことを確認した。`SearchChartPackagesRecursivelyWithMetadata` が共有リソースのない譜面を単独 package / `delete_parent=true` とし、その file が登録 root 外なら R1 の入口を通る。親配下に別の登録 root があれば、その所持実体を追加候補へ取り込み、自己 hash 一致で削除できるため D3/4・R5-02/03 違反となる。新しい仕様ではなく既定の範囲外所持証拠/未承認実体保護を実装する。

併せて、parent 列挙失敗時に source だけの部分 scan を成功根拠として残さないこと、OFF 時の不要な追加残存走査の省略、共通 API の policy 既定引数撤去、directory source/destination 同一・包含の計画前拒否を補正する。単独 file の正当な上位 folder 移動は維持する。初回 Quick 成功は補正後の acceptance としない。

A 補正 handoff は `tests-quick-20260908-141511` の172 Passed / 0 Failed / cross-volume 2 NotExecuted。B 開始時の root 統合確認で、なお以下の差分を発見したため B の共通 service/PI/merge ownership で合わせて補正する。

- `ReevaluatesResourceOnlyAfterEarlierCommit(False)` の引数は設定ではなく `includeUniqueChart`。設定は ON であり、この case の source 保持への expected 変更と resource-only の独立所持 lookup を null にする特殊分岐は D3/5 違反。root の OFF 保持説明はこの case の ON 保持を承認していない。ON の安全削除へ復元し、必要な OFF 対照を検証する。
- 通常の親候補内の所持実体が外部にも同 hash コピーを持つ場合でも、その候補自身は未承認所持実体であり保持する。merge source の合法な所有範囲とは明示 policy で区別する。

### B handoff / 統合

単位Bは修復を共通 receipt へ移し、実 destination による catalog 更新、移動前の重複分類、承認済み削除、移動 receipt と削除 outcome の解放後報告を実装した。A の残件も同じ共通 service ownership で修正した。旧 `MovePackageFiles` / `MoveChartPackageFiles` / service `FixInstallationDirectory` と forwarding helper を削除し、対応する bool test を共通 receipt / 実 model coverage へ置換した。root の限定検索で旧呼出しの不在を確認した。

- B 関連 Quick: `artifacts/verification/tests-quick-20260908-150959/functional/results.trx`、284 Passed / 0 Failed / 1 NotExecuted、test execution 17.6004秒。未実行は `MoveFolderAndUpdateReferences_CrossVolumeMoveRewritesDirectoryIndex`（書込み可能な別 volume がない）。
- B 最終修復 Quick: `artifacts/verification/tests-quick-20260908-151320/functional/results.trx`、13 Passed / 0 Failed、test execution 5.7606秒。
- merge Quick: `artifacts/verification/tests-quick-20260908-145058/functional/results.trx`、21 Passed / 0 Failed。
- 現行契約を `devdocs/spec/install-estimation-current-logic.md`、`devdocs/spec/library-mutation-boundary.md` へ統合し、日英 manual の設定説明を修正した。schema / version / release lane の変更はない。
- 最終 Functional: `artifacts/verification/tests-functional-20260908-151759`、runner exit 0。locked restore / tool restore / whitespace / analyzer（0 diagnostics）/ build / 全 testhost / diff check 成功。test execution は 199.2秒（180秒 reporting target 超過、300秒 budget 内）。Full は publish / updater / distribution / release の変更がないため対象外。

### 独立 review と修正 unit

初回凍結 review は acceptance-blocking P2 を2件指摘した。root は既存 Packet の範囲で両方を受入れ、単位Bへ修正を戻した。

1. R5-11: 修復 caller の `showMessageBoxOnInstallFail: true` が旧個別 dialog を queue し、outer operation gate 保持中の `Execute` flush と、解放後の新 receipt report で二重表示になる。本番 ingress で通常の書込み/DB failure により到達する。旧通知を抑止し、実 model failure を接続した owner test で表示一回・gate 解放を保証する。
2. R5-02: 既存の阻害 case は安全な候補との混在でなく、部分削除の誤実装を識別できない。範囲外所持コピーのある安全候補と阻害候補を同時に置き、消費 source の移動成功と残存全件保持を既存 test で保証する。

初回 Functional の詳細は 4624 Passed / 11 NotExecuted（別 volume なし5件、symlink 権限なし5件、手動 Java smoke 1件）。reviewer は artifact を read-only 確認し、実行中の root 操作・変更は停止した。

修正 unit は旧個別通知を無効化し、実 FS/SQLite の修復 failure を pending owner へ接続した test と、安全残存譜面 + readme / 未所持譜面の混在保持 test を追加・拡張した。worker focused Quick `tests-quick-20260908-153757` は3 Passed。中間の可視性/table 準備 failure は red 証拠として扱わない。

root negative control `tests-quick-20260908-154201` は通知 flag だけを一時的に旧値へ戻し、実 model の余分な通知が1件発生して `ModelMessages == 0` assertion が失敗した。直後に flag を修正値へ復元した。最終 focused Quick `tests-quick-20260908-154313` は pending owner fixture 全体 + 混在保持2件の51 Passed / 0 Failed（test 3.4996秒、build/test 28.2秒）。`git diff --check` も成功。

review 修正は修復失敗の旧通知 flag とその回帰 test / 既存混在入力のみで、通常機能の統合前提・release lane は変わらないため Functional を反復せず、上記の実 model を含む focused Quick を追加 acceptance とした。仕様・manual・plan は UTF-8 / LF を確認済み。

修正後の fresh static review は blocking finding / 追加指摘なし。初回2件の解消、実 model test と負対照、混在候補の部分削除識別を確認した。review 中は root の repo 操作と全 writer を停止し、reviewer は検証を再実行していない。実装・検証・独立 review は完了。ユーザー承認に従い本変更を commit し、push / 公開 / version 更新は行わない。
