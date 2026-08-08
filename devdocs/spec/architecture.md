# Architecture

この資料は現行実装の大枠を示す。詳細な処理境界は機能別仕様を正本にする。

## Runtime / UI stack

- .NET 10 / C# 14
- WPF + Windows Forms host、x64
- LivetCask MVVM components
- SQLite (`sqlite-net-pcl` / `SQLitePCLRaw`)
- Everything SDK 3 native bridge
- NLog、ManagedBass、SevenZipExtractor、NVorbis等

## レイヤとownership

- `BeMusicSeeker.Views`
  - WPF routed event、focus、selection、scroll、hit-test、drag visual、dialog、window handleなどView固有処理。
  - typed immutable presentation requestをterminal applyする。
- `BeMusicSeeker.ViewModels`
  - shell compositionとfeature owner。
  - `MainWindowViewModel`はroot shellであり、playlist、library list、folder tree、install、maintenance、settings、playback等のchild ownerをcompositionする。
  - feature stateやmulti-service workflowをrootへ戻さない。
- `BeMusicSeeker.Models`
  - library catalog、playlist、install、maintenance、resource health、score、LR2同期等のdomain owner。
- `BeMusicSeeker.Models.BmsLibraryInternal`
  - storage、mutation、scan、package、file operation、projection、native runtime等のbounded owner / gateway。
- `BeMusicSeeker.Models.Utils`
  - scan、path、hash、settings等の共通utility。
- `native/EverythingBridge`
  - Everything SDK 3を使うx64 native bridge。managed側と同一distribution contractで扱う。

## 主要component

- `App`: process startup、settings migration、logging、global exception boundary。
- `MainWindowViewModel`: startup / reload orchestration、child owner composition、typed terminal factのshell接続。
- `StartupProgressWorkflowOwner`: operation token、expected / completed phase、failure、UI block、progress presentation。
- `StartupBackgroundTaskSchedulerOwner`: required / post task classification、dependency、lane、coalescing、shutdown drain。
- `BMSLibrary`: owned catalog、resource index、pending / installed package、score snapshot、mutation ownerのcomposition。
- `BMSPlaylist`: table / entry storage、local edit、URL / external sync ownerのcomposition。
- `BmsLibraryDbGateway`: song DB / score DBのread / transaction gateway。
- `EverythingNative`: native packed resultのdecodeとresource index surface。

## Startup boundary

startupは次を分ける。

1. install estimation ready: catalog、destination resource index、pending package state。
2. UI ready: required initial presentation適用。
3. operable: 通常入力解禁、startup scheduler開始。
4. initialization complete: required local hydration完了。
5. post-initialization complete: startup scheduler 管理下の post task と登録済み best-effort warmup の完了。scheduler 外の ranking / XML refresh と遅延 presentation flush は含めず、それぞれの lifecycle marker で追跡する。

optional library-folder refreshやDispatcher Background workをglobal operabilityへ接続しない。詳細は [startup-initialization-flow.md](startup-initialization-flow.md) を参照する。

## UI / model concurrency boundary

WPF binding collectionはUI read modelとして扱い、所有者のUI scheduler / collection applierを通して更新する。

- domain writer lock保持中にUI、dialog、event subscriber、別owner callbackを同期実行しない。
- workerから`Dispatcher.Invoke`、`.Result`、`.Wait()`でUI完了を待たない。
- background producerはimmutable fact / versionをqueueして戻る。
- UI applyはcoalesceし、stale generationを高コスト処理前に棄却する。
- DB、filesystem、networkはUI thread外で実行し、UI terminal apply中には行わない。
- shutdown時はqueued taskを追跡し、観測不能なfire-and-forgetを作らない。

破壊的chart / package operationは [library-mutation-boundary.md](library-mutation-boundary.md) を正本にする。

## Native bridge policy

Everythingが使える場合、通常起動のfile enumerationは`EBridge_ScanChartAndResources`を使う。chart-relative resource keyとreverse lookup surfaceをnative resultに含める。Everythingが使えない場合はmanaged fallback scanを使うが、旧native ABIやcontract mismatchへの互換fallbackは行わない。

## Deployment boundary

main appは`win-x64` Self-contained、managed bundle + ReadyToRunを正本とする。native self-extract、all-content extraction、single-file compression、trimmingは使わない。BASS / 7zは`libs/x64`、Everything bridgeは`native`、language catalogは`lang`を使う。

## Documentation priority

`spec/`配下を現行仕様の正本とする。計画・履歴は`../plan/`、受入れevidenceは`../acceptance/`を参照する。
