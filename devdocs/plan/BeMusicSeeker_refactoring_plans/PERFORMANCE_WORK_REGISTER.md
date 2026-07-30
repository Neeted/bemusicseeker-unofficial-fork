# Performance Work Register

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [現在地](./PLAN_STATUS.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

このregisterはcurrent-onlyである。完了した個別unit、commit、review履歴は残さない。

| ID | Classification | Current evidence | Required closure | Owner unit |
|---|---|---|---|---|
| `UI-02` | `DIRECT_FIX` | stable versioned summary sourceへ移行し、cached same-version revisitはsource交換、reset、selection restoreを行わない | final artifactの実データ確認 | `F6`／user |
| `UI-03` | `DIRECT_FIX` | summary data resetはcolumn layout snapshotを維持する。cross-mode source applyには重複invalidationが残る | main-table transactionでdata／schema／selection dimensionを分離 | `F3` |
| `UI-04` | `DIRECT_FIX` | detail→libraryはrow／column計測がほぼ0～1 msでもfirst visibleまで約1.17 s。related presentationとsource clearがblind interval | single presentation transaction、old source retire後置、無関係property fan-out退役 | `F3` |
| `HOT-01` | `LIKELY_OPTIMIZATION` | startup全体は問題の主因ではないが、forced GC、duplicate warmup、index rebuildがcritical path候補 | first-visible必須workとidle workを分離。重複version workを削除 | `F4` |
| `HOT-02` | `LIKELY_OPTIMIZATION` | install estimation／scan／parseに重複enumeration、normalization、allocationの改善余地が残る | versioned index、single-pass、pre-size／span、bounded progress／parallelism | `F4` |
| `APP-01` | `DIRECT_FIX`／`LIKELY_OPTIMIZATION` | MVVM防御層にgeneric event bus、snapshot copy、publish relayが他featureにも存在する可能性 | application-wide hot-path inventoryをcurrent-onlyで閉じ、実在する同型問題をgrouped fix | `F5` |
| `SAFE-01` | `SAFETY_REQUIRED` | normal-library refreshのnon-blocking producerで既知deadlockは解消 | sync UI wait／callback-under-lockを復活させない。rapid reentry／shutdown testを維持 | all |
| `MANUAL-01` | final user verification | production dataはdevelopment environmentにない | `F6`後、final artifactで一覧、startup、estimation、scanを一度確認 | user |
