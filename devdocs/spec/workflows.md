# 主要処理フロー

## 1. 起動時初期化

入口: `MainWindowViewModel.Initialize()`

1. 設定検証、`BMSLibrary`/`BMSPlaylist` の生成
2. リスナー登録（UI更新、ロック状態通知）
3. `files.Initialize(new List<Action> { taskAdd1, taskAdd2 }, semaphore)` 実行
4. 付随タスク:
   - `taskAdd1`: テーブル初期化 (`tables.Initialize`)
   - `taskAdd2`: 外部テーブル一覧取得

`BMSLibrary.Initialize(...)` は内部で `_initialize` を3段実行し、  
DBロード、ファイルスキャン、メンテ情報更新、導入待ち再構築まで進める。

ファイルスキャンは現在、次の 2 段で統一されている。

- chart scan
  - `.bme/.bms/.bml/.pms/.bmson`
- shared resource scan
  - `Audio/Image/Movie`

resource は `sibling:` ではなく roots 配下から列挙し、最長一致する chart directory へ再集約する。

## 2. リロード系

### 2.1 ライブラリのリロード（本体）

入口: `MainWindowViewModel.ReloadFiles()`

- `files.Initialize(null, null, false)` を呼ぶ
- ライブラリ（BMSFiles）を再走査・再同期
- その後、既存テーブル参照を再付与

### 2.2 プレイリストのリロード

入口: `MainWindowViewModel.ReloadTables()`

- `files.Initialize(..., true)` + `tables.Initialize(reloadExtPlaylist: true)`
- スコア/テーブル寄りの再読込。ライブラリ全面再走査とは目的が異なる。

## 3. 推定先インストール

入口: `BMSLibrary.InstallBMSPackagesToEstimatedDir(...)`

1. 対象パッケージを導入待ち集合から抽出
2. 推定先ディレクトリ単位でグルーピング
3. 各グループを `installBMSPackages(...)` で処理
   - ファイル移動
   - Song DBへの追加反映
   - メンテ対象収集
   - 0ノート判定
   - スコア反映
4. 末尾で導入待ち/導入済みのUI集合を一括適用
5. メンテ情報更新をバッチ末尾で実行

## 4. 推定ロジック

入口:
- `SearchEstimatedInstallationDirectory(BMSPackage)`
- `SearchEstimatedInstallationDirectory(BMSFile, ...)`

概要:

- 対象譜面の構成ファイル情報（WAV/BGA等）を基に、`bmsFolderAllFileList.Keys` の候補ディレクトリを評価。
- `BMSDirectoryFileNameHash` の all-resource basename hash と、`DirectoryResourceLookupCache` のカテゴリ別 hash を使って一致度を計算。
- 最適候補を `instl_dst` に反映。

## 5. 再インストール補助

- `SearchCorrectInstallationDirectory(...)`  
  メンテ警告対象から再インストール先を推定する用途。
- `RemoveInstallDestination(...)`  
  推定結果のクリア。

## 6. UI更新抑制（導入処理）

`MainWindowViewModel` では導入中に UI通知を抑制する制御がある。

- `BeginInstallUiUpdateSuppression()`
- `EndInstallUiUpdateSuppression()`
- 抑制中は pending/installed のイベント更新を遅延適用

この仕組みにより、大規模ライブラリ時の導入処理で UIオーバーヘッドを抑える。
