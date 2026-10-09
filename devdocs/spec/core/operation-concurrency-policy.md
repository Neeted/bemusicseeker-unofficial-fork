# 操作の競合ポリシー

## 目的と適用範囲

アプリ内の操作を共有する変更対象から分類し、受付開始、拒否、受理済み継続と実終端を定めます。公開コマンド、セル編集、設定ボタン、自動処理にも適用します。外部のLR2・beatoraja・利用者によるファイル変更はこの受付で排他しません。保存互換性、外部変更への防御、部分成功、計算並列度は各機能仕様で定めます。

## 用語

[共通用語集](../glossary.md)の受付、操作権限、終端を使います。Lはライブラリ・保留・推定・保守の変更受付、Pはプレイリスト正本と必要な管理出力の変更受付です。論理的な受付は短いモデルロック、DBトランザクション、native資源の所有とは異なります。

## 仕様

### 分類と並行可否

| 新しい要求／実行中 | L | P | L＋P |
| --- | --- | --- | --- |
| L | Busy | 非交差なら受理 | Busy |
| P | 非交差なら受理 | Busy | Busy |
| L＋P | Busy | Busy | Busy |
| 確定済み入力の読取り | 継続可能 | 継続可能 | 継続可能 |

同じ領域は別の譜面・表でも非待機Busyです。通常の非交差L/Pはスタンドアロン・LR2連携の両方で並行できます。LR2管理出力の実再帰source、destination、DB行範囲と交差する物理ライブラリ変更はL＋Pです。交差判定は保存適用済み配置と現正本を使います。設定draft全体の別保存状態は作りません。

両受付が必要な要求は副作用前に非待機取得し、一部だけ取得して競合したら解放します。第三の全体受付はありません。通常要求は未開始Busy、全体LR2の開始競合はskipです。拒否・skipした要求を予約・再実行しません。一要求内の複数入力、成功対象の集約、内部の上限付き並列計算は別の契約として維持します。

### 受付の開始・終端と権限

形式検査と副作用のない選択・確認を終えて実行を確定したら、対象解決、通信、コピー等の準備より前に取得します。受理した機能ownerは入力取得、計算、一括保存、必要な出力と参照公開、開始した全Task、取消・失敗時のcleanupまで同じ権限を保持します。通知の受理、Running、schedulerの空き、世代の変化から完了を推定しません。

内部継続は同じownerの生存権限を明示借用し、自己再取得や自己idle待機を行いません。解放済み・別ownerの権限は拒否します。標準構成のL/P受付は構成の寿命で共有し、library・store・対象・終了状態は個別です。再構築後もPが有効であることを、旧storeの対象が有効である根拠にしません。短いロックとDB接続はawait前に解放します。

### ライブラリ・保留・保守

| 操作と公開・自動入口 | 分類理由と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 導入、drop、URL取得後の導入、保留登録・削除・手動推定・目録<br>導入・drop: [PackageInstallWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs)、URL引渡し: [PlaylistUrlAcquisition](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs)、保留登録・削除・手動推定: [PendingPackageWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs)、目録: [PackageCatalogWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PackageCatalogWorkflowOwner.cs) | L。副作用を伴う入力確保前から入力回収・推定・regroupの実終端まで。実行中の追加入力は未受理Busy。 | 新規取り込みの直接推定は同じL。起動復元pendingは手動推定だけ。[導入](../library/drop-install.md)、[推定](../library/install-estimation.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「導入→直接推定・取消/例外・全join」。 |
| 譜面削除、配置・フォルダ改名、文字コード、難度、重複除去・統合<br>譜面変更: [SelectedChartMutationWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartMutationWorkflowOwner.cs)、フォルダ改名: [RegularChartListOwner](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、重複除去・統合: [DuplicateMaintenanceWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs) | L。実対象解決から物理変更・一変更sessionのDB/索引・必要公開・cleanupまで。管理出力への交差だけL＋P。 | 統合確定後の必須保守へ同live Lを渡す。先行確定事実と元の失敗を保持。[変更](../library/mutations.md)、[整合性](../library/file-db-consistency.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「実L入口、通知/cleanup、予約なし」。 |
| 再検査、警告無視、解析失敗登録の除去、ゼロノート・譜面情報保守<br>再検査・警告無視: [SelectedChartResourceHealthWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartResourceHealthWorkflowOwner.cs)、解析失敗: [ChartInfoParseFailureRemovalWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/ChartInfoParseFailureRemovalWorkflowOwner.cs)、ゼロノート: [ZeroNoteMaintenanceWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/ZeroNoteMaintenanceWorkflowOwner.cs)、再走査: [MaintenanceRescanWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceRescanWorkflowOwner.cs) | L。操作確定後に受付し、実モデル更新と必要公開まで。 | 利用者の新要求を待機予約しない。[譜面情報](../library/chart-info.md)、[保守](../library/mutations.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「実L入口、通知/cleanup、予約なし」。 |
| 初期化・差分の必須入力更新、hydration/backfill/owned maintenance<br>初期化: [BmsLibraryInitializationService](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs)、差分: [FileDiffReloadWorkflowOwner](../../../BeMusicSeeker/ViewModels/Startup/FileDiffReloadWorkflowOwner.cs)、必須保守: [BMSLibrary.InstallableMaintenance](../../../BeMusicSeeker/Models/Library/BMSLibrary.InstallableMaintenance.cs) | Lの受理済み継続。入力変更と実Task/cleanup終端まで。 | 既に受理された必須更新は先行操作の終端を非同期で待てる。Busyで捨てない。任意通信・表示prewarmとは区別。[起動](../runtime/startup.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「設定・再構築・ScoreOnly・shutdown実終端」。 |

推定の取消・最初の評価例外では未適用の兄弟結果を以後適用せず保留に残します。以前に適用した結果と確定済み導入は保持し、全開始Task・SEARCHING解除・cleanupを待ちます。候補分類、先行成功overlay、単一候補の並列度と一括外側並列は[推定仕様](../library/install-estimation.md)の保証です。

物理対象が入力展開や候補計算の後に確定する取り込みでは、入口からLを保持し、auto-install候補から実移動先が確定し `MovePackageFilesForInstallSession` へ渡すscopeが分かった時点で停止・移動・削除・管理DB出力より先にPを取得します。P Busyの交差対象は部分結果の未処理として残し、先行する非交差の確定事実を保持します。予約は作りません。対象が初めから分かる物理変更は停止前にPを取得し、設定等の初期L+P取得失敗は操作全体を未開始とします。純粋な推定は物理作用を伴わず、Pを追加しません。

### プレイリストと必要出力

| 操作と公開・自動入口 | 分類理由と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 新規作成・既存プロパティ保存、詳細セル、フラグ、順序、項目追加・削除、表削除<br>プロパティ保存: [PlaylistPropertySaveService](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、詳細セル: [DetailEditing](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailEditing.cs)、フラグ: [PlaylistSummaryBulkEdit](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs)、保存並べ順: [PlaylistSummaryBmtSortCoordinator](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistSummaryBmtSortCoordinator.cs)、項目変更: [Mutations](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs)、表削除: [PlaylistRemovalWorkflowOwner](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs) | P。対象解決前から正本DB、一括LR2/BMT、必要参照・表示公開とcleanupまで。 | 新規draftは保存まで正本外。既存draftは元store・対象と編集開始時の保存対象値を受付時に一度確認し、退役・値変更は拒否。同名/URLへの読替え、後予約・再適用なし。[保存](../playlist/storage-and-export.md)、[プロパティ](../ui/playlist-properties.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「P通信前・有限batch・draft・保存後出力/公開失敗」。 |
| URI/JSON/collection/内蔵表登録、beatoraja Table URL取込み、外部再同期<br>登録: [ExternalPlaylistImport](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistImport.cs)、beatoraja: [BeatorajaTableUrlImport](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.BeatorajaTableUrlImport.cs)、外部同期: [ExternalPlaylistSync](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs) | P。通信・解析前から複数対象の一括登録、必要出力・公開まで。 | 任意の起動同期は新規Try Pで競合skip。親の必須同期はlive Pを借用。FIFO追加・取得後再捕捉なし。有限の一要求の成功・重複・失敗集約を維持。[URL取込み](../playlist/table-url-import.md)、[保存](../playlist/storage-and-export.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「P通信前・有限batch・draft・保存後出力/公開失敗」。 |
| 表一覧再読込み、必須entries hydration・出力修復<br>再読込み: [PlaylistTablesReloadWorkflowOwner](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistTablesReloadWorkflowOwner.cs)、実初期化・hydration継続: [BMSPlaylist.InitializeAsync](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | P。親Initialize/Reloadの直接Taskとして必要な更新・出力・公開まで。 | 同期通知から別予約へ流さない。任意のGC/表示cache/online準備を親の完了条件へ増やさない。[起動](../runtime/startup.md)、[保存](../playlist/storage-and-export.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「P通信前・有限batch・draft・保存後出力/公開失敗」。 |
| LR2管理表の部分反映、BMT生成・削除・Table URL反映<br>LR2部分反映: [BMSPlaylist](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)、BMT実出力: [PlaylistBmtOutputOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs)、変更元の必須出力待機: [PlaylistWorkspaceViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | P。変更元の同じ権限で実出力と失敗通知まで。 | 同期BMSPlaylist核は有限の変更事実を返し、機能ownerが実出力をawait。部分集合で全量stale cleanupをしない。[LR2出力](../playlist/lr2-custom-folders.md)、[BMT](../playlist/bmt-export.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「P通信前・有限batch・draft・保存後出力/公開失敗」。 |

再同期前の古い詳細行から届く編集は、同じ永続ID・hash・title等でも最新項目へ読み替えて保存しません。P取得後に元の表・項目参照を確認して拒否し、現行DB・表示を維持して最新行からの再操作を通知します。通常の閲覧用参照更新と、最新行からの新しい編集は維持します。

DB確定後の出力・公開失敗は確定DBを保持する部分失敗です。未変更省略、manifest互換、管理外ファイル、生成並列と準備資源再利用を維持し、失敗をlog-only成功に変えません。出力用ハッシュ補完は確定済み読取りモデルを利用しLを取りません。進行中の管理配置移行はprepared旧先・新先を実終端まで保護します。新配置確定後の旧先削除失敗では新配置、残余、元警告を保ち、次の日常処理は新配置を使い旧先のcleanupを再開しません。全過去残余の永久履歴は持ちません。

### 設定・全体操作・終了

| 操作と公開・自動入口 | 分類理由と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 設定Save/Apply、root追加・解除、mode再起動保存<br>[SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)の`ApplySettingsAsync`、`SaveSettings`、検索root追加・解除、`ConfirmAndRestartForOperationModeChange` | 常にL＋P。保存・適用・準備前から必要な後処理まで。 | Busyは保存/再読込/後処理0、draft保持。共有Valuesへの編集は維持し、Busy Save直前との比較で副作用を判定。ScoreOnly/FileDiff/Allへ同live権限を渡す。[設定](../runtime/settings.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「設定・再構築・ScoreOnly・shutdown実終端」。 |
| All再初期化、全体復元・撤去、履歴schema変更<br>再初期化: [MainWindowViewModel.InitializeAsync](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、復元: [PlaylistRestore](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistRestore.cs)、撤去・schema: [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | L＋P。変更開始前から新storeの必須初期化・公開、必要な出力・cleanupまで。 | 短いDB/schema保護は別に維持。終了が必要な操作は自己lease待機を作らない。[起動](../runtime/startup.md)、[保存](../playlist/storage-and-export.md)、[終了](../runtime/shutdown.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「設定・再構築・ScoreOnly・shutdown実終端」。 |
| 全体LR2同期（手動・条件付きFileDiff・自動起動）<br>[Lr2SongDbSyncWorkflowOwner](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) → [Lr2SongDbSyncRequestCoordinator.QueueAsync](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncRequestCoordinator.cs)。差分入口は[FileDiffReloadWorkflowOwner](../../../BeMusicSeeker/ViewModels/Startup/FileDiffReloadWorkflowOwner.cs) | L＋P。設定入力捕捉・Running公開・準備前にTry。競合は未実行skip。 | 開始後は同権限で実workerと保存・cleanupを直接await。内側でscheduler枠や第二受付を待たない。確定済みpartialと未実施wholeを偽Completedにしない。[LR2同期](../integration/lr2-song-db.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「共通規則、live/失効/別owner、通常L/P、交差・両取得」。 |
| 通常Close、mode再起動、更新適用終了<br>[MainWindow.OnClosing](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) → [ShellShutdownWorkflowOwner](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | 新規受付をcloseし、規定の取消後に全受理済みL/P・音声・DB・cleanupの実終端を待つ。 | 構成が共有するL/Pの新規Try受付をClose開始時に不可逆に閉じる。生存権限の借用と既に登録済みの必須背景継続は保持し、受付を再開しない。CloseをBusyで放置しない。shutdown要求通知だけをUI Closed・実終端として扱わない。[終了](../runtime/shutdown.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「設定・再構築・ScoreOnly・shutdown実終端」。 |

専用のディレクトリ前処理警告は、未実行の失敗を確定し全開始Task・cleanupを回収した後、L/Pを連続して解放してから提示します。警告自体では設定変更・必要出力・参照公開を行わず、提示後の新しい明示要求を受け付けます。必要な出力やcleanupの途中で解放する例外ではありません。

保存適用済み配置の公開は設定の永続化段階の正常完了時です。部分保存・確定後失敗の事実は[設定仕様](../runtime/settings.md)に従います。一操作で必要な小さい設定入力を捕捉し非同期準備へ明示的に渡します。次の明示操作は新しい入力を捕捉します。

### 音声と独立した読取り

| 操作と入口 | 分類・寿命・理由 | 確認先 |
| --- | --- | --- |
| 新規試聴・Next<br>[PlaybackPanelViewModel](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | L中Busy。通常試聴同士の既存受理・順次実行は維持。 | player開始・保留copy前に判定。変更側は必要な既受理準備・停止・回収を待つ。停止はBusyにしない。推定開始だけで通常再生を止めない。[音声](../runtime/audio.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「URLstaging・JSON・音声native実寿命」。 |
| 一時copy試聴・音声変換<br>一時試聴: [PlaybackPanelViewModel](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、変換: [SelectedChartAudioConversionWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs) | L。入力利用から一時入力・encoder・session/nativeの実解放まで。 | [音声](../runtime/audio.md)、[一時ファイル](managed-temp-files.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「URLstaging・JSON・音声native実寿命」。 |
| 譜面URLダウンロード・外部package検索<br>[PlaylistUrlAcquisition](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs) | 変更不能入力の確保なのでP不要。全取得pathを一度L導入へ渡す。 | Busyは予約なし。取得成功と導入受理/確定を別報告。未受理アプリ所有stagingは取得側が回収をawait、受理済み入力は導入実終端まで保持。持込み/利用者指定保存物は削除しない。[取得](../playlist/downloads.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「URLstaging・JSON・音声native実寿命」。 |
| JSON出力、SQL backup<br>JSON: [PlaylistExport](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs)、backup: [PlaylistBackup](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistBackup.cs) | 整合した読取り。JSONは出力値だけ加工しData_urlを書換えない。 | [保存](../playlist/storage-and-export.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「URLstaging・JSON・音声native実寿命」。 |
| 閲覧・検索・表示並替・選択、設定閲覧/編集/取消、entries表示hydration、URL補完、prewarm、GC<br>表示・選択: [RegularChartListOwner](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、設定編集: [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)、派生表示: [PlaylistWorkspaceViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | 確定済み入力・派生cacheを使う独立処理。 | 独立した表示世代・対象識別・native/読取り寿命は維持。正本更新が必要なら対応領域へ分類し直す。[データと索引](data-and-indexes.md)、[画面](../ui/README.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「独立表示cacheの失効・再接続・現行性」。 |
| 外部リンク・Explorer・外部player/Score Viewer、順位cache、音声能力照会・デバイステスト<br>外部起動: [MainWindowViewModel](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、音声照会・テスト: [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | 外部起動/候補取得をL/P変更に含めない。独立音声sessionの既存所有と終了待ちを維持。 | [音声](../runtime/audio.md)、[外部連携](../integration/README.md)。通信・外部processとの全競合を確認済みとは扱わない。 確認: [実装とテストの対応](#実装とテストの対応)の「独立表示cacheの失効・再接続・現行性」。 |

### 新機能・変更時の手順

実装前に本番入口と読む/変える共有物を既存操作群へ対応付けます。新しい分類・例外が必要なら利用上必要な並行性を先に判断し、未確定事項は利用者へ確認します。開始・終端・Busy・継続の期待を本仕様で確定し、機能仕様には固有処理を記します。既存の小さい受付検証、各入口への実接続、代表的な横断効果で保証を分担し、操作の全直積や行数に対応するテストは増やしません。同じ変更で本仕様、機能参照、実装・検証対応を更新します。調査履歴や重複した競合台帳は残しません。

## 実装とテストの対応

| 保証・境界 | 実装 | 検証・確認の分担 |
| --- | --- | --- |
| 共通規則、live/失効/別owner、通常L/P、交差・両取得 | [ChartFileOperationSynchronizer](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartFileOperationSynchronizer.cs)、[ApplicationComposition](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs) | [ChartFileOperationSynchronizerTests](../../../BeMusicSeeker.Tests/ChartOperations/ChartFileOperationSynchronizerTests.cs)、[ApplicationCompositionAdmissionTests](../../../BeMusicSeeker.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs)、[BmsLibraryMutationBoundaryTests](../../../BeMusicSeeker.Tests/Library/BmsLibraryMutationBoundaryTests.cs)。設定/全体LR2実入口の両取得とDBを併せて確認。 |
| 実L入口、通知/cleanup、予約なし | [SelectedChartMutationWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartMutationWorkflowOwner.cs)、[SelectedChartResourceHealthWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartResourceHealthWorkflowOwner.cs)、[ChartInfoParseFailureRemovalWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/ChartInfoParseFailureRemovalWorkflowOwner.cs) | 対応するownerテストの実モデル・小DB、[RegularChartFolderRenameTests](../../../BeMusicSeeker.Tests/ChartList/RegularChartFolderRenameTests.cs)のBusy/次明示要求。 |
| P通信前・有限batch・draft・保存後出力/公開失敗 | [PlaylistWorkspaceViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs)、[PlaylistPropertySaveService](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、[PlaylistBmtOutputOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs) | [PlaylistWorkspaceExternalSourceTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs)、[PlaylistWorkspaceActionWorkflowTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[BmsPlaylistPersistenceLifecycleTests](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)、[BmsPlaylistMigrationAndRegistrationTests](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)。[MainWindowPlaylistWorkspaceWpfTests.ResyncedDetailEdit_RejectsOldRowThroughOwnedNotificationAndSavesCurrentRow](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)は実再同期後の旧行拒否、実所有windowへの通知、最新DB/項目保全と最新行の明示保存を一つの本番接続で確認する。実DB/FS・出力barrierを上位判定、BMT形式・並列資源再利用は[BmtTableExportServiceTests](../../../BeMusicSeeker.Tests/Playlist/BmtTableExportServiceTests.cs)が分担。 |
| 設定・再構築・ScoreOnly・shutdown実終端 | [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)、[ShellShutdownWorkflowOwner](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | [SettingDialogEditCompletionTests](../../../BeMusicSeeker.Tests/Settings/SettingDialogEditCompletionTests.cs)、[MainWindowViewHostTests](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewHostTests.cs)、[MainWindowPlaylistWorkspaceWpfTests](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)。同live権限、実store/score/保存、Task・出力・終了順、mode選択のBusy draft保持と次明示保存を確認。schema・撤去は[Lr2PlayHistorySchemaUiTests](../../../BeMusicSeeker.Tests/PlayHistory/Lr2PlayHistorySchemaUiTests.cs)と[ApplicationDataUninstallWorkflowOwnerTests](../../../BeMusicSeeker.Tests/Settings/ApplicationDataUninstallWorkflowOwnerTests.cs)、root追加・解除は設定の実差分継続で両受付と実終端を分担。 |
| URLstaging・JSON・音声native実寿命 | [PlaylistUrlAcquisition](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs)、[SelectedChartAudioConversionWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs) | [PlaylistUrlAcquisitionOwnershipTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlAcquisitionOwnershipTests.cs)、[PlaylistWorkspaceActionWorkflowTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[SelectedChartAudioConversionWorkflowOwnerTests](../../../BeMusicSeeker.Tests/ChartOperations/SelectedChartAudioConversionWorkflowOwnerTests.cs)。JSON実serializer時の正本不変、実導入の入力終端、native解放を確認。 |
| 導入→直接推定・取消/例外・全join | [PackageInstallWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs)、[BMSLibrary.PackageInstall](../../../BeMusicSeeker/Models/Library/BMSLibrary.PackageInstall.cs) | [PackageInstallWorkflowOwnerEstimationTests](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs)、[BmsLibraryPackageInstallServiceTests](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs)。先行成功overlay、SEARCHING、実確定事実は機能仕様の下位保証も維持。 |

各入口の分類・権限転送は本番接続の静的確認も分担します。テストで同じ内部処理順を再生成して判定せず、実受理、副作用、保存事実と全実終端を観測します。独立表示cacheの失効・再接続・現行性は各機能の既存テストで維持します。

## 関連資料

[一般の並行設計](workflow-concurrency.md)、[アーキテクチャ](architecture.md)、[テスト設計](../development/test-authoring.md)、[検証](../development/testing.md)。
