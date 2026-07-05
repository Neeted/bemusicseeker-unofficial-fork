# P1-02 BmsLibraryInitializationService / スキャン Pipeline 分割計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 位置づけ

`BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs` は 5,341 行あり、既に `BMSLibrary` から切り出された service ではあるが、内部では DB load、LR2 folder mtime、file diff、parallel read/parse、commit writer、inline maintenance、chart_info inline build、score load が同居している。

詳細な実装プランは [BMSLibrary ドメイン facade 化計画](./P0-02_BMSLibrary_ドメインFacade化計画.md) の `LibraryInitializationCoordinator` が見えた後に調整する。ただし scan pipeline は比較的自己完結しているため、中粒度 ticket は先に定義できる。

## 現状の責務

| 領域 | 例 |
|---|---|
| song table load | raw SQL loader / sqlite-net fallback / chart digest map apply |
| maintenance table load | maintenance raw select / warning state |
| LR2 normal folder mtime | snapshot / diff / folder row update |
| file diff plan | root enumeration / current row comparison / moved file handling |
| read/parse pipeline | blocking collections, parser degree, read queue, post-parse queue |
| commit writer | chunking, db write, user column restore |
| inline chart_info | BMS / bmson inline build |
| score load | score table read |

## 目標アーキテクチャ案

```text
BmsLibraryInitializationService       // facade over initialization phases
├─ SongCatalogLoader
├─ MaintenanceCatalogLoader
├─ Lr2NormalFolderMtimeSnapshotLoader
├─ FileDiffPlanBuilder
├─ ChartFileReadPipeline
├─ FileDiffParsePipeline
├─ FileDiffPostParseProcessor
├─ FileDiffCommitWriter
├─ InlineChartInfoBuildCoordinator
└─ ScoreTableLoader
```

## Ticket INIT-0A: pipeline metrics / tests の現状を固定する

作業:

1. `BmsLibraryInitializationServiceTests` の coverage を確認する。
2. `.tmp/refactor/initialization-pipeline-inventory.md` に pipeline phase と既存 test を対応付ける。
3. parser degree / commit chunk / queue capacity など environment / override を一覧化する。

受け入れ条件:

- 分割前後でどの metrics を維持すべきか分かる。

## Ticket INIT-1A: `SongCatalogLoader` を抽出する

対象:

- `LoadSongTable` の song table materialization 部分
- `UseRawSongCatalogLoader`
- `LoadSongCatalogRaw`
- `NormalizeSongTable`
- `NormalizeStandaloneSongPathCompatibility`

新規候補:

```text
SongCatalogLoader
SongCatalogLoadOptions
SongCatalogLoadResult
```

受け入れ条件:

- raw string loader と sqlite-net fallback が単体 test できる。
- `BmsLibraryInitializationService.LoadSongTable` は orchestration 中心になる。

## Ticket INIT-1B: `FileDiffPlanBuilder` を抽出する

対象:

- current BMS / bmson rows と filesystem entries の比較
- moved file user column restore preparation
- parse target 作成

受け入れ条件:

- file system snapshot と DB snapshot を入力にして parse target list を返す pure-ish service になる。
- 実 filesystem に依存しない test が増える。

## Ticket INIT-1C: read / parse / post-parse pipeline をクラス化する

対象:

- `BlockingCollection` read queue / parsed queue / post-parse queue
- reader tasks / parser tasks / completion / exception propagation
- `PipelineExceptionSignal`
- `FileDiffReadCandidate`, `FileDiffParsedCandidate`, `FileDiffPostParseWorkItem`

新規候補:

```text
FileDiffParsePipeline
FileDiffParsePipelineOptions
FileDiffParsePipelineResult
```

受け入れ条件:

- cancellation / exception / queue completion が pipeline class の test で確認できる。
- `BmsLibraryInitializationService` は pipeline 実行と result apply に集中する。

## Ticket INIT-1D: `FileDiffCommitWriter` を抽出する

対象:

- `FileDiffStreamingCommitContext`
- `FileDiffCommitWriterItem`
- chunk add / wait / writer loop / db commit

受け入れ条件:

- DB commit chunking を単体/統合 test できる。
- writer queue capacity と wait metrics が result に残る。

## Ticket INIT-1E: inline maintenance / chart_info build を分離する

対象:

- `BuildInlineBmsMaintenance`
- `BuildInlineBmsonMaintenance`
- `ProcessInlineBmsChartInfo`
- `ProcessInlineBmsonChartInfo`
- `AttachInlineChartInfoRows`

新規候補:

```text
InlineMaintenanceBuilder
InlineChartInfoBuildCoordinator
```

受け入れ条件:

- chart_info build failure / parse failure warning が既存通り。
- `ChartInfoMetadataTests` / `ChartInfoProductionCompareTests` が通る。

## P0-02 完了後に再検討すること

- `BMSLibrary` 側の initialization progress reporting とこの service の progress result をどう接続するか。
- `BMSLibrary` の lock acquisition を coordinator と service のどちらに置くか。
- file system scan / LR2 DB write / chart_info build を将来的に `Task` / cancellation-friendly にするか。

## 関連 test

```powershell
dotnet test .\BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj /p:Configuration=Release --filter "FullyQualifiedName~BmsLibraryInitializationServiceTests|FullyQualifiedName~ChartDirectoryScanBuilderTests|FullyQualifiedName~ChartFileReadPipelinePolicyTests|FullyQualifiedName~RootFileEnumerationTests|FullyQualifiedName~Lr2NormalFolderDbSyncServiceTests|FullyQualifiedName~ChartInfoMetadataTests"
```

## 完了目標

- initialization service が pipeline facade になり、各 phase を単独で理解/test できる。
- startup / rescan の性能ログが phase 別に維持される。
- 将来の並列度調整、cancellation、.NET 10 移行時の API 差し替えがしやすくなる。
