# BeMusicSeeker 応答性・並行処理ハードニング計画

[現在地](./PLAN_STATUS.md) / [risk register](./RESPONSIVENESS_RISK_REGISTER.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [リファクタリング計画](./BeMusicSeekerリファクタリング計画.md)

## 1. 目的

推定先インストールで成立したdeadlockを構造的に除去し、同型のwait cycleが他のfeature／UIへ残っていないことを確認する。

今回の目的は、pre-release buildの異常終了状態を永続的に回復する仕組みを作ることではない。release版ではdeadlockを残さずoperationを正常完了させる。filesystemとsong DBの不一致が外部終了等で生じた場合は、既存のstartup／manual file diffを収束経路とする。

.NET 10 target、managed bundle＋ReadyToRunのselected distribution、updater contractは変更対象ではない。

## 2. Confirmed incident

対象log:

```text
.tmp/推定先にインストールでハング_install-performance.log
```

logでは10件のfile move、LR2 normal-folder sync、resource-health delta、installed lookup更新まで完了し、normal-library refresh dispatch後のoperation endへ到達していない。

```text
background mutation thread
  rwlockBMSFiles writerを含むleaseを保持
  -> normal-library refresh subscriberを同期実行
  -> UI schedulerの同期Invoke完了を待つ

UI thread
  -> normal-library refresh apply
  -> source storage snapshot取得
  -> rwlockBMSFiles readerを待つ
```

workerはUIを待ち、UIはworker保有writer lockを待つ。CPU使用率がほぼ0でUIとlogが停止する症状と一致する。

## 3. 収束contract

専用crash recoveryを追加しない根拠として、現行repositoryには次がある。

1. `Settings.ScanBmsFilesOnStartup`の既定値は`true`。
2. startup initializationは同設定が有効な場合にfile scan／diffを実行する。
3. `ReloadFileDiff`／full reinitializeの明示routeがある。
4. `ApplyFileScanDiff_MovedBmsWithSameMd5PreservesUserSongColumns`は、旧pathがDBに残り同一内容のfileが新pathへ移った状態を新pathへcommitし、user columnsを維持する。

```text
normal release operation
  -> deadlockせず最後まで完了

abnormal external termination after file move
  -> startup scanが有効なら次回起動で差分収束
  -> startup scanが無効ならmanual file diff／full reinitializeで収束
```

これはfilesystemとsong DBの収束contractであり、crash中の全UI stateや一時的なpending表示を完全transaction化する契約ではない。ユーザー設定を強制変更せず、新しいjournal、marker、recovery DB、operation state machineを作らない。

## 4. Non-goals

- 現在のpre-release hangで残った一回限りの中途状態を自動修復するproduction code。
- filesystem、song DB、package DB、maintenanceを跨ぐ擬似分散transaction。
- すべてのoperationへのprogress、cancel、retry、semantic operation state追加。
- raw lock count bindingの一律置換。
- すべての`.GetResult()`、`.Wait()`、dispatcher invokeの機械的削除。
- selected distribution profileの再benchmark／再選定。

## 5. Target model

### 5.1 Non-blocking normal-library refresh

normal-library presentation ownerはlatest versionをthread-safeに記録し、drainが未scheduleならUI queueへ一度だけscheduleしてproducerへ即時returnする。UI laneはlatest versionまでcoalesceしてapplyし、dispose／shutdownでは新規scheduleを止めてpending applyを明示的にdrainまたはdiscardする。

通常mutationはUI drainを待たない。shutdown／testだけが必要に応じて、全model guard解放後に`DrainAsync`をawaitする。

### 5.2 Estimated-install lock scope

- file-operation全体の重複実行は既存のsemantic serializerで防ぐ。
- catalog／pending／install DBのwriter lockを保持したままUI、dialog、event subscriber、別owner callbackを同期実行しない。
- file move中に不要なcatalog writer lockを保持している場合は、既存snapshotとserializerを使って短縮する。
- lock scopeを分けるためだけに新しいcoordinator、journal、persistent receipt、recovery stateを作らない。
- catalog／package applyの既存順序、file名、DB schema、pending／installedのobservable semanticsを維持する。
- LR2 reservation等の重複取得はactual wait graphで不要と確認できた場合だけ整理する。

### 5.3 Application-wide wait review

全featureのsync wait／UI invoke／callback-under-lock候補を、producer lane、held guard、blocking call、target lane、target guard、boundedness、behavior evidenceで分類する。

| Classification | Meaning |
|---|---|
| `BLOCKING` | deadlock、無期限UI block、lock中external callback、thread-affinity violation |
| `ALLOWED_BOUNDARY` | bounded worker-local wait、短いUI-terminal apply、既存の安全なserializer |
| `DEFERRED_OWNER` | active outcome外の明示owner |

candidateの存在だけでdefectにしない。実際のcycle、unbounded block、lock-held callbackがあるrouteだけを修正する。

### 5.4 User-visible behavior

release blockerとして修正するのは、無期限停止、commit後の誤failure、lock中modal dialog、観測不能failure、suppression leak、hangと区別できないsilent rejectionである。

progress／cancelがない短時間operation、問題を起こさないraw lock count binding、bounded worker-local barrier、cosmeticなUI polishは一律blockerにしない。

## 6. Finite implementation batch

このbatchはactive中だけ`PLAN_STATUS.md`へmaterializeし、H1からH4を順に実行する。H4完了後はactive batchをemptyへ遷移する。

### `H1 NORMAL-REFRESH-DEADLOCK`

- dedicated UI laneとheld writer guardを使うdeterministic regression testを追加する。
- `RegularChartListOwner`のproducer-side terminal apply waitを退役する。
- refresh notificationをnon-blocking、coalesced、versioned UI drainへ変更する。
- UI applyがcatalog snapshotを取得する時点でproducerのmutation guardが解放済みであることをtestする。
- dispose／shutdownのpending drain、failure観測、lost update防止を閉じる。

禁止: timeout追加、`TryEnterReadLock` fallback、estimated-installだけrefresh skip、synchronous waitを残したAPI名だけのasync化。

Exit: old wait graphのtestが完了し、estimated-install routeがoperation endとUI suppression releaseへ到達し、dispatcher heartbeatが継続する。

### `H2 ESTIMATED-INSTALL-LOCK-SCOPE`

- broad lease内のfile move、catalog apply、package apply、maintenance、warning、notificationを棚卸しする。
- UI、dialog、event subscriber、owner外callbackを全guard解放後へ移す。
- file move中のcatalog／package writer lockが不要であることを既存snapshot／serializerから証明できる範囲だけscopeを狭める。
- nested LR2 sequence／reservation等の重複guardは、同じsemantic operationであることが確認できる場合だけ一本化する。
- estimated-installのnormal completion、pending removal、installed merge、maintenance、end logをbehavior testで確認する。
- 既存moved-file diff testを維持し、必要ならstartup／manual routeから同behaviorへ到達するintegration testだけを追加する。

明示的な非対象: durable journal／marker、recovery coordinator／database、forced termination後の全package state自動修復、incident専用startup flag、startup scan設定の強制変更。

Exit: guard内synchronous UI／dialog／external callbackが0、normal operationが最後まで完了、既存file diffでstale DB pathが収束、新しい永続回復surfaceが0。

### `H3 APPLICATION-WIDE-WAIT-AUDIT`

対象family:

| Family | Route examples |
|---|---|
| library／package | scan、reinitialize、move、rename、delete、merge、install、maintenance、LR2 |
| playlist／persistence | load、edit、save、import、export、external sync |
| shell／external | startup、dialog、shutdown、playback、HTTP、updater、settings |

- sync wait、dispatcher invoke、callback-under-lock、lock-derived notificationを全件inventoryする。
- same owner／same wait edge／same fixtureを一つのfix unitへまとめ、最大3 familyで閉じる。
- `BLOCKING`だけを修正し、`ALLOWED_BOUNDARY`にはboundednessとtest根拠を残す。
- actual freeze、silent rejection、commit後誤failure、suppression leakを確認する。
- raw lock countやprogress／cancelは、具体的なdefectへ結び付くrouteだけを変更する。

禁止: grep match一件ごとのcommit、application-wide async rewrite、全operationのstate machine化。

Exit: inventoryの全candidateが分類済み、`BLOCKING=0`、representative operationのnormal／failure／rapid reentry／shutdown contentionでUI heartbeatが通る。

### `H4 RESPONSIVENESS-GATE`

- H1 deterministic regression。
- estimated-install end-to-endとstartup／manual file diff convergence evidence。
- library、package、playlist、shellのrepresentative interaction smoke。
- optional external catalog HTTPがcore startup／initialization guard／shutdownを遅延させず、generation一致時だけUIへ反映されるbehavior evidence。
- 全5 projectのlocked restore、Release build、full tests、analyzer／warning gate。
- selected main-app／updater publishからstartup／shutdown、existing-data、update success／rollback。
- frozen snapshotのfresh outcome review、重大指摘修正後の再検証／fresh review。

forced process termination、incident専用recovery、runtime未導入clean machineはこのGateに含めない。

## 7. Exit

```text
strict Refactoring Completion Gate: met
concurrency / responsiveness acceptance: met
engineering migration: complete
active outcome: none
active implementation batch: empty
selected distribution: managed bundle + ReadyToRun
post-engineering manual acceptance: pending user action; non-blocking
```
