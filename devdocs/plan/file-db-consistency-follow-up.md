# FS+DB 整合性契約の適用計画

最終更新: 2026-09-05

Status: Active（A 実装・関連検証済み、静的レビュー待ち。B/C のテスト契約承認済み）

## 目的と範囲

共通契約の正本は [file-db-consistency.md](../spec/file-db-consistency.md)。本資料は確認済みの実装差分と、後続 unit の切り方だけを記録する。全 FS+DB route を監査済みとする記録ではない。

共通仕様は `402cd729` で commit 済み。後続実装と必要な単位ごとの commit はユーザー承認済み。push、version 更新、公開は対象外。

## 実装の決定事項

- **Goal:** 捕捉した FS+DB の部分失敗・cleanup failure を、利用者が通常完了と取り違えない。既存の結果情報を使い、未確認の FS 状態や DB commit を推測しない。
- **利用者確認済み:** 異常結果は操作終了後に一度だけ警告／エラーダイアログで要約する。完了部分、未完了の段階、確認対象、対応を表示し、多い詳細は既存ログへ記録する。自動再試行は設けない。
- **表示の既定:** 正常時は新しいダイアログを出さない。cleanup-only は durable success を保持した警告、未 commit／手動確認／必須反映失敗／削除の未実行・未確認はエラーとする。混在時は最も重い区分を使うが全 failure dimension を保持する。任意通知自体の failure は既存診断だけに記録し、元の結果を変更したり再帰的に通知したりしない。
- **表示の量と意味:** recovery paths は確認候補であり実在確認済みとは書かない。代表 path は最大 3 件・各 240 文字、代表 error は 400 文字、本文全体は 4096 文字以内。省略時は詳細ログを案内する。receipt の件数は操作単位であり、ファイル数と呼ばない。全件の対象・例外の診断は既存 logger を使う。正確な翻訳文言はテストの oracle にしない。
- **互換性:** FS／DB の実行順序、既存 source retention／補償、空走査保護、batch の継続・停止条件、既存の承認ダイアログを維持する。対応する結果を集約表示する経路では旧 receipt-backed 個別通知を退役し、receipt のない事前拒否や legacy caller の通知を無言にしない。
- **削除の範囲:** 結果伝達を変更する。存在確認の false は削除未実行・実在未確認とし、DB purge の根拠にしない。directory 削除の例外は配下の結果が未確認として扱い、新しい再走査を加えない。確認済み削除対象と既存の DB delta の範囲は維持する。
- **対象外:** journal、retry、破壊的 replay、新しい永続状態、global fault latch、0 件からの必ずの収束、補償 executor の全面改修。

## 実装単位と ownership

同じ owner、resource、fixture に変更が及ぶため writer は一つずつ、A → B → C の順にする。root は計画・契約承認・統合・commit を所有する。UI resource は各 unit で `Resources.resx`／`Resources.cs`／6 言語 JSON の parity を揃える。

| Unit | Observable outcome / 所有範囲 | Verification / 退役対象 |
| --- | --- | --- |
| A: folder receipt の集約表示 | 移動・手動 folder rename・merge の既存 receipt を stateless な表示処理へ渡す。feature owner の terminal と必要な composition、folder／merge の旧通知、表示用 resources を所有。lease と外側 gate の解放後に表示する。 | `RegularChartFolderRenameTests`、`DuplicateMaintenanceWorkflowOwnerTests`、`SelectedChartMutationWorkflowOwnerTests`、関係する folder model fixture、localization parity。対応する receipt-backed 個別エラーと結果消失を置換する。 |
| B: 残りの既存 receipt consumer | A の表示処理を自動 rename、drop install、保留の強制／手動導入へ適用する。各 feature owner／terminal／composition と対応する旧通知を所有。`RefreshRequired == false`／package 0 件でも異常 receipt を表示する。 | `FolderAutoRenameWorkflowOwnerTests`、`PackageInstallWorkflowOwnerTests`、`PendingPackageWorkflowOwnerTests` と直接 terminal fixture。通常結果だけを条件にした通知省略と個別 receipt エラーを置換する。 |
| C: library deletion の部分結果 | deletion executor／`LibraryFileOperationOwner`／facade、選択削除・重複削除・導入先修正の直接 caller、削除固有の小さな immutable result と表示、resources を所有。confirmed FS results と既存 catalog commit result を terminal まで保持する。 | `OwnedChartCollectionLibraryMutationTests`、選択／重複／保留 workflow fixture、関係する duplicate model fixture、localization parity。個別 delete dialog、計画件数を削除実績とする扱い、result を失う throw 経路を置換する。 |

モデル・gateway の failure／post-lease 順序が正しさに影響する unit には `implementation-worker-frontier` を使用する。言語 parity の固定 8 files を含むため file 数は増えるが、operation family 単位で分離し、同じ巨大 file を同時編集しない。追加の subsystem や永続化を必要とする evidence、caller の意味変更、テストでしか到達しない failure しか構成できない場合は編集を止めて root へ戻す。

各 unit の実装前に oracle-first の Test Contract Packet を承認・保存する。候補 fixture は配置の evidence として使い、既存 assertion／翻訳文言を expected semantics の authority にしない。GUID 付き temporary FS／DB、既存 UI dispatcher／recording dialog、Task／event completion を使う。runner、lane、timeout、DNP の変更は行わない。

各単位の filtered Quick と必要な red／negative control、凍結 snapshot の静的レビュー後に commit する。通常機能の統合 acceptance は最終 snapshot の Functional を一回とし、review 修正後の追加 lane は testing strategy に従う。

### Unit A の final design / writable paths

既存 `FileDbMutationReceipt`／`FileDbMutationBatchReceipt` を入力とする stateless な report formatter／presenter を ViewModel の表示責務に置き、既存 `IUiDialogService` を使う。新しい domain result hierarchy、global dialog coordinator の集約状態は作らない。各 feature owner の terminal が明示的に一回呼ぶ。手動 rename には既存の狭い dialog dependency を composition から渡し、UI thread で sync-over-async をしない。

canonical receipt-aware entry は model の receipt-backed 個別表示を明示的に抑止し、新しい terminal を唯一の表示者にする。既存の表示あり入口は同じ処理を通して残し、legacy／receipt-less preflight の通知を失わない。抑止は通知の ownership だけを変え、FS／DB の処理を分岐させない。

failed receipt を含む batch を通常成功にせず、既に durable となった item の事実は残す。cleanup-only は成功を保持する。gate／activity／scope cleanup の failure が重なっても receipt を捨てず、primary failure と共に保持する。

Worker A の許可 path（必要なものだけを変更）:

- `BeMusicSeeker/ViewModels/MainWindow/FileDbMutationReport*.cs`（表示用の新規 file のみ）
- `BeMusicSeeker/ViewModels/MainWindow/RegularChartListOwner.cs`
- `BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/MainWindow/DuplicateMaintenanceWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/ApplicationComposition.cs`（上記の composition wiring のみ）
- `BeMusicSeeker/Models/BMSLibrary.cs`、`BeMusicSeeker/Models/BMSLibrary.LibraryFileOperationOwner.cs`（folder entry の表示 ownership 引数と wiring のみ）
- `BeMusicSeeker/Models/BMSLibrary.LibraryFileOperationOwner.Merge.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/LibraryFolderMoveCoordinator.cs`
- `BeMusicSeeker/Properties/Resources.resx`、`BeMusicSeeker/Properties/Resources.cs`、`lang/en-US.json`、`lang/fr-FR.json`、`lang/ja-JP.json`、`lang/ko-KR.json`、`lang/zh-CN.json`、`lang/zh-TW.json`（`FileDbMutationReport_` prefix の新規 key）
- `BeMusicSeeker.Tests/FileDbMutationReportTests.cs`（packet が必要とした表示処理の新規 fixture のみ）
- `BeMusicSeeker.Tests/RegularChartFolderRenameTests.cs`、`BeMusicSeeker.Tests/RegularChartListOwnerTestSupport.cs`、`BeMusicSeeker.Tests/SelectedChartMutationWorkflowOwnerTests.cs`、`BeMusicSeeker.Tests/DuplicateMaintenanceWorkflowOwnerTests.cs`、`BeMusicSeeker.Tests/BmsLibraryFolderRenameRefreshTests.cs`、`BeMusicSeeker.Tests/LocalizationResourceParityTests.cs`
- `devdocs/spec/library-mutation-boundary.md`、`devdocs/spec/duplicate-file-check.md`（対応した terminal behavior と Verification map）

root は本計画を所有し、worker は変更しない。A は既存 callback・failure の順序と三つの caller に跨ぐ通知 ownership が正しさを左右するため frontier worker を選ぶ。A の Quick filter は packet の最終 coverage ledger に合わせ、上記の実際に触った fixture と localization parity に限定する。

### Unit B の final design / writable paths

A の stateless report を feature の終了処理から使い、表示 helper の複製や二つ目の報告者を作らない。自動 rename の completion／failure、drop install の batch completion、pending force/manual install の terminal は、通常の refresh／package 追加がない場合にも receipt を報告する。先行成功 item の表示・導入済み登録は既存契約に従い保持する。

model の receipt-backed 個別表示を抑止するのは、この unit で terminal reporting が接続された canonical route に限る。既存 package service の `showMessageBoxOnInstallFail` と同様の明示的 ownership を利用し、legacy caller や receipt のない事前拒否を無言にしない。新しい report の成否で completion、durable facts、後続 queue item の lifecycle を変更しない。

Worker B の許可 path（必要なものだけ、先行 A の変更を維持）:

- `BeMusicSeeker/ViewModels/MainWindow/FolderAutoRenameWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PackageInstallWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PendingPackageWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/ApplicationComposition.cs`、`BeMusicSeeker/ViewModels/MainWindowViewModel.cs`（上記 composition／terminal wiring のみ）
- `BeMusicSeeker/Views/MainWindowFeatureTerminals.cs`（pending package mutation terminal の結果提示のみ）
- `BeMusicSeeker/Models/BMSLibrary.cs`、`BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`、`BeMusicSeeker/Models/BMSLibrary.LibraryFileOperationOwner.cs`（上記の receipt 表示 ownership の伝播のみ）
- `BeMusicSeeker/Models/BmsLibraryInternal/AutoRenameBatchCoordinator.cs`、`BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs`（上記の旧 receipt-backed 通知のみ）
- A の `FileDbMutationReport*.cs`（B の既存 receipt facts に必要な overload／operation label のみ。A の契約を変更しない）
- `BeMusicSeeker.Tests/FolderAutoRenameWorkflowOwnerTests.cs`、`BeMusicSeeker.Tests/PackageInstallWorkflowOwnerTests.cs`、`BeMusicSeeker.Tests/PendingPackageWorkflowOwnerTests.cs`、`BeMusicSeeker.Tests/MainWindowPendingPackageMutationViewTerminalTests.cs`、`BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs`
- `BeMusicSeeker.Tests/MainWindowFileDbMutationConsumerTests.cs`（既存 fixture で composition の公開 completion 経路を閉じられない場合だけ新設）、`BeMusicSeeker.Tests/MainWindowViewModelTestFactory.cs`（上記に必要な recording dialog wiring のみ）
- `BeMusicSeeker.Tests/BmsLibraryFolderRenameRefreshTests.cs`（B のモデル companion）、`BeMusicSeeker.Tests/MainWindowPackageMaintenanceWpfTests.cs`（保留導入四経路の既存 harness を使った wiring 確認）
- A と同じ UI resource 8 files および `LocalizationResourceParityTests.cs`（新しい label が必要な場合だけ同じ prefix）
- `devdocs/spec/library-mutation-boundary.md`、`devdocs/spec/install-estimation-current-logic.md`（対応済み behavior／Verification map）

B は複数の非同期 batch completion と dialog ownership の正しさが必要なため frontier worker を選ぶ。A 完了後に同じ presenter API を受け取り、packet B を承認して実装する。独自の retry／failure fallback は追加しない。

### Unit C の final design / writable paths

削除固有の小さな immutable result に、確認済み FS 対象／件数、未実行・stale・unresolved・FS failure の対象、catalog apply の到達段階・durable fact・failure を保持する。`FileDbMutationCommitResult` の既存判定を使うが、callback を含む commit result 自体を terminal へ持ち出さず、確認済み fact を取り出す。

選択削除と重複削除はこの結果を返して、feature owner が outer release 後に一回報告する。重複削除の件数は計画件数を流用せず、確認済み FS 削除の chart target 数とする。一般的な成功選択や維持処理へ、catalog failure を成功と伝えない。

導入先修正は、先行 repair delta が commit 済みでも後段の削除が失敗し得る。catalog／必須反映が失敗した場合は、削除結果を持つ狭い typed exception でこの bridge の依存処理を停止し、`PendingPackageWorkflowOwner.FixInstalledLocationsAsync` の outer gate 解放後にその exception だけを捕捉・報告してよい。個別 FS failure だけの場合の既存継続方針は維持し、その結果も最後に報告する。選択・重複への不要な例外化や global App handler の変更はしない。削除失敗を扱った後で一般エラー／終了ダイアログへ再送出しない。無関係な例外の既存 handling は維持する。

repair の通常 return でも、削除を行った場合の outcome を library／store から feature owner へ返す。これにより FS-only の部分失敗も報告できる。削除を行わない場合は outcome なしとし、新しい汎用 repair result hierarchy は作らない。

報告は削除固有の facts を扱い、A の bounded 表示・diagnostic mechanics を必要に応じて共有する。receipt に偽の source/destination や durable state を詰めて削除結果を代用しない。primary catalog error と確認済み FS 成功、FS failure の独立した facts は最重 severity で隠さない。

Worker C の許可 path（必要なものだけ）:

- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryLibraryFileOperationsService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/LibraryChartRemovalOutcome.cs`（削除固有 result／必要な bridge exception の新規 file）
- `BeMusicSeeker/Models/BMSLibrary.LibraryFileOperationOwner.cs`、`BeMusicSeeker/Models/BMSLibrary.cs`（削除・導入先修正の bridge のみ）
- `BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs`、`BeMusicSeeker/ViewModels/MainWindow/DuplicateMaintenanceWorkflowOwner.cs`、`BeMusicSeeker/ViewModels/MainWindow/PendingPackageWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/MainWindow/LibraryChartRemovalReport*.cs`（削除表示の新規 file）、A の `FileDbMutationReport*.cs`（bounded 表示の mechanics を共有する場合だけ）
- `BeMusicSeeker/Views/MainWindow.cs`、`BeMusicSeeker/Views/MainWindowFeatureTerminals.cs`（対象結果の terminal wiring が必要な場合だけ。domain decision は追加しない）
- `BeMusicSeeker.Tests/OwnedChartCollectionLibraryMutationTests.cs`、`BeMusicSeeker.Tests/OwnedChartCollectionTestSupport.cs`、`BeMusicSeeker.Tests/SelectedChartMutationWorkflowOwnerTests.cs`、`BeMusicSeeker.Tests/DuplicateMaintenanceWorkflowOwnerTests.cs`、`BeMusicSeeker.Tests/PendingPackageWorkflowOwnerTests.cs`、`BeMusicSeeker.Tests/BmsLibraryDuplicateServiceTests.cs`、`BeMusicSeeker.Tests/LibraryChartRemovalReportTests.cs`（packet が新表示の独立 coverage を必要とした場合）
- `BeMusicSeeker.Tests/BmsLibraryFolderRenameRefreshTests.cs`（既存の導入先修正 bridge fixture の拡張）
- A と同じ UI resource 8 files および `LocalizationResourceParityTests.cs`（`LibraryChartRemovalReport_` prefix の新 key）
- `devdocs/spec/library-mutation-boundary.md`、`devdocs/spec/duplicate-file-check.md`（対応済み behavior／Verification map）

C は FS 結果、catalog durable/finalization fact、repair bridge の unwind が正しさを左右するため frontier worker を選ぶ。現在 executor と既存 SQLite gateway の入口で失敗を再現し、旧 service の private/direct-only test を根拠にしない。LR2 mode で削除済み chart の directory が pruning 対象になるため、実 library deletion と `folder` DELETE abort trigger で postcommit failure を検証できる。

## 確認済みの後続 unit

### Unit A の検証証跡

- Packet `FSDB-A-20260905` A01–A07。最終 filtered Quick は 116/116 成功、build + test 37.6 秒、test execution 12.47 秒。artifact: `artifacts/verification/tests-quick-20260905-174630/functional/results.trx`。
- Base red は non-durable batch の成功扱い、任意 observer failure による receipt 消失の 2 failures。同 run の fingerprint mismatch は root の計画編集との干渉であり assertion red と分離する。artifact: `tests-quick-20260905-172547/functional/results.trx`。
- cleanup severity、表示上限、rename の旧個別通知抑止を崩す代表 mutant は 4 intended failures / 7 cases。すべて復元し、最終 Quick の fingerprint は不変。artifact: `tests-quick-20260905-174512/functional/results.trx`。
- 正常無通知、実 LR2 postcommit failure、実 DB abort + 補償 failure、canonical/compatibility と preflight、解放後一回表示、任意通知 failure、全言語 parity を検証。timeout なし。統合 Functional は B/C 完了後。


### ライブラリ削除の部分結果の伝達

- **Production route:** `SelectedChartMutationWorkflowOwner.DeleteAsync` → store → `BMSLibrary.RemoveLibraryCharts` → `LibraryFileOperationOwner.RemoveLibraryChartsCore` → `ExecuteLibraryChartRemovalAfterAdmission` → `ExecuteLibraryChartRemovalPlan` → catalog apply。duplicate maintenance からも同じ library removal owner を呼ぶ。
- **現行 evidence:** FS executor は削除呼出しが成功した target だけを結果へ入れ、存在しない path は skip する。owner はその後で delta を反映する。`ApplyLibraryMutationDeltaUnderExistingReservation` が failure を throw すると、後ろに置かれた削除件数・個別 failure の通知登録と post-lease 通知呼出しへ到達しない。outer workflow の一般的な failure は返るが、FS／DB の部分結果が十分に保持されない。
- **Gap:** `FSDB-FACTS`／`FSDB-REPORT` に対し、FS 変更と DB／内部反映の failure の違いを caller が保持できる形へ整理する必要がある。再起動や同じ削除 command の再実行で解消済みになるとは約束できない。
- **Ownership:** 後続 unit は deletion executor、library file operation owner、canonical deletion caller／UI terminal までを一つの縦の範囲とする。DB commit の判定は既存 catalog owner の result を使い、別 writer を作らない。
- **Done when:** 捕捉した削除部分結果・DB failure・commit 後の内部反映 failure が必要な caller／UI まで区別して伝わる。利用者に実在しない自動復旧や安全未確認の retry を案内せず、success-only effect を誤って実行しない。
- **実装前の確認:** directory の部分削除を既存 gateway でどこまで確定できるか、存在しない path の扱い、実際に案内可能な再読込／手動対応、更新する UI resource と既存 fixture を対象 route に限定して決める。未確認の状態を救うための全件再走査・新しい永続状態は既定にしない。
- **対象外:** 削除ファイルの復元、汎用的な自動再整合、journal、0 件保護の撤去、全ての欠落 DB 行をこの unit で必ず収束させること。

### 既存 receipt の UI consumer

- **確認範囲:** `FileDbMutationBoundary.cs`、導入・move・merge・rename の直接の caller を静的に確認した。receipt があることだけでは `FSDB-REPORT` の利用者向け表示まで対応済みとは判断しない。
- **現行 evidence:** merge の `Views/MainWindow.cs` の completion 処理は cleanup failure／recovery paths をログへ出し、その後で通常の完了ログへ進む。手動 rename の `ViewModels/MainWindow/RegularChartListOwner.cs` の completion 処理は non-durable／finalization failure を判定するが、その箇所では cleanup-only failure を分けて扱わない。
- **後続範囲:** 対象 feature を変更するときに、completion から実際の画面表示までを確認し、必要なら既存 receipt の情報を terminal UI へ渡す。削除と同じ result 型への一括統合や、executor の補償撤去は要求しない。
- **Done when:** その feature で捕捉した要確認の結果を、利用者が通常完了と取り違えない。観測結果と安全に利用できる次の対応を保持し、任意通知の failure だけで durable success を取り消さない。

### 既存の共通 executor を今後変更するときの適用

`FileDbMutationBoundary.cs` の receipt 前の限定補償と、receipt 後に rollback しない契約は現行仕様として維持する。これを前方回復の共通方針に合わせるためだけに削除する unit は設けない。導入、folder move、merge、自動 rename の各 caller を将来変更するときに、既存 receipt の消失や失敗の成功扱いが実際にあれば、その feature unit 内で対応する。

`BmsLibraryInitializationService` の空走査保護による非収束は許容済みの制限であり、未修正 bug の一覧に含めない。

## 検証と記録

今回の文書変更は、参照先、UTF-8／LF、whitespace、`git diff --check` と fresh static review を対象とする。runtime test の assertion semantics は変更しない。後続実装の完了時は実施 evidence を本資料に残し、恒久的な挙動は該当 feature spec へ統合する。
