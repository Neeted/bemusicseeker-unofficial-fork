# テスト運用方針

最終更新: 2026-08-23

この文書は BeMusicSeeker のテスト lane、標準コマンド、時間予算の正本である。機能回帰を短時間で検出する通常検証と、性能測定、大容量データ、外部プロセス、publish / update の受入検証を分離し、テスト追加によって通常検証が際限なく長時間化しないようにする。個々の test の設計、既存 coverage 調査、共通 infrastructure、Codex handoff は [test-authoring-contract.md](test-authoring-contract.md) を正本とする。

## 運用目標

- 通常の機能検証は `verify-refactor.ps1 -Mode Functional` のコマンド全体を 180 秒以内で完了させる。個々の testhost や shard ごとの 180 秒ではない。
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

`BassCollectibleLoadContextTests` は単一クラス専用 shard で実行する。この testhost は WPF host や WPF resource を解決せず、collectible ALC の unload と BASS static initialization の非 native-load 契約を他 fixture の process state から分離する。runner はこのクラスが専用 shard に単独で割り当てられていない構成を test 起動前に失敗させる。

library/chart initialization と metadata は、`BmsLibraryInitializationServiceTests`、`BmsLibraryZeroNoteRefreshTests`、`ChartInfoMetadataTests` の3 classを同じ `library-chart-classwide` testhost/process に割り当て、`ClassLevel` scope の2 workerで実行する。3 classの exact membership、2 worker、`ClassLevel` scope、remaining shardからの assigned-class exclusion、他 routeとの重複なしを test 起動前に独立した allowlist で検証する。これは同一 process 内の worker 数だけを増やす headroom 調整であり、named shard の process 数、exclusive portable-settings lane、`method-level-pre-wave` の先行順、diagnostics / process-tree cleanup、既存の160秒 test-phase観測と180秒 command budgetは変更しない。

constructor-only compiled WPF fixtures は、`LoadPlaylistURIDialogTests`、`MainWindowChartPresentationWpfTests`、`MainWindowPackageMaintenanceWpfTests`、`MainWindowPlaybackWpfTests`、`MainWindowPlayHistoryWpfTests`、`MainWindowPlaylistWorkspaceWpfTests`、`MainWindowProgressStatusBarWpfTests`、`MainWindowSelectedChartContextMenuWpfTests`、`MainWindowTreePresentationWpfTests`、`MainWindowViewHostTests`、`SettingsWindowCompiledBehaviorTests`、`UiDialogCoordinatorWpfTests` の12 classを `compiled-wpf-classwide` の1-worker / `ClassLevel` shardへ割り当てる。これらは process-scoped WPF resources、cursor lifecycle、test-owned self-completing modal seamを共有するためclass-wideに直列化する。production app startup、production `MainWindow` の `Show`、`InitializeAsync` の完走は行わない。一方、dialog fixtureは非activeなtest-owned owner/windowを表示し、modal dispatcher loopを決定的に自己完了する。runnerは12 classの独立したexact allowlist、worker 1、`ClassLevel`、assigned-classのremaining除外、他routeとの重複なしを起動前に検証する。このrunner topology変更の受入は同一条件のFunctional 3回連続（各command 180秒以内、tracked file不変、残留test processなし）とする。

Functional の orchestration は、exclusive portable-settings lane完了後は `lr2-songdb-sync` だけを early entryとしてexact 1回起動し、entryとraw process ownershipを記録してから専用 `method-level-pre-wave` を実行する。pre-wave完了直後にcompleted / exit 0のLR2 entryは一度だけaccountし、runningは同じentryを維持したまま `remaining` と全fanout shardを起動する。settingsの2 routeはpre-wave後のfanoutでそれぞれexact 1回だけ起動し、early entryとして再accountまたは再launchしない。nonzero / canceled / invalid stateはfanout前に失敗させ、LR2 entryと部分起動済みfanoutを同じglobal deadline・result・diagnostics・cleanup setへ合流させる。process start後のentry構築例外もraw identityを失わず、primary failureをcleanup failureで置換しない。runnerは実際に起動へ渡す同一 shard object / arrayをlaunch前validatorへ渡し、early membership、fanout inclusion / exclusion、exact object identity、remaining exclusion、cross-route uniqueness、重複なしを実行用構成そのもので検証する。

LR2 song database sync は、`BmsLibraryLr2SongDbSyncTests` の library / ownership 境界を 1-worker の `lr2-songdb-sync` class shard に残す。`Lr2SongDbSyncServiceTests` の direct service 45 case は、`BmsLibraryIrServiceTests`、`PackageInstallWorkflowOwnerTests` とともに 3-worker の `owned-db-file-class-level` shard で実行する。この shard は MSTest の `ClassLevel` scope を明示し、各 class 内の method は直列、3 class 間は並列に実行する。割り当てられた IR と package install fixture は remaining shard から除外される。各 fixture は test ごとに一意な database / filesystem resource を所有し、direct LR2 service fixture は GUID で一意な temporary directory と song database を使用して `Settings.Default`、共有 dispatcher、固定待ち、process-global mutable state に依存しない。

playlist / presentation 系は、`BmsPlaylistExternalReloadTests` と `BmsPlaylistCustomFolderOutputTests` を `playlist-external-custom-folder`、`BmsPlaylistPersistenceLifecycleTests` と `BmsPlaylistMigrationAndRegistrationTests` を `playlist-persistence-migration` の各1-worker / `ClassLevel`専用 shardへ割り当てる。旧 `BmsPlaylistUpdateTests` と `playlist-update` routeは退役し、4 fixtureの class-wide `DoNotParallelize`、process-local `Settings.Default`、GUID付き temporary database / filesystem ownershipを維持する。`presentation-workspace` は `PlaybackPanelViewModelTests`、`LibraryFolderTreeViewModelTests` と、`PlaylistWorkspaceExternalSourceTests`、`PlaylistWorkspaceActionWorkflowTests`、`PlaylistWorkspaceDetailRefreshTests`、`PlaylistWorkspacePresentationStateTests`、`PlaylistWorkspacePersistenceCommandTests` の5 workspace owner fixtureを `ClassLevel` scope の3-workerで実行する。`PlaybackPanelViewModelTests` の class-wide `DoNotParallelize` safety boundaryは維持し、他のworkspace fixtureだけが並行可能になる。runner の class 重複・専用 shard 検査を維持し、旧 `PlaylistWorkspaceViewModelTests` と同じ fixtureを remaining shard や別 class shardに重複割り当てしない。

次の7 fixtureは、独立した temporary database / directory を test ごとに所有するため、exclusive portable-settings lane の直後に dedicated な `method-level-pre-wave` で実行する: `BmsLibraryFolderRenameRefreshTests`、`BmsLibraryPendingPackageRegroupTests`、`AppSchemaPreflightServiceTests`、`BmsLibraryMaintenanceServiceTests`、`BmsLibraryDuplicateServiceTests`、`BmsPlaylistExternalLoadTests`、`PlaylistViewPipelineTests`。この pre-wave は remaining と同じ12 workerの `MethodLevel` testhost を使うが、LR2 early entry以外の Functional shard と並行起動せず、cross-shard I/O contention を避けるために専用 lane とする。pre-wave の完了後に remaining とLR2 early entry以外の named shardを起動し、pre-wave artifact と経過時間は同じ global budget に含める。`BmsLibraryPendingPackageRegroupTests` の4 methodは `AutoApplyAmbiguousInstallDestination` を、`BmsPlaylistExternalLoadTests` の5 methodは playlist関連の `Settings.Default` を変更・復元するため、既存の method-level `DoNotParallelize` を個別の safety contract として維持する。その他のテストは、永続化を検証するケースを含め GUID で一意な temporary directory と song database を所有し、`PlaylistViewPipelineTests` は fresh な composition / settings を使う。culture が必要なケースは read-only な投影経路のための scoped `ja-JP` culture だけを使い、process-wide な localization resource を変更しない。ViewModel の UI scheduler は既存の `TestUiDispatcherHost` が所有する共有 dispatcher に接続し、テストごとに dispatcher や `Application` を作らない。`RowsReplacementCanceled` の lock-release probe と playlist edit commit の完了待ちにある 5 秒の wait は、通常完了を推定する sleep ではなく、deadlock / stalled commit を検出する failure watchdog として維持する。runner はこの pre-wave の exact membership、remaining からの assigned-class exclusion、worker 数、`MethodLevel` scope を test 起動前に独立した allowlist で検証する。timeout の延長や DNP の追加は行わない。

class / method の `DoNotParallelize` は、任意の filter を同一 testhost で実行する Quick を含む全 route の safety boundary である。Functional の named shard は testhost ownership と性能 topology を定めるが、属性の safety contract を代替しない。`BmsLibraryLr2SongDbSyncTests` は process-global `Settings.Default` の LR2 mode/root、custom-folder output paths、IR flag を変更し、各 test の cleanup で固定 fixture baseline へ reset する。4つのBMS playlist owner fixtureはそれぞれ playlist URL completion、LR2/output、Beatoraja output、IR settingsの元値を退避・変更・復元する。`PlaybackPanelViewModelTests` は playback mode、player selection、volume、panel state、stagefile / external-panel settingsを退避・変更・復元する。これらの lifecycle が別 class の設定観測と交差しないよう、BMS playlist 4 fixtureとPlayback fixtureのclass-level serializationを維持する。

Functional の専用 shard は ownership と performance の境界である。`settings-edit-foreground-classwide` は `SettingsForegroundInteractionTests` と non-foregroundの `SettingDialogEditCompletionTests` だけを 1-worker の `ClassLevel` scopeで実行する。foreground interactionを保証する7 method（`SettingsWindowPresentationTests`から移動した4 methodと `SettingDialogEditCompletionTests`から移動した3 method）は、このrouteでだけ実行する。`settings-window-nonactivating-classwide` は non-foregroundの `SettingsWindowPresentationTests`だけを別の1-worker / `ClassLevel` hostで実行し、foreground fixtureを含めない。`settings-state-classwide` は process-local な application、settings、WPF dispatcher、resource、presentation stateを共有する次の14 classを 1-workerの `ClassLevel` scopeで実行する: `ApplicationCompositionTests`、`ApplicationSettingsLifecycleTests`、`ApplicationUiSchedulerBoundaryTests`、`BeatorajaBmtOptionsSnapshotTests`、`BmsLibraryOptionsSnapshotTests`、`CustomFolderOutputSettingsSnapshotTests`、`MainWindowViewSettingsBoundaryTests`、`PlayerSettingsGatewayTests`、`PlaylistUrlCompletionOptionsSnapshotTests`、`ResourceIconContractTests`、`SettingDialogCustomFolderOutputBaseTests`、`SettingDialogOpenCommandTests`、`ShellShutdownWorkflowOwnerTests`、`StartupSettingsSnapshotTests`。3つのsettings testhostは process-localな `Application`、dispatcher、resources、generated resource culture、`Settings.Default` instanceを共有せず、state hostとnonactivating hostへforeground fixtureを追加しない。`process-global-lifecycle` は native BASS lifecycle と NLog の process-global configurationを変更する `BassNativeRuntimeTests`、`NLogWrapperTests` の2 classだけを同じく1-workerの `ClassLevel` scopeで実行する。`feature-process-global-state` は、Functionalの測定済みheadroomとowner-local resource isolationのための16 classの topology groupである。このうち `InstalledOnlyResourceOverwriteValidationTests`、`LibraryFileScanPipelineOwnerTests`、`Lr2PlayHistorySchemaUiTests`、`MainWindowExternalShellTests`、`PlayHistoryReadModelTests` の5 classは既存の class-wide `DoNotParallelize` safety boundaryを保持する。groupの16 classは次のとおりで、全体を1-workerの`ClassLevel`専用testhostで他のFunctional shardから分離する: `AudioContractsTests`、`AudioDeviceTestWorkflowOwnerTests`、`BmsLibraryInstallEstimationServiceTests`、`CatalogMutationOwnerTests`、`ChartListVirtualViewTests`、`InstallDestinationStateOwnerTests`、`InstalledOnlyResourceOverwriteValidationTests`、`LibraryFileScanPipelineOwnerTests`、`Lr2PlayHistorySchemaServiceTests`、`Lr2PlayHistorySchemaUiTests`、`MainWindowExternalShellTests`、`MainWindowViewModelStartupProgressTests`、`PlayHistoryReadModelTests`、`PlaylistOperationNotificationOwnerTests`、`PlaylistUrlAcquisitionOwnershipTests`、`PlaylistUrlCompletionTests`。dedicatedな `method-level-pre-wave` はLR2 early entryの起動後に実行し、完了後に remainingとLR2以外のnamed shard群を並行起動する。専用の `ClassLevel` shardでは class間を直列化し、pre-waveは `MethodLevel`で独立 fixtureのmethodsを処理するが、cross-shard I/O contentionを避けるため専用laneとする。既存の `DoNotParallelize` 属性は任意の Quick filterに対する個別fixtureの safety contractとして維持し、runnerは専用 class-wide shard群とpre-waveのexact membership、worker数、scope、early entry object identity、fanout exclusionをtest起動前に独立したallowlistで検証し、assigned classをremainingから除外する。これら以外の旧 class-wide fixtureは、別の共有resource契約がない限り明示的なcatchall shardを作らずremaining shardで実行する。

remaining の `BmsLibraryStateApplierTests` は、26 caseの論理集合と各 test の GUID付き temporary song DB、filesystem root、UI scheduler / cancellation signalを維持したまま、owner単位の3 `ClassLevel` fixtureへ分ける。`BmsLibraryStateApplierTests` は library initialization / mutation の13 case、`BmsLibraryPackageLifecycleTests` は pending package publication / durable delta の7 case、`BmsLibraryCatalogRelocationTests` は catalog relocation の6 caseを持つ。3 fixtureは既存のremaining processと12-worker `ClassLevel` runsettingsを共有し、新しい named shard/process、worker増加、timeout変更、`DoNotParallelize`、DNP相当のfallbackは追加しない。runnerのremaining exclusion allowlistやlogical test setは変更せず、ClassLevelがfixture間をスケジュール可能にすることで同一owner内の直列性だけを維持する。

lock file を所有する project は `RuntimeIdentifiers=win-x64` を宣言し、C# Dev Kit などが RID を明示せず通常 restore を行った場合も、tracked lock file の base graph と `win-x64` graph を維持する。通常 restore が tracked lock file を変更した場合は dependency graph の不整合として失敗を隠さず調査する。標準検証入口は引き続き、標準スクリプトの locked `win-x64` restore と `--no-restore` build / test route を使う。RID を指定しない素の `dotnet test BeMusicSeeker.sln /p:Configuration=Release` は、locked restore、tracked-file 不変確認、共通の時間予算を迂回するため標準入口ではない。

Functional の名前付き shard は、それぞれ別 testhost/process で ownership を分離するが、性能上は `remaining` shard と並行起動する。exclusive portable-settings lane完了後は `lr2-songdb-sync` だけを early entry としてexact 1回起動し、entryとraw process ownershipを記録してから専用 `method-level-pre-wave` を実行する。pre-wave完了直後にcompleted / exit 0のLR2 entryは一度だけaccountし、runningは同じentryを維持したまま `remaining` と全fanout shardを起動する。settingsの2 routeはpre-wave後のfanoutでそれぞれexact 1回だけ起動し、early entryとして再accountまたは再launchしない。nonzero / canceled / invalid stateはfanout前に失敗させ、LR2 entryと部分起動済みfanoutを同じglobal deadline・result・diagnostics・cleanup setへ合流させる。process start後のentry構築例外もraw identityを失わず、primary failureをcleanup failureで置換しない。runnerは実際に起動へ渡す同一 shard object / arrayをlaunch前validatorへ渡し、early membership、fanout inclusion / exclusion、exact object identity、remaining exclusion、cross-route uniqueness、重複なしを実行用構成そのもので検証する。

runnerは実際に起動へ渡す同一 shard object / arrayを launch前 validatorへ渡し、exact membership、worker数、scope、remaining exclusion、cross-route uniqueness、重複なしを実行用構成そのもので検証する。descriptor-only の自己申告metadataは正本にしない。

## Verification map: critical-tail owner topology

Unit 4b のFunctional topologyは15 launch shard / 14 retained fanout descriptorで構成し、BMS playlist ownerは `playlist-external-custom-folder` と `playlist-persistence-migration` の2 processへ各1 worker / `ClassLevel`でgroup化する。`presentation-workspace` は7 class・3 worker / `ClassLevel`、settingsはforeground edit、non-activating window、stateの3 hostへ分ける。`remaining` は従来どおり12 worker / `ClassLevel`、exclusive portable-settings lane、LR2-only early entry、`method-level-pre-wave` の順序、170秒 process deadline、10秒 cleanup reserveを維持する。

runnerは `New-FunctionalShardPlan` が返す同じ shard objectを `Assert-FunctionalShardConfiguration` と `Invoke-ParallelFunctionalTestShards` の双方へ渡す。validatorは最終 fixture名のexact membership、worker / scope、assigned-classのremaining除外、cross-route uniqueness、旧 `BmsPlaylistUpdateTests` / `PlaylistWorkspaceViewModelTests` FQN不在、foreground 7 methodの dedicated route、LR2-only early 1 entryのidentity、settings 2 routeのfanout exact-onceを検証する。fanoutは検証済みobjectをそのまま使用し、metadata-only allowlistやfallback routeは持たない。

### Unit 4c: LR2-only early and regular-chart owner map

最終Functional planは15 launch shard、14 retained fanout descriptor、14 fanout launch object、1 early descriptorを持つ。`lr2-songdb-sync` はexclusive lane後にexact 1回だけ起動し、pre-wave後に同じentryを再launchまたは二重accountしない。`settings-edit-foreground-classwide` と `settings-window-nonactivating-classwide` はpre-wave後のfanoutへ戻し、各routeを同じ検証済みobjectでexact 1回だけ起動する。foreground interactionは7 methodのallowlistを維持し、non-activating routeはforeground methodを含めない。

旧 `RegularChartListOwnerTests` の76 caseは新しいnamed routeを作らず、既存 `remaining` の12-worker / `ClassLevel` host内で5 owner fixtureへ分ける。`RegularChartNavigationTests` はmethods 1-7、`RegularChartNormalLibraryRefreshTests` は8-10と14-19、`RegularChartFolderRenameTests` は11-13、`RegularChartViewBuildAndOrderingTests` は20-45、`RegularChartCommitAndLifecycleTests` は46-76を所有する。method body、assertion、GUID付きDB / root、task / event completion、failure watchdogは維持し、helperは一つのnarrow supportへ集約する。DNP、新process、mutable shared state、production seamは追加しない。Quickの対象は5 fixtureの76 case、`VerificationRunnerContractTests`、LR2 103 case、settings 142 case、library 243 case、grouped playlist 47 caseである。

runner の redirected process は、`scripts/verification-process-lifecycle.ps1` の共有 bounded lifecycle seam を通る。`Invoke-MonitoredCommand` と Functional shard は、root PID と観測できた descendant の identity だけを所有対象として、process exit、stdout / stderr の完了、cleanup、残留 PID 確認を同じ deadline 内で処理する。Functional の cleanup では、保持済み root handle の launch identity を直前に再検証して root-only termination request を全 shard root へ先に fanout し、その後に一つの絶対 cleanup deadline 内で lineage / descendant 回収を行う。通常の monitored command は成功を含む全経路で一度だけ cleanup transition に入り、transition deadline は `min(now + 5 seconds, phaseDeadline)` とする。Functional の10秒 cleanup reserve は canonical owner が絶対 process / cleanup deadline として一度だけ配分し、entry ごとの deadline reset は行わない。stream fault、cleanup、diagnostic write failure は primary process / orchestration failure を置き換えず、成功 process の cleanup-only failure は成功に隠さない。deadline 到達後は reader close、残留 snapshot、blocking I/O / wait、dispose wait を開始せず、未確認の ownership は明示的な uncertainty として診断する。process 名による global kill と未完了 stream task の無期限同期取得は行わない。

## Verification map: process lifecycle

`VerificationProcessLifecycleTests` は `ProcessIntegration` lane の canonical fixture であり、PowerShell の `verification-process-lifecycle-probe.ps1` を実際の `verification-process-lifecycle.ps1` / `verify-refactor.ps1` caller seamへ接続する。各 test は GUID付き temporary diagnostics directory、probe root / descendant の exact PID・creation identity ledger、primitive event ledgerを所有し、fixture間で process、stream task、artifact pathを共有しない。normal / asymmetric stream completion、lifecycle-local late-fault scope、terminal diagnostic flush、post-start caller exception、staged early entryのexit0 / running / nonzero state・account-once・raw promotion、Functional fan-out の shared cutoffを同じ fixtureへ `extend` して検証する。完了signalは `Task` completion、`ManualResetEventSlim` gate、primitive observer eventであり、probe processの bounded watchdogは失敗検出専用である。新しい lane、shard、`DoNotParallelize`、production lifecycleのtest側コピー、固定sleepは追加しない。

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
- real-window test の既定は `TestWindowActivation.NonActivating` とする。`WindowStartupLocation.Manual`、`ShowInTaskbar=false`、`ShowActivated=false` を表示直前に設定し、現在の virtual screen の外側へ配置したうえで、生成された HWND の矩形が全 monitor 外で foreground HWND でもないことを確認する。scope は `Window` の `Loaded` または早期 `Closing`、追跡した `Popup` の `Opened` で HWND が破棄される前にこの policy を観測し、観測失敗を test body から独立して保持する。開始時の `Application.Windows` と共有 dispatcher thread の HWND だけを baseline とし、明示追跡した popup、owner-depth の深い順の window（`SettingsWindow` は `CloseForOwnerShutdown`）、購読、dispatcher queue、残留 HWND の順で決定的に片付ける。失敗の優先順位は test body、presentation observation、cleanup とし、primary より後の failure は secondary diagnostic に残す。
- foreground input、keyboard focus、hit testing、nested modal activation が保証対象である次の7 method だけは、明示的に `TestWindowActivation.ForegroundInteraction` を指定してよい。
  - `SettingsForegroundInteractionTests.SettingsWindow_NavigationSupportsKeyboardAutomationAndResetsPageScroll`
  - `SettingsForegroundInteractionTests.SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes`
  - `SettingsForegroundInteractionTests.SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes`
  - `SettingsForegroundInteractionTests.SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow`
  - `SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting`
  - `SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected`
  - `SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor`
- `settings-edit-foreground-classwide` includes `SettingsForegroundInteractionTests` and the non-foreground members of `SettingDialogEditCompletionTests` in its dedicated one-worker host; `settings-window-nonactivating-classwide` includes only `SettingsWindowPresentationTests`, and `settings-state-classwide` owns the other exact 14 classes in a separate one-worker host. Their seven foreground-sensitive methods remain explicit `TestWindowActivation.ForegroundInteraction` cases; method単位の別foreground shardは作らない。allowlist の追加、visible / foreground window が必要な受入検証、OS focus policy に依存する操作は通常の Functional へ安易に追加せず、まず Full / release acceptance の責務として分離できるか検討する。待機時間の延長や固定 sleep を foreground 成功条件にしない。
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
