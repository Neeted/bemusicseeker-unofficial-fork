# プレイリストのランプ集計

## 目的と適用範囲

一つのプレイリストについて、所持数、クリア状況、DJレベルをローカルの別ウィンドウで表示する仕様です。現在の所属・所持状態と、選択日のスコアを区別します。

## 用語

[共通用語集](../glossary.md)に従います。

**区間**は積上げバーの一カテゴリです。**基準日**は、その日の終了時点のスコアを再現するための日付です。

## 仕様

### 開く操作と状態

プレイリストツリーとサマリー行の「ページを開く」の直下に「ランプビューアを開く」を表示します。型付きの画面要求で処理し、ブラウザや外部URLは使用しません。

呼出しごとに新しいウィンドウを作ります。同じプレイリストも複数開けます。メインウィンドウを一時的な所有者として初期位置を決め、モードレス表示と最初のアクティブ化を一回試みた直後に所有者を解除します。以後の重なり順をメインウィンドウに拘束せず、ビューアはそれぞれタスクバーへ表示します。通知ダイアログの所有者はメインウィンドウのままです。

| 状態 | 表示前 | 表示後 |
| --- | --- | --- |
| `Loading` | 最初の確定結果を待つ。 | 明示操作による新しい集計結果を待つ。 |
| `Ready` / `Empty` | 表示する。 | 内容を更新する。 |
| `Deleted` | 一回通知し、表示しない。 | 対象のビューアだけを通知なしで閉じる。 |
| `Failed` | 未取得として表示し、結果に依存する表示・操作を無効にする。 | 現在の要求の取得失敗を表示し、結果に依存する表示・操作を無効にする。 |

取得・集計の失敗だけでビューアを閉じず、正常な空集計・ゼロ件・全件未プレイへ置き換えません。結果領域を未取得へ更新し、旧表示の保存・条件照合・復元は要求しません。失敗後も別の日付や過去日から「最新」への変更で、保持する取得組から再集計できます。他の窓の表示・操作は独立しています。

対象削除は、既存のプレイリスト一覧変更通知を管理主体が受け、安定した `playlist_id` で現在の対象を再解決して判断します。この通知で捕捉・再集計は行いません。一覧の再読込みに伴う同じ対象の一時的な除去・再追加を削除と誤認しません。初回の削除案内に失敗しても対象窓の回収を妨げません。

### 初期配置とDPI

プロセスは埋込みマニフェストでPerMonitorV2を宣言します。

初期位置は、表示直前の所有者と最大の面積で重なるモニターの作業領域に収めます。通常状態は所有者の現在の外形矩形、最大化・最小化状態は選んだ作業領域を、中央の基準にします。復元用の位置は使いません。希望外形寸法は1290 × 820 DIP、通常の最小外形寸法は820 × 560 DIPです。

- 希望寸法が収まる場合は、基準の上下左右の中央へ置き、はみ出す位置だけを作業領域内へ補正します。
- 希望寸法が超過する場合は、超過した軸だけを縮め、作業領域の左上へ置きます。
- 位置は負座標も含む物理ピクセルで扱い、DPI倍率は寸法だけに掛けます。整数へ丸めた後も、作業領域への包含を保ちます。
- 通常の最小寸法も収まらない軸は、表示先に合わせて緩和します。OSの枠などが要求する最小寸法を超える極端に小さい領域までは保証しません。

初回結果と、取消・対象削除の確認を得た後に、同じUIスレッドで未表示のネイティブウィンドウを作ります。最小寸法を一時的に緩和して、対象の作業領域内へ位置と寸法を同時に移し、移動後のネイティブ側とWPFのDPI変換が一致することを確認します。その後、希望寸法から最終の外形を適用して表示します。未表示の画面要素は未接続なので、初期配置のDPIは画面要素から取得しません。待機やポーリング、最終寸法の二重適用は行いません。ネイティブ側の取得・配置の失敗は、既存の表示失敗と同じ回収経路へ返します。

表示後の移動・サイズ変更はWPFと利用者の操作を優先し、初期位置へ戻しません。最小寸法は現在のDPIと作業領域で更新し、広い画面では通常値へ戻します。表示先の大きさを恒久的な最大寸法にはしません。同じDPIで作業領域だけが変わる移動では、最小寸法だけを更新します。

DPI変更時はWPFの標準処理を維持し、フォルダ名・件数の列幅を再計測します。区間ラベルも、論理幅が同じでも現在のDPIで収容判定を再評価します。この更新では、DB読取り・再集計、行・区間の再生成、表示モード・選択・スクロールの初期化は行いません。ロード前・閉鎖後の列計測は、既存の更新予約・解除で抑止します。

### 取得の契機と通常の終了

開くとき、別の基準日へ変更するとき、過去日から「最新」へ戻すときに、その時点の表所属・フォルダ・所持状態・アプリ内スコアと、使用する情報源の履歴を捕捉します。同じ日付の再設定と、既に最新の状態での「最新」は何もしません。

アプリ内スコアの再読込みが失敗した後の明示捕捉は、その情報源の失敗状態を使い、以前の成績を今回の利用可能な結果へ置き換えません。次の通常読込みが成功すれば、新しい明示捕捉にその成績を使います。既に捕捉した不変入力は後の読込みで変えません。[保存成績の再読込み](../library/chart-model.md#保存成績の再読込みと旧結果)に従います。

現在成績と履歴の情報源・DBパス・ノーツは、一回だけ捕捉した公開済みのスコアスナップショットから一組として作ります。同種の情報源で設定や成績がAからBへ切り替わっても、Aの現在成績とBの履歴・ノーツを混ぜません。巻戻し行のない譜面にも同じ捕捉の現在成績を使い、次の明示要求から、新しく公開された一組を使います。既存の取得元の世代は引き継ぎますが、追加の世代照合やDB再読込みで一貫性を補いません。

スコアが未公開なら捕捉から構築・公開・通知を始めず、スコアと過去日の履歴を利用不可にします。最新設定から別の履歴文脈を作ったり、空の正常スコアへ置き換えたりせず、通常ロード後の新しい明示要求で復帰します。公開済みの取得元がLR2なら、連携用の取得元が保持するDBパスを履歴にも使い、最新の連携ON/OFFやパスを読み直しません。beatorajaも、捕捉したDBパスと正のノーツ数を履歴の読取りに使います。

開いているだけの窓は、スコア・表所属・所持状況の変更へ自動追随しません。これらの変更用の購読、通知ワーカー、更新予約は持ちません。外部スコアDBの自動再読込みや監視、更新ボタン、古さの警告、日付別の結果キャッシュも設けません。表エントリが初回準備中の場合だけ既存の準備完了を非同期で待ち、対象を再解決して捕捉します。窓の取消しは待機を終え、共有の準備処理そのものは停止しません。

ビューアのセッションは、初回取得と、明示操作で受理した全ての捕捉・集計を、終端まで所有します。後の要求で置き換えられた処理も含みます。要求の世代番号で古い結果を拒否し、UIスレッドへ渡した後も、画面へ適用する時点で世代と閉鎖状態を確認します。取消しを無視した古い捕捉が後から戻っても、後続の集計は開始しません。

手動での閉鎖、対象削除、初回取得の取消し、表示準備の失敗、所有者の終了は、各窓の一つの回収処理へ合流します。未表示のまま生成済みのウィンドウも実際に閉じ、アプリケーションのウィンドウ集合と所有者の所有ウィンドウから除去します。閉鎖イベントからの再入も同じ回収へ合流し、初回表示や通知自身を逆方向に待ちません。

同期の `Dispose` は、受付と結果の公開を止め、実際のウィンドウの閉鎖を先に要求します。取消コールバックや捕捉・集計をUIスレッドで同期的に待ちません。メイン画面の終了経路は `DisposeAsync` で、開始済みの処理と通常の回収の終端を待ち、閉鎖後の窓や通知は表示しません。初回表示用の取消トークンは、その処理と取消コールバックが終端してから解放します。資源を持たない捕捉データ源に、通知活動や専用の破棄契約は設けません。

生成・表示準備・閉鎖・必要な回収の失敗は、任意案内の失敗へ読み替えません。既存の回収経路で残りを試み、所有者へ失敗を返します。所有する失敗の通知は既存の翻訳済み一般エラー文言を使い、元例外の詳細は既存のログへ記録します。解放を確認できなかった資源は所有者の管理下に残し、同じ終了待機から確認できるようにします。自動再試行や通知失敗の履歴保存は行いません。位置・大きさ・選択は保存しません。

### 統計とカテゴリ

#### 集計の単位

同じ表の保存済みの所属（フォルダと、元の項目のMD5・SHA-256・LR2 ID・タイトルの順で選ぶキー）を集計単位とし、同じ所属の重複した配送だけを除きます。所持譜面の解決後のMD5やパスで、別の所属を統合しません。同じ譜面がMD5だけの所属とSHA-256だけの所属にある場合は、所持状態が変わっても二件のまま、同じ成績を各所属へ適用します。カテゴリ、割合、統計、区間から開く詳細一覧も同じ単位です。

有効な項目だけを集計し、未所持の項目も総数に含めます。全体の割合の分母は総数、フォルダの割合の分母はそのフォルダの項目数です。分母が0なら「利用不可」とし、0%にはしません。

通常フォルダの保存名が文字どおり `[NO SONG]` でも、他の通常フォルダと同じく所属・件数・全体の集計へ含めます。表から捕捉するフォルダ順と項目に、特殊ノードは混ぜません。ツリーの未所持の特殊ノードは、表示名ではなく特殊ノードの種別で区別し、表示名が同じことを通常フォルダの除外理由にしません。

#### 統計と補助行

統計は次の3群で表示します。

| 群 | 項目 |
| --- | --- |
| 所持 | 総数、所持数、未所持数、所持率 |
| プレイ | プレイ済み数、未プレイ数、プレイ率 |
| スコア | EXスコア率の算術平均、全体のクリア率 |

クリア率は、`ASSIST` 以上の数を総数で割った値で、`FAILED` と `NP` を含めません。

- 各項目はラベルと値の組で、組の途中では改行しません。項目は内容の幅で並べ、狭い場合は群の単位で折り返し、行端に群の区切りを残しません。既定の大きさでは、日本語の9項目を一段に収めます。
- 使用中のスコアの情報源と、プレイリストの最終更新は、統計の下の補助行に表示します。補助行のラベルと値の連結、統計の読み上げ名には、言語ごとの書式を使います。
- 統計と補助行の文言は表示言語の変更へ、色はテーマの変更へ追従します。これらの表示の更新だけでは、集計・取得の要求やグラフの投影を変更しません。

スコアを取得できない場合は、プレイ群とスコア群の5項目をまとめて省略し、グラフとその操作も表示しないか無効にします。所持群と補助行は維持します。空の集計や全件未プレイに置き換えず、この状態だけでビューアを終了しません。

`playlist.last_update` はローカルの壁時計の値です。時刻帯を割り当てたり変換したりせず、固定の文化圏・Gregorianの `yyyy-MM-dd HH:mm` で、分の粒度で補助行に表示します。NULLは「利用不可」です。スコアの情報源の最終更新日時を代わりに使いません。

#### カテゴリ

| 情報源・区分 | 表示規則 |
| --- | --- |
| beatorajaのクリア | `MAX`、`PERFECT`、`FC`、`EXHARD`、`HARD`、`NORMAL`、`EASY`、`ASSIST`、`FAILED`、`NP` の順。`ASSIST` は `INVALID` と `L_ASSIST` を含む。 |
| DJレベル | `AAA`、`AA`、`A`、`B`、`C`、`D`、`E`、`F`、`NP` の順。値 `MAX` は `AAA` に含める。 |
| DJレベルF | プレイ済みの `F` と値 `INVALID` を含む。`NO_PLAY` / `NO_SONG` は含めない。 |
| NP | 未プレイまたはスコアなし。専用のNSカテゴリは作らない。 |
| LR2 | 凡例、全体・フォルダのバー、ツールチップ、アクセシビリティ情報のすべてで `MAX` と `EXHARD` を除く。 |

### 基準日のスコア

過去日のスコアには、[取得の契機](#取得の契機と通常の終了)で現在の成績と一組に捕捉した、同じ公開済みスコアスナップショットの全履歴を使います。現在の成績と履歴は、同じ一回の取得で得たものでなければなりません。日付の変更ではDBへ接続せず、取得後のDBの変更・移動・消失や最新の設定を参照して、別の情報源へ切り替えません。スコアが未公開なら取得を開始せず、利用不可にします。LR2は取得時のスキーマ判定を共有し、索引だけの不備は警告付きで利用でき、トリガーの不足・不一致では過去の成績を利用不可にします。

初期値は「最新」で、現在のスコアを使用します。基準日はカレンダーだけで選択し、テキスト欄は読取り専用です。使用中の情報源全体で対象になる最古の履歴行のローカル日付から今日までを許可します。履歴がない場合は日付選択だけを無効にし、「最新」は使用できます。選択はウィンドウごとに独立し、保存しません。

日付入力面・Calendarは[共通の日付部品](../ui/appearance.md#日付部品の表示)を使います。未選択の案内、短日付、カレンダーの月・曜日・週開始と操作説明はアプリの選択言語に追従します。非Gregorianの追加言語でもWPF標準のGregorian書式を使い、フォーカス喪失やカレンダーを開く操作で選択日を変えません。履歴の取得や再有効化後も読取り専用を維持します。親がLoadedのままテンプレート内のテキスト部品を再生成した場合も、新しいPARTへ表示文化圏と読取り専用を再接続し、選択日・表示月・Calendarモード・取得要求を変更しません。表示中の同じ部品を更新し、言語・テーマ変更で選択日、表示月、割合／曲数モード、取得要求を変更しません。`null` は現在スコアを意味し、「最新」は日付指定を解除します。同じ日付の指定と、既に最新での「最新」は再取得しません。

ローカル日付 `D` を選ぶと、[履歴の共通日付境界](play-history.md#期間と集計) `Bz(D+1)` 以降の履歴を現在のスコアから巻き戻します。0時が重複する場合は早いUTC、欠落する場合は指定日以降の最初の有効な境界を使います。丸日欠落では前日と欠落日の翌日境界が同じになり、同じスコアを復元します。境界時刻と同じ行も含め、時刻の降順、同時刻では情報源の行IDの降順に処理します。読取り要求内で時刻帯を一度決め、今日・履歴行のローカル日付・翌日境界へ同じ値を使います。省略時は `TimeZoneInfo.Local` を使用し、時刻帯を保存しません。

| 情報源 | 復元規則 |
| --- | --- |
| LR2 | 確定・未確定の両方を読み、`old_clear`、`old_op_history`、`old_exscore`、`old_totalnotes` を復元する。 |
| LR2の旧スコアなし | `old_playcount = NULL` なら、他の旧値があってもNPにする。 |
| LR2の不完全な旧値 | `old_playcount` が非NULLで、前記4フィールドがすべてNULLなら履歴スコアを利用不可にする。現在値やNPで補わない。 |
| beatoraja | 同じ取得組のモード0のrawから `oldclear`、`oldscore` と取得時に固定したノーツ数を使う。 |
| 巻戻し対象の行がないハッシュ | 現在のスコアを維持する。旧スコアなしとは区別する。 |

読取りは使用中の情報源だけで行い、失敗しても別の情報源へ切り替えません。LR2では必要な履歴トリガーがすべて一致することを要求します。修復可能な索引だけの不具合は警告付きで読めますが、この読取りでは修復・書込みをしません。

履歴の欠落、不正、読取り不能、日付範囲外は過去成績だけの利用不可とし、「最新」では保持する通常成績を使えます。履歴の取得失敗は日付変更で読み直さず、次の許可取得で更新します。取消に応じない処理でも、明示要求の世代番号で古い結果の公開を抑えます。

履歴DBの元例外は[共通の読取り境界](play-history.md#外部操作と診断)で診断します。ランプ固有の履歴変換に失敗した場合も、その場所で元例外・取得元・段階・対象パスを記録し、利用不可という結果へ変換します。診断のために例外を保持・再送出しません。

過去日に変わるのはスコア依存の数・割合・グラフだけです。プレイリスト所属、フォルダ順、所持・欠落状態、最終更新には、その明示操作時に捕捉した現在の情報を使います。

### グラフと操作性

#### 配置

左をクリア、右をDJレベルとし、同じ幅の二つの領域に、見出し・凡例・積上げバーを表示します。全体のバーの下には一つの縦スクロールと仮想化された行の集合を置き、通常フォルダをその順序で表示します。0件の通常フォルダも残します。実フォルダ名が `[NO SONG]` の行も、表示と共通の尺度へ含め、未所持の特殊ノードは集計行にしません。

ヘッダーには、プレイリスト名、基準日の選択、「最新」ボタン、表示モードの切替を置きます。長いプレイリスト名は省略記号と全文のツールチップで扱い、通常の最小幅や各言語・明暗テーマでも操作部と重ならないようにします。

#### 表示モード

「割合」「曲数」は排他的な二択で、初期値は割合です。ウィンドウごとに独立し、保存しません。スコアが利用不可の間は操作を無効にします。基準日・最新への変更、データの更新、利用不可からの復帰でも、選んだモードを保持します。切替のボタンは、キーボード操作・フォーカス・選択状態・アクセシビリティ情報を維持します。

| モード・対象 | 幅の尺度と区間ラベル |
| --- | --- |
| 割合の通常行 | 各行自身の項目数 `N` で100%へ自己正規化し、区間の割合を表示する。 |
| 曲数の通常行 | 通常フォルダ全件の最大の項目数 `M` を共通の尺度とし、利用可能幅 `W` に対して、区間件数 `c` の幅を `W*c/M`、行の占有幅を `W*N/M` とする。左右で同じ尺度を使い、区間件数を表示する。 |
| 全体のバー | 両モードとも全幅へ自己正規化し、区間ラベルだけを割合・曲数へ切り替える。 |

曲数は既存の項目数です。同じタイトルやハッシュによる追加の集約はしません。最大値は、未実体化の行も含む通常行の全件から、結果の更新時に再計算します。0件の通常行は保持し、全行が0件・フォルダなしでは区間を作らず、ゼロ除算をしません。最後の区間は行の占有幅の終端で端数を調整し、短い行の余白を埋めません。

幅の尺度と、詳細の割合の分母は分けます。ツールチップとアクセシビリティ名は各フォルダ自身の件数を分母とし、`M=100` でも50件中20件は40%です。

モードの切替だけでは、DB読取り、再集計、結果の適用、行・区間・コレクションの再構築、選択やスクロールの初期化、一覧への要求を起こしません。幅が変わらない最大行と全体のバーも、ラベルを即時に更新します。

#### 行と区間

各行は、ラベル、バー、件数の順に並べます。ラベルと件数の列幅は書体と現在の内容から測り、両方の領域で共有します。各列幅には上限を設け、長いラベルには省略記号と全文のツールチップを使います。行の更新後に再計算し、バーが重ならない行間を確保します。

正の件数の区間だけを、件数で重み付けた標準のボタンとして配置します。最後の区間に、占有幅の中の端数のピクセルを割り当て、利用可能幅を超えないようにします。0件の区間は、正の幅やボタンを持ちません。既定の幅では凡例を一行に収め、狭い場合は折り返せます。

#### 数値の書式

| 表示 | 書式 |
| --- | --- |
| 件数（凡例・行・曲数モードの区間） | 単位なし・桁区切りなしの整数。固定の文化圏で表示する。 |
| 割合 | 小数点を `.` とし、小数2桁・桁区切りなし。 |
| 表示精度より小さい正の割合 | 0ではなく、翻訳済みの下限の表現。その数値も同じ固定の書式を使う。 |

凡例にはラベルと件数を表示します。バーの中の割合・曲数は完全に収まるときだけ表示し、切れた文字や省略記号にしません。正の件数の区間のツールチップとアクセシビリティ名には、完全なラベル、件数、0ではない割合を残します。

#### 言語・テーマと選択

表示中の言語の変更は、同じ全体・フォルダのクリア・DJレベルの区間へ反映します。凡例のラベル、割合、ツールチップ、アクセシビリティ名と選択状態の説明を更新し、小さい正の値の下限の表現と、算出不可の説明も現在の言語を使います。区間・ボタン・フォルダ行と各コレクション、選択、表示モード、集計結果を維持し、再取得・再集計・結果の再適用・一覧への要求を起こしません。言語の変更の購読は親のViewModelの寿命で管理し、ウィンドウの閉鎖と破棄の終端後は、親と区間へ通知しません。

色は動的なテーマから取得します。

- 曲数モードでバーが使わない領域は、実際のウィンドウの背景が見えるようにし、NPの区間と区別できるようにします。
- 区間の文字色は、実際の単色の背景のWCAGの相対輝度から、不透明な黒または白を選びます。選択後も同じ規則を使います。
- 選択した区間だけを、枠線・太字・アクセシビリティの状態で示します。

区間のボタンは、Enter、Space、アクセシビリティのInvokeで同じ要求を発行します。

### 一覧への移動

正の区間を選ぶと `PlaylistLampSegmentInvocationRequest` を渡します。安定した `playlist_id` で現在のプレイリストを再解決し、対象フォルダとキーワードを組にした一件の要求で詳細表示へ反映します。古い・削除済み・0件・利用不可の要求は何もしません。

通常フォルダでは同じフォルダを確認して通常の一覧を選択し、実名 `[NO SONG]` も同じ条件で扱います。全体の `Overall` ではテーブルルートを選び、同名の実フォルダを含む現在の通常フォルダの和集合を対象にします。未所持の特殊ノードは移動先にしません。通常フォルダ内の未所持エントリも集計対象とし、スコアのないエントリと `NO_PLAY` / `NO_SONG` はNPの対象です。

クリアの別名 `pf`、`fc`、`exh`、`hc`、`nc`、`ec`、`ae`、`lae`、`f` と、DJレベルの `aaa`、`aa`、`a`～`f` を実際の一覧パーサーに渡します。`ASSIST` は `INVALID | L_ASSIST`、`AAA` は `AAA | MAX` に対応します。文字列の厳密な順序ではなく、最終的な所属と一回の表示反映を契約にします。

過去日のグラフからも現在のカテゴリ・範囲へ移り、基準日時を一覧へ渡しません。ツリー選択が成功した後だけ、メインウィンドウの最小化解除・アクティブ化・フォーカスを一回試みます。フォーカスに失敗しても、適用した移動を取り消しません。ビューアと他のウィンドウの選択状態は維持します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 中央配置・位置補正・片軸の縮小、負の原点、非整数DPI、最小寸法の緩和と復帰 | [配置計算](../../../BeMusicSeeker/Views/Playlist/WindowPlacementCalculator.cs) | [`WindowPlacementCalculatorTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/WindowPlacementCalculatorTests.cs): 代表条件、平行移動、丸めた後の包含。 |
| 表示前のネイティブウィンドウ、DPIの一致、表示直前の所有者と状態別の基準、タスクバー | [配置](../../../BeMusicSeeker/Views/Playlist/WindowPlacementCalculator.cs)、[ウィンドウの管理](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindowManager.cs) | [`PlaylistLampViewerWindowPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs) の `Viewer_initialPlacementUsesCurrentOwnerAndSynchronizesHiddenNativeWindow`: 表示前の外形・非表示・DPIと、タスクバーの設定。集計後の配置から表示への接続は静的確認。異なるDPIのモニター間の移動とタスクバーの見た目は自動検証に含めない。 |
| DPI変更での列幅の再計測とラベルの再評価、既存状態の維持 | [画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml.cs) | 同クラスの `Viewer_countModeUsesAllNormalFoldersAndPreservesProjectionUntilDataRefresh`: 非整数DPIの通知で、行・区間の参照、選択・スクロール・モード、捕捉・集計の回数が変わらないこと。 |
| 配布する実行ファイルのPerMonitorV2 | [マニフェスト](../../../BeMusicSeeker/app.manifest) | [`ManagedDependencyOutputPolicyTests`](../../../tests/BeMusicSeeker.Windows.Tests/Verification/ManagedDependencyOutputPolicyTests.cs) の `ApplicationExecutableEmbedsPerMonitorV2Manifest`: 構築した実行ファイルの埋込みマニフェスト。 |
| 明示操作時の表・所持・スコアの捕捉、初回準備の待機 | [データ源](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsLibraryPlaylistLampDataSource.cs) | [`BmsLibraryPlaylistLampDataSourceTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsLibraryPlaylistLampDataSourceTests.cs): その時点の入力、準備完了の待機と取消。`CaptureAsync_keepsChartInfoShaSeparateAndUsesItForBeatorajaLookup`: 実名 `[NO SONG]` のフォルダ順・所属と、未所持のSHA-256の補完。 |
| 現在の成績と履歴の同一の捕捉、未公開時の利用不可 | [データ源](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsLibraryPlaylistLampDataSource.cs)、[取得元の構成](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs) | 同クラスの `CaptureAsync_keepsCurrentScoresAndHistoryFromOnePublishedBeatorajaSource`: 捕捉後に別のスコアが公開されても、成績・ノーツ・最古日が同じ一組に揃い、次の要求から新しい組を使うこと。`CaptureAsync_unpublishedScoresStayUnavailableUntilNormalLoad`: 暗黙の構築・通知をせず、通常の読込み後に復帰すること。`CaptureAsync_keepsCapturedLr2SourceAfterLatestSettingsDisableLink`: 設定の変更後も捕捉したLR2の履歴を使うこと。 |
| 保存済みの所属単位の捕捉と重み付け、所持の変化後の維持 | [データ源](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsLibraryPlaylistLampDataSource.cs)、[集計](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampAggregationService.cs) | `BmsLibraryPlaylistLampDataSourceTests.CaptureAsync_PreservesSavedMembershipAcrossOwnershipChanges`: MD5だけ・SHA-256だけの二所属が、所持・パスの変更と未所持への復帰の後も二件のままで、区間から詳細の二行へ移ること。`PlaylistLampAggregationTests.Aggregate_WeightsSavedMembershipsRatherThanResolvedCharts`: 所属単位の重み付け。 |
| 集計の分母・カテゴリ・更新日時、実フォルダ名の保持 | [集計入力](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampModels.cs)、[集計](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampAggregationService.cs) | [`PlaylistLampAggregationTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistLampAggregationTests.cs): 正常な空、未所持、情報源別の計算。`Aggregate_usesCanonicalFolderOrderKeepsEmptyAndExcludesNonRealEntries`: 実名 `[NO SONG]` の行・件数・全体の成績、削除・ダミーの除外。`Aggregate_emptyPlaylistPreservesOrdinaryFoldersAndLeavesRatesUnavailable`: 0件の行と利用不可の割合。 |
| 過去日の復元、旧値の欠落、情報源の障害 | [履歴スコア](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampHistoricalScoreSnapshot.cs)、[共通日付境界](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/LocalDateBoundary.cs) | [`PlaylistLampHistoricalScoreSnapshotTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistLampHistoricalScoreSnapshotTests.cs) の `Cutoff_includes_local_midnight_and_same_timestamp_uses_source_id_descending`: 境界と同時刻の行のID降順の復元。`Lr2_reader_skipped_day_shares_cutoff_with_previous_day_without_writing_database`: 丸一日欠落する日と前日が同じスコアになり、DBを書き換えないこと。旧値の欠落・取得元・スキーマ・取消は同クラスの既存ケース、境界の条件差は履歴側が分担する。 |
| 明示要求の競合、失敗結果の非採用、全受理処理の終端 | [セッション](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampViewerSession.cs) | [`PlaylistLampViewerSessionTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistLampViewerSessionTests.cs): 新旧の日付の完了の逆転、取消しを無視した捕捉の後の集計の抑止、停止後の受付・公開の拒否。 |
| UIスレッドへ渡した後の古い結果・閉鎖後の結果の非採用 | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs) | [`PlaylistLampViewerViewModelTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistLampViewerViewModelTests.cs): 渡し済みの旧結果で、日付・表示・操作が巻き戻らないこと。 |
| 初回・表示中の取得失敗、複数ウィンドウ、対象の削除、再読込み | [ウィンドウの管理](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindowManager.cs) | [`PlaylistLampViewerWindowManagerTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistLampViewerWindowManagerTests.cs): 未取得の表示と別ウィンドウの独立、初回の削除案内、表示中の対象だけの閉鎖、同じ対象の再読込みで閉じないこと。 |
| 閉鎖の先行、初回の取消し、開始済みの日付取得の終端 | [ウィンドウの管理](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindowManager.cs) | 同クラス: 実際の閉鎖、所有集合からの除去、UIの応答、受理済み処理の回収、遅い完了でも再表示しないこと。 |
| 統計3群と補助行、内容幅・群単位の折返し、言語・テーマ | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs)、[画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml) | `PlaylistLampViewerViewModelTests.Viewer_statisticsKeepOwnershipPlayAndScoreGroupsSeparateFromSupplementaryDetails`: 値の群と書式。`PlaylistLampViewerWindowPresentationTests.Viewer_statisticsUseContentWidthsAndWrapWholeGroupsWhileTrackingLanguageAndTheme`: 既定の日本語・最小幅・大きな数値・長い翻訳・明暗テーマの配置と、言語の切替後の文言・アクセシビリティ名。スコア利用不可時の表示は既存のLR2表示とViewModelのケースが分担する。 |
| 既存の区間の言語追従と、参照・選択・モード・集計結果の維持、破棄後の通知停止 | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs) | 同 `Viewer_statisticsUseContentWidthsAndWrapWholeGroupsWhileTrackingLanguageAndTheme`: 言語の変更前に捕捉した区間のボタン・凡例が、同じ参照のまま現在の言語の文言・割合・小さい正の値の表現へ更新され、取得・集計・一覧の要求回数とコレクションが変わらないこと。閉鎖と破棄の後は通知しないこと。同梱辞書の整合は `LocalizationResourceParityTests` が担う。 |
| 表示モード、尺度、幅が変わらない行のラベル、切替の副作用なし | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs)、[画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml.cs) | 同 `Viewer_countModeUsesAllNormalFoldersAndPreservesProjectionUntilDataRefresh`: 100・50・0件と、未実体化の実名 `[NO SONG]` の最大行で、左右の尺度、参照・選択・スクロールの保持、切替で捕捉・集計・一覧の要求がないこと。最大行の区間からの要求と、日付の更新後の尺度の再計算。 |
| 日付部品の言語・テーマ追従と、表示の更新の副作用なし | [共通日付表示](../../../BeMusicSeeker/Views/DatePickerPresentation.cs)、[言語サービス](../../../BeMusicSeeker/ViewModels/ResourceService.cs)、[画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml) | `PlaylistLampViewerWindowPresentationTests.Viewer_datePresentationTracksApplicationCultureWithoutChangingTheQuery`: 言語ごとの月日・週の開始、非Gregorianの追加言語、フォーカス喪失・カレンダーを開く操作、開いたカレンダーの維持と、取得回数が変わらないこと。同じ日付・最新の再取得の抑止は `PlaylistLampViewerSessionTests.Session_queryUpdateKeepsTheLatestImmutableQuery` が担う。 |
| 日付の変更・最新への復帰・LR2・利用不可、複数ウィンドウとモードの独立 | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs)、[画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml.cs) | [`PlaylistLampViewerWindowPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs): 操作による再取得と表示モードの保持、全0件・フォルダなし、ラベルの収容。 |
| 区間からの移動、現在の対象の再解決、実名と特殊ノードの区別 | [ワークスペース](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistLampViewer.cs)、[詳細入力](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailSource.cs) | [`PlaylistWorkspaceActionWorkflowTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) の `PlaylistLampNavigation_OverallAndRealNoSongFolderCommitNormalRowsAndRejectSpecialNode`: 全体のNPが三つのフォルダの所属を対象にし、実名 `[NO SONG]` を通常の選択として確定し、特殊ノードを拒否すること。[`PlaylistWorkspaceDetailRefreshTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceDetailRefreshTests.cs) の `BuildDetailSourceRows_OrdinaryRootRetainsRealFolderNamedNoSong`: 通常ルートでの同名の実所属の保持。ツリー・アクティブ化・フォーカスは [`MainWindowPlaylistWorkspaceWpfTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) / [`MainWindowPlaylistWorkspaceWpfTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `PlaylistLampNavigation` で始まるケースが担う。 |

## 関連資料

[プレイ履歴](play-history.md)、[LR2履歴スキーマ](lr2-history-schema.md)、[共通外観](../ui/appearance.md)。
