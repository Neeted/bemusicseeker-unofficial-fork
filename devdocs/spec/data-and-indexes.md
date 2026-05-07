# データモデルと索引

## 1. 中核モデル

- `BMSFile` (`BeMusicSeeker.Models/BMSFile.cs`)
  - 譜面1件を表す
  - `path`, `hash`, `instl_dst`, `maintenanceInfo`, `bmsScore` などを保持
  - ヘッダ解析・構成ファイル解析・ヘルス算出機能を持つ
- `BMSPackage` (`BeMusicSeeker.Models/BMSPackage.cs`)
  - 導入単位（フォルダまたは単体ファイル）
  - `BMSFiles` を遅延解決
- `BMSTable` / `BMSTableEntry`
  - 外部テーブル情報
- `BMSScore`
  - スコア情報
- `BMSFileMaintenanceInfo`
  - 構成ファイル不足などのメンテ情報

## 2. BMSLibraryが保持する主データ

- `BMSFiles`  
  所持譜面一覧（メインライブラリ）
- `BMSPackagesPending`  
  導入待ちパッケージ一覧
- `BMSPackagesInstalled`  
  導入済みパッケージ一覧（管理用）
- `directoryResourceLookupCache : DirectoryResourceLookupCache`
  chart directory ごとのカテゴリ別 chart-relative resource key 集合。chart directory key set と resource index の正本
  - aggregate ownership と self-only ownership の二重 view

## 3. `ChartResourceKeyHash` の仕様

`ChartResourceKeyHash` は live cache ではなく、拡張子なし resource key 用の静的 hash helper としてだけ残っている。

ハッシュ関数:

- `xxHash32`
- 事前正規化あり
  - `.ogg`, `.mp3` を `.wav` 系として正規化
  - `.bmp`, `.jpg` を `.png` 系として正規化
  - 大文字化して比較

目的:

- BMS / BMSON resource reference と file enumeration の key を同じ正規化で hash 化する
- `DirectoryResourceLookupCache`、`ChartResourceSnapshot`、scan result builder などがカテゴリ別 key を作るための暫定 helper

補足:

- `ChartResourceKeyHash` は static helper であり、instance cache API は存在しない。
- 導入先推定の照合本体と reverse lookup は `DirectoryResourceLookupCache` の audio / image / movie chart-relative key を使う。
- 導入先推定は relative-only evaluation で、category 別 basename hash は照合に使わない。
- resource health / maintenance も `DirectoryResourceLookupCache.Entry` のカテゴリ別 chart-relative key を使い、旧 union view や basename-only matching には fallback しない。
- `foo.wav` は `foo`、`sound/foo.wav` は `sound/foo` として扱われ、旧 basename-only matching は使わない。
- 導入先推定では candidate directory 集合も照合本体も `DirectoryResourceLookupCache` を使い、旧 union view には fallback しない
- relative path を含む source of truth は `DirectoryResourceLookupCache.Entry` 側に置く

## 4. scan result / resource cache の形

初期化時の resource index の正本は `LibraryResourceIndex` / `DirectoryResourceLookupCache` である。

native bridge を使う通常起動では、`EBridge_ScanChartAndResources` の packed result から `LibraryResourceIndex` を直接 materialize する。`BmsScanResult` は file diff 用の `ChartFilePaths` / `ChartDirectories` を保持するだけで、resource dictionary は materialize しない。

Everything が使えない場合の managed fallback scan と、テスト用の scan merge 経路では、`BmsScanResult` が次の hash-only shape を持つ。

- `ChartFilePaths`
- `ChartDirectories`
- aggregate ownership
  - `AudioRelativePathHashesByChartDirectory`
  - `ImageRelativePathHashesByChartDirectory`
  - `MovieRelativePathHashesByChartDirectory`
- self-only ownership
  - `SelfOwnedAudioRelativePathHashesByChartDirectory`
  - `SelfOwnedImageRelativePathHashesByChartDirectory`
  - `SelfOwnedMovieRelativePathHashesByChartDirectory`

managed scan result は chart-relative key だけを保持する。native bridge payload も chart-relative resource-key surface だけを返し、managed 側で重複する dictionary surface を作らない。
resource は「存在ディレクトリ」ではなく、chart directory keyed に再集約する。
未分類 all-resource surface は保持しない。必要な場合の union は audio / image / movie のカテゴリ配列からその場で派生し、live cache としては持たない。
`2026-04-23` 時点では次の二重 semantics を持つ。

- aggregate ownership
  - resource を含む path 上の **すべての ancestor chart directory**
- self-only ownership
  - resource を最も近くで所有する chart directory のみ

例:

- `chartdir\\subchart\\sound\\00.wav` かつ `subchart` に chart がある
  - aggregate
    - `chartdir` は `subchart\\sound\\00.wav`
    - `chartdir\\subchart` は `sound\\00.wav`
  - self-only
    - `chartdir\\subchart` のみ
- `chartdir\\..\\sound\\00.wav` のような親参照は今回未対応

Everything と通常列挙の差は、設計上「速度だけ」に寄せる。

また、initial/reload と install/merge/delete/move 後の増分更新は、この chart-directory keyed shape を `DirectoryResourceLookupCache` へ反映する。folder 操作後の cache cleanup も `DirectoryResourceLookupCache.Keys` を正本にし、extensionless union の live cache は持たない。

## 5. DBテーブル（BMSLibraryコンストラクタで整備）

- `song`（既存LR2）
- `install`
- `maintenance`
- `ir_score`
- `ir_data`

加えてインデックス作成:

- `song_idx_folder`
- `ir_data_idx`

## 6. 一貫性更新の基本方針

- ファイル実体変更後は次を同期:
  1. `BMSFiles`
  2. Song DB (`song`)
  3. `directoryResourceLookupCache`
  4. 必要に応じてハッシュ索引再構築
- 導入待ち/導入済みは末尾一括反映を優先し、UI通知の過多を避ける。
