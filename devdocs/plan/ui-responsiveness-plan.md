# 起動と一覧操作の応答性改善

## 目的と現状

起動時の一覧構築と、メニュー・選択・ソート・編集が重なる場合の待ち時間を調べます。起動中の操作拒否と終了後の遅延通知抑止は[起動仕様](../spec/runtime/startup.md)・[終了仕様](../spec/runtime/shutdown.md)に従います。既にある防御を作り直す計画ではありません。

## 残る確認

通常一覧の再構築でUIスレッド上に残る処理、初期化中の書込みロック範囲、Everything走査後の索引構築・所持判定・一覧反映を分けて測ります。取得済みの結果を使った非同期化と、画面反映を適切な小区間へ分ける余地を調べます。

`everything_scan`、`startup_ready_operable`、`startup_ui_blocked`、`playlist_datagrid_state`、`playlist_sortglyph_refresh`、`callback_exec_sort`、`playlist_context_menu_prepare` / `assign` / `manual_open` を操作時刻と対応させます。ログ上の静穏や低CPU使用率を成功の根拠にせず、利用者操作と実際の完了を測ります。

クォータ不足や閉鎖済みウィンドウへの例外は症状として分類し、Dispatcherの滞留や遅延通知を原因と断定する前に再現を確認します。起動の閲覧・変更受付を分ける作業は[順序統合の計画](lr2-startup-procedural-orchestration-plan.md)と調整します。

## 制約と完了条件

UIの待ち時間を減らすために競合する変更を並列化せず、設定画面や確定済み一覧を必要以上に閉じません。新しい通知・受付状態の保存は、必要性を示してから判断します。代表操作の応答と完了時間、終了中の安全性、対象外操作の同等性を確認し、採用した契約を対応仕様へ移します。
