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
| pending chart | 現行 `PendingChartEntry : BMSFile` | package / pending install 上の譜面 adapter。現行では BMS と bmson の両方を `BMSFile` 互換 API に載せるが、これは除去対象の構造互換であり、最終形では `BMSFile` 継承ではなく package / pending 用 chart entry と legacy adapter 境界を分ける。 |
| chart row | `LibraryChartRow`, `ChartListSourceRow` | 通常一覧や仮想 filter / sort 用の read model。storage の正本ではない。 |
| operation target | `ChartOperationTarget` | UI command / context menu が扱う操作対象。capability を持つ。 |
| library chart ref | `LibraryChartRef` | model 層の移動 / 削除などで使う BMS / bmson 共通参照。 |
| compatibility BMSFile | `PendingChartEntry.CreateFromBmsonSong(...)` など | bmson を既存 `BMSFile` 引数 API へ渡すための一時 adapter。新規 API の設計中心には置かず、利用箇所を狭めて削除する。 |

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
- pending / newly installed の `PendingChartEntry`

`LibraryChartRow.Chart` は `ChartFile` を返す。BMS では storage owner として実体 `BMSFile` を `BmsFile` に持つ。bmson では storage owner として `BmsonSong` を持つ。`ChartFile` 自体は既存 `BMSFile` API 用 adapter を保持せず、必要な場合は row / `ChartOperationTarget` の compatibility 境界から取得する。pending bmson の場合は、現在の adapter path / hash を projection に使いつつ `BmsonSong` も保持する。

所持 bmson row の `CompatibilityBmsFile` は、ViewModel が持つ shared bmson chart adapter cache から供給される。`LibraryChartRow` / `PlaylistDetailSourceRow` は adapter 作成用 provider と既存 adapter 参照用 provider を分け、同じ `PendingChartEntry` adapter を共有し、インストール先修復候補や warning state を row 再生成後も保持する。ただし `PlaylistDetailSourceRow` の表示用 `ChartFile` と `ChartListSourceRow` は、adapter そのものではなく `ChartFileTransientState` provider を受け取り、mutable display snapshot を `ChartFile` に重ねる。表示用 `ChartFile` は既存 adapter があればその snapshot を読むが、存在しない場合は表示のためだけに adapter を新規作成しない。`LibraryChartRow` は adapter の `PropertyChanged` を UI へ伝搬する責務があるため、既存 adapter を読んだ場合は購読も行う。`CompatibilityBmsFile` は operation target が legacy API へ降りる時の lazy 境界である。これは現行実装の互換境界であり、最終形では adapter に状態を持たせず `ChartFile` / chart operation state / storage row のいずれかへ責務を移して削除する。

表示用の読み取りでは、`ChartFile` が subtitle / warning snapshot / install destination 表示値、maintenance / resource health 表示値を持つ。BMS では `BMSFile` 由来、bmson では `bmson_song.MaintenanceInfo` と shared compatibility adapter 由来の mutable state を `ChartFileProjection` が read model に写す。`LibraryChartRow` / `ChartListSourceRow` / playlist detail row の WARNING 表示、install destination 表示、health / encoding 表示 getter は `ChartFile` を読む。`ChartListSourceRow` / playlist detail source は `ChartFileTransientState` provider 経由で既存 state だけを読み、adapter を新規作成しない。`LibraryChartRow` は通知購読も必要な materialized view row なので、既存 adapter がある場合はその adapter を保持する。編集・修復・filter など既存 `BMSFile` API が必要な経路は引き続き `CompatibilityBmsFile` を使う。この残存経路は今後の除去対象であり、新規の chart 共通処理では `BMSFile` 互換 adapter を要求しない。

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

keyword / sort / virtual source row の基本判定は `ChartListSourceRow` の chart 共通プロパティを直接見る。`Title` / `Artist` / `Genre` / `Folder` / `Path` / `Mode` / `Level` / `Tag` / hash などの identity 系 getter は、row 生成時に作る warning なしの `ChartFile` snapshot を読む。`ChartInfo` は hydration 後の attach を反映できるよう storage owner から読む。install destination や warning 表示のように mutable state を反映する getter は、必要に応じて `ChartFileProjection` で現在 snapshot を作るが、`ChartListSourceRow` は `PendingChartEntry` provider を持たず、ViewModel から渡される `ChartFileTransientState` だけを読む。provider は既存 adapter がある場合だけ transient state を返し、表示 / sort / keyword filter のためだけには materialize しない。warning snapshot が不要な getter は provider にその旨を渡すため、install destination 表示だけで warning list を構築しない。`ChartListSourceRow` 自体は operation 用 `CompatibilityBmsFile` を公開しない。normal-library の folder tree filter は virtual source row と materialized row の両方で `NormalLibraryTreeFilter` を使い、`ChartListSourceRow` では `row.Path` / `row.Artist`、`LibraryChartRow` では `row.path` / `row.Artist` を見る。したがって folder / artist filter のために bmson `CompatibilityBmsFile` を作る経路は残さない。

bmson library rows は全ての tree mode に無条件で混ざるわけではない。`ShouldIncludeBmsonLibraryRowsInMainView(...)` は、通常 root / folder / keyword / mode filter と `FullScanAllChartsFilterSelected` では bmson を含めるが、playlist tree active、maintenance filter、install filter では除外する。maintenance / install / playlist detail 側は、それぞれ専用 source や pending adapter の経路で bmson を扱う。

通常一覧の subset view 用の仮想 filter / sort cache は `VirtualChartSubset*` helper で扱う。これは file missing / duplicate / pending install / newly installed / chart_info parse failure などの subset を `ChartListSourceRow` として並べ替える経路であり、BMS / bmson を含む chart row subset を対象にする。chart_info parse failure subset は warning 付き `ChartFile` projection をそのまま source row に渡し、表示のためだけに BMS / bmson compatibility adapter を materialize しない。performance log の scope 文字列は過去ログ検索互換のため、現状 `bms_file_subset` のまま残している。

### Duplicate view

duplicate view は `DuplicateChartGroups` / `SearchDuplicateChartGroups()` を入口にし、現行 snapshot は `BmsLibraryDuplicateService.BuildSnapshot(...)` で `BMSFiles` と `BmsonSongs` を結合する。

`DuplicateChartRow` は grouping / duplicate 判定用の path / primary lookup hash と、表示・operation の正本として `ChartFile` を持つ。bmson duplicate row は `ChartFile.BmsonSong` として storage owner を保持し、duplicate subset の仮想一覧表示は `DuplicateGroup.ChartFiles` を BMS storage row と bmson storage row に分けて `ChartListSourceRow` へ渡す。したがって duplicate tree から一覧を表示するだけなら、bmson を `BMSFile` compatibility adapter として扱う必要はない。

duplicate tree の group は `DuplicateGroup.ChartFiles` を正本にし、XAML は group の `Folders` だけを child node として表示する。旧 `DuplicateGroup.Files` facade と duplicate tree 用の `List<BMSFile>` parameter branch は削除済みであり、単一フォルダ内の重複 hash cleanup も `DuplicateGroup.ChartFiles` から削除対象を決め、ViewModel へ `ChartFile` として渡す。

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

`ChartFileProjection.FromBmsFile(...)` は通常 BMS では `BmsFile` に実体を入れ、`PendingChartEntry` の bmson adapter では `Kind=Bmson`, `BmsFile=null`, `BmsonSong=adapter owner` とする。`FromBmsonSong(...)` は storage owner として `bmson_song` を持ち、必要な場合だけ caller が `ChartFileTransientState` を渡して表示 snapshot を作る。現時点の `ChartFileTransientState` は shared compatibility adapter から作ることが多いが、projection API は adapter object ではなく subtitle / install destination / warning / health / encoding の一時状態だけを受け取る。

`ChartFileProjection` は path / hash / title / artist / level / mode / chart_info に加えて、表示に必要な subtitle / warning snapshot / install destination 表示値も集約する。これは `CompatibilityBmsFile` を即座に廃止するためではなく、表示 getter が adapter API を直接読む箇所を減らし、adapter を mutation / legacy API 境界へ閉じ込めるための段階である。

playlist row では `RealFile` があれば BMS として扱い、`ResolvedBmson` があれば bmson として扱う。どちらもない playlist entry は、現状 `ChartFileKind.Bms` の missing row として扱われる。

`GridRowResolver.GetRealBmsFile(...)` は、既存 View / drag-drop / preview 経路の BMS-only 互換 API として残っている。これは実体 BMS `BMSFile` だけを返し、bmson adapter は返さないため、chart 種別を判断する正本ではない。新しい operation 判定は `TryGetChartFile(...)` / `TryGetChartOperationTarget(...)` と capability を優先する。handler が既存 API へ chart を渡す場合は `ChartOperationTarget.CompatibilityBmsFile` / `LibraryChartRef` 経由で扱う。旧 `GridRowResolver.GetCompatibilityBmsFile(...)` は production 参照がなく、テストだけの互換 helper になっていたため削除済みである。`FOLDER` セル編集は `TryGetFolderEditChartOperationTarget(...)` で `MoveInLibrary` capability を確認し、UI thread 上で `RenameChartFolderTargetSnapshot` を作ってから `RenameChartFolder(...)` へ渡すため、owned bmson row も BMS row と同じ folder rename 経路に入るが、background task 側で bmson compatibility adapter を materialize しない。`ChartFile.BmsFile` は BMS storage owner だけを表し、bmson adapter には使わない。

### `ChartOperationTarget`

`ChartOperationTarget` は UI command / context menu の primary target である。`CompatibilityBmsFile` は lazy に解決され、target 作成時点では bmson adapter を必ずしも materialize しない。capability 判定や repository open など chart snapshot だけで済む経路は adapter 作成なしで進め、legacy `BMSFile` API へ渡す時点で初めて解決する。

主なフィールド:

- `Chart`: `ChartFile`
- `CompatibilityBmsFile`: legacy `BMSFile` API へ渡す operation 用 adapter
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

library mutation へ渡す `LibraryChartRef` は `ChartOperationTarget.ToLibraryChartRef()` で作る。`ToLibraryChartRef()` は `ChartFile` を正本にし、bmson row の削除 / 移動参照を作るだけでは compatibility adapter を新規作成しない。すでに materialize 済みの bmson adapter があっても、`LibraryChartRef` には `BmsFile` として保持せず、`BmsonSong` / path / hash の chart 参照へ戻す。legacy `BMSFile` API へ渡す必要がある handler は `ChartOperationTarget.CompatibilityBmsFile` を明示的に呼び、その時点で lazy adapter を解決する。単なる wrapper だった `ChartOperationTarget.ToCompatibilityBmsFile()` は削除済みである。

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

`RepairInstalledLocation` は BMS / bmson の両方に付与される。UI handler は `ChartOperationTarget` を ViewModel に渡し、ViewModel が UI thread 上で repair 用 snapshot を作る。snapshot の `HasTargets` は `ChartFile` だけで判定し、context menu の表示可否確認だけでは bmson compatibility adapter を materialize しない。search / clear の実行時は mutable install-destination state が必要なため、UI handler は background task に入る前に `MaterializeRepairEntries()` で snapshot の `RepairEntries` を確定させる。`RepairEntries` は `PackageChartEntry` の集合で、BMS は storage row、bmson は shared adapter または current chart から作るため、ViewModel の repair payload は `BMSFile` ではなく chart entry として扱われる。model facade と install estimation service は `PackageChartEntry` を対象に search / clear を行う。一方、fix 実行は `RepairCharts` を model 層へ渡す。`RepairCharts` は基本的に snapshot の `ChartFile` をそのまま使うが、playlist 詳細 row などで snapshot の `ChartFile` が古い導入先状態を持つ場合に備え、現在の `RepairEntries` に install destination / metadata / suggestions があればそれを `ChartFile` に重ねる。model 層は `PackageChartEntry.FromChart(...)` と `ChartFile.InstallDestination` から移動対象を組み立てるため、fix の mutation 入口は chart-native である。bmson 修復候補が shared adapter 側にある場合も warning 判定は install destination を見落とさず、background task が shared adapter cache を初期化しない。bmson owned row は ViewModel 側の operation 用 `PendingChartEntry` adapter cache を参照し、`SearchCorrectInstallationDirectoryCharts(...)` で得た `instl_dst` / warning / suggestion を row 再生成後も維持する。この adapter cache は bmson song の参照ではなく primary hash / path の安定 key で引き継ぐため、同じ譜面が別 `bmson_song` instance として再読込されても修復候補状態を保持し、mutation target だけ現在の `bmson_song` に差し替える。model 側の修復では、BMS は `FilePathChanges`、bmson は `BmsonSongPathChanges` として同じ `LibraryMutationDelta` に載せ、`BmsLibraryStateApplier.ReplaceBmsonSongPath(...)` が `bmson_song` の path / folder と DB row を更新する。

resource health と folder auto rename の UI handler も `ChartOperationTarget` を ViewModel に渡し、ViewModel が `ChartCompatibilityTargetSnapshot` を作る。resource health の強制再チェックと warning ignore / unignore は ViewModel から `ChartFile` のまま model 層へ渡し、UI / ViewModel 側では bmson compatibility adapter を materialize しない。model 層では既存 maintenance service がまだ `BMSFile` ベースで health calculation を行うため、`BMSLibrary` が `ChartFile` を BMS storage owner または bmson maintenance target へ解決する境界を持つ。一方、folder auto rename は snapshot 内の `ChartFile` を使い、plan 生成では bmson compatibility adapter を materialize しない。UI handler 側に BMSFile adapter 選択 helper は残さない。installed package record clear は package identity を落とさないため、`ChartOperationTarget` をそのまま ViewModel に渡して `ChartPackage` に解決する。

`RunResourceHealthCheck` は capability と context menu policy の両方で bmson も対象にできる。bmson のみ選択時でも full scan menu は `RunResourceHealthCheck` capability を見て表示され、LR2IR / ranking / encoding / zero-note / audio convert などの BMS-only menu だけが後段 policy で非表示になる。

## Model-layer chart reference

### `LibraryChartRef`

model 層では `LibraryChartRef` が BMS / bmson 共通参照として使われる。

`LibraryChartRef` は次から作れる。

- `FromBmsFile(...)`: BMS storage owner。移行中の `PendingChartEntry` bmson adapter が渡された場合は、adapter を `BmsFile` として保持せず `BmsonSong` / path / hash の bmson 参照へ変換する。
- `FromChartFile(...)`: `ChartFile` の storage owner を使う。BMS では `ChartFile.BmsFile`、bmson では `ChartFile.BmsonSong`、最後に path / hash fallback を使う。
- `LR2SongDBExtended.bmson_song`
- path / md5 / sha256

`LibraryChartRef.FromPath(...)` は path 必須の fallback 参照であり、live storage owner を必ず持つわけではない。

`LibraryChartRef.BmsFile` は `Kind=Bms` の storage owner 専用である。`PendingChartEntry` の bmson compatibility adapter は `BMSFile` 継承型だが、`LibraryChartRef` では BMS owner として扱わない。`FromChartFile(...)` も compatibility adapter 引数を持たず、`ChartFile` の storage owner / path / hash だけから参照を作る。

library chart 削除は `RemoveLibraryCharts(...)` が model 層入口で、BMS / bmson の両方を `LibraryChartRef` 経由で扱う。削除結果も `RemovedCharts` を正本にし、BMS / bmson の storage owner は caller 側で `Kind` に応じて分ける。旧 `RemoveBMSFiles(...)` / `RemoveChartFiles(IEnumerable<BMSFile>)` wrapper、未使用の single chart move wrapper、削除結果用の `RemovedFiles` adapter list、`LibraryChartRef.ToCompatibilityBmsFile()` は残していない。

ViewModel / UI 層の pending package 操作は `SearchInstallDestinationForPendingPackages` / `SearchInstallDestinationForPendingCharts`, `SearchMergeDestinationForPendingPackages` / `SearchMergeDestinationForPendingCharts`, `ForceInstallPendingPackages` / `ForceInstallPendingCharts`, `ManualInstallPendingPackages` / `ManualInstallPendingCharts`, `RemovePendingPackages` / `RemovePendingPackagesAll`, `RemovePendingCharts`, `ClearInstallDestinationForPendingPackages` / `ClearInstallDestinationForPendingCharts`, `SetPendingInstallDestination`, `GetPendingPackagesContainingOnlyInstalledCharts`, `DeletePendingPackageSources` を入口にする。これらは package 内 chart を扱う操作であり、BMS 専用 API ではない。UI から選択 chart を渡す `*PendingCharts` は `ChartOperationTarget` payload に寄せ、package 所属 target は `PackageChartEntry` identity で package operation へ展開する。search / merge / clear の batch 操作は UI thread 上で `PendingInstallDestinationTargetSnapshot` を作り、`HasTargets` は `ChartFile` だけで判定する。legacy loose fallback が必要な場合は UI handler が background task に入る前に `MaterializeLooseEntries()` で `LooseEntries` を確定し、background task 側では snapshot の package target identity を現行 `ChartPackagesPending` へ再解決し、loose fallback は snapshot の `LooseEntries` だけを使う。導入先セル手動編集は UI thread 上で `PendingInstallDestinationEditTargetSnapshot` を作り、snapshot 作成だけでは bmson compatibility adapter を materialize しない。`LibraryChartRow.instl_dst` は表示用 getter のみで、セル編集の書き戻しには使わない。`PackageEntry` があれば adapterless bmson も package-level API に降り、compatibility adapter を作らず entry へ書き戻す。package 外 target は `ChartOperationTarget` の lazy compatibility provider から `PackageChartEntry` を作って model 層へ渡すため、adapterless bmson の mutable install-destination state は一時 entry ではなく shared adapter 側へ書き戻す。batch 系の legacy loose fallback で mutable adapter state が必要な場合も、UI handler が background task 前に `LooseEntries` を確定する。

pending chart 削除の UI 経路は `ChartOperationTarget.Chart` を `BMSLibrary.RemovePendingCharts(IEnumerable<ChartFile>)` へ渡し、model 層では `BmsLibraryPackageInstallService.DeletePendingCharts(...)` が `ChartFile.Path` を deletion target に正規化する。BMS / adapterless bmson とも削除成功時は `PendingFileDeletionResult.ChartPathsToRemove` に載せ、pending package mutation も path で entry を落とす。削除のためだけに `PackageChartEntry.GetOrCreateCompatibilityAdapter()` を呼ばず、旧 `BMSFile` payload 入口や BMSFile 削除結果 list は残していない。pending chart deletion の正本は `ChartFile` payload である。

pending install destination クリアの UI 経路も、pending section では選択 `ChartOperationTarget` を `PendingInstallDestinationTargetSnapshot` に変換してから `MainWindowViewModel.ClearInstallDestinationForPendingCharts(...)` へ渡す。package row から作られた `LibraryChartRow` / `ChartOperationTarget` は source `PackageChartEntry` を保持し、package に属する chart は entry identity で `ChartPackage` を解決して `PackageChartEntry.ClearInstallDestination()` を呼ぶため、adapterless bmson package entry のクリアでは compatibility adapter を materialize しない。package 外に残った target は fallback として snapshot の `LooseEntries` に確定し、`ChartOperationTarget.ToPackageChartEntry()` で legacy adapter / chart fallback の順に entry 化してから ViewModel 側で model の clear API へ渡す。`ToPackageChartEntry()` 自体は package entry があればそれを最優先するが、pending batch snapshot では package target を先に分離する。

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
- `ChartPackage.GetChartAdapters()` は production 参照がなくなったため削除済みである。package 内 chart をまとめて読む入口は `ChartEntries` とし、adapter が必要な操作は対象 `PackageChartEntry` から明示的に取得する。
- 明示的に chart adapter list を渡された package でも private `PackageChartEntry` list を保持し、読み取りは `ChartEntries` から行う。
- それ以外では `PackageChartDiscoverySnapshot` を lazy build し、chart file path から `PendingChartEntry` を作る。
- 旧 `PendingCharts` view は production 参照がなく、adapterless entry を表示確認だけで materialize し得るため削除済みである。pending package 内 chart は `ChartEntries` を正本として読み、mutation / warning 書き戻しが必要な時だけ対象 entry の adapter を取得する。

production code の `ChartPackage` 経由の chart-all 参照は、読み取り系と install estimation snapshot 内部では `ChartEntries` に寄せている。互換 adapter が必要な UI / mutation target list でも package 全体の adapter snapshot は公開せず、対象 entry から adapter を取得する。公開側の pending install orchestration でも導入先推定 request / batch state は entry を正本にし、searching flag は既存 adapter または BMS storage owner がある対象にだけ反映する。warning / install destination 書き戻しも可能な範囲で `PackageChartEntry` に寄せ、実際に adapter mutation が必要な経路だけ対象 entry から adapter を取得する。旧 `BMSFiles` alias は production 参照がなくなった段階で削除済みであり、package 内 chart の正本は明示 package / path discovery ともに `PackageChartEntry` に寄せている。`ChartPackage` 内の private `ChartFiles` property と `PackageChartDiscoverySnapshot.ChartFiles` は削除済みである。

`PackageChartDiscoverySnapshot` は `PackageChartEntry` を内部正本として保持する。`PackageChartEntry` は `ChartFile` を必ず持ち、operation / mutation 用の `CompatibilityAdapter` は nullable である。path discovery で見つけた bmson はまず `ChartFileProjection.FromBmsonSong(...)` による adapterless entry として保持し、mutation 操作が adapter を要求した時だけ `PendingChartEntry` compatibility adapter を lazy materialize する。`ReplaceChartEntries(...)` は entry list を正本として置換するため、adapterless bmson entry を adapter 化せずに残せる。`ChartEntries` getter は常に entry 正本を返し、別の compatibility adapter list cache は持たない。旧 `BmsFiles` alias は削除済みであり、snapshot の読み取り経路は `PackageChartEntry` へ移し始めている。
`PackageChartEntry` は pending package 用の warning state と install destination state を持てる。BMS storage owner または compatibility adapter がある場合は従来どおり `BMSFile` の warning / install destination fields へ委譲し、adapterless bmson では entry 内の pending snapshot を `ChartFile` projection に重ねる。後から `GetOrCreateCompatibilityAdapter()` が呼ばれた場合は entry 側 warning と resolved install destination / representative title / artist / install destination suggestions を materialized adapter へコピーする。これにより、installed / single-file / nested-chart warning の付与、起動時 pending warning 初期化、手動導入先設定、導入先推定の低信頼 warning / suggestions 書き戻しは、それだけでは adapterless bmson を materialize しない。resource health の strict warning 判定や suggestion popup open state など、`BMSFile` の mutable UI state に依存する経路はまだ adapter writeback 境界として残る。

`ChartPackage` は `RemoveChartEntries(...)` / `ApplySingleFileInstallDestination(...)` / `ApplyDirectoryInstallDestination(...)` / `ReplaceChartEntries(...)` を持ち、operation / mutation 側が adapter list を直接状態更新する箇所を増やさないための所有者境界になり始めている。TreeView header など表示側は `DisplayTitle` を使い、`DisplayTitle` は `ChartEntries` / `ChartFile` から作るため bmson package header 表示だけでは compatibility adapter を materialize しない。XAML から package 内 chart list に直接 binding しない。count / empty 判定は `ChartEntries.Count` を直接見るため、compatibility adapter materialize を要求しない。選択 chart が package に属するかの判定は caller 側で既存 adapter reference と `ChartFile.Path` を見る。`RemoveChartEntries(...)` は削除対象を `PackageChartEntry` predicate で判定し、path 削除でも adapterless entry を materialize しない。install destination apply helpers は `PackageChartEntry` を受け取り、実ファイル移動済みの対象 entry だけ adapter writeback する。install destination clear は package wrapper を経由せず対象 `PackageChartEntry.ClearInstallDestination()` を呼ぶ。`ReplaceChartEntries(...)` は chart entry list の明示置換として扱うため、adapterless entry を保持する。production / test ともに未使用だった predicate-based `RemoveChartAdapters(...)`、path-based `RemoveChartAdaptersByPath(...)`、adapter-list replacement の `ReplaceChartAdapters(...)`、membership helper の `ContainsChartAdapter(...)` は削除済みである。count / empty だけの薄い wrapper だった `GetChartAdapterCount()` / `IsChartAdapterEmpty()` と、install destination clear だけの薄い wrapper だった `ClearChartAdapterInstallDestinations()` も削除済みである。
`ChartPackage(BMSFile)` / `ChartPackage(IEnumerable<BMSFile>)` constructor は production API から削除済みである。package を明示 chart list 付きで構築する場合は `ChartPackage.FromChartEntries(...)` を使い、`BMSFile` から package entry に落とす必要がある境界では caller が `PackageChartEntry.FromCompatibilityAdapter(...)` を明示する。tests 側だけで必要な fixture construction は `ChartPackageTestExtensions.CreatePackage(...)` に閉じ込め、production に BMSFile-list constructor を互換 API として残さない。
auto install discovery で directory scan 済みの package を明示 chart list 付きで作る時は `ChartPackage.FromChartEntries(...)` を使う。recursive metadata discovery でも BMS は health 判定用に `PendingChartEntry` を作るが、bmson は `PackageChartEntry.FromPath(...)` により adapterless entry として保持し、resource 判定は `ChartResourceSnapshot` を読む。`SearchChartPackagesRecursivelyWithMetadata(...)` / auto install grouping で既知 chart list を package 化する境界も `PackageChartEntry` を渡し、package construction の内部 API では `IEnumerable<BMSFile>` を要求しない。これにより、scan 結果の adapterless bmson entry を `BMSFile` list に戻さず package 正本として保持できる。既存 UI / mutation が adapter を必要とした時だけ対象 entry から materialize する。

`BMSLibrary` / package install service の pending package 読み取り経路は `ChartEntries` / `ChartFile` へ移行中である。導入先推定 snapshot、advanced cleanup の「全 chart が installed 済みか」判定、追加 bmson 抽出、resource-only merge の hash / path 読み取りは package entry を読む。pending package の導入先推定で package 内 chart を already-installed / missing に分ける時も、判定は `PackageChartEntry.Chart` の `PrimaryLookupHash` で行い、manual single-package estimation / manual batch estimation / background batch preparation の partition 時点では adapterless bmson を materialize しない。pending estimate request / batch state は `PackageEntries` / `MissingEntries` / `AlreadyInstalledEntries` を持ち、`BMSFile` adapter list を状態として保持しない。snapshot と scoring の入力、および installed directory index からの混在 package 導入先解決は `MissingEntries`、導入先 / low-confidence warning / suggestions の writeback も package target では `PackageChartEntry` を使う。`SEARCHING` 表示が必要な場合も entry から既存 adapter または BMS storage owner だけを参照し、adapterless bmson を materialize しない。estimated install batch plan の installed / duplicate / install target 分類も `PackageChartEntry.Chart.PrimaryLookupHash` と `ChartFile.InstallDestination` を読み、install work package 生成など adapter が必要な時だけ materialize する。pending package の full-selection 判定、safe cleanup 用の hash snapshot も entry / chart path / primary hash を読む。advanced resource overwrite の一時的な path-only destination 書き込み / 復元は `CaptureInstallDestinationState(...)` / `SetInstallDestinationPathOnly(...)` / `RestoreInstallDestinationState(...)` を使い、warning / suggestions を壊さず adapterless bmson entry を materialize しない。manual estimate の package membership 判定は duplicate hash を package containment と誤認しないよう、既存 adapter reference / selected path と `PackageChartEntry.Chart.Path` の一致だけを見る。root folder move / merge / deleted-folder cleanup の install destination 判定も `ChartFile.InstallDestination` を先に読む。pending regroup 成功ログの file count / install destination metadata 判定も `ChartEntries` を読む。playlist reference 同期など、adapter へ状態を書き戻す必要がある経路は対象 entry から adapter を取得する。package path から既知 chart list や validation target list を作る経路は mutation target を返すため `PackageChartEntry.GetOrCreateCompatibilityAdapter()` を呼ぶ。一方、package 内 target の一致判定は adapterless bmson を materialize しないよう、既存 adapter の reference / path を見た後に `ChartFile.Path` で fallback する。merge destination 探索は、installed directory index で導入先が解決できる成功パスでは `ChartEntries` に導入先 metadata を書き戻し、adapterless bmson entry を materialize しない。merge 実行後の song / bmson_song upsert と maintenance target も `Repackage.ChartEntries` から storage owner を分けるため、移動済み adapterless bmson entry を materialize しない。resource estimation fallback が必要な場合も package-level install estimation の low-confidence state は entry に書き戻す。install 実行時の移動対象 chart 選別と auto naming の chart 入力も `ChartEntries` / `ChartFile.Path` で対象 entry を絞ってから adapter を取得し、移動成功後の package chart set は移動した `PackageChartEntry` で置き換える。install execution result は `AddedEntries` / `AddedCharts` を正本にし、BMS storage row は `AddedBmsFiles`、adapter-backed bmson は `AddedBmsonAdapters`、bmson storage row は `AddedBmsonSongs` として分ける。adapterless bmson は `AddedBmsonSongs` として返るため、install result / bmson_song upsert / installed package registration / maintenance target / resource lookup cache update に含めるだけでは adapter 化しない。通常 install では inline chart_info persist が bmson_song upsert も担い、estimated/deferred install では deferred state apply 用に先に bmson_song を upsert する。移動対象から外れた entry は install result / installed package registration へ含めず、adapter 化もしない。ここで扱う `BMSFile` は LR2 song storage row または package chart adapter として用途別に分け、list identity や list mutation は `ChartPackage` 側に閉じ込める。estimated/deferred install の state apply context も `AddedBmsFiles` / `AddedBmsonAdapters` / `AddedBmsonSongs` の分割を維持し、混合 `AddedFiles` list を状態として持たない。
library directory merge の準備結果である `LibraryMergeResult` は、source chart を単一の `BMSFile` list として保持しない。BMS storage owner は `SourceBmsFiles`、bmson storage owner は `SourceBmsonSongs` に分け、`Repackage.ChartEntries` も storage owner から組み立てる。したがって installed bmson を merge source として扱うだけでは `PendingChartEntry` compatibility adapter を作らず、existing-hash snapshot も `ChartFile.PrimaryLookupHash` を読む。folder move / merge に伴う pending package 内 chart の install destination rewrite は `LibraryInstallDestinationChange.Entry` で `PackageChartEntry` を直接更新するため、adapterless bmson entry は導入先が移動元配下でも rewrite のためだけに materialize しない。deleted-folder cleanup は `PackageChartEntry.ClearInstallDestination()` で metadata / suggestions / install-estimation warning も含めて clear し、adapterless bmson entry を materialize しない。
BMS 系 chart 専用の保留 snapshot は、`ChartEntries` の `ChartFile.Kind` と拡張子で BMS-format chart だけを選び、既存 adapter または `ChartFile.BmsFile` を優先して返す。これにより、zero-note / invalid-extension 対象外の adapterless bmson は snapshot 取得だけでは materialize されず、BMS storage owner がある entry も不要な compatibility adapter を作らない。
nested chart warning も `ChartEntries` の `ChartFile.Path` で入れ子判定してから対象 entry の adapter だけ materialize する。package 直下の adapterless bmson は警告付与対象外なので adapter 化しない。
auto install workflow の pending / auto-install 分類では、resource reference count は `PackageChartEntry.ResourceSnapshot` を先に読み、SingleBmsFile / SingleBmsonFile / resource health warning を書き戻す時だけ対象 entry の adapter を materialize する。installed 判定は `ChartFile` callback で行い、AlreadyInstalled warning を書き戻す entry だけ adapter を materialize する。
pending package から選択 chart を削る mutation delta も、削除対象判定は `PackageChartEntry` の既存 adapter reference / `ChartFile.Path` で行う。残す entry は `ReplaceChartEntries(...)` で package へ書き戻すため、残存する adapterless bmson も削除される adapterless bmson も、この再構成だけでは adapter 化しない。
force install の normal install 上書き確認は、package に `ChartFile.InstallDestination` を持つ entry があるかで判定する。確認ダイアログを出すかどうかだけなら adapter mutation target は不要なので、adapterless bmson entry は確認判定だけでは materialize しない。install 成功後の `instl_dst` clear はまだ adapter writeback 境界である。
estimated install の resource-only merge 後に installed package history/list へ追加する display package は、既存 library row から `PackageChartEntry` を組み立てる。BMS は storage owner `BMSFile` から entry を作るが、bmson は `bmson_song` から `ChartFile` entry を作るため、installed display package の作成だけでは bmson compatibility adapter を materialize しない。
install table load result は pending warning 初期化の対象 adapter list を公開しない。warning count と package / stale row の結果だけを返し、adapter list は warning 書き戻しの内部処理に閉じる。起動時 pending package の installed 判定は `ChartFile` callback で行い、AlreadyInstalled / SingleFile / strict resource warning の書き戻しが必要な entry だけ adapter を materialize する。strict resource warning 判定は resource reference を持つ entry に限る。
pending package tree の「導入先を開く」は、package 内 chart の `ChartFile.InstallDestination` と `PrimaryLookupHash` を読む。これは explorer を開く先を解決するだけの UI 読み取りなので、adapterless bmson entry を `BMSFile` に materialize しない。pending package の install destination clear は `PackageChartEntry` の install destination state を clear し、adapterless bmson entry も entry 内 state と `ChartFile` projection を更新するため、clear だけでは compatibility adapter を materialize しない。
playlist reference の pending package / installed package 反映は `ChartFile.Md5` / `Sha256` で一致判定する。BMS entry と既存 compatibility adapter には従来どおり `BMSFile.RefTables` を書き戻すが、adapterless bmson entry は一致しても `PendingChartEntry` adapter を作らず、表示は `PlaylistReferenceIndex` と chart identity から解決する。参照テーブルと一致しない adapterless bmson entry だけでなく、一致する adapterless bmson entry も playlist reference refresh や install 後 reference 反映だけでは materialize しない。reload / replace で旧 table 分を除去する場合は既存 adapter だけを mutation 対象にし、新規 table の表示は index 更新へ寄せる。
pending / newly installed package の一覧表示 row は `PackageChartEntry` から作り、既存 adapter がある entry だけ `BMSFile` row として扱う。adapterless bmson entry は `bmson_song` から `LibraryChartRow` を作るため、表示するだけでは compatibility adapter を materialize しない。virtual package subset の source snapshot も `PackageChartEntry` を既存 adapter / BMS row と adapterless bmson song に分け、`ChartListSourceRow.BuildStandardLibraryRows(...)` へ二列で渡すため、package view の sort / keyword filter に入るだけでは adapterless bmson entry を materialize しない。
pending package install / resource overwrite 前の再生停止対象は、package 内 `PackageChartEntry` の既存 BMS compatibility adapter だけを snapshot する。これは BMS 再生中の file lock / process を閉じるための UI 側ガードであり、bmson や adapterless entry を再生停止対象にするためだけに materialize しない。
split した pending package の regroup 判定は `PackageChartEntry.Chart` の path / primary hash / install destination を読む。全 entry が同じ expected destination に解決できることを確認した後、regrouped package に書き戻す entry だけ compatibility adapter を materialize する。regroup 後の warning 再初期化も起動時 pending warning 初期化と同じく `PackageChartEntry` を走査し、BMS では health status を必要に応じて更新し、bmson では AlreadyInstalled / SingleBmsonFile / strict resource / nested chart warning のように書き戻しが必要な entry だけ adapter を materialize する。健康な adapterless bmson directory entry は warning 再初期化だけでは adapter 化しない。
pending package フォルダー削除の集計と mutation delta は `PackageChartEntry.Chart.Path` を使う。削除成功時も削除済み chart path を `PendingFileDeletionResult.ChartPathsToRemove` に返し、BMS-format 専用の invalid extension / zero-note rename も pending package 更新へ渡す payload は `ChartPathsToRemove` に正規化する。`BuildPendingPackageMutationDelta(...)` は path だけで対象 entry を落とすため、adapterless bmson entry は成功 / 失敗どちらでも folder 削除後処理だけでは materialize されない。

このため、package / pending install 層では `BMSFile` が「LR2 song 由来 BMS」ではなく「install 対象 chart adapter」を表す場面がある。これは現行最大の構造互換であり、`PendingChartEntry : BMSFile` 廃止の前提として、package discovery snapshot / install estimation / pending tree が chart-native entry を扱うようにする。

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

`BuildInstalledHashToDirectoryMap(...)` は、`IEnumerable<BMSFile>` と `IEnumerable<bmson_song>` を受け取り、md5 / sha256 から installed directory を作る。

この領域ではすでに「installed chart」という考え方が入り始めているが、入力型はまだ BMS / bmson の二本立てである。map は md5 / sha256 の両方を登録する一方、個別 chart の候補判定や package resolve では `ChartFile.PrimaryLookupHash` を使うため、「常に両 hash で union lookup する」仕様ではない。installed-only package destination / package-level installed directory scoring / pending destination validation は package 内 chart を `PackageChartEntry` として列挙し、最終的な mutation 対象としてのみ `CompatibilityAdapter` を返す。installed-only resource overwrite の skip 診断で原因 chart を探す helper は `ChartFile` を返すため、adapterless entry でも path / primary hash をログへ出せる。

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
- `Chart`: playlist detail row の chart read model

`PlaylistDetailSourceRow` は source snapshot 構築時に `Chart` を作る。`RealFile` があれば BMS、`ResolvedBmson` があれば bmson、どちらもなければ playlist missing の metadata chart として扱う。`PlaylistDetailRow` は source row の `Chart` を引き継ぎ、missing row の手動 level 編集や source row の entry chart_info patch 時には `Chart` を作り直す。これにより、view row からも source row と同じ chart_info / identity snapshot を `GridRowResolver` に渡せる。

`GridRowResolver` は playlist row から `ChartOperationTarget` を作る際、row の `Chart.Kind` と `RealFile` / `ResolvedBmson` / `CompatibilityBmsFile` を組み合わせて source scope と capability を決める。

playlist detail row は view row materialization だけでは bmson compatibility adapter を作らない。`PlaylistDetailSourceRow` / `PlaylistDetailRow` の `CompatibilityBmsFile` は operation target が legacy API へ降りる時の lazy 境界であり、表示用 `ChartFile` は bmson storage row と `ChartFileTransientState` だけから作る。ViewModel が渡す transient state provider は既存 shared adapter がある場合だけ repair / warning 表示 state を写し、存在しない adapterless bmson row は表示するだけでは `PendingChartEntry` へ変換しない。直接テストなどで provider がない場合のみ、既存 adapter provider から transient state を組み立てる fallback を持つ。

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
- resource health / folder auto rename の chart-domain 境界: ViewModel の `ChartCompatibilityTargetSnapshot.Charts`
- resource health の既存 `BMSFile` maintenance service 境界: `BMSLibrary` の `ChartFile` target resolution
- installed location repair の selection 境界: ViewModel の `RepairInstalledLocationTargetSnapshot.Charts`
- installed location repair の search / clear 境界: ViewModel の `RepairInstalledLocationTargetSnapshot.RepairEntries`
- installed location repair の fix mutation 境界: ViewModel の `RepairInstalledLocationTargetSnapshot.RepairCharts` から model の `FixInstallationDirectoryCharts(IEnumerable<ChartFile>)` へ渡す
- pending package / pending chart の context menu batch 操作: `GetSelectedChartTargets(..., isPendingSection: true)` から `PendingInstallDestinationTargetSnapshot` を作り、background task へ渡す
- pending install destination のセル編集: `ChartOperationTarget` から `PendingInstallDestinationEditTargetSnapshot` を作り、background task へ渡す
- 所持 bmson row の共有 adapter cache: `sharedBmsonChartAdaptersByKey` と `BmsonChartAdapterProvider`
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

### `ChartPackage` adapter materialization boundary

`PackageChartDiscoverySnapshot.ChartFiles` と `ChartPackage.GetChartAdapters()` は削除済みで、discovery snapshot と package は `ChartEntries` を正本にする。package 全体を `List<BMSFile>` snapshot として取り出す production API は残さない。

adapter が必要な操作は、対象 `PackageChartEntry.GetOrCreateCompatibilityAdapter()` を呼ぶ境界で明示する。bmson は `PendingChartEntry` として materialize される可能性があるため、materialized adapter を「BMS のみ」と解釈してはいけない。

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

source scope と legacy `BMSFile` API 用 compatibility adapter は現行 `ChartFile` ではなく、row / `ChartOperationTarget` 側が持つ。これは移行中の互換境界であり、最終形では chart 共通 operation が `ChartOperationTarget.CompatibilityBmsFile` を必要としない状態にする。

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

移行中は `BMSFile` 引数 API が残るため、bmson は `PendingChartEntry.CreateFromBmsonSong(...)` で compatibility BMSFile に変換される。

この変換は最終形では残さない。現行で残っている間は、次のルールを守る。

- compatibility BMSFile を storage 正本として扱わない。
- BMS-only operation へ compatibility bmson を渡さない。
- playlist entry へ変換する場合は sha256-only identity を保つ。
- resource health / chart_info / install estimation のために使う場合は、元の `bmson_song` へ結果を戻す経路を確認する。

## Active migration plan

2026-05-20 時点の方針は、層を薄く順番に chart-native 化するのではなく、production に残っている compatibility 境界を境界単位で閉じることを優先する。

進捗指標は `BMSFile` 参照総数ではない。`BMSFile` は最終形でも BMS storage row / LR2 `song` row / BMS-only operation として残る。進捗は次の production compatibility surface が消えているかで見る。

- `CompatibilityBmsFile` / `CompatibilityAdapter` / `GetOrCreateCompatibilityAdapter()` / `FromCompatibilityAdapter(...)`
- `PendingChartEntry : BMSFile`
- bmson を `BMSFile` adapter に materialize しないと表示・検索・sort・playlist・resource health・package operation が進まない経路
- production では不要になった旧名 wrapper / 互換 API

### 作業粒度

1 commit は、原則として 1 つの production compatibility boundary を閉じる単位にする。helper 1 個、local rename 1 個、test-only cleanup 1 個だけでは commit 単位にしない。

中間 helper を増やして旧実装を包むより、最終 owner へ直接責務を移す。責務の移動先は `ChartFile`、`PackageChartEntry`、`ChartOperationTarget`、`LibraryChartRef`、`BMSFile`、`bmson_song` のいずれかを優先する。`Compatibility*` の別名 wrapper や、最終的に消す facade を新設しない。

大きめの compiler-driven 変更は許容する。ただし、settings 名、UI 文言、DB table / column、playlist JSON / DB、LR2 互換 schema は別契約として扱い、抽象化だけを理由に変更しない。

### 残っている主な compatibility 境界

1. resource health / maintenance warning 境界  
   `ChartResourceSnapshot.Create(ChartFile)` は存在するが、pending package warning 判定や maintenance service にはまだ `BMSFile` adapter を要求する経路が残る。resource health は BMS / bmson 共通なので、BMS-only encoding / zero-note と分離し、`BMSFile` adapter を warning / ignore state の正本にしない。入力と保存先は `ChartFile`、`ChartResourceSnapshot`、maintenance row、または既存 storage owner の責務として整理する。

2. playlist reference / playlist detail 境界  
   playlist detail 表示は `ChartFile` / `PlaylistReferenceIndex` に寄っており、owned bmson の参照表示のためには adapter を materialize しない。残る境界は、pending / package 側の ref table state apply / writeback や legacy adapter API へ降りる操作に限定する。source of truth は playlist entry / `PlaylistReferenceIndex` / chart identity に寄せ、表示のために `BMSFile.RefTables` 風 adapter を materialize しない。

3. package / pending install entry 境界  
   `ChartPackage.ChartEntries` と `PackageChartEntry.Chart` は package 内 chart の正本になっている。一方、`PackageChartEntry.CompatibilityAdapter` / `FromCompatibilityAdapter(...)` / `GetOrCreateCompatibilityAdapter()` が残り、install execution、resource warning、state apply、initialization の一部が adapter を要求する。pending warning / install destination / suggestions / resource snapshot は `PackageChartEntry` と storage row に寄せ、adapter は legacy `BMSFile` API に降りる境界から外す。

4. UI row / operation target 境界  
   `LibraryChartRow`、`PlaylistDetailSourceRow`、`PlaylistDetailRow`、`GridRowResolver`、`ChartOperationTarget` は `ChartFile` を持つが、legacy API 用に `CompatibilityBmsFile` を lazy materialize できる。BMS-only command は `ChartFile.Kind == Bms` と `ChartFile.BmsFile` で制限し、chart-common command は `CompatibilityBmsFile` を要求しない。

5. `PendingChartEntry : BMSFile` 境界  
   `PendingChartEntry` は現行最大の構造互換であり、package / pending chart、bmson adapter、BMSFile clone、warning / install destination state、chart identity helper が混在している。resource / playlist reference / package entry / UI operation の adapter 呼び出し理由を減らしたら、最後の cleanup としてではなく前倒しで BMSFile 継承を解体する。

### 推奨実装順

1. resource health を chart-common 化する。`BuildResourceHealthWarnings`、pending package warning 判定、warning ignore / unignore、resource health index / projection の入力を `BMSFile` adapter ではなく `ChartFile` / `ChartResourceSnapshot` / maintenance row / storage owner へ寄せる。BMS-only encoding / zero-note は `BMSFile` 境界へ残す。
2. playlist reference 表示と playlist detail の adapter mutation を分離する。pending / bmson の ref 表示は `PlaylistReferenceIndex` と chart identity から解決し、`BMSFile.RefTables` を持つ adapter へ書き戻さない。
3. `PackageChartEntry` から compatibility adapter surface を削る。`CompatibilityAdapter` / `FromCompatibilityAdapter(...)` / `GetOrCreateCompatibilityAdapter()` を呼ぶ理由を install execution、state apply、initialization、resource warning の順に消し、entry は `ChartFile` と package transient state の owner にする。
4. UI row / operation target から `CompatibilityBmsFile` を削る。`GridRowResolver` は chart operation target と BMS-only target を分け、`ChartOperationTarget` は legacy adapter provider を持たない。
5. `PendingChartEntry : BMSFile` を解体する。必要な static helper は final owner へ移し、pending / package chart は `PackageChartEntry` / `ChartFile` / storage row を正本にする。`PendingChartEntry` を残す場合でも production から参照されない legacy adapter に限定する。
6. テストと docs を cleanup する。production で使われない互換 API をテストのために残さず、adapter materialization を検証したい場合は test-local helper に閉じ込める。

### 実装メモ

- resource health の warning 計算は mutation から分離する。`BMSFile` の warning set を更新する入口は BMS / compatibility adapter へ降りる境界として残すが、一覧 filter / projection / pending package 判定は `ChartFile` と maintenance row から `ChartWarning` list を計算する。
- bmson は parser 直後に `bmson_song.MaintenanceInfo` が存在していても、resource health の defined / existing count が未計算なら placeholder とみなし、`ChartResourceSnapshot` と現在の filesystem 状態から一時 maintenance snapshot を作る。hash が現在の `bmson_song.md5` と一致する場合だけ既存の ignored flag を引き継ぎ、hash が古い maintenance row は使わない。旧実装ではこの判定のために `PendingChartEntry` adapter を作って `SetHealthStatus()` していたが、adapter materialization は状態計算の副作用だったため移植しない。
- pending package discovery、install table load、pending regroup 後の warning 再初期化では、bmson の `ResourceHealth` warning は `PackageChartEntry` に直接書き戻す。BMS entry は従来どおり `BMSFile` adapter の `SetHealthStatus()` / strict check を使う。これは BMS の parser / health cache と bmson の `bmson_song` resource snapshot の owner がまだ異なるためであり、bmson を BMS adapter に寄せ戻す旧挙動は残さない。
- BMS chart については、現時点では `BMSFile.HasValidMaintenanceInfoSnapshot` を持つ場合だけ resource health projection の入力にする。BMS の strict resource scan / encoding / zero-note はまだ BMS-only maintenance boundary へ残し、今回の chart-common 計算へ無理に混ぜない。
- `ResourceHealthIndexSnapshot.ActiveTargets` / `IgnoredTargets` は `ChartFile` を返す。旧実装の `BMSFile` target list は UI filter へ BMS adapter を渡すための構造であり、bmson を adapterless に扱う最終形では chart row projection に直接渡す。
- playlist reference の表示 lookup は `PlaylistReferenceIndex.Find(ChartFile)` / `Find(LibraryChartRef)` を入口にする。md5 / sha256 の文字列 pair は index 内部の lookup detail として残し、通常一覧 source row / materialized row / playlist detail row は chart identity を渡す。旧実装では、pending / installed package の adapterless bmson が playlist reference に一致した場合、表示用 `RefTables` を持たせるためだけに `PendingChartEntry` adapter を materialize していた。現行実装では、BMS / 既存 adapter だけに `BMSFile.RefTables` を書き戻し、adapterless bmson の表示は index へ任せるため、この副作用は移植しない。md5 優先、sha256 fallback の一致順序は維持する。
- `PackageChartEntry.GetPlaylistReferenceAdapter(...)` は削除済み。playlist reference 境界で必要なのは既存 materialized adapter を読むことだけであり、playlist reference のために adapter を作る入口を残さない。

### 完了判定

- `BMSFile` は実体 BMS / LR2 `song` row / BMS-only operation に閉じている。
- bmson を扱う通常表示、playlist detail、package / pending、duplicate、install destination、resource health の production 経路が `BMSFile` adapter 生成を要求しない。
- `PendingChartEntry` が `BMSFile` を継承していない、または production から参照されない legacy adapter に限定されている。
- `ChartPackage.ChartEntries` / `PackageChartEntry.Chart` が package 内 chart の正本であり、package-level adapter list API が復活していない。
- `CompatibilityBmsFile` / `CompatibilityAdapter` / `GetOrCreateCompatibilityAdapter()` / `FromCompatibilityAdapter(...)` は production surface に残っていない。
- storage は BMS `BMSFiles` / bmson `BmsonSongs` の二本立てを維持し、playlist / LR2 DB / settings の永続互換を壊していない。

## 今後の仕様整理で守る境界

1. Storage は BMS / bmson の二本立てを維持する。
2. UI / operation の入口は chart target に寄せる。
3. BMS-only 処理は capability で明示する。
4. `BMSFile` 型を見ただけで chart 共通処理に使わない。現行互換 adapter が残る間は `PendingChartEntry` の kind または `ChartFile.Kind` を確認し、最終的には `BMSFile` を BMS storage row / BMS-only 処理に閉じ込める。
5. package 内 chart の読み取りは `ChartEntries` を入口にする。adapter materialization は対象 entry 単位に限定し、package-level adapter list API を再導入しない。
6. settings 名と UI 文言の BMS は互換契約として残す。内部 helper / log / operation symbol は必要に応じて chart 名へ寄せる。
7. `ChartFile` / `ChartPackage` を使う場合も、既存の LR2 互換 DB と playlist JSON / DB の永続形式は維持する。
