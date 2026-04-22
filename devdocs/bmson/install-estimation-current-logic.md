# 現状の導入先推定ロジック整理

## 目的

この資料は、`2026-04-22` 時点の **実装上の現状** を整理するためのものです。  
現在の推定は、通常の `インストール先を推定` と `マージ先を推定` を、**評価単位の違う 2 つの機能**として扱います。

## 先に結論

現在の推定は次の 2 系統に分かれます。

1. `インストール先を推定`
   - final evaluation は **`candidate + package bundled resources`**
   - source は候補に入れない
   - 「この package を外部のどこへ入れれば成立するか」を探す
2. `マージ先を推定`
   - final evaluation は **`candidate only`**
   - source は候補に入れない
   - 「source 以外に、既存リソースだけで成立する統合先があるか」を探す

一方で source は完全に不要になったわけではなく、**background auto-estimate 抑制の source baseline health 判定**にだけ使います。

さらに `2026-04-22` 時点では、`TITLE` / `ARTIST` の metadata を

- resource 指標で僅差の上位候補群に対する **frontier tie-break**
- 最終 1 位候補に対する **selected-candidate validation**

の 2 段で使います。

## 関連クラス

- `BMSLibrary`
  - 推定の入口
  - pending / startup restore / auto-install / 手動再推定から推定を呼ぶ
- `BmsLibraryInstallEstimationService`
  - 候補評価ロジック本体
- `PackageInstallEstimationSnapshot`
  - package-aware 推定用 snapshot
- `PackageInstallEstimationSnapshotBuilder`
  - package source path から bundled resources を構築する
- `ChartResourceSnapshot`
  - target 側の resource 定義 union
- `DirectoryResourceLookupCache`
  - chart directory ごとの resource hash cache
- `BMSDirectoryFileNameHash`
  - chart directory ごとの all-resource basename hash index

## 推定入口

### 1. package-aware 経路

主経路は `BMSLibrary.SearchEstimatedInstallationDirectoryCore(BMSPackage)` です。

- package を
  - `alreadyInstalledFiles`
  - `missingFiles`
  に分ける
- `missingFiles.Count == 0` なら通常推定は行わない
- mixed package では、まず既存配置先再利用を試す
  - 成功すれば推定に入らず適用
  - 失敗時だけ `Fix` モードで package-aware 推定へ入る
- 全未所持 package は `Normal` モードで package-aware 推定へ入る

この経路では、`missingFiles` と package の source path から **package snapshot** を作って評価します。

### 2. background pending estimate の抑制

startup restore と auto-install 後の background pending estimate では、package-aware 推定へ入る前に **source baseline viability** を見ます。

- directory package で source baseline の primary health が `innerWavHealthThreshold=70` 以上
  - pending には残す
  - ただし background auto-estimate は走らせない
  - package には transient に `DeferredEstimateReason=HealthySourceBaseline` を付ける
  - `INSTL DST` / suggestion / low-confidence warning / 推定 metadata は空に戻す
- file package
  - deferred 抑制対象外
  - 従来どおり background estimate の候補になり得る
- mixed package
  - まず installed-directory reuse を試す
  - reuse 不成立時だけ source baseline 判定へ進む

つまり現在は、**pending に残ること** と **background auto-estimate 対象になること** を分けています。

### 3. loose-file 経路

`IEnumerable<BMSFile>` / `BMSFile` から直接呼ぶ経路も残っています。

- `PackageInstallEstimationSnapshotBuilder.BuildForLooseFiles(...)` を使う
- `BundledResources` は空
- `SourceCandidateResources` は source baseline health 判定用にだけ持つ

pending package・startup restore・auto-install では通常この経路は使いません。

## package snapshot の中身

`PackageInstallEstimationSnapshot` は少なくとも次を持ちます。

- `RepresentativeFile`
- `DefinedResources`
- `TargetMetadataProfile`
- `BundledResources`
- `SourceCandidateResources`
- `SourceDirectory`
- `ChartCount`

### 1. `DefinedResources`

`DefinedResources` は **package 内の対象 chart 群の union** です。  
`ChartResourceSnapshot.CreateAggregate(...)` を使います。

### 2. `BundledResources`

`BundledResources` は **package が導入時に持ち込む non-chart resource 実体** です。  
shape は `DirectoryResourceLookupCache.Entry` と揃えています。

### 3. `SourceCandidateResources`

source baseline health を判定するための transient entry です。

- directory package: source root 全体
- file package: 親 directory
- loose files: 代表 file の親 directory

### 4. `TargetMetadataProfile`

target 側 metadata は、代表 1 件ではなく **対象 chart 群の最頻値 profile** を使います。

- `DominantNormalizedTitle`
- `DominantNormalizedArtist`
- `DominantNormalizedTitleArtistPair`
- support count

package-aware 経路でも loose-file 経路でも同じ shape を持ちます。

## 候補母集団

現在の候補母集団は scan redesign 後の **chart directory** です。

- `folderAllFileList.Keys`
- source directory は通常推定でも merge 推定でも候補に含めない
- 比較対象は常に external candidate のみ

resource-only subdir は候補に入りません。

## coarse filter

coarse filter では、`snapshot.DefinedResources.EnumerateAllBaseNameHashes()` を使って候補 directory を絞ります。

- `DirectoryResourceLookupCache` がある場合:
  - `EnsureDirectoriesByHashes(targetHashes)`
  - 各 hash に対応する directory を union
- cache がない場合:
  - `BMSDirectoryFileNameHash` の hash array を直接なめる

`2026-04-22` 時点の Perf-2a 修正後は、この broad prefilter の後に **unified audio gate** をかけます。

- `audioRefs >= 2`
  - candidate 自身の audio basename hash 2 件以上一致必須
- `audioRefs == 1`
  - candidate 自身の audio basename hash 1 件以上一致必須
- `audioRefs == 0`
  - audio gate を適用しない

さらに `audioRefs > 0` かつ `DirectoryResourceLookupCache` がある経路では、

- 通常推定 / `Fix`
  - `candidate + bundled` の effective audio health が `innerWavHealthThreshold=70` 超でない候補を落とす
- merge 推定
  - `candidate only` の effective audio health が `innerWavHealthThreshold=70` 超でない候補を落とす

つまり coarse filter は次の 2 段です。

1. broad prefilter
2. unified audio gate

つまり、

- `audioRefs > 0 && audioMatched == 0`
  - image/movie/optional が一致していても候補に残さない
- bundled だけで threshold を満たしても
  - candidate 自身に最低限の audio 一致がなければ候補に残さない
- `candidateDirsAfter=0`
  - 全 library へ fallback せず、そのまま no destination に落とす

`innerWavHealthThreshold=70` は、現在は

- coarse filter の unified audio gate
- 最終 confidence / viable 判定

の **二段**で使います。

Perf-2a 再修正では、この unified audio gate の仕様は変えず、内部実装だけを軽くしています。

- `candidate self minimum match`
  - candidate 側 audio basename を直接なめる early-exit
- `effective viability`
  - `health > 70` に到達した時点で打ち切る threshold-only check

つまり現在は、**1 helper / 2 条件**のまま、旧 2段ゲート時の cheap/heavy 構造を内部へ戻した状態です。

### 現状の `innerWavHealthThreshold` の位置づけ

`innerWavHealthThreshold=70` は、`2026-04-22` 時点では **coarse filter と最終判定の両方**で使う。

- coarse filter
  - broad prefilter
  - unified audio gate
- final evaluation 後
  - viable destination 判定
  - `High + destination` / `High + no destination` / `Low + suggestions`
- background pending estimate 抑制
  - source baseline health 判定

に分かれている。

つまり現状は、

- `audio gate`
  - 軽量な候補縮小と viability gate
- `innerWavHealthThreshold`
  - 前段と最終段の両方で使う

という役割分担である。  
ただし threshold の意味自体は変えておらず、「宛先として有効なのは audio health が threshold を超える candidate」という前提を、前段 candidate 除外にも流用している。

## 最終評価

各候補は `EvaluateCandidate(...)` で評価します。

### 通常推定 / `Fix`

比較対象:

- `CandidateResources ∪ BundledResources`
- `snapshot.DefinedResources`

つまり **`candidate + package bundled resources`** を見ます。

### merge 推定

比較対象:

- `CandidateResources`
- `snapshot.DefinedResources`

つまり **`candidate only`** を見ます。  
source の bundled resources は merge の final evaluation には足しません。

## 算出する指標

カテゴリごとに次を算出します。

- `Matched`
- `ExactMatched`
- `Health`
- `Precision`
- `Jaccard`

`Precision` / `Jaccard` はログ/UI には整数 `%` を出しますが、**内部順位付けと tie 判定は raw ratio** を使います。

## 並び順

候補は概ね次の順で降順比較します。

1. `AudioHealth`
2. `AudioMatched`
3. `AudioExactMatched`
4. `AudioJaccard`
5. `AudioPrecision`
6. 同様に `Visual`
7. 同様に `Movie`
8. 同様に `OptionalImage`
9. raw precision / jaccard
10. `DirectoryPath`

raw ratio 比較を入れているため、`1281/1282` と `1281/1285` のような差が 100/100 に丸め潰されて path 順になるのを避けています。

## `TITLE` / `ARTIST` metadata

metadata は主スコアには入れず、後段で使います。

- 対象は先頭候補と `HasSameRankingMetrics(...)` な viable candidate 群
- 対象数は最大 3 件
- candidate 側 metadata は destination directory 配下の全譜面から作る **最頻値 profile**
- target 側 metadata も package / loose-file 単位の最頻値 profile

frontier tie-break は、**resource 指標で僅差の上位 frontier** にだけ後段適用します。

- `TITLE`
  - trim / 全半角 / 空白 / 大小を正規化
  - `(` `[` `~` ` -` 以降を無視
  - ただし先頭 delimiter は切らない
  - 正規化後 exact に加え、bigram Dice coefficient による軽量 fuzzy を使う
- `ARTIST`
  - trim / 全半角 / 空白 / 大小を正規化
  - 文字列中の `obj` `note` `notes` + セパレータ marker 以降を切る
  - `/` 以降を無視

比較順は次です。

1. `TitleArtistPair` 一致
2. `Title` 一致
3. `Artist` 一致
4. pair support
5. title support
6. artist support
7. それでも同点なら既存の raw ratio / path 順

metadata tie-break の結果が明確なら、resource 指標上は tie でも `Low` を `High` へ上げます。
このとき `ConfidenceReason = metadata_tiebreak_distinct` になります。

### selected-candidate validation

frontier tie-break 後も、最終 1 位候補に対して metadata 妥当性を再判定します。

- viable candidate が 1 件だけ
- `distinct_primary_metrics`
- `single_candidate`

でも必ず実行します。

metadata evidence は次です。

- `Strong`
  - pair exact
  - title exact
  - title fuzzy strong + artist exact
- `Weak`
  - title fuzzy strong
  - title weak
  - artist exact のみ
- `None`
  - 上記以外

selected candidate の metadata evidence が `Weak` / `None` の場合は、

- `Confidence = Low`
- `INSTL DST` は空
- suggestion に top candidate を残す

として、自動確定しません。

## source の扱い

source は ranking 本体では扱いません。  
現在の source は次の用途に限定しています。

- background auto-estimate 抑制の source baseline health 判定
- package snapshot 内の `SourceCandidateResources`

通常推定でも merge 推定でも、候補 list・選択候補・suggestion には **source を含めません**。

## `innerWavHealthThreshold=70` の使い方

`innerWavHealthThreshold=70` は、今は次の必要条件です。

- viable destination
- `High` 判定
- `ShouldAutoApplyDestination`
- background auto-estimate 抑制の source baseline 判定

さらに coarse filter の unified audio gate では、

- 通常推定
  - `candidate self minimum match`
  - かつ `candidate + bundled` が `health > 70`
- merge 推定
  - `candidate self minimum match`
  - かつ `candidate only` が `health > 70`

の両方を必要条件として使います。  
たとえば `audioRefs = 100` なら、`health > 70` を満たす最小 matched は `71` です。通常推定で bundled が `50` 有効なら、effective matched としては残り `21` 件以上が必要ですが、それとは別に candidate 自身の minimum match 条件も必要です。  
つまり threshold は、**前段 gate と最終安全弁の二段**で使っています。

なお Perf-2a 再修正で変更したのは、この前段 gate の**実装コストだけ**です。

- 仕様:
  - 不変
- 実装:
  - self minimum match は early-exit
  - viability は full matched count ではなく threshold 到達 boolean

に最適化しています。

## `INSTL DST` 反映条件

`BMSLibrary.ApplyInstallEstimationResultToFiles(...)` では:

- `ShouldAutoApplyDestination == true`
- `DestinationDirectory` が空でない

ときだけ `instl_dst` を自動適用します。

それ以外では:

- `instl_dst = null`
- `HasViableDestination == true` の場合だけ代表 metadata を入れる
- `Confidence = Low` の場合だけ non-source suggestion を保持する
- ambiguity の場合は 2 件以上 suggestion があるとき warning を保持する
- metadata mismatch の場合は 1 件 suggestion でも warning を保持する

また、**UI に見える pending 状態で `instl_dst` を反映する経路**では、推定結果適用・手動 `INSTL DST` 入力・resolved destination 再利用・pending regroup のいずれでも、`INSTL DST TITLE` / `INSTL DST ARTIST` を同時に同期します。

## 手動 `インストール先を推定` と `マージ先を推定` の違い

### 1. 手動 `インストール先を推定`

目的は、**未所持譜面の external install destination を決めること**です。

- background auto-estimate 抑制とは無関係で、手動なら高ヘルスでも実行する
- source は候補に入れない
- external candidate を `candidate + bundled` で評価する

mixed package では:

- まず `TryResolveInstalledDestinationFromPackage(...)` で、既所持譜面の実配置先を未所持譜面へ再利用できるか試す
- 成功したら、その配置先を **未所持譜面だけ** に反映し、代表 metadata も同期して終了
- 失敗したら `Fix` モードで **未所持譜面だけ** を package-aware 推定する

つまり通常推定は、mixed package では **「既所持側の配置先に未所持を寄せる補完」** が第一です。

### 2. 手動 `マージ先を推定`

目的は、**source の同梱リソースを使わず、source 以外に既存リソースだけで成立する統合先があるかを見ること**です。

- package 単位では `MergeCandidateOnly` モードを使う
- source は候補に含めない
- external candidate を `candidate only` で評価する

その上で:

- まず `TryResolveInstalledDestinationFromPackage(...)` を試す
- 解決できれば、その配置先を package 全体へ反映し、代表 metadata も同期する
- 解決できなければ `MergeCandidateOnly` で package 全体を external merge search する

merge の結果は次で固定します。

- viable external candidate が 1 件で明確
  - `High + destination`
- viable external candidate が複数で僅差
  - `Low + non-source suggestions`
- viable external candidate が 0 件
  - `High + no destination`

つまりマージ推定は、**「source 以外に、既存リソースだけで成立する外部統合先があるか」** を見る機能です。

## 既所持譜面を含む package の扱い

### 通常推定

- 既所持譜面は warning 対象になる
- 推定対象は **未所持譜面だけ**
- まず既所持譜面の実配置先を再利用できるか試す
- 再利用できなければ、未所持譜面だけ `Fix` モードで推定する

### マージ推定

- package 単位では **既所持・未所持をまとめて** 扱う
- 既存配置先再利用が成功すれば、その配置先と代表 metadata を package 全体へ入れる
- 失敗したら package 全体を `MergeCandidateOnly` で評価する

### file 選択時の注意

- 通常推定
  - pending package に属する file は package 単位へ束ねて処理する
- マージ推定
  - 現在は file ごとに `MergeCandidateOnly` を回す

そのため、同じ pending package でも **package 選択時と file 選択時で merge 結果がずれる余地** は残っています。

## pending batch と demand build

startup restore / auto-install 由来の pending 推定は、現在は queue で非同期に進みます。

- package 群は先に DataGrid に出る
- 推定は batch worker が package 単位で進める
- batch 開始前に package aggregate hash をまとめて `EnsureDirectoriesByHashes(...)` する
- ただし source baseline が高ヘルスなら、その package は deferred として skip する

## 関連資料

- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
- [install-estimation-target-design.md](install-estimation-target-design.md)
- [install-estimation-performance-foundation.md](install-estimation-performance-foundation.md)
- [../spec/data-and-indexes.md](../spec/data-and-indexes.md)
