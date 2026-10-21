# URL・外部APIからの取得と導入引渡し

## 目的と適用範囲

プレイリスト詳細の本体・差分URL、または譜面MD5からファイルを取得し、一回の導入要求へまとめて渡す契約を定めます。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 受付とURIの境界

取得は変更不能入力の確保です。導入との受付分離は[競合ポリシー](../core/operation-concurrency-policy.md#音声と独立した読取り)に従います。全取得pathとその所有情報を一つの `DroppedInstallBatchRequest` にまとめ、`PackageInstallWorkflowOwner.TryEnqueue` へ一度渡します。未受理なら `Warn_PackageInstallUnavailable` を一度表示します。導入の予約、ブラウザー代替、自動再試行へ変換しません。取得成功と導入受理・確定を別々に報告します。

アプリ所有stagingは未受理時に要求が一回だけ回収し、取得Taskは回収の実終端を待ちます。受理した入力の寿命は待ち行列へ移し、導入開始前の取消・世代退役でも未消費入力の回収と実idleまで待ちます。導入開始直前に導入側へ所有権を移し、その後は[導入側の保全・回収規則](../core/managed-temp-files.md#削除の時機)に従います。取得Taskの終端では無条件に削除しません。

実推定後も保留へ残る単体譜面は、保留理由によらず実ファイルとバイト列を保持し、後の明示手動導入に使えます。導入の入力使用終端は、保留入力の寿命の終端ではありません。持込み入力・利用者指定保存物へ削除を広げません。

初期URI、HTTP転送後の最終URI、HTML/JSONから辿る各URIはHTTP(S)だけを許可します。ローカルパス、UNC、`file:`、FTP、独自スキームは入力エラーとして取得・一時保存・ブラウザー代替・登録を始めません。これは保存済み表の外部同期元がローカルパスを扱う契約とは別です。

### 単体URLと複数行

右クリックの「本体URLをブラウザで開く」「差分URLをブラウザで開く」は、有効行が1行なら指定列の元の絶対HTTP(S) URLをブラウザーで一度開きます。`AutoInstall` に依存せず、取得用の共有URL正規化、通信、一時保存、導入、確認、取得進捗、ツリー展開を始めません。0行、空・相対・非HTTP(S) URLは無作用です。ブラウザー起動は既存のUI配送を使い、その終端を待ち、起動・配送の失敗は呼出元の通知経路へ返します。取得中の入口抑止とメニュー可否は維持します。

URLセルクリックの単体操作は、`AutoInstall` が有効で起動時ファイル検査を省略していなければ、自動取得を先に試みます。対応ファイルを得たら導入へ渡します。サイズ上限超過と不対応スキーム、導入未受理ではブラウザーを開きません。自動取得条件を満たさない場合も、通常取得失敗や未対応ページの場合も、許可済みHTTP(S)の元URLをブラウザーで開きます。

有効行が2行以上の右クリックは「選択中の本体URLを取り込む」「選択中の差分URLを取り込む」となり、開始時の行を固定し、選んだ列だけを使います。空・非絶対URI・非プレイリスト行を除き、URL文字列の完全一致で重複を除きます。重複除去後が1URLでも複数行の確認・一括取得として扱います。解決後はDrive/MediaFireのファイルID、その他はフラグメントを除いた正規化URIで同じ実体を除きます。

開始前に確認し、50件以上では一時領域の空き容量と取得・展開時間、本体が大きくなりやすい点を追加警告します。逐次取得して進捗を表示し、取消後は新しいURLを開始しません。進行中の接続・HTML読取り・保存にも取消を渡し、実処理が観測して終わるまで待ちます。

取消までに取得したファイルも共通受付へまとめて渡します。一括取得ではブラウザーを開かず、要ブラウザー・取消未処理を件数で示します。結果の成功件数はダウンロード成功であり、解凍・譜面検出・導入確定の件数ではありません。導入拒否の警告と取得結果を混ぜません。

引渡し後の入力の寿命は[導入後の入力の寿命](../library/drop-install.md#導入後の入力の寿命)と[一時ファイル](../core/managed-temp-files.md)を参照します。

### 外部APIによる候補取得

「外部APIから入手先を探す」はURL欄でなくMD5を使います。開始時の選択行から、表のMD5、なければ解決した譜面の有効なMD5を選びます。32桁の16進数だけを許可し、空フォルダ用の仮MD5を除き、大小文字を無視して選択順に重複除去します。対象なしは警告、同じ取得機能の実行中は未開始とします。導入中でも取得そのものは可能で、引渡し時の受理を別に判定します。

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

### 外部表の通信期限

通常の外部難度表の HTTP GET と汎用フォーム POST は、送信開始から本文読取り完了まで一つの要求期限と呼出し元の取消を使います。ヘッダー受信で期限を更新せず、本文 I/O を取り消してから応答・ストリームを破棄します。途中本文を成功で返しません。同じバッファ方式の同期 GET・フォーム POST・テキスト POST にも適用します。大容量取得、multipart 送信、ストリームを返す API の期限とは区別します。

内蔵の推定表・リコメンド表は通信せず、[ローカル計算の契約](local-recommendations.md)に従います。一般の外部表取込み、URL 補完、利用者のダウンロードの通信と取消は維持します。

### 失敗と事後通知の境界

通常の通信・本文I/O・入力形式の失敗は取得対象内で扱い、単体のブラウザー代替や外部APIの次候補という既存の条件を維持します。内部故障・資源不足・未解決のstaging回収は通常取得失敗へ変換せず、操作を止めて所有する入力を回収します。所有する取消し要求がない例外を利用者取消し件数へ加えません。

進捗、結果・受付拒否の案内、診断の失敗は局所で記録し、取得済みファイルの導入引渡しや取得結果へ戻しません。進捗は購読先ごとに分離し、他の購読先への終端通知を妨げません。開始確認、導入への引渡し、入力使用の実終端、必要な回収は任意通知ではありません。

取得途中の読取り・書込み失敗、サイズ超過、取消しでは、自分が作成した未完了ファイルを回収します。保存と応答の解放を終える前に取得成功を返しません。応答破棄で取得物を渡せなかった場合も回収し、回収自体が失敗した場合は元の取得失敗と未回収を隠しません。

### 任意のURL補完

補完元の通常の通信・ファイル読取り・JSON形式の失敗では、利用可能な既存補完を保持します。内部故障や資源不足を取得元不在へ読み替えず、予約された作業を失敗として終えます。モデルが所有する停止要求は取消しとして返し、退役した表へ結果を適用しません。

TSVとStellaの開始済み取得はともに終端まで待ちます。複数の取得元が失敗した場合の元例外はその合流で診断し、UI用に保持しません。診断故障は予約、キャッシュ適用、必須参照更新の成否を変えません。既存の同一取得元キャッシュと明示要求による再取得を維持し、自動再試行は追加しません。

### 診断と検証境界

`playlist_url_download` と `playlist_external_package_lookup` で解決、対象外、重複、サイズ、保存、失敗、取消を記録します。引渡し後の展開は `auto_install extract_start` / `extract_done` / `extract_failed` に対象、展開先、項目数、経過時間を記録します。

通信期限のテストは要求ごとのHttpClientとTimeProviderで、送信・ヘッダー・本文到達を観測して時計を進めます。実時間の競争ではなく実際の読取りtokenの取消とストリーム解放を確認します。切断確認は後片付けより先に行い、試験側の接続終了を成功に数えません。共有設定やグローバル時計を変更しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 単体右クリックの元URL配送と取得なし | `PlaylistWorkspaceViewModel.PlaylistUrlAcquisition` | `PlaylistUrlAcquisitionOwnershipTests.ContextMenuSingleUrl_*` は本体・差分、自動導入ON/OFF、元の共有URL、UI配送一回、取得・保存・導入等の無作用、空・不正入力、配送待機を確認する。起動・配送の例外伝播は `BrowserFallbackSinkExceptionIsNotSuppressed` / `BrowserFallbackPresentationFailureIsPropagatedThroughAwaitableScheduler` でセルクリックと分担する。 |
| 複数行の重複除去と取得中抑止、セルクリックの維持 | 同上 | `ContextMenuDuplicateRows_ConfirmsAndImportsOnceWithoutBrowser` は同じURLの別二行で確認前未取得・一回取得と引渡しを確認する。既存の取消・入力所有権・二取得物の集約は維持する。`PlaylistUrlContextMenuAvailability_DisablesActionsWhileDownloading` は通信待機中の単体右クリックも抑止し、`WorkspaceSizeLimitDoesNotBrowserFallback` は宣言サイズ超過を小さい応答で確認する。 |
| 実メニューの表示とセル・右クリックの別配送 | `MainWindowPlaylistWorkspaceTerminals`、`MainWindow` | `MainWindowPlaylistWorkspaceWpfTests.PlaylistUrlBulkImport_IsOwnedByWorkspaceAndShellForwarded` は通常・未所持の実メニューの表示・Click、単体・複数行の固定行と列区分、選択外行、セルの別入口への配送を確認する。辞書整合は `LocalizationResourceParityTests`、取得用URL正規化は `MainWindowContextMenuResourceTests` に分担する。 |
| 進捗・結果・診断の失敗、通常通信と内部故障、未引渡し入力の回収 | `PlaylistWorkspaceViewModel.PlaylistUrlAcquisition`、`PlaylistUrlAcquisitionWorkflow`、`PlaylistExternalPackageLookupService` | `PlaylistUrlAcquisitionOwnershipTests` の単体・一括引渡し、結果表示失敗後の次要求、`DownloadFailureReclaimsUnpublishedInputAndPreservesUnresolvedCleanup`、`PlaylistExternalPackageLookupServiceTests` の通常期限切れ・内部故障・回収不能。確認失敗と導入利用終端の既存保証を維持する。 |
| URI境界・共有ページ・サイズ・保存 | [`PlaylistUrlAcquisitionWorkflow`](../../../BeMusicSeeker/Models/Playlist/PlaylistUrlAcquisitionWorkflow.cs) | [`PlaylistUrlAcquisitionOwnershipTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlAcquisitionOwnershipTests.cs) |
| API候補・優先順・重複・取消 | [`PlaylistExternalPackageLookupService`](../../../BeMusicSeeker/Models/Playlist/PlaylistExternalPackageLookupService.cs) | [`PlaylistExternalPackageLookupServiceTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistExternalPackageLookupServiceTests.cs) |
| 取得完了と取消の交差、取得済み入力の集約 | [`PlaylistExternalPackageLookupService`](../../../BeMusicSeeker/Models/Playlist/PlaylistExternalPackageLookupService.cs)、[`PlaylistWorkspaceViewModel.PlaylistUrlAcquisition`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs) | `LookupService_StopsAtFirstProviderWhenDownloadSucceeds` は成功返却直前の取消でも成功pathを保持する。`DownloadedPackages_CancellationDuringResponseDisposalHandsOffCompletedDownloadsOnce` は実保存後の応答解放中に取り消し、開始済み取得の終端、取得済み2件の一回引渡し、API・URL一括それぞれで次対象の未開始と受理後のstaging保持を確認する。導入の受理・利用終端の条件は既存の所有テストに分担する。 |
| 通信中の許可と取得後の導入拒否 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs)、[`PackageInstallWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs) | [`PlaylistUrlAcquisitionOwnershipTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlAcquisitionOwnershipTests.cs) |
| 受理後の保留入力と後の明示導入 | `PlaylistWorkspaceViewModel.PlaylistUrlAcquisition`、`DroppedInstallBatchRequest`、`PackageInstallWorkflowOwner` | `PlaylistUrlAcquisitionOwnershipTests.DownloadedChart_RealEstimationRetainsPendingBytesForLaterManualInstall` は単体・複数URL・APIから実導入・推定、SQLiteとFSを通し、取得Taskと実idle後の保留正本・install行・バイト列、後の手動導入先・song行・所持を確認する。 |
| 未受理と開始前取消・世代退役の回収終端 | 同上 | `DownloadedInput_UnacceptedWaitsForCleanupAndPreservesBothFailures` は実削除gateで取得Taskの待機、一回の回収、元故障と回収原因の保持を確認する。`DownloadedInput_CancellationBeforeStartReclaimsOwnedSourceBeforeIdle` はURLが生成した所有要求を実queueへ入れ、開始前の取消／退役でport未呼出し、入力削除、実idle後の次の明示受付を確認する。 |
| 開始後の入力保持と導入の実終端 | `PlaylistWorkspaceViewModel.PlaylistUrlAcquisition`、`PackageInstallWorkflowOwner` | `DownloadedInput_HoldsStagingUntilActualInstallTerminalWhilePlaylistIsBusy` はP中の読取り取得、実ownerの導入中の可読性と取得Taskの待機、通常／故障終端後の入力保持を確認する。適用前の管理入力回収と適用後の保全は[導入側の検証](../library/drop-install.md#実装とテストの対応)に分担する。 |
| 単一期限・本文取消・接続解放 | [`AppHttpClient`](../../../BeMusicSeeker/Ribbit/Net/AppHttpClient.cs) | [`AppHttpClientTests`](../../../BeMusicSeeker.Tests/Runtime/AppHttpClientTests.cs) |
| HTMLの再取得なし・相対URI基準 | [`PlaylistExternalSyncOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs) | [`BmsPlaylistExternalLoadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalLoadTests.cs) |
| 補完元の通常失敗・内部故障と退役 | `BMSPlaylist.UrlCompletion` | `PlaylistUrlCompletionTests.BMSPlaylist_UrlCompletionDistinguishesUnavailableSourceFromInternalFailure` は旧補完と次の明示要求、`BMSPlaylist_UrlCompletionUsesInjectedOptionsAndRetiredResultDoesNotApply` は停止token、取消終端と旧行への適用拒否を確認する。 |
| 外部同期のURL補完とローカルURIの区別 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`PlaylistUrlCompletionTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistUrlCompletionTests.cs) |

## 関連資料

[ライブラリ変更](../library/mutations.md)、[ドラッグ入力](../library/drop-install.md)、[一時ファイル](../core/managed-temp-files.md)、[正本の保存](storage-and-export.md)を参照します。
