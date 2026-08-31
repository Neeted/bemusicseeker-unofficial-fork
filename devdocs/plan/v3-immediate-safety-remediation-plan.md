# v3.0.0.0 immediate safety remediation plan

Status: Active

Plan ID: `V3-SAFETY-2026-08-31`

Planning snapshot: `c1139d88c49f184ef10994b97ae1387fa553d46a`

Stable comparison point: `v2.1.6.0` / `3c000ec2e7a6e619c60d0f8c9e48ad12bd06d4f5`

Target release: `v3.0.0.0`

Last updated: 2026-08-31

Implementation status: in progress

## この計画の運用

- 本文の decision list と Test Contract Packet を実装時の authority とする。current implementation、現在の出力、既存 snapshot を期待値の根拠にしない。
- 各 unit は `Pending -> In progress -> Implemented -> Verified -> Reviewed` の順で進め、下の progress ledger に owner、実行結果、artifact path、残リスクを追記する。
- 同時 writer は最大 2。writable path、巨大 file、schema、shared fixture が重なる unit は直列にする。
- observable contract を変える必要が生じた場合、green にするため test expectation を弱めず、該当 unit を `Needs replan` に戻す。
- 実装 worker は承認済み Packet ID、base-fail または targeted negative-control evidence、focused Quick、変更 path、退役 route、残リスクを handoff する。
- 全実装 worker を閉じた後に integrated snapshot を凍結して static review を行う。reviewer の実行中、root は repository を読み書きせず、build/test もしない。
- 完了時は恒久契約を `devdocs/spec/` へ統合し、この文書には release 固有 evidence と履歴を残す。

## Goal

監査で「ただちに修正が必要」と判定した挙動を、v3.0.0.0 の release blocker として閉じる。特に次を保証する。

1. 公開済み v2.1.6.0 から v3 への first hop を維持し、その実 artifact を release gate で検証する。
2. v3 以後の updater は、managed file がロックされた通常の失敗で旧アプリを起動不能にしない。
3. 起動初期化と playlist import の lock-order cycle を除去し、UI 無応答を防ぐ。
4. DB repair、raw SQLite read、playlist の保存・復元において、失敗を正常完了に見せず、途中状態を commit/publish しない。
5. package install と library folder move に、現実的で有限な filesystem/DB compensation contract を導入する。
6. 外部入力の用途別境界を固定し、URL1/URL2 は HTTP(S) のみにしつつ external playlist sync の local/UNC を維持する。drop は全 descendant reparse を拒否する。
7. release-critical test の未検出、重複、skip/inconclusive を release success にしない。

この plan の全必須 unit と release gate が完了するまでは v3.0.0.0 を release-ready と判定しない。

## Context

- 公開 v2.1.6.0 zip の authority は size `11,260,709` bytes、SHA-256 `C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2` である。
- 実 artifact の通常更新は現 snapshot の v3 package へ到達できた。一方、旧 updater で `lang/ja-JP.json` を排他 lock すると updater は失敗し、canonical `BeMusicSeeker.exe` が消えて backup にだけ残る状態を再現した。
- 現行 Full の `ab9d97ed...` baseline は current updater の互換・recovery lane としては有用だが、公開 v2.1.6.0 artifact の first-hop oracle ではない。
- 起動中は playlist initialization が semaphore を保持したまま同期 UI mutation を待ち、UI 上の recommended/external import が同じ semaphore を待てるため cycle が成立する。
- non-generic `Task.Logging()` は元 task ではなく logging continuation の成功を返し、startup schema repair、detail/bulk edit、restore などの failure を成功に変換できる。
- raw SQLite reader は `SQLITE_ROW` 以外をすべて EOF とみなし、`SQLITE_BUSY`、`SQLITE_ERROR`、`SQLITE_CORRUPT` などでも partial result を正常扱いできる。
- app schema repair の outer transaction 内で playlist schema normalization が `Commit()` し、後続 failure を rollback 不能にできる。
- package install と folder move は filesystem を先に破壊的変更し、その後に DB durable update を行う。
- `DroppedInstallIngressMaterializer` は transient source を再帰検査するが、stable source は存在確認後に早期 return し、descendant reparse を検査していない。

## Constraints and compatibility

- v2.1.6.0 updater が理解する protocol/package format 1 と `minimumUpdaterVersion: 1` を維持する。bridge release や first hop の打切りはこの plan に含めない。
- actual legacy updater の locked-managed-file failure は edge case として characterization する。旧 managed tree の自動復旧は acceptance に含めないが、legacy updater の既存 console/`stderr` diagnostic と非 zero exitによるfailureの明示、および `data/`、`config/` の不変は必須とする。
- current v3 updater の既存 journal、RunOnce/persistent supervisor、recovery contract は維持する。「新しい persistent journal を導入しない」という制約は package/folder の filesystem+DB unit にだけ適用する。
- filesystem と SQLite を一つの crash-atomic transaction にできるとは主張しない。media failure、OS crash、電源断、検査後 TOCTOU、compensation 自体の I/O failure を完全には吸収しない。
- failure を隠す fallback、意味の変わる browser fallback、再帰的 rollback、自動 infinite retry は追加しない。
- UI thread で sync-over-async を行わず、model lock、DB transaction、operation gate を保持したまま UI、dialog、subscriber、別 owner の完了を同期的に待たない。
- 新しいユーザー向け status/error text が必要なら resx、generated accessor、六言語 JSON を同じ unit で更新し、exact 翻訳文は test oracle にしない。
- commit、tag、push、package 公開、version 更新はこの plan の実行だけでは行わない。

## Decision list

1. `D-01 First hop`: 公開 v2.1.6.0 updater から v3 への経路を維持する。実 artifact の代わりに source build または `ab9d` baseline を使う fallback は認めない。
2. `D-02 Legacy lock`: actual v2 updater の lock failure は characterization とする。legacy console/`stderr` の非空 error diagnostic と非 zero exit、および `data/config` 不変を要求するが、managed tree の自動 rollback、canonical exe、lock 解放後の自動 retry convergence は要求しない。
3. `D-03 Manual recovery`: 公開 v3 release note に、更新失敗時は v3 を新しい空 directory へ展開し、旧 directory の `data` と `config` を手動コピーすればよい旨を記載する。in-app note だけには依存しない。
4. `D-04 Current updater`: current v3 updater は mutation 前に managed path の利用可能性を preflight し、通常の lock failure では mutation を始めない。mutation 後 failure の rollback は canonical tree を delete-first にせず、backup から staging/replace で復元し、失敗を receipt に残す。
5. `D-05 Startup admission`: playlist initialization 中の external/recommended import は捨てたり同期拒否したりせず、単一 owner が admit/queue する。readiness `Task` まで非同期 defer し、shutdown cancellation で producer/consumer の双方を terminal にする。
6. `D-06 DB transaction owner`: path-based schema API が transaction を所有し、borrowed connection participant は commit/rollback しない。outer repair の durable commit だけが schema/version/digest/data を確定する。
7. `D-07 SQLite completion`: raw reader は `ROW` を継続、`DONE` だけを成功とする。それ以外は throw し、step error を finalize/cleanup error より authoritative にする。partial dump/read は publish/restore しない。
8. `D-08 Failure visibility`: awaited/coordinated task の failure は logger 以外から観測できる。durable failure 後に success milestone、live-state publication、`afterApply`、success dialog、close/shutdown を行わない。
9. `D-09 File/DB boundary`: package/folder mutation は immutable preflight、destination-local sibling staging、source retention、overwrite backup、DB durable receipt、finalize の順にする。batch 全体の compensation owner は一つだけとする。
10. `D-10 Bounded compensation`: pre-commit failure では compensation をちょうど一度だけ best effort で試す。compensation failure 後は以後の mutation を停止し、source/backup/staging を保持して `ManualRecoveryRequired` と関連 path を返す。再帰補償と自動 replay は行わない。
11. `D-11 Durable cleanup`: DB durable success 後は compensation しない。destination と DB を authoritative とし、source/backup cleanup を一度だけ行う。cleanup failure は `CompletedWithCleanupFailure` とし、残置物を保持して fresh install として再実行しない。
12. `D-12 External URI`: external playlist sync は drive path、`file:` URI、UNC を許し、SMB 接続を禁止しない。URL1/URL2 acquisition の initial/resolved/recursive hop は `http`/`https` のみとし、unsupported scheme を gateway、temp write、browser、install sink へ渡さない。`AppHttpClient` は global 制限しない。
13. `D-13 Drop`: stable、managed、transient の別なく、source、trusted root 下 ancestor、全 descendant の reparse point を batch admission 前に拒否する。一件でも該当すれば batch 全体を拒否する。検査後 TOCTOU の完全排除は対象外とする。
14. `D-14 Deferred findings`: 「修正推奨だが v2.1.6.0 からの回帰ではない問題」と「稀な edge」は初回実装へ混ぜず、後続 backlog に残す。
15. `D-15 LR2 sync`: LR2 同期が未収束でも Completed になり得る件は本 plan で変更せず、[LR2 song.db 同期診断の監査メモ](../memo/lr2-song-db-sync-diagnostic-audit-note.md) を後続調査の正本とする。
16. `D-16 Empty DB first scan`: 完了 marker 不在は、既定設定では file diff による retry が成功すれば収束するため、issue/backlog として扱わない。

## Done when

- `U1` から `U9` がすべて `Reviewed` になり、未解決の P0/P1 または acceptance へ直接反する P2 がない。
- exact identity の公開 v2.1.6.0 artifact から protocol 1 の v3 package へ更新し、v3 app の起動と `data/config` の保持を確認した receipt がある。
- actual legacy lock characterization が legacy console/`stderr` diagnostic、非 zero exit、`data/config` 不変を確認する。current updaterはpreflight lockで旧treeを無変更に保ち、post-mutation failureで旧treeをexact復元し、rollback second faultで両failureと回復materialを保持して既存recoveryへ収束できる。
- startup 中 import の UI responsiveness、readiness 後一回実行、shutdown cancellation を固定待ちなしで検証する。
- schema repair の late fault が全変更を rollback し、同じ DB の retry が収束する。raw step error と partial restore の negative control が通る。
- detail、bulk、restore、startup の durable failure が成功 publication/close/shutdown を起こさない。
- package と folder move の failure matrix が、source/destination/DB/backup/result と compensation 回数を検証する。
- URL1/URL2 の全 hop policy、external sync の local/file/UNC、stable source を含む drop descendant reparse rejection が検証される。
- release-critical roster の全 exact FQN が一度だけ `Passed` で、required skip/inconclusive/missing/duplicate がない。optional skip は exact allowlist と理由 receipt を持つ。
- public `release notes/v3.0.0.0 リリースノート.md` に `D-03` の案内が含まれ、`devdocs/spec/` が実装後 contract と一致する。
- integrated focused Quick と、canonical Functional phase を内包する release 用 Full が final snapshot で成功し、`git diff --check` が clean である。同じ final snapshot で standalone Functional を重ねて実行しない。

## Out of scope for the initial implementation

- actual v2 updater の locked-managed-file failure 後に旧 managed tree を自動復旧する bridge/bootstrap。
- package/folder filesystem+DB mutation の persistent journal、自動 crash replay、電源断に対する完全原子性、recursive compensation。
- file handle を保持した traversal による完全な reparse/TOCTOU 防止、すべての filesystem/media failure の回復。
- DB physical corruption の自動 quarantine/rename と空 DB rebuild、代表的な実運用大規模 v2 DB/WAL/hot-journal の release fixture。
- settings provider durability、unknown future schema、LR2 backup partial suppression、root playlist の multi-store failure。
- updater の VM hard-power-loss qualification、別 volume、junction/mount-point、network share の完全 support matrix。
- archive/JSON 展開 budget、URL1/URL2 以外の download source の一括再設計。
- auto-rename/merge へ同じ filesystem+DB contract を広げること。必要性が判明した場合は別 unit として再計画する。
- Everything service、external encoder、production host integration の provisioned qualification。
- empty DB first scan の新しい完了 marker。

## Frozen Test Contract Packets

Authority は decision list、上記の approved audit facts、関連する feature spec、SQLite result-code contract、公開 artifact identity である。Test Contract Packet は plan-clarifier 後に独立 session で凍結し、root が承認した。test method 名、fixture mechanics、internal result type は、observable outcome を保つ限り variation とする。

### `SAFE-UPDATER-3000`

| Contract ID | Required observable outcome | Negative control / completion | Placement |
| --- | --- | --- | --- |
| `UPD-V216-HAPPY` | exact public v2 artifact の updater から protocol-1 v3 package へ成功する。updater適用直後の`data/config` treeはbyte-identicalで、v3初回起動後もfixtureの設定・DB意味状態を保持する | source-build/ab9 substitution、preserved treeの1 byte変更、post-start semantic mismatchをfail。updater exit、stream drain、restart/terminal receipt、v3 startup probeで完了 | new v2.1.6 first-hop ReleaseAcceptance |
| `UPD-V216-LOCK` | actual legacy lock は success にせず、非 zero exit と legacy updater が `stderr` へ出す非空 error diagnostic を観測し、`data/config` を不変にする。managed tree 自動回復は要求しない | zero exit、空 diagnostic、preserved data の 1 byte 変更を fail。process exitとredirected stderr drainをcompletionにする | same acceptance。characterization only |
| `UPD-V3-LOCK` | current updater の preflight lock failure はmanaged tree、`data/config`、unmanaged fileをexactに無変更で保ち、persistent failure receiptを残し、restart/commitしない | preflight省略、stderr-only、任意のtree hash変更、canonical exe欠落をfail。pre/post exact tree hash、receipt、process exitで完了 | extend `UpdaterPackageSyncTests` / ProcessIntegration |
| `UPD-V3-POSTMUTATION` |少なくとも一つのmanaged pathをpromoteした後のdeterministic primary failureで、rollback成功時は旧managed tree全体をexactに復元し、`data/config` とunmanaged fileを不変にする | rollback無効化、delete-firstで旧fileを失う、mixed-generation treeをfail | same fixture。promotion barrier、primary failure、updater exit、tree hashで完了 |
| `UPD-V3-ROLLBACK-FAIL` | primary failureに加えてrollbackをdeterministicに失敗させた場合、success/restart/commitせず、primaryとrollbackの両failureをpersistent receiptへ残し、backup/work/journalを回復可能な形で保持する。fault除去後の既存recoveryがold/newいずれかのcomplete treeへ収束する | rollback failureを隠す、唯一のbackupをdelete、recursive rollback、success化をfail | same fixture。dual-fault receipt、retained recovery material、second recovery terminalで完了 |
| `UPD-NOTE` | public release note に fresh v3 + old `data/config` copy の意味を含める | exact prose test は作らず release artifact checklist で確認 | release documentation review |

`UPD-V216-LOCK` だけを characterization とする。public v2.1.6.0 first-hop support を正式終了した場合にだけ退役できる。legacy characterization を current updater の正しい rollback oracle に使わない。

### `SAFE-RELEASE-ARTIFACT-3000`

| Contract ID | Required observable outcome | Negative control / completion | Placement |
| --- | --- | --- | --- |
| `REL-V216-ID` | size/hash の両方が一致する zip だけを採用し、fallback しない | size-only/hash-only/source-build/ab9 substitution を個別に fail | extend `DistributionArtifactContractTests` + pinned metadata |
| `REL-CRITICAL-ROSTER` | mapped exact FQN が各一回だけ `Passed` | missing、duplicate、Skipped、Inconclusive、NotExecuted を個別に fail | extend `VerificationRunnerContractTests` + Full outcome gate |
| `REL-OPTIONAL-SKIP` | optional skip は exact FQN allowlist と非空 reason receipt がある時だけ許可 | category allow、unknown skip、空理由を fail | same runner contract |

semantic authority は下記全自動 Contract ID である。各 implementation unit は exact FQN mapping を handoff し、root が `U9` の Full 前に checked-in roster を凍結する。test rename は roster と Contract ID mapping を同じ変更で更新する。

- `UPD-V216-HAPPY`, `UPD-V216-LOCK`, `UPD-V3-LOCK`, `UPD-V3-POSTMUTATION`, `UPD-V3-ROLLBACK-FAIL`
- `REL-V216-ID`, `REL-CRITICAL-ROSTER`, `REL-OPTIONAL-SKIP`
- `START-ADMIT`, `START-DEFER`, `START-SHUTDOWN`
- `DB-OUTER-ROLLBACK`, `DB-RETRY`, `DB-STEP`, `DB-PRIMARY`, `DB-PARTIAL`
- `FAIL-LOG`, `FAIL-STARTUP`, `FAIL-DETAIL`, `FAIL-BULK`, `FAIL-RESTORE`, `FAIL-SHUTDOWN`
- `COMP-PREFLIGHT`, `COMP-PRECOMMIT`, `COMP-MANUAL`, `COMP-DURABLE`, `COMP-CLEANUP`, `COMP-SUCCESS`
- `ING-URL-INITIAL`, `ING-URL-HOPS`, `ING-EXTERNAL-LOCAL`, `ING-DROP-REPARSE`, `ING-DROP-CLEANUP`

### `SAFE-STARTUP-IMPORT-3000`

| Contract ID | Required observable outcome | Negative control / completion | Placement |
| --- | --- | --- | --- |
| `START-ADMIT` | initialization 中も request を queue し、UI/caller へ即時に制御を返す | readiness 未完で dispatcher sentinel が完了しない実装を fail | extend startup owner + workspace adapter tests |
| `START-DEFER` | readiness 前は download/persistence せず、ready 後に一度だけ開始 | pre-ready mutation、二重実行を counter で fail | readiness TCS、import-start TCS、operation Task |
| `START-SHUTDOWN` | readiness 待ちの producer/consumer が shutdown で terminal になり、late mutation しない |片側だけ cancel、ready 後 continuation を fail | cancellation、queue-idle、mutation counter |

positive delay や fixed sleep を正常完了条件にしない。既存の「確認 dialog 後に initialization lock を再確認して拒否する」test は、atomic admission/defer contract の test に置換する。

### `SAFE-DB-TX-3000`

| Contract ID | Required observable outcome | Negative control / completion | Placement |
| --- | --- | --- | --- |
| `DB-OUTER-ROLLBACK` | normalization 後の late fault で schema/version/digest/data を全 rollback | participant commit mutant で durable差分が残れば fail | extend `AppSchemaPreflightServiceTests` |
| `DB-RETRY` | failure 後、同じ DB の retry が重複・損失なく current preflight へ収束 | partial commit 前提の retry を fail | same DB reopen/retry |
| `DB-STEP` | `ROW` 継続、`DONE` 成功、それ以外 throw | non-ROW を break する mutant を fail | extend `SQLiteProviderRuntimeTests` または narrow raw-reader fixture |
| `DB-PRIMARY` | step と finalize/cleanup の dual failure では step error が authoritative | finalize exception で上書きする mutant を fail | same raw-reader owner |
| `DB-PARTIAL` |途中 failure の partial rows を dump/restore/publish しない | partial restore mutant を persisted state で fail | extend `PlaylistSchemaMigrationTests` |

failure injection に test-only public API や private reflection は使わない。実行可能 seam が不足する場合は issue-resolver を呼び、contract を弱めない。

### `SAFE-FAILURE-PROP-3000`

| Contract ID | Required observable outcome | Negative control / completion | Placement |
| --- | --- | --- | --- |
| `FAIL-LOG` | awaited non-generic logging が source fault を await caller へ伝播 | fault-consuming continuation mutant を fail | extend `TaskLoggingTests` |
| `FAIL-STARTUP` | playlist/schema repair fault が startup を fault し、ready/success/後続 mutation がない | log-only continuation を fail | startup owner / schema startup tests |
| `FAIL-DETAIL` | detail durable failure で live row、success event、`afterApply` を publishしない |各 side-effect counter mutant を fail | extend `PlaylistViewPipelineTests` |
| `FAIL-BULK` | bulk failure で全体 success、live apply、close、`afterApply` を行わない | first/late failure variants | extend `PlaylistSummaryBulkEditTests` |
| `FAIL-RESTORE` | restore failure で旧 tables/live state を維持し、success follow-up を行わない | publish-before-durable mutant | extend `PlaylistWorkspacePersistenceCommandTests` |
| `FAIL-SHUTDOWN` | settings/restore failure は close/shutdown authorization を出さない | catch-and-shutdown mutant | extend `SettingsWindowPresentationTests` |

failure UI は resource key/non-empty/semantic state を検証し、翻訳文の exact copy を固定しない。

### `SAFE-FILE-DB-COMP-3000`

| Contract ID | Required observable outcome | Negative control / completion | Placement |
| --- | --- | --- | --- |
| `COMP-PREFLIGHT` | immutable plan 完了まで FS/DB mutation なし。staging は destination-local sibling | source/tmp staging、mutable request reread を fail | file planning/package fixture |
| `COMP-PRECOMMIT` | durable receipt 前 failure は source/DB prior state を保持し、一 owner が compensation を一度だけ試す | source-first delete、duplicate rollback owner を fail | package full matrix |
| `COMP-MANUAL` | compensation failure で mutation を停止し、source/backup/staging と recovery paths を保持 | cleanup継続、recursive compensation、success を fail | package/folder fault injection |
| `COMP-DURABLE` | durable success 後は compensateせず、destination+DB authoritative | receipt 前 cleanup、commit 後 rollback を fail | durable receipt barrier |
| `COMP-CLEANUP` | post-commit cleanup failure は `CompletedWithCleanupFailure`、leftover保持、fresh retry禁止 | pendingへ戻す/全体rollbackを fail | source/backup cleanup faults |
| `COMP-SUCCESS` | success は destination+DB authoritative、cleanup対象残置なし、各 cleanup 一回 | double apply/cleanup を fail | persisted/file state + counters |

package が full matrix の canonical owner、folder は parity case、low-level file service は stage/backup primitive だけを検証する。DB contract を low-level fixture へ複製しない。

### `SAFE-INGRESS-3000`

| Contract ID | Required observable outcome | Negative control / completion | Placement |
| --- | --- | --- | --- |
| `ING-URL-INITIAL` | URL1/URL2 initial URI は HTTP(S) だけが gateway へ進む | file/ftp/custom の gateway/temp/browser/install counter > 0 を fail | extend `PlaylistUrlAcquisitionOwnershipTests` |
| `ING-URL-HOPS` | resolved/recursive の各 hop を再検証する | initial-only check、http→file、recursive unsafe hop を fail | same acquisition owner |
| `ING-EXTERNAL-LOCAL` | external sync は drive path、file URI、UNC を受理可能 | global validator/AppHttpClient restriction mutant を fail | extend `BmsPlaylistExternalLoadTests` |
| `ING-DROP-REPARSE` | stable/managed/transient の ancestor/source/descendant reparse で whole batch reject | stable early-return、safe itemだけenqueueを fail | extend `DroppedInstallIngressMaterializerTests` |
| `ING-DROP-CLEANUP` | mixed transient batch の partial ingress を一度だけ cleanup、original不変、requestなし | cleanup 0/2回、partial request を fail | same materializer fixture |

URL acquisition と external playlist sync は別 owner/allowlist のままにする。drop test は injected attributes で deterministic にし、OS privilege 依存 reparse fixture の skip だけに依存しない。

### Packet metadata and repository-fit decisions

上の Contract ID tables と次の metadata/coverage tables を合わせたものが承認済み packet の完全版である。一方だけを実装者へ渡してはならない。metadata row の change class、authority、public seam、allowed variation、lane、retired coverage は、そのpacket内の各Contract IDへ適用する。ただし `UPD-V216-LOCK` のcharacterization exceptionだけは個別記述を優先する。

| Packet | Change class | Precise authority | Public seam | Allowed variation | Required base-fail / negative-control evidence | Shared resource and lane | Retired assumption / coverage |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `SAFE-UPDATER-3000` | bugfix + legacy characterization | `D-01`–`D-04`、公開protocol-1/package/preserved-path contract、`devdocs/spec/portable-auto-update.md`、actual v2/current updater audit reproduction | sealed v2 updater process、current packaged updater process、persistent updater receipt | diagnostic copy、temp path、内部rollback順、budget内の所要時間 | current preflight lock、post-promotion primary failure、rollback second faultを別々にbase-fail。actual v2 lockはbase/head characterization | sealed app roots、owned PID lineage。ProcessIntegration / ReleaseAcceptance / Full | source-build/ab9 first-hop fallback、delete-first rollback、legacy behaviorをcurrent rollback oracleにするtest |
| `SAFE-RELEASE-ARTIFACT-3000` | release-gate hardening | `D-01`–`D-03`、zip size/hash、`devdocs/spec/testing-strategy.md`、rootのContract ID→FQN freeze decision | artifact preparation/seal、checked-in exact-FQN roster、TRX/result gate | canonical cache path、parser/receipt format、optional reason文面 | size/hash mutation、0/2件、各non-passed outcome、unknown/empty-reason skip | sealed distribution rootとsynthetic TRX temp root。ProcessIntegration + Full | source-build/latest artifact fallback、substring/latest-result match、category-wide skip allow |
| `SAFE-STARTUP-IMPORT-3000` | bugfix | `D-05`、`devdocs/spec/startup-initialization-flow.md`、`devdocs/spec/playlist-data-and-export-flow.md`、確認済みUI/semaphore cycle | startup readiness coordinator、external/recommended import command、shutdown | queue内部型、terminal result型、readiness後の公平順序。ただし同一queueの入力順を維持 | readiness未完dispatcher sentinel、pre-ready mutation、片側cancel/late mutation mutants | deterministic TCS/barrier、必要時TestUiDispatcherHost。Functional remainingまたは既存serial-state-b | initialization中reject/drop、UI semaphore sync wait、fixed-sleep watchdog-as-success |
| `SAFE-DB-TX-3000` | bugfix | `D-06`,`D-07`、SQLite ROW/DONE contract、startup/schema/playlist specs、nested commit/raw step audit | app schema repair gateway、borrowed connection core、raw reader、dump/restore | SQL順、transaction API、exception subtype/diagnostic、secondary error保持方法 | normalization後late fault、same-DB retry、non-DONE step、step+finalize dual fault、partial publish mutant | GUID temp SQLite/provider runtime。Functional remaining | participant commit、non-ROW=EOF、partial result success。outer matrixのfixture複製 |
| `SAFE-FAILURE-PROP-3000` | bugfix | `D-08`、startup/persistence/dialog lifecycle specs、non-generic logging audit | awaited task helper、startup Task、detail/bulk/restore/settings commands | logger format、localized presentation copy、exception/result mechanics | source exception identityと各success/live/afterApply/close/shutdown counterをbaseで失敗させる | fake durable ports、GUID DB、既存Settings/WPF scope。Functional remaining/serial-state-a/b | logging continuation success、fire-and-forget durable owner、failure後success publication、source/exact-copy tests |
| `SAFE-FILE-DB-COMP-3000` | safety hardening | `D-09`–`D-11`、File + DB failure-state contract、`devdocs/spec/library-mutation-boundary.md` | package/folder mutation command、DB durable receipt、terminal result | stage/backup名、immutable type/result mechanics、cleanup順。persistent journalは不可 | source-first delete、backupなしoverwrite、duplicate compensation owner、compensation failure後cleanup、post-commit rollback/fresh retry mutants | GUID fs/SQLite、injected mutation service。`remaining-bms-library` Functional | destructive move-before-DB、exceptionからcommit推測、recursive rollback、low-level fixtureへのDB matrix複製 |
| `SAFE-INGRESS-3000` | ingress/security bugfix | `D-12`,`D-13`、URI用途別authority、drop ingress spec、stable early-return audit | URL1/URL2 acquisition、external table load、drop materializer acquire | URI/path normalization、typed rejection、translated copy。TOCTOU handle traversalは不可 | initial-only scheme check、unsafe resolved hop、global local/UNC rejection、stable descendant bypass、partial/duplicate cleanup mutants | fake gateway/sink、isolated fs、injected attributes。Functional remaining | global AppHttpClient scheme restriction、unsafe browser fallback、stable-source passthrough、privilege依存skip-only coverage |

### Coverage ownership ledger

| Observable owner | Canonical Contract IDs | Canonical fixture / coverage decision | Completion signal | Duplication boundary |
| --- | --- | --- | --- | --- |
| public v2 identity/result gate | `REL-*` | extend `DistributionArtifactContractTests`, `VerificationRunnerContractTests`; new Full acceptance metadata/consumer | seal receipt、parsed result receipt | updater behavior fixtureでhash/TRX parserを再実装しない |
| updater behavior | `UPD-*` | extend `UpdaterPackageSyncTests`; new actual-v2 ReleaseAcceptance consumer | process exit、stream drain、persistent receipt、restart/recovery terminal | legacy characterizationとcurrent preflight/post-mutation/dual-faultを別caseにする |
| startup readiness | `START-*` | extend `StartupLibraryInitializationWorkflowOwnerTests`; workspace fixturesはthin adapter | readiness/import/shutdown Tasks、dispatcher sentinel | coordinator matrixをexternal/recommended fixtureへ複製しない |
| schema outer transaction | `DB-OUTER-*`, `DB-RETRY` | extend `AppSchemaPreflightServiceTests` | synchronous exception、reopen state、same-DB retry | `PlaylistSchemaMigrationTests`へouter rollback matrixを複製しない |
| raw SQLite protocol | `DB-STEP`, `DB-PRIMARY` | extend `SQLiteProviderRuntimeTests`またはnarrow raw-reader fixture | reader terminal/exception | restore fixtureはpartial-publicationだけを所有 |
| partial dump/restore | `DB-PARTIAL` | extend `PlaylistSchemaMigrationTests` | thrown operation + reopened DB/live generation | raw result-code matrixを複製しない |
| failure propagation | `FAIL-*` | ownerごとの既存Task/startup/detail/bulk/restore/settings fixtureをextend | returned Task/resultとnegative side-effect counters | DB rollback/file compensation state matrixをここへ置かない |
| file+DB compensation | `COMP-*` | package fixtureがfull matrix、folder fixtureがparity、low-level fixtureはstage/backup primitive | terminal result、durable receipt barrier、exact fs/DB state、once counters | package/folderで全matrixを二重実装しない |
| URL acquisition/local sync/drop | `ING-*` | URL ownership、external load、materializer fixtureをそれぞれextend | acquisition/load Task、synchronous acquire、gateway/sink/cleanup counters | URL1 scheme policyとexternal local-source acceptanceを分離し、transient既存coverageを複製しない |

### Packet handoff completeness

- worker promptにはPacket ID、該当Contract ID、expected outcome、allowed variation、canonical fixture/lane、base-fail/negative control、retired routeをすべて含める。
- workerは既存fixtureを検索し、各coverageを`extend / replace / new`のどれにしたか、shared resource、completion signal、exact FQN mappingをhandoffする。
- packet metadataまたはContract ID tableの一部を変更する必要がある場合はrootへ戻し、U0を`Needs replan`にする。
- `UPD-NOTE`だけはautomated source-copy assertionを持たず、public artifact checklistでsemantic reviewする。それ以外の自動Contract IDはrequired FQN rosterへ入れる。

## File + DB failure-state contract

| Failure point | DB state | Source | Destination / backup / stage | Terminal result and permitted follow-up |
| --- | --- | --- | --- | --- |
| preflight / stage preparation before live promotion | unchanged | retained | destination unchanged。失敗 stage は best-effort cleanup | explicit failure。fresh retry可 |
| promotion 後、durable DB receipt 前。compensation成功 | unchanged | retained | destination pre-state restored、stage/backup cleanupを一度だけ試行 | explicit failure。cleanup residueを明示した時だけ fresh retry可 |
| promotion 後、durable DB receipt 前。compensation失敗 | unchanged または DB owner が明示した prior state | retained | uncertain。backup/stageを保持 | `ManualRecoveryRequired(paths)`。全 mutation停止。自動retry禁止 |
| durable DB success 後、source/backup cleanup成功 | destinationへcommit済み | cleanup済み | destination authoritative、backup/stageなし | `Completed`。compensation禁止 |
| durable DB success 後、sourceまたはbackup cleanup失敗 | destinationへcommit済み | delete失敗なら duplicate として保持 | destination authoritative。failed leftovers保持 | `CompletedWithCleanupFailure(paths)`。fresh install扱い禁止、手動cleanup可 |
| durable DB success 後、notification/runtime projection failure | commit済み | durable receiptに従ってfinalize | destination authoritative |既存 post-commit failure contractを維持。DB rollback/compensation禁止 |

### Authoritative commit point

- `ApplyInstalledChartStorageTargets` / `ApplyLibraryMutationDeltaCore` 相当の durable owner が、transaction commit 後に typed durable receipt を返す。
- filesystem coordinator は exception の有無から commit 前後を推測しない。
- source delete、smart overwrite の `SkipSame` / `SkipOlderOrEqual` に伴う source delete、`delete_parent`、空 directory cleanup は durable receipt 後の finalize まで延期する。
- folder move は既存 destination を引き続き reject し、overwrite/merge を新設しない。
- compensation は batch coordinator だけが所有し、file helper、package helper、DB owner がそれぞれ rollback しない。

## Progress ledger

| Unit | Status | Owner | Depends on | Contract IDs | Evidence / next action |
| --- | --- | --- | --- | --- | --- |
| `U0` Plan and oracle freeze | Completed | root | none | all packets | plan-clarifier: questions none。7 packets frozen。implementation未開始 |
| `U1` Current updater safety | Verified | implementation-worker closed | U0 | `UPD-V3-LOCK`, `UPD-V3-POSTMUTATION`, `UPD-V3-ROLLBACK-FAIL` | commit `b13ab8fe`; integrated Quick artifact `tests-quick-20260831-102226`, 27 updater pass + approved reparse skip |
| `U2` Actual v2 first hop and release outcome gate | Verified | implementation-worker closed | U1 | `UPD-V216-*`, `UPD-NOTE`, `REL-*` | commit `1e9f576a`; actual receipt `artifacts/verification/v216-first-hop/v216-first-hop-acceptance.json`; integrated Quick `tests-quick-20260831-113927` 50/50 pass |
| `U3` Raw SQLite completion | Verified | implementation-worker closed | U0 | `DB-STEP`, `DB-PRIMARY`, `DB-PARTIAL` | commit `88cda4e8`; integrated Quick artifact `tests-quick-20260831-102226`, 10/10 pass |
| `U4` App schema transaction ownership | Verified | implementation-worker closed | U0 | `DB-OUTER-ROLLBACK`, `DB-RETRY` | commit `3a8cf6cc`; head Quick `tests-quick-20260831-105151` 26/26 pass; integrated Quick `tests-quick-20260831-113927` 50/50 pass |
| `U5` Startup readiness and import admission | Verified | implementation-worker closed | U0 | `START-*` | commit `e83127e8`; head Quick `tests-quick-20260831-124531` 77/77 pass; integrated Quick `tests-quick-20260831-125110` 154 pass + approved symlink skip |
| `U6` Failure propagation and success suppression | Verified | implementation-worker closed | U4, U5 | `FAIL-*` | commit `9cc94a51`; focused Quick `tests-quick-20260831-141114` 13/13 pass; U7/outcome gateとの統合 Quick `tests-quick-20260831-165123` 376 pass + approved 7 cross-volume skips |
| `U7` Package/folder file+DB boundary | Verified | implementation-worker + issue-resolver closed | U0 | `COMP-*` | commit `0ae8e615`; focused Quick `tests-quick-20260831-164409` 346 pass + approved 7 cross-volume skips; integrated `tests-quick-20260831-165123` |
| `U8` URI and drop ingress boundaries | Verified | implementation-worker closed | U0 | `ING-*` | commit `bb90d8bd`; head 77 pass + deterministic reparse coverage + privilege symlink skip `tests-quick-20260831-123039`; integrated `tests-quick-20260831-125110` |
| `U9` Integration, release qualification, static review | In progress | root + implementation-worker | U1–U8 | full roster | optional outcome gate `a2417f80`; roster cardinality `e4a39294`; format blocker `a4023804`; shared specs `1d8abf51`; actual-v2 Full receipt gate `ad237eb1`; exact roster Quick `tests-quick-20260831-232239` 31/31 pass; final Full、reviewが残る |

## Implementation units

### U1 — Current updater safety

Observable outcome:

- update 対象 managed path を `BackingUp`/live mutation より前に preflight し、既に排他 lock された path があれば canonical tree を変更せず明示 failure にする。
- mutation 後 rollback は「canonical new paths を一括 delete してから backup を順に戻す」route を退役する。backup entry は sibling staging へ復元してから replace/promote し、primary failure と rollback failure を receipt に残す。
- rollback成功時は旧 managed tree全体、`data/`、`config/`、unmanaged fileを保持し、restart/commit扱いにしない。
- rollback自体が失敗した時はsuccess/restart/commitせず、primary/rollbackの両failureと回復pathをpersistent receiptに残す。唯一のbackup、work、journalをcleanupせず、fault除去後の既存recoveryがcomplete old/new treeへ収束できる状態を保持する。
- 現行 journal/recovery、protocol 1、preserved path contract を維持する。preflight後の lock raceや真の電源断保証を追加で主張しない。

Writable paths:

- `BeMusicSeeker.Updater/Program.cs`
- `BeMusicSeeker.Updater/UpdaterFileSystem.cs`
- updater project 内の新しい narrowly scoped transaction/preflight helper（必要な場合）
- `BeMusicSeeker.Tests/UpdaterPackageSyncTests.cs`
- updater fixture helper の direct consumer
- `devdocs/spec/portable-auto-update.md`

Verification and handoff:

- `UPD-V3-LOCK` はmutation前lockとpre/post exact tree hash、`UPD-V3-POSTMUTATION` は最初のpromote後のprimary failure、`UPD-V3-ROLLBACK-FAIL` はrollback中のsecond faultを別々のdeterministic injectionで確認する。
- post-mutation rollback成功時は旧tree exact hash、dual-fault時はprimary/rollback receipt、retained backup/work/journal、fault除去後のrecovery convergenceを確認する。happy pathと既存journal recoveryもcollateralとして維持する。
- fixed sleep ではなく updater exit、failure receipt、owned child process drain を completion signal にする。
- handoff に exact FQN mapping、旧 delete-first route、rollback failure precedence、残る TOCTOU riskを記録する。

Replan if:

- protocol/minimum updater version の変更が必要になる。
- preflightを追加しても通常 lock failureで旧 treeを保持できない。
- current journalが新旧 topologyを区別できず recovery contractを変更する必要がある。

### U2 — Actual v2 first hop, release outcome gate, and recovery note

Observable outcome:

- checked-in metadataで public zip identityを固定し、指定 artifactがない、size/hash不一致なら release laneを fail closed にする。source build fallbackは設けない。
- actual v2 updaterの legacy argument/close protocolで v3 packageを適用する。通常 pathは v3 app起動、`data/config` semantic preservationまで確認する。
- legacy locked-file caseは `UPD-V216-LOCK` の characterizationだけを要求する。old updaterの非zero exitとredirected `stderr` の非空error diagnosticをfailure surfaceとして捕捉し、current updaterのready/decision handshakeを旧 updaterへ要求しない。
- `ab9d` current-updater baseline laneを別 identity/別 purposeとして残し、actual v2 laneと混同しない。
- FullのTRX/resultを解析し、checked-in required FQN rosterの cardinality/outcomeを検証する。optional skipはexact allowlist+reasonをreceiptへ残す。
- public v3 release noteへ `D-03` を追加する。

Writable paths:

- new `devdocs/acceptance/v216-first-hop/artifact.json`
- new `scripts/accept-v216-first-hop.ps1`
- `scripts/distribution-artifact.ps1`
- `scripts/verify-refactor.ps1`
- `scripts/verification-runner-contract.ps1`
- new `scripts/verification-test-outcomes.ps1`（責務分離が必要な場合）
- `BeMusicSeeker.Tests/DistributionArtifactContractTests.cs`
- `BeMusicSeeker.Tests/VerificationRunnerContractTests.cs`
- release acceptance の direct helper/test consumers
- `devdocs/spec/testing-strategy.md`
- `devdocs/spec/portable-auto-update.md`
- `release notes/v3.0.0.0 リリースノート.md`
- `BeMusicSeeker/Views/ReleaseNotesWindow.xaml` は同じ v3 noticeをin-appにも載せる場合だけ

Verification and handoff:

- artifact metadata自体に binary/source-build fallbackを含めない。cache/locationはvariationだがseal receiptを必須にする。
- synthetic TRXで0件、2件、各non-passed outcome、unknown optional skip、空理由を個別に落とす。
- actual v2 process lineageとredirected stdout/stderrをすべてdrainし、非zero exitと非空error diagnosticをreceiptへ残す。legacy edge後のmanaged treeを無理に正常化してcharacterizationを強化しない。
- public noteはhuman/structured release checklistで意味を確認し、exact prose/source copy testを作らない。

Replan if:

- sealed public artifactを取得できない、または identityが不一致である。
- actual v2 happy pathが失敗する。
- old updaterを動かすためにpackage format 1を破る必要がある。
- acceptanceが既存時間budget内でdeterministicに完了せず、固定待ちや単純timeout延長しか案がない。

### U3 — Raw SQLite completion and partial-result rejection

Observable outcome:

- `SQLiteCommandExtended.GetRawValuesAsString` と `ForEachRawValueAsString` 相当の共通 step handlingを `ROW/DONE/error` に分ける。
- result code/messageを保持してthrowし、step errorをfinalize/cleanup errorで上書きしない。
- raw reader consumerがpartial catalog/dumpを正常完了としてrestore/publishしない。

Writable paths:

- `BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs`
- narrowly scoped SQLite error/helper type（必要な場合）
- `BeMusicSeeker.Tests/SQLiteProviderRuntimeTests.cs`
- `BeMusicSeeker.Tests/PlaylistSchemaMigrationTests.cs`（`DB-PARTIAL`だけを所有）

`devdocs/spec/playlist-data-and-export-flow.md` は read-only authority とし、raw completion/partial-publication の spec delta を U9 へ handoff する。

Verification and handoff:

- deterministic step failure seamを使い、prepare-time failureだけを試すtestにしない。
- valid raw text/null coverageは維持し、non-ROW=EOF routeを退役する。
- finalizeの実行保証とprimary error precedenceをhandoffに含める。

Replan if:

- providerが候補failureをすべてprepare時に返し、test-only public API/private reflectionなしではstep failureを作れない。
- provider error mappingとwrapperのownershipが衝突する。

### U4 — App schema transaction ownership and retry convergence

Observable outcome:

- path overloadがsavepoint/commit/rollbackを所有し、borrowed connection coreはtransaction操作を行わない名前付き境界に分ける。`bool ownsTransaction` のようなambiguous flagで済ませない。
- gateway repairとdump restoreはparticipant coreを使い、playlist normalization内のnested `Commit()` routeを退役する。
- normalization後・outer commit前のfailureで全stateが元に戻り、同じDBを使う次回retryがcurrent schemaへ収束する。

Writable paths:

- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistPersistenceRepository.cs`
- direct schema callerのみ
- `BeMusicSeeker.Tests/AppSchemaPreflightServiceTests.cs`

`BeMusicSeeker.Tests/PlaylistSchemaMigrationTests.cs` は U3 が書込み ownership を持ち、U4 では read-only collateral とする。`devdocs/spec/startup-initialization-flow.md` と `devdocs/spec/playlist-data-and-export-flow.md` は read-only authority とし、transaction ownership の spec delta を U9 へ handoff する。

Verification and handoff:

- legacy playlist rowを含むDBへlate faultを注入し、reopen後のschema/version/digest/dataを独立に確認する。
- failure後に同じDBでrepairを再実行し、idempotent convergenceを確認する。
- rollback failureがprimary repair failureを上書きしない既存contractも監査する。

Replan if:

- 未知 caller が mid-repair commit へ依存している。
- production seamを広げなければlate faultを注入できない。
- standalone path wrapperのatomicityを変える必要がある。

### U5 — Startup readiness and import admission

Observable outcome:

- startup/playlist readinessを明示するTask-based coordinatorを単一 ownerとし、external URIとrecommended importをatomicにadmit/queueする。
- playlist initializationがmodel semaphoreを保持したまま同期UI invokeするroute、およびUI threadから同semaphoreを同期waitするrouteを退役する。
- startup-initialization specの「DB connection保持中にUI通知/大量runtime apply/別owner待機をしない」「required playlist readinessと導入可能readinessを分ける」を維持する。
- shutdownはreadiness waiter、queue producer、consumer/drainをcancel/terminalにし、late UI/DB mutationを起こさない。

Writable paths:

- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs`
- `BeMusicSeeker/Models/BMSPlaylist.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistRecommendedTableOwner.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistExternalSyncOwner.cs` のadmission direct consumerのみ
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.ExternalPlaylistImport.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.RecommendedImport.cs`
- composition/direct callers required by the coordinator API
- `BeMusicSeeker.Tests/StartupLibraryInitializationWorkflowOwnerTests.cs`
- `BeMusicSeeker.Tests/PlaylistWorkspaceExternalSourceTests.cs`
- `BeMusicSeeker.Tests/PlaylistWorkspaceActionWorkflowTests.cs`
- `BeMusicSeeker.Tests/PlaylistRecommendedTableOwnerTests.cs`（owner-level invariantが必要な場合）

`devdocs/spec/startup-initialization-flow.md` と `devdocs/spec/playlist-data-and-export-flow.md` は read-only authority とし、readiness/admission の spec delta を U9 へ handoff する。

Verification and handoff:

- playlist initをUI mutation直前でbarrierし、同一dispatcherからimportをsubmitする。dispatcher sentinel、readiness、queue summaryを独立signalにする。
- startup中rejectの旧test/routeを、admit/defer/shutdown contractへ置換する。
- TestUiDispatcherHostを使う場合は既存laneに置き、新しいforeground allowlistやfixed sleepを追加しない。

Replan if:

- score provider readinessがplaylist readinessと一致せず、単一coordinatorでは表現できない。
- recommended loader全体のasync API変更が必要になる。
- 実 MainWindow startup 以外では cycle を再現できず、新しい global test seam が必要になる。

### U6 — Failure propagation and success suppression

Depends on U4 and U5. Startup/schema pathsはU4/U5 handoff後に同じsnapshotへ統合する。

Observable outcome:

- non-generic awaited/coordinated `.Logging()` はsource taskのsuccess/fault/cancellationを保持する。意図的fire-and-forget observationをrepository全体で一括変更しない。
- startup continuationとschema repairはfaultをstartup ownerへ伝播する。
- detail edit、bulk edit、restore/backupはdurable completionをownerがawaitし、failure後にsuccess state/event、`afterApply`、dialog close、shutdownを行わない。
- restoreのtyped failure receiptがtask successで返る既存routeは、shellがsuccessと解釈できないterminal resultへ統一する。

Writable paths:

- `Ribbit/Util/Extensions/TaskEx.cs`
- `BeMusicSeeker/Models/Utils/TaskEx.cs` のdirect contract/helperのみ
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.DetailEditing.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistRestore.cs`
- `BeMusicSeeker/Views/PlaylistSummaryBulkEditDialog.cs`
- `BeMusicSeeker/Views/SettingsWindow.cs`
- direct completion/presentation owner only
- `BeMusicSeeker.Tests/TaskLoggingTests.cs`
- `BeMusicSeeker.Tests/StartupLibraryInitializationWorkflowOwnerTests.cs`
- `BeMusicSeeker.Tests/PlaylistViewPipelineTests.cs`
- `BeMusicSeeker.Tests/PlaylistSummaryBulkEditTests.cs`
- `BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs`
- `BeMusicSeeker.Tests/SettingsWindowPresentationTests.cs`

`devdocs/spec/startup-initialization-flow.md` と `devdocs/spec/playlist-data-and-export-flow.md` は read-only authority とし、failure propagation の spec delta を U9 へ handoff する。

Verification and handoff:

- routeごとにsource exceptionまたはtyped failure、success counter、live generation、close/shutdown counterを確認する。
- localized errorを追加する場合は全resource parityを同unitで所有する。
- current callsiteの機械的一括置換をせず、completion ownerがないfire-and-forgetは別途明示ownerを設ける。

Replan if:

- async-void shell routeのfailure contractをUI errorかfatal crashのどちらにするかauthorityが不足する。
- partial-success policyを変更する必要がある。
- U4/U5と同じ巨大fileを同時編集する必要がある。

### U7 — Package/folder filesystem + DB boundary

このunitは一 writerが `shared primitive -> package -> folder` の順に進める。途中handoffを残しても、同時writerには分割しない。

Observable outcome:

- File + DB failure-state contractをpackage installのcanonical matrixとして実装し、folder moveへ同じdurable receipt/finalize semanticsを適用する。
- staging/backup/promoteはdestination filesystem内で行い、sourceをdurable DB successまで保持する。
- `ApplyInstalledChartStorageTargets` / `ApplyLibraryMutationDeltaCore` がdurable commitとpost-commit notificationをtyped receiptで区別する。
- smart overwriteのsource delete、parent cleanup、folder source deleteをfinalizeへ移す。
- compensation failureで全後続mutationを停止し、pathを含むmanual-recovery resultを返す。rollback-of-rollbackは試さない。

Writable paths:

- `BeMusicSeeker/Models/Utils/IFileMutationService.cs`
- `BeMusicSeeker/Models/Utils/FileMutationKind.cs`
- `BeMusicSeeker/Models/Utils/ResilientFileMutationService.cs`
- `BeMusicSeeker/Models/Utils/LongPathFileSystem.cs`
- new narrowly scoped immutable plan/receipt type
- `BeMusicSeeker/Models/BmsLibraryInternal/LibraryFileOperationSynchronization.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs`
- `BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs`
- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/CatalogMutationOwner.cs` のinstall-row transaction seam
- `BeMusicSeeker/Models/BmsLibraryInternal/LibraryFolderMoveCoordinator.cs`
- `BeMusicSeeker/Models/BMSLibrary.LibraryFileOperationOwner.cs`
- `BeMusicSeeker/Models/BMSLibrary.LibraryFileOperationOwner.Merge.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryLibraryFileOperationsService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/AutoRenameBatchCoordinator.cs`
- package / pending / selected / merge / auto-rename のterminal result contractと、そのdirect workflow/UI consumer
- `BeMusicSeeker.Tests/ResilientFileMutationServiceTests.cs`
- `BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs`
- `BeMusicSeeker.Tests/BmsLibraryLibraryFileOperationsServiceTests.cs`
- `BeMusicSeeker.Tests/BmsLibraryFolderRenameRefreshTests.cs`
- `devdocs/spec/library-mutation-boundary.md`
- `devdocs/spec/install-estimation-current-logic.md`
- `devdocs/spec/path-length-and-io.md` はcross-volume/normal path contractを変更する場合だけ
- localization resourcesはnew resultをUI表示する場合だけ

Verification and handoff:

- GUID temp root/DB、injected file mutation service、SQLite durable failure triggerを使い、source/destination/backup/stage exact tree、DB rows、result kind、compensation/cleanup invocation countを確認する。
- package fixtureが全matrixを所有し、folder fixtureはdestination-exists reject、precommit compensation、postcommit cleanup、postcommit notification parityを検証する。
- queue/model lock内でI/O、cleanup、callback、task startを行わず、cleanup完了前にidleをpublishしない。

Replan if:

- durable ownerがcommit前後をtypedに公開できずexception推測が必要になる。
- batchではなくper-package durable commitが必要になる。
- crash/power-loss replay、auto-rename/merge、cross-volume atomicityまでscopeを広げる必要がある。
- failure resultへrecovery pathsを載せることが既存UI/APIでは不可能である。

#### Replan addendum — issue-resolver result (2026-08-31)

初回実装の静的調査で次の4点をblocking gapとして確認した。いずれも `D-09`–`D-11` から一意に決まり、新しいobservable semanticsの判断は追加しない。

1. `LibraryFileOperationSynchronization` はadmission/reservationと短いsnapshot lockを分離する。executor、DB apply、cleanup、notification、task startの間はmodel/collection/queue lockを保持せず、catalog/live publicationはfinalize後に行う。
2. receipt-aware routeをcanonicalにする。estimated/cleanup-onlyを含むinstall、merge、auto-renameをsource-first legacy routeへ残さない。per-package durable commit後、`ManualRecoveryRequired` でbatchを停止し、`CompletedWithCleanupFailure` はcommit済みとしてfresh retryしない。
3. package、pending、selected chart、folder、merge、auto-renameのpublic command/UI seamまでtyped terminal resultとrecovery pathを伝播する。legacy `void` / failure-listでmanual recovery情報を隠さない。
4. `IFileMutationService` のcopy default implementationを廃止し、`CopyFile` / `CopyDirectory` をresilient retry分類へ追加する。retry時はattemptが作成したpartial destinationだけを処理し、sourceを正規化・削除しない。

追加所有pathは上記Writable pathsに含める。`duplicate-file-check.md` の恒久仕様差分はU7からhandoffし、shared specと合わせてU9で統合する。

### U8 — URI scheme policy and drop descendant reparse rejection

Observable outcome:

- URL1/URL2 acquisitionのinitial normalized URI、gateway response final URI、HTML/JSON等から得るrecursive URIを各hopでHTTP(S)検証する。
- unsupported schemeは明示failureにし、BrowserFallbackへ意味変更しない。temp root、download gateway、browser/install sinkのinvocationは0にする。
- external playlist syncのdrive path、file URI、relative local data、UNC/SMB routeを維持する。
- `DroppedInstallIngressMaterializer.Acquire` はstable sourceの早期return前にもdirectoryを再帰検査し、batchの全source/ancestor/descendant reparseをrejectする。
- rejection後はrequest/queue/copy/installなし、original不変、partial managed ingress cleanup一回とする。

Writable paths:

- `BeMusicSeeker/Models/PlaylistUrlAcquisitionWorkflow.cs`
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs` のdirect result handlingのみ
- `BeMusicSeeker/ViewModels/DroppedInstallIngressMaterializer.cs`
- URL/drop direct helper only
- `BeMusicSeeker.Tests/PlaylistUrlAcquisitionOwnershipTests.cs`
- `BeMusicSeeker.Tests/BmsPlaylistExternalLoadTests.cs`
- `BeMusicSeeker.Tests/PlaylistWorkspaceExternalSourceTests.cs` はadapter regressionが必要な場合だけ
- `BeMusicSeeker.Tests/DroppedInstallIngressMaterializerTests.cs`
- `devdocs/spec/custom-table-view.md`
- `devdocs/spec/drop-install-ingress.md`

`devdocs/spec/playlist-data-and-export-flow.md` は read-only authority とし、external local/file/UNC invariant の spec delta を U9 へ handoff する。

Verification and handoff:

- `http`, `https`, `file`, `ftp`, custom、http→unsafe resolved hopをfake invocation counterで検証する。
- UNC testはnetwork availabilityに依存せずpath/loader boundaryを検証し、実SMB接続を禁止するvalidationを追加していないことを確認する。
- stable nested directory/fileを通常作成し、injected attributesでdescendantだけをreparseに見せるdeterministic testを追加する。privilege依存symlink skipだけをoracleにしない。

Replan if:

- unsupported schemeをbrowser-openする新要件が生じる。
- global AppHttpClient制限なしではpolicyを実装できない。
- handle-based traversal/完全TOCTOU保証、archive/JSON budgetまで要求される。

### U9 — Integration, release qualification, and static review

Tasks:

1. 全 worker handoffのContract ID→exact FQN mappingをreviewし、checked-in required rosterをfreezeする。
2. unitごとのspec更新、退役route、public release note、resource parity、unexpected writable pathを監査する。
3. integrated focused Quickを実行する。
4. final integrated snapshotで、canonical Functional phaseとpublish/updater/release gateを含むFullを一回実行し、actual v2 first-hop receipt、required FQN outcomes、optional skip reasonsを保存する。同じsnapshotでstandalone Functionalを先に重ねない。
5. full outcome gate自体がすべてのTRX/receiptを解析したことを確認する。既存format failureをruntime safety findingとは別に修正し、release sign-off前にcleanにする。
6. implementation agentsを閉じ、snapshotをfreezeして`repo-static-review`へPacket、base/head、diff、base-red/negative controls、verificationを渡す。
7. P0/P1/acceptance-direct P2を修正した場合はfocused Quickと必要なintegration laneを再実行し、fresh reviewerへ修正snapshotを渡す。snapshotが変わった場合だけFullを再実行する。

Expected writable paths:

- all unit specs and tests listed above
- `devdocs/spec/startup-initialization-flow.md`（U4/U5/U6 handoffを一度に統合するexclusive writer）
- `devdocs/spec/playlist-data-and-export-flow.md`（U3/U4/U5/U6/U8 handoffを一度に統合するexclusive writer）
- checked-in required-FQN/optional-skip roster under `devdocs/acceptance/` or the runner contract's existing canonical location
- this plan's progress/evidence sections
- 既存 format failure の直接原因だけ。無関係な format cleanup は行わない

Focused Quick filters:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~UpdaterPackageSyncTests|FullyQualifiedName~DistributionArtifactContractTests|FullyQualifiedName~VerificationRunnerContractTests'

pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~SQLiteProviderRuntimeTests|FullyQualifiedName~AppSchemaPreflightServiceTests|FullyQualifiedName~PlaylistSchemaMigrationTests'

pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~StartupLibraryInitializationWorkflowOwnerTests|FullyQualifiedName~PlaylistWorkspaceExternalSourceTests|FullyQualifiedName~PlaylistWorkspaceActionWorkflowTests|FullyQualifiedName~TaskLoggingTests|FullyQualifiedName~PlaylistViewPipelineTests|FullyQualifiedName~PlaylistSummaryBulkEditTests|FullyQualifiedName~PlaylistWorkspacePersistenceCommandTests|FullyQualifiedName~SettingsWindowPresentationTests'

pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ResilientFileMutationServiceTests|FullyQualifiedName~BmsLibraryPackageInstallServiceTests|FullyQualifiedName~BmsLibraryLibraryFileOperationsServiceTests|FullyQualifiedName~BmsLibraryFolderRenameRefreshTests'

pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~PlaylistUrlAcquisitionOwnershipTests|FullyQualifiedName~BmsPlaylistExternalLoadTests|FullyQualifiedName~DroppedInstallIngressMaterializerTests'
```

Integrated lanes:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

Full 内の Functional phase が 180 秒を超えて成功した場合は実 elapsed を report する。300 秒 hard budget、timeout retry、classification は `devdocs/spec/testing-strategy.md` に従い、timeout 延長だけで症状を隠さない。診断目的でstandalone Functionalが必要になった場合はpre-finalの別snapshotであることと理由をprogress logへ記録し、final acceptanceの一回に数えない。

## Safe execution order and parallel boundaries

```text
U0 packet freeze
 ├─ U1 current updater ──────────────> U2 actual v2/release gate ─┐
 ├─ U3 raw SQLite ────────┐                                      │
 ├─ U4 DB transaction ────┴─> U6 failure propagation ────────────┤
 ├─ U5 startup readiness ────> U6 startup integration ───────────┤
 ├─ U7 primitive -> package -> folder (one writer, serial) ──────┤
 └─ U8 URI/drop ingress ─────────────────────────────────────────┘
                                                                  v
                                                        U9 integration/review
```

- 最初の安全な二並列は `U1` と `U3`。
- `U4` と `U5` はproduction pathが概ね分かれ、shared specsをread-onlyにしたため並列可だが、`U6`より先に両方完了させる。
- `U5` と startup 部分の `U6` は `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs` を共有するため同時 write しない。
- `U4` と schema部分の `U6` はschema fixtureを共有するため直列にする。
- `U7` の primitive/package/folder は `BeMusicSeeker/Models/BMSLibrary.cs` と file mutation API を共有するため一 writer で直列にする。
- `U8` は他 unit と path が分離している時の二本目の worker 候補。`BeMusicSeeker.Tests/PlaylistWorkspaceExternalSourceTests.cs` を U5 が所有中は触らない。
- `U2` はU1のbehaviorとexact FQN handoff後に開始する。
- `devdocs/spec/startup-initialization-flow.md` と `devdocs/spec/playlist-data-and-export-flow.md` は U9 だけが書く。U3/U4/U5/U6/U8 はauthorityとして読むだけにし、unitごとのspec deltaをhandoffする。

## Backlog after the initial implementation

以下は初回 plan の release blocker へ昇格させず、独立 issue/plan として扱う。現在の unit で直接必要になった場合はscopeを黙って広げずreplanする。

### Recommended existing issues

- physical DB corruptionを検出した際に、DBとsidecarを名前変更して保持し、明示確認または契約済みpolicyで空DB再構築へ進むrecovery route。
- 匿名化した代表的な actual v2 DB、large fixture、WAL/hot-journal、header/interior-page corruption の release qualification。
- settings provider保存失敗、unknown future schema拒否、root playlist multi-store failureのfailure contract。
- LR2 backup/cleanupのpartial failureと、[LR2 song.db 同期診断の監査メモ](../memo/lr2-song-db-sync-diagnostic-audit-note.md) に記録した未収束Completed問題。
- production manifest取得→HTTP download→hash/size→shutdown→updaterを通すloopback end-to-end acceptance。
- updater actual-process interruption orderingと、journal recovery二回目のidempotence。

### Rare / provisioned edge cases

- updaterのVM hard power-off、alternate volume、junction/mount point、portable app on secondary drive、network filesystem support matrix。
- package/folder operationのpersistent crash recovery、full TOCTOU protection、auto-rename/merge/cross-volumeへのcontract拡張。
- archive/JSON resource budget、巨大/悪意あるplaylist payload。
- Everything実service、external encoder、production CDN/GitHub asset canary。

### Explicitly no action

- 空DB初回scanの完了marker追加。既定設定ではfile diff retryが収束させるというdecisionを維持する。別の再現で収束しないことが確認された場合だけ新規issueにする。

## Replan triggers applying to every unit

- Packetのauthority、expected outcome、allowed variationを変えなければ実装できない。
- testをgreenにするためfailureをsuccess receiptへ変換、fallback、skip、timeout延長、固定sleepが必要になる。
- public v2 artifact identityが得られない、またはactual first hopが通常条件で失敗する。
- model lock/DB transaction/operation gateを保持したUI wait、sync-over-async、非event-handler `async void` が必要になる。
- worker間で同じ巨大file、shared schema/fixture、persistent stateを同時所有する必要がある。
- file+DB unitでdurable commit pointをtypedに区別できない、またはcompensation failure後も安全に停止できない。
- userがlegacy locked-file auto-recovery、power-loss atomicity、automatic replay、unsupported URI browser fallbackなど、明示的にscope外とした保証を要求する。
- final Fullのrequired FQNがmissing/non-passed、unexpected skip、artifact seal failure、または既存format failureを含む。

## Progress log template

実装開始後、各handoffまたはreviewのたびに追記する。

| Date | Unit | Transition | Owner / reviewer | Evidence | Remaining risk / next action |
| --- | --- | --- | --- | --- | --- |
| 2026-08-31 | U0 | Pending -> Completed | root + plan-clarifier + test-contract-designer | decisions closed、7 packets frozen、authority gap none | U1/U3のbase-failから開始 |
| 2026-08-31 | U0 | QA remediation | two independent plan QA sessions | updater post-mutation/dual-fault contracts、legacy failure surface、full packet metadata、exclusive shared-spec ownership、single final Fullを反映 | prose/link/whitespace validation後にU1/U3へhandoff可能 |
| 2026-08-31 | U3 | Pending -> Verified | implementation-worker + root | base-red `tests-quick-20260831-094841`; head 10/10 `tests-quick-20260831-100427`; integrated 10/10 `tests-quick-20260831-102226`; commit `88cda4e8` | U9へraw completion/partial-publicationのspec deltaをhandoff済み |
| 2026-08-31 | U1 | Pending -> Verified | implementation-worker + root | preflight/post-mutation/dual-fault negative controls; head 27 pass + approved skip `tests-quick-20260831-101855`; integrated同結果 `tests-quick-20260831-102226`; commit `b13ab8fe` | U2がactual v2 first-hopとrelease gateを所有 |
| 2026-08-31 | U4 | Pending -> Verified | implementation-worker + root | base-red `tests-quick-20260831-103335`; commit mutant negative control `tests-quick-20260831-104916`; head 26/26 `tests-quick-20260831-105151`; integrated 50/50 `tests-quick-20260831-113927`; commit `3a8cf6cc` | U6へtransaction owner/failure propagation、U9へstartup/playlist spec deltaをhandoff |
| 2026-08-31 | U2 | Pending -> Verified | implementation-worker + root | exact public artifact 11,260,709 bytes / SHA-256 `C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2`; head 24/24 `tests-quick-20260831-113439`; integrated 50/50 `tests-quick-20260831-113927`; cache-path normalization後 identity 2/2 `tests-quick-20260831-114320`; actual happy/lock receipt `artifacts/verification/v216-first-hop/v216-first-hop-acceptance.json`; commit `1e9f576a` | U9でcomplete exact-FQN roster/optional allowlistをfreezeし、final Fullへ統合 |
| 2026-08-31 | U5 | Pending -> Verified | implementation-worker + root | readiness negative control、head 77/77 `tests-quick-20260831-124531`; integrated 154 pass + approved privilege symlink skip `tests-quick-20260831-125110`; commit `e83127e8` | U6でstartup failure時のpending drain終端とfailure propagationを統合確認、U9へreadiness/FIFO/shutdown spec deltaをhandoff |
| 2026-08-31 | U8 | Pending -> Verified | implementation-worker + root | URL base-red `tests-quick-20260831-115337`; drop base-red `tests-quick-20260831-115448`; head 77 pass + privilege symlink skip `tests-quick-20260831-123039`; integrated同結果を含む `tests-quick-20260831-125110`; commit `bb90d8bd` | U9へexternal drive/file/relative/UNC preservation spec deltaをhandoff。handle-based TOCTOUは対象外 |
| 2026-08-31 | U6 | Implemented -> Verified | implementation-worker + root | focused 13/13 `tests-quick-20260831-141114`; commit `9cc94a51`; U7/outcome gateとの統合 376 pass + approved 7 cross-volume skips `tests-quick-20260831-165123` | failureはsource identityを保って伝播し、detail/bulk/restore/settings/startupのsuccess side effectを抑止。恒久仕様はU9で統合済み |
| 2026-08-31 | U7 | Replanned -> Verified | implementation-worker + issue-resolver + root | 4 blocking gapをaddendumどおり解消; focused 346 pass + approved 7 cross-volume skips `tests-quick-20260831-164409`; integrated `tests-quick-20260831-165123`; commit `0ae8e615` | persistent crash journalとcross-volume atomicityは明示対象外。canonical receipt routeとmanual recovery pathをU9仕様へ統合済み |
| 2026-08-31 | U9 | Pending -> In progress | root + implementation-workers | optional outcome parser `a2417f80`; exact cardinality tests 4/4 `tests-quick-20260831-170358` / commit `e4a39294`; prior Full format blockerをwhitespace-only修正 `a4023804`; shared specs `1d8abf51`; actual-v2 receipt gate 18/18 `tests-quick-20260831-171710` / commit `ad237eb1`; exact MSTest roster 31/31 `tests-quick-20260831-232239` | actual v2の2 JSON resultsを含むfinal Fullと、frozen static reviewが残る |

## Final evidence checklist

- [x] public v2 artifact seal receipt
- [x] actual v2 happy first-hop receipt
- [x] actual v2 locked-file characterization receipt
- [ ] current updater preflight-lock receipt
- [ ] current updater post-mutation rollback exact-tree receipt
- [ ] current updater rollback-second-fault and recovery receipt
- [x] Contract ID -> exact FQN checked-in roster
- [x] optional skip allowlist and non-empty reason receipts
- [x] all focused Quick artifacts
- [ ] final Full artifact including its canonical Functional and update/distribution phases
- [ ] Functional phase elapsed time（180秒超の場合は明示）
- [x] public release note manual-recovery review
- [ ] `git diff --check`
- [ ] frozen snapshot static review with no blocking finding
- [ ] fresh review after any blocking-finding remediation
