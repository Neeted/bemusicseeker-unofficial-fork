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

準備・build後の局所実行例です。

```powershell
dotnet run --project tests/BeMusicSeeker.Compatibility.Tests --no-build -- --filter "TestCategory=Suite.chart-info-bms" --minimum-expected-tests 1 --report-trx
```

suiteの選択が明示opt-inです。欠けた固定入力や外部toolをskipへ変えません。全件Passed、非空TRX、終了コード0と全所有process・pipeの終端を確認します。正式runnerのCompatibility入口とsuite全体の期限は後続の接続対象で、直接moduleの実行結果とは区別します。

JavaとExport CLIは子孫を含む単一Jobで所有し、操作30秒、失敗回収5秒を使います。回収を確認できない場合はケースの媒体を保持して失敗します。parserの既存の個別期限と未指定の意味は維持します。固定corpusは計2,810解析です。
