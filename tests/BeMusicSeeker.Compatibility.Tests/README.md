# 互換検査

固定参照との比較と実tool接続を、対象suiteを明示して実行します。固定入力はリポジトリ内の唯一の正本を読み、可変DB・報告・抽出先はケースのTestWorkspaceへ置きます。

| Suite属性の接尾辞 | ケース数 | 入力・準備 |
| --- | ---: | --- |
| chart-info-bms | 1 | BMS 1,000譜面と参照DB |
| chart-info-bmson | 1 | BMSON 1,034譜面と参照DB |
| chart-info-production | 1 | 既知の遅い4件を除いた755譜面 |
| chart-info-production-slow | 1 | 既知の遅い4譜面 |
| chart-info-production-latest | 1 | 最新参照7譜面 |
| chart-info-edge | 5 | 境界例10譜面 |
| chart-info-archive | 1 | 公式Export build出力、導入済み7-Zip、実native抽出 |
| chartstring-java | 1 | [固定参照と準備JSON](../../tools/chartstring-dump/README.md) |

正式入口では一度に一suiteを指定します。入力・外部toolを準備してから実行します。Javaは参照手順で生成したJSONの絶対pathを `BMS_TEST_CHARTSTRING_REFERENCE_JSON` に指定します。

```powershell
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite chart-info-bms
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite chart-info-edge --filter "FullyQualifiedName~InitialBpm|FullyQualifiedName~Random"
```

ReleaseのCompatibility moduleを1worker・MethodLevelで実行します。登録名は表の8名に限り、大文字小文字を区別します。欠指定・未知名・`all` は準備開始前に不成功となります。filterはsuite条件とのANDで標準MTPへ渡され、別suiteへ広がりません。

各suiteは起動・前提検査・本体・通常回収・実exitで60秒を共有し、失敗回収10秒を一度だけ確定します。内側parser/toolの期限は外側を延長しません。この初期運用上限は全corpusの完了保証ではありません。[入口の説明](../../tools/BeMusicSeeker.Verify/README.md)に結果・証跡と共通期限の扱いを示します。

直接moduleも、準備・build後の局所入口として利用できます。

```powershell
dotnet run --project tests/BeMusicSeeker.Compatibility.Tests --no-build -- --filter "TestCategory=Suite.chart-info-bms" --minimum-expected-tests 1 --report-trx
```

suiteの選択が明示opt-inです。欠けた固定入力や外部toolをskipへ変えません。全件Passed、非空TRX、終了コード0と全所有process・pipeの終端を確認します。正式入口の成功を直接moduleの結果で代用せず、一suiteの成功を互換検査全体やFullの成功とは扱いません。

JavaとExport CLIは子孫を含む単一Jobで所有し、操作30秒、失敗回収5秒を使います。回収を確認できない場合はケースの媒体を保持して失敗します。parserの既存の個別期限と未指定の意味は維持します。固定corpusは計2,810解析です。
