# プレイ履歴と更新履歴の表示

## 目的と適用範囲

LR2の導入後のプレイ履歴と、beatorajaのスコア更新履歴を、現在のライブラリ・プレイリストに関連付けて表示する契約を定めます。取得元を混ぜず、得られない実績を別の値で代用しません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 取得元の選択

通常成績の取得操作が選んだ取得元を履歴・集計にも使います。`UseBeatorajaScoreDb` が有効ならbeatoraja、それ以外はLR2です。通常成績が失敗しても別の情報源へ代替せず、通常成績と履歴の利用可否を独立して保持します。取得後の外部ファイル移動・消失で表示時の取得元を変更せず、同じ画面で両者を合算しません。

LR2は現在の連携プロファイルのスコアDBを読みます。非連携は `SkippedProfile`、パス未設定・欠落・読取不能は `Unreadable` です。履歴の記録構造と明示的な導入・修復は[LR2履歴スキーマ](lr2-history-schema.md)に従います。導入前のスコアから日時を推測して履歴を作りません。

beatorajaは選択プレイヤーの `scorelog.db` を読み取り専用で使います。これはベスト更新ログで、全プレイ履歴ではありません。通常譜面は `mode=0` だけとし、コース等を混ぜません。`scoredatalog.db` はハッシュとモードに対する最新詳細で、追記履歴として使いません。取得失敗を `score.date` 等へ置き換えません。

### 読取り要求と索引

LR2要求はDBパス、連携プロファイルかどうか、開始を含み終了を含まない時刻範囲、確定状態、件数上限を持ちます。通常期間は `FinalizedOnly`、診断は `UnfinalizedOnly` です。通常成績と同時に所有する全履歴には `All` と `DisableLimit=true` を使います。

スキーマ検査を先に行い、読めない状態では行を問い合わせず診断を返します。`AllowRepairableIndexRead` が`false` なら不足・不一致の索引で停止し、`true` なら警告付きで読めます。通常履歴・期間索引の投影は索引不備で停止します。取得操作は通常履歴とランプの用途和集合として `AllowRepairableIndexRead=true`、`RequireCompleteHistoryTriggers=false` で一度読み、捕捉した同じスキーマ検査結果へ各用途の条件を適用します。`RequireCompleteHistoryTriggers` は独立した条件で、`true` ならトリガー不足・不一致で行照会前に停止します。読込み中に修復しません。

期間索引は日別の代表時刻で、通常一覧の件数上限に影響されません。UIでは全件キャッシュの確定行をローカル日付別にまとめ、各日の最大時刻を使います。beatorajaでは「未確定／診断」期間の履歴行は空で、通常の更新履歴を未確定扱いにしません。この期間の扱いは、取得失敗の診断表示を抑止する意味ではありません。

履歴行の読取りと、全履歴からの期間索引・過去日付ツリーの生成は採否を分けます。索引・ツリーだけが失敗しても、正常な選択期間の行・集計は表示します。過去日付の項目を外して利用不可にし、索引の取得失敗を示します。旧日付ツリーの保存・取得元照合は行わず、固定期間の選択は続けられます。

### キャッシュと表示用変換

起動、意味のある成績取得元設定の変更、LR2履歴の導入・修復・削除後に、通常成績と全履歴・player時系列を一つの取得操作で読みます。`ScoreTableLoadResult.PlayHistorySnapshot` から `BMSLibrary.ScoreSnapshot.PlayHistorySnapshot` の一参照へ採用し、既存の `SourceGeneration` を取得世代として使います。複数DBをまたぐ原子的なスナップショットは保証しません。

期間の初回表示・再選択・変更、検索、整列、カード、表示対象、言語、ファイル差分、所属変更と同じライブラリの全再初期化は、通常成績と取得済みrawを保持します。全再初期化本体で通常成績と履歴の外部DBを再取得しませんが、設定で有効な既存IR・順位後続の成績更新は維持します。これらの表示用 `Version` だけの更新では同じ履歴参照と `SourceGeneration` を引き継ぎます。同値のscore設定確定では保存・再取得しません。現在のLR2プロファイルと一致する検査結果は設定の状態表示へ共有しますが、設定画面を開くためにDB照会しません。

正常0件、利用不可、読取り失敗を区別し、失敗結果も今回の取得組に保持します。期間操作では再読取りせず、次の許可取得後にだけ新結果へ更新します。beatorajaのplayer時系列だけが利用不可なら、正常なscorelog行は維持します。取得時点のraw行・player時系列・ノーツ・スキーマ診断を後の成績値や外部DBから再計算しません。

`PlayHistoryReadCache` は期間・確定状態・上限・日索引の純投影であり、DB接続、取得元選択、rawの所有、読取り待機や取得世代を持ちません。同じ取得組の期間変更では最新一件の日索引結果だけを再利用し、全rawから日索引を毎回作り直しません。期間投影は同じraw record参照を返し、全範囲・全確定状態・上限なしではrawリストをそのまま再利用します。取消しは表示投影を止めるだけで、共有する取得組を破棄しません。

`PlayHistoryProjectionIndex` が生の記録を現在の所持譜面とプレイリストへ解決します。候補索引から表示用ChartFileを取得せず、MD5/SHA-256の候補解決と現在値を共通カタログ読取り区間で一括捕捉します。履歴自体の値と取得元別のスコアは独立した記録として維持します。古い要求・取消で構築が中断したら表示を更新しません。譜面を解決できない行は生のハッシュと診断を持つ未解決行として残せます。これはスキーマ不備や取得失敗を成功へ変える意味ではありません。

読取り・表示生成が失敗した場合は、現在要求の結果領域を取得失敗へ更新します。旧行・選択・集計カードを外し、行・カードに依存する操作だけを止めます。失敗表示に空の表示コレクションを使っても、正常0件の取得成功とは区別します。旧表示の保存・条件照合・復元は行わず、期間・表示対象の変更や他画面は利用できます。取消し・旧世代の拒否と取得失敗を区別し、古い成功・失敗結果は新しい要求へ適用しません。日付別の旧結果保存や自動再試行は行いません。

### 行の意味

`HistoryId` はLR2の `history_id`、beatorajaの `scorelog.rowid` です。時刻はUnix秒を保持してローカル時刻で表示します。LR2の元ハッシュはMD5、beatorajaはSHA-256です。解決できた場合だけタイトル・作者・パスと `ResolvedChart` を持ちます。

ベストのCLEAR、DJ、RATE、SCORE、BP、COMBOは更新前後を示し、初回BPは値だけを示します。種類は複数の更新を併記でき、他の更新がなければ `play` です。LR2の実プレイ結果は確定したプレイヤー集計差分から求めます。beatorajaには単曲の実績・オプション・演奏時間を作らず空欄にします。ベストDJ/RATEは履歴の旧新スコアと同じ取得操作で固定したノーツ数から求め、現在のベスト値を履歴へ混ぜません。

LR2のクリア2はEASYビットありをEASY、なしをASSISTとし、ASSISTビットだけで決めません。クリア2・EASYビットなし・BP負値の行は詳細未保存としてクリア更新だけを表示し、EXSCORE/BP/COMBO/OPTIONを推測しません。オプション履歴は増えたビットと消えたビットの両方を示します。beatorajaの旧新BP `int.MaxValue` は未記録として、保存成績と同じ共通変換でNULLへ正規化します。旧新とも未記録ならBP更新とせず、クリアなど他の更新は維持します。

### 期間と集計

固定期間はすべて、今日、昨日、今日を含む最近7日・30日、未確定／診断です。年→月→日の過去日付ツリーはローカル日付でまとめ、降順に並べます。非同期の古い要求は新しい期間選択を上書きしません。

時刻帯 `z` のローカル日付 `D` の開始境界 `Bz(D)` は、0時が有効ならそのUTC、重複するなら早いUTC、欠落するなら指定日以降の最初の有効なローカル境界の早いUTCです。日区間は `[Bz(D), Bz(D+1))` とし、隣接日は同じ境界を共有します。丸日欠落では両端が同値の空区間になります。今日・昨日・最近7日／30日と年・月・日の期間は、同じ境界変換を使います。

対象はアプリが使う `TimeZoneInfo.Local` とOS提供時刻帯です。変換では基準オフセット、各調整規則の基準差分を加えた値、さらに夏時間差分を加えた値を短命な候補集合にし、0時から候補を引いたUTCの逆変換が0時に一致する最小UTCを選びます。写像がなければ最大・最小候補オフセットが挟むUTC区間で、ローカル時刻が0時以上になる最初の境界をticksの二分探索で求めます。この区間は単独で連続する移行を前提とし、任意の人工時刻帯全域やOSデータ以上の歴史補正へ保証を広げません。時刻帯データは保存しません。

一覧の日時はGregorianの `yyyy-MM-dd HH:mm:ss`、率は小数2桁で小数点を `.` として表示します。集計カードの件数は桁区切りなしの整数です。これらは表示書式の規則であり、演奏時間などの集計範囲は以下の取得元別の規則に従います。書式変更で元の成績値やカード検索集合は変えません。`PlayHistoryReadModelTests` は異文化の件数・率と既存カード条件を、`GridKeywordSearchQueryTests` はコピーした表示日時の検索を分担します。

一覧とは別の集計帯に判定数、プレイ数、演奏時間、スコア/BP/COMBO/CLEAR更新数とクリア更新内訳を表示します。beatorajaだけEXH内訳を加えます。更新カードの複数選択はOR、キーワードとはANDで絞ります。カード選択を検索文字列や履歴へ書き込みません。

FC内訳と既存FCカードは、FC・PERFECT・beatorajaのMAXへのクリア更新を同じ対象集合で数え、絞り込みます。MAXのままSCOREだけを更新した行はSCORE更新へ含め、CLEAR更新やFC内訳へ加えません。MAX専用カードは追加せず、`newclear:PF` と `newclear:MAX` の個別検索、元のランプ値と取得元別の変換を維持します。

演奏時間・判定数の集計範囲は取得元で異なります。LR2は検索・カード・表示対象の適用後に残った確定済み集計対象行の `PlaytimeSeconds` と `JudgeTotal` を合計するため、絞込みに追従します。例えば60秒と120秒の2行から60秒の行だけを残すと、演奏時間は3:00から1:00になります。判定数は履歴の判定実績であり、現在の譜面のNOTESを代用しません。

beatorajaの判定数・プレイ数・演奏時間は選択期間全体の実績で、検索・カード・表示対象による行数変化に追従しません。`score.db.player` の累計から、終了境界前の最新値－開始境界前の最新値で求めます。すべてでは最新累計を使います。単曲の実績を補ってLR2の集計範囲へ揃えず、未確定診断・未対応・読取不能は `-` で、別の値へ代用しません。

`score.db.player` だけが読めない場合は、この3実績を利用不可にし、正常に読めた更新履歴と更新数の表示は維持します。履歴全体の読取り失敗へ広げず、履歴行数から実績を作りません。期間実績も同じ取得時点のキャッシュを使い、期間変更だけでは再照会しません。既存のスコア再読込みなどでキャッシュを破棄した次の取得で、実績の取得もやり直します。

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
| `date:` | ローカル日付。`yyyy-MM-dd`、`yyyy/M/d`、`yyyy/MM/dd`、`yyyyMMdd` は表示上の一日全体、`yyyy-MM-dd HH:mm:ss` と既存の `yyyy/MM/dd HH:mm:ss` はローカル時刻の一秒、`date:"yyyy/MM/dd HH:mm:ss..yyyy/MM/dd HH:mm:ss"` は両端を含む範囲に一致する。範囲の両端でも新しいハイフン日時を受け付け、選択から生成するClauseは従来のslash表記を保つ。 |
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

表単体・プリセット・FOLDER専用プリセットでは、ハッシュは大小文字を同一視し、同じ表・同じハッシュのフォルダ名はOrdinalで区別します。`A` と `a` の所属はそれぞれ一回表示し、履歴の `RawHash` と `Md5` が同じ所属へ一致しても重複表示しません。MD5を持つ表entryはMD5を優先し、MD5がないentryだけSHA-256で照合します。表示対象の投影で元のentryや履歴行のFOLDERを書き換えません。

プリセットJSONは名前と `PlaylistId` への参照だけを保存します。存在しないIDは一致せず、次の編集保存で除かれます。表の正本・譜面・更新日時は変更しません。選択は項目オブジェクトではなく表示対象の識別値を保存し、空と `all` はすべてです。まだ候補がなければ画面だけすべてにし、希望する識別値を保って候補出現時に復元します。一時的な未選択通知で保存した識別値を消しません。

名前の入力中は文字列を保持し、確定時に一件の名前だけを `Trim` して対象参照とともに保存します。保存成功後、同じ確定値を設定画面の編集用プリセットへ反映します。前後空白を含む名前で保存しても、再表示せずに行う対象変更・改名・削除は同じ一件に作用します。名前の比較は従来どおり大小文字を無視します。取消、空名・重複名・対象未選択による拒否、保存失敗では確定済みの内容を変えず、他のプリセットや設定の未確定候補を保存・破棄しません。

表の変更通知中に読込みロックを同期取得し直しません。候補再構築はUIへ遅延し、通知が重なれば最新の版まで再構築します。必要な譜面の遅延読込みは使えますが、表全体の再読込み・外部同期・出力を起動しません。

プリセット編集は名前・検索・仮想化された有界の一覧を持ちます。検索は表示名全体への現在文化圏の大小文字無視の部分一致で、空白だけなら全件です。非表示になった選択も保存対象にし、検索文字列は編集セッション外へ持ち越しません。OK/取消はスクロール・サイズ変更によらず到達できます。

### 外部操作と診断

外部操作は右クリックした一行を対象とし、クリック時に現在のハッシュ・パス・設定を再解決します。未解決でもハッシュだけのWeb操作は使えますが、パスが必要な起動をしません。詳細は[外部起動](../ui/external-launch.md)に従います。解決済み行の明示ドロップだけがプレイリストを編集し、未解決行を含む選択は全体を拒否します。

履歴のhashコピー専用メニューは設けず、ヘッダーの表示列メニューから `MD5 HASH`・`SHA256`・`RAW HASH` を表示し、通常のセルコピー・選択行TSVを使います。MD5列は取得済みの `PlayHistoryRow.Md5`、SHA256列は `Sha256` を直接使います。未解決beatorajaはMD5を空欄、SHA256を取得値とし、未解決LR2はMD5に元のMD5を表示します。解決済み行も各本来値を使い、SHA256をMD5へ代用しません。追加hash計算・DB列・再読込みはありません。MD5は既存 `Hash` 列設定で幅・位置・表示状態を保存・復元し、初期化は他の履歴列と同じ経路を使います。

メニュー入力の捕捉は履歴行なら成功し、項目の表示は設定Web操作、各ローカル能力、複数選択の日付snapshotで判断します。コピー項目の有無で未解決行のWeb操作を拒否しません。表示メニューの先頭・末尾・連続に不要なseparatorを残しません。

診断は取得元、段階、重大度、コード、説明、パスを持ち、集約テキストとログへ出します。`play_history_lr2_schema_*` と `play_history_projection_*` を区別し、未導入や失敗をスコア・回数から代用しません。最終プレイ順の固定SQL出力は[LR2出力](lr2-custom-folders.md)に従います。

DB読取り、LR2スキーマ検査、期間実績の取得で例外を結果へ変換する場所では、元例外と取得元・段階・対象パスを既存ログへ渡します。ランプビューアも同じ読取り境界の診断を使い、結果へ元例外を保存・再送出しません。通常のログ出力失敗は元の読取り結果を変えません。

履歴行の終端適用後は、確定した行の所有権を呼出元へ戻しません。必要な行・データ源退役の公開が失敗しても、旧行の回収と独立した後続公開を続け、確定済み行を失敗時の取消処理で破棄しません。データ源退役後の保持状況ログは任意診断であり、ログの失敗を適用失敗へ加えず、残りの診断も試みます。データ源退役そのものや行の公開の失敗は任意診断へ格下げしません。

## 実装とテストの対応

`PlayHistoryReadModelTests.BuildReadView_Lr2OwnsReadPeriodProjectionPresentationAndCacheLifecycle` は実モデルの一括取得で3履歴を捕捉し、既定の時刻降順・同時刻ID降順を独立期待で確認する。DB削除後の暖まった期間検索で再読取りしないこと、新時刻の1履歴を追加した後の許可取得、昇順の異時刻関係を確認する。同時刻の昇順副キーは固定せず、取得組更新・取消拒否の保証を維持する。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 通常成績と履歴の一括取得、表示版と取得世代、全再初期化の保持 | `BmsLibraryInitializationService.LoadScoreTable`、`BMSLibrary.ScoreSnapshot` | `BmsLibrarySavedScoreAvailabilityTests.InitializeStartup_AcquiresHistoryBeforeViewAndFullReinitializeKeepsCapturedBatch` と成否独立性、`ScoreSnapshotPublicationTests` の既存版・購読・非複製検証。取得済みrawの期間・上限・確定状態・日索引とノーツ固定は `PlayHistoryReadModelTests`、DB変更・移動後もランプが捕捉組を使うことは `BmsLibraryPlaylistLampDataSourceTests` で分担する。 |
| 取得元・期間・確定状態・未解決行・累計差分 | [`Lr2PlayHistoryReader`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/Lr2PlayHistoryReader.cs)、[`BeatorajaPlayHistoryReader`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/BeatorajaPlayHistoryReader.cs) | [`PlayHistoryReadModelTests`](../../../BeMusicSeeker.Tests/PlayHistory/PlayHistoryReadModelTests.cs)、[`BmsLibrarySavedScoreAvailabilityTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibrarySavedScoreAvailabilityTests.cs) |
| beatoraja旧新BPの未記録値と更新種別 | `BeatorajaPlayHistoryReader`、[`ScoreValueCalculator.NormalizeMinBp`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Score/ScoreValueCalculator.cs)、`PlayHistoryRow` | `PlayHistoryReadModelTests.BeatorajaReader_TreatsMaxValueOldMinBpAsUnplayed` で実scorelogの旧特殊値→実績10と旧新特殊値のクリア更新を読み、reader・表示行のnull、初回BP表示、BP更新なしとクリア更新の保持を確認する。 |
| 日付境界の通常・欠落・重複・丸日欠落、相対期間と過去日付ツリー | [`LocalDateBoundary`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/LocalDateBoundary.cs)、[`PlayHistoryPeriodRequest`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryPeriodRequest.cs)、[`PlayHistoryPeriodTreeItem`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryPeriodTreeItem.cs) | `PlayHistoryReadModelTests.PlayHistoryPeriodRequest_DayBoundaryTransitionsShareAdjacentUtcEndpoints` は `CreateDay` から固定UTC期待値と隣接境界を確認する。`PlayHistoryPeriodRequest_RelativePeriodsUseMissingMidnightBoundary` は今日・昨日・最近7日／30日、`PlayHistoryPeriodTreeItem_TransitionDatesBuildOneDayWithUsableRequests` は実UTC行から年・月・日requestと同日重複の統合を確認する。既存のUTC・UTC+09・年月・表示名・確定条件とreader/cacheの半開範囲検査を維持する。 |
| FC内訳・カードのMAX更新、SCORE単独更新の除外、PF/MAX個別検索 | [`PlayHistoryPeriodSummary`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryPeriodSummary.cs)、[`PlayHistoryPresentationState`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryPresentationState.cs) | `PlayHistoryReadModelTests.BeatorajaSummaryIncludesMaxClearUpdatesInFullComboCard`は実scorelogから投影・集計・カード検索を接続し、9→10・8→10と既存FC/PFの4行、SCORE単独更新とEXHの除外、元値と個別検索を確認する。既存カード指標・選択条件の対応とLR2変換は同テスト群、カードOR・キーワードANDは`GridKeywordSearchQueryTests`、実カード表示とコマンド接続は`MainWindowPlayHistoryWpfTests`が分担する。 |
| 取得失敗の表示・正常空との区別・次の成功へ復帰 | [`PlayHistoryWorkflowOwner.ViewExecution`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryWorkflowOwner.ViewExecution.cs)、[`PlayHistoryPresentationState`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryPresentationState.cs) | [`PlayHistoryReadModelTests`](../../../BeMusicSeeker.Tests/PlayHistory/PlayHistoryReadModelTests.cs)で実DBの照会失敗から一覧・カード・選択の退役と失敗表示、次の正常0件の採用まで確認する。投影DB故障、条件が一周した古い失敗の拒否、通常画面移動後の非上書きも同テスト群で分担する。 |
| 索引だけの失敗・正常行と集計の採用 | [`PlayHistoryReadCache`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryReadCache.cs)、`PlayHistoryWorkflowOwner.BuildReadPresentation` | `PlayHistoryReadModelTests`で期間外の不正日時と正常な現在期間を使い、更新後の行・非ゼロ集計・過去日項目の除去・固定期間の継続を確認する。player表だけの障害は同テスト群で正常一覧と3実績の利用不可、次の許可取得後の復帰を確認する。 |
| 読取り失敗の一次診断 | 両reader、[`Lr2PlayHistorySchemaService.Check`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/Lr2PlayHistorySchemaService.cs)、[`PlayHistoryReadDiagnostics`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/PlayHistoryReadDiagnostics.cs) | 上記reader・スキーマの実DB失敗テストを回帰実行し、元例外・取得元・段階・パスの局所ログ接続は静的に確認する。例外の表示用輸送やログ故障の組合せを保証に加えない。 |
| 検索構文・日時範囲・表示フィルター | [`PlayHistoryReadCache`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryReadCache.cs) | [`GridKeywordSearchQueryTests`](../../../BeMusicSeeker.Tests/Search/GridKeywordSearchQueryTests.cs)、[`ChartListFilterViewModelTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListFilterViewModelTests.cs) |
| 表単体・両プリセットの所属大小文字・重複一致・元データ不変 | [`PlayHistoryDisplayTargetIndex`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryDisplayTarget.cs) | [`PlayHistoryReadModelTests`](../../../BeMusicSeeker.Tests/PlayHistory/PlayHistoryReadModelTests.cs)の `PlaylistTargetUsesPlaylistFolder` はMD5優先・SHA256単独の両条件でA/a各一回を確認する。`TargetSetFiltersRowsAndUsesOrgSymbolLevel` と `TargetSetProjectionOnlyKeepsRowsAndClearsUnmatchedFolder` はSATA/SATa各一回と非対象行の除外／保持・空欄化、全条件でentry捕捉値と元行FOLDERの不変を確認する。ハッシュの小文字正規化と、所属重複排除のOrdinal比較は静的レビューで分担する。ID限定・全件fastpath・旧要求拒否・MD5優先は同テスト群の既存ケースを維持する。 |
| 実画面の期間・明示メニュー操作・対象変更 | [`PlayHistoryWorkflowOwner`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryWorkflowOwner.cs) | [`MainWindowPlayHistoryWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlayHistoryWpfTests.cs)、[`MainWindowContextMenuResourceTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowContextMenuResourceTests.cs)で実WPFの期間・明示メニュー操作・対象変更を自動検証する。実入力とフォーカスは[ユーザーに推奨する実機確認](../development/testing.md#ユーザーに推奨する実機確認)へ分ける。 |
| 履歴のhash列と通常コピー・メニューの能力分離 | `CustomTableColumnFactory`、`PlayHistoryContextMenuState`、`PlayHistoryWorkflowOwner`、`MainWindow` | `CustomTableColumnFactoryTests.PlayHistoryHashColumns_UseProjectedHashKindsWithoutFallback` はLR2/beatorajaの実投影で所持譜面解決・未解決・所持譜面未解決のSHA単独metadata取得について、各本来値・RAW保持・表示/セル/TSVとMD5整列を確認する。SHA単独metadata条件ではMD5空/SHA正値を維持する。metadata条件は正しいSHA・空MD5で取得し、`ResolvedChart` なしとmetadata取得済みを分ける。`CustomTableNavigationTests.PlayHistoryCopyCurrentCell_UsesLatestSelectedRowAndReplacementSource` は実Ctrl+Cから現在行・列とsource交換後の実factory値を観測し、専用例外でClipboard前に止める。`CustomTableColumnSettingsTests.PlayHistoryHashLayouts_SaveAndReloadWithoutChangingExistingChoices` と既存補完caseは実provider読戻しと既存幅・順序・可視性を確認する。`MainWindowPlayHistoryWpfTests.PlayHistoryColumnMenu_TogglesMd5ColumnThroughCompiledBinding` は実列メニューの表示切替を確認する。`MainWindowContextMenuResourceTests.PlayHistoryContextMenuOwnerCreatesAssociatedTargetForResolvedRow` は未解決両方式・索引対象外のpath/MD5欠損行の元hash保持とローカル3操作拒否、正常BMSのscoreviewer hash/path/titleと空title受理を確認する。scoreviewerの欠損path/MD5条件自体は静的点検へ分担する。同WPFの `PlayHistoryContextMenu_DoesNotInferPrimaryActionsFromSecondaryRows` は空hashの主行state/input受理・ローカル能力なしと、副行にローカル能力があっても実メニューの関連付け/Explorer/viewer非表示・日付表示・主行文脈保持を確認する。同WPFの`PlayHistoryBmsonRowOffersConfiguredMd5WebAction`・`PlayHistoryUnresolvedBeatorajaRowOffersOnlyWebActionsForBothChartKinds`・`PlayHistoryResolvedRowOffersAssociatedOpenForExistingFile`は実クリックのhash/actionID/対象とcopy項目なし・separator合法を確認する。日付/行文脈・設定初期化・資源辞書整合は既存系列を維持する。 |
| 終端適用・データ源退役診断・行所有権 | [`PlayHistoryWorkflowOwner.TerminalShell`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryWorkflowOwner.TerminalShell.cs)、[`PlaylistWorkspaceViewModel.DetailTerminal`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailTerminal.cs) | [`ChartListVirtualViewTests.PlayHistoryWorkflowOwner_ApplySortedRowsKeepsCommittedRowsAfterPublishFailure`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs)で実履歴行、旧行の一回の回収、データ源解除、後続公開とログ故障時の成功を確認する。必須公開の失敗・取消・旧世代の拒否は同テスト群の既存終端ケースで確認する。 |
| プリセットの確定名・継続編集・削除と候補分離 | [`SettingsDialogViewModel.TryApplyPlayHistoryFolderDisplayPresetEditSession`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs)、`RemoveSelectedPlayHistoryFolderDisplayPreset` | [`SettingsDialogBehaviorTests.PlayHistoryPreset_ConfirmedNameKeepsSavedRecordAcrossEditsAndRemoval`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) は ` 練習 ` の確定から対象変更・` 集中 ` への改名・削除を再表示せずに行い、新しいproviderの生JSONと同じ編集用プリセットの名前・対象・件数を確認する。別recordの取消候補とBMT候補の非混入・保持も同ケースが確認する。既存の `SettingDialogCustomFolderOutputBaseTests.PlayHistoryFolderDisplayPresetEditor_DraftsSaveValidationAndIdMatching` は空名・重複名・対象未選択の拒否、ID照合と実履歴への両表示モードの配送を分担する。 |
| プリセットの検索・選択保持・有界表示 | [`PlayHistoryFolderDisplayPresetEditor`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryFolderDisplayPresetEditor.cs) | [`DialogPresentationTests`](../../../BeMusicSeeker.Tests/Dialogs/DialogPresentationTests.cs)、[`SettingDialogCustomFolderOutputBaseTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogCustomFolderOutputBaseTests.cs)。同一識別値で対象参照が変わる場合の再要求は `PlayHistoryDisplayTargetSelection_RequeuesWhenSameIdentityTargetSetChanges` が確認する。 |

## 関連資料

[LR2履歴スキーマ](lr2-history-schema.md)、[ランプビューア](lamp-viewer.md)、[検索支援](../ui/keyword-search.md)を参照します。
