# テスト整理後 Blocking Findings 修正計画

Status: Ready for implementation

Review base: `7835539a21091b47c67dc13866ad4fcdae759ccc`

Plan date: 2026-08-23

## Codex への実行指示

`AGENTS.md` と `devdocs/spec/codex-agent-workflow.md` に従って Unit 0 から実行する。ルートが設計、計画、統合、最終検証を所有し、実装は bounded unit ごとに `implementation-worker` へ渡す。

- review base は blocking findings が確認された production / test snapshot である。開始時の implementation file が review base と異なる場合は、該当差分だけを再監査し、finding が既に解消済みか、line / symbol / test delta を更新すべきか判断してから編集する。
- Unit 0 の `plan-clarifier` は不明点、test coverage、並列境界の点検だけを行う。repository で解けない observable semantics または replan trigger がない限り、計画を作り直したりユーザー質問を増やしたりしない。
- Unit 1 と Unit 2 は同一 worktree では逐次実行する。隔離 worktree、独立した output root、独立した verification がそろう場合だけ並列化する。

## Goal

テスト整理後レビューで確認された次の2件を、Functional の180秒 command budget と既存の failure contract を維持したまま解消する。

1. `scripts/verify-refactor.ps1` が process timeout / failure 後に redirected stdout / stderr の EOF を無期限に待ち、runner 自身が cleanup reserve を超えて停止し得る。
2. 4つの compiled WPF fixture が、watchdog なしのローカル `Dispatcher.PushFrame` helper で shutdown task を待ち、testhost 全体を runner timeout まで停止させ得る。

完了時には、同じ種類の無期限待機を再導入しにくい実装、テスト、レビュー境界も同じ変更で閉じる。

## Context / repository evidence

### Runner

- `scripts/verify-refactor.ps1:627-649` の `Invoke-MonitoredCommand` は、process 終了後に `ReadToEndAsync()` task へ無期限の `GetResult()` を行う。
- `scripts/verify-refactor.ps1:1557-1564` の Functional shard 回収も、同じ無期限待機を行う。
- Functional には10秒の cleanup reserve と7秒の process cleanup deadline があるが、stream drain はその境界を消費せずに待てる。
- `BeMusicSeeker.Tests/VerificationRunnerContractTests.cs:175-239` は、別 process を読む側では5秒の stream timeout を既に採用している。ただし、runner 本体の実行経路は検証していない。
- cleanup で確認、停止する対象は、runner が起動した PID lineage だけとする。マシン上の無関係な `dotnet` / `testhost` / `vstest` を process 名だけで kill してはならない。

### WPF cleanup

次の fixture にローカル `AwaitOnDispatcher` と watchdog なしの `Dispatcher.PushFrame` がある。

- `BeMusicSeeker.Tests/MainWindowPackageMaintenanceWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowProgressStatusBarWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowTreePresentationWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowViewHostTests.cs`

共通の `TestUiDispatcherHost.AwaitTaskOnDispatcher(Task, string)` は `BeMusicSeeker.Tests/TestUiScheduler.cs:197-269` にあり、5秒の failure watchdog、task fault 伝播、diagnostic operation name を既に持つ。

## Constraints

- production behavior、test assertions、Functional shard membership、worker 数、180秒 budget を変更しない。
- timeout 値の延長、Functional の並列度低下、追加の `DoNotParallelize` で症状を隠さない。
- primary failure の優先順位を維持する。process timeout、nonzero exit、launch / orchestration failure がある場合、stream drain や cleanup failure で置き換えず secondary diagnostics として残す。
- process が成功しても、stream drain または runner-owned process cleanup が境界内に完了しなければ成功扱いにせず、infrastructure cleanup failure とする。
- incomplete な `Task<string>` に対して `.Result` / `GetAwaiter().GetResult()` を呼ばない。取得できた completed stream だけを保存し、未完了 stream は明示的な diagnostic を残す。
- runner lifecycle test は、production helper または production execution seam そのものを通す。テスト側に同等ロジックをコピーしたり、自己申告 metadata だけを検証したりしない。
- WPF fixture 側で新しい dispatcher pump を再実装しない。共通 helper の契約が不足する場合だけ `TestUiDispatcherHost` を拡張する。
- 無関係な P2 整理、raw `HwndSource` 全件移行、runner operation plan の全面再設計、既存 source-artifact test の一括移行は、この計画の対象外とする。

## Decision list

次の semantics は確定済みであり、worker が再判断しない。

1. **Stream drain timeout**: process timeout とは独立した bounded cleanup failure である。Functional では残っている10秒 cleanup reserve 内、通常 monitored command では明示した最大5秒以内で完了させる。
2. **Output preservation**: timeout 時も completed な stdout / stderr は UTF-8 log へ保存する。未完了側には `stream-drain-timeout` と対象 stream 名、process PID、経過時間を diagnostic へ残す。
3. **Failure precedence**: primary process / orchestration failure を throw する前に cleanup diagnostics を保存する。cleanup だけが失敗した成功 process では cleanup failure を primary にする。
4. **Process ownership**: cleanup と残留確認は、runner が起動した root PID と追跡できた descendant PID だけを対象にする。global process-name scan による kill は禁止する。
5. **WPF wait**: affected fixture は `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使い、operation ごとに識別可能な名前を渡す。ローカル `AwaitOnDispatcher` は削除する。
6. **Final stability gate**: 最終 snapshot で Functional を3回連続実行する。途中で failure を修正した場合、それ以前の pass は数えず1回目からやり直す。

## Test delta table

| Behavior / failure contract | Existing coverage | Action | Shared resource / lane | Completion signal | Retired route |
| --- | --- | --- | --- | --- | --- |
| process が終了し、stdout / stderr も閉じた通常経路 | runner 本体の直接 contract なし | production lifecycle seam を通る contract test を追加 | `ProcessIntegration` | process exit + 両 stream task 完了 | 無期限 `GetResult()` |
| child が redirected handle を保持し、root process だけが終了する経路 | なし | inherited-handle probe で bounded drain と diagnostic を検証 | `ProcessIntegration`、runner-owned PID lineage | stream deadline または child cleanup 完了 | EOF 待ちによる runner hang |
| process timeout / nonzero exit と stream cleanup failure が併発 | 部分的な process timeout contract のみ | primary failure 保持と secondary cleanup diagnostic を検証 | `ProcessIntegration` | primary error + diagnostic artifact | cleanup failure による primary 上書き |
| Functional shard cleanup 後に runner-owned process が残らない | 文書上の要求のみ | tracked PID lineage の bounded residual check を実装、検証 | Functional runner | tracked PID 不在 | top-level process だけの確認 |
| MainWindow shutdown task の dispatcher 待機 | 4 fixture に unbounded local helper | 共通 watchdog helper へ置換 | `compiled-wpf-classwide` / 1 worker | task completion または5秒 watchdog failure | 4つの local `AwaitOnDispatcher` |
| dispatcher wait の fault / watchdog diagnostic | 共通 helper 実装のみ | helper を変更する場合だけ focused contract test を追加 | shared WPF host | fault 伝播、operation name 入り timeout | fixture ごとの pump 実装 |

## Unit 0: Baseline freeze and plan clarification

Owner: root agent

Writes: この plan の Progress / Verification Log だけ

1. `git status --short` と `git rev-parse HEAD` を記録し、review base 以外の未コミット差分を識別する。
2. `plan-clarifier` にこの文書、review findings、上記 test delta を一度渡す。
3. repository から解けない observable semantics が新たに見つからない限り、ユーザー質問は増やさない。
4. Unit 1 / 2 の path ownership と、runner-owned descendant を識別する Windows API / process seam を確定する。

Replan triggers:

- runner-owned descendant を無関係な process と区別できず、global process scan が必要になる。
- actual runner seam を通す deterministic inherited-handle probe を作れず、production code の大規模分割が必要になる。
- 共通 WPF helper が affected task の dispatcher / shutdown contract を満たさない。

## Unit 1: Bound runner process and stream lifecycle

Owner: `implementation-worker`

Default execution: Unit 2 の前後どちらかで逐次実行する。隔離 worktree があり、generated output と verification を共有しない場合だけ Unit 2 と並列化してよい。

Writable paths:

- `scripts/verify-refactor.ps1`
- 必要なら新しい `scripts/verification-process-lifecycle.ps1`
- 必要なら新しい `scripts/test-fixtures/*`
- `BeMusicSeeker.Tests/VerificationRunnerContractTests.cs`、または責務を分けた新しい runner lifecycle test file
- runner lifecycle contract を変えた場合の `devdocs/spec/testing-strategy.md`

Read-only references:

- `scripts/verification-runner-contract.ps1`
- `BeMusicSeeker.Tests/DistributionArtifactContractTests.cs`
- `devdocs/spec/test-authoring-contract.md`

### Required implementation

1. stdout / stderr task を一つの shared bounded-drain seam で回収し、`Invoke-MonitoredCommand` と Functional shard cleanup の両方から使用する。
2. helper は deadline / remaining cleanup budget、stream tasks、PID、diagnostics directory を受け、completed output と cleanup diagnostics を構造化して返す。
3. 両 task が deadline 内に完了した場合だけ結果を同期取得する。faulted task は例外内容を cleanup diagnostic にする。
4. deadline を超えた場合は completed 側だけを保存し、未完了側を明記する。redirected reader を閉じるか cancellation / continuation で未完了 task の最終例外を観測し、放置した task が後から primary failure を置き換えないようにする。process object の dispose も同じ deadline 内で行う。
5. Functional では process cleanup、fallback helper cleanup、stream drain、tracked descendant 確認を同じ10秒 cleanup reserve 内で配分する。個別に10秒ずつ追加して global budget を破らない。
6. runner-owned root / descendant PID を追跡し、cleanup deadline 後に残っていれば PID と command identity を diagnostic へ残して failure にする。無関係な process は停止しない。
7. primary failure、cleanup failure、diagnostic write failure の優先順位を一箇所で明示し、既存の primary failure 文言と diagnostics path を保つ。

### Required tests

production seam を直接通す deterministic probe で、少なくとも次を確認する。

- normal exit: stdout / stderr が完全に保存され、cleanup failure がない。
- root exit + descendant inherited handle 保持: runner / test probe が bounded time で戻り、`stream-drain-timeout` または tracked descendant cleanup の diagnostic を持つ。
- timeout / nonzero exit + stream cleanup failure: primary failure 種別が維持され、secondary diagnostic が失われない。
- cleanup 完了後: probe が起動した root / descendant PID が残らない。

テストのために production lifecycle 処理を複製してはならない。private function を直接呼べない場合の推奨は、lifecycle helper だけを別 script へ抽出し、runner と probe の双方が source する形である。test-only bypass を追加する場合は、明示 env guard がない通常 runner から到達不能にする。

### Focused verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests|FullyQualifiedName~VerificationProcessLifecycleTests'
```

`VerificationRunnerContractTests` と新しい lifecycle test は `ProcessIntegration` のまま明示 filter 付き Quick で実行する。通常 Functional では除外されることを runner contract でも確認する。

### Handoff

- production process lifecycle seam と caller 一覧
- primary / cleanup failure matrix
- probe が使用した root / descendant PID と最終残留確認
- focused command、elapsed、artifact path
- test delta の `extend / replace / new` 結果

## Unit 2: Replace unbounded WPF fixture pumps

Owner: `implementation-worker`

Writable paths:

- `BeMusicSeeker.Tests/MainWindowPackageMaintenanceWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowProgressStatusBarWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowTreePresentationWpfTests.cs`
- `BeMusicSeeker.Tests/MainWindowViewHostTests.cs`
- 共通 helper の契約変更が必要な場合だけ `BeMusicSeeker.Tests/TestUiScheduler.cs`
- 共通 helper test が必要な場合だけ `BeMusicSeeker.Tests/WpfTestApplicationHostTests.cs`

### Required implementation

1. 各 `AwaitOnDispatcher` call を `TestUiDispatcherHost.AwaitTaskOnDispatcher` へ置換する。
2. operation name は fixture / operation を識別できる固定文字列にする。複数 task を待つ `MainWindowViewHostTests` は別名を使う。
3. 4つの private helper を削除し、不要になった `DispatcherFrame` / `DispatcherPriority` / dispatcher parameter / using を整理する。
4. task fault、cancel、shutdown request の既存 semantics を変更しない。
5. 共通 helper を変更せずに済むなら変更しない。不足がある場合は既定5秒 watchdog を維持し、テストだけ短い watchdog を注入できる internal seam を検討する。通常完了に delay を追加しない。

### Required checks

```powershell
rg -n 'private static void AwaitOnDispatcher|Dispatcher\.PushFrame' `
  .\BeMusicSeeker.Tests\MainWindowPackageMaintenanceWpfTests.cs `
  .\BeMusicSeeker.Tests\MainWindowProgressStatusBarWpfTests.cs `
  .\BeMusicSeeker.Tests\MainWindowTreePresentationWpfTests.cs `
  .\BeMusicSeeker.Tests\MainWindowViewHostTests.cs
```

上記は match 0 を受入条件とする。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~MainWindowPackageMaintenanceWpfTests|FullyQualifiedName~MainWindowProgressStatusBarWpfTests|FullyQualifiedName~MainWindowTreePresentationWpfTests|FullyQualifiedName~MainWindowViewHostTests|FullyQualifiedName~WpfTestApplicationHostTests'
```

既知の低頻度停止を確認するため、統合後に同じ filter を30回反復する。各回で timeout、残留 test process、tracked file 変更がないことを記録する。1回でも failure があれば成功回数を積み増さず root cause を修正し、反復を1回目からやり直す。

### Handoff

- 削除した4 helper と replacement call の対応
- operation name 一覧
- affected fixture の lane / shared resource / `DoNotParallelize` が不変であること
- focused / repeated verification 結果

## Unit 3: Integration, final verification, and fresh review

Owner: root agent

Writes: mechanical integration conflict と plan log だけ

1. Unit 1 / 2 の handoff、path ownership、test delta を確認する。
2. `git diff --check`、PowerShell parse、C# build を行う。
3. Unit 1 / 2 の focused Quick / ProcessIntegration を統合 snapshot で一度実行する。
4. WPF focused filter を30回反復する。failure 修正後は1回目から数え直す。
5. runner 変更のため、同じ最終 snapshot、同じ command、同じ budget で Functional を3回連続実行する。各回について elapsed、run root、tracked fingerprint、runner-owned residual PID を記録する。途中修正後は3回をやり直す。
6. Full を1回実行する。Full failure を単なる Functional pass で代替しない。
7. 実装 thread を閉じ、snapshot を凍結して `repo-static-review` を呼ぶ。review 中に root は repository 操作を行わない。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

Reviewer へ渡す重点:

- incomplete stream task への unbounded wait が全 caller から除去されたか
- process cleanup / stream drain / residual check が global cleanup reserve を超えないか
- primary failure が cleanup failure で上書きされないか
- actual runner seam をテストしているか。metadata が metadata 自身をテストする構造になっていないか
- runner-owned process だけを停止し、無関係な process を巻き込まないか
- 4 fixture の local dispatcher pump が完全に削除され、共通 watchdog へ接続されたか
- helper の local 再実装、固定 sleep、追加 DNP、worker 低下で問題を隠していないか

## Done when

- `Invoke-MonitoredCommand` と Functional shard 回収に unbounded stream `GetResult()` が残っていない。
- stream drain と runner-owned process cleanup は bounded で、diagnostic と failure precedence がテストされている。
- affected 4 fixture に private `AwaitOnDispatcher` と直接 `Dispatcher.PushFrame` が残っていない。
- focused WPF filter 30回、Functional 3回連続、Full 1回が同一の最終 snapshot で成功している。
- Functional 各回が180秒以内、tracked files 不変、runner-owned residual process なしである。
- fresh static review で P0 / P1 と acceptance へ直接反する P2 がない。
- verification evidence に HEAD、worktree diff hash、runner hash、各 run root / elapsed / result が記録されている。

## Progress

| Unit | Status | Notes |
| --- | --- | --- |
| Unit 0: Baseline freeze and clarification | Not started | |
| Unit 1: Runner process and stream lifecycle | Not started | |
| Unit 2: WPF dispatcher cleanup | Not started | |
| Unit 3: Integration and final review | Not started | |

## Verification log

| Snapshot | Command / filter | Result | Elapsed | Artifact / residual-process evidence |
| --- | --- | --- | ---: | --- |
| | | | | |
