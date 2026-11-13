# 操作の競合ポリシー

## 目的と適用範囲

アプリ内の操作を、共有する変更対象から分類し、受付の開始、拒否、受理済みの継続と実際の終端を定めます。公開コマンド、セル編集、設定ボタン、自動処理にも適用します。

各機能の仕様は、受付の分類・競合の結果・継続の寿命を本書へのリンクで示し、同じ規則を重ねて定義しません。次は対象外で、各機能の仕様が定めます。

- 外部のLR2・beatoraja・利用者によるファイルの変更。この受付では排他しない。
- 保存の互換性、外部変更への防御、部分成功、計算の並列度。

競合対策を新しく決める手順は[ワークフローと並行性](workflow-concurrency.md#競合対策を決める手順)に従います。

## 用語

[共通用語集](../glossary.md)の操作の受付、競合拒否、排他権、操作権限、終端を使います。

| 用語 | 意味 |
| --- | --- |
| L | ライブラリ変更の受付。ライブラリ・保留・推定・保守の変更を対象とする。 |
| P | プレイリスト変更の受付。プレイリストの正本と、必要な管理出力の変更を対象とする。 |
| 論理的な受付 | 操作の実行責任を表す受付。短いモデルロック、DBトランザクション、ネイティブ資源の所有とは異なる。 |
| 生存中の権限 | 受理した操作が終端するまで有効な操作権限。解放すると、その権限から借用したものも無効になる。 |
| 受理済みの継続 | 受理した操作の一部として、同じ権限で続ける内部処理。新しい要求とは区別する。 |
| 見送り | 開始の時点で競合したため実行しない結果。競合拒否と同じく未受理であり、予約しない。 |

## 仕様

### 分類と並行可否

| 新しい要求 / 実行中 | L | P | L＋P |
| --- | --- | --- | --- |
| L | 競合拒否 | 非交差なら受理 | 競合拒否 |
| P | 非交差なら受理 | 競合拒否 | 競合拒否 |
| L＋P | 競合拒否 | 競合拒否 | 競合拒否 |
| 確定済みの入力の読取り | 継続可能 | 継続可能 | 継続可能 |

同じ領域の要求は、別の譜面・表が対象でも、待たずに競合拒否とします。通常の非交差のLとPは、スタンドアロンとLR2連携の両方で並行できます。

物理的なライブラリ変更が、LR2の管理出力の実際の再帰的な入力元・出力先・DB行の範囲と交差する場合は、L＋Pです。交差の判定には、保存・適用済みの配置と、現在の正本を使います。判定のために、設定ドラフト全体の別の保存状態は作りません。変更の確定後に行うLR2の局所同期は、実際に行を書き換える範囲だけで交差を判定し、Pを新たに取得せず、物理変更の前に取得したPを借用します（[局所同期の書込み範囲と管理出力の受付](../integration/lr2-song-db.md#局所同期の書込み範囲と管理出力の受付)）。

両方の受付が必要な要求は、副作用の前に両方を待たずに取得します。一部だけ取得して競合した場合は、取得した側を解放します。LとPのほかに第三の全体受付はありません。

#### 競合拒否と見送り

| 要求 | 開始時に競合した場合 |
| --- | --- |
| 通常の要求 | 未開始のまま競合拒否とする。 |
| 全体のLR2同期の新規の開始 | 未実行のまま見送る。 |
| 受理済みの継続・必須の更新 | 競合拒否・見送りにしない。生存中の権限の借用、または先行する操作の終端の待機で続ける（[受理済みの継続](#受理済みの継続)）。 |
| 差分の継続から始める全体のLR2同期が、親の持たないPを取得する場合 | Pを待たずに取得し、別の操作と競合したら全体同期だけを未実行のまま見送る（`SkippedCompeting`）。確定済みの差分は保持する（[設定・全体操作・終了](#設定全体操作終了)の全体のLR2同期）。 |

競合拒否・見送りにした要求は、予約も自動の再実行もしません。次の明示的な要求を、新しい要求として扱います。一要求の中の複数の入力、成功した対象の集約、内部の上限付きの並列計算は、別の契約として維持します。

#### 設定画面の未確定の候補

設定画面の未確定のBMS・LR2詳細の候補は、実操作の受付とは区別します。

- 候補の編集自体には、LとPの制限を加えません。
- 競合する別の編集へ切り替えるときは、対象範囲の名前を示して破棄を確認し、既存の画面の編集可否と、実処理の受付を再検査します。
- 実処理を伴う即時保存では、実際のLとPの受付と最終検証を通った採用点で、関係する候補だけを破棄します。
- 独立した導入先は、確認後の最終検証と同期項目の保存の間だけ、共通のLを待たずに取得します。Lの実行中は、非交差の導入先も拒否します。確認中はLを保持せず、Pも追加しません。
- BMS候補の破棄の確認だけを、既存の設定の担当の単一操作に渡します。取消、同値、不正な入力、受付の拒否では元の候補を保ち、新しい要求を予約しません。
- 稼働環境の変更は、破棄の案内と再起動の確認を一回にまとめます。Shellの先着の受付が拒否した場合も、候補を保ちます。
- 保存済みの媒体と、受理済みの処理の変更不能な入力は、破棄の対象に含めません。

操作ごとの範囲は[設定の候補切替](../runtime/settings.md#競合する候補の切替)を正本とします。

### 受付の開始・終端と権限

#### 取得の時点

形式の検査と、副作用のない選択・確認を終えて実行を確定したら、対象の解決、通信、コピーなどの準備より前に受付を取得します。確認の前から受付を保持する操作（管理データの撤去など）は、操作ごとの表に示します。

#### 保持する範囲

受理した機能の管理主体は、次の終端まで同じ権限を保持します。

- 入力の取得、計算、一括保存。
- 必要な出力と参照の公開。
- 開始したすべてのTaskの実際の終端。
- 取消・失敗時の後片付け。

通知の受理、`Running` の表示、スケジューラーの空き、世代の変化から、完了を推測しません。短いロックとDB接続は、awaitの前に解放します。論理的な受付を保持することは、これらを長く保持することではありません。

#### 受理済みの継続

内部の継続は、同じ管理主体の生存中の権限を明示的に借用します。

- 自己の受付の再取得や、自己の受付の空き待ちをしません。
- 解放済みの権限と、別の管理主体の権限は拒否します。

すでに受理した必須の更新（初期化・差分の入力の更新など）は、先行する操作の終端を非同期に待てます。新しい要求とは違い、競合拒否で捨てません。

標準構成のLとPは、[構成の寿命](architecture.md#構成が共有する管理主体)で共有します。ライブラリ・保存先・対象・終了状態は個別です。共有する受付を閉じる契機は[構成で共有する受付の閉鎖](../runtime/shutdown.md#構成で共有する受付の閉鎖)に従います。再構築の後にPが有効であることを、旧い保存先の対象が有効である根拠にしません。

#### 保留復元の失敗時の選択

必須初期化での保留復元の失敗に対する選択は、同じ生存中のLとPの中の、受理済みの継続です。

- DBトランザクションとモデルロックを解放してから、非同期に利用者の選択を待ちます。
- 再試行と明示的なリセットだけを、同じ権限で実行します。
- 終了が選ばれた場合は、親のLとP、排他、UIの抑止を解放してから、通常のCloseを要求します。初期化のTask自身から、終了の完了を待ちません。

詳細は[保留復元の失敗時選択](../runtime/startup.md#保留復元の失敗時選択)に従います。

### ライブラリ・保留・保守

| 操作と入口 | 分類と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 導入、ドロップ、URL取得後の導入、保留の登録・削除・手動推定・目録<br>導入・ドロップ: [`PackageInstallWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs)、URLの引渡し: [`PlaylistUrlAcquisition`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs)、保留の登録・削除・手動推定: [`PendingPackageWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs)、目録: [`PackageCatalogWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PackageCatalogWorkflowOwner.cs) | L。副作用を伴う入力の確保の前から、推定、後段の導入、必要な同期、必須の公開、入力の回収の実際の終端まで。実行中に追加された入力は、未受理の競合拒否。 | 新規の取り込みの直接推定と、設定が有効な場合の後段の導入は、同じLを借用する。起動時に復元した保留は、手動推定だけを行う。目録の解除に伴う管理入力の回収は、同じLの中で、再生の停止と開始済みの読込みの合流を待ってから行う（[保留目録の解除](../library/mutations.md#保留目録の解除と管理入力の回収)）。[ドロップ導入](../library/drop-install.md)、[導入先推定](../library/install-estimation.md)。確認: [対応表](#実装とテストの対応)の「導入→直接推定・後段導入・取消/例外・全Taskの合流」。 |
| 重複結果の新規解析<br>[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | L。画面の選択を受理する前に待たずに取得し、解析・警告の確定・必要な通知の完了まで保持する。競合拒否は未開始であり、有効な表示と検索を保ち、予約しない。 | 完成した結果の表示と、進行中の解析の参照は、新規のL取得の対象外。受理済みの変更の仕上げは、同じ生存中の権限で内部継続し、更新抑制の解除より前に開始して、親のLの解放前に完了する。行の構築・描画の終端を、解析のLの終端と結び付けない。[重複ファイル](../library/duplicate-files.md#初回解析中の画面移動)。確認: [対応表](#実装とテストの対応)の「重複解析の新規のL受付、参照、内部の継続」。 |
| 譜面の削除、配置・フォルダの改名、文字コード、難度、重複の除去・統合<br>譜面変更: [`SelectedChartMutationWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartMutationWorkflowOwner.cs)、フォルダ改名: [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、重複の除去・統合: [`DuplicateMaintenanceWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs) | L。実際の対象の解決から、物理変更、一つの変更セッションでのDB・索引の反映、必要な公開、後片付けまで。管理出力と交差する場合だけL＋P。 | 統合の確定後の必須保守へ、同じ生存中のLを渡す。明示した統合・ハッシュ整理では、成功後の自動選択のためのグループの準備も、現在の画面によらず親のLの中で完了する。先行して確定した事実と元の失敗を保持する。[ライブラリ変更](../library/mutations.md)、[ファイルとDBの整合性](../library/file-db-consistency.md)。確認: [対応表](#実装とテストの対応)の「実際のLの入口、通知・後片付け、予約なし」。 |
| 再検査、警告の無視、解析失敗の登録の除去、ゼロノート・譜面情報の保守<br>再検査・警告の無視: [`SelectedChartResourceHealthWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartResourceHealthWorkflowOwner.cs)、解析失敗: [`ChartInfoParseFailureRemovalWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/ChartInfoParseFailureRemovalWorkflowOwner.cs)、ゼロノート: [`ZeroNoteMaintenanceWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/ZeroNoteMaintenanceWorkflowOwner.cs)、再走査: [`MaintenanceRescanWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceRescanWorkflowOwner.cs) | L。操作の確定後に取得し、実際のモデルの更新と必要な公開まで。 | 利用者の新しい要求を、待機の予約にしない。[譜面情報](../library/chart-info.md)、[ライブラリ変更](../library/mutations.md)。確認: [対応表](#実装とテストの対応)の「実際のLの入口、通知・後片付け、予約なし」。 |
| 初期化・差分の必須入力の更新、譜面情報の読込み・補完解析・所持譜面の保守<br>初期化: [`BmsLibraryInitializationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/BmsLibraryInitializationService.cs)、差分: [`FileDiffReloadWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/FileDiffReloadWorkflowOwner.cs)、必須保守: [`BMSLibrary.InstallableMaintenance`](../../../BeMusicSeeker/Models/Library/BMSLibrary.InstallableMaintenance.cs) | Lの受理済みの継続。入力の変更と、実際のTaskおよび後片付けの終端まで。 | すでに受理した必須の更新は、先行する操作の終端を非同期に待てる。競合拒否で捨てない。任意の通信や、表示の先行読込みとは区別する。[起動](../runtime/startup.md)。確認: [対応表](#実装とテストの対応)の「設定・再構築・ScoreOnly・終了の実際の終端」。 |

#### 推定の取消と失敗

推定が取り消された場合や、最初の評価で例外が起きた場合は、まだ適用していない兄弟の結果を以後適用せず、保留に残します。以前に適用した結果と確定済みの導入は保持し、開始したすべてのTaskの合流、`SEARCHING` の解除、後片付けを待ちます。候補の分類、先行成功の一時反映、単一候補の並列度と一括の外側の並列は、[導入先推定](../library/install-estimation.md)の保証です。

#### 物理対象が後で確定する取り込み

物理対象が、入力の展開や候補の計算の後に確定する取り込みでは、入口からLを保持し、Pは対象が分かった時点で追加します。

| 状況 | Pの扱い |
| --- | --- |
| 対象が初めから分かる物理変更 | 停止の前にPを取得する。設定などで最初のL＋Pの取得に失敗した場合は、操作全体を未開始とする。 |
| 自動導入の候補から実際の移動先が確定した（`MovePackageFilesForInstallSession` へ渡す範囲が分かった） | 停止・移動・削除・管理DBの出力より先に、Pを取得する。 |
| 自動推定の後の導入 | 初期の検出単位の実際の入力元・出力先・親の片付け範囲で、候補ごとに追加のPを確認する。 |
| 純粋な推定 | 物理的な作用を伴わないため、Pを追加しない。 |

Pが競合拒否となり交差する対象は、部分結果の未処理（自動推定の後の導入では、未開始の保留）として残し、先行する非交差の確定事実を保持します。許可された候補は、同じLで一つの後段のバッチへ渡し、書込みの前に再生の停止を待ちます。取得した追加のPは、必要な公開・同期・入力の回収まで保持します。競合した対象の予約や自動の再実行はしません。

### プレイリストと必要出力

#### 一覧セルの編集

一覧セルの編集は、P（詳細・表の属性）またはL（譜面のフォルダ名・保留の導入先）を、現在の表示への必要な確定値の反映と、編集の後片付けの実際のTaskの終端まで保持します。

- 短いモデルの保護・抑制・変更活動の終了を、論理的な受付の解放と同一視しません。
- 必要な表示の後の独立した並べ替えと全体の再構築は、受付を延ばす理由にしません。取消・古い要求・競合拒否の既存の分類を保ちます。

保証の分担は[一覧セル編集](../ui/table-view.md#実装とテストの対応)を参照します。

#### プロパティと一括編集の準備

既存・新規のプロパティと一括編集の準備も、Pを待たずに取得します。

- 旧い選択の同じIDの解決から、初期値・セッション・画面用ViewModelの生成まで保持し、awaitの間も解放しません。
- 対象が空、または解決できない場合は全体を拒否します。重複する現在の対象はまとめます。
- 準備の成功・失効・例外で解放し、`ShowWindowAsync` より前に終端します。
- 画面の表示中は別のP操作を許可するため、保存時に現行性を検証します。一覧セルの保存は取得済みのPを借用し、同じPの空きを再要求しません。

一括の六操作は、Pの取得・借用の後、差分の抽出・属性の変更・通信の前に、開始時のプレイリストの保存先と同じ正本の参照を、全対象で検証します。

- 全件または一部が失効している場合、同じIDの別の参照、別の保存先は、全体を拒否します。失効した無変更の対象だけを除くこともしません。
- 拒否では、DB・管理出力・入力を保持します。
- 取得失敗・空の属性・出力の競合など、外部属性の初期化での既存の対象別の除外は維持します。
- 起動時の外部同期の予約とスケジューラーは、この受付で変更しません。

| 操作と入口 | 分類と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 新規作成・既存プロパティの保存、詳細セル、フラグ、順序、項目の追加・削除、表の削除<br>プロパティ保存: [`PlaylistPropertySaveService`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、詳細セル: [`DetailEditing`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailEditing.cs)、フラグ: [`PlaylistSummaryBulkEdit`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs)、保存の並べ順: [`PlaylistSummaryBmtSortCoordinator`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistSummaryBmtSortCoordinator.cs)、項目変更: [`Mutations`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs)、表の削除: [`PlaylistRemovalWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs) | P。対象の解決の前から、正本DB、一括のLR2・BMT出力、必要な参照・表示の公開と後片付けまで。 | 新規のドラフトは保存まで正本の外。既存のドラフトは、元の保存先・対象と、編集開始時の保存対象の値を受付時に一度確認し、退役または値が変更されていれば拒否する。同名やURLへの読替え、後からの予約、再適用はしない。[保存と出力](../playlist/storage-and-export.md)、[プロパティ](../ui/playlist-properties.md)。確認: [対応表](#実装とテストの対応)の「Pの通信前の取得、有限のバッチ、ドラフト、保存後の出力・公開の失敗」。 |
| 共通の選択からの外部表・内蔵表・URI/JSONの登録、beatoraja Table URLの取込み、外部の再同期<br>登録: [`ExternalPlaylistImport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistImport.cs)、beatoraja: [`BeatorajaTableUrlImport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.BeatorajaTableUrlImport.cs)、外部同期: [`ExternalPlaylistSync`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs) | P。入力の検証の後、登録済みURLの最終照合・通信・解析の前から、複数対象の一括登録、必要な出力・公開まで。 | 取込みが競合拒否の場合は、選択を保持して拒否し、次の明示的な要求だけを受ける。任意の起動時の同期は、新規に待たずにPを取得し、競合したら見送る。親の必須の同期は、生存中のPを借用する。先入れ先出しの追加と、取得後の再捕捉はしない。有限の一要求の中での、成功・同じURL・同名・失敗の集約は維持する。[取込み](../playlist/import.md)、[URL取込み](../playlist/table-url-import.md)、[保存と出力](../playlist/storage-and-export.md)。確認: [対応表](#実装とテストの対応)の「Pの通信前の取得、有限のバッチ、ドラフト、保存後の出力・公開の失敗」。 |
| 表一覧の再読込み、必須の項目読込み・出力の修復<br>再読込み: [`PlaylistTablesReloadWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistTablesReloadWorkflowOwner.cs)、初期化・項目読込みの継続: [`BMSPlaylist.InitializeAsync`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | P。親の初期化・再読込みの直接のTaskとして、必要な更新・出力・公開まで。 | 同期的な通知から別の予約へ流さない。任意のGC・表示キャッシュ・オンラインの準備を、親の完了条件へ加えない。[起動](../runtime/startup.md)、[保存と出力](../playlist/storage-and-export.md)。確認: [対応表](#実装とテストの対応)の「Pの通信前の取得、有限のバッチ、ドラフト、保存後の出力・公開の失敗」。 |
| LR2管理表の部分反映、BMTの生成・削除・Table URLの反映<br>LR2の部分反映: [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs)、BMTの実出力: [`PlaylistBmtOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs)、変更元の必須出力の待機: [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | P。変更元と同じ権限で、実際の出力と失敗通知まで。 | 同期的な `BMSPlaylist` の中核は有限の変更事実を返し、機能の管理主体が実際の出力をawaitする。部分集合の出力で、全量の古い出力の片付けをしない。[LR2カスタムフォルダ](../playlist/lr2-custom-folders.md)、[BMT出力](../playlist/bmt-export.md)。確認: [対応表](#実装とテストの対応)の「Pの通信前の取得、有限のバッチ、ドラフト、保存後の出力・公開の失敗」。 |

#### 再同期の前の編集

再同期の前の古い詳細行から届く編集は、同じ永続ID・ハッシュ・題名などを持っていても、最新の項目へ読み替えて保存しません。Pの取得後に元の表・項目の参照を確認して拒否し、現行のDBと表示を維持して、最新の行からの再操作を案内します。通常の閲覧用の参照の更新と、最新の行からの新しい編集は維持します。

#### 保存後の出力・公開の失敗

DB確定後の出力・公開の失敗は、確定したDBを保持する部分失敗です。失敗をログだけの成功に変えず、次を維持します。

- 未変更の出力の省略、manifestの互換、管理外のファイル、生成の並列と準備資源の再利用。
- 出力用のハッシュの補完は、確定済みの読取りモデルを使い、Lを取らない。
- 進行中の管理配置の移行では、準備済みの旧い出力先と新しい出力先を、実際の終端まで保護する。
- 新しい配置の確定後に旧い出力先の削除が失敗した場合は、新しい配置、残余、元の警告を保つ。次の日常の処理は新しい配置を使い、旧い出力先の片付けを再開しない。過去の全残余の永久的な履歴は持たない。

### 設定・全体操作・終了

#### 設定画面の操作の受付

設定から明示する非同期の実操作は、設定のViewModelが同時に一件だけ受け付けます。

- ファイルの選択や確認の前に拒否し、無効の表示と実際の入口を揃えます。
- この設定内の受付は、LとPの権限を代用しません。受理済みの処理の本体は、設定の受付を取り直さず、既存のLとPの受付と最終検査へ進みます。
- 無関係な同期項目の保存とページの移動、自動の音声列挙・能力照会は、この単一操作に統合しません。

設定のWindowの閉鎖と、実際のTaskの終端は区別します。

- 通常の使用中は、実操作・明示した音声テストの最中も、Close・Escと再表示を許可し、未確定の候補だけを破棄します。
- 受理済みの入力と、同じTask・操作制限は、設定の担当がWindowを越えて保持します。
- 初回の利用開始・再起動・復元・撤去は、既存の必須の終端まで通常の閉鎖を保留します。
- 通常の閉鎖や、手動同期の前に行う自動の閉鎖の後も、Shellは設定の担当の同じTaskに直接合流して待ちます。

| 操作と入口 | 分類と開始・終端 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 通常の設定項目、局所の候補の編集・取消、音声・画像・再生方式・recordの同期的な局所確定 | LとPは不要。成立した対象だけを選択して保存し、成功後に公開する。 | 別のセクションの候補を混ぜない。通知や処理待ちの最中も、無関係な通常項目と独立した同期確定は保存できる。音声テストと環境の開始・再起動の制約は維持する。[設定](../runtime/settings.md)。 |
| BMS一覧の局所確定、スコア取得元の有効な選択 | ライブラリに依存する処理の保存前から、必要な継続まで、L＋P。BMSは最終の一覧を一回、スコアはスコアだけを処理する。 | 競合拒否では保存・実処理を0として候補を保持する。実処理・後片付け、親の受付の解放、必須のUI・通知の実際の終端までを、同じ設定のViewModelが、受理済みの要求とTaskとして所有する。通常のClose・Escと再表示でも、この所有と操作制限を継続する。 |
| BMTの局所確定、同値の全表の再更新 | 準備・保存の前から、全表の投影、URLの切替、今回の旧い出力先の整理までP。通常項目には昇格させない。 | 最新の入力の再保存と強制投影を許容し、過去の成功した段階は保持しない。既存の担当を使い、管理外のデータとDBの元のハッシュを保つ。 |
| beatorajaのルート・プレイヤー・派生パスの有効な選択 | 必要なスコア・BMTの更新の準備・保存の前から、L＋P。既存の担当で、スコアだけの更新、旧新のBMTとURLの反映を、同じ操作として待つ。 | 保存済みのBMT方針を使い、未確定の候補を混ぜない。同じ操作の旧新の入力は終端まで保持し、次の変更は最新の入力から処理する。BMTが失敗しても、採用済みのルートに必要なスコアの処理を回収する。再起動しない。通常起動の準備から必須UIの終端までは受け付けない。[beatorajaの局所反映](../runtime/settings.md#beatorajaの局所反映)。 |
| 出力先の明示選択、追加・削除・改名 | 保存の前から、設定・検索ルート・表ヘッダーDBの保存、公開・生成・今回の旧い出力先の整理の終端まで、L＋P。 | 捕捉した今回の入力を終端まで使い、次の明示操作は最新の入力を再保存・再生成する。旧い出力先の整理の失敗だけで、次の設定変更を制限しない。通常型が使う追加先の名前と配置定義が一致しない場合は、該当する表の出力を止め、通常の出力先へ読み替えない。ROOT型は追加先の名前を使わず、ROOT出力を続ける。 |
| モード・LR2パスの反映、再起動と初回の明示的な開始 | 稼働中は、先着の受付と必要なL＋Pを通して、モードとLR2の組だけを保存する。個別のDB・XMLは詳細欄でまとめて確定する。初回は、確定済みの必須設定で一回だけ初期化する。 | モード選択の保存は、未設定・不正な最新の確定LR2パスを許す。ルートの選択・詳細パスの組の確定と、利用開始の必須検証は維持する。確認の取消は保存済みの値へ戻す。再起動の受理後は、既存のClose・合流の後に後継を起動する。通常起動の準備から必須UIの終端までは、重複した開始と環境の反映を拒否する。初回は、必須のBMT・UI・ホストの公開と初回の完了まで、同じWindowを保つ。 |
| 通常起動の必須手続き<br>[`StartupLibraryInitializationWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupLibraryInitializationWorkflowOwner.cs) | 事前の判断の後から、必須出力・必要なLR2・開始したすべてのTaskと後片付けの実際の終端まで、親のL＋P。設定の初期化では、同じ管理主体の権限を借用する。 | 必須の実Taskと後片付けの終端、実際の親の解放、受付外の必須UI・ホストのTask、初回・成功の公開と解禁、の順を、通常・初期設定・Allで統一する。任意の登録は解禁後に閉鎖して開始し、外部同期は通信前に通常の新規のPを取得する。読取り・解析の並列性と、正本の変更の順序を区別する。[起動](../runtime/startup.md)。 |
| Allの再初期化、全体の復元、履歴スキーマの変更<br>再初期化: [`MainWindowViewModel.InitializeAsync`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、復元: [`PlaylistRestore`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistRestore.cs)、スキーマ: [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | L＋P。変更の開始前から、新しい保存先の必須の初期化・公開、必要な出力・後片付けまで。 | 短いDB・スキーマの保護は別に維持する。終了が必要な操作は、自己の排他を待つ構成にしない。[起動](../runtime/startup.md)、[保存と出力](../playlist/storage-and-export.md)、[終了](../runtime/shutdown.md)。確認: [対応表](#実装とテストの対応)の「設定・再構築・ScoreOnly・終了の実際の終端」。 |
| 全体のLR2同期（手動・条件付きのFileDiff・自動起動）<br>[`Lr2SongDbSyncWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) から [`Lr2SongDbSyncRequestCoordinator.QueueAsync`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncRequestCoordinator.cs)。差分の入口は [`FileDiffReloadWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/FileDiffReloadWorkflowOwner.cs) | L＋P。手動の新規の要求は、設定入力の捕捉・`Running` の公開・準備の前に待たずに取得し、競合したら未実行のまま見送る。起動・設定・差分の継続は、親の生存中の権限を借用する。親が持たないPは待たずに取得し、別の操作と競合したら全体同期だけを未実行のまま見送る（`SkippedCompeting`）。確定済みの差分は保持する。 | 開始後は同じ権限で、実際の作業と保存・後片付けを直接awaitする。内側でスケジューラーの枠や第二の受付を待たない。確定済みの部分反映と、未実施の全体反映を、偽の `Completed` にしない。[LR2楽曲DB](../integration/lr2-song-db.md)。確認: [対応表](#実装とテストの対応)の「共通の規則、生存中・失効・別の管理主体、通常のLとP、交差・両取得」。 |
| 管理データの撤去 | 確認までL＋P。通常のShellの終了要求を受理して、要求元のLとP・設定操作のTaskを解放する。 | Close・モード・更新と同じUI Dispatcher上の先着判定。全実処理の終端の後に、撤去と成否の通知を行い、失敗時も終了する。自己の受付を待つ循環は作らない。[終了](../runtime/shutdown.md)、[設定](../runtime/settings.md)。 |
| 通常のClose、モードの再起動、更新適用のための終了<br>[`MainWindow.OnClosing`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) から [`ShellShutdownWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | 新規の受付を閉じ、規定の取消の後に、受理済みのすべてのLとP・音声・DB・後片付けの実際の終端を待つ。 | 共有する受付の閉鎖の時機と、閉鎖後も保持する継続は[構成で共有する受付の閉鎖](../runtime/shutdown.md#構成で共有する受付の閉鎖)に従う。Closeを競合拒否で放置しない。終了要求の通知だけを、UIのClosedや実際の終端として扱わない。[終了](../runtime/shutdown.md)。確認: [対応表](#実装とテストの対応)の「設定・再構築・ScoreOnly・終了の実際の終端」。 |

#### LR2の閏年バグの修復の確認

通常起動、設定の明示的な初回開始、公開されている `Initialize` / `Reinitialize` は、設定の検証と未設定の案内の後、LR2の閏年（2/29）バグの修復候補を確認してから、L＋Pを待たずに取得します。候補の条件・確認・結果の内容は[LR2閏年日時修復の判断と終端通知](../runtime/startup.md#lr2閏年日時修復の判断と終端通知)に従い、ここでは受付との関係を定めます。

- 受付の前の候補の読取りと確認は、受付の外で行います。設定の適用での確認は保存の前に行います。
- すでに競合拒否となる状態なら、候補の確認・保存・修復を始めません。確認中も共有する予約を持ちません。取得時の先着の競合は副作用の前に拒否し、次の明示的な要求で候補を新しく捕捉します。
- 受理の後は、承認した対象を再検証してから修復します。
- 実際の修復、通常の初期化、開始済みのTaskの後片付けは、同じL＋Pの中で行います。終端の警告・失敗の通知は、両方の受付、書込みの保護、DBトランザクションを解放してから行います。

#### ディレクトリ前処理の警告

専用のディレクトリ前処理の警告は、未実行の失敗を確定し、開始したすべてのTaskと後片付けを回収した後に、LとPを続けて解放してから提示します。警告自体では、設定の変更・必要な出力・参照の公開を行わず、提示後の新しい明示的な要求を受け付けます。必要な出力や後片付けの途中で受付を解放する例外ではありません。

#### スコアの確定と設定の公開

スコアの確定は、最新の受理済みの入力だけを保存・読込みし、過去の読込みの失敗による予約や入力の固定を持ちません。実処理と後片付けの後に親のLとPを解放しても、必須UIと失敗通知の実際の終端までは、同じ設定操作のTaskを所有します。過去の失敗だけを理由に、次のスコアの入力・LR2環境の確定・復元を拒否しません。[beatorajaの局所反映](../runtime/settings.md#beatorajaの局所反映)に従います。

保存・適用済みの配置は、設定の永続化の段階が正常に完了したときに公開します。部分保存や確定後の失敗の扱いは[設定](../runtime/settings.md)に従います。一操作で必要な小さい設定の入力を捕捉して、非同期の準備へ明示的に渡します。次の明示的な操作は、新しい入力を捕捉します。

### 音声と独立した読取り

| 操作と入口 | 分類・寿命・理由 | 継続・結果・機能仕様 |
| --- | --- | --- |
| 新規の試聴・Next<br>[`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | Lの実行中は競合拒否。通常の試聴同士の既存の受理と順次実行は維持する。 | プレイヤーの開始・保留のコピーの前に判定する。変更の側は、必要な受理済みの準備・停止・回収を待つ。停止は競合拒否にしない。推定の開始だけで通常の再生を止めない。[音声](../runtime/audio.md)。確認: [対応表](#実装とテストの対応)の「一時試聴の受付・回収」と「URLの一時領域・JSON・音声ネイティブの実寿命」。 |
| 一時コピーの試聴、音声変換<br>一時試聴: [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、変換: [`SelectedChartAudioConversionWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs) | L。一時試聴は、候補の解決の後、セッション・世代・選択・表示を変更する前に待たずに取得し、コピー・開始の境界・保持時間・全回収・元の名前の復元の試行まで保持する。変換は、入力の利用から、エンコーダー・セッション・ネイティブの実際の解放まで保持する。 | 一時試聴の拒否では、旧い状態・再生面・保存済みの表示設定を維持し、面の選択要求も予約しない。受理済みの面の要求は、UIの配送の終端を待たずに投稿する。[音声](../runtime/audio.md)、[一時ファイル](managed-temp-files.md)。確認: [対応表](#実装とテストの対応)の「一時試聴の受付・回収」と「URLの一時領域・JSON・音声ネイティブの実寿命」。 |
| 譜面URLのダウンロード、外部パッケージの検索<br>[`PlaylistUrlAcquisition`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs) | 変更不能な入力の確保であり、Pは不要。取得したすべてのパスを、一度にLの導入へ渡す。 | 競合拒否は予約しない。取得の成功と、導入の受理・確定を、別に報告する。未受理のアプリ所有の一時領域は取得側が回収を待ち、受理済みの入力は導入の実際の終端まで保持する。持込み・利用者指定の保存物は削除しない。[URL取得](../playlist/downloads.md)。確認: [対応表](#実装とテストの対応)の「URLの一時領域・JSON・音声ネイティブの実寿命」。 |
| JSON出力、SQLバックアップ<br>JSON: [`PlaylistExport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs)、バックアップ: [`PlaylistBackup`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistBackup.cs) | 整合した読取り。JSONは出力値だけを加工し、Data_urlを書き換えない。 | [保存と出力](../playlist/storage-and-export.md)。確認: [対応表](#実装とテストの対応)の「URLの一時領域・JSON・音声ネイティブの実寿命」。 |
| 閲覧・検索・表示の並べ替え・選択、設定の閲覧・編集・取消、項目の表示用の読込み、URLの補完、先行読込み、GC<br>表示・選択: [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、設定の編集: [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)、派生表示: [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | 確定済みの入力と派生キャッシュを使う、独立した処理。 | 独立した表示世代・対象の識別・ネイティブと読取りの寿命は維持する。正本の更新が必要なら、対応する領域へ分類し直す。[データと索引](data-and-indexes.md)、[画面](../ui/README.md)。確認: 各機能の既存テストが、独立した表示キャッシュの失効・再接続・現行性を維持する。 |
| 譜面プロパティの明示的な表示<br>[`ChartPropertiesViewModel`](../../../BeMusicSeeker/ViewModels/ChartOperations/ChartPropertiesViewModel.cs)、[`ChartResourceDiagnosticsService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceDiagnosticsService.cs) | 独立した読取りの表示。LとPを取得しない。 | 捕捉した単一のファイルと、その後の存在確認を表示する。進行中の変更や外部の更新との原子的な整合は求めず、取得・解析の失敗は画面内で扱う。毎回新しい独立した窓を開き、複数の表示と、読取り中のメイン操作を許可する。表示済みの結果は、選択やファイルの変更に追従しない。メインの終了の受付で全窓を閉じて新規の受付を止め、個別にCloseした窓も含む実際の読取りのTaskを、最終の終了の前に合流して待つ。閉鎖後に結果を反映しないことと、通常の失敗の回収を維持する。[譜面プロパティ](../ui/chart-properties.md)。確認: `ChartPropertiesViewModelTests` / `ChartResourceDiagnosticsTests`。実際のメニュー・管理・MainWindowの終了の接続は同機能の対応表、LとPを取得しないことは静的に確認する。 |
| 外部リンク・Explorer・外部プレイヤー・Score Viewer、順位キャッシュ、音声一覧の取得・能力照会・デバイステスト<br>外部起動: [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、音声の照会・テスト: [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | 外部の起動と候補の取得を、LとPの変更に含めない。独立した音声セッションの既存の所有と終了待ちを維持する。 | 設定の一覧の列挙はLとPを取得せず、列挙のTaskの実際の終端まで、能力照会と明示的なテストを開始しない。他の設定の編集・保存・取消・通常の閉鎖は継続でき、表示が失効した後も、音声の管理主体の完了のTaskを既存の終了経路で待つ。通信や外部プロセスとの全競合を、確認済みとは扱わない。[音声](../runtime/audio.md)、[外部連携](../integration/README.md)。確認: 各機能の既存テストが、独立した表示キャッシュの失効・再接続・現行性を維持する。 |

保留譜面の操作バーにある推定・手動導入・クリアは、一覧メニューと同じ既存の保留操作群へ接続します。クリック時に、現在の変更活動をUIの抑止で拒否し、各管理主体のLの受付と実際の終端を維持します。競合拒否の後の予約や自動実行は追加せず、解放後の次の明示的なクリックを新しい要求として扱います。対象と表示条件は[保留譜面の操作バー](../ui/table-view.md#保留譜面の操作バー)に従います。

### 新機能・変更時の手順

1. 実装の前に、本番の入口と、読む・変える共有物を、既存の操作群へ対応付けます。
2. 新しい分類や例外が必要なら、利用上必要な並行性を[競合対策を決める手順](workflow-concurrency.md#競合対策を決める手順)で判断し、未確定事項は利用者へ確認します。
3. 開始・終端・競合拒否・継続の期待は本書で確定し、機能仕様には固有の処理だけを書きます。
4. 保証は、既存の小さい受付の検証、各入口への実際の接続、代表的な横断効果で分担します。操作の全組合せや行数に対応するテストは増やしません。
5. 同じ変更で、本書、機能仕様からの参照、実装・検証の対応を更新します。調査履歴や、重複した競合の台帳は残しません。

設定の補助操作は[補助操作の対象・受付・閉鎖](../runtime/settings.md#補助操作の対象受付閉鎖)に従い、確定した環境を使います。候補の破棄の確認の承認を、実際の受付とは扱いません。関係する候補を戻すのは、次の受付と最終検査の後です。

| 操作 | 候補を戻す時点 |
| --- | --- |
| 手動のLR2同期 | 実際のLとPの取得と最終検査の後。 |
| 履歴スキーマの変更 | 既存のLとPの取得と最終検査の後。 |
| Table URLの取込み | 実際のPの取得と最終検査の後。 |

競合時は候補と画面を保ち、予約や自動の再実行をしません。設定のWindowの閉鎖と実際のTaskの終端を区別し、受理済みの操作は既存の管理主体が終端まで追跡します。

## 実装とテストの対応

各入口の分類と権限の転送は、本番の接続の静的な確認も分担します。テストで同じ内部の処理順を再生成して判定せず、実際の受理、副作用、保存した事実、すべての実際の終端を観測します。

| 保証・境界 | 実装 | 検証・確認の分担 |
| --- | --- | --- |
| 独立した導入先の短いLと、明示的な再操作 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | `SettingDialogCandidateSwitchTests.IndependentInstallSelection_*` / `ConflictingBmsCandidates_SwitchOnlyAfterValidAcceptedOperation` が、実際のLの最中の拒否、確認中の先着、保存区間、候補の保持を確認する。コンパイル済みの代表ケースが、元の表示と次の明示的な選択を確認する。通常の保存と、Pや別のWindowの既存の許可を保持する。 |
| BMTの準備・採用・URL・整理と、ルートの後の必要なスコアの回収 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[`PlaylistBmtOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs) | `SettingDialogEditCompletionTests.BmtSettingsAdoption_PreservesAdoptedMediaAndAllowsCurrentNextInput` が採用前後の実際の媒体を、`SettingsDialogBehaviorTests.BeatorajaRootConfirmation_BmtFailureStillAwaitsScoresAndRequiredUi` が、親のLとPの中のスコアと、解放後の必須UI・設定のTaskを確認する。通常のClose・再表示、コンパイル済みのBMT、Shellの合流は、既存の設定の表示・終了の系列が分担する。 |
| LR2の短い保存の排他と、操作ごとの限定公開 | [`Lr2ConfigOwner`](../../../BeMusicSeeker/Models/LR2/Lr2ConfigOwner.cs)、[表の削除](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs) | [`Lr2PartialBmsSaveRetainsDeletedManagedRootThroughExplicitRetry`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogLr2OwnerTests.cs) が、XMLだけの成功・実際のROOT表の削除・明示的な再試行を確認する。`ExternalPlayerProcessGatewayTests`（[`Lr2PreviewOwnerTests.cs`](../../../tests/BeMusicSeeker.Core.Tests/Processes/Lr2PreviewOwnerTests.cs) の部分クラス）が、別の作業での保存とプロセス待機の分離を確認する。 |
| 保留譜面の操作バーの、変更活動中の拒否と次の明示的な操作 | [`MainWindow.GetPendingInstallBarTargets`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) と既存の保留の終端 | [`MainWindowPackageMaintenanceWpfTests.PendingInstallBar_UsesCurrentBoundSelectionAndPreservesMultipleRowsAfterFocusRequest`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPendingInstallBarWpfTests.cs) が、実際の変更活動の最中のクリックが終端に届かないこと、解放だけでは実行されないこと、次の明示的な導入だけが届くことを確認する。LとPの受付の本体は、既存の管理主体のテストが担当する。 |
| 重複解析の新規のL受付、参照、内部の継続 | `RegularChartListOwner`、`BMSLibrary.CompleteAcceptedDuplicateDataAsync` と、各変更の非同期の外側 | [重複ファイルの対応表](../library/duplicate-files.md#実装とテストの対応)に従い、実際の解析と実際の移動の相互拒否、競合拒否時に副作用がないこと、通知中の受付の保持、描画との独立性、表示中の内部継続と非表示時の失効、抑制解除の時点の選択の準備への合流を確認する。共通の権限の別の所有者と解放済みの権限の拒否は、既存の `ChartFileOperationSynchronizerTests` が分担する。 |
| 共通の規則、生存中・失効・別の管理主体、通常のLとP、交差・両取得 | [`ChartFileOperationSynchronizer`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartFileOperationSynchronizer.cs)、[`ApplicationComposition`](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs) | [`ChartFileOperationSynchronizerTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/ChartFileOperationSynchronizerTests.cs)、[`ApplicationCompositionAdmissionTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs) / [`ApplicationCompositionAdmissionTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs)、[`BmsLibraryMutationBoundaryTests`](../../../tests/BeMusicSeeker.Windows.Tests/Library/BmsLibraryMutationBoundaryTests.cs)。設定と全体のLR2同期の実際の入口での両方の取得とDBも併せて確認する。 |
| 保留目録の管理入力の回収と、停止待ちの最中のL・競合拒否・次の明示的な要求 | [`PackageCatalogWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PackageCatalogWorkflowOwner.cs)、[`BMSLibrary.PackageInstall`](../../../BeMusicSeeker/Models/Library/BMSLibrary.PackageInstall.cs) | [ライブラリ変更の対応表](../library/mutations.md#実装とテストの対応)の、保留の解除・再生状態・実際の入力の終端の項目が分担する。 |
| 実際のLの入口、通知・後片付け、予約なし | [`SelectedChartMutationWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartMutationWorkflowOwner.cs)、[`SelectedChartResourceHealthWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartResourceHealthWorkflowOwner.cs)、[`ChartInfoParseFailureRemovalWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/ChartInfoParseFailureRemovalWorkflowOwner.cs) | 対応する管理主体のテストの実際のモデルと小さいDB、[`RegularChartFolderRenameTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartList/RegularChartFolderRenameTests.cs) / [`RegularChartFolderRenameTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/RegularChartFolderRenameTests.cs) の競合拒否と次の明示的な要求。 |
| Pの通信前の取得、有限のバッチ、ドラフト、保存後の出力・公開の失敗 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs)、[`PlaylistPropertySaveService`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、[`PlaylistBmtOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs) | [`PlaylistWorkspaceExternalSourceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs) / [`PlaylistWorkspaceExternalSourceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs)、[`PlaylistWorkspaceActionWorkflowTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) / [`PlaylistWorkspaceActionWorkflowTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[`BmsPlaylistPersistenceLifecycleTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) / [`BmsPlaylistPersistenceLifecycleTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) / [`BmsPlaylistMigrationAndRegistrationTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)。[`MainWindowPlaylistWorkspaceWpfTests.ResyncedDetailEdit_RejectsOldRowThroughOwnedNotificationAndSavesCurrentRow`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) が、実際の再同期後の旧い行の拒否、実際に所有するWindowへの通知、最新のDBと項目の保全、最新の行の明示的な保存を、一つの本番の接続で確認する。実DB・ファイルシステム・出力の障壁を上位の判定とし、BMTの形式と並列資源の再利用は [`BmtTableExportServiceTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmtTableExportServiceTests.cs) が分担する。 |
| 編集準備の短期のPと、六操作の全対象の現行性 | `PlaylistWorkspaceViewModel.PropertyEditing`、`PlaylistSummaryBulkEdit`、`PlaylistSummaryBuild` | [プレイリストのプロパティの対応表](../ui/playlist-properties.md#実装とテストの対応)の、三つの準備の入口、実際の項目読込み中のP、全件・一部・無変更の対象の失効、同じIDの置換、別の保存先と、実際の外部再同期の後の旧いドラフトの拒否から開き直しての保存。保存境界での全件の失効・空の入力・非厳格な互換は、`BmsPlaylistPersistenceLifecycleTests.CommitHeaders_CurrentTargetRequirementRejectsAllAndPartialRetirementButAllowsEmpty` が分担する。 |
| 設定・再構築・ScoreOnly・終了の実際の終端 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)、[`ShellShutdownWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | [`SettingDialogEditCompletionTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogEditCompletionTests.cs)、[`SettingDialogEnvironmentConfirmationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogEnvironmentConfirmationTests.cs)、[`SettingDialogOperationShutdownTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogOperationShutdownTests.cs)、[`MainWindowViewHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowViewHostTests.cs)、[`MainWindowPlaylistWorkspaceWpfTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) / [`MainWindowPlaylistWorkspaceWpfTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) が、同じ生存中の権限、実際の保存先・スコア・保存、Task・出力・終了の順序、環境の組の競合拒否時の候補の保持と次の明示的な確定を確認する。スキーマと撤去は [`Lr2PlayHistorySchemaUiTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/PlayHistory/Lr2PlayHistorySchemaUiTests.cs) / [`Lr2PlayHistorySchemaUiTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/PlayHistory/Lr2PlayHistorySchemaUiTests.cs) と [`ApplicationDataUninstallWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Settings/ApplicationDataUninstallWorkflowOwnerTests.cs)、BMS一覧の局所確定は設定の実際の差分の継続のテストが、両方の受付と実際の終端を分担する。`SettingsWindowPresentationTests.MainWindow_NormalSettingsCloseEndsModalBeforeActualFileDiffTerminates` が、通常の閉鎖と新しいWindowへの同じTaskの接続、Shellの合流を、`SettingsDialogBehaviorTests.SettingsOperation_RejectsCompetingRequestsUntilCleanupAndFailureNotificationTerminate` が、実際の入口での拒否と、通知の終端までの受付を確認する。 |
| 一時試聴の受付・回収 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests.TemporaryCopy_AdmissionRacePreservesExistingSessionUntilExplicitInput`](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs) が、冒頭の確認の後に実際のLを先取りして、各入口の拒否と副作用がないこと、明示的な操作での終端を確認する。既存のReady・外部開始・残留通知のケースが、回収・元の名前の復元までの保持と、通知前の解放を分担する。 |
| URLの一時領域・JSON・音声ネイティブの実寿命 | [`PlaylistUrlAcquisition`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs)、[`SelectedChartAudioConversionWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartAudioConversionWorkflowOwner.cs) | [`PlaylistUrlAcquisitionOwnershipTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistUrlAcquisitionOwnershipTests.cs) / [`PlaylistUrlAcquisitionOwnershipTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistUrlAcquisitionOwnershipTests.cs)、[`PlaylistWorkspaceActionWorkflowTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) / [`PlaylistWorkspaceActionWorkflowTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[`SelectedChartAudioConversionWorkflowOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/SelectedChartAudioConversionWorkflowOwnerTests.cs) / [`SelectedChartAudioConversionWorkflowOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartOperations/SelectedChartAudioConversionWorkflowOwnerTests.cs) が、JSONの実際のシリアライザーでの正本の不変、実際の導入の入力の終端、ネイティブの解放を確認する。 |
| 導入→直接推定・後段導入・取消/例外・全Taskの合流 | [`PackageInstallWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs)、[`BMSLibrary.PackageInstall`](../../../BeMusicSeeker/Models/Library/BMSLibrary.PackageInstall.cs) | [`PackageInstallWorkflowOwnerAutomaticContinuationTests`](../../../tests/BeMusicSeeker.Core.Tests/Install/PackageInstallWorkflowOwnerAutomaticContinuationTests.cs)（`PackageInstallWorkflowOwnerTests` の部分クラス）が、後段の候補別のPの競合と独立した候補の一括成功、書込み前の実際の停止のTaskとLR2有効時の追加のPの保持、パッケージ境界での取消後の必要な同期とLとPの解放を確認する。推定の停止と全Taskの合流は [`PackageInstallWorkflowOwnerEstimationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs) / [`PackageInstallWorkflowOwnerEstimationTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs)（同じ部分クラス）、先行成功の一時反映・`SEARCHING`・実際の確定事実は [`BmsLibraryPackageInstallServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) / [`BmsLibraryPackageInstallServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) など、機能仕様の下位の保証が分担する。 |

## 関連資料

[ワークフローと並行性](workflow-concurrency.md)、[アーキテクチャ](architecture.md)、[テスト設計](../development/test-authoring.md)、[検証](../development/testing.md)。
