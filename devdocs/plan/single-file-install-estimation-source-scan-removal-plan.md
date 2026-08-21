# 単体譜面の導入先推定から親ディレクトリ再帰走査を除く実装計画

## 目的

単体ファイル package および loose chart の導入先推定では、譜面の親ディレクトリを package boundary とみなした再帰的な source surface 走査を行わないようにする。

これにより、単体譜面とは無関係な兄弟ファイルや子ディレクトリの規模によって、導入先推定が遅延したり、`SourceSurfaceScanLimitExceeded` warning によって自動推定が中止されたりする挙動をなくす。

一方、ディレクトリ package はフォルダ全体を導入するため、現行の bounded recursive scan、bundled resource 評価、source baseline、50,000 entry 上限、安全な部分結果破棄を維持する。

## ユーザーから見える問題

現在は、単体の `.bms` / `.bme` / `.bml` / `.pms` / `.bmson` を package として扱う場合でも、譜面の親ディレクトリ全体を source surface として再帰走査する。

親ディレクトリ配下に多数の無関係なファイルや子ディレクトリがあると、譜面自体が単体 package として正しく分類されていても、source surface の 50,000 entry 上限に到達し、次の結果になることがある。

- `SourceSurfaceScanLimitExceeded` warning が付く。
- `INSTL DST` の自動推定が中止される。
- 同じ譜面でも、配置元の親ディレクトリの規模だけで推定可否が変わる。

単体ファイル package の実インストール対象は譜面ファイル自身であり、親ディレクトリ全体ではない。このため、親ツリーの規模を単体ファイルの推定停止条件にすることは package boundary と一致しない。

## 現行実装の根拠

### 1. Snapshot builder が file / loose の親を走査する

対象: `BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallEstimationSnapshot.cs`

`PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(string, int)` は、package path がディレクトリなら `CreateDirectoryInstallSurfaceSnapshot(...)` を呼ぶ。ディレクトリでない場合は `ResolveSourceDirectory(...)` で親を求め、`CreateFileInstallSurfaceSnapshot(...)` を呼ぶ。

現行の `CreateFileInstallSurfaceSnapshot(...)` は、ディレクトリ package と同様に次を行う。

1. 親ディレクトリを `EnumerateSourceSurfaceBounded(...)` へ渡す。
2. `BoundedSourceSurfaceEnumerator` で全子ディレクトリを再帰走査する。
3. 列挙結果から `SourceCandidateResources` と scan metrics を作る。
4. 上限超過を `ScanLimitExceeded` として snapshot へ記録する。

`BuildForLooseEntries(...)` も `BuildSourceCandidateResourcesForLooseFiles(...)` を経由し、代表譜面の親ディレクトリに同じ file surface を構築する。

### 2. 通常推定は file surface の探索結果を候補ランキングに使わない

対象: `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInstallEstimationService.cs`

通常推定の resource 評価は `DefinedResources` と `BundledResources` を使い、candidate mode は `package_union_source_excluded` または `candidate_only_source_excluded` である。`SourceDirectory` 自身は候補一覧から除外される。

`SourceCandidateResources` は `EvaluateSourceBaseline(...)` の入力にはなるが、通常の外部 candidate ranking には加えられない。

`SourceDirectory` は次の意味があるため、file / loose でも保持する必要がある。

- 通常推定で配置元を candidate から除外する。
- reinstall correction で現在位置を baseline として評価する。
- source directory に関係する既存 lookup / diagnostics の識別情報を維持する。

したがって、今回廃止するのは親ディレクトリの列挙と resource surface の構築であり、`SourceDirectory` 自体ではない。

### 3. Background batch が file package の親を batch root にする

対象: `BeMusicSeeker/Models/BMSLibrary.cs`

`BuildPendingEstimateSourceBatchSnapshotUnsafe(...)` は、各 package の `SourceDirectory` を求め、次の条件を満たすと `rootsToScan` に追加する。

- missing chart がある。
- installed destination resolve が失敗扱いでない。
- unsupported resource path がない。
- `SourceDirectory` が存在するディレクトリである。

package path 自身がディレクトリかどうかは確認していないため、file package の親も batch root になる。

batch surface の割り当てでも package kind を確認せず、同じ `SourceDirectory` の shared surface を file package へ付ける。shared surface がなければ `BuildPackageInstallSurfaceSnapshot(state.Package.path)` へ fallback し、file package の親を再び走査する。

その後、`PrepareBackgroundPendingEstimatePackagesUnsafe(...)` は `HasSourceSurfaceScanLimitExceeded(state)` を先に確認する。一方、source baseline による defer 判定 `ShouldDeferPendingEstimateBatchPackageUnsafe(...)` は package path がディレクトリの場合だけ有効である。

この順序により、file package では source baseline を使わないにもかかわらず、親ディレクトリの走査上限だけで warning と推定中止が発生し得る。

単一 package / loose chart の直接推定でも、`EvaluateInstallEstimation(...)` は `ChartPackage` の cached surface または `BuildForLooseEntries(...)` の surface を snapshot に取り込み、`SourceSurfaceScanLimitExceeded` を確認してから candidate evaluation へ進む。このため、snapshot builder と background batch の両方を修正しなければ、入口によって file / loose の親走査が残る。

### 4. 再帰走査の上限は拡張子で絞る前の filesystem entry を数える

対象: `BeMusicSeeker/Models/Utils/BoundedSourceSurfaceEnumerator.cs`

`BoundedSourceSurfaceEnumerator` は、ファイルと子ディレクトリを訪問するたびに件数を加算し、その後に対象拡張子かを判定する。このため、BMS resource ではないファイルやディレクトリも 50,000 entry 上限へ寄与する。

この bounded scan はディレクトリ package の安全弁として維持するが、単体ファイルの親には起動しない。

### 5. 相対パス resource は親ツリー走査を必要としない

対象:

- `BeMusicSeeker/Models/BmsLibraryInternal/ChartResourceSnapshot.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryMaintenanceService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInstallEstimationService.cs`

`ChartResourceSnapshot.ResourceReference` は、`foo` と `sound/foo` を異なる chart-relative key として保持する。candidate 側の評価も path-aware key を使う。

既存リソースの実在確認が必要な経路では、`CountExistingResourceReferences(...)` と `ExistsWithCompatibleExtensions(...)` が、譜面に定義された相対パスと互換拡張子だけを直接確認する。

したがって、`sound/00.wav` のような定義があることを理由に親ディレクトリ全体を再帰列挙する必要はない。相対パスの有無を recursive scan の trigger にしない。

### 6. Package 分類は変更対象ではない

対象: `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs`

`SearchChartPackagesRecursivelyWithMetadata(...)` と `PrepareAutoInstallWorkflow(...)` が、選択状態、直下 chart、既存 resource、resource overlap などから directory package と file package を分類する。

今回の問題は分類後の導入先推定 surface にあるため、次は変更しない。

- directory package / single-file package の分類条件
- BMS / bmson の package kind 判定
- resource overlap threshold
- `SingleBmsFile` / `SingleBmsonFile` warning
- nested chart warning
- `RegroupEligibleSourceDirectories`
- 推定後の pending package regroup

## 変更後の契約

| 対象 | `SourceDirectory` | recursive source scan | `BundledResources` | `SourceCandidateResources` | batch surface | scan-limit warning |
| --- | --- | --- | --- | --- | --- | --- |
| directory package | package root | 実行する | package root の bounded scan 結果 | 同じ surface の clone | directory root 単位で共有する | 上限超過時に付与する |
| single-file BMS / bmson package | chart の親 | 実行しない | 空 | 空 | 付与しない | 親ツリー規模では付与しない |
| loose BMS / bmson entry | 代表 chart の親 | 実行しない | 空 | 空 | 対象外 | 親ツリー規模では付与しない |

file / loose の no-scan surface は次を満たす。

```text
SourcePath = 対象 file path または代表 chart path
SourceDirectory = SourcePath の親ディレクトリ
BundledResources = 空の独立 Entry
SourceCandidateResources = 空の独立 Entry
ScanMs = 0
ChartFileCount = 0
ResourceFileCount = 0
TrackedFileCount = 0
HashMaterializeMs = 0
ScanBackend = ""
ScanLimitExceeded = false
VisitedFileSystemEntryCount = 0
MaxVisitedFileSystemEntryCount = 0
```

`PackageInstallSurfaceSnapshot.Empty` は空 path 用の共有 instance であり、実在する file package の no-scan surfaceには使わない。file path と親ディレクトリを保持する新しい snapshot を都度構築する。

## 確定した設計判断

1. `BoundedSourceSurfaceEnumerator` を呼べる package kind は directory package だけにする。
2. file / loose の親ディレクトリは、相対パス resource の有無にかかわらず列挙しない。
3. file / loose の `SourceDirectory` は保持する。
4. file / loose の `BundledResources` と `SourceCandidateResources` は空にする。
5. file / loose の scan metrics は no-scan を表す 0、空文字、`false` にする。呼び出し側の limit 値を `MaxVisitedFileSystemEntryCount` へ記録しない。
6. `ChartPackage` の install surface cache は維持する。file package でも初回は cache miss、同一 path の2回目は cache hit とする。
7. background batch root、shared surface、fallback surface、source baseline gate は directory package だけに限定する。
8. directory package と file package が同じ親ディレクトリを指しても、directory surface を file package へ共有しない。
9. root が0件の batch は `ScanBackend = ""` とする。走査していない batch に `fast` などの backend 名を付けない。
10. directory package の既定上限 50,000、部分結果破棄、warning 文言、warning priority、log route は変更しない。
11. candidate scoring、coarse filter、audio gate、metadata tie-break、confidence、auto-apply 条件は変更しない。
12. 新しい scanner abstraction、service、DTO、warning kind、backend token、compatibility wrapper は追加しない。
13. user-facing string と localization resource は変更しない。
14. BMS と bmson は同じ package-kind contract にする。
15. release、distribution、publish、updater、version、dependency graph は変更しない。

## 実装前に読むもの

開始条件を確認した後、コード編集前に次を読む。

1. `AGENTS.md`
2. `.agents/skills/csharp-semantic-refactor/SKILL.md`
3. 本計画書
4. `devdocs/spec/testing-strategy.md`
5. 更新対象の current specs
   - `devdocs/spec/install-estimation-current-logic.md`
   - `devdocs/spec/path-length-and-io.md`
   - `devdocs/spec/warning-model.md`
   - `devdocs/spec/bms-bmson-chart-abstraction-current-state.md`
6. production route
   - `BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallEstimationSnapshot.cs`
   - `BeMusicSeeker/Models/ChartPackage.cs`
   - `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInstallEstimationService.cs`
   - `BeMusicSeeker/Models/BMSLibrary.cs`
   - `BeMusicSeeker/Models/BmsLibraryInternal/PendingEstimateSourceBatchSnapshot.cs`
   - `BeMusicSeeker/Models/Utils/BoundedSourceSurfaceEnumerator.cs`
   - `BeMusicSeeker/Models/BmsLibraryInternal/ChartResourceSnapshot.cs`
   - `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryMaintenanceService.cs`
   - `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs`
7. tests
   - `BeMusicSeeker.Tests/BmsLibraryInstallEstimationServiceTests.cs`
   - `BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs`
   - `BeMusicSeeker.Tests/BmsLibraryPendingPackageRegroupTests.cs`

コード、設定、script を編集する前に、ルートが `AGENTS.md` と `devdocs/spec/codex-agent-workflow.md` に従って本計画を current evidence で再検証する。実装へ渡す draft には、少なくとも Goal / Context / Constraints / Done when、上記の確定判断、対象外、開始 branch / HEAD / status、変更予定 path と ownership、検証 command を含める。

サブエージェントへ実装を委任する draft は、確定前に `.codex/agents/plan-clarifier.toml` の `plan-clarifier` へ一度だけ渡す。clarifier は repository で解決できる事実、未決 semantics、unsafe assumption、並列編集の衝突だけを点検し、計画全体を作り直さない。ルートは repo 内の正本から解ける事項を自ら解決し、真に一意に決まらない判断だけをユーザーへ確認して、回答を final plan に反映する。

final plan 確定後は、原則として `.codex/agents/implementation-worker.toml` の `implementation-worker` に、1 Unit と競合しない writable path を明示して実装させる。同じ file を複数 worker に同時所有させない。worker が routine failure ではない重大な blocker を発見した場合だけ、worker 自身が `issue-resolver` を一度呼び、結果を取り込んでから続行する。

## 実装 Unit 1: Snapshot surface の package-kind ownership を修正する

### Observable outcome

`PackageInstallEstimationSnapshotBuilder` が、directory package だけを再帰走査する。single-file package と loose entry は、親ディレクトリを一切列挙しない no-scan snapshot を返す。

### Production 変更

対象: `BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallEstimationSnapshot.cs`

1. `BuildPackageInstallSurfaceSnapshot(string, int)` の directory 分岐は維持する。
   - `LongPathFileSystem.DirectoryExists(normalizedPath)` が真のときだけ `CreateDirectoryInstallSurfaceSnapshot(...)` へ進む。
   - limit 引数は directory bounded scan の test seam として維持する。
2. 非 directory 分岐は、`ResolveSourceDirectory(normalizedPath)` の結果を使って no-scan surface を返す。
3. 現在の `CreateFileInstallSurfaceSnapshot(string, string, int)` から `EnumerateSourceSurfaceBounded(...)` を除く。
   - helper 名を維持する場合は limit 引数を削除し、no-scan 構築だけを行う。
   - より明確な private 名へ変える場合も、repository-wide replacement は使わず、compiler-driven な局所編集にする。
4. `BuildSourceCandidateResourcesForLooseFiles(...)` を削除する。
5. `BuildForLooseEntries(IEnumerable<PackageChartEntry>, int)` を削除する。
   - limit は loose entry の observable input ではなくなる。
   - production caller は引数なし overload だけであることを実装前の `rg` と build で再確認する。
6. `BuildForLooseEntries(IEnumerable<PackageChartEntry>)` は、代表 chart の path と親を保持した no-scan surface semantics で snapshot を構築する。
7. 次は維持する。
   - `NormalizeEntries(...)`
   - `SelectRepresentativeEntry(...)`
   - `DefinedResources`
   - `TargetMetadataProfile`
   - `RepresentativeChart`
   - `ChartCount`
   - unsupported relative reference の保持
8. `BuildSharedInstallSurfaceSnapshot(...)` と `CreateDirectoryInstallSurfaceSnapshot(...)` の directory semantics は変更しない。
9. `EnumerateSourceSurfaceBounded(...)` の production caller が directory surface route だけになることを確認する。
10. `PackageInstallSurfaceSnapshot.Empty` を実在 file path の no-scan surfaceとして返さない。

`BeMusicSeeker/Models/ChartPackage.cs` は原則として読み取り確認のみとする。cache contract を満たすための実在する不具合が判明した場合だけ編集し、理由と追加テストを記録する。

### 退役する route

- loose entry から `BuildSourceCandidateResourcesForLooseFiles(...)` を経て親ディレクトリを走査する route
- file package の `CreateFileInstallSurfaceSnapshot(...)` から `EnumerateSourceSurfaceBounded(...)` を呼ぶ route
- loose entry に scan limit を渡すだけの `BuildForLooseEntries(..., int)` overload

不要になった helper を forwarding wrapper として残さない。

### 維持する invariant

- directory package の resource surface、metrics、limit 超過、部分結果破棄は従来どおり。
- file package の `SourcePath` / `SourceDirectory` は cache key と candidate exclusion のため保持する。
- `ChartPackage.GetOrBuildInstallEstimationSnapshotFromEntries(...)` の cache は file / directory の両方で機能する。
- path 変更時の `InvalidateInstallEstimationSnapshot()` と SourcePath 比較による再構築を維持する。
- BMS / bmson の chart projection と resource reference 集約を変更しない。

### Tests

対象:

- `BeMusicSeeker.Tests/BmsLibraryInstallEstimationServiceTests.cs`
- `BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs`

既存 fixture と helper を再利用し、小さい temp directory で決定的に検証する。

1. `PackageInstallEstimationSnapshotBuilder_FilePackageDoesNotIncludeSiblingBundledResources` を full no-scan contract の test に更新する。
   - chart と同じ親に sibling audio、image、movie、別 chart を置く。
   - nested directory に無関係なファイルを置く。
   - `BundledResources` と `SourceCandidateResources` が空である。
   - `SourcePath` と `SourceDirectory` が正しい。
   - 全 scan metrics が no-scan 値である。
2. `PackageInstallEstimationSnapshotBuilder_FilePackageStopsSourceSurfaceScanAtLimit` を置換する。
   - file path に `maxVisitedFileSystemEntryCount: 1` などの小さい値を渡しても走査しない。
   - `ScanLimitExceeded == false`。
   - visited / max count はともに0。
   - sibling / nested file の個数に影響されない。
3. directory package の low-limit control test を追加または既存 test を拡張する。
   - directory package は小さい上限で打ち切られる。
   - visited count は max を超える。
   - `ScanLimitExceeded == true`。
   - 部分的な `BundledResources` / `SourceCandidateResources` を使用しない。
4. loose bmson entry の no-scan test を追加する。
   - bmson に `sound/00.wav` のような relative-path resource reference を持たせる。
   - 親に sibling / nested resource を置いても surface は空、metrics は0。
   - `DefinedResources` には path-aware reference が残る。
5. file package cache test を追加または既存 path package testを分岐する。
   - 初回 `SourceSurfaceCacheHit == false`。
   - 同一 path の2回目 `SourceSurfaceCacheHit == true`。
   - 両方とも backend は空、tracked / visited / max count は0。
6. `PrepareAutoInstallWorkflow_DoesNotPrebuildSourceSurfaceForDiscoveredPackages` の期待値を package kind で分ける。
   - directory package: 初回 miss、2回目 hit、backend は `bounded_fast_source_surface`、tracked count は正数。
   - file package: 初回 miss、2回目 hit、backend は空、resource / tracked / visited / max count は0。
   - discovery 自体が source surface を eager build しない契約は維持する。

既存 test に付いている `DoNotParallelize` をこの変更の都合で増やさない。固定 sleep、大容量 fixture、50,000 files の生成を追加しない。

### Unit 1 verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~BmsLibraryInstallEstimationServiceTests|FullyQualifiedName~BmsLibraryPackageInstallServiceTests'
```

続けて次を確認する。

```text
git status --short
git diff --name-only
git diff --check
```

Unit 1 時点で許容する変更は、本計画書と次の対象だけである。

- `BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallEstimationSnapshot.cs`
- `BeMusicSeeker.Tests/BmsLibraryInstallEstimationServiceTests.cs`
- `BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs`
- compiler / test evidence によって必要性を確認した `BeMusicSeeker/Models/ChartPackage.cs`

### Static review scope

- file / loose から recursive enumerator へ到達する route が残っていないか。
- directory package の bounded scan と partial-result discard を壊していないか。
- no-scan surface が path と独立 resource entry を保持するか。
- cache hit / invalidation contract が保たれているか。
- BMS / bmson parity と relative-path `DefinedResources` が保たれているか。

### 再計画条件

次の evidence が見つかった場合は Unit 2 へ進まず再計画する。

- file / loose の `SourceCandidateResources` を通常 candidate ranking や別の user-visible resultが実際に消費している。
- 親ディレクトリの再帰 surface が file install の移動対象と一致することを要求する別 contract がある。
- `BuildForLooseEntries(..., int)` に plan 外の caller がある。
- cache contract を維持するには public / protected / internal API の追加が必要になる。

## 実装 Unit 2: Background batch の source surface を directory package だけに限定する

### Observable outcome

background pending estimate と複数 package 推定の batch で、single-file package の親を scan root にせず、directory package の shared surface を file package へ割り当てない。file-only batch は filesystem source scan を行わず、scan-limit warning に到達しない。

### Production 変更

対象: `BeMusicSeeker/Models/BMSLibrary.cs`

1. `BuildPendingEstimateSourceBatchSnapshotUnsafe(...)` の state 作成時または root 選定時に、package path 自身がディレクトリかを判定する。
2. `rootsToScan` に追加できるのは、既存条件に加えて `LongPathFileSystem.DirectoryExists(state.Package.path)` が真の state だけにする。
3. surface 割り当て loop では、non-directory package を `sourceSurfaceByRoot` lookup より前に skip する。
   - `SourceSurface` は `null` のまま。
   - `UsesBatchSourceSurface` は `false` のまま。
   - `SourceDirectory` は chart の親を保持する。
4. `BuildPackageInstallSurfaceSnapshot(state.Package.path)` の fallback は directory package だけに限定する。
5. directory package と file package の `SourceDirectory` が同じでも、file state に shared surface を付けない。
6. root が0件なら `PendingEstimateSourceBatchSnapshot.ScanBackend` は `string.Empty` のままにする。
   - 現在の `?? "fast"` fallback を削除する。
   - directory root がある場合の `bounded_fast_source_surface` は維持する。
7. `EvaluateInstallEstimation(...)` の `sourceSurfaceBatchHit` 判定にも package path の directory guard を加える。
   - state 構築側の誤りがあっても file package が shared surface を消費しない defense-in-depth とする。
8. `ShouldDeferPendingEstimateBatchPackageUnsafe(...)` の既存 directory guard は維持する。
9. `HasSourceSurfaceScanLimitExceeded(...)`、warning application、log、deferred reason は変更しない。file state に limit surface が付かなくなることで到達不能にする。
10. `TryPopulatePendingEstimateSourceSurfaceViewsUnsafe(...)` は directory roots のみを受ける既存 owner として維持する。
11. `PendingEstimateSourceBatchPackageState` に新しい package-kind property を追加しない。既存 path と `LongPathFileSystem.DirectoryExists(...)` で必要な boundary を表現する。

`BeMusicSeeker/Models/BmsLibraryInternal/PendingEstimateSourceBatchSnapshot.cs` と `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInstallEstimationService.cs` は原則として読み取り確認のみとする。実在する consumer contract により変更が必要な場合は、変更理由、observable impact、追加テストを記録する。

### 退役する route

- file package の親を `rootsToScan` へ追加する route
- shared directory surface を同じ親の file package へ付ける route
- file package に対する batch fallback surface build
- rootless batch に実行していない backend 名 `fast` を設定する route

### 維持する invariant

- directory package の root deduplication、chunk count、metrics aggregation、bounded backend を維持する。
- directory package の source baseline prefilter と defer semantics を維持する。
- `SourceSurfaceScanLimitExceeded` は directory package で引き続き warning / deferred result になる。
- file package の `SourceDirectory` は candidate exclusion と reinstall correction用に保持する。
- installed destination resolve、unsupported resource path、missing / already-installed partition、regroup logic を変更しない。
- package-level parallelism と candidate evaluation degree を変更しない。

### Tests

主対象: `BeMusicSeeker.Tests/BmsLibraryPendingPackageRegroupTests.cs`

必要に応じて Unit 1 の test files へ関連 assertion を追加する。`WithTemporaryLibrary(...)`、`SeedPendingPackages(...)`、`CreatePendingSingleFilePackage(...)`、`CreateBmsonFile(...)`、`SetLibraryResourceIndex(...)` など、この test class の既存 helper を再利用する。private batch builder の検証は、既存の reflection style に合わせた局所 helper を使ってよい。production に test-only API を追加しない。

1. file-only batch test
   - single-file BMS package と single-file bmson package を用意する。
   - 各親に sibling / nested files を置く。
   - `RootCount == 0`、`ChunkCount == 0`。
   - tracked / resource / visited / max counts は0。
   - `ScanBackend == string.Empty`。
   - 各 state の `SourceSurface == null`、`UsesBatchSourceSurface == false`。
   - 各 `SourceDirectory` は chart の親。
2. directory package control test
   - directory path を package path とする。
   - `RootCount == 1`。
   - state に bounded source surface が付く。
   - backend、tracked count、resource entry が現行 contract を満たす。
3. mixed package-kind same-parent test
   - 1つの directory package root と、その root 直下の chart file package を同じ batch に入れる。
   - root は directory package 由来の1件。
   - directory state だけが surface を持つ。
   - file state は同じ `SourceDirectory` でも surface を共有しない。
4. relative-path resource を持つ single-file BMS の end-to-end 推定 test
   - source chart は `sound/00.wav` のような relative-path resource を定義する。
   - source parent に無関係な nested subtree を置く。
   - library candidate に同じ relative key の resource を登録する。
   - candidate directory が導入先として選ばれる。
   - source directory 自身は candidate にならない。
   - `SourceSurfaceScanLimitExceeded` warning は付かない。
5. BMS / bmson parity test
   - package kind が file なら、chart kind に関係なく batch root と surface がない。
   - `DefinedResources` / representative chart は各 format の projection から維持される。
6. directory scan-limit regression
   - Unit 1 の低上限 directory snapshot test で `SourceSurfaceScanLimitExceeded` が package estimation snapshot まで伝播することを確認する。
   - Unit 2 では warning application / deferred reason の production route が変更されていないことを targeted audit する。
   - batch 専用の limit injection や test-only production API は追加しない。
   - 50,000 files は生成しない。

### Current spec の更新

Unit 2 と同じ変更で次を最新化する。

#### `devdocs/spec/install-estimation-current-logic.md`

`Source package surface` と `Background pending estimate` を、次の契約へ改める。

- recursive source surface は directory package の package root だけ。
- single-file package / loose chart は親ディレクトリを列挙しない。
- file / loose の `BundledResources` と `SourceCandidateResources` は空。
- file / loose の scan metrics は no-scan 値。
- file / loose でも `SourceDirectory` は保持し、配置元の candidate exclusion 等に使う。
- 50,000 entry 上限、partial result discard、`SourceSurfaceScanLimitExceeded` は directory package のみ。
- background batch root、shared surface、source baseline prefilter は directory package のみ。
- relative-path resource は path-aware key で評価し、その有無を recursive scan の条件にしない。

#### `devdocs/spec/path-length-and-io.md`

- bounded source-surface I/O contract を directory package root に限定する。
- file / loose の親を package boundary として recursive enumeration しないことを明記する。
- directory package の long-path-aware bounded scan、上限、部分結果破棄を維持する。

#### `devdocs/spec/warning-model.md`

- `SourceSurfaceScanLimitExceeded` は directory package root の bounded scan 上限超過時だけに付く。
- single-file package / loose chart の親ツリー規模ではこの warning を生成しない。
- warning kind、priority、digest、highlight、localized text は変更しない。

#### `devdocs/spec/bms-bmson-chart-abstraction-current-state.md`

- directory package は BMS / bmson を問わず source surface を scan / share する。
- file / loose は BMS / bmson を問わず no-scan、empty resources、zero metrics とする。
- `SourceDirectory`、`DefinedResources`、`RepresentativeChart`、metadata profile は保持する。
- batch source surface の共有対象は directory package roots だけとする。

4文書を検索し、single-file / loose の親を source surface として再帰走査する旧記述を残さない。変更後の実装と current spec の用語を一致させる。

### Unit 2 verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~BmsLibraryPendingPackageRegroupTests|FullyQualifiedName~BmsLibraryInstallEstimationServiceTests|FullyQualifiedName~BmsLibraryPackageInstallServiceTests'
```

続けて次を確認する。

```text
git status --short
git diff --name-only
git diff --check
```

### Static review scope

- batch root、surface lookup、fallback、consumer guard の全てが directory package boundary と一致するか。
- same-parent mixed kind で file state へ surface が漏れないか。
- rootless metrics / backend が実行実態を正しく表すか。
- directory package の warning / baseline / metrics を退行させていないか。
- relative-path resource の candidate matching と source exclusion が維持されるか。
- current specs が実装と矛盾しないか。

### 再計画条件

次の evidence が見つかった場合は、Functional 前に再計画する。

- file package に source baseline surface を要求する別の user-visible path が存在する。
- `SourceSurface` を `null` にすると package kind と無関係な必須 consumer が壊れる。
- batch package kind を正しく判定するために persisted schema や public contract の変更が必要になる。
- directory package と file package の same-parent surface 共有が、別の明示仕様として要求されている。

## 変更予定 path

通常は次の10 pathだけを変更する。

```text
devdocs/plan/single-file-install-estimation-source-scan-removal-plan.md
BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallEstimationSnapshot.cs
BeMusicSeeker/Models/BMSLibrary.cs
BeMusicSeeker.Tests/BmsLibraryInstallEstimationServiceTests.cs
BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs
BeMusicSeeker.Tests/BmsLibraryPendingPackageRegroupTests.cs
devdocs/spec/install-estimation-current-logic.md
devdocs/spec/path-length-and-io.md
devdocs/spec/warning-model.md
devdocs/spec/bms-bmson-chart-abstraction-current-state.md
```

次は contract 確認対象であり、原則として編集しない。

```text
BeMusicSeeker/Models/ChartPackage.cs
BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInstallEstimationService.cs
BeMusicSeeker/Models/BmsLibraryInternal/PendingEstimateSourceBatchSnapshot.cs
BeMusicSeeker/Models/Utils/BoundedSourceSurfaceEnumerator.cs
BeMusicSeeker/Models/BmsLibraryInternal/ChartResourceSnapshot.cs
BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryMaintenanceService.cs
BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs
```

予定外 path の編集が必要になった場合は、編集前に必要性を compiler error、test failure、実在 consumer のいずれかで確認し、変更理由、受入条件、追加検証を記録する。release file、resource file、project file、lock file、generated file は変更しない。

## 全体検証

両 Unit の Quick が成功した後、通常機能検証を実行する。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

要件:

- command 全体が180秒以内に完了する。
- tracked file を検証処理が書き換えない。
- timeout や flaky failure を、timeout 延長や無制限再実行で隠さない。
- failure 時は `artifacts\verification`、active / last observed test、console progress、TRX、blame artifact を確認し、共有 state、fixture ownership、固定待ち、file / process / port 競合、I/O、入力規模を調べる。
- runner、lane、parallelism、fixture 配置をこの変更の回避策として変えない。

Functional 実行前後で、少なくとも `git status --short`、`git diff --name-only`、`git diff --check` を比較する。tracked diff の内容を比較できる hash を取得してもよい。検証で source diff が変化した場合は原因を特定し、現在の run が生成したことを確認できる pathだけを局所的に戻す。`git reset --hard`、`git clean -fd`、repository 全体への checkout や format は使わない。

この変更では Full を実行しない。release、distribution、publish、updater の boundaryを変更しないためである。

## 静的 audit

Functional 成功後に次を実行し、全 hit を分類する。

```powershell
rg -n "BuildSourceCandidateResourcesForLooseFiles|BuildForLooseEntries\(|CreateFileInstallSurfaceSnapshot|EnumerateSourceSurfaceBounded" BeMusicSeeker BeMusicSeeker.Tests
rg -n "SourceSurfaceScanLimitExceeded|source surface|single-file|loose chart|単体譜面|親ディレクトリ" devdocs/spec BeMusicSeeker BeMusicSeeker.Tests
git diff --check
git status --short
git diff --name-status
```

確認事項:

- `BuildSourceCandidateResourcesForLooseFiles` は production tree に残らない。
- `BuildForLooseEntries(..., int)` は残らない。
- file 用 helper が残る場合、`EnumerateSourceSurfaceBounded` を呼ばない。
- `EnumerateSourceSurfaceBounded` の production call は directory surface route だけ。
- `SourceSurfaceScanLimitExceeded` の warning route は残る。
- file / loose の surface から warning へ到達する route はない。
- current specs に file / loose の親を recursive source surface とする記述がない。
- relative-path resource の path-aware semantics は残る。
- user-facing resource と warning text に差分がない。
- final diff が本計画の path 範囲に収まる。

Markdown は UTF-8、LF、末尾空白なしとし、文書中の file path と symbol が実在することを確認する。

## Repository static review

実装、Quick、Functional、静的 audit が完了したら、`AGENTS.md` に従って `.codex/agents/repo-static-review.toml` の `repo-static-review` を呼ぶ。

reviewer 実行中、呼出元の実装担当は repository への読み取り、検索、編集、build、test、format、stage、commit を行わず、reviewer が確認する snapshot を固定する。

reviewer へ渡すもの:

- 本計画の目的、変更後の契約、非対象、受入条件
- base branch / commit
- 開始時と現在の `git status --short`
- base からの tracked diff と未追跡の本計画書
- Unit 1 / Unit 2 の変更 path
- 実行した Quick / Functional command、結果、所要時間
- Functional 前後で tracked diff が変化していないこと
- 静的 audit の hit 分類
- 各 Unit の static review scope

P0 / P1 と、受入条件へ直接反する P2 を修正する。scope 外の全面整理や単なる改善提案は同じ変更へ取り込まない。修正後は影響する Quick を再実行し、Functional の前提を変えた場合は Functional も再実行する。その後、変更後 snapshot を fresh reviewer に渡し、直前の review finding の解消と直接影響する invariant を再確認する。

## 受入条件

次の全てを満たしたら実装完了とする。

1. single-file BMS / bmson package の親ディレクトリを source surface として列挙しない。
2. loose BMS / bmson entry の親ディレクトリを source surface として列挙しない。
3. file / loose の `BundledResources` と `SourceCandidateResources` が空である。
4. file / loose の scan metrics が全て no-scan 値である。
5. file / loose の `SourceDirectory`、`DefinedResources`、`RepresentativeChart`、metadata profile を維持する。
6. relative-path resource を持つ file / loose でも recursive scan を開始せず、path-aware candidate matching が機能する。
7. file-only background batch の root / chunk / scan counts が0、backend が空、state surface が `null` である。
8. directory / file が同じ親を共有しても、file state に directory surface が付かない。
9. directory package は従来どおり bounded recursive scan を行う。
10. directory package は低い上限で打ち切られ、部分 resource surface を推定に使わない。
11. `SourceSurfaceScanLimitExceeded` は directory package の上限超過時に引き続き機能する。
12. package classification、regroup、candidate scoring、confidence、warning text を変更しない。
13. 4つの current spec が変更後の実装を正確に記述する。
14. 対象 Quick、Functional、`git diff --check`、static review が成功する。
15. 本計画外の path、version、resource、dependency、release artifact に差分がない。

## リスクと検出方法

### Directory package の走査まで止める

検出:

- directory package snapshot の resource count / backend test
- low-limit control test
- `EnumerateSourceSurfaceBounded` caller audit

対策:

- directory / non-directory 分岐を snapshot builder と batch の双方で明示する。

### `SourceDirectory` まで消して candidate exclusion を壊す

検出:

- no-scan snapshot の path assertion
- relative-path end-to-end test で source 自身が candidate にならないこと
- reinstall correction の既存 test

対策:

- no-scan は resource surface だけを空にし、path identity は保持する。

### Same-parent batch で file state に surface が漏れる

検出:

- mixed package-kind same-parent test
- `sourceSurfaceByRoot` lookup より前の directory guard review
- `EvaluateInstallEstimation(...)` の defense-in-depth guard review

### File cache が常に miss になる

検出:

- file package の first miss / second hit test
- path change invalidation の既存 test

対策:

- no-scan surface に正しい `SourcePath` を保存する。

### Specs が一部だけ旧挙動を残す

検出:

- 4文書を対象にした `rg` audit
- static reviewer の docs / implementation consistency review

### テストが通常検証を重くする

検出:

- Functional command 全体の180秒予算
- 固定待ち、`DoNotParallelize`、大量 file fixture の diff review

対策:

- 小さい temp fixture と低い injectable limit を使い、50,000 files を作らない。

## 完了報告に含めるもの

- 開始 branch / HEAD / status
- 変更した production / test / spec path
- 退役した旧 route
- directory package と file / loose の最終 contract
- `SourceDirectory` を保持した理由
- relative-path resource を維持した test
- directory scan-limit safety を維持した test
- 実行した全 verification command、結果、所要時間
- Functional 前後の tracked diff 不変確認
- 静的 audit の各 hit 分類
- static review finding と対応
- `git diff --check` の結果
- 最終 `git status --short` と `git diff --name-status`
- plan 外 path、user-facing resource、version、dependency に差分がないこと
- 未解決事項がある場合は、具体的な evidence と user-visible impact
