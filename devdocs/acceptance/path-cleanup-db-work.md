# 対象パス削除のDB処理量

## 現在の用途と条件

`CatalogMutationOwner.ApplyCatalogMutation` の対象限定削除で、背景行数に連動する不要な処理を識別する測定方法です。操作全体の速度測定ではありません。

2026-09-12の比較で、基準は `474fbf6b81fe77153fec8d4d8ef813f2786b4b74`、候補は当時の対象集合化の差分を適用した作業ツリーです。Windows 11 Pro `10.0.22635`、Ryzen 5 2600（論理12プロセッサ）、メモリ34,288,893,952バイト、SDK `10.0.303`、Release / x64 / net10.0-windowsを使用しました。

BMS・BMSONの背景を各16行と128行とし、対象はBMS 2行、BMSON 1行、保守情報だけの1行の4パスで固定します。一つのBMSは背景とMD5を共有します。固有の一時SQLiteでDB・格納行・所持集合を一致させ、主キー・ハッシュ索引準備後の実書込み接続を観測します。準備と別接続からの読取り確認は計数外です。

## 観測方法と結果

SQLiteの `trace_v2` から返却行とSQL完了を観測し、`FULLSCAN_STEP` と `VM_STEP` を集計します。`VM_STEP` は仮想機械の命令数であり、全表走査の数に出ない索引範囲や、返却行のないSQLの費用も検出する補助指標です。返却行数を割当メモリの実測値とは扱いません。

| 条件 | カタログSQLの返却行 | 全SQLのFULLSCAN_STEP | 全SQLのVM_STEP | PROFILEを得たSQL |
| --- | ---: | ---: | ---: | ---: |
| 背景BMS / BMSON各16行 | 0 | 188 | 1980 | 39 |
| 背景BMS / BMSON各128行 | 0 | 188 | 1980 | 39 |


スキーマ・一時集合の処理も含むため、188・1980やSQL総数そのものを固定契約にはしません。固定対象だけの除去、残る正確なパス集合、共有ダイジェスト保持、最後の所有者を除く場合の削除、BMSONとの分離を同時に確認する設計です。

比較用に全表読取りを戻すと、背景16行でBMS 20行・保守36行・BMSON 17行、合計73行が返り、対象集合の返却行上限で区別できました。また、ハッシュ索引の広い範囲を読む結合では、返却行とハッシュ収集の全表走査が0のまま、全SQLの `VM_STEP` が背景16行の2100から128行の2996へ増えました。一種類の計数だけでは不十分である根拠です。

## 検証との対応と制限

[`CatalogMutationOwnerTests`](../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) の `ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership` と [`SqliteStatementObservation`](../../BeMusicSeeker.Tests/Helpers/SqliteStatementObservation.cs) が、実接続で結果・処理量を確認します。操作後の別接続で作った補助的な実行計画は、本番実行中や大規模入力の計画を証明しません。

実ライブラリの全体時間や800万規模のリソース逆引きはこの入力の範囲外です。現行の契約は[パスの識別](../spec/core/path-identity.md)、実規模の測定は[性能計画](../plan/library-mutation-performance-plan.md)を参照します。
