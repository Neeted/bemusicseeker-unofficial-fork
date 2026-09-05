# FS+DB 整合性契約の適用計画

最終更新: 2026-09-05

Status: Planned（共通方針を仕様化済み。下記の production 対応は未着手）

## 目的と範囲

共通契約の正本は [file-db-consistency.md](../spec/file-db-consistency.md)。本資料は確認済みの実装差分と、後続 unit の切り方だけを記録する。全 FS+DB route を監査済みとする記録ではない。

今回の変更は Markdown のみ。FS の順序、既存 compensation、DB transaction、UI result、空走査保護、テストを変更しない。後続実装では対象 unit の decision list と独立した Test Contract Packet を先に確定する。

## 確認済みの後続 unit

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
