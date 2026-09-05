# Codex エージェント運用契約

最終更新: 2026-09-05

この文書は、BeMusicSeeker で複数段階の変更を計画・実装・レビューするときの Codex 運用の正本である。目的は、ルートエージェントへ要件・判断・統合責任を残しながら、必要な作業だけを適切なモデルへ委譲し、重複調査、実装バイアス、過剰な並列化、長い生ログによる rate limit と context の消費を抑えることである。

単純な質問、読み取りだけの確認、Markdown の軽微な修正まで機械的にサブエージェントへ渡す必要はない。production code、test harness、runner、設定の bounded implementation は原則 `implementation-worker` へ委譲し、section 5 の条件を満たす複雑な unit だけ root が `implementation-worker-frontier` を選ぶ。ルート自身の書込みは計画文書、統合 conflict、機械的な handoff 修正などに限定する。

## 役割

| 役割 | agent | 主な責務 | 書込み |
| --- | --- | --- | --- |
| ルート | 現在の主セッション | ユーザー対話、要件と decision の整理、設計、最終計画、test contract の承認、割当、統合、統合検証、review 対応 | 統合時だけ |
| 計画点検 | `plan-clarifier` | draft plan の未決事項、危険な仮定、test-design gate、並列境界を短く点検 | なし |
| テスト契約設計 | `test-contract-designer` | 実装から独立した oracle、allowed variation、wrong implementation、coverage placement を設計 | なし |
| 通常実装 | `implementation-worker` | 完成済みの bounded unit と承認済み Test Contract Packet を実装し、focused Quick と handoff を返す | 指定 path 内 |
| 複雑 unit 実装 | `implementation-worker-frontier` | ルートが選択した複雑な bounded unit を共通 worker 契約内で実装し、focused verification と handoff を返す | 指定 path 内 |
| blocker 解決 | `issue-resolver` | worker が発見した重大問題を調査・修正。observable semantics は変更しない | 指定 path 内 |
| 静的 review | `repo-static-review` | 凍結 snapshot と Test Contract Packet を fresh / read-only で review | なし |

委譲の深さは `root -> implementation-worker` または `root -> implementation-worker-frontier`、必要な blocker がある場合だけその各 worker から `issue-resolver` までとする。`plan-clarifier`、`test-contract-designer`、`repo-static-review` は root 直下の leaf とし、追加サブエージェントを起動しない。worker は `issue-resolver` 以外を起動せず、`issue-resolver` から先へ再帰しない。この契約は runtime の depth enforcement にかかわらず守る。

## 1. ルートによる要件整理

ルートは実装前に、会話と repository evidence を次の形へ正規化する。

- **Goal**: 何を変え、どの observable outcome を得るか。
- **Context**: 関連する file、symbol、spec、現行 route、既知の failure evidence。
- **Constraints**: 対象外、互換性、ownership、threading、永続化、安全性、作業中差分。
- **Done when**: behavior、削除する旧 route、必要な test lane、review、artifact。test を触る場合は independent authority、既存 coverage、shared resource、completion signal も含める。
- **Decision list**: 選択で observable behavior が変わる事項と、既に決まっている回答。
- **Production reachability / observable impact**: 実 UI / event / startup / scheduler / owner / supported public ingress から対象 state へ至る route、入口 precondition と system assumption、利用者または persisted / external data に現れる差分。

repository の正本、既存 test、履歴、合意済み方針から一意に決まる事項は、ルートまたは clarifier が調べて解決する。永続化、fallback、failure contract、ownership、互換性、破壊的操作などが一意に決まらない場合だけ、ユーザーへ具体的な質問を返す。current implementation や既存 test の expected value が存在することだけで、その behavior を仕様として確定しない。

### Reachability / impact gate

runtime state、failure、invariant を plan、test、implementation、review の対象にするには、承認済み authority、canonical production ingress から実際の production consumer / owner までの route、user-observable behavior または durable / external-data impact を示す。private API の直接呼出し、reflection、test fake、型や分岐上の representability は、それだけでは reachability evidence にならない。承認済みの新機能で production ingress 自体を追加する unit は、その route と impact が final plan に明記されていれば対象にできる。source / build artifact 自体が契約の場合も、実際の build / release ingress と artifact impact を示す。

process-exclusive DB、single writer、外部 filesystem の変更可否、snapshot / refresh boundary などの system assumption は、owner entrance で検証・snapshot 化するか、仕様または composition contract として確立する。downstream は成立済み invariant を受け取り、assumption 外の状態を revalidation、fallback、retry、replay、rollback で救済しない。reachable な invalid state を earliest shared owned ingress で生成不能または明示 reject にできるなら、そこで invariant を閉じ、不要な downstream recovery を退役する。

承認済みで到達可能な observable behavior に必要でない persistent state、retry / replay / rollback、compatibility route、recovery lifecycle、interface / coordinator 等の abstraction は追加しない。fake-only state、将来の可能性、non-blocking recommendation は scope を拡張する authority にならない。

## 2. draft plan と clarifier の点検

ルートはまず自分で draft plan を作る。各 unit は method 数や file 数だけでなく、同じ behavior、owner、failure contract、verification scope が閉じる vertical slice とする。3つを超える subsystem、約15 files を超える見込み、同一巨大 fixture 内に複数領域が混在する場合は分割の signal とする。

サブエージェントへ実装を渡す計画では、完成前に `plan-clarifier` を原則一度だけ呼ぶ。次をまとめて渡す。

```text
Goal:
Context / repository evidence:
Constraints / out of scope:
Done when:
Decision list and resolved answers:
Production ingress / route / entrance assumptions / observable impact:
Draft units:
- observable outcome
- writable paths / read-only references
- dependencies and proposed order
- verification and review scope
Test-design gate when tests change:
- assertion / expected-value semantics change: yes / no
- proposed packet ID and change class
- independent authority sources
- inputs that must not become oracle authority
Test delta:
- candidate existing fixture and `extend / replace / new`
- shared resource / lane / completion signal
- retired test / helper / route
Known open questions:
```

clarifier は計画や oracle を代作せず、repo で解けた事実、ユーザーへ必要な質問、`test-contract-designer` が必要か、authority / reachability packet の不足、安全な並列候補、衝突、replan trigger だけを返す。private call や fake-only route を production reachability の代用にしない。test を触る計画では、feature spec と production symbol から候補 fixture を絞る targeted search があり、既存 coverage、shared resource、completion signal が test delta へ反映されているかも点検する。既存 implementation や test expected から新しい期待値を提案しない。

ルートは clarifier の結果を decision list と draft units へ反映する。回答で ownership や unit 境界が大きく変わった場合だけ、差分を限定して再点検する。repo で解ける事項のために planner loop を繰り返さない。完了した clarifier thread は閉じてから test design または implementation へ進む。

## 3. 独立した Test Contract Packet の設計

次のいずれかに該当する unit では、decision list を閉じた後、実装前に `test-contract-designer` を一度呼ぶ。

- durable test の assertion、expected value、snapshot、golden、source / reflection contract を追加・変更・削除する
- observable behavior の変更に regression test が必要
- brittle source / localized copy / docs prose / broad snapshot test を semantic test へ置換する
- behavior-preserving migration のため characterization test を新設する

名前変更、移動、format、生成物更新だけで assertion semantics が変わらない作業では省略してよい。runner、lane、shared infrastructure だけの変更でも、新しい behavior assertion を伴うなら designer を使う。

ルートは designer へ生の会話や実装案を丸投げせず、次の authority packet を渡す。

```text
Packet ID / unit:
Change class: bugfix | feature | behavior-preserving refactor | characterization | test replacement
Goal / Done when / Constraints:
Resolved decision list:
Independent authority:
- user requirement / approved issue / reproduction
- feature spec / public API / protocol / schema / external standard
Base revision and known failure evidence:
Production ingress / reachability evidence / entrance assumptions / observable impact / candidate fixture hints:
Inputs that must not become oracle authority:
- current implementation / runtime output
- existing expected values / snapshots
- current translation copy / repository prose
```

`test-contract-designer` は二段階で作業する。

1. **Oracle-first**: implementation body、current output、existing expected、translation values、snapshot を読まず、authority から behavior、failure、allowed variation、plausible wrong implementation、evidence strategy を凍結する。
2. **Repository-fit**: oracle を凍結した後だけ、targeted read で canonical production ingress から executable owner seam への route、candidate fixture、shared resource、lane、completion signal、退役 test を決める。implementation body を読む場合も reachability と testability の確認に限定し、期待値を合わせ直さない。private / reflection / fake-only setup しかない runtime state は Contract ID にせず、reachability gap としてルートへ返す。

`read-only` sandbox は書込みを防ぐが、implementation の読み取り自体を技術的に遮断するものではない。通常運用では agent prompt の phase boundary と親が渡す authority packetで独立性を保つ。data loss、security、protocol compatibility など高リスクの oracle は、可能なら Phase A を仕様・issue・public contract だけを置いた別 session / spec-only working directory で行い、その結果を Phase B へ渡す。

出力する `Test Contract Packet` には少なくとも次を含める。

- Packet ID、change class、authority、authority gap
- Contract ID ごとの production ingress / reachability evidence、entrance assumptions / invariant、observable behavior / failure、required invariant / outcome、allowed variation
- plausible wrong implementation、base-fail / head-pass または targeted negative control
- candidate fixture、`extend / replace / new`、shared resource / lane、completion signal、退役 test
- exact string / snapshot / source / reflection / characterization の例外理由、owner、退役条件
- worker が変更してよい mechanics と、変更してはいけない expected semantics

ルートは packet を decision list と受入条件に照らして承認する。designer が `NEEDS_ROOT_DECISION` を返した場合、current implementation から期待値を補完せず、repository evidence またはユーザー判断で authority を閉じる。packet を変更する必要が生じた場合は、変更理由と authority をルートが明示し、必要なら designer へ差分だけ再点検させる。

小さい unit は承認済み packet を final plan と worker prompt に全文で含める。複数 unit にまたがる、または session をまたいで継続する作業では、一時的な packet を `devdocs/plan` の作業計画へ保存し、worker と reviewer へ同じ revision を渡す。packet は feature spec の代用品ではないため、完了後に恒久的な observable contract だけを該当 `devdocs/spec` へ統合し、一時 packet は完了記録として整理する。

## 4. final plan の必須項目

実装 worker へ渡す各 unit には、少なくとも次を含める。

1. observable outcome と完了条件
2. 書込み所有 path と、必要な read-only 参照範囲
3. 先行 unit、他 worker、生成物との依存関係
4. 変更する production / test route と、同時に退役する旧 route
5. 維持する UI、persisted data、file / protocol compatibility、threading、shutdown、failure invariant
6. 承認済み Test Contract Packet / Contract ID。test semantics が変わらない場合は `not required` と理由
7. 追加・更新する behavior test、具体的な Quick filter、統合時の Functional / opt-in lane
8. coverage ledger: candidate fixture、`extend / replace / new`、shared resource / lane、completion signal、退役 test / helper / route
9. base-fail / head-pass または targeted negative control の実行方法
10. worker が返す旧 test / route と replacement の対応表
11. static review の intent、base / head、review scope、packet conformance scope
12. 実装を止めて再計画・ユーザー判断へ戻す具体的な evidence
13. 統合 conflict を避ける path ownership と handoff 順

計画は「調査して適宜直す」だけで終わらせず、worker が observable semantics や test oracle を推測せずに着手できる粒度まで閉じる。

## 5. 並列作業

書込み worker の既定は1つ、`implementation-worker` と `implementation-worker-frontier` の合算で同時実行は最大2つとする。通常は `implementation-worker` を使う。ルートは割当前に、複雑な concurrency、persistence、failure、shutdown、runner、process、release、ownership の設計判断が unit の正しさを左右すると判断した場合だけ `implementation-worker-frontier` を選ぶ。frontier は通常 worker と同じ bounded unit の実装者であり、設計権限、test oracle の決定権、優先順位の決定権を持たない。未決 semantics を frontier へ委ねない。

同じ unit を両 worker へ重複割当せず、worker 比較、shadow 評価、二重実装を行わない。実装途中で worker の選択や unit の意味を変更する必要が生じた場合は、worker が root へ handoff して再計画を待つ。各 worker は `issue-resolver` 以外の agent を起動しない。

並列化は、速度向上が agent 起動・統合コストを上回り、次をすべて満たす場合だけ使う。

- writable path、生成物、schema / migration、shared fixture が重ならない。
- 一方の結果を見ないと他方の正しい実装が決まらない関係ではない。
- 同じ Test Contract Packet の Contract ID を別 worker が重複実装しない。
- 統合後の acceptance を一度に確認できる。
- 同じ巨大 file の別 method を触るだけの見かけ上の分割ではない。

read-heavy な探索、oracle design、inventory、log analysis は write-heavy な実装と比べて並列化しやすいが、test designer は implementation 前に packet を凍結するため、同じ unit の worker と同時には走らせない。各 worker へ他 worker の所有 path と Contract ID を渡し、実際の重複を発見した worker は編集を止めてルートへ返す。

`max_concurrent_threads_per_session = 3` は、2 worker と1つの nested resolver を上限とするために使う。clarifier と test designer は順番に実行して結果受領後に閉じ、review 前には implementation / resolver thread を残さない。設定の具体値は `.codex/config.toml` を正本とし、role の選択と委譲境界はこの workflow と agent prompt で守る。

## 6. worker と issue resolver

この section の worker 契約は `implementation-worker` と `implementation-worker-frontier` の双方に共通する。frontier は root が明示的に選択した場合だけ使うが、所有境界、failure contract、test oracle、verification、handoff の規則は通常 worker と同一である。

### worker の開始確認

worker は編集前に次を確認する。

1. repository path、root `AGENTS.md`、書込み path 配下の nested `AGENTS.md`、`devdocs/spec/codex-agent-workflow.md`
2. `git status --short`、tracked / staged / untracked diff
3. unit の Goal、observable outcome、canonical production ingress からの route、入口 assumption、書込み所有 path、read-only 参照範囲、依存関係、受入条件、維持する invariant、対象 Quick filter、replan trigger
4. test semantics を変更する場合は、root が承認した `Test Contract Packet`、対象 Contract ID、authority、expected outcome、allowed variation、plausible wrong implementation、base-fail / negative-control strategy、`devdocs/spec/test-authoring-contract.md`
5. 他 worker の所有 path / Contract ID と、統合時に root へ返す handoff 形式

開始確認で読んだ対象と plan の ownership を越えて探索・編集しない。repository で確認できる candidate fixture、helper、public seam は worker 自身が調査し、root へ同じ調査を戻さない。

### 共通実装規則と入力不足

assertion、expected value、snapshot、golden、source / reflection contract の semantics を変更するのに承認済み packet がない、packet と final plan の expected semantics が矛盾する、authority が不足する、または runtime state に canonical production ingress からの reachability / user-observable / durable impact がない場合は、編集せず `## NEEDS_ROOT_INPUT` と不足項目だけを root へ返す。repository の actual route と矛盾する、または private / reflection / fake-only seam が product contract の前提になる場合も同じ扱いとする。名前変更、移動、format、生成物更新だけで assertion semantics が変わらない場合は packet 不要である。

指定 unit と所有 path だけを変更し、無関係な既存差分を変更、破棄、整形しない。旧 route、重複処理、脆い test seam の退役まで同じ unit で閉じる。テスト都合だけの public API、service locator、broad callback host、将来用 abstraction を追加しない。reachable な invalid state は approved semantics と compatibility を変えずに可能なら earliest shared owned ingress で生成不能または explicit reject にし、不要な downstream revalidation / recovery を退役する。fake-only state、code representability、non-blocking suggestion を根拠に persistent state、retry、replay、rollback、recovery lifecycle、compatibility route、abstraction を追加しない。

observable behavior、persisted data、failure、threading、shutdown、compatibility を維持し、対応する Contract ID を実装する。fixture、helper、data setup、assertion API の mechanics は repository に適合させてよいが、packet の authority、expected outcome、allowed variation、wrong implementation を current implementation / runtime output / existing expected / translation copy に合わせて変更しない。authority、required outcome、allowed variation、observable semantics の変更が必要なら、green にするため assertion を弱めたり production behavior を勝手に変えたりせず `NEEDS_ROOT_INPUT` を返す。

### test、verification、変更禁止事項

test 変更では feature spec と production symbol から candidate fixture を絞り、`extend / replace / new` を決め、既存の共通 helper を優先する。production logic、expected-value derivation、runner orchestration を test 側へコピーしない。bugfix で承認済み production ingress から再現できる場合は production 修正前に focused regression test を作り、対象 Contract ID の observable mismatch で失敗する red evidence を残す。compile error、fixture setup failure、unrelated exception は red evidence ではない。base で test を構造上実行できない場合、behavior-preserving replacement、characterization では packet の targeted mutant / wrong variant / negative control を使い、test が誤実装を落とす evidence を残す。targeted mutation は確認後に必ず戻し、最終 diffへ混ぜない。

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

### worker の完了 handoff

生ログではなく、次の見出しと fields で root へ返す。

```text
## IMPLEMENTED
- observable outcome と主要変更

## FILES
- 変更 path と役割

## TEST CONTRACT
- packet ID と実装した Contract ID。該当しなければ `not applicable`
- red / head-pass または targeted negative-control evidence
- packet からの deviation または `none`

## TEST COVERAGE
- test を触った場合: searched symbols / candidate fixtures、`extend / replace / new`、退役 test と replacement。該当しなければ `not applicable`

## TEST SAFETY
- test を触った場合: shared resource、lane / shard、completion signal / watchdog、例外的 exact string / snapshot / source / reflection / WPF / process seam。該当しなければ `not applicable`

## VERIFICATION
- 実行 command / filter、結果、未実施項目
- timeout があった場合は初回 evidence と同一条件再実行の結果

## HANDOFF
- 削除した旧 test / route と replacement の対応
- root が統合時に確認すべき dependency / conflict / Contract ID ownership
- residual risk または `none`
```

## 7. verification failure の扱い

標準検証の時間予算、timeout retry、failure classification、必要な diagnostics は `devdocs/spec/testing-strategy.md` を正本とする。worker は exact command / filter / snapshot と artifact を handoff し、ルートが同じ方針で再実行または原因調査を判断する。deterministic failure、規定 retry の failure、同じ症状の再発は、resolver trigger または root の再計画条件として扱う。

red evidence は compile error、fixture setup failure、unrelated exception ではなく、対象 Contract ID の observable mismatch であることを確認する。negative control は packet の plausible wrong implementation と対応し、test が現在の実装に通るという事実だけを品質証拠にしない。

## 8. 統合、検証、review

worker 完了後、ルートは handoff と diff を確認して並列結果を統合し、`testing-strategy.md` に従って統合 snapshot の filtered Quick / Functional / 必要な opt-in lane を実行する。worker が閉じた範囲の再実装ではなく、path / Contract ID ownership、conflict、packet conformance、acceptance evidence の統合に集中する。

実装と標準検証が完了したら、implementation thread を閉じ、worktree を凍結して `repo-static-review` を一度呼ぶ。reviewer には intent、acceptance criteria、承認済み Test Contract Packet / Contract ID、base / head、worktree diff、red / negative-control を含む verification evidence、初回 timeout と再実行結果があればその両方を渡す。reviewer 実行中、ルートは repository の読み取り、検索、編集、build、test、format、stage、commit を行わず、重複チェックをしない。

reviewer は assertion が packet の authority、required invariant、allowed variation と一致するか、expected が implementation / current output / existing expected / translation copy / snapshot の写経になっていないか、plausible wrong implementation を区別できるかを確認する。assertion semantics が変わるのに packet がない、または packet と diff が矛盾する場合は受入条件上の test-design gap として扱う。

behavior / design finding を blocking とするには、次を一組で示す。

- **Classification**: P0 / P1 / acceptance-blocking P2
- **Authority**: 違反する user requirement / acceptance / spec / Contract ID
- **Reachability**: canonical production ingress -> production owner / consumer -> target state
- **Preconditions / assumptions**: 入口で成立する条件と system assumption
- **Observable impact**: UI / failure / persisted data / external effect / cleanup の差分
- **Evidence**: `file:line` と static trace / reproduction / negative control
- **Scope action**: current unit fix または root decision / replan

production reachability または observable impact を示せない runtime-state 仮説は `theoretical / unreachable` または out-of-scope とし、現在の unit の production / test 変更を要求しない。private/direct invocation、reflection、fake-only setup、code representability は reachability evidence ではない。invalid state を ingress で防げる場合は downstream recovery を要求せず、明示承認のない新しい state、retry、replay、rollback、recovery lifecycle、abstraction を non-blocking recommendation から現在の scope へ取り込まない。

FS+DB の finding は [file-db-consistency.md](file-db-consistency.md) section 7 の分類と終了基準を併用し、許容済みの残留リスクと契約違反を区別する。共通方針を理由に機能固有の既存補償を黙って取り除くことも、理論的な failure の追加だけで新しい復旧保証を要求することも行わない。

blocking finding の修正後は、影響範囲の Quick と必要な統合検証を行い、fix delta、previous snapshot、previous findings、更新した packet があればその authority を fresh reviewer へ渡す。同じ unit で2回の修正 review 後も新しい P1 が続く場合は、finding を継ぎ足さず、ownership、scope、acceptance、packet、unit 分割を再計画する。

## 9. agent が利用できない場合

ユーザー要件、承認済み decision、feature spec、repository の `AGENTS.md` とこの workflow は、汎用 Skill の推奨より優先する。Skill が停止、追加承認、scope 変更を要求するように見える場合は、まず指示の適用範囲と既存の承認を確認する。既に承認された scope 内で解決できるなら承認を取り直さず続行する。未決の判断が残る場合だけ、worker は具体的な `SKILL.md` の path、該当指示の引用、適用理由を root へ返し、依存する作業を止める。

custom agent が見つからない場合は、同じ model、reasoning effort、sandbox、role contract を明示した built-in / generic agent を代替にする。`test-contract-designer` の代替では、少なくとも oracle-first と repository-fit を別 turn または別 session に分け、Phase A で implementation body / current output / existing expected を使わない。

multi-agent 機能自体が利用できない場合は、ルートが同じ planning、independent oracle design、implementation、verification、fresh review の境界を順番に再現し、代替した箇所、oracle の独立性、未実施の negative control、独立 review の有無を結果へ明記する。agent が使えないことを理由に、未決 semantics、Test Contract Packet、static review を黙って省略しない。
