# Responsiveness Risk Register

[応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md)

完了基準: production checkpoint `5d51990e`＋H4 frozen worktree、runtime evidence `.tmp/推定先にインストールでハング_install-performance.log`、Full verification、repository publish UI smoke、fresh outcome review。

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
| `RSP-002` | `Closed` | estimated-install broad lease内にfile I/O、catalog／package apply、maintenance、dialog、notificationが混在 | aggregate transitionは`BMSLibrary`、pending／installed collection mutationは`PackageLifecycleOwner`が所有する。snapshot readerとatomic apply writerをboundedに分離し、file move、package-entry／catalog／maintenance event、dialog、log、collection UI flushは全guard解放後に実行する。facade private stateを列挙するcapability adapterは残さない |

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
| `RSP-012` | `Closed` | package entry／lifecycle／LR2 progressがmodel guard内でUI subscriberへ到達し得た | state mutationをguard内、notificationをUI schedulerへ分離する。LR2 property changeはimmutableなproperty-name集合をsingle pending operationへcoalesceし、1 UI turnで1 snapshotだけpublishして継続分を再scheduleする |
| `RSP-013` | `Closed` | updater recovery、external player process／window待機に無期限経路があり、playback stopはsession guard中にplayer closeへ到達し得た | recovery／player waitをvisible timeoutへ統一し、kill後の終了確認、uBMplay main／request sessionのeventual cleanup、settings receipt保持、player closeとupdater decision callbackのprivate guard外実行を固定 |
| `RSP-014` | `Allowed` | file scan、chart-info、LR2 syncのjoinはworker-local pipeline完了待機。manual／startup／deferred ranking routeはnetwork、cache列挙、XML parse、offline lookup準備をmodel guard外で実行し、source generation再検証後のparse済みcache promotion／score applyだけをruntime固有guard内で行う。score通知とdialogは全guard外のUI lane | pipeline failure propagation、cancellation、LR2 bounded UI turns、ranking staged-download／freshness／post-guard publication／parse-failure cache preservation／confirmation／failure behaviorをtestで維持 |
| `RSP-015` | `Allowed` | physical lock count、busy reject、suppression stateにはruntime evidence上のcycle／leakがない | raw countや全commandの機械的async化は行わず、H4 interaction smokeでobservable behaviorを確認 |
| `RSP-016` | `Allowed` | `PlaylistWorkspaceViewModel.ApplyPlaylistPropertyPresentation`はpost-save workerからinjected UI apply Taskの完了を同期waitする。`TrySaveCore`のplaylist writer／table writerは先に解放され、presentation callbackはDB transactionやstore guardを再取得しない | `PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync`のpost-commit presentation boundaryに限定する。normal／failure／concurrent-writer behaviorは`PlaylistWorkspaceViewModelTests`のproperty-save fixtureで維持し、dispatcher shutdownはTask failureとして伝播する |
| `RSP-017` | `Allowed` | `BMSPlaylist.InvokeBMSTablesCollectionMutation`はworkerからUI terminal applyを同期waitする。playlist collectionのreader／writer guardはUIへ到達したmutation内で取得するため、producer-held guard→UI→same guardのcycleはない | header load／reload、create、removeのvisible collection applyに限定する。durable commitとnotification failureの契約は`BmsPlaylistUpdateTests`のcreate／remove rollback fixtureで維持する |
| `RSP-018` | `Allowed` | `PackageStateMutationApplier.InvokeOnUi`は、collection mutation scope中はactionをdeferし、scope外だけUI terminal applyを同期waitする。estimated installは`queuePublication: true`で全writer guard解放後にflushする | direct applyはproducerがpackage collection guardを保持していないrouteだけに限定する。deferred normal／failure／shutdown publicationは`BmsLibraryStateApplierTests`とestimated-install behavior fixtureで維持する |
| `RSP-019` | `Closed` | optional external table-list HTTPを`rwlockBMSFilesInitializedAll` writer内のstartup continuationで同期waitし、core operable到達とshutdownを最大300秒遅延させていた | external catalog fetchを`startup_ready_operable`後のcancel可能background requestへ分離する。workspaceがrequest generation／CTSを所有し、catalogとloading stateはcurrent requestだけUI laneへpublishする。slow fetch、stale completion、shutdown cancellationをdeterministic behavior testで維持 |

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
- fresh outcome reviewに重大指摘がない。
