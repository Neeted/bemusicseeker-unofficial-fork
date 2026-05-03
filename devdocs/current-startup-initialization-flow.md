# 起動・初期化フロー 現行仕様

この資料は、移行前 `song.db` と移行後 `song.db` の両方に対して、BeMusicSeeker が取るべき唯一の起動・初期化フローをまとめる。

方針は「preflight で状態を判定し、必要な migration を起動直後に完了させ、最後に再検査で収束を確認する」ことである。互換のために古い migration 経路を background 処理や `chart_info` backfill の副作用として残さない。

## 正本フロー

```text
Initialize
  -> bmson migration preflight inspect
       read-only
  -> warning required?
       OK: continue
       Cancel: shutdown
  -> startup migration
       BMSPlaylist.EnsureSchema()
       CompleteBmsonStartupMigration() when bmson app schema migration or repair is needed
       EnsureBmsonSchema() when only app-owned schema/index presence must be guaranteed
  -> final preflight inspect
       playlist sha256 migration, bmson app schema migration, repair issue が残れば fail
  -> metadata bundle import
  -> DB load
  -> file enumeration / resource index build
  -> file diff
       new/updated charts are read, parsed, committed, and reflected here
  -> operation ready
  -> startup background tasks
       playlist hydration
       chart_info hydration / full backfill for DB-derived candidates
       installable maintenance for DB-derived missing/stale rows
       score / ranking refresh
```

## bmson Migration

### Preflight

`BmsonMigrationPreflightService.Inspect()` は read-only の判定だけを行う。

| 判定 | 意味 | 起動時の扱い |
| --- | --- | --- |
| `NeedsPlaylistEntrySha256Migration` | playlist entry schema が現行ではない | 警告対象。OK 後に `BMSPlaylist.EnsureSchema()` で移行 |
| `NeedsBmsonAppSchemaMigration` | `app_schema_version(name='bmson_app_schema')` が現行ではない | 警告対象。OK 後に `CompleteBmsonStartupMigration()` で完了 |
| `RepairRequired` | `chart_digest_map` / `bmson_song` / index が欠損または互換外 | 警告不要の修復、または警告後の修復として扱う |

preflight は DB を変更しない。起動時 UI は、この結果だけを見て警告表示と migration 実行の要否を決める。

### Startup Migration API

| API | 責務 | `bmson_app_schema` version |
| --- | --- | --- |
| `BMSPlaylist.EnsureSchema(path)` | playlist / playlist_entry の現行 schema 化 | 書かない |
| `BmsLibraryDbGateway.EnsureBmsonSchema()` | app-owned table / index の作成・修復 | 書かない |
| `BmsLibraryDbGateway.CompleteBmsonStartupMigration()` | bmson startup migration の完了処理。`EnsureBmsonSchema()`、`chart_digest_map` 整合修復、version 書き込みを同一 transaction で行う | 書く |

`EnsureBmsonSchema()` は schema 修復 helper であり、migration 完了印を付ける API ではない。移行前 DB を起動時に current 化する経路は `CompleteBmsonStartupMigration()` だけにする。

### 収束確認

startup migration 後は必ず `Inspect()` をもう一度実行する。

次のいずれかが残る場合、起動 migration は失敗として扱う。

- `NeedsPlaylistEntrySha256Migration`
- `NeedsBmsonAppSchemaMigration`
- `RepairRequired`

これにより、移行前 DB で `bmson_app_schema` が書かれないまま起動を継続し、次回起動でも同じ警告が出る状態を防ぐ。

## chart_info と bmson Migration の分離

`chart_info` hydration / full backfill は、DB 由来の譜面メタデータ補完である。bmson app schema migration の完了印を書いてはいけない。

特に次の経路は正本ではない。

- full backfill 完了時に `bmson_app_schema` を current として mark する。
- no-candidate backfill の skip を migration 完了扱いにする。
- `chart_info` parser version 更新と bmson app schema version を同じ判断で扱う。

bmson app schema の current 判定は preflight と `CompleteBmsonStartupMigration()` の責務で完結させる。

## metadata Bundle Import

metadata bundle は、リリースパッケージ同梱または外部配布の `chart_info` 補助データである。所持譜面から生成された DB 由来の補助情報ではない。

- import タイミングは起動直後に固定する。
- `.db` / `.7z` の探索と import history 判定は、DB load より前に行う。
- import 済み bundle は `imported_metadata/` へ退避し、次回以降の SHA-256 計算と展開を避ける。
- import は bmson migration の代替ではない。bundle の有無にかかわらず、preflight migration は先に収束させる。

## Library Load

`BMSLibrary.Initialize()` の本体は、migration と metadata import が終わった DB を前提に動く。

| Phase | 主な処理 | 備考 |
| --- | --- | --- |
| DB load | `song`, `bmson_song`, `maintenance`, `chart_digest_map` などを読む | 既存 row の正本構築 |
| File enumeration | Everything / fallback で root 配下を列挙し、resource index を作る | 差分なし fast path では譜面本文を読まない |
| File diff | 新規・更新譜面を snapshot read し、軽量 parse、LR2 parent/folder、inline `chart_info`、inline maintenance、chunk commit まで行う | 新規・更新ファイル由来の補助情報はここで処理する |

差分なしの場合は、導入先推定に必要な index を最速で公開し、DB 由来の補助情報は background へ回す。

差分ありの場合は、読んだファイルの近くで DB と memory へ反映し、再起動しないと正しくならない状態を作らない。

## Startup Background Tasks

background task は、既に DB に存在している owner の補助情報を補完するためのものに限定する。

| Task | 正本の責務 |
| --- | --- |
| `chart_info_hydration` | DB の current `chart_info` を memory owner / session index へ適用する |
| full `chart_info` backfill | 旧 DB や外部操作により不足している `chart_info` を補完する |
| `installable_maintenance_deferred` | DB 由来の missing/stale maintenance を補完する |
| score / ranking / playlist hydration | 操作可能後に反映できる DB 由来データを適用する |

新規・更新ファイル由来の `chart_info` と maintenance を background へ押し出さない。

## 実装上の禁止事項

- migration 完了印を `chart_info` backfill の副作用として書かない。
- `EnsureBmsonSchema()` を「migration 完了」とみなさない。
- test からしか呼ばれない旧 migration API を残さない。
- current `chart_info` skip row を file diff result / index delta として大量 publish しない。
- file diff 由来の `WAVfiles` / `BGAfiles` を long-lived model に残さない。
- 起動 critical path の判断を、後続 background task の偶然の完了順に依存させない。

## 関連資料

- `devdocs/current-startup-reload-progress.md`
- `devdocs/current-chart-file-read-pipeline.md`
- `devdocs/empty-db-first-startup-optimization-plan.md`
