# LR2 設定と外部試聴の安全性改善

Status: Completed

基準: `8daf19439adae7635074180772dd39bf8cf3f8ee`。開始時の worktree は clean。

## Goal / Context

ユーザー依頼に従い、v3 安全性改善計画の BMS-001 / BMS-002 / BMS-003 を実装・検証し、コミットする。LR2 Config は設定画面などで保存した変更を正本とし、試聴が古い内容へ戻さない。

現行実装の最低限の妥当性確認:

- 設定再構成の `ApplicationComposition.CreateBmsPlayerForSettings` は独立した LR2Config をプレイヤーへ渡す。startup の `CreateLR2PlayerConfig` は root の instance を共有する場合もある。双方で正本を守る必要がある。
- `LR2body.PlayStart` は保持 XML を変更・保存し、起動直後と終了時に保持 XML を再保存する。検索ルート保存後の逐次試聴でも古い全文が戻る。
- 一時設定保存後の process 開始例外は通常の復元経路へ到達しない。未開始 session の停止 API に依存する cleanup では復元を保証できない。
- `LR2Config.Save` / `RemoveBMSSearchDirectoriesAndSave` と playlist backup は保存先へ直接上書きする。XML memory の復元だけでは途中書込みから旧 file を保全できない。

## Constraints / Decision list

- 設定表示・編集・Cancel、未保存 draft と保存済み設定の区別を維持する。全設定 draft 基盤、汎用 XML merge、retry、journal、version token は追加しない。
- BMS-001 は検索ルートと非所有値の巻戻し防止、BMS-003 は試聴一時値の復元と開始失敗の終端を所有する。同一 scenario の検証は重複させない。
- 電源断耐久、複数プロセスの同時書込み保証、SQL 値変換の BMS-007、他の安全性 issue は対象外。
- 試聴中の外部設定ツールによる更新を「常に正本」に含むかはユーザーへ質問中。まず BeMusicSeeker 内の保存を保証する既存計画の範囲で進めることをユーザーへ通知済み。外部更新保証は勝手に追加しない。同じ root の明示的な再選択で fresh XML を受け入れ、保存後に試聴する逐次導線は維持する。
- 保存公開の複合失敗は主原因・cleanup 原因・残存 staging path を例外の診断情報に保持し、既存の失敗通知経路へ返す。成功通知は公開後のみ。
- コミットはユーザーから許可済み。push、tag、version 更新、公開は行わない。

## Units / ownership / verification

書込み worker は常に一つ。plan-clarifier の点検済み。LR2Config の共有編集を避け、保存公開→正本と試聴 scope の順に handoff する。保存公開の意味は正本方式に依存しない。

### S: BMS-002 保存公開

- Outcome: 既存宛先の保存失敗で旧 bytes を保全し、初回失敗で不完全な宛先を公開しない。LR2 XML と playlist SQL の reader 互換を維持する。
- Writable: `BeMusicSeeker/Models/LR2/LR2Config.cs`、`BeMusicSeeker/Models/Utils/LongPathFileSystem.cs` と必要な小さい共通保存 helper、`BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistBackup.cs`、対応する既存 fixture と保存 helper の必要な fixture、`devdocs/spec/file-db-consistency.md`、`path-length-and-io.md`、`playlist-data-and-export-flow.md`。
- Route: 設定 Save / 検索ルート削除 → LR2Config、playlist backup command → BackupPlaylistAsync → BackupPlaylist → 共通の同一 directory staging と公開。旧直接上書き route は退役する。
- 既存 `LongPathFileSystem` の path / file API を利用する。既存の source 移動・復旧処理と責務が違う場合は、所有 staging を直接公開する小さい処理へ閉じる。
- Quick filter: `FullyQualifiedName~LR2ConfigTests|FullyQualifiedName~PlaylistWorkspacePersistenceCommandTests|FullyQualifiedName~AtomicFile`。helper の最終名に合わせる。

### P: BMS-001 / BMS-003 正本と試聴

- Outcome: 保存した検索ルートと設定を維持し、未保存 draft を試聴が保存しない。開始失敗でも一時値の復元を試み、元の失敗を失わず再試聴可能な終端へ進む。
- Writable: `BeMusicSeeker/Models/LR2/LR2Config.cs`、`LR2body.cs`、同 directory 内の必要な小さい試聴値 snapshot 型。`BeMusicSeeker.Tests/ExternalPlayerProcessGatewayTests.cs`、`SettingsDialogBehaviorTests.cs`、必要な `PlaybackPanelViewModelTests.cs`。`devdocs/spec/external-chart-launch.md`、`settings-change-impact-and-startup-operations.md`、`application-shutdown.md`。composition / View は read-only とし、実変更が必要なら root へ返す。
- 設計: player の保持した LR2Config instance / draft を変更・保存する旧 route を退役する。LR2Config の短い共通保存 lock 内で最新保存 XML を読み、試聴が所有する既存の5項目だけ適用・復元して S の atomic writer から公開する。通常 Save / RemoveAndSave も同じ短い保存排他に参加する。process / window 待ち、UI、通知 callback はこの lock 外とする。恒久的な保存済み文書 cache、version token、汎用 merge は作らない。
- 実入口から必要な temporary state: `PlaybackPanel.StartCommand → Task.Run → LR2body.PlayStart` の起動待ち中にも `MainWindow.ShowSettingsWindow → 設定のnew LR2Config` が可能。disk一時値をdraftへ読み込み、試聴終了後のroot保存で再永続化する経路がある。このため、従来LR2bodyが持つ5fieldの復元値をLR2Config側のpath単位active試聴scopeへ移す。登録は試聴中だけ、内容は5fieldのみ。読み込みは最新diskの非所有値とscopeの保存済み5fieldで構成し、未保存draftを参照しない。通常Save/RemoveAndSave成功後だけscopeの復元値をその保存文書へ更新し、playerの一時書込みとは区別する。終端で登録を解除する。これはP2の到達可能な一時値混入を防ぐ所有境界であり、将来用cacheやrecovery台帳ではない。
- 試聴が所有する項目: `system/windowsize_x`、`windowsize_y`、`screenmode`、`sound/volumemaster`、`volumeflag`。復元値は毎回の開始時に最新保存 XML から取得し、その試聴期間だけ保持。欠落は欠落のまま、元の値は parse fallback から計算せず保持する。開始直後の復元後も終了時の既存復元を維持し、終了時には最新 XML の非所有値を保持する。
- アプリ設定 UI はこれら5値を直接 LR2 XML へ編集する導線を持たない。人工的な「同じ一時値を明示 Save」枝を独立 contract にしない。player 設定の user.config 編集は従来どおりで、未保存 Settings.Default 全体を今回再設計しない。
- Process lifecycle: Start 成功前の例外は未開始 API に依存せず参照・event を片付ける。一時保存に成功した場合だけ復元を試みる。開始後の window/style failure では開始済み process の必要な cleanup と設定復元を独立に試み、終了不能なら既存の process ownership を捨てない。元例外・復元例外・対象 path を既存の例外通知へ届け、保存失敗自体を新しい不要な復元失敗で覆わない。
- Quick filter: `FullyQualifiedName~ExternalPlayerProcessGatewayTests|FullyQualifiedName~SettingsDialogBehaviorTests|FullyQualifiedName~PlaybackPanelViewModelTests|FullyQualifiedName~LR2ConfigTests`。追加実 process / native UI は不要。

## Test necessity / Done when

不具合修正として恒久テストを `既存更新` + `追加`。保存済みデータ保全・開始失敗の外部副作用は継続保証が必要で、現行 coverage に不足する回帰だけを足す。designer が独立 oracle を確定して root 承認後に実装する。

各 unit の focused Quick、最終 snapshot の Functional、fresh read-only static review、`git diff --check`、UTF-8 / LF・参照確認を行い、完了記録を整理してコミットする。review は基準 commit と最終 worktree、ユーザー要件、packet conformance を対象とし、review 中は root の repository 操作を停止する。

Replan trigger: 外部更新保証の追加、未承認の保存順・復旧仕様が必要、所有 path 外の実変更、production ingress から成立しないテスト前提、恒久 state / 汎用 merge / retry の新設が必要、または標準検証の再発 failure。

## 承認済み Test Contract Packet: LR2-SAFE-SAVE-v1

Root approved: 2026-09-08。designer は Phase A で以下の承認済み結果だけから oracle を凍結し、Phase B で production route と fixture の配置を確認した。実装、現行出力、既存 expected、翻訳、snapshot を authority にしていない。

Authority: A1=今回確認・承認した BMS-002 の旧 file 保全と成功偽装禁止。A2=XML は SaveOptions.None・宣言 encoding、SQL は UTF-8 BOM なしで既存 reader の意味的 roundtrip を維持する互換性決定。A3=同一 directory の一意 stage、close 後に既存宛先 Replace / 初回 Move、所有 stage 限定 cleanup、複合失敗の両原因と残存絶対 path を診断へ返す root 決定。A4=両 production consumer を共有 atomic writer に接続し、fault seam は呼出し単位の狭い内部委譲とする設計。

入口 assumption: 有効な LR2 config と保存変更、または非空の playlist store と選択済み backup 宛先。初回保存は SQL backup から到達する。XML の外部削除を試験前提にしない。fixture は固有 directory / DB / handle を所有し、外部 writer 競合は対象外。

| ID | Observable outcome / authority | Production route | 許容差 / 誤実装 / evidence |
|---|---|---|---|
| S1 | stage 途中書込み・公開失敗で既存 bytes 不変、初回宛先は不存在、失敗を返す。A1/A3/A4 | 設定 Save・検索ルート削除 → LR2Config Save / RemoveAndSave、backup picker → BackupPlaylistAsync → writer | wrapper・stage 名・内部配置は自由。直接上書き、公開前削除、失敗後正常 return を識別。writer fault matrix と両 consumer の実 file 失敗を確認 |
| S2 | 成功 XML/SQL が意味的 roundtrip、非 ASCII・宣言 encoding・SQL BOM なしを維持、stage 残留なし。A2 | XML 保存 → LR2 reader、backup → SQL restore / SQLite reader | 意味に影響しない整形・順序・翻訳は自由。空／部分 payload・異なる encoding を識別。独立した小さい入力で確認し GetPlaylistDump 出力を expected にしない |
| S3 | 同一 directory の独自 stage を閉じてから公開、既存 Replace / 初回 Move、所有 stage だけ cleanup。A3/A4 | 両 consumer → writer → filesystem publication | stage の命名規則・symbol・無関係な順序は自由。固定名・共通 temp・close 前 publish・glob cleanup・delete+move を識別。境界状態観測と実 filesystem で確認、source/private call order assertion なし |
| S4 | 主失敗と cleanup 失敗の両方が例外 graph に残り、残存 stage 絶対 path を caller の診断へ返す。SQL は Error、XML は成功扱いしない。A1/A3/A4 | writer → caller exception → 既存設定失敗 / playlist notification | 階層・診断文言・改行・翻訳は自由。finally による主失敗上書き、cleanup 隠蔽、path 消失、成功通知を識別。複合 fault と severity / caller propagation を確認 |

### Coverage ledger / evidence

- S1–S4: 新 `AtomicFileWriterTests`（実 owner 名へ適合可）。共通保存 lifecycle を XML/SQL fixture へ重複しない。呼出し単位の fault delegate と固有 temporary directory、通常 Functional、新 DNP なし。同期 return/throw、write-close-publication 状態を完了 signal とし時間待ちなし。
- XML: `LR2ConfigTests` へ extend。Save と RemoveAndSave の実 file 接続、意味的 readback、failure。直接保存 route を退役する。既存 cleanup の失敗握り潰しを新 case に広げない。
- SQL: `PlaylistWorkspacePersistenceCommandTests` へ extend、成功 case の GetPlaylistDump 自己比較は semantic roundtrip へ replace。既存 factory、固有 SQLite DB/directory、await BackupPlaylistAsync と receipt を完了 signal とする。無関係な warning/restore coverage を維持。
- 共通 IO: `ResilientFileMutationServiceTests` の既存 coverage を利用。新 publish API の独立 coverage が不足する場合だけ extend。既存 source 移動契約は変えない。
- Quick は S unit の filter に `AtomicFileWriterTests` を使用。共通 IO を変更した場合は `FullyQualifiedName~ResilientFileMutationServiceTests` を追加。Functional は root 統合時。
- 回帰の base-fail/head-pass が実入口から決定的に得られる場合は実施。新 seam でしか確認できない場合は、正常系だけでは直接上書きを見逃す具体的識別力リスクに対し、宛先へ直接書き途中 throw する wrong variant 一つで S1 が落ちることを確認し、必ず戻す。compile/setup failure は red に数えない。通常の fault matrix と mutant は区別する。
- bytes 完全一致は A1 の保全契約そのもの、encoding/BOM 限定検査は A2。golden/current output を使用せず、契約変更時にだけ退役する。reflection/source text/exact localized copy/broad snapshot/characterization は使用しない。
- worker は fixture 名、data builder、assertion API、内部委譲 mechanics を適合可能。required outcome / allowed variation / authority は変更不可。追加 rollback/retry を要件へ加えない。新 owner fixture を作る場合は spec の Verification map を更新する。

## 承認済み Test Contract Packet: LR2-PREVIEW-v1

Root approved: 2026-09-09。designer は root の要件・承認済み再現・scope 決定だけで Phase A oracle を凍結し、Phase B で既存 route / fixture を確認。current implementation / expected / runtime output / 翻訳 / snapshot は oracle として使っていない。

Authority: root 承認済み BMS-001 / BMS-003 と本計画 P unit の observable outcome、設定 spec の同一 root 再選択後 fresh 保存契約。最初の一時保存失敗は atomic 保存で未変更のため不要復元を行わないという root 決定を含む。採用 contract に authority gap はない。任意の owned 5 field の UI 直接編集は実入口がないため除外した。

入口 assumption: 有効な LR2 layout / XML / 譜面、実 process/window 境界だけを既存 gateway/host fake へ置換。設定画面表示は playback surface を覆うが、`MainWindow.ShowSettingsWindow` 自体は process を止めない。試聴中 Save は現行で受理される設定 / main-window root 保存入口を使用する。任意の外部同時 writer は対象外。

| ID | Observable outcome | Production ingress / route | 許容差 / 誤実装 / evidence |
|---|---|---|---|
| P1 | 保存済み追加・削除 root / 非所有値を起動直後・終了・開始失敗後も維持 | 設定 Save / main-window root 追加削除 → LR2Config 保存 → 保持中の同 player PlayStart / 終了。明示 root 再選択 fresh XML 保存も互換条件 | XML 整形・属性順・内部 owner・root 順序は固定しない。古い player 文書の全文再保存を識別。player 生成→root追加保存→試聴→root削除保存→終了と開始失敗を意味的 readback |
| P2 | 未保存 XML draft を永続化・消去せず表示編集・Cancelを維持 | 設定 root 編集 → 未保存 draft → 試聴 / 終了 → Save または Cancel | draft保持方式・通知回数・画面構成は自由。shared mutable XMLの暗黙Save / draft消去を識別。実編集保存取消とdiskを確認、object identity固定なし |
| P3 | 試聴用5fieldを開始時の保存済み値・欠落へ戻す。非所有値は後続成功Saveを保持 | PlayStart → process/window → 正常 return / 終了 / Start throw / wait-style failure | 保存回数・復元実装は自由。未承認default追加・部分復元・終了時だけ復元を識別。入力で独立に選んだ5値と欠落をP1/P4ケースへ併合 |
| P4 | 一時保存後のStart失敗で復元・未開始参照cleanup・元失敗保持・次回試聴。開始後failureでも復元を試み、終了不能processのownershipを維持 | playback→一時保存→Start/window。既存close/pause/restart入口 | wrapper・内部cleanup順・ownership表現は自由。未開始HasExited/Close/Killで主失敗を隠す、古いsession残留、終了不能session放棄を識別。gateway faultと次の明示再生/Closeで確認。最初のSave失敗で不要復元なし |
| P5 | 元失敗・復元失敗・対象config pathを既存error通知へ返す。成功偽装/自動retryなし | playback開始失敗→復元失敗→PlaybackPanel既存失敗通知 | 例外階層・表示文言・改行・翻訳は自由。復元だけthrow・握り潰し・path欠落を識別。隔離fileへの復元failureとStart failure、例外graph/通知引数で確認 |

### Coverage ledger / evidence

- P1/P3/P4/P5: `ExternalPlayerProcessGatewayTests` extend。既存 `Lr2ReportsFailureWhenProcessExitsDuringWindowStyleApply` の XML substring 比較は semantic assertion へ replace。個別 temporary directory と既存 recording gateway/host/settings、remaining Functional、新 DNP なし。PlayStart Task/同期throw、明示Exited、Closeの終端がsignal。timeout policyはtimeout自体のcaseのみ。
- P1/P2: `SettingsDialogBehaviorTests` extend。既存の独立 settings session / LR2 layout / harnessで実Save/Cancel・root操作とdisk readback、共有Settings.Defaultの追加変更なし。既存保存coverage維持。必要なharness拡張は最小化する。
- P4/P5 caller: `PlaybackPanelViewModelTests` の既存通知/再試聴 coverage を利用。実LR2由来複合失敗接続が不足するときだけextend。既存DNP / serial-state-bを維持。fake playerだけでLR2全体を保証した扱いにしない。
- `LR2ConfigTests` はS unitの保存coverageを再利用し、Pから原則追加しない。LR2Config層だけのgreenでsettings/player接続を代替しない。
- 旧routeで実行可能なroot巻戻しとStart failure残留のregressionはbase-fail/head-passを実施する。構造変更で実行できない場合、必要な識別力確認に限り「後続Saveを無視して開始時全文書へ戻す」wrong variantを一つ実行しP1が落ちることを確認して戻す。compile/setup failureはredに数えない。通常fault injectionとmutantは別。
- 入力の値・欠落・pathを意味比較し、XML全文snapshot / source / reflection / localized copyを固定しない。新しいnative UI / process / 固定sleepは不要。WPFが必要なら既存TestUiDispatcherHostを使う。
- workerはinput builder・fake callback・通知signal・assertion mechanicsを適合可。oracleは変更不可。新しい外部writer保証、復旧仕様、productionから到達しないstateが必要ならrootへ返す。

### P2 到達経路の追加確認（oracle 変更なし、root / designer 承認済み）

起動待ち中の設定 open → draft 作成 → 試聴終了 → その同じ draft で root 編集・Save により、試聴一時値を再永続化しないこともP2に含む。SettingsDialogBehaviorTests の既存P2ケースへこのtimelineを追加し、実Save後のrootと5fieldを意味比較する。process/window境界signalで一時公開後を観測し、coordinatorはsignalをawait、固定waitなし。Save直前にdraftを作り直して誤実装を隠さない。constructor単体や人工的XDocument編集で置き換えず、active scope登録数/private stateもassertしない。fresh disk読込み＋開始/終了の5field復元だけではこのcaseを満たせない。既存red方針を維持し、この補足だけを理由に追加mutantを要求しない。

## 検証・引継ぎ記録

- S 初回 handoff の Quick: `artifacts/verification/tests-quick-20260909-003013/functional/results.trx`、76成功 / 4skip / 失敗0。skipは既存の跨volume fixture4件。実行3.8秒。
- S 反復中の失敗: `002020` は XML declaration の文字列先頭/BOM仮定、`002514` は一時file名の仮定とその検索結果。contractが許す書式・命名の差をテストが過剰固定していたため、rootはsemantic reader、実path/残存files観測へ適合するよう引継ぎ修正を指示。production failureのredとしては扱わない。
- S の `002226` wrong variant は例外identity assertionで失敗しており、S1のredには数えない。修正後の `004012` は直接上書きvariantを `CollectionAssert.AreEqual` のbytes不一致（index 0）で検出した。variantを撤去し、`011558`で76成功 / 4skip / 失敗0を確認。
- S引継ぎ修正: 同fixtureのcleanup失敗を隠さない、AtomicFileWriter fixtureのVerification map追加、変更APIのXML docs補完。P実装workerが同じ所有unitの差分として完結し、関連Quickで確認する。
- P の `012741` は開始時の全文へ戻すvariantにより、終了後に削除済みrootが復活して `Lr2PreviewUsesTheSameDraftForRootSaveAfterStartupBoundary` の `Assert.IsNull` が失敗。variant撤去後の `012825` は同test成功。設定fixtureの `012101` は25成功、既存PlaybackPanel fixtureの `012315` は52成功。
- 複合Quick `011005` は共有WPF host初期化の `ThemeManager` 例外で失敗。workerの調査では、既存 `SettingsDialogBehaviorTests.FailedNormalSaveRetainsDraftUntilUserCancelsOrRetries` の Cancel → ResetSettings → AppThemeService.ApplyTheme と、Playlist fixtureによる共有STA host初期化との競合。今回のP2 harness追加はWPF Application/resource/STA hostへ到達しない。laneやworker数を変更せず、標準Functionalで統合確認する。共有test infrastructureの修正は今回に混ぜない。
- 最新P関連Quick `014344`: ExternalPlayerProcessGatewayTests / SettingsDialogBehaviorTests / LR2ConfigTests、71成功 / 失敗0。実装変更APIのXML docs、診断文と日本語spec、UTF-8 / LF、diff checkの引継ぎ修正も完了。
- 標準Functional: `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional`、`artifacts/verification/tests-functional-20260909-014917`。locked restore、tool restore、書式検査、analyzer（指摘0）、build、全6host成功。テスト実行201.3秒（180秒target超過、300秒budget内）。合計4651件中4640成功、失敗0、既存条件付きskip11。Full / 実LR2プロセス確認は今回の変更分類では要求せず未実施。
- 初回凍結static review: production blocking regressionなし。acceptance-blocking P2が2件。P1のplayer生成後・起動前の別設定instanceによるroot追加保存と、P3の起動直後/終了後disk全5field readbackが不足。承認済みoracleを変えず既存fixtureを補完する。production/runner/lane前提は変えず、filtered Quickとfresh reviewで確認する。
- Review修正: Settings既存caseにplayer生成後の別harnessからのmain-window root保存、起動直後/終了後のroot保持を追加。External既存caseはready/exitとも全5値・欠落をdiskで確認。`020411`の両fixture Quickは49成功 / 失敗0。修正はtestとVerification mapだけでproduction/統合lane前提を変更していないため、Functional再実行は不要と判断。変更18fileのUTF-8 / LFとdiff check成功。
- Fresh static review: 前回P2の2件を解消、blocking findingなし。production/runner/lane不変に基づくFunctional再実行不要の判断も確認。2026-09-09、BMS-001 / BMS-002 / BMS-003の受入完了。現行契約は対応する `devdocs/spec`、本記録は採用判断・独立oracleと固有検証証跡を保持する。
