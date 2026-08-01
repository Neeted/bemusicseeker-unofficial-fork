# BeMusicSeeker 現行技術仕様概要

この資料は現行仕様への入口である。詳細な正本は`spec/`配下の機能別資料を参照する。

## 技術スタック

- .NET 10 (`net10.0-windows`) / C# 14
- WPF + Windows Forms host / x64
- LivetCask 4.0.2系
- SQLite (`sqlite-net-pcl` / `SQLitePCLRaw`)
- NLog 6系
- Everything SDK 3 + `EverythingBridge_x64.dll`
- SevenZipExtractor / NVorbis
- BASS.NET + x64 native BASS family

projectはSDK-styleで、app、tests、updater、chart-info toolsを.NET 10へ移行済みである。

## 配布形式

main appの正本は`win-x64` Self-contained `bundle-r2r`である。

```text
SelfContained=true
PublishSingleFile=true
PublishReadyToRun=true
PublishTrimmed=false
IncludeNativeLibrariesForSelfExtract=false
IncludeAllContentForSelfExtract=false
EnableCompressionInSingleFile=false
```

managed assembliesはbundleするが、native runtime / all contentのtemporary extractionやsingle-file compressionは使わない。application-owned native assetは`libs/x64`と`native`、language catalogは`lang`に置く。updaterもSelf-contained single-fileを使う。

2026-08-01のPC再起動後比較では、bundle-r2rとfolder-r2rのoperable / required initializationに実用上の差はなく、folder切替条件（5秒以上かつ15%以上短縮）を満たさなかったためbundle-r2rを最終選択とした。

## Logging

通常起動で`log/application.log`と`log/install-performance.log`を出力する。通常loggingは`Ribbit.Logging.NLogWrapper`へ集約する。性能markerはbounded asynchronous writerを使い、UI threadから同期file writeを行わない。

詳細は [logging-policy.md](logging-policy.md) を参照する。

## 起動の主要境界

- `startup_install_estimation_ready`: catalog、destination resource index、pending package stateが揃った。
- `startup_ready_ui`: required initial presentationを適用した。
- `startup_ready_operable`: 通常入力を解禁し、schedulerを開始した。
- `startup_initialization_complete`: required local hydrationが完了した。
- `startup_post_initialization_maintenance_complete`: startup scheduler 管理下の post task と登録済み best-effort warmup が完了した。scheduler 外の ranking / XML refresh と遅延 presentation flush は含めず、それぞれの lifecycle marker で追跡する。

導入先推定に必要なresource indexはoperable前に完成させる。local playlist entriesはrequired initializationに含める。external sync、folder tree final refresh、custom-folder audit、sort prewarmはpost workであり、通常操作をblockしない。

詳細は [startup-initialization-flow.md](startup-initialization-flow.md) を参照する。

## MVVM / owner boundary

`MainWindowViewModel`はshell compositionに限定し、playlist、library list、folder tree、install、maintenance、settings、playback等をchild ownerとして公開する。ViewはWPF固有のterminal apply、focus、selection、hit-test、drag、dialogを担当する。domain state、DB / filesystem mutation、multi-service workflowをViewまたはroot shellへ戻さない。

UI binding collectionはUI laneで適用する。model lock中の同期UI callback、workerからの`Dispatcher.Invoke`、UI境界のsync-over-asyncは禁止する。

## File enumeration / resource index

通常起動ではEverything native bridgeでchart / audio / image / movieを列挙する。native resultはchart-relative resource keyとdestination reverse lookupを含む。Everything unavailable時はmanaged fallbackで同じsemanticsのindexを作る。

## Data model

- LR2 linkedまたはstandaloneのsong DB: chart catalog。
- app-owned tables: playlist、install、maintenance、IR、digest、bmson、chart-info等。
- owned runtime catalog: BMS / BMSON chart identityとversioned derived index。
- resource index: `LibraryResourceIndex` / `DirectoryResourceLookupCache`。

詳細は [data-and-indexes.md](data-and-indexes.md) を参照する。

## Install estimation

`BmsLibraryInstallEstimationService`がpending package、owned catalog、destination resource index、chart-info metadataから候補を評価する。resource indexが無ければ`resource_index_unavailable`とし、起動時readinessを偽装しない。

詳細は [install-estimation-current-logic.md](install-estimation-current-logic.md) を参照する。

## Documentation priority

現行仕様はこの`spec/`を優先する。計画・履歴は`../plan/`、受入れ記録は`../acceptance/`を参照する。
