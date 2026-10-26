# 譜面からの外部起動

## 目的と適用範囲

右クリック操作からWebページと外部プログラムを解決・起動する仕様です。保存する定義、単一譜面の条件、クリック時の再検証、失敗表示と、譜面ビューア登録時のファイル送信を定めます。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 保存形式と既定値

`Settings.RightClickActionsJson` は `webActions` と `programActions` をまとめた利用者設定です。配列順をメニュー順とし、スキーマ版は持ちません。設定値そのものに次の6件を既定値として持ち、新規設定と `既定値へ戻す` で使用します。保存済みの利用者設定へ後から追加された既定項目を自動マージしません。

| 固定ID | 名前 | URLテンプレート | 対象 |
| --- | --- | --- | --- |
| `bms-ir` | BMS-IR | `https://bms-ir.org/new/song?songmd5={md5}&view=both` | BMS |
| `mocha` | Mocha | `https://mocha-repository.info/song.php?sha256={sha256}` | 全形式 |
| `minir` | MinIR | `https://www.gaftalk.com/minir/#/viewer/song/{sha256}/0` | 全形式 |
| `rianir` | rianIR | `https://rianir.link/ranking?sha256={sha256}` | 全形式 |
| `stellaverse-ir` | STELLAVERSE IR | `https://ir.stellabms.xyz/charts/{md5}` | 全形式 |
| `kaleid-ir` | Kaleid IR | `https://kaleidir.com/charts/{sha256}` | 全形式 |

null、空文字、不正JSON、未知・重複項目、重複ID、不正な列挙値や設定値は型付きの解析失敗として返します。起動時に例外で停止させず、既定値へ黙って戻しません。明示的に修正・初期化するまで操作を公開せず、設定エラーを案内します。

Web・プログラムの名前は日時に見える場合もJSONの文字列として原文を保存・復元します。直列化時の自己検査でも日時へ自動変換しません。

### 編集と検証

設定画面は独立した編集用の値を持ちます。入力、並べ替え、実行ファイル選択は永続設定を変えません。保存で全体を一回検証・直列化し、既存の設定保存処理へ渡します。取消と通常の閉じる操作は編集値を破棄します。

既定に戻す操作は常に使用でき、確認を挟まず編集値だけを置換します。保存成功まで永続化しません。不正な保存値に対する案内は、編集値を有効にするまで残し、内部解析の生の文言は画面へ出しません。

Web操作は固定ID、空白でない名前、URLテンプレート、有効設定、対象譜面形式を持ちます。既定項目の名前も通常の設定データであり、サイト名を言語リソースから補完しません。`追加` は空の名前とURLテンプレートを持つ編集行を選択状態で作成し、両入力欄を直ちにエラー表示にします。保存は必要項目を入力するまで拒否します。Webテンプレートは絶対HTTP(S) URLで、大小文字を区別する `{md5}` または `{sha256}` を一つ以上含む必要があります。未知・閉じていない置換記号は拒否します。ハッシュは規定長の16進だけを小文字へ置換し、必要なハッシュがなければその操作を公開しません。

プログラムは固定ID、空白でない名前、絶対実行パス、有効設定、`{filePath}` を含む引数テンプレートを持ちます。保存する項目は `id`、`name`、`executablePath`、`argumentTemplate`、`enabled` の五つです。`追加` は名前と実行パスが空、引数が `{filePath}` の行を選択状態で作り、名前と実行パスの入力欄を直ちにエラー表示にします。実行パスは参照ボタンから選び、名前が空白の場合だけ実行ファイル名（拡張子なし）で補完します。

プログラムの入力欄と保存時の検証は同じ規則を使います。名前の空白、実行パスの未指定・非絶対パス、引数の空白・`{filePath}` の欠落・未知または不均衡な置換記号・不均衡な二重引用符を拒否し、該当欄へ原因に応じた翻訳済みの説明を表示します。入力を修正すると欄のエラーを更新し、言語を変更すると入力値を保って説明を更新します。無効にした行も保存時の検証対象です。保存時に不正なプログラム行がある場合は、配列順で最初の不正行を選択して保存を拒否し、保存済みの値と編集内容を保持します。

引数はWindowsの二重引用符とバックスラッシュ規則で先に分解し、その後に置換します。引用中の二重引用符対は引用状態を保ったまま一文字の `"` にし、`--label "A""B" "{filePath}"` のラベルは `A"B` として渡します。引用外の空の引用区間は文字を増やさず、空の二重引用引数は一つの空引数として保持します。引用符直前のバックスラッシュは二本ごとに一本とし、奇数本の残り一本は引用符を通常文字にします。偶数本の後は、通常の引用符と同じく引用状態と引用符対を解釈します。単一引用符は通常文字です。未閉鎖の引用は拒否し、置換したパスは再分解しません。`{filePath}` 以外の波括弧を通常文字として記述する構文は設けません。実行ファイル名の特別な構文は対象外です。存在確認は定義解析時ではなくクリック時に行います。

### 単一譜面の解決

`RightClickActionResolutionInput` は、一行のMD5、SHA-256、ローカルの絶対譜面パス、判定できる場合の種類を持つ変更不能な入力です。複数選択や異種の集約を渡しません。解決は有効な定義の順を維持し、Web操作を種類と必要ハッシュで絞り、種類を判定できない入力では対象が全形式のWeb操作だけを返します。プログラムはローカル絶対パスがある場合だけ返します。URL、相対パス、履歴のハッシュからプログラム用パスを推測しません。

通常一覧、保留・導入済みのパッケージ行、未所持のプレイリスト行、履歴行もこの解決入力の契約を使います。一覧の行からローカルパスを渡す条件は、ファイルを開く権限があり、譜面ファイルが存在することです。保留・導入済みのBMS/bmsonも所持状態に依存せず、有効なプログラム設定とローカル絶対パスがあれば「プログラムから開く」を表示します。未所持のプレイリスト行にはファイルを開く権限を与えず、行にパスがあってもプログラム操作へ渡しません。ハッシュだけの行、パス欠落、ファイル欠落もプログラム操作の対象外です。

ハッシュだけの履歴はWeb操作だけを表示できます。LR2の未解決履歴はBMSとして扱い、BMS/bmsonをハッシュだけでは区別できないbeatorajaの未解決履歴は種類を未確定として扱います。ローカル譜面へ解決できた履歴には、既存の関連付け起動の直後にプログラムメニューを置きます。解決処理そのものはファイル、プロセス、ブラウザ、ダイアログに触れません。

### メニューとクリック時の起動

関連文書のクリックは、解決済みの正確なパスを再確認して一回シェルへ渡します。実起動の失敗は所有するメイン画面の入口で元原因を診断し、一回通知して止めます。クリップボード書込みも同じ局所終端を持ち、表示文字列やコピー対象を変えません。リリースノートと更新案内のリンクも起動失敗を一回案内し、更新案内は実起動に成功した場合だけ肯定結果で閉じます。通知自身の失敗は再起動や再通知を起こしません。

メニューを開くときに現在の設定を解決します。子項目には捕捉した行やURLではなく固定IDと操作の種類を保持します。クリック時はメニューの正確な対象行と現在の譜面情報を取り直し、現在の設定で再解決してから起動境界へ渡します。複数選択中でも別の選択行で置き換えません。古い操作、設定変更、不正設定、譜面・実行ファイルの欠落は起動せず、多言語のエラーにします。

Webは既存のブラウザ・シェル境界へ渡します。プログラムはクリック時に実行ファイルと譜面の絶対パスを確認し、`UseShellExecute=false`、作業ディレクトリは実行ファイルの親、引数は一つずつ `ArgumentList` に設定します。一回開始して待たず、プロセスを保持しません。標準入出力の転送、生の `Arguments`、関連付け起動への代替は使いません。

### 譜面ビューア登録時のファイル送信

譜面ビューアへの登録は、`AppScoreViewerRegistrationGateway` から `AppHttpClient.PostFile` を呼び、`HttpClient` で登録先へ `multipart/form-data` のPOSTを送ります。ファイル部分のフィールド名は `file`、`filename` は譜面の元のファイル名とします。日本語・アクセント付き文字・補助平面文字などの非ASCII名も、MIME encoded-word / Base64や代替名へ変換せず、UTF-8の生バイトでヘッダーへ出力します。ASCII名とファイル本体のバイト列も保持します。

要求の `Accept` は `application/json` とし、応答はUTF-8で読み取って先頭のBOMだけを除きます。非成功HTTP応答は既存の失敗ログへ応答本文を含めて例外を返し、応答JSONの解釈、登録の成功・拒否・例外、確認と結果通知は登録ワークフローが扱います。

### 一覧再生の外部プレイヤー所有範囲

再生後のメイン画面復元と表フォーカスは任意表示です。Dispatcher予約と実コールバックの故障は局所診断で止め、再生結果を変更しません。既存の画面生存・終了保護処理、前面表示条件、有限回数の確認だけを使い、故障後の再試行や高頻度の同期診断を追加しません。

一覧からの再生は「プログラムから開く」とは別の契約です。停止、次曲、終了通知、一時設定の復元に必要なため再生プレイヤーのプロセスを保持しますが、外部アプリのウィンドウ状態を継続的な正常性判定には使いません。プロセスを開始できない場合、起動直後に終了した場合、またはプレイヤー操作に必要なメインウィンドウを有限時間内に確立できない場合を起動失敗として扱います。

ウィンドウのforeground復元や装飾など、再生そのものに必須でないデスクトップ統合はbest-effortです。OSや外部アプリが要求状態を維持しなくても、開始済みのプレイヤーを起動失敗へ変更したり終了したりしません。`PlayStart` が失敗した場合、共通UI層はBeMusicSeeker側の再生状態と失敗通知を更新しますが、その例外だけを理由に `CloseProcess` を呼びません。開始途中のプロセスを片付ける必要があるプレイヤーは、自身の起動契約に基づいて処理します。明示的な停止、プレイヤー置換、アプリ終了では従来どおり所有中プロセスを終了します。

### BMIIDXView2015とuBMplayのINI保全

既存INIの読取りと一時値の編集、同一ディレクトリへのstaging書込み、原子的公開に成功してから、開始前の同名viewer置換と今回process起動へ進みます。別途起動された同名viewerも従来どおり終了して置換しますが、INI準備失敗時には終了させません。既存INIが読取り不能な場合や同名ディレクトリの場合を不在として扱わず、旧バイト列を保全してパスと原因を既存の再生失敗経路へ返します。初回失敗で不完全な正式INIを公開しません。

BMIIDXView2015はINI不在時に新規生成せず、そのまま起動できます。uBMplayは不足節・キーを補完して生成し、補完の保存に成功した場合はそのINIを残します。Shift_JISの日本語、LF/CRLFと末尾改行の有無、無関係な値・コメントを保ち、音量は0～100へ制限します。

uBMplayは所有mainの終了確認と終了イベントの配送を区別します。次の明示開始が終了済みmainを先に検出した場合も、イベント経路と同じ整理処理で購読・参照・window handleを外してから新しいmainを開始します。HasExitedによる同期通知の再入は捕捉process参照を再確認して一度だけ整理し、配送が遅れた旧snapshotは新しいmainや現在callbackを解除しません。最外の開始・停止・終了eventだけが要求内の通知結果scopeを所有し、同期再入は借用します。scopeへの一時参照はlock内finallyで解除し、確定したcallback snapshotを外側lock解放後のfinallyで順に配送します。各通知・診断故障は残る配送や元の例外を変更しません。復元結果はbackupごとに同じ要求内で共有し、同じbackupの失敗を再試行・重複包装せず、次の明示操作に引き継ぎます。旧backup復元後に準備した新backupの起動失敗cleanupは省略しません。

uBMplayは必要節・キーが元から全て揃う場合に元バイト列を保持し、所有するmain/request両processの終了後に原子的に復元します。process生存中の保留と実復元失敗を区別し、復元失敗ではbackupを保持して新規起動を阻止します。次の明示停止・開始で再試行し、成功後だけbackupの所有を解除します。失敗時の後片付けは今回所有したprocessだけを扱います。

自然終了時も復元後に一回だけ現在要求のcallbackへ通知します。復元失敗は `PlaybackExitEventArgs` でpanelの世代付き終了観測へ渡し、一度通知して自動次曲を止めます。同期再入で同じ故障を最外の開始・停止要求へ返す場合は、その終了引数に呼出元側の通知所有を保持します。終了観測は状態解除・処理済み判定・通知を先取りせず、実際に故障を返す側が一度通知します。開始失敗では開始側が現観測を終端します。それ以外の古い世代の故障は現在曲の表示を消さず、故障自体は通知します。失敗通知の末端で元例外とINI pathを局所診断へ残し、診断故障は通知や再生結果を変更しません。callback・通知自身の故障は局所診断へ止め、processイベントから未処理例外を返しません。外部開始Taskをprocess寿命まで延長しません。

### LR2試聴時の設定保全

LR2bodyの試聴が一時変更するXML項目は、`system/windowsize_x`、`system/windowsize_y`、`system/screenmode`、`sound/volumemaster`、`sound/volumeflag` の五つです。構成が保持する正本のコピーへ五項目だけを重ね、原子的に一時公開してからプロセスを開始します。外部XMLを読み直しません。

試聴中の読取りconsumerと設定候補は通常値だけを使います。初回復元が成功するまでの通常保存は、通常文書を正本へ採用し、実ファイルにだけ試聴五項目を維持します。初回復元成功で一時値の重ね書きを終了し、スコープ自体は終了cleanupまで保持します。五項目の旧値を別途退避せず、現在正本から復元するため元の欠落要素・親も再現します。

LR2の通常window styleは、従来どおり `GWL_STYLE` が試聴用の値へ収束するまで有限時間だけ設定を試みます。収束した場合だけ `GWL_EXSTYLE` を一度設定し、extended styleのreadback一致は成功条件にしません。通常styleが時間内に収束しなくても、メインウィンドウを確立したLR2が生存していれば再生を継続します。foreground復元も一度だけ試みるbest-effort操作です。

ウィンドウ設定後と終了時は、同じ所有者の現在正本を出力し、後から保存した登録と通常値・欠落を保ちます。復元失敗ではprocess終了後もスコープを保持し、既存のStop・Close・次の明示開始で復元を再試行します。未了スコープを新しい試聴で覆いません。開始前の失敗では `HasExited`、終了要求、強制終了を呼ばず参照と購読だけを片付けます。プロセス開始後でもメインウィンドウを確立できない起動失敗では開始途中のプロセスを終了対象にします。メインウィンドウ確立後の補助的なウィンドウ操作や設定復元の失敗だけを理由に再生中のLR2を終了せず、明示的な終了再試行に必要な所有状態を保持します。開始・復元の失敗は既存のプレイヤー失敗通知へ返し、主失敗と後片付けの失敗が重なった場合は対象設定パスと副次失敗を保持します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 既定値、厳密な解析、順序・種類・ハッシュの解決 | [`RightClickActionSettingsStore`](../../../BeMusicSeeker/Models/ExternalActions/RightClickActionSettingsStore.cs) | [`RightClickActionSettingsStoreTests`](../../../BeMusicSeeker.Tests/ExternalActions/RightClickActionSettingsStoreTests.cs)、[`ApplicationSettingsMetadataTests`](../../../BeMusicSeeker.Tests/Settings/ApplicationSettingsMetadataTests.cs) |
| 日時風の操作名の文字列保持 | `RightClickActionSettingsStore` | `RightClickActionSettingsStoreTests.SerializeRoundTripPreservesOrderAndDefinitions` は原文・JSON型と既存の順序・定義、`StoreLoadsAndSavesThroughTheProvidedSettingsOwnerWithoutFallback` は実保存と新providerの読戻し、不正保存後の元JSON・媒体保持を確認する。 |
| Windowsの引数分解と正確な引数列 | [`ExternalProgramArgumentTemplate`](../../../BeMusicSeeker/Models/ExternalActions/ExternalProgramArgumentTemplate.cs) | [`ExternalProgramArgumentTemplateTests`](../../../BeMusicSeeker.Tests/ExternalActions/ExternalProgramArgumentTemplateTests.cs) は引用内の単一・連続ペア、引用外の空区間、空引数、偶数バックスラッシュ後のペアを置換前後の独立した引数列で確認する。既存の奇偶、単一引用符、埋込・反復置換、未知名・不均衡の拒否も維持する。 |
| 追加直後・編集時の欄別検証、保存拒否と不正行の選択、言語切替、既定への復帰、取消、保存と閉じる操作 | [`RightClickActionSettingsEditor`](../../../BeMusicSeeker/ViewModels/Settings/RightClickActionSettingsEditor.cs) | [`RightClickActionSettingsEditorTests`](../../../BeMusicSeeker.Tests/ExternalActions/RightClickActionSettingsEditorTests.cs)、[`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs)、[`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs)、[`SettingsWindowCompiledBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowCompiledBehaviorTests.cs) |
| 所持状態に依存しないファイル条件、クリック時の設定再解決とプロセス起動条件 | [`SelectedChartExternalActionWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartExternalActionWorkflowOwner.cs)、[`ExternalProgramLaunchGateway`](../../../BeMusicSeeker/Models/Utils/Processes/ExternalProgramLaunchGateway.cs) | [`SelectedChartExternalActionWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/ChartOperations/SelectedChartExternalActionWorkflowOwnerTests.cs) の `Execute_ConfiguredProgramRechecksActionAndPassesResolvedTokens` は通常所持BMSと保留BMS/bmsonの空白を含むパスと引用符を含むラベルが解決・起動要求まで保持されること、設定削除後の起動拒否を確認する。`ResolveConfiguredProgram_RejectsUnavailableFileTargetsWithoutLaunching` は未所持プレイリスト・ハッシュのみ・保留ファイル欠落・開く権限なしを確認する。実プロセス起動条件は [`ExternalProgramLaunchGatewayTests`](../../../BeMusicSeeker.Tests/Processes/ExternalProgramLaunchGatewayTests.cs) が担う。 |
| 単一行のメニュー、保留BMS/bmsonの対象取り直し、履歴・未所持の区別 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowSelectedChartContextMenuWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowSelectedChartContextMenuWpfTests.cs) の `CompiledPendingProgramMenuLaunchesCurrentRightClickedChartWithMultipleSelection` は実際の入力生成・設定解決・引数展開へ接続し、複数選択中の右クリック行の親子メニューと、メニュー表示後に更新された正確なパスでの起動要求を確認する。既存の同クラスが通常メニューの接続を、[`MainWindowPlayHistoryWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlayHistoryWpfTests.cs) が履歴との区別を担う。 |
| 型付きの起動失敗メッセージ、多言語リソースの整合 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowContextMenuResourceTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowContextMenuResourceTests.cs)、[`LocalizationResourceParityTests`](../../../BeMusicSeeker.Tests/Localization/LocalizationResourceParityTests.cs) |
| 譜面ビューア登録のUTF-8ファイル名、ファイル本体、応答BOM | [`AppHttpClient.PostFile`](../../../BeMusicSeeker/Ribbit/Net/AppHttpClient.cs) | [`AppHttpClientTests`](../../../BeMusicSeeker.Tests/Runtime/AppHttpClientTests.cs) の `PostFile_PreservesUtf8FileNameAndBytesAndStripsResponseBom` は、非ASCII名 `譜面-é😀.bms` とASCII名 `score.bms` の実ファイルを送信し、loopbackで受信したPOST、multipartのフィールド名・ファイル名の生バイト、バイナリ本文の一致、応答BOMだけの除去を確認する。 |
| 譜面ビューア登録の応答解釈、成功・拒否・例外、確認と結果通知 | [`ScoreViewerRegistration`](../../../BeMusicSeeker/ViewModels/ChartOperations/ScoreViewerRegistration.cs) | [`ScoreViewerRegistrationWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/ChartOperations/ScoreViewerRegistrationWorkflowOwnerTests.cs) がJSON解析と登録ワークフローの結果・通知を担う。登録先、`Accept` とHTTP失敗ログ・例外経路の維持は差分の静的確認で扱う。 |
| 一覧再生のプロセス所有、開始失敗時の状態解除、補助window操作のbest-effort化 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`ExternalPlayerWindowHost`](../../../BeMusicSeeker/Models/Utils/Processes/ExternalPlayerWindowHost.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)、[`ExternalPlayerProcessGatewayTests`](../../../BeMusicSeeker.Tests/Processes/ExternalPlayerProcessGatewayTests.cs) |
| LR2試聴の五項目、通常/extended style、他のXML値、設定編集、開始・終了失敗 | [`LR2body`](../../../BeMusicSeeker/Models/LR2/LR2body.cs)、[`ExternalPlayerWindowHost`](../../../BeMusicSeeker/Models/Utils/Processes/ExternalPlayerWindowHost.cs) | [`ExternalPlayerProcessGatewayTests`](../../../BeMusicSeeker.Tests/Processes/ExternalPlayerProcessGatewayTests.cs)、[`Lr2PreviewOwnerTests`](../../../BeMusicSeeker.Tests/Processes/Lr2PreviewOwnerTests.cs)、[`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) |
| BMIIDX/uBMplayのINI準備、旧bytes保全、開始前置換・初回不在 | [`BMIIDXView2015`](../../../BeMusicSeeker/Models/Playback/BMIIDXView2015.cs)、[`uBMplay`](../../../BeMusicSeeker/Models/Playback/uBMplay.cs)、[`AtomicFileWriter`](../../../BeMusicSeeker/Models/Utils/FileOperations/AtomicFileWriter.cs) | [`ExternalPlayerProcessGatewayTests`](../../../BeMusicSeeker.Tests/Processes/ExternalPlayerProcessGatewayTests.cs) の `ExternalViewerIniFailurePreservesBytesAndDoesNotReplaceProcess` は実INIの読取り禁止・削除共有禁止・同名ディレクトリで未起動と旧process保全、明示再試行を、`ExternalViewerMissingIniPreservesCreationContract` は初回不在を確認する。物理stage書込み・公開・回収失敗は [`AtomicFileWriterTests`](../../../BeMusicSeeker.Tests/FileOperations/AtomicFileWriterTests.cs) が担い、本番helperへの接続は静的点検する。 |
| uBMplayの復元・backup保持・自然終了通知 | [`uBMplay`](../../../BeMusicSeeker/Models/Playback/uBMplay.cs)、[`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | `ExternalPlayerProcessGatewayTests` の既存main/request生存中保留・元bytes復元に加え、`UbmplayRestoreFailureRetainsBackupAndBlocksNewStartUntilExplicitRetry` は実復元失敗後の明示再試行、`UbmplayReusedProcessNotifiesCurrentRequestCallback` は再利用時の現在callback、`UbmplayExitedMainStartsReplacementBeforeDelayedOldNotification` はmain実終了→通知未配送での新main開始→解除前に捕捉された旧通知の配送→新main終了と元bytes復元、およびHasExited読取り中の同期通知再入を確認する。`UbmplaySynchronousExitNotifiesOutsideOutermostLock` はmain/request×実FS復元成功/失敗でcallback入口の復元結果、別threadの実停止の進行、一通知と同一原因を確認する。`UbmplaySynchronousExitsKeepEverySnapshotWhenEarlierNotificationFails` は同じ要求で確定した複数callbackの順序と先の通知故障からの分離を確認する。[`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) の `NaturalExit_UbmplayRestoreFailureNotifiesOnceWithoutAdvancing` は実uBMplayとpanel間の自然終了単独・旧main通知未配送での新曲開始・現開始中の同期終了について、通知所有・一通知/一診断・INI path/元原因・起動阻止・明示再試行・旧snapshotからの新曲保全と通知故障封じ、`NaturalExit_StaleFailurePreservesReplacementAndStillNotifies` は旧世代の表示保全と故障通知を担う。 |
| ubmplayの一時INI生成、値の制限、設定読み戻し | [`uBMplay`](../../../BeMusicSeeker/Models/Playback/uBMplay.cs) | [`UbmplaySettingsTests`](../../../BeMusicSeeker.Tests/Playback/UbmplaySettingsTests.cs) はShift_JIS・無関係値・コメント・音量範囲・補完保持を確認し、`TemporarilyRewriteSettings_PreservesPlayerContractAndRestoresOriginalBytes` はLF/CRLFと末尾改行有無、元bytes復元を集約して確認する。 |
| 関連文書の正確な対象と起動失敗、更新リンクの通知終端 | [`SelectedChartExternalActionWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartExternalActionWorkflowOwner.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[`UpdateAvailableDialog`](../../../BeMusicSeeker/Views/Dialogs/UpdateAvailableDialog.xaml.cs) | [`SelectedChartExternalActionWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/ChartOperations/SelectedChartExternalActionWorkflowOwnerTests.cs) の `OpenRelatedDocument_RechecksPathAndPropagatesLauncherFailure` が元原因と正確なパス・一回起動を、[`DialogPresentationTests`](../../../BeMusicSeeker.Tests/Dialogs/DialogPresentationTests.cs) の `UpdateLinkLaunchFailureKeepsWindowAndDrainsCompiledEvent` が実ボタン、URL、失敗通知と窓の維持を確認する。クリップボードは既存値生成試験、フォーカスの閉鎖抑止は [`MainWindowPlaybackWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaybackWpfTests.cs)、全入口の局所終端は静的配線点検に分担する。 |

## 関連資料

[設定変更](../runtime/settings.md)、[ファイル選択](file-dialogs.md)、[プレイ履歴](../playlist/play-history.md)、[原子的な保存](../library/file-db-consistency.md)を参照します。
