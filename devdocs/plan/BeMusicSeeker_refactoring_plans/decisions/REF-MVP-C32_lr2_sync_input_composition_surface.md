# REF-MVP-C32 LR2 Sync Input Composition Surface Decision

作成日: 2026-07-07

## 決定

`CreateLr2SongDbSyncInput` は、C18-C31 後の時点では root orchestration として許容できる。

次の production code ticket は、full service / builder extraction ではなく `REF-MVP-C33: LR2 sync input prepared surface selection boundary` とする。

C33 では prepared surface selection が返す `PendingSurface` / `ActiveSurface` / prepared flags を dedicated selection DTO のまま formatter / candidate helpers / factory 周辺へ渡し、`CreateLr2SongDbSyncInput` の prepared surface alias を減らす。挙動変更、ログ項目変更、DB schema / setting name 変更、private `CreateLr2SongDbSyncInput` entry point 移動は含めない。

## 理由

- C18-C31 で row / root / settings / scan surface / directory targets / folder candidates / folder info / directory entries / text directories / log timing が snapshot または selection DTO 境界になった。
- `CreateLr2SongDbSyncInput` は現在、root state を直接読む処理よりも、snapshot / selection helper を順に呼び出して最終 input と log を組み立てる orchestration に近い。
- ただし prepared surface はまだ `pendingPreparedSurface`、`preparedSurface`、`hasPreparedSurface`、`hasPreparedLr2FolderSurface` として複数の local alias に展開され、formatter と candidate helpers に個別渡しされている。
- full service / builder extraction に進む前に prepared surface boundary を selection DTO のまま扱うと、次の extraction の引数が安定し、review 単位も小さく保てる。

## まだ root に残すもの

- `GetCurrentLr2SongDbSyncScanSurface` 本体と scan surface snapshot state access は root に残す。理由は lock 下 snapshot 読み取りと root の version / concurrency 境界に依存するため。
- `CreateLr2SongDbSyncInputRowSnapshot` は root に残す。理由は `_BMSFiles`、`rwlockBMSFilesInitializedAll`、`OwnedChartCollectionVersion`、storage row version を同じ reader guard 内で読む必要があるため。
- `CreateLr2SongDbSyncInputRootSnapshot` と `CreateLr2SongDbSyncInputSettingsSnapshot` は当面 root に残す。理由は `getBMSDirectories()`、`Settings.Default`、custom folder output registry、LR2 root path の compatibility boundary に触れるため。
- `TakeLr2SongDbSyncPreparedDataSurface` を伴う prepared surface snapshot consumption は C33 では root helper の中に残す。理由は pending prepared data の消費タイミングを隠さず維持するため。

## 次の ticket

`REF-MVP-C33: LR2 sync input prepared surface selection boundary`

完了条件:

- `LogLr2SongDbSyncInputSurface` / `Lr2SongDbSyncInputSurfaceLogFormatter` が prepared surface selection を直接受け取る。
- folder candidate / folder info / text file directory selection helpers への prepared surface flags は、可能な範囲で prepared surface selection 経由に寄る。
- `CreateLr2SongDbSyncInput` に残る prepared surface local alias が減っている。
- prepared surface consumption timing、ログ項目名、ログ値、selection 順序、DB schema、setting name、private reflection entry point、serialized/public surface は変更しない。

## 後続

C33 完了後に、`CreateLr2SongDbSyncInput` の full builder / service extraction を再評価する。

その時点でまだ root に残すべき stateful helper が多い場合は、service extraction ではなく `CreateLr2SongDbSyncInput` 専用 context / composition DTO を 1 ticket だけ検討する。
