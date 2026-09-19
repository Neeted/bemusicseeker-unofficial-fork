# 検証基盤の改善候補

## 目的と現状

[検証仕様](../spec/development/testing.md)の共有期限、並列実行、失敗判定を維持しつつ、テストの調査・待機・資源分離の保守負担を下げます。通常検証の目標に不要な一括整理はしません。

## 残る検討

`DoNotParallelize` を実在する共有資源で再確認し、不要なクラス全体の直列化を減らします。固定待機を通常完了、失敗検出、性能測定に分け、通常完了だけを実際の状態通知へ置き換えます。外部プロセスの実行が必要な確認は、軽量な契約テストと分離します。`UpdaterPackageSyncTests.RecoverCommandDefersWhenTheApplicationExecutableIsStillRunning` の250ms待機は、模擬プロセスからの準備完了通知へ置き換える候補です。これは `ProcessIntegration` の外部プロセス試験であり、通常の `Functional` には含めません。

性能比較は明示実行に分け、同じ条件の測定値を比較できるようにします。個々の実行時間と不安定さは実行結果から確認し、手書きの恒久台帳を作りません。テストの検出結果から完全修飾名・カテゴリ・元ファイル・共有資源・実行区分を辿れる生成資料が必要か検討します。必要性の確認前に新しい保存状態・専用プロジェクトは増やしません。

## 次回調査の優先度を決めるための確認範囲

### 適用する版と日時

- 整理・確認日：2026年9月20日（日本時間）。この範囲の記録時点は同日06:05 JST。
- 整理前の基準コミット：`3b99191ba0f736e2e167eb64e1cc869acf37ea53`（2026-09-20 02:18:49 +09:00）。
- 以下は、そのコミットに今回のテスト・検証スクリプト・仕様の**未コミット差分を加えた作業ツリー**についての判断。基準コミット自体が整理済みという意味ではない。整理後のコミットIDは記録時点で未確定であり、後日この文書を含む変更のGit履歴から特定する。
- 最終確認は標準 `Functional`、実行開始識別子 `20260920-054104`。テストプロセス起動から実終了まで199.5秒、4,848件成功・既存スキップ11件。200秒未満の余裕は0.5秒で、180秒の運用目標は未達。安定して200秒未満になる保証ではない。`Full` は今回未実行。

今回の調査は200秒未満を目指して重い領域と実際の競合を優先したもので、全領域の全テストを横断的に再審査したものではない。以下の「確認済み」は記載した観点・ケースに限る。次回は整理を含むコミット以降の追加・変更を先に確認し、同じファイルにある新しいテストを確認済みと扱わない。この節は未調査範囲の選定に使い、調査が進んだら更新する。個別テストの時間ランキングや実行履歴は蓄積しない。

### 仕様・必要性まで比較的深く確認した範囲

テスト名は `BeMusicSeeker.Tests/` 配下。仕様上の保証、本番からの到達、重複、入力の組合せ、観測方法を検討した。該当ケースの再調査より、後から追加されたケースや次表の未確認範囲を先に見る目安になる。

| 領域・主なテスト | 今回確認した内容 | 次回にも残る注意点 |
| --- | --- | --- |
| 譜面情報の補完・再試行：`ChartInfoBackfillStorageTests`、`ChartInfoInstallFailureRetryTests`、`ChartInfoMetadataTestSupport` | 投影正規化の単体試験との重複、本番では形式別に分けられる入力、所持集合へ入らないMD5欠落主体、同一MD5の複数所有者、保存失敗と再試行。補助処理によるwriterの再実装を本番の保存経路へ統合した。 | 譜面解析そのものや互換性ケース全体は未監査。失敗・再試行の別条件を件数だけで削らない。 |
| スキーマ・入出力：`AppSchemaPreflightServiceTests`、`ChartInfoMetadataSchemaExportImportTests` | 新規作成・修復、失敗時のロールバックと再試行、取込み・移転・キャッシュの連続操作を統合。定数・SQL文字列の形だけの確認を整理した。DBロック中の検査は因果関係で確認する形へ変更した。 | 他のDBスキーマや移行・保存機能まで確認したわけではない。 |
| 所持集合・ハッシュ反映：`OwnedChartCollectionInlineDigestTests`、`OwnedChartCollectionLibraryMutationTests` | 到達不能な所有者、直接イベント操作との重複、親子削除結果、通知時の解決、旧スナップショット、連続2操作の局所更新を確認した。 | この2クラス以外の所持集合・プレイリスト索引試験全体は未監査。 |
| フォルダ変更・統合：`BmsLibraryFolderRenameRefreshTests`、`BmsLibraryDuplicateServiceTests` | 手動・自動の入口、局所範囲、事前確認と実行時再確認の組合せ、連続操作を確認。完全一致キーや起動時走査なしの相互作用がある条件は残した。 | 両機能の関連クラス全部を監査したわけではない。残る各ケースの準備量は再検討可能。 |
| 構築済み索引の導入・統合：`BmsLibraryPackageInstallServiceTests` のリソースのみの導入と自動・推定先・強制導入の `Warm` ケース、上記統合の `TwoWarmOperations` | 実処理の全件列挙観測と更新数上限により背景16件でも再構築を検出できるため、比較をしていなかった128件側を整理。連続2操作、永続結果、旧スナップショット等は維持した。 | 導入テスト全体の必要性を審査したという意味ではない。サイズ間の仕事量を実際に比較する試験は別扱い。 |

### 待機・分離・準備を中心に確認した範囲

ここは実行上の問題を確認した範囲であり、各クラスの機能ケースの必要性・重複を全件確認した範囲ではない。

| 範囲 | 確認した観点 | 未確認または再検討可能な観点 |
| --- | --- | --- |
| `MSTestSettings`、`verify-refactor.ps1`、`verification-test-discovery.ps1` | メソッド単位実行、実属性からの共有状態検出、実行集合の重複・漏れ、スレッド補充待ち。通常2ホストは全DB共通のプロセス内ロックを分離するために維持。 | `DoNotParallelize` 各指定の必要性を全件監査したわけではない。共有状態側の長時間ケースと、クラス全体を直列化する必要性は候補。 |
| `AppHttpClientTests`、`SingleRequestHttpServer`、`PlaylistWorkspaceDetailRefreshTests`、`PlayHistoryReadModelTests`、`DropInstallQueueProcessorTests`、`ShellShutdownWorkflowOwnerTests`、`StartupPostInitializationWarmupOwnerTests`、`PackageInstallWorkflowOwnerTests` | 通常の通知・完了に設けた局所タイマーを整理し、実際のTask・イベントと、失敗時のゲート解放・開始済み処理の終結を確認。HTTP本来の期限や後始末の監視は区別した。 | 機能ケース同士の重複・組合せ全体は未監査。これら以外に残る局所待機も未整理。 |
| `PlaylistWorkspaceExternalSourceTests`、`PlaylistWorkspacePersistenceCommandTests`、`MainWindowViewModelStartupProgressTests`、`FileDiffReloadWorkflowOwnerTests`、`ScoreOnlyReloadWorkflowOwnerTests` | 実際に問題となった取消通知、完了待ち、Dispatcher上の観測、非同期処理開始前の回数表明を修正。 | 対象外の待機・永続化・再読込みケース全体は未監査。 |
| `ApplicationCompositionTests`、`ApplicationSettingsLifecycleTests`、`BmsLibraryInstallEstimationServiceTests` | 終了処理のプロセス共有資源、設定ファイルへの不要なアクセス・正規化を分離。 | 設定・推定・構成の全機能ケースの必要性は未監査。 |
| `BmsLibraryInitializationFileScanTests`、`BmsLibraryPackageInstallServiceTests`、`BmsLibraryPendingPackageRegroupTests`、`CatalogMutationOwnerTests`、上表のフォルダ変更・統合・所持集合変更 | 明白なfixture準備の逐次commitを一括化し、SELECTだけの検査を読取り専用接続へ変更。`CatalogMutationOwnerTests` の16/128件によるSQL仕事量比較は必要性を確認して保持。 | 全DB準備箇所を置換したわけではない。ファイル生成・解析・保存の準備量、各機能ケースの重複には未確認部分がある。 |

### 次回の着手順の目安

現在の遅さは再測定して判断する。並列テストの各所要時間には共通DBロック等の待ちが含まれ、クラス別の合計は全体経過時間にも、そのクラス単独の処理費用にもならない。過去の結果は保存範囲が不完全なので、今回の機能変更が長時間化の原因だったという推定にも使わない。

| 優先度 | 候補 | 最初に確かめること |
| --- | --- | --- |
| 高 | 整理後に追加・変更されたテストと、新しい実測で長いケース | 本番入口から成立する入力か、既存試験と同じ保証か。待機時間を含む場合は同じ時間帯にロックを保持していた準備・処理も見る。 |
| 高 | `BmsLibraryPackageInstallServiceTests`、`BmsLibraryPendingPackageRegroupTests`、`BmsLibraryInitializationFileScanTests` の未監査ケース | 今回は主に準備処理と一部ケースを改善した。大量の実ファイル・DB準備が必要か、同じ保証を入口ごとに過剰に繰り返していないかを仕様から検討する。 |
| 高 | `Lr2SongDbSyncServiceTests`、`Lr2FolderFileDbSyncServiceTests`、`Lr2SongDbWriterTests`、`BmsLibraryStateApplierTests`、`FolderAutoRenameWorkflowOwnerTests`、`MaintenanceRescanWorkflowOwnerTests` | 今回の途中計測で所要時間の大きい領域として見えたが、必要性・到達性・重複の詳しい監査は未実施。現在も重いなら優先する。 |
| 中 | 共有状態ホスト、プレイリストの永続化・要約・索引、各workflowの条件組合せ | 共有状態を扱う実経路、直列化の範囲、類似試験間の保証分担を確認。今回待機を直したことだけでケース整理済みとは扱わない。 |
| 実測次第 | 譜面パーサーの挙動・互換性、設定の保存・移行、ローカライズ、画面表示、その他上表にない領域 | 今回は横断的な必要性監査をしていない。未確認であること自体を不要の根拠にせず、実測と仕様上の保証から調査順を決める。 |
| Functional短縮とは別 | 更新・配布・外部プロセス、`Full` 専用区分、明示実行の性能・大規模fixture試験 | 通常Functionalには含まれない。上記Updaterの準備待ち等は、その区分を検証するときに整理する。 |

次回も、実行対象の除外・スキップ追加・期限延長で短縮せず、必要な保証と識別力を先に定める。今回深く確認した領域でも、追加差分、新しい遅延、仕様変更があれば優先度を上げる。

## 完了条件

採用した改善が、本来の保証、実行集合、共有期限、前面操作の許可範囲を弱めないこと。実行結果の収集は作業用の生成物に留め、完了した整理の履歴はGitへ委ねます。
