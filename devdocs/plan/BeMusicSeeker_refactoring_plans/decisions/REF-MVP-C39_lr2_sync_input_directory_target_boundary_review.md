# REF-MVP-C39 LR2 Sync Input Directory Target Boundary Review

作成日: 2026-07-07

## 決定

Lane C は `REF-MVP-C40: LR2 sync input directory target selection builder ownership` に進む。

C38 後に builder constructor に残る実質 delegate は `CreateLr2SongDbSyncDirectoryTargetSelection` だけである。この selection は `directoryMetadataTargets`、LR2 folder candidates、root/settings snapshot だけで完結しており、`BMSLibrary` の可変状態や DB 境界を直接読まない。

そのため、次は directory target selection を builder / internal helper 側へ移し、builder constructor から最後の selection delegate を外す。

## 理由

- C38 により folder candidate selection は builder ownership になった。
- directory target selection は pure-ish composition であり、root orchestration に残す理由が薄い。
- `CreateLr2FolderPhysicalParentDirectoryMetadataTargets` は `PrepareLr2FolderParentDirectoryEntrySurface` からも使われているが、workflow ではなく physical parent directory target policy なので dedicated internal helper へ移すのが自然である。
- 既存 `Lr2SongDbSyncInputSurfaceHelper` は surface merge / overlay / normalization が中心であり、physical parent directory target policy とは別ファイルにした方が責務が読みやすい。

## C40 の範囲

- `Lr2SongDbSyncInputBuilder` が directory target selection を直接作る。
- builder constructor から `CreateLr2SongDbSyncDirectoryTargetSelection` delegate を削除する。
- `CreateLr2FolderPhysicalParentDirectoryMetadataTargets` とその private helper を `BmsLibraryInternal` の dedicated helper へ移す。
- `PrepareLr2FolderParentDirectoryEntrySurface` は `BMSLibrary` 側 workflow として残し、移した helper を呼ぶだけにする。

## C40 で触らないもの

- app-managed output scope 採取。
- row/root/settings/scan/prepared snapshot 採取。
- `PrepareLr2FolderParentDirectoryEntrySurface` 自体の workflow 移動。
- queue / run / status / cancel / reservation / mutation block。
- DB write / chart_info projection / UI warning dispatch。
- DB schema、setting name、serialized/public surface、private `CreateLr2SongDbSyncInput` entry point。

## C40 完了後

C40 完了後は `CreateLr2SongDbSyncInput` から builder に渡す selection delegate がなくなる。

その時点で LR2 sync input builder lane を続けるか、Lane C の別 workflow、例えば maintenance、folder / file operation、package install follow-up へ移るかを再評価する。
