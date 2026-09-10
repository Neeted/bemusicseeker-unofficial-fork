# R1: resource-health 全件入力の必要時取得

## Goal / scope

ライブラリ変更後、消費されない全 owned 譜面の resource-health 入力を作らない。
2026-09-11 のユーザー依頼で指定された添付計画書の R1 と参照会話を対象とする。
R2～R7、DB、LR2、resource reverse lookup、操作受付、永続化形式、公開単位は対象外。
開始時の worktree は clean。コミット・公開・version 更新は行わない。

ユーザー指定により、計画点検・テスト設計・実装・blocker 解決は root が担当する。
静的レビューだけを、履歴を fork しない ChatGPT Web Pro に依頼する。

## Decision / constraints

- no-op / invalidate / defer / 成功する delta は full 入力を取得しない。
- full rebuild と、既存契約で full fallback が必要な場合だけ full 入力を取得する。
- 延期・失効・通知、writer 予約、入力の世代・currentness、lock 順序を維持する。
- 新しい永続 state、generation、retry、queue、非同期 workflow は追加しない。
- full 入力が必要な場合の O(C) は残る。不要な経路は全体 C ではなく差分に依存させる。
  通常規模は約21万譜面。大規模の操作時間・改善率は小規模機能テストから推定しない。

## Test Contract Packet: R1（oracle-first）

変更分類は性能不具合修正。恒久テストは既存 fixture の更新・追加を必要とする。
full 入力の非呼出しは出力値だけでは保証できないため、小さい factory / 列挙観測で検証する。
期待結果はユーザーが実装対象に指定した R1 の受入条件と既存互換性から定め、
production body・current output・既存 expected を読む前に root が以下を承認した。
資料内の実装手順は参考であり、新しい製品仕様の authority としない。

| Contract ID | 必須結果 | 許容する差 | 誤実装 | evidence |
| --- | --- | --- | --- | --- |
| R1-SKIP | 少数 UpdatedTargets を持つ defer、invalidate、no-op で全 owned 入力 factory が呼ばれず、既存の延期・失効・通知結果を保つ | 内部の型・配置、ログ文言、実行時間 | dispatch が先に full 入力を取得する、必要な失効まで省く | production route の実 owner seam と library 入口の小規模回帰テスト。repository-fit で限定した negative control を確認 |
| R1-DELTA | 成功する delta は full 入力を取得せず、対象差分を反映する | 内部構造、取得関数の形 | 未使用の fallback 入力を先に全件生成する | 既存 delta fixture に取得回数と結果の観測を追加 |
| R1-FULL | 実 full rebuild / 必要な fallback は全 owned の不変入力を使い、世代不一致・writer 競合時は誤公開しない | 内部構造、正しい取得タイミングの変更 | full 入力を常に省く、古い入力を公開する、lock 下で別 owner を待つ | 既存 full / freshness / writer 予約 coverage と必要な focused case |

### Repository-fit / root 承認（2026-09-11）

入口は duplicate merge → `CatalogMaintenanceOwner.ApplyMaintenance`（DeferOnUpdates）→
receipt → `DispatchOwnedChartCollectionMutation` → `DispatchResourceHealthIndexMutation` →
`ResourceHealthIndexOwner.Apply`。少数 target でも全 owned 入力を作ることが操作完了時間へ影響する。
warning ignore / install maintenance は同 owner の delta / invalidate、明示 maintenance と
hydration は full rebuild、確定済み表示の読み取りは `GetResourceHealthIndexSnapshotForView` へ進む。
入口の single writer 予約・storage guard・入力 mutation scope は既存 owner が所有し、
この変更で別 owner の同期待ち、受付、公開単位を増やさない。

全件取得は `Apply` の実 full 分岐へ渡す同期 provider にする。全件入力が既に指定されている場合は
その版を検証し、古い入力を新しい入力へ黙って置き換えない。owned collection 変更後の古い full 入力は
従来の再取得意図を保って破棄し、必要になった分岐だけで取り直す。取得は state lock の外で行い、
immutable projection の後に current version を読み、既存の build 前・publication 時の照合を維持する。
no-op / invalidate / defer / delta 成功 / delta 失敗時 invalidate は full provider を呼ばない。

| Contract ID | Owner / candidate coverage | 配置と追加観測 | Shared resource / lane | Completion signal | 退役 |
| --- | --- | --- | --- | --- | --- |
| R1-SKIP | ResourceHealthIndexOwnerTests、BmsLibraryDuplicateServiceTests の実 DB・FS merge | owner の既存 invalidate / defer を extend、no-op を追加。merge の既存通知・予約・表示結果を再実行 | owner は local 値のみ、merge は既存 temporary DB / directory。filtered Quick / Functional | 同期 Apply / MergeChartDirectory の return と既存通知 | なし |
| R1-DELTA | ResourceHealthIndexOwnerTests の lease version delta | extend: full provider 非呼出しと追加対象の反映 | local 値のみ、filtered Quick / Functional | Apply return | なし |
| R1-FULL | ResourceHealthIndexOwnerTests、ResourceHealthFullOwnedTargetFreshnessTests、OwnedChartCollectionLibraryMutationTests | full / fallback の取得有無、取得後の collection version 確認、指定済み stale 入力拒否、writer 重複、公開済み入力の不変性を extend。既存 writer・freshness coverage も利用 | owner は local 値のみ、library は既存 fixture。filtered Quick / Functional | Apply / EnsureCurrent return | なし |

候補と共通 `TestBmsFactory` を確認済み。新しい診断 state、共有 hook、reflection、source assertion は追加しない。
旧 API は取得 provider を公開しておらず、既存 merge の結果 assertion だけでは未使用入力を識別できない。
そのため R1-SKIP の red は、先行取得を owner 入口へ戻す限定 negative control で代替する。
期待結果・authority は変更せず、非呼出し assertion が eager capture を落とすことを確認する。
これは作業量の回帰検証であり、大規模 wall-clock 測定の代用ではない。

関連 filter は `FullyQualifiedName~ResourceHealth|FullyQualifiedName~BmsLibraryDuplicateServiceTests|FullyQualifiedName~OwnedChartCollectionLibraryMutationTests|FullyQualifiedName~ChartResourceSnapshotTests`。

## Verification / done when

R1 の production route を更新し、不要な先行全件入力を退役する。
関連 filtered Quick、最終 Functional、差分・UTF-8 / LF・whitespace を確認する。
変更後の仕様と検証記録を残し、凍結 snapshot の ChatGPT Web Pro 静的レビューを完了する。
writer / generation / failure の意味変更が必要になった場合は実装を広げず再計画する。

## 実装・検証記録

- `BMSLibrary` の先行取得2箇所を除去。`Apply` の実 full 分岐へ同期 provider を渡し、
  不足時だけ immutable snapshot と取得後の current version を用意する。指定済み full 入力の版検証は維持。
- 関連 Quick: 140/140 成功。`artifacts/verification/tests-quick-20260911-045028/functional/results.trx`。
  restore 3.4秒、build + test 97.2秒、テスト表示時間13.8929秒。大規模性能の測定ではない。
- 限定 negative control: 旧 dispatcher の先行取得条件を一時的に owner 入口へ戻し、
  defer 2件・invalidate 1件・成功 delta 1件が全件取得拒否 assertion で失敗、no-op 2件は成功。
  `artifacts/verification/tests-quick-20260911-045252/functional/results.trx`、
  `artifacts/verification/r1-eager-control.log`。この一時変更は除去済み。
- TEST CONTRACT: R1-SKIP / R1-DELTA / R1-FULL。oracle deviation なし。
  red の手段だけを repository-fit に記した限定 negative control へ変更。
- TEST COVERAGE / SAFETY: 既存 owner fixture の更新・追加と既存 library ingress の再実行。
  新規共有 state・reflection・source assertion・固定待ち・並列度変更なし。退役 test なし。
- 最終 Functional: `pwsh -NoProfile -File ./scripts/verify-refactor.ps1 -Mode Functional` が exit 0。
  6 host 合計4,713件、成功4,702件、スキップ11件、失敗0件。
  retained ExitTime に基づく test execution は240.7秒。180秒 reporting target は超過したが、
  300秒 hard budget 内の成功であり再実行はしない。restore 3.2秒、build 36.6秒はこの予算外。
  format 検査成功、Roslynator は診断0件、build は警告84件・エラー0件。
  証跡は `artifacts/verification/tests-functional-20260911-050132/` と
  `artifacts/verification/r1-functional-20260911-050130.log`。
- 最終差分の `git diff --check` 成功。変更したコード・テスト・資料6ファイルの UTF-8 / LF を確認。
- 約21万譜面の実データにおける操作完了時間と改善率は未測定。機能検証の成功を大規模性能 pass としない。
