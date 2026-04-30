# CustomDrawTableView 移行計画

## 目的

メインのライブラリ一覧を WPF `DataGrid` から独自描画の軽量表コントロールへ段階的に移行する。目標は、現在 `DataGrid` で重くなっている実表示セル生成を避け、5000 セル程度の可視表示を数十から数百 ms 程度へ落とすこと。

今回の前提は次のとおり。

- サードパーティ Grid は使わない。
- WinForms `DataGridView` は使わない。
- `ItemsControl` / `ListView` / `GridView` は使わない。
- セルを `TextBlock` として並べる方式は採用しない。
- 通常表示は `DrawingContext` / `OnRender` / 必要なら `DrawingVisual` で描く。
- 編集時だけ overlay の `TextBox` / `ComboBox` / `Popup` を出す。

## 現状ログからの判断

直近の `install-performance.log` では、列を大きく絞った状態でも次の値になっている。

```text
requestToBuildStartMs=52
requestToBuildCompleteMs=83
requestToVisibleRenderMs=974
buildToVisibleRenderMs=890
viewCount=979
realizedRowCount=95
compositionRenderingMs=792
```

`main_view_build totalMs=31ms` 程度であり、データ準備ではなく WPF `DataGrid` の表示コンテナ生成と描画が支配的になっている。3 カラム相当でも 1000ms に近いため、必要列を多く出す通常利用では `DataGrid` 維持のまま安定して 1000ms 未満にするのは難しい。

## 対象範囲

最初の実装対象はメインの `dataGrid` とする。

- 本体: `BeMusicSeeker/Views/MainWindow.xaml` の `Name="dataGrid"`
- ItemsSource: `MainWindowViewModel.BMSFilesView`
- 行型:
  - 通常ライブラリ、フルスキャン、重複、文字化け、ゼロノート、インストール系: `LibraryChartRow`
  - プレイリスト詳細: `PlaylistDetailRow`
- 列設定: `dataGridColumnsSettings`
- sort: `MainWindowViewModel.ExecSort`

`dataGridPlaylistSummary` は独立した表だが、列数が少なく編集もないため、コントロール単体の焼き込みや後続移行候補として有用。ただし、最初の本命はメインの `dataGrid` 置換とする。

## 既存 DataGrid が担っている責務

移行時に互換面として意識する責務は以下。

- `BMSFilesView` の表示。
- `SelectedIndexBMSFilesView` との選択同期。
- `ColumnsSettingsBMSFilesView.<Column>.Width` / `.Visibility` / `.DisplayIndex` の反映と保存。
- ヘッダークリック sort と sort glyph 表示。
- 行選択、Shift/Ctrl による複数選択、右クリック選択。
- 行ダブルクリック、Enter 再生。
- 行右クリックメニュー。
- 列ヘッダー右クリックの列表示メニュー。
- URL1/URL2 クリック。
- `WARNING` / `COMMENT` / `MEMO` などの tooltip。
- `ENTRY LEVEL` / `URL1` / `URL2` / `COMMENT` / `MEMO` / `FOLDER` / `INSTL DST` 編集。
- `INSTL DST` 候補 popup。
- playlist tree への DnD。
- ログ計測。

## 既存列の扱い

メイン `dataGrid` の列は `MainWindow.xaml` に XAML として定義されているが、独自表では C# 側の列スキーマへ寄せる。

列スキーマは次のような薄いモデルにする。

```text
CustomTableColumn
  Id
  HeaderText / HeaderResourceKey
  Getter<object, string>
  SortMemberPath
  LayoutRef
  MinWidth
  MaxWidth
  Alignment
  FontRole
  ForegroundRole / ForegroundResolver
  BackgroundResolver
  TooltipGetter
  EditableKind
  ClickAction
```

`LayoutRef` は既存の `dataGridColumnsSettings.dataGridColumnlayouts` を指す。設定ファイルの保存形式は変えない。初期移行では XAML の全列完全移植を目指さず、主要列から始める。

主な列グループ:

- 基本情報: `music`, `ENTRY LEVEL`, `TITLE`, `ARTIST`, `GENRE`, `KEYS`, `TAG`
- URL/メモ: `URL1`, `URL2`, `WARNING`, `COMMENT`, `MEMO`
- パス/ハッシュ: `MD5 HASH`, `SHA256 HASH`, `FOLDER`, `PATH`
- インストール: `INSTL DST`, `INSTL DST TITLE`, `INSTL DST ARTIST`
- ヘルス/エンコード/参照: `WAV`, `BGA`, `MOVIE`, `ENCODING`, `PLAYLIST`
- chart info: `LEVEL`, `DIFFICULTY`, `MAINBPM`, `MAXBPM`, `MINBPM`, `DURATION`, `JUDGE`, `JUDGE%`, `FEATURE`, `NOTES`, `LONG`, `SCRATCH`, `TOTAL`, `T/N`, `DENSITY`, `PEAK`, `END`, `SOFLAN`
- score: `CLEAR`, `DJ LEVEL`, `RATE`, `SCORE`, `COMBO`, `BP`, `RANKING`, `RANK UPDATE`, `T-SCORE`, `ΔMAX`

## 推奨アーキテクチャ

`DataGrid` 互換コンポーネントを作るのではなく、既存 ViewModel の行リスト、列設定、sort pipeline を使う描画専用ビューを作る。

```text
CustomTableView : lightweight Grid container
  CustomTableSurface : FrameworkElement
  vertical ScrollBar
  overlay TextBox / ComboBox / Popup
  ContextMenu bridge
```

`CustomTableSurface` は WPF 要素としてのセルを作らず、`OnRender(DrawingContext dc)` で可視範囲だけを描く。初期は単一の縦 `ScrollBar` の値を行 offset として保持する。必要になったら `IScrollInfo` 実装へ進む。

固定行高を前提にする。

```text
HeaderHeight = 22-24
RowHeight = 19-22
ExtentHeight = HeaderHeight + RowCount * RowHeight
ExtentWidth = sum(visible column widths)
```

ヒットテストは VisualTree ではなく座標計算で行う。

```text
if y < HeaderHeight:
  HeaderHit(column)
else:
  rowIndex = floor((y - HeaderHeight + VerticalOffset) / RowHeight)
  column = FindColumn(x + HorizontalOffset)
  CellHit(rowIndex, column)
```

## 描画方針

`OnRender` で行う処理は最小限にする。

- 背景、ヘッダー、交互行背景、選択行背景を矩形で描く。
- 可視行範囲だけ描く。
- 可視列範囲だけ描く。
- セルテキストは `FormattedText` または将来的に `GlyphRun` で描く。
- クリップは全体または行/列単位に抑え、セルごとの `PushClip` 多用は避ける。
- 線は薄い縦線、ヘッダー下線、必要最低限のグリッド線にする。
- `Brush` / `Pen` は static cache で共有し、可能なら `Freeze()` する。

初期の text cache:

```text
TextLayoutKey
  Text
  Width
  FontRole
  ForegroundRole
  Alignment
  Dpi
```

行リスト差し替え、列幅変更、フォント変更時は破棄する。最初は可視範囲限定 cache でよい。

## Phase 0: 計測土台

目的: `DataGrid` と独自表の比較ができるログを先に整える。

実装内容:

- `table_first_visible` を比較用ログ名にする。
- 既存 `DataGrid` では `target_updated` から `target_updated_render` までを `firstRenderMs` として記録する。
- `rowCount`, `visibleRowCount`, `visibleColumnCount`, `visibleCellCount`, `firstRenderMs`, `renderWorkMs`, `textCacheHitRate`, `stateLogMs` を出す。
- `DataGrid` の `renderWorkMs` と `textCacheHitRate` は比較対象外なので `-1` を出す。
- `visibleCellCount` は実 Visual 数ではなく、`visibleRowCount * visibleColumnCount` の表示規模として扱う。
- 既存ログの `playlist_open_visible` と比較できるよう、同じ viewCount/表示列/ウィンドウ高さで測る。

完了条件:

- 既存 `DataGrid` の 5000 セル相当表示と同じ条件で比較できる。
- 測定コード自体のコストが 1-2ms 程度に収まる。
- 既存の `playlist_open_visible` / `playlist_datagrid_state` は削除・改名しない。

## Phase 1: 固定行高・固定列幅の表示専用

目的: 5000 セル可視表示を WPF visual tree なしで描き、性能目標の到達可能性を確認する。

対象:

- `BMSFilesView`
- `LibraryChartRow` / `PlaylistDetailRow`
- 固定行高
- 固定列幅
- 読み取り専用
- 主要列から開始: `TITLE`, `ARTIST`, `PATH`, `CLEAR`, `DJ LEVEL`, `LEVEL`, `DIFFICULTY`, `JUDGE`

実装内容:

- `CustomTableView` / `CustomTableSurface` を追加。
- `ItemsSource`, `Columns`, `ColumnsSettings`, `RowHeight`, `HeaderHeight`, `SelectedIndex` を dependency property として持つ。
- 可視行/列のみ `OnRender` で描画する。
- Phase 1 対象列は `TITLE`, `ARTIST`, `PATH`, `CLEAR`, `DJ LEVEL`, `LEVEL`, `DIFFICULTY`, `JUDGE` に固定する。
- 列幅、表示/非表示、表示順は既存 `dataGridColumnsSettings` から読むが、リサイズ保存はまだしない。
- DataGrid と切り替える高度な設定として `UseCustomTableView` を追加する。表示名は「一覧画面で軽量テーブル表示を使用する」、既定値は `False`。
- `UseCustomTableView=True` の間はメイン `dataGrid` の `ItemsSource` binding を外し、DataGrid のセル生成コストを測定に混ぜない。
- `table_first_visible controlType=CustomTableView` を出し、`firstRenderMs` と `renderWorkMs` を確認できるようにする。

完了条件:

- 主要列が表示できる。
- 縦スクロールできる。
- 5000 セル相当表示が数十から数百 ms に入る見込みがログで確認できる。
- DataGrid を残したまま切り戻せる。
- Phase 1 では tooltip、編集、列リサイズ、右クリック、DnD、ヘッダー sort、コピーは未対応とする。

実装後の確認:

- 軽量テーブル表示 ON で、見た目は大きな破綻なく一覧として成立している。
- `DataGrid` 側の `target_updated_render` は発生せず、メイン `dataGrid` のセル生成コストは測定に混ざっていない。
- 文字描画 cache 前のログでは `visibleRowCount=99`, `visibleColumnCount=5`, `visibleCellCount=495` の表示で、`requestToVisibleRenderMs=307-357ms`, `buildToVisibleRenderMs=175-197ms`, `firstRenderMs=172-190ms`, `renderWorkMs=168-189ms` だった。
- 文字描画 cache 後のログでは、プレイリスト詳細で `visibleCellCount=495`, `buildToVisibleRenderMs=114-201ms`, `renderWorkMs=116-176ms`, `textCacheHitRate=0.6-0.676` だった。
- 通常ライブラリでは `rowCount=209956`, `visibleCellCount=792`, `firstRenderMs=237ms`, `renderWorkMs=233ms`, `textCacheHitRate=0.681` だった。
- `main_view_build` は通常ライブラリの行数が多い場合に `DataGrid` 利用時から重い処理であり、`CustomDrawTableView` の描画置換とは別件として扱う。

Phase 1 追加作業: 文字描画 cache

- Phase 6 の一部を前倒しし、`OnRender` 中の `FormattedText` 生成回数を減らすために実装した。
- `CustomTableSurface` 単位で `FormattedText` cache を保持し、`ItemsSource` 差し替えやスクロールをまたいで再利用する。
- cache key は `Text`, `MaxTextWidth`, `MaxTextHeight`, `TextAlignment`, `UseBoldText`, `Foreground`, `PixelsPerDip`, `CultureName` とする。
- 最大件数は `8192` 件。超過時は古い挿入順に削除する FIFO eviction とし、hit 時の並べ替えはしない。
- 空文字や極小セルなど描画しないセルは hit rate の分母に含めない。
- `textCacheHitRate` は `table_first_visible controlType=CustomTableView` で `0.0-1.0` の実値を出し、描画対象テキストが 0 件の場合のみ `-1` とする。
- 行単位 `DrawingVisual` 分割や差分再描画はまだ入れず、全面 `InvalidateVisual()` + 可視範囲描画のまま cache 効果を測る。

Phase 1 完了判断:

- 表示専用の軽量テーブルとして、通常ライブラリ表示とプレイリスト詳細表示の主要列描画は成立している。
- `UseCustomTableView=False` が既定値で、従来 `DataGrid` へ切り戻せる。
- Phase 1 の性能検証目的は達成したため、次は Phase 2 のソート、選択、右クリックに進む。

## Phase 2: ソート、行選択、右クリック

目的: 読み取り専用一覧として日常操作できる最低ラインへ到達する。

実装内容:

- `CustomTableColumn.SortMemberPath` を追加し、Phase 1 の 8 列を既存 `DataGrid` 互換の sort path へ接続する。
  - `TITLE`: `Title`
  - `ARTIST`: `Artist`
  - `PATH`: `path`
  - `CLEAR`: `ClearDisplayText`
  - `DJ LEVEL`: `RankDisplayText`
  - `LEVEL`: `ChartLevelSortKey`
  - `DIFFICULTY`: `ChartDifficultySortKey`
  - `JUDGE`: `ChartJudgeSortKey`
- ヘッダークリックで既存 `MainWindowViewModel.ExecSort(sortMemberPath, direction)` を呼ぶ。
- `SortColumnName` / `SortDirection` を `SortParameters` へ OneWay binding し、ヘッダー右端に小さな sort glyph を独自描画する。
- `SortParameters` は WPF binding が安定して追従できるよう `ColumnsName` / `Direction` を property として公開する。
- `CustomTableSelectionModel` を追加し、単一選択、Ctrl toggle、Shift range、右クリック選択維持を実装する。
- `SelectedIndexBMSFilesView` と current row を同期し、描画は複数選択 index 全体を選択色にする。
- `CustomTableView.GetSelectedRowsSnapshot()` を追加し、既存の選択対象取得を DataGrid / CustomTableView 両対応にする。
- 行ダブルクリック、Enter 再生を既存処理へ接続する。
- 行右クリックで既存 `dataGridContextMenu` / `dataGridContextMenuPlaylistMissing` を開く。
- `ContextMenu.Tag` に `CustomTableContextMenuContext(row, rowIndex)` を入れ、既存 click handler は `TryGetContextMenuRow` 経由で DataGridRow と CustomTableView の両方を解決する。
- 列ヘッダー右クリックの列表示メニューへ接続する。
- プレイリスト詳細 source row に `ClearDisplayText` / `RankDisplayText` を持たせ、Phase 1 対象列の `CLEAR` / `DJ LEVEL` sort path が通常一覧と同じ名前で効くようにする。

設計メモ:

- `DataGridRow` 前提の処理を直接再利用しない。
- `row object + rowIndex + columnId + cellRect` を渡す hit-test / event API を使う。
- `GridRowResolver` は行オブジェクト中心なのでそのまま活かす。
- Phase 2 では Phase 1 の 8 列だけを対象とし、URL click、tooltip、編集、DnD、コピー、横スクロール、列リサイズは引き続き後続 Phase に残す。

完了条件:

- クリック選択と選択行の BMSPlayer 情報更新が動く。
- ヘッダー sort が既存 DataGrid と同じ順序になる。
- 右クリックメニューの主要項目が既存と同じ対象行で開く。

## Phase 3: tooltip、横スクロール、列幅変更

目的: 一覧表示としての視認性と列設定の互換性を高める。

実装内容:

- 描画対象列を Phase 1/2 の 8 列から、`URL1`, `URL2`, `WARNING`, `COMMENT`, `MEMO`, `PLAYLIST` を含む 14 列へ広げる。
- 横スクロールを実装し、描画・hit-test・初回描画 metric の可視列数を `HorizontalOffset` 込みで計算する。
- `COMMENT` / `MEMO` / `WARNING` / `URL1` / `URL2` / `PLAYLIST` の tooltip を座標 hit-test で出す。
- tooltip は owner-level の `ToolTip` 1 個を使い、セルごとの WPF 要素は作らない。
- header 境界の resize hit area を実装し、resize hit は sort より優先する。
- 変更した列幅は既存 `dataGridColumnsSettings.dataGridColumnlayouts.Width` に反映する。
- `URL1` / `URL2` は既存 `DataGrid` と同じく 40px 固定幅扱いにし、resize 対象外にする。
- 列表示/非表示や列幅変更後に horizontal offset を有効範囲へ clamp する。

完了条件:

- 既存の列幅設定を読み書きできる。
- 表示列の切替後にレイアウトが崩れない。
- 長文セルは描画では省略表示し、tooltip で全文確認できる。
- 横スクロール後も sort、選択、右クリック、ダブルクリック/Enter 再生が同じ行/列に作用する。
- `DataGrid` fallback と `dataGridPlaylistSummary` には挙動差分を入れない。

Phase 3 完了判断:

- `UseCustomTableView=True` のプレイリスト詳細表示で、14 列表示、tooltip、横スクロール、列幅変更、マウスホイール縦スクロールが成立している。
- URL 列はフォント依存のリガチャではなく、`DrawingContext` で軽量な download icon を直接描画する。
- 最新ログでは `visibleCellCount=882` のプレイリスト詳細で `buildToVisibleRenderMs=168-266ms` 程度、通常ライブラリの描画で `renderWorkMs=216ms` 程度に収まっている。
- 通常ライブラリの `main_view_build` は描画とは別の一覧生成/ソート側の課題として扱う。

## Phase 3.5: 残り列表示、Status 左端固定、横スクロール自然化

目的: 編集に進む前に、表示専用テーブルとして既存 `DataGrid` の非ダミー列を一通り描ける状態へ近づける。

実装内容:

- Phase 3 の 14 列以外のメイン一覧列を `CustomTableColumnFactory` に追加する。
- 対象は `dataGridColumnDummyFill` / `dataGridColumnDummyLast` を除くメイン `DataGrid` の全列とする。
- 既存 `dataGridColumnsSettings` の幅、表示/非表示、DisplayIndex、sort path を引き続き使う。
- `GENRE`, `KEYS`, `HASH`, `FOLDER`, `RATE`, `BP`, chart_info 系、health 系など、編集を伴わない列は表示専用として先に対応する。
- `INSTL DST` など編集予定列も、Phase 3.5 では通常テキスト表示だけに留める。
- `Status` は初期値 `Visible` / `DisplayIndex=0` / `Width=18` に補正し、表示順の左端固定、幅変更不可、sort 不可にする。
- `Status` は freeze column ではないため、横スクロール時は通常列と同じく画面外へ流れる。
- `Status` の表示は LigatureSymbols 依存ではなく、`BMSFileStatus` を軽量なベクターアイコンへ変換して描画する。
- 横スクロール時の描画を一般的な挙動へ修正する。
  - 列の本来位置は `columnX - HorizontalOffset` のまま負の X も許す。
  - viewport で clip し、左端列の幅が縮んだように見える描画にはしない。
  - hit-test と context menu 用の `CellRect` は画面内 rect のまま維持する。
- 列ごとの表示 formatter を整理し、`DataGrid` converter 依存を増やさず `CustomTableColumn` 側で軽く解決する。

完了条件:

- 表示/非表示メニューで有効にした主要列が `CustomTableView` にも表示される。
- `Status` が初期状態で左端に表示され、幅変更や sort の対象にならない。
- 横スクロール時に列幅が縮むように見えず、一般的な表と同じ見え方になる。
- 全列寄りの表示でも初回描画が大きく悪化しない。
- 編集、URL click、DnD、コピー、列順変更保存は引き続き Phase 4/5 に残す。

## Phase 4: overlay TextBox 編集

目的: DataGrid の編集セル生成を使わず、必要なセルだけ overlay editor で編集する。

初期対象:

- プレイリスト詳細: `ENTRY LEVEL`
- プレイリスト詳細: `COMMENT` / `MEMO`

後続実装対象:

- `URL1` / `URL2`
- `FOLDER`
- `INSTL DST`

実装内容:

- Phase 3.5 時点のログ整理を先に行う。
  - `viewCount > 0` なのに `rowCount=0` / `visibleCellCount=0` の `custom_onrender` は、空の準備描画として扱い `playlist_open_visible completed` の対象にしない。
  - `table_first_visible` は必要なら空描画ログとして残すが、本描画と区別できる field を追加する。
  - 表示完了判定は、`CustomTableView` に実データ行が反映され、`visibleRowCount > 0` または `viewCount == 0` が確定した描画 checkpoint に寄せる。
- 初期実装では `ENTRY LEVEL` / `COMMENT` / `MEMO` を editable metadata 付き列にした。
- `F2`、editable current cell 上での文字入力、または選択済み editable cell の再クリックで、セル矩形に `TextBox` を重ねる。
- Enter/Tab/フォーカス喪失で commit、Escape で cancel。
- 縦横スクロール、列幅変更、sort、ItemsSource 差し替え、非表示化の前には編集中セルを commit する。
- playlist 行は `SyncPlaylistSourceRowFromEditedViewRow` / `CommitPlaylistRow` へ接続する。
- 後続実装では `URL1` / `URL2`, `FOLDER`, `INSTL DST` も editable metadata 付き列にした。
- `URL1` / `URL2` はクリック操作を既存 DataGrid と同じ URL open に使い、編集開始は `F2` または文字入力だけに限定する。
- URL 編集の commit は absolute URI のみ反映し、空文字や invalid URI は rollback 扱いで row/source/runtime completion を変更しない。
- URL 編集中は列幅を変えず、overlay editor の幅だけを最大 250px へ広げる。
- `FOLDER` rename は playlist 行では不可、保留インストール選択中も不可とし、commit は `MainWindowViewModel.RenameBMSFolder` へ委譲する。
- `INSTL DST` は `InstallPending` / `FullScanCheck` のみ編集可とし、編集開始時の `PendingInstallDestinationEditState` を capture する。
- `INSTL DST` commit は `SetPendingInstallDestination` へ委譲し、失敗時は `instl_dst`, title/artist, warning, suggestions, low-confidence flag を snapshot から復元する。
- `INSTL DST` 候補は `CustomTableView` の owner-level `Popup` / `ListBox` で表示し、`Down` / `Up` / `Enter` / mouse click / `Escape` に対応する。

完了条件:

- 既存 DataGrid と同じ行種別で編集可否が一致する。
- 編集 commit 後に表示と underlying model が同期する。
- 編集中でもスクロールや選択が破綻しない。

## Phase 5: 選択式 editor、DnD、キーボード、コピー

目的: DataGrid 依存機能を独自表へ移し、通常利用の置換候補にする。

実装内容:

- playlist tree への DnD を実装する。
- DnD data は既存 drop 側が期待する `System.Windows.Controls.SelectedItemCollection` 互換 format と、独自 `BeMusicSeeker.CustomTable.SelectedRows` format の両方を入れる。
- `Apps` / `Shift+F10` / Enter / Ctrl+C / Esc / F2 などのキー操作を整理する。
- 行選択とセル選択の見た目を分ける。
  - 行選択は薄い選択色で描画する。
  - current cell / focused cell は現在の濃い選択色で描画する。
  - 複数行選択中でも、操作対象セルが分かるようにする。
- キーボードによる行選択を追加する。
  - `Ctrl+A`: 全行選択。
  - `Up` / `Down`: current row を上下へ移動し、単一行選択にする。
  - `Shift+Up` / `Shift+Down`: anchor から範囲選択を伸縮し、複数行選択にする。
- `Ctrl+C` は選択行と表示中の列を TSV として clipboard に入れる。セル内の tab / 改行は空白へ正規化する。
- 列リサイズ保存は既存 `Layout.Width` 共有を継続し、Phase 5 では header drag による表示順保存を追加する。
  - header drag 中は、drag 元 header を薄く描画し、drop した場合の挿入位置を header 境界の太線で示す。
- `Status` は表示順の左端固定列として reorder 対象外にする。

完了条件:

- 保留インストール画面の `INSTL DST` 候補操作が使える。
- 複数行を playlist tree へ drag できる。
- 行選択とセル選択が視覚的に区別でき、キーボードだけで単一行/範囲/全行選択できる。
- 選択セル内容のコピーができる。
- 主要キーボード操作が DataGrid 版と同等に動く。

## Phase 5.5: データ変更通知と再描画の基盤整理

目的: DataGrid の Binding が暗黙に行っていた「データ変更後の即時表示反映」を、独自表側の標準経路として実装する。Phase 6 の描画 cache / 行単位 dirty 管理へ進む前に、まず変更通知の入口を揃える。

背景:

- `CustomTableView` は `OnRender` 時に row / column から値を読むだけなので、row の値が変わっても `InvalidateVisual()` されない限り表示が更新されない。
- セル編集 commit は局所的に `RefreshDisplay()` しているが、右クリック menu やメンテナンス系処理など別経路の変更では反映漏れが起きやすい。
- 例: playlist 行からの削除 / 別 folder への移動、ゼロノート再判定での warning 更新、文字化け修正による `encoding` / `title` / `artist` 更新。

実装内容:

- `CustomTableView` が `ItemsSource` 内の row object を購読する。
  - 2026-04 時点の性能確認で、20 万行規模の `ItemsSource` 全行を購読すると sort / ItemsSource 差し替え時に数秒の UI block が出ることが分かったため、全行購読は採用しない。
  - `INotifyPropertyChanged` を実装している row のうち、現在の可視行 + 上下 overscan 5 行だけを購読する。
  - `ItemsSource` 差し替え、`INotifyCollectionChanged` の add / remove / replace / move / reset、縦スクロール、行高 / header 高 / size / visibility 変更に合わせて可視購読範囲を更新する。
  - `LibraryChartRow` / `PlaylistDetailRow` 経由で underlying `BMSFile` の変更が通知される前提をまず活用する。
- row `PropertyChanged` を受けたら、Dispatcher 上で redraw を coalesce する。
  - 連続更新時に property changed の回数だけ `InvalidateVisual()` しない。
  - Phase 5.5 では全面 `InvalidateVisual()` でよい。行単位 dirty は Phase 6 の対象に残す。
  - 画面外 row は即時 redraw 対象外とし、スクロールで可視化された時点で最新値を読む。
- 可視購読の診断ログを追加する。
  - `custom_table_row_subscription` で `reason`, `firstIndex`, `requestedCount`, `subscribedRowCount`, `elapsedMs` を確認できるようにする。
  - ItemsSource 変更 / reset / 100ms 以上の slow case を中心に出し、通常スクロール時のログ量は抑える。
- 一覧構成が変わる操作は、ViewModel 側の更新通知を明確にする。
  - playlist 行削除 / folder 移動など、row 値変更ではなく `BMSFilesView` の構成が変わる操作では、`BMSFilesView` 置換、collection change、または明示的な refresh token で CustomTableView に伝わるようにする。
  - 個別 command 完了後に `customTableView.RefreshDisplay()` を足し続ける方針は避け、標準通知経路に寄せる。
- 既存の局所 `RefreshDisplay()` は当面残す。
  - 編集 commit 後など、既に安全に動いている箇所は互換目的で残してよい。
  - 新規の反映漏れ修正は、原則として row / collection / view refresh 通知側を直す。
- Phase 6 に向けた dirty 情報の設計を決める。
  - row property changed を受けた row index を記録できる形にしておく。
  - Phase 5.5 では全面 redraw、Phase 6 で row 単位 DrawingVisual / cell value cache invalidation に発展させる。

確認対象:

- playlist 行からの削除、playlist 内の別 folder への移動が、行選択変更なしで表示に反映される。
- ゼロノート検索 tree の右クリック「ゼロノート再判定する」で `WARNING` / highlight が即時反映される。
- 文字化け修正機能で `ENCODING` / `TITLE` / `ARTIST` が即時反映される。
- 大量更新時も redraw request が coalesce され、操作が極端に重くならない。
- ライブラリ一覧の PATH sort で、可視購読数が 20 万行ではなく可視行 + overscan 程度に収まり、`buildToVisibleRenderMs` / `callback_exec_sort buildToRenderMs` が Phase 5.5 回帰前相当に戻る。

実装後確認:

- 最新ログでは、ライブラリ一覧 `rowCount=209960` に対して `custom_table_row_subscription subscribedRowCount=47 elapsedMs=0-3` となり、全行購読は解消した。
- PATH sort の `callback_exec_sort buildToRenderMs` は、回帰時の約 4 秒から `211-292ms` 程度へ戻った。
- 初回表示や sort の残コストは `main_view_build` の `folderMs` / `sortMs` が中心で、CustomTableView の row 購読とは別件として扱う。

## Phase 6: 描画キャッシュ、行単位再描画、DrawingVisual 分割

目的: 5000 セル以上の可視表示、スクロール、選択変更、row 更新時の再描画に余裕を持たせる。初回表示全体の残コストは `main_view_build` の `folderMs` / `sortMs` が中心なので、Phase 6 は主に CustomTableView 内の redraw 体感改善を対象にする。

実装内容:

- redraw reason 計測を追加する。
  - `scroll`, `selection`, `row_property_changed`, `column_resize`, `items_source_changed`, `columns_changed` など、どの経路で redraw したかを分けて見る。
  - `renderWorkMs`, `visibleCellCount`, `textCacheHitRate` と合わせて、次に cache すべき場所を判断する。
- 既存の `CustomTableTextLayoutCache` は維持し、hit 率改善対象として扱う。
  - Phase 1 追加作業で導入済みなので、Phase 6 では再導入しない。
  - cache key の妥当性、列幅変更時の miss、同一文字列が多い score 列での hit 率を確認する。
- 列レイアウト cache を導入する。
  - visible columns、column start X、width、viewport 交差判定、extent width をまとめて cache し、`OnRender` / hit-test / scroll bar 更新で共有する。
  - columns、visibility、width、display index、horizontal offset、viewport width が変わった時だけ更新する。
- セル値 cache を導入する。
  - 可視 row × visible column の表示文字列、foreground、cell kind 判定を cache する。
  - row property changed、ItemsSource 差し替え、columns 変更、該当 row の編集 commit で invalidation する。
  - reflection fallback の多い列を優先して効果を測る。
- 選択変更時の redraw 負荷を下げる。
  - まずは cache により全面 `InvalidateVisual()` のまま軽くする。
  - 必要なら、current row / previous row を dirty row として記録できる構造へ広げる。
- 行単位 `DrawingVisual` cache は条件付きで検討する。
  - cache 改善後もスクロール、選択変更、row property changed でフレーム落ちが残る場合に進む。
  - 導入する場合は row 単位で visual を分け、dirty row だけ再生成する。
- スクロール中の簡易描画は後半候補にする。
  - 見た目の切り替わりと分岐が増えるため、列レイアウト cache / セル値 cache 後も不足する場合だけ検討する。

判断基準:

- redraw reason 別ログで、CustomTableView 内の `renderWorkMs` が継続して高い経路を優先する。
- cache 改善後に全面 `InvalidateVisual()` で目標を満たすなら、`DrawingVisual` 分割は見送る。
- スクロールや選択変更でフレーム落ちが目立つ場合だけ、行単位 visual 分割または簡易描画へ進む。

第一段階の実装結果:

- `RequestRedraw(reason)` を追加し、`custom_table_render` で redraw reason、表示規模、`renderWorkMs`、`textCacheHitRate` を確認できるようにした。
- column layout snapshot を追加し、描画、hit-test、resize 判定、scrollbar 更新で列位置 / viewport 交差 / extent width を共有するようにした。
- cell value cache を追加し、可視 row × column の text / foreground / cell kind / alignment / bold 設定を再利用するようにした。
- row `PropertyChanged`、ItemsSource / collection / columns 変更、編集 commit、可視範囲変更、明示 refresh では必要な cache を invalidation する。
- 行単位 `DrawingVisual` 分割、スクロール中簡易描画、部分再描画はまだ未実装。ログで必要性が見えた場合に後続作業とする。

2026-04-30 ログ確認:

- 初回ライブラリ表示は `main_view_build folderMs` / `sortMs` が支配的で、CustomTableView 描画とは別件として扱う。
- `CustomTableView` 側は `visibleCellCount=1200` 程度で初回 / sort 後の `renderWorkMs=171-234ms`、横スクロール / 列操作 / 選択では `textCacheHitRate` がほぼ 1 まで上がり、cache は機能している。
- 縦スクロールは可視 row が入れ替わるため cell value cache が効きにくく、`renderWorkMs=148-295ms` 程度が残る。次に改善するなら行単位 visual cache またはスクロール専用最適化を検討する。
- ゼロノート再判定で、background thread 由来の row `PropertyChanged` が cell value cache を直接 invalidation し、UI thread の描画と競合して `InvalidOperationException: コレクションが変更されました` が発生した。
- 修正として、row `PropertyChanged` handler は row 参照を thread-safe queue に積むだけにし、Dispatcher 上で pending row の cache invalidation と `RequestRedraw("row_property_changed")` を行う。`CustomTableCellValueCache` は UI thread 専用 state として扱う。

## Phase 7: メイン表の DataGrid 表示差分を埋める

目的: メイン一覧について、DataGrid fallback を外す前に残っている見た目差分を CustomTableView 側へ移す。性能は現状で許容できているため、この Phase は表示互換を優先する。

実装内容:

- `CLEAR` / `DJ LEVEL` / `DIFFICULTY` / `JUDGE` のフォントを DataGrid と同じ `SovjetBox` 表示へ戻す。
  - DataGrid の `styleDataGridNotEditingCellScoreText` 相当として、`FontFamily=/BeMusicSeeker;component/resources/#Sovjet Box`、中央寄せ、単色 foreground を CustomTable 側で表現する。
  - `DJ LEVEL` は DataGrid 側と同じく大きめ表示として扱い、必要なら `CLEAR` / `DIFFICULTY` / `JUDGE` とは別の font size / vertical offset を持たせる。
  - `DropShadowEffect` / gradient は戻さず、現行の cached brush provider を維持する。
- CustomTable の text layout cache key を font role に対応させる。
  - 現状の `UseBoldText` だけでは typeface / font size 差分を表現しにくいため、`CustomTableTextStyle` のような軽量 value を導入する。
  - cache key は `Text`, `MaxTextWidth`, `MaxTextHeight`, alignment, foreground, DPI, culture に加え、typeface / font size / weight を区別する。
  - 既存の通常 text は `Meiryo UI` 系、score text は `SovjetBox` 系として分ける。
- `DIFFICULTY` / `LEVEL` / `TOTAL` / `T/N` の未定義警告セル背景を反映する。
  - DataGrid の `#FFFFF6D5` を基準色にする。
  - `LEVEL`: `ChartLevelUndefined`
  - `DIFFICULTY`: `ChartDifficultyUndefined`
  - `TOTAL`: `ChartTotalUndefined`
  - `T/N`: `ChartTotalUndefined`
  - 選択行の薄い選択色と current cell の濃い選択色を優先し、未選択セルだけ未定義背景を出す。
  - 実装は row 全体 warning とは別に、`CustomTableColumn` / `CustomTableCellValue` へ cell background selector を追加する。
- 既存 DataGrid の列定義、sort path、列幅設定は変更しない。
  - Phase 7 は「DataGrid を消す前の表示差分吸収」であり、列構成や ViewModel の値生成は変えない。

確認対象:

- `CLEAR` / `DJ LEVEL` / `DIFFICULTY` / `JUDGE` が DataGrid 時代と同系統の `SovjetBox` 表示になる。
- 4 列の色分けは現行 CustomTableView と同じ mapping のまま維持される。
- `LEVEL` / `DIFFICULTY` / `TOTAL` / `T/N` の未定義セルだけ薄黄色になり、選択中の current cell 表示を邪魔しない。
- 通常ライブラリ表示、プレイリスト詳細表示、横スクロール、列 resize / reorder、sort、編集、DnD、コピーで表示崩れがない。
- text layout cache の hit 率と `renderWorkMs` が大きく悪化しない。

実装後確認:

- `CustomTableTextStyle` を追加し、通常文字、通常太字、score 用 `SovjetBox`、rank 用 `SovjetBox 16px` を列ごとに選べるようにした。
- `CustomTableTextLayoutCache` の key を text style 対応にし、`Meiryo UI` / `SovjetBox` / 11px / 16px の `FormattedText` が混ざらないようにした。
- `CustomTableColumn` / `CustomTableCellValue` に cell background を追加し、未選択時の `LEVEL` / `DIFFICULTY` / `TOTAL` / `T/N` に `#FFFFF6D5` を描けるようにした。
- 描画順は、行背景、未選択セル背景、current cell 背景、text / icon の順にした。
- Phase 7 ではメイン表だけを変更し、プレイリストサマリーと DataGrid fallback は未変更。

## Phase 8: プレイリストサマリーを CustomTableView 化する

目的: `dataGridPlaylistSummary` を通常表示経路から外し、プレイリストサマリー画面も CustomTableView 系で描画・操作する。これにより本アプリの実質的な DataGrid 使用箇所をなくす。

前提:

- 元の DataGrid の template UI を完全再現することは目的にしない。
- `LINK` はリンクを開けること、`SYNC` / `ROOT` は状態表示と切り替えができることを優先する。
- 右クリックリロード、`SYNC` / `ROOT` の切り替え、プレイリスト削除などの複数行選択対応は維持する。

実装内容:

- `ICustomTableColumnLayout` を追加し、main 用 `dataGridColumnsSettings.dataGridColumnlayouts` と summary 用 `PlaylistSummaryColumnSettings.ColumnLayout` を同じ列 layout として扱う。
  - `CustomTableColumn.Layout`、列 resize、列 reorder、layout 変更監視は interface 経由にした。
  - main 用 `ColumnsSettings` 経路は維持し、summary 用には `PlaylistSummaryColumnsSettings` DP を追加した。
- `CustomTableColumnFactory.CreatePlaylistSummaryColumns(PlaylistSummaryColumnSettings settings)` を追加し、summary 用 12 列を CustomTable column として定義した。
  - `ID`: `PlaylistId`, right, sort `PlaylistId`
  - `NAME`: `Name`, left, sort `Name`
  - `SYMBOL`: `Symbol`, center, sort `Symbol`
  - `LAST UPDATE`: `LastUpdate`, center, `yyyy/MM/dd HH:mm:ss`, sort `LastUpdate`
  - `TOTAL`: `TotalCharts`, right, sort `TotalCharts`
  - `OWNED`: `OwnedCharts`, right, sort `OwnedCharts`
  - `MISSING`: `MissingCharts`, right, sort `MissingCharts`
  - `OWNED %`: `OwnedRatio`, right, `F1%`, sort `OwnedRatio`
  - `LINK`: `LinkUri != null` のときだけ `Open` 相当の表示、sort なし
  - `SYNC`: `IsExternalSync` の checked/unchecked 表示、sort `IsExternalSync`
  - `STATUS`: `Status`, center, tooltip `StatusDetail`, sort `StatusSortOrder`
  - `ROOT`: `IsRootFolder` の checked/unchecked 表示、sort `IsRootFolder`
- summary 用 cell kind と checked selector を追加した。
  - `ActionText`: `Open` text を描画し、クリックで `CellActionRequested` を発火する。
  - `CheckBox`: check mark / empty box を DrawingContext で描画し、クリックで `CellActionRequested` を発火する。
  - WPF `Button` / `CheckBox` をセルごとに生成しない。
- `customTablePlaylistSummary` を `dataGridPlaylistSummary` の sibling として追加し、`IsPlaylistSummaryMode=True` の通常表示を CustomTableView にした。
  - `dataGridPlaylistSummary` は Phase 9 の構造削除まで XAML に残すが、通常経路では collapsed にした。
  - `ItemsSource=PlaylistSummaryView`、`PlaylistSummaryColumnsSettings=PlaylistSummaryColumnsSettings`、`SortColumnName/SortDirection=PlaylistSummarySortParameters` を binding した。
- summary 操作 event を MainWindow に橋渡しした。
  - `CellActionRequested` で `LINK` click、`SYNC` toggle、`ROOT` toggle を通知する。
  - `LINK` は DataGrid fallback と同じ `Process.Start(row.LinkUri.ToString())` を使う。
  - `SYNC` は既存と同じ確認 dialog を出し、選択中 rows 全体へ `ApplyPlaylistSummaryFlags(selectedRows, flag, null)` を適用する。
  - `ROOT` は選択中 rows 全体へ `ApplyPlaylistSummaryFlags(selectedRows, null, flag)` を適用する。
- summary 選択・右クリックを DataGrid 依存から切り離した。
  - `getSelectedPlaylistSummaryRows()` は `customTablePlaylistSummary.IsVisible` のとき `GetSelectedRowsSnapshot()` を優先する。
  - `resolvePlaylistSummaryRowFromSender()` は `CustomTableContextMenuContext` / `DataContext` / selected row から解決できるようにした。
  - row context menu は既存 `playlistSummaryContextMenu` を再利用し、menu handler は `PlaylistSummaryRow` 中心に処理する。
  - double click は `TrySelectPlaylistTreeItemFromSummary(row)` に接続する。
- summary sort を CustomTable header click へ接続した。
  - `SortRequested` から `ExecPlaylistSummarySort(sortMemberPath, direction)` を呼び、既存 pipeline に乗せる。
  - sort glyph は `PlaylistSummarySortParameters.ColumnsName` / `Direction` を CustomTable に binding する。
- summary の failure row highlight を反映した。
  - `HasFailureStatus` の row は DataGrid と同じ `#FFFDE4E4` 系背景を使う。
  - 選択表示は main CustomTable と同じく row 選択色 / current cell 色を優先する。

確認対象:

- プレイリストサマリー表示で全列が既存の列幅 / visibility / display index を反映する。
- `LINK` click と context menu の open page が同等に動く。
- `SYNC` / `ROOT` を単一行・複数選択で切り替えられる。
- reload、property、remove playlist が複数選択を維持して動く。
- row double click で対応する playlist tree item を選択できる。
- header sort / glyph / column resize / reorder / copy / keyboard selection が main CustomTable と同じように動く。
- `dataGridPlaylistSummary` の `SelectedItems` や `DataGridRow` を前提にした処理が summary 通常経路に残っていない。

## Phase 9: DataGrid fallback と二重保守の削除

目的: メイン一覧とプレイリストサマリーの CustomTableView 化が完了した後、DataGrid との共存をやめ、実質的・構造的な二重保守をなくす。

実装結果:

- メイン一覧とプレイリストサマリーの実表示は `CustomTableView` に一本化した。
  - `MainWindow.xaml` からメイン `dataGrid` / `dataGridPlaylistSummary` を削除した。
  - summary mode は `customTablePlaylistSummary`、通常一覧 / playlist 詳細は `customTableView` を表示する。
- 切り戻し設定と DataGrid 専用設定を削除した。
  - `UseCustomTableView`、列仮想化設定、DataGrid 高速ソート切替を `Settings.cs` / `app.config` / 高度な設定 UI / resource から削除した。
  - fast sort は現行の正本経路として常時使用する。
- DataGrid 専用コードを削除した。
  - `DataGridExt.cs`、`DragBehavior.cs`、DataGrid column length converter、DataGrid foreground converter を削除した。
  - `MainWindow.cs` の `DataGridRow` / `DataGridCell` / `DataGridColumn` / DataGrid sort glyph / target updated 計測 / DataGrid edit 経路は削除した。
  - playlist 差し替え前メッセージは `PrepareMainTableSwap` へ改名した。
  - DataGrid sort glyph 用の `CallbackExecSort` と列設定同期用の `CallbackColumnsSetingsChanged` は削除し、CustomTable の binding / rebuild を正本にした。
- 計測は CustomTable 正本にした。
  - `table_first_visible controlType=CustomTableView` と `custom_table_render` を表示計測の基準にする。
  - DataGrid の `target_updated_render` / `playlist_datagrid_state` 系ログは実表示経路から外した。
- 列設定の保存形式互換は維持する。
  - `dataGridColumnsSettings` は既存設定ファイルとの互換のため型名を残し、CustomTable の column layout source として扱う。

確認対象:

- XAML 上の実表示表に `DataGrid` が残っていない。
- 通常ライブラリ、プレイリスト詳細、プレイリストサマリーの主要操作が CustomTableView のみで動く。
- 右クリック、複数選択、DnD、編集、URL open、summary reload / remove / sync / root が維持される。
- 既存の DataGrid fallback を使った性能ログや状態ログが残っていない、または明示的に legacy として隔離されている。

## Phase 10: CustomTableView 移行後の性能改善

目的: CustomTableView への構造移行後に残った表示遅延を、ログで支配要因を分けながら順に削る。2026-04-30 のログでは、プレイリスト詳細は描画、通常ライブラリは `main_view_build` が主因になっている。

現状ログの読み取り:

- プレイリスト詳細 `viewCount=7742`:
  - `main_view_build totalMs=310` に対し、`buildToVisibleRenderMs=1517`。
  - `prepare_items_source_swap renderWorkMs=590` と `items_source_changed renderWorkMs=879` が支配的。
  - `prepare_items_source_swap` は旧 `rowCount=794` の準備描画であり、直後に新 `ItemsSource` の本描画が走るため、捨て描画になっている可能性が高い。
- Phase 10 初回実装後のプレイリスト詳細:
  - `prepare_items_source_swap` の明示 redraw は消えた。
  - ただし `PrepareMainTableSwap` 後、`ItemsSource` 差し替え前の `loadColumnSetting(...)` によって `columns_changed` が発生し、旧ライブラリ `rowCount=209972` のまま描画されるケースが残った。
  - この stale `columns_changed` が `table_first_visible` / `playlist_open_visible completed` として扱われると、実際にはまだ playlist detail が見えていないのに表示完了ログになり、さらに 600ms 前後の捨て描画にもなる。
- その後のログ:
  - stale `columns_changed` は表示完了ログから外れたが、WPF の暗黙 render が `custom_table_render reason=implicit rowCount=209972` として走り、`ItemsSource` 差し替え前に旧行を 400-550ms 程度描くケースが残った。
  - これは redraw 要求の抑制だけでは防げないため、pending swap 中は `OnRender` の行描画自体を skip する必要がある。
- 通常ライブラリ `rowCount=209972`:
  - `main_view_build totalMs=1543` が支配的で、内訳は `folderMs=1034`, `sortMs=506`。
  - 描画は `columns_changed renderWorkMs=652` で、無視はできないが表示前 pipeline の方が大きい。
- 追加ログ後の通常ライブラリ:
  - `main_view_folder_detail` では、フォルダなしの通常ライブラリ表示で `regularRowMaterializeMs` が 1 秒台後半まで伸びるケースが見えた。CustomTableView 描画とは別に、20 万件の `LibraryChartRow` 生成が支配的になりうる。
  - `main_sort_detail` では、`PATH` / `TITLE` の大規模 sort が 800ms 前後になるケースがあり、row 生成の次に大きい候補になっている。
- `textCacheHitRate` は通常ライブラリで `0.99` 近くまで出ているため、`FormattedText` 生成 miss だけが描画コストの主因ではない。全セルの背景・罫線・テキスト描画、全面 redraw そのものが効いている可能性が高い。

改善順序:

1. `PrepareMainTableSwap` 中の stale row render を止める。
   - `PrepareForItemsSourceSwap()` で `ItemsSource` 差し替え待ち状態を記録する。
   - `loadColumnSetting(...)` による `ColumnsSettings` / `Columns` rebuild は許可するが、`ItemsSource` が変わるまでは `columns_changed` redraw と first-render marking を抑制する。
   - `ItemsSource` 変更または collection change で抑制を解除し、`items_source_changed` を正本の初回描画として扱う。
   - 目標: 旧 `rowCount=209972` の stale `columns_changed` が `playlist_open_visible completed` にならないこと、かつ捨て描画自体を避けること。
   - 実装済み: `Columns` DP の暗黙 `AffectsRender` を外し、pending swap 中の `OnColumnsChanged` では layout/cache 更新だけ行って redraw を要求しない。
   - `Dispatcher` idle での復旧 redraw は、実 `ItemsSource` 差し替え前に旧 row の stale `columns_changed` を描いてしまうため採用しない。抑制は `ItemsSource` 変更、collection change、明示 `RefreshDisplay()` まで維持する。
   - pending swap 中に別理由の render が発生しても、`table_first_visible` / `playlist_open_visible completed` にはしない。
   - 実装済み: pending swap 中の `OnRender` では背景とヘッダーだけを描き、`DrawRows()` を呼ばない。ログ reason は `pending_items_source_swap_skipped` とする。
   - 実装済み: 通常ライブラリ / sort / filter の最終反映も、可能な範囲で `PrepareMainTableSwap -> loadColumnSetting -> SetChartRowsView` の順に寄せる。

2. `prepare_items_source_swap` の不要 redraw を止める。
   - `PrepareForItemsSourceSwap()` は active edit の commit と current cell 解除に集中し、通常は `RequestRedraw("prepare_items_source_swap")` を呼ばない。
   - playlist 詳細のように直後に `ItemsSource` が差し替わるケースでは、旧 row の描画を避ける。
   - 目標: `buildToVisibleRenderMs` から 500ms 前後の捨て描画を削る。
   - 実装済み: `PrepareForItemsSourceSwap()` から明示 redraw を削除し、active editor commit 時の `edit` redraw は維持する。

3. 通常ライブラリの `main_view_build folderMs` を分解し、row materialize を削る。
   - `folderMs=1034` の内訳を追加ログで分ける。
   - 候補: 対象 row 抽出、`LibraryChartRow` materialize、bmson/BMS 共通 row 化、mode/tag/keyword 前処理、リストコピー。
   - ここは CustomTableView 描画とは別作業として扱うが、ユーザー体感の初回表示には最も効く。
   - 実装済み: `main_view_folder_detail` を追加し、`sourceBmsCount`, `sourceBmsonCount`, `filteredBmsCount`, `filteredBmsonCount`, `regularFilterMs`, `bmsonFilterMs`, `regularRowMaterializeMs`, `bmsonRowMaterializeMs`, `concatToListMs`, `folderMs`, `folderCount` を出す。
   - 次候補: 全件表示時の `LibraryChartRow` 再生成を避ける cache / reuse、または sort/filter 前後で必要な row 化範囲を絞る。

4. 通常ライブラリの `sortMs` をさらに削る。
   - `sortMs=506` は 20 万行規模では十分大きい。
   - sort key の事前計算、PATH/TITLE の比較 profile、文字列比較回数、`DisplayIndex` や列設定の影響がないかを確認する。
   - 既存の fast sort を正本としつつ、列別の hot path だけを絞って改善する。
   - 実装済み: 最適化本体はまだ入れず、`main_sort_detail` で `rowCount`, `columnName`, `direction`, `propertyType`, `sortProfile`, `stringSortKind`, `sortMs` を出す。
   - 次候補: `PATH` / `TITLE` の sort key cache、比較対象文字列の事前正規化、sort reuse 条件の拡張。

5. 描画側は `custom_table_render reason=...` ごとに後半改善を判断する。
   - `items_source_changed`, `columns_changed`, `scroll_vertical`, `selection`, `row_property_changed` を reason 別に比較する。
   - cache 改善だけで `renderWorkMs` が十分下がらない場合、行単位 `DrawingVisual` 分割、dirty row 再描画、スクロール中簡易描画を検討する。
   - ただし実装コストが高いため、まず 1-4 の低リスク・高効果候補を優先する。

確認対象:

- playlist 詳細で `prepare_items_source_swap` の `custom_table_render` が出ない、または描画コストが無視できること。
- playlist 詳細で、旧ライブラリ行数の `columns_changed` が `playlist_open_visible completed` にならないこと。
- playlist 詳細で `columns_changed` による旧 `ItemsSource` の捨て描画が出ないこと。
- playlist 詳細で、旧ライブラリ行数の `implicit` 捨て描画が `pending_items_source_swap_skipped visibleRowCount=0 visibleCellCount=0` になること。
- playlist 詳細の `buildToVisibleRenderMs` が、同条件で `prepare_items_source_swap` 分だけ短縮されること。
- 通常ライブラリの `main_view_build` に `folderMs` 内訳ログが出て、次の改善対象を特定できること。
- PATH/TITLE sort で `sortMs` の改善前後を比較できること。
- 描画改善を入れる場合は `custom_table_render reason` 別に `renderWorkMs` が悪化していないこと。

## 移行順序

推奨順序:

1. メイン `dataGrid` の表示専用プロトタイプを実装する。
2. 実験設定で DataGrid / CustomDrawTableView を切り替えられるようにする。
3. 主要列だけで性能を測る。
4. 通常ライブラリ表示とプレイリスト詳細表示の読み取り専用を広げる。
5. 操作系を追加する。
6. 編集系を追加する。
7. DataGrid Binding 相当の変更通知 / 再描画経路を揃える。
8. メイン表の DataGrid 表示差分を CustomTableView 側で吸収する。
9. プレイリストサマリーも CustomTableView 化する。
10. DataGrid fallback と DataGrid 専用コードを削除する。

別案として、`dataGridPlaylistSummary` は列数が少なく編集もないため、独自表コントロールの安全な試験場にできる。ただし性能課題の本命はメイン `dataGrid` なので、サマリーだけで完結しない。

## リスク

### DataGridRow / DataGridCell 前提の既存処理

`MainWindow.cs` には `DataGridRow`, `DataGridColumn`, `DataGridCell`, `SelectedItems`, `CurrentColumn`, `CommitEdit` を前提にした処理が多い。独自表で DataGrid 互換 API を無理に作ると複雑になるため、行オブジェクト中心の操作 API へ切り出す。

### 編集 commit の差異

DataGrid は binding update、commit/cancel、editing template を面倒見ている。overlay editor では commit を明示的に実装する必要がある。特に playlist 行、`FOLDER`, `INSTL DST` は副作用が大きいので後回しにする。

### 表示再現の優先順位

既存 XAML は style、converter、template column で細かい見た目を作っている。独自描画では完全再現を最初から狙わず、読みやすさと性能を優先する。

### 部分再描画の実装コスト

WPF `FrameworkElement.InvalidateVisual()` は基本的に全面再描画になる。部分再描画は `DrawingVisual` 分割が必要になるが、初期から入れると実装が重い。Phase 1-5 では全面再描画 + 可視範囲限定 + cache で測る。

## 受け入れ基準

初期の性能目標:

- 5000 セル相当の可視表示で初回 render が 300ms 未満。
- 主要列のみなら 100ms 台を目指す。
- 縦スクロール時に操作不能な引っかかりがない。
- `main_view_build` 相当のデータ準備時間は現状維持。

機能目標:

- Phase 1 では表示だけでよい。
- Phase 2 で日常の読み取り操作に耐える。
- Phase 4 以降で DataGrid 置換候補にする。

## 関連ファイル

- `BeMusicSeeker/Views/MainWindow.xaml`
- `BeMusicSeeker/Views/MainWindow.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/ViewModels/dataGridColumnsSettings.cs`
- `BeMusicSeeker/ViewModels/LibraryChartRow.cs`
- `BeMusicSeeker/ViewModels/PlaylistDetailRow.cs`
- `BeMusicSeeker/ViewModels/GridRowResolver.cs`
- `BeMusicSeeker/Views/DataGridExt.cs`
- `BeMusicSeeker/Views/DragBehavior.cs`
- `BeMusicSeeker/ViewModels/PlaylistSummaryRow.cs`
- `BeMusicSeeker/ViewModels/PlaylistSummaryColumnSettings.cs`
