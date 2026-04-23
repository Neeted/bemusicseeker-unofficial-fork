# Library Scan / Source Enumeration Native Aggregation Plan

## Summary

relative-path 対応後の library build 性能悪化は、relative path そのものよりも、

- library build の mainline が
  - `EverythingBridge_x64.dll` の fixed 4-query scan
  - native 側 ownership/hash 集約

から外れ、

- grouped full-path enumeration
- managed 側での owner 探索 / relative hash 化 / 集約

へ寄ったことが主因と整理する。

この計画では次を固定方針とする。

- library build の Everything query は **4 回だけ**
  - chart
  - audio
  - image
  - movie
- query 後の ownership 集約と hash 化は **できるだけ native 側で完結**
- managed 側は **packed result を 1 パスで詰め替えるだけ** に寄せる
- `filename.wav` も `folder\\filename.wav` も、どちらも **chart-relative path** として扱う
  - bare filename を特別扱いしない
- roots を渡して列挙する共通化は進めるが、**共通化の単位は full-path 群ではなく native scan request / result contract** にする

`2026-04-24` 時点の current status:

- **Phase 1 / Phase 2 は実装済み**
  - library build mainline は grouped full-path enumeration を通らず、fixed 4-query native scan を使う
  - library build fixed scan は `EBridge_ScanChartAndResources` を唯一の正式契約として使う
  - managed 側は `ManagedDecodeMs` / `ManagedMaterializeMs` / `BridgeRawBufferBytes` で unpack 残差を追える
- **Phase 3 の実装は投入済み**
  - source-side mainline は `EBridge_ScanSourceRoots` を使う 4-query native surface に切り替えた
  - `BMSPackage.BMSFiles` は `PackageChartDiscoverySnapshot` だけを見る
  - install estimation 用の source surface は `PackageInstallSurfaceSnapshot` に分離した
  - source-side mainline から `__all__` query を外し、`tracked/chart/resource` count を canonical telemetry にした
  - source-side で Everything を使うかどうかは user setting で切り替える
    - 設定名: `保留パッケージの推定時に Everything を使用する`
    - 既定値: `false`
    - `false` のときは source-side を fast-only enumeration で処理する
- grouped enumeration は残すが、source-side mainline ではなく fallback / diagnostics / utility 用に寄せた
- source-side enumeration 自体は blocker ではなくなり、残差は pending estimate の評価 / orchestration 側へ移っている

Phase 3 実測 (`bin/Release/net472/install-performance.log`, setting=`false`):

- `auto_install_prepare`
  - `discoveryMs=978`
  - `installedCheckMs=0`
  - `warningClassifyMs=212`
  - `classificationMs=212`
  - `totalMs=2486`
- `pending_estimate_source_batch_build`
  - `packages=143`
  - `roots=123`
  - `chunks=0`
  - `nativeBridgeMs=0`
  - `elapsedMs=984`
- `pending_estimate_batch`
  - `start -> demand_build = 244ms`
  - `done elapsedMs=30508`
- `estimate_install start`
  - `sourceSurfaceScanBackend=fast` が `136/136`
  - `sourceSurfaceBatchHit=true` が `136/136`
  - `sumSourceSurfaceScanMs=0`

この状態では、source-side Everything を既定で使わない設定でも regress は出ておらず、chart-only source が大半の corpus では fast-only path が妥当と整理してよい。

Phase 1 実測 (`bin/Release/net472/install-performance.log`):

- `everything_scan totalMs=29472`
- `nativeBridgeMs=29429`
- `nativeBridgeReason=everything_bridge_fixed_scan`
- `song_tbl_file_check_breakdown scan_ms=29496`
- `bms_scan totalMs=29496`

Phase 1 前の grouped full-path mainline では:

- `everything_scan totalMs=209564`
- `nativeBridgeMs=66461`
- `bms_scan totalMs=276069`

したがって Phase 1 により、

- library build の `everything_scan` は `209564 -> 29472` で **約 85.9% 短縮**
- `bms_scan totalMs` は `276069 -> 29496` で **約 89.3% 短縮**

となり、grouped full-path enumeration による regress は解消できたとみなしてよい。

Phase 2 実測 (`bin/Release/net472/install-performance.log`):

- `everything_scan totalMs=27784`
- `nativeBridgeMs=26936`
- `managedDecodeMs=540`
- `managedMaterializeMs=275`
- `bridgeRawBufferBytes=331684812`
- `song_tbl_file_check_breakdown scan_ms=27808`
- `dirhash_build_ms=549`
  - `folder_hash_index_ms=43`
  - `resource_lookup_cache_ms=272`
  - `relative_path_hash_index_ms=234`
- `bms_scan totalMs=27808`

したがって Phase 2 により、

- library build の `everything_scan` は `29472 -> 27784` で **約 5.7% 短縮**
- `bms_scan totalMs` は `29496 -> 27808` で **約 5.7% 短縮**
- fixed-scan の残差は
  - native bridge: `26936ms`
  - managed unpack: `540 + 275 = 815ms`
  - index build: `549ms`
  に切り分けられる

## Phase 3 Step 0 Baseline

Phase 3 の比較用ログは Git 管理外の `.tmp` に保存しておく。

- current
  - `.tmp/phase3-step0-install-performance-2026-04-23-2357.log`
  - `.tmp/phase3-step0-application-2026-04-23-2352.log`
- healthy baseline
  - `.tmp/相対パス対応後、パフォーマンス改善第1段階後.log`

Step 0 で重要だったのは、source-side evaluation 本体だけでなく、**package drop 直後の `auto_install_prepare` が大きく悪化していた**ことだった。

- current `auto_install_prepare`
  - `discoveryMs=844`
  - `installedCheckMs=81339`
  - `warningClassifyMs=390`
  - `classificationMs=81730`
  - `totalMs=84256`
- healthy baseline `auto_install_prepare`
  - `discoveryMs=877`
  - `installedCheckMs=858`
  - `warningClassifyMs=277`
  - `classificationMs=1136`
  - `totalMs=3742`

したがって、Step 0 時点の regress 本体は discovery ではなく **installed check / classification** にあると整理できた。

- `discoveryMs` はほぼ同じ
- `warningClassifyMs` も小差
- `installedCheckMs` だけが `858 -> 81339` に跳ねている

回帰の本体は、directory package の `pkg.BMSFiles` 参照が source-side shared snapshot を起動し、**pending estimate に入る前の prepare 段階で重い package source enumeration を踏んでいたこと**にあった。

Phase 3 ではここを次で是正した。

- `PackageSourceScanSnapshot` を廃止し、`PackageChartDiscoverySnapshot` と `PackageInstallSurfaceSnapshot` に分離した
- `BMSPackage.BMSFiles` は chart discovery snapshot だけを参照する
- `PrepareAutoInstallWorkflow(...)` では discovery 時点で得た chart list を package に埋め込み、installed check / warning classification で再利用する
- install estimation 用の source surface は必要になった時点でだけ build する

## Step 0 Root Cause and Phase 3 Direction

### 1. library build regress は止まったが、Step 0 時点の source-side mainline が重かった

library build mainline はすでに fixed 4-query native scan に戻っていた。  
一方、Step 0 時点の source-side は次の経路を mainline にしていた。

- `BMSPackage.BMSFiles`
  - `GetOrBuildPackageSourceScanSnapshot(...)`
  - `PackageInstallEstimationSnapshotBuilder.BuildPackageSourceScanSnapshot(...)`
  - `RootFileEnumerationService.EnumerateFilesWithFallback(...)`
  - `ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(includeAllFiles: true)`
  - `ResourceSurfaceMaterializer.CreateSingleRootEntry(...)`

これは grouped full path 群を managed 側へ戻し、そこから source-surface hash を作る経路で、`auto_install_prepare` にも流入していた。

### 2. Step 0 時点の source-side mainline には `__all__` が入っていた

Step 0 時点の source-side mainline は `includeAllFiles: true` を前提にしており、`chart / audio / image / movie` に加えて `__all__` full-path query を使っていた。

しかし install destination 推定や source baseline で本当に必要なのは、

- grouped chart file paths
- audio/image/movie の basename / relative-path hash surface
- summary counts

であり、`__all__` full-path 自体は source of truth にしなくてよい。

### 3. `auto_install_prepare` が source-side scan を早すぎる段階で踏んでいた

`auto_install_prepare` の `installedCheckMs` 悪化は、directory package の `pkg.BMSFiles` アクセスが source-side snapshot build を起動していたことを示していた。

Phase 3 では、pending estimate や install estimation より前の

- installed check
- warning classification
- pending / auto-install classification

では **lightweight な chart list だけ**で処理し、full source surface build は後ろへ遅延させる。

## Design Principles

### 1. Library build と source-side enumeration は同じ request discipline に寄せる

共通化するのは次の入力モデルとする。

- roots
- 4 category query
  - chart
  - audio
  - image
  - movie
- aggregation mode

ただし result shape は用途ごとに分ける。

- library build
  - chart-directory owned aggregate/self-owned hash surface
- source-side surface
  - single-root owned hash surface

### 2. `__all__` query は mainline に入れない

performance critical path では `__all__` full-path enumeration を禁止する。

- library build は 4 query fixed
- source-side surface も原則 4 query fixed

`TotalFileCount` のような診断が必要なら、

- native 側 summary field で返す
- 4 category union count として定義する

のどちらかで済ませる。

### 3. relative path 正規化は 1 系統だけにする

resource path はすべて、

- chart directory から見た relative path を正規化
- basename hash はその副産物として作る

で統一する。

つまり

- `filename.wav`
- `folder\\filename.wav`

はどちらも relative path として同じ系統で処理し、scan/materialize 時点で分岐しない。

## Phase 1. Library Build Mainline Recovery

### Goal

library build を generic grouped enumeration から切り離し、fixed 4-query native scan を mainline に戻す。

### Changes

- `EverythingFileScanner` は `EverythingRootFileEnumerator` を使わない
- library build の Everything path は
  - query 構築
  - `EverythingNative.ExecuteScan(...)`
  - packed native result の decode
  だけに戻す
- `BmsFileScannerResultBuilder` と `ChartDirectoryScanBuilder.BuildFromGroupedPaths(...)` は
  - library build mainline から外す
  - fallback / tests / small-root utility に限定する

### Why first

いま最も大きい regress は library build であり、ここを直さずに grouped path 共通化を続けると設計ごと重い方向に固定される。

### Acceptance

- library build の Everything path で `EBridge_EnumerateGroupedFiles` を呼ばない
- library build の query 回数は 4 回だけ
- `everything_scan` ログは query 4 本の timings と native aggregation timings を中心に出す
- `everything_scan totalMs` が「数十秒台」へ戻ることを第1目標にする

## Phase 2. Native Aggregation Contract Rebuild

### Goal

library build の result contract を「managed で再構築しない」前提で明文化する。

### Changes

- bridge fixed scan API は `EBridge_ScanChartAndResources` を library build 正式契約として固定する
- managed 側は bridge export の使い分けを行わず、fixed-scan bridge が使えなければ fast scanner fallback に進む
- native result に含めるものを固定する
  - chart file paths
  - chart directories
  - aggregate basename hashes
  - aggregate relative-path hashes
  - self-owned basename hashes
  - self-owned relative-path hashes
  - query hit counts / query ms
  - native collect / assign / merge / pack diagnostics
- managed 側は `BmsScanResult` への 1 パス詰め替えだけにする
- `ManagedDecodeMs` / `ManagedMaterializeMs` / `BridgeRawBufferBytes` を固定 diagnostics として追加する

### Parallelization

parallel 化を入れるなら native 側で行う。

- category 別 raw hit collect
- resource assignment
- hash materialization

の単位で bridge 内並列化を検討する。

### Acceptance

- library build mainline に owner 探索ループが残らない
- `ChartDirectoryScanBuilder.AssignResourceFiles(...)` 相当は fallback 限定になる
- managed materialize 時間を独立計測できる

## Phase 3. Source-Side 4-Query Native Surface

### Goal

package source directory / loose-file source 側も、full-path grouped enumeration ではなく 4-query native aggregation に寄せる。

この段では次を同時に達成する。

- source-side mainline から `__all__` query をなくす
- package source surface を native aggregation 化する
- `auto_install_prepare` で `pkg.BMSFiles` 参照に伴って source-side full scan が走る構造を解消する

### Implemented Changes

- source-side 用の native contract を追加した
  - `EBridge_ScanSourceRoots`
  - `EBridge_FreeSourceRootsResult`
- 入力は library build と同じく
  - roots
  - chart/audio/image/movie query
- 出力は source-side 用に絞る
  - grouped chart file paths
  - root 単位の basename / relative-path hash arrays
  - `trackedFileCount`
  - `chartFileCount`
  - `resourceFileCount`
  - query / pack diagnostics

- `BMSPackage` の cache を 2 系統に分離した
  - `PackageChartDiscoverySnapshot`
  - `PackageInstallSurfaceSnapshot`

- `__all__` full-path query は mainline から削除する
  - `chart / audio / image / movie` 以外の query は投げない
  - 件数は native summary scalar で返す

- `auto_install_prepare` は lightweight な chart discovery / chart file list だけで進める
  - installed check
  - warning classification
  - pending / auto-install classification
  の段階で package source surface build を起動しない
  - full source surface は
    - source baseline evaluation
    - pending estimate
    - install estimation
    に必要になった時点で初めて要求する

- source-side fallback でも `__all__` は要求しない
  - grouped enumeration を使う場合も `chart / audio / image / movie` だけで組む

### Aggregation modes

最低限、次の 2 モードに分ける。

- `ChartDirectoryOwned`
  - library build 用
  - descendant resource を ancestor chart にも見せる
- `SingleRootOwned`
  - source/package surface 用
  - root ごとの hash surface を作る

### Managed impact

- `PackageInstallEstimationSnapshotBuilder`
- `BMSPackage`

は grouped full-path 群から `ResourceSurfaceMaterializer` を作るのを mainline ではやめ、native の single-root result をそのまま使う。

- `BMSPackage.BMSFiles` は chart query result または discovery snapshot を再利用し、source-side surface build と分離する
- `GetOrBuildPackageSourceScanSnapshot(...)` は廃止し、「chart list」と「resource surface」を別 cache に整理する
- `ResourceSurfaceMaterializer.CreateSingleRootEntry(...)` は fallback 用の位置づけに下げる

### Validation Targets

- package source surface でも `__all__` query を使わない
- mainline source-surface build は 4 query fixed
- `ResourceSurfaceMaterializer.CreateSingleRootEntry(...)` は fallback 限定に寄る
- `auto_install_prepare` は package source enumeration に支配されない
  - `installedCheckMs` を Step 0 の `81339ms` から大きく削減する
  - healthy baseline の `858ms` に近い低 single-digit seconds 帯へ戻すことを目標にする
- `auto_install_prepare` の discovery / installed-check / classification を個別に追える diagnostics を維持し、改善前後をログだけで比較できる

## Phase 4. Commonization by Contract, Not by Full Paths

### Goal

「roots を渡せば同じように列挙できる」を保ちながら、共通化の単位を request / result contract に切り替える。

### Changes

- `IRootFileEnumerator` を mainline abstraction にはしない
- mainline は用途別 typed scanner を持つ
  - library build scanner
  - source surface scanner
- 共通化はその下の request builder に寄せる
  - root normalization
  - 4 query build
  - bridge dispatch
  - result header decode

### Reason

full-path grouped enumeration を source of truth にすると、

- library build は重くなる
- managed 側で再集約が必要になる
- relative path / ownership の計算場所が曖昧になる

ため。

### Acceptance

- library build と source-side の「入力の共通化」は進む
- しかし result shape は用途別のまま維持される
- generic grouped full-path enumeration は fallback / diagnostics / small-root utility に役割を限定する

## Phase 5. Cleanup / Perf Gate / Legacy Removal

### Goal

重い managed materialization 経路を mainline から外し、性能 gate を明文化する。

### Changes

- library build mainline から次を外す
  - `EverythingRootFileEnumerator`
  - `BmsFileScannerResultBuilder`
  - `ChartDirectoryScanBuilder.BuildFromGroupedPaths(...)`
- source-surface mainline から次を外す
  - grouped full-path からの `ResourceSurfaceMaterializer`
  - mainline call site から grouped enumeration 依存が残っている箇所
- logs を次の 2 系統に分ける
  - library build
  - source-surface build
- diagnostics を追加する
  - query ms x4
  - native collect ms
  - native assign ms
  - native merge ms
  - native pack ms
  - managed decode/materialize ms

### Performance Gate

最低限、次を acceptance とする。

- library build の Everything query は常に 4 回
- library build mainline で millions of full path を managed へ返さない
- source-surface mainline でも `__all__` query を使わない
- relative-path ownership を維持したまま、library build wall-clock を pre-relative-path 水準へ戻す

stretch goal:

- relative-path 対応前の「並列化なしでも約 30 秒」水準を超える
- 今の並列化前提では、それより速い状態を目標にする

## Work Split Recommendation

### Pass A: まず戻す

- library build mainline を fixed 4-query bridge scan に戻す
- grouped enumeration は source-side と fallback に限定する

これは最短で最大の regress を止める段。

### Pass B: そのうえで source-side を native aggregation 化する

- package surface も 4-query native contract に寄せる
- `__all__` query と full-path materialize を mainline から消す

### Pass C: 最後に abstraction を整理する

- request builder 共通化
- result contract 分離
- fallback 限定コードの整理

## Non-Goals

この段では次を主目的にしない。

- install/merge 実処理の file move / overwrite 最適化
- candidate semantics の再変更
- basename-only fast path の再設計

まずは

- library build
- source-side install surface

の scan / aggregation / materialization を速くし、relative-path 対応を「重くない通常状態」に戻すことを優先する。
