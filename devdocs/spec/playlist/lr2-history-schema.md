# LR2プレイ履歴スキーマ

## 目的と適用範囲

LR2のプレイヤー別スコアDBに追加する表・索引・トリガーと、明示的な導入・修復・削除を定めます。楽曲一覧の `song.db` ではなく、連携プロファイルが指すスコアDBが対象です。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 互換性と所有範囲

LR2が使うSQLite 3.6.7で動くSQLを使います。UPSERT構文、CTE、ウィンドウ関数、RETURNING、部分・式索引、生成列に依存しません。既存の `score` と `player` の必要列・型を検査し、本アプリが管理する名前のオブジェクトだけを操作します。

起動・スコア読込み・履歴表示は検査と読取りであり、DDLや自動修復を行いません。設定画面から対象DBを確認して明示的に導入・修復します。履歴導入前のスコアを過去のプレイとして補いません。

### 表と索引

| オブジェクト | 役割 |
| --- | --- |
| `bms_lr2_play_history` | `history_id INTEGER PRIMARY KEY AUTOINCREMENT`、ハッシュ、Unix秒の `played_at`、既定0の `finalized`、`score_write_type` と旧新のスコア・プレイヤー集計値を保持する。 |
| `bms_lr2_last_play` | `hash TEXT PRIMARY KEY` と非NULLの `last_play_at INTEGER`。最終プレイ順の参照元。 |
| `bms_lr2_play_pending` | `id=1` に限定した一行で、履歴ID・ハッシュ・作成時刻を保持する。スコア書込みと直後のプレイヤー集計を結ぶ。 |
| `idx_bms_lr2_play_history_hash_time` | `(hash, played_at DESC, history_id DESC)`。 |
| `idx_bms_lr2_play_history_time` | `(played_at DESC, history_id DESC)`。 |

スコア側の旧新値は、プレイ・クリア・失敗回数、クリア種別、BP、EXスコア、コンボ、ノーツ、complete、オプション、シード、scorehashです。EXスコアは `perfect*2+great` です。プレイヤー側はプレイ回数、演奏時間、判定総数・各判定数、最大コンボの旧新値と差分を保持します。

### アプリ所有表の永続契約

次の列構成を永続データの契約とします。制約欄はDDL上の制約を示し、`PRIMARY KEY` とだけ記す列には明示的な `NOT NULL` を追加しません。

| 表 | 列 | SQLite型 | 制約・既定値 |
| --- | --- | --- | --- |
| `bms_lr2_last_play` | `hash` | `TEXT` | `PRIMARY KEY` |
|  | `last_play_at` | `INTEGER` | `NOT NULL` |
| `bms_lr2_play_pending` | `id` | `INTEGER` | `PRIMARY KEY CHECK (id = 1)` |
|  | `history_id` | `INTEGER` | `NOT NULL` |
|  | `hash` | `TEXT` | `NOT NULL` |
|  | `created_at` | `INTEGER` | `NOT NULL` |
| `bms_lr2_play_history` | `history_id` | `INTEGER` | `PRIMARY KEY AUTOINCREMENT` |
|  | `hash` | `TEXT` | `NOT NULL` |
|  | `played_at` | `INTEGER` | `NOT NULL` |
|  | `finalized` | `INTEGER` | `NOT NULL DEFAULT 0` |
|  | `score_write_type` | `TEXT` | `NOT NULL` |
|  | `old_playcount` | `INTEGER` | NULL可 |
|  | `new_playcount`, `playcount_delta` | `INTEGER` | `NOT NULL` |
|  | `old_clearcount`, `new_clearcount`, `clearcount_delta`, `old_failcount`, `new_failcount`, `failcount_delta` | `INTEGER` | NULL可 |
|  | `old_clear`, `new_clear`, `old_clear_db`, `new_clear_db`, `old_clear_sd`, `new_clear_sd`, `old_clear_ex`, `new_clear_ex` | `INTEGER` | NULL可 |
|  | `old_minbp`, `new_minbp`, `old_exscore`, `new_exscore`, `old_maxcombo`, `new_maxcombo`, `old_totalnotes`, `new_totalnotes` | `INTEGER` | NULL可 |
|  | `old_complete`, `new_complete`, `old_op_best`, `new_op_best`, `old_op_history`, `new_op_history`, `old_rseed`, `new_rseed` | `INTEGER` | NULL可 |
|  | `old_scorehash`, `new_scorehash` | `TEXT` | NULL可 |
|  | `old_player_playcount`, `new_player_playcount`, `player_playcount_delta`, `old_playtime_total`, `new_playtime_total`, `playtime_delta` | `INTEGER` | NULL可 |
|  | `old_judge_total`, `new_judge_total`, `judge_delta`, `old_player_perfect`, `new_player_perfect`, `perfect_delta` | `INTEGER` | NULL可 |
|  | `old_player_great`, `new_player_great`, `great_delta`, `old_player_good`, `new_player_good`, `good_delta` | `INTEGER` | NULL可 |
|  | `old_player_bad`, `new_player_bad`, `bad_delta`, `old_player_poor`, `new_player_poor`, `poor_delta`, `old_player_maxcombo`, `new_player_maxcombo` | `INTEGER` | NULL可 |

`score_write_type` は `score` 行の追加を `insert`、既存行の更新を `update` として記録します。`old_judge_total` / `new_judge_total` は `perfect + great + good + bad + poor` の累計で、`judge_delta` はその差分です。

初回導入時は `bms_lr2_last_play`、`bms_lr2_play_history`、`bms_lr2_play_pending` を空から開始し、既存 `score` 行から日時や履歴を補いません。通常の `score` 行削除でも `bms_lr2_play_history` と `bms_lr2_last_play` は削除せず、観測済みの履歴と最終プレイ時刻を保持します。履歴自体を消すのは、利用者が表を含む明示削除を選んだ場合だけです。

`op_best` はbest EXスコアを更新したプレイのオプションのスナップショットであり、bestを更新しなかったプレイの実オプションではありません。`rseed` もbestスコア更新時の値として扱います。`op_history` は個別プレイのオプションではなく、一定以上のクリアで達成済みのオプションbitを累積した値です。履歴表示は旧新値の差から変化を求め、変化していないbest値や累積値をそのプレイの実績として読み替えません。

### スコア書込み時の記録

`bms_lr2_score_history_after_insert` は `score` の追加で `NEW.playcount>0` の場合だけ履歴を追加します。旧スコアなしは旧値のNULLで表します。`bms_lr2_score_history_after_update_playcount` は `UPDATE OF playcount` で新回数が旧回数を上回る場合だけ旧新値と差分を追加します。単なるベスト値の編集や回数が増えない更新をプレイとして数えません。

時刻は `CAST(strftime('%s','now') AS INTEGER)` です。追加した履歴を未確定のまま保留一行へ接続し、同じ時刻でハッシュ別最終プレイを `INSERT OR REPLACE` します。次のスコア記録は保留一行を置き換えますが、既存の未確定履歴は消しません。

### プレイヤー集計による確定

`bms_lr2_player_history_after_update` は集計列更新でプレイ回数が増え、保留行の作成時刻が現在から10秒以内の条件を満たすと、その履歴へ旧新集計と差分を記録して `finalized=1` にします。その後、保留行を消します。

`bms_lr2_player_history_cleanup_stale_pending` は同じ回数増加で保留が10秒より古い場合に保留だけを消し、古い履歴を確定しません。トリガー間の順序と時間窓に基づく関連付けであり、任意の外部書込み順から全プレイの詳細を復元する保証ではありません。

### 検査結果と変更操作

| 状態 | 条件と操作 |
| --- | --- |
| `Installed` | 必要表・列・索引・トリガーが揃う。導入処理は変更しない。 |
| `NotInstalled` | 管理対象がまだない。明示導入できる。 |
| `Repairable` | 表と列が互換で、索引・トリガーに不足または不一致がある。明示修復できる。 |
| `ManualRepairRequired` | 基本表・列の不備、互換でない列、一部だけ残る管理表、予約名の型衝突等。自動修復しない。 |
| `Unreadable` | DBの場所・読込み・検査・変更の失敗。原因を示す。 |
| `SkippedProfile` | LR2連携でない。検査・変更対象にしない。 |

`InstallOrRepair` は未導入・修復可能の場合だけ書きます。削除はトリガーだけを外して記録停止・履歴保持とする方式、表と索引も外す方式を選べます。名前だけで異なる型のオブジェクトを消しません。必要な変更はトランザクションで行い、操作後に再検査します。

設定画面は得られた状態を表示し、開くたびに再検査しません。明示操作前後と通常のスコア・履歴読込みで状態を更新します。成功後は履歴読込みキャッシュを無効にします。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 列・索引・トリガー・衝突・明示操作 | [`Lr2PlayHistorySchemaService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/Lr2PlayHistorySchemaService.cs) | [`Lr2PlayHistorySchemaServiceTests`](../../../BeMusicSeeker.Tests/PlayHistory/Lr2PlayHistorySchemaServiceTests.cs) |
| 状態表示・導入と削除の受付 | [`Lr2PlayHistorySchemaService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/Lr2PlayHistorySchemaService.cs) | [`Lr2PlayHistorySchemaUiTests`](../../../BeMusicSeeker.Tests/PlayHistory/Lr2PlayHistorySchemaUiTests.cs) |
| 記録された値と読取り条件 | [`Lr2PlayHistoryReader`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlayHistory/Lr2PlayHistoryReader.cs) | [`PlayHistoryReadModelTests`](../../../BeMusicSeeker.Tests/PlayHistory/PlayHistoryReadModelTests.cs) |

## 関連資料

[履歴表示](play-history.md)、[LR2カスタムフォルダ](lr2-custom-folders.md)を参照します。
