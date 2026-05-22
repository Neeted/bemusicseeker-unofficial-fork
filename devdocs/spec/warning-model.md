# 現行 WARNING モデル

この資料は、現在の WARNING 表示仕様だけをまとめたものです。移行経緯は `../plan/warning-structure-migration-plan.md` を参照してください。

## 基本モデル

- WARNING は主に BMS storage row の `BMSFile.Warnings`、pending package entry の warning state、または `ChartFile` projection 上の `ChartWarning` の集合として扱います。所持 bmson の storage row は `bmson_song` であり、通常一覧や playlist detail では operation adapter ではなく `ChartFile` / `ChartFileTransientState` を通して表示されます。
- 通常一覧と導入済み直後の `新規` 画面の `ResourceHealth` warning は例外的に、cache-aware health 判定で更新した `maintenanceInfo` から構築する runtime `ResourceHealthIndexSnapshot` を `LibraryChartRow` が投影して表示します。DB 永続 warning ではありません。
- `maintenanceInfo` は、DB 由来または計算済みの resource health snapshot だけを正本として扱います。BMS の `BMSFile.maintenanceInfo` lazy default や bmson parse 直後の encoding-only `bmson_song.MaintenanceInfo` は placeholder であり、resource health の defined / existing count が揃うまでは正本に昇格しません。DB 由来の partial snapshot は既存仕様どおり該当カテゴリの warning 投影に使います。
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
- `ResourceHealth` category の warning は、対象 chart の導入先が未設定の間だけ digest に出ます。通常一覧 / playlist detail / pending package では `ChartFile.InstallDestination` を provider として使います。BMS storage row の legacy `BMSFile.instl_dst` は `ChartFileProjection.FromBmsFile(...)` へは投影せず、必要な導入先状態は package state / transient state / model-side runtime overlay から明示的に重ねます。tooltip には導入先設定後も詳細が残ります。
- resource health の一覧所属判定は chart warning collection を mutation せず、`maintenanceInfo` 由来の side-effect-free index で行います。BMS は有効な `BMSFile.maintenanceInfo`、bmson は現在の md5 と一致し resource health snapshot を持つ `bmson_song.MaintenanceInfo` を入力にします。bmson の maintenance row が parser 直後の encoding-only placeholder だったり、hash が古かったりする場合は、adapter を materialize せず `ChartResourceSnapshot` と現在の filesystem 状態から一時 snapshot を作ります。manual rescan / inline initialization でも bmson は `bmson_song` と `ChartResourceSnapshot` から直接 maintenance row を作り、`PendingChartEntry` shim は使いません。`maintenanceInfo` の resource health は file scan 由来の directory resource index を優先し、必要時のみ実ファイル確認へ fallback します。
- 保留画面の `ResourceHealth` warning は導入前配置を評価するための一時状態です。導入成功時に source BMS row または `PackageChartEntry` の warning state から `ResourceHealth` category を消し、導入後の通常一覧・新規画面では `maintenanceInfo` / resource health index から投影します。bmson は `BMSFile` adapter へ戻さず、`bmson_song` / `ChartFile` / package entry state を正本にします。

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
| `InstalledDestinationAmbiguous` | `InstallEstimation` | 43 | `導入先複数` | true | warning が存在する場合 |
| `InstalledDestinationResolveFailed` | `InstallEstimation` | 44 | `導入先不明` | false | warning が存在する場合 |
| `InstallEstimationLowConfidence` | `InstallEstimation` | 45 | `導入先推定` | true | warning が存在する場合 |
| `AlreadyInstalled` | `InstalledState` | 50 | `既に導入済み` | false | warning が存在する場合 |
| `SingleBmsFile` | `PackageLayout` | 60 | `単体BMS` | false | warning が存在する場合 |
| `SingleBmsonFile` | `PackageLayout` | 60 | `単体BMSON` | false | warning が存在する場合 |
| `ResourceWavMissing` | `ResourceHealth` | 80 | `リソース不足` | false | warning が存在し、対象 chart の導入先が未設定の場合 |
| `ResourceBgaMissing` | `ResourceHealth` | 80 | `リソース不足` | false | warning が存在し、対象 chart の導入先が未設定の場合 |
| `ResourceMovieMissing` | `ResourceHealth` | 80 | `リソース不足` | false | warning が存在し、対象 chart の導入先が未設定の場合 |
| `ResourceStagefileMissing` | `ResourceHealth` | 83 | `画像不足` | false | digest には出さない。tooltip のみ |
| `ResourceBackbmpMissing` | `ResourceHealth` | 83 | `画像不足` | false | digest には出さない。tooltip のみ |
| `ResourceBannerMissing` | `ResourceHealth` | 83 | `画像不足` | false | digest には出さない。tooltip のみ |

## 主な生成・削除単位

- `ResourceHealth` は保留パッケージなど導入前評価では category 単位で再構築します。導入成功時に category 単位で clear し、導入後の通常ライブラリ一覧と新規画面では `maintenanceInfo` / resource health index から表示時に投影します。これにより、保留時の「単体譜面なので WAV 0%」という warning が、導入後に WAV 100% へ更新された行へ残りません。
- 導入後に不足 resource を追加した場合の再評価は、行右クリック `ファイルスキャン > 再スキャン` または `ファイルスキャン > 全譜面を再スキャン` で行います。通常起動の `maintenance_hydration` は DB snapshot attach であり、全譜面の resource 再検証は行いません。
- `全譜面を再スキャン` は 1000 件 section 単位で進捗を出す重い明示操作です。通常起動ログ評価では、未完了の manual rescan と `maintenance_hydration` を分けて扱います。
- `InstallEstimation` は導入先推定結果の適用、手動導入先確定、導入成功、推定状態クリアで category 単位に扱います。mixed package で既所持譜面から複数の導入先候補が見つかり、final evaluation 後も複数 viable 候補が残る場合は `InstalledDestinationAmbiguous` を付与し、候補一覧を tooltip に出します。
- `DuplicateChart` は kind 単位で set / clear します。
- `ZeroNoteMismatch` は `chart_info.notes == 0` の BMS を正規表現で確認したとき、本文に可視ノート風記述がある場合に set し、`chart_info` が未生成または 0 notes ではなくなった場合は stale warning として clear します。
- `ChartInfoParseFailure` は解析エラー画面用の表示 shim 行に付与します。通常ライブラリの元行へは mutation しません。
- `Lr2PathEncodingUnsupported` は起動時の `song` 正規化、file diff 追加、`UpsertSongs()` 前補正で付与します。Shift_JIS 互換 path として LR2 `folder` / `parent` CRC を計算できる場合は clear します。この警告は「LR2 と連携中か」ではなく「LR2 では扱えない可能性が高い path か」を示すため、standalone profile の既存 `song` 読み込みでも同じ判定を行います。
- `NestedChartFileInPackage`、`AlreadyInstalled`、`SingleBmsFile`、`SingleBmsonFile` は保留パッケージや復元時の状態初期化で付与します。

## 参照実装

- 定義: `BeMusicSeeker/Models/ChartWarning.cs`。`ChartWarningCollection` は BMSFile owner ではなく、変更通知 callback と導入先 provider を受け取ります。
- `BMSFile` の表示 property と互換 alias: `BeMusicSeeker/Models/BMSFile.cs`
- bmson storage row と一時表示 state の projection: `BeMusicSeeker/Models/ChartFileProjection.cs`, `BeMusicSeeker/Models/ChartFileTransientState.cs`
- pending package の chart state: `BeMusicSeeker/Models/BmsLibraryInternal/PackageChartEntry.cs`
- WARNING 列: `WarningDigestText` を本文、`WarningTooltipText` を tooltip、`HasHighlightedWarning` を行色判定に使います。
