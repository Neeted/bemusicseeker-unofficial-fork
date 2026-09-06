# 動作モード再起動の終了待機統合

## Goal / Context

- Base: `852027bab2bc8f54162c840b08bb3b9817328281`。開始時の worktree は clean。
- 設定の動作モード変更で、通常終了と同じ tracked idle と terminal cleanup の実完了後だけ mutex 解放、新プロセス起動、WPF shutdown を行う。
- 現行入口は GeneralSettingsPage の radio click → SettingsDialogViewModel → App.RestartApplicationAsync。成功・失敗の直接 Shutdown は Closing のキャンセルを無視し、非同期待機を保証しない。
- Microsoft の公開契約: <https://learn.microsoft.com/en-us/dotnet/api/system.windows.application.shutdown?view=windowsdesktop-10.0>。

## Decisions / Constraints

- ユーザー承認: 通常 Close / update / mode restart は最初に受理した terminal intent を優先する。Close / update が先なら mode を保存せず再起動しない。mode が先なら待機後に一度だけ再起動する。
- mode、履歴表示 identity、runtime placement の atomic 保存、他の下書きの非保存、保存失敗時の継続、確認キャンセル、初期 profile 無しの選択だけの挙動を維持する。
- 再起動実行失敗は既存リソースによる通知の完了後に終了する。通知失敗も観測して終了する。半終了状態で継続しない。
- 新 launcher、retry / replay、永続的な再起動状態、version 更新、無関係な変更は対象外。commit は許可済み、push は行わない。
- UI thread 上で mode の受付・同期 atomic 保存を完結させる。update の終了準備受付も同じ UI dispatcher へ送り、チェックと保存の間に更新が先着する穴を作らない。owner lock 中の UI / callback / 他 owner の完了待ちは禁止。
- 「新プロセス起動を待つ」の対象は mode restart の後継アプリ。既存 updater の prepared launch / Proceed / Abort protocol は維持し、mode が先着した場合はその既存 Abort に合流する。

## Final design

1. SettingsDialogViewModel は確認後、composition 済みの狭い mode restart request を呼ぶ。自身から low-level lifetime の restart / shutdown を呼ばない。要求中の設定編集を止め、拒否・保存失敗では表示を復元する。
2. ShellShutdownWorkflowOwner が mode と history identity を受け取り、既存 UI dispatch 境界で Close / update の先着を確認し、既存 settings session の mode-only atomic 保存を実行する。成功後だけ一時的な restart intent を保持し、View に Close を要求する。保存失敗は caller へ伝え、終了準備へ進まない。
3. MainWindow は明示イベントを購読して Close し、既存 OnClosing / preparation / dialog close / state capture / terminal cleanup をそのまま利用する。domain orchestration を View へ追加しない。
4. owner の既存 terminal Task の末尾（player、settings、audio、DB locks、temp cleanup 後）で、mode intent がある場合だけ低レベル restart を await する。失敗通知もこの Task 内で完了させる。MainWindow は既存どおり Task 完了後に close を認可し、最終 Shutdown を要求する。
5. App / ApplicationRestartCoordinator は終端から呼ばれる新プロセス起動境界とする。直接 Shutdown callback を退役し、重複する性能ログ停止は owner の既存 preparation に統合する。mutex 解放は新プロセス起動の直前とし、先行 cleanup が済むまでは呼ばれない。
6. StartupUpdateWorkflowOwner の終了準備受付を UI dispatcher 上で完結させる。mode が先着して通常 Close arbitration から NotifyClosing した場合、後着 update は既存 abort route に従う。update が先着した場合、mode owner は保存前に拒否する。
7. 再起動失敗のダイアログは既存 UiDialogCoordinator / リソース / failure reporter を composition から渡す。新しい翻訳文言は不要。

### 接続の詳細

- mode request は `Task<bool>` など受理/拒否を明示できる狭い delegate とし、immutable な mode と history identity を渡す。保存失敗は例外で戻し、拒否を成功へ読み替えない。保存・先着確認は owner の既存 `dispatchToUi` 内で一続きに実行する。
- 保存成功後の View 向け Close 要求は既存 event composition の様式に揃える。MainWindow の OnClosing が既存 window close Task を所有する。購読がない場合に mode だけ保存して放置する route は作らない。
- owner が restart intent を確定して Close を要求するまで UI 上で await / message pump を挟まない。update 側は同じ dispatcher で TryBeginShutdownPreparation の受理判定を行い、その後だけ既存 background continuation を進める。
- low-level lifetime の restart は後継プロセス起動までを担当し、WPF Shutdown は MainWindow が終端 Task を await した後の一箇所へ残す。公開/internal の変更契約へ日本語 XML documentation を付ける。

## Ownership / verification

- 実装は単一 worker。対象は App.cs、AppApplicationLifetime.cs、Models/Utils/ApplicationRestartGateway.cs、Models/ApplicationContextPorts.cs（必要な契約文書）、ViewModels/SettingsDialogViewModel.cs、ApplicationComposition.cs、MainWindowViewModel.cs、MainWindow/ShellShutdownWorkflowOwner.cs、StartupUpdateWorkflowOwner.cs、Views/MainWindow.cs。
- テストは近傍の SettingsDialogBehaviorTests、ApplicationRestartGatewayTests、MainWindowViewHostTests、ShellShutdownWorkflowOwnerTests、StartupUpdateWorkflowOwnerTests を優先する。constructor / interface の変更に伴う他 fixture の機械的追随だけ許可する。
- 現行仕様は application-shutdown.md、settings-change-impact-and-startup-operations.md、startup-initialization-flow.md を更新する。本文の恒久契約は仕様を正本とする。
- 1つの lifecycle を分断すると所有権が重なるため並列実装しない。root は計画と統合・標準検証・commit を所有する。
- Packet: `OPERATION-MODE-RESTART-DRAIN`（designer 完了後に下記へ凍結）。実装前に承認必須。
- focused Quick: 上記5 fixture と変更に直接関係する composition fixture。通常終了・更新の受付も触るため最終 acceptance は Full（内包する canonical Functional は一度のみ）。
- 既存入口で実行可能な regression は base-fail / head-pass。構造的に新しい接続が必要な契約だけ packet の targeted negative control を使う。固定 sleep、テスト用 public API、実 Shutdown による shared testhost 終了は禁止。
- 完了条件: 旧迂回の退役、全 Contract の coverage と negative evidence、標準検証成功、凍結 snapshot の fresh static review、必要修正の再検証、commit。
- replan: UI 受付の直列化だけで先着を保証できない、保存失敗が終了を開始する、他下書きが再保存される、notification が終了前に表示できない、既存 update protocol の変更が必要、packet authority / reachability 不足。

## Test Contract Packet

Packet ID: `OPERATION-MODE-RESTART-DRAIN` / bugfix。root 承認済み。

Authority はユーザー承認と先着優先の追加回答、application-shutdown の終了入口/準備/terminal、settings-change-impact:353、startup-initialization:118/120/140、WPF の公開 Shutdown 契約。designer は Phase A でこれらと運用契約だけを読み C1–C7 を凍結し、Phase B で実装と既存 fixture を配置確認だけに使用した。authority / reachability gap は none。全件 behavior 契約であり source/snapshot/exact localized copy/characterization 例外は none。

### Oracle ledger

| ID | production ingress と保証 | 許容差分 / 誤実装 |
| --- | --- | --- |
| C1 | active profile の radio/公開 VM selection → 確認 → 実 shell 受付/保存 → 実 MainWindow Close → preparation/terminal → gateway。tracked worker 未完了時と player drain 中は mutex release/start/shutdown がゼロ。capture、settings、audio/temp/DB 最終確認後に restart/shutdown が各一回。UI marker を処理し再入 Close で短絡しない | 内部 Task/class/無関係な順序・所要時間は自由。早期 start、player detach、UI 同期 block は不可 |
| C2 | mode 入口 → 実 session/provider。mode/history/runtime placement だけ atomic 保存し、terminal 後の永続値にも他 draft が混入しない。保存失敗は bytes/他 draft を保持し raw/表示を復元、報告して preparation/restart/shutdown なし。terminal player の最終 placement も保存する | XML表現/翻訳/入力値/正当な終端 window state 保存は自由。全 draft Save、失敗後 Reload、失敗後 Close、古い placement 上書きは不可 |
| C3 | 確認 cancel は mode 表示を戻し、保存/preparation/restart/shutdown/profile再初期化なし。初期 profile 未成立では draft 選択だけ、確認/保存/terminal なし | presentation構造は自由。無条件 terminal/CancelをSave扱いにしない |
| C4 | 実 Window.Close または実 update の Start→presentation→download→受付が先着し、保留中 mode 確認が復帰。先行 terminal を保持し mode 保存/restart ゼロ | 拒否の型/UI構造は自由。更新check/download開始だけをterminal受付と扱わない。確認前だけのstale check/保存後拒否/UI外update受付は不可 |
| C5 | mode 保存/受付後に実Close再入/update continuationが到達してもdrain後restart/shutdown各一回。後着updateはProceedせず既存abort完了 | 既存prepare/abort mechanicsは自由。latest-wins、restart消去、Proceedとrestart両方は不可 |
| C6 | C1終端のgateway失敗→実通知owner。cleanup前startなし、通知未完了中shutdownなし、通知成功/fault/非表示failure後に報告してshutdown一回。半終了継続/retryなし | 翻訳/診断/例外wrapperは自由。fire-and-forget通知、通知faultで残留、VM直Shutdownは不可 |
| C7 | 実Close/update→実shell/MainWindow。既存tracked idle/player/settings/audio/lifetime保証を維持しmode未受理ではrestartなし。terminal settings save失敗でも通知/cleanup/終了継続 | protocolを変えない内部整理のみ。常時restart、通常終了hang、保存failureでcleanup中断は不可 |

### Coverage ledger / safety

- C1/C5/C6/C7: MainWindowViewHostTests の player drain case 周辺を extend。実非表示Window、shared WPF Application、Bass、既存DNP/serial-state-b。worker/player entered/completed、dispatcher marker、通知Task、lifetime eventで同期。C1にはplayerだけでなく実package install Enqueue等のproduction tracked worker独立gateを含める。
- C2/C3: SettingsDialogBehaviorTests の mode-only provider/save-failure/選択caseを extend/replace。固有settings/temp configと実disk再読込。旧localized exact copy/固定sequence/fake restart成功だけのassertionを新しいbehaviorへ置換。
- C4/C5: ShellShutdownWorkflowOwnerTests / StartupUpdateWorkflowOwnerTestsをextend。実shellとのcompositionとupdate Start→receipt→download→受付を使う。直接PrepareForStartupUpdateAsyncだけでは受付の直列化証明にしない。既存lane/shared ownership維持。
- C1/C6: ApplicationRestartGatewayTestsのcoordinator shutdown所有assertionをreplaceし、引数契約は保持。gateway invocation/return/faultを観測、process起動なし。fake lifetimeの終端adapter内で実coordinatorとrecording mutex/process gatewayを使うことは許可する。App実adapterの接続は静的reviewでも確認する。
- 新fixture不要。TestUiDispatcherHost、TestWindowPresentationScope、AwaitTaskOnDispatcherを使い、finallyでgateを解放し実owner/player/notificationを回収、Window/resource/native runtimeを復元。watchdogはfailure検出のみ。通常完了はTask/event/state。private Running設定、public test-only API、source文字列検証、shared testhostの実Shutdownは禁止。

### Negative controls

- N1/C1: mode受付後にpreparation/terminalを待たずgatewayを呼ぶvariantをtracked worker gateで落とす。
- N2/C1: preparation後のplayer awaitを外すvariantを早期start/audio/lifetimeで落とす。UI同期player close variantはdispatcher marker watchdogで落とし、外部coordinatorが必ずgate解放する。
- N3/C2/C3: subsetを全draft保存に置換、または保存例外後terminalへ進めるvariantをdisk再読込と終了作用ゼロで落とす。cancel/初期profileは入力variationで維持する。
- N4/C4/C5: 受付判定を省略/確認前だけにするvariantを、確認中のClose/update先着で落とす。逆順はrestart消失/二重実行/update Proceedを検出する。
- N5/C6: 通知awaitを外すvariantと、通知例外をterminalへ再throwし終了しないvariantを、通知gate中/通知fault後の最終作用で落とす。
- 既存入口で安全に動くものはbase-failを優先。新接続がなく同一regressionが構造的にbaseで実行不能な場合だけ理由を残し上記mutantを使う。compile/setup/unrelated failureはredに数えない。

workerはfixture mechanicsを適合させてよいが、先着の意味、保存subset、failure境界、drain前の禁止作用、通知完了、一回性を変えてはならない。技術的seam不足はresolver、authority/semantics/production reachability不足はNEEDS_ROOT_INPUT。

## 統合・検証記録

- implementation-worker の変更を統合後、追加 worker の再開・新規起動が `agent thread limit reached` で失敗したため、root が既存 packet を維持して追加統合を実施した。独立 oracle は上記 designer の packet を維持した。
- `SettingDialogEditCompletionTests` の radio click helper を新しい受付境界へ追随し、設定 VM の直接 lifetime 呼び出し期待を退役した。C1–C3 の既存 UI 入力 coverage として同じ unit に含める。
- C4 は実 MainWindow の確認中 Close と実 update の Start→presentation→download→受付を確認。C5 は updater の launch receipt を受け取る直前に gate を置き、mode 先着後の Abort 一回 / Proceed ゼロを確認した。
- C6 は通知完了 gate の確認前に UI の継続処理を進め、単純な通知 await 除去を検出できるよう補強した。通知成功、非表示結果、通知 Task の例外を確認した。
- 初回 Full `tests-full-20260906-104848` は `MainWindowChartPresentationWpfTests.SearchEditor_RoutedTabAppliesCandidateAndShiftTabReachesSavedActions` の focus assertion で失敗し、後続 phase は未実施。read-only explorer は今回の再起動 route との直接因果を発見せず、非 activation WPF fixture の合成 Space 入力を候補として報告した。production / 当該 fixture は変更せず、同クラスを含む Quick `tests-quick-20260906-105517` で 174/174 成功。単純な timeout retry とは扱わない。
- 関連6 fixture の Quick `tests-quick-20260906-110208` は 150/150 成功（test 14.8635 秒、build/test 47 秒）。初回に漏れていた旧 lifetime test の追随と追加競合 coverage を含む。
- 新しい shell request / View event 接続が base に存在しないため、同じ統合 test を base で実行する代わりに、下記の targeted negative control を実施した。各変更は直後に元の bytes へ復元し、常設 production 差分へ混入させない。

| ID | 意図的な誤実装と検出結果 | 証拠（`artifacts/verification/` 以下） |
| --- | --- | --- |
| N1 | mode 受付直後の早期 start。tracked worker 未完了時の restart 0 期待に対し 1 | `mode-restart-negative-20260906/N1/results.trx` |
| N2 | player close を開始するが await を除去。player gate 中の終了作用 0 期待に対し 1 | `mode-restart-negative-20260906/N2/results.trx` |
| N2-sync | player close を UI 上で同期実行。外部 observer の dispatcher marker watchdog が失敗し、finally で player gate を解放 | `mode-restart-negative-20260906/N2-sync/results.trx` |
| N3 | mode subset 保存を全 draft 保存へ置換。成功時の無関係 draft の永続値と失敗時の mode 保持 assertion が失敗 | `mode-restart-negative-20260906/N3/results.trx` |
| N4 | Close/update 先着判定を除去。先行 Close 後に mode を受理する副作用で失敗 | `mode-restart-negative-20260906/N4/results.trx` |
| N5-await | 通知を単純な fire-and-forget に置換。通知 gate 中に Shutdown が完了して失敗 | `mode-restart-negative-20260906/N5-await/results.trx` |
| N5-throw | 通知例外を terminal へ再 throw。通知例外後の最終 Shutdown が完了せず watchdog が失敗 | `mode-restart-negative-20260906/N5-throw/results.trx` |

恒久的な挙動の正本は対応する `devdocs/spec` とし、この節は当該変更固有の検証証跡を保持する。

- 最終 Full `tests-full-20260906-110452` は Functional（194.1 秒）、tool smoke、current/baseline publish、既存データ受入、更新受入、ProcessIntegration、v2.1.6.0 first-hop、ReleaseAcceptance、format が成功した。Functional の 180 秒 reporting target 超過は最終報告に含める。
- Full の analyzer phase は `RCS1194` 3 件で失敗した。verbosity normal で診断を再取得した結果も同じで、`LibraryChartRemovalOutcome.cs:50`、`SettingsEditSession.cs:77`、`PortableSettingsException.cs:7` の例外 constructor に対するものだった。3 ファイルは HEAD `852027bab2bc8f54162c840b08bb3b9817328281` と差分がなく、この unit では変更していない。今回の不具合修正に不要な constructor 追加や警告抑制は行わず、Full 全体を成功扱いしない。詳細ログは `.tmp/mode-restart-analyzer-details.log`。
- 初回の独立 static review は production correctness の blocking finding なし。C2 の保存失敗を実 shell owner 経由で確認する coverage 不足を acceptance-blocking P2 として受け、`MainWindowViewHostTests` の既存統合 helper を追加拡張する。常設 production は変更せず、同じ C2/N3 の範囲で実ファイル保存失敗と owner の失敗無視 mutant を検証する。修正後は関連 Quick と format、fix delta を対象とする fresh static review を行う。製品コード・通常機能・配布の前提が変わらない test-only 修正のため、Full の各 phase は再実行しない。
- C2 補強は実 provider に対して user.config の置換を FileStream の共有モードで阻止し、失敗報告を待ってから bytes・未保存 draft・raw/表示 mode の保持、preparation/restart/shutdown ゼロを確認した。終了準備の観測は実 shell と lifetime marker を使い、その後に blocker/gate を解放して通常 Close で回収する。N3-owner の「保存例外を無視して Close」mutant を検出し、非同期の失敗報告も許容する最終 test で再検証した証拠は `mode-restart-negative-20260906/N3-owner-async/results.trx`（報告未到達を 5 秒 watchdog で検出）。元 owner の SHA256 `52B93381138758442EE0DEB5DA7D0854935C83B8F4B98C78D47AE43673730CBE` に復元済み。
- review 修正後の最終 Quick `tests-quick-20260906-113918` は MainWindowViewHostTests / SettingsDialogBehaviorTests / ShellShutdownWorkflowOwnerTests の 61/61 成功（test 8.8605 秒、build/test 33.7 秒）。追加 helper の初回期待値誤りは実装の regression red として数えず修正し、最終結果に含めていない。
- 修正後の format / `git diff --check` は成功。fresh reviewer 新規起動、前回 reviewer 再開、同じ model / reasoning effort の generic read-only reviewer 起動はいずれも `agent thread limit reached` で失敗したため、workflow section 9 の代替として root が verification 完了後に修正差分だけを静的再確認した。実 owner への接続、報告完了待ち、bytes/draft/raw mode 保持、準備・終了作用ゼロ、失敗時の gate / file blocker / Window 回収を確認し、初回 P2 は解消と判断した。初回は独立 review 済みだが、修正後の独立 fresh review は未実施である。

## 完了時の残留事項

本 unit の製品コード修正、関連仕様、回帰テストと negative control は完了。通常終了・更新・動作モード変更の先着優先を保持する。検証上の残留事項は、変更範囲外の `RCS1194` 3 件による Full 全体の未成功と、ツール上限による修正後の独立 fresh review 未実施である。これらを最終報告に明記し、無関係な constructor 追加や診断抑制を混ぜずに本 unit をコミットする。
