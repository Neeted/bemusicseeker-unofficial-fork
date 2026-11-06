# テストmoduleの作業指針

[ルート](../AGENTS.md)、[テスト設計](../devdocs/spec/development/test-authoring.md)、[検証](../devdocs/spec/development/testing.md)に従います。

6moduleはCore、Windows、Scenarios、Desktop.E2E、Compatibility、Performanceです。共通補助はTestSupportへ集約し、機能ごとのprojectや独自process hostを増やしません。Coreへ実Windowやprocess全体の設定変更を移しません。ケース開始前にmodule初期化で一度だけ固定し、ケースが変更・復元しないスレッドプールのworker下限は、この設定変更に含めません。

各実行ケースはTestWorkspaceで可変ファイル領域を所有します。test/appの起動はOwnedProcessを使い、例外・取消を含めて全childと両pipeを回収します。Windowsはmodule全体のsession leaseを取得せず、実Activateを使うPlaylistLampViewerWindowManagerTestsが非UI側でケース前に取得し、Windowと開始済み処理の回収後に解放します。Scenarios・Desktop・Compatibility・Performanceは従来どおりmodule全体のsession leaseを維持します。

ケースの移動・置換ではCompile対象とprofileを一体で更新し、入力・期待結果・保証コメントを照合します。空moduleを成功dummyで埋めたり自動除外したりしません。標準入口の選択範囲と時間・結果の意味は[入口の説明](../tools/BeMusicSeeker.Verify/README.md)を参照します。

機能別ディレクトリへcaseと専用fixtureを置き、共通補助はTestSupportの既存境界を使います。追加・意味変更・置換は承認済みの独立判定基準で行い、元保証の後継を確認して不要な旧処理を除きます。ケースには条件・期待結果・検出する誤りが分かる日本語XMLコメントを付けます。

反復は対象moduleの直接MTP、通常コミット・dev統合はverify、公開候補はfullを使います。選択ゼロ・skip・回収失敗を成功にしません。Desktopの実入力はFullに含め、既存session leaseの下で単一workerが所有入力と資源の回収まで担当します。
