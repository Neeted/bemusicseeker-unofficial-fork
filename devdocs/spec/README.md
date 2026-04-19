# BeMusicSeeker Documentation

本ディレクトリは、現行実装（逆コンパイル移植版）を前提にした仕様整理です。  
改善作業の前提共有と、処理経路の確認を目的にしています。

## 読み順

1. `devdocs/spec/architecture.md`  
   アプリ全体の構成、責務分割、主要クラス
2. `devdocs/spec/workflows.md`  
   起動/リロード/推定インストール/再インストール系の実行フロー
3. `devdocs/spec/data-and-indexes.md`  
   DB、メモリ構造、ハッシュ索引の仕様
4. `devdocs/spec/performance-and-operations.md`  
   スキャン経路、ログ引数、既知の性能特性
5. `devdocs/bmson/README.md`
   `bmson` 対応のフェーズ分割ロードマップとフェーズ別実行プラン

## 対象範囲

- 現在の実装コードを基準にした「動く仕様」
- 主に以下のモジュール:
  - `BeMusicSeeker/App.cs`
  - `BeMusicSeeker.ViewModels/MainWindowViewModel.cs`
  - `BeMusicSeeker.Models/BMSLibrary.cs`
  - `BeMusicSeeker.Models.Utils/*Scanner*.cs`
  - `BeMusicSeeker.Models/BMSDirectoryFileNameHash.cs`

## 補足

- 文中の「初期化」は `MainWindowViewModel.Initialize()` からの起動時処理を指します。
- 文中の「リロード」はライブラリ側 `ReloadFiles()` を指します（プレイリスト側 `ReloadTables()` とは別）。
