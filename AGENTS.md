# BeMusicSeeker Unofficial Fork

## 正本

作業開始時は次を読む。

1. `devdocs/plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md`
2. active outcomeに対応する計画
3. `devdocs/plan/BeMusicSeeker_refactoring_plans/00_Codex共通実行ルール.md`
4. 必要な台帳

`PLAN_STATUS.md`は現在地と現行evidenceだけを持つ。過去のunit、commit、review logはGit historyへ委ねる。

## 実行

- active implementation batchに`active`または`pending`があればplannerを起動せず、記載順に実装する。
- planner／reviewerはsingle-flightで使う。サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、stage、commitを凍結する。
- rootによる同scopeの独立再調査、第2planner、consensus取得を行わない。planner後は指定routeのbounded feasibility checkだけを行う。
- `NO_SAFE_UNIT`は無効。内部複雑性はowner、wait graph、behavior corridorへ分解する。
- method、callback、lock、wait、property一件だけをunitにしない。同じowner、待機graph、invariant、verification scopeを持つ残件をまとめる。
- active batchの状態更新は対応code／test unitと同じcommitに含め、status-only progress commitを作らない。
- Windows UI smokeでは対象作業の直前にComputer Useを開始し、repository内の検証対象exeとそのwindowを一意に選択する。smoke終了時は対象appを閉じ、Computer Use runtimeも終了する。次のUI検証時は新しいruntimeとして再開する。
- Computer UseがEscape等のユーザー操作でcancelされた場合は、古いwindow／element stateを破棄し、対象appとwindowを再列挙して同じsmokeを再試行する。cancel自体を応答境界やunit停止条件にしない。

## Concurrency の非交渉条件

- model lock、DB transaction、reservation、operation gate、collection mutation scopeを保持したまま、別threadのUI executionを同期的に待たない。
- `PropertyChanged`、event、dialog、UI scheduler、View callback、別owner callbackをowner lock内から同期実行しない。
- background producerはversion／immutable change factをqueueして戻る。UI反映はcoalesced asynchronous drainで行う。
- UI反映完了を待つ必要があるshutdown／test routeは、すべてのmodel guardを解放した後だけ明示的にawaitする。
- timeout、retry、`TryEnter`、notification skip、追加`Task.Run`、lock recursion変更でdeadlockを隠さない。wait cycleを構造的に除去する。
- file I/O中のcatalog／package writer lockは、実際の整合性要件を保ったまま短縮できる範囲だけ短縮する。phaseや抽象を増やすこと自体を目的にしない。

## Recovery と収束

- 今回のpre-release deadlockで生じた中途状態を救済するためだけに、durable operation journal、marker、recovery coordinator、専用reconciliation stateを追加しない。
- release contractは、対象operationがdeadlockせず正常完了することである。
- 異常終了後のfilesystem／song DB差分は、既存の起動時file diff（既定有効）または明示的な「ファイル差分確認」／再初期化を収束経路とする。
- startup scanを無効にしたユーザー設定を上書きしない。無効時は既存のmanual diff routeを使う。
- in-memoryのversion／change factはUI decouplingに使ってよいが、永続的な回復基盤へ拡張しない。

## MVVM と挙動

- stateとbehaviorを同じownerに置き、facadeのprivate state、lock、mutable collection、private operationを列挙するport／host／adapterを追加しない。
- WPFのfocus、selection、scroll、hit-test、virtualization、visual mapping、typed presentation requestのterminal applyはView／view-hostに残してよい。
- setting、DB、file format、update protocol、supported external contractは明示的なmigration taskなしに変えない。
- deadlock、無期限wait、lock中dialog、成功後の誤failure、観測不能なbackground failureは挙動互換として保存しない。
- すべてのcommandへ新しいprogress、cancel、operation stateを機械的に追加しない。実際に長時間blockingまたは不自然なuser-visible behaviorがあるrouteだけを修正する。
- raw lock countのUI bindingはcandidateであり、それ自体を一律違反にしない。callback cycle、誤ったcommand状態、UI thread通知を作る場合に限りsemantic stateへ置換する。

## 配布と手動受入れ

- .NET 10、selected main-app profile、updater profileはactive concurrency outcomeと無関係な変更を加えない。
- managed DLLを`libs`へ移す独自loader、probing、deps書換え、post-publish relocation、wrapper launcherを追加しない。
- `.NET Desktop Runtime`未導入machine／VM、署名、公開release、proprietary license証跡はpost-engineeringのユーザー作業であり、active outcomeや`EXTERNAL_BLOCKER`へ入れない。

## Git・Release Freeze

- rootだけがwriter／stager／committerとなる。unrelatedな差分へ触れない。
- code／config unitは検証とfresh read-only reviewで重大指摘がなくなった後にcommitする。active outcomeが未完ならunit commitを応答境界にしない。
- `git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで禁止する。
