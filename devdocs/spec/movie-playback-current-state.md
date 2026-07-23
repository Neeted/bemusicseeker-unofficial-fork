# Movie Playback Current State

この文書は、現行実装に残っている動画再生関連機能の仕様をまとめる。

## 対象範囲

- プレイヤーパネルの `MOVIE_PLAYER` 表示状態。
- 埋め込み `WebBrowser` view-host の残存状態。
- 外部ブラウザ利用設定 `UseExternalWebBrowser`。

プレイリスト行の右クリックメニューにあった `動画を開く` / `入手先を検索` は対象外であり、現行 UI からは削除済み。これらが利用していた Ribbit / LR2IR song info cache 由来の動画 URL、ダウンロード候補、コメント中 URL の収集も現行機能には含めない。

## 残っている状態

### パネル状態

プレイヤーパネル状態は `Settings.Default.PlayerPanelState` に保存され、standalone `PlayerPanelState` の `BMS_PLAYER` / `MOVIE_PLAYER` / `TITLE_SMALL` の組み合わせで扱う。

`MOVIE_PLAYER` の serialized value は設定互換性のため保持される。ただし動画 payload の production writer はなく、現行のパネルでは動画面を選択できない。プレイヤー制御部の回転処理も movie surface を unavailable として扱う。

### 埋め込みブラウザ

`WebBrowser` は後続の `MIG-03` platform closure まで view-host residual として XAML に残るが、現行パネルでは常に `Collapsed` である。`BrowserHtml`、`BrowserSource`、HTML attached binding は削除済みで、動画面の payload や `LoadCompleted` による状態切り替えは行わない。

### 外部ブラウザ設定

設定画面には `動画再生` グループと `外部ブラウザで再生する` 設定が残る。

`UseExternalWebBrowser` の既存設定値と設定画面は persisted compatibility のため当面残る。現行の WebBrowser host は常に collapsed なので、この設定は movie surface の選択可否を変えない。

## 現行の入口

プレイリスト行の右クリックから YouTube / ニコニコ動画を設定する入口と、埋め込み HTML を生成する production route は削除済み。

## 残る課題

- `MOVIE_PLAYER` の persisted value、`UseExternalWebBrowser` 設定、設定画面の `動画再生` グループは、setting key / serialized value を維持したまま後続の platform/settings 方針で整理する。
- `WebBrowser` host の完全撤去は `MIG-03` の WPF platform closure で判断する。
