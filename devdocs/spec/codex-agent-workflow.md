# Codex エージェント運用契約

最終更新: 2026-09-16

この文書は、BeMusicSeeker で複数段階の変更を計画・実装・レビューするときの Codex 運用の正本である。ルートエージェントが要件・判断・統合を担い、必要な作業を適切な役割へ委譲する。重複調査や実装に引きずられたテスト設計を避け、判断に必要な情報を簡潔に引き継ぐ。

単純な質問、読み取りだけの確認、Markdown の軽微な修正まで機械的にサブエージェントへ渡す必要はない。production code、test harness、runner、設定の bounded implementation は `implementation-worker` へ委譲し、ルート自身の書込みは計画文書、統合 conflict、機械的な handoff 修正などに限定する。

## 用語

| 日本語名 | 意味 | 原語・対応する名称 |
| --- | --- | --- |
| 作業単位 | 同じ挙動と完了条件をまとめて実装・検証する範囲 | unit |
| 管理主体 | 状態や処理の開始から完了までに責任を持つ実装 | owner |
| 本番の入口 | 実際の操作・起動・イベントなどから処理へ入る経路 | production ingress |
| 維持する条件 | 処理を通じて成立させる契約 | invariant |
| 仕様の根拠 | 挙動や期待値を決める要件・承認済み判断・外部仕様 | authority |
| 判断材料 | 経路の追跡や実行結果など、調査・検証の判断に用いる情報 | evidence |
| 判定基準 | テストが正しい結果と誤った結果を区別する基準 | oracle |
| テスト設計書 | 根拠、期待する結果、許容差分、配置を実装前に定める文書 | Test Contract Packet |
| 既存の検証範囲 | 既存テストが確認している挙動と条件 | coverage |
| テスト構成 | テストクラスと、その入力準備・後処理 | fixture |
| 実行区分 | 通常検証、性能検証など、目的別のテスト実行範囲 | lane |
| 識別力の確認 | 意図的な誤実装をテストが検出できるか確かめること | targeted negative control |

## 役割

| 役割 | エージェント名 | 主な責務 | 書込み |
| --- | --- | --- | --- |
| ルート | 現在の主セッション | ユーザー対話、要件と判断の整理、設計、最終計画、テスト設計書の承認、割当、統合、統合検証、レビュー対応 | 計画と統合 |
| 計画点検 | `plan-clarifier` | 計画案の未決事項、危険な仮定、独立テスト設計の要否、並列境界を点検 | なし |
| テスト設計 | `test-contract-designer` | 実装から独立した判定基準、許容差分、検出すべき誤実装、テスト配置を設計 | なし |
| 実装 | `implementation-worker` | 確定した作業単位と、必要な承認済みテスト設計書に従って実装し、関連 Quick の結果と引継ぎを返す | 指定パス内 |
| 障害解決 | `issue-resolver` | 実装担当が発見した重大問題を調査・修正。観測できる挙動の意味は維持する | 指定パス内 |
| 静的レビュー | `repo-static-review` | 凍結した変更を、実装とは独立した視点から読み取り専用でレビュー | なし |

委譲の深さは `root -> implementation-worker -> issue-resolver` までとする。`plan-clarifier`、`test-contract-designer`、`repo-static-review` は root 直下の leaf とし、追加サブエージェントを起動しない。worker は `issue-resolver` 以外を起動せず、`issue-resolver` から先へ再帰しない。この契約は runtime の depth enforcement にかかわらず守る。

## 1. ルートによる要件整理

ルートは実装前に、ユーザー要件とリポジトリで確認した事実を次の形へ整理する。

- **目的と完了条件**: 変える挙動、得たい結果、退役する旧処理、必要な検証とレビュー。
- **背景と根拠**: 関連する仕様、ファイルと識別子、現行の処理経路、既知の失敗と再現条件。
- **制約と決定事項**: 対象外、互換性、担当範囲、スレッド、永続化、安全性、作業中の差分、未決の選択。操作受付を変える場合は [並行性契約 section 6](workflow-concurrency-and-complexity.md#6-操作種別ごとの共通既定と維持する例外) に照らし、Busy で拒否する新規要求、受理済みの仕事、維持する並行操作を分ける。
- **到達可能性と影響**: 本番の入口から管理主体・対象状態までの経路、入口の前提、利用者・永続データ・外部データに現れる差分。
- **変更分類と恒久テストの必要性**: [テスト作成契約 section 1](test-authoring-contract.md#1-変更分類と恒久テストの必要性) に従い、扱いと検証方法を数行で決める。
- **性能影響がある場合**: [性能要件](performance-and-scale.md) に従い、対象操作、全体規模と差分、全件処理の反復回数、キャッシュ・一時的な参照データ・処理結果の保持範囲、必要な逐次境界、比較する完了地点を整理する。処理速度を最優先とし、測定は変更に関係する操作を対象にする。

repository の正本、既存 test、履歴、合意済み方針から一意に決まる事項は、ルートまたは clarifier が調べて解決する。永続化、fallback、failure contract、ownership、互換性、破壊的操作などが一意に決まらない場合だけ、ユーザーへ具体的な質問を返す。current implementation や既存 test の expected value が存在することだけで、その behavior を仕様として確定しない。

アプリが同時に受理する利用者操作、単一 batch 内の計算並列度、実装 worker の並列度は別々の判断である。並行性を減らす案でも既存の明示的な機能を失うなら behavior change として扱う。設定画面を開くことを設定適用の受付と同一視せず、通信待ちを理由に全ライブラリ操作を禁止しない。共通 spec で決定済みの要件を再び未決として worker へ渡さず、触る実入口・状態境界・残る実装選択だけを unit に記載する。

### 到達可能性と影響の確認

runtime state、failure、invariant を plan、test、implementation、review の対象にするには、承認済み authority、canonical production ingress から実際の production consumer / owner までの route、user-observable behavior または durable / external-data impact を示す。private API の直接呼出し、reflection、test fake、型や分岐上の representability は、それだけでは reachability evidence にならない。承認済みの新機能で production ingress 自体を追加する unit は、その route と impact が final plan に明記されていれば対象にできる。source / build artifact 自体が契約の場合も、実際の build / release ingress と artifact impact を示す。

process-exclusive DB、single writer、外部 filesystem の変更可否、snapshot / refresh boundary などの system assumption は、owner entrance で検証・snapshot 化するか、仕様または composition contract として確立する。downstream は成立済み invariant を受け取り、assumption 外の状態を revalidation、fallback、retry、replay、rollback で救済しない。reachable な invalid state を earliest shared owned ingress で生成不能または明示 reject にできるなら、そこで invariant を閉じ、不要な downstream recovery を退役する。

承認済みで到達可能な observable behavior に必要でない persistent state、retry / replay / rollback、compatibility route、recovery lifecycle、interface / coordinator 等の abstraction は追加しない。fake-only state、将来の可能性、non-blocking recommendation は scope を拡張する authority にならない。

## 2. 計画案の点検

ルートはまず自分で draft plan を作る。各 unit は method 数や file 数だけでなく、同じ behavior、owner、failure contract、verification scope が閉じる vertical slice とする。3つを超える subsystem、約15 files を超える見込み、同一巨大 fixture 内に複数領域が混在する場合は分割の signal とする。

サブエージェントへ実装を渡す計画では、完成前に `plan-clarifier` を原則一度だけ呼ぶ。section 1 の要件整理、section 4 の作業単位案、未決事項を渡す。恒久テストを追加・意味変更・置換する場合は、判定基準の変更点、設計書の作業名、独立した仕様の根拠、既存のテスト配置候補と不足事項も添える。

clarifier は計画や oracle を代作せず、repo で解けた事実、ユーザーへ必要な質問、変更分類と root の恒久テスト必要性判断が妥当か、必要とした場合の `test-contract-designer`、authority / reachability packet の不足、安全な並列候補、衝突、replan trigger だけを返す。private call や fake-only route を production reachability の代用にしない。恒久テストを必要とした計画では、feature spec と production symbol から候補 fixture を絞る targeted search があり、既存 coverage、shared resource、completion signal が test delta へ反映されているかも点検する。テスト不要または削除のみの判断に packet や代替 test を要求しない。既存 implementation や test expected から新しい期待値を提案しない。

ルートは clarifier の結果を decision list と draft units へ反映する。回答で ownership や unit 境界が大きく変わった場合だけ、差分を限定して再点検する。repo で解ける事項のために planner loop を繰り返さない。完了した clarifier thread は閉じてから test design または implementation へ進む。

## 3. 独立したテスト設計

変更を目的と影響で分類し、root が恒久テストの追加・意味変更・置換を必要と判断した unit では、decision list を閉じた後、実装前に `test-contract-designer` を一度呼ぶ。

- durable test の assertion、expected value、snapshot、golden、source / reflection contract を追加・変更する
- observable behavior の変更に regression test が必要であると判断した
- brittle source / localized copy / docs prose / broad snapshot test を semantic test へ置換する
- behavior-preserving migration のため characterization test を新設する

削除のみで代替不要と root が判断した場合は理由付きで designer を省略する。名前変更、移動、format、生成物更新などの mechanical change で assertion semantics が変わらない場合も省略する。runner、lane、shared infrastructure だけの変更では、恒久テストを必要と判断して behavior assertion を追加・意味変更・置換する場合だけ designer を使う。

ルートはテスト設計担当へ、作業名、変更分類とテストの目的、section 1 の確定した要件、期待値の根拠となる仕様や判断、変更前の版と既知の再現条件、配置候補を渡す。実装や現在の出力から独立して判定基準を決められるよう、根拠となる資料と配置確認用の資料を区別する。

`test-contract-designer` は二段階で作業する。

1. **判定基準の確定（Phase A）**: 実装本体、現在の出力、既存の期待値、翻訳文言、保存された比較用出力を読む前に、仕様の根拠から挙動、失敗、許容差分、検出すべき誤実装、確認方法を決める。
2. **配置の確認（Phase B）**: 判定基準を確定した後、本番の入口から実装の呼出し口までの経路、既存テストの候補、共有資源、実行区分、完了の待ち方、退役するテストを調べる。実装本体の参照は到達可能性とテスト方法の確認に限り、期待値は維持する。非公開呼出しやテスト専用の状態生成でしか作れない状態は、到達経路の不足としてルートへ返す。

`read-only` sandbox は書込みを防ぐが、implementation の読み取り自体を技術的に遮断するものではない。通常運用では agent prompt の phase boundary と親が渡す authority packetで独立性を保つ。data loss、security、protocol compatibility など高リスクの oracle は、可能なら Phase A を仕様・issue・public contract だけを置いた別 session / spec-only working directory で行い、その結果を Phase B へ渡す。

テスト設計書の内容、一時的な参照名の有効範囲、期待値と配置の書式は [テスト作成契約 section 2・3](test-authoring-contract.md#2-仕様の根拠とテスト設計書) を正本とする。実装担当に渡す際は、許容するテスト構成の調整と、維持する期待値の意味を明確にする。

ルートは packet を decision list と受入条件に照らして承認する。designer が `NEEDS_ROOT_DECISION` を返した場合、current implementation から期待値を補完せず、repository evidence またはユーザー判断で authority を閉じる。packet を変更する必要が生じた場合は、変更理由と authority をルートが明示し、必要なら designer へ差分だけ再点検させる。

小さい作業では承認済みテスト設計書を実装担当への指示に含める。複数の作業単位やセッションで共有する場合は、進行中の計画へ保存し、実装担当とレビュー担当に同じ版を渡す。計画はその参照先を示せばよい。完了時は section 4 に従い、契約と実装・テストの対応だけを恒久仕様へ統合し、計画と作業専用の設計書を削除する。

## 4. 最終計画と記録の使い分け

最終計画は、実装担当が挙動や判定基準を推測せず着手できる内容にする。作業単位ごとに次をまとめ、決定済みの詳細は正本の該当箇所を参照する。

1. **目的・状態・完了条件**: 観測できる結果と、現在の進捗。
2. **担当範囲と順序**: 書込みパス、読み取り範囲、先行作業・他担当・生成物との依存、統合の順序、変更する本番経路と退役する旧処理。
3. **維持する条件**: UI、永続データ、ファイル・通信の互換性、スレッド、終了処理、失敗時の挙動。受付変更では、具体的な入口における拒否時の無副作用と、維持する予約・閲覧・並行操作を示す。
4. **テストの扱い**: 必要性の判断と理由。追加・意味変更・置換する場合は、承認済みテスト設計書と対象項目、既存テストの配置調査、旧テスト・補助処理との置換関係を参照する。
5. **検証とレビュー**: 関連 Quick のフィルター、必要な統合・明示実行の区分、必要と判断した修正前の失敗再現や識別力の確認方法。静的レビューの目的、比較する版、対象範囲と設計書の確認範囲も示す。テスト不要の場合は適切な実行確認・静的検査を選ぶ。
6. **再計画の条件**: 実装を止めて設計やユーザー判断へ戻す具体的な状況。
7. **性能影響がある場合**: 代表規模と操作差分、処理量、比較条件、未検証範囲。実装担当は操作別の実測または未測定を引き継ぐ。

計画の目的は進捗把握と再開であり、作業中は判断に必要な設計を保持する。完了時は [計画の運用](../plan/README.md#運用) に従い、恒久的な挙動と実装・テストの対応を [現行仕様](README.md#仕様書の書式) へ、現在も適用する採用理由を `decisions/` へ統合する。必要な残課題を未完了の計画へ引き継いだ後、完了計画と作業専用の付随資料を削除し、参照元も更新する。短い完了記録や別名のアーカイブには置き換えない。

段階番号・作業番号・Packet ID / Contract ID は計画と引継ぎの内側だけで使う。恒久仕様、コード・テストの名前・ID・コメントには持ち込まず、機能・契約・条件・期待結果と実際の識別子で対応を示す。最終レビューには、退役する設計書の承認済み内容を引継ぎまたは Git 上の版で渡し、レビューのために完了資料を現行ツリーへ復元しない。

通常の検証は、引継ぎと最終報告で実行範囲・結果・未実施事項を簡潔に伝える。障害調査中の詳細な実行条件やログの場所は、その調査を続ける担当間で共有する。結論が現行の契約・採用理由なら正本へ、必要な残課題なら未完了の計画へ反映し、通常の実行履歴や完了報告をリポジトリに保存しない。現在の設計・性能判断や比較基準として使う測定だけは、[現行情報の維持](../README.md#現行情報の維持) に従い用途・条件・結論を揃えて `devdocs/acceptance/` に置く。

## 5. 並列作業

書込み worker の既定は1つ、同時実行は最大2つとする。独立した unit の並列化は、速度向上が agent 起動・統合コストを上回り、次をすべて満たす場合だけ使う。

同じ unit を複数 worker へ重複割当せず、worker 比較、shadow 評価、二重実装を行わない。各 worker は `issue-resolver` 以外の agent を起動しない。

- writable path、生成物、schema / migration、shared fixture が重ならない。
- 一方の結果を見ないと他方の正しい実装が決まらない関係ではない。
- 同じ Test Contract Packet の Contract ID を別 worker が重複実装しない。
- 統合後の acceptance を一度に確認できる。
- 同じ巨大 file の別 method を触るだけの見かけ上の分割ではない。

read-heavy な探索、oracle design、inventory、log analysis は write-heavy な実装と比べて並列化しやすいが、test designer は implementation 前に packet を凍結するため、同じ unit の worker と同時には走らせない。各 worker へ他 worker の所有 path と Contract ID を渡し、実際の重複を発見した worker は編集を止めてルートへ返す。

`max_concurrent_threads_per_session = 3` は、2 worker と1つの nested resolver を上限とするために使う。clarifier と test designer は順番に実行して結果受領後に閉じ、review 前には implementation / resolver thread を残さない。設定の具体値は `.codex/config.toml` を正本とし、role の選択と委譲境界はこの workflow と agent prompt で守る。

## 6. 実装担当と障害解決担当

この section の worker 契約は `implementation-worker` に適用する。worker は final plan の unit だけを実装し、所有境界、failure contract、test oracle、verification、handoff の規則を守る。

### 実装担当の開始確認

worker は編集前に次を確認する。

1. repository path、root `AGENTS.md`、書込み path 配下の nested `AGENTS.md`、`devdocs/spec/codex-agent-workflow.md`
2. `git status --short`、tracked / staged / untracked diff
3. unit の Goal、observable outcome、canonical production ingress からの route、入口 assumption、書込み所有 path、read-only 参照範囲、依存関係、受入条件、維持する invariant、対象 Quick filter、replan trigger
4. 必要性判断で test semantics を変更する場合は、root が承認した `Test Contract Packet`、対象 Contract ID、authority、expected outcome、allowed variation、plausible wrong implementation、base-fail / negative-control strategy、`devdocs/spec/test-authoring-contract.md`。不要または削除のみの場合は packet を要求しない
5. 他 worker の所有 path / Contract ID と、統合時に root へ返す handoff 形式

開始確認で読んだ対象と plan の ownership を越えて探索・編集しない。repository で確認できる candidate fixture、helper、public seam は worker 自身が調査し、root へ同じ調査を戻さない。

### 共通実装規則と入力不足

必要性判断で test の assertion、expected value、snapshot、golden、source / reflection contract の semantics を変更するのに承認済み packet がない、packet と final plan の expected semantics が矛盾する、authority が不足する、または runtime state に canonical production ingress からの reachability / user-observable / durable impact がない場合は、編集せず `## NEEDS_ROOT_INPUT` と不足項目だけを root へ返す。repository の actual route と矛盾する、または private / reflection / fake-only seam が product contract の前提になる場合も同じ扱いとする。削除のみで代替不要と root が判断した場合は理由付きで packet 不要とする。名前変更、移動、format、生成物更新などの mechanical change で assertion semantics が変わらない場合も packet 不要である。

指定 unit と所有 path だけを変更し、無関係な既存差分を変更、破棄、整形しない。旧 route、重複処理、脆い test seam の退役まで同じ unit で閉じる。テスト都合だけの public API、service locator、broad callback host、将来用 abstraction を追加しない。reachable な invalid state は approved semantics と compatibility を変えずに可能なら earliest shared owned ingress で生成不能または explicit reject にし、不要な downstream revalidation / recovery を退役する。fake-only state、code representability、non-blocking suggestion を根拠に persistent state、retry、replay、rollback、recovery lifecycle、compatibility route、abstraction を追加しない。

observable behavior、persisted data、failure、threading、shutdown、compatibility を維持する。必要性判断で test を変更する場合だけ、対応する Contract ID を実装する。fixture、helper、data setup、assertion API の mechanics は repository に適合させてよいが、packet の authority、expected outcome、allowed variation、wrong implementation を current implementation / runtime output / existing expected / translation copy に合わせて変更しない。authority、required outcome、allowed variation、observable semantics の変更が必要なら、green にするため assertion を弱めたり production behavior を勝手に変えたりせず `NEEDS_ROOT_INPUT` を返す。

### テストと検証

必要性判断で test を追加・意味変更・置換する場合、feature spec と production symbol から candidate fixture を絞り、必要な追加・置換の配置（`extend / replace / new`）を決め、既存の共通 helper を優先する。production logic、expected-value derivation、runner orchestration を test 側へコピーしない。bugfix で承認済み production ingress から再現できる場合の red は原則有用だが、実施要否は計画の必要性と識別力リスクに従う。compile error、fixture setup failure、unrelated exception は red evidence ではない。base で test を構造上実行できないことだけでは targeted mutant / wrong variant / negative control を要求しない。bugfix の red の代替、または識別力に具体的なリスクがあり計画で必要と判断した場合だけ、behavior-preserving replacement、characterization など packet の targeted mutant / wrong variant / negative control を使い、test が誤実装を落とす evidence を残す。通常の不正入力・failure test と mutant 実行を混同しない。targeted mutation は確認後に必ず戻し、最終 diffへ混ぜない。

stage、commit、push、tag、version 更新は行わない。反復中は指定された filtered Quick を優先し、統合 Functional、Full、最終 review は root に任せる。verification failure の分類、timeout retry、必要な evidence は `devdocs/spec/testing-strategy.md` に従う。root の依頼がない限り、runner、release、process、アプリ起動、統合検証へ scope を広げない。

worker が `issue-resolver` を呼べるのは、次の runtime / contract blocker または operational blocker に限る。

runtime / contract blocker は reachability / impact gate を満たす必要がある。route または impact が不足する、canonical ingress が所有 path 外、または未承認の recovery machinery が必要な場合は resolver trigger ではなく root の replan trigger とする。

- data loss、security、起動不能、現実的な deadlock / race / compatibility regression
- final plan または packet の前提を崩す cross-cutting ownership / failure contract
- 所有 path 外の変更なしには安全に閉じられない問題
- 承認済み production ingress と observable behavior が立証済みで、packet を保ったまま解消できる可能性がある、packet と executable seam の技術的な実矛盾。private / fake-only state の testability、単に current implementation が test に通らないこと、または authority / expected semantics の再決定が必要なことは resolver trigger ではない

operational blocker は production reachability ではなく `testing-strategy.md` の failure evidence または具体的な ownership / diff conflict で判定する。

- 通常の局所修正で解消しない deterministic verification failure、同一条件の2回目の timeout / failure
- 実際の差分衝突または機械的 handoff だけでは閉じない ownership conflict

resolver は上記 trigger に該当する場合だけ使う。worker は編集を止め、current diff、書込み可能 path、維持する invariant、packet / Contract ID、試した検証に加え、runtime / contract blocker では canonical ingress から impact までの evidence、operational blocker では `testing-strategy.md` に沿う failure evidence または具体的な ownership / diff conflict evidence を1つの resolver へ渡して結果を待つ。resolver は category にかかわらず承認済み invariant と expected semantics を変更せず、指定 path 内で blocker を閉じる技術的な修正に限定する。runtime / contract blocker では到達可能な contract に必要な fixture / adapter / public seam / ownership だけを修正し、未承認 scenario のための seam、persistent state、recovery abstraction は追加しない。authority、observable semantics、allowed variation の決定や packet の修正が必要な場合は、worker と resolver の双方がルートへ返す。

### 実装担当の完了報告

ルートが統合に必要な判断をできるよう、次の内容を要約して返す。既に共有した計画・テスト設計書は該当箇所を参照し、今回確定した内容を補う。テスト設計・配置は、テストを追加・意味変更・置換した場合に記す。

```text
## 実施内容
- 得られた結果、主要変更、変更ファイルと役割

## テスト設計・配置
- 承認済み設計書の対象項目と、実装したテストメソッド。設計からの差異があれば理由
- 調べた既存テストと採用した配置、旧テスト・補助処理・経路との置換関係
- 共有資源、実行区分・分割先、完了の待ち方と失敗検出の期限、例外的な検証方法
- 必要と判断して行った修正前の失敗再現や識別力の確認結果

## 検証結果
- 実行コマンド・フィルターと結果、未実施事項と理由
- 時間超過があった場合は初回と規定の再実行結果、調査の結論または継続に必要な情報

## 引継ぎ事項
- 統合時に確認する依存関係、競合、担当範囲、残課題
```

## 7. 検証失敗の扱い

標準検証の時間予算、再実行、失敗分類、診断情報は [テスト運用方針](testing-strategy.md) を正本とする。失敗を調査する間は、実装担当が実行コマンド・フィルター・対象の版・診断ログを引き継ぎ、ルートが再実行または原因調査を判断する。決定的な失敗、規定の再実行の失敗、同じ症状の再発は、障害解決担当の利用条件またはルートの再計画条件として扱う。文書へ残す内容は section 4 の記録方針に従う。

必要性判断で test を実施するとした場合、red evidence は compile error、fixture setup failure、unrelated exception ではなく、対象 Contract ID の observable mismatch であることを確認する。negative control は packet の plausible wrong implementation と対応し、test が現在の実装に通るという事実だけを品質証拠にしない。非 bugfix の red / mutant 未実施だけを理由に不足としない。

## 8. 統合、検証、レビュー

worker 完了後、ルートは handoff と diff を確認して並列結果を統合し、`testing-strategy.md` に従って統合 snapshot の filtered Quick / Functional / 必要な opt-in lane を実行する。worker が閉じた範囲の再実装ではなく、path / Contract ID ownership、conflict、packet conformance、acceptance evidence の統合に集中する。

実装と標準検証が完了したら、実装担当のセッションを閉じ、作業ツリーを凍結して `repo-static-review` を一度呼ぶ。レビュー担当には目的、受入条件、変更分類とテスト必要性判断、比較する版、作業差分、検証結果の要約を渡す。適用したテスト設計書と修正前の失敗再現・識別力の確認結果、時間超過の初回と再実行結果は、該当する場合だけ添える。レビュー中、ルートはリポジトリの読み取り、検索、編集、ビルド、テスト、整形、ステージ、コミットを停止する。

reviewer は変更対象に完了計画・作業専用資料が残っていないか、必要な契約・残課題の引継ぎと参照更新が済んでいるか、一時的な段階番号や ID が恒久仕様・コード・テストへ漏れていないかを確認する。退役前の計画や承認内容が必要なら渡された版を参照し、現行ツリーへの保存を要求しない。

reviewer は assertion が packet の authority、required invariant、allowed variation と一致するか、expected が implementation / current output / existing expected / translation copy / snapshot の写経になっていないか、plausible wrong implementation を区別できるかを確認する。必要性判断で test semantics を変更したのに packet がない、または packet と diff が矛盾する場合は受入条件上の test-design gap として扱う。red / negative-control の未実施だけを理由に finding を作らない。

挙動・設計の指摘を修正必須とする場合は、次を一組で示す。

- **重大度**: P0 / P1 / 受入条件に反する P2
- **仕様の根拠**: 違反するユーザー要件・受入条件・仕様。テスト設計書の項目を使う場合は、その項目が参照する根拠
- **到達経路**: 本番の入口から管理主体・利用箇所を経て対象状態に至る経路
- **前提**: 入口で成立する条件とシステムの前提
- **影響**: UI、失敗、永続データ、外部への作用、後処理に現れる差分
- **判断材料**: `file:line` と経路の追跡、再現結果、識別力の確認結果
- **対応範囲**: 現在の作業単位で修正するか、ルートの判断・再計画が必要か

production reachability または observable impact を示せない runtime-state 仮説は `theoretical / unreachable` または out-of-scope とし、現在の unit の production / test 変更を要求しない。private/direct invocation、reflection、fake-only setup、code representability は reachability evidence ではない。invalid state を ingress で防げる場合は downstream recovery を要求せず、明示承認のない新しい state、retry、replay、rollback、recovery lifecycle、abstraction を non-blocking recommendation から現在の scope へ取り込まない。

FS+DB の finding は [file-db-consistency.md](file-db-consistency.md) section 7 の分類と終了基準を併用し、許容済みの残留リスクと契約違反を区別する。共通方針を理由に機能固有の既存補償を黙って取り除くことも、理論的な failure の追加だけで新しい復旧保証を要求することも行わない。

blocking finding の修正後は、影響範囲の Quick と必要な統合検証を行い、fix delta、previous snapshot、previous findings、更新した packet があればその authority を fresh reviewer へ渡す。同じ unit で2回の修正 review 後も新しい P1 が続く場合は、finding を継ぎ足さず、ownership、scope、acceptance、packet、unit 分割を再計画する。

## 9. エージェントが利用できない場合

ユーザー要件、承認済み decision、feature spec、repository の `AGENTS.md` とこの workflow は、汎用 Skill の推奨より優先する。Skill が停止、追加承認、scope 変更を要求するように見える場合は、まず指示の適用範囲と既存の承認を確認する。既に承認された scope 内で解決できるなら承認を取り直さず続行する。未決の判断が残る場合だけ、worker は具体的な `SKILL.md` の path、該当指示の引用、適用理由を root へ返し、依存する作業を止める。

custom agent が見つからない場合は、同じ model、reasoning effort、sandbox、role contract を明示した built-in / generic agent を代替にする。`test-contract-designer` の代替では、少なくとも oracle-first と repository-fit を別 turn または別 session に分け、Phase A で implementation body / current output / existing expected を使わない。

multi-agent 機能自体が利用できない場合は、ルートが同じ planning、independent oracle design、implementation、verification、fresh review の境界を順番に再現し、代替した箇所、oracle の独立性、未実施の negative control、独立 review の有無を結果へ明記する。agent が使えないことを理由に、未決 semantics、Test Contract Packet、static review を黙って省略しない。

## 実装とテストの対応

| 仕様項目 | 適用する指示・設定 | 確認方法 |
| --- | --- | --- |
| 要件整理・計画点検 | [ルート指示](../../AGENTS.md)、[計画点検担当](../../.codex/agents/plan-clarifier.toml) の `developer_instructions` | 自動テストなし。目的・未決判断・担当範囲が揃い、計画の書式と一致するかを点検する。 |
| 現行情報の維持・計画の退役 | [ルート指示](../../AGENTS.md)、[資料配置](../README.md#現行情報の維持)、[計画の運用](../plan/README.md#運用) | 自動テストなし。最終差分と参照検索で、契約・残課題の移管、完了資料の削除、リンクと一時 ID の残存を点検する。 |
| 独立したテスト設計 | [テスト設計担当](../../.codex/agents/test-contract-designer.toml) の `developer_instructions`、[テスト作成契約](test-authoring-contract.md) | 自動テストなし。期待値の根拠と配置調査の順序、設計書の項目を点検する。 |
| 実装・障害解決・完了報告 | [実装担当](../../.codex/agents/implementation-worker.toml)、[障害解決担当](../../.codex/agents/issue-resolver.toml) の `developer_instructions` | 自動テストなし。担当範囲、障害解決の条件、検証結果と残課題の引継ぎを点検する。 |
| 静的レビュー・並列度 | [レビュー担当](../../.codex/agents/repo-static-review.toml) の `developer_instructions`、[設定](../../.codex/config.toml) の `agents`・`features.multi_agent_v2` | 自動テストなし。TOML の構文と役割設定、凍結・待機・指摘の判断基準を点検する。 |
