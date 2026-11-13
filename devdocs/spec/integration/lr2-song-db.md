# LR2の楽曲DB生成と同期

## 目的と適用範囲

LR2連携モードで、現在のBMS譜面・検索ルート・プレイリスト出力から、LR2の `song.db` にある `song` 表と `folder` 表を生成する契約を定めます。対象は、起動時・差分読込み後・設定画面からの全体同期と、ライブラリ変更・プレイリスト出力に伴う局所同期です。LR2自身の起動時自動更新には依存しません。

## 用語

[共通用語集](../glossary.md)を参照します。受付のLとPは[操作の競合ポリシー](../core/operation-concurrency-policy.md#用語)の定義を使います。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 全体同期 | 全登録ルートの完全な入力から `folder` 表全体を置き換え、既存の `song` 行の生成列を更新する同期。 |
| 局所同期 | 一つのライブラリ変更・プレイリスト出力で確定した対象と、その必要な祖先だけを反映する同期。 |
| 生成列 | 現在の入力から作り直せる `song`・`folder` の列。 |
| 利用者列 | 利用者が編集する `song` の `favorite`、`tag`、`adddate`。同期は保持する。 |
| 必要集合 | 同期後の `folder` 表に存在すべき行のパスの集合。 |
| 準備面 | 全体同期の前に、プレイリスト出力と組込みカスタムフォルダを準備した結果。管理出力の定義を含む。 |
| 確定パス | 同じ操作のファイル差分がDBへ確定したBMSパス。 |
| 楽曲チャンク | 楽曲行と関連する事実を一つのトランザクションで確定する単位。 |
| 処理位置 | 同期状態に保存する、確定済みの処理件数（`processed_cursor`）。次の要求の再開位置には使わない。 |
| 終端投影 | 確定済みの現在値・互換性の事実を、所持譜面の現在値へ反映する処理。正常完了に必要な必須反映である。 |

## 仕様

### 管理範囲と設定

#### 行の担当

`song` と `folder` の生成列は、再生成できるデータです。行と列の担当を次のように分けます。

| 対象 | 担当 | 全体同期での扱い |
| --- | --- | --- |
| `song` 行の追加・削除 | ファイル差分検出と、対象範囲の差分処理 | 行の所属を変えず、存在する行の生成列だけを更新する。不要行の削除や、管理用の補助行・ダイジェストの整理は行わない。 |
| `folder` 行 | 全体同期と局所同期 | 全体同期は完全な入力から必要集合を作り、表全体を置き換える。局所同期は明示された範囲だけを反映する。 |
| 利用者列 | 利用者 | 明示的な保持対象とし、同期で書き換えない。 |

BMSONはLR2へ出力せず、本アプリの `bmson_song` などで管理します。BMSONにはLR2互換性警告を出さず、BMSONの保守でLR2の `song` 行を生成しません。

スタンドアロンモードでは、LR2用のフォルダ行、`.lr2folder` の探索、`folderinfo.txt`、組込みカスタムフォルダの階層を生成しません。

#### LR2の設定と検索ルート

LR2環境の確定時と、起動時の設定読込みで、`config.xml` の `<autoreload>` を `0`（手動更新のみ）にします。

BMS検索ルートは、本アプリの共通画面から管理します。ルートを追加する操作そのものでは再帰走査せず、次の通常の差分更新に含めます。入力は、存在するディレクトリで、かつShift_JISで表せるパスに限ります。相対パスと、同一・親子関係の重複は、正規化して検証します。

### 受付と実終端

全体同期の受付の開始、競合時の見送り、開始後に同じ権限で実作業・保存・後片付けを直接待つことは、[操作の競合ポリシー](../core/operation-concurrency-policy.md#設定全体操作終了)の「全体のLR2同期」を正本とします。受付の一般規則は[受付の開始・終端と権限](../core/operation-concurrency-policy.md#受付の開始終端と権限)、受理済みの継続による権限の借用は[受理済みの継続](../core/operation-concurrency-policy.md#受理済みの継続)に従います。この節では、LR2同期に固有の条件だけを定めます。

- 起動時の全体同期と、差分読込み後に必要と判定した全体同期は、開始時に受付が競合すれば見送ります。その場合も、確定した譜面と通常フォルダの差分は保持し、参照を更新します。未実施の全体同期を `Completed` とせず、既存の `Needed`・`Incomplete` などの状態と実際の結果で区別します。
- 推定入力を実際に変更する受理済みの背景更新は、先行操作の終端後に共通受付を取得してから実更新します。
- 受理済みの起動要求では、スケジューラーへの登録・依存関係・進捗を外側で維持します。登録しただけで完了とは扱いません。
- 同期の失敗は、状態を保存した後も元の例外を返します。先行して確定した変更は巻き戻しません。
- `Running` は進捗表示であり、物理処理の成功を表しません。
- 受付とは別に、DB接続の短いロック、SQLiteのbusy待機、永続状態・進捗・`Incomplete` の記録は、それぞれの規則に従います。

### 全体同期の流れ

全体同期は、次の順に進みます。前の段階が成功しなければ、後の段階を確定しません。

1. 事前準備として、プレイリスト出力と組込みカスタムフォルダの準備面を作ります。この間のファイル作成・検証は、フォルダ表の確定と分けます。
2. カスタム定義とディレクトリ情報を読み、通常フォルダとカスタムフォルダの完全な投影を検証して、`folder` 表を一括で確定します（[folderの全体生成](#folderの全体生成)）。
3. 楽曲の生成列を、楽曲チャンクごとに確定します（[楽曲チャンクの確定と必須反映](#楽曲チャンクの確定と必須反映)）。
4. 保守書込み処理と接続を解放した後、終端投影を行い、同じ入力による同期状態を確定します。

### 同期への入力

#### 設定入力の捕捉

一つの受理済みLR2操作は、共通受付の取得後、非同期の準備を始める前に、小さい変更不能な設定入力と、カスタムフォルダ出力設定のスナップショットを一回だけ捕捉します。生成準備、プレイリスト出力、実処理の入力へは、この捕捉値を明示的に渡して一致させます。

- 出力配置は、保存・適用済みの配置のスナップショットを使います。設定画面の別セクションにある未確定の候補で、現行性の判定や出力先を変えません。
- 配置以外の生成フラグも、確定した値を捕捉します。値の変更は、次の明示要求で新しく捕捉したときに使います。
- LR2の入力とは独立した最新性・世代の検査は、捕捉値とは別に行います。

検索先と管理出力の配置は、LR2設定の管理主体 `Lr2ConfigOwner` が公開する一つの変更不能な入力から捕捉します。一つの操作の事前検査・走査・親フォルダのキャッシュ・導入は同じ捕捉値を使い、採用後に外部で変わったXMLの内容は取り込みません。検索先の実在・利用可能性と、実際の保存の失敗は、それぞれの操作時に検査します。

#### 入力の範囲

全体同期は、現在のBMSパス、検索ルート、必要な祖先ディレクトリとその更新時刻、`folderinfo.txt`、探索済みの管理外 `.lr2folder`、アプリの出力、LR2の組込みフォルダ、設定値、解析結果・互換性警告・リソース調査の結果を受け取ります。不完全な入力を、同期内の広範な再探索で補って成功扱いにしません。

最終的な入力は、その操作の走査結果、確定した行の集合、同じ操作で生成・検証した完全な準備面から作ります。後続の途中再走査で、新旧の入力を混ぜません。走査を伴わない新規要求は、現在の入力を取得します。UIの要求世代、読取りキャッシュの失効、永続化したLR2の状態は維持します。

LR2に見せる検索先と、本アプリが譜面・リソースを走査するルートは、用途が異なります。通常出力先・追加出力先・ルート出力先の扱いは[設定](../runtime/settings.md#カスタムフォルダと検索対象)を参照します。

#### 利用者登録と走査対象

LR2の検索先は、二つの用途に読み分けます。どちらも、`Lr2ConfigOwner` が公開した登録と、同時に公開された出力配置から作ります。

| 用途 | 作り方 |
| --- | --- |
| 利用者が登録した検索先の一覧 | 通常出力先・追加出力先・ルート出力先の管理領域と、その配下を除く。存在しない登録と保存順は保つ。 |
| 走査対象のルート | 存在確認、Shift_JISで表せること、祖先による包含の縮約で正規化する。通常出力先は含め、追加出力先とルート出力先の領域は除く。 |

LR2設定の管理主体を持たない実ライブラリを、スタンドアロンの入力へ読み替えません。

#### LR2ルートを基準にした登録パス

LR2の検索先の登録は、相対・絶対のどちらも受理し、保存時にはLR2ルートを基準にした絶対パスを `jukebox/path` の本文へ保存します。

- 完全修飾でないパス（ルート相対・ドライブ相対・通常の相対）は、`Path.GetFullPath(path, LR2RootPath)` の標準構文で解決します。プロセスの現在ディレクトリや、ドライブごとの現在ディレクトリには依存しません。
- 保存し続ける `path` 要素の未知の属性、未知の兄弟要素、順序を保持します。読取りの操作では保存しません。
- 起動時の既存登録の調整も、差分のない `SaveRoots` を通して、相対の本文を絶対パスにします。
- 通常の楽曲の絶対パス化は、組込み・管理外カスタムフォルダの相対DBパスの特例（[カスタムフォルダの階層](#カスタムフォルダの階層)）と区別します。

LR2側の照合との一致も保証します。固定版の[OpenLR2の読取り](https://github.com/GOMazk/OpenLR2/blob/a87100a0a0f629a5e8130d1e8a3b5c807b9bf96c/LR2/LR2_configsave.cpp#L14-L48)は末尾区切りを一つへ揃え、[ROOT照合](https://github.com/GOMazk/OpenLR2/blob/a87100a0a0f629a5e8130d1e8a3b5c807b9bf96c/LR2/LR2_songmanage.cpp#L2827-L2863)は大文字小文字を含めた文字列の完全一致を使います。このため、XMLの登録、通常の `folder` 行、その子の `parent`、`song` の `folder`・`parent` を一回の処理で同じ表記に揃え、LR2によるルートの再探索と、既存曲の新曲（NEW）扱いを防ぎます。CRCは、保存パスと同じく末尾区切り一つにNULを付けた文字列を計算対象にします。

### songの生成

#### 照合と生成列

ファイル差分検出は、信頼できる走査範囲の現在のパスを、DBの完全一致キーで照合します。行の収束、大小文字だけが異なるパス、同一MD5の一対一の移転に伴う保存値の引継ぎは[パスの識別](../core/path-identity.md)に従います。

`level`、`judge`、`date`、`folder`、`parent` などの生成列は、現在の入力から作ります。`date` は譜面ファイルの更新時刻をUnix秒で保存し、0や負の値も有効なUnix秒として扱います。新しい行の `adddate` が未設定なら、同期時点のLR2形式の時刻で初期化します。

#### レベル

- 共通現在値から生成列を保存するときは、現在値の `level` を優先します。難易度表によるレベル上書きの後も、取得済みの譜面情報に残る元の表記レベルで上書き値を戻しません。
- 文字コード補正の限定更新は、既存の `level` を維持します。保存後の再読込みは、確定済みのレベルを使います。
- 手動再同期だけは、`level` を譜面由来の値へ戻します（[手動再同期](#手動再同期)）。

#### folderとparentのCRC

`song` の `folder` と `parent` は、LR2互換のCRC32です。譜面のディレクトリ（`folder`）またはその親（`parent`）のパスの末尾区切りを一つにし、NULを付けたShift_JISのバイト列から計算します。計算は `LR2CRC32` と `Lr2SongFolderParentNormalizer` を正本とします。

パスをShift_JISで表せない場合や、Windowsのパスとして解釈できない場合も、管理対象のBMS行は削除しません。両列を `NULL` にし、LR2互換性警告を付けます。

新規追加と `UpsertSongs` は、この正規化を保存前に行います。相対パスの補正は、絶対パス化とCRC計算の両方が成功した場合だけ `song.path` へ反映します。計算できないパスだけを中途半端に更新したり、警告対象の行を次の走査で削除・再追加したりしません。

#### 読取り・解析の失敗

独立した譜面の取得・解析の未知の失敗をその回だけスキップし、恒久的な解析不能記録にしない一般規則は[入力不良と対象のスキップ](../core/exception-handling.md#入力不良と対象のスキップ)に従います。LR2同期では、失敗の種類ごとに次を保持します。

| 失敗 | 扱い |
| --- | --- |
| 譜面本体を読み取れない | DB境界で、同じ完全一致パス・MD5の既存行を使い、取得できなかった生成列を保持する。現在のテキストグループと、取得済みの詳細に対応する限定した列だけを反映する。保存モデルから共通現在値や同期要求を参照させない。 |
| 基本入力（読取り・ハッシュ・軽量解析）の通常の未知失敗 | その回は対象を採用せず、旧楽曲行と利用者列、正常な別対象を保持する。正常な代替行の生成へ置き換えない。既知のI/O失敗と、読取りバッファーを得られない場合の既存の互換代替は維持し、扱いが異なる用途を区別する分類だけを残す。 |
| 詳細解析だけの通常の未知失敗 | [譜面情報の失敗](../library/chart-info.md#失敗)に従ってその回の対象の失敗として終え、正常に得た基本楽曲行と、正常な別対象の詳細を保持する。 |

いずれの場合も、失敗した対象の順序位置を終端まで処理し、欠番によって後続を止めません。未完成の結果を成功として採用せず、取得・生成だけを囲む捕捉範囲に、DBの確定・必須引渡し・共有状態の更新を含めません。要求の取消と、待ち行列などの基盤の故障は、この対象スキップと区別します（[並列読取りと順序回収](#並列読取りと順序回収)）。

#### 再復号のメタデータ限定保存

所持BMSの文字コード変更と、リソース再調査に伴って再復号した場合は、既存の `song` 行へ限定したUPDATEを行います。

- **対象**: `SongMetadataUpdate` に捕捉した完全一致パス・MD5と一致する、既存の同一内容の行です。保留・未登録・別MD5の行は新設しません。
- **更新する列**: `title`、`subtitle`、`artist`、`subartist`、`genre` と `date` だけです。前の5列には表示合成前の `RawTitle`、`RawSubtitle`、`RawArtist`、`Subartist`、`Genre` を使い、`date` には捕捉したファイル更新時刻のUnix秒を使います。
- **保持する列**: BPM・ノート数・exlevel・type・LN/BGA/RANDOM・レベル・判定・利用者列を含む、他の全列です。
- 全解析や既存の全列の読戻しを、保存の条件にしません。

メタデータの更新は、文字コード・修正印などの保守値と同じ操作単位のトランザクションで確定し、DBの確定に成功した後にだけ、所持譜面の現在値と通知へ反映します。

`song` を更新するかどうかは、次のとおりです。

- 明示の文字コード指定で、生メタデータが同値なら、`song` の更新を要求しません。
- 選択・全所持の強制リソース再調査で、具体的な確定文字コードを保持して再復号した文字列が同値なら、`song` を更新しません。資源だけが変化した場合は保守値だけを保存し、保守値も同じならDBへ書きません。
- 未確定の値を再検出して非CP932の文字コードが確定した場合は、再復号した文字列が同値でも、限定更新を行います。

確定文字コードを保持する条件は[譜面の読取り](../library/chart-file-reading.md#導入と手動再走査)に従います。

#### TXT列

通常導入・資源のみ上書き・重複統合が成功して最終的なTXTの配置が決まったとき、追加・移転した譜面と同じ親の直下にある存続BMSの `song.txt` は、[導入の保存境界](../library/mutations.md#導入と導入先修正)で導入先の実配置から確定します。

- 存続する譜面は、同じトランザクションで `txt` 列だけを更新します。
- 保守とパッケージ表示の登録後の全所属で、保存値と共通現在値を一致させます。配置・更新日時・内容が変わらない直後の差分読込みで、不要なTXT差分の再解析を起こしません。
- BMSONには、このLR2列の規則を適用しません。

#### 手動再同期

設定画面からの手動再同期は、`folder` と `song` の生成情報を全件作り直します。

- `level` を譜面由来の値へ戻します。要求が成功したら、そのawaitの直後に、一覧の現在値 `Level` / `LevelText` も同じ値へ揃えます。UI通知の排出や次回の再読込みを待ちません。
- 新たな解析と現行の詳細キャッシュのどちらを採用した場合も、生成済みの値を再利用します。
- 利用者列を保持し、`song` 行自体の追加・削除は行いません。
- 旧版が保存した、形式は有効だが値が誤ったCRCも修復します。自動修復用の版や状態は追加しません。

### folderの全体生成

#### 行の形式

`folder.path` が行のキーです。自身のCRC32を保存する専用列はありません。

- 通常ディレクトリは `type=1` とし、親ディレクトリのCRC32を `parent` に設定します。
- LR2ルート直下として扱う行は、`ROOT` にNULを付けた文字列のCRC32を `parent` にします。
- `date` は、ディレクトリまたは `.lr2folder` の更新時刻です。ルート行も実際の時刻と一致させます。
- カスタムフォルダの種別と親は[カスタムフォルダの階層](#カスタムフォルダの階層)に従います。

#### 必要集合

必要集合は、次の和から作ります。

- 検索ルート。
- BMSのあるディレクトリと、ルートまでの祖先。
- `folderinfo.txt` のあるディレクトリと、登録ルートまでの祖先。譜面もカスタムフォルダもない深い階層の `folderinfo.txt` も、既存の `folder` 行の有無にかかわらず、列挙済みの入力から含めます。
- テキストグループ。
- `.lr2folder` に必要な親の階層。

候補を絞る前に祖先まで拡張し、そのための追加の全走査は行いません。

同じパスに複数の候補がある場合は、組込みカスタムフォルダ、`.lr2folder`、`folderinfo.txt` のあるディレクトリ、通常ディレクトリの順に優先します。同じ順位で内容も同じなら、順序に依存せず一つにまとめます。内容が異なれば衝突です。

#### 一括確定と不完全な入力

全入力の探索・取得・解析と、必要な親行の構成が成功してから、一つのトランザクションで `folder` 表全体を置き換えます。不完全な入力や同じ順位の衝突では表を変更せず、`Incomplete` または `Failed` を返します。

正常に読み取れたBMSや `.lr2folder` でも、パスをCP932で表せない場合は、該当する通常の `folder` 行・`.lr2folder` 行・親行だけを投影から省き、他の行の同期を続けます。そのBMSの `song` 行は保持し、`folder` と `parent` を `NULL` にしてLR2互換性警告を残します。

一方、投影の事前検査で次を検出した場合は、全体同期を失敗させます。この規則を、BMS譜面本体の読取り失敗へは広げません。

- 探索が完了していない。
- `.lr2folder` または `folderinfo.txt` の読込みに失敗した。
- 必要なディレクトリのメタデータが不足している。

#### 全登録ルートの完全走査

全登録ルートを完全に走査した場合は、空の走査・不完全な走査に対する既存の保護を通過した後、全ての現在の走査結果と、通常フォルダ・祖先・`folderinfo.txt`・物理的に残存するカスタムフォルダの必要な親から、必要集合を確定します。

- 受理済みの登録ルート内にある全ての旧行（元の文字列）と必要集合の完全一致の差分を、一つの共通の計画で作ります。`song` の差分や更新時刻の有無を理由に、全体同期を省略しません。
- 保存行と生成行の識別、別キーの新規追加時刻、完全一致で存続する行の `NULL` の保持は[folderの追加時刻](../core/path-identity.md#folderの追加時刻)に従います。
- 二回目の同期でも必要集合を生成し、完全一致で同値の行の保存だけを省略します。

この通常の同期では、同じ操作で確定した表の一覧と出力配置が所有する各表ディレクトリと、その配下だけを削除対象から除外します。通常出力先の基点自身・その祖先・BMSの探索と生成は維持し、表の内部の縮小・削除は管理出力の管理主体が行います。管理範囲を取得できなければ、空の成功として削除を進めません。

管理外のカスタムフォルダが消失した場合は、その行を整理します。登録ルートの同じ場所の表記を訂正した場合は、走査開始前の旧表記も、外部同期の既存行の読取りへ渡します。この追加の読取りは現在の物理対象に限り、生成・削除の包含範囲や管理出力の除外を広げません。

#### 通常出力先の基点の表記訂正

走査で通常出力先の基点に同じ場所の表記差を確認した場合は、旧・新の基点をファイル走査の結果から既存の出力境界へ渡します。

- 差分読込みでは保存済みの `Completed` による省略の前に、起動では必須データの読込み（hydration）の後に、受理済みのLとPを保持したまま、必要な表の再出力とDB同期を待ちます。
- 登録、通常の親、カスタムフォルダのパスと親CRCを、同じ操作で揃えます。
- 全体同期が別途必要になる場合は、既存の完全生成へ委ね、再出力を重ねません。
- 必要な反映が未検証・失敗の場合は伝播し、先行して確定した媒体を独自に巻き戻しません。

#### 全folder行の取得

ライブラリの全体初期化（Full）と全体の再初期化は、既存の `folder` 全件取得を一回だけ使います。

1. 相対パス・親・日時の補正と、元の文字列の完全一致による削除→upsertを確定します。
2. その後、未変更の行と通常以外の行も含む全列の有効集合を、`SongTableLoadResult`、既存の監視側、`ApplyActiveFileScan`、`Lr2NormalFolderDbSyncRequest` へ渡します。

相対の `folder` 行を絶対パスにするとき、補正前の元集合に宛先の完全一致行があれば、元の行だけを削除し、宛先自身の日時補正と読取り不能による削除を優先します。元集合にない宛先だけを通常の新規値で作り、同じ初期化のUTC時刻を共通の生成まで使います。

差分の再読込み（ReloadFileDiff）は、Lの受理・事前検査・走査開始の後に、全 `folder` 行の全列を一括で取得します。

- Fullと再読込みは、どちらも同じ元の文字列の `Ordinal` の集合を、通常フォルダの生成・カスタムフォルダの依存・差分計画で共有します。比較後の旧行の再検索、更新時刻のための先行Taskや提供処理、文字種による代替の取得、常駐キャッシュは持ちません。
- 必要な操作スナップショットが欠けた場合は、空集合として成功させません。
- 個別の `song.db` でLR2ルートを推定できない場合も、全ての元の行を保持します。未補正の通常相対・ドライブ相対・ルート相対のパスを、現在ディレクトリやドライブごとの現在ディレクトリで絶対の物理範囲へ補完して、採用・削除しません。

#### 局所同期との分担

通常のプレイリスト・設定・譜面の変更は、明示された局所範囲だけを同期します。局所的な導入・移動・削除は、対象を限定した既存の読取りと同じ共通の生成・計画・保存を使い、全件のスナップショットによって範囲を広げません。

- 局所的な完全一致の整理指定は `Ordinal`、物理的なルートの包含とメタデータの照合は `OrdinalIgnoreCase` で比較します。
- 通常以外の保存行を保全し、既存のカスタムフォルダ候補の物理的な残存確認と、`folderinfo.txt`・祖先の確定を、生成の前に完了します。
- 後続で必要になったLR2の全体再整合は、最新のDBを独立して読み、通常フォルダの反映前のスナップショットを引き回しません。

局所同期の対象の捕捉と、移転・削除に伴うフォルダ行の扱いは[局所変更と性能](#局所変更と性能)に従います。

### 楽曲チャンクの確定と必須反映

#### チャンクの確定

通常の楽曲同期は、1,000行ごとの確定と、既存のCPU数に応じた並列度を使います。要求単位で処理幅（`Lr2SongSyncExecutionOptions`）を指定できますが、省略時の既定値と保存形式は変えません。

楽曲行、`chart_info`、解析失敗と互換性の事実、処理位置は、同じ楽曲チャンクのトランザクションで一回だけ確定します。

- 確定前の失敗は、そのチャンクを巻き戻します。先行して確定したチャンクと、利用者列・処理位置は保持します。
- 実際の確定後に索引・投影が失敗しても、DBを巻き戻したり、処理位置を未確定の位置へ戻したりしません。

#### 必須引渡しと任意通知

`ChartInfoRowsCommitted`、`ChartInfoParseFailuresCommitted`、`SongCurrentFactsCommitted` は、確定済みの事実の必須引渡しです。

- 現在値の事実は、実際に確定したチャンクの分だけを収集し、DB接続と保守書込み処理の解放後の終端投影へ集約します。同じ所持項目（`OwnedChartToken`）の `Level`・`LevelText` と、任意の互換性列だけを、一回の `CommitCurrentValues` へ渡します。全件のパス辞書や、新しい終端の系統は持ちません。
- 保存先の確認は、既存の一時表と当該チャンクの完全一致の結合に限ります。欠落した `song` 行のレベルを、成功として公開しません。基本入力の未知失敗や、有効な詳細のない代替行のレベルは保持します。
- 一つの引渡しが失敗しても、同じチャンクの他の必要な引渡しをそれぞれ一度試み、失敗原因とスタックを維持して同期を失敗させます。必須引渡しの内部で起きた取消例外を、同期が所有する終了の取消へ読み替えません。

進捗、性能記録、背景報告、画面通知は任意通知です。その失敗によって、確定した結果、失敗状態の保存、実際の要求の終端を飛ばしません。

#### 原因の選択と取消の分類

取消と失敗の区別、取消で失敗を隠さないことの一般規則は[取消と後片付け](../core/exception-handling.md#取消と後片付け)に従います。楽曲行の処理で原因が重なった場合は、次のように選びます。

- 先着した実故障を保持します。後発の取消で、先着した実故障や、所有しない取消例外を再分類しません。
- 先着した原因が所有する取消だった場合だけ、後続の必須引渡しなどの実故障を優先します。
- 所有する取消とは、要求トークンの取消、または管理主体の終了要求による取消です。記録した時点の取消例外とトークンで判定し、型・トークンが一致しない例外を所有する取消と扱いません。

原因にかかわらず、全ての待機へ直ちに停止を通知し、開始済みの読取り・評価と待ち行列の完了処理を終端まで待ちます。

所有しない取消例外は、未解決の巻戻しがあればそれを優先して判定し、`Failed` の保存を試みた後で、実失敗として返します。元のメッセージ、内部原因のトークン、スタックを保持し、後発の終了要求があっても通常の取消へ変換しません。

#### 巻戻しと取消時の終端進捗

楽曲チャンクの巻戻しでは、SQLiteの実際のトランザクション終端を確認します。保存の再実行とは区別し、[SQLiteの確定失敗](../library/file-db-consistency.md#sqliteの確定失敗)の規則と併せて扱います。

- 管理APIによる巻戻しが失敗しても、既存の直接SQLによる巻戻しが成功した場合、またはSQLiteが既に巻き戻している場合は、安全な取消完了として扱い、元の操作の失敗・終了取消の区別を保持します。管理状態だけが解除されて実際のトランザクションが残る場合も、直接の巻戻しを試みます。
- 実際の状態を確立できなければ、元の処理と両方の巻戻しの原因を診断します。未解決の接続の上へ状態の保存を重ねず、所有する接続の解放を含む実際の終端まで待ってから、失敗として返します。終了要求があることだけを理由に、正常な中断へ変換しません。

取消の種類ごとの結果は、次のとおりです。

- 記録した所有する取消は、要求トークンの取消を確認します。トークンが取消済みなら、既存の通知と例外の返却を使います。
- 終了要求が先着し、トークンが未取消のまま確認を通過した場合は、全ての開始済み処理の終端後に、実際の確定位置・全体の対象件数・段階 `song_rows`・段階件数0/0を通知してから、元の取消例外を返します。未解決の巻戻しがあれば、この通知より先に失敗とします。任意通知が故障しても元の例外と確定済みのDB結果を保ち、古い評価の進捗が確定位置を追い越さないよう、要求の調整処理が保存する `Incomplete` へ実際の確定位置を引き渡します。
- 終了要求を伴わない要求トークンの取消は、想定外の取消として `Failed`（`Unexpected operation cancellation.`）を保存し、要求トークン付きの取消例外を返します。

#### 必須投影と要求全体の完了

`Lr2SongDbSyncService.Run` が最終段階まで進んだこと（`FinalStage == CompletedStage`）は、内部の生成の成功だけを示します。このとき、永続状態は `Running`、実際に確定した処理位置、未設定の完了時刻のままであり、生成側は全体の完了進捗も出しません。

完了は、要求の調整処理が次の順で確定します。

1. 保守書込み処理と所有する接続を解放します。
2. 終端投影を一度実行します。
3. 所有するトークンの取消を確認します。
4. 既存の状態保存窓口の `MarkCompleted` で、実際の完了を保存します。
5. 保存に成功した後にだけ、完了の進捗と完了した要求の版を公開します。

終端投影を生成側のコールバックへ移して、保守書込み処理と所持集合の書込み処理のロック順を逆転させません。

終端投影の失敗と競合は、次のように扱います。

- 試行済みと成功済みを区別し、失敗した投影を `catch` から再実行しません。先行した詳細索引と、一部の現在値・互換性の投影の成功は保持します。
- 終了による取消の後に必須投影が失敗した場合も、`Failed` とします。元の実失敗に続く二次的な投影の失敗は、両方を診断して元の失敗を返します。
- 投影に成功した後でも、所有する終了が先着すれば完了を保存せず、`Incomplete` とします。
- 失敗状態の保存自体が失敗しても元の原因を置き換えず、公開している `Running` を解除して要求を終結します。保存先に `Running` が残る場合も未完了とし、確定していない完了によって次回の同期を省略しません。
- 全体の再試行や巻戻し、新たな永続的な故障台帳は追加しません。

### カスタムフォルダの階層

#### 種別

通常の `.lr2folder` は `type=2` です。特殊な種別は組込みフォルダだけに付け、`newsong.lr2folder` は3、`course1.lr2folder` から `course3.lr2folder` は6とします。それ以外は、本文に `#COMMAND` や `#TAG` があっても2です。

#### 実ルートと生成の停止境界

実際のルートの集合と、生成を止める境界は区別します。

- 登録ルートの子は、その登録ルートのCRCを `parent` に持ちます。
- ドライブやUNC共有のルート自身の行を含め、通常出力先・追加出力先の基点が登録ルートと重なっても、実際のルートの集合を祖先による包含で縮約しません。
- ルート出力の各表ディレクトリと、既知の相対パス `LR2files\CustomFolder` の扱いも、局所同期と全体同期で同じ階層判定を使います。

管理外のファイルは、実際の探索ルートの配下の階層を維持します。ルート自身だけを `ROOT` の直下とし、子ディレクトリやファイルを平坦化しません。

| 出力先 | ルートとして扱うディレクトリ | 親 |
| --- | --- | --- |
| 通常出力先 | 出力の基点 | 基点の配下の階層に従う。 |
| ルート出力先 | 各プレイリストのディレクトリ | 連番ファイルはそのプレイリストのディレクトリを親とする。出力基点の直下に置く単独ファイルだけを `ROOT` の直下にする。 |

#### 出力と行の生成

プレイリストの保存時は、`0000.lr2folder` からの連番をShift_JISで出力し、同じ生成内容からDB行を同期します。ルート出力の設定変更は、LR2設定の検索ルートにも反映します。

| 種類 | 全体同期での行の生成 |
| --- | --- |
| 管理外・組込み | 更新時刻が一致しても、物理的な本文を再解析する。 |
| 管理出力 | 確定したプレイリスト由来の変更不能な型定義を、準備面から実処理の入力へ渡し、物理的な本文を再解析せずにDB行を生成する。 |

管理出力が提供されていないことと、管理出力が完全に0件であることを区別します。期待する管理出力のパスに定義が不足している場合は、入力の不完全として止めます。組込みフォルダのメタデータだけの準備面を、管理出力の定義として扱いません。

#### 組込みフォルダ

組込みフォルダの場所は、LR2実行ファイルの配下の `LR2files/CustomFolder` です。

- `<customfolder>` のビットマスクが、`RANDOM`、`favorite.lr2folder`、`TOP10.lr2folder`、`PLAYLEVEL`、`CLEAR`、`RANK`、`ignore.lr2folder`、`INSANE01`、`INSANE02` の有効化を制御します。コースは常に扱い、新曲フォルダは `titleflash` と `song.adddate` から生成対象を決めます。
- DBにはLR2ルートからの相対パスで保存します。
- 旧版の物理的な絶対パスのキーは、削除の事実だけに使います。相対パスの完全一致行がなければ、実際の定義を解析して通常の新規行を作ります。相対パスの完全一致行が共存する場合は、その値を保持し、更新時刻による保持の分岐でも旧い絶対パスの行を退役させます。定義のない新規行や、旧い絶対パスの値の継承は行いません。

### 同期状態と起動

#### 状態の保存

同期状態は `lr2_song_db_sync_status` の `name=default` 行に保持します。状態、実行識別子、処理位置、総数、段階、エラー、更新・完了時刻を記録します。状態には `NotNeeded`、`Needed`、`Running`、`Completed`、`Failed`、`Cancelled`、`Incomplete` があります。

`signature` 列を含む状態表のスキーマは、旧版との互換のために維持しますが、署名は生成・判定・受渡しに使いません。既存の行を移行のために書き換えず、通常の状態の書込みでは空文字を保存します。失敗時の進捗の引継ぎは、同じ実行識別子だけで照合します。旧版へ戻すと、旧版の署名の判定によって再同期される場合があります。

#### 表示用の状態

表示用の同期状態（`Lr2SongDbSyncRuntimeStatus`）は、種別・段階の識別子・段階件数・処理位置・保存済みの診断・検査日時・要求を事実として保持します。状態名・進捗文・詳細は、読み取るたびに現在の表示リソースから作ります。

- 同じ状態のインスタンスのまま表示言語を変えると、状態名・進捗文・詳細は現在の言語になります。処理位置の数値、時刻、未知の段階識別子や、他の形式の先頭を持つ診断は変わりません。
- 詳細に含める検査日時は、保存値と時刻帯を変えず、グレゴリオ暦の `yyyy-MM-dd HH:mm:ss` で表示します。OSの暦や日時の区切りに依存させません。

#### Completedの意味と省略

`Completed` は、次の全てが成功したことを示します。

- 完全な生成入力の取得と、`folder` 表の確定。
- 楽曲の生成列の更新。
- 親のLとPの中で取得した入力の反映。
- 必須の詳細索引と、現在値・互換性の終端投影。
- 状態の保存。

保存状態が `Completed` なら、通常の全体同期を省略します。初回と、`Completed` 以外の状態では先頭から実行し、手動の強制要求は `Completed` でも実行します。省略した場合も、通常の譜面の追加・変更・削除と、必要な詳細の補完は続けます。`Completed` は、`chart_info` の存在・完全性・現行性の証明ではありません。譜面情報の補完は[譜面情報の管理](../library/chart-info.md)に従います。

#### 起動時の同期

通常起動では、ローカルの必須出力が成功した後、既存の状態から必要と判定した全体同期を、同じ親のLとPの中で直接待ちます。正常・失敗・中断と後片付けの実際の終端の後に、親の受付を解放します。

- ローカルの処理が失敗した場合は、同期へ進みません。
- LR2の同期だけが通常の失敗をした場合は、ローカルの確定を維持し、独立した結果・永続記録・通知へ残します。同期の失敗を、上位のローカル準備の成功へ混ぜません。
- 親のLとPの解放後、必須のUIと実際のホストの接続が成功してから、通常起動と設定からの起動で共通の終端で操作を解禁します。同期の失敗を、UIの成功の証明にも、UIの失敗の取消にも読み替えません。
- 進捗バーに再試行ボタンは設けません。設定画面の手動同期と次回の起動は、新しい入力を取得します。

失敗の保存と公開は、`BMSLibrary.Lr2SynchronizationOwner` の同じ責務に集めます。

- 実行開始前の境界で失敗した場合も、元の例外を型付きの結果へ残します。既に確定した `Failed`・`Incomplete` を上書きせず、重ねて通知しません。
- 本番の起動・再初期化は、この結果を受け取って、既存のLR2の状態を表示へ接続します。
- 終了による取消は、致命的でないLR2の失敗結果へ変換せず、上位の `ShutdownRequested` へ伝播します。親の受付の終端後も、初回の完了・操作の解禁・外部同期の投入には進みません。

#### 再実行と取消

未完了の状態、失敗後の再試行、手動の強制同期は、先頭から実行します。保存した処理位置から再開しません。

利用者向けの同期の取消操作は設けず、アプリ終了による内部の取消だけを区別します。安全に記録できる場合は、`Incomplete` と `shutdown_interrupted` を残します。旧DBの `Cancelled` は、読込み・表示・再試行の判定に対応しますが、新しい通常の実行では作りません。終了以外の取消は、失敗として表面化します。

#### 同期中の操作と進捗

同期中も読取り操作は許可し、DBの変更を伴う操作は制限します。

事前のプレイリスト出力と組込みカスタムフォルダの準備も、同期と同じLR2の進捗行へ通知します。段階・件数・不定表示の意味と通知の間引きは[進捗管理](../runtime/progress.md#lr2楽曲db全体同期の段階)に従います。進捗は通知だけを整理するものであり、DBの確定・処理位置の保存・物理的な整理の順序を変えません。通知先の例外で、DBの結果を変えません。

### 読込みと確定結果の再利用

#### 同期開始時の読込み

楽曲行の処理を始めるときに、現行のパーサー版の譜面情報と、再試行期限を考慮した解析失敗のMD5集合を一回だけ取得します。

- 各ワーカーは、既存の `ChartFileSnapshot` とこれらの値を共通の評価処理へ渡します。ワーカー内で追加の譜面読込みやDB照会を行いません。
- 不足・古い譜面情報も、専用のパーサーを追加せず、共通の補完処理を使います。
- 詳細解析の依存は、要求に指定したサービス、または既存の共通サービスを、一つの操作で一度だけ解決して各評価へ渡します。

#### 文字コードの採用と失効

全体同期の表示メタデータは、捕捉済みの楽曲行の保守値にある、同じパス・同一MD5の具体的な確定文字コードを採用します。具体的な文字コード名と、通常の資源・CP932で保存する画像・診断の条件は[譜面の読取り](../library/chart-file-reading.md#lr2同期と全件補完)に従います。既存の利用者列を保持し、文字コードの指定のための追加の読取り・全件の辞書・保存状態を作りません。

互換性の保守値を、同じ完全一致パスの別内容のハッシュへ更新するときは、そのハッシュの更新と同じSQLで `encoding=NULL`、`is_encoding_fixed=false` とし、旧内容に対する文字コードの指定を失効させます。

- 通常のDB再読込みの後の、次の明示同期でも、旧い指定を採用しません。
- 同一MD5で大小文字だけが異なる場合は失効させず、資源の健康度の列・警告の無視などの保存契約を保ちます。
- 読み取れず互換性の事実を生成しなかった対象には、失効を書きません。
- チャンクが失敗した場合は、ハッシュと指定を同時に巻き戻します。
- 過去に別内容へ付け替え済みの指定は、推測して修復しません。

#### 互換性の事実の評価

リソースのLR2互換性は、同じスナップショットから独立したCP932の走査で求め、三つの事実へ集約します。

- 未使用・対象外・任意の後続画像の定義も、全てのWAV・BMPと画像の原文として評価します。実際に採用した本文の通常の資源から逆算しません。
- 空・空白の値を最大長0にしません。非空の未知の種類や、名前のない定義も、LR2の長さの評価に含めます。
- 元の拡張子のCP932での長さ、259・260バイトの境界、`.` の整理を維持します。
- 復号できないことと親ディレクトリの参照は、同時に保持し、他の定義の長さの評価を続けます。
- 保存するBMS画像の三項目はCP932で先頭の非空の値、通常の資源は具体的な確定指定または自動判定で実際に採用した本文に従います。

#### 確定結果の直接引渡し

同じ操作のファイル差分がDBへ確定したBMSパスと走査結果は、変更不能な `LibraryFileInitializationResult` として後段へ直接渡します。親のLとPが必須処理と後片付けまで生存し、競合する正本の変更を受け付けないため、共有の証票の公開・取得・消費・破棄や、因果を推測する全体の版の照合は行いません。

- 再初期化と差分の再読込みも、同じ結果の引渡しを使います。手動同期は過去の結果を使いません。
- 確定パスの比較は `OrdinalIgnoreCase` で行い、別パスの同一MD5を連動して除外しません。DBの保存キーの完全一致とは区別します。

#### 確定パスの投入前除外

`Run` は、有効な現在の楽曲行のうち、その要求へ直接渡された確定パスに一致する行だけを、総数・初期進捗・実行状態を確定する前に一度だけ除外します。

- 現在の入力にない確定パスは、件数から差し引きません。
- 既存の対象行のリストの構成を置き換え、残る行の相対順を保って連続した番号を振ります。全パスの再整列や、新たな全件の複製は増やしません。
- 除外した対象には先行処理の枠・読取り候補・計算結果・チャンクを作らず、読み手・ワーカー・書き手へ流しません。

件数は次のように定義し、全ての表示・保存をこの対象集合に揃えます。

| 記号 | 意味 |
| --- | --- |
| `A` | 有効な候補の数。 |
| `E` | 入力行に一致して除外した数。 |
| `T` | 譜面の処理対象数（`A - E`）。 |
| `F` | フォルダの処理分。 |

- 入口の診断は `A`・`E`・`T` を記録します。
- 全体の `TotalCount` / `total_count` は `F + T`、譜面の段階の分母は最初から `T` です。確定済みの `ProcessedCount` / `processed_cursor`、段階の境界、チャンクの位置、結果・ログも同じ対象集合に揃えます。
- `SongRowProcessedCount` は投入後に処理した件数、`SongRowSkippedCount` はそのうち基本入力を採用しなかった件数だけを示し、投入前の除外と合算しません。処理中の確定パス用のカウンターは持ちません。

全件が確定パスの場合と、元の譜面入力が0件の場合は、どちらも `T = 0` として扱います。

- 譜面の段階は `T > 0` の場合だけ開始します。空のパイプライン、スキップ用のチャンク、不要な譜面の段階の状態・進捗通知を作りません。存在しない譜面の作業を表示しません。
- 全ての `ChartPaths`、祖先・フォルダの候補、現在の所属集合と利用者列は保持し、必要なフォルダの生成・検証・確定、`final_validation`、書き手の解放後の終端投影・完了の保存へ進みます。`final_validation` / `sync_state_saving` の不定の進捗は維持します。
- 0件であることを全体の成功の証明にしません。不完全なフォルダ入力、親の権限の終了、終了による取消、DBの失敗は、非成功の結果のままです。

除外されなかった対象は、1,000件単位（末尾は残りの件数）と同じ並列度で処理します。

#### 並列読取りと順序回収

楽曲行の処理は、複数の読み手と評価ワーカーが並列に読取り・評価し、単一の書き手が番号順に結果を回収して、チャンクごとにDBへ書き込みます。番号順の回収を待つ先行数は有限の枠で制限し、採番済みで未回収の項目は必ず枠を確保済みとします。ワーカーは追加の譜面読取り・DB照会を行わず、投入前の除外後の対象だけを順序回収へ通します。枠の取得・返却の詳細は、実装のXMLドキュメントを正本とします。

読み手・評価ワーカーの内部の故障は、次のように扱います。

- 番号順の回収が故障を発見するまで待たず、最初の原因を保持して、操作内の取消を直ちに通知します。先行処理の枠と両方の待ち行列の待機を起こし、残りの投入・生成・回収を止めます。
- 開始済みの全ての読取り処理・ワーカーと、待ち行列の完了処理を待ってから、元の型・メッセージ・スタックで失敗を返します。閉鎖済みの待ち行列への後続の出力や、内部の停止のための取消例外で、最初の原因を置き換えません。
- 通常の譜面の読取り不能は、既存の生成列の保持（[読取り・解析の失敗](#読取り解析の失敗)）へ進みます。内部の解析・資源の故障を、解析失敗の行へ保存しません。

内部の停止のための取消は、要求トークンを取消にせず、利用者・終了の要求の分類へ読み替えません。実際の要求トークンが取り消された場合は、要求トークン付きの例外と既存の状態の分類を使います（[巻戻しと取消時の終端進捗](#巻戻しと取消時の終端進捗)）。確定済みのチャンク・利用者列・処理位置を保持し、全体の巻戻しや、未確定の後続部分の新たな確定の保証は追加しません。

### 局所変更と性能

#### 取り込み後の自動推定と後段の導入

取り込み後の自動推定と[後段の自動導入](../library/install-estimation.md#取り込み後の自動推定に続く導入)では、初段と後段の同期できる確定分の必要な同期を、操作末尾の一回へ集約します。

- 同じ操作権限と、取り込み入口の設定を渡し、別の予約や自動再試行を作りません。
- 初段の成功後に推定が失敗・取消された場合、後段の準備が失敗・競合した場合、後段のパッケージ境界で取消された場合も、安全に同期できる確定分の必要な同期を待ちます。
- 設定が無効でも、初段の必要な同期は維持します。
- DB確定後の必須反映の失敗や、未回復の物理的な失敗など、既存の整合条件が同期を許さない場合は迂回しません。初段を含む確定の事実と、必要な同期が未完了であることを操作結果へ残します。
- 同期自体が失敗しても、推定・後段の元の失敗や取消を上書きせず、確定済みの変更を保持します。

#### 変更対象の捕捉

導入・削除・移動・改名・統合では、確定した結果から、追加・削除されたBMSパスと、旧新のBMSパスを捕捉します。

- 追加だけなら、現在のBMSの再検索は不要です。
- 削除・移動では、変更したディレクトリ内と、登録ルートまでの祖先のBMS件数だけを、既存の索引から取得します。BMSONを残存BMSに数えず、削除の範囲をルート全体へ広げません。
- 値は所持集合のロックと対応する版の下で固定し、確定した結果に可変の検索関数を持たせません。
- 自動改名も、共通の変更セッションの中で局所の値を取得し、LR2同期を一回で終えます。
- 取得・同期の失敗は失敗として返し、既に確定したファイル・DBの処理を再実行・巻戻ししません。

ディレクトリの比較と、DBの完全一致キーを区別します。通常の局所変更で、全BMSの列挙・整列や、全祖先の索引の再構築を行いません。全体同期では、一回の完全なフォルダの生成と確定を行い、楽曲行の所属は変更しません。探索が不完全なら、広い範囲の不要行の削除を行いません。

#### フォルダの移転

フォルダの改名・移動では、物理処理に成功した旧新の範囲を、同じ変更セッションへ渡します。

- 範囲内の管理外のカスタムフォルダは、BMSのない子の階層も含めて、新しい完全一致のキーと親CRCへ移します。SQL・表示列・定義の本文と、元の追加時刻（`NULL` を含む）を保持します。
- 統合では、物理処理が確定した実際の宛先を使います。
- 局所の反映には、BMSの直接の親だけでなく、成功した物理的な配下と、カスタムフォルダの実際の宛先の事実を渡し、直下がBMSONだけの場合も必要な親を捕捉します。
- CP932で表せず通常の行を省略した、`folderinfo.txt` だけの子の階層は、成功した移転先の配下だけを再列挙して、表せる名前へ戻ったときに生成します。
- 旧範囲・削除した範囲を再探索せず、失敗・未処理の範囲と、管理出力の所有境界を広げません。

大小文字だけの手動・自動の改名も、同じ明示的な移転として扱います。結果記録の最終表記を必要な祖先へ渡し、通常の `folder` 行と管理外のカスタムフォルダの旧い完全一致キーを整理して、親CRCを最終表記から計算します。元の追加時刻（`NULL` を含む）と、カスタムフォルダの全列を保持します。

文字制限を無効にして、CP932で表せない名前へ改名した場合も、BMS・BMSONの保存パス、保守、同じ所持項目の現在値、索引とパッケージ参照を最終的な配置へ揃えます。

- 自身または親のパスを投影できない通常の `folder` 行は、旧キーを退役させ、新しい行を省きます。そのBMSの `song.folder`・`song.parent` は両方 `NULL` とします。
- 同じ明示的な移転に含まれる管理外のカスタムフォルダは、新しい完全一致のキーへ列の値と追加時刻を保持して移します。表せないパスの `parent` は `NULL` にし、通常の階層から切り離します。
- 表せる名前へ戻すと、必要な通常の階層とCRCを復元し、親CRCを再計算します。全体再同期を操作完了の条件にしません。
- 表せない名前の間に別途明示した全体同期での投影の省略規則は変えません。警告の種類・フラグ・最新のパスは、既存の互換性警告の経路へ渡します。

#### 譜面の削除と残存するカスタムフォルダ

最後のBMSを削除・無効化した後も、局所の整理候補に残る `folderinfo.txt` の名称と祖先、管理外のカスタムフォルダの必要な親を、生成集合へ含めます。両方が残れば和を保持し、依存がなくなったBMSの親と、物理的に削除した範囲の不要な通常の行を整理します。

譜面だけを削除して、物理的なカスタムフォルダが残るときは、その直接の親と、登録ルートまでの通常の祖先を生成・保持します。削除候補の祖先の、別の子の階層にあるカスタムフォルダも依存に含めます。

- 候補は、整理する配下と削除候補の祖先の必要な範囲から、索引付きのDB読取りで完全一致のパスだけを取得します。
- 残存は、ファイル差分では同じ操作の完全な走査候補との交差で、カタログの変更では候補だけの物理的な確認で確定します。消失と取得の失敗を区別し、DBに残っていることだけを保持の根拠にしません。
- 管理主体が必要なメタデータを捕捉して生成処理へ渡し、計画は生成結果との差分だけを扱います。

カスタムフォルダも物理的に消失した場合は、不要な通常の親を整理します。旧いカスタムフォルダの行自身の即時削除は、追加の保証に含めません。カスタムフォルダの全列・完全一致のパスキー・無関係なルートは維持し、`folder` 表全体の走査、常駐の台帳、削除画面からの直接の補修、強制的な全体同期は追加しません。

OpenLR2の楽曲検索が先に成功した場合に、フォルダを同時に表示することは要求しません。フォルダ検索へ進む配置のCRC階層から、カスタムフォルダへ辿れることを保証します。

#### 局所同期の書込み範囲と管理出力の受付

カタログの変更に伴う局所同期が生成・更新する通常の行の対象ディレクトリは、次の和です。

- 追加・移転先の譜面の、登録ルートまでの祖先。
- 整理範囲に残るBMSの祖先、残存する管理外のカスタムフォルダの親と祖先、残存する `folderinfo.txt` の階層、通常の子が残る祖先。
- 削除・移動元の存続する直接の親とその祖先。存続する直接の親は、削除・移転元（譜面・フォルダ・カスタムフォルダ）の親ディレクトリから登録ルートへ向かって、変更の確定後もBMSが残る最も近いディレクトリです。子を削除・移動すると実際の更新時刻が変わるため、同じ同期で `date` を実時刻へ揃えます。決定には変更範囲と登録ルートまでの祖先のBMS件数の既存の読取りだけを使い、全件を走査しません。
- 変更元・変更先を含む登録ルート。BMSが残るかによらず、全体同期と同じく存続するものとして行を保持し、日時を取り直します。

変更と無関係な登録ルートの行は、局所同期で生成も更新もしません。整理（行の削除）の範囲は[譜面の削除と残存するカスタムフォルダ](#譜面の削除と残存するカスタムフォルダ)のとおりで、登録ルート自身は整理しません。資源だけの導入などBMSの変更がなく局所同期を行わない場合のディレクトリの時刻は、次回の起動時の走査・差分読込みで揃えます。

管理出力の受付（P）の交差判定には、上記の対象と整理範囲（整理の起点は配下を含む）を同じ集合から渡し、実際に行を書き換える範囲だけで判定します。局所同期はファイル・DBの確定後にPを新たに取得しません。交差する場合は、物理変更の前に呼出し元が取得したPを借用します。出力基点は登録ルートの子に置けないため（[既存登録との配置可否](../playlist/lr2-custom-folders.md#既存登録との配置可否)）、局所同期の範囲が管理領域と交差するのは、物理変更の実際の移動元・先・整理対象が交差する場合に限られ、各入口の物理変更前の判定でL＋Pが確定しています。交差するのにPがない場合は内部の不変条件違反の失敗として扱い、競合拒否の案内にはしません。これにより、通常出力先を走査ルートに含む構成でも、出力と無関係な変更はPの保持中に並行して確定し、局所同期まで完了します。

#### 導入後の新曲フォルダ

局所同期の確定後、今回追加したBMSの `song.adddate` のいずれかが `titleflash` の期間内で、`folder` 表に組込みの新曲フォルダ（`LR2files\CustomFolder\newsong.lr2folder`、type 3）の行がなければ、組込みフォルダの生成と同じ規則でその行を一件作ります。行の有無は、相対キーと現在のLR2rootでの旧い絶対キーの完全一致で確認します。移動・改名した譜面は追加に含めません。LR2rootに組込みのファイルがなければ作りません。判定は追加した行と完全一致の索引の読取りだけで行い、全譜面や `folder` 表全体を走査しません。期間の経過による行の撤去は、起動時の走査・差分読込み・全体同期の判定に任せます。作成の失敗は、他の局所同期の失敗と同じく必須反映の失敗として扱います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 同期詳細のグレゴリオ暦固定の日時と言語追従 | `Lr2SongDbSyncStatusMapper` | `Lr2SongDbSyncStatusMapperTests` はar-SA・th-THで `yyyy-MM-dd HH:mm:ss` の説明と、検査日時・元の状態・原因の保持を確認する。Windowsの `Lr2SongDbSyncStatusMapperTests.RuntimeStatus_SameInstanceFollowsLanguageAndKeepsFacts` は、同じ状態インスタンスの日英の往復で状態名・進捗文・詳細が現在の言語になり、種別・段階件数・処理位置の数値・検査日時・要求・未知の段階や他形式の診断の原文が変わらないことを確認する。 |
| 管理表配下の通常の整理からの保護と、基点表記の必須反映 | `Lr2FolderGenerationScopePlanner`、`BMSPlaylist.SynchronizeNormalOutputBasePathChangeAsync`、起動・LR2の処理経路 | [`SettingDialogLr2OwnerTests.NormalOutputScan_PreservesManagedHierarchyAndConvergesConfirmedPaths`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogLr2OwnerTests.cs) は、無変更の二周期と新規起動、CP932のギリシャ文字の稼働中の訂正・消失した外部の親、ASCIIの次回起動での初検出を分担する。全管理行とCRCの連鎖・通常BMSの利用者列を保持し、起動直後の `Completed` の実行の維持・修復対象0・ファイル日時の不変によって、全体同期や任意の修復による救済と区別する。表の削除・縮小・ルート出力先や追加出力先への移行は、既存の `BmsPlaylistCustomFolderOutputTests`、`BmsPlaylistMigrationAndRegistrationTests` が分担する。 |
| 残存するカスタムフォルダの必要な親と、不要な通常の親の整理 | `Lr2NormalFolderDbSyncService`、`Lr2SynchronizationOwner` | `Lr2NormalFolderDbSyncServiceTests.Sync_ConfirmedCustomDependenciesKeepOnlyRequiredAncestors` が、直接の親・別の子の階層・欠落した親・依存なしと、カスタムフォルダの全列・無関係なルートを実SQLiteで比較する。`BmsLibraryLr2SongDbSyncTests.RemoveLibraryCharts_PrunesNormalFolderRowsForRemovedBmsWhenLr2SongDbSyncEnabled` は、実際の削除入口での残存と全物理消失を接続する。候補の読取りと走査との交差が限定されていることは、本番経路の静的確認へ分担する。 |
| `folderinfo.txt` だけの深い階層 | `Lr2SongDbSyncInputBuilder`、`Lr2FolderTableReconciliationService` | `Lr2FolderTableReconciliationServiceTests.Reconcile_FolderInfoOnlyDeepHierarchyCreatesMissingAncestorsAndPreservesAddDate` が、新規・既存行の実DB・時刻・タイトル・親・追加時刻を確認する。`Lr2SongDbSyncInputBuilderTests.CreateInput_FolderInfoOnlyHierarchyReachesWholeTableProjection` は、走査結果あり・なしの実際の入力構築から最終DBへの接続と列挙回数を確認する。走査なしでは、列挙だけの外部の `folderinfo.txt` だけの階層と、組込みの準備面だけの深いカスタムフォルダ・`folderinfo.txt`・メタデータを混在させ、合流後の候補・祖先・異なるタイトル・CRC・組込みの相対DBパスの保持も確認する。 |
| 実際のルートと生成の停止境界の分離 | `Lr2FolderPhysicalParentDirectoryTargetHelper` | `Lr2FolderRowGeneratorTests.FolderHierarchy_RealRootsRemainIncludedAndChildrenUseRootCrc` が、通常・ドライブ・UNCと重なりを字句の条件で確認する。`Lr2FolderFileDbSyncServiceTests.Sync_RegisteredRootChildrenUseRootCrcForChangedAndPreservedCustom` は、実SQLiteで通常の場合と未変更の再利用を確認する。保存済みの `Completed` による省略の代表では、先に局所生成した正しい階層とDB全体の不変を確認する。`BmsPlaylistCustomFolderOutputTests.ReOutputCustomFolderAndCommitToDB_SyncsLr2FolderRowsFromConfirmedPlaylist` は、有限の必要な親から登録ルートの `folderinfo.txt` を捕捉し、局所の出力・全体同期の後もタイトルとCRCを保持する。 |
| 相対のXML・通常DBとLR2の照合の二周期での収束 | `LR2Config.SetBMSSearchDirectories`、起動時の登録調整、通常フォルダの差分 | `LR2ConfigTests.StartupRelativeRegistration_ConvergesForExactLr2ConsumerAcrossTwoCycles` が、実SQLiteでのルートの完全一致の比較、INSERTの成功による再探索の契機0、既存曲のNEW化0、パス・ハッシュ別の件数とスコア結合の一重性、更新時刻・スコア・利用者列を確認する。LR2本体の全体や集計画面の実行は含まない。同クラスの `RootPlanningDocument_ResolvesRegistrationsAgainstLr2RootWithoutFileAccess` が、ルート相対・同一/別ドライブのドライブ相対・`.`/親要素の標準構文での解決をメモリ上のXMLで確認する。要求から保存・公開への接続と、XMLの読取りでの非保存・未知の属性/兄弟/順序は `Lr2ConfigOwnerTests.EmptyRootChange_NormalizesRelativeTextWithoutLosingXmlInformation`、CRCの独立した値は既存の `Lr2CompatibilityGoldenTests` が分担する。 |
| `folder` 集合と生成経路の追加時刻 | `Lr2NormalFolderDbSyncService`、`Lr2FolderRowGenerator`、`Lr2FolderTableReconciliationService`、`Lr2FolderFileDbSyncService` | `Lr2NormalFolderDbSyncServiceTests.Sync_UsesRawExactFolderSet` が四つの集合の条件を担当する。`Lr2FolderRowGeneratorTests.GenerateNormalDirectoryRows_UsesFolderInfoTitleAndPreservesExistingAddDate` と `Lr2FolderTableReconciliationServiceTests.Reconcile_PreservesExistingFolderAddDateAcrossWholeTableApply` は、完全一致での存続・別の大小文字・共存の経路の保証を分担する。`Lr2FolderFileDbSyncServiceTests.Sync_MigratesExistingAbsoluteBuiltinRowToRelativePath` は旧い絶対パスだけ・相対パスとの共存・保持分岐での退役を、`Sync_MatchesExistingKnownRelativeLr2FolderRow` は値と `NULL` の保持を確認する。 |
| 更新時刻が同じ表記差と旧い完全一致キーの退役 | `SongTableLoadResult`、`BmsLibraryInitializationService`、`Lr2FolderGenerationScopePlanner` | `BmsLibraryInitializationLoadTests.LoadSongTable_FixesRelativePathsWithoutMaintenanceHydration` が、相対パスだけ・宛先の共存・宛先の読取り不能を通し、`song` の三列と、補正・削除・未変更を含む確定後のDBと返却値を比較する。`BmsLibraryInitializationLr2NormalFolderTests.ApplyFileScanDiff_ReusesOperationFolderSnapshot` は、Fullの結果と再読込みの一括取得の双方で、ASCII・CP932のギリシャ文字、更新時刻が同値の旧い行の退役・完全一致で存続する行の値と `NULL`・逆順・親CRC・利用者列・スコア・実譜面・ルート外の保全と二周期を確認する。`LoadFolderTableSnapshot_ReadsAllRawRowsAndColumnsForWholeOperation` は、再読込みでの全ての元の行・全列を確認する。`BmsLibraryInitializationFileScanTests.Initialize_StartupPublishesScanAfterLeaseReleaseAndIsolatesTerminalSubscriber` が既定以外の `song.db` の実際の受渡しと、相対パスを現在ディレクトリで解決しないことを、`Lr2NormalFolderDbSyncServiceTests.Sync_PrunesOnlyRequestedNormalFolderScope` が局所の対象外の既定以外の値の保持を分担する。既存の移動したBMS・完全一致の計画・大小文字違いの整理のケースも維持する。 |
| 保存済み `Completed` の再利用と旧署名の互換 | `Lr2SongDbSyncStatusService` | `Lr2SongDbSyncStatusServiceTests.Evaluate_ReusesLegacyCompletedWithoutChangingStoredRow` は、旧い複合署名付きの `Completed` の行とスキーマが変わらないことを確認する。無効・初回・`Completed` 以外・先頭の処理位置と、同じ実行識別子だけでの失敗時の進捗の継承も、同サービスの実SQLiteで確認する。 |
| 全体同期への確定文字コードの接続と、内容変更時の指定の失効 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs)、`CatalogMutationOwner.CaptureLr2SynchronizationInputRowSnapshot` | [`BmsLibraryLr2SongDbSyncTests.QueueLr2SongDbSync_PreservesFixedEncodingAndExpiresOnlyChangedContent`](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) は、通常のDB読込み・実際のカタログの捕捉から明示同期へ進み、Big5・UTF-8のDBの生の5項目と現在の一覧、指定・固定印・利用者列・健康度・警告の無視を確認する。同じパスの別内容では、指定の失効と、通常のDB再読込み後の次の同期で復活しないことを確認する。最初の旧い指定を採用しないことの識別を含む採否の条件、実際に採用した通常の資源と独立したCP932の保存画像・診断、単回の読取りは[譜面読取りのサービス系列](../library/chart-file-reading.md#実装とテストの対応)が分担し、MD5の大小文字差での保持もそこで確認する。`SyncService_RollsBackEncodingExpiryWhenChunkCommitFails` は、失効後の処理位置の保存失敗で旧いハッシュ・指定・健康度を保つことを、既存の読取り不能時の生成列保持のケースは指定を失効させないことも確認する。確定パスの投入前除外・BMSON・所持の保守は既存の各系列へ分担する。 |
| 利用者登録の分類とLR2の走査集合の分離 | [`Lr2SearchRootSnapshotOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SynchronizationPorts.cs) | `BmsRootRegistrationTests.Lr2Registration_ClassifiesPublishedPlacementWithoutChangingScanRoots` が利用者登録を、`BmsLibraryLr2SongDbSyncTests.CaptureBmsDirectories_*` / `Lr2SynchronizationArchitectureTests.SearchRootSnapshot*` が、実XMLの登録の保持・走査の正規化・通常出力先の包含と追加出力先・ルート出力先の除外を分担する。 |
| 登録と分類用の配置の同時捕捉 | [`BmsLibraryOptionsSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryOptionsSnapshot.cs)、[`Lr2SynchronizationPorts`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SynchronizationPorts.cs) | [`Lr2ConfigOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2ConfigOwnerTests.cs) が旧い捕捉値の不変と新しい登録・配置の公開を、[`BmsConfirmation_CompletesRealFileDiffBeforeClosing`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogEditCompletionTests.cs) が新しい登録を使った実際の走査・DB・表示と後続での維持を確認する。 |
| レベル上書き後の文字コード補正と再読込みでのレベルの維持 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) のレベル上書き、`SongMetadataUpdate` による限定更新 | [`BmsPlaylistMigrationAndRegistrationTests.PlaylistTableLevelOverwriteWorkflow_UpdatesOnlyExistingSongLevel`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) が、実際の上書き、メタデータの再解析、SQLiteの再読込みを連続して行い、上書き値が元のファイル・詳細の値へ戻らないことを確認する。 |
| 文字コード変更・再調査のメタデータ限定保存と、詳細未付与の起動状態 | [`CatalogMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMaintenanceOwner.cs)、[`CatalogMutationOwner.ApplyMaintenanceWriteUnderGuard`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMutationOwner.cs)、`SongMetadataUpdate` | [`SelectedChartMutationWorkflowOwnerTests.ApplyEncoding_RealStoreAfterDbLoadPreservesUnchangedSongColumns`](../../../tests/BeMusicSeeker.Windows.Tests/ChartOperations/SelectedChartMutationWorkflowOwnerTests.cs) と [`SelectedChartResourceHealthWorkflowOwnerTests.RescanAsync_RealStoreAfterDbLoadPreservesUnchangedSongColumns`](../../../tests/BeMusicSeeker.Windows.Tests/ChartOperations/SelectedChartResourceHealthWorkflowOwnerTests.cs) は、共有の [`MaintenanceMetadataSongFixture`](../../../tests/BeMusicSeeker.Windows.Tests/ChartOperations/MaintenanceMetadataSongFixture.cs) で、実DB読込み・最新の詳細索引の読込みの後も共通現在値の `ChartInfo` が未付与の起動状態を作り、本番の操作で生メタデータ5列と固定した更新時刻由来の `date`、他の全 `song` 列・背景の行の保持を読取り専用のSELECTで確認する。未確定からの再検出は、文字列の差分あり・同値の両方を扱う。同クラスの `Rescan_FixedUtf8AfterDbLoadPreservesMetadataAndOnlyPersistsResourceChanges` は、選択・全所持の確定UTF-8で、保存値・現在の一覧・ハッシュ・対象外の全列を保ち、資源の増減時に `song` のUPDATEを拒否しても保守値だけを更新することを確認する。保留での `song` の非新設と、同値・修正印は、既存の選択保留の実storeのケースと `BmsLibraryMaintenanceServiceTests` が分担する。 |
| 局所同期の書込み範囲、存続する直接の親の時刻、確定後のPの借用 | `Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation`、`BMSLibrary.Lr2SynchronizationOwner.SyncLr2NormalFoldersForCatalogMutation` | `Lr2NormalFolderSyncScopeBuilderTests.CreateForCatalogMutation_AddsSurvivingDirectParentsAndChangedRootsOnly` が、存続する親・変更を含むルート（BMSが残らない場合を含む）を含み、消える親・ルート外・無関係なルートを含まない条件を確認する。`BmsLibraryPackageInstallServiceTests.AutoInstall_UnrelatedNormalOutputScanRootSyncsFoldersWithoutPostCommitBusy` は、通常出力先を走査ルートに含み、Pを保持した状態での非交差の導入が、確定後の失敗なしに導入先の行と日時を反映し、通常出力先の行を変えず、同期状態を `Completed` に保つことを確認する。`BmsLibraryLr2SongDbSyncTests.RemoveLibraryCharts_PrunesNormalFolderRowsForRemovedBmsWhenLr2SongDbSyncEnabled` は削除で同じ条件と存続する親の `date` を、`RemoveLibraryCharts_RootLevelRemovalWithRemainingBmsRefreshesRootRowDate` は登録ルート直下の削除でBMSが残るルートの `date` を、`RenameChartFolder_MovesNormalFolderRowsForMovedBmsWhenLr2SongDbSyncEnabled` は移動元の存続する親の `date` を確認する。交差する変更の競合拒否は既存の `AutoInstall_ManagedIntersectionBusyPreservesNonIntersectingCommittedPackage` などが分担し、交差するのにPがない不変条件違反は入口の静的確認に分担する。 |
| 導入後の新曲フォルダ | `Lr2FolderFileDbSyncService`、`BMSLibrary.Lr2SynchronizationOwner` | `BmsLibraryLr2SongDbSyncTests.InstallPendingPackages_CreatesBuiltinNewSongRowOnlyForRecentAdditionWithoutExistingRow` が、期間内の追加での一件の作成、`titleflash` 0・既存行あり・組込みファイルなしでの非作成と既存行の不変、同期状態の保持を確認する。 |
| 自動推定後の導入を含む操作末尾の必要な同期と先行結果の保持 | `BMSLibrary.PackageInstall`、`Lr2SongDbSyncRequestCoordinator` | [`PackageInstallWorkflowOwnerTests.AutomaticContinuation_SynchronizesCommittedStagesAtTerminal`](../../../tests/BeMusicSeeker.Core.Tests/Install/PackageInstallWorkflowOwnerAutomaticContinuationTests.cs) は、後段の正常成功・初段後の推定の失敗/取消・後段の部分成功後の取消・推定と同期の失敗の重畳を、本番の受付・実際の調整処理・SQLiteの `song`/`folder` の出力と同期状態まで確認する。`AutomaticContinuation_ExcludesOnlyPlaylistBusyTargetWithoutReplay` は、Pの競合による必須の失敗・同期未完了の案内と先行結果の保持・自動再実行0を確認する。正常な行は、同じ初期化後の二回目の実際の取り込みで、`Completed` による省略後も正常な結果・エラーでない案内を維持する。`QueueLr2SongDbSync_LegacyCompletedSkipsWithoutStartingOrChangingStoredCompletion` は、保存済みの `Completed`・実行・DBの保持と、準備・新規要求0・LとPの解放を確認する。既存の [`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) / [`BmsLibraryLr2SongDbSyncTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) は同期の中核の条件を分担する。 |
| 差分インストールのTXTの保存と差分読込みへの接続 | [`Lr2TextGroupResolver`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2TextGroupResolver.cs)、`LibraryMutationSession.Commit` | [`BmsLibraryPackageInstallServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `EstimatedInstall_TextFlagMatchesFinalPlacementWithoutImmediateFileDiffReparse` は、既存の直下のTXT・後続の同梱TXT・子だけのTXTを分担し、実SQLiteの `txt` と導入後の現在値、LR2モードの `BmsAddedTargetCount=0`・`BmsTextOnlyUpdateCount=0` を確認する。[`BmsLibraryInitializationFileScanTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) の `ApplyFileScanDiff_NewBmsSetsTxtFromDirectTextGroupOnlyWhenLr2SongDbSyncEnabled` と `ApplyFileScanDiff_ExistingBmsTextGroupChangeUpdatesTxtOnlyWhenLr2SongDbSyncEnabled` は、新規の譜面・既存の譜面のTXTの変更と、同期モード別の差分の条件を担う。 |
| 両受付の開始競合、準備・書込み0と部分取得の解放 | `Lr2SongDbSyncRequestCoordinator` | [`QueueLr2SongDbSync_CompetingAdmissionSkipsBeforePreparationAndReleasesPartialLease`](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs): 実際の到達・結果・Taskの終端を確認し、保持点は `finally` で解放して全ての開始済みTaskを待つ。 |
| 差分の実際の譜面・通常フォルダの確定と、全体同期の競合による見送りの区別 | `FileDiffReloadWorkflowOwner` | [`DailyLibraryDiffAndIndependentPlaylistRegistration_CompleteActualChangesConcurrently`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs): 実際の到達・結果・Taskの終端を確認し、保持点は `finally` で解放して全ての開始済みTaskを待つ。 |
| 全原文のLR2評価、照合用の別名との分離、CP932の境界 | [`Lr2CompatibilityEvaluator`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2CompatibilityEvaluator.cs) | [`Lr2CompatibilityGoldenTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2CompatibilityGoldenTests.cs) は、原文の `.flac` / `.jpeg` の259・260バイト、同じキーの全原文、非空の空キー・種類不明、長いディレクトリで空の定義だけの最大相対長がnullとなる条件、実際のバイト列の復号不能と別の定義の長さを確認する。同期への接続と既存の健全性の保持は `QueueLr2SongDbSync_ProjectsLr2CompatibilityWarningsToLiveRows` が確認する。 |
| 共通受付の権限・寿命とBusy | [`ChartFileOperationSynchronizer`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartFileOperationSynchronizer.cs)、[`Lr2SongDbSyncRequestCoordinator`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncRequestCoordinator.cs) | [`ChartFileOperationSynchronizerTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/ChartFileOperationSynchronizerTests.cs) は、別の管理主体・解放済み・二重解放と借用を確認する。`BmsLibraryLr2SongDbSyncTests` は、準備から実処理の終端までのBusyと、成功・失敗・終了による取消を確認する。既存の準備境界の明示的な待合せを `finally` で解放し、開始済みの実際のTaskを待つ。 |
| 実際のLR2と試聴・設定の標準接続 | [`ApplicationComposition`](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs)、[`Lr2SongDbSyncWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) | [`ApplicationCompositionTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs) / [`ApplicationCompositionTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs) は、実行時の実際の準備・実処理の待機から、新規の試聴とライブラリに依存する局所確定のBusy、既存の試聴・候補の維持、無関係な通常項目の実際の保存を確認する。Busyの呼出し前後の対象値・所有するXML・再読込み回数を比較し、追加の副作用がないことを確認する。 |
| 同じ操作の設定入力と次回の捕捉 | [`Lr2SongDbSyncRequestCoordinator`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncRequestCoordinator.cs)、[`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`CustomFolderOutputSettingsSnapshotTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/CustomFolderOutputSettingsSnapshotTests.cs) は、捕捉Aの不変と次回のBを確認する。標準構成の `StandardComposition_Lr2UsesCapturedGenerationOptionAndAdoptsConfirmedOptionOnNextRequest` は、Aの捕捉後に独立した項目を保存してもAで実際に生成し、配置以外の生成フラグだけを変更して次の明示要求で更新することを確認する。出力配置の未確定の候補と保存後の公開は、標準構成の局所確定の試験に分担する。未送信の検知の生成オプションだけをA=trueからB=falseへ編集する場合は、現行性の対象を固定し、Aの未送信の定義とフォルダ行の実際の保存・`Completed`、次の要求での定義・行の除去と `Completed` を確認する。設定入力の一回の捕捉と明示的な受渡しは静的確認とし、現行性の確認のための再読込みは維持する。 |
| 標準のプレイリストの生存中の権限と保存結果 | [`ApplicationComposition`](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs) の `CreateBmsPlaylist` | [`ApplicationCompositionTests`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs) の `StandardComposition_PlaylistBindingGeneratesFileAndPersistsFolderUnderAcceptedLr2Capability`: 実際に生成したファイルとフォルダ行、内部の終端後も外側の受付を保持することを確認する。 |
| 受理済みの保守とLR2の非重複・実際の更新 | [`BMSLibrary.InstallableMaintenance`](../../../BeMusicSeeker/Models/Library/BMSLibrary.InstallableMaintenance.cs) | [`InstallableMaintenanceAdmissionTests`](../../../tests/BeMusicSeeker.Core.Tests/Startup/InstallableMaintenanceAdmissionTests.cs) は、実際の初期化の依存と、実際のスケジューラーの後続枠1を維持し、手動のLR2準備中に必須の保守が受付を待つ交差から、フォルダ表・保守表の両方の更新、全Taskとスケジューラーの終端、次の明示操作までを確認する。 |
| 準備面の合成・消費・実際の生成と保存の分担 | [`BMSLibrary.Lr2SynchronizationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs)、[`BmsLr2SongDbSyncWorkflowRuntime`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) | [`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) / [`BmsLibraryLr2SongDbSyncTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) の `PreparedSurface` と入力構築のケースは、既存の管理主体の境界で合成・消費・失効を確認する。実際の生成から保存は、Queueと標準構成で確認し、管理主体の代替実装が自作する準備順は上位の判定基準にしない。 |
| 受理済みのLR2継続の権限と実際のTaskの待機 | [`Lr2SongDbSyncWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs)、[`FileDiffReloadWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/FileDiffReloadWorkflowOwner.cs) | [`Lr2SongDbSyncWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncWorkflowOwnerTests.cs)、[`FileDiffReloadWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Startup/FileDiffReloadWorkflowOwnerTests.cs): 生存中の権限・理由・要求条件を明示的に転送し、保持したTaskの終端前に後段が止まること、元の失敗と継続の条件を確認する。 |
| 生成入力と走査なしの候補の構成 | [`Lr2SongDbSyncInputBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncInputBuilder.cs) | [`Lr2SongDbSyncInputBuilderTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncInputBuilderTests.cs) / [`Lr2SongDbSyncInputBuilderTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/Lr2SongDbSyncInputBuilderTests.cs) |
| CP932で表せないパスの投影の省略と同期の継続 | [`Lr2FolderTableReconciliationService.BuildProjection`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderTableReconciliationService.cs) | [`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) / [`BmsLibraryLr2SongDbSyncTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) の `QueueLr2SongDbSync_CompletesWhenChartDirectoryPathIsUnsupported`、`QueueLr2SongDbSync_SkipsUnsupportedLr2FolderFileNameAndContinues`、`QueueLr2SongDbSync_SkipsUnsupportedLr2FolderParentAndContinues`。 |
| 完全なフォルダ集合・衝突・一括確定 | [`Lr2FolderTableReconciliationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderTableReconciliationService.cs) | [`Lr2FolderTableReconciliationServiceTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2FolderTableReconciliationServiceTests.cs)、[`Lr2FolderRowGeneratorTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2FolderRowGeneratorTests.cs) |
| 同期状態と生成列の永続化 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs)、[`Lr2SongDbWriter`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbWriter.cs) | [`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) / [`BmsLibraryLr2SongDbSyncTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs)、[`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs)、[`Lr2SongDbWriterTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbWriterTests.cs) |
| 保存CRCの回帰とLR2側の独立計算 | `LR2CRC32`、`Lr2SongFolderParentNormalizer`、`Lr2FolderFileDbSyncService` | テスト側の `Lr2StoredPathReference` が、DBの `folder.path` をそのままCP932にしてNULだけを追加する、OpenLR2と同じ独立の方式で期待値を作る。固定値は `Lr2CompatibilityGoldenTests`、通常・ドライブ・UNCのカスタムフォルダと管理出力の必要な親は `Lr2FolderFileDbSyncServiceTests`、初回・差分は `BmsLibraryInitializationLr2NormalFolderTests`、移転は `BmsLibraryCatalogRelocationTests`、起動時の相対パスの補正は `BmsLibraryInitializationLoadTests` が分担する。 |
| 手動再同期のCRC・レベルの修復と現在値の即時反映 | `Lr2SongDbSyncRequestCoordinator`、`Lr2SongDbSyncService` | `BmsLibraryLr2SongDbSyncTests.QueueLr2SongDbSync_RunsLr2SongDbSyncAndMarksCompletedWhenClean` は、旧い `Incomplete` からの先頭実行と、`Completed` からの手動の強制要求によるCRC・レベルの修復、利用者列の保持、再読込みを確認する。解析器の版21での再解析と、現行の版22の詳細キャッシュの両経路で、UI通知の排出前にDBと現在値の `Level=3`・`LevelText="3"` を確認し、実際の読取りでの再読込みでも3を使うことを観測する。`SyncService_UsesResolverFactsAndReadsEachSongRowSnapshotOnce` は、両経路の確定値の引渡しと一回の読取りを確認する。`CurrentProjection_LevelOnlyCommitReachesCommonRefreshPublisher` は、詳細・警告に変化がなくても共通の配送に変更した譜面が入ることを確認する。 |
| 操作内の確定結果の直接引渡し | [`LibraryFileInitializationResult`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/LibraryFileInitializationResult.cs) | [`Lr2SongDbSyncCommittedInputTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncCommittedInputTests.cs) |
| 確定パスの投入前除外と混在した入力 | `Lr2SongDbSyncService.Run` | [`Lr2SongDbSyncCommittedInputTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncCommittedInputTests.cs) の `CommittedInputSongRowsSkipReaderAndCurrentnessRead` は、大小文字の比較、入力外の確定パス、別パスの同一MD5、基本入力の非採用を分担し、読取り・保存・入口の集計・対象数と確定位置を確認する。 |
| 全件除外と元の入力0件でのフォルダ・進捗 | `Lr2SongDbSyncService.Run`、`Lr2SongDbSyncStatusMapper` | `CommittedInputSongRows_ExcludeAllAndKeepFoldersWithoutSongStages` は、フォルダありの全件除外で、譜面の読取り・解析・パイプライン・チャンク・譜面の通知がないことと、全 `ChartPaths` からの祖先・フォルダ、最終検証、保存行・利用者列、`Running`・完了時刻の未設定を確認する。`SyncService_EmptyInputOmitsCountedStages` は、元の入力0件でのフォルダの有無を分担する。 |
| 除外しない対象のチャンク境界と順序の枠の超過 | `Lr2SongDbSyncService.UpsertSongRows` | [`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_RealSongUpdatesKeepFiniteRoleAcrossChunkCommits` は、5件・7件とチャンク幅2で、連続したチャンクの位置・順序回収・末尾・全保存・利用者列・確定位置を確認する。[Performanceの `ProductionDefaults_1000RowChunksPersist10001Rows`](../../../tests/BeMusicSeeker.Performance.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) は、処理幅を指定せずに10,001件を更新し、本番の既定の1,000行による11回の確定と同じ保存の保証を担当する。取消・必須反映の故障は既存の実更新のケースが分担する。 |
| 実際のファイル確定から0件の同期の実終端・失敗まで | `StartupLibraryInitializationWorkflowOwner`、`Lr2SongDbSyncRequestCoordinator` | [`StartupRequiredInitializationTests`](../../../tests/BeMusicSeeker.Core.Tests/Startup/StartupRequiredInitializationTests.cs) の `RequiredInitialization_HoldsBothAdmissionsUntilLr2TerminalAndWritesSavedOutputs` は、実際のファイルの確定・直接の入力・同じ親のLとPから、実際の調整処理・SQLiteのフォルダと完了の保存までを確認する。実際に生成した状態を表示用の変換と進捗の集約へ渡し、架空の譜面の表示を防いで、最終検証・保存の不定の進捗を維持する。`BmsLibraryLr2SongDbSyncTests.ReloadFileDiff_PassesCommittedInputAfterCapturedSurfaceFilesystemChanges` は、捕捉後のファイルシステムの変更でも直接の確定入力を維持し、全件除外の成功、不完全なフォルダ入力、完了の保存のDB失敗を実終端まで確認する。 |
| 採番と順序の枠の所有権 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) の `UpsertSongRows` | 採番より前の枠の取得、全ての退出経路の返却条件、番号順の回収時の返却を静的に確認する。境界件数のテストでは本番のCPU数に応じた並列度を使い、特定のスレッドの切替順を強制しない。 |
| 実際の確定後の必須引渡しと任意通知の故障の分離 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) | [`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_RequiredCommittedCallbackFailureKeepsDurableFactsAndCursor` は、正常な譜面の実際の解析で詳細・互換性の事実を確定し、両方の引渡しの失敗を分担する。進捗・性能診断も失敗する条件で、元のスタック、保存行・利用者列・確定した処理位置を保持することを確認する。他の必須引渡しも試み、確定済みのDBを巻き戻さず未完了とする。巻戻し・確定時の取消で未確定の事実0、先頭部分の取消で先行1,000件だけの引渡しは、既存の同クラスのケースへ集約する。 |
| 所有する取消・所有しない取消と必須引渡しの故障の競合 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) の `UpsertSongRows` | [`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_PipelineCancellationRacePreservesGenuineCauseAndCommittedPrefix` は、正常な先行・末尾の譜面から先行分の詳細を実際に生成し、所有する取消の後の詳細・互換性の引渡しの故障と、管理主体の取消より前に先着する読取り側の取消例外の3条件を確認する。後者は、後発の終了要求・詳細の引渡しの故障があっても、先着した原因のメッセージ・トークン・スタックを保持する。各条件で、全ての必須引渡しを一回試み、全ての読取り・引渡しの終端、`Failed`、確定済みのチャンク・利用者列・処理位置、未確定の末尾と未完了の状態の保持、確定後の巻戻しがないことを確認する。終了要求のフラグだけが先着する条件は含まない。 |
| 終了要求のフラグとトークンの取消の順序、チャンク全体の巻戻し | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) の `UpsertSongRows` | [`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_ShutdownCancellationAtSongCommitRollsBackWholeChunk` は、起動時と同じ既設のスキーマへの詳細の保存後の終了要求について、トークンも即時に取り消す条件と、フラグだけの条件を分担する。要求トークン付きの取消例外、楽曲・詳細の行の巻戻し、トランザクションの終端、確定した処理位置の不変、完了時刻・`Failed`・`Completed` の保存と必須の確定の引渡しがないことを確認する。フラグだけの条件では、返却時も要求トークンが未取消であることを確認する。 |
| 終了による取消後の代替の巻戻し・未解決の状態と、確定位置の終端通知 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) | [`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_ShutdownRollbackDistinguishesSafeFallbackFromUnresolvedTransaction` は、管理APIだけの巻戻しの失敗と両経路の失敗を即時のトークンの取消で、管理APIだけの失敗を終了要求のフラグだけで確認する。直接SQLによる巻戻しの成功では取消を保ち、最後の進捗が実際の確定位置・全体の件数・`song_rows`・段階件数0/0と安全なトランザクションの終端に一致する。フラグだけの条件は任意通知も失敗させ、未取消のトークン付きの元の例外とDBの結果の保持を確認する。両方の巻戻しの失敗は、実失敗として原因を診断する。全ての条件で、確定済みのチャンク・利用者列・処理位置と、未確定の末尾・未完了の状態を保持する。両方の巻戻しの失敗とフラグだけの組合せは含まない。 |
| 必須の終端投影・状態の保存と所有する終了の競合 | [`Lr2SongDbSyncRequestCoordinator`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncRequestCoordinator.cs)、[`BMSLibrary.Lr2SynchronizationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.Lr2SynchronizationOwner.cs) | [`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) / [`BmsLibraryLr2SongDbSyncTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) の `QueueLr2SongDbSync_CompletionWaitsForRequiredProjectionAndOwnerCheckpoint` は、正常な二譜面の詳細を実際に生成・確定し、投影の待機中の `Running`・確定した行・利用者列・処理位置と両方の受付を保持する。投影の成功は `Completed`、通常の完了側で部分的な投影の後に終了要求を出してから投影が失敗する条件と、終了による取消の捕捉側で投影が失敗する条件は `Failed`、投影の成功後の所有する終了は `Incomplete` を確認する。`Failed` の保存だけが失敗する場合は、保存先の `Running` と元の投影の原因・公開した `Failed` を保つ。先行した索引と一部の現在値・互換性の投影を巻き戻さず、投影を再試行しないことと、要求・両方の受付の終端を確認する。現在値の投影も含めて分担し、楽曲行の処理内の取消の競合とは別の、終端投影の保証である。 |
| 独立した詳細の失敗の非恒久化と、基本行・順序の保持 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) と共通の詳細の評価処理 | [`Lr2SongDbSyncServiceTests.SyncService_UnknownDetailFailurePreservesBasicUserValuesAndOrderedCompletion`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) は、実SQLiteで、詳細が失敗した対象の基本行・利用者列、正常な対象の詳細、元の失敗行を作らないこと、順序番号に欠落がない完了と、開始済みの健全な処理の終端を確認する。 |
| 独立した基本入力の未知の失敗と、次の通常の機会 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) の読取り・ハッシュ・基本解析 | [`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_UnknownBasicReadFailurePreservesOldRowAndCompletesNormalSiblings` は、読取りが失敗した対象の旧い行・利用者列、正常な別の対象の実際の解析・保存、順序の位置、恒久的な失敗行を作らないこと、開始済みの処理の終端と次の要求での再評価を確認する。ハッシュ・解析の捕捉範囲と、保存・必須反映の分離は静的確認、既知の入力の互換性は既存の解析テストに分担する。 |
| 要求の取消と開始済みの処理の終端 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) の `UpsertSongRows` | [`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_RequestCancellationPreservesCommittedPrefixAndJoinsWorkers` は、CPU数によらず要求の取消を伝播し、確定済みのチャンク・利用者列・処理位置、解析失敗の行を作らないこと、全ての開始済みの処理の終端を確認する。独立した基本入力の失敗による対象のスキップとは分ける。 |
| 局所のBMS範囲・祖先・完全一致キー | [`Lr2NormalFolderSyncScopeBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2NormalFolderSyncScopeBuilder.cs)、[`Lr2NormalFolderDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2NormalFolderDbSyncService.cs) | [`Lr2NormalFolderSyncScopeBuilderTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2NormalFolderSyncScopeBuilderTests.cs)、[`Lr2NormalFolderDbSyncServiceTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2NormalFolderDbSyncServiceTests.cs)、[`BmsLibraryFolderRenameRefreshTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryFolderRenameRefreshTests.cs) |
| 大小文字だけの改名でのフォルダ行の移転 | フォルダ改名の変更セッションと、LR2の通常フォルダ同期（`Lr2NormalFolderDbSyncService`） | [`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) の `RenameChartFolder_MovesNormalFolderRowsForMovedBmsWhenLr2SongDbSyncEnabled` は、手動・自動の大小文字だけの改名の条件で、実際の列挙名と保存結果を確認する。同じテストの他の条件は、通常の改名・ルートの改名と追加時刻 `NULL` の保持を分担する。 |

## 関連資料

[一括再構成の設計判断](../../decisions/lr2-song-db-one-shot-reconciliation.md)、[索引](../core/data-and-indexes.md)、[プレイリスト出力](../playlist/storage-and-export.md)を参照します。
