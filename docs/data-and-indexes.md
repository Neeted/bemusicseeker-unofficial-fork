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
  「譜面があるフォルダ」ごとの直下ファイルハッシュ配列

## 3. `BMSDirectoryFileNameHash` の仕様

`BMSDirectoryFileNameHash` は以下の形式を持つ:

- キー: ディレクトリ絶対パス（`OrdinalIgnoreCase`）
- 値: そのディレクトリ直下ファイル名の `uint[]` ハッシュ列

ハッシュ関数:

- `xxHash32`
- 事前正規化あり
  - `.ogg`, `.mp3` を `.wav` 系として正規化
  - `.bmp`, `.jpg` を `.png` 系として正規化
  - 大文字化して比較

目的:

- 推定先探索の一致判定を高速化
- ヘルス判定での存在比較コストを低減

## 4. DBテーブル（BMSLibraryコンストラクタで整備）

- `song`（既存LR2）
- `install`
- `maintenance`
- `ir_score`
- `ir_data`

加えてインデックス作成:

- `song_idx_folder`
- `ir_data_idx`

## 5. 一貫性更新の基本方針

- ファイル実体変更後は次を同期:
  1. `BMSFiles`
  2. Song DB (`song`)
  3. `bmsFolderAllFileList`
  4. 必要に応じてハッシュ索引再構築
- 導入待ち/導入済みは末尾一括反映を優先し、UI通知の過多を避ける。
