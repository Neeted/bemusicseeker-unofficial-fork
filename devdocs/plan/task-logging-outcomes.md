# Task ログと完了結果の分離

## Goal / Context

基準 commit: `1f5cb421fb2b44ce452196dd717badcb35cc329f`。開始時 worktree は clean。

非ジェネリック `Logging()` がログ continuation の成功を操作の成功として返す経路を廃止する。ユーザーは静的調査結果の推奨方針による実装と、必要な commit を承認済み。push / release は対象外。

production evidence:

- BMT 順ドラッグ -> `MainWindow.customTablePlaylistSummary_Drop` -> workspace / BMT sort coordinator -> DB 保存。保存失敗は owner から再送出されるが旧 helper が吸収し、`Effects = Move` へ進む。
- 表削除メニュー -> table removal owner -> 表削除 -> LR2 config 保存。後半だけ失敗する部分成功がある。空 root の選択整理は既に削除された表示を整える処理として失敗時も必要。
- chart folder edit -> `RegularChartListOwner.RenameChartFolderAsync` -> rename task 登録 -> shutdown drain。操作 Task と単なる直列化用 tail の意味を分ける。
- `App.Application_DispatcherUnhandledException` は通常の I/O 例外等でアプリを終了するため、UI event から新たな例外漏出を作らない。

## Decisions / Constraints

1. 操作 Task は成功、元例外、キャンセルと generic 結果を呼出元へ保持する。ログ failure は操作結果を置き換えない。
2. 完了結果を捨てる呼出しは `ObserveFault`（void）へ移行する。元 Task の例外を観測し、logger が失敗しても未観測の診断 Task を作らない。
3. UI の失敗はログとエラー通知で操作を中断し、アプリを継続する。キャンセルは通知しない。既存の機能別通知を優先する（ユーザー回答済み）。既存リソースを利用し、翻訳の exact copy をテストしない。
4. 成功時だけの UI 続行と、部分成功でも必要な表示整理を区別する。新しい rollback / retry / persistent state / operation framework は追加しない。
5. 既存の owner、mutation gate、thread affinity、shutdown の待機対象、DB / file format を維持する。既存 `LoggingAndPropagate` の無関係な UI route（startup の fatal 契約を含む）を全面整理しない。
6. 既存旧 helper は移行中のみ残し、全呼出元を移行した段階で overload ごと削除する。旧 helper を互換 route として残さない。

## Draft units / Ownership

U1 は一つの worker で完了・検証する。その後 U2 と U3 は所有pathを分離して最大2 workerで実装する。両者の編集完了後にQuickを順番に実行し、build/test/fingerprint競合を防ぐ。

- U1: `Ribbit/Util/Extensions/TaskEx.cs`、`BeMusicSeeker/Models/Utils/TaskEx.cs`、`BeMusicSeeker.Tests/TaskLoggingTests.cs`、`devdocs/spec/logging-policy.md`。Task / Task<T> の伝播と void observer の契約を実装。既存ログ仕様へ統合し、新しい仕様ファイルを増やさない。旧 overload は U3 まで一時残置。
- U2: `BeMusicSeeker/Models/` と `BeMusicSeeker/ViewModels/` 内の旧 helper 呼出し（U1 helper を除く）。返却 / 保持 / await する Task と observer を分類して移行する。関連既存 fixture のみ変更する。大半の observer 置換は機械的変更であり、新 owner / state は追加しない。
- U3: `BeMusicSeeker/Views/MainWindow.cs` と必要なら同 partial の UI terminal 専用ファイル、既存 UI regression fixture。旧 await を結果保持へ移行し、イベントの失敗 / キャンセル処理を明示する。残る観測専用 route を移行し、U1 helper の旧 API を削除する。
- Root: この計画、packet 承認、移行完了後の旧helper機械的削除、統合検証、凍結 snapshot review、commit。

## Verification / Done when

Test Contract Packet は独立 designer の結果を root が承認後にここへ記録する。実装は承認後に開始する。

- 既存 `TaskLoggingTests` を extend。UI は `MainWindowPlaylistWorkspaceWpfTests`、owner は `RegularChartFolderRenameTests` 等の既存 coverage を優先する。
- bugfix は実 production ingress の base-fail / head-pass、追加 API は targeted negative control。正常完了は Task / event signal で確認し、固定 sleep と物理カーソルを使わない。
- unit ごとの filtered Quick 後、最終 snapshot の Functional を一回実行する。Full / release は対象外。
- `.Logging(` と旧 API が残らないことを検索と build で確認する。source text を固定する test は追加しない。
- 標準検証後は worker を終了させ、凍結 snapshot を fresh static reviewer に渡す。review 中 root は repository 操作を停止する。
- 仕様と coverage map、旧 route の退役、必要な cleanup が揃い、review blocking finding がなくなれば完了。

## Replan triggers

既存の機能別通知と新 UI 通知の重複を局所的に判定できない、型付き failure の意味が一意でない、shutdown が操作成功と drain 完了を混同している、新たな state / retry / rollback や広い owner 変更を要する場合は root へ戻す。ユーザー判断が必要な observable semantics は質問として提示する。

## 計画点検の反映

`plan-clarifier` の点検完了。仕様正本は既存 `logging-policy.md` を使用する。startup の fatal policy は対象外。callsite の分類不能、既存通知との重複、操作 Task と shutdown drain の混同は worker が自己判断で変更せず root へ返す。U2 の canonical coverage は `RegularChartFolderRenameTests`、U3 は `MainWindowPlaylistWorkspaceWpfTests` と既存 typed terminals / dialog service を使う。clarifierが安全な並列候補としたU1後のU2/U3は、下記の仕様path所有と検証順を分離して並列実装する。

## 承認済み Test Contract Packet: TASK-LOGGING-1

独立 `test-contract-designer` が Phase A で production body / existing expected / runtime output / 翻訳値を読む前に oracle を凍結し、Phase B で repository の placement を確認した。Root 承認済み。bugfix と API migration。authority は上記ユーザー決定、`logging-policy.md` の目的、`application-shutdown.md` の実完了 drain、`playlist-data-and-export-flow.md` の永続化失敗と publication 契約。

| ID | 必須 outcome / production ingress | 許容変化 | 誤実装 / evidence |
| --- | --- | --- | --- |
| TL-RESULT | production の await / 保存 / return -> TaskEx。Task / Task<T> の成功値・元例外・cancel を保持、早期完了しない。logger failure は操作結果を置換しない | Task identity、内部構造、scheduler、診断format | fault吸収、cancel成功化/fault化、generic default、loggerで元例外上書き。既存3case＋新API mutant |
| TL-OBSERVE | production fire-and-forget -> void observer。元faultを観測し、操作完了待ちを強制せず元Task状態維持。cancel error通知なし、診断failureの未観測Taskなし | 同期済みTaskのcallback timing、名前、診断format | callback欠落、未完了時通知、awaitableな偽成功を返す。callback signal / void型適合、内部Task漏出は静的review |
| TL-RENAME | UI cell edit -> RenameChartFolderAsync -> 登録Task -> StopAsync。busy / finalization failureは操作Taskがfault、成功refresh禁止、gate解放。StopAsyncは処理と既存refreshの実完了を待ちcleanup継続 | tail/queue構造、Task identity | fault吸収、tail汚染、未完了detach、cleanup短絡。既存busy/finalizationをbase-fail/head-pass、drain既存case＋早期完了mutant |
| TL-UI | 実UI event -> terminal/owner -> UI終端。failureを診断・既存優先error通知し成功後続中断、dispatcherへ漏らさず継続。cancel無通知。通知自体のfailureはログのみ、代替dialog/retryなし | 翻訳、private構造、通知API | 無通知吸収、catch欠落、二重通知、cancel通知、通知failure漏出。実routed eventと既存dialog port |
| TL-PERSIST | BMT順UI -> workspace/coordinator -> DB。実保存failureを合成Taskがfault保持し成功refresh/selection/URL同期にしない | fixture入力、内部構造 | failure後success publication。実DB failureとevent、owner単独base-greenは移行redと混同しない |
| TL-PARTIAL | 表削除MenuItem -> TableRemoval -> 削除済み -> LR2 config失敗。failure保持/通知と空root選択整理を両立 | cleanup配置、control内部、翻訳 | cleanupをsuccess-onlyへ移動、cleanupのためfailure成功化。既存empty-rootのfailure variantとmutant |

### Coverage ledger / 検証境界

| ID | Fixture / 方針 | Resource / lane | 完了signal / 退役 |
| --- | --- | --- | --- |
| TL-RESULT / TL-OBSERVE | TaskLoggingTests extend | test-local callback、remaining、NLog global変更なし | 元/返却Taskとcallback TCS。旧3case重複なし、旧APIはU3退役 |
| TL-RENAME | RegularChartFolderRenameTests replace+extend | GUID DB / folder、既存gate/scheduler/dialog、remaining | rename/refresh/StopAsync。busy/finalizationの正常GetResult期待をfaultへ置換 |
| TL-UI / TL-PARTIAL | MainWindowPlaylistWorkspaceWpfTests extend | 既存DNP/serial-state-b、共有WPF harness | dialog signal、TreeSelectionActivated、AwaitTaskOnDispatcher。実MenuItemと既存TableRemoval terminal |
| TL-PERSIST | PlaylistWorkspacePresentationStateTests extend | GUID DB、実coordinator、既存scheduler、remaining | workspace Task / publication / 明示dispatch drain |
| playback TL-UI | PlaybackPanelViewModelTests 既存利用、必要ならextend | 既存資源、serial-state-b | 既存notification / operation signal |

正常完了の固定sleep/GC/未観測例外global eventに依存しない。翻訳文言/source/private反射のexactness例外なし。新APIはfault吸収、cancel成功化、generic default、logger再throw、observer callback省略のtargeted negative controlsを対応assertionで落とす。旧API不存在は検索/build evidenceのみ。

BMT drag Effects のdrag元伝達保証は対象外。cancelされたasync void eventは元Taskだけを完了signalとせず、既存event/dispatcherで閉じられない部分はhelper検証とUI静的traceへ限定し未実行evidenceを明記する。新しいpublic test seamやpersistent stateを作らない。

### U1 final assignment

上記U1の4 pathのみ編集。TL-RESULT / TL-OBSERVEを実装する。`LoggingAndPropagate`はTaskとTask<T>、observerは`ObserveFault`でvoidとし、アプリwrapperは既存NLogWrapper経由log2fileを使う。変更APIの日本語XML契約を整える。旧Loggingは移行中だけ残す。元taskの結果保持とlogger failure非干渉を守り、UI classや新logger stateを追加しない。Quick filterは `FullyQualifiedName~TaskLoggingTests`。本unitの終了時にproduction/test path、contract、red/negative-control、Quick結果を引き渡し、Functionalやcommitは実行しない。

### U2 callsite 分類 / final assignment

Models / ViewModels の全旧 helper callsite を read-only explorer が分類した。対象ファイルが多いのは同じ旧 API の観測専用 callsite が分散しているためであり、以下の例外以外は式の機械的置換に限る。新 subsystem の設計変更はしない。

| Callsite | 分類 / 移行 |
| --- | --- |
| BMSLibrary: IrScorePrefetch | 保存 Task<IrScorePrefetchResult>、generic LoggingAndPropagate |
| LibraryFileScanPipelineOwner: NormalFolderMtimeSnapshotTask / lr2FolderFileDiffPreparationTask | 保存 Task<T>、generic LoggingAndPropagate |
| MainWindowViewModel: Initialize backup await | generic LoggingAndPropagate、既存BackupSaveResultと機能通知維持 |
| InternalBMSAutoPlayerSoundOnly: _infloopTask | 未設定時だけ元Task保存＋ObserveFault。既存null化を維持、restart/recovery追加なし |
| RegularChartListOwner: renameTask | 元Task保存/返却＋ObserveFault。直列化用folderRenameTailは今までどおり完了signal。操作結果とtailを混同しない |
| PlaybackPanelViewModel: CreateBackgroundCommand | UI command終端としてworker内でaction/catch/既存通知を完結、TaskをObserveFault。同期通知adapterをUI threadから呼ばない |
| 上記とU1のhelper転送を除く全Models/ViewModels callsite | 戻り値未使用、ObserveFaultへ。voidになるため不要な `_ =` を削除。元Taskの保管/awaitは維持 |

U2の書込み所有は上記分類に該当する `BeMusicSeeker/Models/`、`BeMusicSeeker/ViewModels/` のcallsiteのみ（U1 helperは除く）、`RegularChartFolderRenameTests.cs`、`PlaybackPanelViewModelTests.cs`、既存仕様 `application-shutdown.md` の該当契約。`logging-policy.md` はU3所有。root以外は本計画を書き換えない。

- TL-RENAME: shutdown側の `Task.WhenAll(folderRenameTasks)` は全件終端を待ってから、observerが既にログ観測する操作failureを明示的に受け止める。`StopAsync`の目的はdrainであり、保存操作の成功証明に使わない。後続shutdown cleanupを短絡させない。過去の完了faultを保存する台帳は追加しない。
- UI cell editのrenameがreceipt作成前に失敗した場合、既存 `mutationDialogs` があればgate解放後に通知する。receiptがある場合は既存 `FileDbMutationReport` を優先する。cancel無通知。通知自体のfailureは診断のみで元操作例外を置換しない。dialog port無しの既存pure owner fixtureに新UI依存を追加しない。
- Playbackのcommand単位で「既存通知あり」と分類する案は不採用。Start/Next/Previousでも一時譜面の退避/復元MoveFile、StartのTogglePauseに未通知failureがあり、これらをログだけにしてしまうため。通知済みstate/例外Data/AsyncLocal/新exception markerは追加しない。
- ユーザー追加回答済み: 再生通知の表示自体のfailureもログに残し再通知せず操作中断、次曲等の後続へ進めない。direct Startの通知failure再throw契約もこの決定で変更する。`NotifyPlaybackFailureSafely` と `StopAfterPlaybackStartFailure` の `propagateNotificationFailure` 引数/再throwを削除し、既存failure branchのreturnと停止cleanupを維持する。全commandのworker境界で未処理操作failureを既存通知seamへ渡し、cancelは通知しない。
- TL-UIのplayback追加caseは未処理action failure、cancel無通知、次のcommandが受理されること。通知failureの既存testは独立designer追補後にreplaceする。TL-RENAME busy/finalizationのfault期待はbase-fail/head-passで閉じ、進行中rename failureとStopAsyncのdrainも検証する。
- Quick filter: `FullyQualifiedName~RegularChartFolderRenameTests|FullyQualifiedName~PlaybackPanelViewModelTests|FullyQualifiedName~TaskLoggingTests`。既存generic prefetch等は最終Functionalで統合検証する。旧Logging API削除はU3で行う。

### U3 callsite 分類 / final assignment

対象は `Views/MainWindow.cs`、必要な同View partial、`MainWindowPlaylistWorkspaceWpfTests.cs`、`PlaylistWorkspacePresentationStateTests.cs`、必要なら既存 `MainWindowPackageMaintenanceWpfTests.cs` のharness引数転送のみ。`logging-policy.md`の完成もこのunitに含む。U1 helperの旧overload削除は両worker完了後rootが機械的に行う。

- 旧LoggingをawaitするUI eventは、元Taskをawaitして失敗をcatchする（またはAndPropagate＋UI catch）。genericの結果を保ち、成功時の後続コードはtry内に置く。OCEは通知せずその操作を終端、その他はログと既存dialog serviceで通知し例外をevent外へ漏らさない。
- 既存 `playlistWorkspaceDialogService` と既存翻訳 `Msg_error_unexpected` / `Error` を通知へ使用する。同期ShowUiMessageを新UI async境界で呼ばず、awaitするprivate通知methodへまとめる。通知自体のfailureはdiagnosticのみとし、成功へ戻った扱い/代替dialog/retryをしない。新forwarding class、全機能横断のstate、exception markerを追加しない。
- 操作receiptのfailureを `Task.FromException(...).Logging(...)` で記録する既存event / `ObservePackageCatalogMutationAsync` は ObserveFault に式を置換するだけで、既存typed result / FileDbMutationReport等の通知を維持する。
- `ApplyPackageCatalogMutationViewAsync` は合成Taskでありgeneric AndPropagate（またはraw await）で結果を伝播する。このhelperを使う3つのUI eventも新たなUI例外漏出を作らないよう確認する。
- 同期eventのfolder rename / create / delete / playlistTableDropは、UIの明示操作なのでeventをasync化してawait/catchする。受付時に設定するdrag effectの意味は勝手に成功receiptへ変更しない。
- `tableContextMenuOpened` の補助document populationはobserverへ移行し既存cancel処理とメニュー更新を維持する。`ApplyTerminalShutdownAsync` のdiscard AndPropagateはObserveFaultへ移行する。startup / その他既存AndPropagateの全面改修は対象外。
- BMT summary DropのMove設定は操作成功後だけ。既存preview cleanupはfinallyに残す。表削除の空root selection整理はfinallyに置き、LR2config失敗後も必要な整理を行う。
- TL-UI / TL-PARTIALは既存MenuItem.RaiseEventとTableRemoval terminal/dialog fakeを通す。helperのprivate reflection testを追加しない。TL-PERSISTは実DB failureと既存workspace publication seamを使う。
- 関連Quick: `FullyQualifiedName~MainWindowPlaylistWorkspaceWpfTests|FullyQualifiedName~PlaylistWorkspacePresentationStateTests|FullyQualifiedName~TaskLoggingTests`。harnessを変更した場合は既存package maintenance fixtureも加える。無関係なテスト基盤/runnerは変更禁止。
- rootは両workerの編集完了後、Ribbitとアプリwrapperの旧Logging全overloadを機械的に削除する。検索で残存なし、順番にbuild/Quickで全callsiteの型適合を確認する。source固定testは追加しない。

### 並列実装と検証の引渡し

U2/U3はまずregression testだけを編集して `TESTS_READY` を返し停止する。rootが両者の停止確認後に関連Quickを一度実行してbase相当のredを採取する（U1のみ実装済み）。その後両者へproduction編集を許可する。両者は編集完了で `EDITS_READY` を返し停止する。rootが旧APIを削除し、同じfilterのQuickでhead確認する。必要なnegative controlは片方ずつ明示許可し、もう片方は編集/検証停止する。これにより各workerのpath所有、oracle、標準入口とtestのred/head境界を保ったまま競合を防ぐ。

### 承認済み playback packet 追補

独立designerがユーザー追加回答からPhase Aを凍結後、指定既存2testのみplacement確認。root承認済み。

| ID | 必須outcome | 誤実装 / evidence | Fixture / 完了signal |
| --- | --- | --- | --- |
| TL-UI-PB | 再生操作/command -> Start等 -> 再生failure -> dialog failure。元再生failureへの通知を試行し、通知failureは診断のみ。direct Startでも再throwせず、再通知/成功通知/次曲へ進まない | 同期だけ再throwするbaseでred、再通知/次曲続行mutantを拒否 | PlaybackPanel_DoesNotHideNotificationFailureOrRunFollowingStopをreplace。direct Start return、通知記録、公開playback state。既存async case維持 |
| TL-UI-PB-CMD | 実command Execute -> 未報告のplayer/file操作failure。ログと既存機能通知、cancel無通知、通知failure再通知なし | commandを一括通知済み扱いするmutant、cancel通知mutant | PlaybackPanelViewModelTests extend、既存player/file seam、command/owner完了signal |

両IDは既存 `serial-state-b` のfixture資源所有を維持。callback開始signalだけで後続通知不在を証明しない。新test-only API/state/exception marker/固定sleepを追加せず、completion seamが不足すればgapとしてrootへ返す。safe helper構造、ログ/翻訳文言、private cleanup回数は固定しない。旧通知exception throw期待のみ新契約へ退役し、非同期封じ込めcoverageは重複新設しない。

## Verification evidence

### U1 helper 完了

- 新APIは両層ともTask/Task<T>のAndPropagateとTask receiverのvoid ObserveFaultの3本に限定。不要なtyped logger/generic observer overloadは統合確認で削除した。
- `Quick -TestFilter 'FullyQualifiedName~TaskLoggingTests'`: 最終10/10成功、`artifacts/verification/tests-quick-20260906-052406`。
- generic default mutantは初回の2caseで失敗。さらにfault/cancel吸収、logger再throw、observer callback欠落の複合mutantで5/10失敗（`tests-quick-20260906-052256`）、復元後10/10成功。
- 元Taskの未完了、元例外identity、generic値、IsCanceled/IsFaulted、callback signalとvoid型適合を確認。内部診断continuationのfailure非漏出は静的traceも使用する。
- U1 worker完了、所有4pathをrootへhandoff。U2/U3のtest-only準備を並列開始。旧APIは両移行後に削除する。

### U2 / U3 修正前の回帰検証

- test-only snapshot の combined Quick: `tests-quick-20260906-054830`。129件中121成功 / 8失敗、test実行44.5秒、tracked fingerprint不変。
- 想定どおりのred: rename busy / durable finalization 2条件 / in-flight failure の4件は操作例外が返らない。direct Startは通知例外が漏出。command failureとUI表削除failure 2条件は通知がなく、限定watchdogで失敗。
- UI確認Cancel / operation cancel、実SQLite保存failureからworkspace Taskへの伝播はbase-green。これらを移行bugのred証跡とは扱わない。
- 派生例外を許容するcapture、実operation faultとshutdown drainの分離、通知の正のsignalを使用。fire-and-forgetのcallback開始だけでは再通知不在を証明しないため、その部分は静的traceへ残す。
- U2/U3にproduction編集を許可した。両workerはEDITS_READYまで編集のみとし、build/testを起動しない。rootが両者停止後に旧API削除とhead検証を行う。

### 統合時の追加確認

- U2/U3のhandoff後、旧Logging overloadをrootが削除。検索で旧APIと呼出しの残存なし。
- 初回head Quick `tests-quick-20260906-060957` はMainWindowのvoid observerへのdiscard代入1件でcompile failure。testは未実行であり、behavior redとは扱わない。
- MainWindowの同期throwはAndPropagateへ到達しないため、元例外の診断をUI通知methodへ集約し、新たに囲んだeventだけraw awaitへ揃える。U3aの所有はMainWindow/logging-policy、別writer停止下でhelper workerを再利用する。
- UIセル編集SetPendingAsyncの未報告DB failureという仮定は、実routeにDB書込みが確認できないため撤回。invalid selectionは既存機能通知があり、fake-only一般failureを根拠に拡張しない。
- 行再生ActivateRowの一時譜面ファイル移動failureは実routeがあり、observerだけでは無通知。ユーザーの既定UI方針に含める最小追補packetを独立designerが設計中。元のrename receipt無しfailure通知も初回計画どおり閉じる。

### 承認済み ActivateRow 追補とU2a

独立designerのPhase A/Bを経てroot承認。TL-UI-ACTIVATE: 実行可能な行activation -> HandleTableRowActivation -> ExecuteTableRowActivation -> StartAtIndex -> 一時installの同名回避MoveFile failure。既存playback dialogへ通知し中断、cancel無通知、通知failureは診断のみ、後続再生/成功publication/再通知なし。ログ/翻訳/内部helper/timingは固定しない。rollback/retry/new stateは追加しない。

- `PlaybackPanelViewModelTests`をextend。既存CreateTemporaryInstallPanel、GUID source/destinationに同名chart、sourceをread共有・delete非共有の所有handleで開いて実共有違反を作る。既存notification signalを限定watchdogで待つ。旧observer-only routeの通知欠落をred/headで確認。通知後の将来不在は終端Taskが公開されないため静的trace併用、新seam/sleep/早期shutdownで成功を作らない。
- SetPendingAsyncのDB failureとActivateRowのBeforeClose fake failureは対象外。後者はcloseProcess=falseのため入口から到達しない。
- U2a所有: PlaybackPanelViewModel、RegularChartListOwner、同2既存test。行activationとcommandで未報告failureのterminal処理を共通化し、同じprivate worker境界から既存dialogを呼ぶ。renameはreceipt無しfailureでも既存mutationDialogsがあればgate等解放後に通知し、元taskのfailureを維持。初回TL-RENAME/TL-UIの未完了分であり新通知stateは追加しない。
- まずtest-onlyでTESTS_READY停止、U3a終了後rootが追加filterのredを確認してproduction編集を許可する。Functionalは全unit完了後の一回とする。

### 統合 Quick と追加ケースの完了

- U3a終了後は他writerを停止し、U2a workerへtest-only Quick -> production修正 -> head Quickの逐次実行を許可した。MainWindowのdiscardはhandoff後にも残存したため、rootが機械修正し全source検索で解消を確認した。`tests-quick-20260906-061901`もcompile failureでありtest evidenceには含めない。
- `tests-quick-20260906-062451`: 130件中128成功、追加activation通知欠落とbusy rename通知0の2件だけ想定red。最初の8件はすべてhead-pass。
- `tests-quick-20260906-062825`: 130/130成功、test実行30.0秒。U2a worker終了、全implementation worker停止。
- TL-PARTIALのcleanup呼出しだけを一時的に省いたtargeted negative control `tests-quick-20260906-063201` はfailure、通知failure、operation cancelの3/3でcleanup signal欠落を検出。元の呼出しはrootが復元しdiff-check成功。復元後の受入は同じsourceを再buildするFunctionalで行う。
- rename drainは既存の進行中signalとStopAsync未完了assertion、成功/failure両DataRowで検証済み。detach mutantは既存fixtureの失敗cleanupが進行中workerを解放せず10秒watchdogへ依存するため追加実行しない。故意の未完了worker残留を作らず、このnegative-control未実行は静的traceと既存drain assertionの証跡と区別する。

### 最終 Functional

- `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional`: `tests-functional-20260906-063352` 成功。
- 4,517成功 / 13 skip / 0失敗。全6host成功、test実行197秒（180秒reporting target超過、300秒budget内、retryなし）。実測値をユーザー報告へ含める。
- restore/build成功、tracked fingerprint不変 `626923BBA4B40E033E5C7B7CB05751458C716C752CE8120D87FD599C77E27783`。TL-PARTIAL mutant復元後のsourceを再buildして受入済み。
- 全implementation worker停止。base `1f5cb421fb2b44ce452196dd717badcb35cc329f` と現在のworktree全差分を凍結し、static reviewへ渡す。

### 初回 static review と修正 U4

独立 `repo-static-review` は38 tracked差分、計画、consumerまで静的確認し、P2を1件指摘。`playlistTableDrop` -> `AddRowsToFolderAsync` -> custom folder output failure -> 既存receipt警告 -> primary再送出 -> 新UI汎用通知により二重表示する。通知自体のfailure後も再通知するため既存優先契約違反。ほかのblocking/recommendationなし。初回snapshotは `artifacts/verification/task-logging-review-initial.patch` と同名directory内の4fileへ保存。

独立designerのdrop追補をroot承認:

- TL-UI-DROP: 既存Warning/Error receiptがあればgenericerrorを追加しない。未報告primaryfailureはgenericerrorを一回、Informationだけでは代替しない。通知callback failure後は診断のみで再attemptせず、元Taskは元primaryfailureを保持。cancelは無通知。
- TL-UI-DROP-PRESERVE: `library-mutation-boundary.md` の既存DB/reference/UI invalidation/BMT post-lease独立attemptとdurable部分成功を維持。primary無しの成功を通知failureで失敗へ変えない。rollback/retryなし。
- Coverage: `PlaylistWorkspacePersistenceCommandTests.PlaylistDropAdmission_IsAtomicAndConvergesAfterLeaseRelease`をextend。既存outputfailureにreceiptのWarning/Error件数・元例外identity・通知attemptを追加。未報告failureは実DB書込拒否でgeneric通知を検証。GUID DB/output、既存lease/BMT seam、remaining lane、operation Taskの終端を使う。新fixture、DNP、UI seam、reflection、cursor、sleepなし。
- priorityを無条件generic追加へ変えるmutant、generic欠落、notification retry/primary上書きを最小の既存caseで検出する。UI実dropはreceipt consumerが追加dialog fakeを通らず許可されたDragEventArgs seamもないため、owner単独base-greenと実UI redを混同しない。UI再通知経路の退役はstatic evidence。Information-onlyはpriority判定negative control、fake-only cancel caseは追加しない。
- U4所有: `Views/MainWindow.cs` のplaylistTableDropのみ、`PlaylistWorkspaceViewModel.Mutations.cs` のdrop通知境界、上記既存test、`logging-policy.md` の通知責務。既存notificationSession/receiptでdrop全処理の通知を一つの終端へ閉じ、一般failureは既存Warning/Error無しの場合だけ追加。source Taskの元failureは再送出。UI dropは既に通知責務が閉じたTaskのfaultをログ観測するだけにする。例外marker、Data、追加AsyncLocal、通知済みpersistent state、compatibility overloadを作らない。他mutation通知の意味を変えない。
- 全他writer停止。U4 workerはtests -> red/negative-control -> production -> filtered Quickを順次実行。rootはその間read-only。MainWindowPlaylistWorkspaceWpfTests / PlaylistWorkspacePersistenceCommandTests / TaskLoggingTestsの関連Quickを使用。修正後fresh reviewはfix deltaと今回finding、直接影響するinvariantを主対象にする。

### U4 の検証と統合

- 初回review用sourceコピーをrootがartifact配下へ `.cs` のまま保存したため、SDKのCompile globへ混入し `tests-quick-20260906-065804` がcompile停止した。対象3fileを検証済みの同一directory内で `.cs.snapshot` へ改名して解消。productionの除外設定は追加していない。これは検証準備の不備でありbehavior redに数えない。
- workerによる修正前Quick `tests-quick-20260906-070728`: 57/58成功、実DB書込みfailureのgeneric receipt欠落だけがred。修正後 `tests-quick-20260906-071007`: 58/58成功。修正前source復元時のtimestampによるstale DLL再利用はtimestamp更新で解消し、上記red/headで実sourceを再buildした。
- rootのpriority negative control `tests-quick-20260906-071654`: Warning/Errorの優先条件だけを一時的に除去すると1/1失敗（必要なWarningがErrorへ変わった）。条件は復元済み。generic欠落のredと区別し、UI実dragの二重表示を動的に再現した証跡とは扱わない。
- 新規assertの内部route名・翻訳文言固定だけをpacketに合わせて削除し、receipt severity/count、元failure identity、通知attempt、durable effectsの検証を保持する。Information-only、再通知、primary上書きの個別mutantは追加実行しておらず、既存assertと静的traceの証跡に限る。
- U4は通知責務を移動して通常機能検証の前提が変わったため、復元後の最終snapshotにFunctionalをもう一回実行する。runnerや予算を変えず、初回197秒の成功とは別の修正後受入として記録する。

### 修正後の最終受入

- `tests-functional-20260906-071832`: 4,517成功 / 13 skip / 0失敗、全6host成功。test executionは200.5秒（180秒target超過、300秒budget内、retryなし）。restore/build成功、0 compile error。
- tracked fingerprint不変 `F82C714E1CBF98976F9DC97FFA2ECF8EC3C0C8EAA26A6A3CD8F13588BC7CEF9C`。priority mutant復元とpacket exactness整理後のsourceを再buildして受入済み。
- 全writer停止。U4の4file、初回reviewのP2、直接影響するfailure/notification/lease契約と本計画をfix deltaとしてfresh static reviewへ渡す。

### 完了

- 修正後の独立 `repo-static-review` はU4の4fileのfix delta、前回P2、consumer、TL-UI-DROP / PRESERVEと検証証跡を確認し、blocking finding / recommendationともになし。一次例外・cancel・成功結果とpost-lease effectsを維持したまま二重通知を解消した。レビュー中のroot/workerによる編集・検証はなし。
- 旧Logging APIと呼出しは退役済み。待機する操作の結果伝播、専用observer、UI failure/cancel/通知failureの方針、再生中断、renameの結果保持とshutdown drainを実装・検証済み。現行契約は `devdocs/spec/logging-policy.md` と `application-shutdown.md` に反映した。本計画は独立packet、判断履歴、固有のred/negative-control/受入証跡として完了記録に残す。
- 実UI dragの二重表示と、上記に明記した個別mutantは動的未実施。ownerの実DB/output failure試験とstatic traceによる証跡に限る。全agentは作業終了、未決のユーザー質問なし。
