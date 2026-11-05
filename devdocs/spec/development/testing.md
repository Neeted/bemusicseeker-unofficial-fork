# テストの実行と受入検証

## 目的と適用範囲

通常の機能検証、性能測定、大容量データ、外部プロセス、配布・更新の受入検証について、実行範囲・環境・資源・待機・結果判定を定めます。保証対象とテストの追加・変更・削除は[テスト設計](test-authoring.md)、担当の実行責任と引継ぎは[エージェント運用](agent-workflow.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

**テストプロセス**はMTPテストアプリと、その所有するテストホストを指します。**完全修飾名（FQN）**は名前空間・クラス・メソッドまでを含むテスト識別子です。**判定記録**は実行結果を機械的に確認するTRXやJSONです。

**実機確認**は、実環境で人またはエージェントが操作・観察する確認です。画面、実OS入力・フォーカス・IME、DPI・実モニター、音声機器、担当の起動・設定適用・入力継承などを含みます。実アプリや外部プロセスを起動して結果を自動判定する既存の検証は、自動検証として扱います。

## 仕様

### 検証方針

開発環境の構築、実装、統合、レビュー、コミット、リリースを通じて、実機確認を必須工程にしません。エージェントは原則として自動テストと静的確認で検証し、表示・配置などの確認に必要なら画面外のWPF描画スクリーンショットを使います。スクリーンショットを全件の必須条件にせず、この方針のためだけに代替基盤やテストを増設しません。

実機確認が有用な場合は、変更に関係する理由、条件、操作、期待結果をユーザーへ簡潔に推奨します。結果待ちを必須工程にせず、エージェント自身が実施するのはユーザーが明示的に依頼した場合です。実機確認の未実施だけで各工程の完了を妨げません。

自動検証の成功を実機確認済みとは扱わず、未実施・未確認の範囲を報告します。必須の自動検証、失敗の報告と修正、既知不具合への対処は維持します。以下の実行区分・期限・失敗条件と、各機能仕様の自動検証をこの方針で緩和しません。

静的レビューと実行検証は独立した合格条件です。[レビュー開始条件](agent-workflow.md#レビューの進行と結果)を満たせば、日常テスト・Full・性能測定の完了や成功を待たずに予備レビューを開始し、予備レビュー合格後は最終レビューへ進みます。全体テストの未完了・未成功だけを静的な不合格理由にせず、具体的なコード上の不備・保証欠落は指摘します。最終静的レビュー合格・実行検証未完了という中間状態を認めますが、統合・完了時には最終差分に適用できる両方の合格を要求します。レビュー合格で必要なテストを省略せず、失敗・未実行を成功へ読み替えません。

### 実行範囲の選択

復旧の実製品process代表は[`RecoveryScenarioTests`](../../../tests/BeMusicSeeker.Scenarios.Tests/Recovery/RecoveryScenarioTests.cs)のSC13一件とSC14二件をFullで実行します。Dailyには追加しません。専有`TestWorkspace`、`OwnedProcess`、UI moduleのsession排他を共有し、各ケースの同じ期限で確認到達・操作Task・応答・実退出を待ちます。自然退出は入力前に期待登録し、未知切断や未応答を成功として許可しません。SQL復元だけは実handlerが開始する通常終了と応答が重なるため、その実Task終端の応答を制御窓口の破棄より先に回収します。

SQLの固定入力では、実modalの帰還と実handlerの終端を分けます。dialog decoratorはmodalが閉じた時点で既存の同期呼出元へ帰還し、SQL commandだけが同じ実handlerの終端・入力消費・元例外を待ちます。固定入力はhandlerのfinallyまで保持します。modal帰還を復元成功とは数えず、handler終端後のcommand応答、制御窓口の回収、実退出の順を維持します。非SQLの入力と通常Viewsの待機契約は変更しません。

検証区分は作業段階で選びます。反復中は対象moduleの直接MTP実行または `dev`、通常開発の最終検証は `verify` を使います。必要な実行検証の成功と独立した最終静的レビューの合格を揃えて、通常開発のコミット・`dev` への統合を行えます。文書のみの変更は[文書と検証](../../../AGENTS.md#文書と検証)に従います。

`full` は正式リリース前の公開候補に対して必須とします。`prepare-candidate` で公開可能なZIPを先に生成し、`full --distribution-catalog <absolute catalog>` で指定した候補を検証します。テストだけの変更で製品・依存関係・配布設定・同梱資料や内容が変わらない場合は、通常のGit差分を確認して候補を再利用できます。それらが変わった場合は新しい候補で再実行します。Fullの整形による変更も同じ判断の対象です。公開時には合格summaryと同じZIPを照合し、後の再生成物へ差し替えません。公開までの順序は[リリース手順](release.md#正式リリースの準備)に従います。

配布スクリプトや更新処理を開発中に変更する場合も、通常の最終検証は `verify` とし、変更した契約を確認できる関連テスト・受入検証を対象に絞って追加します。必要と判断した場合は開発中にも `full` を実行できますが、変更ファイルの種類や将来リリースされることだけを理由に、コミット前の `full` 全体を必須にはしません。次版のリリースノート作成や、配布物に含まれる実行時設定の変更だけでも同様です。レビューでは作業段階と変更の影響を確認し、変更固有の検証が不足する場合は、その対象と確認すべき契約を具体的に示します。

局所テストを追加したことや、広いテストの新設が不要だったことを理由に、選択した段階で必要な既存検証を省略しません。テストの保証範囲と実行区分は別です。統合・E2Eという呼び方だけで `full` へ移したり、すべての変更に全体フローの新設を要求したりしません。必要な環境・資源・規模から[テスト区分](#テスト区分)を選び、どこまで確認したかを報告します。

### 複数ワークツリーの検証

`verify` は全ワークツリーを通じて同時に一件だけ実行します。`full` も同じ扱いとし、全体ルートが担当間の連絡で開始と終了を調整します。実行枠は必要な局所検証などの準備が揃った順に割り当てます。Fullの通し実行だけが残った場合も同じ順序へ入れ、後から準備が揃った `verify` によって待機が延び続けないようにします。別ワークツリーの 局所MTP実行 と、その実行に必要なビルドは並行できます。局所MTP実行 は単位責任者が開始・結果・所有プロセスの回収まで管理し、実行のたびに全体ルートの開始許可を求めません。

同一ワークツリーでも静的レビューと実行検証は並行できます。レビュー対象と判断に使う共有依存を固定し、それらを書き換える編集・自動整形・生成処理を止めます。必要な自動整形は凍結前に済ませます。ソースを変更しないテストと、レビュー対象外の生成物だけを書くビルドは継続できます。ただし、実行中のテストが読むバイナリやデータをビルド等で上書きせず、別ワークツリーを含め共有出力・設定・DB等の資源衝突を避けます。標準入口に自動整形が含まれる場合は、その工程をレビューと重ねません。

実行失敗が判明してもレビュー全体を自動停止・失効させません。新しい証拠を担当へ共有し、判断に影響する箇所を確認します。修正は該当範囲の凍結を解除して行い、契約を保つ局所修正は関連する局所検証と局所再レビューで扱います。主要な設計・共有依存の変更に応じて範囲を広げ、無関係な確認済み範囲は保持します。実測負荷や資源問題が出た場合は、その根拠に応じて全体ルートが実行の開始順や並行範囲を調整します。性能測定は他の重い検証と分離し、全体ルートが開始を調整します。既存の担当連絡とGitで版・範囲を引き継ぎ、恒久台帳や自動予約機構は追加しません。

開始済みの処理を別の検証の都合で中断せず、終了と結果回収を待ちます。競合しない別ワークツリーの調査・編集は進められます。ワークツリーの追加・完了・再利用の判断、所有と独立性、移管時の確認結果の引継ぎは[並列作業](agent-workflow.md#並列作業)に従います。

最終検証済みの版をそのまま `dev` へfast-forwardで統合できる場合、統合直後に同じ `verify` を重複して実行しません。検証後にコードや関連する基底が変わった場合は、新しい版に必要な検証を行います。ブランチ更新と影響するレビューは[通常の統合手順](release.md#通常の作業ブランチの統合)に従います。

### 標準入口

WindowsのPowerShell 7からリポジトリルートで実行します。標準入口は [BeMusicSeeker.Verify](../../../tools/BeMusicSeeker.Verify/README.md) です。`global.json` のMSTest.Sdkがframework・adapter・MTPの版を所有し、locked restoreで依存を固定します。発見・DataRow・filter・TRXは標準MTPへ任せ、自前のFQN列挙やshardは作りません。

```powershell
dotnet run --project tools/BeMusicSeeker.Verify -- dev
dotnet run --project tools/BeMusicSeeker.Verify -- verify
$candidate = Join-Path (Get-Location) 'artifacts/candidates/release-1'
dotnet run --project tools/BeMusicSeeker.Verify -- prepare-candidate --output $candidate
dotnet run --project tools/BeMusicSeeker.Verify -- full --distribution-catalog "$candidate/catalog.json"
dotnet run --project tools/BeMusicSeeker.Verify -- compatibility --suite chart-info-bms
dotnet run --project tools/BeMusicSeeker.Verify -- performance --suite scan-commit
```

| profile | 対象と用途 |
| --- | --- |
| `dev` | ReleaseのCore・Windows・Daily Scenarios。整形を省いた反復。 |
| `verify` | 変更C#の整形、Releaseの同3module。通常コミット・dev統合の標準。 |
| `full` | 全体整形・Release・明示候補の検査と展開・Core・Scenarios・Desktop各一回、Windows3分類。公開候補の受入。 |
| `compatibility` | 登録済み16suiteの一つ。専用入力・環境を準備して明示実行する。 |
| `performance` | 登録済み9suiteの一つ。全仕事量の計測を他の重い検証と分離する。 |

各選択moduleはbuild済みMTP exeを直接起動し、専有results、TRX、`--minimum-expected-tests 1`、標準runsettingsを渡します。CoreとWindowsはmodule全件、DailyはSC-01/02/09を必須とします。`dev/verify` のfilterでもmodule集合は減らず、必須ID不足は失敗です。局所filterは対象projectを直接実行します。

```powershell
dotnet run --project tests/BeMusicSeeker.Core.Tests -c Debug -p:Platform=x64 -- --filter 'FullyQualifiedName~FractionTests' --report-trx --minimum-expected-tests 1
```

直接MTPの成功は標準入口の整形・build・全module/必須IDの結果確認を代替しません。直接実行でも非空・全Passed・exit・回収を確認します。`full/performance` のfilter、suiteの欠指定・未知名は準備前に拒否します。選択ゼロ、skip、欠落report、timeout、回収失敗を成功にしません。環境suiteの未選択は未実施であり、Full成功を全環境suite成功へ読み替えません。

Linuxでは[クロスビルドと局所検証](../../setup-linux.md)の実行範囲と未実施Windows検証を報告し、Windowsの`verify/full`成功に置き換えません。

### 自動整形の対象と実行

`verify` は、未ステージ・ステージ済みの変更ファイルと、Git の除外設定に該当しない未追跡ファイルの和集合から、現在存在する C# ファイルを選びます。対象は作業ツリー上のファイル全体であり、変更行だけには限定しません。`full` は全体を対象とします。いずれも生成先の `artifacts/verification`、`bin`、`obj`、`.tmp` を除きます。変更対象がゼロなら両 formatter を起動せず、対象指定なしの全体整形に置き換えません。

[共通の整形処理](../../../tests/BeMusicSeeker.TestSupport/Verification/RepositoryFormatter.cs)は、先に `dotnet format style BeMusicSeeker.sln --severity error --no-restore --verbosity minimal` でスタイルを修正し、続いて `dotnet format whitespace --folder` で空白・改行を揃えます。変更範囲では同じファイル一覧を両方の `--include` に渡します。整形後に同じ formatter の変更なし検査を重ねません。C# 以外も含む Git 差分の空白検査は、整形後・ビルド前に行います。

標準検証では対象取得と両 formatter が `format` フェーズの180秒期限を共有します。Git の取得失敗、formatter の非ゼロ終了・期限超過、差分の空白エラーでは、後続のビルド・テストへ進みません。このフェーズは復元・ビルド・テストの時間予算に含めません。formatter の警告は診断へ残します。自動整形の成功は、自動修正できないものも含むすべてのスタイル診断がゼロであることを意味しません。

手動で整形だけを行う場合も、同じ処理を使います。

```powershell
pwsh -NoProfile -File .\scripts\format-code.ps1
pwsh -NoProfile -File .\scripts\format-code.ps1 -Scope All
```

手動PS入口は固定ReleaseのVerify exeへ透過します。未準備は変更C#ゼロでも失敗し、暗黙のbuild・restoreや別構成の探索をしません。初回と関連source更新後は次を実行します。

```powershell
dotnet restore tools/BeMusicSeeker.Verify/BeMusicSeeker.Verify.csproj --locked-mode --disable-build-servers -p:Platform=x64
dotnet build tools/BeMusicSeeker.Verify/BeMusicSeeker.Verify.csproj --no-restore --disable-build-servers -c Release -p:Platform=x64
```

手動入口の既定範囲は `Changed` です。整形対象がある場合はロックファイルに従う復元から実行し、復元済みなら `-NoRestore` で省略できます。手動実行では対象取得・復元・整形を一つの180秒期限で扱います。整形規則、SDK、プロジェクト構成を変更した作業では、`-Scope All` で全体を整形します。通常検証が設定変更から対象範囲を推定して自動で広げることはありません。

C# の正規形は [`.editorconfig`](../../../.editorconfig) を正本とし、エラーに設定した規則を formatter の自動修正対象にします。ビルド時のスタイル解析は全モードで有効化せず、スタイルの処理を formatter に集約します。SDK 標準の品質解析は [`global.json`](../../../global.json) の `version` と `rollForward` に従って選択した SDK の既定を維持します。

[`Directory.Build.props`](../../../Directory.Build.props) は本体 `BeMusicSeeker`、更新プログラム `BeMusicSeeker.Updater`、TestSupportと各 `*.Tests` の nullable 診断をビルドエラーとして扱います。nullable 解析の範囲は各プロジェクトとソースの既存設定に従います。補助ツールには nullable 診断のエラー化を追加せず、全警告も一律にエラーへ変更しません。生成コードは SDK の扱いに従い、テストへリンクした補助ツールのコードは引き続きテストプロジェクトのコンパイル対象とします。

文書だけの変更の確認は[ルートの指針](../../../AGENTS.md)に従います。Mermaidを追加・変更する場合は[補助図の更新と確認](../README.md#更新と確認)も行い、描画確認と対象アプリの検証を区別します。

### 実行期限と失敗分類

dev/verifyのtest段階の運用上限は600秒、Fullは1380秒を全moduleで共有します。起動前の一つのQPC始点をScenarios・Desktopにも渡し、後発moduleや局所待機で延長しません。summaryの `testStageStartTimestamp` にtest段階開始のQPCを一度保存し、受入側が同machineでrunner実復帰直後のQPCとの差を記録します。lang copy、起動、Gate、summary保存、回収を含みます。`executionMs` は最初のtest起動からGate後・summary保存前までの補助値、`cleanupMs` はその内数です。準備・build・候補の検査と展開は別段階です。Fullの600秒は後段で測定する速度目標であり、実行上限ではありません。監視上限を速度合格の閾値にしません。

致命失敗の回収は、検出時刻+10秒と実行期限+10秒の早い方を一度だけ確定します。期限後の遅い正常終了を成功に戻しません。時間内の決定的失敗は初回から原因を調べます。

真の時間超過だけは、所有するプロセスツリーの停止・残留確認と診断保存の後、同じコマンド、フィルター、時間予算、版、条件で一回だけ再実行できます。予算内で成功し、症状の再発や決定的な診断がなければ一過性の負荷として両結果を報告します。それ以外は原因を調べます。局所的な失敗検出タイマーは、この再実行規則の代わりではありません。

失敗結果は原因調査の入力であり、担当の技術的な行き詰まりを示すものではありません。統合検証の失敗は[不備の差し戻し](agent-workflow.md#不備の差し戻し)で修正担当へ戻します。修正後の新しい版の検証と、失敗した同一版の再実行は区別します。障害解決担当の呼出しを正当化するためだけの追加実行はしません。

### テスト整理前後の時間比較

テスト群の整理では、同じWindows環境・同じ測定範囲で、整理前の標準 `verify` 一回と最終版の一回を基本に比較します。現行版の実行条件が分かる結果があれば基準に使い、平均や分散を得るためだけの複数回実行は要求しません。反復中は関連する条件を 局所MTP実行 で確認し、最終版の実行区分と同一版の再実行は[実行範囲](#実行範囲の選択)・[失敗分類](#実行期限と失敗分類)に従います。正式リリース前の確認などで `full` を使う場合、対象・構成が同じ範囲だけを比較に使い、計測のためだけに通常検証を重ねません。

比較するのはテストプロセスの全体経過時間です。ホスト別の終了時刻、長いケース、同じ時間帯の待機・共有資源から残る負担を調べます。並列ケースの所要時間を単純合計して全体時間や短縮可能時間と見なさず、スクリプト全体の時間とも区別します。条件が違う結果を同条件の改善実績とは扱いません。

軽量化は、[不要・重複する保証の整理](test-authoring.md#既存テストの採否)、保証に関係しない準備の削減、共有資源に基づく直列化の見直し、通常完了の固定待機の解消の順に検討します。Windowを使わなくしたことだけで `DoNotParallelize` を外さず、設定・文化圏・共有通知等の所有を確認します。通常時に消費しない待機上限を短くすることは、短縮の根拠になりません。残ったホスト間の偏りが実測で分かる場合に分割・配分を検討し、先にホスト数やworker数を増やしません。

保証整理の完了と180秒の達成は別々に判定します。必要なケースを対象外区分へ移す、スキップを増やす、巨大ループへまとめて件数だけ減らす、成功まで再実行する、時間予算を緩めることを短縮手段にしません。実行件数・スキップと理由・実測時間・未実施区分を報告し、目標超過時は実測時間と未達を示します。結果は作業用の生成物として使い、時間履歴の恒久台帳や自動配分の保存状態は追加しません。

成功時の運用目標超過だけでは追加調査・再実行を始めません。利用者から調査を依頼された場合は、テスト別所要時間だけでなく最長ホストの遅延区間を確認し、ロックの保持側やDBの確定回数など共通処理まで追います。

旧基盤からの切替は既存保証の保持と時間の非退行を必須とし、上限内の成功だけでは完了にしません。旧warm5の中央値304.9799643秒を基準に、新Release日常の主時計が294.211秒未満なら明瞭非退行、294.211〜335.133秒なら同版あと2回を含む中央値が基準以下で合格です。335.133秒超または中央値超は再設計します。Windowsの既存2389行と、新Windows3実行TRX＋Coreへ移した条件はFQN・DataRowを区別する標準testName等のidentity多重集合で一度照合し、和集合一致と群間重複なしを確認します。新規ケース差分は既存対応表とreportへ明記し、件数だけで欠落と重複を相殺しません。

### 通常テストの分割と並列実行

Windowsは同じassemblyをSettings、Shell（MainWindow・Playlist）、両カテゴリの補集合の3実行に分け、各1workerとします。未分類ケースは補集合へ入り、追加filterのOR全体を分類条件とANDします。CPU予算Bを1〜16へ制限し、UI同時数Wは `clamp(B/4整数, 1, 3)`、Coreは `max(1, B-W-1)` workerです。CoreとWindowsを優先して並行させ、dev/verifyのDaily Scenariosは同じ有界UI幅の4番目の要求として空いた枠で開始します。Fullだけは全Windows所有対象の終端後にScenariosを開始します。B1だけ全体を逐次実行し、W1でも3要求を省略しません。同projectのbuildは一度だけで、ExecutionId別にrunsettings・TRX・runroot・profile・Jobを分離します。ケース分配はMTPのMethodLevelへ委ね、`DoNotParallelize` は標準属性として扱います。 runnerはtest段階のQPC始点を記録後、実行体のドライブ直下の `bms-tests/<unique>` に専有親領域を一つ作り、ExecutionId別の子領域を `BMS_TEST_RUN_ROOT` で渡します。親領域の絶対パスは起動時とsummaryの `caseRoot` に記録し、runnerによる親領域の再帰削除は行いません。直接MTPの既定も同じ基底へ揃え、repository監視下やOSのsystem tempへケース入力を置きません。ケースのTestWorkspace・PreserveArtifactsは維持し、TRX・ログ・runsettings・配布catalogはartifacts側に残します。

Windowsはmodule全体のsession leaseを取得せず、実Activateを使うPlaylistLampViewerWindowManagerTestsが非UI側でケース前に取得し、Windowと開始済み処理の回収後に解放します。Scenarios・Desktop・Compatibility・Performanceは従来どおりmodule全体のsession leaseを維持します。Coreは実Window・プロセス全体の可変設定を所有しません。所有する一時領域と入力の分離は[資源の分離と共有](#資源の分離と共有)に従います。

LR2同期・フォルダ改名・重複統合の実DB/FS、rollback、receipt、通知時現在値の保証はCoreの同名classへ置き、生成点で既存のheadless schedulerを明示します。QueuedUiSchedulerの保留・別thread排出と実PropertyChangedは維持します。Windowsには `QueueLr2SongDbSync_RunsLr2SongDbSyncAndMarksCompletedWhenClean` の実Dispatcher通知3条件と `DuplicateAdmission_RealAnalysisRejectsLibraryMoveUntilRequiredNotificationsComplete` の実AppComposition/VM投影1条件を残します。共通factoryの既定は変えず、Performance共用の祖先確認helperもscheduler省略時の意味を維持します。

### テスト区分

実行集合はmoduleとprofileで決めます。Core/Windowsの通常ケース、ScenariosのDaily/Full、FullのDesktop、Compatibility/Performanceの明示 `Suite.*` を区別します。旧 `ProcessIntegration` 等の属性が残っていても新入口の除外規則には使いません。明示導入先消失後の再読込・再選択はScenariosのFull、実入力と外部UIAの接続はDesktopで確認します。

環境不足を `Ignore` / `Inconclusive` で飛ばさず失敗にし、未実施を成功として扱いません。[BMS譜面位置の比較ツール](chart-parse-audit.md)は簡易開発用です。変更確認は対象RunnerのReleaseビルドと差分点検に限定し、恒常toolテストは設けません。

Windows moduleの実プロセス境界は [`ExternalPlayerProcessGatewayTests`](../../../tests/BeMusicSeeker.Windows.Tests/Processes/ExternalPlayerProcessGatewayTests.cs)、[`UpdaterProcessGatewayTests`](../../../tests/BeMusicSeeker.Windows.Tests/Update/UpdaterProcessGatewayTests.cs)、[`UpdaterDeploymentBoundaryTests`](../../../tests/BeMusicSeeker.Windows.Tests/Update/UpdaterDeploymentBoundaryTests.cs) が日常検証で担います。更新マーカー2件は `ProcessIntegration` 属性を維持しつつ、module全件を選択する新入口では日常検証に含めます。外部起動2件と更新Gateway5件は既存Windows probeを起動し、fresh playbackと同じ全終端360秒・独立回収10秒・取消・唯一の成功JSONの判定を使います。公開引数3行は実PowerShell関数を直接 `OwnedProcess` で呼び、起動前からroot・両pipe・Jobの終端まで60秒、失敗回収は一度確定した独立5秒を共有します。元の製品期限・停止表明を回収による停止で代替しません。

カテゴリは実行特性を表します。小さい互換性テストや、少数の実部品を接続する検証は通常検証に含められます。`ProcessIntegration` は別プロセス等を扱う区分であり、主体間の接続を確認するすべてのテストを指しません。

### 資源の分離と共有

通常テストは固有の一時ファイル・DB・設定と、再現可能な入力を使い、追跡対象ファイルや利用者のデータを変更しません。公開サービスの可用性、ローカルサービスの索引反映、同じPCでの利用者の操作を合否条件にしません。EverythingやOS走査を利用する機能は、取得範囲と完全性を明示した変更不能な入力集合で検証し、作成直後のファイルが外部索引に現れることを前提にしません。外部環境との接続そのものを確認する場合は、対応する実行区分と条件を明示します。

環境依存を見つけた場合は、保証と必要資源から次の四つへ分類します。準備場所が一時ディレクトリでも、OS全体や利用者セッションへ影響する操作は通常I/Oと区別します。

| 分類 | 適用する条件 |
| --- | --- |
| 日常検証に残す | 所有tempの通常I/O・SQLite・設定、readonly・共有ロックや明示注入した失敗等で保証を再現できる。 |
| 依存をなくす | 保証に不要なdrive割当やTEMPの長さへの依存を外し、同じ本番処理・観測を所有tempで維持できる。 |
| 専用環境で明示実行する | 実ACL、symlink/junction、drive alias、別volume、HKCUのRun/RunOnce、外部native/toolそのものとの接続が保証対象。`EnvironmentIntegration` を付ける。 |
| 削除する | 必要な保証が既存の担当へ移管済み、または契約上不要。移管先の表明まで確認して旧ケースと不要な補助を除く。 |

専用環境は利用者の通常データ・設定から隔離し、ACL変更・リンク作成・drive別名の割当と解除・HKCU回復登録の作成と回収が可能なWindowsを用意します。`DoNotParallelize` は別利用者や別プロセスとの競合を防がないため、準備した環境でだけ明示実行します。処理・出力の終端後に所有資源を回収し、CWDを変更したケースは復元後にdrive aliasを解除します。準備不足・権限不足は失敗です。

クロスボリューム試験は、`BMS_TEST_CROSS_VOLUME_SOURCE_ROOT` と `BMS_TEST_CROSS_VOLUME_DESTINATION_ROOT` に既存の書込み可能なfixture基点を絶対パスで指定します。両者は異なるローカルdrive rootかつ異なる実volumeでなければなりません。[GetVolumePathNameW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamew) で末端のmount pointを取得し、[GetVolumeNameForVolumeMountPointW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumenameforvolumemountpointw) のvolume GUIDで同一実体を拒否します。別名だけを別volumeと見なしません。環境探索や書込みprobeは行わず、指定基点の下へケース固有の子だけを作成・回収し、基点自体は保持します。

外部依存は対象に応じて用意します。Everythingのnative接続は `BMS_EVERYTHING_PATH_IDENTITY_FIXTURE` に固定SDKとfixture bridgeの基点を指定し、7-Zip接続は既存のProgram Files配置に7-Zip CLIを導入します。Java参照toolは `JAVA_HOME` またはPATHでJDKを用意し、[`chartstring-dump/run.ps1`](../../../tools/chartstring-dump/run.ps1) の参照リポジトリと固定譜面を準備します。外部encoderは[外部エンコーダー](#解析大規模データ外部エンコーダー)の環境変数を使います。これらの専用保証はCompatibilityの対応suiteで明示実行します。

DBの準備・観測は検証対象の処理と分けます。入力作成だけの複数行投入・スキーマ準備は一つのトランザクションへまとめ、SELECTだけの観測には既存の読取り専用接続を使います。無関係な一時DBの観測がプロセス共通の書込みロックを取り合わないようにします。スキーマ作成・確定境界自体を保証するテストでは、対象の本番処理とトランザクション境界を維持します。

共有設定、リソース辞書、文化圏、テーマ、ウィンドウ一覧などを変更する場合は、準備から復元までの所有を確認します。`DoNotParallelize` は分離不能な共有資源とその復元方法を説明できる範囲に使い、独立した状態だけを扱うテストは並列実行します。この属性は他プロセスや利用者の操作を排他しません。外部入力の分離とテスト間の共有状態は別々に確認します。

通常fixtureの設定入力は固定snapshot、専用pathのSettings、in-memory storeを使い、暗黙の `Settings.Default` や利用者設定providerへ戻しません。設定保存の保証には専用pathの実providerを使います。日本語リソースの通常基準はAssemblyInitializeで一回だけ準備します。実文化・テーマの切替と共有通知への購読は、準備から処理終端・購読解除・復元まで所有します。標準compositionはWindowがなくても共有通知を購読するため、その寿命も非並列にします。

### 非同期処理の待機

通常完了は、対象のTask、イベント、状態遷移に直接結び付く通知で待ちます。通知より先に処理が失敗した場合も観測し、来ない通知だけを待ち続けません。テストが閉じた待合せは `finally` で解放し、所有する処理の終結と後片付けを待ちます。

成功通知の到達待ちは、`TestUiDispatcherHost.AwaitNotificationAsync` で通知を生成する実Taskの失敗・取消にも接続します。通知なしで実処理が終わった場合は、その場で未到達として失敗します。生成元が失敗を結果通知へ変換する所有者では、その所有者の実終端を使います。別に予約されたUI受信は生成workerの完了で代用せず、受信処理自身を所有します。MainWindowの寿命は `CloseCompletion` で終了要求からDispatcherと終端処理まで待ち、通知前の失敗もClosed・共有Resourceの回収と併せて観測します。

単なる到達・完了確認には局所期限を追加せず、停止は検証全体の期限で検出します。局所的な制限時間は、外部プロセス、loopback HTTP fixture、画面表示、解放、ロックが成立しないこと、時間超過契約の失敗検出に限定します。固定の `Thread.Sleep` や余裕時間としての `Task.Delay` から成功を推測しません。短い否定観測は、通常完了を待つ方法や時間保証の証明とは区別します。

不安定なテストは共有状態、順序依存、待機、資源競合を調べます。期限延長、再試行増加、実行担当数や分割数の削減だけで失敗を隠しません。同一版の再実行は[実行期限と失敗分類](#実行期限と失敗分類)に従います。

### HTTP fixtureの待機と回収

loopback HTTP fixtureを所有するケースは、開始時に `TestContext.CancellationToken` とlinkedした操作CTSを一つ作り、操作全体のwatchdogを一度だけ設定します。現在の通常上限は30秒で、製品のHTTP期限を仮想時刻で検査する値とは区別します。accept・要求読取り・応答書込み・gate・各phaseとclient完了の待機は同じ操作tokenを共有し、段階ごとに期限を延長しません。phase通知だけを待たず、client outcomeとserverの実終端も監視して先行故障と通知なしの終端を失敗にします。実producerの終端を観測した後にも通知を再確認して同時終端した通知を失わず、正常終了済みの補助producerは待機集合から除きます。元Taskを公開しない所有者では、対象固有の既存終了通知を使い、別workを保持した全体終端だけでHTTPの先行終了を隠しません。

HTTPのfinally開始時には、操作CTSから独立したcleanup CTSを一つ作ります。現在の通常上限は10秒で、client・全server・切断監視と関連資源の回収が同じcleanup tokenを共有します。client取消、fixture停止通知、listener停止・gate解放・accepted clientのローカルclose、実Taskのjoinの順に回収し、操作取消を理由にcleanupを省略しません。複数serverは一方の失敗でも残りの停止とjoinを試行します。待機wrapperの取消を製品取消とせず、元client Taskの実終端と元Taskのawaitから得た取消例外を検証します。同期APIを専用threadで起動したTaskの `Faulted(OperationCanceledException)` も、元結果の取消として扱います。保留responseのpeer EOF/resetは、ローカルcloseや本文gate解放より前に実観測します。一次失敗とcleanup失敗を別に保持し、二次診断または集約例外へ残します。未回収とcleanup期限超過は非成功とし、実所有処理の終端を確認する前にDB・ディレクトリを解放しません。

HTTPの準備を非同期に開始する場合は、開始前からcallerが元の型付き準備Taskと返却資源のdirectoryを所有します。操作待機だけが取り消された後の遅い成功結果・元故障も、同じfinallyとcleanup期限で観測・回収します。methodから `TestCleanup` やusingの解放へ移っても所有を切らず、元HTTP処理・server・関連workerの同じ実終端条件を使います。外側でcleanup期限を更新して待ち直さず、未終端なら依存DB・設定を保持して明示的に非成功とします。未回収資源を別caseへ引き継ぎません。HTTP transportの取消・停止と、処理終端後の通常Disposeも区別します。

HTTPで使う設定はcaseが所有するdirectoryの `TestSettingsFactory.OpenSettings` へ接続し、元Task未終端時にAssemblyCleanupが設定を削除するglobal登録を避けます。既存fixtureの別設定間の値差と非HTTPの既定経路は維持します。AssemblyCleanupを、実処理未終端時に依存資源を破棄してよい例外にはしません。

HTTPを使うMainWindowのfixtureは、HTTPの元Task・全serverの実Completion・内部の受理済みTask・実shutdownを同じcleanup tokenでjoinします。この一つの実終端確認をWindowの通常閉鎖・設定VM・一時DBの通常解放に共用し、callerとfixtureで別の終端基準を使いません。失敗・取消で終端した元Taskも、実終端を確認できた場合は資源回収へ進み、元失敗と回収失敗を保持して報告します。終端未確認なら資源を保持し、元失敗と回収失敗を残してテストを非成功とします。その後の既存 `RunWindowTest` の表示scopeは、失敗時の最終回収としてWindow・HWNDを強制回収します。この最終回収を、実処理の正常終端や通常shutdown成功の証明には使いません。

### プロセスの待機と失敗

開始、完了待機、標準出力・標準エラーの読取り、停止、診断保存、残留確認を一つの管理主体が行います。起動したPID、生成時刻、子孫関係を追跡し、名前だけで無関係なプロセスを停止しません。待機と出力読取りは残りの期限内に収めます。

標準検証の子プロセスは [OwnedProcess](../../../tests/BeMusicSeeker.TestSupport/Processes/OwnedProcess.cs) がUTF-8として出力を読み取ります。Preparationは起動前に `StandardOutputEncoding` と `StandardErrorEncoding` を設定し、Applicationは所有する標準出力・標準エラーのpipeをUTF-8の `StreamReader` で読み取ります。Preparationの子環境には `DOTNET_CLI_FORCE_UTF8_ENCODING=1` を設定します。配布準備で起動する `pwsh` は [DistributionPreparer](../../../tests/BeMusicSeeker.TestSupport/Verification/DistributionPreparer.cs) が子のコマンド先頭で `[Console]::OutputEncoding` をUTF-8へ初期化します。保存時にUTF-8へ変換するだけでは、読取り時の誤変換を直せません。親のコンソール・出力文字コード・カルチャ・環境変数や、この所有境界外のネイティブコマンドの既定の文字コードは変更しません。

失敗時は、実行中または最後に確認したテスト、経過時間、出力、進捗・TRX・停止診断の場所を残します。元の失敗を後片付けの失敗で置換せず、二次的な診断に保持します。元の失敗がなくても後片付けが失敗したら失敗です。開始前の環境変数と作業ディレクトリも復元します。追跡対象ファイルの指紋による外部変更監視は行いません。

### 画面テストの分離

保証の選び方は[画面に関わる保証の分担](test-authoring.md#画面に関わる保証の分担)に従います。以下は必要と判断した境界での実行・資源管理の規則です。

WPFは `BeMusicSeeker.TestSupport.Wpf` の `TestUiDispatcherHost` にある一つの `Application` と専用STA Dispatcherをプロセス内で共有します。最初の `Dispatcher` / `Invoke` / `ProcessQueuedPresentation` / `RunWindowTest` 利用時に遅延起動し、テストごとに作りません。Supportの参照だけではWPF・設定を初期化せず、画面を使わないmoduleでは起動しません。ウィンドウなしの操作は `Invoke`、実ウィンドウ・Popupは `RunWindowTest` と `TestWindowPresentationScope` を使います。

新しいUI moduleの `UiModuleLifetime` はrunRoot準備、必要なsession lease取得、runRoot内のmodule専有profileの `ApplicationPathSnapshot.FromLayout` / `ApplicationPathPolicy.Configure`、日本語リソース基準の順に初期化します。配置確定は `Settings.Default` の初読より前とし、実行体・Assetsは同じbuild出力を指定します。WindowsだけはResourceService・host初読前に同buildのlang JSONを専有profile/langへcopyし、その場所をLanguageDirectoryへ渡します。他UI moduleのlang配置は維持します。必要素材の欠落を通常ユーザー領域へのfallbackで補いません。plain hostではAppの文化一覧を複製しません。初期化とshutdownは既存のmodule寿命へ集約します。

各ケースがOwnedProcessを回収した後、moduleは開始済みhostのWindow・Dispatcher終端とthread join、共有設定fixtureの専有領域、生成済み管理一時sessionの実Task終端、session lease解放の順に回収します。共有設定はhost終了を確認できた場合だけ回収し、host終了失敗時は保持します。設定回収が失敗しても管理一時sessionとleaseの回収を続け、各失敗を集約します。初期化失敗時も取得済みleaseを解放します。未使用の管理一時sessionをcleanupのために生成しません。共通WindowsProcessProbeはleaseを取得せず、親ケースのOwnedProcessが子と両pipeを終端まで所有します。module全体の子process台帳は持ちません。

表示入力の反映には共通の `ProcessQueuedPresentation` を使い、Loaded境界の後で実controlの値・選択・配置を確認します。この補助は機能処理、Background予約、閉鎖や全Dispatcherのidleを保証しません。実処理はconsumerのTaskまたは適用・終端通知を待ち、未完了の否定観測は到達gateを保持して行い、finallyで解放と開始済みTaskの回収を行います。入力TCSの完了だけをconsumer終端としません。成功後に追加作用を持たないイベントハンドラーは、compiled入口の引数・受理状態と委譲Taskの回収、委譲後に追加作用がない静的接続で分担します。

Dispatcher上の通常機能Task待機は、局所期限を加えない `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使います。描画・閉鎖・native解放の通知欠落は同じDispatcher基盤の `AwaitPresentationOnDispatcher` で既定15秒の期限付きに観測します。明示された通知期限と通常Taskの外側期限は別に扱います。テストごとの `Application`・STAスレッド・独自のDispatcher待機ループは作りません。直接の `Dispatcher.PushFrame` や `HwndSource` は共通基盤か明示された例外に限ります。

実Window・Popupのscopeは共有Window/HWND一覧を観測するため、準備から処理終端・後片付けまで非並列にします。同一テストの親子Windowには同じscopeを渡し、独自の入れ子scopeを作りません。Windowを作らず共有状態を変えない `Invoke` はこの理由では直列化しません。実Dispatcherの終了を保証する専用STAと、共通基盤の意図的な入れ子scope検査は、それぞれの保証のために維持します。

MainWindowの生成前からshutdown・Closed・設定購読解除・`Resources["vm"]` 復元までは `MainWindowTestLifetime` が所有します。機能入力と実処理の終端観測は機能側へ残します。compiled内容だけを描画する場合も共通scopeの実Windowを使い、Binding・値・選択・配置の表明を維持します。

constructor-only harnessは `PrepareConstructorOnlyShell` で既存のshell activation境界へ完了済みの初期化を渡し、表示だけのfixtureから本番起動を開始しません。実起動・起動取消・終了の接続は `MainWindowViewHostTests` とPlaylistの `ActualMainWindowFixture` が本番のactivationで検証します。

共有hostの要求は実DispatcherOperationと既存のhost終端通知を観測します。hostが既に終了した場合と、要求の受理後に終了した場合は、所有threadをjoinして元host例外を返します。Dispatcher停止の二次失敗でも終端通知・joinを失わず、二次失敗を元例外へ添付します。健全なhost上のAction自身の失敗は保持し、後続要求は受理します。

`ShowAndWaitForContentRendered` は表示前に通知を購読し、期限付きのDispatcher処理で読込み・描画・非ゼロの配置・HWNDを確認します。任意の `renderNotificationTimeout` はLoaded到達後のContentRendered通知待機だけに適用し、Loaded・Dispatcher・cleanupの期限へ流用しません。モーダル、即時終了、描画通知を制御する場合は、表示直前に `PrepareForOwnedPresentation` を呼びます。

表示の観測待機は、実際の描画・配置・ネイティブ条件の成立を完了条件とします。成立後に、無関係な継続処理による一般的な `ApplicationIdle` 待ちを成功条件へ追加しません。描画通知がない場合、即時終了、ネイティブ観測の失敗、期限超過を成功へ置き換えず、表示観測と後片付けの待機を区別します。

共通基盤は非アクティブ表示専用です。表示直前に手動配置、タスクバー非表示、非アクティブ表示を適用し、全モニターの外へ置きます。HWND生成時には既存の拡張スタイルを保って `WS_EX_NOACTIVATE` を設定・再読取りします。前面でないことと矩形も確認し、Popupにも開く境界で同じ規則を適用します。ネイティブAPIの失敗や表示観測の失敗は、テスト本体とは独立して保持します。

通常テストに前面操作の例外は設けません。実OSカーソルの取得・移動や `Mouse.GetPosition` による物理位置、実フォーカスの取得成功、物理修飾キー、入力キャプチャ、Popupが外部入力で閉じないことを合否条件にしません。イベントを明示しても呼出先がOS状態を参照する場合は分離できていないため、本番の処理へ明示入力を渡す境界で検証します。Popupの内容と接続は、開閉イベント・操作入力、閉じたテンプレートのBinding、明示配置などで確認します。

非公開WPFメソッドによるフォーカス偽装や、テスト専用の本番分岐、外部入力を無視する製品仕様の変更は行いません。実フォーカス移動・IME等のOS接続は[実機確認](#ユーザーに推奨する実機確認)と分担します。共有Dispatcherの待機中には別テストが再入し得るため、Dispatcherの利用だけで排他できると考えず、[資源の分離と共有](#資源の分離と共有)に従います。

開始時の共有スレッドのウィンドウ・HWNDを基準に、追跡したPopup、深い所有関係から順にウィンドウ、購読、Dispatcherの残描画処理、残留HWNDを片付けます。開始時に存在して明示追跡していない別ownerのWindowは、後からHWNDを持っても当scopeの残留破棄へ含めません。そのWindowに属する実native ownerも、既訪問handleで停止する所有チェーンから導出して保護します。閉鎖前に `Closed` を購読し、実閉鎖を待ってから購読を解除します。未開・既閉の対象は区別し、閉鎖取消やWindow/HWNDの残留を成功にしません。機能処理の開始済みTaskは呼出側が回収し、一般的な `ApplicationIdle` はその終端や閉鎖の証明に使いません。設定画面には `CloseForOwnerShutdown` を使います。失敗の優先順位はテスト本体、表示観測、後片付けです。

共有ホストは現在の `AppearanceTheme` を退避し、準備完了前に保存せずLightへ設定します。終了時は同じDispatcherで復元して `Application` とDispatcherを停止し、通知とスレッド結合で待ちます。未起動なら何もしません。既存Applicationとの競合、起動失敗、呼出し・終了失敗は表面化させ、`Application.ResourceAssembly` は変更しません。

### Fullの処理順と期限

`prepare-candidate` が公開可能なHTML付きCurrent、固定Legacy、試験Nextとcatalogを先に生成します。Fullは全体整形・Release build後に、絶対パスで明示したcatalogの検査と候補の展開・接続を行い、Core・Scenarios・Desktopを各一回、Windowsを重複のない3分類で実行します。製品publishやZIP再生成は行いません。明示環境/性能suiteは含めません。Scenariosの必須12IDは `SC-01, SC-02, SC-05, SC-09, SC-10A, SC-10B, SC-11, SC-12, SC-13, SC-14, SC-ExistingData, SC-CurrentUpdate`、Desktopの必須IDは `DT-01, DT-02, DT-03, SC-16, SC-15A` です。同じmodule内の同じIDに複数行を許し、全行Passedを要求します。

Desktopは既存のsession leaseと単一workerで実入力を専有し、Scenariosの後に実行します。各caseは共有suite期限と3分の早い方（更新処理を含むSC-15Aは4分の早い方）を外側期限とし、所有入力と資源の回収に10秒を先取りした操作期限を使います。残時間が回収余裕以下なら新入力を開始しません。実入力後の失敗では最初の検出時刻+10秒と外側期限の早い方を一度だけ確定し、所有Up・source STA・返却済みapp session・fixtureの回収へ渡します。通常回収は操作期限内で行い、遅い完了や回収失敗を成功へ変えません。起動接続の返却前失敗は既存driverの回収を維持します。

| 段階 | 制限時間 |
| --- | ---: |
| 全体整形 | 180秒 |
| restore / build | 各300秒 |
| 候補生成: 固定旧ZIPの取得・検査 | 240秒 |
| 候補生成: Current / Nextのpublish・検査 | 各240秒 |
| Full: 指定候補の検査・Current展開 | 240秒 |
| 全test moduleの共有段階 | 1380秒 |
| 既存データ起動 / 現行更新 / UIからの更新（SC-15A）の局所上限 | 180秒 / 240秒 / 240秒（共有期限を延長しない） |

[VerificationPlan](../../../tests/BeMusicSeeker.TestSupport/Verification/VerificationPlan.cs)と[Runner](../../../tests/BeMusicSeeker.TestSupport/Verification/VerificationRunner.cs)が期限・選択を所有します。候補生成は存在しない専有絶対出力先の `v/`・`next/` に分離し、完成後だけ直下へ `catalog.json` を保存します。任意のmetadata同梱Currentも同じcatalogで保持します。Fullは検査済Currentをrun専有 `application/` に展開し、catalogとそのrootを子へ明示します。通常起動の実行体・assets・lang・nativeを同じinstallationへ揃え、profileはケース専有のままです。更新・故障fixtureの明示installationは維持します。候補指定が片方だけの場合は日常buildへ代替せず失敗します。consumerはcatalogと追跡された旧版identityを照合し、必要なケースでは所有TestWorkspaceへ展開します。再publish、別catalog探索、欠落入力の自動代替は行いません。詳細は[配布物の受渡し](../../../tools/BeMusicSeeker.Verify/README.md#配布物の受渡し)を参照します。

### 公開旧版からの移行とリリース判定

[固定配布物の指定](../../acceptance/v216-first-hop/artifact.json)が示す公開v2.1.6.0 ZIPだけを使用します。サイズは11,260,709バイト、SHA-256は次の値です。

```text
C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2
```

指定のHTTPSリリースURL、メタデータ、サイズ、ハッシュの不一致は失敗です。キャッシュがない場合だけ同じディレクトリの一時ファイルへ期限付きで取得し、検証後に原子的に公開します。既存の不正キャッシュの自動修復、再取得による隠蔽、別版やソースからの代替ビルドはしません。

旧版からの適用はC#の `ReleaseAcceptance` テストで、固定ZIP内の実際のprotocol-1更新プログラムを直接起動します。現行の実配布ZIPを入力とし、期限内の正常終了と出力回収に加え、全管理ファイルの相対位置・内容、新版から除かれた旧管理ファイルの正規配置からの除去を確認します。更新プログラムが再生成する管理一覧は記載されたパス集合を比較し、改行・列挙順のバイト一致は要求しません。`data/`・`config/` と利用者追加ファイルは、更新プログラムの起動前に採取したパス集合・バイト内容と比較します。退避・作業領域を含めた全ツリー一致は要求しません。旧更新プログラムは入力ZIPを消費するため、指定候補を変更せず、同じ内容の検証用コピーを渡します。

旧アプリの画面起動、起動ログ待ち、更新通知や所有モーダルの有無、旧アプリの通常終了、旧版の終了待ちと管理ファイルロック時の特性は、この受入から除きます。別枠の旧GUI受入も設けません。旧更新プログラムが子プロセスを作る場合は所有・回収しますが、画面内容を合格条件にはしません。現行更新プログラムの失敗・復元・終了待ちの検査は維持し、現行配布物の起動とDB・設定の意味上の移行は既存データ起動および現行更新の受入で検証します。

この旧版専用ケースは.NET Framework 4.7.2から.NET 10への配布構成移行を確認する一時的な互換性検査です。移行版の受入を終え、旧配布構成からの直接更新を必須の検査対象から外す判断をした時点で、ケース、固定旧ZIPの指定と専用キャッシュ処理、専用の必須判定、関連資料をまとめて退役させます。旧版番号ごとの検査を恒久的に増やしません。

既存データ起動と更新の受入は、初期設定・ライブラリ構築済みの利用者を表す設定と既存DBを入力にします。設定生成は共通処理を使い、`AssemblyVersion` を実保存形式の `SerializableVersion` XML、その他の単一値の設定を文字列として保存します。更新後の最初の起動は、初期設定からの新規利用ではなく、移行した設定とDBを読み取れるかの確認です。

既存データ起動では、`startup_ready_operable` と `startup_post_initialization_maintenance_complete` の両方を同じ実行期限内で待ってから終了します。必須ローカル処理と必要LR2同期は画面操作可能に先立って終端し、その後の任意外部同期・保守も完了を確認します。終了後に既存の意味保持と同期結果を検査し、処理が終わったことだけで同期成功とは扱いません。

現行配布物の画面起動受入では、予期しない所有ダイアログが可視・有効なら失敗とし、自動で閉じて成功にしません。初回構築通知も自動操作せず、利用済み入力の不備や起動の問題として扱います。初期設定自体の検証を更新受入へ混ぜません。この判定を、画面を起動しない旧更新プログラムのファイル適用検査には適用しません。

`ResultGate` は選択moduleの非空TRX、全行Passed、run-level結果、正常exit、所有終端を確認します。部分skip・不正report・timeout・cleanup失敗を拒否します。必須SC/DT-IDはtestNameの先頭から読み、同一module内の複数行は許可し、別moduleへの重複と必須ID欠落は拒否します。FQN全表や独自DataRow展開は保持しません。

### 解析・大規模データ・外部エンコーダー

通常の解析確認は小さい合成譜面を使います。解析器・復号・譜面情報の保存形式を変える場合は、対象に合う追加検証を明示します。

固定corpusと実toolはCompatibility moduleの `Suite.*` 属性で選択します。suiteの明示選択をopt-inとし、旧 `BMS_TEST_CHART_INFO_FULL` / `BMS_TEST_PRODUCTION_DIFF_FULL` / `BMS_TEST_CHART_INFO_SLOW` の事前ガードは使いません。通常のCore/Windows検証へ固定corpus全件を混ぜません。

固定入力はリポジトリの `BeMusicSeeker.Tests/TestData` を読み取り専用で参照し、build出力へ複製しません。入力・参照DB・外部tool・Java準備JSONの欠如や不正は失敗です。suite一覧、準備、正式入口と直接module実行は[互換検査の案内](../../../tests/BeMusicSeeker.Compatibility.Tests/README.md)を参照します。正式入口は登録済み16suiteから一つを選び、Release・1workerで実行します。suiteの起動・前提検査・本体・通常回収・実exitはDriveRoot/DirectorySymbolicLinkが180秒、他suiteが60秒を共有し、失敗回収10秒は既存の共通cutoffを使います。この初期運用上限はcorpus全体の完了保証ではなく、内側parser/toolの期限でも延長しません。直接moduleの成功を正式入口の成功へ読み替えません。

性能検査は `dotnet run --project tools/BeMusicSeeker.Verify -- performance --suite NAME` を使います。登録名と初期監視上限は[正式入口](../../../tools/BeMusicSeeker.Verify/README.md#明示性能suite)に示します。Release・単一worker・session排他で既存14メソッド/16 MTP行を9suiteへ分け、全3規模・全route・全反復を固定します。正式performanceのfilter、未登録・未指定suiteは準備前に拒否し、部分調査は直接MTPへ分担します。日常の診断writer5契約や小型機能保証を実負荷成功の代用にはしません。

suiteの起動・前提・本文・通常回収・実exitは共通ProcessDeadlineの一期限を共有し、親の失敗回収10秒も一回だけです。監視上限は実行を監視する運用値であり、速度目標ではありません。新しい業務期待やworkloadを増やさず既存保証を移管します。scan/LR2/Folder/Copy/Bmsonは準備・表明・回収を外へ分けて実操作を計時し、既存時計は維持します。800万逆引きや実DB・20 ZIP導入全体の代わりとは扱いません。条件、完了範囲、反復値、結果同等性は[性能仕様](../core/performance-and-scale.md)に従います。

外部エンコーダーはCompatibilityの `ExternalAudioEncoderSmokeTests` を正式 `compatibility --suite audio-encoders` で明示実行します。`EnvironmentIntegration` として専用環境で実行し、`BMS_TEST_AUDIO_ENCODERS=1` を必須とします。指定がなければ失敗です。`BMS_TEST_AUDIO_ENCODER_DIR`、アプリ基準、`libs\x64`、`x64` の順に検索します。`BMS_TEST_AUDIO_ENCODER_TYPES` は `MP3_LAME,AAC_NERO,OPUS,FLAC,OGG_VORBIS` の部分集合です。指定subsetの未知名・カンマリスト内の空要素・重複・実行ファイル不足、または種類指定なしで一つも見つからない場合は失敗です。種類未指定・空文字・全空白は発見分だけを実行します。

生成した短いステレオPCM/WAVを本番の `BassAudioWriter` とエンコード・停止・後片付けへ通し、ファイルの形式識別と採番を確認します。実行ファイル、ライセンス、生成音声をリポジトリの固定入力へ追加しません。

環境条件のDriveRoot（8methods/10rows）、DirectorySymbolicLink（6/6、file linkを含む）、CrossVolume（4/4）、Junction（3/7）はCompatibilityの明示suiteへ所属します。実能力不足をskipや代用入力へ変えません。全Compatibilityは既存UiModuleLifetimeでsession排他・専有profile・日本語辞書を準備し、実hostは遅延生成します。正式runnerは `BMS_TEST_SUITE_DEADLINE` のQPC始点とTimeSpan ticksを子だけへ渡し、lease・updater・probe・junctionの局所期限が元cutoffを延長しないよう制限します。未指定の直接MTPだけ有限既定を使い、明示不正・期限切れは失敗です。Compatibility/Performance/Scenarios/Desktop以外のmoduleと準備processは同変数を明示除去し、親環境は変更しません。親の失敗回収は既存の `min(suite cutoff + 10秒, 検出時刻 + 10秒)` を維持します。期限の再読・不正拒否・gate後の実子importとrunner接続はCore/Verificationの契約testで分担します。

ACLは `compatibility --suite install-acl`（2行）、固定Everythingの実pack/decodeは `compatibility --suite everything-native-path-identity`（2行）、BASSencの実WSH子終了値は `compatibility --suite audio-encoder-child`（2行）を使います。実外部encoderの1行と合わせた4suiteは各60秒・共通失敗回収10秒の初期上限であり、全種類・入力の実測完走保証ではありません。decoderの旧30秒は親suite残とのmin、回収は `min(suite cutoff + 10秒, 失敗検出時刻 + 10秒)` とし、root・両pipe・Jobの終端を確認します。未回収媒体と未解放encoder/sessionを保全し、主失敗とcleanup失敗を残します。

ACLは専有領域の実操作終端後に両DACLを復旧してからworkspaceを回収し、外部rootのACLは変更しません。nativeは必須 `BMS_EVERYTHING_PATH_IDENTITY_FIXTURE` の固定SDK/fixtureを読み、可変DLLコピーをTestWorkspaceに置きます。未指定・不足・能力不成立をskip・成功にせず、準備生成180秒とsuite実行60秒は別枠です。固定nativeは実IPC/indexのSC-07、WSH子は実外部encoderの代替ではありません。正式名・入力・旧3ファイルの後継と準備例は[Compatibilityの説明](../../../tests/BeMusicSeeker.Compatibility.Tests/README.md)にまとめます。入力環境は既存の子環境継承を使い、親processの環境を書き換えません。

### 画面確認

ユーザーの明示依頼によりエージェントが実機で画面確認を行う場合は、[検証方針](#検証方針)に加えて、以下の起動・操作・終了手順に従います。

実アプリを確認するときは、作成したリポジトリ内の `BeMusicSeeker.exe` の正確なパスを指定します。作業ディレクトリと利用データも確認し、インストール版を名前検索で起動しません。起動したPIDと生成時刻を控え、確認後は通常終了を要求して終了を待ち、操作セッションも終了します。無関係な同名プロセスを停止しません。

利用者の操作で画面操作が一時中断された場合は、現在の画面と対象プロセスを確認し、最後の安全な地点から操作を再取得して再開します。一度の中断だけで作業全体を終了しません。利用者が要件を変更した場合は新しい指示を優先します。中止指示や作業の異常終了で確認を終える場合は、一時的な操作権の喪失とは区別し、所有するプロセスと操作セッションを終了します。

画面の表示、入力、完了状態は区別して確認します。単にウィンドウが現れたことやログが静かになったことを、対象操作の成功条件にしません。

### ユーザーに推奨する実機確認

入力・フォーカス経路、表示、機器対応、担当設定などを変更し、実環境との接続を確かめることが有用な場合に、次の項目から関係する確認を推奨します。必須工程やレビューの合格条件にはしません。通常の 局所MTP実行・`verify`・`full` の成功だけで、この範囲を確認済みとはしません。専用CI、操作禁止のPC、入力遮断を通常テストの前提にせず、エージェントへの明示依頼がある場合の起動と後片付けは[画面確認](#画面確認)に従います。

具体的な操作と期待結果は変更対象の機能仕様から選び、条件・対象操作・観測結果を短く示します。以下は確認観点であり、全項目を毎回実施する一覧ではありません。

| 確認観点 | 操作と観測 |
| --- | --- |
| OS入力・フォーカス・IME | 入力欄、候補、行内ボタン、表、メニュー等の変更した経路で、規定のキー・修飾キー・マウス操作と移動先を確認する。IME確定と機能の操作を混同せず、対象外の入力・保存状態を変えない。具体的な挙動は[検索支援](../ui/keyword-search.md)、[外観](../ui/appearance.md)、[一覧](../ui/table-view.md)等の対象仕様に従う。 |
| 表示・DPI・実モニター | 変更した画面を対象に、仕様の範囲でDPI・モニター移動による描画・配置・入力位置の対応と操作継続を確認する。 |
| 音声機器 | 利用可能な機器・出力方式で、変更に関係する再生・音量・終了・切断・再接続を確認する。条件と期待結果は[音声仕様](../runtime/audio.md#診断と実機確認)に従う。 |
| 担当の起動・設定適用・入力継承 | 変更した担当を起動し、指定の役割・モデル・権限と、引継ぎ・履歴の受渡しを確認する。設定と引継ぎの静的な整合確認とは区別する。 |

確認時の外部操作による中断は製品不具合と区別します。未確認は未確認として報告し、通常テストのスキップや成功扱いに置き換えません。未実施の扱いは[検証方針](#検証方針)に従います。対象機能の廃止、または同じ接続を外部入力から独立して検証できるようになった場合は該当手順を退役します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 通常開発と正式リリース前の検証区分、変更に応じた追加検証 | 本書の[実行範囲の選択](#実行範囲の選択)、[開発指針](../../../AGENTS.md#文書と検証)、[リリース手順](release.md#配布物の生成と検証)、[エージェント運用](agent-workflow.md#統合と予備レビュー) | 作業段階・公開候補の確定と各文書の案内を照合する。運用文言を固定する自動テストは設けない。 |
| 複数ワークツリーの検証と結果の再利用 | 本書の[複数ワークツリーの検証](#複数ワークツリーの検証)、[並列作業](agent-workflow.md#並列作業)、[通常の統合手順](release.md#通常の作業ブランチの統合) | 全体ルートによる `verify`・性能測定の開始調整と単位責任者の 局所MTP実行 自主管理、別ワークツリーの必要ビルドとの並行、資源衝突と実測負荷、対象版・共有依存、移管時の結果引継ぎ、検証版と統合版の一致を照合する。運用文言を固定する自動テストは設けない。 |
| module選択・共有期限・全Passed | [VerificationRunner](../../../tests/BeMusicSeeker.TestSupport/Verification/VerificationRunner.cs) | CoreのVerificationPlanTests / VerificationRunnerTests / ResultGateTestsが選択module・必須ID・filter・非空結果・全行Passed・元期限の継承を確認する。 |
| テスト整理前後の比較 | [検証入口](../../../tools/BeMusicSeeker.Verify/README.md)、本書 | 同条件のwall time、全終端、結果件数を比較し、移管と速度目標を区別する。 |
| 手動・標準の整形、前段失敗 | [RepositoryFormatter](../../../tests/BeMusicSeeker.TestSupport/Verification/RepositoryFormatter.cs)、[手動PS](../../../scripts/format-code.ps1) | CoreのRepositoryFormatterTests / VerificationRunnerTestsがGit三集合、All、NoRestore、同期限、実adapter、元診断・非zeroと停止を確認する。 |
| 各段階の期限・診断・停止 | [検証スクリプト群](../../../scripts) | 実行に使う期限とプロセス所有情報、失敗時の診断・残留を確認する。 |
| 失敗時の差し戻し・再実行 | [エージェント運用](agent-workflow.md#不備の差し戻し)、本書 | 修正担当への差し戻し、同一版の再実行と修正後の検証、障害解決の条件を区別する。 |
| UTF-8両pipe・所有child・親環境 | [OwnedProcess](../../../tests/BeMusicSeeker.TestSupport/Processes/OwnedProcess.cs) | CoreのOwnedProcessTests / OwnedProcessPipeTests / VerificationRunnerTestsで全終端、期限・元失敗・診断保存を確認する。 |
| 一時資源・共有状態・通常完了の待機 | [TestSettingsFactory](../../../tests/BeMusicSeeker.TestSupport/Components/TestSettingsFactory.cs)、[Coreのmodule寿命](../../../tests/ModuleInitialization/ModuleLifetime.cs)、[UIのmodule寿命](../../../tests/ModuleInitialization/UiModuleLifetime.cs)、各機能のテスト準備 | 所有・復元、通知前の失敗、待合せの解放、実際の完了条件を確認する。UIのhost終了成功後だけ設定を回収し、設定回収失敗後も一時session・leaseを回収する順序は制御フローで静的確認する。失敗注入の実施とは扱わない。 |
| Dispatcherの受付・終端・通常Task待機 | [`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs) | [`DispatcherHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/DispatcherHostTests.cs)。既終端・受理後終了と停止二次失敗、Action失敗後の継続、join、consumerの成功・失敗・取消・通知欠落、単一STAを確認する。 |
| 画面の準備、表示、破棄 | [`TestWindowPresentationScope`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestWindowPresentationScope.cs) | [`SettingsControlPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsControlPresentationTests.cs)、[`WindowPresentationScopeTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WindowPresentationScopeTests.cs)。一般idleと別に実描画・配置・Win32結果、入れ子の所有資源、失敗優先順位を確認する。 |
| 共有hostのテーマ・意味別リソース | [`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs) | [`WpfThemeResourceTests`](../../../tests/BeMusicSeeker.Windows.Tests/Themes/WpfThemeResourceTests.cs)。Lightと実コンストラクターの意味別ブラシへの到達を確認する。 |
| UI moduleの配置と終了所有 | [`UiModuleLifetime`](../../../tests/ModuleInitialization/UiModuleLifetime.cs)、[`TempDirectoryPublisher`](../../../BeMusicSeeker/TempDirectoryPublisher.cs) | 初期化・例外時を含む回収順序とlease解放は静的確認。[`WpfHostLifecycleTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WpfHostLifecycleTests.cs) と共通probeの [`WpfHostLifecycleProbe`](../../../tests/Fixtures/WindowsProcessProbe/WpfHostLifecycleProbe.cs) は同じSupport hostを使い、子の生存中にHWND・実thread・temp sessionの消失を確認し、release後に全childと両pipeを回収する。 |
| loopback HTTPの共有操作期限・独立cleanup、通知前のclient/server終端、実取消とpeer切断 | [`SingleRequestHttpServer`](../../../tests/BeMusicSeeker.TestSupport/Components/SingleRequestHttpServer.cs)、HTTPを使う各機能fixture | [`AppHttpClientTests`](../../../tests/BeMusicSeeker.Core.Tests/Runtime/AppHttpClientTests.cs) は有限二応答の未要求accept回収、取消済みcleanup tokenで未終端clientを成功扱いしないこと、補助producer正常終了後のclient先行故障、通知有無と同時終端・元故障、準備待機取消後の遅い結果・故障、GET/formの元要求取消とlocal解放前の実peer切断を確認する。PlaylistのURL acquisition/completionは、未終端処理の依存DB・設定をTestCleanupでも保持して非成功にすることをgateで確認する。Playlist/MainWindowは同じ期限・回収入口で既存の取得・保存・表示保証を保持する。 |
| 公開旧版の適用・現行更新 | [Scenarios/Update](../../../tests/BeMusicSeeker.Scenarios.Tests/Update) | LegacyUpdaterDistributionMigrationTests / UpdaterPackageSyncTestsとSC-CurrentUpdateで実配布の適用・保存・再起動を確認する。UIの更新ダイアログからの提示・適用・別PIDの再起動と同じ配信での非循環は、[Desktop.E2E/Update](../../../tests/BeMusicSeeker.Desktop.E2E.Tests/Update)のSC-15Aが実UIAで確認する。 |
| 利用済み設定・既存DBの移行 | [Scenarios/Acceptance](../../../tests/BeMusicSeeker.Scenarios.Tests/Acceptance) | PublishedExistingDataAcceptanceTestsでStandalone/LR2の各2起動、意味保持・設定安定と元入力保持を確認する。 |
| 予期しない所有モーダルの拒否 | [PublishedApplicationSession](../../../tests/BeMusicSeeker.TestSupport/Scenarios/PublishedApplicationSession.cs) | SC-ExistingData / SC-CurrentUpdate / SC-16が実所有Windowと通常終了を観測し、自動応答で異常を隠さない。schema確認と初期化失敗の通知は所有modalの観測で検出する。 |
| 外部エンコーダー | [`BassAudioWriter`](../../../BeMusicSeeker/Ribbit/Media/BassAudioWriter.cs) | [`ExternalAudioEncoderSmokeTests`](../../../tests/BeMusicSeeker.Compatibility.Tests/Playback/ExternalAudioEncoderSmokeTests.cs) |

## 関連資料

[テスト設計](test-authoring.md)、[エージェント運用](agent-workflow.md)、[リリース](release.md)、[テスト入力の案内](../../../BeMusicSeeker.Tests/TestData/README.md)。
