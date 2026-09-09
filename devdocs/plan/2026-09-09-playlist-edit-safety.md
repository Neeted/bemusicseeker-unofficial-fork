# プレイリスト編集の安全性改善

Status: Completed

基準 commit: `7b6ae9bfe71d21936a0b737383a0a97787a6bc72`。開始時の worktree は clean。

## Goal / Context

ユーザー依頼の「プレイリスト編集」（BMS-004 / BMS-005 / BMS-006）について、実入口と影響を確認した問題を修正し、検証・独立 review・commit を完了する。commit は許可済み、push / version 更新 / release は対象外。

指摘の妥当性確認:

- BMS-004: MainWindow の rename / create folder / entry delete → Workspace.Mutations の Task.Run → collection reader 保持中の PublishEntriesChanged → MainWindow の同期 UI refresh。一方、手動 reload / deferred sync → ExternalSyncOwner → Aggregate の UI collection replacement は UI callback 内で writer を取得する。folder removal も別の PlaylistRemovalWorkflowOwner で reader 中に FolderRemovalApplied を発火する。通知が reader 解放を待たせる循環を除く必要がある。
- BMS-005: BMSPlaylist の ApplyLocalTableMutation / ApplyPlaylistDropMutation は live table を先に変更し、後で ReplaceTablesWithEntries を呼ぶ。repository の savepoint は DB を戻すが、aggregate の補償は playlist_id だけであり、entry / folder order / last_update / revision の変更が残る。次の保存へ失敗編集が混入し得る。
- BMS-006: root-folder drop の directory group ごとの探索は table.folder_list / entries だけを見る。先行 group で予定した新しい folder / entries が後続 group の検索対象にならず、同順の逐次投入との分類差が生じる。

## Constraints / 決定

- UI を止めないことと、編集を同時に成功させることを分離する。通信・先行編集中の後続編集は別表も含め非待機 Busy、未実行・無副作用・自動再実行なし。利用者の再要求はその時点の対象へ解決する。
- 通信全体へ library file mutation lease を掛けない。通信中の所持譜面操作、設定画面の表示・編集、既存追加 ZIP queue は維持する。URL download / external package lookup の既存相互拒否・導入との拒否を解除しない。
- 新しい manual queue、global library gate、retry / replay、恒久 snapshot cache、広い generation token は追加しない。accepted deferred sync は既存 coalescing worker が終端まで処理する。
- 維持側の具体入口: 通常一覧の folder cell 編集 → `RegularChartListOwner.RenameChartFolderAsync` → chartFileOperations の既存受付 → `BMSLibrary.RenameChartFolderWithReceipt` は playlist 通信状態を見ず許可される。設定は `SettingsDialogViewModel.OpenCommand` → `ISettingDialogPresentationPort.OpenSettingsDialog` → `MainWindow.ShowSettingsWindow` に同通信判定がない。新受付をこれらへ接続しない。`MainWindow.Window_Drop` → `DroppedInstallDropTerminal.Evaluate` は URL download 中の追加 drop を現行で拒否するため維持する。導入中の追加 ZIP は既存 `PackageInstallWorkflow.AcquireAndTryEnqueueDroppedPaths` queue の契約を維持する。
- 設定・property dialog・detail-cell 編集は既存の個別 rollback 契約を維持する。今回の DB 失敗修正は local folder rename / remove / create、entry add / remove、drop の shared local mutation owner を対象とし、playlist 全削除や別の編集基盤を全面的に再設計しない。
- stale 表は保存済み playlist_id または現行 object から一意に解決する。folder / entry が消失・不明なら明示的な未実行とする。entry は元の table / folder と canonical hash（md5 優先、sha256-only を許容）で範囲を限定し、不明な対象を別の同名・同hash配置へ置き換えない。
- plan-clarifier の名前 fallback 質問は、現 ResolveActivePlaylistTable が Reload / LampViewer 表示の caller だけで、今回の mutation 入口へそのまま到達しないためユーザー質問を要しないと判断した。編集用に名前 fallback を追加せず、閲覧 resolver の全面変更も行わない。
- BMS-005 は操作内の snapshot で、durable commit 前に失敗した編集の live 状態だけを戻す方針とする。既存 entry identity を不用意に置換しない。DB commit 後の派生出力失敗では保存済み編集を維持し、通知・reference / UI 更新を durable 結果に従わせる。
- BMS-006 は同順入力のバッチ分割不変性を保証する。曲判定は directory の library MD5 集合と folder 内 entry MD5 の overlap を維持し、タイトルだけの同一視をしない。命名 suffix、重複除去、missing playlist row / bmson の扱いを維持する。既存手動分類の migration、任意順序不変性は追加しない。

## Units / ownership

plan-clarifier の read-only 照合を実施済み。共通 path が多いため writer は一つ、A → B → C の順に進める。root は設計・packet 承認・計画書・統合を所有する。

### A: 編集受付と通知境界（BMS-004）

- Outcome: 手動 reload / URL 取得 / 先行編集の終端まで後続編集を Busy とし、受理済み更新を失わず、UI 通知と collection reader / writer の循環をなくす。
- Writable: `PlaylistWorkspaceViewModel.Mutations.cs`、`Reload.cs`、`PlaylistUrlAcquisition.cs`、`ExternalPlaylistSync.cs`、`LockState.cs`、workspace 本体の既存 gate 宣言、`PlaylistRemovalWorkflowOwner.cs`、`BMSPlaylist.cs` と同モデル側の小さい admission、必要な `PlaylistAggregatePersistenceOwner.cs` / `PlaylistExternalSyncOwner.cs` の接続。MainWindow / composition は型付き通知・依存接続に必要な範囲だけ。
- 設計: playlist store 単位の小さい論理受付へ、manualReloadSemaphore と URL command gate の重複を統合する。新手動操作は非待機取得、accepted deferred worker は既存 queue 内でのみ非同期に先行操作の終端を待つ。external updating の nested counter と既存適用 reservation / revision を維持し、他の背景入口中も新規 local edit を拒否する。公開・通知まで論理 ownership を持つが、collection/table guard・DB scope は UI 完了待ち前に解放する。
- entry の複数表削除は一つの要求として受付し、その中で順次処理する。同要求内の Task.Run fanout が自分自身を Busy 拒否する構造にしない。新たな複数表 DB transaction の保証は作らない。
- UI の Busy / stale 未実行通知は既存 presentation route を使う。必要な新文字列は Resources.resx / Resources.cs / lang 全6言語と parity 検証を揃える。
- Tests candidate: `PlaylistWorkspacePersistenceCommandTests`、`MainWindowPlaylistWorkspaceWpfTests`、`BmsPlaylistExternalReloadTests` と URL / removal の既存 owner fixture。fixture fit は独立 designer で確定する。
- 制約: 手動 reload / URL gate の統合は playlist 内の排他であり、新しい library-wide Busy ではない。既存 deferred queue が受理した request の非同期待機だけを許容し、新手動 reload を待機列へ積む旧 semaphore route は非待機へ置換する。

### B: local mutation の DB 失敗境界（BMS-005）

- Outcome: DB commit 前の失敗で live と DB の編集内容を戻し、次の別編集の成功保存・再openへ失敗内容を漏らさない。commit 後の出力失敗は保存内容を保持する。
- Writable: `BMSPlaylist.cs`、`BMSTable.cs`、必要な `BMSTableEntry.cs` の限定状態保存、`PlaylistAggregatePersistenceOwner.cs`、A の通知 consumer。repository への fault 接続が必要なら既存実 DB failure fixture を優先し、production の汎用 fault / retry 基盤を作らない。
- 設計: shared local mutation と drop の保存を、membership / table writer / aggregate 保存境界に閉じる。変更前の entries と必要な mutable field、folder order、更新情報を操作内だけ保持し、commit 前の失敗に限り復元する。保存失敗後の別編集と並行しない。commitFlag=false の既存内部 memory-only 経路は維持する。
- 旧 live mutation → 独立保存の無補償 route を両 consumer から退役する。派生出力の例外で DB failure 補償へ戻らない。drop の既存 durable outcome / receipt を重複実装せず共有できる部分をまとめる。
- Tests candidate: `PlaylistWorkspacePersistenceCommandTests`、`BmsPlaylistPersistenceLifecycleTests`、既存 custom-folder failure coverage。同 invariant の fault matrix を複数 fixture へ重複しない。
- `AddPlaylistEntriesToFolderBMSTable` は現 production consumer がなく、宣言だけを根拠とする専用 runtime contract は作らない。現 drop は `ApplyPlaylistDropMutation` を使う。shared helper と既存内部互換の静的確認で扱う。
- 復元境界の追加確認: BMSTable.RenameFolder は既存entryのfolder、entries集合、Folder_order、last_update、entries revisionを変える。RemoveFolderはrenameへ接続し、CreateFolderはdummy追加、RemoveEntriesは最後のentry消失時dummy保持、dropはこれらを組み合わせる。参照を保持したentries集合と変更field、順序・更新情報を一つの操作内で保全する。repository正規化の実際の変更fieldも確認し、意味のある失敗変更を残さない。private cacheは復元データに合わせて再構築でき、cache形状自体を永続契約にしない。

### C: 同順 drop の分類（BMS-006）

- Outcome: 一括 / 分割の同順投入で同じ folder 所属と重複除去後 entry 集合にする。
- Writable: `PlaylistWorkspaceViewModel.Mutations.cs`、`BMSPlaylist.cs` の drop plan / apply、必要な `BMSTable.cs` 命名規則の共有、近傍既存 drop fixture。
- 設計: directory 順を保つ一回限りの working set に、予定 folder と entry identity を加える。後続 group は既存＋予定を同じ曲判定で検索する。命名・重複除去の二重実装を避け、計画段階で live table を編集しない。B の保存境界へ接続する。
- 入口 assumption の補足確認: LR2SongDB.song の primary key は path（hashではない）。同MD5の別directory譜面は実libraryで扱う状態であり、既存duplicate serviceもこの状態を対象にする。C01のdirectory間MD5 overlapはfake専用の不可能状態ではない。

## Test necessity / verification / Done when

今回の変更は durable data と並行性・終端を修正する bugfix のため、恒久テストを必要と判断した。既存更新＋不足箇所の追加とし、`PLAYLIST-EDIT-SAFETY-v1` の独立 oracle / coverage ledger 承認後に実装する。現実の ingress からの red / head-pass を優先し、compile / setup / unrelated failure を red と数えない。

各 unit の関連 filter Quick、最終 production snapshot の Functional、必要な新リソース parity、UTF-8 / LF / 参照 / diff check、凍結 static review を完了し、計画の AC と evidence を整理して commit する。review 中は root の repository 操作を停止する。Full は release / runner の前提変更がない限り対象外。

Replan trigger: 同期中に維持すべき library / settings / queue 入口を失う、新しい manual queue / persistent state / recovery machinery が必要、実入口から到達しないテスト前提、未承認の identity / 分類変更、所有 path 外の大きい変更、または再発する verification failure。曖昧な observable semantics はユーザー質問として提示し、独立な作業を続ける。

## 承認済み Test Contract Packet: PLAYLIST-EDIT-SAFETY-v1

Root approved: 2026-09-09。designer は Phase A で上記の root 承認 outcomes、各 issue AC、schema / 並行性契約から oracle を凍結し、その後の Phase B で実入口・fixture・resource・completion だけを照合した。production body、既存 expected / snapshot、current output、翻訳値は oracle の authority にしていない。

Authority: A=root採用BMS-004とconcurrency spec section 6、B=root採用BMS-005と正本保存・派生出力契約、C=root採用BMS-006とplaylist_entryのMD5優先規則。A4は上記で確認した実入口とdrop-install-ingress仕様。前提は初期化済みactive store、通常の所有DB、正規の表示行/library chart。外部DB改ざん、private state任意生成、架空のidentity-less編集は除外。複数表削除は一要求受付・表ごとcommitで、既存の成功済み別表までrollbackしない。

| ID | Outcome / authority / production route | 必須 invariant と識別する誤実装 |
|---|---|---|
| A01 | A / 004-AC1..3。MainWindow rename/create/drop/entry delete、確認済みfolder delete → Workspace / RemovalOwner → store admission | manual reload / URL download / external package / 先行編集の受理から通知・cleanup終端まで、別表含む後続編集を待たず明示未実行。live/DB/output副作用・終端後の自動再生なし。改めた要求は実行可能。queue化・別表例外・通知前解放・複数表削除の自己Busyを識別 |
| A02 | A / 004-AC3、hash schema。正規reload/削除後の古い表示選択 → 編集受付 → 現在対象解決 | saved playlist identity/現行objectで一意に解決。entryは元table/folder＋canonicalhash（md5優先、sha256-only）。消失・曖昧は未実行、同名別対象へ転送しない。stale直接編集・名前fallback・folder無視の同hash削除を識別 |
| A03 | A / 004-AC1/2。manual/deferred sync → ExternalSyncOwner → Aggregate → 実UI scheduler、local edit → 実refresh consumer | 通信保留中もDispatcher continuationを処理。accepted deferredを競合だけで捨てず、反映・通知を終端し後続明示編集を受理。失敗時も受付解放。reader内UI待ち・counter早期解除・accepted sync破棄を識別 |
| A04 | A / 004-AC4。通常folderrename owner、settings presentation、Drop terminal/install queue | 通信だけ保留中に実folderrenameが受理・完了。設定入口を一括無効化しない。URL download中ZIP拒否・導入中追加ZIPqueueを維持。library fileleaseへの排他拡張・ZIP常時許可/拒否を識別 |
| B01 | B / 005-AC1。reachable rename/create/remove folder/delete entries/drop → BMSPlaylist → Aggregate → Repository | commit前失敗でtableのlive/DBを編集前へ戻す。entry内容/所属、folder構成/順序、変更metadataを保全し、entry identityを不用意に置換しない。失敗編集の成功表示/reference更新なし。playlist_idだけ/DBだけ/entriesだけ補償を識別 |
| B02 | B / 005-AC2。B01失敗 → 同active tableの独立成功編集 → DB再open | 最初の失敗変更が次の保存・再openへ混入しない。後の明示変更だけ保存。保持entryが以後も編集可能。画面だけの復元やdirty別collection保存を識別。無条件reloadで試験準備をリセットしない |
| B03 | B / 005-AC3。実local edit commit → custom-folder/BMT owner → notification/UI/reference | commit後の出力失敗でもlive/DB/reopenの編集保持。出力対象・原因の失敗通知、UI/referenceはdurable結果に従う。async BMTは既存の別終端を観測。catch-all rollback、出力失敗隠蔽、表示のみ旧状態残留を識別 |
| C01 | C / 006-AC1。root-folder AddRowsToFolderAsync → planning → ApplyPlaylistDropMutation → save | 同順一括/二分割で曲folder所属と重複除去entriesが等価。後続directoryのlibrary MD5集合に先行chart MD5が含まれる例は、所定2hashが1folderに各1件。比較だけでなく独立invariantもassert。先行予定無視/毎group新folderを識別 |
| C02 | C / 006-AC2、hash schema。正規library/playlist row → root drop → save | overlap既存folderへ合流、同タイトル別曲・orgなし別曲は非統合、名前衝突で上書きなし。missing row/bmsonの既存情報保持。タイトル同一視/空org共通identity/名前key衝突/sha256-only消失を識別 |
| C03 | C / 006-AC3。root drop → private planning → admitted mutation/save | planningは操作内候補のみでliveへ先行適用しない。B01/B02も満たす。private planner test/probeは追加せず、C01/C02/B01のcommand coverage＋静的reviewで閉じる |

許容差: gate/working set/snapshotの型、翻訳・presentation・format、無関係な通知順・coalescing内部回数・revision具体数値・DB採番/日時は固定しない。A04は設定の既存Save/overlay制約とURL/install相互拒否を維持し、全library操作の同時成功を要求しない。Cの命名suffixをexpectedへ写経せず、一括/分割の既存規則一致と衝突非破壊を確認する。任意入力順不変やmigrationは要求しない。

### Coverage ledger / verification

- A01/A02: `PlaylistWorkspacePersistenceCommandTests` extend。folder removalの実terminalは`MainWindowPlaylistWorkspaceWpfTests`。GUID DB/filesystem、既存settings ownership。実command Task・rejection/presentation・先行completionをsignalにする。既存external/special拒否coverage維持。
- A03: `BmsPlaylistExternalReloadTests` / `MainWindowPlaylistWorkspaceWpfTests` extend。既存DNP/serial-state-aと共有`TestUiDispatcherHost`を維持。HTTP到達/解放・UIcallback completion・owner Task・Dispatcher markerを観測。fake ownerだけやprivate lock任意生成で循環を作らない。
- A04: `RegularChartFolderRenameTests` / `PlaylistUrlAcquisitionOwnershipTests`で通信保留中実renameを一例追加。設定は既存実入口fixtureと静的接続、ZIPは`PackageInstallWorkflowOwnerTests`等の既存coverage利用。
- B01/B02: `BmsPlaylistPersistenceLifecycleTests` / `PlaylistWorkspacePersistenceCommandTests` extend。reachable編集をdata caseにし、所有DBの実保存失敗、明示fieldsの比較、独立成功保存・再openを一caseへまとめる。前者の既存DNPを維持。対象外bulk/property rollbackを重複追加しない。
- B03: `BmsPlaylistPersistenceLifecycleTests`と既存drop/reference通知caseをextend。所有outputへの実I/O failure、edit Task/custom-folder receipt/実scheduled BMT Taskを観測。eventへfailureを直接投げるだけで代替しない。
- C01/C02: `PlaylistWorkspacePersistenceCommandTests` extend。別々の一括/分割DBと小さいchart fixture、AddRowsToFolderAsync completion/readback。C03はこのcoverageとB01、static reviewを利用。
- 共通helperは`BmsPlaylistTestSupport`、`PlaylistWorkspaceFixtureFactory`、既存chartbuilder、`TestUiDispatcherHost.AwaitTaskOnDispatcher`を優先。固定sleep/成功推定timeout無し。HTTP/scheduler gateはfinallyで解放し、発行済みTask終端後に所有resourceを破棄。新DNP/lane/shardなし。
- Red/head-pass対象はA01通信中Busy、B01/B02保存失敗後混入、C01一括/分割差。sharedDispatcherへ故意のdeadlockを残すA03 redは必須でない。compile/setup/unrelated failureはredでない。targeted mutantは現時点not applicable、新seamでbase不可だけを理由に追加しない。
- Exactness exceptionなし。localized copy/source/private symbol/broad snapshot/characterizationを固定しない。合成hash/folderは独立入力として比較可。C03 static reviewはsource text testの新設ではない。
- Quick filterは担当unitの上記fixture名で限定、必要な新resourceは`LocalizationResourceParityTests`を含める。最後のFunctionalはroot。workerはfixture/builder/fault接続/assertion mechanicsを適合可能だが、oracle/authority/variationや拒否範囲を変更しない。旧未補償route、reader内通知、旧gate、先行予定を無視するplanの退役をhandoffで示す。

## 実装・統合記録

### A handoff

- store単位の受付、active identity解決、reader解放後通知、accepted deferredの非同期待機を実装。B/Cは後続。
- A03の配置は既存 `PlaylistWorkspaceDetailRefreshTests` の実consumerへ適合。packetのoutcomeは変更せず、rootがownership追加を承認。
- Quick: `tests-quick-20260909-040415` 128/128、`040247` DetailRefresh 47/47、`040209` PersistenceLifecycle 25/25。
- A01: `041232` head-pass 1/1。`041140` の一時negative-controlはBusy判定を無効化し、対象assertionで1/1失敗。productionは復元済み。
- root統合で、fixture専用fallback state、対象外whole-table removalへの拡張、消失drop先folderの検証不足を指摘。B作業と同じwriterへA補完として返し、最終handoffで閉じる。最終acceptanceは未実施。

### B handoff

- 操作内snapshotと共有保存境界、commit後のdurable outcomeに応じた通知・参照更新を実装。rename/create/remove folder、行削除、dropの実DB失敗→別編集保存→再openを追加。
- Quick: `tests-quick-20260909-050833` 154/154。B01の失敗変更を識別するnegative-controlはCの担当へ追加検証を依頼。
- A補完としてproductionのfixture専用fallback、whole-table removalへの拡張を退役し、消失したnamed folderへのdrop拒否を追加。
- root統合で、fixture全体のstatic store共有によるadmission競合とcleanup欠落を指摘。test/workspaceの所有単位へ変更するよう返した。entry補償も、対象mutationと正規化が変更するfieldに限定することを再確認。これらはCと同じwriterで補完し、最終handoffで閉じる。

### C / 最終実装 handoff

- root-folder dropを入力順のworking setへ変更。先行予定folderの後続利用、既存命名suffixの共有、Bの保存境界への接続を実装。
- C01: `tests-quick-20260909-053018` base-fail、`054246` head-pass 2/2。B01: `053450` snapshot復元を無効化したnegative-controlで失敗、`053418` head-pass。両方ともproductionは復元済み。
- 最終関連Quick: `tests-quick-20260909-054434` 173/173。production build 0 errors、diff check成功。
- fixture単位の実DB所有・cleanupへ変更し、static mutable storeを退役。entry復元fieldも対象mutation/正規化が変更するものへ限定。A/B統合指摘は実装担当が補完済みと報告。
- 全体Functionalとfresh static reviewはrootで実施する。

### 全体検証

- `tests-functional-20260909-054727`: restore / format / build成功、analyzer 0 diagnostics。serial-state-bの `DefaultFactory_MapsEveryOwnerRouteAndPreservesPlaylistCancellationPriority` がHTTP受付待ちでtimeout（1件）。他の完了hostは成功、remainingはfail-fastで中断。全体passとして数えない。
- compositionだけ生成した未初期化fixtureからURL取得を呼ぶため、store必須化との前提不整合を候補として実装担当へ原因分類と限定修正を依頼。timeout延長・並列度変更で回避しない。修正後の関連QuickとFunctionalを実施する。
- 原因はfixtureの初期化不足と分類。owned schema-backed storeと既存StartupLibraryServicesを接続し、cancel-priorityのassertionは維持。`055517` red 4/5 → `055859` head 5/5、`060007` 関連Quick 62/62。同型の未初期化composition＋実URL要求fixtureは追加で見つからず、production fallbackは追加していない。
- review前Functional: `tests-functional-20260909-060110` 成功。全4658件、4647成功・11スキップ・0失敗。test execution 200.1秒（180秒reporting target超過、300秒budget内）。restore / format / build成功、analyzer 0 diagnostics。ここから差分を凍結してfresh static reviewへ渡す。

### Static review 1

- P2 / C01: 複数folderとoverlapする後続directoryについて、working set作成順と実folderのFolder_order＋自然順が異なり、一括/分割で所属が変わる。実順序規則を共有し、複数overlap入力のcommand regressionを補完する。期待値は承認済み一括/分割同値と独立した入力hash不変条件から置く。
- P2 / A01/A03: 追加のdeferred testはgate直接取得・空collection・worker delegateだけで、実通信とUI consumerを通らない。実manual reload/URLの通信保留、別表Busy、Dispatcher継続、UI通知終端と再要求を既存fixtureで補完し、fake-onlyの重複caseを置換する。
- 上記2件を採用して同writerへ限定修正を委譲。packetのobservable semanticsは変更なし。修正後は関連Quick、production分類変更を含むFunctional、fresh reviewを行う。
- R1補正: BMSTableのfolder順序規則をplanningと共有。`tests-quick-20260909-062051` 旧順序red、`062517` head-pass、`072223` C01 pass。
- R2補完: 実HTTP・MainWindow consumerを通すmanual reload/deferred sync、別表Busy、UI終端と再要求の検証へ置換。旧fake-only A03追加分は削除し、DetailRefresh fixtureの最終差分はなし。`072614` 実consumer pass、`072350` 関連40件、`072505` Persistence33件、`072528` Detail46件成功。diff check成功。
- 修正後Functional: `tests-functional-20260909-072830` 成功。全4658件、4647成功・11スキップ・0失敗、test execution 210.6秒（300秒budget内、180秒reporting target超過）。restore / format / build成功、analyzer 0 diagnostics。最終差分を凍結してfresh reviewへ渡す。

### Static review 2

- P1 / C02: 正規reloadが保持する削除履歴だけのfolderも新working setが候補へ含め、同期解除後のroot dropで履歴と重複する新entryが消える。非削除folderの既存候補規則へ合わせ、正規状態生成から正常追加・DB保持の回帰を補完する。削除履歴全消去・dedup全面改修はしない。
- P2 / A03: 新しい実consumer testはmanual終端後にdeferredを開始し、先行操作とのadmission競合を検証していない。実通信保留中にaccepted deferredを開始し、先行解放後の通信・UI反映・通知終端を確認する順序へ修正する。
- 上記を採用して同writerへ限定修正を委譲。C02/A03のauthorityとoutcomeは変更なし。修正後の関連Quick、Functional、fresh reviewを行う。
- R3: 分類候補からis_removed行を除外し、削除履歴自体は保持。正規reload後の同期解除・root dropからDB readbackを確認。`074910` negative-control失敗、`074939` head-pass。
- R4: 既存WPF実HTTP fixtureでmanual競合中のaccepted deferredを保持し、通信・UI終端と再要求まで確認。`075546` exact pass、`075824` R3/R4 2/2、`075853` 関連54/54成功。diff check成功。
- `tests-functional-20260909-080048`: restore / format / build成功、analyzer 0 diagnostics。他5host成功、serial-state-bが300秒budgetでtimeout。最後の成功はMainWindowのplaylist dialog/close関連、次の実HTTP競合caseを候補に調査を委譲。cleanupのdeadline警告あり、終了後のtesthost残存はなし。全体passとして数えず、timeout延長やlane変更で回避しない。
- rootの機械的handoff補完として、追加APIのXML契約コメントを日本語で追記。処理変更なし。
- timeout調査では前段＋対象27/27、Functional順前段11class＋対象231/231、同じWorkers=1のserial-state-b相当437/437（`artifacts/verification/diagnostics-serial-b-all/results.trx`、1.48分）が成功。code/test追加変更なし、testhost/vstest残存なし。単独成功から他host競合を原因と断定せず、未再現・原因未確定として同じ標準Functionalを一度再実行する。
- retry `tests-functional-20260909-082303` はserial-state-aの既存SettingsForegroundInteractionTestsでkeyboard focus assertion failure。serial-state-bは新R4へ到達する前にfail-fast終了したため、timeout解消の証拠にはしない。前回とは別の失敗であり、machine loadへ断定せず、workerからissue-resolverを一度使い、共有UI/OS状態とfixture ownershipのoperational blockerを切り分ける。

### R4 operational blocker の設計補正

- issue-resolver が実 completion callback 内で admission 保持を制御して再現。通知 event は worker Task 終端の保証ではなく、これを待って直ちに再編集するテストが Busy modal で停止し得た（`diagnostics-issue-resolver-r4-0830/report.md`）。production の通知中 admission 保持は維持する。
- A03 は既存 scheduler / task owner の実 worker Task 終端を待つ。event を終端へ移動する変更、固定待ち、Busy retry、private state probe、新しい永続 state は入れない。既存 seam で実 Task を観測できない場合は実装を止めて root に返す。
- Busy 通知は MainWindow の既存 playlistWorkspaceDialogService に接続する。UiDialogCoordinator の既定 message 表示も既存 modalScopeFactory を表示前から終了まで適用し、ThemedMessageBox は同じ表示処理へ scope を渡せる最小の境界を設ける。テストは実 message box を共有 NonActivating scope で表示する。既存 custom presenter 契約は維持し、無関係な MainWindow message route の全面変更はしない。
- writer ownership を MainWindow.cs、UiDialogCoordinator.cs、ThemedMessageBox.cs、MainWindowPlaylistWorkspaceWpfTests.cs と該当既存 dialog tests の必要最小限の機械的更新へ拡張。A01/A03 の packet semantics は変更なし。focused Quick 後、root が Functional と fresh review を行う。
- settings focus failure の原因は未確定。R4 の停止と同一原因と断定せず、runner や foreground assertion は変更しない。
- Task 観測補正: 実 scheduler は bool 受付しか公開しないため、テストだけの Task 公開 state は追加しない。実 HTTP・通知の完了を観測した後、既存 store の `WaitForPlaylistMutationAsync` を非同期に取得して即解放し、admission の終端を同期点として使う。これは競合状態の生成や実入口の代替ではなく、すでに実操作が保持する受付の解放待ちに限る。A03 の authority は Task object 自体ではなく、通知後に明示した編集が Busy にならず成功すること。fixture shutdown は既存 owner drain を維持する。最終編集でも予期しない通知を fixture scope で閉じ、失敗時に modal 停止へ変えず結果 assertion を失敗させる。
- R4 operational fix handoff: Busy 通知を既存 dialog service へ接続し、既定 message presenter に表示前から終了まで modal scope を適用。テストの process-wide class handler を退役し、fixture 所有の Dispatcher close operation へ統合。completion / presentation 後に既存 admission の解放を待って最終 rename を検証。`tests-quick-20260909-090344` 関連8/8成功、diff check成功。root が最終 Functional を実行する。
- root 統合で default presenter の例外判定を bound delegate equality に置き換え、custom presenter の既存 shutdown failure semantics を維持。Functional の build 開始前に反映し、他の progress route は変更なし。

- tests-functional-20260909-090500: analyzer 0、build 成功。serial-state-a の既存 ShellShutdownWorkflowOwnerTests.PreparationWaitsForRunningReloadCleanupAfterPendingCancellation が共有 bin/config/user.config 読込みで IOException（他 process 使用中）。fail-fast により未完了 host があり全体成功ではない。前回の focus failure とも別事象。実 fixture の設定ファイル所有を切り分け、retry / lane 縮小 / production fallback は追加しない。終了後 testhost / BeMusicSeeker 残存なし。

### 独立したテスト設定所有の補正

- 失敗 stack は既存 shutdown fixture の既定 Settings が共有出力先 user.config を読むことを立証する。排他アクセスした相手の test/process の特定まではできておらず、PortableSettingsPersistenceTests 自体の通常 fixture は一意 path を使うため、同 class を原因 writer と断定しない。
- 対象メソッド `PreparationWaitsForRunningReloadCleanupAfterPendingCancellation` にだけ、既存 `PortableSettingsPersistenceTests.OpenSettings(path)` と一意 temporary directory を使って設定を注入する。shutdown / cleanup assertion semantics は変えず、専有 DB 等の新しい一般基盤は追加しない。変更分類は既存 fixture の機械的な resource ownership 修正で、独立 packet 不要。writer ownership は ShellShutdownWorkflowOwnerTests.cs の対象メソッドのみ。
- 関連 Quick 後、独立した test-only commit として本筋の変更から分ける。review は全差分をまとめた凍結 snapshot で行い、root が最終 Functional を担当する。

- 設定分離の handoff: 対象メソッドへ一意 temporary user.config を注入し、finally で SettingDialog と directory を cleanup。既存 assertion 不変。Quick 092542 の ShellShutdownWorkflowOwnerTests 26/26 成功、diff check 成功。

### 最終 Functional

- tests-functional-20260909-092653 成功。全4659件、4648成功・11スキップ・0失敗、test execution 177.8秒。restore / format / build 成功、analyzer 0 diagnostics。diff check 成功、testhost / BeMusicSeeker 残存なし。settings focus failure はこの最終 run で再発せず、原因を断定しない。
- 全差分を凍結し、review 2 からの fix delta（削除履歴候補の除外、実競合 deferred test、admission 解放待ち、message modal scope、設定 fixture 分離）とその直接影響を fresh review へ渡す。

### 最終 static review / 完了

- fresh reviewer が review 2 以降の修正差分、前回 finding の解消、直接影響する production route・tests・spec・証跡を read-only で確認。blocking finding なし、追加推奨なし。未確定の既存 Settings focus failure は今回変更への帰因 evidence なしとして区別した。
- BMS-004 / 005 / 006 の受入条件を確認し、現行契約を playlist-data-and-export-flow.md へ統合。ユーザー承認済みの範囲で commit し、push / version 更新は行わない。
- 設定fixture分離は独立commit 9c1ae6a8（test: isolate shutdown cleanup settings fixture）へ分離した。
