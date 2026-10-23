# 外観と共通の表示部品

## 目的と適用範囲

テーマ、標準の表示部品、設定画面とダイアログの外観を定めます。機能の受付、保存、終了順序は各機能の仕様に従い、外観の変更で変えません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

「共通スタイル」は `App.Canonical.*` で識別する、用途ごとに明示して採用する表示設定です。DIPはWindowsの表示倍率に依存しない論理単位、DWMはWindowsのウィンドウ描画管理機構です。

## 仕様

### テーマの保存と切替

`Settings.Default.AppearanceTheme` は `Light`、`Dark`、`Sepia`、`Frost`、`NeonViolet`、`GreenTerminal`、`AmberTerminal`、`OledBlack`、`DeepSea`、`DosBlue` のいずれかを保存します。既定と不明・空・nullの正規化先は `Light` です。保存前にも正規化します。明るいテーマは `Light`、`Sepia`、`Frost` とし、それ以外は標準題名部にも暗色指定を行います。テーマとは別に、一覧の文字サイズ、行の高さ、見出しの高さを設定できます。

`AppThemeService` は起動時に正規化済みのテーマ辞書をアプリのリソースへ追加します。辞書の参照は `/BeMusicSeeker;component/Themes/` 形式に統一します。切替対象は登録済みの10配色辞書だけとし、既存の配色辞書を除去して指定された辞書を追加します。同じフォルダにある共通スタイルの辞書は保持します。`ThemeChanged` を通知し、`Version` を進めます。

XAMLは原則として `DynamicResource` を使います。コード描画はテーマ変更と版を使って描画色・文字列・描画結果のキャッシュを破棄します。一覧の `CustomTablePalette` とスコア色もこの規則に従います。

### 配色の定義

[明るいテーマ](../../../BeMusicSeeker/Themes/Light.xaml)、[暗いテーマ](../../../BeMusicSeeker/Themes/Dark.xaml)と追加の8配色辞書は同じ用途のキーを持ちます。各配色値はその辞書を正本とします。

| キーの系統 | 用途 |
| --- | --- |
| `App.*` | 背景、本文、境界、無効状態、入力、操作の強弱、成功・警告・失敗、ダイアログ、メニュー、検索 |
| `Table.*` | 見出し、行、選択、現在セル、警告、未定義値、区切り線、並べ替え、チェック |
| `ScrollBar.*` | 軌道、つまみ、ボタン、境界、矢印と各操作状態 |
| `TreeViewItem.TreeArrow.*` | ツリーの展開状態 |
| `NormalBrush` 等 | 既存の `Simple Styles.xaml` が使う互換用のキー。新しい設定画面用の色には使わない |

新しい色は既存の意味付きのキーで表せるかを先に確認します。設定画面やダイアログで固定色や表専用の色を流用しません。進捗バーはテーマの強調色とは別に、共通の緑色と薄い軌道を使います。選択された表の文字は明るいテーマで黒、暗いテーマで白を基本とします。

### 共通スタイルの所有

アプリの辞書は `CanonicalDialogStyles.xaml`、そこから `CanonicalControls.xaml` を参照します。`App.Canonical.*` は用途を明示した共通スタイルのキーです。導入だけで未採用のメイン画面へ波及しないよう、アプリ全体の型名による暗黙スタイルとして追加しません。例外として `ToolTip` は全画面で型スタイルを適用し、文字列から自動生成されるもの、明示生成、SliderのAutoToolTipを同じ意味ブラシへ接続します。背景は `App.PopupBackgroundBrush`、本文は `App.TextBrush`、枠は `App.BorderBrush` とし、標準テンプレートを保って表示中のテーマ切替にも追随します。

共通の操作・状態アイコンは `AppFontIcon` と `App.IconFontFamily`、`App.Canonical.FontIconStyle` を明示して使います。Windows 11 の `Segoe Fluent Icons` を第一候補、Windows 10 の `Segoe MDL2 Assets` を代替にし、両方で同じ意味を持つコードポイントを選びます。Segoe のフォントファイルはアプリへ同梱しません。`AppFontIcon` は固定領域の文字ベースラインへ合わせず、解決された字形の実描画領域を表示領域の中央へ配置します。アイコンの色は固定値にせず、本文、補助、強調、警告、失敗など既存の意味付きブラシへ接続します。

設定画面は `SettingsControls` から共通辞書へ接続します。設定画面の既存キーは `BasedOn` で対応する共通スタイルを参照し、設定画面内にだけ既定スタイルを置きます。その他の独自ダイアログは共通辞書を直接使います。辞書やテンプレートを画面ごとに複製しません。

ボタン、入力欄、選択欄、チェック、ラジオボタン、ラベル、グループ、タブ、折りたたみ、メニューと区切り、一覧、スクロール等を共通化します。標準のキーボード操作とアクセシビリティを保ち、`PART_ContentHost`、`PART_Popup`、`PART_EditableTextBox`、`PART_Track`、ScrollViewerの部品、Expanderの `HeaderSite` を欠かしません。

共通Expanderの見出しは、通常背景に `App.ControlBackgroundBrush`、1 DIPの枠に `App.BorderBrush` を使い、表示中のテーマ切替へ追随します。折畳み・展開の矢印は見出しの文字色と揃え、通常は `App.TextBrush`、無効時は `App.DisabledTextBrush` に追随します。開閉の双方向の結び付け、内容の表示、アクセシビリティ、ホバー・フォーカス表示と余白を保ちます。

検証対象は同一ウィンドウ内で実際に適用された役割とテンプレート、未採用の部品への非波及です。別々に読み込んだ辞書のオブジェクト同一性、ソース文字列、子要素の順序、固定座標を契約にしません。

### 通常版と省スペース版

`CompactButtonStyle`、`CompactComboBoxStyle`、`CompactSliderStyle` はそれぞれの `App.Canonical` 通常スタイルから寸法だけを変更します。描画テンプレート、意味色、ホバー・押下・無効状態、表示中のテーマ変更を共有します。文字サイズを縮めず、最小寸法と余白で省スペースにします。ComboBoxの候補項目は通常の共通項目スタイルを維持します。

再生メニューの文字ボタン、ランプビューアの「最新」、プレイログの表示対象選択は省スペース版を明示して採用します。選択欄自身が枠と背景を所有し、外側の装飾用Borderを重ねません。再生帯のアイコン操作とランプの「割合／曲数」は、それぞれの役割のスタイルを保ちます。別用途が残る旧Button／ComboBoxの暗黙スタイルは維持し、全画面を一括で変更しません。

設定と再生の全Sliderは、通常版・省スペース版とも同じ軌道・つまみを使います。通常のつまみと進行側は `App.AccentBrush`、未進行側は `App.BorderBrush`、ホバーは `App.AccentHoverBrush`、ドラッグは `App.AccentPressedBrush`、無効なつまみは `App.DisabledTextBrush` です。軌道とつまみに輪郭線を付けず、つまみの部品寸法を描画寸法に揃えて周囲に透明な余白を設けません。用途固有の時刻は共通のつまみへの表示差込みで扱い、Trackを複製しません。

全Sliderは縦横とも `FocusVisualStyle=null` とし、テンプレートにも外周のフォーカス枠やフォーカストリガーを持ちません。フォーカス、Tab移動、方向キー、Home／End、PageUp／PageDown、個別の刻み・値Binding・反転・目盛を維持します。枠抑止のためにフォーカスを移動・解除しません。

### 日付部品の表示

`App.Canonical.DatePickerStyle` は標準DatePickerの入力面・カレンダーボタン・PopupとCalendarを共通テーマへ接続します。24 DIP以上の最小高を使い、省スペースButtonと中央を揃えます。テキスト欄は読取り専用で、`PART_TextBox`、`PART_Button`、`PART_Popup`、Calendarの標準セル・月年移動・キー操作を維持します。Calendarの背景・枠・文字・選択・ホバー・無効状態は動的な意味ブラシで描画します。

表示文化圏は `ResourceService.DisplayCulture` が `Resources.Culture` から都度導出し、未指定時は既存の文字列資源と同じ `CurrentUICulture` を使います。言語を別フィールドや設定へ保存せず、サービスと起動時の文化圏設定の生成順に依存しません。アプリの言語変更は文化圏プロパティも通知します。プロセス・スレッドの文化圏は変更しません。

`DatePickerPresentation` は日付の選択状態に触れず、言語・週開始・短日付の表示だけを更新します。案内文はテンプレートの独立したTextBlockへ表示し、DatePicker.Textへの代入や内部Watermarkの反射操作を使いません。選択済み値から標準PART_TextBoxを直接整形し、同じCalendarの公開更新経路で月・曜日・週開始を再表示します。選択日・表示月・表示モードと取得要求を維持し、表示中の同じPopupとCalendarへ反映します。暦と書式はWPFの標準規則へ揃えます。Languageから具体的な文化圏を解決し、非Gregorian文化圏では利用可能なGregorianのうちLocalizedを優先し、なければ最初の候補を使います。候補がない場合だけ標準と同じInvariantのGregorian書式を使います。追加JSONでth-THやar-SAを選んでも、表示後の標準LostFocusやカレンダーを開く処理で別の日付へ再解析しません。言語の受付範囲は変更しません。

標準DatePickerTextBoxは再有効化時にIsReadOnlyを変更するため、共通表示接続がその[標準の再有効化処理](https://github.com/dotnet/wpf/blob/v10.0.13/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/DatePickerTextBox.cs)の後に読取り専用へ戻します。履歴取得やLoadingからReadyへの復帰でも、文字入力を許可せずカレンダーから選択します。

WPF 10.0.13では、[DatePickerのLanguage変更](https://github.com/dotnet/wpf/blob/v10.0.13/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/DatePicker.cs)は文字列を整形せず、Textの変更は日付を再解析します。標準TextBoxのTextChangedはTextへ無解析で転送するため、確定済み日付の表示更新に使います。[DateTimeHelperの文化圏解決](https://github.com/dotnet/wpf/blob/v10.0.13/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/DateTimeHelper.cs)はLanguageの値の出所も判定するため、この表示接続がLanguageの明示値を所有します。[CalendarのLanguage変更とOnApplyTemplate](https://github.com/dotnet/wpf/blob/v10.0.13/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Calendar.cs)は、DatePickerが週開始をBindingする条件では前者がセル更新を省略するため、後者で同じセルを更新します。内部のキーボード基準日は表示月の日付へ再同期します。選択や月・年表示のモードは変更しません。

### スクロールとフォーカス

縦横のスクロールバーは `App.Canonical.VerticalScrollBarTemplate` / `HorizontalScrollBarTemplate` と `ScrollBarStyle` が方向、軌道、つまみ、無効状態を所有します。`SimpleScrollBar` は互換名として共通テンプレートを参照します。端の矢印ボタンは表示せず、`PART_Track` のページ送りボタン、標準Line・Pageコマンドとアクセシビリティを保ちます。ScrollViewerの部品名、内容スクロール、位置・可視範囲・最大値も維持します。

バーは内容領域へ重ね、表示・非表示の切替で内容の測定幅・高さを変えません。透明な入力領域は縦16 DIP幅・横16 DIP高に固定し、通常は外側の端に丸い3 DIPのつまみだけを示します。入力領域全体へのホバーでつまみを8 DIPへ広げ、軌道を控えめに示します。ドラッグ中は領域外でも8 DIPとドラッグ用ブラシを維持します。通常・ホバー・ドラッグの色には既存の `ScrollBar.Thumb*BackgroundBrush` を使い、ホバーよりドラッグを優先します。内部の描画だけを変え、部品寸法や表の再計算へ波及させません。

つまみと外側の可視端の間には、通常・ホバー・ドラッグを通して2 DIPの空間を設けます。`ScrollBar.Padding` は共通テンプレートの `ThumbChrome.Margin` へ渡す描画設定とし、縦は右、横は下だけに適用します。長手方向には余白を足さず、透明なThumb・Track・ページボタンと16 DIPの入力領域は縮めません。

つまみ長は28 DIP以上を基本とし、それより短い軌道では軌道内に縮めます。両軸表示時の交点は縦バーへ割り当て、横バーを右16 DIPだけ短くして入力を重ねません。不透明な角は置きません。スクロール不要時の非表示は従来の可視性判定を使い、完全自動非表示のタイマーや保存設定は持ちません。メイン画面と表は配置の丸めと画素への整列を有効にします。

標準ScrollViewerの内容は `PART_ScrollContentPresenter` で全面を使います。共通テンプレートの縦横PARTバーだけは `OverlayScrollBar` を使い、表示切替時に論理スクロールの再測定を接続します。WPFの仮想化パネルはバー切替後の再測定を前提としますが、オーバーレイでは内容の測定条件が変わらず再測定が省略され得るためです。`Visibility` の変更かつ `CanContentScroll=true` のときだけ、WPFと同じ選択順で既存の `IScrollInfo` 候補を一つ選び、同じ `ScrollOwner` と既存presenterの子孫であることを確認します。その候補からpresenterまでの中間ラッパーを含む祖先経路だけを再測定対象にし、現在のWPFの測定処理へ戻します。未実体化・対象なし・不適格な候補・物理スクロールは何もせず、別候補への代替、項目走査、追加状態、予約、明示的な測定、hover/drag/Value変更による発火は行いません。

標準ScrollViewerの内容は `PART_ScrollContentPresenter` で全面を使います。`SimpleScrollViewer` も同じ共通テンプレートを実体化時に解決し、辞書の読込順を保ちます。`CanContentScroll`、仮想化、TextBoxの `PART_ContentHost` を変えません。全画面への一律余白は設けず、端の重要な操作対象に対処するときだけ、その画面に表示状態へ連動しない内側余白を使います。表の末尾をバーの手前へ出す範囲計算は[一覧仕様](table-view.md#スクロール領域)に従います。

WPF既定の点線のフォーカス飾りは使いません。表は選択と現在セル、ツリーは選択背景と文字、入力欄は `App.InputFocusBorderBrush`、その他の部品は選択・重ね合わせ・押下状態で示します。分割線はTab移動の対象外にし、ドラッグによるサイズ変更を保ちます。

### メイン画面

メイン画面は背景と文字色をテーマへ接続します。外周は内容の測定範囲を縮めない1 DIPの表示専用枠とし、入力を受け取りません。非アクティブ時は控えめな文字色、アクティブ時は強調色です。最大化時は既存の8 DIPの余白に合わせて内側へ移し、サイズ変更の判定を妨げません。

表2個とサイドバーのツリー2個の右端では、外枠または区切り線1 DIPの内側に2 DIPを残すため、つまみの右描画余白を3 DIPとします。表2個と下側ツリーの下端は、`ProgressHub.HasRows=false` で進捗行が折り畳まれていると外枠に接するため3 DIP、`HasRows=true` で進捗行が見えると外枠に接しないため2 DIPとします。上側ツリーの下端は横分割線がツリー外に描かれるため常に2 DIPです。共通Canonicalを基底とするWindow資源を、表では各Resourcesの暗黙ScrollBar Style、ツリーでは各Resourcesの共通キー別名として適用します。Popupの資源検索とFindAncestorはどちらも配置先へ接続し得るため、MainWindow祖先の存在だけで対象を決めず、Popup祖先を持つバーは共通の縦右2 DIP・横下2 DIPを保ちます。下端3 DIPの条件もPopup外かつ実MainWindowの`HasRows=false`に限り、祖先が見つからない値をfalseへ代替しません。方向・進捗条件は描画だけに反映し、表のSurface・編集領域・終端露出範囲や内容の測定寸法を保ちます。

キャプションボタンは常に控えめな文字色です。通常背景は透明で、`IsMouseOver` のときだけ `App.ControlHoverBrush` を表示します。押下やキーfocusだけでは背景を点灯させず、影・白色光沢・パネル全体の常時背景は付けません。既存位置、標準の `WindowChrome`、`SystemCommands` と専用の入力判定を使い、サイズ変更の判定幅5を保ちます。

再生パネルは操作専用の独立したテーマ資源と同梱画像を使います。操作帯、画像上の文字、seek・音量の共通Sliderと操作ボタンのfocus装飾除去は[再生パネル](playback-panel.md#テーマ画像とフラットな操作表示)に従います。

再生パネルと、その下の左サイドバー・一覧の境界には、下側領域の上端全幅へ1 DIPの線を重ねます。色は左右ペイン間と同じ `App.SeparatorBrush` を使い、テーマ変更へ追従します。表示専用として入力を受け取らず、既存の領域寸法とSplitterの操作領域を維持します。ツールバーと一覧ヘッダーの境界は[一覧側の上枠](table-view.md#外観の更新)が所有します。

ツリーの通常・選択・無効の文字色は、`TextBlock` と `EditableTextBlock` の両方へ明示します。アイコン付きツリー項目の本文は、表示・編集のどちらでも行内の上下中央に揃えます。サイドバーの分割線は操作範囲5、見える線1とし、透明な操作範囲を重ねます。余分な余白でツリーと表、スクロールバーを離しません。

メイン画面右上の通常検索欄とプレイリスト一覧の検索欄は、非フォーカス時の枠にテーマ別の `App.SearchBorderBrush` を使い、入力領域を識別できる控えめな輪郭を示します。全10配色辞書に `SolidColorBrush` として定義し、両検索欄から `DynamicResource` で参照してテーマ切替へ追随します。各検索エディター内にキーボードフォーカスがあるときだけ、枠を既存の `App.InputFocusBorderBrush` へ切り替えます。ヘルプ等の周辺ボタンへのフォーカスはこの判定に含めません。高さ24 DIP、枠線1 DIP、角丸6 DIPとし、横幅、余白、アイコン配置、候補表示、検索操作は保ちます。

両検索欄の背景は、空入力時に `App.ControlBackgroundBrush`、文字があるときに `App.ControlBackgroundActiveBrush` を使い、絞込み中であることを示します。明るいテーマは薄い桃色、暗いテーマは落ち着いた注意色とし、構文警告の `!` とは区別します。候補の操作は検索支援の仕様に従います。

検索欄の絞込み、構文警告、ヘルプ、クリアと、左サイドバーのプレイリスト、フォルダ、検索、パッケージ、処理中の状態は共通アイコン体系へ揃えます。`[NO SONG]` は通常フォルダと同じ表示にせず、未所持の特殊フォルダと分かるフォルダ系アイコンを使います。処理中は通常アイコンを同期アイコンへ置き換えて回転させ、処理状態の判定や寿命は既存の管理主体から変えません。

### 設定画面の構造と即時反映

設定画面の区画には `SettingsSection` を使い、区画間の下余白は `SettingsSectionMargin` で揃えます。区画内の補足を折り畳む `Expander` は共通の `SettingsExpanderStyle` を使います。親が区画間の下余白を持ち、展開内容との間隔は共通テンプレートが持つため、呼出し側で同じ余白を重ねません。

一般ページのLR2パス設定の説明文は、LR2ディレクトリ設定の「詳細」内で個別パスより前に表示します。文字色は設定画面の説明文と同じ `App.DisabledTextBrush` を動的に参照します。

設定画面は左のカテゴリ一覧、見出し、本文、下部の操作から成り、上部タブは使いません。カテゴリ、見出し、下部操作を固定し、選択中の本文だけを一つの縦ScrollViewerでスクロールします。横スクロールは使いません。長い翻訳のラベルや選択文は折り返します。

カテゴリは角を丸めた選択背景と左端の強調線で選択を示し、マウス、キーボード、無効状態も識別可能にします。大きな節を入れ子のカードにせず、節間と内部は8 / 12 / 16 / 24の間隔尺度に揃えます。

テーマは `AppearanceThemeOptions` の表示名と識別子を、表示領域幅に広がる単一の選択欄で扱い、`AppearanceTheme` の双方向の結び付けで選択します。開くたびに作る画面は、表示ツリーへ接続して現在値を受け取ります。未接続の部品へローカル値を設定して結び付けを壊しません。選択時にテーマだけを保存して即時適用し、閉じても保存済みの選択を保ちます。保存失敗では元の確定値と配色を保ちます。

一覧の文字サイズ、行・見出しの高さはスライダーのドラッグ・キー操作の完了時に当該項目だけを保存し、`MainWindowViewSettingsStore` と実際のメイン表へ反映します。別の固定データの見本表を置きません。初期化は三つの値を既定値として保存し、閉鎖時にも保存済みの表示を保ちます。

### 設定用の表示部品

`SettingsSection` は見出し、任意の説明、内容の一つのテンプレートを所有します。有限の高さでは見出しと説明を除く残りを内容へ渡し、自動調整の画面では内容の必要高さを保ちます。人工的な余白や画面ごとの複製を作りません。

`SettingsField`、`SettingsOptionRow`、`SettingsPathPicker`、`SettingsListEditor`、`SettingsStatusBanner` は表示用のプロパティとイベントだけを公開します。サービス探索、業務命令、保存は所有しません。表示へ接続するだけで編集値を変更しません。再生設定は選択中のプレイヤーの詳細だけを表示し、選択変更で非表示のプレイヤーの設定を書き換えません。

`SettingsField` と `SettingsPathPicker` は入力検証の意味を `ValidationStatus` と説明文で受け取ります。`Warning` は `App.WarningTextBrush`、`Error` は `App.ErrorTextBrush` を使い、パス入力と検証対応を明示した入力部品では入力枠にも同じ意味色を反映します。状態は色だけに依存させず、可視の説明文と `AutomationProperties.ItemStatus` / `HelpText` を併用します。正常時は状態と説明文を空にし、説明文が空なら検証文言を表示しません。表示部品自身は Warning / Error の業務上の判定を行いません。

`SettingsField.Header` は実際の入力部品のアクセシビリティ名を補うためにも使います。自身を参照元とする一方向の結び付けを所有し、その結び付け自体で所有を判定します。呼出元の値・別の結び付けは、同じ表示値でも上書きしません。見出し変更や取り外し時も自分が所有するものだけを更新・除去します。

セクションが入力対象を示す場合は、同じ意味の直下見出しを重ねません。説明・検証を持たない単純な囲みは取り除き、実入力へ翻訳済みの名前を明示します。一覧編集と検証欄は `SettingsField`／`SettingsListEditor` の `IsHeaderVisible`（既定true）で表示だけを抑止し、`Header` の意味と入力名、説明、検証状態・文言を保持します。非表示の見出しと専用余白は領域を占有しません。表示文字列の一致や先頭入力かどうかによる自動判定は行いません。具体的な項目名、置換パターンの説明、状態や操作を示すチェックボックス・ボタンの文字は、それぞれの役割として残します。

環境選択は標準RadioButton自身を等幅二列・同高のカードにし、名称を一回、折り返す説明をその下へ表示します。通常設定と初期環境設定は同じ表示を使い、最小ウィンドウ幅で横スクロールを生じません。背景を含む全表面と標準キー入力から、既存の `Click` による確認へ一回接続します。選択はラジオマークとテーマ対応の枠・背景、フォーカスは入力フォーカス色の枠、無効時は薄い表示で示します。既存の一方向の選択Binding、有効条件、確認取消・失敗による復元を保持します。

状態表示は空・空白の文字列を畳み、構造を持つ内容と呼出元の非表示指定を保ちます。内容が戻れば表示を回復します。メッセージ全文をアクセシビリティ名、意味を `ItemStatus`、更新を控えめな通知として公開し、装飾アイコンを重複して読み上げません。音声機器の利用不可理由やテスト結果などの長文だけは、専用の複数行スタイルで幅に合わせて折り返します。

非編集のComboBoxは選択内容・中央・矢印の全体で開けます。編集型は前面の入力欄、カーソル、キー、アクセシビリティ、標準のポップアップを維持します。両方とも開いた時点の実測幅を候補の外側の幅に使い、閉じて幅を変更した後も追随します。内容幅、固定値、初回の値、最小幅だけで代用しません。

初回の「この環境で開始」は強調色の系列を保ち、フォーカスの枠は各強調色と3:1以上の明暗差を持たせます。通常の下部操作は「閉じる」で、BMS・BMT・環境・recordの確定と取消は各セクションに置き、有効なスコア・音声・再生方式・画像の選択は自動保存します。環境は適用可能な差分だけ確定を強調し、差分なしは確定・取消とも無効にします。失敗時だけ必要な再試行を表示します。設定Window内に進捗行は置きません。危険操作とアイコン操作も共通の状態モデルを使い、命令や有効条件を変えません。スライダーは縦横と全ての目盛位置を保ち、目盛は `App.SliderTickBrush` で描画します。

### 標準ウィンドウの題名部

独自の外枠を所有するメイン画面以外のアプリのウィンドウは、XAML・コード生成とも `ThemedWindow` を使います。標準のWPF題名部を保ち、ハンドルの作成後に現在のテーマと題名・文字・境界の色をDWMへ要求します。表示中の変更へ追随し、終了時に購読を解除します。

OSや属性の非対応、API不在、HRESULTの失敗は標準のWindows表示を使います。一方、リソース読込みや接続処理の予期しない例外は隠しません。初回接続中なら購読・接続状態・ハンドルを戻して同じ管理器で再試行できるようにし、接続後のテーマ変更中なら接続を維持して後続の変更と終了を可能にします。購読解除は一回だけです。

### ダイアログの表示面

標準ウィンドウでは `App.Canonical.NativeWindowContentStyle` が外周の間隔だけを所有します。内側へもう一つ塗りつぶし・丸い境界・カードを重ねません。設定画面の本体は標準の表示領域の端まで延ばし、内部の機能ごとの間隔は保ちます。

メイン画面内に重ねる初回言語選択とプレイリストURL入力は、`DialogOverlayStyle` と `DialogContentStyle` を明示して使います。標準ウィンドウへこの重ね表示用の外面を流用しません。OSのファイル・フォルダ選択と緊急ダイアログは対象外です。所有元、モーダル結果、取消、終了の抑制、処理と寿命の所有は表示部品の統一で変えません。

通常運用の `ThemedMessageBox` は質問、情報、警告、失敗を共通アイコン体系で表示し、質問・情報は強調色、警告は警告色、失敗は失敗色を使います。緊急経路のOSネイティブダイアログはこの統一対象に含めません。

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
| 共通オーバーレイ、固定入力幅、細いつまみ、標準操作 | `CanonicalControls.xaml` のScrollBar・ScrollViewer、`Simple Styles.xaml` の互換名 | [`MainWindowContextMenuResourceTests.CanonicalScrollViewer_MaterializesSharedScrollBarsAndPreservesBehavior`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowContextMenuResourceTests.cs) はcompiled部品の16 DIP入力幅・通常3 DIP・丸み・短い軌道内への収まり、交点の非重複、なし/縦/横/両軸で内容領域一定、Lineコマンドの移動・ページボタンのInvoke・Scroll Automation・offset/value・viewport/maximum同期・無効表示を確認する。hover/dragはcompiled条件・8 DIP setterと優先順位を確認し、ブラシ・軌道の状態と固定配置の接続は静的確認する。 |
| つまみと可視端の間隔、メイン画面の外枠・区切り線補正 | 共通ScrollBarのPaddingと `ThumbChrome.Margin`、`MainWindow.xaml` の局所ScrollBar Style | 上記のcompiled寸法・標準操作と既存MainWindowChartPresentationWpf/CustomTable系列を自動回帰に使う。横断方向だけの描画余白、通常/hover/dragで共通の接続、共通2 DIP・右端3 DIP・下端の進捗条件による3/2 DIP、上側ツリー下端2 DIP、Styleの優先順位と辞書の非循環・MainWindow祖先とPopup祖先による適用条件、16 DIP入力と内容寸法・終端露出計算の不変は静的差分確認で分担する。実ポインター操作や配色・高DPIでの見え方は自動検証済みとは扱わない。 |
| オーバーレイの論理スクロール再測定、リサイズ時の仮想化維持 | [`OverlayScrollBar`](../../../BeMusicSeeker/Views/OverlayScrollBar.cs)、共通ScrollViewerのPARTバー | [`DialogPresentationTests.PlayHistoryPresetDialog_UsesBoundedResizablePlaylistViewport`](../../../BeMusicSeeker.Tests/Dialogs/DialogPresentationTests.cs) は未実体化の末尾へスクロールして選択した後、Window拡大で論理viewportが増えることを確認する。物理・論理の標準操作とItemsPresenterのconsumerは既存MainWindowContextMenuResource/SettingsControlPresentation/DialogPresentation系列へ分担し、候補の選択順・不適格時の無処理・祖先経路だけの無効化・状態なしと発火条件は静的確認する。 |
| 表の共通バーとテーマ切替、交点 | `CustomTableView`、共通ScrollBarテンプレート | [`MainWindowChartPresentationWpfTests.CustomTableScrollBarsUseCompiledThemeAndOverlayContract`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs) は実テーマ切替の表・実バーへの反映と、全面寸法・交点の非重複を確認する。設定・ダイアログの実採用は既存 `SettingsControlPresentationTests` / `DialogPresentationTests` のconsumer検査へ分担する。OS入力、領域外ドラッグの実操作、配色の見え方と高DPIは実機確認と分担する。 |
| テーマの切替と共通スタイルの保持 | [`AppThemeService`](../../../BeMusicSeeker/Models/Settings/AppThemeService.cs) | [`NativeWindowThemeContractTests`](../../../BeMusicSeeker.Tests/Themes/NativeWindowThemeContractTests.cs) は各配色への切替で、辞書のキー整合、共通スタイルの解決、変更通知と版更新、題名部の明暗判定を確認する。 |
| 通常検索・プレイリスト一覧検索の控えめな通常枠、フォーカス枠、入力状態の背景と配置 | [`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) の `KeywordSearchComposite` / `PlaylistSummaryKeywordSearchComposite`、全10配色辞書の `App.SearchBorderBrush` | `NativeWindowThemeContractTests` は全テーマのキー整合、[`MainWindowChartPresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs) は検索操作とアイコン配置を回帰確認する。全10辞書の検索枠ブラシのキー・型・配色、両通常枠の動的参照、エディター専用のフォーカス判定、背景トリガーと寸法・余白の維持は変更時の静的差分確認で分担する。実環境での色の見え方は自動検証の対象に含めない。 |
| 全ToolTipの表示中テーマ追随 | `CanonicalControls.xaml` の型スタイル | `NativeWindowThemeContractTests.ToolTip_VisibleContentTracksThemeWithoutExplicitStyle` はIsOpenで表示した明示StyleなしToolTipで明→暗→明の実効背景・枠・本文、文字列と折返しTextBlockの文字継承・内容保持を確認する。文字列からの自動生成、CustomTableViewの明示生成、SliderのAutoToolTipが同じ型スタイルを使う接続は静的確認で分担する。 |
| テーマの切替、標準題名部の接続・解除と失敗 | [`AppThemeService`](../../../BeMusicSeeker/Models/Settings/AppThemeService.cs)、[`ThemedWindow`](../../../BeMusicSeeker/Views/ThemedWindow.cs) | [`NativeWindowThemeContractTests`](../../../BeMusicSeeker.Tests/Themes/NativeWindowThemeContractTests.cs)、[`NativeWindowTitleBarTests`](../../../BeMusicSeeker.Tests/Themes/NativeWindowTitleBarTests.cs) |
| 共通Expander見出しの背景・枠・文字・矢印と動的テーマ切替 | [`CanonicalControls.xaml`](../../../BeMusicSeeker/Themes/CanonicalControls.xaml) の `App.Canonical.ExpanderHeaderToggleStyle` / `ExpanderHeaderToggleTemplate`、[`SettingsControls.xaml`](../../../BeMusicSeeker/Views/Settings/SettingsControls.xaml) の `SettingsExpanderStyle` | [`SettingsControlPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsControlPresentationTests.cs) の `SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes` は同じ表示済みExpanderで明→暗→明の背景・1 DIPの枠と、折畳み・展開・無効時の文字・矢印の実効色、双方向開閉、内容表示と `IExpandCollapseProvider` を確認する。ホバー・フォーカスの表示と有効条件、余白は既存トリガーとテンプレートの差分確認で維持を確認する。 |
| 日付部品の表示言語・テーマと値の保持 | [`DatePickerPresentation`](../../../BeMusicSeeker/Views/DatePickerPresentation.cs)、[`ResourceService`](../../../BeMusicSeeker/ViewModels/ResourceService.cs)、共通DatePicker／Calendarスタイル | `PlaylistLampViewerWindowPresentationTests.Viewer_datePresentationTracksApplicationCultureWithoutChangingTheQuery` は未選択／選択済みの同じ実PART・開いたPopup、サービス生成順、OS側との文化圏不一致、同梱言語と非Gregorianの追加言語・明暗テーマで表示更新、標準の再解析経路を通した日付通知・取得・表示モードの不変を確認する。翻訳のキー集合は `LocalizationResourceParityTests` が担う。 |
| 通常／省スペースの共通描画、Sliderの縦横・反転・目盛・枠抑止・キー相当入力とテーマ追従 | [`CanonicalControls.xaml`](../../../BeMusicSeeker/Themes/CanonicalControls.xaml) | `SettingsControlPresentationTests.CanonicalControls_CompactVariantsSharePresentationAndPreserveSliderInput` は実部品の共通テンプレート・相対寸法・意味色・一回の命令と標準Sliderコマンドを確認する。`SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes` は通常／省スペースの全表面、編集入力と候補幅を確認する。OSの実Tab・画像上の視認性・異なるDPIは実機推奨へ分担する。 |
| 設定の表示部品、検証状態、アクセシビリティ、候補幅、項目保存と即時反映 | [`SettingsWindow`](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs)、[`SettingsField`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs)、[`SettingsPathPicker`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs)、[`SettingsSection`](../../../BeMusicSeeker/Views/Settings/SettingsPresentationControls.cs) | [`SettingsWindowPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowPresentationTests.cs) の `SettingsPages_RequiredValidationBindingsUseSharedWarningAndErrorPresentation`、`SettingsValidationPresentation_ExposesWarningAndErrorWithoutRelyingOnColorAlone`、および既存の表示・アクセシビリティ検査、[`SettingsWindowCompiledBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsWindowCompiledBehaviorTests.cs)、[`SettingsControlPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsControlPresentationTests.cs)、[`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) |
| 環境カードの二列・同高・名称一回・全表面入力・言語／テーマ追従と選択不可 | `GeneralSettingsPage.xaml`、設定辞書の `SettingsEnvironmentRadioStyle` | `SettingsWindowPresentationTests.SettingsWindow_EnvironmentCardsFitAndKeepWholeSurfaceInputAcrossLanguages` は通常／初期設定と全6言語・最小幅で実測し、同じカードへの明暗テーマ反映とライブラリ操作中の無効化・復帰を確認する。明示した選択状態とClickから確認・取消への接続は `SettingDialogEditCompletionTests.OperationModeRadio_*`、標準キーとフォーカス枠の接続は型・イベント・テンプレートの静的確認、実Space・Tab入力と異なるDPIの見え方は実機推奨へ分担する。 |
| 見出し抑止と入力名・説明・検証・占有領域の保持 | `SettingsField.IsHeaderVisible`、各設定ページ | `SettingsWindowPresentationTests.SettingsField_HiddenHeaderPreservesMeaningDescriptionAndValidationWithoutEmptySpace` はField／ListEditorの実テンプレートを測定する。既存の `SettingsField_*` は可視／非表示の両方で、明示名・別Binding・所有の引取り・取り外しを確認する。実ページの採用と入力名は `SettingsPages_HeadingsAppearOnceAndEditorsKeepLocalizedAutomationNames`、検証配送と全ページ最小幅は既存ケースが担う。 |
| ダイアログの表示面、標準メニュー、結果とサイズ | [`ThemedMessageBox`](../../../BeMusicSeeker/Views/ThemedMessageBox.cs) | [`DialogPresentationTests`](../../../BeMusicSeeker.Tests/Dialogs/DialogPresentationTests.cs)、[`ThemedMessageBoxTests`](../../../BeMusicSeeker.Tests/Dialogs/ThemedMessageBoxTests.cs)、[`UiDialogCoordinatorWpfTests`](../../../BeMusicSeeker.Tests/Dialogs/UiDialogCoordinatorWpfTests.cs) |
| 表とツリーへの配色・表示、共通アイコンの描画 | [`CustomTablePalette`](../../../BeMusicSeeker/Views/CustomTable/CustomTablePalette.cs)、[`AppFontIcon`](../../../BeMusicSeeker/Views/AppFontIcon.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowChartPresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs)、[`MainWindowTreePresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowTreePresentationWpfTests.cs) |
| 実キャプションからの状態変更と終了 | [`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowViewHostTests.MainWindowPlayerDrainKeepsDispatcherResponsiveAndDefersTerminalClose`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewHostTests.cs) は同じ実Windowで最大化・復元・最小化をAutomation Invokeから実状態へ照合し、通常・更新準備済みのCloseを同じキャプションから受付、player排出、UI保存、実Closedまで確認する。`MainWindowPlaybackWpfTests.PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner` は通常の透明な背景と影なしを実templateで確認する。hoverだけの背景接続と押下・focusだけでは点灯しないことは静的確認する。OSポインターによるhit-testとサイズ変更はこの自動検証の対象に含めない。 |

## 関連資料

[ダイアログの受付](dialogs.md)、[一覧表示](table-view.md)、[検索支援](keyword-search.md)、[設定と保存](../runtime/settings.md)、[表示テスト](../development/testing.md)を参照します。
