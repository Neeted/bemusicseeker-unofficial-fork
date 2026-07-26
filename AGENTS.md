# BeMusicSeeker Unofficial Fork

## 正本

作業開始時は次をこの順に読む。

1. `devdocs/plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md`
2. active outcomeに対応する計画
   - terminal refactoring: `BeMusicSeekerリファクタリング計画.md`
   - .NET 10 migration: `BeMusicSeeker_NET10移行計画.md`
3. `00_Codex共通実行ルール.md`
4. migration中は`DOTNET10_DEPENDENCY_REGISTER.md`と`DOTNET10_MIGRATION_BLOCKERS.md`

`PLAN_STATUS.md`は現在地だけを持つ。過去のunit、commit、review logを追記しない。

## 実行

- active implementation batchに`active`または`pending`があればplannerを起動せず、記載順に実装する。
- plannerとreviewerはsingle-flightで使う。サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、stage、commitを凍結する。
- rootによる同scopeの独立再調査、第2planner、consensus取得を行わない。planner後は指定routeのbounded feasibility checkだけを行う。
- `NO_SAFE_UNIT`は無効。内部複雑性はowner corridorに分解する。停止できるのは具体的な`EXTERNAL_BLOCKER`だけとする。
- method、package、DLL、callback、property一件だけをunitにしない。同じowner、behavior、compatibility invariant、verification scopeを持つ残件をまとめる。
- active batchの状態更新は対応code unitと同じcommitに含め、status-only progress commitを作らない。

## 設計・互換性

- stateとbehaviorを同じownerに置き、facadeのprivate state、lock、mutable collection、private operationを列挙するport／host／adapterを追加しない。
- WPFのfocus、selection、scroll、hit-test、virtualization、visual mapping、typed presentation requestのterminal applyはView／view-hostに残してよい。
- setting keyとserialized value、DB schemaと既存データ、playlist／chart形式、update protocol、UI observable behavior、失敗契約を明示的なmigration taskなしに変えない。
- public修飾子だけを外部互換性契約とみなさない。supported out-of-repository consumerを特定できない内部APIは、同じunitで全consumerを更新してよい。
- migrationではbuild outputではなく`dotnet publish`のSelf-contained outputを配布候補として検証する。main appは当面folder publish、untrimmed、non-single-fileとする。

## Git・Release Freeze

- rootだけがwriter／stager／committerとなる。unrelatedな差分を変更、stage、commitしない。
- code unitは検証とfresh read-only reviewで重大指摘がなくなった後にcommitしてよい。unit commitは内部checkpointであり、active outcomeが未完なら応答境界にしない。
- planning／agent設定だけの変更は、構文、参照、whitespace、`git diff --check`を確認して独立commitにしてよい。
- `git push`、tag、release、公開publish、version／release notes変更、配布物の公開は、ユーザーの明示指示まで禁止する。
