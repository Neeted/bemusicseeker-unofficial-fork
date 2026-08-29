# テスト実装・既存 coverage 確認契約

最終更新: 2026-08-27

この文書は、BeMusicSeeker でテストを追加・変更・削除するときの実装契約である。`testing-strategy.md` は lane、時間予算、shard、共有 resource の正本、この文書は test oracle の authority、独立設計、既存 coverage の調べ方、テストの形、例外的 seam、Codex handoff の正本とする。機能固有の observable behavior は各 feature spec を正とする。

目的はテスト数を増やすことではなく、実装から独立した contract を、既存 coverage と重複しない最小の決定的なテストで保証することである。

## 1. Source of authority と Test Contract Packet

assertion、expected value、snapshot、golden、source / reflection contract の semantics を追加・変更・削除する unit では、実装前に `test-contract-designer` が作成し、ルートが承認した `Test Contract Packet` を用意する。名前変更、移動、format、生成物更新だけで assertion semantics が変わらない作業は例外とする。

期待値の authority として使えるものは、次のうち対象 behavior を明示しているものとする。

- ユーザー要件と decision list
- approved issue、bug report、再現手順、受入条件
- feature spec、public API contract、protocol、schema、file format
- 外部標準または互換対象の明示された仕様
- root が明示的に承認した characterization scope と baseline

次は coverage、placement、testability の evidence には使えるが、単独では期待値の authority にしない。

- current production implementation、private method body、current runtime output
- 既存 test の expected value、snapshot、golden file
- `Resources.resx`、`lang/*.json`、docs prose の現在の文字列
- current source text、symbol placement、private call order

`Test Contract Packet` は、implementation body や current output を読む前に oracle を凍結し、少なくとも次を含む。

| Contract ID | Observable behavior / failure | Authority | Public seam | Required invariant / outcome | Allowed variation | Plausible wrong implementation | Evidence strategy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| | | | | | | | |

- `Allowed variation` には、翻訳文言、順序、format、内部構造、timing など、変更してよいものを明記する。
- `Evidence strategy` は、bugfix なら原則 base-fail / head-pass とする。base で test を構造上実行できない場合は理由と targeted mutant / negative control を示す。
- `characterization` は正しさの証明ではない。current behavior を authority にする root decision、凍結対象、利用目的、退役条件を packet に明記する。
- exact string、snapshot、source artifact、private reflection は detail 自体が contract である authority、owner、退役条件が packet にある場合だけ使う。

packet 承認後、worker は fixture、helper、data setup、assertion API などの mechanics を repository に適合させてよいが、authority、expected outcome、allowed variation、wrong implementation を current implementation に合わせて変更してはいけない。packet を保ったまま解消できる可能性がある技術的な seam / ownership 不足は workflow の resolver trigger に従う。authority や expected semantics の変更が必要なら `NEEDS_ROOT_INPUT` を返す。

## 2. 編集前の coverage reconnaissance

新しい fixture や test method を書く前に、packet の Contract ID ごとに次の ledger を plan または worker notes へ作る。

| Contract ID | Production owner / symbol | Candidate existing coverage | Decision | Shared resource / lane | Completion signal | Retired test |
| --- | --- | --- | --- | --- | --- | --- |
| | | | `extend` / `replace` / `new` | | | |

`Decision` の意味は次のとおり。

- `extend`: canonical な既存 fixture へ case を追加する。
- `replace`: 脆い、または重複した旧テストを、同じ Contract ID を守る behavior / semantic test へ置換し、旧テストを同じ unit で削除する。
- `new`: 既存 fixture へ置くと owner、resource、lane、failure contract が混ざるため、新しい fixture を作る。理由を ledger に残す。

調査順は次を既定とし、最初から test project 全体を通読しない。

1. 対象 feature の `devdocs/spec` と、その `Verification map` があれば読む。
2. production owner、public contract、result 型、event 名を `rg` で `BeMusicSeeker.Tests` から検索する。
3. feature 用語、既知の failure 文言、旧 route 名で候補を絞る。
4. candidate fixture と、直接利用する共通 helper だけを読む。
5. candidate が見つからない、または ownership が横断的な場合だけ検索範囲を広げる。

既存 test は coverage / placement の evidence であり、packet の oracle を上書きする authority ではない。広域読み取りが必要になった場合は、何を検索して不足したかを handoff へ一行残す。単に「既存テストが多い」ことを理由に新しい fixture を作らない。

canonical fixture を新設、移動、分割した場合は、対象 feature spec へ短い `## Verification map` を追加または更新し、Contract ID、behavior、fixture、lane、例外的 shared resource を記録する。巨大な global 一覧を人手で重複管理せず、feature spec を入口にして近傍 coverage へ到達できる状態を保つ。

## 3. Test shape の優先順位

テストは次の順に実現可能性を検討する。

1. **Behavior contract**: public / internal owner 境界から observable result、persisted state、notification、failure、cancel、cleanup を検証する。
2. **Semantic / compiled structure contract**: behavior だけでは保証できない thread affinity、interface 実装、XAML materialization、compiled symbol / operation を検証する。
3. **Source artifact contract**: source generator input、resource key、build / release script、禁止 route など、source artifact そのものが契約である場合に限定する。
4. **Legacy absence contract**: 廃止 route が再導入されないことが migration 完了条件の場合に限定する。
5. **Characterization contract**: 明示された behavior-preserving migration の安全網として限定し、仕様テストと区別する。

source text、private reflection、method body 文字列、行順、localized copy、docs prose、broad snapshot の assertion は、便利だから、または current output が取得できるからという理由では選ばない。使用する場合は packet と近傍 comment に次を残す。

- artifact / exact detail 自体がなぜ contract なのか
- behavior / compiled semantic test では検出できない理由
- authority、owner、退役条件
- broad helper で repository 全体を読み込まず、対象 artifact を最小範囲に限定していること

翻訳は通常、key parity、non-empty、placeholder、plural、fallback、format parse、rendering を検証し、各言語の文言を test code に完全複製しない。docs や source に関する整合性は、可能なら schema、generated artifact consistency、lint、compiled semantic contract として検証する。

`SourceTextTestHelper` のような汎用 source 抽出基盤を再導入しない。source-artifact test の増加は review で明示的に扱う。

## 4. Existing infrastructure first

新しい local helper を書く前に、同じ resource や wait を所有する共通 test infrastructure を検索する。

- WPF application / dispatcher / window presentation: `TestUiDispatcherHost`、`TestWindowPresentationScope`
- dispatcher 上の task 完了待ち: `TestUiDispatcherHost.AwaitTaskOnDispatcher`
- test data: `TestBmsFactory` と既存の feature fixture builder
- runner / distribution: production script の実行 seam。テスト側に同じ orchestration をコピーしない

共通 helper の契約が不足する場合は local copy を作らず、owner helper へ最小の拡張を行い、その契約を focused test で閉じる。

### WPF / native primitive

- fixture へ新しい直接 `Dispatcher.PushFrame` を追加しない。有限 watchdog と明示的 completion signal を所有する共通 infrastructure 内だけで使う。
- fixture へ新しい直接 `HwndSourceParameters` / `HwndSource` を追加しない。既存の presentation helper へ寄せる。避けられない場合は offscreen、nonactivating、deterministic disposal、HWND residual check を同じ owner で閉じる。
- test / fixture から physical OS cursor を操作・観測しない（`GetCursorPos`、`SetCursorPos`、`Mouse.GetPosition` による physical cursor 位置の判定を含む）。key / routed event、explicit hit、deterministic fake / typed action seam を使う。
- production `MainWindow.Show`、任意の foreground activation を Functional へ追加しない。必要性が observable contract なら `testing-strategy.md` の明示 allowlist と lane を同じ変更で更新する。

### Process primitive

process を起動する test / runner は、次を一つの lifecycle として設計する。

- bounded process wait
- bounded stdout / stderr drain
- runner / test が所有する PID lineage だけの停止と残留確認
- diagnostic artifact の保存
- primary failure を cleanup failure で上書きしない優先順位

process 名だけでマシン全体の `dotnet` / `testhost` / `vstest` を停止しない。production execution seam をテストし、metadata や test-side copy だけを green にしない。

## 5. Async, completion, and flake safety

- 正常完了は `TaskCompletionSource`、event、barrier、channel、fake scheduler / clock など、対象 state transition に結び付く signal で待つ。
- timeout は failure watchdog であり、正常完了の推定には使わない。
- fixed `Thread.Sleep` や成功を待つための正の `Task.Delay` を追加しない。
- task fault、cancel、dispatcher shutdown、cleanup failure を握りつぶさない。
- `DoNotParallelize` は分離できない shared resource が実在する場合だけ使い、resource owner と復元処理を comment または spec へ書く。
- shard / worker 低下や timeout 延長だけで flake を消した扱いにしない。
- 正常完了の coordinator は対象の `Task`、event、signal、state transition を plain `await` で待ち、`.Wait`、`.Result`、`GetAwaiter().GetResult()`、`WaitOne`、`SpinUntil` で同期 block しない。
- local bound は cleanup、external process、UI presentation、negative lock、timeout contract の failure watchdog に限る。固定 sleep、成功推定用の正の delay、既定 timeout helper、bulk な timeout 変更は追加しない。

runner、lane、parallelization、fixture placement、shared WPF / process infrastructure を変更した場合は、影響する focused Quick と acceptance lane を handoff へ明示する。Functional の実行回数、300秒 hard budget、180秒 reporting target、timeout retry、failure classification は `testing-strategy.md` に従う。180秒を超えて成功した場合は actual elapsed をユーザーへの報告に含める。

規定の retry 後も同じ症状が再発する flake は、active / last observed test、shared state、process / window / pipe handle、settings、temp resource、worker topology を failure ledger へ残し、対象 filter を実際の shard context で調査する。

## 6. Red evidence と negative control

- bugfix で既存 interface から再現できる場合は、production 修正前に focused regression test を作り、対象 bug の observable mismatch で失敗することを確認する。compile error、fixture setup failure、unrelated exception は red evidence にしない。
- new API など base で test を構造上実行できない場合は、その理由を packet と handoff に残す。実装後、packet の plausible wrong implementation を一時的な targeted mutant、fake、input variation で表現し、test が落とすことを確認する。
- behavior-preserving replacement や characterization test は base で green でもよいが、少なくとも packet が定める wrong variant / invariant violation を検出する evidence を残す。
- mutation score や coverage は診断値であり、それだけを completion signal にしない。changed decision logic に対応する少数の targeted negative control を優先する。

## 7. Plan and worker handoff

テストを触る unit の final plan には、次を含める。

- 承認済み `Test Contract Packet` と対象 Contract ID
- coverage ledger と `extend / replace / new` の判断
- 削除する旧 test / helper / route と replacement
- shared mutable resource、lane / shard、`DoNotParallelize` 判断
- normal completion signal と failure watchdog
- base-fail / head-pass、または targeted negative control の確認方法
- focused Quick filter、Functional / Full / opt-in lane
- source text、private reflection、exact string / snapshot、raw WPF / process primitive を使う場合の例外理由

worker は完了時に、少なくとも次を handoff する。

```text
## TEST CONTRACT
- packet ID and implemented Contract IDs
- red / head-pass or negative-control evidence
- deviations from packet or none

## TEST COVERAGE
- searched symbols / candidate fixtures
- extend / replace / new decision and reason
- retired tests and replacements

## TEST SAFETY
- shared resources, lane / shard, DoNotParallelize decision
- completion signal and failure watchdog
- exceptional source / reflection / exactness / WPF / process seams or none
- anti-pattern scan result

## VERIFICATION
- exact commands / filters / lanes
- elapsed, artifacts, timeout retry evidence
- not-run items and reason
```

packet からの deviation が必要になった場合は、worker が独自に採用せず、authority、observable impact、推奨変更を `NEEDS_ROOT_INPUT` として返す。

## 8. Review contract

reviewer はテスト変更がある場合、green result だけでなく次を確認する。

- assertion semantics が承認済み packet / Contract ID に対応し、authority、expected outcome、allowed variation と一致するか
- expected value が current implementation、current output、existing expected、translation copy、snapshot、repository prose から写経されていないか
- packet の plausible wrong implementation を test が実際に区別し、red / negative-control evidence が妥当か
- actual executable seam を通っているか。test fixture が production logic や期待値導出をコピーしていないか
- canonical existing fixture を無視した重複 test / local helper が増えていないか
- failure、cancel、retry / reentry、shutdown、cleanup が正常系と同じ owner で閉じているか
- raw dispatcher pump、visible HWND、physical cursor、process、settings、filesystem などの shared resource が documented policy に従うか
- fixed wait、追加 DNP、worker 低下、timeout 延長で不安定性を隠していないか
- exact string / snapshot / source-artifact / reflection / characterization / legacy-absence test に authority、owner、退役条件があるか
- test 移動後に旧 coverage が重複して残らず、feature spec の Verification map が current か

assertion semantics が変わるのに packet がない場合、または packet と diff が矛盾する場合は、明示された受入条件に対する test-design gap として扱う。Recommendations と blocking finding を分離し、既存の例外を一括整理するために現在の bounded unit を無制限に広げない。
