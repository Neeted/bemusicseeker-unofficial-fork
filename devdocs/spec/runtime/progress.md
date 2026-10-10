# 初期化・再読込みの進捗

## 目的と適用範囲

ステータスバーの表示行、`StartupProgressWorkflowOwner` が管理する親進捗、操作の識別、完了の順序、後続処理の表示を定めます。表示対象の概観は[ステータスバーに表示する処理](statusbar-progress.md)、処理の内容と準備条件は[起動仕様](startup.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 所有と計算

`StartupProgressWorkflowOwner` は初期化・再読込みの予定、完了、失敗、操作トークンと親ゲージを所有します。`OperationProgressHubViewModel` は各機能が送る事実を表示行へ投影し、画面の親は処理の接続と配置を担当します。表示のために処理の受付・保存・実行順を画面へ移しません。

開始時に予定する段階を固定し、最大値をその件数、現在値を予定済みかつ完了済みの件数とします。後から予約した処理で分母を増やしません。不要な段階はスキップの確定後に完了とします。背景への要求が必要な段階を、要求・スキップの決定前に完了させません。

操作トークンは共通セマフォの取得後、実際の開始直前に作ります。初回はモデルの作成後に基準を捕捉します。遅延表示、フォルダ更新、外部同期、参照反映は、予約時と現在のトークンが一致する場合だけ段階を進めます。

通常起動・初期設定・Allの必須終端はcoreから捕捉設定と既存tokenを持つ局所結果で渡します。UI flush予約を成功へ数えず、受付外のUI実Taskを待って完了を公開します。解禁後、停止中schedulerに任意登録をそろえ、登録閉鎖後に開始します。記録用stopwatchや背景idleを必須完了条件にしません。終了取消・実UI失敗と旧tokenは成功として完了させません。

### 操作ごとの予定

| 操作 | 必須の対象 |
| --- | --- |
| `Startup` | 開始、DB・列挙・差分、データ・実UI・操作解禁、保存スコア・譜面情報・プレイリスト項目。必要参照と出力・LR2・cleanupは直接待った手続きの終端に含めます。 |
| `FullReinitialize` | 既存組のDB・列挙・差分、保存スコア・譜面情報・項目・参照、実UIと操作解禁。 |
| `ReloadFileDiff` | 開始、列挙・差分、項目・参照、必要表示と操作解禁。 |
| `ScoreOnly` | 開始、保存スコア・必要表示と操作解禁。 |
| `ReloadTables` | 開始、項目・参照・利用者が要求した外部同期、必要表示と操作解禁。 |

段階数は表示の実装上の集約です。順位・保守・installable・chart_info補完など任意処理は親の初期予定へ入れず、要求の同一性を持つ後続・独立行として追跡します。直接待った必須手続きと実UIの終端で完了し、ログ・scheduler登録閉鎖・全idleを成功条件にしません。

### 段階の意味

| 識別子 | 完了する条件 |
| --- | --- |
| `CoreInitializeStarted` | 操作を開始した |
| `LibraryDatabaseLoadDone` | 目録のDB読込みを完了した |
| `LibraryFileEnumerationDone` | 譜面・リソースの列挙を完了した |
| `LibraryFileDiffDone` | 差分を反映した |
| `StartupReadyData` | 導入判定用データが揃った |
| `StartupReadyUi` | 親L/P解放後に必須の表示と実host接続のUI Taskを終えた |
| `StartupReadyOperable` | 必須処理・親解放・必須UI/hostが終わり、成功公開と通常入力の解禁が成立した |
| `ScoreHydrationDone` | 現在のスコアを適用した |
| `ChartInfoHydrationDone` | 現在の譜面情報を読み込んだ |
| `PlaylistEntriesHydrationDone` | ローカルのプレイリスト項目を読み込んだ |
| `PlaylistReferenceApplied` | 参照を適用した |
| `ExternalPlaylistSyncDone` | 外部同期を終えた |

必須進捗は親受付解放後の実UIと成功公開で完了します。後続が残っていても必須段階は完了でき、LR2の段階件数・単独失敗を親の分母・ローカル準備成功へ混ぜません。

### 行の単位と配置

実行中の親名は「初期化」「ファイル差分の再読込み」「スコアの再読込み」「プレイリストの再読込み」「全再初期化」で固定します。操作可能の到達で親名を切り替えません。完了・失敗は各操作名へ状態を添えます。通常の親に未完了子を一つ選んだ補足は出さず、明示的な失敗理由だけを保持します。理由が空なら推測した子名を補いません。画面準備は起動のデータ準備完了から操作可能まで子行で示します。

並行する独立処理はそれぞれ一行で同時に表示します。初期化など全体進捗が必要な操作は親行、そこで実行中の処理はインデントした子行とし、原則二段までにします。逐次進む一つの処理の読取り・解析・反映は同じ行の段階変更で表します。内部ワーカーや個々のファイルは独立行にしません。

各行は左のゲージ、残り幅を使う処理名・状態・件数・対象、必要な末尾の取消で構成します。対象名は幅に応じて省略し、全文をツールチップへ渡します。ステータスバー全体を固定一行高さにせず、詳細文字列にも固定幅を設けません。画面の実測高さを配置に使い、行数から別の高さ状態を管理しません。

表示順は処理の意味ごとに固定し、件数更新で並べ替えません。実行中の行を中心に表示し、進めない理由がある待機はその理由を示します。未開始の全予定を子行として並べる必要はありません。同じ仕事のスケジューラー通知と機能専用通知は一つの行へ統合します。独立したURL取得、導入、推定を優先順位で一つの表示枠へまとめません。取消は対象行の本来の所有者へ届きます。LR2の明示同期は設定画面から行います。

プレイリストも要求元の処理を区別します。外部同期、通常の表URL取込み、beatorajaの表URL取込み、選択表の再同期、サマリー一括編集は、それぞれの開始・進捗・終端を同じ要求元の識別へ渡します。既存の操作IDがある場合は保持し、受付が一つに制限される経路に表示用の新しい操作台帳を追加しません。外部同期のスケジューラー行は、同じ外部同期の専用行がある場合だけ統合し、別の取込み・編集の行を理由に隠しません。

### 件数と通知

表示識別は受付時に捕捉し、再利用ワーカーは各周の要求版・識別・通知先を同じ既存ロックで取り出します。呼出し文脈のない新規受付は、その時点の表示世代とトークン0で独立した要求とし、旧文脈がある要求の世代を付け直しません。スケジューラー版と機能要求版は横断比較しません。参照・外部同期・項目読込みの共用完了スロットには表示用の要求元と実要求版を添え、既存の最大版と完了計算を保ちます。読込み受領や外部同期の各実行周から起こす参照・出力・修復等の表示発生元は、その要求に捕捉した世代と操作を引き継ぎ、後続自身の既存要求版を添えます。機能側のトークン0と完了計算は変更しません。表示識別のトークン0を現在親と一致扱いにせず、スケジューラーの予約世代をリセットしても発生元を付け直しません。

プレイリスト専用通知は既存 `Source` / `OperationId` と捕捉表示識別の組で区別します。同じ組の開始・更新・終端だけが同じ行を所有します。別要求は同じ `Source` でも併存でき、旧要求の終端で新要求の行を消しません。既存の要求版・操作IDがない一般手動操作は既存排他・スコープ・UI通知順序を維持し、表示用の採番や台帳を増やしません。

確認対象、差分解析対象、反映するDB行数は別の単位です。処理名と件数の単位を対応させ、候補総数を変更件数として表示しません。総数が確定していない探索・読込みは不定ゲージにし、探索途中の結果件数から総数を推測しません。親のゲージは完了段階数であり、子の対象件数・実時間の割合ではありません。

初期化詳細は処理別の最新値として集約し、並行するDB読込みと探索の開始通知を互いに上書きしません。通知は送出時に捕捉した操作識別を保持し、受信時の現在操作を古い通知へ付け直しません。置き換えられた操作の通知は現在の行へ適用しません。

譜面情報の読込み・補完では、一つのワーカーが後から予約された要求も処理します。進捗件数にはその処理が捕捉した要求版を保持し、現在の親操作が必要とする要求版と一致する値だけを反映します。ワーカーの開始時点や通知を受信した時点の操作識別で、新しい要求や古い要求を読み替えません。

スコア・順位更新の実行境界も要求ごとに扱います。受付時に表示世代を捕捉し、ワーカーは各周の要求版とその通知先を一緒に取り出します。同じワーカーが次の要求を処理しても、旧要求の終端と新要求の開始を混ぜません。受付時に捕捉した表示世代・発生元操作トークン・要求元・同主体内の要求版が現在の予定・追跡要求と一致する場合だけ子行にし、それ以外は独立行とします。表示のために実行・依存解除・収束の条件を変更しません。

共通の通知集約は最新値を使い、短い処理の開始・中間値が描画されることを必須にしません。実処理の完了は表示排出を待たず、保存成功・失敗を処理済み件数100%から判断しません。通常の成功行は終端で消え、初期化親の完了余韻は維持します。LR2未完了・失敗など利用者の操作が必要な状態は、他の進捗と同時に残します。

### 表示と後続処理の区別

表示は少なくとも開始、DB読込み、列挙、差分、画面準備、各種情報の読込み、必須処理の完了を区別します。件数を持つ子処理は処理済み件数・総数と対象を出せます。

`startup_post_initialization_maintenance_complete` は進捗の段階ではありません。実UIと成功公開後の明示した一括登録を閉じ、固有の依存で実行した後続と事前計算の実終端が揃って空になったことを記録します。ログや集計が新しい仕事を開始しません。必要でないLR2要求への依存を作らず、独立readerの表示収束は含めません。

操作可能の直前から、起動の操作トークンと表示世代を持つ「起動に伴う追加処理」の親を有効にします。別の永続状態や処理件数として扱いません。必須初期化の親行が終了しても後続が残る場合は、起動後処理のグループで追います。プレイリストやLR2などの独立した行の存在を理由に隠しません。

専用のLR2は必須起動・再初期化・差分再読込みで直接待つ実行中に同じ要求の親へ対応させ、終端後の警告・未完・失敗は独立行として表示します。初期化親の実行中・完了余韻中も同時に表示し、親の成功や分母へ混ぜません。

背景表示の終了条件は追跡対象の後続の実終端と同じです。独立readerの遅延表示で寿命を延ばしません。別の操作が起動を置き換えたときは古い表示を無効にします。終了時の画面破棄後までプロセス内の値が残ることは許容します。

作業スレッドからの後続完了も、表示の終端だけを既存のUIディスパッチ経路へ渡します。終端の送出時に操作トークンとスケジューラー世代を捕捉し、UIへ反映する直前に両方の一致を検査します。本体の収束・完了記録は表示排出を待たず、古い終端で次の操作の背景行を消しません。

### LR2フォルダの確認と反映

差分内に準備済みのLR2フォルダ反映がある場合は、空白でない実体化済み要求パス数を確認対象ファイルの分母として固定します。変更不要と判定したファイルも確認済みに数え、譜面の差分解析対象数と区別します。

通知値は0～総数の範囲で進み、保存前の準備済み件数を保存完了と読み替えません。通知は他処理と同じ最新値集約へ渡し、先頭中間値の専用保持や描画機会の保証を持ちません。

反映成功後に `LibraryFileDiffCompletedVersion` を進め、表示スケジューラーの排出を待たせません。反映失敗を成功完了へ変換せず、進捗の通知例外でもDB反映・取消・失敗の意味を変えません。

### LR2楽曲DB全体同期の段階

事前のプレイリスト準備は表の投影を表数、物理出力を実ファイル数で通知し、変更不要のファイルも確認済みに含めます。実体の探索、出力後の整理、ディレクトリ情報の構成、出力状態の保存、準備結果の構成は不定段階です。表ごとの不要ファイル・空ディレクトリの再帰整理は独立した不定段階を残し、次の表の出力では同じ全体ファイル数の有限段階へ戻します。物理整理の順序は変えません。共通の出力処理へLR2専用通知を接続し、独立したプレイリスト操作の既存の複合通知・失敗契約は維持します。

全体同期は同じLR2行を更新し、段階の実対象数を分母、確認・処理済み数を分子にします。カスタム定義の読込み、ディレクトリ情報と `folderinfo.txt` の読込み、検索ルート・通常フォルダ・カスタムフォルダ・親行の生成、投影の構成、利用者値の引継ぎ、楽曲処理は、それぞれの対象件数を使います。対象外パスを省いた場合も、その対象の確認は処理済みに数えます。件数や単位が変わる境界で段階を分け、生成行数と入力候補数を混ぜません。

入力準備・探索、初回DBスキーマ準備、投影の一括検証、既存DB行の取得、全folder表保存、入力の最終確認、同期状態の確定は不定段階として通知します。楽曲は準備終了後に有限段階を開始し、同じ対象の短いチャンク保存は楽曲処理へ含め、同じ役割と実対象分母・処理済み分子を維持します。楽曲の読取り・計算とチャンク保存は既存どおり並行できます。表示のための再走査、追加の複製、待機、保存方式の変更は行いません。

進捗文言とゲージは `StageProcessedCount` / `StageTotalCount` だけを使い、総数がない段階で全体の保存カーソルを借りません。詳細の保存位置は診断としてラベル付きで残します。既知の現行・旧段階識別子は進捗と詳細の両方で役割に対応するリソースへ変換し、未知の保存済み識別子は情報を保持します。準備100%は保存成功・正常終端を意味しません。

通知粒度は処理速度を優先して間引けます。件数が確定した対象ありの段階は件数付きの開始と末尾を生産し、同名の件数なし開始を重ねません。既知0件の件数段階は通知を省略し、量不定の作業へ読み替えません。共通の最新値集約により全中間値の描画や厳密な時機・通知順・数値の絶対単調増加は保証しません。処理の完了条件にUI排出を加えません。

LR2事前準備は段階件数だけを通知し、保存位置を持たない全体のカーソル・総数は未指定にします。並行する楽曲処理・保存の公開スナップショットは一回の通知の段階名と件数から構成し、個別の観測プロパティを読み戻して異なる通知の値を混ぜません。通知順や全中間値の描画は保証しません。

Playlist・通常フォルダ・差分更新・出力先設定変更・Catalog書込み失敗から届く既知の固定段階も、進捗と詳細を処理の役割リソースへ対応付けます。診断は保存形式を変えず、既知の段階と一致する既定の先頭 `ID: ` だけを表示時に置き換えます。未知IDや他の先頭形式、元の例外本文・型・パス・引数・内部例外は保持します。

### 失敗と変更時の制約

失敗は操作を失敗状態にし、通常の使用中と再試行可能な失敗を分けます。進捗完了を理由に作業スレッドからUIの集合を直接変えません。計測を短く見せるために必須処理を後続へ移したり、後続通信を起動進捗へ戻して通常操作を止めたりしません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 予定と完了の集合、古い通知の拒否、表示と失敗 | [`StartupProgressWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs) | [`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) |
| 譜面情報の要求版と進捗件数の一体送出、一致する要求だけの親投影 | [`CatalogChartInfoOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogChartInfoOwner.cs)、[`StartupProgressWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs) | [`ChartInfoInlineHydrationTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs) は同じワーカーの旧要求・後続要求を区別し、[`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) は不一致の件数・対象が現在の子行を更新しないことを確認する。 |
| スコア・順位の要求ごとの表示世代捕捉、親所属と独立した終結 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[`StartupBackgroundTaskSchedulerOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupBackgroundTaskSchedulerOwner.cs)、[`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`BmsLibraryIrStartupTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs) は後続要求と通知例外の隔離、[`StartupBackgroundTaskSchedulerOwnerTests`](../../../BeMusicSeeker.Tests/Startup/StartupBackgroundTaskSchedulerOwnerTests.cs) は受付時の世代と非重複、[`OperationProgressHubViewModelTests`](../../../BeMusicSeeker.Tests/MainWindow/OperationProgressHubViewModelTests.cs) は予定・要求版による所属、[`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) は独立順位処理で後続表示を延命しないことを確認する。 |
| 必須の登録終了、後続の収束と一回の事前計算 | [`StartupBackgroundTaskSchedulerOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupBackgroundTaskSchedulerOwner.cs) | [`StartupBackgroundTaskSchedulerOwnerTests`](../../../BeMusicSeeker.Tests/Startup/StartupBackgroundTaskSchedulerOwnerTests.cs)、[`StartupPostInitializationWarmupOwnerTests`](../../../BeMusicSeeker.Tests/Startup/StartupPostInitializationWarmupOwnerTests.cs) |
| 実UI後の任意登録、実Taskの追跡と背景表示の終了 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs) | [`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs)は実起動・ツリー全再初期化のUI gate保持中に任意開始がないこと、解禁後の開始と末尾Taskまでidleでないことを確認します。[`MainWindowProgressStatusBarWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowProgressStatusBarWpfTests.cs)は実初期化・任意Taskからのcompiled Bindingと行の終端を代表で確認します。 |
| LR2未完了状態と初期化の同時表示、親計算からの独立と失敗表示 | [`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`OperationProgressHubViewModelTests`](../../../BeMusicSeeker.Tests/MainWindow/OperationProgressHubViewModelTests.cs) |
| LR2反映の保存結果と表示排出に依存しない差分完了 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) は実DB結果・完了版とUI排出前の未通知、排出後の最新モデル状態・通知例外隔離を確認する。[`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) は差分完了の親反映を確認し、LR2表示は [`Lr2SongDbSyncStatusMapperTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbSyncStatusMapperTests.cs) とHubのLR2ケースで確認する。 |
| LR2全体同期の実段階・件数と保存前の未確定 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs)、[`Lr2FolderTableReconciliationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderTableReconciliationService.cs) | [`Lr2SongDbSyncServiceTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_ReportsActualStageTargetsAndKeepsPreparationSeparateFromCommit` は小さい実DBと通常・カスタム・楽曲の複数入力から通知を観測し、分母を入力から判定する。通知例外・空投影は既存ケース、実QueueとUI排出の独立は `BmsLibraryLr2SongDbSyncTests` が担う。 |
| LR2の翻訳・保存位置から独立した段階進捗 | [`Lr2SongDbSyncStatusMapper`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncStatusMapper.cs) | `Lr2SongDbSyncStatusMapperTests` は既知・旧・未知・空段階、段階総数なし・0・正数、日本語と英語を確認する。辞書整合は `LocalizationResourceParityTests`、有限→不定→正常終端と親計算の独立は `OperationProgressHubViewModelTests`、Bindingは既存の `MainWindowProgressStatusBarWpfTests` が担う。 |
| 処理別の行、伸縮する配置と行固有の操作 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、[`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) | [`MainWindowProgressStatusBarWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowProgressStatusBarWpfTests.cs) |
| プレイリストの要求元ごとの生産・終端、同じ外部同期だけの表示統合 | [`PlaylistWorkspaceViewModel.SyncProgress`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.SyncProgress.cs)、[`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`PlaylistWorkspaceExternalSourceTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs)、[`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[`PlaylistSummaryBulkEditTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) は実操作からの進捗・終端を確認する。[`MainWindowPlaylistWorkspaceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `MainWindowPlaylistSync_UsesHttpAndUiTerminalBeforeAcceptingAnotherEdit` は実Queue外部同期の要求元・対象・終端と既存の受付・UI終端を確認する。[`OperationProgressHubViewModelTests`](../../../BeMusicSeeker.Tests/MainWindow/OperationProgressHubViewModelTests.cs) の `PlaylistRows_SyncAndBeatorajaImportRemainIndependentInEitherCompletionOrder` は進捗受付境界への開始・更新・終端入力で、併存・同源だけの統合・両完了順の片側保持・保留表示排出後の消去を確認する。 |

## 関連資料

LR2事前準備の生産境界は `PlaylistCustomFolderOutputOwnerTests` が2表3ファイル（1件変更不要）の実出力、不要ファイル・空子ディレクトリの再帰整理と有限復帰、専用通知例外での同等結果を確認します。`BmsPlaylistCustomFolderOutputTests` の `Lr2SongDbSyncPreparation_ReportsActualTablesAndFilesThroughWorkflowWithoutSavingFolderRows` は実Workflowから通知・保存済み出力状態・準備入力・folder表不変・予約解放まで確認します。空表と空ファイル投影では件数段階の通知省略を確認します。

`Lr2SongDbSyncServiceTests.SyncService_RealSongUpdatesKeepFiniteRoleAcrossChunkCommits` は小さい実譜面1001件を実writerで更新し、複数チャンクの保存前後の有限段階と実対象分母、生成列更新・利用者列保持を確認します。複数入力の段階ケースは同名の0/0通知を含めて全イベントを検査し、空入力は件数段階を通知しないことを確認します。Hubは同じ楽曲役割の有限継続と、独立した最終確認の不定表示を分担します。

事前準備の保存カーソル未指定は上記の実Workflowケース、保存位置なしの表示は `Lr2SongDbSyncStatusMapperTests.Create_PreparationStageCountsDoNotAppearAsSavedPosition` が確認します。公開状態の段階名と件数の対応は `BmsLibraryLr2SongDbSyncTests.ProgressPublication_InterleavedOwnerUpdatesKeepStageAndCountsFromOneNotification` が所有者の観測プロパティ更新境界で別通知を挟んで確認します。

[起動](startup.md)、[設定](settings.md)、[終了](shutdown.md)、[性能](../core/performance-and-scale.md)を参照します。
