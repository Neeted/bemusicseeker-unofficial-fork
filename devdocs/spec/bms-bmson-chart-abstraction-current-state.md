# BMS / bmson chart abstraction current state

## 目的

この文書は、現行実装における BMS / bmson の譜面抽象化の状態と、今後の ChartFile domain model 化で守る境界を記録する。

今後 `BMSFile` に残る chart 共通責務を `ChartFile` などのアプリ内 domain model へさらに分離していくために、現在どこまで chart として共通化され、どこに BMS / LR2 前提の境界が残っているかを明文化する。

旧 `devdocs/plan/bmson/bms-bmson-chart-abstraction-migration-plan.md` は、bmson 段階導入時の履歴資料であり、現在進行中の active plan ではない。今後の作業判断はこの文書を正とし、古い計画書へ進捗や方針を追記しない。

最終ゴール:

- アプリ全体で譜面を扱う入口を `ChartFile` domain model ベースにする。
- `BMSFile` は BMS 専用処理と LR2 `song` / `folder` 永続化に寄せる。
- `LR2SongDBExtended.bmson_song` は bmson 専用処理と `bmson_song` 永続化に寄せる。
- `PendingChartEntry : BMSFile` や `CompatibilityBmsFile` のような構造互換 adapter は最終形として残さない。
- production から使われない旧名 wrapper / 互換 API / test-only production code は残さない。
- 設定値名と UI 文言は、永続設定移行やユーザー体験の変更を避けるため、抽象化作業だけを理由には変えない。

## 用語

| 用語 | 現行実体 | 意味 |
| :--- | :--- | :--- |
| BMS chart | `BMSFile : LR2SongDB.song` | LR2 `song` / `folder` table 由来の所持 BMS 譜面。 |
| bmson chart | `LR2SongDBExtended.bmson_song` | アプリ独自 table `bmson_song` 由来の所持 bmson 譜面。 |
| storage row | LR2 `song` row, `LR2SongDBExtended.bmson_song` row | DB table へ保存する永続化単位。BMS の現行 in-memory owner は `BMSFile : LR2SongDB.song` だが、DB commit は LR2 `song` row 型として行う。bmson は app-owned `bmson_song` row。 |
| application chart file | `ChartFile` | 本アプリで譜面を操作・表示するための domain model。直接の DB 永続化型ではなく、Kind と storage owner を持つ。 |
| pending chart | `PackageChartEntry` / `ChartPackage.ChartEntries` | package / pending install 上の譜面。production の正本は `ChartFile` を持つ package entry であり、`BMSFile` 継承 adapter ではない。 |
| chart row | `LibraryChartRow`, `ChartListSourceRow` | 通常一覧や仮想 filter / sort 用の read model。storage の正本ではない。 |
| operation target | `ChartOperationTarget` | UI command / context menu が扱う操作対象。capability を持つ。 |
| library chart ref | `LibraryChartRef` | model 層の移動 / 削除などで使う BMS / bmson 共通参照。 |
| compatibility BMSFile | なし | bmson を既存 `BMSFile` 引数 API へ渡すために使っていた旧 adapter は削除済み。`BMSFile` 引数 API は BMS storage row / BMS-only 処理に限定し、bmson は `ChartFile` / `bmson_song` / `PackageChartEntry` を正本にする。 |

## Storage model

### BMS

BMS の所持譜面の正本は `BMSLibrary.BMSFiles` であり、要素は `BMSFile` である。

`BMSFile` は `LR2SongDB.song` を継承しており、次の LR2 / BMS 前提の情報を直接持つ。

- `hash` / `md5`
- `sha256`
- `path`
- `folder`
- `parent`
- title / artist / level / mode
- LR2 score / ranking / IR 関連の表示値
- BMS parser 由来の resource references
- runtime `ChartInfo`
- `maintenanceInfo`
- `RefTables`

`BMSLibrary.BMSFiles` の更新時には、playlist summary owned hash snapshot、installed chart key / directory index、parent folder cache、install estimation metadata profile cache、duplicate cache、resource health index が無効化される。

### bmson

bmson の所持譜面の正本は `BMSLibrary.BmsonSongs` であり、要素は `LR2SongDBExtended.bmson_song` である。

`bmson_song` は BMS の `song` table には入れない。永続列として path / folder / md5 / sha256 / title / subtitle / artist / genre / level / mode_hint / banner / backbmp / stagefile / preview_music / updated_at を持つ。

resource references、`HasFreshResourceReferences`、`ChartInfo`、`MaintenanceInfo` は `bmson_song` の runtime-only 情報であり、`bmson_song` table の列としては保存されない。DB から hydrate した `chart_info` / `maintenance` や、parser 直後の resource references を同一オブジェクトへ載せるための in-memory owner として扱う。

`BMSLibrary.BmsonSongs` の更新時には、playlist summary owned hash snapshot、installed chart key / directory index、parent folder cache、install estimation metadata profile cache、duplicate cache、resource health index が無効化される。

parent folder cache は更新通知としては bmson 変更でも無効化されるが、現行の parent folder candidate rebuild は `getBMSDirectories()` と `BMSFiles` snapshot を入力にする。bmson path 変更は cache refresh の契機にはなるが、bmson だけで新しい BMS root candidate を作るわけではない。

### 共通 table

次の永続情報は BMS / bmson の両方で使う。

| 情報 | 現行仕様 |
| :--- | :--- |
| `chart_info` | md5 / sha256 を介して BMS と bmson の両方へ適用する。 |
| `maintenance` | path / hash ベースの resource health snapshot として BMS / bmson の両方で使う。ただし encoding fix などは BMS 専用。 |
| playlist entry `sha256` | bmson playlist entry の primary identity として使う。BMS は従来どおり md5 identity が基本。 |

## Read model

### `LibraryChartRow`

通常一覧の表示 row は `LibraryChartRow` に寄せられている。

`LibraryChartRow` は次のいずれかを包む。

- `BMSFile`
- `LR2SongDBExtended.bmson_song`
- `PackageChartEntry` 由来の `ChartFile`

`LibraryChartRow.Chart` は `ChartFile` を返す。BMS では storage owner として実体 `BMSFile` を `BmsFile` に持つ。bmson では storage owner として `BmsonSong` を持つ。`ChartFile` 自体は既存 `BMSFile` API 用 adapter を保持せず、`LibraryChartRow` / `PlaylistDetailSourceRow` / `PlaylistDetailRow` / `ChartOperationTarget` も owned bmson の compatibility adapter surface を公開しない。pending / newly installed の bmson は `PackageChartEntry.Chart` と `bmson_song` owner から表示する。

所持 bmson row の一時表示状態は、ViewModel が持つ `ChartFileTransientState` cache から供給される。これは install destination、候補、warning snapshot、health / encoding など、DB storage row へ直接保存しない UI / repair 表示 state を row 再生成後も保持するための cache である。旧実装では同じ目的で shared `PendingChartEntry` adapter を保持していたが、現行実装では adapter object を row / operation target に公開せず、`ChartFileTransientState` を `ChartFileProjection.FromBmsonSong(...)` に重ねる。

表示用の読み取りでは、`ChartFile` が subtitle / warning snapshot / install destination 表示値、maintenance / resource health 表示値を持つ。BMS では `BMSFile` 由来、bmson では `bmson_song.MaintenanceInfo` と `ChartFileTransientState` 由来の mutable state を `ChartFileProjection` が read model に写す。`LibraryChartRow` / `ChartListSourceRow` / playlist detail row の WARNING 表示、install destination 表示、health / encoding 表示 getter は `ChartFile` を読む。`ChartListSourceRow` / playlist detail source は provider 経由で既存 transient state だけを読み、表示 / sort / keyword filter のためだけには adapter を新規作成しない。

表示列は BMS / bmson で共通化されているものが多い。

- title / artist / genre / folder / path
- md5 / sha256
- level / mode
- chart_info 系列
- resource health
- warning 表示
- playlist reference (`RefTablesSymbols` / `RefTablesNames`)

playlist reference 表示は、BMS では従来どおり `BMSFile.RefTables` を読む。所持 bmson row は `BmsFile == null` / `RealFile == null` なので、playlist entry の md5 / sha256 から作る読み取り専用の `PlaylistReferenceIndex` を通常一覧 row / virtual source row / playlist detail source row に注入し、chart identity から参照 table 表示を解決する。lookup は既存の参照適用と同じく md5 優先、sha256 fallback とする。

一方、次の列は BMSFile 由来に依存するため、所持 bmson では空または既定値になりやすい。

- LR2 score / ranking 系
- LR2 BMSID / diff name

### `ChartListSourceRow`

仮想 filter / sort / keyword search の入力は `ChartListSourceRow` である。

`BuildStandardLibraryRows(...)` は `BMSFile` と `bmson_song` を結合し、BMS / bmson 共通の source row を作る。bmson は path 順で追加される。

keyword / sort / virtual source row の基本判定は `ChartListSourceRow` の chart 共通プロパティを直接見る。`Title` / `Artist` / `Genre` / `Folder` / `Path` / `Mode` / `Level` / `Tag` / hash などの identity 系 getter は、row 生成時に作る warning なしの `ChartFile` snapshot を読む。`ChartInfo` は hydration 後の attach を反映できるよう storage owner から読む。install destination や warning 表示のように mutable state を反映する getter は、必要に応じて `ChartFileProjection` で現在 snapshot を作るが、`ChartListSourceRow` は `PendingChartEntry` provider を持たず、ViewModel から渡される `ChartFileTransientState` だけを読む。warning snapshot が不要な getter は provider にその旨を渡すため、install destination 表示だけで warning list を構築しない。`ChartListSourceRow` 自体は operation 用 `CompatibilityBmsFile` を公開しない。normal-library の folder tree filter は virtual source row と materialized row の両方で `NormalLibraryTreeFilter` を使い、`ChartListSourceRow` では `row.Path` / `row.Artist`、`LibraryChartRow` では `row.path` / `row.Artist` を見る。したがって folder / artist filter のために bmson `CompatibilityBmsFile` を作る経路は残さない。

bmson library rows は全ての tree mode に無条件で混ざるわけではない。`ShouldIncludeBmsonLibraryRowsInMainView(...)` は、通常 root / folder / keyword / mode filter と `FullScanAllChartsFilterSelected` では bmson を含めるが、playlist tree active、maintenance filter、install filter では除外する。maintenance / install / playlist detail 側は、それぞれ専用 source、`PackageChartEntry` / `ChartFile`、`ChartFileTransientState` / `PlaylistReferenceIndex` の経路で bmson を扱う。

通常一覧の subset view 用の仮想 filter / sort cache は `VirtualChartSubset*` helper で扱う。これは file missing / duplicate / pending install / newly installed / chart_info parse failure などの subset を `ChartListSourceRow` として並べ替える経路であり、BMS / bmson を含む chart row subset を対象にする。chart_info parse failure subset は warning 付き `ChartFile` projection をそのまま source row に渡し、表示のためだけに BMS / bmson compatibility adapter を materialize しない。performance log の scope 文字列は過去ログ検索互換のため、現状 `bms_file_subset` のまま残している。

### Duplicate view

duplicate view は `DuplicateChartGroups` / `SearchDuplicateChartGroups()` を入口にし、現行 snapshot は `BmsLibraryDuplicateService.BuildSnapshot(...)` で `BMSFiles` と `BmsonSongs` を結合する。

`DuplicateChartRow` は grouping / duplicate 判定用の path / primary lookup hash と、表示・operation の正本として `ChartFile` を持つ。BMS duplicate row は storage owner として `BMSFile` も保持し、bmson duplicate row は `ChartFile.BmsonSong` として storage owner を保持する。bmson duplicate row は `PendingChartEntry` compatibility adapter を materialize しない。duplicate subset の仮想一覧表示は `DuplicateGroup.ChartFiles` をそのまま `ChartListSourceRow` へ渡すため、projection-only warning も落とさない。

duplicate tree の group は `DuplicateGroup.ChartFiles` を正本にし、XAML は group の `Folders` だけを child node として表示する。旧 `DuplicateGroup.Files` facade と duplicate tree 用の `List<BMSFile>` parameter branch は削除済みであり、単一フォルダ内の重複 hash cleanup も `DuplicateGroup.ChartFiles` から削除対象を決め、ViewModel へ `ChartFile` として渡す。

duplicate warning の永続的な正本はまだ BMS 側に寄っている。BMS は `BMSFile` に warning を付与して標準一覧にも反映する。bmson は `bmson_song` に warning collection を持たないため、duplicate view の `DuplicateGroup.ChartFiles` に warning 付き `ChartFile` projection を置く。標準一覧の bmson storage row へ duplicate warning を直接永続化する境界ではない。

## Operation model

### `ChartFile`

`ChartFile` は Models 配下のアプリ内 chart read model である。DB row ではなく、UI 操作や一覧 read model が参照する domain object として扱う。

主なフィールド:

- `Kind`: `Bms` / `Bmson`
- `Path`
- `Directory`
- `Md5`
- `Sha256`
- title / artist / genre / folder / level / mode / tag
- `ChartInfo`
- `BmsFile`
- `BmsonSong`

`GridRowResolver.TryGetChartFile(...)` は、次の row から `ChartFile` を解決する。

- `PlaylistDetailRow`
- `PlaylistDetailSourceRow`
- `LibraryChartRow`
- `BMSFile`

`ChartFile` 生成は `ChartFileProjection` に集約する。`LibraryChartRow.Chart` と playlist detail row の `Chart` は storage row / playlist row / compatibility adapter からの値取り出しを行うが、最終的な `ChartFile` の組み立ては projection helper を通す。これにより、BMS / bmson / playlist missing の mapping drift を避ける。

通常一覧 row と playlist detail row は、どちらも row 側に `ChartFile` snapshot を持つ。`GridRowResolver` は `LibraryChartRow` / `PlaylistDetailSourceRow` / `PlaylistDetailRow` では row の `Chart` をそのまま返し、resolver 内では再構築しない。直接 `BMSFile` が渡された場合だけ、互換境界として `ChartFileProjection.FromBmsFile(...)` を使う。

`ChartFileProjection.FromBmsFile(...)` は BMS storage owner 専用であり、常に `Kind=Bms` / `BmsFile=file` の chart を作る。`FromBmsonSong(...)` は storage owner として `bmson_song` を持ち、必要な場合だけ caller が `ChartFileTransientState` を渡して表示 snapshot を作る。`ChartFileTransientState` は adapter object ではなく subtitle / install destination / warning / health / encoding の一時状態だけを受け取る。

`ChartFileProjection` は path / hash / title / artist / level / mode / chart_info に加えて、表示に必要な subtitle / warning snapshot / install destination 表示値も集約する。UI row / operation target からは compatibility adapter surface を削除済みであり、表示 getter は adapter API ではなく `ChartFile` / `ChartFileTransientState` を読む。

playlist row では `RealFile` があれば BMS として扱い、`ResolvedBmson` があれば bmson として扱う。どちらもない playlist entry は、現状 `ChartFileKind.Bms` の missing row として扱われる。

`GridRowResolver.GetRealBmsFile(...)` は、既存 View / drag-drop / preview 経路の BMS-only 互換 API として残っている。これは実体 BMS `BMSFile` だけを返し、bmson adapter は返さないため、chart 種別を判断する正本ではない。新しい operation 判定は `TryGetChartFile(...)` / `TryGetChartOperationTarget(...)` と capability を優先する。chart-common mutation は `ChartFile` / `LibraryChartRef` / `PackageChartEntry` を使い、BMS-only handler だけが `ChartFile.BmsFile` や `GetRealBmsFile(...)` を見る。旧 `GridRowResolver.GetCompatibilityBmsFile(...)` は production 参照がなく、テストだけの互換 helper になっていたため削除済みである。`FOLDER` セル編集は `TryGetFolderEditChartOperationTarget(...)` で `MoveInLibrary` capability を確認し、UI thread 上で `RenameChartFolderTargetSnapshot` を作ってから `RenameChartFolder(...)` へ渡すため、owned bmson row も BMS row と同じ folder rename 経路に入るが、background task 側で bmson compatibility adapter を materialize しない。`ChartFile.BmsFile` は BMS storage owner だけを表し、bmson adapter には使わない。

### `ChartOperationTarget`

`ChartOperationTarget` は UI command / context menu の primary target である。`ChartFile` と capability を持ち、owned bmson を legacy `BMSFile` adapter へ lazy materialize する provider は持たない。capability 判定、repository open、folder move、library remove、repair installed location、pending install destination など chart-common operation は `ChartFile` / `LibraryChartRef` / `PackageChartEntry` を入口にする。

主なフィールド:

- `Chart`: `ChartFile`
- `PlaylistEntry`
- `SourceScope`
- `IsOwned`
- `IsPending`
- `IsPlaylistMissing`
- `Capabilities`

`SourceScope` は次を区別する。

- `Library`
- `PendingPackage`
- `NewlyInstalledPackage`
- `PlaylistOwned`
- `PlaylistMissing`

UI は原則として `Kind` 直接判定ではなく capability を見る。handler 側でも capability を再確認する。

library mutation へ渡す `LibraryChartRef` は `ChartOperationTarget.ToLibraryChartRef()` で作る。`ToLibraryChartRef()` は `ChartFile` を正本にし、bmson row の削除 / 移動参照を作るだけでは compatibility adapter を新規作成しない。`ChartOperationTarget.ToPackageChartEntry()` も、source `PackageEntry` がなければ `ChartFile` から package entry を作り、BMS storage owner がある場合だけ BMS format adapter に落とす。単なる wrapper だった `ChartOperationTarget.ToCompatibilityBmsFile()` と `CompatibilityBmsFile` surface は削除済みである。

### Capability

現行 capability は次の通り。

| Capability | BMS | bmson | 現行意味 |
| :--- | :---: | :---: | :--- |
| `OpenFile` | Yes | Yes | path があり missing でなければ可。 |
| `OpenFolder` | Yes | Yes | path があり missing でなければ可。 |
| `OpenRepositoryBySha256` | Yes | Yes | chart / chart_info に有効な sha256 があれば可。 |
| `OpenPlaylistUrls` | Yes | Yes | playlist row に URL / URL diff があれば可。 |
| `RunResourceHealthCheck` | Yes | Yes | path があり missing でなければ可。 |
| `UseLr2Ir` | Yes | No | BMS かつ md5 または LR2BMSID がある場合。 |
| `UseScoreViewer` | Yes | No | BMS かつ md5 がある場合。 |
| `UpdateRanking` | Yes | No | BMS かつ md5 がある場合。 |
| `RunBmsEncodingCheck` | Yes | No | BMS かつ missing でない場合。 |
| `RunBmsEncodingFix` | Yes | No | BMS かつ missing でない場合。 |
| `RunZeroNoteCheck` | Yes | No | BMS かつ missing でない場合。 |
| `RenameInvalidExtension` | Yes | No | BMS かつ missing でない場合。 |
| `ConvertToAudio` | Yes | No | BMS かつ missing でない場合。 |
| `RepairInstalledLocation` | Yes | Yes | path があり missing でなく pending でない場合。 |
| `MoveInLibrary` | Yes | Yes | path があり missing でなく pending でない場合。 |
| `RemoveFromLibrary` | Yes | Yes | path があり missing でなく pending でない場合。 |
| `UpdateInstallDestination` | Yes | Yes | pending package row で、playlist row ではない場合。 |

`RepairInstalledLocation` は BMS / bmson の両方に付与される。UI handler は `ChartOperationTarget` を ViewModel に渡し、ViewModel が UI thread 上で repair 用 snapshot を作る。snapshot の `HasTargets` は `ChartFile` だけで判定し、context menu の表示可否確認だけでは bmson compatibility adapter を materialize しない。search / clear の実行時も、UI handler は background task に入る前に `MaterializeRepairEntries()` で snapshot の `RepairEntries` を確定させるが、`RepairEntries` は `PackageEntry` または `ChartFile` projection から作る `PackageChartEntry` の集合であり、bmson loose target の lazy compatibility provider は呼ばない。BMS は storage row、bmson は operation target 作成時点の `ChartFile` snapshot を修復 payload として扱う。model facade と install estimation service は `PackageChartEntry` を対象に search / clear を行う。一方、fix 実行は `RepairCharts` を model 層へ渡す。`RepairCharts` は基本的に snapshot の `ChartFile` をそのまま使うが、playlist 詳細 row などで snapshot の `ChartFile` が古い導入先状態を持つ場合に備え、現在の `RepairEntries` に install destination / metadata / suggestions があればそれを `ChartFile` に重ねる。model 層は `PackageChartEntry.FromChart(...)` と `ChartFile.InstallDestination` から移動対象を組み立てるため、fix の mutation 入口は chart-native である。bmson 修復候補は `ChartFile` に投影済みの state を見るため、background task が shared adapter cache を初期化しない。model 側の修復では、BMS は `FilePathChanges`、bmson は `BmsonSongPathChanges` として同じ `LibraryMutationDelta` に載せ、`BmsLibraryStateApplier.ReplaceBmsonSongPath(...)` が `bmson_song` の path / folder と DB row を更新する。

resource health と folder auto rename の UI handler も `ChartOperationTarget` を ViewModel に渡し、ViewModel が `ChartOperationTargetSnapshot` を作る。resource health の強制再チェックと warning ignore / unignore は ViewModel から `ChartFile` のまま model 層へ渡し、UI / ViewModel 側では bmson compatibility adapter を materialize しない。model 層では `BmsLibraryMaintenanceService` が BMS storage row と `bmson_song` を別入力として受け、resource health projection / ignore state は `ChartFile` から解決する。一方、folder auto rename は snapshot 内の `ChartFile` を使い、plan 生成では bmson compatibility adapter を materialize しない。UI handler 側に BMSFile adapter 選択 helper は残さない。installed package record clear は package identity を落とさないため、`ChartOperationTarget` をそのまま ViewModel に渡して `ChartPackage` に解決する。

`RunResourceHealthCheck` は capability と context menu policy の両方で bmson も対象にできる。bmson のみ選択時でも full scan menu は `RunResourceHealthCheck` capability を見て表示され、LR2IR / ranking / encoding / zero-note / audio convert などの BMS-only menu だけが後段 policy で非表示になる。

## Model-layer chart reference

### `LibraryChartRef`

model 層では `LibraryChartRef` が BMS / bmson 共通参照として使われる。

`LibraryChartRef` は次から作れる。

- `FromBmsFile(...)`: BMS storage owner 専用。`BMSFile` subtype から bmson を復元する互換分岐は production から削除済みである。
- `FromChartFile(...)`: `ChartFile` の storage owner を使う。BMS では `ChartFile.BmsFile`、bmson では `ChartFile.BmsonSong`、最後に path / hash fallback を使う。
- `LR2SongDBExtended.bmson_song`
- path / md5 / sha256

`LibraryChartRef.FromPath(...)` は path 必須の fallback 参照であり、live storage owner を必ず持つわけではない。

`LibraryChartRef.BmsFile` は `Kind=Bms` の storage owner 専用である。bmson は `FromBmsonSong(...)` または `FromChartFile(...)` で参照を作り、`BMSFile` 継承型 adapter から bmson owner を復元しない。

library chart 削除は `RemoveLibraryCharts(...)` が model 層入口で、BMS / bmson の両方を `LibraryChartRef` 経由で扱う。削除結果も `RemovedCharts` を正本にし、BMS / bmson の storage owner は caller 側で `Kind` に応じて分ける。旧 `RemoveBMSFiles(...)` / `RemoveChartFiles(IEnumerable<BMSFile>)` wrapper、未使用の single chart move wrapper、削除結果用の `RemovedFiles` adapter list、`LibraryChartRef.ToCompatibilityBmsFile()` は残していない。

ViewModel / UI 層の pending package 操作は `SearchInstallDestinationForPendingPackages` / `SearchInstallDestinationForPendingCharts`, `SearchMergeDestinationForPendingPackages` / `SearchMergeDestinationForPendingCharts`, `ForceInstallPendingPackages` / `ForceInstallPendingCharts`, `ManualInstallPendingPackages` / `ManualInstallPendingCharts`, `RemovePendingPackages` / `RemovePendingPackagesAll`, `RemovePendingCharts`, `ClearInstallDestinationForPendingPackages` / `ClearInstallDestinationForPendingCharts`, `SetPendingInstallDestination`, `GetPendingPackagesContainingOnlyInstalledCharts`, `DeletePendingPackageSources` を入口にする。これらは package 内 chart を扱う操作であり、BMS 専用 API ではない。UI から選択 chart を渡す `*PendingCharts` は `ChartOperationTarget` payload に寄せ、package 所属 target は `PackageChartEntry` identity で package operation へ展開する。search / merge / clear の batch 操作は UI thread 上で `PendingInstallDestinationTargetSnapshot` を作り、`HasTargets` は `ChartFile` だけで判定する。package target は source `PackageChartEntry` を保持し、background task 側では snapshot の package target identity を現行 `ChartPackagesPending` へ再解決する。package 外 target は snapshot の `LooseEntries` として `ChartOperationTarget.ToPackageChartEntry()` から確定するが、bmson では lazy compatibility provider を呼ばず `ChartFile` projection から一時 entry を作る。導入先セル手動編集は UI thread 上で `PendingInstallDestinationEditTargetSnapshot` を作り、snapshot 作成だけでなく `GetOrCreateChartEntry()` 時も loose bmson compatibility adapter を materialize しない。`LibraryChartRow.instl_dst` は表示用 getter のみで、セル編集の書き戻しには使わない。`PackageEntry` があれば adapterless bmson も package-level API に降り、compatibility adapter を作らず entry へ書き戻す。package 外 target の mutable install-destination state は operation target 作成時点の `ChartFile` snapshot にある state だけを使い、後段処理のために shared adapter を作って書き戻す旧挙動は残さない。

pending chart 削除の UI 経路は `ChartOperationTarget.Chart` を `BMSLibrary.RemovePendingCharts(IEnumerable<ChartFile>)` へ渡し、model 層では `BmsLibraryPackageInstallService.DeletePendingCharts(...)` が `ChartFile.Path` を deletion target に正規化する。BMS / adapterless bmson とも削除成功時は `PendingFileDeletionResult.ChartPathsToRemove` に載せ、pending package mutation も path で entry を落とす。削除のためだけに `PackageChartEntry.GetOrCreateCompatibilityAdapter()` を呼ばず、旧 `BMSFile` payload 入口や BMSFile 削除結果 list は残していない。pending chart deletion の正本は `ChartFile` payload である。

pending install destination クリアの UI 経路も、pending section では選択 `ChartOperationTarget` を `PendingInstallDestinationTargetSnapshot` に変換してから `MainWindowViewModel.ClearInstallDestinationForPendingCharts(...)` へ渡す。package row から作られた `LibraryChartRow` / `ChartOperationTarget` は source `PackageChartEntry` を保持し、package に属する chart は entry identity で `ChartPackage` を解決して `PackageChartEntry.ClearInstallDestination()` を呼ぶため、adapterless bmson package entry のクリアでは compatibility adapter を materialize しない。package 外に残った target は fallback として snapshot の `LooseEntries` に確定し、`ChartOperationTarget.ToPackageChartEntry()` で BMS storage owner だけを adapter entry 化し、bmson は `ChartFile` projection から entry 化して ViewModel 側で model の clear API へ渡す。`ToPackageChartEntry()` 自体は package entry があればそれを最優先するが、pending batch snapshot では package target を先に分離する。

pending install destination search / merge search の UI 経路も、pending section では選択 `ChartOperationTarget` を `PendingInstallDestinationTargetSnapshot` に変換してから ViewModel に渡す。package row 由来 target は `PackageChartEntry` identity で package に展開し、package-level estimation / merge search を走らせる。package 外に残った target は background task 前に snapshot の `LooseEntries` として確定し、ViewModel 側で model 層へ渡す。この fallback は loose chart として処理し、再び path で pending package に吸い込まない。model の manual estimate / merge / clear 入口も loose chart については `PackageChartEntry` を受け、旧 `BMSFile` batch overload は残さない。adapterless bmson package entry の search / merge のためだけに compatibility adapter を materialize しない。

pending package に対する force install / manual install / selected package record removal も、UI では `ChartOperationTarget` を ViewModel に渡す。ViewModel は `PackageChartEntry` identity で `ChartPackage` を抽出し、既存の package-level API (`ForceInstallPendingPackages`, `ManualInstallPendingPackages`, `RemovePendingPackages(IEnumerable<ChartPackage>)`) へ流す。したがって package row 選択を package 操作へ変換するためだけには `BMSFile` compatibility adapter を materialize しない。

pending package に属する target は `PendingInstallDestinationSelectionResult.TargetEntries` に保持し、validation result には旧 `TargetFiles` list を残さない。導入先セルの手動編集も UI では `ChartOperationTarget` を ViewModel に渡し、package entry target は `BMSLibrary.SetPendingInstallDestination(PackageChartEntry, ...)` へ降りる。`BMSLibrary.SetPendingInstallDestination(...)` は `TargetEntries` へ導入先を書き戻すため、invalid destination warning で終わる場合だけでなく、manual destination を adapterless bmson package に設定する場合も validation / writeback だけでは compatibility adapter を作らない。低信頼 install estimation の候補選択時は、preserve 判定も `PackageChartEntry` の low-confidence warning と `ChartFile.InstallDestinationSuggestions` を見る。searching flag はまだ `BMSFile` adapter 側の UI state だが、adapterless entry では searching flag を持たずに package-level estimation を実行する。pending package 表示 row は `PackageChartEntry` provider から現在の `ChartFile` projection を読み直すため、row 作成後に entry の install destination / suggestions / warnings が更新されても古い snapshot を固定しない。

merge 先探索や installed-only package destination resolve は package 内 chart を `PackageChartEntry` として列挙し、BMS / bmson 共通の installed hash index で既所持 directory を採点する。installed hash index 自体は md5 / sha256 の両方を登録するが、chart 側の lookup は `ChartFile.PrimaryLookupHash` により md5 優先、sha256 fallback の primary key を使う。

`SearchMergeDestinationForPendingCharts(...)` は、選択 chart が pending package に属する場合、選択 chart 単体ではなく所属 package に展開して package-level merge を走らせる。mixed package の既所持先が複数 directory に分かれている場合でも、hash 一致数が単独最多の directory があればそれを package 全体の merge 先として採用する。最多 directory が同点の場合や hash 一致がない場合は、hash 由来の自動決定をせず、`MergeCandidateOnly` の resource 評価へ fallback する。

direct install / drop install の ViewModel 入口は `InstallChartPackages(...)` で、model 層の pending package install 入口は `InstallChartPackagesAuto`, `ForceInstallPendingPackages`, `InstallPendingPackagesToEstimatedDestinations` を使う。旧 `InstallBMSFilesAuto` / `InstallChartPackagesForce` / `InstallChartPackagesToEstimatedDir` / 単数 wrapper は残さない。

newly installed tree に表示される installed package history/list のクリアは `RemoveInstalledPackageRecords` / `RemoveInstalledPackageRecordsAll` を入口にする。これは chart file 自体の削除ではなく、installed package record を list から消す操作である。

library folder operation は root/search-directory の public / user-facing 名に `BMSDirectory` が残るが、chart 行に対する folder 操作は `RenameChartFolder(...)` / `MergeChartDirectory(...)` / `AutoRenameChartFolders(...)` / `AutoRenameAllChartFolders(...)` に寄せる。`BuildFolderMoveDelta(...)` は `BMSFiles` と `BmsonSongs` の両方を受け、`FilePathChanges` と `BmsonSongPathChanges` を同じ mutation delta に載せる。folder move / merge path では BMS / bmson path と installed package / pending package の install destination も合わせて更新対象になる。root folder move の UI 経路は `MoveLibraryCharts(...)` から `MoveLibraryRootFolder(...)` に入り、`ChartOperationTarget` / `LibraryChartRef` を通して BMS / bmson chart を扱う。旧 `MoveBMSRootFolder(...)` wrapper は production 参照がなく、テストだけの旧名互換 API になっていたため削除済みである。`BMSDirectory` 系 UI / settings vocabulary は BMSFile-based root-folder / LR2 search-root 境界として残っている。

## Package / pending install

### `ChartPackage`

`ChartPackage` は package 内 chart の discovery container であり、BMS / bmson 混在 package を同じ単位で扱う。

pending / installed package record の永続正本は `install` table の row であり、実質的には source path と delete_parent などの package record metadata を保存する。package 内 chart list 自体は永続化されず、DB restore 後は `ChartPackage.path` から lazy rediscovery される。

主な現行仕様:

- `ChartPackage.ChartEntries` は package 内 chart discovery の読み取り primary API になりつつあり、`PackageChartEntry` / `ChartFile` を返す。
- `ChartPackage.GetChartAdapters()` は production 参照がなくなったため削除済みである。package 内 chart をまとめて読む入口は `ChartEntries` とし、BMS storage owner が必要な操作は対象 `PackageChartEntry.Chart.BmsFile` から明示的に取得する。
- 明示的に chart adapter list を渡された package でも private `PackageChartEntry` list を保持し、読み取りは `ChartEntries` から行う。
- それ以外では `PackageChartDiscoverySnapshot` を lazy build し、chart file path から BMS は `BMSFile`、bmson は `bmson_song` / `ChartFile` entry を作る。
- 旧 `PendingCharts` view は production 参照がなく、adapterless entry を表示確認だけで materialize し得るため削除済みである。pending package 内 chart は `ChartEntries` を正本として読み、mutation / warning 書き戻しが必要な時は対象 entry の `ChartFile` / storage owner へ直接降りる。

production code の `ChartPackage` 経由の chart-all 参照は、読み取り系と install estimation snapshot 内部では `ChartEntries` に寄せている。BMS-only mutation target list でも package 全体の adapter snapshot は公開せず、対象 entry の `Chart.BmsFile` を読む。公開側の pending install orchestration でも導入先推定 request / batch state は entry を正本にし、searching flag は BMS storage owner がある対象にだけ反映する。warning / install destination 書き戻しも可能な範囲で `PackageChartEntry` に寄せ、BMS row mutation が必要な経路だけ対象 entry の `Chart.BmsFile` へ降りる。旧 `BMSFiles` alias は production 参照がなくなった段階で削除済みであり、package 内 chart の正本は明示 package / path discovery ともに `PackageChartEntry` に寄せている。`ChartPackage` 内の private `ChartFiles` property と `PackageChartDiscoverySnapshot.ChartFiles` は削除済みである。

`PackageChartDiscoverySnapshot` は `PackageChartEntry` を内部正本として保持する。`PackageChartEntry` は `ChartFile` を必ず持ち、BMS storage owner を読む production 経路は `PackageChartEntry.Chart.BmsFile` を読む。旧 `GetExistingBmsFormatAdapter()` は production surface から削除済みであり、BMS row に対する mutable API / warning 更新も `Chart.BmsFile` へ降りる。path discovery で見つけた bmson は `ChartFileProjection.FromBmsonSong(...)` による adapterless entry として保持し、package entry の production API は bmson compatibility adapter を lazy materialize しない。`ReplaceChartEntries(...)` は entry list を正本として置換するため、adapterless bmson entry を adapter 化せずに残せる。`ChartEntries` getter は常に entry 正本を返し、別の compatibility adapter list cache は持たない。旧 `BmsFiles` alias は削除済みであり、snapshot の読み取り経路は `PackageChartEntry` へ移し始めている。
`PackageChartEntry` は pending package 用の warning state と install destination state を持てる。BMS entry は `ChartFile.BmsFile` を唯一の BMS storage owner として読み、`BMSFile` の warning / install destination fields へ委譲する。bmson entry では entry 内の pending snapshot を `ChartFile` projection に重ねる。production の package entry API は、後段処理のためだけに adapterless bmson を `PendingChartEntry` へ変換しない。これにより、installed / single-file / nested-chart warning の付与、起動時 pending warning 初期化、手動導入先設定、導入先推定の低信頼 warning / suggestions 書き戻しは、それだけでは adapterless bmson を materialize しない。pending / install resource health warning の判定は `BmsLibraryPackageInstallService.BuildPendingResourceHealthWarnings(PackageChartEntry)` に集約し、BMS entry も callback で `BMSFile.Warnings` を中間状態として読む経路には戻さない。

`ChartPackage` は `RemoveChartEntries(...)` / `ApplySingleFileInstallDestination(...)` / `ApplyDirectoryInstallDestination(...)` / `ReplaceChartEntries(...)` を持ち、operation / mutation 側が adapter list を直接状態更新する箇所を増やさないための所有者境界になり始めている。TreeView header など表示側は `DisplayTitle` を使い、`DisplayTitle` は `ChartEntries` / `ChartFile` から作るため bmson package header 表示だけでは compatibility adapter を materialize しない。XAML から package 内 chart list に直接 binding しない。count / empty 判定は `ChartEntries.Count` を直接見るため、compatibility adapter materialize を要求しない。選択 chart が package に属するかの判定は caller 側で既存 adapter reference と `ChartFile.Path` を見る。`RemoveChartEntries(...)` は削除対象を `PackageChartEntry` predicate で判定し、path 削除でも adapterless entry を materialize しない。install destination apply helpers は `PackageChartEntry` を受け取り、実ファイル移動済みの対象 entry だけ adapter writeback する。install destination clear は package wrapper を経由せず対象 `PackageChartEntry.ClearInstallDestination()` を呼ぶ。`ReplaceChartEntries(...)` は chart entry list の明示置換として扱うため、adapterless entry を保持する。production / test ともに未使用だった predicate-based `RemoveChartAdapters(...)`、path-based `RemoveChartAdaptersByPath(...)`、adapter-list replacement の `ReplaceChartAdapters(...)`、membership helper の `ContainsChartAdapter(...)` は削除済みである。count / empty だけの薄い wrapper だった `GetChartAdapterCount()` / `IsChartAdapterEmpty()` と、install destination clear だけの薄い wrapper だった `ClearChartAdapterInstallDestinations()` も削除済みである。
`ChartPackage(BMSFile)` / `ChartPackage(IEnumerable<BMSFile>)` constructor は production API から削除済みである。package を明示 chart list 付きで構築する場合は `ChartPackage.FromChartEntries(...)` を使い、BMS storage owner から package entry に落とす必要がある境界では caller が `PackageChartEntry.FromBmsFile(...)` を明示する。tests 側だけで必要な fixture construction は `ChartPackageTestExtensions.CreatePackage(...)` に閉じ込め、production に BMSFile-list constructor を互換 API として残さない。
auto install discovery で directory scan 済みの package を明示 chart list 付きで作る時は `ChartPackage.FromChartEntries(...)` を使う。recursive metadata discovery でも BMS は `BMSFile.CreateBMSFileFromFile(...)`、bmson は `PackageChartEntry.FromPath(...)` により entry として保持し、resource 判定は `ChartResourceSnapshot` を読む。`SearchChartPackagesRecursivelyWithMetadata(...)` / auto install grouping で既知 chart list を package 化する境界も `PackageChartEntry` を渡し、package construction の内部 API では `IEnumerable<BMSFile>` を要求しない。これにより、scan 結果の adapterless bmson entry を `BMSFile` list に戻さず package 正本として保持できる。

`BMSLibrary` / package install service の pending package 読み取り経路は `ChartEntries` / `ChartFile` へ移行中である。導入先推定 snapshot、advanced cleanup の「全 chart が installed 済みか」判定、追加 bmson 抽出、resource-only merge の hash / path 読み取りは package entry を読む。pending package の導入先推定で package 内 chart を already-installed / missing に分ける時も、判定は `PackageChartEntry.Chart` の `PrimaryLookupHash` で行い、manual single-package estimation / manual batch estimation / background batch preparation の partition 時点では adapterless bmson を materialize しない。pending estimate request / batch state は `PackageEntries` / `MissingEntries` / `AlreadyInstalledEntries` を持ち、`BMSFile` adapter list を状態として保持しない。snapshot と scoring の入力、および installed directory index からの混在 package 導入先解決は `MissingEntries`、導入先 / low-confidence warning / suggestions の writeback も package target では `PackageChartEntry` を使う。`SEARCHING` 表示が必要な場合も entry から既存 adapter または BMS storage owner だけを参照し、adapterless bmson を materialize しない。estimated install batch plan の installed / duplicate / install target 分類も `PackageChartEntry.Chart.PrimaryLookupHash` と `ChartFile.InstallDestination` を読む。pending package の full-selection 判定、safe cleanup 用の hash snapshot も entry / chart path / primary hash を読む。advanced resource overwrite の一時的な path-only destination 書き込み / 復元は `CaptureInstallDestinationState(...)` / `SetInstallDestinationPathOnly(...)` / `RestoreInstallDestinationState(...)` を使い、warning / suggestions を壊さず adapterless bmson entry を materialize しない。manual estimate の package membership 判定は duplicate hash を package containment と誤認しないよう、同一 entry / storage row reference / `PackageChartEntry.Chart.Path` の一致だけを見る。root folder move / merge / deleted-folder cleanup の install destination 判定も `ChartFile.InstallDestination` を先に読む。pending regroup 成功ログの file count / install destination metadata 判定も `ChartEntries` を読む。playlist reference 同期、install result の `AddedBmsFiles` 抽出、folder merge 後の song upsert target のように既存 BMS storage owner が必要な経路は `PackageChartEntry.Chart.BmsFile` を読むため、adapterless bmson のために新規 materialize しない。package 内 target の一致判定は adapterless bmson を materialize しないよう、`ChartFile` identity に集約し、hash fallback は使わない。merge destination 探索は、installed directory index で導入先が解決できる成功パスでは `ChartEntries` に導入先 metadata を書き戻し、adapterless bmson entry を materialize しない。merge 実行後の song / bmson_song upsert と maintenance target も `Repackage.ChartEntries` から storage owner を分けるため、移動済み adapterless bmson entry を materialize しない。resource estimation fallback が必要な場合も package-level install estimation の low-confidence state は entry に書き戻す。install 実行時の移動対象 chart 選別と auto naming の chart 入力も `ChartEntries` / `ChartFile.Path` で対象 entry を絞ってから BMS storage owner を取得し、移動成功後の package chart set は移動した `PackageChartEntry` で置き換える。install execution result は `AddedEntries` / `AddedCharts` を正本にし、BMS storage row は `AddedBmsFiles`、bmson storage row は `AddedBmsonSongs` として分ける。bmson は install result / bmson_song upsert / installed package registration / resource lookup cache update に含めるだけでは adapter 化しない。通常 install では inline chart_info persist が bmson_song upsert も担い、estimated/deferred install では deferred state apply 用に先に bmson_song を upsert する。移動対象から外れた entry は install result / installed package registration へ含めず、adapter 化もしない。ここで扱う `BMSFile` は LR2 song storage row または package chart adapter として用途別に分け、list identity や list mutation は `ChartPackage` 側に閉じ込める。estimated/deferred install の state apply context も BMS は `AddedBmsFiles`、bmson は `AddedBmsonSongs` を保持し、bmson adapter list や混合 `AddedFiles` list を状態として持たない。
library directory merge の準備結果である `LibraryMergeResult` は、source chart を単一の `BMSFile` list として保持しない。BMS storage owner は `SourceBmsFiles`、bmson storage owner は `SourceBmsonSongs` に分け、`Repackage.ChartEntries` も storage owner から組み立てる。したがって installed bmson を merge source として扱うだけでは `PendingChartEntry` compatibility adapter を作らず、existing-hash snapshot も `ChartFile.PrimaryLookupHash` を読む。folder move / merge に伴う pending package 内 chart の install destination rewrite は `LibraryInstallDestinationChange.Entry` で `PackageChartEntry` を直接更新するため、adapterless bmson entry は導入先が移動元配下でも rewrite のためだけに materialize しない。deleted-folder cleanup は `PackageChartEntry.ClearInstallDestination()` で metadata / suggestions / install-estimation warning も含めて clear し、adapterless bmson entry を materialize しない。
BMS 系 chart 専用の保留 snapshot は、`ChartEntries` の `ChartFile.Kind` と拡張子で BMS-format chart だけを選び、既存 adapter または `ChartFile.BmsFile` を優先して返す。これにより、zero-note / invalid-extension 対象外の adapterless bmson は snapshot 取得だけでは materialize されず、BMS storage owner がある entry も不要な compatibility adapter を作らない。
nested chart warning も `ChartEntries` の `ChartFile.Path` で入れ子判定してから対象 entry の adapter だけ materialize する。package 直下の adapterless bmson は警告付与対象外なので adapter 化しない。
auto install workflow の pending / auto-install 分類では、resource reference count は `PackageChartEntry.ResourceSnapshot` を先に読み、SingleBmsFile / SingleBmsonFile / resource health warning を書き戻す時だけ対象 entry の adapter を materialize する。installed 判定は `ChartFile` callback で行い、AlreadyInstalled warning を書き戻す entry だけ adapter を materialize する。
pending package から選択 chart を削る mutation delta も、削除対象判定は `PackageChartEntry` の既存 adapter reference / `ChartFile.Path` で行う。残す entry は `ReplaceChartEntries(...)` で package へ書き戻すため、残存する adapterless bmson も削除される adapterless bmson も、この再構成だけでは adapter 化しない。
force install の normal install 上書き確認は、package に `ChartFile.InstallDestination` を持つ entry があるかで判定する。確認ダイアログを出すかどうかだけなら adapter mutation target は不要なので、adapterless bmson entry は確認判定だけでは materialize しない。install 成功後の `instl_dst` clear はまだ adapter writeback 境界である。
estimated install の resource-only merge 後に installed package history/list へ追加する display package は、既存 library row から `PackageChartEntry` を組み立てる。BMS は storage owner `BMSFile` から entry を作るが、bmson は `bmson_song` から `ChartFile` entry を作るため、installed display package の作成だけでは bmson compatibility adapter を materialize しない。
install table load result は pending warning 初期化の対象 adapter list を公開しない。warning count と package / stale row の結果だけを返し、pending warning 初期化は `PackageChartEntry` を走査して entry の warning state へ直接書き戻す。起動時 pending package の installed 判定は `ChartFile` callback で行い、AlreadyInstalled / SingleFile / ResourceHealth warning も `PackageChartEntry` の warning state として保持する。ResourceHealth warning 判定は resource reference を持つ entry に限り、warning 初期化のためだけに adapterless bmson entry を materialize しない。
pending package tree の「導入先を開く」は、package 内 chart の `ChartFile.InstallDestination` と `PrimaryLookupHash` を読む。これは explorer を開く先を解決するだけの UI 読み取りなので、adapterless bmson entry を `BMSFile` に materialize しない。pending package の install destination clear は `PackageChartEntry` の install destination state を clear し、adapterless bmson entry も entry 内 state と `ChartFile` projection を更新するため、clear だけでは compatibility adapter を materialize しない。
playlist reference の pending package / installed package 反映は `ChartFile.Md5` / `Sha256` で一致判定する。BMS entry には従来どおり `BMSFile.RefTables` を書き戻すが、bmson entry は一致しても `PendingChartEntry` adapter を作らず、表示は `PlaylistReferenceIndex` と chart identity から解決する。参照テーブルと一致しない bmson entry だけでなく、一致する bmson entry も playlist reference refresh や install 後 reference 反映だけでは materialize しない。reload / replace / remove / synchronize で旧 table 分を除去する場合も、pending target は `PackageChartEntry` / `ChartFile` で照合し、BMS storage owner だけを `RefTables` mutation 対象にする。bmson の表示は index 更新へ寄せる。
pending / newly installed package の一覧表示 row は `PackageChartEntry` から作る。virtual package subset の `PackageChartSourceSnapshot` は `PackageChartEntry.Chart` をそのまま `ChartFile` list として保持し、`ChartListSourceRow.BuildStandardLibraryRows(IEnumerable<ChartFile>)` へ渡す。BMS / bmson の storage row 分割は virtual package source snapshot には持たせないため、package view の sort / keyword filter に入るだけでは adapterless bmson entry を materialize しない。
pending package install / resource overwrite 前の再生停止対象は、package 内 `PackageChartEntry` の既存 BMS compatibility adapter だけを snapshot する。これは BMS 再生中の file lock / process を閉じるための UI 側ガードであり、bmson や adapterless entry を再生停止対象にするためだけに materialize しない。
split した pending package の regroup 判定は `PackageChartEntry.Chart` の path / primary hash / install destination を読む。全 entry が同じ expected destination に解決できることを確認した後、regrouped package は `PackageChartEntry` list として書き戻す。regroup 後の warning 再初期化も起動時 pending warning 初期化と同じく `PackageChartEntry` を走査し、BMS では resource snapshot 更新のために必要な範囲で `SetHealthStatus()` を通し、warning list 自体は `BuildPendingResourceHealthWarnings(PackageChartEntry)` で entry へ書き戻す。bmson も AlreadyInstalled / SingleBmsonFile / ResourceHealth / nested chart warning を entry state として保持し、warning 再初期化のためだけに adapterless bmson entry を materialize しない。
pending package フォルダー削除の集計と mutation delta は `PackageChartEntry.Chart.Path` を使う。削除成功時も削除済み chart path を `PendingFileDeletionResult.ChartPathsToRemove` に返し、BMS-format 専用の invalid extension / zero-note rename も pending package 更新へ渡す payload は `ChartPathsToRemove` に正規化する。`BuildPendingPackageMutationDelta(...)` は path だけで対象 entry を落とすため、adapterless bmson entry は成功 / 失敗どちらでも folder 削除後処理だけでは materialize されない。

package / pending install 層の正本は `PackageChartEntry` / `ChartFile` である。`BMSFile` が「install 対象 chart adapter」を表す production 経路は削除済みで、BMS storage owner としてだけ扱う。

### `PackageInstallEstimationSnapshot`

導入先推定 snapshot builder の primary input は `PackageChartEntry` である。`ChartPackage.GetOrBuildInstallEstimationSnapshotFromEntries(...)` / `BuildInstallEstimationSnapshotFromEntries(...)` と `PackageInstallEstimationSnapshotBuilder.Build(...)` / `BuildForLooseEntries(...)` は entry list を直接受け取り、`BMSFile` adapter list 入口は持たない。これにより、snapshot 化だけのために adapter list へ戻す経路を production に残さない。

主なフィールド:

- `RepresentativeChart: ChartFile`
- `DefinedResources`
- `TargetMetadataProfile`
- `ChartCount`
- source / bundled resources
- source surface snapshot / scan metrics / cache hit
- batch source surface hit と scan backend 情報

target chart list は `PackageChartEntry` として snapshot builder に渡され、読み取り専用で参照する代表譜面を `RepresentativeChart` として `ChartFile` 化する。pending package の通常推定では `MissingEntries` を直接渡すため、missing target を snapshot 化するだけなら追加の adapter 再解決は不要である。package-level target が空の場合は package の `ChartEntries` 全体を使うため、adapterless bmson entry も snapshot から落ちない。package-level pending estimation の request / batch state は `PackageEntries` / `MissingEntries` / `AlreadyInstalledEntries` だけを保持し、mutation writeback も entry を入口にする。adapter list は install / move 実行のように実ファイル操作が adapter を要求する境界で取得する。`DefinedResources` は `PackageChartEntry.Chart` から作るため、bmson pending chart では `PendingChartEntry` adapter の component cache ではなく `bmson_song` の resource refs を使う。metadata profile は `ChartFile` projection の title / artist / path から作る。bmson pending chart では `Kind=Bmson` と `BmsonSong` owner を保持し、BMS 専用 storage owner とは分ける。複数 package 推定では、`PackageInstallSurfaceSnapshot` や batch source surface を共有し、同じ source tree の scan / resource surface を再利用できる。

loose chart 推定の model 入口は `PackageChartEntry` を受け取り、snapshot も entry から組み立てる。installed hash での除外や既に install destination が入っている対象の skip 判定は `ChartFile.PrimaryLookupHash` / `ChartFile.InstallDestination` を読む。UI から legacy compatibility adapter が必要な loose target を渡す場合も、ViewModel が background task 前に adapter を確定し、`PackageChartEntry` に包んでから model 層へ渡す。

### installed directory index

installed directory index は BMS と bmson の両方を扱う。

`BuildInstalledHashToDirectoryMap(...)` は、`IEnumerable<ChartFile>` を受け取り、md5 / sha256 から installed directory を作る。BMS / bmson の storage row 分割は caller 側の installed chart snapshot 作成時だけに閉じ、install estimation service の directory index surface は chart-common にする。

この領域では「installed chart」という考え方に寄せ、service surface の入力型は `ChartFile` に統一する。map は md5 / sha256 の両方を登録する一方、個別 chart の候補判定や package resolve では `ChartFile.PrimaryLookupHash` を使うため、「常に両 hash で union lookup する」仕様ではない。installed-only package destination / package-level installed directory scoring / pending destination validation は package 内 chart を `PackageChartEntry` として列挙し、BMS-only mutation が必要な境界だけ BMS storage owner を返す。installed-only resource overwrite の skip 診断で原因 chart を探す helper は `ChartFile` を返すため、adapterless entry でも path / primary hash をログへ出せる。

## Playlist

### Entry identity

playlist entry は md5 と sha256 の両方を持てる。

現行方針:

- BMS entry は md5 identity が基本。
- bmson entry は sha256-only identity が基本。
- `BMSTableEntry(BMSFile)` は BMS storage row 専用で、md5 identity を使う。
- bmson entry は `BMSTableEntry(ChartFile)` を入口にし、`ChartFile.Kind == Bmson` の場合に `MarkAsBmsonPlaylistIdentity(...)` を呼び、`md5 = null`, `sha256 = preferredSha256`, `Org_md5 = []` にする。

### Playlist detail resolution

playlist detail は `PlaylistDetailSourceRow` / `PlaylistDetailRow` で表示される。

主な状態:

- `RealFile`: 所持 BMS へ解決できた場合の `BMSFile`
- `ResolvedBmson`: 所持 bmson へ解決できた場合の `bmson_song`
- `IsOwned`: `RealFile` または `ResolvedBmson` が path を持つ場合 true
- `Chart`: playlist detail row の chart read model

`PlaylistDetailSourceRow` は source snapshot 構築時に `Chart` を作る。`RealFile` があれば BMS、`ResolvedBmson` があれば bmson、どちらもなければ playlist missing の metadata chart として扱う。`PlaylistDetailRow` は source row の `Chart` を引き継ぎ、missing row の手動 level 編集や source row の entry chart_info patch 時には `Chart` を作り直す。これにより、view row からも source row と同じ chart_info / identity snapshot を `GridRowResolver` に渡せる。

`GridRowResolver` は playlist row から `ChartOperationTarget` を作る際、row の `Chart.Kind` と `RealFile` / `ResolvedBmson` を組み合わせて source scope と capability を決める。owned bmson playlist row では `ChartFile.BmsonSong` を storage owner として保持し、operation target 作成のためだけには compatibility adapter を作らない。

playlist detail row は view row materialization だけでは bmson compatibility adapter を作らない。`PlaylistDetailSourceRow` / `PlaylistDetailRow` は表示用 `ChartFile` を bmson storage row と `ChartFileTransientState` だけから作り、`CompatibilityBmsFile` surface を持たない。ViewModel が渡す transient state provider は repair / warning 表示 state を `ChartFileTransientState` として返し、存在しない adapterless bmson row は表示するだけでは `PendingChartEntry` へ変換しない。

playlist detail の `RefTablesSymbols` / `RefTablesNames` は source snapshot 構築時に確定する。BMS row では `RealFile.RefTables`、bmson row や missing row では `PlaylistReferenceIndex` の md5 / sha256 lookup 結果を使う。これにより表示列と `playlist:` / `ref:` / `table:` keyword search が同じ参照情報を読む。

playlist detail 表示時の `ChartRowsView` 実体は `PlaylistDetailVirtualView` である。`PlaylistDetailSourceRow` を全件 source として保持し、可視 index だけ `PlaylistDetailRow` へ遅延 materialize する。playlist detail 中は `UseAsyncChartRowsViewBinding` を false に切り替え、通常一覧側の async binding policy と分けている。

### Playlist への追加

`MainWindowViewModel.AddChartRowsToFolderBMSTable(...)` は、playlist table 概念として `BMSTable` 名を残しつつ、追加元の一覧 row は Chart として解決する。

通常 folder への追加では、row から `ResolvePlaylistDropChart(...)` で `ChartFile` を解決し、`BMSTableEntry(ChartFile)` で playlist entry を作る。bmson はこの経路で `PendingChartEntry` adapter を作らず、`ChartFile.Kind == Bmson` と `ChartFile.BmsonSong` から sha256 identity の playlist entry になる。

- BMS row は実体 `BMSFile` を使う。
- bmson library row は `ChartFile` / `BmsonSong` の identity から playlist entry を作り、playlist 追加のためだけには `PendingChartEntry` adapter を作らない。
- playlist row は通常 folder 追加では `BMSTableEntry.Duplicate()` を優先し、既存 playlist metadata を保つ。

folder table root への追加では、BMS は従来の同一ディレクトリ md5 group を使う。所持 playlist row は BMS / bmson とも `ResolvePlaylistDropChart(...)` で `ChartFile` へ解決され、root folder 自動振り分けでは新しい `BMSTableEntry(chart)` を作る。missing row など chart を解決できない playlist row は `BMSTableEntry.Duplicate()` で既存 metadata を保つ。bmson は sha256 identity を保ち、`Org_md5` は空にする。

## File read pipeline

chart file bytes の読み取りは `ChartFileSnapshot` / `ChartFileContentReader` で共通化されている。

この snapshot は full path、bytes、length、lastWriteTimeUtc、md5、sha256 を持つ parser / inline chart_info / maintenance 用の短命な読み取り結果であり、長期に保持する chart model ではない。`ChartFile` という名前に近いが、責務は「ファイル内容の snapshot」であり、「アプリ内の譜面実体」ではない。

`ChartFile` は Models 配下の domain/read model だが、既存 `ChartFileSnapshot` と責務が混ざらないようにする。

## Maintenance / resource health / chart_info

### Resource health

resource health は BMS / bmson 共通の表示概念である。

現行では次の形で扱われる。

- BMS は `BMSFile.maintenanceInfo` を持つ。
- bmson は `bmson_song.MaintenanceInfo` を持つ。
- `LibraryChartRow` / `ChartListSourceRow` は BMSFile と bmson の両方から health 値を表示できる。
- 旧 `PendingChartEntry` は bmson pending adapter として maintenance 情報を持てたが、production では bmson maintenance は `bmson_song.MaintenanceInfo` / `ChartResourceSnapshot` を正本にする。
- `BmsLibraryMaintenanceService.UpdateMaintenanceInfo(...)` は、BMS row と `bmson_song` を別入力として受ける。BMS は既存の `BMSFile` maintenance pipeline を使い、bmson は `bmson_song` と `ChartResourceSnapshot` / `ChartFileProjection` から adapterless に resource health を計算し、`maintenance` row だけを upsert する。
- 起動中 file diff の inline bmson maintenance も `PendingChartEntry` shim を作らず、parser 済み `bmson_song` へ `MaintenanceInfo` を直接 attach する。

ただし encoding check / fix / zero-note check は capability 上 BMS 専用であり、bmson に適用しない。

### chart_info

`chart_info` は BMS / bmson 共通 metadata として扱う。

`ChartInfoBuildService` / inline chart_info pipeline は BMS と bmson の owner を別入力で受け、最終的に md5 / sha256 で chart_info を適用する。

通常一覧の chart_info 表示や sort key は `LibraryChartRow` / `ChartListSourceRow` に materialize された `ChartInfo` を使う。

### 通常一覧 sort

通常一覧の sort 正本は `LibraryChartRowSortEngine` である。

`BMSFileSortEngine` は旧 `BMSFile` 互換確認用の production helper として残っていたが、通常一覧の実行経路から外れ、参照がテストだけになったため削除済みである。
互換・性能確認は `BmsSortCompatibilityTests` の test-local legacy baseline と `LibraryChartRowSortEngine` の比較で行う。

## UI binding / naming boundary

通常一覧の表示実体は `LibraryChartRow` へ寄っており、MainWindow の主要 binding 名も chart row 名へ移行済みである。

代表例:

- `ChartRowsView`
- `SelectedIndexChartRowsView`
- `ColumnsSettingsChartRowsView`
- `UseAsyncChartRowsViewBinding`

内部の一覧再構築は `RefreshChartRowsView(...)` / `SetChartRowsView(...)` / `ChartRowsFolderView` など chart row 名の helper に寄せている。

このため、現在の仕様では通常一覧 / playlist detail の表示 binding は「BMSFile collection」ではなく「BMS / bmson 共通 chart row view」として扱う。

一方、View の control 名、menu item 名、event handler 名、ログ名にはまだ BMS 名が残る。内部 symbol / log 名は互換維持の対象ではないため、ChartFile 化の妨げになる場合は semantic rename で整理する。ただし、ユーザー設定値名と UI 文言は別扱いにする。

設定値名は既存ユーザー設定の永続 key であり、`DirPath_BMS` / `Standalone_BMSDirectories` / `IsWriteLockHeldInitializeBMSFiles` / `ShowRecommUpdatedMsg` などは、抽象化作業だけを理由には rename しない。変更する場合は設定移行を伴う独立作業として扱う。

UI 文言と翻訳 resource は、機能自体がユーザー目線で変わっていない限り rename しない。内部で chart 共通化しても、BMS というユーザー向け概念が BMS + bmson を含む場面では、既存文言を維持する。

## 現在の抽象化済み領域

次の領域は、すでに BMS / bmson を chart として扱う入口がある。

- 通常一覧 row: `LibraryChartRow`
- 仮想 filter / sort / search source: `ChartListSourceRow`
- UI 選択解決: `GridRowResolver.TryGetChartFile(...)`
- UI operation: `ChartOperationTarget` + `ChartOperationCapabilities`
- UI operation target 解決: `GridRowResolver.TryGetChartOperationTarget(...)`
- context menu / command selection helper: `GetSelectedChartTargets(...)`
- resource health / folder auto rename の chart-domain 境界: ViewModel の `ChartOperationTargetSnapshot.Charts`
- resource health の既存 `BMSFile` maintenance service 境界: `BMSLibrary` の `ChartFile` target resolution
- installed location repair の selection 境界: ViewModel の `RepairInstalledLocationTargetSnapshot.Charts`
- installed location repair の search / clear 境界: ViewModel の `RepairInstalledLocationTargetSnapshot.RepairEntries`
- installed location repair の fix mutation 境界: ViewModel の `RepairInstalledLocationTargetSnapshot.RepairCharts` から model の `FixInstallationDirectoryCharts(IEnumerable<ChartFile>)` へ渡す
- pending package / pending chart の context menu batch 操作: `GetSelectedChartTargets(..., isPendingSection: true)` から `PendingInstallDestinationTargetSnapshot` を作り、background task へ渡す
- pending install destination のセル編集: `ChartOperationTarget` から `PendingInstallDestinationEditTargetSnapshot` を作り、background task へ渡す
- 所持 bmson row の一時表示 state cache: `bmsonChartTransientStatesByKey` と `ChartFileTransientState`
- BMS 専用 operation helper: `GetSelectedBmsChartFiles(...)`
- model mutation reference: `LibraryChartRef`
- installed directory lookup: BMSFile + bmson_song の両方を hash / path で登録
- playlist entry identity: md5-only と sha256-only の両対応
- package discovery: `PackageChartEntry` / `ChartFile` により BMS / bmson を pending chart として扱う

## 現在の未抽象化領域

次の領域では、まだ `BMSFile` の名前と型が chart 共通概念を兼ねている。

### `BMSFile` の二重責務

`BMSFile` は本来 BMS / LR2 song table の model だが、現行では次の用途も持つ。

- pending install chart adapter
- bmson pending adapter の基底型
- legacy filter / sort / playlist / install API の共通引数
- compatibility BMSFile 変換の受け皿

production では `BMSFile` 型は BMS storage row として扱う。chart 共通処理は `ChartFile.Kind` を見る。

### `ChartPackage` adapter materialization boundary

`PackageChartDiscoverySnapshot.ChartFiles` と `ChartPackage.GetChartAdapters()` は削除済みで、discovery snapshot と package は `ChartEntries` を正本にする。package 全体を `List<BMSFile>` snapshot として取り出す production API は残さない。

package entry から BMS storage owner を扱う production 操作は、`PackageChartEntry.Chart.BmsFile` を読む。旧 `GetExistingBmsFormatAdapter()`、generic な `GetOrCreateCompatibilityAdapter()`、`CompatibilityAdapter` property、`GetOrCreateBmsFormatAdapter()` の production API は削除済みであり、bmson を package entry から新規 `PendingChartEntry` として materialize する入口や、既存 bmson adapter を chart-common state の正本として読む入口は残さない。

旧 `BMSFiles` alias と `GetChartAdapters()` は production 利用がないことを確認したうえで削除済みであり、テストコードにも production 互換 API を残すためのテストは置かない。adapter materialization 自体を検証したいテストは test-local helper に隔離し、production surface へ戻さない。

### model APIs の残存 BMS 名

`ChartRowsView` など通常一覧の public binding 名は chart row 名へ移行済みである。

`RemoveLibraryCharts(...)` は BMS / bmson 共通の library chart 削除入口であり、旧 `RemoveBMSFiles(...)` / `RemoveChartFiles(IEnumerable<BMSFile>)` wrapper は残さない。未使用だった single chart move wrapper も削除済みである。
pending package install 入口も `InstallChartPackagesAuto`, `ForceInstallPendingPackages`, `InstallPendingPackagesToEstimatedDestinations` へ移行済みで、旧 `InstallBMSFilesAuto` / `InstallChartPackagesForce` / `InstallChartPackagesToEstimatedDir` wrapper は残さない。
duplicate view の cache / search 入口は `DuplicateChartGroups` / `SearchDuplicateChartGroups()` に移行済みで、旧 `BMSFilesDuplicated` / `SearchBMSFilesDuplicated()` は残さない。

### BMS-only views and workflows

次の領域は BMS 専用意味を持つため、単純に chart 化しない。

- garbled / encoding check / encoding fix
- zero-note check
- LR2IR
- score viewer
- ranking update
- invalid extension rename
- audio convert
- LR2 `song` / `folder` update
- `chart_digest_map`

ここは `ChartFile` 化後も BMS-only capability として残す。
pending invalid extension rename は `GetPendingBmsFormatChartFilesSnapshot` / `RenamePendingBmsFormatChartFileExtensions` / `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` を入口にし、BMS-format chart file のみを対象にする。

## ChartFile / ChartPackage 化に向けた現在制約

### `ChartFile` に求められる性質

現行仕様では、`ChartFile` は DB row ではなく、アプリ内で譜面を扱うための domain/read model である。`ChartFile` は storage 正本を直接兼ねず、Kind と storage owner を通して BMS / bmson の永続化型へ接続する。

`ChartFile` は少なくとも次を表す必要がある。

- `Kind`: BMS / bmson
- current path / directory
- md5 / sha256
- title / artist / level / mode
- chart_info
- maintenance / resource health
- storage owner: BMS は `BmsFile`, bmson は `BmsonSong`

resource references は現行 `ChartFile` には直接載っていない。BMS では `BMSFile`、bmson では `bmson_song.wav_files` / `bga_files` と parser 直後の runtime-only 状態、集計では `ChartResourceSnapshot` が担う。resource health / install estimation を完全に chart-native にする過程で、resource reference の owner を `ChartFile` に載せるか、別の chart resource model として分離するかを決める。

source scope と operation capability は `ChartOperationTarget` が持つ。owned bmson 用の legacy `BMSFile` adapter provider は UI row / operation target surface から削除済みであり、chart 共通 operation は `ChartOperationTarget.CompatibilityBmsFile` を必要としない。

今後の整理では、新しい汎用中間表現を増やすことを優先しない。まず既存の `ChartFile`、`PackageChartEntry`、`ChartOperationTarget`、`LibraryChartRef`、BMS / bmson storage row のいずれかへ責務を置けるかを確認する。単に `Compatibility*` の名前を変えた factory / helper / facade は最終形で消える中間層になりやすいため、追加しない。

Kind ごとの storage 境界は次の通り。

| Kind | storage owner | 永続化先 | 現行責務 |
| :--- | :--- | :--- | :--- |
| BMS | `BMSFile` | LR2 `song` / `folder`、app-owned `chart_digest_map` など | LR2 `song` row として保存できる BMS 専用 data と、移行前から残る chart helper を併せ持つ。 |
| bmson | `LR2SongDBExtended.bmson_song` | app-owned `bmson_song` | path / folder / md5 / sha256 / title / subtitle / artist / genre / level / mode_hint / image/audio metadata / updated_at など bmson catalog 保存に特化する。resource refs / chart_info / maintenance は runtime owner として持つが table へは保存しない。 |

したがって、単純に `BMSFile` を `ChartFile` へ rename すると、LR2 `song` row という永続化境界と、BMS / bmson 共通の operation/read model 境界が混ざったまま名前だけ変わる。整理の主眼は、`BMSFile` から chart 共通責務を外し、BMS storage row と Chart domain model を分けることである。

`ChartFile` が存在しても storage の正本は二本立てを維持する。BMS は `BMSFile` または将来の BMS song row 型、bmson は `bmson_song` が永続正本である。

### `ChartPackage` に求められる性質

現行仕様から見ると、`ChartPackage` は少なくとも次を表す必要がある。

- package path
- lazy chart discovery snapshot
- pending chart list
- install estimation surface snapshot
- representative chart
- chart resource aggregate

`ChartPackage` の heavy lazy discovery 挙動は重要である。rename や API 置換時も、chart adapter / entry 参照時に discovery が走る既存意味を不用意に変えない。

### compatibility API の扱い

production の旧 compatibility BMSFile adapter surface は削除済みである。bmson は `BMSFile` 継承 adapter に変換せず、BMS storage owner が必要な経路だけ `ChartFile.BmsFile` / `PackageChartEntry.FromBmsFile(...)` を使う。

production に残る `Compatibility` 名は playlist summary column settings の設定互換、LR2 compatibility warning category、旧 standalone song path 正規化のような永続 / 外部仕様互換だけである。譜面 row / package / resource health の adapter surface 由来の `Compatibility*` 名は残さない。

旧実装で compatibility adapter が担っていた state は、現在は次の owner に分ける。

- BMS storage row state: `BMSFile`
- bmson storage row state: `bmson_song`
- package / pending chart state: `PackageChartEntry`
- UI 表示上の一時 state: `ChartFileTransientState`
- chart identity / projection: `ChartFile`

## Active migration plan

2026-05-20 時点の方針は、層を薄く順番に chart-native 化するのではなく、production に残っている compatibility 境界を境界単位で閉じることを優先する。

進捗指標は `BMSFile` 参照総数ではない。`BMSFile` は最終形でも BMS storage row / LR2 `song` row / BMS-only operation として残る。進捗は次の production compatibility surface が消えているかで見る。

- `CompatibilityBmsFile` / `CompatibilityAdapter` / `GetOrCreateCompatibilityAdapter()`
- `PendingChartEntry : BMSFile`
- bmson を `BMSFile` adapter に materialize しないと表示・検索・sort・playlist・resource health・package operation が進まない経路
- production では不要になった旧名 wrapper / 互換 API

### 作業粒度

1 commit は、原則として 1 つの production compatibility boundary を閉じる単位にする。helper 1 個、local rename 1 個、test-only cleanup 1 個だけでは commit 単位にしない。

中間 helper を増やして旧実装を包むより、最終 owner へ直接責務を移す。責務の移動先は `ChartFile`、`PackageChartEntry`、`ChartOperationTarget`、`LibraryChartRef`、`BMSFile`、`bmson_song` のいずれかを優先する。`Compatibility*` の別名 wrapper や、最終的に消す facade を新設しない。

大きめの compiler-driven 変更は許容する。ただし、settings 名、UI 文言、DB table / column、playlist JSON / DB、LR2 互換 schema は別契約として扱い、抽象化だけを理由に変更しない。

### 残っている主な compatibility 境界

2026-05-20 時点では、active plan で追っていた production compatibility boundary は閉じている。以降は `BMSFile` の残存参照を件数ではなく意味で監査し、BMS storage row / LR2 `song` row / BMS-only operation 以外の production 参照が見つかった場合に新しい active plan 項目として切り出す。

### 閉じた compatibility 境界

- `ChartPackage.ChartEntries` と `PackageChartEntry.Chart` は package 内 chart の正本である。generic な `PackageChartEntry.GetOrCreateCompatibilityAdapter()`、`PackageChartEntry.CompatibilityAdapter`、`PackageChartEntry.GetOrCreateBmsFormatAdapter()`、`PackageChartEntry.GetExistingBmsFormatAdapter()` production API は削除済みである。BMS storage owner は `PackageChartEntry.Chart.BmsFile` として読み、`PackageChartEntry` 内に別の BMS owner field は持たない。
- `LibraryChartRow`、`PlaylistDetailSourceRow`、`PlaylistDetailRow`、`GridRowResolver`、`ChartOperationTarget` は `ChartFile` を正本にし、owned bmson 用 `CompatibilityBmsFile` surface は削除済みである。production では旧 adapter-backed bmson の storage owner 抽出も削除済みであり、`ChartFileProjection.FromBmsFile(...)` は BMS storage owner 専用である。
- `PendingChartEntry` は production assembly だけでなく test fixture からも削除済みである。test helper の BMS owner 取得も `GetBmsOwnerForTest()` / `GetBmsOwnersForTest()` へ寄せている。
- playlist reference mutation は `PackageChartEntry` / `ChartFile` で pending target を照合し、BMS `RefTables` writeback だけを `ChartFile.BmsFile` に限定する。bmson の参照表示は `PlaylistReferenceIndex` / chart identity で完結し、pending bmson のために adapter を materialize しない。
- library file operation cleanup は `LibraryChartRef` / `PackageChartEntry` / `ChartFile` を入口にする。pending package の install destination cleanup / rewrite は `PackageChartEntry` を直接更新し、installed library row 側の persistent install destination writeback は `LibraryInstallDestinationChange.BmsFile` に限定する。
- resource warning ignore / unignore の production 入口は `ChartFile` であり、maintenance service の API 名も `SetChartResourceWarningsIgnored(...)` に寄せている。BMS / bmson の ignored flag は `ChartFile` から `BMSFile.maintenanceInfo` または `bmson_song.MaintenanceInfo` へ解決する。resource health index delta も `setMaintenanceInfo(...)` 内の maintenance target charts を直接使い、未使用だった BMSFile-only delta parameter は残さない。
- pending / install resource health warning projection は `BmsLibraryPackageInstallService.BuildPendingResourceHealthWarnings(PackageChartEntry)` へ集約する。`BmsLibraryInitializationService.LoadInstallTable(...)`、auto install workflow、pending regroup 後の warning 再初期化は `Func<BMSFile, bool>` callback を受けず、BMS / bmson とも `PackageChartEntry` から warning list を作る。BMS は resource snapshot 計算のために BMS-only `SetHealthStatus(...)` を使うが、呼び出し側が `BMSFile.Warnings` を strict check の中間状態として読む経路は残さない。

### 推奨実装順

1. production の `BMSFile` 参照を意味で監査する。BMS storage row / LR2 `song` row / BMS-only operation ではない参照が見つかった場合だけ、境界単位で新しい plan 項目にする。
2. test と docs の adapter 表現を cleanup する。behavior test は `bmson_song` / `ChartFile` / `PackageChartEntry` を正本にし、旧 adapter 非生成を明示する必要がある場合だけ historical wording を残す。

### 実装メモ

- `PendingChartEntry : BMSFile` は production assembly と test fixture の両方から削除済みである。旧実装では `BMSFile` 引数 API に bmson adapter を渡して chart-common 処理へ入れる経路があったが、現行 production では `ChartFileProjection.FromBmsFile(...)` は BMS 専用、`ChartFileKindResolver.IsBmsChartFile(...)` も BMS storage row 判定専用とする。
- resource health の warning 計算は mutation から分離する。`BMSFile` の warning set を更新する入口は BMS storage row へ降りる境界として残すが、一覧 filter / projection / pending package 判定は `ChartFile` と maintenance row から `ChartWarning` list を計算する。
- bmson は parser 直後に `bmson_song.MaintenanceInfo` が存在していても、resource health の defined / existing count が未計算なら placeholder とみなし、`ChartResourceSnapshot` と現在の filesystem 状態から一時 maintenance snapshot を作る。hash が現在の `bmson_song.md5` と一致する場合だけ既存の ignored flag を引き継ぎ、hash が古い maintenance row は使わない。旧実装ではこの判定のために `PendingChartEntry` adapter を作って `SetHealthStatus()` していたが、adapter materialization は状態計算の副作用だったため移植しない。
- `BmsLibraryMaintenanceService.UpdateMaintenanceInfo(bmsFiles, bmsonSongs, ...)` は BMS と bmson を内部で別 pipeline として処理する。旧実装は `bmson_song` を `PendingChartEntry` に変換して BMSFile pipeline へ混ぜ、同一 section log に載せていた。現行実装では `maintenance_target_summary` / `maintenance_update section_*` log が BMS section と bmson section に分かれる場合があるが、resource health / reparse / reused / failed / maintenance upsert / no song upsert の集計値は `MaintenanceWorkflowResult` に合算する。これは log 粒度だけの差であり、bmson を BMSFile list に混ぜる構造互換は移植しない。
- bmson resource health 計算は `ChartResourceSnapshot` と `ResourceHealthLookupContext` を使い、directory resource index を優先し、missing cache entry の時だけ実ファイル確認へ fallback する。旧実装の `PendingChartEntry.SetHealthStatusUsingLookupContext(...)` が内部で使っていた cache-hit / fallback counter は維持するが、`BMSFile` の component cache や `memClear` 副作用は bmson storage row には移植しない。
- `ChartFile` は chart domain model として audio / visual resource refs と optional image refs（stagefile / backbmp / banner）を持つ。bmson の resource health は `ChartFile` projection だけから `ChartResourceSnapshot` を作れるため、`bmson_song` owner へ戻って resource refs を読む必要はない。BMS は `BMSFile` parser / component cache が BMS 専用責務として残るため、`ChartResourceSnapshot.Create(ChartFile)` は BMS storage owner がある場合だけ `BMSFile` の component cache を使う。
- aggregate resource snapshot の production 入口は `IEnumerable<ChartFile>` のみである。旧 `IEnumerable<BMSFile>` overload は production 参照がなく、テストだけの旧入口になっていたため削除する。単一 BMS chart の parser / component cache 読み取りは `ChartResourceSnapshot.Create(ChartFile)` 内部の BMS storage owner 分岐に閉じ、package / estimation / resource surface の集約は `ChartFile` list を正本にする。
- file diff 初期化中の inline bmson maintenance は parser 済み `bmson_song` から直接 `MaintenanceInfo` を作る。旧実装の `PendingChartEntry` shim は BMSFile API に health 計算を通すためだけの一時 object だったため、inline 成功判定、recoverable exception handling、lookup counter は維持しつつ shim 作成は移植しない。
- pending package discovery、install table load、pending regroup 後の warning 再初期化では、bmson の `ResourceHealth` warning は `PackageChartEntry` に直接書き戻す。BMS entry は `SetHealthStatus()` を BMS parser / resource cache 更新境界として必要な範囲で通し、その `BMSFileMaintenanceInfo` から `ChartWarning` list を作って `PackageChartEntry` に書き戻す。旧実装のような `Func<BMSFile, bool>` callback や `BMSFile.Warnings` を pending package warning の中間 state として使う経路は残さない。
- BMS chart については、現時点では `BMSFile.HasValidMaintenanceInfoSnapshot` を持つ場合だけ resource health projection の入力にする。BMS の strict resource scan / encoding / zero-note はまだ BMS-only maintenance boundary へ残し、今回の chart-common 計算へ無理に混ぜない。
- `ResourceHealthIndexSnapshot.ActiveTargets` / `IgnoredTargets` は `ChartFile` を返す。旧実装の `BMSFile` target list は UI filter へ BMS adapter を渡すための構造であり、bmson を adapterless に扱う最終形では chart row projection に直接渡す。
- resource health projection の production 入口は `ChartFile` のみである。旧実装との互換として存在していた `BMSFile` / `bmson_song` projection lookup overload は削除し、通常一覧 / virtual source row の fallback も `row.Chart` だけを見る。これは表示対象 row が BMS / bmson とも `ChartFile` を作れる状態になったためであり、BMS storage row や bmson row を projection lookup の public surface として残さない。
- playlist reference の表示 lookup は `PlaylistReferenceIndex.Find(ChartFile)` / `Find(LibraryChartRef)` を入口にする。md5 / sha256 の文字列 pair は index 内部の lookup detail として残し、通常一覧 source row / materialized row / playlist detail row は chart identity を渡す。旧実装では、pending / installed package の bmson が playlist reference に一致した場合、表示用 `RefTables` を持たせるためだけに `PendingChartEntry` adapter を materialize したり、既に materialized された bmson adapter の `BMSFile.RefTables` を更新したりしていた。現行実装では、`BMSFile.RefTables` の書き戻しは BMS storage row だけに限定し、bmson の表示は `PlaylistReferenceIndex` へ任せる。このため、bmson compatibility adapter に playlist reference を持たせる副作用は移植しない。md5 優先、sha256 fallback の一致順序は維持する。
- `PackageChartEntry.GetPlaylistReferenceAdapter(...)` は削除済み。playlist reference 境界で必要な BMS writeback は `ChartFile.BmsFile` を読むだけであり、playlist reference のために adapter を作る入口を残さない。
- playlist table の replace / remove / synchronize は pending package target を `PackageChartEntry` / `ChartFile` で照合し、BMS `RefTables` の remove / refresh だけを `ChartFile.BmsFile` に適用する。旧実装では pending target snapshot が `BMSFile` adapter list だったため、bmson pending target は mutation 対象として表現されにくかった。現行実装では bmson も `targetsOldPending` などの chart match には含めるが、bmson storage row には `RefTables` を持たせず、表示は `PlaylistReferenceIndex` 更新で消える/付くようにする。
- playlist detail の score 表示は `ScoreSnapshot` の md5 / sha256 index から直接 `BMSScore` snapshot を解決する。旧実装では未所持 row 用に `PlaylistScoreProbeBmsFile : BMSFile` を一時生成して `SetBMSScoreWithMetrics(...)` に通し、fake BMSFile の `bmsScore` を表示 source row に渡していた。現行実装では score 表示のためだけに BMSFile 派生 object を作る構造互換は残さない。owned BMS row では storage owner である `realFile` の hash / sha256 を playlist entry の hash より優先する。旧実装は playlist entry 側の stale hash が非空だと別 chart の score snapshot を拾い得たが、owned row の表示は現在の library storage row を正本にするほうが自然なので改善する。missing row は従来どおり playlist entry md5 と entry chart_info sha256 を使う。`playlist_score_probe_*` log 名は履歴上の operation 名として残るが、chunk / BMSFiles lock wait の内訳は出さず、target / matched / elapsed だけを出す。
- playlist table replace / external resync 後は `NormalLibraryReferenceTablesChangedReason` で通常一覧 sort key と bmson playlist reference 表示を明示的に無効化する。`PlaylistTableUpdateContext.Updated` は `last_update` 更新有無を表すため、旧 DB 修復や hash 初期化のように playlist entry persistence だけが必要な場合は `ReferenceEntriesChanged` で replace callback を走らせる。BMS row は `RefTables` property change でも表示更新され得るが、bmson row は `PlaylistReferenceIndex` provider から表示値を読むため、index 更新だけで可視 row へ通知しない旧挙動は移植しない。
- folder delete / move / merge に伴う install destination cleanup は、pending package では `PackageChartEntry.Chart.InstallDestination` を読み、library row では `LibraryChartRef` を入口にして BMS storage owner だけを `LibraryInstallDestinationChange.BmsFile` に落とす。`bmson_song` には `instl_dst` 相当の永続列がないため、library bmson row の persistent install destination mutation は新設しない。旧実装では cleanup helper が最初から `IEnumerable<BMSFile>` を受けていたが、現行実装では chart-common operation 境界を保ちつつ、BMS-only persistent writeback を property 名で明示する。
- package membership / selection target の一致判定は `PackageChartEntry.IsSameChartTarget(...)` へ寄せる。判定は `ChartFile.Kind` を揃えた上で、同一 entry / BMS storage row / bmson storage row reference、最後に path 一致を見る。旧実装の一部には path が無い時に `PrimaryLookupHash` fallback で一致させる経路があったが、同一 hash の別 package chart を package 所属と誤認する可能性があるため移植しない。install destination resolve や playlist reference のような「同一譜面候補を hash で探す」処理は別責務であり、package membership では hash を使わない。
- split pending package の regroup 時に同じ chart target を重複除外する判定も `PackageChartEntry.IsSameChartTarget(...)` を使う。旧実装では `BMSFile` adapter reference と path の二重判定で重複を落としていたため、adapterless bmson は path だけが identity になり、同じ `bmson_song` owner を持つ path-less / path-changed entry を別 chart として扱い得た。regroup は package membership の再構成なので、adapter を読まず chart identity に寄せる。
- `ChartPackage.ReplaceChartEntries(...)` / `PackageChartDiscoverySnapshot.ReplaceChartEntries(...)` は `PackageChartEntry.ToChartEntrySnapshot()` で snapshot 化する。BMS は LR2 `song` row owner を保持するため `BMSFile` adapter を残すが、bmson は `bmson_song` storage row と `ChartFile` projection を正本にし、materialized `PendingChartEntry` adapter は snapshot へ持ち越さない。これは旧実装の「bmson adapter があれば BMS 側 source として扱う」挙動を改善するもので、表示 source snapshot は `PackageChartEntry.Chart` を `ChartFile` list として渡し、BMS / bmson storage row の二列 snapshot へ戻さない。
- `LibraryChartRow.FromPackageChartEntry(...)` は `ChartFile.Kind` を優先する。旧実装では package entry が materialized bmson adapter を持ち得たが、現行の表示 row の storage shape は `bmson_song` 側に寄せ、`BmsFile` としては持たない。operation target は `PackageEntry` を保持するため、package 操作では row の `CompatibilityBmsFile` に戻らない。BMS-only row だけが `BmsFile` を持つ。
- `LibraryChartRow` の入力境界は `ChartFile` である。`FromBmsFile(...)` / `FromBmsonSong(...)` は storage owner から warning なしの `ChartFile` projection を作って row を初期化し、`Chart` getter では owner の現在値と transient state から再 projection する。一方、`FromChartFile(...)` は caller supplied projection をそのまま `Chart` として返す。旧実装では `chartOverride` が projection instance を固定していたため、repair / warning 表示 state のように ViewModel 側で作った projection instance を operation target へ渡せた。現行実装でもこの性質は維持しつつ、通常 library row の owner-backed freshness とは `hasSourceChartProjection` で分ける。
- `LibraryChartRow` の identity / 表示用 getter（title、artist、genre、mode、tag、level、hash、folder、path、chart_info など）は、storage owner がある row では従来どおり `BMSFile` / `bmson_song` の現在値を優先し、metadata-only の `ChartFile` や package entry 由来 projection だけ `ChartFile` snapshot を fallback として読む。旧実装では通常一覧 row が常に storage owner を持つ前提だったため、projection-only chart row で値が落ち得た。owner-backed row の property change / hydration 追従と、identity getter が warning snapshot を毎回作らない性能特性は維持する。`Level` / `Folder` の setter は現時点では BMS storage row の手動編集境界として残し、bmson は folder rename / move の chart operation 経路で扱う。
- 一覧 summary の folder count も、`LibraryChartRow` / `PlaylistDetailRow` では row getter を優先し、未知 row 型だけ `GridRowResolver.TryGetChartFile(...)` の `ChartFile.Folder` へ fallback する。旧実装は row 型ごとの getter を直接見ていたため、owner-backed row の path / folder mutation 後も current owner を読めた。`ChartFile` fallback を先にすると `LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(file))` のような owner-backed snapshot が stale folder を返し得るので、その副作用は移植しない。
- `ChartListSourceRow.ChartInfo` は storage owner の現在値を優先し、projection-only `ChartFile` では `sourceChart.ChartInfo` を fallback として読む。旧実装では virtual source row が常に `BMSFile` / `bmson_song` owner を持つ前提だったため、duplicate / package / parse-failure などの projection-only subset で chart_info sort key が落ち得た。owner-backed row の hydration 追従は維持し、storage owner がない read model だけ `ChartFile` snapshot を正本にする。
- `ChartListSourceRow` は `ChartFile` projection を constructor の正本にする。BMS / bmson の storage owner は `ChartFile.BmsFile` / `ChartFile.BmsonSong` から取り出し、owner-backed row では `Chart` getter が現在 owner と transient state から再 projection する。旧実装では `BMSFile` / `bmson_song` を constructor 引数として持ち、必要に応じて内部で `ChartFile` を組み立てていたが、virtual source row の責務は chart identity / sort / filter read model なので、入力境界を `ChartFile` に寄せる。ただし `HasSourceChartProjection` は「caller supplied `ChartFile` projection を materialization 時にも尊重する」契約であり、storage owner が無いことを意味しない。通常 library の owner-backed source row はこの flag を立てず、`LibraryChartRow.FromChartFile(...)` へ誤って materialize しない。bmson owner-backed projection で provider transient state と source projection warning が両方ある場合は、provider の install destination state を優先しつつ、provider が warning を持たないときだけ source projection warning を fallback として重ねる。
- table context menu の playlist missing 判定は `ChartOperationTarget.IsPlaylistMissing` だけを見る。旧 fallback の `GridRowResolver.IsPlaylistRow(row) && GridRowResolver.GetRealBmsFile(row) == null` は、owned bmson playlist row も `RealFile == null` になり得る BMS-only 判定だったため移植しない。`TryGetChartOperationTarget(...)` が失敗する row は chart operation target として表現できないので、通常 menu を選ぶ。
- `ChartOperationTarget.ToPackageChartEntry()` は、`PackageEntry` がない loose target では `ChartFile` から `PackageChartEntry` を作る。BMS storage owner を持つ chart だけ `PackageChartEntry.FromBmsFile(...)` へ落とし、bmson では compatibility provider を呼ばない。旧実装では repair / install destination 用 snapshot の lazy entry materialization 時に provider から bmson adapter を作り、adapter の `instl_dst` / warning を overlay し得た。現行実装では、operation target 作成時点の `ChartFile` snapshot に含まれる state だけを使い、後段処理のためだけに loose bmson adapter を materialize する副作用は移植しない。
- `PackageInstallExecutionResult` / estimated install batch state は bmson adapter list を持たない。install 後の package entry は `ChartPackage.ReplaceChartEntries(...)` の snapshot 化で bmson adapter を落とし、`bmson_song` storage row を正本に戻すため、adapter-backed / adapterless の差は install result に残さない。旧実装は空になり得る `AddedBmsonAdapters` を BMSFiles 差し替えや maintenance target の分岐に残していたが、bmson は `BmsonSongs` 更新と `bmson_song` 由来の maintenance target 作成で扱う。BMSFiles の差し替え対象は BMS storage row のみとし、bmson path を BMSFiles から除去する旧互換処理は「bmson を BMSFiles に入れない」前提へ寄せて移植しない。
- estimated install batch の deferred maintenance state は BMS と bmson を分け、BMS は `DeferredBmsMaintenanceTargets`、bmson は `DeferredBmsonMaintenanceSongs` に保持する。`setMaintenanceInfo(...)` は BMS row と `bmson_song` を別入力として受け、`BmsLibraryMaintenanceService.UpdateMaintenanceInfo(...)` も bmson を一時 `PendingChartEntry` に変換しない。旧実装では deferred target list 自体に bmson adapter を混ぜていたが、batch result / BMSLibrary orchestration / maintenance service のいずれでも `bmson_song` を正本にする。inline chart_info は direct deferred bmson songs と installed package から解決した bmson songs を path dedupe して渡し、resource-only display package 由来の既存挙動を落とさない。
- folder merge 後の maintenance 再計算も BMS row と `bmson_song` を分けて `setMaintenanceInfo(...)` に渡す。旧実装では移動済み bmson と移動先既存 bmson を `PendingChartEntry` adapter にして BMS target list へ混ぜていたが、merge orchestration と maintenance service の正本は `bmson_song` であり、adapter 作成は移植しない。
- resource warning ignore / unignore の model 入口は `ChartFile` のみを production surface とし、BMS / bmson の maintenance row を `ChartFile` から解決する。旧 `BMSFile` list 入口は installed bmson を `PendingChartEntry` adapter にして同じ list へ混ぜていたが、production 参照がなくなったため削除する。bmson の ignore flag 正本は `bmson_song.MaintenanceInfo` とし、旧 adapter の maintenance snapshot へ同期する副作用は移植しない。
- resource warning ignore / unignore の service API は `SetChartResourceWarningsIgnored(...)` とする。旧名 `SetFilesWarningIgnored(...)` は chart 共通 operation になった後も files / BMSFile 寄りの名前を残していたため削除し、テストからだけ参照される互換名も残さない。
- `setMaintenanceInfo(...)` の resource health delta は、実際に maintenance 対象として組み立てた `ChartFile` list を使う。旧実装由来の BMSFile-only delta target parameter は production 参照がなく、bmson maintenance target を別入力にした現在の構造では中間 adapter list を再導入する入口になり得るため残さない。
- `PackageChartEntry.GetOrCreateCompatibilityAdapter()` は production surface から削除済みである。BMS format chart で BMS parser / maintenance API が必要な箇所は `PackageChartEntry.Chart.BmsFile` を使い、bmson adapter materialization は production の package entry API では提供しない。旧 test が adapter writeback を検証する場合は test-local helper に閉じ込め、後続 cleanup で chart state assertion へ寄せる。
- `PackageChartEntry` 内の mutable writeback target は `ChartFile.BmsFile` で表される BMS storage owner のみとする。`PackageChartEntry` は BMS owner 専用の別 field を持たず、install destination / warning / installed path などの chart-common state は BMS では `BMSFile`、bmson では `bmson_song` と package entry state に保持する。旧実装では adapter-backed bmson の adapter fields も同時に更新され得たが、bmson adapter は最終形の storage owner ではないため、この副作用は移植しない。
- pending package の resource health warning 計算では、旧実装のように `requiresPendingWarning(BMSFile)` へ BMS / bmson を渡さず、`PackageChartEntry` / `ChartFile` / maintenance snapshot から warning list を返す。BMS entry は resource snapshot を更新するために `BMSFile.SetHealthStatus()` を呼ぶが、これは BMS parser / resource cache 更新であり、呼び出し側が `BMSFile.Warnings` を strict warning 判定の中間状態として読む副作用は移植しない。旧実装では callback が任意の warning を `BMSFile` に積めたが、production では resource health warning list だけが必要なため、test 用 callback 互換も残さない。
- `PackageChartEntry.GetOrCreateBmsFormatAdapter()` と `PackageChartEntry.GetExistingBmsFormatAdapter()` は production surface から削除済みである。旧実装では名前上 adapter creation / existing adapter 境界に見えたが、最終的には BMS storage owner / 既存 BMS adapter を読むだけになっていたため、BMS format chart は `PackageChartEntry.Chart.BmsFile` に統一する。起動時 pending warning 初期化や BMS extension rename snapshot のように BMS parser / maintenance API が必要な箇所も `Chart.BmsFile` を読む。旧 `GetOrCreateCompatibilityAdapter()` は bmson も materialize できるため、chart-common 経路から直接呼ばない。
- BMS storage owner を読むだけの playlist reference の `RefTables` 書き戻し、install result の `AddedBmsFiles` 抽出、install estimation の lock target、folder merge 後の BMS upsert target は `PackageChartEntry.Chart.BmsFile` を読む。bmson は旧 adapter-backed 形由来であっても `ChartFile.BmsFile == null` とし、chart-common 表示 / resource / install metadata は `ChartFile` / `PackageChartEntry` 側を読む。package source row / playback stop target snapshot も `PackageChartEntry.Chart.BmsFile` を直接読むため、表示・snapshot のためだけに BMS-only helper へ降りない。
- install 後の source folder safe cleanup は、残存 chart がすべて導入済み hash かだけを確認する。bmson 残存 chart は `BmsonSongParser.Parse(...)` で `bmson_song` として lookup hash を読み、BMS 残存 chart は `ChartFileContentReader.ReadSnapshot(...)` で MD5 を読む。`PendingChartEntry.CreateFromFilePath(...)` へは通さない。旧実装では hash 確認だけのために `PendingChartEntry` を作っていたが、cleanup 判定は chart identity の照合であり adapter state を必要としないため移植しない。
- chart lookup hash の共通 helper は `ChartLookupKey` へ移す。旧 `PendingChartEntry.GetPrimaryLookupHash(...)` / `GetPrimaryLookupHashKind(...)` と `PendingChartLookupHashKind` は chart identity の概念であり、pending adapter 固有ではないため production surface から削除する。MD5 優先、なければ SHA256 という lookup semantics は維持する。
- installed directory index の service 入口は `IEnumerable<ChartFile>` に統一する。旧実装では `BuildInstalledHashToDirectoryMap(IEnumerable<BMSFile>, IEnumerable<bmson_song>)` のように BMS が主入力で bmson が補助入力だったが、directory index は chart identity と path directory だけを見る chart-common index なので、BMS / bmson storage owner の分岐は `BMSLibrary` 側の installed chart snapshot 作成に閉じる。未使用だった `TryGetInstalledDirectoryByHash(IEnumerable<BMSFile>, ...)` は、BMSFile list 入口を復活させるだけの旧 API だったため移植しない。test は test-local helper で BMS / bmson storage row を `ChartFile` に投影してから service を呼ぶ。
- chart path / kind 判定の共通 helper は `ChartFileKindResolver` へ移す。production の `ChartFileKindResolver` は path extension と BMS storage row 判定だけを持ち、`BMSFile` subtype から bmson adapter を認識する API は削除する。旧 `PendingChartEntry.IsBmsonFilePath(...)` / `IsSupportedChartFilePath(...)` / `IsBmsonChartFile(...)` / `IsBmsChartFile(...)` は pending adapter 固有の責務ではないため production surface へ残さない。
- package 表示 row / virtual source snapshot / 再生停止 target snapshot は `PackageChartEntry.Chart` を正本にし、BMS row は `ChartFile.BmsFile`、bmson row は `ChartFile.BmsonSong` を読む。旧実装は `CompatibilityAdapter` だけを見ていたため、`PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(...))` のような adapterless BMS entry は停止 target から漏れ得た。これは「既存 BMS storage owner を持つ package entry は BMS playback target である」という chart-domain 上の期待に反するため、旧挙動は移植せず BMS storage owner も含める。
- duplicate analysis は bmson duplicate row の `PendingChartEntry` adapter を作らない。旧実装では duplicate 判定後に bmson adapter を materialize し、BMS と同じ `DuplicateFiles` set に入れて warning を付与していたが、duplicate tree 表示の正本は `DuplicateGroup.ChartFiles` であり、bmson storage row に warning collection もないため、この副作用は移植しない。BMS storage row には従来どおり `DuplicateChart` warning を付与し、bmson には duplicate group 内の `ChartFile` projection へ同 warning を重ねる。ただし duplicate group は connected directory 内の全 chart を表示するため、warning を重ねる対象は重複 hash group に属する row だけに限定する。旧実装でも warning 付与対象は duplicate hash group の `DuplicateFiles` だけだったため、同一フォルダ内の unrelated sibling chart を duplicate warning 対象にしない。
- UI row / operation target は owned bmson の `CompatibilityBmsFile` surface を持たない。旧実装では shared `PendingChartEntry` adapter が repair install destination や warning state の owner になり、row 再生成後も同じ adapter を参照していた。現行実装では ViewModel の `bmsonChartTransientStatesByKey` が `ChartFileTransientState` を保持し、`LibraryChartRow` / `PlaylistDetailSourceRow` / `ChartListSourceRow` へ `ChartFile` projection として重ねる。これにより、表示 state の保持は維持しつつ、operation target が bmson を BMSFile adapter に戻す副作用は移植しない。
- 旧 adapter-backed bmson から `bmson_song` storage owner を取り出す互換処理は production から削除済みである。`LibraryChartRow`、`LibraryChartRef`、resource maintenance orchestration、state applier は `ChartFile` / `bmson_song` / `PackageChartEntry` を直接受け取り、BMSFile subtype の owner 抽出を行わない。旧実装では各所で `PendingChartEntry.BmsonSong` を直接読んでいたが、bmson adapter は最終形の storage owner ではないため、この副作用は移植しない。
- `PackageChartEntry` は BMS storage owner を別 field として保持しない。BMS path / BMS parser result は `ChartFileProjection.FromBmsFile(...)` で `ChartFile.BmsFile` に接続し、bmson path は `ChartFileProjection.FromBmsonSong(...)` で `ChartFile.BmsonSong` に接続する。旧実装や一部 test helper では adapter-backed bmson entry が `compatibilityAdapter` field に残り得たが、bmson adapter は storage owner ではないため、この保持副作用は移植しない。
- test helper の `GetBmsOwnerForTest()` / `GetBmsOwnersForTest()` は production field を reflection で読む helper ではなく、`PackageChartEntry.Chart.BmsFile` を読む BMS storage owner helper である。旧 helper 名の `GetCompatibilityAdapterForTest()` / `MaterializeChartAdaptersForTest()` は削除済みである。adapter materialization を行う production surface は残さない。historical wording が XML doc / test name / local variable に残る場合も、behavior が bmson adapter 非生成を明示する場合を除き、後続 cleanup で BMS owner / chart entry 表現へ寄せる。
- production に残る `Compatibility` 名は playlist summary column settings の設定互換、LR2 compatibility warning category、旧 standalone song path 正規化のような永続 / 外部仕様互換だけである。譜面 row / package / resource health の adapter surface 由来の `Compatibility*` 名は削除または BMS owner 名へ置き換える。未使用だった `ChartFileTransientState.FromCompatibilityFile(...)` は削除し、resource health key の storage owner factory も projection lookup surface からは削除済みである。
- package discovery の BMS path は `BMSFile.CreateBMSFileFromFile(...)` で BMS storage owner を作る。旧実装では BMS path でも `PendingChartEntry.CreateFromFilePath(...)` を通し、BMS 用 pending adapter として package entry に入れていたが、BMS parser 結果は既に `BMSFile` であり、package entry が必要とする mutable state も `BMSFile` 側にあるため、BMS pending discovery 用の `PendingChartEntry` wrapper は作らない。bmson path は従来どおり `bmson_song` / `ChartFile` entry として保持する。

### 完了判定

- `BMSFile` は実体 BMS / LR2 `song` row / BMS-only operation に閉じている。
- bmson を扱う通常表示、playlist detail、package / pending、duplicate、install destination、resource health の production 経路が `BMSFile` adapter 生成を要求しない。
- `PendingChartEntry` が production / test fixture のどちらにも存在しない。
- `ChartPackage.ChartEntries` / `PackageChartEntry.Chart` が package 内 chart の正本であり、package-level adapter list API が復活していない。
- `CompatibilityBmsFile` / `CompatibilityAdapter` は production surface に残っていない。`GetOrCreateCompatibilityAdapter()` / `GetOrCreateBmsFormatAdapter()` / `GetExistingBmsFormatAdapter()` は production surface から削除済みであり、package 内 BMS storage owner は `PackageChartEntry.Chart.BmsFile` で読む。
- storage は BMS `BMSFiles` / bmson `BmsonSongs` の二本立てを維持し、playlist / LR2 DB / settings の永続互換を壊していない。

## 今後の仕様整理で守る境界

1. Storage は BMS / bmson の二本立てを維持する。
2. UI / operation の入口は chart target に寄せる。
3. BMS-only 処理は capability で明示する。
4. `BMSFile` 型を chart 共通処理の種別判定に使わない。production では `BMSFile` を BMS storage row / BMS-only 処理に閉じ込め、chart 共通処理は `ChartFile.Kind` を確認する。
5. package 内 chart の読み取りは `ChartEntries` を入口にする。adapter materialization は対象 entry 単位に限定し、package-level adapter list API を再導入しない。
6. settings 名と UI 文言の BMS は互換契約として残す。内部 helper / log / operation symbol は必要に応じて chart 名へ寄せる。
7. `ChartFile` / `ChartPackage` を使う場合も、既存の LR2 互換 DB と playlist JSON / DB の永続形式は維持する。
