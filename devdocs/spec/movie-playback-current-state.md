# Movie Playback Current State

この文書は、現行実装に残っている動画再生関連機能の仕様をまとめる。

## 対象範囲

- プレイヤーパネルの `MOVIE_PLAYER` 表示状態。
- `BrowserHtml` を `WebBrowser` に流し込む埋め込み表示。
- 外部ブラウザ利用設定 `UseExternalWebBrowser`。

プレイリスト行の右クリックメニューにあった `動画を開く` / `入手先を検索` は対象外であり、現行 UI からは削除済み。これらが利用していた Ribbit / LR2IR song info cache 由来の動画 URL、ダウンロード候補、コメント中 URL の収集も現行機能には含めない。

## 残っている機能

### パネル状態

プレイヤーパネル状態は `Settings.Default.PlayerPanelState` に保存され、`MainWindowViewModel.PanelState` の `BMS_PLAYER` / `MOVIE_PLAYER` / `TITLE_SMALL` の組み合わせで扱う。

`MOVIE_PLAYER` が有効な場合、メインウィンドウは通常の BMS プレイヤー領域ではなくブラウザ領域を表示する。プレイヤー制御部の回転ボタンからも `BMS_PLAYER` と `MOVIE_PLAYER` の切り替え候補が生成される。

### 埋め込みブラウザ

動画表示領域は `MainWindow.xaml` の `WebBrowser` で、`MainWindowViewModel.BrowserHtml` が `WebBrowserUtility.Html` にバインドされる。

`MOVIE_PLAYER` は、次の条件を満たすときだけ有効なパネル状態として扱う。

- `webBrowser` が生成済みである。
- `webBrowser.IsEnabled` が `true` である。
- `MainWindowViewModel.BrowserHtml` が `null` ではない。

`WebBrowser.LoadCompleted` が発火すると、現在のパネル状態は `MOVIE_PLAYER` に切り替わる。

### 外部ブラウザ設定

設定画面には `動画再生` グループと `外部ブラウザで再生する` 設定が残る。

`UseExternalWebBrowser` は `WebBrowser.IsEnabled` に反転してバインドされる。つまり、この設定が `true` の場合は埋め込み `WebBrowser` が無効になり、`MOVIE_PLAYER` は有効なパネル状態にならない。

## 現行の入口

現行コードには `BrowserHtml` と `SetMoviePlayerHeader` が残っているが、プレイリスト行の右クリックから YouTube / ニコニコ動画を設定する入口は削除済み。

そのため、通常操作で新しく動画 URL を選び、`BrowserHtml` に埋め込み HTML を設定する明確な UI 導線は残っていない。

## 残る課題

- `MOVIE_PLAYER`、`BrowserHtml`、`SetMoviePlayerHeader`、`UseExternalWebBrowser`、設定画面の `動画再生` グループを今後も維持するか決める必要がある。
- 維持する場合は、外部 API に依存しない動画 URL 入力または譜面メタデータ由来の入口を改めて設計する必要がある。
- 維持しない場合は、パネル状態、設定、converter、XAML、リソースを含めた動画再生面の完全撤去を別タスクとして行う。
