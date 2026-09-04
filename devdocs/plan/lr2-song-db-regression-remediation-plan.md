# LR2 `song.db` one-shot 回帰修正計画

- **状態:** In progress
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
- receipt／scan surface の version 条件は弱めない。`HasDbDiff` の catalog commit 境界で `OwnedChartCollectionVersion` を一度だけ進め、その committed version を receipt／projection へ運ぶ。
- UI／PropertyChanged notification の timing は post-lease のまま維持し、そこで二度目の version increment は行わない。

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

Test Contract Packet `LR2-FDR-07` は Unit A 完了後、実装前に凍結する。observable contract は、successful `HasDbDiff` commit ごとに owned version がちょうど一度進み、同じ version を持つ scan surface／one-shot receipt を post-lease notification 後の immediate full sync が利用すること、BMS source version が変われば receipt を拒否すること、二回目の take は失敗することである。

## Verification and review

- 各 unit は関連 filter の `verify-refactor.ps1 -Mode Quick` を実行し、fresh static review 後に独立 commit とする。
- 統合 snapshot で `-Mode Functional` を一回実行する。
- startup／durable DB／Full runner acceptance への影響を含むため、最終 snapshot で `-Mode Full` を実行する。
- static reviewer には base／head、Contract ID、base-fail／head-pass または承認済み negative-control evidence、退役 route と replacement を渡す。

## Replan triggers

- current prepared membership を authoritative input として扱えない feature authority が見つかる。
- version を commit boundary で一度だけ進めると、承認済みの別 catalog mutation completion contract が壊れる。
- production ingress を通すために新しい public seam、persistent state、retry／fallback、または別 DB writer が必要になる。
- packet の expected semantics を変更しなければ deterministic test seam を作れない。
