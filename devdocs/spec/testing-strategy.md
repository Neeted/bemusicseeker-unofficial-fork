# テスト運用方針

最終更新: 2026-08-25

この文書は BeMusicSeeker のテスト lane、標準コマンド、時間予算の正本である。機能回帰を短時間で検出する通常検証と、性能測定、大容量データ、外部プロセス、publish / update の受入検証を分離し、テスト追加によって通常検証が際限なく長時間化しないようにする。個々の test の設計、既存 coverage 調査、共通 infrastructure、Codex handoff は [test-authoring-contract.md](test-authoring-contract.md) を正本とする。

## 運用目標

- 通常の機能検証は `verify-refactor.ps1 -Mode Functional` のコマンド全体を 180 秒以内で完了させる。個々の testhost や shard ごとの 180 秒ではない。
- Functional の canonical runner は開始時に一つの execution deadline（開始時刻 + `FunctionalTimeoutSeconds`）と、その deadline + 10 秒の failure-cleanup cutoff を作る。restore、build、portable、fanout、output / whitespace success check は同じ execution deadline を使い、host / phase ごとに reset しない。+10 秒は timeout / failure 時の owned-process cleanup と stream drain 専用であり、成功を command budget 外へ延長しない。
- Functional は、追跡対象ファイルを変更せず、実行順序や並列度によらず決定的に成功する。
- CPU と I/O は、安定性を維持できる範囲で十分に利用して wall-clock time を短縮する。マシン負荷を抑えることだけを理由に並列度を制限しない。
- リソース競合で不安定になる場合は、共有 state、fixture ownership、固定待ち、process / file / port の競合を修正する。
- timeout 時は process tree を停止し、active または last observed test、経過時間、標準出力・標準エラー、console progress / TRX / blame artifact の場所を残す。最初の単発 timeout は、残留 process がないことを確認して同一 command・filter・budget で一度だけ再実行してから調査要否を判断する。
- runner、lane、並列化、fixture 配置を変更した場合は、同一の最終 snapshot で Functional を同一条件で3回連続実行し、各回が180秒以内であることを確認する。途中で failure を修正した場合は修正前の pass を数えず、1回目からやり直す。

## 標準コマンド

### Functional: 通常の機能検証

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

この呼び出しの開始から終了までが 180 秒の時間予算である。solution の locked restore、build、output validation、論理的に一つの通常 test phase を `Invoke-CanonicalFunctionalVerification` が所有し、`dotnet test` は restore 済みの dependency graph を使う。内部 sharding の有無にかかわらず、時間予算は command 全体に一度だけ適用する。Functional からは `Performance`、`LargeFixture`、`ParserCompatibilityFull`、`ParserCompatibilitySlow`、`ProductionDiffFull`、`ProcessIntegration`、`ReleaseAcceptance` を除外する。

Quick の filter なし呼び出しもこの canonical owner を一度だけ使う。filter 付き Quick だけは明示 filter の専用 route を使い、canonical Functional の shard topology は起動しない。owner は呼び出し元が渡す diagnostics root と `FunctionalTimeoutSeconds` を受け取り、caller-owned root 配下へ restore / build / shard artifact を保存する。tracked-file fingerprint の取得と不変確認は mode の外側で一度ずつ行う。

`BassCollectibleLoadContextTests` は単一クラス専用 host で実行する。この testhost は WPF host / resource を解決せず、collectible ALC の unload と BASS static initialization の非 native-load 契約を他 fixture の process state から分離する。

### Functional launch plan

Functional は category exclusion を適用した論理 test set を一度だけ discovery し、全体6 hostの一つの検証済み plan array を実行する。portable settings host の完了後、同じ descriptor object を使って残り5つの host を待機 phase なしで開始する。validator は起動対象そのものに対して host 名、selector、worker、scope、重複、remaining exclusion、foreground ownership、退役 FQN を検証する。metadata-only allowlist、二重 discovery、early entry、pre-wave、fallback route は持たない。

| host | worker / scope | exact ownership |
| --- | --- | --- |
| `portable-settings` | 1 / `ClassLevel` | `PlayerPanelStateSettingsCompatibilityTests` |
| `bass-collectible` | 1 / `ClassLevel` | `BassCollectibleLoadContextTests` |
| `serial-state-a` | 1 / `ClassLevel` | Unit 4e-A の exact 23 class selector |
| `serial-state-b` | 1 / `ClassLevel` | Unit 4e-A の exact 20 class selector |
| `remaining-bms-library` | `ProcessorCount` / `ClassLevel` | `FullyQualifiedName~BeMusicSeeker.Tests.BmsLibrary` の logical-prefix positive route |
| `remaining` | `ProcessorCount` / `ClassLevel` | 上記 selector の negative route。portable、BASS、A、B の exact 45 classを除く shared baseを logical once |

`serial-state-a` は settings / foreground / playlist settings / native logging owner を、`serial-state-b` は LR2、compiled WPF、class-wide DNP owner を所有する。A / B の exact selector はこの Unit 4e-A plan と runner の実装本文を正本とし、残りの論理 test setは、45 class exclusionを共有する `R` を一つの `BmsLibrary` selectorのpositive / negative predicateへ分けて一度だけ実行する。BmsLibraryのexact class allowlistは実行源にせず、論理 prefixで自動 routeする。fanout 5 host の専用 worker は Bass / A / B が各1、二つのremaining partitionが各 `ProcessorCount` で、portable完了後の最大同時 worker 数は約27である。

起動順は portable host を単独で完了させ、その成功後に Bass、serial A、serial B、`remaining-bms-library`、remaining を同じ validated plan array から即時 start する。二つのremaining partitionは同じ `R` base filter、共通selectorのpositive / negative predicate、`ClassLevel` / `ProcessorCount`を持ち、互いに重ならず合計で `R` 全体を覆う。全 test process は canonical 開始時刻 + `FunctionalTimeoutSeconds` の一つの absolute execution deadline と、その +10 秒の failure-cleanup cutoff へ合流し、testhost ごとの deadline reset はしない。execution deadline を超えた invocation は、cleanup cutoff まで raw PID / creation identity の ownership を保持した descendant cleanup、stdout / stderr drain、artifact 保存、primary failure precedence を実行してから失敗する。execution deadline 内の成功だけを成功扱いにする。`--blame-crash` は保持し、per-testhost の `--blame-hang` 系引数と unused shard timeout plumbing は持たない。

foreground input、keyboard focus、hit testing、nested modal activation が保証対象の7 methodは、すべて現行 FQN の `SettingsForegroundInteractionTests` に属し、`serial-state-a` だけが所有する。`SettingDialogEditCompletionTests` や旧 SettingsWindow owner の FQN を foreground selector に含めない。

正常完了は対象の `Task`、signal、event、state transitionを plain `await` で待つ。coordinator は `.Wait`、`.Result`、`GetAwaiter().GetResult()`、`WaitOne`、`SpinUntil` などの同期 block を行わない。local bound は cleanup、external process、UI presentation、negative lock、timeout contract の failure watchdog に限り、固定 sleep、成功推定用の正の delay、既定 timeout helper は追加しない。

Functional の category exclusion、locked restore / build、180 秒 command budget、tracked-file fingerprint、Quick / Full mapping は維持する。Functional plan の変更受入は、同一 snapshot・同一条件の Functional 3回連続とし、この Unit の focused Quick では runner contract と process lifecycle contract を検証する。

## Verification map: process lifecycle

`VerificationProcessLifecycleTests` は `ProcessIntegration` lane の canonical fixture であり、PowerShell の `verification-process-lifecycle-probe.ps1` を実際の `verification-process-lifecycle.ps1` / `verify-refactor.ps1` caller seamへ接続する。各 test は GUID付き temporary diagnostics directory、probe root / descendant の exact PID・creation identity ledger、primitive event ledgerを所有し、fixture間で process、stream task、artifact pathを共有しない。normal / asymmetric stream completion、lifecycle-local late-fault scope、terminal diagnostic flush、post-start caller exception、Functional fan-out の shared cutoffを同じ fixtureへ `extend` して検証する。完了signalは `Task` completion、`ManualResetEventSlim` gate、primitive observer eventであり、probe processの bounded watchdogは失敗検出専用である。新しい lane、shard、`DoNotParallelize`、production lifecycleのtest側コピー、固定sleepは追加しない。

### Quick: 反復中の対象テスト

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<MSTest filter>'
```

実装中は変更した invariant に対応する filter を指定する。filter なしの Quick は後方互換のため Functional と同じ通常機能検証を行うが、全体確認では意図を明確にするため Functional を使う。

### Full: release / distribution 受入検証

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

Full は Functional に加えて、tool / analyzer、`ProcessIntegration`、self-contained publish、既存データ起動受入、update package 受入を実行する。Full は `Invoke-CanonicalFunctionalVerification` を `FunctionalTimeoutSeconds` と Full run root 配下の diagnostics root で一度だけ呼び出し、locked restore、build、built-output validation、Functional shard を別 route で再構築しない。canonical Functional 完了後に current distribution publish / baseline / acceptance へ進む。Functional の 180 秒予算とは別の release 検証であり、通常のコード変更ごとには実行しない。Performance、LargeFixture、parser full / slow は Full にも自動では含めず、変更対象に応じて明示実行する。

Full 内でも canonical Functional phase は tool restore の後、tool smoke / publish や update acceptance より前に実行する。release artifact workload の CPU / I/O の影響を通常機能検証へ持ち込まず、Functional の 180 秒予算を同じ条件で評価するためである。

Full の post-Functional phase budget は内部 `verification-runner-contract.ps1` の descriptor を正本とし、次の値を変更しない。各 phase は descriptor の diagnostics segment と bounded monitored execution を一度だけ使い、process tree を停止してから cleanup する。primary failure は cleanup failure で置き換えず、cleanup failure は phase diagnostics に記録する。primary が無い場合の cleanup failure は失敗として扱い、成功時の cleanup failure も成功に隠さない。Full の finally は開始前の process environment と working directory を復元し、復元 failure も同じ優先順位で記録する。

Full は `tests-full-<run>` の下に一つだけ `distribution` root を作る。current app / updater / exact-version `SkipDocHtml` package と、固定 baseline commit をその root に準備し、`distribution-manifest.json` と SHA-256 seal を作成する。manifest は schema、run / artifact ID、absolute canonical paths、exact version / commit、relative path を `/` に正規化して ordinal-sort した tree hash、package SHA-256 を持つ。runner は baseline preparation 完了時の run ID、artifact ID、manifest JSON の SHA-256、seal の値を expected identity として保持し、`existing-data`、`update`、`ProcessIntegration`、`ReleaseAcceptance` の各 consumer の前後と Full 最終確認で全値の exact match を要求する。したがって consumer が manifest を自己整合的に置換・再 seal しても受け入れない。timestamp / latest scan、global fixed-path fallback、Full consumer の再 publish は行わない。acceptance script の manifest mode は明示指定時に必須であり、manifest が無い場合や path、ID、version、seal、artifact が一致しない場合は明示 failure にする。bare invocation は従来どおり self-preparation を許容する互換 mode である。

Full の non-UI updater launch / recovery と updater package-sync fixture process は `CreateNoWindow=true` を使う。画面を表示して startup / window behavior を検証する documented UI launch はこの例外であり、UI acceptance の foreground / window contract は変更しない。

| phase | budget |
| --- | ---: |
| tool restore | 120 秒 |
| tool smoke | 60 秒 |
| current distribution publish | 180 秒 |
| baseline preparation | 300 秒 |
| existing-data acceptance | 180 秒 |
| update acceptance | 240 秒 |
| `ProcessIntegration` | 180 秒 |
| `ReleaseAcceptance` | 180 秒 |
| format | 120 秒 |
| analyzer | 180 秒 |

Full の `format` phase は solution / project を評価する route を使わず、repository root を `dotnet format whitespace --folder` で検査する。`artifacts/verification`、`bin`、`obj` は runner が生成する diagnostics / build output のため format scope から除外するが、repository 内のそれ以外の genuine workspace files は全件検査し、workspace の format failure を隠さない。baseline preparation の source checkout と build output は OS の temporary root（repository 外）へ展開し、検証用 distribution package と manifest だけを Full run root へコピーする。これにより baseline source が SDK の広域 item glob や後続 project evaluation に混入しない。temporary root の cleanup は primary phase failure を置き換えず、成功時の cleanup failure は phase failure として扱う。

## テスト lane

| lane | `TestCategory` | Functional | Full | 用途 |
| --- | --- | --- | --- | --- |
| 機能検証 | 省略または機能名 | 実行 | 実行 | 小さい合成入力による決定的な回帰検証 |
| 軽量互換検証 | `Compatibility` | 実行 | 実行 | 少数 fixture による parser / schema 互換 |
| 外部プロセス統合 | `ProcessIntegration` | 除外 | 明示実行 | updater、別 process、PowerShell 等との実統合 |
| リリース受入 | `ReleaseAcceptance` | 除外 | 明示実行 | publish artifact、update package、実アプリ smoke |
| 性能測定 | `Performance` / `Net10Performance` | 除外 | 除外 | 変更前後の性能比較と退行調査 |
| 大容量 fixture | `LargeFixture` | 除外 | 除外 | 実 DB や大量 fixture の互換性検証 |
| Parser full | `ParserCompatibilityFull` | 除外 | 除外 | 実譜面全件の参照実装互換 |
| Production diff | `ProductionDiffFull` | 除外 | 除外 | production data との差分調査 |
| Parser slow | `ParserCompatibilitySlow` | 除外 | 除外 | 既知の巨大・低速 fixture |

カテゴリはテストの保証内容ではなく実行特性を表す。たとえば軽量な互換性テストは `Compatibility` のまま Functional に含める。複数 lane に該当するときは、より重い実行特性のカテゴリを追加する。

## 外部 audio encoder の opt-in smoke

`ExternalAudioEncoderSmokeTests` は、repository や NuGet package に含めない利用者提供の encoder 実行ファイルを、明示的に opt-in したときだけ使う `ProcessIntegration` テストである。テスト内で生成した短い deterministic stereo PCM/WAV を、production の NULL_DEVICE `BassAudioWriter`、encoder session、pull rendering、stop、cleanup へ渡し、出力の最小 signature と collision suffix を確認する。固定 sleep や copyrighted media fixture は使わない。

- `BMS_TEST_AUDIO_ENCODERS=1` が無い通常 run は `Assert.Inconclusive` で終了する。
- `BMS_TEST_AUDIO_ENCODER_DIR` を指定した場合はそのディレクトリを先に検索し、その後 production と同じ `AppContext.BaseDirectory`、`libs\x64`、`x64` の順で検索する。
- `BMS_TEST_AUDIO_ENCODER_TYPES` は `MP3_LAME,AAC_NERO,OPUS,FLAC,OGG_VORBIS` のカンマ区切り subset である。指定した実行ファイルが無い場合は fail とし、未指定時に一つも見つからない場合も全 skip で成功扱いにしない。
- 使用可能な encoder が見つかった場合はすべて実行し、MP3、M4A、Opus、FLAC、Ogg Vorbis の container/frame marker を検証する。
- encoder binary、license、生成 WAV は repository の fixture や package asset に追加しない。cleanup failure が primary failure を置き換えない。

通常の regression は次の filter で行う。`ProcessIntegration` の opt-in 条件が無いため外部 process は起動しない。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~AudioEncoderCommandFactoryTests|FullyQualifiedName~AudioEncoderSessionTests|FullyQualifiedName~BassAudioWriterTests|FullyQualifiedName~ExternalAudioEncoderSmokeTests'
```

実行ファイルを用いる場合は、同じ test filter を `BMS_TEST_AUDIO_ENCODERS=1` と必要な `BMS_TEST_AUDIO_ENCODER_DIR` / `BMS_TEST_AUDIO_ENCODER_TYPES` とともに明示実行する。実行環境に対応 executable が一つも無い場合は、opt-in test の deterministic failure と検索結果を記録し、real encoder pass 未実施として扱う。

## Functional に含めるテスト

- 小さい合成入力または少数 fixture で完結する機能テスト。
- in-memory / temp directory / deterministic fake で閉じる DB、filesystem、audio、dispatcher、network boundary のテスト。
- test ごとの固有 resource を所有し、並列実行しても競合しないテスト。
- timeout が機能契約そのものである場合を除き、wall clock の偶然ではなく signal、barrier、fake clock、完了通知で同期するテスト。

Functional に含めないもの:

- 処理時間や比率そのものを測定する benchmark / performance test。
- 実譜面数百〜数千件、巨大 DB、大量コピーを必要とする互換性棚卸し。
- updater や別の `dotnet` / PowerShell process、publish artifact、実アプリを起動する end-to-end 受入検証。
- 固定 sleep の経過だけで非同期処理の完了を推定するテスト。

## 非同期・並列テストの規則

- MSTest の assembly-level parallel execution を標準とする。`DoNotParallelize` は process-wide singleton、固定 port、外部アプリなど、分離できない共有 resource が実在する場合だけ使い、理由をコメントまたは fixture contract で示す。
- WPF UI テストは `TestUiDispatcherHost` が所有する単一の `Application` と専用 STA dispatcher を共有する。host は assembly 初期化では起動せず、`Dispatcher`、`Invoke`、`Drain`、`RunWindowTest` の初回利用時に thread-safe に遅延起動する。fixture や test ごとに `Application` / STA dispatcher thread を作らない。window を表示しない UI 操作は `TestUiDispatcherHost.Invoke` で owner dispatcher に送り、実 `Window` / `Popup` を表示するテストは `RunWindowTest` の `TestWindowPresentationScope` を使う。
- `TestWindowPresentationScope.ShowAndWaitForContentRendered` は `ContentRendered` を `Show` より前に購読し、固定 sleep や busy wait を使わない有限の dispatcher pump によって、`IsLoaded`、render signal、非ゼロ layout、非ゼロ HWND を確認する。modal window、表示直後に閉じる window、または delayed `ContentRendered` を個別に制御する場合は、`ShowDialog` / `Show` の直前に `PrepareForOwnedPresentation` を呼ぶ。presentation policy は window 構築時ではなく、この表示境界で適用する。
- real-window test の既定は `TestWindowActivation.NonActivating` とする。`WindowStartupLocation.Manual`、`ShowInTaskbar=false`、`ShowActivated=false` を表示直前に設定し、現在の virtual screen の外側へ配置したうえで、生成された HWND の矩形が全 monitor 外で foreground HWND でもなく、native extended style に `WS_EX_NOACTIVATE` を含むことを確認する。window の HWND 作成境界では既存 extended style を保持したまま `WS_EX_NOACTIVATE` を設定して readback し、追跡した popup は `Opened` で同じ native policy を適用する。scope は `Window` の `Loaded` または早期 `Closing`、追跡した `Popup` の `Opened` で HWND が破棄される前にこの policy を観測し、native API failure / style mismatch を含む観測失敗を test body から独立して保持する。開始時の `Application.Windows` と共有 dispatcher thread の HWND だけを baseline とし、明示追跡した popup、owner-depth の深い順の window（`SettingsWindow` は `CloseForOwnerShutdown`）、購読、dispatcher queue、残留 HWND の順で決定的に片付ける。失敗の優先順位は test body、presentation observation、cleanup とし、primary より後の failure は secondary diagnostic に残す。
- foreground input、keyboard focus、hit testing、nested modal activation が保証対象である次の7 method だけは、明示的に `TestWindowActivation.ForegroundInteraction` を指定してよい。
  - `SettingsForegroundInteractionTests.SettingsWindow_NavigationSupportsKeyboardAutomationAndResetsPageScroll`
  - `SettingsForegroundInteractionTests.SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes`
  - `SettingsForegroundInteractionTests.SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes`
  - `SettingsForegroundInteractionTests.SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow`
  - `SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting`
  - `SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected`
  - `SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor`
- `serial-state-a` is the only Functional host that owns `SettingsForegroundInteractionTests`; all seven foreground-sensitive methods remain explicit `TestWindowActivation.ForegroundInteraction` cases. `SettingDialogEditCompletionTests` and retired SettingsWindow owner FQNs are not foreground selectors. allowlist の追加、visible / foreground window が必要な受入検証、OS focus policy に依存する操作は通常の Functional へ安易に追加せず、まず Full / release acceptance の責務として分離できるか検討する。待機時間の延長や固定 sleep を foreground 成功条件にしない。
- 共有 WPF host は process-local な `AppearanceTheme` を退避し、ready signal を通知する前に保存せず Light theme を適用する。assembly cleanup は未起動なら何もせず、起動済みなら owner dispatcher 上で元の値へ戻してから `Application` と dispatcher を停止し、完了 signal と thread join によって終了を待つ。既存 `Application` との競合、キャッシュされた起動失敗、invoke・cleanup の例外は failure として表面化させる。testhost の `Application.ResourceAssembly` は変更しない。
- 固定 `Thread.Sleep` や余裕時間としての長い `Task.Delay` を待機手段にしない。`TaskCompletionSource`、`ManualResetEventSlim`、channel、fake scheduler / clock など、観測対象の state transition と直接結び付く同期を使う。
- deadlock / cancellation timeout は機能テストに含めてよいが、通常完了を固定時間で待つのではなく、短い failure watchdog と決定的な完了 signal を組み合わせる。
- 性能閾値、応答時間分布、throughput は機能 assertion と混ぜず Performance lane へ置く。
- 最初の単発 timeout では、process tree を停止して diagnostics を保存し、残留 test process がないことを確認したうえで、同一 command・filter・budget を一度だけ再実行する。
- 2回目が budget 内で成功し、同じ症状の再発または artifact 上の決定的 evidence がなければ、初回を一過性のマシン負荷として両方の結果を記録し、本筋へ戻る。
- 2回目も timeout / failure、同じ症状の再発、active test の停止、または artifact が問題を示す場合は、本筋を一旦止め、共有 state、固定待ち、競合、I/O、入力規模、timeout 根拠を調査してから戻る。timeout 延長、無制限の再試行、並列度低下だけによる隠蔽は行わない。
- timeout 以外の deterministic failure は、再実行で消えることを期待して先送りせず、最初の failure evidence から原因を確認する。

## Parser・実データ互換検証

Parser の通常検証は小さい合成譜面、少数の実 fixture、既知 edge case を使う。full 検証は次のように明示して行う。

```powershell
$env:BMS_TEST_CHART_INFO_FULL = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ParserCompatibilityFull'

$env:BMS_TEST_PRODUCTION_DIFF_FULL = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ProductionDiffFull'

$env:BMS_TEST_CHART_INFO_SLOW = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ParserCompatibilitySlow'
```

`ChartInfoParser`、`ChartInfoBuildService`、BMSON/BMS decode、`chart_info` schema 投影を変更した場合は該当 full lane を追加する。大容量 fixture の詳細は [TestData README](../../BeMusicSeeker.Tests/TestData/README.md) を参照する。

`chart_info_real` と `chart_info_edge_cases` は、`BMS_TEST_CHART_INFO_FULL=1` を build 開始前に設定した場合だけ test output へコピーする。これらの opt-in parser test は対応する full / slow category と環境変数 guard の両方を持ち、flag が無ければ `Inconclusive`、flag があるのに fixture が無ければ failure とする。少数でも約 20 MiB を parse する `chart_info_production_latest_diff` は `ProductionDiffFull` / `LargeFixture` とし、Functional には含めない。Functional の parser behavior は小さい合成入力で検証する。

Chart-info metadata lifecycle coverage uses the five distinct owner fixtures `ChartInfoMetadataSchemaExportImportTests`, `ChartInfoParserBehaviorTests`, `ChartInfoBackfillStorageTests`, `ChartInfoInlineHydrationTests`, and `ChartInfoInstallFailureRetryTests`. They remain in the catch-all `remaining` ClassLevel route with no named selector; normal hydration and backfill completion is awaited from `BMSLibrary.PropertyChanged` state transitions, while cleanup, lock, and timeout-contract bounds remain local.

## 新しいテストを追加するとき

詳細は [test-authoring-contract.md](test-authoring-contract.md) と `BeMusicSeeker.Tests/AGENTS.md` を正本とする。

- 先に observable behavior / failure contract、production owner、candidate existing fixture、`extend / replace / new`、shared resource / lane、completion signal、退役 test を coverage ledger へ示す。
- feature spec、production symbol、feature 用語、failure 文言で候補を絞り、最初から test project 全体を通読しない。canonical fixture を新設・移動・分割する場合は feature spec の `Verification map` を更新する。
- Functional に入れるなら、少数の合成データと deterministic fake で再現できないかを確認する。実データでしか再現しない場合も、最小 fixture に切り出せるなら全件 fixture へ依存しない。
- 外部 process、実 app、publish artifact が必要なら対応する lane を付け、Functional へ混ぜない。性能を測る場合は機能 assertion から分離し、`Performance` と opt-in 条件を付ける。
- fixture へ新しい直接 `Dispatcher.PushFrame`、`HwndSourceParameters`、unbounded process / stream wait を追加せず、既存の owner helper を使う。source text / private reflection は artifact 自体が contractである理由と退役条件を残す。
- `DoNotParallelize`、固定待ち、大容量 output copy を追加する場合は、必要性、resource owner、通常検証の時間予算への影響を review 対象にする。

## 現状の改善 backlog

以下は既知の改善候補だが、Functional の最低目標達成に不要なら一括整理しない。

- 既存の `DoNotParallelize` を共有 resource の実在性で監査し、class 全体指定を resource 単位の分離へ縮小する。
- `Task.Delay`、`Thread.Sleep`、`WaitOne` 等を、通常完了待ち、failure watchdog、性能 assertion に分類し、通常完了待ちを決定的な signal へ置換する。
- updater 以外の process 起動テストを `ProcessIntegration` と軽量 contract test に分離する。
- 性能テストを専用 project または明示実行 profile へ移し、履歴比較可能な測定結果を保存する。
- test ごとの実行時間と flaky 履歴を継続収集し、Functional の 180 秒予算を超える前に増加を検出する。
- compiled test discovery から FQN、category、source fixture、lane / shard、shared resource tag、実行時間、flaky 履歴を生成する machine-readable catalog を derived artifact として整備し、Codex が候補を絞るために test source 全体を読む必要を減らす。人手管理の巨大一覧を正本にはしない。
