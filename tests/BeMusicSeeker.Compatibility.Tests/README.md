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
| DriveRoot | 10 | 専有・非Z drive alias、必要caseの専有HKCU領域、実WPF、updater |
| DirectorySymbolicLink | 6 | 実directory symlinkとfile symlinkを作成できる権限 |
| CrossVolume | 4 | 異なる実volume上の既存入力rootを明示 |
| Junction | 7 | PowerShell 7と実junction作成能力 |

正式入口では一度に一suiteを指定します。入力・外部toolを準備してから実行します。Javaは参照手順で生成したJSONの絶対pathを `BMS_TEST_CHARTSTRING_REFERENCE_JSON` に指定します。

```powershell
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite chart-info-bms
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite chart-info-edge --filter "FullyQualifiedName~InitialBpm|FullyQualifiedName~Random"
```

ReleaseのCompatibility moduleを1worker・MethodLevelで実行します。登録名は表の12名に限り、大文字小文字を区別します。欠指定・未知名・`all` は準備開始前に不成功となります。filterはsuite条件とのANDで標準MTPへ渡され、別suiteへ広がりません。

DriveRootとDirectorySymbolicLinkは180秒、他suiteは60秒を、起動・前提検査・本体・通常回収・実exitで共有し、失敗回収10秒を一度だけ確定します。内側parser/toolの期限は外側を延長しません。この初期運用上限は全corpusの完了保証ではありません。[入口の説明](../../tools/BeMusicSeeker.Verify/README.md)に結果・証跡と共通期限の扱いを示します。

直接moduleも、準備・build後の局所入口として利用できます。

```powershell
dotnet run --project tests/BeMusicSeeker.Compatibility.Tests --no-build -- --filter "TestCategory=Suite.chart-info-bms" --minimum-expected-tests 1 --report-trx
```

suiteの選択が明示opt-inです。欠けた固定入力や外部toolをskipへ変えません。全件Passed、非空TRX、終了コード0と全所有process・pipeの終端を確認します。正式入口の成功を直接moduleの結果で代用せず、一suiteの成功を互換検査全体やFullの成功とは扱いません。

JavaとExport CLIは子孫を含む単一Jobで所有し、操作30秒、失敗回収5秒を使います。回収を確認できない場合はケースの媒体を保持して失敗します。parserの既存の個別期限と未指定の意味は維持します。固定corpusは計2,810解析です。

Compatibility全体は既存UiModuleLifetimeのsession lease・専有profile・日本語辞書を使用します。WPF hostは実際に必要なcaseで遅延生成します。正式runnerは単調時計の始点と期限を `BMS_TEST_SUITE_DEADLINE` で子だけへ渡し、lease待機・updater・probe・junctionの小上限を共有cutoffで制限します。他module・準備processには同変数を渡さず、親環境も変更しません。未指定の直接MTPだけ有限の局所既定を使い、明示空・不正・期限切れは失敗です。

CrossVolumeには `BMS_TEST_CROSS_VOLUME_SOURCE_ROOT` と `BMS_TEST_CROSS_VOLUME_DESTINATION_ROOT` に既存の絶対local drive配下を指定します。volume GUIDが異なることを実照会し、各基点の専有子だけを回収します。drive文字やaliasだけの違いでは代用できません。DirectorySymbolicLinkにはfile linkの2条件も含みます。Junctionは作成process30秒・pipe5秒・失敗回収5秒を維持し、全終端後にリンクを解除します。未回収なら媒体を保持します。

DriveRootのCLI入力は `BMS_SCD_UPDATER_PUBLISH_ROOT` の明示指定、または未指定時の通常build出力です。明示した入力が欠ければ失敗します。再起動失敗callbackは別のmanaged fixtureを使います。ACL・固定Everything native fixture・実Everything service/indexのSC-07はこの4suiteに含みません。
