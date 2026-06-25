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
4. [path-identity.md](path-identity.md)
   - DB 上の `path` identity、case-sensitive exact match、`COLLATE NOCASE` の利用制限。
5. [path-length-and-io.md](path-length-and-io.md)
   - 長パスを含む譜面ファイル I/O、内部ファイル操作、保存 path と extended-length path の境界、読めないファイルの集約方針。
6. [library-mutation-boundary.md](library-mutation-boundary.md)
   - 譜面 / パッケージ操作の共通 mutation 境界、dialog / report、UI / model lock 契約。
7. [lr2-song-db-generation.md](lr2-song-db-generation.md)
   - LR2 連携モードの `song.db` 生成、`song` / `folder`、`.lr2folder`、自動更新設定。
8. [install-estimation-current-logic.md](install-estimation-current-logic.md)
   - 導入先推定の現行仕様。
9. [playlist-url-download-resolution.md](playlist-url-download-resolution.md)
   - プレイリスト `URL1` / `URL2` の自動ダウンロード解決、一括取り込み、対応サイト。
10. [workflows.md](workflows.md)
   - リロード、導入、再インストールなどの主要処理フロー。
11. [logging-policy.md](logging-policy.md)
   - アプリログ、性能診断ログ、出力先、ローテーション、起動引数互換。
12. [testing-strategy.md](testing-strategy.md)
   - 通常検証、parser 互換検証、大容量 fixture、性能検証の切り分け。

## 機能別仕様

- [chart-file-read-pipeline.md](chart-file-read-pipeline.md)
- [chart-info-parser-compatibility-notes.md](chart-info-parser-compatibility-notes.md)
- [startup-reload-progress.md](startup-reload-progress.md)
- [settings-change-impact-and-startup-operations.md](settings-change-impact-and-startup-operations.md)
- [path-identity.md](path-identity.md)
- [path-length-and-io.md](path-length-and-io.md)
- [library-mutation-boundary.md](library-mutation-boundary.md)
- [lr2-song-db-generation.md](lr2-song-db-generation.md)
- [playlist-data-and-export-flow.md](playlist-data-and-export-flow.md)
- [play-history.md](play-history.md)
- [playlist-url-download-resolution.md](playlist-url-download-resolution.md)
- [bms-bmson-chart-abstraction-current-state.md](bms-bmson-chart-abstraction-current-state.md)
- [warning-model.md](warning-model.md)
- [duplicate-file-check.md](duplicate-file-check.md)
- [appearance-theme.md](appearance-theme.md)
- [custom-table-view.md](custom-table-view.md)
- [file-selection-dialogs.md](file-selection-dialogs.md)
- [logging-policy.md](logging-policy.md)
- [testing-strategy.md](testing-strategy.md)

## 旧 TECH_SPEC について

[TECH_SPEC.ja.md](TECH_SPEC.ja.md) は現在仕様を概観するための短い入口として維持します。詳細な正本は上記の機能別仕様を参照してください。
