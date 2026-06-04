# LR2 非対応パス warning

## 概要
- LR2 の `song.folder` / `song.parent` は Shift_JIS 文字列を前提にした CRC で計算される。
- パスに Shift_JIS で表現できない文字が含まれる譜面は、LR2 用の folder/parent ID を計算できない。
- LR2 は古い path 長制限にも影響されるため、譜面 path や resource path が長すぎる場合も warning として表示する。
- 現在は当該レコードを削除せず、`Lr2Compatibility` warning を付けて `LR2非対応パス` 画面に表示する。

## 観測ログ（抜粋）
- `song_tbl_load_detail ... deletedSongs=24 ...`
- `song_tbl_file_check_breakdown ... deleted_count=0 added_count=24 ... db_commit_ms=6411 ...`

## 原因
- 旧実装では、`song_tbl_load` の folder/parent CRC 正規化で Shift_JIS エンコード例外が発生すると、当該 `song` 行を削除対象にしていた。
- その後 `song_tbl_file_check` が実ファイルスキャン結果から同ファイルを再追加するため、毎回「削除→再追加」の往復になっていた。

実データ確認（2026-02-19）:
- `song.hash` の `null/empty` 件数は 0（hash 空が主因ではない）。
- `Shift_JIS` でエンコード不能な `song.path` が 26 件存在。

## 影響
- 旧実装では初期化時の `song_tbl_file_check` で不要な DB 更新が発生し、`db_commit_ms` が増えていた。
- 現在は削除しないため、削除→再追加の往復更新は発生しない。
- ただし LR2 側では選曲不能になる可能性が高いため、ユーザーが改名または移動できるよう warning として可視化する。

## 現行仕様
- file diff で新規追加された BMS は、DB insert 前に `folder` / `parent` CRC を設定する。
- package install などの `UpsertSongs()` 経路でも、保存前に同じ正規化を行う。
- LR2 非対応 path / resource path の場合:
  - `song` 行は削除しない。
  - 譜面 path が Shift_JIS 非対応で `folder` / `parent` CRC を計算できない場合は、`folder` / `parent` を空のままにする。
  - `Lr2PathEncodingUnsupported` / `Lr2PathTooLong` / `Lr2ResourcePathUnsupported` / `Lr2ResourcePathTooLong` warning を maintenance facts から付ける。
  - `LR2非対応パス` 画面に表示する。
- relative path 補正では、絶対 path 化と CRC 計算が両方成功した場合だけ `song.path` を更新する。Shift_JIS 非対応時は中途半端な path 更新を残さない。

## 今後の改善候補
- `LR2非対応パス` 画面で、パス・ファイル名・推奨対応（改名/移動）をより分かりやすく表示する。
- `song_tbl_load_detail` に削除理由内訳（例: `hash_empty`, `normalize_exception`）を出力し、原因を即判別可能にする。

## 補足
- LR2 が `Shift_JIS` 前提であるため、`Shift_JIS` 非対応パスは DB に存在しても選曲不能となる可能性が高い。
- BeMusicSeeker 上では削除せず可視化するが、最終的な対応は Shift_JIS 互換パスへの改名または移動になる。
