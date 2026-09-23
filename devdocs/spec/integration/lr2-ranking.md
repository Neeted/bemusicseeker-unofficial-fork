# LR2IRスコアと順位の更新

## 目的と適用範囲

LR2IRの本人スコア、ローカル順位キャッシュ、未送信判定と表示用集計を定めます。楽曲DBの生成やプレイ履歴とは独立した連携です。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 二つの情報源

本人スコアXMLと `ir_score` / `ir_score_refresh_metadata` は未送信判定とUNSENT SONGS出力に使います。順位キャッシュXMLと `ir_data` は順位表示とオフラインスコアの順位推定に使います。有効設定も別々です。

本人スコア取得を無効にすると、取得、保存、その情報による未送信判定を行いません。起動時順位キャッシュ更新を無効にすると、ローカルXMLの走査・読込み・保存を行いません。両方無効なら起動時の順位更新を予約しません。

### 予約と要求の鮮度

予約には、現在の初期化がスコア更新を要求すること、取得元がLR2であること、スコアDBパスがnullでないこと、二系統のいずれかが有効であることを要求します。`OperationModeLR2DB`、プレイヤーID、オフライン順位推定の設定を予約条件へ追加しません。

予約時の設定を使って受付を判断し、実行時に最新設定から二系統の作業を再評価します。プレイヤーID0は実行時に処理不要とする条件であり、予約の有無は変えません。

本人スコアはID確定後から通信・XML解析・正規化ハッシュの計算を先行できます。順位更新は現在の結果だけを受け取り、読込み、置換・更新、メモリへの統合を行います。XMLの譜面側の更新時刻 `lastupdate` はスコア内容の同一性判定から除きます。通信の取消・終了待ち・取得結果の再利用はデータ仕様と終了仕様に従います。

### 順位キャッシュの解析

`ir_data.lastcacheupdate` とXML末尾の更新時刻で再読込み対象を判断します。既定の並列数は `max(1, ProcessorCount - 1)` で、処理数を制限します。起動時、手動取得、`LR2IRCache` は共通の解析を使います。

更新用の解析は全順位の行集合を作らず、スコア要素を一回読み、人数、平均、標本標準偏差、本人のスコア、順位を集計します。ID、クリア、ノート数、コンボ、PG、GR、最小BPが0以上の整数である行だけを使い、不正・負数を含む行は除きます。

順位は本人より高いスコアの件数+1です。本人がいなければ未プレイかつ順位-1の行を作ります。XMLの更新時刻が空・不正・NUL終端ならファイル更新時刻を使います。解析自体の失敗はスキップし、旧式の全件解析へ切り替えません。`xmlFallbackLoads` は診断互換の項目であり、切替経路の存在を意味しません。

### オフライン順位推定と保存

推定が有効でローカルスコアがIRの値より高い場合だけ、順位計算用の小さな情報を要求時に読みます。同じ更新中に読み込んだハッシュは再利用します。起動時キャッシュ更新が無効なら、その経路から推定結果を自動更新しません。

対象IDの `ir_data` が空の場合は、重複排除した行を一つのトランザクションで一括追加します。既存行がある場合や一括追加の条件を満たさない場合はハッシュごとに更新します。互換性のため一意制約は追加せず、`ir_data_idx_lr2id_hash(lr2id, hash)` の非一意索引を使います。

`ranking_cache_refresh done` は読込みと保存時間、並列数、解析行数、解析失敗、互換項目、一括追加の利用、追加の推定読込みを記録します。過去の測定値を現行の時間上限にはしません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 予約条件、本人スコアの事前取得と設定変更 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`StartupRankingRefreshPolicyTests`](../../../BeMusicSeeker.Tests/Startup/StartupRankingRefreshPolicyTests.cs)、[`BmsLibraryIrStartupTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs) |
| XML集計、順位と未送信、保存 | [`LR2IRCache`](../../../BeMusicSeeker/Models/Ir/LR2IRCache.cs) | [`BmsLibraryIrServiceTests`](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs) |
| キャッシュの取得要求、確認、取消、成功・失敗件数の通知 | [`RankingCacheDownloadWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/RankingCacheDownloadWorkflowOwner.cs) | [`RankingCacheDownloadWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Startup/RankingCacheDownloadWorkflowOwnerTests.cs) |

## 関連資料

[データと索引](../core/data-and-indexes.md)、[起動](../runtime/startup.md)、[終了](../runtime/shutdown.md)、[LR2楽曲DB](lr2-song-db.md)を参照します。
