# Post-engineering Manual Acceptance

[現在地](./PLAN_STATUS.md) / [distribution](../../acceptance/net10-distribution-performance.md)

この文書はEngineering Gate後のユーザー確認を持つ。未実施項目をactive outcome、planner停止条件、`EXTERNAL_BLOCKER`にしない。

## MANUAL-01 — Cold boot and distribution — Completed

2026-08-01、同じPC / settings / dataでfolder-r2rとbundle-r2rを、PC起動後初回と2回目に確認した。

- いずれもoperable約22秒、required initialization complete約31～33秒。
- 旧約100秒cold-startは再発しない。
- folder-r2rは0.9～1.1秒だけ短く、5秒かつ15%の切替条件を満たさない。
- 機能上の大差はない。

選択: `bundle-r2r`。

## Completed observation — List transitions

playlist summary、playlist detail、full libraryの画面遷移は体感上問題なし。stable source / atomic presentation / data-only invalidationを保護する。

## MANUAL-02 — Runtime-free machine — Pending, non-blocking

.NET Desktop Runtime未導入のclean x64 Windows / VMで、選択したSelf-contained artifactの起動、基本操作、終了、updaterを確認する。

## RELEASE-01 — Distribution rights — Pending release prerequisite

BASS native等のproprietary / vendor componentについて、licensee、registration、redistribution証跡を公開前に確認する。ManagedBassのMIT noticeとは分離して扱う。
