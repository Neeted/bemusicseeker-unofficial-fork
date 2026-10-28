# ステータスバーに表示する処理

## 目的と適用範囲

実際に表示する日本語の処理名を起点に、所属、ゲージの単位、終了と行固有の操作を示します。行の計算・要求の対応づけは[進捗仕様](progress.md)、処理の依存と受付は[起動仕様](startup.md)と各機能仕様を正本とします。

## 用語

[共通用語集](../glossary.md)を参照します。「親行」は操作全体、「子行」はその親に対応する実行中の処理、「独立行」は現在表示中の親が待つ要求に属さない処理です。以下の件数と対象名は表示例です。

## 仕様

### 表示例

起動の必須処理と追加処理は別の親で追います。通常起動は必須処理、親受付解放、必須UIと実host接続を終えてから操作可能になります。任意登録を閉鎖して開始するまで追加処理を成功根拠にせず、実行中の初期化親は「初期化」のままです。

```text
[8/11] 初期化
  スコアの読込み・反映              （不定ゲージ）
  [120/300] 譜面情報の読込み
```

必須初期化の完了余韻中は追加処理と併記し、余韻が終了した後も、対象起動の追加処理が実終端するまでその親と子を残します。追加処理は最大3件を並列実行し、依存とレーン制限は[起動仕様](startup.md#追加処理の並列実行と依存)に従います。順位更新は起動から独立しています。

```text
[2/8] 起動に伴う追加処理
  難易度表リストの取得              （不定ゲージ）
  プレイリスト索引の準備            （不定ゲージ）
  譜面一覧の索引・ソートキャッシュの準備 （不定ゲージ）
順位の更新                          （不定ゲージ）
```

再読込みでは、実際にその操作が待つ要求だけを子にします。別要求や要求元の異なる処理を、同じ数値の要求版や処理名だけで子へ移しません。

スコア反映中の表示例です。

```text
[2/4] スコアの再読込み
  スコアの読込み・反映              （不定ゲージ）
```

同じ操作が順位更新へ進んだ時点の表示例です。

```text
[3/4] スコアの再読込み
  順位の更新                        （不定ゲージ）
```

独立した導入、推定、URL取得、編集などは同時に表示できます。行の取消・再試行はその行が所有する処理へ届きます。

```text
パッケージ・フォルダの導入 1/3件                  package.zip   [取消]
保留パッケージの導入先推定 2/4件  package.zip
プレイリストURLからの取得 1/2件                     表名         [取消]
選択プレイリストの再同期 1/3件処理済み             直近の処理終了：表名
```

### 親行

| 実表示名 | 条件 | ゲージ | 終了・状態 | 行の操作 |
| --- | --- | --- | --- | --- |
| 初期化 | 起動時の必須初期化 | 固定13段階 | 「初期化 完了」の余韻後に消す。失敗は「初期化 失敗」と明示理由を残す | なし |
| ファイル差分の再読込み | 差分再読込み | 固定6段階 | 操作名に「完了」「失敗」を添える | なし |
| スコアの再読込み | スコアだけの再読込み | 固定4段階 | 操作名に「完了」「失敗」を添える | なし |
| プレイリストの再読込み | 項目・参照と外部同期の再読込み | 固定5段階 | 操作名に「完了」「失敗」を添える | なし |
| 全再初期化 | DBとファイルから再構築 | 固定14段階 | 操作名に「完了」「失敗」を添える | なし |
| 起動に伴う追加処理 | 起動の後続登録から対象起動の実終端まで | 受理した論理仕事数と終端済み数の確定ゲージ。分母は追加受付で増え得る | 登録閉鎖・開始と対象仕事・事前準備の実終端で消す。開放中0件や一時100%では消さず、独立仕事で延命しない | なし |

親の段階数はファイル数や所要時間の割合ではありません。追加親の所属・集約・失敗と破棄の計上は[追加処理の段階と終端](progress.md#追加処理の段階と終端)に従います。未完了段階を一つ選んで親の補足へ出すことはせず、失敗理由が空なら推測した子処理名を補いません。

### 実行中の処理行

「対応親」は、捕捉した表示世代・発生元操作トークン・要求元・同主体内の要求版が、親の予定・追跡要求に一致する親です。起動由来の後続要求は「起動に伴う追加処理」へ属し、それ以外は独立行です。同じ要求の汎用通知と専用件数通知は、同名・同所属の一行へ統合します。

| 実表示名 | 所属・独立条件 | ゲージと対象 | 終了 | 操作・正本 |
| --- | --- | --- | --- | --- |
| カタログDB読込み | 初期化・全再初期化の子 | 総数未確定で不定 | DB読込み完了 | なし・[起動](startup.md#目録とファイルの読込み) |
| 譜面・リソースの探索 | 初期化・ファイル差分・全再初期化の子。探索種別を併記 | 総数未確定で不定。結果件数から総数を推測しない | 探索完了 | なし・[起動](startup.md#目録とファイルの読込み) |
| 譜面ファイルの差分確認・解析 | 初期化・ファイル差分・全再初期化の子 | 今回の解析対象譜面数と処理済み数、対象パス | 差分反映成功 | なし・[譜面読込み](../library/chart-file-reading.md) |
| LR2カスタムフォルダの変更確認 | 差分処理の子 | 管理外の確認対象ファイル数と対象パス。DB変更行数とは別 | 差分反映成功。準備件数100%では消さない | なし・[LR2同期](../integration/lr2-song-db.md) |
| 画面準備 | 起動データ準備完了から操作可能まで、初期化の子 | 不定 | 操作可能に到達 | なし・[起動](startup.md) |
| スコアの読込み・反映 | 対応親の子。それ以外は独立 | 不定 | 捕捉した要求の終端 | なし・[起動](startup.md) |
| 順位の更新 | スコア再読込み・全再初期化が待つ対応要求は子。起動では独立 | 不定 | 捕捉した要求の終端。追加親の寿命に含めない | なし・[順位](../integration/lr2-ranking.md) |
| 譜面情報の読込み | 対応親の子。それ以外は独立 | 既知の読込み総数と適用件数。未確定で不定 | 対応要求の読込み完了 | なし・[譜面情報](../library/chart-info.md) |
| 譜面情報の補完 | 対応親の子。それ以外は独立 | 対象譜面数と処理済み数、対象パス | 対応要求の補完完了 | なし・[譜面情報](../library/chart-info.md) |
| プレイリスト項目の読込み | 対応親の子。それ以外は独立 | 不定 | 対応する読込み実行の終端 | なし・[起動](startup.md) |
| プレイリスト参照の反映 | 対応親の子。起動の対応後続は追加親 | 不定 | 対応要求の終端 | なし・[保存と出力](../playlist/storage-and-export.md) |
| 外部プレイリストの同期 | 対応親の子。起動の対応後続は追加親。それ以外は独立 | 対象表数と試行終了数を「件処理済み」で表示。直近の処理終了名、空名ならURI。0件では補足なし。専用通知前は不定 | 同じ要求の複合終端 | なし・[保存と出力](../playlist/storage-and-export.md) |
| 難易度表リストの取得 | 起動の対応後続は追加親 | 不定 | 要求終端 | なし・[起動](startup.md) |
| プレイリストURLの補完 | 起動の対応後続は追加親 | 不定 | 要求終端 | なし・[起動](startup.md) |
| フォルダ一覧の更新 | 起動の対応後続は追加親 | 不定 | 要求終端 | なし・[起動](startup.md) |
| メンテナンス情報の読込み | 全再初期化が待つ対応要求は子。起動の対応後続は追加親 | 不定 | 対応要求の終端 | なし・[保守](../library/warnings.md) |
| 導入可能譜面のメンテナンス情報更新 | 全再初期化が待つ対応要求は子。起動の対応後続は追加親 | 不定 | 対応要求の終端 | なし・[起動](startup.md) |
| LR2カスタムフォルダ出力の修復 | 必須出力はInitialize/Reloadの子。同じ受理操作が実終端を待つ | 修復の準備・出力作業と同期段階の合計、現在の表名。探索ファイル数とは別 | 専用件数行は自身の終端。汎用実行行は要求終端 | なし・[カスタムフォルダ](../playlist/lr2-custom-folders.md) |
| beatoraja BMTの出力 | 必須出力はInitialize/Reloadまたは変更操作の子。同じ受理操作が実終端を待つ | 投影段階は対象表数。出力段階は投影・出力作業の合計と現在の表名 | 同じ要求の専用・汎用終端 | なし・[BMT](../playlist/bmt-export.md) |
| プレイリスト索引の準備 | 起動の対応後続は追加親 | 不定 | 要求終端 | なし・[起動](startup.md) |
| 譜面一覧の索引・ソートキャッシュの準備 | 起動の対応後続は追加親 | 不定 | 要求終端 | なし・[起動](startup.md) |
| 起動後のメモリ整理 | 起動の対応後続は追加親 | 不定 | 要求終端 | なし・[起動](startup.md) |
| LR2楽曲DB同期の準備 | 起動の対応後続は追加親 | 不定 | 登録終端 | なし・[LR2同期](../integration/lr2-song-db.md) |
| LR2楽曲DBの同期 | 必須起動の対応する実行中は初期化親。終端後の警告・未完・失敗は独立 | 翻訳した実段階と段階内件数。楽曲の短いチャンク保存は同じ有限段階に含める。量不定の準備・再帰整理・一括保存・最終確認は不定。既知0件の件数段階は省略。全体保存位置は詳細の診断 | 正常終端で消す。未完・失敗は残す | 設定画面の手動同期・[LR2同期](../integration/lr2-song-db.md#同期状態と起動)、[段階と件数](progress.md#lr2楽曲db全体同期の段階) |

ハッシュは統合された譜面情報パイプラインで補完します。独立したハッシュ補完が不要と確定する本番経路では、別の子行を出さず、親の固定分母にある段階はスキップとして完了します。

### 独立した操作行

| 実表示名 | ゲージと対象 | 終了 | 行の操作・正本 |
| --- | --- | --- | --- |
| パッケージ・フォルダの導入 | 一要求の完了入力パス数・総入力パス数。実行中の詳細対象 | 一要求の処理と後片付けの終端 | 取消・[導入](../library/drop-install.md#追加受付と画面の結果) |
| 保留パッケージの導入先推定 | 推定対象パッケージ数と適用済み数、実評価の対象名 | 開始済み全評価Taskと後片付けの終端 | なし・[導入先推定](../library/install-estimation.md#自動推定と並列処理) |
| プレイリストURLからの取得 | 完了URL数・総URL数と対象 | 取得終端 | 取消・[取得](../playlist/downloads.md) |
| 外部サービスでパッケージの入手先を検索 | 検索済み数・対象数と対象 | 検索終端 | 取消・[取得](../playlist/downloads.md) |
| 選択プレイリストの再同期 | 対象表数と試行終了数を「件処理済み」で表示。直近の処理終了名、空名ならURI。0件では補足なし | 複合終端 | なし・[保存と出力](../playlist/storage-and-export.md) |
| 表URLの取込み | 入力URL数と参照・登録などの後続段階を含む複合進捗 | 複合終端 | なし・[保存と出力](../playlist/storage-and-export.md) |
| beatorajaの表URLの取込み | 入力URL数と参照・順序反映などを含む複合進捗 | 複合終端 | なし・[表URL](../playlist/table-url-import.md) |
| プレイリストのプロパティ更新 | 対象表数と現在の表名。出力段階ではその処理名 | 複合終端 | なし・[保存と出力](../playlist/storage-and-export.md) |
| プレイリストの外部プロパティ初期化 | 対象表数と現在の表名。出力段階ではその処理名。別要求元の同期と併存 | 複合終端 | なし・[保存と出力](../playlist/storage-and-export.md) |
| メンテナンス情報の再検査 | 今回の対象譜面数と対象パス | 処理終端 | 取消・[保守](../library/warnings.md) |
| フォルダ名の自動変更 | 今回の変更対象フォルダ数と対象パス | 処理終端 | なし・[ライブラリ変更](../library/mutations.md) |

外部同期と選択プレイリストの再同期は、成功・失敗とも表単位の再取得・反映試行が終了したときに加算します。開始対象で補足名を更新せず、既存の対象選択による分母を使います。全表が処理済みでもLR2カスタムフォルダ・BMT等の複合処理の実終端まで行を維持し、親の固定5段階は変えません。逆順通知の採用は[件数と通知](progress.md#件数と通知)に従います。

プロパティ更新とサマリー一括編集は、出力段階では同じ操作行を「LR2カスタムフォルダの出力」、同期段階では「LR2カスタムフォルダ情報の同期」として更新します。件数は表の準備・出力作業と同期段階の合計であり、別の操作や探索ファイル数へ読み替えません。専用名を指定しない既存のプレイリスト同期通知は「プレイリストの同期」で表示します。

LR2の終端状態は「LR2楽曲DBの同期が必要です」「LR2楽曲DBの同期に失敗しました」「LR2楽曲DBの同期が未完了です」を独立した行に残します。正常完了した同期は状態行を消します。

表示対象の追加・削除・統合時は本表と生産・検証の対応を更新します。過去の完了を保存する台帳は持ちません。モーダル進捗、局所的な読込み表示、再生位置は対象外です。

## 実装とテストの対応

| 表示対象・リソースキー | 生産・表示 | 検証境界 |
| --- | --- | --- |
| 操作親 `Statusbar_progress_startup` / `reload_files` / `reload_scores` / `reload_tables` / `full_reinitialize`、状態書式 `operation_completed_format` / `operation_failed_format` | [`StartupProgressWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs) | [`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs): 5操作名、固定分母、空・明示失敗理由、画面準備、件数と対象 |
| DB・探索・差分・画面 `Statusbar_progress_phase_library_db_load` / `file_enumeration` / `file_diff` / `lr2_folder_file_check` / `ui_prepare` | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)からOwner | 同上と[`OperationProgressHubViewModelTests`](../../../BeMusicSeeker.Tests/MainWindow/OperationProgressHubViewModelTests.cs): 並行詳細、旧操作拒否。差分の保存境界は[進捗仕様](progress.md#lr2フォルダの確認と反映) |
| 読込み・補完・参照・保守・順位 `Statusbar_progress_phase_score_hydration` / `ranking_refresh` / `chart_info_load` / `chart_info` / `playlist_loading` / `playlist_ref` / `maintenance` / `installable_maintenance` | 既存機能所有者から[`StartupBackgroundTaskSchedulerOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupBackgroundTaskSchedulerOwner.cs)とHub | [`ChartInfoInlineHydrationTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs)、[`BmsLibraryIrStartupTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs)、[`PlaylistWorkspaceDetailRefreshTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceDetailRefreshTests.cs)、[`StartupBackgroundTaskSchedulerOwnerTests`](../../../BeMusicSeeker.Tests/Startup/StartupBackgroundTaskSchedulerOwnerTests.cs): 受付・実行・要求捕捉と終端。Owner/Hubで親一致と数値衝突を分担 |
| 起動の追加親 `Statusbar_progress_startup_additional` と後続子 `Statusbar_progress_task_*`、LR2 `Statusbar_progress_phase_lr2_song_db_sync` | [`StartupBackgroundTaskSchedulerOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupBackgroundTaskSchedulerOwner.cs)・[`StartupPostInitializationWarmupOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupPostInitializationWarmupOwner.cs)から[`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs)へ段階を投影 | [`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs)とHubで実起動の所属・必須完了後の存続・段階数・独立仕事による非延命・専用通知統合を分担。[`MainWindowProgressStatusBarWpfTests.StartupPostInitialization_ActualWorkReflectsThroughCompiledStatusBarBinding`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowProgressStatusBarWpfTests.cs)で実ProgressBarの値・最大値・確定表示、ラベル、20pxインデント、実終端後の親消去を確認 |
| 外部同期・取込み・編集・出力 `Statusbar_progress_task_external_playlist_sync` / `playlist_manual_reload` / `custom_folder_repair` / `bmt_output` / `playlist_property_update` / `playlist_external_property_initialization`、`Playlist_*progress*` / `Beatoraja_*progress*` / `Custom_folder_output_progress*` / `Custom_folder_db_sync_progress_single_label` | [`PlaylistWorkspaceViewModel.SyncProgress`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.SyncProgress.cs)、[`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)、[`PlaylistBmtOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs) | プレイリスト既存の実操作テストとHub: 生産、終端、同じ要求だけの統合、別要求非抑止・旧終端隔離。[`MainWindowPlaylistWorkspaceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)はHTTP経由の外部同期と実接続 |
| 自動・選択再同期の `Playlist_reload_processed_label_format` / `Playlist_reload_last_processed_label_format` | [`PlaylistExternalSyncOwner.ReloadPlaylistTargetsAsync`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs)からWorkspace・Hub | [`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)で初期空名、実HTTP保持中の成功・失敗2/3と解放後3/3、実BMT中のN/N・Taskと行の存続を確認。Hubの `PlaylistReloadPresentation_KeepsProcessedSnapshotAcrossReversedNotifications` は自動・手動の即時／予約配送で件数と終了対象の一体採用、同数更新・URI代替・次要求0件・対象外の従来表示を担当。実WPFの `ReloadTables_ActualMainWindowPublishesCompletedSummariesWhileAnotherHttpBodyIsPending` は2/3のTextBlock・ProgressBarのBindingを担当 |
| 導入・推定・URL・再検査・自動変更 `Drop_install_queue_label_format` / `Install_estimation_label_format` / `Playlist_url_download_progress_label_format` / `Playlist_external_package_lookup_progress_label_format` / `Maintenance_rescan_progress_label_format` / `Statusbar_progress_task_folder_rename` | 各管理主体からHub | Hubの既存件数・取消・並行ケースと[`MainWindowProgressStatusBarWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowProgressStatusBarWpfTests.cs): 実Binding、伸縮、複数行、行固有操作 |
| 全キーと六言語の整合 | `Resources.resx` / `Resources.cs` / `lang/` | [`LocalizationResourceParityTests`](../../../BeMusicSeeker.Tests/Localization/LocalizationResourceParityTests.cs)の共通検査。個別文言固定テストは増やさない |

リソース名を省略した同じ行の後続項目は、先頭の `Statusbar_progress_phase_` / `Statusbar_progress_task_` / `Statusbar_progress_` 接頭辞を共用します。

## 関連資料

[進捗](progress.md)、[起動](startup.md)、[ログ](../core/logging.md)、[画面の表示境界](../ui/dialogs.md)。
