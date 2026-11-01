# テスト基盤移行の残作業

## 目的と範囲

日常回帰を短時間で検出し、独立した資源で並列実行できる基盤へ統合する。現行の実行契約は[テスト検証](../spec/development/testing.md)を正本とする。

## 採用する方針

MSTest.Sdkとnative MTPを共通基盤とし、発見・DataRow展開・実行・標準結果をMTPへ任せる。`tools/BeMusicSeeker.Verify` の薄いC#入口、Core、Windows、Scenarios、Desktop.E2E、Compatibility、Performanceの6テストプロジェクト、用途別の共通補助へ、既存ケースと配布検証を順次接続する。

単一Testsに残るPowerShellの分割計画・メタデータ発見・FQN列挙は移行中の接続であり、恒久構成にしない。以下を一体で置換する。

- プロジェクトのCompile対象、solution/filter、runnerのmodule計画を同時に切り替え、欠落と二重実行を防ぐ。
- C#入口へ整形、準備、process所有と期限、MTP起動、標準結果の集約を移し、旧 `verify-refactor`、discovery、shard、結果・process補助を退役させる。配布用publish/releaseは配布責務として残す。
- 旧runner専用の検査とMTP接続用の一時fixtureは、新入口の保証が成立した時点で統合・削除する。ゼロ件、全skip、非成功exit、missing report、timeout、cleanup失敗を成功にしない保証は維持する。
- 旧 `VerificationProcessLifecycleTests` の固定時刻に依存するstream待機・複数root cleanupの検査は、新しいprocess所有の検査へ引き継ぐ。旧VSTestでも失敗する条件があるため、移行時の失敗をMTP固有と決めつけず、実終了・両pipe回収・所有子の残留を独立して観測する。期限・期待値の緩和や恒久的な除外で解消した扱いにしない。
- 共有DB・設定・言語・一時領域・WPF/native資源の所有を先に整理してから並列度を調整する。旧6ホストの名前・割当規則を新基盤の契約として引き継がない。

## 共通入口から後続へ渡す接続

- `tests/` の6moduleはケース移管先です。新しいdev/verifyの全体成功は全選択moduleとDailyシナリオの実体化後に確認します。移行中の必須回帰は単一MTP Testsで継続し、空moduleの除外やdummy成功で置き換えません。
- `tools/BeMusicSeeker.Verify` のFullは全source整形、固定旧ZIPとV/Vnextの準備、run専有catalog、4moduleの一回実行と必須短IDへ接続します。通常公開版の正本props・生成InfoVersionとpublishの `-ApplicationVersion`・variant出力分離を共用します。実配布を用いる受入、SC-15a/SC-15b/SC-16・Desktopの必須保証を成立させるまで実Full成功と扱いません。共通test期限1380秒と速度目標600秒を区別し、各variantのpublish＋検査は独立180秒とします。Compatibility/Performanceは実suite・前提・有限仕事量・期限の確定後に接続します。
- `tests/Fixtures/WindowsProcessProbe` のbuild/copy先はWindows module出力の `fixtures/windows-process-probe/` です。portable保存互換2件とBASS collectible1件のpayload・通常wrapperを移し、同じ変更で旧Compile所属から外します。
- `UiModuleLifetime` はsession lease取得後・解放前に本番経路のWPF host初期化・shutdownを接続します。TestWorkspaceはファイル領域だけを所有し、DB/runtimeの集約managerにしません。
- 固定native資産のhash/x64と配布manifestの保証は配布準備側へ移管してから元ケースを退役させます。通常起動の単一instanceとテスト専用profile排他、更新成功・rollback失敗、SQL復元後終了の異なる終端を、同じ成功判定へまとめません。

### 旧検証契約の移管

| 現在の保証 | 後継と残作業 |
| --- | --- |
| runnerの選択・結果欠落・全Passed | 新しいVerificationPlan / ResultGate。旧shard形状とPE発見を保持せず、旧runnerの撤去時に旧検査も退役します。 |
| 変更C#の選択・整形順・前段失敗 | RepositoryFormatterの実Git契約検査。旧PowerShell probeは旧入口の撤去と同時に退役します。 |
| UTF-8両pipe・所有child・期限・主失敗保全 | 共通OwnedProcessと単一ProcessFixture。固定時刻依存の旧検査を恒久除外せず、必要な外部保証の成立後に置換します。 |
| 実artifact・modal・native資産 | DistributionPreparerの小入力検査と実publish受入を分担します。管理manifest集合・既存native pin/AMD64・app/updater実体照合を実配布で確認し、Desktop・Compatibilityの後継保証が成立するまで元検査を残します。 |

## 完了条件

旧ケースの入力と後継保証を照合し、不要となった旧処理・補助・文書を同じ整理で削除する。標準発見結果や過去の8,520件を恒久rosterにしない。新入口の必要な回帰・失敗回収・実シナリオを確認し、同一条件で実行時間を測定する。実機・環境依存・性能の未実施範囲を通常機能検証の成功へ置き換えない。
