# Chart Info Lifecycle

この資料は、所持譜面の `chart_info` currentness、起動時 hydration、parse failure、session index、backfill の現行契約の正本である。parser の入力互換性は [chart-info-parser-compatibility-notes.md](chart-info-parser-compatibility-notes.md)、file read の orchestration は [chart-file-read-pipeline.md](chart-file-read-pipeline.md) を参照する。

## Identity

- BMS は `song.hash` の MD5 と `chart_digest_map` の SHA-256 を結び、`chart_info.sha256` を参照する。
- BMSON は `bmson_song.sha256` を第一 identity とし、`chart_digest_map` を作らない。
- `chart_info` は metadata cache でもあるため、現在の所持 catalog に owner がない row も hydration や backfill だけを理由に削除しない。
- storage owner は BMS の `song` row または BMSON の `bmson_song` rowであり、runtime `ChartInfo` object は attach しない。表示は session index と projection provider から解決する。

## Currentness

単一 owner の分類優先順位は次のとおりである。

1. identity が一致し、current parser version の `chart_info`
2. current parser version かつ現在の timeout 条件を満たす `chart_info_parse_failure`
3. parse candidate

current `chart_info` と current parse failure が併存する場合は `chart_info` を採用する。stale parser version の row、または現在の parse timeout より短い timeout で記録された failure は current としない。

この分類と parse result mapping の正本は `ChartInfoBuildService.EvaluateSnapshot(...)` である。caller は、既に読んだ単一 `ChartFileSnapshot`、owner identity、事前に取得した current row、current failure の有無を渡す。evaluator 自身は file read、DB query、DB writeを行わず、次のいずれかを返す。

- current row を再利用する durable storage application（runtime `ChartInfo` attach ではない）
- current failure により parse を省略する結果
- snapshot bytes を current parser で解析した成功または failure 結果
- snapshot が得られず解析できない unavailable 結果

inline file diff / package install、full backfill、LR2 `song_rows` は同じ evaluator を使う。各経路が異なるのは、snapshot と currentness facts の準備、結果の staging、transaction ownership だけであり、parser、優先順位、failure message normalization を分岐させない。

### 既存pathからのinline解析のfailure取得

package導入後など、pathからinline解析する場合はbatch内で正常に読めたsnapshotのMD5だけを対象にfailureを取得する。discovery時のownerが保持するMD5は、実read時の内容と異なることがあるため検索の正本にしない。対象MD5が空ならDBを開かずfailure queryも実行しない。

failure取得はMD5のparameter化queryをchunkに分け、無関係なfailure行を全表materializeしてからfilterしない。parser/timeoutのcurrent条件とinfo優先順位は共通evaluatorの契約を維持し、session cacheは追加しない。全ownerを照合するhydration等の全件取得は別経路として残す。current infoの再利用でも、新ownerへ必要なstorage application、commit後digest/session反映と通知を省略しない。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 対象failure query、空入力、current判定 | [[BmsLibraryDbGateway.cs](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs)](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の `LoadCurrentChartInfoParseFailuresByMd5` | [[ChartInfoInlineHydrationTests.cs](../../BeMusicSeeker.Tests/ChartInfoInlineHydrationTests.cs)](../../BeMusicSeeker.Tests/ChartInfoInlineHydrationTests.cs) の同名queryの3case。実SQLiteの返却行・走査量、空入力の無query、旧parser/短timeout除外 |
| discovery後に内容が変化した導入 | [[ChartInfoInlineBuildService.cs](../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoInlineBuildService.cs)](../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoInlineBuildService.cs) の `BuildForExistingCharts` | [[ChartInfoInstallFailureRetryTests.cs](../../BeMusicSeeker.Tests/ChartInfoInstallFailureRetryTests.cs)](../../BeMusicSeeker.Tests/ChartInfoInstallFailureRetryTests.cs) の `InstallChartPackages_UsesInstalledSnapshotMd5ForFailureLookup`。実fileをA→Bへ変更し、Bのfailure再利用とAだけのfailureではBを解析すること |
| current infoの新owner反映と既存公開境界 | 同serviceの `BuildForSnapshots` と `CatalogChartInfoOwner` / [CatalogMutationOwner](../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogMutationOwner.cs) | [ChartInfoInlineHydrationTests.ChartInfoInlineBuildService_AppliesExistingCurrentRowWithoutParsing](../../BeMusicSeeker.Tests/ChartInfoInlineHydrationTests.cs)、`CatalogChartInfoOwner_InlinePublicationOrdersDigestEffectsAfterSessionIndex`、既存backfill storage/failure coverage |

## Durable Storage And Publication

inline / full backfill は、storage mutation と chart-info facts を immutable な `CatalogChartInfoStorageWriteRequest` にまとめ、`CatalogMutationOwner.ApplyChartInfoStorageWrite(...)` から一つの catalog transaction へ保存する。新規・更新譜面のinline経路は基本解析結果とchart-info projectionを組み合わせたBMS/BMSON storage rowを保存できる。一方、既所持譜面を対象とするfull backfillはsong行全体を再構築せず、既存BMS `song` rowへ`Lr2SongDbWriter.UpdateChartInfoSongProjections(...)`でupdate-only projectionを適用する。

full backfillが新規譜面生成と共有するのはchart-info由来projectionの値と正規化規則だけである。対象列は`level`、`difficulty`、`maxbpm`、`minbpm`、`bga`、`exlevel`、`longnote`、`random`、`karinotes`の9列とし、`mode`、`judge`を含む基本列と`favorite`、`tag`、`adddate`などuser管理列は既存DB値を尊重する。pathと正規化MD5が一致する既存rowだけを更新し、rowが存在しない場合やidentityが一致しない場合は診断件数・bounded sampleを残してfactsのcommitを継続し、`song` rowを暗黙INSERTしない。

full backfill は parse success だけでなく、missing digest candidate が existing current row を再利用した場合も storage application を作る。BMS の duplicate MD5 target は一度の read/evaluation結果を同じtargetに属する全BMS storage ownerへ投影する。BMSONはSHA-256-first identityを維持し、`chart_info` / session indexは更新するが、`chart_digest_map` とLR2 `song` rowは作らない。

publication順は `transaction commit / durable receipt -> canonical storage ownerのdigest・DBでmatchedしたchart-info由来列 -> digest-derived index -> chart-info session index -> warning/digest event` とする。commit前またはcommit失敗時に canonical owner、digest index、session index、eventを部分更新しない。storage ownerへruntime `ChartInfo` objectはattachせず、表示はsession indexとprojection providerから解決する。

## Startup Hydration

LR2 linked と standalone は同じ actual-data hydration を使う。read-only loader が実在する current `chart_info` と current parse failure を取得し、owned chart summary と照合して session index と candidate count を作る。この read phase は schema ensure や hidden writeを行わず、install readiness を同期 block しない。

`lr2_song_db_sync_status` は LR2 generated `song` / `folder` data sync の開始、再開、完了判定だけに使う。`Completed` status や file-diff の無変更状態は `chart_info` の行の現存または完全性を証明しない。したがって、両 profile とも current info と current failure がない owned chart は同じ backfill candidate になる。

## Session All-current Snapshot

actual-data hydration が全 owner を current info または current failure と分類した場合、owner collection、BMS/BMSON storage row、parser version、timeout versionを含む `ChartInfoHydrationAllCurrentSnapshot` を記録する。同じ session でこれらが変わらない間だけ、後続の candidate summary と full backfill を `hydration_all_current` として省略できる。

この snapshot は実データ照合後の mode-neutral optimization であり、LR2 sync status から合成しない。owner/storage/parser/timeout の変更時は無効化する。parse failure の明示削除はfailure rowとwarning表示だけを更新し、同一sessionのsnapshotを無効化せず、即時のhydration/backfillもqueueしない。current `chart_info` がない譜面は、次回アプリ起動のactual-data hydrationでcandidateへ戻る。同一起動中に進行中または後続のhydrationがどちらの状態を観測するかは保証しない。

## Failure Contract

hydration DB read が失敗した場合は all-current を合成せず、失敗をログへ残す。current `chart_info` がなければ、current failure の削除後は次回アプリ起動のactual-data判定でcandidateへ戻る。current `chart_info` がある場合は、failure を削除しても info 優先順位により再解析しない。

parse timeout、parser exception、最終 parse failure は evaluator が同じ result mapping と bounded message normalization を適用する。digest を計算できた failure は `chart_info_parse_failure` の永続化候補を返し、成功時は同じ MD5 の failure を削除する候補を返す。BMSはstorage write時に`chart_digest_map`を更新するが、BMSONはfailure時もdigest mapを作らない。DB write と削除の transaction は各 orchestration owner が管理する。

## Related Specifications

- [startup-initialization-flow.md](startup-initialization-flow.md)
- [data-and-indexes.md](data-and-indexes.md)
- [lr2-song-db-generation.md](lr2-song-db-generation.md)
- [chart-file-read-pipeline.md](chart-file-read-pipeline.md)
- [chart-info-parser-compatibility-notes.md](chart-info-parser-compatibility-notes.md)

## Verification map

Chart-info metadata coverage is organized in five owner-local source files, each with a distinct `TestClass` fixture discovered once by the existing `remaining` ClassLevel route. The async completion signal observes `BMSLibrary.PropertyChanged` and rechecks the requested/completed version and running predicates, so hydration, backfill, and install cases do not synchronously block a process ThreadPool worker. This preserves the original GUID-owned song database / filesystem roots, parser compatibility categories, dispatcher and task/event completion signals, failure watchdogs, and cleanup behavior; it adds no process, DNP, fixed wait, timeout change, or production seam.

| Behavior / failure contract | Owner fixture | Retired cases | Route |
| --- | --- | --- | --- |
| schema creation, bundle export/import, startup importer, catalog mutation | `ChartInfoMetadataSchemaExportImportTests` | `ChartInfoMetadataTests` cases 1-19 | `remaining`, ClassLevel discovery |
| BMS/BMSON parser behavior and compatibility fixtures | `ChartInfoParserBehaviorTests` | cases 23-81 | same route |
| full backfill, storage projection, digest/index publication and transaction failure | `ChartInfoBackfillStorageTests` | cases 82-96 | same route |
| read-only lookup, deferred hydration, inline evaluator and hydration candidate state | [ChartInfoInlineHydrationTests](../../BeMusicSeeker.Tests/ChartInfoInlineHydrationTests.cs) | cases 20-22, 97-111 | same route |
| install, parse-failure warning/removal, retry and contention contracts | [ChartInfoInstallFailureRetryTests](../../BeMusicSeeker.Tests/ChartInfoInstallFailureRetryTests.cs) | cases 112-134 | same route |

The old `ChartInfoMetadataTests` selector and retired partial `ChartInfoMetadataOwnerTests` selector are absent from the launch plan and remaining exclusion ledger. The five distinct fixtures are not named selectors; `remaining` discovers each exactly once. The replacement owns every original behavior case exactly, including `DataRow` cases.
