# C#検証入口

標準MTPの発見・DataRow・実行・TRXを利用し、準備、moduleの起動、結果回収だけを担当します。ケースのFQN表や独自shardは持ちません。

```powershell
dotnet run --project tools/BeMusicSeeker.Verify -- dev
dotnet run --project tools/BeMusicSeeker.Verify -- verify
$candidate = Join-Path (Get-Location) 'artifacts/candidates/release-1'
dotnet run --project tools/BeMusicSeeker.Verify -- prepare-candidate --output $candidate
dotnet run --project tools/BeMusicSeeker.Verify -- full --distribution-catalog "$candidate/catalog.json"
dotnet run --project tools/BeMusicSeeker.Verify -- dev --filter "FullyQualifiedName~対象名"
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite chart-info-bms
dotnet run --project tools/BeMusicSeeker.Verify -- performance --suite scan-commit
dotnet run --project tests/BeMusicSeeker.Core.Tests -- --filter "FullyQualifiedName~ResultGateTests" --minimum-expected-tests 1 --report-trx
```

`dev` はReleaseのCore、Windows、Daily Scenariosを選びます。`verify` は変更C#の自動整形後に同じ対象を実行します。復元後、全style batch、全whitespace batch、build、testの順に進み、前段失敗で後段を開始しません。変更C#ゼロではformatterを起動しません。

Windowsの日常検証には外部プレイヤーの実起動2件、更新Gatewayの実プロセス5件、公開関数のrestore/publish引数3行を含みます。更新マーカー2件は旧入口の `ProcessIntegration` から日常へ含める保証です。実起動7件とfresh playback3件は既存Windows probeと同じ全終端360秒・独立回収10秒を共用します。公開引数は単一60秒・独立回収5秒で実PowerShellを所有し、実R2R生成は候補生成の実publish、生成物の利用はFullが担います。

## 標準の実行範囲

通常コミット・dev統合は `verify`、整形を省く反復は `dev`、公開候補は `full` を使います。旧単一TestsとPowerShell検証runnerは廃止し、読み取り専用TestDataだけを現位置へ保持します。

`full` はReleaseのCore・Scenarios・Desktopを各1回、Windowsを重複のない3分類で実行します。Scenariosの必須12IDは `SC-01, SC-02, SC-05, SC-09, SC-10A, SC-10B, SC-11, SC-12, SC-13, SC-14, SC-ExistingData, SC-CurrentUpdate`、Desktopの必須IDは `DT-01, DT-02, DT-03, SC-16, SC-15A, SC-15C` です。明示導入先消失後の再読込・再選択を `SC-05` で確認します。DesktopはScenariosの後に単一workerで実入力を専有し、同じIDの複数行を含め全行Passedを要求します。正式Fullの `--filter` は準備前に拒否し、局所検証は対象projectを直接実行します。

`prepare-candidate` が固定Legacy、公開可能なCurrent、試験Nextとcatalogを先に生成します。NextはCurrentの実バイナリ版の末尾をcheckedで1増やした実行限定の版で、通常公開版とAssemblyVersionは変えず、publishの `-ApplicationVersion` とvariant別ArtifactRootで分離します。Fullは全C#の整形・build後、明示catalogの検査と候補の展開・接続を行い、製品publishやZIP再生成をせずtest段階へ進みます。fixtureの成功を候補生成・Full成功の代わりにはしません。Nextは、SC-15AがUIから更新する先として、ケース所有のloopback配信がそのZIPの実bytesとサイズ・SHA-256で配ります。SC-15Cは、そのNext ZIPから実行ファイルだけを実行できないbytesへ差し替えた手作りpackageをケース内で作って先に配り、巻き戻しと通知の後に本物のNextを配ります。

## 手動整形の準備

`scripts/format-code.ps1` は固定 `tools/BeMusicSeeker.Verify/bin/x64/Release/net10.0-windows/BeMusicSeeker.Verify.exe` のformat commandへ渡す薄い入口です。初回と関連source更新後は次を実行します。

```powershell
dotnet restore tools/BeMusicSeeker.Verify/BeMusicSeeker.Verify.csproj --locked-mode --disable-build-servers -p:Platform=x64
dotnet build tools/BeMusicSeeker.Verify/BeMusicSeeker.Verify.csproj --no-restore --disable-build-servers -c Release -p:Platform=x64
pwsh -NoProfile -File scripts/format-code.ps1
pwsh -NoProfile -File scripts/format-code.ps1 -Scope All -NoRestore
```

未準備なら変更C#ゼロでも非成功とし、暗黙のbootstrap・build・restoreや別構成の探索をしません。既定ChangedはGitの未stage・stage済み・未追跡のNUL集合から存在するC#を選び、対象ゼロならrestoreとformatterを省きます。Allはsource列挙なしでsolution styleとfolder whitespaceを各一回実行します。通常はsolutionをlocked restoreし、NoRestoreはこれを省きます。Git・任意restore・全batchは一つの180秒、失敗回収は10秒です。引数・非zero・元診断を保持し、外CWDからもscript所在repositoryを使って元CWDへ戻します。

## native queryの追加検証

[EverythingBridgeの手順](../../native/EverythingBridge/README.md#native取得の追加検証)に従い、固定ReleaseのVerify準備後に `native/EverythingBridge/test-x64.ps1 [-BuildPathIdentityFixture]` を使います。通常profileへは追加しません。固定SDKの版/hash、MSVC v145のRelease/x64 compile、query実行、指定時だけfixture生成を一つの180秒で行い、前段非成功では停止します。生成物・診断は当repositoryの `native/EverythingBridge/obj/query-tests` に限定し、既存準備process所有と失敗回収10秒を共用します。製品DLLやC++ sourceは変更しません。

## 明示互換suite

`compatibility --suite <名前>` は[互換検査の16suite](../../tests/BeMusicSeeker.Compatibility.Tests/README.md)から一つを選び、Release・1worker・MethodLevelで実行します。Compatibilityのsolution filterをlocked restore・buildしてから起動し、整形は行いません。suiteの欠指定・空白・未知名は準備開始前に拒否します。名前は大文字小文字を区別し、aliasや `all` はありません。標準 `--filter` はsuite条件とのANDで渡し、OR条件で他suiteへ広げません。選択ゼロも不成功です。

DriveRootとDirectorySymbolicLinkは180秒、CrossVolume・Junction、固定参照8suite、install-acl・everything-native-path-identity・audio-encoders・audio-encoder-childは60秒を、起動・前提検査・本体・通常回収・実exitで共有します。これは初期運用上限であり、corpus全体の完了時間保証ではありません。失敗回収は共通の10秒を一度だけ確定します。Java準備JSONなどの入力は子環境へ継承し、実caseで検査します。必要なtoolや固定入力の取得・展開・権限設定は事前に済ませ、通常runの準備処理へ追加しません。

環境条件のDriveRoot（8メソッド・10行）、DirectorySymbolicLink（6・6、file linkを含む）、CrossVolume（4・4）、Junction（3・7）は明示suiteとして実行します。実能力・必要入力の不足をskipや代用品へ変えません。未登録suiteは準備前に拒否します。ACL（2行）、固定Everything native（2行）、実外部encoder（1行）、BASSenc/WSH子（2行）はそれぞれ `install-acl`、`everything-native-path-identity`、`audio-encoders`、`audio-encoder-child` を指定します。固定nativeには `BMS_EVERYTHING_PATH_IDENTITY_FIXTURE`、実外部encoderには `BMS_TEST_AUDIO_ENCODERS=1` と実exeの準備が必要です。入力不足は未成功とし、WSH子の成功で実外部encoderを代用しません。固定nativeも実IPC/indexのSC-07を代替しません。詳細な入力と後継sourceは[互換検査](../../tests/BeMusicSeeker.Compatibility.Tests/README.md#acl固定native外部音声)を参照します。一つのCompatibility suiteの成功を、全suiteや正式Fullの成功とは扱いません。直接module実行の局所結果は、正式suiteの共有期限内成功と区別します。

正式runnerはScenarios・Desktopにもtest段階を通じて同じ期限を渡します。Compatibility/PerformanceもQPC始点とTimeSpan ticksの期限を `BMS_TEST_SUITE_DEADLINE` で子だけへ渡し、lease・updater・probe・junction・外部toolの既存小上限との早い方を使います。Compatibility/Performance/Scenarios/Desktop以外のmoduleと準備processは同変数を明示除去し、親環境は変更しません。未指定の直接MTPだけ有限の局所既定を使い、明示空・不正・期限切れは失敗です。直接MTPの局所成功を正式runnerの共有期限内成功とは扱いません。

## 明示性能suite

`dotnet run --project tools/BeMusicSeeker.Verify -- performance --suite NAME` はReleaseのPerformance moduleだけを単一workerで実行します。全suiteが既存UiModuleLifetimeのsession排他を使い、他の検証・build・publish・測定と重ねません。正式入口の `--filter` は準備前に拒否し、局所の調査はmoduleの直接MTP実行へ分担します。

| suite | 固定仕事量 | 監視上限 |
| --- | --- | ---: |
| scan-commit | 10,001実BMSの既定commit | 180秒 |
| lr2-sync | 10,001行の既定同期chunk | 180秒 |
| folder-mutation | 背景Entry4,096／親譜面256の2行 | 180秒 |
| copy-tracking | 256実fileの転送と回収 | 60秒 |
| bmson-continuation | 1,000／4,000／16,000noteの全位置探索 | 60秒 |
| row-cache | ChartFile／PackageChartEntry各5反復 | 180秒 |
| playlist-summary | 500表・58万entry・21万所持、全5route各5反復 | 300秒 |
| install-estimation | 全3規模・全policy・cold/warm・既存全反復 | 600秒 |
| audio | 実NULL_DEVICEの4負荷と先読み切替18条件 | 300秒 |

全14メソッド・16 MTP行の内部規模・route・35周波数条件を維持します。`BMS_NET10_PERF_SCALES` による縮小はなく、正式suiteは全3規模固定です。秒数は運用watchdogで、速度の合否閾値や改善目標ではありません。既存保証の移管であり、新しい業務期待や負荷は追加しません。

起動・前提・本文・通常回収・実exitにsuiteの一期限を共有し、Compatibilityと同じ子環境の共有cutoffを渡します。親の失敗検知から一回だけ開始する回収10秒は既存runnerが担当します。子が失敗した瞬間を親へ通知する追加IPC保証はありません。直接MTPのPredecodeは300秒の有限既定を使い、明示された不正・期限切れのsuite期限を既定へ置換しません。失敗後の回収期限は一回だけ確定し、Close/開始済みTaskの実終端を確認します。回収期限後も実Taskが未終端の場合だけ、診断とworkspaceを保持して専有MTPをFailFastで非成功終了し、後続caseや通常のmodule回収へ未終端nativeを渡しません。fault/cancelでも実終端したTaskは通常の原因集約・回収を行います。同期nativeの中断を保証する変更ではありません。

scan/LR2/Folder/Copy/Bmsonは準備・表明・回収と対象操作の経過を分けて記録します。既存の時計・割当・cold/warm区間は維持し、旧hostのThreadPool下限を移植しないため旧値との同条件baselineとは扱いません。実native・必要assets・writable workspace等の能力不足は非成功です。suiteの成功は選択した固定仕事量の完了を示し、性能改善の証明とは区別します。

## 配布物の受渡し

固定旧ZIPのidentityは追跡された `devdocs/acceptance/v216-first-hop/artifact.json` を正本とします。有効cacheは取得せず、不正cacheは変更・再取得せず失敗します。不在時だけ一度取得し、size/hash検査済みの一時fileをatomicに公開します。

`prepare-candidate --output <専有絶対dir>` は存在しない専有出力先に Current と Next を各1回publishし、HTMLを含む公開可能なZIPを作ります。生成ZIPは実publishのapp/updaterと照合し、`update-managed-files.txt` 自身を除く全fileとの集合一致、既存layout、代表HTMLの `README.html` / `README.ja.html` の存在と非空、採用済nativeのhash/AMD64、test依存の混入を検査します。manifestの列挙順・改行は契約にしません。最上位config/data/log/logs/update_work/update_backupの6保護領域を含めません。imported_metadataはapp管理領域ですが、既存の配布layout禁止は維持します。この受入検査は製品updaterの入力受理を変更しません。bundle内の依存は既存の製品依存検査と分担し、一般依存の新しいhash台帳を作りません。

全配布物の完成後だけ出力直下の `catalog.json` をatomicに公開します。Current は `v/`、Next は `next/` に分離します。`--include-metadata` を付けると Current の同じpublish出力から同梱版も生成し、任意の `CurrentWithMetadata` としてcatalogへ記録します。元データは `--metadata-source <path>` で指定でき、省略時はpublishの既定元データを使います。同梱版は `chart-info-metadata.7z` と管理一覧へのその追加以外、通常版と全fileの内容が同じであることを確認します。

`full --distribution-catalog <absolute catalog>` は指定済み候補を検査して使います。catalogはFullで必須とし、他profileでは指定しません。Fullは製品publish・ZIP再生成・固定旧版の再取得を行いません。テストとその依存componentのbuildは行います。`DistributionCatalog.ReadFromEnvironmentAsync(repository, deadline)` は呼出元が明示したrepositoryの追跡manifestを正本として旧版identityを照合し、全ZIPの実体を検査します。未指定・不正・欠落は失敗とし、別catalog探索や代替生成を行いません。作成したproducerの世代にかかわらず、同じ実体条件を満たすcatalogを受理します。Nextは独立生成・版・配布構成を検査し、現行更新受入はCurrent installationへCurrent ZIPを適用する既存保証を維持します。

FullはCurrent ZIPをrun専有の `application/` へ一回展開し、`BMS_TEST_APPLICATION_ROOT` と `BMS_TEST_DISTRIBUTION_CATALOG` を子へ渡します。通常シナリオの実アプリと事前fixtureのexe/assets/lang/nativeは同じ展開先を使い、可変profileはケース専有のままです。日常・局所で両変数が未指定の場合だけ同じbuildのfixtureを使います。片方だけの指定は失敗です。直接moduleへcatalogを渡す場合は、そのCurrent ZIPを専有領域へ展開した絶対rootも `BMS_TEST_APPLICATION_ROOT` に指定します。更新・故障検査の明示installationはこの選択で上書きせず、既存の `ExtractInstallationAsync` でケース専有に展開します。

テストだけの変更で製品・依存・配布設定・同梱資料や内容が変わらない場合、通常のGit差分を確認して同じ候補を再利用できます。これらが変わった場合は新しい出力先で候補を生成します。Fullの整形で製品等が変わった場合も同じ判断を行います。公開にはFull合格時の既存summaryに記録された通常ZIPと任意metadata ZIPをそのまま使い、後の再build品を同一検証済みと扱いません。[公開手順](../../devdocs/spec/development/release.md#正式リリースの準備)を参照してください。

更新の実配布境界は `Scenarios/Update` の非Daily 35メソッド・53行で検査します。旧 `UpdaterPackageSyncTests` の27メソッド・29行と公開受入3件を移し、元のFQN・属性・故障入力・表明を維持します。ドライブルート4件とreparse1件の専用保証はCompatibilityのDriveRoot/DirectorySymbolicLinkへ所属します。これらのCLIは明示した `BMS_SCD_UPDATER_PUBLISH_ROOT`、または未指定時の通常buildを使い、managed callbackは既存build fixtureを使います。短いSC-IDを代用して必須ID不足を解消したことにはしません。選択moduleの空結果・skipは引き続き非成功です。

この53行はcase開始時の単調180秒をcatalog読取りから全Job・pipe終端まで共有し、公開payload30秒とlayout60秒など元の小さい上限も維持します。起動途中失敗とfinallyは同じLazy回収10秒を使用し、公開payload/layoutは元の回収5秒へ一度だけ制限します。Currentの実updater・公開root・ZIPと固定Legacy ZIPをTestWorkspaceへコピーまたは展開し、Nextの用途は増やしません。手作りZIPやjournalは故障条件として維持します。managed callback境界だけは既存updaterの明示build fixtureを配布し、NativeAOT配布やcatalogへ混ぜません。

生存アプリと再起動診断の2メソッド・6行は既存WindowsProcessProbeの固定payloadで実Processを保持します。boolのfalse/trueを要求と結果で照合し、全親Jobの終端後に親caseが自分のHKCU登録、tempの順で回収します。失敗時の主診断と回収診断を分け、未確認の回収を成功にしません。

## 所有と判定

test/appは共通 `OwnedProcess` がsuspended起動からJob割当を確立し、全所有childとstdout/stderrの終端を回収します。準備commandはJobへ入れず、子環境だけでビルドサーバーを無効化します。既存のビルドサーバーを列挙・停止しません。

Windowsは同じassemblyをSettings、Shell（MainWindow・Playlist）、両カテゴリの補集合の3実行に分け、各1workerとします。未分類ケースは補集合へ入り、追加filterのOR全体を分類条件とANDします。CPU予算Bを1〜16へ制限し、UI同時数Wは `clamp(B/4整数, 1, 3)`、Coreは `max(1, B-W-1)` workerです。CoreとWindowsを優先して並行させ、dev/verifyのDaily Scenariosは同じ有界UI幅の4番目の要求として空いた枠で開始します。Fullだけは全Windows所有対象の終端後にScenariosを開始します。B1だけ全体を逐次実行し、W1でも3要求を省略しません。同projectのbuildは一度だけで、ExecutionId別にrunsettings・TRX・runroot・profile・Jobを分離します。

各moduleは直接実行でも同じドライブ直下の基底に専有run rootを準備します。repository監視下やOSのsystem tempへケース入力を置きません。runner経由ではtest段階のQPC始点を記録後、実行体のドライブ直下の `bms-tests/<unique>` に専有の親領域を一つ作り、そのExecutionId別の子領域を子環境の `BMS_TEST_RUN_ROOT` へ渡し、各caseが `TestWorkspace.Create(runRoot)` で独立した可変領域を作ります。DB・Window・processの所有者が先に終端してからworkspaceを破棄します。証跡を残す `PreserveArtifacts()` は他の資源の回収を省略しません。専有親領域の絶対パスは起動時の表示と既存summaryの `caseRoot` に残し、runnerは親領域を再帰削除しません。TRX・ログ・runsettings・配布catalogは従来どおりartifacts側へ保存します。

Windowsはmodule全体のsession leaseを取得せず、実Activateを使うPlaylistLampViewerWindowManagerTestsが非UI側でケース前に取得し、Windowと開始済み処理の回収後に解放します。Scenarios・Desktop・Compatibility・Performanceは従来どおりmodule全体のsession leaseを維持します。Windowsのlang JSONは同build出力から専有profile/langへcopyして使用し、exe・nativeは共有読取のままです。Compatibilityも専有profile・日本語辞書を使い、実WPF hostは必要なcaseで遅延生成します。module内の全Window・子processを回収した後に解放します。runnerと子appは同じleaseを二重取得しません。

期待ExecutionIdごとの今回のTRX、process終了コード、全終端を照合し、選択結果が全件Passedの場合だけ成功します。部分skip、全skip、ゼロ件、report欠落・不正、timeout、回収失敗も不成功です。未選択moduleは「対象外」として区別します。

`dev/verify/compatibility` の `--filter` は標準MTP条件を各moduleへ渡し、module集合を変更しません。Dailyやsuiteの条件があるmoduleでは括弧で囲んだAND条件にします。`dev/verify` はScenariosの `[SC-01]`、`[SC-02]`、`[SC-09]` というtestName先頭の必須IDを照合し、filter指定時にも不足を成功として扱いません。

同じIDのDataRowが一つのmodule内で複数行になることは許可しますが、選択された全行のPassedが必要です。同じIDを別moduleへ重複配置することは許可しません。IDの後ろは空白・DataRowの括弧・文字列末尾で区切り、ケース全体のFQN一覧や独自DataRow展開は使いません。

全所有対象が終端した非ゼロ終了のmoduleは失敗を保持して他moduleへ進みます。起動失敗、取消、期限超過、回収失敗では同段階のactive processも取り消し、後続UI moduleを開始しません。主失敗・stdout/stderrの保存失敗・回収失敗を別々に記録し、保存または回収だけの失敗でも非成功です。stdout保存が失敗してもstderr保存を試みます。summary保存失敗時はコンソールに元の失敗と保存失敗を残します。

## 時間と証跡

各runは `artifacts/verification/mtp-*` に独立した結果領域を作ります。TRX、stdout/stderr、`summary.json` を保存します。summaryにsource SHA、dirty、実SDK、OS、論理CPU数、worker数、configuration、選択suite、module別filter、適用test期限を記録し、computer nameや準備JSONの内容・個人pathを新たに転載しません。標準TRXの生データはローカル診断として扱います。

`preparationMs` は復元・整形・実行条件取得、`buildMs` はbuild、`distributionMs` はFullの指定候補検査・展開です。summaryの `passed` は全選択moduleと必須IDを含む最終判定、`distributionCatalog` と `releaseArtifacts` は公開時に同じZIPを照合する入力です。主時計はsummaryの `testStageStartTimestamp`（test段階開始QPC）から同machineでrunner実復帰直後までを受入側で測定します。lang copy・Gate・summary保存・回収を含みます。`executionMs` は最初のtest起動からGate後・summary保存前までの補助wall timeです。module別の `ElapsedMs` はそのmoduleの起動から出力・所有childの回収までで、未起動ならnullです。`cleanupMs` は重なりを除いた回収区間で、`executionMs` の内数です。`totalMs` は入口全体のwall timeで、cleanupを重複加算しません。ケースdurationや並行moduleの時間の合計をwall timeと比較しません。

dev/verifyのtest段階の運用上限は600秒、Fullは全testで共有する1380秒です。Fullの600秒は後段の速度目標であり実行上限とは区別し、整形・build・候補検査と展開をtest時間へ含めません。個々のscenarioの180秒・updateの240秒も共通期限を延長しません。formatは180秒、restore/buildは各300秒、候補生成の固定旧ZIP取得とCurrent/Nextのpublish＋検査、Fullの指定候補検査・展開は各段階独立240秒、回収は10秒です。実行中の各待機で段階期限を作り直しません。速度目標の判定と期限変更は後段で判断しますが、旧基盤からの切替には既存保証と旧約305秒からの非退行が必要です。[受入条件](../../devdocs/spec/development/testing.md#通常テストの分割と並列実行)に従い、運用上限延長だけで完了にはしません。

全format batchは一つの段階期限を共有し、restore/buildはそれぞれ別の段階です。test段階はCore・Windows各実行・Scenarios・Desktopで期限を共有します。正常Core終了時には致命失敗用の回収期限を確定しません。致命失敗を検出した時に、検出時刻+10秒と段階実行期限+10秒の早い方を一度だけ確定し、同段階の停止・出力保存で共有します。processごとの新しい10秒は与えません。

起動途中のnative割当・再開失敗も、低位の停止・回収を始める前にrunnerへ通知して同じcutoffを使います。独立した `OwnedProcess.Start` の利用では、従来どおり最初の起動失敗から有限10秒の回収期限を使います。

## 構成

判断・整形・集約の部品は既存のTestSupport.Verificationに置き、Verify入口とCore.Testsが同じライブラリを参照します。Verify自身はprofileのbuild対象へ入れません。sourceの二重compile、実行中exeの再build、別のrunner用projectを避けます。moduleが増えたりcaseが移る際は、旧Compile対象・新所属・profile選択を同じ変更で更新し、欠落・二重実行を防ぎます。
