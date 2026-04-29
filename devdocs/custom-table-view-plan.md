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
- プレイリスト詳細: `URL1` / `URL2`
- プレイリスト詳細: `COMMENT` / `MEMO`

後続対象:

- `FOLDER`
- `INSTL DST`

実装内容:

- Phase 3.5 時点のログ整理を先に行う。
  - `viewCount > 0` なのに `rowCount=0` / `visibleCellCount=0` の `custom_onrender` は、空の準備描画として扱い `playlist_open_visible completed` の対象にしない。
  - `table_first_visible` は必要なら空描画ログとして残すが、本描画と区別できる field を追加する。
  - 表示完了判定は、`CustomTableView` に実データ行が反映され、`visibleRowCount > 0` または `viewCount == 0` が確定した描画 checkpoint に寄せる。
- `BeginEdit(rowIndex, columnId)` でセル矩形に `TextBox` を重ねる。
- Enter/Tab/フォーカス喪失で commit、Escape で cancel。
- URL 編集中の列幅一時拡大は、列そのものではなく overlay 幅を広げる。
- 既存 `dataGridCellEditEnding` の処理を、DataGrid event 非依存の edit service へ切り出す。
- playlist 行は `SyncPlaylistSourceRowFromEditedViewRow` / `CommitPlaylistRow` へ接続する。

完了条件:

- 既存 DataGrid と同じ行種別で編集可否が一致する。
- 編集 commit 後に表示と underlying model が同期する。
- 編集中でもスクロールや選択が破綻しない。

## Phase 5: 選択式 editor、DnD、キーボード、コピー

目的: DataGrid 依存機能を独自表へ移し、通常利用の置換候補にする。

実装内容:

- `INSTL DST` の候補 `Popup` / `ListBox` を overlay として実装する。
- playlist tree への DnD を実装する。
- 既存 drop 側が期待する `System.Windows.Controls.SelectedItemCollection` 互換を見直し、独自 data format を追加する。
- `Apps` / `Shift+F10` / Enter / Ctrl+C / Esc / F2 などのキー操作を整理する。
- 選択セル/選択行の内容を TSV として clipboard に入れる機能を追加する。
- 列リサイズ保存、表示順保存を実装する。

完了条件:

- 保留インストール画面の `INSTL DST` 候補操作が使える。
- 複数行を playlist tree へ drag できる。
- 選択セル内容のコピーができる。
- 主要キーボード操作が DataGrid 版と同等に動く。

## Phase 6: 描画キャッシュ、行単位再描画、DrawingVisual 分割

目的: 5000 セル以上の可視表示やスクロール時にも余裕を持たせる。

実装内容:

- テキスト layout cache を導入する。
- 列レイアウト cache を導入する。
- セル値 cache を導入する。
- スクロール中は簡易描画、停止後に詳細描画するか検討する。
- 必要なら行単位 `DrawingVisual` cache へ進む。
- 選択変更時は前行/新行だけ再生成する構造を検討する。

判断基準:

- 全面 `InvalidateVisual()` で目標を満たすなら、`DrawingVisual` 分割は見送る。
- スクロールや選択変更でフレーム落ちが目立つ場合だけ分割する。

## 移行順序

推奨順序:

1. メイン `dataGrid` の表示専用プロトタイプを実装する。
2. 実験設定で DataGrid / CustomDrawTableView を切り替えられるようにする。
3. 主要列だけで性能を測る。
4. 通常ライブラリ表示とプレイリスト詳細表示の読み取り専用を広げる。
5. 操作系を追加する。
6. 編集系を追加する。
7. DataGrid 使用箇所の置換範囲を広げる。

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
