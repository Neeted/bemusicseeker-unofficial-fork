# Path Identity Policy

本アプリは Windows / NTFS を主なターゲット環境とするが、永続化された `path` の同一性はファイルシステムの一般的な大文字小文字規則へ暗黙に寄せない。

パス長や実ファイル I/O の長パス対応は [path-length-and-io.md](path-length-and-io.md) を参照する。本資料は DB 行の path identity、現在の path 集合への収束、および異なる path 間の保存値引継ぎを区別して定める。実装済みの保証と採用済み・未実装の要件は分けて記載する。

## 目的

LR2 互換の `song` / `folder` schema は `path TEXT primary key` であり、`COLLATE NOCASE` ではない。従来版 BeMusicSeeker や LR2 などが同じ `song.db` を利用すると、ファイルシステム上は同じ実体へ解決される path でも、大文字小文字の異なる文字列を別の DB 行として登録し得る。

本アプリが path を exact に扱う目的は、そのような既存行を取り落とさず識別し、スキャンや確定した操作結果に基づく現在の path 集合へ収束させることである。物理的に別のファイルだとみなすためでも、case-only の複数行を恒久的に残すためでもない。実際に同じファイルへ解決される path も、異なるファイルの path も、DB 行集合の差分では同じ規則で扱う。

## 基本方針

- `song.path`, `folder.path`, `maintenance.path`, `bmson_song.path`, `install.path` など、DB の主キーまたは行同一性として使う `path` は **case-sensitive な exact string** として扱う。
- exact に異なる path は別の DB 行の識別子である。大文字小文字だけの差分か、それ以外も異なる差分かで、行集合の追加・更新・削除規則を分けない。
- 行 identity を比較する境界では、case fold、`Trim`、`GetFullPath` などで旧 DB key を別文字列へ変換してから照合しない。各 workflow が入力時に行う storage path の生成・正規化は別責務であり、その契約を一律に除去しない。
- 削除には **削除対象の旧 DB 行が持つ exact key** を使う。追加・更新には **現在のスキャンまたは確定した操作結果が持つ exact path** を使う。旧 key を現在の表記へ置き換えてから削除しない。
- `File.Exists` や通常のパス解決結果だけで、スキャン差分の既存行を current と判定したり stale 判定を覆したりしない。case-only の path が同じ実体へ解決されても、行集合の照合には exact set を使う。物理操作の可否・成功確認は各 workflow の FS 契約に従う。
- 行集合の収束と、異なる path 間で `favorite` / `tag` / `adddate` などを引き継ぐ relink は別の規則である。relink の有無を理由に stale 行を残したり、現在の行を統合したりしない。

<a id="convergence"></a>
## 収束の範囲と共通規則

正常に差分反映できる対象範囲について、既存集合を `D`、現在のスキャン集合を `S` とすると、exact 比較で `D ∩ S` を維持・更新、`D − S` を削除対象、`S − D` を追加対象にする。`A` と `B` が exact に異なる限り、次の規則は case-only の差分にも、それ以外の差分にも同じように適用する。

| 反映前の DB 行集合 | 現在のスキャン集合 | 正常反映後の行集合 |
| --- | --- | --- |
| `{A, B}` | `{B}` | `{B}`。既存 B の保存値を A で上書きしない |
| `{A}` | `{B}` | `{B}`。保存値の引継ぎ可否は relink の規則で別に判定する |
| `{A, B}` | `{A, B}` | `{A, B}`。どちらも current なら別行として保持する |
| `{B}` | `{B}` | `{B}`。同じ入力の再反映で不要な追加・削除を繰り返さない |

この表は、対象範囲の入力が信頼でき、必要な読取・登録が成功し、既存の保護条件を満たした場合の行集合の規則である。不完全走査、利用不能 root、空走査保護、個別 read / parse failure、途中の DB failure を無視して全 DB を `S` へ置き換える指示ではない。[ファイルと DB の整合性](file-db-consistency.md) の保護・部分成功・非保証を維持する。`maintenance` だけの残存行などの整理も、各 workflow が明示する対象範囲に限る。

局所的な install / delete / move / merge は、確定した操作結果から更新・削除する exact key 集合を組み立てる。上流で複数の旧行を対象と確認した場合は、それぞれの exact key を渡す。下流の request / DB / storage rows / owned collection / package 反映が case-insensitive 一致で勝手に対象を拡大したり、必要な行を一つへ畳んだりしてはいけない。局所操作の完了時の不一致を、後の file diff が修復する前提にしない。

軽量 `ReloadFileDiff` は現在の in-memory collection と scan result を比較し、DB を再読込しない。外部アプリによる DB 編集を取り込み直すには `FullReinitialize` を使う。任意の reload が外部編集を含む全 DB を無条件に収束させる保証はない。詳細は [chart-file-read-pipeline.md](chart-file-read-pipeline.md) に従う。

## `COLLATE NOCASE` の扱い

`COLLATE NOCASE` は path identity には使わない。

使用してよい例:

- `.lr2folder` など拡張子や表示用ソートのように、行同一性ではない比較。
- md5 / sha256 / bundle id など、path ではない識別子の正規化や表示順。
- Windows 向けの探索範囲最適化。ただし、探索 scope と行の認可集合は区別し、DB 更新・prune では取得した旧行の exact key と現在の exact set から対象を確定する。

避ける例:

- `path TEXT PRIMARY KEY COLLATE NOCASE` の一時テーブルで current path を保持する。
- `WHERE existing.path = current.path COLLATE NOCASE` で `song`, `folder`, `maintenance`, `bmson_song`, `install` の更新対象を決める。
- `NOT EXISTS (... COLLATE NOCASE)` で stale row prune を行う。
- NOCASE 一致だけを根拠に、旧行の key を現在の表記へ単純 `UPDATE` して二行の区別を失う。明示的な move の old / new facts による更新とは区別する。

## LR2 `song.db` での責務

`song` の membership（追加・削除・stale prune）は file diff / scoped incremental route が担当する。LR2 full reconciliation は既存 `song` row の生成列を更新するが、`song` の membership は変更しない。`folder` の full reconciliation は別の完全な生成入力から期待集合を作る。各同期の範囲は [lr2-song-db-generation.md](lr2-song-db-generation.md) に従い、「LR2 同期」という総称で全行の収束を保証しない。

file diff で扱う責務は次のとおり。これは意味の分担であり、既存の chunk / transaction / 公開境界を一つの巨大 transaction に変更する指示ではない。

1. 現在スキャン結果から exact current path set と差分対象を作る。
2. relink に必要な旧 BMS 行の保存値は、削除前に旧 exact key で snapshot する。
3. 現在 path の `song` / `maintenance` を exact match で upsert し、対象範囲内の stale `song` 行を旧 exact key で削除する。
4. 削除対象の `song` 行に対応する exact `maintenance` と、残存 owner のない orphan digest を整理する。同じ MD5 の他配置まで消さない。
5. 採用済み relink 条件を満たす場合だけ保存値を現在 path の行へ復元する。現行実装との差分は後述する。

DB 内の行を区別したまま処理することで、case-only の複数行がある場合も、現在の exact path の行を巻き込まず収束させる。これは、外部編集・I/O・DB 障害を含むすべての状況で同期が成功する保証ではない。

## 通常スキャン差分

通常スキャンで得た譜面 path も exact set として扱う。

- `ChartScanResult.ChartFilePaths` と `ChartFileEntriesByPath` は case-sensitive な exact key にする。
- `RootFileEnumerationResult` の group 内 path 集合 / entry map と、managed fallback scan の chart file 集約も exact key にする。
- 既存 `song` / `bmson_song` 行との比較は exact path で行う。case-only かどうかを問わず、現在スキャンにない既存行を stale とし、新しい current path は通常の追加 / upsert 対象とする。
- `OwnedChartCollectionState` や `LibraryChartRefIndexSnapshot` など、アプリ内の owned chart path lookup も exact key にする。
- 現在の同じ exact path の行に属する保存値の維持と、異なる path からの relink を分ける。MD5 一致を path identity の代用にしない。

ディレクトリ探索、リソース探索、`.lr2folder` の prefix scope、拡張子判定など、ファイルシステム探索に近い処理は Windows / NTFS 前提の case-insensitive 比較を使うことがある。DB 境界では、消す旧 exact key と登録する現在の exact path を区別して扱う。

## 局所catalog mutationの実装境界（R2a）

`OwnedChartRemoveRequest.Path`、`CatalogStorageRowsRemovalRequest`のcleanup集合、DBの削除対象、storage rows、owned/ref lookup、installed packageのchart pruningは、未加工のexact pathを同じ規則で照合する。明示されたmaintenance-only行も対象へ含める。通常moveの新exact pathは同じkeyのcleanupから保護し、別keyのcleanupを妨げない。

導入先のupsertは同じexact keyだけを置換し、`ChartStorageTargetSet`のBMSON集約も同じkey内に限定する。構築済みinstalled lookupとprimary-only lookupは、catalogで置換する旧行だけの所持hashを除く。別caseやdot成分を含む別keyの追加で、残るownerの所持を失わせない。path-only選択や別instanceの再解決もexact keyを使い、未登録の別表記をFSの同一性だけで削除対象へ解決しない。

mergeはsource scopeに含まれる旧exact keyをすべて保持する。一方、detached packageの物理source・owner copy・package rootだけを既存FS正規化へ揃え、case-insensitive比較で一回の処理にまとめる。集合の比較keyだけでなく実際のpackage pathも揃え、component列挙が同じファイルを再投入しないようにする。代表にしない旧行もcatalog分類から落とさず、確定した移動先へのrelocationと、残りの旧keyへのPathCleanupに分けて同じcatalog transactionへ渡す。これはfile diffの同一MD5 relinkや、新しいcase-only物理renameの実装ではない。

現在のlibrary catalog pathをauthorityとして実library BMS fileまたはcatalog membershipを変更する操作は、現在読み込んでいるcatalog generationについてauthoritativeなfile scanからexact path集合のdiffとcanonical storage replacementまで完了し、file-diff pipelineがrequired publicationのhandoffまで正常に到達した後だけ受付ける。startup / FullReinitialize / ReloadFileDiffがcatalogまたはfile-diff workを開始するとこのreadinessを未確認へ戻し、正常収束したpipelineだけが再び受付を開く。ScoreOnly initializationはcatalog membershipを再読込・再収束しないため、既存のreadinessを変更しない。

起動時file diffを設定で省略した場合、scan surfaceがnon-authoritative / incompleteだった場合、既存DBを保護するempty-scan skipになった場合、またはfile diff / canonical applyが失敗した場合はreadinessを開かない。この状態でmerge / delete / library install等のcatalog依存file mutationを要求しても、既存のexclusive mutation leaseを取得した入口でO(1)のreadiness確認により拒否し、filesystem / catalog DB / owned stateを変更しない。収束を作るStartup scan / FullReinitialize / ReloadFileDiff自身はこのgateの対象外で、既存のraw mutation leaseを使う。mutationごとの全catalog走査やcase-insensitive duplicate scanは追加しない。playlist reload / playlist DB / LR2 custom-folder操作と、pending packageの追加・削除・clear・source cleanup等はcurrent library catalog pathを物理BMS targetのauthorityとして使わないためgate対象外とする。auto install ingressではdiscoveryとpending投入を許可し、未収束時は実libraryへのauto installだけを抑止してinstallable candidateをpendingへ保持する。

譜面単位のrecoverableなread / lightweight parse failureは、それだけではscan surfaceをnon-authoritativeにしない。読めずcatalog rowを生成できなかった譜面はcurrent exact path集合のrowにならず、他のenumerated pathの収束を妨げないため、authoritative enumerationとcatalog applyが正常に完了すればreadinessを開いてよい。`chart_info` parse failureも同様に`song` / `bmson_song`登録を止める条件ではない。directory enumeration等によりscan surface自体がincompleteな場合とは区別する。

whole-folder削除のようにphysical folderの成功factを起点に後続stateを更新する場合も、folder containment / deletion判定はfilesystem規則を使い、どのcatalog rowのinstall-destination stateをclearするかはexact row pathで識別する。row identityとphysical target identityを同じcomparerへ統合しない。

R2bではPathCleanupの全表materialize・孤児確認を対象集合へ限定し、R2cではresource-health indexのchart path keyをexact identityへ揃えた。storage listの全件再構築削減はR5aに残る。R2a単体はこれらの完了や大規模速度改善を保証しない。

## 局所 PathCleanup の対象集合（R2b）

R2b の PathCleanup は、上流で確定した未加工の exact cleanup key と owner から解決した現在の削除 path だけを削除集合にする。入力順や重複は集合化しても path の大小文字・dot 成分を変えず、`COLLATE NOCASE` や filesystem alias で対象を広げない。存続する relocation owner の destination exact key は保護し、削除対象 owner の destination は保護集合へ含めない。明示された maintenance-only path も `song` 行の存在を待たずに同じ exact key で cleanup する。

BMS の削除では、削除前に対象 path から得た `song.hash` と削除 owner の hash を候補集合へ集める。削除後に `song.hash` の残存 owner がある digest は保持し、最後の owner を失った候補だけ `chart_digest_map` から削除する。BMSON は `bmson_song` と共有 `maintenance` の対象 pathだけを更新し、LR2 `song`・BMS digestの処理へ混ぜない。DB gateway は既存の一時 lookup table と集合 SQLを同じ transaction 内で使い、catalog 表の全行 materialize と hash ごとの孤児確認を行わない。処理量は実接続のtrace_v2 PROFILE callbackから各statement完了時の `SQLITE_STMTSTATUS_FULLSCAN_STEP` と `SQLITE_STMTSTATUS_VM_STEP` を取得し、同じ4件のΔを背景16件・128件で比較する。FULLSCAN_STEPは全表scanだけを数え、ROW callbackが0のINSERT SELECTやindex range traversalを表さないため、VM_STEPをstatement実行仕事量のproxyとして併用する。`EXPLAIN QUERY PLAN` は実SQLの補助診断として併記するが、空の一時表を別接続へ再構成した計画であるため処理量判定には使わない。

### 実装・テスト対応表 — R2b

| 仕様項目 | 実装 | テスト |
| --- | --- | --- |
| `ExactCleanup` | [`BmsLibraryDbGateway.cs`](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の `DeleteBmsMutationRows` / `DeleteBmsonMutationRows` | [`CatalogMutationOwnerTests.cs`](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) の `ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership`、`ApplyCatalogMutation_RemovalDeletesMaintenanceWhenSongRowIsMissing` |
| `DestinationProtection` | [`BmsLibraryDbGateway.cs`](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の relocation destination 保護集合 | [`CatalogMutationOwnerTests.cs`](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) の `ApplyCatalogMutation_PathCleanupDoesNotRemoveRelocatedDestination` |
| `DigestOwnership` | [`BmsLibraryDbGateway.cs`](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の `BulkDeleteBmsPaths` / `DeleteChartDigestsIfOrphanedFromTemp` | [`CatalogMutationOwnerTests.cs`](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) の `ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership` |
| `BmsonIsolation` | [`BmsLibraryDbGateway.cs`](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の `DeleteBmsonMutationRows` / `BulkDeleteBmsonPaths` | [`CatalogMutationOwnerTests.cs`](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) の `ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership` |
| `DbFailure` | [`BmsLibraryDbGateway.cs`](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の既存 transaction 境界 | [`CatalogMutationOwnerTests.cs`](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) の `ApplyCatalogMutation_WhenRemovalFailsRollsBackRelocationAndLiveVersions` |
| `BoundedDbWork` | [`BmsLibraryDbGateway.cs`](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の exact path/hash temp set と集合 SQL | [`CatalogMutationOwnerTests.cs`](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) の背景16件・128件対照、および [`SqliteStatementObservation.cs`](../../BeMusicSeeker.Tests/Helpers/SqliteStatementObservation.cs) の実接続PROFILE/FULLSCAN_STEP/VM_STEP観測 |

## Resource-health index の chart path identity（R2c）

`ResourceHealthIndexSnapshot`のchart keyは、catalog rowと同じくpath成分を未加工のexact identityとして扱う。Windows上の物理file identityやdirectory探索用のcase-insensitive比較を、warning projectionのrow keyへ流用しない。

- keyのkindとMD5は既存どおりcase-insensitiveに比較し、pathだけをordinal exactに比較する。hash codeも同じ比較規則と一致させる。
- full buildでは、同じkind / MD5でもcase-onlyに異なるpathは別targetとして保持し、それぞれのmaintenanceから独立したwarning projectionを作る。projection lookupもexact pathで解決する。
- deltaの「同じchart」はkind + exact pathで判定する。同じexact pathのrehashでは旧hashのtarget / projectionを除いて新hashへ置換するが、case-onlyの別pathを削除・更新対象へ広げない。
- delta入力の重複排除はfull buildと同じkeyを使うため、case-onlyの別exact pathを一件へ畳まない。`ResourceHealthIndexOwner`のtarget normalizationもnull除外だけとし、case-insensitive dedupeを追加しない。
- snapshotはdelta適用時に新しいdictionary / set / target listを構築し、更新前snapshotのtarget countとprojectionを変化させない。

この変更はkey比較の是正だけであり、新しいcatalog走査、DB query、全件copy、fallback、retryを追加しない。約21万譜面規模でのterminal wall-clock改善はR2c単独の受入条件ではなく、R2cは誤ったcase-foldによるtarget欠落と巻添え更新を除く正しさの修正である。

### 実装・テスト対応表 — R2c

| 仕様項目 | 実装 | テスト |
| --- | --- | --- |
| `ExactResourceHealthPathKey` | [`ResourceHealthWarningProjection.cs`](../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealthWarningProjection.cs) の `ResourceHealthChartKey.Equals` / `GetHashCode` | [`BmsLibraryMaintenanceServiceTests.cs`](../../BeMusicSeeker.Tests/BmsLibraryMaintenanceServiceTests.cs) の `GetChartsNeedResourceFix_CaseOnlyExactPathsKeepIndependentResourceHealthProjections` |
| `ExactResourceHealthDeltaIdentity` | 同 `ResourceHealthChartKey.HasSameChartIdentity`、`DistinctValidTargets`、`RemoveMatchingChartIdentity` | [`BmsLibraryMaintenanceServiceTests.cs`](../../BeMusicSeeker.Tests/BmsLibraryMaintenanceServiceTests.cs) の `ResourceHealthIndexSnapshot_CaseOnlyExactPathsRemainIndependentAcrossDeltaAndRehash` |
| `ResourceHealthHashRule` | 同 `ResourceHealthChartKey.Equals` / `GetHashCode` のMD5比較 | `GetChartsNeedResourceFix_CaseOnlyExactPathsKeepIndependentResourceHealthProjections` のhash case対照、`ResourceHealthIndexSnapshot_CaseOnlyExactPathsRemainIndependentAcrossDeltaAndRehash` のexact path rehash対照 |

<a id="r2a-test-map"></a>
### Test map — R2a

この表のSpec IDを恒久的なbehavior識別子とし、テストコード側は`Class.Method`を対応IDとして参照する。DataTestMethodは同じmethod IDの各data rowで対照条件を持つ。実行日時、TRX、Quick / Functionalの成功記録は現行仕様には保持しない。

| Spec ID | 現行保証 | 対応するテストコード ID |
| --- | --- | --- |
| `R2-EXACT-REMOVE` | 明示された旧exact keyだけをDB / storage / owned / packageから除去し、別exact rowを巻き込まない | `BmsLibraryStateApplierTests.ApplyLibraryMutationDelta_ExactRemovalKeepsOtherRowsAndPackages`; `CatalogMutationOwnerTests.ApplyCatalogMutation_CaseVariantPathCollisionRemovesOldExactRow` |
| `R2-MISSING-ROW` | `song`がなくても明示されたexact maintenance rowをcleanupし、無関係rowへ広げない | `CatalogMutationOwnerTests.ApplyCatalogMutation_RemovalDeletesMaintenanceWhenSongRowIsMissing` |
| `R2-EXACT-UPSERT` | 同じexact keyだけを置換し、case-only / dot別表記のowner・保存値・hash所持を保持する | `CatalogMutationOwnerTests.ApplyInstalledTargetUpsert_PreservesEveryExactKey`; `OwnedChartCollectionInstalledOverlayTests.ApplyInstalledChartStorageTargets_BuiltLookupUpsertUsesOwnedPathExactView`; `OwnedChartCollectionProjectionTests.FromStorageRows_FiltersPathlessMd5lessAndExactDuplicateRows` |
| `R2-PROTECT` | 通常moveの確定destination exact keyだけを保護し、別exact cleanup keyを過剰保護しない | `CatalogMutationOwnerTests.ApplyCatalogMutation_PathCleanupDoesNotRemoveRelocatedDestination`; `BmsLibraryStateApplierTests.ApplyLibraryMutationDelta_PathCleanupDoesNotPruneRelocatedDestination` |
| `R2-TARGET-SET` | 上流で確定した複数exact rowを全層へ渡し、case foldで落とさず、未確定rowを追加しない | `BmsLibraryDuplicateServiceTests.MergeChartDirectory_ConsumesEveryConfirmedExactSourceKey`; `OwnedChartCollectionLibraryMutationTests.RemoveLibraryCharts_CaseOnlyExactRowsAreBothRemoved`; `OwnedChartCollectionLibraryMutationTests.GetLibraryWholeFolderDeleteConfirmationPaths_PreservesCaseOnlyNestedSelections` |
| `R2-PHYSICAL-ALIAS` | 選択済みexact rowが同じ物理fileを指す場合はfilesystem mutation結果を共有するが、failure時にalias経由でretryしない | `OwnedChartCollectionLibraryMutationTests.RemoveLibraryCharts_PhysicalAliasExactRowsAreBothRemovedWithoutWholeFolderDelete`; `OwnedChartCollectionLibraryMutationTests.RemoveLibraryCharts_PhysicalAliasDeleteFailureIsNotRetried` |
| `R2-MUTATION-GATE` | path収束未確認ではcatalog依存file mutationをFS / catalog DB変更前に拒否し、authoritative ReloadFileDiff後は受付を再開する。playlist / pending-onlyのshared mutation laneは止めず、auto-install ingressはcandidateをpendingへ保持する。譜面単位のrecoverable parse failureだけでは受付を閉じない | `BmsLibraryDuplicateServiceTests.MergeChartDirectory_AfterStartupWithoutFileScanRejectsUntilReloadFileDiffConverges`; `BmsLibraryInitializationFileScanTests.Initialize_StartupParseFailureStillOpensCatalogFileMutationAdmission`; `BmsLibraryInitializationFileScanTests.Initialize_StartupWithoutFileScanKeepsPlaylistLeaseAvailableAndUsesSettingWarningForCatalogMutation`; `BmsLibraryPackageInstallServiceTests.RemovePendingPackagesAll_UnconvergedCatalogStillClearsPendingWithoutWarning`; `BmsLibraryPackageInstallServiceTests.InstallChartPackagesAuto_UnconvergedCatalogKeepsDiscoveredPackagePendingInsteadOfInstalling` |
| `R2-EXACT-LOOKUP` | path-only / owner lookupはexact row keyで解決し、directory探索用のFS identityをrow認可へ流用しない | `OwnedChartCollectionReferenceIndexTests.CreateSnapshotForPaths_ProjectsOnlyRequestedPaths`; `OwnedChartCollectionReferenceIndexTests.CreateLibraryChartRefIndexSnapshot_ResolvesPathOnlyAndCountsRealPathSubtree`; `OwnedChartCollectionLookupMembershipTests.ContainsKnownChart_UsesOwnedReferenceAndKindPathExactLookup`; `OwnedChartCollectionLibraryMutationTests.RemoveLibraryCharts_ResolvesSelectionThroughProductionOwner`; `OwnedChartCollectionLibraryMutationTests.RemoveLibraryCharts_UnresolvedSelectionKeepsFilesystemAndDatabase` |
| `R2-INSTALL-STATE` | file-scan residual、runtime overlay、row projection、whole-folder後clearでinstall-destination stateをexact row pathへ結び、hash比較規則は維持する | `InstallDestinationStateOwnerTests.ReattachFileScanResidualInstallDestinationCharts_UsesExactPathWhenAliasHashMatches`; `InstallDestinationStateOwnerTests.OverlayRuntimeStates_UsesExactPathAndCaseInsensitiveHash`; `InstallDestinationStateOwnerTests.CreateOverlaySnapshot_PreservesCaseOnlyRowsWithSameHash`; `ChartListVirtualViewTests.RowProjectionTransientState_UsesExactPathAndCaseInsensitiveHash`; `OwnedChartCollectionLibraryMutationTests.RemoveLibraryCharts_WholeFolderClearsCaseOnlyExactInstallDestinationsIndependently` |
| `R2-CONVERGE` | 正常file diffでは既存/currentのexact集合差分へ収束し、case-onlyも通常差分と同じrow規則で扱う | `BmsLibraryInitializationFileScanTests.ApplyFileScanDiff_CaseOnlyBmsPathMismatchReplacesExactPathWithoutMigratingUserColumns`; `BmsLibraryInitializationFileScanTests.ApplyFileScanDiff_CaseOnlyBmsonPathMismatchAddsExactPathWithoutMigratingMaintenance`; `BmsLibraryInitializationFileScanTests.ApplyFileScanDiff_CaseOnlyBmsonPathMismatchReplacesExactPathAndConverges`; `BmsLibraryInitializationInlineChartInfoTests.ApplyFileScanDiff_BulkDeleteKeepsExactPathKeys`; `Lr2SongDbWriterTests.UpsertGeneratedSongs_TreatsCaseOnlyPathAsDistinct` |
| `R2-REAPPLY` | 同じ正常scanの再適用では不要なrow追加・削除を繰り返さない | `BmsLibraryInitializationFileScanTests.ApplyFileScanDiff_CaseOnlyBmsPathMismatchReplacesExactPathWithoutMigratingUserColumns`; `BmsLibraryInitializationFileScanTests.ApplyFileScanDiff_CaseOnlyBmsonPathMismatchReplacesExactPathAndConverges` |

source文字列assertionやprivate workflowの直接呼出しをこの契約の正本にはしない。file diffの保存値relink期待値は[Relink](#relink-policy)の実装状態に従い、R2aのSpec IDへ混ぜない。

<a id="relink-policy"></a>
## Relink: 保存値引継ぎ

### 採用済み方針

通常 file diff の BMS moved-hash relink は、**同一 MD5 の削除候補と新規追加候補が一対一の場合に、旧行の `favorite` / `tag` / `adddate` を新行へ引き継ぐ**規則に統一する。case-only の組だけを除外せず、通常のファイル名・ディレクトリ名の差分と同じ条件を使う。既存の通常 relink を全廃する方針ではない。

これは同一 MD5 の差分候補を使った保存値の引継ぎであり、実ファイルが同一である証明や、アプリが明示的に move した事実ではない。行集合は relink の成否にかかわらず exact path で確定する。

- 削除候補はその file diff で stale と確定した旧 BMS 行、新規候補は exact path の既存行に一致せず、読取・軽量 parse から登録対象になった BMS とする。現在 path の既存行は新規候補にしない。
- 同一 MD5 ごとの候補数を、それぞれ exact path の別行として数える。旧一件・新一件であり、旧 exact key の保存値 snapshot を取得できた場合だけ引き継ぐ。hash の既存の比較規則は変えない。
- 旧候補が複数、または新候補が複数なら引き継がない。case-insensitive の dedupe、case-only の優先、列挙順の first-win で一対一に見せない。保存値の取得失敗を理由に旧候補数を減らして曖昧さを隠さない。
- `DB={A,B}` / `scan={B}` では、既存 B を維持・更新し、stale A を除く。A と B が同じ MD5 でも B の保存値を A から上書きしない。
- 引継ぎ対象は `Lr2SongUserColumns` の三列に限定する。新しい path、mtime、folder / parent CRC、生成 metadata、resource-health / encoding / warning 等の `maintenance` は現在の入力から作り、旧行全体を移植しない。通常差分・case-only 差分とも同じ規則とする。
- BMSON には LR2 song user columns の relink を新設しない。BMSON の行集合は同じ exact 規則で収束させ、異なる path の maintenance を自動移植しない。同じ exact path の既存情報の維持は各 workflow の契約に従う。
- 現行の旧保存値 snapshot、DB commit 後の exact destination への復元、memory 側の対応値、復元失敗の伝播を維持する。chunk を跨ぐ全原子性、自動 retry、過去の保存値を再起動後に復旧する仕組みは追加しない。

### 実装状態と作業境界

現行の `FileScanParseCommitOwner.PrepareMovedBmsUserColumnRestores` は通常の一対一 relink を持つが、`IsCaseOnlyPathPair` で case-only の組を除外している。したがって **上記の統一化は採用済み・未実装**であり、この文書変更だけで挙動や既存テストの期待値が変わったとは扱わない。

[ライブラリ変更計画](../plan/BeMusicSeeker-library-mutation-performance.md) の **RELINK-1** で、この除外と対応するテストを独立して変更する。R2aの局所行identity修正には含めず、DB処理の限定化はR2b、resource-health keyのexact identityはR2cで管理する。通常スキャンの行集合の規則は維持し、RELINK-1の完了時にこの実装状態を更新する。
