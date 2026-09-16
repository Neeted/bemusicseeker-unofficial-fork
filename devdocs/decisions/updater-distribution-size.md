# 更新プログラムにNative AOTを使う理由

## 適用する判断

`BeMusicSeeker.Updater` は `net10.0-windows` / `win-x64` の自己完結型Native AOTとし、`OptimizationPreference=Size` を使います。配布先の.NET実行基盤に依存せず、更新プログラム自体のサイズを抑えるためです。

## 比較の根拠

同じソース `8942938a` から各構成を一回ずつ作成した実行ファイルの比較です。現在の配布サイズや処理時間を保証する値ではありません。

| 構成 | バイト数 | 配布ビルドの警告数 |
| --- | ---: | ---: |
| CoreCLR単一ファイル | 73,590,553 | 0 |
| 全体トリミング・圧縮付きCoreCLR単一ファイル | 11,488,963 | 0 |
| Native AOT | 3,111,424 | 0 |

サイズだけのために国際化、スタックトレース、リソースキーの機能を減らしたり、警告を抑制したりしません。更新記録のJSONにはソース生成された `System.Text.Json` メタデータを使います。

## 構成上の帰結

Native AOTが一つのネイティブ実行ファイルを生成するため、CoreCLR用の `PublishSingleFile`、圧縮、ネイティブDLLの自己展開設定は使いません。パッケージのルートには `BeMusicSeeker.Updater.exe` 一つを配置します。更新・復元・再開の契約は[ポータブル更新仕様](../spec/integration/portable-update.md)で定めます。

## 関連資料

[リリース](../spec/development/release.md)、[配布の受入](../acceptance/net10-distribution-performance.md)、[検証](../spec/development/testing.md)。
