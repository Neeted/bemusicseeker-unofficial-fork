# 現状の導入先推定ロジック整理

## 目的

この資料は、`2026-04-20` 時点の **実装上の現状** を整理するためのものです。  
`P2` 再設計後の導入先推定が、現在どの単位で何を比較しているかを、次の実機調整に入れる粒度でまとめます。

## 先に結論

現在の最終評価単位は、従来の `candidate only` ではなく **`candidate + package bundled resources`** です。  
推定サービスは、まず package 単位の snapshot を作り、その snapshot を使って候補 directory を比較します。

大きい流れは次です。

1. 対象を `PackageInstallEstimationSnapshot` に変換する
2. `DefinedResources` の basename hash で chart directory 候補を粗く絞る
3. 各候補に対して `EffectiveResources = CandidateResources ∪ BundledResources` を作る
4. `EffectiveResources` と `DefinedResources` を比較して `Health / Matched / Exact / Precision / Jaccard` を出す
5. source も通常候補と同じ土俵で比較する
6. background pending estimate では、source baseline が `innerWavHealthThreshold=70` 以上なら自動推定を抑制する
7. ただし結果は `High + destination` / `Low + suggestions` / `High + no destination` に整理して返す
8. `innerWavHealthThreshold=70` は候補除外には使わず、viable destination と `High` / auto-apply の下限に使う

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

## 1. package-aware 経路

主経路は `BMSLibrary.SearchEstimatedInstallationDirectoryCore(BMSPackage)` です。

- package を
  - `alreadyInstalledFiles`
  - `missingFiles`
  に分ける
- `missingFiles.Count == 0` なら推定しない
- mixed package では、まず既存配置先再利用を試す
  - 成功すれば推定に入らず適用
  - 失敗時だけ `Fix` モードで package-aware 推定へ入る
- 全未所持 package は `Normal` モードで package-aware 推定へ入る

この経路では、`missingFiles` と package の source path から **package snapshot** を作って評価します。

### background pending estimate の抑制

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
高ヘルス source の package は「未推定」ではなく、**自動推定不要・必要なら手動 merge 対象**として保留に残ります。

## 2. loose-file 経路

`IEnumerable<BMSFile>` / `BMSFile` から直接呼ぶ経路も残っています。

- こちらは `PackageInstallEstimationSnapshotBuilder.BuildForLooseFiles(...)` を使う
- `BundledResources` は空
- `SourceCandidateResources` は代表 file の親 directory から作る

つまり loose-file 推定は、まだ package install surface を持たない軽量経路です。  
pending package・startup restore・auto-install では通常こちらは使いません。

## package snapshot の中身

`PackageInstallEstimationSnapshot` は少なくとも次を持ちます。

- `RepresentativeFile`
- `DefinedResources`
- `BundledResources`
- `SourceCandidateResources`
- `SourceDirectory`
- `ChartCount`

### 1. `DefinedResources`

`DefinedResources` は **package 内の対象 chart 群の union** です。  
従来の「代表譜面 1 件だけ」ではなく、`ChartResourceSnapshot.CreateAggregate(...)` を使います。

含むもの:

- `AudioBaseNameHashes`
- `VisualBaseNameHashes`
- `MovieBaseNameHashes`
- `OptionalImageBaseNameHashes`
- 各 relative path hash

## 2. `BundledResources`

`BundledResources` は **package が導入時に持ち込む non-chart resource 実体** です。  
shape は `DirectoryResourceLookupCache.Entry` と揃えています。

含むもの:

- `AllBaseNameHashArray`
- `Audio/Image/MovieBaseNameHashArray`
- `Audio/Image/MovieRelativePathHashArray`

### directory package

- `package.path` 配下を再帰列挙する
- chart file は除外する
- audio / image / movie だけを分類して hash 化する
- relative path は **package root 基準**で正規化する

### file package

- 実 install では chart file 自身しか component として移動しない
- そのため bundled non-chart resources は空
- sibling resources は bundled に含めない

## 3. `SourceCandidateResources`

source を通常候補と同じ土俵で比較するための entry です。

- directory package: source root 全体の resource entry
- file package: parent directory を source candidate surface として読む
- loose files: 代表 file の親 directory を使う

`source` が library cache に載っていない場合も、この transient entry を使って比較できます。

## 候補母集団

現在の候補母集団は scan redesign 後の **chart directory** です。

- `bmsFolderAllFileList.Keys`
- merge 以外では `SourceDirectory` も追加候補として含める
- merge (`MergeSourceBaseline`) でも source を含めて比較し、source baseline より良い non-source があるかを見る

resource-only subdir は候補に入りません。

## coarse filter

coarse filter では、`snapshot.DefinedResources.EnumerateAllBaseNameHashes()` を使って候補 directory を絞ります。

- `DirectoryResourceLookupCache` がある場合:
  - `EnsureDirectoriesByHashes(targetHashes)`
  - 各 hash に対応する directory を union
- cache がない場合:
  - `BMSDirectoryFileNameHash` の hash array を直接なめる

ここで重要なのは、**`innerWavHealthThreshold=70` による候補除外はもうしていない**ことです。  
threshold は coarse filter ではなく、最終 confidence 判定側で使います。

候補 0 件なら fallback で全 chart directory 比較に戻ります。

## 最終評価

各候補は `EvaluateCandidate(...)` で評価します。

候補ごとに作るもの:

- `CandidateResources`
  - library cache の `DirectoryResourceLookupCache.Entry`
  - source の場合は `SourceCandidateResources`
- `BundledResources`
  - package snapshot から渡された bundled resources
- `EffectiveResources`
  - `CandidateResources ∪ BundledResources`

比較対象は常に:

- `EffectiveResources`
- `snapshot.DefinedResources`

です。

## 算出する指標

カテゴリごとに次を算出します。

- `Matched`
  - basename hash 一致数
- `ExactMatched`
  - relative path hash 一致数
- `Health`
  - `matched / defined`
- `Precision`
  - `matched / effectiveCandidateCount`
- `Jaccard`
  - `matched / (defined + effectiveCandidateCount - matched)`

ここでの `effectiveCandidateCount` は **union 後の候補側件数**です。

## 並び順

候補は現在、概ね次の順で降順比較します。

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

`Precision` / `Jaccard` はログ/UI には整数 `%` を出しますが、**内部順位付けと tie 判定は raw ratio** を使います。
そのため、`1281/1282` と `1281/1285` のような差が 100/100 に丸めつぶされて path 順になるのを避けています。

つまり P2 現在地は「candidate+bundled union 評価に移行済みで、その指標で並べている」状態です。

## source の扱い

source は別枠 reinject ではなく、**通常候補と同じ list** で比較します。

- source が 1 位で non-source 候補を十分に上回る場合:
  - `Confidence = High`
  - `DestinationDirectory = null`
  - `ShouldAutoApplyDestination = false`
  - `confidenceReason = source_directory_preferred_no_destination`
- source が 1 位だが viable な non-source 候補と僅差の場合:
  - `Confidence = Low`
  - `DestinationDirectory = null`
  - `ShouldAutoApplyDestination = false`
  - `confidenceReason = tie_on_primary_metrics`

このため、source が最上位でも **自動確定はしません**。  
また、候補 suggestion には **source を含めません**。

## `innerWavHealthThreshold=70` の使い方

`innerWavHealthThreshold=70` は、今は次の必要条件です。

- `High` 判定
- `ShouldAutoApplyDestination`

具体的には、1 位候補について

- primary health が threshold を超える

をまず viable destination の必要条件とし、その上で

- source ではない
- viable な 2 位候補と僅差ではない

ときだけ `High` になります。

つまり threshold は **候補 recall を削る前段フィルタ** ではなく、**viable destination / 自動確定の安全弁** です。

## `INSTL DST` 反映条件

`BMSLibrary.ApplyInstallEstimationResultToFiles(...)` では:

- `ShouldAutoApplyDestination == true`
- `DestinationDirectory` が空でない

ときだけ `instl_dst` を自動適用します。

それ以外では:

- `instl_dst = null`
- `HasViableDestination == true` の場合だけ代表 metadata を入れる
- `Confidence = Low` の場合だけ non-source suggestion を保持する
- そのうち候補が 2 件以上ある場合だけ warning を保持する

また、**UI に見える pending 状態で `instl_dst` を反映する経路**では、推定結果適用・手動 `INSTL DST` 入力・resolved destination 再利用・pending regroup のいずれでも、`INSTL DST TITLE` / `INSTL DST ARTIST` を同時に同期します。  
destination 側に代表譜面 metadata が存在しない場合のみ、`instl_dst` が入っても title/artist は空を許容します。

## 手動 `インストール先を推定` と `マージ先を推定` の違い

現在の手動操作は、入口の目的が明確に分かれています。

### 1. 手動 `インストール先を推定`

目的は、**未所持譜面の導入先を決めること**です。

- background auto-estimate 抑制とは無関係で、手動なら実行する
- pending package に対して実行した場合は `DeferredEstimateReason` を解除する
- package 内を
  - `alreadyInstalledFiles`
  - `missingFiles`
  に分ける

その上で:

- `missingFiles == 0`
  - 何もしない
- mixed package
  - まず `TryResolveInstalledDestinationFromPackage(...)` で、既所持譜面の実配置先を未所持譜面へ再利用できるか試す
  - 成功したら、その配置先を **未所持譜面だけ** に反映し、代表 metadata も同期して終了
  - 失敗したら `Fix` モードで **未所持譜面だけ** を package-aware 推定する
- 全部未所持
  - `Normal` モードで通常推定する

つまり通常推定は、mixed package では **「既所持側の配置先に未所持を寄せる補完」** が第一です。

### 2. 手動 `マージ先を推定`

目的は、**source baseline を上回る non-source merge 先があるかを見ること**です。

- package 単位では `MergeSourceBaseline` モードを使う
- source を候補に含めて比較する
- `DeferredEstimateReason` を解除した上で実行する

その上で:

- まず `TryResolveInstalledDestinationFromPackage(...)` を試す
- 解決できれば、その配置先を package 全体へ反映し、代表 metadata も同期する
- 解決できなければ `MergeSourceBaseline` で source を baseline に比較する

`MergeSourceBaseline` では、

- non-source が source を **materially** 上回る
  - merge destination を返す
- source が最善
  - `High + no destination`
- source が最善だが viable non-source と僅差
  - `Low + non-source suggestions`

となります。

つまりマージ推定は、**「source のままで十分か、それとも source より良い merge 先があるか」** を見る機能です。

## 既所持譜面を含む package の扱い

既所持譜面を含む mixed package は、通常推定とマージ推定で扱いが異なります。

### 通常推定

- 既所持譜面は warning 対象になる
- 推定対象は **未所持譜面だけ**
- まず既所持譜面の実配置先を再利用できるか試す
- 再利用できなければ、未所持譜面だけ `Fix` モードで推定する

つまり mixed package に対する通常推定は、**「既所持分の実配置先へ未所持分を寄せる」** 挙動です。

### マージ推定

- package 単位では **既所持・未所持をまとめて** 扱う
- 既存配置先再利用が成功すれば、その配置先と代表 metadata を package 全体へ入れる
- 失敗したら package 全体を `MergeSourceBaseline` で評価する

つまり mixed package に対するマージ推定は、**「package 全体の行き先を source baseline 付きで見直す」** 挙動です。

### regroup 時の補足

- pending regroup で expected destination が一意に解決できた場合も、`instl_dst` だけでなく代表 metadata を同期する
- regroup では warning 再初期化は行うが、metadata 同期のために suggestion / low-confidence 文脈を追加で消さない

### file 選択時の注意

file 群を対象にした場合は、現在まだ wrapper が完全には同じではありません。

- 通常推定
  - pending package に属する file は package 単位へ束ねて処理する
- マージ推定
  - 現在は file ごとに `MergeSourceBaseline` を回す

そのため、同じ pending package でも **package 選択時と file 選択時で merge 結果がずれる余地** は残っています。
現状 docs では、この差を既知の実装上の違いとして明示しておきます。

## pending batch と demand build

startup restore / auto-install 由来の pending 推定は、現在は queue で非同期に進みます。

- package 群は先に DataGrid に出る
- 推定は batch worker が package 単位で進める
- batch 開始前に package aggregate hash をまとめて `EnsureDirectoriesByHashes(...)` する

そのため、package ごとの `lazyHashBuildMsDelta` は小さく抑える設計です。

## 現在の設計で解決したこと

- `candidate only` 評価により destination が過小評価される問題
- `innerWavHealthThreshold` による比較前の候補全落ち
- source 別枠 reinject により無情報 source が勝ちやすい問題
- startup restore 時に保留推定完了まで一覧が出ない問題

## まだ残っている調整対象

- `竹取はっぴー` のような実機ケースで、package-aware union 評価後の実順位を再確認する
- `Precision / Jaccard` 重みの最終調整
- source を suggestion に含めることの UI/UX 妥当性
- loose-file 経路の bundled 空評価をどこまで維持するか
- 将来の `TITLE/ARTIST` tie-break や fingerprint 併用

## 関連資料

- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
- [install-estimation-target-design.md](install-estimation-target-design.md)
- [../spec/data-and-indexes.md](../spec/data-and-indexes.md)
