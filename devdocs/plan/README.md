# Plans And History

このディレクトリは実装計画・調査記録・移行履歴の置き場である。製品の現在仕様の正本は `../spec/` を参照する。

## 運用

- 実行中の計画は冒頭に `Status: Active` を置き、現在の受入条件と writable path を示す。
- `Status: Complete` の文書は、その snapshot での判断、実行 command、検証結果を残す履歴資料として読む。現行方針は `../spec/` と適用範囲の `AGENTS.md` を正とする。
- 完了後に恒久化する契約は `../spec/` へ統合し、計画書には archive note と固有の evidence だけを残す。固有の evidence がなければ git 履歴へ退役させる。
