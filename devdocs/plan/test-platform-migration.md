# テスト基盤移行の残作業

## 目的と範囲

日常回帰を短時間で検出し、独立した資源で並列実行できる基盤へ統合する。現行の実行契約は[テスト検証](../spec/development/testing.md)を正本とする。

## 採用する方針

MSTest.Sdkとnative MTPを共通基盤とし、発見・DataRow展開・実行・標準結果をMTPへ任せる。次の作業では `tools/BeMusicSeeker.Verify` に薄いC#入口を置き、Core、Windows、Scenarios、Desktop.E2E、Compatibility、Performanceの6テストプロジェクトと用途別の共通補助へ分ける。

単一Testsに残るPowerShellの分割計画・メタデータ発見・FQN列挙は移行中の接続であり、恒久構成にしない。以下を一体で置換する。

- プロジェクトのCompile対象、solution/filter、runnerのmodule計画を同時に切り替え、欠落と二重実行を防ぐ。
- C#入口へ整形、準備、process所有と期限、MTP起動、標準結果の集約を移し、旧 `verify-refactor`、discovery、shard、結果・process補助を退役させる。配布用publish/releaseは配布責務として残す。
- 旧runner専用の検査とMTP接続用の一時fixtureは、新入口の保証が成立した時点で統合・削除する。ゼロ件、全skip、非成功exit、missing report、timeout、cleanup失敗を成功にしない保証は維持する。
- 旧 `VerificationProcessLifecycleTests` の固定時刻に依存するstream待機・複数root cleanupの検査は、新しいprocess所有の検査へ引き継ぐ。旧VSTestでも失敗する条件があるため、移行時の失敗をMTP固有と決めつけず、実終了・両pipe回収・所有子の残留を独立して観測する。期限・期待値の緩和や恒久的な除外で解消した扱いにしない。
- 共有DB・設定・言語・一時領域・WPF/native資源の所有を先に整理してから並列度を調整する。旧6ホストの名前・割当規則を新基盤の契約として引き継がない。

## 完了条件

旧ケースの入力と後継保証を照合し、不要となった旧処理・補助・文書を同じ整理で削除する。標準発見結果や過去の8,520件を恒久rosterにしない。新入口の必要な回帰・失敗回収・実シナリオを確認し、同一条件で実行時間を測定する。実機・環境依存・性能の未実施範囲を通常機能検証の成功へ置き換えない。
