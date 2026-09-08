# R2: 保留推定導入の確定実績による逐次判定

## Goal / Context

実際には導入しない先行パッケージのハッシュ予約で、後続の譜面を除外する問題を修正する。
開始 HEAD は `f7eba67d`、開始時 worktree は clean。ユーザーは参考会話・計画を現在の HEAD で再検証した実装と commit を承認している。push・公開は含まない。

参考計画の対象 `924a611f` 以降の変更でも、service の事前予約と導入先 group 実行は残っていた。直近の登録 BMS ルート取り込み除外修正は維持する。

実経路は `Views/MainWindow.ManualInstallSelectedPendingChartsAsync` → `PendingPackageWorkflowOwner.InstallPendingAsync` → store の `ManualInstallPackagesWithReceipt` → `BMSLibrary.InstallPendingPackagesToEstimatedDestinationsWithReceipt` → 共通推定導入入口 → `BmsLibraryPackageInstallService` → FS/DB receipt。UI capability は DST 更新操作の可否であり、全 DST 空の選択もこの経路へ届く。外部変更や並列 mutation は再現の前提ではない。

## Decisions / Constraints

1. 共通モデル入口の既存 estimate/model guard 内で現在の保留との照合、入力順の重複排除、全有効譜面 DST 空の候補除外を行う。guard を解放してから適格候補だけの component 情報を取得する。除外対象の source・保留行・DST・warning は変更しない。
2. 各パッケージを入力順に分類・実行・確定する。所持判定は開始時所持と先行の実 commit `AddedEntries` だけ。既存 `PrimaryHashGuardLookup` を一つ共有し、低層の確定時追記を唯一の更新箇所にする。
3. パッケージ内の同一 hash は局所的に抑止する。未確定の局所重複に `AlreadyInstalled` を新規付与しない。部分 DST 空は未所持対象の DST が非空・一意なら実行できる。実行不能候補から実績を増やさない。
4. 後続が全件既所持になれば通常の resource-only / cleanup-only 条件をその時点で評価する。source 削除設定の意味は変更しない。
5. private install executor は既存 `PackageInstallExecutionResult` を返し、推定導入は一件の実 receipt に基づき成功を判定する。他 caller は必要な `FailedPackages` を参照して契約を維持する。失敗集合の補集合で成功を作らない。
6. 補償済み独立失敗では後続を評価する。cleanup だけの失敗では実績を保持し結果へ残す。手動復旧・durable finalization 失敗では停止し、未着手 suffix の source・保留・DST を維持する。結果不明の例外から継続しない。
7. 既存の操作全体 lease、短い model guard、バッチ単位の maintenance / collection / 通知境界、BMS/BMSON primary hash 規則を維持する。新しい scheduler、永続状態、retry/replay/rollback、汎用 owner は追加しない。R1/R3/R4/R5、他導入経路の復旧設計は対象外。

## Unit / ownership / Done when

単一 implementation worker が次を一つの vertical unit として担当する。

- `BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/PendingInstallBatchPlan.cs`、`PendingInstallBatchItem.cs`、`PendingInstallBatchGroup.cs`、必要な近傍推定導入 contracts / classification
- `BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs`（必要な同契約の既存 fixture は root と ownership を調整）
- `devdocs/spec/install-estimation-current-logic.md`、`devdocs/spec/workflows.md`、`docs/manual.ja.md`

root はこの計画・packet・統合・最終検証・review・commit を担当する。他 worker の同時書込みはない。旧 group 型、事前完成 work plan、予約分類、不要になった未着手補集合補正を同 unit で退役し、consumer と metrics を揃える。禁止 route の文字列テストは追加しない。

変更分類は observable behavior の bugfix。恒久テストは既存更新・追加・旧構造 assertion の置換が必要。保守価値は FS/DB への導入漏れと誤った保留解除の再発防止にある。既存の group 形状テストだけでは実行失敗後の判定を保証できない。

plan-clarifier の点検は完了、ユーザーへの未決質問なし。独立 designer の packet を root 承認して実装した。canonical fixture は model 導入、resource-only BMSON、guard 解放、実 FS failure helper を持つ既存 `BmsLibraryPackageInstallServiceTests`。期待値を旧 group・予約挙動から作っていない。

標準検証は関連 filter の Quick、最終 snapshot の Functional 一回、fresh read-only static review、`git diff --check`。空 DST 先行と共通譜面を持つ後続について、修正前の model 経路で FS/DB の red を確認する。Full は release / distribution を変更しないため対象外。

replan trigger: 到達経路の前提不一致、既存補償・通知 ownership を維持できない場合、source 削除意味や receipt の observable semantics の変更が必要な場合、所有外の修正が不可欠な場合。テストを green にするために期待値を変更しない。

## 承認済み Test Contract Packet: R2-20260908

root 承認済み。独立 designer が Decisions 1–7 と既存 FS/DB failure 契約から oracle を先に凍結し、その後に実装と既存テストを到達性・配置のためだけに確認した。旧予約/group の prose・expected・翻訳・現在 output は authority にしていない。下表の P は Goal に記した本番経路を指す。入口 assumption は単一プロセス、exclusive DB、既存 mutation lease。全件空 DST は通常 UI 選択から到達する。

| ID | Authority / route | Required outcome | Allowed variation / wrong implementation | Evidence |
| --- | --- | --- | --- | --- |
| R2-01 | D1 / P の共通 model 準備 | 現在保留照合・入力順 dedup。全有効 entry DST が null/empty/whitespace の候補は副作用前に除外。source/保留DB/DST/warning不変。非保留も不変 | 内部表現・診断文言は可。setterが空表現を正規化する場合は等価入力統合可。warning更新後skipや全件component取得は誤り | model の実source/DB/enum assertion。component非列挙は専用test APIを作らずstatic review |
| R2-02 | ユーザー不具合、D1–2 / P→実FS/DB | A(h)全DST空→B(h,h2)有効で、B両譜面がdestinationとDB/catalogに存在。A不変。B保留解除はdurable receiptと一致 | fixture名・実parserによるhash導出は可。A予約でBのhだけ欠落は誤り | 修正前model回帰のbase-failと修正後head-pass。件数だけでなく両path/identity |
| R2-03 | D2–4 / P→一件分類 | 所持は初期＋先行commit AddedEntriesのみ。既所持行空DSTは新規targetの非空一意DSTを妨げない。新規targetのDST不足/不一致skipは実績を増やさない。local duplicateは二重導入せず未確定AlreadyInstalled新規付与なし | local代表は互換条件内で可。全entry一致要求・skipから実績追加は誤り | mixed DST、成功先行→後続mixed、local duplicate失敗のFS/DB/warning確認 |
| R2-04 | D2,6 / P→逐次receipt | A(D1)→B(D2)→C(D1)でB停止ならA確定保持、Cのsource/保留DB/DST/warning未変更 | private call/log順は固定しない。A→C→Bへのgroup並替えは誤り | ManualRecovery caseと統合しC未着手を実FS/DBで確認 |
| R2-05 | D2,5–6、FSDB-FACTS / P→DB failure→補償 | A確定前失敗・補償完了ならA保留維持、Bの同hash導入可。失敗/未着手を成功へ含めない。成功は対応durable receipt | failure文言は可。試行hash追加・failed補集合成功は誤り | 所有DBのpath限定INSERT triggerと実executor、A補償/DB不在、B導入。receiptなしを架空のsupported runtime stateとしてテスト新設しない |
| R2-06 | D5–6、FSDB-REPORT、mutation boundary / P→receipt消費 | 導入commit後cleanupだけ失敗は実績保持・異常結果維持。ManualRecovery停止とprefix保持/suffix不変。DurableFinalizationFailedはdurable FS/DB事実保持、失敗item成功登録/maintenanceなしで停止 | cleanup残存・表示文章差は可。cleanup失敗で所持を失う、停止後続行、finalization失敗を成功扱いは誤り | 実DB failure＋既存destination delete failure port、source cleanup failure port。finalizationは既存実executor＋throwing finalizerを推定orchestrationへ接続 |
| R2-07 | D4,7、既存hash/後処理契約 / P | BMSはSHA256一致だけでMD5不一致を既所持にしない。BMSON primary維持。先行成功で後続全所持ならresource-only再評価。resource対象なしcleanup-onlyは既存設定条件。maintenance/通知集約とlease解放後publication維持 | metrics/private型/翻訳は可。後続一律skip、packageごとのbatch後処理重複は誤り | 既存format/resource-only/cleanup/guard/subscriber coverageを維持・構造依存はbehaviorに置換。成功後resource-onlyを追加 |

### Coverage ledger / safety

全 ID の canonical fixture は `BmsLibraryPackageInstallServiceTests`。新 fixture 不要。R2-01–02 は model tests と非保留不変caseを extend、R2-03 は旧 `BuildEstimatedInstallBatchPlan_*` を replace/extend、R2-04–05 は既存force/autoの実DB failure setupを活用して model caseをextend。R2-06は既存 `InstallPackagesWithFileMutationReceipts_StopsBatchAfterDurableFinalizationFailure` の実executor/portを推定consumerへ接続し、低層の独立契約テストは保持する。R2-07は `ResourceOnlyBmsonWorksWithoutBmsFiles`、cleanup advice、guard/subscriberを活用し、旧plan/groupとmaintenance mechanics assertionをbehaviorへ置換する。近傍auto-install契約は保持する。

共有資源は既存 `WithTemporarySongDb` とGUID temp FS、明示options/captured scan、既存dispatcher helperで所有する。通常Functional lane。同期model/receipt returnが完了signal、asyncを使う場合は実Task await、watchdogはrunnerのみ。DNP・固定待ち・visible WPF・live Everything・logger設定・新規reflection/source assertionは追加しない。exactness例外はなし。warning enum、terminal state、fixture path/DB identityはsemantic assertion。

R2-02は新規FQNを指定したQuickで修正前redを実施し、compile/setup failureはredとみなさない。head-passを同じ回帰で確認する。他のfailure injectionは製品契約の検証でありmutantではない。追加targeted mutantは不要。test mechanics/helper/データ構築の適合は可、required outcomeの変更はrootへ返す。component取得の非実行だけを観測するtest-only APIは増やさずstatic reviewで閉じる。

focused Quick: `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~BmsLibraryPackageInstallServiceTests'`。
rootのFunctionalとfresh reviewへ、ID coverage、旧route/旧test対応、red/head-pass、safety、exact command/artifactをhandoffする。

## 検証証跡

- 修正前 red: `verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~InstallPendingPackagesToEstimatedDestinations_EmptyDestinationPackageDoesNotReserveHashForLaterPackage'`。production は開始 HEAD のまま、回帰 test だけ追加。1 件実行・1 件失敗。後続の `shared.bms` が destination に存在する assertion が失敗し、compile / setup failure ではないことを root も確認した。artifact: `artifacts/verification/tests-quick-20260908-101412/functional/results.trx`（テスト実行 4.3584 秒）。
- 初回実装 Quick: fixture 146 件中 144 成功、2 skip（既存）。`tests-quick-20260908-105315/functional/results.trx`。最終整理後 R2-02 単独成功: `tests-quick-20260908-110035/functional/results.trx`。
- 初回統合 Functional: `verify-refactor.ps1 -Mode Functional` 成功。artifact: `artifacts/verification/tests-functional-20260908-110233`。analyzer 0 diagnostics、build 0 errors。actual retained-ExitTime-derived test execution は **180.3 秒**（180 秒 reporting target 超過、300 秒 budget 内、timeout ではない）。

### 統合確認での補完

初回 handoff の後、root が次を確認した。packet の意味変更ではなく、D2/D4/D6 と R2-04/06/07 の既定条件を満たすための補完である。

- 先行成功で後続が全件既所持になる resource-only 再分類、および導入 commit 後 cleanup だけ失敗しても後続同 hash を抑止する直接接続 coverage が未実装だった。単独の初期所持 resource-only / cleanup-only test では代替しない。新 estimated loop と実 executor を接続した小さなケース（正常/cleanup failure を統合してもよい）で補う。
- cleanup-only 候補を後回しにする別 loop が残っており、その候補の durable finalization 停止より前に後続の通常導入が実行され得る。cleanup-only の認定・実行・receipt 判定も候補の番で完結させる。新しい cleanup 機構や削除条件は追加せず、既存 callback と集約 counters/通知を維持する。
- R2-04/06 の停止 test の variation として、先行成功→cleanup-only の durable finalization failure→後続新規導入未着手を既存実 executor の mandatory finalizer failure port で確認する。authority は D6 と既存 `ExecutePendingPackageSourceCleanupWithReceipt`→実 executor→mandatory DB/model finalizer。fake-only な新状態は追加しない。

初回 Functional を開始してから不足が判明したため、その run の結果も保持する。補完後の filtered Quick と、通常機能の順序が変わる最終 snapshot の Functional を実行する。

補完完了:

- cleanup-only を候補 loop 内へ統合。`ExecuteEstimatedInstallBatchPlan_StopsAfterCleanupOnlyDurableFinalizationFailure` は実 executor の finalization failure 後の suffix 未着手を確認し成功（`tests-quick-20260908-113132`）。関連 fixture は 148 件中 146 成功、既存 2 skip（`tests-quick-20260908-113356`）。
- `InstallPendingPackagesToEstimatedDestinations_ReevaluatesResourceOnlyAfterEarlierCommit` の二つの variant で、A の実 FS/DB/mandatory finalization 成功後に元譜面削除だけを失敗させ、`CompletedWithCleanupFailure` と batch の異常 flag を確認。B は共通 hash を二重導入せず、resource-only または固有譜面 h2 を導入する。実 model と実 executor を通し、テスト側の hash 追記はない。2 件成功（`tests-quick-20260908-114349`）。
- 追加ケースの調整中に test API の誤参照による compile failure、cleanup-only fixture の component count 条件、および混在 package の未移動重複 source の assertion を修正した。これらを bugfix の red や timeout として数えていない。上記の最終 focused run はすべて成功。
- 補完後の最終 Functional: `verify-refactor.ps1 -Mode Functional` 成功。artifact: `artifacts/verification/tests-functional-20260908-114519`、全体ログ: `.tmp/r2-final-functional-20260908.log`。analyzer 0 diagnostics、build 0 errors。actual retained-ExitTime-derived test execution は **209.7 秒**（180 秒 reporting target 超過、300 秒 budget 内、timeout ではない）。
- 初回 fresh static review: runtime の blocking finding なし。`workflows.md` の変更対象外 auto install 説明が確定 hash 判定へ誤変更されていた P2 を修正し、その一行だけ開始 HEAD の説明へ復元した。推定導入の説明と production/test は変更なし。文書のみの修正のため追加 Quick/Functional は不要、UTF-8/LF と diff check、fresh follow-up review で確認する。
- fresh follow-up review 完了: 上記 P2 の解消を確認し、blocking finding なし。最終 Functional は合計 4,638 件、4,625 成功、既存 13 skip、失敗 0。実装・検証・review を完了し、ユーザー承認済みの commit へ進む。公開・push は行わない。
