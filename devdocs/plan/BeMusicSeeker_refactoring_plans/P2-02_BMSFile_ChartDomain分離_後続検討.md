# P2-02 BMSFile / Chart domain 分離 後続検討

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 位置づけ

`BMSFile.cs` は 2,498 行で、LR2 song table row、譜面解析結果、hash、encoding detection、resource reference、maintenance warning、UI 表示に近い状態が混ざっている。

ただし `BMSFile` は DB row と互換性が強く、scan pipeline / DB gateway / UI row projection に広く使われている。したがって、詳細計画は次が完了した後に検討する。

- [P0-02 BMSLibrary ドメイン facade 化計画](./P0-02_BMSLibrary_ドメインFacade化計画.md)
- [P1-02 BmsLibraryInitializationService / スキャン Pipeline 分割計画](./P1-02_BmsLibraryInitializationService_スキャンPipeline分割計画.md)
- [P2-01 DB / LR2 永続化境界整理](./P2-01_DB_LR2_永続化境界整理_後続検討.md)

## 方向性

将来的には次の分離を検討する。

```text
BMSFile / LR2SongRow                 // persistence compatibility row
ChartMetadata                         // title, artist, level, mode, bpm, etc.
ChartIdentity                         // md5, sha256, path, resource key
ChartResourceManifest                 // wav/bga/movie/stagefile/banner references
ChartMaintenanceState                 // warnings, encoding, resource health
ChartDisplayProjection                // UI row projection
BmsChartParser                        // BMS format parser
BmsonChartParser                      // bmson parser
```

## 現時点で先行可能なこと

- parser pure functions の test を増やす。
- `ChartFileSnapshot`, `ChartInfoDisplaySnapshot`, `ChartWarning` との重複を調査する。
- `BMSFile` に新しい UI-only property を増やさない。

## 現時点でやらないこと

- `BMSFile : LR2SongDB.song` の継承を急に外さない。
- DB row mapping と domain model 分離を同じ ticket で行わない。
- BMS 形式固有名を無計画に Chart 名へ rename しない。persisted / user-facing / format-specific な名前を分類してから行う。
