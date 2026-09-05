# Duplicate File Check

この資料は、重複ファイルチェック画面と重複フォルダ整理の現行仕様をまとめる。ユーザー向けの操作説明は `docs/manual.ja.md` にあり、この資料は実装・ログ・性能方針の正本として扱う。

## Scope

重複ファイルチェックは、所持譜面 catalog 内の BMS / bmson chart から、同一譜面と判断できる chart を検出し、重複候補の確認、削除、フォルダマージ、同一フォルダ内 hash cleanup を行う画面である。

対象は owned current installed source の chart である。pending package、install destination overlay、playlist metadata、projection-only chart は重複ファイルチェックの owned duplicate 判定には混ぜない。

## Identity

owned duplicate 判定の identity は BMS / bmson 共通で MD5 primary hash のみである。

- BMS は `BMSFile.hash` を使う。
- bmson は `bmson_song.md5` を使う。
- SHA-256 は chart_info / playlist sha-only entry / external score などの別ドメインで有効だが、重複ファイルチェックの fallback identity にはしない。
- pathless / md5less storage row は owned collection 入口で current installed source から除外されるため、duplicate search にも warning clear にも入らない。

同じ MD5 を持つ chart が複数 path にある場合だけ重複候補になる。owned chart の canonical path は app-wide unique であり、同一 path 複数 row は正常系として扱わない。

## Data Model

`DuplicateChartGroups` は現在の検索結果 cache である。cache miss 時に `BMSLibrary.SearchDuplicateChartGroups()` が full search を行い、`DuplicateGroup` の list として置き換える。cache hit 時は再検索しない。

`OwnedDuplicateChartRowSnapshot` は owned collection の duplicate row adjacent snapshot である。BMS / bmson storage owner identity、path、directory path、MD5 lookup hash、duplicate MD5 bucket index を保持する。`DuplicateChartRow.CreateChart()` は storage owner から必要時だけ fresh `ChartFile` projection を作る。snapshot row 自体に materialized chart を保存しない。

`BmsLibraryDuplicateService` は snapshot builder ではなく、duplicate analyze と BMS duplicate warning apply / clear を担当する。

## Grouping

duplicate analyze は、同一 MD5 を持つ row を duplicate row として検出する。duplicate row が存在する directory を Union-Find で接続し、接続成分ごとに `DuplicateGroup` を作る。

例:

- directory A と B に hash X がある。
- directory B と C に hash Y がある。
- A / B / C は 1 つの `DuplicateGroup` になる。

group の chart list には、duplicate hash を持つ chart だけでなく、接続された directory 内の sibling chart も含める。これにより、フォルダ単位で整理するときに同梱 chart を同じ一覧で確認できる。duplicate warning は duplicate hash を持つ chart にだけ付与する。

`DuplicateGroup.Header` は group の先頭 chart title である。`DuplicateGroup.Folders` は tree の child node として表示する directory path の list である。group list は header 昇順で表示する。

## Warning

重複 warning は `ChartWarningKind.DuplicateChart` である。

- BMS は `BMSFile` の warning collection に反映するため、通常ライブラリでも warning として表示できる。
- bmson storage row は warning collection を持たないため、duplicate view の `DuplicateGroup.ChartFiles` に warning 付き `ChartFile` projection を置く。
- duplicate warning clear は前回実際に duplicate warning を付けた BMS owner set に対して行う。BMS storage row full replacement / file scan replacement 後だけ、未追跡 warning を消すため次回 search で full clear を行う。

## Tree And List Views

XAML の duplicate root は `treeViewItemSearchDuplicated` であり、`ItemsSource` は `DuplicateChartGroups` である。

選択と一覧 source は次の通り。

| Tree selection | Parameter | List subset |
| --- | --- | --- |
| duplicate root | `null` | 全 duplicate group の chart |
| group node | group header / `DuplicateGroup` | group の chart |
| folder node | folder path | その folder path 配下の chart |

list view は `DuplicateFilterSelected` の virtual subset として `ChartListSourceRow` / `ChartListVirtualView` 経路を使う。表示列は `Settings.Default.DuplicateCustomTableColumnSettings`、既定列は `custom-table-view.md` の `DUPLICATE` 定義を使う。

## Operations

### Open Explorer

folder node の context menu から対象 directory を Explorer で開く。directory が存在しない場合は何もしない。

### Merge Folder

folder node の context menu `マージ先` は、同じ `DuplicateGroup.Folders` から選択中 source folder を除いた destination folder list を表示する。source folder と destination folder を選ぶと、`Settings.Default.ShowDuplicateFileCheckConfirmMsg` が ON の場合は確認 dialog で OK した後、OFF の場合は確認 dialog を省略して、background task で `MainWindowViewModel.MergeChartDirectory()` を呼ぶ。

merge flow:

1. 再生中 chart を停止する。
2. UI update suppression を開始する。
3. duplicate refresh priority window を開始し、playlist resolve prewarm を duplicate refresh 後へ defer できるようにする。
4. model snapshot lock の内側で source chart、hash、catalog delta、immutable detached package を準備し、lock を解放する。
5. source を保持したまま destination-local sibling staging と overwrite backup を作り、preflight を完了する。
6. staged files を destination へ promote し、catalog delta を 1 つの DB durable transaction で commit する。durable receipt より前の失敗は単一 owner が filesystem compensation を一度だけ試す。
7. durable receipt 後に source delete、空 source directory cleanup、reverse lookup、maintenance、notification を finalize / post-commit として行う。
8. UI suppression を解除し、terminal receipt に応じて recovery 案内または duplicate refresh 後の auto-select を行う。

merge の resource-health maintenance は `ResourceHealthIndexUpdateMode.DeferOnUpdates` を使う。canonical workflow route は `DuplicateMergeMaintenanceReceipt` として、merge 成否、file/DB mutation terminal state、manual recovery path、cleanup failure、maintenance の更新有無、intermediate defer、delta/full rebuild の dispatch facts を UI owner まで返す。merge 中は resource-health の delta 適用と full rebuild を行わず、次の canonical `GetResourceHealthIndexSnapshotForView()` read が current owned target snapshot を一度だけ full rebuildし、続く read は同じ snapshot reference/version を再利用する。公開 `BMSLibrary.MergeChartDirectory(string, string)` の `void` 入口は互換用に残すが、通常の duplicate UI/workflow は receipt-aware overload を使い、terminal result を破棄しない。

durable DB receipt 前の compensation が成功した場合は source と DB prior state を authoritative とし、失敗として終了する。compensation 自体に失敗した場合は `ManualRecoveryRequired(paths)` として batch を停止し、stage / backup / source の recovery path を保持して自動 retry や後続 cleanup を行わない。durable receipt 後の内部 finalizer が throw した場合は `DurableFinalizationFailed` とし、destination と DB を commit 済みの authoritative state として保持する。merge receipt の `MergeApplied` / workflow の `Succeeded` は false とし、通常の success report、selection、refresh、maintenance を行わず、後続 merge mutation を開始しない。finalization failure と source cleanup failure が併発した場合は両方の exception dimension と recovery path を保持する。source cleanup だけが失敗した場合は `CompletedWithCleanupFailure` とし、destination と DB は commit 済みのため DB rollback、filesystem compensation、fresh install retryを行わない。directory/file operation gate は持てるが、model/collection/queue lock を filesystem executor、DB transaction、cleanup、UI notification の待機をまたいで保持しない。

この境界は process crash / power loss を replay する persistent journal、あらゆる cross-volume filesystem の atomicity を保証するものではない。destination-local staging、source retention、one-shot compensation、typed terminal receipt を通常運用での安全境界とし、それでも回復不能な場合は recovery paths を失わず手動復旧へ移す。

source chart を除外した owned installed primary MD5 lookup に同一 MD5 が既にある場合、その source chart file は移動対象から外す。これは destination folder に限らず、library 内に同一 MD5 の current owned chart が残る場合も含む。chart ファイル名だけが衝突する場合は別名へずらす。component resource の衝突は smart overwrite 設定に従う。

source folder は、空になった場合、または残っている file がすべて supported chart かつ既所持/current package hash と判断できる場合だけ削除する。非譜面、parse できない譜面、hash 不明、未所持 hash の chart が残る場合は削除しない。

### Ctrl+G

folder node で `Ctrl+G` を押すと、重複整理 shortcut として動作する。

| Folder count in group | Action |
| ---: | --- |
| 1 | 同一フォルダ内 hash cleanup |
| 2 | 選択 folder をもう一方の folder へ merge |
| 3 以上 | context menu を開き、`マージ先` submenu を展開する |

同一フォルダ内 hash cleanup は、folder 内 chart を MD5 ごとに group 化し、各 hash group で 1 件だけ残す。残す chart は更新日時が古いものを優先し、同日時なら file name が短いものを優先する。削除対象は `Settings.Default.ShowDuplicateFileCheckConfirmMsg` が ON の場合は確認 dialog 後に、OFF の場合は確認 dialog を省略して、`RemoveLibraryCharts()` でごみ箱へ移動する。

### Delete From List

duplicate list 上の chart は library section の chart として扱う。通常の一覧 context menu から削除した場合、`RemoveLibraryCharts()` に入り、library mutation は duplicate cache を dirty にする。

## Refresh And Coalescing

`DuplicateChartGroups` は result property であり、dirty signal は `DuplicateChartGroupsInvalidationVersion` として分ける。library mutation で duplicate cache が invalidated された場合、cache が non-null なら null 化し、version を進める。

ViewModel の `EnsureDuplicateChartGroupsReady()` は duplicate refresh を single-flight / coalesce する。同時に複数経路から refresh が要求された場合、先行 refresh が `searched`、後続は `joined` として同じ結果を使う。ログは `duplicate_refresh_coalesce action=searched|joined reason=... version=... elapsedMs=...`。

merge 中は `BeginDuplicateRefreshPriorityWindow("merge_folder")` を使う。duplicate view 表示中かつ `owned_collection_changed` 由来の playlist library index prewarm は、duplicate refresh priority window の間は `deferred_for_duplicate_refresh` となり、UI refresh 後に `released_after_duplicate_refresh` で再 schedule する。

UI update suppression は `LibraryMainView` / `LibraryFolderTree` / `InstallTree` / `DuplicateTree` をまとめて保留する。suppression 中に pending になった channel は解除時に flush する。

## Auto Select

merge /同一フォルダ hash cleanup 後は、次に見るべき duplicate group を自動選択する。

- 2-folder merge: group が消える想定なので、次の group header を保存する。
- 3-folder 以上の merge: group が残る想定なので、同じ group header を保存する。
- 1-folder hash cleanup: group が消える想定なので、次の group header を保存する。
- 次 group がない場合は auto-select しない。

`WaitForDuplicateListUpdateAndSelect()` は `DuplicateChartGroups` property changed、already-ready、timeout fallback を併用する。tree virtualization のため、`Loaded` / `Render` / `ContextIdle` priority で retry し、必要なら virtualized item を realize してから選択する。ログは `duplicate_group_autoselect ...`。

auto-select の保存 key は現状 `DuplicateGroup.Header` であり、同じ header の group が複数ある場合は最初に一致した group を選ぶ best-effort 動作になる。header collision を完全に区別する stable group key は未導入である。

## Startup

Startup 中の `DuplicateTree` と duplicate main view は、startup presentation が整うまで遅延される。duplicate analyze は startup critical path ではなく explicit on-demand operation として扱う。

## Performance

merge 後 refresh では、`OwnedDuplicateChartRowSnapshot` が duplicate MD5 bucket index を持つため、`BmsLibraryDuplicateService.Analyze()` は全 row の hash dictionary build を行わない。

- `DuplicateChartGroups` は引き続き full replacement とし、group result の incremental update には踏み込まない。
- owned adjacent snapshot は first occurrence order の duplicate MD5 bucket を持つ。
- `BmsLibraryDuplicateService.Analyze()` は full hash dictionary build を行わず、snapshot の duplicate buckets から Union-Find を開始する。
- directory sibling inclusion のため、directory -> row lookup は必要だが、duplicate-connected directory だけを対象にする。
- snapshot が remove / path change / upsert / MD5 digest update で差し替わる場合、duplicate bucket index も同じ snapshot replacement に同期する。

`SearchDuplicateChartGroups` log は `duplicateHashCount` / `duplicateHashRowCount` / `connectedDirCount` を出し、全 row 数に対する duplicate candidate の規模を確認できるようにする。

## Logs

主要ログ:

| Log | Meaning |
| --- | --- |
| `SearchDuplicateChartGroups: cacheHit=false ...` | duplicate full search の stage breakdown |
| `duplicate_refresh_coalesce action=searched|joined` | duplicate refresh single-flight/coalesce |
| `duplicate_merge_*` | merge UI / VM / model stage |
| `library_mutation_delta_apply context=duplicate_merge_unregister` | source unregister mutation breakdown |
| `owned_collection_mutation_dispatch reason=library_delta` | owned adjacent index / presentation dispatch breakdown |
| `ui_suppress begin/pending/end/flush` | UI refresh suppression |
| `duplicate_refresh_priority begin/end` | duplicate refresh priority window |
| `playlist_library_index_prewarm deferred_for_duplicate_refresh` | duplicate refresh 優先による playlist prewarm defer |
| `duplicate_group_autoselect ...` | merge 後 group auto-select |

## Verification map

folder merge の terminal reporting は [library-mutation-boundary.md](library-mutation-boundary.md#folder-terminal-reporting) を正本とする。`DuplicateMaintenanceWorkflowOwner.RunFolderMergeAsync` が receipt-backed 個別通知を引き取り、gate・activity・priority・dialog scope の終了後に一度だけ集約表示する。cleanup-only の成功、durable finalization failure と non-durable failure の結果を保持し、任意通知 failure は log-only とする。receipt のない互換経路は従来の終了挙動を保持する。

`FSDB-A-20260905` A01–A06 は `DuplicateMaintenanceWorkflowOwnerTests` を extend して検証する。terminal observer cleanup を一般失敗にする旧 case は、成功 receipt を保持する optional log-only case に置換する。local gate／ports と returned Task を使い、既存 remaining lane で実行する。severity・表示上限・全言語 format は `FileDbMutationReportTests` と `LocalizationResourceParityTests` を共有する。

## References

- User manual: `docs/manual.ja.md`
- Columns: `devdocs/spec/custom-table-view.md`
- Warning model: `devdocs/spec/warning-model.md`
- Chart abstraction and owned collection: `devdocs/spec/bms-bmson-chart-abstraction-current-state.md`
- Main classes:
  - `BMSLibrary.SearchDuplicateChartGroups()`
  - `BmsLibraryDuplicateService.Analyze()`
  - `OwnedChartCollectionState.CreateDuplicateChartRowSnapshot()`
  - `MainWindowViewModel.EnsureDuplicateChartGroupsReady()`
  - `MainWindow.ExecuteDuplicateFolderMerge()`
