# テスト基盤移行の残作業

## 目的と範囲

日常回帰を検出し、独立した資源で並列実行できる基盤へ統合する。直近は段階1として全既存保証の後継・隔離・結果回収・終了と旧基盤退役を完了させる。段階2の既知不具合修正、段階3の広範な追加Scenario/Desktop、段階4の数値速度目標とは区切る。段階1完了は元刷新計画の全目標達成やリリース可能の認定ではない。現行の実行契約は[テスト検証](../spec/development/testing.md)を正本とし、段階別の対象切替は実装・テスト・資料を同時に揃える。

## 採用する方針

MSTest.Sdkとnative MTPを共通基盤とし、発見・DataRow展開・実行・標準結果をMTPへ任せる。`tools/BeMusicSeeker.Verify` の薄いC#入口、Core、Windows、Scenarios、Desktop.E2E、Compatibility、Performanceの6テストプロジェクト、用途別の共通補助へ、既存ケースと配布検証を順次接続する。

単一Testsに残るPowerShellの分割計画・メタデータ発見・FQN列挙は移行中の接続であり、恒久構成にしない。以下を一体で置換する。

- プロジェクトのCompile対象、solution/filter、runnerのmodule計画を同時に切り替え、欠落と二重実行を防ぐ。
- C#入口へ整形、準備、process所有と期限、MTP起動、標準結果の集約を移し、旧 `verify-refactor`、discovery、shard、結果・process補助を退役させる。配布用publish/releaseは配布責務として残す。
- 旧runner専用の検査とMTP接続用の一時fixtureは、新入口の保証が成立した時点で統合・削除する。ゼロ件、全skip、非成功exit、missing report、timeout、cleanup失敗を成功にしない保証は維持する。
- 旧 `VerificationProcessLifecycleTests` の固定時刻に依存するstream待機・複数root cleanupの検査は、新しいprocess所有の検査へ引き継ぐ。旧VSTestでも失敗する条件があるため、移行時の失敗をMTP固有と決めつけず、実終了・両pipe回収・所有子の残留を独立して観測する。期限・期待値の緩和や恒久的な除外で解消した扱いにしない。
- 共有DB・設定・言語・一時領域・WPF/native資源の所有を先に整理してから並列度を調整する。旧6ホストの名前・割当規則を新基盤の契約として引き継がない。

## 共通入口から後続へ渡す接続

- `tests/` の6moduleは用途別の移管先です。段階1のdev/verifyはCore・Windows・Daily Scenarios（SC-01/02/09）の全体受入を残します。旧Testsの必要保証は後継成立まで保持し、旧Functionalを空にして成功とせず、dummy成功にも置き換えません。追加専用Desktopの保留は明示的な範囲変更であり、選択moduleの0件拒否を緩める理由にはしません。
- 段階1のFullは全source整形、既存Current/Next/Legacy配布準備とrun専有catalog、Core・Windows・Scenariosの3moduleの一回実行へ接続します。完成済みSCと既存Full保証に加え、既存配布/更新の最小後継 `SC-ExistingData` / `SC-CurrentUpdate` を必須にします。SC-15a/SC-15b/SC-16の広範な拡充と追加Desktopは後段へ保留し、Desktop moduleを段階1の必須集合へ残しません。旧保証の唯一後継は層にかかわらず段階1に残します。通常公開版props・InfoVersion・publishの `-ApplicationVersion`・variant出力分離を共用し、Current/Next/Legacyを便宜的に縮小しません。共通test期限1380秒は維持し、速度目標600秒の達成判定は段階4へ分けます。各variantのpublish＋検査は独立180秒です。既存Compatibility/Performanceの移管と計測経路は段階1で継続します。
- `tests/Fixtures/WindowsProcessProbe` のbuild/copy先はWindows module出力の `fixtures/windows-process-probe/` です。移管済みのportable保存互換2件とBASS collectible1件のpayload・通常wrapperを維持し、旧hostを復活させません。
- `UiModuleLifetime` はsession lease取得後・解放前に本番経路のWPF host初期化・shutdownを接続します。TestWorkspaceはファイル領域だけを所有し、DB/runtimeの集約managerにしません。
- 固定native資産は保証する入力と時点を分けます。tracked BASS 6資産とVorbisの固定hash/version/API/AMD64はCoreで維持し、配布ZIP検査との機械的重複として削除しません。Everythingのtracked 2資産の既存pin/AMD64は配布検証と信頼する固定旧版identityへ移管済みの保証を維持します。一般依存の新hash台帳は作りません。通常起動の全体単一instanceと通常/test共通profile排他、更新成功・rollback失敗、SQL復元後終了の異なる終端を、同じ成功判定へまとめません。

### 旧検証契約の移管

| 現在の保証 | 後継と残作業 |
| --- | --- |
| runnerの選択・結果欠落・全Passed | 新しいVerificationPlan / ResultGate。旧shard形状とPE発見を保持せず、旧runnerの撤去時に旧検査も退役します。 |
| 変更C#の選択・整形順・前段失敗 | RepositoryFormatterの実Git契約検査。旧PowerShell probeは旧入口の撤去と同時に退役します。 |
| UTF-8両pipe・所有child・期限・主失敗保全 | 共通OwnedProcessと単一ProcessFixture。固定時刻依存の旧検査を恒久除外せず、必要な外部保証の成立後に置換します。 |
| 実artifact・native資産 | DistributionPreparerの小入力検査と実publish受入で管理manifest全file集合・配布native pin/AMD64・app/updater実体を確認します。tracked BASS/Vorbis入力の固定保証はCoreに残します。既存保証の後継成立を元検査退役の条件とし、追加Desktop全体の完成は待ちません。 |
| 既存native path identity | EverythingのSDK getter→pack/decode・分類・逆引き・健康度・通常推定の2条件はCompatibilityへ移管します。追加SC-07の実service/corpusで代替しません。 |
| 旧実配布受入 | 下記最小2受入をScenariosの非Dailyへ接続します。旧保証の実起動・modal・終了を保持し、SC-15/16の追加拡充とは区別します。 |

### 段階1に残す実配布受入

- `SC-ExistingData` はStandalone/LR2の旧LocalAppData `user.config` 移行と固定旧DBを使い、実配布appを各2回正常起動・終了します。2回目のconfig安定と元input保持を確認し、事前portable config設置で旧設定移行を避けません。
- `SC-CurrentUpdate` は現updaterのpublic handshakeから通常appの実自動再起動、ready、正常終了までを通し、DB/設定/非管理file保持と旧管理file退役を確認します。既存Current→Currentの保証を保ち、V→Vnext更新UIや旧layout全網羅を必須へ混ぜません。

未解決の専有LocalAppData到達や所有process境界は既存保証を満たす最小設計で確定し、利用者領域への書込みや新しい通常起動flagで迂回しません。開始成功だけで実再起動・ready・終了の成功を作らず、未実装の最小受入を成功にしません。

## 段階1の完了条件

旧ケースの入力・期待・失敗保全・終端と後継保証を照合し、旧保証が保留作業だけに残らないことを確認する。後継成立後に不要な旧処理・補助・文書を退役し、標準発見結果や過去の8,520件を恒久rosterにしない。

新dev/verifyと段階1 Fullの必要な回帰・失敗回収・最小実受入、既存互換/性能検証の移行、独立レビュー・統合を確認する。選択suiteのmissing/zero/skip/timeout/cleanup失敗を成功にしない。シンボリックリンク6条件の現環境未成功は合意どおり別記し、移行対応と実行成功を区別する。

測定可能性と既存の性能回帰保証は維持し、日常中央値60秒/p95 90秒・Full10分の数値目標達成は後段で判定する。既知の誤期待は現行動作の記録として既存修正計画/移行表へ対応付け、正本へ格上げせず製品修正時に仕様・期待と同時に置換する。段階1の完了、不具合修正残作業、追加検証保留、性能実測/未達を分けて報告する。
