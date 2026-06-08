# 初期化・リロード進捗ゲージ 現行仕様

## 概要

ステータスバーの初期化・リロード進捗は `MainWindowViewModel` の `StartupProgressPhase` を基に表示する。

起動時の app schema preflight / repair は、`BMSLibrary.Initialize()` 本体に入る前の前提整備として扱う。詳細は `devdocs/spec/startup-initialization-flow.md` を参照する。

進捗の分母は operation 開始時に固定され、後続の background request で増えない。発生しない phase は skip 完了扱いにし、ゲージが巻き戻って見えないようにしている。

表示は `MainWindow.xaml` のステータスバーに出る。

- main label: `StartupProgressLabel`
- sub label: `StartupProgressSubLabel`
- progress value: `StartupProgressValue`
- progress maximum: `StartupProgressMaximum`

`StartupProgressSubLabel` は幅 200、長い文字列は省略表示し、tooltip に全文を出す。

## 計算モデル

```text
StartupProgressMaximum = ExpectedPhases に含まれる phase 数
StartupProgressValue   = ExpectedPhases かつ CompletedPhases に含まれる phase 数
```

`ExpectedPhases` は `Startup` / `ReloadFileDiff` / `ScoreOnly` / `FullReinitialize` / `ReloadTables` の開始時に固定される。request 済みだが不要になった phase、または request されなかった phase は `SkippedPhases` と `CompletedPhases` に入る。

progress operation には `OperationToken` を付与する。UI suppress の遅延 flush、folder tree の遅延 refresh、external playlist sync、playlist reference apply のように operation 本体より遅れて戻る callback は、スケジュール時 token と現在の token が一致する場合だけ progress phase を進める。これにより、前回 operation の遅延 callback が次回 operation の `StartupReadyUi` / `StartupReadyOperable` / background phase を誤って完了させることを防ぐ。

background 系 phase は request 済みでなければ complete できない。ただし次の基礎 phase と library load phase は request 不要で complete できる。

- `CoreInitializeStarted`
- `StartupReadyData`
- `StartupReadyUi`
- `StartupReadyOperable`
- `LibraryDatabaseLoadDone`
- `LibraryFileEnumerationDone`
- `LibraryFileDiffDone`

`ChartInfoHydrationDone` は完了後に full `ChartInfoBackfillDone` を queue するため、hydration request を受けた時点で `ChartInfoBackfillDone` も request 済みとして扱う。これにより、`Initialize` 完了直後の未 request phase skip で譜面メタデータ解析が完了扱いにならず、backfill 開始後に `[processed/total] 譜面メタデータ解析 fileName` が表示される。

## Operation 別 ExpectedPhases

| Operation | Expected count | Expected phases |
| --- | ---: | --- |
| `Startup` | 17 | 全 phase |
| `ReloadFileDiff` | 6 | `CoreInitializeStarted`, file enumeration, file diff, `StartupReadyOperable`, playlist reference, playlist entries hydration |
| `ScoreOnly` | 4 | `CoreInitializeStarted`, `StartupReadyOperable`, score hydration, ranking refresh |
| `FullReinitialize` | 14 | `CoreInitializeStarted`, library load 3 phase, `StartupReadyOperable`, playlist reference, playlist entries hydration, chart info hydration/backfill, chart digest backfill, score hydration, ranking refresh, maintenance deferred, installable maintenance deferred |
| `ReloadTables` | 5 | `CoreInitializeStarted`, `StartupReadyOperable`, `PlaylistReferenceApplied`, `ExternalPlaylistSyncDone`, `PlaylistEntriesHydrationDone` |

`ReloadFileDiff` は外部ファイル操作による追加・削除・移動検出用の軽量 reload で、DB 読み込み、chart_info hydration/backfill、score/ranking、maintenance deferred、installable maintenance deferred を expected に含めない。`FullReinitialize` は旧 ReloadFiles 相当の初期化再実行として残す。

`ScoreOnly` は score DB 設定変更専用の reload で、playlist reference、external playlist sync、playlist entries hydration、file scan、chart_info、maintenance を expected に含めない。LR2 source で ranking refresh が要求されない場合や beatoraja source の場合は、ranking refresh phase は skip 完了扱いにする。

`ReloadTables` は file scan / chart_info / chart digest / score / ranking / maintenance を expected に含めない。

LR2 linked / standalone の mode 切替は hot reload でも full initialize でもなく、プロセス再起動境界で反映する。起動済み profile がある通常運用時は、設定ダイアログで mode トグルを切り替えた時点で再起動確認を出す。OK の場合は永続化済み設定を reload し、動作モードだけ保存してアプリを再起動する。他の未保存設定は保存しない。Cancel の場合は保存済み mode に戻す。mode 切替では current process の startup progress operation を開始しないため、`Startup` / `FullReinitialize` / `ReloadFileDiff` / `ReloadTables` / `ScoreOnly` のいずれにも分類しない。

初回起動や設定不備で有効な active profile がまだ一度も成立していない場合は、mode トグルで再起動確認を出さない。トグルは設定ダイアログ内の draft mode を変えるだけで、OK 保存後に同じ process の `Startup` を開始する。初期設定保存では runtime post-save action を走らせず、直後の `Startup` だけが search root、player、playlist などを反映する。ただし LR2 linked の `config.xml` など永続化対象の設定ファイルは保存する。この場合に進捗ゲージへ出る operation は、OK 後に開始される通常の `Startup` だけである。

standalone mode は app-owned `data\song.db` と設定画面の複数 BMS root を使うため、LR2 `song.db` / `config.xml` / `score.db` の validation や LR2 backup/custom folder/IR ranking phase は発生しない。standalone mode でも BMS インストール先は必須で、登録済み BMS root のいずれかを選ぶ必要がある。beatoraja score.db 設定だけを切り替えた場合は `ScoreOnly` として扱い、playlist/table reload や external playlist sync は起動しない。

BMS search root の追加・削除は `ReloadFileDiff` として扱う。実行前に runtime の `BMSLibrary.SearchTargets` を保存済み root set へ同期するため、standalone の BMS ディレクトリ追加や LR2 linked の search directory 変更は同一プロセス内の file diff に反映される。

## Phase 一覧

| Phase | 意味 | 主な表示 |
| --- | --- | --- |
| `CoreInitializeStarted` | operation 開始 | なし。開始時点で completed |
| `LibraryDatabaseLoadDone` | active profile の song.db / bmson_song / maintenance / digest map 読み込み | `DB読み込み` |
| `LibraryFileEnumerationDone` | BMS root 配下のファイル列挙 | `ファイル列挙`、scanner 判明時は `(Everything)` / `(Fallback)` 付き |
| `LibraryFileDiffDone` | DB と列挙結果の差分確認、追加譜面の読み込み | `ファイル差分確認` |
| `StartupReadyData` | Startup の主要データ読込完了 | Startup 専用 gate |
| `StartupReadyUi` | Startup の初期 UI 構築完了 | `画面準備` |
| `StartupReadyOperable` | ユーザー操作可能 | 操作可能後に未完了 phase があれば `操作可能(バックグラウンド更新中)` |
| `PlaylistReferenceApplied` | playlist 参照解決反映 | `プレイリスト参照更新` |
| `ExternalPlaylistSyncDone` | 外部 playlist 同期 | `プレイリスト参照更新` |
| `PlaylistEntriesHydrationDone` | playlist entry hydration | `プレイリスト読込` |
| `ChartInfoHydrationDone` | 既存 chart_info のメモリ適用 | `譜面メタデータ反映` |
| `ChartInfoBackfillDone` | chart_info 解析 | `[processed/total] 譜面メタデータ解析 fileName` |
| `ChartDigestBackfillDone` | chart digest 補完 | `[processed/total] 譜面メタデータ解析 fileName` |
| `ScoreHydrationDone` | score 反映 | `スコア反映` |
| `RankingRefreshDone` | ranking refresh | `ランキング更新` |
| `MaintenanceDeferredDone` | maintenance hydration / orphan cleanup | `保守参照更新` |
| `InstallableMaintenanceDeferredDone` | installable maintenance deferred 更新 | `保守情報更新` |

`InstallableMaintenanceDeferredDone` には bounded 並列の cache-aware resource health 実チェックと、その結果を通常一覧へ投影するための runtime resource health index build が含まれる。WARNING 表示用の全件 `BMSFile.Warnings` 再構築は行わない。

`ChartDigestBackfillDone` は app schema repair の一部ではない。startup repair は `chart_digest_map` の schema と既存 row の保持だけを扱い、`song` table 全件から実ファイルを読んで digest を補完しない。missing digest がある場合は file diff / install / chart_info pipeline 側の補完として扱う。

Startup / FullReinitialize では `chart_info_hydration` と `maintenance_hydration` が完了してから、`InstallableMaintenanceDeferredDone` の task を開始する。これにより、譜面メタデータの大量 hydration/backfill と保守情報更新が同時に走って UI 更新や DB commit が競合する状態を避ける。

新規・更新ファイル由来の `chart_info` / maintenance は background phase へ送らず、`LibraryFileDiffDone` の内側で扱う。`ChartInfoBackfillDone` と `InstallableMaintenanceDeferredDone` は、DB に既に存在する owner の補助情報を補完する phase として扱う。

`chart_info` backfill の事前候補が 0 件の場合も、no-op の requested/completed version を発行して `ChartInfoBackfillDone` を完了させる。これは hydration 時点で progress が backfill phase を expected に含めるためで、実処理は起動しない。

## Library Load の細分化

`Startup` と `FullReinitialize` では、従来 `ライブラリ読込` として見えていた区間を 3 phase に分ける。`ReloadFileDiff` は DB load phase を持たず、file enumeration / file diff だけを表示する。

| Phase | 完了条件 | 件数表示 |
| --- | --- | --- |
| `LibraryDatabaseLoadDone` | `LoadSongTable()` 完了 | なし |
| `LibraryFileEnumerationDone` | `ExecuteChartScanWithManagedFallback()` の native canonical resource index 取得、または Everything unavailable 時の managed resource index 取得 | なし |
| `LibraryFileDiffDone` | `ApplyFileScanDiff()` 完了 | 追加 BMS + 追加/更新 bmson の parse 件数 |

Everything scan は DB 読み込みと並列 prefetch される場合がある。表示上は DB 読み込みを優先し、DB 完了後に file enumeration が未完了なら `ファイル列挙` を表示する。

`ApplyFileScanDiff()` の parse 進捗は BMS と bmson を合算する。成功・失敗のどちらも processed に含める。

差分ファイルがない場合は、file diff phase は DB と列挙結果の比較、および導入先推定用 index の公開だけで完了する。譜面本文 read は行わず、操作可能化を優先する。

metadata bundle import はリリース同梱または外部配布 metadata の取り込みであり、差分ファイル由来/DB由来 background 補完とは別枠で扱う。配置されている場合は起動直後に一度 import を試み、import 済み bundle は `imported_metadata/` 退避により通常起動の critical path から外す。

差分なし fast path では、full `chart_info` hydration/backfill は導入先推定に不要な DB 由来補助情報として background 側へ寄せる。

差分ファイルがある場合は、`LibraryFileDiffDone` が次を含む。

- reader による `ReadBuffer()`。
- parser worker による digest 計算 / snapshot 作成。
- lightweight parse。
- LR2 parent/folder 正規化。
- inline `chart_info` 解析または current skip 判定。
- 新規・更新ファイル由来の maintenance row / resource health 判定。
- DB chunk commit。
- in-memory catalog 反映。

このため、`LibraryFileDiffDone` は単なる差分検出ではなく、新規・更新ファイルを DB と memory に反映しきる phase として扱う。

## 件数付き SubLabel

件数付き表示は、狭いステータスバーでも分母が残りやすいように count を先頭に置く。
ただし `ChartInfoHydrationDone` は既存 chart_info の DB load / memory owner 反映であり、`applied/total` が直感的な進捗になりにくいため件数を出さない。

```text
[processed/total] phaseLabel fileName
```

例:

```text
[6695/209999] ファイル差分確認 added.bms
[6695/209999] 譜面メタデータ解析 chart.bms
```

file name がない場合は末尾を省略する。

## Label 遷移

main label は operation kind と完了状態で決まる。

| 状態 | Startup | ScoreOnly | ReloadFileDiff / ReloadTables | FullReinitialize |
| --- | --- | --- | --- | --- |
| 実行中 | `初期化中` | `スコア更新中` | `ライブラリ更新中` | `初期化再実行中` |
| 操作可能だが background phase が残る | `操作可能(バックグラウンド更新中)` | `操作可能(バックグラウンド更新中)` | `操作可能(バックグラウンド更新中)` | `操作可能(バックグラウンド更新中)` |
| 全 expected phase 完了 | `初期化完了` | `スコア更新完了` | `ライブラリ更新完了` | `初期化再実行完了` |
| 失敗 | `初期化失敗` | `スコア更新失敗` | `ライブラリ更新失敗` | `初期化再実行失敗` |

## ログとガード

expected 外の request は進捗へ反映せず、ログへ出す。

```text
startup_progress_request_ignored operation=... phase=... reason=not_expected
```

skip 後に request が来ても pending には戻さず、ログへ出す。

```text
startup_progress_request_after_skip operation=... phase=...
```

Dispatcher 反映は operation token で guard される。古い operation の delayed reflect は UI に反映しない。
