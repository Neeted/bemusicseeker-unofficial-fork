# 設計判断

現行設計を支える理由と、採用しない代替案を置きます。実行契約・実装とテストの対応は[仕様](../spec/README.md)を正本とし、作業の経緯や完了報告は残しません。

| 判断 | 適用範囲 |
| --- | --- |
| [ライブラリ変更の操作単位化](library-mutation-session.md) | 成功依存、DB反映の粒度、限定補償。 |
| [LR2楽曲DBの一括再生成](lr2-song-db-one-shot-reconciliation.md) | 完全な入力、保存済み途中位置を再開に使わない理由。 |
| [Everythingの種別別検索](everything-query-boundary.md) | 外部検索の候補生成を暗黙の前提にしない理由。 |
| [ManagedBassの採用](managedbass-adoption.md) | ラッパーとネイティブDLLの責務、配布条件。 |
| [音声処理の設計判断](audio-library-boundaries.md) | ライブラリとの責務分担、音源ロードの並列化、音質・発音時刻・終了判定の採用理由と制約。 |
| [bmson再生の時刻と音声区間を分ける判断](bmson-playback-boundaries.md) | 整数pulseと全曲時刻の分離、実譜面の分母肥大化、固定刻みの誤差と音声区間の独立性。 |
| [厳密な有理数によるBMS時刻計算の採用](ribbit-exact-timing-adoption.md) | 実譜面の受理互換性・性能・最大7.2µsの時刻差を根拠とする本番採用、分数復元より入力の厳密値を選ぶ理由、局所退行の受容。 |
| [更新プログラムへのNative AOT採用](updater-distribution-size.md) | 配布サイズと実行時の依存。 |
| [旧更新プログラムの受入範囲](legacy-updater-acceptance.md) | 旧GUIの検査を廃止し、配布構成の移行をC#で確認する理由と退役条件。 |

[開発資料の入口](../README.md)へ戻ります。
