# BMS / bmson chart abstraction current state

## 目的

この文書は、現行実装における BMS / bmson の譜面抽象化の状態と、今後の ChartFile domain model 化で守る境界を記録する。

現在の `BMSLibrary` は、UI / operation surface を `ChartFile` に寄せている一方で、所持譜面集合の runtime 正本はまだ `BMSFiles` と `BmsonSongs` の二本立て storage row collection に残っている。今後の本筋は、アプリ内の所持譜面集合を `ChartFile` を要素とする collection へ移し、BMS / bmson storage row は永続化 owner と BMS / bmson 固有 producer として扱うことである。

旧 `devdocs/plan/bmson/bms-bmson-chart-abstraction-migration-plan.md` は、bmson 段階導入時の履歴資料であり、現在進行中の active plan ではない。今後の作業判断はこの文書を正とし、古い計画書へ進捗や方針を追記しない。

最終ゴール:

- アプリ全体で譜面を扱う入口を `ChartFile` domain model ベースにする。
- `BMSLibrary` 内の所持譜面集合の runtime 正本を `ChartFile` collection にする。
- `BMSFile` は BMS 専用処理と LR2 `song` / `folder` 永続化に寄せる。
- `LR2SongDBExtended.bmson_song` は bmson 専用処理と `bmson_song` 永続化に寄せる。
- `BMSFiles` / `BmsonSongs` は DB 永続化 owner / 互換 property / BMS・bmson 固有 producer の境界として残し、chart 共通 snapshot / index / mutation の primary source にはしない。
- `PendingChartEntry : BMSFile` や `CompatibilityBmsFile` のような構造互換 adapter は最終形として残さない。
- production から使われない旧名 wrapper / 互換 API / test-only production code は残さない。
- 設定値名と UI 文言は、永続設定移行やユーザー体験の変更を避けるため、抽象化作業だけを理由には変えない。

## 用語

| 用語 | 現行実体 | 意味 |
| :--- | :--- | :--- |
| BMS chart | `BMSFile : LR2SongDB.song` | LR2 `song` / `folder` table 由来の所持 BMS 譜面。 |
| bmson chart | `LR2SongDBExtended.bmson_song` | アプリ独自 table `bmson_song` 由来の所持 bmson 譜面。 |
| storage row | LR2 `song` row, `LR2SongDBExtended.bmson_song` row | DB table へ保存する永続化単位。BMS の現行 in-memory owner は `BMSFile : LR2SongDB.song` だが、DB commit は LR2 `song` row 型として行う。bmson は app-owned `bmson_song` row。 |
| application chart file | `ChartFile` | 本アプリで譜面を操作・表示するための domain model。直接の DB 永続化型ではなく、Kind と storage owner を持つ。`BMSLibrary` runtime では owned chart collection の entry としても使い始めている。 |
| owned chart collection | `OwnedChartCollectionState` | `BMSLibrary` 内に導入済みの所持譜面 `ChartFile` identity collection。現状は storage owner を保持する lazy cache で、snapshot 作成時に owner の現在値を再投影する。unregister / install upsert とは差分同期するが、全ての snapshot / index の primary source にはまだなっていない。 |
| pending chart | `PackageChartEntry` / `ChartPackage.ChartEntries` | package / pending install 上の譜面。production の正本は `ChartFile` を持つ package entry であり、`BMSFile` 継承 adapter ではない。 |
| chart row | `LibraryChartRow`, `ChartListSourceRow` | 通常一覧や仮想 filter / sort 用の read model。storage の正本ではない。 |
| operation target | `ChartOperationTarget` | UI command / context menu が扱う操作対象。capability を持つ。 |
| library chart ref | `LibraryChartRef` | model 層の移動 / 削除などで使う BMS / bmson 共通参照。 |
| compatibility BMSFile | なし | bmson を既存 `BMSFile` 引数 API へ渡すために使っていた旧 adapter は削除済み。`BMSFile` 引数 API は BMS storage row / BMS-only 処理に限定し、bmson は `ChartFile` / `bmson_song` / `PackageChartEntry` を正本にする。 |

## Storage model

### BMS

現状、BMS の所持譜面は `BMSLibrary.BMSFiles` に保持され、要素は `BMSFile` である。これは LR2 `song` / `folder` 永続化 owner として残す。`BMSLibrary` には `OwnedChartCollectionState` も導入済みで、current installed source の `ChartFile` snapshot はここを通るが、永続化 owner と一部の index / maintenance target はまだ `BMSFiles` を直接参照する。

`BMSFile` は `LR2SongDB.song` を継承しており、次の LR2 / BMS 前提の情報を直接持つ。

- `hash` / `md5`
- `sha256`
- `path`
- `folder`
- `parent`
- title / artist / level / mode
- `BMSScore` attachment と LR2 score / ranking / IR 関連の storage state
- BMS parser 由来の resource references
- `maintenanceInfo`

`BMSFile` は LR2 score row の attachment と listener lifecycle を持つが、clear / rank / rate / ranking などの一覧表示・sort 用 getter は持たない。score 表示値は `ChartScoreSnapshot` を介して `ChartFile` / row read model 側で解決する。

LR2 song の provisional note count は base `LR2SongDB.song.karinotes` 列として残す。`BMSFile.notes` のような chart_info / 表示語彙に見える alias は production 参照がなく、BMS storage row と chart_info の境界を曖昧にするため残さない。譜面メタデータの notes 表示・検索は `ChartInfo` / `ChartInfoDisplaySnapshot` を通す。

`BMSFile.Level` / `BMSFile.Folder` は LR2 song row 由来の表示値を読むだけの getter として残す。手動編集可能な chart-common mutable property ではないため setter は持たない。FOLDER セル編集は `GridRowResolver.TryGetFolderEditChartOperationTarget(...)` から `RenameChartFolder(...)` へ入る chart operation として扱い、LEVEL 編集は playlist detail row の entry-level 編集に限定する。

playlist reference の mutable cache は `BMSFile` には残さない。表示列の `RefTablesSymbols` / `RefTablesNames` は歴史的な UI / column 名として残るが、実データは `PlaylistReferenceIndex` と chart identity から解決する。

`BMSFile` は BMS storage owner に紐付く structured warning collection と BMS 専用 producer からの mutation helper（重複、zero-note、LR2 path encoding など）を持つ。ただし warning の digest / tooltip / display text は `BMSFile` の表示 alias としては持たず、`ChartWarningCollection` / `ChartWarningProjectionFormatter` と row read model 側で解決する。production から使われない copy / replace-all 用の `BMSFile` 互換 wrapper は残さず、collection 全体の検証は `ChartWarningCollection` を直接対象にする。

`BMSFile` は `maintenanceInfo` snapshot の所有・由来管理、BMS parser 由来の resource refs、encoding producer を持つが、`encoding` / `WAVHealth` / `BGAHealth` / `MovieHealth` / `StagefileHealth` / `BannerHealth` / `BackbmpHealth` の表示 alias は持たない。resource health の production 更新は `ChartFile` 共通計算から `maintenanceInfo` へ merge し、encoding / reload は BMS-only producer が更新する。`ChartFileProjection.FromBmsFile(...)` は有効な `maintenanceInfo` snapshot がある場合だけ health / encoding を `ChartFile` に投影し、lazy placeholder を表示正本として作らない。`maintenanceInfo` 内部の変更を UI へ反映する場合は、変更した producer が `BMSFile.NotifyMaintenanceInfoChanged(...)` で `maintenanceInfo` を明示通知し、`LibraryChartRow` が `WAVHealth` / `encoding` などの row 表示 property へ展開する。

`BMSFile` には `instl_dst` / `InstallDestinationTitle` / `InstallDestinationArtist` / `InstallDestinationSuggestions` / popup state などの install destination runtime property を残さない。install destination は BMS / bmson のどちらにも適用される chart 共通の runtime / pending state であり、通常一覧 / playlist detail / loose chart 操作では ViewModel の chart 共通 `ChartFileTransientState` cache、`BMSLibrary` の model-side runtime overlay、`PackageChartEntry` projection state を正本にする。`ChartFileProjection.FromBmsFile(...)` は BMS storage owner から install destination を投影しない。BMS owner を持つ chart に install destination state が必要な場合は、caller が `ChartFileProjection.WithPackageState(...)`、`ChartFileTransientState`、または model-side runtime overlay で明示的に重ねる。`PackageChartEntry` は BMS owner を持つ entry でも install destination を entry projection state として持ち、BMS owner へ同期しない。起動時 cleanup / folder cleanup / repair の `LibraryMutationDelta` は entry がない installed BMS storage owner でも `BMSFile` を直接書き換えず、適用後 `ChartFile` snapshot を one-shot buffer と model-side runtime overlay に反映する。entry 付きの変更は `PackageChartEntry` へ書き戻され、installed bmson row にも install destination 相当の model-side writeback はない。

`BMSLibrary.BMSFiles` の丸ごと置換は DB load / external refresh の境界として扱い、owned chart collection と派生 index を full invalidate する。通常の unregister と install upsert は owned chart collection へ差分 mutation を適用する。まだ全 mutation と派生 index が同一 collection から同期されているわけではないため、`CreateLibraryChartRefSnapshotUnsafe()`、playlist owned hash snapshot、installed lookup などは順次 collection view / index に寄せる。

### bmson

現状、bmson の所持譜面は `BMSLibrary.BmsonSongs` に保持され、要素は `LR2SongDBExtended.bmson_song` である。これは app-owned `bmson_song` table の永続化 owner として残す。BMS と同じく current installed source の `ChartFile` snapshot は owned chart collection を通るが、DB reload / external replacement と一部の subset projection では `BmsonSongs` が直接入力になる。

`bmson_song` は BMS の `song` table には入れない。永続列として path / folder / md5 / sha256 / title / subtitle / artist / genre / level / mode_hint / banner / backbmp / stagefile / preview_music / updated_at を持つ。

resource references、`HasFreshResourceReferences`、`MaintenanceInfo` は `bmson_song` の runtime-only 情報であり、`bmson_song` table の列としては保存されない。DB から hydrate した `maintenance` や、parser 直後の resource references を同一オブジェクトへ載せるための in-memory owner として扱っている。`chart_info` は BMS / bmson storage owner の property としては持たず、通常一覧 / playlist detail / zero-note 判定は `BMSLibrary.ResolveChartInfo(...)` provider と session `ChartInfoIndex` を正本にする。inline build / backfill の parser 結果は result row / DB commit / index update へ流し、storage owner へ短命 cache として attach しない。

`BMSLibrary.BmsonSongs` の丸ごと置換も DB load / external refresh の境界として扱い、owned chart collection と派生 index を full invalidate する。通常 mutation では `ChartFile.Kind == Bmson` の owned chart entry と `bmson_song` storage row を同じ意味で更新する方向へ移行中である。parent folder cache version の変更通知は BMS / bmson の両方で発行する。

parent folder cache は UI / settings の語彙としては BMS root / BMS directory の名前を残すが、candidate rebuild は `getBMSDirectories()` と installed `ChartFile` snapshot を入力にする。したがって、bmson だけを含む root でも、譜面が存在する root として candidate に残る。

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

`LibraryChartRow.Chart` は `ChartFile` を返す。BMS / bmson の storage owner は row 内部に閉じ、外部 consumer は `GetBmsStorageOwner()` / `GetBmsonStorageOwner()` または `Chart` を入口にする。`ChartFile` 自体は既存 `BMSFile` API 用 adapter を保持せず、`LibraryChartRow` / `PlaylistDetailSourceRow` / `PlaylistDetailRow` / `ChartOperationTarget` も owned bmson の compatibility adapter surface を公開しない。pending / newly installed の bmson は `PackageChartEntry.Chart` と `bmson_song` owner から表示する。

所持 chart row の一時表示状態は、ViewModel が持つ chart 共通 `ChartFileTransientState` cache から供給される。これは install destination、候補、warning snapshot、health / encoding など、DB storage row へ直接保存しない UI / repair 表示 state を row 再生成後も保持するための cache である。旧実装では bmson について同じ目的で shared `PendingChartEntry` adapter を保持していたが、現行実装では adapter object を row / operation target に公開せず、BMS / bmson とも `ChartFileTransientState` を `ChartFile` projection に重ねる。install destination 操作で更新する cache は install-estimation warning だけを部分 projection し、resource health など別 category の warning や health / encoding 値を古い snapshot で固定しない。

表示用の読み取りでは、`ChartFile` が subtitle / warning snapshot / install destination 表示値、maintenance / resource health 表示値を持つ。BMS / bmson の storage owner 由来 state に加え、install destination repair / pending loose edit などで発生した runtime state は `ChartFileTransientState` として overlay する。`LibraryChartRow` / `ChartListSourceRow` / playlist detail row の WARNING 表示、install destination 表示、health / encoding 表示 getter は `ChartFile` を読む。`ChartListSourceRow` / playlist detail source は provider 経由で既存 transient state だけを読み、表示 / sort / keyword filter のためだけには adapter を新規作成しない。

表示列は BMS / bmson で共通化されているものが多い。

- title / artist / genre / folder / path
- md5 / sha256
- level / mode
- chart_info 系列
- resource health
- warning 表示
- playlist reference (`RefTablesSymbols` / `RefTablesNames`)

playlist reference 表示は、BMS / bmson を分けず `ChartFile` identity から解決する。playlist entry の md5 / sha256 から作る `PlaylistReferenceIndex` を通常一覧 row / virtual source row / playlist detail source row に注入し、lookup は md5 優先、見つからない場合だけ sha256 fallback とする。`BMSLibrary` の playlist reference 更新も BMS storage row の `RefTables` cache へは書き戻さず、UI 更新は `PlaylistReferenceIndex` の version / table 更新通知と chart row の playlist reference projection invalidation で行う。BMS storage row の playlist reference cache は削除済みであり、表示 / sort / keyword search / `BMSLibrary` mutation の正本は `PlaylistReferenceIndex` である。`RefreshReferenceDisplayForTable(...)` / `RemoveReferenceBMSTables(...)` などの BMS 名は playlist table の歴史的語彙として残るが、実装は index の replace / remove / synchronize だけを行い、BMS storage row へ playlist reference を書き戻さない。

playlist 追加時の LR2 `org_md5` 補助探索は `GetPlaylistOrgMd5sForChart(...)` から入るが、現状の実体は `GetMD5sOfTheSameSong(BMSFile)` の BMS storage row / WAV health heuristic であり、bmson chart では空を返す。これは playlist reference index とは別の LR2 互換補助処理で、Chart entrypoint を持つが producer は BMS-only boundary として残る。細かい実装メモとして、同一ディレクトリ内の候補を返す際は選択元 chart の WAV health ではなく候補 chart 自身の WAV health を見る。旧実装の候補判定は選択元の health を再利用しており、同じ folder 内の resource missing chart を org_md5 候補に含め得たため改善する。

score / ranking 系の表示値は `ChartScoreSnapshot` として `ChartFile` に投影され、`LibraryChartRow` / `ChartListSourceRow` は `BMSFile` の score 表示 getter を直接読まない。通常一覧の source row は `BMSLibrary.ScoreSnapshot` provider を優先し、LR2 score は md5、beatoraja score は sha256 から解決する。`BMSFile` 側には移行中の `bmsScore` attachment と listener lifecycle だけを残し、`clear` / `rank` / `score` / `rateDouble` / `rankingString` などの表示・sort getter は row read model 側へ閉じる。通常 library の所持 bmson は現時点で LR2 score storage を持たないため、path がある chart は `NO_PLAY`、path が無い chart は `NO_SONG` の既定 snapshot になる。これは「score 表示 API の入口は Chart だが、score の storage producer は BMS/LR2 境界に残る」という整理である。

`ChartFileProjection.FromBmsFile(...)` / `FromStorageOwner(...)` の既定は `BMSFile.bmsScore` を読まない。score を `BMSFile.bmsScore` attachment から読む必要がある BMS-only 入口は `includeScoreSnapshot:true` を明示する。chart 共通処理で score が必要な場合は、`BMSLibrary.ResolveChartScoreSnapshot(...)` のような provider から `ChartScoreSnapshot` を投影する。`ChartListSourceRow` は provider なしで `BMSFile.bmsScore` を fallback として読まない。provider がない場合に利用できる score は、入力 `ChartFile` に明示的に投影済みの `ChartFile.Score` だけである。

一方、次の列は BMS / LR2 storage 由来に依存するため、所持 bmson では空または既定値になりやすい。

- LR2 BMSID / diff name

### `ChartListSourceRow`

仮想 filter / sort / keyword search の入力は `ChartListSourceRow` である。

`BuildStandardLibraryRows(...)` は `ChartFile` list を入口にして BMS / bmson 共通の source row を作る。通常 library root / folder view では、ViewModel が `BMSFile` / `bmson_song` storage owner から `ChartListSourceRow` を直接作り、bmson は path 順で追加する。これは full `ChartFile` list を中間に作らず、source row 自体を chart identity read model として扱う hot path である。

keyword / sort / virtual source row の基本判定は `ChartListSourceRow` の chart 共通プロパティを直接見る。`Title` / `Artist` / `Genre` / `Folder` / `Path` / `Mode` / `Level` / `Tag` / hash などの identity 系 getter は、通常 library の owner-backed row では storage owner から直接読む。projection-only subset では caller supplied `ChartFile` snapshot を読む。`ChartInfo` は ViewModel から渡される chart_info projection provider を優先し、通常一覧では `BMSLibrary.ResolveChartInfo(sha256, md5)` の index 解決を読む。provider が無い projection-only 経路では、caller が明示的に `ChartFileProjection.WithChartInfo(...)` で重ねた `sourceChart.ChartInfo` を fallback として読む。storage owner から現在 projection を作る場合も `BMSFile.ChartInfo` / `bmson_song.ChartInfo` へは戻らない。install destination や warning 表示のように mutable state を反映する getter は、通常 library では ViewModel から渡される `ChartFileTransientState` を読み、pending / newly installed package subset では `PackageChartEntry.Chart` を live provider として読む。通常 library row の `Chart` は provider-backed state の stale cache を避けるため都度 current projection を作り、package row だけ `PackageChartEntry.ProjectionVersion` で cache する。warning snapshot が不要な getter は provider にその旨を渡すため、install destination 表示だけで warning list を構築しない。`ChartListSourceRow` 自体は operation 用 `CompatibilityBmsFile` や storage owner の `BmsFile` / `BmsonSong` property を公開せず、外部 consumer は `Chart` または chart-common getter を読む。normal-library の folder tree filter は virtual source row、non-virtual fallback の `ChartFile` source、materialized row のいずれでも `NormalLibraryTreeFilter` を使い、`ChartListSourceRow` では `row.Path` / `row.Artist`、`ChartFile` source では `chart.Path` / `chart.Artist`、`LibraryChartRow` では `row.path` / `row.Artist` を見る。したがって folder / artist filter のために bmson `CompatibilityBmsFile` を作る経路は残さない。

bmson library rows は全ての tree mode に無条件で混ざるわけではない。`ShouldIncludeBmsonLibraryRowsInMainView(...)` は、通常 root / folder / keyword / mode filter と `FullScanAllChartsFilterSelected` では bmson を含めるが、playlist tree active、maintenance filter、install filter では除外する。maintenance / install / playlist detail 側は、それぞれ専用 source、`PackageChartEntry` / `ChartFile`、`ChartFileTransientState` / `PlaylistReferenceIndex` の経路で bmson を扱う。

通常一覧の subset view 用の仮想 filter / sort cache は `VirtualChartSubset*` helper で扱う。これは file missing / duplicate / pending install / newly installed / chart_info parse failure などの subset を `ChartListSourceRow` として並べ替える経路であり、BMS / bmson を含む chart row subset を対象にする。BMS-only subset である garbled / garble fixed / unregistered / zero-note も UI source 境界では `ChartFile` snapshot へ投影してから `ChartListSourceRow` を作る。zero-note 一覧は `ChartFile.Kind == Bms` と `BMSLibrary.ResolveChartInfo(...)` 由来の `notes == 0` を見る。これは実ファイルを読み直して zero-note 不整合 warning を再判定する `RunZeroNoteCheck` / `RecheckZeroNoteWarnings` が BMS parser / BMS file content 境界なので BMS-only capability のまま残るためである。chart_info parse failure subset は warning 付き `ChartFile` projection をそのまま source row に渡し、表示のためだけに BMS / bmson compatibility adapter を materialize しない。performance log の scope 文字列は過去ログ検索互換のため、現状 `bms_file_subset` のまま残している。

### Duplicate view

duplicate view は `DuplicateChartGroups` / `SearchDuplicateChartGroups()` を入口にし、現行 snapshot は `BmsLibraryDuplicateService.BuildSnapshot(...)` で `ChartFile` snapshot を受け取る。`BMSLibrary` は `BMSFiles` と `BmsonSongs` を installed chart snapshot に投影してから duplicate service へ渡す。

`DuplicateChartRow` は grouping / duplicate 判定用の path / primary lookup hash と、表示・operation の正本として `ChartFile` を持つ。BMS duplicate row の storage owner は `ChartFile.GetBmsStorageOwner()` から読む。bmson duplicate row は `ChartFile.GetBmsonStorageOwner()` で storage owner へ降りる。bmson duplicate row は `PendingChartEntry` compatibility adapter を materialize しない。duplicate subset の仮想一覧表示は `DuplicateGroup.ChartFiles` をそのまま `ChartListSourceRow` へ渡すため、projection-only warning も落とさない。

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
- private storage owner (`GetBmsStorageOwner()` / `GetBmsonStorageOwner()` からだけ取り出す)

`GridRowResolver.TryGetChartFile(...)` は、次の row から `ChartFile` を解決する。

- `PlaylistDetailRow`
- `PlaylistDetailSourceRow`
- `LibraryChartRow`

`ChartFile` 生成は `ChartFileProjection` に集約する。`LibraryChartRow.Chart` と playlist detail row の `Chart` は BMS storage owner / bmson storage owner / package entry chart / playlist row metadata から値を取り出し、最終的な `ChartFile` の組み立ては projection helper を通す。これにより、BMS / bmson / playlist missing の mapping drift を避ける。

通常一覧 row と playlist detail row は、どちらも row 側に `ChartFile` snapshot を持つ。`GridRowResolver` は `LibraryChartRow` / `PlaylistDetailSourceRow` / `PlaylistDetailRow` では row の `Chart` をそのまま返し、resolver 内では再構築しない。raw `BMSFile` は chart-common resolver の入力にしない。BMS player や LR2/IR などの BMS-only 境界では raw `BMSFile` を扱えるが、chart operation target が必要な caller は `LibraryChartRow`、playlist row、または明示的な `ChartFileProjection.FromBmsFile(...)` で chart row / chart projection を作って渡す。

`ChartFileProjection.FromBmsFile(...)` は BMS storage owner 専用であり、常に `Kind=Bms` / `BmsFile=file` の chart を作る。`FromBmsonSong(...)` は storage owner として `bmson_song` を持つ。どちらも storage owner の runtime `ChartInfo` は投影しない。chart_info は `BMSLibrary.ResolveChartInfo(...)` provider、playlist entry metadata、または caller が明示的に `ChartFileProjection.WithChartInfo(...)` で重ねる表示 metadata として扱う。caller が持つ runtime state は `ChartFileProjection.WithTransientState(...)` または `FromBmsonSong(..., ChartFileTransientState, ...)` で重ねる。`ChartFileTransientState` は adapter object ではなく subtitle / install destination / warning / health / encoding の一時状態だけを受け取る。install destination は「空にした」という明示 state も必要なため、`HasInstallDestinationProjection` を持つ。

`ChartFileProjection` は path / hash / title / artist / level / mode に加えて、表示に必要な subtitle / warning snapshot / install destination 表示値も集約する。chart_info だけは owner projection で勝手に正本化せず、read model 側の provider / explicit metadata として重ねる。UI row / operation target からは compatibility adapter surface を削除済みであり、表示 getter は adapter API ではなく `ChartFile` / `ChartFileTransientState` を読む。BMS owner-backed row でも transient overlay を通せるため、loose chart の repair / manual install destination state を BMS storage owner へ戻さなくても表示へ反映できる。

BMS / bmson storage row から warning なしの installed / standard snapshot を作る境界も `ChartFileProjection.FromStorageRows(...)` / `FromBmsFiles(...)` / `FromBmsonSongs(...)` に集約する。current installed source は owned chart collection を優先し、任意 subset や追加 bmson だけのように collection view としてまだ表現できない入力だけ storage row projection を使う。Model / ViewModel 側は BMS row list と bmson row list を直接結合する実装を増やさず、storage owner から chart domain model へ投影する責務を `ChartFileProjection` に閉じる。

playlist row では `ChartFile.Kind` を chart 種別の正本にし、BMS / bmson の storage owner が必要な場合だけ `GetBmsStorageOwner()` / `GetBmsonStorageOwner()` で降りる。どちらの storage owner もない playlist entry は、現状 `ChartFileKind.Bms` の missing row として扱われる。細かい実装メモとして、`BMSTableEntry(ChartFile)` の bmson playlist identity は owner 有無ではなく `ChartFile.Kind == Bmson` で決める。これは metadata-only / ownerless bmson projection でも sha256 playlist identity を維持し、BMS 用 `org_md5` を混ぜないためである。

`GridRowResolver.TryGetBmsPlayerFile(...)` は、既存 View / preview 経路の BMS player 用 API として残っている。これは BMS player が現在 BMS storage row だけを再生対象にするための BMS-only 境界であり、bmson adapter は返さない。chart 種別を判断する正本ではない。operation 判定は `TryGetChartFile(...)` / `TryGetChartOperationTarget(...)` と capability を優先する。chart-common mutation は `ChartFile` / `LibraryChartRef` / `PackageChartEntry` を使い、BMS player / BMS-only handler だけが `GetBmsStorageOwner()` や `TryGetBmsPlayerFile(...)` を見る。旧 `GridRowResolver.GetRealBmsFile(...)` / `GetCompatibilityBmsFile(...)` は production 参照がなく、BMSFile 互換 helper を戻す口になっていたため削除済みである。hash / repository SHA256 / display title などの chart row getter も raw `BMSFile` fallback を持たず、row / read model が `ChartFile` を公開している場合だけ値を返す。BMS player controls が raw `BMSFile` から表示文字列を読む必要がある箇所は `GetBmsPlayerDisplayTitle(...)` などの BMS-only helper に分ける。細かい実装メモとして、旧 display helper は BMS player と chart row resolver を兼ねていたが、bmson adapter を chart-common resolver へ戻さないため役割を分離した。旧 `GetLR2IRSongInfoCache(BMSFile)` は `GridRowResolver.GetLr2BmsId(raw BMSFile)` も fallback にしていたが、raw BMS storage row に LR2BMSID はなく、この fallback は実質 dead path だったため移植せず、BMS storage owner 入口では MD5 のみを見る。playlist row は引き続き entry snapshot の `lr2_bmsid` fallback を使う。`FOLDER` セル編集は `TryGetFolderEditChartOperationTarget(...)` で `MoveInLibrary` capability を確認し、UI thread 上で `RenameChartFolderTargetSnapshot` を作ってから `RenameChartFolder(...)` へ渡すため、owned bmson row も BMS row と同じ folder rename 経路に入るが、background task 側で bmson compatibility adapter を materialize しない。`ChartFile` の BMS storage owner は private で、明示 helper 経由でだけ読む。

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

library mutation へ渡す `LibraryChartRef` は `ChartOperationTarget.ToLibraryChartRef()` で作る。`ToLibraryChartRef()` は `ChartFile` を正本にし、bmson row の削除 / 移動参照を作るだけでは compatibility adapter を新規作成しない。`ChartOperationTarget.ToPackageChartEntry()` も、source `PackageEntry` がなければ `ChartFile` から package entry を作る。BMS storage owner が必要な後段処理だけ、作成された entry の `Chart.GetBmsStorageOwner()` へ明示的に降りる。単なる wrapper だった `ChartOperationTarget.ToCompatibilityBmsFile()` と `CompatibilityBmsFile` surface は削除済みである。

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

`RepairInstalledLocation` は BMS / bmson の両方に付与される。UI handler は `ChartOperationTarget` を ViewModel に渡し、ViewModel が UI thread 上で repair 用 snapshot を作る。snapshot の `HasTargets` は `ChartFile` だけで判定し、context menu の表示可否確認だけでは bmson compatibility adapter を materialize しない。search / clear の実行時も、UI handler は background task に入る前に `MaterializeRepairEntries()` で snapshot の `RepairEntries` を確定させるが、`RepairEntries` は `PackageEntry` または `ChartFile` projection から作る `PackageChartEntry` の集合であり、bmson loose target の lazy compatibility provider は呼ばない。BMS は storage row、bmson は operation target 作成時点の `ChartFile` snapshot を修復 payload として扱う。model facade と install estimation service は `PackageChartEntry` を対象に search / clear を行う。一方、fix 実行は `RepairCharts` を model 層へ渡す。`RepairCharts` は基本的に snapshot の `ChartFile` をそのまま使うが、playlist 詳細 row などで snapshot の `ChartFile` が古い導入先状態を持つ場合に備え、現在の `RepairEntries` に install destination / metadata / suggestions があればそれを `ChartFile` に重ねる。model 層は `PackageChartEntry.FromChart(...)` と `ChartFile.InstallDestination` から移動対象を組み立てるため、fix の mutation 入口は chart-native である。bmson 修復候補は `ChartFile` に投影済みの state を見るため、background task が shared adapter cache を初期化しない。model 側の修復では、BMS / bmson の path 更新をどちらも `LibraryMutationDelta.ChartPathChanges` として返し、`BmsLibraryStateApplier` が BMS storage row と `bmson_song` に分けて DB row を更新する。修復後の maintenance 再計算対象は BMS / bmson とも `LibraryFixInstallationResult.MaintenanceCharts` の `ChartFile` として返し、BMSFile-only target list へは戻さない。

resource health と folder auto rename の UI handler も `ChartOperationTarget` を ViewModel に渡し、ViewModel が `ChartOperationTargetSnapshot` を作る。resource health の強制再チェックと warning ignore / unignore は ViewModel から `ChartFile` のまま model 層へ渡し、UI / ViewModel 側では bmson compatibility adapter を materialize しない。model 層では `BmsLibraryMaintenanceService` が `ChartFile` を受け、service 内部で BMS storage row と `bmson_song` に分け、resource health projection / ignore state は `ChartFile` から解決する。一方、folder auto rename は snapshot 内の `ChartFile` を使い、plan 生成では bmson compatibility adapter を materialize しない。UI handler 側に BMSFile adapter 選択 helper は残さない。installed package record clear は package identity を落とさないため、`ChartOperationTarget` をそのまま ViewModel に渡して `ChartPackage` に解決する。

`RunResourceHealthCheck` は capability と context menu policy の両方で bmson も対象にできる。bmson のみ選択時でも full scan menu は `RunResourceHealthCheck` capability を見て表示され、LR2IR / ranking / encoding / zero-note / audio convert などの BMS-only menu だけが後段 policy で非表示になる。

## Model-layer chart reference

### `LibraryChartRef`

model 層では `LibraryChartRef` が BMS / bmson 共通参照として使われる。

`LibraryChartRef` は次から作れる。

- `FromBmsFile(...)`: BMS storage owner 専用。`BMSFile` subtype から bmson を復元する互換分岐は production から削除済みである。
- `FromChartFile(...)`: `ChartFile` の storage owner を使う。BMS では `GetBmsStorageOwner()`、bmson では `GetBmsonStorageOwner()`、最後に path / hash fallback を使う。
- `LR2SongDBExtended.bmson_song`
- path / md5 / sha256

`LibraryChartRef.FromPath(...)` は path 必須の fallback 参照であり、live storage owner を必ず持つわけではない。

`LibraryChartRef` の storage owner は `GetBmsStorageOwner()` / `GetBmsonStorageOwner()` でだけ読む。bmson は `FromBmsonSong(...)` または `FromChartFile(...)` で参照を作り、`BMSFile` 継承型 adapter から bmson owner を復元しない。

library chart 削除は `RemoveLibraryCharts(...)` が model 層入口で、BMS / bmson の両方を `LibraryChartRef` 経由で扱う。削除結果も `RemovedCharts` を正本にし、BMS / bmson の storage owner は caller 側で `Kind` に応じて分ける。旧 `RemoveBMSFiles(...)` / `RemoveChartFiles(IEnumerable<BMSFile>)` wrapper、未使用の single chart move wrapper、削除結果用の `RemovedFiles` adapter list、`LibraryChartRef.ToCompatibilityBmsFile()` は残していない。

ViewModel / UI 層の pending package 操作は `SearchInstallDestinationForPendingPackages` / `SearchInstallDestinationForPendingCharts`, `SearchMergeDestinationForPendingPackages` / `SearchMergeDestinationForPendingCharts`, `ForceInstallPendingPackages` / `ForceInstallPendingCharts`, `ManualInstallPendingPackages` / `ManualInstallPendingCharts`, `RemovePendingPackages` / `RemovePendingPackagesAll`, `RemovePendingCharts`, `ClearInstallDestinationForPendingPackages` / `ClearInstallDestinationForPendingCharts`, `SetPendingInstallDestination`, `GetPendingPackagesContainingOnlyInstalledCharts`, `DeletePendingPackageSources` を入口にする。これらは package 内 chart を扱う操作であり、BMS 専用 API ではない。UI から選択 chart を渡す `*PendingCharts` は `ChartOperationTarget` payload に寄せ、package 所属 target は `PackageChartEntry` identity で package operation へ展開する。search / merge / clear の batch 操作は UI thread 上で `PendingInstallDestinationTargetSnapshot` を作り、`HasTargets` は `ChartFile` だけで判定する。package target は source `PackageChartEntry` を保持し、background task 側では snapshot の package target identity を現行 `ChartPackagesPending` へ再解決する。package 外 target は snapshot の `LooseEntries` として `ChartOperationTarget.ToPackageChartEntry()` から確定するが、bmson では lazy compatibility provider を呼ばず `ChartFile` projection から一時 entry を作る。導入先セル手動編集は UI thread 上で `PendingInstallDestinationEditTargetSnapshot` を作り、snapshot 作成だけでなく `GetOrCreateChartEntry()` 時も loose bmson compatibility adapter を materialize しない。`LibraryChartRow.instl_dst` は表示用 getter のみで、セル編集の書き戻しには使わない。`PackageEntry` があれば adapterless bmson も package-level API に降り、compatibility adapter を作らず entry へ書き戻す。package 外 target の mutable install-destination state は operation target 作成時点の `ChartFile` snapshot にある state だけを使い、後段処理のために shared adapter を作って書き戻す旧挙動は残さない。

pending chart 削除の UI 経路は `ChartOperationTarget.Chart` を `BMSLibrary.RemovePendingCharts(IEnumerable<ChartFile>)` へ渡し、model 層では `BmsLibraryPackageInstallService.DeletePendingCharts(...)` が `ChartFile.Path` を deletion target に正規化する。BMS / adapterless bmson とも削除成功時は `PendingFileDeletionResult.ChartPathsToRemove` に載せ、pending package mutation も path で entry を落とす。削除のためだけに `PackageChartEntry.GetOrCreateCompatibilityAdapter()` を呼ばず、旧 `BMSFile` payload 入口や BMSFile 削除結果 list は残していない。pending chart deletion の正本は `ChartFile` payload である。

pending install destination クリアの UI 経路も、pending section では選択 `ChartOperationTarget` を `PendingInstallDestinationTargetSnapshot` に変換してから `MainWindowViewModel.ClearInstallDestinationForPendingCharts(...)` へ渡す。package row から作られた `LibraryChartRow` / `ChartOperationTarget` は source `PackageChartEntry` を保持し、package に属する chart は entry identity で `ChartPackage` を解決して `PackageChartEntry.ClearInstallDestination()` を呼ぶため、adapterless bmson package entry のクリアでは compatibility adapter を materialize しない。package 外に残った target は fallback として snapshot の `LooseEntries` に確定し、`ChartOperationTarget.ToPackageChartEntry()` で BMS storage owner だけを adapter entry 化し、bmson は `ChartFile` projection から entry 化して ViewModel 側で model の clear API へ渡す。`ToPackageChartEntry()` 自体は package entry があればそれを最優先するが、pending batch snapshot では package target を先に分離する。

pending install destination search / merge search の UI 経路も、pending section では選択 `ChartOperationTarget` を `PendingInstallDestinationTargetSnapshot` に変換してから ViewModel に渡す。package row 由来 target は `PackageChartEntry` identity で package に展開し、package-level estimation / merge search を走らせる。package 外に残った target は background task 前に snapshot の `LooseEntries` として確定し、ViewModel 側で model 層へ渡す。この fallback は loose chart として処理し、再び path で pending package に吸い込まない。model の manual estimate / merge / clear 入口も loose chart については `PackageChartEntry` を受け、旧 `BMSFile` batch overload は残さない。adapterless bmson package entry の search / merge のためだけに compatibility adapter を materialize しない。

pending package に対する force install / manual install / selected package record removal も、UI では `ChartOperationTarget` を ViewModel に渡す。ViewModel は `PackageChartEntry` identity で `ChartPackage` を抽出し、既存の package-level API (`ForceInstallPendingPackages`, `ManualInstallPendingPackages`, `RemovePendingPackages(IEnumerable<ChartPackage>)`) へ流す。したがって package row 選択を package 操作へ変換するためだけには `BMSFile` compatibility adapter を materialize しない。

pending package に属する target は `PendingInstallDestinationSelectionResult.TargetEntries` に保持し、validation result には旧 `TargetFiles` list を残さない。導入先セルの手動編集も UI では `ChartOperationTarget` を ViewModel に渡し、package entry target は `BMSLibrary.SetPendingInstallDestination(PackageChartEntry, ...)` へ降りる。`BMSLibrary.SetPendingInstallDestination(...)` は `TargetEntries` へ導入先を書き戻すため、invalid destination warning で終わる場合だけでなく、manual destination を adapterless bmson package に設定する場合も validation / writeback だけでは compatibility adapter を作らない。低信頼 install estimation の候補選択時は、preserve 判定も `PackageChartEntry` の low-confidence warning と `ChartFile.InstallDestinationSuggestions` を見る。pending search の `SEARCHING` 表示は `PackageChartEntry` の `ChartFile.Status` projection として保持し、BMS storage owner へ同期せず、adapterless bmson でも同じ entry state として扱う。pending package 表示 row は `PackageChartEntry` provider から現在の `ChartFile` projection を読み直し、entry の projection change を購読するため、row 作成後に entry の install destination / suggestions / warnings / status が更新されても古い snapshot を固定しない。

merge 先探索や installed-only package destination resolve は package 内 chart を `PackageChartEntry` として列挙し、BMS / bmson 共通の installed hash index で既所持 directory を採点する。installed hash index 自体は md5 / sha256 の両方を登録するが、chart 側の lookup は `ChartFile.PrimaryLookupHash` により md5 優先、sha256 fallback の primary key を使う。

`SearchMergeDestinationForPendingCharts(...)` は、選択 chart が pending package に属する場合、選択 chart 単体ではなく所属 package に展開して package-level merge を走らせる。mixed package の既所持先が複数 directory に分かれている場合でも、hash 一致数が単独最多の directory があればそれを package 全体の merge 先として採用する。最多 directory が同点の場合や hash 一致がない場合は、hash 由来の自動決定をせず、`MergeCandidateOnly` の resource 評価へ fallback する。

direct install / drop install の ViewModel 入口は `InstallChartPackages(...)` で、model 層の pending package install 入口は `InstallChartPackagesAuto`, `ForceInstallPendingPackages`, `InstallPendingPackagesToEstimatedDestinations` を使う。旧 `InstallBMSFilesAuto` / `InstallChartPackagesForce` / `InstallChartPackagesToEstimatedDir` / 単数 wrapper は残さない。

newly installed tree に表示される installed package history/list のクリアは `RemoveInstalledPackageRecords` / `RemoveInstalledPackageRecordsAll` を入口にする。これは chart file 自体の削除ではなく、installed package record を list から消す操作である。

library folder operation は root/search-directory の public / user-facing 名に `BMSDirectory` が残るが、chart 行に対する folder 操作は `RenameChartFolder(...)` / `MergeChartDirectory(...)` / `AutoRenameChartFolders(...)` / `AutoRenameAllChartFolders(...)` に寄せる。`BuildFolderMoveDelta(...)` は `LibraryChartRef` snapshot を受け、path 更新 / unregister の mutation payload は `LibraryMutationDelta.ChartPathChanges` / `ChartsToUnregister` の `ChartFile` として保持する。folder move / merge path では BMS / bmson path と installed package / pending package の install destination も合わせて更新対象になる。root folder move の UI 経路は `MoveLibraryCharts(...)` から `MoveLibraryRootFolder(...)` に入り、`ChartOperationTarget` / `LibraryChartRef` を通して BMS / bmson chart を扱う。旧 `MoveBMSRootFolder(...)` wrapper は production 参照がなく、テストだけの旧名互換 API になっていたため削除済みである。`BMSDirectory` 系 UI / settings vocabulary は BMSFile-based root-folder / LR2 search-root 境界として残っている。

## Package / pending install

### `ChartPackage`

`ChartPackage` は package 内 chart の discovery container であり、BMS / bmson 混在 package を同じ単位で扱う。

pending / installed package record の永続正本は `install` table の row であり、実質的には source path と delete_parent などの package record metadata を保存する。package 内 chart list 自体は永続化されず、DB restore 後は `ChartPackage.path` から lazy rediscovery される。

主な現行仕様:

- `ChartPackage.ChartEntries` は package 内 chart discovery の読み取り primary API になりつつあり、`PackageChartEntry` / `ChartFile` を返す。
- `ChartPackage.GetChartAdapters()` は production 参照がなくなったため削除済みである。package 内 chart をまとめて読む入口は `ChartEntries` とし、BMS storage owner が必要な操作は対象 `PackageChartEntry.Chart.GetBmsStorageOwner()` から明示的に取得する。
- 明示的に chart entry list を渡された package でも private `PackageChartEntry` list を保持し、読み取りは `ChartEntries` から行う。
- それ以外では `PackageChartDiscoverySnapshot` を lazy build し、chart file path から BMS は `BMSFile`、bmson は `bmson_song` / `ChartFile` entry を作る。
- 旧 `PendingCharts` view は production 参照がなく、adapterless entry を表示確認だけで materialize し得るため削除済みである。pending package 内 chart は `ChartEntries` を正本として読み、mutation / warning 書き戻しが必要な時は対象 entry の `ChartFile` / storage owner へ直接降りる。

production code の `ChartPackage` 経由の chart-all 参照は、読み取り系と install estimation snapshot 内部では `ChartEntries` に寄せている。BMS-only mutation target list でも package 全体の adapter snapshot は公開せず、対象 entry の `Chart.GetBmsStorageOwner()` を読む。公開側の pending install orchestration でも導入先推定 request / batch state は entry を正本にし、searching flag は `PackageChartEntry` の `ChartFile.Status` projection として BMS / bmson へ同じように反映する。warning / install destination 書き戻しも可能な範囲で `PackageChartEntry` に寄せ、BMS row mutation が必要な経路だけ対象 entry の `Chart.GetBmsStorageOwner()` へ降りる。旧 `BMSFiles` alias は production 参照がなくなった段階で削除済みであり、package 内 chart の正本は明示 package / path discovery ともに `PackageChartEntry` に寄せている。`ChartPackage` 内の private `ChartFiles` property と `PackageChartDiscoverySnapshot.ChartFiles` は削除済みである。

`PackageChartDiscoverySnapshot` は `PackageChartEntry` を内部正本として保持する。`PackageChartEntry` は `ChartFile` を必ず持ち、BMS storage owner を読む production 経路は `PackageChartEntry.Chart.GetBmsStorageOwner()` を読む。旧 `GetExistingBmsFormatAdapter()` は production surface から削除済みであり、BMS row に対する parser / storage owner access が必要な時だけ helper 経由で BMS storage owner へ降りる。path discovery で見つけた bmson は `ChartFileProjection.FromBmsonSong(...)` による adapterless entry として保持し、package entry の production API は bmson compatibility adapter を lazy materialize しない。`ReplaceChartEntries(...)` は entry list を正本として置換するため、adapterless bmson entry を adapter 化せずに残せる。`ChartEntries` getter は常に entry 正本を返し、別の compatibility adapter list cache は持たない。旧 `BmsFiles` alias は削除済みであり、snapshot の読み取り経路は `PackageChartEntry` へ移行済みである。
`PackageChartEntry` は pending package 用の warning state、install destination state、resource health projection state を持てる。BMS entry は `ChartFile.GetBmsStorageOwner()` で BMS storage owner を読めるが、package warning / install destination / resource health 表示値は DB 永続化 state ではなく chart 共通 runtime / pending state であり、entry projection state を正本にする。BMS owner の duplicate / zero-note / LR2 path など storage warning は引き続き `BMSFile.Warnings` から projection し、package entry は `PackageLayout` / `InstalledState` / `ResourceHealth` / `InstallEstimation` category だけを overlay する。BMS player / LR2 score sync 由来の status は引き続き BMS storage owner から projection し、package search status はその上に entry projection として重ねる。bmson entry でも同じく entry 内の pending snapshot を `ChartFile` projection に重ねる。production の package entry API は、後段処理のためだけに adapterless bmson を `PendingChartEntry` へ変換しない。これにより、installed / single-file / nested-chart warning の付与、起動時 pending warning 初期化、手動導入先設定、導入先推定の低信頼 warning / suggestions 書き戻し、WAV/BGA/MOVIE health の反映は、それだけでは BMS storage owner や adapterless bmson adapter を pending state の正本にしない。pending / install resource health projection は `BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(PackageChartEntry)` に集約し、一時 maintenance snapshot から warning と health 表示値を同時に entry へ書き戻す。BMS entry も callback で `BMSFile.Warnings` を中間状態として読む経路には戻さない。

`ChartPackage` は `RemoveChartEntries(...)` / `ApplySingleFileInstallDestination(...)` / `ApplyDirectoryInstallDestination(...)` / `ReplaceChartEntries(...)` を持ち、operation / mutation 側が adapter list を直接状態更新する箇所を増やさないための所有者境界になり始めている。TreeView header など表示側は `DisplayTitle` を使い、`DisplayTitle` は `ChartEntries` / `ChartFile` から作るため bmson package header 表示だけでは compatibility adapter を materialize しない。XAML から package 内 chart list に直接 binding しない。count / empty 判定は `ChartEntries.Count` を直接見るため、compatibility adapter materialize を要求しない。選択 chart が package に属するかの判定は `PackageChartEntry.IsSameChartTarget(...)` と `ChartFile` identity に寄せる。`RemoveChartEntries(...)` は削除対象を `PackageChartEntry` predicate で判定し、path 削除でも adapterless entry を materialize しない。install destination apply helpers は `PackageChartEntry` を受け取り、実ファイル移動済みの対象 entry に対して `PackageChartEntry.ApplyInstalledPath(...)` で BMS storage owner path または `bmson_song` path / folder を更新する。install destination clear は package wrapper を経由せず対象 `PackageChartEntry.ClearInstallDestination()` を呼ぶ。`ReplaceChartEntries(...)` は chart entry list の明示置換として扱うため、adapterless entry を保持する。production / test ともに未使用だった predicate-based `RemoveChartAdapters(...)`、path-based `RemoveChartAdaptersByPath(...)`、adapter-list replacement の `ReplaceChartAdapters(...)`、membership helper の `ContainsChartAdapter(...)` は削除済みである。count / empty だけの薄い wrapper だった `GetChartAdapterCount()` / `IsChartAdapterEmpty()` と、install destination clear だけの薄い wrapper だった `ClearChartAdapterInstallDestinations()` も削除済みである。
`ChartPackage(BMSFile)` / `ChartPackage(IEnumerable<BMSFile>)` constructor は production API から削除済みである。package を明示 chart list 付きで構築する場合は `ChartPackage.FromChartEntries(...)` を使い、BMS storage owner から package entry に落とす必要がある境界でも caller は `ChartFileProjection.FromBmsFile(...)` で `ChartFile` に投影してから `PackageChartEntry.FromChart(...)` を使う。tests 側だけで必要な fixture construction は `ChartPackageTestExtensions.CreatePackage(...)` に閉じ込め、production に BMSFile-list constructor や BMSFile 専用 package-entry factory を互換 API として残さない。
auto install discovery で directory scan 済みの package を明示 chart list 付きで作る時は `ChartPackage.FromChartEntries(...)` を使う。recursive metadata discovery でも BMS は `BMSFile.CreateBMSFileFromFile(...)`、bmson は `PackageChartEntry.FromPath(...)` により entry として保持し、resource 判定は `ChartResourceSnapshot` を読む。`SearchChartPackagesRecursivelyWithMetadata(...)` / auto install grouping で既知 chart list を package 化する境界も `PackageChartEntry` を渡し、package construction の内部 API では `IEnumerable<BMSFile>` を要求しない。これにより、scan 結果の adapterless bmson entry を `BMSFile` list に戻さず package 正本として保持できる。

`BMSLibrary` / package install service の pending package 読み取り経路は `ChartEntries` / `ChartFile` へ移行中である。導入先推定 snapshot、advanced cleanup の「全 chart が installed 済みか」判定、追加 bmson 抽出、resource-only merge の hash / path 読み取りは package entry を読む。pending package の導入先推定で package 内 chart を already-installed / missing に分ける時も、判定は `PackageChartEntry.Chart` の `PrimaryLookupHash` で行い、manual single-package estimation / manual batch estimation / background batch preparation の partition 時点では adapterless bmson を materialize しない。pending estimate request / batch state は `PackageEntries` / `MissingEntries` / `AlreadyInstalledEntries` を持ち、`BMSFile` adapter list を状態として保持しない。snapshot と scoring の入力、および installed directory index からの混在 package 導入先解決は `MissingEntries`、導入先 / low-confidence warning / suggestions の writeback も package target では `PackageChartEntry` を使う。`SEARCHING` 表示が必要な場合も entry の `ChartFile.Status` projection だけを更新し、BMS storage owner や adapterless bmson compatibility object を materialize しない。estimated install batch plan の installed / duplicate / install target 分類も `PackageChartEntry.Chart.PrimaryLookupHash` と `ChartFile.InstallDestination` を読む。pending package の full-selection 判定、safe cleanup 用の hash snapshot も entry / chart path / primary hash を読む。advanced resource overwrite の一時的な path-only destination 書き込み / 復元は `CaptureInstallDestinationState(...)` / `SetInstallDestinationPathOnly(...)` / `RestoreInstallDestinationState(...)` を使い、warning / suggestions を壊さず adapterless bmson entry を materialize しない。manual estimate の package membership 判定は duplicate hash を package containment と誤認しないよう、同一 entry / storage row reference / `PackageChartEntry.Chart.Path` の一致だけを見る。root folder move / merge / deleted-folder cleanup の install destination 判定も `ChartFile.InstallDestination` を先に読む。pending regroup 成功ログの file count / install destination metadata 判定も `ChartEntries` を読む。playlist reference 同期、install result の BMS storage owner 抽出、folder merge 後の song upsert target のように既存 BMS storage owner が必要な経路は `PackageChartEntry.Chart.GetBmsStorageOwner()` を読むため、adapterless bmson のために新規 materialize しない。package 内 target の一致判定は adapterless bmson を materialize しないよう、`ChartFile` identity に集約し、hash fallback は使わない。merge destination 探索は、installed directory index で導入先が解決できる成功パスでは `ChartEntries` に導入先 metadata を書き戻し、adapterless bmson entry を materialize しない。merge 実行後の song / bmson_song upsert と maintenance target も `Repackage.ChartEntries` から storage owner を分けるため、移動済み adapterless bmson entry を materialize しない。resource estimation fallback が必要な場合も package-level install estimation の low-confidence state は entry に書き戻す。install 実行時の移動対象 chart 選別と auto naming の chart 入力も `ChartEntries` / `ChartFile.Path` で対象 entry を絞ってから BMS storage owner を取得し、移動成功後の package chart set は移動した `PackageChartEntry` で置き換える。install execution result は `AddedEntries` / `AddedCharts` を正本にし、BMS / bmson storage row list は `AddedCharts` から派生させる。bmson は install result / bmson_song upsert / installed package registration / resource lookup cache update に含めるだけでは adapter 化しない。通常 install では result-level storage callback が `song` / `bmson_song` upsert を行い、inline chart_info persist は最終配置 path の chart_info 適用後に BMS / bmson storage row を冪等 upsert する。estimated/deferred install でも result-level callback が先に `bmson_song` を upsert し、batch state apply は `AddedCharts` から BMS / bmson storage row を派生させて library state / inline chart_info に渡す。移動対象から外れた entry は install result / installed package registration へ含めず、adapter 化もしない。ここで扱う `BMSFile` は LR2 song storage row / BMS parser result / existing BMS storage owner として用途別に分け、list identity や list mutation は `ChartPackage` 側に閉じ込める。estimated/deferred install の state apply context は `AddedCharts` を受けて BMS / bmson storage row に分割し、bmson adapter list や混合 `AddedFiles` list を状態として持たない。
library directory merge の準備結果である `LibraryMergeResult` は、source chart を `SourceCharts` の `LibraryChartRef` として保持する。BMS / bmson storage owner への分解は unregister / DB upsert / maintenance target の直前だけで行い、`Repackage.ChartEntries` は source chart ref から作った `ChartFile` を正本にする。したがって installed bmson を merge source として扱うだけでは `PendingChartEntry` compatibility adapter を作らず、existing-hash snapshot も `ChartFile.PrimaryLookupHash` を読む。folder move / merge に伴う pending package 内 chart の install destination rewrite は `LibraryInstallDestinationChange.Entry` で `PackageChartEntry` を直接更新するため、adapterless bmson entry は導入先が移動元配下でも rewrite のためだけに materialize しない。deleted-folder cleanup は `PackageChartEntry.ClearInstallDestination()` で metadata / suggestions / install-estimation warning も含めて clear し、adapterless bmson entry を materialize しない。
BMS 系 chart 専用の保留 snapshot は、`ChartEntries` の `ChartFile.Kind` と拡張子で BMS-format chart だけを選び、既存 adapter または `ChartFile.GetBmsStorageOwner()` を優先して返す。これにより、zero-note / invalid-extension 対象外の adapterless bmson は snapshot 取得だけでは materialize されず、BMS storage owner がある entry も不要な compatibility adapter を作らない。
nested chart warning も `ChartEntries` の `ChartFile.Path` で入れ子判定してから対象 `PackageChartEntry` へ直接付与する。BMS / bmson のどちらでも warning は entry の pending warning projection state に保持する。package 直下の adapterless bmson は警告付与対象外なので adapter 化しない。
auto install workflow の pending / auto-install 分類では、resource reference count は `PackageChartEntry.ResourceSnapshot` を先に読み、SingleBmsFile / SingleBmsonFile / resource health warning は対象 `PackageChartEntry` に書き戻す。installed 判定は `ChartFile` callback で行い、AlreadyInstalled warning も entry state として保持する。BMS entry は resource snapshot 更新のために `ChartFile.GetBmsStorageOwner()` の parser / cache API を通ることがあるが、bmson entry は warning 書き戻しのためだけに compatibility adapter を materialize しない。
pending package から選択 chart を削る mutation delta も、選択 chart の package containment は `PackageChartEntry.IsSameChartTarget(...)` に寄せる。削除済み path payload から pending package を更新する箇所だけ `ChartFile.Path` で entry を落とす。残す entry は `ReplaceChartEntries(...)` で package へ書き戻すため、残存する adapterless bmson も削除される adapterless bmson も、この再構成だけでは adapter 化しない。
force install の normal install 上書き確認は、package に `ChartFile.InstallDestination` を持つ entry があるかで判定する。確認ダイアログを出すかどうかだけなら adapter mutation target は不要なので、adapterless bmson entry は確認判定だけでは materialize しない。install 成功後の post-install cleanup は `PackageChartEntry` projection state の suggestions / warning を clear し、解決済みの install destination / title / artist は保持する。BMS owner へは書き戻さない。
estimated install の resource-only merge 後に installed package history/list へ追加する display package は、既存 library row から `PackageChartEntry` を組み立てる。BMS は storage owner `BMSFile` から entry を作るが、bmson は `bmson_song` から `ChartFile` entry を作るため、installed display package の作成だけでは bmson compatibility adapter を materialize しない。
install table load result は pending warning 初期化の対象 adapter list を公開しない。warning count と package / stale row の結果だけを返し、pending warning 初期化は `PackageChartEntry` を走査して entry の warning state へ直接書き戻す。起動時 pending package の installed 判定は `ChartFile` callback で行い、AlreadyInstalled / SingleFile / ResourceHealth warning も `PackageChartEntry` の warning state として保持する。ResourceHealth warning 判定は resource reference を持つ entry に限り、warning 初期化のためだけに adapterless bmson entry を materialize しない。
pending package tree の「導入先を開く」は、package 内 chart の `ChartFile.InstallDestination` と `PrimaryLookupHash` を読む。これは explorer を開く先を解決するだけの UI 読み取りなので、adapterless bmson entry を `BMSFile` に materialize しない。pending package の install destination clear は `PackageChartEntry` の install destination state を clear し、adapterless bmson entry も entry 内 state と `ChartFile` projection を更新するため、clear だけでは compatibility adapter を materialize しない。
playlist reference の pending package / installed package 反映は `ChartFile.Md5` / `Sha256` で一致判定する。BMS entry / bmson entry のどちらも表示は `PlaylistReferenceIndex` と chart identity から解決し、参照テーブルに一致する bmson entry も playlist reference refresh や install 後 reference 反映だけでは materialize しない。reload / replace / remove / synchronize で旧 table 分を除去する場合も、target は `PackageChartEntry` / `ChartFile` で照合し、最終形では BMS storage owner への `RefTables` mutation は行わない。
pending / newly installed package の一覧表示 row は `PackageChartEntry` から作る。virtual package subset の `PackageChartSourceSnapshot` は `PackageChartEntry` list を保持し、`ChartListSourceRow.BuildPackageRows(IEnumerable<PackageChartEntry>)` へ渡す。`ChartListSourceRow` は package entry を live provider として保持するため、導入先推定や resource health projection が background で entry に反映された後も、画面遷移で snapshot を作り直さなくても `INSTL DST` / WARNING / WAV/BGA/MOVIE health を読み直せる。virtual subset の source row signature には `PackageChartEntry.ProjectionVersion` を含め、pending projection 変更が sort / cache reuse に埋もれないようにする。BMS / bmson の storage row 分割は virtual package source snapshot には持たせないため、package view の sort / keyword filter に入るだけでは adapterless bmson entry を materialize しない。
pending package install / resource overwrite 前の再生停止対象は、package 内 `PackageChartEntry.Chart` をそのまま snapshot する。実際に停止するかは現在再生中の BMS storage row の directory と chart path の関係だけで判定し、bmson や adapterless entry を再生停止対象にするためだけに materialize しない。細かい実装メモとして、旧実装では package 内 BMS storage row だけを snapshot していたため、BMS と同じフォルダーにある bmson だけを変更する mutation では再生停止しなかった。現行実装は mutation target を chart として扱うため、kind に関係なく再生中 BMS の directory 配下にある chart を変更する場合は停止対象にする。これは file lock / directory mutation の安全側挙動として移植せず改善する。
一方、file-only の削除 / repair / invalid extension rename / zero-note rename は BMS-format chart だけを再生停止判定に渡す。bmson file だけを削除・修復する操作は同じフォルダーの BMS file を直接変更しないため、旧実装の BMSFile-bound stop behavior と同じく BMS player を閉じない。whole-folder delete や folder rename / move / auto-rename のようにディレクトリ全体へ影響する操作だけは、chart kind に関係なく対象 folder と現在再生中 BMS の directory overlap を見る。
split した pending package の regroup 判定は `PackageChartEntry.Chart` の path / primary hash / install destination を読む。全 entry が同じ expected destination に解決できることを確認した後、regrouped package は `PackageChartEntry` list として書き戻す。regroup 後の warning 再初期化も起動時 pending warning 初期化と同じく `PackageChartEntry` を走査し、resource health projection は BMS / bmson とも `ApplyPendingResourceHealthProjection(PackageChartEntry)` が一時 maintenance snapshot から warning と health 表示値を entry へ書き戻す。bmson も AlreadyInstalled / SingleBmsonFile / ResourceHealth / nested chart warning を entry state として保持し、warning 再初期化のためだけに adapterless bmson entry を materialize しない。
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

target chart list は `PackageChartEntry` として snapshot builder に渡され、読み取り専用で参照する代表譜面を `RepresentativeChart` として `ChartFile` 化する。pending package の通常推定では `MissingEntries` を直接渡すため、missing target を snapshot 化するだけなら追加の adapter 再解決は不要である。package-level target が空の場合は package の `ChartEntries` 全体を使うため、adapterless bmson entry も snapshot から落ちない。package-level pending estimation の request / batch state は `PackageEntries` / `MissingEntries` / `AlreadyInstalledEntries` だけを保持し、mutation writeback も entry を入口にする。install / move 実行のような実ファイル操作でも、対象は `PackageChartEntry` / `ChartFile` を正本にし、BMS-only parser / storage mutation が必要な時だけ `ChartFile.GetBmsStorageOwner()` へ明示的に降りる。`DefinedResources` は `PackageChartEntry.Chart` から作るため、bmson pending chart では `PendingChartEntry` adapter ではなく `bmson_song` の resource refs を使う。metadata profile は `ChartFile` projection の title / artist / path から作る。bmson pending chart では `Kind=Bmson` と `BmsonSong` owner を保持し、BMS 専用 storage owner とは分ける。複数 package 推定では、`PackageInstallSurfaceSnapshot` や batch source surface を共有し、同じ source tree の scan / resource surface を再利用できる。

loose chart 推定の model 入口は `PackageChartEntry` を受け取り、snapshot も entry から組み立てる。installed hash での除外や既に install destination が入っている対象の skip 判定は `ChartFile.PrimaryLookupHash` / `ChartFile.InstallDestination` を読む。UI から standalone chart target を渡す場合も、ViewModel が background task 前に `ChartFile` を確定し、`PackageChartEntry` に包んでから model 層へ渡す。BMS-only parser / storage owner が必要な場合だけ `ChartFile.GetBmsStorageOwner()` へ降り、bmson loose target のために compatibility adapter を作らない。

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

- `Chart`: playlist detail row の chart read model。所持 BMS / 所持 bmson は `ChartFile.GetBmsStorageOwner()` / `GetBmsonStorageOwner()` で storage owner を取り出す。
- `IsOwned`: `ChartFile.GetBmsStorageOwner()` または `GetBmsonStorageOwner()` が path を持つ場合 true

`PlaylistDetailSourceRow` は source snapshot 構築時に解決済み `ChartFile` を受け取る。BMS / bmson の storage owner は `ChartFile.GetBmsStorageOwner()` / `GetBmsonStorageOwner()` から派生し、playlist missing row は `resolvedChart == null` と playlist entry / `entryChartInfo` から metadata chart を作る。`BuildPlaylistSourceRows(...)` も `BMSFile + bmson_song` の tuple を source row 入口にせず、entry resolve の結果を `ChartFile` として保持する。`PlaylistDetailRow` は source row の `Chart` を引き継ぎ、missing row の手動 level 編集や source row の entry chart_info patch 時には `Chart` を作り直す。これにより、view row からも source row と同じ chart_info / identity snapshot を `GridRowResolver` に渡せる。

`GridRowResolver` は playlist row から `ChartOperationTarget` を作る際、row の `Chart.Kind` と `ChartFile` の storage owner を組み合わせて source scope と capability を決める。owned bmson playlist row では `ChartFile.GetBmsonStorageOwner()` を storage owner として保持し、operation target 作成のためだけには compatibility adapter を作らない。`PlaylistDetailRow` と `PlaylistDetailSourceRow` はどちらも playlist entry row として扱い、entry duplication / playlist cell edit policy / root folder drop preservation の判定で同じ `BMSTableEntry` を返す。

playlist detail row は view row materialization だけでは bmson compatibility adapter を作らない。`PlaylistDetailSourceRow` / `PlaylistDetailRow` は表示用 `ChartFile` を bmson storage row と `ChartFileTransientState` だけから作り、`CompatibilityBmsFile` surface を持たない。ViewModel が渡す transient state provider は repair / warning 表示 state を `ChartFileTransientState` として返し、存在しない adapterless bmson row は表示するだけでは `PendingChartEntry` へ変換しない。

playlist detail の `RefTablesSymbols` / `RefTablesNames` は source snapshot 構築時に確定する。BMS / bmson / missing row とも `PlaylistReferenceIndex` の md5 / sha256 lookup 結果を使う。これにより表示列と `playlist:` / `ref:` / `table:` keyword search が同じ参照情報を読む。

playlist detail 表示時の `ChartRowsView` 実体は `PlaylistDetailVirtualView` である。`PlaylistDetailSourceRow` を全件 source として保持し、可視 index だけ `PlaylistDetailRow` へ遅延 materialize する。playlist detail 中は `UseAsyncChartRowsViewBinding` を false に切り替え、通常一覧側の async binding policy と分けている。

### Playlist への追加

`MainWindowViewModel.AddChartRowsToFolderBMSTable(...)` は、playlist table 概念として `BMSTable` 名を残しつつ、追加元の一覧 row は Chart として解決する。

通常 folder への追加では、row から `ResolvePlaylistDropChart(...)` で `ChartFile` を解決し、`BMSTableEntry(ChartFile)` で playlist entry を作る。bmson はこの経路で `PendingChartEntry` adapter を作らず、`ChartFile.Kind == Bmson` と `ChartFile.GetBmsonStorageOwner()` から sha256 identity の playlist entry になる。

- BMS row は実体 `BMSFile` を使う。
- bmson library row は `ChartFile` / `BmsonSong` の identity から playlist entry を作り、playlist 追加のためだけには `PendingChartEntry` adapter を作らない。
- playlist row は通常 folder 追加では `BMSTableEntry.Duplicate()` を優先し、既存 playlist metadata を保つ。
- 追加後の playlist reference 表示更新は、追加元 row から解決した `ChartFile` list を `BMSLibrary` の playlist reference index 更新へ渡す。旧実装のように追加後の対象を `BMSFile` list へ戻すと bmson が参照更新対象から落ちるため、その中間表現は残さない。最終形では BMS storage row にも `RefTables` を書き戻さず、`PlaylistReferenceIndex` と chart row invalidation で表示へ反映する。

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
- `BmsLibraryMaintenanceService.UpdateMaintenanceInfo(...)` は `ChartFile` を入力として受ける。service 内部で BMS storage owner と `bmson_song` に分けるが、resource health の defined / existing count は BMS / bmson とも `ChartResourceSnapshot` / `ChartFileProjection` から作る chart-common 計算を使う。BMS は同じ `maintenance` row に encoding / reload 結果も保持するため、resource health 計算結果を既存 `BMSFile.maintenanceInfo` へ merge し、encoding / `is_encoding_fixed` は BMS 専用 pipeline が更新する。
- 起動中 file diff の inline bmson maintenance も `PendingChartEntry` shim を作らず、parser 済み `bmson_song` へ `MaintenanceInfo` を直接 attach する。

ただし encoding check / fix / zero-note check は capability 上 BMS 専用であり、bmson に適用しない。

### chart_info

`chart_info` は BMS / bmson 共通 metadata として扱う。

inline chart_info pipeline の wrapper は、読み取り済み内容 snapshot を `InlineChartSnapshotTarget` の `ChartFile` と組み合わせて受ける。install 後などの既存ファイル inline build も `BuildForExistingCharts(...)` で `ChartFile` list を入口にする。`ChartInfoBuildService.BuildInlineChartInfo(...)` も snapshot と `ChartFile` を入口にし、BMS digest writeback が必要なときだけ storage owner へ降りる。短命な parser payload でも BMS list と bmson list を別々の正本として持たない。`ChartInfoBuildService.BackfillChartInfos(...)` も `ChartFile` list を入口にし、backfill 側の `ChartInfoBuildTarget` は `ChartFile` list を正本にする。BMS md5-first / bmson sha256-first の grouping key、BMS-only digest target 判定、missing identity skip は `ChartInfoBuildTargetMapper` に集約する。BMS digest writeback と inline snapshot digest の storage owner 書き戻しは `ChartStorageOwnerMutator` に集約するが、BMS / bmson storage owner へ chart_info を attach する責務は持たない。最終的な chart_info 適用は md5 / sha256 index、DB commit、ViewModel projection provider で行う。

細かい実装メモとして、backfill target の grouping key は既存の DB / digest 振る舞いを維持する。BMS は LR2 song row の `hash` が primary identity で、sha256 が未計算でも md5 で同一譜面をまとめて一度だけ読み、digest を BMS storage owner へ書き戻す。bmson は parser 時点で sha256 を持つため sha256 優先で grouping し、`chart_digest_map` への digest backfill は行わない。この違いは storage owner の都合ではなく、BMS song row と bmson_song row の永続化責務差として残す。

通常一覧の chart_info 表示や sort key は `LibraryChartRow` / `ChartListSourceRow` が `BMSLibrary.ResolveChartInfo(...)` provider から受け取った `ChartInfo` を使う。production の通常一覧 / playlist detail row では session `ChartInfoIndex` を正本にし、BMS / bmson storage owner に runtime `ChartInfo` property は持たせない。inline build / backfill の parser result も DB commit / callback / index update の row payload として扱い、hydration 通知互換や fallback のために storage owner へ attach しない。

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
- 所持 chart row の一時表示 state cache: chart 共通 transient state registry と `ChartFileTransientState`
- BMS-format selection helper: `GetSelectedBmsFormatCharts(...)`
- model mutation reference: `LibraryChartRef`
- installed directory lookup: BMS / bmson の両方を hash / path で登録する shared lookup
- playlist entry identity: md5-only と sha256-only の両対応
- package discovery: `PackageChartEntry` / `ChartFile` により BMS / bmson を pending chart として扱う

## 現在の未抽象化領域

次の領域では、入口は chart-native に寄っているが、`BMSLibrary` 内部の primary source / index はまだ owned chart collection へ寄せ切れていない。

### 所持譜面集合の runtime 正本

現状の `BMSLibrary` は、所持譜面集合を `BMSFiles` と `BmsonSongs` の二本立て storage row collection と、`OwnedChartCollectionState` の両方で保持している。`OwnedChartCollectionState` は storage owner identity を持つ `ChartFile` collection で、current installed source の snapshot はここから作る。snapshot は entry の古い値を返さず、`ChartFileProjection.FromStorageOwner(...)` で storage owner の現在値を再投影する。

ただし、これはまだ完全な runtime 正本ではない。storage row setter / DB reload / external refresh では collection を full invalidate し、次回利用時に storage row から再構築する。差分同期も unregister と install upsert が中心で、すべての chart-common index / mutation payload が collection から派生しているわけではない。

本筋の移行では、この層を「storage owner から作る lazy cache」ではなく、chart-common の collection / index / mutation の primary source にする。storage row は永続化 owner と BMS / bmson 固有 producer として残す。

### snapshot / lookup helper

`CreateInstalledChartSnapshot(BMSFiles, BmsonSongs, ...)` は current installed source を検出した場合、owned chart collection の snapshot を返す。任意 subset を渡された場合は、まだ collection view として表現できないため `ChartFileProjection.FromStorageRows(...)` に fallback する。

`CreateLibraryChartRefSnapshotUnsafe()` は `CreateInstalledChartSnapshot(...)` 経由で current source の owned snapshot を使うが、まだ library ref 用の indexed view ではなく、呼び出しごとに `ChartFile` list を materialize する。duplicate merge / folder move などの準備処理では、この materialize cost と service 側の analysis cost が残る。

`CreateOwnedResourceMaintenanceCharts()` は owned snapshot を使う。一方、resource maintenance 用の任意 target、追加 install chart、merge 先 directory に限った target は `CreateResourceMaintenanceCharts(...)` で subset を明示して作る。これは全件 owned collection ではなく、対象 chart だけを resource references 付きで扱うための境界である。

playlist owned hash snapshot と installed chart lookup はまだ owned chart collection の index ではない。playlist owned hash は storage row を直接走査し、installed chart lookup は projection を避ける storage row direct builder と差分更新 state を持つ。どちらも最終設計では owned chart collection の隣接 index へ寄せる。

新規 helper を追加する場合は、`BMSFiles` + `BmsonSongs` を直接結合する API を増やさず、owned chart collection の view / index へ置くか、任意 subset / BMS-only / bmson-only / DB 境界であることを API 名で分かるようにする。

### 残る subset projection 境界

owned chart collection は current installed source の全体集合を表すため、次の入力は全体 snapshot へ単純置換しない。

- 任意 subset: UI 選択、warning ignore、resource repair、文字化け再判定、merge / repair 後の対象 directory など、呼び出し側が対象 chart を絞っている場面。`ChartFile` subset なら null 除外と必要な projection overlay を行い、storage row subset なら BMS / bmson の kind 条件と path 条件を保って投影する。
- 追加 bmson だけ: 推定インストール batch 後、deferred installed package から追加 bmson song だけを inline chart info 対象へ足す場面。これは直近 batch の追加分を補うための入力であり、owned collection 全体から再抽出すると対象範囲が広がる。
- resource maintenance 用 target: `maintenance` / resource health 更新に必要な resource references を持つ target。全件 index build は owned snapshot を使えるが、install 後、merge 後、repair 後、delta update では対象 subset だけを resource references 付きで作る。

これらを collection 化する場合は、全件 `CreateSnapshot(...)` の後に caller 側で filter するのではなく、owned collection 側に filtered view / target builder を用意する。target が storage row 由来の場合は、その呼び出しが BMS-only / bmson-only / DB 境界なのか、chart-common subset なのかを先に名前で分ける。

### `ChartPackage` adapter materialization boundary

`PackageChartDiscoverySnapshot.ChartFiles` と `ChartPackage.GetChartAdapters()` は削除済みで、discovery snapshot と package は `ChartEntries` を正本にする。package 全体を `List<BMSFile>` snapshot として取り出す production API は残さない。

package entry から BMS storage owner を扱う production 操作は、`PackageChartEntry.Chart.GetBmsStorageOwner()` を読む。旧 `GetExistingBmsFormatAdapter()`、generic な `GetOrCreateCompatibilityAdapter()`、`CompatibilityAdapter` property、`GetOrCreateBmsFormatAdapter()` の production API は削除済みであり、bmson を package entry から新規 `PendingChartEntry` として materialize する入口や、既存 bmson adapter を chart-common state の正本として読む入口は残さない。

旧 `BMSFiles` alias と `GetChartAdapters()` は production 利用がないことを確認したうえで削除済みであり、テストコードにも production 互換 API を残すためのテストは置かない。adapter materialization 自体を検証したいテストは test-local helper に隔離し、production surface へ戻さない。

### model APIs の残存 BMS 名

`ChartRowsView` など通常一覧の public binding 名は chart row 名へ移行済みである。

`RemoveLibraryCharts(...)` は BMS / bmson 共通の library chart 削除入口であり、旧 `RemoveBMSFiles(...)` / `RemoveChartFiles(IEnumerable<BMSFile>)` wrapper は残さない。未使用だった single chart move wrapper も削除済みである。
pending package install 入口も `InstallChartPackagesAuto`, `ForceInstallPendingPackages`, `InstallPendingPackagesToEstimatedDestinations` へ移行済みで、旧 `InstallBMSFilesAuto` / `InstallChartPackagesForce` / `InstallChartPackagesToEstimatedDir` wrapper は残さない。
duplicate view の cache / search 入口は `DuplicateChartGroups` / `SearchDuplicateChartGroups()` に移行済みで、旧 `BMSFilesDuplicated` / `SearchBMSFilesDuplicated()` は残さない。

### BMS-only views and workflows

次の領域は BMS 専用意味を持つため、単純に chart 化しない。

- garbled / encoding check / encoding fix
- zero-note check / BMS-format zero-note rename
- LR2IR
- score viewer
- ranking update
- invalid extension rename
- audio convert
- LR2 `song` / `folder` update
- `chart_digest_map`

ここは `ChartFile` 化後も BMS-only capability として残す。zero-note 一覧も BMS-format chart だけを対象にするが、UI source 境界は `ChartFile` snapshot に寄せる。
pending invalid extension rename は `GetPendingBmsFormatChartFilesSnapshot` / `RenamePendingBmsFormatChartFileExtensions` / `RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions` を入口にし、BMS-format chart file のみを対象にする。

## ChartFile / ChartPackage 化に向けた現在制約

### `ChartFile` に求められる性質

現行仕様では、`ChartFile` は DB row ではなく、アプリ内で譜面を扱うための domain/read model である。`BMSLibrary` 内では owned chart collection の entry としても使い始めているが、まだ storage owner identity cache の段階である。DB 永続化の正本は引き続き BMS / bmson の storage row とし、chart-common の snapshot / lookup / mutation / read model は owned chart collection を primary source に寄せる。

`ChartFile` は少なくとも次を表す必要がある。

- `Kind`: BMS / bmson
- current path / directory
- md5 / sha256
- title / artist / level / mode
- chart_info
- maintenance / resource health
- storage owner: BMS は `BmsFile`, bmson は `BmsonSong`

resource references は `ChartFile` に read model として載る。BMS では `BMSFile` の parser 由来 resource refs、bmson では `bmson_song.wav_files` / `bga_files` と parser 直後の runtime-only 状態を `ChartFileProjection` が `AudioResourcePaths` / `VisualResourcePaths` / `Stagefile` / `Backbmp` / `Banner` に写す。resource health / install estimation の集計は `ChartResourceSnapshot.Create(ChartFile)` を入口にし、BMS owner 由来 refs が必要な場合だけ `ChartFile.GetBmsStorageOwner()` へ降りる。

source scope と operation capability は `ChartOperationTarget` が持つ。owned bmson 用の legacy `BMSFile` adapter provider は UI row / operation target surface から削除済みであり、chart 共通 operation は `ChartOperationTarget.CompatibilityBmsFile` を必要としない。

今後の整理では、新しい汎用中間表現を増やすことを優先しない。まず既存の `ChartFile`、`PackageChartEntry`、`ChartOperationTarget`、`LibraryChartRef`、BMS / bmson storage row のいずれかへ責務を置けるかを確認する。単に `Compatibility*` の名前を変えた factory / helper / facade は最終形で消える中間層になりやすいため、追加しない。

Kind ごとの storage 境界は次の通り。

| Kind | storage owner | 永続化先 | 現行責務 |
| :--- | :--- | :--- | :--- |
| BMS | `BMSFile` | LR2 `song` / `folder`、app-owned `chart_digest_map` など | LR2 `song` row として保存できる BMS 専用 data と、BMS parser / score attachment / BMS-only helper を持つ。install destination state は chart-common runtime state へ移し、`BMSFile` には残さない。 |
| bmson | `LR2SongDBExtended.bmson_song` | app-owned `bmson_song` | path / folder / md5 / sha256 / title / subtitle / artist / genre / level / mode_hint / image/audio metadata / updated_at など bmson catalog 保存に特化する。resource refs / chart_info / maintenance は runtime owner として持つが table へは保存しない。 |

したがって、単純に `BMSFile` を `ChartFile` へ rename すると、LR2 `song` row という永続化境界と、BMS / bmson 共通の operation/read model 境界が混ざったまま名前だけ変わる。整理の主眼は、`BMSFile` / `bmson_song` を storage owner として残しつつ、`BMSLibrary` が操作する所持譜面集合を `ChartFile` collection として一元化することである。

永続化 storage は二本立てを維持する。BMS は `BMSFile` または将来の BMS song row 型、bmson は `bmson_song` が永続正本である。一方、アプリ実行中の chart-common 処理では、storage row list を都度束ねるのではなく、owned chart collection を差分更新された runtime 正本として使う。

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

production の旧 compatibility BMSFile adapter surface は削除済みである。bmson は `BMSFile` 継承 adapter に変換せず、BMS storage owner が必要な経路だけ `ChartFile.GetBmsStorageOwner()` を使う。BMS storage owner から package entry を作る場合も `ChartFileProjection.FromBmsFile(...)` で domain model に投影してから `PackageChartEntry.FromChart(...)` へ渡す。

production に残る `Compatibility` 名は playlist summary column settings の設定互換、LR2 compatibility warning category、旧 standalone song path 正規化のような永続 / 外部仕様互換だけである。譜面 row / package / resource health の adapter surface 由来の `Compatibility*` 名は残さない。

旧実装で compatibility adapter が担っていた state は、現在は次の owner に分ける。

- BMS storage row state: `BMSFile`
- bmson storage row state: `bmson_song`
- package / pending chart state: `PackageChartEntry`
- UI 表示上の一時 state: `ChartFileTransientState`
- chart identity / projection: `ChartFile`

## Active migration plan

この節は、これから進める本筋の active plan である。前段の adapter / compatibility 境界整理は完了扱いとする。`OwnedChartCollectionState` は導入済みなので、今後はこれを lazy identity cache から chart-common collection / index / mutation の primary source へ育てる。

### 目標

- `BMSLibrary` が保持する所持譜面集合の primary source を、`BMSFiles` / `BmsonSongs` の二本立て storage row collection から、`ChartFile` を要素とする owned chart collection へ移す。
- `BMSFiles` / `BmsonSongs` は DB 永続化 owner、外部互換 property、BMS / bmson 固有 producer の境界として残す。chart-common の snapshot / lookup / mutation / read model は storage row list を都度結合しない。
- `CreateInstalledChartSnapshot(...)`、`CreateLibraryChartRefSnapshotUnsafe()`、resource maintenance target 作成、playlist owned hash snapshot、installed chart lookup などは owned chart collection の view / index から作る。
- install / uninstall / merge / repair / folder move / path rename は owned chart collection と storage row owner を同じ mutation として差分更新する。
- large library での全件 projection を減らし、初回 build と繰り返し mutation の両方で O(N) rebuild を避ける。

### 非目標

- `ChartFile` を DB row にしない。永続化形式は LR2 `song` / `folder` と app-owned `bmson_song` の二本立てを維持する。
- BMS / bmson の storage table を統合しない。
- settings 名、UI 文言、playlist DB / JSON、LR2 互換 schema は抽象化だけを理由に変更しない。
- BMS-only 処理を chart-common 処理へ無理に広げない。encoding / zero-note / LR2IR / score viewer / ranking update / invalid extension rename / audio convert は capability で BMS-only として残す。
- startup 直後に不要な heavy prewarm を増やさない。必要な index は lazy build か mutation 同期で用意する。

### Target architecture

`BMSLibrary` には `OwnedChartCollectionState` がある。現行 v1 は owner identity list と snapshot factory であり、最終的な責務は次の通りである。

- 初回利用時または DB reload 後に `BMSFiles` / `BmsonSongs` から `ChartFile` entry を構築する。
- 各 entry は `Kind`、path、md5、sha256、storage owner、必要な runtime projection key を持つ。
- collection は stable identity として、kind + storage owner identity、path、primary lookup hash を扱える。
- collection は read-only snapshot / filtered view / index view を提供し、呼び出し側に mutable internal collection を渡さない。
- collection は add / remove / move / upsert / hash change / storage owner replace を差分 mutation として受ける。
- collection から BMS storage row view と bmson storage row view を取得できるが、それは永続化・互換境界用の view であり、chart-common 処理の primary input ではない。

派生 index は owned chart collection の内部または隣接 state として管理する。

- primary hash count
- md5 / sha256 -> directory lookup
- known chart directory set
- path / directory lookup
- kind partition
- playlist owned hash snapshot
- parent folder candidate view
- resource maintenance target view

index は collection mutation に同期して差分更新する。丸ごと DB reload、外部 setter による collection replacement、表現できない mutation だけ full invalidate / rebuild に落とす。

### Migration phases

1. **Owned chart collection v1 の導入**
   - `BMSLibrary` 内に `OwnedChartCollectionState` を追加済み。
   - current installed source の `CreateInstalledChartSnapshot(...)` と BMS-only zero-note snapshot は owned collection から作る。
   - unregister と install upsert は owned collection と同期済み。
   - entry は storage owner identity を保持し、snapshot 作成時に owner の現在値を再投影する。
   - 残タスクは、mutation coverage と派生 index を増やし、storage row list parity 確認と診断 log を整えること。

2. **全件 snapshot helper の置換**
   - `CreateInstalledChartSnapshot(...)` の current source 経路は owned collection 利用済み。任意 subset fallback は、collection-backed filtered view を用意してから縮小する。
   - `CreateLibraryChartRefSnapshotUnsafe()` は full `ChartFile` list materialize ではなく、owned chart entry から `LibraryChartRef` view / iterator を作る。
   - `CreateOwnedResourceMaintenanceCharts()` は owned snapshot 利用済み。resource maintenance 用 target は、subset の意味を残したまま collection-backed target view へ寄せる。
   - playlist owned hash snapshot、parent folder cache、install destination cleanup、installed chart lookup は collection view / index から作る。
   - `BMSFiles` + `BmsonSongs` を直接結合して `ChartFileProjection.FromStorageRows(...)` へ渡す新規経路を増やさない。残す場合は任意 subset / BMS-only / bmson-only / DB 境界として明示する。

3. **Mutation pipeline の同期化**
   - `LibraryMutationDelta`、install result、merge result、repair result、folder move result に owned chart collection mutation を含める。
   - `BmsLibraryStateApplier` と storage row upsert 経路は、owned chart collection も同じ transaction boundary で更新する。
   - internal mutation では `BMSFiles` / `BmsonSongs` setter による full invalidation を避け、collection と派生 index を差分更新する。
   - external replacement / DB reload は full rebuild として明示し、log に reason を残す。

4. **Read model / service input の collection 化**
   - 通常一覧 root / folder / keyword / virtual subset は owned chart collection または collection-backed source row から作る。
   - duplicate search、resource maintenance、install estimation、playlist reference apply は owned chart collection の snapshot / view を入口にする。
   - `ChartListSourceRow` の hot path では、必要な projection だけを遅延構築し、全件 `ChartFile` の再 materialize を避ける。

5. **Storage row collection の役割縮小**
   - `BMSFiles` / `BmsonSongs` は DB commit、BMS-only producer、既存 binding 互換、外部 refresh の境界に限定する。
   - chart-common 処理から直接 `BMSFiles` / `BmsonSongs` を enumerate して lookup / snapshot / refs を作る箇所をなくす。
   - 残す direct enumeration は BMS-only / bmson-only / DB load-save / compatibility boundary としてコメントまたは API 名で分かるようにする。

### 注意点

- `ChartFile` は DB row ではない。storage owner は残すが、chart-common collection の entry として stale snapshot にならないようにする。
- BMS owner の warning / maintenance / score attachment は BMS-only producer state として残る。表示・sort・operation がそれを直接正本にしないよう、`ChartFile` / row projection / provider 経由にする。
- bmson は warning collection を storage row に持たない。duplicate / resource / install destination warning は projection state として扱う。
- install destination は storage row 永続列ではなく chart-common runtime / pending state である。owned chart collection と ViewModel transient state の key がずれないようにする。
- path move / folder merge / repair fix では、old path と new path の両方を mutation payload に残す。BMS と bmson で DB update safety check の前提が違うため、storage owner へ降りる直前まで kind を保持する。
- playlist reference、chart_info、score はそれぞれ専用 index / provider を正本にする。owned chart collection に短命 hydration result を attach して producer boundary を曖昧にしない。
- 21万譜面規模では per-chart object / dictionary の増加が効く。owned chart collection 導入時は memory、startup、main view first render、merge / install の repeated mutation log を必ず見る。
- `Compatibility*` / `Adapter` / `PendingChartEntry` 由来の production surface は復活させない。必要な場合は BMS storage owner helper、`ChartFile`、`PackageChartEntry`、`LibraryChartRef` のどれかに責務を置く。

### 性能確認

この計画は large library startup / main view / duplicate search / install estimation / merge に直接触れる。性能確認では最新の完了済み cycle を同じ DB / roots / Release net472 build で比較する。

重点 log:

- `startup_ready_operable` / `startup_background_summary`
- `song_tbl_load_projection` / owned chart collection full build log
- `song_tbl_file_check_breakdown`
- `main_view_build` / `custom_table_render`
- `installed_chart_lookup_index build/update`
- `duplicate_merge_model prepare_done`
- `SearchDuplicateChartGroups`
- `playlist_library_index_prewarm`

成功目安:

- 初回 owned chart collection build が、従来の全件 projection より明確に重くならない。
- merge / install / repair の 2 回目以降で、owned chart collection と installed lookup の full rebuild が出ない。
- `CreateInstalledChartSnapshot(...)` / `CreateLibraryChartRefSnapshotUnsafe()` 相当の処理が、全件 storage row projection として log に現れない。
- 通常一覧 root / folder 切替で可視行以外の heavy projection が増えない。

### 完了条件

- `BMSLibrary` の chart-common 処理が owned chart collection を primary source にしている。
- `BMSFiles` / `BmsonSongs` の直接 enumeration は DB load-save、external full refresh、BMS-only / bmson-only producer に限定されている。
- `CreateInstalledChartSnapshot(...)` と `CreateLibraryChartRefSnapshotUnsafe()` は storage row list から全件 projection しない。
- owned chart collection、installed lookup、playlist owned hash、parent folder cache、resource maintenance target が同じ mutation から差分更新される。
- BMS / bmson の install、uninstall、merge、repair、folder move、path rename、maintenance、playlist reference、duplicate search の既存挙動が維持される。
- 既存の LR2 DB、app-owned bmson DB、playlist DB / JSON、settings、UI 文言の互換性を壊していない。

## 今後の仕様整理で守る境界

1. 永続化 storage は BMS / bmson の二本立てを維持する。
2. `BMSLibrary` の runtime 所持譜面集合は `ChartFile` collection を正本にする。
3. UI / operation / chart-common service の入口は chart target に寄せる。
4. BMS-only 処理は capability で明示する。
5. `BMSFile` 型を chart 共通処理の種別判定に使わない。production では `BMSFile` を BMS storage row / BMS-only 処理に閉じ込め、chart 共通処理は `ChartFile.Kind` を確認する。
6. package 内 chart の読み取りは `ChartEntries` を入口にする。BMS storage owner access は対象 entry 単位に限定し、package-level adapter list API を再導入しない。
7. settings 名と UI 文言の BMS は互換契約として残す。内部 helper / log / operation symbol は必要に応じて chart 名へ寄せる。
8. `ChartFile` / `ChartPackage` を使う場合も、既存の LR2 互換 DB と playlist JSON / DB の永続形式は維持する。
