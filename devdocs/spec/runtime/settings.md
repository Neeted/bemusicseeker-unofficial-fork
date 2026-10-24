# 設定の編集と反映

## 目的と適用範囲

設定画面の項目保存、局所候補の確定・取消と、それぞれに必要な実処理を定めます。ファイルの保存と起動時の破損回復は[設定の永続化](settings-persistence.md)、外観は[画面とテーマ](../ui/appearance.md)を参照します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 起動時の設定診断

読取り可能な設定の正規化保存とバージョン記録の保存が失敗した場合は、同じ読込み済み値・文化圏・テーマで起動を継続します。保存警告のコールバックや任意の診断が例外を返しても、初期化結果へ加えず再通知しません。保存時の再読込み、旧設定の移行、その他の必須設定準備の失敗は保存警告へ格下げせず、後続の初期化へ進みません。元ファイルの保全と失敗分類は[設定の永続化](settings-persistence.md#起動時の回復)に従います。

### 編集状態と画面の所有者

確定値だけを `Settings` と実行時readerへ置きます。通常項目は有効な選択時、自由入力のEnter・フォーカス離脱、slider操作完了時に対象項目だけ保存し、成功後に公開します。同値は再保存せず、失敗では当該確定値・現在表示・次回入力を旧値に保ちます。別項目の確定値は戻しません。

音量は `PlaybackPanelViewModel` と再生設定ストアが操作中の値を所有し、現在の再生へ即時反映します。ドラッグ・マウス・キー操作の終了、フォーカス離脱、画面閉鎖で最終有効値だけを保存します。操作中の値は `Settings` と実行要求のreaderへ公開せず、別項目保存や終了時の通常保存にも混ぜません。同値の終端は保存せず、保存失敗では当該表示と現在再生を最新の確定音量へ戻します。主画面と設定画面は同じ再生担当を使います。

候補はBMS一覧、beatorajaの選択、BMT、音声出力、外部プレイヤー、外部画像、出力配置、LR2の詳細パスとrecordの各編集範囲に限ります。beatorajaの有効な選択、音声・再生方式・外部画像は、組が成立した入力イベントで当該組だけを自動確定します。未成立の組と保存失敗の入力はその欄に保持し、明示再操作で修復できます。開く・ページ初期接続・同値通知は保存要求も実処理も開始しません。他セクションの候補を読み込まず、確定時に最新の確定値で再検証します。

音声出力は候補と確定値に同じ正規化を使い、既定機器の空文字とnullを差分と扱いません。実際の自動保存失敗が未了の場合に、音声設定の保存再試行であることが分かるボタンと案内を表示します。保存成功または確定値への復帰で案内を解消します。機器テストの再実行とは区別します。

設定画面はメイン画面が所有する同じモーダルWindowを使います。ページは分類を初めて表示するときに生成し、分類を移動しても局所候補を保持します。設定Windowには進捗行を表示しません。局所操作と非同期の自動確定は、実入力イベントから実処理・cleanup・必須公開・通知まで同じWindowがTaskを追跡して待ちます。処理中の利用者Close・Esc・同じ再確定は拒否し、無関係な通常項目保存は継続できます。owner終了では表示失効と受理済みTask・native解放のjoinを区別します。

### 画面操作の失敗と結果通知

設定の実`Button`・`BrowseRequested`・ユーザー取消の入口は、未処理の通常操作失敗をその操作の終端で受け止め、元原因と経路を診断して結果通知を一回だけ試みます。下位が既に作った失敗結果記録のための再送出や重複通知は行いません。通知・診断先自身の失敗はその場所で診断を試みて終え、元結果へ加えず再通知しません。検証警告・スキーマ結果・局所操作の結果通知も同じ境界を使い、任意通知の失敗で保存済み事実を取り消しません。外部リンクの起動失敗もリンク操作の入口で扱います。

選択画面の利用者取消・閉鎖は、その選択による保存を行いません。モード・LR2の再起動確認を取り消した場合は保存済みの値へ戻します。確認表示の失敗は承認や利用者取消として扱わず、選択画面・削除等の後続を開始しません。非同期の局所操作は、処理、後片付け、必須公開と通知の終端まで追跡し、競合する操作と通常閉鎖を拒否します。破棄した画面を再有効化しません。

プレイリスト復元の終了可否は[保存・出力](../playlist/storage-and-export.md#json出力sqlバックアップと復元)の結果に従います。競合拒否・受付拒否・必須処理の失敗では設定画面とアプリを閉じません。終了が許可された場合だけ共通のアプリ終了処理へ渡し、任意の完了・終了案内の表示失敗で必要な閉鎖を妨げません。

### 分類と表示

分類は11個で、末尾は詳細設定、右クリックメニュー、このソフトについての順です。外枠と局所操作は既存の設定 ViewModel が接続し、音声の候補・照会・テスト表示は `AudioDeviceTestWorkflowOwner` が所有します。左分類はクライアント端、右本文のScrollViewerはペイン端まで配置し、本文の内側16 DIPを一回だけ適用します。見出し・下部操作の左右も16へ揃え、区切り線から各領域の内容を16離します。本文は幅に追従し、分類変更時に本文の縦スクロールを先頭へ戻します。採用する最小寸法でも入力と固定操作へ到達でき、長いラベル・状態文は折り返します。横スクロールや固定最大幅による余白を作りません。セクション・フィールド・関連操作の間隔は[共通の内容余白](../ui/appearance.md#内容余白と間隔)に従います。

「インストール」分類は次の6セクションをこの順で表示します。詳細設定は「メッセージ表示」「初期化」「LR2連携」「危険な操作」を持ちます。起動時の保留選択は詳細設定の「初期化」に配置します。

| セクション | 設定・表示順 |
| --- | --- |
| インストール先 | 確定済み一覧から選ぶ `BMSInstallDir`、BMS一覧の編集先へ移動する操作・検証表示。 |
| 新規フォルダの命名 | `FolderNameFormat`、`%ARTIST%` / `%TITLE%` の案内、`UseOnlyShiftJISChars`。命名書式と文字制限は自動リネームにも共通で使用する。 |
| 取り込み・新規インストール | `EnableAutoInstall`、`KeepInstallablePackagesPending`、`RequireCompleteUsedResourcesForAutoInstall`。 |
| 導入先の自動推定 | `AutoApplyAmbiguousInstallDestination`、`InstallDestinationAutoApplyMinAudioHealth`、`AutoInstallAfterAutomaticEstimation`。 |
| 上書き・元パッケージの整理 | `EnableSmartComponentOverwrite`、`KeepSmartOverwriteProtectedFilesByRenaming`、`DeletePendingPackageSourceAfterInstall`。 |
| 確認メッセージ | `ShowDiffBMSInstallConfirmMsg`、`ShowNewPackageInstallConfirmMsg`。 |

説明は各項目の直下で常時表示し、ラベル・説明とも本文幅で折り返します。取り込み後の自動推定に続く導入と、単一行のURL1/URL2セルクリックによる取得・導入は別設定として説明します。`EnableAutoInstall` は起動時スキャンも有効な場合の単一行セルクリックを対象とし、複数行URL取り込みや通常のファイル取り込み全体の可否を制御しません。上書き・整理の説明では、移動・上書き・削除が起こること、元の配布物を残すには事前コピーが必要なことを示します。スマート上書きの子設定は親がOFFでも値を消さず、編集を無効化しません。新規として導入する確認設定では、導入先が設定済みの場合に省略できないことを明記します。既存の保存キー・既定値・入力検証を維持し、項目保存と局所候補から実処理の確定値捕捉へ接続します。

全般ではLR2の場所、状態、手動同期、履歴DBの状態を区別します。音声設定は出力設定、音量、専用テスト、詳細な音声設定を別々のセクションにします。プレイリストのURL対応表とプリセット、バックアップ、危険な削除操作はそれぞれの目的が分かる見出しで示します。「このソフトについて」はバージョン、ライセンス、リンク、読み取り専用のリリースノートを扱います。リリースノート画面は設定画面を所有者とし、設定編集の状態を持ちません。文化圏変更の購読は表示中だけ保持します。

一般ページは言語、環境、BMSディレクトリ、LR2の手動同期、LR2プレイログ、beatoraja連携の順に配置します。LR2の個別パスとbeatorajaの派生パスは初期状態で閉じた詳細欄に置き、操作グループ間に余白を設けます。変更・確定は適用可能な局所差分がある場合に強調表示し、不要なときは無効・グレーにします。取消は無効な入力を含む取消可能な編集があるときだけ有効です。recordは選択中の一項目で判断し、他項目の変更では有効化しません。未完了作業の明示再試行とBMTの同値全表再更新は、差分がない場合も利用できます。

環境選択はスタンドアローンとLR2連携を等幅二列・同高のRadioButtonカードにし、名称一回と折り返す説明を表示します。通常設定と初期環境設定に共通で適用し、カード全表面・標準キー入力から既存の確認経路へ渡します。`IsEnvironmentSelectionEnabled` と一方向の選択表示、確認一回、取消・失敗時の保存済み選択への復元を保持し、選択表示を設定値への単純な双方向Bindingに置き換えません。

設定ページ内の意味上の重複見出しは明示的に整理し、言語・テーマ・音量・録音ファイル名・LR2プレイログの単純な囲みを除きます。BMS一覧、インストール先、二種のプレイリストURL、右クリックのWeb／プログラム一覧は入力名・一覧編集・検証の役割を保持して直下見出しだけを非表示にします。再生方式はラジオ入力の名称一回と各説明を残します。他の具体的な項目名、説明・検証状態と文言、ボタン・チェックボックスが示す操作・状態は保ち、分類や確定方式は変更しません。表示とアクセシビリティの詳細は[設定用の表示部品](../ui/appearance.md#設定用の表示部品)に従います。

インストール分類の説明付きチェックボックスには `SettingsCheckBoxOption` を使います。既存の標準CheckBoxと設定値への直接Bindingを内容に残し、説明と余白だけを共通化します。説明はラベル文字の左端に揃え、他の設定説明と同じテーマ対応の薄い文字色を使います。保存・取消・標準のキーボード操作は維持し、説明部品にフォーカス対象や設定状態を増やしません。

### 起動時のソートキャッシュ準備範囲

詳細設定の初期化設定で、通常ライブラリ一覧の起動後に事前生成するソートキャッシュの範囲を選びます。保存キーはユーザー設定の整数 `StartupSortCachePrewarmPriority`、キー欠落時の既定値は `1` です。

| 値 | 表示 | 対象優先度 | 現行定義での昇降順の合計 |
| --- | --- | --- | ---: |
| 0 | 準備しない | なし | 0 |
| 1 | 基本のみ（既定） | 1 | 8 |
| 2 | 標準 | 1～2 | 42 |
| 3 | すべて | 1～3 | 78 |

未準備の列も選択時に通常どおり整列・キャッシュします。対象列と順序は[一覧仕様](../ui/table-view.md)の列優先度を正本とし、件数を実装へ固定しません。変更は[次回起動](startup.md#起動後のソートキャッシュ準備)から適用し、この設定の保存だけで再初期化やキャッシュの削除・追加生成は開始しません。

選択完了で当該項目だけを保存し、成功後に選択値と表示へ反映します。保存失敗では当該項目の確定値を維持します。言語変更は選択値を維持して選択肢の表示名を更新します。共通の `SettingsField`、`ComboBox` と警告用 `SettingsStatusBanner` がテーマへ追従します。

保存済み値が0～3外なら補正せず無選択と警告を表示します。setterへのnull・範囲外入力では確定値を変更しません。有効値を明示選択したときにその項目だけを保存します。無関係な項目・局所操作の保存は妨げません。必須起動の検査はこの任意設定を条件に加えず、範囲外値の実行は起動後処理の失敗として扱います。

### 言語辞書と資源の代替

表示文字列は、指定カルチャのJSON、指定カルチャの埋込み資源、日本語の埋込み資源の順で解決します。利用者が編集した日本語JSONも優先します。UTF-8/BOM、任意キー、大小文字を区別する辞書と既存の `DataContractJsonSerializer` による受理を維持し、辞書の整合検査を実行時の追加拒否条件にはしません。

JSONの欠落・アクセス・読取り失敗、シリアライザーの入力失敗だけを空辞書へ変換して資源の代替へ進みます。内部の契約型故障や資源不足を入力不良・空の成功へ変換しません。`ConcurrentDictionary.GetOrAdd` の生成処理が完了しなければ、その辞書は登録しません。任意の診断が失敗しても正常に読めた辞書を失いません。埋込み資源の取得では資源自体の欠落だけを日本語の最終取得へ渡し、キー・型などの内部故障は伝えます。

候補探索は日本語を常に含み、正常辞書を破損・無効カルチャ・言語名なしのファイルと独立して扱います。完了した探索結果だけを共有スナップショットへ公開します。ディレクトリ欠落は日本語候補として完了し、既知のディレクトリ読取り・アクセス失敗は今回だけの日本語候補を返して探索済み状態を保存しません。予期しない故障や資源不足では未完了の候補を公開しません。

### LR2検索ルート候補の取得

登録内容の変更検出用取得は欠落した登録も保持し、実処理用取得は存在する登録だけを使います。それぞれの一回の取得で、現在・変更前の通常/追加/ROOT出力先の除外集合を一度だけ構築し、全ルートに適用します。候補が0件、または現在と対象の変更前ROOT出力先がどちらも空なら、プレイリスト全件スナップショットは取得しません。必要なROOT出力先計算には一回の取得結果を共有します。正規化、同一・子パス除外、親・兄弟パスの保持、登録の列挙順を維持します。別の取得には新しい入力を捕捉し、永続キャッシュは追加しません。

### 初回表示とページの寿命

設定画面は外枠と最初に表示する全般ページを作成し、残りのページは最初の選択時にUIスレッドで一度だけ生成します。同じウィンドウでは生成済みページを再利用し、未確定の`TextBox`入力も保持します。画面を閉じた後の全体ページキャッシュは持ちません。テーマ・言語を変更してから未訪問ページを開くと現在の資源と編集中の値を使います。未作成の外観ページを選択更新のためだけに生成しません。音声ページは生成時点の`PlaybackPanel`へ接続し、その後の変更は作成済みページだけへ渡します。

共通コントロールのスタイルは`App`が読み込む辞書を参照し、設定用辞書で重複読込みしません。設定固有の暗黙スタイルは各設定ページと外枠の局所スコープに保ちます。「このソフトについて」の文化圏購読は表示中だけ保持し、分類変更時の本文スクロール先頭復帰、保存済み外観の維持、閉鎖時の`DataContext`解除を維持します。

表示窓口で要求を受信した時点の時刻を捕捉し、最初の`OnContentRendered`入口までを `settings_dialog_first_render` の `requestToFirstRenderMs` へ記録します。モーダル画面が閉じるまでの時間は含めません。ウィンドウ構築は `settings_window_construct`、各ページの構築開始・終端は `settings_page_construct_start` / `settings_page_construct_end`、描画後のスキーマ表示更新は `settings_dialog_schema_presentation`、一覧のバックグラウンド処理実行時間は `settings_audio_device_list_worker` として区別します。既存`NLogWrapper`を使い、診断の失敗は編集・列挙・閉鎖の結果へ加えません。絶対時間の改善値は対象環境での測定によって判断します。

### 音声一覧の非同期取得

設定表示要求ではネイティブ機器を列挙しません。当該ウィンドウの初回音声表示時に、既存音声管理主体が一覧取得タスクを登録し、古い能力照会を失効・取消要求してその実タスク終端を非同期に待ってから、列挙だけをバックグラウンド処理で行います。WPF要素・バインディング・編集中の値の読み書きと通知はUI側で行います。プロパティ取得とコンストラクターから列挙を開始しません。既存列挙処理の初期化確認・共有操作リース取得・ネイティブ列挙・解放は同じバックグラウンド処理内で完結し、方式別列挙は逐次のままです。

初回訪問は表示が無効から有効へ遷移したときにリセットし、表示が有効で音声ページが可視の共通入口から開始します。可視通知と表示有効化の到着順に依存しません。同じウィンドウの再訪問では再列挙しません。開き直したウィンドウの初回音声表示は新しい更新機会ですが、前の取得が未完了なら同じタスクを再利用し、完了後の追加取得を予約しません。

取得中は状態を出力欄へ表示し、出力先の変更、他設定の編集・保存・適用・取消、分類切替、通常閉鎖を許可します。一覧の取得をテスト実行中として扱わず、明示音声テストだけは一覧タスクの実終端まで受付不可にします。能力照会も実終端まで保留します。完了時に画面が有効かつ音声ページが可視なら、その時点で編集中の値から候補と選択を作り直し、開始時の選択を復元しません。明示編集によるAutoへのリセット理由は保持します。非表示ページと閉じた画面へ取得結果を通知せず、後の音声再表示は取得済みカタログと編集中の値を使います。

未知の出力方式と未接続機器の識別子・名前を保持し、一時的なnull選択をAuto/Defaultへの編集として扱いません。下位カタログの方式別失敗と直近の正常一覧保持を維持します。外側の取得が失敗した場合も既存候補と現在選択を残し、ローカライズ済みの更新失敗状態を表示・診断します。正常な0件成功へ置き換えず、同じウィンドウの再訪問で成功表示へ変えません。取得中状態は実終端で解除し、明示テストと現在選択の能力照会は従来の受付条件へ戻します。失敗による恒久停止や自動再試行は追加せず、次のウィンドウの初回音声表示を次の更新機会とします。

表示終了では結果反映と後続照会を無効にしますが、非取消ネイティブ処理を強制解放しません。音声管理主体が一覧・能力照会・テストそれぞれの実完了タスクを保持し、`MainWindow`の終了では表示を先に失効させてから `AudioSettingsCompletionTask` を取得し、バックグラウンド処理待ちと方式間を含む一覧実終端、既存照会・テストの解放まで待って音声基盤の終了へ進みます。表示フラグの解除を処理終端とは見なしません。

### 音声設定の編集と照会

出力方式・機器・sample rate・format・buffer・WASAPIは音声担当の局所候補です。方式・機器変更によるAutoへのリセット、保存済み利用不能選択の表示、能力照会とnative寿命は[音声仕様](audio.md)に従います。有効な組の変更時に当該組だけを保存し、次の再生開始から使います。現在の再生Task・譜面・playerは維持します。機器一覧・能力照会の結果だけでは保存せず、未成立の入力は確定readerへ公開しません。

有効な選択・組の成立で音声出力だけを自動保存します。SRC品質とミキサー並列度は項目保存し、音量は現再生へ即時反映します。「再生を停止してテスト」は候補を試す非保存操作で、交渉された形式を保存値へ書き戻しません。

### 新規としてインストールの確認

詳細のメッセージ表示に `ShowNewPackageInstallConfirmMsg` を配置します。既定値は true で、旧設定にキーがない場合も確認を有効にします。変更時の項目保存と、保存成功後のプロパティ通知は他の確認設定と同じ経路を使います。false で省略するのは導入先未設定の保留パッケージの新規導入確認だけです。設定済みの導入先を使わない確認は常に残します。表示する確認は No を既定とし、未選択で閉じても新規導入を承認しません。

### 自動新規導入に要求するリソース

インストール分類の「取り込み・新規インストール」に `RequireCompleteUsedResourcesForAutoInstall` を配置します。既定はfalseで、旧設定にキーがなくてもOFFです。確定時に当該項目だけを保存し、チェックボックスの直下へON/OFF時の動作を説明します。

画面説明では、OFFは使用音源がそろえばBGA・動画・補助画像が不足していても自動新規導入の対象にすること、ONは全使用リソースを要求して不足時に保留へ追加することを別の行から示します。保留画面で不足を確認し、未制作のBGA・動画などがなくても問題ないと判断すれば「新規としてインストール」を実行できることも案内します。推定の詳細はこの説明へ混在させません。

| 使用音源 | 非音声の使用リソース | OFF | ON | 保留になった場合の新規リソース探索 |
| --- | --- | --- | --- | --- |
| 完全（要求0件を含む） | 完全 | 自動新規導入を許可 | 自動新規導入を許可 | 不要 |
| 完全（要求0件を含む） | 欠損または定義済み参照の照合不能 | 自動新規導入を許可 | 保留 | 省略。手動推定は可能 |
| 欠損または定義済み参照の照合不能 | 任意 | 保留 | 保留 | 実行。元の充足率が自動設定閾値以上でも100%未満なら省略しない |

「許可」はリソース条件だけを指します。既所持・単体・入れ子・利用者の保留維持設定・ライブラリ未準備・取消・失敗など、他の保留条件を解除しません。非音声には使用BGA・動画と、STAGEFILE・BANNER・BACKBMP等の補助画像の明示参照を含めます。BMSONに非空の不正な補助画像パスや親参照が明記されている場合も、入力を拒否せず既存の補助画像不足WARNINGを表示し、音源完全ならONで保留・新規リソース探索省略、OFFで自動新規導入を許可します。未指定・null・空文字は対象外です。OFFで非音声不足を許可してもWARNINGを保持します。

自動推定を開始するのは、未所持譜面を含み、同じ操作の初期分類で新規インストール対象ではない保留パッケージだけです。全既所持と、保留維持設定などで残った新規対象は、推定準備から省略して導入先を設定しません。手動推定はどちらも実行できます。準備前の選別と所持分割は[自動推定](../library/install-estimation.md#自動推定と並列処理)に従います。

自動推定の対象で完全音源の場合に省略するのは、リソースから新たに候補を探す処理だけです。混在パッケージで既所持ハッシュの候補が分かる場合は、一意先のStrong判定と同点の限定候補評価を残します。未所持群だけを評価し、Strongと各譜面の音源許可条件が成立すれば導入先を設定します。Strong不成立では自動設定せず、候補表示は共通の関連性ポリシーに従い、別候補へ迂回しません。未定義IDは必要集合へ含めず、分類・警告・推定可否へ影響させません。

初期分類、保留追加、後続推定は同じ操作で捕捉した設定と取得済みのリソースを使います。途中で設定を再取得して分類を変えません。この設定は自動新規導入と保留後の自動推定の要否を変えるもので、[導入先自動確定の音源条件](../library/install-estimation.md#確信度と画面への適用)を緩めません。

### 導入先を自動設定するときの音源充足率

インストール分類の「導入先の自動推定」に `InstallDestinationAutoApplyMinAudioHealth` を配置し、90～100%を1%刻みで選択します。既定100%、旧設定にキーがない場合も100%です。設定した値は操作開始時の `BmsLibraryOptionsSnapshot` に捕捉し、途中の設定変更で兄弟パッケージの判定を変えません。

設定は各対象譜面の音源充足率へ適用し、候補順位、推定開始、新規インストールの分類・許可へ流用しません。欠損を許可しても一般の不足警告は残ることを画面の説明に明記します。確認不能な音源は許容せず、保存済みの範囲外値は診断付きで自動設定を拒否します。範囲外値を黙って90%等へ丸めず、画面で利用者が有効な値へ修正できます。値の確定時に当該項目だけを保存します。詳細な許可条件は[導入先推定](../library/install-estimation.md#確信度と画面への適用)を正本とします。

### 取り込み後の自動推定に続く導入

インストール分類の「導入先の自動推定」に、説明付きチェックボックス「取り込み後の自動推定で導入先が確定したら、自動インストールする」を置きます。ユーザー設定 `AutoInstallAfterAutomaticEstimation` はbool、既定falseです。新規ファイル・旧設定のキー欠損のいずれも無効とし、更新だけで有効にしません。

有効時は、保留への登録に続く自動推定が正常完了し、今回導入先が自動確定した対象を確認画面なしで導入します。説明文には、手動で開始した推定は対象外で、上書きと元パッケージ削除は既存設定に従うことを示します。対象・失敗・取消の正本は[推定仕様](../library/install-estimation.md#取り込み後の自動推定に続く導入)です。ダウンロード後の取り込みを制御する `AutoInstall` / `EnableAutoInstall` とは別の設定です。

当該項目の保存と変更通知へ接続し、保存するだけで推定・導入・スキャンや再起動を開始しません。取り込み入口の設定スナップショットを終端まで使い、処理中の編集は実行中の取り込みに混入させません。次の取り込みはその入口の設定を捕捉します。

### 新規判定の保留と複数候補の説明

`KeepInstallablePackagesPending` は、新規と判定されたパッケージを導入前に保留画面で確認したい場合の設定として説明します。譜面ハッシュが変わった既所持作品の導入先確認と、難易度表パッケージなどを別フォルダに分けたくない用途を、別々の行に示します。自動の導入先推定は行わず、必要ならパッケージを選択して「インストール」→「インストール先を推定」を実行することを案内します。

`AutoApplyAmbiguousInstallDestination` は、同じ構成のBMS作品が複数フォルダにあるなど候補が同率で残る場合の設定として説明します。ONは自動確定条件を満たす第1候補を設定し、OFFは候補を確認して選択します。一意に決まる場合はこの設定によらず自動確定することを案内します。譜面数の補助判断を全リソース探索の優先条件として説明しません。判断条件の正本は[導入先推定](../library/install-estimation.md#メタデータの強一致と同順位の解決)です。

### LR2の場所と設定XML

`LR2RootPath`、`LR2SongDBPath`、`LR2ConfigXmlPath` は既存形式の個別キーで、モードと整合した組を保存します。通常の起動で子パスをルートから再生成しません。保存された設定XMLのパスが欠落・読取不能・不正な場合も、別設定の保存や画面の再表示を理由に生のパスを消しません。パス文字列、解析済み設定、表示用の状態を分けます。

LR2連携モードで `LR2RootPath` が空の既存設定は非推奨ですが、互換性のためそれだけでは保存を拒否しません。一般ページのLR2ディレクトリ欄へ `Warning` と理由を表示し、値を設定するよう促します。この警告は参照操作で不正な候補を選んだ一時的な選択エラーとは別に扱い、ルートを設定した場合、または単独動作モードでは表示しません。ルートから子パスを推測する新しいフォールバックは追加しません。

正本未成立時と未採用の別パスの入力検証は `Lr2ConfigSession.TryResolve` を入口とします。`LR2Config` の構築には `config/jukebox` 要素が必要で、XMLとして正常でもこの構造がないファイルは無効です。登録が0件の空の `jukebox` は許容し、`system` 等の任意要素を読込み時に追加しません。登録ルートが現在存在するかどうかは別の検査で判断します。無効な入力では解析結果を公開せず、生の保存パスやファイルは変更しません。起動時の案内と処理順は[起動仕様](startup.md#登録ディレクトリの検査)に従います。

`LR2Config` の直接読込みや検索ルート登録で生じる例外の詳細も、画面へ渡る文言は表示リソースを使います。構造エラーは `Error_InvalidLR2ConfigStructure`、ファイル名の案内は `config.xml` / `config.xmh` の許容条件に従います。ウィンドウ寸法は正値、音量は0以上100以下という入力条件と文言を一致させます。通常の設定検証では既存の設定不備の案内を維持します。辞書の検査は[全件共通検査](../development/test-authoring.md#表示リソースの検査)に集約します。

標準配置はルート配下の `LR2files/Database/song.db` と `LR2files/Config/config.xml` または `config.xmh` です。未採用のルート選択は候補と設定XMLを検証し、そのルートから解決した3パスで反映処理へ進みます。同じ採用済みルートの再選択では、個別指定のXMLもその識別を維持し、DBの標準配置への復帰とは分けます。モード選択は保存済みのLR2パスを使い、詳細欄の未確定パスを混ぜません。有効profileがある場合は保存前に再起動を確認し、承認後に必要な受付と選択保存から通常の終了・後継起動へ進みます。取消は保存済みの値へ戻します。無効な今回の選択やpicker取消で、以前の有効候補を確定しません。

同じ正規化済みLR2ルートを選び直した場合は、構成が採用済みのXML識別と正本を使い、更新日時でXML候補を選び直しません。同値の組なら再保存・再起動しません。`song.db` がまだなくても標準パスを候補にできますが、保存時の必要条件は別途満たす必要があります。無効なルートや設定XMLでは保存済みの組と解析結果を保ちます。保存成功後に詳細候補と依存表示を確定値へ揃えます。

一般ページの初期閉状態の詳細Expanderでは `song.db` と `config.xml` / `config.xmh` の現在値を読み取り専用で表示し、それぞれの「参照」から個別に選択できます。個別参照の前には、通常はLR2ディレクトリから設定される標準配置を使うよう注意を表示し、利用者が同意した場合だけファイル選択へ進みます。選択したファイルだけを検証・変更し、もう一方のパスは変更しません。無効な候補では現在の有効な値と解析結果を保持します。

個別参照は自動保存せず、両パスを同じ詳細候補で編集できます。詳細欄内の「環境を確定（稼働中は再起動）」で一組を一回保存し、稼働中は一回の再起動へまとめます。詳細の取消は保存済みの組へ戻します。この組にbeatorajaの候補を含めません。

設定XMLの個別参照で同じ正規化済みパスを選び直した場合は、構成が保持する所有者を再利用します。未採用の別パスだけを新しい入力として読み込みます。環境の確定後は新しい環境から履歴DBの対象と依存する表示を更新します。候補の選択だけでは履歴schema操作の対象を変えません。

状態表示の由来は操作履歴ではなく現在の有効なパスから求めます。現在の `song.db` / 設定XMLがLR2ルート由来の標準配置と一致すればLR2ディレクトリからの自動設定として表示し、それ以外は個別設定として表示します。個別参照で標準パスそのものを選び直した場合も標準配置として扱い、別の「カスタム」状態は保存しません。
同じ `song.db` を選び直した場合もファイルの存在を確認し、パス文字列が変わらなくても検出状態を通知します。表示中の言語変更では、ファイルの再選択や設定画面の開き直しを要求せず、現在の設定元に対応する状態文言を更新します。

変更検出用のBMS検索ルート取得では、現在存在しないパスも比較対象に残します。実処理用の有効ルート取得とは分け、取得処理から設定XMLを保存しません。LR2プレビューが一時変更する項目の保存・復元は[外部起動](../ui/external-launch.md)を参照します。

BMS差分再読込み・スコア再読込み・BMTの保存後実処理が未完了の場合は、そのセクション内にも保存済み・未完了・明示再試行を表示します。別の設定保存では再試行せず、局所取消・再表示でも未完了の事実を消しません。音声・画像・再生方式・recordの同期確定はL/Pを消費しないため、BMT等の処理中も受け付けます。音声テスト中と環境開始・再起動中の制約は維持します。

LR2連携でバックアップを有効化した場合は、当該項目の保存成功直後に次回起動からの適用を案内します。案内の失敗で保存済み値を戻さず、スコア等の別の局所確定や再試行で同じ案内を重ねません。

### LR2設定の所有・候補・公開

`ApplicationComposition` の `Lr2ConfigSession` が採用先を保持し、`Lr2ConfigOwner` がそのXML全文の正本を一つ所有します。初回採用後の外部XML変更・破損は参照・再表示・取消・同じパスの選択・保存前検証へ取り込みません。次の実際の書込みは、採用した全文と本アプリで成立した変更から行います。同内容の確定に外部修復のための再保存を加えません。新しいプロセスはその時点のファイルを読み込みます。

画面のBMS編集候補は両モード共通のユーザー一覧で、管理登録を含めません。確定一覧は所有者の小さい変更不能な公開入力から投影します。別の出力保存は未確定のユーザー追加・削除を保存・破棄しません。出力を公開する直前の確定ユーザー一覧と編集候補を操作内で捕捉し、出力への採用で確定一覧が変わる場合だけ、新しい確定一覧へ候補の追加・削除を引き継ぎます。ユーザー一覧が変わらなければ候補を作り直さず、残る項目の選択も保ちます。採用済みの旧出力を次のBMS確定でユーザー登録へ復活させません。検索・導入・親フォルダcacheは登録と分類用出力配置を一緒に捕捉します。

保存は短いXML排他内で正本コピーに今回の変更を適用し、実保存成功後に通常文書を採用します。ディレクトリ準備・移行・DB・UI・プロセスの待機は排他外です。必要媒体の成立前は公開入力を変更せず、成立後は今回の登録差分・配置だけを公開します。例えばBMSのXMLだけA→Bに成功して公開Aの間にROOT登録Gを削除すると、正本BからGを除き、公開AからGだけを除きます。BMSの明示再試行は成功済みXMLを読み直したり保存し直したりせず、残る媒体成立後にBを公開し、Gを復活させません。

正本はconfig・jukeboxの未知属性・未知子、継続するpathの未知属性を保持します。削除要求の対象path以外を一覧置換の都合で消しません。byte単位の書式保持は保証しません。

### beatorajaの局所反映

beatorajaのroot・選択player・派生するscore.dbとBMT出力先を一つの局所組として扱います。rootを選び直すと、その構成のplayer名が一覧にあれば採用し、なければ名前順の先頭、一覧が空なら空値に揃えます。新rootの一覧から別playerを選べます。派生パスは詳細欄へ表示し、表示した有効な組を選択操作から自動保存します。

スコア使用がONなら、rootと解決済みscore.dbを初期化と同じ条件で検証します。不正なら組を未適用のまま保持し、スコアを自動で無効化しません。有効playerの選択で組が成立すれば反映できます。スコアを使わない場合は、その非使用DBの不備を拒否条件に加えません。

保存後、スコア使用時は既存のスコアのみ再読込みと履歴キャッシュ失効・必要表示を待ちます。BMTは保存済みオプションに従って旧管理出力・旧管理URLを整理し、新先の必要全表出力とURL同期を待ちます。未確定のBMTオプションは混ぜず、管理外データを保ちます。モード・LR2パスの保存やアプリ再起動を伴いません。Table URL取込みは確定rootを使います。

保存前の失敗は確定値を変更しません。保存後の必要処理が失敗した場合は、保存済み事実と必要な旧新入力・未了処理を当該担当の寿命で保持し、同セクションの明示再試行で修復します。再表示や別設定の保存で暗黙に再開せず、旧先を最新rootへ読み替えません。修復まで関連変更を制限し、無関係な通常項目は保存できます。

### 取消と閉じる操作

下部の通常操作は「閉じる」です。局所の取消は当該セクションの最新確定値へ入力を戻し、保存済みの部分成功は取り消しません。

処理外のClose・Esc・Alt+F4は候補だけを破棄します。通常項目の保存済み値は保持し、再表示は確定値から作ります。未了保存・実処理の再試行情報はWindowより長い当該操作担当の寿命で保持します。

### 保存の受付と処理選択

通常項目保存にL/Pを一律要求しません。実処理を伴う局所操作は[操作の競合ポリシー](../core/operation-concurrency-policy.md)の該当受付を使い、Busyは保存・後続処理0で候補を保ちます。出力配置の未受理候補は、確定配置または受理済み未完了要求の配置・改名対応へ戻します。

| 確定単位 | 保存後の処理 |
| --- | --- |
| BMS一覧と、必要な場合だけ明示選択した代替導入先 | 最終一覧を一回保存し、runtime反映・必要な差分再読込みを一回。変更なしは保存・差分0。LR2 XMLの正本コピーへユーザールートだけを変更し管理出力rootを保持する。 |
| スコア取得元とプレイヤー | 有効な選択時に保存しスコアだけを再読込みする。保存後失敗は値を保ち、同値の明示再試行で保存を増やさない。FileDiff・BMT・All・再起動を合成しない。 |
| BMT有効・保持・URL登録・hash方式 | 旧新出力先とoption組を捕捉し、既存BMT担当で実出力・旧先整理・URL反映を待つ。同値の明示再更新も全表をforce投影し、同値は再保存しない。DB元hashと管理外ファイルを保つ。 |
| 音声backend・device・rate・format・buffer・WASAPI | 有効な組だけを保存し、現再生Task・譜面・playerは維持して次回開始から使う。品質・並列度は独立項目、音量は現再生へ即時反映する。device test自体は追加保存せず、交渉結果を書き戻さない。 |
| 再生方式と必要exe、外部画像の有効とパス | 有効な組の成立時にそれぞれの関連組だけ自動確定する。方式は現player・曲・Taskを維持して次回の新規開始で置換し、その方式の形式能力で選曲する。操作能力と表示面は実置換に伴って更新し、画像は現表示へ反映する。LR2bodyPathは確定環境から導出する。 |
| 右クリックaction、履歴プリセット | 一recordを確定し他recordの入力を混ぜない。削除・並替・既定値復帰は明示操作として保存する。右クリックactionの保存順は、未確定行を含む画面順から確定IDだけを投影し、新しい行の確定時にも表示位置を反映する。他行の未確定内容は保存しない。既存JSONを維持する。 |
| 出力配置と追加先名 | 通常・ROOTはフォルダ選択と既存移行確認、追加先は追加・削除・改名から操作対象だけ確定する。取消は副作用0。失敗時だけ明示再試行を表示する。必要な保存、実移行、登録改名、検索rootのディスク保存、公開と通知まで追跡する。BMS候補を混ぜない。 |
| beatoraja root・player・派生path | 有効な選択で整合組を保存し、必要なスコアのみ再読込み・旧新BMT処理・表示を待つ。再起動しない。 |
| mode・LR2基礎path | モード・ルート選択または詳細欄の明示確定から、その組だけを保存し既存の再起動受付・Close・join・後継起動へ進む。 |

有効な環境がない初回・修復では同じ局所保存を使い、ライブラリ依存処理は開始時に使います。初回・修復のLR2では通常・ルート出力先を一つずつ保存でき、未設定・欠落した他区分はその保存を妨げません。未変更の要修復先を作り直さず、表に依存するROOT登録は初期化時に揃えます。選択した出力先と設定済みの配置の保護条件は検査し、初期化開始時には両方の必須出力先を検査します。環境保存で再起動せず、「この環境で開始」が確定済み必須値で一回だけ初期化し、必要全表BMT・必須UI/host公開を待って初回完了とCloseへ進みます。設定不足、処理失敗、終了要求を既存型で区別します。

通常起動の準備開始から必須UI・host公開の実終端までは、設定からの重複開始、モード・LR2・beatoraja組の反映を受け付けません。閲覧と無関係な普通の項目保存は可能です。開始ボタンは既存のライブラリ操作状態と設定操作状態に従って無効化し、連続クリックも同じ受付で拒否します。有効profileの成功公開で開始表示を更新します。初回・設定不備案内では、起動抑止と受付を解放済みなら、元の案内Taskが未完了でも同じ設定画面から開始できます。

LR2 XMLとuser.configは別々に保存するため、一方だけが保存済みになる場合があります。必要な保存が全部成功するまで稼働consumerへ混成組を公開せず、成功済み媒体・要求・未了段階を当該操作担当が保持します。同内容再試行は成功媒体を再保存しません。関連変更だけを修復まで拒否し、無関係な通常項目は保存できます。Window再表示後も事実を保ちます。この段階記録はアプリの実行中だけ保持し、次回起動は保存済み媒体の設定検証と修復に従います。

部分保存後にフォルダ移動・削除等で前提を失った場合は、同じセクションで候補を修正して再確定できます。必要なら成功媒体も修正保存します。表headerの確定直後に移行対象をreceiptへ保持し、新先投影・DB同期が失敗しても、変更前の登録名による再検索へ依存しません。成立した物理・DB出力は旧先整理失敗で戻さず、明示再試行は捕捉済み対象・旧新絶対path・型・登録対応の未了部分を扱います。

新先投影・DB同期の成立先と、旧先の物理整理済み対象・未了DB行整理範囲を区別して保持します。候補修正時は受理した配置で未了投影を再準備し、他の移行ですでに成立した同じ表・出力先を再投影しません。途中で部分生成された先も、現出力を保護したうえで旧先整理へ引き継ぎます。設定操作が投影後の整理を一括で待ち、旧DB行整理だけが未了なら成功投影・物理整理を繰り返しません。別設定保存や日常出力では暗黙再開しません。

BMTの保存後未完了は、保存前の旧出力先と受理した新先・option組を保持します。外部configの後続変更や未確定候補で要求を置き換えず、同セクションの明示再試行で修復してから次の変更を確定できます。再試行の入口は現在configの新規確定用検証に依存させず、必要なURL同期の読取り・書込みが失敗した場合は未完了を保持します。旧先を整理する場合は台帳を消す前に旧管理URLを除き、無効化と保持ONの組では旧ファイル・台帳・URLを残します。

### 反映範囲の型とスナップショット

各局所入口が必要な実処理へ直接渡します。`BmsLibraryOptionsSnapshot`、`InstallDestinationWorkflowSettingsSnapshot`、`CustomFolderOutputSettingsSnapshot`、`StartupSettingsSnapshot`、`BeatorajaBmtOptionsSnapshot` は確定値を操作開始時に捕捉します。開始済み操作は途中で設定を読み直さず、次の明示操作だけが新しい確定値を使います。

### カスタムフォルダと検索対象

基点の変更・追加先の削除・改名は、表の変更後の実効絶対パス集合を[配置条件](../playlist/lr2-custom-folders.md#出力先と事前検査)で検査してから設定・DB・LR2 XMLへ保存します。追加先の削除で既定先に集まる場合も同一・包含を拒否します。`SettingDialogEditCompletionTests.OutputConfirmation_RejectsOverlapAfterAdditionalBaseRemovalBeforeSaving` が実port・各保存媒体・正本とファイルの無変更を確認します。


LR2で変更前に有効な `BMSInstallDir` が、今回の通常・追加・ROOT出力先と同一またはその配下になる変更は、候補採用時と局所確定の最終配置検査で拒否します。別の確定BMSディレクトリへ先に導入先を変更し、出力先を選び直すよう案内します。比較は既存のパス正規化を使い、大小文字・末尾区切りを同一視し、`BMS` と `BMS2` を区別します。保存・XML・表登録・実配置へ副作用を与えず、代替先を自動選択しません。初回や既存の不正な導入先を理由に無関係な段階保存を拒否せず、導入先以外のユーザールートは引き続き採用できます。

出力の未受理候補は確定配置へ戻し、既存の導入先欄から修正・再操作できます。修復中は受理済みTarget・改名対応へ復元します。修復候補のTarget・改名・成功済み段階は、L/P受付と表登録検証の成功前に更新しません。受理後の保存・実処理失敗は従来どおり未完了として保持します。

カスタムフォルダ出力先は本アプリの管理領域です。通常出力先だけは従来互換のため楽曲検索に残し、追加通常出力先とルートフォルダ出力先は楽曲検索から除きます。いずれも一般ページのBMSディレクトリ一覧、導入候補、フォルダツリーには混ぜません。楽曲検索の対象とLR2の `jukebox` 登録は区別し、出力に必要な登録は維持します。

出力基点同士の同一・親子関係は禁止します。既存の `jukebox` 登録に対しては、次の配置だけを許可します。比較には表示用一覧で除外する前の登録を使い、現在存在しないパスも残して正規化したパス関係を検証します。

| 指定する出力先と既存登録の関係 | 通常出力先・追加通常出力先 | ルートフォルダ出力先 |
| --- | --- | --- |
| 同一 | 許可 | 許可 |
| 出力先が既存登録の親 | 禁止 | 配下の登録を復元・整理するため許可 |
| 出力先が既存登録の子 | 禁止 | 禁止 |
| 同一でも親子でもない | 許可 | 許可 |

保存済みの追加・ルート出力先を通常出力先へ転用する操作は、編集後に追加・ルート側を変更または削除していても、同一・親子関係を拒否します。登録同士の親子重複も設定不備として検出し、通常・追加出力先のために親登録を外して子へ置換する自動補正は行いません。ルート型の基点・旧子登録を現在の表ディレクトリへ揃える同期は[出力仕様](../playlist/lr2-custom-folders.md#jukeboxとの同期)に従います。

既存登録を新たに出力管理領域として採用する場合は、保存時に管理領域とファイル整理の注意を確認します。通常出力先の同一登録への復元では楽曲検索を維持し、追加・ルート出力先として採用する場所は検索対象から外れることを併記します。保存済みの通常出力先を変更する場合も、その旧パスが検索対象から外れることを同じ確認へまとめます。旧通常出力先を追加・ルート出力先へ転用する場合は、既管理領域を理由に検索除外の確認を省略しません。

出力の確認は保存済み設定と今回のフォルダ選択・追加・削除・改名が作る対象だけから作り、操作の保存前に表示します。同値の選択では確認・保存・実処理を始めません。警告の比較元には確定済みreaderと所有者が保持するBMS登録を使い、別セクションの未確定候補を混ぜません。検証エラーは確認より先に拒否します。確認の取消では未受理の出力候補を復元し、保存・同期・ファイル変更へ進みません。正常保存後に同じ役割・パスを継続する場合は再確認せず、警告済みの永続状態も持ちません。BMSディレクトリ追加は登録済みルートや出力領域との同一・親子関係を拒否し、出力に必要な登録の削除は保護します。出力先の変更は対応する出力設定から行います。

### アプリデータの撤去

アプリデータ撤去はsong.dbに実生成したアプリ拡張表・索引・トリガーと `playlist_custom_folder_output_status` を対象とします。LR2本来の表と索引、未知の利用者の表・索引・トリガー・`sqlite_sequence`行、譜面ファイルは保持します。アプリ表に属する`sqlite_sequence`行は撤去し、`score.db`の履歴撤去は別操作として維持します。既存の対象一覧を明示的に使い、接頭辞推測や全表削除へ広げません。

確認後は通常Shell終了要求を同じUI Dispatcherで先着判定し、新規L/P受付を閉じます。要求元は受理だけを待ち、L/Pと実設定操作Taskを解放します。通常終了の取消・通信・保存・再生・後片付けの実終端後に捕捉したDBパスを撤去し、設定Windowの寿命から独立した窓口で成否を通知して終了します。撤去失敗、通知例外、通知不能でも停止済みownerの再開・再初期化は行いません。

### 履歴DBの状態と計測

履歴DBの状態表示はキャッシュを読み、設定画面を開くたびにDBを照会しません。スコア読込み、対象プロファイルの履歴読込み、明示的な導入・修復・削除に伴う確認結果で更新します。対象を切り替えた場合は「未確認」に戻し、前のプロファイルの導入済み状態を使いません。明示操作は変更前後に状態を確認し、成功後に履歴の読込みキャッシュも無効にします。

画面表示、取消、検証、局所保存、必要処理の診断を分けます。メッセージを利用者が閉じるまでの時間を内部処理の性能値と混ぜません。診断の失敗で操作の成否を変更しません。

BMSディレクトリの追加・削除・複数選択・D&Dは設定画面内の候補だけを編集し、局所確定で最終一覧を保存します。稼働中のモード・LR2組は確認・先着受付後に保存し、既存の終了・後継起動へ進みます。Busy・保存失敗では副作用を始めず、Busy解消・再表示で自動再試行しません。再起動確認の取消では保存済みの値へ戻します。

### 保存済み出力配置と内部継続

保存済みの整合した出力配置を構成から公開し、通常出力はその配置を使います。局所移行の未了段階は設定担当が要求と成立済み事実を保持し、同じセクションの明示操作だけで再試行します。外側受付を解放した後の必要通知まで操作Taskで待ち、通知完了を受付解放と混同しません。

## 実装とテストの対応

| 保証 | 実装 | 検証 |
| --- | --- | --- |
| 出力への有効な導入先の取込み拒否と再操作 | `SettingsDialogViewModel.ValidateOutputBasePreservesInstallDirectory`、`CommitOutputSettingsAsync`、`RestoreUnacceptedOutputCandidates` | `SettingDialogCustomFolderOutputBaseTests.OutputBase_ProtectsConfirmedInstallDirectory` は通常・追加・ROOTの同一/包含親と大小文字・末尾・BMS2、元から未登録・欠落した導入先の互換性を確認する。`SettingDialogEditCompletionTests.OutputConfirmation_ProtectsInstallDirectoryUntilAnotherConfirmedRootIsSelected` は実user.config/XML/DB/配置の副作用0と案内、別導入先確定後の実移行・初期化検査を確認する。`OutputConfirmation_RepairsPartialMediaAndCleanupWithoutRepeatingSuccessfulProjection` は拒否修復後も受理済み宛先・改名対応を保つ。`OutputConfirmation_RejectedRepairPreservesAcceptedPlacement` はBusy・実登録検証拒否で受理済み要求を保持し、後続の実修復を完了する。初回段階保存は既存compiled picker系列、ユーザールート採用は既存通常/ROOT採用系列、翻訳キーは `LocalizationResourceParityTests` が分担する。 |
| LR2の初回・未採用入力の検査 | [`Lr2ConfigSession.TryResolve`](../../../BeMusicSeeker/Models/LR2/Lr2ConfigOwner.cs) | [`LR2ConfigTests`](../../../BeMusicSeeker.Tests/Lr2/LR2ConfigTests.cs) は未指定・不正パス、不完全XML・必須構造欠落の拒否と元ファイル保持、xml/xmh・欠落登録・空jukeboxの受理と参照時の非変更を確認します。採用後の外部変更非採用は所有者の局所検証が分担します。 |
| BMS候補・正本・公開入力の分離、同じ環境の再選択 | [`設定の局所確定`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[`Lr2ConfigOwner`](../../../BeMusicSeeker/Models/LR2/Lr2ConfigOwner.cs) | [`LR2設定の実接続`](../../../BeMusicSeeker.Tests/Settings/SettingDialogLr2OwnerTests.cs) が出力変更後のユーザー候補保持、ユーザー登録Uを出力へ採用した後のU→B→BMS確定での非復活と候補差分・選択・取消、確定一覧が不変の出力保存での候補非更新、部分保存と実表削除、外部XML・mtime非採用を確認します。実保存の未知XML保持・保存失敗は[`所有者の局所検証`](../../../BeMusicSeeker.Tests/Lr2/Lr2ConfigOwnerTests.cs) が分担します。 |
| 本文端と余白の一回適用、分類・見出し・操作の固定 | `SettingsWindow.xaml`、`SettingsControls.xaml`、11設定ページ | `SettingsWindowPresentationTests.SettingsWindow_SidebarReachesNativeClientEdgeWhilePageKeepsInnerSpacing` は左分類・本文端と内容inset、`SettingsWindow_AllPagesFitActualMinimumWidthViewport` は11ページの横方向到達性、`AudioSettingsPage_LongDeviceTestStatusWrapsAtMinimumWindowWidthAndKeepsFullAutomationName` は採用最小幅の長文折返しを確認する。`SettingsControlPresentationTests.SettingsWindow_NavigationAutomationSelectionSynchronizesPageAndResetsScroll` は本文移動中の見出し・フッター固定と分類変更時の先頭復帰を分担する。 |
| 環境カードの入力・確認・取消と表示 | [`GeneralSettingsPage`](../../../BeMusicSeeker/Views/Settings/Pages/GeneralSettingsPage.xaml)、[`SettingsControls`](../../../BeMusicSeeker/Views/Settings/SettingsControls.xaml) | `SettingDialogEditCompletionTests.OperationModeRadio_*` は両方向の選択状態と明示Clickから確認一回・保存一回、取消時の選択復元と無保存を確認する。標準RadioButtonのキー接続は型・イベント接続を静的確認し、実OSのSpace・Tab入力は実機推奨へ分担する。`SettingsWindowPresentationTests.SettingsWindow_EnvironmentCardsFitAndKeepWholeSurfaceInputAcrossLanguages` は通常／初期設定の最小幅・全6言語・全表面hit-test・テーマ・選択不可を担う。 |
| 重複見出しの整理と名前・説明・検証の保持 | [`SettingsField`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs)、設定の各ページ | `SettingsWindowPresentationTests.SettingsPages_HeadingsAppearOnceAndEditorsKeepLocalizedAutomationNames` は実ページの単一見出しと入力名、`SettingsField_HiddenHeaderPreservesMeaningDescriptionAndValidationWithoutEmptySpace` は非表示時の領域・説明・状態・文言を確認する。既存の名前所有、検証配送、全ページ最小幅のケースを併用する。 |
| 項目保存・成功後公開・画面外保存・未知キー | [`Settings.Commit`](../../../BeMusicSeeker/Properties/Settings.cs)、[`PortableSettingsProvider`](../../../BeMusicSeeker/Properties/PortableSettingsProvider.cs) | [`PortableSettingsPersistenceTests`](../../../BeMusicSeeker.Tests/Settings/PortableSettingsPersistenceTests.cs) の専用providerとfresh reader。候補非漏出と保存失敗は [`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) の実XML・標準composition・次回gatewayで確認する。 |
| 入力確定・カテゴリ・再表示 | [`SettingsWindow`](../../../BeMusicSeeker/Views/Settings/SettingsWindow.Confirmation.cs)、既存compiled各ページ | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogCategoryPersistenceTests.cs) の実BAML選択・Enter・離脱・slider終端から実保存、reader、再表示。ページ寿命とnative制約は [`SettingsWindowCompiledBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowCompiledBehaviorTests.cs)、[`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs) が分担する。 |
| 音量preview・操作終端保存・失敗復帰 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`PlaybackSettingsStore`](../../../BeMusicSeeker/Models/Playback/PlaybackSettingsStore.cs)、[`SettingsWindow`](../../../BeMusicSeeker/Views/Settings/SettingsWindow.Confirmation.cs) | [`SettingDialogEditCompletionTests.SettingDialogVolumeBinding_UsesComposedPlaybackOwner`](../../../BeMusicSeeker.Tests/Settings/SettingDialogEditCompletionTests.cs) は実compiled Sliderからruntimeへの即時反映、別保存中の非漏出、drag/mouse/key/closeの項目保存、同値終端の再保存0と実保存失敗時の復帰を確認する。dragの失敗行は保存60の成功後のpreview70を失敗させ、初期値50でなく最新確定60へ表示・現在再生・readerを戻す。[`PlaybackPanelViewModelTests.PlaybackPanelView_VolumeMenuCommitsOnMouseCompletionAndShutdown`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) は主画面の実Popup・binding・操作終端と、終了開始時の実保存を確認する。 |
| BMS最終一覧・代替・二媒体修復 | [`SettingsDialogViewModel.CommitBmsRootsAsync`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) | [`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) のStandalone/LR2、実媒体失敗、候補修正・再表示と未了だけの再試行。実差分の終端は [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogEditCompletionTests.cs) の `BmsConfirmation_CompletesRealFileDiffBeforeClosing` で確認する。 |
| score・BMT・初回の必要実終端 | [局所確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) と既存起動・出力担当 | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogEditCompletionTests.cs) の実score DB、同rootのBMT出力先A→B→C・現在先の再設定ごとの設定・台帳・全表・旧先整理・URL終端と管理外保全、二表途中失敗と捕捉済み先への同値全表修復・現config不正時の再試行入口と失敗保持・無効保持、初回typed結果。下位の形式・所有権は [`BmtTableExportServiceTests`](../../../BeMusicSeeker.Tests/Playlist/BmtTableExportServiceTests.cs)、初期化の実DBと必要出力は [`StartupRequiredInitializationTests`](../../../BeMusicSeeker.Tests/Startup/StartupRequiredInitializationTests.cs) が分担する。 |
| 音声候補・現再生維持・次回入力・test非保存 | [`AudioDeviceTestWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Settings/AudioDeviceTestWorkflowOwner.Presentation.cs)、[`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`SettingsDialogBehaviorTests.AudioConfirmationKeepsCurrentPlaybackAndPublishesOnlyNextOutputRequest`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) の実XML・gatewayと現Task/曲/player。方式交換は [`PlaybackPanelViewModelTests.ConfirmedPlayerFactoryIsUsedOnlyAtNextPlaybackStart`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)、機器・能力・native寿命は既存audio open/list/test系列が分担する。 |
| 有効な選択の自動保存、現画像と保存失敗・同値・別項目保存 | [局所保存](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[画像表示](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | `SettingDialogEditCompletionTests.CompiledImageSelection_AutomaticallySavesAndUpdatesCurrentArtworkOrKeepsFailure`は実compiled選択、fresh XML、実PlaybackPanel画像の画素、実保存失敗と明示再試行を確認する。`SettingsDialogBehaviorTests.AudioConfirmationKeepsCurrentPlaybackAndPublishesOnlyNextOutputRequest`は実再生Taskと次回音声readerを、`SettingsDialogBehaviorTests.CompiledPlayerSelection_AutomaticallySavesAndDefersFactoryUntilNextPlayback`は実player選択から現再生の維持、次回factoryの捕捉済み設定、実置換時の操作能力・表示面のBinding更新を分担する。 |
| 出力選択と既存確認からの保存・取消 | [設定Window](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs) | `SettingDialogEditCompletionTests.CompiledOutputSelection_CommitsOnlySelectedPlacementAndKeepsCancelledMedia`は通常・ROOT・追加先の実compiled入口とpicker/確認取消、Busyで保持した候補から無効な選択を行った場合の暗黙再試行0、実二媒体を確認する。 |
| LR2詳細の局所差分・検証・受付と履歴対象 | [LR2反映](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[General](../../../BeMusicSeeker/Views/Settings/Pages/GeneralSettingsPage.xaml) | [`SettingDialogEnvironmentConfirmationTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogEnvironmentConfirmationTests.cs) は詳細の無差分・有効差分・無効差分・L/P競合・取消、モードとルートの自動反映、個別パスの一括確定を分担する。`Lr2EnvironmentConfirmation_RefreshesHistoryTargetOnlyAfterSaving`は初回root・修復XMLの候補と保存失敗で旧履歴対象を維持し、確定後の実schema操作が新DBだけへ届くことを確認する。 |
| 出力配置・実移行・明示再試行 | [局所確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) と既存playlist移行担当 | [`SettingDialogEditCompletionTests.OutputConfirmation_RepairsPartialMediaAndCleanupWithoutRepeatingSuccessfulProjection`](../../../BeMusicSeeker.Tests/Settings/SettingDialogOutputConfirmationTests.cs) は実FS/DB・XMLで通常先の同値修復/旧先再利用、追加先改名後の修正/再表示を確認する。`OutputConfirmation_RepairsHeaderCommittedMigrationAfterProjectionFailure`は追加先削除・改名のheader確定後の物理投影/DB同期失敗、明示修復と未了先の候補修正、成立先の非再投影を確認する。[`CompiledInitialLr2OutputPickers_SaveEachRequiredPlacementBeforeAllowingInitialization`](../../../BeMusicSeeker.Tests/Settings/SettingDialogCategoryPersistenceTests.cs) は初回の両空値と両旧先欠落から実picker・確認・二媒体保存を両順で行い、未変更の要修復先へアクセスしないこと・開始必須検査・reader整合を確認する。登録と保護条件は [`SettingDialogCustomFolderOutputBaseTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogCustomFolderOutputBaseTests.cs)、下位の補償と新先保持は [`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) が分担する。 |
| beatorajaの表示組・保存前検証・score-onlyと旧新BMT修復 | [局所反映](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[`GeneralSettingsPage`](../../../BeMusicSeeker/Views/Settings/Pages/GeneralSettingsPage.xaml) | [`SettingDialogEnvironmentConfirmationTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogEnvironmentConfirmationTests.cs) は新rootのplayer解決・一覧・派生パス、実provider保存とscore-only、実旧新BMT・管理URL・管理外保全、保存後失敗と明示修復を分担する。 |
| モード・LR2の先着保存・通常終了・後継起動 | [`ShellShutdownWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) と[LR2反映](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) | [`ShellShutdownWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/MainWindow/ShellShutdownWorkflowOwnerTests.cs) の先着受付・保存失敗、[`MainWindowViewHostTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewHostTests.cs) の実Windowのplayer解放と後継起動接続を維持する。 |
| 起動中の環境操作拒否・開始入力とowner終了時のjoin | [設定入口](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs)、[`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`SettingDialogEnvironmentConfirmationTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogEnvironmentConfirmationTests.cs)、[`SettingDialogOperationShutdownTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogOperationShutdownTests.cs)、[`MainWindowViewModelStartupProgressTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) は事前準備・必須UI待ち、案内中の明示開始、実開始の連続入力・成功公開・失敗修復、参照消去後の操作Task回収を分担する。 |
| 独立局所確定と受理Taskの寿命、保存後の再試行表示 | [`SettingsWindow.Confirmation`](../../../BeMusicSeeker/Views/Settings/SettingsWindow.Confirmation.cs)、[`SettingsDialogViewModel.Confirmation`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) | [`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs) の `BmtButtonWhileOutputIsPending_AllowsIndependentImageConfirmationAndPreservesOwnerJoin` は実P内の待機中の画像保存・Close拒否・表示失効とTask joinを、`SectionProcessingFailure_ShowsRetryAfterReopenAndClearsOnlyAfterExplicitSuccess` はBMS/Score/BMTの実providerとボタン・再表示・局所banner・同値再試行を確認する。実モデル反映・全表投影の内容は既存EditCompletion系列と分担する。 |
| record保存・既存JSON | [右クリックrecord編集](../../../BeMusicSeeker/ViewModels/Settings/RightClickActionSettingsEditor.Confirmation.cs)、[履歴preset](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | [`SettingsDialogBehaviorTests.RightClickRecordConfirmationPreservesOtherSavedRecords`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) の他record候補非混入と保存済JSON。[`RightClickRecordOrderProjectsDisplayedPositionsWithoutSavingOtherCandidates`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) はWeb/Programの未確定行を含む移動・新行確定位置・削除と実保存失敗時の維持を確認する。各recordの入力・互換性は既存右クリック・履歴系列が分担する。 |

## 関連資料

[設定の永続化](settings-persistence.md)、[起動と再読込み](startup.md)、[音声](audio.md)、[画面テスト](../development/testing.md)を参照します。
