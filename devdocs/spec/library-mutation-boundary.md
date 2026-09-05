# Library Mutation Boundary

本資料は、譜面行、保留パッケージ、導入済みパッケージに対する破壊的操作の UI / model 境界を定義する。長パス I/O の正本は [path-length-and-io.md](path-length-and-io.md) だが、ユーザー操作としての実行順、二重実行防止、dialog 表示タイミングは本資料を正本にする。

## 基本方針

- 譜面 / パッケージの変更系操作は `MainWindowViewModel.RunChartPackageMutation(...)` を入口にする。
- 境界の内側では、`lockCopyFile`、再生停止、UI refresh suppression、操作中フラグ、operation dialog scope、操作後 refresh flush を一元管理する。
- mutation 中は譜面行 / 保留行 / package 行の context menu open と command 起動を拒否する。context menu の enable 判定で UI thread から file existence check を走らせない。
- `BMSLibrary` の writer lock 中に `Dispatcher.Invoke`、message box、UI event callback、`Task.Wait` / `.Result` のような同期待ちは行わない。
- 失敗を隠す fallback は追加しない。必要な確認が取れない場合は処理を進めず、境界違反は明示的な失敗にする。

## ViewModel 境界

`RunChartPackageMutation(...)` は、次の責務だけを持つ。

- 同時実行中かどうかを `IsChartPackageMutationInProgress` で公開する。
- 操作中は LR2 song DB 同期や譜面 / package 操作の再入をブロックできる状態にする。
- 必要なら対象譜面の再生を停止する。
- `BeginUiUpdateSuppression(...)` / `EndUiUpdateSuppression(...)` で refresh をまとめ、操作後に pending install tree、library main view、folder tree、duplicate tree などの必要 channel を flush する。
- `BMSLibrary.BeginOperationDialogScope()` を開始し、model lock を抜けた後で蓄積された warning / error dialog を一度だけ表示する。

境界は、確認 dialog の意味を決めない。ユーザー確認が必要な操作は、mutation 実行前の preflight で ViewModel が dialog を表示し、結果を explicit decision として `BMSLibrary` へ渡す。

## Model 契約

`BMSLibrary` は、破壊的操作の結果として表示すべき OK dialog を operation report として蓄積できる。operation dialog scope が有効な場合、OK dialog は即時表示せず、scope の flush で表示する。

`YesNo` / `OKCancel` などの interactive prompt は、operation dialog scope 内では表示しない。必要な decision が渡されていない場合、`BMSLibrary` は暗黙の yes / no に倒さず、境界違反として明示失敗にする。これにより、model writer lock 中に UI thread を待つ経路を残さない。

scope 外から `BMSLibrary` を直接呼ぶ既存テストや内部ユーティリティでは、従来どおり dialog service を使う。ただし、アプリ本体の譜面 / パッケージ操作入口は ViewModel 境界を通す。

### Startup LR2 leap-year repair

起動時の LR2 leap-year 修復候補は、`BmsLibraryInitializationService.NormalizeSongTable` が既存の folder-table loop で一度だけ収集し、`SongTableLoadResult` に immutable な候補（catalog identity、正規化済み filesystem/display path、観測時刻）を返す。この loop は dialog、filesystem mutation、追加の folder-table enumeration を行わない。初期 lease と model lock を解放した後で候補ごとの確認を行い、承認された候補だけが第二の短い mutation lease に入る。

修復 lease 内では候補が保持する元の `folder.path` だけを `Lr2FolderExistingRowLookup.QueryExactPaths` へ渡し、対象 K 行を取得する。各行について catalog identity、directory existence、観測時刻との完全一致を再検証し、差し替え・消失・時刻不一致なら filesystem / DB mutation は 0 とする。確認済み候補の timestamp mutation と対応する catalog update が成功した場合だけ永続化し、mutation service が null の場合は明示的な failure とする。外部 DB 変更検出の全件 scan、無関係 path の mtime probe、全 row dictionary は追加しない。

## Chart-info Catalog Write

background hydration/backfillやpackage inlineのchart-info writeは、UIの`RunChartPackageMutation(...)`とは別のcatalog mutationである。transaction ownerは`CatalogMutationOwner`のままとし、inline/fullは同じ`ApplyChartInfoStorageWrite(CatalogChartInfoStorageWriteRequest)`を使う。

- requestはinline用BMS/BMSON persistence copy、full-backfill用immutable narrow projection、`CatalogChartInfoWriteRequest`をsnapshotとして束ねる。
- inline storage rowsまたはfull-backfill用song update、`chart_digest_map`、`chart_info`、parse-failure upsert/deleteは一つのcatalog transactionで保存する。
- full backfillは`Lr2SongDbWriter.UpdateChartInfoSongProjections(...)`でpath+MD5が一致する既存BMS `song` rowのchart-info由来9列だけをupdateする。full row upsertやmissing row insertは行わず、基本列、`mode`、`judge`、user列をUPDATE句へ含めない。BMSONからLR2 `song` rowは作らない。
- durable receipt後だけcanonical storage owner、digest/index、chart-info session index、warning/digest eventを更新する。commit失敗時はcanonical/index/eventを変更せず、対象は次回もcandidateとして残る。
- parse-failure明示削除などstorage rowを伴わない処理にはfacts-onlyの`ApplyChartInfoWrite(...)`を残す。
- DB transactionやmodel/storage lockを保持したままUI/event subscriberを待たない。

## 対象操作

P0 として次の操作は共通境界を通す。

- 譜面フォルダの移動、削除、マージ、フォルダ名変更、自動リネーム、拡張子修正。
- 導入済みパッケージ record 削除。
- 保留パッケージ / 保留譜面の導入先検索、マージ先検索、導入先設定、導入先解除。
- 保留パッケージ / 保留譜面の通常導入、手動導入、削除、source 削除、スマート上書き。
- 導入先修正で既存 duplicate record を消す可能性がある操作。

これらはファイル操作の意味上も P0 長パス対応範囲であり、実際の存在確認、コピー、移動、削除、タイムスタンプ更新は `LongPathFileSystem` / `IFileMutationService` を使う。

## Dialog と Report

操作前に確認が必要な例は次のとおりである。

- 通常導入済み扱いの譜面を force install で上書きするか。
- 導入先修正で、同じ譜面を指す既存 record を削除するか。
- 譜面削除で、譜面ファイルだけでなく package folder 全体を削除するか。

これらは preflight で確認し、承認済み path や boolean decision として execution option へ渡す。decision が不足した状態で model lock 内 prompt に戻る実装は不可とする。

操作後の warning / error は report として蓄積し、`lockCopyFile` と model lock を抜けた後に dialog service で表示する。重複 warning を避けるため、同じ mutation の中で同じ message を複数 queue しない。

### Pending chart legacy mutations

保留譜面の削除、無効拡張子の修正、導入行の差分更新は、確認時に対象 K 件だけの immutable projection を一度作る。projection は対象の file identity、package / entry membership、authorized path を保持し、無関係な pending package の chart entries を読み直さない。lease 内で live owner、package collection、entry、path、file existence、directory / reparse safety を再検証し、失敗した対象は明示的な non-success として扱う。確認後の process-exclusive owner と transaction が authoritative であり、外部 DB の全件再読込や全 catalog 比較は行わない。

保留譜面の child が stale / missing または削除失敗になった場合、失敗・未処理 sibling を保持し、package root / ancestor の recursive delete へ拡大しない。全 child が同一 projection により承認され、成功した後だけ、空になった authorized package directory を non-recursive に best-effort cleanup できる。無効拡張子修正は canonical durable apply / publication が成功するまで live path / global index を変更せず、dialog / notification は release 後の best-effort effect とする。nested pending/install-row apply は現在の live owner が発行した非 null・未 dispose capability を必須とし、nullable / foreign / disposed capability を no-op fallback に変換しない。

## Verification map

`BmsLibraryStateApplierTests` の26 caseは、owner境界と `remaining-bms-library` の `ClassLevel` 実行単位を一致させるため、次の3 fixtureへ分けて維持する。

- `BmsLibraryStateApplierTests`: library initialization progress と `ApplyLibraryMutationDelta(...)` の13 case。
- `BmsLibraryPackageLifecycleTests`: pending package collection publication と durable pending-package delta の7 case。
- `BmsLibraryCatalogRelocationTests`: catalog relocation の storage-row、path、LR2 compatibility の6 case。

3 fixtureは既存の `BmsLibraryStateApplierTestSupport` が提供する GUID付き temporary song DB、package state callback、UI scheduler、completion / cancellation signalを共有する。ただし各 testの resource rootとDBは従来どおり個別に所有し、Functional の `remaining-bms-library` process、`ProcessorCount` worker、`ClassLevel` scopeから新しい laneや `DoNotParallelize`を追加せずに実行する。分割は test semantics、永続化結果、failure contract、cleanupを変更しない。

`BmsLibraryInitializationServiceTests` の108 casesは、既存の GUID付き song DB / filesystem、dispatcher/task/event completion、failure watchdog、cleanupを保持したまま、library ownerごとの5 fixtureへ置換する。BmsLibrary prefixを持つこれらのfixtureと `BmsLibraryZeroNoteRefreshTests` は `remaining-bms-library` processの `ProcessorCount` / `ClassLevel` routeで実行し、chart-info / startup owner fixturesはselectorのnegative側である `remaining` routeに留める。新しい process、DNP、fixed wait、timeout変更、production seamは追加しない。

| Behavior / failure contract | Owner fixture | Retired cases | Route |
| --- | --- | --- | --- |
| catalog / maintenance load, BMSON load, leap-year validation | `BmsLibraryInitializationLoadTests` | `BmsLibraryInitializationServiceTests` cases 1-9, 76-77, 107 | `remaining-bms-library`, `ProcessorCount` / `ClassLevel` |
| install initialization, pending package restoration, resource warning projection | `BmsLibraryInitializationInstallTests` | cases 47-49, 98-106 | same route |
| file scan diff, catalog mutation, deletion, path/date/hash preservation, scan cache | `BmsLibraryInitializationFileScanTests` | cases 10-28, 67-75, 78-97 | same route |
| LR2 folder and normal-folder synchronization and affected-scope pruning | `BmsLibraryInitializationLr2NormalFolderTests` | cases 29-46 | same route |
| inline chart-info / maintenance batches, current-row reuse, parse failure and callback publication | `BmsLibraryInitializationInlineChartInfoTests` | cases 50-66 | same route |

The old `BmsLibraryInitializationServiceTests` selector is absent from the route and the 45-class exclusion ledger. All current `BmsLibrary*` owner fixtures are automatically routed by the logical prefix selector to `remaining-bms-library`; no exact class allowlist is maintained. `BmsLibraryStateApplierTests` remains a separate owner map within that route above.

## 関連仕様

- [architecture.md](architecture.md): UI / model concurrency boundary。
- [path-length-and-io.md](path-length-and-io.md): 長パス対応 I/O、`LongPathFileSystem`、`IFileMutationService`。
- [warning-model.md](warning-model.md): warning の表示仕様。

## File / DB durable boundary

アプリ全体の FS+DB の保証・非保証、前方回復、失敗の表示、レビューで受け入れる制限は [file-db-consistency.md](file-db-consistency.md) を正本とする。以下の `COMP-*` は `FileDbMutationExecutor` を使う既存経路の限定補償契約であり、削除等を含むすべての mutation に FS rollback を要求するものではない。この仕様整理だけでは、既存の補償や caller の挙動を変更しない。

package install、estimated install、smart overwrite、folder move、merge、自動リネームは、共通の file/DB mutation boundary を使う。各 command は immutable な preflight plan を完成させてから executor を呼び、executor は destination filesystem 内の sibling staging / backup を使う。source は DB の durable success まで削除しない。

executor の receipt は commit 前後を区別する terminal state を持つ。

- `COMP-PREFLIGHT`: plan 完成まで filesystem / DB mutation は 0。staging と backup は destination の sibling でなければならない。
- `COMP-PRECOMMIT`: durable receipt 前の failure は source を保持し、DB 側の rollback と結果判定は gateway の transaction 契約に従う。同じ plan 内の promoted destination の取り消しと backup 復元は、指定された一つの owner が一回限りの best-effort compensation として行い、その成功時だけ FS 側の補償完了として扱う。別 item の durable success は取り消さず、補償失敗は `COMP-MANUAL` に従う。FS+DB 全体の原子性は主張しない。
- `COMP-MANUAL`: compensation failure は `ManualRecoveryRequired` とし、処理を直ちに停止する。source / backup / staging と recovery paths を保持し、後続 cleanup、再帰補償、自動 replay を行わない。
- `COMP-DURABLE`: durable success 後は compensate しない。destination と DB を authoritative とし、receipt 前の cleanup は行わない。
- `COMP-DURABLE-FINALIZATION`: durable filesystem / DB receipt 後の内部 finalizer exception は `DurableFinalizationFailed` とする。`DurableCommit=true`、compensation=0 とし、destination と DB を authoritative に保持する。finalization exception は receipt に保持し、cleanup exception が併発した場合も両方の診断事実と recovery paths を保持する。batch はその item で停止し、後続 mutation と通常 success publication を行わない。
- `COMP-CLEANUP`: durable success 後の cleanup failure は `CompletedWithCleanupFailure` とし、leftover と recovery paths を保持する。fresh install / pending retry には戻さない。
- `COMP-SUCCESS`: destination と DB が authoritative で、必要な内部 apply と post-commit cleanup が完了する。post-lease notification は下記の best-effort 契約に従い、通知失敗で durable result を変更しない。

receipt と recovery paths は package / folder command の public result と UI workflow completion まで保持する。legacy の void / failure-list だけで terminal outcome を表現してはならない。

### Exclusive lease and deferred effects

ファイルを変更する command は、preflight confirmation の後に短い sequence admission を行い、その sequence monitor を解放してから `LibraryFileMutationLease` を取得する。拒否時も sequence monitor と既に取得した scope を直ちに解放し、拒否 dialog を monitor 内で待たない。lease は LR2 同期と他の file mutation に対して排他的であり、filesystem executor、durable DB apply、compensation / cleanup、内部 finalization が終わるまで保持する。

folder move、auto-rename、merge の snapshot は initialized-min read、pending-install write、BMS-files write の順で取得し、逆順で解放する。その他の route は必要な短い route-specific snapshot lock だけを取得する。いずれの場合も snapshot / model / package / collection lock は filesystem I/O、DB apply、compensation / cleanup、内部 finalization の前にゼロに戻す。

ネストされた DB / catalog / file apply は、現在の outer lease から明示的に発行された `LibraryFileMutationCapability` を引数として渡す。capability は所有者、lease の生存、dispose 状態を検証し、ambient `AsyncLocal`、thread、monitor reentrancy を認可には使用しない。通常の外部 entry は同一 thread からの再入でも拒否する。

`installable_maintenance` は自身の outer `LibraryFileMutationLease` を一度だけ取得し、mode detection と catalog maintenance をその lease 内の通常処理として capability-free に完了する。内側で lease を取り直さず、Unit A のこの route では `LibraryFileMutationCapability` を作成・伝播しない。capability を保持するのは、package の installed-target durable completion から LR2 normal-folder sync までを同じ outer lease でつなぐ実在の nested bridge だけであり、その bridge の under-existing-lease entry で owner / lease lifetime / dispose を一度だけ検証する。

LR2 custom-folder 出力を伴うローカル playlist 編集は、entry hydration と active-table の初期確認を終えてから、モデル変更前に同じ非ブロッキング lease を取得する。busy の場合は待機や内部 retry を行わず、モデル、playlist DB、LR2 folder row、生成ファイル、BMT queue を変更せずに明示失敗する。lease 取得後は active membership を対象 table だけ再確認し、短い table mutation、playlist DB apply、対象 custom-folder projection と LR2 row sync を同じ capability で完了する。terminal publication と BMT queue は lease 解放後に行う。`commitFlag=false` と LR2 mode 無効時はこの file-mutation lease を取得しない。

| user operation | owner | pre-admission 許可 | lease 内 model / file / DB | post-release notification / BMT / reference |
| --- | --- | --- | --- | --- |
| `playlistTableDrop -> AddRowsToFolderAsync`（ローカル playlist、LR2 custom-folder mode） | `PlaylistWorkspaceViewModel` が `BMSPlaylist` の drop owner route を呼ぶ | hydration と active-table 確認後に nonblocking lease を一回取得。busy は明示失敗し、全 durable / success effect を行わない | 同じ capability で table model、playlist DB、custom-folder file、LR2 folder/file row を完了。output failure は一次例外として保持する | lease 解放後に chart reference、UI invalidation、notification、BMT を各一回独立 attempt し、secondary failure は既存 diagnostic に記録して primary を置換せず、primary があれば元の例外を再送出する。primary なしの durable success は notification failure で失敗にしない。将来の JSON 副作用も UI caller ではなく owner の post-durable effect とする |

将来 JSON を追加する場合も、UI caller 個別の副作用にはせず、owner が durable completion 後の post-durable effect として一度だけ発行する。

`TryRunLr2SongDbSyncDataPreparation(...)` も admission を一回だけ試み、busy の場合は同じ呼び出し内で待機、lease 解放後の再開、内部 retry を行わず、false を terminal に返す。lease 解放後に再実行できるのは新しい明示 request だけである。LR2 preparation の playlist / builtin generated-data bridge は concrete runtime の nested entry に閉じ、request / DTO / coordinator / ordinary helper は capability-free semantic operation とする。

`FileDbMutationExecutor` は live outer session 内で durable DB apply を終えた後、session-local な one-shot `DurableFinalizer` をちょうど一度だけ実行する。receipt は callback-free の immutable な terminal fact であり、receipt 自身の callback、replay、retry を持たない。内部 finalizer が throw した場合は `DurableFinalizationFailed` として `Failed` / `ManualRecoveryRequired` と同じく成功 publication を付けず、command owner は canonical finalization 後に plain な one-shot publication action を command-owned の post-lease list へ記録する。lease と全 model lock を解放した後、その list を best-effort で実行し、subscriber / dialog / UI scheduler の失敗は durable / cleanup terminal state、compensation、retry、既存の primary failure を変更せず、後続 publication を中断しない。dialog、UI scheduler / Dispatcher、PropertyChanged / public subscriber、terminal progress / terminal publication、通常 refresh / index warmup、task start、別 owner callback はこの post-lease phase に遅延する。失敗・manual・durable-finalization-failure receipt の対象 item は成功 publication されない。中間 progress だけは feature-local の narrow writer へ immutable fact を nonblocking に送れるが、owner 側 consumer は latest-wins の pending / draining を各1以下に制限し、model / package / collection lock を保持せずに配信する。writer は terminalization 開始時に seal し、同一 generation の late progress を捨てる。中間 progress や診断通知の失敗は durable / cleanup terminal state、compensation、retry、既存の primary failure を変更しない。

### package install destination coherence

`BmsLibraryPackageInstallService.MovePackageFilesWithReceipt` は、filesystem mutation plan の preflight 中に source path ごとの actual destination を一度だけ確定する。collision resolution や directory-relative projection を完了した後で、basename、source root、または destination root から chart path を再計算してはならない。receipt の destination、detached DB / storage projection、live package entry、installed package registration、duplicate merge の catalog delta は、同じ preflight map の destination value を使う。

既存 destination file との collision では、選択済みの collision suffix path だけを新しい chart の destination とし、旧 file と旧 DB row は変更しない。single-file package に installable chart がない cleanup-only case では、chart destination は作らず package path は preflight で選択した installation directory を保持する。map は package-install owner の immutable fact として扱い、generic file/DB boundary、retry、rollback、persistent recovery state は追加しない。

LR2 preparation の中間 stage / table / batch progress は `BMSLibrary` の既存 facade dispatcher queue が latest-state として coalesce して配信し、lease 保持中に public `PropertyChanged` subscriber を同期実行しない。dispatcher drain 内の subscriber 例外はログ後に次の property を継続し、generated output、LR2 folder row、DB status の durable 結果や terminal failure を変更しない。


batch で compensation を所有するのは一つの owner だけであり、per-item owner や rollback-of-rollback は追加しない。crash replay、persistent journal、cross-volume atomicity、TOCTOU の解消はこの境界の主張に含めない。destination-exists の folder move は従来どおり reject とし、merge / overwrite は新設しない。
