# LR2の楽曲DB生成と同期

## 目的と適用範囲

LR2連携モードで、現在のBMS譜面・検索ルート・プレイリスト出力から `song.db` の `song` と `folder` を生成する契約を定めます。LR2自身の起動時自動更新には依存しません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

操作の受付分類・競合結果・必要継続の寿命は[競合ポリシー](../core/operation-concurrency-policy.md)を正本とします。以下は入力・処理・資源所有と結果の固有契約です。

### 管理範囲と設定

`song` と `folder` の生成列は再生成可能なデータです。ただし `song` の行の追加・削除はファイル差分検出が担当し、全体同期はその所属を変更しません。利用者が編集する `favorite`、`tag`、`adddate` は明示的な保持対象です。

BMSONはLR2へ出力せず、本アプリの `bmson_song` 等で管理します。BMSONにLR2互換性警告を出しません。スタンドアロンモードでは、LR2用のフォルダ行、`.lr2folder` 探索、`folderinfo.txt`、組込みカスタムフォルダの階層を生成しません。

LR2連携を初めて選択するとき、設定保存時、起動時の設定読込みで、`config.xml` の `<autoreload>` を `0`（手動更新のみ）にします。BMS検索ルートは本アプリの共通画面から管理します。追加操作そのものでは再帰走査せず、通常の差分更新に含めます。入力は存在するディレクトリかつShift_JISで表せるパスとし、相対パスや同一・親子関係の重複を正規化・検証します。

### 受付と実終端

全体同期の開始・競合・権限継続は[競合ポリシー](../core/operation-concurrency-policy.md#設定全体操作終了)を正本とします。Runningは進捗表示であり物理処理の成功を表しません。

受理済みの設定・導入・差分の必須部分反映は同じ権限で実終端までawaitし、自己受付を取り直しません。推定入力を実変更する受理済み背景更新は先行操作の終端後に共通受付を取得して実更新します。起動全体同期と差分後のNeeded全体同期は開始競合なら見送り、確定したchart・通常フォルダ差分を保持して参照を更新します。未実施全体をCompletedとせず、既存Needed/Incomplete等と実結果で区別します。共通受付取得後の実実処理は手動要求も直接作業Taskで開始して実終端をawaitし、内側で起動スケジューラーの枠を待ちません。受理済み起動要求の外側の登録・依存・進捗は維持します。登録だけを完了にせず、失敗・終了取消でも開始済み処理と後片付けを待機してから解放します。同期失敗は状態保存後も元の例外を返し、先行の確定を巻き戻しません。DB接続の短期ロック、SQLite busy、署名・永続状態・進捗・Incompleteは維持します。

### 同期への入力

同一の受理済みLR2操作では小さい変更不能な設定入力・custom-フォルダ設定スナップショットを共通受付取得後・非同期準備Taskの開始前に一回捕捉し、署名・生成準備・プレイリスト・実処理入力へ明示的に渡して一致させます。配置は保存・適用済みの小スナップショットを使い、未保存Values編集で署名・currentness・出力先を変えません。非配置の生成フラグは既存Values共有編集を維持し、次の明示要求で新しく捕捉します。LR2独立の最新性・世代検査は維持します。

現在のBMSパス、検索ルート、必要な祖先ディレクトリと更新時刻、`folderinfo.txt`、探索済みの管理外 `.lr2folder`、アプリ出力、LR2組込みフォルダ、設定値、解析結果・互換性警告・リソース調査結果を受け取ります。不完全な入力を、同期内の広範な再探索で補って成功扱いにしません。

LR2に見せるjukeboxルートと、本アプリが譜面・リソースを走査するルートは用途が異なります。通常出力先、追加出力先、ルート出力先の扱いは[設定](../runtime/settings.md#カスタムフォルダと検索対象)を参照します。

### songの生成

ファイル差分検出は、信頼できる走査範囲の現在パスをDBの完全一致キーで照合します。行の収束、大小文字だけ異なるパス、一対一・同一MD5での保存値引継ぎは[パスの同一性](../core/path-identity.md)に従います。全体同期は存在する行の生成列だけを更新し、不要行の削除、管理用補助行やダイジェストの整理を担当しません。

`date` は譜面ファイルの更新時刻をUnix秒で保存します。新しい行の `adddate` が未設定なら、同期時点のLR2形式時刻で初期化します。`level`、`judge`、`date`、`folder`、`parent` 等は現在の入力から生成します。日時の0や負値も有効なUnix秒として扱います。

`folder` と `parent` はLR2互換のCRC32です。対象ディレクトリまたはその親のパスに末尾の `\` とNULを付け、Shift_JISのバイト列から計算します。パスをShift_JISで表せない、またはWindowsパスとして解釈できない場合も管理対象のBMS行は削除せず、両列を `NULL` にして互換性警告を付けます。計算は `LR2CRC32` と `Lr2SongFolderParentNormalizer` を正本とします。

新規追加と `UpsertSongs` では、この正規化を保存前に行います。相対パスの補正は、絶対パス化とCRC計算の両方が成功した場合だけ `song.path` に反映します。計算できないパスだけを中途半端に更新したり、警告対象を次の走査で削除・再追加したりしません。

譜面本体を読み取れない場合は、DB境界で同じ完全一致パスとMD5の既存行を使い、未取得の生成列を保持します。現在のテキストグループと取得済み詳細の限定列だけを反映し、保存行を共通現在値や同期要求へ逆参照させません。

### folderの全体生成

`folder.path` が行のキーです。通常ディレクトリは `type=1` とし、親のCRC32を `parent` に設定します。LR2ルート直下として扱う行は `ROOT` とNULのCRC32を親にします。自身のCRC32を保存する専用列はありません。`date` はディレクトリまたは `.lr2folder` の更新時刻であり、ルート行も実際の時刻と一致させます。

期待する集合は検索ルート、BMSのあるディレクトリとルートまでの祖先、`folderinfo.txt` のある場所、テキストグループ、`.lr2folder` に必要な親階層から作ります。同一パスの優先度は、組込みカスタムフォルダ、`.lr2folder`、`folderinfo.txt` のあるディレクトリ、通常ディレクトリの順です。同順位で同内容なら順序に依存せず一つにまとめ、異なる内容なら衝突です。

正常に読み取れたBMSや `.lr2folder` でも、パスをCP932で表現できない場合は該当する通常folder行・`.lr2folder` 行・親行だけを投影から省き、他の行の同期を続けます。BMSのsong行は保持し、`folder` と `parent` を `NULL` にしてLR2互換性警告を残します。folder投影の既存事前検査で探索未完了、`.lr2folder` または `folderinfo.txt` の読込み失敗、必要なディレクトリメタデータ不足を検出した場合は、従来どおり全体同期を失敗させます。この規則はBMS譜面本体の読取り失敗へ広げません。実装は `Lr2FolderTableReconciliationService.BuildProjection`、回帰テストは [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) の `QueueLr2SongDbSync_CompletesWhenChartDirectoryPathIsUnsupported`、`QueueLr2SongDbSync_SkipsUnsupportedLr2FolderFileNameAndContinues`、`QueueLr2SongDbSync_SkipsUnsupportedLr2FolderParentAndContinues` です。

全入力の探索・取得・解析と必要な親行の構成が成功してから、一つのトランザクションでフォルダ表全体を置き換えます。不完全な入力や同順位の衝突では表を変更せず、`Incomplete` または `Failed` を返します。通常のプレイリスト・設定・譜面変更は、明示された局所範囲だけを同期します。

### 楽曲チャンクの確定と必須反映

楽曲行、`chart_info`、解析失敗と互換性事実、処理位置は同じ既存チャンクのトランザクションで一回だけ確定します。確定前の失敗はそのチャンクを巻き戻し、先行する確定済みチャンクと利用者列・処理位置を保持します。実確定後の索引・投影失敗でDBを巻き戻したり、処理位置を未確定の位置へ戻したりしません。

`ChartInfoRowsCommitted`、`ChartInfoParseFailuresCommitted`、`Lr2CompatibilityFactsCommitted` は確定済み事実の必須引渡しです。一つが失敗しても同じチャンクの他の必要な引渡しを各一度試み、失敗原因とスタックを維持して同期を失敗させます。必須引渡し内部の取消例外を、同期所有の終了取消へ読み替えません。進捗、性能記録、背景報告と画面通知は任意であり、その失敗で確定結果や失敗保存・実要求終端を飛ばしません。

楽曲行pipelineは先着した実故障を保持します。先着原因が所有取消だった場合だけ、後続の必須引渡しなどの実故障を優先し、通常の終了取消に隠しません。取消例外の所有資格は原因の記録時点で、例外のtokenが要求tokenまたはそのlinked pipeline tokenと一致し、要求tokenが取消済み、または所有者の実終了flagが成立している場合に限って確定します。例外型とtoken一致を先に確認し、該当しない実故障で終了flagの取得を行いません。後から要求が取消されても、無関係な取消例外や先着した実故障を再分類・置換しません。選択した原因にかかわらず全待機を直ちに停止し、開始済みreader・workerと完了continuationの実終端を待ってから状態保存と原因の返却を行います。非所有取消例外は全join・未解決巻戻しの優先判定・`Failed` 保存を試みた後にだけ、元messageと内部原因のtoken・stackを保った実失敗として返します。要求coordinatorの通常取消分岐へ渡さず、後発shutdownでも `Failed` を維持します。記録済みの所有取消は既存の要求token checkpointを通します。終了flagがtoken取消に先行してcheckpointが戻った場合も、全join後に実確定cursor・全体対象件数・`song_rows`・段階件数0/0を既存のguard付き進捗入口へ最後に渡してから、元OCEを返し、`Failed` 保存へ落としません。これにより古いworker進捗が確定位置を追い越さず、要求coordinatorの `Incomplete` 保存へ実確定位置を引き渡します。取消済みtokenの経路はcheckpoint内の通知と例外返却を使い、未解決巻戻しの経路はこの通知より先に失敗します。任意通知の故障で元OCEや確定DB結果を変えません。終了flagのない要求token取消は、従来どおり想定外取消を `Failed` として保存して要求token付きOCEを返します。それ以外の実故障の返却契約は変えません。

巻戻しではSQLiteの実トランザクション終端を確認します。管理APIが失敗しても、既存の直接SQLによる巻戻しが成功した場合、またはSQLiteが既に巻戻している場合は、安全な取消完了として元操作の失敗・終了取消の区別を保持します。管理状態だけが解除されて実トランザクションが残る場合も直接巻戻しを試みます。実状態を確立できなければ元処理と両巻戻し原因を診断し、未解決の接続上へ状態保存を重ねず、所有接続の解放を含む実終端まで失敗として返します。終了要求の有無だけで正常な中断へ変換しません。

`Lr2SongDbSyncService.Run` の `FinalStage == CompletedStage` は内部の生成成功だけを示し、永続状態は `Running` と実確定した処理位置、未設定の完了時刻を残します。生成側は全体の完了進捗も出しません。保守writerと所有接続を解放した後、既存要求coordinatorが必須終端投影を一度実行し、所有tokenの取消を確認してから、既存状態保存窓口の `MarkCompleted` で実完了を保存します。その保存成功後にだけ完了進捗と完了要求版を公開します。投影を生成側のcallbackへ移して保守writerと所持集合writerのロック順を逆転させません。

要求の終端で確定互換性事実を投影するときは、試行済みと成功済みを区別し、失敗した投影をcatchから再実行しません。先行する詳細索引や一部互換性投影の成功を保持し、終了取消の後に必須投影が失敗した場合も `Failed` とします。元の実失敗に続く二次投影の失敗は両方を診断して元失敗を返します。実投影の成功後でも所有終了が先着すれば完了を保存せず `Incomplete` とします。失敗状態の保存自体が失敗しても元原因を置換せず、公開の `Running` を解除して実要求を終結します。保存先に `Running` が残る場合も未完了を維持し、未確定の完了で次回同期を省略しません。全体の再試行・巻戻しや新たな永続故障台帳は追加しません。

### カスタムフォルダの階層

通常の `.lr2folder` は `type=2` です。特殊な種別は組込みフォルダだけに付け、`newsong.lr2folder` は3、`course1.lr2folder` から `course3.lr2folder` は6です。それ以外は本文に `#COMMAND` や `#TAG` があっても2です。

管理外ファイルは実際の探索ルート配下の階層を維持します。ルート自身だけを `ROOT` の直下とし、子ディレクトリやファイルを平坦化しません。アプリの通常出力先は出力基点をルートとして扱い、ルート出力先では各プレイリストのディレクトリをルートとして扱います。連番ファイルはそのプレイリストディレクトリを親とし、ルート出力基点の直下に置く単独ファイルだけを `ROOT` の直下にします。

保存時は `0000.lr2folder` からの連番をShift_JISで出力し、同じ生成内容からDB行を同期します。ルート出力の設定変更はLR2設定の検索ルートにも反映します。全体同期では更新時刻が一致しても本文を再解析します。全体同期の事前準備中に行うファイル作成・検証と、フォルダ表の確定を分けます。

組込みフォルダの場所はLR2実行ファイル配下の `LR2files/CustomFolder` です。`<customfolder>` のビットマスクが `RANDOM`、`favorite.lr2folder`、`TOP10.lr2folder`、`PLAYLEVEL`、`CLEAR`、`RANK`、`ignore.lr2folder`、`INSANE01`、`INSANE02` の有効化を制御します。コースは常に扱い、新曲フォルダは `titleflash` と `song.adddate` から生成対象を決めます。DBにはLR2ルートからの相対パスで保存します。

### 同期状態と起動

同期状態は `lr2_song_db_sync_status` の `name=default` 行に保持します。状態、入力署名、実行識別子、処理位置、総数、段階、エラー、更新・完了時刻を記録します。状態には `NotNeeded`、`Needed`、`Running`、`Completed`、`Failed`、`Cancelled`、`Incomplete` があります。

`Completed` は、完全な生成入力、フォルダ表の確定、楽曲の生成列更新、親L/P内で取得した入力の反映、必須詳細索引・互換性投影、状態の保存が成功したことを示します。同じ入力署名で完了済みなら全体同期を省略します。これは `chart_info` の存在・完全性・現行性の証明ではありません。譜面情報の補完は[譜面情報の管理](../library/chart-info.md)に従います。

通常起動ではローカル必須出力の成功後、既存状態・署名で必要な全体同期を同じ親L/Pで直接待ちます。正常・失敗・中断とcleanupの実終端後に親受付を解放します。ローカル失敗では同期へ進まず、LR2だけの通常失敗ではローカル確定を維持し独立結果・永続記録・通知へ残します。親L/P解放後の必須UIと実host接続が成功してから通常・設定共通の終端で解禁し、同期失敗をUI成功の証明にもUI失敗の取消にも読み替えません。同期の失敗を上位のローカル準備成功へ混ぜず、進捗バーの再試行ボタンは設けません。設定画面の手動同期と次回起動は新しい入力を取得します。

失敗の保存と公開は`BMSLibrary.Lr2SynchronizationOwner`の同じ責務に集め、実行開始前の境界で失敗した場合も元例外を型付き結果へ残します。既に確定した`Failed`/`Incomplete`は上書き・重複通知しません。本番の起動・再初期化はこの結果を消費して既存のLR2状態を表示へ接続します。終了取消は非致命的なLR2失敗結果へ変換せず、上位の`ShutdownRequested`へ伝播し、親受付の終端後も初回完了・操作解禁・外部同期投入には進みません。

未完了状態、失敗後の再試行、手動の強制同期は先頭から実行します。保存した処理位置から再開しません。利用者向けの同期取消操作は設けず、アプリ終了の内部取消だけを区別します。安全に記録できる場合は `Incomplete` と `shutdown_interrupted` を残します。旧DBの `Cancelled` は読込み・表示・再試行判定に対応しますが、新しい通常実行では作りません。終了以外の取消は失敗として表面化します。

同期中も読み取り操作を許可し、DB変更を伴う操作は制限します。全体同期はカスタム定義とディレクトリ情報を読み、通常・カスタムフォルダの完全な投影を検証・一括確定してから、楽曲の生成列、同じ入力による同期状態の確定へ進みます。事前のプレイリスト出力・組込みカスタムフォルダ準備も同じLR2行へ通知します。観測用の実段階・件数・不定表示は[進捗管理](../runtime/progress.md#lr2楽曲db全体同期の段階)に従い、通知先の例外でDB結果を変えません。

楽曲の有限進捗はスキーマ等の準備後に開始し、同じ対象の短いチャンク保存でも役割と件数を維持します。既知0件の件数段階は通知しません。プレイリストの表ごとの再帰整理は独立した不定段階として残し、次表の出力で全体ファイル数の有限段階へ戻します。通知だけを整理し、DB確定・保存カーソル・物理整理の順序は維持します。

### 読込みと確定結果の再利用

リソース互換性は[共通リソース結果](../library/chart-model.md#譜面情報とリソース保守)の全元記述で評価します。推定・保守・同期・移転再評価の入口によらず、検索キーが同じ別原文も個別に扱い、非空のUnknownや空キーも原文の長さを評価します。真の空・空白だけの原文は共通結果に保持してもリソース長の評価対象にはせず、最大相対長を0として記録しません。`.flac` / `.jpeg` を照合用の `.wav` / `.png` として1バイト短く数えません。元記述のトリム、区切り、独立した `.` の整理とCP932の259・260バイト境界を維持します。パスなしCP932診断は文字コード警告を付け、他の参照の長さ評価を妨げません。

楽曲行の処理開始時に、現行パーサー版の譜面情報と、再試行期限を考慮した解析失敗MD5集合を取得します。各ワーカーは既存の `ChartFileSnapshot` とこれらの値を共通評価器へ渡します。ワーカー内で追加の譜面読込みやDB照会を行いません。不足・古い譜面情報も、専用パーサーを新設せず共通の補完処理を使います。

今回のファイル差分がDBへ確定したBMSパスと走査面は、変更不能な`LibraryFileInitializationResult`として後段へ直接渡します。親L/Pが必須処理・cleanupまで生存し、競合する正本変更を受付けないため、共有証票の公開・取得・消費・破棄や因果を推測する全体版照合を行いません。再初期化・差分再読込みも同じ結果引渡しを使います。手動同期は過去の結果を使いません。確定パスのファイル読取り・DB現行性照会を省き、別パスの同一MD5を連動して省略しません。

確定パスも現行の譜面処理対象数・進捗の分母に含め、読取り・解析を省いた結果として1,000件単位（末尾は残件数）で回収します。全件が確定パスでも専用の一括スキップ経路は設けません。

複数の読み手は、順序回収までの先行数を制限する枠を取得してから対象を採番します。採番済みで未回収の項目は必ず枠を確保済みとし、先頭の不足番号が後続結果で埋まった枠を待つ循環を防ぎます。受渡し前の末尾到達・終了取消・中断・例外では読み手が取得済みの枠だけを返し、受渡し後は書き手が番号順に回収した時点で返します。枠は一度だけ返し、過剰返却の例外を無視しません。

読み手・評価ワーカーの内部故障は、番号順回収が故障を発見するまで待たず、最初の原因を保持して操作内の取消を直ちに通知します。先行処理枠と両待ち行列の待機を起こして残りの投入・生成・回収を停止し、全開始済みreader/workerと待ち行列の完了処理を待機してから、元の型・メッセージ・stackで失敗を返します。閉鎖済み待ち行列への後続出力や内部停止の取消例外で、最初の原因を置換しません。通常の譜面読取り不能は既存の生成列保持へ進み、内部解析・資源故障を解析失敗行へ保存しません。

内部停止用の取消は要求tokenを取消にせず、利用者・終了要求の分類へ読み替えません。実要求tokenが取消された場合は既存の要求token付き例外と状態分類を使い、終了以外の取消は `Failed` / `Unexpected operation cancellation.` を維持します。確定済みチャンク・利用者列・保存カーソルを保持し、全体巻戻しや未確定suffixの新たな確定保証を追加しません。詳細解析依存は要求に指定したサービス、または既存の共通サービスを一操作で一度解決して各評価へ渡します。

最終入力は今回の走査面、確定した行集合、同じ操作で生成・検証した完全な準備面から作ります。後続の途中再走査で新旧入力を混ぜません。走査なしの新規要求は現在の入力を取得します。UI要求世代、読取りcache失効、永続LR2状態・互換署名は維持します。

#### 並列生成と順序回収の所有権

図は同期パイプラインが継続する通常の受渡しと、読み手が引渡し前に退出する経路だけを示します。ワーカーや書き手の例外で残りのパイプラインを打ち切り、同期を失敗として扱う経路は本文の失敗規則に従います。矢印は処理順と、先行処理枠・番号付き結果の受渡しです。枠は採番より前に取得し、読取り結果の待ち行列への引渡し成立後は書き手だけが番号順の回収時に返します。評価ワーカーは枠を返さず、枠の返却はDB確定成功の通知でもありません。

```mermaid
sequenceDiagram
    participant Slots as 先行処理枠
    participant Reader as 複数の読み手
    participant Worker as 複数の評価ワーカー
    participant Writer as 単一の書き手
    Reader->>Slots: 枠を取得
    Slots-->>Reader: 取得成功
    Reader->>Reader: 対象を採番・読取り結果を用意
    alt 読取り結果の引渡し成立
        Reader->>Worker: 番号付き読取り結果（待ち行列経由）
        Worker->>Worker: 共通評価器で生成
        Worker->>Writer: 番号付き生成結果（待ち行列経由）
        Writer->>Writer: 番号順に結果を回収
        Writer->>Slots: 回収した枠を一回返す
        Writer->>Writer: 規定の単位でDBへ集約・書込み
    else 引渡し前の末尾到達・取消・中断・例外
        Reader->>Slots: 自分が取得済みの枠だけを一回返す
    end
```

採番済みなのに枠を持たない先頭項目を作りません。ワーカーは追加の譜面読取り・DB照会を行わず、確定済みパスの省略も既存の順序回収へ通します。

### 局所変更と性能

導入・削除・移動・リネーム・統合では、確定結果から追加・削除・旧新BMSパスを捕捉します。追加だけなら現在BMSの再検索は不要です。削除・移動では変更ディレクトリ内と、登録ルートまでの祖先のBMS件数だけを既存索引から取得します。BMSONを残存BMSに数えず、削除範囲をルート全体へ広げません。

値は所持集合のロックと対応する版の下で固定し、確定結果に可変な検索関数を持たせません。自動リネームも共通の変更セッション内で局所値を取得し、LR2同期を一回終えます。取得・同期の失敗は失敗として返し、既に確定したファイル・DB処理を再実行・巻き戻ししません。

ディレクトリ比較とDBの完全一致キーを区別します。通常の局所変更で全BMSの列挙・整列・全祖先索引の再構築を行いません。全体同期では一回の完全なフォルダ生成と確定を行い、楽曲行の所属は変更しません。探索が不完全なら広範囲の不要行削除を行いません。

低水準の文の解放では、`sqlite3_finalize` が直前の実行失敗を返した場合も文自体は破棄済みとして扱います。既に実行失敗がある場合は二次診断をログへ置き、`Exception.Data` の履歴へ追加しません。解放APIの呼出し自体が例外で終わり解放を確認できない場合は、未解決の後片付けとして元失敗とともに返します。実行失敗がない場合の解放エラーは、そのまま操作失敗です。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 両受付の開始競合、準備・書込み0と部分取得解放 | `Lr2SongDbSyncRequestCoordinator` | [`QueueLr2SongDbSync_CompetingAdmissionSkipsBeforePreparationAndReleasesPartialLease`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs): 実到達・実結果・Task終端を確認し、保持点はfinallyで解放して全開始Taskを待機する。 |
| 差分の実chart/通常フォルダ確定と全体同期競合skipの区別 | `FileDiffReloadWorkflowOwner` | [`DailyLibraryDiffAndIndependentPlaylistRegistration_CompleteActualChangesConcurrently`](../../../BeMusicSeeker.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs): 実到達・実結果・Task終端を確認し、保持点はfinallyで解放して全開始Taskを待機する。 |
| 全原文のLR2評価、照合別名との分離、CP932境界 | [`Lr2CompatibilityEvaluator`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2CompatibilityEvaluator.cs) | [`Lr2CompatibilityGoldenTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2CompatibilityGoldenTests.cs) は原文の `.flac` / `.jpeg` の259・260バイト、同キーの全原文、非空の空キー・種類不明、長いdirectoryで空定義だけの最大相対長がnullとなる条件、パスなし診断と別参照の長さを確認する。同期への接続と既存健全性の保持は `QueueLr2SongDbSync_ProjectsLr2CompatibilityWarningsToLiveRows`。 |
| 共通受付の権限・寿命とBusy | [`ChartFileOperationSynchronizer`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartFileOperationSynchronizer.cs)、[`Lr2SongDbSyncRequestCoordinator`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncRequestCoordinator.cs) | [`ChartFileOperationSynchronizerTests`](../../../BeMusicSeeker.Tests/ChartOperations/ChartFileOperationSynchronizerTests.cs) は別管理主体・解放済み・二重解放と借用を確認。`BmsLibraryLr2SongDbSyncTests` は準備から実処理終端のBusy、成功・失敗・終了取消を確認。既存準備境界の明示barrierをfinallyで解放し、開始済み実Taskを待機する。 |
| 実LR2と試聴・設定の標準接続 | [`ApplicationComposition`](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs)、[`Lr2SongDbSyncWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) | [`ApplicationCompositionTests`](../../../BeMusicSeeker.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs) は実実行時の準備・実処理待機から、新規試聴/保存Busy・既存試聴維持・draft保持・終端後同値保存を確認。設定編集後かつSave/Apply直前の対象値・所有XML・保存/再読込み回数を比較し、Busy呼出しによる追加副作用がないことを確認する。 |
| 同じ操作の設定入力と次回の捕捉 | [`Lr2SongDbSyncRequestCoordinator`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncRequestCoordinator.cs)、[`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`CustomFolderOutputSettingsSnapshotTests`](../../../BeMusicSeeker.Tests/Playlist/CustomFolderOutputSettingsSnapshotTests.cs) は捕捉Aの不変と次回Bを確認。標準構成の `StandardComposition_Lr2UsesCapturedGenerationOptionAndAdoptsDraftOnNextExplicitRequest` はA捕捉後の編集でもAの実生成を確認し、非配置の生成フラグだけを変更して次の明示要求で更新する。出力配置の未保存draftと保存後公開は標準構成の実Save試験に分担する。生成optionの未送信検知だけをA=trueからB=falseへ編集する場合は、現行性対象を固定し、Aの未送信定義とフォルダ行の実保存・Completed、次要求の定義・行除去とCompletedを確認する。設定入力の一回捕捉と明示受渡しは静的確認、現行性確認の再読込みは維持する。 |
| 標準playlistの生存権限と保存結果 | [`ApplicationComposition`](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs) の `CreateBmsPlaylist` | [`ApplicationCompositionTests`](../../../BeMusicSeeker.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs) の `StandardComposition_PlaylistBindingGeneratesFileAndPersistsFolderUnderAcceptedLr2Capability`: 実生成ファイルと実フォルダ行、内部終端後も外側lease保持を確認。 |
| 受理済み保守とLR2の非重複・実更新 | [`BMSLibrary.InstallableMaintenance`](../../../BeMusicSeeker/Models/Library/BMSLibrary.InstallableMaintenance.cs) | [`InstallableMaintenanceAdmissionTests`](../../../BeMusicSeeker.Tests/Startup/InstallableMaintenanceAdmissionTests.cs) は実初期化の依存・実schedulerのpost枠1を維持し、手動LR2準備中に必須保守が受付を待つ交差から、実フォルダ表・保守表の両更新、全Taskとschedulerの終端・次の明示操作まで確認。 |
| 準備面の合成・消費・実生成と保存の分担 | [`BMSLibrary.Lr2SynchronizationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs)、[`BmsLr2SongDbSyncWorkflowRuntime`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) の `PreparedSurface` と入力構築caseは既存管理主体境界で合成・消費・失効を確認する。実生成から保存はQueueと標準構成で確認し、管理主体のfakeが自作する準備順は上位の判定基準にしない。 |
| 受理済みLR2継続の権限と実Task待機 | [`Lr2SongDbSyncWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs)、[`FileDiffReloadWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/FileDiffReloadWorkflowOwner.cs) | [`Lr2SongDbSyncWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbSyncWorkflowOwnerTests.cs)、[`FileDiffReloadWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Startup/FileDiffReloadWorkflowOwnerTests.cs): 生存権限・reason・要求条件を明示転送し、保持Task終端前の後段停止、元失敗と継続条件を確認する。 |
| 生成入力と走査なしの候補構成 | [`Lr2SongDbSyncInputBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncInputBuilder.cs) | [`Lr2SongDbSyncInputBuilderTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbSyncInputBuilderTests.cs) |
| 完全なフォルダ集合・衝突・一括確定 | [`Lr2FolderTableReconciliationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderTableReconciliationService.cs) | [`Lr2FolderTableReconciliationServiceTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2FolderTableReconciliationServiceTests.cs)、[`Lr2FolderRowGeneratorTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2FolderRowGeneratorTests.cs) |
| 同期状態と生成列の永続化 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs)、[`Lr2SongDbWriter`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbWriter.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs)、[`Lr2SongDbSyncServiceTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbSyncServiceTests.cs)、[`Lr2SongDbWriterTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbWriterTests.cs) |
| 操作内の確定結果の直接引渡し | [`LibraryFileInitializationResult`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/LibraryFileInitializationResult.cs) | [`Lr2SongDbSyncCommittedInputTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbSyncCommittedInputTests.cs) |
| 確定パスのチャンク処理・分母・保存結果の維持 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) の `UpsertSongRows` | [`Lr2SongDbSyncCommittedInputTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2SongDbSyncCommittedInputTests.cs) の `CommittedInputSongRows_CompleteAcrossChunkAndOrderingWindowBoundaries`: 全件が直接確定入力の対象の0・1・1,000・1,001・10,001件で、読取りなしの内部生成成功、Running・未設定完了時刻・完了進捗不在、確定処理位置、保存行と利用者列の保持を確認する。全体Completedは実coordinatorの投影・状態保存境界へ分担する。混在入力は既存の `CommittedInputSongRowsSkipReaderAndCurrentnessRead`。 |
| 採番と順序枠の所有権 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) の `UpsertSongRows` | 採番より前の枠取得、全退出経路の返却条件、番号順回収時の返却を静的に確認する。境界件数のテストでは本番のCPU別並列度を使い、特定のスレッド切替順を強制しない。 |
| 実確定後の必須引渡しと任意通知の故障分離 | `Lr2SongDbSyncService` | `Lr2SongDbSyncServiceTests.SyncService_RequiredCommittedCallbackFailureKeepsDurableFactsAndCursor` は詳細／互換性の2条件で、callback内の別readonly接続により実確定を観測し、任意進捗・性能記録も故障している条件で元stack・未完了・保存行／利用者列／実cursorとrollback不在、他の必要な引渡しを確認する。 |
| 所有取消と非所有取消に交差する必須確定引渡しの実故障 | `Lr2SongDbSyncService.UpsertSongRows` | `Lr2SongDbSyncServiceTests.SyncService_PipelineCancellationRacePreservesGenuineCauseAndCommittedPrefix` は実1001行と二つの実chart入力、reader／必須callbackの別gateを使う。詳細／互換性の2条件ではCPU1でも実workerの所有取消が先着した診断を待ち、元callback型・message・stackと保持中readerのjoin待機を確認する。第3条件では要求未取消のまま末尾readerから要求token付きOCEを先着させ、診断時の要求未取消を確認してからshutdown／取消と別callback故障を起こす。外側が通常取消ではない実失敗であり、先着OCEのmessage・token・stackが保持されることを確認する。各条件で全必要callback各一回、別readonly接続のRunningからFailed・song_rows・cursor1000・未設定完了時刻、実確定prefixと全利用者列、未変更tail、完了進捗不在、authorizerによるrollback不在と全reader／callback終端を確認する。既存pipelineの実故障first-cause保証を維持する。 |
| 終了flagがtoken取消に先行する正規終了とチャンク巻戻し | `Lr2SongDbSyncService.UpsertSongRows` | `Lr2SongDbSyncServiceTests.SyncService_ShutdownCancellationAtSongCommitRollsBackWholeChunk` は同じ1譜面と実SQLite writerで、実詳細保存後の終了flag＋即時token取消／終了flagのみを確認する。両条件とも要求token付きOCE、flagのみではservice返却時も要求未取消、元title／artistと詳細行の巻戻し、実transaction終端、cursor0・未設定完了時刻・Failed／Completed保存なし、必須確定callback不在を確認する。先着非所有OCEと必須callback優先・全joinは既存3条件のpipeline race、未解決巻戻しの優先はauthorizerケースが分担する。 |
| 終了取消後の安全な巻戻し・未解決状態と確定cursorの終端引渡し | `Lr2SongDbSyncService` | `Lr2SongDbSyncServiceTests.SyncService_ShutdownRollbackDistinguishesSafeFallbackFromUnresolvedTransaction` は実1001行と所有接続のSQLite authorizerでROLLBACKコンパイルだけ一回／二回拒否する既存2条件に、安全fallback＋終了flagのみを加える。全worker join後の末尾writerでflagを立てた後の通知を収集し、安全rollbackの最後の通知がcursor1000・total1001・song_rows・段階件数0/0・autocommit1であることを時間待ちなしに確認する。flagのみでは要求token未取消のまま要求token付きOCEを返し、記録後の任意reporter故障でも元OCEとDB結果を保持する。両rollback故障の実失敗・原因診断、確定prefix／利用者列とdurable cursor、未確定tail・未設定完了時刻・未完了、authorizer解除と接続解放後の別接続読戻しを維持する。 |
| 必須投影失敗と同時終了、先行投影と実要求終端 | `Lr2SongDbSyncRequestCoordinator`、`BMSLibrary.Lr2SynchronizationOwner` | `BmsLibraryLr2SongDbSyncTests.QueueLr2SongDbSync_CompletionWaitsForRequiredProjectionAndOwnerCheckpoint` は実モデル／SQLite／DataPortと投影portの局所decoratorを使う。投影入口をgateで保持し、別readonly接続のRunning・未設定完了時刻・確定行／利用者列／cursor3、実Task未完了・両受付保持・完了版不進行を確認する。解放後の実投影成功はCompleted、通常確定後／終了取消catchでの必須投影故障はFailed、投影成功後の所有終了はIncompleteとする。Failed保存だけを拒否する実triggerでも保存先のRunning・元投影原因・公開Failedを維持する。先行索引・一部互換性投影を保持し、投影を再試行せず全実Taskと両受付の終端後にUIキューを排出する。正常完了・保存失敗・通常終了取消は既存Queueケースが分担する。 |
| reader/workerの即時故障停止、元原因と全開始処理の終端、実要求取消の区別 | `Lr2SongDbSyncService.UpsertSongRows` | `Lr2SongDbSyncServiceTests.SyncService_PipelineTerminationPreservesFirstCauseAndCommittedPrefix`。実解析・実SQLite・実1,000件確定を使う1001行の末尾内部/OOM故障と要求取消はCPU1でも確認する。9002行のparser/reader欠番は実policyで必要な並列度がある場合だけ実行し、本番診断のordering window容量、最終枠のreader、保持中の健全parserをgateで観測して、元stack・prefix/利用者列保持・解析失敗非生成・全callback終端を確認する。 |
| 局所BMS範囲・祖先・完全一致キー | [`Lr2NormalFolderSyncScopeBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2NormalFolderSyncScopeBuilder.cs)、[`Lr2NormalFolderDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2NormalFolderDbSyncService.cs) | [`Lr2NormalFolderSyncScopeBuilderTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2NormalFolderSyncScopeBuilderTests.cs)、[`Lr2NormalFolderDbSyncServiceTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2NormalFolderDbSyncServiceTests.cs)、[`BmsLibraryFolderRenameRefreshTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryFolderRenameRefreshTests.cs) |


## 関連資料

[一括再構成の設計判断](../../decisions/lr2-song-db-one-shot-reconciliation.md)、[索引](../core/data-and-indexes.md)、[プレイリスト出力](../playlist/storage-and-export.md)を参照します。
