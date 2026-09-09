# AUD-01 宛先型衝突の修正

完了記録（2026-09-09）。現行の挙動契約は [file-db-consistency.md](../spec/file-db-consistency.md) を正本とし、本書は当時の判断・独立テスト契約・固有の検証証跡を保持する。

## Goal / Context

同梱通常ファイルが同名の既存ディレクトリを置換し、成功時 cleanup で内部資産を恒久削除する問題を修正する。基準 HEAD は `5277e3dcf39e258f1905bcbf0f7dff637f674f31`。開始時の作業ツリーは clean。

ユーザーは参照会話の修正計画を参考に修正すること、英語 `docs/manual.md` も修正すること、commit まで進めることを承認済み。仕様の曖昧さはユーザー質問とする。参照計画のマニュアルは `docs/manual.ja.md` なので両言語が対象であり、再質問は不要。

## Decision list / Constraints

1. ファイル／ディレクトリ型衝突は両方向、スマート上書き ON/OFF に関係なく、変更前にパッケージ全体を拒否する。
2. source・宛先・対象 DB・保留一覧を保全し、成功後 cleanup／成功数へ混ぜない。独立した後続パッケージは継続する。
3. マージは現在の選択 source フォルダーから宛先への一確定単位を維持する。単一の detached package に含まれる対象全体を先行検証する。
4. 既存の採番、同型置換、除外規則を維持する。検証と実行の列挙／宛先算出を共用する。新しいコンポーネント改名を設けない。
5. executor は計画全体の先行検証と退避直前の検証を行う。実施済み操作に対する既存の補償／cleanup／durable failure の意味を維持する。
6. 事前拒否の分類は外側 read-only 検証だけの catch で行う。実行中の同型例外や補償失敗を無変更拒否へ変換しない。
7. 事前拒否の一時的な結果情報を既存の結果・terminal 経路へ運ぶ。Warning / OK を一操作一回、lease・DB・lock 解放後に表示する。先頭5件と残件数、全件ログ、成功時のみ別成功数を示す。cancel 既検出情報を保持し shutdown モーダル抑制を維持する。
8. 新しいロック、永続状態、監視、retry/replay/rollback、一般 FS 抽象、schema、version 更新は対象外。AUD-02以降は修正しない。既存の変更受付、追加 ZIP queue、設定画面、確定済み表示閲覧、取消・終了の境界は変えない。
9. `LongPathFileSystem` と `IFileMutationService` を再利用する。多言語 UI は resx / Resources.cs / 全6言語を揃える。

## Reachability / impact

- 保留 UI → `PendingPackageWorkflowOwner` → `BMSLibrary.InstallPendingPackagesToEstimatedDestinationsWithReceipt` → `installChartPackages` → `InstallPackagesWithFileMutationReceipts` → `MovePackageFilesWithReceipt` → `FileDbMutationExecutor`。
- 自動導入 UI → `PackageInstallWorkflowOwner` → `InstallChartPackagesAutoWithProgress` → 同共通サービス。既存の宛先採番は継続する。
- 重複 UI → `DuplicateMaintenanceWorkflowOwner` → `LibraryFileOperationOwner.MergeChartDirectory` → 一つの `DetachedMergePackage` → 同共通サービス。
- 正常解析可能な未所持譜面と通常ファイル `BGA`、既存宛先 `BGA` ディレクトリの組合せで到達する。外部競合や破損入力は不要。保全対象は実 filesystem、DB 登録、一覧／成功状態。
- executor の実行時検査は参照計画で明示された安価な最終防御であり、外部変更の包括的保証へ拡張しない。

## 変更分類 / ownership / verification

不具合修正。恒久テストは既存更新＋追加を必要と判断する。データ消失を既存 suite が検出しておらず、実入口・executor・通知の保全保証に保守負担に見合う効果がある。独立 `test-contract-designer` が AUD01-v1 を設計して root が承認した後、一つの `implementation-worker` へ委譲する。

worker は service / executor / 実入口 / 直接通知 consumer / 対応結果型 / UI リソース / 対応テストを単一 vertical slice として所有する。root はこの計画、参照記録、`docs/manual.md`、`docs/manual.ja.md`、`devdocs/spec/file-db-consistency.md` を所有する。書込み worker は一つ。両者は相互の変更を破棄しない。

接続確認で判明した既存直接 consumer `MainWindowPendingPackageMutationViewTerminal` と対応既存 fixture も、AUD-01 terminal 報告に必要な限定範囲を worker 所有へ含める。owner と terminal で二重表示せず、既存の terminal に一本化する。

candidate fixture は `BmsLibraryPackageInstallServiceTests`、`BmsLibraryDuplicateServiceTests`、`ResilientFileMutationServiceTests`、`FileDbMutationReportTests`、直接 workflow の既存 fixture、`LocalizationResourceParityTests`。実 temp filesystem / DB、既存 fault injection と async completion を使い、sleep や実モーダル表示は使わない。

実装前に再現用テストを追加し、少なくとも smart ON/OFF・後半衝突・executor 前半無変更の red を記録する。関連 filter の `verify-refactor.ps1 -Mode Quick` で反復する。統合時は参照計画の `-Mode Full` を一回実行し、内包する Functional を別途重複実行しない。timeout／failure は testing-strategy に従う。最終 `git diff --check`、UTF-8 / LF、参照、文書と実装の整合を確認し、凍結 snapshot の `repo-static-review` を行う。review 中は root も repository 操作を停止する。

## Replan triggers

- 読み取り専用検証の前に不可避な業務変更があり、既存 owner の移動だけでは閉じない。
- 保全／success／cancel／shutdown を守るため未承認の persistent state または新しい recovery が必要。
- 既存採番／除外／確定単位との observable semantics が決定リストと両立しない。
- 独立 oracle の実入口が存在せず fake-only な新保証になる。
- owner 外の実装変更やテスト基盤修正が必要。worker は編集を止め root へ根拠を返す。

## Done when

決定リストを実装し、型衝突の事前拒否で元・宛先・DB・一覧が保全され、独立した成功処理が継続する。executor の復旧エラーを格下げせず、Warning は資源解放後に一回。両マニュアルと現行 spec を更新し、Quick / Full / fresh static review が完了する。commit は root が行い、push はしない。

## Test Contract Packet

AUD01-v1: `test-contract-designer` が oracle-first で設計し、root が承認済み。Phase A はユーザー要件・参照計画 §§1–9,11 のみを根拠とし、実装／既存 expected／翻訳／出力を未読で凍結した。Phase B は到達経路・fixture の接続確認に限定した。

| ID | 必須 outcome / authority | wrong implementation / evidence | 配置 |
|---|---|---|---|
| P01/P02/P04 | 正常解析・未所持・現在の保留対象を smart ON/OFF 両方で全 package 事前拒否。正常譜面と BGA ファイル、宛先 BGA 内複数 sentinel、対象 DB、pending/installed 状態を保持。計画 §§1,2,4,5,9 | smart skip の source 削除／部分導入／成功除去を区別。実 FS・SQLite・一覧の base-fail/head-pass | BmsLibraryPackageInstallServiceTests extend |
| P03 | 明示順序の正常候補→衝突候補でも、変更・commit せず全体保持。計画 §§2.2,4.2,9.1 | per-item 検査と即変更／後補償を区別。production 共用の自然な internal 候補 seam と実入口保全 case を対応付け、OS 列挙順を仮定しない。base-fail/head-pass | 同上 |
| P05 | 選択 source→dest の既存一確定単位全体を拒否、全 component・登録・パス・統合状態保持。計画 §§2.2,5 と root decision | source 登録先行削除／正常分のみ統合を区別。実 merge＋SQLite。P06 の人工 multi-source は作らない | BmsLibraryDuplicateServiceTests extend |
| P07 | 独立 A/B/C の A/C 成功、B 拒否保持、成功2・拒否1。計画 §§2.2,8,9.1 | batch 全体中止／全選択除去／B成功加算を区別。DB 全体不変は要求しない | BmsLibraryPackageInstallServiceTests extend |
| P08 | 採番後の実宛先が通常ファイルなら予定ディレクトリを変更前拒否。計画 §§2.1,2.4,4.1 | root型未検査／採番前検査を区別。明示先逆衝突と既存採番正常系 | 同上 |
| E01/E02/E03/E04 | executor plan 全体変更前に両方向型衝突を拒否。存在／置換 flag で迂回しない。専用 IOException は source・実宛先・予定型・既存型を保持。計画 §§3.1,6.1,9.2 | 後半まで前半 stage／backup後検査／flag迂回を区別。明示順 plan＋recording mutation adapter＋commit観測。E01/E04 base-fail/head-pass | ResilientFileMutationServiceTests extend |
| E05/E06/E07 | 検証後変化は退避前停止、未実施を実施済みにせず既存一回補償。補償失敗では primary/recovery/path 維持。計画 §§6.2–6.4,9.2 | false receipt 削除／補償省略／Warning格下げを区別。実FS委譲 IFileMutationService の決定的 hook、sleep不使用 | 同上 |
| U01/U02/U03/U04 | 操作終端、lease・DB・lock解放後の Warning/OK 一回。理由・対象・拒否数・source/dest/conflict、全拒否成功案内なし、混在のみ成功数。計画 §§7.1–7.3,8 | per-package modal／lock保持通知／無条件成功／強制続行を区別。実 model 通知時 admission/writer/既存DB probe と owner gate/activity 解放、OnMessage | PendingPackageWorkflowOwnerTests / PackageInstallWorkflowOwnerTests / DuplicateMaintenanceWorkflowOwnerTests の必要な既存fixtureをextend |
| U05/U06 | 詳細5件＋残数、全検出log、通常cancel既検出保持、shutdown新modalなし。計画 §7.4 | 6件目以降facts/log欠落／cancel紛失／遅延modalを区別。明示cancel/shutdown signal | 同workflowとFileDbMutationReportTests extend |
| U07 | 通常拒否は外側read-only検査のみ。executor／補償失敗は既存Error/復旧結果を維持。計画 §§6.4,8 | broad catch／inner例外探索による格下げを区別。E05/E07結果＋owner reporting | 同上 |
| R01/R02/R03 | 同型・既存smart/置換許可/採番・不存在正常系維持。計画 §§2.1,2.4,9.3 | 全既存先拒否／採番前拒否／独断改名を区別。近傍の既存tests再実行、expected写経なし | 近傍既存coverage変更なし |
| L01 | accessor/resx/6JSON key parity・non-empty・format parse・path/count/type引数の保持。AGENTS多言語契約 | 言語欠落／placeholder欠落を区別。schema/format semantics | LocalizationResourceParityTests extend |

Allowed variation: 翻訳・句読点・改行・内部 symbol/構造、OS atime、読み取り解析・進捗・診断、非契約順序。最初の衝突で検証終了可。業務データの変更や拒否単位の変更は不可。E 系は既存補償の best-effort 残留リスクを維持する。

Coverage はすべて behavior（L01 は resource schema）。exact copy / snapshot / source / reflection / characterization 例外なし。test ごとの既存 temp FS/SQLite helperを使い、model return、returned Task、completion/failure event＋queue idle を完了signalにする。watchdogのみtimeout可。既存laneを維持し、追加DNPなし。退役testはなし。追加targeted mutant義務はなし（指定redとfault injectionが識別する）。compile/setup/解析失敗はredに数えない。

P03で base にseamがない場合は既存executable seamでbaselineの変更を観測し、実現不能ならrootへ返す。workerのmechanics変更は許可するが、新test-only public API／FS基盤／永続stateは不可。oracle変更、fake-only到達、所有外修正はNEEDS_ROOT_INPUT。

Quick filter（必要な層へ絞る）: `FullyQualifiedName~BmsLibraryPackageInstallServiceTests|FullyQualifiedName~BmsLibraryDuplicateServiceTests|FullyQualifiedName~ResilientFileMutationServiceTests|FullyQualifiedName~FileDbMutationReportTests|FullyQualifiedName~PendingPackageWorkflowOwnerTests|FullyQualifiedName~PackageInstallWorkflowOwnerTests|FullyQualifiedName~DuplicateMaintenanceWorkflowOwnerTests|FullyQualifiedName~LocalizationResourceParityTests`。

## 検証記録

- 設定fixture隔離は独立commit `4e8ee15a`（`PlaylistUrlCompletionTests.cs` のみ）として記録した。
- fresh再reviewは前回5findingの解消を確認し、blocking findingなし。修正差分と直接影響するproduction/test route、全言語resource、AUD01-v1・specの整合をread-onlyで確認した。最終review後は本完了記録だけを更新し、production/test差分は検証済みsnapshotを維持した。
- review修正後Functional `tests-functional-20260909-144501` はexit 0。format、analyzer診断0、buildエラー0、全6testhost成功。test execution 218.3秒（300秒制限内、180秒target超過）。配布前提は不変で既存Full受入を維持し、5finding修正と直接影響範囲をfresh再reviewへ渡す。
- 5件のreview修正を統合。cleanup専用resourceを全言語へ追加し、混在Warning/Error・復旧要約を維持。詳細pathなどを項目ごとに制限し、5詳細・残件数・復旧情報を同一通知へ収めた。未使用gateのtestは実pending owner→terminalと同じgate/activityのprobeへ置換し、翻訳固定assertionをresource選択/引数のsemantic検証へ置換した。関連Quick `tests-quick-20260909-143654`: 312成功・4スキップ、21.7秒。targeted negative control `tests-quick-20260909-142940`: `ValidatePlan` を `Stage` 後へ一時移動するとE01–E04が4件全て失敗し、変更操作数3/3/3/6を検出。直後に正規順序へ復元済み。UTF-8/LF・XML/JSON・diff check成功。
- 初回fresh static reviewは5件のblocking findingを返した。型衝突と混在するcleanup failureの通知欠落（P1）、4096文字切詰めによる詳細・残数・復旧情報の欠落（P2）、変更前検査をstaging後検査から識別できないtest（P2）、production ownerへ接続されないgate probe（P2）、翻訳文言固定assertion（P2）。いずれも既存AUD01-v1 E01–E04/U01–U07とdecision 5/7の適合修正とし、oracleは変更しない。rootはreportと既存関連fixtureのbounded修正を元workerへ委譲。関連Quickとtargeted negative controlの後、通知実装が変わるためFunctionalを再実行する。配布・更新の前提は不変なのでFullのrelease laneは再実行せず、fresh reviewを行う。
- Full `tests-full-20260909-135755` は exit 0 で完了。format、Roslynator（診断0）、build、Functional、tool smoke、配布生成、既存データ起動、現行更新、旧v2.1.6.0移行、ProcessIntegration（52成功・2スキップ）、ReleaseAcceptance（2成功）が通過した。静的reviewへ渡すsnapshotをここで凍結する。
- 共有設定競合の独立unitは、`PlaylistUrlCompletionTests` の設定providerを既存helperでtestごとの一時pathへ接続して解消した。assertion semanticsは不変。Quick `tests-quick-20260909-135611`: 25成功。統合Full `tests-full-20260909-135755` のFunctionalは全host成功、実行213.3秒（300秒制限内、180秒reporting target超過）。配布受入結果は後記する。
- 実装委譲を完了。最終関連Quick `tests-quick-20260909-134013`: 309成功・4スキップ、filtered build+test 25.4秒。report/resource Quick `tests-quick-20260909-133855`: 24成功。UTF-8/LF、RESX/JSON、`git diff --check` 成功。隔離base worktreeは削除済み。
- 接続配置は、全canonical入口が使う `MovePackageFilesWithReceipt` のread-only検証だけを囲むcatchへ集約した。既存呼出元のresult/receipt経路を再利用し、`BMSLibrary.PackageInstall.cs` / merge入口へ重複検査を追加しない。通常事前拒否とexecutor失敗をimmutable receiptの明示factsで区別する。rootは実入口テストと引継ぎから、この配置上の変更を承認した。挙動契約は変更していない。
- Full初回 `tests-full-20260909-134311` は新規例外コンストラクターの開始braceのindent 1箇所でformat検査失敗。rootが機械的修正し、`tests-full-20260909-134420` で再実行。format / Roslynator（診断0）/ build（エラー0）通過。機能・配布検証とfresh reviewの結果は追記する。
- 同FullのFunctionalで `PlaylistUrlCompletionTests.BMSPlaylist_StellaFullSettingDisablesFetchAndClearsStellaRuntimeCompletion` が共有 `config/user.config` のread/write競合で失敗。`remaining/results.trx` は3037成功・1失敗・8スキップ。timeoutではないため単純retryせず、設定をlazy readする `new Settings()` のfixture所有を調査し、assertionを変えないtest-only隔離修正を独立unitへ委譲した。並列度や時間制限は変えない。この修正は別commitとする。
- 作業中に別変更として `b2d45a0c`（`.codex/config.toml` のみ）がcommitされた。今回の差分で変更・破棄せず保持する。再現用baseline `5277e3d` のproduction codeとの同一性は維持されている。

- 初回 `tests-quick-20260909-121305` は新専用例外型の未定義による CS0246。behavior red として扱わない。
- 隔離 base `C:\work\BeMusicSeeker-decomp-aud01-base`（`5277e3d`）へ追加テストのみを適用し、新専用型への assertion 1行を除いて実行。本番 worktree の差分は戻していない。
- base Quick filter: `FullyQualifiedName~MovePackageFilesWithReceipt_RejectsBundledFileWhenDestinationIsDirectory|FullyQualifiedName~BuildComponentMovePlan_ExplicitOrderedCandidatesRejectWholePackageBeforePromotion|FullyQualifiedName~FileDbMutationExecutor_RejectsFileDestinationWhenExistingDirectory|FullyQualifiedName~FileDbMutationExecutor_RejectsWholePlanWhenLaterDestinationTypeConflicts`。
- base artifact: 隔離 worktree 内 `artifacts/verification/tests-quick-20260909-123256`。5件全てが期待する Failed に対して Completed となり失敗。P01/P02/P03/E01/E04 の behavior red。root 承認済み。
- 途中 head Quick `artifacts/verification/tests-quick-20260909-122810` は176成功・4スキップ。最終 acceptance は実装終了後に実行する。
- 関連8fixture Quick `tests-quick-20260909-124159`: 298成功・4スキップ、17.83秒。script末尾はroot参照doc EOF空行で失敗し、同空行は修正済み。
- 追加 Quick `tests-quick-20260909-125635`: 9成功。実保留 A/B/C の対象別 FS/SQLite/一覧保全、実merge拒否、逆型衝突、P01/P02/P03/E01/E04、gate解放後Warningを確認。U06/U07と最終関連Quickはこの時点では継続中。
- 追加 Quick `tests-quick-20260909-125929`: 13成功。U06 cancel/shutdown、U07 executor専用例外の通常Error、pending terminalでgate解放後Warning一回、既存receipt報告経路を確認。
- 追加 Quick `tests-quick-20260909-130620`: 5成功。E02/E03/E05/E06/E07。逆方向衝突、backup flagによる迂回不可、全体検証後の型変化、先行promotionの一回補償、補償失敗のManualRecoveryとrecovery pathsを既存I/O境界で確認。
