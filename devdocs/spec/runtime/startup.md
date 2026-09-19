# 起動・初期化・再読込み

## 目的と適用範囲

ライブラリの構築、操作可能になる条件、必須処理と後続処理、各種再読込みの範囲を定めます。起動を単一の完了時刻で表さず、実際に利用できる機能と処理の所有者を区別します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 起動の流れと完了境界

起動は、登録ディレクトリの検査、動作モードと設定の確認、DB構造の修復、必要なバックアップ・最適化、走査とDB読込み、差分反映と保留復元、必須の画面反映、後続処理の順に進みます。走査とメタデータ取込み・DB読込みは並行でき、差分反映は双方の入力が揃ってから始めます。

| 記録名 | 成立する状態 |
| --- | --- |
| `startup_install_estimation_ready` / `startup_install_ready` | 所持目録、導入先リソース索引、保留パッケージが揃い、導入先推定を開始できる |
| `startup_ready_data` | 導入判定に必要なデータが揃う |
| `startup_ready_ui` | 必須の初期表示を適用済み |
| `startup_ready_install` / `startup_ready_operable` | 基本一覧の通常入力を解禁し、起動後処理を開始する |
| `startup_initialization_complete` | 必須のローカル読込みと進捗段階を完了し、ローカルのプレイリスト編集などができる |
| `startup_post_initialization_maintenance_complete` | スケジューラー内の後続処理と登録済みの任意の事前計算が収束する |

最後の記録も、独立した順位・XML更新や遅延表示の完了を含みません。それぞれの記録で確認します。現在の早期受付をより単純な手順へ変更する作業は[未完了の起動整理計画](../../plan/lr2-startup-procedural-orchestration-plan.md)で扱い、変更済みとは記述しません。

`startup_ready_operable` が成立する前は、初期化済みの基本一覧やツリーを前提とする通常入力を処理本体へ入れません。列・行のメニュー、整列、行やツリーの選択、行の起動、URL操作、セル編集など、一覧状態を読み書きする入口ではイベントを処理済みまたは取消として終えます。この拒否だけを理由にモーダルな失敗通知を表示せず、必要な診断はログへ記録します。

初回完了の案内は必須初期化の完了後です。自動のLR2同期は同じ境界で先に一回予約し、案内を閉じるまで待ちません。失敗・中断では予約しません。外部同期、出力、保守、事前計算まで完了したと案内しません。

### 登録ディレクトリの検査

`InitializeAsync` は設定を捕捉した後、出力検索ルートの自動修復より前に、全ての登録BMSルートを作業スレッドで検査します。LR2連携では通常・追加・ルート形式の出力基点も含めます。起動時走査を無効にしていても省略しません。

LR2連携では、検査要求を作る前に `LR2Config.TryLoad` で設定XMLを読み込みます。パスの未設定・不正、ファイルの欠落・読取不能、XML不正、必要な `config/jukebox` 要素の欠落は通常の設定不備です。検査入力を構成できない状態を空の登録ルートや検査成功に置き換えず、設定案内へ戻します。初回かつ全項目が空の場合だけに限定した例外扱いはしません。

通常の起動入口 `InitializeAsync` で設定不備が見つかった場合は、排他とUI抑止を解放してから、初回なら言語選択、それ以外なら通常の設定確認の警告と設定画面へ案内し、`false` を返します。設定保存から開始した初期化では、初回フラグが真でも言語選択へ戻さず、設定確認の警告を表示して、`ISettingsDialogStatePort.InitializeLibraryAsync` から `StartupInitializationOutcome.SettingsRequired` を返します。出力検索ルートの修復・保存、DB処理、モデル構築、走査へ進みません。想定済みの入力不備だけをこの経路で扱い、それ以外の例外は予期しない失敗として通知・記録します。

初期化内部の設定準備・モデル構築・ファイル初期化からは設定画面を開きません。通常起動の失敗は外側の初期化入口が排他を解放してから案内します。設定から開始した場合は、初期化の結果と後片付けを呼出元へ返し、設定の保存処理が画面を保持するか再表示するかを判断します。成功、設定修復が必要な失敗、既に要求した終了を区別して返し、終了要求を設定画面への復帰に読み替えません。これにより、初期化の排他や保存中状態を保持したまま、新しいモーダル設定画面の終了を待つことを防ぎます。

LR2連携の通常起動が正常に完了し、その初期化で捕捉した `LR2RootPath` が空なら、初期化の排他とライブラリ表示更新の抑止を解放した後に起動継続可能な警告を一回表示します。警告は、LR2のスコアDBの場所を解決できずLR2のスコアDBを利用できないこと、LR2バックアップやLR2標準カスタムフォルダの検出で予期しない動作になり得ること、設定画面でLR2ディレクトリを設定すべきことを伝えます。OKで閉じた後も初期化成功を維持し、設定画面を自動では開きません。

この警告は `LR2RootPath` が空の通常起動ごとに表示し、抑止する永続設定は持ちません。単独動作、ルート設定済み、設定画面から保存後に呼び出す `ISettingsDialogStatePort.InitializeLibraryAsync` では表示しません。必須設定不備を `SettingsRequired` として扱う経路とは区別し、空のルートを別パスから推測して補いません。

ディレクトリ検査と設定全体の検証が成功してから出力検索ルートを修復します。修復とライブラリのプロファイル構成には、ディレクトリ検査で読み込んだ同じLR2設定を渡します。これらの処理のためにXMLを再読込みせず、検証前の保存も行いません。

利用不能なら設定修復・保存、単独動作DBの作成、構造修復、モデル構築、バックアップ、最適化、走査、後続処理へ進みません。初期化の排他とUI抑制を解除してから一回だけ警告します。通常の起動入口は `false`、設定保存側の入口は `StartupInitializationOutcome.SettingsRequired` を返します。設定画面への案内は上記の呼出元の分担に従います。欠落登録を自動で外さず、修正・再接続後のOKで新しい入力を確認して再試行できます。

モデル初期化と差分反映前にも同じ検査を使います。内側の型付き停止は外へ伝え、外側で解放後に通知します。この段階ではDB構造等が既に変わっていることがあるため、「副作用が全くない」とは案内しません。停止後に成功記録や成功後の処理を公開しません。

### 動作モード

| モード | 楽曲DBとルート | 外部連携 |
| --- | --- | --- |
| LR2連携 | 保存済みのLR2楽曲DBと設定XMLの検索先を使う | LR2のカスタムフォルダ、バックアップ、IR・順位更新を許可する |
| 単独動作 | アプリ内の `data/song.db` と複数の登録ルートを使う | LR2の実出力・XML変更・バックアップ・IRは行わない。明示設定がある場合だけbeatorajaのスコアを使う |

単独動作DBはAppDataではなくポータブルなアプリデータです。ディレクトリ、DB、ライブラリ、プレイリスト、BMSONと譜面情報の構造を順に作ります。LR2互換の構造と出力設定値は保持できますが、実際のLR2出力は行いません。

単独動作のルートは複数登録でき、存在するパスの正規化と大小文字を無視した重複排除を行います。利用者が登録した親子や用途の単位を勝手にまとめません。旧 `BMSRootPath` はルート一覧が空で実在する場合だけ初回移行に使います。新規導入先は登録ルートのいずれかを必須とします。

有効な実行中モードの切替はプロセス再起動です。設定の変更意図に対する確認・先着受付・最小設定の保存と終了は、設定と終了の仕様に従います。同じプロセスの再初期化でDB所有や連携先を差し替えません。まだ有効なモードが一度も成立していない初回・検証失敗時は、編集値を選んで保存した後に同じプロセスで初期化できます。

初回の言語選択は `InitialSetupLanguageDialog` で行い、次にメイン画面を所有元とする設定画面を開きます。一般の設定不備は通常の確認画面で案内します。必須設定が揃うまで初回走査を開始しません。LR2bodyのプレイヤー選択はライブラリのモードとは別で、単独動作でも使用できます。

### DB構造の確認と修復

`AppSchemaPreflightService.Inspect` は読み取り専用です。既存 `playlist_entry` のSHA-256対応やアプリ構造の版更新が必要なら警告します。完全な初回DBへアプリ用の構造を追加するだけの場合は互換性警告を出しません。補助表・索引の欠落や非互換は修復しますが、ハッシュ対応行の充足率は構造確認に含めません。

`EnsureAppOwnedSchema` / `RepairAppOwnedSchema` はアプリ所有の表・索引を揃え、`app_schema=1` を記録します。既存のMD5・SHA-256対応を保持します。`BmsLibraryDbGateway` の一つの外側トランザクションが全変更を所有し、借りた接続を使う参加処理は確定・取消をしません。途中失敗では全体を戻し、最初の例外を伝えます。

修復後は必ず再確認し、未収束なら起動失敗です。後の譜面情報読込みを構造修復の代わりにしません。構造修復のために楽曲全件や実ファイルからSHA-256を計算せず、実際に譜面を読む差分・導入・補完へ任せます。

### 補助メタデータの取込み

配布された譜面情報は起動時のDB読込み前に取り込みます。取込み済みのファイルは `imported_metadata/` の同名の管理キャッシュへ移します。`chart-info-metadata.7z` または `.db` をルートへ戻せば再取込みできます。再初期化と差分再読込みでは再取込みしません。

配布情報の `chart_info_schema_version` は輸出入形式の版であり、アプリDB構造の版とは別です。取込みをDB構造の修復や所持判定の代わりにしません。

### 目録とファイルの読込み

楽曲・BMSON・ハッシュ対応をDBから読み、スコアの取得元を選びます。LR2の取得元だけはプレイヤーID確定後にIRスコアの事前取得を開始できます。単独動作でbeatoraja無効ならスコア取得元はありません。

ファイル列挙は `EBridge_ScanChartAndResources` を使い、Everythingが利用できない場合だけ同じ契約のマネージド走査を使います。古いDLLやABIの不一致への互換代替は持ちません。ネイティブ橋渡しとC#は同じ成果物として配布します。

通常走査は音声・画像・動画の譜面相対キーと逆引き索引まで完成させます。未完成で導入可能とせず、保留バッチへ遅延構築を押し付けません。ネイティブ経路は復号済み配列から索引を作り、中間のリソース辞書を実体化しません。マネージド走査と検証用の結合経路だけが辞書を持ちます。

列挙した譜面時刻、テキスト、フォルダ時刻、LR2出力情報は[LR2同期仕様](../integration/lr2-song-db.md)の生産側契約へ従います。通常差分の意味とLR2派生行の修復を混ぜません。LR2の同期未完了だけを理由に通常差分を全件更新へ変えません。

### 導入とプレイリストの準備完了

`StartupInstallReadinessState` は `CatalogLoaded && DestinationResourceIndexReady && PendingPackagesRestored` で導入先推定を許可します。スコア、順位、譜面情報、保守、プレイリストの読込みはこの条件に加えません。明示的に起動走査を省略してリソース索引がなければ、推定は `resource_index_unavailable` として利用不能になり得ます。

保留復元は既存行のパスを正規化し、完全一致で先着を残します。大小文字だけの違いは別の保存識別で、ドットや末尾の表記差は正規化後の衝突として扱います。不正・欠落・譜面なしと重複の生行を既存トランザクションで削除し、残す正規行を書き込みます。パス以外の `delete_parent` 等は生存行から保ち、確定後に保留状態を公開します。

`StartupReadinessCoordinator` はプレイリストの必須読込みと外部・おすすめの取込みを管理します。準備中も取込み要求を順番に受け付けますが、通信、解析・保存、公開集合の変更は準備完了まで開始しません。呼出元を同期的に待たせず、準備後に一つの実行処理で順に扱います。

初期化失敗では準備待ちと受理済み要求を同じ失敗で終結させ、受付の再開や空の成功結果を公開しません。終了では未開始の受理結果を取消の通知より先に終結させ、実行中の要求は実行側の `finally` まで所有します。取消コールバックの失敗でも他のコールバックを試し、最初の失敗を保持します。遅れて戻る通信や初期化の継続からDB・公開状態を変更しません。

### 必須の処理と後続処理

起動時のスケジューラーは `startup_ready_operable` で開始します。モデルへ注入した受付を `StartupBackgroundTaskSchedulerOwner.Queue` に接続し、依存と分類ごとに実行します。

| 分類 | 主な処理 | 完了条件 |
| --- | --- | --- |
| 必須の読込み | プレイリスト項目、譜面情報 | `read_hydration` の並列数2 |
| 必須のスコア | メモリ上のスコア適用 | `default` の並列数1 |
| 必須の補完段階 | ハッシュ・譜面情報の補完、または不要判定 | 要求の確定・DB反映・公開まで待つ |
| 後続の表示・情報追加 | フォルダツリー、URL補完、参照反映、外部同期・一覧 | 通常は経路ごとに並列数1で必須処理とも重なって進める |
| 後続の保守・出力 | 保守読込み、未完の保守、出力修復、BMT出力、GC | スケジューラーの後続処理として記録する |
| 自動LR2同期 | 正常な必須初期化完了後に一回予約 | 専用状態で報告し、起動進捗の成功・失敗を変更しない |

`StartupBackgroundTasksDone` は必須処理の登録を閉じ、待機中・実行中の必須要求が0になったことを示します。スケジューラー全体の空とは異なります。

出力修復、導入可能譜面の保守、起動後GCは必須処理がなくなるまで開始せず、その実行中も新しい必須処理を始めません。この制約を他の後続処理へ広げません。出力修復が変更操作と競合した場合は、背景の処理では警告画面・待機・再試行・状態やファイルの変更をせず、その回を明示的な失敗またはスキップで終えます。次回起動では通常どおり現在の状態から判断します。

フォルダツリーの最終更新は独立し、遅くても操作可能・必須初期化を止めません。純粋なキャッシュの事前計算は、後続の登録と動的なLR2登録が閉じ、全処理が一度収束した後に一回登録します。その完了も含めて後続完了を記録します。

### 読込みと表示への反映

読み取り専用処理は接続を閉じてから所有状態・索引・画面へ反映し、内部で構造修復や暗黙の書込みをしません。SQLiteの結果判定、接続設定、保守の有効性は[データと索引](../core/data-and-indexes.md)に従います。

譜面情報は両モードで実際のDB行と現在の失敗記録を照合します。LR2同期の完了行は譜面情報の現存を証明しません。全対象が現在の値を持つと確認できた場合だけ補完を省きます。部分的な既存BMSの更新は譜面情報由来の9列に限定し、BMSONからLR2楽曲行を作りません。

情報追加後の表示は、現在の並べ替え・絞込み・列への依存で判断します。不要な全件再構築をせず、基本値の順序を捨てません。起動中の一部の表示更新は必須初期化後の `startup_presentation_flush` へ集約できます。通常の起動でリソース変更を全件再評価せず、明示再走査が選択譜面または所持全件を扱います。

### 再読込みの範囲

| 操作 | 行うこと | 行わないこと |
| --- | --- | --- |
| `FullReinitialize` | DBとファイルから目録を再構築し、必要な補完を行う | 実行中モードの切替、補助メタデータの再取込み |
| `ReloadFileDiff` | メモリ目録と新しい走査を比較し、追加・更新・削除、索引、参照を反映する | 楽曲DB全体の再読込み |
| `ReloadTables` | 見出し、項目、参照を読み直し、その後に外部同期を実行する | スコア・順位の読込み |
| `ScoreOnly` | スコアの取得元、スナップショット、適用と必要なLR2順位更新 | プレイリスト見出し・項目・外部同期の再読込み |

これらは同じセマフォで直列化します。既に操作可能な状態からの要求では、進捗をリセットしてもスケジューラーは実行可能に保ちます。検索ルートの追加・削除は保存後に実行中の検索先を同期して差分を再読込みします。外部のDB編集を取り込む場合は差分更新ではなく再初期化を使います。

差分0件なら差分由来のDB確定・譜面情報・保守計算を行いません。成功した走査が0件でも既存の保存譜面がある場合は削除の正本にせず、DB・LR2同期・メモリ置換を止めて設定と検索状態を確認する警告を出します。新規の空DBは空ライブラリとして許します。判定は `ScanSource` を使い、診断用のフラグから推測しません。

通常差分と手動差分は同じ[譜面読込み](../library/chart-file-reading.md)を使います。LR2フォルダの準備済み反映がある場合、進捗は中間値、終端値、差分完了の順に公開し、反映失敗で成功完了を進めません。詳細は[進捗](progress.md)に従います。

テーブル再読込みは自動対象を外部同期ONかつ絶対URIのものに絞ります。単体・選択範囲の手動再読込みは外部同期設定によらず選択を対象とし、並列数を制限した共通処理を使います。失敗は個別画面を乱発せず、ログと一覧の状態へ集約します。項目の反映コールバックまで完了してから外部同期へ進みます。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 設定保存後の初期化失敗と画面への復帰 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs)、[`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `ApplySettingsAsync_InitialDirectoryFailureReopensOnceAfterCleanupAndCanRetry`: 実際の初期化入口で排他・UI抑止の解放、警告と再表示の一回性、次の保存の受付を確認する。 |
| モード別の構築、検索先、作成・適用の失敗 | [`StartupLibraryInitializationWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/StartupLibraryInitializationWorkflowOwner.cs) | [`StartupLibraryProfileTests`](../../../BeMusicSeeker.Tests/StartupLibraryProfileTests.cs)、[`StartupLibraryFailureContractTests`](../../../BeMusicSeeker.Tests/StartupLibraryFailureContractTests.cs)、[`StartupLibraryInitializationWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/StartupLibraryInitializationWorkflowOwnerTests.cs) |
| LR2設定の未設定・読取不能・構造不正、その他の必須設定不備 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs)、[`LR2Config`](../../../BeMusicSeeker/Models/LR2/LR2Config.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `InitializeAsync_InvalidLr2SettingsUseSettingsGuidanceAndPreserveFiles`、`InitializeLibrary_SettingsValidationFailureRoutesGuidanceByCaller`: 初回・通常起動と設定保存後での設定案内の分担、UI抑止解除、保存パス・XML・DBの保持、保存・初期化の中止。 |
| LR2ディレクトリ未設定時の通常起動警告 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `InitializeLibrary_Lr2RootPathWarningIsLimitedToEmptyRootOnNormalStartup`: 通常起動成功後だけ警告し、設定保存後の再初期化では追加表示しないこと、設定画面を自動で開かず成功結果を維持することを確認する。 |
| ディレクトリ不通、外側の警告、設定後の再試行 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs)、[`FileDiffReloadWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/FileDiffReloadWorkflowOwnerTests.cs)、[`LibraryDirectoryWarningFormatterTests`](../../../BeMusicSeeker.Tests/LibraryDirectoryWarningFormatterTests.cs)、[`MainWindowTreePresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindowTreePresentationWpfTests.cs) |
| 必須・後続の依存、終結、導入可能条件 | [`StartupBackgroundTaskSchedulerOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/StartupBackgroundTaskSchedulerOwner.cs)、[`StartupInstallReadinessState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/StartupInstallReadinessState.cs) | [`StartupBackgroundTaskSchedulerOwnerTests`](../../../BeMusicSeeker.Tests/StartupBackgroundTaskSchedulerOwnerTests.cs)、[`StartupInstallReadinessStateTests`](../../../BeMusicSeeker.Tests/StartupInstallReadinessStateTests.cs)、[`StartupPostInitializationWarmupOwnerTests`](../../../BeMusicSeeker.Tests/StartupPostInitializationWarmupOwnerTests.cs) |
| 起動失敗と進捗の後片付け、表示の遅延 | [`StartupProgressWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/StartupProgressWorkflowOwner.cs) | [`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindowViewModelStartupProgressTests.cs) |
| アプリ所有DB構造のInspect、外側transaction、修復後の再確認 | [`AppSchemaPreflightService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/AppSchemaPreflightService.cs)、[`BmsLibraryDbGateway`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) | [`AppSchemaPreflightServiceTests`](../../../BeMusicSeeker.Tests/AppSchemaPreflightServiceTests.cs) の `EnsureAppOwnedSchema_FreshLr2DatabaseConvergesPreflightWithoutDigestMigration`、`RepairAppOwnedSchema_MissingVersionRowConvergesPreflight`、`RepairAppOwnedSchema_CurrentVersionWithInvalidDigestMapStillRepairsSchema`、`RepairAppOwnedSchema_LateFailureRetryConvergesOnSameDatabase`。Inspectの無変更、既存digest行の保持、途中失敗時の旧状態、再試行後の収束を実DBで確認する。`Inspect_PathOverload_DoesNotWaitForLr2SongDbExtendedMonitorLock` は共有Monitorの保持中に検査が完了することを確認し、解放待ちに依存しない起動前検査を保証する。 |
| スコアとIRの要求条件、事前取得 | [`BMSLibrary`](../../../BeMusicSeeker/Models/BMSLibrary.cs) | [`BmsLibraryIrStartupTests`](../../../BeMusicSeeker.Tests/BmsLibraryIrStartupTests.cs)、[`StartupRankingRefreshPolicyTests`](../../../BeMusicSeeker.Tests/StartupRankingRefreshPolicyTests.cs) |
| アプリ所有権と構成順序、構成・画面表示の失敗 | [`ApplicationStartupCompositionOwner`](../../../BeMusicSeeker/ApplicationStartupCompositionOwner.cs) | [`ApplicationStartupCompositionOwnerTests`](../../../BeMusicSeeker.Tests/ApplicationStartupCompositionOwnerTests.cs) |
| スコアだけの再読込み、失敗・取消後の受付解放 | [`ScoreOnlyReloadWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/ScoreOnlyReloadWorkflowOwner.cs) | [`ScoreOnlyReloadWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/ScoreOnlyReloadWorkflowOwnerTests.cs) |
| ディレクトリの事前検査、不通時の既存データ保持 | [`BMSLibrary`](../../../BeMusicSeeker/Models/BMSLibrary.cs)、[`LibraryDirectoryPreflightService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/LibraryDirectoryPreflightService.cs) | [`LibraryDirectoryPreflightTests`](../../../BeMusicSeeker.Tests/LibraryDirectoryPreflightTests.cs)、[`BmsLibraryDirectoryAvailabilityTests`](../../../BeMusicSeeker.Tests/BmsLibraryDirectoryAvailabilityTests.cs) |

## 関連資料

[進捗](progress.md)、[設定](settings.md)、[終了](shutdown.md)、[LR2同期](../integration/lr2-song-db.md)、[IRと順位](../integration/lr2-ranking.md)、[バックアップ](../integration/lr2-backup.md)、[プレイリストの保存と出力](../playlist/storage-and-export.md)を参照します。
