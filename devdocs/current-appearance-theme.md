# 外観テーマ設定 現行仕様

## 概要

BeMusicSeeker は外観テーマとして `Light` / `Dark` を持つ。既定は `Light` で、設定ダイアログの `一般 > 外観 > テーマ` から変更できる。

テーマはアプリ全体の配色リソースを差し替える仕組みであり、現時点では主にメイン画面、左ツリー、CustomTableView、検索欄、ステータスバー、設定ダイアログの一部を対象にしている。フォントや行高はまだテーマ設定の対象外。

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
- `App.TextBrush`
- `App.SubtleTextBrush`
- `App.DisabledTextBrush`
- `App.BorderBrush`
- `App.StrongBorderBrush`
- `App.SeparatorBrush`
- `App.AccentBrush`
- `App.AccentSubtleBrush`
- `App.WarningTextBrush`
- `App.DialogOverlayBrush`
- `App.DialogBackgroundBrush`
- `App.PopupBackgroundBrush`
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

## MainWindow

メインウィンドウは `Background` / `Foreground` をテーマ resource へ接続する。

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

設定ダイアログ全体の完全なダークテーマ対応は未完了。背景、文字、更新履歴欄など主要部分はテーマ化済みだが、標準 WPF control の細部は今後の対象。

## 未調整 / 今後の課題

- コンテキストメニューの全面的なテーマ化。
- 各種ダイアログの全面的なテーマ化。
- 設定ダイアログの全 control template 明示化。
- TextBox / ComboBox / Button / TabControl / GroupBox など標準 WPF control の暗色時 hover / focus / disabled 表現。
- 画像 asset の白背景前提アンチエイリアス確認。
- ツリーやボタンのアイコン色調整。
- フォント設定、テーブル行高設定。
- ユーザー定義テーマ、色の個別カスタマイズ。
- OS テーマ追従。

## 実装時の注意

- 新しく画面色を追加するときは、直接色を置く前に既存の `App.*` / `Table.*` key で表現できるか確認する。
- CustomTableView のようなコード描画では `DynamicResource` が効かないため、`AppThemeService.ThemeChanged` と palette invalidation を使う。
- テーマ切替時に cache された Brush/Pen/FormattedText/描画結果が残らないようにする。
- 設定ダイアログなど非表示で再利用される control は、キャンセル後や再表示時に ComboBox / SelectedValue が stale にならないよう注意する。
