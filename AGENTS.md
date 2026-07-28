# BeMusicSeeker Unofficial Fork

## 正本

作業開始時は次を読む。

1. `devdocs/plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md`
2. active outcomeに対応する計画
3. `devdocs/plan/BeMusicSeeker_refactoring_plans/00_Codex共通実行ルール.md`
4. migration中は依存関係台帳とblocker台帳

`PLAN_STATUS.md`は現在地だけを持つ。過去のunit、commit、review logはGit historyに委ねる。

## 実行

- active implementation batchに`active`または`pending`があればplannerを起動せず、記載順に実装する。
- planner／reviewerはsingle-flightで使う。サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、stage、commitを凍結する。
- rootによる同scopeの独立再調査、第2planner、consensus取得を行わない。planner後は指定routeのbounded feasibility checkだけを行う。
- `NO_SAFE_UNIT`は無効。内部複雑性はowner／compatibility corridorへ分解する。
- method、package、DLL、callback、property一件だけをunitにしない。同じowner、behavior、compatibility invariant、verification scopeを持つ残件をまとめる。
- active batchの状態更新は対応code unitと同じcommitに含め、status-only progress commitを作らない。

## Engineering と手動受入れ

- `.NET Desktop Runtime`未導入machine／VMでの確認、署名、公開release、proprietary license証跡確認はpost-engineeringのユーザー作業である。
- これらをactive outcome、planner停止条件、`EXTERNAL_BLOCKER`、CodexのEngineering Gateへ入れない。`POST_MIGRATION_MANUAL_ACCEPTANCE.md`へhandoffして作業を継続・完了する。
- `EXTERNAL_BLOCKER`にできるのは、外部入力がなければコード／自動検証そのものを選択または実行できない場合だけである。

## 設計・配布

- stateとbehaviorを同じownerに置き、facadeのprivate state、lock、mutable collection、private operationを列挙するport／host／adapterを追加しない。
- WPFのfocus、selection、scroll、hit-test、virtualization、visual mapping、typed presentation requestのterminal applyはView／view-hostに残してよい。
- setting、DB、file format、update protocol、UI observable behavior、failure contractを明示的なmigration taskなしに変えない。
- 配布候補はbuild outputではなくwin-x64 Self-contained publish outputとする。
- folder publishでmanaged DLLがexe隣接になるのは標準host layoutである。`libs`へ移すための独自`AssemblyLoadContext`／`AssemblyResolve`、probing、deps書換え、post-publish relocation、wrapper launcherを追加しない。
- 配布物の簡素化は公式single-file publishだけを有限に評価する。特殊回避が必要なら`NOT_ADOPTED`としてfolder Self-containedへ戻すことを正常完了とする。

## Git・Release Freeze

- rootだけがwriter／stager／committerとなる。unrelatedな差分へ触れない。
- code unitは検証とfresh read-only reviewで重大指摘がなくなった後にcommitする。unit commitは内部checkpointであり、active outcomeが未完なら応答境界にしない。
- `git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで禁止する。
