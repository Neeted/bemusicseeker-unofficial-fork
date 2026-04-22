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
- `bmsFolderAllFileList : BMSDirectoryFileNameHash`  
  「chart directory」ごとの all-resource basename hash 配列
- `directoryResourceLookupCache : DirectoryResourceLookupCache`
  chart directory ごとのカテゴリ別 basename / relative-path hash 集合

## 3. `BMSDirectoryFileNameHash` の仕様

`BMSDirectoryFileNameHash` は以下の形式を持つ:

- キー: chart directory 絶対パス（`OrdinalIgnoreCase`）
- 値: その chart directory に再集約された resource basename の `uint[]` ハッシュ列

ハッシュ関数:

- `xxHash32`
- 事前正規化あり
  - `.ogg`, `.mp3` を `.wav` 系として正規化
  - `.bmp`, `.jpg` を `.png` 系として正規化
  - 大文字化して比較

目的:

- 推定先探索の候補抽出を高速化
- Everything / Fast の両 scanner が同じ意味の chart-directory keyed hash を返せるようにする

補足:

- `BMSDirectoryFileNameHash` は **basename-only index** であり、`sound\bgm1` のような path-aware key は保持しない
- relative path を含む source of truth は `DirectoryResourceLookupCache.Entry` 側に置く
- `bgm1` と `sound\bgm1` を broad filter 入口で分離するのは、この index ではなく別 phase の責務とする

## 4. scan result / resource cache の形

初期化時の scan 結果は raw file name 一覧ではなく、次の hash-only shape を source of truth にする。

- `ChartFilePaths`
- `ChartDirectories`
- `AllResourceBaseNameHashesByChartDirectory`
- `AudioBaseNameHashesByChartDirectory`
- `ImageBaseNameHashesByChartDirectory`
- `MovieBaseNameHashesByChartDirectory`
- `AudioRelativePathHashesByChartDirectory`
- `ImageRelativePathHashesByChartDirectory`
- `MovieRelativePathHashesByChartDirectory`

resource は「存在ディレクトリ」ではなく、**最長一致する chart directory** に再集約する。

- `chartdir\\00.wav` → `chartdir`
- `chartdir\\sound\\00.wav` → `chartdir`
- `chartdir\\subchart\\sound\\00.wav` かつ `subchart` に chart がある → `chartdir\\subchart`
- `chartdir\\..\\sound\\00.wav` のような親参照は今回未対応

Everything と通常列挙の差は、設計上「速度だけ」に寄せる。

また、initial/reload と install/merge 後の増分更新は、この chart-directory keyed shape を同じ意味で `DirectoryResourceLookupCache` / `BMSDirectoryFileNameHash` に反映することを前提にする。

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
  3. `bmsFolderAllFileList`
  4. `directoryResourceLookupCache`
  5. 必要に応じてハッシュ索引再構築
- 導入待ち/導入済みは末尾一括反映を優先し、UI通知の過多を避ける。
