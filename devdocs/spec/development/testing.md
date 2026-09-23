# テストの実行と受入検証

## 目的と適用範囲

通常の機能検証、性能測定、大容量データ、外部プロセス、配布・更新の受入検証を分け、実行条件と結果の判定を定めます。テストを作る判断はテスト設計仕様に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

**テストプロセス**は実際の `dotnet test` と、そのテストホストを指します。**完全修飾名（FQN）**は名前空間・クラス・メソッドまでを含むテスト識別子です。**判定記録**は実行結果を機械的に確認するTRXやJSONです。

## 仕様

### 標準入口

PowerShell 7から、リポジトリのルートで実行します。

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

`Quick` はフィルターの有無によらず独立した整形検査を省き、通常のビルドで SDK 標準解析を実行します。`Functional` / `Full` はロックファイルに従う復元の後、空白整形の検査、SDK 標準解析を含む通常のビルド、出力検査、実テストの順に進みます。自動修正はしません。

整形検査はプロジェクトを評価せず、ルートを `dotnet format whitespace --folder` で検査します。生成先の `artifacts/verification`、`bin`、`obj`、`.tmp` だけを除き、その他の作業ファイルの失敗を隠しません。文書だけの変更の確認は[ルートの指針](../../../AGENTS.md)に従います。Mermaidを追加・変更する場合は[補助図の更新と確認](../README.md#更新と確認)も行い、描画確認と対象アプリの検証を区別します。

SDK 標準解析は [`global.json`](../../../global.json) に記載した SDK の `version` と `rollForward` の選択に従います。[`Directory.Build.props`](../../../Directory.Build.props) は本体 `BeMusicSeeker`、更新プログラム `BeMusicSeeker.Updater`、テスト `BeMusicSeeker.Tests` にだけ `EnforceCodeStyleInBuild=true` を適用し、nullable 診断を警告ではなくビルドエラーとして扱います。nullable 解析そのものは各プロジェクトとソースの既存設定に従い、この共通設定から新しい解析範囲を有効化しません。補助ツールのプロジェクトにはこのビルド時スタイル検査と nullable 診断のエラー化を追加せず、SDK の既定解析を従来どおり実行します。

書き方は [`.editorconfig`](../../../.editorconfig) を正本とし、ファイルスコープ名前空間の `IDE0161`、型が明白な場合に `var` を使う `IDE0007`、組込み型と型が明白でない場合に明示型を使う `IDE0008` をビルドエラーとして扱います。不要な `this` 修飾を除く `IDE0003` は SDK のビルドでは実行されないため、IDE の提案に留めます。nullable 診断は null 許容契約の不一致を作業中に残さないためビルドエラーとして扱い、その他の SDK 品質診断は既定の重大度を維持します。全警告を一律にエラーへ変更しません。生成コードは SDK の扱いに従い、テストへリンクした補助ツールのコードはテストプロジェクトのコンパイル対象として検査します。

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

カテゴリは保証内容ではなく実行特性です。小さい互換性テストは通常検証に含めます。通常テストでは固有の一時資源と決定的な代替入力を使い、外部Everything索引への即時反映や固定の待ち時間に依存しません。

### プロセスの待機と失敗

開始、完了待機、標準出力・標準エラーの読取り、停止、診断保存、残留確認を一つの管理主体が行います。起動したPID、生成時刻、子孫関係を追跡し、名前だけで無関係なプロセスを停止しません。待機と出力読取りは残りの期限内に収めます。

既知のUTF-8出力元である `dotnet` / `pwsh` は、起動前に `StandardOutputEncoding` と `StandardErrorEncoding` の両方へUTF-8のデコーダーを設定します。通常のコマンドと分割テストの起動は、共通の `Set-VerificationRedirectedProcessEncoding` を使います。保存時にUTF-8へ変換するだけでは、読取り時の誤変換を直せません。`dotnet` の `DOTNET_CLI_FORCE_UTF8_ENCODING=1` は子プロセスの環境だけへ設定し、親のコンソール・出力文字コード・カルチャ・環境変数、および他のネイティブコマンドの既定の文字コードを変更しません。

失敗時は、実行中または最後に確認したテスト、経過時間、出力、進捗・TRX・停止診断の場所を残します。元の失敗を後片付けの失敗で置換せず、二次的な診断に保持します。元の失敗がなくても後片付けが失敗したら失敗です。開始前の環境変数と作業ディレクトリも復元します。追跡対象ファイルの指紋による外部変更監視は行いません。

### 画面テストの分離

WPFは `TestUiDispatcherHost` の一つの `Application` と専用STA Dispatcherを共有します。最初の `Dispatcher` / `Invoke` / `Drain` / `RunWindowTest` 利用時に遅延起動し、テストごとに作りません。ウィンドウなしの操作は `Invoke`、実ウィンドウ・Popupは `RunWindowTest` と `TestWindowPresentationScope` を使います。

`ShowAndWaitForContentRendered` は表示前に通知を購読し、期限付きのDispatcher処理で読込み・描画・非ゼロの配置・HWNDを確認します。モーダル、即時終了、描画通知を制御する場合は、表示直前に `PrepareForOwnedPresentation` を呼びます。

共通基盤は非アクティブ表示専用です。表示直前に手動配置、タスクバー非表示、非アクティブ表示を適用し、全モニターの外へ置きます。HWND生成時には既存の拡張スタイルを保って `WS_EX_NOACTIVATE` を設定・再読取りします。前面でないことと矩形も確認し、Popupにも開く境界で同じ規則を適用します。ネイティブAPIの失敗や表示観測の失敗は、テスト本体とは独立して保持します。

通常テストに前面操作の例外は設けません。実OSカーソルの取得・移動や `Mouse.GetPosition` による物理位置、実フォーカスの取得成功、物理修飾キー、入力キャプチャ、Popupが外部入力で閉じないことを合否条件にしません。イベントを明示しても呼出先がOS状態を参照する場合は分離できていないため、本番の処理へ明示入力を渡す境界で検証します。Popupの内容と接続は、開閉イベント・操作入力、閉じたテンプレートのBinding、明示配置などで確認します。

共有Dispatcherの利用だけではテスト全体の排他を保証しません。待機中の再入と、共有リソース・設定・テーマ等の変更を確認し、所有を分離できない範囲に `DoNotParallelize` を残します。独立した入力と状態だけを扱うテストは並列実行できます。

開始時の共有スレッドのウィンドウ・HWNDを基準に、追跡したPopup、深い所有関係から順にウィンドウ、購読、Dispatcherの残処理、残留HWNDを片付けます。設定画面には `CloseForOwnerShutdown` を使います。失敗の優先順位はテスト本体、表示観測、後片付けです。

共有ホストは現在の `AppearanceTheme` を退避し、準備完了前に保存せずLightへ設定します。終了時は同じDispatcherで復元して `Application` とDispatcherを停止し、通知とスレッド結合で待ちます。未起動なら何もしません。既存Applicationとの競合、起動失敗、呼出し・終了失敗は表面化させ、`Application.ResourceAssembly` は変更しません。

### Fullの処理順と期限

共通の通常検証を一回完了した後、ツールの基本確認、公開旧版のキャッシュ準備、現在版の配布物作成、既存データ起動、現行更新の基本確認、`ProcessIntegration`、`ReleaseAcceptance` の順に進みます。重い配布処理を通常テストの300秒に混ぜません。

| 段階 | 制限時間 |
| --- | ---: |
| 整形検査 | 120秒 |
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

`UpdaterPackageSyncTests` の通常の終了・受付確認は、実プロセスの終了とreadyファイルで順序を保証し、局所的な5秒・30秒の合否条件を設けません。停止は標準入口の検証全体の期限で検出します。生存アプリの模擬プロセスは準備通知を受けてから使い、検査が終わるまで入力待ちで生存させます。決定前・排他競合中に終了しないことの短い否定観測は、通常完了の待機とは区別します。ケース固有のアプリディレクトリと回復登録を使って並列実行を維持し、所有プロセスと出力の回収後に登録・一時領域を清掃します。

### 公開旧版からの移行とリリース判定

[固定配布物の指定](../../acceptance/v216-first-hop/artifact.json)が示す公開v2.1.6.0 ZIPだけを使用します。サイズは11,260,709バイト、SHA-256は次の値です。

```text
C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2
```

指定のHTTPSリリースURL、メタデータ、サイズ、ハッシュの不一致は失敗です。キャッシュがない場合だけ同じディレクトリの一時ファイルへ期限付きで取得し、検証後に原子的に公開します。既存の不正キャッシュの自動修復、再取得による隠蔽、別版やソースからの代替ビルドはしません。

`accept-v216-first-hop.ps1` は旧版の実際のprotocol-1更新プログラムを使います。期限内の終了、出力の全読取り、旧アプリの終了、新版の `startup_ready_operable` を確認し、現行のready/decision通信は旧版に要求しません。適用直後は `data/`・`config/` のバイト一致、初回起動後は設定とDBの意味上の保持を確認します。管理ファイルをロックする確認では、非ゼロ終了、非空の標準エラー、データ保持を要求し、旧管理ツリーの自動復元は要求しません。

既存データ起動と更新の受入は、初期設定・ライブラリ構築済みの利用者を表す設定と既存DBを入力にします。設定生成は共通処理を使い、`AssemblyVersion` を実保存形式の `SerializableVersion` XML、その他の単一値の設定を文字列として保存します。更新後の最初の起動は、初期設定からの新規利用ではなく、移行した設定とDBを読み取れるかの確認です。

既存データ起動では、`startup_ready_operable` と `startup_post_initialization_maintenance_complete` の両方を同じ実行期限内で待ってから終了します。画面操作可能の記録だけで終了すると、起動後のLR2自動同期を中断するためです。終了後に既存の意味保持と同期結果を検査し、処理が終わったことだけで同期成功とは扱いません。

公開旧版では、既存ログの起動完了を待った後、生存している旧アプリのPIDを渡して旧更新プログラムを起動し、旧アプリへ通常終了を要求します。ウィンドウが現れただけで起動完了とは扱いません。旧版に現行版専用の起動ログや更新通信を要求しません。

画面起動の受入では、予期しない所有ダイアログが可視・有効なら失敗とし、自動で閉じて成功にしません。初回構築通知も自動操作せず、利用済み入力の不備や起動の問題として扱います。初期設定自体の検証を更新受入へ混ぜません。

`Assert-VerificationTestOutcomes` は通常テスト全プロセスと後続受入のTRXまたは厳密な完全修飾名付きJSONを合成し、`release-outcomes.json` を生成します。必須項目は正確に一件の `Passed` を要求します。欠落、重複、その他の状態は失敗です。任意項目の `Skipped` / `Inconclusive` / `NotExecuted` は完全修飾名の明示一覧と空でない理由がある場合だけ許可します。カテゴリ全体や未知の項目を一括で除外しません。

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

実アプリを確認するときは、作成したリポジトリ内の `BeMusicSeeker.exe` の正確なパスを指定します。作業ディレクトリと利用データも確認し、インストール版を名前検索で起動しません。起動したPIDと生成時刻を控え、確認後は通常終了を要求して終了を待ち、操作セッションも終了します。無関係な同名プロセスを停止しません。

利用者の操作で画面操作が一時中断された場合は、現在の画面と対象プロセスを確認し、最後の安全な地点から操作を再取得して再開します。一度の中断だけで作業全体を終了しません。利用者が要件を変更した場合は新しい指示を優先します。中止指示や作業の異常終了で確認を終える場合は、一時的な操作権の喪失とは区別し、所有するプロセスと操作セッションを終了します。

画面の表示、入力、完了状態は区別して確認します。単にウィンドウが現れたことやログが静かになったことを、対象操作の成功条件にしません。

### 入力操作の明示受入

OS入力との接続自体は、該当機能の入力・フォーカス経路を変更するときに、実装担当または統合担当が明示的に画面確認します。通常の `Quick`・`Functional`・`Full` の成功だけで、この範囲を確認済みとはしません。専用CI、操作禁止のPC、入力遮断を通常テストの前提にせず、手動確認を選べます。起動と後片付けは[画面確認](#画面確認)に従います。

| 対象 | 操作と期待結果 |
| --- | --- |
| 通常・サマリー検索欄 | 入力欄への移動で候補が開く。上下選択は入力欄にフォーカスを残し、Enter / Tabで候補を適用する。IME変換確定のEnterでは候補を適用せず、履歴も保存しない。 |
| 検索候補の保存行 | Shift+Tabで行内ボタンへ移り、Tab / Shift+Tabで移動する。Enter / Spaceで操作後は入力欄へ戻り、検索条件は変えない。Escで戻る場合は同一条件の候補を再表示しない。 |
| 検索欄の離脱・クリア | 外クリック・別ウィンドウへの移動で候補が閉じる。クリアは対応する欄だけを空にしてフォーカスを維持・復帰し、空入力候補を表示する。 |
| 設定とプロパティのカテゴリ・ComboBox | 規定の方向キー・Home・Endでカテゴリを移動し、無効項目を飛ばす。ComboBoxの開閉・キー選択・編集入力が働き、フォーカス表示を識別できる。 |
| 表と履歴メニュー | 実キーと修飾キーで選択・編集・再生が行われる。Apps / Shift+F10と右クリックで対象のメニューを開き、日付条件追加で検索欄へ不要にフォーカスを移さない。 |

確認時の外部操作による中断は製品不具合と区別します。未確認は未確認として報告し、通常テストのスキップや成功扱いに置き換えません。対象機能の廃止、または同じ接続を外部入力から独立して検証できるようになった場合は該当手順を退役します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 通常検証の分割、選択、共有期限 | [標準スクリプト](../../../scripts/verify-refactor.ps1) | 実際の実行計画、検出されたテスト集合、終了時刻、TRXを照合する。 |
| 各段階の期限・診断・停止 | [検証スクリプト群](../../../scripts) | 実行に使う期限とプロセス所有情報、失敗時の診断・残留を確認する。 |
| UTF-8出力の読取りと保存、親環境の非変更 | [共通のプロセス処理](../../../scripts/verification-process-lifecycle.ps1) の `Set-VerificationRedirectedProcessEncoding` / `Start-VerificationRedirectedProcess`、[分割テストの起動](../../../scripts/verify-refactor.ps1) の `Start-FunctionalShardProcess` | [`VerificationProcessLifecycleTests`](../../../BeMusicSeeker.Tests/Verification/VerificationProcessLifecycleTests.cs) の `RedirectedUtf8OutputPreservesBothPipesAndArtifactsWithoutChangingParent`（`ProcessIntegration`）は、両ストリームと保存物の日本語・記号・絵文字、親設定の不変、残留プロセスなしを確認する。通常コマンドと分割テストが同じ設定処理を起動前に呼ぶことは、両入口を点検する。 |
| 画面の準備、表示、破棄 | [`TestUiDispatcherHost`](../../../BeMusicSeeker.Tests/Helpers/TestUiScheduler.cs)、[`TestWindowPresentationScope`](../../../BeMusicSeeker.Tests/Helpers/TestUiScheduler.cs) | [`SettingsControlPresentationTests`](../../../BeMusicSeeker.Tests/Settings/SettingsControlPresentationTests.cs) |
| 公開旧版と現在版の更新 | [旧版からの受入](../../../scripts/accept-v216-first-hop.ps1)、[現行更新の受入](../../../scripts/accept-net10-update.ps1) | [`UpdaterPackageSyncTests`](../../../BeMusicSeeker.Tests/Update/UpdaterPackageSyncTests.cs) |
| 利用済み設定の生成と非初回判定 | [受入設定の生成](../../../scripts/acceptance-settings-fixture.ps1)、[既存データ起動](../../../scripts/accept-net10-existing-data.ps1) | [`ApplicationSettingsLifecycleTests`](../../../BeMusicSeeker.Tests/Settings/ApplicationSettingsLifecycleTests.cs) の `LegacySettingsFixtureGeneratorPreservesTypedVersionAndScalarValues` は生成した設定を実設定ストアで読み、版・設定値・非初回判定を確認する。公開旧版での読取りはFullの実更新受入で確認する。 |
| 予期しない所有モーダルの拒否 | [共通の画面観測](../../../scripts/verification-ui-automation.ps1) | [`ExistingDataAcceptanceDialogContractTests`](../../../BeMusicSeeker.Tests/Verification/ExistingDataAcceptanceDialogContractTests.cs) はPID・所有先・可視・有効・モーダル条件とプロセス結果ゲートを確認する。自動応答は行わない。 |
| 外部エンコーダー | [`BassAudioWriter`](../../../BeMusicSeeker/Ribbit/Media/BassAudioWriter.cs) | [`ExternalAudioEncoderSmokeTests`](../../../BeMusicSeeker.Tests/Playback/ExternalAudioEncoderSmokeTests.cs) |

## 関連資料

[テスト設計](test-authoring.md)、[エージェント運用](agent-workflow.md)、[リリース](release.md)、[テスト入力の案内](../../../BeMusicSeeker.Tests/TestData/README.md)。
