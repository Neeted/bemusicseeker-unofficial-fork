# C#検証入口

標準MTPの発見・DataRow・実行・TRXを利用し、準備、moduleの起動、結果回収だけを担当します。ケースのFQN表や独自shardは持ちません。

```powershell
dotnet run --project tools/BeMusicSeeker.Verify -- dev
dotnet run --project tools/BeMusicSeeker.Verify -- verify
dotnet run --project tools/BeMusicSeeker.Verify -- full
dotnet run --project tools/BeMusicSeeker.Verify -- dev --filter "FullyQualifiedName~対象名"
dotnet run --project tests/BeMusicSeeker.Core.Tests -- --filter "FullyQualifiedName~ResultGateTests" --minimum-expected-tests 1 --report-trx
```

`dev` はDebugのCore、Windows、Daily Scenariosを選びます。`verify` は変更C#の自動整形後に同じ対象を実行します。復元後、全style batch、全whitespace batch、build、testの順に進み、前段失敗で後段を開始しません。変更C#ゼロではformatterを起動しません。

Windowsの日常検証には外部プレイヤーの実起動2件、更新Gatewayの実プロセス5件、公開関数のrestore/publish引数3行を含みます。更新マーカー2件は旧入口の `ProcessIntegration` から日常へ含める保証です。実起動7件とfresh playback3件は既存Windows probeと同じ全終端360秒・独立回収10秒を共用します。公開引数は単一60秒・独立回収5秒で実PowerShellを所有し、実R2R生成の保証はFullの実publishに残します。

## 移行中の実行範囲

現在は共通基盤と6moduleを接続し、既存ケースとシナリオを順次移管しています。移管は未完了のため、必須回帰の標準入口は `scripts/verify-refactor.ps1` のままです。新しい `dev/verify` で空moduleが選択された場合は不成功になります。新入口の全体合格には、既存ケースの移管とDailyシナリオの接続が必要です。

外部プレイヤーの起動判断・更新要求・runtime引数の通常解析はCore、ログ構成と相対manifestのCWDを扱うcaseはWindowsへ配置します。設定互換とBASS collectibleの三caseはWindowsから一case一fresh `WindowsProcessProbe` を起動し、子内の表明と回収を成功JSONの前に完了します。旧暫定入口は残存する通常対象を固定ハッシュで分けた2ホストを実行し、移管済みfresh caseを重ねて実行しません。除外後の通常集合への `DoNotParallelize` 混入と空の片側hostは非成功です。新moduleの実行方式は変更しません。

`full` はReleaseのCore、Windows、Scenarios、Desktop.E2Eを各1回実行します。Scenariosは全件、SC-07はCompatibility所属のため対象外です。必須IDはScenariosのSC-01〜SC-14（SC-07を除く）、DesktopのSC-15a・SC-15b・SC-16・DT-01〜DT-03です。正式Fullは `--filter` を準備開始前に拒否します。局所検証は対象projectを直接実行します。

Fullは全C# sourceの整形・build後、固定旧ZIP、現行V、次版Vnextを準備してからtest段階を開始します。Vは通常の公開版値、VnextはVの実バイナリの版から末尾をcheckedで1増やした実行限定の版です。通常の公開版値とAssemblyVersionを変えません。版指定はpublishの `-ApplicationVersion` から生成属性へ接続し、各ArtifactRoot内のproject別obj/binへ分離します。実シナリオは引き続き移行対象です。未接続によるpublish失敗や必須ID不足を成功扱いせず、fixtureを使う準備部品の検査だけで実Full成功としません。

`compatibility` と `performance` はsuiteの実体・前提・有限仕事量と期限が確定するまで不成功を返します。未完成suiteを名前だけで有効化しません。

## 配布物の受渡し

固定旧ZIPのidentityは追跡された `devdocs/acceptance/v216-first-hop/artifact.json` を正本とします。有効cacheは取得せず、不正cacheは変更・再取得せず失敗します。不在時だけ一度取得し、size/hash検査済みの一時fileをatomicに公開します。

VとVnextはrun内の別のArtifactRootへ各1回publishします。生成ZIPは実publishのapp/updaterと照合し、`update-managed-files.txt` 自身を除く全fileとの集合一致、既存layout、採用済nativeのhash/AMD64、test依存の混入を検査します。manifestの列挙順・改行は契約にしません。最上位config/data/log/logs/update_work/update_backupの6保護領域を含めません。imported_metadataはapp管理領域ですが、既存の配布layout禁止は維持します。この受入検査は製品updaterの入力受理を変更しません。bundle内の依存は既存の製品依存検査と分担し、一般依存の新しいhash台帳を作りません。

全配布物の完成後だけ `distribution/catalog.json` をatomicに公開し、子環境の `BMS_TEST_DISTRIBUTION_CATALOG` で渡します。`DistributionCatalog.ReadFromEnvironmentAsync(repository, deadline)` は呼出元が明示したrepositoryの追跡manifestを正本としてcatalogの旧版identityを照合し、未指定・不正を失敗にします。catalogの自己申告や所在から正本を選ばず、自動探索・再取得は行いません。直接module実行でも検査済みcatalogを明示指定し、moduleからの自動publishは行いません。caseは `ExtractInstallationAsync` でTestWorkspace内の新しいinstallationへZIPを展開し、共有publish/ZIPを変更しません。通常はCurrentを使い、SC-15aの更新先にだけNextを使います。

更新の実配布境界は `Scenarios/Update` の非Daily 30メソッド・32行で検査します。旧 `UpdaterPackageSyncTests` の27メソッド・29行と公開受入3件を移し、元のFQN・属性・故障入力・表明を維持します。旧5件のドライブルート・reparse専用保証は旧側に残します。短いSC-IDを代用して必須ID不足を解消したことにはしません。旧FullのReleaseAcceptance空選択は非成功を保ち、旧入口の退役は移行完了時に行います。

この32行はcase開始時の単調180秒をcatalog読取りから全Job・pipe終端まで共有し、公開payload30秒とlayout60秒など元の小さい上限も維持します。起動途中失敗とfinallyは同じLazy回収10秒を使用し、公開payload/layoutは元の回収5秒へ一度だけ制限します。Currentの実updater・公開root・ZIPと固定Legacy ZIPをTestWorkspaceへコピーまたは展開し、Nextの用途は増やしません。手作りZIPやjournalは故障条件として維持します。managed callback境界だけは既存updaterの明示build fixtureを配布し、NativeAOT配布やcatalogへ混ぜません。

生存アプリと再起動診断の2メソッド・3行は既存WindowsProcessProbeの固定payloadで実Processを保持します。boolのfalse/trueを要求と結果で照合し、全親Jobの終端後に親caseが自分のHKCU登録、tempの順で回収します。失敗時の主診断と回収診断を分け、未確認の回収を成功にしません。

## 所有と判定

test/appは共通 `OwnedProcess` がsuspended起動からJob割当を確立し、全所有childとstdout/stderrの終端を回収します。準備commandはJobへ入れず、子環境だけでビルドサーバーを無効化します。既存のビルドサーバーを列挙・停止しません。

各moduleは直接実行でも専有run rootを準備します。runner経由ではmoduleごとに異なる `cases` 領域を子環境の `BMS_TEST_RUN_ROOT` へ渡し、各caseが `TestWorkspace.Create(runRoot)` で独立した可変領域を作ります。DB・Window・processの所有者が先に終端してからworkspaceを破棄します。証跡を残す `PreserveArtifacts()` は他の資源の回収を省略しません。

Windows、Scenarios、Desktopは初期化前に同一ユーザー・Windows sessionのfile leaseを取得します。module内の全Window・子processを回収した後に解放します。runnerと子appは同じleaseを二重取得しません。

期待moduleごとの今回のTRX、process終了コード、全終端を照合し、選択結果が全件Passedの場合だけ成功します。部分skip、全skip、ゼロ件、report欠落・不正、timeout、回収失敗も不成功です。未選択moduleは「対象外」として区別します。

`full` 以外の `--filter` は標準MTP条件を各moduleへ渡し、module集合を変更しません。Dailyやsuiteの条件があるmoduleでは括弧で囲んだAND条件にします。`dev/verify` はScenariosの `[SC-01]`、`[SC-02]`、`[SC-09]` というtestName先頭の必須IDを照合し、filter指定時にも不足を成功として扱いません。

同じIDのDataRowが一つのmodule内で複数行になることは許可しますが、選択された全行のPassedが必要です。同じIDを別moduleへ重複配置することは許可しません。IDの後ろは空白・DataRowの括弧・文字列末尾で区切り、ケース全体のFQN一覧や独自DataRow展開は使いません。

全所有対象が終端した非ゼロ終了のmoduleは失敗を保持して他moduleへ進みます。起動失敗、取消、期限超過、回収失敗では同段階のactive processも取り消し、後続UI moduleを開始しません。主失敗・stdout/stderrの保存失敗・回収失敗を別々に記録し、保存または回収だけの失敗でも非成功です。stdout保存が失敗してもstderr保存を試みます。summary保存失敗時はコンソールに元の失敗と保存失敗を残します。

## 時間と証跡

各runは `artifacts/verification/mtp-*` に独立した結果領域を作ります。TRX、stdout/stderr、`summary.json` を保存します。summaryにsource SHA、dirty、実SDK、OS、論理CPU数、worker数、configurationを記録し、computer nameや個人profile pathを転載しません。標準TRXの生データはローカル診断として扱います。

`preparationMs` は復元・整形・実行条件取得、`buildMs` はbuild、`distributionMs` はFull配布準備です。`executionMs` は最初のtest起動から、全reportの集約と所有childの終端までのwall timeです。module別の `ElapsedMs` はそのmoduleの起動から出力・所有childの回収までで、未起動ならnullです。`cleanupMs` は重なりを除いた回収区間で、`executionMs` の内数です。`totalMs` は入口全体のwall timeで、cleanupを重複加算しません。ケースdurationや並行moduleの時間の合計をwall timeと比較しません。

移行中のdev/verify実行期限は360秒、Fullは全testで共有する1380秒です。Fullの600秒速度目標とは区別し、整形・build・配布準備をtest時間へ含めません。個々のscenarioの180秒・updateの240秒も共通期限を延長しません。formatは180秒、restore/buildは各300秒、固定旧ZIP取得は180秒、V/Vnextのpublish＋検査は各variant独立180秒、回収は10秒です。実行中の各待機で段階期限を作り直しません。日常実行の速度受入後にdev/verifyのtest段階を180秒へ切り替えます。

全format batchは一つの段階期限を共有し、restore/buildはそれぞれ別の段階です。test段階はCoreとUI laneで期限を共有します。正常Core終了時には致命失敗用の回収期限を確定しません。致命失敗を検出した時に、検出時刻+10秒と段階実行期限+10秒の早い方を一度だけ確定し、同段階の停止・出力保存で共有します。processごとの新しい10秒は与えません。

起動途中のnative割当・再開失敗も、低位の停止・回収を始める前にrunnerへ通知して同じcutoffを使います。独立した `OwnedProcess.Start` の利用では、従来どおり最初の起動失敗から有限10秒の回収期限を使います。

## 構成

判断・整形・集約の部品は既存のTestSupport.Verificationに置き、Verify入口とCore.Testsが同じライブラリを参照します。Verify自身はprofileのbuild対象へ入れません。sourceの二重compile、実行中exeの再build、別のrunner用projectを避けます。moduleが増えたりcaseが移る際は、旧Compile対象・新所属・profile選択を同じ変更で更新し、欠落・二重実行を防ぎます。
