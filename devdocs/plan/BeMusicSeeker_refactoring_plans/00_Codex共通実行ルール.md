# Codex 共通実行ルール

[現在地](./PLAN_STATUS.md) / [応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [risk register](./RESPONSIVENESS_RISK_REGISTER.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md)

## 1. 正本と現在地

1. `PLAN_STATUS.md`を読む。
2. active outcomeに対応する計画と台帳を読む。
3. active implementation batchに未完unitがあればplannerを起動せず、記載順に進める。

`PLAN_STATUS.md`には現在の判定、active batch、現行evidenceだけを置く。過去のunit、commit、review logはGit historyへ委ねる。

## 2. Orchestration

- planner／reviewerは同時に一つだけ起動する。
- サブエージェント実行中、rootはrepositoryの読み取り、検索、編集、build、test、format、analyzer、stage、commitを凍結する。
- rootは同scopeを独立再調査しない。planner後は指定symbol、route、testのbounded feasibility checkだけを行う。
- active batchが空、batch完了後もanchor未達、または具体的evidenceでbatch前提が崩れた場合だけplannerを起動する。
- `NO_SAFE_UNIT`は無効。内部複雑性はowner、wait graph、behavior corridorへ分解する。

## 3. Unit

一つのunitでproduction route、actual wait graph、normal／failure／shutdown behavior、旧sync routeの退役、targeted verification、fresh review、status／risk register更新を閉じる。

method、callback、lock、property、static finding一件だけをunitにしない。同じwait cycle、owner、invariant、fixture、verification scopeを持つ変更をまとめる。status-only progress commitを作らない。

## 4. Concurrency invariants

### 4.1 Callback-under-lock禁止

catalog／playlist／package lock、DB transaction、file-operation serializer、collection lock、initialization／shutdown gateを保持したまま、別execution laneまたはowner外コードを同期実行しない。

対象には`PropertyChanged`、event、dialog、UI scheduler、View callback、別owner callback、plugin／native callbackを含む。通知に必要な値はguard内でversion／immutable change factへ固定し、guard解放後にqueueする。

### 4.2 UI boundary

- workerから`Dispatcher.Invoke`／`IUiScheduler.Invoke`してUI完了を待たない。
- UI反映は`Schedule`／`InvokeAsync`相当へqueueし、同じsourceの更新はversionでcoalesceする。
- producer event handlerはqueue登録だけで戻る。
- apply完了が必要なshutdown／testは、全model guard解放後に明示`DrainAsync`をawaitする。
- UI境界を越える`.GetAwaiter().GetResult()`、`.Wait()`、`.Result`、`Task.WaitAll`は原則禁止する。残す場合はworker-localまたは短いUI-terminalであるwait graph、上限、shutdown behaviorを台帳へ記録する。

### 4.3 Lock scope

- dialog、UI callback、外部process／network、別owner callbackをglobal mutation lock内で実行しない。
- file I/O中のcatalog／package writer lockは、実際の整合性要件を保ったまま短縮できる場合にだけ短縮する。
- phase数や抽象数を増やすこと自体を目的にしない。既存owner／serializerを使い、同じ責務を新しいcoordinatorへ複製しない。
- UI通知のdeadlock除去にmulti-store atomicityや異常終了回復を持ち込まない。

### 4.4 収束方針

今回のpre-release hangで生じた中途状態はrelease contractの通常状態として扱わない。

- release版では対象operationがdeadlockせず正常完了することを第一条件とする。
- incident専用のdurable operation journal、marker、recovery coordinator、専用reconciliation stateを追加しない。
- filesystemとsong DBの差分は、既存の起動時file diffまたはmanual `ReloadFileDiff`／full reinitializeで収束させる。
- `ScanBmsFilesOnStartup`は既定`true`だが、ユーザーが無効にした設定を強制変更しない。
- file diffが旧path削除＋新path追加／同一MD5 relinkを正しくcommitするbehavior testを維持する。
- forced process termination、crash-only recovery、複数store journalはEngineering Gateへ含めない。

in-memoryのversion／change factはUI decoupling用であり、永続回復基盤ではない。

### 4.5 禁止する場当たり修正

- timeout後に成功扱い／lock強制解放
- `Task.Run`を一層追加するだけ
- route固有のrefresh suppressionを増やす
- `TryEnter`失敗時にstale stateを表示する
- lock recursion policyを変える
- exception／Task failureを握りつぶす
- UIを全面disableして循環を隠す

watchdogはstage、logical guard、thread／laneを記録し、testをfailさせる診断用途に限る。

## 5. Behavior compatibility

| Classification | 方針 |
|---|---|
| persisted data／external protocol | migration／golden evidenceなしに変えない |
| documented user workflow | behavior testで維持または意図的に改善 |
| accidental legacy quirk | deadlock、無期限wait、lock中dialog、誤failureなら正規化する |
| cosmetic／workflow preference | active outcomeのblockerにしない |

すべてのoperationへ新しいprogress、cancel、retry、semantic stateを一律導入しない。実測上長時間で、UIが固まる、操作が無言で失われる、結果が誤表示されるrouteだけを修正する。

raw lock countのUI bindingは調査signalであり、それ自体を違反にしない。lock内通知や誤ったcommand availabilityを作る場合に限りownerのsemantic stateへ置換する。

## 6. Verification

### Planning／agent docs only

UTF-8、LF、末尾改行、TOML／Markdown構文、相対link、table、whitespace、`git diff --check`を確認する。production／test／build差分がなければbuild、test、code reviewは不要。

### Concurrency unit

- affected projectのlocked restore／Release build／targeted test
- 二execution laneと明示barrierを使うdeterministic deadlock regression
- dispatcher heartbeatまたはequivalent response probe
- estimated-installのoperation end、UI suppression release、pending／installed state更新
- stale old path＋moved new fileを既存file diffがDBへ収束させるbehavior evidence
- source wait inventoryとruntime interaction evidence
- frozen snapshotのfresh read-only review

forced interruption用のproduction recovery codeやjournal testは要求しない。

### Final responsiveness Gate

- full tests、analyzer／warning gate
- selected Self-contained publishからのstartup／shutdown
- [応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md)のinteraction matrix
- app-wide wait inventoryの全candidateが分類済みで、`BLOCKING`が0
- estimated installのnormal completionとUI heartbeat
- startup／manual file diffの既存収束contract
- fresh outcome review

selected publish layoutを変更しない限り、distribution benchmarkを再実行しない。

## 7. 手動受入れとRelease Freeze

`.NET Desktop Runtime`未導入clean machine／VM、署名、公開、BASS.NET entitlementは[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)へhandoffする。これらをCodexのactive outcome、Engineering Gate、planner停止条件、`EXTERNAL_BLOCKER`にしない。

rootだけがwrite、stage、commitする。`git push`、tag、署名、公開release／publish、version／release notes変更はユーザーの明示指示まで行わない。
