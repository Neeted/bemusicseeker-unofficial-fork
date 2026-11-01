# C#検証入口

標準MTPの発見・DataRow・実行・TRXを利用し、準備、moduleの起動、結果回収だけを担当します。ケースのFQN表や独自shardは持ちません。

```powershell
dotnet run --project tools/BeMusicSeeker.Verify -- dev
dotnet run --project tools/BeMusicSeeker.Verify -- verify
dotnet run --project tests/BeMusicSeeker.Core.Tests -- --filter "FullyQualifiedName~ResultGateTests" --minimum-expected-tests 1 --report-trx
```

`dev` はDebugのCore、Windows、Daily Scenariosを選びます。`verify` は変更C#の自動整形後に同じ対象を実行します。復元後、全style batch、全whitespace batch、build、testの順に進み、前段失敗で後段を開始しません。変更C#ゼロではformatterを起動しません。

## 移行中の実行範囲

現在は共通基盤と6moduleの配置を先行して接続しています。既存ケースはまだ単一の `BeMusicSeeker.Tests` にあり、必須回帰の標準入口は `scripts/verify-refactor.ps1` のままです。新しい `dev/verify` で空moduleが選択された場合は不成功になります。新入口の全体合格には、既存ケースの移管とDailyシナリオの接続が必要です。

`full`、`compatibility`、`performance` の固定module計画は定義していますが、配布準備・環境preflight・実行期限の接続が終わるまで入口で不成功を返します。`full` の10分という設計目標を、無断でtimeout値へ読み替えません。fresh-process probeのpayloadと通常wrapperも後続の移管対象です。

## 所有と判定

test/appは共通 `OwnedProcess` がsuspended起動からJob割当を確立し、全所有childとstdout/stderrの終端を回収します。準備commandはJobへ入れず、子環境だけでビルドサーバーを無効化します。既存のビルドサーバーを列挙・停止しません。

各moduleは直接実行でも専有run rootを準備します。runner経由ではmoduleごとに異なる `cases` 領域を子環境の `BMS_TEST_RUN_ROOT` へ渡し、各caseが `TestWorkspace.Create(runRoot)` で独立した可変領域を作ります。DB・Window・processの所有者が先に終端してからworkspaceを破棄します。証跡を残す `PreserveArtifacts()` は他の資源の回収を省略しません。

Windows、Scenarios、Desktopは初期化前に同一ユーザー・Windows sessionのfile leaseを取得します。module内の全Window・子processを回収した後に解放します。runnerと子appは同じleaseを二重取得しません。

期待moduleごとの今回のTRX、process終了コード、全終端を照合し、選択結果が全件Passedの場合だけ成功します。部分skip、全skip、ゼロ件、report欠落・不正、timeout、回収失敗も不成功です。未選択moduleは「対象外」として区別します。

## 時間と証跡

各runは `artifacts/verification/mtp-*` に独立した結果領域を作ります。TRX、stdout/stderr、`summary.json` を保存します。summaryにsource SHA、dirty、実SDK、OS、論理CPU数、worker数、configurationを記録し、computer nameや個人profile pathを転載しません。標準TRXの生データはローカル診断として扱います。

`preparationMs` は復元・整形・実行条件取得、`buildMs` はbuildです。`executionMs` は最初のtest起動から、全reportの集約と所有childの終端までのwall timeです。module別の `ElapsedMs` はそのmoduleの起動から出力・所有childの回収までで、未起動ならnullです。`cleanupMs` は重なりを除いた回収区間で、`executionMs` の内数です。`totalMs` は入口全体のwall timeで、cleanupを重複加算しません。ケースdurationや並行moduleの時間の合計をwall timeと比較しません。

移行中のdev/verify実行期限は360秒、formatは180秒、restore/buildは各300秒、回収は10秒です。実行中の各待機で段階期限を作り直しません。日常実行の速度受入後にtest段階を180秒へ切り替えます。

## 構成

判断・整形・集約の部品は既存のTestSupport.Verificationに置き、Verify入口とCore.Testsが同じライブラリを参照します。Verify自身はprofileのbuild対象へ入れません。sourceの二重compile、実行中exeの再build、別のrunner用projectを避けます。moduleが増えたりcaseが移る際は、旧Compile対象・新所属・profile選択を同じ変更で更新し、欠落・二重実行を防ぎます。
