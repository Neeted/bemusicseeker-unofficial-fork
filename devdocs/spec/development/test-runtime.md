# テストの実行基盤

## 目的と適用範囲

テストと検証基盤を書く・直すときに守る、実行時間・並列実行・テスト区分・資源の分離・非同期処理の待機・外部プロセス・解析と大規模データの規則を定めます。検証の実行範囲・期限・結果判定は[テストの実行と受入検証](testing.md)、保証対象とテストの追加・変更・削除は[テスト設計](test-authoring.md)、画面テストの規則は[画面テストの分離](ui-test-isolation.md)に従います。

## 用語

[共通用語集](../glossary.md)と[テストの実行と受入検証](testing.md#用語)の用語を使います。コードの識別子は原表記を使います。

## 仕様

### テスト時間の短縮

テスト群を整理して実行時間を短縮する場合は、次の方針に従います。短縮は必要な保証を維持したうえでの改善であり、保証の整理と時間の改善は別々に判定します。

#### 比較の方法

同じWindows環境・同じ測定範囲で、変更前の標準 `verify` 一回と最終版の一回を比較することを基本にします。現行版の実行条件が分かる結果があれば基準に使えます。平均や分散を得るためだけの複数回実行は要求しません。反復中は関連する条件を対象moduleの局所MTP実行で確認し、最終版の検証区分と同一版の再実行は[実行範囲の選択](testing.md#実行範囲の選択)と[実行期限と失敗分類](testing.md#実行期限と失敗分類)に従います。`full` を構成する一部の短縮は、まずその部分（対象module・suite・段階）を直接実行して確認し、最初の手段に `full` の通し実行を使いません。通しの所要や統合した状態を確かめる目的があれば `full` を実行して構いません。`full` の結果を比較に使う場合は、対象・構成が同じ範囲だけを使います。

比較するのはテストプロセスの全体経過時間です。原因の調査では、ホスト別の終了時刻、長いケース、同じ時間帯の待機・共有資源から残る負担を調べます。並列ケースの所要時間を単純合計して全体時間や短縮可能時間と見なさず、スクリプト全体の時間とも区別します。条件が違う結果を同条件の改善実績とは扱いません。

#### 短縮の手段

次の順に検討します。

1. [不要・重複する保証の整理](test-authoring.md#既存テストの採否)
2. 保証に関係しない準備の削減
3. 共有資源に基づく直列化の見直し
4. 通常完了を固定時間で待つ処理の解消

Windowを使わなくしたことだけで `DoNotParallelize` を外さず、設定・文化圏・共有通知等の所有を確認します。通常時に消費しない待機上限を短くすることは、短縮の根拠になりません。残ったホスト間の偏りが実測で分かる場合に分割・配分を検討し、先にホスト数やworker数を増やしません。

次の手段は短縮として扱いません。

- 必要なケースを対象外の検証区分へ移す。
- スキップを増やす。
- 巨大なループへまとめて件数だけを減らす。
- 成功するまで再実行する。
- 制限時間を緩める。

#### 目標時間と報告

目標時間を定める場合は、その短縮作業の計画に置きます。目標は標準入口の制限時間の範囲内で定め、制限時間を延ばして目標を満たしたことにはしません。

報告には、実行件数、スキップとその理由、実測時間、未実施の検証区分を含めます。目標を超えた場合は実測時間と未達を示します。結果は作業用の生成物として使い、時間履歴の恒久台帳や自動配分の保存状態は追加しません。

成功した実行が目標を超えただけでは、追加調査・再実行を始めません。利用者から調査を依頼された場合は、テスト別の所要時間だけでなく最長ホストの遅延区間を確認し、ロックの保持側やDBの確定回数など共通処理まで追います。

### 通常テストの分割と並列実行

標準入口は、次の分割でmoduleを実行します。具体的なworker数の算出と実行順の詳細は[所有と判定](../../../tools/BeMusicSeeker.Verify/README.md#所有と判定)に示します。

- Windows moduleは同じアセンブリを、カテゴリ `Windows.Shell`（MainWindow・Playlist）、両カテゴリ以外、`Windows.Settings` の3実行に分け、各実行を1workerで動かします。カテゴリのないケースは両カテゴリ以外の実行へ入ります。追加のfilterは、分類条件とANDで組み合わせます。
- CoreはWindowsと並行し、複数workerで実行します。走査・同期などの製品パイプラインはブロッキング段を専用スレッドで実行するため、テストプロセスでスレッドプールのworker下限を変更しません。
- `dev`・`verify` のDaily Scenariosは、UIを使う実行の最初の枠で開始します。module全体で利用者セッションの排他を持つため、排他を取得するWindowsのケースとの重なりを段階の冒頭に限ります。`full` では、Windowsの全実行が終端してからScenariosを開始し、Scenariosの後にDesktopを実行します。
- 同じprojectを実行ごとに別々にビルドせず、全実行で一つのビルド結果を共有します。実行ごとにrunsettings・TRX・実行領域・profile・プロセスの所有単位を分けます。

ケースの分配はMTPのメソッド単位の並列化へ任せ、`DoNotParallelize` は標準属性として扱います。

ケースの入力と可変ファイルは、標準入口が用意する実行領域の下に置きます。直接MTP実行でも同じ基底を使います。リポジトリの監視下やOSのシステム一時フォルダーへケース入力を置きません。ケースの `TestWorkspace`・`PreserveArtifacts` は維持し、TRX・ログ・runsettings・配布候補の目録は `artifacts` 側に残します。実行領域の配置は[所有と判定](../../../tools/BeMusicSeeker.Verify/README.md#所有と判定)に示します。

UIの利用者セッションを専有するための排他（session lease）は、moduleごとに次のように取得します。所有する一時領域と入力の分離は[資源の分離と共有](#資源の分離と共有)に従います。

| module | 排他の取得 |
| --- | --- |
| Windows | module全体では取得しない。実Activateを使う `PlaylistLampViewerWindowManagerTests` だけが、UIスレッド外でケース前に取得し、Windowと開始済み処理の回収後に解放する。 |
| Scenarios・Desktop・Compatibility・Performance | module全体で取得する。 |
| Core | 取得しない。実Window・プロセス全体の可変設定を所有しない。 |

### テスト区分

実行集合はmoduleとprofileで決めます。Core・Windowsの通常ケース、ScenariosのDaily・Full、`full` のDesktop、Compatibility・Performanceの明示 `Suite.*` を区別します。`ProcessIntegration` 等の属性がケースに残っていても、標準入口の選択・除外には使いません。明示した導入先が消失した後の再読込・再選択はScenariosのFull、実入力と外部UIAの接続はDesktopで確認します。

環境不足を `Ignore` / `Inconclusive` で飛ばさず失敗にし、未実施を成功として扱いません。[BMS譜面位置の比較ツール](chart-parse-audit.md)は簡易開発用です。変更確認は対象RunnerのReleaseビルドと差分点検に限定し、恒常的なツールのテストは設けません。

Windows moduleの実プロセス境界は、[`ExternalPlayerProcessGatewayTests`](../../../tests/BeMusicSeeker.Windows.Tests/Processes/ExternalPlayerProcessGatewayTests.cs)、[`UpdaterProcessGatewayTests`](../../../tests/BeMusicSeeker.Windows.Tests/Update/UpdaterProcessGatewayTests.cs)、[`UpdaterDeploymentBoundaryTests`](../../../tests/BeMusicSeeker.Windows.Tests/Update/UpdaterDeploymentBoundaryTests.cs) が日常検証で担います。`ProcessIntegration` 属性を持つ更新マーカーのケースも、module全件を選択する標準入口では日常検証に含まれます。これらは共通のWindows probeまたは `OwnedProcess` で子プロセスを所有し、起動前から全終端までの制限時間と、一度だけ確定する独立した回収期限を使います。製品側の元の期限や停止の表明を、回収による停止で代替しません。各ケースの制限時間は[検証入口の説明](../../../tools/BeMusicSeeker.Verify/README.md)に示します。

カテゴリは実行特性を表します。小さい互換性テストや、少数の実部品を接続する検証は通常検証に含められます。`ProcessIntegration` は別プロセス等を扱う区分であり、主体間の接続を確認するすべてのテストを指しません。

### 資源の分離と共有

通常テストは固有の一時ファイル・DB・設定と、再現可能な入力を使い、追跡対象ファイルや利用者のデータを変更しません。公開サービスの可用性、ローカルサービスの索引反映、同じPCでの利用者の操作を合否条件にしません。EverythingやOS走査を利用する機能は、取得範囲と完全性を明示した変更不能な入力集合で検証し、作成直後のファイルが外部索引に現れることを前提にしません。外部環境との接続そのものを確認する場合は、対応する検証区分と条件を明示します。

環境依存を見つけた場合は、保証と必要資源から次の四つへ分類します。準備場所が一時ディレクトリでも、OS全体や利用者セッションへ影響する操作は通常I/Oと区別します。

| 分類 | 適用する条件 |
| --- | --- |
| 日常検証に残す | 所有する一時領域での通常I/O・SQLite・設定、読取り専用属性・共有ロック、明示的に注入した失敗等で保証を再現できる。 |
| 依存をなくす | 保証に不要なドライブ割当や一時フォルダーのパス長への依存を外し、同じ本番処理・観測を所有する一時領域で維持できる。 |
| 専用環境で明示実行する | 実ACL、シンボリックリンク・ジャンクション、ドライブの別名、別ボリューム、HKCUの `Run`・`RunOnce`、外部のネイティブ部品・ツールそのものとの接続が保証対象。`EnvironmentIntegration` を付ける。 |
| 削除する | 必要な保証が既存の担当へ移管済み、または契約上不要。移管先の表明まで確認して旧ケースと不要な補助を除く。 |

専用環境は利用者の通常データ・設定から隔離し、ACL変更・リンク作成・ドライブ別名の割当と解除・HKCU回復登録の作成と回収が可能なWindowsを用意します。`DoNotParallelize` は別利用者や別プロセスとの競合を防がないため、準備した環境でだけ明示実行します。処理・出力の終端後に所有資源を回収し、作業ディレクトリを変更したケースは復元してからドライブ別名を解除します。準備不足・権限不足は失敗です。

クロスボリューム試験は、`BMS_TEST_CROSS_VOLUME_SOURCE_ROOT` と `BMS_TEST_CROSS_VOLUME_DESTINATION_ROOT` に、既存の書込み可能な入力の基点を絶対パスで指定します。両者は異なるローカルドライブのルート配下で、かつ異なる実ボリュームでなければなりません。[GetVolumePathNameW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamew) で末端のマウントポイントを取得し、[GetVolumeNameForVolumeMountPointW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumenameforvolumemountpointw) のボリュームGUIDで同一実体を拒否します。別名だけを別ボリュームと見なしません。環境の探索や書込みの試行は行わず、指定した基点の下へケース固有の子だけを作成・回収し、基点自体は保持します。

外部依存は対象に応じて用意します。Everythingのネイティブ接続は `BMS_EVERYTHING_PATH_IDENTITY_FIXTURE` に固定SDKとfixture bridgeの基点を指定し、7-Zip接続は既存のProgram Files配置に7-Zip CLIを導入します。Java参照ツールは `JAVA_HOME` またはPATHでJDKを用意し、[`chartstring-dump/run.ps1`](../../../tools/chartstring-dump/run.ps1) の参照リポジトリと固定譜面を準備します。外部エンコーダーは[外部エンコーダー](#解析大規模データ外部エンコーダー)の環境変数を使います。これらの専用保証はCompatibilityの対応suiteで明示実行します。

DBの準備・観測は検証対象の処理と分けます。入力作成だけの複数行投入・スキーマ準備は一つのトランザクションへまとめ、SELECTだけの観測には既存の読取り専用接続を使います。無関係な一時DBの観測がプロセス共通の書込みロックを取り合わないようにします。スキーマ作成・確定境界自体を保証するテストでは、対象の本番処理とトランザクション境界を維持します。

共有設定、リソース辞書、文化圏、テーマ、ウィンドウ一覧などを変更する場合は、準備から復元までの所有を確認します。`DoNotParallelize` は分離不能な共有資源とその復元方法を説明できる範囲に使い、独立した状態だけを扱うテストは並列実行します。この属性は他プロセスや利用者の操作を排他しません。外部入力の分離とテスト間の共有状態は別々に確認します。

通常のテスト群の設定入力には、固定したスナップショット、専用パスの `Settings`、メモリ上の保存先を使い、暗黙の `Settings.Default` や利用者設定の保存先へ戻しません。設定保存の保証には専用パスの実保存処理を使います。日本語リソースの通常の基準は `AssemblyInitialize` で一回だけ準備します。実際の文化圏・テーマの切替と共有通知への購読は、準備から処理の終端・購読解除・復元まで所有します。標準の画面構成はWindowがなくても共有通知を購読するため、その寿命も非並列にします。

### 非同期処理の待機

通常完了は、対象のTask、イベント、状態遷移に直接結び付く通知で待ちます。通知より先に処理が失敗した場合も観測し、来ない通知だけを待ち続けません。テストが閉じた待合せは `finally` で解放し、所有する処理の終結と後片付けを待ちます。

成功通知の到達待ちは、`TestUiDispatcherHost.AwaitNotificationAsync` で通知を生成する実Taskの失敗・取消にも接続します。通知なしで実処理が終わった場合は、その場で未到達として失敗します。生成元が失敗を結果通知へ変換する管理主体では、その管理主体の実際の終端を使います。別に予約されたUI受信は生成側のworkerの完了で代用せず、受信処理自身を所有します。MainWindowの寿命は `CloseCompletion` で終了要求からDispatcherと終端処理まで待ち、通知前の失敗も `Closed`・共有リソースの回収と併せて観測します。

単なる到達・完了確認には局所的な制限時間を追加せず、停止は検証全体の制限時間で検出します。局所的な制限時間は、外部プロセス、ループバックHTTPのfixture、画面表示、解放、ロックが成立しないことの確認、時間超過の契約の失敗検出に限ります。標準入口の期限・回収を偽プロセスで検査するテストの到達待ちは、外部プロセスでも[プロセスの待機と失敗](#プロセスの待機と失敗)の特則に従い、局所的な制限時間を置きません。固定の `Thread.Sleep` や余裕時間としての `Task.Delay` から成功を推測しません。短い否定観測は、通常完了を待つ方法や時間保証の証明とは区別します。

ロックが成立しないこと・デッドロックの確認は、probeを専用スレッドで動かし、保持側が判定まで保持したまま「保持中に完了したか」で判定します。判定は `ConcurrencyProbe.AssertCompletesWhileHeld`・`AssertCompletesWhileHeldAsync`・`AssertSignalsWhileHeld` で行います。`AssertCompletesWhileHeld(Async)` では保持側のTaskを `holder` に、`AssertSignalsWhileHeld` では通知の生成元Taskを `producer` に渡し、それらの先行終端をその場で失敗にします。期限にはTestSupportの `ConcurrencyProbe.StallTimeout` だけを使い、正しい実装の合否には使いません。`ManualResetEventSlim` や条件の成立を同期的に待つ到達待ちは `ConcurrencyProbe.WaitForSignal`・`WaitUntil` で、期限なしに通知または生成元の終端まで待ちます。非同期の成功通知は前段の `AwaitNotificationAsync` を使います。製品のcallback内など、その場で投げられない箇所では `WaitCompletedWhileHeld` で完了の有無だけを保持し、保持の解放後にprobeを回収してから表明します。

不安定なテストは共有状態、順序依存、待機、資源競合を調べます。制限時間の延長、再試行の増加、実行担当数や分割数の削減だけで失敗を隠しません。同一版の再実行は[実行期限と失敗分類](testing.md#実行期限と失敗分類)に従います。

### HTTP fixtureの待機と回収

ループバックHTTPのfixtureを所有するケースは、操作の待機と後片付けの待機に別々の制限時間を一つずつ使います。現在の通常の上限は操作が30秒、後片付けが10秒です。これらは製品のHTTP期限を仮想時刻で検査する値とは区別します。

#### 操作の待機

開始時に `TestContext.CancellationToken` と連動する操作の取消源を一つ作り、操作全体の監視期限を一度だけ設定します。接続の受付・要求の読取り・応答の書込み・待合せ・各段階とクライアント完了の待機は同じ取消を共有し、段階ごとに期限を延長しません。

- 段階の通知だけを待たず、クライアントの結果とサーバーの実際の終端も監視し、先行した故障や通知なしの終端を失敗にします。
- 実際の生成側の終端を観測した後にも通知を再確認し、同時に終端した通知を失いません。正常終了した補助の生成側は待機の対象から除きます。
- 元のTaskを公開しない管理主体では、対象固有の既存の終了通知を使います。別の処理を保持した全体の終端だけで、HTTPの先行した終了を隠しません。

#### 後片付け

`finally` の開始時に、操作の取消源から独立した後片付けの取消源を一つ作ります。クライアント・全サーバー・切断監視と関連資源の回収は、この取消を共有します。

- クライアントの取消、fixtureの停止通知、リスナーの停止・待合せの解放・受け付けた接続のローカルな切断、実Taskの合流の順に回収します。操作の取消を理由に後片付けを省略しません。
- 複数のサーバーは、一方が失敗しても残りの停止と合流を試みます。
- 待機用のラッパーの取消を製品の取消とは見なさず、元のクライアントTaskの実際の終端と、元のTaskの `await` から得た取消例外を検証します。同期APIを専用スレッドで起動したTaskの `Faulted(OperationCanceledException)` も、元の結果の取消として扱います。
- 保留中の応答に対する相手側の切断（EOF・リセット）は、ローカルの切断や本文の待合せの解放より前に実際に観測します。
- 一次失敗と後片付けの失敗を別に保持し、二次診断または集約例外へ残します。未回収と後片付けの期限超過は非成功とし、実際に所有する処理の終端を確認する前にDB・ディレクトリを解放しません。

#### 非同期の準備と資源の所有

HTTPの準備を非同期に開始する場合は、開始前から呼出し側が元の型付きの準備Taskと、返される資源のディレクトリを所有します。操作の待機だけが取り消された後の遅い成功結果・元の故障も、同じ `finally` と後片付けの期限で観測・回収します。

- メソッドから `TestCleanup` や `using` による解放へ移っても所有を切らず、元のHTTP処理・サーバー・関連workerの同じ実終端条件を使います。
- 外側で後片付けの期限を更新して待ち直しません。未終端なら依存するDB・設定を保持し、明示的に非成功とします。未回収の資源を別のケースへ引き継ぎません。
- HTTP通信の取消・停止と、処理の終端後の通常の `Dispose` は区別します。

HTTPで使う設定は、ケースが所有するディレクトリの `TestSettingsFactory.OpenSettings` へ接続し、元のTaskが未終端のときに `AssemblyCleanup` が設定を削除する全体登録を避けます。既存fixtureの別設定間の値の差と、HTTPを使わない既定経路は維持します。`AssemblyCleanup` を、実処理の未終端時に依存資源を破棄してよい例外にはしません。

#### MainWindowを使うfixture

HTTPを使うMainWindowのfixtureは、HTTPの元のTask・全サーバーの実際の完了・内部で受理したTask・実際の終了処理を、同じ後片付けの取消で合流させます。この一つの実終端確認を、Windowの通常の閉鎖・設定ViewModel・一時DBの通常の解放に共用し、呼出し側とfixtureで別の終端基準を使いません。

失敗・取消で終端した元のTaskも、実際の終端を確認できた場合は資源の回収へ進み、元の失敗と回収の失敗を保持して報告します。終端を確認できなければ資源を保持し、元の失敗と回収の失敗を残してテストを非成功とします。その後の既存 `RunWindowTest` の表示範囲は、失敗時の最終回収としてWindow・HWNDを強制回収します。この最終回収を、実処理の正常終端や通常の終了処理の成功の証明には使いません。

### プロセスの待機と失敗

開始、完了待機、標準出力・標準エラーの読取り、停止、診断保存、残留確認を一つの管理主体が行います。起動したPID、生成時刻、子孫関係を追跡し、名前だけで無関係なプロセスを停止しません。待機と出力読取りは残りの制限時間内に収めます。

標準検証の子プロセスの出力は、[OwnedProcess](../../../tests/BeMusicSeeker.TestSupport/Processes/OwnedProcess.cs) と配布準備の [DistributionPreparer](../../../tests/BeMusicSeeker.TestSupport/Verification/DistributionPreparer.cs) が、所有する境界の中でUTF-8として読み取れるように設定します。保存時にUTF-8へ変換するだけでは、読取り時の誤変換を直せません。親プロセスのコンソール・出力文字コード・カルチャ・環境変数や、この所有境界外のネイティブコマンドの既定の文字コードは変更しません。

失敗時は、実行中または最後に確認したテスト、経過時間、出力、進捗・TRX・停止診断の場所を残します。元の失敗を後片付けの失敗で置換せず、二次的な診断に保持します。元の失敗がなくても後片付けが失敗したら失敗です。開始前の環境変数と作業ディレクトリも復元します。追跡対象ファイルの指紋による外部変更監視は行いません。

標準入口の期限・回収を偽プロセスで検査するテストは、次を守ります。

- readyファイルや起動の通知は、生成元（所有するプロセスのJob全体の終端、またはrunnerの実行）と結んで期限なしで待ち、生成元が通知なしで終端したらその場で失敗にします。停止はrunnerの段階期限と検証全体の期限で検出します。
- 壁時計の失効そのものを検査する窓だけが予算を持ちます。保証に無関係なプロセスの起動を窓から除き、窓の前提が崩れた場合は前提不成立として失敗させます。検査対象でない段階期限は既定値のままにします。
- 回収期限は、runnerの回収経路が開始した証拠（ready後にゲートで止めた所有プロセスのように、取消による回収以外では終端しないプロセスのJobの終端など）を得た後に読みます。テストの読取りに伴う中断が、runnerの中断経路が働かない回帰を覆い隠さないようにするためです。テスト側の正常終了要求で回収猶予を消費して、回収が間に合わない状態を作りません。

`VerificationRunnerTests` には、到達待ちに局所期限を残すケース（整形と同時のビルド、UI実行枠の配分、`SuccessfulCoreDoesNotStartTheLaterUiCleanupBudget` の後始末の15秒など）、検査対象でない段階期限の上書き（`UiExecutionsRespectBudgetAndStartDailyScenariosInTheFirstSlot` の段階期限30秒）と、保証と無関係なプロセスの起動を含む経過時間の上限（整形の失敗で取り消した同時のビルドの所要）があります。これらは上の規則へ移行する対象です。

リリーススクリプトを実プロセスで通しで実行する `ReleaseScriptVersionSourceTests` は、ケースごとに一つの期限だけを持ち、その内側にコマンドごとの入れ子の予算を置きません。

### 解析・大規模データ・外部エンコーダー

通常の解析確認は小さい合成譜面を使います。解析器・復号・譜面情報の保存形式を変える場合は、対象に合う追加検証を明示します。

#### 互換検査

固定入力と実ツールを使う検査は、Compatibility moduleの `Suite.*` 属性で選択します。suiteの明示選択が実行の条件であり、環境変数による事前の有効化は使いません。通常のCore・Windows検証へ固定入力の全件を混ぜません。

固定入力はリポジトリの `BeMusicSeeker.Tests/TestData` を読取り専用で参照し、ビルド出力へ複製しません。入力・参照DB・外部ツール・Java準備JSONの欠如や不正は失敗です。

正式入口は登録済みのsuiteから一つを選び、Release・1workerで実行します。suiteごとの制限時間は、起動・前提検査・本体・通常の回収・実際の終了で共有し、失敗時の回収は共通の10秒を一度だけ確定します。この制限時間は固定入力全体の完了時間の保証ではなく、内側の解析器・ツールの期限でも延長しません。直接moduleの成功を正式入口の成功へ読み替えません。suiteの一覧・入力・準備・制限時間・直接実行の方法は[互換検査の案内](../../../tests/BeMusicSeeker.Compatibility.Tests/README.md)を正本とします。

環境条件のsuite（DriveRoot、DirectorySymbolicLink、CrossVolume、Junction）も、実能力の不足をskipや代用の入力へ変えません。Compatibilityの全体は既存の `UiModuleLifetime` で利用者セッションの排他・専有profile・日本語辞書を準備し、実ホストは必要なケースで遅延生成します。

#### suiteの共有期限

正式runnerは、suiteの制限時間の始点と長さを `BMS_TEST_SUITE_DEADLINE` で子プロセスだけへ渡します。

- 子プロセス側の排他の待機・更新プログラム・probe・ジャンクション・外部ツールの局所的な上限は、この共有期限を延長しないよう、早い方に制限します。
- 未指定の直接MTP実行だけ、有限の局所的な既定値を使います。明示された値が空・不正・期限切れなら失敗です。
- Compatibility・Performance・Scenarios・Desktop以外のmoduleと準備用のプロセスには、この変数を明示的に除いて渡し、親プロセスの環境は変更しません。
- 親の失敗時の回収は、`min(suiteの期限 + 10秒, 失敗検出時刻 + 10秒)` を維持します。

期限の再読取り・不正値の拒否・実際の子プロセスへの引渡しとrunnerとの接続は、Core・Verificationの契約テストで分担します。

#### ACL・固定ネイティブ・外部音声

ACL、固定Everythingの実ネイティブ処理、BASSencの実WSH子プロセスの終了値、実外部エンコーダーは、それぞれ専用のsuiteで明示実行します。

- ACLの検査は、専有領域の実操作の終端後に両方のDACLを復旧してからworkspaceを回収し、外部のルートのACLは変更しません。
- 固定Everythingのネイティブ検査は、必須の `BMS_EVERYTHING_PATH_IDENTITY_FIXTURE` の固定SDK・fixtureを読み、可変のDLLコピーを `TestWorkspace` に置きます。準備の生成とsuiteの実行は別の制限時間です。
- 未指定・不足・能力の不成立をskip・成功にしません。
- 固定ネイティブの検査は実IPC・索引の `SC-07` の、WSH子プロセスの検査は実外部エンコーダーの代替ではありません。
- デコーダーの既存の待機は、親suiteの残り時間との早い方に制限します。ルート・両方のパイプ・プロセスの所有単位の終端を確認し、未回収の媒体と未解放のエンコーダー・セッションを保全して、主失敗と後片付けの失敗を残します。

入力環境は既存の子プロセス環境の継承を使い、親プロセスの環境を書き換えません。準備例と各suiteの入力は[互換検査の説明](../../../tests/BeMusicSeeker.Compatibility.Tests/README.md#acl固定native外部音声)にまとめます。

外部エンコーダーは、Compatibilityの `ExternalAudioEncoderSmokeTests` を正式の `compatibility --suite audio-encoders` で明示実行します。`EnvironmentIntegration` として専用環境で実行し、`BMS_TEST_AUDIO_ENCODERS=1` を必須とします。指定がなければ失敗です。

- 実行ファイルは `BMS_TEST_AUDIO_ENCODER_DIR`、アプリの基準位置、`libs\x64`、`x64` の順に検索します。
- `BMS_TEST_AUDIO_ENCODER_TYPES` は `MP3_LAME,AAC_NERO,OPUS,FLAC,OGG_VORBIS` の部分集合です。指定した部分集合に未知の名前・カンマ区切りの空要素・重複・実行ファイルの不足があれば失敗です。
- 種類を指定しない場合（未指定・空文字・空白のみ）は、見つかったものだけを実行します。一つも見つからなければ失敗です。

生成した短いステレオPCM・WAVを本番の `BassAudioWriter` とエンコード・停止・後片付けへ通し、ファイルの形式識別と採番を確認します。実行ファイル、ライセンス、生成音声をリポジトリの固定入力へ追加しません。

#### 性能検査

性能検査は `dotnet run --project tools/BeMusicSeeker.Verify -- performance --suite NAME` を使います。登録名・固定仕事量・監視上限は[正式入口](../../../tools/BeMusicSeeker.Verify/README.md#明示性能suite)に示します。Release・単一worker・利用者セッションの排他で実行し、全規模・全経路・全反復を固定します。正式入口のfilter、未登録・未指定のsuiteは準備前に拒否し、部分的な調査は直接MTP実行へ分担します。日常の診断出力の契約や小さな機能保証を、実負荷の成功の代用にはしません。

suiteの起動・前提・本体・通常の回収・実際の終了は一つの制限時間を共有し、親の失敗時の回収10秒も一回だけです。監視上限は実行を監視する運用値であり、速度目標ではありません。性能suiteは既存の保証を移したもので、新しい業務上の期待や仕事量を追加しません。スキャン・LR2同期・フォルダ変更・コピー・BMSONの検査は、準備・表明・回収と分けて対象操作を計時し、既存の時計を維持します。800万規模の逆引きや実DB・20 ZIP導入全体の代わりとは扱いません。条件、完了範囲、反復値、結果の同等性は[性能仕様](../core/performance-and-scale.md)に従います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| テスト時間の短縮 | [検証入口](../../../tools/BeMusicSeeker.Verify/README.md)、本書の[テスト時間の短縮](#テスト時間の短縮) | 同条件の全体経過時間、全終端、結果件数を比較し、保証の整理と目標時間の達成を区別して報告する。運用文言を固定する自動テストは設けない。 |
| UTF-8の両パイプ・所有する子プロセス・親環境 | [OwnedProcess](../../../tests/BeMusicSeeker.TestSupport/Processes/OwnedProcess.cs) | Coreの `OwnedProcessTests`・`OwnedProcessPipeTests`・`VerificationRunnerTests` で、全終端、期限・元の失敗・診断の保存を確認する。 |
| 一時資源・共有状態・通常完了の待機 | [TestSettingsFactory](../../../tests/BeMusicSeeker.TestSupport/Components/TestSettingsFactory.cs)、[Coreのmodule寿命](../../../tests/ModuleInitialization/ModuleLifetime.cs)、[UIのmodule寿命](../../../tests/ModuleInitialization/UiModuleLifetime.cs)、各機能のテスト準備 | 所有・復元、通知前の失敗、待合せの解放、実際の完了条件を確認する。UIのホスト終了の成功後だけ設定を回収し、設定の回収失敗後も一時セッション・排他を回収する順序は、制御フローで静的に確認する。失敗注入による実施とは扱わない。 |
| ロック不成立・デッドロックの停止検出と到達待ち | [`ConcurrencyProbe`](../../../tests/BeMusicSeeker.TestSupport/Components/ConcurrencyProbe.cs) | 専用スレッドのprobe、保持中の完了判定、probeと保持側の失敗の伝播、通知なしの終端、期限なしの到達待ちを、制御フローで静的に確認する。失敗注入による実施とは扱わない。 |
| ループバックHTTPの共有操作期限・独立した後片付け、通知前のクライアント・サーバーの終端、実際の取消と相手側の切断 | [`SingleRequestHttpServer`](../../../tests/BeMusicSeeker.TestSupport/Components/SingleRequestHttpServer.cs)、HTTPを使う各機能のfixture | [`AppHttpClientTests`](../../../tests/BeMusicSeeker.Core.Tests/Runtime/AppHttpClientTests.cs) は、有限の二応答で要求されなかった受付の回収、取消済みの後片付けの取消で未終端のクライアントを成功扱いしないこと、補助の生成側の正常終了後のクライアントの先行故障、通知の有無と同時終端・元の故障、準備待機の取消後の遅い結果・故障、GET・フォーム送信の元の要求の取消とローカル解放前の実際の相手側の切断を確認する。PlaylistのURL取得・完了は、未終端の処理が依存するDB・設定を `TestCleanup` でも保持して非成功にすることを待合せで確認する。Playlist・MainWindowは同じ期限・回収の入口で既存の取得・保存・表示の保証を保持する。 |
| 外部エンコーダー | [`BassAudioWriter`](../../../BeMusicSeeker/Ribbit/Media/BassAudioWriter.cs) | [`ExternalAudioEncoderSmokeTests`](../../../tests/BeMusicSeeker.Compatibility.Tests/Playback/ExternalAudioEncoderSmokeTests.cs) |

## 関連資料

[テスト設計](test-authoring.md)、[テストの実行と受入検証](testing.md)、[画面テストの分離](ui-test-isolation.md)、[検証入口の説明](../../../tools/BeMusicSeeker.Verify/README.md)、[互換検査の案内](../../../tests/BeMusicSeeker.Compatibility.Tests/README.md)、[テスト入力の案内](../../../BeMusicSeeker.Tests/TestData/README.md)。
