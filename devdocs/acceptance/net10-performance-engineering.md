# .NET 10 Performance Engineering Evidence

[起動仕様](../spec/startup-initialization-flow.md) / [distribution](./net10-distribution-performance.md) / [現在地](../plan/BeMusicSeeker_refactoring_plans/PLAN_STATUS.md)

この文書は下記 Reviewed snapshot の受入記録である。逐次unit履歴はGit historyへ委ね、後続HEADの性能passを保証しない。データ規模・処理速度の優先順位・共通受入条件は [performance-and-scale.md](../spec/performance-and-scale.md) を正本とし、本文の配布形式selection thresholdを全操作の退行許容値へ流用しない。

## Reviewed snapshot

```text
HEAD: c9fcb9a4f6a523688c977264a6a78cf85be07665
logs:
  .tmp/20260801_folder-r2r_log
  .tmp/20260801_bundle-r2r_log
```

各logにはPC起動後初回起動と同じPC sessionの2回目起動が含まれる。

## Startup result

| Layout | Run | Install estimation ready | Ready operable | Required initialization complete | Post maintenance complete |
| --- | --- | ---: | ---: | ---: | ---: |
| folder-r2r | PC起動後初回 | 22,348 ms | 22,398 ms | 32,156 ms | 約63.6 s |
| folder-r2r | 2回目 | 21,770 ms | 21,815 ms | 31,406 ms | window close前に未完 |
| bundle-r2r | PC起動後初回 | 22,306 ms | 22,362 ms | 33,043 ms | 約64.0 s |
| bundle-r2r | 2回目 | 21,882 ms | 21,956 ms | 32,504 ms | 約62.8 s |

評価:

- 以前再現していたPC起動後初回の約100秒化は再発していない。
- cold / second runの差はrequired initializationで概ね1秒未満である。
- folder-r2rとbundle-r2rの差は0.9～1.1秒、約2.7～3.4%で、selection thresholdの5秒かつ15%を満たさない。
- current issueはdistribution formatではなく、既に修正したoptional folder-tree readiness dependencyであった。

## Required / post task semantics

required initializationで待つ主要work:

- playlist entries hydration: 約6.7～7.1 s
- chart-info hydration: 約1.4～1.6 s
- score hydration: 約1.1～1.2 s
- LR2 required enrollment: 10 ms以下

post-initialization workはoperable直後からrequired workと並行して開始する。全post taskのconcurrencyは1である。主なtail:

- playlist virtual-order prewarm: 約20～21 s
- custom-folder physical repair / audit: 約3.9～5.7 s
- maintenance hydration: 約5.1～5.4 s
- external catalog: 約2.3～3.3 s
- post-initialize GC: 約1.2 s

したがって`startup_initialization_complete`の短縮は、単に全workをその後から開始する変更ではない。ただしvirtual sort prewarmはrequired complete後にqueueされるbest-effort workであり、post-completeまでは約63～64秒かかる。

## User-visible capability decision

- chart install / destination estimation: operable前にreadiness完了。
- normal library / playlist browsing: operableから利用可能。
- local playlist edit: playlist entries hydrationをrequiredとし、initialization completeまでに保証。
- automatic URL completion / playlist reference / external sync: post work。initialization complete時点の完了は保証しない。
- library folder tree final refresh: post work。global operabilityをgateしない。
- sort cache: post best-effort。未完時はon-demand fallback。

## List transition protection

一覧遷移改善は維持されている。

- playlist summaryはstable versioned sourceを使う。
- main-table rows / mode / selectionをatomic presentation commitで公開する。
- data-only変更でcolumn layoutを破棄しない。
- unrelated settings fan-outをtyped eventへ分離した。
- performance logはbounded asynchronous writerを使う。
- normal-library refresh producerはUI completionを同期waitしない。

今回のstartup変更はこれらのrouteを再設計していない。

## Shutdown observation

folder-r2rの2回目はpost-initialization external catalog実行中にwindow closeされ、HTTP requestがcancelされ、queued BMT taskが1件discardされた。shutdown drainは約104 msで完了しており、ユーザー終了に伴うexpected cancellationである。required initializationおよびdata integrity failureのevidenceではない。

## Engineering gate evidence recorded in repository

- locked restore / Release build: passed
- full tests: 3,589 passed, 16 skipped, 0 failed
- Roslynator: 0 diagnostics
- bundle-r2r / folder-r2r reproducible publish: passed
- existing-data acceptance: passed
- update success / fault rollback: passed
- Release executable UI smoke: passed

このレビュー環境ではbuild / testを再実行しておらず、上記はrepositoryに記録されたgate evidenceである。
