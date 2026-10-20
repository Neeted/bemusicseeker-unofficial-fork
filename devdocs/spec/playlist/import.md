# プレイリストのインポート

## 目的と適用範囲

外部難易度表、内蔵の難度推定表・リコメンド、直接指定したURIやJSONファイルを一つの選択一覧へ集め、一回の要求として読み込みます。候補の表示と選択はインポート画面が所有し、取得・登録・必要な出力・完了通知はプレイリストの既存処理が所有します。

beatorajaの設定に保存されたURLを取り込む操作は、[beatorajaのTable URL取込み](table-url-import.md)を参照します。

## 用語

共通語義は[用語集](../glossary.md)に従います。

- **候補**: URI、表示名、記号、分類、入力元からなる画面用の値です。可変の登録済み表を直接保持しません。
- **選択一覧**: 今回の画面で利用者が選んだ候補を、初めて選んだ順で保持する一時的な集合です。
- **同URL**: 絶対URIの `AbsoluteUri` 文字列が `Ordinal` で一致することです。パスやクエリの大文字小文字を独自に同一視しません。
- **同名候補**: 候補の表示名が既存表の名前と `Ordinal` で一致するものです。登録時の同名判定は取得後の実際の表名を使います。

## 仕様

### 画面と入力元

プレイリストルートの右クリックメニューにある「プレイリストをインポート」から、メインウィンドウを所有者とするサイズ変更可能なモーダルウィンドウを開きます。初期サイズは920×680、最小サイズは760×500です。左側に入力元のタブと候補、右側に常時表示する選択一覧を配置します。件数と全解除は選択一覧の下、取消と実行は画面の下部に置きます。候補と選択一覧は独立してスクロールします。

| 入力元 | 候補と操作 |
| --- | --- |
| 難易度表リスト | 起動時の任意取得で公開された外部カタログを利用します。第一分類、第二分類、表の順に表示し、第二分類がない表は第一分類の直下へ置きます。分類の展開と選択は別の操作です。 |
| 内蔵表 | EASY / NORMAL / HARD / FC の難度推定と、標準・未プレイをFAILED扱い・FAILEDを未プレイ扱いの三種類のリコメンドを選びます。生成規則は[内蔵表の仕様](local-recommendations.md)に従います。 |
| URL・ファイル | URIを複数行で入力します。JSONファイルの選択結果も入力欄へ追記し、利用者が「選択に追加」を押して確定します。 |

カタログの未開始・取得中・取得成功・取得失敗を区別して表示します。空のカタログは取得失敗に読み替えません。他の入力元はカタログ取得の成否によらず使えます。画面を開く操作による再取得や、閉じる操作によるカタログ取得取消は行いません。

左右の項目は名前と短い注意ラベルを一行で表示し、注意は名前領域の右端に揃えます。選択一覧の入力元ラベルは表示しません。長い名前は省略表示と全文ツールチップで扱います。候補ツリーは入力や選択一覧と同じ背景・枠・角丸を使い、チェック済みの行は右端まで選択一覧と同じ選択色で表示します。文字、矢印、チェックの中間状態、背景、枠、ホバー、選択、フォーカスは既存テーマ資源に従い、表示中のLight/Dark切替も反映します。

検索欄の右に外部候補全体の小さなチェックを置き、共通の非表示フィルターは横並びにします。空のカタログ状態表示や操作メッセージは余分な高さを取りません。左側の二重余白を避け、残りの高さを候補表示に割り当てます。

候補ツリーはピクセル単位でスクロールします。ホイールとスクロールバーは、展開された分類全体の高さに吸着せず、行の途中まで移動できます。同じ内容・開閉状態・表示寸法では、位置だけを変えてもスクロール範囲とつまみの比率を変えません。分類の開閉や表示条件で内容が変わる場合は、その高さに応じて範囲を更新します。

### 検索・表示フィルター・選択

検索は外部難易度表を対象に、前後の空白を除いた一つの文字列を、表名・記号・第一分類・第二分類・URLの `AbsoluteUri` へOrdinalIgnoreCaseで部分一致させます。URL全体、ドメインやパスの一部でも検索できます。検索構文や単語分割、異なる項目を連結した照合はありません。通常一覧と同じWPF Bindingの `Delay=500` を使い、入力が500ms止まった後に反映します。独自のタイマーや背景処理は設けません。

外部の初期表示では、子分類を持つ分類を展開し、表だけを持つ末端分類を折りたたみます。典型的な構造ではSP・DP・PMS・EVENTを開き、General・Personal・Self-made Chart Only・BMS Eventを閉じます。第二分類がない第一分類は折りたたみ、直下の表と子分類が混在する第一分類は展開します。判定にはフィルター適用前のカタログ構造を使い、子分類が非表示になっても既定の開閉を変えません。内蔵表は初回に全分類を展開します。

分類名・行の余白・矢印は単一クリックで開閉し、チェック操作では開閉しません。通常時の開閉状態は画面セッション内で保持し、フィルターによる一時的な非表示やカタログ・言語更新を経ても同じ分類へ戻します。内蔵分類は翻訳後の表示名で識別しません。検索中は検索と非表示条件の両方に一致する表と祖先分類を表示して展開し、検索解除後は検索前の通常開閉状態に戻します。外部検索は内蔵の表示・開閉へ影響しません。

「同URLの登録済みを非表示」は初期ON、「同名候補を非表示」は初期OFFです。外部・内蔵の共通位置に置き、両方の見え方に作用させます。URL・ファイル入力や選択一覧には作用しません。

外部の候補階層はカタログ更新時、内蔵の候補階層は画面セッション作成時に構築します。検索・非表示条件・登録済み表・言語の変更では階層を作り直さず、既存行の表示条件と必要な表示プロパティを更新します。条件外の葉と、条件一致する子孫を持たない分類は行全体を非表示にします。条件一致と展開状態は別に扱い、折りたたまれた子孫も選択集約の対象にします。

分類の左チェックは、表示条件に一致する子孫のURIを重複除去して集約します。未選択はチェックなし、一部選択は横線、全選択はチェックで示します。未選択・一部選択を押すと条件一致する対象をすべて選び、全選択を押すと対象を解除します。折りたたまれた分類の子孫も対象にし、条件外の選択は維持します。対象が空なら未選択かつ操作不可です。検索欄右の「すべて」は同じ規則で外部候補全体を操作し、内蔵の無関係な選択には作用しません。内蔵の一括操作には分類チェックを使います。

同じURIが複数の分類や入力元に現れても、選択は一つです。選択解除はすべての出現箇所へ反映します。解除した後の再選択は末尾へ追加します。タブ切替、検索、表示フィルター、カタログの更新で選択を消しません。

選択一覧は一件解除と全解除を提供します。直接URIの表名は取得前に推測しません。既存表のページURL・絶対ヘッダーURLとの一致は「同URL」、候補名の一致は「同名候補」と表示し、両方に該当する場合は「同URL」を優先します。正常な候補と名前未取得の直接URIには状態ラベルを表示しません。「同URL」は実行時の除外予定、「同名候補」は取得後の確認に先立つ注意表示です。同名候補は実行候補数に含め、選択件数は実行候補数と同URL件数の和です。

### URI入力とファイル選択

各行の前後の空白を除き、空行を無視して `Uri.TryCreate` の絶対URIとして検証します。HTTP(S)、`file:`、Windowsドライブ・UNCパス、`bmseeker:` など、従来の絶対URI入力を受理する範囲を維持し、独自のスキーム制限は設けません。

有効な行を初出順で共通の選択へ追加し、既存の選択順と重複除去を維持します。無効な行があれば、その行だけを案内し、修正できるよう入力原文全体を残します。修正して再追加しても、追加済みURIは重複・再配置せず、新しいURIだけを末尾へ追加します。すべて有効なら、全件が追加済みの場合も入力と案内を空にします。全行が無効なら選択を変えず、原文と無効行の案内を残します。空または空白だけの入力は選択と入力を変えず、入力案内を表示します。選択への追加では受付・通信・登録を行わず、実行操作でだけ共通受付へ渡します。

ファイル選択は共通の `IUiDialogService.PickFileAsync` を使います。取消は入力を変更せず、選択成功時は入力欄へ追記します。失敗を取消や成功として扱いません。

### 受付・重複・結果

全入力元を `TryStartPlaylistImport` の一要求へ渡します。URIの重複は初出順で除き、準備・終了状態を確認してPを非待機取得します。競合する要求を予約・再試行しません。

| 受付結果 | 画面の挙動 |
| --- | --- |
| `Started` | 対象を既存の一括処理へ引き渡して閉じます。取得後の成否は完了通知で確認します。 |
| `Busy` | 他操作の実行中を表示し、選択を保持します。 |
| `NotReady` | プレイリストの準備未完了を表示し、選択を保持します。 |
| `ShuttingDown` | 新規受付の終了を表示し、開始しません。 |
| `NoTargets` | 空選択または全件が同URLで除外されたことを件数とともに表示し、選択を保持します。インポート完了通知は出しません。 |

P取得後に最新の既存表を短い読取り区間で捕捉し、ページURLと絶対ヘッダーURLを照合します。画面を開いた後の登録もここで反映します。相対ヘッダーURLは表のページURLを基準に解決します。通信中にモデルの読取りロックを保持しません。

残った対象を選択順で取得します。別URIが取得後に同じ表名へ解決された場合は、先に登録対象となる表を残し、後続を同名として除外します。候補リストの表示名でこの判定を代用しません。既存処理の一括登録、必要なLR2/BMT出力、参照・表示公開、結果イベントの発行まで同じPを保持します。必要出力などが集めた警告と集計は一つの完了イベントで画面へ渡します。画面は警告と集計を順に表示しますが、通知画面の完了は同期待ちせず、結果の発行後にPを解放します。

完了結果は成功、同URL除外、同名除外、失敗を別々に集計します。一部の取得・登録失敗は成功済み対象を巻き戻しません。登録後の必須出力の失敗は、既存の確定DBを保持したまま失敗として通知します。

### 寿命と保存

画面の検索・フィルター・通常の分類開閉・URI入力・選択は一時状態で、再表示時に初期状態へ戻します。取消、Esc、閉じる操作は未開始の選択を破棄します。開始済みインポートのTaskと取消は画面へ移管しません。

開いている間はカタログ、登録済み表、言語の変更を反映し、閉じる際に購読を解除します。メインウィンドウの終了処理は子画面を閉じ、その後片付けを待ちます。受理済みインポートの終了は既存のアプリケーション終了経路が待ちます。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| URI単位の共通選択、検索、階層、フィルター、入力検証 | [PlaylistImportDialogViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistImportDialogViewModel.cs) | [PlaylistImportDialogViewModelTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistImportDialogViewModelTests.cs)。入力元をまたぐ重複・順序、URLを含む部分一致検索、三状態集約と条件一致対象だけの一括選択・解除、外部/内蔵の共通フィルターのURI集合、元構造に従う初期展開、開閉保持と検索後復帰、混在入力の有効行追加・原文と無効行案内の保持、修正再追加の順序・重複除去、全無効・空入力、絶対URI形式の受理、開始時だけの受付を確認。 |
| 三入力元、受付結果と画面の開閉、非同期ファイル選択、配置とテーマ | [PlaylistImportDialog](../../../BeMusicSeeker/Views/Playlist/PlaylistImportDialog.xaml)、[コード](../../../BeMusicSeeker/Views/Playlist/PlaylistImportDialog.cs) | [PlaylistImportDialogTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistImportDialogTests.cs)。実コントロールからの混在入力追加と原文・案内のBinding、開始時だけの受付、開閉とチェックの分離、葉と空分類の表示・非表示、遅延検索、ホイールとスクロールバーの画素移動・範囲とつまみの安定性、行の配置と全幅選択、ライブテーマ切替、受付拒否時の保持、pickerの結果・取消・失敗を確認。 |
| 共通チェックの中間表示と既存操作 | [CanonicalControls](../../../BeMusicSeeker/Themes/CanonicalControls.xaml) | [PlaylistSummaryBulkEditTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) の `PlaylistSummaryBulkEditDialog_BindsAllCustomFolderOptionsTwoWayAndLocalized` が全出力種別の双方向接続に加え、中間表示・標準三状態循環・二状態の実Toggleを確認。インポート専用の集約操作は共通の循環へ移さない。 |
| カタログの取得状態・世代、表示中の変更と購読解除 | [ExternalTableList](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalTableList.cs)、[ImportDialog接続](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ImportDialog.cs) | [PlaylistWorkspaceExternalSourceTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs) の `ExternalTableListCatalog_*` が低速取得、空の成功と取得失敗、終了取消、旧応答の非反映を確認。画面の反映・解除は `PlaylistImportDialogTests` が分担。 |
| 最新URL再照合、型付き受付、一括処理、通信・必要出力中のP | [ExternalPlaylistImport](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistImport.cs)、[受付契約](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistImportContracts.cs) | `PlaylistWorkspaceExternalSourceTests.TryStartPlaylistImport_*` が全除外、混在結果、Busy後の次明示要求、HTTP・BMT出力の実終端を確認。 |
| 成功・同URL・同名・失敗の集計、内蔵推薦の三方針 | [ExternalPlaylistImportQueueSummary](../../../BeMusicSeeker/ViewModels/Playlist/ExternalPlaylistImportQueue.cs) | [ExternalPlaylistImportQueueTests](../../../BeMusicSeeker.Tests/Playlist/ExternalPlaylistImportQueueTests.cs) が件数・対象URI、[PlaylistWorkspaceActionWorkflowTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) の `PlaylistWorkspaceRecommendedImport*` が実スコアからの三方針と準備前拒否を確認。 |
| ルートの右クリック入口、所有モーダル、終了時の閉鎖と後片付け | [MainWindow](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[XAML](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) | [MainWindowPlaylistWorkspaceWpfTests](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)。実ルートへのメニュー接続とそこからの所有者設定、独立ボタン撤去、閉鎖後の再表示と終了時の寿命を確認。`PlaylistImportDialog_StartedClosesBeforeWorkspaceImportFinishes` は開始後のHTTP・登録・出力の継続と、結果画面の応答前にPを解放することを確認。`PlaylistImport_Lr2OutputFailurePreservesCommittedDatabaseAndReleasesAdmissionBeforeWarningResponse` は実LR2出力失敗時の保存済みDBと非空の警告・集計を維持し、警告への応答前にPを解放することを確認。 |

## 関連資料

- [プレイリストの保存と更新](storage-and-export.md)
- [内蔵の難度推定表とリコメンド](local-recommendations.md)
- [beatoraja向けBMT出力](bmt-export.md)
- [ダイアログの表示境界](../ui/dialogs.md)
- [操作の競合ポリシー](../core/operation-concurrency-policy.md)
