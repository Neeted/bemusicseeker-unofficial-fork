# Playlist URL Download Resolution

この資料は、プレイリスト詳細の `URL1` / `URL2` からダウンロード導入を試みる現行仕様です。
実装計画、調査履歴、完了済み作業の記録は含めません。

## 目的

プレイリストには、本体 URL や差分 URL として、直接アーカイブを指す URL だけでなく、配布ページ、共有ページ、難易度表会場ページが入ることがあります。

BeMusicSeeker は、対応できる範囲で URL をダウンロード可能なファイル URL に解決し、BMS / PMS / bmson 譜面または対応アーカイブを既存の導入処理へ渡します。
解決できない URL は、単体操作では従来どおりブラウザで開く対象になり、複数行一括取り込みではブラウザを開かずにスキップします。

## 適用範囲

対象はプレイリスト詳細行の `URL1` / `URL2` と、そこから派生する URL 候補です。

- `URL1` は本体 URL として扱います。
- `URL2` は差分 URL として扱います。
- URL 補完で実行時に埋められた値も同じ経路で扱います。
- Google Drive folder の列挙や候補選択は行いません。
- JavaScript 操作、CAPTCHA、ログイン、ブラウザの download event 捕捉が必要な配布元は自動解決しません。

## 操作ごとの挙動

### 単体 URL 操作

単体行の右クリックメニューに表示される `本体URLを開く` / `差分URLを開く` は、名称どおり URL をブラウザで開きます。
この操作では `AutoInstall` の値に関係なく、自動ダウンロード導入を試みません。

`URL1` / `URL2` 列クリックなど、右クリックメニュー以外の単体 URL 操作では、`AutoInstall` が有効、かつ起動時ファイルチェックを省略していない場合に、先に自動ダウンロード導入を試みます。

- 対応ファイルを取得できた場合は `installChartPackages` へ渡します。
- 自動ダウンロード導入を試みる間は、1 件分の進捗をステータスバーへ表示します。
- サイズ上限超過の場合はブラウザを開かず終了します。
- 対応できない URL、HTML ページ、通信失敗などはブラウザで開きます。

`AutoInstall` が無効の場合は、右クリックメニュー以外の単体 URL 操作でも URL をそのままブラウザで開きます。

### 複数行 URL 取り込み

複数行選択時の右クリックメニューは、`選択中の本体URLを取り込む` / `選択中の差分URLを取り込む` として動作します。

- 対象行は操作開始時点で snapshot 化します。
- `URL1` / `URL2` のどちらを使うかはメニュー種別で固定します。
- 絶対 URI ではない値、空 URL、非プレイリスト行は除外します。
- 同一 URL 文字列は完全一致で重複排除します。
- 共有ページ解決後に同じ実体へ到達した URL は、最終ダウンロードキーで重複排除します。
  - Google Drive は file id をキーにします。
  - MediaFire は file id をキーにします。
  - その他の URL は fragment を除いた正規化後 URI をキーにします。
- 処理開始前に確認ダイアログを表示します。
- ダウンロードは順次実行し、進捗はステータスバーへ表示します。
- ダウンロード中はステータスバーからキャンセルできます。
  - キャンセル後は新しい URL のダウンロードを開始しません。
  - キャンセル時点で進行中の 1 件は完了または失敗まで待ちます。
  - キャンセル時点までに取得できたファイルは、通常通り導入キューへ渡します。
- 取得できたファイルは、画面へのドラッグアンドドロップと同じ導入キューへまとめて渡します。
- ブラウザフォールバック URL は開かず、結果件数の「ブラウザで開く必要がある URL」として集計します。
- キャンセルにより開始しなかった URL は、結果件数の「キャンセルにより未処理」として集計します。

## ダウンロード候補の判定

ダウンロード結果は、最終的にファイル名から導入可能かを判定します。
ファイル名は `Content-Disposition`、リダイレクト後 URI、要求 URI などから決めます。
ただし、既知の共有ページ / 配布ページ host では、URL 末尾の拡張子より共有ページ解決を優先します。
たとえば MediaFire の landing page URL が `.7z` や `.zip` で終わっていても、HTML 応答はアーカイブとして保存せず、ページ内の直接ダウンロードリンクを抽出します。

導入可能なファイルは次の通りです。

- `ChartFileKindResolver` が対応する譜面ファイル: `.bme` / `.bms` / `.bml` / `.pms` / `.bmson`。
- `.zip`
- `.7z`
- `.rar`
- `.lzh`

`Content-Length` が `0` の場合はブラウザフォールバック扱いです。
`Content-Length` または実コピー量が 512 MiB を超える場合はサイズ上限超過として扱います。

HTML など導入可能ファイル名に見えない応答では、共有ページ解決候補なら HTML を読み、直接ダウンロード URL の抽出を試みます。
HTML 読み取り上限は 2 MiB です。
`Content-Type` が HTML の応答は、ファイル名が導入可能拡張子に見えても保存しません。

`Content-Disposition` のファイル名は、raw header の `filename*` を優先し、RFC 5987 形式として復号します。
`filename` が UTF-8 を Latin-1 として読んだ文字化けに見える場合は、UTF-8 として復元してから保存名に使います。
URI 由来のファイル名は percent decode して使います。

## URL 正規化

ダウンロード前に、既知の共有 URL は直接ダウンロード URL へ正規化します。

| 対象 | 処理 |
| --- | --- |
| `drive.google.com/file/d/{id}/...` | `drive.usercontent.google.com/download?id={id}&export=download` に変換します。 |
| `drive.google.com/open?id={id}` | `drive.usercontent.google.com/download?id={id}&export=download` に変換します。 |
| `docs.google.com/uc?id={id}` | `drive.usercontent.google.com/download?id={id}&export=download` に変換します。 |
| `drive.usercontent.google.com/download?id={id}` | `export=download` を付与します。 |
| Dropbox `/scl/fi/...` | `dl=1` を付与または上書きします。 |
| 旧 Dropbox 直リンク形式 | `dl.dropboxusercontent.com` 形式へ変換します。 |
| `onedrive.live.com/redir?...` | `onedrive.live.com/download?...` 形式へ変換します。 |

正規化に失敗した場合は元の URL を使います。

## 共有ページ解決

導入可能ファイルではない HTML 応答を受け取った場合、次のホストでは直接ダウンロード URL を抽出します。
解決後の URL は、再度同じダウンロード候補判定へ通します。

| 対象 | 解決方法 |
| --- | --- |
| Google Drive / Google usercontent | `id="download-form"` の GET form を読み、hidden input を query として再送します。action は Google Drive 系 host の HTTP/HTTPS に限定します。 |
| MediaFire | `id="downloadButton"` または `class` に `popsok` を含む anchor の `href` を使います。リンク先は MediaFire の exact host または subdomain に限定します。 |
| `manbow.nothing.sh/event.cgi` | `DownLoadAddress` / `DownloadAddress` 周辺の anchor から `href` を抽出します。抽出先が別の対応 source page の場合も再帰解決を許可します。 |
| `venue.bmssearch.net/.../{number}` | Next.js / React flight 風の埋め込みデータ内 `type: "CORE"` の `downloadURL`、または anchor から候補 URL を抽出します。直アーカイブまたは既知 download landing page だけを許可します。イベント全体の `packages` は作品個別ページの導入候補から除外し、`packages` として埋め込まれている URL は anchor fallback でも除外します。 |
| `bmssearch.net/bmses/...` | 埋め込みデータ内 `downloads[].url`、または anchor から候補 URL を抽出します。直アーカイブまたは既知 download landing page だけを許可します。 |

再帰解決は最大 4 段です。
fragment を除いた URI 文字列で既訪問判定し、循環した場合はそれ以上追跡しません。

`venue.bmssearch.net` と `bmssearch.net/bmses` から、別の `manbow` / `venue` / `bmssearch` source page へは再帰しません。
これは会場内ナビゲーションや関連リンクを誤って追跡しないためです。
`manbow` の `DownLoadAddress` だけは、実際の配布先として別 source page が置かれることがあるため source page 再帰を許可します。

## ログ

URL 取り込みでは、共有ページ解決、解決不能、HTML スキップ、導入対象外、サイズ上限、重複スキップ、保存完了、失敗、キャンセル要求を `playlist_url_download ...` として application log と install-performance log へ記録します。

導入キューへ渡った後のアーカイブ解凍では、`auto_install extract_start` / `auto_install extract_done` / `auto_install extract_failed` を記録します。
解凍ログには対象パス、展開先、entry 数、経過時間を含めます。

## ブラウザフォールバックとスキップ

次のような URL は自動導入対象にしません。

- 対応 source page 以外の `/` 終端、`.htm`、`.html` ページ。
- 対応外の拡張子や、導入可能ファイル名を判定できない応答。
- Google Drive folder。
- MEGA、AXFC、getuploader など、JavaScript 操作、CAPTCHA、同意 form、ログイン、ブラウザ download event が必要なページ。
- HTML から抽出された HTTP/HTTPS 以外の URL。
- 2 MiB を超える HTML 解決候補。

単体操作ではブラウザフォールバックとしてブラウザで開きます。
複数行取り込みではブラウザを開かず、結果サマリへ件数だけ反映します。

## 現行仕様上の課題

- 複数行 URL 取り込み中のキャンセルは、新しい URL の開始を止めるためのものです。
  - 現在進行中の HTTP 接続や stream 読み取りを即時中断する挙動は未実装です。
  - そのため、キャンセル後も進行中の 1 件が完了または失敗するまで待つ場合があります。
- 複数行 URL 取り込みの結果サマリは、ダウンロード結果までを表します。
  - ダウンロード後のアーカイブ解凍、譜面検出、導入可否の結果は後段の導入キュー、警告表示、ログで扱います。
  - そのため、ダウンロード成功後に解凍や導入で失敗した件数は、URL 取り込み結果サマリには反映されません。
- `venue.bmssearch.net` / `bmssearch.net` の埋め込みデータ解析は正規表現ベースです。
  - サイト側の埋め込み形式が変わった場合、作品個別 URL を見落としたり、意図しない候補を拾ったりする可能性があります。
- 自動解決できる配布元は限定的です。
  - MEGA、AXFC、getuploader など、JavaScript 操作、CAPTCHA、同意 form、ログイン、ブラウザ download event が必要なページは対象外です。
- URL 解決、ダウンロード、HTML 解析の主実装は `MainWindow` 内の private メソッド群に残っています。
  - 対応 host の追加や失敗分類の拡張を行う場合は、専用サービスへ分離するとテストしやすくなります。

## 安全性

- HTML から抽出した URL は HTTP/HTTPS のみ受け付けます。
- Google Drive confirmation form の action は Google Drive 系 host に限定します。
- MediaFire の直接リンクは MediaFire の exact host または subdomain に限定します。
- `venue.bmssearch.net` は末尾 segment が数値の詳細ページだけを source page として扱います。
- Google Drive folder は候補選択の誤爆を避けるため対象外です。

## 実装の主な正本

- `MainWindow.DownloadPlaylistUrlCandidateAsync`
- `MainWindow.DownloadPlaylistUrlResponseCandidate`
- `MainWindow.NormalizeDownloadUri`
- `MainWindow.ResolveSharedDownloadPageUri`
- `MainWindow.DownloadSelectedPlaylistUrlsAsync`
- `MainWindowContextMenuResourceTests.PlaylistUrlDownload_*`
