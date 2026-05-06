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

`foo` と `sound/foo` は別 key です。`sound/foo.wav` が `foo` に fallback することはありません。

### Source package surface

package 内に同梱されている non-chart resource は `BundledResources` として持ちます。

通常のインストール先推定では `candidate + bundled` を評価します。つまり、package が持ち込む resource も導入後に使えるものとして数えます。

merge / reinstall correction では source/bundled resource を足さず、`candidate only` で評価します。

### Library resource index

通常起動の file enumeration 結果から `LibraryResourceIndex` を作ります。導入先推定で使う正本は `DirectoryResourceLookupCache` のカテゴリ別 index です。

- audio basename / relative key
- image basename / relative key
- movie basename / relative key
- self-owned audio / image / movie
- category reverse lookup

`BMSDirectoryFileNameHash` / `FolderAllFileList` は正本ではありません。これはカテゴリ別 index から派生した folder-level extensionless union view で、候補 directory の一覧や cacheless 補助に残っています。

## 推定入口

### 通常 package 推定

未所持 chart を含む package に対して、外部 chart directory を探します。

- 既所持 chart だけの package は通常推定しません。
- mixed package では、まず既所持 chart の実配置先を hash index から候補 directory 集合として解決します。
- 候補が 1 件なら、未所持 chart をその配置先へ寄せます。
- 候補が 2 件以上なら、その候補集合だけを通常推定と同じ final evaluation へ渡します。
  - 一意に勝つ viable candidate があれば `INSTL DST` を自動設定します。
  - 複数 viable candidate が残る場合は `INSTL DST` を空にし、候補を suggestions に入れ、`InstalledDestinationAmbiguous` warning を付けます。
- 候補が 0 件、または候補限定 final evaluation に必要な `DirectoryResourceLookupCache` がない場合は、通常推定へ fallback せず `InstalledDestinationResolveFailed` warning の対象にします。

### マージ先推定

source package の resource を使わず、既存 library 側の resource だけで成立する統合先を探します。

評価は `candidate only` です。

### 再インストール先推定

既存 library chart を現在位置より良い既存 chart directory へ移せるかを探します。

評価は `candidate only` です。現在配置 directory は候補から除外し、現在配置の health は baseline としてだけ使います。

### Background pending estimate

startup restore / auto-install 由来の pending estimate は package ごとの batch で走ります。zip/package ごとに batch を分け、重い package が他 package を巻き込まないようにします。

## 候補母集団

候補は chart directory です。resource-only subdirectory は候補になりません。

現在の候補一覧は `FolderAllFileList.Keys` から得ます。これは extensionless resource union view を保持する構造ですが、ここで使う主目的は「候補 chart directory の集合」です。

source directory は通常推定でも merge 推定でも候補に入れません。

## Coarse Filter

候補を全件評価しないために、先に lightweight filter を通します。

### 1. Path-aware broad filter

`DirectoryResourceLookupCache` がある通常経路では、カテゴリ別 reverse lookup を使います。

- audio refs -> audio relative reverse map
- image / optional image refs -> image relative reverse map
- movie refs -> movie relative reverse map

cacheless 経路では、`BMSDirectoryFileNameHash` と `DirectoryRelativePathHashIndex` の補助を使います。この経路では extensionless union が混ざり得ますが、通常の native canonical index 経路ではカテゴリ別 reverse lookup が正本です。

mixed package の複数候補評価では cacheless 経路を使いません。`DirectoryResourceLookupCache` がない状態で複数候補になった場合は、導入先を推定不可として扱います。`SkipInitFileCheck` のように起動時 resource index を作らない設定では、この制約により導入先推定ができない場合があります。

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

resource match はカテゴリ別です。

- audio ref は audio key とだけ照合
- image ref は image key とだけ照合
- movie ref は movie key とだけ照合
- optional image ref は image key と照合

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

ただし union は次の補助に残っています。

- `FolderAllFileList.Keys` による candidate chart directory 集合
- `DirectoryResourceLookupCache.Entry.AllBaseNameHashes` の lazy union
- cacheless / fallback view で category set がない場合の補助
- `AudioFileCount` など診断・表示寄りの派生値

このため、現時点でも union が完全に無関係ではありません。ただし、カテゴリ別 index が使える通常経路では、導入先の primary matching と primary tie-break の正本は audio / image / movie のカテゴリ別 key です。

mixed package の既存配置先再利用では、hash tie が複数候補になっても extensionless union の health 判定補助は使いません。候補限定 final evaluation の category resource metrics で評価し、曖昧なら suggestions と warning に落とします。

今後の整理では、extensionless union を候補 directory list 以外の推定材料から外し、順序安定が必要なだけなら path 順などの明示的で安全な tie-break へ置き換えます。

## Metadata Tie-break

resource 指標が同一の viable frontier だけに metadata tie-break を適用します。

対象は先頭 candidate と `HasSameRankingMetrics()` な候補群のうち最大 3 件です。

比較順:

1. Title + artist pair exact
2. Title exact
3. Artist exact
4. Title fuzzy strength
5. Pair support count
6. Title support count
7. Artist support count
8. Resource metrics / path order

metadata tie-break で明確に 1 位が分かれた場合、resource metrics 上は tie でも `metadata_tiebreak_distinct` として high confidence にできます。

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
- `candidateViewFallbackCount`
- `shadowSuppressed`
- `confidence`
- `confidenceReason`
- `lazyHashBuildMsDelta`

metadata frontier が発生した場合は `estimate_install metadata_frontier` / `estimate_install metadata_tiebreak` も出ます。

## 関連資料

- `devdocs/spec/data-and-indexes.md`
- `devdocs/spec/workflows.md`
- `devdocs/bmson/install-estimation-target-design.md`
- `devdocs/bmson/install-estimation-relative-path-foundation.md`
- `devdocs/bmson/install-estimation-performance-foundation.md`
- `devdocs/bmson/library-scan-fast-path-resource-index-plan.md`
