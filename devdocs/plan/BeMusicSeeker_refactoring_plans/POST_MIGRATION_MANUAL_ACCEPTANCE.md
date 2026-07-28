# Post-migration Manual Acceptance

[現在地](./PLAN_STATUS.md) / [.NET 10移行計画](./BeMusicSeeker_NET10移行計画.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md)

## Scope

このchecklistはCodexのEngineering Gate完了後にユーザーが実施する。active implementation batch、planner停止条件、`EXTERNAL_BLOCKER`、Engineering Gateには含めない。

Engineering完了時点でrepository内のbuild、tests、publish、existing-data、update／rollback acceptanceは完了している前提とする。ここでは実配布環境と公開権限だけを確認する。

## `MANUAL-01 Runtime-free clean-machine acceptance`

実施条件:

- x64 Windows machine／VM
- .NET Desktop Runtime 10がmachine-wideに導入されていないことを確認
- Engineering Gateで生成した最終packageを使用
- packageのSHA-256とselected layoutを記録

最小確認:

1. packageを新しいdirectoryへ展開し、追加runtimeをinstallせず`BeMusicSeeker.exe`を起動する。
2. startup、library initialize／scan、playlist表示、search／filter、playback、settings save／restartを確認する。
3. package install／uninstall、maintenanceを利用する運用なら代表routeを確認する。
4. Everything、LR2、external playerが未導入の場合の既存fallback／messageを確認する。
5. updaterを手動確認する場合は、Engineering Gateと同じsigned／unsigned条件を記録し、success後に再起動できることだけを確認する。rollbackの故障注入は自動acceptanceを正本とする。
6. unexpected runtime installation prompt、silent settings reset、DB loss、partial updateがないことを確認する。

記録template:

```text
MANUAL-01
Date:
Windows edition / build:
Architecture: x64
Machine-wide .NET Desktop Runtime 10: absent
Artifact name:
Artifact SHA-256:
Main-app layout: single-file ADOPTED / folder SCD NOT_ADOPTED
Startup: pass / fail
Core workflow smoke: pass / fail
Settings restart: pass / fail
Optional integration/fallback: pass / fail / not tested
Notes:
```

## `RELEASE-01 BASS.NET provenance and entitlement`

公開配布前にユーザー／release ownerが次を確認する。

- `Bass.Net.dll` 2.4.12.1に対応するexact source／vendor archive
- 正式license textとredistribution条件
- licensee scope
- registration key／entitlementの適用範囲
- retained six native BASS librariesの配布条件
- repository内noticeと実際のartifact inventoryの一致

この確認はtechnical Engineering Gateではない。未確認の状態を配布許可と解釈しない。

## `RELEASE-02 Release operation`

署名、version／release notes更新、tag、push、公開uploadはユーザーの明示指示後に行う。Engineering完了commitから再現したartifactを使用し、hash、署名結果、公開先をrelease recordへ残す。
