# 操作受付と排他の単純化

## 目的と停止位置

ライブラリ・プレイリスト・設定・起動後処理の本番入口を棚卸しし、必要な並行性を少数の受付規則で表します。現行コードで許される操作の組合せは観測事実であり、そのまま維持要件にはしません。利用者は、更新中の同じプレイリストの編集や、外部同期と設定画面のbeatoraja取込みの同時実行を必要としていません。

方針を提示して実装を停止しています。以下の受付統一、テストの置換、関連仕様の改訂は未実装です。現行の作業差分を保持し、再開時は個別の競合修正に戻る前に、この計画の操作一覧と保証の対応を確定します。旧引継ぎの「通信中はPを取得しない」「現在の並行性を維持する」は、この見直しの判断根拠にしません。

調査は画面・コマンド・起動登録から主要な処理主体、受付、共有物、後処理までの静的確認です。全SQL文・全失敗枝・実機上の全時系列を網羅した証明ではありません。実装とテストは変更・実行していません。

## 採用する整理

変更受付は次の二領域にします。画面や対象一件ごとの受付は増やしません。

| 領域 | 一操作にまとめる内容 |
| --- | --- |
| ライブラリ受付（L） | 譜面・フォルダ・保留・導入先・ライブラリ正本の変更、推定、入力を変更する保守。 |
| プレイリスト受付（P） | 表・項目・全表集合・保存する順序とフラグの変更、外部取得を伴う登録・更新、必要なLR2/BMT管理出力。 |
| LとPの両方 | 設定保存・適用、全再構築、LR2全体同期、全体復元・撤去など、両側の前提や所有するデータを変える操作。物理変更が管理出力領域へ交差する場合も副作用前に両方を揃える。 |

LとPは別の受付を使うという意味であり、三つ目の全体受付や汎用の資源ロック管理を作るという意味ではありません。

| 先行する操作 | 後続L | 後続P | 後続L＋P | 閲覧・検索・表示上の並べ替え・設定draft編集 |
| --- | --- | --- | --- | --- |
| Lのみ | Busy | 許可 | Busy | 許可 |
| Pのみ | 許可 | Busy | Busy | 許可 |
| L＋P | Busy | Busy | Busy | 許可 |

通常のLとPが触る物理領域は非交差であることを前提とします。既存の管理出力領域への交差判定を使い、保存・適用済み配置から判定します。未保存の設定編集で実行中の配置を変えません。スタンドアローンとLR2連携で日常操作の受付規則を揃え、LR2の同じDBに書くという理由だけで全面直列化しません。接続・トランザクションの短い保護は別に必要です。

### 一操作の開始と終端

- 利用者が実行を確定した入口で、対象の確定・通信・副作用を伴う準備より前に必要な受付を非待機で取得します。入力欄の形式検査、変更を伴わないファイル選択は前に行えます。両受付が必要な操作で片方を取得できなければ、取得済み分を解放し未開始のBusyとします。
- 同じ領域の後続変更は別対象でもBusyです。プレイリストAの更新中はBの変更も受け付けません。通信が遅くてもUIスレッドは占有せず、ライブラリ操作や閲覧は続けられます。一要求内の複数表の取得・解析等は並列にできます。
- 受理後は、入力取得、変更対象の計算、成功対象の一括反映、必要な出力・参照公開、取消・失敗時の後片付けまで一操作として直接待ちます。保存と後処理の間で解放して取り直しません。内部継続には同じ生存権限を渡します。
- 競合する未受理要求の追加予約・自動再実行は作りません。URI取込み、セル保存、手動改名等の待ち行列をこの原則で整理します。起動準備が未完なら変更を未受理として制限し、無制限に入力を積みません。受理済み必須処理の完了待ちと、未受理操作の待ちを区別します。
- 結果は一回の操作へ集約します。DB保存済み・物理成功済みの事実を後続出力失敗で取り消さず、部分失敗・未処理を返します。Busy、取消、例外を成功・空結果へ置換しません。
- LR2全体同期は開始時Busyなら見送ります。予約・後からの自動再実行をしません。通常操作解禁前へ全体同期を移す手続き化は、[起動の順序統合](lr2-startup-procedural-orchestration-plan.md)で扱います。

### 対象と下書き

新規表を編集のためだけに正本集合へ仮追加せず、保存まではdraftとします。これにより取消のための正本削除をなくします。既存表のdraftも正本と分け、保存受付後に元のstore・対象が有効か一度確認します。消滅した対象を同名・同URLの別対象へ読み替えません。

受付を取得する前の画面選択やdraftには現行性確認が必要です。一方、取得後のアプリ内削除・差替えを受付で禁止すれば、その競合のためだけの世代判定、再捕捉、再試行は削除できます。外部プロセスによるファイル変更やDB保存失敗まで受付で防げるとは扱いません。

### 管理出力と独立した処理

- BMTは表示キャッシュではなく外部ファイル・manifest・beatoraja設定を変更します。変更表を一括し、必要なBMT生成・削除・URL反映までP操作へ含める方針です。変更のたびの全表出力は行わず、未変更省略と生成の並列度は維持します。これで不要になる対象IDキュー、要求置換、古いmetadataの再投影、競合調整用の世代・ロックを削除します。台帳の互換性や安全なファイル置換は別の保証です。
- BMTのハッシュ補完等の読取り依存だけでLも取得しません。確定済みの読取りモデルを使い、全ライブラリと同一時点である保証を新設しません。
- 譜面URLダウンロード・外部パッケージ検索はプレイリスト正本を変更しません。選択情報を変更不能な入力として確保し、Pから外します。取得した入力はまとめてLの導入受付へ渡し、Busyなら予約しません。ダウンロード成功と導入の受理・成功を分けて通知し、一時入力の回収まで追跡します。
- JSON出力のための一時的な正本プロパティ書換えは、出力用データの加工へ置き換えます。SQLバックアップとともに、整合した入力の読取り・出力として扱います。
- 表示用の一覧生成、検索、URL補完、外部表カタログ取得、先行読込みには、必要な読取りスナップショット・世代・取消・寿命管理を残します。正本変更の受付と同じ理由で一括撤去しません。
- 通常試聴・音声テストのnative資源管理は残します。新しい試聴はL中にBusyとし、物理変更は必要な再生停止と実際の回収を待ちます。一時コピー試聴はLの変更操作です。音声変換は入力ファイルを使う期間をLで保護する方向とし、独自の実行中表示で代用しません。

## 本番入口からの操作一覧

以下の「現行」は未コミット変更を含む調査時点の事実です。操作内のhelperを別操作として数えず、同じ入口からの複数対象処理は一群にしています。各行の受付開始と終端、必要な反映の最終対応は再開時にここへ補います。整理完了時には、操作分類と競合制約を後述の競合ポリシーへ、機能固有の処理結果を各機能仕様へ移します。

### ライブラリ・保留・再生

表の「集合等」は所持譜面・パス・ハッシュ・リソース索引と関連するDB行です。物理変更はLR2管理出力へ交差する場合だけPも必要になります。

| 操作群 | 入口の責任主体・主な共有物 | 現行と整理先 |
| --- | --- | --- |
| ZIP・フォルダ・譜面の取り込み | [PackageInstallWorkflowOwner](../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs)。入力、一時展開、保留、集合等、導入先。 | 既に入力確保から直接推定・回収までL。一要求内の複数入力を維持する。 |
| 保留の導入先・統合先推定 | [PendingPackageWorkflowOwner](../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs)。導入元、所持索引、保留候補。 | L。復元した保留の起動時自動推定を復活させない。 |
| 所持譜面の再導入先推定 | 同上。所持譜面と一時的な候補。 | L。 |
| 導入先の手動設定・解除 | [RegularChartListOwner](../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、保留workflow。保留・候補・代表情報。 | L。軽い変更であることだけで推定中の編集を許可しない。 |
| 保留の導入・強制新規導入 | 保留workflow。導入元・先、保留、集合等。 | 確認・停止・変更・回収をLでまとめる。 |
| 再導入・誤配置修正 | 保留workflow。配置、重複削除、集合等。 | L。 |
| 保留・導入履歴の記録削除 | [PackageCatalogWorkflowOwner](../../BeMusicSeeker/ViewModels/Install/PackageCatalogWorkflowOwner.cs)。install行、集合、管理導入元の回収。 | 確認から必要な回収までL。 |
| 保留の一括保守 | 保留workflow。既所持導入元削除、ゼロノート無効化、資源上書き。 | 有限対象をまとめたL。取消・部分成功を保持する。 |
| 譜面・空になるフォルダの削除 | [SelectedChartMutationWorkflowOwner](../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartMutationWorkflowOwner.cs)。物理対象、集合等。 | L。 |
| 選択譜面の別ルートへの移動 | 同上。物理フォルダ、配置、集合等。 | L。 |
| 手動・自動フォルダ改名 | RegularChartListOwner、[FolderAutoRenameWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/FolderAutoRenameWorkflowOwner.cs)。配置、集合等。 | 手動は前回Taskの後ろへ予約してからL。入口で非待機取得する一操作へ揃える。 |
| 無効拡張子への変更 | 選択変更workflow。ファイル名、所持・保留所属、集合等。 | L。 |
| 文字コード指定・修正済みマーク | 選択変更workflow、[BMSLibrary](../../BeMusicSeeker/Models/Library/BMSLibrary.cs)。maintenance・song値。 | 外側Lから内部モデルへの権限接続を再確認する。静的調査で内部再取得への転送抜けがある。 |
| 選択・全件の資源健全性再検査 | [MaintenanceRescanWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceRescanWorkflowOwner.cs)、BMSLibrary。資源、maintenance、警告索引。 | 専用実行状態とmodel内Lが分離。開始から終端まで一つのLに揃える。 |
| 資源警告の無視・解除 | BMSLibraryの保守入口。maintenanceと現在値。 | 現行model内L。Busy結果を入口まで明示する。 |
| ゼロノート警告の再確認 | [ZeroNoteMaintenanceWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/ZeroNoteMaintenanceWorkflowOwner.cs)。chart_info、実譜面、警告。 | worker内L。操作の受付へ揃える。 |
| 解析失敗記録の削除 | [ChartInfoParseFailureRemovalWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/ChartInfoParseFailureRemovalWorkflowOwner.cs)。解析失敗DB、警告表示。 | 現行Lなし・短いDB保護。Lの変更として揃える。 |
| 重複フォルダ統合・余剰譜面削除 | [DuplicateMaintenanceWorkflowOwner](../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs)。譜面・資源、集合等、統合後保守。 | L。必須保守を同一操作の終端へ含める。 |
| 試聴・次曲・停止・一時コピー試聴 | [PlaybackPanelViewModel](../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)。再生列、音声資源、一時ファイル、外部player設定。 | 新規試聴のL判定、物理変更前の停止・回収、一時コピーのLを維持する。停止要求をBusyで妨げない。 |
| 音声変換・録音ファイル出力 | [SelectedChartAudioConversionWorkflowOwner](../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs)。実譜面・音声資源、出力、native資源。 | 独自isRunningだけでLなし。入力利用から解放までLへまとめ、native資源の別責任は残す。 |

### プレイリスト・外部出力

表・項目だけでなく、全表集合、重複名・出力先、BMT順序を共有します。このため表一件ごとの受付に細分化しません。スタンドアローンでも正本保存・参照反映の問題は存在し、LR2モードでは管理出力が加わります。

| 操作群 | 入口の責任主体・主な共有物 | 現行と整理先 |
| --- | --- | --- |
| 新規空表作成・編集・取消 | [PropertyEditing](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PropertyEditing.cs)、[BMSPlaylist](../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)。全表集合。 | 仮追加はPなし。正本外draftとし、保存時P。取消の正本削除をなくす。 |
| プロパティ保存・外部同期設定変更 | [PlaylistPropertyDialogViewModel](../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs)、[PlaylistPropertySaveService](../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)。表、DB、出力、参照。 | 保存から通信・後処理まで既にP。全経路を同じ寿命へ揃える。 |
| サマリーの名前等のセル編集 | PropertyEditing。表、DB、出力。 | 独自待機の後、保存と後処理が別P。一回のPへ統合する。 |
| サマリーフラグ・一括編集 | [PlaylistSummaryBulkEdit](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs)、[PlaylistSummaryBuild](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBuild.cs)。複数表と出力。 | 多くはP、BMT出力フラグはP外。すべて変更前からP。 |
| BMT保存順の変更・ドラッグ | [PlaylistSummaryBmtSortCoordinator](../../BeMusicSeeker/ViewModels/Playlist/PlaylistSummaryBmtSortCoordinator.cs)。全表の順序、header DB、URL順。 | P外の独自gate。Pへ統合する。表示上のsortとは区別する。 |
| フォルダ・譜面項目・詳細セルの変更 | [Mutations](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs)、[DetailEditing](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailEditing.cs)。項目、DB、出力、参照。 | 確定時からP。必要後処理まで同じP。 |
| 単表・複数表の削除 | [PlaylistRemovalWorkflowOwner](../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs)。全表集合、DB、管理出力。 | P内の削除と別予約のBMT削除を一操作へまとめる。 |
| 選択表の外部再同期 | [Reload](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Reload.cs)。通信、表の差替え、DB、出力、参照。 | 既に通信前からP。別表の変更もBusyとする共通形。 |
| URI・JSON・collection・内蔵表の登録 | [ExternalPlaylistImport](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistImport.cs)。入力、全表集合、DB、出力。 | 現行はFIFO受理・準備待ち・取得後P。実行可能時に一要求分をPで受理し、途中追加を予約しない。 |
| beatoraja Table URL取込み・BMT cache復元 | [BeatorajaTableUrlImport](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.BeatorajaTableUrlImport.cs)。設定/cache入力、既存URL、登録、順序。 | 設定画面の入口から、通信前にPを取得する。外部取得失敗時の既存cache復元という機能は維持する。 |
| プレイリストルート再読込み | [MainWindowViewModel](../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、BMSPlaylist。見出し・項目・参照と外部同期。 | 全体セマフォ、headerのP、別予約の外部同期に分割。必要な更新を一回のPへ整理する。 |
| SQLバックアップ・復元 | [PlaylistBackup](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistBackup.cs)、[PlaylistRestore](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistRestore.cs)。DB、復元時の全表集合・出力。 | backupは整合した読取り。restoreは非日常のL＋P操作へまとめる。 |
| JSON出力 | [PlaylistExport](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs)。表の読取り、利用者指定ファイル。 | 現行はData_urlの一時書換えがある。出力用値だけを加工する読取りへ整理する。 |
| BMT生成・削除・URL順反映 | [PlaylistBmtOutputOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs)。BMT、manifest、beatoraja設定。 | P外のID集約・世代・再投影。変更元のPの必要後処理へ統合する。 |
| LR2管理出力・起動修復 | BMSPlaylist、[Lr2SynchronizationOwner](../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs)。管理ファイル、folder行、検索root。 | 日常はP、全体同期はL＋P。独立修復は新規Pとして受付する。 |
| URL補完・外部表カタログ取得 | [UrlCompletion](../../BeMusicSeeker/Models/Playlist/BMSPlaylist.UrlCompletion.cs)、[ExternalTableList](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalTableList.cs)。runtime値・候補cache。 | 正本DB保存のない派生処理。読取り側の世代・寿命を維持する。 |
| 譜面URL取得・外部パッケージ検索 | [PlaylistUrlAcquisition](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs)。URL/hash、staging、導入要求。 | 現行P。正本変更をしない取得としてPから外し、取得後のL導入と結果を区別する。 |
| 表の難易度を実譜面へ上書き | [PlaylistTableLevelOverwriteWorkflowOwner](../../BeMusicSeeker/ViewModels/Playlist/PlaylistTableLevelOverwriteWorkflowOwner.cs)。表を読み、譜面・library DBを更新。 | 画面の所属によらずLへ接続する。 |

### 横断操作と自動処理

| 操作群・入口 | 共有物と現行の境界 | 整理方針 |
| --- | --- | --- |
| 設定Save/Apply | [SettingsDialogViewModel](../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)。保存、配置、All/ScoreOnly/FileDiff等の後処理。現行はL＋条件付きP。 | 保存項目による受付分岐をやめL＋P。閲覧・編集・取消を保ち、Busy時draft保持。 |
| ライブラリroot追加・解除 | MainWindowからSettingsへ。設定、検索root、差分走査・反映。 | L＋Pの設定変更としてまとめる。 |
| 全再初期化・差分再読込み・スコア再読込み | [MainWindowViewModel](../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、[Startup](../../BeMusicSeeker/ViewModels/Startup)。ライブラリ/store差替え、入力・score・参照。 | AllはL＋P。単独FileDiffはL、設定からの継続は親の権限。ScoreOnlyも設定の継続として同じ権限を渡す。必要な全体LR2は両受付で判定する。 |
| 初回起動と必須の遅延入力更新 | [BMSLibrary](../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[CatalogChartInfoOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogChartInfoOwner.cs)、[InstallableMaintenance](../../BeMusicSeeker/Models/Library/BMSLibrary.InstallableMaintenance.cs)。譜面情報、保守、score、保留入力。 | 必須の正本更新はLの操作・継続として分類する。全体の直接待機化は起動計画へ。表示のprewarmや任意通信を一括した解禁条件にしない。 |
| LR2全体同期 | [Lr2SongDbSyncWorkflowOwner](../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs)。song/folder、出力、保存状態。 | 開始前L＋P。Busyなら見送り、実終端まで保持。 |
| 起動・再読込み後の外部表同期 | [ExternalPlaylistSync](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs)。表の取得・差替え・保存。 | Pの一括操作として扱う。必須継続と任意の起動要求を明示し、scheduler空きや世代から親の完了を推測しない。 |
| プレイ履歴schemaの導入・修復・撤去 | 設定の専用ボタン、SettingsDialogViewModel。score DB、schema、履歴cache。現行は画面gate・実行中判定・schema固有保護。 | 非日常のL＋P操作へ分類する。DB/schema保護と必要な読取Taskの終端は別に保持する。 |
| アプリ管理データの撤去 | [ApplicationDataUninstallWorkflowOwner](../../BeMusicSeeker/ViewModels/Settings/ApplicationDataUninstallWorkflowOwner.cs)。DB管理領域と終了。現行は開始時の状態値判定。 | L＋Pで新規変更を止め、受理済み作業と終了の順序を明示する。 |
| モード変更・再起動・更新適用・終了 | 設定、[StartupUpdateWorkflowOwner](../../BeMusicSeeker/ViewModels/Startup/StartupUpdateWorkflowOwner.cs)、[ShellShutdownWorkflowOwner](../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs)。設定、全store、実行Task、音声・DB資源。 | 新規受付閉鎖→取消→開始済みTask/回収完了。終了をBusyとして放置したり、親の受付を自分で待ったりしない。 |
| 音声能力照会・デバイステスト | [AudioDeviceTestWorkflowOwner](../../BeMusicSeeker/ViewModels/Settings/AudioDeviceTestWorkflowOwner.cs)。候補表示・native出力session。 | 能力照会と実音声テストを区別し、既存の音声資源の寿命を保持する。設定画面の操作抑止だけで背景Task停止を仮定しない。 |
| 一覧・履歴・参照の読取り、entries hydration、prewarm、URL補完、GC | 各表示owner、[PlaylistEntriesHydrationOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistEntriesHydrationOwner.cs)。表示用集合・cache・古い表示の回収。 | 確定済み入力を読む処理は独立。正本変更と必要参照公開だけを変更操作へ含め、描画やGCの完了まで領域を占有しない。 |
| 外部リンク・Explorer・外部アプリ・Score Viewer・ランキングcache取得 | [ScoreViewerRegistration](../../BeMusicSeeker/ViewModels/ChartOperations/ScoreViewerRegistration.cs)、[RankingCacheDownloadWorkflowOwner](../../BeMusicSeeker/ViewModels/Startup/RankingCacheDownloadWorkflowOwner.cs) 等。選択情報、外部通信、cache。 | 外部起動そのものは変更受付の対象外。入力確保と後続の実際の正本更新を区別する。通信内部・外部プロセスとの競合の全確認は今回未実施。 |

## 整理完了時の競合ポリシー

整理の完了成果物として、`devdocs/spec/core/operation-concurrency-policy.md` に「操作の競合ポリシー」を作成します。本計画の操作群を、現行の操作と競合制約を管理する仕様上の台帳へ移し、新機能を追加するときの判断の起点にします。調査時の実装や旧挙動を保存する履歴台帳にはせず、合意した方針・整理後の実装・確認できた保証を区別して記載します。現時点では作成せず、計画上の未実装方針を実装済み仕様として公開しません。

### 文書ごとの正本

| 資料 | 所有する内容 | 他資料との接続 |
| --- | --- | --- |
| 競合ポリシー | L・P・両受付・独立処理の分類、並行可否の表、操作別の受付開始と終端、Busy・見送り・受理済み継続の扱い、条件付き制約と例外の理由。 | 各操作から機能仕様、実入口、競合保証の確認箇所へリンクする。 |
| [ワークフローと並行性](../spec/core/workflow-concurrency.md) | 到達可能性と利用者要件による判断手順、論理的な受付と短いロックの区別、非同期待機・世代・公開に関する共通の設計原則。 | 現在の具体的な許可・拒否・例外表を競合ポリシーへ移し、参照に置き換える。ポリシーの分類を重ねて定義しない。 |
| 各機能仕様 | 入出力、保存形式、処理手順、部分成功、失敗時のデータ保全、表示内容、性能など、その機能の契約。 | 競合制約はポリシーの対応する操作・条件の節を参照する。別のBusy表や独自の例外一覧を持たない。受付終端に関係する詳細手順にはポリシー側からリンクする。 |

同じ競合条件を本文へ複写して参照リンクだけ加える形にはしません。例えば機能仕様は「外部同期中にこの表の保存を拒否する」を再定義せず、保存操作の受付条件としてポリシーの該当節へ案内します。必要な出力の具体的手順は機能仕様に置き、その手順のどこまで受付を保持するかをポリシーが定めます。

### 操作台帳に残す項目

| 項目 | 記録する内容 |
| --- | --- |
| 操作群と入口 | 利用者に対応が分かる操作名、画面・コマンド・セル・設定・自動起動等の入口。名前が似ていても入口や制約が異なる場合は区別する。 |
| 共有する対象と分類 | 正本・ファイル・出力・設定等の読取り／変更対象、L・P・両方・独立処理の分類とその理由。独立処理も明示し、記載漏れを並行許可と解釈しない。 |
| 受付の寿命と継続 | 受付開始、対象確定、必要な反映・出力・後片付けを含む解放条件、親操作から権限を受け取る継続と独立した後続要求の区別。 |
| 条件・結果・例外 | モード差、管理出力への交差、起動・終了等で分類が変わる条件、Busy・見送り・取消時の受付上の結果。例外を設ける場合は必要な利用者要件と理由。 |
| 根拠と確認先 | 機能仕様の対応節、受付を担う実装、競合保証を確認する既存テスト・静的確認の分担への参照。 |

共通の並行可否は領域間の小さい表で一度だけ定義し、操作行には分類と条件差を記録します。全操作同士の組合せ表や、同じ意味の入口ごとの重複行は増やしません。現在の50群という数は固定要件にせず、意味が同じものはまとめ、異なる制約が必要なものを分けます。参照は機能・操作名を表す見出しを使い、一時的な作業番号や行番号を恒久的な識別子にしません。

### 新機能追加・変更時の更新手順

1. 実装前に、本番入口と読む／変える共有物から既存の操作群・受付分類へ対応付ける。同じ契約なら既存行に入口を追加し、新しい操作群なら行を追加する。既存分類に収まらない場合は利用上必要な並行性を先に判断し、実装が可能という理由だけで例外を作らない。
2. 受付開始・終端・後続Busy等の期待結果をポリシーで確定し、対応する機能仕様から参照する。利用者の意図に依存する未確定事項は質問し、必要のない並行性の維持を前提にしない。
3. 変更する競合保証と本番入口の接続について、既存の確認で足りる部分、更新・追加・削除するテストを決める。機能固有のテスト条件は各機能仕様へ置き、ポリシーには競合保証と確認先だけを記録する。台帳の行数に合わせてテストを新設しない。
4. 機能の実装・変更・廃止と同じ変更で、ポリシー、参照元の機能仕様、実装・検証への対応を更新する。レビューでは入口の未分類、独立処理の根拠、例外の必要性、受付終端の漏れ、制約の重複記述を確認する。

仕様の目次と開発指針の「先に読む資料」から競合ポリシーへ辿れるようにします。既存の「専用台帳を作らない」という指針は、調査履歴や重複台帳を増やさない趣旨へ整理し、この現行仕様の台帳を更新する手順と矛盾させません。新しい承認工程や、実行時の操作登録機構を追加するものではありません。

## 現状の問題と検証の組み直し

全量テストの成功だけでは、操作入口が同じ受付規則へ接続されていることを保証できていません。特に次の二種類を分けて扱います。

- [PlaylistWorkspaceExternalSourceTests](../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs) のURI取込み・beatoraja取込みは、実通信中にPを取得できることを明示的に期待しています。これは今回捨てる並行性なので、その期待を維持するための削除・再登録競合テストは増やしません。通信前受付と後続Busyの検証へ置き換えます。
- [SelectedChartMutationWorkflowOwnerTests](../../BeMusicSeeker.Tests/ChartOperations/SelectedChartMutationWorkflowOwnerTests.cs) の文字コード指定には、代替storeへの転送と表示順を確認する検証があります。これだけでは実モデルの受付借用を保証しません。既存の局所保証を残すか統合するかと、実接続の不足を補うかは別に判断します。

件数や同じコードの通過だけで重複・過剰を断定しません。次の順で既存テストを分類し、必要性を決めてから編集します。

1. 操作一覧の各行をL・P・両方・独立処理へ対応付け、実行確定、対象捕捉、最初の副作用、必要反映、解放の境界を明示する。画面、セル、設定ボタン、自動処理を入口として含める。
2. 共通受付の条件は小さい境界で検証する。同側Busy、LとPの並行、両方取得失敗時の解放、同一操作の借用、取消・失敗・後片付け後の解放を扱う。
3. 各入口の実接続を確認する。代替storeへの呼出し確認だけで、実modelへ権限が届く保証を代用しない。行ごとに既存の実接続テスト、更新対象、追加が必要な箇所、静的確認の分担を記入する。新しい汎用テスト基盤は前提にしない。
4. 機能横断の代表確認を絞る。通信中Pと後続変更、Lと非交差Pの実並行、管理領域への交差、設定保存・再構築、出力失敗、終了を実際の接続で確認する。全操作の直積を大量のE2Eへ展開しない。
5. 旧並行性・旧キュー・古い結果の再適用だけを保証するテストは置換または削除する。DB/ファイルの部分成功、取消、出力失敗、draft保持等の残す保証には移管先を示す。禁止した途中操作の全競合パターンを引き続き検証しない。

これにより「受付規則の組合せ」「各本番入口への接続」「代表的な実際の効果」を分担します。整理完了後は競合ポリシーの操作分類・条件を競合保証の根拠とし、現在のコードや既存期待値から制約を逆算しません。全ケースが一つのテストにあることや、全量の件数増加を網羅性の指標にはしません。独立した判定基準と具体的なテスト配置の再設計・引継ぎ点検は、実装再開前に行います。

## 再開時の作業順と範囲

1. この操作一覧と競合表を基準に、必要な終端と独立処理の境界を引継ぎへ確定する。既存差分・テストを維持、更新、削除へ分類する。新しい入力拒否や機能の意味変更が別途必要なら、その点だけ利用者へ確認する。
2. Pの入口を一操作へ揃える。新規draft、セル・フラグ・順序、URI/beatoraja取込み、同期・削除・再読込みを対象とし、保存と後処理を直接接続する。beatorajaの対象読替え・Busy通知漏れもこの境界の中で解消する。
3. BMTの必要出力を同じPへまとめ、不要な要求置換・再投影を削除する。設定・全体操作の両受付と、L側の入口分離・権限転送抜けを整理する。稀な操作の細かな併行可否は増やさない。
4. 整理後の操作群・競合制約を競合ポリシーへ移管し、各機能仕様とワークフロー仕様の重複する制約を参照へ置き換える。仕様目次と開発指針に入口・更新手順を接続する。[起動計画](lr2-startup-procedural-orchestration-plan.md)、[安全性計画](safety-improvements-plan.md)も改訂し、現在の操作許可を無条件に維持する記述、独立BMT終端・要求置換を前提にした残課題を見直す。
5. 変更した版で対象を絞った検証、統合検証、独立レビューを行う。受付・寿命・永続化を変える重大変更として扱う。新しい不備を発見したら操作一覧と保証の欠落を先に確認し、個別のテスト追加だけで閉じない。

全起動段階の型付き結果への移行、無関係な安全性項目、すべての表示cacheの世代撤去は、この整理へ一括して含めません。アプリの受付は外部LR2・beatoraja・利用者のファイル操作を排他しません。外部変更・DB失敗に対する既存の処理は区別します。

完了条件は、列挙した全入口の受付と終端が説明できること、日常L/Pの並行性が残ること、不要な同側の並行性・予約・再照合が削除されること、残すデータ互換性・部分成功・失敗通知が検証されることです。文書についても、競合ポリシーが操作群・競合制約の単一の正本として整備され、機能仕様から該当条件へ参照でき、新機能追加時の分類・更新・検証の手順が明記されることを完了条件に含めます。移管時は調査時の旧挙動欄と一時的な作業情報を残さず、制約の重複・参照漏れ・未分類の入口を確認したうえで、この計画を削除します。
