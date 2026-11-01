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

### 実行範囲の選択

復旧の実製品process代表は[`RecoveryScenarioTests`](../../../tests/BeMusicSeeker.Scenarios.Tests/Recovery/RecoveryScenarioTests.cs)のSC13一件とSC14二件をFullで実行します。Dailyには追加しません。専有`TestWorkspace`、`OwnedProcess`、UI moduleのsession排他を共有し、各ケースの同じ期限で確認到達・操作Task・応答・実退出を待ちます。自然退出は入力前に期待登録し、未知切断や未応答を成功として許可しません。SQL復元だけは実handlerが開始する通常終了と応答が重なるため、その実Task終端の応答を制御窓口の破棄より先に回収します。

SQLの固定入力では、実modalの帰還と実handlerの終端を分けます。dialog decoratorはmodalが閉じた時点で既存の同期呼出元へ帰還し、SQL commandだけが同じ実handlerの終端・入力消費・元例外を待ちます。固定入力はhandlerのfinallyまで保持します。modal帰還を復元成功とは数えず、handler終端後のcommand応答、制御窓口の回収、実退出の順を維持します。非SQLの入力と通常Viewsの待機契約は変更しません。

検証区分は作業段階で選びます。反復中は変更に関係する契約と接続に絞った `Quick`、通常開発の最終検証は `Functional` を使います。必要な検証が成功すれば、通常開発のコミット・`dev` への統合を行えます。文書のみの変更は[文書と検証](../../../AGENTS.md#文書と検証)に従います。

`Full` は正式リリース前の公開候補に対して必須とします。コード・依存関係・配布設定を確定して実行し、成功後にこれらを変更した場合は、新しい候補で再実行します。文書だけを修正した場合は、その影響に応じた文書・生成物の検証を行います。公開までの順序は[リリース手順](release.md#正式リリースの準備)に従います。

配布スクリプトや更新処理を開発中に変更する場合も、通常の最終検証は `Functional` とし、変更した契約を確認できる関連テスト・受入検証を対象に絞って追加します。必要と判断した場合は開発中にも `Full` を実行できますが、変更ファイルの種類や将来リリースされることだけを理由に、コミット前の `Full` 全体を必須にはしません。次版のリリースノート作成や、配布物に含まれる実行時設定の変更だけでも同様です。レビューでは作業段階と変更の影響を確認し、変更固有の検証が不足する場合は、その対象と確認すべき契約を具体的に示します。

局所テストを追加したことや、広いテストの新設が不要だったことを理由に、選択した段階で必要な既存検証を省略しません。テストの保証範囲と実行区分は別です。統合・E2Eという呼び方だけで `Full` へ移したり、すべての変更に全体フローの新設を要求したりしません。必要な環境・資源・規模から[テスト区分](#テスト区分)を選び、どこまで確認したかを報告します。

### 複数ワークツリーの検証

`Functional` は全ワークツリーを通じて同時に一件だけ実行します。`Full` に含まれる `Functional` も同じ扱いとし、全体ルートが担当間の連絡で開始と終了を調整します。別ワークツリーの `Quick` と、その実行に必要なビルドは並行できます。`Quick` は単位責任者が開始・結果・所有プロセスの回収まで管理し、実行のたびに全体ルートの開始許可を求めません。

同一ワークツリーでは対象と必要な共有依存の版を固定し、編集・自動整形・ビルド・レビューを競合させません。別ワークツリーでも共有出力や設定・DB等の資源が衝突しないことを確認します。実測負荷や資源問題が出た場合は、その根拠に応じて全体ルートが開始順や並行範囲を調整します。性能測定は他の重い検証と分離し、全体ルートが開始を調整します。恒久台帳や自動予約機構は追加しません。

開始済みの処理を別の検証の都合で中断せず、終了と結果回収を待ちます。競合しない別ワークツリーの調査・編集は進められます。ワークツリーの追加・完了・再利用の判断、所有と独立性、移管時の確認結果の引継ぎは[並列作業](agent-workflow.md#並列作業)に従います。

最終検証済みの版をそのまま `dev` へfast-forwardで統合できる場合、統合直後に同じ `Functional` を重複して実行しません。検証後にコードや関連する基底が変わった場合は、新しい版に必要な検証を行います。ブランチ更新と影響するレビューは[通常の統合手順](release.md#通常の作業ブランチの統合)に従います。

### 標準入口

PowerShell 7から、リポジトリのルートで実行します。

テストは `global.json` で指定する `MSTest.Sdk` と native Microsoft.Testing.Platform（MTP）を使います。SDKがframework・adapter・MTPの版を所有し、`packages.lock.json` とlocked restoreで実際の依存を固定します。TRX・CrashDump・HangDump拡張を有効にし、coverageは明示選択時だけ追加します。自動retryは導入しません。

標準入口はビルド済みMTP実行ファイルを直接起動し、専有結果ディレクトリ、TRX出力、`--minimum-expected-tests 1`、必要なfilterとrunsettingsを渡します。実行ファイル相対の言語・native資産を使うため、`dotnet <dll>` で別の実行ファイルの場所を参照する起動にはしません。フィルター付きQuickも整形後にsolutionを一回ビルドし、出力検査後に同じ予算内でMTPを起動します。旧分割計画とPowerShell入口の統合先は[テスト基盤移行の残作業](../../plan/test-platform-migration.md)に示します。

テストプロジェクト単体を直接実行する場合も、MTPの標準filterを使えます。次は小範囲の実行例で、標準入口の整形・出力検査・全Passedの結果ゲートを代替しません。

```powershell
dotnet run --project BeMusicSeeker.Tests -c Release -p:Platform=x64 -- --filter 'FullyQualifiedName~FractionTests' --report-trx --minimum-expected-tests 1
```

標準入口はWindows上で使います。Linuxで実装を変更した場合は、[Linuxの構築手順](../../setup-linux.md)に従い、変更に関連する既存テストのうちLinuxで実行可能なものを実行し、Windows向けクロスビルドの成功も確認します。実行した範囲・結果と、Linuxで実行できないテスト・未実施のWindows検証を明示して報告します。Linuxの結果を `Functional` / `Full` の成功へ置き換えません。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~対象のテスト名'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full -AllowEnvironmentIntegration
```

| 区分 | 用途と処理 |
| --- | --- |
| フィルター付き `Quick` | 反復中の対象確認。指定filter全体を括弧で囲み、`TestCategory!=EnvironmentIntegration` をAND結合する。専用環境を明示許可した場合だけこの除外を外す。 |
| フィルターなし `Quick` | 通常の機能テストの共通実行処理を一回使う。 |
| `Functional` | 通常開発のコミット・`dev` への統合前の最終検証。同じ最終版に対して一回を原則とする。 |
| `Full` | 準備済み専用環境で正式リリース前の公開候補を受け入れる。`-AllowEnvironmentIntegration` が必須で、共通の `Functional` の後に専用環境・配布物の受入を行う。 |

専用環境の対象を絞る場合は、準備完了後に明示filterと許可を組み合わせます。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=EnvironmentIntegration' -AllowEnvironmentIntegration
```

`Functional` とfilterなし `Quick` は許可switch付きの実行を拒否し、`Full` はswitchなしを拒否します。`Functional` / `Full` へのfilter指定も拒否します。拒否は復元・整形・ビルド・テストの開始前です。許可switchだけでは専用環境の準備を代行しません。通常Quickで専用ケースだけを指定して対象が空になった場合も不成功です。

`Quick` はフィルターの有無によらず、`Functional` / `Full` と共通の自動整形処理を使います。全コードモードで、ロックファイルに従う復元、C# スタイルの自動修正、空白・改行の自動整形、Git 差分の空白検査、SDK 標準の品質解析を含む通常のビルド、出力検査、実テストの順に進みます。整形による変更は失敗とせず、整形後のコードを同じ実行内で検証します。

### 自動整形の対象と実行

`Quick` / `Functional` は、未ステージ・ステージ済みの変更ファイルと、Git の除外設定に該当しない未追跡ファイルの和集合から、現在存在する C# ファイルを選びます。対象は作業ツリー上のファイル全体であり、変更行だけには限定しません。`Full` は全体を対象とします。いずれも生成先の `artifacts/verification`、`bin`、`obj`、`.tmp` を除きます。変更対象がゼロなら両 formatter を起動せず、対象指定なしの全体整形に置き換えません。

[共通の整形処理](../../../scripts/format-code.ps1)は、先に `dotnet format style BeMusicSeeker.sln --severity error --no-restore --verbosity minimal` でスタイルを修正し、続いて `dotnet format whitespace --folder` で空白・改行を揃えます。変更範囲では同じファイル一覧を両方の `--include` に渡します。整形後に同じ formatter の変更なし検査を重ねません。C# 以外も含む Git 差分の空白検査は、整形後・ビルド前に行います。

標準検証では対象取得と両 formatter が `format` フェーズの180秒期限を共有します。Git の取得失敗、formatter の非ゼロ終了・期限超過、差分の空白エラーでは、後続のビルド・テストへ進みません。このフェーズは復元・ビルド・テストの時間予算に含めません。formatter の警告は診断へ残します。自動整形の成功は、自動修正できないものも含むすべてのスタイル診断がゼロであることを意味しません。

手動で整形だけを行う場合も、同じ処理を使います。

```powershell
pwsh -NoProfile -File .\scripts\format-code.ps1
pwsh -NoProfile -File .\scripts\format-code.ps1 -Scope All
```

手動入口の既定範囲は `Changed` です。整形対象がある場合はロックファイルに従う復元から実行し、復元済みなら `-NoRestore` で省略できます。手動実行では対象取得・復元・整形を一つの180秒期限で扱います。整形規則、SDK、プロジェクト構成を変更した作業では、`-Scope All` で全体を整形します。通常検証が設定変更から対象範囲を推定して自動で広げることはありません。

C# の正規形は [`.editorconfig`](../../../.editorconfig) を正本とし、エラーに設定した規則を formatter の自動修正対象にします。ビルド時のスタイル解析は全モードで有効化せず、スタイルの処理を formatter に集約します。SDK 標準の品質解析は [`global.json`](../../../global.json) の `version` と `rollForward` に従って選択した SDK の既定を維持します。

[`Directory.Build.props`](../../../Directory.Build.props) は本体 `BeMusicSeeker`、更新プログラム `BeMusicSeeker.Updater`、テスト `BeMusicSeeker.Tests` の nullable 診断をビルドエラーとして扱います。nullable 解析の範囲は各プロジェクトとソースの既存設定に従います。補助ツールには nullable 診断のエラー化を追加せず、全警告も一律にエラーへ変更しません。生成コードは SDK の扱いに従い、テストへリンクした補助ツールのコードは引き続きテストプロジェクトのコンパイル対象とします。

文書だけの変更の確認は[ルートの指針](../../../AGENTS.md)に従います。Mermaidを追加・変更する場合は[補助図の更新と確認](../README.md#更新と確認)も行い、描画確認と対象アプリの検証を区別します。

### 実行期限と失敗分類

通常の機能テストは、ポータブル設定用のMTPプロセス開始直前から、全テストプロセスの実際の `ExitTime` までを一つの実行範囲とします。準備・復元・ビルド・出力確認・結果回収・環境復元・差分確認は、この時間に含めません。

`FunctionalTimeoutSeconds` による既定の実行期限は360秒です。一つの期限をポータブル設定、残りの起動、全プロセスの終了まで共有し、プロセスや段階ごとに作り直しません。失敗後の停止と出力読取りには、この期限から追加10秒までの共通期限を使います。

| 実際の終了までの時間・結果（既定期限の場合） | 判定 |
| --- | --- |
| 成功かつ180秒以下 | 通常成功。 |
| 成功かつ180秒超、360秒以下 | 成功のまま、運用目標の超過と実測時間を警告し、利用者への報告にも含める。 |
| 360秒超 | 時間超過。後片付け中の遅い終了を成功へ戻さない。 |
| 時間内の決定的な失敗 | 初回から原因を調べる。成功するまでの再実行はしない。 |

真の時間超過だけは、所有するプロセスツリーの停止・残留確認と診断保存の後、同じコマンド、フィルター、時間予算、版、条件で一回だけ再実行できます。予算内で成功し、症状の再発や決定的な診断がなければ一過性の負荷として両結果を報告します。それ以外は原因を調べます。局所的な失敗検出タイマーは、この再実行規則の代わりではありません。

失敗結果は原因調査の入力であり、担当の技術的な行き詰まりを示すものではありません。統合検証の失敗は[不備の差し戻し](agent-workflow.md#不備の差し戻し)で修正担当へ戻します。修正後の新しい版の検証と、失敗した同一版の再実行は区別します。障害解決担当の呼出しを正当化するためだけの追加実行はしません。

### テスト整理前後の時間比較

テスト群の整理では、同じWindows環境・同じ測定範囲で、整理前の標準 `Functional` 一回と最終版の一回を基本に比較します。現行版の実行条件が分かる結果があれば基準に使い、平均や分散を得るためだけの複数回実行は要求しません。反復中は関連する条件を `Quick` で確認し、最終版の実行区分と同一版の再実行は[実行範囲](#実行範囲の選択)・[失敗分類](#実行期限と失敗分類)に従います。正式リリース前の確認などで `Full` を使う場合、その共通 `Functional` 部分を最終値として使い、計測のためだけに通常検証を重ねません。

比較するのはテストプロセスの全体経過時間です。ホスト別の終了時刻、長いケース、同じ時間帯の待機・共有資源から残る負担を調べます。並列ケースの所要時間を単純合計して全体時間や短縮可能時間と見なさず、スクリプト全体の時間とも区別します。条件が違う結果を同条件の改善実績とは扱いません。

軽量化は、[不要・重複する保証の整理](test-authoring.md#既存テストの採否)、保証に関係しない準備の削減、共有資源に基づく直列化の見直し、通常完了の固定待機の解消の順に検討します。Windowを使わなくしたことだけで `DoNotParallelize` を外さず、設定・文化圏・共有通知等の所有を確認します。通常時に消費しない待機上限を短くすることは、短縮の根拠になりません。残ったホスト間の偏りが実測で分かる場合に分割・配分を検討し、先にホスト数やworker数を増やしません。

保証整理の完了と180秒の達成は別々に判定します。必要なケースを対象外区分へ移す、スキップを増やす、巨大ループへまとめて件数だけ減らす、成功まで再実行する、時間予算を緩めることを短縮手段にしません。実行件数・スキップと理由・実測時間・未実施区分を報告し、目標超過時は実測時間と未達を示します。結果は作業用の生成物として使い、時間履歴の恒久台帳や自動配分の保存状態は追加しません。

成功時の運用目標超過だけでは追加調査・再実行を始めません。利用者から調査を依頼された場合は、テスト別所要時間だけでなく最長ホストの遅延区間を確認し、ロックの保持側やDBの確定回数など共通処理まで追います。

### 通常テストの分割と並列実行

コンパイル済みテストDLLを型のロードなしで一回走査し、旧暫定Functionalで実際の実行に使う2プロセス分の計画を同じメタデータから生成・検証して使用します。全領域とrunsettingsを準備してから、同じ計画の2プロセスを一つの起動ループで開始します。最初の起動直前に作る360秒の共通期限で全entryを監視し、失敗時は共通の10秒回収期限を維持します。classとmethodの `TestCategory` の和集合を読み、既知の通常除外カテゴリを分割前に除きます。`DoNotParallelize` は名前空間を含む属性名で判定し、型属性はその型の全テストメソッドへ、メソッド属性はそのメソッドへ適用します。

| プロセス | 並列数・範囲 | 担当 |
| --- | --- | --- |
| `parallel-a` | `ProcessorCount`、`MethodLevel` | `DoNotParallelize`を除く通常テストの完全修飾名をUTF-8でSHA-256へ通し、先頭バイトが偶数となる集合。 |
| `parallel-b` | `ProcessorCount`、`MethodLevel` | 同じ通常テストのSHA-256先頭バイトが奇数となる集合。 |

クラス・メソッドの所属は `verify-refactor.ps1` がコンパイル済みDLLのメタデータから作る実行計画を正本とします。全ホストが同じ判定から生成した完全修飾名のinclude条件をrunsettingsへ渡します。通常テストは完全修飾名の固定ハッシュで二つのホストへ割り当て、入力順や無関係な追加・削除で既存メソッドの所属を変えません。ハッシュは所属だけに用い、テストの識別には完全一致の名前を使います。`parallel-a` と `parallel-b` の和が通常テスト全体になることを重複・漏れなく検査します。通常テストの集合を一回ずつ実行し、表示名や`DataRow`表示値による分割は行いません。最大同時実行数は `2 * ProcessorCount` です。通常テストは追跡対象ファイルを変えず、実行順・並列度によらず結果を維持します。負荷を下げるためだけの並列数制限ではなく、共有資源と競合を修正します。

`LR2SongDBExtended` は全DBで共通のstatic `Monitor`を使います。一つの通常ホストでは互いに無関係な一時DBの処理まで直列化されるため、通常テストを二つのプロセスへ分けます。`parallel-a` / `parallel-b` の固定ハッシュ割当は、メソッド追加に伴う大量の所属反転を避けながら負荷を分散する規則です。特定の名前や実行結果に合わせた例外・調整は設けません。共有資源の安全性はこの割当順には依存させず、fixtureの所有で保ちます。旧暫定入口の除外後の通常集合にclass/methodの `DoNotParallelize` が一件でも入った場合は計画を失敗とします。黙って除外したり、別hostへ迂回したりしません。両hostが非空であることを要求し、移管時点の件数や名前を恒久ルールへ固定しません。

テストアセンブリの`AssemblyInitialize`では、既存のIO完了ポート下限を保持したまま、worker下限を `max(既存値, 7 * ProcessorCount + ProcessorCount)` に設定します。7は小規模fixtureで同時に占有される既知のパイプライン主体（テスト本体、reader、parser、post-parse、2つのcollector、writer）の数であり、本番の最大値や厳密な上限ではありません。各通常ホストの最大ProcessorCount並列に継続処理用のProcessorCount分を加え、同期的なテスト待機によるworker補充遅延を避けるテスト環境専用の設定です。設定に失敗した場合は検証を失敗として扱います。この設定はテストアセンブリだけに適用し、本番のスレッドプール設定や上限・監視は変更しません。

Windows moduleの `BassCollectibleLoadContextTests` と `PlayerPanelStateSettingsCompatibilityTests` は、一case一fresh `WindowsProcessProbe` を既存 `OwnedProcess` で起動します。前者はWPFのホスト・リソースを解決せず、子生存中に回収可能なロードコンテキストの解放とnative未読込みを表明します。後者は元の設定名・enum値・実provider往復と正規化保存失敗を表明し、設定領域の回収後だけ成功JSONを出します。親は共通360秒watchdog内のroot・両pipe・Job全体の終端、exit 0、stderr空、一致する唯一の結果を要求し、失敗後も独立10秒の停止・解放・領域回収を試みます。外側moduleの期限を延長せず、主失敗と回収失敗を分離します。通常テストの譜面情報の保存・解析・補完を含むテスト群は専用の接頭辞分割を作らず、`parallel-a` / `parallel-b` のメソッド単位実行を使用します。

### テスト区分

| `TestCategory` | Functional | Full | 用途 |
| --- | --- | --- | --- |
| 省略・機能名、`Compatibility` | 対象 | 対象 | 小さい合成入力・少数の固定入力による機能と軽量互換性。 |
| `ProcessIntegration` | 除外 | 対象 | 別プロセスや更新プログラムとの統合。`EnvironmentIntegration` 併記分は一般phaseから除く。 |
| `EnvironmentIntegration` | 除外 | 専用phase必須 | ACL、実リンク、drive alias・別volume、HKCU回復登録、外部tool等を準備した専用環境で明示実行する。 |
| `ReleaseAcceptance` | 除外 | 対象 | 配布物、更新パッケージ、実アプリの受入。 |
| `Performance` / `Net10Performance` | 除外 | 除外 | 変更前後の処理時間・仕事量の比較。 |
| `LargeFixture` | 除外 | 除外 | 巨大DB、大量の実データ。 |
| `ParserCompatibilityFull` | 除外 | 除外 | 全実譜面と参照実装との互換性。 |
| `ProductionDiffFull` | 除外 | 除外 | 実データとの差分。 |
| `ParserCompatibilitySlow` | 除外 | 除外 | 巨大・低速の解析入力。 |

[BMS譜面位置の比較ツール](chart-parse-audit.md)は簡易開発用です。変更確認は対象RunnerのReleaseビルドと差分点検に限定し、恒常toolテストは設けません。

`EnvironmentIntegration` を他カテゴリと併記しても、Fullでは専用phaseで一回だけ実行します。環境不足を `Ignore` / `Inconclusive` で飛ばさず失敗にし、未実施のFullを成功扱いにしません。

Windows moduleの実プロセス境界は [`ExternalPlayerProcessGatewayTests`](../../../tests/BeMusicSeeker.Windows.Tests/Processes/ExternalPlayerProcessGatewayTests.cs)、[`UpdaterProcessGatewayTests`](../../../tests/BeMusicSeeker.Windows.Tests/Update/UpdaterProcessGatewayTests.cs)、[`UpdaterDeploymentBoundaryTests`](../../../tests/BeMusicSeeker.Windows.Tests/Update/UpdaterDeploymentBoundaryTests.cs) が日常検証で担います。更新マーカー2件は `ProcessIntegration` 属性を維持しつつ、module全件を選択する新入口では日常検証に含めます。外部起動2件と更新Gateway5件は既存Windows probeを起動し、fresh playbackと同じ全終端360秒・独立回収10秒・取消・唯一の成功JSONの判定を使います。公開引数3行は実PowerShell関数を直接 `OwnedProcess` で呼び、起動前からroot・両pipe・Jobの終端まで60秒、失敗回収は一度確定した独立5秒を共有します。元の製品期限・停止表明を回収による停止で代替しません。

カテゴリは実行特性を表します。小さい互換性テストや、少数の実部品を接続する検証は通常検証に含められます。`ProcessIntegration` は別プロセス等を扱う区分であり、主体間の接続を確認するすべてのテストを指しません。

### 資源の分離と共有

通常テストは固有の一時ファイル・DB・設定と、再現可能な入力を使い、追跡対象ファイルや利用者のデータを変更しません。公開サービスの可用性、ローカルサービスの索引反映、同じPCでの利用者の操作を合否条件にしません。EverythingやOS走査を利用する機能は、取得範囲と完全性を明示した変更不能な入力集合で検証し、作成直後のファイルが外部索引に現れることを前提にしません。外部環境との接続そのものを確認する場合は、対応する実行区分と条件を明示します。

環境依存を見つけた場合は、保証と必要資源から次の四つへ分類します。準備場所が一時ディレクトリでも、OS全体や利用者セッションへ影響する操作は通常I/Oと区別します。

| 分類 | 適用する条件 |
| --- | --- |
| Functionalに残す | 所有tempの通常I/O・SQLite・設定、readonly・共有ロックや明示注入した失敗等で保証を再現できる。 |
| 依存をなくす | 保証に不要なdrive割当やTEMPの長さへの依存を外し、同じ本番処理・観測を所有tempで維持できる。 |
| 専用環境で明示実行する | 実ACL、symlink/junction、drive alias、別volume、HKCUのRun/RunOnce、外部native/toolそのものとの接続が保証対象。`EnvironmentIntegration` を付ける。 |
| 削除する | 必要な保証が既存の担当へ移管済み、または契約上不要。移管先の表明まで確認して旧ケースと不要な補助を除く。 |

専用環境は利用者の通常データ・設定から隔離し、ACL変更・リンク作成・drive別名の割当と解除・HKCU回復登録の作成と回収が可能なWindowsを用意します。`DoNotParallelize` は別利用者や別プロセスとの競合を防がないため、準備した環境でだけ明示実行します。処理・出力の終端後に所有資源を回収し、CWDを変更したケースは復元後にdrive aliasを解除します。準備不足・権限不足は失敗です。

クロスボリューム試験は、`BMS_TEST_CROSS_VOLUME_SOURCE_ROOT` と `BMS_TEST_CROSS_VOLUME_DESTINATION_ROOT` に既存の書込み可能なfixture基点を絶対パスで指定します。両者は異なるローカルdrive rootかつ異なる実volumeでなければなりません。[GetVolumePathNameW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamew) で末端のmount pointを取得し、[GetVolumeNameForVolumeMountPointW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumenameforvolumemountpointw) のvolume GUIDで同一実体を拒否します。別名だけを別volumeと見なしません。環境探索や書込みprobeは行わず、指定基点の下へケース固有の子だけを作成・回収し、基点自体は保持します。

外部依存は対象に応じて用意します。Everythingのnative接続は `BMS_EVERYTHING_PATH_IDENTITY_FIXTURE` に固定SDKとfixture bridgeの基点を指定し、7-Zip接続は既存のProgram Files配置に7-Zip CLIを導入します。Java参照toolは `JAVA_HOME` またはPATHでJDKを用意し、[`chartstring-dump/run.ps1`](../../../tools/chartstring-dump/run.ps1) の参照リポジトリと固定譜面を準備します。外部encoderは[外部エンコーダー](#解析大規模データ外部エンコーダー)の環境変数を使います。Fullではこれらの専用保証もすべて実施します。

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

既知のUTF-8出力元である `dotnet` / `pwsh` は、起動前に `StandardOutputEncoding` と `StandardErrorEncoding` の両方へUTF-8のデコーダーを設定します。通常のコマンドと分割テストの起動は、共通の `Set-VerificationRedirectedProcessEncoding` を使います。保存時にUTF-8へ変換するだけでは、読取り時の誤変換を直せません。`dotnet` の `DOTNET_CLI_FORCE_UTF8_ENCODING=1` は子プロセスの環境だけへ設定し、親のコンソール・出力文字コード・カルチャ・環境変数、および他のネイティブコマンドの既定の文字コードを変更しません。

失敗時は、実行中または最後に確認したテスト、経過時間、出力、進捗・TRX・停止診断の場所を残します。元の失敗を後片付けの失敗で置換せず、二次的な診断に保持します。元の失敗がなくても後片付けが失敗したら失敗です。開始前の環境変数と作業ディレクトリも復元します。追跡対象ファイルの指紋による外部変更監視は行いません。

### 画面テストの分離

保証の選び方は[画面に関わる保証の分担](test-authoring.md#画面に関わる保証の分担)に従います。以下は必要と判断した境界での実行・資源管理の規則です。

WPFは `BeMusicSeeker.TestSupport.Wpf` の `TestUiDispatcherHost` にある一つの `Application` と専用STA Dispatcherをプロセス内で共有します。最初の `Dispatcher` / `Invoke` / `ProcessQueuedPresentation` / `RunWindowTest` 利用時に遅延起動し、テストごとに作りません。Supportの参照だけではWPF・設定を初期化せず、画面を使わないmoduleでは起動しません。ウィンドウなしの操作は `Invoke`、実ウィンドウ・Popupは `RunWindowTest` と `TestWindowPresentationScope` を使います。

新しいUI moduleの `UiModuleLifetime` はrunRoot準備、session lease取得、runRoot内のmodule専有profileの `ApplicationPathSnapshot.FromLayout` / `ApplicationPathPolicy.Configure`、日本語リソース基準の順に初期化します。配置確定は `Settings.Default` の初読より前とし、実行体・Assets・langは同じbuild出力を指定します。必要素材の欠落を通常ユーザー領域へのfallbackで補いません。plain hostではAppの文化一覧を複製しません。旧Testsの既存初期化・cleanup順序は維持します。

各ケースがOwnedProcessを回収した後、moduleは開始済みhostのWindow・Dispatcher終端とthread join、共有設定fixtureの専有領域、生成済み管理一時sessionの実Task終端、session lease解放の順に回収します。共有設定はhost終了を確認できた場合だけ回収し、host終了失敗時は保持します。設定回収が失敗しても管理一時sessionとleaseの回収を続け、各失敗を集約します。初期化失敗時も取得済みleaseを解放します。未使用の管理一時sessionをcleanupのために生成しません。同じ親lease内で起動する共通WindowsProcessProbeはleaseを再取得せず、親ケースのOwnedProcessが子と両pipeを終端まで所有します。module全体の子process台帳は持ちません。

表示入力の反映には共通の `ProcessQueuedPresentation` を使い、Loaded境界の後で実controlの値・選択・配置を確認します。この補助は機能処理、Background予約、閉鎖や全Dispatcherのidleを保証しません。実処理はconsumerのTaskまたは適用・終端通知を待ち、未完了の否定観測は到達gateを保持して行い、finallyで解放と開始済みTaskの回収を行います。入力TCSの完了だけをconsumer終端としません。成功後に追加作用を持たないイベントハンドラーは、compiled入口の引数・受理状態と委譲Taskの回収、委譲後に追加作用がない静的接続で分担します。

Dispatcher上の通常機能Task待機は、局所期限を加えない `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使います。描画・閉鎖・native解放の通知欠落は同じDispatcher基盤の `AwaitPresentationOnDispatcher` で期限付きに観測します。テストごとの `Application`・STAスレッド・独自のDispatcher待機ループは作りません。直接の `Dispatcher.PushFrame` や `HwndSource` は共通基盤か明示された例外に限ります。

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

共通の通常検証を一回完了した後、ツールの基本確認、公開旧版のキャッシュ準備、現在版の配布物作成、既存データ起動、現行更新の基本確認、`ProcessIntegration`、`EnvironmentIntegration`、`ReleaseAcceptance` の順に進みます。重い配布処理を通常テストの360秒に混ぜません。

| 段階 | 制限時間 |
| --- | ---: |
| 整形検査 | 180秒 |
| ツールの基本確認 | 60秒 |
| 公開旧版のキャッシュ準備 | 180秒 |
| 現在版の配布物作成 | 180秒 |
| 既存データ起動 | 180秒 |
| 現行更新の受入 | 240秒 |
| `ProcessIntegration`、`EnvironmentIntegration`、`ReleaseAcceptance` | 各180秒 |

値と診断先は `verification-runner-contract.ps1` の記述を正本とします。各段階で一度作る実行期限と、その10秒後の後片付け期限を内部の全コマンドへ渡します。小区間で期限を作り直さず、後片付け時間に完了した処理を成功へ戻しません。

現在版のアプリ・更新プログラム・正確な版の `SkipDocHtml` パッケージは、一回だけ作成して共有します。`distribution-manifest.json` がパス、版、コミット、実行・生成物ID、診断用SHA-256を伝えます。利用側は存在・配置を確認して隔離コピーを操作し、追加の封印や前後の全ツリー再照合はしません。隔離作業先はリポジトリ外です。

`accept-net10-update.ps1` はこのマニフェストを必須とし、現在版へ同じ現在版を適用して、受付、管理対象の更新・削除、本物のアプリの再起動、起動完了、終了、データ保持を確認します。単独実行でも作成済みの配布物を使います。

再起動失敗時の復元テストは、本番の準備・適用処理と呼出し単位のプロセス開始代替処理を使い、配布実行ファイルを破壊しません。非公開の処理入口を呼ぶ例外はこの境界だけとし、確認対象は元の例外、到達した起動、メタデータ・ファイル・利用者データの復元です。非公開名そのものを契約にしません。画面を確認しない更新プロセスは `CreateNoWindow=true` を使用します。

`UpdaterPackageSyncTests` はHKCUの回復登録を使います。新Scenariosでは非Dailyの27メソッド・29行を実配布catalogへ接続し、旧側にはドライブルート・reparse専用の5件を残します。両側の元カテゴリ属性は維持します。実プロセスの終了とreadyファイルで順序を制御し、[非同期処理の待機](#非同期処理の待機)に従います。旧側は標準入口の検証全体の期限を使います。新Scenariosの更新32行（公開受入3件を含む）は、case開始時に作った単調180秒をcatalog読取り・コピー・受付・CLI・全Jobとpipe終端で共有し、起動失敗とfinallyは同じLazy回収10秒を使います。公開payload30秒・layout60秒と、この2件の回収5秒など既存の小さい上限は延長しません。生存アプリの模擬プロセスは準備通知後に使い、検査が終わるまで入力待ちで生存させます。決定前・排他競合中に終了しないことは通常完了と別に観測します。ケース固有のアプリディレクトリと回復登録を使い、所有プロセスと出力の回収後に登録・一時領域を清掃します。

再起動先と生存アプリには、[テスト専用GUI stub](../../../tests/Fixtures/UpdaterRestartApplication/Program.cs)を使います。既定Terminalへconsoleの寿命を渡さないよう本番と同じWinExeとし、追加引数なしの本番ShellExecuteを通します。版ごとのmarker内容・生存gateは隣接入力をpackage・backupとともに移動させます。marker生成は終了の代わりにせず、root・両pipeのEOF・Job内の全子終了を待ちます。直接Updaterとそのexecution gateの起動は引き続き画面を作りません。stubは.NET 10 runtimeのある検証環境で動くframework依存の単一exeで、通常のProjectReferenceでbuildし、同じ構成・global propertyの一つの公開targetから旧テストとWindows probeへ単一exeを配布します。

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

`Assert-VerificationTestOutcomes` は各実行の全結果へ `Passed` を要求します。空結果・欠損/不正TRX・未知method・非Passedは失敗です。TRXのrun-level `Completed` / `Passed` は許可し、`Failed` / `Aborted` 等は拒否します。理由付きskip許可機構はなく、旧optional記述が入力へ残っていても非Passedを許可しません。

通常の共通計画とFull各phaseでは、選択methodごとに結果一件以上を要求し、返った各DataRowを全件確認します。DataRowの事前全展開は行いません。任意filterのQuickは独自filter解釈をせず、DLL上の既知method集合・非空結果・全Passedを検査し、選択methodの欠落照合は共通計画/Fullへ分担します。

Fullは通常2ホスト、一般 `ProcessIntegration`、専用 `EnvironmentIntegration`、`ReleaseAcceptance` の実TRXを合算し、`release-outcomes.json` を生成します。専用phaseを含む必要なTRXの欠落は失敗です。旧更新受入も実C#テストの完全修飾名と結果を使います。固定配布物rosterの必須項目だけは正確に一件の `Passed` を要求し、欠落・重複を拒否します。通常のDataRowをこの一件制限へ含めません。

### 解析・大規模データ・外部エンコーダー

通常の解析確認は小さい合成譜面を使います。解析器・復号・譜面情報の保存形式を変える場合は、対象に合う追加検証を明示します。

固定corpusと実toolはCompatibility moduleの `Suite.*` 属性で選択します。suiteの明示選択をopt-inとし、旧 `BMS_TEST_CHART_INFO_FULL` / `BMS_TEST_PRODUCTION_DIFF_FULL` / `BMS_TEST_CHART_INFO_SLOW` の事前ガードは使いません。通常のCore/Windows検証へ固定corpus全件を混ぜません。

固定入力はリポジトリの `BeMusicSeeker.Tests/TestData` を読み取り専用で参照し、build出力へ複製しません。入力・参照DB・外部tool・Java準備JSONの欠如や不正は失敗です。suite一覧、準備、直接module実行は[互換検査の案内](../../../tests/BeMusicSeeker.Compatibility.Tests/README.md)を参照します。正式runnerのCompatibility入口とsuite全体の期限は接続が完了するまで未検証として扱い、直接moduleの成功で代用しません。

性能検査の入口は `benchmark-net10-performance.ps1 -Corpus all -Configuration Release -Scale small,medium,large` です。`contract` は診断出力、`estimation` は候補リソースの評価、`parser` は同位置だけのノートの全位置で次音なしを確認する機能検査で、走査回数・所要時間は測定しません。規模に対応する入力数は各ケースに置き、800万の逆引きキーや実DB・20 ZIPの導入全体の代わりとは扱いません。条件、完了範囲、反復値、結果同等性は[性能仕様](../core/performance-and-scale.md)に従います。

`contract` はCoreの `TestCategory=contract` を実行し、診断出力の5ケースはCoreの日常検証でも実行します。`estimation` / `parser` は旧 `BeMusicSeeker.Tests` の従来カテゴリを実行します。`all` はCoreのcontract、旧プロジェクトの `TestCategory=Net10Performance` の順に各一回実行し、各実行の失敗で停止します。TRXはプロジェクト別の名前へ分離し、`command.json` に各project・filter・TRX名を入力の記録として残します。

外部エンコーダーは `ExternalAudioEncoderSmokeTests` で明示実行します。`EnvironmentIntegration` として専用環境で実行し、`BMS_TEST_AUDIO_ENCODERS=1` を必須とします。指定がなければ失敗です。`BMS_TEST_AUDIO_ENCODER_DIR`、アプリ基準、`libs\x64`、`x64` の順に検索します。`BMS_TEST_AUDIO_ENCODER_TYPES` は `MP3_LAME,AAC_NERO,OPUS,FLAC,OGG_VORBIS` の部分集合です。指定した実行ファイルがない場合、または指定なしで一つも見つからない場合は失敗です。

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
| 通常開発と正式リリース前の検証区分、変更に応じた追加検証 | 本書の[実行範囲の選択](#実行範囲の選択)、[開発指針](../../../AGENTS.md#文書と検証)、[リリース手順](release.md#配布物の生成と検証)、[エージェント運用](agent-workflow.md#統合と予備レビュー) | 作業段階・公開候補の確定と各文書の案内を照合する。運用文言を固定する自動テストは設けない。 |
| 複数ワークツリーの検証と結果の再利用 | 本書の[複数ワークツリーの検証](#複数ワークツリーの検証)、[並列作業](agent-workflow.md#並列作業)、[通常の統合手順](release.md#通常の作業ブランチの統合) | 全体ルートによる `Functional`・性能測定の開始調整と単位責任者の `Quick` 自主管理、別ワークツリーの必要ビルドとの並行、資源衝突と実測負荷、対象版・共有依存、移管時の結果引継ぎ、検証版と統合版の一致を照合する。運用文言を固定する自動テストは設けない。 |
| 通常検証の分割、選択、共有期限 | [標準スクリプト](../../../scripts/verify-refactor.ps1) | [`VerificationRunnerContractTests`](../../../BeMusicSeeker.Tests/Verification/VerificationRunnerContractTests.cs)（`ProcessIntegration`）は実PE読取りによるclass/methodカテゴリ和集合とDNP適用・別namespaceの区別、実計画生成とrunsettingsによる専用カテゴリ除外後の通常Serial拒否、通常2hostへの一意完全分割を確認する。固定ベクトルによるSHA-256先頭byte偶奇、追加・削除・順序変更・別プロセスに対する所属の不変、両側非空条件も確認する。typed probeの許可表と合成TRXによる全行Passed・欠落/未知/不正/run-level失敗・Full専用phase欠落の拒否も確認する。実起動の接続と期限は、実際の実行計画、検出されたテスト集合、終了時刻、TRXを照合する。 |
| テスト整理前後の比較 | [標準スクリプト](../../../scripts/verify-refactor.ps1)、本書 | 同条件の全体経過時間、ホスト終了時刻、実行件数・スキップを照合し、保証整理と時間目標を分けて報告する。 |
| 変更範囲・全体の自動整形、整形後の検証、失敗時の停止 | [共通の整形処理](../../../scripts/format-code.ps1)、[標準スクリプト](../../../scripts/verify-refactor.ps1) の `Invoke-RepositoryFormattingPhase` | [`RepositoryFormattingTests`](../../../BeMusicSeeker.Tests/Verification/RepositoryFormattingTests.cs)（`ProcessIntegration`）は実 Git による対象選択と、外部コマンドを代替した標準経路の実行順・対象ゼロ・失敗伝播・共有期限を確認する。SDK 自体の修正内容はこのテストの対象外。 |
| 各段階の期限・診断・停止 | [検証スクリプト群](../../../scripts) | 実行に使う期限とプロセス所有情報、失敗時の診断・残留を確認する。 |
| 失敗時の差し戻し・再実行 | [エージェント運用](agent-workflow.md#不備の差し戻し)、本書 | 修正担当への差し戻し、同一版の再実行と修正後の検証、障害解決の条件を区別する。 |
| UTF-8出力の読取りと保存、親環境の非変更 | [共通のプロセス処理](../../../scripts/verification-process-lifecycle.ps1) の `Set-VerificationRedirectedProcessEncoding` / `Start-VerificationRedirectedProcess`、[分割テストの起動](../../../scripts/verify-refactor.ps1) の `Start-FunctionalShardProcess` | [`VerificationProcessLifecycleTests`](../../../BeMusicSeeker.Tests/Verification/VerificationProcessLifecycleTests.cs) の `RedirectedUtf8OutputPreservesBothPipesAndArtifactsWithoutChangingParent`（`ProcessIntegration`）は、両ストリームと保存物の日本語・記号・絵文字、親設定の不変、残留プロセスなしを確認する。通常コマンドと分割テストが同じ設定処理を起動前に呼ぶことは、両入口を点検する。 |
| 一時資源・共有状態・通常完了の待機 | [TestSettingsFactory](../../../tests/BeMusicSeeker.TestSupport/Components/TestSettingsFactory.cs)、[Coreのmodule寿命](../../../tests/ModuleInitialization/ModuleLifetime.cs)、[UIのmodule寿命](../../../tests/ModuleInitialization/UiModuleLifetime.cs)、各機能のテスト準備 | 所有・復元、通知前の失敗、待合せの解放、実際の完了条件を確認する。UIのhost終了成功後だけ設定を回収し、設定回収失敗後も一時session・leaseを回収する順序は制御フローで静的確認する。失敗注入の実施とは扱わない。 |
| Dispatcherの受付・終端・通常Task待機 | [`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs) | [`DispatcherHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/DispatcherHostTests.cs)。既終端・受理後終了と停止二次失敗、Action失敗後の継続、join、consumerの成功・失敗・取消・通知欠落、単一STAを確認する。 |
| 画面の準備、表示、破棄 | [`TestWindowPresentationScope`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestWindowPresentationScope.cs) | [`SettingsControlPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsControlPresentationTests.cs)、[`WindowPresentationScopeTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WindowPresentationScopeTests.cs)。一般idleと別に実描画・配置・Win32結果、入れ子の所有資源、失敗優先順位を確認する。 |
| 共有hostのテーマ・意味別リソース | [`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs) | [`WpfThemeResourceTests`](../../../tests/BeMusicSeeker.Windows.Tests/Themes/WpfThemeResourceTests.cs)。Lightと実コンストラクターの意味別ブラシへの到達を確認する。 |
| UI moduleの配置と終了所有 | [`UiModuleLifetime`](../../../tests/ModuleInitialization/UiModuleLifetime.cs)、[`TempDirectoryPublisher`](../../../BeMusicSeeker/TempDirectoryPublisher.cs) | 初期化・例外時を含む回収順序とlease解放は静的確認。[`WpfHostLifecycleTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WpfHostLifecycleTests.cs) と共通probeの [`WpfHostLifecycleProbe`](../../../tests/Fixtures/WindowsProcessProbe/WpfHostLifecycleProbe.cs) は同じSupport hostを使い、子の生存中にHWND・実thread・temp sessionの消失を確認し、release後に全childと両pipeを回収する。 |
| loopback HTTPの共有操作期限・独立cleanup、通知前のclient/server終端、実取消とpeer切断 | [`SingleRequestHttpServer`](../../../tests/BeMusicSeeker.TestSupport/Components/SingleRequestHttpServer.cs)、HTTPを使う各機能fixture | [`AppHttpClientTests`](../../../tests/BeMusicSeeker.Core.Tests/Runtime/AppHttpClientTests.cs) は有限二応答の未要求accept回収、取消済みcleanup tokenで未終端clientを成功扱いしないこと、補助producer正常終了後のclient先行故障、通知有無と同時終端・元故障、準備待機取消後の遅い結果・故障、GET/formの元要求取消とlocal解放前の実peer切断を確認する。PlaylistのURL acquisition/completionは、未終端処理の依存DB・設定をTestCleanupでも保持して非成功にすることをgateで確認する。Playlist/MainWindowは同じ期限・回収入口で既存の取得・保存・表示保証を保持する。 |
| 公開旧版のファイル適用、現行版の更新 | C#の旧更新受入、[現行更新の受入](../../../scripts/accept-net10-update.ps1) | [`LegacyUpdaterDistributionMigrationTests`](../../../tests/BeMusicSeeker.Scenarios.Tests/Update/LegacyUpdaterDistributionMigrationTests.cs) は固定旧updaterによる実配布ZIPの適用・旧ファイル除去・利用者ファイル保持、[`UpdaterPackageSyncTests`](../../../tests/BeMusicSeeker.Scenarios.Tests/Update/UpdaterPackageSyncTests.cs) は現行updaterのプロトコル・失敗・復元を確認する。 |
| 利用済み設定の生成と非初回判定 | [受入設定の生成](../../../scripts/acceptance-settings-fixture.ps1)、[既存データ起動](../../../scripts/accept-net10-existing-data.ps1) | [`ApplicationSettingsLifecycleTests`](../../../BeMusicSeeker.Tests/Settings/ApplicationSettingsLifecycleTests.cs) の `LegacySettingsFixtureGeneratorPreservesTypedVersionAndScalarValues` は生成した設定を実設定ストアで読み、版・設定値・非初回判定を確認する。現行配布物での既存設定の読取りはFullの既存データ起動で確認する。 |
| 予期しない所有モーダルの拒否 | [共通の画面観測](../../../scripts/verification-ui-automation.ps1) | [`ExistingDataAcceptanceDialogContractTests`](../../../BeMusicSeeker.Tests/Verification/ExistingDataAcceptanceDialogContractTests.cs) はPID・所有先・可視・有効・モーダル条件とプロセス結果ゲートを確認する。自動応答は行わない。 |
| 外部エンコーダー | [`BassAudioWriter`](../../../BeMusicSeeker/Ribbit/Media/BassAudioWriter.cs) | [`ExternalAudioEncoderSmokeTests`](../../../BeMusicSeeker.Tests/Playback/ExternalAudioEncoderSmokeTests.cs) |

## 関連資料

[テスト設計](test-authoring.md)、[エージェント運用](agent-workflow.md)、[リリース](release.md)、[テスト入力の案内](../../../BeMusicSeeker.Tests/TestData/README.md)。
