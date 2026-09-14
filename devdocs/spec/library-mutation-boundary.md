# Library Mutation Boundary

本資料は、譜面行、保留パッケージ、導入済みパッケージに対する破壊的操作の UI / model 境界を定義する。長パス I/O の正本は [path-length-and-io.md](path-length-and-io.md) だが、ユーザー操作としての実行順、二重実行防止、dialog 表示タイミングは本資料を正本にする。

## 基本方針

- 譜面 / パッケージのUI変更操作は、選択譜面、重複、保留、導入等のfeature workflow ownerを入口にする。`MainWindowViewModel` はshellとcompositionを担当する。
- 各workflowは既存の操作受付、再生停止、UI refresh suppression、操作中表示、operation dialog scope、操作後refreshを管理する。共有の `ChartMutationActivityOwner` はactivity表示を担い、モデルの変更許可は既存のmutation lease / capabilityで判断する。
- mutation 中は譜面行 / 保留行 / package 行の context menu open と command 起動を拒否する。context menu の enable 判定で UI thread から file existence check を走らせない。
- `BMSLibrary` の writer lock 中に `Dispatcher.Invoke`、message box、UI event callback、`Task.Wait` / `.Result` のような同期待ちは行わない。
- 失敗を隠す fallback は追加しない。必要な確認が取れない場合は処理を進めず、境界違反は明示的な失敗にする。
- 複数譜面・複数フォルダ・複数パッケージを一回の利用者操作で変更する場合は、`1 user operation = 1 mutation session / N changes` を変更管理の中心契約とする。item 単位の判定や filesystem 操作が逐次でも、canonical DB / state / index / required publication を item ごとに完結させない。

## ViewModel 境界

`SelectedChartMutationWorkflowOwner`、`DuplicateMaintenanceWorkflowOwner`、`PendingPackageWorkflowOwner`、`PackageInstallWorkflowOwner` 等が、対象操作の表示と実行手順を管理する。フォルダ名の直接編集は `RegularChartListOwner`、自動renameは `FolderAutoRenameWorkflowOwner` を入口にする。旧 `MainWindowViewModel.RunChartPackageMutation(...)` は現行コードには存在しない。

workflow側の責務は次のとおりである。

- 共有activityから操作中かどうかを表示側へ公開する。
- 操作中は LR2 song DB 同期や譜面 / package 操作の再入をブロックできる状態にする。
- 必要なら対象譜面の再生を停止する。
- refresh抑制の開始・終了を表示側へ伝え、操作後にpending install tree、library main view、folder tree、duplicate treeなどの必要channelを更新する。
- `BMSLibrary.BeginOperationDialogScope()` を開始し、model lock を抜けた後で蓄積された warning / error dialog を一度だけ表示する。

境界は、確認 dialog の意味を決めない。ユーザー確認が必要な操作は、mutation 実行前の preflight で ViewModel が dialog を表示し、結果を explicit decision として `BMSLibrary` へ渡す。

### 現行のモデル側の責務

file操作の確定内容は `LibraryCatalogMutationFacts`（削除・譜面/フォルダ移動）と `LibraryPackageReferenceFacts`（導入先・package参照）へ分け、入力列を構築時にコピーする。failure・件数・timingは操作別reportへ分離し、索引の要否判断には使わない。folder移動の既存row-path公開方針は明示的なpolicyで保持する。汎用Deltaと任意追加のroot入口は退役し、追加行は実install targetまたはscan replacementからのみ流す。

`Lr2SynchronizationOwner` が共有の変更leaseを発行し、`LibraryFileOperationSynchronization` とcatalog依存の受付が、操作ごとのscopeとpath収束条件を接続する。既存file ownerを再編した `LibraryMutationOwner` が、削除・移動・マージと、導入・chart-info・maintenance・走査producerの確定結果から、共通反映と公開順序を管理する。利用者起点の multi-change 操作では `LibraryMutationOwner` が operation-scoped mutation session を所有し、各 physical success から得た typed facts を session change として集約してから canonical surface へ反映する。`CatalogMutationOwner` はcatalogの保存・正本更新を、`CatalogOwnedCollectionOwner` は所持collection・hash・primary/full installed・playlist解決索引の状態と読み書きを所有する。

`BMSLibrary` は公開facade、producerの外側の受付・既存currentness同期、専門cacheと表示通知の接続を保持する。mutationの旧新facts組立て、semantic index反映、必須完了、公開用の操作内状態は `LibraryMutationOwner` のprivate実装であり、rootへ反映callbackを渡して代行させない。新しいcommand bus、queue、global gate、変更履歴cacheは置かない。追加ZIP予約やbackground処理等の受付例外は [並行性仕様 section 6](workflow-concurrency-and-complexity.md#6-操作種別ごとの共通既定と維持する例外) を維持し、UIの操作中表示だけで一律に拒否しない。

### 操作から確定・公開までの対応

表の「共通反映」は [LibraryMutationOwner.Common](../../BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.Common.cs) がfactsを判定して各専門ownerへ適用する処理を指す。各索引へ同じ更新を一律に流す意味ではない。未利用のoptional索引は不要構築せず、構築済み索引は旧新factsから差分更新する。明示走査の全置換と局所操作を区別する。

| 操作・入口 | 要求・確定事実とwriter | 共通反映・公開単位 | 実操作の検証 |
| --- | --- | --- | --- |
| 選択譜面削除、重複チェックのmerge | [LibraryMutationOwner](../../BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.cs) / [Merge](../../BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.Merge.cs)。確認済み対象を現在catalogへ解決し、旧kind/exact path/MD5/SHAを捕捉。削除は成功対象を一つのsessionへ集約し、mergeは単一changeでも同じsession APIを使う | 選択削除は全成功FS factsからcatalog/packageを一回反映し、成功したdirectory subtreeのreverse lookup除去はcatalog/required apply成功後の同session derived-state phaseで一回行う。catalog/required finalization failureでは先行除去しない。merge後maintenanceは同じuser operationのpost-commit phaseとしてterminalへ保持し、別のlibrary mutation sessionを作らない。sourceなしはlookup取得前に終了 | [LibraryMutationTests](../../BeMusicSeeker.Tests/OwnedChartCollectionLibraryMutationTests.cs)、[DuplicateServiceTests](../../BeMusicSeeker.Tests/BmsLibraryDuplicateServiceTests.cs)：独立2操作、後続getter、旧snapshot、種類別通知、FS/DB |
| 手動移動・rename、自動rename、拡張子修正、導入先修正 | 同ownerと既存coordinator。複数対象は `LibraryFolderMoveFacts` / `LibraryCatalogMutationFacts` / `LibraryPackageReferenceFacts` を一つのsessionへ追加する | 複数folder move、auto rename、通常拡張子修正はphysical changeを逐次実行してもcanonical applyは操作終端で一回。通常拡張子修正は `.b*` / `.p*` family の既存batchを一つのuser-operation sessionへappendし、per-family / per-file durable receiptを作らない。pending-only拡張子修正はpackage/install lifecycle ownerに留める。auto renameのLR2同期・reverse lookup・公開もsession終端へ集約する | [FolderRenameRefreshTests](../../BeMusicSeeker.Tests/BmsLibraryFolderRenameRefreshTests.cs)、[CatalogRelocationTests](../../BeMusicSeeker.Tests/BmsLibraryCatalogRelocationTests.cs)、[LibraryFileOperationsServiceTests](../../BeMusicSeeker.Tests/BmsLibraryLibraryFileOperationsServiceTests.cs) |
| 自動・推定先・強制導入、resource-only導入 | [PackageInstall](../../BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs) と `BmsLibraryPackageInstallService`。各packageの実FS結果を同じinstall sessionへ追加し、確定destinationの `ChartStorageTargetSet` / package factsを集約する | 先行packageの実成功だけをsession-local ownership overlayへ直ちに追加して後続分類へ使う。canonical storage/catalog/package state、resource index、maintenance target、required publicationは全package終端で集約反映する。譜面追加0でもresource/packageの必要changeをsessionへ残す | [PackageInstallServiceTests](../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)：4入口各16/128、連続操作、実FS/DB、resource警告、prefix |
| inline / background chart-info | [CatalogChartInfoOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogChartInfoOwner.cs)→typed storage write→DB/canonical digest→`PrepareOwnedChartDigestPublication` | 同じ反映結果を一度だけ組立てて索引へ適用。chart-info session更新とdigest window解放後に公開Actionを実行 | [InlineDigestTests](../../BeMusicSeeker.Tests/OwnedChartCollectionInlineDigestTests.cs)、[ChartInfoInlineHydrationTests](../../BeMusicSeeker.Tests/ChartInfoInlineHydrationTests.cs)：通知内のhash/session/playlist、failure |
| 通常・推定maintenance、resource警告ignore | 既存maintenance受付→CatalogMaintenanceOwnerの限定保存receipt→共通反映 | metadata/resourceの実変更だけを反映。membership差分0を理由に省略せず、hash不変でinstalled全失効を起こさない。既存lease・解放後公開 | [MaintenanceServiceTests](../../BeMusicSeeker.Tests/BmsLibraryMaintenanceServiceTests.cs)：連続差分、通知時状態、警告、DB |
| 起動走査・ReloadFileDiff・全再初期化 | [LibraryFileScanPipelineOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/LibraryFileScanPipelineOwner.cs)→`FileScanCatalogReplacementEvent` / residual facts | request/receiptとresource世代を共通反映へ渡す。全置換本来の失効を維持し、空residualで追加失効・通知なし。解放後公開 | [Lr2SongDbSyncTests](../../BeMusicSeeker.Tests/BmsLibraryLr2SongDbSyncTests.cs)、[RefreshTests](../../BeMusicSeeker.Tests/OwnedChartCollectionRefreshTests.cs)：readiness、通知内索引、旧snapshot、terminal |
| pendingのみの追加・削除・推定先設定、導入済みpackage記録のみの削除 | 既存PackageLifecycleOwner等の要求・writerを維持 | 所持catalogのmembership変更とは別の契約。導入・移動の複合操作に含まれる場合だけ共通反映から参照更新順を接続 | [PackageLifecycleOwnerTests](../../BeMusicSeeker.Tests/BmsLibraryPackageLifecycleTests.cs)、既存pending/導入workflow tests |
| encoding指定、playlist・score・IR関連更新 | `SetBMSFilesEncoding`→CatalogMaintenanceOwnerの限定write、playlist参照owner、BMS-only level writeback等の既存専門入口 | membership/path/hashの変更と区別する。encodingセル等の限定更新で所持collectionを全再公開しない。必要なwarning/maintenance表示の変更は共通effectへ接続 | [FolderRenameRefreshTests.SetBMSFilesEncoding_UpdatesEncodingCellWithoutLibraryCollectionChanged](../../BeMusicSeeker.Tests/BmsLibraryFolderRenameRefreshTests.cs)、[PlaylistReferenceServiceTests](../../BeMusicSeeker.Tests/BmsLibraryPlaylistReferenceServiceTests.cs)。score/IR等全機能の再検証を今回の受入としては扱わない |

writer / filesystem result が確定した内容を反映の正本とし、操作summaryの件数・failure・timingから対象を再推測しない。durable後の必須完了失敗と通知失敗を区別し、確定済みprefixを破棄しない。削除・導入・移動・metadataの各実入口と後続読取りで検証し、一つの内部applyテストを全操作の受入に代用しない。

### Mutation session 契約

利用者が確認・実行した一回のライブラリ変更 command は一つの logical mutation session が所有する。session は複数 change を持てるが、change 自体を durable session や public publication の単位にしない。単一対象の操作も同じ API を使ってよい。

1. **開始:** outer mutation lease / capability を取得した command owner が session を一回だけ開始する。item loop の内側で別 session を作らない。
2. **変更収集:** filesystem / package executor が確認した成功から immutable な `LibraryCatalogMutationFacts`、`LibraryPackageReferenceFacts`、storage target、resource / LR2 change を追加する。skip、collision、stale、failure は terminal facts として別に保持し、成功 change に偽装しない。
3. **逐次依存:** 後続 package の分類やdestination解決が先行成功に依存する場合は、session-local ownership / destination overlay を更新して参照する。この overlay は canonical DB、installed lookup、publication を更新した証拠ではない。
4. **commit / apply:** session 終端で facts を正規化・集約し、catalog、storage rows、package references、owned/index state、reverse lookup、LR2 sync、required publication を各 surface の契約に従って operation 単位で適用する。複数 DB を一つの物理 transaction に統合することは要求しないが、item 数だけ同じ canonical apply を繰り返さない。
5. **失敗:** deterministic な item-level no-op / rejection は操作結果へ集約して継続可否を機能契約で決める。予期しない FS / DB / required internal apply の例外では unsafe な後続を止め、確認済み成功、失敗対象、未処理対象をsession resultへ保持する。全成功 item の rollback、transaction replay、crash recoveryを通常契約にしない。
6. **公開:** required internal apply が完了してから、一つのsession resultに基づくpublic notification / terminal reportをlease解放後に行う。cache warmup等のbest-effort処理をitemごとに開始しない。

この契約への production route 移行は [operation-scoped mutation session 実装計画](../plan/library-mutation-session-batching-plan.md) を正本とする。移行中の既存 `FileDbMutationExecutor` / per-item receipt は互換性の恒久要件ではない。

### compositionに残す接続

共通反映の管理主体はBMSLibrary本体を保持しない。catalog/storage/owned collection/resource/package/playlist/LR2の既存ownerを明示依存として受け取る。rootに残る接続は次の既存責務のために使い、変更factsの再組立て・索引方針の選択は任せない。

| 接続 | 残す理由 |
| --- | --- |
| parent folder / duplicate group / install-estimation metadata cacheの失効 | 各cacheの既存stateとconsumerはrootに残る。共通反映が失効要否を決め、狭い命令として呼ぶ。parentのraw prefix規則やduplicate graphの再設計は対象外 |
| owned collection / normal refresh / storage rows / duplicate / parentの通知 | WPF側が購読する既存facadeのproperty eventへ接続するため。通知の順序・内容は管理主体で確定し、UI同期待ちを加えない |
| LR2 normal-folder receiptの投影・現在BMSの捕捉 | LR2 mode、登録root、既存current captureを使う専門処理。catalogのcommitとLR2同期の順序は管理主体に置く |
| 全所持resource maintenance targetの取得 | resourceの明示full operationが使う共通snapshot生成。局所targetごとの全件取得にしない |
| folder命名とduplicate repair path取得 | 既存設定・専門cacheに基づく対象準備。catalog/indexの共通反映callbackではない |
| merge後maintenanceの再受付 | mergeのlease解放後に別の既存maintenance予約を取得する操作契約を維持する。受理済みtarget反映で予約を取り直す用途には使わない |
| 性能・失敗ログ、reverse lookup warmup通知 | 既存診断channelとresource warmupへ接続する。新しいper-item同期I/Oやschedulerを導入しない |

公開後に必要な専門cacheの再計算、利用者による一覧全件列挙、cold索引の初回構築、明示全走査は残る。今回の受入は固定差分に対する不要な全構築・copy・sortの除去と整合性であり、21万譜面の実環境wall-clock短縮率は未測定である。

| 仕様項目 | 現行実装 | 既存の検証範囲 |
| --- | --- | --- |
| 選択譜面の確認・終了処理 | [SelectedChartMutationWorkflowOwner](../../BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs) | [同workflow tests](../../BeMusicSeeker.Tests/SelectedChartMutationWorkflowOwnerTests.cs) |
| 重複・マージの操作と表示 | [DuplicateMaintenanceWorkflowOwner](../../BeMusicSeeker/ViewModels/MainWindow/DuplicateMaintenanceWorkflowOwner.cs) | [同workflow tests](../../BeMusicSeeker.Tests/DuplicateMaintenanceWorkflowOwnerTests.cs)、[実mergeのmodel tests](../../BeMusicSeeker.Tests/BmsLibraryDuplicateServiceTests.cs) |
| 共有activityとモデルの受付 | [ChartMutationActivityOwner](../../BeMusicSeeker/ViewModels/MainWindow/ChartMutationActivityOwner.cs)、[LibraryFileOperationSynchronization](../../BeMusicSeeker/Models/BmsLibraryInternal/LibraryFileOperationSynchronization.cs) | [activity tests](../../BeMusicSeeker.Tests/ChartMutationActivityOwnerTests.cs)、[mutation boundary tests](../../BeMusicSeeker.Tests/BmsLibraryMutationBoundaryTests.cs) |
| catalog保存・正本更新 | [CatalogMutationOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogMutationOwner.cs) | [catalog owner tests](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs)。操作から後続索引構築までの性能受入の代用にはしない。 |

## Model 契約

`BMSLibrary` は、破壊的操作の結果として表示すべき OK dialog を operation report として蓄積できる。operation dialog scope が有効な場合、OK dialog は即時表示せず、scope の flush で表示する。

`YesNo` / `OKCancel` などの interactive prompt は、operation dialog scope 内では表示しない。必要な decision が渡されていない場合、`BMSLibrary` は暗黙の yes / no に倒さず、境界違反として明示失敗にする。これにより、model writer lock 中に UI thread を待つ経路を残さない。

scope 外から `BMSLibrary` を直接呼ぶ既存テストや内部ユーティリティでは、従来どおり dialog service を使う。ただし、アプリ本体の譜面 / パッケージ操作入口は ViewModel 境界を通す。

### Startup LR2 leap-year repair

起動時の LR2 leap-year 修復候補は、`BmsLibraryInitializationService.NormalizeSongTable` が既存の folder-table loop で一度だけ収集し、`SongTableLoadResult` に immutable な候補（catalog identity、正規化済み filesystem/display path、観測時刻）を返す。この loop は dialog、filesystem mutation、追加の folder-table enumeration を行わない。初期 lease と model lock を解放した後で候補ごとの確認を行い、承認された候補だけが第二の短い mutation lease に入る。

修復 lease 内では候補が保持する元の `folder.path` だけを `Lr2FolderExistingRowLookup.QueryExactPaths` へ渡し、対象 K 行を取得する。各行について catalog identity、directory existence、観測時刻との完全一致を再検証し、差し替え・消失・時刻不一致なら filesystem / DB mutation は 0 とする。確認済み候補の timestamp mutation と対応する catalog update が成功した場合だけ永続化し、mutation service が null の場合は明示的な failure とする。外部 DB 変更検出の全件 scan、無関係 path の mtime probe、全 row dictionary は追加しない。

## Chart-info Catalog Write

background hydration/backfillやpackage inlineのchart-info writeは、UI操作のworkflowとは別の入口を持つcatalog mutationである。transaction ownerは`CatalogMutationOwner`のままとし、inline/fullは同じ`ApplyChartInfoStorageWrite(CatalogChartInfoStorageWriteRequest)`を使う。

- requestはinline用BMS/BMSON persistence copy、full-backfill用immutable narrow projection、`CatalogChartInfoWriteRequest`をsnapshotとして束ねる。
- inline storage rowsまたはfull-backfill用song update、`chart_digest_map`、`chart_info`、parse-failure upsert/deleteは一つのcatalog transactionで保存する。
- full backfillは`Lr2SongDbWriter.UpdateChartInfoSongProjections(...)`でpath+MD5が一致する既存BMS `song` rowのchart-info由来9列だけをupdateする。full row upsertやmissing row insertは行わず、基本列、`mode`、`judge`、user列をUPDATE句へ含めない。BMSONからLR2 `song` rowは作らない。
- durable receipt後だけcanonical storage owner、digest/index、chart-info session index、warning/digest eventを更新する。commit失敗時はcanonical/index/eventを変更せず、対象は次回もcandidateとして残る。
- parse-failure明示削除などstorage rowを伴わない処理にはfacts-onlyの`ApplyChartInfoWrite(...)`を残す。
- DB transactionやmodel/storage lockを保持したままUI/event subscriberを待たない。

digestの確定factsは `PrepareOwnedChartDigestPublication` で共通反映結果を一度だけ組み立て、依存索引へ適用する。同じ結果の公開Actionをchart-info ownerへ返し、session index更新・digest mutation window解放後に実行する。prepareと公開でfactsを再組立てしない。通常・推定maintenanceのreceiptも `ApplyCatalogMaintenanceMutation` で共通反映へ接続する。

公開順序は `ChartInfoInlineHydrationTests.CatalogChartInfoOwner_InlinePublicationOrdersDigestEffectsAfterSessionIndex` の通知内hash/session/playlist読取りで、連続差分は `OwnedChartCollectionInlineDigestTests.BuildInlineChartInfo_WarmDigestDeltaStaysLocalAcrossTwoOperations` と `BmsLibraryMaintenanceServiceTests.RescanResourceHealthCharts_UpdatesCurrentResourceHealthIndexByDelta` で確認する。背景16/128件の仕事量確認は、本番規模のwall-clock測定を意味しない。

## ファイル走査結果の反映

全置換は `FileScanCatalogReplacementEvent` の確定request/receiptとresource世代を共通反映へ渡す。走査で解除した導入先は、別の `FileScanCatalogResidualEvent` にimmutableな譜面factsとして渡す。残余反映の効果は実factsから決め、汎用deltaの失効フラグを指定しない。空factsは追加反映・通知を行わないが、走査本来の全置換は省略しない。

解除後projectionは `CatalogStorageRowsOwner` の既存kind別exact-path索引から現在ownerへ再接続する。各対象で全storage rowを列挙しない。current-owned非空導入先を新規生成する本番経路は未確認のため、既存exact cleanup内部契約を維持し、新しい生成・回復保証は設けない。

`BmsLibraryLr2SongDbSyncTests.ReloadFileDiff_PublishesCatalogAfterLeaseReleaseAndIsolatesTerminalSubscriber` は実reloadから、通知時のhash/installed/playlist/resource、lease解放、後続取得と旧snapshotを確認する。pipeline・初期化・directory availabilityの既存testsでreadinessと失敗を、`OwnedChartCollectionRefreshTests` でtyped残余反映と空入力を確認する。

## 対象操作

P0 として次の操作は共通境界を通す。

- 譜面フォルダの移動、削除、マージ、フォルダ名変更、自動リネーム、拡張子修正。
- 導入済みパッケージ record 削除。
- 保留パッケージ / 保留譜面の導入先検索、マージ先検索、導入先設定、導入先解除。
- 保留パッケージ / 保留譜面の通常導入、手動導入、削除、source 削除、スマート上書き。
- 導入先修正で既存 duplicate record を消す可能性がある操作。

これらはファイル操作の意味上も P0 長パス対応範囲であり、実際の存在確認、コピー、移動、削除、タイムスタンプ更新は `LongPathFileSystem` / `IFileMutationService` を使う。

現在のlibrary catalog pathをauthorityとして実library BMS fileまたはcatalog membershipを変更する操作は、現在のcatalog generationがauthoritativeなfile diffでpath収束済みであることを必須とする。UI向けのnon-reserving preflightは未収束を先に拒否してよいが、authoritativeな受付判定は既存のexclusive `LibraryFileMutationLease`を先に取得し、そのleaseを保持したままprocess-localなreadinessをO(1)で再確認する。未確認ならleaseを直ちに解放して拒否し、filesystem、catalog DB、owned stateを変更しない。mutationごとの全catalog再走査は行わない。

この追加gateはshared mutation laneそのものには掛けない。playlist reload / playlist DB編集 / LR2 custom-folder生成・再出力、pending packageの追加・選択削除・全clear・pending sourceのrename / delete等、current library catalog pathを物理BMS targetのauthorityとして使わない処理は従来のraw exclusive mutation admissionだけを使う。auto install ingressもpackage discoveryとpendingへの投入は許可し、path未収束時は実libraryへのauto installだけを行わずinstallable candidateをpendingへ保持する。明示的なpending install、library chart delete / merge / move / rename、installation-directory repair等、実library BMS fileまたはcatalog membershipを変更する段階でgateを要求する。Startup file scan / `ReloadFileDiff` / `FullReinitialize`はreadinessを作る側なのでこの追加gateを通さず、従来のraw mutation leaseを使う。ScoreOnly initializationはreadinessを変更しない。scan / parse failureの扱いは[chart file read pipeline](chart-file-read-pipeline.md)と[path identity](path-identity.md#r2a-test-map)を正本とする。

## Dialog と Report

操作前に確認が必要な例は次のとおりである。

- 通常導入済み扱いの譜面を force install で上書きするか。
- 導入先修正で、同じ譜面を指す既存 record を削除するか。
- 譜面削除で、譜面ファイルだけでなく package folder 全体を削除するか。

これらは preflight で確認し、承認済み path や boolean decision として execution option へ渡す。decision が不足した状態で model lock 内 prompt に戻る実装は不可とする。

操作後の warning / error は report として蓄積し、`lockCopyFile` と model lock を抜けた後に dialog service で表示する。重複 warning を避けるため、同じ mutation の中で同じ message を複数 queue しない。

### 保留の登録BMSルート除外

保留の正本へ取り込む対象は、その取込み時点の登録BMSルート自身・配下を除外する。所持hashの有無や、譜面が既に索引へ載っているかには依存しない。登録ルートは既存の `Lr2SearchRootSnapshot.RequestedRoots`（LR2連携ではconfig、単体モードではSearchTargets）から取得し、譜面配置ディレクトリ集合や走査用の存在確認・出力先除外済み集合と取り違えない。

- DnD由来の導入では、`PrepareAutoInstallWorkflow` のdiscovery結果から登録ルート自身・配下を除外してから、自動導入・保留追加・install行保存へ進む。ルート外の正当なパッケージは同じドロップに含まれていても受け付ける。
- 起動・全再初期化では、`LoadInstallTable` が該当行を保留候補に入れず、既存のstale install行整理で削除してから正本の保留一覧を公開する。削除するのはinstall行だけであり、譜面・リソース・通常ライブラリのcatalog行は削除しない。旧版で混入した保留行もこの読込み境界で整理する。
- パス比較は既存 `LongPathFileSystem.IsSameOrDescendantDirectoryPath` の正規化・大文字小文字を区別しない同一／子孫判定を使う。文字列の接頭辞だけが一致する別フォルダは除外しない。削除・試聴・導入先設定など個々の保留操作には、登録ルートの再検証を追加しない。

**暫定仕様の制限:** 保留公開後の登録ルート変更を監視して、既存の保留全体を即時に再選別する仕組みは持たない。保留の元フォルダを含む領域を新しく登録した場合は、保留操作を続ける前にアプリを再起動するか全再初期化を完了させる。通常のファイル差分更新・プレイリスト更新だけでの再選別は保証しない。登録変更と保留操作が競合する場合の原子的な再選別も今回の保証外とする。

除外の対象はパッケージのsource pathが登録ルート自身・配下である場合に限る。登録ルートを内包する祖先ディレクトリ全体のパッケージ化、別名パスによる物理的な同一性の解決、取込み後の外部変更への防御は拡張しない。登録ルートの祖先を導入元としてドロップする運用は避ける。

回帰確認は `BmsLibraryPackageInstallServiceTests` の `InstallChartPackagesAuto_ExcludesRegisteredRootsWithoutChartIndex` / `PrepareAutoInstallWorkflow_ExcludesRegisteredRootsButKeepsOutsideSibling` と、`BmsLibraryInitializationInstallTests.ReloadInstallTable_ExcludesRegisteredRootsBeforePendingPublicationAndPreservesSources` に置く。既存fixtureの固有FS・DBと同期owner returnを使用し、新しいlane・共有設定・待機機構は追加しない。

### Pending chart legacy mutations

保留譜面の削除、無効拡張子の修正、導入行の差分更新は、確認時に対象 K 件だけの immutable projection を一度作る。projection は対象の file identity、package / entry membership、authorized path を保持し、無関係な pending package の chart entries を読み直さない。lease 内で live owner、package collection、entry、path と、選択された操作単位の source existence / type、directory / reparse safety を再検証し、失敗した対象は明示的な non-success として扱う。確認後の process-exclusive owner と transaction が authoritative であり、外部 DB の全件再読込や全 catalog 比較は行わない。

無効拡張子修正は canonical durable apply / publication が成功するまで live path / global index を変更せず、dialog / notification は release 後の best-effort effect とする。nested pending/install-row apply は現在の live owner が発行した非 null・未 dispose capability を必須とし、nullable / foreign / disposed capability を no-op fallback に変換しない。

#### 保留譜面削除の操作単位

利用者向け契約は [manual.ja.md の「保留」](../../docs/manual.ja.md#保留) の動作とする。「フォルダごと削除」は、譜面削除後に空ディレクトリだけを掃除する機能ではなく、同一パッケージの譜面が残らなくなる選択で、そのパッケージ全体を削除する機能である。

- **同一パッケージと判定されたディレクトリ**: discovery / pending owner が保持する `ChartPackage.path` がパッケージのディレクトリを指す。フォルダ削除 option が ON で、そのパッケージの remaining `ChartEntries`（子ディレクトリの譜面と BMSON を含む）がすべて選択されていれば、ディレクトリ全体を一つの削除対象とする。WAV、BGA、画像、説明書、子ディレクトリなども含む。リソースが残ることは削除を拒否する理由ではない。
- **単体譜面と判定されたパッケージ**: `ChartPackage.path` が譜面ファイル自身を指す。同じ親フォルダにある単体譜面をすべて選択しても、親フォルダは削除対象にならない。親パスをまとめ直してディレクトリパッケージ扱いにせず、`delete_parent` をこの削除の認可に使わない。元譜面パスが実行時にディレクトリへ置き換わっても、パッケージ区分を変更しない。
- **部分選択または option が OFF**: 選択された譜面ファイルだけを削除する。入れ子を含む未選択譜面やリソース、フォルダは残す。個別ファイルの失敗を理由に ancestor の削除へ fallback しない。部分削除後、残った最後の譜面を選択した次の操作では、ON ならパッケージ全体、OFF なら譜面だけを削除する。

全体削除は譜面の個別削除より先に、`IFileMutationService.DeleteDirectoryShell` へ渡す。`sendToRecycleBin` に従って `SendToRecycleBin` / `DeletePermanently` を選び、既存の `RecursiveDirectoryTree` correction options を渡す。パッケージ root の type / reparse 検証は維持するが、フォルダ全体の承認を個別 child の削除承認へ縮小しない。

全体削除が成功した package の譜面パスだけを `ChartPathsToRemove` に入れ、既存の owner / capability 境界で pending entries と `install` rows を反映する。全体削除が失敗した package は対象譜面件数を failed として数え、directory failure を一件報告し、その package のパスを成功扱いで除去しない。失敗・取消し後に個別ファイル削除を試みない。別 package の成功は独立して反映する。OS shell 自体が部分削除してから失敗した場合の自動 rollback / atomicity は主張せず、残存ファイルと保留行を保持して失敗を通知する。

## Verification map

保留削除 (`B2-01`–`B2-06`) は利用者のパッケージ単位の契約を検証する。recycle policy は既存 fake の呼出し記録で確認し、テストマシンの実ごみ箱は変更しない。

| Behavior | Fixture | Lane / completion |
| --- | --- | --- |
| 全体削除の resource / subtree、recycle policy、失敗時の非 fallback と pending / install row 保持、別 package の成功 | `BmsLibraryPendingLegacyMutationTests.RemovePendingCharts_WholePackagesHonorRecyclePolicyAndKeepFailedPackage` | remaining-bms-library / 固有 DB・FS、model return |
| discovery の入れ子譜面、部分選択から最後の選択、option OFF の保持 | `BmsLibraryPendingLegacyMutationTests.RemovePendingCharts_PartialThenLastSelectionHonorsWholePackageOption` | 同上 |
| discovery が単体と判定した同一フォルダの全譜面、`delete_parent` に関係なく親を保持 | `BmsLibraryPendingLegacyMutationTests.RemovePendingCharts_SingleFilePackagesKeepParentEvenWhenAllSelected` | 同上 |
| リソースを含む BMS / BMSON 全体削除、BMSON 非 materialization とフォルダ失敗結果 | `BmsLibraryPackageInstallServiceTests` の `DeletePendingCharts_*` | remaining-bms-library / 固有 FS、service return |
| 全選択候補の区分と単体 source の除外 | `BmsLibraryLibraryFileOperationsServiceTests.GetPendingPackagesFullyCoveredBySelection_ReturnsOnlyFullySelectedPackages` | remaining-bms-library / local values、service return |

FS+DB folder terminal reporting (`FSDB-A-20260905`, A01–A07) は以下で検証する。新しい lane、共有 logger 設定、visible window は使わない。

| Behavior | Fixture | Lane / completion |
| --- | --- | --- |
| R の実 LR2 finalizer failure、durable DB 保持、成功 refresh 抑止、解放後の集約表示 | `RegularChartFolderRenameTests` | remaining / rename Task と StopAsync、固有 DB・FS |
| M の session confirmed / failure facts 保持、解放後表示、optional notification failure | `SelectedChartMutationWorkflowOwnerTests` | remaining / MoveAsync Task、local ports |
| model の session-backed 表示抑止と互換・事前拒否、multi-folder の operation-level publication、confirmed prefix / failed / unprocessed、DB apply failure時の非rollback、BMS/BMSON storage-row通知 | `BmsLibraryFolderRenameRefreshTests` | remaining-bms-library / 固有 DB・FS、model return |
| severity、独立 failure 次元、操作件数、表示上限、通知 failure | `FileDbMutationReportTests` | remaining / local facts と presenter Task |
| accessor・resx・全言語の key、nonempty、placeholder schema | `LocalizationResourceParityTests` | 既存 lane / read-only resource return |
| library chart delete のsession receipt、catalog/required finalization failure時のresource reverse lookup非先行除去、success-only削除結果 | `OwnedChartCollectionLibraryMutationTests` / `SelectedChartMutationWorkflowOwnerTests` | remaining-bms-library / remaining、model return / workflow Task |
| 通常 invalid-extension rename の `.b*` / `.p*` family 一括session、terminal receipt、pending-only lifecycle分離 | `BmsLibraryFolderRenameRefreshTests` / `SelectedChartMutationWorkflowOwnerTests` | remaining-bms-library / remaining、model return / workflow Task |

`BmsLibraryStateApplierTests` の26 caseは、owner境界と `remaining-bms-library` の `ClassLevel` 実行単位を一致させるため、次の3 fixtureへ分けて維持する。

- `BmsLibraryStateApplierTests`: library initialization progress と `ApplyPackageReferenceFacts(...)` の13 case。
- `BmsLibraryPackageLifecycleTests`: pending package collection publication と durable pending-package delta の7 case。
- `BmsLibraryCatalogRelocationTests`: catalog relocation の storage-row、path、LR2 compatibility の6 case。

3 fixtureは既存の `BmsLibraryStateApplierTestSupport` が提供する GUID付き temporary song DB、package state callback、UI scheduler、completion / cancellation signalを共有する。ただし各 testの resource rootとDBは従来どおり個別に所有し、Functional の `remaining-bms-library` process、`ProcessorCount` worker、`ClassLevel` scopeから新しい laneや `DoNotParallelize`を追加せずに実行する。分割は test semantics、永続化結果、failure contract、cleanupを変更しない。

`BmsLibraryInitializationServiceTests` の108 casesは、既存の GUID付き song DB / filesystem、dispatcher/task/event completion、failure watchdog、cleanupを保持したまま、library ownerごとの5 fixtureへ置換する。BmsLibrary prefixを持つこれらのfixtureと `BmsLibraryZeroNoteRefreshTests` は `remaining-bms-library` processの `ProcessorCount` / `ClassLevel` routeで実行し、chart-info / startup owner fixturesはselectorのnegative側である `remaining` routeに留める。新しい process、DNP、fixed wait、timeout変更、production seamは追加しない。

| Behavior / failure contract | Owner fixture | Retired cases | Route |
| --- | --- | --- | --- |
| catalog / maintenance load, BMSON load, leap-year validation | `BmsLibraryInitializationLoadTests` | `BmsLibraryInitializationServiceTests` cases 1-9, 76-77, 107 | `remaining-bms-library`, `ProcessorCount` / `ClassLevel` |
| install initialization, pending package restoration, resource warning projection | `BmsLibraryInitializationInstallTests` | cases 47-49, 98-106 | same route |
| file scan diff, catalog mutation, deletion, path/date/hash preservation, scan cache | `BmsLibraryInitializationFileScanTests` | cases 10-28, 67-75, 78-97 | same route |
| LR2 folder and normal-folder synchronization and affected-scope pruning | `BmsLibraryInitializationLr2NormalFolderTests` | cases 29-46 | same route |
| inline chart-info / maintenance batches, current-row reuse, parse failure and callback publication | `BmsLibraryInitializationInlineChartInfoTests` | cases 50-66 | same route |

The old `BmsLibraryInitializationServiceTests` selector is absent from the route and the 45-class exclusion ledger. All current `BmsLibrary*` owner fixtures are automatically routed by the logical prefix selector to `remaining-bms-library`; no exact class allowlist is maintained. `BmsLibraryStateApplierTests` remains a separate owner map within that route above.

## 関連仕様

- [architecture.md](architecture.md): UI / model concurrency boundary。
- [path-length-and-io.md](path-length-and-io.md): 長パス対応 I/O、`LongPathFileSystem`、`IFileMutationService`。
- [warning-model.md](warning-model.md): warning の表示仕様。

## File / DB durable boundary

アプリ全体の FS+DB の保証・非保証、前方回復、失敗の表示、レビューで受け入れる制限は [file-db-consistency.md](file-db-consistency.md) を正本とする。以下の `COMP-*` は `FileDbMutationExecutor` を使う既存・移行中経路の限定補償契約であり、削除等を含むすべての mutation に FS rollback を要求するものではない。

package install、estimated install、smart overwrite、merge などの未移行経路には item ごとに `FileDbMutationExecutor` を完結するものが残る。一方、auto rename、手動 rename / 複数 folder move、library chart delete、通常 invalid-extension rename、導入先修正は operation-scoped session へ移行済みである。pending-only invalid-extension rename は owned catalog を変更しないため package/install lifecycle route のまま維持する。multi-change 操作の恒久契約は [Mutation session 契約](#mutation-session-契約) とし、immutable な preflight / destination type guard、source保全等の有用な局所安全策は維持してよい一方、DB durable receiptをitemごとに作ること自体は要件ではない。session移行後は、確認済みphysical successをchangeとして蓄積し、canonical durable applyをoperation境界へ集約する。source cleanupをdurable apply後へ遅延できる操作ではsession終端まで遅延する。

executor の receipt は commit 前後を区別する terminal state を持つ。

- `COMP-PREFLIGHT`: plan 完成まで filesystem / DB mutation は 0。staging と backup は destination の sibling でなければならない。
- `COMP-PRECOMMIT`: durable receipt 前の failure は source を保持し、DB 側の rollback と結果判定は gateway の transaction 契約に従う。同じ plan 内の promoted destination の取り消しと backup 復元は、指定された一つの owner が一回限りの best-effort compensation として行い、その成功時だけ FS 側の補償完了として扱う。別 item の durable success は取り消さず、補償失敗は `COMP-MANUAL` に従う。FS+DB 全体の原子性は主張しない。
- `COMP-MANUAL`: compensation failure は `ManualRecoveryRequired` とし、処理を直ちに停止する。source / backup / staging と recovery paths を保持し、後続 cleanup、再帰補償、自動 replay を行わない。
- `COMP-DURABLE`: durable success 後は compensate しない。destination と DB を authoritative とし、receipt 前の cleanup は行わない。
- `COMP-DURABLE-FINALIZATION`: durable filesystem / DB receipt 後の内部 finalizer exception は `DurableFinalizationFailed` とする。`DurableCommit=true`、compensation=0 とし、destination と DB を authoritative に保持する。finalization exception は receipt に保持し、cleanup exception が併発した場合も両方の診断事実と recovery paths を保持する。batch はその item で停止し、後続 mutation と通常 success publication を行わない。
- `COMP-CLEANUP`: durable success 後の cleanup failure は `CompletedWithCleanupFailure` とし、leftover と recovery paths を保持する。fresh install / pending retry には戻さない。
- `COMP-SUCCESS`: destination と DB が authoritative で、必要な内部 apply と post-commit cleanup が完了する。post-lease notification は下記の best-effort 契約に従い、通知失敗で durable result を変更しない。

移行前 executor の receipt / recovery paths と、移行後 session の confirmed / failed / unprocessed facts は、package / folder command の public result と UI workflow completion まで保持する。legacy の void / failure-list だけで terminal outcome を表現してはならない。

### Mutation terminal reporting

手動 folder rename、選択 folder move、auto rename、選択 library chart delete、通常 invalid-extension rename、導入先修正、duplicate folder merge の canonical terminal は operation-scoped session result とし、異常結果を操作終了後に一度だけ集約表示する。model lease、外側 gate、activity、dialog scope の終了処理を済ませてから既存 `IUiDialogService` を await する。全正常は無通知、cleanup-only は durable success を保持した Warning、未 commit・required internal apply failure・未確認 physical result は Error とし、混在時も confirmed success と failure / unprocessed を保持する。任意 subscriber／reporter の failure は診断だけに記録し、primary failure と session result を変更せず再通知・再実行しない。

表示は操作名、session result により確認した change 件数・確定済み／未確定・cleanup／required apply failure の各件数、確認候補、手動確認とログ参照の案内を含む。件数はファイル数ではない。候補は実在確認を行わず最大 3 件・各 240 文字、代表 error は最大 3 件・各 400 文字、本文は 4096 文字以内とし、全対象・例外は既存 logger へ best effort で記録する。自動 retry・復旧保証は案内しない。

移行中の `FileDbMutationReport` / `reportAtTerminal` は既存 executor receipt をこの terminal へ接続するためだけに維持できる。session 移行済み route に per-item report suppression のための新しい互換 flag を追加しない。receipt のない preflight／destination-exists 拒否は従来の通知を保持する。

### Exclusive lease and deferred effects

ファイルを変更する command は、preflight confirmation の後に短い sequence admission を行い、その sequence monitor を解放してから `LibraryFileMutationLease` を取得する。拒否時も sequence monitor と既に取得した scope を直ちに解放し、拒否 dialog を monitor 内で待たない。lease は LR2 同期と他の file mutation に対して排他的であり、session 内の filesystem changes、operation-scoped durable apply、required internal finalization と必要なcleanupが終端するまで保持する。移行前 executor route では既存 compensation / cleanup まで保持する。

folder move、auto-rename、merge の snapshot は initialized-min read、pending-install write、BMS-files write の順で取得し、逆順で解放する。その他の route は必要な短い route-specific snapshot lock だけを取得する。いずれの場合も snapshot / model / package / collection lock は filesystem I/O、DB apply、compensation / cleanup、内部 finalization の前にゼロに戻す。

ネストされた DB / catalog / file apply は、現在の outer lease から明示的に発行された `LibraryFileMutationCapability` を引数として渡す。capability は所有者、lease の生存、dispose 状態を検証し、ambient `AsyncLocal`、thread、monitor reentrancy を認可には使用しない。通常の外部 entry は同一 thread からの再入でも拒否する。

配置変更の反映範囲は、session に追加されたstorage/path、folder、install destination、installed package pathのconfirmed factsから決める。move・手動/自動rename・通常拡張子修正・導入先修正のcallerは索引失効を個別指定しない。自動rename、複数folder move、通常拡張子修正、導入先修正は外側のlive capabilityから一つのsessionを開始し、通常拡張子修正ではextension familyを跨いでもcanonical applyを一回にする。library chart delete は成功したfolder subtreeのresource reverse lookup除去をsession derived-stateへ登録し、catalog/required apply成功前に直接変更しない。権限なしの専用apply callbackは持たない。

この契約の実装移行は LibraryMutationOwner、AutoRenameBatchCoordinator、BMSLibraryの共通反映、LibraryFolderMoveCoordinatorを対象とし、詳細は [mutation session 実装計画](../plan/library-mutation-session-batching-plan.md) に従う。BmsLibraryFolderRenameRefreshTestsの実rename（背景16/128・2操作）、auto batch/部分失敗、repairと、BmsLibraryPackageInstallServiceTests.RenameBMSFilesExtensions_UnregistersOnlySuccessfulChartsAndPreservesHashOwner、関連workflow testsで、FS/DB、索引、旧snapshot、通知と受付を確認する。

`installable_maintenance` は自身の outer `LibraryFileMutationLease` を一度だけ取得し、mode detection と catalog maintenance をその lease 内の通常処理として capability-free に完了する。内側で lease を取り直さず、Unit A のこの route では `LibraryFileMutationCapability` を作成・伝播しない。capability を保持するのは、package の installed-target durable completion から LR2 normal-folder sync までを同じ outer lease でつなぐ実在の nested bridge だけであり、その bridge の under-existing-lease entry で owner / lease lifetime / dispose を一度だけ検証する。

LR2 custom-folder 出力を伴うローカル playlist 編集は、entry hydration と active-table の初期確認を終えてから、モデル変更前に同じ非ブロッキング lease を取得する。busy の場合は待機や内部 retry を行わず、モデル、playlist DB、LR2 folder row、生成ファイル、BMT queue を変更せずに明示失敗する。lease 取得後は active membership を対象 table だけ再確認し、短い table mutation、playlist DB apply、対象 custom-folder projection と LR2 row sync を同じ capability で完了する。terminal publication と BMT queue は lease 解放後に行う。`commitFlag=false` と LR2 mode 無効時はこの file-mutation lease を取得しない。

| user operation | owner | pre-admission 許可 | lease 内 model / file / DB | post-release notification / BMT / reference |
| --- | --- | --- | --- | --- |
| `playlistTableDrop -> AddRowsToFolderAsync`（ローカル playlist、LR2 custom-folder mode） | `PlaylistWorkspaceViewModel` が `BMSPlaylist` の drop owner route を呼ぶ | hydration と active-table 確認後に nonblocking lease を一回取得。busy は明示失敗し、全 durable / success effect を行わない | 同じ capability で table model、playlist DB、custom-folder file、LR2 folder/file row を完了。output failure は一次例外として保持する | lease 解放後に chart reference、UI invalidation、notification、BMT を各一回独立 attempt し、secondary failure は既存 diagnostic に記録して primary を置換せず、primary があれば元の例外を再送出する。primary なしの durable success は notification failure で失敗にしない。将来の JSON 副作用も UI caller ではなく owner の post-durable effect とする |

将来 JSON を追加する場合も、UI caller 個別の副作用にはせず、owner が durable completion 後の post-durable effect として一度だけ発行する。

`TryRunLr2SongDbSyncDataPreparation(...)` も admission を一回だけ試み、busy の場合は同じ呼び出し内で待機、lease 解放後の再開、内部 retry を行わず、false を terminal に返す。lease 解放後に再実行できるのは新しい明示 request だけである。LR2 preparation の playlist / builtin generated-data bridge は concrete runtime の nested entry に閉じ、request / DTO / coordinator / ordinary helper は capability-free semantic operation とする。

移行前の `FileDbMutationExecutor` は live outer lease 内で durable DB apply を終えた後、one-shot `DurableFinalizer` を実行する。receipt は callback-free の immutable terminal fact とし、receipt 自身の callback、replay、retry を持たない。この契約は残存 executor route に限定し、multi-change session の item ごとに durable finalizer を作る根拠にしない。

session 移行済み route は、全 confirmed changes の required internal apply を session commit で完了してから plain な one-shot publication action を command-owned の post-lease list へ記録する。lease と全 model lock を解放した後、その list を best-effort で実行し、subscriber / dialog / UI scheduler の失敗は durable / cleanup terminal state、retry、既存の primary failure を変更せず、後続 publication を中断しない。dialog、UI scheduler / Dispatcher、PropertyChanged / public subscriber、terminal progress / terminal publication、通常 refresh / index warmup、task start、別 owner callback はこの post-lease phase に遅延する。中間 progress だけは feature-local の narrow writer へ immutable fact を nonblocking に送れるが、owner 側 consumer は latest-wins の pending / draining を各1以下に制限し、model / package / collection lock を保持せずに配信する。writer は terminalization 開始時に seal し、同一 generation の late progress を捨てる。中間 progress や診断通知の失敗は session terminal state、retry、既存の primary failure を変更しない。

### 導入targetの確定と共通反映

自動・推定先・強制・resource-only導入は、確定destinationとstorage ownerを同じtargetとしてsessionへ渡す。DB用rowはdetached projectionで作り、session durable apply後にlive ownerのpath/folderとstorage/canonicalを更新する。入力を作るための一時的なlive path差替え・復元は行わない。通常変更とupsertの反映結果はstorage factsを起点とする共通組立を使い、upsert固有のexact replacementを保つ。packageごとのphysical successは後続判定用overlayへ反映するが、canonical確定境界はuser operationのsession終端とする。

resource-onlyで譜面targetが空ならowned collection versionを進めない。必要なinstall row削除、resource移動、package表示のdestination health/warningは反映する。移動後にも未充足の参照が残れば警告は保持する。操作内target再利用は同じchart集合のときだけ行い、追加対象を古いtargetで隠さない。

実装は ChartStorageTargetSet、CatalogMutationOwner、BMSLibrary.PackageInstallと共通反映。BmsLibraryPackageInstallServiceTestsの通常3入口 UsesPreflightDestinationAndWarmDelta と OverwritePendingInstalledOnlyPackagesResources_UsesWarmCatalogWithoutChartDelta が、背景16/128・同libraryの独立2操作・実FS/SQLite・通知後readback・旧snapshot・実work observerを確認する。DB失敗/先行prefix/inline metadataは既存関連fixtureと併せて検証する。

### package install destination coherence

`BmsLibraryPackageInstallService.MovePackageFilesWithReceipt` は、filesystem mutation plan の preflight 中に source path ごとの actual destination を一度だけ確定する。collision resolution や directory-relative projection を完了した後で、basename、source root、または destination root から chart path を再計算してはならない。receipt の destination、detached DB / storage projection、live package entry、installed package registration、duplicate merge の catalog delta は、同じ preflight map の destination value を使う。

既存 destination file との collision では、選択済みの collision suffix path だけを新しい chart の destination とし、旧 file と旧 DB row は変更しない。single-file package に installable chart がない cleanup-only case では、chart destination は作らず package path は preflight で選択した installation directory を保持する。map は package-install owner の immutable fact として扱い、generic file/DB boundary、retry、rollback、persistent recovery state は追加しない。

### Package source cleanup policy

`MovePackageFilesWithReceipt` は source cleanup の同意を `PackageSourceCleanupPolicy` として明示的に受け取る。`PreserveUnconsumedContents` は preflight で移動・消費した source だけを durable receipt 後に削除し、除外された譜面や同梱物を残す。`DeleteVerifiedResidualContents` は追加削除を許可するが、preflight で記録した source / `delete_parent` 範囲の全残存候補を評価する。候補がすべて対応する BMS / BMSON で読取・確定 hash 化でき、既存 catalog の独立した primary-hash 所持証拠に一致する場合だけ、その file 群をまとめて追加削除する。一つでも非譜面、読取不能、hash 不一致、未所持があれば追加分を全件保持する。source 自身や未実行の予約・計画だけは所持証拠に数えない。

`delete_parent` は候補範囲を記録するだけで、destination またはその祖先を cleanup 対象にしない。parent とその descendant directory は移動後に空であることを確認できる場合だけ深い順に削除し、残存 file がある場合は残す。追加 cleanup は destination の physical success と session durable apply が確定した後にだけ実行する。cleanup failure は該当packageのterminal factsとしてsession resultへ残し、canonical applyを再実行しない。後段のrequired projection / internal applyが失敗した場合は後続packageを開始せず、confirmed physical successとfailure phaseを同じsession resultへ保持する。通常、推定、auto、force、resource-only、merge の caller は同じ操作の設定 snapshot と独立した所持 hash snapshot を一度取得し、policy とともに同じsessionへ渡す。

LR2 preparation の中間 stage / table / batch progress は `BMSLibrary` の既存 facade dispatcher queue が latest-state として coalesce して配信し、lease 保持中に public `PropertyChanged` subscriber を同期実行しない。dispatcher drain 内の subscriber 例外はログ後に次の property を継続し、generated output、LR2 folder row、DB status の durable 結果や terminal failure を変更しない。


残存 executor route で compensation を所有するのは一つの owner だけであり、per-item owner や rollback-of-rollback は追加しない。session route では full-batch rollback を新設しない。crash replay、persistent journal、cross-volume atomicity、TOCTOU の解消はこの境界の主張に含めない。destination-exists の folder move は従来どおり reject とし、merge / overwrite は新設しない。

### Mutation session result consumers

自動 folder rename（選択／全件）、drop install、保留の強制／手動導入を含む multi-change route は、操作終了後に operation-scoped session result を一度だけ workflow terminal へ渡す。auto rename の refresh 判定、drop の登録 package 件数、保留 view 更新／navigation は既存条件を維持し、異常報告の条件には使わない。正常は silent、cleanup-only は durable success を保持した Warning、未 commit／required internal apply failure／未確認physical resultは Error とする。

外側 gate、activity、dialog scope cleanup の failure が session result 取得後に起きても、confirmed success、primary、cleanup、recovery / confirmation paths を terminal まで保持する。任意 report failure は診断のみとし、mutation／通知の再試行をしない。sessionを開始する前の事前拒否と無関係な lifecycle failure の伝播を維持する。移行前routeの `FileDbMutationReport` / `reportAtTerminal` は session result への置換が完了するまでの接続に限定する。

#### Verification map — FSDB-B-20260905

| IDs / behavior | Fixture | Completion / resource |
| --- | --- | --- |
| B1/B2/B4/B5: 実 mutation→production UI consumer、refresh=false Error、空 package Error、非空 Warning、正常 silent、report failure | `MainWindowPackageMaintenanceWpfTests` | dispatcher task／owner idle、固有 FS・SQLite、既存 application-lifetime fixture |
| B1/B4: rename cleanup 後の receipt 保持 | `FolderAutoRenameWorkflowOwnerTests` | failure event／idle、local gate/activity |
| B2/B4: drop cleanup 後の receipt 保持 | `PackageInstallWorkflowOwnerTests` | completion/failure event／queue idle |
| B3/B4: pending cleanup 後の receipt 保持と terminal severity | `PendingPackageWorkflowOwnerTests`、`MainWindowPendingPackageMutationViewTerminalTests` | awaited owner/terminal task、local ports |
| B3: 強制／手動の package/chart 四経路 | `MainWindowPackageMaintenanceWpfTests` | 既存 constructor-only harness、routed event completion |
| B5: auto rename canonical/legacy の個別通知対照 | `BmsLibraryFolderRenameRefreshTests` | real FS/model result、固有 DB |

既存 Functional lane を使い、新しい DNP／固定待ち／process／共有 logger 設定は追加しない。drop の cleanup-only が空 package となる直積は要求しない。

既存の estimated cleanup-only 正常完了案内は保持する。同じ batch に異常 receipt が含まれる canonical route の場合だけ、この案内を一回の異常 report へ統合し、正常部分は durable 操作件数に残す。legacy と receipt のない案内は抑止しない。`BmsLibraryPackageInstallServiceTests.EstimatedCleanupKeepsNormalAdviceButDefersMixedAbnormalAdviceToTerminal` が実 cleanup の正常／異常と canonical／legacy の対照を検証する。

### 導入先修正後の保守再検査

`FixInstallationDirectoryCharts` は outer file-mutation lease の中で一つの `LibraryMutationSession` を開始し、選択された譜面の package physical move だけを逐次実行する。兄弟譜面・resource・親 directory は cleanup 対象にしない。`CreateInstalledChartKeySnapshotExcludingChartsUnsafe` は操作開始時の baseline を一回作り、先行した physical repair success の primary hash だけを session-local overlay へ追加する。後続 duplicate 判定に canonical DB commit を使わない。

各 package move は `MovePackageFilesPhysicalWithReceipt` の detached physical result として扱い、executor-local receipt を user-operation terminal に昇格させない。衝突採番後の実 destination から `CreateFixMutationFacts` を作り、成功した path / install-destination facts を session へ append する。item loop 内で `ApplyLibraryMutationFactsForFileMutation` を呼ばず、全 confirmed repair facts を操作終端で一回 apply する。重複は move 前に分類し、未承認なら source を保持する。承認済みなら filesystem delete phase を同じ outer lease 内で実行し、その confirmed removal / package-reference / resource-directory facts を repair session へ追加する。別の `RemoveLibraryChartsCore` や別 session へ再入しない。

成功した repair の owner を session commit 後に取り直し、移動後の BMS / BMSON を `forceUpdate: true` で一回の保守再検査へ集合化する。新しい場所にリソースが存在するかを DB と表示へ反映し、移動前の不足情報をそのまま成功結果にしない。この後処理は取得済み file-mutation lease を使う `ApplyCatalogMaintenanceUnderExistingReservation` へ接続し、通常の外部受付を再呼出しして自分自身の予約と競合させない。外部操作からの再入拒否は維持し、lane や再入許可は追加しない。保守の DB 反映が終わるまで外側の lease を保持し、通知は command-owned の post-lease list へ渡す。

physical success 後の session catalog apply または保守 DB の失敗は同じ `LibraryMutationSessionReceipt` の apply / finalization failure として呼出し側へ伝え、成功済み filesystem change を source へ rollback しない。予期しない physical failure では現在 item と unsafe suffix を止める一方、既に confirmed な prefix facts は同じ session commit へ残す。cleanup failure は durable meaning を変えず同 receipt に保持する。

`BmsLibraryFolderRenameRefreshTests.FixInstallationDirectoryCharts_RechecksResourcesUnderExistingReservation` が BMS / BMSON の移動前不足→移動後充足、DB の保守値、警告、解放後通知を検証する。後半の保守 write だけを SQLite trigger で失敗させる対照も含む。既存の overlay のみを見る BMS 修正テストを置換し、固有 FS / DB と同期 model return を使用する。Functional の `remaining-bms-library` route は変更しない。

#### フォルダ統合後の保守との区別

`MergeChartDirectory` は file-mutation lease の解放後に統合先を再検査するため、導入先修正とは別の `applyMergeFolderMaintenanceAfterRelease` へ接続する。通常の `ApplyCatalogMaintenance` が新しい予約を取得し、既存の `forceUpdate: true` / `DeferOnUpdates` / `merge_folder` の契約を維持する。取得済み予約用の処理を無予約で流用せず、導入先修正を通常受付へ戻すこともしない。

`BmsLibraryDuplicateServiceTests.MergeChartDirectory_RechecksResourcesAfterReleasingMutationReservation` が、実際の BMS / BMSON、統合先だけに存在する WAV、保守 DB の更新、警告解除、DeferOnUpdates と解放後通知を検証する。既存の統合失敗・衝突・receipt のテストも維持し、新しい lane / fixture / 待機処理は追加しない。

### Library deletion terminal facts

`LibraryChartRemovalOutcome` は削除 executor が既存 API 呼出し時に観測した chart target と、既存 catalog owner の apply attempted／durable／failure を保持する callback-free immutable result とする。削除 API が正常 return した対象だけを確認済み件数に含める。exists=false は削除未実行・実在未確認、directory 削除例外は配下の削除結果未確認として保持し、新しい probe／rescan／DB purge は行わない。catalog 失敗で確認済み FS 結果を捨てず、durable 後の必須反映失敗を未 commit や cleanup warning に読み替えない。

選択削除・重複 hash 削除は、outer gate／activity／dialog scope／model lease の解放後に `LibraryChartRemovalReport` へ一回だけ結果を渡す。正常は silent、未実行・未確認・stale・unresolved・FS failure・catalog failure は Error。確認済み件数、未確認対象、catalog 段階、手動確認・詳細ログ案内を表示する。path は最大3件・各240字、代表 error は最大3件・各400字、本文4096字まで。任意 report failure は既存診断のみとし、結果変更や再通知をしない。削除固有 facts を folder receipt に偽装しない。

導入先修正内の承認済み duplicate delete は独立した delete command ではなく repair session の removal change である。`LibraryFixInstallationResult` は moved / duplicate-skipped / approved-removed の count と一つの `LibraryMutationSessionReceipt` を保持し、physical failure、catalog apply failure、required maintenance failure、cleanup failure を同じ operation terminal に残す。pending owner は outer finally 解放後にこの receipt を `FileDbMutationReport` へ一回だけ渡し、repair 内の approved deletion を別 `LibraryChartRemovalReport` で二重報告しない。catalog／必須反映失敗後は success-only の追加処理へ進まず、成功済み physical move / confirmed delete を取り消さない。

R5 の検証は `BmsLibraryPackageInstallServiceTests`（設定 ON/OFF、範囲外所持証拠、親保護、resource-only、確定 prefix）、`BmsLibraryFolderRenameRefreshTests`（実 FS/SQLite の BMS・bmson 修復、衝突、重複承認、LR2 列、保守 failure）、`BmsLibraryDuplicateServiceTests`（統合と解放後保守）、`PendingPackageWorkflowOwnerTests`（解放後の移動・削除結果報告）を使う。旧 bool 移動・修復 forwarding のテストは共通 receipt と実 model の保証へ置換し、独立した互換経路として残さない。

#### Verification map — FSDB-C-20260905

| IDs / behavior | Fixture | Completion / resource |
| --- | --- | --- |
| C1–C3: real library ingress、成功／exists=false／directory部分変更後throw、song DELETE abort、durable後folder DELETE abort | `OwnedChartCollectionLibraryMutationTests` | 同期 library return、固有 FS／SQLite、既存 file adapter／LR2 config helper |
| C1/C4: BMSON pending row の確認済み削除 | `BmsLibraryDuplicateServiceTests` | real library outcome／DB readback |
| C4: keeper維持、計画2件中確認済み1件、解放後一回報告 | `DuplicateMaintenanceWorkflowOwnerTests` | awaited owner Task、local gate/activity/dialog |
| C4/C6: catalog failure／任意 reporter failure後もfacts保持 | `SelectedChartMutationWorkflowOwnerTests` | awaited owner Task、gate再取得／activity inactive |
| S4-APPROVED-REMOVAL / S4-FAILURE-TERMINAL: repair move + approved duplicate removal の同一session、catalog / maintenance failure の非rollback | `BmsLibraryFolderRenameRefreshTests` | real repair return／session receipt、固有FS／SQLite trigger |
| S4-FAILURE-TERMINAL / C6: repair session terminal の解放後一回報告、無関係exception伝播 | `PendingPackageWorkflowOwnerTests` | awaited owner Task、local ports／post-release dialog |
| C6: unknownはError、正常silent、bounds／任意reportfailure | `LibraryChartRemovalReportTests`、`LocalizationResourceParityTests` | renderer return／dialog Task、read-only resources |

既存 Functional lane を使う。新 DNP、固定待ち、共有 logger 設定、アプリ lifetime fixture は追加しない。削除個別 failure dialog、結果を失う catalog throw、計画件数を実績とする旧 route は上記 terminal と confirmed outcome へ置換する。

### 通常ライブラリの親子フォルダ削除

これは保留パッケージの「フォルダごと削除」とは別の、通常ライブラリの選択削除の契約である。利用者のフォルダ削除承認を preflight で取得することと、子が実際に削除できたかという execution fact を区別する。

`BuildLibraryChartRemovalPlan` は path / kind / 承認済み操作とともに、各親フォルダの全体削除が成功を前提とする子の target indexes を記録する。`ExecuteLibraryChartRemovalPlan` は、該当する全 child の削除 API が正常終了した場合だけ、承認済み親フォルダを再帰削除する。child の失敗・未実行時はその親を再帰削除せず、親グループ自身の選択譜面だけを個別削除する。失敗した子への再試行・親からの巻取り削除は行わない。

一方、全 child が成功した場合のフォルダ全体削除は、リソースや説明書を含め従来どおり行う。無関係な枝や、名前の prefix が似ているだけのフォルダの失敗で他の成功可能な処理を止めない。未選択の譜面が残る場合、または全体削除を承認しない場合は、選択譜面だけを削除する。

DB / owned catalog から除去するのは、現在の executor が確認済みとした target indexes だけとする。失敗した子の譜面・リソース・行を保持し、成功した親自身の譜面や独立したフォルダは反映する。shell 自体の部分失敗を atomic / rollback 可能とは主張せず、既存の Unconfirmed / NotExecuted を維持する。新しい FS probe、purge、永続状態は追加しない。

未使用だった `BmsLibraryLibraryFileOperationsService.DeleteLibraryCharts` と `LibraryRemovalResult`、その専用の結果集計・参照解除 helper は退役する。通常の削除は `BMSLibrary.RemoveLibraryCharts` → canonical/preflight → path-only plan/executor → capability 下の catalog apply → post-lease report だけを使う。

#### Verification map — R2

旧サービス単体の `DeleteLibraryCharts_*` 8件は退役し、`OwnedChartCollectionLibraryMutationTests` の実 owner / DB テストへ置換する。

| Contract | Fixture method | 元の coverage / 追加条件 |
| --- | --- | --- |
| R2-01/02 | `RemoveLibraryCharts_ParentDeletionDependsOnObservedChildResult` | 子が BMSON だけのフォルダ成功、子 directory 失敗・未存在・file 失敗、承認親自身の続行、独立した似た名前の枝、ごみ箱 / 完全削除 |
| R2-03 | `RemoveLibraryCharts_UnselectedDescendantKeepsResourcesAndRows` | 入れ子の未選択譜面・resource・DB 行を保持 |
| R2-04 | `RemoveLibraryCharts_ResolvesSelectionThroughProductionOwner` | path-only、同一pathの別インスタンスを canonical owner へ解決 |
| R2-04 | `RemoveLibraryCharts_UnresolvedSelectionKeepsFilesystemAndDatabase` | 非catalog、同一hash別path、pathを持たなくなったownerの旧選択を削除しない |
| R2-03/04 | `RemoveLibraryCharts_LastChartHonorsWholeFolderConfirmation` | 最後の譜面の確認、Yes / No の実挙動、DB と FS の一致 |
| R2-05 | `RemoveLibraryCharts_WholeFolderClearsInstallDestinationsForBothFormats` | BMS/BMSON 削除、pending/library の導入先・提案・警告解除、adapterless BMSON 保持 |

共有 helper は既存 `OwnedChartCollectionTestSupport` に限定し、FS failure 注入と呼出し時の recycle policy 記録だけを追加する。成功の確認は同期 model return、FS / SQLite readback、owned collection、immutable outcome。通常の Functional `remaining` route、固有の一時 FS / DB を使い、新しい process / lane / DNP / 固定待ちは追加しない。
