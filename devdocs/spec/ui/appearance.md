# 外観と共通の表示部品

## 目的と適用範囲

テーマ、標準の表示部品、設定画面とダイアログの外観を定めます。機能の受付、保存、終了順序は各機能の仕様に従い、外観の変更で変えません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

「共通スタイル」は `App.Canonical.*` で識別する、用途ごとに明示して採用する表示設定です。DIPはWindowsの表示倍率に依存しない論理単位、DWMはWindowsのウィンドウ描画管理機構です。

## 仕様

### テーマの保存と切替

`Settings.Default.AppearanceTheme` は `Light` または `Dark` を保存します。既定と不明・空・nullの正規化先は `Light` です。保存前にも正規化します。テーマとは別に、一覧の文字サイズ、行の高さ、見出しの高さを設定できます。

`AppThemeService` は起動時に正規化済みのテーマ辞書をアプリのリソースへ追加します。切替時は相対形式と `/BeMusicSeeker;component/Themes/*.xaml` 形式の既存辞書を同じものとして除去し、後者の形式で新しい辞書を追加します。`ThemeChanged` を通知し、`Version` を進めます。

XAMLは原則として `DynamicResource` を使います。コード描画はテーマ変更と版を使って描画色・文字列・描画結果のキャッシュを破棄します。一覧の `CustomTablePalette` とスコア色もこの規則に従います。

### 配色の定義

[明るいテーマ](../../../Themes/Light.xaml)と[暗いテーマ](../../../Themes/Dark.xaml)は同じ用途のキーを持ちます。値と完全な一覧はこの二つを正本とします。

| キーの系統 | 用途 |
| --- | --- |
| `App.*` | 背景、本文、境界、無効状態、入力、操作の強弱、成功・警告・失敗、ダイアログ、メニュー、検索 |
| `Table.*` | 見出し、行、選択、現在セル、警告、未定義値、区切り線、並べ替え、チェック |
| `ScrollBar.*` | 軌道、つまみ、ボタン、境界、矢印と各操作状態 |
| `TreeViewItem.TreeArrow.*` | ツリーの展開状態 |
| `NormalBrush` 等 | 既存の `Simple Styles.xaml` が使う互換用のキー。新しい設定画面用の色には使わない |

新しい色は既存の意味付きのキーで表せるかを先に確認します。設定画面やダイアログで固定色や表専用の色を流用しません。進捗バーはテーマの強調色とは別に、共通の緑色と薄い軌道を使います。選択された表の文字は明るいテーマで黒、暗いテーマで白を基本とします。

### 共通スタイルの所有

アプリの辞書は `CanonicalDialogStyles.xaml`、そこから `CanonicalControls.xaml` を参照します。`App.Canonical.*` は用途を明示した共通スタイルのキーです。導入だけで未採用のメイン画面へ波及しないよう、アプリ全体の型名による暗黙スタイルとして追加しません。

設定画面は `SettingsControls` から共通辞書へ接続します。設定画面の既存キーは `BasedOn` で対応する共通スタイルを参照し、設定画面内にだけ既定スタイルを置きます。その他の独自ダイアログは共通辞書を直接使います。辞書やテンプレートを画面ごとに複製しません。

ボタン、入力欄、選択欄、チェック、ラジオボタン、ラベル、グループ、タブ、折りたたみ、メニューと区切り、一覧、スクロール等を共通化します。標準のキーボード操作とアクセシビリティを保ち、`PART_ContentHost`、`PART_Popup`、`PART_EditableTextBox`、`PART_Track`、ScrollViewerの部品、Expanderの `HeaderSite` を欠かしません。

検証対象は同一ウィンドウ内で実際に適用された役割とテンプレート、未採用の部品への非波及です。別々に読み込んだ辞書のオブジェクト同一性、ソース文字列、子要素の順序、固定座標を契約にしません。

### スクロールとフォーカス

縦横のスクロールバーは `App.Canonical.VerticalScrollBarTemplate` / `HorizontalScrollBarTemplate` と `ScrollBarStyle` が方向、軌道、行・ページ単位の操作、つまみ、端、無効状態を所有します。`SimpleScrollBar` は互換名としてこれを参照します。ScrollViewerの部品名、内容スクロール、位置・可視範囲・最大値、アクセシビリティの契約は維持します。

スクロールバーの幅・高さと軌道は15に揃え、交点も軌道の背景で埋めます。つまみは軌道内で中央に置きます。端の隙間を隠すためにずらしません。メイン画面と表は配置の丸めと画素への整列を有効にします。

WPF既定の点線のフォーカス飾りは使いません。表は選択と現在セル、ツリーは選択背景と文字、入力欄は `App.InputFocusBorderBrush`、その他の部品は選択・重ね合わせ・押下状態で示します。分割線はTab移動の対象外にし、ドラッグによるサイズ変更を保ちます。

### メイン画面

メイン画面は背景と文字色をテーマへ接続します。外周は内容の測定範囲を縮めない1 DIPの表示専用枠とし、入力を受け取りません。非アクティブ時は控えめな文字色、アクティブ時は強調色です。最大化時は既存の8 DIPの余白に合わせて内側へ移し、サイズ変更の判定を妨げません。

キャプションボタンは常に控えめな文字色です。標準の `WindowChrome`、`SystemCommands` と専用の入力判定を使い、サイズ変更の判定幅5を保ちます。

ツリーの通常・選択・無効の文字色は、`TextBlock` と `EditableTextBlock` の両方へ明示します。サイドバーの分割線は操作範囲5、見える線1とし、透明な操作範囲を重ねます。余分な余白でツリーと表、スクロールバーを離しません。

検索欄に文字があるときは `App.ControlBackgroundActiveBrush` で絞込み中であることを示します。明るいテーマは薄い桃色、暗いテーマは落ち着いた注意色とし、構文警告の `!` とは区別します。候補の操作は検索支援の仕様に従います。

### 設定画面の構造と即時反映

設定画面は左のカテゴリ一覧、見出し、本文、下部の操作から成り、上部タブは使いません。カテゴリ、見出し、下部操作を固定し、選択中の本文だけを一つの縦ScrollViewerでスクロールします。横スクロールは使いません。長い翻訳のラベルや選択文は折り返します。

カテゴリは角を丸めた選択背景と左端の強調線で選択を示し、マウス、キーボード、無効状態も識別可能にします。大きな節を入れ子のカードにせず、節間と内部は8 / 12 / 16 / 24の間隔尺度に揃えます。

テーマは `IsLightAppearanceTheme` / `IsDarkAppearanceTheme` と `AppearanceTheme` の双方向の結び付けで選択します。開くたびに作る画面は、表示ツリーへ接続して現在値を受け取ります。未接続の部品へローカル値を設定して結び付けを壊しません。取消では保存済みのテーマへ戻して即時適用します。

一覧の文字サイズ、行・見出しの高さはスライダー操作ごとに設定と `MainWindowViewSettingsStore` へ通知し、実際のメイン表へ反映します。別の固定データの見本表を置きません。初期化は三つの値を既定に戻し、取消は保存済みの値とメイン表の表示を戻します。

### 設定用の表示部品

`SettingsSection` は見出し、任意の説明、内容の一つのテンプレートを所有します。有限の高さでは見出しと説明を除く残りを内容へ渡し、自動調整の画面では内容の必要高さを保ちます。人工的な余白や画面ごとの複製を作りません。

`SettingsField`、`SettingsOptionRow`、`SettingsPathPicker`、`SettingsListEditor`、`SettingsStatusBanner` は表示用のプロパティとイベントだけを公開します。サービス探索、業務命令、保存は所有しません。表示へ接続するだけで編集値を変更しません。再生設定は選択中のプレイヤーの詳細だけを表示し、選択変更で非表示のプレイヤーの設定を書き換えません。

`SettingsField` と `SettingsPathPicker` は入力検証の意味を `ValidationStatus` と説明文で受け取ります。`Warning` は `App.WarningTextBrush`、`Error` は `App.ErrorTextBrush` を使い、パス入力と検証対応を明示した入力部品では入力枠にも同じ意味色を反映します。状態は色だけに依存させず、可視の説明文と `AutomationProperties.ItemStatus` / `HelpText` を併用します。正常時は状態と説明文を空にし、説明文が空なら検証文言を表示しません。表示部品自身は Warning / Error の業務上の判定を行いません。

`SettingsField.Header` は実際の入力部品のアクセシビリティ名を補うためにも使います。自身を参照元とする一方向の結び付けを所有し、その結び付け自体で所有を判定します。呼出元の値・別の結び付けは、同じ表示値でも上書きしません。見出し変更や取り外し時も自分が所有するものだけを更新・除去します。

状態表示は空・空白の文字列を畳み、構造を持つ内容と呼出元の非表示指定を保ちます。内容が戻れば表示を回復します。メッセージ全文をアクセシビリティ名、意味を `ItemStatus`、更新を控えめな通知として公開し、装飾アイコンを重複して読み上げません。音声機器の利用不可理由やテスト結果などの長文だけは、専用の複数行スタイルで幅に合わせて折り返します。

非編集のComboBoxは選択内容・中央・矢印の全体で開けます。編集型は前面の入力欄、カーソル、キー、アクセシビリティ、標準のポップアップを維持します。両方とも開いた時点の実測幅を候補の外側の幅に使い、閉じて幅を変更した後も追随します。内容幅、固定値、初回の値、最小幅だけで代用しません。

保存して閉じるボタンは全状態で強調色の系列を保ち、フォーカスの枠は各強調色と3:1以上の明暗差を持たせます。取消は控えめな操作です。危険操作とアイコン操作も共通の状態モデルを使い、命令や有効条件を変えません。スライダーは縦横と全ての目盛位置を保ち、目盛は `App.SliderTickBrush` で描画します。

### 標準ウィンドウの題名部

独自の外枠を所有するメイン画面以外のアプリのウィンドウは、XAML・コード生成とも `ThemedWindow` を使います。標準のWPF題名部を保ち、ハンドルの作成後に現在のテーマと題名・文字・境界の色をDWMへ要求します。表示中の変更へ追随し、終了時に購読を解除します。

OSや属性の非対応、API不在、HRESULTの失敗は標準のWindows表示を使います。一方、リソース読込みや接続処理の予期しない例外は隠しません。初回接続中なら購読・接続状態・ハンドルを戻して同じ管理器で再試行できるようにし、接続後のテーマ変更中なら接続を維持して後続の変更と終了を可能にします。購読解除は一回だけです。

### ダイアログの表示面

標準ウィンドウでは `App.Canonical.NativeWindowContentStyle` が外周の間隔だけを所有します。内側へもう一つ塗りつぶし・丸い境界・カードを重ねません。設定画面の本体は標準の表示領域の端まで延ばし、内部の機能ごとの間隔は保ちます。

メイン画面内に重ねる初回言語選択とプレイリストURL入力は、`DialogOverlayStyle` と `DialogContentStyle` を明示して使います。標準ウィンドウへこの重ね表示用の外面を流用しません。OSのファイル・フォルダ選択と緊急ダイアログは対象外です。所有元、モーダル結果、取消、終了の抑制、処理と寿命の所有は表示部品の統一で変えません。

### 個別画面の寸法と読み上げ

| 画面 | 契約 |
| --- | --- |
| リリースノート | `FlowDocumentScrollViewer`、`Meiryo UI`、文字サイズ12。本文は通常、版の見出しは太字。選択可能、ツールバーなし、縦は自動、横は無効の一つの表示範囲を持つ |
| URL入力の重ね表示 | 外側の大きさを変えず、見出し・入力・下部操作を `Auto` / `*` / `Auto` で配置する。入力の固定高さを置かず、文字の拡大時も下部操作へ重ねない |
| プレイリスト一括編集 | 初期幅を既存の最小幅に揃える。正の最小幅・高さ、サイズ変更、縦スクロール、終了の契約を保つ |
| プレイリストのプロパティ | 初期640×720、最小560×420、サイズ変更可。上部選択と下部操作を固定し、選択ページだけ縦スクロールする。既定の日本語・書体・96 DPIの初期一般ページは縦スクロール不要。横スクロールは使わない |

リリースノートの右クリックは標準の `ContextMenuService` に任せ、コピーと全選択を含む標準の役割を維持します。アプリ独自のメニューや文言を追加せず、選択・クリップボード・物理カーソルを横取りしません。

プレイリストのプロパティの上部はGeneral / Folder / Custom Folderの単一選択です。共通の上部ナビゲーションを明示的に使い、標準の選択・項目選択のアクセシビリティとキー操作を保ちます。選択は下線で示し、ページ全体を囲むカードは作りません。

Custom Folderでは十三のまとめ方と説明を持つOutput Folderを先に、出力基点・フォルダ名・ルート形式のOutput Destinationを後に置きます。上から自然に並べ、ウィンドウを高くしただけで後半を下端へ移動させません。

### 対象外

OS標準の選択画面、利用者定義テーマ、個別色の編集、OSテーマへの自動追随はこの仕様の提供範囲に含めません。新しい画面も既存の共通スタイルを優先し、機能のない将来設定を追加しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| テーマの切替、標準題名部の接続・解除と失敗 | [`AppThemeService`](../../../BeMusicSeeker/Models/AppThemeService.cs)、[`ThemedWindow`](../../../BeMusicSeeker/Views/ThemedWindow.cs) | [`NativeWindowThemeContractTests`](../../../BeMusicSeeker.Tests/NativeWindowThemeContractTests.cs)、[`NativeWindowTitleBarTests`](../../../BeMusicSeeker.Tests/NativeWindowTitleBarTests.cs) |
| 設定の表示部品、検証状態、アクセシビリティ、候補幅、即時反映と取消 | [`SettingsWindow`](../../../BeMusicSeeker/Views/SettingsWindow.cs)、[`SettingsField`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs)、[`SettingsPathPicker`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs)、[`SettingsSection`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs) | [`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs) の `SettingsPages_RequiredValidationBindingsUseSharedWarningAndErrorPresentation`、`SettingsValidationPresentation_ExposesWarningAndErrorWithoutRelyingOnColorAlone`、および既存の表示・アクセシビリティ検査、[`SettingsWindowCompiledBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsWindowCompiledBehaviorTests.cs)、[`SettingsControlPresentationTests`](../../../BeMusicSeeker.Tests/SettingsControlPresentationTests.cs)、[`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/SettingsDialogBehaviorTests.cs) |
| ダイアログの表示面、標準メニュー、結果とサイズ | [`ThemedMessageBox`](../../../BeMusicSeeker/Views/ThemedMessageBox.cs) | [`DialogPresentationTests`](../../../BeMusicSeeker.Tests/DialogPresentationTests.cs)、[`ThemedMessageBoxTests`](../../../BeMusicSeeker.Tests/ThemedMessageBoxTests.cs)、[`UiDialogCoordinatorWpfTests`](../../../BeMusicSeeker.Tests/UiDialogCoordinatorWpfTests.cs) |
| 表とツリーへの配色・表示の反映 | [`CustomTablePalette`](../../../BeMusicSeeker/Views/CustomTablePalette.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow.cs) | [`MainWindowChartPresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindowChartPresentationWpfTests.cs)、[`MainWindowTreePresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindowTreePresentationWpfTests.cs) |

## 関連資料

[ダイアログの受付](dialogs.md)、[一覧表示](table-view.md)、[検索支援](keyword-search.md)、[設定と保存](../runtime/settings.md)、[表示テスト](../development/testing.md)を参照します。
