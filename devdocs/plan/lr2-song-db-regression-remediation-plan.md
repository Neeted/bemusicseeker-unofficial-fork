# LR2 `song.db` one-shot 回帰修正計画

- **状態:** Complete
- **基準 revision:** `dfea9be7`
- **開始時 HEAD:** `4b1d2d34`
- **対象:** prepared app-managed `.lr2folder` の complete projection と、file-diff commit 後の receipt／scan surface version ordering

## Goal

実際の startup／reload／manual／retry ingress で、LR2 `song.db` の one-shot reconciliation が次を満たす状態へ戻す。

1. current workflow が生成・検証した app-managed playlist `.lr2folder` を complete projection に含める。
2. file-diff commit が確定した version で scan surface と committed-path receipt を発行し、直後の full sync が同じ BMS を再読込・再列挙しない。

retry、fallback、新しい persistent state、scoped DB prewrite、旧 prune-preservation route は追加しない。

## Decision list

- base の physical／captured discovery は current app-managed directory scope で除外し、その後に authoritative な current `PendingSurface` を overlay する。
- prepared surface の全 member は managed directory 配下でも full projection の入力である。同 scope の nonprepared physical file は入力ではない。scope 外の external candidate は維持する。
- managed scope または required discovery が incomplete なら、whole-table folder mutation は行わず `Completed` にしない。
- receipt／scan surface の validity は `OwnedChartCollectionVersion` に依存させない。receipt は transitional な `BmsRowsVersion` guard を維持し、scan surface は generation、BMS/BMSON rows、BMS roots、LR2 roots を guard とする。`HasDbDiff` の catalog commit 境界で `OwnedChartCollectionVersion` を一度だけ進める契約と、runtime-wide input currentness の guard は維持する。
- UI／PropertyChanged notification の timing は post-lease のまま維持し、そこで二度目の version increment は行わない。

## Follow-up correction: `LR2-OWNED-NARROW-20260904`

この follow-up は、上記の旧「version 条件は弱めない」という判断を supersede します。`OwnedChartCollectionVersion` は runtime-wide input currentness の guard としては必要ですが、captured scan surface の選択/currentness や committed-path receipt の validity dependency ではありません。

- `LR2-NR-01`: 同じ generation、BMS/BMSON rows version、BMS roots、LR2 roots なら Owned だけが異なる scan surface を再利用し、それらの narrow guard の不一致は拒否する。
- `LR2-NR-02`: receipt は BMS rows version が一致する限り Owned の差だけでは無効化せず、first take のみ path set を返す。BMS mismatch/null は破棄し、後から復活させない。
- `LR2-NR-03`: accepted receipt path の reader/currentness query skip と nonreceipt の通常処理を維持する。
- `LR2-NR-04`: input 作成後の Owned change は runtime-wide currentness では stale/non-success のままにし、surface/receipt の narrow validity 修正を global currentness へ波及させない。
- `LR2-NR-05`: supported reload/startup fixture で production chart-info route が生成可能な場合だけ publication から consumption までを拡張し、生成不能なら NR-01～04 の negative control で閉じる。新しい public/private/test-only seam は追加しない。

`OwnedChartCollectionVersion` の token restamp/carry-forward、新 token、retry/replay/rollback/fallback、persistent state、filesystem live revalidation は追加しません。将来の application-level procedural startup owner は [lr2-startup-procedural-orchestration-plan.md](lr2-startup-procedural-orchestration-plan.md) にのみ記録し、今回の修正では実装しません。

## Unit A: prepared managed folder projection

### Test Contract Packet `LR2-FDR-06`

Independent authority は `devdocs/spec/lr2-song-db-generation.md`、`devdocs/decisions/lr2-song-db-one-shot-reconciliation.md`、承認済み FDR-01／FDR-05、および永続行欠落を示す user-approved defect とする。current implementation、runtime 件数、既存 inverse assertion は oracle にしない。

- `LR2-FDR-06-01`: supported full sync が `Completed` になる場合、current prepared managed file の全 row が現在内容と正しい parent で最終 `folder` table に存在する。
- `LR2-FDR-06-02`: composition は `complete discovered/cached base -> managed-scope exclusion -> current PendingSurface overlay` とし、prepared member を含め、same-scope nonprepared file を除外し、scope 外 candidate を維持する。
- `LR2-FDR-06-03`: managed scope または required prepared discovery が incomplete の場合、既存 `folder` rows は変化せず `Completed` にしない。

Base-fail／head-pass は、同じ production-shaped regression test を `dfea9be7` と修正後 snapshot で実行し、base では setup や例外でなく prepared row 欠落により失敗することを証拠とする。既存の prepared exclusion inverse tests は置換し、base physical managed exclusion、external eligibility、late-preflight zero-mutation coverage は維持する。

### `LR2-FDR-06-02` no-scan addendum

scan surface が無い production input route も同じ合成順を使うことを、live Everything に依存せず確認する。既存の `IRootFileEnumerator` を `Lr2SongDbSyncInputBuilder` の internal dependency として候補、folder-info／text、directory enumeration へ限定伝播し、production の未指定経路は従来の Everything → Fast／fail-closed behavior を維持する。public API、persistent state、retry／fallback policy は増やさない。

canonical coverage は `Lr2SongDbSyncInputBuilderTests` とする。captured scan surface を与えず、非空の persisted managed directory `M` と次の immutable grouped surface を使う。

- `P = M\0000.lr2folder`: current prepared member。
- `S = M\stale.lr2folder`: base enumeration にだけ存在する same-scope nonmember。
- `E = <external>\external.lr2folder`: managed scope 外の base candidate。

normalized candidate set は正確に `{P, E}`、`S` absent、discovery complete とする。fake enumerator は private group name を oracle にせず、directory 要求と extension set から semantic consumer を判定し、実際に渡された group name へ結果を返す。

targeted negative control は no-scan 分岐だけを `enumerate -> prepared overlay -> managed exclusion` へ戻す mutant とする。この mutant では `P` が消え、同じ test が candidate count `expected 2, actual 1` で失敗することを確認した。mutant は保持しない。

coverage ledger:

- `Lr2SongDbSyncInputBuilderTests`: `new`。no-scan の base exclusion／PendingSurface overlay 順序を所有し、unique temp DB/files、通常 Functional lane、同期 `Create` return を completion signal とする。
- `BmsLibraryLr2SongDbSyncTests`: `replace／extend`。scan-surface merge と scheduled whole-table apply 後の managed row、current title、parent、`Completed` を所有する。
- 退役: prepared member の exclusion を正しい挙動とした2 test と、full sync で managed row absence を期待した assertion。
- 維持: physical managed base exclusion、scope-external eligibility、incomplete／late-preflight zero-mutation coverage。

### Unit A completion evidence

- regression test を production 修正前に実行し、prepared managed path が候補から欠落する assertion failure を確認した。
- no-scan enumeration 分岐だけを `overlay -> managed exclusion` へ戻す targeted mutant は、`P` 欠落により candidate count `expected 2, actual 1` で失敗した。mutant は削除済み。
- `Lr2SongDbSyncInputBuilderTests.CreateNoScanInput_EnumeratesThenExcludesManagedScopeAndOverlaysPreparedFile` の標準 Quick は 1/1 passed。
- `BmsLibraryLr2SongDbSyncTests` の class Quick は 95/95 passed。
- final fresh static review は P0／P1／受入条件へ直接反する P2 なし。

## Unit B: committed version publication ordering

### Test Contract Packet `LR2-FDR-07` (historical; validity wording superseded)

Independent authority は user-approved reproduction、`devdocs/spec/lr2-song-db-generation.md`、one-shot ADR、`library-mutation-boundary.md`、`workflow-concurrency-and-complexity.md`、および「successful `HasDbDiff` commit でちょうど一回 advance、post-lease notification は notification-only」という resolved decision とする。absolute version、production-scale件数、現行 constructor／test expected は oracle にしない。

- `FDR07-COMMIT-01`: applied file-scan catalog replacement は owned version を `V -> V+1` へ一度進め、immutable replacement receipt も同じ committed version を持つ。no-diff／pre-apply failure は進めない。
- `FDR07-PUBLISH-02`: PropertyChanged publication は lease 解放後のまま、already committed `V+1` を通知し、二度目の increment を行わない。
- `FDR07-STAMP-03` (historical): LR2 scan surface と immediate input は committed owned/BMS versions を facts として carry し、input は positive captured scan generation を持つ。receipt の validity は current follow-up で BMS rows version のみに狭めた。
- `FDR07-REUSE-04`: source revision が介在しなければ immediate reload／startup full sync は scan surface と receipt を利用し、eligible committed BMS を reader／currentness verifier へ戻さず、scan 後に追加された folder metadata を今回の projection へ入れない。
- `FDR07-VALIDATE-05` (superseded): old wording treated owned-only mismatch as a receipt invalidation. The current follow-up contract `LR2-OWNED-NARROW-20260904` keeps BMS-only mismatch invalidation and allows an owned-only mismatch while preserving one-take consumption.
- `FDR07-FAIL-06`: post-lease publication failure は committed catalog を rollback／再incrementしないが、次の consumer に usable receipt を残さない。

coverage ledger:

- `CatalogMutationOwnerTests`: `extend`。commit-boundary `+1` と no-diff不変を所有する。
- `BmsLibraryLr2SongDbSyncTests`: `extend`。real reload、post-lease event、scan／receipt stamp、captured scheduled work の reuse を所有する。
- `BmsLibraryInitializationFileScanTests`: `extend`。startup側の最終version整合を所有する。startup全体の absolute increment回数は固定しない。
- `Lr2SongDbSyncCommittedPathReceiptTests`: `replace／extend`。owned-only／BMS-only mismatch、matching one-take、real BMS mutation invalidationを所有する。
- `LibraryFileScanPipelineOwnerTests`: `extend`。late post-lease failure後の actual take null と version不変を所有する。

base-fail／head-pass は、production fix 前の同じ test が setup／compile error ではなく old-owned/new-BMS stamp、deferred double-boundary mismatch、scan／receipt non-reuseの observable mismatch で失敗することを証拠とする。negative filesystem variation は一つの committed chart と一つの post-capture candidate に限定し、件数／時間閾値は固定しない。

### `FDR07-REUSE-04` deterministic coverage split

Functional test を live Everything に依存させず、BMSLibrary へ test-only runtime seam を増やさないため、次の合成 coverage を採用する。

1. real `ReloadFileDiff` と `MissingEverythingBridge` で actual `HasDbDiff` commit、final owned／BMS version、committed-path receipt、post-lease notification の一回だけの version advance を確認する。この環境で naturally incomplete となる initial LR2 physical scan は、successful scan-capture evidence とみなさない。
2. 同じ committed state に対し、既存の typed production owner `CaptureLr2SongDbSyncScanSurface` へ complete immutable surface を渡す。scan surface、input、receipt、library の version 一致と positive generation を queue 前に確認する。
3. actual library／coordinator routeへ receipt eligibility 付きで queue し、scheduled work 実行前に committed BMS を別の valid title／artist へ変更し、captured surface に無い `.lr2folder` を追加する。
4. exact scheduled task を await し、DB が file-diff commit 時の title／artist を保持すること、late folder が projection に無いこと、terminal `Completed`、subsequent take が null であることを確認する。

typed manual capture は deterministic fixture mechanics であり、file-scan capture ordering の単独証拠にはしない。pipeline／startup test が、real catalog replacement 後に scan surface／receipt が final committed version で stamp される edge を別途所有する。

### Unit B completion evidence

- commit-boundary increment を除く mutant は `expected 1, actual 0` で失敗した。
- post-lease 側の increment を復活させる mutant は `expected 2, actual 3` で失敗した。
- coordinator から receipt eligibility を除く mutant は DB title が commit 時の `Captured` ではなく変更後の `Rewritten After Capture` になって失敗した。
- captured scan surface を消す mutant は `MissingEverythingBridge` の fail-closed path に入り、`expected Completed, actual Failed` で失敗した。
- affected five test classes の標準 Quick は 204/204 passed。mutant はすべて削除済み。
- fix 後の fresh static review は P0／P1／受入条件へ直接反する P2 なし。

## Verification and review

- 各 unit は関連 filter の `verify-refactor.ps1 -Mode Quick` を実行し、fresh static review 後に独立 commit とする。
- 統合 snapshot で `-Mode Functional` を一回実行する。
- startup／durable DB／Full runner acceptance への影響を含むため、最終 snapshot で `-Mode Full` を実行する。
- static reviewer には base／head、Contract ID、base-fail／head-pass または承認済み negative-control evidence、退役 route と replacement を渡す。

### Integration completion evidence

- Unit A は `5d6c6908`、Unit B は `fb09e29b` として独立 commit にした。
- shutdown interruption test は、非同期 `PropertyChanged` 通知を worker の停止境界とみなして exact stage を固定していた。Test Contract Packet `LR2-SHUTDOWN-01` に従い、durable stage／count と完了後の authoritative runtime snapshot の一致を検証する test に置換し、`cc37c5b8` とした。focused Quick は 1/1 passed、fresh static review は blocking finding なし。
- `ChartFileOperationSynchronizerTests` は、通常完了する `Task.Run` に 5 秒の成功 timeout を課していたため、高負荷の canonical shard で二度 timeout した。assertion と cross-thread lease contract を変えず direct await に置換し、`3cad0962` とした。focused Quick は 2/2 passed、fresh static review は blocking finding なし。
- final snapshot `3cad0962` の Functional は 2812 tests（2804 passed、8 skipped）、test execution 174.6 秒で成功した。artifact は `artifacts/verification/tests-functional-20260904-155626`。
- 同じ snapshot の Full は成功した。内部 Functional は 2812 tests（2804 passed、8 skipped）、test execution 141.4 秒、ProcessIntegration は 57 tests（55 passed、2 skipped）、ReleaseAcceptance は 2/2 passed。tool smoke、current／baseline publish、existing-data、update、v2.1.6.0 first-hop、format、analyzer も成功し、analyzer は 0 diagnostics。artifact は `artifacts/verification/tests-full-20260904-155953`。
- 最終 fresh static review で Unit A、Unit B、両 test-only correction のいずれにも P0／P1／受入条件へ直接反する P2 は残っていない。

## Replan triggers

- current prepared membership を authoritative input として扱えない feature authority が見つかる。
- version を commit boundary で一度だけ進めると、承認済みの別 catalog mutation completion contract が壊れる。
- production ingress を通すために新しい public seam、persistent state、retry／fallback、または別 DB writer が必要になる。
- packet の expected semantics を変更しなければ deterministic test seam を作れない。
