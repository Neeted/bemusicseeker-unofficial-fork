# Data And Indexes

この資料は、現行実装で正本として扱うデータと索引をまとめる。

## Catalog

所持 catalog は BMS と BMSON を含む。

- BMS
  - LR2 互換の `song` / `folder` を基礎にする。
  - SHA-256 などの拡張情報は app 側の補助 table / map で扱う。
- BMSON
  - `bmson_song` を app 側 catalog として使う。
  - BMS と同じ LR2 再生 capability を持つとは扱わない。
- digest / metadata
  - `chart_digest_map` は chart identity と chart_info hydration の橋渡しに使う。
  - `chart_info` と current parse failure は startup background hydration で memory owner / session index へ適用する。

## Startup DB Projection

install readiness の critical path では、導入先推定に必要な catalog projection だけを読む。

- path / hash / timestamp / folder / installed membership。
- chart digest / bmson catalog。
- maintenance 全件と chart_info 全件は critical path に戻さない。

詳細は [startup-initialization-flow.md](startup-initialization-flow.md) を参照する。

## Resource Index

通常起動の destination resource index は file enumeration から作る。

- 正本:
  - `LibraryResourceIndex`
  - `DirectoryResourceLookupCache`
- key semantics:
  - chart-relative resource key。
  - `foo.wav` は `foo`。
  - `sound/foo.wav` は `sound/foo`。
  - basename-only matching は使わない。
- category:
  - audio
  - image
  - movie
- reverse lookup:
  - resource-key -> candidate chart directory。
  - install readiness 前に完成している。
  - pending package batch 側へ lazy build を持ち越さない。

native bridge path では `EBridge_ScanChartAndResources` の packed result から直接 resource index を作る。`BmsScanResult` は chart paths / chart directories の carrier として使い、resource dictionaries は通常起動 main path では materialize しない。

Everything unavailable 時の managed fallback scan とテスト用 merge path では、`BmsScanResult` が category 別 resource dictionary を持つ。

## Resource Ownership

resource ownership は chart-directory keyed に再集約する。

- aggregate ownership
  - ancestor chart directory から見える resource を含む。
- self-only ownership
  - 最も近い chart directory が所有する resource だけを含む。

health / install estimation / maintenance は category 別 chart-relative key を使う。旧 all-resource union や basename-only fallback は正本にしない。

## Maintenance And Resource Health

`BMSFile.maintenanceInfo` は常に valid snapshot とは限らない。

- valid snapshot:
  - DB 由来の `DbHydrated`
  - file diff / install / manual rescan 由来の `Calculated`
- placeholder:
  - 起動直後や未 hydration owner の暫定値。
  - resource health index の正本として扱わない。

通常起動では全譜面の resource file existence を再検証しない。persisted maintenance snapshot を hydration し、必要な missing/stale target だけ deferred maintenance で補完する。

## Playlist And Score Data

- playlist header は startup early phase で読む。
- playlist entries は startup background task `playlist_entries_hydration` で読む。
- score DB load は startup early phase で行い、LR2ID 確定後に LR2IR player score XML prefetch を開始する。
- ranking refresh / score hydration は install readiness blocker ではない。

LR2 ranking 系は 2 table に分かれる。

- `ir_score`
  - LR2IR player score XML 由来。
  - 未送信検出と `UNSENT SONGS` に使う。
  - normalized digest は LR2IR XML の score 実体を対象にし、hash 側更新時刻として揺れる `lastupdate` は無視する。
- `ir_data`
  - LR2IR local ranking cache XML 由来。
  - ranking 表示と offline score ranking estimation に使う。
  - XML reload は hash cache file の mtime / tail `lastupdate` で判定する。
  - startup refresh、manual download、`LR2IRCache` wrapper は同じ ranking cache XML parser を使う。
  - refresh path は full ranking list materialize を避け、valid `<score>` rows を 1 pass summary parse する。`id`、`clear`、`notes`、`combo`、`pg`、`gr`、`minbp` は 0 以上の整数だけを valid とし、不正 row は集計対象から外す。
  - offline score ranking estimation は、必要時だけ同じ parser の compact rank calculator を on-demand load する。startup refresh で reload 済みの hash はその lookup を再利用する。
  - 初回構築では対象 LR2ID の既存 row が DB 上も 0 件であることを transaction 内で確認し、dedupe 済み rows を bulk insert する。incremental 更新は従来通り `(hash, lr2id)` 単位の delete + insert upsert を使う。
  - schema 互換のため unique 制約は持たない。index は既存 `ir_data_idx(lr2id)` に加え、非 unique `ir_data_idx_lr2id_hash(lr2id, hash)` を持つ。

## DB Access

startup hydration の read phase は read-only connection を使う。

- read-only loader は schema ensure / migration / repair を行わない。
- write が必要な cleanup / backfill / metadata update / file diff commit は write-capable transaction path に分ける。
- `bmson_app_schema` などの migration 状態は startup migration phase で収束させる。

bmson startup preflight は、警告が必要な migration と警告不要の初回準備を分ける。

- warning target:
  - 既存 `playlist_entry` に `sha256` column / index を追加する。
  - 既存 `playlist_entry_idx_uniq` を `sha256` 込みへ作り直す。
  - 既存 `bmson_app_schema` version row を更新する。
  - version row が無い状態で既存 `chart_digest_map` / `bmson_song` があり、app-owned schema/data を現行化する。
- no-warning startup preparation:
  - LR2 `song.db` に playlist tables が無く、初回連携用に追加する。
  - `chart_digest_map` / `bmson_song` / `app_schema_version` が無く、初回連携用に追加して current version を記録する。

`EnsureBmsonStartupSchema()` は no-warning preparation 用で、schema/index ensure と `bmson_app_schema` current version stamp だけを行う。既存 app-owned data の digest consistency migration が必要な場合は `CompleteBmsonStartupMigration()` を使う。

## Consistency Updates

file diff / install / merge / delete / move 後は、必要な範囲で次を同期する。

- memory catalog
- LR2 song DB / app extension tables
- `bmson_song`
- `chart_digest_map`
- inline `chart_info`
- inline or deferred maintenance snapshot
- `LibraryResourceIndex`
- `DirectoryResourceLookupCache`
- playlist references when affected

増分更新では、旧 union cache ではなく `DirectoryResourceLookupCache` の directory key set を正本にする。
