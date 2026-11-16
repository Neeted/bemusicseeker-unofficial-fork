# データ規模と性能

## 目的と適用範囲

カタログ・DB・索引・キャッシュ、導入・削除・走査、一覧、起動の設計と、性能の受入の正本です。新規の実装と変更する箇所へ適用する要件であり、現行の全経路を代表規模で測定済みという宣言ではありません。未達の既存の経路は制約として示し、無関係な全面改修を一律には要求しません。

索引の所有・識別・更新の規則は[データと索引](data-and-indexes.md)、受付と並行可否は[操作の競合ポリシー](operation-concurrency-policy.md)を正本とします。

## 用語

[共通用語集](../glossary.md)の実時間、処理量、差分、計測点、分割問い合わせを使います。規模を表す記号は次のとおりです。

| 記号 | 意味 |
| --- | --- |
| `C` | 所持譜面数 |
| `D` | 譜面フォルダ数 |
| `K` | 逆引きキー数 |
| `E` | フォルダ別のリソース登録数 |
| `F` | 列挙件数 |
| `L` | プレイリストの項目数 |
| `P` | 一操作のパッケージ数 |
| `Δ` | 変更対象 |

## 仕様

### 優先するもの

同じ仕事を正しく完了するまでの時間の短縮と、処理量の向上を最優先にします。索引の保持、計算結果の再利用、一括処理、独立した計算の並列化を認め、CPU使用率や常駐メモリが高いことだけでは不合格にしません。メモリを減らすために、同じDB取得・ファイル読取り・ハッシュ計算・解析・索引構築を繰り返しません。

並列度を最大にすること自体も目的ではありません。ロックやI/Oの競合、GC、ページング、一時コピーが遅延の原因なら、その原因を減らします。固定のRAM・CPUの上限や、キャッシュを最小にする目標は置きません。資源の上限が必要な機能では、その理由と速度への影響を示します。

走査・同期・保守などのパイプラインで、キューの受取り・追加や同期待機によって操作全体の間ブロックする段は、[`BlockingPipelineStage`](../../../BeMusicSeeker/Models/Utils/BlockingPipelineStage.cs) で専用スレッドに置きます。スレッドプールに置くと、並行する操作や呼出元の同期待機とプールのスレッドを奪い合い、プールの増加を待って停滞するためです。段の内部の短い並列ループは、スレッドプールを使います。

次は許容しません。

- メモリ不足、停止、破損。
- 不要になった旧世代の無期限の保持。
- 取消・終了・WPFのスレッド制約の違反。

UIの外の計算を並列化することと、UIスレッドで重い同期処理をすることは別です。最初の表示だけを早くし、必要な仕事を後処理・遅延取得・次の操作へ移した場合は、その完了と、後続の操作への影響も測ります。

### 代表規模

[代表ライブラリの測定記録](../../acceptance/performance-reference-2026-09-09.md)の規模を、少なくとも検討・検証する規模として使います。入力の上限ではなく、より大きい入力を切り捨てる根拠にもしません。

| 対象 | 規模 | 数え方・制約 |
| --- | --- | --- |
| 所持カタログ `C` | 約21万譜面 | BMS・BMSONが混在する。同じMD5の複数の配置を含み、ハッシュの種類数とは異なる。 |
| 譜面フォルダ `D` | 約3万 | 直下・祖先込み・物理列挙の数は区別する。 |
| 逆引きキー `K` | 約800万台 | 音声・画像・動画のカテゴリ別のキーの合計。実ファイル数ではない。 |
| フォルダ別の登録 `E` | 約1,200万台 | 譜面フォルダごとのリソース登録。複数のフォルダの関係を含む。 |
| 列挙件数 `F` | 約1,200万台 | 列挙した仕事量。異なるキーの数と区別する。 |
| `song` / `bmson_song` | BMS約21万行 / BMSON約1千行 | 同じハッシュが別のパスに存在し得る。BMSONをLR2の `song` に入れない。 |
| フォルダ行 | 約3万台 | 通常のフォルダ、祖先、独自の出力の範囲を区別する。 |
| 譜面情報など | 各約21万件級 | `chart_digest_map`、`chart_info`、`maintenance`。部分的なキャッシュ、所持主体のない情報、解析失敗を区別する。 |
| 表・項目 `L` | 約500表 / 約58万項目 | 同じ譜面の複数の表への所属と、未所持の項目を含む。 |
| 独自フォルダの出力 | 約31万件 | 物理的な出力の件数。DB行・表の数とは別。 |
| スコア・IR | 約2万行級の読込み | 参照記録での観測であり、履歴の総数や容量の上限ではない。 |

正確な件数・ログ名・チェックサムは、測定記録へ集約します。DB容量、索引の容量、文字列・BLOBの長さ、逆引きの候補数の分布は、この件数だけでは分かりません。必要な比較では、ファイル容量、実際の行数・索引・キーの分布も記録します。

操作の基準は、20個のZIPの一括投入と、単一のパッケージです。

- 参照した操作は20パッケージ・24譜面・20削除フォルダですが、一般に一つのZIPが一つの譜面とは限りません。パッケージは、複数の譜面と数百のリソースを含み得ます。
- 検証には千件級も含めますが、実際の分布や最大値は未測定のため、上限には使いません。
- 複数の譜面が共有するリソース、逆引きの変更が0件の場合、物理的な存在確認の回数を区別します。

導入先推定の探索上限は別の契約です。[導入先推定](../library/install-estimation.md)では通常10,000ファイル未満を想定し、ディレクトリ型のパッケージのルートごとに50,000項目で探索を打ち切って、不完全な結果を使わずに警告します（[推定用探索の上限](path-length-and-io.md#推定用探索の上限)）。この安全境界を、ライブラリ全体の容量の上限と混同しません。

### 全件処理と局所処理

局所的な変更の仕事量は、主として、変更対象・関連キー・必要な祖先に依存させます。小さい操作の反復と巨大な集合が掛け合わさる `O(P*C)`、`O(P*K)`、`O(ΔD*K)` は、退行のリスクです。処理をコンストラクター・プロパティ・別の管理主体へ移しても、仕事量は減りません。

| 経路 | 要件 |
| --- | --- |
| 起動・明示的な全走査 | 必要な全件の構築は認める。同じ世代の同じ入力を、段階ごとに読み直さない。完全性と代替の経路の条件を維持する。 |
| 初回の索引取得 | 初回のコストを明示し、同じ世代の構築を共有する。パッケージごとに再構築しない。 |
| 少数の導入・移動・削除 | 内側の反復で、全カタログ・全逆引き・全プレイリストを走査・複製・整列しない。 |
| フォルダ判定・LR2同期 | 対象のパス、関連フォルダ、祖先を既存の索引から得る。少数の存在確認だけのために、全BMSの一覧を作らない。 |
| 複数の対象の変更 | 一操作の変更セッションで、前処理、DB・索引の反映、LR2同期、必要な公開を集約する。 |
| 一覧の絞込み・整列 | 全件が必要な問い合わせと、可視行の表示を分ける。無関係な更新で全順序を捨てない。 |

共通の所持集合から形式別の範囲を読む場合は、前に並ぶ形式の件数に比例する読み捨てや、範囲の末端より後の訪問をしません。項目ごとの木の添字検索や全範囲の事前の複製を避け、途中で打ち切る列挙での範囲内の先読みは、実際の訪問として計測します。通常一覧のBMS専用の表示は、捕捉の前からBMSONの準備・整列を省きます。背景の件数によらない範囲取得と、捕捉後の不変性は、[データと索引](data-and-indexes.md#共通現在値の格納識別順序)の対応表で確認します。

全件処理が本当に必要な場合は、規模、頻度、既存の索引で代替できない理由、比較区間と結果を示します。「変更不能だから」「安全側だから」だけで全件の複製を選ばず、計算量と、定数項・候補数・実測を併せて評価します。

#### 祖先リソースの更新

物理フォルダの変更に伴う祖先のリソースの更新は、成功した範囲の親を辿り、既存の項目へ点検索して選びます。

- 必要な祖先は、同じ操作の中で一度だけ取得・置換します。走査ルートの包含による縮約で、下位の必要な項目を捨てません。
- 少数の対象のために、新しい全カタログの走査や全項目の複製を追加しません。既存の移動処理の全キーの照合とは区別します。
- 検証では、対象・祖先・資源を固定して、無関係な背景の項目と、同じ親の譜面数を別々に増やし、項目の訪問、祖先の判定、変更、逆引きの書込み、実際の列挙の範囲・件数を確認します。
- 実際の終端までの時間は、修正前後のReleaseを同じ条件で交互に、原則各5回以上測ります。初回と再利用時、祖先の取得の部分を分け、全値・中央値・範囲を比較します。
- 旧い版の祖先の不整合を、結果が同等であることを理由に性能の成功と見なしません。合成した小さい入力から、代表規模での成功を主張しません。

### 再利用と保存

スナップショットには、既存の世代、構造共有、差分、影響範囲だけの変更不能な値を使います。公開済みの可変な配列を、保護なしで共有しません。変更なしの判定の前に巨大な辞書を複製して捨てず、対象のキー・カテゴリ・候補集合で判定します。逆引きが変わらなくても、フォルダの対応は変わり得ます。

実際の変更も操作の中で集約し、中間の読取りが必要とする世代、部分成功、失敗時の旧いスナップショットを守ります。結果記録には利用先が必要な事実だけを持たせ、全パスの二重の整列や索引の再作成を固定費にしません。失効の範囲は入力の依存に合わせ、同じ失効に対する再構築は既存の集約の境界を使います。

同じパッケージの列挙・バイト列・ハッシュ・解析結果は、有効期間の範囲で再利用します。同じ内容でも、新しい保存先でのリソースの存在確認は別です。外部の変更、削除の結果、解析器・スキーマの版など、再確認が必要な条件は明示します。必要な警告や確認を落とす短縮は認めません。物理的な処理は、件数に加えて、容量、同一・別のボリューム、展開済みか、削除の方式、失敗・再試行の数も記録します。

#### DBの読取りと書込み

DBは、問い合わせの数だけでなく、読み出す行数・列・索引を確認します。少数のパスの処理を、全テーブルの取得後の絞込みに戻しません。既存のDB窓口で、パラメーター化、集合での取得・更新、分割問い合わせを使います。パスの識別、同じハッシュの複数の所持、動作モード、部分的なキャッシュの意味を変えません。

DBの読込み方式は、起動時に捕捉した変更不能な `BmsDatabaseLoadOptions` で指定します。

- 楽曲カタログ、`maintenance`、譜面情報の補完読込みの三つの方式を、`ApplicationRuntimeOptions` から各DB窓口と初期化サービスへ明示的に渡します。読込みのたびに環境変数を再参照しません。
- 環境変数の値は、大文字小文字を区別しない `sqlite_net` だけを従来のプロバイダーの方式（false）、それ以外を生の値の方式（true）とする意味を維持します。
- 同じプロセスの中で別の構成が異なる方式を使っても、先に作った構成が捕捉した方式は変わりません。
- 生の値の方式に固有の列の射影と型の保持、プロバイダーの方式による行の読込みは、それぞれの互換条件を維持します。

起動時の `chart_info` と `playlist_entry` の生の射影は、SQLiteのINTEGER・REALを型のまま読み、必要な文字列の列だけを取得します。毎行の文字列配列やボックス化、全行のORM化を中間の段階に増やしません。現在の行は同期的なコールバックの中だけで借用し、コールバック先は未公開のモデルを生成するだけとします。読取りの途中で、DB・UI・他の管理主体のロックへ再入しません。従来の文字列のAPIも同じ読取りの経路を共用し、元の例外とSQL文の解放を維持します。値の解釈は次のとおりです。消費側の既定値と行の採否は、各機能の仕様に従います。

| 型 | 解釈 |
| --- | --- |
| 整数 | INTEGER、またはInvariantな整数のTEXT。Int32の範囲外とREALは不適合。 |
| `double` | REALは直接、INTEGERは明示的に変換する。TEXTだけをInvariantな浮動小数点・桁区切りとして解釈する。 |
| `bool` | INTEGERの非0、またはTEXTの真偽の字句・Invariantな整数の非0。REALはfalse。 |
| NULL・不適合 | nullableはnull、それ以外はfalse。文字列の欄の数値風のTEXTを再解釈しない。 |
| 日時 | 範囲内のINTEGERのticks、または旧いTEXTのticksから作る。地域に依存する日付の推測をしない。 |

### SQLiteの競合待機

楽曲DB・スコアDBの接続では、SQLiteのネイティブなbusy待機を60秒に保ちます。

- `SQLiteConnectionEx` は、DeleteとInsertOrReplaceを含むSQLの外側で、BusyとLockedを再試行しません。
- Commitも一回だけ実行し、失敗を呼出元へ返します。プロバイダーはCommitに失敗するとトランザクションの内部状態を解除してrollbackを試みるため、Commitを再び呼んで正常扱いに変換しません。

この待機の設定は、一回の競合待機の設定です。通常のSQLの実行時間や、終了処理全体の期限ではありません。終了時は[終了](../runtime/shutdown.md)の実際の終端の待機に従います。

### 操作単位の反映

競合する操作の受付、一操作の中の独立した計算の並列化、対象の逐次処理は分けます。先行するパッケージの実際の成功が次の分類に必要なら、操作内の成功差分で渡します。逐次の判断を、対象ごとのDB確定・索引の更新・通知の理由にしません。

一回の確定要求は、一つの変更セッション（`LibraryMutationSession`）にまとめます。物理的な処理は対象ごとで構いません。成功した旧新の事実を追加し、DB、保存行、参照、所持集合、逆引き、関連するキャッシュ、LR2、必要な公開は、各管理主体へ原則一回だけ反映します。SQLの分割や別のDBのトランザクションは認めますが、同じ全体処理を対象の数だけ繰り返しません。

事前に判断できるスキップ・衝突・古い対象は、通常の結果に集約します。予期しない例外は安全でない後続を止め、成功済みと未確認の対象を残します。全対象を元へ戻す擬似的なトランザクションは要求しません。具体的な保証は[ライブラリ変更](../library/mutations.md)と[ファイルとDBの整合性](../library/file-db-consistency.md)に従います。

取り込みの自動推定は、確定後も同じ論理的な受付の中で直接待ちます。準備済みの入力元のリソースと、所持・未所持の分割を、同じ操作の中で再利用し、競合後の再捕捉・再評価を通常のコストに含めません。単一の対象の候補の並列度は `max(1, CPU数-1)`、一括の対象は外側を並列にして候補の内側を1とすることを維持します。受付は、開始したすべてのTaskと後片付けの終端まで保持します。

### 計測する完了範囲

主な指標は操作の実時間です。CPU時間、割当量、GC、常駐量は、原因の調査に使います。処理量は、譜面/秒、リソース/秒、バイト/秒など、単位を示します。

| 操作 | 比較する範囲 |
| --- | --- |
| ZIPの投入・保留の準備 | 受理から、展開・発見・分類・保留の登録・必要な推定結果まで。登録と推定の完了は分けてよい。 |
| 導入 | 確認後の受付から、必要な物理変更・DB確定、正本と譜面情報の反映、公開・後片付け・終端まで。 |
| 削除・移動・統合 | 受付から、対象の処理、カタログ・逆引き・LR2、後片付け、終端まで。失敗の件数・状態も比較する。 |
| 起動 | 導入の準備、操作の解禁、必須の初期化、後続処理を分ける。スケジューラーの外の処理も区別する。 |
| 再読込み・保守・補完解析 | 発見から、必要な読取り・計算・保存・反映まで。変更・不変・スキップ・総数を示す。 |
| 一覧・プレイリスト | 受付から、正しい検索・整列結果の反映と有用な描画まで。後続の集計・先行読込みは別に記す。 |

利用者の回答待ちと候補の計算、待ち行列・ロックの待機、必須の表示、任意の先行読込みを分けます。遅延の区間を外へ移しただけで改善としません。入れ子や並列の累積時間を独立した区間として合算せず、計測名と実際の範囲を揃えます。範囲を変更した場合は、旧い値と比較できるかを示し、未計測・欠測を0で補いません。

### 変更セッションの集約診断

`LibraryMutationSession.Commit` が返る時点の集約値を、排他権の解放後に、計測点 `library_mutation_session_apply_done` として一回記録します。作業中の一時的な集計と、変更不能な `LibraryMutationSessionApplyCounts` を使い、永続的な状態や対象ごとの同期ログを増やしません。

| フィールド | 意味 |
| --- | --- |
| `reason` / `changeCount` | 診断名と、受理した確定変更の数。理由の文字列で挙動を分岐しない。 |
| `durableCommit` / `requiredFailure` | この時点の永続確定と、必須処理の失敗。物理変更を取り消す値ではない。 |
| `catalogApplyCount` | カタログの移転・削除の反映を試みた回数。 |
| `installedTargetApplyCount` | 導入先・導入行の反映を試みた回数。カタログの反映とは別の系統。 |
| `packageReferenceApplyCount` | カタログの変更に伴うパッケージ参照の反映を試みた回数。導入行の書込みや、個別の物理的な完了の数ではない。 |
| `reverseLookupApplyCount` | 移動・導入・統合・削除したフォルダの集合を、リソースの管理主体へ渡した回数。SQLの分割数・フォルダ数ではない。 |
| `lr2SyncCount` | LR2同期の管理主体の呼出しの数。モードが無効、またはBMSの変更がなく内部の処理が不要でも、呼出しは数える。 |
| `requiredPublicationCount` | 準備した通常の通知のグループの数。後の必須の失敗で非公開になり得る。通知先の実行・成功の数や、失敗の診断の数ではない。 |
| `folderDbTargetRows` | 移転成功の結果記録にある、実際の既存のフォルダ行の数。要求したパスの数ではない。対象なし、またはDB失敗で成功の記録がない場合は0。 |
| `folderDbFullScanCount` | カタログの移転でのフォルダの全件取得の数。現在は完全一致の分割問い合わせだけのため0。別の目的のLR2の全体同期は含めない。 |

各回数は呼出しの直前に加算し、途中で失敗しても到達した段階を残します。未到達は0、変更なしは全て0とし、失敗後に成功用の固定値へ置き換えません。正常な複数の対象の操作で、同じ反映が対象の数に比例して増える状態は、構造上の退行です。

この計測点はセッションの反映の範囲であり、操作全体の完了ではありません。同じ受理済みの操作の権限を借用する確定後の保守（統合後のリソースの再検査など）と、最終の表示は、この後に続きます。その失敗は同じ結果の `WithFinalizationFailure` へ保持し、すでに数えた反映の回数は変えません。

### 比較条件と受入

基本のシナリオは、同じ検証用のファイル・DB・設定へ20個のZIPを保留として投入し、既所持の警告を無視して導入し、新規の画面からその追加譜面を削除する操作です。実際に利用中のライブラリではなく複製を使い、各測定の前に状態を復元します。「削除したから元どおり」とは仮定しません。合成した入力では、その入力の正しい件数を期待値にします。

変更対象に応じて、次のようなケースを選びます。全組合せの毎回の実行は要求しません。

- 固定の差分で背景の規模を増やす。固定の背景で1個と20個を比べる。
- 1・3譜面と、100・500・1,000リソースを組み合わせる。
- 逆引きの変更なし、空のカテゴリ、少数のキー、大きい候補集合を使う。
- 複数の所持、形式の混在、入れ子、部分失敗を通す。
- 初回と再利用を分ける。

基準の版は、通常は変更直前のコミットです。既知の古い退行を調べる場合も、その版を別の基準として示します。次の条件を揃えます。

- 実行物とビルド設定、ハードウェア・OS・ランタイム。
- DBと入力、設定・表示列、Everything、容量、キャッシュ、並行処理、ログ。

通常は各5回以上を目安に順序を交互にし、全値・中央値・範囲を残します。初回・再利用・起動直後は別の群とし、異なる20パッケージを20回の反復とは数えません。少数の標本から、安定したp95や有意差を主張しません。

受入では、結果・安全性・警告を維持し、操作別の再現する悪化を、他の操作の短縮で相殺しません。局所的な変更で全件の仕事を増やさず、反映の回数と実測を確認し、後処理や次の操作への遅延の付替えも調べます。未測定・条件不足は制約として示し、機能テストの成功を大規模な性能の成功とは見なしません。固定の秒数の目標や許容する退行率は、基準データと反復測定なしには定めません。検証の実行の360秒などの制限時間は、アプリの性能予算ではありません。

管理出力との交差判定は、保存・適用済みの基点と、現在の正本の対象を使い、全所持譜面の走査・複製を追加しません。日常の非交差のライブラリの反映とプレイリストの反映は並行し、全体同期の一括生成・短期のDBの保護・大規模な入力の再利用を維持します。

設計・報告には、対象の入口、規模と差分、全件処理の回数、再利用と失効、逐次の境界、計測する完了範囲、結果の同等性、実測と制約を短く示します。専用の台帳や、全変更への大規模な測定は要求しません。

### 設定画面の構築とルート候補

設定の表示要求から最初の `OnContentRendered` までは、ウィンドウの構築、各ページの構築、音声一覧の背景処理の実行を、別の区間として記録します。未訪問のページは生成しません。

一回のLR2ルート候補の取得では、出力先の除外集合とプレイリストの全件スナップショットを、現在の計算と変更前の計算で共有します。候補が0件、または現在・対象の変更前のROOT出力先が共に空なら、全件スナップショットを取得しません。次の取得は新しい入力を捕捉し、永続的なキャッシュは増やしません。計測の入口・終点、失敗・表示の寿命、回帰の検証は[設定](../runtime/settings.md#初回表示とページの寿命)を正本とします。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 祖先の取得の仕事量と、背景・親の譜面数の分離 | [`LibraryMutationSession`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Session.cs)、[`ChartDirectoryScanBuilder`](../../../BeMusicSeeker/Models/Utils/Scanning/ChartDirectoryScanBuilder.cs) | [`BmsLibraryFolderRenameRefreshTests.FolderMutation_RefreshesExistingAncestorsOnceFromFinalFilesystem`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryFolderRenameRefreshTests.cs) が、小さい背景と親の譜面の改名・移動で、実際に列挙したルートと返却したファイル数、項目の訪問、祖先の項目の書込み回数を確認する。Performanceの [`FolderMutation_WorkScalesWithAffectedAncestors`](../../../tests/BeMusicSeeker.Performance.Tests/Maintenance/BmsLibraryFolderRenameRefreshTests.cs) が、背景と親の譜面数を別の条件で増やし、必要な祖先の仕事量が譜面数に比例しないことを担当する。旧新の版の実際の終端までの時間と祖先の取得の時間は、同じ条件での比較で確認し、速度の合否の閾値は追加しない。 |
| 差分の走査のコミット幅、並列の合流、故障時の終端 | [`FileScanParseCommitOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Scanning/FileScanParseCommitOwner.cs) | [`BmsLibraryInitializationFileScanTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) / [`BmsLibraryInitializationFileScanTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Startup/BmsLibraryInitializationFileScanTests.cs) が、小さいケースで、分割した保存、既存と新規の混在での複数の作業の実際の入場、トランザクション前のログの故障、後の分割の実際のSQLの故障による先頭の部分の保全とrollbackを確認する。Performanceの [`FileScan_ProductionDefaultsPersistGeneratedLibrary`](../../../tests/BeMusicSeeker.Performance.Tests/Scanning/BmsLibraryInitializationFileScanTests.cs) が、既定のコミット幅を超える件数の実際のBMSを既定の設定で保存し、末尾と全行を確認する。 |
| パイプラインのブロッキング段の専用スレッド | [`BlockingPipelineStage`](../../../BeMusicSeeker/Models/Utils/BlockingPipelineStage.cs)、`FileScanParseCommitOwner`、`ChartInfoBuildService`、`BmsLibraryMaintenanceService`、`Lr2SongDbSyncService`、`BmsLibraryIrService` | [`BlockingPipelineStageTests`](../../../tests/BeMusicSeeker.Core.Tests/Runtime/BlockingPipelineStageTests.cs) が、補助が段をスレッドプール外で実行し例外を保持することを確認する。各パイプラインのブロッキング段がこの補助を使うことは、差分で静的に確認する。 |
| 一時コピーの競合と、量に対する所有の追跡 | [`temporarilyCopyFiles`](../../../BeMusicSeeker/ViewModels/ChartOperations/temporarilyCopyFiles.cs) | [`TemporaryCopyFilesTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/TemporaryCopyFilesTests.cs) の日常のケースが、実際のファイルと操作単位の並列度で両方の作業の入場を観測し、全バイト・全回収・入力の保全を確認する。Performanceの [`CopyTracking_RepeatedParallelOwnership`](../../../tests/BeMusicSeeker.Performance.Tests/ChartOperations/TemporaryCopyFilesTests.cs) が、既定の並列度で多数の追跡と回収を担当する。 |
| 起動時の生の射影の型の保持、固定のTEXTの互換、SQL文の寿命 | [`LR2SongDBExtended.SQLiteCommandExtended`](../../../BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs) | [`SQLiteProviderRuntimeTests`](../../../tests/BeMusicSeeker.Core.Tests/Runtime/SQLiteProviderRuntimeTests.cs) の `SQLiteCommandExtended_PreservesRawTextAndNullValues` が、実際のSQLiteのdoubleのビット、ticks、原文、NULLと、従来の文字列のAPIを確認する。`SQLiteCommandExtended_TypedGettersKeepStorageTypeAndFixedTextGrammar` が、複数の地域設定での型・範囲・TEXTの互換・不適合を確認する。既存の一括・逐次の読取りの失敗と `SQLiteCommandExtended_TypedCallbackPreservesExceptionAndReleasesStatement` が、元の例外と、解放後の明示的なSQLを確認する。読取り専用の借用の寿命、共用する読取りとモデルの生成の非再入は静的に確認する。最終的な集合の精度は各読込みのテストへ分担し、大規模な性能の成功と区別する。 |
| SQLiteのネイティブなbusy待機と、外側の再試行がないこと | `SQLiteConnectionEx`、`LR2SongDBExtended`、`LR2ScoreDBExtended` | `SQLiteProviderRuntimeTests` の `SQLiteConnectionEx_WriteContentionExecutesOnceAndPreservesData` が、実際のDeleteとInsertOrReplaceを別の接続のロックで失敗させ、SQLが一回だけ実行されることと保存値の不変を確認する。`Lr2Connections_KeepNativeBusyTimeoutForReadAndWrite` が実際の接続の60秒の設定を、`SQLiteConnectionEx_CommitBusyRemainsFailureAndRollsBackUncommittedChanges` がCommitの失敗とrollback、次の明示的なトランザクションを確認する。外側のループと未使用の補助処理がないことは、差分でも確認する。 |
| 構成ごとに固定するDBの読込み方式 | `BmsDatabaseLoadOptions`、`BmsLibraryDbGateway`、`BmsLibraryInitializationService` | [`BmsLibraryInitializationLoadTests.LoadSongTable_RawCatalogLoaderPreservesSongColumns`](../../../tests/BeMusicSeeker.Core.Tests/Startup/BmsLibraryInitializationLoadTests.cs) と [`ChartInfoInlineHydrationTests.LoadChartInfoHydrationData_UsesRawProjectionAndPreservesCurrentness`](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs) が、同じプロセスで生の値・プロバイダー・生の値の順に実際に読み、方式と結果の独立を確認する。`maintenance` は、既存の生の値・プロバイダーの両方のケースが分担する。生の値に固有の射影とTEXTの互換を、プロバイダーの方式へ拡張しない。 |
| 複数のフォルダ・拡張子の反映の集約とDBの失敗 | `LibraryMutationSession`、`CommitCatalogSessionChanges` | [`BmsLibraryFolderRenameRefreshTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryFolderRenameRefreshTests.cs) の `AutoRenameChartFolders_BatchesMultipleFolderMutationsIntoOneRefresh`、`MoveLibraryRootFolder_MixedFoldersPublishesOneOperationNotification`、`MoveLibraryRootFolder_DatabaseApplyFailureKeepsConfirmedPhysicalMoves`、`RenameBMSFilesExtensionsWithReceipt_MultipleExtensionFamiliesUseSingleSession` が、反映の回数をファイル・SQLite・通知・索引と対照する。 |
| 複数のパッケージと、失敗時の非公開 | `CommitInstalledSessionChanges` | [`BmsLibraryPackageInstallServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) / [`BmsLibraryPackageInstallServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `PendingResourcePackages_InstallThroughLibraryAndPreserveEarlierResourceSnapshot`、`InstallPendingPackagesToEstimatedDestinations_CanonicalApplyFailureKeepsPreparedTargetsWithoutPartialPublication` が、旧い世代と、成功後の対象集合を確認する。 |
| 修正・削除・統合・LR2の失敗 | ライブラリ変更の管理主体と、統合後の保守 | [`BmsLibraryFolderRenameRefreshTests`](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryFolderRenameRefreshTests.cs) の `FixInstallationDirectoryCharts_MultipleRepairsShareOneCatalogTransaction`、`RemoveLibraryCharts_CommitsCatalogBeforePublishingOwnedCollectionChange`、`AutoRenameChartFolders_Lr2FinalizationFailureReturnsDurableNonSuccess` と、[`BmsLibraryDuplicateServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) / [`BmsLibraryDuplicateServiceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `MergeChartDirectory_RechecksResourcesWithinAcceptedOperation`。 |
| 実際のフォルダ行の数 | `BmsLibraryDbGateway.ReplaceFolderRecords` | [`CatalogMutationOwnerTests.ApplyRelocation_CombinedBmsBmsonAndFolderEmitsDurableReceipt`](../../../tests/BeMusicSeeker.Core.Tests/Catalog/CatalogMutationOwnerTests.cs) が、対象の行数と移転後の行を確認する。 |
| 全候補の評価の明示的な性能計測と、解析器の入力規模 | 導入先推定・解析器 | [`InstallEstimationPerformanceTests`](../../../tests/BeMusicSeeker.Performance.Tests/Install/InstallEstimationPerformanceTests.cs) が、要求数・同梱率・候補数を変えた条件で、単体の逐次・上限付きの並列と同一の複数の入力、初回のプロフィールの構築・再利用について、全値・中央値・範囲・割当・逆引きの訪問・実際の比較数を繰り返し測る。索引の準備を別に記し、検証データが所有するプロフィールの供給キャッシュを使うサービスの、捕捉済みの入力の評価までが対象。同じ系列で、実際のカタログのプロフィールの解決と診断カウンターを使い、単体・複数の要求での初回の共有と再利用も別の区間で測る。実際のファイルシステム・DB保存・UIの終端は含まない。[`Net10ScanParserPerformanceTests`](../../../tests/BeMusicSeeker.Performance.Tests/Chart/Net10ScanParserPerformanceTests.cs) の同じ位置のノート系列は、全規模の全位置の探索を計時し、結果の配列も事前に確保して、全位置がnullであることを計時後に確認する。走査の回数や速度の閾値は保証しない。 |
| 明示的な性能検査と、実際の規模の区別 | [性能検証の入口](../../../tools/BeMusicSeeker.Verify/README.md#明示性能suite)、[Performanceモジュール](../../../tests/BeMusicSeeker.Performance.Tests) | [診断の書出しのケース](../../../tests/BeMusicSeeker.Core.Tests/Performance/Net10PerformanceCorpusContractTests.cs)と、小型の機能保証は、Coreの日常の検証に維持する。実際の負荷は固定の検査群を、Release・単一の作業・セッションの排他で実行し、全規模を環境値で縮小しない。走査・LR2・フォルダ・コピーは、準備と表明を除く操作の呼出しから実際の終端までを記録する。固定の秒数は監視の上限であり、速度の目標でも、移管だけによる性能改善の証拠でもない。実際のDBの全規模や800万の逆引きを自動で構築する保証へは拡張しない。 |

## 関連資料

[データと索引](data-and-indexes.md)、[ワークフローと並行性](workflow-concurrency.md)、[ログ出力](logging.md)、[検証](../development/testing.md)。
