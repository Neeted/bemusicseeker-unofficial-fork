# P2-01 DB / LR2 永続化境界整理 後続検討

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 位置づけ

SQLite / LR2 song.db / folder.db / score.db / playlist DB へのアクセスは、`BMSLibrary`, `BMSPlaylist`, `BmsLibraryInternal` の複数 service に広がっている。これは .NET 10 移行時の SQLite library 差し替え、schema migration、transaction boundary 調整のリスクになる。

ただし DB 境界は `BMSLibrary` と `BMSPlaylist` の state / mutation 分割後でないと正確に切れないため、現時点では後続検討に留める。

## 詳細計画を検討する条件

- [P0-02 BMSLibrary ドメイン facade 化計画](./P0-02_BMSLibrary_ドメインFacade化計画.md) の mutation / LR2 song.db sync / package install coordinator が完了している。
- [P1-01 BMSPlaylist 責務分割計画](./P1-01_BMSPlaylist_責務分割計画.md) の persistence / external sync 境界が見えている。
- `.NET 10` dry-run blocker report で SQLite / System.Data / native sqlite の問題が分類されている。

## 方向性

- `BmsLibraryDbGateway` を中心に repository / transaction script を整理する。
- SQL string を集約し、schema version / migration の責務を明確にする。
- LR2 互換 DB と BeMusicSeeker 独自 DB を型で分ける。
- SQLite library 置換は独立 ticket にし、DB schema 変更と混ぜない。

## 後続計画で扱う候補

- `ILr2SongDbGateway`
- `IPlaylistDatabaseGateway`
- `ILr2FolderDbGateway`
- `IScoreDbGateway`
- `DatabaseTransactionRunner`
- `SchemaMigrationService`
- `SqliteNativeDependencyResolver`

## 現時点でやらないこと

- DB schema 名や column 名を変更しない。
- SQLite library を勢いで置換しない。
- LR2 互換挙動を unit test なしに変えない。
