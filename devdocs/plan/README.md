# Plans And History

このディレクトリは、実装計画・調査記録・移行履歴の置き場です。

`plan/` の資料は製品の現在仕様の正本ではありません。現行仕様は `../spec/` を参照してください。

例外として、`BeMusicSeeker_refactoring_plans/` の4文書は進行中のリファクタリング作業に対する運用上の正本です。このフォルダでは完了履歴を資料として蓄積せず、Git commit を履歴として使います。

## 主な資料

- `BeMusicSeeker_refactoring_plans/`: 現在進行中のリファクタリング完了計画、実行ルール、状態、`.NET 10` blocker register。
- `bmson/`: bmson 対応メジャーアップデートの計画と履歴。
- `playlog/`: LR2 / beatoraja プレイログ参照機能の計画と調査。
- `chart-file-read-consolidation-plan.md`: chart file read pipeline 整理の履歴。
- `chart-file-read-pipeline-unification-plan.md`: 譜面 bytes read / hash / worker / writer pipeline 統一の次期計画。
- `chart-info-metadata-bundle-import-plan.md`: metadata bundle import の設計履歴。
- `chart-info-parse-failure-plan.md`: chart_info parse failure 扱いの設計履歴。
- `empty-db-first-startup-optimization-plan.md`: 空 DB 初回起動最適化の履歴。
- `portable-auto-update-plan.md`: ポータブル zip 配布を維持した自動アップデート計画。
- `startup-reload-progress-plan.md`: 起動・リロード progress 表示整理の履歴。
- `warning-structure-migration-plan.md`: warning model 移行の履歴。

現行仕様は `../spec/README.md` を参照してください。
