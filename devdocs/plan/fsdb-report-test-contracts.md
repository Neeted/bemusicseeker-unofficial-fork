# FS+DB 結果伝達 Test Contract Packets

Status: Active

実行計画は [file-db-consistency-follow-up.md](file-db-consistency-follow-up.md)。本資料は独立した test-contract-designer が oracle-first で設計し、root が承認して実装前に凍結したテスト契約である。current output／既存 assertion／翻訳文言／snapshot は oracle の authority にしない。

## FSDB-A-20260905 — Approved

Change class: bugfix + 承認済み terminal reporting feature。Base: `402cd729`。根拠はユーザーの終了時一回の集約ダイアログの選択、root の表示・failure decisions、`file-db-consistency.md` の `FSDB-FACTS`／`REPORT`／`FORWARD`／`LIMITS`、library mutation の既存補償・post-lease 契約、多言語 parity 契約。

Designer は production／test body を読む前に A01–A07 を凍結し、その後に到達性と配置だけを確認した。対象 behavior の authority gap はなし。merge 固有の実 FS／DB postcommit failure の具体的 setup は未確定のため、fake callback failure を必須 runtime scenario に増やさない。

### 入口・前提

- R: folder-edit event → `RegularChartListOwner.RenameChartFolderAsync` → `BMSLibrary.RenameChartFolderWithReceipt` → `LibraryFolderMoveCoordinator`／executor／catalog finalizer → owner terminal。
- M: selected context-menu move → `SelectedChartMutationWorkflowOwner.MoveAsync` → production terminal store → `MoveLibraryRootFolderWithReceipt` → coordinator／executor → owner terminal。
- D: duplicate-folder merge terminal → `RunFolderMergeAsync` → production terminal store → `MergeChartDirectory` → package executor／catalog finalizer → owner terminal。
- 共通前提: 有効な対象と必要な承認、既存の process-exclusive DB／single-writer と admission、利用可能な terminal dialog。crash／全通知先喪失／未承認の外部 DB 変更からの回復は対象外。
- 実到達 evidence: R の LR2 `folder` INSERT abort は song move の commit 後の必須反映 failure を起こす。M の song INSERT abort と補償 delete failure は manual recovery へ到達する。source cleanup は既存 `IFileMutationService` で failure を起こせる。activity observer exception は任意通知の failure として扱い、必須 finalizer の authority にしない。

### Oracle ledger

| ID | Required outcome | Allowed variation | Wrong variant / evidence |
| --- | --- | --- | --- |
| A01 | R／M／D で正常時は追加 dialog なし。異常時は集約一回。FS／model lease、outer gate／activity と終了 cleanup の後で表示。 | cleanup 内順序、Task scheduling、文言 | per-item 表示・gate 内表示・正常にも警告。dialog callback で gate 再取得と activity inactive、代表実 model route の解放を確認。 |
| A02 | cleanup-only は Warning かつ durable success 保持。non-durable／manual／必須反映 failure は Error。混合は最重 severity、finalizer と cleanup の併発は両次元保持。receipt 操作数を数え、未処理を成功件数へ入れない。 | path／集約順、内部 report 表現 | durable flag だけで正常判定、cleanup が finalizer を覆う、receipt 数を file 数と呼ぶ。到達可能な結果の小さい table cases と owner 接続。 |
| A03 | 先行 durable item とそのデータを保持し、部分失敗は全正常にしない。追加 failure で receipt を捨てない。必須反映 failure item の success publication なし。既存契約で許可された成功 item の表示挙動は保持。 | 独立 item の既存継続方針、保持 mechanics | catch で receipt を捨てる、全 rollback、失敗 item の成功 event。実 FS／DB fault と terminal facts を併せて確認。 |
| A04 | 操作、確認済み完了、未完了・未確認、対象、対応を表示。候補 path 最大 3 件・各 240 文字、primary error 各 400 文字、全体 4096 文字以内。詳細は既存 logger。候補を実在と断言せず、retry／必ず直る案内なし。 | 翻訳、改行、ellipsis、候補選択順、配置 | 無制限列挙、最終上限なし、未確認の実在断定。識別可能な入力と上限超過入力、guidance の resource review。exact copy なし。 |
| A05 | terminal-owned route の任意通知／presenter failure は利用可能な診断へ best effort。durable/result を failure へ変えず、既存 primary と receipt を保持。再帰通知・元 mutation 再実行なし。 | 診断文言、独立通知順序 | dialog exception による結果変換、primary 上書き、再通知。正常 receipt + optional subscriber throw／異常 receipt + reporter throw で call count と facts を確認。 |
| A06 | canonical receipt-backed failure の lower dialog と terminal を二重表示しない。terminal なし互換 caller、事前確認／拒否／destination exists の意味を維持。 | reporting ownership の API 形、互換文言 | 全 model dialog 無効化、二重表示、拒否を承認扱い。canonical と compatibility の paired cases、取消時 mutation なし。 |
| A07 | 新 key の compiled accessor／resx／6 言語 parity、nonempty、format と必要 placeholder の対応。全言語で bounded renderer の format 成功。 | 訳語、順序、punctuation | key／placeholder 欠落、範囲外引数、固定 UI 文言。既存 parity fixture を extend、formatter 引数契約を根拠にする。 |

### Coverage / safety ledger

| 対象 | Fixture / placement | Resource / completion / retirement |
| --- | --- | --- |
| A01／A03／A05／A06 R | `RegularChartFolderRenameTests`、`RegularChartListOwnerTestSupport`: extend | 固有 temp FS／DB、既存 scheduler／dispatcher、returned rename Task と recorded events、StopAsync。既存 LR2 finalization case を拡張し、新 case に private reflection helper を増やさない。Functional remaining。 |
| A01／A02／A03／A05／A06 M | `SelectedChartMutationWorkflowOwnerTests`: extend | local terminal store／dialog／gate／activity、MoveAsync Task／TCS。receipt 喪失を容認する期待を置換。store-only を実到達の唯一の証拠にしない。remaining。 |
| A01／A02／A03／A05／A06 D | `DuplicateMaintenanceWorkflowOwnerTests`: extend／replace | local ports と gate、RunFolderMergeAsync Task、confirmation TCS。terminal route の `RunFolderMergeAsync_ObserverCleanupFailureIsAggregatedAfterPriorityRelease` は optional log-only に置換、互換 route へ拡張しない。remaining。 |
| A03／A06 model | `BmsLibraryFolderRenameRefreshTests`: extend | GUID temp DB／FS、SQLite trigger、既存 IFileMutationService adapter、recording model dialog。model return／publication signal。既存 `RenameChartFolder_FailureDialogRunsAfterFilesystemAndOnlyOnce` の互換 coverage を保持し canonical suppression を別途確認。remaining-bms-library。 |
| A02／A04／A05 report | `FileDbMutationReportTests`: new | 共有表示の新責務に対し local facts／recording dialog、formatter return／presenter Task。重複 summary helper を残さず feature Verification map を更新。remaining。 |
| A07 | `LocalizationResourceParityTests`: extend | read-only resources／accessor、同期 return。exact localized snapshot は追加しない。既存 lane。 |

### Red / negative control

- 既存 owner API で可能な R の LR2 failure は base-fail／head-pass を優先。base にも失敗 dialog はあるので、欠落する facts／集約／解放時点の mismatch を red 理由にする。
- M の異常 receipt と post-release optional observer failure を使い receipt 喪失を区別する。任意 observer failure を必須 finalization failure へ分類しない。
- 新 presenter API の test が base では compile できない場合、その部分は targeted mutant を使う。候補は durable-only 成功判定、最初の severity だけ採用／failure dimension の欠落、report の gate 前移動または lower dialog 抑止解除、表示上限の除去、presenter failure の primary 置換／再通知。対応する changed decision を識別する少数の代表 mutant を選び、適用・red evidence・復元を記録する。
- compatibility 正常維持は base-green でよい。全 dialog 抑止 mutant が paired case で落ちることを退役範囲の証拠にできる。
- 新しい DNP、visible window、global logger 設定、fixed sleep、runner timeout 変更は行わない。source／reflection／exact-copy の例外はなし。test fake は実到達する結果の識別にだけ使う。

Worker は fixture／recording／assertion mechanics を調整できる。severity、独立 failure dimensions、durable 保持、operation count、上限、解放後表示、optional failure log-only、互換 route を変えてはならない。private/fake-only state しか作れない場合、意味変更、retry/probe/state 追加が必要な場合は `NEEDS_ROOT_INPUT`。focused Quick は対象 fixture + localization parity、Functional は root、Full／opt-in は不要。

## Packet FSDB-B-20260905 — 残りの receipt consumer

- Change class: bugfix。root 承認: 2026-09-05、B 実装前。
- Authority: 利用者の終了時一回通知の決定、本計画の B decision、FSDB-FACTS / REPORT / FORWARD。既存 generation / admission / shutdown publication を維持する。
- 独立性: designer は production body、existing assertion、runtime output、翻訳、snapshot の参照前に oracle を凍結し、その後だけ route / fixture を確認した。authority / reachability gap: none。現在の implementation evidence は oracle ではない。
- 全 Contract ID は behavior。入口 assumption: 現行 library generation、既存 admission、承認済み対象、process-exclusive DB / single writer。

| Contract ID | Production ingress / required outcome | Allowed variation / wrong implementation / evidence |
| --- | --- | --- |
| FSDB-B1 | 選択／全自動 rename request → FolderAutoRenameWorkflowOwner → completion/failure → production UI consumer。外側 gate/activity 解放後、一回通知。cleanup-only は durable を保持した Warning、non-durable/manual/required-finalization は Error。primary、cleanup、durable prefix を保持し、refresh の有無で通知を落とさない。 | 翻訳、内部 result、独立項目順序、正常 refresh は自由。completion だけ購読、RefreshRequired の早期 return が反例。selected/all ingress と実 consumer で cleanup/finalization を観測する。cleanup-only と refresh=false の到達不能な直積は要求しない。 |
| FSDB-B2 | dropped paths enqueue → ProcessBatch → BmsLibraryPackageInstallMutationPort → completion → UI。新規 Packages が 0 件でも異常 receipt を報告。cleanup-only は Warning、error dimension は保持する。 | 正常空 no-op は silent、queue continuation 維持。Packages.Count の中だけで報告する実装を落とす。実 cleanup-only candidate モデル companion と enqueue→completion→dialog を使う。 |
| FSDB-B3 | 保留の強制／手動導入の四 UI route → ExecuteInstallAsync → MainWindowPendingPackageMutationViewTerminal.ApplyAsync。同じ receipt facts を一回報告し、ShouldApplyView/navigation で異常を省略しない。正常は silent。 | 既存の正常 selection/navigation、copy、private 構造は自由。package route だけ接続し chart route を漏らす反例。四つの軽量 wiring と代表 owner/terminal warning/error、formatter 全 matrix は複製しない。 |
| FSDB-B4 | B1–B3 の実 mutation →既存 cleanup → report。durable/primary/finalization/cleanup/recovery facts を保持。report failure は診断のみ、outcome を変更せず再通知／再実行しない。無関係な lifecycle failure は既存伝播。 | exception 容器、診断文言は自由。finally で receipt を捨てる、presenter failure を install failure として再報告する反例。既存 suppression/dialog-scope cleanup の限定 failure、reporter failure を注入し、facts と mutation/notification 試行数を観測。 |
| FSDB-B5 | receipt-aware model ingress → owner terminal、対照は legacy / receipt-less refusal。同じ receipt の model 個別表示と report を重複させず、confirmation と receipt なし事前拒否は保持。正常は silent。 | 既存確認選択肢、翻訳、診断順序は自由。全 model dialog 抑止、旧 item dialog 残留を落とす。model dialog と IUiDialogService の両方を観測し、confirmation と結果件数を区別。 |

### B coverage / verification

| IDs | Candidate fixture / placement | Resource / completion / retirement |
| --- | --- | --- |
| B1 | FolderAutoRenameWorkflowOwnerTests を extend、モデル companion は BmsLibraryFolderRenameRefreshTests | GUID temp FS/DB、既存 port、completion/failure event と WaitForIdleAsync。receipt 消失経路を置換。 |
| B2 | PackageInstallWorkflowOwnerTests、BmsLibraryPackageInstallServiceTests を extend | GUID temp ingress/DB、既存 port、completion/idle signal。package 件数による通知省略を退役。 |
| B1/B2 wiring | 必要なら MainWindowFileDbMutationConsumerTests を new、MainWindowViewModelTestFactory/ApplicationComposition の既存 composition を使用 | TestUiDispatcherHost、recording dialog、実 production request terminal/dispatcher completion。window 表示なし。test 側で production handler を再実装しない。 |
| B3 | PendingPackageWorkflowOwnerTests、MainWindowPendingPackageMutationViewTerminalTests、四 route は MainWindowPackageMaintenanceWpfTests を extend | 既存 constructor-only WPF harness、recording store/dialog、awaited owner→terminal task/event。receipt-backed 個別通知を置換。 |
| B4/B5 | 上記 fixture の近傍を extend / 必要時 replace | 同じ resource、report callback で gate 再取得・activity inactive を観測。旧 dialog 件数だけの assertion を aggregate behavior へ置換。 |

通常 Functional lane、追加 DNP / 固定待ちなし。Quick は実際に触った fixture FQN と LocalizationResourceParityTests の OR。最終 Functional は root 一回。

- Negative controls: base 402cd729 で実行可能な regression は production 修正前 red。新 A API / wiring で base compile 不可なら理由を残し、packages/refresh guard 復活、failure subscriber omission、cleanup catch の receipt 破棄、旧 item dialog 再有効化から関連する代表 mutant を落とす。temporary mutation は戻す。
- Exactness exceptions: none。A の path 3件/240字、error400字、本文4096字、operation 件数を継承し、翻訳全文／formatter matrix を重複固定しない。
- Worker mechanics: fixture factory、adapter、data、assertion API は変更可。severity、全 failure dimensions、durable facts、通知 ownership、normal silent、no retry、無関係 failure 伝播は固定。新 recovery、generation 置換後の保証、failure 再分類が必要なら NEEDS_ROOT_INPUT。

## Packet FSDB-C-20260905 — library deletion の部分結果

- Change class: bugfix。root 承認: 2026-09-05、C 実装前。
- Authority: 利用者の前方回復・終了時一回通知、本計画 C decision、FSDB-FACTS / REPORT / FORWARD、hash cleanup の承認範囲。結果型の形や現行 assertion は oracle ではない。
- 独立性: B と同時に body/test/translation 参照前に oracle 凍結。authority / reachability gap: none。実 LR2 prune route の到達性は確認済み、worker が実行 evidence を得る。
- 全 Contract ID は behavior。既存 canonical identity、削除確認、file mutation admission、process-exclusive DB を前提とし、外部 writer/crash/zero-scan convergence は対象外。

| Contract ID | Production ingress / required outcome | Allowed variation / wrong implementation / evidence |
| --- | --- | --- |
| FSDB-C1 | selected/duplicate → BMSLibrary.RemoveLibraryCharts → LibraryFileOperationOwner →現 executor。API 成功 return の chart target だけ confirmed。exists=false は未実行・実在未確認、directory throw は配下未確認。missing-file purge、新 probe、成功数推測を追加しない。 | 既存独立 target 継続、record 表現は自由。planned count 成功扱い、false exists DB purge、directory failure 全子成功扱いが反例。real library/temp FS/SQLite に成功 target、missing catalog target、部分変更後 directory throw を分離して与える。 |
| FSDB-C2 | 同入口→FS→catalog。confirmed FS facts と apply attempted/failure を保持し、未観測 durable を推測しない。成功 FS の restore/replay なし。 | SQLite 文言、result 容器は自由。throw で FS facts を落とす、DB rollback から FS 未変更を推測する反例。対象 song の BEFORE DELETE RAISE(ABORT) で FS 消失・DB 行残存・terminal Error/targets を観測。 |
| FSDB-C3 | removal→catalog commit callback→SyncLr2NormalFoldersForCatalogMutation→normal folder prune。durable を保持した required-finalization Error。巻戻し、cleanup Warning、通常成功へ格下げしない。 | 診断表現、callback-free result 形は自由。全例外を non-durable に潰す、必須 LR2 failure を任意通知扱いする反例。LR2 enabled/root/最後の BMS/normal folder row、対象 folder DELETE abort。song 行削除済み、folder 行残存、FS 消失、durable+failure を検証。 |
| FSDB-C4 | Selected.DeleteAsync / Duplicate.RunHashCleanupAsync→store→library→feature terminal。outer lease/gate/activity 解放後一回。duplicate RemovedCount は confirmed FS target 数。catalog/finalization failure 後は success-only selection/maintenance を進めない。 | confirmation、keeper、純 FS 個別 failure の既存 continuation は維持。planned count、gate 保持中報告、catalog failure 成功扱いが反例。keeper+候補2で削除1、C2/C3 facts、gate 再取得・activity・terminal result を観測。 |
| FSDB-C5 | repair UI→FixInstalledLocationsAsync→store→repair delta apply→removal→outer finally→feature report。先行 commit を保持し、削除 catalog/finalization failure 後の dependent maintenance を停止。狭い typed failure のみ post-release に報告して global App error/exit へ再送出しない。 | 型名、容器、純 FS failure 継続、先行 delta なしの入力は自由。無関係例外の扱いを維持。typed failure 漏洩、catch 後 maintenance 継続、先行 rollback 済み表示が反例。real repair duplicate source failure と別の更新 target で先行 DB 変更、owner task/report、無関係例外対照を観測。 |
| FSDB-C6 | C1–C5 terminal→削除 report→IUiDialogService。confirmed、未実行/未確認/failure、catalog 段階、対象、手動確認案内を保持。unknown/notexecuted は Error、正常 silent、任意 report failure は診断のみ。 | 翻訳/順序/A の bounds は自由。unconfirmed を未変更扱い、必ず復旧する再実行案内、report failure 再報告が反例。既存 dialog capture を再利用し、mapping と試行数を検証。A 一般 renderer matrix は複製しない。 |

### C coverage / verification

| IDs | Candidate fixture / placement | Resource / completion / retirement |
| --- | --- | --- |
| C1–C3 | OwnedChartCollectionLibraryMutationTests を extend、OwnedChartCollectionTestSupport を再利用 | GUID temp FS/DB、TestBmsLibrary、file adapter、同期 real library outcome/dispatcher completion。legacy DeleteLibraryCharts direct test を現 executor の証明にしない。 |
| C2 | 同 fixture。CatalogMutationOwnerTests は trigger setup 参考のみ | test 固有 SQLite trigger、test readback は oracle 確認専用。DB failure の結果消失を置換。 |
| C3 | 同 fixture、LR2 helper は BmsLibraryDuplicateServiceTests / BmsPlaylistTestSupport.CreateLr2Config を再利用可 | instance options、root、song/folder rows、TestUiScheduler。背景完了を sleep で推定しない。 |
| C4/C6 | SelectedChartMutationWorkflowOwnerTests、DuplicateMaintenanceWorkflowOwnerTests を extend/replace | recording store/dialog、gate/activity、awaited task/report callback。RunHashCleanupAsync_ChoosesShortestNameAndReturnsRemovalCount の planned-count 部分を actual-result oracle へ置換、keeper coverage 保持。 |
| C1/C4 | BmsLibraryDuplicateServiceTests の RemoveLibraryCharts_BmsonPendingRow_UnregistersBmsonSong 近傍 | 既存 temp DB/file、real library completion。無関係な旧 service fixture へ重複追加しない。 |
| C5 | BmsLibraryFolderRenameRefreshTests の FixInstallationDirectoryCharts_BmsonDuplicateRemovesRepairSourceAndKeepsInstalledRow 近傍、PendingPackageWorkflowOwnerTests を extend | real repair temp FS/DB と recording owner/dialog、task/maintenance publication。handled deletion failure を global へ rethrow する route を退役。 |
| C6 | 必要なら LibraryChartRemovalReportTests を new、resource 追加時 LocalizationResourceParityTests | existing fixture/helper を優先。localized exact-copy assertion なし。 |

C3 の到達性: Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation が削除 BMS directory を prune scope へ加え、Lr2FolderDbWriter.ApplySyncPlan が songDb.Delete<LR2SongDB.folder> を呼ぶ。その前の durable callback を既存 ApplyLibraryMutationDeltaForFileMutationUnderExistingLease が保持する。private direct call は不要。

- Quick は変更 fixture + parity、通常 Functional lane、追加 DNP / fixed wait なし。root が最終 Functional 一回。
- Negative controls: 既存 ingress で runnable な regression は base-fail/head-pass。new result/typed exception のため compile 不可なら理由を記録し、false exists / directory throw の confirmed 誤追加、catalog catch の FS/durable 消失、RemovedCount=planned、typed catch 除去/maintenance 継続、reporter failure で outcome 改変/再通知から代表 mutant を落とし、元に戻す。
- Exactness exceptions: none。trigger は fault injection、source oracle ではない。path identity / confirmed count / durable は契約として検証し、翻訳全文・private symbol・callback の厳密な列は固定しない。
- Worker mechanics: record/fixture/DB setup/recording adapter/assertion API は変更可。FS 呼出し範囲、既存 plan/delta、confirmed 定義、no purge/rescan、先行 commit、catalog failure 停止、post-release 一回、unrelated failure handling は固定。production DB readback で commit を再判定しない。bridge で新 recovery/state/probe/純 FS 停止条件の変更が必要なら NEEDS_ROOT_INPUT。
