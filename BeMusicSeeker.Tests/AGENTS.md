# BeMusicSeeker.Tests Codex instructions

この directory 以下でテストを追加、変更、削除する場合、root `AGENTS.md` に加えて `../devdocs/spec/test-authoring-contract.md` と `../devdocs/spec/testing-strategy.md` を先に読む。ここにはテスト作業で毎回必要な差分だけを置く。

## Before editing

- 対象 behavior ごとに、production owner / symbol、candidate existing fixture、`extend / replace / new`、shared resource / lane、completion signal、退役 test を表にする。
- feature spec、production symbol の test 参照、feature 用語、failure 文言の順で候補を絞り、canonical fixture と共通 helper を先に確認する。
- final plan に test delta 表がない、または shared resource / completion signal が不明な場合は、編集前に `NEEDS_ROOT_INPUT` を返す。repository で確認できる候補は自分で調べる。
- canonical fixture を新設、移動、分割する場合は、対象 feature spec の `Verification map` を同じ unit で更新する。

## Local safety boundaries

- observable behavior、persisted state、failure、cancel、threading、shutdown、cleanup を優先し、置換した旧 test / helper / route は同じ unit で退役させる。例外的な source artifact、reflection、test-only seam は `test-authoring-contract.md` の理由・退役条件を記録する。
- WPF application / dispatcher / presentation は `TestUiDispatcherHost` と `TestWindowPresentationScope`、dispatcher 上の task 待ちは `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使う。直接 `Dispatcher.PushFrame` / `HwndSource` を所有するのは、同契約に定めた共通 infrastructure または明示的な例外だけとする。
- test / fixture から physical OS cursor を操作・観測しない（`GetCursorPos`、`SetCursorPos`、`Mouse.GetPosition` による physical cursor 位置の判定を含む）。key / routed event、explicit hit、deterministic fake / typed action seam を使う。
- 正常完了は対象の `Task`、event、state transition に結び付く signal で待ち、local timeout は failure watchdog として使う。`DoNotParallelize` は分離不能な shared resource、owner、復元処理を説明できる場合だけ使う。
- process test は bounded wait / stream drain、owned PID lineage cleanup、diagnostics、primary failure precedence を一つの lifecycle owner へ閉じる。process 名だけの global kill は禁止する。

## Verification and handoff

- 反復中は変更 behavior に対応する filtered `Quick` を使う。Functional / Full と timeout 時の扱いは `testing-strategy.md` に従い、統合 owner へ引き渡す。
- 完了時は通常の worker handoff に加えて、`TEST COVERAGE` と `TEST SAFETY` を返す。検索した candidate fixture、`extend / replace / new`、退役 test、shared resource、lane / shard、completion signal、例外 seam、実行 filter を含める。
