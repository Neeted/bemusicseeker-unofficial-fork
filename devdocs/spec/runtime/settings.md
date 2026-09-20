# 設定の編集と反映

## 目的と適用範囲

設定画面の編集、保存の受付、取消、変更内容に応じた再読込みを定めます。ファイルの保存と起動時の破損回復は[設定の永続化](settings-persistence.md)、外観は[画面とテーマ](../ui/appearance.md)を参照します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 編集状態と画面の所有者

設定画面はメイン画面を所有者とするモーダル画面です。表示中の再要求は既存画面へフォーカスを戻します。ライブラリ操作中でも、表示、編集、取消を一律には禁止しません。保存、手動同期、音声テストなどの受付条件は個別に判定します。既に受理された処理は継続し、処理開始時に捕捉した設定を使う範囲を各機能が管理します。

`Settings.Default` 自体が編集途中の値を持つ項目があります。保存済み値のスナップショットと現在値を比較して変更を判定し、プロパティ変更通知の発生回数で判定しません。画面を開くたびに「全般」から表示し、前回の分類・スクロール位置は引き継ぎません。閉じる際は表示用処理を終えた後にデータコンテキストとバインディングを切り離します。遅れて届く表示イベントで閉じた画面を再有効化しません。

設定表示中は背面の再生操作を抑止しますが、再生中の音を停止する操作ではありません。表示の終了・失敗でも抑止を解除します。音声テスト中は設定の保存、取消、閉じる操作を受け付けず、テストの終了後に戻します。

### 分類と表示

分類は11個で、末尾は詳細設定、右クリックメニュー、このソフトについての順です。各分類の画面は共通の設定 ViewModel を利用し、分類別の状態管理を重ねません。外枠の既定サイズは820×760、最小サイズは820×600、左の分類欄は216です。本文は幅に追従し、分類変更時に本文の縦スクロールを先頭へ戻します。横スクロールや固定最大幅による余白を作りません。

全般ではLR2の場所、状態、手動同期、履歴DBの状態を区別します。音声設定は出力先、詳細設定、専用テストを分けます。プレイリストのURL対応表とプリセット、バックアップ、危険な削除操作はそれぞれの目的が分かる見出しで示します。「このソフトについて」はバージョン、ライセンス、リンク、読み取り専用のリリースノートを扱います。リリースノート画面は設定画面を所有者とし、設定編集の状態を持ちません。文化圏変更の購読は表示中だけ保持します。

入力エラーは該当欄に表示し、分類の切替や画面の再アクティブ化で失いません。URL等の未確定入力は検証失敗時にもそのまま残します。設定画面を閉じて新しく開いたときは、前回の入力エラーを通知付きで解除します。テーマと一覧表示に関するプレビューは即時反映し、取消では保存済みの値へ戻します。「既定値に戻す」も、取消後の基準を変更しません。

### LR2の場所と設定XML

`LR2RootPath`、`LR2SongDBPath`、`LR2ConfigXmlPath` は独立した保存値です。通常の起動で子パスをルートから再生成しません。保存された設定XMLのパスが欠落・読取不能・不正な場合も、別設定の保存や画面の再表示を理由に生のパスを消しません。パス文字列、解析済み設定、表示用の状態を分けます。

LR2連携モードで `LR2RootPath` が空の既存設定は非推奨ですが、互換性のためそれだけでは保存を拒否しません。一般ページのLR2ディレクトリ欄へ `Warning` と理由を表示し、値を設定するよう促します。この警告は参照操作で不正な候補を選んだ一時的な選択エラーとは別に扱い、ルートを設定した場合、または単独動作モードでは表示しません。ルートから子パスを推測する新しいフォールバックは追加しません。

設定XMLの読込み判定は `LR2Config.TryLoad` を共通の入口とします。`LR2Config` の構築には `config/jukebox` 要素が必要で、XMLとして正常でもこの構造がないファイルは無効です。登録が0件の空の `jukebox` は許容し、`system` 等の任意要素を読込み時に追加しません。登録ルートが現在存在するかどうかは別の検査で判断します。無効な入力では解析結果を公開せず、生の保存パスやファイルは変更しません。起動時の案内と処理順は[起動仕様](startup.md#登録ディレクトリの検査)に従います。

`LR2Config` の直接読込みや検索ルート登録で生じる例外の詳細も、画面へ渡る文言は表示リソースを使います。構造エラーは `Error_InvalidLR2ConfigStructure`、ファイル名の案内は `config.xml` / `config.xmh` の許容条件に従います。ウィンドウ寸法は正値、音量は0以上100以下という入力条件と文言を一致させます。通常の設定検証では既存の設定不備の案内を維持します。辞書の検査は[全件共通検査](../development/test-authoring.md#表示リソースの検査)に集約します。

標準配置はルート配下の `LR2files/Database/song.db` と `LR2files/Config/config.xml` または `config.xmh` です。ルート選択は候補と設定XMLを検証し、3パスと解析結果をまとめて変更します。同じルートを選び直した場合も現在のXMLを読みます。`song.db` がまだなくても標準パスを設定できますが、保存時の必要条件は別途満たす必要があります。無効なルートや設定XMLでは、元の3パスと解析結果を保持します。依存する表示を更新する前に3パスを揃えます。

一般ページでは `song.db` と `config.xml` / `config.xmh` の現在値を読み取り専用で表示し、それぞれの「参照」から個別に選択できます。個別参照の前には、通常はLR2ディレクトリから設定される標準配置を使うよう注意を表示し、利用者が同意した場合だけファイル選択へ進みます。選択したファイルだけを検証・変更し、もう一方のパスは変更しません。無効な候補では現在の有効な値と解析結果を保持します。

設定XMLの個別参照で同じパスを選び直した場合も、現在のXMLを再読込みします。解析結果を採用した後は、パス文字列が同じでも履歴DBの対象と依存する表示を更新します。プレイヤーが変わった場合は以前の履歴DB状態を引き継ぎません。

状態表示の由来は操作履歴ではなく現在の有効なパスから求めます。現在の `song.db` / 設定XMLがLR2ルート由来の標準配置と一致すればLR2ディレクトリからの自動設定として表示し、それ以外は個別設定として表示します。個別参照で標準パスそのものを選び直した場合も標準配置として扱い、別の「カスタム」状態は保存しません。
同じ `song.db` を選び直した場合もファイルの存在を確認し、パス文字列が変わらなくても検出状態を通知します。表示中の言語変更では、ファイルの再選択や設定画面の開き直しを要求せず、現在の設定元に対応する状態文言を更新します。

変更検出用のBMS検索ルート取得では、現在存在しないパスも比較対象に残します。実処理用の有効ルート取得とは分け、取得処理から設定XMLを保存しません。LR2プレビューが一時変更する項目の保存・復元は[外部起動](../ui/external-launch.md)を参照します。

### 取消と閉じる操作

変更がなければ、取消は画面を閉じるだけです。設定全体の復元、XMLの再読込み、再描画、再読込み処理を行いません。変更があれば編集値とプレビューを保存済みスナップショットへ戻して閉じます。

通常の閉じる操作、Alt+F4、Escは取消です。ただし、保存成功・変更なし・手動同期に伴う ViewModel からの終了要求では取消を重ねません。`IsEditCancellationEnabled` が無効の間は利用者の閉じる操作を拒否しますが、所有者の終了処理を妨げません。

設定から開始したスコア再読込み・ファイル差分更新の失敗で再試行待ちになった場合、取消と手動同期を無効にし、次のOKで同じ処理を再試行します。既に保存した設定を再保存せず、編集内容と失敗状態を保持します。

### 保存の受付と処理選択

有効なプロファイルがある場合、ライブラリ操作中のOKは保存を開始せず警告し、編集値を保存済み値へ戻します。変更がないOKは検証・保存・後続処理を行わず閉じます。

有効なプロファイルがない場合は、初回設定と設定修復を同じ経路で扱います。変更がなくても必要項目を検証し、必要な保存に成功した後で設定ウィンドウを閉じます。`Closed` の発生だけでなく、モーダル表示からの復帰、表示中ウィンドウの参照解除、表示抑止の復元まで待ってから後続処理へ渡します。

`IsFirstStartup` が真の場合は、設定ウィンドウの終了後にメインウィンドウを親として `Msg_initsetting_completed` を表示し、利用者が通知を閉じた後で初期化を一度だけ開始します。偽の場合は、この通知を表示せず初期化へ進みます。初回起動の判定条件は変更しません。画面を閉じても、保存処理のタスクは通知と初期化の結果まで追跡します。

保存に失敗した場合は閉じず、編集値と必要な失敗状態を保ちます。保存成功後の通知表示失敗では初期化へ進みません。通知または初期化に失敗した場合は、必要な失敗案内と後片付けを終え、保存中状態を解除してから設定画面を一度だけ再表示します。初期化から終了要求が返った場合は通常の失敗と区別し、再表示しません。表示要求はUIキューへ送り、前の保存処理が新しいモーダル画面の終了を待つ状態を作りません。保存済み値を無条件に巻き戻しません。

有効なプロファイルがある通常設定の全初期化・スコア再読込み・ファイル差分更新は、後続処理の結果を待ってから画面の終了を判断します。`Msg_init_completed` の表示境界と自動LR2同期の受付順序は変更しません。

変更内容が必要条件に影響する場合は全体を検証します。その他の場合も、保存に必要な外部状態の確認は省略しません。利用者設定に変更があるときだけ `Settings.Save` を行います。再生位置など実行中に変更された永続化対象も変更に含みます。LR2のXMLは、その内容の変更が必要な場合だけ保存します。

保存を拒否する必須項目は該当欄へ `Error` と理由を表示します。少なくとも新規インストール先、通常カスタムフォルダ出力先、ROOT形式カスタムフォルダ出力先は、保存判定と同じ検証結果を表示に使います。`Warning` は注意を促す非ブロッキング状態、`Error` は保存を拒否する状態として区別し、表示のためだけに別の可否判定を重ねません。

必須の起動処理・再読込み、操作を許可できない段階、譜面変更の排他はOKを阻止します。起動後の保守が走っているだけでは一律に阻止しません。非同期処理の進捗は操作の排他権を取得した後に開始し、待機中の初期化を重ねて作りません。設定起因の初期化・スコア再読込み・差分更新が失敗した場合は、再試行に必要な失敗状態を残しつつ受付上の使用中状態を解除します。

| 変更内容 | 保存後の処理 |
| --- | --- |
| 外観・一覧表示 | 即時反映のみ。ライブラリを再読込みしない。 |
| スコアの取得元、beatorajaの場所、LR2のプレイヤープロファイル | スコアのみ再読込みする。プレイリスト全体の再読込みは行わない。 |
| プレイ履歴の表示プリセット | JSONを保存して選択肢を更新する。DB照会やプレイリスト出力は行わない。 |
| 次回起動時の走査方針、保留パッケージの方針、外部起動など | 保存して次回利用時に適用する。保存だけで処理や外部プログラムを起動しない。 |
| LR2設定XMLの場所、検索ルート、カスタムフォルダ出力先 | 必要なXML・実行時検索対象を更新して、ファイル差分更新を待つ。 |
| `song.db` の場所、またはスコアとフォルダの両方に影響する変更 | ライブラリを初期化する。 |
| 動作モード | 有効なプロファイルがあれば、必要最小限の設定を保存して再起動する。初回設定では編集値として初期化へ渡す。 |

初回保存後の初期化では、通常の保存後処理を重ねません。ルート、プレイヤー、プレイリストの初期設定は初期化側が担当します。モード変更時は、現在の履歴識別情報と実行時の再生位置など必要な値だけを保存し、他の未確定編集を混ぜません。終了要求の競合と保存失敗の扱いは[終了処理](shutdown.md)に従います。

終了受付前のモード変更要求が失敗した場合は、元のモードへ戻し、設定の編集・取消を再び受け付けます。失敗は既定の通知処理から設定画面の共通表示窓口へ一度だけ渡します。通知処理を差し替えた場合は元の例外をその処理へ渡し、既定通知を重ねません。

### 反映範囲の型とスナップショット

`SettingsPostSaveImpact` が保存後の対象を明示します。`CustomFolderSearchRootSync` は出力先とLR2検索ルート、`PlayerRuntime` はプレイヤー実行時状態、`Lr2BackupEnabledNotice` はバックアップ有効化の案内、`PlaylistUrlCompletion` はURL補完、`Lr2CoreSync` はモード・ルートの同期、`ExternalLr2FolderRowsSync` は必要な外部フォルダ行、`BeatorajaBmtExport` はBMT出力を扱います。LR2全体同期に含まれる外部行の同期を重ねません。

`SettingsSnapshotRefreshScope` は保存済み比較値の更新範囲です。`StandaloneSearchRoots`、`Lr2SearchRoots`、`CustomFolderOutputBase`、`PlayHistoryDisplayPreset`、`OperationMode`、`ValidationState`、`Full` を使い分けます。画面設定だけの変更で全検索ルートを取得し直しません。同一のコレクションや選択値をクリアして追加し直すことも避けます。LR2のBMSルートや出力先の変更がXMLへ反映される場合は、直接ルート欄を編集していなくても対応する比較値を更新します。

### カスタムフォルダと検索対象

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

確認は保存済み設定と最終編集値から作り、参照時の選び直しごとには表示しません。警告の比較元には保存済みと編集中の両方のBMS登録を使い、同じ編集内で登録を削除してから出力先に採用しても確認を省略しません。検証エラーは確認より先に拒否します。確認の取消では編集値を残し、保存・同期・ファイル変更へ進みません。正常保存後に同じ役割・パスを継続する場合は再確認せず、警告済みの永続状態も持ちません。BMSディレクトリ追加は登録済みルートや出力領域との同一・親子関係を拒否し、出力に必要な登録の削除は保護します。出力先の変更は対応する出力設定から行います。

### 履歴DBの状態と計測

履歴DBの状態表示はキャッシュを読み、設定画面を開くたびにDBを照会しません。スコア読込み、対象プロファイルの履歴読込み、明示的な導入・修復・削除に伴う確認結果で更新します。対象を切り替えた場合は「未確認」に戻し、前のプロファイルの導入済み状態を使いません。明示操作は変更前後に状態を確認し、成功後に履歴の読込みキャッシュも無効にします。

画面表示、取消、検証、影響分類、保存、反映、比較値更新の診断を分けます。メッセージを利用者が閉じるまでの時間を内部処理の性能値と混ぜません。診断の失敗で操作の成否を変更しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 表示・閉じる・分類・入力保持 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs)、[`SettingsWindowCompiledBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsWindowCompiledBehaviorTests.cs)、[`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsDialogBehaviorTests.cs) |
| 動作モード変更の確認・再起動要求・失敗通知 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) の `OperationModeLR2DB` | [`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsDialogBehaviorTests.cs) の `SettingDialogOperationModeChange_ConfirmsAndRoutesThroughShellRequest`: 初回編集、確認の許可・取消、要求失敗時のモード復元・編集再開・保存と終了の抑止、既定通知一回と差替え通知への元例外引渡し。 |
| 初回・修復設定の保存、閉鎖、通知、初期化の順序 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `ApplySettingsAsync_InitialSettings_ClosesBeforeNotificationAndAwaitsInitialization`: 単独・LR2、初回通知の有無、表示終了と通知の待機、二重要求の拒否。 |
| 初回設定の実表示と失敗後の再表示 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow.cs)、[`SettingsWindow`](../../../BeMusicSeeker/Views/SettingsWindow.cs) | [`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs) の `MainWindow_InitialSettingsCloseBeforeRealCompletionMessageAndRecoverAfterInitialization`: 実際の通知、親ウィンドウ、モーダル表示の終了、失敗後の編集受付、終了要求時の再表示抑止。 |
| 初回保存・通知・初期化の失敗と再試行 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs)、[`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `ApplySettingsAsync_InitialSaveFailureKeepsDraftWithoutClosingOrInitializing`、`ApplySettingsAsync_InitialSettingsDialogFailureIsReported`、`ApplySettingsAsync_InitialInitializationFailureReopensAfterCleanupAndCanRetry`、`ApplySettingsAsync_InitialDirectoryFailureReopensOnceAfterCleanupAndCanRetry`: 保存失敗では入力保持、通知失敗では初期化抑止、初期化失敗では保存済み値と再試行受付を保持。 |
| 設定部品とAutomation選択、実入力の受入 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingsControlPresentationTests`](../../../BeMusicSeeker.Tests/SettingsControlPresentationTests.cs)、実フォーカスとキー操作は[入力操作の明示受入](../development/testing.md#入力操作の明示受入) |
| 変更範囲と検索ルートの比較 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`StartupSettingsSnapshotTests`](../../../BeMusicSeeker.Tests/StartupSettingsSnapshotTests.cs)、[`CustomFolderOutputSettingsSnapshotTests`](../../../BeMusicSeeker.Tests/CustomFolderOutputSettingsSnapshotTests.cs) |
| 出力先とBMS登録の同一採用・親子禁止・入力順 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs)、[`CustomFolderOutputBaseSearchRootSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/CustomFolderOutputBaseSearchRootSyncService.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `CustomFolderOutput_RejectsForbiddenBmsNestingAtSelectionAndSave`、`CustomFolderOutputChoices_RejectOverlappingBasesInEitherEntryOrder`。旧追加・旧ルートの転用保護とBMS追加・削除は [`SettingDialogCustomFolderOutputBaseTests`](../../../BeMusicSeeker.Tests/SettingDialogCustomFolderOutputBaseTests.cs)。 |
| 出力設定の復元、保存時確認と取消 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) の `ApplySettingsAsync` | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `ApplySettingsAsync_RestoresRegisteredOutputWithCancellableConfirmation`、`ApplySettingsAsync_NormalOutputChangeWarnsOnlyForSavedSearchRoot`: 通常・追加・ルートの復元（登録削除を先行する同一編集を含む）、取消時の編集値・XML保持、保存後の再警告抑止、中間値への通知抑止、旧通常の追加転用。 |
| LR2設定XMLの構造と読込み結果 | [`LR2Config`](../../../BeMusicSeeker/Models/LR2/LR2Config.cs) の `TryLoad` とコンストラクター | [`LR2ConfigTests`](../../../BeMusicSeeker.Tests/LR2ConfigTests.cs) の `TryLoad_UnsetOrInvalidPathReturnsNoConfig`、`TryLoad_InvalidDocumentRejectsWithoutChangingFile`、`TryLoad_ValidConfigPreservesUnavailableRegisteredRoots`、`TryLoad_EmptyJukeboxIsValidWithoutCreatingOptionalSections`: 不正入力の拒否、空登録と `.xml` / `.xmh` の許容、欠落ルートとファイルの保持。 |
| LR2楽曲DBの再選択と検出状態 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) の `LR2SongDBPath` | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `Lr2SongDbPicker_ReselectingRestoredFileRefreshesStatusWithoutSaving`: 標準配置・個別設定で欠落後に復元された同じファイルを選び直すと、状態の文字列・種類・アイコンを通知する。3パス、設定XML、保存回数は変更しない。 |
| LR2設定元の状態表示と言語変更 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs)、[`GeneralSettingsPage`](../../../BeMusicSeeker/Views/Settings/Pages/GeneralSettingsPage.xaml) | [`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs) の `SettingsWindow_Lr2PathSourceStatusUpdatesWhenLanguageChanges`: 標準配置・個別設定のそれぞれで、表示中の楽曲DBと設定XMLの状態文言が言語変更に追従し、3パスを保持する。 |
| LR2パスの再選択とXML由来の履歴DB対象 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) の `LR2RootPath`、`LR2ConfigXmlPath` | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `Lr2PathPickers_ReselectingCurrentPathAdoptsExternalConfigBeforeLaterSave`: 同じルート・XML・XMHの再選択で外部変更とプレイヤーを採用し、履歴DBの対象と通知を更新する。選択時の保存抑止と後の保存での外部要素保持も確認する。 |
| 無効なLR2設定パスの保存・再表示・取消 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `Lr2InvalidPersistedConfig_OpenSaveReopenAndParentCancelPreserveRawTuple`: ファイル欠落、XML不正、必要構造不足でも独立した3パスを保持する。 |
| LR2ディレクトリ未設定の保存を阻止しない警告 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs)、[`GeneralSettingsPage`](../../../BeMusicSeeker/Views/Settings/Pages/GeneralSettingsPage.xaml) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `Lr2RootPathEmpty_IsWarningOnlyAndDoesNotBlockSaving`: LR2連携時だけWarningを表示し、ルート空だけでは保存を拒否せず、一時的な参照エラーと混在しないことを確認する。 |
| 保存不能な必須項目のError表示 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs)、[`InstallSettingsPage`](../../../BeMusicSeeker/Views/Settings/Pages/InstallSettingsPage.xaml)、[`PlaylistSettingsPage`](../../../BeMusicSeeker/Views/Settings/Pages/PlaylistSettingsPage.xaml) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `RequiredSettingsValidationPresentation_UsesErrorStateForSaveBlockingFields`: 新規インストール先、通常・ROOT形式出力先の表示状態と保存判定が一致することを確認する。 |
| LR2の保存先とプレイヤー設定 | [`SettingsPlayerSettingsGateway`](../../../BeMusicSeeker/Models/PlayerSettingsGateway.cs) | [`PlayerSettingsGatewayTests`](../../../BeMusicSeeker.Tests/PlayerSettingsGatewayTests.cs) |
| 履歴DBの状態と明示操作 | [`Lr2PlayHistorySchemaService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2PlayHistorySchemaService.cs) | [`Lr2PlayHistorySchemaUiTests`](../../../BeMusicSeeker.Tests/Lr2PlayHistorySchemaUiTests.cs) |
| 設定値の捕捉と追加パスの分離 | [`BMSLibrary`](../../../BeMusicSeeker/Models/BMSLibrary.cs) | [`BmsLibraryOptionsSnapshotTests`](../../../BeMusicSeeker.Tests/BmsLibraryOptionsSnapshotTests.cs) |
| 設定画面の単一要求、音声設定の取消と保存失敗 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingDialogOpenCommandTests`](../../../BeMusicSeeker.Tests/SettingDialogOpenCommandTests.cs) |

## 関連資料

[設定の永続化](settings-persistence.md)、[起動と再読込み](startup.md)、[音声](audio.md)、[画面テスト](../development/testing.md)を参照します。
