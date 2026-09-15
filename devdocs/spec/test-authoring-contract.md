# テスト設計と既存テストの利用

最終更新: 2026-09-16

この文書は、BeMusicSeeker でテストを追加・変更・削除するときの設計契約である。実行区分・時間予算・共有資源は [テスト運用方針](testing-strategy.md)、委譲と報告は [エージェント運用契約](codex-agent-workflow.md) を正本とし、この文書は判定基準の根拠、独立設計、既存テストの調査と配置を定める。機能の挙動は各仕様を正とする。共通語は [運用契約の用語表](codex-agent-workflow.md#用語) を参照する。

目的はテスト数を増やすことではなく、実装から独立した contract を、既存 coverage と重複しない最小の決定的なテストで保証することである。

## 1. 変更分類と恒久テストの必要性

変更分類は file 名ではなく目的と影響で決める。複数の目的が混在する変更は、部分ごとに分類して判断する。root は計画または作業説明に、継続して保証する契約、既存 coverage の不足、保守負担に見合う効果を数行で残し、恒久テストの扱いを `変更なし`、`既存更新`、`追加`、`削除`（組合せ可）から選ぶ。

| 変更分類 | 恒久テストの判断 |
| --- | --- |
| 不具合修正 | 原則として回帰 coverage を確保する。既存 test の活用・更新で足りる場合は新設しない。承認済み production ingress から再現できる回帰 test では、red は原則有用だが、実施要否は必要性と識別力の具体的なリスクで決める。 |
| 新しいユーザー仕様・安定契約 | 継続して保証する価値があり、既存 coverage で不足する場合に追加または更新する。旧実装が新仕様に合わないことだけを理由に、旧実装の red を必須にしない。 |
| refactor・運用・build・検証順序・内部構成 | 原則として新規追加しない。既存 test の意味変更・削除が必要かは別途判断し、red は一律に要求しない。 |

回帰テストは、実在する不具合の再発または確立した契約の再破壊を防ぐテストを指す。意図的な仕様変更で旧仕様を否定するためだけに恒久テストを追加しない。今回新しく決めた仕様を旧実装が満たさないことは、それだけでは不具合や回帰の証拠にならない。旧 route が存在しないことも、廃止した事実だけでは固定せず、再導入が具体的な契約違反になる場合に限って扱う。

必要性判断の結果、テストが不要でも検証が不要になるわけではない。既存の関連 test、適切な実行確認、静的検査から変更に合う手段を選ぶ。この判断自体に新しい巨大な packet、台帳、ユーザー承認手続きを追加しない。削除のみの判断では代替を要求せず、保持すべき契約への影響を確認する。

必要性の再評価は、追加・意味変更・置換が必要になった部分に限る。

## 2. 仕様の根拠とテスト設計書

必要性判断で恒久テストの追加、assertion semantics の変更、または置換が必要になった unit では、実装前に `test-contract-designer` が作成し、ルートが承認した `Test Contract Packet` を用意する。削除のみで代替不要と判断済みの場合は、その理由を記録して packet 不要とする。名前変更、移動、format、生成物更新などの mechanical change で assertion semantics が変わらない場合も packet 不要である。

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

テスト設計書（`Test Contract Packet`）では、実装本体や現在の出力を読む前に判定基準を確定する。作業名・変更分類と、項目ごとの次の内容を揃える。

| 項目 | 挙動・失敗 | 仕様の根拠 | 本番の入口・到達経路・入口の前提 | 必須の条件・結果 | 許容差分 | 検出すべき誤実装 | 確認方法 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| | | | | | | | |

`Packet ID` は作業名、`Contract ID` はその設計書内の項目を指す一時的な参照名であり、意味の分かる名前を基本とする。有効範囲は進行中の計画・テスト設計書・担当間の引継ぎに限る。番号を使う場合も作業内だけに留め、別担当へは対象の計画・見出し・版を渡す。保証の根拠は参照名ではなく、各項目が参照する要件・仕様・承認済み判断にある。

計画の段階番号・作業番号や Packet ID / Contract ID を、恒久仕様の見出し・仕様 ID、テストのクラス名・メソッド名・属性・ケース ID・表示名・コメントへ転記しない。テストは機能・入力条件・期待結果を表す名前にし、必要な根拠は現行仕様の項目で示す。計画とテストメソッドの一時的な対応は設計書の配置表や引継ぎで示し、コード側に作業履歴用のメタデータを増やさない。外部標準や現行のデータ契約が定める識別子・番号は、この一時名とは区別する。

完了時は [仕様書の書式](README.md#仕様書の書式) に従い、現在も必要な契約と実装・テストの対応だけを仕様へ統合する。計画と作業専用の設計書・配置表は [計画の運用](../plan/README.md#運用) に従って削除し、作業番号の別名や旧新対応表を恒久資料へ残さない。既存の一時名を整理するときは、検証対象の契約・期待値を変えずに、対応する仕様参照や test filter も同じ変更で更新する。

- 「許容差分」には、翻訳文言、順序、書式、内部構造、タイミングなど、変更してよいものを明記する。
- 「確認方法」では、必要性判断で回帰テストを追加・更新するとした不具合修正について、本番の入口から再現できるなら修正前の失敗確認（red）を原則有用とする。変更前の版で実行できないことだけでは代替確認を必須にせず、失敗再現の代替または識別力に具体的な懸念がある場合に、その理由と確認方法を示す。
- `characterization` は正しさの証明ではない。current behavior を authority にする root decision、凍結対象、利用目的、退役条件を packet に明記する。
- exact string、snapshot、source artifact、private reflection は detail 自体が contract である authority、owner、退役条件が packet にある場合だけ使う。

runtime Contract ID は、canonical production ingress から target state を経て user-observable behavior または durable / external-data impact へ至る route を示す。supported public API は ingress になり得るが、public / private symbol の直接呼出し、reflection、fake が任意状態を生成できること、code 上の representability だけは reachability evidence ではない。承認済み新機能で ingress 自体を追加する場合は、final plan がその route と impact を authority として示す。

process-exclusive DB、single writer、外部 filesystem / index の out-of-process mutation 可否、snapshot / refresh boundary などは、authority と owner entrance の assumption として packet に記録する。fake はその assumption 内の production surface を表現するために使い、assumption 違反を downstream recovery の新契約へ変換しない。invalid input は、承認済みの entrance prevention / explicit rejection を検証する negative control として扱える。

packet 承認後、worker は fixture、helper、data setup、assertion API などの mechanics を repository に適合させてよいが、authority、expected outcome、allowed variation、wrong implementation を current implementation に合わせて変更してはいけない。packet を保ったまま解消できる可能性がある技術的な seam / ownership 不足は workflow の resolver trigger に従う。authority や expected semantics の変更が必要なら `NEEDS_ROOT_INPUT` を返す。

## 3. 既存テストの調査と配置

恒久テストの追加・判定内容の意味変更・置換が必要な場合、実装前に設計書の項目ごとに次の配置表を作る。進行中の計画、テスト設計書、担当間の引継ぎのいずれかで共有する。

| 設計書の項目 | 実装の管理主体・識別子 | 既存テストの候補 | 追加・置換の配置 | 共有資源・実行区分 | 完了の待ち方 | 退役するテスト・補助処理・経路 |
| --- | --- | --- | --- | --- | --- | --- |
| | | | `extend` / `replace` / `new` | | | |

`必要な追加・置換の配置` の意味は次のとおり。これは必要性判断の下位にある配置判断であり、すべての変更に求めるものではない。

- `extend`: canonical な既存 fixture へ case を追加する。
- `replace`: 脆い、または重複した旧テストを、同じ Contract ID を守る behavior / semantic test へ置換し、旧テストを同じ unit で削除する。
- `new`: 既存のテスト構成に置くと管理主体、共有資源、実行区分、失敗時の契約が混ざるため、新しい構成を作る。配置表に理由を添える。

調査順は次を既定とし、最初から test project 全体を通読しない。

1. 対象機能の仕様と「実装とテストの対応」（既存の `Verification map` を含む）を読む。
2. production owner、public contract、result 型、event 名を `rg` で `BeMusicSeeker.Tests` から検索する。
3. feature 用語、既知の failure 文言、旧 route 名で候補を絞る。
4. candidate fixture と、直接利用する共通 helper だけを読む。
5. candidate が見つからない、または ownership が横断的な場合だけ検索範囲を広げる。

既存テストは検証範囲と配置の判断材料であり、期待値を上書きする根拠にはしない。検索で不足した範囲と採用した配置の理由を、引継ぎで簡潔に伝える。

仕様とコード・テストの対応が変わった場合は、[仕様書の書式](README.md#仕様書の書式) に従って、対象機能の対応表を同じ変更で更新する。機能仕様から実装とテストメソッドへ辿れる状態を保ち、実行区分や共有資源の詳細はその正本を参照する。

## 4. Test shape の優先順位

テストは次の順に実現可能性を検討する。

1. **Behavior contract**: 実 UI / command / event / startup / scheduler / owner / supported public ingress から到達する observable result、persisted state、notification、failure、cancel、cleanup を検証する。internal owner や test seam を直接呼べるだけでは product contract にしない。
2. **Semantic / compiled structure contract**: behavior だけでは保証できない thread affinity、interface 実装、XAML materialization、compiled symbol / operation を検証する。
3. **Source artifact contract**: source generator input、resource key、build / release script、禁止 route など、source artifact そのものが契約である場合に限定する。
4. **Legacy absence contract**: 廃止 route の再導入が具体的な契約違反となり、migration 完了条件である場合に限定する。廃止したこと自体だけでは固定しない。
5. **Characterization contract**: 明示された behavior-preserving migration の安全網として限定し、仕様テストと区別する。

source text、private reflection、method body 文字列、行順、localized copy、docs prose、broad snapshot の assertion は、便利だから、または current output が取得できるからという理由では選ばない。使用する場合は packet と近傍 comment に次を残す。

private API、reflection、fake でしか作れない runtime state は downstream recovery の behavior test にしない。earliest owned ingress の invariant / rejection、または到達可能な contract に対する wrong implementation の negative control である場合だけ使用する。

- artifact / exact detail 自体がなぜ contract なのか
- behavior / compiled semantic test では検出できない理由
- authority、owner、退役条件
- broad helper で repository 全体を読み込まず、対象 artifact を最小範囲に限定していること

翻訳は通常、key parity、non-empty、placeholder、plural、fallback、format parse、rendering を検証し、各言語の文言を test code に完全複製しない。docs や source に関する整合性は、可能なら schema、generated artifact consistency、lint、compiled semantic contract として検証する。

`SourceTextTestHelper` のような汎用 source 抽出基盤を再導入しない。source-artifact test の増加は review で明示的に扱う。

## 5. Existing infrastructure first

新しい local helper を書く前に、同じ resource や wait を所有する共通 test infrastructure を検索する。

- WPF application / dispatcher / window presentation: `TestUiDispatcherHost`、`TestWindowPresentationScope`
- dispatcher 上の task 完了待ち: `TestUiDispatcherHost.AwaitTaskOnDispatcher`
- test data: `TestBmsFactory` と既存の feature fixture builder
- runner / distribution: production script の実行 seam。テスト側に同じ orchestration をコピーしない
- Everything / OS scan と app-managed physical output: test ごとに immutable な captured surface を明示して渡す。live Everything service / index や、test-created file が即時に発見されることを fixture の前提にしない。共有 fake の incomplete default、service 有無 conditional、reflection、sleep / retry で surface を補完しない。production-shaped test factory は native bridge が存在しない application snapshot を使い、local Everything service を composition から隔離する。これは bridge failure を fallback success に読み替える仕組みではなく、consumer の scan 契約は explicit surface を受け取る owner fixture で検証する。

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

## 6. Async, completion, and flake safety

- 正常完了は `TaskCompletionSource`、event、barrier、channel、fake scheduler / clock など、対象 state transition に結び付く signal で待つ。
- timeout は failure watchdog であり、正常完了の推定には使わない。
- fixed `Thread.Sleep` や成功を待つための正の `Task.Delay` を追加しない。
- task fault、cancel、dispatcher shutdown、cleanup failure を握りつぶさない。
- `DoNotParallelize` は分離できない shared resource が実在する場合だけ使い、resource owner と復元処理を comment または spec へ書く。
- shard / worker 低下や timeout 延長だけで flake を消した扱いにしない。
- 正常完了の coordinator は対象の `Task`、event、signal、state transition を plain `await` で待ち、`.Wait`、`.Result`、`GetAwaiter().GetResult()`、`WaitOne`、`SpinUntil` で同期 block しない。
- local bound は cleanup、external process、UI presentation、negative lock、timeout contract の failure watchdog に限る。固定 sleep、成功推定用の正の delay、既定 timeout helper、bulk な timeout 変更は追加しない。

runner、lane、parallelization、fixture placement、shared WPF / process infrastructure を変更した場合は、変更の検証に用いる focused Quick または適切な実行確認と acceptance lane を handoff へ明示する。Functional の実行回数、300秒 hard budget、180秒 reporting target、timeout retry、failure classification は `testing-strategy.md` に従う。180秒を超えて成功した場合は actual elapsed をユーザーへの報告に含める。

規定の再実行後も同じ症状が再発する場合は、実行中または最後に確認できたテスト、共有状態、プロセス・ウィンドウ・パイプ、設定、一時資源、並列構成を調査担当へ引き継ぎ、実際の分割実行と同じ条件で調査する。

## 7. Red evidence と negative control

- 必要性判断で回帰 test を追加・更新するとした bugfix が承認済み production ingress から再現できる場合、production 修正前の focused regression test による red は原則有用である。実施要否は計画で必要性と識別力の具体的なリスクを踏まえて決め、compile error、fixture setup failure、unrelated exception は red evidence にしない。
- 必要性判断で test を追加・更新・置換するとした unit で base の test が構造上実行できない場合でも、それだけでは targeted mutant / negative control を要求しない。bugfix の red の代替、または test の識別力に対する具体的なリスクがあり、計画で必要と判断した場合だけ、その理由を packet と handoff に残し、packet の plausible wrong implementation を targeted mutant、fake、input variation で表現して test が落とすことを確認する。fake / mutant は到達可能な production input に対する wrong implementation を表すために使い、fake が表現できること自体を supported state の authority にしない。
- 通常の不正入力・failure test は product の failure contract を検証する。targeted mutant は test 自体の識別力を確かめるために限って使い、両者を混同しない。
- 必要性判断で behavior-preserving replacement や characterization test を行う場合は、base で green でもよい。packet が定める wrong variant / invariant violation を検出する evidence は、計画で必要とした場合だけ残す。
- mutation score や coverage は診断値であり、それだけを completion signal にしない。changed decision logic に対応する少数の targeted negative control を優先する。

## 8. 計画と引継ぎ

計画の内容と保存範囲は [運用契約 section 4](codex-agent-workflow.md#4-最終計画と記録の使い分け)、完了報告は [実装担当の完了報告](codex-agent-workflow.md#実装担当の完了報告) に従う。テスト設計が必要な場合は、section 2・3 の設計書と配置表を参照し、実装したメソッド、置換関係、共有資源と完了の待ち方、必要と判断した識別力の確認結果を引き継ぐ。テスト不要または削除のみの場合は、必要性の判断と適切な検証結果を伝える。

packet からの deviation が必要になった場合は、worker が独自に採用せず、authority、observable impact、推奨変更を `NEEDS_ROOT_INPUT` として返す。packet が適用されない変更で red / negative-control がないことだけを理由に追加作業を要求しない。

## 9. レビュー

reviewer は、必要性判断でテストを追加・意味変更・置換した場合、green result だけでなく次を確認する。テスト不要または削除のみの判断を、packet や代替 test がないことだけで覆さない。

- assertion semantics が承認済み packet / Contract ID に対応し、authority、expected outcome、allowed variation と一致するか
- expected value が current implementation、current output、existing expected、translation copy、snapshot、repository prose から写経されていないか
- 適用された packet の plausible wrong implementation を test が実際に区別するか。red / negative-control は、bugfix の再現または test の識別力に具体的なリスクがあり、計画で必要とした場合の evidence として妥当か
- packet の production ingress から対象 state と observable impact まで実際に到達するか。private direct call、reflection、fake-only route が entrance invariant を迂回していないか。test fixture が production logic や期待値導出をコピーしていないか
- canonical existing fixture を無視した重複 test / local helper が増えていないか
- failure、cancel、retry / reentry、shutdown、cleanup が正常系と同じ owner で閉じているか
- raw dispatcher pump、visible HWND、physical cursor、process、settings、filesystem などの shared resource が documented policy に従うか
- fixed wait、追加 DNP、worker 低下、timeout 延長で不安定性を隠していないか
- exact string / snapshot / source-artifact / reflection / characterization / legacy-absence test に authority、owner、退役条件があるか
- 変更した仕様項目と、実装・テストメソッドの対応が現行仕様へ反映され、置換した旧テストが重複して残っていないか
- 計画固有の段階番号・Packet ID / Contract ID が恒久仕様やテストの名前・属性・ケース ID・コメントへ漏れていないか。完了した設計書の保存を要求せず、引継ぎまたは指定された版で承認内容を確認できるか

必要性判断で test semantics を変更するのに packet がない場合、または packet と diff が矛盾する場合は、明示された受入条件に対する test-design gap として扱う。red / negative-control の未実施だけでは finding にせず、変更分類、必要性判断、具体的な識別力リスクに照らして判断する。finding の evidence と分類は `codex-agent-workflow.md` の reachability / impact gate に従う。production reachability または observable impact を示せない事項は recommendation / theoretical / out-of-scope とし、現在の bounded unit に production seam、persistent state、retry / replay、rollback、recovery abstraction、追加 test を要求しない。

## 実装とテストの対応

| 仕様項目 | 適用する指示 | 確認方法 |
| --- | --- | --- |
| 必要性判断・独立した判定基準・作業内の参照名 | [テスト設計担当](../../.codex/agents/test-contract-designer.toml) の `developer_instructions` | 自動テストなし。設計書の項目が要件に基づき、配置調査で期待値を変更しない手順かを点検する。 |
| 既存テストの利用・配置・引継ぎ | [テスト指示](../../BeMusicSeeker.Tests/AGENTS.md)、[実装担当](../../.codex/agents/implementation-worker.toml) の `developer_instructions` | 自動テストなし。配置表と完了報告が正本を参照し、仕様から実装とテストへ辿れるかを点検する。 |
| 一時的な参照名の範囲・設計書の退役 | [テスト指示](../../BeMusicSeeker.Tests/AGENTS.md)、[計画の運用](../plan/README.md#運用) | 自動テストなし。最終差分の命名・属性・コメントと仕様参照を確認し、契約を維持したまま一時 ID と作業専用資料を退役しているかを点検する。 |
| レビュー | [レビュー担当](../../.codex/agents/repo-static-review.toml) の `developer_instructions` | 自動テストなし。根拠・到達可能性・識別力の判断が本契約と一致するかを点検する。 |
