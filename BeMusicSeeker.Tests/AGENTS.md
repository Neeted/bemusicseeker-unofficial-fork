# BeMusicSeeker.Tests Codex instructions

この directory 以下でテストを追加、変更、削除する場合、root `AGENTS.md` に加えて `../devdocs/spec/test-authoring-contract.md` と `../devdocs/spec/testing-strategy.md` を先に読む。ここにはテスト作業で毎回必要な差分だけを置く。

## Test Contract Packet gate

- durable test の assertion、expected value、snapshot、golden、source / reflection contract を追加・変更・削除する場合は、編集前にルートが承認した `Test Contract Packet` と対象 Contract ID を受け取る。名前変更、移動、format、生成物更新だけで assertion semantics が変わらない場合は例外とする。
- packet は user requirement、approved issue、feature spec、public API / protocol / schema、明示的な characterization decision など、実装から独立した authority を示す。current implementation、current runtime output、既存 test の expected value、翻訳ファイルの現在値、repository prose は単独では authority にしない。
- worker は packet の expected outcome、allowed variation、wrong implementation を変更しない。fixture、helper、data setup、assertion API などの mechanics は適合させてよい。技術的な seam / ownership 不足は workflow の resolver trigger に従い、authority や expected semantics の変更が必要な場合は、green にするため assertion を弱めず `NEEDS_ROOT_INPUT` を返す。
- exact localized copy、docs prose、source text、private symbol、method body、broad snapshot を固定する test は、detail 自体が contract である理由、owner、退役条件が packet に明示されている場合だけ追加する。通常は key parity、placeholder / plural / fallback、schema、public behavior、generated artifact consistency を検証する。
- characterization test は正しさの証明と混ぜず、packet で凍結対象、current behavior を authority にする root decision、退役条件を明記する。

## Before editing

- 対象 Contract ID ごとに、production owner / symbol、candidate existing fixture、`extend / replace / new`、shared resource / lane、completion signal、退役 test を表にする。
- feature spec、production symbol の test 参照、feature 用語、failure 文言の順で候補を絞り、canonical fixture と共通 helper を先に確認する。既存 test は coverage placement の evidence であり、packet の oracle を上書きする authority ではない。
- final plan に承認済み packet / Contract ID、coverage ledger、shared resource / completion signal がない場合は、編集前に `NEEDS_ROOT_INPUT` を返す。repository で確認できる candidate fixture と helper は自分で調べる。
- canonical fixture を新設、移動、分割する場合は、対象 feature spec の `Verification map` を同じ unit で更新する。
- bugfix で既存 interface から再現できる場合は production 修正前に regression test を書き、意図した理由で失敗する red evidence を残す。base 実行が構造上不可能、または behavior-preserving replacement の場合は packet の targeted mutant / negative control を使う。

## Local safety boundaries

- observable behavior、persisted state、failure、cancel、threading、shutdown、cleanup を優先し、置換した旧 test / helper / route は同じ unit で退役させる。例外的な source artifact、reflection、test-only seam は `test-authoring-contract.md` の理由・退役条件を記録する。
- production logic、期待値の導出、runner orchestration を test 側へコピーしない。private call order、current output、translation copy、source placement に合わせて green になる assertion を作らない。
- WPF application / dispatcher / presentation は `TestUiDispatcherHost` と `TestWindowPresentationScope`、dispatcher 上の task 待ちは `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使う。直接 `Dispatcher.PushFrame` / `HwndSource` を所有するのは、同契約に定めた共通 infrastructure または明示的な例外だけとする。
- test / fixture から physical OS cursor を操作・観測しない（`GetCursorPos`、`SetCursorPos`、`Mouse.GetPosition` による physical cursor 位置の判定を含む）。key / routed event、explicit hit、deterministic fake / typed action seam を使う。
- 正常完了は対象の `Task`、event、state transition に結び付く signal で待ち、local timeout は failure watchdog として使う。`DoNotParallelize` は分離不能な shared resource、owner、復元処理を説明できる場合だけ使う。
- process test は bounded wait / stream drain、owned PID lineage cleanup、diagnostics、primary failure precedence を一つの lifecycle owner へ閉じる。process 名だけの global kill は禁止する。

## Verification and handoff

- 反復中は変更 behavior に対応する filtered `Quick` を使う。Functional / Full と timeout 時の扱いは `testing-strategy.md` に従い、統合 owner へ引き渡す。
- 完了時は通常の worker handoff に加えて、`TEST CONTRACT`、`TEST COVERAGE`、`TEST SAFETY` を返す。実装した Contract ID、red / negative-control evidence、packet からの deviation または `none`、検索した candidate fixture、`extend / replace / new`、退役 test、shared resource、lane / shard、completion signal、例外 seam、実行 filter を含める。

## Code Review Rules

### Test authority and independence

- assertion semantics が変わる diff に承認済み `Test Contract Packet` / Contract ID がない、または test が packet の authority・allowed variation と一致しない場合は指摘する。
- expected value が production implementation、current output、既存 expected、翻訳文言、snapshot、repository prose から写経され、独立 authority がない場合は指摘する。
- exact string / snapshot / source / reflection / characterization の例外に authority、owner、退役条件がない場合は指摘する。
- packet が列挙した plausible wrong implementation を通してしまう assertion、または red / negative-control evidence の欠落を指摘する。
- harmless refactor、翻訳改善、文言変更、内部配置変更で壊れる一方、observable contract を追加で守らない test を指摘する。
