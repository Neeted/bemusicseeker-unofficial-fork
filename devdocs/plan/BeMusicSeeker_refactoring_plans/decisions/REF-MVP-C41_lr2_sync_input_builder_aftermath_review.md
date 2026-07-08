# REF-MVP-C41 LR2 Sync Input Builder Aftermath Review

作成日: 2026-07-07

## 決定

Lane C は `REF-MVP-C42: LR2 sync input normal directory metadata target builder ownership` に進む。

C40 後の LR2 sync input builder lane はほぼ閉じているが、`CreateLr2SongDbSyncInput` にはまだ `CreateLr2SongDbSyncDirectoryMetadataTargets` と `directoryMetadataTargets` local が残っている。この target selection は `scanSurface`、`rootSnapshot`、`rowSnapshot` だけで完結し、root mutable state / DB boundary を直接読まない。

そのため、次は normal directory metadata target selection を builder ownership へ移してから、LR2 sync input builder lane を閉じるか再評価する。

## 理由

- C40 で directory target selection は builder ownership になった。
- normal directory metadata target は directory target selection の input であり、builder 側へ寄せると target selection boundary が揃う。
- `directoryTargetsMs` の意味は normal directory metadata target 作成時間として維持できる。
- row/root/settings/scan/prepared/app-managed scope 採取は、root-state / concurrency / mutable cache / DB-read warning boundary として `BMSLibrary` に残す理由がある。

## C42 の範囲

- `CreateLr2SongDbSyncDirectoryMetadataTargets` を builder-owned helper へ移す。
- `CreateLr2SongDbSyncInput` から `directoryMetadataTargets` local と helper 呼び出しを外す。
- `directoryTargetsMs` の計測範囲、ログ項目名、ログ順序、ログ値を維持する。

## C42 で触らないもの

- row/root/settings/scan/prepared/app-managed scope 採取。
- `PrepareLr2FolderParentDirectoryEntrySurface`。
- queue / run / status / cancel / reservation / mutation block。
- DB write / chart_info projection / UI warning dispatch。
- DB schema、setting name、serialized/public surface、private `CreateLr2SongDbSyncInput` entry point。

## C42 完了後

C42 完了後に `CreateLr2SongDbSyncInput` と `Lr2SongDbSyncInputBuilder` の boundary を再確認する。

追加の明確な builder ownership cleanup がなければ、LR2 sync input builder lane を閉じて、Lane C の別 workflow、例えば maintenance、folder / file operation、package install follow-up へ移る。
