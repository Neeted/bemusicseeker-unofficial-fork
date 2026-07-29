# BeMusicSeeker リファクタリング完了計画

[現在地](./PLAN_STATUS.md) / [応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md)

## 現在の判定

WPFアプリをMVVMとして整理し、.NET 10移行に必要なfeature owner、dependency direction、test boundaryを成立させる**構造的な整理は概ね完了**している。

ただし、`UI-05`で導入されたnormal-library refreshの同期terminal applyとlegacy mutation lockの組合せにより、推定先インストールで決定的なUI deadlockが成立する。strict Refactoring Completion Gateは[応答性・並行処理ハードニング計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md)を閉じるまで再開する。

## 維持するtarget architecture

- root shellはcomposition、window lifecycle、typed presentation routingを担い、feature state／workflowはchild ownerへ置く。
- View／view-hostはfocus、selection、scroll、hit-test、drag visual、virtualization、WPF event、typed requestのterminal applyを担ってよい。
- library、package、LR2、playlist、maintenance、configuration、path、process、native interopは明示owner／adapterを持つ。
- owner間はimmutable request／snapshot／version／event factを使い、相手ownerのlockやmutable collectionを公開しない。
- setting、DB、file format、update protocol等のpersisted／external contractはmigration evidenceで保護する。

## Temporal architecture

- model mutationはmodel lockを保持したままUI completionを待たない。
- mutation guard内ではpresentation callback、dialog、別owner callbackを同期実行しない。
- UI applyはversion／immutable change factを非同期queueで消費する。
- long-held guardは具体的なwait-edgeまたはlatency evidenceがある範囲だけ狭める。
- physical lock occupancyをpresentationへ出す既存routeは、一律削除せず、実際にunsafe notificationまたは誤ったUI stateを作る場合だけ置換する。

## 異常終了後の収束

今回のpre-release deadlockを回復するための専用transaction journalやrecovery frameworkは作らない。

- release contractはdeadlockを除去し、推定先インストールを正常完了させることである。
- filesystemだけが先に変化した異常終了時は、既存の起動時file diff（既定有効）またはmanual `ReloadFileDiff`／full reinitializeでsong DBを収束させる。
- startup scanを無効にしたユーザー設定は上書きしない。
- 既存のmoved-file／same-MD5 relink behaviorをtestで保護し、専用の永続operation stateを追加しない。

## Compatibilityの優先順位

```text
safety / data integrity / responsiveness
  > documented user behavior
  > accidental legacy timing / bug-compatible quirk
```

旧実装のdeadlock、無期限wait、lock中dialog、成功後の誤failureは互換性として保存しない。一方、すべてのcommandへprogress、cancel、retry、semantic stateを追加する全面的UX再設計は、このGateの目的に含めない。

## Completion Gate

1. 推定先インストールのdeadlock cycleが構造的に消え、deterministic regression testが通る。
2. estimated-install routeがfile move、catalog／package apply、operation end、UI suppression releaseまで正常完了する。
3. estimated-installのlock区間から同期UI wait、dialog、owner外callbackが除去され、不要なlong-held writer guardが具体的evidenceに基づいて縮小される。
4. app全体のsync wait／UI invoke／callback-under-lock inventoryが完了し、実際の`BLOCKING`が0。
5. 起動時file diffまたはmanual file diffがmoved fileとstale DB pathを既存contractで収束させるevidenceがある。
6. final Gateでfull tests、analyzer、selected publish smoke、fresh reviewが通る。

forced interruption recovery、durable journal、全commandのUX刷新、行数／grep件数はCompletion Gateに含めない。過去のLIB／UI unit履歴はGit historyへ委ねる。
