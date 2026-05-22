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
- `BMSScore` attachment と LR2 score / ranking / IR 関連の storage state
- BMS parser 由来の resource references
- `maintenanceInfo`

`BMSFile` は LR2 score row の attachment と listener lifecycle を持つが、clear / rank / rate / ranking などの一覧表示・sort 用 getter は持たない。score 表示値は `ChartScoreSnapshot` を介して `ChartFile` / row read model 側で解決する。

LR2 song の provisional note count は base `LR2SongDB.song.karinotes` 列として残す。`BMSFile.notes` のような chart_info / 表示語彙に見える alias は production 参照がなく、BMS storage row と chart_info の境界を曖昧にするため残さない。譜面メタデータの notes 表示・検索は `ChartInfo` / `ChartInfoDisplaySnapshot` を通す。

`BMSFile.Level` / `BMSFile.Folder` は LR2 song row 由来の表示値を読むだけの getter として残す。手動編集可能な chart-common mutable property ではないため setter は持たない。FOLDER セル編集は `GridRowResolver.TryGetFolderEditChartOperationTarget(...)` から `RenameChartFolder(...)` へ入る chart operation として扱い、LEVEL 編集は playlist detail row の entry-level 編集に限定する。

playlist reference の mutable cache は `BMSFile` には残さない。表示列の `RefTablesSymbols` / `RefTablesNames` は歴史的な UI / column 名として残るが、実データは `PlaylistReferenceIndex` と chart identity から解決する。

`BMSFile` は BMS storage owner に紐付く structured warning collection と BMS 専用 producer からの mutation helper（重複、zero-note、LR2 path encoding など）を持つ。ただし warning の digest / tooltip / display text は `BMSFile` の表示 alias としては持たず、`ChartWarningCollection` / `ChartWarningProjectionFormatter` と row read model 側で解決する。production から使われない copy / replace-all 用の `BMSFile` 互換 wrapper は残さず、collection 全体の検証は `ChartWarningCollection` を直接対象にする。

`BMSFile` は `maintenanceInfo` snapshot の所有・由来管理、BMS parser / component cache、encoding producer を持つが、`encoding` / `WAVHealth` / `BGAHealth` / `MovieHealth` / `StagefileHealth` / `BannerHealth` / `BackbmpHealth` の表示 alias は持たない。resource health の production 更新は `ChartFile` 共通計算から `maintenanceInfo` へ merge し、encoding / reload は BMS-only producer が更新する。`ChartFileProjection.FromBmsFile(...)` は有効な `maintenanceInfo` snapshot がある場合だけ health / encoding を `ChartFile` に投影し、lazy placeholder を表示正本として作らない。`maintenanceInfo` 内部の変更を UI へ反映する場合は、変更した producer が `BMSFile.NotifyMaintenanceInfoChanged(...)` で `maintenanceInfo` を明示通知し、`LibraryChartRow` が `WAVHealth` / `encoding` などの row 表示 property へ展開する。

`BMSFile` には `instl_dst` / `InstallDestinationTitle` / `InstallDestinationArtist` / `InstallDestinationSuggestions` / popup state などの install destination runtime property を残さない。install destination は BMS / bmson のどちらにも適用される chart 共通の runtime / pending state であり、通常一覧 / playlist detail / loose chart 操作では ViewModel の chart 共通 `ChartFileTransientState` cache、`BMSLibrary` の model-side runtime overlay、`PackageChartEntry` projection state を正本にする。`ChartFileProjection.FromBmsFile(...)` は BMS storage owner から install destination を投影しない。BMS owner を持つ chart に install destination state が必要な場合は、caller が `ChartFileProjection.WithPackageState(...)`、`ChartFileTransientState`、または model-side runtime overlay で明示的に重ねる。`PackageChartEntry` は BMS owner を持つ entry でも install destination を entry projection state として持ち、BMS owner へ同期しない。起動時 cleanup / folder cleanup / repair の `LibraryMutationDelta` は entry がない installed BMS storage owner でも `BMSFile` を直接書き換えず、適用後 `ChartFile` snapshot を one-shot buffer と model-side runtime overlay に反映する。entry 付きの変更は `PackageChartEntry` へ書き戻され、installed bmson row にも install destination 相当の model-side writeback はない。

`BMSLibrary.BMSFiles` の更新時には、playlist summary owned hash snapshot、installed chart key / directory index、parent folder cache、install estimation metadata profile cache、duplicate cache、resource health index が無効化される。service / API surface は chart-native に寄っているが、`BMSLibrary` 内の installed snapshot / installed directory index / installed key check / library chart ref snapshot は `BMSFiles` と `BmsonSongs` の storage collection から rebuild して `ChartFile` / `LibraryChartRef` へ投影する。その上で、install destination については model-side runtime overlay を重ねるため、BMS / bmson storage owner に永続列や legacy runtime property を持たせずに、model 内の installed snapshot / library ref snapshot へ反映できる。

### bmson

bmson の所持譜面の正本は `BMSLibrary.BmsonSongs` であり、要素は `LR2SongDBExtended.bmson_song` である。

`bmson_song` は BMS の `song` table には入れない。永続列として path / folder / md5 / sha256 / title / subtitle / artist / genre / level / mode_hint / banner / backbmp / stagefile / preview_music / updated_at を持つ。

resource references、`HasFreshResourceReferences`、`MaintenanceInfo` は `bmson_song` の runtime-only 情報であり、`bmson_song` table の列としては保存されない。DB から hydrate した `maintenance` や、parser 直後の resource references を同一オブジェクトへ載せるための in-memory owner として扱っている。`chart_info` は BMS / bmson storage owner の property としては持たず、通常一覧 / playlist detail / zero-note 判定は `BMSLibrary.ResolveChartInfo(...)` provider と session `ChartInfoIndex` を正本にする。inline build / backfill の parser 結果は result row / DB commit / index update へ流し、storage owner へ短命 cache として attach しない。

`BMSLibrary.BmsonSongs` の更新時には、playlist summary owned hash snapshot、installed chart key / directory index、parent folder cache、install estimation metadata profile cache、duplicate cache、resource health index が無効化される。parent folder cache version の変更通知も BMS / bmson の両方で発行する。

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

BMS / bmson storage row から warning なしの installed / standard snapshot を作る境界も `ChartFileProjection.FromStorageRows(...)` / `FromBmsFiles(...)` / `FromBmsonSongs(...)` に集約する。Model / ViewModel 側は BMS row list と bmson row list を直接結合する実装を増やさず、storage owner から chart domain model へ投影する責務を `ChartFileProjection` に閉じる。

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

target chart list は `PackageChartEntry` として snapshot builder に渡され、読み取り専用で参照する代表譜面を `RepresentativeChart` として `ChartFile` 化する。pending package の通常推定では `MissingEntries` を直接渡すため、missing target を snapshot 化するだけなら追加の adapter 再解決は不要である。package-level target が空の場合は package の `ChartEntries` 全体を使うため、adapterless bmson entry も snapshot から落ちない。package-level pending estimation の request / batch state は `PackageEntries` / `MissingEntries` / `AlreadyInstalledEntries` だけを保持し、mutation writeback も entry を入口にする。install / move 実行のような実ファイル操作でも、対象は `PackageChartEntry` / `ChartFile` を正本にし、BMS-only parser / storage mutation が必要な時だけ `ChartFile.GetBmsStorageOwner()` へ明示的に降りる。`DefinedResources` は `PackageChartEntry.Chart` から作るため、bmson pending chart では `PendingChartEntry` adapter の component cache ではなく `bmson_song` の resource refs を使う。metadata profile は `ChartFile` projection の title / artist / path から作る。bmson pending chart では `Kind=Bmson` と `BmsonSong` owner を保持し、BMS 専用 storage owner とは分ける。複数 package 推定では、`PackageInstallSurfaceSnapshot` や batch source surface を共有し、同じ source tree の scan / resource surface を再利用できる。

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

現行仕様では、`ChartFile` は DB row ではなく、アプリ内で譜面を扱うための domain/read model である。`ChartFile` は storage 正本を直接兼ねず、Kind と storage owner を通して BMS / bmson の永続化型へ接続する。

`ChartFile` は少なくとも次を表す必要がある。

- `Kind`: BMS / bmson
- current path / directory
- md5 / sha256
- title / artist / level / mode
- chart_info
- maintenance / resource health
- storage owner: BMS は `BmsFile`, bmson は `BmsonSong`

resource references は `ChartFile` に read model として載る。BMS では `BMSFile` の parser / component cache、bmson では `bmson_song.wav_files` / `bga_files` と parser 直後の runtime-only 状態を `ChartFileProjection` が `AudioResourcePaths` / `VisualResourcePaths` / `Stagefile` / `Backbmp` / `Banner` に写す。resource health / install estimation の集計は `ChartResourceSnapshot.Create(ChartFile)` を入口にし、BMS parser cache が必要な場合だけ `ChartFile.GetBmsStorageOwner()` へ降りる。

source scope と operation capability は `ChartOperationTarget` が持つ。owned bmson 用の legacy `BMSFile` adapter provider は UI row / operation target surface から削除済みであり、chart 共通 operation は `ChartOperationTarget.CompatibilityBmsFile` を必要としない。

今後の整理では、新しい汎用中間表現を増やすことを優先しない。まず既存の `ChartFile`、`PackageChartEntry`、`ChartOperationTarget`、`LibraryChartRef`、BMS / bmson storage row のいずれかへ責務を置けるかを確認する。単に `Compatibility*` の名前を変えた factory / helper / facade は最終形で消える中間層になりやすいため、追加しない。

Kind ごとの storage 境界は次の通り。

| Kind | storage owner | 永続化先 | 現行責務 |
| :--- | :--- | :--- | :--- |
| BMS | `BMSFile` | LR2 `song` / `folder`、app-owned `chart_digest_map` など | LR2 `song` row として保存できる BMS 専用 data と、BMS parser / score attachment / BMS-only helper を持つ。install destination state は chart-common runtime state へ移し、`BMSFile` には残さない。 |
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

production の旧 compatibility BMSFile adapter surface は削除済みである。bmson は `BMSFile` 継承 adapter に変換せず、BMS storage owner が必要な経路だけ `ChartFile.GetBmsStorageOwner()` を使う。BMS storage owner から package entry を作る場合も `ChartFileProjection.FromBmsFile(...)` で domain model に投影してから `PackageChartEntry.FromChart(...)` へ渡す。

production に残る `Compatibility` 名は playlist summary column settings の設定互換、LR2 compatibility warning category、旧 standalone song path 正規化のような永続 / 外部仕様互換だけである。譜面 row / package / resource health の adapter surface 由来の `Compatibility*` 名は残さない。

旧実装で compatibility adapter が担っていた state は、現在は次の owner に分ける。

- BMS storage row state: `BMSFile`
- bmson storage row state: `bmson_song`
- package / pending chart state: `PackageChartEntry`
- UI 表示上の一時 state: `ChartFileTransientState`
- chart identity / projection: `ChartFile`

## Active migration plan

2026-05-22 時点の方針は、層を薄く順番に chart-native 化するのではなく、production に残っている compatibility 境界を境界単位で閉じることを優先する。

進捗指標は `BMSFile` 参照総数ではない。`BMSFile` は最終形でも BMS storage row / LR2 `song` row / BMS-only operation として残る。進捗は次の production compatibility surface が消えているかで見る。

- `CompatibilityBmsFile` / `CompatibilityAdapter` / `GetOrCreateCompatibilityAdapter()`
- `PendingChartEntry : BMSFile`
- bmson を `BMSFile` adapter に materialize しないと表示・検索・sort・playlist・resource health・package operation が進まない経路
- production では不要になった旧名 wrapper / 互換 API

### 作業粒度

1 commit は、原則として 1 つの production compatibility boundary を閉じる単位にする。helper 1 個、local rename 1 個、test-only cleanup 1 個だけでは commit 単位にしない。

標準確認手順とサブエージェントレビューのコストが大きいため、今後は「境界内の小さな helper / test 修正 / doc 更新」を個別 commit に分けない。例えば playlist reference を `BMSFile.RefTables` から `PlaylistReferenceIndex` 正本へ移す場合は、model service、row 表示、sort / filter invalidation、該当 test 更新を同じ boundary commit にまとめる。DB 永続化、settings、UI 文言、F2 rename が必要な symbol rename だけは別 commit または作業中断の判断点にする。

中間 helper を増やして旧実装を包むより、最終 owner へ直接責務を移す。責務の移動先は `ChartFile`、`PackageChartEntry`、`ChartOperationTarget`、`LibraryChartRef`、`BMSFile`、`bmson_song` のいずれかを優先する。`Compatibility*` の別名 wrapper や、最終的に消す facade を新設しない。

大きめの compiler-driven 変更は許容する。ただし、settings 名、UI 文言、DB table / column、playlist JSON / DB、LR2 互換 schema は別契約として扱い、抽象化だけを理由に変更しない。

### 性能確認

抽象化作業は large library startup / virtual list / pending package projection の hot path に触れやすい。性能影響があり得る境界 commit では、標準確認手順に加えて Release build 後に `bin\Release\net472\BeMusicSeeker.exe --log-level=Info` を起動し、`bin\Release\net472\install-performance.log` を確認する。GUI 操作が難しい環境では、目的の起動 log が出たら process を終了してよい。

`startup_background_summary elapsedMs` が 60000ms 以上なら異常として扱い、抽象化作業を進める前に原因を直す。ただし background tail だけを見て初期化全体の悪化を見落とさない。抽象化作業前の large library 起動では `startup_ready_operable elapsedMs=23405` 程度で、Everything scan が約 20 秒ならその数秒後に operable になるのが期待値である。`startup_ready_operable` が 30 秒台後半へ伸びる場合は、song table load / file diff / install destination cleanup / initial main view build / first render など前段を含めて regression として扱う。特に次の値は regression guard として見る。

- `song_tbl_file_check_breakdown`: `scan_ms` / `apply_ms` / `instl_dst_cleanup_ms`。install destination cleanup は chart 数に対して線形である必要があり、owner membership 判定に O(N^2) を持ち込まない。
- `startup_ready_operable` / `startup_background_summary`: 起動操作可能化と背景処理完了の総時間。
- `main_view_build`: 通常一覧 root / folder 遷移の `totalMs`、`sourceRowsReuse`、`viewRowsCreated`、`sortMs`。可視行のみ materialize する前提なので、全件一覧の切替で秒単位の projection work が出る場合は source row / transient state provider / cache invalidation を疑う。
- `custom_table_render`: 初回 `items_source_changed` の `renderWorkMs` と breakdown。`rowHighlightMs` が大きい場合は WARNING / resource health projection の同期 build、`cellValueMs` が大きい場合は row getter / reflection、`textLayoutMs` / `drawTextMs` が大きい場合は visible cell text rendering を疑う。
- pending package 関連 log: `pending_estimate_batch`、`resource_health_projection`、pending section の `main_view_build`。導入先推定、WARNING digest / tooltip、WAV/BGA/MOVIE health は同じ projection / notification 方針で即時反映される必要がある。

毎 commit で GUI 起動確認を行う必要はないが、startup file diff、install destination、resource health / warning projection、main view source row、pending package state、playlist reference / chart info hydration に触れた場合は実行対象にする。

### 現在のフェーズ

2026-05-22 時点で、active plan で追っていた adapter materialization boundary は閉じている。playlist reference についても、BMS storage row の mutable cache と `PlaylistReferenceIndex` の二重構造を解消し、BMS / bmson とも `ChartFile` identity と `PlaylistReferenceIndex` を正本にしている。

一方、`BMSFile` の型参照だけではなく、`BMSFile` 内部に残る property / helper の意味を監査すると、warning storage / chart_info / maintenance snapshot ownership など chart 共通 concept と接続する state がまだ `BMSFile` の中に残っている。install destination runtime property と maintenance health / encoding の表示 alias は削除済みであり、現在は adapter cleanup 後の最終 audit ではなく、残る `BMSFile` 内部の chart-common concept を BMS-only storage / parser / producer として説明できるか分類し、説明できないものを `ChartFile` / `PackageChartEntry` / `ChartFileTransientState` / 専用 snapshot へ移すフェーズである。

### 残っている主な compatibility 境界

2026-05-22 現在、active plan で明示的に追っていた `CompatibilityBmsFile` / `PendingChartEntry : BMSFile` / playlist reference cache のような構造互換 boundary は閉じている。残りは新しい中間層を追加する作業ではなく、残存 `BMSFile` 参照と `BMSFile` member surface を意味で監査し、BMS-only / LR2 storage row / BMS parser / score producer として説明できないものを境界単位で潰す作業である。

主な監査対象:

- `BMSFile` 参照が BMS storage row / LR2 `song` row / BMS parser / BMS player / score・IR・encoding・zero-note などの BMS-only boundary に閉じているか。
- `ChartFileProjection.FromBmsFile(...)` などの BMS owner helper が、bmson を chart-common 経路へ戻す adapter として使われていないか。
- install destination state が DB 永続化 state や BMS storage owner state ではなく chart 共通 runtime state として扱われているか。通常一覧 / playlist detail / loose chart operation では chart 共通 transient state / model overlay / package entry state を優先し、`BMSFile` へ legacy runtime property が復活していないかを監査する。
- `BMSFile.Warnings` / `ChartInfo` / `maintenanceInfo` / resource reference cache が、BMS-only parser / storage producer の境界を越えて chart-common read / operation API の正本になっていないか。maintenance health / encoding の表示は `ChartFile` / row 側の read model を正本にし、`BMSFile` 側へ表示 alias が復活していないか。
- production では使われず tests だけから参照される旧名 wrapper / 互換 API / projection overload が残っていないか。
- `Compatibility*` / `Adapter` / `PendingChartEntry` などの語が、外部互換・履歴説明・negative assertion 以外の production surface に残っていないか。`RefTablesSymbols` / `RefTablesNames` は UI / column の歴史的表示名として残してよいが、`BMSFile.RefTables*` や BMS storage row への playlist reference writeback API は復活させない。
- settings 名、UI 文言、playlist DB / JSON、LR2 互換 schema の BMS 語彙は、抽象化だけを理由に変更していないか。

### 最終監査 plan

今後の作業は、helper 単位ではなく audit category 単位で進める。各 commit は「監査で見つかった同種の残存 boundary を一括で閉じる」単位にする。

1. **旧 adapter / compatibility 語彙の audit**
   - production の `CompatibilityBmsFile` / `CompatibilityAdapter` / `GetOrCreateCompatibility*` / `PendingChartEntry` が復活していないことを確認する。
   - `BMSFile.RefTables*` / BMS storage row への playlist reference writeback API が復活していないことを確認する。`RefTablesSymbols` / `RefTablesNames` は UI / column 表示名として許容する。
   - 残存 hit は settings 互換、LR2 compatibility warning、XAML `markup-compatibility`、UI / column 表示名、履歴説明、negative assertion のいずれかへ分類する。
   - test-only production helper が見つかった場合は削除し、test 側を `ChartFile` / `PackageChartEntry` / storage owner helper に寄せる。

2. **Install destination state の chart-domain 化**
   - `instl_dst` は UI / column 名としては残るが、LR2 `song` row の永続列でも BMS storage owner の runtime property でもない。BMS / bmson のどちらにも適用される chart 共通 runtime state として扱う。
   - `ChartFileProjection.FromBmsFile(...)` は BMS owner から install destination を読まない。BMS chart の install destination は `WithPackageState(...)` / `ChartFileTransientState` / model-side runtime overlay で明示的に重ねる。
   - pending package / repair / folder cleanup / install estimation は `PackageChartEntry.Chart.InstallDestination` と entry projection state を正本にする。
   - `PackageChartEntry` は BMS owner を持つ entry でも install destination / title / artist / suggestions を BMS storage row へ同期せず、entry projection state として保持する。loose library chart の search / merge / manual edit / clear も ViewModel の chart transient state cache へ反映し、表示 / validation / regroup / warning digest が `BMSFile` property を正本にしない状態へ寄せる。
   - 2026-05-22 現在、package entry と ViewModel row 表示 / loose chart transient cache の境界は chart-common state へ寄っている。`LibraryInstallDestinationChange.GetCurrentInstallDestination()` は `Entry.Chart` / `Chart` を読むだけにし、folder move / merge の target enumeration と BMS player temporary install fallback も `ChartFile.InstallDestination` を読む。repair fix 後の BMS install destination clear、deleted-folder cleanup 後の BMS install destination full clear、startup file scan diff の stale install destination cleanup は、service 内の直接 mutation ではなく `LibraryMutationDelta.UpdatedInstallDestinations` に載せる。startup cleanup の stale 判定入力も `BMSLibrary.CreateInstalledChartSnapshot(...)` から渡した overlay 済み `ChartFile.InstallDestination` を読む。`BMSLibrary` は `BmsLibraryStateApplier` 適用前に `LibraryInstallDestinationChange` から適用後 chart snapshot を作り、ViewModel 向け one-shot buffer へ publish する。ViewModel は `BMSFiles` / `BmsonSongs` refresh listener の先頭で `ConsumeLatestInstallDestinationChangedCharts()` を呼んで共通 `ChartFileTransientState` cache へ取り込む。これにより、startup worker thread から ViewModel cache を直接更新せず、main view refresh が古い transient state を読む前に取り込める。model-side install destination runtime overlay は `BmsLibraryStateApplier` の適用成功後に同じ chart snapshot を反映し、`CreateInstalledChartSnapshot(...)` / `CreateLibraryChartRefSnapshotUnsafe()` の projection に重ねる。`BmsLibraryStateApplier.ApplyLibraryMutationDelta(...)` は entry 付き target だけ `PackageChartEntry` へ書き戻し、entry のない installed BMS storage owner は直接 mutate しない。`LibraryChartRef.FromChartFile(...)` は overlay 済み `ChartFile` snapshot を保持するため、folder move / merge の library row 側 target enumeration でも BMS / bmson の install destination runtime state を落とさない。overlay key は ViewModel shared cache と同じ `ChartFileRuntimeStateKey` を使い、同一 hash 別 path の誤適用を避ける primary key に加えて path key も保持する。repair / maintenance refresh などで hash が変わっても同一 owner / path の runtime state を見失わないためである。path-only mutation でも既存 overlay を新 path key へ移した snapshot を one-shot buffer と model overlay の両方へ反映する。BMS owner install destination writeback と `FromBmsFile(...)` readback は閉じており、legacy runtime property も削除済みである。

3. **BMSFile member surface の chart-common 責務 audit**
   - `BMSFile` の property / helper が BMS-only storage state なのか、Chart 共通の表示・操作 concept なのかを分類する。
   - `ChartInfo` / warning / maintenance / resource health / score display など、bmson にも適用される concept は `ChartFile` / `ChartFileTransientState` / 専用 snapshot へ移し、`BMSFile` には BMS / LR2 storage owner と parser / score producer として必要な state だけを残す。install destination はこの方針に従って chart-common runtime state へ移行済みである。
   - 一覧 row の score / ranking 表示 getter は `ChartScoreSnapshot` を読む。通常一覧では `ScoreSnapshot` provider を優先し、`BMSFile.bmsScore` attachment は移行中の producer/cache 互換としてのみ残る。row / sort / keyword search 側は `BMSFile` の score 表示 API に依存しない。`BMSFile` の score 表示 getter は削除済みである。
   - `ChartWarningCollection` は BMSFile owner 依存を持たず、変更通知 callback と install destination provider だけを受け取る。BMS storage row は引き続き `BMSFile.Warnings` を保持するが、collection 自体は chart-common warning list として使える。`BMSFile` の warning 表示 alias は削除済みであり、row 表示は `ChartFile.Warnings` / `ChartWarningProjectionFormatter` を読む。
   - maintenance health / encoding の表示 alias は `BMSFile` から削除済みである。`BMSFile` は `maintenanceInfo` の所有・由来管理、BMS parser / component cache、encoding fix producer を持つ。resource health の production 更新は `BmsLibraryMaintenanceService.BuildResourceHealthMaintenanceInfo(ChartFile, ...)` を通り、BMS owner 由来の refs が必要な時だけ `ChartResourceSnapshot.Create(ChartFile)` 内で BMS parser / component cache へ降りる。`ChartFileProjection.FromBmsFile(...)` は有効な `maintenanceInfo` snapshot を直接読んで `ChartFile` へ投影し、lazy placeholder は表示正本として作らない。`BMSFile` からの通知は `maintenanceInfo` 変更に集約し、row 側で health / encoding 表示 property へ展開する。
   - `ChartInfo` の表示テキスト / sort key / undefined 判定は `ChartInfoDisplaySnapshot` を読む。`BMSFile` / `bmson_song` は `ChartInfo` storage owner ではなく、hydration 通知も `ChartInfoIndex` / projection provider / dependency refresh で扱う。`ChartLevelText` / `ChartTotalSortKey` のような chart-common 表示 property は storage owner に持たせない。

4. **chart-common API に残る BMSFile list / overload の audit**
   - package / playlist / resource health / duplicate / install destination / library mutation の production 入口に `IEnumerable<BMSFile>` が戻っていないか確認する。
   - BMS-only callback（zero-note、score、IR、encoding、BMS parser）以外の BMSFile list 入口は `ChartFile` / `LibraryChartRef` / `PackageChartEntry` に統一する。
   - 既に未使用になった old-name facade は互換用に残さず削除する。

5. **docs / tests / logs の terminology cleanup**
   - active spec は現行実装を正本として保ち、古い計画書には進捗を追記しない。
   - テスト名や assertion が旧 adapter の存在を前提に読める場合は、adapter 非生成または chart-native behavior を直接表す名前へ寄せる。
   - 内部ログ文言は互換契約ではないため、実装の chart-common semantics と明らかにずれているものだけ直す。ユーザー向け UI 文言と settings 名は変更しない。

F2 が必要な symbol rename が出る場合はそこで中断する。Roslynator rename-symbol または compiler-driven edits で安全に閉じられる場合は、同じ audit category 内でまとめて進める。

### 進捗感

`a1cc092d281a27cd3abc0edba6d7035d8cfdc058` 時点からの作業で、adapter materialization / `PendingChartEntry : BMSFile` / package entry / resource health / installed directory index / duplicate / main view row source の主要 boundary はほぼ閉じている。`BMSFile` 参照総数は完了指標ではないが、chart-common operation が BMSFile adapter を要求する領域は大きく減っている。

playlist reference 統合と `BMSFile.RefTables*` legacy API cleanup が終わり、install destination runtime property、warning 表示 alias、maintenance health / encoding 表示 alias、chart_info runtime attachment も `BMSFile` / `bmson_song` から削除済みである。`BMSFile` は `maintenanceInfo` の子 property change を自動で BMSFile property change へ転送せず、maintenance service など producer が `NotifyMaintenanceInfoChanged(...)` を明示的に呼ぶ。これにより maintenance 表示更新は chart-common producer 境界で発火し、BMS storage row が health / encoding 表示 alias の observer を持たない。install destination については package entry、ViewModel transient cache、model-side runtime overlay への移行が完了し、通常表示 / playlist detail / loose edit、BMS player temporary install fallback、installed directory index / folder move library ref snapshot の正本は chart state 側へ寄っている。folder move / merge / deleted-folder cleanup / startup cleanup は overlay 済み `ChartFile.InstallDestination` と `LibraryMutationDelta` を読み、entry のない installed BMS owner を mutate しない。`ChartFileProjection.FromBmsFile(...)` も BMS owner-backed chart の install destination state を作らず、maintenance health / encoding は有効な `maintenanceInfo` snapshot からだけ投影する。chart_info 表示 read model と zero-note 判定は `BMSLibrary.ResolveChartInfo(...)` provider を優先し、storage owner へ hydrate 済み row を attach しない。現在の完了度は **98% 前後** と見る。残りは `BMSFile` 参照数の削減ではなく、`BMSFile` member surface を BMS-only / LR2 storage row / BMS parser / score producer として説明できる状態へ寄せる作業である。

今後の commit 見込みは、warning / maintenance ownership の残存妥当性確認、最終監査 / test-doc cleanup で **2〜4 commit 程度**を目標にする。commit 数を目標以内に収めること自体は必須ではないが、作業単位は helper 単位ではなく audit category 単位に寄せ、標準確認手順とサブエージェントレビューは各 boundary commit ごとに実施する。

### 閉じた compatibility 境界

- `ChartPackage.ChartEntries` と `PackageChartEntry.Chart` は package 内 chart の正本である。generic な `PackageChartEntry.GetOrCreateCompatibilityAdapter()`、`PackageChartEntry.CompatibilityAdapter`、`PackageChartEntry.GetOrCreateBmsFormatAdapter()`、`PackageChartEntry.GetExistingBmsFormatAdapter()` production API は削除済みである。BMS storage owner は `PackageChartEntry.Chart.GetBmsStorageOwner()` として読み、`PackageChartEntry` 内に別の BMS owner field は持たない。
- `LibraryChartRow`、`PlaylistDetailSourceRow`、`PlaylistDetailRow`、`GridRowResolver`、`ChartOperationTarget` は `ChartFile` を正本にし、owned bmson 用 `CompatibilityBmsFile` surface は削除済みである。production では旧 adapter-backed bmson の storage owner 抽出も削除済みであり、`ChartFileProjection.FromBmsFile(...)` は BMS storage owner 専用である。
- `GridRowResolver.GetRealBmsFile(...)` は削除済みである。BMS preview / player UI / LR2IR cache lookup のように現時点で BMS storage row だけを対象にする UI 経路は `TryGetBmsPlayerFile(...)` を使い、chart-common operation 判定や bmson owned row の missing 判定には使わない。
- `PendingChartEntry` は production assembly だけでなく test fixture からも削除済みである。test helper の BMS owner 取得も `GetBmsOwnerForTest()` / `GetBmsOwnersForTest()` へ寄せている。
- playlist reference の adapter materialization boundary、BMS storage row への `RefTables` writeback boundary、`BMSFile.RefTables*` legacy API surface は閉じている。pending bmson のために adapter は materialize せず、BMS storage row にも playlist reference cache を書き戻さない。外部から任意の `IEnumerable<BMSFile>` を渡して playlist reference を部分適用 / 表示更新する production API は残さず、更新は `PlaylistReferenceIndex` / chart identity / row invalidation へ寄せる。
- library file operation cleanup は `LibraryChartRef` / `PackageChartEntry` / `ChartFile` を入口にする。pending package の install destination cleanup / rewrite は `PackageChartEntry` を直接更新する。folder move / merge の library row 側 target enumeration は `LibraryChartRef.ToChartFile()` で作った `ChartFile.InstallDestination` を読む。`LibraryChartRef` と installed chart snapshot は model-side install destination runtime overlay を重ねるため、storage owner に install destination state を戻さなくてもこの経路で検出できる。repair fix の BMS install destination path clear、deleted-folder cleanup の BMS install destination full clear、startup file scan diff の stale install destination cleanup は `LibraryMutationDelta.UpdatedInstallDestinations` として返し、state applier は entry target の `PackageChartEntry` だけを mutate する。entry のない installed library row 側の install destination mutation は DB 永続化でも BMS storage owner mutation でもなく chart runtime state の更新として扱い、`LibraryInstallDestinationChange.CreateAppliedChartSnapshot(...)` で適用後の chart state を作る。startup cleanup の stale 判定入力は `BMSLibrary.CreateInstalledChartSnapshot(...)` で model-side overlay を重ねた `ChartFile.InstallDestination` を `BmsLibraryInitializationService.ApplyFileScanDiff(...)` へ渡して使う。deleted-folder cleanup / startup cleanup の full clear は path だけでなく title / artist / suggestions / install-estimation warning を消す必要があるため、`LibraryInstallDestinationChange.ClearInstallDestinationState` で path-only update と区別する。同一 delta に path change がある場合は storage owner identity で一致する library chart だけ新 path を反映した keyで `BMSLibrary.ConsumeLatestInstallDestinationChangedCharts()` から ViewModel の shared transient cache へ戻す。pending package entry は偶然同じ path でも library row の path change を消費しない。model-side overlay は primary key と owner-guarded path key を併用するため、repair 後の maintenance refresh で BMS hash が変わっても同一 owner / path の clear state を落とさず、同じ path に別 owner が入った場合には古い overlay を適用しない。
- resource warning ignore / unignore の production 入口は `ChartFile` であり、maintenance service の API 名も `SetChartResourceWarningsIgnored(...)` に寄せている。BMS / bmson の ignored flag は `ChartFile` から `BMSFile.maintenanceInfo` または `bmson_song.MaintenanceInfo` へ解決する。resource health index delta も `setMaintenanceInfo(...)` 内の maintenance target charts を直接使い、未使用だった BMSFile-only delta parameter は残さない。
- 音声 / 画像 / 動画の resource extension は `ChartResourceExtensions` を正本にする。BMS 形式 chart extension と BMS / bmson を合わせた chart extension は `ChartFileKindResolver` を正本にする。`BMSFile` にあった `wavExtensions` / `bgaImageExtensions` / `bgaMovieExtensions` などの public resource extension 定数と `bmsExtensions` は production 参照がなく、BMS storage row の責務ではなく chart-common resource lookup / kind 判定の責務だったため削除する。
- 通常一覧 / playlist detail / custom table status column の表示 status は `ChartFileStatus` を正本にする。`BMSFile.BMSFileStatus` は BMS player runtime mutation 用に残し、`ChartFileProjection.FromBmsFile(...)` と row getter が `ChartFileStatus` へ投影する。LR2IR score の未送信表示は BMS storage row status ではなく、runtime-only `BMSScore.IsLr2IrScoreUnsent` から `ChartScoreSnapshot` 経由で `ChartFileStatus.SCORE_UNSENT` として重ねる。custom table は row surface の `ChartFileStatus` を読む。BMS player パネルの play/pause button converter は `NowPlayingBMS.status` に直接 bind しているため BMS-only UI 境界として残す。
- package install service の post-processing callback は `PackageInstallExecutionResult` を payload にする。storage row upsert / maintenance / score / library state apply は result の `AddedCharts` から BMS / bmson storage row を派生させて処理し、install result の public surface に BMSFile-only convenience list を残さない。これにより bmson install 後処理のために BMSFile list へ adapter を混ぜる入口を残さない。
- main view row source は virtual root / folder の hot path では `ChartListSourceRow` を BMS / bmson storage owner から直接作り、subset / non-virtual fallback では `ChartFile` list を入口にする。通常 root / folder view は source row 自体が chart identity read model なので、全件 `ChartFile` snapshot を先に作らない。`NormalLibraryRowCache` は BMS owner reference key と bmson path / source-reference sync を内部に持ち、ViewModel は bmson 専用 row dictionary を持たない。BMS collection change の prune は BMS storage owner identity だけを対象にし、bmson collection change は `NormalLibraryRowCache.SyncBmsonRows(...)` で membership / source-reference / sort-key change を判定する。unregister のように owner identity だけが必要な model-side 経路では `ChartFileProjection.FromBmsStorageOwnerIdentity(...)` / `FromBmsonStorageOwnerIdentity(...)` を使う余地があるが、ViewModel の shared transient state prune は通常 library の current storage owner set から cache key を作り、cache key 以外の表示 state は `ChartFileRuntimeStateKey` では読まない。garbled / zero-note などの BMS-only subset も UI source selector は `ChartFile` を返し、`ChartListSourceRow` 入口へ BMSFile list を戻さない。
- main view の virtual sort / cell edit 判定は `LibraryChartRow` / `ChartFile` row surface の property 名を正本にする。旧実装では default sort column や folder edit branch が同じ文字列を得るためだけに `nameof(BMSFile.Title)` / `nameof(BMSFile.Folder)` を読んでいたが、これは UI chart row 操作から BMS storage row symbol へ依存するだけなので移植しない。settings の column 名自体はユーザー設定契約のため変えず、内部の symbol reference だけを chart row 側に寄せる。
- duplicate detection の snapshot 入口は `ChartFile` list であり、`BMSFile` / `bmson_song` の二系統引数は残さない。BMS / bmson storage row の結合は `BMSLibrary` の installed chart snapshot 作成に閉じ、duplicate service は `ChartFile.PrimaryLookupHash` と `ChartLookupKey.GetPrimaryHashKind(ChartFile)` だけを見る。BMS storage row への warning writeback は `ChartFile.GetBmsStorageOwner()` がある row に限定する。
- estimated install batch plan の installed library 入力も `ChartFile` list である。BMS / bmson storage row の結合は `BMSLibrary` の installed chart snapshot 作成に閉じ、package install service は installed hash set を `ChartFile.PrimaryLookupHash` から作る。旧 `installedFiles` / `installedBmsonSongs` 二系統引数は、chart-common batch planning へ BMSFile list を戻す入口になるため残さない。
- pending / install resource health projection は `BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(PackageChartEntry)` へ集約する。`BmsLibraryInitializationService.LoadInstallTable(...)`、auto install workflow、pending regroup 後の warning 再初期化は `Func<BMSFile, bool>` callback を受けず、BMS / bmson とも `PackageChartEntry` から warning list と health 表示値を作る。pending package の resource existence check / warning construction は `ChartFile` projection から一時 `BMSFileMaintenanceInfo` を組み立て、BMS owner の `maintenanceInfo` を chart-common 判定の正本として読まない。writeback は `PackageChartEntry` の warning / resource health projection state へ行い、resource health の計算自体も表示値の反映も BMS owner mutation に依存しない。

### 保守時の監査観点

1. production の `BMSFile` 参照と `BMSFile` member surface を意味で監査する。BMS storage row / LR2 `song` row / BMS-only parser / BMS player / score producer ではない参照が見つかった場合だけ、境界単位で新しい plan 項目にする。
2. install destination / warning / chart_info / maintenance-resource health は、BMS と bmson の両方に適用される chart-common concept として扱う。install destination は `BMSFile` から分離済みであり、同種の runtime property が復活していないかを監査する。warning / chart_info / maintenance-resource health については、`BMSFile` に残る property が BMS-only storage / parser / producer として説明できるかを分類する。
3. test と docs の adapter 表現を cleanup する。behavior test は `bmson_song` / `ChartFile` / `PackageChartEntry` を正本にし、旧 adapter 非生成を明示する必要がある場合だけ historical wording を残す。

### 実装メモ

- `PendingChartEntry : BMSFile` は production assembly と test fixture の両方から削除済みである。旧実装では `BMSFile` 引数 API に bmson adapter を渡して chart-common 処理へ入れる経路があったが、現行 production では `ChartFileProjection.FromBmsFile(...)` は BMS 専用、`ChartFileKindResolver.IsBmsChartFile(...)` も BMS storage row 判定専用とする。
- resource health の warning 計算は mutation から分離する。`BMSFile` の warning set を更新する入口は BMS storage row へ降りる境界として残すが、一覧 filter / projection / pending package 判定は `ChartFile` と maintenance row から `ChartWarning` list を計算する。
- bmson は parser 直後に `bmson_song.MaintenanceInfo` が存在していても、resource health の defined / existing count が未計算なら placeholder とみなし、`ChartResourceSnapshot` と現在の filesystem 状態から一時 maintenance snapshot を作る。hash が現在の `bmson_song.md5` と一致する場合だけ既存の ignored flag を引き継ぎ、hash が古い maintenance row は使わない。旧実装ではこの判定のために `PendingChartEntry` adapter を作って `SetHealthStatus()` していたが、adapter materialization は状態計算の副作用だったため移植しない。
- `BmsLibraryMaintenanceService.UpdateMaintenanceInfo(charts, ...)` は `ChartFile` を受け、BMS storage owner と `bmson_song` を内部で別 pipeline として処理する。旧実装は `bmson_song` を `PendingChartEntry` に変換して BMSFile pipeline へ混ぜ、同一 section log に載せていた。現行実装では `maintenance_target_summary` / `maintenance_update section_*` log が BMS section と bmson section に分かれる場合があるが、resource health / reparse / reused / failed / maintenance upsert / no song upsert の集計値は `MaintenanceWorkflowResult` に合算する。これは log 粒度だけの差であり、bmson を BMSFile list に混ぜる構造互換は移植しない。BMS section の resource health も `ChartFileProjection.FromBmsFile(... includeResourceReferences:false)` から chart-common 計算へ入り、missing encoding だけの target では resource scan を行わない。`HealthTargetCount` / `BmsResourceTargetCount` は実際に health scan する BMS target だけを数え、encoding-only target は `CheckedFileCount` / `MissingEncodingTargetCount` にだけ載せる。計算結果は既存 `BMSFile.maintenanceInfo` に merge し、encoding / `is_encoding_fixed` / 非 force 時の ignored flag を保持する。
- bmson resource health 計算は `ChartResourceSnapshot` と `ResourceHealthLookupContext` を使い、directory resource index を優先し、missing cache entry の時だけ実ファイル確認へ fallback する。旧実装の `PendingChartEntry.SetHealthStatusUsingLookupContext(...)` が内部で使っていた cache-hit / fallback counter は維持するが、`BMSFile` の component cache や `memClear` 副作用は bmson storage row には移植しない。
- `ChartFile` は chart domain model として audio / visual resource refs と optional image refs（stagefile / backbmp / banner）を持つ。production の resource-health / package / estimation flow は `ChartFile` projection から `ChartResourceSnapshot` を作るため、bmson flow が BMSFile adapter へ戻る必要はない。`ChartResourceSnapshot.Create(bmson_song)` は parser / test など storage owner 直近の snapshot 作成用 helper としてまだ残るが、chart-common operation surface へ `bmson_song` 入口を広げるものではない。BMS は `BMSFile` parser / component cache が BMS 専用責務として残るが、chart-common operation では projection 済み refs を正本にする。
- `ChartResourceSnapshot.Create(ChartFile)` は `ChartFile` に投影済みの resource refs を優先し、BMS owner の parser/component cache へ降りるのは projection に refs が無い場合だけにする。`ChartResourceSnapshot` の lookup key は拡張子 alias 正規化後に拡張子を落とすため、chart-common resource health 計算では movie も `ChartResourceExtensions.MovieExtensions` を使って existence check する。旧 BMS-only `SetHealthStatus(...)` は production の maintenance 更新境界から外れ、BMS parser/component cache の低レベル互換 API としてだけ残っている。pending package / installed maintenance / playlist org_md5 補助探索の resource health 判定は `BuildResourceHealthMaintenanceInfo(ChartFile, ...)` へ寄せる。
- 通常表示、folder operation、chart_info parse failure、zero-note 再確認、inline chart_info target、playlist detail、package entry の current projection は resource refs を既定では含めない。WAV/BGA/MOVIE refs は resource health / install estimation / component move planning の入力になった時だけ `ChartResourceSnapshot.Create(ChartFile)` が storage owner から読む。fallback 判定は `TotalReferenceCount == 0` ではなく、`ChartFile.AudioResourcePaths` / `VisualResourcePaths` が投影済みかを見る。軽量 projection でも stagefile / banner / backbmp は入るため、optional image だけで fallback を止めると audio / movie refs が落ちる。この差は旧実装の暗黙 full projection を維持せず、必要な境界でだけ resource refs を読むための性能上の実装メモである。
- aggregate resource snapshot の production 入口は `IEnumerable<ChartFile>` のみである。旧 `IEnumerable<BMSFile>` overload は production 参照がなく、テストだけの旧入口になっていたため削除する。単一 BMS chart の parser / component cache 読み取りは `ChartResourceSnapshot.Create(ChartFile)` 内部の BMS storage owner 分岐に閉じ、package / estimation / resource surface の集約は `ChartFile` list を正本にする。
- file diff 初期化中の inline bmson maintenance は parser 済み `bmson_song` から直接 `MaintenanceInfo` を作る。旧実装の `PendingChartEntry` shim は BMSFile API に health 計算を通すためだけの一時 object だったため、inline 成功判定、recoverable exception handling、lookup counter は維持しつつ shim 作成は移植しない。
- startup file scan diff の stale install destination cleanup は、`CreateInstalledChartSnapshot(...)` で投影済みの current chart snapshot と次世代 owner set を照合する。2026-05-22 の性能検証で、chart ごとに next BMS owner list を `Any(ReferenceEquals(...))` する O(N^2) regression が `instl_dst_cleanup_ms=355423` として出たため、BMS / bmson owner は事前に `HashSet` 化する。これは単なる高速化ではなく、install destination cleanup を BMS owner list 操作ではなく chart owner membership 判定として扱うための設計修正である。bmson も同じ cleanup 対象に含め、`bmson_song` に install destination 永続列を新設せず chart runtime state の clear delta として扱う。
- 通常一覧の virtual source rows は BMS / bmson storage owner から直接作る。以前の中間実装では `CreateStandardLibraryChartSnapshot(...)` で full `ChartFile` projection を作った後、`ChartListSourceRow` constructor が owner から current projection を再作成しており、large library の全件一覧で `main_view_build totalMs=10000ms+` の regression になった。通常 root / folder view の source row は owner-backed read model なので、最初の入力は `BMSFile` / `bmson_song` storage owner と provider delegate で足りる。`ChartListSourceRow` の identity getter は title / folder / path / level / mode / hash などを storage owner から直接読み、score / warning / maintenance health / chart_info / resource refs は必要な property や可視 row が `Chart` を読む時に current owner projection と provider から解決する。score は `BMSFile.bmsScore` attachment より `ScoreSnapshot` provider を優先し、source row の score cache version は `ScoreSnapshotVersion` に従う。可視行の `LibraryChartRow` は source row から渡された score projection を保持し、storage owner の再投影で `BMSFile.bmsScore` へ戻らない。通常一覧 row cache の再利用時も `UpdateSourceProjection(...)` で provider-backed score projection を差し替えるため、score refresh 後に古い `BMSFile.bmsScore` を表示正本へ戻さない。warning digest、install destination、maintenance health、encoding の source row getter は sort / filter の全件 access でも resource reference 配列を必要としないため、current projection を作る場合も `includeResourceReferences=false` を使う。したがって全件 source row 作成時に WAV/BGA refs の配列化、score snapshot 作成、identity `ChartFile` の二重作成を行わない。shared transient state cache prune も毎回 full snapshot を作らず、cache がある時だけ storage owner から current key を作る。
- 通常一覧の virtual source row cache は source membership / identity と sort key を分けて扱う。warning、maintenance、playlist reference、install destination、chart_info のような sort-key invalidation は order cache だけを捨て、21万行規模の `ChartListSourceRow` identity cache は捨てない。BMS path/title、bmson path/title/artist/genre/mode/level/hash など `ChartListSourceRow` の identity getter 自体が変わる理由だけ source row cache を捨てる。bmson の `SortKeyChanged` は chart_info / maintenance などの projection-only 列も含む broad signal なので、`SourceIdentityChanged` を別に持ち、`bmson_sort_key_changed` では source row cache を捨てず、`bmson_source_identity_changed` のときだけ捨てる。ただし同じ path の `bmson_song` が別インスタンスへ差し替わった場合は、source row が storage owner reference を保持しているため stale storage owner 回避のため source row cache を捨てる。2026-05-22 の性能検証では、sort-key invalidation ごとに source row cache まで捨てる中間実装により startup 後半の chart_info / maintenance 反映で全件 source row rebuild が起こり、`startup_background_summary` が 60000ms をわずかに超えた。cache 境界と初回 render の同期 index build を修正した後の確認では `startup_background_summary elapsedMs=46992` になり、抽象化作業前の 47923ms と同程度に戻った。
- chart_info / score / maintenance / warning hydration の反映は、full normal library で現在 sort/filter に影響しない場合、`ChartRowsView` を差し替えず `RefreshMainTableDisplay` message で custom table の可視 cell cache だけを破棄する。message 前に実体化済み `LibraryChartRow` の dependency cache も無効化するため、可視セル再描画だけで provider-backed chart_info / maintenance / warning / score の最新値へ到達できる。旧中間実装では hydration 完了ごとに default title sort の full view を再構築し、startup 後半や `startup_presentation_flush` で 21 万行の sort/view rebuild が重なりやすかった。source membership / identity sort key の変更、現在 sort と同じ dependency の変更、keyword / mode / folder filter 適用中、playlist detail view は従来どおり full refresh にする。
- virtual normal library source rows は中間 `List<ChartFile>` を作らず、BMS / bmson storage owner から `ChartListSourceRow` を直接積む。これは ChartFile 抽象化を戻すものではなく、source row が最初から chart identity read model であるという境界を保ったまま不要な全件 list allocation を省くための hot-path 最適化である。BMS level は LR2 song row の `int? level` から直接 `LevelText` / `Level` へ投影し、全件 identity projection で `string -> double` parse を行わない。provider-backed state の stale cache を避けるため、通常 library source row の `Chart` は都度 current projection を作り、pending / newly installed package row だけ `PackageChartEntry.ProjectionVersion` で cache する。
- chart_info sort / prewarm は `ChartListSourceRow` ごとに `ChartInfoDisplaySnapshot` を cache する。cache version は `BMSLibrary.ChartInfoIndexVersion` と package entry の `ProjectionVersion` から作り、chart_info hydration や package projection 変更後は stale display snapshot を使わない。旧中間実装では chart_info 列ごとに `ChartInfoDisplaySnapshot.FromChartInfo(...)` と provider lookup を繰り返していたため、`virtual_order_prewarm` が列数に比例して重くなりやすかった。source row cache を維持する設計では、identity row 自体は捨てず、chart_info display cache だけ index version で更新する。
- 通常一覧の source row が使う chart_info / score cache version は `MainWindowViewModel` 側で event/build 境界ごとに snapshot し、row getter からは lock なしの cached version provider を読む。`BMSLibrary.ChartInfoIndexVersion` は index lock を持つため、sort key getter ごとに直接読ませると 21 万行 x 列数の lock acquisition になりやすい。`ChartListSourceRow` は version provider が無い場合は cache を使わず live projection を読むため、テストや小さな one-shot row で stale cache を残さない。score 表示 snapshot も `ScoreSnapshotVersion` と package projection version で row-local cache し、通常一覧では `BMSLibrary.ResolveChartScoreSnapshot(...)` から `ChartScoreSnapshot` を取得する。これにより sort key access ごとに `ChartScoreSnapshot.FromBmsFile(...)` と `BMSFile.bmsScore` attachment lookup を繰り返さない。
- startup 後の `virtual_order_prewarm` は priority 1 (`Title` / `Folder` / `path` / `Artist`) のみを作る。抽象化作業前の挙動に寄せて score / chart_info の visible column まで全件 prewarm すると、readiness / `startup_background_summary` に含まれないとはいえ operable 後に 21 万行 x 多数列の sort build が走り、CPU が長時間残る。score / chart_info 列は direct row projection と display cache を持つが、startup 直後に全 visible column を先読みする必要はないため、ユーザーが実際に sort した列だけ on-demand cache に入れる。2026-05-22 の性能検証では、全 visible column prewarm は `descriptorCount=54` / `elapsedMs=13151` だったが、priority 1 限定後は `descriptorCount=8` / `elapsedMs=1518` になった。
- 通常一覧の初回描画では、行ハイライト判定のために resource health index を同期構築しない。`LibraryChartRow` / `ChartListSourceRow` の resource health provider は `TryGetCurrentResourceHealthWarningProjection(...)` で現在の snapshot だけを読み、snapshot が未構築または invalidated の場合は空 projection を返す。resource health filter や maintenance hydration は従来どおり明示的に index を構築する。2026-05-22 の性能検証では、初回 render 中に `projection_read` が full resource health index を同期構築し、`custom_table_render rowHighlightMs=3275` / `renderWorkMs=3371` になっていた。non-building provider へ切り替えた後は `rowHighlightMs=17` / `renderWorkMs=166`、`startup_ready_operable elapsedMs=26375`、`startup_background_summary elapsedMs=46992` まで戻った。
- playlist library index prewarm は full `ChartFile` representative を持たず、`LibraryChartRef` の hash / path identity を持つ。`LibraryChartRef.Directory` は lazy 計算にし、playlist index 構築では directory を読まない。playlist detail row で owned chart の表示が必要になった場合も identity-only `ChartFile` に戻してから provider-backed chart_info / score / transient state を重ねる。2026-05-23 の性能検証では、この修正により `playlist_library_index_prewarm` が `1904ms` から `555ms` になり、`startup_ready_operable elapsedMs=22083`、`startup_background_summary elapsedMs=42735` になった。
- playlist reference index の全 table 同期は、`ReplaceTable(...)` を table ごとに呼んで key 追加のたびに `PlaylistReferenceDisplay` を作り直さない。`FromTables(...)` では hash -> table list と table -> key set を先に bulk build し、最後に hash ごとに display を一度だけ作る。incremental replace / remove は従来どおり display を即時更新する。2026-05-23 の性能検証では、この修正により `playlist_ref_apply` が `4436ms` から `2349ms` になり、`startup_ready_operable elapsedMs=21375`、`startup_background_summary elapsedMs=41060` になった。
- installed chart の「存在するか」「同一 hash の別 path があるか」だけを見る処理は、全件 `ChartFile` snapshot を作らず、BMS / bmson storage row から path と primary lookup hash だけを読む軽量 lookup entry を使う。これは BMSFile API へ戻すものではなく、chart-common identity lookup を resource / warning / score / chart_info projection から切り離すための境界である。pending install の installed 判定や install-location repair の duplicate prompt は md5 優先、なければ sha256 という `ChartFile.PrimaryLookupHash` と同じ semantics を維持しつつ、21 万件規模の full projection と resource ref 配列化を避ける。
- installed chart snapshot の内部 helper は resource refs を含めるかを呼び出し側で明示する。chart_info backfill、installed directory index、duplicate grouping、installed-only display package、estimated install batch plan、folder move / merge / delete / delete-confirmation の library chart ref snapshot は path / hash / owner / transient install destination だけを読むため `includeResourceReferences=false` とする。resource maintenance / health だけは refs が計算入力なので `includeResourceReferences=true` とする。これは抽象化のために全 chart を full domain projection へ寄せるのではなく、domain model の必要 field を用途ごとに明示する性能境界である。
- `LibraryChartRef.ToChartFile()` は library file operation 用の軽量 chart projection を返し、storage owner から再 projection する場合も resource refs を含めない。folder move / merge / delete / delete-confirmation は path / owner / hash / install destination runtime state を扱う境界であり、WAV/BGA/MOVIE refs は resource health / maintenance 側で明示的に要求する。`LibraryChartRef` が overlay 済み `chartSnapshot` を保持している場合は、その snapshot の install destination state を維持する。
- BMS-only subset fallback（garbled / garble-fixed / unregistered / zero-note など）の表示用 `ChartFile` snapshot は resource refs を含めない。これらは BMS 専用 filter だが、一覧表示の read model であり resource health 計算ではないため、`WAVfiles` / `BGAfiles` の配列化は不要である。score 表示が必要な subset だけ score snapshot を含める。
- `LibraryFolderTree` の親フォルダ cache は、候補判定に chart path しか使わないため、起動時に全所持譜面を `ChartFile` 化しない。BMS / bmson storage row から path snapshot だけを作り、`BmsLibraryParentFolderCacheService` は path list と root list で判定する。
- file diff の stale install-destination cleanup は、runtime install-destination state を持つ chart だけを対象にする。`instl_dst` は DB 永続化値ではなく chart runtime state なので、通常起動で runtime state が空の場合に BMS / bmson storage row 全件を `ChartFile` 化して cleanup しない。明示的に cleanup 対象 snapshot が渡されたテスト/処理では従来どおり stale destination を clear する。
- `CustomTableColumnFactory` は通常一覧 / playlist detail の主要列を row 型で直接読む。chart row では `genre` / `mode` / `hash` / `sha256` / `WAVHealth` / chart_info 表示 text などを reflection 経由で読む必要はない。これは UI column 名や settings 名を変えずに、row surface を `ChartFile` read model として扱うための hot-path 最適化である。
- startup `chart_info_hydration` は DB から `chart_info` index と parse failure count を hydrate し、表示 read model は `BMSLibrary.ResolveChartInfo(...)` provider から読む。旧実装のように hydrate 済み `chart_info` を storage owner へ全件 attach すると、large library では owner mutation / property-check のためだけに 1 秒超の処理が追加される。現行実装では hydration load 時に `CurrentChartInfoSha256s` / `CurrentParseFailureMd5s` を作り、full backfill を省略できるかの owner summary は HashSet membership だけで計算する。DB 側 `GetChartInfoBackfillCandidateSummary(...)` は fallback として残るが、startup の all-current 判定で毎回走らせると 4 秒程度の追加 cost になるため、hydration summary が得られる場合は使わない。
- pending package discovery、install table load、pending regroup 後の warning 再初期化では、bmson の `ResourceHealth` warning は `PackageChartEntry` に直接書き戻す。BMS entry も warning classification は `ChartFile` projection から一時的に組み立てた resource health snapshot を使い、BMS owner の `maintenanceInfo` を中間 state として読まない。discovery 時に BMS parser / component cache を温めることはあるが、pending warning 判定の正本は `PackageChartEntry.Chart` である。旧実装のような `Func<BMSFile, bool>` callback や `BMSFile.Warnings` を pending package warning の中間 state として使う経路は残さない。
- pending package の resource health projection は warning と health 表示値を同じ `PackageChartEntry` state として更新する。2026-05-22 の検証では、warning だけを entry に戻して health 値を捨てる中間実装により、保留画面の WAV/BGA/MOVIE が空のままになった。また、導入先が入ると resource health digest だけを隠す旧 digest policy により、WARNING digest は消えるが tooltip は残る不整合が出た。現行では pending / package row では resource health digest を隠さず、通常 owned library row だけ既存 suppression を維持する。
- pending / newly installed package の virtual source row は detached `ChartFile` snapshot ではなく `PackageChartEntry` を保持する。導入先推定や resource health projection が background で更新されても、row getter は entry の現在 `Chart` を読むため即時反映できる。`PackageChartEntry.ProjectionVersion` は source row signature に含め、sort / filter cache が projection 変更を古い snapshot として再利用しないようにする。
- BMS chart についても、pending package の resource health projection では `ChartFile` の resource refs から一時 maintenance snapshot を作る。これは pending 表示値と strict warning 判定のための projection であり、BMS owner の `maintenanceInfo` を更新しない。installed maintenance scan の resource health も同じ chart-common 計算へ寄せるが、encoding / reload / zero-note は BMS-only boundary として storage owner へ降りる。
- `ResourceHealthIndexSnapshot.ActiveTargets` / `IgnoredTargets` は `ChartFile` を返す。旧実装の `BMSFile` target list は UI filter へ BMS adapter を渡すための構造であり、bmson を adapterless に扱う最終形では chart row projection に直接渡す。
- resource health projection の production 入口は `ChartFile` または `ChartFileKind + path + md5` identity である。通常一覧の visible row 判定や virtual source row provider は full `ChartFile` materialization を避けるため identity overload を使うが、これは BMSFile / bmson_song lookup surface を戻すものではない。FullScanAllCharts などの view logging は現在 snapshot を読むだけにし、invalidated index を log のために同期 rebuild しない。旧実装との互換として存在していた `BMSFile` / `bmson_song` projection lookup overload は削除済みであり、BMS storage row や bmson row を projection lookup の public surface として残さない。
- resource health warning の構築 API も、projection 用は `ChartFile` または `BMSFileMaintenanceInfo` のどちらかを入口にする。旧 `BMSFile` overload や `ChartFile + maintenanceInfo` overload は production 参照がなく、BMS storage row 入口や test-only 中間 API を projection surface に戻すだけだったため削除する。BMS-only maintenance workflow は引き続き `BMSFile` storage owner を直接更新するが、warning projection の read API とは分ける。
- `BMSFile` の maintenance health / encoding 表示 alias は削除済みである。旧実装では `BMSFile.encoding` / `WAVHealth` / `BGAHealth` / `MovieHealth` / `StagefileHealth` / `BannerHealth` / `BackbmpHealth` が `maintenanceInfo` を横流ししていたが、これは BMS storage row の責務ではなく chart 表示 read model の責務である。現行実装では `ChartFileProjection.FromBmsFile(...)` が有効な `BMSFileMaintenanceInfo` snapshot だけを `ChartFile` に投影し、`LibraryChartRow` / `ChartListSourceRow` / playlist detail row が `ChartFile` から表示値を読む。`maintenanceInfo` 内部 mutation を表示へ反映する producer は `BMSFile.NotifyMaintenanceInfoChanged(...)` を明示的に呼び、`LibraryChartRow` が row 表示 property に展開する。旧 alias 名への property changed を維持しないため、test は BMS storage row の互換 alias ではなく `maintenanceInfo` 通知または row 表示 property を検証する。
- resource extension lookup は `ChartResourceExtensions` へ移す。旧実装では BMS parser / LR2 song row 永続化モデルである `BMSFile` が `.wav` / `.ogg` / `.png` / `.bmp` / movie extensions まで持っていたため、bmson の scan / health / package estimation でも `BMSFile` の static 定数を読む形になっていた。現行実装では resource lookup / alias normalization は chart-common domain として扱い、BMSFile 内部の maintenance 計算も同じ `ChartResourceExtensions` を読む。旧 public 定数を互換 wrapper として残すと production で使われない BMSFile API が残るため移植しない。拡張子セットと `.ogg` / `.mp3` / `.flac` → `.wav`、`.bmp` / `.jpg` / `.jpeg` → `.png` の lookup alias は維持する。
- `BMSFile.BMSFileStatus` は BMS player status だけを表す。旧実装では row / converter / custom table column も nested BMS enum を直接読み、LR2IR score 未送信を `BMSFile.status` に混ぜていたため、bmson row の status 表示も BMSFile 型に依存していた。現行実装では `ChartFileStatus` を row display enum とし、BMS owner がある場合は BMS player flag だけを mapping する。LR2IR score 未送信は `BMSScore.IsLr2IrScoreUnsent` から `ChartScoreSnapshot` へ投影し、`ChartFileStatus.SCORE_UNSENT` として row display に重ねる。pending search の `SEARCHING` は BMS storage owner ではなく package entry projection として重ね、adapterless bmson でも表示できる。custom table の icon 優先順位（FORWARD、BACKWARD、PLAY、LOADING、PAUSE、SEARCHING、SCORE_UNSENT）と tooltip 優先順位（PLAY、LOADING、PAUSE、FORWARD、BACKWARD、SEARCHING、SCORE_UNSENT）は旧実装どおり維持する。旧 WPF status converter は resource 定義だけが残る未使用 surface だったため削除する。
- LR2IR score 未送信判定は、score table 更新時に現在の LR2 score と LR2IR player score の md5 match を比較して毎回再計算する。旧実装では `BMSFile.status` に `SCORE_UNSENT` を立てた後、同じ update 内で LR2IR 側の高い score を local score へ overwrite しても flag が残り得た。また次回の再計算で一致しても明示的に消えない場合があった。現行実装では比較後の `BMSScore` snapshot を正本にし、missing IR score / score / minbp / clear の差分から runtime-only flag を再設定する。local PA と IR FC は旧実装と同じく差分扱いしない。
- `ChartLookupKey` の production API は `ChartFile` を入口にする。旧実装では hash helper が BMSFile / bmson_song を直接受けていたため、package regroup や duplicate repair path のような chart-common lookup でも storage row overload が使われやすかった。現行実装では installed chart matching で `ChartFile.PrimaryLookupHash` を使い、storage row から呼ぶ必要がある場合も caller が warning なしの `ChartFile` projection を作る。これにより `ChartLookupKey` 自体へ BMS / bmson storage row overload を残さない。
- playlist library index は `LibraryChartRef` representative を md5 / sha256 ごとに持つ。playlist entry の解決は chart kind による優先を持たず、まず entry.md5 で chart 集合を探し、見つからない場合だけ entry.sha256 で探す。各 hash に複数 chart が一致する場合は path の ordinal-ignore-case 昇順で代表を選ぶ。旧実装では BMS storage row index と bmson storage row index が分かれていた結果として BMS md5 → BMS sha256 → bmson md5 → bmson sha256 に近い解決順になり、entry.md5 が bmson に一致しても entry.sha256 が BMS に一致する場合は BMS が選ばれ得たが、これは storage row 分割に由来する偶然の挙動であり、ChartFile index 化では移植しない。startup / prewarm hot path では全 library を `ChartFile` に投影せず、playlist detail row の表示・score・chart_info 解決が必要になった代表だけ identity-only `ChartFile` snapshot に戻す。`LibraryChartRef.Directory` も folder operation で必要になるまで lazy にし、playlist index prewarm の全件 path directory 計算を避ける。playlist score resolution は選ばれた chart identity の md5 / sha256 を読む。
- folder move / merge service 境界は `LibraryChartRef` snapshot を入口にする。旧実装では facade から `BMSFiles` と `BmsonSongs` を二系統で渡し、service 内で `LibraryChartRef` / `ChartFile` に組み直していた。現行実装では facade 側で library chart ref snapshot を作り、service は path selection / install destination rewrite / repackage source を chart ref として扱う。BMS storage row / bmson storage row への分解は unregister、song / bmson_song upsert、maintenance target の直前だけに限定する。旧実装の細かい挙動として、folder move の BMS path change は `OldPath` を明示しないため `ReplaceBmsFilePath(...)` が現在 path を old path として扱う一方、repair fix のように file move 後の BMS row では `OldPath` を持つ。この差は既存 DB 更新の安全確認に関わるため移植し、bmson path change は従来どおり `OldPath` を明示する。
- `LibraryChartRef` は library file operation 用の lightweight chart reference であり、BMS / bmson storage owner property を外部 surface として公開しない。storage owner が必要な箇所は `GetBmsStorageOwner()` / `GetBmsonStorageOwner()` で明示的に降り、package entry / mutation payload が必要な箇所は `ToChartFile()` で `ChartFile` projection に戻す。これにより folder move / merge / delete の service 境界で `LibraryChartRef.BmsFile` / `BmsonSong` を正本として扱う旧構造を残さない。
- playlist reference の表示 lookup は `PlaylistReferenceIndex.Find(ChartFile)` / `Find(LibraryChartRef)` を入口にする。md5 / sha256 の文字列 pair は index 内部の lookup detail として残し、通常一覧 source row / materialized row / playlist detail row は chart identity を渡す。旧実装では、pending / installed package の bmson が playlist reference に一致した場合、表示用 `RefTables` を持たせるためだけに `PendingChartEntry` adapter を materialize したり、既に materialized された bmson adapter の `BMSFile.RefTables` を更新したりしていた。現行実装では BMS / bmson とも `PlaylistReferenceIndex` を正本にし、storage row に playlist reference cache を持たせない。md5 優先、sha256 fallback の一致順序は維持する。
- `PackageChartEntry.GetPlaylistReferenceAdapter(...)` は削除済み。playlist reference のために adapter を作る入口は残さない。BMS storage row への `RefTables` writeback と `BMSFile.RefTables*` API も削除済みであり、production mutation / display 正本は `PlaylistReferenceIndex` である。
- playlist table の replace / remove / synchronize は pending package target を `PackageChartEntry` / `ChartFile` で照合する。旧実装では pending target snapshot が `BMSFile` adapter list だったため、bmson pending target は mutation 対象として表現されにくかった。現行実装では bmson も `targetsOldPending` などの chart match に含めるが、BMS / bmson storage row mutation ではなく `PlaylistReferenceIndex` 更新で参照表示が消える/付くようにする。
- playlist reference の全 table 同期は、library 側は `LibraryChartRef` snapshot、pending / installed package 側は `PackageChartEntry` snapshot を内部で取得して適用する。旧実装にあった `AddReferenceBMSTables(tables, files)` / `RefreshReferenceDisplayForTable(table, files)` / `RemoveReferenceBMSTables(table, files)` のような BMSFile subset 指定 API は、production からは使われず、chart-common boundary に BMSFile list を再導入する口になっていたため残さない。`BmsLibraryPlaylistReferenceService.ApplyReferenceMap(...)` の installed 側入口も `IEnumerable<LibraryChartRef>` とし、全 library `ChartFile` 投影を避けながら md5 / sha256 matching は BMS / bmson を同じ chart として扱う。細かい実装メモとして、旧 log の `matchedSongFiles` は BMS row の matching だけを示していたが、現行 log は `matchedLibraryCharts` として bmson の match も含める。match count が増えても storage row に `RefTables` を持たせる副作用は移植せず、表示は `PlaylistReferenceIndex` / row projection を正本にする。
- playlist detail の score 表示は `ScoreSnapshot` の md5 / sha256 index から直接 `BMSScore` snapshot を解決する。旧実装では未所持 row 用に `PlaylistScoreProbeBmsFile : BMSFile` を一時生成して `SetBMSScoreWithMetrics(...)` に通し、fake BMSFile の `bmsScore` を表示 source row に渡していた。現行実装では score 表示のためだけに BMSFile 派生 object を作る構造互換は残さない。owned row では解決済み `ChartFile` の md5 / sha256 を playlist entry の hash より優先する。旧実装は playlist entry 側の stale hash が非空だと別 chart の score snapshot を拾い得たが、owned row の表示は現在の library chart identity を正本にするほうが自然なので改善する。これにより所持 bmson row でも beatoraja score snapshot は `ChartFile.Sha256` で解決できる。missing row は従来どおり playlist entry md5 と entry chart_info sha256 を使う。resolved score がある場合は BMS / bmson / missing row のいずれでも clear / rank / score / combo / bp を表示し、所有状態は score が無い場合の既定値（owned chart は NO_PLAY、missing row は NO_SONG）だけに影響する。`playlist_score_probe_*` log 名は履歴上の operation 名として残るが、chunk / BMSFiles lock wait の内訳は出さず、target / matched / elapsed だけを出す。
- playlist detail source row の constructor は解決済み `ChartFile` を受け取る。旧実装では `BuildPlaylistSourceRows(...)` が `BMSFile realFile` と `bmson_song resolvedBmson` を tuple として保持し、その二股 payload を constructor に渡していた。現行実装では entry resolve の正本を `ChartFile` にし、constructor 内で BMS / bmson storage owner を `ChartFile` から読む。score probe も `BMSFile realFile` ではなく `ChartFile` の md5 / sha256 を読むため、playlist source row の chart identity 入口を保つ。playlist detail の title / artist / genre / mode / tag / hash / sha256 / path / warning / health / encoding / status / level 表示は、storage owner を個別に読み直すのではなく、まず `CreateChartFile()` が作る `ChartFile` projection を読む。BMS / bmson の install destination / warning / health 表示は、ViewModel の chart-common `ChartFileTransientState` provider がある場合は provider から再投影する。provider がない場合、bmson / metadata projection は constructor へ渡された stateful `ChartFile` snapshot を保持する一方、BMS owner-backed row は live BMS storage owner から再 projection して freshness を優先する。`ChartInfo` も production では ViewModel の chart_info projection provider を優先するため、playlist source 再利用中の chart_info index 更新は owned row / missing row のどちらにも反映できる。provider がない row では storage owner attach へ戻らず、解決済み `ChartFile` snapshot の `ChartInfo` または missing entry の `EntryChartInfo` を fallback として読む。
- playlist table への追加経路は `BMSTableEntry` を受ける。旧 `BMSTable.AddBMSTableEntriesToFolder(IEnumerable<BMSFile>, ...)` overload は production 参照がなく、BMS storage row list を playlist 追加 API へ戻す入口になるため削除する。UI の drop / paste は `ResolvePlaylistDropChart(...)` で `ChartFile` を解決し、BMS / bmson とも `BMSTableEntry.CreateForPlaylistDrop(chart, ...)` または既存 playlist entry の duplicate として table に渡す。`org_md5` は playlist entry の LR2/BMS 由来互換 field なので、BMS row では既存の同一曲 md5 探索を `BMSLibrary.GetPlaylistOrgMd5sForChart(...)` 経由で使い、bmson では従来どおり空にする。この BMS/bmson 分岐は ViewModel からは隠し、BMSFile owner を playlist drop 境界へ直接戻さない。`BMSTable` / `BMSTableEntry` という型名は playlist table の既存概念として残すが、追加対象は chart / playlist entry であり BMSFile list ではない。
- pending package membership 判定は `ChartFile` / `PackageChartEntry` / `ChartOperationTarget` を入口にする。旧 `ExtractChartPackagesFromChartFiles(...)` と `ContainsChartTarget(ChartPackage, BMSFile)` は production から chart-common package selection に BMSFile list を戻す入口になっていたため削除する。BMS player の temporary install 再生判定は BMS-only 処理だが、pending package 所属確認そのものは `ChartFileProjection.FromBmsFile(...)` で作った `ChartFile` を渡して行う。
- playlist table replace / external resync 後は `NormalLibraryReferenceTablesChangedReason` で通常一覧 sort key と playlist reference 表示を明示的に無効化する。`PlaylistTableUpdateContext.Updated` は `last_update` 更新有無を表すため、旧 DB 修復や hash 初期化のように playlist entry persistence だけが必要な場合は `ReferenceEntriesChanged` で replace callback を走らせる。旧実装では BMS row の `RefTables` property change でも表示更新され得たが、現行実装では BMS / bmson とも `PlaylistReferenceIndex` provider から表示値を読むため、index 更新と row/provider invalidation を UI 更新契約にする。
- folder delete / move / merge に伴う install destination cleanup は、pending package では `PackageChartEntry.Chart.InstallDestination` を読む。library row 側は `LibraryChartRef` を入口にして、BMS storage owner を持つ `ChartFile` を `LibraryInstallDestinationChange.Chart` に保持し、service 内では直接 `BMSFile` を変更しない。`instl_dst` は UI / column 名として残るが、BMS song row の永続列でも BMS storage owner property でもなく chart runtime state なので、entry のない installed BMS owner writeback は行わず、`BMSLibrary` の one-shot buffer / model-side overlay と ViewModel `ChartFileTransientState` cache へ反映する。`bmson_song` にも `instl_dst` 相当の永続列は新設せず、library bmson row の install destination も同じ chart runtime state として扱う。旧実装では cleanup helper が最初から `IEnumerable<BMSFile>` を受けていたが、現行実装では chart-common operation 境界を保ちつつ、BMS owner に install destination runtime state を持たせない。
- package membership / selection target の一致判定は `PackageChartEntry.IsSameChartTarget(...)` へ寄せる。判定は `ChartFile.Kind` を揃えた上で、同一 entry / BMS storage row / bmson storage row reference、最後に path 一致を見る。旧実装の一部には path が無い時に `PrimaryLookupHash` fallback で一致させる経路があったが、同一 hash の別 package chart を package 所属と誤認する可能性があるため移植しない。install destination resolve や playlist reference のような「同一譜面候補を hash で探す」処理は別責務であり、package membership では hash を使わない。
- split pending package の regroup 時に同じ chart target を重複除外する判定も `PackageChartEntry.IsSameChartTarget(...)` を使う。旧実装では `BMSFile` adapter reference と path の二重判定で重複を落としていたため、adapterless bmson は path だけが identity になり、同じ `bmson_song` owner を持つ path-less / path-changed entry を別 chart として扱い得た。regroup は package membership の再構成なので、adapter を読まず chart identity に寄せる。
- `ChartPackage.ReplaceChartEntries(...)` / `PackageChartDiscoverySnapshot.ReplaceChartEntries(...)` は `PackageChartEntry.ToChartEntrySnapshot()` で snapshot 化する。BMS は LR2 `song` row owner を保持するため `BMSFile` adapter を残すが、bmson は `bmson_song` storage row と `ChartFile` projection を正本にし、materialized `PendingChartEntry` adapter は snapshot へ持ち越さない。これは旧実装の「bmson adapter があれば BMS 側 source として扱う」挙動を改善するもので、表示 source snapshot は `PackageChartEntry.Chart` を `ChartFile` list として渡し、BMS / bmson storage row の二列 snapshot へ戻さない。
- `LibraryChartRow.FromPackageChartEntry(...)` は `ChartFile.Kind` を優先する。旧実装では package entry が materialized bmson adapter を持ち得たが、現行の表示 row の storage shape は `bmson_song` 側に寄せ、BMS storage owner としては持たない。operation target は `PackageEntry` を保持するため、package 操作では row の `CompatibilityBmsFile` に戻らない。BMS-only row だけが `GetBmsStorageOwner()` で BMS storage owner を返す。
- `LibraryChartRow` の入力境界は `ChartFile` である。`FromBmsFile(...)` / `FromBmsonSong(...)` は storage owner から warning なしの `ChartFile` projection を作って row を初期化し、`Chart` getter では owner の現在値と transient state から再 projection する。`FromChartFile(...)` も owner-backed projection では live owner から再 projection し、caller supplied projection に install destination / warning / health / encoding などの差分 state がある場合だけ、その差分を field 単位で重ねる。旧実装では `chartOverride` が projection instance を固定していたため、repair / warning 表示 state のように ViewModel 側で作った projection instance を operation target へ渡せた。現行実装では参照同一性ではなく、owner identity と transient state の値を維持しつつ、通常 library row の owner-backed freshness を落とさない。virtual subset materialization も owner を直接 `FromBmsonSong(...)` へ戻さず、この `ChartFile` entrypoint を通す。
- `LibraryChartRow` の identity / 表示用 getter（title、artist、genre、mode、tag、level、hash、sha256、chart_info など）は `Chart` projection を正本にする。owner-backed row の `Chart` getter は現在の `BMSFile` / `bmson_song` と transient state から再 projectionし、chart_info projection provider があれば `BMSLibrary.ResolveChartInfo(...)` の結果を重ねるため、property change / hydration / index 更新追従は維持する。metadata-only の `ChartFile` や package entry 由来 projection では caller supplied projection をそのまま読む。旧実装では通常一覧 row が常に storage owner を持つ前提だったため、projection-only chart row で値が落ち得た。現行実装では row 外部が storage owner property を直接読まず、chart-common getter は `ChartFile` domain model に寄せる。細かい実装メモとして、`FromChartFile(...)` で渡された owner-backed projection は、plain projectionなら live owner から再投影し、`WithPackageState(...)` などで baseline owner と異なる transient state が載っている場合だけその state を field 単位で重ねる。warning なし projection は「warning を空にする意図」ではなく表示用軽量 snapshot とみなし、live owner の warning を mask しない。このため `row.Chart` の参照同一性は契約にせず、operation target も kind / path / hash / storage owner identity で扱う。`Folder` / `path` は folder move / rename 後の summary / sort が owner-backed row の live path を読む既存挙動を保つため、BMS / bmson storage owner の現在値を優先する。これは `FromChartFile(ChartFileProjection.FromBmsFile(file))` のような owner-backed snapshot が stale folder を返す副作用を避けるためで、外部 consumer に owner property を戻すものではない。`Level` / `Folder` の setter は残さず、FOLDER 手動編集は BMS / bmson とも chart operation の folder rename / move 経路で扱い、LEVEL 編集は playlist detail entry-level 編集に限定する。
- owner-backed row の live projection は `ChartFileProjection.FromStorageOwner(...)` / `FromStorageOwnerWithTransientState(...)` へ集約し、chart_info だけは ViewModel から渡される projection provider があれば provider を優先する。`LibraryChartRow` / `ChartListSourceRow` / `PlaylistDetailSourceRow` は BMS / bmson の storage owner 分岐を個別に再実装せず、`ChartFile` に保持された owner identity から現在 snapshot を作る。bmson の subtitle / maintenance health / encoding は従来どおり `bmson_song` storage row を transient state より優先し、BMS は従来どおり `FromBmsFile(...)` に transient state を重ねる。`LibraryChartRow.UpdateFromBmsonSong(...)` の後は現在の `bmson_song` reference を owner identity として projection helper に渡し、古い source projection に戻らない。
- `LibraryChartRow` が購読する BMS storage owner の `PropertyChanged` は `LibraryChartRowSourceNotificationMapper` で chart row 表示 group へ写像する。`BMSFile.Warnings` / `ChartInfo` / `bmsScore` / `maintenanceInfo` は BMS storage row が発行する通知名として mapper 内だけで扱い、row 本体は WARNING / chart_info / score / maintenance の表示更新 group だけを見る。空 property 名は従来どおり全 group の再通知として扱う。
- non-virtual fallback の `BuildStandardLibraryRowsForView(...)` も `ChartFile` list を入口にする。旧実装では `IEnumerable<BMSFile>` と bmson `LibraryChartRow` list を分けて受け、BMS row cache と bmson row cache を呼び出し側で結合していた。現行実装では ViewModel が BMS / bmson storage row を warning なしの `ChartFile` snapshot に投影し、row factory が `NormalLibraryRowCache.GetOrCreate(ChartFile, ...)` を呼ぶ。BMS owner-backed row は BMS owner reference で再利用し、bmson owner-backed row は bmson sync 済みの path / source-reference cache から再利用する。これにより通常一覧の fallback 経路でも chart source 境界へ `BMSFile` list と bmson row list の二本立て API を残さない。BMS-only subset の garbled / garble fixed / unregistered / zero-note は挙動として BMS 専用のままだが、virtual source selector は `ChartFile` snapshot を返す。
- normal-library の row cache は `ChartFile` を entrypoint にする。BMS row reuse は同一 `BMSFile` object に対する `LibraryChartRow` を再利用し、property change / hydration / manual edit の追従を owner reference に結び付ける。bmson row reuse は `SyncBmsonRows(...)` が path 昇順の storage snapshot から membership / source-reference / sort-key snapshot を更新し、同期済み row だけを cache の正本にする。同期前に `GetOrCreate(...)` で bmson chart を materialize した場合は旧実装どおり detached row として返し、membership / sort-key state へは登録しない。BMS prune は `BMSFiles` collection change に対する BMS-only cleanup なので、削除判定の入力は引き続き BMS storage owner collection とする。
- main view の non-virtual fallback でも BMS-only subset の row materialization は `ChartFile` snapshot を入口にする。旧 `ToLibraryChartRows(IEnumerable<BMSFile>)` helper は、caller が BMSFile list を chart-common 表示 source として戻せる口になっていたため削除する。BMS-only subset 自体は BMS 専用のままなので、row factory は `ChartFile.GetBmsStorageOwner()` がある場合だけ従来の BMS owner-backed `LibraryChartRow.FromBmsFile(...)` へ降り、property change 追従の挙動は維持する。これらの subset row は従来どおり regular row cache の再利用対象ではない。
- 一覧 summary の folder count も、`LibraryChartRow` / `PlaylistDetailRow` では row getter を優先し、未知 row 型だけ `GridRowResolver.TryGetChartFile(...)` の `ChartFile.Folder` へ fallback する。旧実装は row 型ごとの getter を直接見ていたため、owner-backed row の path / folder mutation 後も current owner を読めた。`ChartFile` fallback を先にすると `LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(file))` のような owner-backed snapshot が stale folder を返し得るので、その副作用は移植しない。
- `ChartListSourceRow.ChartInfo` は provider があれば `BMSLibrary.ResolveChartInfo(...)` の index 解決を読む。provider が無い projection-only `ChartFile` では `sourceChart.ChartInfo` を fallback として読む。旧実装では virtual source row が常に `BMSFile` / `bmson_song` owner を持つ前提だったため、duplicate / package / parse-failure などの projection-only subset で chart_info sort key が落ち得た。owner-backed row の hydration / index 追従は provider で維持し、storage owner property へは戻らない。playlist detail source row も同じく provider を優先し、missing entry だけ `EntryChartInfo` を読む。view row materialize 時には source row の `Chart` snapshot を再投影し、表示値と operation target 用 `ChartFile` の chart_info がずれないようにする。ただし playlist detail の bmson row は、playlist 解決時に渡された `ChartFile` projection の title / artist / warning / install destination などを優先する契約があるため、owner hydration 後は projection 全体を storage owner で置き換えず、provider / `ChartFileProjection.WithChartInfo(...)` で `ChartInfo` だけを差し替える。
- `ChartListSourceRow` は `ChartFile` projection を constructor の正本にする。BMS / bmson の storage owner は `ChartFile.GetBmsStorageOwner()` / `ChartFile.GetBmsonStorageOwner()` から private field として取り出し、owner-backed row では `Chart` getter が現在 owner と transient state から再 projection する。旧実装では `BMSFile` / `bmson_song` を constructor 引数として持ち、必要に応じて内部で `ChartFile` を組み立てていたが、virtual source row の責務は chart identity / sort / filter read model なので、入力境界と外部 surface を `ChartFile` に寄せる。ただし `HasSourceChartProjection` は「caller supplied `ChartFile` projection を materialization 時にも尊重する」契約であり、storage owner が無いことを意味しない。通常 library の owner-backed source row はこの flag を立てず、`LibraryChartRow.FromChartFile(...)` へ誤って materialize しない。bmson owner-backed projection で provider transient state と source projection warning が両方ある場合は、provider の install destination state を優先しつつ、provider が warning を持たないときだけ source projection warning を fallback として重ねる。
- virtual source rows signature は storage owner の有無ではなく `ChartFile.Kind` を使う。旧実装では ownerless BMS metadata row が `BMSFile == null` のため bmson / metadata-only 側と同じ区分になったが、現行では BMS metadata chart として区別する。signature には path / md5 / sha256 も含まれるため cache identity の安定性は維持する。
- context menu の BMS-format 専用選択 helper は `GetSelectedBmsFormatCharts(...)` として `ChartFile` を返す。旧 `GetSelectedBmsChartFiles(...)` は selection 時点で `BMSFile` に潰していたため、invalid extension rename のような chart operation shaped command でも View / ViewModel 境界が `BMSFile` list になっていた。現行実装では View / ViewModel / BMSLibrary の入口は `ChartFile` とし、encoding fix / audio convert / 実ファイル rename のような BMS-only mutation 直前だけ `ChartFile.GetBmsStorageOwner()` を読む。これにより bmson を adapter 化する余地を作らず、BMS-format capability は `ChartFile.Kind` の predicate として扱う。
- pending zero-note / invalid-extension rename の snapshot も `ChartFile` を返す。旧実装では pending package の BMS-format chart を `BMSFile` snapshot として取り出してから rename pipeline に渡していたが、現行実装では pending package membership は `PackageChartEntry.Chart` を正本にし、path / BMS owner reference の dedupe を維持した上で `ChartFile` snapshot を渡す。zero-note 判定と rename 実行は BMS parser / filesystem mutation 境界なので `BMSFile` へ降りるが、その変換は `BmsLibraryPackageInstallService` 内部に閉じる。
- installed library の invalid-extension rename も `ChartFile` list を入口にする。処理対象は BMS-format chart だけなので、実際の filesystem rename / duplicate delete callback の直前で `ChartFile.GetBmsStorageOwner()` へ降りる。bmson は対象外であり、BMSFile list を service surface に戻して mixed chart selection を二本立てにしない。
- `ChartListSourceRow` の production 入口は、通常 root / folder hot path では storage owner identity、subset / package / projection-only path では `ChartFile` または `PackageChartEntry` である。旧 `BuildStandardLibraryRows(IEnumerable<BMSFile>, IEnumerable<bmson_song>, ...)` は削除済みだが、通常 library の 21万行規模 hot path では全件 `ChartFile` list を作らないため、internal factory として `FromBmsStorageOwner(...)` / `FromBmsonStorageOwner(...)` を持つ。これは BMSFile API へ戻す互換 surface ではなく、source row が chart identity read model であることを保ったまま projection allocation を避けるための境界である。package / duplicate / metadata-only など caller-supplied projection を正本にする経路は `PreserveSourceProjection` または `PackageChartEntry` live provider を使い、warning / install destination snapshot を落とさない。
- ChartInfo 表示列の派生値は `ChartInfoDisplaySnapshot` に集約する。旧 `BMSFile.ChartLevelText` / `ChartTotalSortKey` などは BMS storage row に chart-common display API を生やしていたため削除し、`LibraryChartRow` / `ChartListSourceRow` / `PlaylistDetailSourceRow` が `ChartFile.ChartInfoDisplay` または現在の `ChartInfo` から snapshot を読む。通常一覧 / playlist detail / zero-note 判定では `BMSLibrary.ResolveChartInfo(...)` の md5 / sha256 index を優先し、`BMSFile.ChartInfo` / `bmson_song.ChartInfo` property は持たない。startup hydration は `ChartInfoIndex` を更新し、BMS / bmson storage owner へ hydrate 済み `ChartInfo` を attach しない。hydration 後の full backfill skip 判定は、hydration load 時に作った current chart_info sha256 set / current parse-failure md5 set と storage owner identity の membership を見るだけにし、owner mutation は行わない。inline parse / digest backfill も parser result rows を DB / index / callback に渡すだけで、直近 owner へ短命 cache として載せない。
- `ChartFile.GetBmsStorageOwner()` / `GetBmsonStorageOwner()` は、domain `ChartFile` から永続化 owner へ降りる明示的な境界である。row cache / BMS player / package entry materialization など BMS-only または owner-backed な処理はこの helper を読む。storage owner property 自体は private にし、ViewModel 側で `chart.Kind` と raw owner property を直接組み合わせる分岐は増やさない。
- `GridRowResolver.TryGetChartFile(...)` / `TryGetChartOperationTarget(...)` は raw `BMSFile` を chart-common row として受けない。旧実装では直接 `BMSFile` を渡すと `ChartFileProjection.FromBmsFile(...)` で chart operation target に変換できたが、production ではこの入口は BMS player / BMS-only helper 以外から使われておらず、test-only compatibility surface になっていた。現行実装では raw `BMSFile` は `TryGetBmsPlayerFile(...)` と display / hash helper の BMS-only fallback にだけ残し、chart operation が必要な場合は caller が `LibraryChartRow` や `ChartFile` projection を明示的に作る。
- `PlaylistDetailSourceRow` / `PlaylistDetailRow` は `RealFile` / `ResolvedBmson` property を公開しない。旧実装では playlist detail row の外部 consumer が BMS / bmson owner を個別 property で見ていたが、現行実装では `ChartFile` を正本にし、storage owner が必要な箇所だけ `GetBmsStorageOwner()` / `GetBmsonStorageOwner()` を読む。`CommitPlaylistRow(...)` の bmson identity repair や `TryGetBmsPlayerFile(...)` も `row.Chart` から owner を取り出す。source row 内部は解決済み `ChartFile` snapshot を保持し、score は row 構築前に `ChartFile` identity で解決する。chart_info / playlist reference snapshot 構築で storage owner が必要な場合も `ChartFile` から読むだけで、row API surface には個別 owner property を戻さない。
- `PlaylistDetailSourceRow` の warning / install destination / health / encoding 表示値は `ChartFile` projection と ViewModel の chart-common transient state provider を読む。旧実装では BMS row の `WarningDigestText` / `instl_dst` / `WAVHealth` / `encoding` へ fallback していたが、BMS owner 由来の install destination は `ChartFileProjection.FromBmsFile(...)` ではなく caller supplied projection / transient cache / model overlay から重ねるため、playlist detail read model から BMSFile 表示 API への直接 fallback は残さない。
- `PlaylistDetailSourceRow` の score 表示値は `ChartScoreSnapshot` を正本にする。playlist score resolution から渡る `BMSScore` は constructor 境界で `ChartScoreSnapshot.FromBmsScore(...)` に変換し、score snapshot がない場合は `ChartFile.Score` を読む。旧実装では owned BMS row だけ `bmsOwner.bmsScore` へ直接 fallback していたが、BMS / bmson / missing row の score 表示を chart-domain snapshot へ揃える。`ChartScoreSnapshot` の rank / clear normalization は従来の snapshot 実装を維持し、この変更では score 表示仕様を変えない。
- BMS storage row の warning 表示 text property と warning 表示 alias は削除済みである。`BMSFile` は structured `Warnings` と mutation helper だけを持ち、warning digest / tooltip / display text は `ChartWarningCollection` または `ChartWarningProjectionFormatter` が作る。`BMSFile.Warnings` の変更通知は `LibraryChartRow` で `DisplayWarning` / `WarningDigestText` / `WarningTooltipText` / `HasHighlightedWarning` / `HasZeroNoteMismatchWarning` の row 通知に展開する。
- table context menu の playlist missing 判定は `ChartOperationTarget.IsPlaylistMissing` だけを見る。旧 fallback の `GridRowResolver.IsPlaylistRow(row) && BMS player 対象が無い` 判定は、owned bmson playlist row も BMS player 対象ではないため missing と誤認し得る BMS-only 判定だったので移植しない。`TryGetChartOperationTarget(...)` が失敗する row は chart operation target として表現できないので、通常 menu を選ぶ。
- `ChartOperationTarget.ToPackageChartEntry()` は、`PackageEntry` がない loose target では `PackageChartEntry.FromChart(Chart)` で entry を作る。BMS storage owner がある chart でも loose target materialization のために BMSFile 専用 factory へ落とさず、`ChartFile` projection を正本にする。bmson では compatibility provider を呼ばない。旧実装では repair / install destination 用 snapshot の lazy entry materialization 時に provider から bmson adapter を作り、adapter の `instl_dst` / warning を overlay し得た。現行実装では、operation target 作成時点の `ChartFile` snapshot に含まれる state だけを使い、後段処理のためだけに loose bmson adapter を materialize する副作用は移植しない。
- installed location repair の maintenance target は `ChartFile` に統一する。旧実装由来で BMS は `List<BMSFile>`、bmson は `List<ChartFile>` に分かれていたが、fix mutation の入口は `RepairCharts` であり、修復後の resource maintenance も chart-common operation なので、BMS だけ BMSFile-only result list へ戻す中間表現は残さない。
- parent folder cache は installed `ChartFile` snapshot から root candidate を判定する。旧実装では cache invalidation 自体は bmson 更新でも走っていたが、rebuild 入力が `BMSFiles` だけだったため、LR2 mode で `.lr2folder` を含む root は bmson だけを持っていても空 root と同じ扱いで除外され得た。現行実装では BMS / bmson とも `ChartFile.Path` で譜面存在を判定する。UI / settings 名の BMS root という語彙は永続設定境界のため維持する。
- `GridRowResolver.IsPlaylistRow(...)` / `GetPlaylistEntry(...)` は `PlaylistDetailRow` と `PlaylistDetailSourceRow` の両方を対象にする。旧実装では operation target 作成時だけ source row を playlist row 扱いし、entry 取得 helper は view row だけを見ていたため、virtual playlist source row を直接渡す経路では owned bmson row と missing row の root folder drop policy が view row 経路とずれ得た。現行実装では source / view row の entry 解決を対称にし、playlist entry 複製や編集可否の判断を row materialization 状態に依存させない。
- URL からの direct download / install 入口は、chart ファイル判定を `ChartFileKindResolver.IsSupportedChartFilePath(...)` に寄せる。旧実装では `BMSFile.bmsExtensions` と archive extension の結合で許可判定していたため、単体 `.bmson` URL は archive ではない chart として扱えなかった。現行実装では BMS / bmson の direct chart URL を同じ chart file として許可し、archive extension は download/install 固有の別リストとして残す。UI 文言の download/install 機能名は変更しない。
- `PackageInstallExecutionResult` / estimated install batch state は bmson adapter list を持たない。install 後の package entry は `ChartPackage.ReplaceChartEntries(...)` の snapshot 化で bmson adapter を落とし、`bmson_song` storage row を正本に戻すため、adapter-backed / adapterless の差は install result に残さない。旧実装は空になり得る `AddedBmsonAdapters` を BMSFiles 差し替えや maintenance target の分岐に残していたが、bmson は `AddedCharts` から派生する `bmson_song` 更新と maintenance target 作成で扱う。BMSFiles の差し替え対象は BMS storage row のみとし、bmson path を BMSFiles から除去する旧互換処理は「bmson を BMSFiles に入れない」前提へ寄せて移植しない。
- estimated install batch plan の installed hash 入力は `ChartFile` list である。旧実装では `BMSFile` list と `bmson_song` list を service に渡し、service 内で installed hash set を合成していた。現行実装では `BMSLibrary` が installed chart snapshot を作って service へ渡し、service は `ChartFile.PrimaryLookupHash` だけで installed / duplicate-in-batch 判定を行う。これは hash set 作成だけの chart-common 境界なので、BMS / bmson storage row の二系統引数は残さない。
- package install service 内に残っていた `DeduplicateFilesByPathOrReference(IEnumerable<BMSFile>)` は production 参照がなく、BMSFile list 入口を復活させるだけの旧 helper になっていたため削除する。chart-common duplicate / install planning の dedupe は `PackageChartEntry` / `ChartFile` / install destination target の identity helper に寄せ、BMS storage row list 専用 helper を中間表現として残さない。
- install execution の storage row upsert / maintenance / library state apply callback は `PackageInstallExecutionResult` を受け取る。旧実装では callback payload が BMSFile list だったため、通常 install の bmson は callback の外側で追加 upsert / maintenance / `BmsonSongs` 差し替えを行っていた。現行実装では BMS と bmson を同じ result-level callback で処理し、BMS-only の zero-note / score だけを `AddedCharts` から派生した BMS storage owner list に限定する。通常 install の bmson storage row upsert は BMS と同じ song-db phase に寄せるため、後続の inline chart_info persist でも再 upsert され得るが、これは既に BMS で行っていた冪等 upsert と同じ扱いにする。
- resource maintenance の model-facing entry は `ChartFile` を primary にする。`BMSLibrary` は受け取った `ChartFile` list をそのまま `BmsLibraryMaintenanceService.UpdateMaintenanceInfo(...)` へ渡し、service 内部で BMS storage owner と `bmson_song` に分ける。service 内の BMS / bmson 二本立て pipeline と `MaintenanceWorkflowResult` の合算は維持するが、UI / ViewModel / filter 側へ `BMSFile` list を chart-common entry として公開しない。
- `ChartStorageTargetSet` は `ChartFile` から BMS storage row / `bmson_song` へ降りる永続化境界の helper である。DB upsert、inline chart_info persist、library state apply のように storage row が必要な箇所でだけ使い、resource maintenance service や UI filter へ BMS / bmson の二系統 list を再公開するための互換 API としては扱わない。
- 全所持譜面の resource health 再計算は、旧 `BMSFiles + includeInstalledBmson` の暗黙指定ではなく、BMS / bmson を結合した `ChartFile` snapshot を入力にする。対象集合は従来どおり全 BMS + installed bmson だが、log の input count は chart count として扱い、section log は service 内部の BMS section / bmson section に分かれ得る。
- estimated install batch の deferred maintenance state は `DeferredMaintenanceCharts` として `ChartFile` を保持する。`BMSLibrary` は batch 末尾で path / kind dedupe した `ChartFile` list を `setMaintenanceInfo(...)` に渡し、service boundary で BMS row と `bmson_song` へ再分割する。旧実装では deferred target list 自体に BMS / bmson の二本立て payload を持ち、さらに古い実装では bmson adapter を BMSFile list に混ぜていたが、batch result / BMSLibrary orchestration / maintenance service のいずれでも chart-common payload は `ChartFile` を正本にする。inline chart_info は deferred maintenance charts から split した bmson songs と installed package から解決した bmson songs を path dedupe して渡し、resource-only display package 由来の既存挙動を落とさない。旧実装では BMS だけ path dedupe していたが、現行では batch maintenance chart 全体を kind + path で dedupe する。
- folder merge 後の maintenance 再計算も、移動済み BMS / bmson と移動先既存 BMS / bmson を `ChartFile` snapshot にして `setMaintenanceInfo(...)` に渡す。旧実装では移動済み bmson と移動先既存 bmson を `PendingChartEntry` adapter にして BMS target list へ混ぜていたが、merge orchestration と maintenance service の正本は `bmson_song` であり、adapter 作成は移植しない。
- `LibraryMutationDelta` の unregister payload は `ChartFile` list、path mutation payload は `ChartFile` backed な `LibraryChartPathChange` に統一する。旧実装では BMS 用の `FilePathChanges` / `FilesToUnregister` と bmson 用の `BmsonSongPathChanges` / `BmsonSongsToUnregister` が並列に存在し、mutation result の外部 surface が BMS-first だった。現行実装では `ChartPathChanges` / `ChartsToUnregister` を applier が `UnregisterCharts(...)` として受け、BMS storage row と `bmson_song` への分割は DB apply / unregister 直前だけに限定する。`LibraryChartPathChange` も `BmsFile` / `BmsonSong` property を公開せず、必要な箇所だけ `GetBmsStorageOwner()` / `GetBmsonStorageOwner()` で明示的に storage row へ降りる。旧実装の細かい挙動として、folder move の BMS path change は `OldPath` を明示しないため `ReplaceBmsFilePath(...)` が現在 path を old path として扱う一方、repair fix のように file move 後の BMS row では `OldPath` を持つ。この差は既存 DB 更新の安全確認に関わるため移植し、bmson path change は従来どおり `OldPath` を明示する。
- `LibraryMutationDelta.RaiseLibraryChartsChanged` は、BMS / bmson のどちらの path mutation でも一覧・playlist index を更新する chart-common 通知を表す。旧名 `RaiseBmsFilesChanged` と internal log reason `library_bmsfiles_changed` は BMS row だけの変化に見えたが、bmson root move でも同じ更新経路を使うため、内部名と reason は chart 名へ寄せる。実際に `BMSLibrary` が UI へ通知する property は既存 binding との兼ね合いで `BMSFiles` のままだが、これは BMS-only semantics ではなく library chart view の更新 trigger として扱う。
- resource warning ignore / unignore の model 入口は `ChartFile` のみを production surface とし、BMS / bmson の maintenance row を `ChartFile` から解決する。旧 `BMSFile` list 入口は installed bmson を `PendingChartEntry` adapter にして同じ list へ混ぜていたが、production 参照がなくなったため削除する。bmson の ignore flag 正本は `bmson_song.MaintenanceInfo` とし、旧 adapter の maintenance snapshot へ同期する副作用は移植しない。
- resource warning ignore / unignore の service API は `SetChartResourceWarningsIgnored(...)` とする。旧名 `SetFilesWarningIgnored(...)` は chart 共通 operation になった後も files / BMSFile 寄りの名前を残していたため削除し、テストからだけ参照される互換名も残さない。
- `setMaintenanceInfo(...)` の resource health delta は、実際に maintenance 対象として受け取った `ChartFile` list を使う。旧実装由来の BMSFile-only delta target parameter や `includeInstalledBmson` flag は production 参照がなく、bmson maintenance target を adapter 化して単一 BMSFile list へ混ぜる入口になり得るため残さない。全件 snapshot が必要な場合は `BMSLibrary` 内部で明示的に BMS / bmson の `ChartFile` snapshot を作る。
- zero-note warning 再確認は BMS-only operation だが、入口は `ChartFile` list とする。対象抽出は `ChartFile.Kind == Bms` と `BMSLibrary.ResolveChartInfo(...)` 由来の `notes` を読み、実ファイル再確認 / warning mutation の直前だけ `ChartFile.GetBmsStorageOwner()` で BMS storage owner へ降りる。resolver が無い service-local test / fallback 経路では `ChartFile.ChartInfo` を読む。これにより、zero-note subset / recheck の chart_info 判定は chart-info index を正本にし、bmson を BMSFile list へ混ぜる余地を残さない。
- ViewModel の shared `ChartFileTransientState` prune は `ChartFile` snapshot を入口にする。旧実装由来の BMS row list + bmson row list の二本立て入口は、runtime-state key を作る直前で storage identity projection するだけだったため、通常 library snapshot 作成側で `ChartFile` に寄せる。これにより、表示 state cache の lifecycle 管理にも BMSFile / bmson split API を残さない。
- `PackageChartEntry.GetOrCreateCompatibilityAdapter()` は production surface から削除済みである。BMS format chart で BMS parser / maintenance API が必要な箇所は `PackageChartEntry.Chart.GetBmsStorageOwner()` を使い、bmson adapter materialization は production の package entry API では提供しない。旧 test が adapter writeback を検証する場合は test-local helper に閉じ込め、後続 cleanup で chart state assertion へ寄せる。
- `PackageChartEntry` 内の mutable writeback target は state の種類ごとに分ける。package warning / install destination / pending search status は BMS / bmson 共通の runtime / pending state なので、BMS owner がある entry でも `PackageChartEntry` の projection state を正本にし、BMS owner へは書かない。ただし BMS owner の BMS 専用 warning まで freeze / mask しないよう、entry warning は category overlay として合成する。BMS owner status は projection 元として読むだけで、pending search の `SEARCHING` は `PackageChartEntry` の `ChartFile.Status` projection として重ねる。bmson の installed path / folder は `bmson_song` に書き、pending warning / install destination は `PackageChartEntry` の projection state に保持する。`PackageChartEntry` は BMS owner 専用の別 field を持たない。旧実装では adapter-backed bmson の adapter fields も同時に更新され得たが、bmson adapter は最終形の storage owner ではないため、この副作用は移植しない。
- `PackageChartEntry.Chart` は BMS owner だけを live projection するのではなく、BMS / bmson の storage owner がある場合は `CreateCurrentChartFromStorageOwner()` で現在 projection を作る。install destination / warning / searching status はその上に entry projection state として重ねる。install destination の apply / restore / metadata 更新は kind 分岐を持たず entry state を更新するだけにし、修正検索の同期 root も `PackageChartEntry.GetStorageMutationSyncRoot()` で BMS owner / bmson owner / entry の順に選ぶ。これにより package entry の chart-common runtime state が BMSFile owner だけを特別扱いしない。
- pending package の resource health warning 計算では、旧実装のように `requiresPendingWarning(BMSFile)` へ BMS / bmson を渡さず、`PackageChartEntry` / `ChartFile` / maintenance snapshot から warning list を返す。BMS entry も bmson entry も `BmsLibraryMaintenanceService.BuildResourceHealthMaintenanceInfo(ChartFile)` で一時 maintenance snapshot を作り、`BMSFile.SetHealthStatus()` や `BMSFile.Warnings` を strict warning 判定の中間状態として使わない。旧実装では callback が任意の warning を `BMSFile` に積めたが、production では resource health warning list だけが必要なため、test 用 callback 互換も残さない。
- pending package の install destination projection は BMS entry でも `PackageChartEntry` が保持する。BMS owner の旧 transient property へは同期しない。`PackageChartEntry.Chart` は BMS owner から作った `ChartFile` に package state を重ねて返すため、表示 / validation / regroup / install result cleanup の正本は entry chart projection である。
- `PackageChartEntry.GetOrCreateBmsFormatAdapter()` と `PackageChartEntry.GetExistingBmsFormatAdapter()` は production surface から削除済みである。旧実装では名前上 adapter creation / existing adapter 境界に見えたが、最終的には BMS storage owner / 既存 BMS adapter を読むだけになっていたため、BMS format chart は `PackageChartEntry.Chart.GetBmsStorageOwner()` に統一する。起動時 pending warning 初期化や BMS extension rename snapshot のように BMS parser / maintenance API が必要な箇所も `Chart.GetBmsStorageOwner()` を読む。旧 `GetOrCreateCompatibilityAdapter()` は bmson も materialize できるため、chart-common 経路から直接呼ばない。
- install result からの BMS storage owner 抽出、install estimation の lock target、folder merge 後の BMS upsert target は `PackageChartEntry.Chart.GetBmsStorageOwner()` を読む。bmson は旧 adapter-backed 形由来であっても BMS storage owner を持たない扱いにし、chart-common 表示 / resource / install metadata は `ChartFile` / `PackageChartEntry` 側を読む。package source row / playback-stop target snapshot は `PackageChartEntry.Chart` を直接読むため、表示・snapshot のためだけに BMS-only helper へ降りない。playlist reference の `RefTables` 書き戻しは BMS storage owner 抽出の正当な残存用途とは扱わず、`PlaylistReferenceIndex` 正本化で削除する。
- install 後の source folder safe cleanup は、残存 chart がすべて導入済み hash かだけを確認する。bmson 残存 chart は `BmsonSongParser.Parse(...)` で `bmson_song` として lookup hash を読み、BMS 残存 chart は `ChartFileContentReader.ReadSnapshot(...)` で MD5 を読む。`PendingChartEntry.CreateFromFilePath(...)` へは通さない。旧実装では hash 確認だけのために `PendingChartEntry` を作っていたが、cleanup 判定は chart identity の照合であり adapter state を必要としないため移植しない。
- chart lookup hash の共通 helper は `ChartLookupKey` へ移す。旧 `PendingChartEntry.GetPrimaryLookupHash(...)` / `GetPrimaryLookupHashKind(...)` と `PendingChartLookupHashKind` は chart identity の概念であり、pending adapter 固有ではないため production surface から削除する。MD5 優先、なければ SHA256 という lookup semantics は維持する。
- installed directory index の service 入口は `IEnumerable<ChartFile>` に統一する。旧実装では `BuildInstalledHashToDirectoryMap(IEnumerable<BMSFile>, IEnumerable<bmson_song>)` のように BMS が主入力で bmson が補助入力だったが、directory index は chart identity と path directory だけを見る chart-common index なので、BMS / bmson storage owner の分岐は `BMSLibrary` 側の installed chart snapshot 作成に閉じる。未使用だった `TryGetInstalledDirectoryByHash(IEnumerable<BMSFile>, ...)`、per-chart lookup の `BMSFile` overload、BMS-only rebuild helper、md5 / sha256 union lookup helper は、BMSFile list 入口や chart-common ではない hash lookup semantics を復活させる旧 API だったため移植しない。test は test-local helper で BMS / bmson storage row を `ChartFile` に投影してから service を呼ぶ。単一 chart / package resolve は `ChartFile.PrimaryLookupHash` の md5 優先・sha256 fallback を正本にし、複数 hash を union して候補 directory を広げる旧 helper の挙動は通常 flow へ戻さない。
- chart path / kind 判定の共通 helper は `ChartFileKindResolver` へ移す。production の `ChartFileKindResolver` は path extension と BMS storage row 判定だけを持ち、`BMSFile` subtype から bmson adapter を認識する API は削除する。旧 `PendingChartEntry.IsBmsonFilePath(...)` / `IsSupportedChartFilePath(...)` / `IsBmsonChartFile(...)` / `IsBmsChartFile(...)` は pending adapter 固有の責務ではないため production surface へ残さない。
- package 表示 row / virtual source snapshot / 再生停止 target snapshot は `PackageChartEntry.Chart` を正本にし、BMS row は `ChartFile.GetBmsStorageOwner()`、bmson row は `ChartFile.GetBmsonStorageOwner()` を読む。旧実装は `CompatibilityAdapter` だけを見ていたため、`PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(...))` のような adapterless BMS entry は停止 target から漏れ得た。これは「既存 BMS storage owner を持つ package entry は BMS playback target 判定に参加する」という chart-domain 上の期待に反するため、旧挙動は移植せず chart snapshot として含める。最終的な停止判定は `ChartFile.Path` と現在再生中 BMS の directory だけを見る。
- duplicate analysis は bmson duplicate row の `PendingChartEntry` adapter を作らない。旧実装では duplicate 判定後に bmson adapter を materialize し、BMS と同じ `DuplicateFiles` set に入れて warning を付与していたが、duplicate tree 表示の正本は `DuplicateGroup.ChartFiles` であり、bmson storage row に warning collection もないため、この副作用は移植しない。`DuplicateAnalysisResult` の mutation target も `DuplicateCharts` として chart を保持し、BMS storage row にはそこから `GetBmsStorageOwner()` で降りた場合だけ従来どおり `DuplicateChart` warning を付与する。既存 duplicate warning の clear 入口も `ChartFile` list を受け、BMS storage owner だけを clear する。bmson には duplicate group 内の `ChartFile` projection へ同 warning を重ねる。ただし duplicate group は connected directory 内の全 chart を表示するため、warning を重ねる対象は重複 hash group に属する row だけに限定する。旧実装でも warning 付与対象は duplicate hash group の `DuplicateFiles` だけだったため、同一フォルダ内の unrelated sibling chart を duplicate warning 対象にしない。
- duplicate snapshot service の入口は `ChartFile` list である。旧実装では `BuildSnapshot(BMSFiles, BmsonSongs)` が service 内で storage row を結合していたが、duplicate 判定は chart identity / path / primary lookup hash の処理なので、BMSLibrary 側で installed chart snapshot を作って渡す。BMS storage row へ duplicate warning を書き戻す必要がある時だけ `DuplicateAnalysisResult.DuplicateCharts` から `GetBmsStorageOwner()` で降りる。bmson duplicate warning は従来どおり `DuplicateGroup.ChartFiles` の projection にだけ載せる。
- UI row / operation target は owned bmson の `CompatibilityBmsFile` surface を持たない。旧実装では shared `PendingChartEntry` adapter が repair install destination や warning state の owner になり、row 再生成後も同じ adapter を参照していた。現行実装では ViewModel の chart-common transient state registry が `ChartFileTransientState` を保持し、`LibraryChartRow` / `PlaylistDetailSourceRow` / `ChartListSourceRow` へ `ChartFile` projection として重ねる。これにより、表示 state の保持は維持しつつ、operation target が bmson を BMSFile adapter に戻す副作用は移植しない。BMS の loose chart install destination state も同じ registry を通すため、BMS storage row を通常表示 state の正本にしない。
- 旧 adapter-backed bmson から `bmson_song` storage owner を取り出す互換処理は production から削除済みである。`LibraryChartRow`、`LibraryChartRef`、resource maintenance orchestration、state applier は `ChartFile` / `bmson_song` / `PackageChartEntry` を直接受け取り、BMSFile subtype の owner 抽出を行わない。旧実装では各所で `PendingChartEntry.BmsonSong` を直接読んでいたが、bmson adapter は最終形の storage owner ではないため、この副作用は移植しない。
- `PackageChartEntry` は BMS storage owner を別 field として保持しない。BMS path / BMS parser result は `ChartFileProjection.FromBmsFile(...)` で `ChartFile.GetBmsStorageOwner()` に接続し、bmson path は `ChartFileProjection.FromBmsonSong(...)` で `ChartFile.GetBmsonStorageOwner()` に接続する。旧実装や一部 test helper では adapter-backed bmson entry が `compatibilityAdapter` field に残り得たが、bmson adapter は storage owner ではないため、この保持副作用は移植しない。
- test helper の `GetBmsOwnerForTest()` / `GetBmsOwnersForTest()` は production field を reflection で読む helper ではなく、`PackageChartEntry.Chart.GetBmsStorageOwner()` を読む BMS storage owner helper である。旧 helper 名の `GetCompatibilityAdapterForTest()` / `MaterializeChartAdaptersForTest()` は削除済みである。adapter materialization を行う production surface は残さない。historical wording が XML doc / test name / local variable に残る場合も、behavior が bmson adapter 非生成を明示する場合を除き、後続 cleanup で BMS owner / chart entry 表現へ寄せる。
- production に残る `Compatibility` 名は playlist summary column settings の設定互換、LR2 compatibility warning category、旧 standalone song path 正規化のような永続 / 外部仕様互換だけである。譜面 row / package / resource health の adapter surface 由来の `Compatibility*` 名は削除または BMS owner 名へ置き換える。未使用だった `ChartFileTransientState.FromCompatibilityFile(...)` は削除し、resource health key の storage owner factory も projection lookup surface からは削除済みである。
- package discovery の BMS path は `BMSFile.CreateBMSFileFromFile(...)` で BMS storage owner を作る。旧実装では BMS path でも `PendingChartEntry.CreateFromFilePath(...)` を通し、BMS 用 pending adapter として package entry に入れていたが、BMS parser 結果は既に `BMSFile` である。package entry が必要とする install destination runtime state は BMS / bmson とも `PackageChartEntry` projection state に保持し、BMS pending discovery 用の `PendingChartEntry` wrapper は作らない。bmson path は従来どおり `bmson_song` / `ChartFile` entry として保持する。

### 最終完了判定

以下は最終的に満たす判定条件である。2026-05-22 現在、`BMSFile` member surface には warning storage / maintenance snapshot ownership など chart-common concept と接続する state が残るため、1 つ目の条件はまだ未達である。chart_info attachment、install destination、warning 表示 alias、maintenance health / encoding 表示 alias は chart-common runtime / read model 側へ分離済みである。

- `BMSFile` は実体 BMS / LR2 `song` row / BMS-only operation に閉じている。
- bmson を扱う通常表示、playlist detail、package / pending、duplicate、install destination、resource health の production 経路が `BMSFile` adapter 生成を要求しない。
- `PendingChartEntry` が production / test fixture のどちらにも存在しない。
- `ChartPackage.ChartEntries` / `PackageChartEntry.Chart` が package 内 chart の正本であり、package-level adapter list API が復活していない。
- `CompatibilityBmsFile` / `CompatibilityAdapter` は production surface に残っていない。`GetOrCreateCompatibilityAdapter()` / `GetOrCreateBmsFormatAdapter()` / `GetExistingBmsFormatAdapter()` は production surface から削除済みであり、package 内 BMS storage owner は `PackageChartEntry.Chart.GetBmsStorageOwner()` で読む。
- playlist reference 表示 / sort / keyword search は `PlaylistReferenceIndex` / chart identity / row projection を正本にし、BMS storage row への `RefTables` writeback と `BMSFile.RefTables*` legacy API surface は production / tests に残っていない。仕様書内の `BMSFile.RefTables*` 記述は旧実装との差分説明と完了済み checklist に限る。
- storage は BMS `BMSFiles` / bmson `BmsonSongs` の二本立てを維持し、playlist / LR2 DB / settings の永続互換を壊していない。

## 今後の仕様整理で守る境界

1. Storage は BMS / bmson の二本立てを維持する。
2. UI / operation の入口は chart target に寄せる。
3. BMS-only 処理は capability で明示する。
4. `BMSFile` 型を chart 共通処理の種別判定に使わない。production では `BMSFile` を BMS storage row / BMS-only 処理に閉じ込め、chart 共通処理は `ChartFile.Kind` を確認する。
5. package 内 chart の読み取りは `ChartEntries` を入口にする。BMS storage owner access は対象 entry 単位に限定し、package-level adapter list API を再導入しない。
6. settings 名と UI 文言の BMS は互換契約として残す。内部 helper / log / operation symbol は必要に応じて chart 名へ寄せる。
7. `ChartFile` / `ChartPackage` を使う場合も、既存の LR2 互換 DB と playlist JSON / DB の永続形式は維持する。
