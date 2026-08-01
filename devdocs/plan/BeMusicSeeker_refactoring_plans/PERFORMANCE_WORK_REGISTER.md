# Performance Work Register

[性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [現在地](./PLAN_STATUS.md) / [current evidence](../../acceptance/net10-performance-engineering.md)

このregisterはcurrent-onlyである。完了した個別unit／commit履歴は残さない。

| ID | Classification | Current evidence | Required closure | Owner |
|---|---|---|---|---|
| `UI-01` | protected completion | summary cache再訪約32 ms、detail約129 ms、full library約23～60 ms。stable source／atomic commit／data-only invalidationが成立 | startup変更で退行させない | all |
| `START-READINESS` | protected completion | folder refresh、optional maintenance、global ThreadPool tuningはrequired readinessから分離済み。専用lane、coalescing、shutdown drainを保護する。S5 structural／eventual-apply／shutdown検証済み | startup変更でrequired readinessを再びgateしない | all |
| `PERF-03` | protected completion | cold-start engineering gateはS1～S5で完了。locked restore、full test、selected publish、existing-data、update／rollback、Release executable UI smokeを通過 | PC再起動後のbundle-r2r／folder-r2r比較はMANUAL-01で一回実施 | user |
| `DIST-01` | fallback | S4 snapshot `c5c5a47366ff446f6aafcbca47756416cb870f39`からbundle-r2r（24 files）とfolder-r2r（505 files）の同一設定artifactを再生成済み。PC再起動後のcold comparisonは未評価 | MANUAL-01で一回比較し、必要なら一括profile切替 | S4／user |
| `SAFE-01` | `SAFETY_REQUIRED` | normal refresh deadlockはnon-blocking producerで解消 | sync UI wait／callback-under-lockを復活させない | all |
