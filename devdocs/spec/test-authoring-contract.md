# テスト実装・既存 coverage 確認契約

最終更新: 2026-08-23

この文書は、BeMusicSeeker でテストを追加・変更・削除するときの実装契約である。`testing-strategy.md` は lane、時間予算、shard、共有 resource の正本、この文書は既存 coverage の調べ方、テストの形、例外的 seam、Codex handoff の正本とする。機能固有の observable behavior は各 feature spec を正とする。

目的はテスト数を増やすことではなく、既存 coverage との重複を避けながら、observable behavior、failure、threading、cleanup を最小の決定的なテストで保証することである。

## 1. 編集前の coverage reconnaissance

新しい fixture や test method を書く前に、対象 behavior ごとに次の ledger を plan または worker notes へ作る。

| Behavior / failure contract | Production owner / symbol | Candidate existing coverage | Decision | Shared resource / lane | Completion signal | Retired test |
| --- | --- | --- | --- | --- | --- | --- |
| | | | `extend` / `replace` / `new` | | | |

`Decision` の意味は次のとおり。

- `extend`: canonical な既存 fixture へ case を追加する。
- `replace`: 脆い、または重複した旧テストを、同じ invariant を保つ behavior / semantic test へ置換し、旧テストを同じ unit で削除する。
- `new`: 既存 fixture へ置くと owner、resource、lane、failure contract が混ざるため、新しい fixture を作る。理由を ledger に残す。

調査順は次を既定とし、最初から test project 全体を通読しない。

1. 対象 feature の `devdocs/spec` と、その `Verification map` があれば読む。
2. production owner、public contract、result 型、event 名を `rg` で `BeMusicSeeker.Tests` から検索する。
3. feature 用語、既知の failure 文言、旧 route 名で候補を絞る。
4. candidate fixture と、直接利用する共通 helper だけを読む。
5. candidate が見つからない、または ownership が横断的な場合だけ検索範囲を広げる。

広域読み取りが必要になった場合は、何を検索して不足したかを handoff へ一行残す。単に「既存テストが多い」ことを理由に新しい fixture を作らない。

canonical fixture を新設、移動、分割した場合は、対象 feature spec へ短い `## Verification map` を追加または更新し、behavior、fixture、lane、例外的 shared resource を記録する。巨大な global 一覧を人手で重複管理せず、feature spec を入口にして近傍 coverage へ到達できる状態を保つ。

## 2. Test shape の優先順位

テストは次の順に実現可能性を検討する。

1. **Behavior contract**: public / internal owner 境界から observable result、persisted state、notification、failure、cancel、cleanup を検証する。
2. **Semantic / compiled structure contract**: behavior だけでは保証できない thread affinity、interface 実装、XAML materialization、compiled symbol / operation を検証する。
3. **Source artifact contract**: source generator input、resource key、build / release script、禁止 route など、source artifact そのものが契約である場合に限定する。
4. **Legacy absence contract**: 廃止 route が再導入されないことが migration 完了条件の場合に限定する。

source text、private reflection、method body 文字列、行順の assertion は、便利だからという理由では選ばない。使用する場合は coverage ledger または近傍 comment に次を残す。

- artifact / placement 自体がなぜ contract なのか
- behavior / compiled semantic test では検出できない理由
- owner と退役条件
- broad helper で repository 全体を読み込まず、対象 artifact を最小範囲に限定していること

`SourceTextTestHelper` のような汎用 source 抽出基盤を再導入しない。source-artifact test の増加は review で明示的に扱う。

## 3. Existing infrastructure first

新しい local helper を書く前に、同じ resource や wait を所有する共通 test infrastructure を検索する。

- WPF application / dispatcher / window presentation: `TestUiDispatcherHost`、`TestWindowPresentationScope`
- dispatcher 上の task 完了待ち: `TestUiDispatcherHost.AwaitTaskOnDispatcher`
- process-global cursor: `TestProcessGlobalCursorScope`
- test data: `TestBmsFactory` と既存の feature fixture builder
- runner / distribution: production script の実行 seam。テスト側に同じ orchestration をコピーしない

共通 helper の契約が不足する場合は local copy を作らず、owner helper へ最小の拡張を行い、その契約を focused test で閉じる。

### WPF / native primitive

- fixture へ新しい直接 `Dispatcher.PushFrame` を追加しない。有限 watchdog と明示的 completion signal を所有する共通 infrastructure 内だけで使う。
- fixture へ新しい直接 `HwndSourceParameters` / `HwndSource` を追加しない。既存の presentation helper へ寄せる。避けられない場合は offscreen、nonactivating、deterministic disposal、HWND residual check を同じ owner で閉じる。
- production `MainWindow.Show`、任意の foreground activation、physical cursor 操作を Functional へ追加しない。必要性が observable contract なら `testing-strategy.md` の明示 allowlist と lane を同じ変更で更新する。

### Process primitive

process を起動する test / runner は、次を一つの lifecycle として設計する。

- bounded process wait
- bounded stdout / stderr drain
- runner / test が所有する PID lineage だけの停止と残留確認
- diagnostic artifact の保存
- primary failure を cleanup failure で上書きしない優先順位

process 名だけでマシン全体の `dotnet` / `testhost` / `vstest` を停止しない。production execution seam をテストし、metadata や test-side copy だけを green にしない。

## 4. Async, completion, and flake safety

- 正常完了は `TaskCompletionSource`、event、barrier、channel、fake scheduler / clock など、対象 state transition に結び付く signal で待つ。
- timeout は failure watchdog であり、正常完了の推定には使わない。
- fixed `Thread.Sleep` や成功を待つための正の `Task.Delay` を追加しない。
- task fault、cancel、dispatcher shutdown、cleanup failure を握りつぶさない。
- `DoNotParallelize` は分離できない shared resource が実在する場合だけ使い、resource owner と復元処理を comment または spec へ書く。
- shard / worker 低下や timeout 延長だけで flake を消した扱いにしない。

runner、lane、parallelization、fixture placement、shared WPF / process infrastructure を変更した場合、最終 snapshot で Functional を3回連続実行する。途中で failure を修正した場合、修正前の pass を数えず1回目からやり直す。

一度でも再発した flake は、単発 timeout の retry rule だけで閉じない。active / last observed test、shared state、process / window / pipe handle、settings、temp resource、worker topology を failure ledger へ残し、対象 filter を実際の shard context で反復する。反復回数と topology variation は plan で先に定め、成功回数の後付け積み増しをしない。

## 5. Plan and worker handoff

テストを触る unit の final plan には、次を含める。

- 上記 coverage ledger
- canonical fixture を `extend / replace / new` のどれにするか
- 削除する旧 test / helper / route と replacement
- shared mutable resource、lane / shard、`DoNotParallelize` 判断
- normal completion signal と failure watchdog
- focused Quick filter、必要な repeat、Functional / Full / opt-in lane
- source text、private reflection、raw WPF / process primitive を使う場合の例外理由

worker は完了時に、少なくとも次を handoff する。

```text
## TEST COVERAGE
- searched symbols / candidate fixtures
- extend / replace / new decision and reason
- retired tests and replacements

## TEST SAFETY
- shared resources, lane / shard, DoNotParallelize decision
- completion signal and failure watchdog
- exceptional source / reflection / WPF / process seams or none
- anti-pattern scan result

## VERIFICATION
- exact commands / filters / repetitions
- elapsed, artifacts, timeout retry evidence
- not-run items and reason
```

## 6. Review contract

reviewer はテスト変更がある場合、green result だけでなく次を確認する。

- assertion が observable contract を検証しているか。単に現在の method body / metadata / placement を固定していないか
- actual executable seam を通っているか。test fixture が production logic をコピーしていないか
- canonical existing fixture を無視した重複 test / local helper が増えていないか
- failure、cancel、retry / reentry、shutdown、cleanup が正常系と同じ owner で閉じているか
- raw dispatcher pump、visible HWND、physical cursor、process、settings、filesystem などの shared resource が documented policy に従うか
- fixed wait、追加 DNP、worker 低下、timeout 延長で不安定性を隠していないか
- source-artifact / legacy-absence test に理由と退役条件があるか
- test 移動後に旧 coverage が重複して残らず、feature spec の Verification map が current か

Recommendations と blocking finding を分離し、既存の例外を一括整理するために現在の bounded unit を無制限に広げない。
