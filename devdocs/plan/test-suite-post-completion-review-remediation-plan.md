# テスト整理完了後レビュー P2 修正計画

Status: Ready for implementation

Review base: `30d25e792ec4b58c552db7651e8d615fe54c11d1`

Plan date: 2026-08-24

## Goal

完了済みの runner lifecycle 改善に残った4件の acceptance-direct P2 を、既存の絶対 cleanup deadline、Functional 180秒 command budget、runner-owned identity、primary failure precedence を維持したまま閉じる。

1. stdout / stderr の片側だけが完了した非対称 timeout でも、完了側の UTF-8 artifact を期限内に保存する。
2. deadline 後に fault した task observation を lifecycle owner ごとに分離し、後続 command / shard へ誤帰属させない。
3. `Process.Start()` 成功後の予期しない例外も同じ bounded ownership cleanup に合流させ、live process と既存 artifact を残さない・壊さない。
4. write / dispose 待機で発生した terminal diagnostic を final snapshot より前に確定し、成功した `process-lifecycle.log` から欠落させない。

併せて、完了計画の一覧表記と canonical lifecycle fixture の Verification map を現状へ合わせる。

## Context

- `Invoke-BoundedProcessLifecycle` は片側 stream が未完了のまま deadline を消費すると、完了側の値を回収しても persistence 全体の deadline gate により `stdout.log` / `stderr.log` を両方省略する。
- late observation は process-wide static queue へ入り、最後の drain 後に enqueue された observation を次 lifecycle が回収できる。
- `Invoke-MonitoredCommand` の outer catch は、process start 後でも bounded stop を行わず dispose だけを試み、既存 log を空文字で上書きし得る。
- `process-lifecycle.log` の payload は terminal write / dispose task を待つ前に snapshot されるため、待機中の fault / timeout と terminal-operation skip が artifactへ入らない。
- `devdocs/plan/README.md` は完了済み計画を現在進行中として掲載している。
- `VerificationProcessLifecycleTests` は canonical fixture だが、`devdocs/spec/testing-strategy.md` から fixture / lane / shared resource contract を直接辿れない。

## Constraints

- cleanup deadline を延長しない。deadline 到達後に blocking wait、reader close、native residual scan、filesystem write、dispose wait を新規開始しない。
- process 名による global kill、意味の変わる fallback、PIDだけの ownership 判定を追加しない。
- primary process / orchestration failure を cleanup / persistence failure で置換しない。primary がない cleanup-only failure は成功に隠さない。
- production lifecycle を test 側へ複製しない。actual shared seam / caller を guarded deterministic probe から通す。
- runner、lane、worker topology、Functional 180秒 budget、WPF fixture の production behavior は変更しない。
- fixed sleep、timeout延長、retry、追加 `DoNotParallelize` を使わない。

## Decision list

- 3件はすべて現行仕様へ直接反する P2 であり、修正対象とする。
- lifecycle-local observation owner を正本とし、PID filter付き global queue は採用しない。
- timeout 時の durable owner diagnostic は deadline 内に確定できる `stream-drain-timeout` とする。deadline 後の exact task exception は必ず観測して unobserved fault を防ぐが、別 lifecycle の result / failure へ追加しない。
- terminal persistence のために絶対 deadline 内の reserve / cutoff を明示し、完了 stream とその時点までの diagnostics を未完了 stream から独立して保存する。deadline 後の書込み許可は採用しない。
- post-start fault injection は test-only guarded seam から actual `Invoke-MonitoredCommand` owner を通し、通常 runner から到達不能にする。
- fault injection seam は既定無効の内部 parameter / guard と deterministic signal を使い、通常 CLI routeや単なる環境変数だけでは有効化できない構造にする。
- final diagnostic flush は terminal operations とscope seal後に最後のI/Oとして一度だけ行う。cutoff前に開始したflushのfailureはowner secondary diagnosticとして返しprimaryを置換しない。cutoff後は開始せず、`terminal-diagnostic-flush` skipをresultのsecondary diagnosticに残す。sink自身が失敗した場合はartifactではなくresult/caller failure reportingを正本とし、既存artifactは空書きしない。
- 既存 canonical fixtureを `extend` し、新 fixture / lane / DNP は追加しない。

## Unit 1: Close lifecycle persistence, observation ownership, and post-start cleanup

Owner: `implementation-worker`（single sequential owner）

Writable paths:

- `scripts/verification-process-lifecycle.ps1`
- `scripts/verify-refactor.ps1`
- `scripts/test-fixtures/verification-process-lifecycle-probe.ps1`
- 必要な既存 `scripts/test-fixtures/` 内 fixture 1件まで
- `BeMusicSeeker.Tests/VerificationProcessLifecycleTests.cs`
- `devdocs/spec/testing-strategy.md`
- `devdocs/plan/README.md`
- 本計画書

### Observable outcomes

1. 各 stream は独立に完了状態と値を snapshot され、他方が timeout しても完了側の log が UTF-8 で保存される。
2. stream / terminal task observation は一意な lifecycle scope だけへ publishされる。scope seal 後の fault は観測されるが、後続 lifecycle の secondary diagnostics、artifact、failureへ入らない。
3. stdout / stderr persistence、reader / process disposalをterminal-operation cutoff前に完了またはskip確定し、scopeをsealして最後にdrainする。final diagnostic flush reserve内に確定snapshotを一度だけ書き、write / dispose待機中に生成された diagnosticもartifactまたはresultへ帰属する。
4. process start 後の例外は retained handle、PID、取得済み identity、stream taskを持つ同じ cleanup ownerへ合流する。exact owned root/descendantをboundedに停止し、元例外をprimaryとして維持する。
5. outer catch は既存 artifact を空文字で上書きしない。completed outputを保存し、未取得側とcleanup failureをcontext付きsecondary diagnosticにする。
6. `testing-strategy.md` に lifecycle fixture / ProcessIntegration lane / shared process resource / completion signalの Verification mapを記録し、完了済み旧計画をREADMEの現在進行中一覧から外す。

### Test delta

| Behavior / failure contract | Production owner / symbol | Candidate coverage | Decision | Shared resource / lane | Completion signal | Retired route |
| --- | --- | --- | --- | --- | --- | --- |
| normal completed outputの実artifact | `Invoke-BoundedProcessLifecycle` persistence | `VerificationProcessLifecycleTests.NormalExitPreservesCompletedStandardOutputAndError` | extend | exact-ledger probe process / `ProcessIntegration` | both stream tasks completed + log files read before probe cleanup | result JSONだけのassert |
| completed stdout + pending stderr と逆方向 | stream drain / terminal persistence | existing stream-timeout case | extend | same | completed task + guarded pending/fault gate + absolute deadline | 両stream同時timeoutだけのcoverage |
| lifecycle Aのlate faultがclean lifecycle Bへ混入しない | lifecycle observation scope | existing late-fault / fanout cases | extend | same PowerShell probe process / `ProcessIntegration` | A return → controlled fault signal → B completion | global queueのmanual drainをowner contractとするroute |
| post-start unexpected exception cleanup | `Invoke-MonitoredCommand` actual caller | existing probe-failure cleanup case | extend | exact-owned process / `ProcessIntegration` | guarded injection signal + exact ledger PID absence | outer harness cleanupだけでrunner残留を隠すcoverage |
| terminal operation中のdiagnosticとfinal flush failure | terminal operation cutoff / `process-lifecycle.log` | existing stream-timeout / cleanup diagnostic cases | extend | same | controlled write/dispose fault or cutoff signal + result/artifact read before cleanup | terminal wait前のlog payload snapshot |
| Functional shared deadlineの非resetとcutoff後primitive禁止 | `Invoke-VerificationFunctionalCleanup` | existing fanout-order / deadline cases | extend | multiple exact-ledger entries / `ProcessIntegration` | primitive event ledger with shared deadline and entry lifecycle IDs | entry単位の自己申告metadataだけのassert |

### Focused verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~VerificationRunnerContractTests|FullyQualifiedName~VerificationProcessLifecycleTests'
```

Worker は PowerShell parse、`git diff --check`、focused Quick まで行い、Functional / Full / WPF反復はrootへhandoffする。

### Replan triggers

- absolute deadline内のterminal persistence reserveでは既存 Functional cleanupを閉じられず、global cleanup budget / orchestration shapeの変更が必要になる。
- retained root handleからidentity取得前のbounded root-only stopを安全に行えない。
- actual caller fault injectionが通常 runnerから到達不能な明示guardでは構成できない。
- lifecycle-local scopeではdeadline後のfault observationとdurable diagnostic ownershipを両立できず、run-level owner追加が必要になる。

## Integration and final review

Unit 1をcommit後、同一最終snapshotで次を実行する。途中でfailureを修正した場合は、該当stability gateを1回目から数え直す。

1. PowerShell parse、`git diff --check`、Release build、runner hash。
2. lifecycle focused Quick。
3. 旧計画指定のWPF focused filterを30回。各回でtimeout、tracked file変更、残留test processがないことを確認する。
4. Functionalを3回連続。各command 180秒以内、tracked fingerprint不変、残留test process 0。
5. Fullを1回。
6. implementation threadを閉じ、snapshotを凍結してfresh `repo-static-review`を呼ぶ。

Reviewer は asymmetric persistence、scope seal後のfault、actual post-start caller exception、artifact非破壊、primary/secondary precedence、deadline後のprimitive開始、test自己検証を重点確認する。

## Done when

- 4件のacceptance-direct P2がbehavior testとdurable specを伴って解消される。
- completed stream artifactとlifecycle diagnosticが他streamのtimeoutに失われない。
- late faultが別lifecycleへ混入せず、元ownerのtimeout diagnosticとfault observation contractが明確である。
- post-start exception後のexact-owned PID residualが0で、既存artifactとprimary failureが維持される。
- terminal operation中に確定したdiagnosticが成功したfinal logに含まれ、final flush失敗時はresult/callerのsecondary failureとして観測される。
- WPF 30回、Functional 3回、Full 1回が同一最終snapshotで成功する。
- fresh static reviewでP0/P1とacceptanceへ直接反するP2がない。
- verification evidence、review result、最終commitを本計画へ記録し、StatusをCompleteにする。
