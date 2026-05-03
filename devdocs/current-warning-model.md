# 現行 WARNING モデル

この資料は、現在の WARNING 表示仕様だけをまとめたものです。移行経緯は `warning-structure-migration-plan.md` を参照してください。

## 基本モデル

- WARNING は主に `BMSFile.Warnings` が保持する `ChartWarning` の集合です。
- 通常一覧と導入済み直後の `新規` 画面の `ResourceHealth` warning は例外的に、cache-aware health 判定で更新した `maintenanceInfo` から構築する runtime `ResourceHealthIndexSnapshot` を `LibraryChartRow` が投影して表示します。DB 永続 warning ではありません。
- 旧来の自由文字列 `warning` は廃止済みで、表示・tooltip・行ハイライトは structured warning から算出します。
- `DisplayWarning` は tooltip と同じ詳細全文、`WarningDigestText` は一覧セル用 digest、`WarningTooltipText` は tooltip 詳細です。
- `HasLowConfidenceInstallWarning` / `HasZeroNoteMismatchWarning` / `IsHashDuplicated` は互換用 property として残っていますが、状態の正本は warning kind の有無です。

## 表示ルール

- digest は `[KindCount] DigestLabel_priority_1st, DigestLabel_priority_2nd` 形式です。
- digest の並び順は `Priority` 昇順です。
- digest label は同一文字列を重複表示しません。
- `KindCount` は現在有効な warning kind 数を数えます。たとえば WAV と BGA が両方不足している場合、label は `リソース不足` 1 つでも `[2] リソース不足` になります。
- tooltip は `ShowInTooltip = true` の warning message を `Priority` 昇順で改行連結します。
- 行ハイライトは、いずれかの warning が `HighlightRow = true` の場合に有効です。
- `ResourceHealth` category の warning は、`instl_dst` が未設定の間だけ digest に出ます。tooltip には導入先設定後も詳細が残ります。
- resource health の一覧所属判定は `BMSFile.Warnings` を mutation せず、`maintenanceInfo` 由来の side-effect-free index で行います。`maintenanceInfo` の resource health は file scan 由来の directory resource index を優先し、必要時のみ実ファイル確認へ fallback します。
- 保留画面の `ResourceHealth` warning は導入前配置を評価するための一時状態です。導入成功時に source `BMSFile.Warnings` から `ResourceHealth` category を消し、導入後の通常一覧・新規画面では `maintenanceInfo` / resource health index から投影します。

## Warning 定義

| Kind | Category | Priority | DigestLabel | HighlightRow | Digest 条件 |
| --- | --- | ---: | --- | --- | --- |
| `NestedChartFileInPackage` | `PackageLayout` | 10 | `サブフォルダ譜面` | false | warning が存在する場合 |
| `ChartInfoParseFailure` | `ChartMetadata` | 15 | `メタデータ解析エラー` | true | warning が存在する場合 |
| `Lr2PathEncodingUnsupported` | `Lr2Compatibility` | 18 | `LR2パス非対応` | true | warning が存在する場合 |
| `ZeroNoteMismatch` | `ChartContent` | 20 | `ゼロノート不整合` | true | warning が存在する場合 |
| `DuplicateChart` | `Duplicate` | 30 | `重複譜面` | true | warning が存在する場合 |
| `InstallEstimationAmbiguous` | `InstallEstimation` | 40 | `推定先複数` | true | warning が存在する場合 |
| `InstallEstimationMetadataMismatch` | `InstallEstimation` | 41 | `TITLE/ARTIST不一致` | true | warning が存在する場合 |
| `InstallEstimationReinstallNotImproved` | `InstallEstimation` | 42 | `再導入改善なし` | true | warning が存在する場合 |
| `InstalledDestinationResolveFailed` | `InstallEstimation` | 43 | `導入先不明` | false | warning が存在する場合 |
| `InstallEstimationLowConfidence` | `InstallEstimation` | 44 | `導入先推定` | true | warning が存在する場合 |
| `AlreadyInstalled` | `InstalledState` | 50 | `既に導入済み` | false | warning が存在する場合 |
| `SingleBmsFile` | `PackageLayout` | 60 | `単体BMS` | false | warning が存在する場合 |
| `SingleBmsonFile` | `PackageLayout` | 60 | `単体BMSON` | false | warning が存在する場合 |
| `ResourceWavMissing` | `ResourceHealth` | 80 | `リソース不足` | false | warning が存在し、`instl_dst` が未設定の場合 |
| `ResourceBgaMissing` | `ResourceHealth` | 80 | `リソース不足` | false | warning が存在し、`instl_dst` が未設定の場合 |
| `ResourceMovieMissing` | `ResourceHealth` | 80 | `リソース不足` | false | warning が存在し、`instl_dst` が未設定の場合 |
| `ResourceStagefileMissing` | `ResourceHealth` | 83 | `画像不足` | false | digest には出さない。tooltip のみ |
| `ResourceBackbmpMissing` | `ResourceHealth` | 83 | `画像不足` | false | digest には出さない。tooltip のみ |
| `ResourceBannerMissing` | `ResourceHealth` | 83 | `画像不足` | false | digest には出さない。tooltip のみ |

## 主な生成・削除単位

- `ResourceHealth` は保留パッケージなど導入前評価では category 単位で再構築します。導入成功時に category 単位で clear し、導入後の通常ライブラリ一覧と新規画面では `maintenanceInfo` / resource health index から表示時に投影します。これにより、保留時の「単体譜面なので WAV 0%」という warning が、導入後に WAV 100% へ更新された行へ残りません。
- `InstallEstimation` は導入先推定結果の適用、手動導入先確定、導入成功、推定状態クリアで category 単位に扱います。
- `DuplicateChart` は kind 単位で set / clear します。
- `ZeroNoteMismatch` は `chart_info.notes == 0` の BMS を正規表現で確認したとき、本文に可視ノート風記述がある場合に set し、`chart_info` が未生成または 0 notes ではなくなった場合は stale warning として clear します。
- `ChartInfoParseFailure` は解析エラー画面用の表示 shim 行に付与します。通常ライブラリの元行へは mutation しません。
- `Lr2PathEncodingUnsupported` は起動時の `song` 正規化、file diff 追加、`UpsertSongs()` 前補正で付与します。Shift_JIS 互換 path として LR2 `folder` / `parent` CRC を計算できる場合は clear します。
- `NestedChartFileInPackage`、`AlreadyInstalled`、`SingleBmsFile`、`SingleBmsonFile` は保留パッケージや復元時の状態初期化で付与します。

## 参照実装

- 定義: `BeMusicSeeker/Models/ChartWarning.cs`
- `BMSFile` の表示 property と互換 alias: `BeMusicSeeker/Models/BMSFile.cs`
- WARNING 列: `WarningDigestText` を本文、`WarningTooltipText` を tooltip、`HasHighlightedWarning` を行色判定に使います。
