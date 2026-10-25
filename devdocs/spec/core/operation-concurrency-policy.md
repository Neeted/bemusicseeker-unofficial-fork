# 操作の競合ポリシー

## 目的と適用範囲

アプリ内の操作を共有する変更対象から分類し、受付開始、拒否、受理済み継続と実終端を定めます。公開コマンド、セル編集、設定ボタン、自動処理にも適用します。外部のLR2・beatoraja・利用者によるファイル変更はこの受付で排他しません。保存互換性、外部変更への防御、部分成功、計算並列度は各機能仕様で定めます。

## 用語

[共通用語集](../glossary.md)の受付、操作権限、終端を使います。Lはライブラリ・保留・推定・保守の変更受付、Pはプレイリスト正本と必要な管理出力の変更受付です。論理的な受付は短いモデルロック、DBトランザクション、native資源の所有とは異なります。

## 仕様

設定の失敗状態と一覧編集の終端は、新しい保存・生成契約へ移行します。実装・回帰置換が未完了の範囲は[リリース前の必須修正](../../plan/release-blocking-fixes.md)を参照します。

### 分類と並行可否

| 新しい要求／実行中 | L | P | L＋P |
| --- | --- | --- | --- |
| L | Busy | 非交差なら受理 | Busy |
| P | 非交差なら受理 | Busy | Busy |
| L＋P | Busy | Busy | Busy |
| 確定済み入力の読取り | 継続可能 | 継続可能 | 継続可能 |

同じ領域は別の譜面・表でも非待機Busyです。通常の非交差L/Pはスタンドアロン・LR2連携の両方で並行できます。LR2管理出力の実再帰source、destination、DB行範囲と交差する物理ライブラリ変更はL＋Pです。交差判定は保存適用済み配置と現正本を使います。設定draft全体の別保存状態は作りません。

両受付が必要な要求は副作用前に非待機取得し、一部だけ取得して競合したら解放します。第三の全体受付はありません。通常要求は未開始Busy、全体LR2の開始競合はskipです。拒否・skipした要求を予約・再実行しません。一要求内の複数入力、成功対象の集約、内部の上限付き並列計算は別の契約として維持します。

設定画面の未確定BMS・LR2詳細候補は実操作の受付とは区別します。競合する別編集への切替では対象範囲名を示して破棄を確認し、既存の画面編集可否と実処理の受付を再検査します。候補編集自体へL/P制限は追加せず、実処理を伴う即時保存では実L/P受付と最終検証後の採用点で関係候補だけを破棄します。独立導入先の項目保存にはL/P制限を追加せず、BMS候補破棄の確認だけを既存設定担当の単一操作所有へ渡します。取消・同値・不正入力・受付拒否は元候補を保ち、新要求を予約しません。稼働環境変更は破棄案内と再起動確認を一回にまとめ、Shellの先着受付拒否時にも候補を保ちます。保存済み媒体や受理済み処理の不変入力は破棄対象に含めません。操作ごとの範囲は[設定の候補切替](../runtime/settings.md#競合する候補の切替)を正本とします。

### 受付の開始・終端と権限

形式検査と副作用のない選択・確認を終えて実行を確定したら、対象解決、通信、コピー等の準備より前に取得します。受理した機能ownerは入力取得、計算、一括保存、必要な出力と参照公開、開始した全Task、取消・失敗時のcleanupまで同じ権限を保持します。通知の受理、Running、schedulerの空き、世代の変化から完了を推定しません。

内部継続は同じownerの生存権限を明示借用し、自己再取得や自己idle待機を行いません。解放済み・別ownerの権限は拒否します。標準構成のL/P受付は構成の寿命で共有し、library・store・対象・終了状態は個別です。再構築後もPが有効であることを、旧storeの対象が有効である根拠にしません。短いロックとDB接続はawait前に解放します。

### ライブラリ・保留・保守

| 操作と公開・自動入口 | 分類理由と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 導入、drop、URL取得後の導入、保留登録・削除・手動推定・目録<br>導入・drop: [PackageInstallWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs)、URL引渡し: [PlaylistUrlAcquisition](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs)、保留登録・削除・手動推定: [PendingPackageWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs)、目録: [PackageCatalogWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PackageCatalogWorkflowOwner.cs) | L。副作用を伴う入力確保前から推定・再グループ化・後段導入・必要同期・必須公開・入力回収の実終端まで。実行中の追加入力は未受理Busy。 | 新規取り込みの直接推定と設定有効時の後段導入は同じLを借用。起動復元pendingは手動推定だけ。[導入](../library/drop-install.md)、[推定](../library/install-estimation.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「導入→直接推定・後段導入・取消/例外・全join」。 |
| 譜面削除、配置・フォルダ改名、文字コード、難度、重複除去・統合<br>譜面変更: [SelectedChartMutationWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartMutationWorkflowOwner.cs)、フォルダ改名: [RegularChartListOwner](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、重複除去・統合: [DuplicateMaintenanceWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs) | L。実対象解決から物理変更・一変更sessionのDB/索引・必要公開・cleanupまで。管理出力への交差だけL＋P。 | 統合確定後の必須保守へ同live Lを渡す。先行確定事実と元の失敗を保持。[変更](../library/mutations.md)、[整合性](../library/file-db-consistency.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「実L入口、通知/cleanup、予約なし」。 |
| 再検査、警告無視、解析失敗登録の除去、ゼロノート・譜面情報保守<br>再検査・警告無視: [SelectedChartResourceHealthWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartResourceHealthWorkflowOwner.cs)、解析失敗: [ChartInfoParseFailureRemovalWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/ChartInfoParseFailureRemovalWorkflowOwner.cs)、ゼロノート: [ZeroNoteMaintenanceWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/ZeroNoteMaintenanceWorkflowOwner.cs)、再走査: [MaintenanceRescanWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceRescanWorkflowOwner.cs) | L。操作確定後に受付し、実モデル更新と必要公開まで。 | 利用者の新要求を待機予約しない。[譜面情報](../library/chart-info.md)、[保守](../library/mutations.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「実L入口、通知/cleanup、予約なし」。 |
| 初期化・差分の必須入力更新、hydration/backfill/owned maintenance<br>初期化: [BmsLibraryInitializationService](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs)、差分: [FileDiffReloadWorkflowOwner](../../../BeMusicSeeker/ViewModels/Startup/FileDiffReloadWorkflowOwner.cs)、必須保守: [BMSLibrary.InstallableMaintenance](../../../BeMusicSeeker/Models/Library/BMSLibrary.InstallableMaintenance.cs) | Lの受理済み継続。入力変更と実Task/cleanup終端まで。 | 既に受理された必須更新は先行操作の終端を非同期で待てる。Busyで捨てない。任意通信・表示prewarmとは区別。[起動](../runtime/startup.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「設定・再構築・ScoreOnly・shutdown実終端」。 |

推定の取消・最初の評価例外では未適用の兄弟結果を以後適用せず保留に残します。以前に適用した結果と確定済み導入は保持し、全開始Task・SEARCHING解除・cleanupを待ちます。候補分類、先行成功overlay、単一候補の並列度と一括外側並列は[推定仕様](../library/install-estimation.md)の保証です。

物理対象が入力展開や候補計算の後に確定する取り込みでは、入口からLを保持し、auto-install候補から実移動先が確定し `MovePackageFilesForInstallSession` へ渡すscopeが分かった時点で停止・移動・削除・管理DB出力より先にPを取得します。P Busyの交差対象は部分結果の未処理として残し、先行する非交差の確定事実を保持します。予約は作りません。対象が初めから分かる物理変更は停止前にPを取得し、設定等の初期L+P取得失敗は操作全体を未開始とします。純粋な推定は物理作用を伴わず、Pを追加しません。自動推定後の導入は再グループ化後の実source・destination・親片付け範囲で候補ごとに追加Pを確認し、競合する候補だけを未開始の保留へ残します。許可された候補は同じLで一つの後段バッチへ渡し、書込み前に再生停止をawaitします。取得した追加Pは必要な公開・同期・入力回収まで保持し、Busy対象の予約・自動再実行は行いません。

### プレイリストと必要出力

一覧セル編集のP（詳細・表属性）とL（譜面フォルダ名・保留導入先）は、現在表示への必要な確定値反映と編集cleanupの実Task終端まで保持します。短いmodel保護・抑制・変更activityの終了を論理受付の解放と同一視しません。必要表示後の独立したsort・全再構築は受付を延ばす理由にせず、取消・stale・Busyの既存分類を保ちます。保証の分担は[一覧セル編集](../ui/table-view.md#実装とテストの対応)を参照します。

既存・新規プロパティと一括編集の準備もPの非待機取得を通します。旧選択の同ID解決から初期値・セッション・画面用ViewModel生成まで保持し、await中も解放しません。空・解決不能は全体拒否し、重複する現対象をまとめます。準備の成功・失効・例外で解放し、`ShowWindowAsync`より前に終端します。画面の表示中は別のP操作を許可するため、保存時の現行性検証を維持します。一覧セル保存は既取得Pを借用し、同じPの空き状態を再要求しません。

一括六操作はP取得・借用後、差分抽出・属性変更・通信前に、開始時のstoreと同一正本参照を全対象で検証します。全件・一部失効、同ID別参照、別storeは全体拒否とし、失効した無変更対象だけを除くことも行いません。拒否ではDB・管理出力・入力を保持します。取得失敗・空属性・出力競合など、外部属性初期化の既存の対象別除外は維持します。起動時の外部同期予約・スケジューラーはこの受付で変更しません。

| 操作と公開・自動入口 | 分類理由と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 新規作成・既存プロパティ保存、詳細セル、フラグ、順序、項目追加・削除、表削除<br>プロパティ保存: [PlaylistPropertySaveService](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、詳細セル: [DetailEditing](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailEditing.cs)、フラグ: [PlaylistSummaryBulkEdit](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs)、保存並べ順: [PlaylistSummaryBmtSortCoordinator](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistSummaryBmtSortCoordinator.cs)、項目変更: [Mutations](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs)、表削除: [PlaylistRemovalWorkflowOwner](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs) | P。対象解決前から正本DB、一括LR2/BMT、必要参照・表示公開とcleanupまで。 | 新規draftは保存まで正本外。既存draftは元store・対象と編集開始時の保存対象値を受付時に一度確認し、退役・値変更は拒否。同名/URLへの読替え、後予約・再適用なし。[保存](../playlist/storage-and-export.md)、[プロパティ](../ui/playlist-properties.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「P通信前・有限batch・draft・保存後出力/公開失敗」。 |
| 共通選択からの外部表・内蔵表・URI/JSON登録、beatoraja Table URL取込み、外部再同期<br>登録: [ExternalPlaylistImport](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistImport.cs)、beatoraja: [BeatorajaTableUrlImport](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.BeatorajaTableUrlImport.cs)、外部同期: [ExternalPlaylistSync](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs) | P。入力検証後、登録済みURLの最終照合・通信・解析前から複数対象の一括登録、必要出力・公開まで。 | インポートのBusyは選択を保持して拒否し、次の明示要求だけを受ける。任意の起動同期は新規Try Pで競合skip。親の必須同期はlive Pを借用。FIFO追加・取得後再捕捉なし。有限の一要求の成功・同URL・同名・失敗集約を維持。[インポート](../playlist/import.md)、[URL取込み](../playlist/table-url-import.md)、[保存](../playlist/storage-and-export.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「P通信前・有限batch・draft・保存後出力/公開失敗」。 |
| 表一覧再読込み、必須entries hydration・出力修復<br>再読込み: [PlaylistTablesReloadWorkflowOwner](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistTablesReloadWorkflowOwner.cs)、実初期化・hydration継続: [BMSPlaylist.InitializeAsync](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | P。親Initialize/Reloadの直接Taskとして必要な更新・出力・公開まで。 | 同期通知から別予約へ流さない。任意のGC/表示cache/online準備を親の完了条件へ増やさない。[起動](../runtime/startup.md)、[保存](../playlist/storage-and-export.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「P通信前・有限batch・draft・保存後出力/公開失敗」。 |
| LR2管理表の部分反映、BMT生成・削除・Table URL反映<br>LR2部分反映: [BMSPlaylist](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)、BMT実出力: [PlaylistBmtOutputOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs)、変更元の必須出力待機: [PlaylistWorkspaceViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | P。変更元の同じ権限で実出力と失敗通知まで。 | 同期BMSPlaylist核は有限の変更事実を返し、機能ownerが実出力をawait。部分集合で全量stale cleanupをしない。[LR2出力](../playlist/lr2-custom-folders.md)、[BMT](../playlist/bmt-export.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「P通信前・有限batch・draft・保存後出力/公開失敗」。 |

再同期前の古い詳細行から届く編集は、同じ永続ID・hash・title等でも最新項目へ読み替えて保存しません。P取得後に元の表・項目参照を確認して拒否し、現行DB・表示を維持して最新行からの再操作を通知します。通常の閲覧用参照更新と、最新行からの新しい編集は維持します。

DB確定後の出力・公開失敗は確定DBを保持する部分失敗です。未変更省略、manifest互換、管理外ファイル、生成並列と準備資源再利用を維持し、失敗をlog-only成功に変えません。出力用ハッシュ補完は確定済み読取りモデルを利用しLを取りません。進行中の管理配置移行はprepared旧先・新先を実終端まで保護します。新配置確定後の旧先削除失敗では新配置、残余、元警告を保ち、次の日常処理は新配置を使い旧先のcleanupを再開しません。全過去残余の永久履歴は持ちません。

### 設定・全体操作・終了

設定から明示する非同期実操作は設定ViewModelが同時一件だけ受け付けます。picker・確認より前に拒否し、無効表示と実入口を揃えます。この設定内の受付はL/P権限を代用せず、受理済みcoreは設定受付を取り直さずに既存L/P受付と最終検査へ進みます。無関係な同期項目保存とページ移動、自動音声列挙・能力照会はこの単一操作へ統合しません。通常使用中は実操作・明示音声テスト中もClose・Escと再表示を許可し、候補だけを破棄します。受理済み入力と同じTask・操作制限は設定担当がWindowを越えて保持します。初回利用開始・再起動・復元・撤去は既存の必須終端まで通常閉鎖を保留します。通常閉鎖や手動同期の先行自動閉鎖後も、Shellは設定担当の同じTaskを直接joinします。

| 操作と公開・自動入口 | 分類理由と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 通常の設定項目・局所候補編集・取消、音声・画像・再生方式・recordの同期局所確定 | L/P不要。成立した対象だけを選択保存し成功後に公開。 | 別セクションの候補を混ぜず、通知や処理待ち中も無関係な通常項目と独立同期確定は保存できる。音声テスト・環境開始/再起動の制約は維持する。[設定](../runtime/settings.md)。 |
| BMS一覧の局所確定・スコア取得元の有効選択 | ライブラリ依存処理の保存前から必要継続までL＋P。BMSは最終一覧一回、scoreはスコアだけ。 | Busyは保存・実処理0で候補保持。実処理・cleanup→親受付解放→必須UI・通知の実終端まで同じ設定ViewModelが受理済み要求とTaskを所有する。通常Close・Escと再表示でも、この所有と操作制限を継続する。 |
| BMTの局所確定・同値全表再更新 | 保存前から全表投影・URL反映までP。通常項目には昇格させない。 | 最新入力の再保存とforce投影を許容し、過去の成功段階は保持しない。既存担当を使い、管理外データとDB元hashを保つ。 |
| beatorajaのroot・player・派生パスの有効選択 | 必要なスコア・BMT更新の保存前からL＋P。既存担当でscore-only、旧新BMT・URL反映を同じ操作として待つ。 | 保存済みBMT方針を使い未確定候補を混ぜない。同じ操作の旧新入力は終端まで保持し、次の変更は最新入力から処理する。BMT失敗でも採用済みrootの必要score処理を回収する。再起動しない。通常起動の準備から必須UI終端までは受け付けない。[設定](../runtime/settings.md#beatorajaの局所反映)。 |
| 出力先の明示選択・追加削除改名 | 保存前から設定・検索root・表header DBの保存、公開・生成・今回旧先整理の終端までL＋P。 | 捕捉済み今回入力を終端まで使い、次の明示操作は最新入力を再保存・再生成する。旧先整理失敗だけで次の設定変更を制限しない。通常型が使う追加先名と配置定義の不一致は該当表の出力を止め、通常先へ読み替えない。ROOT型は追加先名を使わずROOT出力を続ける。 |
| モード・LR2パス反映と再起動・初回の明示開始 | 稼働中は先着受付と必要L＋Pを通しmode・LR2組だけ保存。個別DB/XMLは詳細欄でまとめて確定。初回は確定済必須設定で一回初期化。 | モード・ルート選択は有効な組で確認へ進み、取消は保存済み値へ戻す。再起動受理後は既存Close/join→後継起動。通常起動の準備から必須UI終端までは重複開始と環境反映を拒否。初回は必須BMT/UI/host公開と初回完了まで同じWindowを保つ。 |
| 通常起動の必須手続き<br>[StartupLibraryInitializationWorkflowOwner](../../../BeMusicSeeker/ViewModels/Startup/StartupLibraryInitializationWorkflowOwner.cs) | 事前判断後から必須出力・必要LR2・全開始Taskとcleanupの実終端まで親L＋P。設定初期化では同ownerの権限を借用。 | 必須実Taskとcleanupの終端→実親解放→受付外の必須UI/host Task→初回/成功公開と解禁を通常・初期設定・Allで統合する。任意登録は解禁後に閉鎖し開始、外部同期は通常の新規Pで通信前に取得。読取り・解析の並列性と正本変更の順序を区別する。[起動](../runtime/startup.md)。 |
| All再初期化、全体復元、履歴schema変更<br>再初期化: [MainWindowViewModel.InitializeAsync](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、復元: [PlaylistRestore](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistRestore.cs)、schema: [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | L＋P。変更開始前から新storeの必須初期化・公開、必要な出力・cleanupまで。 | 短いDB/schema保護は別に維持。終了が必要な操作は自己lease待機を作らない。[起動](../runtime/startup.md)、[保存](../playlist/storage-and-export.md)、[終了](../runtime/shutdown.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「設定・再構築・ScoreOnly・shutdown実終端」。 |
| 全体LR2同期（手動・条件付きFileDiff・自動起動）<br>[Lr2SongDbSyncWorkflowOwner](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) → [Lr2SongDbSyncRequestCoordinator.QueueAsync](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncRequestCoordinator.cs)。差分入口は[FileDiffReloadWorkflowOwner](../../../BeMusicSeeker/ViewModels/Startup/FileDiffReloadWorkflowOwner.cs) | L＋P。手動新規は設定入力捕捉・Running公開・準備前にTryし、競合は未実行skip。起動・設定・差分の必須継続は親の生存権限を借用し、競合skipへ落とさない。 | 開始後は同権限で実workerと保存・cleanupを直接await。内側でscheduler枠や第二受付を待たない。確定済みpartialと未実施wholeを偽Completedにしない。[LR2同期](../integration/lr2-song-db.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「共通規則、live/失効/別owner、通常L/P、交差・両取得」。 |
| 管理データ撤去 | 確認までL＋P、通常Shell終了要求を受理して要求元のL/P・設定操作Taskを解放する。 | Close・mode・更新と同じUI Dispatcherの先着判定。全実処理の終端後に撤去・成否通知を行い、失敗時も終了する。自己受付を待つ循環は作らない。[終了](../runtime/shutdown.md)、[設定](../runtime/settings.md)。 |
| 通常Close、mode再起動、更新適用終了<br>[MainWindow.OnClosing](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) → [ShellShutdownWorkflowOwner](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | 新規受付をcloseし、規定の取消後に全受理済みL/P・音声・DB・cleanupの実終端を待つ。 | 構成が共有するL/Pの新規Try受付をClose開始時に不可逆に閉じる。生存権限の借用と既に登録済みの必須背景継続は保持し、受付を再開しない。CloseをBusyで放置しない。shutdown要求通知だけをUI Closed・実終端として扱わない。[終了](../runtime/shutdown.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「設定・再構築・ScoreOnly・shutdown実終端」。 |

通常起動、設定の明示初回開始、公開`Initialize` / `Reinitialize`は、設定検証・未設定の案内後、閏年日時の候補行とmtimeだけを受付前に読み、No既定のYesNo判断を終えてからL＋Pを非待機取得します。候補読取は既存folderの必要行だけを使い、未作成DBやschemaを作らず、通常の全件ロード・走査を繰り返しません。設定適用の確認は保存前です。既にBusyなら候補確認・保存・修復を始めず、確認中も共有予約を持ちません。取得時の先着競合は副作用前に拒否し、次の明示要求で新しく候補を捕捉します。受理後は承認した保存行・exact-path・mtimeを再検証し、差替え対象へ承認を読み替えません。実修復、通常初期化と開始済みTaskのcleanupは同じL＋P内に保ち、終端の警告・失敗通知は両受付、writer保護とDBトランザクションの解放後に行います。

専用のディレクトリ前処理警告は、未実行の失敗を確定し全開始Task・cleanupを回収した後、L/Pを連続して解放してから提示します。警告自体では設定変更・必要出力・参照公開を行わず、提示後の新しい明示要求を受け付けます。必要な出力やcleanupの途中で解放する例外ではありません。

保存適用済み配置の公開は設定の永続化段階の正常完了時です。部分保存・確定後失敗の事実は[設定仕様](../runtime/settings.md)に従います。一操作で必要な小さい設定入力を捕捉し非同期準備へ明示的に渡します。次の明示操作は新しい入力を捕捉します。

### 音声と独立した読取り

| 操作と入口 | 分類・寿命・理由 | 確認先 |
| --- | --- | --- |
| 新規試聴・Next<br>[PlaybackPanelViewModel](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | L中Busy。通常試聴同士の既存受理・順次実行は維持。 | player開始・保留copy前に判定。変更側は必要な既受理準備・停止・回収を待つ。停止はBusyにしない。推定開始だけで通常再生を止めない。[音声](../runtime/audio.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「一時試聴の受付・回収」と「URLstaging・JSON・音声native実寿命」。 |
| 一時copy試聴・音声変換<br>一時試聴: [PlaybackPanelViewModel](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、変換: [SelectedChartAudioConversionWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs) | L。一時試聴は候補解決後、session・世代・選択・表示の変更前に非待機取得し、コピー・開始境界・保持時間・全回収・元名復元の試行まで保持。拒否では旧状態・再生面・保存表示設定を維持し、面選択要求も予約しない。受理済みの面要求はUIの配送終端を待たず投稿する。変換は入力利用からencoder・session/nativeの実解放まで。 | [音声](../runtime/audio.md)、[一時ファイル](managed-temp-files.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「一時試聴の受付・回収」と「URLstaging・JSON・音声native実寿命」。 |
| 譜面URLダウンロード・外部package検索<br>[PlaylistUrlAcquisition](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs) | 変更不能入力の確保なのでP不要。全取得pathを一度L導入へ渡す。 | Busyは予約なし。取得成功と導入受理/確定を別報告。未受理アプリ所有stagingは取得側が回収をawait、受理済み入力は導入実終端まで保持。持込み/利用者指定保存物は削除しない。[取得](../playlist/downloads.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「URLstaging・JSON・音声native実寿命」。 |
| JSON出力、SQL backup<br>JSON: [PlaylistExport](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs)、backup: [PlaylistBackup](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistBackup.cs) | 整合した読取り。JSONは出力値だけ加工しData_urlを書換えない。 | [保存](../playlist/storage-and-export.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「URLstaging・JSON・音声native実寿命」。 |
| 閲覧・検索・表示並替・選択、設定閲覧/編集/取消、entries表示hydration、URL補完、prewarm、GC<br>表示・選択: [RegularChartListOwner](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、設定編集: [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)、派生表示: [PlaylistWorkspaceViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | 確定済み入力・派生cacheを使う独立処理。 | 独立した表示世代・対象識別・native/読取り寿命は維持。正本更新が必要なら対応領域へ分類し直す。[データと索引](data-and-indexes.md)、[画面](../ui/README.md)。 確認: [実装とテストの対応](#実装とテストの対応)の「独立表示cacheの失効・再接続・現行性」。 |
| 譜面プロパティの明示的な表示<br>[ChartPropertiesViewModel](../../../BeMusicSeeker/ViewModels/ChartOperations/ChartPropertiesViewModel.cs)、[ChartResourceDiagnosticsService](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceDiagnosticsService.cs) | 独立した読取り表示。L/Pの変更受付権を取得しない。 | 単一の捕捉ファイルと、その後の存在確認を表示する。進行中の変更・外部更新との原子的整合を求めず、取得・解析失敗は画面内で扱う。毎回新しい独立窓を開き、複数表示と読取り中のメイン操作を許可する。既表示結果は選択変更やファイル変更に追従しない。メイン終了受付で全窓を閉じ新規受付を止め、個別Close済みも含む実読取りTaskを最終終了前にjoinする。閉鎖後非反映と通常失敗の回収を維持する。[譜面プロパティ](../ui/chart-properties.md)。確認: `ChartPropertiesViewModelTests` / `ChartResourceDiagnosticsTests`、実メニュー・manager・MainWindow終了の接続は同機能仕様の対応表、L/P非取得は静的確認。 |
| 外部リンク・Explorer・外部player/Score Viewer、順位cache、音声一覧取得・能力照会・デバイステスト<br>外部起動: [MainWindowViewModel](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、音声照会・テスト: [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | 外部起動/候補取得をL/P変更に含めない。独立音声sessionの既存所有と終了待ちを維持。設定の一覧列挙はL/Pを取得せず、列挙タスクの実終端まで能力照会と明示テストを開始しない。他設定の編集・保存・取消・通常閉鎖は継続でき、表示失効後も音声管理主体の完了タスクを既存終了経路で待つ。 | [音声](../runtime/audio.md)、[外部連携](../integration/README.md)。通信・外部processとの全競合を確認済みとは扱わない。 確認: [実装とテストの対応](#実装とテストの対応)の「独立表示cacheの失効・再接続・現行性」。 |

保留譜面操作バーの推定・手動導入・クリアは、一覧メニューと同じ既存の保留操作群へ接続します。クリック時に現在の変更活動をUIガードで拒否し、各管理主体のL受付・実終端を維持します。Busy後の予約・自動実行は追加せず、解放後の次の明示クリックを新しい要求として扱います。対象と表示条件は[保留譜面の操作バー](../ui/table-view.md#保留譜面の操作バー)に従います。

### 新機能・変更時の手順

実装前に本番入口と読む/変える共有物を既存操作群へ対応付けます。新しい分類・例外が必要なら利用上必要な並行性を先に判断し、未確定事項は利用者へ確認します。開始・終端・Busy・継続の期待を本仕様で確定し、機能仕様には固有処理を記します。既存の小さい受付検証、各入口への実接続、代表的な横断効果で保証を分担し、操作の全直積や行数に対応するテストは増やしません。同じ変更で本仕様、機能参照、実装・検証対応を更新します。調査履歴や重複した競合台帳は残しません。

設定の補助操作は[設定仕様](../runtime/settings.md#補助操作の対象受付閉鎖)に従い、確定環境を使います。候補破棄の確認承認を実受付と扱わず、手動LR2同期は実L/P、履歴schema変更は既存L/P、Table URL取込みは実Pを取得して最終検査した後に関係候補を戻します。競合時は候補と画面を保ち、予約・自動再実行を行いません。設定Windowの閉鎖と実Task終端を区別し、受理済み操作は既存ownerが終端まで追跡します。

## 実装とテストの対応

| 保証・境界 | 実装 | 検証・確認の分担 |
| --- | --- | --- |
| LR2の短い保存排他と操作ごとの限定公開 | [`Lr2ConfigOwner`](../../../BeMusicSeeker/Models/LR2/Lr2ConfigOwner.cs)、[`表削除`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs) | [`Lr2PartialBmsSaveRetainsDeletedManagedRootThroughExplicitRetry`](../../../BeMusicSeeker.Tests/Settings/SettingDialogLr2OwnerTests.cs) がXMLのみ成功・実ROOT表削除・明示再試行を、[`Lr2PreviewOwnerTests`](../../../BeMusicSeeker.Tests/Processes/Lr2PreviewOwnerTests.cs) が別workerの保存とprocess待機の分離を確認します。 |
| 保留譜面操作バーの実変更活動中の拒否と次の明示操作 | [`MainWindow.GetPendingInstallBarTargets`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) と既存保留terminal | [`MainWindowPackageMaintenanceWpfTests.PendingInstallBar_UsesCurrentBoundSelectionAndPreservesMultipleRowsAfterFocusRequest`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPendingInstallBarWpfTests.cs) は実変更活動中の3クリックのterminal未到達、解放だけでは未実行、次の明示導入だけが到達することを確認する。L/P受付本体は既存ownerテストが担当する。 |
| 共通規則、live/失効/別owner、通常L/P、交差・両取得 | [ChartFileOperationSynchronizer](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartFileOperationSynchronizer.cs)、[ApplicationComposition](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs) | [ChartFileOperationSynchronizerTests](../../../BeMusicSeeker.Tests/ChartOperations/ChartFileOperationSynchronizerTests.cs)、[ApplicationCompositionAdmissionTests](../../../BeMusicSeeker.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs)、[BmsLibraryMutationBoundaryTests](../../../BeMusicSeeker.Tests/Library/BmsLibraryMutationBoundaryTests.cs)。設定/全体LR2実入口の両取得とDBを併せて確認。 |
| 実L入口、通知/cleanup、予約なし | [SelectedChartMutationWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartMutationWorkflowOwner.cs)、[SelectedChartResourceHealthWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartResourceHealthWorkflowOwner.cs)、[ChartInfoParseFailureRemovalWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/ChartInfoParseFailureRemovalWorkflowOwner.cs) | 対応するownerテストの実モデル・小DB、[RegularChartFolderRenameTests](../../../BeMusicSeeker.Tests/ChartList/RegularChartFolderRenameTests.cs)のBusy/次明示要求。 |
| P通信前・有限batch・draft・保存後出力/公開失敗 | [PlaylistWorkspaceViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs)、[PlaylistPropertySaveService](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、[PlaylistBmtOutputOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs) | [PlaylistWorkspaceExternalSourceTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs)、[PlaylistWorkspaceActionWorkflowTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[BmsPlaylistPersistenceLifecycleTests](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)、[BmsPlaylistMigrationAndRegistrationTests](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)。[MainWindowPlaylistWorkspaceWpfTests.ResyncedDetailEdit_RejectsOldRowThroughOwnedNotificationAndSavesCurrentRow](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)は実再同期後の旧行拒否、実所有windowへの通知、最新DB/項目保全と最新行の明示保存を一つの本番接続で確認する。実DB/FS・出力barrierを上位判定、BMT形式・並列資源再利用は[BmtTableExportServiceTests](../../../BeMusicSeeker.Tests/Playlist/BmtTableExportServiceTests.cs)が分担。 |
| 編集準備の短期Pと六操作の全対象現行性 | `PlaylistWorkspaceViewModel.PropertyEditing`、`PlaylistSummaryBulkEdit`、`PlaylistSummaryBuild` | [プロパティ仕様の対応表](../ui/playlist-properties.md#実装とテストの対応)の三準備入口・実hydration中P・全件/一部/無変更対象失効・同ID置換・別storeと、実外部再同期後の旧draft拒否から開き直し保存。保存境界の全件失効・入力空・非厳格互換は`BmsPlaylistPersistenceLifecycleTests.CommitHeaders_CurrentTargetRequirementRejectsAllAndPartialRetirementButAllowsEmpty`が分担。 |
| 設定・再構築・ScoreOnly・shutdown実終端 | [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)、[ShellShutdownWorkflowOwner](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | [SettingDialogEditCompletionTests](../../../BeMusicSeeker.Tests/Settings/SettingDialogEditCompletionTests.cs)、[MainWindowViewHostTests](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewHostTests.cs)、[MainWindowPlaylistWorkspaceWpfTests](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)。同live権限、実store/score/保存、Task・出力・終了順、環境組のBusy候補保持と次の明示確定を確認。schema・撤去は[Lr2PlayHistorySchemaUiTests](../../../BeMusicSeeker.Tests/PlayHistory/Lr2PlayHistorySchemaUiTests.cs)と[ApplicationDataUninstallWorkflowOwnerTests](../../../BeMusicSeeker.Tests/Settings/ApplicationDataUninstallWorkflowOwnerTests.cs)、BMS一覧の局所確定は設定の実差分継続で両受付と実終端を分担。`SettingsWindowPresentationTests.MainWindow_NormalSettingsCloseEndsModalBeforeActualFileDiffTerminates` は通常閉鎖・新Windowへの同一Task接続とShell join、`SettingsDialogBehaviorTests.SettingsOperation_RejectsCompetingRequestsUntilCleanupAndFailureNotificationTerminate` は実入口の拒否と通知終端までの受付を確認。 |
| 一時試聴の受付・回収 | [PlaybackPanelViewModel](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [PlaybackPanelViewModelTests](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) の `TemporaryCopy_AdmissionRacePreservesExistingSessionUntilExplicitInput` は冒頭確認後に実Lを先取りして五入口の拒否無副作用と明示操作での終端を確認する。既存Ready/外部開始/残留通知ケースが回収・元名復元までの保持と通知前解放を分担する。 |
| URLstaging・JSON・音声native実寿命 | [PlaylistUrlAcquisition](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs)、[SelectedChartAudioConversionWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs) | [PlaylistUrlAcquisitionOwnershipTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlAcquisitionOwnershipTests.cs)、[PlaylistWorkspaceActionWorkflowTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[SelectedChartAudioConversionWorkflowOwnerTests](../../../BeMusicSeeker.Tests/ChartOperations/SelectedChartAudioConversionWorkflowOwnerTests.cs)。JSON実serializer時の正本不変、実導入の入力終端、native解放を確認。 |
| 導入→直接推定・後段導入・取消/例外・全join | [PackageInstallWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs)、[BMSLibrary.PackageInstall](../../../BeMusicSeeker/Models/Library/BMSLibrary.PackageInstall.cs) | [PackageInstallWorkflowOwnerAutomaticContinuationTests](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerAutomaticContinuationTests.cs) は後段候補別のP競合と独立候補の一括成功、書込み前の実停止TaskとLR2有効時の追加P保持、パッケージ境界取消後の必要同期とL/P解放を確認する。推定停止と全joinは [PackageInstallWorkflowOwnerEstimationTests](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs)、先行成功overlay・SEARCHING・実確定事実は [BmsLibraryPackageInstallServiceTests](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) 等の機能仕様の下位保証も維持。 |

各入口の分類・権限転送は本番接続の静的確認も分担します。テストで同じ内部処理順を再生成して判定せず、実受理、副作用、保存事実と全実終端を観測します。独立表示cacheの失効・再接続・現行性は各機能の既存テストで維持します。

## 関連資料

[一般の並行設計](workflow-concurrency.md)、[アーキテクチャ](architecture.md)、[テスト設計](../development/test-authoring.md)、[検証](../development/testing.md)。
