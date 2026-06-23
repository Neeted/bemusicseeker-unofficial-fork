# 導入先推定 現行仕様

この資料は、BeMusicSeeker の導入先推定処理の正本です。実装履歴ではなく、現行コードが前提にしている入力、候補生成、評価、tie-break、confidence の意味をまとめます。

関連する主な実装は `BMSLibrary` と `BmsLibraryInstallEstimationService` です。package 側の入力 snapshot は `PackageInstallEstimationSnapshot`、resource index は `LibraryResourceIndex` / `DirectoryResourceLookupCache` を正本にします。

## 目的

導入先推定は、保留 package や選択譜面について「どの既存 chart directory に入れると譜面が要求する resource を最も満たせるか」を推定します。

推定結果は次へ反映されます。

- `INSTL DST`
- `INSTL DST TITLE / ARTIST`
- low-confidence warning
- destination suggestions
- auto install の可否判断

導入処理そのもの、ファイル移動、DB 更新はこの資料の対象外です。

## 入力

### Target chart set

推定対象は package または loose file の chart 群です。複数 chart の package では、対象 chart 群の resource reference を union して評価します。

target snapshot は `ChartResourceSnapshot` で、resource を次のカテゴリへ分けます。

- audio
- image
- movie
- optional image

resource key は chart-relative な extensionless key です。

- `foo.wav` は `foo`
- `sound/foo.wav` は `sound/foo`
- `./foo.wav` は `foo`
- `sound/./foo.wav` は `sound/foo`
- `sound/foo.v2.wav` は `sound/foo.v2`

`foo` と `sound/foo` は別 key です。`sound/foo.wav` が `foo` に fallback することはありません。

`..` を含む resource path は親ディレクトリ参照として扱います。BeMusicSeeker は親ディレクトリ参照を導入先推定の lookup key へ入れず、`ChartResourceSnapshot` の unsupported reference として保持します。target chart set に unsupported reference がある場合、その package / entry は導入先推定不能になり、`UnsupportedResourcePath` warning を表示します。

`ChartResourceSnapshot` の `ResourceReference` は、この extensionless resource key と hash を保持します。複数 chart の package で aggregate snapshot を作る時は、各 chart の正規化済み key と unsupported reference をそのまま union し、raw path として再正規化しません。

### Source package surface

package 内に同梱されている non-chart resource は `BundledResources` として持ちます。

通常のインストール先推定では `candidate + bundled` を評価します。つまり、package が持ち込む resource も導入後に使えるものとして数えます。

merge / reinstall correction では source/bundled resource を足さず、`candidate only` で評価します。

source package surface の再帰探索は bounded scan で行います。導入先推定は単体譜面の親 directory や directory package 全体を source surface として見るため、ドライブ root や大量のサブディレクトリを持つ場所を起点にすると、通常の BMS package の範囲を大きく超える可能性があります。

- source surface scan は filesystem entry を訪問しながら上限を確認し、上限を超えた時点で打ち切ります。
- 既定上限は 1 つの source surface ごとに 50,000 entry です。これは投入バッチ全体の合算ではなく、1 つの導入先推定対象 package / source directory を BMS package 境界として扱えるかの上限です。
- 1 つのフォルダ投入から 50 作品分の package が発見された場合、各作品が別 source surface として評価される限り、50 作品全体の合計 file 数が 50,000 を超えても打ち切り理由にはなりません。
- 一般的な BMS package は兄弟ファイルやサブディレクトリを含めても 10,000 file 未満を想定し、1 source surface で 50,000 entry を超える場合は異常に広い source として扱います。
- 打ち切った source surface は部分的な resource surface として推定に使いません。部分結果で「候補なし」と判定すると意味が変わるためです。
- 打ち切り時は `SourceSurfaceScanLimitExceeded` warning を付与し、`INSTL DST` の自動推定を行いません。
- source surface scan では Everything bridge の全件 materialize 経路を使わず、ストリーミング可能な bounded scan を使います。

### Pending resource health projection

保留 package の `WAV/BGA/MOVIE` health 表示は、package 代表ではなく `PackageChartEntry` 単位の pending projection です。package を pending に分類する判定では resource warning の有無を package 単位で使いますが、health projection 自体は resource reference を持つ全 entry に書き戻します。

`AlreadyInstalled`、`SingleBmsFile` / `SingleBmsonFile`、nested chart warning などの package warning と resource health は排他ではありません。warning の優先表示は既存の warning digest 合成に任せ、health 列は同じ entry の一時 maintenance snapshot から表示します。

### Library resource index

通常起動の file enumeration 結果から `LibraryResourceIndex` を作ります。導入先推定で使う正本は `DirectoryResourceLookupCache` のカテゴリ別 index です。

- audio basename / relative key
- image basename / relative key
- movie basename / relative key
- self-owned audio / image / movie
- category reverse lookup

`ChartResourceKeyHash` は拡張子なし resource key の静的 hash helper です。導入先推定の candidate 列挙や matching の正本は `DirectoryResourceLookupCache` のカテゴリ別 chart-relative key で、旧 `FolderAllFileList` / all-base union view は使いません。導入先推定の final evaluation は relative-only であり、category 別 basename hash は照合にも audio gate にも使いません。

`DirectoryResourceLookupCache` がない状態では導入先推定を行いません。`SkipInitFileCheck` のように起動時 resource index を作らない設定では、推定不可になる場合があります。

## 推定入口

### 通常 package 推定

未所持 chart を含む package に対して、外部 chart directory を探します。

- 既所持 chart だけの package は通常推定しません。
- mixed package では、まず既所持 chart の実配置先を hash index から候補 directory 集合として解決します。
- hash 一致先の score が単独最多なら、その directory を `INSTL DST` に自動設定します。
- score 同点の候補が 2 件以上なら、その候補集合だけを通常推定と同じ final evaluation へ渡します。
  - 候補限定の resource / metadata evaluation で一意に勝つ viable candidate があれば自動設定します。
  - resource / metadata が完全同等の場合だけ、候補 directory 内の unique primary hash 数を補助 tie-break として使います。単独最多なら通常決定として自動設定し、low-confidence warning は出しません。
  - それでも複数 viable candidate が残り、`AutoApplyAmbiguousInstallDestination` が ON かつ selected candidate の TITLE / ARTIST evidence が Strong なら、第1候補を自動設定したうえで suggestions と `InstalledDestinationAutoAppliedAmbiguous` warning を残します。
  - 設定 OFF、または metadata evidence が Weak / None の場合は `INSTL DST` を空にし、候補を suggestions に入れ、`InstalledDestinationAmbiguous` warning を付けます。
- 候補が 0 件、または候補限定 final evaluation に必要な `DirectoryResourceLookupCache` がない場合は、通常推定へ fallback せず `InstalledDestinationResolveFailed` warning の対象にします。

### マージ先推定

source package の resource を使わず、既存 library 側の resource だけで成立する統合先を探します。

評価は `candidate only` です。

merge 先探索では、まず package 内の chart 全体を BMS / bmson 共通の hash index で既所持 chart directory へ対応付けます。ここでは package が mixed で、既所持先が複数 directory に分かれていても即座に失敗とはしません。

- directory score は、その directory に hash 一致した package 内 chart 数です。
- hash 一致しない chart は score に参加しません。
- score が単独最多の directory があれば、その directory を正式な merge 先として採用し、操作対象 chart 全体へ同じ `INSTL DST` を設定します。
- score 同点の最多 directory が複数ある場合は hash だけでは決めず、`MergeCandidateOnly` の resource 評価へ fallback します。
- hash 一致候補が 0 件の場合も、同じく `MergeCandidateOnly` の resource 評価へ fallback します。

このため、例えば mixed package の既所持一致が `A, A, B` に分かれる場合は `A` を採用し、`A, B` のような同点では hash 由来の自動決定を行いません。

### 再インストール先推定

既存 library chart を現在位置より良い既存 chart directory へ移せるかを探します。

評価は `candidate only` です。現在配置 directory は候補から除外し、現在配置の health は baseline としてだけ使います。

### Background pending estimate

startup restore / auto-install 由来の pending estimate は package ごとの batch で走ります。zip/package ごとに batch を分け、重い package が他 package を巻き込まないようにします。

batch 末尾では、folder DnD や全ファイル選択 DnD で同一 source directory から複数の単体 pending package として発見された chart を、条件付きで directory package へまとめ直します。この再グループ化は導入先推定結果の表示整理であり、通常推定そのものではありません。

- 再グループ化対象は discovery が `RegroupEligibleSourceDirectories` として記録した source directory です。
- 同じ source directory に属する pending package が 2 件以上あり、source directory 自体の pending package が既に存在せず、deferred estimate package が混じらない場合だけ検討します。
- 各 entry の期待導入先は、既所持 chart なら hash index で解決した実配置 directory、未所持 chart なら既に設定済みの `INSTL DST` です。
- 全 entry の期待導入先が 1 つに揃う場合だけ、pending package を source directory 単位へまとめます。期待導入先が分割される場合や、解決不能な entry がある場合はまとめません。
- 既所持 chart の実配置先は再グループ化可否の判定材料として使いますが、既所持 entry 自身へ `INSTL DST` は投影しません。
- 再グループ化後の `INSTL DST` / `INSTL DST TITLE` / `INSTL DST ARTIST` は未所持 entry にだけ同期します。全 entry が既所持の場合、再グループ化後も `INSTL DST` は空のままです。
- warning / resource health は再グループ化後の package 単位で再初期化します。既所持 entry には `AlreadyInstalled` warning を付けます。

## 現在の並列度

導入先推定の並列化は、現在 2 つの層に分かれています。

### Candidate evaluation 並列

1 回の `estimate_install` の中では、coarse filter / audio gate 後に残った candidate directory を `EvaluateDirectoryCandidate()` で評価します。この candidate 評価は `BmsLibraryInstallEstimationService` 側で PLINQ により並列実行されます。

- `asParallel=true` の場合、`candidateDegree = max(1, Environment.ProcessorCount - 1)` です。
- `asParallel=false` の場合、`candidateDegree = 1` です。
- `candidateDegree` は `estimate_install start` ログに出ます。
- candidate が 1 件まで絞られている場合、`candidateDegree` が複数でも実質的な並列効果はありません。

手動の単一 chart / package 推定、手動の loose chart 群推定内の各 chart 推定は、基本的に `asParallel=true` で candidate 評価を並列化します。単一 work item しかない場合は外側で並列化できないため、candidate 側で CPU を使います。

複数 package を扱う batch 推定では、外側の package 並列と二重に並列化しないため、各 package 内の candidate 評価は `asParallel=false` です。したがって background pending estimate と手動の複数 package 推定の `estimate_install start` では通常 `candidateDegree=1` になります。

### Package / work item 並列

Background pending estimate と手動の複数 package 推定は、batch 内の package を `Task.Run` で複数同時に評価します。

- background pending estimate の外側並列度は `Settings.Default.PendingInstallEstimateMaxParallelPackages` から決まり、`0` の場合は `max(1, Environment.ProcessorCount - 1)` を使います。
- 手動の複数 package 推定の外側並列度は `max(1, Environment.ProcessorCount - 1)` です。
- 実効値は `pending_estimate_batch start` / `progress` / `done` ログの `packageDegree` に出ます。

Background pending estimate では、source baseline prefilter で missing entries の `ChartResourceSnapshot` を作ります。同じ missing entries を評価する package snapshot では、この target resource snapshot を再利用し、同じ chart 群から resource key を再集計しません。

`SearchEstimatedInstallationDirectory(IEnumerable<ChartPackage>)` は、2 件以上の package を受け取った場合、background pending estimate と同じ batch pipeline を使います。loose chart 側の `SearchEstimatedInstallationDirectory(IEnumerable<PackageChartEntry>)` も、対象 chart がすべて pending package に属する場合は package work item にまとめ、2 件以上の package は同じ batch pipeline で評価します。

一方、loose chart が混じる手動 chart 群推定と `fixMode=true` の再インストール先修正は、現在も外側 work item を逐次処理します。この経路は chart 単位の `fixMode` と package 単位の推定を混ぜる必要があり、batch pipeline へ寄せる前に適用順序と警告更新の仕様整理が必要なためです。ただし各 loose chart 内の candidate 評価は `candidateDegree` に応じて並列化されます。

### 排他制御

手動推定と background pending estimate はどちらも `RunPendingEstimateExclusive()` を通るため、推定 batch 同士は同時に走りません。これは pending file/package の mutable state、warning、`INSTL DST`、progress 表示を同時更新しないための排他です。

## 推定先への移動

`InstallPendingPackagesToEstimatedDestinations` は、推定済み pending package を destination directory ごとの group にまとめて処理します。ファイル移動そのものは group は逐次です。これは移動済みファイルと `song.db` 反映の対応を保ち、失敗時の切り分けを単純にするためです。

group ごとの処理では、ファイル移動、`song.db` の譜面 upsert、install row 削除対象の収集、インストール済み package への登録対象収集を行います。`song.db` の譜面 upsert は group ごとに維持します。ここを batch 末尾へ寄せると、移動済みファイルが DB に未反映のままクラッシュする窓が広がるためです。

一方、library/cache/index は batch 末尾でまとめて反映します。具体的には、group ごとに追加 chart と変更 directory を `EstimatedInstallBatchApplyContext` に蓄積し、全 group 完了後に `BMSFiles` / `BmsonSongs` の置換、`directoryResourceLookupCache` の追加 directory scan、playlist library index invalidation/prewarm を最大 1 回に寄せます。追加 bmson は追加 chart のうち `Kind=Bmson` のものとして扱い、batch 後の inline chart_info 対象も `AddedCharts` から再投影します。これにより、複数 group install で `playlist_library_index_prewarm cancelled/debounced` や `reverse_lookup_incremental_update` が group 数分発生しないようにします。

maintenance / chart_info inline 更新も batch 末尾です。maintenance 対象は、追加された BMS / bmson chart に加えて、resource file が移動された destination directory 内の既存 installed chart です。chart も resource も移動しない cleanup-only 成功では maintenance を行いません。

この後処理は起動時 file diff とは別経路です。pending package discovery では source 側の path-based parse で作った `BMSFile` / `BmsonSong` model を使い、install 後は destination の実ファイルを対象に `chart_info_inline_install` と `setMaintenanceInfo(forceUpdate: true)` を実行します。起動時 file diff のように 1 つの `ChartFileSnapshot` を lightweight parse、inline maintenance、inline chart_info で共有する処理ではないため、discovery から install までに source file が変わると、metadata model と install 後の chart_info / maintenance の鮮度が分かれる可能性があります。

resource health index は delta 更新を優先します。既存 snapshot があり、affected chart が特定できる推定先 install では、`resource_health_index_delta reason=install_package_estimated` として対象 chart の projection だけを更新します。snapshot が無い、または既に invalidated の場合は、この install 後処理だけで full rebuild せず dirty のまま維持します。対象が特定できない明示的な全体再スキャン系操作だけが `resource_health_index_build` の full rebuild に進みます。

ログ確認時は次を見ると、処理の粒度を確認できます。

- `install_pending_packages_to_estimated_destinations group`: destination group ごとの逐次移動と group 単位の DB 反映。
- `reverse_lookup_incremental_update reason=install_package`: batch 末尾の reverse lookup 差分更新。複数 group でも原則 1 回。
- `maintenance_update`: batch 末尾の affected chart maintenance。`resourceHealthIndexMode=delta` なら resource health index は差分更新です。
- `resource_health_index_delta`: full rebuild ではなく affected chart の projection だけを更新したことを示します。
- `chart_info_inline_install`: batch 末尾の chart_info inline parse / persist。

## 候補母集団

候補は chart directory です。resource-only subdirectory は候補になりません。

候補一覧は `DirectoryResourceLookupCache.Keys` から得ます。これは file enumeration で chart directory として確定した directory 集合です。

source directory は通常推定でも merge 推定でも候補に入れません。

## Coarse Filter

候補を全件評価しないために、先に lightweight filter を通します。

### 1. Path-aware broad filter

カテゴリ別 reverse lookup を使います。

- audio refs -> audio relative reverse map
- image / optional image refs -> image relative reverse map
- movie refs -> movie relative reverse map

`DirectoryResourceLookupCache` がない場合は、旧 union view へ fallback せず、`resource_index_unavailable` として推定不可にします。

候補が 0 件になった場合、全 library への fallback はしません。`no_viable_destination_below_threshold` として扱います。

### 2. Audio gate

audio reference がある target では、candidate 自身に最低限の audio 一致を要求します。

- audio refs が 2 件以上: candidate 自身で 2 件以上一致
- audio refs が 1 件: candidate 自身で 1 件以上一致
- audio refs が 0 件: audio gate なし

さらに viable audio health が `innerWavHealthThreshold` を超える見込みがない candidate は落とします。

通常推定では `candidate + bundled` の effective health を使います。merge / reinstall correction では `candidate only` です。

## Final Evaluation

各 candidate は `EvaluateDirectoryCandidate()` で評価されます。

評価対象:

- 通常推定: `candidate + bundled`
- merge: `candidate only`
- reinstall correction: `candidate only`
- source baseline: source directory のみ

resource match はカテゴリ別かつ chart-relative です。

- audio ref は audio relative key とだけ照合
- image ref は image relative key とだけ照合
- movie ref は movie relative key とだけ照合
- optional image ref は image relative key と照合

chart-relative key が一致した場合に match とします。basename-only reference も現在は extensionless relative key として扱われ、`foo` と `sound/foo` は別 key です。

## 評価指標

カテゴリごとに以下を算出します。

- `Matched`
- `Health`
- `CandidateCount`
- `Precision`
- `Jaccard`

`Health` は `Matched / Defined` です。導入先として viable かどうかは primary health が `innerWavHealthThreshold` を超えるかで決まります。

primary health は次の順で選ばれます。

- audio ref がある場合: audio health
- audio ref がない場合: image / movie / optional image の最大値

## Tie-break

候補は `CompareCandidateEvaluations()` で並びます。順序は概ね次です。

1. Audio health
2. Audio matched
3. Audio jaccard
4. Audio precision
5. Image health
6. Image matched
7. Image jaccard
8. Image precision
9. Movie health
10. Movie matched
11. Movie jaccard
12. Movie precision
13. Optional image health
14. Optional image matched
15. Optional image jaccard
16. Optional image precision
17. Directory path

`Precision` / `Jaccard` は表示用の丸め値ではなく、raw ratio で比較します。これにより、整数表示では同じ `100%` に見える候補でも内部順位が潰れにくくなります。

### extensionless resource union と tie-break

現在の主 tie-break では、extensionless resource union そのものは直接の順位軸ではありません。

通常経路の `CandidateCount` はカテゴリ別 relative key set から出ます。つまり、audio / image / movie のそれぞれの candidate count が precision / jaccard に効きます。

all-base union 派生 API は `DirectoryResourceLookupCache.Entry` から削除済みです。導入先の candidate 列挙、primary matching、primary tie-break の正本は audio / image / movie のカテゴリ別 relative key です。

mixed package の既存配置先再利用では、hash tie が複数候補になっても extensionless union の health 判定補助は使いません。候補限定 final evaluation の category resource metrics で評価し、metadata まで完全同等な場合だけ候補 directory 内の unique primary hash 数を補助 tie-break として使います。unique primary hash 数も同等なら suggestions と warning に落とします。

resource health / maintenance もカテゴリ別 `ResourceHealthLookupContext` を正本にし、`FolderAllFileList` には fallback しません。health 判定も chart-relative key のみを使うため、`foo.wav` は root の `foo`、`sound/foo.wav` は `sound/foo` として別物です。folder move/delete/merge/install/reload 後の memory index 差分更新も `DirectoryResourceLookupCache` 正本へ移したため、extensionless union の live cache は残していません。順序安定だけが必要なら path 順などの明示的で安全な tie-break を使います。

## Metadata Tie-break

resource 指標が同一の viable frontier だけに metadata tie-break を適用します。

通常推定では、対象は先頭 candidate と `HasSameRankingMetrics()` な候補群のうち最大 3 件です。mixed package の既存配置先再利用で directory unique primary hash count resolver が渡されている場合だけ、同等候補群すべてを対象にします。UI に表示する suggestions はどちらの場合も後段で上位最大 3 件に制限します。

比較順:

1. Title + artist pair exact
2. Title exact
3. Artist exact
4. Title fuzzy strength
5. Pair support count
6. Title support count
7. Artist support count
8. mixed package 候補限定評価で resolver が渡された場合だけ、directory unique primary hash count
9. Resource metrics / path order

metadata tie-break で明確に 1 位が分かれた場合、resource metrics 上は tie でも `metadata_tiebreak_distinct` として high confidence にできます。

directory unique primary hash count は、metadata 比較が完全同等の場合だけ使う補助 tie-break です。metadata exact / fuzzy / support count のいずれかで順位差がある場合、譜面数でその順位を上書きしません。ここで単独最多の候補が選ばれた場合は `directory_hash_count_tiebreak_distinct` として high confidence にします。

## Ancestor Shadow Suppression

aggregate ownership では、child directory の resource が ancestor chart directory からも見えることがあります。

candidate set に ancestor / descendant 関係がある場合だけ、ancestor-shadow suppression を行います。

ancestor を抑制する条件:

- ancestor と descendant が同等以上の ranking metrics
- ancestor の self-owned matched total が 0
- descendant の self-owned matched total が 1 以上

self-owned match はこの比較が必要な候補だけ lazy に計算します。

## Confidence

推定結果は confidence と auto-apply 可否に変換されます。

- viable candidate がない
  - `Confidence=High`
  - `DestinationDirectory=null`
  - `ShouldAutoApplyDestination=false`
- viable candidate が一意で metadata が妥当
  - `Confidence=High`
  - `ShouldAutoApplyDestination=true`
- viable candidate が複数で resource metrics が同一
  - metadata tie-break が distinct なら high
  - mixed package 候補限定評価で metadata まで完全同等、かつ directory unique primary hash count が distinct なら high
  - そうでなければ low + suggestions
- metadata mismatch
  - low + suggestion
- reinstall correction で現在配置より改善しない
  - low + suggestion

`INSTL DST` は `ShouldAutoApplyDestination=true` かつ destination がある場合だけ自動設定します。

## Source Baseline

source は候補 list には入りません。source は background pending estimate を抑制するための baseline health 判定に使います。

source baseline が十分に高い directory package は pending に残しますが、background auto-estimate は省略します。手動推定ではこの抑制は適用しません。

## Logging

主なログは `estimate_install start` です。重要なフィールドは次です。

- `evalMode`
- `candidateMode`
- `coarseFilterMode`
- `candidateDirsBefore`
- `candidateDirsAfterBroadFilter`
- `candidateDirsAfterAudioGate`
- `candidateDirsAfter`
- `candidateViewBuildMs`
- `candidateMatchMs`
- `shadowSuppressed`
- `confidence`
- `confidenceReason`
- `lazyHashBuildMsDelta`

metadata frontier が発生した場合は `estimate_install metadata_frontier` / `estimate_install metadata_tiebreak` も出ます。

## 関連資料

- `devdocs/spec/data-and-indexes.md`
- `devdocs/spec/workflows.md`
- `devdocs/plan/bmson/install-estimation-target-design.md`
- `devdocs/plan/bmson/install-estimation-relative-path-foundation.md`
- `devdocs/plan/bmson/install-estimation-performance-foundation.md`
- `devdocs/plan/bmson/library-scan-fast-path-resource-index-plan.md`
