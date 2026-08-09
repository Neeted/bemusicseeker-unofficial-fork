# chart_info ライフサイクル整理 実装計画

## PLAN

- **Objective:**
  - `chart_info` の current 判定、解析、解析失敗、永続化、session index、LR2 `song` 派生列投影を、LR2連携モードとスタンドアローンモードで同じライフサイクルへ整理する。
  - 起動時の欠損検出は、両モードとも実際の `chart_info` / `chart_info_parse_failure` / owned chart を照合する通常 hydration を正本とする。
  - `lr2_song_db_sync_status` は LR2連携モードの `song.db` 完全生成処理の開始・再開・完了判定だけに限定し、`chart_info` の現存・完全性を証明する入力には使わない。
  - `chart_info` または有効な `chart_info_parse_failure` が欠ける owned chart は次回起動時に再解析対象へ戻す。current `chart_info` が存在する場合は、それを current parse failure より優先する。
  - full backfill で生成または再利用した `chart_info` を、DB の `chart_info` 行だけでなく対応する BMS `song` の generated columns にも永続反映する。ユーザー管理列は保持する。
  - inline、full backfill、LR2 `song_rows` に重複している単一 snapshot の current 判定・parser 呼び出し・失敗行生成を一つの route-neutral evaluator に集約する。
  - 実装と同じ unit で現行仕様を `devdocs/spec` へ反映し、完了済みの旧計画を競合する仕様正本として残さない。

- **Current evidence:**
  - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoOwner.cs`
    - `ProcessDeferredHydrationRequests()` は、full backfill を後続予約する場合だけ `HydrateChartInfos(..., allowAllCurrentFastPath: true)` を呼ぶ。
    - 通常の `HydrateChartInfos()` は `BmsLibraryDbGateway.LoadChartInfoHydrationData()` と `OwnedChartCollectionState.CreateChartInfoHydrationOwnerSummary()` を使い、実在する current `chart_info`、current parse failure、backfill candidate をモード非依存で分類する。
    - `TryCreateAllCurrentHydrationResultFromCompletedLr2SongDbSync()` は LR2モードだけで `lr2_song_db_sync_status=Completed` と file-diff由来 trust snapshot を参照し、`chart_info` / parse failure を読まずに全ownerをcurrentとして合成する。このため外部または意図的に削除した行を見逃す。
    - 実データ照合後に作られる `ChartInfoHydrationAllCurrentSnapshot` は、同一session内のowner/storage/parser/timeout versionが変わらない場合に candidate summary を省略する正当な共通最適化である。
    - `ProcessBackfillRequests()` のchunk writerは `CatalogMutationOwner.ApplyChartInfoWrite()` へ digest、`chart_info`、parse failureだけを渡し、BMS `song` generated columnsを更新しない。
    - `BuildInline()` は `ApplyChartInfoRowsToBmsStorageRows()` と `ApplyInlineChartInfoWrite()` により、BMS/BMSON storage rowsとchart-info factsを同一transactionで保存している。
    - `RemoveParseFailuresByMd5()` はfailure rowを削除してwarning refreshを発行するが、同一sessionの `hydrationAllCurrentSnapshot` を無効化しない。
  - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoState.cs`、`ILr2ChartInfoTrustPort.cs`、`BeMusicSeeker/Models/BMSLibrary.Lr2SynchronizationOwner.cs`、`LibraryFileScanPipelineOwner.cs`
    - `ChartInfoLr2TrustInput`、`ChartInfoCompletedLr2SongDbSyncTrustSnapshot`、`ILr2ChartInfoTrustPort` とfile-diff captureが、LR2同期完了をchart-info完全性へ転用する専用seamを形成している。
  - `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs`
    - `LoadChartInfoHydrationData()` は current parser version の `chart_info` と、parser version / timeout条件を満たすcurrent parse failureをread-onlyで取得する。
    - `GetChartInfoBackfillCandidateSummary()` は `song` / `bmson_song`、`chart_digest_map`、current/any `chart_info`、current parse failureを照合する。分類順は current `chart_info`、current parse failure、missing digest / missing `chart_info` / stale `chart_info` である。
  - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoBuildService.cs`、`ChartInfoInlineBuildService.cs`、`Lr2SongDbSyncService.cs`
    - inline、full backfill、LR2 `song_rows` の三経路に、current row/failure判定、`ChartInfoParser.ParseBytesDetailed()`、timeout、failure row、message正規化が重複している。
    - full backfill の `ParseQueuedItem()` は current failure を current row より先に確認しており、hydration/candidate summary/inline/LR2経路の「current `chart_info` 優先」と表現が揃っていない。
    - LR2 `song_rows` は同じ `ChartFileSnapshot` から不足・stale `chart_info` を構築し、`song` と同一chunk transactionへ保存する。これは維持すべき既存契約である。
  - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoWriteRequest.cs`、`CatalogMutationOwner.cs`
    - `CatalogInlineChartInfoWriteRequest` / `ApplyInlineChartInfoWrite()` は必要なtransaction境界を既に持つが、名称と利用範囲がinline専用で、full backfillから再利用されていない。
    - `Lr2SongDbWriter.UpsertGeneratedSongs()` は既存 `song` のuser columnsを保持し、generated columnsだけを比較・更新できる。
  - 現行仕様には不整合がある。
    - `devdocs/spec/startup-initialization-flow.md` は、LR2 `song_rows` workerが同じsnapshotからmissing/stale `chart_info`を生成すると定義している。
    - `devdocs/spec/chart-file-read-pipeline.md` には、`song_rows` 前に別処理で補完され、`song_rows` は再生成しないという旧説明が残っている。
    - `devdocs/spec/chart-info-parser-compatibility-notes.md` の「`song` テーブルは変更しない」はparser単体の責務と、chart-info lifecycleによるgenerated column投影を区別できていない。
  - 作業開始時点は branch `refactor`、HEAD `0177922e`。worktreeには本件と無関係な既存変更・削除・untracked fileが多数あるため、本計画の実装ではそれらを変更、整形、reset、stageしない。

- **Decision status:**
  - 未決のproduct semanticsはない。以下を確定事項として実装する。
  - `lr2_song_db_sync_status` はLR2 `song.db` generated data syncのdurable lifecycleだけに使う。chart-info hydration/backfillのskip条件へ使わない。
  - LR2連携モードとスタンドアローンモードは、同じactual-data hydration/currentness判定を使う。
  - LR2専用synthetic all-current shortcutを削除する。初手でdirty table、SQLite trigger、別監査task、LR2専用coverage probeは追加しない。
  - actual-data hydration後の `ChartInfoHydrationAllCurrentSnapshot` は両モード共通のsession最適化として維持する。
  - currentnessの優先順位は `current chart_info > current parse failure > parse candidate` とする。
  - current `chart_info` が無い譜面について `chart_info_parse_failure` を削除した場合、次回起動または次の明示的hydration/backfill判定で再解析対象へ戻る。削除操作だけでは即時再解析を自動queueしない。
  - `chart_info` を意図的に削除した場合も、次回起動のactual-data hydrationで再解析対象へ戻る。
  - full backfillで成功またはexisting current rowを再利用したcandidateは、BMS `song` generated columnsへ投影する。全件current時の任意の`song`列drift監査は本計画に含めない。
  - BMSONは `bmson_song` をstorage正本とし、LR2 `song` rowを生成しない。
  - parse failure messageの新規保存規則は、共通 `ChartInfoBuildService` の現行規則（CR/LFを空白化、trim、最大1024文字）を正本とする。既存failure rowの一括migrationは行わない。
  - 後続の性能最適化が必要になった場合は、両モードに同じ利益があるactual-data based最適化として別途計画する。LR2 status由来shortcutは再導入しない。

- **Assumptions:**
  - standaloneで稼働中の通常hydration/backfillは、起動critical path外のbackground処理として許容されている。この処理をLR2モードにも適用してもinstall readinessを同期blockしない。
  - `chart_info` schema、parser version、UI文字列、警告UI、release/version、verification runner/laneは変更しない。
  - BMS storage ownerは `CreateSongRowPersistenceCopy()` でtransaction用copyを作れ、`Lr2SongDbWriter.UpsertGeneratedSongs()` がuser columnsを保持する。
  - full backfill targetの `ChartFile` / `ChartInfoBuildTarget` は、DB再検索をせず対応するBMS storage ownerへcommit後の結果を適用できる参照を保持している。
  - unowned `chart_info` rowはmetadata cacheとして保持し、full backfillやhydrationで削除しない。

- **Out of scope:**
  - 全ownerがcurrentと判定された後に、`song` generated columnsと `chart_info` の全列一致を毎起動監査すること。
  - chart-info専用schema version、dirty flag、trigger、fingerprint、定期監査taskの追加。
  - LR2同期statusやfile-diff statusを使った別名のchart-info trust shortcut。
  - parser互換アルゴリズム、表示列、warning UI、ユーザー向け文言の変更。
  - 無関係な既存worktree差分の修復・整理。

### Unit 1: 起動時currentnessをactual-data hydrationへ統一する

1. **Observable outcome**
   - LR2連携モードとスタンドアローンモードが、起動時に同じ `chart_info` / `chart_info_parse_failure` / owned chart照合を行う。
   - `lr2_song_db_sync_status=Completed` でも、owned chartに対応するcurrent `chart_info`とcurrent parse failureの両方が無ければbackfill candidateになる。
   - current `chart_info`が全ownerを被覆する、またはcurrent parse failureで終端している場合だけ、実データ由来の `hydration_all_current` でfull candidate summary/backfillをskipする。
   - parse failureを削除した譜面と、`chart_info`を削除した譜面は、両モードで次回起動時に同じ条件で再解析対象へ戻る。

2. **Production route / files / symbols**
   - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoOwner.cs`
     - `ProcessDeferredHydrationRequests()` から `allowAllCurrentFastPath` 分岐を外し、常に通常の `HydrateChartInfos(reason)` を呼ぶ。
     - `HydrateChartInfos()` のfast-path引数と `TryCreateAllCurrentHydrationResultFromCompletedLr2SongDbSync()` を削除する。
     - 実データ由来の `CaptureHydrationAllCurrentSnapshot()` / `CreateCurrentHydrationAllCurrentResult()` / `QueueBackfill()` のskipは維持する。
     - synthetic route専用の `fastPath` / `CandidateSummaryMs` log項目は、他用途が無ければstateとlogから削除する。通常hydrationのDB/owner計測項目は維持する。
   - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoState.cs`
     - `ChartInfoLr2TrustInput`、`ChartInfoCompletedLr2SongDbSyncTrustSnapshot` を削除する。
     - synthetic route専用なら `ChartInfoHydrationResult.FastPath` を削除する。
     - actual-data `ChartInfoHydrationAllCurrentSnapshot` とowner/storage/parser/timeout version契約は維持する。
   - `BeMusicSeeker/Models/BmsLibraryInternal/ILr2ChartInfoTrustPort.cs`
     - interfaceファイルを削除する。
   - `BeMusicSeeker/Models/BMSLibrary.Lr2SynchronizationOwner.cs`
     - `ILr2ChartInfoTrustPort` 実装、trust gate/state、capture/clear/getメソッド、関連logを削除する。
     - LR2 sync request/status/file-diff freshnessなど本来の同期owner責務は変更しない。
   - `BeMusicSeeker/Models/BmsLibraryInternal/LibraryFileScanPipelineOwner.cs`
     - `CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff()` 呼び出しを削除する。
   - `BeMusicSeeker/Models/BMSLibrary.cs`
     - `CatalogChartInfoOwner.ConfigureWorkflow()` へのLR2 trust port配線と、synthetic route専用定数を削除する。
   - 必要に応じてsource-text guardやcomposition testを更新するが、削除済みsymbol名だけを監視する脆い新規テストは追加しない。

3. **Existing owner / service / gateway and new contract**
   - 正本として使う既存境界:
     - `BmsLibraryDbGateway.LoadChartInfoHydrationData()`
     - `OwnedChartCollectionState.CreateChartInfoHydrationOwnerSummary()`
     - `CatalogChartInfoOwner.ReplaceIndex()`
     - `CatalogChartInfoOwner.CaptureHydrationAllCurrentSnapshot()`
     - `BmsLibraryDbGateway.GetChartInfoBackfillCandidateSummary()`
   - 新しいproduction contractは追加しない。LR2専用trust contractを減らすunitである。

4. **Old route / duplicate seam removed in the same unit**
   - `completed_song_db_sync` を根拠に全owner currentを合成する経路を完全に削除する。
   - `ILr2ChartInfoTrustPort` とfile-diff由来chart-info trust snapshotを同じunitで退役させ、dead wiringを残さない。
   - `lr2_song_db_sync_status` をchart-info ownerから参照する経路を0件にする。
   - actual-data `hydrationAllCurrentSnapshot` は旧routeではないため削除しない。

5. **Invariants**
   - **UI / readiness:** chart-info hydration/backfillは既存どおりstartup background taskであり、install readiness blockerへ戻さない。新しい同期waitをUI threadに追加しない。
   - **Persisted data:** このunitではDB schema/row形式を変えない。unowned `chart_info`を削除しない。
   - **Compatibility:** LR2 `song.db` sync statusの生成・resume・Completed判定は従来どおり。standaloneではLR2 statusを読まない。
   - **Currentness:** current parser versionとtimeout-aware parse failure条件を維持し、優先順位はcurrent info、current failure、candidateとする。
   - **Threading/shutdown:** hydration gate、storage reader guard、owned collection gate、shutdown skip、同期backfill bypassのlock順と完了version契約を変えない。
   - **Failure:** hydration DB read失敗時はall-currentを合成せず、既存のfailed result/log契約を維持する。
   - **Performance:** actual-data hydrationでcandidate 0なら、既存 `ChartInfoHydrationAllCurrentSnapshot` により続くcandidate summary/full backfillを省略する。

6. **Behavior tests**
   - `BeMusicSeeker.Tests/ChartInfoMetadataTests.cs`
     - `DeferredChartInfoHydration_UsesAllCurrentFastPathAndLazyLoadsDisplayIndex` はsynthetic LR2 trustを仕様化しているため削除またはactual-data hydration testへ置換する。
     - LR2 / standaloneを同じfixtureで実行し、以下を対にして確認する。
       - current `chart_info`あり: actual-data hydrationでcandidate 0、full candidate summary/backfillを呼ばない。
       - `song` / `chart_digest_map`あり、current `chart_info`なし: modeやLR2 Completed statusに関係なくcandidateとなる。
       - current `chart_info`を起動前に削除: candidateとなる。
       - current `chart_info`なしでparse failureを起動前に削除: candidateとなる。
       - current parse failureあり: parse対象にしない。
       - stale parser versionまたは現在timeoutより短いtimeout failure: candidateとなる。
       - current `chart_info`とfailureが併存: current `chart_info`を優先する。
     - synthetic trust capture用reflection helperを削除する。
   - `BeMusicSeeker.Tests/OwnedChartCollectionStateTests.cs`
     - owner summaryのcurrent info / current failure / candidate優先順位を維持する。
   - `BeMusicSeeker.Tests/BmsLibraryLr2SongDbSyncTests.cs`
     - `lr2_song_db_sync_status` がLR2 syncのno-op/再同期判定には引き続き効くことを確認し、chart-info completenessの期待を混ぜない。
   - `BeMusicSeeker.Tests/LibraryFileScanPipelineOwnerTests.cs`
     - trust capture削除後もfile-diff apply、inline chart-info、warning event、LR2 scan surface captureが変わらないことを確認する。

7. **Verification**
   - 反復中:

     ```powershell
     pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ChartInfoMetadataTests.DeferredChartInfoHydration_|FullyQualifiedName~ChartInfoMetadataTests.LoadChartInfoHydrationData_|FullyQualifiedName~ChartInfoMetadataTests.GetChartInfoBackfillCandidateSummary_|FullyQualifiedName~OwnedChartCollectionStateTests.CreateChartInfoHydrationOwnerSummary_|FullyQualifiedName~LibraryFileScanPipelineOwnerTests'
     ```

   - LR2 status境界の回帰確認:

     ```powershell
     pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~BmsLibraryLr2SongDbSyncTests.QueueLr2SongDbSync_DoesNotQueueWhenCompletedStatusIsCurrent|FullyQualifiedName~BmsLibraryLr2SongDbSyncTests.QueueLr2SongDbSync_RunsLr2SongDbSyncAndMarksCompletedWhenClean'
     ```

   - unit完了時に `-Mode Functional` を1回実行し、command全体180秒以内、tracked file不変、残留test processなしを確認する。Fullは不要。
   - timeout時は `artifacts/verification`、active/last test、TRX/process treeを確認し、単純なtimeout延長や無制限再実行をしない。

8. **repo-static-review scope**
   - `lr2_song_db_sync_status` がchart-info owner/hydration/backfillのcurrentness入力として残っていないこと。
   - actual-data all-current snapshotを誤って削除・弱体化していないこと。
   - hydration/background scheduler、shutdown、lock順、sync backfill bypassに新しいdeadlock/待機がないこと。
   - LR2 trust seam削除がfile-diff freshnessやLR2 sync status本来の責務を壊していないこと。
   - testsがmode差ではなく同一observable behaviorを検証していること。

9. **Concrete replanning evidence**
   - `ILr2ChartInfoTrustPort` またはtrust snapshotに、synthetic hydration以外のproduction consumerが見つかった場合。
   - 通常hydrationをLR2モードで使うことでstartup schedulerの依存cycle、UI同期wait、既存lock順違反が発生する場合。
   - actual-data hydrationがLR2モードでDB schema mutationを要求することが判明した場合。read-only loaderへschema repairを戻さず、startup schema phaseとの責務を再計画する。
   - 正常LR2データでcandidate summary/full backfillが毎回走る場合。currentness分類またはsnapshot invalidationの不具合を直し、LR2専用shortcutへ戻さない。

10. **Independent stage / commit paths**
    - 対象:
      - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoOwner.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoState.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/ILr2ChartInfoTrustPort.cs`（削除）
      - `BeMusicSeeker/Models/BMSLibrary.Lr2SynchronizationOwner.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/LibraryFileScanPipelineOwner.cs`
      - `BeMusicSeeker/Models/BMSLibrary.cs`
      - 上記behavior tests
      - Unit 1で更新するspec files
    - `CatalogChartInfoOwner.cs`、`ChartInfoMetadataTests.cs`、新規 `chart-info-lifecycle.md` は後続unitでも触るため、unitごとにhunkを分けてstageし、Unit 2/3の先行変更をUnit 1へ混ぜない。
    - 既存の無関係なdirty/untracked filesをstageしない。commit/pushは別途明示された場合だけ行う。

11. **Specs updated in this unit**
    - 新規 `devdocs/spec/chart-info-lifecycle.md`
      - identity、currentness優先順位、actual-data startup hydration、session all-current snapshot、LR2 status非依存、parse-failure削除の次回retry契約を現行仕様として記述する。
    - `devdocs/spec/README.md`
      - 新規lifecycle specを機能別仕様と推奨読順へ追加する。
    - `devdocs/spec/startup-initialization-flow.md`
      - LR2/standalone共通hydrationと、`lr2_song_db_sync_status` のscopeを明記する。
    - `devdocs/spec/lr2-song-db-generation.md`
      - statusはLR2 generated `song`/`folder` 完全生成のdurable lifecycleであり、chart-info row現存の証明ではないと明記する。
    - `devdocs/spec/data-and-indexes.md`
      - `song.hash -> chart_digest_map -> chart_info.sha256`、BMSON identity、current parse failure、actual-data hydrationを整理する。

### Unit 2: 単一snapshotのcurrent判定・解析・失敗生成を共通化する

1. **Observable outcome**
   - inline file-diff/package、full backfill、LR2 `song_rows` が同じroute-neutral evaluatorを使い、同じsnapshot、同じcurrentness優先順位、同じtimeout、同じparse failure形式でchart-infoを評価する。
   - LR2 `song_rows` は引き続きBMS row解析と同じ `ChartFileSnapshot` bytesを使い、追加のfile readやworker内DB queryを行わない。
   - current `chart_info`、current parse failure、parse success、parse failure、timeoutの分類が三経路で一致する。

2. **Production route / files / symbols**
   - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoBuildService.cs`
     - `BuildInlineChartInfo()` をroute-neutralな単一snapshot evaluator（例: `EvaluateSnapshot()`）へ改名・一般化する。
     - `InlineChartInfoBuildResult` を用途中立の `ChartInfoSnapshotBuildResult` に改名する。aggregateの `ChartInfoInlineBuildResult` はinline batch結果として維持する。
     - evaluatorは `ChartFileSnapshot`、`ChartInfoBuildTarget` またはsingle-owner `ChartFile`、pre-resolved current row、current-failure fact、parse timeout、log callbackを受ける。
     - evaluator内で identity compatibility、parser version currentness、`current chart_info > current failure > parse`、parser invocation、diagnostic log、timeout、failure row、failure-delete MD5を決定する。
     - full backfillの `ParseQueuedItem()` は、readerが得たbytesと既知identityから既存 `ChartFileSnapshot` を組み立て、このevaluatorへ委譲する。別のsnapshot DTOは追加しない。
   - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoInlineBuildService.cs`
     - batchでpreloadしたcurrent rows/current failuresをevaluatorへ渡す。per-chart DB lookupを追加しない。
   - `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncService.cs`
     - `CreateSyncSongRowItem()` は、resolver/current-failure setでpre-resolveしたfactsと、同じ `ChartFileSnapshot` から作るsingle-owner chart/targetをevaluatorへ渡す。
     - `TryBuildChartInfoFromSnapshot()`、LR2専用failure row生成、`NormalizePersistedChartInfoParseFailureMessage()`、重複currentness分岐を削除する。
     - generated row callback、in-run resolver更新、song rowへの `ApplyLr2ChartInfoDetailedColumns()`、同一chunk transactionは維持する。
   - `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncRequestCoordinator.cs` またはrequest型は、既存のpreloaded resolver/current failure setを渡す契約変更が本当に必要な場合だけ狭く更新する。

3. **Existing owner / service / gateway and new contract**
   - ownerは `ChartInfoBuildService` のままにする。新しいservice/interfaceは作らない。
   - `ChartFileSnapshot`、`ChartInfoBuildTarget`、`ChartInfoBuildTargetMapper`、`ChartInfoParser.ParseBytesDetailed()` を再利用する。
   - evaluatorはDBを開かず、current row/failureはcallerがbatch/run単位でpreloadする。
   - 新設するcontractは `ChartInfoSnapshotBuildResult` と、必要最小限のinternal evaluator signatureだけとする。

4. **Old route / duplicate seam removed in the same unit**
   - full backfillの独自 `ParseQueuedItem()` parser/failure処理をevaluator委譲へ縮小する。
   - LR2 `TryBuildChartInfoFromSnapshot()` とLR2専用failure message normalizerを削除する。
   - inline専用名称のsingle-item resultを退役する。
   - parser呼び出しを共通化した後も、file-diff/packageのbatch wrapperやLR2 bounded pipeline自体は統合しない。異なるorchestrationを一つの巨大serviceにしない。

5. **Invariants**
   - **Snapshot/I/O:** 一譜面一回のfile readを維持し、BMS song rowとchart-infoは同じbytes/digestを使う。full backfill reader/workerのbounded queueを維持する。
   - **Currentness:** current rowはparser versionとidentity compatibilityを満たすものだけ。current rowをcurrent failureより優先する。
   - **Failure:** parse failureはMD5がある場合だけ永続化し、timeoutだけ `parse_timeout_ms` を保存する。成功時は対象MD5のfailure delete factを出す。
   - **Logging:** fatal parse failureのWARN、diagnostic、slow parse/phase logの意味とchannelを維持する。per-itemの無制限info logを追加しない。
   - **Persistence compatibility:** table schemaと既存failure rowを変更しない。新規failure messageだけ共通正規化規則へ揃える。
   - **Concurrency:** evaluatorはstatelessまたはcall-localで、worker間でmutable dictionaryを更新しない。LR2 generated-row resolver updateは既存thread-safe callback境界を維持する。
   - **Shutdown/cancellation:** full backfill/LR2 syncの既存cancel、bounded collection完了、chunk rollback/resume契約を変更しない。

6. **Behavior tests**
   - `BeMusicSeeker.Tests/ChartInfoMetadataTests.cs`
     - evaluator単体について、current row、current failure、success、parse failure、timeout、stale row、identity mismatchを小さいsnapshot fixtureで確認する。
     - full backfillとinline wrapperが同じresult/failure rowを得るparity testを追加する。
     - `BackfillChartInfos_*` のone-read、duplicate MD5 grouping、digest persistence、current/stale failure、timeout、success-clear、chunk commit testを維持・更新する。
     - `ChartInfoInlineBuildService_*` のprovided snapshot only、current row reuse、current failure skip、failure persistenceを維持・更新する。
   - `BeMusicSeeker.Tests/BmsLibraryLr2SongDbSyncTests.cs`
     - `SyncService_BuildsChartInfoFromSongRowSnapshotsWhenNoResolverIsProvided`
     - `SyncService_SkipsChartInfoParseWhenCurrentParseFailureIsKnown`
     - `SyncService_UsesRequestChartInfoResolverWithoutChartInfoDbLookup`
     - `SyncService_RequestChartInfoResolverUsesStableMd5FallbackWhenSha256DoesNotMatch`
     - `SyncService_RebuildsChartInfoWhenRequestResolverReturnsStaleRow`
     - chunk rollback/resume、same transaction、generated row availabilityを維持する。
   - current `chart_info`とcurrent failureが同時に与えられた場合、三経路すべてがcurrent rowを採用するtestを追加する。

7. **Verification**
   - 反復中:

     ```powershell
     pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ChartInfoMetadataTests.BackfillChartInfos_|FullyQualifiedName~ChartInfoMetadataTests.ChartInfoInlineBuildService_|FullyQualifiedName~BmsLibraryLr2SongDbSyncTests.SyncService_BuildsChartInfoFromSongRowSnapshotsWhenNoResolverIsProvided|FullyQualifiedName~BmsLibraryLr2SongDbSyncTests.SyncService_SkipsChartInfoParseWhenCurrentParseFailureIsKnown|FullyQualifiedName~BmsLibraryLr2SongDbSyncTests.SyncService_UsesRequestChartInfoResolverWithoutChartInfoDbLookup|FullyQualifiedName~BmsLibraryLr2SongDbSyncTests.SyncService_RebuildsChartInfoWhenRequestResolverReturnsStaleRow'
     ```

   - `ChartInfoBuildService` のparser routeを変更するため、unit内で一度次を実行する。

     ```powershell
     pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ParserCompatibilityFull'
     ```

   - unit完了時に `-Mode Functional` を1回実行し、command全体180秒以内を確認する。parser algorithm/schema/release runnerは変えないためFullは不要。

8. **repo-static-review scope**
   - 三経路でcurrentness、timeout、failure message、delete factにsemantic driftがないこと。
   - evaluatorまたはworkerにDB lookup、追加file read、mutable global stateが入っていないこと。
   - LR2 resolverがstale/incompatible rowを誤ってcurrent扱いせず、generated rowを同一run内の重複hashへ安全に共有できること。
   - bounded reader/worker/writer、chunk ordering、rollback/resume、shutdown/cancelを壊していないこと。
   - 共通化が巨大callback host、service locator、将来用abstractionになっていないこと。

9. **Concrete replanning evidence**
   - evaluator再利用のためにworker内DB queryまたは二回目のfile readが必要になる場合。
   - LR2 `song_rows` とfull/inlineで必要なobservable failure semanticsが実コード上で両立しない場合。
   - generated row resolver更新にshared mutable dictionary raceが発生し、既存callback境界で閉じられない場合。
   - parser compatibility fixtureで新規差分が出た場合。単なる期待値更新をせず、snapshot identity、decode、timeout、message normalizationのどこが変わったかを特定して再計画する。
   - runner/lane/fixture配置変更が必要になった場合。本計画のscope外として再計画し、変更するならFunctionalを同条件3回・各180秒以内で確認する。

10. **Independent stage / commit paths**
    - 対象:
      - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoBuildService.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoInlineBuildService.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncService.cs`
      - 必要最小限のrequest/coordinator file
      - `ChartInfoMetadataTests.cs`
      - `BmsLibraryLr2SongDbSyncTests.cs`
      - Unit 2で更新するspec files
    - `ChartInfoBuildService.cs` とtestsはUnit 3でも触るため、evaluator共通化だけをUnit 2としてstageし、storage projection/commit contract変更を混ぜない。
    - 無関係な既存worktree差分をstageしない。commit/pushは別途明示された場合だけ行う。

11. **Specs updated in this unit**
    - `devdocs/spec/chart-info-lifecycle.md`
      - single-snapshot evaluator、三つのorchestration route、currentness/failure contractを追加する。
    - `devdocs/spec/chart-file-read-pipeline.md`
      - LR2 `song_rows`が同じsnapshotからmissing/stale `chart_info`を生成する現行実装へ修正し、「事前補完されsong_rowsでは再生成しない」という旧記述を削除する。
      - inline/full/LR2が同じevaluatorを使うが、reader/worker/writer orchestrationは用途別に維持することを記述する。
    - `devdocs/spec/startup-initialization-flow.md`
      - startup backfillとLR2 syncのsnapshot再利用境界を整理する。
    - `devdocs/spec/lr2-song-db-generation.md`
      - LR2 syncがpreloaded current row/failure factsを使い、missing/stale時だけ同じsnapshotで共通evaluatorを呼ぶことを記述する。
    - `devdocs/spec/chart-info-parser-compatibility-notes.md`
      - parser compatibility事実と、caller route共通化・failure正規化を区別して記述する。

### Unit 3: full backfillのstorage projectionとparse-failure再試行契約を閉じる

1. **Observable outcome**
   - full backfillでcandidateとなったBMSについて、成功またはexisting current row再利用後に `chart_digest_map`、`chart_info`、parse failure、対象 `song` generated columnsを同一catalog transactionで収束させる。
   - `favorite`、`tag`、`adddate` などuser columnsを保持し、chart-info由来の `level`、`difficulty`、`mode`、`maxbpm`、`minbpm`、`bga`、`exlevel`、`longnote`、`random`、`karinotes` を更新する。
   - duplicate MD5で一度だけ解析した結果を、同じtargetに属する全BMS storage ownerへ投影する。
   - BMSONでは `chart_info` / session indexだけを更新し、LR2 `song` rowを作らない。
   - commit失敗時はcanonical storage owner、digest event、session indexを部分更新せず、次回起動でもcandidateとして検出できる。
   - parse failureを明示削除した後は同一sessionのactual-data all-current snapshotを無効化し、後続の明示的hydration/backfill判定がstale skipを再利用しない。即時parseはqueueしない。

2. **Production route / files / symbols**
   - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoWriteRequest.cs`
     - `CatalogInlineChartInfoWriteRequest/Receipt` を用途中立の `CatalogChartInfoStorageWriteRequest/Receipt` へ改名する。
     - BMS/BMSON storage rowsと `CatalogChartInfoWriteRequest` を一つのimmutable transaction requestとして維持する。
   - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogMutationOwner.cs`
     - `ApplyInlineChartInfoWrite()` を `ApplyChartInfoStorageWrite()` へ一般化する。
     - BMS rowsは既存bulk `Lr2SongDbWriter.UpsertGeneratedSongs()` を使い、1 rowごとのSELECT loopへ戻さない。
     - BMSON rows、chart-info factsを同じtransactionで保存する。
     - facts-onlyの `ApplyChartInfoWrite()` はparse-failure明示削除などstorage rowを伴わない用途に残す。
   - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoOwner.cs`
     - `BuildInline()` をroute-neutral storage writeへ移行する。
     - `ProcessBackfillRequests()` のfour-list writer callbackを、一つのimmutable storage write request/receiptへ置換する。
     - full backfillのcommit成功後だけindex upsert、digest event、canonical owner projectionをpublishする。
     - `RemoveParseFailuresByMd5()` のdelete成功後に `ClearHydrationAllCurrentSnapshot("parse_failure_removed")` を実行する。自動backfill queueは追加しない。
   - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoBuildService.cs`
     - commit bufferで「DBへ新規persistするchart_info rows」と「成功/reuse rowをstorage ownerへ適用するapplication」を分ける。
     - reused existing rowでもcandidate ownerのsong projectionに必要なapplicationを保持する。
     - BMS transaction用copyへmissing digestと `ApplyLr2ChartInfoColumns()` を適用し、route-neutral storage write requestに含める。
     - receipt成功後だけcanonical ownerへdigest/chart-info-derived columnsを適用し、結果count/event/index callbackを進める。
   - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoBuildTarget.cs`、`ChartStorageOwnerMutator.cs`
     - storage owner参照からBMS persistence copyを作る狭い操作と、commit済みdigest/derived columnsをcanonical ownerへ適用する狭い操作を追加する。
     - path再queryやBMS/BMSON compatibility adapter materializationを追加しない。
   - `BeMusicSeeker/Models/BMSFile.cs`、`Lr2SongDbWriter.cs`
     - 原則既存 `CreateSongRowPersistenceCopy()`、`ApplyLr2ChartInfoColumns()`、`UpsertGeneratedSongs()` を利用する。既存bulk APIで不足するobservable contractがある場合だけ最小変更する。

3. **Existing owner / service / gateway and new contract**
   - transaction ownerは `CatalogMutationOwner`、build/commit sequencing ownerは `ChartInfoBuildService` / `CatalogChartInfoOwner` のままにする。
   - 新設ではなく既存inline request/receiptをroute-neutralへ改名・再利用し、full専用の第三のwrite APIを作らない。
   - canonical ownerへの適用は `ChartInfoBuildTarget` / `ChartStorageOwnerMutator` に閉じ、`CatalogMutationOwner` はmutable modelを保持しない。
   - session indexは引き続き `CatalogChartInfoOwner` が正本を持ち、storage ownerへruntime `ChartInfo` objectをattachしない。

4. **Old route / duplicate seam removed in the same unit**
   - inline専用名のstorage write contractを退役し、inline/fullが同じcatalog transaction routeを使う。
   - full backfillのfacts-only commitと、別経路でのsong projectionというsplit writeは作らない。
   - `PendingChartInfoApplication` をcountだけに使う現状を廃止し、commit後owner projectionの実契約にする。
   - completed parse-failure計画の契約をcurrent specへ統合後、`devdocs/plan/chart-info-parse-failure-plan.md` を削除する。競合する旧仕様を残さない。

5. **Invariants**
   - **Atomicity:** 同一chunkのBMS generated rowsとchart-info factsは同一DB transaction。失敗時はどちらもcommitしない。
   - **Canonical publication:** persistence成功前にcanonical BMS owner、digest index、chart-info session index、warning/digest eventを更新しない。
   - **User data:** `favorite`、`tag`、`adddate` などuser columnsを上書きしない。generated column比較/updateは `Lr2SongDbWriter` の既存契約を使う。
   - **BMS/BMSON:** BMSだけ `song` generated columnsへ投影し、BMSONは `bmson_song` とchart-info indexを正本にする。
   - **Duplicate identity:** BMSはMD5 groupingを維持し、一解析結果を全ownerへ投影する。BMSONはSHA-256-first groupingとし `chart_digest_map` を作らない。
   - **Current row reuse:** missing digestなどでcandidateになりexisting current `chart_info`を再利用した場合もsong projectionを行う。current ownerとしてhydrationでskipされた全件のdrift監査は行わない。
   - **Parse failure delete:** current `chart_info`が存在する場合、failureだけを削除しても再解析しない。current `chart_info`が無い場合だけ次回判定でcandidateになる。
   - **Threading/shutdown:** model/storage lockまたはDB transactionを保持したままUI/event subscriberを待たない。shutdown中の既存skip/rollback契約を維持する。
   - **Performance:** chunk単位bulk writeを維持し、ownerごとのDB SELECT、path再検索、全song再投影を追加しない。

6. **Behavior tests**
   - `BeMusicSeeker.Tests/ChartInfoMetadataTests.cs`
     - LR2/standalone双方のstartup/full backfillで、missing `chart_info`を生成し、対応BMS `song` generated columnsまで永続更新する。
     - generated columns更新後も `favorite`、`tag`、`adddate` が保持される。
     - duplicate MD5 ownerは一回parseし、全BMS pathのsong rowへ投影する。
     - existing current rowを再利用するmissing-digest candidateでもsong projectionする。
     - BMSON candidateはchart-info/indexを更新するが `song` rowを作らない。
     - commit failure injectionでDB facts、canonical digest/columns、session indexが部分更新されない。次回candidate summaryが同じ譜面を再検出する。
     - index updateはdurable receipt後だけ発生する。
     - `RemoveChartInfoParseFailuresByMd5_RemovesFailureRowsAndPublishesWarningRefresh` を拡張し、actual-data all-current snapshotがclearされること、自動parseは始まらないこと、次回明示判定/次回起動で両モードともretryすることを確認する。
     - current `chart_info`がある場合、failure delete後もcurrent rowが優先されることを確認する。
   - `BeMusicSeeker.Tests/OwnedChartCollectionStateTests.cs`
     - storage write request/receipt renameと、inline/fullのowner projectionがstorage ownerへruntime `ChartInfo`をattachしないことを維持する。
   - `BeMusicSeeker.Tests/BmsLibraryLr2SongDbSyncTests.cs`
     - LR2 sync既存のsame-transaction song/chart-info生成、user column保持と、新しい共通write routeが競合しないことを確認する。
   - `CatalogMutationOwner` のtransaction rollbackを既存fake/gatewayで検証できない場合は、既存transaction test patternを再利用し、新しいtest-only broad hookは追加しない。

7. **Verification**
   - 反復中:

     ```powershell
     pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ChartInfoMetadataTests.BackfillChartInfos_|FullyQualifiedName~ChartInfoMetadataTests.RemoveChartInfoParseFailuresByMd5_|FullyQualifiedName~ChartInfoMetadataTests.DeferredChartInfoHydration_|FullyQualifiedName~OwnedChartCollectionStateTests|FullyQualifiedName~BmsLibraryLr2SongDbSyncTests.QueueLr2SongDbSync_BuildsMissingChartInfoInsideSongRows|FullyQualifiedName~BmsLibraryLr2SongDbSyncTests.SyncService_PreservesUserColumnsWhenRunningOnCopiedSongDb'
     ```

   - storage projectionがparser result mappingへ触れるため、Unit 2での実行から追加変更がある場合は次を再実行する。

     ```powershell
     pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ParserCompatibilityFull'
     ```

   - unit完了時に `-Mode Functional` を1回実行し、command全体180秒以内、tracked file不変、残留test processなしを確認する。schema/release/runner変更はないためFullは不要。
   - prose/Markdown更新はリンク、UTF-8/LF、trailing whitespace、`git diff --check`を確認する。

8. **repo-static-review scope**
   - BMS rowsとchart-info factsが同一transactionであり、commit失敗前にcanonical/index/eventをpublishしていないこと。
   - `UpsertGeneratedSongs()` がuser columnsを保持し、全row個別SELECTへ退行していないこと。
   - duplicate MD5、reused row、missing digest、BMSON分離が正しいこと。
   - runtime `ChartInfo` をBMS/BMSON storage ownerへattachしていないこと。
   - parse-failure削除がsame-session snapshotだけを無効化し、意図せず即時full backfillやLR2 syncをqueueしないこと。
   - request/receiptの一般化でfacts-only write、inline write、full writeのfailure contractが曖昧になっていないこと。

9. **Concrete replanning evidence**
   - `ChartInfoBuildTarget` からtransaction用BMS copyまたはcommit後canonical ownerを安定して特定できず、path DB requeryが必要になりそうな場合。path lookupを追加せず、immutable owner-keyed application receiptへ再計画する。
   - 既存 `CatalogMutationOwner` でBMS rowsとchart-info factsを同一transactionにできない場合。split transactionへ妥協せずmutation boundaryを再計画する。
   - `UpsertGeneratedSongs()` が必要列以外を変更する、またはuser column保持testが失敗する場合。
   - commit後canonical mutationとowned digest eventのlock順が既存catalog mutation contractに収まらない場合。
   - full backfill後に全件song drift監査が必要という新しいproduct要件が出た場合。candidate repairとは別のmode-neutral auditとして再計画する。

10. **Independent stage / commit paths**
    - 対象:
      - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoWriteRequest.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogMutationOwner.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoOwner.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoBuildService.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoBuildTarget.cs`
      - `BeMusicSeeker/Models/BmsLibraryInternal/ChartStorageOwnerMutator.cs`
      - 必要時のみ `BMSFile.cs` / `Lr2SongDbWriter.cs`
      - behavior tests
      - Unit 3で更新するspec files
      - `devdocs/plan/chart-info-parse-failure-plan.md`（削除）
    - Unit 1/2と共有するfileは、前unit完了snapshotをbaseにUnit 3 hunkだけをstageする。前unitの未stage差分や無関係なworktree差分を巻き込まない。
    - commit/pushは別途明示された場合だけ行う。

11. **Specs updated in this unit**
    - `devdocs/spec/chart-info-lifecycle.md`
      - durable write、generated song projection、commit/publication順、parse-failure削除、index/lazy display、failure/shutdown、test contractを完成させる。
    - `devdocs/spec/data-and-indexes.md`
      - chart-info facts、BMS generated projection、BMSON分離、unowned metadata保持、transaction境界を更新する。
    - `devdocs/spec/bms-bmson-chart-abstraction-current-state.md`
      - storage ownerへruntime `ChartInfo`をattachしない契約を維持しつつ、BMS `song` generated columnsへのdurable projectionは別責務として明記する。
    - `devdocs/spec/library-mutation-boundary.md`
      - chart-info storage write requestによるBMS/BMSON rowsとchart-info factsのatomic commit、commit後publicationを追加する。
    - `devdocs/spec/chart-file-read-pipeline.md`
      - full backfill candidateの成功結果がBMS generated columnsまで収束することを追加する。
    - `devdocs/spec/chart-info-parser-compatibility-notes.md`
      - parser自体はDB writeを所有しない一方、lifecycle ownerが成功結果をgenerated song columnsへ投影することを明記し、「song tableは変更しない」という曖昧な記述を修正する。
    - `devdocs/spec/startup-initialization-flow.md`
      - missing/stale repairの完了条件をchart-info row、session index、対象BMS generated columnsまで含む形へ更新する。
    - `devdocs/plan/chart-info-parse-failure-plan.md`
      - current specへ契約を統合し、実装完了済みの旧計画として削除する。履歴はGit historyへ委ねる。
    - `devdocs/spec/warning-model.md`
      - UI warningのobservable behaviorを変更しない限り更新しない。実装中に表示/selection/remove操作契約が変わる場合だけ同じunitで更新する。

- **Completion acceptance:**
  - `rg` でchart-info hydration/backfillから `ILr2ChartInfoTrustPort`、`ChartInfoCompletedLr2SongDbSyncTrustSnapshot`、`TryCreateAllCurrentHydrationResultFromCompletedLr2SongDbSync`、`source=completed_song_db_sync` が消えている。
  - `lr2_song_db_sync_status` のproduction参照はLR2 song/folder generated data sync lifecycleに限定され、chart-info currentnessの入力ではない。
  - LR2/standaloneの両方で、missing/deleted `chart_info` と削除済みcurrent failureが同じstartup candidateになる。
  - actual-data current info/current failureだけの正常DBでは、両モードともcandidate summary/full backfillをskipできる。
  - inline/full/LR2 `song_rows` が一つのsnapshot evaluatorを使用し、LR2専用parser/failure builderが残っていない。
  - full backfillが対象BMS `song` generated columnsまでdurably補完し、user columnsを保持する。
  - commit失敗時にDB/canonical/indexのpartial publicationがない。
  - `devdocs/spec/chart-info-lifecycle.md` がcurrent contractの正本となり、関連spec間の矛盾が解消され、旧parse-failure planが競合して残っていない。
  - 各unitのfiltered Quick、必要なparser compatibility lane、最終Functional、repo-static-reviewが完了している。

## RISKS

- **LR2モードの通常hydrationコスト:** synthetic shortcut削除によりLR2でも実DB hydrationが走る。`chart_info_hydration` の `dbLoadMs`、`indexBuildMs`、`ownerApplyMs`、`totalMs` とstartup install readiness時刻をstandalone/変更前ログと比較する。background契約を守り、問題が実測された場合だけ両モード共通のkey-only/owned-only hydration最適化を別計画にする。
- **currentness semantic drift:** 三経路の優先順位やtimeout判定がずれると再parse回数または失敗skipが変わる。共有evaluatorのdirect parity testsとexisting LR2/full/inline testsで検出する。
- **duplicate owner projection:** MD5 groupingで一rowを複数pathへ適用するため、先頭ownerだけ更新する不具合が起こり得る。duplicate MD5の全owner DB rowとcanonical ownerを検証する。
- **transaction/publication順:** DB commit前のowner/index更新は異常終了時の不整合を作る。failure injectionとstatic reviewで、copy作成、transaction、receipt、canonical/index/eventの順を確認する。
- **user columns破壊:** BMS rowのreplace/upsert方法を誤るとfavorite/tag/adddateを失う。bulk generated writerの利用とpreservation testを必須にする。
- **BMSON境界の混同:** 共通化によりBMSONをLR2 `song`へ投影する危険がある。BMSON no-song-row testとspecで分離する。
- **parser compatibility regression:** evaluator移動でdecode、digest、timeout、exception handlingが変わる可能性がある。`ParserCompatibilityFull` とproduction diff系既存テストの失敗を期待値更新で隠さない。
- **Functional timeout/flakiness:** Functionalはcommand全体180秒以内。timeoutや明白な長時間化を検出したらprocess tree、TRX、parallel shared state、fixed wait、I/O、fixture量を調査する。現在unitのinvariantに起因するtest問題は同じunitで直し、横断的runner問題は独立再計画する。runner/lane/parallelism/fixture配置を変更する場合は同条件Functional 3回、各180秒以内、tracked file不変、残留processなしを確認する。
- **仕様の多重正本:** lifecycle説明が既存specと旧planへ重複すると再度分岐する。新規lifecycle specを正本にし、関連specは責務別の要点とlinkへ整理し、完了済みparse-failure planを退役させる。
