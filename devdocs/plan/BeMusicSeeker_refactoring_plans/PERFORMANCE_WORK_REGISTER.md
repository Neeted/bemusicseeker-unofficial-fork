# Performance Work Register

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [現在地](./PLAN_STATUS.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

このregisterはcurrent-onlyである。完了した個別unit／commit履歴は残さない。

| ID | Classification | Current evidence | Required closure | Owner |
|---|---|---|---|---|
| `UI-01` | protected completion | summary cache再訪約32 ms、detail約129 ms、full library約23～60 ms。stable source／atomic commit／data-only invalidationが成立 | startup変更で退行させない | S5 |
| `START-READINESS` | protected completion | folder refresh、optional maintenance、global ThreadPool tuningはrequired readinessから分離済み。専用lane、coalescing、shutdown drainを保護する | S4／S5でstartup structural／eventual-apply contractを再確認 | all |
| `DIST-01` | fallback | previous non-reboot testでfolder-r2rとbundle-r2rは実用上同等。cold rebootは未評価 | 同一HEADの二artifactと一回manual decision | S4／user |
| `SAFE-01` | `SAFETY_REQUIRED` | normal refresh deadlockはnon-blocking producerで解消 | sync UI wait／callback-under-lockを復活させない | all |
