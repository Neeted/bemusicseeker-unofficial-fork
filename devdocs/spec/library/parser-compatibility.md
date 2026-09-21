# 譜面解析の互換性

## 目的と適用範囲

`ChartInfoParser` が採用するBMS・BMSONの解釈と、保存値の比較条件を定めます。一般的な形式の説明ではなく、リポジトリの検証データで固定しているjbms-parser、beatoraja、songdata-updaterとの互換契約です。DBへの保存は解析器の責務に含めません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 詳細解析 | `chart_info` を生成する解析。一覧登録用の軽量解析とは別の責務 |
| 譜面文字列 | 時刻・ノートなどを規定の順で文字列化した `ChartString`。ファイルの元の文字列ではない |
| 譜面ハッシュ | 上記文字列から求める `charthash`。ファイル全体のSHA-256とは異なる |
| 診断 | 解析を継続できる不正や衝突の情報。最終的な解析失敗とは区別する |

## 仕様

### 互換性の対象

BMSは `BMSDecoder`、`Section`、`BMSModel`、`TimeLine`、BMSONは `BMSONDecoder` の解釈を基準にします。情報集計は `SongInformation`、譜面文字列は `BMSModel.toChartString()` に対応します。

数値の読取りはJDK 17の `Double.parseDouble`、浮動小数点数の文字列化は参照DBを生成したJDK 21の `Double.toString` に合わせます。異なるJava版の文字列化を、同じ期待値として混ぜません。RANDOMの過去の選択結果は復元できないため、参照DBとの値差分を許容します。

### BMSの文字コードと行の解釈

通常の詳細解析はMS932系の既定で読み、表示用の `maintenance.encoding` を使いません。低水準の検証用 `encodingName` は残しますが、通常の補完解析からはnullを渡します。

予約語は単なる空白区切りではなく、参照実装の位置依存の切出しに合わせます。`#DIFFICULTY 2`、`#DIFFICULTY=2`、古い無空白の `#TITLExxx`、添字付きのBPM・STOP・SCROLLを区別して処理します。

チャンネル行は `#mmmcc\s*:(.*)` の形を許容しますが、コロンの後をトリムしません。後続を二文字ずつ切り、最後の一文字は無視します。例えば `#01301: 100` の先頭は空白と `1` の組であり、空白を取り除いて有効ノートへ変えません。

### 整数と浮動小数点数

整数はJavaの `Integer.parseInt` 相当とし、文字列全体が有効である必要があります。`12abc`、`12.5`、桁あふれを先頭の整数として受け入れません。Unicodeの十進数字は受け入れ、`４` は4、`2８` は28になります。

`#DEFEXRANK` は判定幅にだけ使います。`chart_info.exlevel` は `#EXLEVEL` の最後の有効な整数値、未定義なら0です。

浮動小数点数も末尾の不要文字を認めません。Java同様に前後のU+0020以下を除き、符号、指数、`NaN`、`Infinity`、16進浮動小数点、`f/F/d/D` の接尾辞を扱います。多桁の数値は正確な比から最近接偶数丸めを行い、二進64ビットの値を合わせます。

| 対象 | 数値変換 |
| --- | --- |
| BMS | `#BPM`、添字付きBPM・STOP・SCROLL、`#TOTAL`、小節長へJDK 17互換の変換を適用する |
| BMSON | JSONの元の数値文字列から `info.init_bpm`、`info.total`、BPM、STOP、SCROLL、地雷ダメージを読み直す。JSONライブラリが先に変換したdoubleをそのまま正本にしない |
| 出力 | 速度変化、譜面文字列、TOTAL、地雷ダメージはJDK 21互換で文字列化する |

例えば `114.15384615384615384615384615` は `114.15384615384616`、`131.4889812233735` はその値として扱います。一ビット単位の丸め差が速度変化と譜面ハッシュへ連鎖するため、表示上の近似一致だけで済ませません。

`total` は表示用の有効値、`total_defined` は明示の有無です。未定義は致命的な失敗ではなく参照実装の既定計算を使います。BPMの最小・最大には初期BPMも含め、詳細情報では小数を保持します。整数範囲を越える値はJava側の保存結果に合わせて制限し、LR2・参照DBとの比較では整数部を使います。先頭のBPM変更が有効なら、未定義の初期BPMが0のまま最小候補に残ることがあります。

### 基数と条件分岐

`#BASE 62` は添字とデータの値に作用します。チャンネル識別自体の36進解釈と混同しません。短いBPMチャンネル `03` と地雷ダメージでは、62進の値を文字列へ戻して36進で再解釈する参照実装の処理を維持します。

RANDOMは全て分岐1を最初に試します。使用フラグは行の見た目ではなく、引数が有効な整数として解釈されることを条件にします。`#RANDOM4` は予約語として見つかっても有効な引数がなく、RANDOMの使用フラグを立てません。

`#ENDIF` と `#ENDRANDOM` は引数のない指令として行頭で認識します。`#ENDRANDOM` がなくても、IFの除外状態が閉じていれば後続の通常データを読みます。

初期BPM不正、極端な時刻・分布、異常に長い分岐は、RANDOMに限り別候補を試せます。候補順は全て1、全て `min(2,max)`、同3、同4、全て最大値、SHA-256・MD5を使う候補です。各値は1から分岐数までに制限します。途中の失敗を警告ログへ出さず、最終失敗だけを報告します。

参照する処理では `#SWITCH`、`#CASE`、`#SKIP`、`#ENDSW` を独立した条件分岐として処理しません。別のIF除外がない限り内部の通常データを読み、独自の条件スタックを追加しません。

### モードとレーン

モードは明示値だけでなく使用チャンネルから、5鍵から7鍵、片側から10鍵・14鍵へ昇格します。5鍵、7鍵、10鍵、14鍵、9鍵でレーン割当が異なります。特に7鍵・14鍵はスクラッチと鍵盤の内部順を、画面の表示順やチャンネルの見た目の順へ置き換えません。

### 時刻と小節

時刻はdoubleのマイクロ秒として累積し、各 `TimeLine` を作るときだけlongへゼロ方向に切り詰めます。ミリ秒値はマイクロ秒を1000で割って得ます。ミリ秒で累積したり四捨五入したりしません。

```text
次の厳密時刻 = 前の厳密時刻 + 前の停止時間
             + 240000.0 × 1000 × (次の小節座標 - 前の小節座標) / BPM
```

小節開始は前小節の長さを足して求め、その小節内は `小節開始 + 位置 × 当該小節の長さ` とします。小節長を隣接する開始座標の差から逆算せず、解析した元の長さを別に保持します。

イベントの順序は小節座標を基準とし、時刻だけで再並べ替えしません。負のBPM・STOP・SCROLLがある入力でも、参照実装の集計順を維持します。

`#` と三桁数字で始まり7文字以上ある行は、チャンネルとして壊れていても最大小節を更新します。`#187だいすき`、`#0736:bd` などで空の末尾小節が生まれ、演奏長を伸ばさずに速度変化の末尾時刻だけが伸びる場合があります。

24時間を一律の上限にしません。ミリ秒のint範囲や、分布配列の整数・割当可能な範囲を越える入力は失敗にします。RANDOMなら別分岐の候補に進めます。

### ロングノートと地雷

`#LNOBJ` は同じレーンを後ろ向きに探し、直前の通常ノートを始点に置き換えるか、未対応のロングノートに終点を付けます。対応できなければ診断に留めます。

ロングノート専用チャンネルは始点と終点を交互に処理します。既存範囲の判定は両端を含みます。始点に通常ノートがあれば置き換え、異なる音の通常ノートは背景音へ移します。終点から始点まで後ろ向きに調べ、途中のノートを除き、通常ノートなら背景音へ移します。始点が実際に見つかった場合だけ対を作り、未閉鎖の始点は最後に除去します。

既存のロングノート内に専用チャンネルが現れた場合は、通常の始点として数えず、参照実装の特別な開始状態を次のチャンネルで解除します。無条件に対を作って未閉鎖ノートを残しません。

地雷は同時刻・同レーンにノートがある場合と、既存ロングノートの範囲内には配置しません。62進のダメージ値は基数の特殊変換に従います。

### 分布・密度・速度・主BPM

分布は `lastTime / 1000 + 2` 個の秒区間と7種の値を持ち、各ノートを参照実装のミリ秒から秒へ変換して数えます。ロングノートは始点から終点の秒まで密度用の区間を埋め、通常LN形式の終点はノート数から除きます。

境界位置は `totalNotes * (1 - 100 / total)` の通過位置です。`density` は対象となる秒区間の平均、`peakdensity` は演奏ノートの秒間最大、`enddensity` は境界以後の移動区間の最大値です。ノート数、LN、時刻のずれを、別の密度補正で打ち消しません。

`speedchange` は初期BPMと時刻0で始め、各時点の `bpm * scroll` を記録します。停止中は速度0とし、変化時刻は参照実装のミリ秒値です。最後の記録と最終イベント時刻が異なれば末尾を補います。時刻、イベント数、数値の文字列表現を分けて検証します。

`mainbpm` は滞在時間ではなく、BPMごとの `getTotalNotes()` の合計で選びます。同点時は参照実装のJava `HashMap<Double, Integer>` の走査順に合わせます。

### 譜面文字列とハッシュ

`charthash` は `BMSModel.toChartString()` 相当の文字列のSHA-256です。判定幅、TOTAL、LNモード、時刻、BPM・STOP、小節線、レーン状態、地雷、LN記号と音声長が影響します。BPMは入力の元文字列ではなく計算済みdoubleを文字列化します。

LN記号と音声長は文字列の単純連結ではなく、記号の整数値と長さの加算に対応します。BMSONの音声切出しの開始・長さも反映します。差異の調査では `tools/chartstring-dump` と `ChartInfoParseResult.ChartString` を行単位で比較し、生成に使ったJava版を明示します。

### BMSONのJSONとイベント

JSON.NETを使い、無効なJSONは失敗、未知項目は無視、重複キーは後の値を採用します。欠けたオブジェクト・配列は次の既定値で補います。

| 項目 | 既定値 |
| --- | --- |
| `info`、`bga` | 空の対応オブジェクト |
| `lines`、`bpm_events`、`stop_events`、`scroll_events`、`sound_channels`、`mine_channels`、`key_channels` | 空配列 |
| `mode_hint` | `beat-7k` |
| `judge_rank`、`total` | 100 |
| `resolution` | 240 |
| SCROLLの `rate` | 1.0 |

整数項目は四捨五入せず、数値や数値文字列を受ける場合も整数へ切り詰めます。同じy座標ではSCROLL、BPM、STOPの順に統合し、STOPの時間にはその時点のBPMを使います。負のBPMやSTOPは直ちに失敗にせず診断として扱います。

新しい時点へBPMと厳密時刻は引き継ぎますが、SCROLLは引き継ぎません。新しい `TimeLine` のSCROLLは1.0であり、指定がある時点だけ上書きします。

BGA使用は、BMSのBGA・LAYERチャンネルまたはBMSONの `bga_events` に実イベントがある場合だけ1です。リソース定義だけでは使用扱いにしません。

### レベルと難易度

`level` は未定義・不正をNULLで表し、別の定義フラグを持ちません。BMSの `#PLAYLEVEL 12` は12、`12abc` はNULL、全角 `４` は4です。BMSONの欠落・nullはNULL、明示0は0です。

`difficulty` は表示・並べ替え用の有効値、`difficulty_defined` は明示の有無です。BMSの有効な非0指定は明示値、0・欠落・不正は推定です。BMSONは常に推定です。

推定は副題の `beginner/normal/hyper/another/insane/leggendaria`、題名と副題、ノート数の順です。ノート数は250未満を1、600未満を2、1000未満を3、2000未満を4、それ以上を5とします。

### 失敗・診断・時間上限

ファイル読取り不能、無効なBMSON、最終的な解析不能、予期しない例外、時間超過だけを通常の警告ログへ出します。既知コマンドの不正、未定義参照、数値やチャンネルの不正、LN・地雷の衝突は原則として診断に留めます。

一譜面の解析時間上限は60秒で、導入時・補完時とも同じです。時間超過は解析失敗として扱います。結果の意味が変わる場合だけ解析器の版を上げます。ハッシュを作れた失敗は保存可能ですが、`chart_info` は成功した行だけを保存します。BMSのハッシュ対応とBMSONのSHA-256優先の区別は維持します。

### 参照データとの比較

比較母集団はbeatorajaの `song` と `information` の共通部分です。RANDOMの値差分、BMSONのMD5非保持、レベルの0とNULLによる未定義表現を区別します。BPMは整数部、その他のdoubleは所定の許容誤差で比較します。

`chart_info_production_diff` と `chart_info_production_latest_diff` の検証データは、基本項目、時刻・分布・密度、速度、譜面ハッシュを検証します。追加データは既存データと重複して複製しません。Java版による文字列差を期待値の根拠とする場合は、検証データに対応した版で比較します。過去の差分件数や処理時間を、現行版の無条件の保証にしません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 文字コード、構文、条件分岐、LN、BMSON、難易度 | [`ChartInfoParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoParser.cs) | [`ChartInfoParserBehaviorTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoParserBehaviorTests.cs) |
| 数値の読取りとJDK別の文字列化 | [`JavaDoubleParserJdk17`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/JavaDoubleParserJdk17.cs)、[`JavaDoubleToStringJdk21`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/JavaDoubleToStringJdk21.cs)、[`JavaDoubleToStringJdk17`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/JavaDoubleToStringJdk17.cs) | [`ChartInfoParserBehaviorTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoParserBehaviorTests.cs)、[`ChartInfoProductionCompareTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoProductionCompareTests.cs) |
| 参照DBとの値比較、RANDOMと未定義の扱い | [`ChartInfoCompareRunner`](../../../tools/chart-info-compare/ChartInfoCompareCore.cs) | [`ChartInfoProductionCompareTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoProductionCompareTests.cs) |
| 時間上限、共通評価、保存と公開の分離 | [`ChartInfoBuildService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoBuildService.cs)、[`CatalogMutationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMutationOwner.cs) | [`ChartInfoBackfillStorageTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoBackfillStorageTests.cs)、[`ChartInfoInlineHydrationTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs)、[`ChartInfoInstallFailureRetryTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInstallFailureRetryTests.cs) |

## 関連資料

[譜面情報の保存](chart-info.md)、[ファイル読取り](chart-file-reading.md)、[検証データの利用条件](../../../BeMusicSeeker.Tests/TestData/README.md)、[テスト実行](../development/testing.md)を参照します。
