# Shift_JIS 非対応パスによる `song` テーブル往復更新（調査メモ）

## 概要
- 起動時初期化で、`song_tbl_load` で一部譜面が削除され、直後の `song_tbl_file_check` で同件数が再追加される往復が発生する。
- 今回の実測では `deletedSongs=24` と `added_count=24` が対応し、`db_commit_ms` が増大した。

## 観測ログ（抜粋）
- `song_tbl_load_detail ... deletedSongs=24 ...`
- `song_tbl_file_check_breakdown ... deleted_count=0 added_count=24 ... db_commit_ms=6411 ...`

## 原因
- `song_tbl_load` の正規化処理では、フォルダ CRC 計算時に `Shift_JIS` エンコードを使用する。
- パスに `Shift_JIS` 非対応文字が含まれると例外が発生し、当該レコードは削除対象になる。
- その後 `song_tbl_file_check` は実ファイルスキャン結果から同ファイルを再追加するため、毎回「削除→再追加」の往復になる。

実データ確認（2026-02-19）:
- `song.hash` の `null/empty` 件数は 0（hash 空が主因ではない）。
- `Shift_JIS` でエンコード不能な `song.path` が 26 件存在。

## 影響
- 初期化時の `song_tbl_file_check` で不要な DB 更新が発生し、`db_commit_ms` が増える。
- 対象件数が増えると起動時間悪化が顕在化しやすい。

## 今回の方針
- 本件は機能仕様変更を伴うため、今回はコード変更を行わず現状維持とする。
- 運用側で対象譜面のパスを `Shift_JIS` 互換に修正して解消する。

## 理想的な対応（将来）
1. 書き込み時バリデーション
- `song` テーブルへの Insert/Update 前に `path` の `Shift_JIS` エンコード可否をチェック。
- 不可なら書き込まない（読み取り時の削除条件と整合）。

2. 可視化 UI の提供
- `Shift_JIS` 非対応譜面の一覧をアプリ内で確認できる画面を追加。
- パス・ファイル名・推奨対応（改名/移動）を表示して、利用者が修正しやすい導線を提供。

3. ログ改善
- `song_tbl_load_detail` に削除理由内訳（例: `hash_empty`, `normalize_exception`）を出力し、原因を即判別可能にする。

## 補足
- LR2 が `Shift_JIS` 前提であるため、`Shift_JIS` 非対応パスは DB に存在しても選曲不能となる可能性が高い。
- したがって、上記の書き込み時バリデーションは性能だけでなく、実利用上の整合性改善にもなる。
