# Post-migration Manual Acceptance

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [current evidence](../../acceptance/net10-performance-engineering.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md)

## Scope

このchecklistはCodexのEngineering Gate完了後にユーザーが実施する。active implementation batch、planner停止条件、`EXTERNAL_BLOCKER`、Engineering Gateには含めない。Codexは結果を待たない。

## `MANUAL-01 Runtime-free clean-machine acceptance`

実施条件:

- x64 Windows machine／VM。
- .NET Desktop Runtime 10がmachine-wideに導入されていないことを確認。
- final Engineering commitから生成したpackageを使用。
- package SHA-256、selected profile、distribution report ID／hashを記録。

最小確認:

1. packageを新しいdirectoryへ展開し、追加runtimeをinstallせず`BeMusicSeeker.exe`を起動する。
2. startup、library initialize／scan、playlist表示、search／filter、playback、settings save／restartを確認する。
3. unexpected runtime installation prompt、silent settings reset、DB loss、partial updateがないことを確認する。

記録template:

```text
MANUAL-01
Date:
Windows edition / build:
Architecture: x64
Machine-wide .NET Desktop Runtime 10: absent
Artifact name / SHA-256:
Main-app profile:
Startup: pass / fail
Core workflow smoke: pass / fail
Settings restart: pass / fail
Notes:
```

## `MANUAL-02 Real-data performance acceptance`

この確認は、P1〜P5、full tests、publish、fresh reviewがすべて完了した後に、final .NET 10 artifactと本番相当のユーザーデータで一度行う。net472 buildは使用しない。多数回の統計benchmarkを要求しない。

### Preparation

- final artifact SHA-256とcommitを記録する。
- current .NET 10 performance diagnostic loggingを有効にする。
- 通常利用と同じDB、library root、playlist、columns、window size、DPIを使う。
- background update、backup、antivirus等を特別に停止せず、実運用条件として記録する。

### Operation script

1. 起動し、`startup_ready_operable`とinitialization completeまで待つ。
2. playlist summaryを初回表示し、同じ画面へ一度戻る。
3. small／large playlist detailを一つずつ表示する。
4. full library、代表subset、play history等へ遷移する。
5. 導入先推定を代表packageで一度実行する。
6. 通常運用でstartup scan／manual file diffを使う場合は代表操作を一度実行する。
7. UI freeze、秒単位の無表示区間、操作の取りこぼし、異常なmemory増加を記録する。

### Acceptance reading

- logのinteraction IDでinputからfirst useful visibleまでstageが欠落なく追跡できる。
- modal dialogや明示的な長時間処理を除き、説明不能な秒単位UI停止がない。
- slow stageがある場合、queue、projection、binding／render、external I/Oのいずれかへ分類できる。
- functional regression、deadlock、data lossがない。

不満が残る場合はlogと操作を添えて新しいperformance follow-upを開始する。現行Codex batchを結果待ち状態に戻さない。

記録template:

```text
MANUAL-02
Date:
Final commit:
Artifact name / SHA-256:
Windows / CPU / memory:
Data summary (chart / playlist scale, no private content):
Diagnostic log path / SHA-256:
Startup: acceptable / follow-up
Playlist summary first / revisit: acceptable / follow-up
Playlist detail: acceptable / follow-up
Full library / subset: acceptable / follow-up
Install estimation: acceptable / follow-up / not run
Scan / diff: acceptable / follow-up / not run
UI freeze / deadlock: none / observed
Follow-up issue:
Notes:
```

## `RELEASE-01 BASS.NET provenance and entitlement`

公開配布前にユーザー／release ownerが、`Bass.Net.dll` 2.4.12.1のexact source／vendor archive、正式license、licensee scope、registration／redistribution entitlement、six native BASS librariesの配布条件、noticeとartifact inventoryの一致を確認する。

この確認はtechnical Engineering Gateではない。未確認の状態を配布許可と解釈しない。

## `RELEASE-02 Release operation`

署名、version／release notes更新、tag、push、公開uploadはユーザーの明示指示後に行う。Engineering完了commitから再現したartifactを使用し、hash、署名結果、公開先をrelease recordへ残す。
