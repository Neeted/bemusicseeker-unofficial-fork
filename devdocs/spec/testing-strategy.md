# テスト運用方針

最終更新: 2026-09-10

この文書は BeMusicSeeker のテスト lane、標準コマンド、時間予算の正本である。機能回帰を短時間で検出する通常検証と、性能測定、大容量データ、外部プロセス、publish / update の受入検証を分離し、テスト追加によって通常検証が際限なく長時間化しないようにする。個々の test の設計、既存 coverage 調査、共通 infrastructure、Codex handoff は [test-authoring-contract.md](test-authoring-contract.md) を正本とする。

## 運用目標

- 通常の機能検証では、portable settings の `dotnet test` 開始直前から、portable と fanout する全 Functional testhost の実際のプロセス `ExitTime` までの test execution を 300 秒以内で完了させる。個々の testhost や shard ごとの 300 秒ではない。script startup、preflight、restore、build、output 準備、artifact 回収、環境復元、whitespace確認、postflight はこの test budget の対象外である。180 秒は運用上の reporting target とし、300 秒以内の遅い成功を timeout として扱わない。
- Functional の canonical runner は portable settings の `dotnet test` 開始直前に一つの execution deadline（開始時刻 + `FunctionalTimeoutSeconds`）と、その deadline + 10 秒の failure-cleanup cutoff を作る。同じ deadline を portable、fanout launch、全 testhost 完了まで共有し、host / phase ごとに reset しない。+10 秒は timeout / failure 時の owned-process cleanup と stream drain 専用であり、成功判定を testhost 完了後の cleanup、環境復元、whitespace 確認まで延長しない。
- retained process `ExitTime` から求めた elapsed が 180 秒ちょうど以下なら通常成功として出力する。180 秒を超えて 300 秒以下で完了した成功 run も通常成功のままとし、通常の elapsed 出力に加えて 180 秒 target 超過、actual retained-`ExitTime` elapsed、およびその実時間を user-facing report に必ず含める指示を warning / diagnostic へ出力する。300 秒を超えた場合だけ timeout とし、timeout retry はこの真の timeout に限る。
- Functional は、追跡対象ファイルを変更せず、実行順序や並列度によらず決定的に成功する。
- CPU と I/O は、安定性を維持できる範囲で十分に利用して wall-clock time を短縮する。マシン負荷を抑えることだけを理由に並列度を制限しない。
- リソース競合で不安定になる場合は、共有 state、fixture ownership、固定待ち、process / file / port の競合を修正する。
- timeout 時は process tree を停止し、active または last observed test、経過時間、標準出力・標準エラー、console progress / TRX / blame artifact の場所を残す。残留 process がないことを確認し、同一 command・filter・budget・snapshot・条件で一度だけ再実行する。retry が budget 内で成功し、同じ症状の再発や決定的 artifact がなければ一過性の machine load として両結果を記録する。それ以外は原因を調査する。
- Functional の最終 acceptance は変更種別にかかわらず、同一の最終 snapshot で一回を原則とする。timeout 時の retry は前項に従い、timeout 以外の deterministic failure は初回から調査する。

## 標準コマンド

Functional / Full は locked restore の後、tool restore → `dotnet format` → Roslynator analyzer の順に事前検査し、合格してから build / 実テストへ進む。長いテストを終えた後に静的診断で不合格となるのを避けるためである。指摘があればログを確認して修正し、同じ入口を再実行する。runner は自動修正しない。Quick は filter の有無にかかわらず事前検査を省略し、通常のコード変更の最終確認には Functional 以上を使う。事前検査は Functional test execution の300秒予算に含めない。

### Functional: 通常の機能検証

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

この呼び出しでは、script startup、solution の locked restore / build / output validation、環境復元 は test execution の 300 秒予算外である。`Invoke-CanonicalFunctionalVerification` はこれらの準備後、portable settings の `dotnet test` 開始直前から全 testhost の実際の `ExitTime` までを論理的に一つの通常 test phase として所有し、`dotnet test` は restore 済みの dependency graph を使う。内部 sharding の有無にかかわらず、test execution の時間予算は一度だけ適用する。Functional からは `Performance`、`LargeFixture`、`ParserCompatibilityFull`、`ParserCompatibilitySlow`、`ProductionDiffFull`、`ProcessIntegration`、`ReleaseAcceptance` を除外する。

Quick の filter なし呼び出しもこの canonical owner を一度だけ使う。filter 付き Quick だけは明示 filter の専用 route を使い、canonical Functional の shard topology は起動しない。owner は呼び出し元が渡す diagnostics root と `FunctionalTimeoutSeconds` を受け取り、caller-owned root 配下へ restore / build / shard artifact を保存する。環境復元は mode の外側で行う。作業中の外部変更を監視する tracked-file fingerprint は取得・比較しない。

`BassCollectibleLoadContextTests` は単一クラス専用 host で実行する。この testhost は WPF host / resource を解決せず、collectible ALC の unload と BASS static initialization の非 native-load 契約を他 fixture の process state から分離する。

### Functional launch plan

Functional は category exclusion を適用した論理 test set を一度だけ discovery し、全体6 hostの一つの検証済み plan array を実行する。portable settings host の完了後、同じ descriptor object を使って残り5つの host を待機 phase なしで開始する。起動対象、selector、worker、scope、重複、remaining exclusion、foreground ownership は executable plan の実行結果で検証し、metadata-only allowlist、二重 discovery、early entry、pre-wave、fallback route は持たない。

| host | worker / scope | exact ownership |
| --- | --- | --- |
| `portable-settings` | 1 / `ClassLevel` | `PlayerPanelStateSettingsCompatibilityTests` |
| `bass-collectible` | 1 / `ClassLevel` | `BassCollectibleLoadContextTests` |
| `serial-state-a` | 1 / `ClassLevel` | executable plan の exact selector。settings / foreground / playlist settings / native logging owner |
| `serial-state-b` | 1 / `ClassLevel` | executable plan の exact selector。LR2 / compiled WPF / class-wide DNP owner |
| `remaining-bms-library` | `ProcessorCount` / `ClassLevel` | `FullyQualifiedName~BeMusicSeeker.Tests.BmsLibrary` の logical-prefix positive route |
| `remaining` | `ProcessorCount` / `ClassLevel` | dedicated selector を除く shared base の logical-negative route |

`serial-state-a` と `serial-state-b` の exact class membership は `scripts/verify-refactor.ps1` の executable plan array を正本とする。この文書は owner の分類、logical exact-once partition、worker shape を定める。残りの論理 test set `R` は `BmsLibrary` prefix の positive / negative predicate に分けて一度だけ実行し、portable 完了後の最大同時 worker 数は `3 + 2 * ProcessorCount` である。

起動順は portable host の `dotnet test` 開始直前に test execution の deadline を作り、portable host を単独で完了させ、その成功後に Bass、serial A、serial B、`remaining-bms-library`、remaining を同じ validated plan array から即時 start する。二つのremaining partitionは同じ `R` base filter、共通selectorのpositive / negative predicate、`ClassLevel` / `ProcessorCount`を持ち、互いに重ならず合計で `R` 全体を覆う。全 test process はこの一つの absolute execution deadline と、その +10 秒の failure-cleanup cutoff へ合流し、testhost ごとの deadline reset はしない。execution deadline を超えた invocation は、cleanup cutoff まで raw PID / creation identity の ownership を保持した descendant cleanup、stdout / stderr drain、artifact 保存、primary failure precedence を実行してから失敗する。各 testhost の成功判定は retained process handle の実際の `ExitTime` が deadline 内であることを基準とし、execution deadline 内に全 testhost が完了した場合だけを成功扱いにする。成功時の elapsed は portable-boundary `StartUtc` から全 host の最大 retained `ExitTime` まで、timeout 時の elapsed は `StartUtc` から execution deadline までを記録し、poll / cleanup 時間を含めない。成功後の出力収集、環境復元は test budget 外で行う。`--blame-crash` は保持し、per-testhost の `--blame-hang` 系引数と unused shard timeout plumbing は持たない。

foreground input、keyboard focus、hit testing、nested modal activation が保証対象の7 methodは、すべて現行 FQN の `SettingsForegroundInteractionTests` に属し、`serial-state-a` だけが所有する。`SettingDialogEditCompletionTests` や旧 SettingsWindow owner の FQN を foreground selector に含めない。

正常完了は対象の `Task`、signal、event、state transitionを plain `await` で待つ。coordinator は `.Wait`、`.Result`、`GetAwaiter().GetResult()`、`WaitOne`、`SpinUntil` などの同期 block を行わない。local bound は cleanup、external process、UI presentation、negative lock、timeout contract の failure watchdog に限り、固定 sleep、成功推定用の正の delay、既定 timeout helper は追加しない。

既知の UTF-8 出力元である `dotnet` / `pwsh` の redirected stdout / stderr は、process 起動前に UTF-8 decoder を明示する。通常の monitored command と Functional shard は同じ `Set-VerificationRedirectedProcessEncoding` を使い、`dotnet` の UTF-8 指定はその子プロセスの environment にだけ設定する。親の console encoding、culture、environment と、その他 native command の既定 decoder は変更しない。UTF-8 として artifact に書き直すだけでは、pipe 読取り時点の文字化けを修復できない。`VerificationProcessLifecycleTests.RedirectedUtf8OutputPreservesBothPipesAndArtifactsWithoutChangingParent` は既存 ProcessIntegration probe から本番の起動・drain・保存 owner を通し、両 stream と artifact の非 ASCII payload、親設定の不変、残留 process なしを検証する。Functional の host 数・worker 数・deadline・結果判定はこの encoding 対応で変更しない。

Functional plan の変更受入は上記の最終 acceptance 方針に従う。focused Quick では実際の runner entry point と `VerificationProcessLifecycleTests` を使い、locked restore / build、deadline、process ownership、Quick route の実行結果を検証する。release outcome の executable gates は `VerificationRunnerContractTests` が実際の `verification-test-outcomes.ps1` に接続して検証する。

## Verification map: process lifecycle

`VerificationProcessLifecycleTests` は `ProcessIntegration` lane の canonical fixture であり、PowerShell の `verification-process-lifecycle-probe.ps1` を実際の `verification-process-lifecycle.ps1` / `verify-refactor.ps1` caller seamへ接続する。各 test は GUID付き temporary diagnostics directory、probe root / descendant の exact PID・creation identity ledger、primitive event ledgerを所有し、fixture間で process、stream task、artifact pathを分離する。normal / asymmetric stream completion、lifecycle-local late-fault scope、terminal diagnostic flush、post-start caller exception、Functional fan-out の shared cutoffを同じ fixtureへ `extend` して検証する。完了signalは `Task` completion、`ManualResetEventSlim` gate、primitive observer eventであり、probe processの bounded watchdogは失敗検出専用である。lane、shard、parallelization、lifecycle owner を変える場合は、runner plan、この Verification map を同じ変更で更新する。

### Quick: 反復中の対象テスト

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<MSTest filter>'
```

実装中は変更した invariant に対応する filter を指定する。filter なしの Quick は後方互換のため Functional と同じ通常機能検証を行うが、全体確認では意図を明確にするため Functional を使う。

### Full: release / distribution 受入検証

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

Full は Functional に加えて、tool smoke、`ProcessIntegration`、self-contained publish、既存データ起動受入、update package 受入を実行する。Full は `Invoke-CanonicalFunctionalVerification` を `FunctionalTimeoutSeconds` と Full run root 配下の diagnostics root で一度だけ呼び出し、locked restore、build、built-output validation、Functional test execution を別 route で再構築しない。canonical Functional の test execution 完了後、tool smoke、公開 v2.1.6.0 cache preparation、current distribution publish、既存データ受入、現行 updater の正常系 smoke、`ProcessIntegration`、公開旧版からの移行を含む `ReleaseAcceptance` へ進む。Functional の 300 秒 hard budget と 180 秒 reporting target は test execution phase だけに適用し、Full の準備・post-Functional release 検証とは別である。Performance、LargeFixture、parser full / slow は Full にも自動では含めず、変更対象に応じて明示実行する。

Full 内でも共通の事前検査と canonical Functional test execution を tool smoke / publish や update acceptance より前に実行する。release artifact workload の CPU / I/O の影響を test execution の 300 秒予算へ持ち込まず、portable 起動から全 testhost 完了までを同じ条件で評価するためである。

共通の事前検査と Full の post-Functional phase budget は内部 `verification-runner-contract.ps1` の descriptor を正本とし、次の値を変更しない。各 phase は descriptor の diagnostics segment と bounded monitored execution を一度だけ使い、process tree を停止してから cleanup する。primary failure は cleanup failure で置き換えず、cleanup failure は phase diagnostics に記録する。primary が無い場合の cleanup failure は失敗として扱い、成功時の cleanup failure も成功に隠さない。Full の finally は開始前の process environment と working directory を復元し、復元 failure も同じ優先順位で記録する。

共通の事前検査と各 Full phase は phase 開始時に `ExecutionDeadlineUtc` を一度だけ作成し、そこから正確に 10 秒後の `CleanupDeadlineUtc` と組にする。この pair は phase 内の全 monitored command、test lane、acceptance script へそのまま渡し、各 stage / subscenario が `TimeoutSeconds` から新しい期限を作らない。execution 中の wait、process exit、stream drain、artifact 検証は execution deadline の残り時間だけを使う。primary failure / timeout 後だけ owned-process stop、stream drain、dispose、diagnostic、sandbox cleanup が cleanup grace を使える。grace 中に遅れて終了した process は成功に戻さず、primary failure を維持する。

Full は `tests-full-<run>` の下で current app / updater / exact-version `SkipDocHtml` package を一度だけ publish する。`distribution-manifest.json` はその path、version、commit、run / artifact ID と診断用 package SHA-256 を渡すための生成物である。consumer は入力の存在・配置を確認して sandbox コピーを操作し、manifest の seal、tree hash、phase 前後の内容再照合や tracked-file fingerprint による外部変更監視は行わない。公開旧版は `devdocs/acceptance/v216-first-hop/artifact.json` が指定する配布 ZIP だけを使用する。

`scripts/accept-net10-update.ps1` は `-ArtifactManifestPath` を必須とする。現在版 ZIP の隔離コピーに同じ現在版 ZIP を適用し、現行 updater の ready/decision、適用、不要な管理ファイルの削除、本物の `BeMusicSeeker.exe` の自動再起動、起動完了、終了とデータ保持を確認する。旧版からの移行は `accept-v216-first-hop.ps1` が担当する。単独実行でも Full が生成した manifest を渡し、旧ソースや現在ソースを再ビルドしない。

`UpdaterPackageSyncTests` の再起動失敗後の復元３件は、実行可能ファイルを破壊せず、managed build の実際の準備・適用処理と呼出し単位の process-start delegate を使う。NativeAOT executable を直接差し替えるための CLI や環境変数は追加しない。既存の private transaction entry を reflection で呼ぶのはこの境界に限定し、journal をテスト側で再実装せず、本番の preparation を使う。保証対象は起動境界への到達、元の例外、metadata・file/directory 遷移・利用者ファイルの復元であり、private symbol 自体の存続ではない。各ケースは GUID 付き directory を所有し、共有 hook や残留 task を作らない。他の process test と Full smoke は publish 済み updater の正常動作・復旧を引き続き確認する。

`v216-cache-preparation` は metadata を最初に読み、metadata missing / invalid は failure とする。canonical cache が存在する場合は固定 size / SHA-256 を検証し、不一致を download で隠さず failure とする。canonical cache が存在しない場合だけ、metadata の exact HTTPS URL を `curl.exe` の bounded monitored process で同一 directory の一時 file へ取得し、固定 size / SHA-256 検証後に同じ directory へ atomic publish する。途中 bytes は canonical path に出さず、temporary file は phase owner が cleanup する。source build、latest artifact、自動 repair / retry は行わない。cache miss 自体は failure ではない。

Full の non-UI updater launch / recovery と updater package-sync fixture process は `CreateNoWindow=true` を使う。画面を表示して startup / window behavior を検証する documented UI launch はこの例外であり、UI acceptance の foreground / window contract は変更しない。

| phase | budget |
| --- | ---: |
| tool restore（共通事前検査） | 120 秒 |
| format（共通事前検査） | 120 秒 |
| analyzer（共通事前検査） | 180 秒 |
| tool smoke | 60 秒 |
| v2.1.6.0 cache preparation | 180 秒 |
| current distribution publish | 180 秒 |
| existing-data acceptance | 180 秒 |
| update acceptance | 240 秒 |
| `ProcessIntegration` | 180 秒 |
| `ReleaseAcceptance` | 180 秒 |

Functional / Full の `format` phase は solution / project を評価する route を使わず、repository root を `dotnet format whitespace --folder` で検査する。`artifacts/verification`、`bin`、`obj`、`.tmp` は runner が生成する diagnostics / build output / temporary workspace のため format scope から除外するが、repository 内のそれ以外の genuine workspace files は全件検査し、workspace の format failure を隠さない。acceptance の sandbox は repository 外の temporary root に作り、primary failure を維持して後片付けする。

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

## アプリ性能・大規模データの検証

アプリが扱う規模、処理速度最優先の方針、代表操作と性能の受入条件は [performance-and-scale.md](performance-and-scale.md) に従う。本書の300秒・180秒等は検証runnerの予算であり、install / delete / startupの許容時間ではない。Functional / Full成功は大規模性能passを意味しない。

既存の明示入口は次のとおり。通常検証入口でrestore済みの環境で実行する（このscript自身は `--no-restore`）。既存scriptとrunnerの挙動は変更しない。

```powershell
pwsh -NoProfile -File .\scripts\benchmark-net10-performance.ps1 -Corpus all -Configuration Release -Scale small,medium,large -Output artifacts/performance/scale-review
```

`Net10PerformanceCorpus` の1,000 / 25,000 / 200,000行はsynthetic row corpusであり、800万キーのreverse lookup、実DB、20 ZIPのinstall / delete全体を再現するfixtureではない。現在のcandidate view / parserの仕事量検証と、実ライブラリ規模の性能受入を分ける。対応するbenchmarkがない経路は、明示的な実アプリ比較または対象ownerを通るbenchmarkで評価し、未実施なら未検証と記録する。

- 小さい入力で結果・世代不変・重複仕事等を決定的に検証できる場合は、[test-authoring-contract.md](test-authoring-contract.md)の必要性判断に従い既存coverageを利用する。特定のprivate methodやcollection型を固定するtestは作らない。
- 大規模fixture、実時間・throughputの比較は既存のopt-in laneへ置き、Functionalへ混ぜない。代表規模と固定差分の組合せを測り、chart行数だけを増やしてresource / DB規模も再現したことにしない。
- baseline / candidate、同一断面、cache / startup workの状態、完了marker、反復値・ばらつき、結果の同等性を残す。具体的な判定は性能要件を正本とし、個々のbenchmarkで恣意的な「許容退行率」を追加しない。
- 文書だけの変更はroot `AGENTS.md`のprose検証でよく、大規模fixtureや新しい性能assertionを機械的に追加しない。

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
- Everything / OS scan や app-managed custom-folder output を観測する Functional test は、live Everything service / index や test-created file の即時発見に依存しない。各 test が immutable な captured scan / physical surface を明示的に供給し、その snapshot の範囲で結果を検証する。production-shaped test factory は missing-bridge application snapshot で local Everything service を隔離し、bridge failure を managed fallback や empty success に読み替えない。
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
- test / fixture から external user / OS 操作に左右される physical OS cursor の操作・観測を導入・利用しない。`GetCursorPos`、`SetCursorPos`、`Mouse.GetPosition` による physical cursor 位置の判定を禁止し、key / routed event、explicit hit、deterministic fake / typed action seam を使う。
- 共有 WPF host は process-local な `AppearanceTheme` を退避し、ready signal を通知する前に保存せず Light theme を適用する。assembly cleanup は未起動なら何もせず、起動済みなら owner dispatcher 上で元の値へ戻してから `Application` と dispatcher を停止し、完了 signal と thread join によって終了を待つ。既存 `Application` との競合、キャッシュされた起動失敗、invoke・cleanup の例外は failure として表面化させる。testhost の `Application.ResourceAssembly` は変更しない。
- 固定 `Thread.Sleep` や余裕時間としての長い `Task.Delay` を待機手段にしない。`TaskCompletionSource`、`ManualResetEventSlim`、channel、fake scheduler / clock など、観測対象の state transition と直接結び付く同期を使う。
- deadlock / cancellation timeout は機能テストに含めてよいが、通常完了を固定時間で待つのではなく、短い failure watchdog と決定的な完了 signal を組み合わせる。
- 性能閾値、応答時間分布、throughput は機能 assertion と混ぜず Performance lane へ置く。
- 標準 runner の timeout / retry / diagnostics と deterministic failure の分類は「運用目標」に従う。test-local の failure watchdog は、runner-level retry の代わりにしない。

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

## v2.1.6.0 first-hop と release outcome gate

Full の release acceptance は、checked-in の
`devdocs/acceptance/v216-first-hop/artifact.json` が指す公開 v2.1.6.0 zip だけを
使用する。metadata が無い、size が `11,260,709` bytes と違う、または SHA-256 が
`C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2` と一致しない場合は
明示的に失敗する。公開旧版を source build や別版のファイルに置き換えない。

metadata の `downloadUrl` は `https://` の指定 GitHub release URL と完全一致しなければ
invalid とする。Full では `ProcessIntegration` より前の単一 `v216-cache-preparation`
phase が canonical cache を確認する。valid cache はそのまま hit とし、cache miss のとき
だけ同一 directory の一時 file へ bounded download し、size / SHA-256 を検証してから
atomic に canonical path へ公開する。既存 cache の mismatch、metadata invalid、download
failure、downloaded bytes の mismatch はすべて failure であり、fallback、repair、retry は
行わない。

`scripts/accept-v216-first-hop.ps1` はこの artifact の legacy protocol-1 updater を
実際に起動し、v3 package を適用する。process の bounded exit、stdout/stderr の full
drain、legacy app の close、v3 初回 `startup_ready_operable` を completion signal とし、
ready/decision handshake の存在を first-hop 完了条件にしない。適用直後は `data/` と
`config/` を byte-identical と比較し、v3 初回起動後に設定と fixture の DB semantic
state が保持されることを検証する。managed-file lock の characterization は非 zero
exit、非空 legacy stderr、`data/config` 不変を要求し、旧 managed tree の自動 rollback
や current handshake は要求しない。

Full の `ProcessIntegration` と `ReleaseAcceptance` は、それぞれ TRX または exact-FQN JSON
result receipt を保存し、
`Assert-VerificationTestOutcomes` が canonical Functional の全 shard と
ProcessIntegration/ReleaseAcceptance の receipt を合成して
`release-outcomes.json` を作る。artifact metadata の `releaseOutcome.required` にある
各 exact FQN は結果がちょうど一件で、状態が `Passed` でなければ release failure と
する。missing、duplicate、`Skipped`、`Inconclusive`、`NotExecuted`、その他の non-passed
結果は受け入れない。optional は exact FQN の allowlist に明示された場合だけ `Skipped`、
`Inconclusive`、`NotExecuted` を許し、allowlist entry と receipt の reason は空であっては
ならない。category 全体の
skip や unknown FQN の skip は許可しない。release gate は結果 cardinality、status、
optional reason を synthetic TRX/JSON receipt でも確認し、source text snapshot や広域
snapshot を oracle にしない。

## テスト変更の判断

変更分類、恒久テストの必要性、独立した oracle、既存 coverage、fixture の配置、red / negative control は [test-authoring-contract.md](test-authoring-contract.md) と `BeMusicSeeker.Tests/AGENTS.md` を正本とする。テスト不要または削除のみの判断に、Packet や代替 test を追加で要求しない。

変更の検証には、既存の関連 test、適切な実行確認、静的検査から変更に合う手段を選ぶ。Functional / Full の lane、時間予算、並列性、fixture の安全性、completion signal、外部 process / WPF の境界はこの文書の既存契約に従い、テスト不要の判断でも必要な検証は省略しない。

## 現状の改善 backlog

以下は既知の改善候補だが、Functional の最低目標達成に不要なら一括整理しない。

- 既存の `DoNotParallelize` を共有 resource の実在性で監査し、class 全体指定を resource 単位の分離へ縮小する。
- `Task.Delay`、`Thread.Sleep`、`WaitOne` 等を、通常完了待ち、failure watchdog、性能 assertion に分類し、通常完了待ちを決定的な signal へ置換する。
- updater 以外の process 起動テストを `ProcessIntegration` と軽量 contract test に分離する。
- 性能テストを専用 project または明示実行 profile へ移し、履歴比較可能な測定結果を保存する。
- test ごとの実行時間と flaky 履歴を継続収集し、Functional の 300 秒 hard budget を超える前に増加を検出する。180 秒 reporting target 超過時は actual retained-`ExitTime` elapsed を user-facing report に反映する。
- compiled test discovery から FQN、category、source fixture、lane / shard、shared resource tag、実行時間、flaky 履歴を生成する machine-readable catalog を derived artifact として整備し、Codex が候補を絞るために test source 全体を読む必要を減らす。人手管理の巨大一覧を正本にはしない。
