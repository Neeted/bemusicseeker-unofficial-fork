# Responsiveness Risk Register

[応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

調査基準: HEAD `874d6fb16d93e1e6ac4837c84f7852f3884a11ac`、runtime evidence `.tmp/推定先にインストールでハング_install-performance.log`。

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

## App-wide candidates

| ID | Initial state | Family / pattern | Audit question |
|---|---|---|---|
| `RSP-010` | `P1 candidate` | `MainWindowViewModel`／`RegularChartListOwner`のsync UI invoke／sync-over-async | model guardを保持するproducer routeが残るか |
| `RSP-011` | `P1 candidate` | playlist／persistence ownerのUI scheduler／event | DB／table lockとUI callbackが交差するか |
| `RSP-012` | `P1 candidate` | package lifecycle、maintenance、LR2のcallback／dialog | long-held guardまたはnested reservationがあるか |
| `RSP-013` | `P2 candidate` | shutdown、playback、HTTP、rankingのWait／Result | worker-local bounded barrierか、UI lane blockか |
| `RSP-014` | `P2 candidate` | physical lock countの`PropertyChanged`／XAML binding | callback cycle、thread-affinity、誤command状態を作るか |
| `RSP-015` | `P2 candidate` | busy中command reject、suppression、error display | silent no-op、suppression leak、commit後誤failureがあるか |

H3で各candidateにproducer lane、held guards、blocking call、target lane、target guard、boundedness、classification、behavior testを記録する。

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
