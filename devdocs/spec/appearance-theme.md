# 外観テーマ設定 現行仕様

## 概要

BeMusicSeeker は外観テーマとして `Light` / `Dark` を持つ。既定は `Light` で、設定ダイアログの `一般 > 外観 > テーマ` から変更できる。

テーマはアプリ全体の配色リソースを差し替える仕組みであり、現時点では主にメイン画面、左ツリー、CustomTableView、検索欄、ステータスバー、スクロールバー、コンテキストメニュー、標準 control、設定ダイアログ、主要なアプリ内ダイアログを対象にしている。フォントや行高はまだテーマ設定の対象外。

## 設定値

- 設定名: `Settings.Default.AppearanceTheme`
- 値:
  - `Light`
  - `Dark`
- 不明値、空値、`null` は `Light` に正規化する。
- 保存前にも正規化し、設定ファイルに不明値を残さない。

設定ダイアログでは、選択肢の `ItemsSource` を安定したリストとして保持する。キャンセル時や再表示時に ComboBox の選択が空にならないよう、設定値と UI 選択状態を再同期する。

## テーマ適用

`AppThemeService` が現在のテーマ ResourceDictionary を管理する。

- 起動時:
  - `Settings.Default.AppearanceTheme` を正規化する。
  - `Themes/Light.xaml` または `Themes/Dark.xaml` を `Application.Resources.MergedDictionaries` へ追加する。
- 変更時:
  - 既存の `/Themes/*.xaml` を削除する。
  - 新しいテーマ辞書を追加する。
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

ScrollBar は `SimpleScrollBar` を `ScrollBar.*` key に接続し、標準 `ScrollBar` の implicit style としても適用する。CustomTableView の縦横スクロールバーも標準 `ScrollBar` を使うため、この style 経由でテーマ色になる。

## 標準 Control / Menu

`Simple Styles.xaml` は主要な標準 WPF control に implicit style を定義している。動的生成された control でも、アプリ内の visual tree 上にあればテーマ resource が適用される。

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

## 設定ダイアログ

設定ダイアログには `一般 > 外観` にテーマ選択 ComboBox がある。

- 選択肢: ライトモード / ダークモード
- `SelectedValue` は `AppearanceTheme` に TwoWay binding。
- キャンセル時は保存済みのテーマへ戻し、即時に `AppThemeService.ApplyTheme()` を呼ぶ。
- ダイアログは非表示で再利用されるため、再表示時にも ComboBox 選択を現在設定へ同期する。

設定ダイアログは標準 control の implicit style を使う。タブやグループ枠、入力欄、ボタンなどは `Simple Styles.xaml` のテーマ resource 参照に寄せている。

## アプリ内ダイアログ

アプリが描画する主要なダイアログは `App.DialogOverlayBrush` / `App.DialogBackgroundBrush` / `App.DialogBorderBrush` / `App.TextBrush` を参照する。

対応済み:

- `SettingDialog`
- `InitialSetupLanguageDialog`
- `PlaylistPropertyDialog`
- `LoadPlaylistURIDialog`
- `PendingDeleteConfirmDialog`
- `Parago/Windows/ProgressDialog`

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
- 設定ダイアログなど非表示で再利用される control は、キャンセル後や再表示時に ComboBox / SelectedValue が stale にならないよう注意する。
