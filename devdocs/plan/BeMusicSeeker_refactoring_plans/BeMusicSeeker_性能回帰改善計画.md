# BeMusicSeeker .NET 10 性能・起動境界の維持方針

[current evidence](../../acceptance/net10-performance-engineering.md) / [distribution](../../acceptance/net10-distribution-performance.md) / [現在地](./PLAN_STATUS.md)

## 完了判定

MVVM / owner整理、.NET 10移行、既知deadlock修正、一覧遷移改善、cold-start readiness修正、Self-contained配布形式選定は一段落とする。

今後はactive implementation planを持たず、次のinvariantを保護する。

## 維持するinvariant

- chart install readinessはoperable前に完成させる。
- local playlist entriesはrequired initializationに含める。
- optional folder tree、network、audit、export、prewarmをglobal operabilityへ接続しない。
- required workとpost workを別markerで観測する。
- post workはoperable直後から進め、単にtimer外へ移しただけにしない。
- sort prewarmはoptional / on-demand fallbackとする。
- stable list source、atomic presentation、data-only invalidationを維持する。
- model lock保持中の同期UI waitを復活させない。
- main distributionはbundle-r2rとする。

## 明示した割り切り

`startup_initialization_complete`はrequired local initializationの完了であり、自動外部同期、playlist reference enrichment、custom-folder physical audit、external catalog、export、sort prewarmの完了を保証しない。これらの完了は`startup_post_initialization_maintenance_complete`で表す。

製品要件としてautomatic external sync完了をユーザーへ明示する必要が出た場合は、core initializationをnetwork待ちへ戻さず、独立したonline synchronization statusを追加する。
