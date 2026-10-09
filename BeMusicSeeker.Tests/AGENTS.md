# テスト作業の指針

[ルートの指針](../AGENTS.md)に加え、設計は[テスト作成](../devdocs/spec/development/test-authoring.md)、実行条件は[検証](../devdocs/spec/development/testing.md)、担当の入力・承認は[エージェント運用](../devdocs/spec/development/agent-workflow.md#テスト設計の要否)に従います。以下は、このディレクトリで作業する際の入口です。

## 編集前

変更目的と既存の保証から、恒久テストの扱いを確認します。追加・意味変更・置換には承認済みの独立した判定基準と、検証境界・保証の分担を使います。機械的変更と代替不要の削除には設計書を要求しません。

既存ケースにも[採否基準](../devdocs/spec/development/test-authoring.md#既存テストの採否)を適用し、必要性の判断、保証の集約、残すケースのコメント整備の順に進めます。過去の調査や実行区分を免除理由にしません。

根拠・期待結果・到達可能性・実行条件に不足があれば `NEEDS_ROOT_INPUT`、契約や保証の変更判断が必要なら `NEEDS_ROOT_DECISION` を返します。実装や既存期待値から未確定の正解を補いません。

## 配置と共通補助処理

機能別の `Catalog`、`ChartInfo`、`Install`、`Playlist`、`PlayHistory`、`MainWindow` 等にテストと、その機能だけの準備・代替実装を置きます。機能横断の補助は `Helpers`、検証・配布スクリプトの契約は `Verification`、固定入力は `TestData`、性能測定は `Performance`、プロジェクト・アセンブリ設定は直下を使います。

同じ責務の既存テストと補助処理を先に調べます。通常のライブラリ準備は `TestBmsFactory`、WPFは `TestUiDispatcherHost` と `TestWindowPresentationScope` を入口とします。待機・解放の詳細は[画面テストの分離](../devdocs/spec/development/testing.md#画面テストの分離)に従います。物理フォルダや局所・統合・E2Eの名称だけで実行区分・並列度を決めません。

画面を使う必要性は[画面に関わる保証の分担](../devdocs/spec/development/test-authoring.md#画面に関わる保証の分担)で判断し、Windowを使わない処理に画面用scopeを持ち込まないようにします。

設定fixtureは `MainWindowViewModelTestFactory.CreateIsolatedSettings` 等の専用providerか固定snapshotを使います。実MainWindowの共通寿命は `MainWindowTestLifetime`、通常Taskと表示・閉鎖の待機はそれぞれ `AwaitTaskOnDispatcher` と `AwaitPresentationOnDispatcher` に揃えます。同一テストの親子Windowには同じ表示scopeを渡します。

## 編集と確認

[検証境界と保証の分担](../devdocs/spec/development/test-authoring.md#検証境界と保証の分担)に沿って、条件の組合せと実接続・代表フローの保証を分けます。対象の実装を動かし、承認された期待結果を維持します。分解・統合・削除では、元の保証の移管先か、不要になった理由を確認してから重複を取り除きます。

テスト名は機能・条件・期待結果で表し、対応する機能仕様も更新します。[レビュー観点](../devdocs/spec/development/test-authoring.md#引継ぎとレビュー)と[資源・待機の規則](../devdocs/spec/development/testing.md#資源の分離と共有)で自己点検します。契約外の内部詳細の固定や、失敗を隠す判定の弱体化は行いません。

残すケースには[保証コメント](../devdocs/spec/development/test-authoring.md#各ケースの保証コメント)を付け、データ駆動・ループ内の条件差と実際の表明・待機順序まで照合します。整理単位ごとに旧テストと不要になった補助処理・固定入力も退役させます。

反復中は関連する条件に絞った `Quick`、最終統合はルートが `Functional`・`Full` を実行します。完了報告は[実装担当の完了報告](../devdocs/spec/development/agent-workflow.md#実装担当の完了報告)に従い、保証の分担・置換関係と、実行した範囲・結果・未確認点を返します。
