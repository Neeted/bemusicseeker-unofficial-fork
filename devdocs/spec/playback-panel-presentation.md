# 再生パネル表示 現行仕様

## 状態の所有権

`PlaybackPanelViewModel.PlayerPanelState` は、保存された requested state の正本である。`PlaybackPanelView` は `Settings.Default` を直接参照せず、利用可能な再生面を考慮した `EffectivePlayerPanelState` だけを表示判断に使う。

`TITLE_SMALL` は compact 表示を表す flag である。`TITLE_LARGE` は値 0 の正規状態であり、未初期化 sentinel として扱わない。MOVIE_PLAYER 互換値、BMS_PLAYER が利用できない場合の fallback、退役済み WebBrowser を選択しない契約は [movie-playback-current-state.md](movie-playback-current-state.md) に従う。surface fallback は requested state を書き換えない。

## 初期同期と通常遷移

View は panel height、artwork blur、拡大タイトル margin、banner opacity、compact title opacity を単一の表示状態 coordinator で同期する。

- 初回の有効な ViewModel 適用は、animation clock を除去して最終状態へ即時同期する。
- DataContext が Loaded 前に設定された場合も Loaded 中に設定された場合も、同じ即時同期経路を使う。
- Loaded 中の DataContext 差し替えと Unloaded からの再 Loaded は初期同期として扱い、以前の ViewModel や以前の lifecycle の状態からアニメーションしない。
- Unloaded と即時同期では、対象 dependency property に残る animation clock を除去する。
- 初期同期後、同じ購読世代の同じ ViewModel で compact flag が変更された場合だけ通常遷移を使う。
- surface flag や capability だけが変更され、compact flag が同じ場合は presentation animation を再開しない。
- ViewModel 購読には lifecycle ごとの世代を持たせ、差し替えまたは再 Loaded より前に queue された通知を新しい表示へ適用しない。

通常遷移は従来の choreography を維持する。panel height と blur は約 1 秒、拡大タイトルの退避・復帰は 0.7 秒、compact banner は 0.8 秒後から 0.5 秒で表示し、expanded 時は 0.2 秒で非表示にする。compact title は compact 遷移の 0.8 秒後から表示する。遷移の完了または置換後は clock を除去し、base value を最終状態の正本とする。

## 初期フレーム受入条件

### Compact (`TITLE_SMALL`)

- `PlaybackPanelView.Height = 110`
- artwork の `BlurEffect.Radius = 20`
- 拡大用半透明タイトル panel の下 margin は `-40`
- banner opacity は `1`
- compact title opacity は `1`
- 上記 property に animation clock がない

requested state が `TITLE_SMALL | BMS_PLAYER` で BMS 面が利用できない場合も、effective surface だけを fallback し、最初のフレームから compact 表示にする。

### Expanded (`TITLE_LARGE`)

- `PlaybackPanelView.Height = 286`
- artwork の `BlurEffect.Radius = 0`
- 拡大用半透明タイトル panel の margin は `0`
- banner opacity は `0`
- compact title opacity は `1`（visibility は既存 converter が決定する）
- 上記 property に animation clock がない

初期同期のために panel を一時的に非表示にしたり、任意の Dispatcher 遅延、固定待ち、起動時専用 opacity を使ったりしない。

## Verification map

再生パネルと playlist workspace の owner coverage は既存 `presentation-workspace` testhost内で `ClassLevel` scope の3 workerに分ける。

| behavior | canonical fixture | Functional route / safety |
| --- | --- | --- |
| playback session、player replacement、panel requested/effective state、初期同期と遷移 | `PlaybackPanelViewModelTests` | `presentation-workspace`, 3 workers / `ClassLevel`; class-wide `DoNotParallelize` を維持 |
| library folder tree refresh、selection、explorer boundary | `LibraryFolderTreeViewModelTests` | `presentation-workspace`, 3 workers / `ClassLevel` |
| workspace external source、action workflow、detail refresh、presentation state、persistence command | `PlaylistWorkspaceExternalSourceTests`、`PlaylistWorkspaceActionWorkflowTests`、`PlaylistWorkspaceDetailRefreshTests`、`PlaylistWorkspacePresentationStateTests`、`PlaylistWorkspacePersistenceCommandTests` | `presentation-workspace`, 3 workers / `ClassLevel` |

旧 `PlaylistWorkspaceViewModelTests` の monolithic routeは退役する。workspace 5 fixtureのGUID付き filesystem / task completion signalと、PlaybackのDNP safety boundaryを変更せず、runnerは7 classのexact membership・remaining exclusion・他 routeとの重複なしを起動前に検証する。
