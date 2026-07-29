# BeMusicSeeker Unofficial Fork

## 正本

作業開始時は次を読む。

1. `devdocs/plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md`
2. active outcomeに対応する計画
3. `devdocs/plan/BeMusicSeeker_refactoring_plans/00_Codex共通実行ルール.md`
4. migration／distribution作業では依存関係台帳とblocker台帳

`PLAN_STATUS.md`は現在地と現行evidenceだけを持つ。過去のunit、commit、review logはGit historyに委ねる。

## 実行

- active implementation batchに`active`または`pending`があればplannerを起動せず、記載順に実装する。
- planner／reviewerはsingle-flightで使う。サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、stage、commitを凍結する。
- rootによる同scopeの独立再調査、第2planner、consensus取得を行わない。planner後は指定routeのbounded feasibility checkだけを行う。
- `NO_SAFE_UNIT`は無効。内部複雑性はowner、compatibility、publish、performance corridorへ分解する。
- method、package、DLL、callback、property、candidate profile一件だけをunitにしない。同じowner、invariant、verification scopeを持つ残件をまとめる。
- active batchの状態更新は対応code／config unitと同じcommitに含め、status-only progress commitを作らない。

## 設計

- stateとbehaviorを同じownerに置き、facadeのprivate state、lock、mutable collection、private operationを列挙するport／host／adapterを追加しない。
- WPFのfocus、selection、scroll、hit-test、virtualization、visual mapping、typed presentation requestのterminal applyはView／view-hostに残してよい。
- setting、DB、file format、update protocol、UI observable behavior、failure contractを明示的なmigration taskなしに変えない。

## 配布性能

- main appの最終profileは、公式publish機構だけを使った小規模な外部起動比較で、実用上有意な性能差の有無を確認して決める。
- production log内の`startup_ready_* elapsedMs`だけでend-to-end startupを判定しない。process startからmain-window ready／`startup_ready_operable`検出までをharness側で計測する。
- folder Self-contained、managed bundle＋native隣接、native self-extract single-file、ReadyToRun有無は同じfixtureでfresh install／warm cacheを分けて比較する。
- harness変更後は一candidateの最小smoke、全candidateの少数回比較の順に進める。実用差の境界にある上位候補だけを少数追加測定し、統計精度のための反復を目的化しない。
- 明確な性能優位がなければ、native self-extractを避け、同等性能ならmanaged bundleで配布file数を減らし、ReadyToRunの起動特性を加味して選ぶ。`folder-il`を根拠のないfallbackにしない。
- standard hostが要求するexe隣接managed／runtime fileを、数だけを理由にfindingにしない。
- managed DLLを`libs`へ移す独自`AssemblyLoadContext`／`AssemblyResolve`、private probing、deps書換え、post-publish relocation、wrapper launcherを追加しない。
- application-owned assetは`libs/x64`、`native`、`lang`等のowner directoryへ置く。updaterは低頻度かつtransaction handoff用なので、updater固有の問題がない限りsingle-fileを維持する。

## Engineering と手動受入れ

- `.NET Desktop Runtime`未導入machine／VMでの確認、署名、公開release、proprietary license証跡確認はpost-engineeringのユーザー作業である。
- これらをactive outcome、planner停止条件、`EXTERNAL_BLOCKER`、CodexのEngineering Gateへ入れない。`POST_MIGRATION_MANUAL_ACCEPTANCE.md`へhandoffする。

## Git・Release Freeze

- rootだけがwriter／stager／committerとなる。unrelatedな差分へ触れない。
- code／config unitは検証とfresh read-only reviewで重大指摘がなくなった後にcommitする。active outcomeが未完ならunit commitを応答境界にしない。
- `git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで禁止する。
