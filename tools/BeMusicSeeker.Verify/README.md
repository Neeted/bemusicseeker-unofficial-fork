# C#検証入口

標準MTPのテスト発見・DataRowの展開・実行・TRXを利用し、準備、moduleの起動、結果の回収だけを担当します。ケースのFQN一覧や独自の分割実行は持ちません。検証区分の選び方、結果の判定、期限の考え方は[テスト検証](../../devdocs/spec/development/testing.md)を正本とし、本書は各profileの対象・制限時間・証跡の一覧を正本とします。

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

## 標準の実行範囲

| profile | 自動整形 | 実行するmodule | 必須ID |
| --- | --- | --- | --- |
| `dev` | なし | ReleaseのCore、Windows（3分類）、Scenariosの `Daily` カテゴリ | Scenarios: `SC-01`、`SC-02`、`SC-09` |
| `verify` | 変更したC# | `dev` と同じ | `dev` と同じ |
| `full` | 全体 | ReleaseのCore・Scenarios（全件）・Desktopを各1回、Windows（3分類） | 下表 |
| `compatibility` | なし | Compatibilityの指定suite | なし |
| `performance` | なし | Performanceの指定suite | なし |

`full` の必須IDは次のとおりです。

| module | 必須ID |
| --- | --- |
| Scenarios | `SC-01`、`SC-02`、`SC-05`、`SC-09`、`SC-10A`、`SC-10B`、`SC-11`、`SC-12`、`SC-13`、`SC-14`、`SC-ExistingData`、`SC-CurrentUpdate` |
| Desktop | `DT-01`、`DT-02`、`DT-03`、`SC-16`、`SC-15A`、`SC-15C` |

明示した導入先が消失した後の再読込・再選択は `SC-05` で確認します。DesktopはScenariosの後に単一workerで実入力を専有します。

`verify` は、復元の後、自動整形（全スタイル修正、全空白修正、Git差分の空白検査）とビルドを同時に開始し、両方の成功後にテストへ進みます。整形が失敗したら同時のビルドを取り消し、再ビルドやテストを開始しません。整形が対象の内容を変えた場合は、同時のビルドが変更前・変更中の内容を読んだ可能性があるため、その結果を採らず、変更したファイルの更新時刻を進めて再ビルドし、その結果で判定します。採らなかったビルドもログ保存と所有プロセスの回収の成功を要求し、summaryの `superseded` で区別します。変更したC#がゼロならformatterを起動せず、同時のビルドの結果をそのまま使います。正式な `full`・`performance` の `--filter` は準備前に拒否します。局所的な検証は対象projectを直接実行します。読取り専用のTestDataは現在の位置に保持します。

### 段階ごとの制限時間

自動整形は冷えた全体ビルドと同時に始まり、CPUを奪い合います。整形の所要は約60〜170秒で、競合すると180秒を超えうるため、同時に走るビルドと同じ300秒の予算にそろえています。準備段階は整形とビルドの両方の終了を待つので、成功する経路では、整形の上限をビルドの上限以下に保てば段階全体の最悪時間は延びません。整形が止まって失敗する場合は、検出が最大120秒（180秒から300秒）遅れます。

| 段階 | 制限時間 |
| --- | ---: |
| 自動整形（全formatterで共有） | 300秒 |
| 復元 / ビルド | 各300秒 |
| 候補生成: 固定旧ZIPの取得・検査 | 240秒 |
| 候補生成: Current / Nextのpublish・検査 | 各240秒 |
| `full`: 指定候補の検査・Currentの展開 | 240秒 |
| テスト段階（`dev`・`verify`） | 600秒 |
| テスト段階（`full`） | 1380秒 |
| 致命的な失敗後の回収 | 10秒（一度だけ確定） |

テスト段階の制限時間は、Core・Windowsの各実行・Scenarios・Desktopで共有します。ケース固有の上限は共有の制限時間を延長しません。

| ケース | ケース固有の上限 |
| --- | ---: |
| Scenariosの各ケース | 3分 |
| 既存データ起動（`SC-ExistingData`） | 180秒 |
| 現行更新（`SC-CurrentUpdate`） | 240秒 |
| Desktopの各ケース | 3分 |
| UIからの更新（`SC-15A`）、更新の再起動失敗からの回復（`SC-15C`） | 各240秒 |

Windowsの日常検証には、外部プレイヤーの実起動、更新Gatewayの実プロセス、公開関数の復元・publish引数の検査を含みます。

- 外部プレイヤーの実起動と更新Gatewayは、既存のWindows probeを起動し、fresh playbackと同じ全終端360秒・独立した回収10秒・取消・唯一の成功JSONの判定を共用します。
- 公開関数の引数の検査は、実PowerShell関数を直接 `OwnedProcess` で呼び、起動前からルート・両方のパイプ・プロセスの所有単位の終端まで60秒、失敗時の回収は一度だけ確定する独立した5秒を使います。実際のReadyToRun生成は候補生成の実publish、生成物の利用は `full` が担います。

`ProcessIntegration` 属性を持つ更新マーカーのケースも、module全件を選択する標準入口では日常検証に含まれます。

## 手動整形の準備

`scripts/format-code.ps1` は、`tools/BeMusicSeeker.Verify/bin/x64/Release/net10.0-windows/BeMusicSeeker.Verify.exe` のformatコマンドへ処理を渡す薄い入口です。初回と関連ソースの更新後は次を実行します。

```powershell
dotnet restore tools/BeMusicSeeker.Verify/BeMusicSeeker.Verify.csproj --locked-mode --disable-build-servers -p:Platform=x64
dotnet build tools/BeMusicSeeker.Verify/BeMusicSeeker.Verify.csproj --no-restore --disable-build-servers -c Release -p:Platform=x64
pwsh -NoProfile -File scripts/format-code.ps1
pwsh -NoProfile -File scripts/format-code.ps1 -Scope All -NoRestore
```

- 未準備なら変更したC#がゼロでも非成功とし、暗黙の準備・ビルド・復元や別構成の探索をしません。
- 既定の `Changed` は、Gitの未ステージ・ステージ済み・未追跡のNUL区切りの集合から、存在するC#を選びます。対象がゼロなら復元とformatterを省きます。
- `All` はソースを列挙せず、ソリューションのスタイル修正とフォルダー単位の空白修正を各一回実行します。
- 通常はソリューションをロックファイルに従って復元し、`NoRestore` はこれを省きます。
- Git・任意の復元・全formatterは、`verify` の自動整形と同じ一つの300秒を共有し、失敗時の回収は10秒です。同時にビルドが走らないため、通常は余裕を残して終わります。
- 引数・非ゼロ終了・元の診断を保持し、リポジトリ外の作業ディレクトリから呼んでもスクリプトのあるリポジトリを使い、元の作業ディレクトリへ戻します。

## ネイティブ取得の追加検証

[EverythingBridgeの手順](../../native/EverythingBridge/README.md#native取得の追加検証)に従い、ReleaseのVerifyの準備後に `native/EverythingBridge/test-x64.ps1 [-BuildPathIdentityFixture]` を使います。通常のprofileへは追加しません。固定SDKの版・ハッシュ、MSVC v145のRelease/x64コンパイル、問合せの実行、指定時だけのfixture生成を一つの180秒で行い、前段が非成功なら停止します。生成物・診断は `native/EverythingBridge/obj/query-tests` に限り、既存の準備プロセスの所有と失敗時の回収10秒を共用します。製品のDLLやC++のソースは変更しません。

## 明示互換suite

`compatibility --suite <名前>` は、[互換検査](../../tests/BeMusicSeeker.Compatibility.Tests/README.md)に登録されたsuiteから一つを選び、Release・1worker・メソッド単位の並列化で実行します。suiteの一覧・入力・制限時間・準備は互換検査の説明を正本とします。

- Compatibilityのソリューションフィルターをロックファイルに従って復元・ビルドしてから起動し、自動整形は行いません。
- suiteの指定漏れ・空白・未登録名は準備開始前に拒否します。名前は大文字小文字を区別し、別名や `all` はありません。
- 標準の `--filter` はsuite条件とのANDで渡し、OR条件で他のsuiteへ広げません。選択ゼロも不成功です。
- Java準備JSONなどの入力は子プロセスの環境へ継承し、実ケースで検査します。必要なツールや固定入力の取得・展開・権限設定は事前に済ませ、通常の実行の準備処理へ追加しません。
- 一つのsuiteの成功を、全suiteや正式な `full` の成功とは扱いません。直接moduleを実行した局所的な結果は、正式suiteの共有期限内の成功と区別します。

正式runnerは、Scenarios・Desktopにもテスト段階を通じて同じ期限を渡します。Compatibility・Performanceにも、単調時計の始点と期限の長さを `BMS_TEST_SUITE_DEADLINE` で子プロセスだけへ渡します。共有期限の扱いは[suiteの共有期限](../../devdocs/spec/development/test-runtime.md#suiteの共有期限)に従います。

## 明示性能suite

`dotnet run --project tools/BeMusicSeeker.Verify -- performance --suite NAME` は、ReleaseのPerformance moduleだけを単一workerで実行します。全suiteが既存の `UiModuleLifetime` による利用者セッションの排他を使い、他の検証・ビルド・publish・測定と重ねません。正式入口の `--filter` は準備前に拒否し、局所的な調査はmoduleの直接MTP実行へ分担します。

| suite | 固定仕事量 | 監視上限 |
| --- | --- | ---: |
| scan-commit | 10,001件の実BMSの既定の確定 | 180秒 |
| lr2-sync | 10,001行の既定の同期チャンク | 180秒 |
| folder-mutation | 背景のエントリー4,096件／親譜面256件の2条件 | 180秒 |
| copy-tracking | 256件の実ファイルの転送と回収 | 60秒 |
| bmson-continuation | 1,000／4,000／16,000ノートの全位置探索 | 60秒 |
| row-cache | `ChartFile`／`PackageChartEntry` 各5反復 | 180秒 |
| playlist-summary | 500表・58万エントリー・21万所持、全5経路の各5反復 | 300秒 |
| install-estimation | 全3規模・全方針・cold/warm・既存の全反復 | 600秒 |
| audio | 実NULL_DEVICEの4負荷と先読み切替18条件 | 300秒 |

各suiteの内部の規模・経路・周波数条件を維持します。`BMS_NET10_PERF_SCALES` による縮小はなく、正式suiteは全3規模に固定します。秒数は運用上の監視上限であり、速度の合否基準や改善目標ではありません。性能suiteは既存の保証を移したもので、新しい業務上の期待や負荷は追加しません。

- 起動・前提・本体・通常の回収・実際の終了にsuiteの一つの期限を共有し、Compatibilityと同じ子プロセス環境の共有期限を渡します。親の失敗検知から一回だけ開始する回収10秒は既存のrunnerが担当します。子プロセスが失敗した瞬間を親へ通知する追加のプロセス間通信の保証はありません。
- 直接MTP実行のPredecodeは300秒の有限の既定値を使い、明示された不正・期限切れのsuite期限を既定値へ置き換えません。
- 失敗後の回収期限は一回だけ確定し、Close・開始済みTaskの実際の終端を確認します。回収期限後も実Taskが未終端の場合だけ、診断とworkspaceを保持して専有のMTPを `FailFast` で非成功終了し、後続のケースや通常のmoduleの回収へ未終端のネイティブ処理を渡しません。失敗・取消でも実際に終端したTaskは、通常の原因の集約・回収を行います。同期的なネイティブ処理の中断を保証するものではありません。
- スキャン・LR2同期・フォルダ変更・コピー・BMSONの検査は、準備・表明・回収と対象操作の経過を分けて記録します。既存の時計・割当・cold/warm区間は維持します。Performanceには以前のテストホストのThreadPool下限を移植していないため、以前の値との同条件の基準とは扱いません。
- 実ネイティブ部品・必要なassets・書込み可能なworkspace等の能力不足は非成功です。suiteの成功は選択した固定仕事量の完了を示し、性能改善の証明とは区別します。

## 配布物の受渡し

固定旧ZIPの識別情報は、追跡された `devdocs/acceptance/v216-first-hop/artifact.json` を正本とします。有効なキャッシュがあれば取得しません。不正なキャッシュは変更・再取得せず失敗します。不在のときだけ一度取得し、サイズ・ハッシュを検査済みの一時ファイルを原子的に公開します。

### 候補の生成

`prepare-candidate --output <専有の絶対パス>` は、存在しない専有の出力先に、CurrentとNextを各1回publishし、HTMLを含む公開可能なZIPを作ります。

- Native AOTのリンク準備が使う `vswhere.exe` がPATHから解決できず、Visual Studio Installerの標準の導入先（`ProgramFiles(x86)` 配下の `Microsoft Visual Studio\Installer`、環境変数がなければOSの既知フォルダー）にある場合だけ、その場所を `scripts/publish.ps1` が実行中のpublishのPATH先頭へ加え、1行を出力します。解決できるなら何もせず、終了時に元のPATHへ戻し、利用者の環境設定は変えません。
- NextはCurrentの実バイナリの版の末尾を、桁あふれを検査して1増やした実行限定の版です。通常の公開版と `AssemblyVersion` は変えず、publishの `-ApplicationVersion` とvariant別の `ArtifactRoot` で分離します。
- 生成したZIPは実publishのアプリ・更新プログラムと照合します。`update-managed-files.txt` 自身を除く全ファイルとの集合の一致、既存の配置、代表HTMLの `README.html` / `README.ja.html` の存在と非空、採用済みネイティブ部品のハッシュ・AMD64、テスト依存の混入を検査します。管理一覧の列挙順・改行は契約にしません。
- 最上位の `config`・`data`・`log`・`logs`・`update_work`・`update_backup` の6保護領域を含めません。`imported_metadata` はアプリの管理領域ですが、既存の配布配置での禁止は維持します。
- この受入検査は、製品の更新プログラムの入力受理を変更しません。同梱される依存は既存の製品依存の検査と分担し、一般依存の新しいハッシュ台帳を作りません。

全配布物の完成後だけ、出力直下の配布候補の目録（`catalog.json`）を原子的に公開します。配布候補の目録は公開候補のZIPを記録するファイルで、譜面の正本であるカタログとは別のものです。Currentは `v/`、Nextは `next/` に分離します。`--include-metadata` を付けると、Currentの同じpublish出力から同梱版も生成し、任意の `CurrentWithMetadata` として配布候補の目録へ記録します。元データは `--metadata-source <path>` で指定でき、省略時はpublishの既定の元データを使います。同梱版は、`chart-info-metadata.7z` と管理一覧へのその追加以外、通常版と全ファイルの内容が同じであることを確認します。

候補の用途は次のとおりです。fixtureの成功を、候補生成や `full` の成功の代わりにはしません。

- Next: `SC-15A` がUIから更新する先として、ケースが所有するループバック配信が、そのZIPの実際のバイト列とサイズ・SHA-256で配ります。
- `SC-15C`: Next ZIPの実行ファイルだけを実行できないバイト列へ差し替えた手作りのパッケージをケース内で作って先に配り、巻き戻しと通知の後に本物のNextを配ります。

### Fullでの利用

`full --distribution-catalog <絶対パス>` は、指定済みの候補を検査して使います。配布候補の目録は `full` で必須とし、他のprofileでは指定しません。

- `full` は製品のpublish・ZIPの再生成・固定旧版の再取得を行いません。テストとその依存部品のビルドは行います。
- `DistributionCatalog.ReadFromEnvironmentAsync(repository, deadline)` は、呼出し元が明示したリポジトリの追跡済みの指定を正本として旧版の識別情報を照合し、全ZIPの実体を検査します。未指定・不正・欠落は失敗とし、別配布候補の目録の探索や代替の生成を行いません。生成したツールの世代にかかわらず、同じ実体条件を満たす配布候補の目録を受理します。
- Nextは独立した生成・版・配布構成を検査し、現行更新の受入はCurrentの配置へCurrent ZIPを適用する既存の保証を維持します。

`full` はCurrent ZIPを実行専有の `application/` へ一回展開し、`BMS_TEST_APPLICATION_ROOT` と `BMS_TEST_DISTRIBUTION_CATALOG` を子プロセスへ渡します。

- 通常シナリオの実アプリと事前fixtureの実行ファイル・assets・表示言語ファイル・ネイティブ部品は同じ展開先を使い、可変のprofileはケース専有のままにします。
- 日常・局所の検証で両方の変数が未指定の場合だけ、同じビルドのfixtureを使います。片方だけの指定は失敗です。
- 直接moduleへ配布候補の目録を渡す場合は、そのCurrent ZIPを専有領域へ展開した絶対パスも `BMS_TEST_APPLICATION_ROOT` に指定します。
- 更新・故障の検査が明示する配置はこの選択で上書きせず、既存の `ExtractInstallationAsync` でケース専有に展開します。

候補の再利用の条件は[実行範囲の選択](../../devdocs/spec/development/testing.md#実行範囲の選択)、公開の手順は[正式リリースの準備](../../devdocs/spec/development/release.md#正式リリースの準備)に従います。公開には `full` 合格時のsummaryに記録された通常ZIPと任意のメタデータ同梱ZIPをそのまま使い、後で再ビルドしたものを同一の検証済みとは扱いません。

### 更新の実配布境界

更新の実配布境界は、`Scenarios/Update` のDaily以外のケースで検査します。元のFQN・属性・故障入力・表明を維持します。ドライブルートとリパースポイントの専用の保証は、CompatibilityのDriveRoot・DirectorySymbolicLinkに所属します。これらのコマンドラインの入力は、明示した `BMS_SCD_UPDATER_PUBLISH_ROOT`、または未指定時の通常ビルドを使い、マネージドのコールバックは既存のビルドのfixtureを使います。短いSC-IDを代用して必須IDの不足を解消したことにはしません。選択moduleの空の結果・skipは非成功です。

- 各ケースは、ケース開始時からの単調な180秒を、配布候補の目録の読取りから全プロセスの所有単位・パイプの終端まで共有し、公開payloadの30秒や配置の60秒など元の小さい上限も維持します。
- 起動途中の失敗と `finally` は同じ遅延初期化の回収10秒を使い、公開payload・配置は元の回収5秒へ一度だけ制限します。
- Currentの実際の更新プログラム・公開ルート・ZIPと、固定Legacy ZIPを `TestWorkspace` へコピーまたは展開し、Nextの用途は増やしません。手作りのZIPやジャーナルは故障条件として維持します。
- マネージドのコールバックの境界だけは、既存の更新プログラムの明示ビルドのfixtureを配布し、NativeAOTの配布や配布候補の目録へ混ぜません。

生存中のアプリと再起動診断のケースは、既存の `WindowsProcessProbe` の固定payloadで実プロセスを保持します。boolのfalse・trueを要求と結果で照合し、全親プロセスの所有単位の終端後に、親ケースが自分のHKCU登録、一時領域の順で回収します。失敗時の主診断と回収の診断を分け、未確認の回収を成功にしません。

## 所有と判定

テストプロセス・アプリは共通の `OwnedProcess` が、一時停止状態での起動からプロセスの所有単位（Job）の割当を確立し、全所有子プロセスと標準出力・標準エラーの終端を回収します。準備コマンドはJobへ入れず、子プロセスの環境だけでビルドサーバーを無効化します。既存のビルドサーバーを列挙・停止しません。

各選択moduleは、ビルド済みのMTP実行ファイルを直接起動し、専有の結果ディレクトリ、TRX、`--minimum-expected-tests 1`、標準のrunsettingsを渡します。

Windowsは同じアセンブリを、`Windows.Shell`（MainWindow・Playlist）、両カテゴリ以外（`Windows.Remaining`）、`Windows.Settings` の3実行に分け、各1workerとします。カテゴリのないケースは `Windows.Remaining` へ入り、追加のfilterのOR全体を分類条件とANDします。UI枠へはこの順で要求し、通常の所要が最短の `Windows.Settings` を最後に回します。

- CPU予算Bを1〜16へ制限し、UIを使う実行の同時数Wは `clamp(B/4の整数部, 1, 3)`、Coreは `max(1, B-W-1)` workerです。
- CoreとUIを使う実行を並行させます。`dev`・`verify` のDaily Scenariosは同じ上限のUI枠への最初の要求として開始し、Windowsの3実行が続きます。Scenariosはmodule全体で利用者セッションの排他を持つため、段階の冒頭で終え、排他を取得するWindowsのケースが段階の終盤で最長の実行を待たせないようにします。
- `full` だけは、全Windows実行の所有対象が終端した後にScenariosを開始します。
- B=1のときだけ全体を逐次実行し、W=1でも3要求を省略しません。
- 同じprojectを実行ごとに別々にビルドせず、全実行で一つのビルド結果を共有し、ExecutionId別にrunsettings・TRX・実行領域・profile・Jobを分離します。

各moduleは直接実行でも、同じドライブ直下の基底に専有の実行領域を準備します。リポジトリの監視下やOSのシステム一時フォルダーへケース入力を置きません。

- runner経由では、テスト段階の単調時計の始点を記録した後、実行ファイルのドライブ直下の `bms-tests/<unique>` に専有の親領域を一つ作り、そのExecutionId別の子領域を子プロセス環境の `BMS_TEST_RUN_ROOT` へ渡します。
- 各ケースは `TestWorkspace.Create(runRoot)` で独立した可変領域を作ります。DB・Window・プロセスの所有者が先に終端してからworkspaceを破棄します。証跡を残す `PreserveArtifacts()` は他の資源の回収を省略しません。
- 専有の親領域の絶対パスは、起動時の表示と既存のsummaryの `caseRoot` に残し、runnerは親領域を再帰的に削除しません。TRX・ログ・runsettings・配布候補の目録は `artifacts` 側へ保存します。

利用者セッションの排他（session lease）は、moduleごとに[テストの実行基盤](../../devdocs/spec/development/test-runtime.md#通常テストの分割と並列実行)の表に従って取得します。Windowsの表示言語ファイルは同じビルド出力から専有profileの `lang` へコピーして使い、実行ファイル・ネイティブ部品は共有の読取りのままです。Compatibilityも専有profile・日本語辞書を使い、実WPFホストは必要なケースで遅延生成します。module内の全Window・子プロセスを回収した後に排他を解放します。runnerと子プロセスのアプリは同じ排他を二重に取得しません。

期待するExecutionIdごとの今回のTRX、プロセスの終了コード、全終端を照合し、選択結果が全件Passedの場合だけ成功します。一部または全部のskip、ゼロ件、TRXの欠落・不正、制限時間超過、回収失敗も不成功です。未選択のmoduleは「対象外」として区別します。

`dev`・`verify`・`compatibility` の `--filter` は、標準MTPの条件として各moduleへ渡し、moduleの集合を変更しません。Dailyやsuiteの条件があるmoduleでは、括弧で囲んだAND条件にします。`dev`・`verify` は、Scenariosのテスト名の先頭の必須ID（`[SC-01]`、`[SC-02]`、`[SC-09]`）を照合し、filter指定時にも不足を成功として扱いません。

同じIDのDataRowが一つのmodule内で複数行になることは許可しますが、選択された全行のPassedが必要です。同じIDを別moduleへ重複して配置することは許可しません。IDの後ろは空白・DataRowの括弧・文字列の末尾で区切り、ケース全体のFQN一覧や独自のDataRow展開は使いません。

全所有対象が終端した非ゼロ終了のmoduleは、失敗を保持して他のmoduleへ進みます。起動失敗、取消、制限時間超過、回収失敗では、同じ段階の実行中のプロセスも取り消し、後続のUI moduleを開始しません。主失敗・標準出力と標準エラーの保存失敗・回収失敗を別々に記録し、保存または回収だけの失敗でも非成功です。標準出力の保存が失敗しても標準エラーの保存を試みます。summaryの保存に失敗したときは、コンソールに元の失敗と保存の失敗を残します。

## 時間と証跡

各実行は `artifacts/verification/mtp-*` に独立した結果領域を作り、TRX、標準出力・標準エラー、`summary.json` を保存します。summaryにはソースのSHA、未コミット差分の有無、実際のSDK、OS、論理CPU数、worker数、構成、選択suite、module別のfilter、適用したテスト期限を記録します。コンピューター名や準備JSONの内容・個人のパスを新たに転載しません。標準TRXの生データはローカルの診断として扱います。

| 項目 | 意味 |
| --- | --- |
| `preparationMs` | 復元・整形・実行条件の取得。 |
| `buildMs` | ビルド。自動整形がある場合は整形と同時の開始から再ビルドの終了までで、`preparationMs` と重なる。 |
| `distributionMs` | `full` の指定候補の検査・展開。 |
| `testStageStartTimestamp` | テスト段階の開始時の単調時計の値。受入側が、同じマシンでrunnerの実際の復帰直後の値との差を主時計として記録する。表示言語ファイルのコピー・結果判定・summaryの保存・回収を含む。 |
| `executionMs` | 最初のテスト起動から結果判定後・summary保存前までの補助的な経過時間。 |
| module別の `ElapsedMs` | そのmoduleの起動から出力・所有子プロセスの回収まで。未起動ならnull。 |
| `cleanupMs` | 重なりを除いた回収区間。`executionMs` の内数。 |
| `totalMs` | 入口全体の経過時間。回収時間を重複して加算しない。 |
| `passed` | 全選択moduleと必須IDを含む最終判定。 |
| `distributionCatalog`・`releaseArtifacts` | 公開時に同じZIPを照合するための入力。 |

ケースの所要時間や並行moduleの時間の合計を、全体の経過時間と比較しません。速度の目標を定める場合の比較方法は[テスト時間の短縮](../../devdocs/spec/development/test-runtime.md#テスト時間の短縮)に従い、制限時間の延長だけで達成とはしません。

全formatterは一つの段階期限を共有し、復元・ビルドはそれぞれ別の段階です。実行中の各待機で段階期限を作り直しません。正常にCoreが終了したときは、致命的な失敗用の回収期限を確定しません。致命的な失敗を検出したときに、検出時刻+10秒と段階の制限時間+10秒の早い方を一度だけ確定し、同じ段階の停止・出力保存で共有します。プロセスごとに新しい10秒は与えません。

起動途中のネイティブの割当・再開の失敗も、低水準の停止・回収を始める前にrunnerへ通知し、同じ期限を使います。独立した `OwnedProcess.Start` の利用では、最初の起動失敗から有限の10秒の回収期限を使います。

## 構成

判断・整形・集約の部品は既存の `TestSupport.Verification` に置き、Verifyの入口とCore.Testsが同じライブラリを参照します。Verify自身はprofileのビルド対象へ入れません。ソースの二重コンパイル、実行中の実行ファイルの再ビルド、別のrunner用projectを避けます。moduleが増えたりケースが移ったりするときは、旧Compile対象・新しい所属・profileの選択を同じ変更で更新し、欠落・二重実行を防ぎます。
