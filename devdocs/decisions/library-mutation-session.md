# ライブラリ変更を操作単位の session に集約する判断

状態: 採用済み
整理日: 2026-09-16

## 背景

一回のライブラリ変更で複数の譜面・フォルダ・パッケージを扱う。対象ごとの filesystem（FS）処理と、後続対象の分類には逐次性がある。一方、各対象で DB、所持索引、関連参照、LR2 同期、通知まで完結させると、変更件数に比例して同じ反映と公開を繰り返す。

`2a36701a` の pending estimated install は、未実行の予約を所有済みと誤認しないため、先行 package の実成功を後続の分類へ渡す順序を採用した。維持すべきなのはこの成功依存であり、package ごとに canonical DB を commit することではない。手動で導入先を消した package、未実行・skip・失敗・取消対象を所有済みと見なさない。

`0ae8e615` の file/DB 補償境界は、自動 rename の変更を最後に一括反映する構造から、folder ごとの executor と durable apply へ分解していた。source・上書き先の保全に有用な staging / backup は残すが、通常は発生しない FS / DB / in-memory 例外の完全補償を、正常系の per-item commit の根拠にはしない。

## 採用する境界

```text
1 user operation = 1 LibraryMutationSession = N confirmed changes
```

一回の command が既存の mutation lease / capability を所有し、その内側で physical success の確定 facts を収集する。append は canonical state を変更しない。session の `Commit` が各 owner の apply を一括して呼び、必須反映の成功後に通常通知を準備する。単一対象も同じ API を使い、coordinator から直接 canonical apply する互換経路は持たない。

catalog DB、package/install rows、外部出力のすべてを一つの物理 transaction に統合する判断ではない。既存 owner の writer / transaction 境界を維持する。install の storage と install-row 更新のように一体で保存する surface は同じ transaction を使い、gateway 内の SQL parameter chunking は許容する。「一回」は、対象件数だけ同じ canonical surface の apply を繰り返さない意味である。

先行成功への依存は、操作開始時の installed lookup / destination state と、先行 physical success の hash / 実 destination を持つ operation-local overlay で満たす。overlay は永続化せず、DB の durable result、canonical index generation、public notification の代用にしない。後続判断に必要でない状態は追加しない。

## 失敗と保全の判断

事前に確定できる source 欠落、衝突、stale、承認済み no-op は機能ごとの skip / reject 契約に従う。予期しない physical failure は未確認対象と依存する後続を停止し、確認済みの成功集合には一回の session commit を試みる。DB failure、durable 後の必須反映 failure、cleanup failure は別々の facts として同じ terminal に保持する。

session canonical apply の失敗を理由に、全 physical change の rollback、transaction replay、再帰的 compensation を追加しない。局所 package executor の staging / backup と一回限りの physical compensation は、上書き保護が必要な範囲に限定して維持する。`FileDbMutationReceipt` はその局所結果と cleanup の引継ぎに使い、操作全体の終端には `LibraryMutationSessionReceipt` を使う。source cleanup は durable point 後とし、cleanup failure で canonical apply を再実行しない。

merge 後の maintenance は、必要な既存予約の解放・再取得を維持するが、論理的には同じ利用者操作の必須後処理である。対象は canonical owner の移転後・source cleanup 前に固定し、maintenance の例外・受付拒否も同じ terminal に保持する。第二の session や別の成功報告には分割しない。

pending package の追加・単純削除・推定先設定・pending-only 拡張子修正など、owned catalog を変更しない操作は `PackageLifecycleOwner` の契約に留める。install / repair の一部として発生する lifecycle change は当該 session に接続する。

## 採用しない代替案

| 代替案 | 採用しない理由 |
| --- | --- |
| package ごとの DB commit を後続判断への通信に使う | 成功依存を満たすために、索引反映・公開まで item 数だけ繰り返す必要はない。成功 overlay で依存を表現できる。 |
| FS と全 DB を擬似的な一つの transaction にする | 既存 owner の境界を越え、全体 rollback / replay と保全用の永続状態を要求する。既存の非原子性と明示的な失敗結果を維持する。 |
| 操作後に全 catalog / reverse lookup を再構築する | 局所変更のための全件処理を正当化しない。確定した旧新 facts と対象集合で差分反映する。 |
| 自動 rename に専用の LR2 先取り snapshot と通知経路を残す | catalog 確定 receipt から同じ局所 BMS query を実行できる。共通 session 内の LR2 同期と通知に統合し、二重実行・失敗結果の分散を避ける。 |
| reason 文字列で反映・通知 policy を選ぶ | 診断名と挙動が結合する。必要な row-path 通知方針は typed policy で渡す。 |

## 恒久仕様と検証

契約と各操作の実装・テスト対応は [ライブラリ変更境界](../spec/library-mutation-boundary.md#mutation-session-契約)、保全・非保証範囲は [FS/DB整合](../spec/file-db-consistency.md)、規模・集約診断は [性能要件](../spec/performance-and-scale.md#43-mutation-session-の集約診断) を正本とする。

folder table の relocation は distinct な旧 exact path を parameterized `IN` query の chunk で取得し、取得行だけを同じ transaction で移転する。対象行がなければ no-op とし、全 table fallback scan は行わない。反映回数は owner の実呼出箇所で集計し、正常・失敗の永続結果と合わせて検証する。反映回数が減ったことと wall-clock の改善は区別し、異なる操作・完了範囲の時間で退行を相殺しない。
