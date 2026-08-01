# Performance Work Register

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [現在地](./PLAN_STATUS.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

このregisterはcurrent-onlyである。完了した個別unit／commit履歴は残さない。

| ID | Classification | Current evidence | Required closure | Owner |
|---|---|---|---|---|
| `UI-01` | protected completion | summary cache再訪約32 ms、detail約129 ms、full library約23～60 ms。stable source／atomic commit／data-only invalidationが成立 | startup変更で退行させない | S5 |
| `START-01` | `READINESS_DEFECT` | cold runで`startup_ready_ui`→`startup_ready_operable`が65,496 ms。warmは278～345 ms | operability／schedulerをfolder-tree completionから分離 | S1 |
| `START-02` | `OBSERVABILITY_SUPPORT` | folder cache本体は119 msだがrequest→applyの内訳がない | worker、reader wait、snapshot、Dispatcher waitをcorrelate | S1／S3 |
| `START-03` | `DIRECT_FIX` | schedulerはoperable時にのみ開始し、cold runで全11 taskの開始が65秒遅延 | required readiness後に即start | S1 |
| `START-04` | `LIKELY_OPTIMIZATION` | custom-folder repairはpending 0でも335,550 entriesを確認し約6～7秒。network／exportもscheduler idleをgate | required completionとpost-maintenanceを分離 | S2 |
| `START-05` | risk | `ThreadPool.SetMinThreads(200, 200)`がglobal tuningとして残る | dependency修正後に根拠を再評価し、不要なら退役 | S3 |
| `DIST-01` | fallback | previous non-reboot testでfolder-r2rとbundle-r2rは実用上同等。cold rebootは未評価 | 同一HEADの二artifactと一回manual decision | S4／user |
| `SAFE-01` | `SAFETY_REQUIRED` | normal refresh deadlockはnon-blocking producerで解消 | sync UI wait／callback-under-lockを復活させない | all |
