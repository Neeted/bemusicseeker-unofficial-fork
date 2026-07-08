# REF-MVP-C43 LR2 Sync Input Builder Lane Closure

作成日: 2026-07-07

## 決定

LR2 sync input builder lane は C42 で閉じる。

C42 後の `CreateLr2SongDbSyncInput` は root-state snapshot 採取、mutable cache / prepared surface selection、app-managed output scope 採取、timing stopwatch の生成、`Lr2SongDbSyncInputBuilder` 呼び出しに収まっている。

`Lr2SongDbSyncInputBuilder` は normal directory metadata target、LR2 folder candidate、directory target、folder info、directory entry、text file directory、input surface log、final input factory composition を所有している。現時点で、さらに 1 ticket として切るべき明確な builder ownership cleanup はない。

次の Lane C 実装は `REF-MVP-C44: maintenance hydration coordinator seam` とする。

## BMSLibrary に残す境界

- private `CreateLr2SongDbSyncInput` entry point と全体 timing。
- `_BMSFiles` / storage row version を reader lock 内で読む row snapshot capture。
- `Settings.Default`、LR2 root / custom output registry、builtin folder setting を読む settings / root snapshot capture。
- scan surface / prepared surface / file-diff freshness の mutable cache と generation 判定。
- app-managed output scope の DB read、incomplete warning、custom output registry boundary。
- queue / run / status / cancel / reservation / mutation block。
- DB schema、setting name、serialized/public surface。

## 次 workflow の判断

`BMSLibrary.cs` の次の extraction 候補から、LR2 sync input builder aftermath review を外す。

次は maintenance を第一候補にする。

理由:

- P0-02 の目標アーキテクチャで maintenance workspace / coordinator が明示されている。
- `maintenance_hydration` は queue / worker lifecycle / result apply / resource health dispatch の境界が見えており、folder / file operation より slice を切りやすい。
- package install follow-up は既に複数 coordinator seam が入っており、次の大きな責務削減としては maintenance の方が効果が高い。

## C44 の範囲

`REF-MVP-C44: maintenance hydration coordinator seam` では、`QueueDeferredMaintenanceHydration`、`CompleteMaintenanceHydrationForShutdown`、`ProcessDeferredMaintenanceHydrationRequests` の queue / worker lifecycle を coordinator seam へ移す。

`BMSLibrary` は public observable state、startup task reporting、shutdown skip、scheduler bridge、`HydrateMaintenanceTable` 実行、`ApplyMaintenanceHydrationResult` 実行を host として提供する。

C44 では `ApplyMaintenanceHydrationResult` の内部、resource health index mutation、maintenance DB schema、warning projection、`installable_maintenance_deferred` は移さない。

## 確認

- production code は変更していない。
- 静的レビューで重大な指摘はなかった。
