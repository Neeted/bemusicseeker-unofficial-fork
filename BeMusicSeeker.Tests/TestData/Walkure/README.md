# Walkure 固定回帰入力

出典は [naktazdim/walkure-offline の固定版](https://github.com/naktazdim/walkure-offline/tree/3836d501cd05da8fd8fd4fdc1881b3115399c55b)です。モデル更新の承認なしに期待値を更新しません。著作権表示は `Copyright (c) 2026 walkure.net`、[MIT 全文](../../../third_party/licenses/21-Walkure-MIT.txt)を正本としてモデル・固定入力とともに配布します。

- `legacy-player-report.json`: 上流 `test/fixtures/legacy-player-report.json` の未改変コピー。θ=-0.18823981285095215、★6.60、推薦654件中の通常譜面649件をキー・目標ランプ・二桁確率で比較します。段位5候補は表へ表示しません。
- `legacy-lr2-observations.json`: 上流 `test/fixtures/LR2beta3/LR2files/Database/Score/nakt.db` を同固定版 `src/infrastructure/lr2.js` の `createLr2Reader().loadPlayerSnapshot()` で一度読んだキーと正規化ランプ424件です。段位を除く加工はせず、製品 reader へ通しません。
- `math-cases.json`: 同固定版 `src/domain/player-rating.js` の `estimatePlayerRating` へ明示した原観測を渡した独立期待値です。5件の混合ランプ、標準、全モデルの未観測を failed で補充、failed を除外する三方針を含みます。製品関数で期待値を計算しません。

採取では、リポジトリ外の一時作業先へ固定版の reader・数学・直接依存だけを取得し、Node.js の `node:sqlite` を読み取り専用で開く IO 境界として reader に注入しました。reader 自身が `SELECT lower(hash) AS hash, clear FROM score` と上流のランプ正規化を実行し、finally で接続を閉じた後、`scores` Map を `{md5, clearLamp}` の配列へ保存します。原観測とモデルをキーで突合し、原数学へ `{md5, clearLamp, modelEntry}` の配列と固定換算点を渡します。原推薦関数で通常候補649件も確認します。

製品の LR2 reader は raw clear=2 を op_history とともに解釈し、履歴なしを INVALID とするため、数学 oracle の原 reader とは意味が異なります。実 DB との接続は小さい合成 DB で別に検証します。恒久テストは本番の実埋込みモデルをロードし、Node.js、上流 checkout、ネットワーク、外部 DB を要求しません。列挙順・同率候補の二次順序は契約にしません。

θの許容差は 1e-9、★と確率は二桁の数値で一致します。逆推薦は対象外です。モデルと fixture の欠落は失敗とし、外部取得で補完しません。

固定 `nakt.db` の原readerは424観測を返し、そのうち374がモデルに一致します。段位キーを除く加工はしていませんが、この入力に実際のcourse観測はありません。段位は製品readerの合成LR2 DBで160文字の複合キーを保持する接続ケースと、`base=failed` の1264件補充で確認します。
