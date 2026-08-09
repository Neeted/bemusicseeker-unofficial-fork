# Chart Info Lifecycle

この資料は、所持譜面の `chart_info` currentness、起動時 hydration、parse failure、session index、backfill の現行契約の正本である。parser の入力互換性は [chart-info-parser-compatibility-notes.md](chart-info-parser-compatibility-notes.md)、file read の orchestration は [chart-file-read-pipeline.md](chart-file-read-pipeline.md) を参照する。

## Identity

- BMS は `song.hash` の MD5 と `chart_digest_map` の SHA-256 を結び、`chart_info.sha256` を参照する。
- BMSON は `bmson_song.sha256` を第一 identity とし、`chart_digest_map` を作らない。
- `chart_info` は metadata cache でもあるため、現在の所持 catalog に owner がない row も hydration や backfill だけを理由に削除しない。
- storage owner は BMS の `song` row または BMSON の `bmson_song` rowであり、runtime `ChartInfo` object は attach しない。表示は session index と projection provider から解決する。

## Currentness

単一 owner の分類優先順位は次のとおりである。

1. identity が一致し、current parser version の `chart_info`
2. current parser version かつ現在の timeout 条件を満たす `chart_info_parse_failure`
3. parse candidate

current `chart_info` と current parse failure が併存する場合は `chart_info` を採用する。stale parser version の row、または現在の parse timeout より短い timeout で記録された failure は current としない。

この分類と parse result mapping の正本は `ChartInfoBuildService.EvaluateSnapshot(...)` である。caller は、既に読んだ単一 `ChartFileSnapshot`、owner identity、事前に取得した current row、current failure の有無を渡す。evaluator 自身は file read、DB query、DB writeを行わず、次のいずれかを返す。

- current row を適用する結果
- current failure により parse を省略する結果
- snapshot bytes を current parser で解析した成功または failure 結果
- snapshot が得られず解析できない unavailable 結果

inline file diff / package install、full backfill、LR2 `song_rows` は同じ evaluator を使う。各経路が異なるのは、snapshot と currentness facts の準備、結果の staging、transaction ownership だけであり、parser、優先順位、failure message normalization を分岐させない。

## Startup Hydration

LR2 linked と standalone は同じ actual-data hydration を使う。read-only loader が実在する current `chart_info` と current parse failure を取得し、owned chart summary と照合して session index と candidate count を作る。この read phase は schema ensure や hidden writeを行わず、install readiness を同期 block しない。

`lr2_song_db_sync_status` は LR2 generated `song` / `folder` data sync の開始、再開、完了判定だけに使う。`Completed` status や file-diff の無変更状態は `chart_info` の行の現存または完全性を証明しない。したがって、両 profile とも current info と current failure がない owned chart は同じ backfill candidate になる。

## Session All-current Snapshot

actual-data hydration が全 owner を current info または current failure と分類した場合、owner collection、BMS/BMSON storage row、parser version、timeout versionを含む `ChartInfoHydrationAllCurrentSnapshot` を記録する。同じ session でこれらが変わらない間だけ、後続の candidate summary と full backfill を `hydration_all_current` として省略できる。

この snapshot は実データ照合後の mode-neutral optimization であり、LR2 sync status から合成しない。owner/storage/parser/timeout の変更時は無効化する。parse failure の明示削除については、削除だけで即時 parse を queue せず、同一 session の snapshot を無効化して次回の明示 hydration/backfill または次回起動で actual data を再判定する。

## Failure Contract

hydration DB read が失敗した場合は all-current を合成せず、失敗をログへ残す。current `chart_info` がなければ、current failure の削除後は次回判定で candidate へ戻る。current `chart_info` がある場合は、failure を削除しても info 優先順位により再解析しない。

parse timeout、parser exception、最終 parse failure は evaluator が同じ result mapping と bounded message normalization を適用する。digest を計算できた failure は `chart_info_parse_failure` の永続化候補を返し、成功時は同じ MD5 の failure を削除する候補を返す。DB write と削除の transaction は各 orchestration owner が管理する。

## Related Specifications

- [startup-initialization-flow.md](startup-initialization-flow.md)
- [data-and-indexes.md](data-and-indexes.md)
- [lr2-song-db-generation.md](lr2-song-db-generation.md)
- [chart-file-read-pipeline.md](chart-file-read-pipeline.md)
- [chart-info-parser-compatibility-notes.md](chart-info-parser-compatibility-notes.md)
