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
| install-acl | 2 | 専有container/rootのDACLを変更・復旧できるWindows環境 |
| everything-native-path-identity | 2 | 明示した固定Everything SDK 3.0.0.9とpath identity fixture DLL |
| audio-encoders | 1 | 明示opt-in、対象encoderと必要decoderの実exe |
| audio-encoder-child | 2 | BASS/BASSencとSystem32のcscript.exe、実exit 0/7 |

正式入口では一度に一suiteを指定します。入力・外部toolを準備してから実行します。Javaは参照手順で生成したJSONの絶対pathを `BMS_TEST_CHARTSTRING_REFERENCE_JSON` に指定します。

```powershell
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite chart-info-bms
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite chart-info-edge --filter "FullyQualifiedName~InitialBpm|FullyQualifiedName~Random"
```

ReleaseのCompatibility moduleを1worker・MethodLevelで実行します。登録名は表の16名に限り、大文字小文字を区別します。欠指定・未知名・`all` は準備開始前に不成功となります。filterはsuite条件とのANDで標準MTPへ渡され、別suiteへ広がりません。

DriveRootとDirectorySymbolicLinkは180秒、他suiteは60秒を、起動・前提検査・本体・通常回収・実exitで共有し、失敗回収10秒を一度だけ確定します。内側parser/toolの期限は外側を延長しません。この初期運用上限は全corpusの完了保証ではありません。[入口の説明](../../tools/BeMusicSeeker.Verify/README.md)に結果・証跡と共通期限の扱いを示します。

直接moduleも、準備・build後の局所入口として利用できます。

```powershell
dotnet run --project tests/BeMusicSeeker.Compatibility.Tests --no-build -- --filter "TestCategory=Suite.chart-info-bms" --minimum-expected-tests 1 --report-trx
```

suiteの選択が明示opt-inです。欠けた固定入力や外部toolをskipへ変えません。全件Passed、非空TRX、終了コード0と全所有process・pipeの終端を確認します。正式入口の成功を直接moduleの結果で代用せず、一suiteの成功を互換検査全体やFullの成功とは扱いません。

JavaとExport CLIは子孫を含む単一Jobで所有し、操作30秒、失敗回収5秒を使います。回収を確認できない場合はケースの媒体を保持して失敗します。parserの既存の個別期限と未指定の意味は維持します。固定corpusは計2,810解析です。

Compatibility全体は既存UiModuleLifetimeのsession lease・専有profile・日本語辞書を使用します。WPF hostは実際に必要なcaseで遅延生成します。正式runnerは単調時計の始点と期限を `BMS_TEST_SUITE_DEADLINE` で子だけへ渡し、lease待機・updater・probe・junctionの小上限を共有cutoffで制限します。他module・準備processには同変数を渡さず、親環境も変更しません。未指定の直接MTPだけ有限の局所既定を使い、明示空・不正・期限切れは失敗です。

CrossVolumeには `BMS_TEST_CROSS_VOLUME_SOURCE_ROOT` と `BMS_TEST_CROSS_VOLUME_DESTINATION_ROOT` に既存の絶対local drive配下を指定します。volume GUIDが異なることを実照会し、各基点の専有子だけを回収します。drive文字やaliasだけの違いでは代用できません。DirectorySymbolicLinkにはfile linkの2条件も含みます。Junctionは作成process30秒・pipe5秒・失敗回収5秒を維持し、全終端後にリンクを解除します。未回収なら媒体を保持します。

DriveRootのCLI入力は `BMS_SCD_UPDATER_PUBLISH_ROOT` の明示指定、または未指定時の通常build出力です。明示した入力が欠ければ失敗します。再起動失敗callbackは別のmanaged fixtureを使います。実Everything service/indexのSC-07は、これらのsuiteや固定native fixtureでは検証しません。

## ACL・固定native・外部音声

旧 `PackageInstallPendingNormalizationWorkflowTests.cs` は [Installの同名ファイル](Install/PackageInstallPendingNormalizationWorkflowTests.cs)、旧 `EverythingNativePathIdentityTests.cs` は [Scanningの同名ファイル](Scanning/EverythingNativePathIdentityTests.cs)、旧 `ExternalAudioEncoderSmokeTests.cs` は [Playbackの同名ファイル](Playback/ExternalAudioEncoderSmokeTests.cs) に所属します。FQNと入力・期待を維持し、音声は方法別の二suiteで選択します。

`install-acl` は専有containerのListDirectoryと対象rootのReadAttributesを非継承Denyにし、実属性拒否を確認します。実操作の終端後に両DACLの復旧・属性取得・列挙・元bytesを確認してからworkspaceを回収します。専有root外のACLは変更せず、権限不足や拒否未再現をskip・成功にしません。

`everything-native-path-identity` は `BMS_EVERYTHING_PATH_IDENTITY_FIXTURE` に固定SDKとfixture DLLを持つ絶対baseを必須指定します。固定入力を読取り専用で使い、可変DLLコピーだけをTestWorkspaceへ置きます。実getter・pack・managed decode・索引・健康度・推定を確認し、実サービスのIPCや索引更新を扱うSC-07の代替にはしません。[nativeの準備と正式入口](../../native/EverythingBridge/README.md#native取得の追加検証)を参照してください。

```powershell
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite install-acl
# PowerShell 7.4以降。入力をVerifyの子環境だけへ渡し、親環境は変更しません。
$nativeRun = Start-Process dotnet -ArgumentList 'run --project tools/BeMusicSeeker.Verify -- compatibility --suite everything-native-path-identity' -WorkingDirectory $PWD.Path -Environment @{ BMS_EVERYTHING_PATH_IDENTITY_FIXTURE = 'C:\fixtures\path-identity-fixture' } -WindowStyle Hidden -Wait -PassThru
if ($nativeRun.ExitCode -ne 0) { throw "native suite failed: $($nativeRun.ExitCode)" }
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite audio-encoder-child
```

`audio-encoders` は `BMS_TEST_AUDIO_ENCODERS=1` が必須です。`BMS_TEST_AUDIO_ENCODER_DIR` は優先検索先、`BMS_TEST_AUDIO_ENCODER_TYPES` は `MP3_LAME,AAC_NERO,OPUS,FLAC,OGG_VORBIS` の部分集合です。types未指定・空文字・全空白は発見した種類だけを検査し、0種類なら失敗します。明示subsetの未知名・カンマリスト内の空要素・重複・指定exe不足も失敗です。5種類のencoderとOpus検査用opusdecの計6exeは利用者が準備し、自動取得や代替実行をしません。`audio-encoder-child` の2行は、16byteのPCMを実WSH子へ送り同期EncodeStop後の複製handleからexit 0/7を確認する別保証であり、実外部encoderの1行の代わりにはなりません。

4suiteはいずれも起動・前提検査・本体・通常回収・実exitを共有する60秒、失敗回収は共通10秒の初期上限です。全入力・全形式の実測完走保証ではありません。decoderの旧30秒上限は同じsuite残時間との早い方を使い、後続decoderでsuite予算を更新しません。decoderはOwnedProcessでroot・両pipe・Jobを回収し、未回収なら媒体を保全します。encoder未解放時はsource/sessionを解放せず所有を保持し、主失敗と後片付け失敗を残します。native fixtureの生成180秒はsuite実行と別の準備枠です。
