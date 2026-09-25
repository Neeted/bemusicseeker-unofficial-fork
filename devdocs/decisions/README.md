# 設計判断

現行設計を支える理由と、採用しない代替案を置きます。実行契約・実装とテストの対応は[仕様](../spec/README.md)を正本とし、作業の経緯や完了報告は残しません。

| 判断 | 適用範囲 |
| --- | --- |
| [ライブラリ変更の操作単位化](library-mutation-session.md) | 成功依存、DB反映の粒度、限定補償。 |
| [LR2楽曲DBの一括再生成](lr2-song-db-one-shot-reconciliation.md) | 完全な入力、保存済み途中位置を再開に使わない理由。 |
| [Everythingの種別別検索](everything-query-boundary.md) | 外部検索の候補生成を暗黙の前提にしない理由。 |
| [ManagedBassの採用](managedbass-adoption.md) | ラッパーとネイティブDLLの責務、配布条件。 |
| [音声処理のライブラリとアプリの分担](audio-library-boundaries.md) | 24bit量子化の実測根拠、共通音量・正規化・入力接続層を持つ理由と見直し条件。 |
| [更新プログラムへのNative AOT採用](updater-distribution-size.md) | 配布サイズと実行時の依存。 |
| [旧更新プログラムの受入範囲](legacy-updater-acceptance.md) | 旧GUIの検査を廃止し、配布構成の移行をC#で確認する理由と退役条件。 |

[開発資料の入口](../README.md)へ戻ります。
