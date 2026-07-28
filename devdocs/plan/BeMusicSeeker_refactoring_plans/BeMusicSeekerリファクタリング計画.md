# BeMusicSeeker リファクタリング完了記録

[現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md)

## 判定

WPFアプリをMVVMとして整理し、.NET 10移行に必要なowner、dependency direction、test boundaryを成立させるリファクタリングは完了している。activeなリファクタリングOutcomeはない。

成立済みのboundary:

- root shellはcomposition、window lifecycle、typed presentation routingを担い、feature state／workflowはchild ownerへ置く。
- View／view-hostはfocus、selection、scroll、hit-test、drag visual、virtualization、WPF event、typed requestのterminal applyを担ってよい。
- library、package、LR2、playlist、maintenance、configuration、path、process、native interopは明示owner／adapterを持つ。
- owner間はimmutable request／snapshot／receipt／event factを基本とし、相手ownerのlockやmutable collectionを公開しない。
- facade private state／lock／private operationを列挙するbroad host／port、および非event `async void`を完了Gateから排除した。
- setting、DB、file format、update protocol、UI observable behavior、failure contractをbehavior／golden testで固定した。

## Regression Gate

今後の.NET 10作業で次を再導入しない。

1. feature state、domain decision、durable write、retry／fallback、複数serviceの順序制御をroot View／facadeへ戻す。
2. facade private state、lock、mutable collection、private operationを列挙するbroad host／portを作る。
3. non-event `async void`、unobserved Task、通常経路の意味を変えるfallbackを作る。
4. WPF terminal mappingを理由なくserviceへ隠す、またはplatform dependencyをfeature ownerへ漏らす。
5. setting、DB、file／update contractをmigration evidenceなしに変更する。

行数、file数、type数は調査signalにだけ使い、合否値にしない。cohesiveなView-host、transaction boundary、algorithmを数値のために分断しない。

今後の作業は[BeMusicSeeker .NET 10移行計画](./BeMusicSeeker_NET10移行計画.md)を正本とする。過去のLIB／UI unit履歴はGit historyに委ねる。
