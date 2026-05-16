# BMS / bmson chart abstraction current state

## 目的

この文書は、現行実装における BMS / bmson の譜面抽象化の状態を記録する。

今後 `BMSFile` を `ChartFile`、`BMSPackage` を `ChartPackage` に近づけていく前に、現在どこまで chart として共通化され、どこに BMS / LR2 前提の境界が残っているかを明文化する。

この文書は移行計画ではなく、現在仕様を表す。将来の rename や抽象化を行う場合も、ここに書いた storage / read model / operation target の責務を崩さないことを前提にする。

## 用語

| 用語 | 現行実体 | 意味 |
| :--- | :--- | :--- |
| BMS chart | `BMSFile : LR2SongDB.song` | LR2 `song` / `folder` table 由来の所持 BMS 譜面。 |
| bmson chart | `LR2SongDBExtended.bmson_song` | アプリ独自 table `bmson_song` 由来の所持 bmson 譜面。 |
| pending chart | `PendingChartEntry : BMSFile` | package / pending install 上の譜面 adapter。BMS と bmson の両方を `BMSFile` 互換 API に載せる。 |
| chart row | `LibraryChartRow`, `ChartListSourceRow` | 通常一覧や仮想 filter / sort 用の read model。storage の正本ではない。 |
| owned chart ref | `OwnedChartRef` | UI 操作で使う、BMS / bmson 共通の所持譜面参照。 |
| operation target | `ChartOperationTarget` | UI command / context menu が扱う操作対象。capability を持つ。 |
| library chart ref | `LibraryChartRef` | model 層の移動 / 削除などで使う BMS / bmson 共通参照。 |
| compatibility BMSFile | `PendingChartEntry.CreateFromBmsonSong(...)` など | bmson を既存 `BMSFile` 引数 API へ渡すための一時 adapter。 |

## Storage model

### BMS

BMS の所持譜面の正本は `BMSLibrary.BMSFiles` であり、要素は `BMSFile` である。

`BMSFile` は `LR2SongDB.song` を継承しており、次の LR2 / BMS 前提の情報を直接持つ。

- `hash` / `md5`
- `path`
- `folder`
- `parent`
- title / artist / level / mode
- LR2 score / ranking / IR 関連の表示値
- BMS parser 由来の resource references
- `maintenanceInfo`
- `RefTables`

`BMSLibrary.BMSFiles` の更新時には、BMS hash index、installed chart key / directory index、parent folder cache、duplicate cache、resource health index が無効化される。

### bmson

bmson の所持譜面の正本は `BMSLibrary.BmsonSongs` であり、要素は `LR2SongDBExtended.bmson_song` である。

`bmson_song` は BMS の `song` table には入れない。path を中心に、md5 / sha256 / title / artist / mode_hint / resource references / `ChartInfo` / `MaintenanceInfo` を持つ。

`BMSLibrary.BmsonSongs` の更新時には、playlist summary owned hash snapshot、installed chart key / directory index、parent folder cache、install estimation metadata profile cache、duplicate cache、resource health index が無効化される。

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

`LibraryChartRow.Chart` は `OwnedChartRef` を返す。BMS では `BmsFile` を持ち、bmson では `BmsonSong` を持つ。pending bmson の場合は、現在の adapter path / hash を優先しつつ `BmsonSong` も保持する。

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

## Operation model

### `OwnedChartRef`

`OwnedChartRef` は UI 操作側の chart read model である。

主なフィールド:

- `Kind`: `Bms` / `Bmson`
- `Path`
- `Directory`
- `Md5`
- `Sha256`
- title / artist / level / mode
- `ChartInfo`
- `BmsFile`
- `BmsonSong`

`GridRowResolver.TryGetChartRef(...)` は、次の row から `OwnedChartRef` を作る。

- `PlaylistDetailRow`
- `PlaylistDetailSourceRow`
- `LibraryChartRow`
- `BMSFile`

playlist row では `RealFile` があれば BMS として扱い、`ResolvedBmson` があれば bmson として扱う。どちらもない playlist entry は、現状 `OwnedChartKind.Bms` の missing row として扱われる。

### `ChartOperationTarget`

`ChartOperationTarget` は UI command / context menu の primary target である。

主なフィールド:

- `Chart`: `OwnedChartRef`
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
| `RepairInstalledLocation` | Yes | No | BMS かつ path があり pending でない場合。 |
| `MoveInLibrary` | Yes | Yes | path があり missing でなく pending でない場合。 |
| `RemoveFromLibrary` | Yes | Yes | path があり missing でなく pending でない場合。 |
| `UpdateInstallDestination` | Yes | Yes | pending package row で、playlist row ではない場合。 |

## Model-layer chart reference

### `LibraryChartRef`

model 層では `LibraryChartRef` が BMS / bmson 共通参照として使われる。

`LibraryChartRef` は次から作れる。

- `BMSFile`
- `PendingChartEntry` の bmson adapter
- `LR2SongDBExtended.bmson_song`
- path / md5 / sha256

`MoveBMSFile(...)` / `RemoveBMSFiles(...)` は残っているが、内部では `MoveChartFile(...)` / `RemoveChartFiles(...)` へ委譲する経路がある。

`ToCompatibilityBmsFile()` は、BMS では `BMSFile` を返し、bmson では `PendingChartEntry.CreateFromBmsonSong(...)` を返す。これは既存 API へ渡すための互換変換であり、bmson の storage 正本ではない。

## Package / pending install

### `BMSPackage`

`BMSPackage` は名前上は BMS package だが、現行では package 内 chart の discovery container としても使われる。

主な現行仕様:

- `BMSPackage.ChartFiles` は package 内 chart discovery の primary API であり、`List<BMSFile>` を返す。
- `BMSPackage.BMSFiles` は既存呼び出し互換の alias であり、実態は `ChartFiles` と同じ list である。
- 明示的に `chartFiles` を渡された package ではその list を返す。
- それ以外では `PackageChartDiscoverySnapshot` を lazy build し、chart file path から `PendingChartEntry` を作る。
- `PendingCharts` は `ChartFiles.OfType<PendingChartEntry>()` である。

production code の `BMSPackage` 経由の chart-all 参照は `ChartFiles` を primary API として使う。`BMSFiles` は互換 alias の挙動を保証する箇所や旧 API 境界に限定し、新規の package 内 chart 処理では使わない。

`PackageChartDiscoverySnapshot.ChartFiles` も `List<BMSFile>` である。互換のため `BmsFiles` alias も残す。ここに入る bmson は `PendingChartEntry` として `BMSFile` 互換化される。

このため、package / pending install 層では `BMSFile` が「LR2 song 由来 BMS」ではなく「install 対象 chart adapter」を表す場面がある。

### `PackageInstallEstimationSnapshot`

導入先推定 snapshot は `BMSFile` list を入力にする。

主なフィールド:

- `RepresentativeFile: BMSFile`
- `DefinedResources`
- `TargetMetadataProfile`
- `ChartCount`
- source / bundled resources

bmson pending chart は `PendingChartEntry` としてここに入るため、推定ロジックは BMSFile API 互換 adapter に依存している。

### installed directory index

installed directory index は BMS と bmson の両方を扱う。

`BuildInstalledHashToDirectoryMap(...)` は、`IEnumerable<BMSFile>` と `IEnumerable<bmson_song>` を受け取り、md5 / sha256 から installed directory を作る。

この領域ではすでに「installed chart」という考え方が入り始めているが、入力型はまだ BMS / bmson の二本立てである。

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

### Playlist への追加

`MainWindowViewModel.AddEntriesToFolderBMSTable(...)` は、既存 API 名は BMSTable のままだが、現行では bmson も追加できる。

通常 folder への追加では、row から `ResolvePlaylistDropCompatibilityFile(...)` を通して `BMSFile` 互換 chart を作る。

- BMS row は実体 `BMSFile` を使う。
- bmson library row は `ChartOperationTarget` / `LibraryChartRef` 経由で `PendingChartEntry` に変換する。
- playlist row は `BMSTableEntry.Duplicate()` を優先し、既存 playlist metadata を保つ。

folder table root への追加では、BMS は従来の同一ディレクトリ md5 group を使う。bmson は sha256 identity を保ち、`Org_md5` は空にする。

## File read pipeline

chart file bytes の読み取りは `ChartFileSnapshot` / `ChartFileContentReader` で共通化されている。

この snapshot は parser / inline chart_info / maintenance 用の短命な読み取り結果であり、長期に保持する chart model ではない。`ChartFile` という名前に近いが、責務は「ファイル内容の snapshot」であり、「アプリ内の譜面実体」ではない。

将来 `ChartFile` を導入する場合、既存 `ChartFileSnapshot` と責務が混ざらないようにする。

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

## UI binding / naming compatibility

通常一覧の表示実体は `LibraryChartRow` へ寄っているが、public binding 名は互換のため BMS 名を残している。

代表例:

- `BMSFilesView`
- `SelectedIndexBMSFilesView`
- `ColumnsSettingsBMSFilesView`
- `UseAsyncBMSFilesViewBinding`
- `makeBMSFilesView(...)`

内部では `SetChartRowsView(...)` や `ChartRowsFolderView` など chart row 名の helper が混在している。

このため、現在の仕様では「UI binding 名に BMS が残っていても、値は BMS / bmson 共通 chart row であり得る」と扱う。

## 現在の抽象化済み領域

次の領域は、すでに BMS / bmson を chart として扱う入口がある。

- 通常一覧 row: `LibraryChartRow`
- 仮想 filter / sort / search source: `ChartListSourceRow`
- UI 選択解決: `GridRowResolver.TryGetChartRef(...)`
- UI operation: `ChartOperationTarget` + `ChartOperationCapabilities`
- context menu / command selection helper: `GetSelectedChartTargets(...)`
- model mutation reference: `LibraryChartRef`
- installed directory lookup: BMSFile + bmson_song の両方を hash / path で登録
- playlist entry identity: md5-only と sha256-only の両対応
- package discovery: `PendingChartEntry` adapter により BMS / bmson を pending chart として扱う

## 現在の未抽象化領域

次の領域では、まだ `BMSFile` / `BMSPackage` の名前と型が chart 共通概念を兼ねている。

### `BMSFile` の二重責務

`BMSFile` は本来 BMS / LR2 song table の model だが、現行では次の用途も持つ。

- pending install chart adapter
- bmson pending adapter の基底型
- legacy filter / sort / playlist / install API の共通引数
- compatibility BMSFile 変換の受け皿

`PendingChartEntry : BMSFile` により移行は進んでいるが、`BMSFile` 型を見るだけでは「実体 BMS」なのか「chart adapter」なのか判定できない。`PendingChartEntry.IsBmsChartFile(...)` / `IsBmsonChartFile(...)` や `OwnedChartRef.Kind` を見る必要がある。

### `BMSPackage.ChartFiles` / `BMSPackage.BMSFiles`

`BMSPackage.ChartFiles` / `BMSPackage.BMSFiles` は package 内 chart discovery の結果だが、型は `List<BMSFile>` のままである。

bmson は `PendingChartEntry` として混ざるため、`BMSPackage.BMSFiles` を「BMS のみ」と解釈してはいけない。

将来 `ChartPackage` 化する場合、既存 `BMSPackage` は `ChartFiles` を primary API、`BMSFiles` を互換 alias として残しながら呼び出し側を段階移行する。
テストコードでも、互換 alias そのものを検証するテスト以外は `ChartFiles` を使い、`BMSLibrary.BMSFiles` とは別概念として扱う。

### model APIs の BMS 名

次の API は chart 共通処理を含むが、BMS 名を残している。

- `MoveBMSFile(...)`
- `RemoveBMSFiles(...)`
- `ForceFileScanCheckBMSFiles(...)`
- `AddEntriesToFolderBMSTable(...)`
- `BMSFilesPendingInstall`
- `BMSFilesView`

一部は内部で chart 名 helper へ委譲しているが、外部名だけを見ると BMS 専用に見える。

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

## ChartFile / ChartPackage 化に向けた現在制約

### `ChartFile` に求められる性質

現行仕様から見ると、`ChartFile` は少なくとも次を表す必要がある。

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

ただし `ChartFile` は storage の正本にしてはいけない。BMS は `BMSFile`、bmson は `bmson_song` が永続正本である。

### `ChartPackage` に求められる性質

現行仕様から見ると、`ChartPackage` は少なくとも次を表す必要がある。

- package path
- lazy chart discovery snapshot
- pending chart list
- install estimation surface snapshot
- representative chart
- chart resource aggregate

`BMSPackage.BMSFiles` の heavy lazy discovery 挙動は互換上重要である。rename や API 置換時も、参照時に discovery が走る既存意味を不用意に変えない。

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
4. `BMSFile` 型を見ただけで BMS 専用と判断しない。`PendingChartEntry` の kind または `OwnedChartRef.Kind` を確認する。
5. `BMSPackage.ChartFiles` は現状「package 内 chart adapter list」であり、BMS のみの list ではない。`BMSFiles` は互換 alias として同じ list を返す。
6. public binding / settings 名の BMS は互換契約として残り得る。内部 helper から段階的に chart 名へ寄せる。
7. `ChartFile` / `ChartPackage` を導入しても、既存の LR2 互換 DB と playlist JSON / DB の永続形式は維持する。
