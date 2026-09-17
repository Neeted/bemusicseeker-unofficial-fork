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

設定XMLの読込み判定は `LR2Config.TryLoad` を共通の入口とします。`LR2Config` の構築には `config/jukebox` 要素が必要で、XMLとして正常でもこの構造がないファイルは無効です。登録が0件の空の `jukebox` は許容し、`system` 等の任意要素を読込み時に追加しません。登録ルートが現在存在するかどうかは別の検査で判断します。無効な入力では解析結果を公開せず、生の保存パスやファイルは変更しません。起動時の案内と処理順は[起動仕様](startup.md#登録ディレクトリの検査)に従います。

`LR2Config` の直接読込みや検索ルート登録で生じる例外の詳細も、画面へ渡る文言は表示リソースを使います。構造エラーは `Error_InvalidLR2ConfigStructure`、ファイル名の案内は `config.xml` / `config.xmh` の許容条件に従います。ウィンドウ寸法は正値、音量は0以上100以下という入力条件と文言を一致させます。通常の設定検証では既存の設定不備の案内を維持します。辞書の検査は[全件共通検査](../development/test-authoring.md#表示リソースの検査)に集約します。

標準配置はルート配下の `LR2files/Database/song.db` と `LR2files/Config/config.xml` または `config.xmh` です。ルート選択は候補と設定XMLを検証し、3パスと解析結果をまとめて変更します。同じルートを選び直した場合も現在のXMLを読みます。`song.db` がまだなくても標準パスを設定できますが、保存時の必要条件は別途満たす必要があります。無効なルートや設定XMLでは、元の3パスと解析結果を保持します。依存する表示を更新する前に3パスを揃えます。

詳細指定では親画面の値のコピーを編集します。ファイル選択は現在の入力を初期位置として使い、バインディングを維持したまま入力欄を更新します。完了時には両方の入力欄の最新文字列を検証し、成功時だけ親へ一括反映します。取消・Esc・閉じる操作ではコピーを破棄し、設定ファイルは保存しません。標準配置と一致しないかどうかはパスから求め、別の「カスタム」状態を保存しません。

変更検出用のBMS検索ルート取得では、現在存在しないパスも比較対象に残します。実処理用の有効ルート取得とは分け、取得処理から設定XMLを保存しません。LR2プレビューが一時変更する項目の保存・復元は[外部起動](../ui/external-launch.md)を参照します。

### 取消と閉じる操作

変更がなければ、取消は画面を閉じるだけです。設定全体の復元、XMLの再読込み、再描画、再読込み処理を行いません。変更があれば編集値とプレビューを保存済みスナップショットへ戻して閉じます。

通常の閉じる操作、Alt+F4、Escは取消です。ただし、保存成功・変更なし・手動同期に伴う ViewModel からの終了要求では取消を重ねません。`IsEditCancellationEnabled` が無効の間は利用者の閉じる操作を拒否しますが、所有者の終了処理を妨げません。

設定から開始したスコア再読込み・ファイル差分更新の失敗で再試行待ちになった場合、取消と手動同期を無効にし、次のOKで同じ処理を再試行します。既に保存した設定を再保存せず、編集内容と失敗状態を保持します。

### 保存の受付と処理選択

有効なプロファイルがある場合、ライブラリ操作中のOKは保存を開始せず警告し、編集値を保存済み値へ戻します。変更がないOKは検証・保存・後続処理を行わず閉じます。有効なプロファイルがない初回設定では、変更がなくても必要項目を検証し、保存して初期化完了を待ちます。

変更内容が必要条件に影響する場合は全体を検証します。その他の場合も、保存に必要な外部状態の確認は省略しません。利用者設定に変更があるときだけ `Settings.Save` を行います。再生位置など実行中に変更された永続化対象も変更に含みます。LR2のXMLは、その内容の変更が必要な場合だけ保存します。

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

### 反映範囲の型とスナップショット

`SettingsPostSaveImpact` が保存後の対象を明示します。`CustomFolderSearchRootSync` は出力先とLR2検索ルート、`PlayerRuntime` はプレイヤー実行時状態、`Lr2BackupEnabledNotice` はバックアップ有効化の案内、`PlaylistUrlCompletion` はURL補完、`Lr2CoreSync` はモード・ルートの同期、`ExternalLr2FolderRowsSync` は必要な外部フォルダ行、`BeatorajaBmtExport` はBMT出力を扱います。LR2全体同期に含まれる外部行の同期を重ねません。

`SettingsSnapshotRefreshScope` は保存済み比較値の更新範囲です。`StandaloneSearchRoots`、`Lr2SearchRoots`、`CustomFolderOutputBase`、`PlayHistoryDisplayPreset`、`OperationMode`、`ValidationState`、`Full` を使い分けます。画面設定だけの変更で全検索ルートを取得し直しません。同一のコレクションや選択値をクリアして追加し直すことも避けます。LR2のBMSルートや出力先の変更がXMLへ反映される場合は、直接ルート欄を編集していなくても対応する比較値を更新します。

### カスタムフォルダと検索対象

通常のカスタムフォルダ出力先は、既存譜面の走査には残す場合がありますが、利用者の検索ルート、導入候補、フォルダツリーには混ぜません。追加出力先とそのルートは譜面検索対象からも除きますが、必要なjukebox登録は維持します。

新しい通常出力先が、管理外の既存jukeboxルートと一致するか親子関係になる場合は検証エラーです。最終的なBMS検索ルートと出力先が重なる状態を、確認ダイアログや暗黙の部分除外で受理しません。

### 履歴DBの状態と計測

履歴DBの状態表示はキャッシュを読み、設定画面を開くたびにDBを照会しません。スコア読込み、対象プロファイルの履歴読込み、明示的な導入・修復・削除に伴う確認結果で更新します。対象を切り替えた場合は「未確認」に戻し、前のプロファイルの導入済み状態を使いません。明示操作は変更前後に状態を確認し、成功後に履歴の読込みキャッシュも無効にします。

画面表示、取消、検証、影響分類、保存、反映、比較値更新の診断を分けます。メッセージを利用者が閉じるまでの時間を内部処理の性能値と混ぜません。診断の失敗で操作の成否を変更しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 表示・閉じる・分類・入力保持 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs)、[`SettingsWindowCompiledBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsWindowCompiledBehaviorTests.cs)、[`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsDialogBehaviorTests.cs) |
| フォーカスと実際の利用者操作 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingsForegroundInteractionTests`](../../../BeMusicSeeker.Tests/SettingsForegroundInteractionTests.cs) |
| 変更範囲と検索ルートの比較 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`StartupSettingsSnapshotTests`](../../../BeMusicSeeker.Tests/StartupSettingsSnapshotTests.cs)、[`CustomFolderOutputSettingsSnapshotTests`](../../../BeMusicSeeker.Tests/CustomFolderOutputSettingsSnapshotTests.cs) |
| LR2設定XMLの構造と読込み結果 | [`LR2Config`](../../../BeMusicSeeker/Models/LR2/LR2Config.cs) の `TryLoad` とコンストラクター | [`LR2ConfigTests`](../../../BeMusicSeeker.Tests/LR2ConfigTests.cs) の `TryLoad_UnsetOrInvalidPathReturnsNoConfig`、`TryLoad_InvalidDocumentRejectsWithoutChangingFile`、`TryLoad_ValidConfigPreservesUnavailableRegisteredRoots`、`TryLoad_EmptyJukeboxIsValidWithoutCreatingOptionalSections`: 不正入力の拒否、空登録と `.xml` / `.xmh` の許容、欠落ルートとファイルの保持。 |
| 無効なLR2設定パスの保存・再表示・取消 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingDialogEditCompletionTests`](../../../BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs) の `Lr2InvalidPersistedConfig_OpenSaveReopenAndParentCancelPreserveRawTuple`: ファイル欠落、XML不正、必要構造不足でも独立した3パスを保持する。 |
| LR2の保存先とプレイヤー設定 | [`SettingsPlayerSettingsGateway`](../../../BeMusicSeeker/Models/PlayerSettingsGateway.cs) | [`PlayerSettingsGatewayTests`](../../../BeMusicSeeker.Tests/PlayerSettingsGatewayTests.cs) |
| 履歴DBの状態と明示操作 | [`Lr2PlayHistorySchemaService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2PlayHistorySchemaService.cs) | [`Lr2PlayHistorySchemaUiTests`](../../../BeMusicSeeker.Tests/Lr2PlayHistorySchemaUiTests.cs) |
| 設定値の捕捉と追加パスの分離 | [`BMSLibrary`](../../../BeMusicSeeker/Models/BMSLibrary.cs) | [`BmsLibraryOptionsSnapshotTests`](../../../BeMusicSeeker.Tests/BmsLibraryOptionsSnapshotTests.cs) |
| 設定画面の単一要求、音声設定の取消と保存失敗 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs) | [`SettingDialogOpenCommandTests`](../../../BeMusicSeeker.Tests/SettingDialogOpenCommandTests.cs) |

## 関連資料

[設定の永続化](settings-persistence.md)、[起動と再読込み](startup.md)、[音声](audio.md)、[画面テスト](../development/testing.md)を参照します。
