# P1-01 BMSPlaylist 責務分割計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 位置づけ

`BMSPlaylist.cs` は 9,748 行あり、プレイリスト DB 永続化、外部テーブル同期、beatoraja BMT 出力、LR2 custom folder 出力、推定表、URL 補完、更新差分判定を抱えている。

ただし `BMSPlaylist` は `BMSLibrary` の owned chart / playlist reference / LR2 song.db sync と強く結合しているため、詳細な実装プランは [BMSLibrary ドメイン facade 化計画](./P0-02_BMSLibrary_ドメインFacade化計画.md) の主要 state / mutation 境界が整った後に再検討する。

現時点では、先行しても安全な分割方針と中粒度 ticket だけを定義する。

## 現状の責務

| 領域 | 例 |
|---|---|
| プレイリスト保持 | `BMSTables`, reload, entry hydration, diff detection |
| DB 永続化 | header / entry persistence, hash / fingerprint, migration |
| 外部表同期 | HTTP fetch, table parse, reload target, concurrency |
| beatoraja BMT | BMT export queue, hash output mode, sorting |
| LR2 custom folder | output plan, repair, migration, filesystem materialization |
| 推定表 | easy / normal / hard / fc recommended table |
| URL 補完 | `BMSPlaylist.UrlCompletion.cs` と関連 |
| UI 通知 | operation notification scope |

## 目標アーキテクチャ案

```text
BMSPlaylist                         // facade / public compatibility API
├─ PlaylistRepository                // SQLite persistence / transaction
├─ PlaylistEntryHydrationService
├─ PlaylistReloadService
│  ├─ PlaylistContentDiffService
│  └─ PlaylistReloadPersistenceDecisionService
├─ ExternalPlaylistSyncService
├─ PlaylistUrlCompletionService      // existing partial を整理
├─ CustomFolderOutputWorkspace
│  ├─ CustomFolderOutputPlanner
│  ├─ CustomFolderOutputRepairPlanner
│  ├─ CustomFolderOutputMaterializer
│  └─ CustomFolderOutputMigrationService
├─ BeatorajaBmtExportCoordinator
├─ RecommendedTableBuilder
└─ PlaylistOperationNotificationSink
```

## 先行可能な ticket

### Ticket PL-0A: source-text / reflection test 棚卸し

作業:

1. `BMSPlaylist.cs` を直接読む test を一覧化する。
2. private reflection があれば分類する。
3. `ReadBmsPlaylistSourceText()` helper を追加する。

受け入れ条件:

- partial 分割で test が壊れない。

### Ticket PL-1A: partial 分割だけを行う

移動先候補:

```text
BeMusicSeeker/Models/Playlist/BMSPlaylist.Core.cs
BeMusicSeeker/Models/Playlist/BMSPlaylist.Reload.cs
BeMusicSeeker/Models/Playlist/BMSPlaylist.ExternalSync.cs
BeMusicSeeker/Models/Playlist/BMSPlaylist.CustomFolderOutput.cs
BeMusicSeeker/Models/Playlist/BMSPlaylist.BeatorajaBmt.cs
BeMusicSeeker/Models/Playlist/BMSPlaylist.EstimationTables.cs
BeMusicSeeker/Models/Playlist/BMSPlaylist.Persistence.cs
BeMusicSeeker/Models/Playlist/BMSPlaylist.NestedTypes.cs
```

namespace は `BeMusicSeeker.Models` のまま維持する。

受け入れ条件:

- 挙動差分なし。
- `BMSPlaylist` public API 差分なし。
- line count と責務の塊が見える。

### Ticket PL-1B: `PlaylistContentDiffService` を抽出する

対象:

- `ComparablePlaylistEntryRow`
- `PlaylistContentDiffResult`
- playlist entry fingerprint / diff 判定
- `PlaylistReloadPersistenceDecision`

作業:

1. playlist row comparison を pure service にする。
2. reload persistence decision を pure service にする。
3. 既存 `BmsPlaylistUpdateTests` から diff / decision test を移す。

受け入れ条件:

- reload の DB 書き込み要否判定が単体 test できる。
- external table fetch や file system に依存しない。

### Ticket PL-1C: operation notification を外部 sink 化する

現状 `BMSPlaylist` 内に AsyncLocal の operation notification scope がある。View / dialog と domain の境界を明確にするため、通知 sink と scope を切り出す。

新規候補:

```text
PlaylistOperationNotification
PlaylistOperationNotificationScope
IPlaylistOperationNotificationSink
```

受け入れ条件:

- `BMSPlaylist` static scope 依存が減る。
- UI 表示方法を playlist domain から切り離せる。

## BMSLibrary 完了後に詳細化する ticket

### PL-2: CustomFolderOutputWorkspace

詳細化条件:

- BMSLibrary の LR2 song.db sync coordinator と custom folder physical surface の所有者が決まっていること。

検討内容:

- custom folder output plan / repair / migration / materialization を service 化。
- LR2 folder DB writer との責務境界を整理。
- file system mutation failure と rollback 方針を明確化。

### PL-3: ExternalPlaylistSyncService

詳細化条件:

- MainWindowViewModel の playlist workspace と BMSLibrary playlist reference coordinator が整理されていること。

検討内容:

- HTTP fetch / parse / diff / persist / BMT export queue を pipeline 化。
- cancellation / progress / concurrency を外出し。

### PL-4: BeatorajaBmtExportCoordinator

詳細化条件:

- `BmtTableExportService` と `BMSPlaylist` 内 BMT 出力ロジックの境界が確認できていること。

検討内容:

- BMT sort key / hash resolver / output queue を coordinator 化。
- settings snapshot と file output を分離。

## 関連 test

```powershell
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~BmsPlaylistUpdateTests|FullyQualifiedName~BmsPlaylistExternalLoadTests|FullyQualifiedName~PlaylistReloadMergeTests|FullyQualifiedName~PlaylistSchemaMigrationTests|FullyQualifiedName~BmtTableExportServiceTests|FullyQualifiedName~PlaylistUrlCompletionTests"
```

## 完了目標

- `BMSPlaylist` は public facade と playlist aggregate root に近づく。
- external sync / custom folder / BMT export / persistence が個別に test できる。
- BMSLibrary との依存は coordinator / interface を通す。
