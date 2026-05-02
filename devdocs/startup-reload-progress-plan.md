# 初期化・リロード進捗ゲージ整理計画

## 概要

ステータスバーの初期化・ライブラリリロード進捗は、`StartupProgressPhase` の `ExpectedPhases` と `CompletedPhases` から算出される。

現状は、初期化開始時点では最小限のフェーズだけを `ExpectedPhases` に入れ、deferred 処理が実際に要求されたタイミングで待機対象を追加している。このため、途中で `StartupProgressMaximum` が増え、`StartupProgressValue` が減っていなくてもゲージの比率が下がって見えることがある。

今後は、各 operation で発生し得るフェーズを開始時点で `ExpectedPhases` に入れ、不要・未発生・スキップが確定したフェーズは即座に完了扱いにする方針へ寄せる。これにより、ゲージの分母を operation 中に増やさず、見た目の巻き戻りを避ける。

実装では `RequestedPhases` と `SkippedPhases` を追加し、`ExpectedPhases` は operation 開始時に固定する。後続 request は `RequestedPhases` と required version だけを更新し、expected 外 request はログに残して進捗には反映しない。

## 現状の表示モデル

UI は `MainWindow.xaml` のステータスバーで以下に binding されている。

- `IsStartupProgressActive`
- `StartupProgressLabel`
- `StartupProgressSubLabel`
- `StartupProgressValue`
- `StartupProgressMaximum`

`MainWindowViewModel.RecomputeStartupProgressPresentation()` は次の考え方で値を作る。

```text
StartupProgressMaximum = ExpectedPhases に含まれるフェーズ数
StartupProgressValue   = ExpectedPhases かつ CompletedPhases に含まれるフェーズ数
```

完了処理は `CompletedPhases |= phase` のため、完了済みフェーズ自体は基本的に減らない。巻き戻りの主因は `ExpectedPhases` が後から増えることにある。

## 現状のフェーズ一覧

| Phase | 主な意味 | 現状の追加タイミング |
| --- | --- | --- |
| `CoreInitializeStarted` | 進捗 operation 開始 | 開始時に常に expected / completed |
| `StartupReadyData` | 起動時の主要データ読込完了 | Startup のみ開始時に expected |
| `StartupReadyUi` | 起動時の UI 準備完了 | Startup のみ開始時に expected |
| `StartupReadyOperable` | 操作可能状態 | 開始時に常に expected |
| `PlaylistReferenceApplied` | playlist 参照解決反映 | 対象 reason の request 発生時に expected 追加 |
| `ExternalPlaylistSyncDone` | 外部 playlist 同期 | 対象 reason の request 発生時に expected 追加 |
| `PlaylistEntriesHydrationDone` | playlist entry hydration | request 発生時に expected 追加 |
| `ChartInfoHydrationDone` | chart_info hydration | request 発生時に expected 追加 |
| `ChartInfoBackfillDone` | chart_info backfill | request 発生時に expected 追加 |
| `ChartDigestBackfillDone` | chart digest backfill | request 発生時に expected 追加 |
| `ScoreHydrationDone` | score hydration | request 発生時に expected 追加 |
| `RankingRefreshDone` | ranking refresh | request 発生時に expected 追加 |
| `MaintenanceDeferredDone` | maintenance deferred 更新 | request 発生時に expected 追加 |

実装後は、上記の「現状の追加タイミング」は `RequestedPhases` 更新タイミングになる。`ExpectedPhases` の追加タイミングではない。

## 現状の operation ごとの粒度

### Startup

開始時点では次を expected にする。

- `CoreInitializeStarted`
- `StartupReadyData`
- `StartupReadyUi`
- `StartupReadyOperable`

その後、初期化中または操作可能後に次が要求された場合だけ expected に追加される。

- `PlaylistEntriesHydrationDone`
- `ChartInfoHydrationDone`
- `ChartInfoBackfillDone`
- `ChartDigestBackfillDone`
- `ExternalPlaylistSyncDone`
- `PlaylistReferenceApplied`
- `ScoreHydrationDone`
- `RankingRefreshDone`
- `MaintenanceDeferredDone`

### ReloadFiles

開始時点では次だけを expected にする。

- `CoreInitializeStarted`
- `StartupReadyOperable`

その後、ファイル再読込に伴って要求された場合だけ expected に追加される。

- `PlaylistReferenceApplied`
- `PlaylistEntriesHydrationDone`
- `ChartInfoHydrationDone`
- `ChartInfoBackfillDone`
- `ChartDigestBackfillDone`
- `ScoreHydrationDone`
- `RankingRefreshDone`
- `MaintenanceDeferredDone`

### ReloadTables

開始時点では次だけを expected にする。

- `CoreInitializeStarted`
- `StartupReadyOperable`

その後、playlist/table 再読込に伴って要求された場合だけ expected に追加される。

- `ExternalPlaylistSyncDone`
- `PlaylistReferenceApplied`
- `PlaylistEntriesHydrationDone`

`ReloadTables` は通常、score / ranking / maintenance / chart_info / digest backfill を主目的にしないため、修正方針ではそれらを expected に含めない。

## 問題点

### ゲージ比率が下がる

例:

```text
開始直後:      Completed 1 / Expected 4
UI 準備後:     Completed 3 / Expected 4
deferred 追加: Completed 3 / Expected 10
```

この場合、`StartupProgressValue` は 3 のままだが、`StartupProgressMaximum` が 4 から 10 に増えるため、ProgressBar の見た目は後退する。

### サブラベルの表示対象も後から変わる

`GetStartupProgressSubLabel()` は未完了 expected フェーズのうち優先度が高いものを表示する。後から expected が増えると、表示対象が `バックグラウンド更新中` から `譜面メタデータ解析` などへ戻ったように見えることがある。

### operation の粒度差が大きい

`Startup` は開始時点で 4 フェーズを持つ一方、`ReloadFiles` / `ReloadTables` は 2 フェーズから始まる。そのため、リロードでは deferred フェーズが追加されたときの分母増加が相対的に大きく、巻き戻りが目立ちやすい。

## 修正方針

### 1. operation 開始時に expected フェーズを確定する

`StartStartupProgressOperation()` で operation kind ごとの expected フェーズ集合を作る。

| Operation | 開始時点で expected に入れる候補 |
| --- | --- |
| `Startup` | 全フェーズ |
| `ReloadFiles` | ファイル再読込で発生し得るフェーズ |
| `ReloadTables` | playlist/table 再読込で発生し得るフェーズ |

開始後の `TrackStartupProgress*Requested()` は `ExpectedPhases` を増やさない。役割は `RequestedPhases`、required version、カウンタ初期化、完了待ち解除の更新に限定する。

### 2. 不要フェーズは skip 完了する

実際に request が発生しないフェーズは、operation の適切な節目で完了扱いにする。

例:

- `ReloadTables` では score / ranking / maintenance / chart_info / digest 系を expected に入れない。
- `ReloadFiles` で chart_info backfill が不要と判断されたら `ChartInfoBackfillDone` を完了扱いにする。
- `Startup` で score DB がない、または score hydration が要求されない場合は `ScoreHydrationDone` を完了扱いにする。
- 外部 playlist 同期を行わない設定なら `ExternalPlaylistSyncDone` を完了扱いにする。
- `ChartDigestBackfillDone` は producer が実質 disabled のため、Startup / ReloadFiles で request が出なければ skip 完了する。

この処理は「待たない」ではなく「この operation では完了済みとして扱う」という意味にする。そうすると `AreExpectedStartupProgressPhasesCompleted()` の完了判定と整合する。

### 3. request されなかった deferred フェーズの確定ポイントを作る

現状は request が来たフェーズだけを追跡するため、request が来ないことを明示的に処理していない。新方針では skip 確定が必要になる。

候補:

- `files.Initialize(...)` 完了直後
  - ファイル読込中に request されるべき deferred 作業が出揃う。
  - 出ていない chart digest / chart_info / score / ranking / maintenance を skip 完了できる。
- `tables.Initialize(...)` 完了直後
  - playlist entry hydration / external sync / playlist reference の有無を判定しやすい。
- `StartDeferredExternalPlaylistSync(...)` や `ScheduleDeferredPlaylistReferenceApply(...)` を呼ばない分岐
  - 対応フェーズを skip 完了する。

### 4. expected 追加 API を guarded にする

移行期間中は安全のため、`TrackStartupProgress*Requested()` が expected を増やす場合でも、すでに operation 開始後に分母を増やしたことをログに出せるようにする。

```text
startup_progress_expected_late_add operation=ReloadFiles phase=ChartInfoBackfillDone
```

最終的には late add が出ない状態を目標にする。

実装では late add は行わない。expected 外 request は次のようにログへ残す。

```text
startup_progress_request_ignored operation=ReloadTables phase=ScoreHydrationDone reason=not_expected
```

skip 後に request が来た場合も pending には戻さず、単調表示を優先して次のログを出す。

```text
startup_progress_request_after_skip operation=ReloadFiles phase=ChartDigestBackfillDone
```

### 5. request-before-complete を導入する

background 系フェーズは request 済みでなければ complete できない。これにより、古い completion event や required version `0` のままの completion で、未要求フェーズが完了扱いになることを防ぐ。

`CoreInitializeStarted`、`StartupReadyData`、`StartupReadyUi`、`StartupReadyOperable` は request 不要の基礎フェーズとして扱う。

### 6. Dispatcher 反映も operation token で守る

別要因として、`Dispatcher.BeginInvoke` に古い表示値が遅れて反映される可能性がある。`RecomputeStartupProgressPresentation()` で capture した `OperationToken` を UI 反映時にも確認し、現在の token と違う場合は捨てる。

これは分母増加による巻き戻りとは別の対策だが、進捗表示の安定性として同時に行う価値がある。

## expected フェーズ案

### Startup

Startup は起動直後に起こり得る全フェーズを expected に入れる。

| Phase | 扱い |
| --- | --- |
| `CoreInitializeStarted` | 開始時に completed |
| `StartupReadyData` | 必須 |
| `StartupReadyUi` | 必須 |
| `StartupReadyOperable` | 必須 |
| `PlaylistEntriesHydrationDone` | expected。不要なら skip 完了 |
| `ChartInfoHydrationDone` | expected。不要なら skip 完了 |
| `ChartInfoBackfillDone` | expected。対象 0 件なら完了 |
| `ChartDigestBackfillDone` | expected。対象 0 件なら完了 |
| `ExternalPlaylistSyncDone` | expected。外部同期なしなら skip 完了 |
| `PlaylistReferenceApplied` | expected。参照更新なしなら skip 完了 |
| `ScoreHydrationDone` | expected。score 対象なしなら skip 完了 |
| `RankingRefreshDone` | expected。ranking 更新なしなら skip 完了 |
| `MaintenanceDeferredDone` | expected。保守更新なしなら skip 完了 |

### ReloadFiles

ファイル再読込ではライブラリ本体、playlist 参照、metadata / score / maintenance 系を対象にする。

| Phase | 扱い |
| --- | --- |
| `CoreInitializeStarted` | 開始時に completed |
| `StartupReadyOperable` | 必須 |
| `PlaylistEntriesHydrationDone` | expected。playlist 側更新がない場合は skip 完了 |
| `ChartInfoHydrationDone` | expected。不要なら skip 完了 |
| `ChartInfoBackfillDone` | expected。対象 0 件なら完了 |
| `ChartDigestBackfillDone` | expected。対象 0 件なら完了 |
| `PlaylistReferenceApplied` | expected。`ScheduleDeferredPlaylistReferenceApply` しない場合は skip 完了 |
| `ScoreHydrationDone` | expected。score 更新なしなら skip 完了 |
| `RankingRefreshDone` | expected。ranking 更新なしなら skip 完了 |
| `MaintenanceDeferredDone` | expected。保守更新なしなら skip 完了 |

`StartupReadyData` / `StartupReadyUi` は Startup 専用の概念として、ReloadFiles には含めない。

### ReloadTables

table / playlist 再読込では playlist entry hydration、外部同期、参照更新を対象にする。

| Phase | 扱い |
| --- | --- |
| `CoreInitializeStarted` | 開始時に completed |
| `StartupReadyOperable` | 必須 |
| `PlaylistEntriesHydrationDone` | expected。不要なら skip 完了 |
| `ExternalPlaylistSyncDone` | expected。外部同期なしなら skip 完了 |
| `PlaylistReferenceApplied` | expected。参照更新なしなら skip 完了 |

score / ranking / maintenance / chart_info / chart digest 系は ReloadTables の主対象ではないため、開始時 expected には含めない。将来 table reload からそれらの更新が明示的に必要になった場合は、その時点で expected 案を見直す。

## 実装案

### helper を追加する

`StartupProgressPhase` の集合を operation kind から返す helper を追加する。

```text
GetInitialExpectedStartupProgressPhases(operationKind)
```

`StartStartupProgressOperation()` はこの helper の結果を `ExpectedPhases` に設定する。

### skip 完了 helper を追加する

不要フェーズの完了を明示するため、通常完了と同じ `CompletedPhases` に入れる helper を追加する。

```text
SkipStartupProgressPhaseIfExpected(phase, reason)
CompleteStartupProgressPhaseIfExpected(phase)
```

ログ上は `completed` と `skipped` を区別できると調査しやすい。

```text
startup_progress_phase_skipped operation=ReloadFiles phase=ScoreHydrationDone reason=no_request
```

### request tracking の役割を変更する

`TrackStartupProgress*Requested()` は次の役割に限定する。

- 現在 operation が対象 reason / version を追跡すべきか判断する。
- 対象 phase を `RequestedPhases` に入れる。
- required version を更新する。
- 対象 phase が expected に含まれていない場合は進捗へ反映せずログに残す。

late add は行わない。

### operation 終了前の skip 確定を行う

`ReloadFiles` / `ReloadTables` / `Initialize` の `finally` や deferred scheduling 分岐で、request が来なかったフェーズを skip 完了する。

特に `StartupReadyOperable` を完了する直前に、未要求 deferred フェーズを整理しておくと、操作可能後に「背景フェーズの分母だけが残る」状態を制御しやすい。

ReloadFiles の `ScheduleDeferredPlaylistReferenceApply()` は `EnsureAllPlaylistEntriesLoadedAsync()` を直接呼ぶため、この経路では playlist reference request と同じ version で `PlaylistEntriesHydrationDone` を request / complete する。entries が既に loaded の場合も complete する。

Startup / ReloadTables の callback 付き external sync は `ReplaceReferenceBMSTable` を external sync 内で行うため、明示的な deferred playlist reference がない場合は `PlaylistReferenceApplied` を skip する。

### サブラベル順は維持する

`GetStartupProgressSubLabel()` の優先順位は現状維持でよい。

ただし expected が最初から多くなるため、未 request だがまだ skip 確定していないフェーズがサブラベルに出る可能性がある。これを避けるには、未 request フェーズは開始直後から `Pending` として表示対象にするのではなく、次のどちらかにする。

- skip 確定を早める。
- `RequestedPhases` を別に持ち、サブラベルは requested かつ未完了のフェーズを優先する。

最初の実装では skip 確定を早める方が変更範囲を抑えやすい。

### 操作可能後の表示文言を調整する

現状は `StartupReadyOperable` 完了後に一瞬 `操作可能` と表示され、その後 deferred フェーズが残っていると `バックグラウンド更新中` へ遷移することがある。

新方針では、操作可能到達後も expected フェーズが残るケースを通常状態として扱う。そのため、操作可能後に background フェーズが残っている場合は、完了ラベルを単純な `操作可能` ではなく次のように表示する。

```text
操作可能(バックグラウンド更新中)
```

これにより、「操作可能になったが、軽い後続更新は継続している」という状態を一つの表示として扱える。すべての expected フェーズが完了したら、従来通り `初期化完了` または完了相当の表示へ移る。

## テスト方針

- `StartStartupProgressOperation(Startup)` 直後の `StartupProgressMaximum` が、Startup で想定する全フェーズ数になること。
- `ReloadFiles` / `ReloadTables` 直後の `StartupProgressMaximum` が、それぞれの想定フェーズ数になること。
- deferred request 発生時に `StartupProgressMaximum` が増えないこと。
- request なしフェーズを skip 完了すると `StartupProgressValue` が増えること。
- chart_info backfill / digest backfill / score hydration / ranking refresh / maintenance deferred の完了順が前後しても `StartupProgressValue` が減らないこと。
- operation token が変わった後、古い Dispatcher 反映が UI 値を上書きしないこと。
- `ReloadFiles` 後に playlist reference が request された場合でも、分母が増えず、完了時に value だけ進むこと。
- `ReloadTables` 後に external sync が request された場合でも、分母が増えず、完了時に value だけ進むこと。
- `ReloadTables` では score / ranking / maintenance が expected に入らないこと。
- 操作可能到達後に background フェーズが残っている場合、表示が `操作可能(バックグラウンド更新中)` になること。

## 残る検討点

- expected に含めたが長時間 request されないフェーズを、どのタイミングで skip と判断するか。
- 操作可能後の background 表示をどの程度長く出すか。表示文言は `操作可能(バックグラウンド更新中)` とする。

## 今回の確定事項

- `ChartInfoBackfillDone` と `ChartDigestBackfillDone` はどちらも `譜面メタデータ解析` 表示のままでよい。
- `ReloadTables` では score / ranking / maintenance は expected に入れない。
