# Codex エージェント運用契約

最終更新: 2026-08-23

この文書は、BeMusicSeeker で複数段階の変更を計画・実装・レビューするときの Codex 運用の正本である。目的は、ルートエージェントへ要件・判断・統合責任を残しながら、必要な作業だけを適切なモデルへ委譲し、重複調査、過剰な並列化、長い生ログによる rate limit と context の消費を抑えることである。

単純な質問、読み取りだけの確認、Markdown の軽微な修正まで機械的にサブエージェントへ渡す必要はない。production code、test、runner、設定の bounded implementation は原則 `implementation-worker` へ委譲し、ルート自身の書込みは計画文書、統合 conflict、機械的な handoff 修正などに限定する。

## 役割

| 役割 | agent / model | 主な責務 | 書込み |
| --- | --- | --- | --- |
| ルート | 現在の主セッション | ユーザー対話、要件と decision の整理、設計、最終計画、割当、統合、統合検証、review 対応 | 統合時だけ |
| 計画点検 | `plan-clarifier` / Luna Low | draft plan の未決事項、危険な仮定、並列境界を短く点検 | なし |
| 実装 | `implementation-worker` / Luna Max | 完成済みの bounded unit を実装し、focused Quick と handoff を返す | 指定 path 内 |
| blocker 解決 | `issue-resolver` / Sol High | worker が発見した重大問題を調査・修正 | 指定 path 内 |
| 静的 review | `repo-static-review` / Sol High | 凍結 snapshot を fresh / read-only で review | なし |

委譲の深さは `root -> implementation-worker -> issue-resolver` までとする。`plan-clarifier` と `repo-static-review` は追加サブエージェントを起動せず、`issue-resolver` から先へ再帰しない。この契約は runtime の depth enforcement にかかわらず守る。

## 1. ルートによる要件整理

ルートは実装前に、会話と repository evidence を次の形へ正規化する。

- **Goal**: 何を変え、どの observable outcome を得るか。
- **Context**: 関連する file、symbol、spec、現行 route、既知の failure evidence。
- **Constraints**: 対象外、互換性、ownership、threading、永続化、安全性、作業中差分。
- **Done when**: behavior、削除する旧 route、必要な test lane、review、artifact。test を触る場合は既存 coverage、shared resource、completion signal も含める。
- **Decision list**: 選択で observable behavior が変わる事項と、既に決まっている回答。

repository の正本、既存 test、履歴、合意済み方針から一意に決まる事項は、ルートまたは clarifier が調べて解決する。永続化、fallback、failure contract、ownership、互換性、破壊的操作などが一意に決まらない場合だけ、ユーザーへ具体的な質問を返す。

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
Test delta when tests change:
- behavior / failure contract
- candidate existing fixture and `extend / replace / new`
- shared resource / lane / completion signal
- retired test / helper / route
Known open questions:
```

clarifier は計画を代作せず、repo で解けた事実、ユーザーへ必要な質問、安全な並列候補、衝突、replan trigger だけを返す。test を触る計画では、feature spec と production symbol から候補 fixture を絞る targeted search があり、既存 coverage、shared resource、completion signal が test delta へ反映されているかも点検する。ルートは回答を decision list と draft units へ反映してから final plan とする。回答で ownership や unit 境界が大きく変わった場合だけ、差分を限定して再点検する。repo で解ける事項のために planner loop を繰り返さない。

## 3. final plan の必須項目

実装 worker へ渡す各 unit には、少なくとも次を含める。

1. observable outcome と完了条件
2. 書込み所有 path と、必要な read-only 参照範囲
3. 先行 unit、他 worker、生成物との依存関係
4. 変更する production / test route と、同時に退役する旧 route
5. 維持する UI、persisted data、file / protocol compatibility、threading、shutdown、failure invariant
6. 追加・更新する behavior test、具体的な Quick filter、統合時の Functional / opt-in lane
7. test を触る場合の coverage ledger: behavior / failure contract、候補 fixture、`extend / replace / new`、shared resource / lane、completion signal、退役 test / helper / route
8. worker が返す旧 test / route と replacement の対応表
9. static review の intent、base / head、review scope
10. 実装を止めて再計画・ユーザー判断へ戻す具体的な evidence
11. 統合 conflict を避ける path ownership と handoff 順

計画は「調査して適宜直す」だけで終わらせず、worker が observable semantics を推測せずに着手できる粒度まで閉じる。

## 4. 並列作業

書込み worker の既定は1つ、同時実行は最大2つとする。並列化は、速度向上が agent 起動・統合コストを上回り、次をすべて満たす場合だけ使う。

- writable path、生成物、schema / migration、shared fixture が重ならない。
- 一方の結果を見ないと他方の正しい実装が決まらない関係ではない。
- 統合後の acceptance を一度に確認できる。
- 同じ巨大 file の別 method を触るだけの見かけ上の分割ではない。

read-heavy な探索、inventory、log analysis は並列化しやすい。write-heavy な変更は conflict と coordination cost が増えるため慎重に扱う。各 worker へ他 worker の所有 path を渡し、実際の重複を発見した worker は編集を止めてルートへ返す。

`max_concurrent_threads_per_session = 3` は、2 worker と1つの nested resolver を上限とするために使う。完了した clarifier / worker / resolver thread は要約を受け取った時点で閉じ、review 前に実装 thread を残さない。

## 5. worker と issue resolver

`implementation-worker` は final plan の unit だけを実装し、反復中は対象を絞った Quick を使う。test を触る場合は `devdocs/spec/test-authoring-contract.md` と近傍の `AGENTS.md` に従い、feature spec、production symbol、failure 文言から candidate fixture を絞って読む。最初から test project 全体を通読したり、既存の共通 helper を local に複製したりしない。ルートは worker と同じ path を同時に編集せず、同じ調査や test を重複実行しない。worker の返却内容は、生ログではなく変更概要、path、verification、旧 route の replacement、残る risk の要約とし、test 変更時は `TEST COVERAGE` と `TEST SAFETY` を含める。

worker が `issue-resolver` を呼べるのは、次のような大きな問題に限る。

- data loss、security、起動不能、現実的な deadlock / race / compatibility regression
- final plan の前提を崩す cross-cutting ownership / failure contract
- 所有 path 外の変更なしには安全に閉じられない問題
- 通常の局所修正で解消しない deterministic failure、同一条件の2回目の timeout / failure、または実際の差分衝突

通常の symbol 検索、compile error、最初の単発 timeout、style、局所的な test failure は resolver の理由にしない。worker は編集を止め、blocker evidence、current diff、書込み可能 path、維持する invariant、試した検証を1つの resolver へ渡して結果を待つ。resolver が observable semantics の決定を必要と判断した場合は、worker と resolver の双方が編集を止めてルートへ返す。

## 6. timeout の扱い

標準検証の詳細は `devdocs/spec/testing-strategy.md` を正本とする。単発の timeout では、process tree を停止し、active / last observed test、経過時間、console / TRX / blame artifact を保存したうえで、残留 process がないことを確認し、同じ command・filter・budget を一度だけ再実行する。

2回目が budget 内で成功し、同じ症状の再発や artifact 上の決定的 evidence がなければ、初回を一過性のマシン負荷として記録し、本筋へ戻る。2回目も timeout / failure、同じ症状が再発、active test が停止、または artifact が共有 state・固定待ち・競合・I/O・入力規模の問題を示す場合は、本筋を止めて調査する。timeout 延長、無制限の再試行、並列度低下だけによる隠蔽は行わない。timeout 以外の deterministic failure は最初から原因を確認する。

## 7. 統合、検証、review

worker 完了後、ルートは handoff と diff を軽く確認し、並列結果を統合する。許されるのは、path ownership の確認、機械的 conflict の解消、明白な欠落の確認、統合 snapshot に対する filtered Quick / Functional / 必要な opt-in lane である。worker が既に閉じた範囲を同じ深さで再実装・再調査しない。runner、lane、並列化、fixture 配置を変更した場合の3回連続 Functional は、同一の最終 snapshotで数える。途中で failure を修正した場合は修正前の pass を破棄し、1回目からやり直す。

実装と標準検証が完了したら、実装 thread を閉じ、worktree を凍結して `repo-static-review` を一度呼ぶ。reviewer には intent、acceptance criteria、base / head、worktree diff、verification evidence、初回 timeout と再実行結果があればその両方を渡す。reviewer 実行中、ルートは repository の読み取り、検索、編集、build、test、format、stage、commit を行わず、重複チェックをしない。

blocking finding の修正後は、影響範囲の Quick と必要な統合検証を行い、fix delta、previous snapshot、previous findings を fresh reviewer へ渡す。同じ unit で2回の修正 review 後も新しい P1 が続く場合は、finding を継ぎ足さず、ownership、scope、acceptance、unit 分割を再計画する。

## 8. agent が利用できない場合

custom agent が見つからない場合は、同じ model、reasoning effort、sandbox、role contract を明示した built-in / generic agent を代替にする。multi-agent 機能自体が利用できない場合は、ルートが同じ planning、implementation、verification、fresh review の境界を順番に再現し、代替した箇所と未実施の独立 review を結果へ明記する。agent が使えないことを理由に、未決 semantics の確認や static review を黙って省略しない。
