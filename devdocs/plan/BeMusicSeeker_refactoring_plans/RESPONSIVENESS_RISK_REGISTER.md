# Responsiveness Risk Register

[応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

調査基準: production checkpoint `c12f6e6ce3896e6d6235630401dfdbf199ed2bfc`＋H3 frozen worktree、runtime evidence `.tmp/推定先にインストールでハング_install-performance.log`。

この台帳はcurrent-only inventoryである。grep match数ではなく、具体的なwait edge、held guard、target lane、behavior evidenceを記録する。

## Classification

| Level | Meaning |
|---|---|
| `P0` | 再現可能なdeadlock、無期限UI block、data corruption risk |
| `P1` | 同型cycleを作れるcallback-under-lock、長時間guard、thread-affinity violation |
| `P2` | boundednessまたはuser behaviorの確認が必要 |
| `Allowed` | wait graph、上限、shutdown behaviorが証明済み |

## Confirmed root cause

| ID | State | Wait edge | Closure |
|---|---|---|---|
| `RSP-001` | `Closed` | estimated-install workerが`rwlockBMSFiles` writerを保持しUI completionを待つ。UI applyは同lock readerを要求 | producerはlatest versionだけをqueueし、UI laneがcoalesceしてapplyする。held writer／dedicated UI lane regressionとshutdown drainで固定 |
| `RSP-002` | `Closed` | estimated-install broad lease内にfile I/O、catalog／package apply、maintenance、dialog、notificationが混在 | snapshot readerとatomic apply writerをboundedに分離し、estimate／LR2 reservation内はsemantic state applyだけに限定する。package-entry／catalog／maintenance event、failure fact、dialog、log、collection UI flushは全guard解放後にpublish |

## Existing convergence contract

| ID | State | Evidence | Decision |
|---|---|---|---|
| `RSP-003` | `Allowed` | `ScanBmsFilesOnStartup`既定`true`、startup file diff、manual `ReloadFileDiff`、same-MD5 moved-file DB relink test | incident専用journal／recovery coordinatorを追加しない |

startup scanをユーザーが無効にした場合、自動収束は行わず、既存manual file diff／full reinitializeを使う。設定を強制変更しない。

## App-wide audit

| ID | State | Wait graph / evidence | Closure |
|---|---|---|---|
| `RSP-010` | `Closed` | normal-library producer writer→UI completion、UI→catalog reader | H1のversion coalescing non-blocking UI drainで解消 |
| `RSP-011` | `Closed` | playlist hydration receipt、tree selection、store notification、reference apply、BMT progressがowner guard内からexternal callbackを呼び得た | receipt freshnessだけをguard内で判定し、reference indexはguard外でprepareしてrevision一致時だけatomic commitする。BMT progressはsingle FIFO drain、recommended-table network loadはcache monitor外のsingle-flight |
| `RSP-012` | `Closed` | package entry／lifecycle／LR2 progressがmodel guard内でUI subscriberへ到達し得た | state mutationをguard内、notificationをUI schedulerへ分離し、schedule failureを観測 |
| `RSP-013` | `Closed` | updater recovery、external player process／window待機に無期限経路があり、playback stopはsession guard中にplayer closeへ到達し得た | recovery／player waitをvisible timeoutへ統一し、kill後の終了確認、uBMplay main／request sessionのeventual cleanup、settings receipt保持、player closeとupdater decision callbackのprivate guard外実行を固定 |
| `RSP-014` | `Allowed` | file scan、chart-info、LR2 sync、rankingのjoinはworker-local pipeline完了待機。UI threadとの相互guard edgeはない | pipeline failure propagation、cancellation、bounded queue completionを既存testで維持 |
| `RSP-015` | `Allowed` | physical lock count、busy reject、suppression stateにはruntime evidence上のcycle／leakがない | raw countや全commandの機械的async化は行わず、H4 interaction smokeでobservable behaviorを確認 |

## Explicit non-risk for this outcome

- pre-release deadlock中に強制終了した一回限りのpackage／UI中途状態。
- durable operation journal、crash recovery coordinatorが存在しないこと。
- すべてのcommandにprogress／cancel／retryがないこと。
- raw lock count bindingが存在すること自体。
- selected publish layoutのfile数／bundle方式。

## Exit

- `P0 active`／`P1 active`が0。
- 全candidateが`BLOCKING`、`Allowed`、`Deferred`のいずれかへ分類済み。
- `Allowed`はwait graph、bounded completion、shutdown behaviorのevidenceを持つ。
- H4のinteraction smokeとfull Gateが通る。
