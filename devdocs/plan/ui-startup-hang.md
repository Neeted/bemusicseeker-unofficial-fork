# 起動直後 UI ハング調査メモ

## 概要

起動直後、初期化処理がまだ `startup_ready_operable` に到達していないタイミングで DataGrid ヘッダーや行、TreeView を操作すると、UI スレッド上の ContextMenu 生成、ソート、一覧再構築、選択変更処理が初期化中の重い処理と衝突し、アプリがハングしたように見えることがある。

短期対策では、起動完了前の UI 操作を「無視してログに残す」扱いにする。ユーザー向けダイアログは出さない。

## 再現タイミング

- `everything_scan start` 以降、`startup_ready_operable` が出る前。
- DataGrid の列ヘッダーを右クリックして列表示メニューを開こうとする。
- DataGrid 行の右クリック、ヘッダー左クリックによるソート、TreeView 選択、行ダブルクリック、Enter 再生、URL セルクリック、セル編集開始/確定でも同種の重い経路に入る可能性がある。
- ハング相当の高負荷中にウィンドウを閉じると、終了処理中の遅延 UI 更新が閉鎖済み Window に触れて例外化することがある。

## 関連ログ例

起動完了前の操作は `install-performance.log` に次の形式で出力される。

```text
startup_ui_blocked action=column_header_context_menu reason=startup
startup_ui_blocked action=datagrid_row_context_menu reason=startup
startup_ui_blocked action=datagrid_sort reason=startup
startup_ui_blocked action=tree_pending_install_select reason=startup
```

終了開始後に同じ入口へ来た場合は `reason=closing` になる。

## 原因仮説

- DataGrid ヘッダーの ContextMenu が共有リソースとして使われ、起動直後の binding 評価や列状態更新と重なる。
- 初期化中は Everything 検索後の一覧構築、ハッシュ/所有判定、DataGrid ItemsSource 反映などが集中し、UI スレッドの Dispatcher キューが詰まりやすい。
- 起動完了前のソート、TreeView 選択、行右クリックは一覧再構築や ContextMenu サブメニュー生成に入り、初期化中の ViewModel 更新と競合しやすい。
- 終了時は ContextMenu サブメニューや sort glyph 更新などの遅延 Dispatcher 処理が残り、閉じ始めた Window に対して Visibility、Focus、WindowInteropHelper.Handle 取得を行うことがある。

## 短期対策

- `MainWindowViewModel.IsStartupUiInteractionBlocked` を追加し、初期化開始から `startup_ready_operable` まで true にする。
- 起動完了前の DataGrid ヘッダー右クリック、行右クリック、ソート、TreeView 選択、行ダブルクリック、Enter 再生、URL セルクリック、セル編集をガードする。
- ガード時は `e.Handled = true` または `e.Cancel = true` とし、処理本体に入らない。
- DataGrid ヘッダー ContextMenu は `x:Shared="False"` にして、ヘッダー間で同じ ContextMenu インスタンスを共有しない。
- Window 終了開始時に `_isClosingOrClosed` を立て、ContextMenu 非同期タスクをキャンセルし、保留中の sort glyph DispatcherOperation を中止する。
- 代表的な `Dispatcher.BeginInvoke` の実行冒頭で `_isClosingOrClosed` を確認し、終了中は UI 更新しない。
- `WindowInteropHelper(this).Handle` を使う終了時の配置保存は try/catch で保護し、失敗しても設定保存全体を落とさない。

## 添付エラーの意味

```text
.NET Frameworkでエラーが発生しました
エラー概要:
このコマンドを実行するための十分なクォータがありません。
```

これはハング中のリソース逼迫、または Dispatcher キュー滞留の副作用として扱う。今回の対策では、起動完了前に重い UI 経路へ入らないことで再発率を下げる。

```text
.NET Frameworkでエラーが発生しました
エラー概要:
Window が閉じている場合、Visibility を Visible に設定したり、Show、ShowDialog、Close、および WindowInteropHelper.EnsureHandle を呼び出したりすることはできません。
```

これは終了開始後に残った遅延 UI 処理が、閉鎖中または閉鎖済みの Window に触った症状として扱う。今回の対策では、終了開始後は UI 変更をしない方針で抑止する。

## 中長期課題

- `RefreshChartRowsView()` とその周辺の一覧再構築を本格的に非同期化する。
- 初期化中の writer lock 範囲を短くする。
- Everything スキャン後のインデックス構築、所有判定、DataGrid 反映を小さな単位に分割する。
- UI 操作可能状態を画面上でも表現するか検討する。ただし短期対策ではダイアログやトーストは追加しない。

## 調査時に見るログキー

- `everything_scan start`
- `everything_scan success`
- `startup_ready_operable`
- `startup_ui_blocked`
- `playlist_datagrid_state`
- `playlist_sortglyph_refresh`
- `callback_exec_sort`
- `playlist_context_menu_prepare`
- `playlist_context_menu_assign`
- `playlist_context_menu_manual_open`
