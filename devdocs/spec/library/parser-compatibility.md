# 譜面解析の互換性

## 目的と適用範囲

`ChartInfoParser` が採用するBMS・BMSONの解釈と、保存値の比較条件を定めます。一般的な形式の説明ではなく、リポジトリの検証データで固定している jbms-parser、beatoraja、songdata-updater との互換契約です。DBへの保存は解析器の責務に含めません。

軽量解析が使用リソースを抽出する規則も本書で定めます。軽量解析の入口と寿命は[譜面ファイルの読取り](chart-file-reading.md)、内蔵再生用の解析は[RibbitのBMS時刻計算](ribbit-timing.md)と[bmson再生](bmson-playback.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。機能固有の用語は次のとおりです。

| 用語 | 意味 |
| --- | --- |
| 詳細解析 | `chart_info` を作る解析。一覧登録用の軽量解析とは別の責務。 |
| 譜面文字列 | 時刻・ノートなどを規定の順で文字列化した `ChartString`。ファイルの元の文字列ではない。 |
| 譜面ハッシュ | 譜面文字列から求める `charthash`。ファイル全体のSHA-256とは異なる。 |
| 解析の診断 | 解析を継続できる不正や衝突の情報。最終的な解析失敗とは区別する。 |

## 仕様

### 互換性の対象

BMSは `BMSDecoder`、`Section`、`BMSModel`、`TimeLine`、BMSONは `BMSONDecoder` の解釈を基準にします。情報の集計は `SongInformation`、譜面文字列は `BMSModel.toChartString()` に対応します。

数値の読取りはJDK 17の `Double.parseDouble`、浮動小数点数の文字列化は参照DBを生成したJDK 21の `Double.toString` に合わせます。異なるJava版の文字列化を、同じ期待値として混ぜません。RANDOMの過去の選択結果は復元できないため、参照DBとの値の差を許容します。

### BMSの文字コードと行の解釈

通常の詳細解析はMS932系を既定として読み、表示用の `maintenance.encoding` を使いません。検証用の低水準の引数 `encodingName` は残しますが、通常の補完解析からは null を渡します。

予約語は単なる空白区切りではなく、参照実装の位置に依存する切出しに合わせます。`#DIFFICULTY 2`、`#DIFFICULTY=2`、古い空白なしの `#TITLExxx`、添字付きのBPM・STOP・SCROLLを区別して処理します。

チャンネル行は `#mmmcc\s*:(.*)` の形を許容しますが、コロンの後をトリムしません。コロンのない行も同じ切出しで受理し、行全体をチャンネルデータとして扱います。後続を二文字ずつ切り、最後の一文字は無視します。たとえば `#01301: 100` の先頭は空白と `1` の組であり、空白を取り除いて有効なノートへ変えません。

### 軽量解析の使用リソース

軽量解析のリソース抽出は、[共通モデルの不変な結果](chart-model.md#譜面情報とリソース保守)へ確定します。BMSは実際に採用した復号本文、BMSONはJSON文字列を通常リソースの原文とし、用途・分類・照合キーを入口で一度だけ決めます。LR2のCP932診断は別の保守の事実へ集約し、通常リソースに混ぜません。これらは詳細解析の戻り値・解釈とは別の保証です。

#### 使用要求の集め方

健全性と導入先推定は、定義行の全数ではなく、使用されるIDから解決したファイルを要求します。同じ種別・正規化済みの相対キーを複数のIDが参照していても一件です。詳細解析のノート配置やRANDOMの選択結果は、軽量解析へ持ち込みません。

| 形式・記述 | 使用要求の条件 |
| --- | --- |
| BMS/PMSの01、1x/2x、3x/4x、5x/6x | 0でない音声ID。LNOBJの定義先も音声要求へ含める。 |
| BMS/PMSのDx/Ex | 0でない地雷があり、WAV00が定義されている場合だけ地雷音を要求する。データ値はダメージであり、音声IDではない。 |
| BMS/PMSの04/06/07/0A | 0でない画像IDからBMPの定義を解決する。定義されたBMP00は初期のミス画像として使用する。 |
| BMS/PMSのRANDOM/IF | 実行条件と到達可能性を評価せず、成立しないIFや入れ子を含む、記述された全分岐の使用参照を集める。 |
| BMS/PMSのBASE62 | 音声・画像IDの大文字小文字を区別する。チャンネルの識別そのものの解釈とは分ける。 |
| BMSONのsound/key/mineチャンネル | ノートを持つチャンネルの音声を対象にする。preview_musicの既存の明示参照も維持する。 |
| BMSONのBGA/LAYER/POORイベント | 数値IDで参照されたヘッダーを対象にし、参照されないヘッダーは要求しない。 |
| 補助画像 | STAGEFILE・BANNER・BACKBMPなどの既存の明示参照を、別の用途として維持する。 |

同じ使用IDの定義先は、条件の有無や行の順序を問わずすべて要求へ含め、同じ種別・正規化済みのパスの重複を除きます。LNOBJによる除外は行いません。これは詳細なノート解析や、再生側の条件分岐・LNOBJの解釈とは別の契約です。

WAV00・BMP00の省略や、定義のない使用WAV・BMPのIDは要求に含めません。BMSONのBGA/LAYER/POORイベントも同じです。対象外の定義と未定義のIDは、健全性・不足の警告・導入の自動設定を変えません。名前付きで照合できない参照と取得の失敗を、正常な要求0件へ読み替えません。未使用の定義の欠損や親参照は、使用要求の完全性を妨げません。

#### 原文の分類

通常の要求は、正規化前の原文と用途から次の三つに分類します。

| 分類 | 条件 | 扱い |
| --- | --- | --- |
| 対象外 | 空・空白だけ、最終要素が空・`.`・`..`、`.wav` のように最後のドットより前に名前がない値、不正なWAV・BMPの定義構文、種類が分からないBMP・BGA、BMP・BGAに付いた音声の拡張子 | 分母・存在の照合・自動化の禁止のいずれにも含めない。 |
| 照合可能 | 名前があり、通常の相対パスとして照合できる値。`stem..wav`、`.hidden.wav`、`foo.v2.wav`、親の名前にドットがある通常の相対パスを含む | 照合キーで存在を確認する。 |
| 名前付き非照合 | 名前があるが、不正なパス、ルートからのパス、独立した `..` を含む値 | 架空の検索キーを作らず、個別の未充足として数える。 |

音声の用途では、拡張子なし・末尾がドット・未知の拡張子の値も音声（`Audio`）として扱います。

任意画像は役割ごとに最初の空白でない値を採用してから分類し、対象外や欠損でも後続の定義へ繰り上げません。後続の定義は診断専用であり、未使用を含めて取得する場合も通常の要求へ戻しません。保存するBMSの画像三項目のCP932の値は[読取り仕様](chart-file-reading.md#リソース参照と文字コード)に従います。

#### 用途ごとの集合

パッケージの発見・グループ化は、分類済みの全定義について照合可能なものの存在確認と共通率を使います。対象外・名前付き非照合・後続の任意画像は、その根拠へ戻しません。LR2のCP932診断は、未使用・対象外・後続の任意画像も含む全原文から、独立して集約します。全定義と使用IDの集合は永続化しません。

地雷音の用途は[参照解析器](https://github.com/exch-bms2/jbms-parser/blob/master/src/bms/model/Section.java)、BMP00と0Aは[プレイヤーの仕様](https://github.com/lifthrasiir/angolmois/blob/master/INTERNALS.md)、BMSONの音声・BGAイベントは[形式仕様](https://bmson-spec.readthedocs.io/en/master/doc/index.html)を根拠とします。使用の判定は構文上の参照の収集であり、すべてのプレイヤーの実行結果を再現することは保証しません。

### 整数と浮動小数点数

整数はJavaの `Integer.parseInt` 相当とし、文字列全体が有効である必要があります。`12abc`、`12.5`、桁あふれを、先頭の整数として受け入れません。Unicodeの十進数字は受け入れ、`４` は4、`2８` は28になります。

`#DEFEXRANK` は判定幅にだけ使います。`chart_info.exlevel` は `#EXLEVEL` の最後の有効な整数値で、未定義なら0です。

浮動小数点数も末尾の不要な文字を認めません。Javaと同様に前後のU+0020以下の文字を除き、符号、指数、`NaN`、`Infinity`、16進の浮動小数点、`f/F/d/D` の接尾辞を扱います。多桁の数値は正確な比から最近接偶数への丸めを行い、二進64ビットの値を一致させます。

| 対象 | 数値の変換 |
| --- | --- |
| BMS | `#BPM`、添字付きのBPM・STOP・SCROLL、`#TOTAL`、小節長に、JDK 17互換の変換を適用する。 |
| BMSON | JSONの元の数値文字列から、`info.init_bpm`、`info.total`、BPM、STOP、SCROLL、地雷ダメージを読み直す。JSONライブラリが先に変換したdoubleを、そのまま正本にしない。 |
| 出力 | 速度変化、譜面文字列、TOTAL、地雷ダメージは、JDK 21互換で文字列化する。 |

たとえば `114.15384615384615384615384615` は `114.15384615384616`、`131.4889812233735` はその値として扱います。1ビットの丸めの差が速度変化と譜面ハッシュへ連鎖するため、表示上の近似的な一致だけで済ませません。

#### TOTALとBPMの範囲

`total` は表示用の有効値、`total_defined` は明示の有無です。未定義は致命的な失敗ではなく、参照実装の既定の計算を使います。BMSONのTOTALは、明示した正の値だけを定義済みとします。省略・0・負の値・null も受理して未定義とし、有効なTOTALには既定の計算を使います。明示した100は、既定の計算と同じ有効値を持つ定義済みの値です。その他の正の値は百分率の倍率として適用し、表示側の補正は加えません。

BPMの最小・最大には初期BPMも含め、詳細情報では小数を保持します。整数の範囲を超える値はJava側の保存結果に合わせて制限し、LR2・参照DBとの比較では整数部を使います。先頭のBPM変更が有効な場合、未定義の初期BPMが0のまま最小の候補に残ることがあります。

### 基数と条件分岐

`#BASE 62` は添字とデータの値に作用し、チャンネルの識別そのものの36進の解釈とは混同しません。短いBPMチャンネル `03` は、元の二文字を36進で解釈し直します。地雷ダメージは最終的なBASEで解釈した値を保持し、短いBPMと同じ変換へは統一しません。

RANDOMは、すべて分岐1を最初に試します。使用フラグは行の見た目ではなく、引数が有効な整数として解釈できることを条件にします。`#RANDOM4` は予約語として見つかっても有効な引数がないため、RANDOMの使用フラグを立てません。

`#ENDIF` と `#ENDRANDOM` は、引数のない指令として行頭で認識します。`#ENDRANDOM` がなくても、IFの除外状態が閉じていれば後続の通常のデータを読みます。

初期BPMの不正、極端な時刻・分布、異常に長い分岐に限り、RANDOMの別の候補を試せます。候補の順は、すべて1、すべて `min(2,max)`、同じく3、同じく4、すべて最大値、SHA-256・MD5を使う候補です。各値は1から分岐数までに制限します。途中の失敗は警告ログへ出さず、最終的な失敗だけを報告します。

参照実装は `#SWITCH`、`#CASE`、`#SKIP`、`#ENDSW` を独立した条件分岐として処理しません。別のIFによる除外がない限り内部の通常のデータを読み、独自の条件スタックは追加しません。

### モードとレーン

モードは明示値だけでなく、使用チャンネルから5鍵から7鍵、片側から10鍵・14鍵へ昇格します。5鍵、7鍵、10鍵、14鍵、9鍵でレーンの割当てが異なります。特に7鍵・14鍵では、スクラッチと鍵盤の内部の順を、画面の表示順やチャンネルの見た目の順へ置き換えません。

### 時刻と小節

時刻はdoubleのマイクロ秒として累積し、各 `TimeLine` を作るときだけlongへゼロ方向に切り捨てます。ミリ秒の値は、マイクロ秒を1000で割って得ます。ミリ秒で累積したり、四捨五入したりしません。

```text
次の厳密時刻 = 前の厳密時刻 + 前の停止時間
             + 240000.0 × 1000 × (次の小節座標 - 前の小節座標) / BPM
```

小節の開始は前の小節の長さを足して求め、その小節内は `小節開始 + 位置 × 当該小節の長さ` とします。小節長を隣接する開始座標の差から逆算せず、解析した元の長さを別に保持します。

#### 要求順と生成

各小節は、開始線、制御イベント、ノートの順に要求します。制御イベントは小節内の Position、SCROLL・BPM・STOPの優先順、入力の Sequence で整列し、ノートは元のチャンネル順・組の順で処理します。0や負の小節長でも、この要求順を物理座標の昇順へ置き換えません。完成したタイムラインだけを座標順に取り出し、時刻の安定した順序へ並べます。

時刻はタイムラインを要求したときに初めて生成し、生成済みの厳密な直前の座標を参照します。直前の座標がなければ、生成済みの最小の座標を使います。まだ生成していない後方の座標は計算に使いません。±0は CompareTo で同じキーとし、最初に要求した値を生成時の Section として保持します。有限でない座標も、事前の索引化だけで拒否せず、そのタイムラインを生成する段階で既存の時刻の検査を通します。座標の索引化の採用理由は[詳細解析の座標索引と出力の判断](../../decisions/chart-info-position-index.md)を参照します。

`#` と三桁の数字で始まり7文字以上ある行は、チャンネルとして壊れていても最大小節を更新します。`#187だいすき`、`#0736:bd` などで空の末尾小節が生まれ、演奏長を伸ばさずに速度変化の末尾の時刻だけが伸びる場合があります。

#### 範囲の上限

24時間を一律の上限にはしません。ミリ秒のint範囲や、分布配列の整数範囲を超える入力は解析失敗とし、RANDOMなら別の分岐の候補に進めます。分布配列を実際に割り当てるときに起きる `OutOfMemoryException` は資源不足として伝播し、回復可能な入力失敗へ包んだり、別の分岐へ進めたりしません。

### ロングノートと地雷

`#LNOBJ` は同じレーンを後ろ向きに探し、直前の通常ノートを始点に置き換えるか、閉じていないロングノートに終点を付けます。直前のnullでないノートが地雷か対応済みのLNなら、そこで探索を止め、それより古い通常ノートへは進みません。対応できなければLNOBJの終端を配置せず、既存のノートを維持します。

ロングノート専用チャンネルは、始点と終点を交互に処理します。既存の範囲の判定は両端を含みます。

- 始点に通常ノートがあれば置き換え、異なる音の通常ノートは背景音へ移します。
- 終点から始点まで後ろ向きに調べて途中のノートを除き、通常ノートなら背景音へ移します。
- 生成済みのタイムラインの Section が始点と一致すれば対を作るため、始点のセルが上書き済み・空でも閉じられます。
- 始点が終点以上の専用LNでは、終点より前の削除を行っても対を作らず、未閉鎖の状態を維持します。閉じていない始点は、最後にその所有者の現在のセルを除去します。
- 完成したLNの両端を含む被覆は、端点が後から上書き・削除されても残ります。

既存のロングノートの内側に専用チャンネルが現れた場合は、通常の始点として数えず、参照実装の特別な開始状態を次のチャンネルで解除します。無条件に対を作って、閉じていないノートを残すことはしません。

地雷は、同じ時刻・同じレーンにノートがある場合と、既存のロングノートの範囲内には配置しません。地雷ダメージは最終的なBASEで解釈した値を使います。

### 分布・密度・速度・主BPM

ノートとLNを確定した後に、総数・種別ごとの数・タイムライン別のノート数・BPMの範囲・特徴・BGA・演奏長を一回で要約し、難易度と既定のTOTALを確定します。分布と主BPMはこの要約を共有します。

分布は `lastTime / 1000 + 2` 個の秒区間と7種の値を持ち、各ノートを参照実装のミリ秒から秒へ変換して数えます。ロングノートは始点から終点の秒までを、密度用の区間へ差分配列の累積で加えます。通常のLN形式の終点は、ノート数から除きます。

境界の位置は `totalNotes * (1 - 100 / total)` を通過する位置です。`density` は対象の秒区間の平均、`peakdensity` は演奏ノートの秒間の最大、`enddensity` は境界以後の移動区間の最大です。ノート数、LN、時刻のずれを、別の密度の補正で打ち消しません。

`speedchange` は初期BPMと時刻0で始め、各時点の `bpm * scroll` を記録します。停止中は速度0とし、変化の時刻は参照実装のミリ秒値です。最後の記録と最終イベントの時刻が異なれば、末尾を補います。時刻、イベント数、数値の文字列表現を分けて検証します。

`mainbpm` は滞在時間ではなく、BPMごとの `getTotalNotes()` の合計で選びます。同点の場合は、参照実装のJava `HashMap<Double, Integer>` の走査順に合わせます。

### 譜面文字列とハッシュ

`charthash` は `BMSModel.toChartString()` 相当の文字列のSHA-256です。判定幅、TOTAL、LNモード、時刻、BPM・STOP、小節線、レーンの状態、地雷、LNの記号と音声長が影響します。BPMは入力の元の文字列ではなく、計算済みのdoubleを文字列化します。

整数はInvariantCultureのASCII数字と負号、doubleはJavaの書式で書き、OSの地域設定によらず同じ文字列とハッシュを作ります。保存済みの成功行の再利用と通常の補完は[解析器の版による最新性](chart-info.md#最新性の判定)に従い、地域設定に依存した旧いハッシュだけを理由に再解析しません。

`ParseBytesDetailed` は、既定では同じUTF-8の出力から検証用の文字列を保持します。末尾の引数 `retainChartString: false` を指定した結果の `ChartString` はnullです。`Parse`・`ParseBytes`・`ChartInfoBuildService.EvaluateSnapshot` は保持を要求せず、行・診断・ハッシュは保持する場合と一致します。出力の方式は[詳細解析の座標索引と出力の判断](../../decisions/chart-info-position-index.md#数値互換と出力)を参照します。

LNの記号と音声長は、文字列の単純な連結ではなく、記号の整数値と長さの加算に対応します。BMSONの音声の切出しの開始・長さも反映します。差異の調査では `tools/chartstring-dump` と `ChartInfoParseResult.ChartString` を行単位で比較し、生成に使ったJava版を明示します。

### BMSONのJSONとイベント

JSON.NETを使います。無効なJSONは失敗とし、未知の項目は無視し、重複したキーは後の値を採用します。

#### 旧キーの正規化

基本値と詳細値は、共通のJSON境界で旧キーを正規化します。次の別名を受理し、受理をversionで制限しません。

| 旧名 | 現代名 |
| --- | --- |
| `info.initBPM` | `init_bpm` |
| ルートの `bpmNotes` | `bpm_events` |
| ルートの `stopNotes` | `stop_events` |
| ルートの `soundChannel` | `sound_channels` |
| BPMイベントの `v` | `bpm` |
| STOPイベントの `v` | `duration` |

各原文オブジェクトに現代名が存在すれば、その値がnull・空配列・不正でも旧名へ戻りません。DTOが受け付ける現代名の大文字小文字の違いも存在として扱い、その綴りは変更しません。

versionが `0.21` の入力、または version が省略され、旧ルートキーか最後の `info` の `initBPM` がある入力は、`info.resolution` の値をDTOへ変換する前に240へ固定します。旧形式の判定には再生用の変換と同じ最後の `info` を使いますが、DTOへの設定は各原文オブジェクトの順序を保ちます。旧STOPの `v` はpulseの長さとして扱い、単位の換算は加えません。

構文を先に確認してから、原文のキーと対象の `resolution` だけを置き換えます。壊れたJSONを修復して受理しません。オブジェクトや重複したプロパティは集約せず、JSON.NETの既存の設定順、元の数値字句、既定値、既存の受理範囲を保ちます。再生用の解析器が持つその他の時刻処理や入力制限は、この境界へ移しません。

#### 既定値とイベントの統合

欠けたオブジェクト・配列は、次の既定値で補います。

| 項目 | 既定値 |
| --- | --- |
| `info`、`bga` | 空の対応オブジェクト |
| `lines`、`bpm_events`、`stop_events`、`scroll_events`、`sound_channels`、`mine_channels`、`key_channels` | 空配列 |
| `mode_hint` | `beat-7k` |
| `judge_rank`、`total` | 100 |
| `resolution` | 240 |
| SCROLLの `rate` | 1.0 |

整数の項目は四捨五入せず、数値や数値文字列を受ける場合もゼロ方向に切り捨てて整数にします。同じy座標では、SCROLL、BPM、STOPの順に統合し、STOPの時間にはその時点のBPMを使います。負のBPMやSTOPは直ちに失敗にせず、解析の診断として扱います。

新しい時点へBPMと厳密な時刻は引き継ぎますが、SCROLLは引き継ぎません。新しい `TimeLine` のSCROLLは1.0であり、指定がある時点だけ上書きします。

BGAの使用は、BMSのBGA・LAYERチャンネルか、BMSONの `bga_events` に実際のイベントがある場合だけ1です。リソースの定義だけでは使用とみなしません。BMSONの `bga_events`・`layer_events`・`poor_events` の座標の扱いは[BMSONの画像イベント](chart-info.md#bmsonの共通表示と画像イベント)に従います。

### レベルと難易度

`level` は未定義・不正をNULLで表し、別の定義フラグを持ちません。BMSの `#PLAYLEVEL 12` は12、`12abc` はNULL、全角の `４` は4です。BMSONの欠落・nullはNULL、明示した0は0です。

`difficulty` は表示・並べ替え用の有効値、`difficulty_defined` は明示の有無です。BMSの有効な0以外の指定は明示値、0・欠落・不正は推定です。BMSONは常に推定です。

推定は、副題の `beginner/normal/hyper/another/insane/leggendaria`、題名と副題、ノート数の順に行います。ノート数では、250未満を1、600未満を2、1000未満を3、2000未満を4、それ以上を5とします。

### 失敗・診断・時間上限

ファイルの読取り不能、無効なBMSON、最終的な解析不能、予期しない例外、時間超過だけを通常の警告ログへ出します。既知のコマンドの不正、未定義の参照、数値やチャンネルの不正、LN・地雷の衝突は、原則として解析の診断に留めます。

一譜面の解析時間の上限は60秒で、導入時と補完時で同じです。時間超過は解析失敗として扱います。解析器の版は、結果の意味が変わる場合だけ上げます。ハッシュを作れた入力の解析失敗と時間超過は保存できますが、内部故障・資源不足や診断の出力先の故障を、恒久的な解析不能として保存しません。`chart_info` には成功した行だけを保存します。BMSのハッシュ対応と、BMSONのSHA-256優先の区別は維持します。保存と最新性の扱いは[譜面情報](chart-info.md#失敗)に従います。

### 参照データとの比較

比較の母集団は、beatorajaの `song` と `information` の共通部分です。RANDOMによる値の差、BMSONがMD5を持たないこと、レベルの0とNULLによる未定義の表現を区別します。BPMは整数部、その他のdoubleは所定の許容誤差で比較します。

`chart_info_production_diff` と `chart_info_production_latest_diff` の検証データは、基本項目、時刻・分布・密度、速度、譜面ハッシュを検証します。追加のデータは既存のデータと重複させて複製しません。Java版による文字列の差を期待値の根拠とする場合は、検証データに対応した版で比較します。過去の差分の件数や処理時間を、現行版の無条件の保証にはしません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 文字コード、構文、条件分岐、LN、BMSON、難易度 | [`ChartInfoParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoParser.cs) | [`ChartInfoParserBehaviorTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoParserBehaviorTests.cs)、[`ChartInfoParserBehaviorTests`（互換性テストの固定入力・外部ツール）](../../../tests/BeMusicSeeker.Compatibility.Tests/ChartInfo/ChartInfoParserBehaviorTests.cs) |
| 旧BMSONの別名、原文オブジェクトごとの現代名の優先、固定のresolution、数値字句 | [`BmsonJsonParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/BmsonJsonParser.cs)、[`BmsonLegacyKeyNormalizer`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/BmsonLegacyKeyNormalizer.cs) | `ChartInfoParserBehaviorTests` の `ParseBmson_LegacyAliasesMatchModernTimingAndHash` は、120→240BPM・STOP 240 pulse・3ノート・最終1000msと、同じ意味の入力の譜面文字列・ハッシュの一致を確認する。`BmsonJsonParser_ModernNamesWinWithinEachOriginalObject` は順序の逆転・null・空配列・大文字小文字・重複と設定順を、`BmsonJsonParser_ModernInvalidValuesAndMalformedLegacyResolutionRemainFatal` は不正な値・構文の失敗を、`ParseBmson_RawJsonDoublesUseJavaParseDoubleRounding` は新旧3形式の丸めを分担する。 |
| 軽量解析の使用参照、重複キー、特殊なID、全行の収集、未定義の除外 | [`BmsChartFileParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/BmsChartFileParser.cs)、[`BmsonChartFileParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/BmsonChartFileParser.cs) | [`ChartResourceSnapshotTests`](../../../tests/BeMusicSeeker.Core.Tests/Resources/ChartResourceSnapshotTests.cs)、[`BmsonSongParserTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/BmsonSongParserTests.cs) が、実際の入力から使用要求と、定義済みで照合できない参照を確認する。`ParseSnapshot_CollectsAllLinesWithoutEvaluatingConditions` は成立しないIF・入れ子の収集を、`ParseSnapshot_LnobjDefinitionIsRequiredForEveryAudioChannel` はLNOBJで除外しない音声要求を確認する。詳細なノート解析とは分担する。 |
| 実際に採用した本文の三分類、任意画像の先頭の採用、CP932診断の分離 | `BmsChartFileParser`、`BmsonChartFileParser`、`ChartResourceReference` | `ChartResourceSnapshotTests` が元のバイト列と用途・使用IDから要求数を、`ChartResourceDiagnosticsTests` が後続を含む全原文・CR/LF/CRLF、同じCP932本文の一回の出力と別の解釈の独立した原文を、`Lr2CompatibilityGoldenTests` がCP932の三つの事実を確認する。 |
| 生成済みの直前の座標、単調でない座標、LNの探索・被覆、解析の診断の順、文字列の保持の有無 | [`BMS入力と疎な操作`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoParser.BmsTimeline.cs)、[`共通集計とUTF-8出力`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoParser.Output.cs) | [`ChartInfoParserBoundaryTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoParserBoundaryTests.cs)。基準版の小さな入力の結果、全行のdoubleのビット、解析の診断を維持し、同じバイト列を ja-JP/en-US/de-DE/fr-FR/th-TH/ar-SA と負号を変えたカルチャで解析する。負の時刻 `-3333` / `-2000` を含む独立した固定値の譜面文字列と、そのUTF-8から計算したSHA-256を照合する。文字列の保持の有無による行・診断・ハッシュの一致も分担する。 |
| Java互換の数値の読取りと文字列化 | [`JavaDoubleParserJdk17`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/JavaDoubleParserJdk17.cs)、[`JavaDoubleToStringJdk21`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/JavaDoubleToStringJdk21.cs) | [`ChartInfoParserBehaviorTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoParserBehaviorTests.cs)、[`ChartInfoParserBehaviorTests`（互換性テストの固定入力・外部ツール）](../../../tests/BeMusicSeeker.Compatibility.Tests/ChartInfo/ChartInfoParserBehaviorTests.cs)、[`ChartInfoProductionCompareTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoProductionCompareTests.cs) |
| 参照DBとの値の比較、RANDOMと未定義の扱い | [`ChartInfoCompareRunner`](../../../tools/chart-info-compare/ChartInfoCompareCore.cs) | [`ChartInfoProductionCompareTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoProductionCompareTests.cs) |
| 時間の上限、共通の評価、保存と公開の分離 | [`ChartInfoBuildService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoBuildService.cs)、[`CatalogMutationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMutationOwner.cs) | [`ChartInfoBackfillStorageTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoBackfillStorageTests.cs)、[`ChartInfoInlineHydrationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs)、[`ChartInfoInlineHydrationTests`（Windows。接続・外部入力）](../../../tests/BeMusicSeeker.Windows.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs)、[`ChartInfoInstallFailureRetryTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoInstallFailureRetryTests.cs)、[`ChartInfoInstallFailureRetryTests`（Windows。接続・外部入力）](../../../tests/BeMusicSeeker.Windows.Tests/ChartInfo/ChartInfoInstallFailureRetryTests.cs) |
| BMSONのTOTALの未定義と有効値 | [`ChartInfoParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoParser.cs) | `ChartInfoParserBehaviorTests.ParseBmson_TotalDefinitionPreservesEffectiveValue` が、省略・100・0・負の値・null・正の値の倍率を実際の解析で比較する。 |

## 関連資料

[詳細解析の座標索引と出力の判断](../../decisions/chart-info-position-index.md)、[譜面情報の保存](chart-info.md)、[ファイル読取り](chart-file-reading.md)、[検証データの利用条件](../../../BeMusicSeeker.Tests/TestData/README.md)、[テスト実行](../development/testing.md)を参照します。
