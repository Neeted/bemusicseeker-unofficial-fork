# テスト運用方針

最終更新: 2026-08-15

この文書は BeMusicSeeker のテスト lane、標準コマンド、時間予算の正本である。機能回帰を短時間で検出する通常検証と、性能測定、大容量データ、外部プロセス、publish / update の受入検証を分離し、テスト追加によって通常検証が際限なく長時間化しないようにする。

## 運用目標

- 通常の機能検証は `verify-refactor.ps1 -Mode Functional` のコマンド全体を 180 秒以内で完了させる。個々の testhost や shard ごとの 180 秒ではない。
- Functional は、追跡対象ファイルを変更せず、実行順序や並列度によらず決定的に成功する。
- CPU と I/O は、安定性を維持できる範囲で十分に利用して wall-clock time を短縮する。マシン負荷を抑えることだけを理由に並列度を制限しない。
- リソース競合で不安定になる場合は、共有 state、fixture ownership、固定待ち、process / file / port の競合を修正する。
- timeout 時は process tree を停止し、active または last observed test、経過時間、標準出力・標準エラー、console progress / TRX / blame artifact の場所を残す。
- runner、lane、並列化、fixture 配置を変更した場合は、Functional を同一条件で 3 回連続実行し、各回が 180 秒以内であることを確認する。

## 標準コマンド

### Functional: 通常の機能検証

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

この呼び出しの開始から終了までが 180 秒の時間予算である。solution の locked restore、build、論理的に一つの通常 test phase を含み、`dotnet test` は restore 済みの dependency graph を使う。内部 sharding の有無にかかわらず、時間予算は command 全体に一度だけ適用する。Functional からは `Performance`、`LargeFixture`、`ParserCompatibilityFull`、`ParserCompatibilitySlow`、`ProductionDiffFull`、`ProcessIntegration`、`ReleaseAcceptance` を除外する。

`BassCollectibleLoadContextTests` は単一クラス専用 shard で実行する。この testhost は WPF host や WPF resource を解決せず、collectible ALC の unload と BASS static initialization の非 native-load 契約を他 fixture の process state から分離する。runner はこのクラスが専用 shard に単独で割り当てられていない構成を test 起動前に失敗させる。

LR2 song database sync は、`BmsLibraryLr2SongDbSyncTests` の library / ownership 境界を 1-worker の `lr2-songdb-sync` class shard に残す。`Lr2SongDbSyncServiceTests` の direct service 45 case は method-level pre-wave ではなく、`BmsLibraryIrServiceTests`、`PackageInstallWorkflowOwnerTests` とともに 3-worker の `owned-db-file-class-level` shard で実行する。この shard は MSTest の `ClassLevel` scope を明示し、各 class 内の method は直列、3 class 間は並列に実行する。割り当てられた IR と package install fixture は remaining shard から除外される。各 fixture は test ごとに一意な database / filesystem resource を所有し、direct LR2 service fixture は GUID で一意な temporary directory と song database を使用して `Settings.Default`、共有 dispatcher、固定待ち、process-global mutable state に依存しない。

playlist / presentation 系は、`BmsPlaylistUpdateTests` を 1-worker の `playlist-update` class shard に単独で割り当てる。`presentation-workspace` は `PlaybackPanelViewModelTests`、`PlaylistWorkspaceViewModelTests`、`LibraryFolderTreeViewModelTests` の3 class を 1-worker で順次実行する。runner の class 重複・専用 shard 検査を維持し、同じ fixture を remaining shard や別 class shard に重複割り当てしない。

`PlaylistViewPipelineTests` は method-level pre-wave に含める。各テストの composition / `Settings` は fresh なインスタンスを使い、永続化を検証するケースは GUID で一意な temporary directory と song database を所有する。culture が必要なケースは read-only な投影経路のための scoped `ja-JP` culture だけを使い、process-wide な localization resource を変更しない。ViewModel の UI scheduler は既存の `TestUiDispatcherHost` が所有する共有 dispatcher に接続し、テストごとに dispatcher や `Application` を作らない。これらの ownership 境界により class-wide の追加 serialization は不要であり、runner の独立した exact-membership preflight によってこの class は pre-wave に一度だけ割り当てられ、remaining shard から除外される。`RowsReplacementCanceled` の lock-release probe と playlist edit commit の完了待ちにある 5 秒の wait は、通常完了を推定する sleep ではなく、deadlock / stalled commit を検出する failure watchdog として維持する。

class / method の `DoNotParallelize` は、任意の filter を同一 testhost で実行する Quick を含む全 route の safety boundary である。Functional の named shard は testhost ownership と性能 topology を定めるが、属性の safety contract を代替しない。`BmsLibraryLr2SongDbSyncTests` は process-global `Settings.Default` の LR2 mode/root、custom-folder output paths、IR flag を変更し、各 test の cleanup で固定 fixture baseline へ reset する。`BmsPlaylistUpdateTests` は playlist URL completion、LR2/output、Beatoraja output、IR settings、`PlaybackPanelViewModelTests` は playback mode、player selection、volume、panel state、stagefile / external-panel settings の元値を退避・変更・復元する。これらの lifecycle が別 class の設定観測と交差しないよう、3 fixture とも class-level serialization を維持する。

Functional の専用 shard は ownership と performance の境界である。`foreground-window-interaction` は foreground input / focus / hit testing を行う `SettingsWindowPresentationTests` と `SettingDialogEditCompletionTests` の2 classだけを 1-worker の `ClassLevel` scope で実行する。`process-global-lifecycle` は native BASS lifecycle、NLog の process-global configuration、`Settings.Default` lifecycle をそれぞれ変更する `BassNativeRuntimeTests`、`NLogWrapperTests`、`ApplicationSettingsLifecycleTests` の3 classだけを同じく 1-worker の `ClassLevel` scope で実行する。runner は両 shard の exact membership、worker 数、scope を test 起動前に検証する。これら以外の旧 class-wide fixture は、別の共有 resource 契約がない限り明示的な catchall shard を作らず remaining shard で実行する。

lock file を所有する project は `RuntimeIdentifiers=win-x64` を宣言し、C# Dev Kit などが RID を明示せず通常 restore を行った場合も、tracked lock file の base graph と `win-x64` graph を維持する。通常 restore が tracked lock file を変更した場合は dependency graph の不整合として失敗を隠さず調査する。標準検証入口は引き続き、標準スクリプトの locked `win-x64` restore と `--no-restore` build / test route を使う。RID を指定しない素の `dotnet test BeMusicSeeker.sln /p:Configuration=Release` は、locked restore、tracked-file 不変確認、共通の時間予算を迂回するため標準入口ではない。

### Quick: 反復中の対象テスト

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<MSTest filter>'
```

実装中は変更した invariant に対応する filter を指定する。filter なしの Quick は後方互換のため Functional と同じ通常機能検証を行うが、全体確認では意図を明確にするため Functional を使う。

### Full: release / distribution 受入検証

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

Full は Functional に加えて、tool / analyzer、`ProcessIntegration`、self-contained publish、既存データ起動受入、update package 受入を実行する。Functional の 180 秒予算とは別の release 検証であり、通常のコード変更ごとには実行しない。Performance、LargeFixture、parser full / slow は Full にも自動では含めず、変更対象に応じて明示実行する。

Full 内でも Functional phase は build の直後、publish や update acceptance より前に実行する。release artifact workload の CPU / I/O の影響を通常機能検証へ持ち込まず、Functional の 180 秒予算を同じ条件で評価するためである。

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
- real-window test の既定は `TestWindowActivation.NonActivating` とする。`WindowStartupLocation.Manual`、`ShowInTaskbar=false`、`ShowActivated=false` を表示直前に設定し、現在の virtual screen の外側へ配置したうえで、生成された HWND の矩形が全 monitor 外で foreground HWND でもないことを確認する。scope は `Window` の `Loaded` または早期 `Closing`、追跡した `Popup` の `Opened` で HWND が破棄される前にこの policy を観測し、観測失敗を test body から独立して保持する。開始時の `Application.Windows` と共有 dispatcher thread の HWND だけを baseline とし、明示追跡した popup、owner-depth の深い順の window（`SettingsWindow` は `CloseForOwnerShutdown`）、購読、dispatcher queue、残留 HWND の順で決定的に片付ける。失敗の優先順位は test body、presentation observation、cleanup とし、primary より後の failure は secondary diagnostic に残す。
- foreground input、keyboard focus、hit testing が保証対象である次の6 method だけは、明示的に `TestWindowActivation.ForegroundInteraction` を指定してよい。
  - `SettingsWindowPresentationTests.SettingsWindow_NavigationSupportsKeyboardAutomationAndResetsPageScroll`
  - `SettingsWindowPresentationTests.SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes`
  - `SettingsWindowPresentationTests.SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes`
  - `SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting`
  - `SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected`
  - `SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor`
- 上記2 class は同じ `foreground-window-interaction` の 1-worker / `ClassLevel` shard で実行する。`SettingsWindowPresentationTests` の61 case は remaining shard からこの shard へ移し、foreground-sensitive fixture 同士を並行表示しない。allowlist の追加、visible / foreground window が必要な受入検証、OS focus policy に依存する操作は通常の Functional へ安易に追加せず、まず Full / release acceptance の責務として分離できるか検討する。待機時間の延長や固定 sleep を foreground 成功条件にしない。
- 共有 WPF host は process-local な `AppearanceTheme` を退避し、ready signal を通知する前に保存せず Light theme を適用する。assembly cleanup は未起動なら何もせず、起動済みなら owner dispatcher 上で元の値へ戻してから `Application` と dispatcher を停止し、完了 signal と thread join によって終了を待つ。既存 `Application` との競合、キャッシュされた起動失敗、invoke・cleanup の例外は failure として表面化させる。testhost の `Application.ResourceAssembly` は変更しない。
- 固定 `Thread.Sleep` や余裕時間としての長い `Task.Delay` を待機手段にしない。`TaskCompletionSource`、`ManualResetEventSlim`、channel、fake scheduler / clock など、観測対象の state transition と直接結び付く同期を使う。
- deadlock / cancellation timeout は機能テストに含めてよいが、通常完了を固定時間で待つのではなく、短い failure watchdog と決定的な完了 signal を組み合わせる。
- 性能閾値、応答時間分布、throughput は機能 assertion と混ぜず Performance lane へ置く。
- flaky、timeout、明白な長時間化を発見したら本筋を一旦止め、再実行だけで済ませない。共有 state、固定待ち、競合、I/O、入力規模、timeout 根拠を調査してから戻る。

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

## 新しいテストを追加するとき

- 先にそのテストが保証する observable behavior と failure contract を一文で示す。
- Functional に入れるなら、少数の合成データと deterministic fake で再現できないかを確認する。
- 実データでしか再現しない場合も、最小 fixture に切り出せるなら全件 fixture に依存しない。
- 外部 process、実 app、publish artifact が必要なら対応する lane を付け、Functional へ混ぜない。
- 性能を測る場合は機能 assertion から分離し、`Performance` と opt-in 条件を付ける。
- `DoNotParallelize`、固定待ち、大容量 output copy を追加する場合は、必要性と通常検証の時間予算への影響をレビュー対象にする。

## 現状の改善 backlog

以下は既知の改善候補だが、Functional の最低目標達成に不要なら一括整理しない。

- 既存の `DoNotParallelize` を共有 resource の実在性で監査し、class 全体指定を resource 単位の分離へ縮小する。
- `Task.Delay`、`Thread.Sleep`、`WaitOne` 等を、通常完了待ち、failure watchdog、性能 assertion に分類し、通常完了待ちを決定的な signal へ置換する。
- updater 以外の process 起動テストを `ProcessIntegration` と軽量 contract test に分離する。
- 性能テストを専用 project または明示実行 profile へ移し、履歴比較可能な測定結果を保存する。
- test ごとの実行時間と flaky 履歴を継続収集し、Functional の 180 秒予算を超える前に増加を検出する。
