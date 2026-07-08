# REF-MVP-C34 LR2 Sync Input Builder Extraction Decision

作成日: 2026-07-07

## 決定

Lane C は `REF-MVP-C35: LR2 sync input builder extraction` に進む。

次は service extraction ではなく builder extraction とする。

`Lr2SongDbSyncInputBuilder` を `BmsLibraryInternal` に追加し、`CreateLr2SongDbSyncInput` の orchestration を builder へ移す。ただし BMSLibrary が採取する root-state snapshot / mutable cache boundary は移さない。

## 理由

- `CreateLr2SongDbSyncInput` は C18-C33 により、snapshot / selection helper を順に呼び、log と final input factory へ渡す orchestration に整理された。
- `Lr2SongDbSyncService.Run` は既に request input で動く内部 service として存在しており、次に必要なのは service 増設ではなく input composition の builder 化である。
- builder extraction にすると、root state 採取と pure-ish composition の境界を分けられる。
- full service extraction は、root state / concurrency / mutable cache を誤って移すリスクが高い。

## C35 の範囲

- `Lr2SongDbSyncInputBuilder` を `BmsLibraryInternal` に追加する。
- `CreateLr2SongDbSyncInput` の orchestration を builder へ移す。
- BMSLibrary は次の snapshot / selection / scope を採取して builder に渡す。
  - `Lr2SongDbSyncInputRowSnapshot`
  - `Lr2SongDbSyncInputRootSnapshot`
  - `Lr2SongDbSyncInputSettingsSnapshot`
  - `Lr2SongDbSyncScanSurfaceSelection`
  - `Lr2SongDbSyncPreparedSurfaceSelection`
  - `Lr2SongDbSyncAppManagedOutputScope`
- 既存 log formatter と `Lr2SongDbSyncInputFactory` は使い続ける。
- `CreateLr2SongDbSyncInput` の private reflection entry point は維持する。

## C35 で触らないもの

- `CreateLr2SongDbSyncInputRowSnapshot`
- `CreateLr2SongDbSyncInputRootSnapshot`
- `CreateLr2SongDbSyncInputSettingsSnapshot`
- `CreateLr2SongDbSyncScanSurfaceSelection`
- `CreateLr2SongDbSyncPreparedSurfaceSelection`
- `CreateLr2SongDbSyncAppManagedOutputScope`
- queue / run / status / cancel / reservation / mutation block
- DB write / chart_info projection / UI warning dispatch
- DB schema、setting name、serialized/public surface

## BMSLibrary に残す state / concurrency 境界

- sync 実行状態、予約、mutation block、cancel token、progress / property notification。
- host adapter の lock 下 snapshot / reservation。
- scan surface、prepared surface、file-diff freshness の mutable cache と世代管理。
- `_BMSFiles` と storage / owned collection version を reader lock 下で同時に読む row snapshot。
- current 判定と freshness skip 判定。
- chart_info hydration / projection と UI warning dispatch。

## C35 完了後

C35 完了後に `CreateLr2SongDbSyncInput` の root 側残存責務を再確認する。

builder が安定した場合は、Lane C を別 workflow、例えば maintenance、folder / file operation、package install follow-up へ移すことを検討する。
