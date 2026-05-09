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
4. [install-estimation-current-logic.md](install-estimation-current-logic.md)
   - 導入先推定の現行仕様。
5. [workflows.md](workflows.md)
   - リロード、導入、再インストールなどの主要処理フロー。

## 機能別仕様

- [chart-file-read-pipeline.md](chart-file-read-pipeline.md)
- [chart-info-parser-compatibility-notes.md](chart-info-parser-compatibility-notes.md)
- [startup-reload-progress.md](startup-reload-progress.md)
- [settings-change-impact-and-startup-operations.md](settings-change-impact-and-startup-operations.md)
- [warning-model.md](warning-model.md)
- [appearance-theme.md](appearance-theme.md)

## 旧 TECH_SPEC について

[TECH_SPEC.ja.md](TECH_SPEC.ja.md) は現在仕様を概観するための短い入口として維持します。詳細な正本は上記の機能別仕様を参照してください。
