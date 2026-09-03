# 初期化・リロード進捗ゲージ 現行仕様

## 概要

ステータスバーの初期化・リロード進捗は `StartupProgressWorkflowOwner` が所有する。`MainWindowViewModel` は operation orchestration と shell coordination を担当し、進捗 state、operation token、表示 writer は owner が保持する。

表示 binding:

- label: `ProgressHub.StartupProgress.Label`
- sub label: `ProgressHub.StartupProgress.SubLabel`
- value: `ProgressHub.StartupProgress.Value`
- maximum: `ProgressHub.StartupProgress.Maximum`
- visibility: `ProgressHub.StartupProgress.IsActive`

進捗の分母は operation 開始時に固定し、後から queue された post-initialization task で増やさない。発生しない expected phase は skip 完了として扱う。

## 計算モデル

```text
Maximum = ExpectedPhases に含まれる phase 数
Value   = ExpectedPhases かつ CompletedPhases に含まれる phase 数
```

各 operation は `OperationToken` を持つ。遅延 UI flush、folder refresh、external sync、playlist reference apply などの callback は、schedule 時 token と current token が一致する場合だけ current operation を更新する。

background request が必要な phase は、request / skip が確定する前に完了扱いにしない。基礎 phase と library load phase、required scheduler idle phaseは明示 request なしでもownerが完了できる。

## Startup の二つの完了境界

### `StartupBackgroundTasksDone`

Startup の expected phase である。現行では、**required task の enrollment が閉じた後の required work の idle** を表す。required scheduling が閉じる前に idle になっても、この phase は完了しない。

```text
runningRequiredCount == 0
かつ
required request が queue に存在しない
```

post-initialization taskがqueueまたはrunningでも完了できる。required task enrollmentが閉じ、他のexpected phaseが完了していることも必要である。自動 LR2 `song.db` 同期は post-startup task であり、この phase の完了条件・分母・値には含めない。

### `startup_post_initialization_maintenance_complete`

進捗ゲージのphaseではない。scheduler 管理下の post schedulingが閉じ、schedulerがfully idleで、post-startup warmup pending countが0になった時に別markerとして記録する。scheduler 外の ranking/XML refresh と遅延 presentation flush の完了はこのmarkerに含めず、それぞれのphase / lifecycle markerで記録する。

この分離により、通常利用に必要なlocal initializationと、scheduler 管理下の network / audit / export / prewarmの完了を同じ分母へ混ぜない。独立 worker の完了もStartup progressの分母へ暗黙に追加しない。

## Operation 別 ExpectedPhases

| Operation | Expected count | Expected phases |
| --- | ---: | --- |
| `Startup` | 13 | core、library DB / enumeration / diff、ready data / UI / operable、score、digest、chart-info hydration / backfill、playlist entries、required background idle |
| `FullReinitialize` | 14 | core、library DB / enumeration / diff、operable、playlist reference、score / ranking、maintenance / installable maintenance、digest、chart-info hydration / backfill、playlist entries |
| `ReloadFileDiff` | 6 | core、file enumeration、file diff、operable、playlist reference、playlist entries |
| `ScoreOnly` | 4 | core、operable、score hydration、ranking refresh |
| `ReloadTables` | 5 | core、operable、playlist reference、external playlist sync、playlist entries |

Startup では `PlaylistReferenceApplied`、`ExternalPlaylistSyncDone`、`MaintenanceDeferredDone`、`InstallableMaintenanceDeferredDone`、`RankingRefreshDone` を initial expected set に含めない。前4つはStartupのscheduler管理下のpost work、`RankingRefreshDone` はscheduler外の独立 ranking refresh worker または別operationのphaseである。

## Phase 一覧

| Phase | 主な意味 |
| --- | --- |
| `CoreInitializeStarted` | operation開始 |
| `LibraryDatabaseLoadDone` | catalog DB load完了 |
| `LibraryFileEnumerationDone` | chart / resource enumeration完了 |
| `LibraryFileDiffDone` | file diff apply完了 |
| `StartupReadyData` | install readinessに必要なdata完了 |
| `StartupReadyUi` | required UI flush完了 |
| `StartupReadyOperable` | 通常入力解禁・scheduler開始 |
| `ScoreHydrationDone` | active score snapshot適用完了 |
| `ChartDigestBackfillDone` | digest phase完了またはskip |
| `ChartInfoHydrationDone` | current chart-info session hydration完了 |
| `ChartInfoBackfillDone` | backfill完了または不要skip |
| `PlaylistEntriesHydrationDone` | local playlist entries hydration完了 |
| `StartupBackgroundTasksDone` | Startup required scheduler work idle |
| `PlaylistReferenceApplied` | playlist reference apply完了。Startupではpost扱い |
| `ExternalPlaylistSyncDone` | external playlist sync完了。Startupではpost扱い |
| `MaintenanceDeferredDone` | maintenance deferred phase完了。Startupではpost扱い |
| `InstallableMaintenanceDeferredDone` | installable maintenance完了。Startupではpost扱い |
| `RankingRefreshDone` | ranking refresh完了。Startupではrequired phaseではない |

## Label 遷移

表示はcurrent expected phaseとactive sub-progressに基づく。少なくとも次を区別する。

```text
初期化開始
→ DB読込
→ ファイル列挙
→ 差分反映
→ UI準備
→ 譜面情報 / score / playlist hydration
→ required background完了
```

file / chart件数を持つ処理はsub labelへ`processed / total`と現在対象を出せる。長い文字列は省略表示し、tooltipへ全文を出す。

post-initialization taskはStartup progressの分母へ追加しない。自動 LR2 `song.db` 同期も同じ扱いで、正常な `startup_initialization_complete` 後に一度だけ queue する。初回完了ダイアログが pending の場合も、同期を先に queue し、ダイアログの終了は待機条件にしない。LR2 の `Running` / progress / `Incomplete` / `Failed` は専用 status のみを更新し、startup の expected/completed/failed/gauge を変更しない。startup の visual linger 中は専用 status を一時的に隠せるが、linger が消えたら最新の非終端 status を表示する。scheduler 管理下の task は`startup_background_task`、`startup_background_summary`、`startup_post_initialization_maintenance_complete`で観測し、scheduler 外の ranking/XML refresh や遅延 presentation flush は固有の phase / lifecycle markerで観測する。

## 初期化完了メッセージ

初回設定後の完了メッセージは`startup_initialization_complete`後に従来のダイアログ経路で表示する。自動 LR2 同期はその前に queue するため、ダイアログの終了を待たない。これはrequired local initializationの完了を意味し、LR2 sync、external sync、physical audit、export、sort prewarmまで完了したことは意味しない。

## ガード

- 前operationのcallbackでcurrent tokenを完了させない。
- required taskを数値短縮のためだけにpostへ移さない。
- post taskをStartup progressへ戻してnetworkやauditで通常操作をblockしない。
- failure時はoperation stateをfailedにし、retryable failureと通常busyを区別する。
- progress完了だけでUI collectionをworker threadから直接変更しない。

関連仕様: [startup-initialization-flow.md](startup-initialization-flow.md)。
