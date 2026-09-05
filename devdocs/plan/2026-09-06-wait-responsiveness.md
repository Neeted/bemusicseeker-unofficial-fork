# 待機経路の応答性改善（完了記録）

U1（4599b0d1）、U2（9010aed3）、U3（本記録の最終更新と同じcommit）の実装・検証・静的確認を完了。最終Functionalは4502成功/13skip/失敗0、188.8秒。updater/Everything、SQLiteの待機予算、native workerの強制切離しは対象外。以下の実施履歴には途中失敗と未実施の独立検証も保持する。

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

## 実装単位 / ownership / Done when

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

## 実施履歴（各時点の記録）

- 計画点検: `wait_plan_check` 完了。U2 の DB 成功後 UI 反映失敗についてユーザー回答を得て解決。全3 unit は packet 必須、直列実装。review は検証後・commit 前に行う。
- test packet: U1/U2/U3 承認済み。
- U1: 実装完了。Quick `tests-quick-20260906-020345` 59/59 pass、test 3.9385秒、build/test 34秒、fingerprint不変。base red `013906` はH1/F1の2件が意図したfailure（4.6817秒）。mutant `015306` はF1/H2分類、`015751` はS1接続、`020202` はH1本文token無視（4.4529秒）を検出。prefixは同日 `artifacts/verification/tests-quick-20260906-`。compile/setup失敗、編集中fingerprint不一致run、旧cleanup失敗runは証拠から除外。Functional/静的review/commitは未完了。
- U1 Functional `tests-functional-20260906-020720`: IR testはpass、remaining hostの既存 `StartupPostInitializationIdleRouteEnrollsWarmupOnceAfterPredecessors(False)` がline575のprogress表示assertionで1件failure。timeoutではない。対象の標準Quick `tests-quick-20260906-021219` は2/2pass（3.0760秒）、snapshot fingerprint不変。rootの経路確認では当該testのempty-score fixtureからIR取得は開始しない。因果関係は未確認、全体greenとは報告せず証拠保持し、後続unitの最終Functionalでも再発を確認する。待機延長・runner並列度変更・対象testの削除は行わない。
- U2 / U3: 未着手。

- U1 静的確認: 新規 repo-static-review は thread limit で起動不可、close API なし。既存 designer は固定 role により代替不可。workflow §9 に従い root が実装と検証を止め、production diff/caller/consumer/failure/関連 test/spec/packet を逐次静的確認し、blocking finding なし。独立 reviewer は未実施。Functional の既存 progress test failure は上記のまま未解決として保持。

- U2 R2 mechanics 補足: hydration publication の競合拒否は既存 TryBeginHydrationPublish lease（collection writer guardでreceipt snapshot/current確認を保護）の保有中に限定する。PlaylistEntriesHydrationOwner と BMSPlaylist は通知 callback 前に明示的にleaseを解放しているため、通知中のrestore拒否を新契約にしない。event内assertionは不適切な観測として撤回し、既存generation/current receipt契約を維持する。実lease中をproduction seamで停止できない場合は独立test未実証と明記し、private state/new callbackで補わない。R2主要実証はregistration/個別reload/restore中writeとterminal解放。

- U1 commit: 4599b0d1。U2実装完了。最終Quick025616は111/111pass（test14.0100秒、build/test38.1秒、fingerprint不変）。base red022946、単独mutant025236（await欠落2.5539秒）/025329（UI DB待機7.5900秒）/025443（個別reload guard欠落2.6625秒）は有効。複合mutant024503は297秒timeout、cleanup補強と単独再検証を行いred証拠から除外。fixture setup失敗run023614/024015も除外。hydration snapshot lease途中停止とcommit後header read独立failure injectionは既存production seamがなく未実証。Functional/review/commitへ進む。

- U2 Functional025805はremaining hostで2failure、他unit関連はpass、fingerprint不変。新R1のprocess-wide Monitor再取得を他fixtureが妨害したため当該methodのみDNPで隔離（assertion/予算変更なし）、Quick030218 1/1pass（2.7663秒）。もう1件は既存PlaylistViewPipelineTests.CommitPlaylistRow_ExternalSyncEntryDoesNotBackfillHashesFromResolvedChartの未初期化Settings instanceが共有bin/config/user.configを読み込む際のPortableSettingsException/IOException（別processがfile使用中）。今回変更前からあるfixture/composition routeで、rootは具体的stackと共有resourceを確認し、製品コード変更との因果なしとして対象外で記録。U1時のprogress test failureは今回再発なし。全体greenとはせず、U3最終Functionalで再確認する。

- U2 fresh static review restore_static_review: P1、RestorePlaylistDumpAsyncのUI適用後BeginPlaylistInitializationに続く出力先同期failureでreadiness未終端となり、後続external importが永久pending。実root output同名file衝突等で到達、旧ReloadTablesのFailRequiredPlaylistReadiness互換が必要。rootはfindingを採用し、readiness開始後の既存後処理をcatchしてFailRequiredPlaylistReadinessへ渡し元例外を再throwする修正を承認。commit済DB/liveを戻さず、precommit/UI適用前failureに新たなreadiness変更を加えない。追加delta packetを独立designerへ依頼。

## 承認済みU2 review delta packet: U2-R5-F1

restore_failure_contract のoracle-first/R5 deltaをroot承認。authorityはuserのcommit後DB保持+error、復元所有境界、root決定の既存readiness failure終端維持。Phase Aはauthorityのみ、Phase Bはseam/placementのみ。入口は正常LR2 configとroot playlistを含むvalid backup、固有output directoryと同名の通常fileによる実I/O failure。restore commit/UI apply/readiness開始後に元I/O例外を返し、WaitForRequiredPlaylistReadinessAsyncも同じ元例外で終端する。DB/liveは復元後状態を保持、成功/cancel/pending/retry/rollbackへ変換しない。後続URIは既存admissionでreject可、consumer接続はstatic traceと既存StartupReadiness_FailedInitializationTerminalizesPendingImportDrainを再利用。

coverage: BmsPlaylistPersistenceLifecycleTestsに1case追加、既存class DNP/serial-state-a、GUID DB/backup/output/config/既存UI host。正常完了はrestore/readiness Task、未終端検出のみ有限watchdog、finallyはRequestShutdownでpending waiterを回収。private Begin/Fail/state注入禁止。旧U2 catch欠落snapshotでred、修正後pass（開始baseには新APIがなくcompile不可なので現U2をnegative controlとする）。元例外identityは明示failure契約、文言/stack/translation/内部構造は固定しない。ownership追加不要。Quickは `FullyQualifiedName~BmsPlaylistPersistenceLifecycleTests|FullyQualifiedName~StartupLibraryInitializationWorkflowOwnerTests.StartupReadiness_FailedInitializationTerminalizesPendingImportDrain`。修正はreadiness開始後の後処理に既存failure終端catchを適用、元例外を再throw。rootはfocused検証後fresh fix-delta review、統合laneは最終U3 snapshotで再実行する。

- U2 review修正: R5-F1 red031235は実file衝突後のreadiness未終端を5.7991秒で検出、finally shutdownで回収。readiness開始後の同期後処理に既存FailRequiredPlaylistReadiness+元例外rethrowを追加。Quick031345 26/26pass（6.6797秒、build/test65.9秒、fingerprint不変）。新1caseでDB/live保持と元IOException identityを確認、元URI admission既存coverage再利用。fresh fix-delta reviewへ渡す。

- U2 fresh fix-delta review restore_fix_review: blocking findingなし、前回P1解消。readiness consumer/packet/red/Quick/仕様整合確認済み。最終FunctionalはU3後に実施する。

- U2 commit: 9010aed3。U3実装完了。追加ownershipはApplicationCompositionTests.csの既存terminal caller1methodのみasync適合、assertion semantics維持。旧同期caller退役、PlaybackPanel APIは変更不要。実MainWindow通常/更新準備済2caseでUI marker・再入Close・owner sharedTask・settings/UI affinity・audio受付順序を確認。
- U3最終Quick033128:77/77pass（test15.8827秒、build/test47.5秒）、fingerprint72B0978B15721C2B743DED11D6EF5902CDD96AB1EB1106719685901F2696D045不変、whitespace/UTF8LF確認。base red031943 UI marker停止8.5787秒。単独mutant032652二回目CompletedTask4.0462秒、032918 IsCloseAllowed迂回8.5150秒、033019 player await欠落で早期audio終了3.7900秒を検出し復旧済み。compile failure032115/032206、旧testのcompletion待ち不足032420、whitespace failure032557、旧cleanup問題032751はgreen/red証拠から除外。cleanup補強後owner/native/Window/queued markerを回収。最終Functional→fresh reviewへ進む。
- 共有user.configの先行failure補足: 読み込みの実stackは確認したが、競合writerそのものは未特定。直接configを書き換えるPlayerPanelStateSettingsCompatibilityTestsはportable専用hostで先行実行されるため、そのfixtureを今回の原因と断定しない。製品diffへの因果未確認として結果を保持する。

- U3 Functional033304: portable設定互換testの共有config原子的置換がIOExceptionで失敗。testhost残留なし、ReadOnlyなし、排他read可能を確認。対象Quick033407は2/2pass（2.7664秒）、同code/snapshotの診断後Functional033445ではportable成功。その全体runはserial-state-bで55件のmanaged Window残留failure、原因は複数既存Window helperが準備Taskだけ待ってCloseを完了扱いしていたU3追従漏れと特定。時限延長やassertion削除なし。
- U3追加ownership（同invariantのcleanup mechanicsのみ）: MainWindowTreePresentationWpfTests / MainWindowPackageMaintenanceWpfTests / MainWindowProgressStatusBarWpfTests / MainWindowPlaylistWorkspaceWpfTests の既存Window helper/fixture。実Window.Close→composed terminal lifetime→実Closed完了待ちへ適合。VM-only preparation callerは維持、製品変更なし。共有のinert lifetime adapterをTree helperで再利用し、Playlistは既存RecordingApplicationLifetimeを使用。
- 追従後Quick034230:173/173pass（test69.0573秒、build/test82.8秒）、fingerprint18F7995D1C7B17B95B23CB0BA4BBA0FB93086CB68865FAF96072DAEB7B4F0193不変。影響9fixtureとU3既存filterを検証し、UTF8LF/whitespace成功。最終Functionalへ進む。

- Functional034446:serial-state-bにMainWindowExternalShellTestsの同型cleanup漏れ2failure、他完了hostはpass（remaining完了前にrunner回収）。新規Window生成/RequestWindowCloseAsyncを全Testsで検索し、名称Wpfだけの検索で漏れたcleanupを閉じた。追加ownershipはMainWindowExternalShellTestsのWindow後片付け2case、SettingsWindowPresentationTestsのCloseMainWindowThroughShutdownWorkflow/helperと4callerの明示lifetime接続・Closed回収。通常assertion/U2復元契約は維持、VM-only呼出しは変更なし。専用DispatcherFrameを既存AwaitTaskOnDispatcherへ退役。
- 全MainWindow構築は14箇所/7fileを確認。External2、Settings4、ViewHost4、Tree/Package/Progress/Playlist各1、継承/Activator生成なし。残るRequestWindowCloseAsyncはowner-only/VM-only/window-null分岐/実Close後観測のみ。Quick035055は34/34pass（9.1524秒、build/test21.6秒）、fingerprintBAA507E30F4AFD572BBBF989634CA01A5232975DC0C5966712A7BEDEF152EDBC不変、UTF8LF/whitespace成功。再度最終Functionalへ進む。

- 最終Functional035224成功: 全6host合計4500pass/13skip/0failure、test execution185.7秒（retained ExitTime基準、300秒budget内、180秒reporting target超過）。fingerprint67B2D4B113C769F36531406C7E176E43D71EDC247E368E5E229D94FF045821D2不変。先行progress failure/config file競合/window残留は再発なし。先行failed runを隠さず記録保持、U3 frozen static reviewへ渡す。

## U3 review修正と承認済packet: U3-T4-shutdown-admission

shutdown_static_reviewはP1を報告: terminal worker close待機中の実PlaybackPanel NextCommand→Next/StartAtIndex/TryPlayStartがclose後にplayerを再使用し、外部process再起動/audio終了競合へ到達する。root採用。UI応答性だけから競合再生を許可しない。独立shutdown_admission_contractのoracle-first/PhaseB packetを承認。authorityはU3 player→settings→audio→app、通常Stop/交換互換、concurrency §§2–3/13、root T4。observable gapなし、command非同期完了観測の制約は下記。

確定設計: PlaybackPanelに終了中の受付を表す短命boolを一つ置き、Shell terminalの最初のawait前に明示BeginShutdownで不可逆に閉じ、既存generationで古いstart/exitを失効させる。command CanExecuteと実execution/queuedAction、通常player開始/control入口で閉鎖を検査。終了中の通常入力はnoop拒否し、新failure dialogを出さない。terminalの実closeはworker上で既存workflowGateへ合流して先行workflowを回収し、その後既存playerOperationGateでcloseする。UI上でworkflowGate待ちをせず、ownerlock中にUIcallback待ちを追加しない。通常Stop/通常close/交換で永久閉鎖せず、disk state/session ledger/retry/replay/detach/new recoveryなし。所有追加はPlaybackPanelViewModel.csとPlaybackPanelViewModelTests.cs、既存Shell/ViewHost/specの関連部分のみ。

| ID | invariant / production route | coverage / wrong variant |
| --- | --- | --- |
| U3T4a | 実Window.Close→terminal最初await前受付閉鎖。以後UIcommand/direct入口/登録済exit自動Nextから新start/通常controlの増分0。command availability閉鎖、UImarker、T1-T3順序維持 | MainWindowViewHostTests既存gated通常/更新済2caseをextend、T4前snapshotでred。CanExecuteだけ/受付閉鎖遅延/旧exit受理を検出 |
| U3T4b | 合法2譜面の先行Nextが既存workflow内queue解決で待つとき、実closeは先行workflowを追越さず、受付閉鎖後まだ開始していないstartを実行しない。両Taskjoin後も新startなし | PlaybackPanelViewModelTestsをextend、実MainChartListPlaybackQueueを委譲するGetRow gate wrapper、owned Next/terminal Taskをjoin。workflow合流除去/遅い再入口check除去mutant |
| U3T4c | terminal前の通常Next/StopとStop後Start、既存player replacement互換 | 既存normal navigation/exit/Stop/交換coverage再利用、不足Stop→Startのみextend。通常Stopで永久閉鎖する誤実装を検出 |

元の開始済player呼出しの完了とterminal自身closeは『禁止する新control』から除外。内部FIFO/privategate形状/訳文/内部順序/所要時間は固定しない。UIcommandはfire-and-forgetのためExecute直後のcount0を完了証拠にしない。実WindowはCanExecute/同期拒否を観測、raceはownedTaskでNext/terminalを回収しwiringを静的確認。新test-only完了API不要。既存DNP/sharedWPF/runtime、固有2譜面、既存UIhost、finally全gate/task/Window/native回収。新visible/process/reflection/sourceassert/fixedsleepなし。

Quick: `FullyQualifiedName~MainWindowViewHostTests|FullyQualifiedName~PlaybackPanelViewModelTests|FullyQualifiedName~ShellShutdownWorkflowOwnerTests`。T4前U3snapshotのWindow redを先に取得、新terminalAPIがbaseにないowner caseは理由を記録し単独mutant。rootは修正後Functional/fresh fix-delta reviewを担当。

- T4 fallback: worker新規/旧worker再開がthread limitで失敗したため、workflow §9の逐次代替でrootが実装。独立designer packetは利用済み。最初のred041038は実Windowの通常/更新済2caseで終了中CanExecute=trueを検出（041950ではなく040950はCanExecute API誤用compile failureとして除外）。初回Quick041403は78/78pass、test16.4233秒、build/test86.9秒。
- T4 mechanics amendment: BeginShutdownはUIからsessionGateを取得しない。既存controlはそのlock内で外部playerを呼ぶため、新たなUI待機を作らないようvolatile boolで受付を閉じる。全current observation判定もflagを見るため、ここでのgeneration加算は不要として削除。generation更新と先行control待ちはworker closeの既存境界に残す。close済playerへ遅れて到着する通常stopはplayerOperationGate内のflag確認でterminal closeに委ねる。通常Stopの互換は維持。
- T4b evidence amendment（独立designer確認済み）: queue解決中Next→同期受付閉鎖→queue解放→Next/terminal双方join後の新startなしは決定的に検証。close時のqueue解放済assertionは補助。workflowGate取得試行の既存signalがないため、合流だけを除去するmutantの決定的検出は未立証と明記し、static reviewのlock order確認で補う。新test-only API/private reflection/thread state/fixedsleepを導入しない。

- T4 negative control041733: queue解決から復帰した旧workflowが受付flagを再開する単独mutantをT4bが追加PlayStartとして検出（2.0779秒）。復元後Quick041825はCopy-Itemの旧timestampによりmutant時DLLをincremental buildが再使用し1failure。保存済み正本とのbyte内容一致/flag再開なし/ソースとDLLのtimestampを確認し、File.WriteAllTextで内容同一のソースを再保存。再build Quick041948は78/78pass（test15.9133秒、build/test42.5秒）、fingerprint89E76F96B4E21F5F0B508EFF963C84E5016007E9EAC70B90362163D29D31995A不変。041825はhead品質証拠から除外し失敗記録を保持する。

- T4 Functional042052は専用portable-settings hostの最初のround-tripで既存File.Replaceの『置換されるファイルを削除できません』を再検出。後続並列host開始前の失敗。残留testhostなし、ReadOnlyなし、失敗後の排他read成功。独立sqlite_investigationがfixture/provider/Settings初期化/runner順序を調査し、未Dispose streamや同時test ownerの証拠なし、holder不明。Defender等の稼働だけで原因とは断定しない。Quick042214は2/2pass（2.4311秒、build/test8.1秒）、同codeでの独立固有ファイル置換probe32回成功。初回PowerShell probeのnull引数変換エラーは原因証拠に使わない。新retry/wait/runner変更なし。
- T4最終Functional042306成功: 4502pass/13skip/0failure、全6host完了、retained ExitTime実行188.8秒（300秒budget内、180秒target超過）。fingerprint39CDE3F200E43EE5B954D8F8C1C6285B35EDD8E28E383002865A2760E23F77D4不変。初回failed runを保持し、UI/player/DB復元/IRを含む最終codeを確認。fresh fix-delta reviewへ渡す。

- U3 fresh fix-delta review shutdown_admission_fix_review: blocking findingなし、前回P1解消。受付閉鎖、先行workflow→player operation合流、通常Stop/Next/交換互換、packet/evidenceを確認。合流除去mutantの決定的検出は未立証のまま明示し、静的lock順確認で補完した。独立review中rootは全repo操作を停止。U3実装はworker起動上限によりroot逐次代替、独立oracleと最終reviewは実施済み。U1の独立review未実施は上記記録どおり。全unit完了。
