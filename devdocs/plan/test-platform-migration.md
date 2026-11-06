# テスト基盤の残課題

検証区分・期限・所有と回収・結果判定は[テスト検証](../spec/development/testing.md)、module選択と配布物の受渡しは[標準入口](../../tools/BeMusicSeeker.Verify/README.md)を正本とする。

## 現状

テスト基盤の移行と追加の受入シナリオは完了し、Fullの必須IDと実行範囲は正本へ反映済みである。残るのは、追加速度目標の判定と改善、環境不足で未実行の条件、移行中に見つけたテスト側の改善候補だけである。

## 追加速度目標

日常中央値60秒・p95 90秒、Full10分の追加速度目標は後段で判定する。同じ対象・条件で準備から回収までを比較する。対象削減・skipや運用上限の延長を性能改善と扱わず、各修正で必要な既存の性能回帰検証を維持する。

改善候補として、[PlaylistCatalogViewTestSupport](../../tests/BeMusicSeeker.TestSupport/Components/PlaylistCatalogViewTestSupport.cs) のスコア登録を一つのトランザクションへまとめる案がある。採用時は同じ条件の前後比較で効果を確認する。

## 未実施条件

権限不足のシンボリックリンク6条件と入力不足の外部encoder 1条件は、合意どおり未実行・未成功として残す。必要な権限・入力が揃った時点で再開し、移行対応済みであることと実行成功を区別する。

## テスト側の改善候補

いずれも現行の検証を止めていない。着手時に現行ソースで到達を確認し、未確認の事象は仮説として扱う。

| 対象 | 内容 |
| --- | --- |
| [SettingsStartupTestFixture](../../tests/BeMusicSeeker.TestSupport/Wpf/SettingsStartupTestFixture.cs) の起動後処理の終端待ち | 起動後グループの集計外で必須の処理（遅延ランキング更新など）が最後に終わると、待機を起こす通知が来ず待ち続けうる。IR有効とスコアDB指定を同時に使う呼出しで到達しうる。現状の呼出しでの到達は未確認である。採用時は終端の判定と通知の順序を揃える。 |
| 初期化完了直後に明示的なライブラリ操作へ進むWPF fixture | 実画面のfixtureは起動後処理の終端を待ってから操作へ進む。`SettingsWindowPresentationTests` の初期化完了直後に設定適用のスキャンへ進む経路は、同じ受付の競合で失敗する候補として未確認である。 |
| `UpdateMarkerReaderSharingTests` の更新ツール解決 | 更新ツールのDLLを `WindowsProcessProbe` のビルド出力内の固定パスから読む。更新ツールの解決をTestSupportへ集約する際に置き換える。 |
| Scenarios/Update の `UpdaterPackageSyncTests.WindowsGatewayCancelsAndPreparesNextUpdate` の間欠失敗 | 13回の実行で1回だけ失敗し、その後のFullでは再発していない。失敗時の症状は記録していない。再発時は期限延長や再試行ではなく原因を特定する。 |
