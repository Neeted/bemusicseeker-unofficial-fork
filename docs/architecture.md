# アーキテクチャ概要

## 1. レイヤ構成

- `BeMusicSeeker.Views`  
  WPF画面（`MainWindow.xaml` 等）。ユーザー操作を ViewModel に中継。
- `BeMusicSeeker.ViewModels`  
  画面状態とユースケース制御。中核は `MainWindowViewModel`。
- `BeMusicSeeker.Models`  
  ドメイン処理。中核は `BMSLibrary`（ライブラリ、推定、インストール、整合性）。
- `BeMusicSeeker.Models.LR2`  
  LR2 DBスキーマ拡張・DBアクセスモデル。
- `BeMusicSeeker.Models.Utils`  
  ファイル列挙、Everything連携、コマンドライン引数、ユーティリティ。

## 2. 主要コンポーネント

- `App` (`BeMusicSeeker/App.cs`)  
  起動初期化、設定アップグレード、ログ設定、例外ハンドリング。
- `MainWindowViewModel` (`BeMusicSeeker.ViewModels/MainWindowViewModel.cs`)  
  画面ユースケースの調停。`BMSLibrary` と `BMSPlaylist` を統括。
- `BMSLibrary` (`BeMusicSeeker.Models/BMSLibrary.cs`)  
  楽曲ライブラリとインストール管理の中核。DB更新、メンテ情報、推定ロジックを担う。
- `BMSPlaylist` (`BeMusicSeeker.Models/BMSPlaylist.cs`)  
  テーブル/プレイリスト管理。`BMSLibrary` と連携して参照情報を付与。

## 3. 永続データ

- LR2 Song DB（必須）  
  `LR2SongDB.song` に楽曲情報を保持。
- LR2拡張テーブル（同DB内）  
  `install`（導入待ち/導入済みパッケージ）, `maintenance`（ヘルス）, `ir_score`, `ir_data`。
- LR2 Score DB（任意）  
  スコア統合用途。設定と存在条件で有効化。

## 4. スレッド/ロック方針

- 大枠は `ReaderWriterLockSlimWrapper` による分離ロック。
- 代表ロック:
  - `rwlockBMSFilesInitializedAll`（初期化全体）
  - `rwlockBMSFiles`（所持BMS本体）
  - `rwlockBMSFilesPendingInstall`（導入待ち）
  - `rwlockSongDBInstall`（install系DB更新）
- UIバインドは `DispatcherCollection` と `PropertyChanged` を介して反映。

## 5. 現行スキャン設計（重要）

- 初期化スキャンは `IBmsFileScanner` 抽象で実行。
- 現行の優先経路:
  - `EverythingFileScanner`（Bridge必須）
  - 失敗時 `FastDirectoryFileScanner` にフォールバック
- Bridge経路は native DLL (`EverythingBridge_x64.dll`) で結果を集約して返す。
