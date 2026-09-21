# 初期化・再読込みの進捗

## 目的と適用範囲

`StartupProgressWorkflowOwner` が管理する進捗、操作の識別、完了の順序、後続処理の表示を定めます。処理の内容と準備条件は[起動仕様](startup.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 所有と計算

表示は `ProgressHub.StartupProgress` の `Label`、`SubLabel`、`Value`、`Maximum`、`IsActive` を使います。画面の親は処理の接続を担当し、進捗の状態、操作トークン、表示の書込みは専用の所有者へ集約します。

開始時に予定する段階を固定し、最大値をその件数、現在値を予定済みかつ完了済みの件数とします。後から予約した処理で分母を増やしません。不要な段階はスキップの確定後に完了とします。背景への要求が必要な段階を、要求・スキップの決定前に完了させません。

操作トークンは共通セマフォの取得後、実際の開始直前に作ります。初回はモデルの作成後に基準を捕捉します。遅延表示、フォルダ更新、外部同期、参照反映は、予約時と現在のトークンが一致する場合だけ段階を進めます。

### 操作ごとの予定

| 操作 | 件数 | 対象 |
| --- | ---: | --- |
| `Startup` | 13 | 開始、DB・列挙・差分、データ・画面・操作可能、スコア、ハッシュ、譜面情報の読込み・補完、プレイリスト項目、必須処理の収束 |
| `FullReinitialize` | 14 | 開始、DB・列挙・差分、操作可能、参照、スコア・順位、保守・導入可能譜面の保守、ハッシュ、譜面情報の読込み・補完、項目 |
| `ReloadFileDiff` | 6 | 開始、列挙、差分、操作可能、参照、項目 |
| `ScoreOnly` | 4 | 開始、操作可能、スコア、順位 |
| `ReloadTables` | 5 | 開始、操作可能、参照、外部同期、項目 |

起動では参照、外部同期、保守、導入可能譜面の保守、順位を初期の予定へ入れません。前四つは後続処理、順位は独立した処理または別操作の段階です。

### 段階の意味

| 識別子 | 完了する条件 |
| --- | --- |
| `CoreInitializeStarted` | 操作を開始した |
| `LibraryDatabaseLoadDone` | 目録のDB読込みを完了した |
| `LibraryFileEnumerationDone` | 譜面・リソースの列挙を完了した |
| `LibraryFileDiffDone` | 差分を反映した |
| `StartupReadyData` | 導入判定用データが揃った |
| `StartupReadyUi` | 必須の表示を適用した |
| `StartupReadyOperable` | 通常入力を解禁し、スケジューラーを開始した |
| `ScoreHydrationDone` | 現在のスコアを適用した |
| `ChartDigestBackfillDone` | ハッシュの補完を完了または不要と確定した |
| `ChartInfoHydrationDone` | 現在の譜面情報を読み込んだ |
| `ChartInfoBackfillDone` | 譜面情報の補完を完了または不要と確定した |
| `PlaylistEntriesHydrationDone` | ローカルのプレイリスト項目を読み込んだ |
| `StartupBackgroundTasksDone` | 必須処理の登録を閉じ、待機・実行中の必須要求がなくなった |
| `PlaylistReferenceApplied` | 参照を適用した |
| `ExternalPlaylistSyncDone` | 外部同期を終えた |
| `MaintenanceDeferredDone` | 保守結果の読込みを終えた |
| `InstallableMaintenanceDeferredDone` | 導入可能譜面の保守を終えた |
| `RankingRefreshDone` | 順位更新を終えた |

必須処理が一時的に空でも、登録を閉じる前に `StartupBackgroundTasksDone` を完了させません。後続処理が残っていても必須段階は完了できます。自動LR2同期は分母・値・成功・失敗のいずれにも含めません。

### 表示と後続処理の区別

表示は少なくとも開始、DB読込み、列挙、差分、画面準備、各種情報の読込み、必須処理の完了を区別します。件数を持つ処理は補助表示に処理済み件数・総数と対象を出せます。長い文字列は省略し、全文をツールチップへ渡します。

`startup_post_initialization_maintenance_complete` は進捗の段階ではありません。後続の登録と動的な追加が閉じ、スケジューラーが一度空になってから任意の事前計算を一回登録し、それも含めて再び空、未完の事前計算0になったことを記録します。不要なLR2同期には存在しない要求への依存を作りません。独立した順位・XML更新と遅延表示はこの記録に含めません。

操作可能の直前から、スケジューラーの後続処理を示すプロセス内の表示状態を有効にします。別の永続状態や処理件数として扱いません。起動進捗が消え、プレイリストとLR2の専用表示もないときだけ、不定進捗として表示します。

専用のLR2の実行中・未完・失敗・再試行可能な表示は一般の背景表示より優先します。専用表示が消えても全体が未完なら背景表示へ戻ります。起動進捗の余韻表示中は専用表示を一時的に隠せますが、消えた後には最新の非終端状態を表示します。

背景表示の終了条件は後続完了の記録と同じです。順位・XML・遅延表示で寿命を延ばしません。別の操作が起動を置き換えたときは古い表示を無効にします。終了時の画面破棄後までプロセス内の値が残ることは許容します。

### LR2フォルダ反映の中間値

差分内に準備済みのLR2フォルダ反映がある場合は、空白でない実体化済み要求パス数を分母として固定します。準備中の件数は0～総数の範囲で単調に進め、複数件では終端より前に厳密な中間値を少なくとも一回報告します。

表示の集約でも最初の中間値を一枠だけ保持します。一回の排出は一つの表示値を公開し、後続値が残ればLR2専用の制限されたBackground継続へ渡します。通常の差分表示へこの例外を広げません。

反映成功後に差分完了を同じ表示スケジューラーのBackgroundへ予約し、未排出の進捗を先に公開してから `LibraryFileDiffCompletedVersion` を進めます。観測順序は中間値、終端値、差分完了です。表示スケジューラーがない場合の同期完了は維持します。反映失敗を成功完了へ変換せず、進捗の通知例外でもDB反映・取消・失敗の意味を変えません。

### 失敗と変更時の制約

失敗は操作を失敗状態にし、通常の使用中と再試行可能な失敗を分けます。進捗完了を理由に作業スレッドからUIの集合を直接変えません。計測を短く見せるために必須処理を後続へ移したり、後続通信を起動進捗へ戻して通常操作を止めたりしません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 予定と完了の集合、古い通知の拒否、表示と失敗 | [`StartupProgressWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs) | [`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) |
| 必須の登録終了、後続の収束と一回の事前計算 | [`StartupBackgroundTaskSchedulerOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupBackgroundTaskSchedulerOwner.cs) | [`StartupBackgroundTaskSchedulerOwnerTests`](../../../BeMusicSeeker.Tests/Startup/StartupBackgroundTaskSchedulerOwnerTests.cs)、[`StartupPostInitializationWarmupOwnerTests`](../../../BeMusicSeeker.Tests/Startup/StartupPostInitializationWarmupOwnerTests.cs) |
| LR2反映と差分完了、表示の順序 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs)、[`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) |
| 画面の進捗管理主体と処理の接続、表示優先順位 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs) | [`MainWindowProgressStatusBarWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowProgressStatusBarWpfTests.cs) |

## 関連資料

[起動](startup.md)、[設定](settings.md)、[終了](shutdown.md)、[性能](../core/performance-and-scale.md)を参照します。
