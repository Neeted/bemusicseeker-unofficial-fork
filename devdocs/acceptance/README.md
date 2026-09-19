# 測定と受入の資料

現在の比較条件・設計根拠として必要な測定と、受入に使う固定データを案内します。対象の版・入力・完了範囲を確認し、過去の値を現行版の性能や合格の証明には使いません。通常の実行ログと作業完了報告は保存しません。

| 資料 | 用途 |
| --- | --- |
| [.NET 10配布形式の比較](net10-distribution-performance.md) | 通常配布形式の選択を支える同条件の起動比較。 |
| [大規模ライブラリの参照値](performance-reference-2026-09-09.md) | 譜面・リソース索引・プレイリストの規模と、比較条件の注意。 |
| [重複統合の連続操作](duplicate-merge-performance-2026-09-13.md) | 索引再構築を見落とさない操作全体の測定範囲。 |
| [対象パス削除のDB処理量](path-cleanup-db-work.md) | 背景行数に連動する処理を識別する観測方法。 |
| [既存データ受入の固定入力](net10-existing-data/fixture-manifest.json) | 管理する固定入力と受入条件。 |
| [公開旧版の配布物指定](v216-first-hop/artifact.json) | 旧版からの実移行に使用する版・サイズ・ハッシュ。 |

現行の判定規則は[性能仕様](../spec/core/performance-and-scale.md)と[検証仕様](../spec/development/testing.md)を参照します。[開発資料の入口](../README.md)へ戻ります。
