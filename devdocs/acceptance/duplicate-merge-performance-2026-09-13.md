# 性能対策後の重複マージ2回の観測

状態: 提供ログの分析記録（2026-09-13）。改善後の再測定ではない。

## 条件と範囲

- 利用者が性能対策後のビルドで重複チェックからマージを2回実行した `install-performance.log` を提供した。
- ログの区間は2026-09-13 13:10:02.477～13:10:36.008。コードの対応確認は `6d35c789`。ログ自体にはビルドcommitの識別子がないため、同commitで生成されたことの独立した証明ではない。
- 1回目 `op=733765346993`、2回目 `op=733970329769`。初回の入力はBMS 211,721行、BMSON 1,065行、primary hash 212,377件。full lookupのMD5/SHAキーは424,754件、directory参照425,568件。これらは実ファイル数ではない。
- 両方とも移動譜面数0、source側除去が6件・3件。譜面の実移動を伴うmergeや大容量resource転送の性能を、この数値から判断しない。ストレージ条件、OS cache状態、一般的なmerge全体の分布は未測定。

## 内訳

| 区間・処理 | 1回目 | 2回目 |
| --- | ---: | ---: |
| モデル処理全体 | 5,641 ms | 5,443 ms |
| primary hash / excluding用のcold構築 | 667 ms | 692 ms |
| full installed lookup構築 | 4,249 ms | 4,294 ms |
| 上記2索引の合計 / モデル全体の割合 | 4,916 ms / 87.1% | 4,986 ms / 91.6% |
| catalog apply全体 | 347 ms | 256 ms |
| そのうちBMS削除DB処理 | 2 ms | 1 ms |
| LR2 normal folder同期の内部marker | 6 ms | 7 ms |
| maintenance wall-clock | 22 ms | 8 ms |
| モデルdone後からUI refresh doneまで | 約372 ms | 約402 ms |
| 後続playlist index prewarmのbuild | 2,265 ms | 3,550 ms |
| prewarm全体（500 ms debounceを含む） | 2,779 ms | 4,063 ms |
| モデルstartから上記prewarm完了まで | 約8.794 s | 約9.909 s |

区間には親子関係があるため各行を合計しない。maintenance内の並列compute累積値もwall-clockへ加算しない。最後の行は利用者のUI待ち時間ではなく、後続background構築まで含めたログ上の区間である。1回目のprewarm終了は2回目開始より前であり、重なりによる説明は要らない。

各操作でinstalled primary/fullとplaylist resolveの失効を記録し、次の操作またはprewarmで再構築している。resource reverse lookupは対象directoryの差分を適用しており、両操作でfullを維持、warmup不要。LR2は各3 count query・1 range query・既存参照訪問0であり、この記録の主因ではない。

## コードとの対応と結論

`LibraryFileOperationOwner.BuildMergeCatalogDelta` が除去をpath-onlyの `PathCleanup` として作り、共通applyもその要求から旧snapshotを生成するためhashが不足する。共通DB writerを通っていても、installed lookup / playlist解決に十分な除去factsが届かない。

また、merge準備時のfull installed snapshot捕捉はsource有無の確定判定より前にある。no-op前の取得と、必要な所有判定の入力範囲も見直す。

R3～R6の個別改善を否定する記録ではないが、R5bの「連続mergeで全失効・再構築を避ける」という操作の完了条件は未達である。次の対応は [変更要求統合計画](../plan/library-mutation-unification-plan.md) にまとめる。2件のログから汎用的な改善率や、修正後の秒数は推定しない。
