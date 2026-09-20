# プレイ履歴と更新履歴の表示

## 目的と適用範囲

LR2の導入後のプレイ履歴と、beatorajaのスコア更新履歴を、現在のライブラリ・プレイリストに関連付けて表示する契約を定めます。取得元を混ぜず、得られない実績を別の値で代用しません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 取得元の選択

`UseBeatorajaScoreDb` が有効で選択プレイヤーのスコアを実際に読み込んでいる場合はbeatoraja、それ以外はLR2を選びます。同じ画面で両者を合算しません。

LR2は現在の連携プロファイルのスコアDBを読みます。非連携は `SkippedProfile`、パス未設定・欠落・読取不能は `Unreadable` です。履歴の記録構造と明示的な導入・修復は[LR2履歴スキーマ](lr2-history-schema.md)に従います。導入前のスコアから日時を推測して履歴を作りません。

beatorajaは選択プレイヤーの `scorelog.db` を読み取り専用で使います。これはベスト更新ログで、全プレイ履歴ではありません。通常譜面は `mode=0` だけとし、コース等を混ぜません。`scoredatalog.db` はハッシュとモードに対する最新詳細で、追記履歴として使いません。取得失敗を `score.date` 等へ置き換えません。

### 読取り要求と索引

LR2要求はDBパス、連携プロファイルかどうか、開始を含み終了を含まない時刻範囲、確定状態、件数上限を持ちます。通常期間は `FinalizedOnly`、診断は `UnfinalizedOnly` です。内部の全件キャッシュには `All` と `DisableLimit=true` を使います。

スキーマ検査を先に行い、読めない状態では行を問い合わせず診断を返します。`AllowRepairableIndexRead` が`false` なら不足・不一致の索引で停止し、`true` なら警告付きで読めます。期間索引の専用取得は索引不備で常に停止します。`RequireCompleteHistoryTriggers` は独立した条件で、`true` ならトリガー不足・不一致で行照会前に停止します。読込み中に修復しません。

期間索引は日別の代表時刻で、通常一覧の件数上限に影響されません。UIでは全件キャッシュの確定行をローカル日付別にまとめ、各日の最大時刻を使います。beatorajaの診断表示は空で、通常の更新履歴を未確定扱いにしません。

### キャッシュと表示用変換

最初の表示で取得元ごとの全履歴を `PlayHistoryReadCache` へ読みます。期間、診断、上限、検索、整列、表示対象は原則として同じ値から求め、DB照会を繰り返しません。SQLによる取得と行の変換は、取得元別の読取り処理だけが担当します。

LR2のキーはスコアDBパスと連携判定、beatorajaはスコアDB・履歴DBパスとスコアの版です。スコア再読込み、履歴スキーマ操作、取得元・パス変更で破棄し、次回表示時に読み直します。現在のLR2プロファイルと一致する検査結果は設定の状態表示へ共有しますが、設定画面を開くためにDB照会しません。

`PlayHistoryProjectionIndex` が生の記録を現在の所持譜面とプレイリストへ解決します。古い要求・取消で構築が中断したら表示を更新しません。譜面を解決できない行は生のハッシュと診断を持つ未解決行として残せます。これはスキーマ不備や取得失敗を成功へ変える意味ではありません。

### 行の意味

`HistoryId` はLR2の `history_id`、beatorajaの `scorelog.rowid` です。時刻はUnix秒を保持してローカル時刻で表示します。LR2の元ハッシュはMD5、beatorajaはSHA-256です。解決できた場合だけタイトル・作者・パスと `ResolvedChart` を持ちます。

ベストのCLEAR、DJ、RATE、SCORE、BP、COMBOは更新前後を示し、初回BPは値だけを示します。種類は複数の更新を併記でき、他の更新がなければ `play` です。LR2の実プレイ結果は確定したプレイヤー集計差分から求めます。beatorajaには単曲の実績・オプション・演奏時間を作らず空欄にします。ベストDJ/RATEは履歴の旧新スコアと既存スコア読込み時のノーツ数から求め、現在のベスト値を履歴へ混ぜません。

LR2のクリア2はEASYビットありをEASY、なしをASSISTとし、ASSISTビットだけで決めません。クリア2・EASYビットなし・BP負値の行は詳細未保存としてクリア更新だけを表示し、EXSCORE/BP/COMBO/OPTIONを推測しません。オプション履歴は増えたビットと消えたビットの両方を示します。beatorajaの旧BP `int.MaxValue` は未プレイとしてNULLへ正規化します。

### 期間と集計

固定期間はすべて、今日、昨日、今日を含む最近7日・30日、未確定／診断です。通常期間はローカル午前0時から次の境界までの半開区間です。年→月→日の過去日付ツリーもローカル時刻で範囲を求め、降順に並べます。非同期の古い要求は新しい期間選択を上書きしません。

一覧とは別の集計帯に判定数、プレイ数、演奏時間、スコア/BP/COMBO/CLEAR更新数とクリア更新内訳を表示します。beatorajaだけEXH内訳を加えます。更新カードの複数選択はOR、キーワードとはANDで絞ります。カード選択を検索文字列や履歴へ書き込みません。

演奏時間は期間の値で、検索・カード・表示対象による行数変化に追従しません。beatorajaの期間実績は `score.db.player` の累計から、終了境界前の最新値－開始境界前の最新値で求めます。すべてでは最新累計を使います。未確定診断・未対応・読取不能は `-` で、別の値へ代用しません。

### 検索と整列

取得と表示用変換、表示対象の適用後に `GridKeywordSearchContext.PlayHistory` の検索を行い、最後に整列します。検索・整列・表示対象だけの変更は同じ期間の読込み・変換結果を使います。通常譜面一覧とは整列状態を共有せず、既定は時刻降順、同時刻は履歴ID降順です。履歴列が持つ `SortMemberPath` だけを受け付けます。

| フィールド | 意味 |
| --- | --- |
| フィールド指定なし | 題名、作者、パス、フォルダの表示名、プレイリスト名、生のハッシュ、SHA-256、更新種別、取得元、プレイ日時を横断検索する。 |
| `title:` / `artist:` / `path:` | 解決済み譜面の表示情報。未解決行では空。 |
| `folder:` | 表示対象を適用した後の `FolderLabels`。「すべて」では表示用変換時の表記号、表単体では表内フォルダ、プリセットでは一致する難易度表の項目の `org_symbol + level`。`FOLDER: プリセット` では、プリセット外の行を空文字列として扱う。 |
| `playlist:` / `ref:` / `table:` | 参照するプレイリストの表示名。 |
| `md5:` | 解決した譜面のMD5。 |
| `hash:` | 取得元の生ハッシュ。LR2ではMD5、beatorajaではSHA-256。 |
| `sha256:` | 解決した譜面のSHA-256。未解決行では空。 |
| `date:` | ローカル日付。`yyyy-MM-dd`、`yyyy/M/d`、`yyyy/MM/dd`、`yyyyMMdd` は表示上の一日全体、`yyyy/MM/dd HH:mm:ss` はローカル時刻の一秒、`date:"yyyy/MM/dd HH:mm:ss..yyyy/MM/dd HH:mm:ss"` は両端を含む範囲に一致する。 |
| `year:` | ローカル時刻の年との完全一致。 |
| `month:` | ローカル時刻の月との完全一致。`M`、`MM`、`yyyy-M`、`yyyy-MM`、`yyyy/M`、`yyyy/MM` を受け付ける。 |
| `type:` / `kind:` | `Kind` とLR2の `score_write_type`。主名は `type:`、互換別名は `kind:`。 |
| `clear:` | LR2のベストクリアの変更前後の表示。`HC` など、既存のクリア種別の別名を使える。 |
| `oldclear:` | LR2のベストクリアの変更前の表示。クリア種別の別名も使える。 |
| `newclear:` | LR2のベストクリアの変更後の表示。クリア種別の別名も使える。 |
| `finalized:` | `true` / `false`、`1` / `0`、`finalized` / `unfinalized` / `pending` を真偽値として扱う。 |
| `source:` | 取得元の表示名とパス。 |

日付・年・月は部分一致でなく完全一致です。日時と範囲はローカル壁時計の秒で比較し、オフセットへ変換しません。日付範囲の両端は含みます。不正、逆順、空、片側なし、複数区切り、小数秒、オフセット、比較演算形式は警告して不一致とし、否定でも全件一致に変えません。有効なOR候補は通常どおり評価します。フィールドなしの貼付け日時を日付条件へ昇格しません。正規表現を指定した場合だけ表示文字列へ適用します。

2行以上の選択でメニューを開くと、その時点の表示時刻の最小・最大を固定し、`date:"start..end"` を検索末尾へ追加できます。クリック前の選択・整列変更では値を変えず、同秒でも範囲で表します。

### 表示対象とプリセット

選択肢は、すべて、プリセット、`FOLDER: プリセット`、表単体の順です。すべては行を絞らず既存の表記号を列挙します。プリセットは一致する譜面へ絞り `org_symbol + level`、FOLDER専用プリセットは行を残して一致しないFOLDERだけ空欄、表単体はその表へ絞り表内フォルダを示します。追加の全件走査で「すべて」の表示を補いません。

プリセットJSONは名前と `PlaylistId` への参照だけを保存します。存在しないIDは一致せず、次の編集保存で除かれます。表の正本・譜面・更新日時は変更しません。選択は項目オブジェクトではなく表示対象の識別値を保存し、空と `all` はすべてです。まだ候補がなければ画面だけすべてにし、希望する識別値を保って候補出現時に復元します。一時的な未選択通知で保存した識別値を消しません。

表の変更通知中に読込みロックを同期取得し直しません。候補再構築はUIへ遅延し、通知が重なれば最新の版まで再構築します。必要な譜面の遅延読込みは使えますが、表全体の再読込み・外部同期・出力を起動しません。

プリセット編集は名前・検索・仮想化された有界の一覧を持ちます。検索は表示名全体への現在文化圏の大小文字無視の部分一致で、空白だけなら全件です。非表示になった選択も保存対象にし、検索文字列は編集セッション外へ持ち越しません。OK/取消はスクロール・サイズ変更によらず到達できます。

### 外部操作と診断

外部操作は右クリックした一行を対象とし、クリック時に現在のハッシュ・パス・設定を再解決します。未解決でもハッシュだけのWeb操作は使えますが、パスが必要な起動をしません。詳細は[外部起動](../ui/external-launch.md)に従います。解決済み行の明示ドロップだけがプレイリストを編集し、未解決行を含む選択は全体を拒否します。

診断は取得元、段階、重大度、コード、説明、パスを持ち、集約テキストとログへ出します。`play_history_lr2_schema_*` と `play_history_projection_*` を区別し、未導入や失敗をスコア・回数から代用しません。最終プレイ順の固定SQL出力は[LR2出力](lr2-custom-folders.md)に従います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 取得元・期間・確定状態・未解決行・累計差分 | [`Lr2PlayHistoryReader`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2PlayHistoryReader.cs)、[`BeatorajaPlayHistoryReader`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BeatorajaPlayHistoryReader.cs) | [`PlayHistoryReadModelTests`](../../../BeMusicSeeker.Tests/PlayHistoryReadModelTests.cs)、[`PlayHistoryReadSourceSelectorTests`](../../../BeMusicSeeker.Tests/PlayHistoryReadSourceSelectorTests.cs) |
| 検索構文・日時範囲・表示フィルター | [`PlayHistoryReadCache`](../../../BeMusicSeeker/ViewModels/PlayHistoryReadCache.cs) | [`GridKeywordSearchQueryTests`](../../../BeMusicSeeker.Tests/GridKeywordSearchQueryTests.cs)、[`ChartListFilterViewModelTests`](../../../BeMusicSeeker.Tests/ChartListFilterViewModelTests.cs) |
| 実画面の期間・明示メニュー操作・対象変更 | [`PlayHistoryWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/PlayHistoryWorkflowOwner.cs) | [`MainWindowPlayHistoryWpfTests`](../../../BeMusicSeeker.Tests/MainWindowPlayHistoryWpfTests.cs)、[`MainWindowContextMenuResourceTests`](../../../BeMusicSeeker.Tests/MainWindowContextMenuResourceTests.cs)。実入力とフォーカスは[入力操作の明示受入](../development/testing.md#入力操作の明示受入)。 |
| プリセットの検索・選択保持・有界表示 | [`PlayHistoryFolderDisplayPresetEditor`](../../../BeMusicSeeker/ViewModels/PlayHistoryFolderDisplayPresetEditor.cs) | [`DialogPresentationTests`](../../../BeMusicSeeker.Tests/DialogPresentationTests.cs)、[`SettingDialogCustomFolderOutputBaseTests`](../../../BeMusicSeeker.Tests/SettingDialogCustomFolderOutputBaseTests.cs) |

## 関連資料

[LR2履歴スキーマ](lr2-history-schema.md)、[ランプビューア](lamp-viewer.md)、[検索支援](../ui/keyword-search.md)を参照します。
