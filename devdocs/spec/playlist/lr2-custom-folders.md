# プレイリストのLR2カスタムフォルダ出力

## 目的と適用範囲

プレイリストから `.lr2folder` と対応するLR2のフォルダ行を生成する契約を定めます。正本のDB保存とは別の処理で、LR2連携モードだけが実ファイルを出力します。

## 用語

[共通用語集](../glossary.md)に従います。

## 仕様

設定画面での候補の編集、破棄の確認、局所確定の受付と処理の寿命は[設定](../runtime/settings.md)を正本とします。本書は、確定した配置を入力とする配置検査、出力、旧出力の整理を定めます。

### 出力先と事前検査

出力配置は、起動時の保存値から初期化する小さな不変入力として扱います。`user.config`・必要なLR2のXML・表ヘッダーDBの保存が完了するまで新しい配置を公開せず、未保存の編集内容は通常出力・交差判定・全体同期の入力や最新性判定へ反映しません。古いプロパティ画面から保存するときも、局所の受付後に現行の配置と正本の対象へ固定します。保存失敗・部分保存・公開後失敗は[設定仕様](../runtime/settings.md)に従います。

設定画面の通常・ROOTの選択と、追加先の追加・削除・改名は、一回の操作で一つの区分だけを確定し、他の区分は最新の確定配置を使います。明示的な再操作は最新の入力を再保存・再生成し、過去の要求や成功した段階は保持しません。追加先の改名による旧名から新名への対応は、その操作の終端で破棄します。保存に失敗した後の追加先の候補は、公開済みの配置から作り直します。候補の保持・破棄の確認と処理の寿命は[設定の競合する候補の切替](../runtime/settings.md#競合する候補の切替)に従います。

既定の通常出力先は `LR2CustomFolderOutputBaseDir`、追加先は `LR2CustomFolderAdditionalOutputBaseDirs`、ルート型は `LR2CustomFolderOutputBaseDirRootType` です。追加先はJSON文字列配列でフルパスを保存し、表示とDBの選択値には末尾のディレクトリ名を使います。

追加先の改名は、確定時に前後の空白を除去し、内部の空白を残した末尾の名前を、追加先のパス・保存値・参照する表の名前に揃えます。空白だけの名前と `.` は拒否します。入力中の候補の保持、通常の閉鎖での破棄、確定済みの名前と同値の改名の扱いは[設定の追加出力先の改名](../runtime/settings.md#追加出力先の改名)に従います。

表の `custom_folder_output_base_name` がNULLなら既定先、登録済み名なら追加先を選びます。通常型で使う保存名が設定にない場合は、通常先へ読み替えず該当表の出力を止めて知らせます。既存データも対象とし、元の保存名を保持・表示し、表の保存先再選択で解消します。未登録名からの再選択では旧先を推測せず、新先だけを生成し、通常先にある無関係なファイル・管理行を保ちます。設定画面で追加先を削除した場合は参照をNULLへ、改名した場合は新名へ更新します。ルート型の間も通常先の選択値は保持し、実効出力先だけルート型を優先します。追加先名が未登録でもROOT出力は続け、通常型へ戻した時にその名前を使う場合は出力を止めます。

`output_dir` はパスを含まない単一のフォルダ名です。新しい明示入力では、相対・絶対パス、ドライブ付き指定、区切り、`.`・`..` をサニタイズや同値判定より前に拒否します。通常・追加・ROOT・スタンドアロンで入力条件は共通です。空欄・空白・NULLは既定名指定とし、trim・Shift_JIS互換化・無効名文字除去後の名前が表名由来の既定名と同じならDBではNULLとします。通常型は選択先の下、ルート型はルート型基点の下へこの名前のディレクトリを作ります。

既存保存値は文字列全体を一つの名前として整形し、`TableName\sub`・`TableName/sub` は `TableNamesub` として表示・配置・出力します。`.`・`..` をディレクトリ移動として解釈せず、整形結果が空なら既定名を使います。読取りや表示だけでは保存原値を書き換えません。別列編集で既存値を無変更で引き継ぐ保存と、DB読取り・SQL復元は拒否しません。出力名を手入力する入口では、元値と同じパス文字列の再入力も拒否します。旧階層の物理ファイルを自動移動・削除しません。アプリ生成の内部階層やBMS検索ルートはこの名前入力と区別します。

外部表の単表・一括登録では、実効出力名が空なら捕捉済みLR2連携モードでだけ拒否します。Unicode実名に由来する空の出力名を許可するスタンドアロンの保存・取込みは[プレイリスト保存](storage-and-export.md#外部取得と公開の境界)に従います。

新規draftは登録前の旧配置を持たず、初回保存では選択した通常・追加・ROOT先へ既存の出力管理主体で生成します。旧先整理は既存配置からの移行だけに適用し、共通基点や兄弟表のファイル・LR2行を保持します。ROOTの実効先はXMLへ保存し、失敗をメモリ内登録だけで成功扱いにしません。初回出力の失敗後も登録済みIDの未完了出力を同じ捕捉設定で明示継続します。空表の生成対象と空ディレクトリ整理は通常の出力規則に従います。

起動・差分更新・初期化の前には全出力基点を検査し、BMSルートと同じ読取り（ディレクトリであること、直下を列挙できること）を要求します。検査は基点とその配下へファイルを作成・書込み・削除せず、内容と更新日時を変えません。基点の更新日時は `folder` 行の `date` と一致させる対象であり、変化のない起動で書き換えないためです。書込みできるかは事前に検査せず、実際に `.lr2folder` を出力する時点の失敗として扱います。使えない基点を自動作成して成功扱いにしません。配下の表ディレクトリや出力ファイルがまだないことは失敗ではありません。スタンドアロンではLR2出力先の残存設定で起動を止めません。

設定変更前に有効な導入先を今回の通常・追加・ROOT管理領域へ取り込む変更は、同一・包含親のどちらも保存・XML・実配置変更前に拒否します。導入先以外の既存登録の採用は維持し、先に別の確定BMSルートへ導入先を変更すれば再選択できます。未受理候補は確定値へ戻し、再操作は今回入力と他項目の確定値から処理します。初回・既存不正状態の段階保存と比較方法の正本は[設定の導入先保護](../runtime/settings.md#カスタムフォルダと検索対象)です。

出力基点同士の同一・親子の配置は拒否します。既存の `jukebox` 登録に対する配置は[既存登録との配置可否](#既存登録との配置可否)に従います。既存登録を管理領域として採用する場合の保存時確認、旧管理領域の保護、検索対象の区別は[設定](../runtime/settings.md#カスタムフォルダと検索対象)を正本とします。

管理表同士の実効絶対パスは、同一・親子のどちらも許可しません。単一名化後の実効絶対パスを、大文字小文字・区切り・相対要素の表記差を正規化して比較し、名前・出力名・基点選択・ルート型・基点設定の変更後集合を副作用前に検査します。一括変更は対象同士と未選択表を含めます。外部表の新規登録も既存表と全候補の変更後集合を検査し、一括登録に衝突があれば全件を登録前に拒否します。`Parent` と `Parent2`、兄弟、重ならない別基点は許可します。スタンドアロンにはこのLR2配置検査を適用しません。

通常・ROOT・追加通常のいずれも、既存内容のある表出力先を受理します。管理範囲は現在の表と確定した設定による実効出力ディレクトリで決まり、別環境の残置物や手作業のファイルがあること、非空であることを理由に追加確認・拒否・別先への振替を行いません。既存の表間衝突・組込み予約領域等の配置条件は維持します。

保存済み値の単一名化後に衝突・包含する場合も出力・移行・出力削除・表削除・検索ルート同期を拒否し、DB・正本・ファイル・検索ルートを変更しません。自動改名・設定修復・包含を保護しながらの出力は行いません。外部情報初期化の対象別除外は[プロパティ編集](../ui/playlist-properties.md#サマリーからの一括編集)に従います。

確定したLR2rootの `LR2files/CustomFolder` は組込みの予約領域です。通常・追加・ROOTの出力基点は組込み自身・配下・直上の `LR2files` を拒否し、LR2rootやdrive rootなど、それより上の祖先は許可します。明示BMSrootは組込み自身・配下だけを拒否します。安全な兄弟、無関係な同名、似たprefixは許可し、LR2root不明時に名前だけから推測しません。検査は起動、出力設定、LR2root・詳細設定、明示BMS登録の受理時に各一回行います。検査済みの出力基点と単一出力名を使う表編集・一括編集・移行・再出力・同期では組込みを再検査せず、表間出力衝突など既存の検査を維持します。表実効先が組込みを含む祖先となる場合の保護は保証しません。出力ディレクトリは専用にし、重要なデータを同居させないでください。

### jukeboxとの同期

#### 既存登録との配置可否

既存の `jukebox` 登録に対しては、次の配置だけを許可します。比較には、表示用の一覧で除外する前の登録を使い、現在存在しないパスも残して、正規化したパスの関係を検証します。

| 指定する出力先と既存登録の関係 | 通常出力先・追加通常出力先 | ルートフォルダ出力先 |
| --- | --- | --- |
| 同一 | 許可 | 許可 |
| 出力先が既存登録の親 | 禁止 | 配下の登録を復元・整理するため許可 |
| 出力先が既存登録の子 | 禁止 | 禁止 |
| 同一でも親子でもない | 許可 | 許可 |

保存済みの追加・ルート出力先を通常出力先へ転用する操作は、編集の後に追加・ルート側を変更または削除していても、同一・親子の関係を拒否します。登録同士の親子の重複も設定の不備として検出し、通常・追加出力先のために親の登録を外して子へ置き換える自動補正は行いません。

#### 登録の補完と整理

通常・追加先は利用者設定を正本として `jukebox` に不足分を補い、同一登録は保持します。両者の同期・起動時修復は共通の配置検証を使い、全候補を検証してからディレクトリ作成・登録追加へ進みます。利用者登録の親・子を指定した場合は設定不備として拒否します。今回除去が確定した旧管理exact登録だけを除いた最終候補集合で新先を検査し、移行中は必要な旧新登録を保持します。不正な出力先を黙って省略しません。

ルート型は基点そのものではなく、各表の実効出力ディレクトリをルートとして扱います。既存登録と同じ基点、または既存の表ルートを含む親を指定でき、子ディレクトリが未生成なら必要に応じて作成します。保存時と起動時は基点・不要な旧子登録を現在の表ディレクトリへ揃えますが、基点の外側にある親登録は削除対象にせず、基点がその子となる設定を拒否します。起動時の完全な整理は表一覧を読み込んだ後に行います。`ReloadTables` は不足する表ルートを補い、不要ルートの完全な整理は保存時・起動時が担当します。

### 出力除外と種類

`ignore_folder_output` は出力しない種類を表すビットです。既存値に新しい種類のビットがなければ、その種類は出力します。旧版の全除外相当を現在の全除外値へ拡張しません。`entry_type=Folder` では保存値に関係なくレベル別出力を無効にします。

新しい通常表は `PlaylistDefaultIgnoreFolderOutput`、推定表とおすすめ表は専用の固定値を使います。既存表の外部同期では保存済みの値を保持します。

| 種別 | bit | `PlaylistDefaultIgnoreFolderOutput` の既定 | 推定表 プレイリスト の既定 | おすすめ表 プレイリスト の既定 |
| --- | ---: | --- | --- | --- |
| 利用者フォルダ | `0x001` | 出力 | 出力 | 出力 |
| レベル | `0x002` | 出力しない | 出力 | 出力しない |
| 頭文字 | `0x004` | 出力しない | 出力しない | 出力しない |
| クリア | `0x008` | 出力 | 出力しない | 出力しない |
| DJレベル | `0x010` | 出力 | 出力しない | 出力しない |
| 譜面特性 | `0x020` | 出力しない | 出力しない | 出力しない |
| その他 | `0x040` | 出力しない | 出力しない | 出力しない |
| ランダム | `0x080` | 出力 | 出力しない | 出力しない |
| BPM順 | `0x100` | 出力 | 出力しない | 出力しない |
| BP順 | `0x200` | 出力 | 出力しない | 出力しない |
| プレイ回数順 | `0x400` | 出力 | 出力しない | 出力しない |
| 最終プレイ順 | `0x800` | 出力 | 出力しない | 出力しない |
| 全曲 | `0x1000` | 出力 | 出力 | 出力 |

通常の既定除外は `Level | Alphabet | CategoryAll | Other` です。`AllFolders=0x1FFF` は全出力を禁止する値です。

全曲と利用者フォルダが対象範囲を選び、クリア、DJレベル、BPM順、BP順、プレイ回数順、最終プレイ順はその範囲ごとに出力します。レベル、頭文字、譜面特性、その他は独立した全体分類です。

| 実装上の種類 | 生成内容・条件 |
| --- | --- |
| `AllSongsFolder` / `UserFolder` | 全体の「表名 ALL」と各フォルダ。削除済み譜面を除き、空のフォルダ名は表名を表示する。 |
| `LevelFolder` | レベルを切り捨てた整数ごとの `LEVEL n` とNULL用の `LEVEL ???`。整数はInvariantのASCII表記で、名前とSQLの両方に使い、通常版とRANDOM版の選曲集合を一致させる。 |
| `AlphabetFolder` | `A.B.C.D.` から `U.V.W.X.Y.Z.` までの6区間と `OTHERS`。 |
| `ClearFolder` | `CLEAR FOLDER` 配下に番号付きのNO PLAY、FAILED、ASSIST、EASY、CLEAR、HARD、FC、P.A。タイトルには番号を付けない。NO PLAYはscore行なしまたはclear=0とする。LR2のclear=2をEASYビットでASSIST/EASYに分け、FCはP.Aビットを持たないものだけとする。 |
| `DJLevelFolder` | `DJ LEVEL` 配下のAAA、AA、A、UNDER A。A未満とスコアなしを最後の区分へまとめる。 |
| `CategoryAllFolder` | `ALL LONG NOTES` と判定種類別。 |
| `OtherFolder` | `MY BEST`、`NEW SONGS`、`REMOVED SONGS`。IR取得・未送信検知が有効なときは `UNSENT SONGS`。 |
| `RandomFolder` | 対象範囲・レベル・クリア・DJレベルの各出力にランダム版を追加する。`ORDER BY random()` と `#MAXTRACKS 1` の両方が必要。 |
| `BpmSortFolder` | `chart_info.mainbpm` の昇順、欠損は末尾。パーサー版を条件にしない。 |
| `BpSortFolder` | 非負の `score.minbp` の昇順、詳細未保存の負値・NULL・スコアなしは末尾。 |
| `PlayCountSortFolder` | `score.playcount` の降順、未プレイ・欠損は末尾。 |
| `LastPlaySortFolder` | `bms_lr2_last_play.last_play_at` の降順、欠損は末尾。他の日時へ代用しない。 |

地域設定の負号で壊れた旧版のLEVEL出力は、該当表の内容・出力設定を明示的に保存したときに再出力して修復します。強制的な再出力は行いません。

最終プレイ順は履歴スキーマ未導入でもファイルを生成しますが、LR2側で開くには対象表が必要です。SQLで現在の履歴を参照するため、プレイごとの再出力は不要です。

BP順と `CLEAR FOLDER` の通常版は、0を含む非負のBPを昇順に並べ、負値・NULL・スコアなしを欠損として末尾へ置きます。欠損同士の順は保証しません。クリア分類はBP欠損と独立し、詳細未保存のASSISTをNO PLAYへ移しません。DJレベルの `rate` とプレイ回数の降順では負値も数値として扱い、BP順のために保存済みスコアや詳細解析を変更しません。

このBP順は新規生成と既存表の明示再出力で反映します。生成済みファイルの自動一斉更新は追加しません。LR2全体同期のCompleted判定からも独立した出力契約です。

### ファイルとSQL

管理 `.lr2folder` はDBを正本とする再生成可能な出力です。大量生成と差分出力の速度を維持するため直接書込みを使い、書込み失敗時の旧本文保全は保証しません。全ファイルの原子的保存や全本文の再検証は行いません。利用者が保存するJSONの保証は[JSON出力](storage-and-export.md#json出力sqlバックアップと復元)に従います。

各相対ディレクトリで `0000.lr2folder` から採番し、Shift_JISで出力します。通常版を先に、ランダム版をその後に並べます。本文は `#COMMAND`、`#MAXTRACKS`、`#CATEGORY`、`#TITLE`、`#INFORMATION_A`、`#INFORMATION_B` を持ちます。カテゴリは表名、タイトルは表示フォルダ名です。

確定プレイリストと `Lr2FolderFileDefinition` を正本とし、同じ型定義から本文と内部folder行を派生させます。本文の項目順・改行・Shift_JISを維持し、DB行はディレクティブ値の端だけを従来のTrim相当で整えます。SQL内部の空白は変更しません。常に出す項目のnullは空文字、未出現のSubtitle・Banner等はnullです。生成本文の再parseや別編集できるText正本は保持しません。管理外と組込みの物理parse・特殊type・相対pathは維持します。

コマンドはLR2/OpenLR2が `song LEFT JOIN score ON song.hash=score.hash` に適用できるWHERE句断片です。ファイル単位の表は所属MD5へ絞り、フォルダ単位の表は所属MD5から得た `song.folder` へ広げます。`playlist_entry`、`chart_info`、必要な `ir_score` と履歴表も参照します。

所属範囲の指定は `null` だけを全所属（ALL）とし、非nullは空文字・受理済み空白名を含む実所属として扱います。LEVEL/ADDDATEのソート値も抽出と同じ所属範囲から参照し、同じhashの別所属にあるlevel/adddateを混ぜません。空文字・空白名の保存値は保持し、表示上の表名への置換とは区別します。ALLで同じhashが複数所属にある場合の代表値、スキーマ・索引・ランダムの規則は変更しません。

この所属内ソートは新規生成、および既存表の内容・設定を変更保存する等の明示再出力で反映します。通常起動や手動の全体同期だけで、既存ファイルの旧COMMANDが自動更新されることは保証しません。 全体同期のDB投影は今回の確定型定義を使い、準備後の同mtime本文変更をDBへ混入させません。OpenLR2自身が物理Shift_JIS本文を再parseする場合の文字置換は保証外です。一斉強制再生成や通常起動の毎回全COMMAND生成・本文比較は追加しません。

最終プレイ順は次の固定SQLを使います。

```sql
ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC,
         (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC
```

管理・管理外双方の必要祖先を含め、全準備と事前検査が成功してから `folder` 行を一括反映します。`date` は実際の更新時刻、既存 `adddate` は保持し、新規だけ生成時刻を使います。通常先→表ディレクトリ→連番ファイルという階層を保ち、ルート型は各表ディレクトリだけをLR2ルート直下へ置きます。階層を平坦化しません。CRC32や表の列は[楽曲DB同期](../integration/lr2-song-db.md)を参照します。

### 管理領域の整理

表の実効出力ディレクトリとその配下は、通常・ROOT・追加通常とも管理領域です。ファイルの作成元によらず現在の表定義で上書きし、生成時には配下も含めて期待集合にない `.lr2folder` を削除します。出力先変更、ルート切替、表削除では旧表ディレクトリ全体と対応範囲のDB行を整理します。整理対象を基点全体や無関係な兄弟項目へ広げません。別表の包含に備えた子孫保護は持ちません。同一表の旧親から新子への移行、旧パスの再利用、管理基点外の保全は移行整理の別の保証として維持します。通常基点と追加基点を同じ設定要求で変更するときは、承認した現在名から最終登録名への対応を移行準備へ明示して渡し、未改名の表を含む配置検査・候補出力・整理時の保全先を同じ最終配置で解決します。

単表移行では旧新が同一か別先かによらず、今回の生成対象をすべて必須書込みにします。宛先に同名ファイルと更新時刻があっても、旧本文を今回の定義の反映済みとは扱いません。既存の生成・DB一括同期・出力状態保存・旧先整理に接続し、新先を丸ごと空にする前処理は加えません。日常更新・起動時の必要出力判定と、管理領域内の一般ファイルを保護対象にしない契約は維持します。

既存の禁止設定は、プロファイルの構築前に設定を訂正する経路へ戻します。構築前でも、通常・ROOTの選択と、追加先の追加・改名・削除は、当該区分だけを保存できます。追加先の旧禁止集合は一件ずつ縮小できます。未設定または旧予約領域の区分は今回の安全な訂正を妨げず、有効な既存基点の一般的な配置衝突と旧配置の警告は維持します。旧組込み領域の自身・配下の物理配置、旧範囲、旧出力先は、移行・削除・DB行の整理へ渡しません。通常・追加の変更区分では既知旧管理exact登録だけを除き、ROOTでは現在XMLに実在するbuiltin自身・配下のexact登録だけを除きます。祖先・drive・無関係な未知登録を推測して整理せず、上位祖先を特別に保護・除去しません。全体の起動受理が成立するまではプロファイルとバックエンドを公開せず、旧ファイルを保持します。

ROOT基点がドライブルート・UNC共有ルートの場合も、整理対象は基点直下の当該表ディレクトリです。基点全体を削除対象へ広げません。[一般BMS一覧・導入先からの除外](../runtime/settings.md#カスタムフォルダと検索対象)は基点全体と子孫を対象とする別の契約です。

SQL復元では、LR2連携のとき、DB置換前の実効の絶対配置・基点・ROOT種別を、その復元だけのために捕捉します。新しい正本の必須生成が成功した後に、同じ受付の中で旧配置と対応する `folder` 行を整理してから、必須準備を完了します。

- 旧DBに所有表・出力状態がなく、出力先に残置ファイルだけがある場合も、復元した表の定義で必須生成します。空の出力先への変更や事前の退避を前提にしません。
- 空の表集合への復元でも旧配置を整理します。
- 復元前後の表IDで対応を決めず、新しい配置の集合全体を保全します。別の表による旧パスの再利用、旧親から新子への配置、旧ROOTから通常・追加先への復元でも、新しい出力を保持します。
- 同じ配置では、無効にした種類を含む不要な `.lr2folder` と対応する行を期待集合へ揃え、通常のファイルは保持します。管理基点の外と、基点直下の他の物は整理しません。

新しい必須生成の失敗は準備の失敗として伝播し、旧配置を整理しません。新しい正本と必須出力が成立した後に、旧配置のファイル削除またはDB行の削除だけが失敗した場合は、残存の警告とし、新DB・新出力・旧残余と利用可能性を保持します。自動の再試行や、次の再出力への整理状態の持越しは行いません。スタンドアロンのSQL保存・復元は、既存の `.lr2folder` と全 `folder` 行を変更しません。復元の確定・失敗の境界は[SQL復元](storage-and-export.md#sql復元)に従います。

### 修復と一括反映

設定変更後の生成失敗で設定を旧値へ戻しません。現在の設定と保存済み表headerから生成し直し、旧先残存の整理だけを理由に後続設定を制限しません。設定の再操作に過去の移行段階は持ち越しません。追加先の改名・削除は不変なheader更新をDBへ保存してから表メモリへ公開し、DB保存失敗を生成物の残存警告へ格下げしません。

起動時は通常・追加の不足検索登録を補い、表header読取り後にROOTの表別登録を補います。LR2モードの出力対象について、現在配置の署名・物理不足・管理行等の既存判定に従って再生成します。過去の改名意図やDBに保存できなかった削除を再現する機構ではありません。

出力状態がない、入力が変わった、物理ファイルが欠落した、ファイル・表ディレクトリ・その配下でファイルの祖先となるディレクトリの更新時刻が変わった場合は再確認対象にします。出力状態は表ディレクトリとその配下だけを表し、出力基点の更新時刻を含みません。再確認対象の表だけ正本から軽量な期待集合を作り、ファイル行・ディレクトリ行の欠落、日時・親の不一致、余分な行を調べます。この照合は本文の定義を生成せず、配置と行だけを比べます。本文の差は検出できないため、出力状態がない表と入力が変わった表は全ファイルを書き直します。ファイル・ディレクトリの更新時刻や祖先ディレクトリの行だけが理由の再確認では、行が期待どおりのファイルを書き直しません。列挙基盤が使えない場合は再作成対象にし、全ファイルの個別存在確認を通常経路にしません。

状態が現行ならDB行だけの外部編集を常時監査しません。例外は、次のルート型表のディレクトリ行と、通常・追加通常出力先の祖先ディレクトリ行の二つです。jukeboxへルート型表を補完した直後は、表ディレクトリ自身の行を完全一致パスで確認します。末尾区切り付きパス、`type=1`、`parent=e2977170` に限り、不一致ならその表を再確認対象へ戻します。

通常・追加通常出力先の表では、状態が現行でも、必要出力を判定するたびに、表ディレクトリの親から出力基点までの祖先ディレクトリ（通常は基点そのもの）の行を完全一致パスで確認します。行が存在し、`date` が祖先の実際の更新時刻（Unix秒）と異なる場合は、その祖先の配下にある表を再確認対象へ戻し、既存の再確認・再生成・一回のDB同期で `date` を実際の時刻へ揃えます。追加通常出力先はBMSの走査対象外で、通常フォルダ同期では基点の `date` が追従しないためです。通常出力先も、起動時の走査を行わない場合は同じです。行がない場合、祖先の更新時刻を取得できない場合、行の照会に失敗した場合は、この確認では再確認対象へ戻しません。ROOT型の表は基点自身の行を持たないため対象外です。祖先の行は配下の表の出力ファイルから生成するため、配下の表がどれも出力ファイルを持たない基点では、この再確認で `date` を揃えられず、ファイルを書き込まない再確認だけが判定のたびに繰り返されます。不一致がない通常時に一回の判定で増える処理は、基点数ぶんの更新時刻の取得と完全一致の照会一回です。

設定画面からの手動再同期は、確定した環境を使います。対象を変える環境候補の破棄の確認、受付、画面の閉鎖と処理の寿命は[設定の補助操作](../runtime/settings.md#補助操作の対象受付閉鎖)に従います。受付の通知が失敗した場合も、両方の受付を解放し、準備・保存へ進まずに元の失敗を伝播します。

欠落表や手動再同期の全対象はまとめて生成し、物理ファイルの差分だけを書き、DB同期は一回で行います。表ごとに旧出力を消して同期を繰り返しません。生成開始、表の処理、物理反映、DB同期、状態保存まで進捗と診断を出します。

管理外の `.lr2folder` 探索は、管理表ディレクトリを列挙時の除外範囲として渡します。全件を候補化してから捨てません。保持した走査結果と現在の除外範囲が変わった場合は現在の範囲で探索し直し、出力前の古いファイル集合を出力後の同期に使いません。

同じ基点内でも、表出力範囲外の兄弟・似た接頭辞の別ディレクトリ・基点直下にある任意配置の `.lr2folder` は保持し、物理本文から `folder` 行へ同期します。同一・包含は正規化されたパスで判定し、表フォルダ内の生成予定外ファイルは管理内です。別環境の `Old` が残り、現在の表が `New` を使う間は `Old` を管理外として読み、現在の表の出力先を `Old` に変えた後は管理内として現在定義で生成・整理します。管理外本文の `COMMAND` が参照する表ID等は現在のDBで評価し、別環境の所属を移植・変換しません。

日常部分出力・全体生成・設定移行の受付条件と寿命は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。進捗は既存の最新状態通知キューを使い、購読先の例外は診断し、物理ファイル・DB・保存状態の結果と区別します。

基点・表ディレクトリ移行は捕捉済み旧先・新先を用いて処理します。新しい先の確定後に旧先削除が失敗しても、新しい先のファイル・DB・出力状態、旧先の残余と元警告を保持します。次の通常要求は新しい先を使い、古い編集画面の配置から旧先cleanupを再開しません。プレイリストのプロパティ編集における同じ編集セッションの明示保存継続は、保存結果に捕捉した旧先・新先の絶対パスと旧型・旧基点を使って未完了部分だけを先に処理します。設定画面の局所出力確定では、今回捕捉した旧先の絶対パスと旧型・旧基点を同じ操作の終端までだけ使い、現在正本から生成します。必要な設定媒体とheader DBを保存してから公開し、生成失敗・旧先残存のどちらも過去要求の再開情報として保持しません。正本保存・必要生成の失敗と、旧先整理だけの警告を区別します。次の明示操作はその時点の最新入力を再保存して生成します。

設定操作で新しい先の生成後に旧管理行の整理に失敗した場合は、旧出力先と原因を残存警告として知らせ、必要な外部フォルダ行の同期へ進みます。一般的な保存失敗だけに置き換えず、表プロパティ保存の未完了継続とは分けます。

起動時の保存済み表の必要出力は、最小カタログと必要な譜面情報・ハッシュの準備後に、親のLとPの中で直接待ちます。外部ネットワークとの同期は、親の受付を解放して操作を解禁した後に行います。全体のLR2出力が必要なら、修復と共通の生成・検証経路から完全な面を一度だけ返します。開始・更新・全終端は、空でない発生元と同じ親関係を持つ要求識別で通知します。カスタムフォルダの修復の後片付けにも同じ要求を渡します。BMT出力との共通契約は[BMT出力](bmt-export.md)に従います。

ROOT登録の補完・同期、表削除、プロパティ、一括編集は同じLR2設定所有者へ今回の登録差分を渡します。ディレクトリ準備・出力移行はXML保存排他外で行い、保存失敗は正本へ採用しません。公開では別セクションの部分保存を巻き込みません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 出力基点の読取りだけの事前検査と、基点の内容・更新日時の不変 | [`LibraryDirectoryPreflightService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Startup/LibraryDirectoryPreflightService.cs) | [`LibraryDirectoryPreflightTests`](../../../tests/BeMusicSeeker.Core.Tests/Library/LibraryDirectoryPreflightTests.cs) の `LinkedModePreflight_SucceedsWithoutChangingBmsRootOrAnyOutputBase` が本番の読取り境界と実ディレクトリで、読取り専用属性・既存内容・空の各基点の成功と、項目集合・ファイル内容・全ディレクトリの更新日時の不変を確認する。`LinkedModeInspectsEveryOutputBaseAndDoesNotCreateMissingBase` が通常・追加・ROOTの各基点の欠落を種別付きの失敗とし、自動作成しないことを確認する。書込み不能の事前判定は行わないため、ACLによる書込み拒否は検査しない。 |
| 状態が現行の通常・追加通常の表での、出力基点の行の `date` の追従と、再確認時にファイルを書き直さないこと | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) の必要出力の修復計画（祖先ディレクトリ行の確認、配置だけの照合） | [`BmsPlaylistCustomFolderOutputTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) の `ReloadTables_StatusCurrentOutputBaseRowFollowsActualMtimeOnlyUnderStaleBase` が、通常・追加通常のそれぞれを対象に実DB・実ファイルで再読込みし、行が一致する基点と行のない基点を戻さないこと、基点の更新時刻だけの変化と行の `date` だけの変化のどちらでも対象基点の表だけを再確認して一回の同期で `date` を揃えること、`.lr2folder` の本文・更新時刻と既存の `adddate` の保持、修復後の収束を確認する。ROOT型の除外は既存のROOT行ケースと差分の静的確認、更新時刻の取得不能と照会失敗は静的確認へ分担する。 |
| 手動再同期の受付通知失敗時の両受付の解放 | 設定の出力確定、LR2楽曲DB同期の受付 | `BmsLibraryLr2SongDbSyncTests.QueueLr2SongDbSync_AdmissionNotificationFailureReleasesBothLeasesBeforePreparation` が確認する。 |
| 新先生成後の旧管理行の整理失敗と後続同期 | 設定の出力確定 | `OutputConfirmation_GenerationAndCleanupFailuresAllowLatestRequest` が、実DELETE故障による旧行の残存、新先の維持、通知内容、後続同期への接続を確認する。 |
| 起動時の必要出力の接続と、BMT・LR2出力の生産側通知 | 起動の必須初期化、[`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`StartupRequiredInitializationTests`](../../../tests/BeMusicSeeker.Core.Tests/Startup/StartupRequiredInitializationTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) / [`BmsPlaylistMigrationAndRegistrationTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)。 |
| 非空宛先への単表移行と旧登録のないSQL復元 | [`PlaylistCustomFolderOutputMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs) の `PrepareCustomFolderOutputMigration`、[`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) の `RestorePlaylistDumpAsync` | [`BmsPlaylistCustomFolderOutputTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) の `PlaylistPropertyDialog_MigrationReplacesExistingDestinationFromCurrentDefinition` が通常・ROOT・追加通常の実mtime付き旧本文をプロパティ保存で置換し、実本文・実DB行・選曲を確認する。`PlaylistRestore_ReplacesExistingOutputWithoutPreviousPlaylistState` が旧表登録・出力状態のないDBへの実SQL復元直後に残置本文の置換を確認する。管理外の物理本文からのDB同期は `Lr2FolderFileDiscoveryServiceTests.ExcludeAppManagedOutputCandidates_RemovesManagedOutputDirectoryFilesOnly` と [`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) / [`BmsLibraryLr2SongDbSyncTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) の管理候補除外・外部定義保持の既存検証へ分担する。 |
| 組込み予約領域と旧禁止設定の訂正 | `CustomFolderOutputBaseSearchRootSyncService`、設定確定担当、`StartupLibraryConstructionOwner` | `SettingDialogCustomFolderOutputBaseTests.SettingsAcceptance_ReservesBuiltinOnlyAtConfigurationBoundary` が三種出力基点・BMS登録の条件表と上位祖先出力下の禁止BMS登録を確認する。`SettingsDialogBehaviorTests.OutputConfirmation_ReservedBaseKeepsAllMediaAndBackendUnchanged` と `OutputCorrection_SectionsAndAdditionalShrinkKeepBuiltinFilesWithoutStartingBackend` が拒否前の媒体保全と実sectionの逐次訂正を、`SettingDialogEnvironmentConfirmationTests.ReservedOutputModeSave_FreshStartupRequiresCorrectionThenAncestorBaseAndPropertySaveSucceed` がmode即時保存・fresh起動訂正・許容祖先baseと実表編集を分担する。安全管理先の通常移行・削除は `BmsPlaylistMigrationAndRegistrationTests` が維持する。 |
| ドライブルート配下の実ROOT表削除と管理外楽曲の保持 | `PlaylistRemovalWorkflowOwner`、`PlaylistCustomFolderOutputMaintenanceOwner` | [`SettingDialogLr2OwnerTests`](../../../tests/BeMusicSeeker.Compatibility.Tests/Settings/SettingDialogLr2OwnerTests.cs) の `Lr2DriveRootOutput_PreservesConfirmedInstallThroughGenerationInstallationAndTableRemoval` は設定で保存した管理外導入先への実導入後、同じ表の物理ディレクトリ・folder行・表登録・jukebox登録の削除と、楽曲の実体・DB行・表外ファイルの保持を確認します。別名は全owner終端後に解除し、清掃は所有した一時実パスだけへ行います。 |
| 同じLR2正本へのROOT登録保存と限定公開 | [`PlaylistCustomFolderOutputMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs)、[`Lr2ConfigOwner`](../../../BeMusicSeeker/Models/LR2/Lr2ConfigOwner.cs) | [`BmsPlaylistCustomFolderOutputTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) / [`BmsPlaylistCustomFolderOutputTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) / [`BmsPlaylistMigrationAndRegistrationTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistSummaryBulkEditTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) / [`PlaylistSummaryBulkEditTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) が各入口の保存・出力を、[`部分保存と実表削除`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogLr2OwnerTests.cs) が別操作の未公開値を巻き込まないことを確認します。 |
| 同一・包含の両方向拒否、名前表記と区切り境界 | `ManagedPlaylistOutputPlacement` | `ManagedPlaylistOutputPlacementTests.FinalDirectories_RejectOnlyIdenticalOrNestedPaths` は条件表を共通判定へ集約する。 |
| 保存済み不正配置の出力・削除・移行・root準備拒否 | `PlaylistCustomFolderOutputMaintenanceOwner` | `BmsPlaylistCustomFolderOutputTests.SavedCollidingOutput_RejectsOutputAndRemovalBeforeMutation`、`BmsPlaylistMigrationAndRegistrationTests.MigrateCustomFolderOutputDirectory_RejectsSavedCollidingLayout` は旧保存値から実経路を通し、DB・ファイルを保全する。 |
| 同一表の旧新、旧path再利用、管理基点外 | 移行の旧先整理 | `BmsPlaylistMigrationAndRegistrationTests.MigrateCustomFolderOutputDirectory_PreservesSameTableDestination` と既存の管理基点外・`Folder`/`Folder2`・今回旧新配置の整理ケースで維持する。 |
| 外部新規登録の配置拒否と受付解放 | `PlaylistAggregatePersistenceOwner.PrepareExternalRegistration` | `BmsPlaylistMigrationAndRegistrationTests.ExternalTableRegistration_RejectsOverlappingCandidateBeforeMutation` は単一と安全候補を先頭に含む一括でDB・正本・ファイル・XMLの未変更と次の安全登録を確認する。 |
| 通信・必要公開までの受付と次明示要求 | `PlaylistExternalSyncOwner`、`PlaylistWorkspaceViewModel` | [`BmsPlaylistExternalReloadTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistExternalReloadTests.cs) / [`BmsPlaylistExternalReloadTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[`PlaylistWorkspaceExternalSourceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs) / [`PlaylistWorkspaceExternalSourceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs): 実通信前から同側Busy、未受理の削除・登録が自動再実行されないこと、実DB/出力と解放後の新明示要求を確認する。 |
| 日常の非交差並行と正本・CP932・フォルダ実反映 | `ApplicationComposition` | [`DailyLibraryDiffAndIndependentPlaylistRegistration_CompleteActualChangesConcurrently`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs): 実到達・実結果・Task終端を確認し、保持点はfinallyで解放して全開始Taskを待機する。 |
| 導入先を出力管理領域へ取り込む設定変更 | [設定の出力候補・局所確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、`ValidateOutputBasePreservesInstallDirectory` | `SettingDialogCustomFolderOutputBaseTests.OutputBase_ProtectsConfirmedInstallDirectory` が各基点・正規化比較を、`SettingDialogOutputConfirmationTests.OutputConfirmation_ProtectsInstallDirectoryUntilAnotherConfirmedRootIsSelected` が拒否の実媒体・配置無変更と導入先変更後の実移行を確認する。今回の最新入力による再保存・再生成は次行の実媒体検証へ分担する。 |
| 追加先改名の生入力・通常閉鎖の破棄と確定時の名前・保存先一致 | `SettingsDialogViewModel.SelectedCustomFolderAdditionalOutputBaseName`、`RenameSelectedCustomFolderAdditionalOutputBaseDir`、`ExecuteCloseCommand`、`CustomFolderOutputBaseRegistry.RenameLastDirectoryName`、設定の局所出力確定 | [`CompiledAdditionalOutputRename_PreservesInputSpacesUntilConfirmation`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogCategoryPersistenceTests.cs) が実Bindingの逐次入力と前後空白の確定時除去、未確定での保存なしを確認する。正常確定後の同値明示改名では表示だけを正規化し、provider書込み・確認・通知・二媒体の変化なしとBMT候補の保持・確定値への非混入を確認する。別ページの通常保存では候補保持、実Close・同VM再表示では追加保存なく最新公開名へ復帰し、次の明示改名へ破棄名を混入させないことも同じ代表で確認する。`SettingDialogCustomFolderOutputBaseTests.AdditionalOutputBaseRename_RejectsCurrentDirectoryName` が `.`・空白だけの拒否を、[`OutputConfirmation_HeaderFailureUsesPublishedNamesOrFreshSavedDefinitions`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogOutputConfirmationTests.cs) がDB失敗後の同一runtimeとfresh owner、内部空白を含む新名のfresh provider保存値・実表参照・実出力先の一致、未知旧参照の明示再選択までの出力停止を分担する。 |
| 二媒体部分保存後の最新入力再保存と正本DB失敗 | 設定の局所出力確定、playlist header保存 | `SettingDialogOutputConfirmationTests.OutputConfirmation_LatestSelectionResavesBothMediaAfterPartialFailure`、`OutputConfirmation_GenerationAndCleanupFailuresAllowLatestRequest` が今回入力の保存・生成・旧先残存後の次操作を、`BmsPlaylistMigrationAndRegistrationTests.OutputSettings_HeaderFailureKeepsAllPublishedNames` が全headerのDB確定を確認する。 |
| 未登録追加名の保持・停止、ROOT継続と明示再選択 | `CustomFolderOutputBaseRegistry`、`PlaylistPropertyDialogViewModel`、既存出力管理主体 | `CustomFolderOutputBaseRegistryTests` が純粋な解決条件を、`BmsPlaylistCustomFolderOutputTests.UnregisteredOutputBase_PreservesNameAndStopsOnlyNormalOutputUntilReselected` が既存DBの名前、正常な兄弟表、サマリー表示、プロパティ保存・ROOT切替・通常先再選択と実出力を確認する。`PlaylistSummaryBulkEditTests.BulkOutputChange_UnregisteredOldBasePreservesUnrelatedNormalOutput` が一覧の通常・追加先への再選択による実DB・必要生成と通常先の保全、外部名初期化時の停止継続を確認する。`BmsPlaylistMigrationAndRegistrationTests.ExternalTableRegistration_UnregisteredOutputBasePreservesHeaderAndCompletesBmt` は単一表の移行入口でもLR2出力だけを停止し、正本保存・BMT出力を完了することを確認する。 |
| 旧先削除失敗と新先状態、次通常処理で旧残余不変 | `BMSPlaylist` | [`RegenerateCustomFolderOutputs_UsesInjectedSettingsAndPreservesCommittedDestinationAfterOldCleanupFailure`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs): 実到達・実結果・Task終端を確認し、保持点はfinallyで解放して全開始Taskを待機する。 |
| 同一編集セッションの未完了移行・config原子的保存とC保存前の完了 | `PlaylistPropertySaveService`、`BMSPlaylist.MigrateCustomFolderOutputDirectoryWithSettings` | [`BmsPlaylistCustomFolderOutputTests.PlaylistPropertyDialog_UnfinishedOutputsCompleteBeforeSavingFurtherEdits`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)。DB=B、旧A/新B、ディスクから再読込みしたconfig、BMT、他表と管理外データを確認する。通常型・ルート型・追加先の正常移行は既存保存・移行テストと分担する。 |
| 確定所属からの管理DB生成と物理本文の独立性 | `BMSPlaylist`、`Lr2SongDbSyncPreparedDataSurface`、`Lr2FolderTableReconciliationService` | `BmsPlaylistCustomFolderOutputTests.ManagedDefinitions_PreserveConfirmedSelectionsWhenPhysicalTextChangesAtSameMtime` が実保存のFile/Folder、한글/??別所属、日本語・単引用符・compat_prefixを実SQLiteの独立hash集合で比較する。実準備後に同mtimeの旧条件・別条件・空本文へ変えたbytesを残し、局所と全体同期の選曲が一致することを確認する。型定義の生成文字列再parse撤去は生産経路の静的確認へ分担する。 |
| 種類・SQL・階層と保存設定 | [`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`BmsPlaylistCustomFolderOutputTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) / [`BmsPlaylistCustomFolderOutputTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) |
| BP順・クリア通常版の負値・NULL・スコアなしの末尾配置と新規生成・明示再出力 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) の `MakeBpNullLastOrder` とBP・クリア出力 | [`BmsPlaylistCustomFolderOutputTests.CustomFolderOutput_BpMissingValuesFollowNonnegativeValuesOnCreationAndExplicitReoutput`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) が実ファイルのCOMMANDを実DBへ適用し、0→20→欠損集合、ASSIST・NO PLAYの分類、対象所属・削除除外、rate・playcountの負値を含む降順と保存済みscore不変を確認する。階層・ランダム・ASSIST/EASY分類は同クラスの `ReOutputCustomFolderAndCommitToDB_WritesHierarchicalRandomAndSortFolders`、詳細未保存ASSISTの履歴投影は `PlayHistoryReadModelTests.RowProjectionTreatsInitialLr2ForceEasySentinelsAsClearOnly` が分担する。自動一斉更新を加えないことは差分検査で確認する。 |
| 名前なし・空白名の所属保持とLEVEL/ADDDATEの所属内ソート、変更保存時の再出力 | [`BMSPlaylist.makeCustomFolderCmdSort`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistCustomFolderOutputTests.ReOutputCustomFolderAndCommitToDB_UserFolderSortUsesOwnMembershipValues`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) が実保存値、生成COMMANDのSQLite上の順序と別所属専用曲の除外を確認する。名前なしはLEVEL/ADDDATE両方向、空白名はLEVEL昇順を扱い、名前なしLEVEL昇順だけは方向変更保存後に同じファイルを再読込みして降順を確認する。同じ代表の初回COMMANDは、ケース所有の読取り接続で未指定走査順を反転しても所属内のhash順が不変であることを確認する。ALL/通常名・旧NULL生存行・File/Folderは既存の `ReOutputCustomFolder_RawNullLiveEntriesRemainIncludedAndUsePersistedSortValues`、種類の独立有効化・ランダム・スキーマは既存ケースと分担する。 |
| 一括生成・旧出力先・状態・受付 | [`PlaylistCustomFolderOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputOwner.cs) | [`PlaylistCustomFolderOutputOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistCustomFolderOutputOwnerTests.cs)、[`BmsPlaylistCustomFolderOutputTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) / [`BmsPlaylistCustomFolderOutputTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) |
| 部分修復の開始・更新・全終端の要求識別 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) の `HydrateRequiredAsync` | [`BmsPlaylistPersistenceLifecycleTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) の `ReloadTables_HydrationRepairRegeneratesBatchPrunesStaleRowsAndPreservesCurrentFiles` と `ReloadTables_ZeroTargetRepairPublishesOnlyInactiveProgress`: 通知元`custom_folder_repair`と要求のSource`playlist_custom_folder_output_repair`、世代・親操作IDを保ち、成功・元の修復失敗・対象0件の全終端まで同じ要求IDを確認する。一括再生成・余分な行の整理・現在ファイルと利用者列の保持、既存の部分修復カウントも維持する。 |
| 通常・追加先の同一登録維持と親子配置の拒否 | [`CustomFolderOutputBaseSearchRootSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/CustomFolderOutputBaseSearchRootSyncService.cs) | [`LR2ConfigTests`](../../../tests/BeMusicSeeker.Core.Tests/Lr2/LR2ConfigTests.cs) の `OutputBaseSearchRootValidation_OnlyRootOutputCanContainRegisteredDirectories`、`SyncAdditionalOutputBaseRoots_RejectsParentOrChildWithoutReplacingRegistration`、`RepairNormalOutputBaseRoots_RejectsInvalidAdditionalBeforeAddingDefault`: パス方向、全候補の変更前検証、XML・登録の保持。 |
| ルート型先の復元と外側の親登録の保護 | [`PlaylistCustomFolderOutputMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs) | [`SettingDialogCustomFolderOutputBaseTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogCustomFolderOutputBaseTests.cs) のルート同期テストと `RootOutputBaseSync_RejectsRegisteredParentWithoutChangingRootsOrFiles`: 基点・旧子登録からの復元、未生成ディレクトリの補完、禁止配置の未変更性。 |
| 注入設定による出力先移行 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) の `RegenerateCustomFolderOutputs` | [`BmsPlaylistMigrationAndRegistrationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) / [`BmsPlaylistMigrationAndRegistrationTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs): 同じ実library bindingsで旧ディレクトリの移行、注入先への実生成、共有設定へのfallback不在を確認する。小さい設定providerの総呼出し回数を上位の保証にはしない。 |
| 準備中のフォルダ非保存・空入力・外部行の保存列 | [`BmsLr2SongDbSyncWorkflowRuntime`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) | [`BmsPlaylistCustomFolderOutputTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) / [`BmsPlaylistCustomFolderOutputTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) の実準備case群: 所有libraryの生存権限をtyped bindingsへ接続し、実生成面・件数、空入力の正常終端と解放、外部フォルダ行の保存列を確認する。保存後の結果は実Queueの代表接続に分担する。 |
| LEVEL分類の整数表記と、旧版の出力の明示的な再出力による修復 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | `BmsPlaylistCustomFolderOutputTests.ReOutputCustomFolderAndCommitToDB_LevelBucketsUseInvariantSqlAndRepairOldOutput`: 負の小数・負の整数・0・正の値・未定義のlevelで、通常版とRANDOM版の名前とSQLの選曲が一致し、旧版の不良な本文が明示的な再出力で修復されること。 |
| スキーマの互換性・既定値 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`PlaylistSchemaMigrationTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistSchemaMigrationTests.cs) |
| 単一名入力、旧保存値の利用、raw入力拒否の順序 | `BMSTable`、`PlaylistPropertySaveService`、プロパティ・サマリー編集 | `BMSTableLoadTests.OutputDirectoryInput_RejectsPathsBeforeSanitizing` と `OutputDirectoryName_SanitizesLegacyValueWithoutChangingStoredText` が条件を集約する。`PlaylistWorkspaceActionWorkflowTests` の出力名系列が実保存・旧値継承・未完了出力より先の拒否・サマリー通知を確認する。ROOT実出力と旧ファイル保持は `ReOutputCustomFolderAndCommitToDB_SyncsRootOutputRowsUnderTableDirectory`、Bindingはプロパティ仕様へ分担する。 |

## 関連資料

[プレイリスト保存](storage-and-export.md)、[プロパティ画面](../ui/playlist-properties.md)、[楽曲DB同期](../integration/lr2-song-db.md)を参照します。
