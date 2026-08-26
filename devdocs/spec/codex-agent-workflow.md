# Codex エージェント運用契約

最終更新: 2026-08-27

この文書は、BeMusicSeeker で複数段階の変更を計画・実装・レビューするときの Codex 運用の正本である。目的は、ルートエージェントへ要件・判断・統合責任を残しながら、必要な作業だけを適切なモデルへ委譲し、重複調査、実装バイアス、過剰な並列化、長い生ログによる rate limit と context の消費を抑えることである。

単純な質問、読み取りだけの確認、Markdown の軽微な修正まで機械的にサブエージェントへ渡す必要はない。production code、test harness、runner、設定の bounded implementation は原則 `implementation-worker` へ委譲し、ルート自身の書込みは計画文書、統合 conflict、機械的な handoff 修正などに限定する。

## 役割

| 役割 | agent / model | 主な責務 | 書込み |
| --- | --- | --- | --- |
| ルート | 現在の主セッション | ユーザー対話、要件と decision の整理、設計、最終計画、test contract の承認、割当、統合、統合検証、review 対応 | 統合時だけ |
| 計画点検 | `plan-clarifier` / Luna Low | draft plan の未決事項、危険な仮定、test-design gate、並列境界を短く点検 | なし |
| テスト契約設計 | `test-contract-designer` / Sol High | 実装から独立した oracle、allowed variation、wrong implementation、coverage placement を設計 | なし |
| 実装 | `implementation-worker` / Luna Max | 完成済みの bounded unit と承認済み Test Contract Packet を実装し、focused Quick と handoff を返す | 指定 path 内 |
| blocker 解決 | `issue-resolver` / Sol High | worker が発見した重大問題を調査・修正。observable semantics は変更しない | 指定 path 内 |
| 静的 review | `repo-static-review` / Sol High | 凍結 snapshot と Test Contract Packet を fresh / read-only で review | なし |

委譲の深さは `root -> implementation-worker -> issue-resolver` までとする。`plan-clarifier`、`test-contract-designer`、`repo-static-review` は root 直下の leaf とし、追加サブエージェントを起動しない。`issue-resolver` から先へ再帰しない。この契約は runtime の depth enforcement にかかわらず守る。

## 1. ルートによる要件整理

ルートは実装前に、会話と repository evidence を次の形へ正規化する。

- **Goal**: 何を変え、どの observable outcome を得るか。
- **Context**: 関連する file、symbol、spec、現行 route、既知の failure evidence。
- **Constraints**: 対象外、互換性、ownership、threading、永続化、安全性、作業中差分。
- **Done when**: behavior、削除する旧 route、必要な test lane、review、artifact。test を触る場合は independent authority、既存 coverage、shared resource、completion signal も含める。
- **Decision list**: 選択で observable behavior が変わる事項と、既に決まっている回答。

repository の正本、既存 test、履歴、合意済み方針から一意に決まる事項は、ルートまたは clarifier が調べて解決する。永続化、fallback、failure contract、ownership、互換性、破壊的操作などが一意に決まらない場合だけ、ユーザーへ具体的な質問を返す。current implementation や既存 test の expected value が存在することだけで、その behavior を仕様として確定しない。

## 2. draft plan と Luna Low の点検

ルートはまず自分で draft plan を作る。各 unit は method 数や file 数だけでなく、同じ behavior、owner、failure contract、verification scope が閉じる vertical slice とする。3つを超える subsystem、約15 files を超える見込み、同一巨大 fixture 内に複数領域が混在する場合は分割の signal とする。

サブエージェントへ実装を渡す計画では、完成前に `plan-clarifier` を原則一度だけ呼ぶ。次をまとめて渡す。

```text
Goal:
Context / repository evidence:
Constraints / out of scope:
Done when:
Decision list and resolved answers:
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

clarifier は計画や oracle を代作せず、repo で解けた事実、ユーザーへ必要な質問、`test-contract-designer` が必要か、authority packet の不足、安全な並列候補、衝突、replan trigger だけを返す。test を触る計画では、feature spec と production symbol から候補 fixture を絞る targeted search があり、既存 coverage、shared resource、completion signal が test delta へ反映されているかも点検する。既存 implementation や test expected から新しい期待値を提案しない。

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
Target owner / public seam / candidate fixture hints:
Inputs that must not become oracle authority:
- current implementation / runtime output
- existing expected values / snapshots
- current translation copy / repository prose
```

`test-contract-designer` は二段階で作業する。

1. **Oracle-first**: implementation body、current output、existing expected、translation values、snapshot を読まず、authority から behavior、failure、allowed variation、plausible wrong implementation、evidence strategy を凍結する。
2. **Repository-fit**: oracle を凍結した後だけ、targeted read で public seam、candidate fixture、shared resource、lane、completion signal、退役 test を決める。implementation body を読む場合も testability の確認に限定し、期待値を合わせ直さない。

`read-only` sandbox は書込みを防ぐが、implementation の読み取り自体を技術的に遮断するものではない。通常運用では agent prompt の phase boundary と親が渡す authority packetで独立性を保つ。data loss、security、protocol compatibility など高リスクの oracle は、可能なら Phase A を仕様・issue・public contract だけを置いた別 session / spec-only working directory で行い、その結果を Phase B へ渡す。

出力する `Test Contract Packet` には少なくとも次を含める。

- Packet ID、change class、authority、authority gap
- Contract ID ごとの observable behavior / failure、public seam、required invariant、allowed variation
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

書込み worker の既定は1つ、同時実行は最大2つとする。並列化は、速度向上が agent 起動・統合コストを上回り、次をすべて満たす場合だけ使う。

- writable path、生成物、schema / migration、shared fixture が重ならない。
- 一方の結果を見ないと他方の正しい実装が決まらない関係ではない。
- 同じ Test Contract Packet の Contract ID を別 worker が重複実装しない。
- 統合後の acceptance を一度に確認できる。
- 同じ巨大 file の別 method を触るだけの見かけ上の分割ではない。

read-heavy な探索、oracle design、inventory、log analysis は write-heavy な実装と比べて並列化しやすいが、test designer は implementation 前に packet を凍結するため、同じ unit の worker と同時には走らせない。各 worker へ他 worker の所有 path と Contract ID を渡し、実際の重複を発見した worker は編集を止めてルートへ返す。

`max_concurrent_threads_per_session = 3` は、2 worker と1つの nested resolver を上限とするために使う。clarifier と test designer は順番に実行して結果受領後に閉じ、review 前には implementation / resolver thread を残さない。

## 6. worker と issue resolver

`implementation-worker` は final plan の unit だけを実装し、反復中は対象を絞った Quick を使う。test semantics を変更する場合は承認済み packet を semantic input とし、fixture、helper、data setup、assertion API だけを repository に適合させる。current implementation、runtime output、existing expected、translation copy に合わせて packet の expected outcome や allowed variation を変更しない。

bugfix で既存 interface から再現できる場合は、production 修正前に regression test を作り、対象 bug の observable mismatch で失敗する red evidence を残す。base で test を構造上実行できない場合、behavior-preserving replacement、characterization では packet の targeted mutant / negative control を使う。test を触る場合は `devdocs/spec/test-authoring-contract.md` と近傍の `AGENTS.md` に従い、feature spec、production symbol、failure 文言から candidate fixture を絞り、既存の共通 helper を優先する。

ルートは worker と同じ path を同時に編集せず、統合責任を持つ。worker の返却内容は、生ログではなく変更概要、path、verification、実装 Contract ID、red / negative-control evidence、旧 route の replacement、残る risk の要約とし、test 変更時は `TEST CONTRACT`、`TEST COVERAGE`、`TEST SAFETY` を含める。

worker が `issue-resolver` を呼べるのは、次のような大きな問題に限る。

- data loss、security、起動不能、現実的な deadlock / race / compatibility regression
- final plan または packet の前提を崩す cross-cutting ownership / failure contract
- 所有 path 外の変更なしには安全に閉じられない問題
- packet を保ったまま解消できる可能性がある、packet と executable seam の技術的な実矛盾。単に current implementation が test に通らないこと、または authority / expected semantics の再決定が必要なことは resolver trigger ではない
- 通常の局所修正で解消しない deterministic failure、同一条件の2回目の timeout / failure、または実際の差分衝突

resolver は上記 trigger に該当する場合だけ使う。worker は編集を止め、blocker evidence、current diff、書込み可能 path、維持する invariant、packet / Contract ID、試した検証を1つの resolver へ渡して結果を待つ。resolver は packet の expected semantics を変更せず、fixture / adapter / public seam / ownership の技術的な修正に限定する。authority、observable semantics、allowed variation の決定や packet の修正が必要な場合は、worker と resolver の双方がルートへ返す。

## 7. verification failure の扱い

標準検証の時間予算、timeout retry、failure classification、必要な diagnostics は `devdocs/spec/testing-strategy.md` を正本とする。worker は exact command / filter / snapshot と artifact を handoff し、ルートが同じ方針で再実行または原因調査を判断する。deterministic failure、規定 retry の failure、同じ症状の再発は、resolver trigger または root の再計画条件として扱う。

red evidence は compile error、fixture setup failure、unrelated exception ではなく、対象 Contract ID の observable mismatch であることを確認する。negative control は packet の plausible wrong implementation と対応し、test が現在の実装に通るという事実だけを品質証拠にしない。

## 8. 統合、検証、review

worker 完了後、ルートは handoff と diff を確認して並列結果を統合し、`testing-strategy.md` に従って統合 snapshot の filtered Quick / Functional / 必要な opt-in lane を実行する。worker が閉じた範囲の再実装ではなく、path / Contract ID ownership、conflict、packet conformance、acceptance evidence の統合に集中する。

実装と標準検証が完了したら、implementation thread を閉じ、worktree を凍結して `repo-static-review` を一度呼ぶ。reviewer には intent、acceptance criteria、承認済み Test Contract Packet / Contract ID、base / head、worktree diff、red / negative-control を含む verification evidence、初回 timeout と再実行結果があればその両方を渡す。reviewer 実行中、ルートは repository の読み取り、検索、編集、build、test、format、stage、commit を行わず、重複チェックをしない。

reviewer は assertion が packet の authority、required invariant、allowed variation と一致するか、expected が implementation / current output / existing expected / translation copy / snapshot の写経になっていないか、plausible wrong implementation を区別できるかを確認する。assertion semantics が変わるのに packet がない、または packet と diff が矛盾する場合は受入条件上の test-design gap として扱う。

blocking finding の修正後は、影響範囲の Quick と必要な統合検証を行い、fix delta、previous snapshot、previous findings、更新した packet があればその authority を fresh reviewer へ渡す。同じ unit で2回の修正 review 後も新しい P1 が続く場合は、finding を継ぎ足さず、ownership、scope、acceptance、packet、unit 分割を再計画する。

## 9. agent が利用できない場合

custom agent が見つからない場合は、同じ model、reasoning effort、sandbox、role contract を明示した built-in / generic agent を代替にする。`test-contract-designer` の代替では、少なくとも oracle-first と repository-fit を別 turn または別 session に分け、Phase A で implementation body / current output / existing expected を使わない。

multi-agent 機能自体が利用できない場合は、ルートが同じ planning、independent oracle design、implementation、verification、fresh review の境界を順番に再現し、代替した箇所、oracle の独立性、未実施の negative control、独立 review の有無を結果へ明記する。agent が使えないことを理由に、未決 semantics、Test Contract Packet、static review を黙って省略しない。
