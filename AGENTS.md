# BeMusicSeeker Unofficial Fork

## 正本と作業範囲

- リファクタリング中は `devdocs/plan/BeMusicSeeker_refactoring_plans/BeMusicSeekerリファクタリング計画.md`、`PLAN_STATUS.md`、`00_Codex共通実行ルール.md`、`DOTNET10_MIGRATION_BLOCKERS.md` を正本とする。
- `PLAN_STATUS.md` の active outcome、execution anchor、active implementation batch を読み、batch の未完 unit を順に進める。unit commit は内部 checkpoint であり、outcome が未完ならユーザーへの応答境界にしない。
- execution anchor は package 内の安定した phase を示す。planner が作った一時的な unit 名を cursor として増殖させない。active batch に未完 unit がある間は planner を再起動しない。
- 差分の小ささや行数ではなく、state と behavior が同じ owner に収まり、production route と behavior testを閉じ、旧 routeを減らせる実装単位を選ぶ。
- DB schema / data、setting key / serialized value、外部ファイル形式、UI observable behavior、失敗契約、明示的にサポートする SDK / plugin / CLI / IPC / COM / automation contract の意味を変える必要がある場合だけ、実装前にユーザーへ確認する。
- 意味の変わる fallback を追加せず、既存の失敗契約を維持する。

## 互換性契約

- C# の `public` / `protected` 修飾子だけでは互換性契約とみなさない。
- 同一リポジトリ内の production caller、tests、XAML binding、resource lookup、reflection string は内部 consumer とする。同じ unit で全 consumer を更新し、検証できる場合は call shape を変更してよい。
- test または過去の内部 call shape のためだけに旧 public member、forwarding property、`[Obsolete]` wrapper を残さない。
- 互換性を理由に escalation する前に、具体的な supported out-of-repository consumer を特定する。

## Git と Release Freeze

- active outcome の code unit は、検証と fresh read-only reviewで重大指摘がなくなった後、ユーザー承認待ちなしでcommitしてよい。commit subjectにoutcome IDを含める。
- planning / operation docs と agent設定だけの変更は、構文、参照、whitespace、`git diff --check`を確認して独立commitにしてよい。production / test / build / resource / verification scriptに差分がなければbuild、test、analyzer、code reviewは行わない。
- active batchの状態更新は対応するcode unitの同じcommitに含め、status-onlyの進捗commitを作らない。
- unrelatedな既存差分を変更、stage、commitしない。
- Refactoring Completion Gate前は`git push`、tag、release、publish、version / release notesのリリース目的変更、配布package作成を禁止する。Gate後もユーザーの明示指示なしには開始しない。

## 実装原則

- productionの通常経路へ接続し、同じunitで担当corridorの旧owner、旧route、旧binding、callback host、test-only production seamのいずれかを削除する。
- method 1個、callback 1個、property 1個、test 1個だけをunitにしない。同じowner、同じbehavior、同じverification scopeの隣接routeは一つのunitへまとめる。
- partial split、interface / adapter / hostの追加だけで責務移管を完了扱いにしない。
- WPFのfocus、selection、scroll、hit-test、virtualization、visual mapping、typed presentation requestのterminal applyを、行数やevent数のためだけにView外へ移さない。
- C# symbol rename、signature refactor、type/member moveでは `.agents/skills/csharp-semantic-refactor/SKILL.md` を使用し、identifierの一括text replacementを行わない。
- `NLog`を新たに直接参照せず、既存のlogging boundaryを使う。通常操作で表示する新規UI文言は既存のresource / localization手順に従う。

## 検証とサブエージェント

- code unitの標準入口は`scripts/verify-refactor.ps1`とする。unit中は`-Mode Quick`、outcome完了候補および共有ViewModel / DB / settings / dispatcher / concurrency変更では`-Mode Full`を使う。
- testは標準入口から起動し、300秒以内に終了しなければprocess treeを停止して失敗として扱う。失敗や全体実行時だけ失敗するtestをbaseline / flakyとして放置しない。
- UI smokeはrepository内のRelease build `bin\x64\Release\net472\BeMusicSeeker.exe`だけを対象にする。
- plannerとreviewerはsingle-flightで使い、同時にactiveにするサブエージェントは1つだけとする。起動から結果受領までrootはrepositoryの読み取り、検索、編集、build、test、format、analyzer、stage、commitを凍結する。
- plannerはexecution anchorに対する有限なclosure inventoryと2〜4 unitのactive batchを作る。active batchに未完unitがある間は呼び直さない。rootによる独立調査、第2planner、consensus取得を行わない。
- rootだけをwriter / stager / committerとする。planner結果後は指定されたroute / symbol / testのbounded feasibility checkと実装へ進む。
- reviewerはfrozen snapshotをread-onlyで評価する。重大指摘の修正後は新しいsnapshotとしてfresh reviewerに再レビューさせる。
