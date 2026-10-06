# BMS譜面位置の比較計測

## 目的と適用範囲

固定旧版と現行の本番BMSパーサーによる譜面位置計算を比較する簡易開発ツールです。同じ本文による解析結果と同期解析時間を記録し、差の判断に使います。本番の通常読込み・入力受理・計算規則は[Ribbitの位置と時刻](../library/ribbit-timing.md)を正本とします。

## 用語

「入力行」はfileの指定対象またはscanのsong行です。「case」は入力行のsample・repeat一組です。「準備入力」は担当行で一度だけ読込・判別・復号した本文とencoding・MD5・byte数です。その他は[用語集](../glossary.md)に従います。

## 仕様

### 入力と実行責務

file/scan、out/song-db/chart-root、workers、random-samples/repeat/seed/random-choices、cultureを受けます。fileはワーカー1、scanは指定数のTaskワーカーを同一プロセスで使います。対応形式はBMS/BME/BML/PMSです。

scanはSQLite ReadOnlyの一度のsnapshotをrowid順に採取して接続を閉じます。WALと重複行を保持し、本文の全件cacheは行いません。相対pathはchart-rootで解決します。空path・基準なし相対path・対象外形式・不正pathはskip、不在・拒否・空ファイルなどの準備失敗はinput-errorとして該当caseへ記録します。

ワーカーは担当行を一度だけread・自動判別・復号・MD5採取し、準備入力を旧新の全sample/repeatへ共有します。読込みは本番と同じ共有モードとdecoderを使います。Random、Queue、BMSFileは呼出しごとに独立です。旧新の先行順は交互、同caseのsource/seed/choices/cultureは共通です。choicesの不足・余剰は従来のQueue規約に従い、元RandomPatternの相違はincomparableにします。

### 計時と進捗

run開始時、メモリ上の共通小本文を旧新各一回warmupします。parseMillisecondsは準備済み本文の同期BMSFile解析入口全体だけを計時します。allocatedBytesも同じスレッド・区間の差分です。read・判別・復号・hash、options・乱数・Queue・delegate準備、比較・表示・保存は計測外です。GCとJITはruntime既定に従います。

plannedは解決済み対応形式の入力行数で、重複・不在・拒否も含み、skipは除外します。startedはワーカーが担当行を開始するread前に一度加算します。sample/repeatで増えません。stderrに初期0・実行中の低頻度更新・終了値を日本語表示し、run.jsonにも保存します。

### 比較と集計値

初期BPM、小節Length/starttick、全元noteの位置/kind/index/Value/tick、Durationを比較します。Controlの重複列を除きます。raw結果から一小節のnote参照と元順序だけを整列用に保持し、型付きstructを遅延列挙します。位置は正規BigInteger整数比と特殊値タグで、旧Fractionの算術・CompareTo・GetHashCodeに依存せず比較します。同じ位置・kind・indexの連続出現順で重複を区別します。

追加・消失・Value差・tick差の全件数、最大絶対tick差・符号付き範囲・場所、Duration差、小節開始・小節線の差件数と最大境界差・場所を保存します。整数tick差は2^53超も厳密に扱います。差例は先頭20件だけ文字列化します。未対応Value型や列挙失敗はincomparableとし、全件依存集計・速度をnullにします。既知Duration差と取得済み上限付き差例は残します。

分類はequal/numeric-diff/structural-diff、legacy-only-success/current-only-success/both-failed、skip/input-error/incomparableです。比較を完了した同RandomPatternの成功対だけに速度比と増加時間を付けます。

### 保存・取消・失敗

buffer付きStreamWriterでcases/results/comparisons.jsonlを保存します。比較後、既存一つのwriter lockでcase一行・旧新結果二行・compare一行をまとめて書き、全JSONLのcase順を一致させます。全ノーツの保存は行わず、raw結果は比較保存後に解放します。

Ctrl+C後は受理済みcaseの旧新比較・保存を自然終了し、次case・rowを受理しません。解析拒否・比較不能は観測して次対象へ進み、出力IO失敗は全体errorです。run.jsonはcomplete/cancelled/errorと予定・開始・受理・完了・分類件数を保存します。正常回収の終了0と互換性判断は別です。

### 解析後の集計と発行

analyze.pyは3JSONLの完全prefixを同順に逐次読取り、Runnerの分類・速度・数値集計をそのまま使います。詳細CSVはcaseごとに即出力し、全件のpath・結果詳細を保持しません。速度比・増加時間の数値列から中央値・範囲・分位値を要約し、現行解析時間の分位値と(rowid,sample)の反復統計も維持します。比較完了対の追加・消失・Value・tick・小節開始・小節線の差件数は順次合計し、最大絶対tick差・最大境界差、符号付きtick差の全体範囲を整数のまま集計します。既知Duration差は比較不能のcaseも含めて全体範囲を保存します。tick差の全ケース列や場所・詳細の再整列は行いません。改行のない不完全末尾だけを完全prefix終了とし、完全行の不正JSONは分析失敗として返します。元譜面・DBへのアクセスはありません。任意の抽出閾値は候補の絞込みに使います。

publish.ps1はRelease/win-x64・自己完結型・未trimのRunnerと参照DLLを一folderへ発行し、licenseとZIPを添えます。固定旧sourceはrevision/blob/hashの出所と薄いconnection.patchを持ち、旧数学・制御・decoderを維持します。

## 実装とテストの対応

| 仕様項目 | 実装 | 確認方法 |
| --- | --- | --- |
| 通常decoderと準備済み本文入口 | [BMSFile](../../../BeMusicSeeker/Ribbit/BMS/BMSFile.cs)、[Current接続](../../../tools/chart-parse-audit/Current/Engine.cs) | 本番の数値・時刻テストは従来の責務を維持。tool入口は対象Runnerビルドと差分点検。 |
| 一度の入力準備・Task並列・計測・保存 | [AuditRunner](../../../tools/chart-parse-audit/Runner/AuditRunner.cs)、[Parser](../../../tools/chart-parse-audit/Shared/Parser.cs)、[Inputs](../../../tools/chart-parse-audit/Runner/Inputs.cs) | 対象RunnerのReleaseビルドと責務・計時境界の差分点検。恒常toolテストは設けません。 |
| 遅延位置比較・整数集計・差例 | [Projection](../../../tools/chart-parse-audit/Shared/Projection.cs)、[Comparison](../../../tools/chart-parse-audit/Runner/Comparison.cs) | 型付きキー・重複・不完全集計の差分点検。実行計測は利用者が必要時に行います。 |
| 逐次集計・発行 | [analyze.py](../../../tools/chart-parse-audit/analyze.py)、[publish.ps1](../../../tools/chart-parse-audit/publish.ps1) | 構文・参照と逐次読取りの差分点検。 |

## 関連資料

[ツールの実行説明](../../../tools/chart-parse-audit/README.ja.md)、[テスト検証](testing.md)、[本番の位置と時刻](../library/ribbit-timing.md)、[実譜面比較に基づく採用判断](../../decisions/ribbit-exact-timing-adoption.md)。
