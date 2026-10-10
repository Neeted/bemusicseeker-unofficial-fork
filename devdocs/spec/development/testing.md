# テストの実行と受入検証

## 目的と適用範囲

通常の機能検証、性能測定、大容量データ、外部プロセス、配布・更新の受入検証について、実行範囲・環境・資源・待機・結果判定を定めます。保証対象とテストの追加・変更・削除は[テスト設計](test-authoring.md)、担当の実行責任と引継ぎは[エージェント運用](agent-workflow.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

**テストプロセス**は実際の `dotnet test` と、そのテストホストを指します。**完全修飾名（FQN）**は名前空間・クラス・メソッドまでを含むテスト識別子です。**判定記録**は実行結果を機械的に確認するTRXやJSONです。

**実機確認**は、実環境で人またはエージェントが操作・観察する確認です。画面、実OS入力・フォーカス・IME、DPI・実モニター、音声機器、担当の起動・設定適用・入力継承などを含みます。実アプリや外部プロセスを起動して結果を自動判定する既存の検証は、自動検証として扱います。

## 仕様

### 検証方針

開発環境の構築、実装、統合、レビュー、コミット、リリースを通じて、実機確認を必須工程にしません。エージェントは原則として自動テストと静的確認で検証し、表示・配置などの確認に必要なら画面外のWPF描画スクリーンショットを使います。スクリーンショットを全件の必須条件にせず、この方針のためだけに代替基盤やテストを増設しません。

実機確認が有用な場合は、変更に関係する理由、条件、操作、期待結果をユーザーへ簡潔に推奨します。結果待ちを必須工程にせず、エージェント自身が実施するのはユーザーが明示的に依頼した場合です。実機確認の未実施だけで各工程の完了を妨げません。

自動検証の成功を実機確認済みとは扱わず、未実施・未確認の範囲を報告します。必須の自動検証、失敗の報告と修正、既知不具合への対処は維持します。以下の実行区分・期限・失敗条件と、各機能仕様の自動検証をこの方針で緩和しません。

### 実行範囲の選択

反復中は変更に関係する契約と接続を対象に絞り、最終統合では下表の標準区分を使います。局所テストを追加したことや、広いテストの新設が不要だったことを理由に、既存の必須検証を省略しません。局所的な変更でも、既存の代表的な統合・受入テストを回帰確認として実行できます。

テストの保証範囲と実行区分は別です。統合・E2Eという呼び方だけで `Full` へ移したり、すべての変更に全体フローの新設を要求したりしません。必要な環境・資源・規模から[テスト区分](#テスト区分)を選び、どこまで確認したかを報告します。文書のみの変更は[文書と検証](../../../AGENTS.md#文書と検証)に従います。

### 標準入口

PowerShell 7から、リポジトリのルートで実行します。

標準入口はWindows上で使います。Linuxで実装を変更した場合は、[Linuxの構築手順](../../setup-linux.md)に従い、変更に関連する既存テストのうちLinuxで実行可能なものを実行し、Windows向けクロスビルドの成功も確認します。実行した範囲・結果と、Linuxで実行できないテスト・未実施のWindows検証を明示して報告します。Linuxの結果を `Functional` / `Full` の成功へ置き換えません。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~対象のテスト名'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

| 区分 | 用途と処理 |
| --- | --- |
| フィルター付き `Quick` | 反復中の対象確認。指定対象だけの専用経路を使用する。 |
| フィルターなし `Quick` | 通常の機能テストの共通実行処理を一回使う。 |
| `Functional` | 通常の最終統合。同じ最終版に対して一回を原則とする。 |
| `Full` | 配布・更新・リリースに関わる検証。共通の `Functional` の後に配布物の受入を行う。 |

`Quick` はフィルターの有無によらず、`Functional` / `Full` と同じ整形検査を行います。全コードモードで、ロックファイルに従う復元の後、空白整形と C# スタイルの検査、SDK 標準解析を含む通常のビルド、出力検査、実テストの順に進みます。整形差分が必要なら失敗とし、自動修正はしません。

整形検査は、まずプロジェクトを評価せず、ルートを `dotnet format whitespace --folder --verify-no-changes` で検査します。生成先の `artifacts/verification`、`bin`、`obj`、`.tmp` だけを除き、その他の作業ファイルの失敗を隠しません。続いて `dotnet format style BeMusicSeeker.sln --verify-no-changes --severity error --no-restore --verbosity minimal` で、ソリューションの C# スタイルを検査します。両検査は既存の `format` フェーズの180秒期限を共有し、どちらかの失敗・期限超過で後続のビルド・テストへ進みません。このフェーズは既存の復元・ビルド・テストの時間予算に含めません。文書だけの変更の確認は[ルートの指針](../../../AGENTS.md)に従います。Mermaidを追加・変更する場合は[補助図の更新と確認](../README.md#更新と確認)も行い、描画確認と対象アプリの検証を区別します。

SDK 標準解析は [`global.json`](../../../global.json) に記載した SDK の `version` と `rollForward` の選択に従います。[`Directory.Build.props`](../../../Directory.Build.props) は本体 `BeMusicSeeker`、更新プログラム `BeMusicSeeker.Updater`、テスト `BeMusicSeeker.Tests` にだけ `EnforceCodeStyleInBuild=true` を適用し、nullable 診断を警告ではなくビルドエラーとして扱います。nullable 解析そのものは各プロジェクトとソースの既存設定に従い、この共通設定から新しい解析範囲を有効化しません。補助ツールのプロジェクトにはこのビルド時スタイル検査と nullable 診断のエラー化を追加せず、SDK の既定解析を従来どおり実行します。

C# の正規形は [`.editorconfig`](../../../.editorconfig) を正本とし、エラーに設定した規則を `dotnet format style` で検査します。通常のビルドで実行できるスタイル解析は補助防御として維持します。nullable 診断は null 許容契約の不一致を作業中に残さないためビルドエラーとして扱い、その他の SDK 品質診断は既定の重大度を維持します。全警告を一律にエラーへ変更しません。生成コードは SDK の扱いに従い、テストへリンクした補助ツールのコードはテストプロジェクトのコンパイル対象として検査します。

### 実行期限と失敗分類

通常の機能テストは、ポータブル設定用の `dotnet test` 開始直前から、全テストプロセスの実際の `ExitTime` までを一つの実行範囲とします。準備・復元・ビルド・出力確認・結果回収・環境復元・差分確認は、この時間に含めません。

`FunctionalTimeoutSeconds` による既定・上限は300秒です。一つの期限をポータブル設定、残りの起動、全プロセスの終了まで共有し、プロセスや段階ごとに作り直しません。失敗後の停止と出力読取りには、この期限から追加10秒までの共通期限を使います。

| 実際の終了までの時間・結果 | 判定 |
| --- | --- |
| 成功かつ180秒以下 | 通常成功。 |
| 成功かつ180秒超、300秒以下 | 成功のまま、運用目標の超過と実測時間を警告し、利用者への報告にも含める。 |
| 300秒超 | 時間超過。後片付け中の遅い終了を成功へ戻さない。 |
| 時間内の決定的な失敗 | 初回から原因を調べる。成功するまでの再実行はしない。 |

真の時間超過だけは、所有するプロセスツリーの停止・残留確認と診断保存の後、同じコマンド、フィルター、時間予算、版、条件で一回だけ再実行できます。予算内で成功し、症状の再発や決定的な診断がなければ一過性の負荷として両結果を報告します。それ以外は原因を調べます。局所的な失敗検出タイマーは、この再実行規則の代わりではありません。

失敗結果は原因調査の入力であり、担当の技術的な行き詰まりを示すものではありません。統合検証の失敗は[不備の差し戻し](agent-workflow.md#不備の差し戻し)で修正担当へ戻します。修正後の新しい版の検証と、失敗した同一版の再実行は区別します。障害解決担当の呼出しを正当化するためだけの追加実行はしません。

### テスト整理前後の時間比較

テスト群の整理では、同じWindows環境・同じ測定範囲で、整理前の標準 `Functional` 一回と最終版の一回を基本に比較します。現行版の実行条件が分かる結果があれば基準に使い、平均や分散を得るためだけの複数回実行は要求しません。反復中は関連する条件を `Quick` で確認し、最終版の実行区分と同一版の再実行は[実行範囲](#実行範囲の選択)・[失敗分類](#実行期限と失敗分類)に従います。配布・更新に関わる整理で `Full` を使う場合、その共通 `Functional` 部分を最終値として使い、計測のためだけに通常検証を重ねません。

比較するのはテストプロセスの全体経過時間です。ホスト別の終了時刻、長いケース、同じ時間帯の待機・共有資源から残る負担を調べます。並列ケースの所要時間を単純合計して全体時間や短縮可能時間と見なさず、スクリプト全体の時間とも区別します。条件が違う結果を同条件の改善実績とは扱いません。

軽量化は、[不要・重複する保証の整理](test-authoring.md#既存テストの採否)、保証に関係しない準備の削減、共有資源に基づく直列化の見直し、通常完了の固定待機の解消の順に検討します。Windowを使わなくしたことだけで `DoNotParallelize` を外さず、設定・文化圏・共有通知等の所有を確認します。通常時に消費しない待機上限を短くすることは、短縮の根拠になりません。残ったホスト間の偏りが実測で分かる場合に分割・配分を検討し、先にホスト数やworker数を増やしません。

保証整理の完了と180秒の達成は別々に判定します。必要なケースを対象外区分へ移す、スキップを増やす、巨大ループへまとめて件数だけ減らす、成功まで再実行する、時間予算を緩めることを短縮手段にしません。実行件数・スキップと理由・実測時間・未実施区分を報告し、目標超過時は残る負担と未達を示します。結果は作業用の生成物として使い、時間履歴の恒久台帳や自動配分の保存状態は追加しません。

### 通常テストの分割と並列実行

コンパイル済みテストDLLを型のロードなしで一回走査し、実際の実行に使う6プロセス分の計画を同じメタデータから生成・検証して使用します。ポータブル設定だけを先に完了し、成功後は同じ計画から残る5プロセスを追加の待機段階なしで起動します。`DoNotParallelize` は名前空間を含む属性名で判定し、型属性はその型の全テストメソッドへ、メソッド属性はそのメソッドへ適用します。

| プロセス | 並列数・範囲 | 担当 |
| --- | --- | --- |
| `portable-settings` | 1、`MethodLevel` | `PlayerPanelStateSettingsCompatibilityTests` の全テスト。 |
| `bass-collectible` | 1、`MethodLevel` | `BassCollectibleLoadContextTests`。ポータブル設定完了後に独立して実行する。 |
| `shared-state-a` | 1、`MethodLevel` | `DoNotParallelize` の完全修飾名をOrdinal順に並べ、偶数番目を交互割当した集合。 |
| `shared-state-b` | 1、`MethodLevel` | 同じ集合の奇数番目を交互割当した集合。 |
| `parallel-a` | `ProcessorCount`、`MethodLevel` | 専用ホストと`DoNotParallelize`を除く通常テストを、完全修飾名のOrdinal順で偶数番目に交互割当した集合。 |
| `parallel-b` | `ProcessorCount`、`MethodLevel` | 専用ホストと`DoNotParallelize`を除く通常テストを、完全修飾名のOrdinal順で奇数番目に交互割当した集合。 |

クラス・メソッドの所属は `verify-refactor.ps1` がコンパイル済みDLLのメタデータから作る実行計画を正本とします。全ホストが同じ判定から生成した完全修飾名のinclude条件をrunsettingsへ渡します。通常テストはOrdinal順の偶奇で二つのホストへ交互に割り当て、`parallel-a` と `parallel-b` の和が通常テスト全体になることを重複・漏れなく検査します。専用クラス、共有状態、通常テストの集合を一回ずつ実行し、表示名や`DataRow`表示値による分割は行いません。最大同時実行数は `3 + 2 * ProcessorCount` です。通常テストは追跡対象ファイルを変えず、実行順・並列度によらず結果を維持します。負荷を下げるためだけの並列数制限ではなく、共有資源と競合を修正します。

`LR2SongDBExtended` は全DBで共通のstatic `Monitor`を使います。一つの通常ホストでは互いに無関係な一時DBの処理まで直列化されるため、通常テストを二つのプロセスへ分けます。`parallel-a` / `parallel-b` の交互割当は負荷分散の規則です。共有資源の安全性はこの割当順には依存させず、fixtureの所有、`DoNotParallelize` の共有状態ホスト、専用ホストで保ちます。

テストアセンブリの`AssemblyInitialize`では、既存のIO完了ポート下限を保持したまま、worker下限を `max(既存値, 7 * ProcessorCount + ProcessorCount)` に設定します。7は小規模fixtureで同時に占有される既知のパイプライン主体（テスト本体、reader、parser、post-parse、2つのcollector、writer）の数であり、本番の最大値や厳密な上限ではありません。各通常ホストの最大ProcessorCount並列に継続処理用のProcessorCount分を加え、同期的なテスト待機によるworker補充遅延を避けるテスト環境専用の設定です。設定に失敗した場合は検証を失敗として扱います。この設定はテストアセンブリだけに適用し、本番のスレッドプール設定や上限・監視は変更しません。

`BassCollectibleLoadContextTests` はWPFのホスト・リソースを解決しない専用プロセスで、回収可能なロードコンテキストと、BASSの静的初期化がネイティブDLLをロードしない条件を確認します。通常テストの譜面情報の保存・解析・補完を含むテスト群は専用の接頭辞分割を作らず、`parallel-a` / `parallel-b` のメソッド単位実行を使用します。

### テスト区分

| `TestCategory` | Functional | Full | 用途 |
| --- | --- | --- | --- |
| 省略・機能名、`Compatibility` | 対象 | 対象 | 小さい合成入力・少数の固定入力による機能と軽量互換性。 |
| `ProcessIntegration` | 除外 | 対象 | 別プロセスや更新プログラムとの統合。 |
| `ReleaseAcceptance` | 除外 | 対象 | 配布物、更新パッケージ、実アプリの受入。 |
| `Performance` / `Net10Performance` | 除外 | 除外 | 変更前後の処理時間・仕事量の比較。 |
| `LargeFixture` | 除外 | 除外 | 巨大DB、大量の実データ。 |
| `ParserCompatibilityFull` | 除外 | 除外 | 全実譜面と参照実装との互換性。 |
| `ProductionDiffFull` | 除外 | 除外 | 実データとの差分。 |
| `ParserCompatibilitySlow` | 除外 | 除外 | 巨大・低速の解析入力。 |

[BMS譜面位置の比較ツール](chart-parse-audit.md)は簡易開発用です。変更確認は対象RunnerのReleaseビルドと差分点検に限定し、恒常toolテストは設けません。

カテゴリは実行特性を表します。小さい互換性テストや、少数の実部品を接続する検証は通常検証に含められます。`ProcessIntegration` は別プロセス等を扱う区分であり、主体間の接続を確認するすべてのテストを指しません。

### 資源の分離と共有

通常テストは固有の一時ファイル・DB・設定と、再現可能な入力を使い、追跡対象ファイルや利用者のデータを変更しません。公開サービスの可用性、ローカルサービスの索引反映、同じPCでの利用者の操作を合否条件にしません。EverythingやOS走査を利用する機能は、取得範囲と完全性を明示した変更不能な入力集合で検証し、作成直後のファイルが外部索引に現れることを前提にしません。外部環境との接続そのものを確認する場合は、対応する実行区分と条件を明示します。

DBの準備・観測は検証対象の処理と分けます。入力作成だけの複数行投入・スキーマ準備は一つのトランザクションへまとめ、SELECTだけの観測には既存の読取り専用接続を使います。無関係な一時DBの観測がプロセス共通の書込みロックを取り合わないようにします。スキーマ作成・確定境界自体を保証するテストでは、対象の本番処理とトランザクション境界を維持します。

共有設定、リソース辞書、文化圏、テーマ、ウィンドウ一覧などを変更する場合は、準備から復元までの所有を確認します。`DoNotParallelize` は分離不能な共有資源とその復元方法を説明できる範囲に使い、独立した状態だけを扱うテストは並列実行します。この属性は他プロセスや利用者の操作を排他しません。外部入力の分離とテスト間の共有状態は別々に確認します。

通常fixtureの設定入力は固定snapshot、専用pathのSettings、in-memory storeを使い、暗黙の `Settings.Default` や利用者設定providerへ戻しません。設定保存の保証には専用pathの実providerを使います。日本語リソースの通常基準はAssemblyInitializeで一回だけ準備します。実文化・テーマの切替と共有通知への購読は、準備から処理終端・購読解除・復元まで所有します。標準compositionはWindowがなくても共有通知を購読するため、その寿命も非並列にします。

### 非同期処理の待機

通常完了は、対象のTask、イベント、状態遷移に直接結び付く通知で待ちます。通知より先に処理が失敗した場合も観測し、来ない通知だけを待ち続けません。テストが閉じた待合せは `finally` で解放し、所有する処理の終結と後片付けを待ちます。

成功通知の到達待ちは、`TestUiDispatcherHost.AwaitNotificationAsync` で通知を生成する実Taskの失敗・取消にも接続します。通知なしで実処理が終わった場合は、その場で未到達として失敗します。生成元が失敗を結果通知へ変換する所有者では、その所有者の実終端を使います。別に予約されたUI受信は生成workerの完了で代用せず、受信処理自身を所有します。MainWindowの寿命は `CloseCompletion` で終了要求からDispatcherと終端処理まで待ち、通知前の失敗もClosed・共有Resourceの回収と併せて観測します。

単なる到達・完了確認には局所期限を追加せず、停止は検証全体の期限で検出します。局所的な制限時間は、外部プロセス、画面表示、解放、ロックが成立しないこと、時間超過契約の失敗検出に限定します。固定の `Thread.Sleep` や余裕時間としての `Task.Delay` から成功を推測しません。短い否定観測は、通常完了を待つ方法や時間保証の証明とは区別します。

不安定なテストは共有状態、順序依存、待機、資源競合を調べます。期限延長、再試行増加、実行担当数や分割数の削減だけで失敗を隠しません。同一版の再実行は[実行期限と失敗分類](#実行期限と失敗分類)に従います。

### プロセスの待機と失敗

開始、完了待機、標準出力・標準エラーの読取り、停止、診断保存、残留確認を一つの管理主体が行います。起動したPID、生成時刻、子孫関係を追跡し、名前だけで無関係なプロセスを停止しません。待機と出力読取りは残りの期限内に収めます。

既知のUTF-8出力元である `dotnet` / `pwsh` は、起動前に `StandardOutputEncoding` と `StandardErrorEncoding` の両方へUTF-8のデコーダーを設定します。通常のコマンドと分割テストの起動は、共通の `Set-VerificationRedirectedProcessEncoding` を使います。保存時にUTF-8へ変換するだけでは、読取り時の誤変換を直せません。`dotnet` の `DOTNET_CLI_FORCE_UTF8_ENCODING=1` は子プロセスの環境だけへ設定し、親のコンソール・出力文字コード・カルチャ・環境変数、および他のネイティブコマンドの既定の文字コードを変更しません。

失敗時は、実行中または最後に確認したテスト、経過時間、出力、進捗・TRX・停止診断の場所を残します。元の失敗を後片付けの失敗で置換せず、二次的な診断に保持します。元の失敗がなくても後片付けが失敗したら失敗です。開始前の環境変数と作業ディレクトリも復元します。追跡対象ファイルの指紋による外部変更監視は行いません。

### 画面テストの分離

保証の選び方は[画面に関わる保証の分担](test-authoring.md#画面に関わる保証の分担)に従います。以下は必要と判断した境界での実行・資源管理の規則です。

WPFは `TestUiDispatcherHost` の一つの `Application` と専用STA Dispatcherを共有します。最初の `Dispatcher` / `Invoke` / `ProcessQueuedPresentation` / `RunWindowTest` 利用時に遅延起動し、テストごとに作りません。ウィンドウなしの操作は `Invoke`、実ウィンドウ・Popupは `RunWindowTest` と `TestWindowPresentationScope` を使います。

表示入力の反映には共通の `ProcessQueuedPresentation` を使い、Loaded境界の後で実controlの値・選択・配置を確認します。この補助は機能処理、Background予約、閉鎖や全Dispatcherのidleを保証しません。実処理はconsumerのTaskまたは適用・終端通知を待ち、未完了の否定観測は到達gateを保持して行い、finallyで解放と開始済みTaskの回収を行います。入力TCSの完了だけをconsumer終端としません。成功後に追加作用を持たないイベントハンドラーは、compiled入口の引数・受理状態と委譲Taskの回収、委譲後に追加作用がない静的接続で分担します。

Dispatcher上の通常機能Task待機は、局所期限を加えない `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使います。描画・閉鎖・native解放の通知欠落は同じDispatcher基盤の `AwaitPresentationOnDispatcher` で期限付きに観測します。テストごとの `Application`・STAスレッド・独自のDispatcher待機ループは作りません。直接の `Dispatcher.PushFrame` や `HwndSource` は共通基盤か明示された例外に限ります。

実Window・Popupのscopeは共有Window/HWND一覧を観測するため、準備から処理終端・後片付けまで非並列にします。同一テストの親子Windowには同じscopeを渡し、独自の入れ子scopeを作りません。Windowを作らず共有状態を変えない `Invoke` はこの理由では直列化しません。実Dispatcherの終了を保証する専用STAと、共通基盤の意図的な入れ子scope検査は、それぞれの保証のために維持します。

MainWindowの生成前からshutdown・Closed・設定購読解除・`Resources["vm"]` 復元までは `MainWindowTestLifetime` が所有します。機能入力と実処理の終端観測は機能側へ残します。compiled内容だけを描画する場合も共通scopeの実Windowを使い、Binding・値・選択・配置の表明を維持します。

constructor-only harnessは `PrepareConstructorOnlyShell` で既存のshell activation境界へ完了済みの初期化を渡し、表示だけのfixtureから本番起動を開始しません。実起動・起動取消・終了の接続は `MainWindowViewHostTests` とPlaylistの `ActualMainWindowFixture` が本番のactivationで検証します。

共有hostの要求は実DispatcherOperationと既存のhost終端通知を観測します。hostが既に終了した場合と、要求の受理後に終了した場合は、所有threadをjoinして元host例外を返します。Dispatcher停止の二次失敗でも終端通知・joinを失わず、二次失敗を元例外へ添付します。健全なhost上のAction自身の失敗は保持し、後続要求は受理します。

`ShowAndWaitForContentRendered` は表示前に通知を購読し、期限付きのDispatcher処理で読込み・描画・非ゼロの配置・HWNDを確認します。モーダル、即時終了、描画通知を制御する場合は、表示直前に `PrepareForOwnedPresentation` を呼びます。

表示の観測待機は、実際の描画・配置・ネイティブ条件の成立を完了条件とします。成立後に、無関係な継続処理による一般的な `ApplicationIdle` 待ちを成功条件へ追加しません。描画通知がない場合、即時終了、ネイティブ観測の失敗、期限超過を成功へ置き換えず、表示観測と後片付けの待機を区別します。

共通基盤は非アクティブ表示専用です。表示直前に手動配置、タスクバー非表示、非アクティブ表示を適用し、全モニターの外へ置きます。HWND生成時には既存の拡張スタイルを保って `WS_EX_NOACTIVATE` を設定・再読取りします。前面でないことと矩形も確認し、Popupにも開く境界で同じ規則を適用します。ネイティブAPIの失敗や表示観測の失敗は、テスト本体とは独立して保持します。

通常テストに前面操作の例外は設けません。実OSカーソルの取得・移動や `Mouse.GetPosition` による物理位置、実フォーカスの取得成功、物理修飾キー、入力キャプチャ、Popupが外部入力で閉じないことを合否条件にしません。イベントを明示しても呼出先がOS状態を参照する場合は分離できていないため、本番の処理へ明示入力を渡す境界で検証します。Popupの内容と接続は、開閉イベント・操作入力、閉じたテンプレートのBinding、明示配置などで確認します。

非公開WPFメソッドによるフォーカス偽装や、テスト専用の本番分岐、外部入力を無視する製品仕様の変更は行いません。実フォーカス移動・IME等のOS接続は[実機確認](#ユーザーに推奨する実機確認)と分担します。共有Dispatcherの待機中には別テストが再入し得るため、Dispatcherの利用だけで排他できると考えず、[資源の分離と共有](#資源の分離と共有)に従います。

開始時の共有スレッドのウィンドウ・HWNDを基準に、追跡したPopup、深い所有関係から順にウィンドウ、購読、Dispatcherの残描画処理、残留HWNDを片付けます。開始時に存在して明示追跡していない別ownerのWindowは、後からHWNDを持っても当scopeの残留破棄へ含めません。そのWindowに属する実native ownerも、既訪問handleで停止する所有チェーンから導出して保護します。閉鎖前に `Closed` を購読し、実閉鎖を待ってから購読を解除します。未開・既閉の対象は区別し、閉鎖取消やWindow/HWNDの残留を成功にしません。機能処理の開始済みTaskは呼出側が回収し、一般的な `ApplicationIdle` はその終端や閉鎖の証明に使いません。設定画面には `CloseForOwnerShutdown` を使います。失敗の優先順位はテスト本体、表示観測、後片付けです。

共有ホストは現在の `AppearanceTheme` を退避し、準備完了前に保存せずLightへ設定します。終了時は同じDispatcherで復元して `Application` とDispatcherを停止し、通知とスレッド結合で待ちます。未起動なら何もしません。既存Applicationとの競合、起動失敗、呼出し・終了失敗は表面化させ、`Application.ResourceAssembly` は変更しません。

### Fullの処理順と期限

共通の通常検証を一回完了した後、ツールの基本確認、公開旧版のキャッシュ準備、現在版の配布物作成、既存データ起動、現行更新の基本確認、`ProcessIntegration`、`ReleaseAcceptance` の順に進みます。重い配布処理を通常テストの300秒に混ぜません。

| 段階 | 制限時間 |
| --- | ---: |
| 整形検査 | 180秒 |
| ツールの基本確認 | 60秒 |
| 公開旧版のキャッシュ準備 | 180秒 |
| 現在版の配布物作成 | 180秒 |
| 既存データ起動 | 180秒 |
| 現行更新の受入 | 240秒 |
| `ProcessIntegration`、`ReleaseAcceptance` | 各180秒 |

値と診断先は `verification-runner-contract.ps1` の記述を正本とします。各段階で一度作る実行期限と、その10秒後の後片付け期限を内部の全コマンドへ渡します。小区間で期限を作り直さず、後片付け時間に完了した処理を成功へ戻しません。

現在版のアプリ・更新プログラム・正確な版の `SkipDocHtml` パッケージは、一回だけ作成して共有します。`distribution-manifest.json` がパス、版、コミット、実行・生成物ID、診断用SHA-256を伝えます。利用側は存在・配置を確認して隔離コピーを操作し、追加の封印や前後の全ツリー再照合はしません。隔離作業先はリポジトリ外です。

`accept-net10-update.ps1` はこのマニフェストを必須とし、現在版へ同じ現在版を適用して、受付、管理対象の更新・削除、本物のアプリの再起動、起動完了、終了、データ保持を確認します。単独実行でも作成済みの配布物を使います。

再起動失敗時の復元テストは、本番の準備・適用処理と呼出し単位のプロセス開始代替処理を使い、配布実行ファイルを破壊しません。非公開の処理入口を呼ぶ例外はこの境界だけとし、確認対象は元の例外、到達した起動、メタデータ・ファイル・利用者データの復元です。非公開名そのものを契約にしません。画面を確認しない更新プロセスは `CreateNoWindow=true` を使用します。

`UpdaterPackageSyncTests` は実プロセスの終了とreadyファイルで順序を制御し、[非同期処理の待機](#非同期処理の待機)に従います。通常の終了・受付確認には局所期限を追加せず、標準入口の検証全体の期限を使います。生存アプリの模擬プロセスは準備通知後に使い、検査が終わるまで入力待ちで生存させます。決定前・排他競合中に終了しないことは通常完了と別に観測します。ケース固有のアプリディレクトリと回復登録を使い、所有プロセスと出力の回収後に登録・一時領域を清掃します。

再起動先と生存アプリには、[テスト専用GUI stub](../../../BeMusicSeeker.Tests/Fixtures/UpdaterRestartApplication/Program.cs)を使います。既定Terminalへconsoleの寿命を渡さないよう本番と同じWinExeとし、追加引数なしの本番ShellExecuteを通します。版ごとのmarker内容・生存gateは隣接入力をpackage・backupとともに移動させます。marker生成は終了の代わりにせず、root・両pipeのEOF・Job内の全子終了を待ちます。直接Updaterとそのexecution gateの起動は引き続き画面を作りません。stubは.NET 10 runtimeのある検証環境で動くframework依存の単一exeで、通常のテストbuildから独立発行します。

### 公開旧版からの移行とリリース判定

[固定配布物の指定](../../acceptance/v216-first-hop/artifact.json)が示す公開v2.1.6.0 ZIPだけを使用します。サイズは11,260,709バイト、SHA-256は次の値です。

```text
C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2
```

指定のHTTPSリリースURL、メタデータ、サイズ、ハッシュの不一致は失敗です。キャッシュがない場合だけ同じディレクトリの一時ファイルへ期限付きで取得し、検証後に原子的に公開します。既存の不正キャッシュの自動修復、再取得による隠蔽、別版やソースからの代替ビルドはしません。

旧版からの適用はC#の `ReleaseAcceptance` テストで、固定ZIP内の実際のprotocol-1更新プログラムを直接起動します。現行の実配布ZIPを入力とし、期限内の正常終了と出力回収に加え、全管理ファイルの相対位置・内容、新版から除かれた旧管理ファイルの正規配置からの除去を確認します。更新プログラムが再生成する管理一覧は記載されたパス集合を比較し、改行・列挙順のバイト一致は要求しません。`data/`・`config/` と利用者追加ファイルは、更新プログラムの起動前に採取したパス集合・バイト内容と比較します。退避・作業領域を含めた全ツリー一致は要求しません。旧更新プログラムは入力ZIPを消費するため、Fullの生成物を変更せず、同じ内容の検証用コピーを渡します。

旧アプリの画面起動、起動ログ待ち、更新通知や所有モーダルの有無、旧アプリの通常終了、旧版の終了待ちと管理ファイルロック時の特性は、この受入から除きます。別枠の旧GUI受入も設けません。旧更新プログラムが子プロセスを作る場合は所有・回収しますが、画面内容を合格条件にはしません。現行更新プログラムの失敗・復元・終了待ちの検査は維持し、現行配布物の起動とDB・設定の意味上の移行は既存データ起動および現行更新の受入で検証します。

この旧版専用ケースは.NET Framework 4.7.2から.NET 10への配布構成移行を確認する一時的な互換性検査です。移行版の受入を終え、旧配布構成からの直接更新を必須の検査対象から外す判断をした時点で、ケース、固定旧ZIPの指定と専用キャッシュ処理、専用の必須判定、関連資料をまとめて退役させます。旧版番号ごとの検査を恒久的に増やしません。

既存データ起動と更新の受入は、初期設定・ライブラリ構築済みの利用者を表す設定と既存DBを入力にします。設定生成は共通処理を使い、`AssemblyVersion` を実保存形式の `SerializableVersion` XML、その他の単一値の設定を文字列として保存します。更新後の最初の起動は、初期設定からの新規利用ではなく、移行した設定とDBを読み取れるかの確認です。

既存データ起動では、`startup_ready_operable` と `startup_post_initialization_maintenance_complete` の両方を同じ実行期限内で待ってから終了します。必須ローカル処理と必要LR2同期は画面操作可能に先立って終端し、その後の任意外部同期・保守も完了を確認します。終了後に既存の意味保持と同期結果を検査し、処理が終わったことだけで同期成功とは扱いません。

現行配布物の画面起動受入では、予期しない所有ダイアログが可視・有効なら失敗とし、自動で閉じて成功にしません。初回構築通知も自動操作せず、利用済み入力の不備や起動の問題として扱います。初期設定自体の検証を更新受入へ混ぜません。この判定を、画面を起動しない旧更新プログラムのファイル適用検査には適用しません。

`Assert-VerificationTestOutcomes` は通常テスト全プロセスと後続受入のTRXを合成し、`release-outcomes.json` を生成します。旧更新受入も実際のC#テストの完全修飾名と実行結果を使い、スクリプトが組み立てた専用の成功JSONは使用しません。必須項目は正確に一件の `Passed` を要求します。欠落、重複、その他の状態は失敗です。任意項目の `Skipped` / `Inconclusive` / `NotExecuted` は完全修飾名の明示一覧と空でない理由がある場合だけ許可します。カテゴリ全体や未知の項目を一括で除外しません。

### 解析・大規模データ・外部エンコーダー

通常の解析確認は小さい合成譜面を使います。解析器・復号・譜面情報の保存形式を変える場合は、対象に合う追加検証を明示します。

```powershell
$env:BMS_TEST_CHART_INFO_FULL = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ParserCompatibilityFull'
$env:BMS_TEST_PRODUCTION_DIFF_FULL = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ProductionDiffFull'
$env:BMS_TEST_CHART_INFO_SLOW = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ParserCompatibilitySlow'
```

実譜面・境界例のコピーにはビルド前の `BMS_TEST_CHART_INFO_FULL=1` が必要です。対応フラグがなければ `Inconclusive`、有効なのに入力がなければ失敗とします。約20 MiBを解析する実データ差分も、通常検証へ混ぜません。

性能測定の入口は `benchmark-net10-performance.ps1 -Corpus all -Configuration Release -Scale small,medium,large` です。1,000 / 25,000 / 200,000行の合成入力を、800万の逆引きキーや実DB・20 ZIPの導入全体の代わりとは扱いません。条件、完了範囲、反復値、結果同等性は[性能仕様](../core/performance-and-scale.md)に従います。

外部エンコーダーは `ExternalAudioEncoderSmokeTests` で明示実行します。通常は `Inconclusive`、`BMS_TEST_AUDIO_ENCODERS=1` で有効です。`BMS_TEST_AUDIO_ENCODER_DIR`、アプリ基準、`libs\x64`、`x64` の順に検索します。`BMS_TEST_AUDIO_ENCODER_TYPES` は `MP3_LAME,AAC_NERO,OPUS,FLAC,OGG_VORBIS` の部分集合です。指定した実行ファイルがない場合、または指定なしで一つも見つからない場合は失敗です。

生成した短いステレオPCM/WAVを本番の `BassAudioWriter` とエンコード・停止・後片付けへ通し、ファイルの形式識別と採番を確認します。実行ファイル、ライセンス、生成音声をリポジトリの固定入力へ追加しません。

### 画面確認

ユーザーの明示依頼によりエージェントが実機で画面確認を行う場合は、[検証方針](#検証方針)に加えて、以下の起動・操作・終了手順に従います。

実アプリを確認するときは、作成したリポジトリ内の `BeMusicSeeker.exe` の正確なパスを指定します。作業ディレクトリと利用データも確認し、インストール版を名前検索で起動しません。起動したPIDと生成時刻を控え、確認後は通常終了を要求して終了を待ち、操作セッションも終了します。無関係な同名プロセスを停止しません。

利用者の操作で画面操作が一時中断された場合は、現在の画面と対象プロセスを確認し、最後の安全な地点から操作を再取得して再開します。一度の中断だけで作業全体を終了しません。利用者が要件を変更した場合は新しい指示を優先します。中止指示や作業の異常終了で確認を終える場合は、一時的な操作権の喪失とは区別し、所有するプロセスと操作セッションを終了します。

画面の表示、入力、完了状態は区別して確認します。単にウィンドウが現れたことやログが静かになったことを、対象操作の成功条件にしません。

### ユーザーに推奨する実機確認

入力・フォーカス経路、表示、機器対応、担当設定などを変更し、実環境との接続を確かめることが有用な場合に、次の項目から関係する確認を推奨します。必須工程やレビューの合格条件にはしません。通常の `Quick`・`Functional`・`Full` の成功だけで、この範囲を確認済みとはしません。専用CI、操作禁止のPC、入力遮断を通常テストの前提にせず、エージェントへの明示依頼がある場合の起動と後片付けは[画面確認](#画面確認)に従います。

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
| 通常検証の分割、選択、共有期限 | [標準スクリプト](../../../scripts/verify-refactor.ps1) | 実際の実行計画、検出されたテスト集合、終了時刻、TRXを照合する。 |
| テスト整理前後の比較 | [標準スクリプト](../../../scripts/verify-refactor.ps1)、本書 | 同条件の全体経過時間、ホスト終了時刻、実行件数・スキップを照合し、保証整理と時間目標を分けて報告する。 |
| 全コードモードの空白整形・C# 正規形、差分失敗、共有期限 | [正規形設定](../../../.editorconfig)、[標準スクリプト](../../../scripts/verify-refactor.ps1) の `Invoke-RepositoryFormatVerification` | 両 formatter の変更なし検査と、違反時にビルド前で失敗する実行プローブ。 |
| 各段階の期限・診断・停止 | [検証スクリプト群](../../../scripts) | 実行に使う期限とプロセス所有情報、失敗時の診断・残留を確認する。 |
| 失敗時の差し戻し・再実行 | [エージェント運用](agent-workflow.md#不備の差し戻し)、本書 | 修正担当への差し戻し、同一版の再実行と修正後の検証、障害解決の条件を区別する。 |
| UTF-8出力の読取りと保存、親環境の非変更 | [共通のプロセス処理](../../../scripts/verification-process-lifecycle.ps1) の `Set-VerificationRedirectedProcessEncoding` / `Start-VerificationRedirectedProcess`、[分割テストの起動](../../../scripts/verify-refactor.ps1) の `Start-FunctionalShardProcess` | [`VerificationProcessLifecycleTests`](../../../BeMusicSeeker.Tests/Verification/VerificationProcessLifecycleTests.cs) の `RedirectedUtf8OutputPreservesBothPipesAndArtifactsWithoutChangingParent`（`ProcessIntegration`）は、両ストリームと保存物の日本語・記号・絵文字、親設定の不変、残留プロセスなしを確認する。通常コマンドと分割テストが同じ設定処理を起動前に呼ぶことは、両入口を点検する。 |
| 一時資源・共有状態・通常完了の待機 | [共通補助処理](../../../BeMusicSeeker.Tests/Helpers)、各機能のテスト準備 | 所有・復元、通知前の失敗、待合せの解放、実際の完了条件を確認する。 |
| 画面の準備、表示、破棄 | [`TestUiDispatcherHost`](../../../BeMusicSeeker.Tests/Helpers/TestUiScheduler.cs)、[`TestWindowPresentationScope`](../../../BeMusicSeeker.Tests/Helpers/TestUiScheduler.cs) | [`SettingsControlPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsControlPresentationTests.cs)、[`WpfTestApplicationHostTests`](../../../BeMusicSeeker.Tests/Helpers/WpfTestApplicationHostTests.cs)。一般idleへの到達とは別に実描画・配置・ネイティブ条件を確認し、所有資源と失敗優先順位を維持する。 |
| 公開旧版のファイル適用、現行版の更新 | C#の旧更新受入、[現行更新の受入](../../../scripts/accept-net10-update.ps1) | [`LegacyUpdaterDistributionMigrationTests`](../../../BeMusicSeeker.Tests/Update/LegacyUpdaterDistributionMigrationTests.cs) は固定旧updaterによる実配布ZIPの適用・旧ファイル除去・利用者ファイル保持、[`UpdaterPackageSyncTests`](../../../BeMusicSeeker.Tests/Update/UpdaterPackageSyncTests.cs) は現行updaterのプロトコル・失敗・復元を確認する。 |
| 利用済み設定の生成と非初回判定 | [受入設定の生成](../../../scripts/acceptance-settings-fixture.ps1)、[既存データ起動](../../../scripts/accept-net10-existing-data.ps1) | [`ApplicationSettingsLifecycleTests`](../../../BeMusicSeeker.Tests/Settings/ApplicationSettingsLifecycleTests.cs) の `LegacySettingsFixtureGeneratorPreservesTypedVersionAndScalarValues` は生成した設定を実設定ストアで読み、版・設定値・非初回判定を確認する。現行配布物での既存設定の読取りはFullの既存データ起動で確認する。 |
| 予期しない所有モーダルの拒否 | [共通の画面観測](../../../scripts/verification-ui-automation.ps1) | [`ExistingDataAcceptanceDialogContractTests`](../../../BeMusicSeeker.Tests/Verification/ExistingDataAcceptanceDialogContractTests.cs) はPID・所有先・可視・有効・モーダル条件とプロセス結果ゲートを確認する。自動応答は行わない。 |
| 外部エンコーダー | [`BassAudioWriter`](../../../BeMusicSeeker/Ribbit/Media/BassAudioWriter.cs) | [`ExternalAudioEncoderSmokeTests`](../../../BeMusicSeeker.Tests/Playback/ExternalAudioEncoderSmokeTests.cs) |

## 関連資料

[テスト設計](test-authoring.md)、[エージェント運用](agent-workflow.md)、[リリース](release.md)、[テスト入力の案内](../../../BeMusicSeeker.Tests/TestData/README.md)。
