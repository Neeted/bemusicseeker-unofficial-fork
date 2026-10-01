# BMS譜面位置の旧新比較

固定旧版と作業ツリーの本番パーサーを同じ入力で実行する簡易開発ツールです。初期BPM、小節長・開始tick、元ノーツの位置・種別・Index・Value・tick、Durationを比較し、同期解析の時間と同じスレッドの割当量を記録します。

## ビルドと実行

Windows、リポジトリ指定の.NET SDKを使います。

```powershell
dotnet build tools/chart-parse-audit/Runner/ChartParseAudit.csproj -c Release
& tools/chart-parse-audit/Runner/bin/Release/net10.0-windows/ChartParseAudit.exe file --file 'C:\譜面\sample.bms' --out 'C:\比較\file'
& tools/chart-parse-audit/Runner/bin/Release/net10.0-windows/ChartParseAudit.exe scan --song-db 'C:\LR2\song.db' --chart-root 'C:\譜面' --workers 4 --random-samples 2 --repeat 3 --out 'C:\比較\scan'
python tools/chart-parse-audit/analyze.py --input 'C:\比較\scan' --out 'C:\比較\analysis'
```

`--out`は空のfolderを指定します。対応形式はBMS/BME/BML/PMSです。scanはSQLiteをReadOnlyで開き、WALを含む一度のsnapshotをrowid順に採取して接続を閉じます。重複行もそれぞれ比較します。相対pathは`--chart-root`で解決し、基準が無い相対path・空path・対象外形式はskipにします。

`--workers`は同一プロセスのTaskワーカー数です。fileは1です。`--random-samples`と`--repeat`の既定は1、`--seed`は20260930、`--culture`はen-USです。`--random-choices 1,2,3`で本番Queue規約の選択値を渡せます。Queue不足は同seedの独立Randomへ移り、余剰値はそのまま残ります。旧新の先行順を交互にし、元RandomPatternが異なる対はincomparableにします。

stderrに`処理開始: 開始済み / 予定 譜面`と終了値を表示します。予定は解決済み対応形式の入力行数です。不在・読込み拒否も予定に含み、skipは含みません。開始済みは担当行のread前に一度加算し、試行・反復で増えません。Ctrl+C後は受理済みcaseの比較と保存を終え、次case・rowを開始しません。

## 計測と比較

各ワーカーは担当行を一度だけread・自動判別・復号・MD5採取し、不変の本文・encoding・hash・byte数を旧新と全反復で共有します。呼出しごとのBMSFile・Random・Queueは独立です。起動時にはメモリ上の共通小本文を旧新各1回warmupします。

`parseMilliseconds`と`allocatedBytes`は準備済み本文を受ける同期解析入口全体だけの値です。read・復号・hash・options・乱数・Queue準備、比較、表示、JSON保存はこの区間の外です。強制GCやJITの完全な定常状態は前提にしません。

比較はraw結果を小節単位で遅延列挙します。位置は正規化したBigInteger整数比と特殊値タグで照合し、連続同キーの出現番号で重複を区別します。tick差は任意精度整数で計算します。差例は先頭20件、追加・消失・Value・tickの件数や最大差は全件から集計します。射影が途中で失敗するとincomparableになり、全件依存集計と速度はnullです。既知のDuration差と取得済み差例は残ります。

## 保存と集計

`cases.jsonl`、`results.jsonl`、`comparisons.jsonl`をbuffer付きwriterで保存します。一caseの入力1行・旧新結果2行・比較1行を同じlock内で書き、3ファイルのcase順を揃えます。全ノーツは保存せず、raw結果は比較・保存後に解放します。`run.json`はcomplete/cancelled/error、予定・開始済み・受理・完了・分類件数を保存します。read・空ファイル等の準備失敗はinput-error、同期解析の拒否はparse-errorとして区別します。解析拒否と比較不能は次へ進み、出力IO失敗は全体errorです。終了0は正常に回収したことを表します。

集計は3JSONLの完全prefixを同順に逐次読み、Runnerの分類・速度・数値集計をそのままCSVへ出力します。observations.csvは全case、compatibility.csvは受理失敗・準備失敗・比較不能の候補、numeric.csvは整数tick差、speed.csvは比較完了した成功対です。repetitions.csvは(rowid,sample)の反復中央値とばらつき、report.jsonは分類件数、現行解析時間の分位値、速度比・増加時間の中央値・範囲・分位値を要約します。比較完了対の追加・消失・Value・tick・小節開始・小節線の差件数は順次合計し、最大絶対tick差・最大境界差と符号付きtick差の全体範囲も保存します。既知のDuration差は比較不能のcaseも含めて全体範囲を集計します。全件のtick差列や場所・path詳細は保持せず、速度統計と反復統計の数値列だけを保持します。改行のない不完全末尾は完全prefixまで使い、完全行の不正JSONは分析失敗として返します。元譜面やDBには触れません。

任意の抽出条件は`--min-ms-increase`、`--min-speed-ratio`、`--min-tick-difference`です。合否閾値は設けず、差の判断は利用者が行います。

## 旧版と発行

旧版はrevision `648b1cffc0f1d01e6c8df17ba836ba7e46d0be23`のパーサー依存閉包です。出所のblob・hashと接続差はLegacyのbaseline-manifest.jsonとconnection.patchで示します。接続は準備済み本文入口と独立乱数源だけで、旧数学・解析制御・decoderを保ちます。

`publish.ps1 -Out <空folder>`はRelease/win-x64の自己完結型folder、必要license、ZIPを作成します。Runnerと参照DLLは同じfolderに置きます。
