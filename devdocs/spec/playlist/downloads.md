# URL・外部APIからの取得と導入引渡し

## 目的と適用範囲

プレイリスト詳細の本体・差分URL、または譜面MD5からファイルを取得し、既存の導入キューへ渡す契約を定めます。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 受付とURIの境界

通信中という理由だけで、既存入口が許可するすべてのライブラリ操作を禁止しません。通信と実導入の排他は別です。取得済みパスを `QueuePlaylistUrlInstallPathsAsync` から共通の `PackageInstallWorkflowOwner.Enqueue` へ渡す時点で導入受付を判定します。

未受理なら `Warn_PackageInstallUnavailable` を一度表示して終了します。予期しない例外、ツリー展開、ブラウザー起動、自動再試行へ変換しません。取得済みファイルは既存の一時領域の寿命に従い、拒否だけを理由に消したり、再実行用の状態を追加したりしません。

初期URI、HTTP転送後の最終URI、HTML/JSONから辿る各URIはHTTP(S)だけを許可します。ローカルパス、UNC、`file:`、FTP、独自スキームは入力エラーとして取得・一時保存・ブラウザー代替・登録を始めません。これは保存済み表の外部同期元がローカルパスを扱う契約とは別です。

### 単体URLと複数行

右クリックの「本体URLを開く」「差分URLを開く」はブラウザーで開く操作で、自動導入しません。列クリック等の単体操作は、`AutoInstall` が有効で起動時ファイル検査を省略していなければ、自動取得を先に試みます。対応ファイルを得たら導入へ渡します。サイズ上限超過と導入未受理ではブラウザーを開きません。許可済みHTTP(S)の通常取得失敗や未対応ページだけはブラウザーで開けます。

複数行の本体・差分取込みは、開始時の行を固定し、選んだ列だけを使います。空・非絶対URI・非プレイリスト行を除き、URL文字列の完全一致で重複を除きます。解決後はDrive/MediaFireのファイルID、その他はフラグメントを除いた正規化URIで同じ実体を除きます。

開始前に確認し、50件以上では一時領域の空き容量と取得・展開時間、本体が大きくなりやすい点を追加警告します。逐次取得して進捗を表示し、取消後は新しいURLを開始しません。進行中の接続・HTML読取り・保存にも取消を渡し、実処理が観測して終わるまで待ちます。

取消までに取得したファイルも共通受付へまとめて渡します。一括取得ではブラウザーを開かず、要ブラウザー・取消未処理を件数で示します。結果の成功件数はダウンロード成功であり、解凍・譜面検出・導入確定の件数ではありません。導入拒否の警告と取得結果を混ぜません。

### 外部APIによる候補取得

「外部APIから入手先を探す」はURL欄でなくMD5を使います。開始時の選択行から、表のMD5、なければ解決した譜面の有効なMD5を選びます。32桁の16進数だけを許可し、空フォルダ用の仮MD5を除き、大小文字を無視して選択順に重複除去します。対象なしは警告、URL取得や導入キューとの競合は未開始とします。

毎回、外部へMD5を送ること、再配布許諾が不明な場合があること、作者・正規配布元を優先すべきこと、一時容量・時間、本体パッケージ取得後は必要に応じて保留画面から統合することを確認します。

| 順序 | 問合せ先 | 採用条件 |
| --- | --- | --- |
| 1 | `https://gingerrush.com/download/package/{md5}` | `downloadURL` がHTTP(S)の直接取得候補。不要な `md5s` 等は検証しない。 |
| 2 | `https://bms.alvorna.com/api/hash?md5={md5}` | `result=success` で `data.song_url` がHTTP(S)の直接取得候補。 |

URLパスから対応する譜面・アーカイブ名を判定できるものだけを候補とし、共有ページHTMLの再解決やAPI応答の任意リンク探索は行いません。候補なし・取得失敗なら次の提供元を試し、ダウンロード成功ならそのMD5を終えます。同じ操作内の成功済み・失敗済みURLは再取得せず、次回操作へその状態を持ち越しません。

MD5ごとに逐次処理し、取消後は新しい問合せ・取得を始めません。取得済みファイルは通常と同じ導入受付へ渡します。結果は対象数、成功、候補なし、成功済み重複、失敗済み重複、サイズ上限、対象外、失敗、取消を区別します。

### ファイル判定と上限

対応譜面は `.bme`、`.bms`、`.bml`、`.pms`、`.bmson`、アーカイブは `.zip`、`.7z`、`.rar`、`.lzh` です。保存名はContent-Disposition、転送後URI、要求URI等から求めます。既知の共有ページでは末尾の拡張子よりページ解決を優先します。HTML型の応答をアーカイブとして保存しません。

`Content-Length=0` は要ブラウザー、宣言長または実コピー量が512 MiBを超えた場合はサイズ上限です。HTML解析は2 MiBまでです。`filename*` をRFC 5987として優先し、`filename` がUTF-8をLatin-1として読んだ文字化けなら復元します。URIの名前はパーセント復号します。

### URL正規化と共有ページ

| 対象 | 処理 |
| --- | --- |
| Driveの `file/d/{id}`、`open?id=`、Docsの `uc?id=` | `drive.usercontent.google.com/download?id={id}&export=download` へ変換する。既存downloadには `export=download` を付ける。 |
| Dropbox | `/scl/fi/` に `dl=1` を設定し、旧直リンクは `dl.dropboxusercontent.com` へ変換する。 |
| OneDrive | `onedrive.live.com/redir` を `download` へ変換する。 |

正規化できない場合は元の許可済みURLを使います。

| HTMLの取得元 | 解決方法と制限 |
| --- | --- |
| Google Drive / usercontent | `download-form` のGETフォームとhidden入力。actionはDrive系ホストのHTTP(S)だけ。 |
| MediaFire | `downloadButton` または `popsok` のリンク。MediaFire自身またはそのサブドメインだけ。 |
| `manbow.nothing.sh/event.cgi` | `DownLoadAddress` / `DownloadAddress` 付近のリンク。配布先として他の対応ページを辿れる。 |
| `venue.bmssearch.net` の末尾が数値の作品ページ | 埋込みの `type: CORE` の `downloadURL` またはリンク。イベント全体の `packages` を除き、リンク経由でも復活させない。 |
| `bmssearch.net/bmses/` | 埋込みの `downloads[].url` またはリンク。 |

後二者は直接アーカイブまたは既知の共有ページだけを許可し、他の作品・会場ページへ再帰しません。再帰は最大4段、フラグメントを除いたURIで循環を止めます。解決先にも同じURI・サイズ・ファイル判定を適用します。

Driveフォルダ、JavaScript操作、CAPTCHA、ログイン、同意フォーム、ブラウザーの取得イベントが必要な配布元は自動解決しません。埋込み解析は現在の形式に依存し、形式変更を成功と推定しません。

### 難易度表取得の通信期限

推定表・おすすめ表のHTTP GETとフォームPOSTは、送信開始から本文読取り完了まで一つの要求期限と呼出し元の取消を使います。ヘッダー受信で期限を更新せず、本文I/Oを取り消してから応答・ストリームを破棄します。待機者だけを切り離したり、途中本文を成功で返したりしません。同じバッファ方式の同期GET・フォームPOST・テキストPOSTにも適用します。大容量取得、multipart送信、ストリームを返すAPIの期限とは区別します。

Walkureと参照表の共有取得は非同期で接続し、待機者の取消で他の取得を止めません。排他を取った処理だけが `finally` で解放し、通信中にMonitorやモデルロックを保持しません。

おすすめ表のスコアPOSTは通常失敗で初回＋最大5回、100ms間隔の既存再試行を行い、最終警告後もGETへ進みます。要求自身の時間切れは通常失敗、呼出し元の取消は直ちに伝播して再試行・GETを行いません。期限は1要求のもので、参照表や再試行を含む操作全体の上限ではありません。反映前の失敗・取消は既存表とDBを保持し、更新中状態を必ず解除します。

### 診断と検証境界

`playlist_url_download` と `playlist_external_package_lookup` で解決、対象外、重複、サイズ、保存、失敗、取消を記録します。引渡し後の展開は `auto_install extract_start` / `extract_done` / `extract_failed` に対象、展開先、項目数、経過時間を記録します。

通信期限のテストは要求ごとのHttpClientとTimeProviderで、送信・ヘッダー・本文到達を観測して時計を進めます。実時間の競争ではなく実際の読取りtokenの取消とストリーム解放を確認します。切断確認は後片付けより先に行い、試験側の接続終了を成功に数えません。共有設定やグローバル時計を変更しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| URI境界・共有ページ・サイズ・保存 | [`PlaylistUrlAcquisitionWorkflow`](../../../BeMusicSeeker/Models/Playlist/PlaylistUrlAcquisitionWorkflow.cs) | [`PlaylistUrlAcquisitionOwnershipTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlAcquisitionOwnershipTests.cs) |
| API候補・優先順・重複・取消 | [`PlaylistExternalPackageLookupService`](../../../BeMusicSeeker/Models/Playlist/PlaylistExternalPackageLookupService.cs) | [`PlaylistExternalPackageLookupServiceTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistExternalPackageLookupServiceTests.cs) |
| 通信中の許可と取得後の導入拒否 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs)、[`PackageInstallWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs) | [`PlaylistUrlAcquisitionOwnershipTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlAcquisitionOwnershipTests.cs) |
| 単一期限・本文取消・共有取得・反映前失敗 | [`AppHttpClient`](../../../Ribbit/Net/AppHttpClient.cs) | [`AppHttpClientTests`](../../../BeMusicSeeker.Tests/Runtime/AppHttpClientTests.cs)、[`PlaylistRecommendedTableOwnerTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs) |
| HTMLの再取得なし・相対URI基準 | [`PlaylistExternalSyncOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs) | [`BmsPlaylistExternalLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalLoadTests.cs) |
| 外部同期のURL補完とローカルURIの区別 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`PlaylistUrlCompletionTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs) |

## 関連資料

[ライブラリ変更](../library/mutations.md)、[ドラッグ入力](../library/drop-install.md)、[一時ファイル](../core/managed-temp-files.md)、[正本の保存](storage-and-export.md)を参照します。
