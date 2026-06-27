# P2-03 CustomTableView 表示基盤整理 後続検討

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 位置づけ

`CustomTableView.cs` は 2,840 行の owner-drawn table control で、描画、hit test、selection、keyboard、cell edit、drag、tooltip、column resize、render metrics を持つ。MainWindow の巨大化とは別に UI 基盤として重要だが、先に MainWindow UI 分割と command adapter を進める方が安全。

## 詳細計画を検討する条件

- [P0-03 MainWindow UI / code-behind MVVM 移行計画](./P0-03_MainWindow_UI_MVVM移行計画.md) の `CustomTableCommandAdapter` が導入済み。
- chart table / playlist summary table の DataContext が子 ViewModel に移っている。
- `CustomTableView` の event が code-behind から直接大量に処理されていない。

## 方向性

- selection model、hit test、keyboard command、cell edit controller、render surface を分ける。
- `CustomTableColumnLayout` の pure logic をさらに test 可能にする。
- 描画性能を維持するため、WPF 標準 DataGrid への置換は前提にしない。
- accessibility / high DPI / text rendering は .NET 10 移行後の検討に含める。

## 現時点で先行可能なこと

- `CustomTableCommandAdapter` に必要な event args DTO を定義する。
- 既存 `CustomTable*Tests` を維持しながら pure logic を増やす。

## 現時点でやらないこと

- MainWindow split 前に control 内部を大きく変えない。
- 描画方式を変更しない。
- virtualization / selection behavior を同時に変更しない。
