# BeMusicSeeker.Tests Codex instructions

この directory 以下でテストを追加、変更、削除する場合、root `AGENTS.md` に加えて `../devdocs/spec/test-authoring-contract.md` と `../devdocs/spec/testing-strategy.md` を先に読む。ここにはテスト作業で毎回必要な差分だけを置く。

## Before editing

- いきなり新しい fixture を作らない。対象 behavior ごとに、production owner / symbol、candidate existing fixture、`extend / replace / new`、shared resource / lane、completion signal、退役 test を表にする。
- feature spec、production symbol の test 参照、feature 用語、failure 文言の順で `rg` し、candidate file へ絞ってから読む。最初から test project 全体を通読しない。
- final plan に test delta 表がない、または既存 coverage 候補と shared resource が不明な場合は、編集前に `NEEDS_ROOT_INPUT` を返す。repository で検索できる候補は自分で調べる。
- canonical fixture を新設、移動、分割する場合は、対象 feature spec の `Verification map` を同じ unit で更新する。

## Test design

- observable behavior、persisted state、failure、cancel、threading、shutdown、cleanup を優先する。現在の private method body、呼出順、配置、metadata だけを固定しない。
- canonical な既存 fixture を拡張できるなら拡張する。新規 fixture は owner、failure contract、lane、resource lifecycle を既存 fixture と分離する必要がある場合だけ作る。
- 置換した旧 test、local helper、legacy route は同じ unit で削除し、replacement を handoff へ示す。
- source text、private reflection、method body 抽出は例外である。artifact 自体が contract である理由、behavior / compiled semantic test で代替できない理由、退役条件を記録し、汎用 source 抽出 helper を作らない。
- 新しい test-only public API、service locator、broad host、production fallback を追加しない。

## Shared infrastructure and waits

- WPF application / dispatcher / presentation は `TestUiDispatcherHost` と `TestWindowPresentationScope` を使う。fixture に新しい直接 `Dispatcher.PushFrame` を追加しない。
- dispatcher 上の task 待ちは `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使い、識別可能な operation name を渡す。local pump を複製しない。
- fixture に新しい直接 `HwndSource` / `HwndSourceParameters` を追加しない。共通 presentation policy へ寄せ、例外は offscreen / nonactivating / deterministic cleanup を明示する。
- fixed `Thread.Sleep`、正常完了を推定する正の `Task.Delay`、busy wait を追加しない。deterministic signal と短い failure watchdog を使う。
- `DoNotParallelize` は分離不能な shared resource が実在するときだけ使い、resource owner、復元、Quick を含む必要性を comment または spec へ残す。
- process test は bounded process wait、bounded stream drain、owned PID lineage cleanup、diagnostics、primary failure precedence を一つの owner へ閉じる。process 名だけの global kill を行わない。

## Verification and handoff

- 反復中は変更 behavior に対応する filtered Quick を使い、worker が Functional / Full を重複実行しない。
- test infrastructure、fixture placement、lane、parallelization を変えた場合、root が最終 snapshot で Functional を3回連続実行する。途中修正後は1回目から数え直す。
- 完了時は通常の worker handoff に加えて、`TEST COVERAGE` と `TEST SAFETY` を返す。検索した candidate fixture、`extend / replace / new`、退役 test、shared resource、lane / shard、completion signal、例外 seam、実行 filter を含める。
- 触れた file について、少なくとも新規の `Dispatcher.PushFrame`、`HwndSourceParameters`、`Thread.Sleep`、正常完了用 `Task.Delay`、理由のない `DoNotParallelize`、production `.cs` の広域 `File.ReadAllText` が増えていないか確認する。
