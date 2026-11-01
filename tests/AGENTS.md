# 新テストmoduleの作業指針

[ルート](../AGENTS.md)、[テスト設計](../devdocs/spec/development/test-authoring.md)、[検証](../devdocs/spec/development/testing.md)に従います。

6moduleはCore、Windows、Scenarios、Desktop.E2E、Compatibility、Performanceです。共通補助はTestSupportへ集約し、機能ごとのprojectや独自process hostを増やしません。Coreへ実Windowやprocess全体の設定変更を移しません。

各実行ケースはTestWorkspaceで可変ファイル領域を所有します。test/appの起動はOwnedProcessを使い、例外・取消を含めて全childと両pipeを回収します。WPF/nativeを所有するmoduleはUiModuleLifetimeのsession排他を守り、初期化を取得後、shutdownを解放前へ接続します。

旧単一Testsから移すときはCompile対象とprofileを一体で更新し、残す入力・期待結果・保証コメントを照合します。空moduleを成功dummyで埋めたり自動除外したりしません。新入口の移行状態と時間・結果の意味は[入口の説明](../tools/BeMusicSeeker.Verify/README.md)を参照します。
