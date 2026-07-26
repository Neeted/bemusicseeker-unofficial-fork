# BeMusicSeeker リファクタリング完了計画

[現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md)

## 目的と判定

目的は文書上のOutcomeを消化することではなく、WPFアプリをMVVMとして整理し、.NET 10へ安全に移行できるowner、dependency direction、test boundaryを成立させることである。

現行HEAD `5658b4f3`の静的レビューでは、shell、feature ViewModel、library／playlist owner、configuration／process／native adapterの大枠は成立している。全面的な再リファクタリングは不要であり、MVVM整理は**実用上ほぼ完了**と判断する。

ただし、計画自身の厳格なGateに対して次の2残件があるため、現時点を「完全完了」とはしない。

| ID | 残件 | Gateとの関係 |
|---|---|---|
| `RFR-01` | `ILibraryFileOperationPort`と`BMSLibrary.LibraryFileOperationPort`がfacadeを保持し、file mutation、lock、catalog、package、maintenance、cache、dialog／logの多数のprivate operationを中継する | facade private state／operationを列挙するbroad hostを残さない条件に抵触する |
| `RFR-02` | `InternalBMSAutoPlayerSoundOnly.PlayStart`が非event `async void`で、callerが非同期完了／失敗を観測できない | non-event `async void`と観測不能なfire-and-forgetを残さない条件に抵触する |

この2件をterminal packageで閉じた後、リファクタリングGateを完了し、作業の正本を.NET 10移行計画へ切り替える。

## 成立済みのtarget boundary

- root shellはcomposition、window lifecycle、typed presentation routingを担い、feature stateとworkflowはchild ownerへ委譲する。
- View／view-hostはfocus、selection、scroll、hit-test、drag visual、virtualization、WPF eventとterminal applyを担ってよい。
- library、package、LR2、playlist、maintenance、configuration、path、process、native interopは明示owner／adapterを持つ。
- owner間はimmutable request／snapshot／receipt／event factを基本とし、相手ownerのlockやmutable collectionを公開しない。
- setting、DB、file format、update protocol、failure contractの互換性はbehavior／golden testで守る。

## Terminal outcome: `RFR-01 Refactoring closure`

### `B1 Library file-operation composition boundary`

目的:

- `LibraryFileOperationOwner`を、`BMSLibrary`を保持してprivate operationを再生する45-member portから切り離す。
- 既に存在するcanonical synchronization、filesystem、catalog mutation、package lifecycle、maintenance／resource-health、presentation／logging capabilityを明示compositionする。
- method群をそのまま多数の小interfaceへ分割したり、新しいservice locator／aggregate hostへ移し替えたりしない。

完了条件:

- production file-operation routeが`BMSLibrary`保持adapterを経由しない。
- `ILibraryFileOperationPort`とnested `LibraryFileOperationPort`を削除するか、facade private operationを列挙しないbounded capabilityへ実質的に縮小する。
- folder move／rename、delete、merge、installation-directory repairのsuccess、failure、lock ordering、catalog／package／maintenance更新をbehavior testで維持する。
- broad portの存在だけを固定するarchitecture testを、owner directionと禁止依存を検証するtestへ置換する。

### `B2 Observable playback-start contract`

目的:

- player startの完了／失敗を`PlaybackPanelViewModel`またはplayback ownerが観測できるTask／typed result contractへ変更する。
- 同期player実装は同期完了Taskを返してよい。UI behavior、exit callback、generation guardは維持する。

完了条件:

- productionに非event `async void PlayStart`がない。
- start失敗、cancel／generation mismatch、exit callbackのbehavior testがある。
- callerがstart受付直後の状態遷移と非同期失敗を一貫して扱う。

### `CLOSE Gate verification`

- `scripts/verify-refactor.ps1 -Mode Full`
- repositoryのRelease executableによる変更範囲のUI smoke
- active outcome baseからfrozen HEADまでのfresh static review
- 重大指摘修正後の再検証／fresh review
- `PLAN_STATUS.md`を`refactoring gate: met`、`NET10-01: active`へ更新

## Completion Gate

次をすべて満たしたときだけ完了とする。

1. feature state、domain decision、durable write、retry／fallback、複数serviceの順序制御がroot View／facadeへ戻っていない。
2. facade private state、lock、mutable collection、private operationを列挙するbroad host／portがない。
3. non-event `async void`、unobserved Task、通常経路の意味を変えるfallbackがない。
4. Viewに残る処理はWPF／WinForms／COM／OS windowへのterminal mappingで説明できる。
5. setting、DB、file format、update protocol、UI behavior、failure contractをtests／smokeで維持している。
6. Full verification、Release UI smoke、fresh outcome reviewが通る。

## Structural size guidance

行数、file数、type数は調査開始のsignalにだけ使い、合否値にしない。cohesiveなView-host、transaction boundary、algorithmを数値のために分断しない。逆に数値が小さくても、owner違反、raw writer、broad facade portがあればGateを通さない。

## .NET 10へのhandoff

Gate完了後は[BeMusicSeeker .NET 10移行計画](./BeMusicSeeker_NET10移行計画.md)をactive planとする。過去のLIB／UI／MIG rehearsal履歴はGit historyに委ね、このディレクトリへprogress logを蓄積しない。
