# Current Specifications

このディレクトリは、現行実装を基準にした仕様書の置き場です。

`plan/` 配下の資料は履歴として残しますが、現在の挙動を確認するときはこのディレクトリを優先します。

## 読み順

1. [architecture.md](architecture.md)
   - レイヤ構成、主要 component、native bridge の位置づけ。
2. [startup-initialization-flow.md](startup-initialization-flow.md)
   - 起動、導入可能 readiness、startup background scheduler。
3. [data-and-indexes.md](data-and-indexes.md)
   - catalog、resource index、chart-relative key、DB table。
4. [lr2-song-db-generation.md](lr2-song-db-generation.md)
   - LR2 連携モードの `song.db` 生成、`song` / `folder`、`.lr2folder`、自動更新設定。
5. [install-estimation-current-logic.md](install-estimation-current-logic.md)
   - 導入先推定の現行仕様。
6. [playlist-url-download-resolution.md](playlist-url-download-resolution.md)
   - プレイリスト `URL1` / `URL2` の自動ダウンロード解決、一括取り込み、対応サイト。
7. [workflows.md](workflows.md)
   - リロード、導入、再インストールなどの主要処理フロー。
8. [testing-strategy.md](testing-strategy.md)
   - 通常検証、parser 互換検証、大容量 fixture、性能検証の切り分け。

## 機能別仕様

- [chart-file-read-pipeline.md](chart-file-read-pipeline.md)
- [chart-info-parser-compatibility-notes.md](chart-info-parser-compatibility-notes.md)
- [startup-reload-progress.md](startup-reload-progress.md)
- [settings-change-impact-and-startup-operations.md](settings-change-impact-and-startup-operations.md)
- [lr2-song-db-generation.md](lr2-song-db-generation.md)
- [playlist-data-and-export-flow.md](playlist-data-and-export-flow.md)
- [playlist-url-download-resolution.md](playlist-url-download-resolution.md)
- [bms-bmson-chart-abstraction-current-state.md](bms-bmson-chart-abstraction-current-state.md)
- [warning-model.md](warning-model.md)
- [duplicate-file-check.md](duplicate-file-check.md)
- [appearance-theme.md](appearance-theme.md)
- [custom-table-view.md](custom-table-view.md)
- [file-selection-dialogs.md](file-selection-dialogs.md)
- [testing-strategy.md](testing-strategy.md)

## 旧 TECH_SPEC について

[TECH_SPEC.ja.md](TECH_SPEC.ja.md) は現在仕様を概観するための短い入口として維持します。詳細な正本は上記の機能別仕様を参照してください。
