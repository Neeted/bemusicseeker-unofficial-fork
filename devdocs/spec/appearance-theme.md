# 外観テーマ設定 現行仕様

## 概要

BeMusicSeeker は外観テーマとして `Light` / `Dark` を持つ。既定は `Light` で、設定ウィンドウの `外観` カテゴリから変更できる。

テーマはアプリ全体の配色リソースを差し替える仕組みであり、現時点では主にメイン画面、左ツリー、CustomTableView、検索欄、ステータスバー、スクロールバー、コンテキストメニュー、標準 control、設定ウィンドウ、主要なアプリ内ダイアログを対象にしている。CustomTableView のフォントサイズ、行高、ヘッダー高は配色テーマとは別の外観設定として扱う。

## 設定値

- 設定名: `Settings.Default.AppearanceTheme`
- 値:
  - `Light`
  - `Dark`
- 不明値、空値、`null` は `Light` に正規化する。
- 保存前にも正規化し、設定ファイルに不明値を残さない。

設定ウィンドウでは、選択肢の `ItemsSource` を安定したリストとして保持する。キャンセル時や再表示時に ComboBox の選択が空にならないよう、設定値と UI 選択状態を再同期する。

## テーマ適用

`AppThemeService` が現在のテーマ ResourceDictionary を管理する。

- 起動時:
  - `Settings.Default.AppearanceTheme` を正規化する。
  - `Themes/Light.xaml` または `Themes/Dark.xaml` を `Application.Resources.MergedDictionaries` へ追加する。
- 変更時:
  - 既存の application-relative `/Themes/*.xaml` と component-qualified `/BeMusicSeeker;component/Themes/*.xaml` を同じテーマ辞書として認識して削除する。
  - 新しいテーマ辞書は resource owner を明示する `/BeMusicSeeker;component/Themes/{theme}.xaml` で追加する。
  - `ThemeChanged` を発火する。
  - `Version` を進める。

テーマリソースは `DynamicResource` を基本に参照する。コード描画の CustomTableView などは `ThemeChanged` / `Version` を見て内部 palette cache を更新する。

## Resource Key

テーマ辞書は `Themes/Light.xaml` と `Themes/Dark.xaml` にある。主な key は以下。

### App 系

- `App.BackgroundBrush`
- `App.SurfaceBrush`
- `App.ControlBackgroundBrush`
- `App.ControlBackgroundActiveBrush`
- `App.ControlHoverBrush`
- `App.ControlSelectedBrush`
- `App.ControlPressedBrush`
- `App.TextBrush`
- `App.SubtleTextBrush`
- `App.DisabledTextBrush`
- `App.BorderBrush`
- `App.StrongBorderBrush`
- `App.SeparatorBrush`
- `App.InputFocusBorderBrush`
- `App.AccentBrush`
- `App.AccentForegroundBrush`
- `App.AccentFocusRingBrush`
- `App.AccentHoverBrush`
- `App.AccentPressedBrush`
- `App.DangerBrush`
- `App.DangerForegroundBrush`
- `App.DangerHoverBrush`
- `App.DangerPressedBrush`
- `App.ErrorTextBrush`
- `App.SuccessTextBrush`
- `App.SliderTickBrush`
- `App.AccentSubtleBrush`
- `App.WarningTextBrush`
- `App.DialogOverlayBrush`
- `App.DialogBackgroundBrush`
- `App.DialogBorderBrush`
- `App.PopupBackgroundBrush`
- `App.MenuSelectedBackgroundBrush`
- `App.MenuSelectedTextBrush`
- `App.SearchGlyphBrush`

### Table 系

- `Table.HeaderBackgroundBrush`
- `Table.RowBackgroundBrush`
- `Table.AlternatingRowBackgroundBrush`
- `Table.WarningRowBackgroundBrush`
- `Table.SelectedRowBackgroundBrush`
- `Table.CurrentCellBackgroundBrush`
- `Table.CurrentCellTextBrush`
- `Table.ReorderSourceHeaderBrush`
- `Table.SortGlyphBrush`
- `Table.GridLineBrush`
- `Table.HeaderGridLineBrush`
- `Table.ColumnReorderInsertBrush`
- `Table.CheckBoxBackgroundBrush`
- `Table.CheckBoxBorderBrush`
- `Table.CheckBoxCheckBrush`
- `Table.TextBrush`
- `Table.SelectedTextBrush`
- `Table.SubtleTextBrush`
- `Table.UndefinedCellBackgroundBrush`

### ScrollBar 系

- `ScrollBar.TrackBackgroundBrush`
- `ScrollBar.ThumbBackgroundBrush`
- `ScrollBar.ThumbHoverBackgroundBrush`
- `ScrollBar.ThumbDraggingBackgroundBrush`
- `ScrollBar.ThumbBorderBrush`
- `ScrollBar.ButtonBackgroundBrush`
- `ScrollBar.ButtonHoverBackgroundBrush`
- `ScrollBar.ButtonPressedBackgroundBrush`
- `ScrollBar.ButtonBorderBrush`
- `ScrollBar.ArrowBrush`

### TreeView / Simple Styles 互換

`TreeViewItem.TreeArrow.*` と Simple Styles 用の `NormalBrush` / `MouseOverBrush` / `PressedBrush` などもテーマ辞書側で定義している。既存 control template がこれらを参照するため、ライト/ダーク両方で定義を持つ。

ScrollBar の共有描画 primitive は `App.Canonical.VerticalScrollBarTemplate` / `App.Canonical.HorizontalScrollBarTemplate` とし、`App.Canonical.ScrollBarStyle` がそれぞれの向き、`PART_Track`、line/page command、thumb、終端 affordance、disabled state を所有する。`SimpleScrollBar` は既存 consumer との互換 alias としてこの canonical template を解決し、標準 `ScrollBar` の implicit style としても適用する。CustomTableView の縦横スクロールバーも標準 `ScrollBar` を使うため、MainWindow と同じ primitive / theme key 経由で描画される。既存の `PART_VerticalScrollBar` / `PART_HorizontalScrollBar`、`CanContentScroll`、offset / viewport / maximum、Automation peer の contract は各 ScrollViewer template で維持する。

## 標準 Control / Menu

canonical resource の依存関係は `App -> CanonicalDialogStyles -> CanonicalControls` とする。`App.xaml` は `Themes/CanonicalDialogStyles.xaml` を application-level ResourceDictionary としてマージし、dialog facade が `CanonicalControls.xaml` を所有する。canonical control / dialog role は `App.Canonical.*` の明示 key で所有し、application-wide の type-key implicit style として新たに公開しない。これにより、canonical resource を導入しても、明示的に採用していない MainWindow の control へ Settings / dialog 用 template が波及しない。

`Simple Styles.xaml` に残る既存の互換用 implicit style は、今回の canonical owner とは別の既存 route として扱う。`SettingsWindow` と `Lr2AdvancedPathsDialog` は `SettingsControls -> CanonicalDialogStyles -> CanonicalControls` の順で Settings compatibility alias を解決し、それ以外の custom dialog は `CanonicalDialogStyles -> CanonicalControls` を直接採用する。SettingsControls は `SettingsButtonStyle` などの互換 key と Settings-local の implicit default を保持し、それぞれを対応する `App.Canonical.*` style に `BasedOn` で明示的に接続する。設定画面と custom dialog は、同一 host の実際の visual tree で canonical role と effective template が同等であることを満たす。別個にロードした ResourceDictionary の CLR object identity を契約にしてはならず、canonical dictionary を SettingsControls や各 dialog へ複製してはならない。

対応済みの主な control:

- `Button`
- `TextBox`
- `ComboBox` / `ComboBoxItem`
- `CheckBox`
- `RadioButton`
- `Label`
- `GroupBox`
- `TabControl` / `TabItem`
- `Expander`
- `Menu`
- `ContextMenu`
- `MenuItem`
- `Separator`

Playlist Property の上部カテゴリ navigation は、`App.Canonical.TopNavigationStyle`、`App.Canonical.TopNavigationItemStyle`、`App.Canonical.TopNavigationContentStyle` の keyed role で構成する。navigation は General / Folder / Custom Folder の single-selection ListBox として WPF の Selection / SelectionItem Automation peer および標準 keyboard route を維持し、選択 item は下端 indicator、hover / focus / disabled state を表示する。本文は navigation と別の content host とし、canonical role は未採用の application-wide implicit style へ漏らさない。

`SimpleMenuItem` は `SystemColors.*` ではなく、`App.PopupBackgroundBrush` / `App.TextBrush` / `App.ControlHoverBrush` / `App.MenuSelectedBackgroundBrush` / `App.MenuSelectedTextBrush` / `App.DisabledTextBrush` / `App.BorderBrush` を使う。MainWindow の column header など局所 `MenuItem` style は implicit style を `BasedOn` で継承する。

`Label` / `TextBox` / `ComboBox` の標準 style は、フォーム行の高さに対して内容が上下中央に見えることを既定とする。個別画面で上寄せが必要な場合だけ明示 override する。

`ProgressBar` はライト / ダークの配色差を付けず、従来のステータスバーに近い共通の緑色 indicator と薄い track を使う。進捗ゲージは状態表示であり、テーマ accent 色の一部としては扱わない。

## Focus 表示

アプリ内では、WPF 既定の点線 `FocusVisualStyle` は使わない。キーボード操作時の状態は、各 control の既存 visual で表現する。

- `CustomTableView`: 選択行と current cell の独自描画で表現する。
- `TreeView` / `TreeViewItem`: control 本体の点線 adorner は出さず、選択背景と選択時文字色で表現する。
- `TextBox`: `App.InputFocusBorderBrush` による入力枠で表現する。
- `Button` / `ToggleButton` / `CheckBox` / `RadioButton` / `TabItem` / `Slider` / `ListBoxItem` / `ComboBoxItem`: 点線 adorner は出さず、選択・hover・pressed の visual に任せる。
- `GridSplitter`: Tab focus 対象にせず、点線 adorner も出さない。ドラッグ操作によるサイズ変更は維持する。

## MainWindow

メインウィンドウは `Background` / `Foreground` をテーマ resource へ接続する。

ウィンドウ外周は、content の measure を縮めない 1 DIP の表示専用 overlay frame とする。frame は `IsHitTestVisible=False` で、非アクティブ時は `App.SubtleTextBrush`、アクティブ時は `App.AccentBrush` を `DynamicResource` で使用する。最大化時は既存の `windowBorder` の 8 DIP margin と同じ座標系で内側へ移動し、native resize border の hit test を妨げない。

キャプションボタンの通常色はウィンドウの active / inactive にかかわらず `App.SubtleTextBrush` とする。accent 色はキャプションボタンではなく active window の外周に使う。chrome は native `WindowChrome`、`SystemCommands`、`WindowChrome.IsHitTestVisibleInChrome` を使用し、退役済みの MetroRadiance / `MetroChromeBehavior` / Expression / Interactivity 依存は使用しない。`ResizeBorderThickness=5` はリサイズ操作の hit target として維持する。

対応済みの主な領域:

- 左サイドバー背景
- プレイリストツリー / ライブラリツリー
- ツリー開閉アイコン
- ツリー選択色と選択時文字色
- 一覧上部ラベル、検索欄、検索候補 popup
- ステータスバー
- サイドバー separator / splitter
- スクロールバー
- CustomTableView

ツリーの子ノードは `TextBlock` と `EditableTextBlock` が混在するため、通常時 `App.TextBrush`、選択時 `Table.SelectedTextBrush`、無効時 `App.DisabledTextBrush` を明示している。

サイドバー splitter は、操作しやすいように 5px の hit target を維持しつつ、見た目は 1px の `App.SeparatorBrush` 線だけを描画する。縦 splitter は sidebar 側の右端に透明 overlay として置き、線は hit target の右端に描画する。上下 splitter は layout 上の separator row を 1px にし、5px の hit target を隣接 TreeView 上へ overlay する。これによりツリーのスクロールバーと separator、separator と一覧画面の両方に余白が出ないようにする。

CustomTableView の内蔵スクロールバーは 15px の厚みに統一し、control 背景と縦横スクロールバー同時表示時の右下 corner を `ScrollBar.TrackBackgroundBrush` で埋める。これにより横スクロールバーとステータスバーの間、またはスクロールバー交差部分に背景色の隙間が出ないようにする。

メインウィンドウと CustomTableView は layout rounding / device pixel snap を有効にし、1px separator やスクロールバー境界が fractional DPI で半端な背景色を残さないようにする。通常の `ScrollViewer` も縦横スクロールバー交差 corner を `ScrollBar.TrackBackgroundBrush` で埋める。通常の `ScrollViewer` 内の vertical / horizontal `ScrollBar` は control 自体の `Width` / `Height` も 15px に固定し、template 内の 15px track と実測 control 幅がずれて余白を作らないようにする。

スクロールバーの thumb は縦横とも track 内で中央寄せにする。sidebar の隙間対策は thumb の寄せではなく、`gridTreePane` の余白を持たせないことと、splitter を一覧側 overlay に置くことで行う。

検索欄は入力文字列がある場合に `App.ControlBackgroundActiveBrush` を使う。これは一覧が keyword filter 済みであることを示す注意色で、ライトモードでは従来の `LightPink` 相当、ダークモードでは暗色 palette に馴染む muted color とする。構文警告の `!` 表示とは別の状態表示である。

## CustomTableView

CustomTableView は WPF 標準 control template ではなく独自描画のため、`CustomTablePalette` を使って theme resource を Brush/Pen へ変換する。

- `CustomTablePalette.Current` は `AppThemeService.Version` ごとに cache される。
- `AppThemeService.ThemeChanged` で palette と描画 cache を破棄する。
- 行背景、交互行、警告行、選択行、current cell、header、grid line、checkbox、sort glyph を palette から描画する。
- スコア列などで使う `CustomTableScoreBrushProvider` も palette 経由の色を返す。

ライトモードの薄い選択色では黒文字を維持する。ダークモードの選択色では白文字を使う。

## 設定ウィンドウ

独立した modal `SettingsWindow` の `外観` category control にテーマ選択 ComboBox がある。設定ウィンドウは左navigationと選択カテゴリの本文を持ち、上部tabは使用しない。

- 選択肢: ライトモード / ダークモード
- Light / Dark の各 choice radio は `IsLightAppearanceTheme` / `IsDarkAppearanceTheme` を介して `AppearanceTheme` に TwoWay binding する。
- キャンセル時は保存済みのテーマへ戻し、即時に `AppThemeService.ApplyTheme()` を呼ぶ。
- 設定ウィンドウはopenごとに生成する。Appearance category が visual tree へ接続され共有 `DataContext` を継承した時点で、通常の TwoWay binding が choice radio を現在設定へ同期する。off-tree control へローカル値を設定して binding を置換してはならない。表示中のtheme変更は同じWindowへ反映する。
- 一覧のフォントサイズ、行高、ヘッダー高は slider の変更ごとに `Settings.Default` と `MainWindowViewSettingsStore` へ通知し、メイン画面の実際の `CustomTableView` で即時 preview する。設定画面内に固定データの代替一覧 preview は置かない。
- 一覧外観の reset は3値を既定値へ戻す。Cancel は保存済み snapshot の3値を復元し、表示中のメイン一覧にも復元結果を即時反映する。

最初の5カテゴリは `SettingsSection` / `SettingsField` / `SettingsOptionRow` / `SettingsPathPicker` / `SettingsListEditor` / `SettingsStatusBanner` を共有する。major section は入れ子 card にせず、field label は editor の上へ置く。path は read-only と editable を明示し、status は icon と text の両方で意味を伝える。`SettingsStatusBanner` は null、空文字、空白だけのstring contentをcollapseし、non-string contentとcallerが指定した非表示状態を保持する。content bindingが非blankへ戻れば表示を回復し、AutomationではmessageをName、semantic statusをItemStatus、更新をPolite live regionとして公開する。視覚上のiconは同じ意味を重複して読むstandalone Automation elementを作らず、callerがContentへ渡したstructured contentはそのまま維持する。presentation control は dependency property / routed event だけを公開し、service lookup、domain command、永続化を所有しない。

`SettingsField.Header` は装飾用containerではなく、内包する実際の `TextBox` / `ComboBox` / `ListBox` / `Slider` Automation peerのfallback accessible nameとして公開する。fallbackはSettingsField自身をSourceとするone-way bindingで付与し、そのBindingExpressionのidentityでownershipを追跡する。editorにcaller-ownedのlocal値または別bindingがある場合は、表示値がfallbackと同じでも上書きせず、Header変更を挟まない即時detachを含めcaller値をclearしない。Header変更時もSettingsField自身が所有するbindingだけを更新する。最初の5カテゴリはpage attachだけでdraftを変更しない。再生カテゴリは選択中playerの詳細だけをvisual treeへ提示し、radio操作は選択flagだけを変更して非選択playerの隠れたpath/settingsを変更しない。

設定ウィンドウと10個の category control は `Views/Settings/SettingsControls.xaml` の compatibility alias / Settings-local adoption を共有する。Button、TextBox、ComboBox / ComboBoxItem、CheckBox、RadioButton、Slider、ScrollViewer / ScrollBar、ListBox / ListBoxItem、Expander、navigation の generic template と state は application-level `App.Canonical.*` resource が所有し、Settings 側は既存 key を同じ canonical object へ `BasedOn` で接続する。内部 popup、item container、content host、scrollbar も canonical explicit resource を解決し、未採用の application implicit styleへ漏らさない。標準 control の keyboard / Automation peer を維持し、`PART_ContentHost`、`PART_Popup`、`PART_EditableTextBox`、`PART_Track`、ScrollViewer parts、Expander `HeaderSite` を欠落させない。

設定内の非編集 `ComboBox` は選択内容、中央部、矢印を含む表示面全体で dropdown を開ける。編集可能 `ComboBox` は `PART_EditableTextBox` を前面の入力面として維持し、文字入力、caret、keyboard、Automation と `PART_Popup` の標準経路を保持する。editable / noneditable とも、popup の presentation root 幅は open 時点の ComboBox `ActualWidth` と同じ DIP 値でなければならない。close 後に ComboBox を resize して再 open した場合も、新しい `ActualWidth` へ追随する。固定幅、item 内容幅、`MinWidth` のみ、または最初の open 時の幅を cache する実装は契約違反とする。

通常の `SettingsStatusBanner` は compact な表示と caller が渡した structured content を維持する。Audio device の利用不可理由とテスト結果のような長い文字列だけは keyed multiline style を使用し、利用可能な本文幅で折り返して高さを自動拡張する。折り返し後も message 全文を Automation Name、semantic status を ItemStatus、更新を Polite live region として公開し、装飾 icon は Automation tree に出さない。

- 左navigationの項目は rounded pill と左端のaccent indicatorで選択を示す。hover、keyboard focus、disabledもそれぞれtheme resourceで識別可能にする。
- 設定groupは `App.ControlBackgroundBrush` / `App.BorderBrush` を使う rounded cardとし、カテゴリheader、card間、card内は `8 / 12 / 16 / 24` pxのspacing scaleへ揃える。
- button は `SettingsButtonStyle` を共通 template とし、primary / quiet / danger / icon の各styleを同じ state modelから派生させる。Save and closeは accent familyを通常、hover、pressed、disabled、focusの全状態で維持し、primaryのkeyboard focusは各accent stateと3:1以上のcontrastを持つ `App.AccentFocusRingBrush` で示す。Cancelはquiet actionとする。buttonのcommand/click、enabled、focus behaviorは既存contractを変えない。
- label / value / actionの設定行はflexible Gridで構成し、長いcultureのlabelとcheckbox textは折り返す。Window shellが所有する単一の縦ScrollViewerで選択中の本文だけをscrollし、navigation、header、footerは固定する。横scrollは使用しない。
- 設定 visual tree と template は legacy の `NormalBrush` / `MouseOverBrush` / `PressedBrush` / `PressedBorderBrush` / `DefaultedBorderBrush` や table 専用 brush を参照せず、固定色も持たない。
- Slider は horizontal / vertical orientationと `None` / `TopLeft` / `BottomRight` / `Both` のtick placementをtemplateで保持し、tickは `App.SliderTickBrush` で描画する。
- アプリが所有する標準 native window は `ThemedWindow` を共通 base とし、標準 WPF title bar を残す。native handle 確定後に current theme の dark-mode flag と `App.DialogBackgroundBrush` / `App.TextBrush` / `App.BorderBrush` 由来の caption / text / border color を DWM へ要求し、theme変更時に同じ生存中の window へ再適用する。window close で theme change の購読を解除する。未対応OS、未対応attribute、API不在、HRESULT失敗では例外を外へ出さず、Windowsのsystem fallbackをそのまま使う。
- title bar の初回 attach は transactional とする。theme resource の読み取りまたは native gateway の予期しない失敗時は、theme change 購読、attached 状態、window handle をすべて巻き戻して例外を伝播し、同じ controller で再試行できるようにする。attach 完了後の theme change 適用で予期しない失敗が起きた場合は接続を維持したまま例外を伝播し、後続の theme change と dispose を有効に保つ。dispose は購読を一度だけ解除する。
- `MainWindow` だけは独自の `WindowChrome`、caption button、hit-test を所有するため `ThemedWindow` の対象外とする。ほかの production `Window` を追加するときは XAML/code-only のどちらでも `ThemedWindow` から派生し、この例外集合を増やさない。

## アプリ内ダイアログ

アプリが描画する主要なダイアログは `App.DialogOverlayBrush` / `App.DialogBackgroundBrush` / `App.DialogBorderBrush` / `App.TextBrush` を参照する。

`ThemedWindow` へ移行済みの native window:

- `SettingsWindow`
- `ReleaseNotesWindow`
- `Settings/Lr2AdvancedPathsDialog`
- `UpdateAvailableDialog`
- `PendingDeleteConfirmDialog`
- `PlayHistoryFolderDisplayPresetEditDialog`
- `Lr2PlayHistorySchemaUninstallDialog`
- `PlaylistPropertyDialog`
- `PlaylistSummaryBulkEditDialog`
- `Parago/Windows/ProgressDialog`
- code-only で構築する `ThemedMessageBox`

これらは HWND 作成時に title bar theme を attach し、表示中の theme 変更を live 反映し、close 時に detach する。owner、modal `ShowDialog` / `DialogResult`、close/cancel、各 view model や worker の lifetime ownership は従来どおり各 dialog が保持する。`ProgressDialog` の `HideCloseButton` と busy 中の close 抑止も変更しない。

上記の native dialog と code-only MessageBox は、`ThemedWindow` が所有する単一の native client surface 上へ、塗りつぶし、border、corner radius を持たない `App.Canonical.NativeWindowContentStyle`（content spacing 専用 role）を必要に応じて明示的に採用する。これにより native window の client 全体へ内側の rounded / bordered surface を重ねない。button の affirmative / quiet / danger role は `App.Canonical.Dialog*ActionStyle` で表現し、入力 control、一覧、group、label、scroll viewer も対応する `App.Canonical.*Style` を各 view で明示する。`DialogContentStyle` は rounded `DialogSurfaceStyle` を継承する overlay 専用 role とし、native window の outer content には使用しない。`SettingsWindow` は shell / sidebar を native client edge まで伸ばし、feature content 側の spacing は保持する。`SettingsWindow` と `Lr2AdvancedPathsDialog` の既存 `Settings*` button / control key は SettingsControls の canonical `BasedOn` alias として維持する。検証は同一 host に実コンストラクタで生成した view の適用済み role / effective template と外側 sentinel の隔離を対象とし、別 load 間の CLR identity、座標、子順、source text を要求しない。これにより、canonical resource の導入で未採用の MainWindow や別画面へ implicit style が波及せず、owner、modal result、既定値、cancel、close、gate、永続化、lifetime の既存契約も変更しない。

`InitialSetupLanguageDialog` と `LoadPlaylistURIDialog` は MainWindow の content 内で背景を覆うため、引き続き overlay とする。これらの主要な描画面は `App.DialogOverlayBrush` / `App.DialogBackgroundBrush` / `App.DialogBorderBrush` / `App.TextBrush` を参照する。

これらの overlay は `App.Canonical.DialogOverlayStyle` と `App.Canonical.DialogContentStyle` を明示的に採用し、overlay 内の action / input control も同じ canonical role を使用する。overlay の routed event、command、binding、validation、選択状態は従来どおり保持する。OS 標準 picker と `EmergencyDialog` はこのアプリ内 dialog canonicalization の対象外である。

OS 標準の `OpenFileDialog` / `SaveFileDialog` / folder picker は Windows 管理 UI のためテーマ対象外。

## MessageBox

アプリ内の確認 / 情報 MessageBox は `UiDialogCoordinator` 経由で owner / dispatcher / active modal state を解決し、表示部品として `ThemedMessageBox` を使う。

- `MessageBoxButton.OK`
- `MessageBoxButton.OKCancel`
- `MessageBoxButton.YesNo`
- `MessageBoxButton.YesNoCancel`
- `MessageBoxImage` の warning / error / question / information 表示
- `defaultResult` による close / cancel 時の戻り値

Livet の `InformationDialogInteractionMessageAction` / `ConfirmationDialogInteractionMessageAction` は撤去済みで、新規の通常 UI 通知 / 確認は coordinator-backed route に寄せる。起動前の致命的エラーなど、テーマ resource がまだ安全に使えない箇所では通常 route ではなく emergency route として OS native dialog を使う。

初回起動の設定案内は MessageBox ではなく `InitialSetupLanguageDialog` で表示する。これは言語選択 ComboBox を含むため、通常の MessageBox ではなく `App.DialogOverlayBrush` / `App.DialogBackgroundBrush` / `App.DialogBorderBrush` / `App.TextBrush` を使う overlay dialog として扱う。

## 未調整 / 今後の課題

- 新しく追加される個別 dialog / popup が direct color を持たないか継続確認する。
- OS 標準 file/folder dialog はテーマ対象外。
- コンテキストメニューやダイアログ内のアイコンは既存 asset を維持している。必要が出た場合のみ個別に調整する。
- フォント設定、テーブル行高設定。
- ユーザー定義テーマ、色の個別カスタマイズ。
- OS テーマ追従。

## 実装時の注意

- 新しく画面色を追加するときは、直接色を置く前に既存の `App.*` / `Table.*` key で表現できるか確認する。
- CustomTableView のようなコード描画では `DynamicResource` が効かないため、`AppThemeService.ThemeChanged` と palette invalidation を使う。
- テーマ切替時に cache された Brush/Pen/FormattedText/描画結果が残らないようにする。
- 設定ウィンドウなど再生成されるpresentationでも、キャンセル後や再表示時に theme radio が stale にならないよう、接続後の TwoWay binding と `AppearanceTheme` の変更通知を維持する。
