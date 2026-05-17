# BMS / bmson chart abstraction current state

## 目的

この文書は、現行実装における BMS / bmson の譜面抽象化の状態を記録する。

今後 `BMSFile` に残る chart 共通責務を `ChartFile` などのアプリ内 domain model へさらに分離していくために、現在どこまで chart として共通化され、どこに BMS / LR2 前提の境界が残っているかを明文化する。

この文書は移行計画ではなく、現在仕様を表す。将来の rename や抽象化を行う場合も、ここに書いた storage / read model / operation target の責務を崩さないことを前提にする。

## 用語

| 用語 | 現行実体 | 意味 |
| :--- | :--- | :--- |
| BMS chart | `BMSFile : LR2SongDB.song` | LR2 `song` / `folder` table 由来の所持 BMS 譜面。 |
| bmson chart | `LR2SongDBExtended.bmson_song` | アプリ独自 table `bmson_song` 由来の所持 bmson 譜面。 |
| storage row | LR2 `song` row, `LR2SongDBExtended.bmson_song` row | DB table へ保存する永続化単位。BMS の現行 in-memory owner は `BMSFile : LR2SongDB.song` だが、DB commit は LR2 `song` row 型として行う。bmson は app-owned `bmson_song` row。 |
| application chart file | `ChartFile` | 本アプリで譜面を操作・表示するための domain model。直接の DB 永続化型ではなく、Kind と storage owner を持つ。 |
| pending chart | `PendingChartEntry : BMSFile` | package / pending install 上の譜面 adapter。BMS と bmson の両方を `BMSFile` 互換 API に載せる。 |
| chart row | `LibraryChartRow`, `ChartListSourceRow` | 通常一覧や仮想 filter / sort 用の read model。storage の正本ではない。 |
| operation target | `ChartOperationTarget` | UI command / context menu が扱う操作対象。capability を持つ。 |
| library chart ref | `LibraryChartRef` | model 層の移動 / 削除などで使う BMS / bmson 共通参照。 |
| compatibility BMSFile | `PendingChartEntry.CreateFromBmsonSong(...)` など | bmson を既存 `BMSFile` 引数 API へ渡すための一時 adapter。 |

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

`bmson_song` は BMS の `song` table には入れない。path を中心に、md5 / sha256 / title / artist / mode_hint を永続列として持つ。

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
- pending / newly installed の `PendingChartEntry`

`LibraryChartRow.Chart` は `ChartFile` を返す。BMS では実体 `BMSFile` を `CompatibilityBmsFile` として持ち、bmson では `BmsonSong` と、必要に応じて既存 `BMSFile` API 用の adapter を `CompatibilityBmsFile` として持つ。pending bmson の場合は、現在の adapter path / hash を優先しつつ `BmsonSong` も保持する。

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
- install destination 系

### `ChartListSourceRow`

仮想 filter / sort / keyword search の入力は `ChartListSourceRow` である。

`BuildStandardLibraryRows(...)` は `BMSFile` と `bmson_song` を結合し、BMS / bmson 共通の source row を作る。bmson は path 順で追加される。

`CreateFilterFile()` は、BMS では元の `BMSFile`、bmson では `PendingChartEntry.CreateFromBmsonSong(...)` を返す。これは既存の filter API が `BMSFile` を要求するための compatibility path であり、storage の正本ではない。

bmson library rows は全ての tree mode に無条件で混ざるわけではない。`ShouldIncludeBmsonLibraryRowsInMainView(...)` は、通常 root / folder / keyword / mode filter と `FullScanAllChartsFilterSelected` では bmson を含めるが、playlist tree active、maintenance filter、install filter では除外する。maintenance / install / playlist detail 側は、それぞれ専用 source や pending adapter の経路で bmson を扱う。

### Duplicate view

duplicate view は `DuplicateChartGroups` / `SearchDuplicateChartGroups()` を入口にし、現行 snapshot は `BmsLibraryDuplicateService.BuildSnapshot(...)` で `BMSFiles` と `BmsonSongs` を結合する。

bmson duplicate row は `DuplicateChartRow.CreateFromBmsonSong(...)` で `PendingChartEntry` display row に変換される。duplicate group の `Files` は `List<BMSFile>` のままだが、ここに含まれる bmson row は storage 正本ではなく display / operation 用 adapter である。

duplicate warning の永続的な正本はまだ BMS 側に寄っている。BMS は `BMSFile` に warning を付与し、bmson は duplicate view 用 adapter row に warning を持つため、標準一覧の bmson storage row へ duplicate warning を直接永続化する境界ではない。

## Operation model

### `ChartFile`

`ChartFile` は Models 配下のアプリ内 chart read model である。DB row ではなく、UI 操作や一覧 read model が参照する domain object として扱う。

主なフィールド:

- `Kind`: `Bms` / `Bmson`
- `Path`
- `Directory`
- `Md5`
- `Sha256`
- title / artist / level / mode
- `ChartInfo`
- `CompatibilityBmsFile`
- `BmsonSong`

`GridRowResolver.TryGetChartFile(...)` は、次の row から `ChartFile` を作る。

- `PlaylistDetailRow`
- `PlaylistDetailSourceRow`
- `LibraryChartRow`
- `BMSFile`

playlist row では `RealFile` があれば BMS として扱い、`ResolvedBmson` があれば bmson として扱う。どちらもない playlist entry は、現状 `ChartFileKind.Bms` の missing row として扱われる。

`GridRowResolver.GetRealBmsFile(...)` は、既存 View / drag-drop / preview 経路の BMS-only 互換 API として残っている。これは実体 BMS `BMSFile` だけを返し、bmson adapter は返さないため、chart 種別を判断する正本ではない。新しい operation 判定は `TryGetChartFile(...)` / `TryGetChartOperationTarget(...)` と capability を優先する。bmson owned row でも `ChartFile.CompatibilityBmsFile` には operation 用 compatibility file が入ることがあるため、handler が既存 API へ chart を渡す場合は `ChartOperationTarget` / `LibraryChartRef` 経由で扱う。

### `ChartOperationTarget`

`ChartOperationTarget` は UI command / context menu の primary target である。

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

`RepairInstalledLocation` は BMS / bmson の両方に付与される。bmson owned row は ViewModel 側の operation 用 `PendingChartEntry` adapter cache を参照し、`SearchCorrectInstallationDirectoryCharts(...)` で得た `instl_dst` / warning / suggestion を row 再生成後も維持する。この adapter cache は bmson song の参照ではなく primary hash / path の安定 key で引き継ぐため、同じ譜面が別 `bmson_song` instance として再読込されても修復候補状態を保持し、mutation target だけ現在の `bmson_song` に差し替える。model 側の修復では、BMS は `FilePathChanges`、bmson は `BmsonSongPathChanges` として同じ `LibraryMutationDelta` に載せ、`BmsLibraryStateApplier.ReplaceBmsonSongPath(...)` が `bmson_song` の path / folder と DB row を更新する。

`RunResourceHealthCheck` は capability と context menu policy の両方で bmson も対象にできる。bmson のみ選択時でも full scan menu は `RunResourceHealthCheck` capability を見て表示され、LR2IR / ranking / encoding / zero-note / audio convert などの BMS-only menu だけが後段 policy で非表示になる。

## Model-layer chart reference

### `LibraryChartRef`

model 層では `LibraryChartRef` が BMS / bmson 共通参照として使われる。

`LibraryChartRef` は次から作れる。

- `FromCompatibilityBmsFile(...)`: 実体 `BMSFile` または `PendingChartEntry` の bmson adapter
- `LR2SongDBExtended.bmson_song`
- path / md5 / sha256

`LibraryChartRef.FromPath(...)` は path 必須の fallback 参照であり、live storage owner を必ず持つわけではない。

library chart 削除は `RemoveChartFiles(...)` / `RemoveLibraryCharts(...)` が入口で、BMS / bmson の両方を `LibraryChartRef` 経由で扱う。旧 `RemoveBMSFiles(...)` wrapper と未使用の single chart move wrapper は残していない。

`ToCompatibilityBmsFile()` は、BMS では保持している `BMSFile` があればそれを返し、path-only BMS ref では null を返す。bmson では保持している `BMSFile` adapter、または `PendingChartEntry.CreateFromBmsonSong(...)` を返す。これは既存 API へ渡すための互換変換であり、bmson の storage 正本ではない。

ViewModel / UI 層の pending package 操作は `SearchInstallDestinationForPendingPackages` / `SearchInstallDestinationForPendingCharts`, `SearchMergeDestinationForPendingPackages` / `SearchMergeDestinationForPendingCharts`, `ForceInstallPendingPackages` / `ForceInstallPendingCharts`, `ManualInstallPendingPackages` / `ManualInstallPendingCharts`, `RemovePendingPackages` / `RemovePendingPackagesAll`, `RemovePendingCharts`, `ClearInstallDestinationForPendingPackages` / `ClearInstallDestinationForPendingCharts`, `SetPendingInstallDestination`, `GetPendingPackagesContainingOnlyInstalledCharts`, `DeletePendingPackageSources` を入口にする。これらは package 内 chart を扱う操作であり、BMS 専用 API ではない。

merge 先探索は package 内 chart を `ChartFiles` として扱い、BMS / bmson 共通の installed hash index で既所持 directory を採点する。installed hash index 自体は md5 / sha256 の両方を登録するが、chart 側の lookup は `PendingChartEntry.GetPrimaryLookupHash(...)` により md5 優先、sha256 fallback の primary key を使う箇所が多い。

`SearchMergeDestinationForPendingCharts(...)` は、選択 chart が pending package に属する場合、選択 chart 単体ではなく所属 package に展開して package-level merge を走らせる。mixed package の既所持先が複数 directory に分かれている場合でも、hash 一致数が単独最多の directory があればそれを package 全体の merge 先として採用する。最多 directory が同点の場合や hash 一致がない場合は、hash 由来の自動決定をせず、`MergeCandidateOnly` の resource 評価へ fallback する。

direct install / drop install の ViewModel 入口は `InstallChartPackages(...)` で、model 層の pending package install 入口は `InstallChartPackagesAuto`, `ForceInstallPendingPackages`, `InstallPendingPackagesToEstimatedDestinations` を使う。旧 `InstallBMSFilesAuto` / `InstallChartPackagesForce` / `InstallChartPackagesToEstimatedDir` / 単数 wrapper は残さない。

newly installed tree に表示される installed package history/list のクリアは `RemoveInstalledPackageRecords` / `RemoveInstalledPackageRecordsAll` を入口にする。これは chart file 自体の削除ではなく、installed package record を list から消す操作である。

library folder operation は public / user-facing 名に `BMSFolder` / `BMSDirectory` が残るが、`BuildFolderMoveDelta(...)` は `BMSFiles` と `BmsonSongs` の両方を受け、`FilePathChanges` と `BmsonSongPathChanges` を同じ mutation delta に載せる。`RenameBMSFolder(...)` / `MergeBMSDirectory(...)` は名前上 BMS だが、現在の folder move / merge path では bmson path と installed package / pending package の install destination も合わせて更新対象になる。root folder move の UI 経路は `MoveLibraryCharts(...)` から `MoveLibraryRootFolder(...)` に入り、`ChartOperationTarget` / `LibraryChartRef` を通して BMS / bmson chart を扱う。`MoveBMSRootFolder(...)` は BMSFile-based root-folder vocabulary の境界として残っている。

## Package / pending install

### `ChartPackage`

`ChartPackage` は package 内 chart の discovery container であり、BMS / bmson 混在 package を同じ単位で扱う。

pending / installed package record の永続正本は `install` table の row であり、実質的には source path と delete_parent などの package record metadata を保存する。`ChartFiles` 自体は永続化されず、DB restore 後は `ChartPackage.path` から lazy rediscovery される。

主な現行仕様:

- `ChartPackage.ChartFiles` は package 内 chart discovery の primary API であり、`List<BMSFile>` を返す。
- 明示的に `chartFiles` を渡された package ではその list を返す。
- それ以外では `PackageChartDiscoverySnapshot` を lazy build し、chart file path から `PendingChartEntry` を作る。
- `PendingCharts` は `ChartFiles.OfType<PendingChartEntry>()` である。

production code の `ChartPackage` 経由の chart-all 参照は `ChartFiles` を primary API として使う。install tree の package header も `ChartFiles` を見る。旧 `BMSFiles` alias は production 参照がなくなった段階で削除済みであり、package 内 chart list は `ChartFiles` に一本化されている。

`PackageChartDiscoverySnapshot.ChartFiles` も `List<BMSFile>` である。旧 `BmsFiles` alias は削除済みであり、ここに入る bmson は `PendingChartEntry` として `BMSFile` 互換化される。

このため、package / pending install 層では `BMSFile` が「LR2 song 由来 BMS」ではなく「install 対象 chart adapter」を表す場面がある。

### `PackageInstallEstimationSnapshot`

導入先推定 snapshot は `BMSFile` list を入力にする。

主なフィールド:

- `RepresentativeFile: BMSFile`
- `DefinedResources`
- `TargetMetadataProfile`
- `ChartCount`
- source / bundled resources
- source surface snapshot / scan metrics / cache hit
- batch source surface hit と scan backend 情報

bmson pending chart は `PendingChartEntry` としてここに入るため、推定ロジックは BMSFile API 互換 adapter に依存している。複数 package 推定では、`PackageInstallSurfaceSnapshot` や batch source surface を共有し、同じ source tree の scan / resource surface を再利用できる。

### installed directory index

installed directory index は BMS と bmson の両方を扱う。

`BuildInstalledHashToDirectoryMap(...)` は、`IEnumerable<BMSFile>` と `IEnumerable<bmson_song>` を受け取り、md5 / sha256 から installed directory を作る。

この領域ではすでに「installed chart」という考え方が入り始めているが、入力型はまだ BMS / bmson の二本立てである。map は md5 / sha256 の両方を登録する一方、個別 chart の候補判定や package resolve では primary lookup hash を使う経路もあるため、「常に両 hash で union lookup する」仕様ではない。

## Playlist

### Entry identity

playlist entry は md5 と sha256 の両方を持てる。

現行方針:

- BMS entry は md5 identity が基本。
- bmson entry は sha256-only identity が基本。
- `BMSTableEntry(BMSFile)` は、`PendingChartEntry.IsBmsonChartFile(...)` の場合に `MarkAsBmsonPlaylistIdentity(...)` を呼び、`md5 = null`, `sha256 = preferredSha256`, `Org_md5 = []` にする。

### Playlist detail resolution

playlist detail は `PlaylistDetailSourceRow` / `PlaylistDetailRow` で表示される。

主な状態:

- `RealFile`: 所持 BMS へ解決できた場合の `BMSFile`
- `ResolvedBmson`: 所持 bmson へ解決できた場合の `bmson_song`
- `IsOwned`: `RealFile` または `ResolvedBmson` が path を持つ場合 true

`GridRowResolver` は playlist row から `ChartOperationTarget` を作る際、`RealFile` を BMS、`ResolvedBmson` を bmson として扱う。

playlist detail の `RefTablesSymbols` / `RefTablesNames` は source snapshot 構築時に確定する。BMS row では `RealFile.RefTables`、bmson row や missing row では `PlaylistReferenceIndex` の md5 / sha256 lookup 結果を使う。これにより表示列と `playlist:` / `ref:` / `table:` keyword search が同じ参照情報を読む。

playlist detail 表示時の `BMSFilesView` 実体は `PlaylistDetailVirtualView` である。`PlaylistDetailSourceRow` を全件 source として保持し、可視 index だけ `PlaylistDetailRow` へ遅延 materialize する。playlist detail 中は `UseAsyncBMSFilesViewBinding` を false に切り替え、通常一覧側の async binding policy と分けている。

### Playlist への追加

`MainWindowViewModel.AddChartRowsToFolderBMSTable(...)` は、playlist table 概念として `BMSTable` 名を残しつつ、追加元の一覧 row は Chart として解決する。

通常 folder への追加では、row から `ResolvePlaylistDropCompatibilityBmsFile(...)` を通して `BMSFile` 互換 chart を作る。

- BMS row は実体 `BMSFile` を使う。
- bmson library row は `ChartOperationTarget` / `LibraryChartRef` 経由で `PendingChartEntry` に変換する。
- playlist row は通常 folder 追加では `BMSTableEntry.Duplicate()` を優先し、既存 playlist metadata を保つ。

folder table root への追加では、BMS は従来の同一ディレクトリ md5 group を使う。所持 BMS playlist row は実体 `BMSFile` へ解決されるため、root folder 自動振り分けでは新しい `BMSTableEntry(file)` を作る。missing row や bmson row など実体 BMS へ解決できない playlist row は `BMSTableEntry.Duplicate()` で既存 metadata を保つ。bmson は sha256 identity を保ち、`Org_md5` は空にする。

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
- `PendingChartEntry` は bmson pending adapter として maintenance 情報を持てる。

ただし encoding check / fix / zero-note check は capability 上 BMS 専用であり、bmson に適用しない。

### chart_info

`chart_info` は BMS / bmson 共通 metadata として扱う。

`ChartInfoBuildService` / inline chart_info pipeline は BMS と bmson の owner を別入力で受け、最終的に md5 / sha256 で chart_info を適用する。

通常一覧の chart_info 表示や sort key は `LibraryChartRow` / `ChartListSourceRow` に materialize された `ChartInfo` を使う。

### 通常一覧 sort

通常一覧の sort 正本は `LibraryChartRowSortEngine` である。

`BMSFileSortEngine` は旧 `BMSFile` 互換確認用の production helper として残っていたが、通常一覧の実行経路から外れ、参照がテストだけになったため削除済みである。  
互換・性能確認は `BmsSortCompatibilityTests` の test-local legacy baseline と `LibraryChartRowSortEngine` の比較で行う。

## UI binding / naming compatibility

通常一覧の表示実体は `LibraryChartRow` へ寄っているが、public binding 名は互換のため BMS 名を残している。

代表例:

- `BMSFilesView`
- `SelectedIndexBMSFilesView`
- `ColumnsSettingsBMSFilesView`
- `UseAsyncBMSFilesViewBinding`

内部の一覧再構築は `RefreshChartRowsView(...)` / `SetChartRowsView(...)` / `ChartRowsFolderView` など chart row 名の helper に寄せている。

このため、現在の仕様では「UI binding 名に BMS が残っていても、値は BMS / bmson 共通 chart row であり得る」と扱う。

View の control 名、menu item 名、event handler 名、ログ名にも BMS 名が残る。これらは XAML wiring や既存 handler 名との互換境界であり、機械的 rename できる場合も MethodBinder / XAML / resources / tests の参照を同時に確認する。

## 現在の抽象化済み領域

次の領域は、すでに BMS / bmson を chart として扱う入口がある。

- 通常一覧 row: `LibraryChartRow`
- 仮想 filter / sort / search source: `ChartListSourceRow`
- UI 選択解決: `GridRowResolver.TryGetChartFile(...)`
- UI operation: `ChartOperationTarget` + `ChartOperationCapabilities`
- UI operation target 解決: `GridRowResolver.TryGetChartOperationTarget(...)`
- context menu / command selection helper: `GetSelectedChartTargets(...)`
- 既存 `BMSFile` 引数 API への adapter 選択 helper: `GetSelectedCompatibilityBmsFiles(...)` / `GetSelectedPendingCompatibilityBmsFiles(...)`
- BMS 専用 operation helper: `GetSelectedBmsChartFiles(...)`
- model mutation reference: `LibraryChartRef`
- installed directory lookup: BMSFile + bmson_song の両方を hash / path で登録
- playlist entry identity: md5-only と sha256-only の両対応
- package discovery: `PendingChartEntry` adapter により BMS / bmson を pending chart として扱う

## 現在の未抽象化領域

次の領域では、まだ `BMSFile` の名前と型が chart 共通概念を兼ねている。

### `BMSFile` の二重責務

`BMSFile` は本来 BMS / LR2 song table の model だが、現行では次の用途も持つ。

- pending install chart adapter
- bmson pending adapter の基底型
- legacy filter / sort / playlist / install API の共通引数
- compatibility BMSFile 変換の受け皿

`PendingChartEntry : BMSFile` により移行は進んでいるが、`BMSFile` 型を見るだけでは「実体 BMS」なのか「chart adapter」なのか判定できない。`PendingChartEntry.IsBmsChartFile(...)` / `IsBmsonChartFile(...)` や `ChartFile.Kind` を見る必要がある。

### `ChartPackage.ChartFiles`

`ChartPackage.ChartFiles` は package 内 chart discovery の結果だが、型は `List<BMSFile>` のままである。

bmson は `PendingChartEntry` として混ざるため、`ChartPackage.ChartFiles` を「BMS のみ」と解釈してはいけない。

`ChartPackage` は `ChartFiles` を primary API として維持する。旧 `BMSFiles` alias は production 利用がないことを確認したうえで削除済みであり、テストコードにも旧 alias そのものを残すためのテストは置かない。

### model APIs の残存 BMS 名

次の API / view 名は chart 共通処理を含むが、BMS 名を残している。

- `BMSFilesView`

`RemoveChartFiles(...)` は BMS / bmson 共通の library chart 削除入口であり、旧 `RemoveBMSFiles(...)` wrapper は残さない。未使用だった single chart move wrapper も削除済みである。
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
- resource references
- source scope: library / pending / newly installed / playlist
- storage owner: `BMSFile` または `bmson_song`
- compatibility file: legacy `BMSFile` API へ渡す必要がある場合だけ作る adapter

Kind ごとの storage 境界は次の通り。

| Kind | storage owner | 永続化先 | 現行責務 |
| :--- | :--- | :--- | :--- |
| BMS | `BMSFile` | LR2 `song` / `folder`、app-owned `chart_digest_map` など | LR2 `song` row として保存できる BMS 専用 data と、移行前から残る chart helper を併せ持つ。 |
| bmson | `LR2SongDBExtended.bmson_song` | app-owned `bmson_song` | path / md5 / sha256 / title / artist / resource refs など bmson catalog 保存に特化する。 |

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

`ChartPackage.ChartFiles` の heavy lazy discovery 挙動は重要である。rename や API 置換時も、参照時に discovery が走る既存意味を不用意に変えない。

### compatibility API の扱い

移行中は `BMSFile` 引数 API が残るため、bmson は `PendingChartEntry.CreateFromBmsonSong(...)` で compatibility BMSFile に変換される。

この変換は便利だが、次のルールを守る。

- compatibility BMSFile を storage 正本として扱わない。
- BMS-only operation へ compatibility bmson を渡さない。
- playlist entry へ変換する場合は sha256-only identity を保つ。
- resource health / chart_info / install estimation のために使う場合は、元の `bmson_song` へ結果を戻す経路を確認する。

## 今後の仕様整理で守る境界

1. Storage は BMS / bmson の二本立てを維持する。
2. UI / operation の入口は chart target に寄せる。
3. BMS-only 処理は capability で明示する。
4. `BMSFile` 型を見ただけで BMS 専用と判断しない。`PendingChartEntry` の kind または `ChartFile.Kind` を確認する。
5. `ChartPackage.ChartFiles` は現状「package 内 chart adapter list」であり、BMS のみの list ではない。旧 `BMSFiles` alias は削除済みであり、package 内 chart list の入口は増やさない。
6. public binding / settings 名の BMS は互換契約として残り得る。内部 helper から段階的に chart 名へ寄せる。
7. `ChartFile` / `ChartPackage` を使う場合も、既存の LR2 互換 DB と playlist JSON / DB の永続形式は維持する。
