# 現行仕様

このディレクトリは、現行実装の仕様と採用済みの設計・性能要件の置き場です。要件文書は適用対象・状態を明示し、未達・未検証の要件を現行実装の保証と混同しません。

`plan/` は未完了の作業に必要な資料だけを置き、完了後は必要な契約をこのディレクトリへ統合して削除します。開発の経緯は Git 履歴で参照します。現行にも適用する設計判断・測定根拠は [現行情報の維持](../README.md#現行情報の維持) に従います。

## 仕様書の書式

仕様書は「目的と適用状態」「用語（必要な場合）」「仕様」「実装とテストの対応」を基本構成とします。本文は [開発資料の書き方](../README.md#書き方) に従い、仕様は利用者の操作や入力条件と、その結果が分かる項目名で説明します。

仕様項目ごとに、次の対応表で実装と確認方法を示します。関連する条件は同じ行にまとめ、本文の仕様項目から該当行へ辿れる形にします。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 仕様の項目名または見出しへのリンクと、対象条件 | ファイルへのリンクと型・メソッド名 | テストファイルへのリンクとクラス・メソッド名、そこで確認する結果 |

実装・テストの参照はファイルと識別子で特定し、複数のメソッドが分担する場合はその役割を示します。データ駆動テストは対象条件も添えます。未実装の要件は「未実装」、自動テストがない項目は「自動テストなし」と確認方法・未確認範囲を記します。テストの追加要否は [テスト作成契約](test-authoring-contract.md#1-変更分類と恒久テストの必要性) で判断します。

仕様、対応するコード、テストの内容や配置が変わったときは、同じ変更で対応表を更新します。作業中のテスト設計から恒久化する内容は、仕様項目名と実装・テストの対応へ統合します。既存の `Verification map` は、変更対象の項目からこの対応表へ揃えます。

計画固有の段階番号・作業番号・Packet ID / Contract ID は、見出し、仕様 ID、対応表、テスト名への対応キーに使いません。項目名は機能・契約・入力条件・期待結果で示し、退役する計画への参照や一時 ID の別名・旧新対応表を残しません。番号そのものではなく実装順に由来する一時名を対象とし、外部標準の識別子や現行契約が定めるデータの番号・バージョンは区別します。

仕様変更時は本文を現在の契約へ更新し、旧仕様と完了報告を追記して並存させません。完了計画や承認済みテスト設計書の全文を移すのではなく、現在も必要な条件と確認方法だけを統合します。必要な採用理由は `decisions/` を参照します。

運用仕様では、実装箇所に適用する指示ファイル・設定項目・スクリプトを示し、確認方法には構文検査や手順の点検など、その規定に適した方法を記します。

## データ規模・性能の共通要件

[performance-and-scale.md](performance-and-scale.md) を、DB / index / cache / snapshot / package 処理 / 並列度の設計・改修前に確認します。処理速度を最優先とし、代表規模、全件処理と差分処理、計測・受入条件を定めます。根拠となる観測値は [2026-09-09 の参照記録](../acceptance/performance-reference-2026-09-09.md) に分離しています。

## 読み順

1. [architecture.md](architecture.md)
   - レイヤ構成、主要 component、native bridge の位置づけ。
2. [startup-initialization-flow.md](startup-initialization-flow.md)
   - 起動、導入可能 readiness、startup background scheduler。
3. [data-and-indexes.md](data-and-indexes.md)
   - catalog、resource index、chart-relative key、DB table。
4. [chart-info-lifecycle.md](chart-info-lifecycle.md)
   - `chart_info` currentness、actual-data hydration、parse failure、session index、backfill。
5. [path-identity.md](path-identity.md)
   - DB 上の `path` identity、case-sensitive exact match、`COLLATE NOCASE` の利用制限。
6. [path-length-and-io.md](path-length-and-io.md)
   - 長パスを含む譜面ファイル I/O、内部ファイル操作、保存 path と extended-length path の境界、読めないファイルの集約方針。
7. [library-mutation-boundary.md](library-mutation-boundary.md)
   - 譜面 / パッケージ操作の共通 mutation 境界、dialog / report、UI / model lock 契約。
8. [lr2-song-db-generation.md](lr2-song-db-generation.md)
   - LR2 連携モードの `song.db` 生成、`song` / `folder`、`.lr2folder`、自動更新設定。
9. [install-estimation-current-logic.md](install-estimation-current-logic.md)
   - 導入先推定の現行仕様。
10. [playlist-url-download-resolution.md](playlist-url-download-resolution.md)
   - プレイリスト `URL1` / `URL2` の自動ダウンロード解決、一括取り込み、対応サイト。
11. [workflows.md](workflows.md)
   - リロード、導入、再インストールなどの主要処理フロー。
12. [logging-policy.md](logging-policy.md)
   - アプリログ、性能診断ログ、出力先、ローテーション、起動引数互換。
13. [testing-strategy.md](testing-strategy.md)
   - 通常検証、parser 互換検証、大容量 fixture、性能検証の切り分け。
14. [test-authoring-contract.md](test-authoring-contract.md)
   - 既存coverage調査、test shape、shared infrastructure、flake safety、Codex handoff。
15. [codex-agent-workflow.md](codex-agent-workflow.md)
   - Codexの計画、worker委譲、並列境界、fresh reviewの運用契約。

## 機能別仕様

- [chart-file-read-pipeline.md](chart-file-read-pipeline.md)
- [chart-info-lifecycle.md](chart-info-lifecycle.md)
- [chart-info-parser-compatibility-notes.md](chart-info-parser-compatibility-notes.md)
- [startup-reload-progress.md](startup-reload-progress.md)
- [settings-change-impact-and-startup-operations.md](settings-change-impact-and-startup-operations.md)
- [path-identity.md](path-identity.md)
- [path-length-and-io.md](path-length-and-io.md)
- [library-mutation-boundary.md](library-mutation-boundary.md)
- [file-db-consistency.md](file-db-consistency.md): FS+DB の整合性、限定補償、前方回復、許容する非収束とレビュー基準。
- [lr2-song-db-generation.md](lr2-song-db-generation.md)
- [playlist-data-and-export-flow.md](playlist-data-and-export-flow.md)
- [beatoraja-table-url-import.md](beatoraja-table-url-import.md)
- [play-history.md](play-history.md)
- [playlist-url-download-resolution.md](playlist-url-download-resolution.md)
- [playback-panel-presentation.md](playback-panel-presentation.md)
- [bms-bmson-chart-abstraction-current-state.md](bms-bmson-chart-abstraction-current-state.md)
- [warning-model.md](warning-model.md)
- [duplicate-file-check.md](duplicate-file-check.md)
- [appearance-theme.md](appearance-theme.md)
- [custom-table-view.md](custom-table-view.md)
- [file-selection-dialogs.md](file-selection-dialogs.md)
- [logging-policy.md](logging-policy.md)
- [playlist-lamp-viewer.md](playlist-lamp-viewer.md)

## 開発運用

- [testing-strategy.md](testing-strategy.md)
- [test-authoring-contract.md](test-authoring-contract.md)
- [codex-agent-workflow.md](codex-agent-workflow.md)

## 旧 TECH_SPEC について

[TECH_SPEC.ja.md](TECH_SPEC.ja.md) は現在仕様を概観するための短い入口として維持します。詳細な正本は上記の機能別仕様を参照してください。
