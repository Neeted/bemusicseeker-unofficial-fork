# 待機経路の応答性改善

## Goal / Context

2026-09-06 のレビュー検討を受け、ユーザーが承認した限定範囲を実装単位ごとに検証・静的レビュー・commit する。開始 revision は `d18df91adb70f548cde8b3b229709153764e8727`、開始時 worktree は clean。

## 確定 decision / Constraints

- Everything と updater は変更しない（updater はユーザーが明示的に除外）。
- 既存の終了時 tracked idle / drain と音声 resource lifetime を維持し、停止しない worker の切り離し・強制終了・再生再利用を追加しない。
- SQLite の timeout / retry 予算、共通 gateway、lock-owner 台帳を変更しない。UI 応答性のために競合 mutation を並行化しない。
- IR 取得失敗を空の正常 score として扱わず、DB と既存 score を保持する。prefetch 失敗後に同じ request の即時再取得を行わない。
- IR は既存30秒を要求開始から本文受信完了までの予算に用い、shutdown cancellation を伝播する。新しい設定項目・persistent state は追加しない。
- IR timeout / unavailable は既存結果型と background status および通常ログで区別して観測する。新しい modal dialog は追加しない。
- DB 復元成功後の UI 反映失敗は、復元済み DB を保持して error を通知する。自動再試行・DB 巻き戻しは追加しない（ユーザー回答で確定）。UI 反映前の rejection なら既存 collection を保持し、部分適用を無理に補償する新しい仕組みは追加しない。
- ここで旧DBを保持する「DB失敗」は復元 transaction のcommit以前に限定する。commit後のheader readは画面反映の準備に含め、失敗しても復元DBを保持してerrorとする。既存transactionへ表示準備readを追加するための拡張はしない。
- 変更した API の契約コメントと現行 feature spec を日本語で更新する。新しい UI 文言が必要なら全言語 resource parity を揃える。
- 各 unit は直列実装。worker は commit せず root が unit ごとに commit する。push / version / release 操作は対象外。

## Draft units / ownership / Done when

### U1: IR 取得の期限と shutdown cancellation

- production route: startup score load → IR prefetch → deferred ranking refresh → score update。ランキング worker の Running は shutdown drain 対象。
- impact: HTTP ヘッダー後の本文停止でも取得を終端でき、shutdown で待機を解消する。失敗・cancel で score/DBを変更しない。
- root cause evidence: `AppHttpClient.GetBytes` は `ResponseHeadersRead` 後の本文を token 無しで読む。IR client はこの同期経路を使用し、prefetch consumer は無期限 Wait、失敗時 null は service の再取得に流れる。
- writable: `BMSLibrary.cs` の IR/shutdown 接続範囲、`BmsLibraryInternal` の IR client/interface/service/result、必要な既存 HTTP API の限定範囲、対応 IR/HTTP/owner tests、`devdocs/spec/data-and-indexes.md` と終了仕様の IR 部分。
- candidate coverage: `BmsLibraryIrServiceTests`（prefetch/digest/既存 score）、`AppHttpClientTests`（HTTP）、startup ranking/shutdown owner の近傍 fixture。独立 oracle は user decision と HttpCompletionOption の公開契約。
- verification: packet 確定後に具体化した filtered Quick、unit 最終 snapshot の Functional、fresh static review。
- replan: 独立 timeout/retry owner や汎用 network 基盤、他機能の HTTP 全面変更が必要になった場合。shutdown と DB mutation の安全境界を保てない場合。

### U2: プレイリスト復元の DB 処理を UI から外す

- production route: SettingsWindow 復元ボタン → PlaylistWorkspaceViewModel.RestorePlaylistAsync → LoadPlaylistDump / ReloadTables。
- impact: lock 取得と DB 復元・read の間 UI を応答可能に保ち、live collection 更新は UI で行う。
- writable: `ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistRestore.cs` と必要な composition、`BMSPlaylist.cs` と既存 playlist persistence/reload owner/repository の復元境界、`PlaylistWorkspacePersistenceCommandTests` と対応 owner tests、`devdocs/spec/playlist-data-and-export-flow.md`。
- constraints: Monitor 取得/DB処理/解放は同一 worker thread の同期 scope。DB lock 保持中の UI 待ち禁止。既存 mutation owner を用い、復元失敗時の DB/live collection保持、通知、export、shutdown の順序を維持する。
- candidate coverage: `PlaylistWorkspacePersistenceCommandTests`。旧 background scheduler 拒否 test は「DBはworker、collection適用はUI」の contract に置換。DB rollback / notification 既存 coverageを維持。
- verification: filtered Quick、unit 最終 snapshot の Functional、fresh static review。
- replan: commit後のUI適用失敗の扱いが未決、既存ownerで競合 mutation を閉じられない、復元以外のreload全面整理が必要になる場合。

U2 の root 確定設計（追加調査後）:

- 既存 `TryBeginReload` は通常 write/remove を排他にしないため、その寿命を延ばすだけの実装は禁止。既存 `PlaylistAggregatePersistenceOwner` 内で `reloadActive` を通常 reload / restore を区別する短命状態へ置き換え、通常 reload の挙動は維持する。
- restore admission は registration / hydration / collection reload / 個別 reload reservation がある場合 DB 更新前に明示 failure。restore 中の同 surface の write/remove/registration/reload も既存 failure 経路へ reject する。新しい queue/retry は追加しない。
- `BMSPlaylist` の非同期 restore operation が admission → DB/ヘッダー読込 → UI apply → 成功後処理を所有する。既存 snapshot/read/apply/post 処理は必要部分だけ private helper 化し、workspace は file read と通知 receipt、成功後 export を担当する。
- DB 復元中は collection lock / owner synchronization lock を保持し続けない。短命 reservation が排他を保証し、取得/commit 後の owner fact 更新だけ短い lock で行う。
- SQLite Monitor と connection は同じ worker scope で解放してから UI apply completion を待つ。restore 自身の persistence generation 更新後の fact を既存 apply 検証へ渡す。
- UI apply 完了まで restore admission を保持し、すべての終端経路で解放する。失敗なら success通知/export/shutdownを発行しない。DB commit後失敗はユーザー承認どおりDB保持+error。
- 同期 `LoadPlaylistDump` の利用は migration 等の既存 public contract を調べて維持し、production の復元 command からの旧同期経路を退役する。

### U3: 終了時の player drain を UI から外す

- production route: MainWindow.OnClosing → shutdown preparation → CompleteTerminalShutdown → PlaybackPanel.CloseProcess → internal player Dispose → playTask.Wait。
- impact: player 停止・解放が未完了でも UI dispatcher は動作できる。完了前に audio runtime 解放や application shutdown を行わない。
- writable: `ShellShutdownWorkflowOwner.cs`、必要な `MainWindow.cs` / `MainWindowViewModel.cs` / playback terminal API、`ShellShutdownWorkflowOwnerTests` と対応 playback tests、`devdocs/spec/application-shutdown.md` / 必要な audio specification。
- constraints: terminal cleanup 一度だけ、設定保存とUI依存操作のthread affinity、player完了→audio解放→application shutdown を保持。通常Stop経路やdecoder隔離には拡張しない。
- candidate coverage: `ShellShutdownWorkflowOwnerTests`（一度だけ/順序）、`PlaybackPanelViewModelTests`（terminal close）。
- verification: filtered Quick、unit 最終 snapshot の Functional、fresh static review。
- replan: UIから切り離せないnative制約、decoder/runtime隔離が必要になる場合。

U3 の root 確定設計:

- terminal cleanup の既存一度だけ状態を進行中 completion Task で表し、`CompleteTerminalShutdownAsync` として多重要求が同じ完了を待てるようにする。既存 bool と重複する状態は残さない。同期 wrapper を互換経路として新設しない。
- player close/drain は worker 上で行って await し、その完了後に UI context の設定保存/通知、音声 runtime 解放、既存残余cleanupへ進む。通常Stop、再生開始、IBMSPlayer全体のasync化は不要。
- `MainWindow.ApplyTerminalShutdownAsync` が completion を await してから terminal window close を認可し application shutdown を要求する。`IsCloseAllowed` が準備完了を表していても、terminal cleanup 未完了の window close は通過させない。
- `MainWindow.OnClosing` と `Closed` の同期 terminal cleanup 再呼出しは退役し、通常/更新/多重closeの全実入口を同じasync completionへ流す。window state capture と設定保存の順序は維持する。
- owner lock を保持して callback/await を行わない。player完了前のaudio解放・app終了は禁止。既存terminal failureのログと継続方針は維持し、native hang timeout/強制解放を導入しない。
- 既存external player closeは通常のplayer交換でもworker経由で使われ、window placement captureは既存hostを通す。UI保存/通知をworkerへ丸ごと移さない。

## Test-design gate

全3 unit は observable threading / failure / cancellation の assertion semantics を変更するため、実装前に test-contract-designer の独立 packet を root が承認・凍結する。packet と coverage ledger は本計画に保存する。expected を現行実装・既存 expected・翻訳文言から作らない。red/head-pass を原則とし、構造上 base 実行不可の contract は packet 指定の targeted negative control を使う。

## 承認済み packet: U1-IR-RESPONSIVENESS

2026-09-06、`ir_test_contract` の oracle-first / repository-fit packet を root が承認。change class は bugfix、base は開始 revision。authority は本計画の user/root decision、data-and-indexes の score/digest 契約、application-shutdown の実 idle drain、[HttpCompletionOption Remarks](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)。Phase A では production body/既存 expected/翻訳/output を参照せず、Phase B は配置と到達性だけを確認。exactness例外なし。

共通入口は LR2 source / 存在する score.db / 非ゼロ player ID / 非空ローカルscore / IR取得有効。同じplayer/DBのstartup requestを対象とし、外部writerや設定変更raceは作らない。

| Contract ID | Observable invariant | production ingress / seam | wrong implementation / evidence |
| --- | --- | --- | --- |
| U1-H1 | request開始から本文完了まで単一30秒予算、header後に再開始しない。未受信本文を成功にしない | startup → IR client → productionで使用するHTTP GET | headerだけtimeout、bodyで予算再開始、空文字成功。短い既存Create予算のphase-controlled loopbackでbase red/head pass、再計時mutant |
| U1-H2 | shutdown cancelが本文待機を終端し、timeoutとtyped区別 | RequestShutdown → IR取得 → HTTP token | body token無視、すべてtimeout。body-ready後cancelとservice分類/owner接続 |
| U1-F1 | failed prefetchの追加取得0回、typed unavailable、既存ir_score/digest metadata/live score不変、startup観測結果で失敗識別 | InitializeStartup → prefetch → deferred ranking → consume → service/gateway | failureをnullに戻して再取得、空score適用、成功としてだけ報告。非空seedのservice base red + startup mutant |
| U1-S1 | 本文待機中shutdownならその結果をDB/scoreへ適用せず、HTTPとrankingが終端してdrainできる | startup → in-flight IR → RequestShutdown → HasShutdownBlockingWork | consumerだけ終端/HTTP放置、Runningだけ下げる、cancel後適用。client開始/cancel/完了とranking完了signal、DB再読 |
| U1-C1 | 成功prefetchは再取得せず適用。score実体が同じでlastupdateのみ差ならdigest同じ | startup/service → gateway/live score | 全unavailable、成功再取得、lastupdateをdigestへ混入。既存成功/digest coverage再利用 |

型/enum名、ログ文言/回数、metric、内部順序は固定しない。cancelとtimeout同時発火の優先順位は固定しない。S1はHTTP待機中のshutdownに限定し、commit途中rollbackや非協力clientの回復を要求しない。別の明示requestまで再取得を永久禁止しない。

| Contract | coverage / decision | resource / lane / completion | retirement |
| --- | --- | --- | --- |
| H1/H2 | AppHttpClientTests extend | 固有loopback port/socket/client、Http/Functional。accepted/headers flushed/request/server Task、timeout/cleanup watchdogのみ | 既存SingleRequestHttpServerを限定拡張し、変更するhelperの無期限cleanupも退役 |
| F1/H2/C1 | BmsLibraryIrServiceTests extend | TempIrEnvironment固有DB、Functional/BmsLibrary、対象Task/token signal | 成功prefetch/digest既存coverage維持、重複追加なし |
| F1/S1/H2 wiring | BmsLibraryIrStartupTests new | startup lifecycle専用。TestBmsFactory、captured options、missing bridge、property/report/client signal、Functional/BmsLibrary | private/reflectionによるprefetch操作禁止、inline client生成退役 |

root追加承認: 既存 `IBmsLibraryIrClient` をBMSLibrary internal constructorの明示依存へ移し、通常compositionは従来clientを供給。TestBmsFactory.cs と必要な実compositionをU1所有へ追加。test-only public API / broad hostは禁止。

negative controls: H1は既存GET/短いCreate予算で本文停止をbase red、headではheader後再計時mutantも検出。H2はbody token無視/誤分類。F1はfailed prefetch後成功するclientで再取得とDB不変を観測、startup側はunavailable→null mutant。S1はshutdown cancellation接続削除またはcancel後適用。新signature/constructor依存でbase compile不可ならその理由を記録してhead mutant使用（compile failureをredとしない）。normal完了はsignal駆動、HTTP timeoutの契約以外に固定sleepを入れず、finallyでsocket/task/shutdown drainを回収する。

H2 mechanics amendment（root承認）: server headers送信だけではclientの本文開始を保証しない。H1の実HTTP本文期限テストでbody token無視mutantを検出し、serviceのtyped分類とstartup S1のshutdown伝播を組み合わせてH2を検証する。補助HTTP cancel testはresponse pending中の外部cancelのみを主張する。EventSource/TPL listenerはsetup依存を増やすため不採用・全撤去。observable contractは変更しない。

U1 final writable paths: BMSLibrary.cs（IR/shutdown/constructor）、BmsLibraryInternal/{BmsLibraryIrClient,IBmsLibraryIrClient,BmsLibraryIrService,IrScorePrefetchResult,IrScoreTableUpdateResult}.cs、AppHttpClient.cs のGET期限/token範囲、実composition必要箇所、Tests/{AppHttpClientTests,BmsLibraryIrServiceTests,BmsLibraryIrStartupTests,TestBmsFactory}.cs、data-and-indexes.md と application-shutdown.md のIR部分。新しいtyped結果は同じIR結果ファイルに収められるなら別基盤不要。所有外へ必要変更が出たらrootへ返す。

U1 Quick filter: `FullyQualifiedName~AppHttpClientTests|FullyQualifiedName~BmsLibraryIrServiceTests|FullyQualifiedName~BmsLibraryIrStartupTests`。workerはred/negative-control/filtered Quickまで、rootがFunctional→frozen fresh review→commitを担当。test/result/specの旧route対応と通常NLogWrapper診断をhandoff。canonical新fixtureのVerification mapはworkerがdata-and-indexesへ更新。

## 承認済み packet: U2-PLAYLIST-RESTORE-UI-RESPONSIVENESS

`remaining_test_contracts` の oracle-first packet をroot承認。bugfix、baseは開始revision。authorityはuser/root決定、playlist-data-and-export-flowの永続化/外部同期/schema契約、concurrency §5.1–5.5/13.1。Phase Aはauthorityのみ、Phase Bは配置/到達性のみ。R3/R4のcommit境界はroot追加decisionで明確化。exactness例外なし。異なる旧DB/liveと小さい有効backupを使い、実registration/hydration publication/reload/write/removeで競合を作る。private flag操作や外部writerで到達性を作らない。

| ID | 必須 observable invariant / route | wrong variant / evidence |
| --- | --- | --- |
| U2-R1 | SettingsWindow→workspace→BMSPlaylist async restore→owner/repository→UI。DB lock/SQL/header readはUI外、collectionはUI、Monitor/connection解放後UI completionを待つ。DB待機中dispatcher markerを処理可能 | file readだけworker、DBをUI、UIをworker、DB lock中UI待ち、postだけ成功。別workerで実process lockを保持してUI marker確認、apply受付/実行/completionを分離しDB lock再取得とUI eventを観測 |
| U2-R2 | 先行registration/hydration publication/collection reload/個別reload reservation中のrestoreはDB前reject。restore中も同surface mutationをUI適用完了までreject。全terminalで解放し次の独立操作を受理 | reload flag延長のみ、commit直後に解放、failure漏れ、silent no-op。実pending publication/reloadによる前後方向matrix、少なくともrestore中writeと個別reload中restoreを区別 |
| U2-R3 | file read/復元transaction commit前failureはDB/header/entry/course/live保持、error伝播、success/export/settings close/shutdownなし | partial transaction残留、failure握潰し、finallyでsuccess。非空旧dataと途中で失敗するdump、別connection再読、実SettingsWindow operation gate |
| U2-R4 | commit後header read/UI dispatch/apply failureは復元DB保持+error、success/export/shutdownなし、retry/rollbackなし。apply前rejectionなら旧live保持、部分apply補償は非保証 | DB巻戻し、UI失敗成功扱い、再post、read失敗後shutdown。有効dump commit後scheduler completion reject/cancel/faultを制御、DB/error/effect観測。header read独立failureはproduction seamがある範囲のみ |
| U2-R5 | backup論理dataがDB/live headersへ反映されUI completion後にsuccess effects。通常reload/同期LoadPlaylistDump migration互換維持、entriesのdeferred hydration維持 | 古いgenerationで自己reject、常時failure、通常reload排他変更、migration破壊。既存成功/通常reload/legacydump coverageとpending apply中effect不在 |

許容: worker ID/helper/scheduler名/所要時間/例外文言/通知集約/generation数値/SQL書式は固定しない。queue済み/read中をactive mutation reservationと同一視しない。部分UI適用の完全復旧は要求しない。

coverage: R1/R4/R5はPlaylistWorkspacePersistenceCommandTestsをextend/replace、共通PlaylistWorkspaceFixtureFactory.csのscheduler接続mechanicsを限定変更可。旧RejectsBackgroundSchedulerBeforeDatabaseApplyをworkerDB/UIapplyに置換。R2はBmsPlaylistPersistenceLifecycleTests/BmsPlaylistExternalReloadTestsと必要なBmsPlaylistMigrationAndRegistrationTestsをextend（既存shared Settings/DNP維持）。R3/R4はSettingsWindowPresentationTestsのRestoreFailureDoesNotAuthorizeCloseOrShutdownも実failure phaseへ適合。同期schema互換はPlaylistSchemaMigrationTestsを維持。new重複fixture不要。

固有DB/file、TestUiDispatcherHost/AwaitTaskOnDispatcher、必要なTestWindowPresentationScopeを用いる。process-wide LR2 lockのtestは所有とfinally解放を明記。operation Task/scheduler accepted-executed-completion/property eventを正常完了signalとし、watchdogは失敗回収のみ。SQL/header readの観測口不足はprivate callbackで埋めずrootへ返す。

red/negative controls: 旧入口で可能な応答性caseはbase red/head pass、新signatureでcompile不可なら理由とhead mutant。R1 UI実行/await欠落/lock保持、R2 guard欠落/早期解放/解放漏れ、R3 partial失敗成功/後続effect、R4 fault握潰し/repost/rollback、R5古いpersistence factを候補とし、既存coverageと重複を避けて実際に区別する。compile/setup失敗はredではない。

U2 final ownership: BMSPlaylist.cs、PlaylistAggregatePersistenceOwner.cs、必要なPlaylistPersistenceRepository.csの復元境界、ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistRestore.csと限定composition、上記6fixtureとPlaylistWorkspaceFixtureFactory.cs、playlist-data-and-export-flow.md。通常reload/同期API互換のため必要最小helper抽出のみ。新UI文言が不可避ならrootへresource scope追加を求め、固定文言を増やさない。

Quick: `FullyQualifiedName~PlaylistWorkspacePersistenceCommandTests|FullyQualifiedName~BmsPlaylistPersistenceLifecycleTests|FullyQualifiedName~BmsPlaylistExternalReloadTests|FullyQualifiedName~BmsPlaylistMigrationAndRegistrationTests|FullyQualifiedName~PlaylistSchemaMigrationTests|FullyQualifiedName~SettingsWindow_RestoreFailureDoesNotAuthorizeCloseOrShutdown`。workerは標準Quick、rootはunit最終Functional→fresh review→commit。

## 承認済み packet: U3-PLAYER-DRAIN-UI-RESPONSIVENESS

`remaining_test_contracts` のoracle-first packetをroot承認。bugfix、baseは開始revision。authorityはroot決定、application-shutdownの終了入口/drain、audio-runtime-phase1 §2/2.1。Phase Aはauthorityのみ（終了specはU1変更前）、Phase Bはroute/fixture確認のみ。exactness例外なし。正常composition/owner dispatcher/実ReplacePlayerAsyncで接続したplayerを入口とし、native hangの発生やprivate注入は要求しない。

| ID | 必須 observable invariant / route | wrong variant / evidence |
| --- | --- | --- |
| U3-T1 | 実MainWindow.Close→OnClosing→preparation→async terminal→PlaybackPanel→接続IBMSPlayer。player close未完了でもdispatcher markerを処理し、save/audio/app/terminal Window close未実行 | UIで同期close、fire-and-forget、早期effects。gated player entered/release/completedとdispatcher marker、未実行effectsを観測 |
| U3-T2 | capture→player close完了→settings save→audio解放→app shutdown。capture/close/save/app requestは各一度、UI capture/save/notificationはUI。既存failureログ/継続方針維持 | terminal全体worker、capture前save、player最終placement未保存、再入再保存。独立fixture値/実settings session/lifetime port、既存save failureをasyncへ適合 |
| U3-T3 | 通常/更新準備後/再入closeは同一owner Taskへ合流し全callerが完了を待つ。IsCloseAllowedだけでwindowを閉じずOnClosing/Closed同期cleanup退役 | boolのみで二回目CompletedTask、IsCloseAllowed早期close、Closed重複cleanup。ownerTask identityと実非表示MainWindow再入Close/Closed/lifetime |

許容: worker ID/所要時間/helper/列挙外cleanup順序/log文言/内部状態は固定しない。MainWindow wrapper Task identityは固定しない。同期nativeのtimeout/detach/force解放やupdater起動変更は対象外。

coverage: ShellShutdownWorkflowOwnerTestsをextend/replace（既存Settings/shared UI/DNP/serial-state-a）、MainWindowViewHostTestsの実非表示MainWindow closingケースをextend（既存serial-state-b/shared Application）、PlaybackPanelViewModelTestsはterminal API変更時だけextend、それ以外既存維持。新visible/process fixture不要。audio runtime状態の観測は既存native owner seamに限定し、物理device/実hangを要求しない。source/reflectionでfake native stateを作らない。

red/negative controls: 実Window.Close応答性は可能ならbase red/head pass。新async Task identityはbase compile不可を記録しhead mutant。UIでclose、await欠落/早期audio、save worker/逆順、二度実行、二回目CompletedTask、IsCloseAllowed迂回を候補とし、最小組合せで区別する。coordinatorはdispatcher外でfinally gate解放、player/terminalTask/Window cleanupを回収する。

U3 final ownership: ShellShutdownWorkflowOwner.cs、MainWindow.cs、必要なMainWindowViewModel.cs/PlaybackPanelViewModel.csのterminal接続、ShellShutdownWorkflowOwnerTests/MainWindowViewHostTests/必要なPlaybackPanelViewModelTests、application-shutdown.md/必要なaudio-runtime-phase1.md。同期terminal wrapperや新recovery lifecycleを追加しない。

Quick: `FullyQualifiedName~ShellShutdownWorkflowOwnerTests|FullyQualifiedName~MainWindowViewHostTests|FullyQualifiedName~PlaybackPanelViewModelTests`。workerは標準Quick、rootは最終Functional→fresh review→commit。

## 実施状況

- 計画点検: `wait_plan_check` 完了。U2 の DB 成功後 UI 反映失敗についてユーザー回答を得て解決。全3 unit は packet 必須、直列実装。review は検証後・commit 前に行う。
- test packet: U1/U2/U3 承認済み。
- U1: 実装完了。Quick `tests-quick-20260906-020345` 59/59 pass、test 3.9385秒、build/test 34秒、fingerprint不変。base red `013906` はH1/F1の2件が意図したfailure（4.6817秒）。mutant `015306` はF1/H2分類、`015751` はS1接続、`020202` はH1本文token無視（4.4529秒）を検出。prefixは同日 `artifacts/verification/tests-quick-20260906-`。compile/setup失敗、編集中fingerprint不一致run、旧cleanup失敗runは証拠から除外。Functional/静的review/commitは未完了。
- U1 Functional `tests-functional-20260906-020720`: IR testはpass、remaining hostの既存 `StartupPostInitializationIdleRouteEnrollsWarmupOnceAfterPredecessors(False)` がline575のprogress表示assertionで1件failure。timeoutではない。対象の標準Quick `tests-quick-20260906-021219` は2/2pass（3.0760秒）、snapshot fingerprint不変。rootの経路確認では当該testのempty-score fixtureからIR取得は開始しない。因果関係は未確認、全体greenとは報告せず証拠保持し、後続unitの最終Functionalでも再発を確認する。待機延長・runner並列度変更・対象testの削除は行わない。
- U2 / U3: 未着手。

- U1 静的確認: 新規 repo-static-review は thread limit で起動不可、close API なし。既存 designer は固定 role により代替不可。workflow §9 に従い root が実装と検証を止め、production diff/caller/consumer/failure/関連 test/spec/packet を逐次静的確認し、blocking finding なし。独立 reviewer は未実施。Functional の既存 progress test failure は上記のまま未解決として保持。
