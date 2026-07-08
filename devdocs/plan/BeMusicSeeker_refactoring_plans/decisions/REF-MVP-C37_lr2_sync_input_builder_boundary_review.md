# REF-MVP-C37 LR2 Sync Input Builder Boundary Review

作成日: 2026-07-07

## 決定

Lane C は `REF-MVP-C38: LR2 sync input folder candidate selection builder ownership` に進む。

C36 後に残る builder delegate surface のうち、次に移す対象は `CreateLr2SongDbSyncFolderCandidateSelection` とする。

`CreateLr2SongDbSyncDirectoryTargetSelection` は移動可能だが、LR2 folder parent directory target helper が別 workflow の parent directory surface preparation からも使われているため、C38 では触らない。

## 理由

- folder candidate selection は scan surface / prepared surface / app-managed output scope を組み合わせる input composition 本体であり、builder ownership に最も近い。
- root に残すべき責務は selection ではなく、row/root/settings/scan/prepared/app-managed scope の採取である。
- `CreateLr2SongDbSyncAppManagedOutputScope` は DB read / warning log を伴うため、C38 では引き続き `BMSLibrary` に残す。
- directory target selection は parent directory target helper の共有範囲が広く、次 ticket としては横に広がりやすい。

## C38 の範囲

- `Lr2SongDbSyncInputBuilder` または dedicated internal helper が folder candidate selection を直接作る。
- `CreateLr2SongDbSyncFolderCandidateSelection` delegate を builder constructor から外す。
- LR2 folder file candidate discovery に必要な低レベル delegate / log action は残してよい。
- `lr2FolderCandidatesSource` の文字列、prepared merge、app-managed filtering、不完全 scope 時の incomplete candidate、`lr2FolderCandidatesMs` 計測範囲は維持する。

## C38 で触らないもの

- `CreateLr2SongDbSyncAppManagedOutputScope`。
- row/root/settings/scan/prepared snapshot 採取。
- directory target selection。
- queue / run / status / cancel / reservation / mutation block。
- DB write / chart_info projection / UI warning dispatch。
- DB schema、setting name、serialized/public surface、private `CreateLr2SongDbSyncInput` entry point。

## C38 完了後

C38 完了後に `Lr2SongDbSyncInputBuilder` に残る directory target delegate を再評価する。

directory target selection の移動がまだ有効なら、parent directory target helper の共有方針を決めてから 1 ticket だけ切る。明確な責務削減が見込めない場合は、Lane C の別 workflow、例えば maintenance、folder / file operation、package install follow-up へ移る。
