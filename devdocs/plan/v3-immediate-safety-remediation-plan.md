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
15. `D-15 Exclusive file-mutation lease`: file mutation admission は短い sequence monitor 内で LR2 と他の file mutation の双方に対する排他的 logical lease を原子的に取得し、monitor は直ちに解放する。lease は immutable snapshot 作成後の executor、DB durable apply、compensation/cleanup、内部 finalization まで保持するが、dialog、UI scheduler、property/event subscriber、task start、別 owner callback の前に解放する。nested internal apply は ambient reentrancy ではなく明示 capability / reserved route だけを使う。
16. `D-16 LR2 sync`: LR2 同期が未収束でも Completed になり得る件は本 plan で変更せず、[LR2 song.db 同期診断の監査メモ](../memo/lr2-song-db-sync-diagnostic-audit-note.md) を後続調査の正本とする。
17. `D-17 Empty DB first scan`: 完了 marker 不在は、既定設定では file diff による retry が成功すれば収束するため、issue/backlog として扱わない。

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
| `REL-V216-CACHE-MISS` | valid checked-in metadata と absent canonical cache は failure にせず、指定 exact HTTPS URL から一時 file へ一度だけ取得し、固定 size/hash 検証後に canonical cache へ公開する | cache miss の failure 化、別 URL、途中 bytes の canonical 公開、取得物の検証省略を fail | Full `v216-cache-preparation` phase |
| `REL-V216-CACHE-HIT` | valid canonical cache は再取得せず、固定 size/hash を検証してそのまま利用する | valid cache の不要 download、size/hash 検証省略を fail | same preparation phase |
| `REL-V216-CACHE-FAIL-CLOSED` | metadata missing/invalid、既存 cache mismatch、download failure は fail closed とし、fallback、repair、retry を行わない | source/latest/ab9 fallback、既存 corrupt cache の上書き、自動 retry を fail | same preparation phase |
| `REL-CRITICAL-ROSTER` | mapped exact FQN が各一回だけ `Passed` | missing、duplicate、Skipped、Inconclusive、NotExecuted を個別に fail | extend `VerificationRunnerContractTests` + Full outcome gate |
| `REL-OPTIONAL-SKIP` | optional skip は exact FQN allowlist と非空 reason receipt がある時だけ許可 | category allow、unknown skip、空理由を fail | same runner contract |

semantic authority は下記全自動 Contract ID である。各 implementation unit は exact FQN mapping を handoff し、root が `U9` の Full 前に checked-in roster を凍結する。test rename は roster と Contract ID mapping を同じ変更で更新する。

- `UPD-V216-HAPPY`, `UPD-V216-LOCK`, `UPD-V3-LOCK`, `UPD-V3-POSTMUTATION`, `UPD-V3-ROLLBACK-FAIL`
- `REL-V216-ID`, `REL-V216-CACHE-MISS`, `REL-V216-CACHE-HIT`, `REL-V216-CACHE-FAIL-CLOSED`, `REL-CRITICAL-ROSTER`, `REL-OPTIONAL-SKIP`
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
| `U9` Integration, release qualification, static review | Replanned | root + implementation-worker | U1–U8 | full roster + `D-15` | first Full shutdown blockerを`cde646a2`で修正。second Full `tests-full-20260901-001311` のstale testsを`74c54918`/`d434427c`で修正。LR2 stale contract調査で全file routeのlong-lived monitor/external-effect P1を確認し、packet `D15-EXCLUSIVE-FILE-MUTATION-20260901`をfreeze。D-15 coherent unit、再Full、reviewが残る |

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

- checked-in metadataで public zip identityとexact HTTPS download URLを固定する。`ProcessIntegration` より前の単一 `v216-cache-preparation` phaseが cache を確認し、valid cacheはhitとしてそのまま使い、canonical cacheが無い場合だけ指定URLから同一directoryのtemporary fileへbounded downloadする。固定 size/hashの検証後にatomic publishするため、cache miss自体はfailureにしない。metadata missing/invalid、既存cache mismatch、download failure、取得物のmismatchはrelease laneをfail closedにする。source build/latest/ab9 fallback、repair、retryは設けない。
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

- artifact metadata自体に binary/source-build/latest/ab9 fallbackを含めない。cache miss時のdownload URLはmetadataに保持し、exact HTTPSを検証する。cache/locationはvariationだが、temporary downloadからの固定size/hash検証とatomic canonical publishを必須にする。
- synthetic TRXで0件、2件、各non-passed outcome、unknown optional skip、空理由を個別に落とす。
- actual v2 process lineageとredirected stdout/stderrをすべてdrainし、非zero exitと非空error diagnosticをreceiptへ残す。legacy edge後のmanaged treeを無理に正常化してcharacterizationを強化しない。
- public noteはhuman/structured release checklistで意味を確認し、exact prose/source copy testを作らない。

Replan if:

- metadataを読み取れない、既存cacheが不一致、または指定URLからのdownload / size/hash検証 / atomic publishが通常budget内で完了しない。
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

#### Replan addendum — exclusive logical mutation lease (2026-09-01)

second Full `artifacts/verification/tests-full-20260901-001311` の stale
`Lr2SynchronizationArchitectureTests` failureを起点に、folder move、auto-rename、duplicate mergeの
scope lifetimeを静的監査した。`EnterMutationReservation` は短いadmission用の
`mutationSequenceGate` monitorと`MutationInProgress` reservationを一つのcompositeとして返し、
filesystem/DB/cleanupだけでなく、dialog、`Dispatcher.Invoke`、property subscriber、task startまで
monitorを保持していた。他方、`TryBeginMutation`は既存`MutationInProgress`を拒否しないため、monitorだけを
早期解放するとfile mutation同士の排他性を失う。これは`D-09`–`D-11`の実装詳細ではなく、
deadlockと中間状態競合を同時に防ぐためのrelease-blocking P1である。

Root decision:

- `D-15`を採用する。短いsequence monitor内でexclusive logical leaseを原子的に取得し、monitorは
  admission直後に解放する。logical leaseはLR2と別file mutationをcommand terminalizationまで拒否する。
- folder move、auto-rename、duplicate mergeのsnapshot scopeはinitialized-min read、pending-install write、
  BMS-files writeのcanonical順だけを保持し、immutable plan/delta完成後、executor前に逆順で解放する。
  package/legacy/init/LR2 preparation routeは必要最小限のroute-specific lockを短く保持してよいが、
  filesystem executor、DB apply、cleanupへ入る前にsnapshot/model/package/collection lockをすべて解放する。
- leaseはfilesystem executor、DB durable apply、compensation/cleanup、external effectを含まない内部
  finalizationまで保持する。dialog、UI scheduling、`PropertyChanged`/public subscriber、normal refresh、
  task start、別 owner callbackはtyped deferred effectとして返し、lease解放後に一度だけ実行する。
- nested DB/catalog applyはsame-thread monitor reentrancyやambient stateで許可せず、明示的な
  `...UnderExistingReservation` capability/routeでのみ参加する。
- `ManualRecoveryRequired`、`CompletedWithCleanupFailure`、source retention、compensation exactly-once、
  durable receiptの意味は変更しない。failureをsuccessに変えるfallbackや新しいpersistent stateは追加しない。
- `devdocs/spec/library-mutation-boundary.md` の「admission/reservationもexecutor前に解放する」という文言は
  unsafeな誤記として、短いmonitor/snapshot lockとlogical leaseのlifetimeを区別する契約へ修正する。
- alternativeとしてlogical reservationもexecutor前に解放する案は、LR2/file mutationが中間filesystem/DB
  stateへ同時進入できるためrejectする。

Implementation unit:

- foundation、folder move、auto-rename、duplicate merge、package install、invalid-extension rename、
  library/pending chart removal、installation-directory repair、startup leap-year timestamp repair、
  LR2 preparation custom-folder output、regular-folder failure dialog ordering、spec/testを一つのcoherent unitとして
  一writerが所有する。foundation-onlyやambient compatibility shimだけのcommitは、旧long-lived scopeまたは
  non-exclusive routeを残すため作らない。
- `FileDbMutationBoundary` はcleanup後のdeferred post-commit effectをtypedに返せない現状だけを最小拡張する。
  executorへmodel/UI ownerを移さず、command ownerがlease解放後のeffectを所有する。
- expected production pathsは`LibraryFileOperationSynchronization.cs`、
  `LibraryFileOperationMutationBoundary.cs`、`BMSLibrary.Lr2SynchronizationOwner.cs`、
  `BMSLibrary.LibraryFileOperationOwner*.cs`、`LibraryFolderMoveCoordinator.cs`、
  `AutoRenameBatchCoordinator.cs`、`InvalidExtensionRenameCoordinator.cs`、
  `BmsLibraryLibraryFileOperationsService.cs`、`BmsLibraryPackageInstallService.cs`、
  `BMSLibrary.PackageInstall.cs`、`BmsLibraryInitializationService.cs`、
  `Lr2SongDbSyncRequestCoordinator.cs`、`BMSPlaylist.cs`、`FileDbMutationBoundary.cs`、
  関連する`BMSLibrary.cs`、`RegularChartListOwner.cs`。explicit capabilityをruntime portへ通す必要がある場合だけ
  `Lr2SongDbSyncWorkflowOwner.cs`を含める。`PackageLifecycleOwner.cs`は既存collection deferralをlease外で
  開始・flushできない場合だけ含める。実装調査で不要と確認したpathは触らない。
- Test Contract Packet `D15-EXCLUSIVE-FILE-MUTATION-20260901`を承認する。Contract IDは
  `D15-ADM`、`D15-SNAP`、`D15-LIFE`、`D15-FOLDER`、`D15-AUTO`、`D15-MERGE`、`D15-CAP`、
  `D15-EXEC`、`D15-REG`、`D15-PKG`、`D15-PKG-SNAP`、`D15-LEG-RENAME`、`D15-LEG-REMOVE`、
  `D15-LEG-FIX`、`D15-INIT-REPAIR`、`D15-LR2-PREP`。conditional packet
  `FULL-LR2-SCOPE-20260901`は`D-15`と本packetでcloseする。既存required 33 FQN rosterは変更しない。
- 書込み並列化はしない。shared synchronization API、巨大owner、fixtureが重なるため最大worker数は1。

Done when:

- sequence monitorはadmission callbackから戻る前に解放される一方、別file mutationとLR2はlogical lease中に
  決定的に拒否/待機し、terminalization後に再度admitできる。
- executor、DB apply、cleanup中にsnapshot/model/collection lockは0で、cleanup完了前にleaseを解放しない。
- folder、auto-rename、merge、package、legacy chart command、startup repair、LR2 preparationの
  dialog/UI/event/subscriber/task-start/other-owner callbackはleaseとsnapshot lockが0の状態で一度だけ実行され、
  callbackからのreentryがmonitor deadlockを起こさない。
- normal/pending file command、startup repair、LR2 preparationも通常のpublic nested admissionではなく、
  outer leaseが発行した明示capabilityだけで内部DB/catalog/file participantへ参加する。
- regular folder renameのouter operation gateはfailure dialog flush前に解放される。
- focused Quick、変更snapshotのFull、frozen static reviewが成功する。

Replan if:

- exclusive leaseとLR2 reservationをexplicit capabilityのまま分離できず、ambient reentrancyが必要になる。
- deferred external effectへ移すためにdurable/cleanup receipt semanticsを変更する必要がある。
- public subscriber/task-startをlease内に残さなければ既存observable orderを維持できない。
- 全file routeを同じexclusive lease contractで閉じられず、shared APIのambient互換shimが必要になる。

#### D-15 implementation checkpoint and corrective slices (2026-09-01)

最初のcoherent implementation attemptはfoundationと全routeの接続を一つのworktree snapshotへ作成し、
focused Quick `artifacts/verification/tests-quick-20260901-032338` で151 pass / 6 approved
cross-volume skip / 0 failまで到達した。ただしfresh static checkpoint auditで、test対象が4 fixtureに限られ、
次のrelease-blocking P1とpacket coverage gapが残ることを確認したため、このsnapshotはcommitしない。

- package auto installのdurable DB apply、installed-target internal finalizationがlease解放後へ漏れ、
  estimated installはpackage execution gateをfilesystem/DB/cleanup中も保持している。
- advanced pending cleanup-only routeはouter leaseを取得せず、package effect chainは先行effect例外で後続
  catalog completionを抑止する。
- merge receiptはdeferred maintenance前にfactsを確定し、post-releaseにunsafe snapshotを取得する。
- legacy rename/remove/fixはpreflight confirmationまたはimmutable snapshot/revalidationが不足し、
  dialog/live stateをlease内へ残す経路がある。
- startup timestamp repairはcandidate identityの再検証が不足し、null file mutation serviceをsuccess扱いする。
- LR2 capability runtimeにlegacy fallback、LR2 ownerにno-op sequence compatibility surfaceが残る。
- direct semantic testは`D15-EXEC`の大半と`D15-ADM`/`D15-SNAP`/`D15-CAP`の一部だけで、
  `D15-LIFE`、route-level merge/regular/legacy/LR2 preparation等は未検証。base-red
  `tests-quick-20260901-025754`はstale expectationであり、approved negative controlの代替にはしない。
- specはatomic admission順を誤って記述し、D-15 verification map、regular gate、startup/LR2 route inventoryが不足する。

Correction order:

1. Package lifecycle sliceを単独で修正する。DB/internal finalizationはouter capability内、publicationだけを
   deferredにし、estimated gateをexecutor前に解放、cleanup-onlyへleaseを追加、effect chainをfailure-isolatedにする。
2. package snapshotがgreenになった後、pathが重ならない範囲でmerge、legacy command、initialization、
   LR2/API cleanupを最大2 writerで修正してよい。`BMSLibrary.cs`またはshared synchronization APIが重なる場合は
   並列化しない。
3. foundation、folder/auto/merge/regular、package/pending、startup/LR2の4 test sliceで全16 Contract IDを
   direct semantic oracleまたはapproved targeted mutantへ対応付ける。source/private reflectionだけでlock/leaseを
   証明しない。
4. 全slice統合後にfocused Quickを一回通し、D-15 snapshotを一commitとして確定する。partial foundationや
   ambient compatibility surfaceを残す中間commitは作らない。

#### D-15 corrective checkpoint 2 (2026-09-01)

Package sliceは、estimated gateのexecutor前解放、auto durable applyとinstalled-target finalizationの
outer capability内実行、advanced cleanup-only routeのouter lease、failure-isolated effect chainを実装した。
targeted mutant `artifacts/verification/tests-quick-20260901-052603` とhead
`artifacts/verification/tests-quick-20260901-052742`（113 pass / 2 approved skip / 0 fail）を証拠とする。
Merge sliceはimmutable maintenance input、lease解放後effect、effect後receipt、failure/reentry oracleを実装し、
head `artifacts/verification/tests-quick-20260901-054937` で4/4 passとした。legacy統合fixtureは
`artifacts/verification/tests-quick-20260901-062221` で164 pass / 1 approved cross-volume skip / 0 failだが、
これはcompile/regression evidenceに限り、次のdirect contract gapを閉じない。

- library removalはplanned childの失敗後にもapproved ancestorをrecursive deleteし得る。whole-folder deleteは
  prerequisite targetの実行成功集合が揃った場合だけ許し、それ以外はfile-onlyへ縮退させる。
- normal/pending invalid-extension rename、pending removal、installation repairのpost-admission revalidationが
  preflight objectの再投影に留まる。normal/repairはcurrent canonical catalog、pending routeはcurrent pending
  package snapshotからidentity、owner、path、hash、membershipを再解決し、不一致ならfilesystemへ進まない。
- invalid-extension renameのfailure dialogはauthoritative catalog/pending publicationより後、かつlease/lock解放後に
  実行する。DB failureをlegacy rename dialogへ誤分類しない。
- after-admission filesystem/DB participantのcapabilityは必須引数とし、nullable/direct apply fallbackを削除する。

Shared lease foundationにはさらにrelease-blocking P1がある。現行`IDisposable.Dispose`はrelease/effectの結果を
返せず、deferred effect failureを捨て、release failureで後続effectを抑止し、primary exception処理中に投げれば
primaryを置換し得る。これは「成功に見える失敗」を禁止するD-15のfailure contractへ直接反するため、次を
coherent correctionとして扱う。

- completion開始時にcapabilityを無効化し、owner releaseを試行した後、release failureの有無にかかわらず
  全deferred effectを登録順・各一回・failure-isolatedで実行する。completionはrelease/effectの全failureを
  immutable receiptとして返す。
- command runnerはprimary exceptionの同一instanceとthrow siteを保持し、secondary completion receiptを失わない。
  primaryがないvoid/list/bool routeはcompletion failureをthrowし、成功値を返さない。
- typed routeはdurable/cleanup/manual-recovery factsを再分類せず、terminalization failureをreceipt/resultで明示する。
  effectをleaseへ移譲したreceiptから同じeffectを再取得・再実行できないようにする。
- outer command ownerだけがcompletionを行い、nested participantは必須capabilityだけを受け取る。
  `EnterMutationSequence` no-op compatibility surface、capability-free write overload、nullable/runtime-probe fallbackを退役する。

このfailure propagationはobservable test semanticsを含むため、既存packetへの独立amendmentを実装前に凍結する。
amendmentがauthority gapを報告した場合、またはtyped consumerまでfailureを運ぶためにdurable stateの意味変更が
必要になった場合だけ再計画する。そうでなければshared foundationを一writerで先に修正し、そのAPIへlegacy、
startup、LR2 routeを順に移行する。D15全体は引き続き一commitとし、最新snapshotのfocused Quick、Full、
frozen static reviewが揃うまで確定しない。

Test Contract Packet amendment `D15-EXCLUSIVE-FILE-MUTATION-20260901 / Amendment A1:
TERMINALIZATION` を承認する。追加Contract IDは`D15-TERM-CAP`、`D15-TERM-EFFECTS`、
`D15-TERM-PRIMARY`、`D15-TERM-VOID`、`D15-TERM-RECEIPT`、`D15-TERM-RELEASE`、
`D15-TERM-READMIT`、`D15-TERM-LEGACY`。observable authority gapはなく、既存16 IDの意味とrequired 33 FQN
rosterは変更しない。worker編集前snapshotはbase `4150c69f49e5ef66a7732dd3817660f4b54ca1b9`、
binary diff hash `119f399efd602721d492d5451cbaf22e39f2e924`、29 filesとして記録した。新しいcompletion seamが
baseへ存在しないcaseはcompile failureをred evidenceにせず、amendment記載のtargeted mutantを使う。

Startup initialization sliceは`D15-INIT-REPAIR` / `D15-LIFE`を実装済みとする。catalog identityとexact mtimeを
admission後に再検証し、差し替え・消失したcandidateではfilesystem/DB mutationを行わず、null mutation serviceを
明示failureにした。base-red `artifacts/verification/tests-quick-20260901-064601`（13 pass / 2 intended fail）、
head `artifacts/verification/tests-quick-20260901-070351`（17/17 pass）を証拠とし、統合後の再実行を残す。

#### D-15 scope simplification decision (2026-09-01)

性能/KISS監査で、全件deferred callback、startup追加全走査、pending全件deep clone、および通常起こらない
internal release failureへのstate-machine保証が正常系コストと保守性を悪化させることを確認した。ユーザー承認により、
次のdecisionをA1より優先する。既存16 Contract ID、durable/cleanup/manual-recovery semantics、required 33 FQN
rosterは変更しないが、A1の`D15-TERM-EFFECTS`、`D15-TERM-VOID`、`D15-TERM-RECEIPT`、
`D15-TERM-RELEASE`はAmendment A2で次の範囲へ改訂する。

- package/auto-rename batchのexclusive leaseは維持する。中間progressは件数比例のclosureを保持せず、
  bounded latest-value coalescerへpublishする。mutation ownerから任意callback、UI dispatcher、task startを呼ばず、
  中間値の間引きを許容する。workflow ownerがadmission前に開始した独立consumerはmodel/package/collection lockを
  持たず、lease中にもbest-effort progressを配信してよい。subscriberからのmutation再入はblockせずlogical leaseで
  決定的に拒否する。terminal progress/resultだけをlease解放後に一度publishする。
- canonical model/catalog/live-stateのauthoritative post-commit publication failureはoperation failureとして明示し、
  success milestoneを返さない。progress、dialog、通常notificationはnon-authoritative best-effortとし、
  failure-isolatedで診断へ残すが、確定済みdurable/cleanup/manual-recovery outcomeを再分類しない。
- internal lease releaseはI/Oと外部callbackを含まないno-fail-by-designの短いowner操作に限定する。
  completion開始時のcapability無効化、通常release後のeffect実行、primary exceptionを隠さないことは維持するが、
  人工的なrelease failure後にも全effect実行・再admissionできるという多重故障保証は対象外とする。
- DBはアプリprocess-exclusiveを前提とし、外部DB変更検知のためのoperationごとの再SELECT、定期reload、
  全件比較を追加しない。transaction/SQLite failureは隠さず、次回起動時の既存整合性確認に委ねる。
- filesystemは外部変更され得るため、承認対象pathの存在、reparse、必要最小限のidentityだけをdestructive operation
  直前に安価に確認する。catalog/pending全体のcloneや再走査は行わない。
- startup leap-year candidateは既存folder load loopで収集し、別のDB全folder列挙と全path mtime問い合わせを追加しない。
  承認済みcandidateだけをadmission後にtargeted revalidateする。
- pending rename/remove/fixは対象package/chart/install-rowだけのimmutable mapを一度構築し、unused full cloneと
  targetごとのlinear scanを削除する。preflight identityがadmission前後で変わればmutationしないこと、child failure後に
  ancestor recursive deleteへ拡大しないことは維持する。
- `EnterWriteScope`等の実体を失ったcompatibility引数、optional/null capability、ambient/runtime-probe fallbackは退役する。

Test Contract Packet `D15-EXCLUSIVE-FILE-MUTATION-20260901 / Amendment A2:
SCOPE-SIMPLIFICATION / Correction C1`を承認する。新規IDは`D15-A2-PROGRESS`、
`D15-A2-PROGRESS-SEAL`、`D15-A2-DB-SCOPE`、`D15-A2-FS-SCOPE`、
`D15-A2-STARTUP-SCOPE`、`D15-A2-PENDING-SCOPE`。A1の`D15-TERM-EFFECTS`、
`D15-TERM-VOID`、`D15-TERM-RECEIPT`を上記classificationへreplaceし、`D15-TERM-RELEASE`をretireする。
`D15-TERM-CAP`、`D15-TERM-READMIT`、`D15-TERM-PRIMARY`、`D15-TERM-LEGACY`は通常releaseと
bounded telemetry exceptionを反映して維持する。

Progressのobservable boundはpending最大1、draining最大1、scheduled/running UI work O(1)とし、wall-clockや
allocation snapshotではなくbackpressureとwork ledgerで検証する。consumerを明示pumpできる場合はlease中に
少なくとも一つの中間progressを配信可能でなければならない。terminalization開始後は同generationのlate progressを
捨て、次batchへ漏らさない。authoritative publication、dialog、terminal callbackは引き続きpost-releaseとする。

実装は一writerで、先にprogress closure、startup追加全走査、pending全clone/O(N²)を縮小し、その上で必要最小限の
completion seamへrouteを移行する。packageでは既存`ActiveStatusPublication`のlatest/scheduled patternを再利用し、
auto-renameへfeature-localに同patternだけを置く。汎用progress frameworkやlossless queueは追加しない。

A2 worker-start snapshotはbase `a1fd18e1c179a1028b33b1895a3129a4e644f4d3`、binary diff hash
`119f399efd602721d492d5451cbaf22e39f2e924`、29 filesとして固定する。

#### D-15 A2 implementation checkpoint (2026-09-01)

Startup scope simplificationは完了した。leap-year candidateを既存`NormalizeSongTable` folder loopで収集し、
initial lease解放後のdialogで承認されたK件だけをexact-path queryとmtimeでrevalidateする。追加全folder capture、
repair時の全row dictionary、approved-path/defer plumbingを退役した。identity-guard mutant
`artifacts/verification/tests-quick-20260901-082016`は意図した1 fail、head
`artifacts/verification/tests-quick-20260901-082111`は17/17 pass。DB workは3N相当からN+K targeted、
追加memoryはO(N)からO(K)となる。

Progress sliceはshared gate blockerにより一旦停止した。package/autoを含むproduction 14 callerは
`ChartFileOperationSynchronizer.Enter()`のblocking/reentrant `Monitor`をBMS logical admissionより先に取得する。
別threadのintermediate progress subscriberから別workflowへ再入するとlogical leaseのrejectへ届かず待機し、
同期waitではdeadlockし得る。既存6 testはbusy中の待機継続を明示的に固定し、startup attachにはsame-thread
Monitor reentrancy依存もある。

Issue resolverは、全workflowへBMS capabilityを露出するlogical-first再設計をrejectし、共有gateを
`Interlocked.CompareExchange`によるthread-independent/nonreentrant `TryEnter(out lease)`だけへ置換する案を
推奨した。blocking/reentrant `Enter`、wait fallback、ambient tokenは残さず、busy時はfilesystem/DB/catalog、
playback stop、activity/suppression publication、dialog、source ownership transferを開始せず、各既存typed/null/bool
routeで明示failure/rejectionを返す。terminal後のfresh admissionは成功する。`RegularChartListOwner`のnested attach
gateはfile operationでないため退役し、playback temporary copyはrename/source enumeration前にgateを取得する。

この案はA2 progress再入をKISSに閉じる一方、通常の同時workflow操作も「gate解放まで待ってから実行」から
「busyとして即時拒否」へ変えるobservable behaviorを伴う。ユーザー承認によりfail-fastを採用する。
`D15-ADM`、`D15-A2-PROGRESS`、`D15-A2-PROGRESS-SEAL`、`D15-TERM-READMIT`のcoherent gate
migrationとして、14 production callerのside-effect-zero failure mappingと、既存wait test 6件の退役/置換を
独立test-contract addendumで凍結してから実装する。busy retry queue、blocking fallback、ambient token、
BMS capabilityのViewModel横断伝播は追加しない。

Test Contract Packet `D15-CHART-FILE-GATE-FAILFAST-20260901`を承認する。Contract IDは
`D15-GATE-CORE`、`D15-GATE-LEASE`、`D15-GATE-BUSY-ZERO`、`D15-GATE-AUTO`、
`D15-GATE-PACKAGE`、`D15-GATE-DUP`、`D15-GATE-CATALOG`、`D15-GATE-PENDING-EXEC`、
`D15-GATE-PENDING-READ`、`D15-GATE-SELECTED-READ`、`D15-GATE-SELECTED-MUTATE`、
`D15-GATE-REGULAR-RENAME`、`D15-GATE-ZERO`、`D15-GATE-PLAYBACK`、`D15-GATE-STARTUP`、
`D15-GATE-REGULAR-ATTACH`、`D15-GATE-TERMINAL`、`D15-GATE-LEGACY`。same/cross-thread busy即時false、
cross-thread/exactly-once/stale-safe lease release、caller別failure、busy side-effect zero、terminal後fresh admitを
凍結する。新規required FQN、lane、DNP、timeout変更は行わない。

Shared gate migrationは完了した。`Monitor`とblocking/reentrant `Enter`を退役し、CAS tokenによるcapacity-1、
thread-independent/nonreentrant `TryEnter(out lease)`へ移行した。leaseはcross-thread dispose、repeated/concurrent
dispose、stale leaseに対してexactly-onceかつactive ownerを解放しない。14 production callerはgate取得をdialog、
activity/suppression、playback stop、filesystem/DB/catalog access、source ownership transfer、startup attachより前へ
移し、busyを各routeの明示failure/rejectionとして即時返す。`RegularChartListOwner`のnormal-library refresh attachは
file operation gateから退役し、startup aggregate attachだけが外側で保護する。

focused verification `artifacts/verification/tests-quick-20260901-103000/functional`はprimitive、package gate
replacement、duplicate、selected、regular rename/attach、playback、application composition、startup、folder refreshの
計199件がpassし、build 0 error、`git diff --check` clean。busy/reentrant mutantは2/2 fail、stale/double-dispose
mutantは1/2 failし、いずれもheadへ復元済み。旧package generation testが新契約でも旧wait signalを残したため
最初のcombined run `artifacts/verification/tests-quick-20260901-093220`はresultなしで300秒hard timeoutしたが、
当該testを「競合中の即時failureとcleanup、解放後のfresh explicit request」に置換し個別にpassした。timeout延長、
worker削減、retry/wait fallbackは追加していない。

Progress simplificationは完了した。package installとfolder auto-renameは、workflow ownerがadmission前に生成する
feature-local capacity-1/latest-wins consumerと、mutation producerへ渡すimmutable factのnonblocking writerへ移行した。
mutation/model側からの同期delegate呼出し、UI dispatcher/task start、件数比例のdeferred closure蓄積を退役した。
consumerはmodel/package/collection lockを持たずにlease中の中間progressを配信でき、shared fail-fast gateにより
subscriber mutation再入は待機せずbusyとなる。pending最大1、draining最大1、scheduled/running UI work O(1)で、
中間値の間引きを許容する。terminal seal後のsame-generation late valueは破棄し次batchへ漏らさず、terminal
progress/resultだけを通常release後にexactly once publishする。progress failureはbest-effort診断でcanonical resultを
再分類しない。

focused Quick `artifacts/verification/tests-quick-20260901-112522/functional`は155 total、153 pass、
既承認skip 2、0 fail、Release build 0 error、`git diff --check` clean。per-item scheduling mutantはbounded-dispatch
assertionでfailし、auto-rename seal除去はdispatch count 3 vs 2、package seal除去はtwo-batch stale-progress oracleで
failした。全mutantはheadへ復元済み。同期progress adapter、旧`ExpandInstallSources`、旧
`InstallChartPackagesAutoWithResult`、auto-renameのper-item deferred progress routeを退役し、汎用framework、timer、
polling、persistent state、lossless queueは追加していない。

Pending/legacy simplificationの独立Test Contract Packet
`D15-EXCLUSIVE-FILE-MUTATION-20260901 / Amendment A2: SCOPE-SIMPLIFICATION / Correction C1 —
PENDING-LEGACY`を承認する。適用IDは`D15-A2-PENDING-SCOPE`、`D15-A2-DB-SCOPE`、
`D15-A2-FS-SCOPE`、`D15-SNAP`、`D15-LIFE`、`D15-REG`、`D15-LEG-RENAME`、
`D15-LEG-REMOVE`、`D15-LEG-FIX`、`D15-TERM-CAP`、`D15-TERM-PRIMARY`、
`D15-TERM-LEGACY`。observable semanticsのauthority gapはない。worker開始時にgate/progressを含む
実行base revisionとbinary diff hashを改めて固定し、A2 worker-start `a1fd18e1c179a1028b33b1895a3129a4e644f4d3`
を無条件のbase-red revisionとしては使わない。

Pending/legacy worker-start snapshotはbase `021a5a6380693d435515d9852f344c2c79641290`、tracked binary diff
hash `d2ace143924106f922ca41c4d5b5edbb63b3048a`、tracked 47 filesとして固定する。untrackedは
`BeMusicSeeker.Tests/ChartFileOperationSynchronizerTests.cs`（blob
`220b9119e2d5e921813e4e005d7d1f0d49a99c80`）と
`BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallProgressContracts.cs`（blob
`770bd0508dbbe6ec5bf4a27002f16565fdc995a8`）の2 filesである。後者は完了済みprogress sliceのproduction
contractであり、pending workerは両untracked fileを変更しない。

対象K件についてprojection buildは一回、unrelated package/chart/install row projectionは0、key lookupはK以下、
sequential candidate comparisonは0をwork ledgerで検証する。wall-clock、allocation、source/private reflectionは
oracleにしない。confirmation後のDB再SELECT/reload/full compareは0とし、destructive work直前のtarget FS
existence/reparse/必要最小identityだけを再検証する。stale/missing/reparse-invalid targetはFS/DB mutation 0かつ
明示non-successとし、同一pathでもowner/package membershipが変わればstaleとする。child failure後のrecursive
ancestor deleteは0で、failed/unprocessed siblingとDB membershipを保持する。renameのglobal path/index changeは
canonical durable apply/publicationだけから生じ、missing/null/foreign/disposed capabilityはmutation前にrejectする。
primary mutation failureをdiagnostic/cleanup failureで置換せず、人工lease-release failureは注入しない。

negative controlはunrelated lazy/tripwire packageへ触るfull-clone mutant、triangular comparisonとなるO(K²) mutant、
execution phase DB full-read mutant、late FS probe省略 mutant、durable apply前global path/index mutation、child failure後の
recursive parent delete、nullable capability direct fallback、primary exception replacementを使う。新しいpersistent
telemetry、test-only public API、全file hash、journal/rollbackは追加しない。

Pending/legacy simplificationは完了した。rename/remove/fixは対象K件だけのimmutable keyed projectionを一回構築し、
unused full Pending deep cloneとtargetごとのlinear matchingを退役した。admission後はprocess-exclusiveなcurrent owner、
package membership、authorized pathとtarget FS existence/reparse/必要最小identityを再検証し、unresolved/stale targetを
明示rejectする。外部DB変更検知のための再SELECT/reload/full compare、guard目的の全file hashは追加していない。

pending install-row deltaはprojectionを受け取り、invalid-extension renameはdurable applyより前にglobal path/indexを
変更しない。selected childの失敗/stale後はrecursive ancestor/package-root deleteへ拡大せず、failed/unprocessed
siblingとmembershipを保持する。既に成功したchildについて、authorized ancestryのnon-recursive empty cleanupだけを
best-effortで許可する。pending-specific nested applyはexplicit live owning capabilityを検証し、direct/null fallbackを
通さない。

filtered Quick `artifacts/verification/tests-quick-20260901-122840/functional`は121 total、119 pass、既承認skip 2、
pending-only `artifacts/verification/tests-quick-20260901-123528/functional`は6/6 pass、related
`artifacts/verification/tests-quick-20260901-122429/functional`は104 total、103 pass、既承認skip 1。
late FS probeを外すmutant `artifacts/verification/tests-quick-20260901-121347`と、child failure後にrecursive parent
cleanupを許すmutant `artifacts/verification/tests-quick-20260901-121438`は各1 intended failで、両方headへ復元済み。
build 0 error、Roslynator project analysis 0 diagnostics、`git diff --check` clean。新規persistent telemetry、test-only
public API、sleep、timeout/lane/DNP変更はない。zero-note legacy package routeは本unit対象外のfoundation handoffとする。

Foundation/LR2 preparationの独立Test Contract Packet `D15-FOUNDATION-LR2-PREP-20260901`を承認する。
適用IDは`D15-ADM`、`D15-CAP`、`D15-EXEC`、`D15-LIFE`、`D15-REG`、`D15-LR2-PREP`、
`D15-TERM-CAP`、`D15-TERM-PRIMARY`、`D15-TERM-READMIT`、`D15-TERM-LEGACY`。observable semanticsの
authority gapはなく、worker開始時にgate/progress/pendingを含むbase revisionとbinary diff hashを固定する。

Foundation/LR2 worker-start snapshotはbase `7ebb0ad503083dc49d67c418f5072376399fbaef`、tracked binary
diff hash `bb0a186031f31b972b20e279ecff2eafaf10451d`、tracked 47 filesとして固定する。untrackedは
`BeMusicSeeker.Tests/BmsLibraryPendingLegacyMutationTests.cs`（blob
`a93bfc6a15dad11e08f86d8c9bd50b9a7b569095`）、
`BeMusicSeeker.Tests/ChartFileOperationSynchronizerTests.cs`（blob
`220b9119e2d5e921813e4e005d7d1f0d49a99c80`）、
`BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallProgressContracts.cs`（blob
`770bd0508dbbe6ec5bf4a27002f16565fdc995a8`）の3 filesで、foundation/LR2 workerは変更しない。

leaseはadmission、capability lifetime、no-I/O/no-callbackの短いreleaseだけを所有し、typed command/coordinatorが
post-release terminalizationを分類する。canonical prepared surface/model/catalog/live-state publication failureは
operation failureでsuccess milestoneを返さず、progress/dialog/log/ordinary notification failureはbest-effort診断として
durable/cleanup/manual-recovery outcomeを再分類しない。primary exception identity/throw frameをsecondary failureより優先し、
人工release failureの注入・receipt・state machineは追加しない。

LR2 preparationはcustom-folder physical outputとplaylist/LR2 DB syncを一つのexplicit live capabilityで所有し、両方の
完了と通常releaseより前にprepared surfaceをcanonical publishしない。queue busyは現在statusを待機なしで返し、explicit
`TryRun`は既存のwait/retry semanticsを維持する。chart-file fail-fastをLR2へ一般化しない。optional/null capability、
capability-free port/runtime、runtime provider probe、`EnterMutationSequence`、`EnterLr2MutationSequence`、ignored
`EnterWriteScope` argumentsは最終compiled caller graphから退役する。foundationとLR2は一writerのserial sub-sliceとし、
途中のcompatibility shimを残さない。required FQNに退役対象が含まれる場合はrosterを黙って変更せず再計画する。

negative controlはcapability invalidation遅延、null/foreign/disposed capability受入、preparation phase間の早期release、
release前prepared-surface publication、authoritative failure swallow、best-effort failureによるdurable再分類、primary
exception置換、queue/TryRun admission統合、compatibility overload/runtime probe再導入を使う。`D15-TERM-RELEASE`の
release-fault mutantは実施しない。LR2未収束`Completed`は本unitの対象外で、
`devdocs/memo/lr2-song-db-sync-diagnostic-audit-note.md`を後続課題の正本とする。

Release required rosterをread-only監査し、canonical
`devdocs/acceptance/v216-first-hop/artifact.json`の33 FQNには、退役予定の
`Lr2SynchronizationArchitectureTests`、`BmsLibraryLr2SongDbSyncTests`、
`Lr2SongDbSyncWorkflowOwnerTests`、capability-free custom-folder/output fixtureのFQNが一件も含まれないことを確認した。
したがってfoundation/LR2 packetのrequired-FQN再計画条件は発火しない。直接影響するrequired entriesは既存
`COMP-*` 6件（package 2件、resilient mutation boundary 4件）だけであり、manifest、
`scripts/verification-runner-contract.ps1`、`scripts/verify-refactor.ps1`、
`scripts/verification-test-outcomes.ps1`は変更しない。

#### D15 local playlist admission correction

Test Contract Packet `D15-PLAYLIST-LOCAL-ADMISSION-20260901` を承認する。Contract ID は
`D15-PL-ADM-ZERO`、`D15-PL-RETRY`、`D15-PL-NOCOMMIT`、`D15-PL-NONLR2` とする。
`RenameFolderBMSTable`、`RemoveFolderBMSTable`、`CreateNewFolderBMSTable`、
`AddPlaylistEntriesToFolderBMSTable`、`RemoveEntriesBMSTable` は、LR2 custom-folder 出力を伴う
commit 時だけモデル変更前に process-wide lease を非ブロッキング取得する。busy は待機せず明示失敗し、
モデル、playlist DB、LR2 row、生成ファイル、BMT queue を変更しない。解放後の通常リトライは一度だけ
mutation を適用して DB / custom-folder 出力を収束させる。`commitFlag=false` と非 LR2 は lease provider を
参照しない。新規 queue、rollback state、full catalog snapshot、blocking fallback は追加しない。

旧 table-writer 待機を固定していた
`ReloadPlaylistTargetsAsync_SerializesLr2FolderConvergenceWithLocalFolderEdit` は、5操作の busy side-effect-zero、
retry convergence、no-commit / non-LR2 negative control を検証する behavior test へ置換した。base-red は
busy admission 前に `last_update` が変化する旧順序を検出し、head の `BmsPlaylistExternalReloadTests` は
17/17 pass。D15 統合 focused Quick
`artifacts/verification/tests-quick-20260901-182527/functional` は 375 total、369 pass、既承認 skip 6、
build 0 error、tracked fingerprint unchanged である。busy 例外の型・翻訳文言、内部 lock 配置、timestamp、
physical write 順は保証対象に含めない。

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

#### U9 Full blocker addendum — shutdown failure isolation

最初の Full `artifacts/verification/tests-full-20260831-232440` は canonical Functional の
`serial-state-a` で 405 件中 2 件が失敗し、post-Functional phaseへ進まなかった。直接の失敗は
uninitialized playlist fixtureで追加済みreadiness ownerが初期化されていなかったことだが、調査により
`StartupReadinessCoordinator.RequestShutdown` の cancellation callback failureが
`PlaylistShutdownCoordinator` のrequested publicationと残りのshutdown callbackを全て迂回し、
shellがshutdown blocking workを待ち続け得るproduction failure isolation gapを確認した。fixtureだけの
workaroundは採用せず、次のpacketで既存`START-SHUTDOWN`契約を補強した。凍結済みrequired rosterの
FQNと33件cardinalityは変更しない。

- Packet: `START-SHUTDOWN-FAILURE-ISOLATION`
- Owner fan-out: requestedを先にpublishし、readiness、BMT、hydration、logを順に各一回試行する。
  callback failure後も残りを実行し、最初のexception identityとthrow-siteを保持して再送出する。
- Readiness terminalization: required/install receiptと未開始drain receiptはcancellation callback failureでも
  terminalにする。実行中drainは早期成功にせず、consumerの`finally`までblocking receiptを保持する。
- Coverage: `PlaylistShutdownCoordinatorTests`をextendし、exact roster FQN
  `StartupReadiness_ShutdownTerminalizesWaiterAndImportQueue`をin-placeで強化する。shared resource、
  fixed wait、追加`DoNotParallelize`はない。
- Evidence: base-red `tests-quick-20260831-235112` 32/34 pass、fan-out targeted mutant
  `tests-quick-20260901-000010`でfailure、head `tests-quick-20260901-000851` 34/34 pass、
  implementation commit `cde646a2`。
- Requalification: snapshotが変わったためFullを再実行する。standalone Functionalは重ねない。

#### U9 Full blocker addendum — existing-data startup repair admission

Full `artifacts/verification/tests-full-20260901-193631` は canonical Functional を全 shardで完了した後、
existing-data phaseで134.441秒後に失敗した。LR2 fixtureでは UI Automation が初期化完了を観測した一方、
product warning dialogが残り、graceful shutdown要求後もself-contained appが終了しなかった。したがってこれは
単なるacceptance-driver timeoutではなく、required LR2 workとpost-initialization custom-folder repairが競合して
interactive busy warningを出したproduction failureと、そのdialogを初期化完了と独立に分類できなかったacceptance
failureのcompound blockerとして扱う。retained sandboxのlogにはshutdown時点でrequired running 0、post task running 1と
60秒の`startupBackgroundTasks` slow-waitが残る。

承認済みTest Contract Packetは `existing-data-init-dialog-v1` と
`startup-repair-required-idle-v1` である。後者のContract IDは `SRI-01`–`SRI-05` とし、次を凍結する。

- custom-folder repairはrequired scheduling closureとqueued / running required work 0の両方を待つ。開始後はterminalまで
  新しいrequired workを開始させないが、通常のpost taskの既存overlap / lane concurrencyは維持する。
- shutdownは未開始repairをexactly once discardし、running required workのdrainを維持する。
- admissionがなおbusyならbackground repairは`showMessage=false`で一度だけ取得を試み、待機・retry・dialog・status / DB /
  filesystem mutationなしでfailureまたはskipとしてterminalにする。後続の通常起動がfresh reconciliationを行い、
  retry stateは永続化しない。
- foregroundの手動mutation routeは`showMessage=true`を維持する。

実装はschedulerの既存required-idle判定を`playlist_custom_folder_output_repair`だけへ追加し、playlistのinjected
lease providerを`Func<string, bool, LibraryFileMutationLease>`へ拡張してbackground repairだけをnoninteractiveにした。
既存の通常post taskをidle-onlyへ広げず、dialog text、busy exception subtype、persistent retry queueは追加しない。
focused evidenceはschedulerの `StartupBackgroundTaskSchedulerOwnerTests` 24/24 pass、existing-data dialog classifierの
4/4 pass、`BmsPlaylistPersistenceLifecycleTests`の23/23 pass
`artifacts/verification/tests-quick-20260901-204244/functional`である。後者は`SRI-03` / `SRI-05`について、
repair targetが存在する状態からbusy admissionが即時terminalになり、一回だけ`showMessage=false`を要求し、status / DB /
LR2 synchronization / output surfaceを変更しないこと、およびmanual再出力が`showMessage=true`を維持することを確認した。
この時点ではreal existing-data acceptanceとfinal Fullを未実施としていた。その後の実受入で、初期化完了
dialogそのものはexact caption/message/actionの分類、Invoke、消滅確認を通過したが、別のstartup raceが
顕在化したため、次のaddendumでterminal convergenceを補強した。

#### U9 Full blocker addendum — LR2 required sync / installable maintenance race

Full `artifacts/verification/tests-full-20260901-215006` は同一binaryで直前に成功していたexisting-data
acceptanceを179秒でtimeoutした。retained sandboxでは、required LR2 preparation lease解放直後に
`installable_maintenance`がouter mutation leaseを取得し、LR2 requestが
`queue_skipped status=Needed requestedVersion=0`のままterminal eventを出さなかった。さらに
maintenance内の`setModeAndCommitToDB`がouter lease保持中にleaseを再取得してself-blockした。acceptanceは
LR2 event待ちからdialog automationへ到達しておらず、dialog contractの回帰ではない。

承認・実装したpacketは次の3件である。

- `startup-repair-required-idle-v2` (`SRRI2-01`–`SRRI2-04`):
  `installable_maintenance`だけを既存required-idle admissionへ追加し、required enrollment / queued / running
  workがterminalになるまで開始しない。通常の他post task concurrencyは維持する。
- `installable-outer-lease-capability-v1` (`IOLC1-01`–`IOLC1-03`)（historical proposal; superseded by the correction addendum below）:
  maintenanceのouter leaseから同じlive capabilityを一度発行し、mode detectionとcatalog maintenanceへ渡す。
  nested reacquisition、nullable fallback、retry、UI waitは追加しない。
- `existing-data-lr2-queue-skipped-failfast-v1` (`EDLR2-01`–`EDLR2-03`):
  acceptance runnerは同一log eventのexact tuple
  `lr2_song_db_sync queue_skipped` / `status=Needed` / `requestedVersion=0`をterminal failureとして
  completionより先に分類する。production status、timeout、retry stateは変更しない。これは当時の
  blocker診断と暫定oracleの履歴であり、現行contractでは後述の`S3-LR2-DURABLE`にsupersedeされる。

focused Quickは`artifacts/verification/tests-quick-20260901-230429/functional/results.trx`で164/164 pass。
最終実配布snapshotのFull `artifacts/verification/tests-full-20260901-233628`はFunctional 2814 pass / 8 approved
skip（170.9秒）、existing-data 22.5秒、update acceptance 40.1秒、ProcessIntegration 74 pass / 2 approved
skip、actual v2.1.6.0 first-hop、ReleaseAcceptance 2/2、formatを完了した。analyzerは既存XML commentの
`targetTypes` typo 1件だけで停止し、その1行を`targetType`へ修正した最終snapshotでanalyzer 0 diagnostics、
format、`git diff --check`を再確認した。packetやproduction behaviorはこのanalyzer-only修正で変わらない。

Full並列負荷で一度だけ失敗したprogress-writer fixtureは、production deadlockではなく
`RunContinuationsAsynchronously` TCSとfake内の同長watchdogを競合させたtest harness flakeだった。
productionを変更せず`ManualResetEventSlim`による専用thread handshakeへ置換し、関連40/40と対象3件の
5反復15/15を通した。timeout延長、`DoNotParallelize`、worker削減、assertion弱化は行っていない。

#### U9 correction addendum — installable capability scope (2026-09-02)

The historical `installable-outer-lease-capability-v1` entry above is retained as the
diagnosis of the earlier design, but it is superseded by
`SIMPLIFY-V3-SAFETY-3000-S2-LIFECYCLE` (`S2-NARROW-NESTED` / `S2-LR2-IDLE`).
It is not a current packet or current evidence source. In the corrected contract,
`installable_maintenance` acquires one outer `LibraryFileMutationLease`; mode detection
and catalog maintenance are ordinary capability-free work inside that lease. The live
`LibraryFileMutationCapability` is retained only for the real installed-target durable
completion → LR2 normal-folder synchronization bridge, where it is validated once at
the under-existing-lease entry. No installable capability propagation, compatibility
fallback, new seam, or new wait is allowed.

The reflection-only installable maintenance test was retired because the nearby public
startup/required-idle fixtures cannot provide the same observable mode-persistence
coverage without a new seam or wait. The current evidence for this correction is
`artifacts/verification/tests-quick-20260902-090050` (153 pass / 2 expected skips / 0 fail),
x64 Release build with 0 errors, and a passing `git diff --check`. The prior
`tests-quick-20260901-230429` and `tests-full-20260901-233628` artifacts remain historical
records and must not be treated as evidence for the corrected installable capability
scope.

#### U9 correction addendum — reachable scan publication and acceptance KISS (2026-09-03)

Unit Cのscan/catalog routeはTest Contract Packet `unit-c-scan-publication`
（`SCAN-PUB-01` / `SCAN-PUB-02`）へ固定した。actual `ReloadFileDiff` と
`Initialize(Startup)`は、song DB/catalog/LR2の内部applyをcaller-owned mutation leaseと
initialization writer内で完了し、public catalog notificationだけを全outer scope解放後に
best-effortでpublishする。subscriber exceptionはdurable successをfailureへ変えない。
process-exclusive入口の内側で別catalog writerが介入するscan-only version conflictは到達不能なため、
二重snapshot、全件copy、direct fixtureを退役した。installed-targetの到達可能なrevalidationは維持する。

actual ingress testはlive Everything indexへ依存せず、既存`IChartFileScanner`の最小internal compositionから
immutable captured surfaceを渡す。production defaultは従来の`EverythingFileScanner`のままである。
startup testはDB-load reset batchをjournalで読み進め、最初のscan non-reset batchにだけpost-release確認と
throwing subscriberを適用する。focused evidenceは`tests-quick-20260902-170734` 98/98、
`tests-quick-20260902-175128` 56/56、final correction `tests-quick-20260902-181214` 2/2、
x64 Release build 0 errorで、fresh static reviewはblocking findingなしだった。

release acceptanceはPacket `S3-ACCEPTANCE-SIMPLIFICATION`へ置換した。deadline contract
`S3-DEADLINE-EXEC` / `S3-DEADLINE-CLEANUP`はphaseごとに一つのabsolute execution deadlineと
exact `+10s` failure-cleanup cutoffを共有し、stageごとのtimeout reset、late-success救済、primary failureの
cleanup/receipt failureによる置換を許さない。process exit、stream drain、exact owned identity cleanup、
phase resultを既存`verification-process-lifecycle.ps1`へ集約した。歴史的に分散していたrunner metadataの
self-validator、metadata/source-copy assertion、test-side copied orchestration、独自`taskkill` fallbackを退役し、
実runner/script/lifecycle seamを実行するtestだけを残した。takeover snapshotから約2,000 net linesを削除し、
final focused evidenceは`tests-quick-20260902-225324` 20/20、fresh static reviewはblocking findingなしである。

existing-data / first-hopは`S3-EXD-SCOPE`、`S3-EXD-MODAL`、`S3-EXD-ACTION`、
`S3-FH-BLOCKING`、`S3-LR2-DURABLE`へ固定した。completion dialogはsame PID、visible/enabled modal、
captured main HWNDのnative owner、唯一のvisible/enabled `ThemedMessageBoxOK` Invoke actionだけで分類し、
localized caption/bodyや「唯一のnon-main window」をoracleにしない。first-hopはcompletion modal不在を許容するが、
予期しないowner-bound blocking modalをdismissせずfailureにする。LR2 terminalはfree-form
`queue_skipped` logではなく、graceful shutdown後のdurable completed status、empty error、nonempty
signature/run ID、completed cursor、canonical folder/dataで判定する。final focused evidenceは
`tests-quick-20260902-234947` 21/21と`tests-quick-20260902-235440` 1/1で、fresh static reviewは
blocking findingなしだった。actual published-app UIA、pinned v2 first-hop、sealed distribution identityを含む
positive oracleは最終Fullで確認するため、`tests-full-20260901-233628`は現snapshotのrelease evidenceに使わない。

owned catalog notificationの統合修正はTest Contract Packet
`UNITC-OWNED-CATALOG-NOTIFY`（`OCN-01`–`OCN-03`）へ固定した。catalog/DB/live projectionは
caller-owned lease内で確定し、public notificationだけをlease解放後にbest-effortで公開する。
normal/pending invalid-extension routeはcommand-owned collectorを必須とし、成功・失敗のどちらでも
`finally`から一度だけflushする。durable commit後の後続LR2/live projection failureは補償、retry、replayせず、
durable stateを保持したまま元例外を呼出元へ伝播する。canonical normal/pending testとtargeted mutantで、
lease内の即時公開、`finally`欠落、durable failureの黙殺を区別した。focused evidenceは
`tests-quick-20260903-020922`、`022425`、`024821`、negative controlは`020528`、`020717`、
`022305`、`024528`で、最終fresh static reviewはblocking findingなしだった。新しいtest lane、
runner self-test、永続状態、rollbackは追加していない。pre-final integrated Functional
`tests-functional-20260903-025624`は159.5秒、2810 pass / 8 approved skip / 0 failで成功した。

最初のfinal Full候補`tests-full-20260903-025959`は、成功した`dotnet tool restore`の終了処理で
`tool-restore/command`が未作成のままstdout/stderrを保存し、`DirectoryNotFoundException`で失敗した。
削除競合ではなくFull専用callerの作成漏れだった。最初の修正後Full
`tests-full-20260903-030852`はtool restore、Functional 2810 pass / 8 approved skip（153.6秒）、publishを通過後、
direct Start/Completeを使うexisting-data appでも未作成`app/log/process`への保存に失敗した。
diagnostics directoryはStart中に消費されず、post-start failureもStop→Completeへ合流するため、作成責務を
artifactの最初のconsumerである共通`Complete-VerificationRedirectedProcess`へ一本化した。外側wrapper、
monitored-command、Startには重複作成を残していない。deadline、cleanup、failure contractは変更していない。
runner metadata/self-test/source-copy assertionは追加せず、この2件のFullをred evidence、修正後Fullを
positive acceptanceとする。存在しないcompletion pathを使う直接Start→Complete smoke、PowerShell parser、
`git diff --check`は成功し、fresh static reviewはblocking findingなしだった。

次のFull`tests-full-20260903-032400`は、Functional 2810 pass / 8 approved skip（158.6秒）、publish、
existing-dataを通過後、update successの再起動appを240秒待って失敗した。updaterはexit 0だったが、共通
lifecycleのnormal-success branchがdetached successorまでowned descendantとして停止し、直後のexact executable
path adoptionより先に再起動appを終了させていた。一時的なstream task未完了で停止対象を決める修正もfocused
actual updateで同じraceを再現した。generic lifecycleだけでは意図したsuccessorと不正な残留childを区別できないため、
normal-successのlineage/stop branchを退役し、既存signal-driven stream drainへ直接進む。tree stopはtimeout、
nonzero、明示`TerminateProcessTree`だけが所有する。新しいhandoff state、switch、固定猶予、retry、timeout延長、
self-testは追加していない。既存lifecycle focused Quickは最終5/5、parser、diff-checkが成功し、fresh static reviewは
blocking findingなしだった。

この修正後のfocused actual updateではsuccess updateと再起動app adoptionが通過し、次にrollback updaterの
意図したexit 1を`Wait-UpdaterExit`がgeneric failureとしてthrowするcaller-contract不整合を検出した。
wait ownerはtimeoutとsecondary lifecycle diagnosticsだけを失敗として伝播し、clean exit codeは返す。
success callerはnonzeroを拒否し、rollback callerはzeroを拒否してnonzero後のfailure receipt、非再起動、
明示recovery、復元状態を検証する。新しいswitch、helper、testは追加せず、parser、diff-check、fresh static reviewは
blocking findingなしだった。`runner-cleanup-success`（`RCS-01`–`RCS-03`）として、成功したowned descendant
cleanupはsecondary failureにせず、primary nonzeroと実cleanup failure/residualを維持するよう既存testを置換した。
focused actual update `update-acceptance-focused-20260903-0440`はsuccess restart、fault rollback、非再起動、
明示recoveryを含めて成功した。

Full`tests-full-20260903-041457`はFunctionalの`remaining-bms-library`で、
`AutoRenameChartFolders_ProgressRunsAfterFilesystemMutation`だけが失敗した。対象testは`BMSFiles`
notificationを観測しないのに、setup helperがfire-and-forget通知を同期5秒waitし、並列負荷時のThreadPool
遅延を機能失敗としていた。catalog stateはsetter復帰前に同期適用済みのため、このtestだけ直接代入へ置換し、
共有helper、timeout、production publication、assertionは変更しない。target 1/1、近傍AutoRename 6/6、
diff-checkが成功し、fresh static reviewはblocking findingなしだった。

Full`tests-full-20260903-042510`はFunctional 2810 pass / 8 approved skip（156.9秒）、publish、
existing-data、update、ProcessIntegration 55 pass / 2 approved skipを通過後、actual v2 first-hopのv3終了要求を
拒否されたものとして失敗した。retained logの時系列から、legacy v2とv3が共有する`app/log`に残った旧
`startup_ready_operable`をv3起動直後に再読し、main window生成前にcloseしていたacceptance mechanicsの
false negativeと確定した。Packet `v216-first-hop-fresh-startup`（`FH-FRESH-READY` / `FH-MAIN-WINDOW`）に従い、
v3起動前にlegacy logをsandbox内へ一度退避し、fresh readiness後に既存`Wait-ForWindow`を同じabsolute
deadlineで再利用する。新しいunit test、fake、helper、retry、timeout、永続状態は追加しない。同じpublished
v2/current v3 packageを使うfocused first-hop receipt `artifacts/verification/v216-first-hop/`
はhappy/lock 2/2 pass、v3 exit 0、preserved treesとsemantic state保持を確認した。

同じ失敗時cleanupの静的調査では、`Stop-VerificationOwnedProcessRecord`の既存failure-cleanup指定が
`Complete-VerificationRedirectedProcess`からshared lifecycleへ転送されず、成功した回収にも
ownership-uncertain secondary diagnosticを付ける現diff回帰を確認した。既存`FailureCleanup` switchを
Stop→Complete→Invokeの一経路だけ転送し、新しい期限や状態を追加していない。既存lifecycle test 16/16、
parser、diff-checkが成功し、二修正をまとめたfresh static reviewはblocking findingなしだった。

Full `tests-full-20260903-045012` は `ReleaseAcceptance` 2/2 を通過した後、outcome gateで失敗した。
checked-in artifactの`COMP-DURABLE`が旧
`BeMusicSeeker.Tests.ResilientFileMutationServiceTests.FileDbMutationExecutor_DurableReceiptFinalizesBeforePostCommitCallback`
を指し、現テストの承認済み`DurableFinalizer` semanticsである
`BeMusicSeeker.Tests.ResilientFileMutationServiceTests.FileDbMutationExecutor_DurableFinalizerRunsBeforeCleanup`
がmissingと判定された。parser bugにより、このmissingが`Count`例外へ変換され、本来のmissing FQN診断を隠していた。
これは同じU9 release-acceptance code unitで閉じるroster alignment問題と判断し、33 Contract IDs/membershipは不変、
`COMP-DURABLE` executable oracle FQNだけを承認済み`DurableFinalizer` semanticsへ置換する。新しいtest/helper/stateは追加しない。

同じFull `tests-full-20260903-045012`のreceipts replayでは、outcome gateが最初のunknown `NotExecuted`を露出した。
同一TRXには、`BeMusicSeeker.Tests.BmsLibraryPendingLegacyMutationTests.DeletePendingCharts_ReparseSourceIsFailureWithoutMutation`
と`BeMusicSeeker.Tests.BmsLibraryPendingLegacyMutationTests.InvalidExtensionPlan_ReparseSourceIsFailureWithoutMutation`
の2件が、`Requires Windows file symbolic-link creation privilege or Developer Mode; validates the real filesystem reparse rejection path on provisioned hosts.`
という同じ理由で記録されていた。これは既存のexact-FQN optional skip方針へallowlist entryを追加して閉じる。
required 33件、test body、required drop FQNは変更せず、新しいseam/testは追加しない。

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
| 2026-09-01 | U9 | Full failed -> blocking remediation verified | test-contract-designer + implementation-worker + root | Full `tests-full-20260831-232440` の`serial-state-a`は403/405 pass、shutdown 2件が失敗。Packet `START-SHUTDOWN-FAILURE-ISOLATION`; base-red `tests-quick-20260831-235112`; targeted mutant `tests-quick-20260901-000010`; head 34/34 `tests-quick-20260901-000851`; commit `cde646a2` | 実行中drainの早期成功を統合時に除去済み。変更snapshotでFullを再実行し、成功後にfrozen static reviewへ進む |
| 2026-09-01 | U9 | second Full failed -> stale tests triaged | root + explorers + test-contract-designer + implementation-workers | Full `tests-full-20260901-001311`: portable 2/2、bass 1/1、serial-state-a 405/405、remaining-bms-library 662 pass + 3 approved skip、remaining 2793 pass / 5 fail / 8 skip。chart reflection fixtureをpublic owner seamへ置換し24/24 `tests-quick-20260901-003429` / commit `74c54918`; MessageBox source scanをcompiled call graphへ置換しtargeted mutant `tests-quick-20260901-004920`、head `tests-quick-20260901-005332` / commit `d434427c` | LR2 failureの調査で`D-15` P1へreplan。packet freeze、single-worker実装、focused Quick後にFullを再実行 |
| 2026-09-01 | U9 / D-15 | Replanned -> packet frozen | root + explorers + test-contract-designer | static impact auditでfolder/auto-rename/mergeに加えpackage、legacy chart commands、startup timestamp repair、LR2 preparation outputをdirect migration対象と判定。Packet `D15-EXCLUSIVE-FILE-MUTATION-20260901` + amendmentsを承認し16 Contract IDへ固定。33 Contract IDs/membershipは不変、COMP-DURABLE executable oracle FQNは承認済みDurableFinalizer semanticsへ置換 | one-writer coherent migration、red/mutant evidence、focused Quickが残る。ambient compatibility shimとroute除外は禁止 |
| 2026-09-01 | U9 / D-15 | Implementation checkpoint -> corrective slices | implementation-worker + fresh read-only auditors + root | uncommitted snapshotはfocused Quick `tests-quick-20260901-032338` 151 pass / 6 approved skip。fresh auditでpackage/merge/legacy/init/LR2のP1と大半のroute-level oracle不足を検出し、commitを拒否。package-firstの5 correction sliceへ再分割 | current diffを保持してpackage lifecycleから修正。全16 Contract IDのdirect test/mutant、統合Quick、spec修正前はcommitしない |
| 2026-09-01 | U9 / D-15 | Scope simplification and local playlist admission integrated | root + implementation-worker + independent test-contract-designer | progress latest-wins、startup scan既存loop統合、Pending target-only projectionを実装。Packet `D15-PLAYLIST-LOCAL-ADMISSION-20260901`; local mutation base-red、head external-reload fixture 17/17、統合 focused Quick `tests-quick-20260901-182527` 375 total / 369 pass / approved skip 6 | final Full、frozen static review、code/test/spec/planを含む単一coherent commitが残る。LR2準備の二段階永続状態機械は追加しない |
| 2026-09-01 | U9 / D-15 | Full existing-data compound blocker -> corrective packets integrated | root + test-contract-designer + implementation-workers | Full `tests-full-20260901-193631`はFunctional全shard後、existing-data 134.441秒でgraceful shutdown failure。UIAは初期化完了を観測したがproduct warningが残った。Packets `existing-data-init-dialog-v1` / `startup-repair-required-idle-v1`; scheduler 24/24、dialog classifier 4/4、playlist lifecycle 23/23 `tests-quick-20260901-204244` | real existing-data、final Full、frozen static reviewが残る。通常post concurrencyを縮小せず、persistent retry stateを追加しない |
| 2026-09-01 | U9 / D-15 | Update acceptance stale oracle -> corrected | root + explorers + test-contract-designer + implementation-worker | Full `tests-full-20260901-204513`でrollback成功後の旧app自動再起動を180秒待つstale harnessを確認。Packet `UPDATE-ROLLBACK-ACCEPTANCE-NORESTART-20260901`; rollback後point-in-time non-restart確認、harness-owned explicit startup、`update_work/current` seam、sandbox environment継承を実装。standalone receipt `update-acceptance-worker-20260901-final`、updater/runner Quick 45 pass + approved skip 1 `tests-quick-20260901-211854` | productionのfailure後non-restart契約は変更しない。短命processの完全観測は保証せず、新規event ledgerは追加しない |
| 2026-09-01 | U9 / D-15 | Existing-data LR2/installable race -> verified | root + explorers + plan-clarifier + test-contract-designer + implementation-worker + issue-resolver | Full `tests-full-20260901-215006`の179秒timeoutをLR2 required syncとinstallable maintenanceのlease競合へ特定。Packets `startup-repair-required-idle-v2` / `installable-outer-lease-capability-v1` / `existing-data-lr2-queue-skipped-failfast-v1`; focused 164/164 `tests-quick-20260901-230429`; final real existing-data 22.5秒 `tests-full-20260901-233628` | LR2 preparation→Runningの一般lease promotionはscope外。timeout延長・retry・persistent stateなし |
| 2026-09-01 | U9 / D-15 | Release qualification -> static review pending | root + implementation-workers | `tests-full-20260901-233628`: Functional 2814 pass / approved skip 8、170.9秒; existing-data、update、ProcessIntegration 74 pass / approved skip 2、actual v2 first-hop、ReleaseAcceptance 2/2、format成功。既存XML doc typo修正後 analyzer 0 diagnostics、format、`git diff --check`成功。progress fixtureはevent handshakeへ置換し40/40 + 5反復15/15 | frozen static review、必要なfresh review、final successful Full artifact、code/test/spec/planの単一coherent commitが残る |
| 2026-09-02 | U9 / D-15 Unit C | Representable-state contract -> reachable scan publication | root + test-contract-designer + implementation-worker + fresh static reviewers | `SCAN-PUB-01` / `SCAN-PUB-02`; live Everythingを使わないactual reload/startup tests。`tests-quick-20260902-170734` 98/98、`175128` 56/56、`181214` 2/2、Release build 0 error。scan-only version conflict/double snapshotとdirect fixtureを退役 | installed-target revalidationは維持。final integrated Fullとcoherent code/spec/plan commitが残る |
| 2026-09-03 | U9 / D-15 S3 | Acceptance / deadline KISS -> reviewed | root + test-contract-designer + implementation-worker + fresh static reviewers | `S3-DEADLINE-*`、`S3-EXD-*`、`S3-FH-BLOCKING`、`S3-LR2-DURABLE`を実装。歴史的metadata/self-test/copied orchestrationを約2,000 net lines削減。deadline 20/20 `tests-quick-20260902-225324`; UIA/first-hop/durable gate 21/21 `234947` + dialog 1/1 `235440`; parser/diff-check pass。各fresh review blockingなし | published app UIA、actual v2 first-hop、sealed distribution、Functionalを含む現snapshotのFullを一度実行する |
| 2026-09-03 | U9 / D-15 OCN | Lease-internal publication -> post-release notification | root + implementation-worker + fresh static reviewers | `OCN-01`–`OCN-03`; normal/pending invalid-extensionの必須collector/finally flush、durable-after-commit failure伝播を実装。head Quick `020922` / `022425` / `024821`、targeted mutants `020528` / `020717` / `022305` / `024528`、fresh review blockingなし。pre-final Functional `tests-functional-20260903-025624` 2810 pass / 8 skip、159.5秒 | final Full、最終frozen review、code/test/spec/planのcoherent commitが残る |
| 2026-09-03 | U9 runner | Full diagnostics owner gap -> corrected | root + explorer + implementation-worker + fresh static reviewers | Full `tests-full-20260903-025959`はtool restoreの未作成`command`、`tests-full-20260903-030852`はFunctional/publish後のexisting-data未作成`app/log/process`で失敗。作成責務を共通Complete ownerへ一本化。direct nonexistent completion-path smoke、parser、diff-check成功。新規self-testなし、fresh review blockingなし | 同snapshotでFullを再実行する |
| 2026-09-03 | U9 runner | Normal-success successor cleanup -> corrected | root + explorer + implementation-worker + fresh static reviewers | Full `tests-full-20260903-032400`はFunctional/publish/existing-data後、updater exit0のdetached restart appをshared lifecycleが停止し240秒timeout。pending-stream条件もfocused actual updateでraceを再現したため、曖昧なnormal-success tree stopを退役。failure/明示tree cleanupは維持。既存Quick最終5/5、parser/diff-check、fresh review成功。新規self-test/state/timeout変更なし | focused actual update後、同snapshotのFullを再実行する |
| 2026-09-03 | U9 runner | Updater exit / cleanup interpretation -> verified | root + explorer + test-contract-designer + implementation-worker + fresh static reviewers | `runner-cleanup-success` RCS-01–03。clean nonzeroの意味はcallerが所有し、成功したowned cleanupをsecondary failureにしない。actual failure/residualは維持。既存test置換、Quick 5/5 `tests-quick-20260903-040606`、focused actual update `update-acceptance-focused-20260903-0440`成功、fresh review blockingなし。新規scenario/helper/state/timeoutなし | 同snapshotのFullを再実行する |
| 2026-09-03 | U9 Functional | Unrelated setup publication wait -> removed | root + explorer + implementation-worker + fresh static reviewer | Full `tests-full-20260903-041457`はAutoRename progress testだけが、未観測のfire-and-forget setup通知を固定5秒waitして失敗。対象testだけ直接state setupへ置換。target 1/1、近傍6/6、diff-check、fresh review成功。production/shared helper/timeout/assertion変更なし | 同snapshotのFullを再実行する |
| 2026-09-03 | U9 release acceptance | Stale readiness / cleanup forwarding -> verified | root + test-contract-designer + implementation-workers + fresh static reviewer | Full `tests-full-20260903-042510`はFunctional 2810/8（156.9秒）、publish、existing-data、update、ProcessIntegration 55/2後にstale v2 readinessでv3 closeを早期実行。`FH-FRESH-READY` / `FH-MAIN-WINDOW`としてlegacy log退避と既存window waitを実装し、focused actual first-hop 2/2 pass。failure-cleanup switch転送欠落も単一forwardingで修正、lifecycle 16/16、fresh review blockingなし | final Full、最終frozen review、coherent commitが残る |
| 2026-09-03 | U9 release acceptance | Outcome-gate roster alignment -> corrected | root + implementation-worker | Full `tests-full-20260903-045012`は`ReleaseAcceptance` 2/2後のoutcome gateで、旧`COMP-DURABLE` FQNをmissingとして失敗。parserは`VFQN-SCALAR-CARDINALITY` / `VFQN-REQ-01`で修正し、既存cardinality testをcanonical StrictModeへ同期済み（`tests-quick-20260903-052943` 3/3 pass、旧scalar/null variant negative-control fail）。focused receipt `tests-full-20260903-045012/release-acceptance/release-outcomes-focused-head.json` はRequired=33 / Optional=15、required drop FQN不変。33 Contract IDs/membership、承認済み`DurableFinalizer` semantics、新しいtest/helper/stateなし | final Full未実施 |
| 2026-09-03 | U9 release acceptance | Unknown optional NotExecuted -> allowlisted | root + implementation-worker | Full `tests-full-20260903-045012`のreceipts replayで最初のunknown `NotExecuted`を露出。同一TRXのPendingLegacy reparse 2件を`PENDING-REPARSE-OPTIONAL-20260903`どおり同じprovisioned-host reasonで既存exact-FQN optional skip allowlistへ追加。focused receipt `tests-full-20260903-045012/release-acceptance/release-outcomes-focused-head.json` はRequired=33 / Optional=15、required drop FQN不変。test bodyと新しいseam/testなし | final Full未実施 |
| 2026-09-03 | U9 Functional | Setup publication sync wait -> local async boundary | root + explorer + test-contract-designer + implementation-worker + fresh static reviewer | Full `tests-full-20260903-053701`はFunctionalのfolder rename test 1件だけが、fire-and-forget setup通知をThreadPool worker上で同期waitして失敗。Packet `folder-rename-setup-publication-boundary` / `FRN-01`に従い、当該testだけmethod-local TCSを非同期awaitしてからrename観測を開始するよう置換。exact FQN 1/1、diff-check、fresh review blockingなし | production/shared helper/runner/lane/worker数/5秒watchdog/assertion semanticsは変更しない。最終Fullを再実行する |
| 2026-09-03 | U9 format | Ignored temporary root leaked into format scope -> corrected | root + explorers + test-contract-designer + implementation-worker | Full `tests-full-20260903-055304`はFunctional 2810/8（184.2秒）、publish、existing-data、update、ProcessIntegration 55/2、actual v2 first-hop、ReleaseAcceptance 2/2後にformat失敗。Packet `FULL-FORMAT-TMP-SCOPE` / `FMT-TMP-EXCLUDE` / `FMT-GENUINE-COVERAGE`として、過去staging残置のgitignored `.tmp`だけを既存format除外配列へ追加し、tracked LR2 testの純粋な過剰indentはformatterで修正。`.tmp`を保持した実format route、parser、diff-check成功 | cleanup/self-test/helper/lane/retry/timeout/stateを追加せず、genuine workspace failureは維持。変更後Fullとfrozen static reviewが残る |
| 2026-09-03 | U9 Functional | Class-local blocking setup helpers -> retired | root + explorer + test-contract-designer + implementation-worker | Full `tests-full-20260903-062253`は同じfolder rename classの別testが同期setup通知waitで失敗。Packet `folder-rename-class-local-nonblocking-setup` / `CLS-DIRECT-01` / `CLS-BARRIER-01` / `CLS-HISTORY-01`に従い、class内30 callsiteを24 direct setter、4 property-specific async barrier、2 setter後refresh baselineへ分類してclass-local wrapper 2件を退役。class Quick 46/46、exact 6/6、diff-check成功 | shared helperを全suiteで作り直さず、production/test追加/lane/worker数/timeout変更なし。変更後Fullとfrozen static reviewが残る |
| 2026-09-03 | U9 release acceptance / v216 cache preparation | Pending -> Implemented | implementation-worker | Packet `REL-V216-CACHE` と `REL-V216-CACHE-MISS` / `REL-V216-CACHE-HIT` / `REL-V216-CACHE-FAIL-CLOSED` に従い、checked-in exact HTTPS URL、固定size/hash検証、同一directory一時ファイルからcanonical cacheへのatomic publish、既存mismatchのfail-closedを実装。Fresh review の blocking P2（curl config/retry suppression、download 前の package format identity validation）を、curl 先頭 `-q` と validated `$packageFormatVersion` の Read-side validation/return で修正。existing exact 2件 `tests-quick-20260903-073537` 2/2、metadata/runner/artifact parser、`git diff --check` pass | actual cache-miss Fullはroot最終統合で実行。metadata invalid、既存mismatch、download failure、owned temp cleanup、ProcessIntegration前phaseを統合時に確認 |
| 2026-09-03 | U9 | Integrated Full -> Verified | root | `tests-full-20260903-075126`: cacheを退避した実missからexact URLを`curl -q`で取得し、固定size/hash検証後にcanonical `.tmp` cacheへatomic publishしてからProcessIntegrationへ進行。Functional 2810 pass / approved skip 8（170.0秒）、existing-data、update、ProcessIntegration 55 pass / approved skip 2、actual v2 first-hop happy/lock、ReleaseAcceptance 2/2、format、analyzer 0 diagnostics、tracked fingerprint不変がすべて成功。取得cacheは11,260,709 bytes / SHA-256 `C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2`を独立確認し、miss再現用backupだけ削除 | frozen snapshot static reviewとcoherent commitが残る |
| 2026-09-03 | U9 / reachable P1 follow-up | Frozen review -> two-path remediation verified | root + test-contract-designer + implementation-workers + fresh static reviewers | 実UIのplaylist DnDで最初のmodel変更前に単一operation leaseを取得し、model / playlist DB / LR2 file・DBを同一owner内で完了させ、reference / UI / BMT / notificationをlease解放後に公開するよう修正。busyは副作用ゼロ、output primary failureはpost-durable effectを試行後に元例外を再送出する。起動時`install.path` sole aliasは既存transaction内でraw行削除＋canonical upsertし、Pending公開前にDBへ収束する。case-only path identityは別物として保持。既存fixtureの4経路を統合Quick `tests-quick-20260903-104013` 4/4、targeted wrong variants fail、2 unitのfresh static reviewはblockingなし。`library-mutation-boundary.md` / `startup-initialization-flow.md`の簡潔な操作・副作用対応表へ実在経路だけを追加 | `tests-full-20260903-075126`後のコード変更なので、変更後snapshotのFullと全体frozen reviewを再実行する。共通operation framework、retry、rollback永続状態、新service、全playlist refactorは追加しない |
| 2026-09-03 | U9 / install identity tests | Full stale fixture -> corrected | root + explorer + implementation-worker + fresh static reviewer | Full `tests-full-20260903-104123`はFunctional `remaining-bms-library`の既存2 testsだけが、dot aliasへ同時にcase changeも加えた旧fixtureのため失敗。Ordinal case-sensitive identityでは別pathなので、入力だけを同一caseの`C:\Pending\Existing\.`へ修正し、same-case dot alias collisionとdurable mutationなしの既存oracleを維持。exact Quick 2/2、fresh review blockingなし | production comparer / normalization / spec / helper / test harnessは変更しない。変更後Fullを再実行する |
| 2026-09-03 | U9 | Final integrated Full -> Verified | root | `tests-full-20260903-105120`: Functional 2811 pass / approved skip 8（171.3秒）、existing-data、update、ProcessIntegration 55 pass / approved skip 2、actual v2 first-hop happy/lock、ReleaseAcceptance 2/2、format、analyzer 0 diagnostics、tracked fingerprint不変がすべて成功。v2.1.6.0 artifactは、先行する実cache-miss Full `tests-full-20260903-075126`で取得・identity検証・atomic publish済みのcanonical cacheを再検証してhit | 全差分のfrozen snapshot static reviewとcoherent commitが残る |
| 2026-09-03 | U9 / playlist preparation failure | Frozen review P1 -> A2 verified | root + test-contract-designer + implementation-worker + fresh static reviewer | 全差分reviewで、playlist model / DB commit後の`PrepareCustomFolderOutput`例外だけがprimary failure result化より手前に残り、post-lease convergenceを迂回する実在P1を検出。Packet `P1-PLAYLIST-DROP-ATOMIC-20260903-A2`に従いpreparation / materializationを同じprimary capture境界へ収め、準備失敗でもcommit済みmodel / DBを維持し、lease解放後にreference / UI / BMT / notificationをattempt後、元例外identityを再送出する。既存同一test methodのbase redはeffect count mismatch、head 2/2 `tests-quick-20260903-112717`、wrapper mutant fail、fresh review blockingなし | 準備できないLR2 file / rowは成功扱いにせず、retry / rollback / 新service / frameworkは追加しない。`tests-full-20260903-105120`後の変更なのでFullを再実行する |
| 2026-09-03 | U9 | A2 final integrated Full / frozen review -> Verified | root + fresh static reviewer | `tests-full-20260903-113433`: Functional 2811 pass / approved skip 8（172.5秒）、existing-data、update、ProcessIntegration 55 pass / approved skip 2、actual v2 first-hop happy/lock、ReleaseAcceptance 2/2、format、analyzer 0 diagnostics、tracked fingerprint不変がすべて成功。前回全体reviewの唯一のP1について、A2修正と直接影響するinvariantのfresh reviewもblockingなし | code / test / spec / planを同一coherent commitへまとめる |

## Final evidence checklist

- [x] public v2 artifact seal receipt
- [x] actual v2 happy first-hop receipt
- [x] actual v2 locked-file characterization receipt
- [x] current updater preflight-lock receipt
- [x] current updater post-mutation rollback exact-tree receipt
- [x] current updater rollback-second-fault and recovery receipt
- [x] Contract ID -> exact FQN checked-in roster
- [x] optional skip allowlist and non-empty reason receipts
- [x] all focused Quick artifacts
- [x] final Full artifact including its canonical Functional and update/distribution phases（`tests-full-20260903-113433`。cache-miss経路は`tests-full-20260903-075126`で別途verified）
- [x] Functional phase elapsed time（最終qualificationは172.5秒。180秒超の注記不要）
- [x] public release note manual-recovery review
- [x] `git diff --check`
- [x] frozen snapshot static review with no blocking finding（全体reviewの唯一のP1をA2で修正し、fresh recheckでblockingなし）
- [x] fresh review after any blocking-finding remediation（playlist DnD / `install.path` two-path remediationはblockingなし）
