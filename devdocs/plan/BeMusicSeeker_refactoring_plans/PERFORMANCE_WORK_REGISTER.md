# Performance Work Register

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [現在地](./PLAN_STATUS.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

このregisterはcurrent-onlyである。完了した個別unit、commit、review履歴は残さない。

| ID | Classification | Current evidence | Required closure | Owner unit |
|---|---|---|---|---|
| `UI-02` | `DIRECT_FIX` | stable versioned summary sourceへ移行し、cached same-version revisitはsource交換、reset、selection restoreを行わない | final artifactの実データ確認 | `F6`／user |
| `UI-03` | `DIRECT_FIX` | summary data resetとcross-mode data-only source applyはcolumn layout snapshotを維持し、cell-cache invalidationを一回にする | final artifactの実データ確認 | `F6`／user |
| `UI-04` | `DIRECT_FIX` | rows／schema／selection／operation context／visible modeをterminal commitし、old detail sourceはownership transfer後にretireする | final artifactの実データ確認 | `F6`／user |
| `HOT-01` | `LIKELY_OPTIMIZATION` | startup直前のforced GCを退役し、library progressはlatest immutable snapshotを一つのUI notificationでcoalesceする | final artifactの実データ確認 | `F6`／user |
| `HOT-02` | `LIKELY_OPTIMIZATION` | degree=1のinstall estimationはsequential pathを使い、scan hash canonicalizationとBMSON continuation探索の既存single-pass contractを維持する | synthetic／golden behaviorをF6で再確認 | `F6` |
| `APP-01` | `DIRECT_FIX`／`LIKELY_OPTIMIZATION` | MVVM防御層にgeneric event bus、snapshot copy、publish relayが他featureにも存在する可能性 | application-wide hot-path inventoryをcurrent-onlyで閉じ、実在する同型問題をgrouped fix | `F5` |
| `SAFE-01` | `SAFETY_REQUIRED` | normal-library refreshのnon-blocking producerで既知deadlockは解消 | sync UI wait／callback-under-lockを復活させない。rapid reentry／shutdown testを維持 | all |
| `MANUAL-01` | final user verification | production dataはdevelopment environmentにない | `F6`後、final artifactで一覧、startup、estimation、scanを一度確認 | user |
