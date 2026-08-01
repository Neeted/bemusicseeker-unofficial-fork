# Historical net472 / .NET 10 Symptom Note

[current evidence](./net10-performance-engineering.md) / [性能計画](../plan/BeMusicSeeker_refactoring_plans/BeMusicSeeker_性能回帰改善計画.md)

この文書は既存logから得たhistorical symptomだけを残す。net472を再build、再instrument、反復benchmarkする正本ではない。

## Current interpretation

- playlist summary、playlist detail、full libraryの画面遷移は、2026-07-31のfinal .NET 10 logとユーザー体感で問題ない水準まで改善した。
- net472との厳密parityは今後のengineering objectiveにしない。成立した.NET 10 presentation contractを保護する。
- PC起動後初回の`startup_initialization_complete`は.NET 10で約103秒、2回目は約38～39秒という再現性がある。
- net472初回が約40秒だったという観測は優先度を示すが、marker粒度が異なるため厳密なA/B数値には使わない。
- current .NET 10内のstage evidenceでは、cold penaltyは`startup_ready_ui`後のoptional folder-tree readiness edgeに集中する。
