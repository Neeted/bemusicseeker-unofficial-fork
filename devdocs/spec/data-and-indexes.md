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
  「chart directory」ごとの resource hash union 配列。入力は audio / image / movie のカテゴリ別 index から派生する
- `directoryResourceLookupCache : DirectoryResourceLookupCache`
  chart directory ごとのカテゴリ別 basename / relative-path hash 集合
  - aggregate ownership と self-only ownership の二重 view
- `directoryRelativePathHashIndex : DirectoryRelativePathHashIndex`
  cacheless path 用の chart directory ごとのカテゴリ別 basename / relative-path hash 索引
  - aggregate ownership と self-only ownership の二重 view

## 3. `BMSDirectoryFileNameHash` の仕様

`BMSDirectoryFileNameHash` は以下の形式を持つ:

- キー: chart directory 絶対パス（`OrdinalIgnoreCase`）
- 値: その chart directory に再集約された resource basename の `uint[]` ハッシュ列
  - native payload から直接受け取る all-resource surface ではなく、audio / image / movie のカテゴリ hash union

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

- `BMSDirectoryFileNameHash` は導入先推定の正本ではなく、カテゴリ別 canonical resource index から派生する folder-level union view である。
- 導入先推定の照合本体と reverse lookup は `DirectoryResourceLookupCache` の audio / image / movie chart-relative key を使う。
- `foo.wav` は `foo`、`sound/foo.wav` は `sound/foo` として扱われ、旧 basename-only matching は使わない。
- relative path を含む source of truth は `DirectoryResourceLookupCache.Entry` / `DirectoryRelativePathHashIndex.Entry` 側に置く

## 4. `DirectoryRelativePathHashIndex` の仕様

`DirectoryRelativePathHashIndex` は cacheless path 用の補助 index で、次を持つ。

- キー: chart directory 絶対パス（`OrdinalIgnoreCase`）
- 値:
  - aggregate ownership
    - `AudioBaseNameHashArray`
    - `ImageBaseNameHashArray`
    - `MovieBaseNameHashArray`
    - `AudioRelativePathHashArray`
    - `ImageRelativePathHashArray`
    - `MovieRelativePathHashArray`
  - self-only ownership
    - `SelfOwnedAudioBaseNameHashArray`
    - `SelfOwnedImageBaseNameHashArray`
    - `SelfOwnedMovieBaseNameHashArray`
    - `SelfOwnedAudioRelativePathHashArray`
    - `SelfOwnedImageRelativePathHashArray`
    - `SelfOwnedMovieRelativePathHashArray`

目的:

- `DirectoryResourceLookupCache` がない経路でも、path-aware ref を broad filter 入口で first-class key として扱う
- `BMSDirectoryFileNameHash` の basename-only 意味を壊さずに、cacheless path の semantics を cache あり経路に揃える

この index は:

- initial / reload では `BmsScanResult` から構築する
- install / merge の増分更新でも同じ chart-directory keyed shape で `AddDir(..., scanResult)` する
- cacheless broad filter に使う
- cacheless final evaluation でも category-aware basename / relative-path semantics を揃えるために使う
- aggregate ownership を install estimation の主 view にし、self-only ownership を suppression / tie-break 補助に使う

## 5. scan result / resource cache の形

初期化時の scan 結果は raw file name 一覧ではなく、次の hash-only shape を source of truth にする。

- `ChartFilePaths`
- `ChartDirectories`
- aggregate ownership
  - `AudioBaseNameHashesByChartDirectory`
  - `ImageBaseNameHashesByChartDirectory`
  - `MovieBaseNameHashesByChartDirectory`
  - `AudioRelativePathHashesByChartDirectory`
  - `ImageRelativePathHashesByChartDirectory`
  - `MovieRelativePathHashesByChartDirectory`
- self-only ownership
  - `SelfOwnedAudioBaseNameHashesByChartDirectory`
  - `SelfOwnedImageBaseNameHashesByChartDirectory`
  - `SelfOwnedMovieBaseNameHashesByChartDirectory`
  - `SelfOwnedAudioRelativePathHashesByChartDirectory`
  - `SelfOwnedImageRelativePathHashesByChartDirectory`
  - `SelfOwnedMovieRelativePathHashesByChartDirectory`

resource は「存在ディレクトリ」ではなく、chart directory keyed に再集約する。  
未分類 all-resource surface は保持しない。必要な folder-level hash は audio / image / movie のカテゴリ union から派生する。
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

また、initial/reload と install/merge 後の増分更新は、この chart-directory keyed shape を同じ意味で

- `DirectoryResourceLookupCache`
- `BMSDirectoryFileNameHash`
- `DirectoryRelativePathHashIndex`

へ反映することを前提にする。

## 6. DBテーブル（BMSLibraryコンストラクタで整備）

- `song`（既存LR2）
- `install`
- `maintenance`
- `ir_score`
- `ir_data`

加えてインデックス作成:

- `song_idx_folder`
- `ir_data_idx`

## 7. 一貫性更新の基本方針

- ファイル実体変更後は次を同期:
  1. `BMSFiles`
  2. Song DB (`song`)
  3. `bmsFolderAllFileList`
  4. `directoryResourceLookupCache`
  5. `directoryRelativePathHashIndex`
  6. 必要に応じてハッシュ索引再構築
- 導入待ち/導入済みは末尾一括反映を優先し、UI通知の過多を避ける。
